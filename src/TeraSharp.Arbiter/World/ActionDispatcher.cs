using Microsoft.Extensions.Logging;
using TeraSharp.Arbiter.Network;
using TeraSharp.Arbiter.Protocol;

namespace TeraSharp.Arbiter.World;

// =============================================================================================
// ActionDispatcher - the one place Arbiter-owned subsystems' output turns into sends (T41).
//
// PartyManager (status/PARTY-DESIGN.md section 10) and GuildHandlers (status/GUILD-DESIGN.md
// section 10) are both pure: they answer an input with a LIST of things to send and never touch
// a socket. That is what makes them golden-testable, and it is also what left each of them with
// its own twenty-line wiring block for the human to paste into HandlerRegistry. Two copies of
// the same loop is one copy too many, and they had already drifted - one addressed clients by
// tunnel ticket, the other by character db id, and only one of them knew that GameSession.
// SendByDef resolves .def files from the SHIPPED registry (see ResolveDef below, which is the
// whole reason the guild corrections would otherwise never reach the wire).
//
// So: both action sets implement IArbiterActions, every emitted item implements IArbiterAction,
// and this class walks them ONCE, in emission order, and performs the sends. The human's part
// becomes three lines.
//
// It does not own any state and it does not throw. An unresolvable recipient - the player went
// offline between the manager deciding to tell them and the dispatcher getting there, which is
// entirely normal for a guild-wide broadcast - is logged and skipped.
//
// The session type is behind IClientSink so the tests can use a fake; ForSessions() binds it to
// the real GameSession, whose Send / SendByDef / SendRawBody already have these exact
// signatures.
// =============================================================================================

/// <summary>Which table resolves a recipient. <see cref="None"/> is the default so an action set
/// produced by a World frame - which has no originating client - says so by omission.</summary>
public enum RecipientKind
{
    None = 0,
    /// <summary>A tunnel ticket: GameSession.TunnelKey, what SA_BYPASS_TO_CLIENT's UserList
    /// carries and what PartyManager addresses by.</summary>
    Ticket,
    /// <summary>A character db id: GameSession.PlayerId, what the guild tables key on and what
    /// GuildHandlers addresses by.</summary>
    PlayerId,
}

/// <summary>Who a packet is for. Two id spaces, one struct, so an action does not have to pick
/// a field name and the dispatcher does not have to guess which one is meaningful.</summary>
public readonly record struct Recipient(RecipientKind Kind, uint Id)
{
    public static readonly Recipient None = new(RecipientKind.None, 0);
    public static Recipient Ticket(uint ticket) => new(RecipientKind.Ticket, ticket);
    public static Recipient Player(int playerId) => new(RecipientKind.PlayerId, unchecked((uint)playerId));

    public override string ToString() => Kind switch
    {
        RecipientKind.Ticket => $"ticket {Id}",
        RecipientKind.PlayerId => $"player {(int)Id}",
        _ => "nobody",
    };
}

/// <summary>Marker for anything an Arbiter subsystem can emit, so one ordered list can hold both
/// halves and the dispatcher can replay them in the order they were produced.</summary>
public interface IArbiterAction { }

/// <summary>
/// One packet for one client. Exactly one of <see cref="Fields"/>, <see cref="RawPacket"/> and
/// <see cref="RawBody"/> is non-null:
/// <list type="bullet">
/// <item>Fields - a def-driven packet; the dispatcher encodes it (see ActionDispatcher.ResolveDef).</item>
/// <item>RawPacket - a finished packet, header included. World built it; pass it straight through.</item>
/// <item>RawBody - a body the dispatcher must frame by packet name.</item>
/// </list>
/// </summary>
public interface IArbiterClientAction : IArbiterAction
{
    Recipient To { get; }
    string PacketName { get; }
    IReadOnlyDictionary<string, object>? Fields { get; }
    byte[]? RawPacket { get; }
    byte[]? RawBody { get; }
}

/// <summary>One Arbiter -&gt; World frame.</summary>
public interface IArbiterWorldAction : IArbiterAction
{
    ushort Opcode { get; }
    byte[] Payload { get; }
}

/// <summary>
/// Everything one input produced. <see cref="Ordered"/> is the authoritative list - the per-kind
/// lists the managers also expose are the same items, kept because their own tests read them.
/// </summary>
public interface IArbiterActions
{
    /// <summary>The session that caused this, so a rejection has somewhere to go.
    /// <see cref="Recipient.None"/> when the input was a World frame.</summary>
    Recipient Origin { get; }

    /// <summary>Every client packet and World frame, in the order the subsystem produced them.</summary>
    IReadOnlyList<IArbiterAction> Ordered { get; }

    /// <summary>Why nothing (or not everything) happened. Null on the happy path.</summary>
    string? Rejected { get; }
}

/// <summary>
/// The three send operations the dispatcher needs. GameSession already has all three with these
/// signatures, so <see cref="ActionDispatcher.ForSessions"/>'s adapter is a one-liner and the
/// tests can substitute a recording fake.
/// </summary>
public interface IClientSink
{
    void SendByDef(string packetName, IReadOnlyDictionary<string, object> fields);
    void SendRawBody(string packetName, byte[] body);
    void Send(byte[] framedPacket);
}

/// <summary>What one Dispatch call did. Counts rather than a bool, because "three of the five
/// guild members were online" is the normal case and is worth seeing in a log.</summary>
public readonly record struct DispatchResult(
    int ClientsSent, int ClientsDropped, int WorldSent, int WorldFailed, bool RejectionSent)
{
    public bool AnythingSent => ClientsSent > 0 || WorldSent > 0 || RejectionSent;
    public override string ToString() =>
        $"{ClientsSent} sent, {ClientsDropped} dropped, {WorldSent} to world, " +
        $"{WorldFailed} world failed{(RejectionSent ? ", rejection relayed" : "")}";
}


/// <summary>
/// The ordinary client action: a packet for one recipient. PartyManager and GuildHandlers each
/// predate this and keep their own record (ClientAction / GuildClientAction) because their tests
/// read the typed lists; anything written after T41 should use this one rather than add a third.
/// </summary>
public readonly record struct ClientPacket(
    Recipient To, string PacketName, IReadOnlyDictionary<string, object>? Fields,
    byte[]? RawPacket, byte[]? RawBody) : IArbiterClientAction
{
    /// <summary>A def-driven packet for a character db id.</summary>
    public static ClientPacket ToPlayer(int playerId, string name, IReadOnlyDictionary<string, object> fields)
        => new(Recipient.Player(playerId), name, fields, null, null);

    /// <summary>A def-driven packet for a tunnel ticket.</summary>
    public static ClientPacket ToTicket(uint ticket, string name, IReadOnlyDictionary<string, object> fields)
        => new(Recipient.Ticket(ticket), name, fields, null, null);

    /// <summary>A finished packet, header included - one World built.</summary>
    public static ClientPacket Framed(Recipient to, byte[] packet) => new(to, "(raw)", null, packet, null);

    /// <summary>A body the dispatcher frames by packet name.</summary>
    public static ClientPacket Body(Recipient to, string name, byte[] body) => new(to, name, null, null, body);
}

/// <summary>
/// A ready-made <see cref="IArbiterActions"/> for subsystems that do not need their own. Keeps
/// one ordered list and nothing else; <see cref="PartyActions"/> and <see cref="GuildActions"/>
/// are the two that came first and carry extra typed lists their tests read.
/// </summary>
public class ArbiterActions : IArbiterActions
{
    private readonly List<IArbiterAction> _ordered = new();

    public Recipient Origin { get; set; } = Recipient.None;
    public IReadOnlyList<IArbiterAction> Ordered => _ordered;
    public string? Rejected { get; set; }
    public bool IsEmpty => _ordered.Count == 0;

    public ArbiterActions Reject(string why) { Rejected = why; return this; }
    public void Client(IArbiterClientAction a) => _ordered.Add(a);
    public void World(ushort opcode, byte[] payload) => _ordered.Add(new WorldAction(opcode, payload));

    /// <summary>Shorthand for the common case: a def-driven packet to a character db id.</summary>
    public void ToPlayer(int playerId, string packetName, IReadOnlyDictionary<string, object> fields)
        => Client(ClientPacket.ToPlayer(playerId, packetName, fields));
}

public sealed class ActionDispatcher
{
    private readonly Func<uint, IClientSink?> _byTicket;
    private readonly Func<int, IClientSink?> _byPlayerId;
    private readonly Func<ushort, byte[], bool> _sendToWorld;
    private readonly ILogger _log;

    public ActionDispatcher(
        Func<uint, IClientSink?> byTicket,
        Func<int, IClientSink?> byPlayerId,
        Func<ushort, byte[], bool> sendToWorld,
        ILogger log)
    {
        _byTicket = byTicket ?? throw new ArgumentNullException(nameof(byTicket));
        _byPlayerId = byPlayerId ?? throw new ArgumentNullException(nameof(byPlayerId));
        _sendToWorld = sendToWorld ?? throw new ArgumentNullException(nameof(sendToWorld));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    /// <summary>
    /// Bind the dispatcher to real sessions. This is the signature the wiring uses:
    /// two lookups and a World sender.
    /// </summary>
    public static ActionDispatcher ForSessions(
        Func<uint, GameSession?> byTicket,
        Func<int, GameSession?> byPlayerId,
        Func<ushort, byte[], bool> sendToWorld,
        ILogger log)
        => new(t => Wrap(byTicket(t)), p => Wrap(byPlayerId(p)), sendToWorld, log);

    private static IClientSink? Wrap(GameSession? s) => s == null ? null : new SessionSink(s);

    /// <summary>
    /// A def override, consulted before the session's own registry. Return null to let the
    /// session encode normally.
    ///
    /// <para>This exists because <c>GameSession.SendByDef</c> resolves the packet definition from
    /// the SHIPPED <c>DefinitionRegistry</c>, and ten of the guild .def files are wrong
    /// (status/GUILD-DESIGN.md section 5.5). Without this hook a handler emitting
    /// S_ADD_GUILD_MEMBER would put the .def's 0x33-byte body on the wire instead of the 0x38
    /// the Arbiter's own dumper guard proves. The wiring passes
    /// <c>n =&gt; GuildHandlers.ResolveDef(null, n)</c>: a null registry makes that method return
    /// the corrections and nothing else.</para>
    /// </summary>
    public Func<string, PacketDef?>? ResolveDef { get; init; }

    /// <summary>
    /// Relay <see cref="IArbiterActions.Rejected"/> to the originating session as
    /// S_SYSTEM_MESSAGE_CUSTOM. On by default because a silent no-op is the worst thing a guild
    /// or party command can do; turn it off for a subsystem whose rejections are internal.
    /// </summary>
    public bool RelayRejections { get; init; } = true;

    /// <summary>S_SYSTEM_MESSAGE_CUSTOM (0x994C): one wide string, a plain literal, never an
    /// <c>@id</c>. The same channel every Arbiter-side GM command answers on - see
    /// GmCommandHandlers.SendCustom and status/GM-DESIGN.md.</summary>
    public const string RejectionPacket = "S_SYSTEM_MESSAGE_CUSTOM";
    /// <summary>The single field of S_SYSTEM_MESSAGE_CUSTOM.</summary>
    public const string RejectionField = "formatted";

    /// <summary>
    /// Perform every send in <paramref name="actions"/>, in emission order, then relay the
    /// rejection if there is one. Never throws: a recipient that cannot be resolved is logged
    /// and skipped, and so is an action whose shape is malformed.
    /// </summary>
    /// <param name="source">A short label for the log - "party", "guild". Not used for routing.</param>
    public DispatchResult Dispatch(IArbiterActions actions, string source = "arbiter")
    {
        if (actions == null) return default;

        int sent = 0, dropped = 0, worldSent = 0, worldFailed = 0;

        foreach (var action in actions.Ordered)
        {
            switch (action)
            {
                case IArbiterClientAction c:
                    if (SendToClient(c, source)) sent++; else dropped++;
                    break;
                case IArbiterWorldAction w:
                    bool ok;
                    try { ok = _sendToWorld(w.Opcode, w.Payload); }
                    catch (Exception ex)
                    {
                        ok = false;
                        _log.LogWarning("{Source}: sending 0x{Op:X4} to World threw: {Msg}", source, w.Opcode, ex.Message);
                    }
                    if (ok) worldSent++;
                    else
                    {
                        worldFailed++;
                        _log.LogWarning("{Source}: World did not accept 0x{Op:X4} ({Len} B payload)",
                            source, w.Opcode, w.Payload?.Length ?? 0);
                    }
                    break;
                default:
                    _log.LogError("{Source}: {Type} is neither a client nor a World action - dropped",
                        source, action?.GetType().Name ?? "null");
                    break;
            }
        }

        bool rejectionSent = RelayRejection(actions, source);

        var result = new DispatchResult(sent, dropped, worldSent, worldFailed, rejectionSent);
        if (dropped > 0 || worldFailed > 0)
            _log.LogDebug("{Source}: {Result}", source, result);
        return result;
    }

    private bool SendToClient(IArbiterClientAction c, string source)
    {
        var sink = Resolve(c.To);
        if (sink == null)
        {
            // Entirely normal: a guild-wide broadcast names every member and most are offline.
            _log.LogDebug("{Source}: {Packet} for {To} dropped - no session", source, c.PacketName, c.To);
            return false;
        }

        try
        {
            if (c.RawPacket != null) { sink.Send(c.RawPacket); return true; }
            if (c.RawBody != null) { sink.SendRawBody(c.PacketName, c.RawBody); return true; }
            if (c.Fields == null)
            {
                _log.LogError("{Source}: {Packet} for {To} carries neither fields nor bytes - dropped",
                    source, c.PacketName, c.To);
                return false;
            }

            var def = ResolveDef?.Invoke(c.PacketName);
            if (def != null) sink.SendRawBody(c.PacketName, new DefinitionWriter().Write(def, c.Fields));
            else sink.SendByDef(c.PacketName, c.Fields);
            return true;
        }
        catch (Exception ex)
        {
            _log.LogWarning("{Source}: sending {Packet} to {To} threw: {Msg}",
                source, c.PacketName, c.To, ex.Message);
            return false;
        }
    }

    private bool RelayRejection(IArbiterActions actions, string source)
    {
        string? why = actions.Rejected;
        if (string.IsNullOrEmpty(why)) return false;

        if (!RelayRejections)
        {
            _log.LogDebug("{Source}: {Why}", source, why);
            return false;
        }

        var sink = Resolve(actions.Origin);
        if (sink == null)
        {
            // A World frame has no originating client, so this is the usual path for OnWorldFrame.
            _log.LogDebug("{Source}: {Why} (no originating session to tell)", source, why);
            return false;
        }

        try
        {
            sink.SendByDef(RejectionPacket, new Dictionary<string, object> { [RejectionField] = why });
            return true;
        }
        catch (Exception ex)
        {
            _log.LogWarning("{Source}: relaying the rejection threw: {Msg}", source, ex.Message);
            return false;
        }
    }

    private IClientSink? Resolve(Recipient to)
    {
        try
        {
            return to.Kind switch
            {
                RecipientKind.Ticket => _byTicket(to.Id),
                RecipientKind.PlayerId => _byPlayerId(unchecked((int)to.Id)),
                _ => null,
            };
        }
        catch (Exception ex)
        {
            _log.LogWarning("resolving {To} threw: {Msg}", to, ex.Message);
            return null;
        }
    }

    /// <summary>The real session behind <see cref="IClientSink"/>. Nothing but a forwarder -
    /// GameSession's three send methods already match the interface exactly.</summary>
    private sealed class SessionSink : IClientSink
    {
        private readonly GameSession _s;
        public SessionSink(GameSession s) => _s = s;
        public void SendByDef(string packetName, IReadOnlyDictionary<string, object> fields)
            => _s.SendByDef(packetName, fields);
        public void SendRawBody(string packetName, byte[] body) => _s.SendRawBody(packetName, body);
        public void Send(byte[] framedPacket) => _s.Send(framedPacket);
    }
}
