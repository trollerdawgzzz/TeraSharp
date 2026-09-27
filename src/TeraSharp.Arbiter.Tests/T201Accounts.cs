// SPDX-License-Identifier: MIT
// Copyright (c) 2026 the TeraSharp contributors

using System.Text;
using Microsoft.Data.Sqlite;
using TeraSharp.Arbiter.Game;
using TeraSharp.Arbiter.Handlers;
using TeraSharp.Arbiter.Persistence;
using TeraSharp.Arbiter.Protocol;
using TeraSharp.Arbiter.World;

namespace TeraSharp.Arbiter.Tests;

public static partial class Tests
{
    private static string T201VipSheets(bool enabled)
    {
        string directory = Path.Combine(Path.GetTempPath(), "t201-vip-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "AccountTrait.xml"),
            "<AccountTrait><Package id='0'/><Package id='100'/><Package id='602'/></AccountTrait>");
        File.WriteAllText(Path.Combine(directory, "VIPSystem.xml"),
            "<VIPSystem><VIPSetting vipSystemOn='" + (enabled ? "true" : "false")
            + "' maxGrade='2' dailyTokenResetHour='12' vipDungeon='77'>"
            + "<Grade level='0' totalExp='0'/><Grade level='1' totalExp='10'/><Grade level='2' totalExp='25'/>"
            + "</VIPSetting><VipMail><LvUpMail sender='sender' title='title' body='body' addItemTemplateId='99999' addItemAmount='100'/></VipMail></VIPSystem>");
        File.WriteAllText(Path.Combine(directory, "VIPBenefit.xml"),
            "<VIPBenefit><Benefit id='1'><Property name='lvUpToken' value='90001'/></Benefit>"
            + "<Benefit id='2'><Property name='lvUpToken' value='90002'/></Benefit></VIPBenefit>");
        File.WriteAllText(Path.Combine(directory, "VIPShop.xml"), "<VIPShop resetHour='9,21'/>");
        File.WriteAllText(Path.Combine(directory, "WorldData.xml"), "<WorldData><Dungeon resetTime='7'/></WorldData>");
        File.WriteAllText(Path.Combine(directory, "DungeonConstraint.xml"), "<DungeonConstraint><Constraint continentId='77' enterLimitCount='1' coolTime='1'/></DungeonConstraint>");
        Hex.True(QaAccountSheet.Entry.Load(directory).FromSheet && VipSystemSheet.Entry.Load(directory).FromSheet,
            "QA package/VIP sheets registered and loaded");
        return directory;
    }

    private sealed class T201AccountHarness : IDisposable
    {
        internal readonly CharacterStore Store = GuildStore(1);
        internal readonly T185Client Client;
        internal readonly T192WorldPeer Main, Instance;
        private readonly T185Environment _environment = new(null);
        private readonly object? _oldStore;
        private readonly string _map = Path.GetTempFileName();
        private readonly GmCommandHandlers _handler = new(QuietLog());
        internal T201AccountHarness()
        {
            var property = typeof(TeraSharp.Arbiter.Program).GetProperty("Store")!;
            _oldStore = property.GetValue(null); property.SetValue(null, Store);
            File.WriteAllText(_map, "{\"maps\":{\"376012\":{\"S_SYSTEM_MESSAGE_CUSTOM\":39244,\"S_SYSTEM_REWARD_MESSAGE\":59982}}}");
            var defs = new DefinitionRegistry(QuietLog()); defs.RegisterFromDef("C_ADMIN", "ref command\nstring command\n");
            defs.RegisterFromDef("S_SYSTEM_MESSAGE_CUSTOM", "ref formatted\nstring formatted\n");
            var bridge = new WorldBridge(WorldReplayTable.Load("/nonexistent", QuietLog()), QuietLog());
            Main = new T192WorldPeer(bridge, 1, 0); Instance = new T192WorldPeer(bridge, 13, 13);
            T185Environment.SetWorld(bridge);
            Client = new T185Client(defs, OpcodeTable.LoadFromFile(_map, "376012"), QuietLog());
            Client.Session.Account.Name = "t39";
            Client.Session.PlayerId = 1; Client.Session.GameId = 20102;
            Client.Session.SelectedCharacter = new FakeCharacter { Id = 1, Name = "g1" };
            Client.Session.EnterWorld(); Client.Session.CurrentWorldId = 13;
        }
        internal void Run(string command) => _handler.OnAdminCommand(Client.Session,
            new byte[] { 6, 0 }.Concat(Encoding.Unicode.GetBytes(command + '\0')).ToArray());
        internal void VipNotification()
        {
            var world = Instance.Frame(); var client = Client.Frame();
            Hex.True(world.Length == 26 && BitConverter.ToUInt16(world, 4) == 0x15DC
                && BitConverter.ToInt32(world, 6) == 1, "native per-user 15DC reaches World13");
            var info = Store.GetVipInfo(1);
            Hex.True(BitConverter.ToInt32(world, 10) == info.GameExp && BitConverter.ToInt32(world, 14) == info.PubExp
                && BitConverter.ToInt64(world, 18) == info.TokenAmount, "15DC game/publisher/token field order");
            Hex.True(client.Length == 55 && BitConverter.ToUInt16(client, 2) == 0x70C2
                && BitConverter.ToUInt16(client, 4) == 53 && BitConverter.ToInt64(client, 11) == info.GameExp
                && BitConverter.ToInt64(client, 19) == info.PubExp && BitConverter.ToInt64(client, 27) == info.TokenAmount,
                "native S_VIP_INFO signed64 XP fields and empty greeting offset");
        }
        public void Dispose()
        {
            Client.Dispose(); Instance.Dispose(); Main.Dispose(); Store.Dispose();
            typeof(TeraSharp.Arbiter.Program).GetProperty("Store")!.SetValue(null, _oldStore);
            _environment.Dispose(); File.Delete(_map); QaGeneralCommands.ResetForTests();
        }
    }

    [Test] public static void T201_account_packages_broadcast_and_restriction_level_uses_native_OR_bits()
    {
        string directory = T201VipSheets(false);
        try
        {
            using var h = new T201AccountHarness();
            T181WithOperators("t39", () =>
            {
                long before = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                h.Run("add_package 100 120");
                var first = h.Main.Frame(); Hex.Eq(h.Instance.Frame(), first, "native15CE broadcasts every linked World");
                Hex.True(first.Length == 22 && BitConverter.ToUInt16(first, 4) == 0x15CE
                    && BitConverter.ToInt32(first, 6) == 1 && BitConverter.ToInt32(first, 10) == 100
                    && BitConverter.ToInt64(first, 14) >= before + 120, "Arb0402757 native user/package/expiry fields");
                h.Run("add_package 100 1"); h.Run("add_package 987654 120"); h.Run("add_package 100 500 offline");
                Hex.True(h.Main.Available == 0 && h.Instance.Available == 0, "no shortening, unknown package, or offline target mutation");
                h.Run("remove_package 100");
                Hex.Eq(h.Main.Frame(), Convert.FromHexString("0E000000CF150100000064000000"), "Arb0454073 remove frame");
                h.Instance.Frame(); h.Run("remove_package 100"); h.Main.Frame(); h.Instance.Frame();
                Hex.True(h.Store.GetAccountBenefits(1).Count == 0, "known absent package removal succeeds like native");
                h.Run("add_package 602 1"); h.Main.Frame(); h.Instance.Frame();
                Hex.True(h.Store.GetAccountBenefits(1).Single().ExpiresAt >= before + 7 * 86400L,
                    "Arb003 special restriction package has seven-day runtime expiry");
                h.Run("clear_package"); h.Main.Frame(); h.Instance.Frame();
                h.Run("set_account_res_level 1");
                Hex.Eq(h.Instance.Frame(), Convert.FromHexString("0E000000BC140100000001000000"), "native14BC currentWorld only");
                h.Run("set_account_res_level 2"); h.Instance.Frame(); h.Run("set_account_res_level 0"); h.Instance.Frame();
                byte[] enter = new byte[108]; BitConverter.GetBytes(1).CopyTo(enter, 32);
                QaGeneralCommands.StampEnterWorldFlags(enter, h.Store);
                Hex.True(enter[107] == 3 && h.Main.Available == 0, "OR1|2|0 persists in runtime AS_ENTER_WORLD field");
            });
            T181WithOperators(null, () =>
            {
                foreach (string name in QaAccountCommands.Names) h.Run(name + " 1");
                Hex.True(h.Main.Available == 0 && h.Instance.Available == 0 && h.Client.Available == 0,
                    "every local account/VIP name retains central authorization");
            });
        }
        finally { QaAccountSheet.Entry.UseBuiltIn(); VipSystemSheet.Entry.UseBuiltIn(); Directory.Delete(directory, true); }
    }

    [Test] public static void T201_VIP_enabled_commands_apply_sheet_XP_tokens_claims_and_native_reward_parcels()
    {
        string directory = T201VipSheets(true);
        try
        {
            using var h = new T201AccountHarness();
            T181WithOperators("t39", () =>
            {
                h.Run("set_vip_level 2"); h.VipNotification(); h.Client.Frame(); h.Client.Frame();
                Hex.True(h.Store.GetVipInfo(1).GameExp == 25 && h.Store.GetParcelsFor(1).Count == 2,
                    "sheet threshold25 grants one new parcel for each crossed level");
                var parcels = h.Store.GetParcelsFor(1).OrderBy(x => x.ParcelId).ToArray();
                for (int i = 0; i < parcels.Length; i++)
                {
                    byte[] record = h.Store.GetParcelRecord(parcels[i].ParcelId)!;
                    Hex.True(BitConverter.ToInt32(record, 0xD8 + 8) == 90001 + i
                        && BitConverter.ToInt32(record, 0xD8 + 12) == 1 && h.Store.CountParcelItems(parcels[i].ParcelId) == 1,
                        "Arb0656418: reward is VIPBenefit.lvUpToken x1, not LvUpMail generic template/amount");
                }
                h.Run("set_vip_level 0"); h.VipNotification(); h.Run("add_vip_game_exp 25"); h.VipNotification();
                Hex.True(h.Client.Available == 0 && h.Store.GetParcelsFor(1).Count == 2, "received-level ledger prevents duplicates");
                h.Run("clear_vip_reward_recv"); h.Run("set_vip_level 0"); h.VipNotification();
                h.Run("add_vip_pub_exp 10"); h.VipNotification();
                Hex.True(h.Store.GetVipInfo(1).PubExp == 10 && h.Store.GetParcelsFor(1).Count == 2,
                    "publisher XP changes effective level but does not run native level-up grants");
                h.Run("set_vip_level 2"); h.VipNotification(); h.Client.Frame();
                Hex.True(h.Store.GetVipInfo(1).GameExp == 15 && h.Store.GetParcelsFor(1).Count == 3,
                    "set level subtracts existing publisher XP; newly crossed level2 can be reclaimed after clear");
                h.Run("add_vip_token 7");
                Hex.Eq(h.Instance.Frame(), Convert.FromHexString("0E000000DD150100000007000000"), "native15DD carries delta not balance");
                h.Run("add_vip_token -8"); h.Run("add_vip_token 1 2");
                Hex.True(h.Store.GetVipInfo(1).TokenAmount == 7 && h.Instance.Available == 0, "negative tokens and wrong arg count rejected");
                h.Run("clear_vip_token_recv"); h.VipNotification();
                Hex.True(h.Store.GetVipDailyTokenReceived(1) > 0 && h.Store.GetVipInfo(1).LastResetTime == 0,
                    "daily receipt cleared independently from shop-reset timestamp");
                h.Run("viptest on"); Hex.Eq(h.Client.Frame(), Convert.FromHexString("0500195C01"), "Arb03911263 native toggle");
                h.Run("viptest off"); Hex.Eq(h.Client.Frame(), Convert.FromHexString("0500195C00"), "native off");
                h.Run("viptest ON"); Hex.True(h.Client.Available == 0 && h.Main.Available == 0, "literal on/off; no World0 leakage");
            });
        }
        finally { QaAccountSheet.Entry.UseBuiltIn(); VipSystemSheet.Entry.UseBuiltIn(); Directory.Delete(directory, true); }
    }

    [Test] public static void T201_VIP_disabled_gate_and_persistent_vs_runtime_account_state()
    {
        string directory = T201VipSheets(false);
        string database = Path.Combine(Path.GetTempPath(), "t201-vip-" + Guid.NewGuid() + ".db");
        try
        {
            using (var h = new T201AccountHarness())
                T181WithOperators("t39", () =>
                {
                    foreach (string command in new[] { "set_vip_level 2", "add_vip_game_exp 99", "add_vip_pub_exp 99", "add_vip_token 99" }) h.Run(command);
                    Hex.True(h.Store.GetVipInfo(1) == new CharacterStore.VipInfoRow(0, 0, 0, 0, 0)
                        && h.Instance.Available == 0 && h.Client.Available == 0, "retail vipSystemOn=false gates actual mutations");
                    h.Run("clear_vip_token_recv"); h.VipNotification();
                });
            using (var store = new CharacterStore(database, QuietLog()))
            {
                long account = store.GetOrCreateAccount("vip-persistence").Id;
                store.GrantAccountBenefit(account, 100, 123); store.AddQaPackage(account, 100, 456);
                store.AddQaVipPublisherExp(account, 7); store.SetQaVipGameExp(account, 25); store.AddQaVipTokens(account, 11);
                store.SetVipDailyTokenReceived(account, 789); Hex.True(store.MarkVipLevelReward(account, 2), "first reward claim");
                Hex.True(store.GetAccountBenefits(account).Single().ExpiresAt == 456 && store.GetVipInfo(account).PubExp == 7,
                    "live package and publisher-XP overlays are effective");
            }
            using (var reopened = new CharacterStore(database, QuietLog()))
            {
                Hex.True(reopened.GetAccountBenefits(1).Single().ExpiresAt == 123 && reopened.GetVipInfo(1)
                    == new CharacterStore.VipInfoRow(0, 25, 11, 0, 0), "native RAM-only package/pub XP do not overwrite DB baseline");
                Hex.True(reopened.GetVipDailyTokenReceived(1) == 789 && !reopened.MarkVipLevelReward(1, 2), "daily date and claimed levels survive restart");
                reopened.ClearVipLevelRewards(1); Hex.True(reopened.MarkVipLevelReward(1, 2), "QA clears persisted claim ledger");
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools(); File.Delete(database);
            QaAccountSheet.Entry.UseBuiltIn(); VipSystemSheet.Entry.UseBuiltIn(); Directory.Delete(directory, true);
        }
    }

    [Test] public static void T201_VIP_notification_layout_reset_times_and_exhausted_dungeon_are_native()
    {
        string directory = T201VipSheets(true);
        try
        {
            var sheet = VipSystemSheet.Entry.Value;
            long now = new DateTimeOffset(new DateTime(2030, 1, 10, 10, 15, 0, DateTimeKind.Local)).ToUnixTimeSeconds();
            Hex.Eq(QaAccountCommands.BuildVipInfo(sheet, new CharacterStore.VipInfoRow(30, -5, 7, 0, 0), true, now, 123),
                Convert.FromHexString("3700C27035000102000000FBFFFFFFFFFFFFFF1E000000000000000700000000000000012C970000000000007B00000000000000010000"),
                "Arb0653495-3511 full55B native S_VIP_INFO; signed XP, shop countdown, dungeon countdown, greeting");
            using var store = GuildStore(1);
            byte[] row = new byte[52]; BitConverter.GetBytes(77).CopyTo(row, 0);
            store.UpsertDungeonCoolTime(1, 77, row);
            Hex.True(QaAccountCommands.VipDungeonRemaining(store, 1, sheet, now) == 0, "unexhausted VIP dungeon has no reset countdown");
            BitConverter.GetBytes(1).CopyTo(row, 40); store.UpsertDungeonCoolTime(1, 77, row);
            Hex.True(QaAccountCommands.VipDungeonRemaining(store, 1, sheet, now) == 20 * 3600 + 45 * 60,
                "Arb06117461 exhausted dungeon counts down to loaded WorldData Dungeon.resetTime7");
            Hex.True(sheet.DailyBoundary(now) - now == 105 * 60 && sheet.NextShopReset(now) - now == 645 * 60,
                "daily and shop reset clocks remain independent");
        }
        finally { QaAccountSheet.Entry.UseBuiltIn(); VipSystemSheet.Entry.UseBuiltIn(); Directory.Delete(directory, true); }
    }
}
