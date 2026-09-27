// SPDX-License-Identifier: MIT
// Copyright (c) 2026 the TeraSharp contributors

using Microsoft.Extensions.Logging;
using TeraSharp.Arbiter.Handlers;
using TeraSharp.Arbiter.Network;
using TeraSharp.Arbiter.Persistence;

namespace TeraSharp.Arbiter.World;

// =============================================================================================
// GuildWiring - the plumbing that turns T39's pure GuildHandlers into a live subsystem (T51).
// Research: status/GUILD-DESIGN.md section 10, and section 11 for what changed since.
//
// The twin of World/PartyWiring.cs (T49), and deliberately the same shape, because the two
// subsystems have the same problem: a pure handler that answers an input with a LIST of things
// to send, and no idea what a GameSession is. What is different is the address space - parties
// address clients by tunnel Ticket, guilds by character db id - and that guilds NEED
// ActionDispatcher.ResolveDef, because ten of their .def files are wrong (GUILD-DESIGN.md
// section 5.5). Without it a handler emitting S_ADD_GUILD_MEMBER puts the .def's 0x33-byte body
// on the wire instead of the 0x38 the Arbiter's own dumper guard proves.
//
// After this file the human-owned diff is:
//
//   HandlerRegistry.RegisterAll  - one foreach over GuildWiring.ClientOpcodes
//   (nothing in WorldEntry, GameSession or WorldBridge - guild roster membership rides the
//    chat-roster path, SocialHandlers.RegisterChat / UnregisterChat, and the guild boot load is
//    a DbProxyHandlers allow-list entry, not a WorldBridge case.)
//
// Section 10's diff also asked for GameSession.ByPlayerId. Not needed: the human added
// WorldBridge.SessionForPlayerId for T47, and that is the authoritative in-world table.
// =============================================================================================

/// <summary>
/// Owns the process-wide <see cref="GuildHandlers"/>, performs its sends, and builds the
/// <c>SDB_INIT_GUILD</c> boot load from rows. Static, because there is one guild table per
/// Arbiter process.
/// </summary>
public static class GuildWiring
{
    // The one place this file reaches outside itself. Fully qualified because the enclosing
    // namespace is TeraSharp.Arbiter.World and "World" is also the property name.
    private static WorldBridge? Bridge => global::TeraSharp.Arbiter.Program.World;
    private static CharacterStore? Store => global::TeraSharp.Arbiter.Program.Store;

    // =========================================================================================
    // 1. The handler layer
    // =========================================================================================

    private static readonly object Gate = new();
    private static GuildHandlers? _guilds;
    private static CharacterStore? _guildStore;
    private static ILogger GuildLog = Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;

    /// <summary>
    /// The process-wide guild handler. Built lazily because <c>Program.Store</c> is not open when
    /// this class is first touched, and rebuilt if the store is replaced (which only happens in
    /// tests) - the same lifecycle <c>SocialHandlers.Chat</c> has.
    /// </summary>
    public static GuildHandlers Guilds
    {
        get
        {
            var store = Store;
            lock (Gate)
            {
                if (_guilds == null || !ReferenceEquals(_guildStore, store))
                {
                    _guildStore = store;
                    _guilds = new GuildHandlers(store!, GuildLog);
                }
                return _guilds;
            }
        }
    }

    /// <summary>Give the guild handler a real logger. Called once, from the SocialHandlers ctor,
    /// next to UseChatLogger and UsePartyLogger - the three share the roster and the sink.</summary>
    public static void UseGuildLogger(ILogger log)
    {
        lock (Gate) { GuildLog = log; _guilds = null; }
    }

    /// <summary>Drop the handler so the next caller rebuilds it against the current store.
    /// Tests only.</summary>
    internal static void ResetForTests()
    {
        lock (Gate) { _guilds = null; _guildStore = null; }
    }

    // =========================================================================================
    // 2. Opcodes and lengths - the data HandlerRegistry's loop reads
    // =========================================================================================

    /// <summary>
    /// The <b>nineteen</b> client packets <see cref="GuildHandlers"/> answers - every
    /// Arbiter-side guild packet in GUILD-DESIGN.md section 5.1, and exactly the set
    /// <see cref="GuildHandlers.Handles"/> returns true for (test
    /// T51_the_registered_set_is_exactly_what_GuildHandlers_answers).
    ///
    /// <para>T52 added the last two. <c>C_INVITE_USER_TO_GUILD</c> (0xEF92) was waiting on a
    /// session lookup that now exists, and <c>C_CHANGE_GUILDNAME</c> (0xFC1C) turned out not to
    /// need the SDB_ASK_CHANGE_GUILD_NAME round trip at all - the only thing that trip decides is
    /// uniqueness, which the SQL UNIQUE index already answers (section 12.2).</para>
    ///
    /// <para>The ten World-side guild packets of section 5.2 (C_LEAVE_GUILD 0x7C83,
    /// C_BANISH_GUILD_MEMBER, C_DESTROY_GUILD, C_CHANGE_GUILD_CHIEF, the four guild-group ones,
    /// C_CHECK_NEW_GUILDNAME, C_REQUEST_USABLE_GUILD_NAME) must stay unregistered: World answers
    /// them, and answering one here double-answers the client.</para>
    /// </summary>
    public static readonly (string Name, ushort Opcode)[] ClientOpcodes =
    {
        ("C_REQUEST_GUILD_INFO",                   GuildPackets.C_REQUEST_GUILD_INFO),
        ("C_REQUEST_GUILD_MEMBER_LIST",            GuildPackets.C_REQUEST_GUILD_MEMBER_LIST),
        ("C_GET_GUILD_HISTORY",                    GuildPackets.C_GET_GUILD_HISTORY),
        ("C_GUILD_APPLY_LIST",                     GuildPackets.C_GUILD_APPLY_LIST),
        ("C_GUILD_APPLY_LIST_PAGE",                GuildPackets.C_GUILD_APPLY_LIST_PAGE),
        ("C_GET_USER_GUILD_LOGO",                  GuildPackets.C_GET_USER_GUILD_LOGO),
        ("C_UPDATE_GUILD_LOGO",                    GuildPackets.C_UPDATE_GUILD_LOGO),
        ("C_UPDATE_GUILD_TITLE",                   GuildPackets.C_UPDATE_GUILD_TITLE),
        ("C_SET_GUILD_JOIN_CONDITION",             GuildPackets.C_SET_GUILD_JOIN_CONDITION),
        ("C_REQUEST_COOLTIME_TO_JOIN_GUILD",       GuildPackets.C_REQUEST_COOLTIME_TO_JOIN_GUILD),
        ("C_REQUEST_GUILD_INFO_BEFORE_APPLY_GUILD", GuildPackets.C_REQUEST_GUILD_INFO_BEFORE_APPLY_GUILD),
        ("C_CHECK_CHANGE_GUILDNAME",               GuildPackets.C_CHECK_CHANGE_GUILDNAME),
        ("C_APPLY_GUILD",                          GuildPackets.C_APPLY_GUILD),
        ("C_ACCEPT_GUILD_APPLY",                   GuildPackets.C_ACCEPT_GUILD_APPLY),
        ("C_REJECT_INVITE_USER_TO_GUILD",          GuildPackets.C_REJECT_INVITE_USER_TO_GUILD),
        ("C_INVITE_USER_TO_GUILD",                 GuildPackets.C_INVITE_USER_TO_GUILD),
        ("C_CHANGE_GUILDNAME",                     GuildPackets.C_CHANGE_GUILDNAME),
        ("C_REQUEST_UPDATE_ANNOUNCE",              GuildHandlers.C_REQUEST_UPDATE_ANNOUNCE),
        ("C_REQUEST_UPDATE_INTRODUCE",             GuildHandlers.C_REQUEST_UPDATE_INTRODUCE),
        // T135, the guild-quest board. Registration is this table plus GuildPackets
        // .MinClientLength - HandlerRegistry loops it and needs no change of its own.
        ("C_REQUEST_START_GUILD_QUEST",            GuildPackets.C_REQUEST_START_GUILD_QUEST),
        ("C_REQUEST_FINISH_GUILD_QUEST",           GuildPackets.C_REQUEST_FINISH_GUILD_QUEST),
        ("C_REQUEST_CANCEL_GUILD_QUEST",           GuildPackets.C_REQUEST_CANCEL_GUILD_QUEST),
    };

    /// <summary>A client packet's <c>[u16 length][u16 opcode]</c> header. The decompile's guards
    /// include it; <c>PacketDispatcher</c>'s do not.</summary>
    public const int ClientHeaderSize = 4;

    /// <summary>
    /// The minimum FRAME length the real handler enforces. Fifteen of the seventeen come
    /// straight from <see cref="GuildPackets.MinClientLength"/>; the two that do not are
    /// C_REQUEST_UPDATE_ANNOUNCE and C_REQUEST_UPDATE_INTRODUCE, whose names contain no GUILD
    /// and so were missed by T36's sweep - both are <c>[u16 off]</c> + wstring, min total 6
    /// (Handler_C_REQUEST_UPDATE_ANNOUNCE / _INTRODUCE, Arb_part_040.c:6664 / :6579).
    /// </summary>
    public static int MinFrameLength(ushort op) => op switch
    {
        GuildHandlers.C_REQUEST_UPDATE_ANNOUNCE => 0x06,
        GuildHandlers.C_REQUEST_UPDATE_INTRODUCE => 0x06,
        _ => GuildPackets.MinClientLength(op),
    };

    /// <summary>
    /// Minimum BODY length, which is what <c>PacketDispatcher.Register</c> takes - it hands the
    /// handler <c>packet[4..]</c> and compares that length, while every guard in the decompile is
    /// a FRAME length that includes the header. <b>Registering the frame figure instead would
    /// reject every guild packet four bytes short</b>, which is exactly the bug
    /// status/CLIENT-REJECTS.md section 7.2c recorded against section 10's diff as written.
    /// </summary>
    public static int MinBodyLength(ushort op)
    {
        int frame = MinFrameLength(op);
        return frame <= ClientHeaderSize ? 0 : frame - ClientHeaderSize;
    }

    /// <summary>Is this one of the seventeen we register?</summary>
    public static bool IsRegistered(ushort op)
    {
        foreach (var (_, code) in ClientOpcodes) if (code == op) return true;
        return false;
    }

    // =========================================================================================
    // 3. The dispatcher
    // =========================================================================================

    /// <summary>
    /// An <see cref="ActionDispatcher"/> bound to live sessions. Guilds address clients by
    /// character db id, so the ticket resolver exists only for completeness; the player resolver
    /// falls back to <paramref name="origin"/> so the sender still gets their own reply (and
    /// their own rejection) when World is not up - standalone mode, and every unit test.
    ///
    /// <para><b>ResolveDef is not optional here.</b> GUILD-DESIGN.md section 5.5 lists ten wrong
    /// .def files; <c>GuildHandlers.ResolveDef(null, name)</c> returns the corrections and
    /// nothing else, so every other packet still takes the ordinary SendByDef path.
    /// <c>Dispatcher_encodes_with_the_corrected_def_when_one_resolves</c> is the test that fails
    /// if this is dropped.</para>
    /// </summary>
    internal static ActionDispatcher Dispatcher(GameSession? origin, ILogger log,
        Func<ushort, byte[], bool>? worldReply = null)
    {
        var world = Bridge;
        int originId = origin != null ? (int)origin.PlayerId : -1;
        return new ActionDispatcher(
            t => Sink(world?.SessionForTicket(t)),
            p => Sink(world?.SessionForPlayerId(p) ?? (p == originId ? origin : null)),
            worldReply ?? ((op, payload) => SendWorldAction(world, op, payload)),
            log)
        { ResolveDef = n => GuildHandlers.ResolveDef(null, n) };
    }

    internal static bool SendWorldAction(WorldBridge? world, ushort op, byte[] payload)
    {
        if (world == null) return false;
        // Native UserJoinToGuild sends this one push to the joining user only:
        // Arb069:4373-4380 -> Arb067:17036. Other unsolicited guild actions are mirrors
        // broadcast to every World (Arb045:4885-5067; Arb027:1219-1265).
        // SA_LOAD_GUILD uses its requesting-link dispatcher instead (Arb072:14286).
        if (op == GuildPackets.AS_GUILD_JOINED || op == GuildWarManager.AS_NOTIFY_GUILD_WAR_INFO)
            return payload.Length >= 4 && GuildPlayerWorldSend(BitConverter.ToInt32(payload, 0), op, payload);
        bool sent = false;
        for (int id = 0; id < WorldRegistration.MaxWorldId; id++)
            if (world.HasLinks(id)) { world.SendFrame(id, op, payload); sent = true; }
        return sent;
    }

    private static bool GuildPlayerWorldSend(int playerId, ushort op, byte[] payload)
        => global::TeraSharp.Arbiter.Handlers.ArbiterClientHandlers.SendToWorld(playerId, op, payload);

    private static IClientSink? Sink(GameSession? s) => s == null ? null : new SessionSinkAdapter(s);

    /// <summary>ActionDispatcher's sink over a real session: the same three methods, same
    /// signatures.</summary>
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

    /// <summary>GuildMemberData+0x70, mirrored in S_UPDATE_GUILD_MEMBER.status. The Arbiter's
    /// load loop forces 2 for everyone and flips to 0 as sessions arrive.</summary>
    public const int StateOnline = GuildHandlers.MemberStateOnline;
    public const int StateOffline = GuildHandlers.MemberStateOffline;

    /// <summary>
    /// T172. AS_UPDATE_GUILD_QUEST_POINT_INFO (0x1453), GuildQuestPointRewardManager::SendPointInfoToWorld
    /// (Arb_part_030.c:1885): <c>[i32 guildId][i32 point][i32 maxPoint][i32 0]</c>, only for a guild
    /// member and only when maxPoint &gt; 0. cap_final2a/2b: in the C_LOAD_TOPO_FIN burst after 0x1439
    /// (and after 0x14AF when it is there), 88 frames, all <c>[2|3][0][900][0]</c> - guild 2 for 1003,
    /// guild 3 for 1. Guild quests are not modelled, so the point is 0; 900 is the captured maximum
    /// of both (fresh) guilds, whose source row (GuildQuestConfigData) is not among our sheets.
    /// </summary>
    public const ushort AS_UPDATE_GUILD_QUEST_POINT_INFO = 0x1453;
    public const int GuildQuestMaxPoint = 900;

    public static byte[] BuildGuildQuestPointInfo(int guildId, int point = 0, int maxPoint = GuildQuestMaxPoint)
    {
        var p = new byte[16];
        BitConverter.GetBytes(guildId).CopyTo(p, 0);
        BitConverter.GetBytes(point).CopyTo(p, 4);
        BitConverter.GetBytes(maxPoint).CopyTo(p, 8);
        return p;
    }

    /// <summary>The load-burst send: nothing for a character with no guild.</summary>
    public static bool SendGuildQuestPointInfo(GameSession? session)
    {
        var chr = session?.SelectedCharacter;
        var store = Store;
        if (chr == null || store == null || !session!.InWorld) return false;
        int guildId = store.GetGuildIdOf((int)chr.Id);
        if (guildId == 0) return false;
        global::TeraSharp.Arbiter.Program.World?.SendFrame(session.CurrentWorldId, AS_UPDATE_GUILD_QUEST_POINT_INFO,
            BuildGuildQuestPointInfo(guildId));
        return true;
    }

    /// <summary>
    /// A guild member entered the world: tell the rest of the guild they are online, and tell
    /// World so its mirror agrees. A character with no guild row produces nothing, which is the
    /// normal case.
    /// </summary>
    public static void Register(GameSession? session)
    {
        var chr = session?.SelectedCharacter;
        if (chr == null || !session!.InWorld) return;
        Dispatch(session, MemberState(Store, (int)chr.Id, StateOnline, lastLogoutTime: null), "guild-join");
    }

    /// <summary>
    /// A guild member left the world. The membership SURVIVES - only GuildMemberData+0x70 moves -
    /// so this is a state flip plus a stored logout time, not a row delete. Leaving the GUILD is
    /// a different thing entirely: C_LEAVE_GUILD (0x7C83) is World-side and comes back as
    /// SA_LEAVE_GUILD (0x13FE), which nothing answers yet (section 11.5).
    /// </summary>
    public static void Unregister(GameSession? session)
    {
        var chr = session?.SelectedCharacter;
        if (chr == null) return;
        Dispatch(session, MemberState(Store, (int)chr.Id, StateOffline,
                                      DateTimeOffset.UtcNow.ToUnixTimeSeconds()), "guild-leave");
    }

    private static void Dispatch(GameSession? origin, ArbiterActions actions, string source)
    {
        if (actions.IsEmpty && actions.Rejected == null) return;
        Dispatcher(origin, GuildLog).Dispatch(actions, source);
    }

    /// <summary>
    /// The online/offline flip, as actions: <c>S_UPDATE_GUILD_MEMBER</c> to every OTHER member of
    /// the guild, then <c>AS_UPDATE_GUILD_MEMBER</c> (0x1410) so World's read-only mirror agrees.
    /// The member themselves is not told - they already know, and the real Arbiter's
    /// Guild::BroadcastPacket skips the originator on this path.
    ///
    /// <para><paramref name="lastLogoutTime"/> non-null also writes the time back to the row, so
    /// an offline member still renders with a sensible "last seen" in S_GUILD_MEMBER_LIST.</para>
    /// </summary>
    internal static ArbiterActions MemberState(CharacterStore? store, int characterId, int state,
                                               long? lastLogoutTime)
    {
        var a = new ArbiterActions { Origin = Recipient.Player(characterId) };
        if (store == null) return a;

        var me = store.GetGuildMember(characterId);
        if (me == null) return a;                     // not in a guild: nothing to say

        if (lastLogoutTime != null)
        {
            store.UpdateGuildMemberLocation(characterId, me.WorldId, me.GuardId, me.SectionId,
                                            me.UserLevel, lastLogoutTime.Value);
            me = store.GetGuildMember(characterId) ?? me;
        }

        var fields = new Dictionary<string, object>
        {
            ["memberDbId"] = me.UserDbId,
            ["worldId"] = me.WorldId,
            ["guardId"] = me.GuardId,
            ["sectionId"] = me.SectionId,
            ["groupId"] = me.GuildGroupId,
            ["userLevel"] = me.UserLevel,
            ["race"] = me.Race,
            ["userClass"] = me.UserClass,
            ["status"] = state,
            ["gender"] = me.Gender,
            ["lastLogoutTime"] = me.LastLogoutTime,
            ["isWorldEventTarget"] = false,
            ["isInGuildWarCombatState"] = false,
            ["cityWarCompensationStatus"] = false,
            ["name"] = me.Name,
        };
        foreach (var other in store.GetGuildMembers(me.GuildId))
            if (other.UserDbId != characterId)
                a.ToPlayer(other.UserDbId, "S_UPDATE_GUILD_MEMBER", fields);

        a.World(GuildPackets.AS_UPDATE_GUILD_MEMBER, GuildPackets.BuildAsUpdateGuildMember(
            me.GuildId, me.UserDbId, me.WorldId, me.GuardId, me.SectionId, me.UserLevel, state));
        return a;
    }

    // =========================================================================================
    // 5. The entry point the human's one line calls
    // =========================================================================================

    /// <summary>
    /// One of <see cref="ClientOpcodes"/> arrived. Always returns true: these seventeen are
    /// Arbiter-owned, so "we could not do anything with it" must still not fall through to
    /// <c>PacketDispatcher</c>'s forward-to-World path - World answers with
    /// "handler has not been implemented yet!!!" (status/CLIENT-REJECTS.md).
    /// </summary>
    public static bool OnClientPacket(GameSession? session, ushort opcode, ReadOnlyMemory<byte> body)
    {
        if (session == null) return true;
        if (Store == null)
        {
            GuildLog.LogDebug("guild: 0x{Op:X4} dropped - no store open", opcode);
            return true;
        }
        // LoginHandlers sets PlayerId = chr.Id on select, so it is the character db id the guild
        // tables key on - the same number Dispatcher() uses for its origin fallback.
        DispatchClientPacket(Guilds, Dispatcher(session, GuildLog), (int)session.PlayerId, opcode,
                             body.ToArray());
        return true;
    }

    /// <summary>
    /// <c>OnClientPacket</c> -&gt; dispatch. Static and parameterised so the tests exercise this
    /// code and not a copy. A rejection reaches the sender as S_SYSTEM_MESSAGE_CUSTOM because
    /// <see cref="GuildHandlers.OnClientPacket"/> stamps
    /// <c>Origin = Recipient.Player(characterId)</c>.
    /// </summary>
    internal static DispatchResult DispatchClientPacket(
        GuildHandlers guilds, ActionDispatcher dispatcher, int characterId, ushort opcode, byte[] body)
        => dispatcher.Dispatch(guilds.OnClientPacket(characterId, opcode, body), "guild");

    /// <summary>Guild creation has no C_ packet - it arrives as SDB_CREATE_GUILD2 on the DB-proxy
    /// link - so this is the seam a DB-proxy handler (or a test) calls.</summary>
    internal static (int GuildId, DispatchResult Sent) DispatchCreateGuild(
        GuildHandlers guilds, ActionDispatcher dispatcher, int chiefId, string name)
    {
        var a = new GuildActions();
        int id = guilds.CreateGuild(a, chiefId, name);
        return (id, dispatcher.Dispatch(a, "guild-create"));
    }


    /// <summary>
    /// T69: the whole of guild creation, driven by <c>SDB_CREATE_GUILD2</c> (0x27D4) rather than
    /// by a client packet. Creates the guild for <paramref name="chiefDbId"/>, adds every founding
    /// member the request names, dispatches the client fan-out, and hands back the blobs
    /// <c>DBS_CREATE_GUILD2</c> has to carry.
    ///
    /// <para>The two group names come from the request: World, not the Arbiter, owns the strings
    /// the rank list shows ("Guild Master" / "Recruit" in cap_social2.log seq 1268).</para>
    ///
    /// <para><b>Known deviation.</b> <see cref="GuildHandlers.CreateGuild"/> calls
    /// <c>EmitMemberAdded</c> for the chief as well, so we emit one AS_GUILD_JOINED (0x2866) more
    /// than seq 1269 does - the capture sends it only for the first-reply member. It is a per-user
    /// push with no reply, so the extra one costs nothing; matching the capture exactly would mean
    /// changing CreateGuild's own contract, which T52's tests pin.</para>
    /// </summary>
    public static CreateGuildResult CreateGuildFromWorld(
        int chiefDbId, string guildName, string masterGroupName, string memberGroupName,
        IReadOnlyList<int>? memberDbIds)
    {
        var store = Store;
        if (store == null) return new CreateGuildResult(0, 0, Array.Empty<byte[]>(), Array.Empty<byte[]>(), string.Empty);

        var a = new GuildActions();
        int guildId = Guilds.CreateGuild(a, chiefDbId, guildName,
            string.IsNullOrEmpty(masterGroupName) ? "Master" : masterGroupName,
            string.IsNullOrEmpty(memberGroupName) ? "Member" : memberGroupName);

        string firstReplyName = string.Empty;
        if (guildId != 0 && memberDbIds != null)
        {
            foreach (int id in memberDbIds)
            {
                if (id == 0 || id == chiefDbId) continue;
                var who = store.GetCharacter(id);
                if (who == null || store.GetGuildIdOf(id) != 0) continue;
                if (store.AddGuildMember(guildId, id, who.Name, who.Race, who.Class, who.Gender,
                                         who.Level, who.AccountId) == 0) continue;
                store.AddGuildLog(guildId, GuildHandlers.GuildLogJoin, who.Name, actorDbId: id);
                store.DeleteGuildAppliesOfUser(id);
                store.DeleteGuildInvitesOfUser(id);
                Guilds.EmitMemberAddedForWorld(a, guildId, id);
                if (firstReplyName.Length == 0) firstReplyName = who.Name;
            }
        }

        Dispatcher(null, GuildLog).Dispatch(a, "guild-create");
        if (guildId == 0) return new CreateGuildResult(0, 0, Array.Empty<byte[]>(), Array.Empty<byte[]>(), string.Empty);

        var groupBlobs = new List<byte[]>();
        foreach (var gr in store.GetGuildGroups(guildId)) groupBlobs.Add(BuildGuildGroupDataBlob(gr));
        var memberBlobs = new List<byte[]>();
        foreach (var m in store.GetGuildMembers(guildId)) memberBlobs.Add(BuildGuildMemberDataBlob(m));

        long createTime = store.GetGuild(guildId)?.CreateDate ?? 0;
        return new CreateGuildResult(guildId, createTime, groupBlobs, memberBlobs, firstReplyName);
    }

    /// <summary>What <see cref="CreateGuildFromWorld"/> gives DBS_CREATE_GUILD2. GuildId 0 means
    /// the create was refused - the name was taken or the founder was already in a guild.</summary>
    public readonly record struct CreateGuildResult(
        int GuildId, long CreateTime, IReadOnlyList<byte[]> GroupBlobs,
        IReadOnlyList<byte[]> MemberBlobs, string FirstReplyName);

    // =========================================================================================
    // 6. World -> Arbiter: the twelve SA_ guild frames (T52)
    // =========================================================================================

    /// <summary>
    /// Every World -&gt; Arbiter guild opcode, in opcode order (GUILD-DESIGN.md section 4.2).
    /// Ten of them have a layout and a parser in <see cref="GuildPackets"/>; the last two
    /// (<c>SA_INC_GUILD_ACCOUNT_LIMIT</c> 0x1414 and <c>SA_PUSH_GUILD_BUFF</c> 0x145C) have
    /// neither - the design doc's table leaves their payload column empty because no dumper for
    /// them was found. They are gated anyway, and consumed with a log line, because the
    /// alternative is the replay table handing World somebody else's reply.
    /// </summary>
    public static readonly ushort[] WorldOpcodes =
    {
        GuildPackets.SA_LOAD_GUILD,               // 0x13FB
        GuildPackets.SA_DESTROY_GUILD,            // 0x13FC
        GuildPackets.SA_LEAVE_GUILD,              // 0x13FE
        GuildPackets.SA_BANISH_GUILD_MEMBER,      // 0x1400
        GuildPackets.SA_CHANGE_GUILD_CHIEF,       // 0x1402
        GuildPackets.SA_SET_GUILDGROUP_AUTHORITY, // 0x1404
        GuildPackets.SA_CREATE_GUILD_GROUP,       // 0x1406
        GuildPackets.SA_REMOVE_GUILD_GROUP,       // 0x1408
        GuildPackets.SA_CHANGE_GUILDGROUP,        // 0x140B
        GuildPackets.SA_UPDATE_GUILD_MEMBER,      // 0x140F
        GuildPackets.SA_INC_GUILD_ACCOUNT_LIMIT,  // 0x1414 - layout unknown
        GuildPackets.SA_PUSH_GUILD_BUFF,          // 0x145C - layout unknown
    };

    /// <summary>
    /// Is this a World -&gt; Arbiter guild frame? The gate the one <c>WorldBridge</c> line uses,
    /// and the twin of <see cref="PartyWiring.HandlesWorldFrame"/>. It is a membership test
    /// rather than <c>GuildPackets.MinFrameLength(op) != 0</c> because that answers 0 for the two
    /// opcodes whose layout is unknown, and those still must not reach the replay table.
    /// None of the twelve is in <c>DbProxyHandlers</c> or <c>WorldReplayTable.OneWayFromWorld</c>.
    /// </summary>
    public static bool HandlesWorldFrame(ushort op)
    {
        foreach (ushort o in WorldOpcodes) if (o == op) return true;
        return false;
    }

    /// <summary>
    /// A World frame arrived. Returns true when it was a guild frame and has been dealt with, so
    /// the caller can <c>return</c>; false leaves it to DbProxy and the replay table exactly as
    /// before.
    /// </summary>
    public static bool TryHandleWorldFrame(ushort opcode, byte[] payload,
        Action<ushort, byte[]>? reply = null)
    {
        if (!HandlesWorldFrame(opcode)) return false;
        var actions = OnWorldFrame(Store, opcode, payload);
        // Native Handler_SA_LOAD_GUILD sends 144D/27D0/27D1/27D2 through
        // param2, the requesting WorldServerSession (Arb072:14163,14244,
        // 14286-14300,14490-14560). Mutations below remain global mirrors.
        Func<ushort, byte[], bool>? requestReply = opcode == GuildPackets.SA_LOAD_GUILD
            ? (op, body) => { if (reply == null) return false; reply(op, body); return true; }
            : null;
        Dispatcher(null, GuildLog, requestReply).Dispatch(actions, "guild-world");
        return true;
    }

    /// <summary>
    /// The pure half: one W-&gt;A guild frame in, an action set out. <c>Origin</c> stays
    /// <see cref="Recipient.None"/> - a World frame has no originating client - so a rejection
    /// here goes to the log rather than to somebody's chat window, which matters because eight
    /// of the twelve land there every time.
    /// </summary>
    internal static ArbiterActions OnWorldFrame(CharacterStore? store, ushort opcode, byte[] payload)
    {
        var a = new ArbiterActions();
        if (store == null) return a.Reject($"0x{opcode:X4}: no store open");

        switch (opcode)
        {
            case GuildPackets.SA_LEAVE_GUILD: return LeaveGuild(a, store, payload, banished: false);
            case GuildPackets.SA_BANISH_GUILD_MEMBER: return LeaveGuild(a, store, payload, banished: true);
            // ---- T57 ----
            case GuildPackets.SA_LOAD_GUILD: return LoadGuild(a, store, payload);
            case GuildPackets.SA_DESTROY_GUILD: return DestroyGuild(a, store, payload);
            case GuildPackets.SA_CHANGE_GUILD_CHIEF: return ChangeGuildChief(a, store, payload);
            case GuildPackets.SA_SET_GUILDGROUP_AUTHORITY: return SetGuildGroupAuthority(a, store, payload);
            case GuildPackets.SA_CREATE_GUILD_GROUP: return CreateGuildGroup(a, store, payload);
            case GuildPackets.SA_REMOVE_GUILD_GROUP: return RemoveGuildGroup(a, store, payload);
            case GuildPackets.SA_CHANGE_GUILDGROUP: return ChangeGuildGroup(a, store, payload);
            case GuildPackets.SA_UPDATE_GUILD_MEMBER: return UpdateGuildMember(a, store, payload);
            default:
                // Only 0x1414 and 0x145C are left here: SA_INC_GUILD_ACCOUNT_LIMIT and
                // SA_PUSH_GUILD_BUFF. Neither has a modelled subsystem behind it.
                return a.Reject($"0x{opcode:X4} is a guild frame we gate but do not model yet "
                                + "(status/GUILD-DESIGN.md section 12.3)");
        }
    }

    // ---- system message ids, from the decompiled leave worker ----

    /// <summary>
    /// GuildUtil::UserLeaveFromGuild (FUN_1408165f0, Arb_part_070.c:16414) sends exactly three
    /// things and <b>no client packet at all</b>: 0x126 to the leaver with <c>GuildName</c>,
    /// then 0x2F8 (voluntary) or 0x2F9 (banished) to the rest of the guild with <c>UserName</c>,
    /// then AS_LEAVE_GUILD to every World session. Note the capitalisation: this path spells the
    /// keys with capitals, while the invite path (GuildHandlers) spells them lowercase.
    /// </summary>
    public const int SmtYouLeftTheGuild = 0x126;
    public const int SmtMemberLeft = 0x2F8;
    public const int SmtMemberBanished = 0x2F9;

    /// <summary>
    /// SA_LEAVE_GUILD (0x13FE) and SA_BANISH_GUILD_MEMBER (0x1400) - the same worker in the
    /// binary, with one enum apart. Payload (frame-relative):
    /// <c>u32 nameOff@06, i64 ArbiterUser@0A, i32 GuildDbId@12, i32 MemberDbId@16</c>.
    ///
    /// <para>The real handler identifies the leaver by the <c>User</c> object at +0x0A and reads
    /// only <c>GuildDbId</c> out of the frame (<c>*(u32 *)(packet + 0x12)</c>). We have no User
    /// handles, so we use <c>MemberDbId</c> at +0x16, which the frame carries and which the
    /// dumper names - and we verify it really is a member of that guild before touching a row.</para>
    ///
    /// <para>A guild that falls to zero members is destroyed
    /// (<c>GuildManager::MemberLeave_DestroyGuildWithLock</c> is called on this exact path). The
    /// real one also re-picks a chief; ours promotes the longest-serving remaining member, which
    /// is ours and not the decompile's - see section 12.4.</para>
    /// </summary>
    private static ArbiterActions LeaveGuild(ArbiterActions a, CharacterStore store, byte[] payload, bool banished)
    {
        var f = GuildPackets.ParseSaLeaveGuild(payload);
        if (f == null) return a.Reject("SA_LEAVE_GUILD: frame shorter than 0x1A");
        var (nameInFrame, _, guildDbId, memberDbId) = f.Value;

        var member = store.GetGuildMember(memberDbId);
        if (member == null) return a.Reject($"SA_LEAVE_GUILD: {memberDbId} is in no guild");
        if (member.GuildId != guildDbId)
            return a.Reject($"SA_LEAVE_GUILD: {memberDbId} is in guild {member.GuildId}, not {guildDbId}");

        var guild = store.GetGuild(guildDbId);
        if (guild == null) return a.Reject($"SA_LEAVE_GUILD: no guild {guildDbId}");
        string memberName = member.Name.Length > 0 ? member.Name : nameInFrame;

        if (!store.RemoveGuildMember(memberDbId))
            return a.Reject($"SA_LEAVE_GUILD: spLeaveGuildMember removed no row for {memberDbId}");
        store.AddGuildLog(guildDbId, GuildHandlers.GuildLogLeave, memberName, actorDbId: memberDbId);

        // 0x126 to the leaver, then 0x2F8 / 0x2F9 to everyone still in the guild.
        Smt(a, memberDbId, SmtYouLeftTheGuild, "GuildName", guild.Name);
        var remaining = store.GetGuildMembers(guildDbId);
        int smt = banished ? SmtMemberBanished : SmtMemberLeft;
        foreach (var other in remaining) Smt(a, other.UserDbId, smt, "UserName", memberName);

        // World is told regardless of what happens to the guild next.
        a.World(GuildPackets.AS_LEAVE_GUILD, GuildPackets.BuildAsLeaveGuild(guildDbId, memberDbId));

        if (remaining.Count == 0)
        {
            // GuildManager::MemberLeave_DestroyGuildWithLock is called on this exact path.
            store.DeleteGuild(guildDbId);
            a.World(GuildPackets.AS_DESTROY_GUILD, GuildPackets.BuildAsDestroyGuild(guildDbId));
            return a;
        }

        if (guild.ChiefDbId == memberDbId)
        {
            // The master left. Longest-serving member first; the real Arbiter certainly re-picks
            // one (a guild with no chief is not a state the client can render) but the choice was
            // not traced - section 12.4.
            var next = remaining[0];
            foreach (var m in remaining) if (m.GuildJoinDate < next.GuildJoinDate) next = m;
            store.ChangeGuildChief(guildDbId, next.UserDbId);
            // The shipped S_CHANGE_GUILD_CHIEF.def names its single field `playerId`, not the
            // dumper's NewChiefDbId. The def wins - it is what the client reads.
            foreach (var other in remaining)
                a.ToPlayer(other.UserDbId, "S_CHANGE_GUILD_CHIEF",
                    new Dictionary<string, object> { ["playerId"] = (uint)next.UserDbId });
            a.World(GuildPackets.AS_CHANGE_GUILD_CHIEF,
                GuildPackets.BuildAsChangeGuildChief(guildDbId, next.UserDbId));
        }
        return a;
    }


    // =========================================================================================
    // 6b. T57 - the eight gated frames that section 12.3 left unanswered
    //
    // Each one is quoted to its handler in Arb_part_072.c. Two things they have in common and
    // that are easy to get wrong:
    //   * NONE of them sends a client packet except the chief change. The guild window is
    //     refreshed by the S_ packets the C_ handlers already answer, not by these.
    //   * Mutation AS_ fan-out loops over all 32 WorldServerSession slots. SA_LOAD_GUILD
    //     is a request/reply exception: TryHandleWorldFrame sends it to its incoming link.
    // =========================================================================================

    /// <summary>
    /// SA_LOAD_GUILD (0x13FB) - Handler_SA_LOAD_GUILD, Arb_part_072.c:14046. Re-push one guild's
    /// mirror to the requesting WorldServerSession. The writer calls in that function are, in order,
    /// <c>0x144D</c>, <c>0x27D0</c>, <c>0x27D1</c>, <c>0x27D2</c> - so this is NOT the boot load:
    /// the guild blob goes out as <c>AS_LOAD_GUILD_DATA</c> (0x144D) rather than
    /// <c>DBS_INIT_GUILD_DATA</c> (0x27ED), and there is no <c>DBS_LOAD_GUILD_COMPLETE</c>
    /// (0x27D3) terminator. Grepping the whole function for 0x27d3 / 0x27ed finds neither.
    /// </summary>
    private static ArbiterActions LoadGuild(ArbiterActions a, CharacterStore store, byte[] payload)
    {
        var f = GuildPackets.ParseSaGuildAction(payload);
        if (f == null) return a.Reject("SA_LOAD_GUILD: frame shorter than 0x12");
        int guildDbId = f.Value.guildDbId;

        var g = store.GetGuild(guildDbId);
        if (g == null) return a.Reject($"SA_LOAD_GUILD: no guild {guildDbId}");

        a.World(GuildPackets.AS_LOAD_GUILD_DATA,
            GuildPackets.BuildAsGuildData(BuildGuildDataBlob(g, store.GetGuildLogo(guildDbId))));

        var groupBlobs = new List<byte[]>();
        foreach (var gr in store.GetGuildGroups(guildDbId)) groupBlobs.Add(BuildGuildGroupDataBlob(gr));
        a.World(GuildPackets.DBS_INIT_GUILD_GROUP,
            BuildInitArrayPayload(guildDbId, groupBlobs, GuildPackets.GuildGroupDataSize));

        var members = store.GetGuildMembers(guildDbId);
        for (int i = 0; i < members.Count || i == 0; i += MembersPerFrame)
        {
            var batch = new List<byte[]>();
            for (int j = i; j < members.Count && j < i + MembersPerFrame; j++)
                batch.Add(BuildGuildMemberDataBlob(members[j]));
            a.World(GuildPackets.DBS_INIT_GUILD_MEMBER,
                BuildInitArrayPayload(guildDbId, batch, GuildPackets.GuildMemberDataSize));
        }

        a.World(GuildPackets.DBS_INIT_GUILD_PERK_LIST,
            BuildInitArrayPayload(guildDbId, Array.Empty<byte[]>(), GuildPerkRecordSize));
        return a;
    }

    /// <summary>
    /// SA_DESTROY_GUILD (0x13FC) - Handler_SA_DESTROY_GUILD (Arb_part_072.c:13863) is one call to
    /// <c>GuildUtil::DestroyGuild(User *, int)</c> (FUN_1407ed1f0, Arb_part_069.c:5298), which
    /// refuses with system message <see cref="SmtGuildAtWarCannotDisband"/> when the guild is in a
    /// guild war and otherwise runs <c>GuildManager::DestroyGuildWithLock</c> -&gt;
    /// <c>dbo.spDeleteGuild</c>. Guild wars are not modelled at all, so the refusal branch is
    /// unreachable here and the id is a constant rather than a code path.
    ///
    /// <para>The member-facing half of DestroyGuildWithLock was not traced, so - exactly as the
    /// last-member-leaves path in <see cref="LeaveGuild"/> already does - we delete the rows and
    /// tell World, and send the members nothing. Section 12.4.</para>
    /// </summary>
    private static ArbiterActions DestroyGuild(ArbiterActions a, CharacterStore store, byte[] payload)
    {
        var f = GuildPackets.ParseSaGuildAction(payload);
        if (f == null) return a.Reject("SA_DESTROY_GUILD: frame shorter than 0x12");
        int guildDbId = f.Value.guildDbId;

        if (store.GetGuild(guildDbId) == null) return a.Reject($"SA_DESTROY_GUILD: no guild {guildDbId}");
        if (!store.DeleteGuild(guildDbId)) return a.Reject($"SA_DESTROY_GUILD: spDeleteGuild removed no row for {guildDbId}");

        a.World(GuildPackets.AS_DESTROY_GUILD, GuildPackets.BuildAsDestroyGuild(guildDbId));
        return a;
    }

    /// <summary>
    /// SA_CHANGE_GUILD_CHIEF (0x1402) - Handler_SA_CHANGE_GUILD_CHIEF, Arb_part_072.c:13621. The
    /// real one looks up the CURRENT chief from <c>Guild+0xD8</c> and the new one from the frame,
    /// does nothing when either user is missing or the two are the same, refuses with system
    /// message <see cref="SmtCannotBeGuildChief"/> when <c>User+0x1BC != 0</c> on the new chief,
    /// and otherwise calls <c>Guild::ChangeChief</c>.
    ///
    /// <para><c>User+0x1BC</c> is a per-user flag we do not model, so the refusal cannot fire
    /// here; what we do model is the membership check, which the real one gets for free from the
    /// Guild object. The client half is the same pair the leave path emits.</para>
    /// </summary>
    private static ArbiterActions ChangeGuildChief(ArbiterActions a, CharacterStore store, byte[] payload)
    {
        var f = GuildPackets.ParseSaChangeGuildChief(payload);
        if (f == null) return a.Reject("SA_CHANGE_GUILD_CHIEF: frame shorter than 0x16");
        var (_, guildDbId, newChiefDbId) = f.Value;

        var g = store.GetGuild(guildDbId);
        if (g == null) return a.Reject($"SA_CHANGE_GUILD_CHIEF: no guild {guildDbId}");
        if (g.ChiefDbId == newChiefDbId)
            return a.Reject($"SA_CHANGE_GUILD_CHIEF: {newChiefDbId} is already the chief of {guildDbId}");

        var member = store.GetGuildMember(newChiefDbId);
        if (member == null || member.GuildId != guildDbId)
            return a.Reject($"SA_CHANGE_GUILD_CHIEF: {newChiefDbId} is not a member of guild {guildDbId}");

        if (!store.ChangeGuildChief(guildDbId, newChiefDbId))
            return a.Reject($"SA_CHANGE_GUILD_CHIEF: spUpdateGuildChief changed no row for {guildDbId}");

        // The shipped S_CHANGE_GUILD_CHIEF.def names its single field `playerId`, not the
        // dumper's NewChiefDbId - the def wins, it is what the client reads (section 12.2).
        foreach (var other in store.GetGuildMembers(guildDbId))
            a.ToPlayer(other.UserDbId, "S_CHANGE_GUILD_CHIEF",
                new Dictionary<string, object> { ["playerId"] = (uint)newChiefDbId });
        a.World(GuildPackets.AS_CHANGE_GUILD_CHIEF,
            GuildPackets.BuildAsChangeGuildChief(guildDbId, newChiefDbId));
        return a;
    }

    /// <summary>
    /// SA_SET_GUILDGROUP_AUTHORITY (0x1404) - Handler_SA_SET_GUILDGROUP_AUTHORITY
    /// (Arb_part_072.c:14786) validates the new name through
    /// <c>InputRestrictionHelper::CheckGuildGroupName</c> and then runs
    /// <c>_ChangeGuildGroupNameCallback::OnSuccess</c> (:17704): one DB update, one in-memory
    /// update, then <c>AS_SET_GUILDGROUP_AUTHORITY</c> to every World. No guild log, no client
    /// packet.
    /// </summary>
    private static ArbiterActions SetGuildGroupAuthority(ArbiterActions a, CharacterStore store, byte[] payload)
    {
        var f = GuildPackets.ParseSaSetGuildGroupAuthority(payload);
        if (f == null) return a.Reject("SA_SET_GUILDGROUP_AUTHORITY: frame shorter than 0x1E");
        var (newName, _, guildDbId, guildGroupId, authority) = f.Value;

        if (!IsLegalGroupName(newName))
            return a.Reject($"SA_SET_GUILDGROUP_AUTHORITY: '{newName}' is not a legal group name");
        if (store.GetGuild(guildDbId) == null)
            return a.Reject($"SA_SET_GUILDGROUP_AUTHORITY: no guild {guildDbId}");
        if (!store.SetGuildGroupAuthority(guildDbId, guildGroupId, authority, newName))
            return a.Reject($"SA_SET_GUILDGROUP_AUTHORITY: no group {guildGroupId} in guild {guildDbId}");

        a.World(GuildPackets.AS_SET_GUILDGROUP_AUTHORITY,
            GuildPackets.BuildAsSetGuildGroupAuthority(guildDbId, guildGroupId, authority, newName));
        return a;
    }

    /// <summary>
    /// SA_CREATE_GUILD_GROUP (0x1406) - Handler_SA_CREATE_GUILD_GROUP (Arb_part_072.c:13729) plus
    /// <c>_AddGuildGroupNameCallback::OnSuccess</c> (:17616). The id comes from
    /// <c>Guild::GenerateNewGuildGroupId</c> (FUN_14056cbf0, Arb_part_046.c:1901), which walks the
    /// group map keeping the largest id and returns <c>max + 1</c> - so the first group of an
    /// empty guild is 1, and ids are never reused below the maximum.
    ///
    /// <para>Authority is <b>0</b> on a new group: the handler builds its argument block as
    /// <c>{ GuildDbId, 0 }</c> and the callback copies that second word into the record it adds
    /// and into the AS_ frame. Then <c>Guild::AddGuildLog</c> with action
    /// <see cref="GuildHandlers.GuildLogAddGroup"/>, and AS_CREATE_GUILD_GROUP to every World.</para>
    /// </summary>
    private static ArbiterActions CreateGuildGroup(ArbiterActions a, CharacterStore store, byte[] payload)
    {
        var f = GuildPackets.ParseSaCreateGuildGroup(payload);
        if (f == null) return a.Reject("SA_CREATE_GUILD_GROUP: frame shorter than 0x16");
        var (groupName, _, guildDbId) = f.Value;

        if (!IsLegalGroupName(groupName))
            return a.Reject($"SA_CREATE_GUILD_GROUP: '{groupName}' is not a legal group name");
        if (store.GetGuild(guildDbId) == null)
            return a.Reject($"SA_CREATE_GUILD_GROUP: no guild {guildDbId}");

        int groupId = GenerateNewGuildGroupId(store, guildDbId);
        if (!store.CreateGuildGroup(guildDbId, groupId, groupName, GuildGroupDefaultAuthority))
            return a.Reject($"SA_CREATE_GUILD_GROUP: group {groupId} already exists in guild {guildDbId}");

        store.AddGuildLog(guildDbId, GuildHandlers.GuildLogAddGroup, groupName, paramInt: groupId);
        a.World(GuildPackets.AS_CREATE_GUILD_GROUP, GuildPackets.BuildAsCreateGuildGroup(
            guildDbId, groupId, groupName, GuildGroupDefaultAuthority));
        return a;
    }

    /// <summary>
    /// SA_REMOVE_GUILD_GROUP (0x1408) - Handler_SA_REMOVE_GUILD_GROUP, Arb_part_072.c:14643. The
    /// group name is read BEFORE the remove (it is the log's second string), the log entry is
    /// written only when the remove returned true, and <c>AS_REMOVE_GUILD_GROUP</c> goes out
    /// whether or not it did - the fan-out loop sits outside that <c>if</c>. We keep that order.
    /// </summary>
    private static ArbiterActions RemoveGuildGroup(ArbiterActions a, CharacterStore store, byte[] payload)
    {
        var f = GuildPackets.ParseSaRemoveGuildGroup(payload);
        if (f == null) return a.Reject("SA_REMOVE_GUILD_GROUP: frame shorter than 0x16");
        var (_, guildDbId, groupId) = f.Value;

        if (store.GetGuild(guildDbId) == null)
            return a.Reject($"SA_REMOVE_GUILD_GROUP: no guild {guildDbId}");

        var group = store.GetGuildGroup(guildDbId, groupId);
        if (store.DeleteGuildGroup(guildDbId, groupId))
            store.AddGuildLog(guildDbId, GuildHandlers.GuildLogRemoveGroup,
                group?.Name ?? string.Empty, paramInt: groupId);

        a.World(GuildPackets.AS_REMOVE_GUILD_GROUP, GuildPackets.BuildAsRemoveGuildGroup(guildDbId, groupId));
        return a;
    }

    /// <summary>
    /// SA_CHANGE_GUILDGROUP (0x140B) - Handler_SA_CHANGE_GUILDGROUP, Arb_part_072.c:13469. Same
    /// shape as the remove: <c>Guild::ChangeMemberGroup</c>, log action
    /// <see cref="GuildHandlers.GuildLogChangeMemberGroup"/> only when it took, then
    /// <c>AS_CHANGE_GUILDGROUP</c> to every World unconditionally.
    /// </summary>
    private static ArbiterActions ChangeGuildGroup(ArbiterActions a, CharacterStore store, byte[] payload)
    {
        var f = GuildPackets.ParseSaChangeGuildGroup(payload);
        if (f == null) return a.Reject("SA_CHANGE_GUILDGROUP: frame shorter than 0x1A");
        var (_, guildDbId, memberDbId, guildGroupId) = f.Value;

        var member = store.GetGuildMember(memberDbId);
        if (member == null || member.GuildId != guildDbId)
            return a.Reject($"SA_CHANGE_GUILDGROUP: {memberDbId} is not a member of guild {guildDbId}");

        if (store.SetGuildMemberGroup(memberDbId, guildGroupId))
        {
            var group = store.GetGuildGroup(guildDbId, guildGroupId);
            store.AddGuildLog(guildDbId, GuildHandlers.GuildLogChangeMemberGroup,
                group?.Name ?? string.Empty, targetName: member.Name,
                actorDbId: memberDbId, paramInt: guildGroupId);
        }

        a.World(GuildPackets.AS_CHANGE_GUILDGROUP,
            GuildPackets.BuildAsChangeGuildGroup(guildDbId, memberDbId, guildGroupId));
        return a;
    }

    /// <summary>
    /// SA_UPDATE_GUILD_MEMBER (0x140F) - Handler_SA_UPDATE_GUILD_MEMBER, Arb_part_072.c:14924.
    /// The frame carries ONLY <c>GuildDbId</c> and <c>MemberDbId</c>: it is World saying "re-read
    /// this member". The real Arbiter then pulls worldId / guardId / sectionId / level / state off
    /// its own live <c>User</c> object, writes them into GuildMemberData and re-broadcasts
    /// <c>AS_UPDATE_GUILD_MEMBER</c> to every World.
    ///
    /// <para>We have no User objects; the roster (section 11.3) is where those five fields live,
    /// so we echo the stored row instead of inventing values. The state is
    /// <see cref="StateOnline"/>: World only asks about a member it is currently hosting, and the
    /// real handler reaches the same answer through <c>User::IsInWorld</c>.</para>
    /// </summary>
    private static ArbiterActions UpdateGuildMember(ArbiterActions a, CharacterStore store, byte[] payload)
    {
        var f = GuildPackets.ParseSaUpdateGuildMember(payload);
        if (f == null) return a.Reject("SA_UPDATE_GUILD_MEMBER: frame shorter than 0x0E");
        var (guildDbId, memberDbId) = f.Value;

        var member = store.GetGuildMember(memberDbId);
        if (member == null || member.GuildId != guildDbId)
            return a.Reject($"SA_UPDATE_GUILD_MEMBER: {memberDbId} is not a member of guild {guildDbId}");

        a.World(GuildPackets.AS_UPDATE_GUILD_MEMBER, GuildPackets.BuildAsUpdateGuildMember(
            guildDbId, memberDbId, member.WorldId, member.GuardId, member.SectionId,
            member.UserLevel, StateOnline));
        return a;
    }

    /// <summary>Authority a freshly created guild group gets: 0 - see <see cref="CreateGuildGroup"/>.</summary>
    public const int GuildGroupDefaultAuthority = 0;

    /// <summary>
    /// System message the real handler sends when the new chief's <c>User+0x1BC</c> is set
    /// (Arb_part_072.c:13672). Unreachable here - that flag is not modelled - but recorded so the
    /// id is not lost.
    /// </summary>
    public const int SmtCannotBeGuildChief = 0xF5C;

    /// <summary>
    /// System message <c>GuildUtil::DestroyGuild</c> sends when the guild is in a guild war
    /// (Arb_part_069.c:5363). Also unreachable here - guild wars are not modelled.
    /// </summary>
    public const int SmtGuildAtWarCannotDisband = 0x820;

    /// <summary>
    /// <c>InputRestrictionHelper::CheckGuildGroupName</c>: the name must be non-empty and shorter
    /// than 0x10 code units, i.e. 1..15 characters - the same bound as
    /// <c>CharacterStore.MaxGuildGroupName</c>. The forbidden-word check that follows it
    /// (FUN_140945c40 with list 0x11) has no data behind it here.
    /// </summary>
    internal static bool IsLegalGroupName(string? name)
        => !string.IsNullOrEmpty(name) && name!.Length <= CharacterStore.MaxGuildGroupName;

    /// <summary>
    /// <c>Guild::GenerateNewGuildGroupId</c> (Arb_part_046.c:1901): walk the guild's groups
    /// keeping the largest id, return <c>max + 1</c>. Starts at 0, so an empty guild's first
    /// group is 1.
    /// </summary>
    internal static int GenerateNewGuildGroupId(CharacterStore store, int guildDbId)
    {
        int max = 0;
        foreach (var gr in store.GetGuildGroups(guildDbId))
            if (gr.GuildGroupId > max) max = gr.GuildGroupId;
        return max + 1;
    }

    /// <summary>One S_SYSTEM_MESSAGE to one character - the <c>@id\vKey\vValue</c> form.</summary>
    private static void Smt(ArbiterActions a, int characterId, int id, params string[] keysAndValues)
        => a.ToPlayer(characterId, "S_SYSTEM_MESSAGE", new Dictionary<string, object>
        {
            ["message"] = SocialHandlers.Smt(id, keysAndValues),
        });

    // =========================================================================================
    // 7. The SDB_INIT_GUILD (0x27CF) boot load, built from rows
    // =========================================================================================

    /// <summary>
    /// DBS_INIT_GUILD_MEMBER batch size. The real Arbiter's sender writes the whole member vector
    /// into one frame (Arb_part_072.c:14378), but the captures show 31 per frame, and World reads
    /// the array by BYTE LENGTH so any batch is legal. 31 is what World has been observed to
    /// receive, so it is what we send.
    /// </summary>
    public const int MembersPerFrame = 31;

    /// <summary>
    /// The whole answer to <c>SDB_INIT_GUILD</c> (0x27CF), in order, as (opcode, payload) pairs.
    ///
    /// <para>Per guild: <c>DBS_INIT_GUILD_DATA</c> (0x27ED, Success = 1) -&gt;
    /// <c>DBS_INIT_GUILD_GROUP</c> (0x27D0) -&gt; <c>DBS_INIT_GUILD_MEMBER</c> (0x27D1, batched)
    /// -&gt; <c>DBS_INIT_GUILD_PERK_LIST</c> (0x27D2) -&gt; <c>DBS_LOAD_GUILD_COMPLETE</c>
    /// (0x27D3). Then one final <c>DBS_INIT_GUILD_DATA</c> with <c>Success = 0</c>, which is the
    /// terminator World waits for. With no guilds in the DB the whole thing collapses to that one
    /// terminator, which is
    /// <see cref="GuildPackets.BuildEmptyDbsInitGuildData"/> - byte-identical to
    /// <c>data/cap_guild.bin</c> seq 2 apart from the two uninitialised padding bytes the real
    /// Arbiter leaks (GUILD-DESIGN.md section 2.1). Answering from rows is what retires that
    /// replay entry and the leak with it.</para>
    /// </summary>
    public static List<(ushort Op, byte[] Payload)> BuildInitGuildLoad(CharacterStore? store)
    {
        var frames = new List<(ushort Op, byte[] Payload)>();
        var guilds = store?.GetAllGuilds() ?? new List<CharacterStore.GuildRow>();

        foreach (var g in guilds)
        {
            frames.Add((GuildPackets.DBS_INIT_GUILD_DATA, GuildPackets.BuildDbsInitGuildData(
                BuildGuildDataBlob(g, store!.GetGuildLogo(g.GuildId)), LogoIdOf(g), success: true)));

            var groups = store.GetGuildGroups(g.GuildId);
            var groupBlobs = new List<byte[]>(groups.Count);
            foreach (var gr in groups) groupBlobs.Add(BuildGuildGroupDataBlob(gr));
            frames.Add((GuildPackets.DBS_INIT_GUILD_GROUP,
                BuildInitArrayPayload(g.GuildId, groupBlobs, GuildPackets.GuildGroupDataSize)));

            var members = store.GetGuildMembers(g.GuildId);
            for (int i = 0; i < members.Count || i == 0; i += MembersPerFrame)
            {
                var batch = new List<byte[]>();
                for (int j = i; j < members.Count && j < i + MembersPerFrame; j++)
                    batch.Add(BuildGuildMemberDataBlob(members[j]));
                frames.Add((GuildPackets.DBS_INIT_GUILD_MEMBER,
                    BuildInitArrayPayload(g.GuildId, batch, GuildPackets.GuildMemberDataSize)));
            }

            // The perk table exists but nothing fills it (GUILD-DESIGN.md section 10, "What is
            // NOT modelled"), so the array is always empty. The frame still has to be sent -
            // World's load state machine walks 0x27D0 -> 0x27D1 -> 0x27D2 -> 0x27D3 in order.
            frames.Add((GuildPackets.DBS_INIT_GUILD_PERK_LIST,
                BuildInitArrayPayload(g.GuildId, Array.Empty<byte[]>(), GuildPerkRecordSize)));

            frames.Add((GuildPackets.DBS_LOAD_GUILD_COMPLETE, Array.Empty<byte>()));
        }

        frames.Add((GuildPackets.DBS_INIT_GUILD_DATA, GuildPackets.BuildEmptyDbsInitGuildData()));
        return frames;
    }

    /// <summary>DBS_INIT_GUILD_PERK_LIST's element stride, from its writer's loop, which advances
    /// the running frame length by 0xc per element (Arb_part_072.c:14554). The contents are two
    /// u32s and never non-empty here, so only the stride matters.</summary>
    public const int GuildPerkRecordSize = 0x0C;

    /// <summary>
    /// The shared header of DBS_INIT_GUILD_GROUP / _MEMBER / _PERK_LIST, which are one writer
    /// three times over (Arb_part_072.c:14299 / :14378 / :14502):
    /// <code>
    ///   [06] u32 arrayOffset      backpatched to the running frame length, i.e. always 0x12
    ///   [0A] u32 arrayByteLength  count * recordSize - a BYTE length, not an element count
    ///   [0E] u32 guildDbId        Guild+0x88+0, i.e. GuildData.GuildDbId
    ///   [12] record[]             raw, no per-element header
    /// </code>
    /// The byte-length slot is the thing worth pinning: the writer fills it with
    /// <c>((count) * 0xf0)</c> for the member frame, not with <c>count</c>.
    /// </summary>
    public static byte[] BuildInitArrayPayload(int guildDbId, IReadOnlyList<byte[]> records, int recordSize)
    {
        var p = new byte[12 + records.Count * recordSize];
        BitConverter.GetBytes(6 + 12).CopyTo(p, 0);                      // frame-relative 0x12
        BitConverter.GetBytes(records.Count * recordSize).CopyTo(p, 4);
        BitConverter.GetBytes(guildDbId).CopyTo(p, 8);
        int at = 12;
        foreach (var r in records) { Array.Copy(r, 0, p, at, Math.Min(r.Length, recordSize)); at += recordSize; }
        return p;
    }

    /// <summary>
    /// <summary>
    /// AS_UPDATE_GUILD_DATA (0x144E) for one guild, or null when the row is gone. Same payload
    /// as the AS_LOAD_GUILD_DATA push above; only the opcode differs.
    ///
    /// <para>The Arbiter sends this after anything that moves guild-wide state without a
    /// dedicated push of its own. cap_final tap 2501 is the incentive case: 0x27A0 arrives,
    /// the funds move, this goes out, then 0x27A1 answers.</para>
    /// </summary>
    public static byte[]? BuildGuildDataPush(CharacterStore store, int guildDbId)
    {
        var g = store.GetGuild(guildDbId);
        return g == null
            ? null
            : GuildPackets.BuildAsGuildData(BuildGuildDataBlob(g, store.GetGuildLogo(guildDbId)));
    }

    /// One 0x23A0-byte GuildData from a stored row. Offsets are GuildPackets' Gd* constants, which
    /// came from the spLoadAllGuild column binds; the five the capture confirms are GuildLevel,
    /// LastIncentiveTime, JoinMinLevel, JoinMaxLevel and GuildJoinType.
    ///
    /// <para>The two alignment holes (<see cref="GuildPackets.GuildDataPaddingHoles"/>) are left
    /// zero on purpose - they are what the real Arbiter leaks.</para>
    /// </summary>
    public static byte[] BuildGuildDataBlob(CharacterStore.GuildRow g, byte[]? logo)
    {
        var b = new byte[GuildPackets.GuildDataSize];
        void I32(int off, int v) => BitConverter.GetBytes(v).CopyTo(b, off);
        void I64(int off, long v) => BitConverter.GetBytes(v).CopyTo(b, off);
        void Str(int off, string s, int maxChars) => WriteWString(b, off, s, maxChars);
        void Time(int off, long unix) => Timestamp(unix).CopyTo(b, off);

        I32(GuildPackets.GdGuildDbId, g.GuildId);
        Str(GuildPackets.GdGuildName, g.Name, GuildPackets.GuildNameMaxChars);
        I32(GuildPackets.GdChiefDbId, g.ChiefDbId);
        Time(GuildPackets.GdGuildCreateDate, g.CreateDate);
        I32(GuildPackets.GdGuildLevel, g.Level);
        I64(GuildPackets.GdGuildExp, g.Exp);
        I64(GuildPackets.GdGuildPoint, g.Point);
        I64(GuildPackets.GdGuildMoney, g.Money);
        Time(GuildPackets.GdLastIncentiveTime, g.LastIncentiveTime);
        Str(GuildPackets.GdGuildAnnounce, g.Announce, GuildPackets.GuildAnnounceMaxChars);
        I32(GuildPackets.GdRecommendationPoint, g.RecommendationPoint);
        Str(GuildPackets.GdGuildTitle, g.Title, GuildPackets.GuildTitleMaxChars);

        var image = logo ?? Array.Empty<byte>();
        int logoLen = Math.Min(image.Length, GuildPackets.GuildLogoMaxBytes);
        I32(GuildPackets.GdGuildLogoLength, logoLen);
        I32(GuildPackets.GdGuildLogoId, g.LogoId);
        if (logoLen > 0) Array.Copy(image, 0, b, GuildPackets.GdGuildLogo, logoLen);

        Str(GuildPackets.GdGuildPromotion, g.Promotion, GuildPackets.GuildPromotionMaxChars);
        b[GuildPackets.GdIsGuildWarAcceptable] = (byte)(g.WarAcceptable ? 1 : 0);
        Time(GuildPackets.GdGuildWarAcceptableToggleTime, g.WarToggleTime);
        I32(GuildPackets.GdGuildGeneralCoin, g.GeneralCoin);
        I32(GuildPackets.GdForeverEmblemId, g.ForeverEmblemId);
        I32(GuildPackets.GdEmblemId, g.EmblemId);
        Time(GuildPackets.GdUnknownTime2348, 0);
        I32(GuildPackets.GdGuildPreference, g.Preference);
        I32(GuildPackets.GdJoinMinLevel, g.JoinMinLevel);
        I32(GuildPackets.GdJoinMaxLevel, g.JoinMaxLevel);
        I32(GuildPackets.GdGuildJoinType, g.JoinType);
        I64(GuildPackets.GdLastWeekPlayTime, g.LastWeekPlayTime);
        I64(GuildPackets.GdThisWeekPlayTime, g.ThisWeekPlayTime);
        I32(GuildPackets.GdAddAccountLimitValue, g.AddAccountLimit);
        return b;
    }

    /// <summary>One 0xF0-byte GuildMemberData from a stored row. State and CanGuildWar are
    /// runtime-only: the real load loop forces State = 2 (offline) for everyone and lets the
    /// sessions flip it, which is what <see cref="MemberState"/> then does.</summary>
    public static byte[] BuildGuildMemberDataBlob(CharacterStore.GuildMemberRow m)
    {
        var b = new byte[GuildPackets.GuildMemberDataSize];
        void I32(int off, int v) => BitConverter.GetBytes(v).CopyTo(b, off);
        void I64(int off, long v) => BitConverter.GetBytes(v).CopyTo(b, off);

        I32(GuildPackets.GmUserDbId, m.UserDbId);
        WriteWString(b, GuildPackets.GmName, m.Name, GuildPackets.GuildNameMaxChars);
        I32(GuildPackets.GmWorldId, m.WorldId);
        I32(GuildPackets.GmGuardId, m.GuardId);
        I32(GuildPackets.GmSectionId, m.SectionId);
        I32(GuildPackets.GmGuildGroupId, m.GuildGroupId);
        I32(GuildPackets.GmUserLevel, m.UserLevel);
        I32(GuildPackets.GmRace, m.Race);
        I32(GuildPackets.GmUserClass, m.UserClass);
        I32(GuildPackets.GmGender, m.Gender);
        I32(GuildPackets.GmState, StateOffline);
        I32(GuildPackets.GmWeeklyContributionPoint, m.WeeklyContribution);
        I64(GuildPackets.GmTotalContributionPoint, m.TotalContribution);
        I32(GuildPackets.GmUnknown0080, -1);                 // col 14 loads -1
        WriteWString(b, GuildPackets.GmIntroduce, m.Introduce, GuildPackets.MemberIntroduceMaxChars);
        I64(GuildPackets.GmLastLogoutTime, m.LastLogoutTime);
        I64(GuildPackets.GmAccountId, m.AccountId);
        I64(GuildPackets.GmGuildJoinDate, m.GuildJoinDate);
        return b;
    }

    /// <summary>One 0x28-byte GuildGroupData: `i32 id, wchar[16] name, i32 authority`.</summary>
    public static byte[] BuildGuildGroupDataBlob(CharacterStore.GuildGroupRow g)
    {
        var b = new byte[GuildPackets.GuildGroupDataSize];
        BitConverter.GetBytes(g.GuildGroupId).CopyTo(b, GuildPackets.GgGuildGroupId);
        WriteWString(b, GuildPackets.GgName, g.Name, GuildPackets.GuildGroupNameMaxChars);
        BitConverter.GetBytes(g.Authority).CopyTo(b, GuildPackets.GgAuthority);
        return b;
    }

    /// <summary>GuildData carries the logo ID as an i32 and the frame repeats it as a wstring;
    /// the real Arbiter sends "" when there is no logo, which is the only form the capture has.</summary>
    private static string LogoIdOf(CharacterStore.GuildRow g)
        => g.LogoId == 0 ? string.Empty : g.LogoId.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// Unix seconds as the 16-byte <c>tagTIMESTAMP_STRUCT</c> GuildData stores. 0 (and anything
    /// before the epoch) becomes 1970-01-01, which is exactly what the capture's three unset
    /// timestamps hold.
    /// </summary>
    public static byte[] Timestamp(long unixSeconds)
    {
        if (unixSeconds <= 0) return GuildPackets.BuildEpochTimestamp();
        var t = DateTimeOffset.FromUnixTimeSeconds(unixSeconds).UtcDateTime;
        return GuildPackets.BuildTimestamp(t.Year, t.Month, t.Day, t.Hour, t.Minute, t.Second);
    }

    /// <summary>NUL-terminated UTF-16LE into a fixed field, truncated so the terminator always
    /// fits. The same rule PartyPackets.WriteName follows.</summary>
    private static void WriteWString(byte[] b, int off, string? s, int maxChars)
    {
        string v = s ?? string.Empty;
        int n = Math.Min(v.Length, maxChars - 1);
        for (int i = 0; i < n; i++)
        {
            b[off + i * 2] = (byte)v[i];
            b[off + i * 2 + 1] = (byte)(v[i] >> 8);
        }
    }
}
