using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using TeraSharp.Arbiter.Network;

namespace TeraSharp.Arbiter.World;

/// <summary>
/// How a player is leaving the world. The trailing two u32s of AS_LEAVE_WORLD
/// (0x1392) are LeaveWorldType + LogoutReason; the real Arbiter uses different
/// values per path. Verified in the decompile: User::OnLeaveWorldTick
/// (Arb_part_029.c) picks (type=3, reason=0) for a lobby return and
/// (type=1, reason=8) for an exit, then User::SendASLeaveWorld (FUN_1403a4820)
/// writes [u64 gameId][u32 type][u32 reason][u32 playerId].
/// </summary>
public enum LeaveMode
{
    /// <summary>Client socket dropped. Same wire values as Exit; tunnel torn down immediately.</summary>
    Disconnect,
    /// <summary>Logout button -> return to character select. Client socket stays open.</summary>
    Lobby,
    /// <summary>Exit button -> close client.</summary>
    Exit,
}

/// <summary>
/// Per-session tunnel reorder state. Each registered player gets its own buffer
/// keyed by the routing field extracted from 0x13F7 headers (conn or idx).
/// </summary>
internal sealed class TunnelReorderBuffer
{
    public readonly SortedDictionary<uint, byte[]> Pending = new();
    public uint NextSeq;
    public DateTime LastDelivery = DateTime.UtcNow;
    /// <summary>Callback to deliver a reordered client packet to the owning session.</summary>
    public Action<byte[]>? Deliver;
}

/// <summary>
/// Arbiter-side endpoint for the real WorldServer. WorldServer opens ~25 TCP
/// sessions and round-robins tunnel packets across them; each 0x13F7 frame
/// carries a sequence number (payload[30..31] >> 3) so we can reorder.
///
///   Frame: [u32 totalLength][u16 opcode][payload]   ÃƒÆ’Ã†â€™Ãƒâ€ Ã¢â‚¬â„¢ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã†â€™Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬Ãƒâ€¦Ã‚Â¡ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â¬ÃƒÆ’Ã†â€™Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã…Â¡Ãƒâ€šÃ‚Â¬ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â plaintext.
///
/// Tunnel routing: each player gets a unique tunnel key (allocated by
/// <see cref="AllocateTunnelKey"/>). The key is sent to World in AS_ENTER_WORLD
/// at [80..83] and comes back in 0x13F7 headers at <see cref="TunnelKeyOffset"/>.
/// Each key has its own reorder buffer; packets are routed to the session that
/// owns that key, with a broadcast fallback for unknown keys.
/// </summary>
public sealed class WorldBridge
{
    public const int Port = 7802;
    public const ushort OpTunnelToClient = 0x13F7;
    public const ushort OpTunnelFromClient = 0x13F6;
    public const ushort OpPlayerEnter = 0x138E;
    public const ushort OpCharacterData = 0x2738;
    public const ushort OpLoadTopoFin = 0x138F;           // AS_LOAD_TOPO_FIN (from decompile)
    public const ushort OpForceEnterDungeonId = 0x1390;  // AS_FORCE_ENTER_DUNGEON_ID
    public const ushort OpUpdateVisitedSection = 0x1439; // AS_UPDATE_VISITED_SECTION_LIST
    public const ushort OpCancelSkillStrictly = 0x1460; // AS_CANCEL_SKILL_STRICTLY (sent before every leave)
    public const ushort OpLeaveWorld = 0x1392;          // AS_LEAVE_WORLD
    public const ushort OpUserRequestExit = 0x14FF;     // AS_USER_REQUEST_EXIT (sent at button press)
    public const ushort OpUserCancelRequestExit = 0x1500; // AS_USER_CANCEL_REQUEST_EXIT
    public const ushort OpSaLeaveWorld = 0x1393;        // SA_LEAVE_WORLD (World -> us, end of save)
    public const ushort OpArbiterUserDelete = 0x1433;   // AS_ARBITER_USER_DELETE (our reply to 0x1393)
    // Back-compat aliases (older names used elsewhere).
    public const ushort OpPlayerLeave1 = OpCancelSkillStrictly;
    public const ushort OpPlayerLeave2 = OpLeaveWorld;
    public const ushort OpHeartbeat14 = 0x13F2;
    public const ushort OpHeartbeat6 = 0x13E5;
    private const ushort OpHandshakeDone = 0x294F; // last request in the startup sequence

    /// <summary>
    /// Offset within the 0x13F7 payload to extract the per-session routing key.
    /// [20] = conn (connection handle), [24] = idx (slot index from BypassStart).
    /// The human's two-login capture will settle which field World actually uses
    /// to distinguish players. Until then, idx at [24] is the working assumption
    /// because it matches the tunnel slot we send in AS_ENTER_WORLD at [80..83].
    /// </summary>
    internal const int TunnelKeyOffset = 24;

    private readonly ILogger _log;
    private readonly WorldReplayTable _replay;
    private readonly List<WorldLink> _links = new();
    private readonly object _lock = new();
    private int _nextLinkId;

    private readonly object _reorderLock = new();
    private readonly Dictionary<uint, TunnelReorderBuffer> _tunnels = new();
    private const int StallMs = 150;

    /// <summary>
    /// Per-session Ticket allocator (T38 TicketAllocator: linear-probing cursor, a freed ticket is
    /// not reissued until the cursor wraps). Before this every session got 5 - the first time two
    /// clients logged in together World saw both as one ticket ("SpawnMe twice", both stuck at
    /// 100% loading, 2026-09-15 00:04).
    /// </summary>
    private readonly TicketAllocator _tickets = new();

    /// <summary>
    /// In-world players keyed by gameId, for control-message routing (e.g. SA_LEAVE_WORLD
    /// completion). Tunnel routing uses <see cref="_tunnels"/> keyed by tunnel key instead.
    /// </summary>
    private readonly Dictionary<ulong, GameSession> _players = new();
    private readonly object _playersLock = new();

    /// <summary>Allocate a unique tunnel Ticket for a new player session.</summary>
    internal uint AllocateTunnelKey() => _tickets.Allocate();

    public void RegisterPlayer(GameSession s)
    {
        lock (_playersLock) _players[s.GameId] = s;
        lock (_reorderLock)
            _tunnels[s.TunnelKey] = new TunnelReorderBuffer { Deliver = s.Send };
    }

    public void UnregisterPlayer(ulong gameId, uint tunnelKey)
    {
        lock (_playersLock) _players.Remove(gameId);
        lock (_reorderLock) _tunnels.Remove(tunnelKey);
        _tickets.Free(tunnelKey);
    }

    /// <summary>The session that owns a tunnel Ticket, or null (ActionDispatcher / party / chat).</summary>
    public GameSession? SessionForTicket(uint ticket)
    {
        lock (_playersLock)
            foreach (var s in _players.Values) if (s.TunnelKey == ticket) return s;
        return null;
    }

    /// <summary>The in-world session for a character db id, or null (guild/chat/party actions address by player id).</summary>
    public GameSession? SessionForPlayerId(int playerId)
    {
        lock (_playersLock)
            foreach (var s in _players.Values) if (s.PlayerId == playerId) return s;
        return null;
    }

    /// <summary>Snapshot of every in-world session (whisper/online lookups by name).</summary>
    public List<GameSession> InWorldSessions()
    {
        lock (_playersLock) return _players.Values.ToList();
    }

    /// <summary>Register a tunnel route by key with a custom callback (test-facing).</summary>
    internal void RegisterTunnelRoute(uint key, Action<byte[]> callback)
    {
        lock (_reorderLock)
            _tunnels[key] = new TunnelReorderBuffer { Deliver = callback };
    }

    /// <summary>Remove a tunnel route by key (test-facing).</summary>
    internal void UnregisterTunnelRoute(uint key)
    {
        lock (_reorderLock) _tunnels.Remove(key);
    }

    private GameSession? FindPlayer(ulong gameId)
    {
        lock (_playersLock) return _players.TryGetValue(gameId, out var s) ? s : null;
    }

    /// <summary>The session that owns a gameId, or null. Used by the 0x138D (SA_ENTER_WORLD_FAIL) handler.</summary>
    public GameSession? PlayerForGameId(ulong gameId) => FindPlayer(gameId);

    /// <summary>Real DB-proxy handlers; checked before the replay table.</summary>
    public DbProxyHandlers? DbProxy { get; set; }

    /// <summary>True once WorldServer has completed the startup handshake and can accept players.</summary>
    public bool IsReady { get; private set; }

    /// <summary>gameId low part is a per-login counter that restarts at 1 with each World process
    /// (cap_newchar.log: fresh World, playerId 2 -> 0x80000AF00001; lobby_tap.log: ...0001 then ...0002).
    /// It is NOT derived from playerId. Reset when the World handshake completes.</summary>
    private int _gameIdSeq;
    public ulong AllocateGameId() => 0x80000AF00000UL | (ulong)(uint)Interlocked.Increment(ref _gameIdSeq);
    public bool IsConnected { get { lock (_lock) return _links.Count > 0; } }

    public WorldBridge(WorldReplayTable replay, ILogger log)
    {
        _replay = replay;
        _log = log;
    }

    /// <summary>Reset every reorder buffer (World restarted).</summary>
    public void ResetTunnelSequence()
    {
        lock (_reorderLock)
        {
            foreach (var buf in _tunnels.Values)
            {
                buf.Pending.Clear();
                buf.NextSeq = 0;
                buf.LastDelivery = DateTime.UtcNow;
            }
        }
    }

    /// <summary>Reset ONE player's reorder buffer (their re-enter), leaving the others' queues alone.</summary>
    public void ResetTunnelSequence(uint tunnelKey)
    {
        lock (_reorderLock)
        {
            if (!_tunnels.TryGetValue(tunnelKey, out var buf)) return;
            buf.Pending.Clear();
            buf.NextSeq = 0;
            buf.LastDelivery = DateTime.UtcNow;
        }
    }

    /// <summary>
    /// Deliver a reordered client packet to the session that owns the given tunnel key.
    /// Falls back to broadcasting to all registered sessions when the key is unknown
    /// (e.g. World sends a packet before we've registered the player, or uses a key
    /// we don't recognise yet ÃƒÆ’Ã†â€™Ãƒâ€ Ã¢â‚¬â„¢ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã†â€™Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬Ãƒâ€¦Ã‚Â¡ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â¬ÃƒÆ’Ã†â€™Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã…Â¡Ãƒâ€šÃ‚Â¬ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â the two-login capture will clarify).
    /// </summary>
    internal void RouteToClient(uint key, byte[] packet)
    {
        // Unknown ticket: with exactly one session registered, deliver to it (a frame World
        // sends before RegisterPlayer ran - the single-player behaviour that always worked);
        // with several, DROP it - broadcasting one player's packets to everyone is how two
        // clients ended up with each other's spawn (MULTIPLAYER-DESIGN.md section 6).
        Action<byte[]>? deliver = null;
        int count;
        lock (_reorderLock)
        {
            count = _tunnels.Count;
            if (_tunnels.TryGetValue(key, out var buf)) deliver = buf.Deliver;
            else if (count == 1) foreach (var b in _tunnels.Values) { deliver = b.Deliver; break; }
        }
        if (deliver != null) { deliver(packet); return; }
        _log.LogDebug("Tunnel ticket {Key} unknown with {N} session(s) - dropped", key, count);
    }

    private void RouteToClientLegacyBroadcast(uint key, byte[] packet)
    {
        Action<byte[]>? deliver = null;
        lock (_reorderLock)
        {
            if (_tunnels.TryGetValue(key, out var buf))
                deliver = buf.Deliver;
        }
        if (deliver != null)
        {
            deliver(packet);
            return;
        }
        // Broadcast fallback: unknown key ÃƒÆ’Ã†â€™Ãƒâ€ Ã¢â‚¬â„¢ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã†â€™Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã…Â¡Ãƒâ€šÃ‚Â¬ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â ÃƒÆ’Ã†â€™Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã…Â¡Ãƒâ€šÃ‚Â¬ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã…Â¾Ãƒâ€šÃ‚Â¢ send to all registered sessions.
        List<Action<byte[]>> all;
        lock (_reorderLock)
            all = _tunnels.Values
                .Where(t => t.Deliver != null)
                .Select(t => t.Deliver!)
                .ToList();
        if (all.Count > 0)
            _log.LogDebug("Tunnel key {Key} unknown ÃƒÆ’Ã†â€™Ãƒâ€ Ã¢â‚¬â„¢ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã†â€™Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬Ãƒâ€¦Ã‚Â¡ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â¬ÃƒÆ’Ã†â€™Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã…Â¡Ãƒâ€šÃ‚Â¬ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â broadcasting to {N} session(s)", key, all.Count);
        foreach (var d in all) d(packet);
    }

    public async Task RunAsync(CancellationToken ct)
    {
        var listener = new TcpListener(IPAddress.Loopback, Port);
        listener.Start(64);
        _log.LogInformation("WorldBridge listening on 127.0.0.1:{Port} for WorldServer", Port);

        _ = StallWatchdog(ct);

        while (!ct.IsCancellationRequested)
        {
            Socket sock;
            try { sock = await listener.AcceptSocketAsync(ct); }
            catch (OperationCanceledException) { break; }

            var link = new WorldLink(Interlocked.Increment(ref _nextLinkId), sock, this, _log);
            lock (_lock) _links.Add(link);
            _log.LogInformation("WorldServer link #{Id} connected ({N} active)", link.Id, _links.Count);
            _ = link.RunAsync(ct).ContinueWith(_ =>
            {
                int remaining;
                lock (_lock) { _links.Remove(link); remaining = _links.Count; }
                if (remaining == 0)
                {
                    IsReady = false;
                    _log.LogWarning("WorldServer fully disconnected - not ready");
                }
                _log.LogInformation("WorldServer link #{Id} closed ({N} active)", link.Id, remaining);
            }, ct);
        }
        listener.Stop();
    }

    private async Task StallWatchdog(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(50, ct);
            List<(uint key, byte[] pkt)>? flush = null;
            lock (_reorderLock)
            {
                foreach (var (key, buf) in _tunnels)
                {
                    if (buf.Pending.Count > 0 &&
                        (DateTime.UtcNow - buf.LastDelivery).TotalMilliseconds > StallMs)
                    {
                        var first = buf.Pending.Keys.First();
                        _log.LogWarning("Tunnel key={Key} seq stall: expected {Exp}, have {Have} - skipping",
                            key, buf.NextSeq, first);
                        buf.NextSeq = first;
                        flush ??= new();
                        flush.AddRange(DrainInOrder(key, buf));
                    }
                }
            }
            if (flush != null)
                foreach (var (k, p) in flush) RouteToClient(k, p);
        }
    }

    private static List<(uint key, byte[] pkt)> DrainInOrder(uint key, TunnelReorderBuffer buf)
    {
        var outp = new List<(uint, byte[])>();
        while (buf.Pending.TryGetValue(buf.NextSeq, out var pkt))
        {
            buf.Pending.Remove(buf.NextSeq);
            outp.Add((key, pkt));
            buf.NextSeq++;
            buf.LastDelivery = DateTime.UtcNow;
        }
        return outp;
    }

    public void HandleFrame(WorldLink link, ushort op, byte[] payload)
    {
        switch (op)
        {
            case OpHeartbeat6:
                return;

            case OpHeartbeat14:
                // DSA_DUNGEON_TIMELINE_OPEN_INFO. 909 of the 911 frames across the four captures
                // are the empty 14-byte form - a real heartbeat - but the first one after World
                // registers its dungeons carries the open-state list the real Arbiter echoes back
                // as 0x1581, one frame per record (status/HANDSHAKE-DATA.md section 0, T33).
                if (payload.Length > 8) DbProxy?.TryHandle(this, link, op, payload);
                return;

            case OpTunnelToClient:
            {
                // SA_BYPASS_TO_CLIENT addresses N recipients (16 B each) and the client packet
                // follows the list - TunnelFrames owns the layout (T38 / MULTIPLAYER-DESIGN.md
                // section 6). Each recipient has its own Ticket and sequence, so each gets the
                // packet through its own reorder buffer; a second recipient gets a clone because
                // GameSession.Send encrypts in place.
                var frame = TunnelFrames.ParseBypassToClient(payload);
                if (frame == null)
                {
                    _log.LogWarning("Malformed 13F7 on link #{Id} ({Len} B)", link.Id, payload.Length);
                    return;
                }
                bool solo = frame.Recipients.Count == 1;
                foreach (var r in frame.Recipients)
                    DeliverTunnelPacket(r.Ticket, r.Sequence,
                        solo ? frame.ClientPacket : (byte[])frame.ClientPacket.Clone());
                return;
            }

            case OpSaLeaveWorld:
                HandleSaLeaveWorld(link, payload);
                return;

            default:
                if (PartyWiring.TryHandleWorldFrame(op, payload)) return;   // T49: the twelve party SA_ opcodes incl. SA_BYPASS_TO_GROUP
                if (GuildWiring.TryHandleWorldFrame(op, payload)) return;   // T52: guild SA_ opcodes (membership test, not a length gate)
                if (ContractBroker.TryHandleWorldFrame(op, payload)) return;   // T60: 0x2809/0x280C/0x280D/0x280E - party invites travel as contracts
                if (op is not (0x138A or 0x15A8 or 0x1436 or 0x164D))
                    _log.LogInformation("W->A #{Id} 0x{Op:X4} len={Len}", link.Id, op, payload.Length + 6);
                if (DbProxy != null && DbProxy.TryHandle(this, link, op, payload)) return;
                var responses = _replay.GetResponses(op, payload);
                if (responses.Count == 0) { _log.LogDebug("  no replay for 0x{Op:X4}", op); }
                foreach (var (rop, rbody) in responses)
                {
                    // DBS_UPDATE_USER_DATA (0x27CC) must never come from replay ÃƒÆ’Ã†â€™Ãƒâ€ Ã¢â‚¬â„¢ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã†â€™Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬Ãƒâ€¦Ã‚Â¡ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â¬ÃƒÆ’Ã†â€™Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã…Â¡Ãƒâ€šÃ‚Â¬ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â it carries
                    // a reqId that only makes sense for the live request. DbProxy handles it.
                    if (rop == DbProxyHandlers.DBS_UPDATE_USER_DATA)
                    {
                        _log.LogDebug("  skipping replayed 0x{Op:X4} (handled by DbProxy)", rop);
                        continue;
                    }
                    if (rop is not (0x147E or 0x138B or 0x2801))
                        _log.LogInformation("A->W #{Id} 0x{Op:X4} len={Len}", link.Id, rop, rbody.Length + 6);
                    link.SendFrame(rop, rbody);
                }
                if (op == OpHandshakeDone && !IsReady)
                {
                    IsReady = true;
                    Interlocked.Exchange(ref _gameIdSeq, 0);
                    DbProxy?.OnWorldReady(link);   // real Arbiter: ~100 x 0x1581 dungeon-open pushes, 1 s after READY
                    _log.LogInformation("WorldServer handshake complete - READY for players");
                }
                return;
        }
    }

    public void SendFrame(ushort op, byte[] payload)
    {
        WorldLink? primary;
        lock (_lock) primary = _links.Count > 0 ? _links[0] : null;
        primary?.SendFrame(op, payload);
    }

    /// <summary>
    /// One recipient's share of an SA_BYPASS_TO_CLIENT: queue it in that Ticket's reorder buffer
    /// and drain in sequence order. An unknown Ticket goes through <see cref="RouteToClient"/>'s
    /// single-session fallback (or is dropped).
    /// </summary>
    private void DeliverTunnelPacket(uint ticket, uint seq, byte[] clientPkt)
    {
        List<(uint k, byte[] p)>? deliver = null;
        lock (_reorderLock)
        {
            TunnelReorderBuffer? buf = null;
            if (!_tunnels.TryGetValue(ticket, out buf) && _tunnels.Count == 1)
                foreach (var b in _tunnels.Values) { buf = b; break; }
            if (buf != null)
            {
                buf.Pending[seq] = clientPkt;
                deliver = DrainInOrder(ticket, buf);
            }
        }
        if (deliver != null) { foreach (var (k, p) in deliver) RouteToClient(k, p); return; }
        RouteToClient(ticket, clientPkt);   // logs + drops with 2+ sessions
    }

    public void TunnelFromClient(ulong gameId, byte[] clientPacket)
    {
        // The real Arbiter refuses to tunnel a packet of 0x1F41+ bytes and kicks instead.
        if (!TunnelFrames.IsTunnellable(clientPacket)) return;
        ushort cop = clientPacket.Length >= 4 ? (ushort)(clientPacket[2] | (clientPacket[3] << 8)) : (ushort)0;
        _log.LogTrace("TUNNEL C->W client-op={Cop} len={Len}", cop, clientPacket.Length);
        SendFrame(OpTunnelFromClient,
            TunnelFrames.BuildBypassToWorld(gameId, clientPacket, (ulong)Environment.TickCount64));
    }

    /// <summary>
    /// Client finished loading the zone. Per Handler_C_LOAD_TOPO_FIN in the decompiled
    /// Arbiter: send AS_LOAD_TOPO_FIN (0x138F) with the player id. The real Arbiter sends
    /// AS_FORCE_ENTER_DUNGEON_ID (0x1390) [1][0] immediately before it (capture [331]).
    /// World replies by spawning the player (S_SPAWN_ME via tunnel).
    /// </summary>
    public void NotifyTopoLoaded(uint playerId)
    {
        SendFrame(OpForceEnterDungeonId, new byte[] { 1,0,0,0, 0,0,0,0 });
        SendFrame(OpLoadTopoFin, BitConverter.GetBytes(playerId));
        _log.LogInformation("Sent AS_FORCE_ENTER_DUNGEON_ID + AS_LOAD_TOPO_FIN (0x138F) for player {Id}", playerId);
    }

    /// <summary>
    /// Button press: tell World the user asked to leave. The real Arbiter sends this from
    /// User::OnRequestReturnToLobby / OnRequestExit (Arb_part_029.c) the instant the request
    /// arrives, before the countdown. Writer FUN_140353630: AS_USER_REQUEST_EXIT [u32 playerId].
    /// </summary>
    public void SendUserRequestExit(uint playerId)
    {
        SendFrame(OpUserRequestExit, BitConverter.GetBytes(playerId));
        _log.LogInformation("Sent AS_USER_REQUEST_EXIT (0x14FF) for player {Id}", playerId);
    }

    /// <summary>Countdown cancelled. Writer FUN_140353530: AS_USER_CANCEL_REQUEST_EXIT [u32 playerId].</summary>
    public void SendUserCancelRequestExit(uint playerId)
    {
        SendFrame(OpUserCancelRequestExit, BitConverter.GetBytes(playerId));
        _log.LogInformation("Sent AS_USER_CANCEL_REQUEST_EXIT (0x1500) for player {Id}", playerId);
    }

    /// <summary>LeaveWorldType/LogoutReason pair the real Arbiter sends for each leave path.</summary>
    public static (uint type, uint reason) LeaveValues(LeaveMode mode) => mode switch
    {
        // From User::OnLeaveWorldTick (Arb_part_029.c): lobby branch sets (type=3, reason=0),
        // exit/disconnect branch sets (type=1, reason=8). Confirmed by the WorldServer crash
        // "Critical Error LeaveWorld type[1] reason[8]" when (1,8) was wrongly used for a lobby
        // return. Do NOT change these without a matching WorldServer capture.
        // Capture's working AS_LEAVE_WORLD used (1,8) for the button leave; the earlier crash
        // was a duplicate leave (disconnect path re-sending after the button leave), now guarded
        // in GameSession.LeaveWorld. Keep (1,8) to match the capture World accepts.
        LeaveMode.Lobby => (3u, 0u),   // lobby_tap.log 02:51:52: 0x1392 type=3 reason=0
        _ => (1u, 8u),
    };

    /// <summary>
    /// AS_LEAVE_WORLD (0x1392) payload, byte-exact to User::SendASLeaveWorld (FUN_1403a4820):
    /// [u64 gameId (low 63 bits)][u32 leaveWorldType][u32 logoutReason][u32 playerId].
    /// </summary>
    public static byte[] BuildLeaveWorldPayload(ulong gameId, uint playerId, LeaveMode mode)
    {
        var (type, reason) = LeaveValues(mode);
        var p = new byte[20];
        BitConverter.GetBytes(gameId & 0x7FFFFFFFFFFFFFFFUL).CopyTo(p, 0);
        BitConverter.GetBytes(type).CopyTo(p, 8);
        BitConverter.GetBytes(reason).CopyTo(p, 12);
        BitConverter.GetBytes(playerId).CopyTo(p, 16);
        return p;
    }

    /// <summary>
    /// Tell World the player is leaving. Sends AS_CANCEL_SKILL_STRICTLY (0x1460) then
    /// AS_LEAVE_WORLD (0x1392), exactly as User::OnLeaveWorldTick does for both lobby and
    /// exit. (The earlier "remove 0x1460" lead was wrong: the captured real server and the
    /// decompile both send it on every logout.) World answers with its save sequence and
    /// finally SA_LEAVE_WORLD (0x1393).
    /// </summary>
    public void NotifyPlayerLeave(ulong gameId, uint playerId, LeaveMode mode)
    {
        SendFrame(OpCancelSkillStrictly, BitConverter.GetBytes(playerId));
        SendFrame(OpLeaveWorld, BuildLeaveWorldPayload(gameId, playerId, mode));
        var (type, reason) = LeaveValues(mode);
        _log.LogInformation("Sent AS_CANCEL_SKILL_STRICTLY (0x1460) + AS_LEAVE_WORLD (0x1392) gameId={G:X} type={T} reason={R}",
            gameId, type, reason);
    }

    /// <summary>AS_ARBITER_USER_DELETE (0x1433) payload: [u64 gameId][u64 gameId] (FUN_140832f20).</summary>
    public static byte[] BuildArbiterUserDeletePayload(ulong gameId)
    {
        // World looks up the user by this gameId (Handler_AS_ARBITER_USER_DELETE reads it at
        // frame+6). It must match the gameId form World stored at enter (AS_ENTER_WORLD [24],
        // which we send UNMASKED). Masking the high bit here made the lookup miss -> no delete
        // -> character stuck -> relog hangs. Send it unmasked to match enter.
        var p = new byte[16];
        BitConverter.GetBytes(gameId).CopyTo(p, 0);
        BitConverter.GetBytes(gameId).CopyTo(p, 8);
        return p;
    }

    /// <summary>
    /// World finished saving and is releasing the user (SA_LEAVE_WORLD, 0x1393). Reply with
    /// AS_ARBITER_USER_DELETE carrying the LIVE gameId (the replay table can't patch it: a
    /// gameId &gt; 10M is filtered out of id-echo detection, so a replayed 0x1433 would delete
    /// the captured player, not this one, and World would keep ours in-world -> next
    /// C_SELECT_USER stalls). Then let the owning session return the client to lobby / exit.
    ///
    /// 0x1393 payload (frame-relative, Handler_SA_LEAVE_WORLD Arb_part_062.c):
    ///   [0] u64 worldSessionHandle  [8] u64 gameId  [16] u32 type  [20] u32 reason ...
    /// </summary>
    private void HandleSaLeaveWorld(WorldLink link, byte[] payload)
    {
        if (payload.Length < 16)
        {
            _log.LogWarning("SA_LEAVE_WORLD too short ({Len})", payload.Length);
            return;
        }
        ulong gameId = BitConverter.ToUInt64(payload, 8);
        link.SendFrame(OpArbiterUserDelete, BuildArbiterUserDeletePayload(gameId));
        _log.LogInformation("SA_LEAVE_WORLD (0x1393) -> AS_ARBITER_USER_DELETE (0x1433) for gameId {G:X}", gameId);

        var session = FindPlayer(gameId) ?? FindPlayer(gameId | 0x80000AF00000UL);
        if (session != null) session.OnWorldLeaveConfirmed();
        else _log.LogWarning("SA_LEAVE_WORLD: no session for gameId {G:X}", gameId);
    }
}

public sealed class WorldLink
{
    public int Id { get; }
    private readonly Socket _sock;
    private readonly WorldBridge _bridge;
    private readonly ILogger _log;
    private readonly byte[] _rx = new byte[1 << 20];
    private int _rxLen;

    public WorldLink(int id, Socket sock, WorldBridge bridge, ILogger log)
    {
        Id = id; _sock = sock; _bridge = bridge; _log = log;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        var buf = new byte[65536];
        try
        {
            while (!ct.IsCancellationRequested)
            {
                int n = await _sock.ReceiveAsync(buf, SocketFlags.None, ct);
                if (n <= 0) return;

                if (_rxLen + n > _rx.Length) { _log.LogError("Link #{Id} rx overflow", Id); _rxLen = 0; continue; }
                Array.Copy(buf, 0, _rx, _rxLen, n);
                _rxLen += n;

                int pos = 0;
                while (_rxLen - pos >= 6)
                {
                    int len = BitConverter.ToInt32(_rx, pos);
                    if (len < 6 || len > _rx.Length) { _log.LogError("Link #{Id} bad frame len {Len}", Id, len); _rxLen = 0; break; }
                    if (_rxLen - pos < len) break;

                    ushort op = BitConverter.ToUInt16(_rx, pos + 4);
                    var payload = new byte[len - 6];
                    Array.Copy(_rx, pos + 6, payload, 0, payload.Length);
                    // T48: one malformed frame must not unwind the receive loop - the finally below
                    // closes the socket, and with 25 links that is every player disconnected.
                    try { _bridge.HandleFrame(this, op, payload); }
                    catch (Exception ex) { _log.LogError(ex, "Link #{Id}: handler for 0x{Op:X4} ({Len} B) threw - frame dropped", Id, op, payload.Length); }
                    pos += len;
                }
                if (pos > 0) { Array.Copy(_rx, pos, _rx, 0, _rxLen - pos); _rxLen -= pos; }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _log.LogWarning("Link #{Id} error: {Msg}", Id, ex.Message); }
        finally { try { _sock.Close(); } catch { } }
    }

    public void SendFrame(ushort op, byte[] payload)
    {
        if (!_sock.Connected) return;
        int total = 6 + payload.Length;
        var frame = new byte[total];
        BitConverter.GetBytes(total).CopyTo(frame, 0);
        BitConverter.GetBytes(op).CopyTo(frame, 4);
        payload.CopyTo(frame, 6);
        try { _sock.Send(frame); }
        catch (Exception ex) { _log.LogWarning("Link #{Id} send failed: {Msg}", Id, ex.Message); }
    }
}
