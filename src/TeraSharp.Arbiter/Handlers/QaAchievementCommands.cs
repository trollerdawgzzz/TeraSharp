// SPDX-License-Identifier: MIT
// Copyright (c) 2026 the TeraSharp contributors

using System.Runtime.CompilerServices;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using TeraSharp.Arbiter.Network;
using TeraSharp.Arbiter.Persistence;
using TeraSharp.Arbiter.World;

namespace TeraSharp.Arbiter.Handlers;

public static class QaAchievementCommands
{
    public static IReadOnlySet<string> Names { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "change_achievement_season" };
    public sealed record SeasonData(IReadOnlySet<int> Ids);
    public static readonly SheetValue<SeasonData> Sheet = new("AchievementGradeInfo.xml (seasons)", "QA achievement season bounds",
        new(new HashSet<int>()), directory =>
        {
            string path = Path.Combine(directory, "AchievementGradeInfo.xml");
            return File.Exists(path) ? new SeasonData(XDocument.Load(path).Root!.Elements("Season").Select(e => (int?)e.Attribute("id") ?? -1).Where(i => i >= 0).ToHashSet()) : null;
        }, x => x.Ids.Count);
    private sealed class Runtime
    {
        internal readonly object Gate = new(); internal readonly Dictionary<int, long> Overrides = new();
        internal readonly List<(uint User, DateTimeOffset Due)> Refresh = new();
    }
    private static readonly ConditionalWeakTable<CharacterStore, Runtime> States = new();
    internal static Func<DateTimeOffset> Clock { get; set; } = () => DateTimeOffset.UtcNow;
    public static bool TryExecute(GameSession session, CharacterStore? store, GmCommandLine line, ILogger log)
    {
        if (!Names.Contains(line.Name)) return false;
        if (store == null || !session.InWorld || line.Args.Count is < 1 or > 2 || !int.TryParse(line.Args[0], out int season)) return true;
        var now = Clock(); var rows = Snapshot(store); int current = Current(rows, now); int max = Sheet.Value.Ids.DefaultIfEmpty(-1).Max();
        if (season > max) { GmCommandHandlers.SendCustom(session, $"현재 업적 시즌 {max} 까지 있습니다. 입력한 시즌ID: {season}"); return true; }
        if (season <= current)
        {
            GmCommandHandlers.SendCustom(session, season < current ? $"더 최근 업적 시즌이 진행중입니다. 현재 시즌ID: {current}" : $"현재 {current} 시즌이 진행중입니다.");
            Broadcast(Program.World, 0x150F, QaDungeonCommands.IntBytes((int)session.PlayerId)); return true;
        }
        long epoch = now.ToUnixTimeSeconds(); var runtime = States.GetOrCreateValue(store);
        lock (runtime.Gate)
        {
            runtime.Overrides[season] = epoch;
            if (line.Args.Count > 1 && line.Args[1].Equals("true", StringComparison.OrdinalIgnoreCase)) store.SetAchievementSeason(season, epoch);
            runtime.Refresh.Add((session.PlayerId, now.AddSeconds(3)));
        }
        rows[season] = epoch;
        // Native updates the manager then sends current season before its schedule list.
        Broadcast(Program.World, 0x1510, QaDungeonCommands.IntBytes(Current(rows, now.AddTicks(1))));
        Broadcast(Program.World, 0x1511, BuildList(rows));
        GmCommandHandlers.SendCustom(session, $"{season} 업적 시즌을 시작합니다."); return true;
    }
    internal static Dictionary<int, long> Snapshot(CharacterStore store)
    {
        var rows = store.GetAchievementSeasons(); var runtime = States.GetOrCreateValue(store);
        lock (runtime.Gate) foreach (var row in runtime.Overrides) rows[row.Key] = row.Value; return rows;
    }
    internal static int Current(IReadOnlyDictionary<int, long> rows, DateTimeOffset now)
        => rows.Where(r => r.Value <= now.ToUnixTimeSeconds() && Sheet.Value.Ids.Contains(r.Key)).Select(r => r.Key).DefaultIfEmpty(-1).Max();
    public static byte[]? BootFrame(CharacterStore? store, ushort op, DateTimeOffset now)
    {
        if (store == null || op is not (0x1510 or 0x1511)) return null;
        var rows = Snapshot(store); if (rows.Count == 0) return null;
        return op == 0x1510 ? QaDungeonCommands.IntBytes(Current(rows, now)) : BuildList(rows);
    }
    internal static byte[] BuildList(IReadOnlyDictionary<int, long> rows)
    {
        var p = new byte[8 + rows.Count * 20]; BitConverter.GetBytes(rows.Count).CopyTo(p, 0);
        BitConverter.GetBytes(rows.Count == 0 ? 0 : 14).CopyTo(p, 4); int at = 8;
        foreach (var row in rows.OrderBy(r => r.Key))
        {
            BitConverter.GetBytes(at + 6).CopyTo(p, at); BitConverter.GetBytes(at + 20 < p.Length ? at + 26 : 0).CopyTo(p, at + 4);
            BitConverter.GetBytes(row.Key).CopyTo(p, at + 8); BitConverter.GetBytes(row.Value).CopyTo(p, at + 12); at += 20;
        }
        return p;
    }
    public static void Tick(CharacterStore? store, WorldBridge? bridge, DateTimeOffset now)
    {
        if (store == null || bridge == null || !States.TryGetValue(store, out var runtime)) return;
        lock (runtime.Gate)
        {
            foreach (var row in runtime.Refresh.Where(r => r.Due <= now).ToArray())
            { runtime.Refresh.Remove(row); Broadcast(bridge, 0x150F, QaDungeonCommands.IntBytes((int)row.User)); }
        }
    }
    private static void Broadcast(WorldBridge? bridge, ushort op, byte[] payload)
    { if (bridge != null) for (int id = 0; id < WorldRegistration.MaxWorldId; id++) if (bridge.HasLinks(id)) bridge.SendFrame(id, op, payload); }
    internal static void ResetForTests(CharacterStore store) { States.Remove(store); Clock = () => DateTimeOffset.UtcNow; }
}
