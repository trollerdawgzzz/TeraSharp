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

    /// <summary>T108: IsReady + the game-id counter, one per World (MULTIWORLD-DESIGN.md s4.3).</summary>
    private readonly PerWorld<WorldRuntime> _worlds = new(() => new WorldRuntime());

    private readonly object _reorderLock = new();
    /// <summary>
    /// T111: keyed by (World, Ticket), not Ticket alone. A Ticket indexes ONE World's bypass
    /// slots, so two Worlds hand out the same numbers and a flat map would deliver world 13's
    /// packet to a world 0 player (MULTIWORLD-DESIGN.md section 4 item 5).
    /// </summary>
    private readonly Dictionary<(int World, uint Ticket), TunnelReorderBuffer> _tunnels = new();
    private const int StallMs = 150;

    /// <summary>
    /// Per-session Ticket allocator (T38 TicketAllocator: linear-probing cursor, a freed ticket is
    /// not reissued until the cursor wraps). Before this every session got 5 - the first time two
    /// clients logged in together World saw both as one ticket ("SpawnMe twice", both stuck at
    /// 100% loading, 2026-09-15 00:04).
    /// </summary>
    /// <summary>T103: one allocator per World - a Ticket indexes THAT World's bypass slots.</summary>
    private readonly PerWorld<TicketAllocator> _tickets = new(() => new TicketAllocator());

    /// <summary>
    /// In-world players keyed by gameId, for control-message routing (e.g. SA_LEAVE_WORLD
    /// completion). Tunnel routing uses <see cref="_tunnels"/> keyed by tunnel key instead.
    /// </summary>
    private readonly Dictionary<ulong, GameSession> _players = new();
    private readonly object _playersLock = new();

    /// <summary>Allocate a unique tunnel Ticket for a new player session.</summary>
    internal uint AllocateTunnelKey() => AllocateTunnelKey(WorldRegistration.DefaultWorldId);

    /// <summary>The same, in one World's ticket space.</summary>
    internal uint AllocateTunnelKey(int worldId) => _tickets.For(worldId).Allocate();

    public void RegisterPlayer(GameSession s)
    {
        lock (_playersLock) _players[s.GameId] = s;
        lock (_reorderLock)
            _tunnels[(s.CurrentWorldId, s.TunnelKey)] = new TunnelReorderBuffer { Deliver = p => Handlers.ArbiterClientHandlers.DeliverTunnelled(s, p) };   // T121: inject S_ADMIN_GM_SKILL before the tunnelled S_LOAD_TOPO
    }

    public void UnregisterPlayer(ulong gameId, uint tunnelKey)
        => UnregisterPlayer(gameId, WorldRegistration.DefaultWorldId, tunnelKey);

    /// <summary>T111: the Ticket is freed in its own World's space and unkeyed with it.</summary>
    public void UnregisterPlayer(ulong gameId, int worldId, uint tunnelKey)
    {
        lock (_playersLock) _players.Remove(gameId);
        lock (_reorderLock) _tunnels.Remove((worldId, tunnelKey));
        _tickets.For(worldId).Free(tunnelKey);
    }

    /// <summary>The session that owns a tunnel Ticket, or null (ActionDispatcher / party / chat).</summary>
    public GameSession? SessionForTicket(uint ticket)
    {
        lock (_playersLock)
            foreach (var s in _players.Values) if (s.TunnelKey == ticket) return s;
        return null;
    }

    /// <summary>Registered World links (T106 status tab).</summary>
    public int LinkCount { get { lock (_lock) return _links.Count; } }

    /// <summary>T103: every link of one World, in connect order.</summary>
    public List<WorldLink> LinksOf(int worldId)
    {
        lock (_lock) return _links.Where(l => l.WorldId == worldId).ToList();
    }

    /// <summary>T111: whether a World has any link at all - the guard on every routed send.</summary>
    public bool HasLinks(int worldId)
    {
        lock (_lock) return _links.Any(l => l.WorldId == worldId);
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
        => RegisterTunnelRoute(WorldRegistration.DefaultWorldId, key, callback);

    /// <summary>The same, in one World's ticket space.</summary>
    internal void RegisterTunnelRoute(int worldId, uint key, Action<byte[]> callback)
    {
        lock (_reorderLock)
            _tunnels[(worldId, key)] = new TunnelReorderBuffer { Deliver = callback };
    }

    /// <summary>Remove a tunnel route by key (test-facing).</summary>
    internal void UnregisterTunnelRoute(uint key)
        => UnregisterTunnelRoute(WorldRegistration.DefaultWorldId, key);

    /// <summary>The same, in one World's ticket space.</summary>
    internal void UnregisterTunnelRoute(int worldId, uint key)
    {
        lock (_reorderLock) _tunnels.Remove((worldId, key));
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
    public bool IsReady => IsReadyFor(WorldRegistration.DefaultWorldId);

    /// <summary>T108: the same, per World.</summary>
    public bool IsReadyFor(int worldId) => _worlds.For(worldId).IsReady;

    /// <summary>gameId low part is a per-login counter that restarts at 1 with each World process
    /// (cap_newchar.log: fresh World, playerId 2 -> 0x80000AF00001; lobby_tap.log: ...0001 then ...0002).
    /// It is NOT derived from playerId. Reset when the World handshake completes.</summary>
    public ulong AllocateGameId() => AllocateGameId(WorldRegistration.DefaultWorldId);

    /// <summary>T108: each World process restarts its own counter, so each gets its own.</summary>
    public ulong AllocateGameId(int worldId) => _worlds.For(worldId).AllocateGameId();
    public bool IsConnected { get { lock (_lock) return _links.Count > 0; } }

    public WorldBridge(WorldReplayTable replay, ILogger log)
    {
        _replay = replay;
        _log = log;
        // T108: let the Cowork-owned routing in World/WorldInstances.cs see the per-World link
        // sets. Until these are set every link reads as world 0, no World is live, and nothing
        // can be routed - which is the single-World tree exactly as it was.
        WorldRouting.WorldIdOfLink = l => l.WorldId;
        WorldRouting.SendToWorld = (w, op, p) => SendFrame(w, op, p);
        WorldRouting.HasLinks = HasLinks;
        // T111: ServerConfig.xml's WorldServerList is the whole allocator - one continent, one
        // World (MULTIWORLD-DESIGN.md section 7.1). A missing file seeds nothing.
        WorldServerList.SeedDefault(DungeonRouting.Channels, log);
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
        => ResetTunnelSequence(WorldRegistration.DefaultWorldId, tunnelKey);

    /// <summary>The same, in one World's ticket space.</summary>
    public void ResetTunnelSequence(int worldId, uint tunnelKey)
    {
        lock (_reorderLock)
        {
            if (!_tunnels.TryGetValue((worldId, tunnelKey), out var buf)) return;
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
        => RouteToClient(WorldRegistration.DefaultWorldId, key, packet);

    /// <summary>The same, in one World's ticket space (T111).</summary>
    internal void RouteToClient(int worldId, uint key, byte[] packet)
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
            if (_tunnels.TryGetValue((worldId, key), out var buf)) deliver = buf.Deliver;
            else if (count == 1) foreach (var b in _tunnels.Values) { deliver = b.Deliver; break; }
        }
        if (deliver != null) { deliver(packet); return; }
        _log.LogDebug("Tunnel ticket {Key} on world {W} unknown with {N} session(s) - dropped",
            key, worldId, count);
    }

    private void RouteToClientLegacyBroadcast(int worldId, uint key, byte[] packet)
    {
        Action<byte[]>? deliver = null;
        lock (_reorderLock)
        {
            if (_tunnels.TryGetValue((worldId, key), out var buf))
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
                int remaining, mine;
                lock (_lock)
                {
                    _links.Remove(link);
                    remaining = _links.Count;
                    mine = _links.Count(l => l.WorldId == link.WorldId);
                }
                if (mine == 0)
                {
                    // T108: only THIS World stops taking players, and its instances go with it.
                    _worlds.For(link.WorldId).MarkDisconnected();
                    DungeonRouting.Channels.ForgetWorld(link.WorldId);
                    DungeonRouting.Transfers.ForgetWorld(link.WorldId);
                    _log.LogWarning("World {W} fully disconnected - not ready", link.WorldId);
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
            List<((int World, uint Ticket) key, byte[] pkt)>? flush = null;
            lock (_reorderLock)
            {
                foreach (var (key, buf) in _tunnels)
                {
                    if (buf.Pending.Count > 0 &&
                        (DateTime.UtcNow - buf.LastDelivery).TotalMilliseconds > StallMs)
                    {
                        var first = buf.Pending.Keys.First();
                        _log.LogWarning("Tunnel world={W} key={Key} seq stall: expected {Exp}, "
                            + "have {Have} - skipping", key.World, key.Ticket, buf.NextSeq, first);
                        buf.NextSeq = first;
                        flush ??= new();
                        flush.AddRange(DrainInOrder(key, buf));
                    }
                }
            }
            if (flush != null)
                foreach (var (k, p) in flush) RouteToClient(k.World, k.Ticket, p);
        }
    }

    private static List<((int World, uint Ticket) key, byte[] pkt)> DrainInOrder(
        (int World, uint Ticket) key, TunnelReorderBuffer buf)
    {
        var outp = new List<((int, uint), byte[])>();
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
            case WorldRegistration.SA_REGISTER:
            {
                // T103. Until now this fell through to the replay table, so every link on every
                // World was handed world 0's CAPTURED answer - captured WorldId and BypassIndex
                // included, which tells world 13 it is world 0.
                var info = WorldRegistration.Parse(payload);
                if (info == null)
                {
                    _log.LogWarning("SA_REGISTER on link #{Id}: {Len} B - PDL version mismatch",
                        link.Id, payload.Length);
                    return;
                }
                link.WorldId = info.Value.WorldId;
                link.PlanetId = info.Value.PlanetId;
                link.BypassIndex = info.Value.BypassIndex;
                link.Registered = true;
                link.SendFrame(WorldRegistration.AS_REGISTER, WorldRegistration.Reply(info.Value));
                _log.LogInformation("Link #{Id} is world {W} bypass {B} of {N} (result {R})",
                    link.Id, info.Value.WorldId, info.Value.BypassIndex,
                    info.Value.TotalBypassCount, WorldRegistration.ResultFor(info.Value));
                return;
            }

            case OpHeartbeat6:
                return;

            // ---- T138b: the cross-World hand-off (status/MULTIWORLD-DESIGN.md T137c) ----
            case 0x164D:   // SA_WORLD_SERVER_STATUS: this link's continent roster -> the continent table
            {
                int n = WorldContinentList.Apply(DungeonRouting.Channels, payload, _log);
                _log.LogInformation("Link #{Id} world {W}: {N} continent(s) rostered", link.Id, link.WorldId, n);
                return;   // the real Arbiter only relays this to MatchServer; nothing to answer
            }
            case ContinentHandoff.SA_REQUEST_ENTER_CONTINENT:   // 0x13BE: main World asks
            {
                int? continent = ContinentHandoff.ContinentOf(payload);
                var reply = ContinentHandoff.EnterReply(payload);
                if (continent == null || reply == null)
                {
                    _log.LogWarning("0x13BE on link #{Id}: {Len} B - too short", link.Id, payload.Length);
                    return;
                }
                int owner = DungeonRouting.Channels.WorldForContinent(continent.Value) ?? link.WorldId;
                if (owner != link.WorldId && LinksOf(owner).Count == 0) owner = link.WorldId;   // owner not linked: keep it on the asker
                _log.LogInformation("0x13BE continent {C} from world {From} -> 0x13BF to world {To}", continent, link.WorldId, owner);
                if (owner == link.WorldId) link.SendFrame(ContinentHandoff.AS_ENTER_CONTINENT, reply);
                else SendFrame(owner, ContinentHandoff.AS_ENTER_CONTINENT, reply);
                return;
            }
            case ContinentHandoff.SA_CONTINENT_READY:   // 0x13C0: owning World is ready -> 0x13C1 back to the main World
            {
                var reply = ContinentHandoff.ReadyReply(payload);
                if (reply == null) return;
                var mains = LinksOf(0);
                if (link.WorldId != 0 && mains.Count > 0) SendFrame(0, ContinentHandoff.AS_CONTINENT_READY, reply);
                else link.SendFrame(ContinentHandoff.AS_CONTINENT_READY, reply);
                return;
            }
            case 0x13C5:   // SA_ADD_DUNGEON_CHANNEL: the owner registers the instance channel
            {
                var ch = DungeonRouting.Channels.Add(link.WorldId, payload);
                if (ch != null) _log.LogInformation("Link #{Id} world {W}: dungeon channel added {Ch}", link.Id, link.WorldId, ch);
                return;
            }
            case 0x13C6:   // SA_REMOVE_DUNGEON_CHANNEL
                DungeonRouting.Channels.Remove(link.WorldId, payload);
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
                    DeliverTunnelPacket(link.WorldId, r.Ticket, r.Sequence,
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
                if (WorldReplayTable.LogsAtDebug(op))
                    _log.LogDebug("W->A #{Id} 0x{Op:X4} len={Len}", link.Id, op, payload.Length + 6);   // T106: quiet set
                else if (op is not (0x138A or 0x15A8 or 0x1436 or 0x164D))
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
                // T108: MarkReady is the old `&& !IsReady` guard, per World - so OnWorldReady
                // still fires once per World and not once per link, and world 13's handshake no
                // longer zeroes world 0's game-id counter under live players.
                if (op == OpHandshakeDone && _worlds.For(link.WorldId).MarkReady())
                {
                    DbProxy?.OnWorldReady(link);   // real Arbiter: ~100 x 0x1581 dungeon-open pushes, 1 s after READY
                    _log.LogInformation("World {W} handshake complete - READY for players", link.WorldId);
                }
                return;
        }
    }

    public void SendFrame(ushort op, byte[] payload)
        => SendFrame(WorldRegistration.DefaultWorldId, op, payload);

    /// <summary>
    /// T108. There is no worldId field on the wire - the destination World is chosen by which
    /// socket the Arbiter writes to (MULTIWORLD-DESIGN.md section 2). With one World this picks
    /// the same link the old `_links[0]` primary did.
    /// </summary>
    public void SendFrame(int worldId, ushort op, byte[] payload)
    {
        WorldLink? primary;
        lock (_lock) primary = _links.FirstOrDefault(l => l.WorldId == worldId);
        if (primary == null)
        {
            _log.LogWarning("0x{Op:X4}: world {W} has no links - frame dropped", op, worldId);
            return;
        }
        primary.SendFrame(op, payload);
    }

    /// <summary>
    /// One recipient's share of an SA_BYPASS_TO_CLIENT: queue it in that Ticket's reorder buffer
    /// and drain in sequence order. An unknown Ticket goes through <see cref="RouteToClient"/>'s
    /// single-session fallback (or is dropped).
    /// </summary>
    private void DeliverTunnelPacket(int worldId, uint ticket, uint seq, byte[] clientPkt)
    {
        List<((int World, uint Ticket) k, byte[] p)>? deliver = null;
        lock (_reorderLock)
        {
            TunnelReorderBuffer? buf = null;
            if (!_tunnels.TryGetValue((worldId, ticket), out buf) && _tunnels.Count == 1)
                foreach (var b in _tunnels.Values) { buf = b; break; }
            if (buf != null)
            {
                buf.Pending[seq] = clientPkt;
                deliver = DrainInOrder((worldId, ticket), buf);
            }
        }
        if (deliver != null) { foreach (var (k, p) in deliver) RouteToClient(k.World, k.Ticket, p); return; }
        RouteToClient(worldId, ticket, clientPkt);   // logs + drops with 2+ sessions
    }

    public void TunnelFromClient(ulong gameId, byte[] clientPacket)
        => TunnelFromClient(WorldRegistration.DefaultWorldId, gameId, clientPacket);

    /// <summary>The same, to the World the session is in (T111).</summary>
    public void TunnelFromClient(int worldId, ulong gameId, byte[] clientPacket)
    {
        // The real Arbiter refuses to tunnel a packet of 0x1F41+ bytes and kicks instead.
        if (!TunnelFrames.IsTunnellable(clientPacket)) return;
        ushort cop = clientPacket.Length >= 4 ? (ushort)(clientPacket[2] | (clientPacket[3] << 8)) : (ushort)0;
        _log.LogTrace("TUNNEL C->W client-op={Cop} len={Len}", cop, clientPacket.Length);
        SendFrame(worldId, OpTunnelFromClient,
            TunnelFrames.BuildBypassToWorld(gameId, clientPacket, (ulong)Environment.TickCount64));
    }

    /// <summary>
    /// Client finished loading the zone. Per Handler_C_LOAD_TOPO_FIN in the decompiled
    /// Arbiter: send AS_LOAD_TOPO_FIN (0x138F) with the player id. The real Arbiter sends
    /// AS_FORCE_ENTER_DUNGEON_ID (0x1390) [1][0] immediately before it (capture [331]).
    /// World replies by spawning the player (S_SPAWN_ME via tunnel).
    /// </summary>
    public void NotifyTopoLoaded(uint playerId)
        => NotifyTopoLoaded(WorldRegistration.DefaultWorldId, playerId);

    /// <summary>T112: to the World the player is in, not whichever one connected first.</summary>
    public void NotifyTopoLoaded(int worldId, uint playerId)
    {
        SendFrame(worldId, OpForceEnterDungeonId, new byte[] { 1,0,0,0, 0,0,0,0 });
        SendFrame(worldId, OpLoadTopoFin, BitConverter.GetBytes(playerId));
        _log.LogInformation("Sent AS_FORCE_ENTER_DUNGEON_ID + AS_LOAD_TOPO_FIN (0x138F) "
            + "for player {Id} on world {W}", playerId, worldId);
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
        => NotifyPlayerLeave(WorldRegistration.DefaultWorldId, gameId, playerId, mode);

    /// <summary>
    /// T112: to the World the player is in. Both frames address the user by id inside THAT
    /// World - AS_LEAVE_WORLD on the wrong World is the "Critical Error LeaveWorld" crash the
    /// LeaveValues comment above is about, with the user simply not there.
    /// </summary>
    public void NotifyPlayerLeave(int worldId, ulong gameId, uint playerId, LeaveMode mode)
    {
        SendFrame(worldId, OpCancelSkillStrictly, BitConverter.GetBytes(playerId));
        SendFrame(worldId, OpLeaveWorld, BuildLeaveWorldPayload(gameId, playerId, mode));
        var (type, reason) = LeaveValues(mode);
        _log.LogInformation("Sent AS_CANCEL_SKILL_STRICTLY (0x1460) + AS_LEAVE_WORLD (0x1392) "
            + "on world {W} gameId={G:X} type={T} reason={R}", worldId, gameId, type, reason);
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
        if (session != null)
        {
            DbProxy?.CommitPlayTime((int)(session.SelectedCharacter?.Id ?? 0), DateTimeOffset.UtcNow.ToUnixTimeSeconds());   // T113
            session.OnWorldLeaveConfirmed();
        }
        else _log.LogWarning("SA_LEAVE_WORLD: no session for gameId {G:X}", gameId);
    }
}

public sealed class WorldLink
{
    public int Id { get; }

    // --- T103: the link's identity, from its own SA_REGISTER. A link that has not registered
    // reads as world 0, which is what every single-World deployment is. ---

    /// <summary>SA_REGISTER payload +5.</summary>
    public int WorldId { get; internal set; } = WorldRegistration.DefaultWorldId;

    /// <summary>SA_REGISTER payload +1. Low 32 bits of every PDId the Arbiter builds.</summary>
    public int PlanetId { get; internal set; }

    /// <summary>SA_REGISTER payload +13. -1 on the control link, 0..N-1 on the bypass links.</summary>
    public int BypassIndex { get; internal set; } = WorldRegistration.ControlBypassIndex;

    /// <summary>Whether this link has sent its SA_REGISTER yet.</summary>
    public bool Registered { get; internal set; }
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
