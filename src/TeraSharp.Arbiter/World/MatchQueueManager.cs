// SPDX-License-Identifier: MIT
// Copyright (c) 2026 the TeraSharp contributors

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
    /// <param name="Chosen">T138f: the position this player picked in the matching window, as
    /// C_MATCH_ADD's trailing int32 carries it (0 tank, 1 DPS, 2 healer).
    /// <see cref="MatchComposition.NoChoice"/> when none was stated.</param>
    public readonly record struct Queuer(uint PlayerId, int CharacterId, int CharacterClass, int Level,
                                         int Chosen = MatchComposition.NoChoice, float TrueItemLevel = 0)
    {
        /// <summary>The position this queuer is treated as when nothing is matching on it -
        /// their own choice when they can fill it, else the class default.</summary>
        public MatchRole Role => MatchComposition.EffectiveRole(CharacterClass, Level, Chosen);

        /// <summary>The position they asked for, or null.</summary>
        public MatchRole? ChosenRole => MatchComposition.ChosenRole(Chosen);
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
        public int[] InstanceIds { get; set; } = Array.Empty<int>();
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

    /// <summary>T184: progress resolves a party application for any member (Arb077:9059-9116).</summary>
    public static Entry? FindForPlayer(uint playerId)
    {
        lock (Lock)
            return ByLeader.TryGetValue(playerId, out var e) ? e
                : ByLeader.Values.FirstOrDefault(row => row.MemberPlayerIds.Contains(playerId));
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

    /// <summary>T184: Arb_part_077:8929-8955 writes tanker/dealer/healer counts.
    /// Find a compatible partial group containing the requesting entry; the capture's
    /// single DPS is (0,1,0), and the same player queued as tank is (1,0,0).</summary>
    public static byte[]? ProgressFrame(Entry? entry, int askedFor)
    {
        if (entry == null || entry.State != MatchState.Waiting) return null;
        int id = askedFor == AnyInstance ? entry.InstanceIds.FirstOrDefault() : askedFor;
        if (!entry.Wants(id)) return null;
        if (QaMatchSimulation.Progress(id, IsBattleground(id)) is { } simulated) return simulated;
        // The in-process battleground matcher has no MA_MATCH_PROGRESS cache. Report
        // its own application's roles; do not seat a battleground into five dungeon slots.
        if (IsBattleground(id))
            return BuildMatchProgress(id, 1, 0, entry.Members.Count(q => q.Role == MatchRole.Tank),
                entry.Members.Count(q => q.Role == MatchRole.Dps), entry.Members.Count(q => q.Role == MatchRole.Healer));
        var candidates = Pool(id);
        candidates.Remove(entry);
        candidates.Insert(0, entry);
        var rule = DungeonMatchRules.For(id);
        var templates = rule?.Templates() ?? new[] { MatchComposition.DungeonTemplate(GroupSize(id)) };
        var best = Array.Empty<MatchRole>();
        foreach (var template in templates)
        {
            if (candidates.All(e => e.Members.All(q => q.ChosenRole != null)))
            {
                var selected = FixedRoleSubset(candidates, template, entry);
                var assigned = selected.SelectMany(e => e.Members).Select(q => q.Role).ToArray();
                if (assigned.Length > best.Length) best = assigned;
                continue;
            }
            var members = new List<Queuer>(entry.Members);
            if (!TryAssign(members, template)) continue;
            foreach (var e in candidates.Skip(1))
            {
                int before = members.Count;
                members.AddRange(e.Members);
                if (!TryAssign(members, template)) members.RemoveRange(before, members.Count - before);
            }
            if (members.Count <= best.Length) continue;
            var roles = new MatchRole[members.Count];
            if (TryAssign(members, template, roles)) best = roles;
        }
        return BuildMatchProgress(id, IsBattleground(id) ? 1 : 0, 0,
            best.Count(r => r == MatchRole.Tank), best.Count(r => r == MatchRole.Dps),
            best.Count(r => r == MatchRole.Healer));
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
    /// C_MATCH_ADD's second array, which is NOT a member list: all four captured frames carry two
    /// elements with the SAME first int32, the queuing player's own id (4742 in classic_live3,
    /// echoed as the pool player's id in record 8662; 1 in cap_multiworld; 1003 in cap_social4).
    /// The second int32 is the POSITION they picked - see MatchComposition's ChoiceToRole, which
    /// has the four samples and the one that proves it. Layout: <c>[u16 count][u16 offset]</c> at
    /// body 4, elements <c>[u16 here][u16 next][i32 playerId][i32 position]</c>.
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

    /// <summary>
    /// Every <c>(playerId, position)</c> pair in that array. The captures only ever show one
    /// player - twice - so a party queue's shape is unpinned; reading it as a list keyed by
    /// player id is what makes the leader's own choice land whatever the rest turns out to be.
    /// </summary>
    public static List<(int PlayerId, int Choice)> ReadQueueChoices(ReadOnlyMemory<byte> body)
    {
        var list = new List<(int, int)>();
        var b = body.Span;
        if (b.Length < 8) return list;
        int count = BitConverter.ToUInt16(b.Slice(4, 2));
        int off = BitConverter.ToUInt16(b.Slice(6, 2)) - 4;
        for (int i = 0; i < count && off >= 0 && off + 12 <= b.Length; i++)
        {
            int next = BitConverter.ToUInt16(b.Slice(off + 2, 2)) - 4;
            list.Add((BitConverter.ToInt32(b.Slice(off + 4, 4)),
                      BitConverter.ToInt32(b.Slice(off + 8, 4))));
            if (next < 0) break;
            off = next;
        }
        return list;
    }

    /// <summary>
    /// Stamp each queuer with the position they asked for, matched by player id. A member the
    /// array does not name keeps <see cref="MatchComposition.NoChoice"/> and is seated on their
    /// class alone.
    /// </summary>
    public static List<Queuer> WithChoices(IReadOnlyList<Queuer> members,
        IReadOnlyList<(int PlayerId, int Choice)> choices)
    {
        var outp = new List<Queuer>(members?.Count ?? 0);
        if (members == null) return outp;
        foreach (var m in members)
        {
            int pick = MatchComposition.NoChoice;
            if (choices != null)
                foreach (var (id, choice) in choices)
                    if (id == (int)m.PlayerId) { pick = choice; break; }
            outp.Add(m with { Chosen = pick });
        }
        return outp;
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
    /// <summary>
    /// T161b. The frames the real Arbiter sends for a change of matching state on
    /// <paramref name="instances"/> (WorldOfPartyMatchHelper::SendChangeEventMatchingState, and
    /// DoAddToUserPool for the queued one): the EventMatching.xml events whose TargetList holds
    /// one of them - dungeon events in the flag-1 frame, battleground events in the flag-0 frame -
    /// and a frame ONLY when its list is not empty. classic_live3 10335 is 9739's dungeon events
    /// (2154 and 92151 in our sheet; its third, 800029, is a newer datacenter's).
    /// </summary>
    public static List<byte[]> EventMatchingFrames(IEnumerable<int>? instances, bool queued)
    {
        var table = DatasheetLoader.EventMatchingTargets.Value;
        var dungeon = new List<int>();
        var battle = new List<int>();
        foreach (int i in instances ?? Array.Empty<int>())
            if (table.TryGetValue(i, out var ev)) { dungeon.AddRange(ev.Dungeon); battle.AddRange(ev.BattleField); }
        var outp = new List<byte[]>(3);
        if (dungeon.Count > 0) outp.Add(BuildChangeEventMatchingState(dungeon, queued, 1));
        if (battle.Count > 0) outp.Add(BuildChangeEventMatchingState(battle, queued, 0));
        return outp;
    }

    /// <summary>T184: DoFinPartyMatch expands MatchingInfo(-9999,2,2), visiting every
    /// destination in key order for free=0 and free=1. Do not deduplicate the two passes.
    /// Arb_part_079:3245-3249,7274-7326; classic_live3 10580/10581.</summary>
    public static List<byte[]> AllEventMatchingFrames()
    {
        var targets = DatasheetLoader.EventMatchingTargets.Value;
        var dungeon = new List<int>();
        var battle = new List<int>();
        for (int free = 0; free < 2; free++)
            foreach (int id in targets.Keys.OrderBy(id => id))
            {
                dungeon.AddRange(targets[id].Dungeon);
                battle.AddRange(targets[id].BattleField);
            }
        var frames = new List<byte[]>(2);
        if (dungeon.Count > 0) frames.Add(BuildChangeEventMatchingState(dungeon, false, 1));
        if (battle.Count > 0) frames.Add(BuildChangeEventMatchingState(battle, false, 0));
        return frames;
    }

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
    ///        4  i32 RemainSec (T184: Arb076:14950-15035)
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
        => BuildAddInterPartyMatchPool(new[] { instanceId }, players, unk1, unk2);

    /// <summary>T184: full+8 is i32 RemainSec; each matching row owns a PDId-ordered
    /// member list. Arb_part_076:14950-15035; classic_live2 9335 and classic_live3 8662.</summary>
    public static byte[] BuildAddInterPartyMatchPool(IReadOnlyList<int> instances,
        IReadOnlyList<PoolPlayer> players, int matchingType = 0, int freeMatching = 0, int remainSec = 0)
    {
        var ps = (players ?? Array.Empty<PoolPlayer>()).OrderBy(p => p.PlanetId).ThenBy(p => p.PlayerId).ToArray();
        const int Header = 4, InstanceStride = 20, PlayerStride = 17;
        int stride = InstanceStride + ps.Length * PlayerStride;
        int len = Header + 8 + instances.Count * stride;
        if (len > ushort.MaxValue) throw new ArgumentOutOfRangeException(nameof(instances));
        var p = new byte[len];
        BitConverter.GetBytes((ushort)len).CopyTo(p, 0);
        BitConverter.GetBytes(S_ADD_INTER_PARTY_MATCH_POOL).CopyTo(p, 2);

        int inst = Header + 8;
        BitConverter.GetBytes((ushort)instances.Count).CopyTo(p, 4);
        BitConverter.GetBytes((ushort)(instances.Count == 0 ? 0 : inst)).CopyTo(p, 6);
        BitConverter.GetBytes(remainSec).CopyTo(p, 8);

        for (int row = 0; row < instances.Count; row++, inst += stride)
        {
            int pfirst = inst + InstanceStride;
            BitConverter.GetBytes((ushort)inst).CopyTo(p, inst);
            BitConverter.GetBytes((ushort)(row + 1 < instances.Count ? inst + stride : 0)).CopyTo(p, inst + 2);
            BitConverter.GetBytes((ushort)ps.Length).CopyTo(p, inst + 4);
            BitConverter.GetBytes((ushort)(ps.Length == 0 ? 0 : pfirst)).CopyTo(p, inst + 6);
            BitConverter.GetBytes(instances[row]).CopyTo(p, inst + 8);
            BitConverter.GetBytes(matchingType).CopyTo(p, inst + 12);
            BitConverter.GetBytes(freeMatching).CopyTo(p, inst + 16);

            int off = pfirst;
            for (int i = 0; i < ps.Length; i++)
            {
                int next = i + 1 < ps.Length ? off + PlayerStride : 0;
                BitConverter.GetBytes((ushort)off).CopyTo(p, off);
                BitConverter.GetBytes((ushort)next).CopyTo(p, off + 2);
                BitConverter.GetBytes(ps[i].PlanetId).CopyTo(p, off + 4);
                BitConverter.GetBytes(ps[i].PlayerId).CopyTo(p, off + 8);
                p[off + 12] = ps[i].Flag;
                BitConverter.GetBytes(ps[i].Tail).CopyTo(p, off + 13);
                off = next;
            }
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
    // slots are filled from whoever queued next. The SEATING is a bipartite matching over
    // everybody accepted so far (TryAssign), re-run as each entry is added, so an entry that
    // would make the group unseatable is SKIPPED and stays in the pool for the next pass. It
    // is a matching rather than a greedy because classic_live3 record 10587 has a Warrior in
    // the tank slot: positions have to be handed out with the whole group in view, or the
    // first Warrior through the door takes a DPS slot and the group never forms.
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
    /// Legacy per-instance size overrides, used only without a loaded DungeonMatchRules row.
    /// T184 loads production sizes and role bounds from MatchingRoleTemplate.xml.
    /// </summary>
    public static readonly Dictionary<int, int> RaidSizes = new();

    /// <summary>The production RoleData.totalUser; legacy overrides are used only without a sheet row.</summary>
    public static int GroupSize(int instanceId)
        => DungeonMatchRules.For(instanceId)?.Total
            ?? (RaidSizes.TryGetValue(instanceId, out int n) && n > 0 ? n : MatchComposition.PartySize);

    /// <summary>
    /// Retired T138d setting. Kept only to report stale configuration at startup. T184h:
    /// completion and advertised capacity both follow DungeonMatchRules.Total; an environment
    /// value cannot form a partial group or bypass role constraints (cap_2man versus queue5).
    /// </summary>
    public const string MinMembersVariable = "TERASHARP_MATCH_MIN_MEMBERS";

    /// <summary>Legacy configured value for diagnostics only; matchmaking ignores it.</summary>
    public static int MinMembersOverride()
    {
        var raw = TerasConfig.Get(MinMembersVariable);
        return !string.IsNullOrWhiteSpace(raw) && int.TryParse(raw.Trim(), out int v) && v > 0
            ? v : 0;
    }

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
    ///
    /// <para><see cref="Roles"/> is T138d, and it lines up with <see cref="Members"/> index for
    /// index: the position each member was actually SEATED in, which is not always their default
    /// one. classic_live3 record 10587 proves the difference matters - its slot 0 is player 4742,
    /// templateId 11001, an Elin Warrior (defaultPosition 1, DPS) sitting in the party as
    /// position 0, a TANK. S_SYS_PARTY_INFO carries these numbers, so they have to survive the
    /// formation pass rather than be recomputed from the class afterwards.</para>
    /// </summary>
    public sealed record FormedGroup(
        int InstanceId,
        IReadOnlyList<Entry> Entries,
        IReadOnlyList<Queuer> Members,
        IReadOnlyList<Queuer> TeamA,
        IReadOnlyList<Queuer> TeamB,
        IReadOnlyList<MatchRole> Roles);

    /// <summary>
    /// Seat <paramref name="members"/> into <paramref name="template"/>'s slots, or say it
    /// cannot be done. On success <paramref name="seats"/> - when given - says where each
    /// member went, IN <paramref name="members"/>' OWN ORDER.
    ///
    /// <para><b>T138e: this replaced a per-entry greedy, which was wrong.</b> The greedy took
    /// each queue entry in turn and gave each member their default position if one was free.
    /// classic_live3 record 10587 is the counter-example: a Warrior (defaultPosition DPS,
    /// secondPosition tank) is in the matched party AS THE TANK. The greedy would have put the
    /// first-queued Warrior in a DPS slot, and the fifth queuer - who can only DPS - would then
    /// have had nowhere to stand, so a group the real server formed would not form here.</para>
    ///
    /// <para>So the seating is a bipartite matching over the whole candidate set: members on one
    /// side, the template's slots on the other, an edge wherever
    /// <see cref="MatchComposition.CanFill"/> allows it, and Kuhn's augmenting path. Kuhn gets
    /// the Warrior case right for free and it also gets the reverse right: a Warrior who took
    /// the tank slot is pushed back out to DPS the moment a Lancer - who can fill nothing else -
    /// needs it. At five to thirty members and three slot kinds the cost is nothing.</para>
    /// </summary>
    private static bool TryAssign(IReadOnlyList<Queuer> members, in RoleTemplate template,
                                  MatchRole[]? seats = null)
    {
        int total = template.Size;
        if (members == null || members.Count > total) return false;
        if (members.Count == 0) return true;

        var slotRole = new MatchRole[total];
        int k = 0;
        for (int i = 0; i < template.Tanks; i++) slotRole[k++] = MatchRole.Tank;
        for (int i = 0; i < template.Healers; i++) slotRole[k++] = MatchRole.Healer;
        for (int i = 0; i < template.Dps; i++) slotRole[k++] = MatchRole.Dps;

        var slotOwner = new int[total];
        Array.Fill(slotOwner, -1);
        var memberSlot = new int[members.Count];
        Array.Fill(memberSlot, -1);
        var seen = new bool[total];

        for (int m = 0; m < members.Count; m++)
        {
            Array.Clear(seen, 0, seen.Length);
            if (!Augment(m, members, slotRole, slotOwner, memberSlot, seen)) return false;
        }
        if (seats != null)
            for (int m = 0; m < members.Count && m < seats.Length; m++)
                seats[m] = slotRole[memberSlot[m]];
        return true;
    }

    /// <summary>Kuhn's augmenting path: find member <paramref name="m"/> a slot, displacing
    /// whoever holds it if that one can move somewhere else.</summary>
    private static bool Augment(int m, IReadOnlyList<Queuer> members, MatchRole[] slotRole,
                                int[] slotOwner, int[] memberSlot, bool[] seen)
    {
        var q = members[m];
        for (int slot = 0; slot < slotRole.Length; slot++)
        {
            if (seen[slot]) continue;
            if (!MatchComposition.CanFill(q.CharacterClass, slotRole[slot], q.Level, q.Chosen)) continue;
            seen[slot] = true;
            if (slotOwner[slot] < 0
                || Augment(slotOwner[slot], members, slotRole, slotOwner, memberSlot, seen))
            {
                slotOwner[slot] = m;
                memberSlot[m] = slot;
                return true;
            }
        }
        return false;
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

    /// <summary>Complete a template permitted by the destination's MatchingRoleTemplate row.</summary>
    public static FormedGroup? TryFormDungeon(int instanceId, int size, DateTimeOffset now)
    {
        var rule = DungeonMatchRules.For(instanceId);
        if (rule == null)
            return TryFormDungeonTemplate(instanceId, MatchComposition.DungeonTemplate(size), now);
        foreach (var template in rule.Templates())
        {
            var group = TryFormDungeonTemplate(instanceId, template, now);
            if (group != null) return group;
        }
        return null;
    }

    private static FormedGroup? TryFormDungeonTemplate(int instanceId, RoleTemplate configured,
        DateTimeOffset now)
    {
        var template = configured;
        if (template.Size <= 0) return null;
        var pool = Pool(instanceId);
        if (pool.Count == 0) return null;

        // MatchServer FUN_14010c5d0 considers combinations of WHOLE applications.
        // A compatible early solo must not hide a later complete premade. Live requests
        // carry fixed choices; retain the legacy flexible-class path for callers without them.
        if (pool.All(e => e.Members.All(q => q.ChosenRole != null)))
        {
            var selected = FixedRoleSubset(pool, template);
            var selectedMembers = selected.SelectMany(e => e.Members).ToArray();
            if (selectedMembers.Length != template.Size) return null;
            Mark(selected, instanceId);
            return new FormedGroup(instanceId, selected, selectedMembers, selectedMembers,
                Array.Empty<Queuer>(), selectedMembers.Select(q => q.Role).ToArray());
        }

        // Entries stay ATOMIC - a queued party is taken whole or skipped - but the seating is
        // checked over the WHOLE candidate set each time one is added, so an entry that would
        // make the group unseatable is dropped rather than discovered too late. T138e; see
        // TryAssign for the Warrior-as-tank case that forced it.
        var taken = new List<Entry>();
        var members = new List<Queuer>();
        foreach (var e in pool)
        {
            if (e.Members.Length == 0) continue;           // legacy entry, no roles to seat
            if (members.Count + e.Members.Length > template.Size) continue;
            int before = members.Count;
            members.AddRange(e.Members);
            if (!TryAssign(members, template))
            {
                members.RemoveRange(before, members.Count - before);
                continue;
            }
            taken.Add(e);
            if (members.Count >= template.Size) break;
        }
        if (members.Count != template.Size) return null;

        var roles = new MatchRole[members.Count];
        if (!TryAssign(members, template, roles)) return null;

        Mark(taken, instanceId);
        return new FormedGroup(instanceId, taken, members, members, Array.Empty<Queuer>(), roles);
    }

    // T184: bounded role-count dynamic programming, derived from MatchServer:217054-217975.
    // Equal-count alternatives keep the first encountered application order. MatchServer's
    // random/tie priority is not reproduced; no capture pins that selection policy.
    private static List<Entry> FixedRoleSubset(IReadOnlyList<Entry> pool, RoleTemplate template,
        Entry? required = null)
    {
        static (int T, int H, int D) Counts(Entry e) =>
            (e.Members.Count(q => q.Role == MatchRole.Tank),
             e.Members.Count(q => q.Role == MatchRole.Healer),
             e.Members.Count(q => q.Role == MatchRole.Dps));
        bool Fits((int T, int H, int D) c) => c.T <= template.Tanks && c.H <= template.Healers && c.D <= template.Dps;
        var states = new Dictionary<(int T, int H, int D), List<Entry>>();
        var start = required == null ? (0, 0, 0) : Counts(required);
        if (!Fits(start)) return new List<Entry>();
        states[start] = required == null ? new List<Entry>() : new List<Entry> { required };
        foreach (var e in pool)
        {
            if (ReferenceEquals(e, required) || e.Members.Length == 0) continue;
            var n = Counts(e);
            foreach (var s in states.ToArray())
            {
                var next = (T: s.Key.T + n.T, H: s.Key.H + n.H, D: s.Key.D + n.D);
                if (!Fits(next) || states.ContainsKey(next)) continue;
                var chosen = new List<Entry>(s.Value) { e };
                states.Add(next, chosen);
            }
        }
        return states.OrderByDescending(s => s.Key.T + s.Key.H + s.Key.D).First().Value;
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
        int teamSize = rule.TeamSize;
        if (teamSize <= 0) return null;
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
            if (teams[0].Count == teamSize && teams[1].Count == teamSize) break;
        }
        if (teams[0].Count != teamSize || teams[1].Count != teamSize) return null;
        if (!Satisfies(rule, teams[0]) || !Satisfies(rule, teams[1])) return null;

        var all = new List<Queuer>(teams[0]);
        all.AddRange(teams[1]);
        var roles = new List<MatchRole>(all.Count);
        foreach (var q in all) roles.Add(MatchComposition.RoleOf(q.CharacterClass));
        Mark(taken, battleFieldId);
        return new FormedGroup(battleFieldId, taken, all, teams[0], teams[1], roles);
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
