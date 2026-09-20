using TeraSharp.Arbiter.Network;

namespace TeraSharp.Arbiter.World;

// =============================================================================================
// MatchWiring - the four instance-matching client packets, T136b. Shaped like PartyWiring:
// one ClientOpcodes table, one MinBodyLength, one OnClientPacket, so HandlerRegistry needs a
// single foreach.
//
// SPLIT BY SAFETY, not by convenience:
//
//   C_MATCH_PROGRESS    READ-ONLY. Answers from MatchQueueManager's own state.
//   C_MATCH_ROOM_LIST   READ-ONLY. Answers an EMPTY list, which is the truth: we have no rooms.
//
//   C_MATCH_ADD         REFUSED. Registered so the packet stops being forwarded to a World that
//   C_MATCH_DEL         has no handler for it, but answered with the not-queued form.
//
// WHY ADD IS REFUSED. T136 pinned S_FIN_INTER_PARTY_MATCH, and it is a SIGNAL, not a teleport:
// after it the stack must create an instance, tell the party which server it is on, and push the
// S_LOAD_TOPO that moves them. None of that exists - TeraSharp has one World and no
// DungeonServer. Accepting a queue and then emitting FIN would close the client's matching
// window on "match found" and leave it waiting for a load that never comes, which is worse than
// refusing. So this file NEVER calls BuildFinInterPartyMatch. When the hand-off lands, OnMatchAdd
// is the one method that changes.
//
// The refusal is built out of the frames the capture already pins:
//   C_MATCH_ADD -> S_CHANGE_EVENT_MATCHING_STATE(queued: false) + S_CANCEL_PARTY_MATCH_POOL
//   C_MATCH_DEL -> S_DEL_INTER_PARTY_MATCH_POOL + S_CHANGE_EVENT_MATCHING_STATE(queued: false)
// Both leave the client's window in the state it would be in after a normal cancel, which is
// exactly what "you are not in a queue" looks like on the wire. No invented system-message code:
// classic_live3 never shows one on this path, and an @code we made up would render as garbage.
// =============================================================================================
public static class MatchWiring
{
    /// <summary>The four opcodes, for HandlerRegistry's foreach and for ArbiterOwned.</summary>
    public static readonly (string Name, ushort Opcode)[] ClientOpcodes =
    {
        ("C_MATCH_PROGRESS",  MatchQueueManager.C_MATCH_PROGRESS),    // 0xF7B8  read-only
        ("C_MATCH_ROOM_LIST", MatchQueueManager.C_MATCH_ROOM_LIST),   // 0x88D0  read-only
        ("C_MATCH_ADD",       MatchQueueManager.C_MATCH_ADD),         // 0xCE5F  refused
        ("C_MATCH_DEL",       MatchQueueManager.C_MATCH_DEL),         // 0xC057  refused
    };

    /// <summary>
    /// Minimum BODY length. C_MATCH_PROGRESS and C_MATCH_DEL are 24-byte frames in every captured
    /// instance (records 8670, 9183, 9476) and C_MATCH_ROOM_LIST is 28 (record 8826), but the
    /// array they carry could legitimately be empty, so the guard is only the 4-byte array header
    /// the reader needs. C_MATCH_ADD is 55 B in both captures; it is refused without being parsed,
    /// so it needs no guard at all.
    /// </summary>
    public static int MinBodyLength(ushort op) => op switch
    {
        MatchQueueManager.C_MATCH_PROGRESS => 4,
        MatchQueueManager.C_MATCH_ROOM_LIST => 4,
        _ => 0,
    };

    /// <summary>True when <paramref name="op"/> is one of <see cref="ClientOpcodes"/>.</summary>
    public static bool Handles(ushort op)
    {
        foreach (var (_, code) in ClientOpcodes) if (code == op) return true;
        return false;
    }

    /// <summary>
    /// One of <see cref="ClientOpcodes"/> arrived. Always returns true: all four are the
    /// Arbiter's, so there is never a reason to fall through to World.
    /// </summary>
    public static bool OnClientPacket(GameSession? session, ushort opcode, ReadOnlyMemory<byte> body)
    {
        if (session == null) return true;
        switch (opcode)
        {
            case MatchQueueManager.C_MATCH_PROGRESS: return OnMatchProgress(session, body);
            case MatchQueueManager.C_MATCH_ROOM_LIST: return OnMatchRoomList(session);
            case MatchQueueManager.C_MATCH_ADD: return OnMatchAdd(session);
            case MatchQueueManager.C_MATCH_DEL: return OnMatchDel(session, body);
            default: return true;
        }
    }

    /// <summary>
    /// C_MATCH_PROGRESS -> S_MATCH_PROGRESS, from whatever this leader has queued. The client
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
    /// C_MATCH_ROOM_LIST -> an empty S_MATCH_ROOM_LIST. We keep no rooms, and the 44-byte row's
    /// trailing flags are not pinned, so an empty list is the only honest answer.
    /// </summary>
    public static bool OnMatchRoomList(GameSession session)
    {
        session.Send(MatchQueueManager.BuildEmptyMatchRoomList());
        return true;
    }

    /// <summary>
    /// C_MATCH_ADD -> refused, because FIN has nowhere to lead. The body is deliberately NOT
    /// parsed: nothing is stored, so there is nothing to parse it into, and a half-kept queue
    /// entry would be worse than none. See the header for when this changes.
    /// </summary>
    public static bool OnMatchAdd(GameSession session)
    {
        session.Send(MatchQueueManager.BuildChangeEventMatchingState(Array.Empty<int>(), queued: false));
        session.Send(MatchQueueManager.BuildCancelPartyMatchPool());
        return true;
    }

    /// <summary>
    /// C_MATCH_DEL -> the leave pair, and drop any entry this leader had. Safe whether or not
    /// anything was queued: <see cref="MatchQueueManager.Remove"/> is idempotent, and the client
    /// wants the window closed either way.
    /// </summary>
    public static bool OnMatchDel(GameSession session, ReadOnlyMemory<byte> body)
    {
        var ids = MatchQueueManager.ReadInstanceIds(body);
        int instance = ids.Count > 0 ? ids[0] : MatchQueueManager.AnyInstance;
        MatchQueueManager.Remove(session.PlayerId);
        session.Send(MatchQueueManager.BuildDelPool(instance));
        session.Send(MatchQueueManager.BuildChangeEventMatchingState(Array.Empty<int>(), queued: false));
        return true;
    }
}
