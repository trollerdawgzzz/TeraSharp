// SPDX-License-Identifier: MIT
// Copyright (c) 2026 the TeraSharp contributors

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
    public readonly OrderedTunnelDelivery Ready = new();
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
    /// <summary>T161: Tickets whose session has gone - never borrowed for (World/TunnelRouting.cs).</summary>
    private readonly DepartedTickets _departed = new();
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
    private readonly BattlefieldCreation _battlefieldCreation = new(); // T199: BF protocol creation/log state
    private readonly BattlefieldHandoff _battlefield = new(); // T199: pending BG offers and native return path
    private readonly CrossWorldHandoff _crossWorld = new();   // T192: live enter data + type-2 continuation

    /// <summary>Allocate a unique tunnel Ticket for a new player session.</summary>
    internal uint AllocateTunnelKey() => AllocateTunnelKey(WorldRegistration.DefaultWorldId);

    /// <summary>The same, in one World's ticket space.</summary>
    internal uint AllocateTunnelKey(int worldId) => _tickets.For(worldId).Allocate();

    public void RegisterPlayer(GameSession s)
    {
        lock (_playersLock)
        {
            _players[s.GameId] = s;
            CharacterTransientState.PlayerRegistered(this, s.PlayerId);   // T192: supersede a departed owner
        }
        lock (_reorderLock)
        {
            if (_tunnels.TryGetValue((s.CurrentWorldId, s.TunnelKey), out var previous)) previous.Ready.Retire();
            _tunnels[(s.CurrentWorldId, s.TunnelKey)] = new TunnelReorderBuffer { Deliver = p => Handlers.ArbiterClientHandlers.DeliverTunnelled(s, p) };   // T121: inject S_ADMIN_GM_SKILL before the tunnelled S_LOAD_TOPO
            _departed.ForgetEverywhere(s.TunnelKey);   // T161 (+ 2026-09-22: a reissued ticket is live on every world)
        }
    }

    public void UnregisterPlayer(ulong gameId, uint tunnelKey)
        => UnregisterPlayer(gameId, WorldRegistration.DefaultWorldId, tunnelKey);

    /// <summary>T111: the Ticket is freed in its own World's space and unkeyed with it.</summary>
    public void UnregisterPlayer(ulong gameId, int worldId, uint tunnelKey, bool transferring = false)
    {
        lock (_playersLock)
        {
            if (_players.Remove(gameId, out var removed))
                CharacterTransientState.PlayerUnregistered(this, worldId, removed.PlayerId);
        }
        DbProxy?.UserControls.Forget(gameId);   // T180: User-owned hold/departure lifetime
        _crossWorld.Forget(gameId);   // T192: a disconnected session must not finish an old transfer
        if (!transferring) _battlefield.ForgetUser(gameId); // T199: preserve BG state only across type2
        lock (_reorderLock)
        {
            if (_tunnels.Remove((worldId, tunnelKey), out var old)) old.Ready.Retire();
            _departed.Add(worldId, tunnelKey);   // T161: World keeps addressing it until SA_LEAVE_WORLD
        }
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
        {
            if (_tunnels.TryGetValue((worldId, key), out var previous)) previous.Ready.Retire();
            _tunnels[(worldId, key)] = new TunnelReorderBuffer { Deliver = callback };
            _departed.ForgetEverywhere(key);   // T161 (+ 2026-09-22)
        }
    }

    /// <summary>Remove a tunnel route by key (test-facing).</summary>
    internal void UnregisterTunnelRoute(uint key)
        => UnregisterTunnelRoute(WorldRegistration.DefaultWorldId, key);

    /// <summary>The same, in one World's ticket space.</summary>
    internal void UnregisterTunnelRoute(int worldId, uint key)
    {
        lock (_reorderLock)
        {
            if (_tunnels.Remove((worldId, key), out var old)) old.Ready.Retire();
            _departed.Add(worldId, key);   // T161
        }
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

    /// <summary>T201 QA StickTogether: native User::TeleportStart, Arb044:6400 and030:6925.</summary>
    public bool TryQaTeleport(GameSession user, int continent, uint channel, float x, float y, float z, short direction)
    {
        if (!user.InWorld || !HasLinks(user.CurrentWorldId)) return false;
        var previous = _crossWorld.EnterFor(user.GameId, user.CurrentWorldId);
        var move = previous == null ? null : CrossWorldHandoff.QaTeleport(previous, user.GameId, continent, channel, x, y, z, direction);
        if (move == null) return false;
        int? owner = CrossWorldHandoff.ResolveDestination(move,
            c => DungeonRouting.Channels.WorldForChannel(move.Continent, unchecked((int)c)),
            DungeonRouting.Channels.WorldForContinent, DungeonRouting.Channels.CatchAllWorldId);
        if (owner is not int destination || !HasLinks(destination)
            || !_crossWorld.Begin(user.CurrentWorldId, destination, move, user.PlayerId)) return false;
        SendFrame(user.CurrentWorldId, OpLeaveWorld, CrossWorldHandoff.BuildLeave(move, user.PlayerId));
        return true;
    }

    /// <summary>Reset every reorder buffer (World restarted).</summary>
    public void ResetTunnelSequence()
    {
        lock (_reorderLock)
        {
            foreach (var buf in _tunnels.Values)
            {
                buf.Pending.Clear();
                buf.Ready.Clear();
                buf.NextSeq = 0;
                buf.LastDelivery = DateTime.UtcNow;
            }
            _departed.Clear();   // T161: World restarted - its ticket table went with it
        }
        _crossWorld.Clear();
        _battlefield.Clear();
        CharacterTransientState.ResetWorldSessions(this);   // T192: same lifetime as the World process
    }

    /// <summary>T192. Reset only the World whose final link disconnected.</summary>
    internal void ResetTunnelSequenceForWorld(int worldId)
    {
        lock (_reorderLock)
        {
            foreach (var pair in _tunnels.Where(pair => pair.Key.World == worldId))
            {
                pair.Value.Pending.Clear();
                pair.Value.Ready.Clear();
                pair.Value.NextSeq = 0;
                pair.Value.LastDelivery = DateTime.UtcNow;
            }
            _departed.ClearWorld(worldId);
        }
        _crossWorld.ForgetWorld(worldId);
        _battlefield.ForgetWorld(worldId);
        int reset = CharacterTransientState.ResetWorldSessions(this, worldId);
        if (reset != 0) _log.LogWarning("World {W} disconnected - reset transient state for {N} character(s)", worldId, reset);
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
            buf.Ready.Clear();
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
        bool departed;
        lock (_reorderLock)
        {
            count = _tunnels.Count;
            departed = _departed.Contains(worldId, key);
            _tunnels.TryGetValue((worldId, key), out var own);
            switch (TunnelRouting.Decide(own != null, departed, count))   // T161
            {
                case TunnelRouting.Route.Own: deliver = own!.Deliver; break;
                case TunnelRouting.Route.Borrow: foreach (var b in _tunnels.Values) { deliver = b.Deliver; break; } break;
            }
        }
        if (deliver != null) { deliver(packet); return; }
        _log.LogDebug("Tunnel ticket {Key} on world {W} {Why} with {N} session(s) - dropped",
            key, worldId, departed ? "has left" : "unknown", count);
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
            _ = link.RunAsync(ct).ContinueWith(_ => OnWorldLinkClosed(link), CancellationToken.None);
        }
        listener.Stop();
    }

    // Also used by the disconnect regression: closing a worker must not reset the whole World.
    internal void OnWorldLinkClosed(WorldLink link)
    {
        int remaining, mine;
        lock (_lock)
        {
            if (!_links.Remove(link)) return;   // one cleanup per actual link
            remaining = _links.Count;
            mine = _links.Count(l => l.WorldId == link.WorldId);
            if (mine == 0) _worlds.For(link.WorldId).MarkDisconnected();
        }
        if (mine == 0)
        {
            ResetTunnelSequenceForWorld(link.WorldId);
            DungeonRouting.Channels.ForgetWorld(link.WorldId);
            DungeonRouting.Transfers.ForgetWorld(link.WorldId);
            _log.LogWarning("World {W} fully disconnected - not ready", link.WorldId);
        }
        _log.LogInformation("WorldServer link #{Id} closed ({N} active)", link.Id, remaining);
    }

    private async Task StallWatchdog(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(50, ct);
            TeraSharp.Arbiter.Handlers.QaDungeonEvents.Tick(Program.Store, this, DateTimeOffset.UtcNow);
            TeraSharp.Arbiter.Handlers.QaSocialCommands.Tick(Program.Store, this, DateTimeOffset.UtcNow);
            TeraSharp.Arbiter.Handlers.QaAchievementCommands.Tick(Program.Store, this, DateTimeOffset.UtcNow);
            TeraSharp.Arbiter.Handlers.QaFestivalCommands.Tick(Program.Store, this, DateTimeOffset.UtcNow);
            TeraSharp.Arbiter.Handlers.QaItemPeriodCommands.Tick(Program.Store, this, DateTimeOffset.UtcNow);
            TeraSharp.Arbiter.Handlers.QaAwakenCommands.Tick(Program.Store, this, DateTimeOffset.UtcNow);
            TeraSharp.Arbiter.Handlers.QaStyleShopCommands.Tick(Program.Store, this, DateTimeOffset.UtcNow);
            _battlefield.Tick(this, DateTimeOffset.UtcNow);
            DbProxy?.UserControls.Tick(DateTimeOffset.UtcNow, ResolveControlUser, HasLinks,
                send => SendFrame(send.WorldId, send.Opcode, send.Payload));
            FlushStalledTunnels(DateTime.UtcNow);
        }
    }

    // T198: watchdog and normal arrivals enqueue under the SAME sequence lock, then use
    // the SAME per-ticket drainer outside it. No callback runs while _reorderLock is held.
    internal void FlushStalledTunnels(DateTime utcNow)
    {
        List<OrderedTunnelDelivery>? flush = null;
        lock (_reorderLock)
        {
            foreach (var (key, buf) in _tunnels)
            {
                if (buf.Pending.Count > 0 && (utcNow - buf.LastDelivery).TotalMilliseconds > StallMs)
                {
                    var first = buf.Pending.Keys.First();
                    _log.LogWarning("Tunnel world={W} key={Key} seq stall: expected {Exp}, "
                        + "have {Have} - skipping", key.World, key.Ticket, buf.NextSeq, first);
                    buf.NextSeq = first;
                    QueueInOrder(buf);
                    flush ??= new();
                    flush.Add(buf.Ready);
                }
            }
        }
        if (flush != null)
            foreach (var ready in flush) ready.Drain();
    }

    private static void QueueInOrder(TunnelReorderBuffer buf)
    {
        while (buf.Pending.TryGetValue(buf.NextSeq, out var pkt))
        {
            buf.Pending.Remove(buf.NextSeq);
            if (buf.Deliver != null) buf.Ready.Enqueue(buf.Deliver, pkt);
            buf.NextSeq++;
            buf.LastDelivery = DateTime.UtcNow;
        }
    }

    // T180: AS_ENTER_WORLD uses our gameId as its opaque Arbiter User handle. Real captures
    // contain pointers instead; never decode either handle as a UserDbId.
    private WorldControlUser? ResolveControlUser(ulong handle)
    {
        var session = PlayerForGameId(handle);
        if (session == null || session.PlayerId == 0) return null;
        return new WorldControlUser((uint)PartyPackets.PlanetId, session.PlayerId, session.CurrentWorldId, () =>
        {
            // Arb_part_062.c:13016 queues User::OnRequestCancelReturnToLobby before leave.
            // TeraSharp has no pending prepare-appearance object (1498 is currently a no-op).
            var pending = session.PendingLobbyReturn;
            if (pending == null) return;
            pending.Cancel();
            session.PendingLobbyReturn = null;
            SendFrame(session.CurrentWorldId, OpUserCancelRequestExit, BitConverter.GetBytes(session.PlayerId));
            session.SendByDef("S_CANCEL_RETURN_TO_LOBBY", new Dictionary<string, object> { ["byInterrupt"] = (byte)0 });
        });
    }

    public void HandleFrame(WorldLink link, ushort op, byte[] payload)
    {
        if (_battlefieldCreation.TryHandle(link, Program.Store, op, payload)) return;
        if (_battlefield.TryHandle(this, link, _crossWorld, Program.Store, op, payload, DateTimeOffset.UtcNow)) return;
        // T180: consume these before DbProxy/replay. Replies go to the owning/target World,
        // including same-World cases; no same-link fallback for an unknown handle or owner.
        if (DbProxy?.UserControls.TryHandle(op, payload, ResolveControlUser, DungeonRouting.Channels,
                HasLinks, send => SendFrame(send.WorldId, send.Opcode, send.Payload), DateTimeOffset.UtcNow) == true)
            return;
        // ---- T138b: the cross-World hand-off (status/MULTIWORLD-DESIGN.md T137c) ----
        // ONLY the cross-link case is taken here; everything else (including a same-World zone
        // change, which also travels 0x13BE/0x13C0 and is completed by the T108 DbProxy handlers)
        // continues into the switch below. Intercepting the same-World case wedged every zone change.
        if (op == 0x164D)   // SA_WORLD_SERVER_STATUS: this link's continent roster -> the continent table
        {
            int n = WorldContinentList.Apply(DungeonRouting.Channels, payload, _log);
            _log.LogInformation("Link #{Id} world {W}: {N} continent(s) rostered", link.Id, link.WorldId, n);
        }
        else if (op == ContinentHandoff.SA_REQUEST_ENTER_CONTINENT)   // 0x13BE: main World asks
        {
            int? continent = ContinentHandoff.ContinentOf(payload);
            int owner = continent is int c ? (DungeonRouting.Channels.WorldForContinent(c) ?? link.WorldId) : link.WorldId;
            if (owner != link.WorldId && LinksOf(owner).Count > 0)
            {
                // T192: full+6 is an opaque User handle, not UserDbId. The old captured
                // prefix addressed every request to player 1 (handoff1's requester was 10).
                var user = payload.Length >= DbProxyHandlers.RequestEnterDungeonMinPayload
                    ? ResolveControlUser(BitConverter.ToUInt64(payload, 0)) : null;
                if (user == null)
                {
                    _log.LogWarning("0x13BE continent {C}: unknown user handle or short request - dropped", continent);
                    return;
                }
                var reply = ContinentHandoff.EnterReply(payload, user.PlanetId, user.UserDbId);
                if (reply != null)
                {
                    DbProxy?.RecordDungeonEntry(payload, response: false, resolvedPlayerId: (int)user.UserDbId);
                    _log.LogInformation("0x13BE player {Pid} continent {C} from world {From} -> 0x13BF to world {To}", user.UserDbId, continent, link.WorldId, owner);
                    SendFrame(owner, ContinentHandoff.AS_ENTER_CONTINENT, reply);
                    return;
                }
            }
        }
        else if (op == ContinentHandoff.SA_CONTINENT_READY)
        {
            // Arb_part_062.c:13719-13741 resolves the returned PDId, then sends to that
            // user's current World. The source can itself be a DungeonServer, not only 0.
            var user = payload.Length >= DbProxyHandlers.ResponseEnterDungeonMinPayload
                ? SessionForPlayerId(BitConverter.ToInt32(payload, ContinentHandoff.UserDbIdOffset)) : null;
            if (user == null)
            {
                _log.LogWarning("0x13C0: unknown user PDId or short response - dropped");
                return;
            }
            if (user.CurrentWorldId != link.WorldId)
            {
                if (HasLinks(user.CurrentWorldId))
                {
                    DbProxy?.RecordDungeonEntry(payload, response: true, resolvedPlayerId: (int)user.PlayerId);
                    SendFrame(user.CurrentWorldId, ContinentHandoff.AS_CONTINENT_READY,
                        ContinentHandoff.ReadyReply(payload)!);
                }
                return;
            }
            // Same-World requests retain the existing DbProxy path and its state updates.
        }

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

            case Handlers.ArbiterClientHandlers.SA_ADMIN_REQUEST_USERACTION:
            {
                // T155: World filled in the live position for a GM "go to" / summon; forward the
                // record verbatim as 0x2827 to the requester's World (cap_final 917 -> 918,
                // 1082 -> 1083). Handler_SA_ADMIN_REQUEST_USERACTION drops it when the requester
                // (payload 84) is gone.
                int requester = Handlers.ArbiterClientHandlers.UserActionRequester(payload);
                var gm = requester > 0 ? SessionForPlayerId(requester) : null;
                if (gm == null)
                {
                    _log.LogInformation("0x2826 for player {Id}: not online - dropped", requester);
                    return;
                }
                SendFrame(gm.CurrentWorldId, Handlers.ArbiterClientHandlers.AS_ADMIN_REQUEST_USERACTION, payload);
                return;
            }

            case OpHeartbeat14:
                // DSA_DUNGEON_TIMELINE_OPEN_INFO. 909 of the 911 frames across the four captures
                // are the empty 14-byte form - a real heartbeat - but the first one after World
                // registers its dungeons carries the open-state list the real Arbiter echoes back
                // as 0x1581, one frame per record (status/HANDSHAKE-DATA.md section 0, T33).
                if (payload.Length > 8) DbProxy?.TryHandle(this, link, op, payload);
                return;

            case OpTunnelToClient:
            {
                if (!TeraSharp.Arbiter.Handlers.QaDiagnosticCommands.BypassEnabled) return; // Native Arb062:3672; group bypass is separate.
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

            case CrossWorldHandoff.SA_TELEPORT:
            {
                var move = CrossWorldHandoff.Parse(payload);
                var user = move == null ? null : PlayerForGameId(move.UserHandle);
                if (move == null || user == null || user.CurrentWorldId != link.WorldId) return;
                int? owner = CrossWorldHandoff.ResolveDestination(move,
                    channel => DungeonRouting.Channels.WorldForChannel(move.Continent, unchecked((int)channel)),
                    DungeonRouting.Channels.WorldForContinent, DungeonRouting.Channels.CatchAllWorldId);
                if (owner is not int target || !HasLinks(target)
                    || !_crossWorld.Begin(link.WorldId, target, move, user.PlayerId))
                {
                    _log.LogWarning("SA_TELEPORT player {Pid}: missing target World, live enter data, or duplicate transfer", user.PlayerId);
                    return;
                }
                // Native TeleportStart calls LeaveWorldStart(type2); no lobby response.
                link.SendFrame(OpLeaveWorld, CrossWorldHandoff.BuildLeave(move, user.PlayerId));
                return;
            }

            case OpSaLeaveWorld:
                HandleSaLeaveWorld(link, payload);
                return;

            default:
                if (PartyWiring.TryHandleWorldFrame(op, payload)) return;   // T49: the twelve party SA_ opcodes incl. SA_BYPASS_TO_GROUP
                if (GuildWiring.TryHandleWorldFrame(op, payload, link.SendFrame)) return;   // T52: guild SA_ opcodes (membership test, not a length gate)
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
        if (op == OpPlayerEnter)
        {
            TeraSharp.Arbiter.Handlers.QaGeneralCommands.StampEnterWorldFlags(payload, Program.Store);
            DbProxy?.StampEnterWorldCrestPoints(payload); // T197: refresh earned extras before caching any entry/transfer/retry.
            _crossWorld.RememberEnter(worldId, payload);
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
        TunnelReorderBuffer? buf = null;
        lock (_reorderLock)
        {
            // T161: a departed ticket must never borrow the remaining player's sequence.
            _tunnels.TryGetValue((worldId, ticket), out var own);
            switch (TunnelRouting.Decide(own != null, _departed.Contains(worldId, ticket), _tunnels.Count))
            {
                case TunnelRouting.Route.Own: buf = own; break;
                case TunnelRouting.Route.Borrow: foreach (var b in _tunnels.Values) { buf = b; break; } break;
            }
            if (buf != null)
            {
                buf.Pending[seq] = clientPkt;
                QueueInOrder(buf);
            }
        }
        if (buf != null) { buf.Ready.Drain(); return; }
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
        var user = SessionForPlayerId(unchecked((int)playerId));
        if (user == null || !user.InWorld || !HasLinks(user.CurrentWorldId)) return;
        SendFrame(user.CurrentWorldId, OpUserRequestExit, BitConverter.GetBytes(playerId));
        _log.LogInformation("Sent AS_USER_REQUEST_EXIT (0x14FF) for player {Id}", playerId);
    }

    /// <summary>Countdown cancelled. Writer FUN_140353530: AS_USER_CANCEL_REQUEST_EXIT [u32 playerId].</summary>
    public void SendUserCancelRequestExit(uint playerId)
    {
        var user = SessionForPlayerId(unchecked((int)playerId));
        if (user == null || !user.InWorld || !HasLinks(user.CurrentWorldId)) return;
        SendFrame(user.CurrentWorldId, OpUserCancelRequestExit, BitConverter.GetBytes(playerId));
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
        // T161b: User::LeaveWorldStart [Pending] - World is still loading this user (no
        // SA_ENTER_WORLD yet) and drops a leave for it; DbProxyHandlers sends it on SA_ENTER_WORLD.
        if (LeaveGate.Shared.TryReserve(gameId, worldId, playerId, mode))
        {
            _log.LogInformation("AS_LEAVE_WORLD for gameId {G:X} held until SA_ENTER_WORLD (World is still loading it)", gameId);
            return;
        }
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
            if (payload.Length >= 24 && BitConverter.ToUInt32(payload, 16) == 2)
            {
                var transfer = _crossWorld.Take(gameId, link.WorldId);
                if (transfer == null || !session.InWorld || !HasLinks(transfer.DestinationWorld))
                {
                    _log.LogWarning("Type-2 SA_LEAVE_WORLD for {G:X}: no live transfer destination", gameId);
                    return;
                }
                // All source saves precede this confirmation (retail7903 -> 7906). Native
                // LeaveWorldEnd commits the destination before owner138E/2711; preserve login
                // account/session fields and update only the captured transfer fields.
                DbProxy?.CommitTeleportLocation((int)session.PlayerId, transfer.Move, transfer.DestinationWorld);
                UnregisterPlayer(session.GameId, session.CurrentWorldId, session.TunnelKey, transferring: true);
                session.CurrentWorldId = transfer.DestinationWorld;
                session.TunnelKey = AllocateTunnelKey(session.CurrentWorldId);
                RegisterPlayer(session);
                PartyWiring.Register(session);
                LeaveGate.Shared.StartTransfer(session.GameId);
                var enter = CrossWorldHandoff.BuildEnter(transfer.PreviousEnter, transfer.Move, session.TunnelKey);
                // Matching can form a party after login. Native EnterWorldStart reads the
                // current Party each time (Arb028:15773-15781/15847-15850), not the login snapshot.
                var party = PartyWiring.Manager.FindByMember((int)session.PlayerId);
                BitConverter.GetBytes((ulong)(party?.Id ?? 0)).CopyTo(enter, 94);
                enter[102] = party?.IsSys == true ? (byte)1 : (byte)0;
                SendFrame(session.CurrentWorldId, OpPlayerEnter, enter);
                _battlefield.CompleteTransfer(session.PlayerId, session.CurrentWorldId);
                _log.LogInformation("Transferred player {Pid} from World {From} to {To} ({Continent}/{Channel})",
                    session.PlayerId, link.WorldId, session.CurrentWorldId, transfer.Move.Continent, transfer.Move.Channel);
                return;
            }
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
