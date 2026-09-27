// SPDX-License-Identifier: MIT
// Copyright (c) 2026 the TeraSharp contributors

using System.Xml.Linq;

namespace TeraSharp.Arbiter.World;

public static class QaGuildSheet
{
    public sealed record ServerMode(bool Pve);
    public static readonly SheetValue<ServerMode> Mode = new("../DeploymentConfig.xml (ArbiterServerConfig)",
        "native guild QA PvE guard", new(false), directory =>
        {
            string path = Path.GetFullPath(Path.Combine(directory, "..", "DeploymentConfig.xml"));
            if (!File.Exists(path)) return null;
            var config = XDocument.Load(path).Root?.Element("ArbiterServerConfig")
                ?? throw new FormatException("Missing ArbiterServerConfig");
            return new((bool?)config.Attribute("pveServer") ?? false); // Arb077:2403 native default0.
        }, _ => 1);
    public static readonly SheetValue<IReadOnlyDictionary<int, bool>> Emblems = new("GuildEmblem.xml (QA)",
        "guild emblem permanence", new Dictionary<int, bool>(), directory =>
        {
            string path = Path.Combine(directory, "GuildEmblem.xml");
            if (!File.Exists(path)) return null;
            var result = new Dictionary<int, bool>();
            foreach (var row in XDocument.Load(path).Descendants("Emblem"))
            {
                int id = (int?)row.Attribute("id") ?? throw new FormatException("Emblem without id");
                bool forever = (bool?)row.Attribute("forever") ?? throw new FormatException("Emblem without forever");
                if (!result.TryAdd(id, forever)) throw new FormatException("Duplicate emblem id");
            }
            return result;
        }, x => x.Count);
    public sealed record Data(int ContributionLimit, int CastleCoinLimit);
    public static readonly SheetValue<Data> Entry = new("GuildQuestConfig.xml + FloatingCastle.xml (QA)",
        "guild contribution and general-coin caps", new(0, 0), directory =>
        {
            string quests = Path.Combine(directory, "GuildQuest", "GuildQuestConfig.xml"), castle = Path.Combine(directory, "FloatingCastle.xml");
            if (!File.Exists(quests) || !File.Exists(castle)) return null;
            int contribution = (int?)XDocument.Load(quests).Root?.Element("ContributionPointconfig")?.Attribute("weeklyLimit") ?? 0;
            int coin = (int?)XDocument.Load(castle).Root?.Element("CommonConfig")?.Attribute("maxFloatingCastlePartsCoin") ?? 0;
            if (contribution < 0 || coin < 0) throw new FormatException("Invalid guild QA cap");
            return new(contribution, coin);
        }, _ => 2);
}
