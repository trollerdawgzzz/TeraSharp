// SPDX-License-Identifier: MIT
// Copyright (c) 2026 the TeraSharp contributors

using System.Xml;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using TeraSharp.Arbiter.Network;
using TeraSharp.Arbiter.Persistence;
using TeraSharp.Arbiter.World;

namespace TeraSharp.Arbiter.Handlers;

public static class QaCompetitionCommands
{
    public static IReadOnlySet<string> Names { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "update_pverank", "clear_pverank_player", "rank_sort" };
    public static bool TryExecute(GameSession session, CharacterStore? store, GmCommandLine line, ILogger log)
    {
        if (!Names.Contains(line.Name)) return false;
        if (!session.InWorld || session.SelectedCharacter == null || store == null) return true;
        if (line.Name.Equals("rank_sort", StringComparison.OrdinalIgnoreCase))
        {
            // Arb079:12686 flushes DungeonRankingManager's pending vectors into its read cache.
            // TeraSharp commits each rank write synchronously and queries it directly, so no
            // pending vector exists. Reads are already current; no client/World packet is native.
            return true;
        }
        if (line.Args.Count == 0 || !int.TryParse(line.Args[0], out int continent)) return true;
        int owner = (int)session.PlayerId;
        if (line.Name.Equals("clear_pverank_player", StringComparison.OrdinalIgnoreCase))
        { store.DeleteCompetitionResult(owner, continent, RankingBoards.CurrentSeason); return true; }
        if (line.Args.Count < 3 || !int.TryParse(line.Args[1], out int stage)
            || !long.TryParse(line.Args[2], out long time) || !IsCompetitionDungeon(continent)) return true;
        var result = store.UpdateCompetitionResult(owner, continent, RankingBoards.CurrentSeason, session.SelectedCharacter.Class, stage, time);
        if (result != null) session.Send(BuildResult(stage, time, result));
        return true;
    }

    internal static bool IsCompetitionDungeon(int id)
    {
        if (!QaDungeonCommands.HasContinent(id)) return false;
        try
        {
            string path = Path.Combine(HandshakeData.DatasheetDirectory(), "CompetitionDungeon.xml");
            if (!File.Exists(path)) return false;
            var row = XDocument.Load(path).Descendants("Dungeon").FirstOrDefault(e => (int?)e.Attribute("id") == id);
            if (row == null) return false;
            // LeaderBoardDataSheet key LeaderboardsData maps to Leaderboards.xml in ServerConfig.
            path = Path.Combine(HandshakeData.DatasheetDirectory(), "Leaderboards.xml");
            if (!File.Exists(path)) return false;
            var board = XDocument.Load(path).Descendants("ContentsTypeList")
                .Where(e => (string?)e.Attribute("type") == "dungeon").Elements("ContentInfo")
                .FirstOrDefault(e => (int?)e.Attribute("id") == id);
            return board != null && !string.Equals((string?)board.Attribute("seasonOut"), "true", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception e) when (e is IOException or XmlException or UnauthorizedAccessException or FormatException) { return false; }
    }

    internal static byte[] BuildResult(int stage, long time, CharacterStore.CompetitionUpdate result)
    {
        // Arb065:10374-10441 S_DUNGEON_RECORD_INFO. Non-improvements still echo attempted values.
        var p = new byte[29]; BitConverter.GetBytes((ushort)29).CopyTo(p, 0); BitConverter.GetBytes((ushort)0xF247).CopyTo(p, 2);
        p[4] = result.Improved ? (byte)1 : (byte)0; BitConverter.GetBytes(stage).CopyTo(p, 5); BitConverter.GetBytes(time).CopyTo(p, 9);
        BitConverter.GetBytes(result.OldStage).CopyTo(p, 17); BitConverter.GetBytes(result.OldTime).CopyTo(p, 21); return p;
    }

    internal static List<RankingRow> Rank(IEnumerable<CharacterStore.RankingScore> scores, int classFilter)
    {
        var sorted = scores.Where(s => RankingBoards.ClassMatches(classFilter, s.Class)).OrderByDescending(s => s.Level).ThenBy(s => s.Score).ThenBy(s => s.CharacterId).ToArray();
        var rows = new List<RankingRow>(); int rank = 0;
        for (int i = 0; i < sorted.Length; i++)
        {
            var s = sorted[i]; if (i == 0 || s.Level != sorted[i - 1].Level || s.Score != sorted[i - 1].Score) rank = i + 1;
            rows.Add(new(rank, s.CharacterId, s.Name, s.Class, s.Level, s.Score));
        }
        return rows;
    }
}
