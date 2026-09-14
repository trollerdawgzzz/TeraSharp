using Microsoft.Extensions.Logging;
using TeraSharp.Arbiter.Network;

namespace TeraSharp.Arbiter.Handlers;

/// <summary>
/// The three client packets the real Arbiter answers itself for mail — T42, researched in
/// <c>status/MAIL-WAREHOUSE.md</c> section 2. Everything else the mail UI sends
/// (<c>C_LIST_PARCEL</c>, <c>C_SEND_PARCEL</c>, <c>C_RECV_PARCEL</c>, <c>C_RETURN_PARCEL</c>, …)
/// is World's and reaches us as an <c>SDB_*</c> over the World link instead.
///
/// <para><c>C_RETURN_USER_GIFT</c> looks like it belongs here and does not: it is the
/// returning-player reward claim (<c>ReturnUserManager::SendReturnUserReward</c> -> AS 0x15C0),
/// with no parcel in it at all. Return-to-sender is <c>C_RETURN_PARCEL</c> 0xF294, handled by
/// World.</para>
///
/// <para>All three replies are built as raw frames from the Arbiter's own writers rather than
/// through <c>SendByDef</c>. That is not style: <c>S_PARCEL_READ_RECV_STATUS.2.def</c> in
/// <c>tera_v100_MASTER_FINAL</c> declares only the two <c>uint32</c> counters, 8 body bytes,
/// but the writer (<c>FUN_140979060</c>, Arb_part_082.c:18805) emits two u32 <b>and a u8</b> and
/// the capture is 13 bytes. <c>SendByDef</c> would send a 12-byte frame the client cannot
/// parse. The other two defs do agree with their writers; they are built the same way for
/// consistency and so the bytes are testable without a def registry.</para>
/// </summary>
public sealed class ParcelHandlers
{
    private readonly ILogger _log;

    public ParcelHandlers(ILogger log) => _log = log;

    // ------------------------------------------------------------------ opcodes
    // From tera-server-proxy/data/data.json maps."376012", each also read out of the writer in
    // the decompile. Hard-coded rather than looked up so the builders below stay pure; the test
    // Parcel_opcodes_match_the_opcode_table checks them against data.json.

    public const ushort C_SHOW_PARCEL_MESSAGE = 0xFA59;
    public const ushort S_SHOW_PARCEL_MESSAGE = 0xABD3;
    public const ushort C_PARCEL_READ_RECV_STATUS = 0xE292;
    public const ushort S_PARCEL_READ_RECV_STATUS = 0xF26E;
    public const ushort C_PARCEL_REPORT = 0xC09A;
    public const ushort S_PARCEL_REPORT = 0x73FC;

    /// <summary>
    /// The whole frame: <c>[u16 len=13][u16 0xF26E][u32 totalUnread][u32 readUnclaimed][u8 flag]</c>.
    /// Byte-exact against <c>cap_newchar_client.log</c> frame 312, which is all zeroes:
    /// <c>0D 00 6E F2 00 00 00 00 00 00 00 00 00</c>.
    /// </summary>
    public const int ReadRecvStatusFrameSize = 13;

    // ----------------------------------------------------------------- builders

    /// <summary>
    /// S_PARCEL_READ_RECV_STATUS (0xF26E). <paramref name="flag"/> is the trailing byte the
    /// writer takes as its third argument; every call site inside the real Arbiter passes 0.
    /// </summary>
    public static byte[] BuildReadRecvStatus(uint totalUnread, uint readUnclaimed, bool flag = false)
    {
        var p = new byte[ReadRecvStatusFrameSize];
        p[0] = ReadRecvStatusFrameSize; p[1] = 0;
        p[2] = unchecked((byte)S_PARCEL_READ_RECV_STATUS); p[3] = (byte)(S_PARCEL_READ_RECV_STATUS >> 8);
        BitConverter.GetBytes(totalUnread).CopyTo(p, 4);
        BitConverter.GetBytes(readUnclaimed).CopyTo(p, 8);
        p[12] = (byte)(flag ? 1 : 0);
        return p;
    }

    /// <summary>
    /// S_SHOW_PARCEL_MESSAGE (0xABD3):
    /// <c>[u16 len][u16 op][u16 messageOffset=10][u32 parcelId][wchar message][u16 0]</c>.
    ///
    /// <para>The order is the writer's, not the def's field order: <c>FUN_1404edc40</c> reserves
    /// the u16 string slot, writes the u32 id, then backpatches the slot to the running frame
    /// length — which is always 10 — and appends the string with its terminator.</para>
    /// </summary>
    public static byte[] BuildShowParcelMessage(uint parcelId, string? message)
    {
        message ??= string.Empty;
        int textBytes = (message.Length + 1) * 2;      // UTF-16LE + terminator
        int len = 10 + textBytes;
        var p = new byte[len];
        p[0] = (byte)len; p[1] = (byte)(len >> 8);
        p[2] = unchecked((byte)S_SHOW_PARCEL_MESSAGE); p[3] = (byte)(S_SHOW_PARCEL_MESSAGE >> 8);
        p[4] = 10; p[5] = 0;                           // messageOffset, packet-absolute
        BitConverter.GetBytes(parcelId).CopyTo(p, 6);
        System.Text.Encoding.Unicode.GetBytes(message).CopyTo(p, 10);
        return p;                                      // the terminator is already 00 00
    }

    /// <summary>
    /// S_PARCEL_REPORT (0x73FC): <c>[u16 len=5][u16 op][u8 success]</c>. The real Arbiter sends
    /// 0 straight away when the parcel lookup fails and 1 later, asynchronously, out of
    /// <c>ReportManager::ResponseParcelReport</c>. We have no report pipeline, so we answer once.
    /// </summary>
    public static byte[] BuildParcelReport(bool success)
    {
        var p = new byte[5];
        p[0] = 5; p[1] = 0;
        p[2] = unchecked((byte)S_PARCEL_REPORT); p[3] = (byte)(S_PARCEL_REPORT >> 8);
        p[4] = (byte)(success ? 1 : 0);
        return p;
    }

    // ----------------------------------------------------------------- handlers

    /// <summary>
    /// C_SHOW_PARCEL_MESSAGE (0xFA59) — <c>[u32 parcelId]</c>, guard <c>frame &gt;= 8</c>.
    ///
    /// <para><b>Deliberate divergence from the original.</b> The real handler sends the message
    /// body first and only then tests <c>User+0x120 == ParcelData+0x50</c>, so on the real
    /// server any client can read any player's mail by walking parcel ids; the test only gates
    /// the read-flag. We check ownership first and send nothing when it fails.
    /// status/MAIL-WAREHOUSE.md section 2.1.</para>
    /// </summary>
    public bool OnShowParcelMessage(GameSession s, ReadOnlyMemory<byte> body)
    {
        var chr = s.SelectedCharacter;
        if (chr == null) return true;
        if (body.Length < 4)
        {
            _log.LogWarning("C_SHOW_PARCEL_MESSAGE too short ({Len} B)", body.Length);
            return true;
        }

        uint parcelId = BitConverter.ToUInt32(body.Span[..4]);
        var store = Program.Store;
        var parcel = store?.GetParcel((int)parcelId);

        if (parcel == null || parcel.ReceiverDbId != (int)chr.Id)
        {
            _log.LogInformation("C_SHOW_PARCEL_MESSAGE: parcel {Id} is not {Name}'s - ignored",
                parcelId, chr.Name);
            return true;
        }

        s.Send(BuildShowParcelMessage(parcelId, parcel.Message));

        if (!parcel.IsRead && store != null && store.SetParcelRead((int)parcelId, (int)chr.Id))
            SendReadRecvStatus(s);
        return true;
    }

    /// <summary>
    /// C_PARCEL_READ_RECV_STATUS (0xE292) — empty body, guard <c>frame &gt;= 4</c>. The real
    /// handler does nothing but call <c>ParcelManager::SendReadRecvStatusInfo(user, false)</c>.
    /// </summary>
    public bool OnParcelReadRecvStatus(GameSession s, ReadOnlyMemory<byte> body)
    {
        SendReadRecvStatus(s);
        return true;
    }

    /// <summary>
    /// C_PARCEL_REPORT (0xC09A) — <c>[u16 nameOffset][u32 parcelId][u32 reason][wchar name]</c>,
    /// guard <c>frame &gt;= 0x0E</c>. We have neither a report queue nor a moderator, so the
    /// report is logged and answered with the failure form the real Arbiter sends when the
    /// parcel lookup misses.
    /// </summary>
    public bool OnParcelReport(GameSession s, ReadOnlyMemory<byte> body)
    {
        var f = s.ReadByDef("C_PARCEL_REPORT", body);
        string target = f != null && f.TryGetValue("targetUser", out var t) ? t?.ToString() ?? "" : "";
        int parcelId = f != null && f.TryGetValue("parcelId", out var p) ? Convert.ToInt32(p) : 0;
        int reason = f != null && f.TryGetValue("reason", out var r) ? Convert.ToInt32(r) : 0;

        _log.LogInformation("C_PARCEL_REPORT: parcel {Id} against '{Target}' reason {Reason} - not forwarded (no report pipeline)",
            parcelId, target, reason);
        s.Send(BuildParcelReport(false));
        return true;
    }

    /// <summary>
    /// Push the counters. Called from the C_PARCEL_READ_RECV_STATUS handler, after a parcel is
    /// opened, and — this is the one that matters — unprompted at <c>C_LOAD_TOPO_FIN</c>, where
    /// the real Arbiter sends it from <c>User::OnLoadTopoFin</c> (Arb_part_029.c:12574). That is
    /// capture frame 312.
    /// </summary>
    public static void SendReadRecvStatus(GameSession s)
    {
        ArgumentNullException.ThrowIfNull(s);
        var chr = s.SelectedCharacter;
        var store = Program.Store;
        if (chr == null || store == null) { s.Send(BuildReadRecvStatus(0, 0)); return; }

        var (unread, unclaimed) = store.GetParcelCounts((int)chr.Id);
        s.Send(BuildReadRecvStatus((uint)unread, (uint)unclaimed));
    }
}
