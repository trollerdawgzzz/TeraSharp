using Microsoft.Extensions.Logging;
using TeraSharp.Arbiter.Network;
using TeraSharp.Arbiter.Persistence;

namespace TeraSharp.Arbiter.World;

// =============================================================================================
// GuildWarManager - declare, view and withdraw a guild war (T80).
// Research and the frame-by-frame trace: status/GUILD-WAR.md.
//
// cap_social4 contains one complete episode on a single client, so every packet below is pinned
// to bytes: open the window (client 1621/1623), page the history (1627/1628), check an opponent
// (3381/3382), declare (3384 -> 3387 + tap 6023), see the war in the window (3389), withdraw
// (4449 -> 4450 + tap 7468), and the window empty again (4453).
//
// WHAT CROSSES THE LINK. Two A->W pushes, and they are pushes - World never answers:
//   0x14A3 AS_DECLARE_GUILD_WAR   tap 6023, 17-byte payload
//   0x14AC AS_END_GUILD_WAR       tap 7468, 20-byte payload
// A third, 0x14AF AS_NOTIFY_GUILD_WAR_INFO (tap 5094 / 5846 / 5950), is an enter-world push of
// [i32 playerId][u8 flag]. 0x14B3 AS_GUILD_WAR_ADMIN_MAINTAINCOST is NOT a guild-war runtime
// frame: it goes out once, in the World handshake burst at tap seq 29, with eight zero bytes.
//
// PERSISTED, unlike the party board. The real Arbiter keeps GuildWar and GuildWarHistory tables
// in PlanetDB; CharacterStore now has guild_wars and guild_war_history to match, and
// thisGuildDeclareCount is read back out of them.
//
// PURE, like PartyMatchManager: every entry point answers with an ArbiterActions and never
// touches a socket.
//
// The human-owned diff after this file:
//
//   Handlers/HandlerRegistry.cs -
//       foreach (var (warName, warOp) in GuildWarManager.ClientOpcodes)
//           Reg(warName, GuildWarManager.MinBodyLength(warOp),
//               (s, body) => GuildWarManager.OnClientPacket(s, warOp, body));
//
//   (nothing in WorldBridge - no World frame belongs to guild war; nothing in WorldEntry or
//    GameSession - the enter-world push rides SocialHandlers.RegisterChat, the edge T49, T51,
//    T76 and T78 all ride.)
//
// Offsets are PACKET-relative; PacketDispatcher hands a handler the BODY, so body index =
// packet offset - 4.
// =============================================================================================

/// <summary>Declare, view and withdraw a guild war. One table per Arbiter process, in SQLite.</summary>
public static class GuildWarManager
{
    // =========================================================================================
    // 1. Opcodes, guards and constants
    // =========================================================================================

    /// <summary>C_OPEN_GUILD_WAR_WINDOW (38135).</summary>
    public const ushort C_OPEN_GUILD_WAR_WINDOW = 0x94F7;
    /// <summary>C_VIEW_GUILD_WAR (29893) - one page of the history.</summary>
    public const ushort C_VIEW_GUILD_WAR = 0x74C5;
    /// <summary>C_CHECK_TO_DECLARE_GUILD_WAR (30842) - may I declare on this guild?</summary>
    public const ushort C_CHECK_TO_DECLARE_GUILD_WAR = 0x787A;
    /// <summary>C_DECLARE_GUILD_WAR (54118).</summary>
    public const ushort C_DECLARE_GUILD_WAR = 0xD366;
    /// <summary>C_WITHDRAW_GUILD_WAR (64870).</summary>
    public const ushort C_WITHDRAW_GUILD_WAR = 0xFD66;

    /// <summary>S_OPEN_GUILD_WAR_WINDOW (28989) - the live wars plus the declare counters.</summary>
    public const string S_OPEN_GUILD_WAR_WINDOW = "S_OPEN_GUILD_WAR_WINDOW";
    /// <summary>S_VIEW_GUILD_WAR (28255) - one page of finished wars.</summary>
    public const string S_VIEW_GUILD_WAR = "S_VIEW_GUILD_WAR";
    /// <summary>S_CHECK_TO_DECLARE_GUILD_WAR (23753).</summary>
    public const string S_CHECK_TO_DECLARE_GUILD_WAR = "S_CHECK_TO_DECLARE_GUILD_WAR";
    /// <summary>S_NOTIFY_GUILD_WAR_STATUS_CHANGE (44061) - an EMPTY packet, header only.</summary>
    public const string S_NOTIFY_GUILD_WAR_STATUS_CHANGE = "S_NOTIFY_GUILD_WAR_STATUS_CHANGE";
    /// <summary>S_TOTAL_GUILD_WAR_DATA (30808) - a login push, one int32.</summary>
    public const string S_TOTAL_GUILD_WAR_DATA = "S_TOTAL_GUILD_WAR_DATA";

    /// <summary>AS_DECLARE_GUILD_WAR - A-&gt;W push, frame 23 / payload 17. Tap 6023.</summary>
    public const ushort AS_DECLARE_GUILD_WAR = 0x14A3;
    /// <summary>AS_END_GUILD_WAR - A-&gt;W push, frame 26 / payload 20. Tap 7468.</summary>
    public const ushort AS_END_GUILD_WAR = 0x14AC;
    /// <summary>AS_NOTIFY_GUILD_WAR_INFO - A-&gt;W push, frame 11 / payload 5. Tap 5094/5846/5950.</summary>
    public const ushort AS_NOTIFY_GUILD_WAR_INFO = 0x14AF;

    /// <summary>
    /// How many wars one guild may declare. 10 in every captured frame - client 1623, 3382,
    /// 3389 and 4453 all carry it. Not read from anything we have, so it is a constant here.
    /// </summary>
    public const int DeclareLimit = 10;

    /// <summary>
    /// What a declaration costs. 1500 in cap_social4_client frame 3382's guildWarMoney and in
    /// the attacker's money slot of frame 3389.
    /// </summary>
    public const int DeclareCost = 1500;

    /// <summary>
    /// The value both guild blocks carry in the slot after their money. 250 for BOTH sides in
    /// frame 3389 - the attacker who paid 1500 and the defender who paid nothing - so it is not
    /// a per-side amount. Reproduced, not explained; status/GUILD-WAR.md section 4.
    /// </summary>
    public const int GuildBlockUnk2 = 250;

    /// <summary>SMT 1788, the confirmation text S_CHECK_TO_DECLARE_GUILD_WAR carries on a yes.
    /// cap_social4_client frame 3382 is <c>@1788</c> with no parameters.</summary>
    public const int SmtDeclareConfirm = 1788;

    /// <summary>S_VIEW_GUILD_WAR.result / AS_END_GUILD_WAR.reason: the attacker withdrew.</summary>
    public const int ResultWithdrew = 1;

    /// <summary>A client packet's <c>[u16 length][u16 opcode]</c> header.</summary>
    public const int ClientHeaderSize = 4;

    /// <summary>The five client packets guild war owns.</summary>
    public static readonly (string Name, ushort Opcode)[] ClientOpcodes =
    {
        ("C_OPEN_GUILD_WAR_WINDOW",      C_OPEN_GUILD_WAR_WINDOW),
        ("C_VIEW_GUILD_WAR",             C_VIEW_GUILD_WAR),
        ("C_CHECK_TO_DECLARE_GUILD_WAR", C_CHECK_TO_DECLARE_GUILD_WAR),
        ("C_DECLARE_GUILD_WAR",          C_DECLARE_GUILD_WAR),
        ("C_WITHDRAW_GUILD_WAR",         C_WITHDRAW_GUILD_WAR),
    };

    /// <summary>
    /// Minimum BODY length, from each packet's own shape in the capture. C_VIEW_GUILD_WAR and
    /// C_WITHDRAW_GUILD_WAR carry one int32 (client frames 1627 and 4449); the two name packets
    /// carry a string ref plus its data (3381, 3384); the window opener carries nothing (1621).
    /// </summary>
    public static int MinBodyLength(ushort op) => op switch
    {
        C_VIEW_GUILD_WAR => 4,
        C_WITHDRAW_GUILD_WAR => 4,
        C_CHECK_TO_DECLARE_GUILD_WAR => 2,
        C_DECLARE_GUILD_WAR => 2,
        _ => 0,
    };

    /// <summary>Is this one of the five?</summary>
    public static bool IsArbiterSide(ushort op)
    {
        foreach (var (_, code) in ClientOpcodes) if (code == op) return true;
        return false;
    }

    // =========================================================================================
    // 2. State - the store, and the logger
    // =========================================================================================

    private static ILogger Log = Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;

    /// <summary>Give the manager the process logger, beside the other four subsystems.</summary>
    public static void UseLogger(ILogger log) => Log = log ?? Log;

    /// <summary>
    /// Where the wars live. Defaults to the process store; a test sets it to its own in-memory
    /// one. Same shape as Program.Store, kept behind a property so nothing has to be threaded
    /// through Program.cs.
    /// </summary>
    public static CharacterStore? Store { get; set; }

    private static CharacterStore? TheStore => Store ?? global::TeraSharp.Arbiter.Program.Store;

    /// <summary>Drop the test override. Tests only.</summary>
    public static void ResetForTests() => Store = null;

    // =========================================================================================
    // 3. Parsers - packet-relative, read the way the client writes them
    // =========================================================================================

    /// <summary>
    /// A null-terminated UTF-16LE string at a PACKET-relative offset, read out of the BODY. 0,
    /// or an offset past the body, is the codec's "no string".
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

    /// <summary>
    /// The one field of C_CHECK_TO_DECLARE_GUILD_WAR and C_DECLARE_GUILD_WAR: a string ref at
    /// packet 4 and the name behind it. Client frames 3381 and 3384 are both
    /// <c>06 00 66 00 64 00 68 00 00 00</c> - offset 6, then <c>fdh</c>.
    /// </summary>
    public static string ParseOpponentName(ReadOnlySpan<byte> body)
        => body.Length < 2 ? string.Empty : ReadWString(body, BitConverter.ToUInt16(body));

    /// <summary>One int32 at packet 4 - C_VIEW_GUILD_WAR.page and C_WITHDRAW_GUILD_WAR.opponentGuildDbId.</summary>
    public static int ParseInt32(ReadOnlySpan<byte> body)
        => body.Length < 4 ? 0 : BitConverter.ToInt32(body);

    // =========================================================================================
    // 4. Builders - field sets, named as V100Definitions names them
    // =========================================================================================

    /// <summary>One row of S_OPEN_GUILD_WAR_WINDOW, built from a live war row plus both names.</summary>
    public static Dictionary<string, object> BuildWarRow(
        CharacterStore.GuildWarRow war, string attackName, string defendName)
    {
        ArgumentNullException.ThrowIfNull(war);
        return new Dictionary<string, object>
        {
            ["attackGuildId"] = (long)war.AttackGuildId,
            ["attackFlag"] = (byte)1,
            ["attackUnk1"] = 0,
            ["attackMoney"] = war.Money,
            ["attackUnk2"] = GuildBlockUnk2,
            ["attackUnk3"] = (byte)0,
            ["defendGuildId"] = (long)war.DefendGuildId,
            ["defendFlag"] = (byte)0,
            ["defendUnk1"] = 0,
            ["defendMoney"] = 0L,
            ["defendUnk2"] = GuildBlockUnk2,
            ["defendUnk3"] = (byte)0,
            ["date"] = war.DeclaredAt,
            ["attackName"] = attackName ?? string.Empty,
            ["attackEmblem"] = string.Empty,
            ["defendName"] = defendName ?? string.Empty,
            ["defendEmblem"] = string.Empty,
        };
    }

    /// <summary>S_OPEN_GUILD_WAR_WINDOW for one guild.</summary>
    public static Dictionary<string, object> BuildWindowFields(int declareCount, IReadOnlyList<object> wars)
        => new()
        {
            ["thisGuildDeclareCount"] = declareCount,
            ["thisGuildDeclareLimit"] = DeclareLimit,
            ["wars"] = new List<object>(wars ?? Array.Empty<object>()),
        };

    /// <summary>S_VIEW_GUILD_WAR - one page of finished wars. The capture only ever has page 1
    /// of an empty history (client frames 1628 / 1647 / 1671), so maxPages is the page count.</summary>
    public static Dictionary<string, object> BuildViewFields(int page, int maxPages, IReadOnlyList<object> battles)
        => new()
        {
            ["page"] = page,
            ["maxPages"] = maxPages,
            ["battles"] = new List<object>(battles ?? Array.Empty<object>()),
        };

    /// <summary>One row of S_VIEW_GUILD_WAR.</summary>
    public static Dictionary<string, object> BuildBattleRow(
        CharacterStore.GuildWarHistoryRow h, string attackName, string defendName)
    {
        ArgumentNullException.ThrowIfNull(h);
        return new Dictionary<string, object>
        {
            ["result"] = h.Result,
            ["date"] = h.EndedAt,
            ["attackName"] = attackName ?? string.Empty,
            ["attackEmblem"] = string.Empty,
            ["defendName"] = defendName ?? string.Empty,
            ["defendEmblem"] = string.Empty,
        };
    }

    /// <summary>
    /// S_CHECK_TO_DECLARE_GUILD_WAR. <paramref name="ok"/> is the byte the writer always passes
    /// as a literal 1 on the success path (Arb_part_058.c:8238).
    /// </summary>
    public static Dictionary<string, object> BuildCheckFields(bool ok, int declareCount)
        => new()
        {
            ["result"] = (byte)(ok ? 1 : 0),
            ["guildWarMoney"] = DeclareCost,
            ["thisGuildDeclareCount"] = declareCount,
            ["thisGuildDeclareLimit"] = DeclareLimit,
            ["reasonSysMsgFormatted"] = "@" + SmtDeclareConfirm.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["myGuildImageId"] = string.Empty,
            ["opponentGuildImageId"] = string.Empty,
        };

    /// <summary>
    /// S_NOTIFY_GUILD_WAR_STATUS_CHANGE, a HEADER-ONLY packet - client frames 3387 and 4450
    /// are four bytes each, body length zero. Emitted as a raw body rather than through a def
    /// so it does not depend on a def file existing for a packet that has no fields.
    /// </summary>
    public static ClientPacket StatusChanged(int playerId)
        => ClientPacket.Body(Recipient.Player(playerId), S_NOTIFY_GUILD_WAR_STATUS_CHANGE,
                             Array.Empty<byte>());

    /// <summary>S_TOTAL_GUILD_WAR_DATA - one int32. Client frame 3214 is 0.</summary>
    public static Dictionary<string, object> BuildTotalFields(int count)
        => new() { ["countOfGuildWar"] = count };

    /// <summary>
    /// AS_DECLARE_GUILD_WAR (0x14A3) payload, 17 bytes. Tap 6023 is
    /// <c>01 00 00 00 00 00 00 00  02 00 00 00  03 00 00 00  00</c> - the war id as an int64,
    /// then the two guild ids, then a byte.
    /// </summary>
    public static byte[] BuildAsDeclare(long warId, int attackGuildId, int defendGuildId, byte flag = 0)
    {
        var p = new byte[17];
        BitConverter.GetBytes(warId).CopyTo(p, 0);
        BitConverter.GetBytes(attackGuildId).CopyTo(p, 8);
        BitConverter.GetBytes(defendGuildId).CopyTo(p, 12);
        p[16] = flag;
        return p;
    }

    /// <summary>
    /// AS_END_GUILD_WAR (0x14AC) payload, 20 bytes. Tap 7468 is
    /// <c>01 00 00 00 00 00 00 00  02 00 00 00  03 00 00 00  01 00 00 00</c> - the same war id
    /// and pair, then the reason, 1 for a withdrawal.
    /// </summary>
    public static byte[] BuildAsEnd(long warId, int attackGuildId, int defendGuildId, int reason)
    {
        var p = new byte[20];
        BitConverter.GetBytes(warId).CopyTo(p, 0);
        BitConverter.GetBytes(attackGuildId).CopyTo(p, 8);
        BitConverter.GetBytes(defendGuildId).CopyTo(p, 12);
        BitConverter.GetBytes(reason).CopyTo(p, 16);
        return p;
    }

    /// <summary>
    /// AS_NOTIFY_GUILD_WAR_INFO (0x14AF) payload, 5 bytes: <c>[i32 playerId][u8 flag]</c>.
    /// Tap 5094 is <c>02 00 00 00 00</c> for a character with no guild and 5846 / 5950 are
    /// <c>EB 03 00 00 01</c> / <c>01 00 00 00 01</c> for the two members of guild 2 - so the
    /// byte tracks "this character is in a guild", not "at war": all three went out BEFORE the
    /// declare at 01:30:18.
    /// </summary>
    public static byte[] BuildAsNotify(int playerId, bool inGuild)
    {
        var p = new byte[5];
        BitConverter.GetBytes(playerId).CopyTo(p, 0);
        p[4] = (byte)(inGuild ? 1 : 0);
        return p;
    }

    // =========================================================================================
    // 5. The pure core
    // =========================================================================================

    private static ArbiterActions New(int playerId)
        => new() { Origin = Recipient.Player(playerId) };

    private static string NameOf(CharacterStore store, int guildId)
        => store.GetGuild(guildId)?.Name ?? string.Empty;

    /// <summary>C_OPEN_GUILD_WAR_WINDOW - the live wars of the player's guild.</summary>
    public static ArbiterActions OnOpenWindow(int playerId)
    {
        var a = New(playerId);
        var store = TheStore;
        if (store == null) return a.Reject("no store");
        int guildId = store.GetGuildIdOf(playerId);
        var rows = new List<object>();
        if (guildId != 0)
            foreach (var w in store.GetGuildWars(guildId))
                rows.Add(BuildWarRow(w, NameOf(store, w.AttackGuildId), NameOf(store, w.DefendGuildId)));
        a.ToPlayer(playerId, S_OPEN_GUILD_WAR_WINDOW,
            BuildWindowFields(guildId == 0 ? 0 : store.CountGuildWarDeclarations(guildId), rows));
        return a;
    }

    /// <summary>C_VIEW_GUILD_WAR - one page of the finished wars.</summary>
    public static ArbiterActions OnViewHistory(int playerId, int page)
    {
        var a = New(playerId);
        var store = TheStore;
        if (store == null) return a.Reject("no store");
        int guildId = store.GetGuildIdOf(playerId);
        var battles = new List<object>();
        if (guildId != 0)
            foreach (var h in store.GetGuildWarHistory(guildId))
                battles.Add(BuildBattleRow(h, NameOf(store, h.AttackGuildId), NameOf(store, h.DefendGuildId)));
        // One page: the capture never has a second, and maxPages is 0 with an empty history.
        a.ToPlayer(playerId, S_VIEW_GUILD_WAR,
            BuildViewFields(page, battles.Count == 0 ? 0 : 1, battles));
        return a;
    }

    /// <summary>
    /// C_CHECK_TO_DECLARE_GUILD_WAR. <b>A miss answers with SILENCE</b>, which is what
    /// cap_social4_client frame 1691 shows: the player typed their OWN guild name (sdg) and no
    /// S_CHECK_TO_DECLARE_GUILD_WAR came back at all. Frame 3381, naming a different guild, got
    /// one two frames later.
    /// </summary>
    public static ArbiterActions OnCheckToDeclare(int playerId, string opponentName)
    {
        var a = New(playerId);
        var store = TheStore;
        if (store == null) return a.Reject("no store");
        int mine = store.GetGuildIdOf(playerId);
        var them = store.GetGuildByName(opponentName ?? string.Empty);
        if (mine == 0 || them == null || them.GuildId == mine)
        {
            Log.LogDebug("guild war: check against '{Name}' from player {Id} - no answer", opponentName, playerId);
            return a;
        }
        if (store.GetGuildWarBetween(mine, them.GuildId) != null) return a;
        a.ToPlayer(playerId, S_CHECK_TO_DECLARE_GUILD_WAR,
            BuildCheckFields(true, store.CountGuildWarDeclarations(mine)));
        return a;
    }

    /// <summary>
    /// C_DECLARE_GUILD_WAR: the row is written, the client gets an EMPTY
    /// S_NOTIFY_GUILD_WAR_STATUS_CHANGE (client frame 3387 is four bytes - header only) and
    /// World gets AS_DECLARE_GUILD_WAR. The refreshed window at 3389 is the answer to the
    /// C_OPEN_GUILD_WAR_WINDOW the client sends itself at 3388, not a push.
    /// </summary>
    public static ArbiterActions OnDeclare(int playerId, string opponentName, long nowUnix)
    {
        var a = New(playerId);
        var store = TheStore;
        if (store == null) return a.Reject("no store");
        int mine = store.GetGuildIdOf(playerId);
        var them = store.GetGuildByName(opponentName ?? string.Empty);
        if (mine == 0 || them == null || them.GuildId == mine) return a.Reject("no such guild to declare on");
        if (store.CountGuildWarDeclarations(mine) >= DeclareLimit) return a.Reject("declare limit reached");

        long warId = store.DeclareGuildWar(mine, them.GuildId, nowUnix, DeclareCost);
        if (warId == 0) return a.Reject("already at war with that guild");

        a.Client(StatusChanged(playerId));
        a.World(AS_DECLARE_GUILD_WAR, BuildAsDeclare(warId, mine, them.GuildId));
        Log.LogInformation("guild war {War}: guild {A} declared on guild {B}", warId, mine, them.GuildId);
        return a;
    }

    /// <summary>
    /// C_WITHDRAW_GUILD_WAR. Its one int32 is the OPPONENT's guild db id, not a war id - client
    /// frame 4449 is <c>03 00 00 00</c> and guild 3 is the one guild 2 had declared on.
    /// </summary>
    public static ArbiterActions OnWithdraw(int playerId, int opponentGuildId, long nowUnix)
    {
        var a = New(playerId);
        var store = TheStore;
        if (store == null) return a.Reject("no store");
        int mine = store.GetGuildIdOf(playerId);
        if (mine == 0) return a.Reject("not in a guild");
        var war = store.GetGuildWarBetween(mine, opponentGuildId);
        if (war == null) return a.Reject("not at war with that guild");

        store.EndGuildWar(war.WarId, ResultWithdrew, nowUnix);
        a.Client(StatusChanged(playerId));
        a.World(AS_END_GUILD_WAR,
            BuildAsEnd(war.WarId, war.AttackGuildId, war.DefendGuildId, ResultWithdrew));
        Log.LogInformation("guild war {War}: guild {A} withdrew from guild {B}",
            war.WarId, war.AttackGuildId, war.DefendGuildId);
        return a;
    }

    /// <summary>
    /// Enter world: the 0x14AF push to World and the S_TOTAL_GUILD_WAR_DATA count to the client.
    /// Client frame 3214 carries 0 for a character whose guild has no live war.
    /// </summary>
    public static ArbiterActions OnEnterWorld(int playerId)
    {
        var a = New(playerId);
        var store = TheStore;
        if (store == null) return a.Reject("no store");
        int guildId = store.GetGuildIdOf(playerId);
        a.World(AS_NOTIFY_GUILD_WAR_INFO, BuildAsNotify(playerId, guildId != 0));
        a.ToPlayer(playerId, S_TOTAL_GUILD_WAR_DATA,
            BuildTotalFields(guildId == 0 ? 0 : store.GetGuildWars(guildId).Count));
        return a;
    }

    // =========================================================================================
    // 6. The wiring
    // =========================================================================================

    private static WorldBridge? Bridge => global::TeraSharp.Arbiter.Program.World;

    /// <summary>
    /// An ActionDispatcher bound to live sessions. Guild war addresses clients by character db id
    /// only. RelayRejections is off: the real handlers answer a refusal with silence or a
    /// numbered SMT, never with an S_SYSTEM_MESSAGE_CUSTOM literal.
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

    /// <summary>One of <see cref="ClientOpcodes"/> arrived. Always true - all five are ours.</summary>
    public static bool OnClientPacket(GameSession? session, ushort opcode, ReadOnlyMemory<byte> body)
    {
        var chr = session?.SelectedCharacter;
        if (chr == null) return true;
        Protocol.V100Definitions.EnsureRegistered(session!.Definitions);
        var actions = Decide((int)chr.Id, opcode, body.Span, Now());
        if (actions.IsEmpty && actions.Rejected == null) return true;
        Dispatcher(session, Log).Dispatch(actions, "guild-war");
        return true;
    }

    /// <summary>The enter-world edge, called from SocialHandlers.RegisterChat.</summary>
    public static void OnEnterWorld(GameSession? session)
    {
        var chr = session?.SelectedCharacter;
        if (chr == null || !session!.InWorld) return;
        Protocol.V100Definitions.EnsureRegistered(session!.Definitions);
        var actions = OnEnterWorld((int)chr.Id);
        if (actions.IsEmpty && actions.Rejected == null) return;
        Dispatcher(session, Log).Dispatch(actions, "guild-war-enter");
    }

    /// <summary>Unix seconds, the unit S_OPEN_GUILD_WAR_WINDOW's date slot carries - frame 3389
    /// has 1789608618, which is 2026-09-17T01:30:18Z, the second the declare crossed the tap.</summary>
    public static long Now() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    /// <summary>The session-free half, so every test drives this code and not a copy.</summary>
    public static ArbiterActions Decide(int playerId, ushort opcode, ReadOnlySpan<byte> body, long nowUnix)
        => opcode switch
        {
            C_OPEN_GUILD_WAR_WINDOW => OnOpenWindow(playerId),
            C_VIEW_GUILD_WAR => OnViewHistory(playerId, ParseInt32(body)),
            C_CHECK_TO_DECLARE_GUILD_WAR => OnCheckToDeclare(playerId, ParseOpponentName(body)),
            C_DECLARE_GUILD_WAR => OnDeclare(playerId, ParseOpponentName(body), nowUnix),
            C_WITHDRAW_GUILD_WAR => OnWithdraw(playerId, ParseInt32(body), nowUnix),
            _ => New(playerId),
        };
}
