// SPDX-License-Identifier: MIT
// Copyright (c) 2026 the TeraSharp contributors

using System.Xml;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using TeraSharp.Arbiter.Network;
using TeraSharp.Arbiter.Persistence;
using TeraSharp.Arbiter.World;

namespace TeraSharp.Arbiter.Handlers;

/// <summary>Native QA WeeklyTimelinePatch additions, RAM-only; World evaluates transmitted intervals.</summary>
public static class QaTimelineCommands
{
    public static IReadOnlySet<string> Names { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    { "add_timeline_dungeon", "reset_timeline_dungeon", "show_timeline_dungeon",
      "add_timeline_eventmatching", "reset_timeline_eventmatching", "show_timeline_eventmatching" };
    internal sealed record Interval(int Day, int Start, int End);
    private static readonly object Gate = new();
    private static readonly Dictionary<(bool Event, int Id), List<Interval>> Changes = new();

    public static bool TryExecute(GameSession session, CharacterStore? store, GmCommandLine line, ILogger log)
    {
        if (!Names.Contains(line.Name)) return false;
        if (!session.InWorld || session.SelectedCharacter == null) return true;
        bool events = line.Name.EndsWith("eventmatching", StringComparison.OrdinalIgnoreCase);
        if (line.Name.StartsWith("reset_", StringComparison.OrdinalIgnoreCase))
        {
            lock (Gate) foreach (var key in Changes.Keys.Where(k => k.Event == events).ToArray()) Changes.Remove(key);
            // Arb065:7359/Arb050:7261: init contains permanent changes only. QA overrides are removed.
            Broadcast(0x1582, QaDungeonCommands.IntBytes(0, 0, events ? 1 : 0));
            GmCommandHandlers.SendCustom(session, "시간표 초기화 완료."); return true;
        }
        int id = line.Args.Count > 0 && int.TryParse(line.Args[0], out int parsed) ? parsed : 0;
        if (line.Name.StartsWith("show_", StringComparison.OrdinalIgnoreCase))
        {
            foreach (string message in Show(events, id)) GmCommandHandlers.SendCustom(session, message);
            return true;
        }
        if (line.Args.Count < 4 || !int.TryParse(line.Args[1], out int day)
            || !int.TryParse(line.Args[2], out int start) || !int.TryParse(line.Args[3], out int end)) return true;
        var sheet = ReadSheet(events, id);
        if (events && sheet == null) { GmCommandHandlers.SendCustom(session, "유효하지 않은 템플릿 ID입니다."); return true; }
        if (events && string.Equals((string?)sheet!.Attribute("type"), "BattleField", StringComparison.OrdinalIgnoreCase))
        { GmCommandHandlers.SendCustom(session, "type=BattleField인 지령서는 수정할 수 없습니다."); return true; }
        if (day is < 0 or > 7) { GmCommandHandlers.SendCustom(session, "유효하지 않은 요일값입니다."); return true; }
        day = day == 0 ? 8 : day == 7 ? 0 : day;
        var interval = new Interval(day, HhmmSeconds(start), HhmmSeconds(end));
        // Native AddEntry rejects a nonpositive duration, but the command still sends its
        // current patch and prints success. A missing dungeon also gets an empty1583.
        lock (Gate)
        {
            if (sheet != null && interval.Start >= 0 && interval.End > interval.Start)
            {
                if (!Changes.TryGetValue((events, id), out var rows)) Changes[(events, id)] = rows = new();
                rows.Add(interval);
            }
        }
        Broadcast(0x1583, BuildPatch(events, id, Snapshot(events, id)));
        GmCommandHandlers.SendCustom(session, "구간 추가 완료."); return true;
    }

    internal static int HhmmSeconds(int value) => unchecked((value / 100 * 60 + value % 100) * 60);
    internal static IReadOnlyList<Interval> Snapshot(bool events, int id)
    { lock (Gate) return Changes.TryGetValue((events, id), out var rows) ? rows.ToArray() : Array.Empty<Interval>(); }
    internal static void ResetForTests() { lock (Gate) Changes.Clear(); }

    internal static byte[] BuildPatch(bool events, int id, IReadOnlyList<Interval> rows)
    {
        // Arb061:2707-2856 / Arb049:10060: linked30B entries, full frame offset22.
        byte[] p = new byte[16 + 30 * rows.Count];
        BitConverter.GetBytes(rows.Count).CopyTo(p, 0); BitConverter.GetBytes(rows.Count > 0 ? 22 : 0).CopyTo(p, 4);
        BitConverter.GetBytes(events ? 1 : 0).CopyTo(p, 8); BitConverter.GetBytes(id).CopyTo(p, 12);
        for (int i = 0; i < rows.Count; i++)
        {
            int at = 16 + 30 * i; var row = rows[i];
            BitConverter.GetBytes(at + 6).CopyTo(p, at); BitConverter.GetBytes(i + 1 < rows.Count ? at + 36 : 0).CopyTo(p, at + 4);
            // Entry UID0, isQA1, enabled1, day enum, start/end seconds (Arb040:5523/5607).
            p[at + 16] = 1; p[at + 17] = 1; BitConverter.GetBytes(row.Day).CopyTo(p, at + 18);
            BitConverter.GetBytes(row.Start).CopyTo(p, at + 22); BitConverter.GetBytes(row.End).CopyTo(p, at + 26);
        }
        return p;
    }

    internal static IEnumerable<string> Show(bool events, int id)
    {
        var sheet = ReadSheet(events, id);
        if (sheet == null)
        { yield return events ? $"지령서 {id}는 존재하지 않습니다." : $"던전 {id}는 존재하지 않는 던전입니다."; yield break; }
        // The native summary is a normalized weekly union, while1583 carries raw changes.
        const int week = 7 * 24 * 60; var open = new bool[week];
        var enable = sheet.Element("EnableTimeList");
        if (enable == null) Array.Fill(open, true);
        else
        {
            foreach (var e in enable.Elements("EnableTime"))
            {
                int day = ((string?)e.Attribute("day") ?? "").ToLowerInvariant() switch
                { "sunday" => 0, "monday" => 1, "tuesday" => 2, "wednesday" => 3, "thursday" => 4, "friday" => 5, "saturday" => 6, "all" => 8, _ => -1 };
                if (day < 0) continue;
                int Int(string key) => int.TryParse((string?)e.Attribute(key), out int n) ? n : 0;
                Add(new(day, (Int("openHour") * 60 + Int("openMinute")) * 60, (Int("closeHour") * 60 + Int("closeMinute")) * 60));
            }
        }
        foreach (var row in Snapshot(events, id)) Add(row);
        if (open.All(b => b)) { yield return events ? $"지령서 {id}: 언제나 가능." : $"던전 {id}: 언제나 열려 있음."; yield break; }
        if (!open.Any(b => b)) { yield return events ? $"지령서 {id}: 언제나 불가능." : $"던전 {id}: 언제나 닫혀 있음."; yield break; }
        yield return events ? $"지령서 {id} 가능 시간:" : $"던전 {id} 오픈 시간:";
        int gap = Array.FindIndex(open, b => !b);
        for (int n = 1; n <= week; n++)
        {
            int at = (gap + n) % week; if (!open[at]) continue;
            int start = at; while (n <= week && open[(gap + n) % week]) n++;
            int end = (gap + n) % week;
            yield return $"{Time(start)} - {Time(end)}";
        }
        string Time(int minute) => $"{"일월화수목금토"[minute / 1440]}요일 {minute / 60 % 24:00}:{minute % 60:00}";
        void Add(Interval row)
        {
            if (row.End <= row.Start) return;
            foreach (int day in row.Day == 8 ? Enumerable.Range(0, 7) : new[] { row.Day })
                for (int i = row.Start / 60; i < row.End / 60 && i - row.Start / 60 < week; i++)
                    open[((day * 1440 + i) % week + week) % week] = true;
        }
    }

    private static XElement? ReadSheet(bool events, int id)
    {
        try
        {
            string dir = HandshakeData.DatasheetDirectory();
            string path = Path.Combine(dir, events ? "EventMatching.xml" : "DungeonConstraint.xml");
            if (File.Exists(path))
            {
                var row = XDocument.Load(path).Descendants(events ? "Event" : "Constraint")
                    .FirstOrDefault(e => (int?)e.Attribute(events ? "id" : "continentId") == id
                        && (!events || e.Attribute("active") != null));
                if (row != null) return row;
            }
            return !events && QaDungeonCommands.HasContinent(id) ? new XElement("Constraint") : null;
        }
        catch (Exception e) when (e is IOException or XmlException or UnauthorizedAccessException or FormatException) { return null; }
    }
    private static void Broadcast(ushort opcode, byte[] payload)
    {
        var bridge = Program.World; if (bridge == null) return;
        for (int id = 0; id < WorldRegistration.MaxWorldId; id++) if (bridge.HasLinks(id)) bridge.SendFrame(id, opcode, payload);
    }
}
