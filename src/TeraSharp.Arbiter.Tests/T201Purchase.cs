// SPDX-License-Identifier: MIT
// Copyright (c) 2026 the TeraSharp contributors

using Microsoft.Data.Sqlite;
using TeraSharp.Arbiter.Handlers;
using TeraSharp.Arbiter.Persistence;
using TeraSharp.Arbiter.World;

namespace TeraSharp.Arbiter.Tests;

public static partial class Tests
{
    [Test] public static void T201_purchase_reset_commands_clear_real_account_and_world_limits()
    {
        using var h = new T201AccountHarness();
        string directory = Path.Combine(Path.GetTempPath(), "t201-purchase-" + Guid.NewGuid()); Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(Path.Combine(directory, "BuyMenuData.xml"), "<BuyMenuData><BuyMenu id='77'/><BuyMenu id='78'/></BuyMenuData>");
            QaPurchaseCommands.Menus.Load(directory);
            long account = h.Store.AccountOf(1);
            var update = new byte[32]; BitConverter.GetBytes(17).CopyTo(update, 0); BitConverter.GetBytes(1).CopyTo(update, 4);
            BitConverter.GetBytes(account).CopyTo(update, 8); BitConverter.GetBytes(77).CopyTo(update, 16);
            BitConverter.GetBytes(88).CopyTo(update, 20); BitConverter.GetBytes(7).CopyTo(update, 24); BitConverter.GetBytes(5).CopyTo(update, 28);
            var ack = RunHandler1(0x2983, update, h.Store);
            Hex.True(ack.op == 0x2984, "native purchase-write opcode"); Hex.Eq(ack.body, Convert.FromHexString("1100000001"), "unchanged native bare DLM/success ack");
            var load = RunHandler1(0x2981, Convert.FromHexString("1100000001000000"), h.Store);
            Hex.Eq(load.body, Convert.FromHexString("0100000013000000110000000113000000000000004D000000580000000700000005000000"),
                "Arb063:15640 native24B linked purchase row, account-scoped SQL load");
            var bridge = TeraSharp.Arbiter.Program.World!;
            bridge.DbProxy = new DbProxyHandlers(h.Store, QuietLog());
            bridge.HandleFrame(h.Main.Link, 0x162F, Convert.FromHexString("4D000000580000000700000003000000"));
            Hex.Eq(h.Main.Frame(), Convert.FromHexString("1600000030164D000000580000000700000003000000"), "native realm increment broadcasts AS1630 total"); h.Instance.Frame();
            h.Store.AddPurchaseLimit(account, 78, 88, 7, 9);
            T181WithOperators("t39", () => h.Run("reset_buymenu_limit 77"));
            Hex.Eq(h.Main.Frame(), Convert.FromHexString("0B00000031164D00000000"), "native reset without changing menu flag0"); h.Instance.Frame();
            Hex.True(h.Store.GetPurchaseLimits(0).Count == 0 && h.Store.GetPurchaseLimits(account).Single().BuyMenu == 78,
                "resets all account+realm counts only for requested menu");
            T181WithOperators("t39", () => h.Run("reset_buymenu 78"));
            Hex.Eq(h.Main.Frame(), Convert.FromHexString("0B00000031164E00000001"), "native reset/change-menu flag1"); h.Instance.Frame();
            var empty = RunHandler1(0x2981, Convert.FromHexString("1100000001000000"), h.Store);
            Hex.Eq(empty.body, DbProxyHandlers.BuildAchieveList(Convert.FromHexString("1100000001000000")), "empty load preserves pinned T168 retail shape");
            foreach (string name in QaPurchaseCommands.Names)
                Hex.True(GmCommandHandlers.Classify(true, 0, GmCommandParser.Parse(name)) == GmDispatch.NotAuthorised, "operator refusal: " + name);
            string database = Path.Combine(directory, "limits.db");
            using (var first = new CharacterStore(database, QuietLog())) first.AddPurchaseLimit(123, 77, 88, 7, 3);
            using (var reopened = new CharacterStore(database, QuietLog()))
                Hex.True(reopened.GetPurchaseLimits(123).Single() == new CharacterStore.PurchaseLimit(77, 88, 7, 3), "real counts survive store restart");
        }
        finally { QaPurchaseCommands.Menus.UseBuiltIn(); SqliteConnection.ClearAllPools(); Directory.Delete(directory, true); }
    }
}
