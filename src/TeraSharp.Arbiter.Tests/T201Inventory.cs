// SPDX-License-Identifier: MIT
// Copyright (c) 2026 the TeraSharp contributors

using TeraSharp.Arbiter.Game;
using TeraSharp.Arbiter.Handlers;
using TeraSharp.Arbiter.Persistence;
using TeraSharp.Arbiter.Protocol;
using TeraSharp.Arbiter.World;

namespace TeraSharp.Arbiter.Tests;

public static partial class Tests
{
    [Test] public static void T201_makeitem_mprate_uses_sheet_indices_in_created_atom_and_record()
    {
        using var h = new T201AccountHarness();
        string directory = Path.Combine(Path.GetTempPath(), "t201-rate-" + Guid.NewGuid()); Directory.CreateDirectory(directory);
        QaInventoryCommands.ResetForTests();
        try
        {
            File.WriteAllText(Path.Combine(directory, "ItemTemplate.xml"), "<ItemData><Item id='7' masterpieceBasicStatRevise='1;1.01;1.01;1.02;0;1.03'/></ItemData>");
            File.WriteAllText(Path.Combine(directory, "EnchantData.xml"), "<EnchantData normalMaxCount='0' masterpieceMaxCount='0'/>");
            Hex.True(QaItemSheet.Entry.Load(directory).FromSheet, "masterwork multiplier list uses supplied sheet");
            var atom = new byte[ItemCreate.AtomSize]; BitConverter.GetBytes(8).CopyTo(atom, 4);
            BitConverter.GetBytes(20).CopyTo(atom, 0x10); BitConverter.GetBytes(7).CopyTo(atom, 0x18);
            BitConverter.GetBytes(1L).CopyTo(atom, 0x38); BitConverter.GetBytes(1L).CopyTo(atom, 0x50); atom[0x128] = 1;
            h.Store.UpsertItem(20, 1, 0, 0, 7, 1, null);
            byte[] reply = BitConverter.GetBytes(14).Concat(BitConverter.GetBytes(atom.Length)).Concat(atom).ToArray();
            void Run(string command) => QaInventoryCommands.TryExecute(h.Client.Session, h.Store, GmCommandParser.Parse(command)!, QuietLog());
            Run("makeitem_mprate 1.03"); ItemCreate.StoreRecords(h.Store, reply, 0);
            Hex.Eq(reply.AsSpan(8 + 0xE0, 8).ToArray(), Convert.FromHexString("0300000003000000"), "Arb009 filter+index, Arb038 matched override writes both indices");
            Hex.Eq(h.Store.GetItem(20)!.Record!.AsSpan(0x13C, 8).ToArray(), Convert.FromHexString("0300000003000000"), "both stat-rate indices survive item reload");
            Run("makeitem_mprate 1.01"); reply[8 + 0xE8] = 1; ItemCreate.StoreRecords(h.Store, reply, 0);
            Hex.True(BitConverter.ToInt32(reply, 8 + 0xE0) == 3, "explicit World rate suppresses QA override");
            reply[8 + 0xE8] = 0; Run("makeitem_mprate off"); ItemCreate.StoreRecords(h.Store, reply, 0);
            Hex.True(BitConverter.ToInt32(reply, 8 + 0xE0) == 3 && h.Client.Available == 0 && h.Main.Available == 0 && h.Instance.Available == 0,
                "off restores existing normal creation path without an immediate frame");
        }
        finally { QaInventoryCommands.ResetForTests(); QaItemSheet.Entry.UseBuiltIn(); Directory.Delete(directory, true); }
    }

    [Test] public static void T201_inventory_QA_resets_owned_storage_and_preserves_unrelated_data()
    {
        using var env = new T185Environment(null);
        using var store = StoreWithTwoAccounts();
        string map = Path.GetTempFileName(); QaInventoryCommands.ResetForTests();
        try
        {
            File.WriteAllText(map, "{\"maps\":{\"376012\":{\"S_SYSTEM_MESSAGE_CUSTOM\":39244}}}");
            var defs = new DefinitionRegistry(QuietLog()); defs.RegisterFromDef("S_SYSTEM_MESSAGE_CUSTOM", "ref formatted\nstring formatted\n");
            var bridge = new WorldBridge(WorldReplayTable.Load("/nonexistent", QuietLog()), QuietLog());
            using var main = new T192WorldPeer(bridge, 1, 0); using var owner = new T192WorldPeer(bridge, 13, 13);
            using var client = new T185Client(defs, OpcodeTable.LoadFromFile(map, "376012"), QuietLog());
            T185Environment.SetWorld(bridge); var s = client.Session;
            s.PlayerId = 1; s.SelectedCharacter = new FakeCharacter { Id = 1 }; s.CurrentWorldId = 13; s.EnterWorld();
            void Run(string text) => QaInventoryCommands.TryExecute(s, store, GmCommandParser.Parse(text)!, QuietLog());
            Run("masterpiece on"); Hex.True(QaInventoryCommands.NativeMasterpieceFlag, "native obsolete flag sets on");
            Run("masterpiece OFF"); Hex.True(QaInventoryCommands.NativeMasterpieceFlag, "native flag requires literal lowercase token");
            Run("masterpiece off"); Hex.True(!QaInventoryCommands.NativeMasterpieceFlag && client.Available == 0
                && main.Available == 0 && owner.Available == 0, "PE flag has no consumer; do not invent a forced masterwork effect");
            Run("ware_duration 61 ignored"); Hex.True(QaInventoryCommands.CurrentCommissionDurations == new QaInventoryCommands.CommissionDurations(61, 61),
                "native duration writes both account/guild legacy fields");
            Run("ware_duration 60"); Hex.True(QaInventoryCommands.CurrentCommissionDurations.Account == 61,
                "native duration requires strictly greater than60; commission remains unconditionally paid in this build");
            store.LearnItemRecipe(1, 321, false, 123); store.LearnItemRecipe(2, 456, false, 234);
            Run("clear_recipe");
            Hex.Eq(owner.Frame(), T201SocialWorld(0x2829, GmCommandHandlers.BuildWorldForward(1, 1, "clear_recipe_world")),
                "Arb040:8810–8840 clears SQL first then native mode1 World command to current owner");
            Hex.True(store.GetItemRecipes(1).Count == 0 && store.GetItemRecipes(2).Count == 1, "recipe clear is user-scoped");
            Run("clear_recipe"); Hex.True(owner.Available == 0, "native empty-recipe case emits no World command");

            long account = store.AccountOf(1);
            store.AddWarehouseSlots(account, 1, 144); store.AddWarehouseMoney(account, 1, 777);
            store.UpsertItem(100, account, 1, 0, 1000, 3, null);
            store.UpsertItem(101, account, 12, 0, 1001, 2, null);
            Run("reset_ware_slot"); client.Frame();
            Hex.True(store.GetWarehouse(account, 1) == (777L, 0) && store.GetItem(100) != null,
                "native slot-only task5 preserves items/money, resets purchased slots");
            Run("reset_ware"); client.Frame();
            Hex.True(store.GetWarehouse(account, 1) == (0L, 0) && store.GetItem(100) == null && store.GetItem(101) != null,
                "native ClearWarehouse clears only account warehouse before page reset");
            Run("reset_styleware"); client.Frame(); Hex.True(store.GetItem(101) == null, "native style reset uses pocket12");
            Run("use_style_warehouse off"); client.Frame();
            Hex.Eq(main.Frame(), Convert.FromHexString("07000000B51300"), "native AS_USE_STYLE_WAREHOUSE(false) all Worlds");
            Hex.Eq(owner.Frame(), Convert.FromHexString("07000000B51300"), "dungeon World sees global toggle");
            Run("use_style_warehouse"); client.Frame();
            Hex.Eq(main.Frame(), Convert.FromHexString("07000000B51301"), "omitted native argument enables"); owner.Frame();
            Hex.True(QaInventoryCommands.StyleWarehouseOverride == true, "reconnecting World's handshake uses current override");
            Run("request_shop_url");
            Hex.Eq(client.Frame(), Convert.FromHexString("240058C5060074006700770069006B0069002F007200650064006D0069006E0065000000"),
                "Arb065:8521 no-UserEntityGw native fallback URL, not a fabricated publisher address");

            var atom = new byte[DbProxyHandlers.ItemAtomSize];
            BitConverter.GetBytes(8).CopyTo(atom, 4); BitConverter.GetBytes(200).CopyTo(atom, 0x10);
            BitConverter.GetBytes(1000).CopyTo(atom, 0x18); BitConverter.GetBytes(1L).CopyTo(atom, 0x38);
            BitConverter.GetBytes(1L).CopyTo(atom, 0x50); BitConverter.GetBytes(7).CopyTo(atom, 0x104);
            store.UpsertItem(200, 1, 0, 0, 1000, 1, null);
            byte[] reply = BitConverter.GetBytes(14).Concat(BitConverter.GetBytes(atom.Length)).Concat(atom).ToArray();
            Run("open_identify on"); ItemCreate.StoreRecords(store, reply, 0);
            Hex.True(BitConverter.ToInt32(reply, 8 + 0x104) == 0 && BitConverter.ToInt32(store.GetItem(200)!.Record!, 0x134) == 0,
                "Arb038:958/1162 clears unidentified grade in BOTH returned op8 and persisted536B record");
            BitConverter.GetBytes(7).CopyTo(reply, 8 + 0x104); Run("open_identify off"); ItemCreate.StoreRecords(store, reply, 0);
            Hex.True(BitConverter.ToInt32(reply, 8 + 0x104) == 7 && BitConverter.ToInt32(store.GetItem(200)!.Record!, 0x134) == 7,
                "unset QA flag preserves native/capture-supplied grade");
            foreach (string name in QaInventoryCommands.Names)
                Hex.True(GmCommandHandlers.Classify(true, 0, GmCommandParser.Parse(name)) == GmDispatch.NotAuthorised, "operator gate: " + name);
        }
        finally { T185Environment.SetWorld(null); QaInventoryCommands.ResetForTests(); File.Delete(map); }
    }
}
