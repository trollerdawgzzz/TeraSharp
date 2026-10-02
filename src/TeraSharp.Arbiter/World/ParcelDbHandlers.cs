// SPDX-License-Identifier: MIT
// Copyright (c) 2026 the TeraSharp contributors

using Microsoft.Extensions.Logging;
using TeraSharp.Arbiter.Persistence;

namespace TeraSharp.Arbiter.World;

/// <summary>
/// The mail (parcel) half of the Arbiter&lt;-&gt;World DB proxy — T45, layouts researched in
/// <c>status/MAIL-WAREHOUSE.md</c> section 3 and re-derived here from the writers.
///
/// <para><b>Why this exists.</b> T42 built the parcel <i>table</i> and the three client packets
/// the Arbiter answers directly, and left the six W&lt;-&gt;A pairs unimplemented. Nothing
/// answered <c>SDB_LIST_PARCEL</c>, so opening the mailbox produced no
/// <c>DBS_LIST_PARCEL</c> at all — and neither capture contains a parcel frame, so the replay
/// table has nothing to fall back on either. The client drew its empty 12-row grid with a "00"
/// count and World's per-user DLM queue for that character was left holding an item that never
/// completes (status/HANDOFF.md section 1), which is a wedge, not just a cosmetic bug.</para>
///
/// <para><b>Everything here is pure</b> — layouts and reply builders are static and testable;
/// <see cref="DbProxyHandlers"/> owns the wiring and the store.</para>
///
/// <para><b>No capture contains a single parcel frame.</b> Every offset below comes from the
/// Arbiter's PDL dumpers cross-checked against the writer that builds each reply, and the
/// min-length guard each real handler applies is quoted with its layout. The <b>empty</b>
/// <c>DBS_LIST_PARCEL</c> is nevertheless byte-exact without a capture, because every field in
/// it is either echoed from the request or a compiled-in default that
/// <c>Handler_SDB_LIST_PARCEL</c> sets before it looks anything up — see
/// <see cref="BuildDbsListParcel"/>.</para>
/// </summary>
public static class ParcelDbHandlers
{
    // ---------------------------------------------------------------- layouts
    // Offsets below are PAYLOAD-relative (frame offset - 6), the same convention
    // WarehouseHandlers uses. A "binary ref" is [u32 frame-relative offset][u32 byte count] and
    // the offset is the running FRAME length at the point the writer backpatches it.

    // --- SDB_LIST_PARCEL 0x2777 -> DBS_LIST_PARCEL 0x2778 ---
    // Handler_SDB_LIST_PARCEL = FUN_14082fc50 (Arb_part_071.c:15271), guard `param_3 < 0x17`.
    //   frame [6] DlmId [10] UserDbId [14] ViewType [18] CurPage [22] UncheckedOnly
    /// <summary>Minimum request FRAME length the real handler enforces (0x17).</summary>
    public const int ListRequestFrameSize = 23;
    public const int ListReqDlmId = 0;
    public const int ListReqUserDbId = 4;
    public const int ListReqViewType = 8;
    public const int ListReqCurPage = 12;
    public const int ListReqUncheckedOnly = 16;
    public const int ListRequestSize = 17;

    // The reply writer, read straight out of FUN_14082fc50 (Arb_part_071.c:15376-15400):
    //   FUN_140350eb0(&pkt, 0x2778)                    opcode
    //   slotA = cursor; *slotA = 0; u32 0              DataList offset
    //   slotB = cursor; *slotB = 0; u32 0              DataList bytes
    //   u32 dlmId    = *(u32*)(request + 6)            DlmId, echoed
    //   u8  success                                    Success
    //   u32 viewType = *(u32*)(request + 0xe)          ViewType, echoed
    //   u32 curPage  = *(u32*)(request + 0x12)         CurPage, echoed
    //   u32 maxPage                                    MaxPage, a handler local
    //   u32 parcelCount                                ParcelCount, a handler local
    //   *slotA = frameLength                           ALWAYS - even for an empty list
    //   if (list non-empty) { append N x 0x9e8; *slotB = N * 0x9e8; }
    public const int ListReplyHeader = 29;              // payload bytes before the list
    public const int ListRspBinaryRef = 0;              // [0] offset [4] bytes
    public const int ListRspDlmId = 8;
    public const int ListRspSuccess = 12;
    public const int ListRspViewType = 13;
    public const int ListRspCurPage = 17;
    public const int ListRspMaxPage = 21;
    public const int ListRspParcelCount = 25;

    /// <summary><c>ParcelDataNoMsg</c>, 0x9e8 bytes — the stride the writer copies with and the
    /// size its bounds check tests (<c>capacity &lt; frameLength + 0x9e8</c>).</summary>
    public const int ParcelDataNoMsgSize = 0x9e8;

    // --- SDB_MAKE_PARCEL 0x2779 -> DBS_MAKE_PARCEL 0x277a ---
    //   request frame [6] ParcelData ref [14] ParcelTransList ref [22] DlmId, guard 0x1a
    //   reply   frame [6] ParcelTransList ref [14] DlmId [18] Success [19] SendParcelError
    //                 [23] ParcelRecverDbId
    public const int MakeRequestFrameSize = 26;
    public const int MakeReqParcelDataRef = 0;
    public const int MakeReqTransListRef = 8;
    public const int MakeReqDlmId = 16;
    public const int MakeRequestSize = 20;

    public const int MakeReplyHeader = 21;
    public const int MakeRspTransListRef = 0;
    public const int MakeRspDlmId = 8;
    public const int MakeRspSuccess = 12;
    public const int MakeRspSendParcelError = 13;
    public const int MakeRspRecverDbId = 17;

    // --- SDB_RECV_PARCEL 0x277b -> DBS_RECV_PARCEL 0x277c ---
    //   request frame [6] ParcelTransList ref [14] DlmId [18] Step [22] ParcelId, guard 0x1a
    //   reply   frame [6] ParcelData ref [14] ParcelTransList ref [22] DlmId [26] Step [30] Success
    public const int RecvRequestFrameSize = 26;
    public const int RecvReqTransListRef = 0;
    public const int RecvReqDlmId = 8;
    public const int RecvReqStep = 12;
    public const int RecvReqParcelId = 16;
    public const int RecvRequestSize = 20;

    public const int RecvReplyHeader = 25;
    public const int RecvRspParcelDataRef = 0;
    public const int RecvRspTransListRef = 8;
    public const int RecvRspDlmId = 16;
    public const int RecvRspStep = 20;
    public const int RecvRspSuccess = 24;

    // --- SDB_RECV_PARCEL_EX 0x277d -> DBS_RECV_PARCEL_EX 0x277e ---
    //   request frame [6] ref A [14] ref B [22] DlmId [26] Step [30] OwnerDBID [34] IsAllParcel
    //   reply   frame [6] ref A [14] ref B [22] DlmId [26] Step [30] No_parcel [34] Success
    public const int RecvExRequestFrameSize = 35;
    public const int RecvExReqRefA = 0;
    public const int RecvExReqRefB = 8;
    public const int RecvExReqDlmId = 16;
    public const int RecvExReqStep = 20;
    public const int RecvExReqOwnerDbId = 24;
    public const int RecvExReqIsAllParcel = 28;
    public const int RecvExRequestSize = 29;

    public const int RecvExReplyHeader = 29;
    public const int RecvExRspRefA = 0;
    public const int RecvExRspRefB = 8;
    public const int RecvExRspDlmId = 16;
    public const int RecvExRspStep = 20;
    public const int RecvExRspNoParcel = 24;
    public const int RecvExRspSuccess = 28;

    // --- SDB_RETURN_PARCEL 0x2781 -> DBS_RETURN_PARCEL 0x2782 ---
    //   request frame [6] DlmId [10] ParcelId, guard 0x0e
    //   reply   frame [6] DlmId [10] Success
    public const int ReturnRequestFrameSize = 14;
    public const int ReturnReqDlmId = 0;
    public const int ReturnReqParcelId = 4;
    public const int ReturnRequestSize = 8;
    public const int ReturnReplySize = 5;

    // --- SDB_DELETE_PARCEL 0x2811 -> DBS_DELETE_PARCEL 0x2812 ---
    //   request frame [6] DelList ref [14] DlmId [18] UserDbId [22] IsSendParcel, guard 0x17
    //   reply   frame [6] DlmId [10] Success
    public const int DeleteRequestFrameSize = 23;
    public const int DeleteReqDelListRef = 0;
    public const int DeleteReqDlmId = 8;
    public const int DeleteReqUserDbId = 12;
    public const int DeleteReqIsSendParcel = 16;
    public const int DeleteRequestSize = 17;
    public const int DeleteReplySize = 5;

    // -------------------------------------------------------------- accessors

    /// <summary>A u32 at a payload index, or 0 when the payload is too short. Never throws:
    /// an unanswered per-user DB request head-blocks the user for the life of the World
    /// process, so a malformed request still gets an answer.</summary>
    public static uint U32(byte[] p, int at)
        => p is not null && at >= 0 && at + 4 <= p.Length ? BitConverter.ToUInt32(p, at) : 0u;

    public static byte U8(byte[] p, int at)
        => p is not null && at >= 0 && at < p.Length ? p[at] : (byte)0;

    /// <summary>The bytes a binary ref points at, or empty. <paramref name="refAt"/> is the
    /// payload index of the <c>[u32 frame offset][u32 count]</c> pair.</summary>
    public static byte[] Ref(byte[] p, int refAt)
    {
        if (p is null) return Array.Empty<byte>();
        uint frameOff = U32(p, refAt), count = U32(p, refAt + 4);
        if (count == 0 || frameOff < 6) return Array.Empty<byte>();
        long start = (long)frameOff - 6;                       // frame-relative -> payload index
        if (start < 0 || start + count > p.Length) return Array.Empty<byte>();
        var outp = new byte[count];
        Buffer.BlockCopy(p, (int)start, outp, 0, (int)count);
        return outp;
    }

    // --------------------------------------------------------------- builders

    /// <summary>
    /// DBS_LIST_PARCEL (0x2778). <paramref name="parcels"/> is the concatenated
    /// <c>ParcelDataNoMsg</c> records, 0x9e8 bytes each, or null/empty for an empty inbox.
    ///
    /// <para><b>The empty form is byte-exact from the decompile alone.</b> Every field is either
    /// echoed from the request (DlmId, ViewType, CurPage) or a value
    /// <c>Handler_SDB_LIST_PARCEL</c> sets before it consults the parcel manager: MaxPage is
    /// preset to 1 and ParcelCount to 0 at Arb_part_071.c:15334.
    /// The list offset slot is backpatched to the running frame length <i>unconditionally</i>,
    /// which for an empty list is exactly 35; the byte-count
    /// slot is only written inside the non-empty branch, so it stays 0. A fresh character's
    /// inbox is therefore the 35-byte frame
    /// <c>23 00 00 00 78 27 | 23 00 00 00 | 00 00 00 00 | &lt;dlm&gt; | 01 | &lt;view&gt; | &lt;page&gt; | 01 00 00 00 | 00 00 00 00</c>.</para>
    /// </summary>
    public static byte[] BuildDbsListParcel(uint dlmId, bool ok, uint viewType, uint curPage,
                                            uint maxPage, uint parcelCount, byte[]? parcels)
    {
        parcels ??= Array.Empty<byte>();
        var p = new byte[ListReplyHeader + parcels.Length];
        BitConverter.GetBytes((uint)(6 + ListReplyHeader)).CopyTo(p, ListRspBinaryRef);
        BitConverter.GetBytes((uint)parcels.Length).CopyTo(p, ListRspBinaryRef + 4);
        BitConverter.GetBytes(dlmId).CopyTo(p, ListRspDlmId);
        p[ListRspSuccess] = (byte)(ok ? 1 : 0);
        BitConverter.GetBytes(viewType).CopyTo(p, ListRspViewType);
        BitConverter.GetBytes(curPage).CopyTo(p, ListRspCurPage);
        BitConverter.GetBytes(maxPage).CopyTo(p, ListRspMaxPage);
        BitConverter.GetBytes(parcelCount).CopyTo(p, ListRspParcelCount);
        parcels.CopyTo(p, ListReplyHeader);
        return p;
    }

    /// <summary>The empty inbox: the defaults the real handler carries into the writer.</summary>
    public static byte[] BuildEmptyDbsListParcel(uint dlmId, uint viewType, uint curPage)
        => BuildDbsListParcel(dlmId, ok: true, viewType, curPage, maxPage: 1, parcelCount: 0, null);

    /// <summary>DBS_MAKE_PARCEL (0x277a).</summary>
    public static byte[] BuildDbsMakeParcel(byte[]? atoms, uint dlmId, bool ok, uint sendParcelError,
                                            uint recverDbId)
    {
        atoms ??= Array.Empty<byte>();
        var p = new byte[MakeReplyHeader + atoms.Length];
        BitConverter.GetBytes((uint)(6 + MakeReplyHeader)).CopyTo(p, MakeRspTransListRef);
        BitConverter.GetBytes((uint)atoms.Length).CopyTo(p, MakeRspTransListRef + 4);
        BitConverter.GetBytes(dlmId).CopyTo(p, MakeRspDlmId);
        p[MakeRspSuccess] = (byte)(ok ? 1 : 0);
        BitConverter.GetBytes(sendParcelError).CopyTo(p, MakeRspSendParcelError);
        BitConverter.GetBytes(recverDbId).CopyTo(p, MakeRspRecverDbId);
        atoms.CopyTo(p, MakeReplyHeader);
        return p;
    }

    /// <summary>
    /// DBS_RECV_PARCEL (0x277c). Two refs: the parcel record and the transaction atoms. They are
    /// appended in declaration order and each offset is the frame length at its own append
    /// point, which is how every other multi-ref inter-server writer in this build behaves
    /// (DBS_INIT_GUILD_DATA proved the offsets are frame-relative).
    /// </summary>
    public static byte[] BuildDbsRecvParcel(byte[]? parcelData, byte[]? atoms, uint dlmId, uint step,
                                            bool ok)
    {
        parcelData ??= Array.Empty<byte>();
        atoms ??= Array.Empty<byte>();
        var p = new byte[RecvReplyHeader + parcelData.Length + atoms.Length];
        BitConverter.GetBytes((uint)(6 + RecvReplyHeader)).CopyTo(p, RecvRspParcelDataRef);
        BitConverter.GetBytes((uint)parcelData.Length).CopyTo(p, RecvRspParcelDataRef + 4);
        BitConverter.GetBytes((uint)(6 + RecvReplyHeader + parcelData.Length)).CopyTo(p, RecvRspTransListRef);
        BitConverter.GetBytes((uint)atoms.Length).CopyTo(p, RecvRspTransListRef + 4);
        BitConverter.GetBytes(dlmId).CopyTo(p, RecvRspDlmId);
        BitConverter.GetBytes(step).CopyTo(p, RecvRspStep);
        p[RecvRspSuccess] = (byte)(ok ? 1 : 0);
        parcelData.CopyTo(p, RecvReplyHeader);
        atoms.CopyTo(p, RecvReplyHeader + parcelData.Length);
        return p;
    }

    /// <summary>
    /// DBS_RECV_PARCEL_EX (0x277e) with no record list: the step-2 shape, and the refusal.
    ///
    /// <para><b>T202.</b> The first field pair is NOT a binary ref - it is the ParcelDataList
    /// offset CHAIN (<c>[u32 count][u32 head frame offset]</c>), so an empty list writes
    /// <b>0</b> and <b>0</b>, not the running frame length. Writing 35 there made World read
    /// <i>count = 35</i>; only a zero head offset kept that from being followed.</para>
    /// </summary>
    public static byte[] BuildDbsRecvParcelEx(byte[]? a, byte[]? b, uint dlmId, uint step,
                                              uint noParcel, bool ok)
        => BuildDbsRecvParcelEx(listCount: 0, listHead: 0, a, b, dlmId, step, noParcel, ok);

    /// <summary>
    /// DBS_RECV_PARCEL_EX (0x277e). <paramref name="listCount"/> / <paramref name="listHead"/> are
    /// the ParcelDataList chain head at payload +0x00/+0x04 and <paramref name="a"/> is the chain
    /// itself (<see cref="BuildRecvParcelExRecordList"/>); <paramref name="b"/> is the atom ref at
    /// +0x08/+0x0C. <paramref name="noParcel"/> is the ParcelCount World reads to decide whether a
    /// step 2 is worth sending.
    /// </summary>
    public static byte[] BuildDbsRecvParcelEx(uint listCount, uint listHead, byte[]? a, byte[]? b,
                                              uint dlmId, uint step, uint noParcel, bool ok)
    {
        a ??= Array.Empty<byte>();
        b ??= Array.Empty<byte>();
        var p = new byte[RecvExReplyHeader + a.Length + b.Length];
        BitConverter.GetBytes(listCount).CopyTo(p, RecvExRspRefA);
        BitConverter.GetBytes(listHead).CopyTo(p, RecvExRspRefA + 4);
        BitConverter.GetBytes((uint)(6 + RecvExReplyHeader + a.Length)).CopyTo(p, RecvExRspRefB);
        BitConverter.GetBytes((uint)b.Length).CopyTo(p, RecvExRspRefB + 4);
        BitConverter.GetBytes(dlmId).CopyTo(p, RecvExRspDlmId);
        BitConverter.GetBytes(step).CopyTo(p, RecvExRspStep);
        BitConverter.GetBytes(noParcel).CopyTo(p, RecvExRspNoParcel);
        p[RecvExRspSuccess] = (byte)(ok ? 1 : 0);
        a.CopyTo(p, RecvExReplyHeader);
        b.CopyTo(p, RecvExReplyHeader + a.Length);
        return p;
    }

    /// <summary>Records a single DBS_RECV_PARCEL_EX step-1 frame carries. The real writer pages
    /// at ten; we send one page and say so rather than repeat a DlmId on a second frame.</summary>
    public const int RecvExRecordsPerFrame = 10;
    /// <summary>One chain node: <c>[u32 here][u32 next][u32 dataOffset][u32 dataLen]</c>.</summary>
    public const int RecvExNodeSize = 16;

    /// <summary>
    /// T202. The step-1 ParcelDataList: one node per parcel, each followed by its full
    /// <see cref="World.SystemParcelAttachments.ParcelRecordSize"/>-byte record. Offsets are
    /// FRAME-relative, so the first node sits at <c>6 + RecvExReplyHeader</c>.
    /// </summary>
    public static (byte[] Chain, uint Count, uint Head) BuildRecvParcelExRecordList(
        IReadOnlyList<byte[]> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        if (records.Count == 0) return (Array.Empty<byte>(), 0u, 0u);
        int rec = SystemParcelAttachments.ParcelRecordSize, stride = RecvExNodeSize + rec;
        uint first = 6 + (uint)RecvExReplyHeader;
        var chain = new byte[records.Count * stride];
        for (int i = 0; i < records.Count; i++)
        {
            if (records[i] is null || records[i].Length != rec)
                throw new ArgumentException("ParcelData record size", nameof(records));
            uint here = first + (uint)(i * stride);
            int at = i * stride;
            BitConverter.GetBytes(here).CopyTo(chain, at);
            BitConverter.GetBytes(i + 1 < records.Count ? here + (uint)stride : 0u).CopyTo(chain, at + 4);
            BitConverter.GetBytes(here + (uint)RecvExNodeSize).CopyTo(chain, at + 8);
            BitConverter.GetBytes((uint)rec).CopyTo(chain, at + 12);
            records[i].CopyTo(chain, at + RecvExNodeSize);
        }
        return (chain, (uint)records.Count, first);
    }

    /// <summary>
    /// T202. The full 0xdd8 record for one parcel, padded when the stored one is the shorter
    /// 0x9e8 form. Step 1 must hand World the long form: the attachment slots live at
    /// +0xd8 + i*0x1b0, well past 0x9e8, and they are what World builds its step-2 atoms from.
    /// </summary>
    public static byte[] FullParcelRecord(CharacterStore store, CharacterStore.ParcelRow row)
    {
        var served = ServedParcelRecord(store, row, full: true);
        if (served.Length == SystemParcelAttachments.ParcelRecordSize) return served;
        var rec = new byte[SystemParcelAttachments.ParcelRecordSize];
        Buffer.BlockCopy(served, 0, rec, 0, Math.Min(served.Length, rec.Length));
        return rec;
    }

    /// <summary>
    /// T202. The parcel ids in a request's offset chain at payload +0x00/+0x04: nodes are
    /// <c>[u32 here][u32 next][u32 parcelId]</c> and the head is frame-relative. A node that
    /// does not point at itself, repeats, or runs off the payload ends the walk - a malformed
    /// chain must not throw, because an unanswered DB request head-blocks the user.
    /// </summary>
    public static IReadOnlyList<int> ReadParcelIdChain(byte[] payload, int countAt, int headAt)
    {
        var ids = new List<int>();
        if (payload is null) return ids;
        uint count = U32(payload, countAt), next = U32(payload, headAt);
        if (count == 0 || count > (uint)(payload.Length / 12)) return ids;
        var seen = new HashSet<uint>();
        for (uint i = 0; i < count && next >= 6u; i++)
        {
            if (!seen.Add(next)) break;
            uint at = next - 6u;
            if (at + 12u > (uint)payload.Length || U32(payload, (int)at) != next) break;
            ids.Add((int)U32(payload, (int)at + 8));
            next = U32(payload, (int)at + 4);
        }
        return ids;
    }

    /// <summary>
    /// T202. The parcel ids the op-37 markers carry, at atom +0x278 (T170). This is the
    /// authoritative list on a commit: World names one marker per parcel it is collecting.
    /// </summary>
    public static IReadOnlyList<int> ParcelIdsFromAtoms(byte[] atoms)
    {
        var ids = new List<int>();
        if (atoms is null) return ids;
        int size = DbProxyHandlers.ItemAtomSize;
        for (int at = 0; at + size <= atoms.Length; at += size)
        {
            if (BitConverter.ToUInt32(atoms, at + WarehouseHandlers.AtomOp) != WarehouseHandlers.TsRecvParcel)
                continue;
            int id = BitConverter.ToInt32(atoms, at + WarehouseHandlers.AtomRecvParcelId);
            if (id > 0 && !ids.Contains(id)) ids.Add(id);
        }
        return ids;
    }

    /// <summary>DBS_RETURN_PARCEL (0x2782) and DBS_DELETE_PARCEL (0x2812) share a shape:
    /// <c>[u32 DlmId][u8 Success]</c>.</summary>
    public static byte[] BuildDbsDlmAck(uint dlmId, bool ok)
    {
        var p = new byte[ReturnReplySize];
        BitConverter.GetBytes(dlmId).CopyTo(p, 0);
        p[4] = (byte)(ok ? 1 : 0);
        return p;
    }

    // ----------------------------------------------------------------- pieces

    /// <summary>
    /// The <c>ParcelDataNoMsg</c> record for one stored parcel, 0x9e8 bytes.
    ///
    /// <para><b>Only the fields we have ever seen are filled in</b>: the receiver db id at +0x50
    /// (pinned by <c>Handler_C_SHOW_PARCEL_MESSAGE</c>'s ownership test, MAIL-WAREHOUSE.md
    /// section 2.1) and the parcel id at +0, which is what every other message keys on. The rest
    /// of the 2536-byte interior is <b>not pinned by anything we have</b> — no capture contains a
    /// parcel frame — so a parcel that World itself created is replayed from the exact bytes
    /// World gave us in <c>SDB_MAKE_PARCEL</c> (<see cref="CharacterStore.GetParcelRecord"/>)
    /// and this synthesised form is only ever used for a parcel that has no stored record.</para>
    /// </summary>
    public static byte[] BuildParcelDataNoMsg(int parcelId, int receiverDbId,
        int senderDbId = 0, string? senderName = null, string? receiverName = null,
        long money = 0, string? title = null)
    {
        var p = new byte[ParcelDataNoMsgSize];
        BitConverter.GetBytes(senderDbId).CopyTo(p, ParcelDataSenderDbId);
        WriteWString(p, ParcelDataSenderName, senderName, ParcelNameMaxChars);
        BitConverter.GetBytes(receiverDbId).CopyTo(p, ParcelDataReceiverDbId);
        WriteWString(p, ParcelDataReceiverName, receiverName, ParcelNameMaxChars);
        BitConverter.GetBytes(parcelId).CopyTo(p, ParcelDataParcelId);
        BitConverter.GetBytes(money).CopyTo(p, ParcelDataMoney);
        WriteWString(p, ParcelDataTitle, title, ParcelTitleMaxChars);
        return p;
    }

    private static void WriteWString(byte[] rec, int at, string? value, int maxChars)
    {
        if (string.IsNullOrEmpty(value)) return;
        int n = Math.Min(value!.Length, maxChars - 1);
        for (int i = 0; i < n; i++) BitConverter.GetBytes((ushort)value[i]).CopyTo(rec, at + i * 2);
    }

    // ---------------------------------------------------- ParcelData, T61
    //
    // Pinned against two real records in cap_social.log: the 3544-byte one inside
    // SDB_MAKE_PARCEL (seq 1485) and the 2536-byte "NoMsg" one DBS_LIST_PARCEL returned for the
    // same parcel (seq 1539). "Test" (db id 2) sent "two" (1002) a parcel titled "No subject"
    // with 100 money and one item (template 6560 x1).
    //
    //   +0x000 i32   SenderDbId          2 in both
    //   +0x004 wstr  SenderName          "Test"      (0x4C bytes of room)
    //   +0x050 i32   ReceiverDbId        0 in MAKE, 1002 in LIST   <- see ResolveReceiverDbId
    //   +0x054 wstr  ReceiverName        "two"
    //   +0x0A0 i32   ParcelId            0 in MAKE, 1 in LIST      (the Arbiter allocates it)
    //   +0x0D0 i64   Money               100 in both
    //   +0x960 wstr  Title               "No subject"
    //   +0x9E8 ...   message region      only in the full record; empty in this capture
    //
    // Everything between 0x0A4 and 0x0CF is timestamps and flags the Arbiter fills in on the way
    // out; MAKE carries zeroes there. status/MAIL-WAREHOUSE.md section 10.
    public const int ParcelDataSenderDbId = 0x00;
    public const int ParcelDataSenderName = 0x04;
    /// <summary>Offset of the receiver db id inside a ParcelData record.</summary>
    public const int ParcelDataReceiverDbId = 0x50;
    public const int ParcelDataReceiverName = 0x54;
    public const int ParcelDataParcelId = 0xA0;
    /// <summary>T79. +0xA4 is ParcelType: 102 for system mail, 1 for a player parcel
    /// (cap_social2 seq 417 vs 2420). World already sends it, so it is echoed, not stamped.</summary>
    public const int ParcelDataParcelType = 0xA4;
    /// <summary>T151. ParcelType of system mail (SA_MAKE_SYS_PARCEL): 102 in every system row captured
    /// (cap_social2 seq 417, cap_social4 seq 4560 x5, cap_final seq 3365).</summary>
    public const int ParcelTypeSystem = 102;

    /// <summary>T234. The one switch: <c>parcels.deleteSystemOnCollect</c>.</summary>
    public const string DeleteOnCollectVariable = "TERASHARP_PARCEL_DELETE_ON_COLLECT";

    /// <summary>
    /// T234. Remove a system reward parcel once its attachments are claimed, instead of leaving
    /// it in the mailbox as "received" forever. <b>This is OURS, not retail's.</b>
    ///
    /// <para>What retail does, pinned three ways. (1) The list is unchanged across a collect:
    /// cap_final2b 9986/9987 lists user 1003's two parcels, 10030..10033 collects parcel 12 with
    /// the full two-step, and 10062/10063 lists the same two again - same ParcelCount, same
    /// 0x13d0 byte count, same leading record. 28053/28054 lists user 1's two, 28067..28089
    /// collects BOTH (13 and 14), and 28107/28113 still lists two. (2) There is not one
    /// <c>SDB_DELETE_PARCEL</c> (0x2811) frame in the whole of cap_final2b. (3) In WorldServer
    /// the only writer of 0x2811 is <c>FUN_140cab160</c>, reached only from
    /// <c>DeleteParcelContext::ExecuteTransaction</c>, and the only live caller of that context's
    /// constructor (<c>FUN_140cad600</c>; the other two call sites are unreferenced template
    /// factories) sits inside <c>User::Handler_C_DELETE_PARCEL</c>. So the delete is the player
    /// pressing Delete, never a consequence of collecting - which is exactly what cap_social2
    /// shows from the other side: parcel 3 collected at 2372..2375, then 0x2811 at 2401 carrying
    /// that single id, then an empty inbox at 2408.</para>
    ///
    /// <para>We diverge because the UX does not survive it. A reward mail arrives per
    /// achievement, nothing on our side ever sends the delete, and the mailbox fills up with
    /// claimed rows the player cannot clear - six <c>@2051</c> rows for character 13 and seven
    /// for character 9 in the live DB, every one of them <c>is_recved = 1</c>. That is T228 D's
    /// "four reward mails every login": nothing re-creates them (arbiter-t228d.log records zero
    /// parcel writes across two logins), they are collected rows that never leave.</para>
    ///
    /// <para>Scope is deliberately narrow: <see cref="ParcelTypeSystem"/> only. A player's mail
    /// keeps retail behaviour - it stays, read, until its owner deletes it, and
    /// <c>SDB_DELETE_PARCEL</c> already handles that. A system parcel's message is a generated
    /// template (<c>@2052\x0bAchievementName\x0b@Achievement:6900</c>), not correspondence, so
    /// there is nothing to keep once the reward is in the bag.</para>
    /// </summary>
    /// <remarks>Unset means ON, and so does an unrecognised value - a typo in a config file must
    /// not be what quietly fills a player's mailbox back up. Only an explicit false turns it off.</remarks>
    public static bool DeleteSystemParcelOnCollect
        => TerasConfig.Get(DeleteOnCollectVariable)?.Trim()
            is not ("0" or "false" or "False" or "FALSE" or "no" or "off");

    /// <summary>T234. Whether this row goes away once it is collected.</summary>
    public static bool ShouldDeleteOnCollect(CharacterStore.ParcelRow? row)
        => row is not null && row.ParcelType == ParcelTypeSystem && DeleteSystemParcelOnCollect;
    /// <summary>
    /// T79. +0xA8 is the READ flag. Three captures agree: the first DBS_LIST_PARCEL that shows a
    /// parcel has it 0 and every later listing of the same parcel has it 1 - cap_social2 seq 417
    /// (0) then 429 (1), and cap_social4 seq 4580 where parcels 9/8/7/5 are 1 and the unopened 6
    /// is still 0. World sends zero; the Arbiter fills it from its own row.
    /// </summary>
    public const int ParcelDataIsRead = 0xA8;
    /// <summary>
    /// T79. +0xAC starts the parcel's creation time as SIX u16s - year, month, day, hour, minute,
    /// second - not a unix stamp: cap_social seq 1539 reads 2026/9/14 13:54:48, cap_social2 seq
    /// 417 reads 2026/9/16 19:47:03, cap_social4 seq 4560 reads 2026/9/17 01:27:43, and the
    /// matching SDB_MAKE_PARCEL carries twelve zero bytes there.
    ///
    /// <para>This is the "deletion date 2013" and "cannot claim now": we were replaying World's
    /// zeros, so the client read year 0 and everything it computes from the send date - the
    /// retention countdown and the claim delay - came out wrong. It is the one field in the
    /// record the Arbiter must own, because World does not know when the row was written.</para>
    /// </summary>
    public const int ParcelDataCreatedAt = 0xAC;
    /// <summary>The six u16 fields at <see cref="ParcelDataCreatedAt"/>.</summary>
    public const int ParcelDateFields = 6;
    public const int ParcelDataMoney = 0xD0;
    public const int ParcelDataTitle = 0x960;
    /// <summary>Longest name either field has room for, in characters.</summary>
    public const int ParcelNameMaxChars = 0x25;
    /// <summary>Longest title the 0x960 field has room for, in characters.</summary>
    public const int ParcelTitleMaxChars = 0x40;

    /// <summary>The named fields of a ParcelData record.</summary>
    public readonly record struct ParcelFields(
        int SenderDbId, string SenderName, int ReceiverDbId, string ReceiverName,
        int ParcelId, long Money, string Title);

    /// <summary>A null-terminated UTF-16LE string inside a record, bounded by maxChars.</summary>
    public static string WStringAt(byte[] rec, int at, int maxChars)
    {
        if (rec is null || at < 0) return string.Empty;
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < maxChars && at + i * 2 + 1 < rec.Length; i++)
        {
            ushort c = BitConverter.ToUInt16(rec, at + i * 2);
            if (c == 0) break;
            sb.Append((char)c);
        }
        return sb.ToString();
    }

    private static int I32(byte[] r, int at)
        => r is not null && at >= 0 && at + 4 <= r.Length ? BitConverter.ToInt32(r, at) : 0;
    private static long I64(byte[] r, int at)
        => r is not null && at >= 0 && at + 8 <= r.Length ? BitConverter.ToInt64(r, at) : 0L;

    /// <summary>Read the named fields out of a ParcelData record. Never throws on a short one.</summary>
    public static ParcelFields ParseParcelData(byte[] record) => new(
        I32(record, ParcelDataSenderDbId),
        WStringAt(record, ParcelDataSenderName, ParcelNameMaxChars),
        I32(record, ParcelDataReceiverDbId),
        WStringAt(record, ParcelDataReceiverName, ParcelNameMaxChars),
        I32(record, ParcelDataParcelId),
        I64(record, ParcelDataMoney),
        WStringAt(record, ParcelDataTitle, ParcelTitleMaxChars));

    /// <summary>
    /// T61 - the live bug. A ParcelData arriving in SDB_MAKE_PARCEL carries
    /// <b>ReceiverDbId = 0</b>: the sending client only ever knew the name it typed, and
    /// resolving it is the Arbiter's job (the same by-name lookup a party contract does).
    /// cap_social.log seq 1485 has 0 at +0x50 and "two" at +0x54; the record the Arbiter then
    /// stored and served back at seq 1539 has 1002 there.
    ///
    /// <para>Reading +0x50 straight off the request is what filed every parcel under user 0 and
    /// left the recipient's inbox empty. Returns 0 when the name resolves to nobody, which is
    /// the caller's cue to answer with a send error rather than create an orphan row.</para>
    /// </summary>
    public static int ResolveReceiverDbId(CharacterStore? store, byte[] record)
    {
        var f = ParseParcelData(record);
        if (f.ReceiverDbId > 0) return f.ReceiverDbId;
        if (store is null || f.ReceiverName.Length == 0) return 0;
        return store.GetCharacterByName(f.ReceiverName)?.Id ?? 0;
    }

    /// <summary>
    /// The 0x9e8 record to serve for one stored parcel: the exact bytes World gave us in
    /// SDB_MAKE_PARCEL when we have them, with the two fields the Arbiter - not World - owns
    /// stamped into a COPY of them.
    ///
    /// <para><b>T65, live bug.</b> In the MAKE record ParcelId (+0xA0) and ReceiverDbId (+0x50)
    /// are both 0: the sending client knows neither. T61 stored that record verbatim and T61's
    /// list served it verbatim, so every inbox row went out with parcel id 0 - the client
    /// dropped the C_SHOW_PARCEL_MESSAGE it would have sent for it, and after a relog the whole
    /// inbox rendered as N identical blank mails. The ParcelData comment above says it in so
    /// many words ("0 in MAKE, 1 in LIST"); nothing was acting on it.</para>
    /// </summary>
    public static byte[] ServedParcelRecord(CharacterStore store, CharacterStore.ParcelRow row)
        => ServedParcelRecord(store, row, full: false);

    /// <summary>
    /// T74. <paramref name="full"/> keeps the WHOLE stored record instead of truncating it to the
    /// 0x9e8 "NoMsg" stride, which is what <c>DBS_RECV_PARCEL</c> has to send.
    ///
    /// <para>The two forms are different lengths on the wire and carry different things:
    /// <c>DBS_LIST_PARCEL</c> lists 0x9e8-byte records (cap_social.log seq 1539) while
    /// <c>DBS_RECV_PARCEL</c> sends the full <b>3544</b>-byte one - the same record
    /// <c>SDB_MAKE_PARCEL</c> arrived with (seq 1485), message and attachments included, at seq
    /// 1554 and 1556. Sending the short form to a receive is what stopped the attachment ever
    /// arriving: the attachment slots live past 0x9e8, so World had nothing to build its step-2
    /// atoms from and never sent step 2.</para>
    /// </summary>
    public static byte[] ServedParcelRecord(CharacterStore store, CharacterStore.ParcelRow row, bool full)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(row);
        var stored = store.GetParcelRecord(row.ParcelId);
        int size = full && stored is not null && stored.Length > ParcelDataNoMsgSize
            ? stored.Length : ParcelDataNoMsgSize;
        var rec = new byte[size];
        if (stored is not null && stored.Length >= ParcelDataNoMsgSize)
            Buffer.BlockCopy(stored, 0, rec, 0, Math.Min(stored.Length, size));
        else
        {
            // T151: a system parcel has no World-built record to replay, so this form is all the
            // client ever sees of it. The real rows carry the receiver's name and the type too.
            BuildParcelDataNoMsg(row.ParcelId, row.ReceiverDbId, row.SenderDbId, row.SenderName,
                                 store.GetCharacter(row.ReceiverDbId)?.Name, row.Money, row.Title)
                .CopyTo(rec, 0);
            BitConverter.GetBytes(row.ParcelType).CopyTo(rec, ParcelDataParcelType);
        }

        BitConverter.GetBytes(row.ParcelId).CopyTo(rec, ParcelDataParcelId);
        BitConverter.GetBytes(row.ReceiverDbId).CopyTo(rec, ParcelDataReceiverDbId);
        // T79: and the two fields only we know - see the constants above.
        // T196: native status 2 means attachments claimed (cap_final2b 28054 -> 28068 ->
        // 28113: 0 -> 1 -> 2). A claimed system reward must not advertise itself as claimable.
        int status = row.ParcelType == ParcelTypeSystem && row.IsRecved ? 2 : row.IsRead ? 1 : 0;
        BitConverter.GetBytes(status).CopyTo(rec, ParcelDataIsRead);
        WriteRecordDate(rec, ParcelDataCreatedAt, store.GetParcelCreatedUtc(row.ParcelId));
        return rec;
    }

    /// <summary>
    /// Write a date as the six u16s the record uses. LOCAL time, because the client renders the
    /// value verbatim and the captured stamps are wall-clock times of the sessions they came from
    /// (cap_social4 seq 4560 is 01:27 on a capture taken that night). That is the one part of
    /// this field no capture pins on its own - the offset, the order and the widths all are.
    /// </summary>
    public static void WriteRecordDate(byte[] rec, int at, DateTime utc)
    {
        ArgumentNullException.ThrowIfNull(rec);
        if (at < 0 || at + ParcelDateFields * 2 > rec.Length) return;
        var t = utc.Kind == DateTimeKind.Utc ? utc.ToLocalTime() : utc;
        int[] parts = { t.Year, t.Month, t.Day, t.Hour, t.Minute, t.Second };
        for (int i = 0; i < ParcelDateFields; i++)
            BitConverter.GetBytes((ushort)parts[i]).CopyTo(rec, at + i * 2);
    }

    /// <summary>Read the six u16s back - the tests and the log both want them.</summary>
    public static (int Year, int Month, int Day, int Hour, int Minute, int Second) ReadRecordDate(
        byte[] rec, int at)
    {
        ArgumentNullException.ThrowIfNull(rec);
        if (at < 0 || at + ParcelDateFields * 2 > rec.Length) return default;
        ushort U(int i) => BitConverter.ToUInt16(rec, at + i * 2);
        return (U(0), U(1), U(2), U(3), U(4), U(5));
    }

    /// <summary>
    /// The list body for one page of a character's inbox: each parcel's stored record when we
    /// have one, the synthesised form when we do not, truncated or zero-padded to the
    /// 0x9e8 stride the writer copies with.
    /// </summary>
    public static byte[] BuildParcelList(CharacterStore store, int receiverDbId,
                                         out uint parcelCount, out uint maxPage)
        => BuildParcelList(store, receiverDbId, viewType: 0, out parcelCount, out maxPage);

    /// <summary>
    /// T202. <paramref name="viewType"/> picks the list: <b>0 is the inbox, ANY non-zero value is
    /// the Sent box</b>. <c>Handler_SDB_LIST_PARCEL</c> passes it to <c>FUN_140965bc0</c>
    /// (Arb_part_082.c:5336), which branches <c>ParcelOwner::GetRecvList</c> on zero and
    /// <c>GetSentList</c> on everything else - the client sends 1 and 0xffffffff for Sent and
    /// both must land there (cap_social2 2420 view 1 and 2442 view -1 return the same sent
    /// parcel). Ignoring it served the inbox for Sent, so every system reward also appeared in
    /// Sent; a system parcel has sender 0 and drops out of the sent query by itself
    /// (cap_final2b 52741 view 0 -> 3 rows, 52834 view -1 -> 0 rows).
    /// </summary>
    public static byte[] BuildParcelList(CharacterStore store, int userDbId, uint viewType,
                                         out uint parcelCount, out uint maxPage)
    {
        ArgumentNullException.ThrowIfNull(store);
        var rows = viewType == 0 ? store.GetParcelsFor(userDbId) : store.GetParcelsSentBy(userDbId);
        parcelCount = (uint)rows.Count;
        maxPage = 1;
        if (rows.Count == 0) return Array.Empty<byte>();

        var body = new byte[rows.Count * ParcelDataNoMsgSize];
        for (int i = 0; i < rows.Count; i++)
        {
            var rec = ServedParcelRecord(store, rows[i]);
            Buffer.BlockCopy(rec, 0, body, i * ParcelDataNoMsgSize, ParcelDataNoMsgSize);
        }
        return body;
    }
}
