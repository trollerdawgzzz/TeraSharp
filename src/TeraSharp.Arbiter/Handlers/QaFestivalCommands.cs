// SPDX-License-Identifier: MIT
// Copyright (c) 2026 the TeraSharp contributors

using System.Runtime.CompilerServices;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using TeraSharp.Arbiter.Network;
using TeraSharp.Arbiter.Persistence;
using TeraSharp.Arbiter.World;

namespace TeraSharp.Arbiter.Handlers;

public static class QaFestivalCommands
{
    public static IReadOnlySet<string> Names { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "start_festival", "stop_festival", "reserve_festival", "dailyevent" };
    public sealed record Definition(int Id, bool AllWorlds, bool DailyEvent);
    public sealed record Data(IReadOnlyDictionary<int, Definition> Events, bool DailyEnabled, int ResetHour);
    public static readonly SheetValue<Data> Sheet = new("WorldFestival*.xml + DailyEvent.xml", "QA festival scope and daily reset",
        new(new Dictionary<int, Definition>(), false, 0), directory =>
        {
            var files = Directory.Exists(directory) ? Directory.GetFiles(directory, "WorldFestival*.xml").OrderBy(f => f, StringComparer.Ordinal).ToArray() : Array.Empty<string>();
            if (files.Length == 0) return null;
            var events = new Dictionary<int, Definition>();
            foreach (string file in files) foreach (var row in XDocument.Load(file).Descendants("EventObject"))
                if ((int?)row.Attribute("id") is int id) events.TryAdd(id, new(id,
                    string.Equals((string?)row.Attribute("broadcastToWorldServer"), "true", StringComparison.OrdinalIgnoreCase), row.Element("DailyEvent") != null));
            string dailyPath = Path.Combine(directory, "DailyEvent.xml"); var daily = File.Exists(dailyPath) ? XDocument.Load(dailyPath).Root : null;
            return new(events, string.Equals((string?)daily?.Attribute("enable"), "true", StringComparison.OrdinalIgnoreCase), (int?)daily?.Element("Time")?.Attribute("resetHour") ?? 0);
        }, x => x.Events.Count);
    private sealed class Runtime
    {
        internal readonly object Gate = new(); internal readonly HashSet<int> Started = new();
        internal long? FestivalTick, DailyTick; internal int? ForcedDay; internal int CurrentDay;
        internal bool? DailyEnabled;
    }
    private static readonly ConditionalWeakTable<CharacterStore, Runtime> States = new();
    internal static Func<DateTimeOffset> Clock { get; set; } = () => DateTimeOffset.UtcNow;
    public static bool TryExecute(GameSession session, CharacterStore? store, GmCommandLine line, ILogger log)
    {
        if (!Names.Contains(line.Name)) return false;
        if (store == null || !session.InWorld || line.Args.Count == 0 || !int.TryParse(line.Args[0], out int id)) return true;
        var now = Clock(); var runtime = States.GetOrCreateValue(store);
        lock (runtime.Gate)
        {
            if (line.Name.Equals("dailyevent", StringComparison.OrdinalIgnoreCase))
            {
                if (line.Args.Count == 1 && id is >= 0 and < 8) { runtime.ForcedDay = id; runtime.DailyEnabled = true; }
                return true;
            }
            if (!Sheet.Value.Events.TryGetValue(id, out var definition)) return true;
            if (line.Name.Equals("reserve_festival", StringComparison.OrdinalIgnoreCase))
            {
                if (line.Args.Count >= 3 && QaDungeonEvents.TryParseDate(line.Args[1], out long start) && QaDungeonEvents.TryParseDate(line.Args[2], out long end))
                    store.AddFestivalEvent(id, start, end, true);
                return true;
            }
            if (line.Name.Equals("start_festival", StringComparison.OrdinalIgnoreCase))
            {
                var row = store.AddFestivalEvent(id, now.ToUnixTimeSeconds(), 0, false);
                if (row != null) Start(row, definition, runtime, Program.World);
            }
            else foreach (var row in store.GetFestivalEvents().Where(r => r.Event == id && r.Start <= now.ToUnixTimeSeconds() && (r.End == 0 || now.ToUnixTimeSeconds() < r.End)))
                Stop(store, row, definition, runtime, Program.World);
        }
        return true;
    }
    public static void Tick(CharacterStore? store, WorldBridge? bridge, DateTimeOffset now)
    {
        if (store == null || bridge == null) return; var runtime = States.GetOrCreateValue(store);
        lock (runtime.Gate)
        {
            long seconds = now.ToUnixTimeSeconds();
            if (runtime.FestivalTick is not long last || seconds < last || seconds - last >= 10)
            {
                runtime.FestivalTick = seconds;
                foreach (var row in store.GetFestivalEvents())
                {
                    if (!Sheet.Value.Events.TryGetValue(row.Event, out var def)) continue;
                    if (row.Start <= seconds && !runtime.Started.Contains(row.Id)) Start(row, def, runtime, bridge);
                    if (row.End != 0 && row.End < seconds && runtime.Started.Contains(row.Id)) Stop(store, row, def, runtime, bridge);
                }
            }
            if (runtime.DailyTick is not long dayLast || seconds < dayLast || seconds - dayLast >= 1)
            {
                runtime.DailyTick = seconds; int day = Day(runtime, now);
                if (day != runtime.CurrentDay) { runtime.CurrentDay = day; Send(bridge, true, 0x156B, QaDungeonCommands.IntBytes(day)); }
            }
        }
    }
    private static int Day(Runtime runtime, DateTimeOffset now)
    {
        if (!(runtime.DailyEnabled ?? Sheet.Value.DailyEnabled)) return 0;
        if (runtime.ForcedDay is int day) return day;
        var local = now.ToLocalTime(); if (local.Hour < Sheet.Value.ResetHour) local = local.AddDays(-1);
        return (int)local.DayOfWeek + 1;
    }
    public static byte[] DailySnapshot(CharacterStore? store, DateTimeOffset now)
    {
        if (store == null) return QaDungeonCommands.IntBytes(0);
        var runtime = States.GetOrCreateValue(store); lock (runtime.Gate)
        {
            if (runtime.DailyEnabled == null && store.GetFestivalEvents().Any(r => r.Start <= now.ToUnixTimeSeconds() && (r.End == 0 || now.ToUnixTimeSeconds() < r.End) && Sheet.Value.Events.TryGetValue(r.Event, out var d) && d.DailyEvent)) runtime.DailyEnabled = true;
            return QaDungeonCommands.IntBytes(Day(runtime, now));
        }
    }
    public static void Replay(CharacterStore? store, WorldLink link, DateTimeOffset now)
    {
        if (store == null) return;
        foreach (var row in store.GetFestivalEvents().Where(r => r.Start <= now.ToUnixTimeSeconds() && (r.End == 0 || now.ToUnixTimeSeconds() < r.End)))
            if (Sheet.Value.Events.TryGetValue(row.Event, out var d) && (d.AllWorlds || link.WorldId == 0)) link.SendFrame(0x149A, StartPayload(row));
    }
    private static void Start(CharacterStore.FestivalEvent row, Definition def, Runtime runtime, WorldBridge? bridge)
    { runtime.Started.Add(row.Id); if (def.DailyEvent) runtime.DailyEnabled = true; Send(bridge, def.AllWorlds, 0x149A, StartPayload(row)); }
    private static void Stop(CharacterStore store, CharacterStore.FestivalEvent row, Definition def, Runtime runtime, WorldBridge? bridge)
    { runtime.Started.Remove(row.Id); store.DeleteFestivalEvent(row.Id); if (def.DailyEvent) runtime.DailyEnabled = false; Send(bridge, def.AllWorlds, 0x149B, QaDungeonCommands.IntBytes(row.Event)); }
    internal static byte[] StartPayload(CharacterStore.FestivalEvent row)
    { var p = new byte[12]; BitConverter.GetBytes(row.Event).CopyTo(p, 0); BitConverter.GetBytes(row.Start).CopyTo(p, 4); return p; }
    private static void Send(WorldBridge? bridge, bool all, ushort op, byte[] payload)
    { if (bridge != null) for (int id = 0; id < (all ? WorldRegistration.MaxWorldId : 1); id++) if (bridge.HasLinks(id)) bridge.SendFrame(id, op, payload); }
    internal static void ResetForTests(CharacterStore store) { States.Remove(store); Clock = () => DateTimeOffset.UtcNow; }
}
