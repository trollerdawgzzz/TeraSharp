// SPDX-License-Identifier: MIT
// Copyright (c) 2026 the TeraSharp contributors

using System.Xml;
using System.Xml.Linq;
using TeraSharp.Arbiter.Network;
using TeraSharp.Arbiter.Persistence;
using TeraSharp.Arbiter.World;

namespace TeraSharp.Arbiter.Handlers;

/// <summary>Arb029:15616-16155's info_dungeon diagnostics from persisted cooldown records.
/// Runtime hunting-event/QA reduction percentages are not represented by the current store;
/// durations here are the loaded sheet baseline, including a set_dungeoncool override.</summary>
internal static class QaDungeonInfo
{
    internal sealed record Summary(string Name, int Seconds, int Remaining);

    internal static void Send(GameSession session, CharacterStore store)
    {
        string directory = HandshakeData.DatasheetDirectory();
        string constraintsPath = Path.Combine(directory, "DungeonConstraint.xml");
        if (!File.Exists(constraintsPath)) return;
        try
        {
            var constraints = XDocument.Load(constraintsPath);
            foreach (byte[] row in store.GetDungeonCoolTimes((int)session.PlayerId))
            {
                if (row.Length != 52) continue;
                int id = BitConverter.ToInt32(row);
                var constraint = constraints.Descendants("Constraint").FirstOrDefault(e => (int?)e.Attribute("continentId") == id);
                string path = Path.Combine(directory, $"DungeonData_{id}.xml");
                if (constraint == null || !File.Exists(path)) continue;
                string? name = (string?)XDocument.Load(path).Root?.Attribute("name");
                if (name == null) continue;
                var summary = Summarize(row, constraint, name, DateTimeOffset.UtcNow, QaDungeonCommands.CoolMinutesOverride(id));
                session.Send(DbProxyHandlers.BuildSystemMessage(Message(summary)));
            }
            // BattleFieldCoolTimeManager's used-entry list is currently empty (155D/155E).
            // Native likewise emits no @3569 without a nonzero stored entry count.
        }
        catch (Exception e) when (e is IOException or XmlException or UnauthorizedAccessException or FormatException) { }
    }

    internal static Summary Summarize(byte[] row, XElement constraint, string name, DateTimeOffset now, int? cooldownOverride = null)
    {
        int Int(string attr) => int.TryParse((string?)constraint.Attribute(attr), out int value) ? value : 0;
        int limit = string.Equals((string?)constraint.Attribute("phaseSave"), "true", StringComparison.OrdinalIgnoreCase) ? 0 : Int("enterLimitCount");
        int remaining = limit > 0 ? Math.Max(0, limit - BitConverter.ToInt32(row, 40)) : -1;
        int minutes = cooldownOverride ?? Int("coolTime");
        if (BitConverter.ToInt16(row, 48) == 1 && Int("coolTimeForPartyMatching") > 0) minutes = Int("coolTimeForPartyMatching");
        int seconds = 0;
        try
        {
            // DateTime+0..10 = year/month/day/hour/minute/second. FUN034A50 adds minutes*60.
            var entered = new DateTimeOffset(BitConverter.ToUInt16(row, 8), BitConverter.ToUInt16(row, 10),
                BitConverter.ToUInt16(row, 12), BitConverter.ToUInt16(row, 14), BitConverter.ToUInt16(row, 16),
                BitConverter.ToUInt16(row, 18), TimeSpan.Zero);
            seconds = (int)Math.Clamp((entered.AddMinutes(minutes) - now).TotalSeconds, 0, int.MaxValue);
        }
        catch (ArgumentOutOfRangeException) { }
        return new(name, seconds, remaining);
    }

    internal static string Message(Summary row) => row.Remaining >= 0
        ? $"@3568\vdungeonName\v{row.Name}\vcount\v{row.Remaining}"
        : row.Seconds > 0 ? $"@3566\vdungeonName\v{row.Name}\vsecond\v{row.Seconds}"
        : $"@3567\vdungeonName\v{row.Name}";
}
