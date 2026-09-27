// SPDX-License-Identifier: MIT
// Copyright (c) 2026 the TeraSharp contributors

using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using TeraSharp.Arbiter.Network;
using TeraSharp.Arbiter.Persistence;
using TeraSharp.Arbiter.World;

namespace TeraSharp.Arbiter.Handlers;

public static class QaGuildRankingCommands
{
    public static IReadOnlySet<string> Names { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "calc_guild_level_ranking" };
    public sealed record Data(int MinimumAccounts);
    public static readonly SheetValue<Data> Sheet = new("GuildConfig.xml <GuildRanking>", "guild ranking account threshold",
        new(0), dir =>
        {
            string path = Path.Combine(dir, "GuildConfig.xml"); if (!File.Exists(path)) return null;
            var row = XDocument.Load(path).Root?.Element("GuildRanking");
            return row == null ? null : new((int?)row.Attribute("minAccountNum") ?? 0);
        }, _ => 1);
    public static bool TryExecute(GameSession session, CharacterStore? store, GmCommandLine line, ILogger log)
    {
        if (!Names.Contains(line.Name)) return false;
        if (store != null && session.SelectedCharacter != null)
            store.CalculateGuildLevelRanks(Sheet.Value.MinimumAccounts, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        return true;
    }
}
