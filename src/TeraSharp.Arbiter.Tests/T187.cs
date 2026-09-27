// SPDX-License-Identifier: MIT
// Copyright (c) 2026 the TeraSharp contributors

using TeraSharp.Arbiter.World;

namespace TeraSharp.Arbiter.Tests;

/// <summary>
/// T187 - equipping persists. An equip is <c>SDB_EQUIP_ITEM</c> (0x2813) carrying a PAIR of op-36
/// atoms in list B, one per direction: the worn row moves to the bag slot the new item came from,
/// and the new row moves into the worn slot (inven 14). Reading op 36 as "swap whatever is at src
/// with whatever is at dst" applied the same swap twice, which put both rows back where they
/// started - so the next <c>SDB_USER_LOAD_INVENTORY</c> served the level-1 starter set again.
///
/// <para>Pinned to the real Arbiter (cap_final2b seq 4162/4163, GM equipping a Stormcry set on
/// character 1003) and to our own tap (cap_queue4 seq 5034, a non-GM equip of API-given gear on
/// character 9). Both carry exactly the same shape, which is why the echo is byte-exact and only
/// the apply had to change.</para>
/// </summary>
public static partial class Tests
{
    /// <summary>data/cap_t187.bin keyed by capture sequence number, or null with a note.</summary>
    static Dictionary<uint, byte[]>? LoadT187CaptureOrSkip() => LoadTsisOrSkip("cap_t187.bin");

    /// <summary>The (pocket, slot) of a stored row, or null when the item is not in the store.</summary>
    static (int Inven, int Slot)? T187Where(TeraSharp.Arbiter.Persistence.CharacterStore store, int itemDbId)
    {
        var row = store.GetItem(itemDbId);
        return row == null ? null : (row.InvenType, row.Slot);
    }

    [Test] public static void T187_equip_frames_are_echoed_byte_exact()
    {
        var cap = LoadT187CaptureOrSkip();
        if (cap == null) return;

        // Neither frame inserts, so no id may be allocated: the echo is the request with the
        // 0x2814 reply header. 4162 carries three atoms (the pair plus the bind-on-equip op 51),
        // 53820 carries the pair alone.
        var ids = new IdCounter(20000);
        Hex.Eq(DbProxyHandlers.BuildDbs2769(cap[4162], ids.Next), cap[4163],
               "DBS_EQUIP_ITEM (cap_final2b.log seq 4162 -> 4163)");
        Hex.Eq(DbProxyHandlers.BuildDbs2769(cap[53820], ids.Next), cap[53821],
               "DBS_EQUIP_ITEM (cap_final2b.log seq 53820 -> 53821)");
        Hex.True(ids.Calls == 0, $"an equip allocates no item id, got {ids.Calls}");
    }

    [Test] public static void T187_the_atom_pair_carries_the_worn_slot()
    {
        var cap = LoadT187CaptureOrSkip();
        if (cap == null) return;

        // What the destination actually is: list B (the pair at payload offset 8), op 36, and the
        // triple (owner, inven 14, slot) on the atom that names the item being put on.
        var atoms = WarehouseHandlers.ParseAtoms(cap[4162], 8);
        Hex.True(WarehouseHandlers.ParseAtoms(cap[4162], 0).Count == 0, "list A is empty on an equip");
        Hex.True(atoms.Count == 3, $"seq 4162 carries three atoms, got {atoms.Count}");

        var off = atoms[0];   // the worn row leaving slot 14:1
        var on = atoms[1];    // the new row arriving in it
        Hex.True(off.Op == WarehouseHandlers.TsSwapItemPos && on.Op == WarehouseHandlers.TsSwapItemPos,
                 $"both halves are op 36, got {off.Op} and {on.Op}");
        Hex.True(off.SrcInven == BagItems.EquippedPocket && off.SrcSlot == 1
                 && off.DstInven == BagItems.Pocket && off.DstSlot == 9,
                 $"item {off.ItemDbId} leaves 14:1 for the bag, got {off.SrcInven}:{off.SrcSlot} -> {off.DstInven}:{off.DstSlot}");
        Hex.True(on.SrcInven == BagItems.Pocket && on.SrcSlot == 9
                 && on.DstInven == BagItems.EquippedPocket && on.DstSlot == 1,
                 $"item {on.ItemDbId} goes into 14:1, got {on.SrcInven}:{on.SrcSlot} -> {on.DstInven}:{on.DstSlot}");
        Hex.True(atoms[2].Op == WarehouseHandlers.TsBindItem && atoms[2].ItemDbId == on.ItemDbId
                 && atoms[2].SrcInven == atoms[2].DstInven && atoms[2].SrcSlot == atoms[2].DstSlot,
                 "and op 51 binds the item being worn, without moving it (T151)");
        Hex.True(off.ItemDbId == 10011 && on.ItemDbId == 10073 && on.TemplateId == 88384,
                 $"the capture's ids: 10011 out, 10073 (88384) in, got {off.ItemDbId} and {on.ItemDbId}");
    }

    [Test] public static void T187_a_real_equip_moves_the_row_into_the_worn_slot()
    {
        var cap = LoadT187CaptureOrSkip();
        if (cap == null) return;
        string dir = T37TempDir();
        try
        {
            using var store = new TeraSharp.Arbiter.Persistence.CharacterStore(
                Path.Combine(dir, "equip.db"), QuietLog());
            // The rows cap_final2b's character 1003 had when the GM pressed equip.
            store.UpsertItem(10011, 1003, BagItems.EquippedPocket, 1, 59053, 1);
            store.UpsertItem(10073, 1003, BagItems.Pocket, 9, 88384, 1);

            RunHandler1(DbProxyHandlers.SDB_EQUIP_ITEM, cap[4162], store);

            Hex.True(T187Where(store, 10073) == (BagItems.EquippedPocket, 1),
                     $"the new weapon is worn, not in the bag: {T187Where(store, 10073)}");
            Hex.True(T187Where(store, 10011) == (BagItems.Pocket, 9),
                     $"and the old one took its bag slot: {T187Where(store, 10011)}");
            Hex.True(store.CountInventoryItems(1003) == 2, "two rows, nothing invented");

            // World repeats the whole frame when the client retries. The second apply must not
            // put the rows back - that is the bug this task is about.
            RunHandler1(DbProxyHandlers.SDB_EQUIP_ITEM, cap[4162], store);
            Hex.True(T187Where(store, 10073) == (BagItems.EquippedPocket, 1)
                     && T187Where(store, 10011) == (BagItems.Pocket, 9),
                     "applying the same equip twice is the same end state");
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(dir, true); }
    }

    [Test] public static void T187_our_own_equip_survives_a_relog()
    {
        var cap = LoadT187CaptureOrSkip();
        if (cap == null) return;
        string dir = T37TempDir();
        try
        {
            using var store = new TeraSharp.Arbiter.Persistence.CharacterStore(
                Path.Combine(dir, "relog-equip.db"), QuietLog());
            // cap_queue4 seq 5034: character 9 equips item 1070 (template 88384) over the starter
            // weapon 1058 (59053), which is in 14:1.
            store.UpsertItem(1058, 9, BagItems.EquippedPocket, 1, 59053, 1);
            store.UpsertItem(1070, 9, BagItems.Pocket, 2, 88384, 1);

            RunHandler1(DbProxyHandlers.SDB_EQUIP_ITEM, cap[5034], store);

            // The relog: DBS_USER_LOAD_INVENTORY is rebuilt from the rows, in (pocket, slot)
            // order, and that is what World hands the client.
            var rows = store.GetInventoryItems(9);
            var payload = BagItems.BuildPayload(rows, reqId: 1, ownerId: 9);
            var served = BagItems.SplitPayload(payload, 9);

            Hex.True(served.Count == 2, $"two rows served, got {served.Count}");
            var worn = served.Single(r => r.InvenType == BagItems.EquippedPocket);
            Hex.True(worn.ItemDbId == 1070 && worn.TemplateId == 88384 && worn.Slot == 1,
                     $"the load serves the equipped weapon in 14:1, got item {worn.ItemDbId} ({worn.TemplateId}) at {worn.InvenType}:{worn.Slot}");
            var bagged = served.Single(r => r.InvenType == BagItems.Pocket);
            Hex.True(bagged.ItemDbId == 1058 && bagged.Slot == 2,
                     $"and the starter weapon is in the bag slot it swapped with, got {bagged.ItemDbId} at {bagged.InvenType}:{bagged.Slot}");
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(dir, true); }
    }

    [Test] public static void T187_unequip_into_an_empty_slot_moves_the_row_back()
    {
        string dir = T37TempDir();
        try
        {
            using var store = new TeraSharp.Arbiter.Persistence.CharacterStore(
                Path.Combine(dir, "unequip.db"), QuietLog());
            store.UpsertItem(1070, 9, BagItems.EquippedPocket, 1, 88384, 1);

            // No capture holds an unequip into an EMPTY bag slot - every equip in cap_final2b and
            // cap_queue4 swapped with the worn item. The half-pair shape is the same op 36 with
            // nothing at the destination, and it has to move the row rather than do nothing.
            T44Apply(store, T44AtomPayload(DbProxyHandlers.ItemSingleRequestHeader,
                (WarehouseHandlers.TsSwapItemPos, 1070, 88384, 9, (uint)BagItems.EquippedPocket, 1u,
                 9, BagItems.Pocket, 7u, 0)));

            Hex.True(T187Where(store, 1070) == (BagItems.Pocket, 7),
                     $"the row left the worn slot: {T187Where(store, 1070)}");
            Hex.True(store.CountInventoryItems(9) == 1, "one row, still");
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(dir, true); }
    }
}
