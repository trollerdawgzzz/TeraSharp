// SPDX-License-Identifier: MIT
// Copyright (c) 2026 the TeraSharp contributors

using System.Text;
using TeraSharp.Arbiter.Handlers;
using TeraSharp.Arbiter.Persistence;
using TeraSharp.Arbiter.World;

namespace TeraSharp.Arbiter.Tests;

public static partial class Tests
{
    private static void T201StyleItems(T201UtilityEnvironment e)
    {
        File.WriteAllText(Path.Combine(e.DirectoryPath, "EnchantData.xml"), "<EnchantData normalMaxCount='12' masterpieceMaxCount='15'/>");
        File.WriteAllText(Path.Combine(e.DirectoryPath, "ItemTemplate.xml"), "<ItemTemplate><Item id='500' combatItemType='EQUIP_STYLE_BODY'/><Item id='501' combatItemType='SKILLBOOK'/><Item id='502' combatItemType='EQUIP_STYLE_BACK'/><Item id='503' combatItemType='DISPOSAL'/><Item id='504' combatItemType='CHANGE_LOOKS_PREMIUM'/></ItemTemplate>");
        QaItemSheet.Entry.Load(e.DirectoryPath);
    }
    [Test] public static void T201_styleshop_add_operator_validation_timer_all_World_updates_and_expiry()
    {
        using var e = new T201UtilityEnvironment(); T201StyleItems(e); e.TargetOnline();
        var now = DateTimeOffset.FromUnixTimeSeconds(1800000000); QaStyleShopCommands.Clock = () => now;
        try
        {
            T181WithOperators(null, () => e.Run("addstyleshop 500"));
            Hex.True(e.Store.GetStyleProducts().Count == 0 && e.Caller.Available == 0, "central authorization rejects before persisting products");
            T181WithOperators("utility-op", () =>
            {
                e.Run("addstyleshop 500");
                Hex.True(Encoding.Unicode.GetString(e.Caller.Frame()).Contains("StyleShopItem [tid:500] Inserted"), "native command success text");
                var row = e.Store.GetStyleProducts().Single();
                Hex.True(row is { Item: 500, Price: 10, SaleType: 2, MarkType: 0, Discount: 0, Label: "test by QA" }
                    && row.SaleStart == 1800000000 && row.SaleEnd == 1800086400 && row.PreviewStart == row.SaleStart, "native one-day defaults persist all SQL fields");
                Hex.True(e.Main.Available == 0 && e.Dungeon.Available == 0 && e.Target.Available == 0, "insertion itself does not start the visible overlay");
                e.Run("addstyleshop 500"); e.Run("addstyleshop 999"); e.Run("addstyleshop 503"); e.Run("addstyleshop 504");
                e.Run("addstyleshop 501 203001010000 202912310000 202912310000 P 10");
                e.Run("addstyleshop 501 203001010000 203001020000 203001010000 abcdefghijklmnop 10");
                Hex.True(e.Store.GetStyleProducts().Count == 1 && e.Caller.Available == 0, "duplicate, missing/nonstyle item, reversed dates and16-unit label silently refuse");
                QaStyleShopCommands.Tick(e.Store, e.Bridge, now.AddSeconds(29)); Hex.True(e.Main.Available == 0, "native30-second manager interval");
                QaStyleShopCommands.Tick(e.Store, e.Bridge, now.AddSeconds(30));
                var world = Convert.FromHexString("42000000B9152C0000000001000000F40100000000000000D2496B00000000000000000A000000000200000074006500730074002000620079002000510041000000");
                Hex.Eq(e.Main.Frame(), world, "decompile08119526 World update includes full label even during sale");
                Hex.Eq(e.Dungeon.Frame(), world, "04518750 is allWorld broadcast, includingWorld13");
                var client = Convert.FromHexString("250044AB2300F40100000000000000D2496B000000000A0000000000000000020000000000");
                Hex.Eq(e.Caller.Frame(), client, "decompile08119790 sale client uses empty label"); Hex.Eq(e.Target.Frame(), client, "connected-client update broadcast");
                QaStyleShopCommands.Tick(e.Store, e.Bridge, now.AddSeconds(60)); Hex.True(e.Caller.Available == 0 && e.Main.Available == 0, "unchanged display does not resend");
                QaStyleShopCommands.Tick(e.Store, e.Bridge, now.AddDays(1));
                var removed = Convert.FromHexString("2E000000B9152C0000000101000000F4010000000000000000000000000000000000000000000000000000000000");
                Hex.Eq(e.Main.Frame(), removed, "native expiry clears id/item's overlay; unused native saleType is deterministically zero"); Hex.Eq(e.Dungeon.Frame(), removed, "expiry reaches dungeonWorld");
                Hex.Eq(e.Caller.Frame(), Convert.FromHexString("0800B184F4010000"), "native client removal names item, not eventID"); e.Target.Frame();
                Hex.True(e.Store.GetStyleProducts().Count == 0, "expired product is deleted persistently");
                e.Run("addstyleshop 500 ignored"); e.Caller.Frame();
                Hex.True(e.Store.GetStyleProducts().Single().Id == 2, "native non-six-argument form defaults and IDs do not reuse within manager lifetime");
            });
        }
        finally { QaStyleShopCommands.ResetForTests(e.Store); }
    }

    [Test] public static void T201_styleshop_preview_sale_restart_reconnect_and_linked_snapshot_layout()
    {
        using var e = new T201UtilityEnvironment(); T201StyleItems(e);
        var now = new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero); QaStyleShopCommands.Clock = () => now;
        try
        {
            T181WithOperators("utility-op", () => e.Run("addstyleshop 500 203001010001 203001010002 203001010000 P -2")); e.Caller.Frame();
            var row = e.Store.GetStyleProducts().Single();
            Hex.True(row.SaleStart == 1893456060 && row.PreviewStart == 1893456000 && row.Price == -2, "explicit minute dates and saleType2 nonpositive price match native validation");
            var emptyPreview = row with { PreviewStart = 0 };
            Hex.True(QaStyleShopCommands.Display(emptyPreview, now.ToUnixTimeSeconds()) == 0
                && QaStyleShopCommands.BuildUpdate(emptyPreview, false, now.ToUnixTimeSeconds())[33] == 1,
                "native timer's empty-date gate and writer's raw timestamp comparison are intentionally distinct");
            QaStyleShopCommands.Tick(e.Store, e.Bridge, now.AddSeconds(30));
            var preview = Convert.FromHexString("270044AB2300F401000000000000BCD8DB7000000000FEFFFFFF01000000000200000050000000");
            Hex.Eq(e.Caller.Frame(), preview, "preview has label and preview flag");
            var world = Convert.FromHexString("30000000B9152C0000000001000000F401000000000000BCD8DB700000000000000000FEFFFFFF010200000050000000");
            Hex.Eq(e.Main.Frame(), world, "preview World update fields"); e.Dungeon.Frame();
            using (var reopened = new CharacterStore(Path.Combine(e.DirectoryPath, "store.db"), QuietLog()))
            {
                Hex.True(reopened.GetStyleProducts().Single() == row, "all twelve product columns survive restart");
                QaStyleShopCommands.ReplayWorld(reopened, e.Dungeon.Link, now.AddSeconds(31));
                Hex.Eq(e.Dungeon.Frame(), Convert.FromHexString("3F000000B815010000000E0000000E000000000000003B00000001000000F401000000000000BCD8DB700000000000000000FEFFFFFF010200000050000000"), "native15B8 45-byte linked row restores the requestedWorld13");
                Hex.True(e.Main.Available == 0 && e.Caller.Available == 0, "reconnect snapshot is requesting-link-only and no fabricated Arbiter login push");
                QaStyleShopCommands.ResetForTests(reopened);
            }
            QaStyleShopCommands.Tick(e.Store, e.Bridge, now.AddSeconds(60));
            Hex.Eq(e.Caller.Frame(), Convert.FromHexString("250044AB2300F401000000000000BCD8DB7000000000FEFFFFFF0000000000020000000000"), "sale transition clears preview flag and client label"); e.Main.Frame(); e.Dungeon.Frame();
            var second = row with { Id = 2, Item = 501, Label = "XY" };
            var list = T180Frame(0x15B8, QaStyleShopCommands.BuildSnapshot(new[] { row, second }, now.AddSeconds(61).ToUnixTimeSeconds()));
            Hex.True(BitConverter.ToInt32(list, 6) == 2 && BitConverter.ToInt32(list, 10) == 14 && BitConverter.ToInt32(list, 18) == 63
                && BitConverter.ToInt32(list, 63) == 63 && BitConverter.ToInt32(list, 67) == 0 && BitConverter.ToInt32(list, 71) == 108,
                "variable UTF16 strings preserve native full-frame self/next/string references across entries");
            QaStyleShopCommands.Tick(e.Store, e.Bridge, now.AddSeconds(120)); e.Caller.Frame(); e.Main.Frame(); e.Dungeon.Frame();
            QaStyleShopCommands.ReplayWorld(e.Store, e.Dungeon.Link, now.AddSeconds(121)); Hex.True(e.Dungeon.Available == 0, "no empty or expired product snapshot");
        }
        finally { QaStyleShopCommands.ResetForTests(e.Store); }
    }
}
