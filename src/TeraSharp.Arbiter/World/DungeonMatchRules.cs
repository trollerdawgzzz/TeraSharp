// SPDX-License-Identifier: MIT
// Copyright (c) 2026 the TeraSharp contributors

using System.Xml.Linq;

namespace TeraSharp.Arbiter.World;

/// <summary>T184: MatchServer FUN_14010d710 / FUN_14010b870. Admission size and completed
/// size are different fields. Counts below use the production RoleData, not its QA overrides.</summary>
public sealed record DungeonMatchRule(int Total, int TankMin, int TankMax, int DealerMin,
    int DealerMax, int HealerMin, int HealerMax, int MinMatchingMember, int MaxMatchingMember)
{
    // WorldServer.exe.c:3035963-3035982 checks the incoming party, not the completed pool.
    public bool AcceptsApplicant(int count) => count >= MinMatchingMember && count <= MaxMatchingMember;

    public IEnumerable<RoleTemplate> Templates()
    {
        for (int t = TankMin; t <= Math.Min(TankMax, Total); t++)
            for (int h = HealerMin; h <= Math.Min(HealerMax, Total - t); h++)
            {
                int d = Total - t - h;
                if (d >= DealerMin && d <= DealerMax) yield return new RoleTemplate(t, h, d);
            }
    }
}

public static class DungeonMatchRules
{
    public static readonly SheetValue<IReadOnlyDictionary<int, DungeonMatchRule>> Entry = new(
        "DungeonMatching.xml + MatchingRoleTemplate.xml", "dungeon matching sizes and role bounds",
        new Dictionary<int, DungeonMatchRule>(), Read, rows => rows.Count);

    public static DungeonMatchRule? For(int instanceId)
        => Entry.Value.TryGetValue(instanceId, out var r) ? r : null;

    public static IReadOnlyDictionary<int, DungeonMatchRule>? Read(string directory)
    {
        string roles = Path.Combine(directory, "MatchingRoleTemplate.xml");
        string dungeons = Path.Combine(directory, "DungeonMatching.xml");
        if (!File.Exists(roles) || !File.Exists(dungeons)) return null;
        return Parse(XDocument.Load(roles), XDocument.Load(dungeons));
    }

    public static IReadOnlyDictionary<int, DungeonMatchRule> Parse(XDocument roles,
        XDocument dungeons, bool qa = false)
    {
        static int N(XElement e, string key, int fallback = 0)
            => int.TryParse((string?)e.Attribute(key), out int n) ? n : fallback;
        var byRole = new Dictionary<int, DungeonMatchRule>();
        foreach (var role in roles.Descendants("Role"))
        {
            var row = role.Element("RoleData");
            if (row == null) continue;
            int total = N(row, "totalUser");
            if (qa && N(row, "totalUserQa", -1) != -1) total = N(row, "totalUserQa");
            int hmin = N(row, "healerMin"), hmax = N(row, "healerMax", total);
            if (qa && N(row, "healerMinQa", -1) != -1) hmin = N(row, "healerMinQa");
            if (qa && N(row, "healerMaxQa", -1) != -1) hmax = N(row, "healerMaxQa");
            if (total <= 0 || total > PartyPackets.MaxRaidMembers) continue;
            byRole[N(role, "id")] = new(total, N(row, "tankerMin"), N(row, "tankerMax", total - hmin),
                N(row, "dealerMin"), N(row, "dealerMax", total - hmin), hmin, hmax,
                N(row, "minMatchingMember", 1), N(row, "maxMatchingMember", total));
        }
        var result = new Dictionary<int, DungeonMatchRule>();
        foreach (var dungeon in dungeons.Descendants("Dungeon"))
            if (byRole.TryGetValue(N(dungeon, "matchingRoleId", 1), out var rule))
                result[N(dungeon, "id")] = rule;
        return result;
    }
}
