using Microsoft.Extensions.Logging;
using TeraSharp.Arbiter.Persistence;

namespace TeraSharp.Arbiter.World;

/// <summary>
/// The bag half of the item rows — T44. The bag (INVEN_TYPE 0) and the worn slots (14) live in
/// the same <c>items</c> table as the warehouse pockets that T42 created, keyed the same way, so
/// there is exactly one source of truth for where an item is.
///
/// <para>What changed: until T44, <c>DBS_USER_LOAD_INVENTORY</c> (0x27A4) was rebuilt from
/// <c>data/starter_inventory.bin</c> on every login and <c>SDB_ITEM_SINGLE</c> (0x2768) echoed
/// its atoms without storing anything, so every relog handed the character the same six starter
/// items no matter what they had done. Now the kit is written into rows the first time the
/// inventory is loaded, and the reply is rebuilt from those rows.</para>
///
/// <para><b>The seeded reply is byte-identical to the old one.</b> Each row keeps the exact
/// 536-byte <c>ItemData</c> record <see cref="StarterInventory"/> produced, and the rebuild
/// concatenates them in (pocket, slot) order — which is the order
/// <see cref="StarterInventory.Build"/> sorts them into and the order the capture lists them in.
/// That is what <c>T44_seeded_inventory_rebuilds_byte_identical</c> pins.</para>
///
/// <para>Why keep the whole record rather than re-synthesise it: about 40 of the 536 bytes are
/// named, ~400 are zero, and the rest is uninitialised Arbiter heap — the captured records carry
/// recognisable fragments of the real server's own SQL (status/INVENTORY-DESIGN.md section 2).
/// World ignores those bytes, so a synthesised record is safe for an item that never had one,
/// but keeping the original is what makes the starter reply byte-exact.</para>
/// </summary>
public static class BagItems
{
    /// <summary>INVEN_TYPE 0 — the main bag.</summary>
    public const int Pocket = 0;

    /// <summary>INVEN_TYPE 14 — the worn slots.</summary>
    public const int EquippedPocket = 14;

    /// <summary>0x27A4 payload header: <c>[u32 listOffset=19][u32 listBytes][u32 reqId][u8 0]</c>.</summary>
    public const int PayloadHeader = DbProxyHandlers.StarterInventoryItemStart;   // 13

    /// <summary>One <c>ItemData</c> record, the same 0x218 the warehouse view lists.</summary>
    public const int RecordSize = DbProxyHandlers.StarterInventoryItemSize;       // 536

    // Record field offsets, from status/INVENTORY-DESIGN.md section 2. StarterInventory owns the
    // same four; the owner offset lives on DbProxyHandlers because BuildStarterInventory walks it.
    public const int RecordIdOffset = 0;
    public const int RecordTemplateIdOffset = 8;
    public const int RecordOwnerOffset = DbProxyHandlers.StarterInventoryOwnerOffset;  // 16
    public const int RecordAmountOffset = 24;
    public const int RecordPocketOffset = 28;
    public const int RecordSlotOffset = 36;

    /// <summary>
    /// Split a 0x27A4 payload into rows, keeping each 536-byte record verbatim. Returns an empty
    /// list for a payload that is not a whole number of records — a malformed seed must not
    /// produce half an inventory.
    /// </summary>
    public static IReadOnlyList<CharacterStore.ItemRow> SplitPayload(byte[] payload, long ownerDbId)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (payload.Length < PayloadHeader) return Array.Empty<CharacterStore.ItemRow>();
        int body = payload.Length - PayloadHeader;
        if (body <= 0 || body % RecordSize != 0) return Array.Empty<CharacterStore.ItemRow>();

        int n = body / RecordSize;
        var rows = new CharacterStore.ItemRow[n];
        for (int i = 0; i < n; i++)
        {
            int at = PayloadHeader + i * RecordSize;
            var record = new byte[RecordSize];
            Array.Copy(payload, at, record, 0, RecordSize);
            rows[i] = new CharacterStore.ItemRow(
                ItemDbId: BitConverter.ToInt32(record, RecordIdOffset),
                OwnerDbId: ownerDbId,
                InvenType: BitConverter.ToInt32(record, RecordPocketOffset),
                Slot: BitConverter.ToInt32(record, RecordSlotOffset),
                TemplateId: BitConverter.ToInt32(record, RecordTemplateIdOffset),
                Amount: BitConverter.ToInt32(record, RecordAmountOffset),
                Record: record);
        }
        return rows;
    }

    /// <summary>
    /// Rebuild the 0x27A4 payload from stored rows. <paramref name="rows"/> must already be in
    /// (pocket, slot) order — <see cref="CharacterStore.GetInventoryItems"/> returns them that
    /// way. A row with no stored record gets a synthesised one, which is what every item that
    /// arrived through a transaction atom rather than the starter kit has.
    /// </summary>
    public static byte[] BuildPayload(IReadOnlyList<CharacterStore.ItemRow> rows, uint reqId, int ownerId)
    {
        rows ??= Array.Empty<CharacterStore.ItemRow>();
        var payload = new byte[PayloadHeader + rows.Count * RecordSize];
        BitConverter.GetBytes((uint)(6 + PayloadHeader)).CopyTo(payload, 0);      // 19, frame-relative
        BitConverter.GetBytes((uint)(rows.Count * RecordSize)).CopyTo(payload, 4);
        BitConverter.GetBytes(reqId).CopyTo(payload, 8);
        payload[12] = 0;                                                          // flag: 0 in the capture

        for (int i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            int at = PayloadHeader + i * RecordSize;
            byte[]? stored = row.Record;
            if (stored is not null && stored.Length == RecordSize)
                stored.CopyTo(payload, at);
            else
                WarehouseHandlers.BuildItemRecord(row.ItemDbId, row.TemplateId, ownerId,
                                                  (int)row.Amount, row.InvenType, row.Slot)
                                 .CopyTo(payload, at);

            // The owner and the DLM id are the two fields World checks hardest: a mismatched
            // owner is answered with SA_ENTER_WORLD_FAILED. Patch it even on a stored record,
            // so a row that was seeded for one playerId can never be served under another.
            BitConverter.GetBytes(ownerId).CopyTo(payload, at + RecordOwnerOffset);
        }
        return payload;
    }

    /// <summary>
    /// Write a character's starter kit into rows, replacing anything that was there. Called once,
    /// the first time the inventory is loaded for a character with no rows. Returns the number of
    /// rows written.
    /// </summary>
    public static int Seed(CharacterStore store, int ownerId, byte[] payload)
    {
        ArgumentNullException.ThrowIfNull(store);
        var rows = SplitPayload(payload, ownerId);
        if (rows.Count == 0) return 0;
        store.ReplaceInventory(ownerId, rows);
        return rows.Count;
    }

    /// <summary>
    /// Apply the atoms carried by a reply we just built, so the rows and the ids World is given
    /// agree. Always the reply and never the request: the reply is the copy that has the
    /// allocated item DB ids filled in, and parsing the request instead would allocate a second
    /// set and store ids World never saw.
    ///
    /// <para><paramref name="refOffset"/> is the payload index of the reply's
    /// <c>[u32 frame-relative offset][u32 byte count]</c> pair: 0 and 8 for the two lists in
    /// DBS_ITEM_SINGLE (0x2769), 8 for DBS_SET_QUEST_INFO (0x272F), 0 for DBS_USER_LEARN_SKILL
    /// (0x278F).</para>
    /// </summary>
    public static WarehouseHandlers.ApplyResult ApplyReplyAtoms(
        CharacterStore store, byte[] reply, int refOffset, Func<int> allocateItemId, ILogger? log = null,
        int recordSize = 0)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(reply);
        var atoms = WarehouseHandlers.ParseAtoms(reply, refOffset, recordSize);
        return WarehouseHandlers.Apply(store, atoms, allocateItemId, log);
    }
}
