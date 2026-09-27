// SPDX-License-Identifier: MIT
// Copyright (c) 2026 the TeraSharp contributors

using Microsoft.Extensions.Logging;
using TeraSharp.Arbiter.Network;

namespace TeraSharp.Arbiter.World;

// =============================================================================================
// PartyMatchManager - the manual party board ("LFG"), T78.
// Research and the frame-by-frame trace: status/PARTY-MATCH.md.
//
// WHY THIS EXISTS. Six client packets of the party-matching window were on
// HandlerRegistry's accept-silently list or, worse, still being forwarded to World, so the panel
// was permanently blank: publishing did nothing, the list was always empty, and the link button
// produced "handler has not been implemented yet!!!".
//
// WHERE IT LIVES. cap_social4 settles this. The whole episode - browse, publish, link, cancel -
// is client frames 5194..5247 of cap_social4_client.log, and the World<->Arbiter tap
// (cap_social4_ctl.txt) carries NOT ONE FRAME between 01:34:11.150 and 01:34:19.420, the window
// those client frames sit in. The board never touches World. That matches the binary: the pool
// opcodes that would leave the process are AM_/MA_ADD_TO_PARTY_MATCH_POOL (0x4655/0x4656), which
// go to the MATCHING server, not to World, and TeraSharp serves one planet with no matching
// server - so the Arbiter is the whole of it.
//
// AND IT IS RAM. PartyMatchManager in the decompile has no stored procedure of its own: the only
// party-ish SPs in the whole binary are spIssuePartyInnerId and a bounty-hunt ranking one. The
// listing is a per-User cache (User::CachePartyMatchInfo / GetCachedPartyMatchInfo /
// ClearPartyMatchInfoCache, Arb_part_028.c) plus PartyMatchManager's own table, and
// PartyMatchManager::OnLeaveWorld drops it when the leader logs out. So: a dictionary, cleared
// on leave-world, and nothing in CharacterStore.
//
// PURE, like PartyManager and GuildHandlers: every entry point answers with an
// <see cref="ArbiterActions"/> and never touches a socket, so the whole board is golden-testable
// and the human's diff is one foreach.
//
// The human-owned diff after this file:
//
//   Handlers/HandlerRegistry.cs  - remove C_REQUEST_PARTY_MATCH_INFO, C_REQUEST_MY_PARTY_MATCH_INFO
//       and C_PARTY_MATCH_WINDOW_CLOSED from the OnAcceptSilently list, and add
//
//       foreach (var (matchName, matchOp) in PartyMatchManager.ClientOpcodes)
//           Reg(matchName, PartyMatchManager.MinBodyLength(matchOp),
//               (s, body) => PartyMatchManager.OnClientPacket(s, matchOp, body));
//       PartyMatchManager.PartySize = id => PartyWiring.Manager.FindByMember(id)?.Count ?? 1;
//
//   (nothing in WorldBridge - no World frame belongs to the board; nothing in WorldEntry or
//    GameSession - the leave-world drop rides SocialHandlers.UnregisterChat, the same edge T49,
//    T51 and T76 ride.)
//
// Offsets in this file are PACKET-relative, because that is how the decompiled handlers read
// them and how the .def codec numbers its string slots; PacketDispatcher hands a handler the
// BODY, so body index = packet offset - 4.
// =============================================================================================

/// <summary>
/// The manual party board. One table per Arbiter process, keyed by the leader's character db id.
/// </summary>
public static class PartyMatchManager
{
    // =========================================================================================
    // 1. Opcodes, guards and message ids
    // =========================================================================================

    /// <summary>C_REQUEST_PARTY_MATCH_INFO (61398) - browse, with a filter.</summary>
    public const ushort C_REQUEST_PARTY_MATCH_INFO = 0xEFD6;
    /// <summary>C_REQUEST_MY_PARTY_MATCH_INFO (28262) - what am I advertising?</summary>
    public const ushort C_REQUEST_MY_PARTY_MATCH_INFO = 0x6E66;
    /// <summary>C_REGISTER_PARTY_INFO (55554) - publish.</summary>
    public const ushort C_REGISTER_PARTY_INFO = 0xD902;
    /// <summary>C_UNREGISTER_PARTY_INFO (54233) - cancel. Same body as the browse.</summary>
    public const ushort C_UNREGISTER_PARTY_INFO = 0xD3D9;
    /// <summary>C_REQUEST_PARTY_MATCH_LINK (42975) - put my listing in the chat channel.</summary>
    public const ushort C_REQUEST_PARTY_MATCH_LINK = 0xA7DF;
    /// <summary>C_PARTY_MATCH_WINDOW_CLOSED (64109) - the panel was closed.</summary>
    public const ushort C_PARTY_MATCH_WINDOW_CLOSED = 0xFA6D;

    /// <summary>S_SHOW_PARTY_MATCH_INFO (57189) - one page of the board.</summary>
    public const string S_SHOW_PARTY_MATCH_INFO = "S_SHOW_PARTY_MATCH_INFO";
    /// <summary>S_MY_PARTY_MATCH_INFO (46499).</summary>
    public const string S_MY_PARTY_MATCH_INFO = "S_MY_PARTY_MATCH_INFO";
    /// <summary>S_PARTY_MATCH_LINK (58020).</summary>
    public const string S_PARTY_MATCH_LINK = "S_PARTY_MATCH_LINK";
    /// <summary>S_SYSTEM_MESSAGE - the SMT carrier the real handlers answer with.</summary>
    public const string S_SYSTEM_MESSAGE = "S_SYSTEM_MESSAGE";

    /// <summary>SMT 997 (0x3E5): the listing was published. Arb_part_071.c:8141, and
    /// cap_social4_client frame 5221 right after the register at 5219.</summary>
    public const int SmtRegistered = 997;
    /// <summary>SMT 994 (0x3E2): the listing was withdrawn. Arb_part_072.c:1238, and
    /// cap_social4_client frame 5246 right after the unregister at 5245.</summary>
    public const int SmtUnregistered = 994;
    /// <summary>SMT 1582 (0x62E): you have no listing to link. Arb_part_072.c:596, inside
    /// PartyMatchManager::RequestPartyPR, and cap_social4_client frames 5207 and 5212 - both
    /// sent BEFORE the register at 5219.</summary>
    public const int SmtNoListingToLink = 1582;

    /// <summary>
    /// The chat channel S_PARTY_MATCH_LINK names. <c>PartyMatchManager::BroadcastPartyPR</c>
    /// (Arb_part_071.c:10081) uses 0x14 as the ChatType in all three of its branches - the
    /// S_CANNOT_USE_CHAT_CHANNEL it sends when the channel is blocked carries the same 0x14 - and
    /// 20 is what client frame 5226 has in the slot the def calls <c>unk2</c>.
    /// </summary>
    public const int PartyMatchChatChannel = 20;

    /// <summary>A client packet's <c>[u16 length][u16 opcode]</c> header.</summary>
    public const int ClientHeaderSize = 4;

    /// <summary>
    /// The six client packets the board owns, in the order HandlerRegistry should register them.
    /// </summary>
    public static readonly (string Name, ushort Opcode)[] ClientOpcodes =
    {
        ("C_REQUEST_PARTY_MATCH_INFO",    C_REQUEST_PARTY_MATCH_INFO),
        ("C_REQUEST_MY_PARTY_MATCH_INFO", C_REQUEST_MY_PARTY_MATCH_INFO),
        ("C_REGISTER_PARTY_INFO",         C_REGISTER_PARTY_INFO),
        ("C_UNREGISTER_PARTY_INFO",       C_UNREGISTER_PARTY_INFO),
        ("C_REQUEST_PARTY_MATCH_LINK",    C_REQUEST_PARTY_MATCH_LINK),
        ("C_PARTY_MATCH_WINDOW_CLOSED",   C_PARTY_MATCH_WINDOW_CLOSED),
    };

    /// <summary>
    /// The minimum FRAME length each real handler enforces - the number in its
    /// <c>if (local_res18[0] &lt; N)</c> guard, which is also what it reports as
    /// GET_CLIENT_BUFFER_BUFSIZE_MISMATCH.
    /// <code>
    ///   C_REQUEST_PARTY_MATCH_INFO     Arb_part_041.c:8952   &lt; 0x14
    ///   C_REQUEST_MY_PARTY_MATCH_INFO  Arb_part_041.c:8719   &lt; 4
    ///   C_REGISTER_PARTY_INFO          Arb_part_041.c:6053   &lt; 7
    ///   C_UNREGISTER_PARTY_INFO        Arb_part_041.c:13513  &lt; 0x14
    ///   C_REQUEST_PARTY_MATCH_LINK     Arb_part_041.c:9051   no guard
    ///   C_PARTY_MATCH_WINDOW_CLOSED    Arb_part_041.c:5159   no guard
    /// </code>
    /// </summary>
    public static int MinFrameLength(ushort op) => op switch
    {
        C_REQUEST_PARTY_MATCH_INFO => 0x14,
        C_UNREGISTER_PARTY_INFO => 0x14,
        C_REGISTER_PARTY_INFO => 0x07,
        C_REQUEST_MY_PARTY_MATCH_INFO => ClientHeaderSize,
        _ => 0,
    };

    /// <summary>
    /// Minimum BODY length, which is what <c>PacketDispatcher.Register</c> compares. The
    /// decompile's guards are FRAME lengths, so this is that minus the four-byte header -
    /// registering the frame figure would drop every packet the board actually receives.
    /// </summary>
    public static int MinBodyLength(ushort op)
    {
        int frame = MinFrameLength(op);
        return frame <= ClientHeaderSize ? 0 : frame - ClientHeaderSize;
    }

    /// <summary>Is this one of the six?</summary>
    public static bool IsArbiterSide(ushort op)
    {
        foreach (var (_, code) in ClientOpcodes) if (code == op) return true;
        return false;
    }

    // =========================================================================================
    // 2. The board
    // =========================================================================================

    /// <summary>One published listing, exactly the fields S_SHOW_PARTY_MATCH_INFO carries.</summary>
    public sealed record Listing(int LeaderId, string LeaderName, bool IsRaid, string Message);

    private static readonly object Gate = new();
    private static readonly Dictionary<int, Listing> Board = new();
    private static readonly HashSet<int> WindowOpen = new();
    private static ILogger Log = Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;

    /// <summary>
    /// How many players are in this leader's party, for the <c>playerCount</c> column. Pluggable
    /// so the board does not have to know PartyManager exists; the wiring passes
    /// <c>id =&gt; PartyWiring.Manager.FindByMember(id)?.Count ?? 1</c>. A solo advertiser counts
    /// as 1, which is what cap_social4_client frame 5222 carries.
    /// </summary>
    public static Func<int, int>? PartySize { get; set; }

    /// <summary>Give the board the process logger, the way PartyWiring.UsePartyLogger does.</summary>
    public static void UseLogger(ILogger log) => Log = log ?? Log;

    /// <summary>Everything on the board right now, in publication order. Tests and the browse.</summary>
    public static IReadOnlyList<Listing> All()
    {
        lock (Gate) return new List<Listing>(Board.Values);
    }

    /// <summary>This character's listing, or null.</summary>
    public static Listing? Find(int leaderId)
    {
        lock (Gate) return Board.TryGetValue(leaderId, out var l) ? l : null;
    }

    /// <summary>How many listings are live. Tests and the log line.</summary>
    public static int Count { get { lock (Gate) return Board.Count; } }

    /// <summary>Throw the board away. Tests only.</summary>
    public static void Reset()
    {
        lock (Gate) { Board.Clear(); WindowOpen.Clear(); }
    }

    private static int SizeOf(int leaderId)
    {
        int n = PartySize?.Invoke(leaderId) ?? 1;
        return n < 1 ? 1 : n;
    }

    // =========================================================================================
    // 3. Parsers. Packet-relative offsets, read the way the decompiled handlers read them.
    // =========================================================================================

    /// <summary>
    /// The browse filter. <b>C_UNREGISTER_PARTY_INFO carries this same body</b>, not the one
    /// <c>C_UNREGISTER_PARTY_INFO.1.def</c> declares: Handler_C_UNREGISTER_PARTY_INFO
    /// (Arb_part_041.c:13507) and Handler_C_REQUEST_PARTY_MATCH_INFO (Arb_part_041.c:8950) read
    /// the identical six fields and queue the identical job (FUN_140837540). The shipped
    /// unregister def declares <c>int32 unk1</c> where the real packet has a two-byte string
    /// offset and a two-byte field, and has no string ref at all - another wrong def in the class
    /// of the patch-101 S_FRIEND_LIST and the ten guild ones.
    /// </summary>
    public readonly record struct Filter(int Unk1, int MinLevel, int MaxLevel, int Unk2, int Unk3, string Purpose);

    /// <summary>
    /// Parse the browse/cancel body. Null when it is shorter than the handler's 0x14 frame guard.
    /// <code>
    ///   [04] u16 purpose offset    param_2[2], bounds-checked against the packet length
    ///   [06] u16 unk1              param_2[3]
    ///   [08] u16 minLevel          param_2[4]
    ///   [10] u16 maxLevel          param_2[5]
    ///   [12] i32 unk2              *(int *)(param_2 + 6)
    ///   [16] i32 unk3              *(int *)(param_2 + 8)
    /// </code>
    /// cap_social4_client frames 5194 / 5220 / 5245 are all
    /// <c>14 00 00 00 0F 00 19 00 03 00 00 00 00 00 00 00 00 00</c>: purpose at packet 20 (the
    /// empty string that ends the body), unk1 0, levels 15..25, unk2 3, unk3 0.
    /// </summary>
    public static Filter? ParseFilter(ReadOnlySpan<byte> body)
    {
        if (body.Length < MinBodyLength(C_REQUEST_PARTY_MATCH_INFO)) return null;
        return new Filter(
            BitConverter.ToUInt16(body[2..]),
            BitConverter.ToUInt16(body[4..]),
            BitConverter.ToUInt16(body[6..]),
            BitConverter.ToInt32(body[8..]),
            BitConverter.ToInt32(body[12..]),
            ReadWString(body, BitConverter.ToUInt16(body)));
    }

    /// <summary>What C_REGISTER_PARTY_INFO carries.</summary>
    public readonly record struct Publish(bool IsRaid, string Message);

    /// <summary>
    /// Parse the publish body. Null when it is shorter than the handler's 7-byte frame guard.
    /// <code>
    ///   [04] u16 message offset    param_2[2]
    ///   [06] u8  isRaid            (char)param_2[3]
    /// </code>
    /// cap_social4_client frame 5219 is <c>07 00 00 33 00 32 00 31 00 00 00</c>: the message
    /// starts at packet 7, isRaid 0, message "321".
    /// </summary>
    public static Publish? ParsePublish(ReadOnlySpan<byte> body)
    {
        if (body.Length < MinBodyLength(C_REGISTER_PARTY_INFO)) return null;
        return new Publish(body[2] != 0, ReadWString(body, BitConverter.ToUInt16(body)));
    }

    /// <summary>
    /// A null-terminated UTF-16LE string at a PACKET-relative offset, read out of the BODY.
    /// An offset of 0, or one outside the body, is the codec's "no string" and the real
    /// handler's bounds check, which rejects an offset of 0 and any offset the packet length at
    /// packet+0 is less than or equal to.
    /// </summary>
    public static string ReadWString(ReadOnlySpan<byte> body, int packetOffset)
    {
        int at = packetOffset - ClientHeaderSize;
        if (packetOffset <= 0 || at < 0 || at >= body.Length) return string.Empty;
        var sb = new System.Text.StringBuilder();
        for (int i = at; i + 1 < body.Length; i += 2)
        {
            ushort c = BitConverter.ToUInt16(body[i..]);
            if (c == 0) break;
            sb.Append((char)c);
        }
        return sb.ToString();
    }

    // =========================================================================================
    // 4. Builders - the field sets, named exactly as the shipped defs name them
    // =========================================================================================

    /// <summary>
    /// S_SHOW_PARTY_MATCH_INFO (0xDF65), one page. The shipped
    /// <c>S_SHOW_PARTY_MATCH_INFO.1.def</c> is the only version and it is RIGHT: its element is
    /// <c>[u16 here][u16 next][u16 message][u16 leader][i32 leaderId][u8 isRaid][i16 playerCount]</c>
    /// = 15 bytes, which is exactly the stride of cap_social4_client frame 5222 (element at
    /// packet 12, strings from packet 27).
    /// <para>One page: the real board pages with C_REQUEST_PARTY_MATCH_INFO_PAGE, which this
    /// build never sends because pageCount is 0 - the value frame 5222 carries.</para>
    /// </summary>
    public static Dictionary<string, object> BuildShowFields(IReadOnlyList<Listing> listings)
    {
        var rows = new List<object>();
        foreach (var l in listings ?? Array.Empty<Listing>())
            rows.Add(new Dictionary<string, object>
            {
                ["leaderId"] = l.LeaderId,
                ["isRaid"] = l.IsRaid,
                ["playerCount"] = SizeOf(l.LeaderId),
                ["message"] = l.Message,
                ["leader"] = l.LeaderName,
            });
        return new Dictionary<string, object>
        {
            ["pageCurrent"] = 0,
            ["pageCount"] = 0,
            ["listings"] = rows,
        };
    }

    /// <summary>
    /// S_MY_PARTY_MATCH_INFO (0xB5A3). The writer is
    /// <c>PartyMatchManager::SendPartyPRText(User *, bool, const wchar_t *)</c>
    /// (Arb_part_072.c:2490) and its caller <c>RequestMyPartyInfo</c> (Arb_part_072.c:523) decides
    /// the bool: <b>it is "do I have a listing", not isRaid</b> - the no-listing branch passes
    /// <c>0</c> with the empty string and the other passes <c>1</c> with the stored message.
    /// cap_social4_client frames 5199 and 5216 are both <c>07 00 00 00 00</c>, the no-listing form.
    /// </summary>
    public static Dictionary<string, object> BuildMyInfoFields(Listing? mine)
        => new()
        {
            ["unk"] = mine != null,
            ["message"] = mine?.Message ?? string.Empty,
        };

    /// <summary>
    /// S_PARTY_MATCH_LINK (0xE2A4). Writer: the
    /// <c>SendToSession&lt;PKT_S_PARTY_MATCH_LINK_WRITE, int&amp;, wchar_t*, const wchar_t*&amp;, bool, bool&amp;, int&gt;</c>
    /// at Arb_part_071.c:226, which writes
    /// <c>[u16 name][u16 message][i32 id][u8][u8][i32]</c> then the two strings - the shipped
    /// <c>S_PARTY_MATCH_LINK.2.def</c> exactly. The first bool is a by-value literal at the call
    /// site and is 1 in cap_social4_client frame 5226; the second is the listing's isRaid byte
    /// (match info + 0x98); the int is <see cref="PartyMatchChatChannel"/>.
    /// </summary>
    public static Dictionary<string, object> BuildLinkFields(Listing mine)
    {
        ArgumentNullException.ThrowIfNull(mine);
        return new Dictionary<string, object>
        {
            ["id"] = mine.LeaderId,
            ["unk"] = (byte)1,
            ["raid"] = mine.IsRaid,
            ["unk2"] = PartyMatchChatChannel,
            ["name"] = mine.LeaderName,
            ["message"] = mine.Message,
        };
    }

    /// <summary>An SMT-only S_SYSTEM_MESSAGE, the bare <c>@id</c> form the capture carries.</summary>
    public static Dictionary<string, object> BuildSmtFields(int id)
        => new() { ["message"] = "@" + id.ToString(System.Globalization.CultureInfo.InvariantCulture) };

    // =========================================================================================
    // 5. The pure core - one ArbiterActions per client packet
    // =========================================================================================

    private static ArbiterActions New(int playerId)
        => new() { Origin = Recipient.Player(playerId) };

    /// <summary>
    /// C_REGISTER_PARTY_INFO. Answers with SMT 997 and NOTHING ELSE - the client refreshes the
    /// board itself, which is why cap_social4_client has exactly one S_SHOW_PARTY_MATCH_INFO
    /// (5222) for the C_REQUEST_PARTY_MATCH_INFO the client sent at 5220, and none for the
    /// register at 5219.
    /// </summary>
    public static ArbiterActions OnRegister(int playerId, string leaderName, Publish p)
    {
        var a = New(playerId);
        if (playerId <= 0) return a.Reject("no character");
        var listing = new Listing(playerId, leaderName ?? string.Empty, p.IsRaid, p.Message ?? string.Empty);
        lock (Gate) Board[playerId] = listing;
        a.ToPlayer(playerId, S_SYSTEM_MESSAGE, BuildSmtFields(SmtRegistered));
        Log.LogInformation("party match: {Name} published {What} listing - {Msg} ({N} on the board)",
            listing.LeaderName, listing.IsRaid ? "a raid" : "a party", listing.Message, Count);
        return a;
    }

    /// <summary>
    /// C_UNREGISTER_PARTY_INFO. SMT 994 and then a refreshed page - the handler queues the
    /// unregister job AND the send-page job (Arb_part_041.c:13519 and :13528), and
    /// cap_social4_client shows exactly that pair at 5246 / 5247 with no request from the client
    /// in between.
    /// </summary>
    public static ArbiterActions OnUnregister(int playerId, Filter filter)
    {
        var a = New(playerId);
        if (playerId <= 0) return a.Reject("no character");
        bool had;
        lock (Gate) had = Board.Remove(playerId);
        a.ToPlayer(playerId, S_SYSTEM_MESSAGE, BuildSmtFields(SmtUnregistered));
        a.ToPlayer(playerId, S_SHOW_PARTY_MATCH_INFO, BuildShowFields(Matching(filter)));
        Log.LogInformation("party match: player {Id} withdrew ({Had}), {N} left on the board",
            playerId, had ? "had a listing" : "had none", Count);
        return a;
    }

    /// <summary>T201: the QA command calls RequestUnregisterPartyInfo alone
    /// (Arb044:7196; Arb072:1221-1246), without the client's subsequent page job.</summary>
    internal static ArbiterActions OnQaUnregister(int playerId)
    {
        var a = New(playerId);
        bool removed;
        lock (Gate) removed = Board.Remove(playerId);
        if (removed) a.ToPlayer(playerId, S_SYSTEM_MESSAGE, BuildSmtFields(SmtUnregistered));
        return a;
    }

    /// <summary>T201: Arb040:7829 -> Arb072:481-509; success has no reply.</summary>
    internal static ArbiterActions OnChangePr(int playerId, string message)
    {
        var a = New(playerId);
        lock (Gate)
        {
            if (Board.TryGetValue(playerId, out var old)) Board[playerId] = old with { Message = message };
            else a.ToPlayer(playerId, S_SYSTEM_MESSAGE, BuildSmtFields(1003));
        }
        return a;
    }

    /// <summary>C_REQUEST_PARTY_MATCH_INFO - one page of whatever passes the filter.</summary>
    public static ArbiterActions OnRequestInfo(int playerId, Filter filter)
    {
        var a = New(playerId);
        lock (Gate) WindowOpen.Add(playerId);
        a.ToPlayer(playerId, S_SHOW_PARTY_MATCH_INFO, BuildShowFields(Matching(filter)));
        return a;
    }

    /// <summary>C_REQUEST_MY_PARTY_MATCH_INFO.</summary>
    public static ArbiterActions OnRequestMyInfo(int playerId)
    {
        var a = New(playerId);
        a.ToPlayer(playerId, S_MY_PARTY_MATCH_INFO, BuildMyInfoFields(Find(playerId)));
        return a;
    }

    /// <summary>
    /// C_REQUEST_PARTY_MATCH_LINK. With a listing, S_PARTY_MATCH_LINK; without one, SMT 1582 -
    /// cap_social4_client frames 5207 and 5212 (before the register) against 5226 and 5229
    /// (after it).
    ///
    /// <para><b>The real one broadcasts.</b> The job is
    /// <c>PartyMatchManager::BroadcastPartyPR(int, User *, const wchar_t *, bool)</c> and it puts
    /// the link in chat channel 20 for everyone in it, with a per-user cool time (cool-time slot
    /// 0x13; the third press in the capture, frame 5230, got S_MUTE instead). Sending it to the
    /// requester alone is what the capture proves and what a one-client tap can prove; the
    /// broadcast and the cool time are in status/PARTY-MATCH.md section 6, not here.</para>
    /// </summary>
    public static ArbiterActions OnRequestLink(int playerId)
    {
        var a = New(playerId);
        var mine = Find(playerId);
        if (mine == null)
        {
            a.ToPlayer(playerId, S_SYSTEM_MESSAGE, BuildSmtFields(SmtNoListingToLink));
            return a;
        }
        a.ToPlayer(playerId, S_PARTY_MATCH_LINK, BuildLinkFields(mine));
        return a;
    }

    /// <summary>
    /// C_PARTY_MATCH_WINDOW_CLOSED. The real handler is four lines and sends nothing
    /// (Arb_part_041.c:5159 -&gt; FUN_140379be0(user)); it drops the per-user cache
    /// User::ClearPartyMatchInfoCache. The listing SURVIVES - closing the window is not
    /// withdrawing - so this clears only the open-window flag.
    /// </summary>
    public static ArbiterActions OnWindowClosed(int playerId)
    {
        lock (Gate) WindowOpen.Remove(playerId);
        return New(playerId);
    }

    /// <summary>
    /// Leave world or drop the connection: the listing goes, because
    /// <c>PartyMatchManager::OnLeaveWorld</c> is a real method and nothing persists the board.
    /// Returns true when there was one, so the caller can log it.
    /// </summary>
    public static bool OnLeaveWorld(int playerId)
    {
        lock (Gate) { WindowOpen.Remove(playerId); return Board.Remove(playerId); }
    }

    /// <summary>
    /// The filter, applied - which today means not at all, on purpose.
    ///
    /// <para>The filter carries a level window (15..25 in every captured frame) but
    /// <b>a listing has no level</b>: S_SHOW_PARTY_MATCH_INFO's element is leaderId, isRaid,
    /// playerCount and the two strings, and nothing else, so there is nothing on the board to
    /// compare the window against. The real Arbiter matches it against the LEADER's level,
    /// which it has because it holds the User; this class deliberately holds no character
    /// state. Dropping the filter shows a few extra rows; applying a made-up one hides rows the
    /// player asked for. <c>unk1</c>, <c>unk2</c>, <c>unk3</c> and <c>purpose</c> are parsed and
    /// carried so a later pass has them, and are not guessed at here -
    /// see status/PARTY-MATCH.md section 4.</para>
    /// </summary>
    private static List<Listing> Matching(Filter f)
    {
        _ = f;
        return new List<Listing>(All());
    }

    // =========================================================================================
    // 6. The wiring - one entry point, the shape HandlerRegistry's foreach wants
    // =========================================================================================

    private static WorldBridge? Bridge => global::TeraSharp.Arbiter.Program.World;

    /// <summary>
    /// An ActionDispatcher bound to live sessions. The board addresses clients by character db id
    /// only, so the ticket lookup exists purely so the origin still resolves when World is not up.
    /// <c>RelayRejections</c> is off: the real handlers answer with numbered SMTs, never with an
    /// S_SYSTEM_MESSAGE_CUSTOM literal, and this file emits those SMTs itself.
    /// </summary>
    internal static ActionDispatcher Dispatcher(GameSession? origin, ILogger log)
    {
        var world = Bridge;
        int originId = (int)(origin?.SelectedCharacter?.Id ?? 0);
        return new ActionDispatcher(
            _ => Sink(origin),
            p => Sink(world?.SessionForPlayerId(p) ?? (p == originId ? origin : null)),
            (op, payload) => { if (world == null) return false; world.SendFrame(op, payload); return true; },
            log)
        { RelayRejections = false };
    }

    private static IClientSink? Sink(GameSession? s) => s == null ? null : new SessionSinkAdapter(s);

    private sealed class SessionSinkAdapter : IClientSink
    {
        private readonly GameSession _s;
        public SessionSinkAdapter(GameSession s) => _s = s;
        public void SendByDef(string packetName, IReadOnlyDictionary<string, object> fields) => _s.SendByDef(packetName, fields);
        public void SendRawBody(string packetName, byte[] body) => _s.SendRawBody(packetName, body);
        public void Send(byte[] framedPacket) => _s.Send(framedPacket);
    }

    /// <summary>
    /// One of <see cref="ClientOpcodes"/> arrived. Always true: all six are Arbiter-owned, so
    /// "nothing to do" must still not fall through to PacketDispatcher's forward-to-World path -
    /// World answers those with "handler has not been implemented yet!!!".
    /// </summary>
    public static bool OnClientPacket(GameSession? session, ushort opcode, ReadOnlyMemory<byte> body)
    {
        var chr = session?.SelectedCharacter;
        if (chr == null) return true;
        var actions = Decide((int)chr.Id, chr.Name, opcode, body.Span);
        if (actions.IsEmpty && actions.Rejected == null) return true;
        Dispatcher(session, Log).Dispatch(actions, "party-match");
        return true;
    }

    /// <summary>
    /// The session-free half, so every test drives this code and not a copy.
    /// </summary>
    public static ArbiterActions Decide(int playerId, string name, ushort opcode, ReadOnlySpan<byte> body)
    {
        switch (opcode)
        {
            case C_REGISTER_PARTY_INFO:
            {
                var p = ParsePublish(body);
                return p == null
                    ? New(playerId).Reject("C_REGISTER_PARTY_INFO shorter than its 7-byte guard")
                    : OnRegister(playerId, name, p.Value);
            }
            case C_UNREGISTER_PARTY_INFO:
            {
                var f = ParseFilter(body);
                return f == null
                    ? New(playerId).Reject("C_UNREGISTER_PARTY_INFO shorter than its 0x14 guard")
                    : OnUnregister(playerId, f.Value);
            }
            case C_REQUEST_PARTY_MATCH_INFO:
            {
                var f = ParseFilter(body);
                return f == null
                    ? New(playerId).Reject("C_REQUEST_PARTY_MATCH_INFO shorter than its 0x14 guard")
                    : OnRequestInfo(playerId, f.Value);
            }
            case C_REQUEST_MY_PARTY_MATCH_INFO: return OnRequestMyInfo(playerId);
            case C_REQUEST_PARTY_MATCH_LINK: return OnRequestLink(playerId);
            case C_PARTY_MATCH_WINDOW_CLOSED: return OnWindowClosed(playerId);
            default: return New(playerId);
        }
    }
}
