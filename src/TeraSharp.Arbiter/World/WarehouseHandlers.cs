using Microsoft.Extensions.Logging;
using TeraSharp.Arbiter.Persistence;

namespace TeraSharp.Arbiter.World;

/// <summary>
/// The warehouse (bank) half of the Arbiter&lt;-&gt;World DB proxy — T42, researched in
/// <c>status/MAIL-WAREHOUSE.md</c>.
///
/// <para>Everything here is pure: layouts, atom parsing and reply builders are static and
/// testable, and the only state is the <see cref="CharacterStore"/> handed to
/// <see cref="Apply"/>. <see cref="DbProxyHandlers"/> owns the wiring.</para>
///
/// <para><b>T44 widened <see cref="Apply"/> past the warehouse.</b> The atom model here — the
/// <see cref="WarehouseAtom"/> triples, <see cref="CloneAtomsWithIds"/>, the INVEN_TYPE helpers
/// and <see cref="Apply"/> itself — is now shared by every message that carries
/// <c>ItemTransactionAtom</c>s, the bag included: <c>SDB_ITEM_SINGLE</c>, the quest rewards in
/// <c>SDB_SET_QUEST_INFO</c> and the fees in <c>SDB_USER_LEARN_SKILL</c> all go through it, via
/// <see cref="BagItems"/>. The class name is narrower than what it holds; the warehouse
/// <i>message</i> layouts below are still its own.</para>
///
/// <para><b>No capture contains a single warehouse frame</b> — the two A&lt;-&gt;W taps are one
/// login each and nobody opened a bank. Every offset below therefore comes from the Arbiter's
/// PDL dumpers (the functions that emit <c>L"FieldName"</c> plus a frame offset) cross-checked
/// against the writer that builds each reply. The min-length guard the real handler applies is
/// quoted with each layout, and every one of them lands exactly on the last field — which is
/// what makes the offsets trustworthy without a capture.</para>
///
/// <para><b>Two traps, both from the decompile</b> (status/MAIL-WAREHOUSE.md section 4.1):
/// <list type="bullet">
/// <item><c>SDB_INCREASE_WAREHOUSE_SIZE</c> (0x283F) is answered with
/// <c>DBS_INCREASE_INVENTORY_SIZE</c> (<b>0x283E</b>), not 0x2840. Both handlers share the send
/// helper <c>FUN_1407ad2e0</c> (Arb_part_066.c:17451) and that helper writes 0x283E. The two
/// DBS layouts are byte-identical, so World's DLM matching still works; replying 0x2840 would
/// head-block the user.</item>
/// <item><c>SDB_MOVE_WAREHOUSE_ITEM</c> (0x2754) is a ten-line stub in the real Arbiter
/// (<c>FUN_1405b53c0</c>, Arb_part_048.c:13050) that sends nothing at all. We match it: the
/// opcode is in <see cref="WorldReplayTable.OneWayFromWorld"/> so it is never answered and
/// never inherits somebody else's reply.</item>
/// </list></para>
/// </summary>
public static class WarehouseHandlers
{
    // ---------------------------------------------------------------- opcodes
    // The opcode numbers live on DbProxyHandlers with every other SDB_/DBS_ constant -- the
    // Dispatch_switch_and_the_allow_list_agree guard resolves `case NAME:` labels by reflecting
    // over that class, so a warehouse opcode declared anywhere else would break the guard.
    // See DbProxyHandlers, the T42 block: SDB_VIEW_WAREHOUSE 0x274A .. SDB_INCREASE_WAREHOUSE_SIZE
    // 0x283F, and the two traps recorded there (0x283F is answered with 0x283E, and 0x2754 is
    // never answered at all).

    // ------------------------------------------------------------- INVEN_TYPE
    // The pocket id in an item row IS enum INVEN_TYPE, and it is what picks the container.
    // Proven from the dispatch in Handler_SDB_INCREASE_WAREHOUSE_SIZE (Arb_part_063.c:8568-8581):
    //   type == 1    -> *(User+0x3f40) + 0x88   (Account + 0x88)
    //   type == 9    -> User + 0x3db0
    //   type == 0xc  -> *(User+0x3f40) + 0x150  (Account + 0x150), behind a config flag
    // and User+0x3c80 = the bag, from Handler_SDB_ITEM_SINGLE (Arb_part_063.c:11756).

    public const int InvenBag = 0;
    public const int InvenAccountWarehouse = 1;
    public const int InvenGuildWarehouse = 3;
    public const int InvenCharacterWarehouse = 9;
    public const int InvenStyleWarehouse = 12;
    public const int InvenEquipped = 14;

    /// <summary>
    /// {1, 3, 9, 12} — the warehouse family. The literal mask the Arbiter tests, at
    /// Arb_part_047.c:13295 and seven more sites: <c>type &lt; 0xd &amp;&amp; ((0x120a &gt;&gt; type) &amp; 1)</c>.
    /// </summary>
    public const uint WarehouseTypeMask = 0x120A;

    /// <summary>
    /// {1, 4, 5, 8, 12} — keyed by AccountDbId rather than CharacterDbId.
    /// <c>TransSQLExec::IsAccountDbIdInvenType</c> (FUN_14049ac90, Arb_part_038.c:11173).
    /// </summary>
    public const uint AccountKeyedTypeMask = 0x1132;

    public static bool IsWarehouse(uint invenType)
        => invenType < 0xD && ((WarehouseTypeMask >> (int)invenType) & 1) != 0;

    public static bool IsAccountKeyed(uint invenType)
        => invenType < 0xD && ((AccountKeyedTypeMask >> (int)invenType) & 1) != 0;

    /// <summary>
    /// 0x48 = 72 slots per warehouse page. A hard-coded literal in the reply writer
    /// (Arb_part_048.c:13834), not a config value.
    /// </summary>
    public const uint WarehousePageSize = 0x48;

    /// <summary>
    /// What we answer <c>MaxSlotCount</c> with until expansion is persisted per owner. The real
    /// caps (576 account / 360 character / 288 guild) come from ServerConfig.xml, which we do
    /// not read, so the store's row wins and this is only the fallback for an owner with no row.
    /// </summary>
    public const ushort DefaultMaxSlotCount = 0;

    // ------------------------------------------------- ItemTransactionAtom fields
    // Atom-relative. The first four are already in status/INVENTORY-DESIGN.md section 3; the
    // source/destination triples are pinned here from World's PrepareWareSendTransaction
    // (WorldServer.exe.c:1470277) and PrepareWareRecvTransaction (:1470120), which write them
    // field by field, and they agree with TransSQLExec::GetInven reading +0x20 / +0x28.

    public const int AtomOp = 0x04;
    public const int AtomItemDbId = 0x10;   // i64; DbProxyHandlers reads the low half as the id
    public const int AtomTemplateId = 0x18;
    public const int AtomSrcOwner = 0x20;   // i64
    public const int AtomSrcInven = 0x28;
    public const int AtomSrcSlot = 0x30;
    public const int AtomDstOwner = 0x38;   // i64
    public const int AtomDstInven = 0x40;
    public const int AtomDstSlot = 0x48;
    public const int AtomDelta = 0x50;      // i64, signed

    // Sub-operations. 0x0D/0x0E/0x0F are written by PrepareWareSendTransaction and 0x0D/0x11 by
    // PrepareWareRecvTransaction; the names are the Arbiter's DO_TS_WARE_* set, matched by what
    // each branch fills in. 2/7/11 are the general item ops already used by SDB_ITEM_SINGLE.
    public const uint TsChangeItemAmount = 2;
    public const uint TsInsertItem = 7;
    public const uint TsDeleteItem = 11;
    // T44: the rest of the ops a bag actually sees. Indices from the World-side Prepare*
    // builders (status/INVENTORY-DESIGN.md section 4); 2, 6+11, 7 and 9 are the only four an
    // hour of level-1 play issued, 3 and 36 are moving things around the bag.
    public const uint TsChangeItemPos = 3;    // move to an EMPTY slot
    public const uint TsDetachStack = 6;      // first half of "this stack is now empty", paired with 11
    public const uint TsChangeMoney = 9;      // CHARACTER money (template id 0), not warehouse money
    public const uint TsSwapItemPos = 36;     // swap two OCCUPIED slots
    public const uint TsWareChangeMoney = 0x0D;   // src=dst=(wareOwner, wareType); +0x50 = delta
    public const uint TsWareMoveItem = 0x0E;      // whole row moves to (dstOwner, dstInven, dstSlot)
    public const uint TsWareInsertItem = 0x0F;    // new row in the warehouse (a split stack)
    public const uint TsWareChangeAmount = 0x11;  // +0x50 added to the row at (src triple)

    /// <summary>One parsed <c>ItemTransactionAtom</c>. Only the fields the warehouse path uses.</summary>
    public readonly record struct WarehouseAtom(
        int Index, uint Op, long ItemDbId, int TemplateId,
        long SrcOwner, uint SrcInven, uint SrcSlot,
        long DstOwner, uint DstInven, uint DstSlot,
        long Delta);

    // ------------------------------------------------------- request layouts
    // Payload index = frame offset - 6. Each Min* is the real handler's guard, and in every case
    // it lands exactly on the end of the last field.

    public const int ViewRequestSize = 28;          // frame >= 34
    public const int ViewReqDlmId = 0, ViewReqOwnerDbId = 4, ViewReqArbiterUser = 12,
                     ViewReqInvenType = 20, ViewReqViewPos = 24;

    public const int StoreRequestSize = 57;         // frame >= 63
    public const int StoreReqBinaryRef = 0, StoreReqDlmId = 8, StoreReqOldOwner = 12,
                     StoreReqNewOwner = 20, StoreReqInvenType = 28, StoreReqByQac = 32,
                     StoreReqTemplateId = 33, StoreReqAmountDelta = 37, StoreReqItemDbId = 41,
                     StoreReqMoneyDelta = 49;

    public const int GetRequestSize = 56;           // frame >= 62
    public const int GetReqBinaryRef = 0, GetReqDlmId = 8, GetReqOldOwner = 12,
                     GetReqWareInvenType = 20, GetReqNewOwner = 24, GetReqTemplateId = 32,
                     GetReqAmountDelta = 36, GetReqItemDbId = 40, GetReqMoneyDelta = 48;

    public const int ChangePosRequestSize = 28;     // frame >= 34
    public const int ChangePosReqBinaryRef = 0, ChangePosReqDlmId = 8, ChangePosReqOwnerDbId = 12,
                     ChangePosReqInvenType = 20, ChangePosReqOwnerUserDbId = 24;

    public const int ClearRequestSize = 12;         // frame >= 18
    public const int ClearReqDlmId = 0, ClearReqOwnerDbId = 4, ClearReqInvenType = 8;

    public const int AutoSortRequestSize = 20;      // frame >= 26
    public const int SortReqDlmId = 0, SortReqUserDbId = 4, SortReqInvenType = 8,
                     SortReqBegin = 12, SortReqEnd = 16;

    public const int PayCommisionRequestSize = 20;  // frame >= 26
    public const int PayReqDlmId = 0, PayReqOwnerDbId = 4, PayReqInvenType = 8, PayReqCommision = 12;

    public const int IncreaseSizeRequestSize = 28;  // frame >= 34
    public const int IncReqBinaryRef = 0, IncReqDlmId = 8, IncReqUserDbId = 12,
                     IncReqInvenType = 16, IncReqDeltaAmount = 20, IncReqExpandTemplateId = 24;

    public const int MoveItemRequestSize = 32;      // frame >= 38
    public const int MoveReqDlmId = 0, MoveReqSendCharDbId = 4, MoveReqRecvCharDbId = 8,
                     MoveReqItemDbId = 12, MoveReqItemAmount = 20, MoveReqMoney = 24;

    // --------------------------------------------------------- reply layouts

    /// <summary>DBS_VIEW_WAREHOUSE header, payload-relative. The ItemData list follows.</summary>
    public const int ViewReplyHeader = 39;          // frame >= 45
    public const int ViewRspListRef = 0, ViewRspDlmId = 8, ViewRspSuccess = 12, ViewRspViewPos = 13,
                     ViewRspViewSize = 17, ViewRspEndPos = 21, ViewRspTotalItemNum = 25,
                     ViewRspCurrentMoney = 29, ViewRspMaxSlotCount = 37;

    /// <summary>Shared by DBS_STORE_WAREHOUSE, DBS_GET_WAREHOUSE and DBS_CHANGE_WAREHOUSE_POS.</summary>
    public const int TransferReplyHeader = 25;      // frame >= 31
    public const int TransferRspBinaryRef = 0, TransferRspDlmId = 8, TransferRspSuccess = 12,
                     TransferRspError = 13, TransferRspWareCommision = 17;

    public const int ClearReplySize = 5;            // frame >= 11
    public const int AutoSortReplySize = 33;        // frame >= 39
    public const int PayCommisionReplySize = 9;     // frame >= 15
    public const int IncreaseSizeReplySize = 5;     // frame >= 11

    /// <summary>The 536-byte ItemData record DBS_VIEW_WAREHOUSE lists, same as 0x27A4.</summary>
    public const int ItemRecordSize = 0x218;

    // ---------------------------------------------------------------- parsing

    /// <summary>
    /// The atoms behind an <c>[u32 frame-relative offset][u32 byte count]</c> ref at
    /// <paramref name="refOffset"/>. Returns an empty list for a malformed or absent ref rather
    /// than throwing: an unanswered per-user DB item head-blocks the user's whole queue
    /// (status/HANDOFF.md section 1), so the caller must always be able to reply.
    /// </summary>
    public static IReadOnlyList<WarehouseAtom> ParseAtoms(byte[] payload, int refOffset, int recordSize = 0)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (recordSize <= 0) recordSize = DbProxyHandlers.ItemAtomSize;
        if (!TrySliceAtoms(payload, refOffset, minStart: 0, out int start, out int bytes, recordSize))
            return Array.Empty<WarehouseAtom>();
        int n = bytes / recordSize;
        var atoms = new WarehouseAtom[n];
        for (int i = 0; i < n; i++) atoms[i] = ReadAtom(payload, start + i * recordSize);
        return atoms;
    }

    /// <summary>
    /// The atom bytes behind the ref at <paramref name="refOffset"/>, copied, with a freshly
    /// allocated item DB id written into <c>+0x10</c> of every insert that arrived with 0 — and
    /// the same buffer parsed, so the ids the reply carries and the ids the rows get are the
    /// same ones. Parsing the request instead would allocate twice and hand World an id we did
    /// not store.
    ///
    /// <para>Two ops allocate: <see cref="TsInsertItem"/> (7), the general insert
    /// <c>DBS_ITEM_SINGLE</c> already handles, and <see cref="TsWareInsertItem"/> (0x0F), which
    /// <c>PrepareWareSendTransaction</c> writes when a partial stack is banked. The id is
    /// written as a 4-byte int, which is exactly what the captured 0x2769 echoes show
    /// (status/INVENTORY-DESIGN.md, "Reply echo rule").</para>
    /// </summary>
    public static (byte[] Atoms, IReadOnlyList<WarehouseAtom> Parsed) CloneAtomsWithIds(
        byte[] payload, int refOffset, int minStart, Func<int> allocateItemId, int recordSize = 0)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(allocateItemId);
        if (recordSize <= 0) recordSize = DbProxyHandlers.ItemAtomSize;

        if (!TrySliceAtoms(payload, refOffset, minStart, out int start, out int bytes, recordSize))
            return (Array.Empty<byte>(), Array.Empty<WarehouseAtom>());

        var atoms = new byte[bytes];
        Array.Copy(payload, start, atoms, 0, bytes);
        for (int o = 0; o + recordSize <= atoms.Length; o += recordSize)
        {
            uint op = BitConverter.ToUInt32(atoms, o + AtomOp);
            if (op != TsInsertItem && op != TsWareInsertItem) continue;
            if (BitConverter.ToInt64(atoms, o + AtomItemDbId) != 0) continue;
            BitConverter.GetBytes(allocateItemId()).CopyTo(atoms, o + AtomItemDbId);
        }
        return (atoms, ParseAtomArray(atoms, recordSize));
    }

    /// <summary>A packed array of 856-byte atoms, with no ref header in front of it.</summary>
    public static IReadOnlyList<WarehouseAtom> ParseAtomArray(byte[] atoms, int recordSize = 0)
    {
        ArgumentNullException.ThrowIfNull(atoms);
        if (recordSize <= 0) recordSize = DbProxyHandlers.ItemAtomSize;
        int n = atoms.Length / recordSize;
        var parsed = new WarehouseAtom[n];
        for (int i = 0; i < n; i++) parsed[i] = ReadAtom(atoms, i * recordSize);
        return parsed;
    }

    private static bool TrySliceAtoms(byte[] payload, int refOffset, int minStart, out int start, out int bytes,
                                      int recordSize = 0)
    {
        start = 0; bytes = 0;
        if (recordSize <= 0) recordSize = DbProxyHandlers.ItemAtomSize;
        if (refOffset < 0 || refOffset + 8 > payload.Length) return false;
        start = (int)BitConverter.ToUInt32(payload, refOffset) - 6;   // frame -> payload
        bytes = (int)BitConverter.ToUInt32(payload, refOffset + 4);
        return bytes > 0 && start >= minStart
               && bytes % recordSize == 0
               && start <= payload.Length - bytes;
    }

    private static WarehouseAtom ReadAtom(byte[] buf, int a) => new(
        Index: BitConverter.ToInt32(buf, a),
        Op: BitConverter.ToUInt32(buf, a + AtomOp),
        ItemDbId: BitConverter.ToInt64(buf, a + AtomItemDbId),
        TemplateId: BitConverter.ToInt32(buf, a + AtomTemplateId),
        SrcOwner: BitConverter.ToInt64(buf, a + AtomSrcOwner),
        SrcInven: BitConverter.ToUInt32(buf, a + AtomSrcInven),
        SrcSlot: BitConverter.ToUInt32(buf, a + AtomSrcSlot),
        DstOwner: BitConverter.ToInt64(buf, a + AtomDstOwner),
        DstInven: BitConverter.ToUInt32(buf, a + AtomDstInven),
        DstSlot: BitConverter.ToUInt32(buf, a + AtomDstSlot),
        Delta: BitConverter.ToInt64(buf, a + AtomDelta));

    // --------------------------------------------------------------- builders

    /// <summary>
    /// DBS_VIEW_WAREHOUSE (0x274B). <paramref name="items"/> are 536-byte ItemData records, the
    /// same record DBS_USER_LOAD_INVENTORY (0x27A4) carries — <c>status/INVENTORY-DESIGN.md</c>
    /// section 2. An empty list is the correct answer for an owner with nothing banked.
    /// </summary>
    public static byte[] BuildDbsViewWarehouse(uint dlmId, bool ok, uint viewPos, uint endPos,
                                               uint totalItemNum, long currentMoney,
                                               ushort maxSlotCount, IReadOnlyList<byte[]>? items)
    {
        items ??= Array.Empty<byte[]>();
        int listBytes = 0;
        foreach (var it in items) listBytes += it.Length;

        var p = new byte[ViewReplyHeader + listBytes];
        BitConverter.GetBytes((uint)(6 + ViewReplyHeader)).CopyTo(p, ViewRspListRef);   // frame-relative
        BitConverter.GetBytes((uint)listBytes).CopyTo(p, ViewRspListRef + 4);
        BitConverter.GetBytes(dlmId).CopyTo(p, ViewRspDlmId);
        p[ViewRspSuccess] = (byte)(ok ? 1 : 0);
        BitConverter.GetBytes(viewPos).CopyTo(p, ViewRspViewPos);
        BitConverter.GetBytes(WarehousePageSize).CopyTo(p, ViewRspViewSize);
        BitConverter.GetBytes(endPos).CopyTo(p, ViewRspEndPos);
        BitConverter.GetBytes(totalItemNum).CopyTo(p, ViewRspTotalItemNum);
        BitConverter.GetBytes(currentMoney).CopyTo(p, ViewRspCurrentMoney);
        BitConverter.GetBytes(maxSlotCount).CopyTo(p, ViewRspMaxSlotCount);

        int at = ViewReplyHeader;
        foreach (var it in items) { it.CopyTo(p, at); at += it.Length; }
        return p;
    }

    /// <summary>
    /// DBS_STORE_WAREHOUSE / DBS_GET_WAREHOUSE / DBS_CHANGE_WAREHOUSE_POS — one shape, three
    /// opcodes. <paramref name="atoms"/> are the request's atoms echoed back with an allocated
    /// id filled into <c>+0x10</c> of every insert that arrived with 0, exactly the rule
    /// DBS_ITEM_SINGLE follows (status/INVENTORY-DESIGN.md, "Reply echo rule").
    /// </summary>
    public static byte[] BuildDbsTransfer(byte[]? atoms, uint dlmId, bool ok, uint error, long wareCommision)
    {
        atoms ??= Array.Empty<byte>();
        var p = new byte[TransferReplyHeader + atoms.Length];
        BitConverter.GetBytes((uint)(6 + TransferReplyHeader)).CopyTo(p, TransferRspBinaryRef);
        BitConverter.GetBytes((uint)atoms.Length).CopyTo(p, TransferRspBinaryRef + 4);
        BitConverter.GetBytes(dlmId).CopyTo(p, TransferRspDlmId);
        p[TransferRspSuccess] = (byte)(ok ? 1 : 0);
        BitConverter.GetBytes(error).CopyTo(p, TransferRspError);
        BitConverter.GetBytes(wareCommision).CopyTo(p, TransferRspWareCommision);
        atoms.CopyTo(p, TransferReplyHeader);
        return p;
    }

    /// <summary>DBS_CLEAR_WAREHOUSE (0x27E1): <c>[u32 DlmId][u8 Success]</c>.</summary>
    public static byte[] BuildDbsClearWarehouse(uint dlmId, bool ok)
    {
        var p = new byte[ClearReplySize];
        BitConverter.GetBytes(dlmId).CopyTo(p, 0);
        p[4] = (byte)(ok ? 1 : 0);
        return p;
    }

    /// <summary>DBS_WAREHOUSE_AUTO_SORT (0x27E3): the request's four fields echoed back.</summary>
    public static byte[] BuildDbsAutoSort(uint dlmId, bool ok, uint userDbId, uint invenType,
                                          uint sortBegin, uint sortEnd, uint errorMsg, long wareCommision)
    {
        var p = new byte[AutoSortReplySize];
        BitConverter.GetBytes(dlmId).CopyTo(p, 0);
        p[4] = (byte)(ok ? 1 : 0);
        BitConverter.GetBytes(userDbId).CopyTo(p, 5);
        BitConverter.GetBytes(invenType).CopyTo(p, 9);
        BitConverter.GetBytes(sortBegin).CopyTo(p, 13);
        BitConverter.GetBytes(sortEnd).CopyTo(p, 17);
        BitConverter.GetBytes(errorMsg).CopyTo(p, 21);
        BitConverter.GetBytes(wareCommision).CopyTo(p, 25);
        return p;
    }

    /// <summary>DBS_PAY_WAREHOUSE_COMMISION (0x27CA): <c>[u32 DlmId][u8 Success][u32 Error]</c>.</summary>
    public static byte[] BuildDbsPayCommision(uint dlmId, bool ok, uint error)
    {
        var p = new byte[PayCommisionReplySize];
        BitConverter.GetBytes(dlmId).CopyTo(p, 0);
        p[4] = (byte)(ok ? 1 : 0);
        BitConverter.GetBytes(error).CopyTo(p, 5);
        return p;
    }

    /// <summary>
    /// The reply to SDB_INCREASE_WAREHOUSE_SIZE: <c>[u8 Success][u32 DlmId]</c> — Success FIRST,
    /// like 0x2892 and 0x2911 — and it goes out as DBS_INCREASE_INVENTORY_SIZE (0x283E).
    /// </summary>
    public static byte[] BuildDbsIncreaseSize(bool ok, uint dlmId)
    {
        var p = new byte[IncreaseSizeReplySize];
        p[0] = (byte)(ok ? 1 : 0);
        BitConverter.GetBytes(dlmId).CopyTo(p, 1);
        return p;
    }

    /// <summary>
    /// A synthetic 536-byte ItemData record for a stored row. Only the six named fields plus the
    /// handful of constants that are the same on all six captured records are set; the rest is
    /// zero. That is safe because the real records' tails are uninitialised Arbiter heap that
    /// World has to ignore — status/INVENTORY-DESIGN.md section 2, "The tail is uninitialised
    /// Arbiter heap".
    /// </summary>
    public static byte[] BuildItemRecord(int itemDbId, int templateId, int ownerId, int amount,
                                         int pocket, int slot)
    {
        var r = new byte[ItemRecordSize];
        BitConverter.GetBytes(itemDbId).CopyTo(r, StarterInventory.RecordIdOffset);      // 0
        BitConverter.GetBytes(templateId).CopyTo(r, StarterInventory.RecordTemplateIdOffset); // 8
        BitConverter.GetBytes(ownerId).CopyTo(r, DbProxyHandlers.StarterInventoryOwnerOffset); // 16
        BitConverter.GetBytes(amount).CopyTo(r, StarterInventory.RecordAmountOffset);    // 24
        BitConverter.GetBytes(pocket).CopyTo(r, StarterInventory.RecordPocketOffset);    // 28
        BitConverter.GetBytes(slot).CopyTo(r, StarterInventory.RecordSlotOffset);        // 36
        BitConverter.GetBytes(30).CopyTo(r, 44);                                         // constant on all six
        WriteNeverTimestamp(r, 276);
        WriteNeverTimestamp(r, 292);
        WriteNeverTimestamp(r, 464);
        BitConverter.GetBytes(1.0f).CopyTo(r, 484);
        return r;
    }

    /// <summary>The 16-byte ODBC TIMESTAMP the captured records carry for "never": 1970-01-01.</summary>
    private static void WriteNeverTimestamp(byte[] r, int at)
    {
        BitConverter.GetBytes((short)1970).CopyTo(r, at);
        BitConverter.GetBytes((ushort)1).CopyTo(r, at + 2);
        BitConverter.GetBytes((ushort)1).CopyTo(r, at + 4);
        // hour/minute/second/fraction stay 0
    }

    // --------------------------------------------------------------- applying

    /// <summary>What <see cref="Apply"/> did, for the log line and the tests.</summary>
    /// <param name="MoneyDelta">Net WAREHOUSE money moved (op 0x0D).</param>
    /// <param name="Ignored">Atoms that changed no row: an op we do not model, or one modelled
    /// as a deliberate no-op (the detach half of a consumed stack).</param>
    /// <param name="CharacterMoneyDelta">Net CHARACTER money applied (op 9) - T59. It lands on
    /// <c>characters.money</c>, not in <c>items</c>, so it is counted separately from the
    /// warehouse total and is not an "ignored" atom any more.</param>
    public readonly record struct ApplyResult(int Inserted, int Moved, int AmountChanged, int Deleted,
                                              long MoneyDelta, int Ignored, long CharacterMoneyDelta);

    /// <summary>
    /// Apply one message's atoms to the item rows.
    ///
    /// <para><b>T44: every container, not just the warehouse.</b> The bag (INVEN_TYPE 0) and the
    /// worn slots (14) are rows in the same <c>items</c> table as the warehouse pockets, keyed
    /// the same way, so an atom is applied wherever its source and destination triples point.
    /// T42's "skip any atom that touches no warehouse" rule is gone — it existed only because
    /// the bag was still served from <c>data/starter_inventory.bin</c> and a bag row would have
    /// been a second, disagreeing source of truth. There is one source of truth now.</para>
    ///
    /// <para>An atom that moves or changes an item we have never seen <b>inserts</b> the row
    /// instead of failing. Items can reach a character without passing through here — the
    /// starter kit is seeded from <c>StarterInventory</c>, and any op we do not model leaves its
    /// rows untouched — so refusing to act on an unknown id would quietly lose items. The one
    /// thing never invented is a row for a delete or a negative amount change.</para>
    ///
    /// <para>Unmodelled ops are logged and <b>echoed but not applied</b>: the reply still carries
    /// the atoms back, so World keeps its own view, and our rows simply do not follow. That is
    /// the safe direction — the alternative is guessing at an op's semantics and corrupting the
    /// row. status/INVENTORY-DESIGN.md section 4 lists the 82 <c>DO_TS_*</c> operations; the six
    /// a level-1 character actually issues are 2, 6+11, 7 and 9.</para>
    /// </summary>
    public static ApplyResult Apply(CharacterStore store, IReadOnlyList<WarehouseAtom>? atoms,
                                    Func<int> allocateItemId, ILogger? log = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(allocateItemId);
        atoms ??= Array.Empty<WarehouseAtom>();

        int inserted = 0, moved = 0, changed = 0, deleted = 0, ignored = 0;
        long money = 0, charMoney = 0;

        foreach (var a in atoms)
        {
            // Both triples are populated in every captured atom, and they are equal for an op
            // that stays inside one container. A zero destination owner means the builder only
            // filled the source half, so fall back to it rather than writing owner 0.
            long dstOwner = a.DstOwner != 0 ? a.DstOwner : a.SrcOwner;
            uint dstInven = a.DstOwner != 0 ? a.DstInven : a.SrcInven;
            uint dstSlot = a.DstOwner != 0 ? a.DstSlot : a.SrcSlot;

            switch (a.Op)
            {
                // ---- money ----
                case TsWareChangeMoney:
                    store.AddWarehouseMoney(dstOwner, (int)dstInven, a.Delta);
                    money += a.Delta;
                    break;

                case TsChangeMoney:
                {
                    // Character money (template id 0). It lives on the character row, not in
                    // `items`. T59: the delta is SIGNED and RELATIVE -
                    // Inventory::PrepareMoneyTransaction refuses the change when
                    // (current money + amount) < 0 and then writes that same amount to
                    // atom + 0x50, so it is what to add, never a new total.
                    long now = store.AddCharacterMoney(dstOwner, a.Delta);
                    charMoney += a.Delta;
                    log?.LogDebug("items: character money {Delta} for owner {Owner} -> {Total}",
                        a.Delta, dstOwner, now);
                    break;
                }

                // ---- insert ----
                case TsInsertItem:
                case TsWareInsertItem:
                {
                    int id = a.ItemDbId != 0 ? (int)a.ItemDbId : allocateItemId();
                    store.UpsertItem(id, dstOwner, (int)dstInven, (int)dstSlot,
                                     a.TemplateId, a.Delta > 0 ? a.Delta : 1);
                    inserted++;
                    break;
                }

                // ---- move, within a container or between two ----
                case TsChangeItemPos:
                case TsWareMoveItem:
                    if (a.ItemDbId == 0)
                    {
                        // Identified by position instead: find what is at the source triple.
                        var at = store.FindItemAt(a.SrcOwner, (int)a.SrcInven, (int)a.SrcSlot);
                        if (at != null && store.MoveItem(at.ItemDbId, dstOwner, (int)dstInven, (int)dstSlot))
                            moved++;
                        else ignored++;
                        break;
                    }
                    if (store.MoveItem((int)a.ItemDbId, dstOwner, (int)dstInven, (int)dstSlot))
                        moved++;
                    else
                    {
                        // An id we have never seen: it came from a path that does not write rows.
                        store.UpsertItem((int)a.ItemDbId, dstOwner, (int)dstInven, (int)dstSlot,
                                         a.TemplateId, a.Delta > 0 ? a.Delta : 1);
                        inserted++;
                    }
                    break;

                // ---- swap two occupied slots ----
                case TsSwapItemPos:
                {
                    // Both halves are identified by position: the atom names one pair of slots
                    // and the two rows sitting in them trade places.
                    var first = store.FindItemAt(a.SrcOwner, (int)a.SrcInven, (int)a.SrcSlot);
                    var second = store.FindItemAt(dstOwner, (int)dstInven, (int)dstSlot);
                    if (first != null && second != null && store.SwapItemPositions(first.ItemDbId, second.ItemDbId))
                        moved += 2;
                    else ignored++;
                    break;
                }

                // ---- amount ----
                case TsChangeItemAmount:
                case TsWareChangeAmount:
                {
                    long owner = a.SrcOwner != 0 ? a.SrcOwner : dstOwner;
                    int inven = (int)(a.SrcOwner != 0 ? a.SrcInven : dstInven);
                    int slot = (int)(a.SrcOwner != 0 ? a.SrcSlot : dstSlot);

                    int id = (int)a.ItemDbId;
                    if (id == 0) id = store.FindItemAt(owner, inven, slot)?.ItemDbId ?? 0;

                    if (id == 0)
                    {
                        // No id in the atom and nothing at that position. Allocating one here
                        // would put a row in the inventory under an id World has never seen, and
                        // the next operation on it would miss - strictly worse than dropping it.
                        log?.LogWarning("items: amount change of {Delta} at {Inven}:{Slot} names no item - dropped",
                            a.Delta, inven, slot);
                        ignored++;
                    }
                    else if (store.AddItemAmount(id, a.Delta)) changed++;
                    else if (a.Delta > 0)
                    {
                        store.UpsertItem(id, owner, inven, slot, a.TemplateId, a.Delta);
                        inserted++;
                    }
                    else ignored++;   // a negative delta on a row we do not have: nothing to take
                    break;
                }

                // ---- delete ----
                case TsDeleteItem:
                    if (a.ItemDbId != 0 && store.DeleteItem((int)a.ItemDbId)) deleted++;
                    else ignored++;
                    break;

                case TsDetachStack:
                    // The first half of "this stack is now empty"; the paired op 11 in the same
                    // message does the delete. Modelled as a no-op on purpose - acting on it too
                    // would delete the row twice, and the second delete would miss.
                    log?.LogDebug("items: detach (op 6) for item {Id} - the paired delete does the work", a.ItemDbId);
                    ignored++;
                    break;

                default:
                    log?.LogInformation(
                        "items: atom op {Op} not modelled (item {Id}, {SrcInven}:{SrcSlot} -> {DstInven}:{DstSlot}) - echoed, not applied",
                        a.Op, a.ItemDbId, a.SrcInven, a.SrcSlot, dstInven, dstSlot);
                    ignored++;
                    break;
            }
        }

        store.PruneEmptyItems();
        return new ApplyResult(inserted, moved, changed, deleted, money, ignored, charMoney);
    }
}
