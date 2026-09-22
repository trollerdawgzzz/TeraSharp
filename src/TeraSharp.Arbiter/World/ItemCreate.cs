// SPDX-License-Identifier: MIT
// Copyright (c) 2026 the TeraSharp contributors

using TeraSharp.Arbiter.Persistence;

namespace TeraSharp.Arbiter.World;

/// <summary>
/// T153 - atom op 8, the non-stackable item insert (GM makeitem, quest rewards, crafting gear).
///
/// <para><b>What it is.</b> Op 8 arrives with item DB id 0, the template at +0x18, the
/// destination triple at +0x38/+0x40/+0x48 and amount 1 at +0x50 - the same shape as op 7, the
/// stackable insert. The Arbiter's executor for it is DO_TS_INSERT_NONSTACKABLE_ITEM
/// (Arb_part_038.c:771; its own error text names EXECUTE_TS_INSERT_NONSTACKABLE_ITEM): it
/// allocates the id, writes it back at atom +0x10, and builds the item from the atom field by
/// field. Every live pair agrees (cap_social2 1794, cap_social3 960/1318, cap_social4 8611,
/// cap_final 5914/5974, cap_multiworld 1256/1702/10679/10902/10950): the reply is the request
/// with the id at +0x10 - plus, for some templates, a template-derived value at +0x104 and a
/// computed expiry at +0x1F0 for period items, both read from item template data we do not load
/// (they stay as World sent them).</para>
///
/// <para><b>The stored record.</b> The executor fills a local ItemData (the 536-byte record
/// 0x27A4 serves) from the atom; <see cref="Map"/> is that copy, record offset = the local's
/// distance from the struct base. Checked against the four created items the real Arbiter
/// later served back (cap_social2 10018, cap_social3 10030, cap_multiworld 10052/10053):
/// every mapped field matches, apart from a slot the item was since moved from and the heap
/// bytes past the custom string's NUL. So enchant, masterwork and the rest of what World put
/// in the atom survive a relog.</para>
/// </summary>
public static class ItemCreate
{
    public const int AtomSize = DbProxyHandlers.ItemAtomSize;          // 856
    public const int RecordSize = WarehouseHandlers.ItemRecordSize;    // 536

    /// <summary>(atom offset, record offset, bytes) - DO_TS_INSERT_NONSTACKABLE_ITEM's copy.</summary>
    public static readonly (int Atom, int Record, int Size)[] Map =
    {
        (0x010, 0x000, 4),  // item DB id, as allocated into the reply
        (0x018, 0x008, 4),  // template
        (0x038, 0x010, 8),  // owner (destination)
        (0x048, 0x024, 4),  // slot (destination)
        (0x05C, 0x028, 4),
        (0x058, 0x02C, 4),
        (0x25C, 0x030, 4),
        (0x260, 0x034, 1),  // bound flag (T151: RecordBoundFlag)
        (0x264, 0x03C, 4), (0x268, 0x040, 4), (0x26C, 0x044, 4), (0x270, 0x048, 4), (0x274, 0x04C, 4),
        (0x104, 0x134, 4),
        (0x128, 0x138, 1), (0x129, 0x139, 1),
        (0x108, 0x148, 8), (0x110, 0x150, 1), (0x120, 0x158, 8),
        (0x16C, 0x160, 4), (0x170, 0x164, 4), (0x178, 0x168, 4), (0x174, 0x16C, 4),
        (0x1D0, 0x170, 8),
        (0x1C0, 0x1C0, 4), (0x1C4, 0x1C4, 4),
        (0x1E0, 0x1CC, 1),
        (0x1F0, 0x1D0, 8), (0x1F8, 0x1D8, 8),  // a 16-byte timestamp: "never" or the period expiry
        (0x200, 0x1E0, 1),
        (0x234, 0x210, 1),
    };

    /// <summary>The custom item string: up to 32 UTF-16 chars at atom +0x17C, copied up to its
    /// NUL (wcsncpy_s, 0x21) to record +0x17C.</summary>
    public const int NameAtom = 0x17C, NameRecord = 0x17C, NameChars = 32;

    /// <summary>
    /// The record for one op-8 atom (the REPLY copy, id filled in): the synthetic base
    /// (<see cref="WarehouseHandlers.BuildItemRecord"/>) with the executor's fields laid over it.
    /// Null when the span is not a whole atom.
    /// </summary>
    public static byte[]? BuildRecord(ReadOnlySpan<byte> atom)
    {
        if (atom.Length < AtomSize) return null;
        int id = BitConverter.ToInt32(atom.Slice(WarehouseHandlers.AtomItemDbId, 4));
        int tmpl = BitConverter.ToInt32(atom.Slice(WarehouseHandlers.AtomTemplateId, 4));
        int owner = (int)BitConverter.ToInt64(atom.Slice(WarehouseHandlers.AtomDstOwner, 8));
        int pocket = BitConverter.ToInt32(atom.Slice(WarehouseHandlers.AtomDstInven, 4));
        int slot = BitConverter.ToInt32(atom.Slice(WarehouseHandlers.AtomDstSlot, 4));
        long delta = BitConverter.ToInt64(atom.Slice(WarehouseHandlers.AtomDelta, 8));
        var r = WarehouseHandlers.BuildItemRecord(id, tmpl, owner, delta > 0 ? (int)delta : 1, pocket, slot);
        foreach (var (a, rec, n) in Map) atom.Slice(a, n).CopyTo(r.AsSpan(rec, n));
        for (int i = 0; i < NameChars; i++)
        {
            int at = NameAtom + 2 * i;
            if (atom[at] == 0 && atom[at + 1] == 0) break;
            r[NameRecord + 2 * i] = atom[at];
            r[NameRecord + 2 * i + 1] = atom[at + 1];
        }
        return r;
    }

    /// <summary>
    /// After <see cref="WarehouseHandlers.Apply"/> has inserted the rows: give every op-8 row in
    /// the reply's 856-byte list at <paramref name="refOffset"/> its record. Returns how many.
    /// </summary>
    public static int StoreRecords(CharacterStore store, byte[] reply, int refOffset)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(reply);
        if (refOffset < 0 || (uint)refOffset + 8u > (uint)reply.Length) return 0;
        long start = (long)BitConverter.ToUInt32(reply, refOffset) - 6;
        long bytes = BitConverter.ToUInt32(reply, refOffset + 4);
        if (bytes == 0 || bytes % AtomSize != 0 || start < 0 || start + bytes > reply.Length) return 0;

        int stored = 0;
        for (long o = start; o < start + bytes; o += AtomSize)
        {
            var atom = reply.AsSpan((int)o, AtomSize);
            if (BitConverter.ToUInt32(atom.Slice(WarehouseHandlers.AtomOp, 4)) != WarehouseHandlers.TsInsertNonStackItem) continue;
            int id = BitConverter.ToInt32(atom.Slice(WarehouseHandlers.AtomItemDbId, 4));
            var row = id != 0 ? store.GetItem(id) : null;
            var rec = BuildRecord(atom);
            if (row is null || rec is null) continue;
            store.UpsertItem(row.ItemDbId, row.OwnerDbId, row.InvenType, row.Slot, row.TemplateId, row.Amount, rec);
            stored++;
        }
        return stored;
    }
}
