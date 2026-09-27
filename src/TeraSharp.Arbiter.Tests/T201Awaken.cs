// SPDX-License-Identifier: MIT
// Copyright (c) 2026 the TeraSharp contributors

using System.Text;
using TeraSharp.Arbiter.Handlers;
using TeraSharp.Arbiter.Persistence;
using TeraSharp.Arbiter.World;

namespace TeraSharp.Arbiter.Tests;

public static partial class Tests
{
    private static void T201AwakenItems(T201UtilityEnvironment e)
    {
        File.WriteAllText(Path.Combine(e.DirectoryPath, "EnchantData.xml"), "<EnchantData normalMaxCount='12' masterpieceMaxCount='15'/>");
        File.WriteAllText(Path.Combine(e.DirectoryPath, "ItemTemplate.xml"), "<ItemTemplate><Item id='500'/><Item id='501'/></ItemTemplate>");
        QaItemSheet.Entry.Load(e.DirectoryPath);
    }
    [Test] public static void T201_awaken_QA_add_delete_validation_and_expiry_use_native_client_and_World0_packets()
    {
        using var e = new T201UtilityEnvironment(); T201AwakenItems(e); e.TargetOnline();
        var now = DateTimeOffset.FromUnixTimeSeconds(1800000000); QaAwakenCommands.Clock = () => now;
        try
        {
            T181WithOperators(null, () =>
            {
                e.Run("addawakenchange 500 2 3"); e.Run("addawakenenchant 500 2 3 4 5 1");
                e.Run("deleteawakenchange 1"); e.Run("deleteawakenenchant 1");
            });
            Hex.True(e.Store.GetAwakenEvents(false).Count == 0 && e.Store.GetAwakenEvents(true).Count == 0 && e.Caller.Available == 0 && e.Main.Available == 0, "all four commands reject nonoperators before identities or frames");
            T181WithOperators("utility-op", () =>
            {
                e.Run("addawakenchange 500 2 3");
                byte[] change = Convert.FromHexString("1C0019D3010008000800000001000000F40100000200000003000000");
                Hex.Eq(e.Caller.Frame(), change, "Arb08218994 native20-byte client element"); Hex.Eq(e.Target.Frame(), change, "active connected users get same change overlay");
                Hex.True(Encoding.Unicode.GetString(e.Caller.Frame()).Contains("AddAwakenChange Success [eventId : 1]"), "native success includes persisted identity");
                Hex.Eq(e.Main.Frame(), Convert.FromHexString("26000000D515010000000E0000000E0000000000000001000000F40100000200000003000000"), "native24-byte World element goes toWorld0");
                Hex.True(e.Dungeon.Available == 0, "native Awaken manager does not broadcast toWorld13");
                e.Run("addawakenchange 500 4 5"); Hex.True(Encoding.Unicode.GetString(e.Caller.Frame()).Contains("Fail") && e.Main.Available == 0, "same-item inclusive overlap rejected");
                e.Run("addawakenenchant 500 2 3 4 5 2"); Hex.True(Encoding.Unicode.GetString(e.Caller.Frame()).Contains("Fail"), "combat type2 is not native valid type");
                e.Run("addawakenchange 999 2 3"); Hex.True(Encoding.Unicode.GetString(e.Caller.Frame()).Contains("Fail"), "missing item rejected");
                e.Run("addawakenenchant 500 2 3 4 5 1");
                byte[] enchant = Convert.FromHexString("28006F9E010008000800000001000000040000000500000001000000F40100000200000003000000");
                Hex.Eq(e.Caller.Frame(), enchant, "six command args reordered into native32-byte enchant row"); Hex.Eq(e.Target.Frame(), enchant, "enchant user broadcast"); e.Caller.Frame();
                Hex.Eq(e.Main.Frame(), Convert.FromHexString("32000000D415010000000E0000000E0000000000000001000000040000000500000001000000F40100000200000003000000"), "native36-byte World enchant element");
                e.Run("deleteawakenchange 1"); Hex.Eq(e.Caller.Frame(), Convert.FromHexString("08006AE801000000"), "active change deleteclient"); e.Target.Frame(); e.Caller.Frame();
                Hex.Eq(e.Main.Frame(), Convert.FromHexString("0A000000D71501000000"), "change deleteWorld0");
                e.Run("deleteawakenenchant 1"); Hex.Eq(e.Caller.Frame(), Convert.FromHexString("0800215601000000"), "active enchant deleteclient"); e.Target.Frame(); e.Caller.Frame();
                Hex.Eq(e.Main.Frame(), Convert.FromHexString("0A000000D61501000000"), "enchant deleteWorld0");
                e.Run("addawakenchange 501 1 1"); e.Caller.Frame(); e.Caller.Frame(); e.Target.Frame(); e.Main.Frame();
                QaAwakenCommands.Tick(e.Store, e.Bridge, now.AddDays(1));
                Hex.Eq(e.Caller.Frame(), Convert.FromHexString("08006AE802000000"), "native expiry end<=now removes event2"); e.Target.Frame();
                Hex.Eq(e.Main.Frame(), Convert.FromHexString("0A000000D71502000000"), "expiry clears World overlay");
                Hex.True(e.Store.GetAwakenEvents(false).Count == 0 && e.Store.GetAwakenEvents(true).Count == 0, "expiry and explicitdelete remove SQL rows");
            });
        }
        finally { QaAwakenCommands.ResetForTests(e.Store); QaItemSheet.Entry.Load(HandshakeData.DatasheetDirectory()); }
    }

    [Test] public static void T201_awaken_pending_restart_reconnect_and_client_gate_preserve_native_lifecycle()
    {
        using var e = new T201UtilityEnvironment(); T201AwakenItems(e);
        var now = DateTimeOffset.FromUnixTimeSeconds(1800000000); QaAwakenCommands.Clock = () => now;
        try
        {
            var pending = e.Store.AddAwakenEvent(false, new[] { 500, 1, 2 }, now.ToUnixTimeSeconds() + 120, now.ToUnixTimeSeconds() + 240)!;
            Hex.True(e.Store.AddAwakenEvent(false, new[] { 500, 9, 9 }, pending.End, pending.End + 120) == null, "inclusive overlap rejects touching ranges");
            T181WithOperators("utility-op", () => e.Run("deleteawakenchange " + pending.Id));
            Hex.True(Encoding.Unicode.GetString(e.Caller.Frame()).Contains("Success") && e.Main.Available == 0, "deleting pending reservation is SQL-only");
            pending = e.Store.AddAwakenEvent(false, new[] { 500, 1, 2 }, now.ToUnixTimeSeconds() + 120, now.ToUnixTimeSeconds() + 240)!;
            using var reopened = new CharacterStore(Path.Combine(e.DirectoryPath, "store.db"), QuietLog());
            Hex.True(reopened.GetAwakenEvents(false).Single().Id == pending.Id, "pending reservation survives store restart");
            QaAwakenCommands.Tick(reopened, e.Bridge, now); Hex.True(e.Caller.Available == 0 && e.Main.Available == 0, "future reservation remains silent");
            QaAwakenCommands.ClientDataDisabled = () => true;
            QaAwakenCommands.Tick(reopened, e.Bridge, now.AddSeconds(120)); var start = e.Main.Frame();
            Hex.True(e.Caller.Available == 0 && BitConverter.ToUInt16(start, 4) == 0x15D5, "ContentsOnOff11 gates client only, notWorld0");
            QaAwakenCommands.ReplayWorld(reopened, e.Dungeon.Link, now.AddSeconds(121)); Hex.True(e.Dungeon.Available == 0, "no replay to nonmainWorld");
            QaAwakenCommands.ReplayWorld(reopened, e.Main.Link, now.AddSeconds(121)); Hex.Eq(e.Main.Frame(), start, "World0 reconnect restores active persisted overlay");
            QaAwakenCommands.Tick(reopened, e.Bridge, now.AddSeconds(240));
            Hex.True(BitConverter.ToUInt16(e.Main.Frame(), 4) == 0x15D7 && e.Caller.Available == 0 && reopened.GetAwakenEvents(false).Count == 0, "gated expiry still clears World and SQL");
            QaAwakenCommands.ResetForTests(reopened);
        }
        finally { QaAwakenCommands.ResetForTests(e.Store); QaItemSheet.Entry.Load(HandshakeData.DatasheetDirectory()); }
    }
}
