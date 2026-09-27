// SPDX-License-Identifier: MIT
// Copyright (c) 2026 the TeraSharp contributors

using System.Runtime.CompilerServices;
using System.Globalization;
using Microsoft.Extensions.Logging;
using TeraSharp.Arbiter.Network;
using TeraSharp.Arbiter.Persistence;
using TeraSharp.Arbiter.World;

namespace TeraSharp.Arbiter.Handlers;

public static class QaDungeonEvents
{
    public static IReadOnlySet<string> Names { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "start_rookie_event", "add_dungeon_abnormality" };
    private sealed class Runtime
    {
        internal readonly object Gate = new(); internal readonly HashSet<int> Started = new();
        internal readonly HashSet<int> ReplayedRookies = new();
        internal readonly HashSet<int> Abnormalities = new();
        internal long? LastTick;
    }
    private static readonly ConditionalWeakTable<CharacterStore, Runtime> States = new();
    internal static Func<DateTimeOffset> Clock { get; set; } = () => DateTimeOffset.UtcNow;
    public static bool TryExecute(GameSession session, CharacterStore? store, GmCommandLine line, ILogger log)
    {
        if (!Names.Contains(line.Name)) return false;
        if (line.Name.Equals("add_dungeon_abnormality", StringComparison.OrdinalIgnoreCase))
        {
            if (store == null || !session.InWorld || line.Args.Count < 4
                || !int.TryParse(line.Args[0], out int id) || !int.TryParse(line.Args[1], out int abnormality)
                || !QaDungeonCommands.HasContinent(id)
                || !TryParseDate(line.Args[2], out long start) || !TryParseDate(line.Args[3], out long finish)) return true;
            // Arb008:14180/SQL4388: allocation is an independent persistent event identity.
            store.AddContinentAbnormalityEvent(id, abnormality, start, finish);
            TickAbnormalities(store, Program.World, Clock()); // native reserve insertion starts due events immediately.
            GmCommandHandlers.SendCustom(session, "던전 이상상태 추가 완료"); return true;
        }
        if (store == null || !session.InWorld || line.Args.Count != 4
            || !int.TryParse(line.Args[0], out int continent) || !int.TryParse(line.Args[1], out int hours)
            || !int.TryParse(line.Args[2], out int item) || !int.TryParse(line.Args[3], out int amount)) return true;
        // Arb037:13207-13322 validates DungeonTemplate, item existence and at most5 unstackable items.
        if (!QaDungeonCommands.HasContinent(continent)
            || !File.Exists(Path.Combine(HandshakeData.DatasheetDirectory(), $"DungeonData_{continent}.xml"))
            || !QaItemSheet.Entry.Value.Items.TryGetValue(item, out var template)
            || template.Int("maxStack") <= 1 && amount > 5) return true;
        var now = Clock(); DateTimeOffset end;
        try { end = now.AddHours(hours); } catch (ArgumentOutOfRangeException) { return true; }
        if (end < now) return true;
        store.AddDungeonRookieEvent(continent, now.ToUnixTimeSeconds(), end.ToUnixTimeSeconds(), item, amount);
        // Native enqueues the manager's1s timer; the bridge timer performs starts/expiry.
        return true;
    }

    public static void Tick(CharacterStore? store, WorldBridge? bridge, DateTimeOffset now)
    {
        if (store == null || bridge == null) return;
        var runtime = States.GetOrCreateValue(store);
        lock (runtime.Gate)
        {
            long milliseconds = now.ToUnixTimeMilliseconds();
            if (runtime.LastTick is long prior && milliseconds >= prior && milliseconds - prior < 1000) return;
            runtime.LastTick = milliseconds;
            long epoch = now.ToUnixTimeSeconds();
            foreach (var row in store.GetDungeonRookieEvents())
            {
                if (row.End <= epoch)
                {
                    bool started = runtime.Started.Remove(row.Id), replayed = runtime.ReplayedRookies.Remove(row.Id);
                    if (started || replayed) Broadcast(bridge, 0x14EB, QaDungeonCommands.IntBytes(row.Continent));
                    store.DeleteDungeonRookieEvent(row.Id);
                }
                else if (row.Start <= epoch && runtime.Started.Add(row.Id)) Broadcast(bridge, 0x14EA, BuildRookieStart(row));
            }
        }
        TickAbnormalities(store, bridge, now);
    }

    private static void TickAbnormalities(CharacterStore store, WorldBridge? bridge, DateTimeOffset now)
    {
        if (bridge == null) return;
        var runtime = States.GetOrCreateValue(store);
        lock (runtime.Gate)
        {
            long epoch = now.ToUnixTimeSeconds();
            foreach (var row in store.GetContinentAbnormalityEvents())
            {
                // ContentsOnOffManager first starts pending rows, then handles their expiry.
                if (row.Start <= epoch && runtime.Abnormalities.Add(row.Id))
                {
                    Broadcast(bridge, 0x1588, BuildContents(row.Id, false));
                    Broadcast(bridge, 0x161A, QaDungeonCommands.IntBytes(row.Continent, 1, row.Abnormality));
                }
                if (row.End <= epoch)
                {
                    if (runtime.Abnormalities.Remove(row.Id))
                    {
                        Broadcast(bridge, 0x1588, BuildContents(row.Id, true));
                        Broadcast(bridge, 0x161B, QaDungeonCommands.IntBytes(row.Continent, 1, row.Abnormality));
                    }
                    store.DeleteContinentAbnormalityEvent(row.Id);
                }
            }
        }
    }

    public static void ReplayAbnormalities(CharacterStore? store, WorldLink link, DateTimeOffset now)
    {
        if (store == null) return;
        long epoch = now.ToUnixTimeSeconds();
        foreach (var row in store.GetContinentAbnormalityEvents().Where(e => e.Start <= epoch && epoch < e.End))
        {
            link.SendFrame(0x1588, BuildContents(row.Id, false));
            link.SendFrame(0x161A, QaDungeonCommands.IntBytes(row.Continent, 1, row.Abnormality));
        }
    }
    internal static byte[] BuildContents(int id, bool disabled)
    { var p = new byte[9]; Write(p, 0, 14); Write(p, 4, id); p[8] = disabled ? (byte)1 : (byte)0; return p; }
    internal static bool TryParseDate(string text, out long epoch)
    {
        // Arb004:887 ParseYYYYMMDDHHMM: calendar components, not Unix timestamps.
        bool valid = DateTimeOffset.TryParseExact(text, "yyyyMMddHHmm", CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var date);
        epoch = valid ? date.ToUnixTimeSeconds() : 0; return valid;
    }

    public static byte[] BuildRookieSnapshot(CharacterStore? store, DateTimeOffset now, bool rememberReplay = false)
    {
        long epoch = now.ToUnixTimeSeconds();
        var rows = store?.GetDungeonRookieEvents().Where(e => e.Start <= epoch && epoch < e.End).ToArray()
            ?? Array.Empty<CharacterStore.DungeonRookieEvent>();
        if (rememberReplay && store != null)
        {
            var runtime = States.GetOrCreateValue(store);
            lock (runtime.Gate) foreach (var row in rows) runtime.ReplayedRookies.Add(row.Id);
        }
        // Arb038:13486-13610: nested32-bit list references,20B continent then20B reward.
        var p = new byte[8 + rows.Length * 40]; Write(p, 0, rows.Length); Write(p, 4, rows.Length == 0 ? 0 : 14);
        for (int i = 0; i < rows.Length; i++)
        {
            int at = 8 + i * 40, self = at + 6;
            Write(p, at, self); Write(p, at + 4, i + 1 == rows.Length ? 0 : self + 40);
            Write(p, at + 8, 1); Write(p, at + 12, self + 20); Write(p, at + 16, rows[i].Continent);
            Write(p, at + 20, self + 20); Write(p, at + 28, 1); Write(p, at + 32, rows[i].Item); Write(p, at + 36, rows[i].Amount);
        }
        return p;
    }
    internal static byte[] BuildRookieStart(CharacterStore.DungeonRookieEvent row)
    {
        // Arb038:18426-18491: [count,first,continent] + [self,next,target1,item,amount].
        return QaDungeonCommands.IntBytes(1, 18, row.Continent, 18, 0, 1, row.Item, row.Amount);
    }
    private static void Write(byte[] p, int at, int value) => BitConverter.GetBytes(value).CopyTo(p, at);
    private static void Broadcast(WorldBridge bridge, ushort op, byte[] payload)
    { for (int id = 0; id < WorldRegistration.MaxWorldId; id++) if (bridge.HasLinks(id)) bridge.SendFrame(id, op, payload); }
    internal static void ResetForTests(CharacterStore store) { States.Remove(store); Clock = () => DateTimeOffset.UtcNow; }
}
