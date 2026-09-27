// SPDX-License-Identifier: MIT
// Copyright (c) 2026 the TeraSharp contributors

using TeraSharp.Arbiter.Handlers;
using TeraSharp.Arbiter.Persistence;
using TeraSharp.Arbiter.World;

namespace TeraSharp.Arbiter.Tests;

public static partial class Tests
{
    [Test] public static void T201_guild_rank_calculation_publishes_persistent_native_snapshot_and_previous_rank()
    {
        using var e = new T201UtilityEnvironment();
        File.WriteAllText(Path.Combine(e.DirectoryPath, "GuildConfig.xml"), "<GuildConfig><GuildRanking minAccountNum='2' refreshHour='0'/></GuildConfig>");
        QaGuildRankingCommands.Sheet.Load(e.DirectoryPath);
        try
        {
            int Make(string name, long created, int accounts, bool duplicate = false)
            {
                var first = e.Store.GetOrCreateAccount(name + "0");
                int chief = e.Store.CreateCharacter(new CharacterRecord { AccountId = first.Id, Name = name + "chief" });
                int guild = e.Store.CreateGuild(name, chief, false, createdAt: created);
                e.Store.AddGuildMember(guild, chief, name + "chief", 0, 0, 0, 70, first.Id);
                for (int i = 1; i < accounts; i++)
                {
                    long account = duplicate ? first.Id : e.Store.GetOrCreateAccount(name + i).Id;
                    int id = e.Store.CreateCharacter(new CharacterRecord { AccountId = account, Name = name + i });
                    e.Store.AddGuildMember(guild, id, name + i, 0, 0, 0, 70, account);
                }
                e.Store.UpdateGuildEconomy(guild, row => row with { Exp = 100, Level = 2 }); return guild;
            }
            int a = Make("Alpha", 200, 2), b = Make("Beta", 300, 3), c = Make("Charlie", 100, 2);
            int excluded = Make("Duplicate", 1, 2, true); e.Store.UpdateGuildEconomy(excluded, row => row with { Exp = 999 });
            Hex.True(GuildBoard.Ranking(e.Store).Count == 0, "board remains empty before job, as four retail captures");
            T181WithOperators(null, () => e.Run("calc_guild_level_ranking"));
            Hex.True(e.Store.GetGuildLevelRanks().Count == 0, "nonoperator cannot recalculate realm snapshot");
            T181WithOperators("utility-op", () => e.Run("calc_guild_level_ranking"));
            Hex.True(e.Main.Available == 0 && e.Dungeon.Available == 0 && e.Caller.Available == 0, "native calculation emits no invented response");
            var rows = e.Store.GetGuildLevelRanks();
            Hex.True(rows.Select(r => r.GuildId).SequenceEqual(new[] { b, c, a }), "exp then distinct accounts then oldest creation; not name or level");
            Hex.True(rows.All(r => r.PreviousRank == 0) && rows[0].AccountCount == 3, "first snapshot zero previous rank and distinct-account count");
            GuildBoard.OnRequestGuildLevelRanking(e.Caller.Session, BitConverter.GetBytes(1), QuietLog());
            var frame = e.Caller.Frame(); Hex.True(BitConverter.ToUInt16(frame, 2) == 0xDFA0 && BitConverter.ToInt32(frame, 26) == 1
                && BitConverter.ToInt32(frame, 30) == 0 && BitConverter.ToInt32(frame, 46) == 3 && BitConverter.ToInt32(frame, 50) == 2,
                "native DFA0 consumer emits persisted rank/previous/accountCount/level at exact offsets");
            e.Store.UpdateGuildEconomy(a, row => row with { Exp = 200, Level = 9 });
            Hex.True(GuildBoard.Ranking(e.Store)[0].GuildName == "Beta" && GuildBoard.Ranking(e.Store)[2].GuildLevel == 2,
                "ranking and level do not mutate before next calculation");
            T181WithOperators("utility-op", () => e.Run("calc_guild_level_ranking"));
            using var reopened = new CharacterStore(Path.Combine(e.DirectoryPath, "store.db"), QuietLog());
            var winner = reopened.GetGuildLevelRanks()[0];
            Hex.True(winner.GuildId == a && winner.Rank == 1 && winner.PreviousRank == 3 && winner.PreviousExp == 100 && winner.Level == 9,
                "second snapshot preserves previous rank/experience through restart");
            Hex.True(GuildBoard.Ranking(reopened)[0].PreRanking == 3, "client board reads durable previous rank after restart");
        }
        finally { QaGuildRankingCommands.Sheet.Load(HandshakeData.DatasheetDirectory()); }
    }

    [Test] public static void T201_guild_ranking_publishes_top100_but_keeps_previous_rank_beyond100()
    {
        using var store = new CharacterStore(":memory:", QuietLog()); int last = 0;
        for (int i = 0; i < 105; i++)
        {
            last = store.CreateGuild("guild" + i, 0, false, createdAt: i + 1);
            store.UpdateGuildEconomy(last, row => row with { Exp = 105 - i });
        }
        store.CalculateGuildLevelRanks(0, 1800000000);
        Hex.True(store.GetGuildLevelRanks().Count == 100 && store.GetGuildLevelRanks(false).Count == 105, "native top100 cache is distinct from full persisted calculation");
        store.UpdateGuildEconomy(last, row => row with { Exp = 1000 }); store.CalculateGuildLevelRanks(0, 1800000001);
        Hex.True(store.GetGuildLevelRanks()[0].PreviousRank == 105, "new top100 entrant retains previous rank from full SQL snapshot");
    }
}
