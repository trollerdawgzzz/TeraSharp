// SPDX-License-Identifier: MIT
// Copyright (c) 2026 the TeraSharp contributors

using System.Text;
using TeraSharp.Arbiter.Game;
using TeraSharp.Arbiter.Handlers;
using TeraSharp.Arbiter.Persistence;
using TeraSharp.Arbiter.Protocol;
using TeraSharp.Arbiter.World;

namespace TeraSharp.Arbiter.Tests;

public static partial class Tests
{
    private static string T201GuildSheets()
    {
        string dir = Path.Combine(Path.GetTempPath(), "t201-guild-" + Guid.NewGuid());
        Directory.CreateDirectory(Path.Combine(dir, "GuildQuest"));
        // Deliberately different from live: proves no level/point/weekly-boundary constants.
        File.WriteAllText(Path.Combine(dir, "GuildConfig.xml"),
            "<GuildConfig><GuildLevelTable maxLevel='3'>"
            + "<GuildLevel level='1' guildExpNeeded='0' earnGuildPoint='2'/>"
            + "<GuildLevel level='2' guildExpNeeded='10' earnGuildPoint='3'/>"
            + "<GuildLevel level='3' guildExpNeeded='50' earnGuildPoint='7'/>"
            + "</GuildLevelTable></GuildConfig>");
        File.WriteAllText(Path.Combine(dir, "GuildQuest", "GuildQuestConfig.xml"),
            "<GuildQuestconfig weeklyRewardResetWeekDay='Tuesday' weeklyRewardResetHour='9'/>");
        Hex.True(GuildLevelSheet.Entry.Load(dir).FromSheet && GuildLevelSheet.QuestEntry.Load(dir).FromSheet,
            "both guild QA sheets load through startup/check-config registry");
        return dir;
    }

    [Test] public static void T201_guild_point_money_exp_and_level_follow_native_state_and_broadcasts()
    {
        string dir = T201GuildSheets();
        try
        {
            using var store = GuildStore(2);
            int guild = store.CreateGuild("T201", 1, false);
            store.AddGuildMember(guild, 1, "g1", 1, 2, 1, 31, 1);
            store.AddGuildMember(guild, 2, "g2", 1, 2, 0, 32, 1);
            store.UpdateGuildEconomy(guild, g => g with { Level = 1, Exp = 0, Point = 0, Money = 0 });
            GuildActions Run(string command) => QaGuildCardCommands.ApplyGuildEconomy(store, 1, GmCommandParser.Parse(command)!);

            var point = Run("add_guild_point 99");
            Hex.True(store.GetGuild(guild)!.Point == 12, "Arb0034203 cap is sum of all earnGuildPoint values");
            Hex.True(point.ToClients.Count == 2 && point.ToClients.All(x => x.PacketName == "S_GUILD_POINT_INFO_CHANGED"
                && (long)x.Fields!["newPoint"] == 12), "C642 guild/id and signed64 point to both members");
            Hex.True(point.ToWorld.Single().Opcode == 0x144E, "guild mirror precedes point broadcast");
            Hex.Eq(new DefinitionWriter().Write(DefinitionParser.ParseText("S_GUILD_POINT_INFO_CHANGED",
                "int32 guildDbId\nint64 newPoint\n"), point.ToClients[0].Fields!),
                Convert.FromHexString("010000000C00000000000000"), "Arb0455504-5506 native C642 payload");
            Hex.True(Run("add_guild_point -13").IsEmpty && store.GetGuild(guild)!.Point == 12,
                "native rejects negative point balances rather than clamping");
            Run("add_guild_point -12");
            var money = Run("add_guild_money 9000000000");
            Hex.True(store.GetGuild(guild)!.Money == 9000000000L && money.ToClients.All(x =>
                x.PacketName == "S_GUILD_MONEY_INFO_CHANGED" && (long)x.Fields!["newMoney"] == 9000000000L),
                "E4DB uses signed64 money, not an int32 command argument");
            Hex.Eq(new DefinitionWriter().Write(DefinitionParser.ParseText("S_GUILD_MONEY_INFO_CHANGED",
                "int32 guildDbId\nint64 newMoney\n"), money.ToClients[0].Fields!),
                Convert.FromHexString("01000000001A711802000000"), "Arb0455425-5427 native E4DB payload");
            Hex.True(Run("add_guild_money -9000000001").IsEmpty && store.GetGuild(guild)!.Money == 9000000000L,
                "native rejects overdraft");

            Run("add_guild_exp 10");
            Hex.True(store.GetGuild(guild)!.Level == 1, "Arb0032122 strict threshold preserves current level at equality");
            var exp = Run("add_guild_exp 1");
            var after = store.GetGuild(guild)!;
            Hex.True(after.Level == 2 && after.Exp == 11 && after.Point == 3,
                "next sheet level earns its own configured point value");
            Hex.True(exp.ToClients.Count(x => x.PacketName == "S_SYSTEM_MESSAGE") == 2
                && exp.ToClients.Any(x => x.PacketName == "S_SYSTEM_MESSAGE"
                    && (string)x.Fields!["message"] == "@1108\vGuildLevel\v2"), "native level-up message to each member");
            Hex.True(exp.Ordered.Last() is WorldAction && exp.ToClients.Count(x =>
                x.PacketName == "S_GUILD_LEVEL_INFO_CHANGED" && (byte)x.Fields!["isLevelUp"] == 1) == 2,
                "F96B level+XP precedes C642 points, then World mirror");
            Hex.Eq(new DefinitionWriter().Write(DefinitionParser.ParseText("S_GUILD_LEVEL_INFO_CHANGED",
                "int32 guildDbId\nint32 newLevel\nint64 newExp\nbyte isLevelUp\n"),
                exp.ToClients.First(x => x.PacketName == "S_GUILD_LEVEL_INFO_CHANGED").Fields!),
                Convert.FromHexString("01000000020000000B0000000000000001"), "Arb0455344-5348 native F96B payload");
            Run("add_guild_exp -20");
            Hex.True(store.GetGuild(guild)!.Level == 2 && store.GetGuild(guild)!.Exp == -9,
                "native signed delta does not downgrade the level or clamp XP");
            var set = Run("guild_level 99");
            after = store.GetGuild(guild)!;
            Hex.True(after.Level == 3 && after.Exp == 50 && after.Point == 3,
                "direct level command caps at sheet maximum, sets threshold XP, preserves points");
            Hex.True(set.ToClients.Count(x => x.PacketName == "S_GUILD_POINT_INFO_CHANGED") == 0,
                "direct level does not invent a point grant");
            foreach (string command in new[] { "guild_level 0", "guild_level -1", "add_guild_exp 4 5", "add_guild_money invalid" })
                Hex.True(Run(command).IsEmpty, "invalid QA input is silent: " + command);

            // cap_final2_clients/capture_2026-09-22T08-58-07-796Z.log2118 -> 2119,
            // source lines6352-6356. Exact real response: guild3, 10,000,000 money.
            using var captured = GuildStore(1);
            captured.CreateGuild("unused1", 1, false); captured.CreateGuild("unused2", 1, false);
            int capturedGuild = captured.CreateGuild("captured", 1, false);
            captured.AddGuildMember(capturedGuild, 1, "g1", 1, 2, 1, 31, 1);
            var capturedAction = QaGuildCardCommands.ApplyGuildEconomy(captured, 1,
                GmCommandParser.Parse("add_guild_money 10000000")!).ToClients.Single();
            var capturedBody = new DefinitionWriter().Write(DefinitionParser.ParseText("S_GUILD_MONEY_INFO_CHANGED",
                "int32 guildDbId\nint64 newMoney\n"), capturedAction.Fields!);
            Hex.Eq(new byte[] { 16, 0, 0xDB, 0xE4 }.Concat(capturedBody).ToArray(),
                Convert.FromHexString("1000DBE4030000008096980000000000"), "retail2119 complete16B reply, no normalized fields");
        }
        finally
        {
            GuildLevelSheet.Entry.UseBuiltIn(); GuildLevelSheet.QuestEntry.UseBuiltIn();
            Directory.Delete(dir, true);
        }
    }

    [Test] public static void T201_guild_quest_usable_changes_only_callers_season_flag_and_reset_clears_own_status()
    {
        string dir = T201GuildSheets();
        try
        {
            using var store = GuildStore(3);
            int guild = store.CreateGuild("T201Q", 1, false), other = store.CreateGuild("Other", 3, false);
            store.AddGuildMember(guild, 1, "g1", 1, 2, 1, 31, 1);
            store.AddGuildMember(other, 3, "g3", 1, 2, 1, 33, 1);
            store.AddGuildMember(guild, 2, "g2", 1, 2, 0, 32, 1);
            var handlers = NewGuildHandlers(store);
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            long joined = store.GetGuildMember(1)!.GuildJoinDate;
            var on = handlers.SetQaGuildQuestUsable(1, true, now);
            Hex.True(on.ToClients.Single(x => x.CharacterId == 1).RawBody![76] == 0
                && on.ToClients.Single(x => x.CharacterId == 2).RawBody![76] == 1,
                "Arb030329/347: full80 is member-specific IsFirstSeasonUser, not guild-global consent");
            var off = handlers.SetQaGuildQuestUsable(1, false, now);
            Hex.True(off.ToClients.Single(x => x.CharacterId == 1).RawBody![76] == 1,
                "off makes caller a first-season member again");
            Hex.True(store.GetGuildMember(1)!.GuildJoinDate == joined,
                "Arb02910314 changes RAM only, not persisted membership date");
            Hex.True(handlers.BuildGuildQuestList(store.GetGuild(guild)!, now + 8 * 86400L, 1)[76] == 0,
                "off expires at the sheet's weekly boundary");
            store.SetGuildQuest(guild, 10001, 1, now, now + 300, 1, 5);
            store.SetGuildQuest(guild, 10002, 0, now, now, 1, 99);
            store.SetGuildQuest(other, 10001, 1, now, now + 300, 3, 5);
            var reset = handlers.ResetQaGuildQuest(1);
            Hex.True(store.GetGuildQuests(guild).Count == 0 && store.GetGuildQuests(other).Count == 1,
                "Arb0289281 deletes every status for this guild, preserving other guilds");
            Hex.True(reset.ToClients.Count == 1 && reset.ToClients[0].CharacterId == 1
                && reset.ToClients[0].PacketName == "S_GUILD_QUEST_LIST", "native reset sends caller's refreshed board");
        }
        finally
        {
            GuildLevelSheet.Entry.UseBuiltIn(); GuildLevelSheet.QuestEntry.UseBuiltIn();
            Directory.Delete(dir, true);
        }
    }

    [Test] public static void T201_card_QA_commands_refresh_current_world_and_reset_all_account_presets()
    {
        if (FixtureOrSkip(Path.Combine("data", "t190", "cards", "CardTemplate.xml"), "T190 CardTemplate.xml") is null) return;
        T190LoadCardSheet();
        string map = Path.GetTempFileName();
        using var store = GuildStore(2);
        using var env = new T185Environment(null);
        var storeProperty = typeof(TeraSharp.Arbiter.Program).GetProperty("Store")!;
        var oldStore = storeProperty.GetValue(null); storeProperty.SetValue(null, store);
        try
        {
            File.WriteAllText(map, "{\"maps\":{\"376012\":{}}}");
            var definitions = new DefinitionRegistry(QuietLog());
            definitions.RegisterFromDef("C_ADMIN", "ref command\nstring command\n");
            var bridge = new WorldBridge(WorldReplayTable.Load("/nonexistent", QuietLog()), QuietLog());
            using var main = new T192WorldPeer(bridge, 1, 0);
            using var instance = new T192WorldPeer(bridge, 13, 13);
            T185Environment.SetWorld(bridge);
            using var client = new T185Client(definitions, OpcodeTable.LoadFromFile(map, "376012"), QuietLog());
            client.Session.Account.Name = "t201-operator";
            client.Session.PlayerId = 1; client.Session.GameId = 20101;
            client.Session.SelectedCharacter = new FakeCharacter { Id = 1, Name = "g1" };
            client.Session.EnterWorld(); client.Session.CurrentWorldId = 13;
            var handler = new GmCommandHandlers(QuietLog());
            void Run(string text) => handler.OnAdminCommand(client.Session,
                new byte[] { 6, 0 }.Concat(Encoding.Unicode.GetBytes(text + "\0")).ToArray());
            void Refresh() => Hex.Eq(instance.Frame(), Convert.FromHexString("0E00000085290100000000000000"),
                "cap_2man_b15530: account1 refresh reaches current World13");
            T181WithOperators("t201-operator", () =>
            {
                Run("set_card_preset_amount 4"); Refresh();
                Hex.True(store.GetCardInfo(1).PresetAmount == 4, "preset command sets absolute positive amount");
                Run("add_card_collection_point 7500"); Refresh();
                Hex.True(store.GetCardInfo(1) == new CharacterStore.CardInfoRow(4, 3, 7500), "card point level uses sheet");
                Run("add_card_collection_point -10000"); Refresh();
                Hex.True(store.GetCardInfo(1).BookPoint == 0 && store.GetCardInfo(1).BookLevel == 1, "negative points floor0");
                Run("card_collection_level 2"); Refresh();
                Hex.True(store.GetCardInfo(1) == new CharacterStore.CardInfoRow(4, 2, 2500), "level command sets sheet needPoint");
                Run("set_card_preset_amount 0"); Run("card_collection_level 999");
                Hex.True(instance.Available == 0, "invalid preset/level do not mutate or refresh");
                store.AddCard(1, 311034, 20); store.MountCard(1, 0, 311034); store.MountCard(2, 1, 311034);
                store.SetCardPresetIndex(2, 1); store.SetCardCombine(1, 3, 1); store.AddCardBookReward(1, 1);
                long other = store.GetOrCreateAccount("t201-other").Id; store.AddCard(other, 311034, 5);
                Run("reset_card_collection");
                Hex.Eq(client.Frame(), Convert.FromHexString("0400E3DA"), "Arb0441037 native S_CARD_DATA_RESET"); Refresh();
                Hex.True(store.GetAccountCards(1).Count == 0 && store.GetCardMounts(1).Count == 0
                    && store.GetCardMounts(2).Count == 0 && store.GetCardCombines(1).Count == 0
                    && store.GetCardBookRewards(1).Count == 0 && store.GetCardInfo(1) == CharacterStore.DefaultCardInfo,
                    "SQL21055-21075 clears all five account-owned collections");
                Hex.True(store.GetCardPresetIndex(2) == 1 && store.GetAccountCards(other).Single().Amount == 5,
                    "SQL preserves selected indices and every other account");
            });
            T181WithOperators(null, () =>
            {
                foreach (string name in QaGuildCardCommands.Names) Run(name + " 1");
                Hex.True(instance.Available == 0 && main.Available == 0 && client.Available == 0
                    && store.GetCardInfo(1) == CharacterStore.DefaultCardInfo, "all commands retain central operator gate");
            });
            Hex.True(main.Available == 0, "no card refresh leaked to World0");
        }
        finally
        {
            T185Environment.SetWorld(null); storeProperty.SetValue(null, oldStore);
            CardCollectionSheet.Entry.UseBuiltIn(); File.Delete(map);
        }
    }
}
