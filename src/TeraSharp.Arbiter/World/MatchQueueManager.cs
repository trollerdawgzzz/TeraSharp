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
// T138c REPLACED "WHAT THIS DOES NOT DO". There is still no MatchServer, but there is now a
// pool and a formation pass here, in MatchComposition + the T138c region at the bottom of this
// file. Strangers ARE put together; see that region's header for the rules.
//
// AND THE HAND-OFF TURNED OUT NOT TO BE OURS. classic_live3 settles what follows FIN:
//
//   10585  S->C  S_FIN_INTER_PARTY_MATCH   instance 9739
//   10586  S->C  S_PRIVATE_CHAT
//   10587  S->C  S_SYS_PARTY_INFO (488 B)  the matched party
//   10588+ S->C  S_CHANGE_RELATION x14, S_HIDE_HP x9
//   11521  C->S  C_ENTER_DUNGEON (0x9A6C, 8 B: i32 9739)   <-- the PLAYER presses enter
//   12091  S->C  S_LOAD_TOPO
//
// so a match does NOT teleport anybody. It closes the matching window, forms a party, and the
// client then walks in through the ordinary path: C_ENTER_DUNGEON forwards to World, World
// raises SA_REQUEST_ENTER_DUNGEON (0x13BE) and WorldInstances routes it exactly as it does for
// someone who walked to the portal. There is no A->W "put this player in an instance" frame to
// send: AS_FORCE_ENTER_DUNGEON_ID (0x1390) sounds like one and is not - Handler_AS_FORCE_ENTER_
// DUNGEON_ID (WorldServer.exe.c:2987089) reads [i32 playerId][i32 dungeonId] and does nothing
// but store the second into User+0xB528. So the Arbiter's whole job after formation is FIN.
//
// STILL NOT DONE: the PARTY. The real server also sends S_SYS_PARTY_INFO and the relation
// frames above, so five matched strangers arrive as a party; ours arrive as five soloists who
// each walk in and get their own channel. PartyManager has the machinery (JoinCore /
// AS_DO_CREATE_PARTY) but no public "form this party" seam, and PartyWiring.Dispatcher is what
// would carry the actions. That seam is the next task, not this one.
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

    /// <summary>One queued player, with what the matcher needs to slot them. The class and
    /// level come from <c>characters</c>, NOT from C_MATCH_ADD - see MatchComposition's
    /// header for the four captures that prove the packet carries no class.</summary>
    /// <param name="PlayerId">The session's player id, the FIN recipient.</param>
    /// <param name="CharacterId">The <c>characters.id</c> behind it, 0 if unknown.</param>
    /// <param name="CharacterClass">Warrior 0 .. Glaiver 12.</param>
    /// <param name="Level">For the level-gated second positions.</param>
    public readonly record struct Queuer(uint PlayerId, int CharacterId, int CharacterClass, int Level)
    {
        /// <summary>The position this class is slotted as unless something else is needed.</summary>
        public MatchRole Role => MatchComposition.RoleOf(CharacterClass);
    }

    /// <summary>Where a queue entry is. There is no "cancelled": cancelling removes the row.</summary>
    public enum MatchState
    {
        /// <summary>In the pool, waiting for a group to form around it.</summary>
        Waiting = 0,
        /// <summary>Formed. FIN has gone out; the client's window is closed.</summary>
        Matched = 1,
        /// <summary>Waited past <see cref="QueueTimeout"/> without forming.</summary>
        TimedOut = 2,
    }

    /// <summary>One party's queue entry. RAM only - a restart empties every queue, which is the
    /// same thing the real stack does when the match server restarts.</summary>
    public sealed class Entry
    {
        public uint LeaderPlayerId { get; init; }
        public int[] InstanceIds { get; init; } = Array.Empty<int>();
        public uint[] MemberPlayerIds { get; init; } = Array.Empty<uint>();
        public DateTimeOffset QueuedAt { get; init; }
        public bool Matched { get; set; }

        /// <summary>T138c: the members with their roles. Empty on an entry added through the
        /// old <see cref="Add(uint,int[],uint[],DateTimeOffset,out bool)"/> overload, which is
        /// why <see cref="Members"/> is never assumed non-empty by the formation pass.</summary>
        public Queuer[] Members { get; init; } = Array.Empty<Queuer>();

        /// <summary>T138c: Waiting until a group forms around it.</summary>
        public MatchState State { get; set; } = MatchState.Waiting;

        /// <summary>The instance this entry was finally matched into, or 0.</summary>
        public int MatchedInstanceId { get; set; }

        /// <summary>True when the entry queued more than one body - a party, not a soloist.</summary>
        public bool IsParty => Members.Length > 1 || MemberPlayerIds.Length > 1;

        /// <summary>How many bodies this entry brings.</summary>
        public int Size => Members.Length > 0 ? Members.Length : MemberPlayerIds.Length;

        /// <summary>True when this entry asked for <paramref name="instanceId"/>.</summary>
        public bool Wants(int instanceId)
        {
            foreach (int id in InstanceIds) if (id == instanceId) return true;
            return false;
        }
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

    /// <summary>
    /// C_MATCH_ADD's second array, which is NOT a member list. All four captured frames carry
    /// two elements with the SAME first int32 - the queuing player's own id (4742 in
    /// classic_live3, echoed as the pool player's id in record 8662; 1 in cap_multiworld; 1003
    /// in cap_social4) - and a second int32 that is 1 on the first queue and 0 on the second.
    /// The server echoes that second value into S_ADD_INTER_PARTY_MATCH_POOL's player tail
    /// (record 8662 tail 1, record 10333 tail 0), so it is read here and echoed rather than
    /// named. Layout: <c>[u16 count][u16 offset]</c> at body 4, elements
    /// <c>[u16 here][u16 next][i32 playerId][i32 flag]</c>.
    /// </summary>
    public static int ReadQueueFlag(ReadOnlyMemory<byte> body)
    {
        var b = body.Span;
        if (b.Length < 8) return 0;
        int count = BitConverter.ToUInt16(b.Slice(4, 2));
        int off = BitConverter.ToUInt16(b.Slice(6, 2)) - 4;
        if (count <= 0 || off < 0 || off + 12 > b.Length) return 0;
        return BitConverter.ToInt32(b.Slice(off + 8, 4));
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

    public const ushort S_MATCH_ROOM_LIST = 0x68E0;

    /// <summary>
    /// S_MATCH_ROOM_LIST. classic_live3 record 8838 is the ten-room form, 456 B:
    /// <code>
    ///   body 0  u16 count rooms / 2 u16 offset rooms
    ///        4  i32 unk1   (1 in the capture)
    ///        8  i32 unk2   (2 in the capture)
    ///   element 44 B, starting at packet 16
    /// </code>
    /// <para>The row stride and its id fields are clear - room id, a dungeon id, a timestamp -
    /// but three trailing int32 flags are not, and ten rows from one capture is not enough to
    /// name them (see status/MULTIWORLD-DESIGN.md). So only the EMPTY form is built here, which
    /// is all TeraSharp can honestly answer: we have no rooms. A browse window with no rows is a
    /// true statement; a row with guessed flags is not.</para>
    /// </summary>
    public static byte[] BuildEmptyMatchRoomList(int unk1 = 1, int unk2 = 2)
    {
        var p = new byte[16];
        BitConverter.GetBytes((ushort)16).CopyTo(p, 0);
        BitConverter.GetBytes(S_MATCH_ROOM_LIST).CopyTo(p, 2);
        BitConverter.GetBytes((ushort)0).CopyTo(p, 4);
        BitConverter.GetBytes((ushort)0).CopyTo(p, 6);
        BitConverter.GetBytes(unk1).CopyTo(p, 8);
        BitConverter.GetBytes(unk2).CopyTo(p, 12);
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

    // =========================================================================================
    // T138c - the pool, the roles and the formation pass.
    //
    // ONE POOL PER INSTANCE ID, and the id says which kind. Every id in BattleFieldData.xml is
    // under 1000 (5, 10, 11, 26-30, 37-40, 46, 47, 70, 71, 110, 118, 119, 156, 909) and every
    // id in DungeonMatching.xml is 9000-plus, so <see cref="IsBattleground"/> is a range test
    // and not a guess. classic_live2 confirms both share the queue: its member-side
    // S_FIN_INTER_PARTY_MATCH frames carry 9075 (Kelsaik's Nest) and 10 (Fraywind Canyon).
    //
    // DUNGEONS. <see cref="TryForm"/> walks the pool in QUEUE ORDER and takes whole entries:
    // a party that queued together is never split, which is what "party-queued groups keep
    // slots and fill from solo queuers" means - the party seeds the group and the leftover
    // slots are filled from whoever queued next. Each member is slotted in their default
    // position when one is free and in any position MatchComposition.CanFill allows otherwise,
    // least-flexible member first, so a Lancer never loses the tank slot to a Warrior who
    // could have DPSed. An entry that cannot be slotted whole is SKIPPED, not rejected: it
    // stays in the pool for the next pass.
    //
    // BATTLEGROUNDS. Same walk, but two teams, and the side is chosen at random among the
    // sides that will still accept the entry - "random fill in queue order". The per-team caps
    // are MatchComposition's; the FLOORS (Corsairs' three healers, Fraywind's two) are checked
    // once both teams are full, and a pair of teams that misses one is not started.
    //
    // NO MMR. Nothing here reads a rating, by explicit instruction: bg_rating is a leaderboard
    // number (see BattlegroundRating), not a matchmaker input.
    // =========================================================================================

    /// <summary>
    /// How long an entry sits before <see cref="SweepTimeouts"/> calls it timed out.
    /// <c>BattleFieldData.xml</c>'s <c>&lt;MatchingTimeDisplay standardTime="600"&gt;</c> is the
    /// number the client's own matching window counts to, so it is the one used here.
    /// </summary>
    public const int QueueTimeoutSeconds = 600;

    /// <summary><see cref="QueueTimeoutSeconds"/> as a span.</summary>
    public static TimeSpan QueueTimeout => TimeSpan.FromSeconds(QueueTimeoutSeconds);

    /// <summary>
    /// True for a battleground id. Every BattleFieldData.xml id is under 1000 and every
    /// DungeonMatching.xml id is 9000-plus, so the boundary is wide on both sides.
    /// </summary>
    public static bool IsBattleground(int instanceId) => instanceId > 0 && instanceId < 1000;

    /// <summary>
    /// Members a formed group wants, per instance. Dungeons are a party unless an entry here
    /// says otherwise: <c>DungeonMatching.xml</c>'s <c>&lt;Dungeon&gt;</c> rows carry id, name,
    /// levels, minItemLevel and matchingRoleId and NO member count, and the table
    /// <c>matchingRoleId</c> indexes lives in the MatchServer binary this stack does not have.
    /// So a raid size is configured rather than decoded, and
    /// <see cref="MatchComposition.DungeonTemplate"/> turns whatever is put here into counts -
    /// 10 becomes 2 tanks / 2 healers / 6 DPS, 20 becomes 4 / 4 / 12.
    /// </summary>
    public static readonly Dictionary<int, int> RaidSizes = new();

    /// <summary>The group size for an instance - <see cref="RaidSizes"/>, else a party.</summary>
    public static int GroupSize(int instanceId)
        => RaidSizes.TryGetValue(instanceId, out int n) && n > 0 ? n : MatchComposition.PartySize;

    /// <summary>
    /// C_MATCH_ADD with the roles resolved. Keeps <see cref="Entry.MemberPlayerIds"/> in step
    /// so <see cref="Progress"/> and the pool-add frame carry on working unchanged.
    /// </summary>
    public static Entry Add(uint leaderPlayerId, int[] instanceIds, IReadOnlyList<Queuer> members,
        DateTimeOffset now)
    {
        var ms = members == null ? Array.Empty<Queuer>() : new Queuer[members.Count];
        var ids = new uint[ms.Length];
        for (int i = 0; i < ms.Length; i++) { ms[i] = members![i]; ids[i] = members[i].PlayerId; }
        var e = new Entry
        {
            LeaderPlayerId = leaderPlayerId,
            InstanceIds = instanceIds ?? Array.Empty<int>(),
            MemberPlayerIds = ids,
            Members = ms,
            QueuedAt = now,
        };
        lock (Lock) ByLeader[leaderPlayerId] = e;
        return e;
    }

    /// <summary>
    /// Drop whatever entry <paramref name="playerId"/> is in, whether they lead it or sit in
    /// it. This is the disconnect path: a member who drops takes the party's entry with them,
    /// because a party of four queued as five is not the group anyone asked for.
    /// </summary>
    public static bool RemoveByPlayer(uint playerId)
    {
        lock (Lock)
        {
            if (ByLeader.Remove(playerId)) return true;
            foreach (var kv in ByLeader)
            {
                foreach (uint id in kv.Value.MemberPlayerIds)
                    if (id == playerId) { ByLeader.Remove(kv.Key); return true; }
            }
            return false;
        }
    }

    /// <summary>Every waiting entry that asked for <paramref name="instanceId"/>, queue order.</summary>
    public static List<Entry> Pool(int instanceId)
    {
        var outp = new List<Entry>();
        lock (Lock)
            foreach (var e in ByLeader.Values)
                if (e.State == MatchState.Waiting && e.Wants(instanceId)) outp.Add(e);
        outp.Sort((a, b) => a.QueuedAt.CompareTo(b.QueuedAt));
        return outp;
    }

    /// <summary>Every instance id anything is waiting on, so a tick can try each in turn.</summary>
    public static List<int> WaitingInstanceIds()
    {
        var seen = new List<int>();
        lock (Lock)
            foreach (var e in ByLeader.Values)
            {
                if (e.State != MatchState.Waiting) continue;
                foreach (int id in e.InstanceIds) if (!seen.Contains(id)) seen.Add(id);
            }
        return seen;
    }

    /// <summary>
    /// A group the pass put together. <see cref="TeamB"/> is empty for a dungeon and is the
    /// second side for a battleground; <see cref="Members"/> is everybody either way, which is
    /// the FIN recipient list.
    /// </summary>
    public sealed record FormedGroup(
        int InstanceId,
        IReadOnlyList<Entry> Entries,
        IReadOnlyList<Queuer> Members,
        IReadOnlyList<Queuer> TeamA,
        IReadOnlyList<Queuer> TeamB);

    /// <summary>How many of the three positions a class can be slotted into at this level.</summary>
    private static int Flexibility(in Queuer q)
    {
        int n = 0;
        for (int r = 0; r < 3; r++)
            if (MatchComposition.CanFill(q.CharacterClass, (MatchRole)r, q.Level)) n++;
        return n;
    }

    /// <summary>
    /// Try to seat every member of one entry in <paramref name="free"/> (indexed by
    /// <see cref="MatchRole"/>). <paramref name="free"/> is only decremented when the WHOLE
    /// entry fits, so a half-seated party never leaves the pool short.
    /// </summary>
    private static bool TrySeat(IReadOnlyList<Queuer> members, int[] free)
    {
        var order = new List<Queuer>(members);
        order.Sort((a, b) => Flexibility(a).CompareTo(Flexibility(b)));
        var taken = new int[3];
        foreach (var m in order)
        {
            int slot = -1;
            int def = (int)m.Role;
            if (free[def] - taken[def] > 0) slot = def;
            else
                for (int r = 0; r < 3; r++)
                    if (free[r] - taken[r] > 0
                        && MatchComposition.CanFill(m.CharacterClass, (MatchRole)r, m.Level))
                    { slot = r; break; }
            if (slot < 0) return false;
            taken[slot]++;
        }
        for (int r = 0; r < 3; r++) free[r] -= taken[r];
        return true;
    }

    /// <summary>
    /// One formation pass over one instance's pool. Returns the group and marks its entries
    /// <see cref="MatchState.Matched"/>, or null when nothing full could be built - in which
    /// case NOTHING is changed and every entry stays where it was.
    /// </summary>
    public static FormedGroup? TryForm(int instanceId, DateTimeOffset now, Random? rng = null)
        => IsBattleground(instanceId)
            ? TryFormBattleground(instanceId, now, rng)
            : TryFormDungeon(instanceId, GroupSize(instanceId), now);

    /// <summary>1 tank / 1 healer / 3 DPS, or the raid counts for a larger group.</summary>
    public static FormedGroup? TryFormDungeon(int instanceId, int size, DateTimeOffset now)
    {
        var template = MatchComposition.DungeonTemplate(size);
        if (template.Size <= 0) return null;
        var pool = Pool(instanceId);
        if (pool.Count == 0) return null;

        var free = new[] { template.Tanks, template.Dps, template.Healers };
        var taken = new List<Entry>();
        var members = new List<Queuer>();
        foreach (var e in pool)
        {
            if (e.Members.Length == 0) continue;           // legacy entry, no roles to seat
            if (members.Count + e.Members.Length > template.Size) continue;
            if (!TrySeat(e.Members, free)) continue;
            taken.Add(e);
            members.AddRange(e.Members);
            if (members.Count == template.Size) break;
        }
        if (members.Count != template.Size) return null;

        Mark(taken, instanceId);
        return new FormedGroup(instanceId, taken, members, members, Array.Empty<Queuer>());
    }

    /// <summary>
    /// Two teams, random side in queue order, per-team caps from MatchComposition and the
    /// floors checked once both sides are full. A battleground with no row of its own has no
    /// size to fill, so it never forms here - it is a queue we keep, not a match we invent.
    /// </summary>
    public static FormedGroup? TryFormBattleground(int battleFieldId, DateTimeOffset now,
        Random? rng = null)
    {
        var rule = MatchComposition.RuleFor(battleFieldId);
        if (rule.TeamSize <= 0) return null;
        var pool = Pool(battleFieldId);
        if (pool.Count == 0) return null;
        rng ??= Random.Shared;

        var teams = new[] { new List<Queuer>(), new List<Queuer>() };
        var taken = new List<Entry>();
        foreach (var e in pool)
        {
            if (e.Members.Length == 0) continue;
            int first = rng.Next(2);
            for (int t = 0; t < 2; t++)
            {
                var team = teams[(first + t) & 1];
                if (!TeamTakes(rule, team, e.Members)) continue;
                team.AddRange(e.Members);
                taken.Add(e);
                break;
            }
            if (teams[0].Count == rule.TeamSize && teams[1].Count == rule.TeamSize) break;
        }
        if (teams[0].Count != rule.TeamSize || teams[1].Count != rule.TeamSize) return null;
        if (!Satisfies(rule, teams[0]) || !Satisfies(rule, teams[1])) return null;

        var all = new List<Queuer>(teams[0]);
        all.AddRange(teams[1]);
        Mark(taken, battleFieldId);
        return new FormedGroup(battleFieldId, taken, all, teams[0], teams[1]);
    }

    /// <summary>Whether a whole entry still fits one side's caps, counted as it is added.</summary>
    private static bool TeamTakes(in BattlegroundRule rule, List<Queuer> team,
                                  IReadOnlyList<Queuer> incoming)
    {
        if (team.Count + incoming.Count > rule.TeamSize) return false;
        int healers = 0, tanks = 0, lancers = 0;
        var perClass = new int[MatchComposition.ClassCount];
        foreach (var q in team) Count(q, ref healers, ref tanks, ref lancers, perClass);
        foreach (var q in incoming)
        {
            int same = (uint)q.CharacterClass < perClass.Length ? perClass[q.CharacterClass] : 0;
            if (!MatchComposition.TeamAccepts(rule, q.CharacterClass, healers, tanks, lancers, same))
                return false;
            Count(q, ref healers, ref tanks, ref lancers, perClass);
        }
        return true;
    }

    private static void Count(in Queuer q, ref int healers, ref int tanks, ref int lancers,
                              int[] perClass)
    {
        var role = MatchComposition.RoleOf(q.CharacterClass);
        if (role == MatchRole.Healer) healers++;
        if (role == MatchRole.Tank) tanks++;
        if (q.CharacterClass == MatchComposition.ClassLancer) lancers++;
        if ((uint)q.CharacterClass < perClass.Length) perClass[q.CharacterClass]++;
    }

    private static bool Satisfies(in BattlegroundRule rule, List<Queuer> team)
    {
        var classes = new List<int>(team.Count);
        foreach (var q in team) classes.Add(q.CharacterClass);
        return MatchComposition.TeamSatisfies(rule, classes);
    }

    private static void Mark(List<Entry> entries, int instanceId)
    {
        lock (Lock)
            foreach (var e in entries)
            {
                e.State = MatchState.Matched;
                e.Matched = true;
                e.MatchedInstanceId = instanceId;
            }
    }

    /// <summary>
    /// Move every entry that has waited past <see cref="QueueTimeout"/> to
    /// <see cref="MatchState.TimedOut"/> and hand them back. They are LEFT in the dictionary:
    /// the caller has to tell each leader before dropping the row, and a silently vanished
    /// queue leaves the client's matching window spinning forever.
    /// </summary>
    public static List<Entry> SweepTimeouts(DateTimeOffset now)
    {
        var expired = new List<Entry>();
        lock (Lock)
            foreach (var e in ByLeader.Values)
                if (e.State == MatchState.Waiting && now - e.QueuedAt >= QueueTimeout)
                {
                    e.State = MatchState.TimedOut;
                    expired.Add(e);
                }
        return expired;
    }

}
