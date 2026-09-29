// SPDX-License-Identifier: MIT
// Copyright (c) 2026 the TeraSharp contributors

using TeraSharp.Arbiter.Persistence;
using TeraSharp.Arbiter.World;

namespace TeraSharp.Arbiter.Tests;

// =============================================================================================
// T219b - the stored item record drifted away from its row.
//
// The live DB (D:\packetlogs\terasharp.db, 110 rows) disproves the brief's suspicion: no
// duplicate item_db_id, nothing at or above 65000000, nothing below FirstItemId, no record of the
// wrong length, and counters.item_id == max(item_db_id) == 1313, so the allocator is monotonic.
//
// What it does show is eight rows on character 9 - and one on character 10 - whose stored
// 536-byte record names a DIFFERENT pocket and slot than the row's own columns, in swapped pairs:
//
//     id 1058  row (pocket 0, slot 2)   record (pocket 14, slot 1)
//     id 1070  row (pocket 14, slot 1)  record (pocket 0,  slot 2)
//
// MoveItem only ever wrote the columns, and UpsertItem keeps the old blob when it is called with
// no record, so every swap left both records claiming the other item's place. Characters with no
// mismatch (16, fresh) are the ones the brief says still work.
// =============================================================================================
public static partial class Tests
{
    private static byte[] Rec(int id, int tpl, int owner, int amount, int pocket, int slot)
        => WarehouseHandlers.BuildItemRecord(id, tpl, owner, amount, pocket, slot);

    private static (int Pocket, int Slot, int Id, int Amount) RecPos(byte[] r)
        => (BitConverter.ToInt32(r, BagItems.RecordPocketOffset),
            BitConverter.ToInt32(r, BagItems.RecordSlotOffset),
            BitConverter.ToInt32(r, BagItems.RecordIdOffset),
            BitConverter.ToInt32(r, BagItems.RecordAmountOffset));

    /// <summary>
    /// T219b. A move re-stamps the stored record, so the swapped pair the live DB shows cannot
    /// happen: both records name the pocket and slot their row names.
    /// </summary>
    [Test] public static void T219b_move_restamps_the_stored_record()
    {
        using var store = new CharacterStore(":memory:", QuietLog());
        store.UpsertItem(1058, 9, 0, 2, 59053, 1, Rec(1058, 59053, 9, 1, 0, 2));
        store.UpsertItem(1070, 9, 14, 1, 88384, 1, Rec(1070, 88384, 9, 1, 14, 1));

        // The swap the live bag went through: equip 1058, unequip 1070 into the bag slot it left.
        Hex.True(store.MoveItem(1058, 9, 14, 1), "1058 moves into the equipment slot");
        Hex.True(store.MoveItem(1070, 9, 0, 2), "1070 moves into the bag slot");

        var a = store.GetItem(1058)!; var b = store.GetItem(1070)!;
        Hex.True(RecPos(a.Record!) == (14, 1, 1058, 1), $"1058's record follows its row, got {RecPos(a.Record!)}");
        Hex.True(RecPos(b.Record!) == (0, 2, 1070, 1), $"1070's record follows its row, got {RecPos(b.Record!)}");
    }

    /// <summary>
    /// T219b. An upsert with no record - the shape every amount change and every atom-driven move
    /// uses - re-stamps too, rather than keeping the position and count the blob was created with.
    /// </summary>
    [Test] public static void T219b_recordless_upsert_restamps()
    {
        using var store = new CharacterStore(":memory:", QuietLog());
        store.UpsertItem(1056, 9, 0, 0, 6550, 20, Rec(1056, 6550, 9, 20, 0, 0));
        store.UpsertItem(1056, 9, 0, 5, 6550, 17);           // three drunk, moved to slot 5

        var r = store.GetItem(1056)!.Record!;
        Hex.True(RecPos(r) == (0, 5, 1056, 17), $"the record carries the new slot and amount, got {RecPos(r)}");
    }

    /// <summary>
    /// T219b. The repair pass fixes rows written before the re-stamp landed, counts only the rows
    /// it changed, and leaves a record-less row alone - BagItems.BuildPayload synthesises those,
    /// and the synthesised record already agrees with the row.
    /// </summary>
    [Test] public static void T219b_repair_restamps_only_the_drifted_rows()
    {
        using var store = new CharacterStore(":memory:", QuietLog());
        // Stale: the record still says (14, 1) while the row says (0, 2). Written with the record
        // in one call, so nothing re-stamps it - exactly how the live rows got there.
        store.UpsertItem(1058, 9, 0, 2, 59053, 1, Rec(1058, 59053, 9, 1, 14, 1));
        store.UpsertItem(1059, 9, 0, 3, 15004, 1, Rec(1059, 15004, 9, 1, 0, 3));   // already right
        store.UpsertItem(1295, 9, 0, 15, 9368, 4);                                  // no record

        var r = store.RepairItemRecords(9);
        Hex.True(r.Scanned == 3, $"three rows scanned, got {r.Scanned}");
        Hex.True(r.Restamped == 1, $"one row drifted, got {r.Restamped}");
        Hex.True(r.NoRecord == 1, $"one row has no record, got {r.NoRecord}");
        Hex.True(RecPos(store.GetItem(1058)!.Record!) == (0, 2, 1058, 1), "1058 now names its row's slot");
        Hex.True(store.GetItem(1295)!.Record is null, "the record-less row is left record-less");

        var again = store.RepairItemRecords(9);
        Hex.True(again.Restamped == 0, "the pass is idempotent");
    }

    /// <summary>
    /// T219b. A synthesised record and a stored one differ only in the uninitialised tail: the
    /// live DB's stored records carry nothing at 12, 14 or 54 but UTF-16 SQL text, so a row with
    /// record = NULL is NOT what World refuses. Named so the next brief does not retry it.
    /// </summary>
    [Test] public static void T219b_synthesised_record_matches_a_stored_one_in_every_named_field()
    {
        var synth = Rec(1295, 9368, 9, 4, 0, 15);
        Hex.True(synth.Length == WarehouseHandlers.ItemRecordSize, "536 bytes");
        Hex.True(RecPos(synth) == (0, 15, 1295, 4), "id, amount, pocket and slot are all set");
        Hex.True(BitConverter.ToInt32(synth, 8) == 9368, "template id at 8");
        Hex.True(BitConverter.ToInt32(synth, 16) == 9, "owner at 16");
        Hex.True(BitConverter.ToInt32(synth, 44) == 30, "the constant every captured record carries at 44");
    }
}
