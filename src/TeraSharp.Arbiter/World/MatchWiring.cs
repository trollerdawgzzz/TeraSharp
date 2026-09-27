// SPDX-License-Identifier: MIT
// Copyright (c) 2026 the TeraSharp contributors

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
// T138d ADDED THE PARTY. FIN alone left five matched strangers walking in one at a time, each
// getting their own channel. <see cref="FormParty"/> makes the group a party before anybody
// presses enter, so World routes them together.
//
// T161 PUT THE PARTY BEFORE THE FIN. cap_queue1 (the first live queue on TeraSharp) sent FIN
// and THEN the party, and neither client opened the enter-now/later window. classic_live3 has
// the other order for every member: 10568-10579 the party member lists, 10580/10581
// S_CHANGE_EVENT_MATCHING_STATE (queued 0, flag 1) and (queued 0, flag 0), 10585 FIN, 10587
// S_SYS_PARTY_INFO - which is also FUN_14090df50's per-member order in ArbiterServer.exe.c
// (0x15CD to World, FIN, S_SYS_PARTY_INFO). <see cref="MatchFoundFrames"/> is that middle part
// and the party layer slots it in per member; a group that cannot become a party still gets it
// through <see cref="Deliver"/>.
//
// T161 MADE A FORMED MATCH STATE. A <see cref="PendingMatch"/> per member lives from formation
// until that member enters the instance, turns it down, or it expires: C_MATCH_ADD is refused
// while it stands, and "enter later" keeps it
// claimable through C_ENTER_DUNGEON (World's own check: the matched party still carries the
// dungeon id AS_DO_CREATE_PARTY gave it). classic_live3 has one formed match and it was entered
// at once. T184f's cap_2man_client2 now proves that a lobby relog while still waiting does NOT
// resend FIN/SYS (4356 -> 4506 -> 4745 -> 5010); the invented automatic re-offer is suppressed.
// The expiry policy remains unverified against a real unanswered match held past its deadline.
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

    /// <summary>Queue-state pushes use World's fixed data session0. Tests collect the payload.</summary>
    public static Action<MatchQueueManager.Queuer, ushort, byte[]> SendWorld = DefaultSendWorld;

    /// <summary>
    /// T138d/T161. A formed group becomes a party, and every member it reaches gets the match
    /// frames (<see cref="MatchFoundFrames"/>) after the party exists and before their
    /// S_SYS_PARTY_INFO. Returns the character ids that were handed the frames that way; the
    /// rest get them through <see cref="Deliver"/>. The default goes through PartyWiring, which
    /// owns both the manager and the dispatcher; tests replace it because they have neither.
    /// </summary>
    public static Func<MatchQueueManager.FormedGroup, IReadOnlyList<byte[]>, IReadOnlyCollection<int>>
        FormParty = DefaultFormParty;

    /// <summary>Legacy test seam for explicit roster construction; relog no longer resends it.</summary>
    public static Func<int, IReadOnlyDictionary<int, int>, byte[]?> SysPartyInfoFor =
        (id, roles) => PartyWiring.Manager.SysPartyInfoFor(id, roles);

    /// <summary>T161: the clock the pending-match expiry reads. Tests pin it.</summary>
    public static Func<DateTimeOffset> Clock = () => DateTimeOffset.UtcNow;

    /// <summary>Tests only: put the seams back, empty the pool and forget every formed match.</summary>
    public static void Reset()
    {
        PartyOf = DefaultPartyOf;
        Deliver = DefaultDeliver;
        SendWorld = DefaultSendWorld;
        FormParty = DefaultFormParty;
        SysPartyInfoFor = (id, roles) => PartyWiring.Manager.SysPartyInfoFor(id, roles);
        Clock = () => DateTimeOffset.UtcNow;
        lock (PendingLock) PendingByChar.Clear();
        MatchQueueManager.Reset();
        MatchBrowseStatistics.Reset();
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
            chr?.Class ?? 0, chr?.Level ?? 1,
            TrueItemLevel: PartyWiring.Manager.TrueItemLevelFor((int)(chr?.Id ?? 0)));
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
            outp.Add(new MatchQueueManager.Queuer(peer?.PlayerId ?? 0, m.UserDbId, m.Class, m.Level,
                TrueItemLevel: PartyWiring.Manager.TrueItemLevelFor(m.UserDbId)));
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

    private static void DefaultSendWorld(MatchQueueManager.Queuer q, ushort opcode, byte[] payload)
    {
        var session = SessionForCharacter(q.CharacterId);
        // Arb076:15129 (admission) and Arb077:6151 (removal) explicitly resolve
        // GetDataSessionByServerId(0), regardless of the member's current dungeon World.
        if (session != null) Program.World?.SendFrame(WorldRegistration.DefaultWorldId, opcode, payload);
    }

    private static bool WithdrawApplication(MatchQueueManager.Entry? entry)
    {
        if (entry == null || !MatchQueueManager.Remove(entry.LeaderPlayerId)) return false;
        // Arb076:15693-15700 -> Arb077:6149-6152: World receives whether any application
        // remains; a member can also be present in another application's member list.
        foreach (var member in entry.Members)
            SendWorld(member, PartyPackets.AS_CHANGE_EVENT_MATCHING_STATE,
                PartyPackets.BuildAsChangeEventMatchingState(member.CharacterId,
                    isMatching: MatchQueueManager.FindForPlayer(member.PlayerId) != null));
        return true;
    }

    /// <summary>
    /// T138d. A dungeon match is ONE party; a battleground match is TWO, one per side, each
    /// carrying its own teamIndex. Without this the five matched strangers each walk in alone
    /// and World gives each of them their own channel - the one place T138c still differed from
    /// classic_live3, whose record 10587 shows the party arriving before anyone presses enter.
    /// </summary>
    private static IReadOnlyCollection<int> DefaultFormParty(MatchQueueManager.FormedGroup g,
        IReadOnlyList<byte[]> frames)
    {
        var covered = new HashSet<int>();
        if (!MatchQueueManager.IsBattleground(g.InstanceId))
        {
            var members = MatchedMembersFor(g);
            var a = PartyWiring.FormMatchedParty(members,
                raid: members.Count > MatchComposition.PartySize,
                dungeonId: g.InstanceId, matchFrames: frames);
            // A party that formed reached every ONLINE member; an offline one is unreachable
            // either way. Enter-world does not repeat the completion notification (T184f).
            if (a.Rejected == null) foreach (var m in members) covered.Add(m.UserDbId);
            return covered;
        }
        FormTeam(g, g.TeamA, g.InstanceId, teamIndex: 1, frames, covered);
        FormTeam(g, g.TeamB, g.InstanceId, teamIndex: 2, frames, covered);
        return covered;
    }

    /// <summary>The position member <paramref name="i"/> was seated in (T138d), else their own.</summary>
    private static MatchRole RoleAt(MatchQueueManager.FormedGroup g, int i)
        => i < g.Roles.Count ? g.Roles[i] : g.Members[i].Role;

    /// <summary>MA_FIN supplies application metadata; solo means a solo application, not a
    /// one-member completed party. Arb016:4408-4792 / Arb068:14433-14464.</summary>
    public static List<PartyManager.MatchedMember> MatchedMembersFor(MatchQueueManager.FormedGroup group)
    {
        var members = new List<PartyManager.MatchedMember>(group.Members.Count);
        for (int i = 0; i < group.Members.Count; i++)
        {
            var member = group.Members[i];
            var application = group.Entries.FirstOrDefault(e => e.Members.Any(q => q.CharacterId == member.CharacterId));
            int clears = MatchQueueManager.IsBattleground(group.InstanceId) ? 0
                : Program.Store?.GetDungeonClearCounts(member.CharacterId)
                    .FirstOrDefault(row => row.DungeonId == group.InstanceId).Clears ?? 0;
            members.Add(new PartyManager.MatchedMember(member.CharacterId, RoleAt(group, i),
                TrueItemLevel: member.TrueItemLevel, CountOfDungeonClear: clears,
                IsSoloMatching: application?.Size == 1));
        }
        return members;
    }

    /// <summary>
    /// One battleground side. A team has no template positions - the composition table caps
    /// classes, it does not seat them - so each member's DEFAULT position is what
    /// S_SYS_PARTY_INFO carries here.
    /// </summary>
    private static void FormTeam(MatchQueueManager.FormedGroup group,
        IReadOnlyList<MatchQueueManager.Queuer> team, int battleFieldId,
        int teamIndex, IReadOnlyList<byte[]> frames, HashSet<int> covered)
    {
        if (team == null || team.Count < 2) return;
        var teamIds = team.Select(q => q.CharacterId).ToHashSet();
        var members = MatchedMembersFor(group).Where(m => teamIds.Contains(m.UserDbId)).ToList();
        var a = PartyWiring.FormMatchedParty(members,
            raid: members.Count > MatchComposition.PartySize,
            dungeonId: 0, battleFieldId: battleFieldId, teamIndex: teamIndex, matchFrames: frames);
        if (a.Rejected == null) foreach (var m in members) covered.Add(m.UserDbId);
    }

    // ---- the handlers ----------------------------------------------------------------------

    /// <summary>
    /// One of <see cref="ClientOpcodes"/> arrived. Always returns true: all four are the
    /// Arbiter's, so there is never a reason to fall through to World.
    /// </summary>
    public static bool OnClientPacket(GameSession? session, ushort opcode, ReadOnlyMemory<byte> body)
    {
        if (session == null) return true;
        Sweep(DateTimeOffset.UtcNow);
        SweepPending(Clock());
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
    /// C_MATCH_PROGRESS -&gt; S_MATCH_PROGRESS: tanker/dealer/healer counts for an existing
    /// application. Arb077:8848 returns silently when the player has no application.
    /// </summary>
    public static bool OnMatchProgress(GameSession session, ReadOnlyMemory<byte> body)
    {
        var ids = MatchQueueManager.ReadInstanceIds(body);
        int asked = ids.Count > 0 ? ids[0] : MatchQueueManager.AnyInstance;
        var entry = MatchQueueManager.FindForPlayer(session.PlayerId);
        var frame = MatchQueueManager.ProgressFrame(entry, asked);
        if (frame != null) session.Send(frame);
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
        var ids = Offered(MatchQueueManager.ReadInstanceIds(body));   // T157: the sheet decides the battlegrounds
        if (ids.Count == 0) return Refuse(session);

        var queued = PartyOf(session);
        if (queued.Count == 0) return Refuse(session);

        // T161 (b): a formed match nobody has entered yet is still this party's match. Queuing
        // again would put the same people in two groups at once.
        var standing = Standing(queued, Clock());
        if (standing != null)
        {
            Log.LogInformation("match: player {P} refused - character {C} still has instance {I} waiting (until {E:u})",
                session.PlayerId, standing.Value.CharacterId, standing.Value.Match.InstanceId,
                standing.Value.Match.ExpiresAt);
            return Refuse(session);
        }

        // T138f: the trailing int32 of C_MATCH_ADD's second array is the POSITION each player
        // picked in the matching window, so it has to reach the seating - a Warrior who queued
        // as DPS is not a tank we can use. MatchComposition.ChoiceToRole has the samples.
        var members = MatchQueueManager.WithChoices(queued, MatchQueueManager.ReadQueueChoices(body));
        ids.RemoveAll(id => DungeonMatchRules.For(id) is { } rule && !rule.AcceptsApplicant(members.Count));
        if (ids.Count == 0) return Refuse(session);

        var now = Clock();
        MatchQueueManager.Add(session.PlayerId, ids.ToArray(), members, now);
        MatchBrowseStatistics.Start();

        // The pool-add tail is that same position coming back: record 8662 carries 1 for the
        // DPS queue and record 10333 carries 0 for the tank one, both from the same character.
        var pool = new List<MatchQueueManager.PoolPlayer>(members.Count);
        foreach (var m in members)
            pool.Add(new MatchQueueManager.PoolPlayer(
                PartyPackets.PlanetId, (int)m.PlayerId, 0, (int)m.Role));

        // Arb076:14928-14944 / 15123-15136: every local member gets @2173 when
        // the accepted application contains a dungeon. cap_2man client2:819 and
        // client1:1230 put it before C730; refused applications never reach here.
        var queuedFrames = new List<byte[]>();
        if (ids.Any(id => !MatchQueueManager.IsBattleground(id)))
            queuedFrames.Add(DbProxyHandlers.BuildSystemMessage("@2173"));
        // Arb_part_076:15039-15114: the pool and queued-state frames reach each local member.
        queuedFrames.Add(MatchQueueManager.BuildAddInterPartyMatchPool(ids, pool,
            matchingType: MatchQueueManager.IsBattleground(ids[0]) ? 1 : 0));
        queuedFrames.AddRange(MatchQueueManager.EventMatchingFrames(ids, queued: true));
        foreach (var member in members)
            foreach (var frame in queuedFrames)
                if (member.PlayerId == session.PlayerId) session.Send(frame);
                else Deliver(member, frame);

        // cap_2man tap records 1463 / 1838: 15CD [UserDbId][01]. Arb076:15123-15139
        // does this after the client pool/queued notifications, before a match can finish.
        foreach (var member in members)
            SendWorld(member, PartyPackets.AS_CHANGE_EVENT_MATCHING_STATE,
                PartyPackets.BuildAsChangeEventMatchingState(member.CharacterId, isMatching: true));

        Log.LogInformation("match: player {P} queued {N} member(s) for [{Ids}]",
            session.PlayerId, members.Count, string.Join(",", ids));

        foreach (int id in ids) TryFormAndFinish(id, now);
        return true;
    }

    /// <summary>
    /// T157. The instance ids a queue may go on: every dungeon, and only the battlegrounds
    /// BattleFieldData.xml has - the same sheet World builds the battleground tab from, so one
    /// taken out of the sheet can be neither seen nor queued. No sheet: no battleground.
    /// </summary>
    public static List<int> Offered(IReadOnlyList<int> ids)
    {
        var keep = new List<int>(ids.Count);
        var disabled = Program.Store?.GetDisabledDungeons();
        foreach (int id in ids)
        {
            if (disabled?.Contains(id) == true) continue;
            if (!MatchQueueManager.IsBattleground(id) || MatchComposition.IsOffered(id)) { keep.Add(id); continue; }
            if (BattleFieldSheet.Current == null)
                Log.LogWarning("match: battleground {Id} refused - {Sheet} could not be read, so no battleground is offered",
                    id, BattleFieldSheet.Source);
            else
                Log.LogInformation("match: battleground {Id} is not in {Sheet} - refused", id, BattleFieldSheet.Source);
        }
        return keep;
    }

    /// <summary>T201: DungeonManager::ChangeDungeonOnOff cancels applications for a disabled
    /// dungeon (Arb061:7753). Reuse the existing removal and client cancellation sequence.</summary>
    internal static void DisableDungeon(int dungeon)
    {
        foreach (var entry in MatchQueueManager.All().Where(e => e.Wants(dungeon)))
        {
            // Native ClearAllDungeonMatching removes this dungeon from each application;
            // applications with other destinations survive (Arb076:13030-13091).
            var remaining = entry.InstanceIds.Where(id => id != dungeon).ToArray();
            if (remaining.Length == 0) { if (!WithdrawApplication(entry)) continue; }
            else entry.InstanceIds = remaining;
            foreach (var member in entry.Members)
            {
                // UnicastMatchRemove writes S_DEL_INTER_PARTY_MATCH_POOL, not the formed
                // match's S_CANCEL_PARTY_MATCH_POOL (Arb077:9335-9373).
                Deliver(member, MatchQueueManager.BuildDelPool(dungeon));
                foreach (var frame in MatchQueueManager.EventMatchingFrames(new[] { dungeon }, queued: false)) Deliver(member, frame);
            }
        }
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

        MatchBrowseStatistics.Completed(group, now);
        foreach (var e in group.Entries) MatchQueueManager.Remove(e.LeaderPlayerId);
        MatchBrowseStatistics.Publish(now);

        // T161: the party FIRST, then per member the state pair, FIN and S_SYS_PARTY_INFO -
        // classic_live3 10568..10587. Whoever the party layer could not reach still gets the
        // state pair and FIN here: a match that cannot become a party is still a match.
        var frames = MatchFoundFrames(instanceId);
        var covered = FormParty(group, frames) ?? Array.Empty<int>();
        foreach (var m in group.Members)
            if (!covered.Contains(m.CharacterId))
                foreach (var f in frames) Deliver(m, f);

        RecordPending(group, Clock());

        Log.LogInformation("match: instance {Id} formed with {N} player(s) from {E} entr(ies); entry open for {S}s",
            instanceId, group.Members.Count, group.Entries.Count, EntrySeconds());
        return group;
    }

    /// <summary>
    /// T161. What one matched member gets between the party and their S_SYS_PARTY_INFO:
    /// S_CHANGE_EVENT_MATCHING_STATE (queued 0, flag 1), the same with flag 0, then
    /// S_FIN_INTER_PARTY_MATCH - classic_live3 10580, 10581, 10585.
    /// <para>T161b: the state frames carry the instance's EventMatching.xml events and each is
    /// sent only when its list is not empty - WorldOfPartyMatchHelper::SendChangeEventMatchingState
    /// never sends an empty one (MatchQueueManager.EventMatchingFrames). T161 sent both empty.</para>
    /// </summary>
    public static IReadOnlyList<byte[]> MatchFoundFrames(int instanceId)
    {
        var f = MatchQueueManager.AllEventMatchingFrames();
        f.Add(MatchQueueManager.BuildFinInterPartyMatch(instanceId));
        return f;
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
        var application = MatchQueueManager.FindForPlayer(session.PlayerId);
        var queuedFor = application?.InstanceIds ?? ids.ToArray();
        WithdrawApplication(application);
        session.Send(MatchQueueManager.BuildDelPool(instance));
        foreach (var f in MatchQueueManager.EventMatchingFrames(queuedFor, queued: false)) session.Send(f);   // T161b

        // T184: C_MATCH_DEL removes queue applications (World:583220 -> SA 0x13AA,
        // Arb062:6233-6281). It is not a rejection of a party already formed by MA_FIN.
        return true;
    }

    /// <summary>
    /// The session went away. Unlike a party, a QUEUE does not survive a logout - there is
    /// nobody to put in the group - so the whole entry goes, leader or member.
    /// </summary>
    public static void Unregister(GameSession? session)
    {
        if (session == null || session.PlayerId == 0) return;
        if (WithdrawApplication(MatchQueueManager.FindForPlayer(session.PlayerId)))
            Log.LogDebug("match: player {P} left world - queue entry withdrawn", session.PlayerId);
        // T161b: GameSession.LeaveWorld (the socket went) clears InWorld before this runs; the
        // lobby / exit path (OnWorldLeaveConfirmed) still has it set.
        OnLeftWorld(QueuerFor(session).CharacterId, disconnected: !session.InWorld);
    }

    /// <summary>
    /// T161b. A member who DISCONNECTED takes their formed match with them - voided, the rest told
    /// with the cancel pair (arbiter-crash.log: both members crashed at 01:11:42 and instance 9781
    /// stood until 01:13:11). A lobby relog retains the waiting entry. Returns whether one went.
    /// </summary>
    public static bool OnLeftWorld(int characterId, bool disconnected)
        => disconnected && Decline(characterId, "disconnected");

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
                foreach (var f in MatchQueueManager.EventMatchingFrames(e.InstanceIds, queued: false)) Deliver(m, f);
            }
            WithdrawApplication(e);
        }
        if (expired.Count > 0)
            Log.LogInformation("match: {N} queue entr(ies) timed out after {S}s",
                expired.Count, MatchQueueManager.QueueTimeoutSeconds);
        return expired.Count;
    }

    // ---- T161: a formed match is state, not a fire-and-forget FIN ---------------------------

    /// <summary>
    /// One formed match, shared by every member it still waits for. Created when the group
    /// forms, left behind by each member as they enter <see cref="InstanceId"/>, and voided -
    /// with the cancel pair to everyone still waiting - by a decline or by
    /// <see cref="ExpiresAt"/>.
    /// </summary>
    public sealed class PendingMatch
    {
        public int InstanceId { get; init; }
        /// <summary>The matched party at formation; 0 when none could be formed.</summary>
        public long PartyId { get; init; }
        public DateTimeOffset FormedAt { get; init; }
        public DateTimeOffset ExpiresAt { get; init; }
        /// <summary>Character id -&gt; the position they were seated in, for S_SYS_PARTY_INFO.</summary>
        public IReadOnlyDictionary<int, int> Roles { get; init; } = new Dictionary<int, int>();
        internal readonly HashSet<int> Waiting = new();

        /// <summary>The characters that have not entered yet (a copy).</summary>
        public List<int> WaitingFor() { lock (PendingLock) return Waiting.ToList(); }
    }

    private static readonly object PendingLock = new();
    private static readonly Dictionary<int, PendingMatch> PendingByChar = new();
    private static Timer? _expiryTimer;

    /// <summary>
    /// How long a formed match stays claimable. OURS: cap_2man has no unanswered match held
    /// through this deadline, so the expiry window remains an unverified local policy.
    /// </summary>
    public const string EntrySecondsVariable = "TERASHARP_MATCH_ENTRY_SECONDS";
    public const int DefaultEntrySeconds = 300;

    /// <summary><see cref="EntrySecondsVariable"/>, read fresh; <see cref="DefaultEntrySeconds"/> when unset or not a positive number.</summary>
    public static int EntrySeconds()
    {
        var raw = TerasConfig.Get(EntrySecondsVariable);
        return !string.IsNullOrWhiteSpace(raw) && int.TryParse(raw.Trim(), out int v) && v > 0
            ? v : DefaultEntrySeconds;
    }

    /// <summary>The match <paramref name="characterId"/> has standing at <paramref name="now"/>, or null.</summary>
    public static PendingMatch? PendingFor(int characterId, DateTimeOffset now)
    {
        if (characterId <= 0) return null;
        lock (PendingLock)
            return PendingByChar.TryGetValue(characterId, out var m) && now < m.ExpiresAt ? m : null;
    }

    /// <summary>(b) The first of <paramref name="queued"/> with a match standing, or null.</summary>
    public static (int CharacterId, PendingMatch Match)? Standing(
        IReadOnlyList<MatchQueueManager.Queuer> queued, DateTimeOffset now)
    {
        if (queued == null) return null;
        foreach (var q in queued)
        {
            var m = PendingFor(q.CharacterId, now);
            if (m != null) return (q.CharacterId, m);
        }
        return null;
    }

    /// <summary>How many characters have a match standing (expired ones included until swept).</summary>
    public static int PendingCount { get { lock (PendingLock) return PendingByChar.Count; } }

    private static void RecordPending(MatchQueueManager.FormedGroup g, DateTimeOffset now)
    {
        var roles = new Dictionary<int, int>(g.Members.Count);
        for (int i = 0; i < g.Members.Count; i++)
            if (g.Members[i].CharacterId > 0) roles[g.Members[i].CharacterId] = (int)RoleAt(g, i);
        if (roles.Count == 0) return;

        long partyId = 0;
        foreach (int c in roles.Keys) { partyId = PartyWiring.Manager.PartyIdOf(c); if (partyId != 0) break; }

        var m = new PendingMatch
        {
            InstanceId = g.InstanceId,
            PartyId = partyId,
            FormedAt = now,
            ExpiresAt = now.AddSeconds(EntrySeconds()),
            Roles = roles,
        };
        lock (PendingLock)
            foreach (int c in roles.Keys) { m.Waiting.Add(c); PendingByChar[c] = m; }
        EnsureExpiryTimer();
    }

    /// <summary>
    /// The live server only - tests drive <see cref="SweepPending"/> with a pinned clock. The
    /// matcher has no scheduler (the header's "swept, not ticked"), but an expiry has to reach
    /// players who send nothing, so this one sweep gets a timer.
    /// </summary>
    private static void EnsureExpiryTimer()
    {
        if (Program.World == null || _expiryTimer != null) return;
        lock (PendingLock)
            _expiryTimer ??= new Timer(_ =>
            {
                try { SweepPending(Clock()); }
                catch (Exception ex) { Log.LogWarning(ex, "match: pending-match sweep failed"); }
            }, null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
    }

    /// <summary>
    /// cap_2man_client2: FIN 4356, lobby 4506, selection 4745, load-finished 5010;
    /// there is no second FIN or 488-byte SYS at relog. Pending membership is retained,
    /// but replaying the completion notification is not part of enter-world.
    /// </summary>
    public static List<byte[]> ReofferFrames(int characterId, DateTimeOffset now)
    {
        return new List<byte[]>();
    }

    /// <summary>
    /// Compatibility hook for SocialHandlers.SendBlockList. World entry does not replay FIN.
    /// </summary>
    public static bool ReofferPending(GameSession? session)
    {
        var chr = session?.SelectedCharacter;
        if (chr == null) return false;
        var frames = TakeReoffer(session!.GameId, (int)chr.Id, Clock());
        foreach (var f in frames) session.Send(f);
        return frames.Count > 0;
    }

    /// <summary>
    /// Kept for callers/tests of the former re-offer hook. A different GameId does not mean
    /// another match formed: cap_2man_client2 relogs between its second FIN and later entry.
    /// </summary>
    public static List<byte[]> TakeReoffer(ulong gameId, int characterId, DateTimeOffset now)
    {
        return ReofferFrames(characterId, now);
    }

    /// <summary>
    /// (d) <paramref name="characterId"/> turned their match down some way other than leaving
    /// the party (<see cref="OnLeftParty"/>): the match is off, and the rest of it is told.
    /// </summary>
    public static bool Decline(int characterId, string how)
    {
        var m = PendingFor(characterId, Clock());
        if (m == null) return false;
        VoidPending(m, $"declined by character {characterId} ({how})", except: characterId);
        return true;
    }

    /// <summary>
    /// (c) A member walked into the instance - C_ENTER_DUNGEON, World's SA_RESPONSE_ENTER_DUNGEON
    /// admitting them - and their share of the match is used. The others keep theirs.
    /// </summary>
    public static bool OnDungeonEntered(int characterId, int dungeonId)
    {
        PendingMatch? m;
        lock (PendingLock)
        {
            if (!PendingByChar.TryGetValue(characterId, out m) || m.InstanceId != dungeonId) return false;
            PendingByChar.Remove(characterId);
            m.Waiting.Remove(characterId);
        }
        Log.LogInformation("match: character {C} entered matched instance {I}; {N} still to come",
            characterId, dungeonId, m.WaitingFor().Count);
        return true;
    }

    /// <summary>
    /// Forget the pending entry after PartyManager has handled withdrawal. cap_2man_client1
    /// 11051-11055 has one reset/leave/cancel sequence; sending another cancel here duplicates it.
    /// </summary>
    public static bool OnLeftParty(int characterId)
    {
        lock (PendingLock)
        {
            // The leaver may already have entered while another member is still waiting.
            var m = PendingByChar.Values.FirstOrDefault(p => p.Roles.ContainsKey(characterId));
            if (m == null) return false;
            foreach (int c in m.Waiting)
                if (PendingByChar.TryGetValue(c, out var mine) && ReferenceEquals(mine, m))
                    PendingByChar.Remove(c);
            m.Waiting.Clear();
        }
        return true;
    }

    /// <summary>(d) Void every match past its <see cref="PendingMatch.ExpiresAt"/>. Returns how many.</summary>
    public static int SweepPending(DateTimeOffset now)
    {
        var expired = new List<PendingMatch>();
        lock (PendingLock)
            foreach (var m in PendingByChar.Values)
                if (now >= m.ExpiresAt && !expired.Contains(m)) expired.Add(m);
        foreach (var m in expired) VoidPending(m, "entry window expired");
        return expired.Count;
    }

    /// <summary>
    /// Drop <paramref name="m"/> for everyone still waiting and tell each of them with the
    /// cancel pair a queue timeout already uses (S_CANCEL_PARTY_MATCH_POOL, record 53967, and
    /// the not-queued state) - the only "your match is gone" frames any capture has.
    /// <paramref name="except"/> is a member who has been told another way.
    /// </summary>
    private static void VoidPending(PendingMatch m, string why, int except = 0)
    {
        List<int> told;
        lock (PendingLock)
        {
            told = m.Waiting.ToList();
            foreach (int c in told)
                if (PendingByChar.TryGetValue(c, out var mine) && ReferenceEquals(mine, m))
                    PendingByChar.Remove(c);
            m.Waiting.Clear();
        }
        foreach (int c in told)
        {
            if (c == except) continue;
            var q = new MatchQueueManager.Queuer(0, c, 0, 1);
            Deliver(q, MatchQueueManager.BuildCancelPartyMatchPool(m.InstanceId));
            foreach (var f in MatchQueueManager.EventMatchingFrames(new[] { m.InstanceId }, queued: false)) Deliver(q, f);
        }
        Log.LogInformation("match: instance {I} voided ({Why}); {N} member(s) told",
            m.InstanceId, why, told.Count(c => c != except));
    }

    /// <summary>
    /// The T136b answer, kept for the cases where there is nothing to queue: the client is put
    /// back in the state a normal cancel leaves it in, which is what "you are not in a queue"
    /// looks like on the wire. No invented system-message code - classic_live3 never shows one
    /// on this path, and an @code we made up would render as garbage.
    /// </summary>
    private static bool Refuse(GameSession session)
    {
        // T161b: no state frame - nothing is queued, so its list would be empty.
        session.Send(MatchQueueManager.BuildCancelPartyMatchPool());
        return true;
    }
}
