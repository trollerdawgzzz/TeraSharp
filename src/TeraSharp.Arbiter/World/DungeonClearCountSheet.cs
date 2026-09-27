// SPDX-License-Identifier: MIT
// Copyright (c) 2026 the TeraSharp contributors

using System.Globalization;
using System.Xml.Linq;

namespace TeraSharp.Arbiter.World;

/// <summary>T184h: World's ViewDungeonClearCountList (1024060-1024231) selects
/// DungeonMatching rows by target level and completed DungeonData quests, then sorts
/// by descending minItemLevel. Live requests remain World-owned.</summary>
public static class DungeonClearCountSheet
{
    public sealed record Row(int Id, int MinLevel, int MaxLevel, float MinItemLevel, int[] CompletedQuests);
    public sealed record Data(Row[] Rows, int NewbieClearCount);

    // Historical classic_live2:13158 roster, retained only when sheets are absent.
    public static readonly Data BuiltIn = new(new[]
    {
        9068, 9056, 9168, 9156, 9507, 9043, 9768, 9756, 9868, 9856, 9830, 9810, 9739, 9075,
    }.Select(id => new Row(id, 0, int.MaxValue, 0, Array.Empty<int>())).ToArray(), 10);

    public static readonly SheetValue<Data> Entry = new(
        "DungeonMatching.xml + DungeonData_*.xml + DungeonNewbieBonus.xml",
        "standalone dungeon clear-count roster / newbie threshold", BuiltIn, Read, data => data.Rows.Length);

    public static Row[] ForLevel(int level, IReadOnlyCollection<int>? completedQuests = null)
        => Entry.Value.Rows.Where(r => level >= r.MinLevel && level <= r.MaxLevel
            && r.CompletedQuests.All(q => completedQuests?.Contains(q) == true)).ToArray();

    public static bool IsNewbie(int clears) => clears < Entry.Value.NewbieClearCount;

    public static Data? Read(string directory)
    {
        string matching = Path.Combine(directory, "DungeonMatching.xml");
        string newbie = Path.Combine(directory, "DungeonNewbieBonus.xml");
        if (!File.Exists(matching) || !File.Exists(newbie)) return null;
        var check = XDocument.Load(newbie).Root?.Element("DungeonNewbieBonusCheck");
        if (!int.TryParse((string?)check?.Attribute("clearCount"), out int threshold)) return null;
        var rows = new List<Row>();
        foreach (var dungeon in XDocument.Load(matching).Root?.Elements("Dungeon") ?? Enumerable.Empty<XElement>())
        {
            if (!int.TryParse((string?)dungeon.Attribute("id"), out int id)
                || !int.TryParse((string?)dungeon.Attribute("dungeonMinLevel"), out int min)
                || !int.TryParse((string?)dungeon.Attribute("dungeonMaxLevel"), out int max)
                || !float.TryParse((string?)dungeon.Attribute("minItemLevel"), NumberStyles.Float,
                    CultureInfo.InvariantCulture, out float ilvl)) continue;
            string contentPath = Path.Combine(directory, "DungeonData_" + id + ".xml");
            if (!File.Exists(contentPath)) continue;
            var content = XDocument.Load(contentPath).Root;
            if ((string?)content?.Attribute("continentId") != id.ToString(CultureInfo.InvariantCulture)) continue;
            // DungeonTemplate::ParseCondition, World2354540-2354548 -> vector at +0x1e8.
            var quests = content!.Elements("Condition")
                .Where(e => string.Equals((string?)e.Attribute("type"), "completeQuest", StringComparison.OrdinalIgnoreCase))
                .Select(e => int.Parse((string)e.Attribute("value")!, CultureInfo.InvariantCulture)).ToArray();
            rows.Add(new(id, min, max, ilvl, quests));
        }
        // Native starts with the id-ordered map; its <=32-element insertion sort retains
        // equal-ilvl order (World727046-727081). cap_2man_client2:824 contains 25 rows.
        return new(rows.OrderBy(r => r.Id).OrderByDescending(r => r.MinItemLevel).ToArray(), threshold);
    }
}
