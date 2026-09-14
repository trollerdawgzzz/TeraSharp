using Microsoft.Extensions.Logging;
using TeraSharp.Arbiter.Network;
using TeraSharp.Arbiter.World;

namespace TeraSharp.Arbiter.Handlers;

/// <summary>
/// The trade-broker client packets the Arbiter answers itself (T55, researched in
/// <c>status/BROKER-DESIGN.md</c>). Layouts and builders are in
/// <see cref="BrokerPackets"/>; this file is the answers.
///
/// <para><b>Everything here is the empty form.</b> There is no listings table yet - it waits for
/// a capture (BROKER-DESIGN.md section 7) - so the broker window opens, draws an empty grid, and
/// nothing hangs. That is the whole goal: before T55 the window's first packet reached World,
/// which had no handler for it, and the character's DB queue stopped
/// (status/HANDOFF.md section 1). The DB-proxy half of that fix is
/// <c>DbProxyHandlers.OnTradeBrokerRequest</c>.</para>
///
/// <para>Replies are built as raw frames rather than through <c>SendByDef</c>, for the reason
/// <see cref="ParcelHandlers"/> gives: four broker .def files are wrong (BROKER-DESIGN.md
/// section 5) and bytes built here are testable without a def registry.</para>
///
/// <para><b>Sixteen minus one.</b> Section 2.1 lists sixteen Arbiter-answered broker packets;
/// fifteen are registered here. <c>C_TRADE_BROKER_HIGHEST_ITEM_LEVEL</c> (0xEAFB) is the
/// exception - T45 already answers it in <see cref="ArbiterClientHandlers"/> and
/// <c>HandlerRegistry</c> already registers it, and <c>PacketDispatcher.Register</c> throws on a
/// duplicate.</para>
/// </summary>
public sealed class BrokerHandlers
{
    private readonly ILogger _log;

    public BrokerHandlers(ILogger log) => _log = log;

    /// <summary>
    /// The fifteen this class answers, for <c>HandlerRegistry</c>'s loop. Every opcode is
    /// <see cref="BrokerPackets"/>'; the body minimum comes from
    /// <see cref="BrokerPackets.MinBodyLength"/>, which is the decompiled handler's frame guard
    /// minus the 4-byte header.
    /// </summary>
    public static readonly (string Name, ushort Opcode)[] ClientOpcodes =
    {
        ("C_TRADE_BROKER_BOUGHT_ITEM_LIST",       BrokerPackets.C_TRADE_BROKER_BOUGHT_ITEM_LIST),
        ("C_TRADE_BROKER_SOLD_ITEM_LIST",         BrokerPackets.C_TRADE_BROKER_SOLD_ITEM_LIST),
        ("C_TRADE_BROKER_REGISTERED_ITEM_LIST",   BrokerPackets.C_TRADE_BROKER_REGISTERED_ITEM_LIST),
        ("C_TRADE_BROKER_HISTORY_ITEM_LIST_NEW",  BrokerPackets.C_TRADE_BROKER_HISTORY_ITEM_LIST_NEW),
        ("C_TRADE_BROKER_HISTORY_ITEM_LIST_PAGE", BrokerPackets.C_TRADE_BROKER_HISTORY_ITEM_LIST_PAGE),
        ("C_TRADE_BROKER_HISTORY_ITEM_LIST_SORT", BrokerPackets.C_TRADE_BROKER_HISTORY_ITEM_LIST_SORT),
        ("C_TRADE_BROKER_WAITING_ITEM_LIST_NEW",  BrokerPackets.C_TRADE_BROKER_WAITING_ITEM_LIST_NEW),
        ("C_TRADE_BROKER_WAITING_ITEM_LIST_PAGE", BrokerPackets.C_TRADE_BROKER_WAITING_ITEM_LIST_PAGE),
        ("C_TRADE_BROKER_WAITING_ITEM_LIST_SORT", BrokerPackets.C_TRADE_BROKER_WAITING_ITEM_LIST_SORT),
        ("C_TRADE_BROKER_INPUT_PRICE",            BrokerPackets.C_TRADE_BROKER_INPUT_PRICE),
        ("C_TRADE_BROKER_SUGGEST_DEAL",           BrokerPackets.C_TRADE_BROKER_SUGGEST_DEAL),
        ("C_TRADE_BROKER_DEAL_CONFIRM",           BrokerPackets.C_TRADE_BROKER_DEAL_CONFIRM),
        ("C_TRADE_BROKER_DEAL_PRICE_UPDATE",      BrokerPackets.C_TRADE_BROKER_DEAL_PRICE_UPDATE),
        ("C_TRADE_BROKER_REJECT_SUGGEST",         BrokerPackets.C_TRADE_BROKER_REJECT_SUGGEST),
        ("C_TRADE_BROKER_CLOSE",                  BrokerPackets.C_TRADE_BROKER_CLOSE),
    };

    /// <summary>The one Arbiter-answered broker packet this class does NOT register, because
    /// T45 got there first (see the class remarks).</summary>
    public const ushort AlreadyAnsweredByT45 = BrokerPackets.C_TRADE_BROKER_HIGHEST_ITEM_LEVEL;

    /// <summary>Minimum body length for <c>PacketDispatcher.Register</c> - the decompiled
    /// handler's frame guard minus the 4-byte <c>[u16 len][u16 opcode]</c> header.</summary>
    public static int MinBodyLength(ushort op) => BrokerPackets.MinBodyLength(op);

    // ------------------------------------------------------------------ replies

    /// <summary>A whole client frame: <c>[u16 totalLength][u16 opcode][body]</c>.</summary>
    public static byte[] Frame(ushort opcode, byte[] body)
    {
        var p = new byte[body.Length + BrokerPackets.ClientHeaderSize];
        p[0] = (byte)p.Length; p[1] = (byte)(p.Length >> 8);
        p[2] = (byte)opcode;   p[3] = (byte)(opcode >> 8);
        body.CopyTo(p, BrokerPackets.ClientHeaderSize);
        return p;
    }

    /// <summary>
    /// The frame each of the fifteen is answered with, or null when the real Arbiter sends
    /// nothing. Pure, so the bytes are testable without a session.
    ///
    /// <para>The four that answer nothing are the deal family. <c>C_TRADE_BROKER_DEAL_CONFIRM</c>,
    /// <c>_DEAL_PRICE_UPDATE</c> and <c>_REJECT_SUGGEST</c> all reach a
    /// <c>TradeBroker::</c> method that looks the deal up first and returns before any writer
    /// when it is not there - and with no listings there is never a deal. <c>C_TRADE_BROKER_CLOSE</c>
    /// has no client reply at all; what it does send is a frame to World, which
    /// <see cref="Handle"/> does separately.</para>
    ///
    /// <para><c>C_TRADE_BROKER_SUGGEST_DEAL</c> is answered because its failure packet IS pinned:
    /// <c>TradeBrokerOpenDealFetchWork::operator()</c> (Arb_part_084.c:6070) writes
    /// <c>S_TRADE_BROKER_REQUEST_DEAL_RESULT</c> with a single bool, and a suggestion against a
    /// listing that does not exist is exactly the false case.</para>
    /// </summary>
    public static byte[]? ReplyFor(ushort op) => op switch
    {
        BrokerPackets.C_TRADE_BROKER_BOUGHT_ITEM_LIST =>
            Frame(BrokerPackets.S_TRADE_BROKER_BOUGHT_ITEM_LIST, BrokerPackets.BuildSEmptyItemListBody()),
        BrokerPackets.C_TRADE_BROKER_REGISTERED_ITEM_LIST =>
            Frame(BrokerPackets.S_TRADE_BROKER_REGISTERED_ITEM_LIST, BrokerPackets.BuildSEmptyItemListBody()),
        BrokerPackets.C_TRADE_BROKER_SOLD_ITEM_LIST =>
            Frame(BrokerPackets.S_TRADE_BROKER_SOLD_ITEM_LIST, BrokerPackets.BuildSSoldItemListBody()),

        BrokerPackets.C_TRADE_BROKER_HISTORY_ITEM_LIST_NEW or
        BrokerPackets.C_TRADE_BROKER_HISTORY_ITEM_LIST_PAGE or
        BrokerPackets.C_TRADE_BROKER_HISTORY_ITEM_LIST_SORT =>
            Frame(BrokerPackets.S_TRADE_BROKER_HISTORY_ITEM_LIST, BrokerPackets.BuildSPagedListBody()),

        BrokerPackets.C_TRADE_BROKER_WAITING_ITEM_LIST_NEW or
        BrokerPackets.C_TRADE_BROKER_WAITING_ITEM_LIST_PAGE or
        BrokerPackets.C_TRADE_BROKER_WAITING_ITEM_LIST_SORT =>
            Frame(BrokerPackets.S_TRADE_BROKER_WAITING_ITEM_LIST, BrokerPackets.BuildSPagedListBody()),

        BrokerPackets.C_TRADE_BROKER_INPUT_PRICE =>
            Frame(BrokerPackets.S_TRADE_BROKER_INPUT_PRICE, BrokerPackets.BuildSInputPriceBody()),

        BrokerPackets.C_TRADE_BROKER_SUGGEST_DEAL =>
            Frame(BrokerPackets.S_TRADE_BROKER_REQUEST_DEAL_RESULT, BrokerPackets.BuildSFlagBody(false)),

        _ => null,       // CLOSE, DEAL_CONFIRM, DEAL_PRICE_UPDATE, REJECT_SUGGEST
    };

    // ------------------------------------------------------------------ the handler

    /// <summary>
    /// The single entry point <c>HandlerRegistry</c>'s loop calls. Always returns true: these
    /// fifteen are Arbiter-owned, so "nothing to say" must still not fall through to
    /// <c>PacketDispatcher</c>'s forward-to-World path - World answers those with
    /// "handler has not been implemented yet!!!" (status/CLIENT-REJECTS.md).
    /// </summary>
    public bool Handle(GameSession s, ushort opcode, ReadOnlyMemory<byte> body)
    {
        if (s == null) return true;

        // Closing the window is the one that talks to World rather than to the client. World
        // opened it with SA_TRADE_BROKER_OPEN (0x1457) and expects the close
        // (BROKER-DESIGN.md section 2.4); without it World believes the player is still standing
        // at the NPC.
        if (opcode == BrokerPackets.C_TRADE_BROKER_CLOSE)
        {
            Program.World?.SendFrame(BrokerPackets.AS_TRADE_BROKER_CLOSE,
                BrokerPackets.BuildAsBrokerClose((int)s.PlayerId));
            _log.LogDebug("C_TRADE_BROKER_CLOSE from {Id} - told World", s.Id);
            return true;
        }

        var reply = ReplyFor(opcode);
        if (reply == null)
        {
            _log.LogDebug("0x{Op:X4} from {Id} - Arbiter-owned, nothing to answer with while the "
                + "broker has no listings", opcode, s.Id);
            return true;
        }

        s.Send(reply);
        return true;
    }
}
