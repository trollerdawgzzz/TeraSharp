// SPDX-License-Identifier: MIT
// Copyright (c) 2026 the TeraSharp contributors

using Microsoft.Extensions.Logging;
using TeraSharp.Arbiter.Persistence;

namespace TeraSharp.Arbiter.World;

/// <summary>
/// T166 - the item-record edits behind enchanting, awakening, identify and option reset.
///
/// <para><b>The Arbiter never rolls.</b> SDB_ITEM_ENCHANT, _ENCHANT_IDENTIFY, SDB_ENCHANT_ITEM_BOOST,
/// SDB_ITEM_AWAKEN, SDB_EQUIPMENT_INHERITANCE, SDB_ITEM_UNBIND, SDB_ITEM_UNIDENTIFY, SDB_ITEM_EXTRACT
/// and SDB_ITEM_DECOMPOSITION are one generic handler each on the real Arbiter
/// (ArbiterServer.exe.c:1266212..1272486): <c>[ref @6 -&gt; 856-byte atoms][DlmId @0E][UserDbId @12]</c>,
/// ExecTrans the atoms, answer <c>[ref -&gt; atoms][DlmId][u8 ok]</c> (DbAckTable). World decides
/// success or failure itself (ContractEnchant::ExecuteTemper and friends) and sends the atoms for
/// the outcome: the materials always, the new level on success, the penalty level or nothing on
/// failure. So applying the atoms IS following World's result code.</para>
///
/// <para><b>What this class adds</b> is the per-attribute ops <see cref="WarehouseHandlers.Apply"/>
/// cannot express (it sees parsed position/amount fields only): each rewrites fields of the stored
/// 536-byte record, at the offsets the Arbiter's DO_TS_* functions write. The op &lt;-&gt; DO_TS pairing
/// is inferred from the fields each side touches (the dispatch tables are data, not text):</para>
/// <code>
///  op  DO_TS_ (Arbiter line)               record  &lt;- atom
///   4  ENCHANT_ITEM (760107)               +0x28 enchant level &lt;- +0x5C   (old level at +0x244)
///  86  ENCHANT_IDENTIFY_ITEM (759912)      +0x28 &lt;- +0x5C; +0x348: +0x134 &lt;- +0x104, +0x138 &lt;- +0x128,
///                                          +0x13C[+0xD8] &lt;- +0xE0[+0xD8]; +0x349: +0x139 = 1
///  73  CHANGE_ENCHANT_ADJUSTMENT (757337)  +0x160 &lt;- +0x16C
///  75  CHANGE_ENCHANT_BOOSTER (757411)     +0x16C &lt;- +0x174, +0x168 &lt;- +0x178
///  80  ADD_ENCHANT_MATERIAL (756869)       +0x1C0 &lt;- +0x1C0
///  81  AWAKEN_ITEM (757091)                +0x139 = 1
///  92  ITEM_OPTION_RESET (761683)          +0x54..0xCB &lt;- +0x60..0xD7, +0xD0..0xE7 &lt;- +0xEC..0x103, +0x134 &lt;- +0x104
///  93  CHANGE_EQUIPMENT_EXP (757640)       +0x170 (i64) &lt;- +0x1D0
/// </code>
/// <para><b>Not modelled</b>, and said so in the log: op 92's random fill of empty passive slots
/// (the real Arbiter draws them from the item template and EnchantData - cap_multiworld 10860 -
/// neither of which we load, so the slots World left 0 stay 0), op 86's rolled masterwork passive,
/// and op 67 (DO_TS_PERIOD_ITEM_EXTEND, SDB_ITEM_MERGE's time transfer). The Arbiter's own checks
/// (owner, old level == +0x244) are logged, not enforced: our record can be older than World's
/// item, and refusing would block that item for good.</para>
/// </summary>
public static class ItemEdits
{
    public const uint TsEnchantItem = 4, TsChangeEnchantAdjustment = 73, TsChangeEnchantBooster = 75,
                      TsAddEnchantMaterial = 80, TsAwakenItem = 81, TsEnchantIdentifyItem = 86,
                      TsItemOptionReset = 92, TsChangeEquipmentExp = 93, TsPeriodItemExtend = 67;

    // record offsets (the Arbiter's ItemData = the 536-byte record)
    public const int RecEnchantLevel = 0x28, RecPassives = 0x54, RecPassivesSize = 0x78,
                     RecOptionTail = 0xD0, RecOptionTailSize = 0x18, RecGrade = 0x134, RecMasterwork = 0x138,
                     RecAwakened = 0x139, RecIdentifyPassive = 0x13C, RecEnchantAdjustment = 0x160,
                     RecBoosterLevel = 0x168, RecBoosterBonus = 0x16C, RecEquipmentExp = 0x170,
                     RecEnchantMaterial = 0x1C0;

    // atom offsets
    public const int AtomNewEnchant = 0x5C, AtomOldEnchant = 0x244, AtomPassives = 0x60, AtomPassiveSet = 0xD8,
                     AtomIdentifyPassive = 0xE0, AtomOptionTail = 0xEC, AtomGrade = 0x104, AtomMasterwork = 0x128,
                     AtomEnchantAdjustment = 0x16C, AtomBoosterBonus = 0x174, AtomBoosterLevel = 0x178,
                     AtomEnchantMaterial = 0x1C0, AtomEquipmentExp = 0x1D0, AtomMakeMasterwork = 0x348,
                     AtomMakeAwakened = 0x349;

    /// <summary>The ops this class applies (WarehouseHandlers.Apply passes them over).</summary>
    public static bool IsRecordEdit(uint op) => op is TsEnchantItem or TsChangeEnchantAdjustment
        or TsChangeEnchantBooster or TsAddEnchantMaterial or TsAwakenItem or TsEnchantIdentifyItem
        or TsItemOptionReset or TsChangeEquipmentExp;

    /// <summary>
    /// The one write-back the Arbiter makes into an echoed atom (DO_TS_ENCHANT_IDENTIFY_ITEM,
    /// ArbiterServer.exe.c:760037): with the make-masterwork flag set it answers grade 0 at +0x104
    /// and masterwork 1 at +0x128, and World copies those back (ReceiveItemTransactionResult).
    /// Call before the reply is sent. Returns the atoms patched.
    /// </summary>
    public static int PatchReply(byte[] reply, int refOffset)
    {
        int n = 0;
        foreach (int at in Atoms(reply, refOffset))
        {
            if (BitConverter.ToUInt32(reply, at + WarehouseHandlers.AtomOp) != TsEnchantIdentifyItem) continue;
            if (reply[at + AtomMakeMasterwork] != 1) continue;
            BitConverter.GetBytes(0).CopyTo(reply, at + AtomGrade);
            reply[at + AtomMasterwork] = 1;
            n++;
        }
        return n;
    }

    /// <summary>Apply every record-edit atom in the 856-byte list behind the reply ref at
    /// <paramref name="refOffset"/> to its stored item. Returns the records rewritten.</summary>
    public static int Apply(CharacterStore store, byte[] reply, int refOffset, ILogger? log = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(reply);
        int edited = 0;
        foreach (int at in Atoms(reply, refOffset))
        {
            uint op = BitConverter.ToUInt32(reply, at + WarehouseHandlers.AtomOp);
            if (!IsRecordEdit(op)) continue;
            int id = (int)BitConverter.ToInt64(reply, at + WarehouseHandlers.AtomItemDbId);
            var row = id != 0 ? store.GetItem(id) : null;
            if (row is null)
            {
                log?.LogInformation("items: record edit op {Op} for item {Id} - no such row, nothing to change", op, id);
                continue;
            }
            var rec = row.Record is { Length: >= WarehouseHandlers.ItemRecordSize } r ? (byte[])r.Clone()
                : WarehouseHandlers.BuildItemRecord(row.ItemDbId, row.TemplateId, (int)row.OwnerDbId, (int)row.Amount, row.InvenType, row.Slot);
            if (rec.Length < WarehouseHandlers.ItemRecordSize) continue;
            var a = reply.AsSpan(at, DbProxyHandlers.ItemAtomSize);

            switch (op)
            {
                case TsEnchantItem:
                case TsEnchantIdentifyItem:
                {
                    int was = BitConverter.ToInt32(rec, RecEnchantLevel), old = I32(a, AtomOldEnchant), now = I32(a, AtomNewEnchant);
                    if (was != old)
                        log?.LogWarning("items: enchant of item {Id}: stored level {Was}, World's old level {Old} - applied anyway", id, was, old);
                    Put32(rec, RecEnchantLevel, now);
                    if (op == TsEnchantIdentifyItem && a[AtomMakeMasterwork] == 1)
                    {
                        Put32(rec, RecGrade, I32(a, AtomGrade));
                        rec[RecMasterwork] = a[AtomMasterwork];
                        uint set = BitConverter.ToUInt32(a.Slice(AtomPassiveSet, 4));
                        if (set < 2) Put32(rec, RecIdentifyPassive + (int)set * 4, I32(a, AtomIdentifyPassive + (int)set * 4));
                    }
                    if (op == TsEnchantIdentifyItem && a[AtomMakeAwakened] == 1) rec[RecAwakened] = 1;
                    log?.LogInformation("items: item {Id} enchant {Old} -> {New}", id, was, now);
                    break;
                }
                case TsChangeEnchantAdjustment:
                    Put32(rec, RecEnchantAdjustment, I32(a, AtomEnchantAdjustment));
                    break;
                case TsChangeEnchantBooster:
                    Put32(rec, RecBoosterBonus, I32(a, AtomBoosterBonus));
                    Put32(rec, RecBoosterLevel, I32(a, AtomBoosterLevel));
                    break;
                case TsAddEnchantMaterial:
                    Put32(rec, RecEnchantMaterial, I32(a, AtomEnchantMaterial));
                    break;
                case TsAwakenItem:
                    rec[RecAwakened] = 1;
                    log?.LogInformation("items: item {Id} awakened", id);
                    break;
                case TsItemOptionReset:
                    a.Slice(AtomPassives, RecPassivesSize).CopyTo(rec.AsSpan(RecPassives, RecPassivesSize));
                    a.Slice(AtomOptionTail, RecOptionTailSize).CopyTo(rec.AsSpan(RecOptionTail, RecOptionTailSize));
                    Put32(rec, RecGrade, I32(a, AtomGrade));
                    log?.LogInformation("items: item {Id} options reset (empty passive slots are not re-rolled - no template data)", id);
                    break;
                case TsChangeEquipmentExp:
                    BitConverter.GetBytes(BitConverter.ToInt64(a.Slice(AtomEquipmentExp, 8))).CopyTo(rec, RecEquipmentExp);
                    break;
            }
            store.UpsertItem(row.ItemDbId, row.OwnerDbId, row.InvenType, row.Slot, row.TemplateId, row.Amount, rec);
            edited++;
        }
        return edited;
    }

    /// <summary>Payload offsets of the 856-byte atoms behind a reply ref; none for a bad ref.</summary>
    private static IEnumerable<int> Atoms(byte[] reply, int refOffset)
    {
        if (reply is null || refOffset < 0 || (uint)refOffset + 8u > (uint)reply.Length) yield break;
        long start = (long)BitConverter.ToUInt32(reply, refOffset) - 6;
        long bytes = BitConverter.ToUInt32(reply, refOffset + 4);
        int size = DbProxyHandlers.ItemAtomSize;
        if (bytes == 0 || bytes % size != 0 || start < 0 || start + bytes > reply.Length) yield break;
        for (long o = start; o < start + bytes; o += size) yield return (int)o;
    }

    private static int I32(ReadOnlySpan<byte> a, int at) => BitConverter.ToInt32(a.Slice(at, 4));
    private static void Put32(byte[] b, int at, int v) => BitConverter.GetBytes(v).CopyTo(b, at);
}
