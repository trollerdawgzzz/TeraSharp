// SPDX-License-Identifier: MIT
// Copyright (c) 2026 the TeraSharp contributors

namespace TeraSharp.Arbiter.World;

// =============================================================================================
// TunnelRouting - T161. Which reorder buffer one recipient's share of an SA_BYPASS_TO_CLIENT
// goes into. Pure, so it can be tested without a WorldBridge; WorldBridge.DeliverTunnelPacket
// and RouteToClient consult it (status/T161-PATCH.diff - WorldBridge.cs is human-owned).
//
// THE BUG. WorldBridge borrowed the only registered buffer for ANY unknown Ticket whenever
// exactly one session was left. That fallback exists for the single-player case (a frame World
// sends before RegisterPlayer ran), but it also fires the moment the second player of a
// two-player session leaves: World keeps addressing the departed Ticket until its own
// SA_LEAVE_WORLD, and every one of those frames - their solo frames and their share of each
// party fan-out - was queued in the REMAINING player's buffer under the departed player's
// sequence numbers. cap_queue1: Ticket 5 was at seq ~2415 and Ticket 6 at ~1291 when the tap
// ended; the arbiter log then shows "Tunnel world=0 key=6 seq stall: expected 1362, have 2730 -
// skipping" twice. A skip sets NextSeq to the departed player's number, so the survivor is
// handed the other player's packets and their own next frames wait for the watchdog again.
//
// THE RULE. A Ticket that was registered and has since gone is never borrowed for: its frames
// are dropped. The borrow stays for a Ticket that was never seen, which is the case it was for.
// TicketAllocator never reissues a freed number until the table wraps (TunnelFrames.cs), so a
// departed Ticket stays departed until RegisterPlayer hands it out again.
// =============================================================================================
public static class TunnelRouting
{
    public enum Route
    {
        /// <summary>The Ticket has its own buffer.</summary>
        Own,
        /// <summary>Never-seen Ticket, exactly one session: the old single-player fallback.</summary>
        Borrow,
        /// <summary>No buffer to use - a departed Ticket, or several sessions to choose from.</summary>
        Drop,
    }

    /// <summary>Where one recipient's share goes.</summary>
    public static Route Decide(bool hasOwnBuffer, bool departed, int registeredCount)
    {
        if (hasOwnBuffer) return Route.Own;
        if (!departed && registeredCount == 1) return Route.Borrow;
        return Route.Drop;
    }
}

/// <summary>
/// T161. The Tickets whose session has gone, per World. <see cref="Add"/> at UnregisterPlayer,
/// <see cref="Forget"/> when a Ticket is registered again, <see cref="Clear"/> when World restarts
/// and its ticket table goes with it. Bounded: past <see cref="Capacity"/> the oldest is
/// forgotten, which only returns that Ticket to the pre-T161 behaviour. Not thread-safe on its
/// own - WorldBridge holds it under its reorder lock.
/// </summary>
public sealed class DepartedTickets
{
    public const int Capacity = 4096;
    private readonly Dictionary<(int World, uint Ticket), long> _set = new();
    private readonly Queue<((int World, uint Ticket) Key, long Stamp)> _order = new();
    private long _stamp;

    public void Add(int worldId, uint ticket)
    {
        var key = (worldId, ticket);
        if (_set.ContainsKey(key)) return;
        _set[key] = ++_stamp;
        _order.Enqueue((key, _stamp));
        while (_order.Count > Capacity)
        {
            var (old, stamp) = _order.Dequeue();
            // A Ticket forgotten and departed again has a newer stamp - leave that one alone.
            if (_set.TryGetValue(old, out long s) && s == stamp) _set.Remove(old);
        }
    }

    /// <summary>The Ticket was registered again: it is live, not departed.</summary>
    public void Forget(int worldId, uint ticket) => _set.Remove((worldId, ticket));

    /// <summary>The Ticket was registered again - on ANY world. TicketAllocator does reissue numbers
    /// (live 2026-09-22: ticket 7 departed on world 0, reissued, registered under a stale
    /// CurrentWorldId, and every frame for it was dropped - the loading-screen hang).</summary>
    public void ForgetEverywhere(uint ticket)
    {
        foreach (var k in _set.Keys.Where(k => k.Ticket == ticket).ToList()) _set.Remove(k);
    }

    public bool Contains(int worldId, uint ticket) => _set.ContainsKey((worldId, ticket));

    public int Count => _set.Count;

    /// <summary>T192. One World restarted; the other Worlds still own their departed Tickets.</summary>
    public void ClearWorld(int worldId)
    {
        foreach (var key in _set.Keys.Where(key => key.World == worldId).ToArray()) _set.Remove(key);
        var keep = _order.Where(item => item.Key.World != worldId).ToArray();
        _order.Clear();
        foreach (var item in keep) _order.Enqueue(item);
    }

    /// <summary>Every Ticket of every World - World restarted (ResetTunnelSequence).</summary>
    public void Clear() { _set.Clear(); _order.Clear(); }
}

/// <summary>
/// T161b. A leave while World is still loading the user waits for SA_ENTER_WORLD - the real
/// Arbiter's User::LeaveWorldStart: LoginState (User+0x3FC0) 1 only RESERVES the leave ("[Pending]",
/// +0x4008/+0x400C/+0x4010) and User::EnterWorldEnd, run by Handler_SA_ENTER_WORLD, sends it.
///
/// <para>cap_crash / arbiter-crash.log (2026-09-22): the relog's AS_ENTER_WORLD (tap 28079, gameId
/// ...AF00003, ticket 7) stalled on World for 188 s; the client was closed at 28218 and our
/// AS_LEAVE_WORLD went out at once. World ignores a leave for a user it is still loading - no
/// SA_LEAVE_WORLD ever came - finished loading it at 28475 (SA_ENTER_WORLD) and kept it. The next
/// relog (ticket 9, 28813) loaded, but World resolved the character to that leftover user and sent
/// S_SPAWN_ME ...AF00003 and the rest to ticket 7 (29068), which the tunnel drops as departed: the
/// loading-screen hang. CurrentWorldId was 0 throughout.</para>
///
/// <para><see cref="Entering"/> from SocialHandlers.RegisterChat (one line before WorldEntry sends
/// AS_ENTER_WORLD), <see cref="TryReserve"/> from WorldBridge.NotifyPlayerLeave (status/T161b-PATCH.diff),
/// <see cref="Entered"/> from DbProxyHandlers on SA_ENTER_WORLD. Bounded; thread-safe.</para>
/// </summary>
public sealed class LeaveGate
{
    public static readonly LeaveGate Shared = new();
    public const int Capacity = 4096;

    /// <summary>A reserved leave: what NotifyPlayerLeave was called with.</summary>
    public sealed record Reservation(int WorldId, ulong GameId, uint PlayerId, LeaveMode Mode);

    private readonly object _lock = new();
    private readonly Dictionary<ulong, Reservation?> _entering = new();
    private readonly HashSet<ulong> _entered = new();
    private readonly Queue<ulong> _order = new();

    /// <summary>AS_ENTER_WORLD is about to go out for <paramref name="gameId"/>. A gameId World already
    /// confirmed is not re-marked - RegisterChat also runs from the whisper self-heal.</summary>
    public void Entering(ulong gameId)
    {
        if (gameId == 0) return;
        lock (_lock)
        {
            if (_entered.Contains(gameId) || _entering.ContainsKey(gameId)) return;
            _entering[gameId] = null;
            Remember(gameId);
        }
    }

    /// <summary>T192. A type-2 cross-World entry keeps its GameId but needs a new destination
    /// SA_ENTER_WORLD before a client leave may be sent (Arb_part_028.c:15685-15755).</summary>
    public void StartTransfer(ulong gameId)
    {
        if (gameId == 0) return;
        lock (_lock)
        {
            _entered.Remove(gameId);
            _entering[gameId] = null;
            Remember(gameId);
        }
    }

    /// <summary>True when World has not confirmed the enter yet: the leave is kept, not sent.</summary>
    public bool TryReserve(ulong gameId, int worldId, uint playerId, LeaveMode mode)
    {
        lock (_lock)
        {
            if (!_entering.ContainsKey(gameId)) return false;
            _entering[gameId] ??= new Reservation(worldId, gameId, playerId, mode);
            return true;
        }
    }

    /// <summary>SA_ENTER_WORLD: the user is in. Returns the leave to send now, or null.</summary>
    public Reservation? Entered(ulong gameId)
    {
        lock (_lock)
        {
            _entered.Add(gameId);
            Remember(gameId);
            return _entering.Remove(gameId, out var r) ? r : null;
        }
    }

    /// <summary>World gave up on the enter (SA_ENTER_WORLD_FAIL, not retried): nothing to leave.</summary>
    public void Forget(ulong gameId) { lock (_lock) _entering.Remove(gameId); }

    public bool IsEntering(ulong gameId) { lock (_lock) return _entering.ContainsKey(gameId); }

    public void Clear() { lock (_lock) { _entering.Clear(); _entered.Clear(); _order.Clear(); } }

    private void Remember(ulong gameId)
    {
        _order.Enqueue(gameId);
        while (_order.Count > Capacity)
        {
            ulong old = _order.Dequeue();
            if (!_order.Contains(old)) { _entered.Remove(old); _entering.Remove(old); }
        }
    }

    /// <summary>SA_ENTER_WORLD's user: the u64 at frame 0x16 (_Handler_SA_ENTER_WORLD), payload 16.</summary>
    public static ulong GameIdOf(ReadOnlySpan<byte> saEnterWorldPayload)
        => saEnterWorldPayload.Length >= 24 ? BitConverter.ToUInt64(saEnterWorldPayload[16..]) : 0;
}
