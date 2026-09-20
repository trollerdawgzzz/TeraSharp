using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TeraSharp.Arbiter.Network;

namespace TeraSharp.Arbiter.World;

// =============================================================================================
// MatchWiring - the four instance-matching client packets. Shaped like PartyWiring: one
// ClientOpcodes table, one MinBodyLength, one OnClientPacket, so HandlerRegistry needs a single
// foreach.
//
//   C_MATCH_PROGRESS    READ-ONLY. Answers from MatchQueueManager's own state.
//   C_MATCH_ROOM_LIST   READ-ONLY. Answers an EMPTY list, which is the truth: we have no rooms.
//   C_MATCH_ADD         T138c: POOLS, FORMS AND FINS. Was refused through T136b.
//   C_MATCH_DEL         Leaves the pool.
//
// WHY ADD IS NO LONGER REFUSED. T136b refused it because "FIN is a signal, not a teleport" and
// nothing existed to move a matched party into an instance. That was half right and the wrong
// half mattered: classic_live3 shows the real server does not move anybody either. After FIN it
// forms the party, and the PLAYER then presses enter and sends C_ENTER_DUNGEON, which is the
// ordinary walk-in path TeraSharp already routes (MatchQueueManager's header has the record
// numbers). So FIN closes the window and the client does the rest - there is nothing left to
// wait for, and refusing a queue was costing more than accepting one.
//
// WHERE THE ROLES COME FROM. C_MATCH_ADD carries no class - four captures, MatchComposition's
// header. The queuing party is read out of PartyManager and each member's class and level come
// from their own session, so a client cannot queue as a tank by editing a packet.
//
// TIMEOUTS ARE SWEPT, NOT TICKED. There is no scheduler in the Arbiter to hang a matching tick
// on, so <see cref="Sweep"/> runs at the top of every match packet. A queue nobody touches
// therefore expires the next time ANY client speaks to the matcher, which is late but never
// wrong; the alternative - a timer thread for two packets - is not worth the machinery.
// =============================================================================================
public static class MatchWiring
{
    /// <summary>The four opcodes, for HandlerRegistry's foreach and for ArbiterOwned.</summary>
    public static readonly (string Name, ushort Opcode)[] ClientOpcodes =
    {
        ("C_MATCH_PROGRESS",  MatchQueueManager.C_MATCH_PROGRESS),    // 0xF7B8  read-only
        ("C_MATCH_ROOM_LIST", MatchQueueManager.C_MATCH_ROOM_LIST),   // 0x88D0  read-only
        ("C_MATCH_ADD",       MatchQueueManager.C_MATCH_ADD),         // 0xCE5F  queues
        ("C_MATCH_DEL",       MatchQueueManager.C_MATCH_DEL),         // 0xC057  leaves
    };

    /// <summary>
    /// Minimum BODY length. C_MATCH_PROGRESS and C_MATCH_DEL are 24-byte frames in every
    /// captured instance (records 8670, 9183, 9476) and C_MATCH_ROOM_LIST is 28 (record 8826),
    /// but the array they carry could legitimately be empty, so the guard is only the 4-byte
    /// array header the reader needs. C_MATCH_ADD is 55 B in all four captured instances and
    /// carries two array headers plus a scalar, so its guard is 10.
    /// </summary>
    public static int MinBodyLength(ushort op) => op switch
    {
        MatchQueueManager.C_MATCH_PROGRESS => 4,
        MatchQueueManager.C_MATCH_ROOM_LIST => 4,
        MatchQueueManager.C_MATCH_ADD => 10,
        MatchQueueManager.C_MATCH_DEL => 4,
        _ => 0,
    };

    /// <summary>True when <paramref name="op"/> is one of <see cref="ClientOpcodes"/>.</summary>
    public static bool Handles(ushort op)
    {
        foreach (var (_, code) in ClientOpcodes) if (code == op) return true;
        return false;
    }

    // ---- the seams the tests replace -------------------------------------------------------

    /// <summary>
    /// The party <paramref name="session"/> would queue with, leader first. The default reads
    /// PartyManager; a session with no party queues alone. Replaced wholesale in tests, which
    /// have no PartyManager and no sessions.
    /// </summary>
    public static Func<GameSession, IReadOnlyList<MatchQueueManager.Queuer>> PartyOf = DefaultPartyOf;

    /// <summary>
    /// How one formed member is reached. The default finds the live session by character id
    /// and sends; tests collect instead. It takes the <see cref="MatchQueueManager.Queuer"/>
    /// rather than a session so a member who logged out between formation and delivery is a
    /// dropped packet and not a null reference.
    /// </summary>
    public static Action<MatchQueueManager.Queuer, byte[]> Deliver = DefaultDeliver;

    /// <summary>Tests only: put both seams back and empty the pool.</summary>
    public static void Reset()
    {
        PartyOf = DefaultPartyOf;
        Deliver = DefaultDeliver;
        MatchQueueManager.Reset();
    }

    private static ILogger _log = NullLogger.Instance;

    /// <summary>Give the matcher the process logger, the way PartyWiring.UsePartyLogger does.
    /// Called once, from SocialHandlers' logger block.</summary>
    public static void UseMatchLogger(ILogger log) => _log = log ?? NullLogger.Instance;

    private static ILogger Log => _log;

    /// <summary>The queuer one session stands for, from its own character row.</summary>
    public static MatchQueueManager.Queuer QueuerFor(GameSession s)
    {
        var chr = s.SelectedCharacter;
        return new MatchQueueManager.Queuer(s.PlayerId, (int)(chr?.Id ?? 0),
                                            chr?.Class ?? 0, chr?.Level ?? 1);
    }

    private static IReadOnlyList<MatchQueueManager.Queuer> DefaultPartyOf(GameSession s)
    {
        var me = QueuerFor(s);
        var party = me.CharacterId > 0 ? PartyWiring.Manager.FindByMember(me.CharacterId) : null;
        if (party == null) return new[] { me };

        var outp = new List<MatchQueueManager.Queuer> { me };
        foreach (var m in party.Members())
        {
            if (m.UserDbId == me.CharacterId || !m.Online) continue;
            var peer = SessionForCharacter(m.UserDbId);
            outp.Add(new MatchQueueManager.Queuer(peer?.PlayerId ?? 0, m.UserDbId, m.Class, m.Level));
        }
        return outp;
    }

    /// <summary>The live session for a character id, or null. Walks the in-world roster.</summary>
    public static GameSession? SessionForCharacter(int characterId)
    {
        if (characterId <= 0) return null;
        var world = Program.World;
        if (world == null) return null;
        foreach (var s in world.InWorldSessions())
            if (s.SelectedCharacter != null && (int)s.SelectedCharacter.Id == characterId) return s;
        return null;
    }

    private static void DefaultDeliver(MatchQueueManager.Queuer q, byte[] packet)
        => SessionForCharacter(q.CharacterId)?.Send(packet);

    // ---- the handlers ----------------------------------------------------------------------

    /// <summary>
    /// One of <see cref="ClientOpcodes"/> arrived. Always returns true: all four are the
    /// Arbiter's, so there is never a reason to fall through to World.
    /// </summary>
    public static bool OnClientPacket(GameSession? session, ushort opcode, ReadOnlyMemory<byte> body)
    {
        if (session == null) return true;
        Sweep(DateTimeOffset.UtcNow);
        switch (opcode)
        {
            case MatchQueueManager.C_MATCH_PROGRESS: return OnMatchProgress(session, body);
            case MatchQueueManager.C_MATCH_ROOM_LIST: return OnMatchRoomList(session);
            case MatchQueueManager.C_MATCH_ADD: return OnMatchAdd(session, body);
            case MatchQueueManager.C_MATCH_DEL: return OnMatchDel(session, body);
            default: return true;
        }
    }

    /// <summary>
    /// C_MATCH_PROGRESS -&gt; S_MATCH_PROGRESS, from whatever this leader has queued. The client
    /// asks either about one instance or about <see cref="MatchQueueManager.AnyInstance"/>
    /// (-9999, record 8670); <see cref="MatchQueueManager.Progress"/> resolves that to the
    /// entry's own instance. With nothing queued the reply names what was asked for and carries
    /// zeroes, which is what an empty pool looks like.
    /// </summary>
    public static bool OnMatchProgress(GameSession session, ReadOnlyMemory<byte> body)
    {
        var ids = MatchQueueManager.ReadInstanceIds(body);
        int asked = ids.Count > 0 ? ids[0] : MatchQueueManager.AnyInstance;
        var entry = MatchQueueManager.Find(session.PlayerId);
        var (instance, waiting, needed) = MatchQueueManager.Progress(entry, asked);
        session.Send(MatchQueueManager.BuildMatchProgress(instance, 0, 0, waiting, needed, waiting));
        return true;
    }

    /// <summary>
    /// C_MATCH_ROOM_LIST -&gt; an empty S_MATCH_ROOM_LIST. We keep no rooms, and the 44-byte row's
    /// trailing flags are not pinned, so an empty list is the only honest answer.
    /// </summary>
    public static bool OnMatchRoomList(GameSession session)
    {
        session.Send(MatchQueueManager.BuildEmptyMatchRoomList());
        return true;
    }

    /// <summary>
    /// C_MATCH_ADD. Queue the caller's party for every instance the frame names, answer with
    /// the pool-add and the queued state exactly as records 8662 and 8664 do, then run one
    /// formation pass per instance and FIN whatever came out.
    ///
    /// <para>A frame that names no instance is answered with the not-queued pair rather than
    /// with an empty queue entry: there is nothing to wait for and the client's window should
    /// close.</para>
    /// </summary>
    public static bool OnMatchAdd(GameSession session, ReadOnlyMemory<byte> body)
    {
        var ids = MatchQueueManager.ReadInstanceIds(body);
        if (ids.Count == 0) return Refuse(session);

        var members = PartyOf(session);
        if (members.Count == 0) return Refuse(session);

        var now = DateTimeOffset.UtcNow;
        MatchQueueManager.Add(session.PlayerId, ids.ToArray(), members, now);

        int flag = MatchQueueManager.ReadQueueFlag(body);
        var pool = new List<MatchQueueManager.PoolPlayer>(members.Count);
        foreach (var m in members)
            pool.Add(new MatchQueueManager.PoolPlayer(PartyPackets.PlanetId, (int)m.PlayerId, 0, flag));

        session.Send(MatchQueueManager.BuildAddInterPartyMatchPool(ids[0], pool));
        session.Send(MatchQueueManager.BuildChangeEventMatchingState(Array.Empty<int>(), queued: true));

        Log.LogInformation("match: player {P} queued {N} member(s) for [{Ids}]",
            session.PlayerId, members.Count, string.Join(",", ids));

        foreach (int id in ids) TryFormAndFinish(id, now);
        return true;
    }

    /// <summary>
    /// One formation pass on one instance, and FIN to everybody in whatever formed. Returns the
    /// group so a caller (and the tests) can see what happened.
    /// </summary>
    public static MatchQueueManager.FormedGroup? TryFormAndFinish(int instanceId,
        DateTimeOffset now, Random? rng = null)
    {
        var group = MatchQueueManager.TryForm(instanceId, now, rng);
        if (group == null) return null;

        var fin = MatchQueueManager.BuildFinInterPartyMatch(instanceId);
        foreach (var m in group.Members) Deliver(m, fin);

        foreach (var e in group.Entries) MatchQueueManager.Remove(e.LeaderPlayerId);

        Log.LogInformation("match: instance {Id} formed with {N} player(s) from {E} entr(ies)",
            instanceId, group.Members.Count, group.Entries.Count);
        return group;
    }

    /// <summary>
    /// C_MATCH_DEL -&gt; the leave pair, and drop any entry this player is in. Safe whether or not
    /// anything was queued: <see cref="MatchQueueManager.RemoveByPlayer"/> is idempotent, and
    /// the client wants the window closed either way.
    /// </summary>
    public static bool OnMatchDel(GameSession session, ReadOnlyMemory<byte> body)
    {
        var ids = MatchQueueManager.ReadInstanceIds(body);
        int instance = ids.Count > 0 ? ids[0] : MatchQueueManager.AnyInstance;
        MatchQueueManager.RemoveByPlayer(session.PlayerId);
        session.Send(MatchQueueManager.BuildDelPool(instance));
        session.Send(MatchQueueManager.BuildChangeEventMatchingState(Array.Empty<int>(), queued: false));
        return true;
    }

    /// <summary>
    /// The session went away. Unlike a party, a QUEUE does not survive a logout - there is
    /// nobody to put in the group - so the whole entry goes, leader or member.
    /// </summary>
    public static void Unregister(GameSession? session)
    {
        if (session == null || session.PlayerId == 0) return;
        if (MatchQueueManager.RemoveByPlayer(session.PlayerId))
            Log.LogDebug("match: player {P} left world - queue entry withdrawn", session.PlayerId);
    }

    /// <summary>
    /// Expire whatever has waited too long and tell each leader, with the same cancel pair a
    /// board-side cancel uses (record 53967 plus the not-queued state). Returns how many went.
    /// </summary>
    public static int Sweep(DateTimeOffset now)
    {
        var expired = MatchQueueManager.SweepTimeouts(now);
        foreach (var e in expired)
        {
            int instance = e.InstanceIds.Length > 0 ? e.InstanceIds[0] : MatchQueueManager.AnyInstance;
            foreach (var m in e.Members)
            {
                Deliver(m, MatchQueueManager.BuildCancelPartyMatchPool(instance));
                Deliver(m, MatchQueueManager.BuildChangeEventMatchingState(
                    Array.Empty<int>(), queued: false));
            }
            MatchQueueManager.Remove(e.LeaderPlayerId);
        }
        if (expired.Count > 0)
            Log.LogInformation("match: {N} queue entr(ies) timed out after {S}s",
                expired.Count, MatchQueueManager.QueueTimeoutSeconds);
        return expired.Count;
    }

    /// <summary>
    /// The T136b answer, kept for the cases where there is nothing to queue: the client is put
    /// back in the state a normal cancel leaves it in, which is what "you are not in a queue"
    /// looks like on the wire. No invented system-message code - classic_live3 never shows one
    /// on this path, and an @code we made up would render as garbage.
    /// </summary>
    private static bool Refuse(GameSession session)
    {
        session.Send(MatchQueueManager.BuildChangeEventMatchingState(Array.Empty<int>(), queued: false));
        session.Send(MatchQueueManager.BuildCancelPartyMatchPool());
        return true;
    }
}
