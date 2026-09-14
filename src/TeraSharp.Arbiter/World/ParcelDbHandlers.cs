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
    //   u32 uVar3   = *(u32*)(request + 6)             DlmId, echoed
    //   u8  uVar11                                     Success
    //   u32 uVar4   = *(u32*)(request + 0xe)           ViewType, echoed
    //   u32 uVar10  = *(u32*)(request + 0x12)          CurPage, echoed
    //   u32 uVar9   = local_ec                         MaxPage
    //   u32 uVar8   = local_f0                         ParcelCount
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
    /// size its bounds check tests (<c>if (local_d0 &lt; *local_d8 + 0x9e8)</c>).</summary>
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
    /// <c>Handler_SDB_LIST_PARCEL</c> sets before it consults the parcel manager:
    /// <c>local_ec = 1</c> (MaxPage) and <c>local_f0 = 0</c> (ParcelCount) at Arb_part_071.c:15334.
    /// The list offset slot is backpatched to the running frame length <i>unconditionally</i>
    /// (<c>*local_c8 = *local_d8;</c>), which for an empty list is exactly 35; the byte-count
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

    /// <summary>DBS_RECV_PARCEL_EX (0x277e).</summary>
    public static byte[] BuildDbsRecvParcelEx(byte[]? a, byte[]? b, uint dlmId, uint step,
                                              uint noParcel, bool ok)
    {
        a ??= Array.Empty<byte>();
        b ??= Array.Empty<byte>();
        var p = new byte[RecvExReplyHeader + a.Length + b.Length];
        BitConverter.GetBytes((uint)(6 + RecvExReplyHeader)).CopyTo(p, RecvExRspRefA);
        BitConverter.GetBytes((uint)a.Length).CopyTo(p, RecvExRspRefA + 4);
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
    public static byte[] BuildParcelDataNoMsg(int parcelId, int receiverDbId)
    {
        var p = new byte[ParcelDataNoMsgSize];
        BitConverter.GetBytes(parcelId).CopyTo(p, 0);
        BitConverter.GetBytes(receiverDbId).CopyTo(p, 0x50);
        return p;
    }

    /// <summary>Offset of the receiver db id inside a ParcelData record.</summary>
    public const int ParcelDataReceiverDbId = 0x50;

    /// <summary>
    /// The list body for one page of a character's inbox: each parcel's stored record when we
    /// have one, the synthesised form when we do not, truncated or zero-padded to the
    /// 0x9e8 stride the writer copies with.
    /// </summary>
    public static byte[] BuildParcelList(CharacterStore store, int receiverDbId,
                                         out uint parcelCount, out uint maxPage)
    {
        ArgumentNullException.ThrowIfNull(store);
        var rows = store.GetParcelsFor(receiverDbId);
        parcelCount = (uint)rows.Count;
        maxPage = 1;
        if (rows.Count == 0) return Array.Empty<byte>();

        var body = new byte[rows.Count * ParcelDataNoMsgSize];
        for (int i = 0; i < rows.Count; i++)
        {
            var stored = store.GetParcelRecord(rows[i].ParcelId);
            var rec = stored is not null && stored.Length >= ParcelDataNoMsgSize
                ? stored
                : BuildParcelDataNoMsg(rows[i].ParcelId, rows[i].ReceiverDbId);
            Buffer.BlockCopy(rec, 0, body, i * ParcelDataNoMsgSize,
                             Math.Min(rec.Length, ParcelDataNoMsgSize));
        }
        return body;
    }
}
