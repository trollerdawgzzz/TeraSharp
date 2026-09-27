// SPDX-License-Identifier: MIT
// Copyright (c) 2026 the TeraSharp contributors

using System.Text;
using TeraSharp.Arbiter.Handlers;
using TeraSharp.Arbiter.Persistence;
using TeraSharp.Arbiter.World;

namespace TeraSharp.Arbiter.Tests;

public static partial class Tests
{
    [Test] public static void T201_guild_QA_native_toggle_contradiction_and_quest_deadline_clear()
    {
        string directory = Path.Combine(Path.GetTempPath(), "t201-guild-mode-" + Guid.NewGuid());
        string sheets = Path.Combine(directory, "Datasheet"); Directory.CreateDirectory(sheets);
        try
        {
            File.WriteAllText(Path.Combine(directory, "DeploymentConfig.xml"), "<DeploymentConfig><ArbiterServerConfig pveServer='false'/></DeploymentConfig>");
            QaGuildSheet.Mode.Load(sheets);
            using var h = new T201AccountHarness(); int guild = h.Store.CreateGuild("qa", 1, false);
            h.Store.AddGuildMember(guild, 1, "g1", 0, 0, 0, 70, 1);
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            h.Store.SetGuildQuest(guild, 10002, 1, now, now + 60, 1, 0);
            h.Store.SetGuildQuest(guild, 10003, 0, now, now + 60, 1, 0);
            T181WithOperators("t39", () =>
            {
                h.Run("toggle_guildwar_acceptable");
                Hex.True(Encoding.Unicode.GetString(h.Client.Frame()[6..]).TrimEnd('\0') == "Cannot change GuildWar acceptable-state"
                    && !h.Store.GetGuild(guild)!.WarAcceptable, "native wrapper and setter guards make PvP toggle fail, confirmed from PE");
                h.Run("reset_guildwar_toggle_cool"); h.Client.Frame();
                Hex.True(h.Main.Available == 0 && h.Instance.Available == 0, "live PvP reset is only native custom confirmation, no World push");
                File.WriteAllText(Path.Combine(directory, "DeploymentConfig.xml"), "<DeploymentConfig><ArbiterServerConfig pveServer='true'/></DeploymentConfig>");
                QaGuildSheet.Mode.Load(sheets); h.Run("toggle_guildwar_acceptable");
                Hex.True(h.Client.Available == 0, "native PvE wrapper exits silently before calling setter");
                h.Run("guild_quest_add_limit_time 2");
                var quests = h.Store.GetGuildQuests(guild);
                Hex.True(quests.Single(x => x.QuestId == 10002).EndsAt == now + 180
                    && quests.Single(x => x.QuestId == 10003).EndsAt == now + 60, "native minutes apply only to active quest deadlines");
                h.Run("guild_quest_add_limit_time -1");
                Hex.True(h.Store.GetRunningGuildQuest(guild)!.EndsAt == now + 120, "native signed minutes accumulate in RAM");
                h.Run("guild_quest_clear"); Hex.True(h.Store.GetGuildQuests(guild).Count == 0, "native clear deletes only this guild's persisted quest statuses");
                h.Store.SetGuildQuest(guild, 10002, 1, now, now + 60, 1, 0);
                Hex.True(h.Store.GetRunningGuildQuest(guild)!.EndsAt == now + 60, "cleared QA timer does not bleed into restarted quest");
            });
        }
        finally { QaGuildSheet.Mode.UseBuiltIn(); Directory.Delete(directory, true); }
    }

    [Test] public static void T201_guild_QA_emblems_use_sheet_permanence_and_native_collect_arguments()
    {
        string directory = Path.Combine(Path.GetTempPath(), "t201-emblem-" + Guid.NewGuid()); Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "GuildEmblem.xml"), "<GuildEmblem><Emblem id='42' forever='true'/><Emblem id='9' forever='false'/></GuildEmblem>");
        try
        {
            QaGuildSheet.Emblems.Load(directory);
            using var h = new T201AccountHarness(); int guild = h.Store.CreateGuild("emblem", 1, false);
            h.Store.AddGuildMember(guild, 1, "g1", 1, 2, 1, 70, 1);
            TeraSharp.Arbiter.Program.World!.RegisterPlayer(h.Client.Session);
            T181WithOperators("t39", () =>
            {
                h.Run("assign_guild_emblem 42");
                byte[] forever = Convert.FromHexString("0F000000AF1501000000012A000000");
                Hex.Eq(h.Main.Frame(), forever, "Arb04614942: 15AF guild/forever/id, global World push");
                Hex.Eq(h.Instance.Frame(), forever, "all registered Worlds receive the emblem");
                Hex.True(h.Store.GetGuild(guild)!.ForeverEmblemId == 42, "permanent emblem stored in distinct native field");
                Hex.Eq(h.Client.Frame(), Convert.FromHexString("1C004EEA01000800080000000C0000002A0000000000000000000000"),
                    "Arb04519407 native reward message: linked entry type12, emblem42, amount0 to online members");
                Hex.True(h.Store.GetGuildLog(guild, 0).Count == 0, "native system reward broadcast does not insert a guild log");
                h.Run("collect_guild_emblem 9"); Hex.True(h.Main.Available == 0, "native collect requires two arguments");
                h.Run("collect_guild_emblem 9 ignored");
                byte[] temporary = Convert.FromHexString("0F000000AF15010000000009000000");
                Hex.Eq(h.Main.Frame(), temporary, "Arb07211378: collect writes supplied template id; second arg is unused"); h.Instance.Frame();
                Hex.True(h.Store.GetGuild(guild)!.EmblemId == 9 && h.Store.GetGuild(guild)!.ForeverEmblemId == 42, "temporary write preserves permanent slot");
                Hex.True(h.Client.Available == 0, "native collect has no assignment reward broadcast");
                h.Run("assign_guild_emblem 9999"); Hex.True(h.Main.Available == 0, "unknown sheet emblem is rejected");
            });
        }
        finally { QaGuildSheet.Emblems.UseBuiltIn(); Directory.Delete(directory, true); }
    }

    [Test] public static void T201_guild_QA_war_wrappers_reuse_native_client_entry_points()
    {
        using var h = new T201AccountHarness(); GuildWarManager.ResetForTests();
        int mine = h.Store.CreateGuild("mine", 1, false), theirs = h.Store.CreateGuild("theirs", 1, false);
        h.Store.AddGuildMember(mine, 1, "g1", 1, 2, 1, 70, 1); h.Store.AddGuildMoney(mine, 100000);
        T181WithOperators("t39", () =>
        {
            h.Run("guildwar_declare theirs"); h.Main.Frame(); h.Instance.Frame();
            byte[] declaration = h.Main.Frame(); var war = h.Store.GetGuildWarBetween(mine, theirs)!;
            Hex.True(war != null && BitConverter.ToUInt16(declaration, 4) == 0x14A3, "QA04016454 and client0411339 both call native6BB1E0");
            Hex.Eq(declaration.AsSpan(6).ToArray(), GuildWarManager.BuildAsDeclare(war!.WarId, mine, theirs), "existing captured AS_DECLARE layout");
            h.Run("guildwar_cancel theirs");
            byte[] cancelled = h.Main.Frame();
            Hex.True(h.Store.GetGuildWarBetween(mine, theirs) == null && BitConverter.ToUInt16(cancelled, 4) == 0x14AC
                && BitConverter.ToInt32(cancelled, 22) == 1, "QA04016358 and client04114781 share native6CFF50 withdrawal");
            h.Run("guildwar_declare theirs"); h.Main.Frame(); h.Instance.Frame(); h.Main.Frame();
            h.Run("guildwar_surrender theirs");
            byte[] result;
            do { result = h.Main.Frame(); } while (BitConverter.ToUInt16(result, 4) != 0x14AC);
            Hex.True(h.Store.GetGuildWarBetween(mine, theirs) == null && BitConverter.ToInt32(result, 22) == 4,
                "QA04016520 and client0413094 share native6BF1B0 surrender, reason4");
        });
        GuildWarManager.ResetForTests();
    }

    [Test] public static void T201_guild_QA_create_and_join_use_existing_membership_fanout()
    {
        using var h = new T201AccountHarness(); TeraSharp.Arbiter.Program.World!.RegisterPlayer(h.Client.Session);
        T181WithOperators("t39", () =>
        {
            h.Run("create_guild qa missing"); h.Client.Frame(); Hex.True(h.Store.GetGuildByName("qa") == null, "native requires online first user");
            h.Run("create_guild qa g1"); h.Client.Frame();
            int guild = h.Store.GetGuildIdOf(1);
            Hex.True(guild != 0 && h.Store.GetGuildMembers(guild).Single().GuildGroupId == 1,
                "native helper permits chief also named as first user and creates Master group1");
            int other = h.Store.CreateGuild("other", 1, false);
            h.Store.RemoveGuildMember(1, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            h.Run("join_guild other"); h.Client.Frame();
            Hex.True(h.Store.GetGuildIdOf(1) == other && h.Store.GetGuildMember(1)!.GuildGroupId == 2,
                "native UserJoinToGuild mode2 joins caller using Member group2");
            h.Run("create_guild third g1"); h.Client.Frame();
            Hex.True(h.Store.GetGuildByName("third") == null, "already guilded caller refused");
        });
    }

    [Test] public static void T201_guild_recommendation_contribution_coin_and_weekly_time_use_native_consumers()
    {
        string directory = Path.Combine(Path.GetTempPath(), "t201-guild-runtime-" + Guid.NewGuid());
        Directory.CreateDirectory(Path.Combine(directory, "GuildQuest"));
        File.WriteAllText(Path.Combine(directory, "GuildQuest", "GuildQuestConfig.xml"), "<GuildQuestconfig><ContributionPointconfig weeklyLimit='4'/></GuildQuestconfig>");
        File.WriteAllText(Path.Combine(directory, "FloatingCastle.xml"), "<FloatingCastle><CommonConfig maxFloatingCastlePartsCoin='12'/></FloatingCastle>");
        try
        {
            QaGuildSheet.Entry.Load(directory);
            using var h = new T201AccountHarness();
            int guild = h.Store.CreateGuild("runtime", 1, false);
            h.Store.AddGuildMember(guild, 1, "g1", 1, 2, 1, 70, 1);
            int otherGuild = h.Store.CreateGuild("other", 1, false); h.Store.AddQaGuildWeeklyTime(otherGuild, 7000);
            T181WithOperators("t39", () =>
            {
                h.Run("set_guild_rec 7");
                byte[] recommendation = h.Main.Frame();
                Hex.Eq(recommendation, Convert.FromHexString("0E00000011140100000007000000"), "Arb04519577 native1411 guild/id and absolute recommendation");
                Hex.Eq(h.Instance.Frame(), recommendation, "recommendation broadcasts to every registered World");
                Hex.True(h.Store.GetGuild(guild)!.RecommendationPoint == 7, "persisted value feeds existing guild-info view");
                h.Run("add_guild_contribution_point 9"); h.Main.Frame(); h.Instance.Frame();
                var member = h.Store.GetGuildMember(1)!;
                Hex.True(member.WeeklyContribution == 4 && member.TotalContribution == 4, "native weekly cap also clamps the total delta");
                h.Run("add_guild_contribution_point 1"); Hex.True(h.Main.Available == 0, "already-capped member causes no write/broadcast");
                h.Run("set_floating_castle_coin 30");
                Hex.Eq(h.Main.Frame(), Convert.FromHexString("12000000A41501000000000000000C000000"), "Arb04615264 native15A4 is guild/type0/APPLIED DELTA12 to World0");
                Hex.True(h.Store.GetGuild(guild)!.GeneralCoin == 12 && h.Instance.Available == 0, "native coin sheet cap and specific server0 route");
                h.Run("set_floating_castle_coin -13"); Hex.True(h.Main.Available == 0, "negative resultant balance refused");
                h.Run("add_guild_this_week_play_time runtime 9"); h.Client.Frame();
                h.Run("show_guild_week_play_time runtime");
                string message = Encoding.Unicode.GetString(h.Client.Frame(), 6, 2 * "[LastWeekPlayTime = 0] [ThisWeekPlayTime = 9000]".Length);
                Hex.True(message == "[LastWeekPlayTime = 0] [ThisWeekPlayTime = 9000]", "native QA display uses stored milliseconds");
                h.Run("set_guild_play_time_next_week"); h.Client.Frame();
                Hex.True(h.Store.GetGuild(guild)!.LastWeekPlayTime >= 9000 && h.Store.GetGuild(guild)!.ThisWeekPlayTime == 0
                    && h.Store.GetGuild(otherGuild)!.LastWeekPlayTime == 7000, "native rollover changes all guilds");
                var start = new DateTime(2020, 2, 3, 0, 0, 0, DateTimeKind.Utc);
                h.Store.StampLogin(1, start);
                h.Store.AdvanceQaGuildWeek(new[] { 1 }, start.AddSeconds(3));
                Hex.True(h.Store.GetGuild(guild)!.LastWeekPlayTime == 3000,
                    "native rollover includes online play time since the last cursor, in milliseconds");
            });
        }
        finally { QaGuildSheet.Entry.UseBuiltIn(); QaGuildCommands.ResetForTests(); Directory.Delete(directory, true); }
    }

    [Test] public static void T201_guild_QA_member_limit_rejoin_wanted_and_logout_change_existing_state_paths()
    {
        int oldCooldown = GuildHandlers.RejoinCooldownSeconds;
        try
        {
            using var h = new T201AccountHarness();
            int guild = h.Store.CreateGuild("runtime", 1, false);
            h.Store.AddGuildMember(guild, 1, "g1", 1, 2, 1, 70, 1);
            var second = new CharacterRecord { Name = "second", AccountId = 1, Level = 1 };
            int secondId = h.Store.CreateCharacter(second);
            TeraSharp.Arbiter.Program.World!.RegisterPlayer(h.Client.Session);
            T181WithOperators("t39", () =>
            {
                h.Run("Guildaddmax 1");
                Hex.True(h.Store.AddGuildMember(guild, secondId, "second", 1, 2, 1, 1, 1) == 0,
                    "native hard character-count override is enforced at actual membership insertion");
                h.Run("Guildaddmax 0");
                Hex.True(h.Store.AddGuildMember(guild, secondId, "second", 1, 2, 1, 1, 1) != 0, "zero restores native300 cap");
                h.Store.RemoveGuildMember(secondId, DateTimeOffset.UtcNow.ToUnixTimeSeconds()); h.Run("guild_join_cooltime 2");
                var handlers = new GuildHandlers(h.Store, QuietLog());
                var on = handlers.OnClientPacket(secondId, 0xC9C4, Array.Empty<byte>()).ToClients.Single();
                Hex.True(GuildHandlers.RejoinCooldownSeconds == 7200 && (bool)on.Fields!["hasCoolTime"], "native command unit is hours; existing cooltime query sees it");
                h.Run("guild_cooltime OFF");
                var off = handlers.OnClientPacket(secondId, 0xC9C4, Array.Empty<byte>()).ToClients.Single();
                Hex.True(!(bool)off.Fields!["hasCoolTime"], "global off removes actual cooldown gate");
                h.Run("guild_cooltime ON");
                h.Store.SetGuildWanted(1, 0, 0, "wanted", 12345); h.Run("init_guild_wanted_writing_rewrite_time"); h.Client.Frame();
                Hex.True(h.Store.GetGuildWantedTime(1) == 0, "QA clears runtime repost cooldown");
                h.Store.RemoveGuildMember(1, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                Hex.True(h.Store.GetGuildWanted().Single().PromotionStr == "wanted", "QA preserves the board ad, hidden while its poster belongs to a guild");
                h.Store.AddGuildMember(guild, 1, "g1", 1, 2, 1, 70, 1);
                h.Store.SetGuildWanted(1, 0, 0, "again", 12346);
                Hex.True(h.Store.GetGuildWantedTime(1) == 12346, "subsequent post restores its ordinary cooldown");
                h.Run("set_logout_time g1 2020 2 3");
                DateTime expected = new(2020, 2, 3, 0, 0, 0, DateTimeKind.Local);
                Hex.True(h.Store.GetCharacter(1)!.LastLogout == expected.ToUniversalTime()
                    && h.Store.GetGuildMember(1)!.LastLogoutTime == new DateTimeOffset(expected).ToUnixTimeSeconds(),
                    "native target character and guild roster logout fields both persist");
            });
            T181WithOperators(null, () =>
            {
                foreach (string name in QaGuildCommands.Names) h.Run(name + " 1");
                Hex.True(h.Main.Available == 0 && h.Instance.Available == 0 && h.Client.Available == 0,
                    "every added guild QA name retains central authorization");
            });
        }
        finally { GuildHandlers.RejoinCooldownSeconds = oldCooldown; QaGuildCommands.ResetForTests(); }
    }
}
