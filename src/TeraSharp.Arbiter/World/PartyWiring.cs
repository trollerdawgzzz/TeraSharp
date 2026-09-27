// SPDX-License-Identifier: MIT
// Copyright (c) 2026 the TeraSharp contributors

using Microsoft.Extensions.Logging;
using TeraSharp.Arbiter.Network;

namespace TeraSharp.Arbiter.World;

// =============================================================================================
// PartyWiring - the plumbing that turns T35's pure PartyManager into a live subsystem (T49).
// Research: status/PARTY-DESIGN.md section 10.
//
// PartyManager is two total functions over in-memory state; it never touches a socket, never
// looks up a session, and does not know what a GameSession is. That is what makes the whole
// invite -> accept -> leave -> dismiss sequence a golden test, and it is also what left the
// human with a twenty-line paste in HandlerRegistry and another in WorldBridge.
//
// This file is that paste, on the Cowork side of the line. After it, the human-owned diff is:
//
//   HandlerRegistry.RegisterAll  - one foreach over PartyWiring.ClientOpcodes
//   WorldBridge.HandleFrame      - one line at the top of the default: arm
//   (nothing in WorldEntry or GameSession - party registration rides the chat-roster path,
//    SocialHandlers.RegisterChat / UnregisterChat, which both call sites already have.)
//
// See status/PARTY-DESIGN.md section 11.5 for the exact text of those two, and 11.1 for every
// way section 10 no longer matches the tree.
//
// Three things the T35 plan assumed that are no longer true, and one it got wrong:
//
//   1. WorldBridge.Party / WorldBridge.Actions are NOT needed. Both would be new members on a
//      human-owned file. The manager and the dispatcher live here instead, built lazily the same
//      way SocialHandlers.Chat is (T47), so nothing has to be threaded through Program.cs.
//   2. AllocateTunnelKey no longer returns a constant 5 - T38's TicketAllocator hands out a real
//      per-session Ticket - so section 10's "Order of operations" prerequisite is satisfied and
//      this can actually be wired.
//   3. ResolveDef is not needed for parties. The guild and chat wirings need it because ten (and
//      several) of their .def files are wrong; every def PartyManager emits resolves correctly
//      out of the shipped registry, which is what
//      Party_end_to_end_client_packets_go_through_the_real_def_codec already proves. Passing a
//      null hook keeps that the one place party bytes are decided.
//   4. Section 10 registers all seven C_ opcodes straight through to OnClientPacket, but
//      PartyManager's switch has no case for C_REQUEST_PARTY_INFO - it is the party-MATCH query
//      (Handler_C_REQUEST_PARTY_INFO, Arb_part_041.c:8899, queues a PartyMatchManager job), and
//      section 10's "What is not" drops party matching outright. Registered as-written it would
//      answer every request with an S_SYSTEM_MESSAGE_CUSTOM rejection. See NotModelled.
// =============================================================================================

/// <summary>
/// Owns the process-wide <see cref="PartyManager"/> and performs its sends. Everything here is
/// static: there is exactly one party table per Arbiter process, the same way there is exactly
/// one chat roster.
/// </summary>
public static class PartyWiring
{
    // The one place this file reaches outside itself. Fully qualified because the enclosing
    // namespace is TeraSharp.Arbiter.World and "World" is also the property name.
    private static WorldBridge? Bridge => global::TeraSharp.Arbiter.Program.World;

    // =========================================================================================
    // 1. The manager
    // =========================================================================================

    private static readonly object Gate = new();
    private static PartyManager? _manager;
    private static ILogger PartyLog = Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;

    /// <summary>
    /// The process-wide party table. Built lazily so nothing has to be constructed in Program.cs,
    /// and so a test can take it, use it and drop it again.
    /// </summary>
    public static PartyManager Manager
    {
        get
        {
            lock (Gate) return _manager ??= new PartyManager(PartyLog);
        }
    }

    /// <summary>Give the party manager a real logger. Called once, from the SocialHandlers ctor,
    /// alongside <c>UseChatLogger</c> - the two subsystems share the roster and the sink.</summary>
    public static void UsePartyLogger(ILogger log)
    {
        lock (Gate) { PartyLog = log; _manager = null; }
    }

    /// <summary>Throw the party table away. Tests only: every other caller wants the state to
    /// survive, which is the whole point of a party.</summary>
    internal static void ResetForTests()
    {
        lock (Gate) _manager = null;
    }

    // =========================================================================================
    // 2. Opcodes and lengths - the data HandlerRegistry's loop reads
    // =========================================================================================

    /// <summary>
    /// The seven client packets the real Arbiter owns and answers itself
    /// (status/PARTY-DESIGN.md section 6.1). Everything else the party UI sends - C_LEAVE_PARTY
    /// 0xFFB6, C_CHANGE_PARTY_MANAGER 0x60D6, C_EXTEND_PARTY, C_SWAP_PARTY, the vote packets -
    /// has no Arbiter handler at all and must keep going to World, which answers with an SA_.
    /// </summary>
    public static readonly (string Name, ushort Opcode)[] ClientOpcodes =
    {
        ("C_APPLY_PARTY",              PartyPackets.C_APPLY_PARTY),                 // 0xA889
        ("C_PARTY_APPLICATION_DENIED", PartyManager.C_PARTY_APPLICATION_DENIED),    // 0x4F00
        ("C_DISMISS_PARTY",            PartyPackets.C_DISMISS_PARTY),               // 0xC8B9
        ("C_BAN_PARTY_MEMBER",         PartyPackets.C_BAN_PARTY_MEMBER),            // 0x59C1
        ("C_PARTY_LOOTING_METHOD",     PartyPackets.C_PARTY_LOOTING_METHOD),        // 0x5D24
        ("C_MERGE_PARTY_TO_RAID",      PartyPackets.C_MERGE_PARTY_TO_RAID),         // 0xB8D0
        ("C_REQUEST_PARTY_INFO",       PartyPackets.C_REQUEST_PARTY_INFO),          // 0xFD35
        ("C_RESET_ALL_DUNGEON",        PartyPackets.C_RESET_ALL_DUNGEON),           // 0x5867
    };

    /// <summary>
    /// Minimum BODY length for one of <see cref="ClientOpcodes"/>, which is what
    /// <c>PacketDispatcher.Register</c> takes - it hands the handler <c>packet[4..]</c> and
    /// compares that length. <b>The decompile's guards are FRAME lengths</b>
    /// (<c>if (local_res18[0] &lt; 0xc)</c> compares the total packet size, header included), so
    /// every number here is that guard minus the 4-byte <c>[u16 len][u16 opcode]</c> header.
    /// Registering the frame figure instead would silently drop every short-bodied party packet -
    /// C_DISMISS_PARTY carries no body at all.
    /// <code>
    ///   C_APPLY_PARTY              FUN_1404db920  Arb_part_040.c:19121  &lt; 8      -> 4
    ///   C_PARTY_APPLICATION_DENIED FUN_1404e3f60  Arb_part_041.c:5023   &lt; 8      -> 4
    ///   C_DISMISS_PARTY            FUN_1404def50  Arb_part_041.c:1463   no guard -> 0
    ///   C_BAN_PARTY_MEMBER         FUN_1404dbcb0  Arb_part_040.c:19255  &lt; 0xC    -> 8
    ///   C_PARTY_LOOTING_METHOD     FUN_1404e40a0  Arb_part_041.c:5086   &lt; 0x17   -> 0x13
    ///   C_MERGE_PARTY_TO_RAID      FUN_1404e3870  Arb_part_041.c:4724   &lt; 0xD    -> 9
    ///   C_REQUEST_PARTY_INFO       FUN_1404e9b50  Arb_part_041.c:8911   &lt; 8      -> 4
    /// </code>
    /// </summary>
    public static int MinBodyLength(ushort op)
    {
        int frame = MinFrameLength(op);
        return frame <= ClientHeaderSize ? 0 : frame - ClientHeaderSize;
    }

    /// <summary>A client packet's <c>[u16 length][u16 opcode]</c> header. The decompile's guards
    /// include it; <c>PacketDispatcher</c>'s do not.</summary>
    public const int ClientHeaderSize = 4;

    /// <summary>
    /// The minimum FRAME length each real handler enforces, exactly as the decompile writes it -
    /// the number in the <c>if (local_res18[0] &lt; N)</c> guard, which is also the number it
    /// reports back as <c>GET_CLIENT_BUFFER_BUFSIZE_MISMATCH</c>. 0 for anything that is not one
    /// of the seven. <see cref="MinBodyLength"/> is this minus
    /// <see cref="ClientHeaderSize"/>, and that subtraction is the whole point of this pair.
    /// </summary>
    public static int MinFrameLength(ushort op) => op switch
    {
        PartyPackets.C_APPLY_PARTY => 0x08,
        PartyManager.C_PARTY_APPLICATION_DENIED => 0x08,
        PartyPackets.C_DISMISS_PARTY => ClientHeaderSize,   // no guard at all: header-only packet
        PartyPackets.C_BAN_PARTY_MEMBER => 0x0C,
        PartyPackets.C_PARTY_LOOTING_METHOD => 0x17,
        PartyPackets.C_MERGE_PARTY_TO_RAID => 0x0D,
        PartyPackets.C_REQUEST_PARTY_INFO => 0x08,
        PartyPackets.C_RESET_ALL_DUNGEON => ClientHeaderSize,
        _ => 0,
    };

    /// <summary>Is this one of the seven the Arbiter owns?</summary>
    public static bool IsArbiterSide(ushort op)
    {
        foreach (var (_, code) in ClientOpcodes) if (code == op) return true;
        return false;
    }

    /// <summary>
    /// Arbiter-owned, registered so it never reaches World - but deliberately unanswered.
    /// <c>C_REQUEST_PARTY_INFO</c> is the party-MATCH lookup: the real handler queues a
    /// <c>PartyMatchManager::DoAsyncJob</c> that answers with S_PARTY_MEMBER_INFO (0xBEC0), and
    /// none of the three shipped <c>S_PARTY_MEMBER_INFO.def</c> versions matches the v100 writer
    /// (status/PARTY-DESIGN.md section 6.3), so T35 dropped party matching entirely.
    ///
    /// <para>That leaves three possible behaviours, and only one of them is harmless:
    /// forwarding to World gets "handler has not been implemented yet!!!" (T45's whole point);
    /// handing it to <see cref="PartyManager.OnClientPacket"/> falls through to the switch's
    /// default and answers the player with an S_SYSTEM_MESSAGE_CUSTOM rejection for pressing a
    /// button; swallowing it leaves the party-match panel blank, which is what it already is.
    /// So: swallowed, with a debug line.</para>
    ///
    /// <para><b>T78 did not change this.</b> The manual party BOARD - publish, browse, link,
    /// cancel - is six other opcodes and is now answered by
    /// <see cref="PartyMatchManager"/> (status/PARTY-MATCH.md). C_REQUEST_PARTY_INFO is the
    /// other half, the candidate list behind S_PARTY_MEMBER_INFO, and its three shipped defs
    /// still do not match the v100 writer - so it stays here.</para>
    /// </summary>
    public static readonly IReadOnlySet<ushort> NotModelled =
        new HashSet<ushort> { PartyPackets.C_REQUEST_PARTY_INFO };

    /// <summary>
    /// Is this a World -&gt; Arbiter party frame? True for exactly the twelve <c>SA_</c> opcodes in
    /// <see cref="PartyPackets.MinFrameLength"/> (0x1395..0x139D, 0x13AB, 0x13AC and
    /// SA_BYPASS_TO_GROUP 0x13F8) and nothing else, which is what makes it a safe gate in
    /// <c>WorldBridge.HandleFrame</c>'s <c>default:</c> arm. None of the twelve appears in
    /// <c>DbProxyHandlers</c> or in <c>WorldReplayTable</c>, so nothing is being taken away from
    /// either - today they fall through to "no replay for 0x1395".
    /// </summary>
    public static bool HandlesWorldFrame(ushort op) => PartyPackets.MinFrameLength(op) != 0;

    // =========================================================================================
    // 3. The dispatcher
    // =========================================================================================

    /// <summary>
    /// An <see cref="ActionDispatcher"/> bound to live sessions. Parties address clients by tunnel
    /// a stable local recipient key (the character id), not a per-World tunnel ticket,
    /// so the player-id lookup exists purely so a rejection can still reach
    /// <paramref name="origin"/> when World is not up - standalone mode, and every unit test.
    /// <c>ResolveDef</c> is null on purpose; see the header.
    /// </summary>
    internal static ActionDispatcher Dispatcher(GameSession? origin, ILogger log)
    {
        var world = Bridge;
        uint originTicket = origin == null ? 0 : RecipientKey(origin);
        bool haveOrigin = origin != null;
        return new ActionDispatcher(
            t => Sink((Manager.TryGetPlayer(t, out var player) ? world?.SessionForPlayerId(player.UserDbId) : null)
                ?? (haveOrigin && t == originTicket ? origin : null)),
            p => Sink(world?.SessionForPlayerId(p)),
            (op, payload) => SendWorldAction(world, op, payload),
            log);
    }

    private static bool SendWorldAction(WorldBridge? world, ushort op, byte[] payload)
    {
        if (world == null) return false;
        return RouteWorldAction(op, payload, world.HasLinks,
            playerId => world.SessionForPlayerId(playerId)?.CurrentWorldId, world.SendFrame);
    }

    internal static bool RouteWorldAction(ushort op, byte[] payload, Func<int, bool> hasLinks,
        Func<int, int?> worldForPlayer, Action<int, ushort, byte[]> send)
    {
        if (op == PartyPackets.AS_RESET_ALL_DUNGEON
            || op == PartyPackets.AS_DO_CREATE_PARTY
            || op == PartyPackets.AS_DO_ADD_PARTY_MEMBER
            || op == PartyPackets.AS_DO_REMOVE_PARTY_MEMBER
            || op == PartyPackets.AS_DO_DISMISS_PARTY
            || op == PartyPackets.AS_DO_EXTEND_PARTY
            || op == PartyPackets.AS_DO_SWAP_PARTY
            || op == PartyPackets.AS_DO_SET_PARTY_MANAGER
            || op == PartyPackets.AS_DO_CHANGE_PARTY_MEMBER_AUTHORITY
            || op == PartyPackets.AS_DO_SET_LOOTING_METHOD
            || op == PartyPackets.AS_DO_SET_PARTY_OWNER
            || op == PartyPackets.AS_DISMISS_PARTY
            || op == PartyPackets.AS_PARTY_LOOTING_METHOD
            || op == PartyPackets.AS_BAN_PARTY_MEMBER)
        {
            // Broadcast once per registered World, not per bypass socket. Reset:
            // Arb041:10100-10114; remove: Arb067:8245-8260; dismiss: Arb066:18102-18124.
            // Creation: Arb079:15465-15491; cap_2man_b:12571/12572,18232/18234.
            // cap_2man_b:19038/19039 and19048/19049 reach World0 and World13.
            // T195: vote requests also broadcast (Arb041:1489-1535,5113-5137;
            // Arb040:19290-19314). Mirrors update every World (Arb067:5364-5389).
            bool sent = false;
            for (int id = 0; id < WorldRegistration.MaxWorldId; id++)
                if (hasLinks(id)) { send(id, op, payload); sent = true; }
            return sent;
        }
        if (op == PartyPackets.AS_CHANGE_EVENT_MATCHING_STATE)
        {
            // This is deliberately World0, even for users inside a dungeon World:
            // cap_2man_b:17291/17292 and19036/19037. FUN_14056dbe0 is
            // GetDataSessionByServerId(int) (Arb046:2704-2729); admission, formation,
            // and leave call it with literal0 (Arb076:15129,8270; Arb079:3261; Arb077:6151).
            if (payload.Length < 5 || !hasLinks(WorldRegistration.DefaultWorldId)) return false;
            send(WorldRegistration.DefaultWorldId, op, payload);
            return true;
        }
        if (op == PartyPackets.AS_NOTIFY_ABOUT_SYS_PARTY_WITHDRAWAL
            || op == PartyPackets.AS_REQUEST_REFRESH_PARTY_INFO)
        {
            // Leave job resolves the departing user's current World session:
            // Arb_part_082.c:15914-15925; cap_2man_b:19061 goes only to World13,
            // registered on link#54 at11532 (World0 is link#26 at10299).
            // Refresh also resolves the user's World (Arb067:13616-13639), with just
            // UserDbId at payload0 instead of the withdrawal packet's PDId.
            int userOffset = op == PartyPackets.AS_REQUEST_REFRESH_PARTY_INFO ? 0 : 4;
            if (payload.Length < userOffset + 4
                || worldForPlayer(BitConverter.ToInt32(payload, userOffset)) is not { } id)
                return false;
            send(id, op, payload);
            return true;
        }
        send(WorldRegistration.DefaultWorldId, op, payload);
        return true;
    }

    /// <summary>
    /// T208d. Replay every live party to a World that has just connected - our half of
    /// <c>PartyManager::OnConnectWorldServer</c>; the decompile is on
    /// <see cref="PartyManager.BuildWorldConnectReplay"/>.
    ///
    /// <para>Unicast, not broadcast: retail's function is literally
    /// <c>Party::UnicastPartyInfoToSpecialWorldServer(int)</c>, and the live mirror path
    /// (<see cref="RouteWorldAction"/>) already reaches every World for parties that form later.
    /// The frames go out through <see cref="WorldBridge.SendFrame(int, ushort, byte[])"/>, the same
    /// per-World send that path uses, so the replay lands on whichever link of that World would
    /// have carried the original.</para>
    ///
    /// <para>Returns how many frames went out - 0 when no party exists, which is the normal case
    /// for the first World to come up.</para>
    /// </summary>
    public static int ReplayToWorld(int worldId)
    {
        var world = Bridge;
        if (world == null) return 0;
        var frames = Manager.BuildWorldConnectReplay();
        foreach (var (op, payload) in frames) world.SendFrame(worldId, op, payload);
        if (frames.Count > 0)
            PartyLog.LogInformation(
                "party: world {W} connected while {N} party/parties existed - replayed {F} mirror frame(s)",
                worldId, frames.Count / 2, frames.Count);
        return frames.Count;
    }

    private static IClientSink? Sink(GameSession? s) => s == null ? null : new SessionSinkAdapter(s);

    // T195: native party membership uses PDId, not bypass slot. cap_instance1
    // 136991/137332 ->146976/147327 swaps tickets5/6 between users10/9.
    // This key is internal to PartyManager/ActionDispatcher and never goes on the wire.
    private static uint RecipientKey(GameSession session) => session.SelectedCharacter?.Id ?? session.PlayerId;

    /// <summary>ActionDispatcher's sink over a real session: the same three methods, same
    /// signatures. The twin of SocialHandlers' adapter; both are private to their file because
    /// neither is worth a public type.</summary>
    private sealed class SessionSinkAdapter : IClientSink
    {
        private readonly GameSession _s;
        public SessionSinkAdapter(GameSession s) => _s = s;
        public void SendByDef(string packetName, IReadOnlyDictionary<string, object> fields) => _s.SendByDef(packetName, fields);
        public void SendRawBody(string packetName, byte[] body) => _s.SendRawBody(packetName, body);
        public void Send(byte[] framedPacket) => _s.Send(framedPacket);
    }

    // =========================================================================================
    // 4. The roster - called from SocialHandlers.RegisterChat / UnregisterChat
    // =========================================================================================

    /// <summary>
    /// Tell the party manager this character is online, so a Ticket resolves to a character and a
    /// member who logged back in goes Online again in whatever party still holds them. Idempotent.
    ///
    /// <para>Gated on <c>InWorld</c>. The local recipient token is stable across transfers;
    /// World's bypass tickets are a separate namespace and can collide across Worlds.</para>
    /// </summary>
    public static void Register(GameSession? session)
    {
        var chr = session?.SelectedCharacter;
        if (chr == null || !session!.InWorld) return;
        Register(RecipientKey(session!), (int)chr.Id, chr.Name, chr.Level, chr.Class, chr.Race,
                 chr.Gender, session!.GameId, session.CurrentWorldId);
    }

    /// <summary>The session-free half, so the tests can build a roster without a socket.</summary>
    internal static void Register(uint ticket, int userDbId, string name, int level, int cls,
                                  int race, int gender, ulong gameId, int worldId = 0)
        => Manager.Register(new PartyManager.PartyPlayer(
            Ticket: ticket, UserDbId: userDbId, Name: name, Level: level, Class: cls, Race: race,
            Gender: gender, GameId: gameId, WorldId: worldId));

    /// <summary>
    /// Leave world or drop the connection. The party SURVIVES - PartyMemberInfo+0x70 is an Online
    /// flag, not a removal - so <see cref="PartyManager.Unregister"/> marks the slot offline and
    /// returns the S_LOGOUT_PARTY_MEMBER announcements for the others, which have to be dispatched
    /// rather than discarded.
    /// </summary>
    public static void Unregister(GameSession? session)
    {
        if (session == null) return;
        var actions = Manager.Unregister(RecipientKey(session));
        if (actions.IsEmpty && actions.Rejected == null) return;
        Dispatcher(session, PartyLog).Dispatch(actions, "party-leave");
    }

    /// <summary>World has finished loading a party member. Retail asks that user's World
    /// to refresh party data, after SA_ENTER_WORLD rather than during registration:
    /// cap_2man_b raw13913+15 ->14041; Arb029:12364 ->Arb079:14553-14570
    /// ->Arb067:9181/13616-13639. Ordinary and system parties both take this path.</summary>
    internal static bool OnWorldEntryComplete(WorldBridge? world, ulong arbiterUser)
    {
        var session = world?.PlayerForGameId(arbiterUser);
        var character = session?.SelectedCharacter;
        if (session == null || !session.InWorld || character == null
            || Manager.FindByMember((int)character.Id) == null
            || !world!.HasLinks(session.CurrentWorldId)) return false;
        return RouteWorldAction(PartyPackets.AS_REQUEST_REFRESH_PARTY_INFO,
            PartyPackets.BuildAsRequestRefreshPartyInfo((int)character.Id), world.HasLinks,
            id => id == character.Id ? session.CurrentWorldId : null, world.SendFrame);
    }

    /// <summary>
    /// T138d. The matcher put a group together - make them a party and push it to World and to
    /// every member. Returns the actions so a test can read them; the dispatch has already
    /// happened. A rejection is logged rather than thrown: a match that cannot become a party
    /// still gave everyone their FIN, and dropping the party is better than dropping the match.
    /// </summary>
    public static PartyActions FormMatchedParty(
        IReadOnlyList<PartyManager.MatchedMember> members, bool raid, int dungeonId,
        int battleFieldId = 0, int teamIndex = 0, IReadOnlyList<byte[]>? matchFrames = null)
    {
        var actions = Manager.FormMatchedParty(members, raid, dungeonId, battleFieldId, teamIndex,
            matchFrames);
        if (actions.Rejected != null)
            PartyLog.LogWarning("match-party: {Why}", actions.Rejected);
        else if (!actions.IsEmpty)
            Dispatcher(null, PartyLog).Dispatch(actions, "match-party");
        return actions;
    }

    /// <summary>
    /// Register every in-world session. Cheap and idempotent, and it is what makes a party work
    /// for a player who entered world before this code did - the same self-heal
    /// <c>SocialHandlers.SyncChatRoster</c> performs for whisper.
    /// </summary>
    public static void SyncRoster()
    {
        var world = Bridge;
        if (world == null) return;
        foreach (var s in world.InWorldSessions()) Register(s);
    }

    internal static void SendChat(GameSession sender, uint channel, string message)
    {
        SyncRoster();
        Register(sender);
        var store = global::TeraSharp.Arbiter.Program.Store;
        var actions = Manager.Chat(RecipientKey(sender), channel, message,
            (recipient, from) => store?.GetBlocks(recipient).Contains(from) == true);
        Dispatcher(sender, PartyLog).Dispatch(actions, "party-chat");
    }

    // =========================================================================================
    // 5. The two entry points the human's two lines call
    // =========================================================================================

    /// <summary>
    /// One of <see cref="ClientOpcodes"/> arrived. Returns true always: these seven are
    /// Arbiter-owned, so "we could not do anything with it" still must not fall through to
    /// <c>PacketDispatcher</c>'s forward-to-World path.
    /// </summary>
    public static bool OnClientPacket(GameSession? session, ushort opcode, ReadOnlyMemory<byte> body)
    {
        if (session == null) return true;
        if (NotModelled.Contains(opcode))
        {
            PartyLog.LogDebug("party: 0x{Op:X4} is Arbiter-owned but not modelled - swallowed ({Len} B body)",
                opcode, body.Length);
            return true;
        }

        SyncRoster();
        Register(session);
        DispatchClientPacket(Manager, Dispatcher(session, PartyLog), RecipientKey(session), opcode, body.ToArray());
        return true;
    }

    /// <summary>
    /// A World frame arrived. Returns true when it was a party frame and has been dealt with, so
    /// the caller can <c>return</c>; false leaves it to DbProxy and the replay table exactly as
    /// before. The gate is <see cref="HandlesWorldFrame"/>, so a non-party opcode never even
    /// reaches the manager.
    /// </summary>
    public static bool TryHandleWorldFrame(ushort opcode, byte[] payload)
    {
        if (opcode == PartyPackets.SA_EQUIP_ITEM_LEVEL)
        {
            // T180's handle convention: our AS_ENTER_WORLD carries GameId as the opaque
            // Arbiter handle. Native captures carry pointers; neither is a database id.
            Manager.ObserveEquipItemLevel(payload,
                handle => Bridge?.PlayerForGameId(handle)?.SelectedCharacter is { } c ? (int)c.Id : null);
            return true;
        }
        if (!HandlesWorldFrame(opcode)) return false;
        DispatchWorldFrame(Manager, Dispatcher(null, PartyLog), opcode, payload);
        // T161: leaving (or being voted out of) the party a match formed is how a member turns
        // the offer down - there is no decline packet in either binary's opcode list.
        int gone = opcode switch
        {
            PartyPackets.SA_LEAVE_PARTY => PartyPackets.ParseSaLeaveParty(payload)?.MemberDbId ?? 0,
            PartyPackets.SA_KICK_PARTY => PartyPackets.ParseSaKickParty(payload)?.TargetDbId ?? 0,
            _ => 0,
        };
        if (gone > 0) MatchWiring.OnLeftParty(gone);
        return true;
    }

    // =========================================================================================
    // 6. The cores. Static and parameterised so the tests exercise this code and not a copy.
    // =========================================================================================

    /// <summary>
    /// <c>OnClientPacket</c> -&gt; dispatch. A rejection reaches the sender as
    /// S_SYSTEM_MESSAGE_CUSTOM because <see cref="PartyManager.OnClientPacket"/> stamps
    /// <c>Origin = Recipient.Ticket(ticket)</c> and the dispatcher relays it by default.
    /// </summary>
    internal static DispatchResult DispatchClientPacket(
        PartyManager manager, ActionDispatcher dispatcher, uint ticket, ushort opcode, byte[] body)
        => dispatcher.Dispatch(manager.OnClientPacket(ticket, opcode, body), "party");

    /// <summary>
    /// <c>OnWorldFrame</c> -&gt; dispatch. The action set carries <c>Origin = Recipient.None</c>,
    /// so a rejection here goes to the log and not to a client, which is right: a World frame has
    /// no originating session, and four of the twelve gated opcodes (SA_SWAP_PARTY,
    /// SA_CHANGE_PARTY_MEMBER_AUTHORITY, SA_JOIN_PARTY_IN_ARBITER, SA_MERGE_PARTY_TO_RAID) are
    /// gated but not yet modelled, so they land there every time.
    /// </summary>
    internal static DispatchResult DispatchWorldFrame(
        PartyManager manager, ActionDispatcher dispatcher, ushort opcode, byte[] payload)
        => dispatcher.Dispatch(manager.OnWorldFrame(opcode, payload), "party");

    /// <summary><c>Unregister</c> -&gt; dispatch, for a roster the tests drive directly.</summary>
    internal static DispatchResult DispatchLeave(
        PartyManager manager, ActionDispatcher dispatcher, uint ticket)
        => dispatcher.Dispatch(manager.Unregister(ticket), "party-leave");
}
