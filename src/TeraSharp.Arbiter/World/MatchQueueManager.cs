using Microsoft.Extensions.Logging;
using TeraSharp.Arbiter.Network;

namespace TeraSharp.Arbiter.World;

// =============================================================================================
// MatchQueueManager - the instance-matching queue, T136. RAM only.
//
// Ground truth: D:\packetlogs\classic_live3.log, the LEADER side. T134 decoded classic_live2 and
// T134b's note established that capture was a party MEMBER - it only ever showed the pushes.
// classic_live3 is the human queueing Kelsaik (instance 9739) as party leader, matching, and
// cancelling once, so it pins the REQUEST half for the first time.
//
// THE LIFECYCLE, by record number in that capture:
//
//   8643  C->S C_MATCH_ADD                    the leader queues
//   8662  S->C S_ADD_INTER_PARTY_MATCH_POOL   the pool now holds this party
//   8664  S->C S_CHANGE_EVENT_MATCHING_STATE  queued = 1
//   8670  C->S C_MATCH_PROGRESS               "how is it going", instance -9999 = any
//   8678  S->C S_MATCH_PROGRESS
//   8826  C->S C_MATCH_ROOM_LIST              the browse window
//   8838  S->C S_MATCH_ROOM_LIST              456 B, ten rooms
//   9183  C->S C_MATCH_PROGRESS               again, this time naming 9739
//   9193  S->C S_MATCH_PROGRESS
//   9476  C->S C_MATCH_DEL                    the leader cancels
//   9487  S->C S_CHANGE_EVENT_MATCHING_STATE  queued = 0
//   9489  S->C S_DEL_INTER_PARTY_MATCH_POOL
//   9491  S->C S_CHANGE_EVENT_MATCHING_STATE  queued = 0 again (the client is told twice)
//   10322 C->S C_MATCH_ADD                    queued a second time
//   10585 S->C S_FIN_INTER_PARTY_MATCH        MATCH FOUND
//   53967 S->C S_CANCEL_PARTY_MATCH_POOL      the separate board-side cancel
//
// -9999 (0xFFFFD8F1) is the "any instance" sentinel the client uses in C_MATCH_PROGRESS and that
// S_CANCEL_PARTY_MATCH_POOL echoes. It is NOT -10001, which is what a careless read of the hex
// gives.
//
// WHAT THIS DOES NOT DO. There is no MatchServer, so nothing here decides that strangers should
// be put together. A party that queues with enough members matches itself immediately and gets
// S_FIN_INTER_PARTY_MATCH; anything else waits. The instance hand-off that should follow FIN -
// creating the instance and moving the party into it - is NOT implemented and is written up in
// status/MULTIWORLD-DESIGN.md as the next step.
// =============================================================================================
public static class MatchQueueManager
{
    /// <summary>The client's "any instance" sentinel: 0xFFFFD8F1.</summary>
    public const int AnyInstance = -9999;

    /// <summary>How many party members this stub needs before it declares a match.</summary>
    public const int MembersForInstantMatch = 1;

    public const ushort C_MATCH_ADD = 0xCE5F;
    public const ushort C_MATCH_DEL = 0xC057;
    public const ushort C_MATCH_PROGRESS = 0xF7B8;
    public const ushort C_MATCH_ROOM_LIST = 0x88D0;
    public const ushort S_ADD_INTER_PARTY_MATCH_POOL = 0xC730;
    public const ushort S_DEL_INTER_PARTY_MATCH_POOL = 0x72DC;
    public const ushort S_MATCH_PROGRESS = 0x8BE4;
    public const ushort S_MATCH_ROOM_DELETE = 0x74F5;
    public const ushort S_CANCEL_PARTY_MATCH_POOL = 0xD756;
    public const ushort S_FIN_INTER_PARTY_MATCH = 0x6470;
    public const ushort S_CHANGE_EVENT_MATCHING_STATE = 0x87AC;

    /// <summary>One party's queue entry. RAM only - a restart empties every queue, which is the
    /// same thing the real stack does when the match server restarts.</summary>
    public sealed class Entry
    {
        public uint LeaderPlayerId { get; init; }
        public int[] InstanceIds { get; init; } = Array.Empty<int>();
        public uint[] MemberPlayerIds { get; init; } = Array.Empty<uint>();
        public DateTimeOffset QueuedAt { get; init; }
        public bool Matched { get; set; }
    }

    private static readonly object Lock = new();
    private static readonly Dictionary<uint, Entry> ByLeader = new();

    /// <summary>Every queue entry, for tests and for the admin view.</summary>
    public static IReadOnlyList<Entry> All()
    {
        lock (Lock) return new List<Entry>(ByLeader.Values);
    }

    /// <summary>The entry a leader has open, or null.</summary>
    public static Entry? Find(uint leaderPlayerId)
    {
        lock (Lock) return ByLeader.TryGetValue(leaderPlayerId, out var e) ? e : null;
    }

    /// <summary>Drops every entry. Tests only.</summary>
    public static void Reset()
    {
        lock (Lock) ByLeader.Clear();
    }

    /// <summary>
    /// C_MATCH_ADD. Returns the entry, and <paramref name="matched"/> when this stub decides the
    /// party is already big enough - which is the whole of our matchmaking until a MatchServer
    /// exists.
    /// </summary>
    public static Entry Add(uint leaderPlayerId, int[] instanceIds, uint[] memberPlayerIds,
        DateTimeOffset now, out bool matched)
    {
        var e = new Entry
        {
            LeaderPlayerId = leaderPlayerId,
            InstanceIds = instanceIds ?? Array.Empty<int>(),
            MemberPlayerIds = memberPlayerIds ?? Array.Empty<uint>(),
            QueuedAt = now,
        };
        matched = e.MemberPlayerIds.Length >= MembersForInstantMatch && e.InstanceIds.Length > 0;
        e.Matched = matched;
        lock (Lock) ByLeader[leaderPlayerId] = e;
        return e;
    }

    /// <summary>C_MATCH_DEL. True when there was something to remove.</summary>
    public static bool Remove(uint leaderPlayerId)
    {
        lock (Lock) return ByLeader.Remove(leaderPlayerId);
    }

    /// <summary>
    /// The numbers S_MATCH_PROGRESS carries for an entry. The capture only ever shows a party of
    /// its own in the pool, so the two counters are its own size; classic_live2's member-side
    /// frame showed 8 of 17, which is what a real pool would put here.
    /// </summary>
    public static (int Instance, int Waiting, int Needed) Progress(Entry? e, int askedFor)
    {
        if (e == null || e.InstanceIds.Length == 0) return (askedFor, 0, 0);
        int instance = askedFor == AnyInstance ? e.InstanceIds[0] : askedFor;
        return (instance, e.MemberPlayerIds.Length, e.MemberPlayerIds.Length);
    }

    // ---- readers -------------------------------------------------------------------------

    /// <summary>
    /// The instance ids out of C_MATCH_DEL and C_MATCH_PROGRESS, which share one layout:
    /// <c>[u16 count][u16 offset]</c> then 12-byte elements <c>[u16 here][u16 next][i32 id]
    /// [i32][i32]</c>. Records 8670 (-9999), 9183 and 9476 (9739).
    /// </summary>
    public static List<int> ReadInstanceIds(ReadOnlyMemory<byte> body)
    {
        var list = new List<int>();
        var b = body.Span;
        if (b.Length < 4) return list;
        int count = BitConverter.ToUInt16(b[..2]);
        int off = BitConverter.ToUInt16(b.Slice(2, 2)) - 4;
        for (int i = 0; i < count && off >= 0 && off + 12 <= b.Length; i++)
        {
            int next = BitConverter.ToUInt16(b.Slice(off + 2, 2)) - 4;
            list.Add(BitConverter.ToInt32(b.Slice(off + 4, 4)));
            if (next < 0) break;
            off = next;
        }
        return list;
    }

    // ---- builders ------------------------------------------------------------------------

    private static byte[] OneElementArray(ushort opcode, params int[] fields)
    {
        int len = 4 + 4 + 4 + fields.Length * 4;
        var p = new byte[len];
        BitConverter.GetBytes((ushort)len).CopyTo(p, 0);
        BitConverter.GetBytes(opcode).CopyTo(p, 2);
        BitConverter.GetBytes((ushort)1).CopyTo(p, 4);
        BitConverter.GetBytes((ushort)8).CopyTo(p, 6);
        BitConverter.GetBytes((ushort)8).CopyTo(p, 8);
        BitConverter.GetBytes((ushort)0).CopyTo(p, 10);
        for (int i = 0; i < fields.Length; i++) BitConverter.GetBytes(fields[i]).CopyTo(p, 12 + i * 4);
        return p;
    }

    /// <summary>S_MATCH_PROGRESS, record 8678: six int32 in a one-element array.</summary>
    public static byte[] BuildMatchProgress(int instanceId, int a = 0, int b = 0, int c = 0, int d = 1, int e = 0)
        => OneElementArray(S_MATCH_PROGRESS, instanceId, a, b, c, d, e);

    /// <summary>S_DEL_INTER_PARTY_MATCH_POOL, record 9489.</summary>
    public static byte[] BuildDelPool(int instanceId)
        => OneElementArray(S_DEL_INTER_PARTY_MATCH_POOL, instanceId, 0, 0);

    /// <summary>S_MATCH_ROOM_DELETE, record 7337 - one room id per frame.</summary>
    public static byte[] BuildMatchRoomDelete(int roomId)
        => OneElementArray(S_MATCH_ROOM_DELETE, roomId, 0);

    /// <summary>S_CANCEL_PARTY_MATCH_POOL, record 53967: two bare int32, no array.</summary>
    public static byte[] BuildCancelPartyMatchPool(int instanceId = AnyInstance, int reason = 2)
    {
        var p = new byte[12];
        BitConverter.GetBytes((ushort)12).CopyTo(p, 0);
        BitConverter.GetBytes(S_CANCEL_PARTY_MATCH_POOL).CopyTo(p, 2);
        BitConverter.GetBytes(instanceId).CopyTo(p, 4);
        BitConverter.GetBytes(reason).CopyTo(p, 8);
        return p;
    }

    /// <summary>
    /// S_FIN_INTER_PARTY_MATCH, record 10585: three bare int32 and NO array. The shipped
    /// <c>.1.def</c> declares only <c>int32 zone</c> and under-declares the other two, and the
    /// field is the matched INSTANCE id (9739 = Kelsaik here, 9075 and 10 in classic_live2), not
    /// a zone.
    /// </summary>
    public static byte[] BuildFinInterPartyMatch(int instanceId, int unk1 = 0, int unk2 = 0)
    {
        var p = new byte[16];
        BitConverter.GetBytes((ushort)16).CopyTo(p, 0);
        BitConverter.GetBytes(S_FIN_INTER_PARTY_MATCH).CopyTo(p, 2);
        BitConverter.GetBytes(instanceId).CopyTo(p, 4);
        BitConverter.GetBytes(unk1).CopyTo(p, 8);
        BitConverter.GetBytes(unk2).CopyTo(p, 12);
        return p;
    }

    /// <summary>
    /// S_CHANGE_EVENT_MATCHING_STATE, records 8664 (queued) and 9487 (not).
    /// <code>
    ///   body 0  u16 count quests / 2 u16 offset quests
    ///        4  byte queued          1 while the party sits in the pool, 0 once it leaves
    ///        5  byte unk             1 in every captured frame
    ///   element 8 B: u16 here / u16 next / i32 questId
    /// </code>
    /// <para>The 730 B and 1290 B frames at records 10580/10581 are the same layout with 90 and
    /// 160 quest ids - the full vanguard roster, pushed when the state changes for real.</para>
    /// </summary>
    public static byte[] BuildChangeEventMatchingState(IReadOnlyList<int> questIds, bool queued, byte unk = 1)
    {
        var q = questIds ?? Array.Empty<int>();
        const int Header = 4, Stride = 8;
        int len = Header + 6 + q.Count * Stride;
        var p = new byte[len];
        BitConverter.GetBytes((ushort)len).CopyTo(p, 0);
        BitConverter.GetBytes(S_CHANGE_EVENT_MATCHING_STATE).CopyTo(p, 2);
        int first = q.Count == 0 ? 0 : Header + 6;
        BitConverter.GetBytes((ushort)q.Count).CopyTo(p, 4);
        BitConverter.GetBytes((ushort)first).CopyTo(p, 6);
        p[8] = (byte)(queued ? 1 : 0);
        p[9] = unk;
        int off = first;
        for (int i = 0; i < q.Count; i++)
        {
            int next = i + 1 < q.Count ? off + Stride : 0;
            BitConverter.GetBytes((ushort)off).CopyTo(p, off);
            BitConverter.GetBytes((ushort)next).CopyTo(p, off + 2);
            BitConverter.GetBytes(q[i]).CopyTo(p, off + 4);
            off = next;
        }
        return p;
    }

    /// <summary>One player inside S_ADD_INTER_PARTY_MATCH_POOL. 17 B with its element header.</summary>
    public readonly record struct PoolPlayer(int PlanetId, int PlayerId, byte Flag, int Tail);

    /// <summary>
    /// S_ADD_INTER_PARTY_MATCH_POOL, records 8662 and 10333. THIS IS THE LAYOUT THE SHIPPED
    /// <c>.1.def</c> GETS WRONG - T134 flagged it, and the leader-side capture settles it:
    /// <code>
    ///   body 0  u16 count instances / 2 u16 offset instances
    ///        4  u16 count (second array, 0 in both captured frames) / 6 u16 offset
    ///   instance element 20 B:
    ///        +0  u16 here / +2 u16 next
    ///        +4  u16 count players / +6 u16 offset players      &lt;-- a NESTED array
    ///        +8  i32 instanceId / +12 i32 / +16 i32
    ///   player element 17 B:
    ///        +0  u16 here / +2 u16 next
    ///        +4  i32 planetId (2800) / +8 i32 playerId / +12 byte / +13 i32
    /// </code>
    /// <para>The def has a flat <c>players</c> array beside <c>instances</c> and two leading
    /// int32 scalars; decoding record 9335 with it yields <c>type = 2097154</c>, which is the
    /// nested count/offset pair being read as an int32. Do not use that def.</para>
    /// </summary>
    public static byte[] BuildAddInterPartyMatchPool(int instanceId, IReadOnlyList<PoolPlayer> players,
        int unk1 = 0, int unk2 = 0)
    {
        var ps = players ?? Array.Empty<PoolPlayer>();
        const int Header = 4, InstanceStride = 20, PlayerStride = 17;
        int len = Header + 8 + InstanceStride + ps.Count * PlayerStride;
        var p = new byte[len];
        BitConverter.GetBytes((ushort)len).CopyTo(p, 0);
        BitConverter.GetBytes(S_ADD_INTER_PARTY_MATCH_POOL).CopyTo(p, 2);

        int inst = Header + 8;
        BitConverter.GetBytes((ushort)1).CopyTo(p, 4);
        BitConverter.GetBytes((ushort)inst).CopyTo(p, 6);
        BitConverter.GetBytes((ushort)0).CopyTo(p, 8);
        BitConverter.GetBytes((ushort)0).CopyTo(p, 10);

        int pfirst = inst + InstanceStride;
        BitConverter.GetBytes((ushort)inst).CopyTo(p, inst);
        BitConverter.GetBytes((ushort)0).CopyTo(p, inst + 2);
        BitConverter.GetBytes((ushort)ps.Count).CopyTo(p, inst + 4);
        BitConverter.GetBytes((ushort)(ps.Count == 0 ? 0 : pfirst)).CopyTo(p, inst + 6);
        BitConverter.GetBytes(instanceId).CopyTo(p, inst + 8);
        BitConverter.GetBytes(unk1).CopyTo(p, inst + 12);
        BitConverter.GetBytes(unk2).CopyTo(p, inst + 16);

        int off = pfirst;
        for (int i = 0; i < ps.Count; i++)
        {
            int next = i + 1 < ps.Count ? off + PlayerStride : 0;
            BitConverter.GetBytes((ushort)off).CopyTo(p, off);
            BitConverter.GetBytes((ushort)next).CopyTo(p, off + 2);
            BitConverter.GetBytes(ps[i].PlanetId).CopyTo(p, off + 4);
            BitConverter.GetBytes(ps[i].PlayerId).CopyTo(p, off + 8);
            p[off + 12] = ps[i].Flag;
            BitConverter.GetBytes(ps[i].Tail).CopyTo(p, off + 13);
            off = next;
        }
        return p;
    }
}
