using Microsoft.Extensions.Logging;

namespace TeraSharp.Arbiter.World;

// =============================================================================================
// PartyManager - parties as pure in-memory state (T35). Research: status/PARTY-DESIGN.md.
//
// The real ArbiterServer owns parties outright: membership lives in its RAM, it fans S_PARTY_*
// out to the members itself, and World only ever gets a MIRROR so it can do loot / exp /
// instance rules. Nothing about a party is persisted - PARTY-DESIGN.md section 4 proves it four
// ways - so this class has no store and there is no PERSISTENCE-MAP row.
//
// NOTHING IS WIRED UP. This is state plus two pure entry points; WorldBridge and GameSession are
// human-owned and untouched. status/PARTY-DESIGN.md section 10 has the exact wiring diff, and
// since T41 it is three lines because World/ActionDispatcher.cs performs the sends.
//
//   OnClientPacket(ticket, opcode, body)  -> PartyActions
//   OnWorldFrame(opcode, payload)         -> PartyActions
//
// Both are total functions of the manager's state: no I/O, no clock, no randomness, so the whole
// invite -> accept -> loot -> leave -> dismiss sequence is a golden test.
//
// Client packets come back as a NAME plus a field dictionary rather than bytes, because
// GameSession.SendByDef already turns exactly that into bytes through the .def codec. Handing
// back bytes would mean a second, hand-rolled client-packet writer living next to the real one -
// and two of the party .def files are already known-wrong (PARTY-DESIGN.md section 6.3), so the
// duplicate would bake those errors in twice. The tests still go through the codec end to end:
// they load DefinitionRegistry from tera_v100_MASTER_FINAL and write every emitted action.
// The one exception is SA_BYPASS_TO_GROUP, where World hands us a finished client packet - that
// comes back as raw bytes.
//
// Arbiter -> World frames ARE bytes, built by PartyPackets (T28) and byte-exact against the
// layouts in PARTY-DESIGN.md section 5.1.
// =============================================================================================

/// <summary>
/// One packet for one client session. Either a def-driven packet (PacketName + Fields, for
/// GameSession.SendByDef) or a finished client packet World built (RawPacket, for
/// GameSession.Send). T41: also an <see cref="IArbiterClientAction"/>, so ActionDispatcher can
/// send it without knowing it came from a party.
/// </summary>
public readonly record struct ClientAction(
    uint Ticket, string PacketName, IReadOnlyDictionary<string, object>? Fields, byte[]? RawPacket)
    : IArbiterClientAction
{
    public static ClientAction Def(uint ticket, string name, IReadOnlyDictionary<string, object> fields)
        => new(ticket, name, fields, null);

    /// <summary>A client packet World already framed - SA_BYPASS_TO_GROUP's payload.</summary>
    public static ClientAction Raw(uint ticket, byte[] packet)
        => new(ticket, "(raw)", null, packet);

    public bool IsRaw => RawPacket != null;

    /// <summary>Parties address clients by tunnel ticket - GameSession.TunnelKey.</summary>
    public Recipient To => Recipient.Ticket(Ticket);

    /// <summary>Always null: a party's raw packets come from World already framed.</summary>
    public byte[]? RawBody => null;
}

/// <summary>One Arbiter -> World frame: an opcode and the payload PartyPackets built.</summary>
public readonly record struct WorldAction(ushort Opcode, byte[] Payload) : IArbiterWorldAction;

/// <summary>Everything one input produced. Empty is a valid answer.</summary>
public sealed class PartyActions : IArbiterActions
{
    public List<ClientAction> ToClients { get; } = new();
    public List<WorldAction> ToWorld { get; } = new();

    private readonly List<IArbiterAction> _ordered = new();

    /// <summary>
    /// Every client packet and World frame in the order the manager produced them - the list
    /// ActionDispatcher walks. The two typed lists above hold the same items and stay because
    /// the party tests read them; nothing writes to either directly.
    /// </summary>
    public IReadOnlyList<IArbiterAction> Ordered => _ordered;

    /// <summary>The session that caused this. Recipient.None for an OnWorldFrame input, which is
    /// why a rejection there goes to the log rather than to a client.</summary>
    public Recipient Origin { get; internal set; } = Recipient.None;

    /// <summary>
    /// Why nothing happened, when nothing happened for a reason worth logging. The real Arbiter
    /// answers most of these with S_SYSTEM_MESSAGE (FUN_1403aa760(user, 0x3ea, 0) when the
    /// target has no session, 0x53a when blocked, 0xA64 when the world-majority rule fails);
    /// this carries the reason so the wiring can decide, rather than the manager guessing at
    /// message ids we have never seen on the wire.
    /// </summary>
    public string? Rejected { get; set; }

    public bool IsEmpty => ToClients.Count == 0 && ToWorld.Count == 0;

    internal PartyActions Reject(string why) { Rejected = why; return this; }

    internal void Client(ClientAction a) { ToClients.Add(a); _ordered.Add(a); }

    internal void World(ushort op, byte[] payload)
    {
        var w = new WorldAction(op, payload);
        ToWorld.Add(w);
        _ordered.Add(w);
    }
}

/// <summary>
/// One party. Field names and offsets are the real Party object's (PARTY-DESIGN.md section 2);
/// the slot array is deliberately a fixed 30 because slot indices are WIRE-VISIBLE - they are
/// S_PARTY_MEMBER_LIST's `slot` and AS_DO_SWAP_PARTY's SlotIndex1/2 - so a List would not do.
/// </summary>
public sealed class Party
{
    /// <summary>Party+0xD8 / Party+0x1C8: 30 slots, empty = null. Raid capacity is the array size.</summary>
    public const int SlotCount = PartyPackets.MaxRaidMembers;

    public long Id { get; init; }
    public int OwnerPlanetId { get; init; } = PartyPackets.PlanetId;
    /// <summary>Party+0x79. Flips to true on SA_EXTEND_PARTY / a raid merge.</summary>
    public bool Raid { get; set; }
    /// <summary>Party+0xD4.</summary>
    public bool IsAnonymous { get; set; }
    /// <summary>Party+0x94, -1 for a normal party (the ctor default).</summary>
    public int PartyType { get; set; } = -1;
    /// <summary>Party+0xC0/0xC4 - the manager's PDId.</summary>
    public int ManagerPlanetId { get; set; } = PartyPackets.PlanetId;
    public int ManagerDbId { get; set; }
    /// <summary>Party+0xA8..0xBC, the seven fields that travel together.</summary>
    public PartyPackets.LootSettings Loot { get; set; } = DefaultLoot;
    public PartyPackets.PartyMember?[] Slots { get; } = new PartyPackets.PartyMember?[SlotCount];

    /// <summary>Party::Party sets this to 5, or 0x1e when the party is a raid.</summary>
    public int MaxMembers => Raid ? PartyPackets.MaxRaidMembers : PartyPackets.MaxPartyMembers;
    public int Count { get { int n = 0; foreach (var s in Slots) if (s != null) n++; return n; } }

    /// <summary>
    /// The Arbiter reads its defaults out of config (ctor copies +0x15f4..+0x1608). We have never
    /// seen those bytes, so this is the client's own out-of-the-box party setting: free-for-all,
    /// rare = grade 4 and up, roll for rares, equipment counts as rare, no class restriction,
    /// roll for bound loot, looting allowed in combat.
    /// </summary>
    public static readonly PartyPackets.LootSettings DefaultLoot = new(
        Method: 0, RareGradeForDicing: 4, RareItemDistributionMethod: 1,
        EquipmentForDicing: true, FindClassForDicing: false,
        BoundOnLootItemDistributionMethod: 1, ForbidLootingInBattle: false);

    public int IndexOf(int userDbId)
    {
        for (int i = 0; i < Slots.Length; i++)
            if (Slots[i] is { } m && m.UserDbId == userDbId) return i;
        return -1;
    }

    /// <summary>Party::FindEmptyIndex - the first slot whose UserDbId is 0. -1 when full.</summary>
    public int FindEmptySlot()
    {
        int cap = MaxMembers;
        for (int i = 0; i < cap; i++) if (Slots[i] == null) return i;
        return -1;
    }

    public IEnumerable<PartyPackets.PartyMember> Members()
    {
        foreach (var s in Slots) if (s is { } m) yield return m;
    }

    public bool IsManager(int userDbId) => ManagerDbId == userDbId;
}

public sealed class PartyManager
{
    private readonly ILogger _log;
    public PartyManager(ILogger log) => _log = log;

    // ---- identity ----

    /// <summary>ServerConfig.xml planetId; DAT_140e2d020. 0x0AF0 = 2800 in every capture.</summary>
    public const int PlanetId = PartyPackets.PlanetId;

    /// <summary>
    /// The 16-bit tag dbo.spIssuePartyInnerId hands each Arbiter process at boot
    /// (PartyManager::SetPlanetInnerId, FUN_14091d210, Arb_part_079.c:14078). One process, so any
    /// stable value does; it only exists to keep two Arbiters' party ids apart.
    /// </summary>
    public const ushort PlanetInnerId = 1;

    private int _idSeq;

    /// <summary>
    /// PartyId = ((PlanetId &lt;&lt; 16 | PlanetInnerId) &lt;&lt; 32) | ++counter
    /// (PartyManager::New_CreateParty, Arb_part_079.c:15283): the high dword is the PlanetId
    /// shifted left 16 and ORed with the u16 at PartyManager+0x70; the low dword is a
    /// lock-protected counter, read and incremented under LOCK and used as counter + 1.
    /// With PlanetId 2800 the high dword is 0x0AF00001, so the first id is 0x0AF0000100000001.
    /// The counter is process-lifetime on the real server and nothing detects a duplicate, so if
    /// party ids ever need to survive a restart, persist or randomise the seed
    /// (PARTY-DESIGN.md section 7 rule 5).
    /// </summary>
    public long NextPartyId()
    {
        uint high = ((uint)PlanetId << 16) | PlanetInnerId;
        uint low = (uint)Interlocked.Increment(ref _idSeq);
        return (long)(((ulong)high << 32) | low);
    }

    // ---- who is online ----

    /// <summary>
    /// What the routing layer knows about a logged-in character. Ticket is the tunnel Ticket
    /// (MULTIPLAYER-DESIGN.md section 1) - the manager never sees a GameSession.
    /// </summary>
    public readonly record struct PartyPlayer(
        uint Ticket, int UserDbId, string Name, int Level, int Class, int Race, int Gender,
        ulong GameId, int Laurel = 0, int AwakenGrade = 0);

    private readonly Dictionary<uint, PartyPlayer> _byTicket = new();
    private readonly Dictionary<int, uint> _ticketByDbId = new();

    public void Register(PartyPlayer p)
    {
        _byTicket[p.Ticket] = p;
        _ticketByDbId[p.UserDbId] = p.Ticket;
        // A member who was offline comes back online in whatever party still holds them.
        if (_byMember.TryGetValue(p.UserDbId, out var party))
        {
            int i = party.IndexOf(p.UserDbId);
            if (i >= 0) party.Slots[i] = party.Slots[i]!.Value with { Online = true, GameId = p.GameId };
        }
    }

    /// <summary>
    /// The session went away. The party SURVIVES - PartyMemberInfo+0x70 is an Online flag, not a
    /// removal (PartyManager::MemberLeaveWorld, FUN_14091dd20) - so the member is marked offline
    /// and the rest are told with S_LOGOUT_PARTY_MEMBER.
    /// </summary>
    public PartyActions Unregister(uint ticket)
    {
        var a = new PartyActions();
        if (!_byTicket.Remove(ticket, out var p)) return a;
        _ticketByDbId.Remove(p.UserDbId);
        _applications.RemoveWhere(x => x.applicant == p.UserDbId || x.target == p.UserDbId);

        if (!_byMember.TryGetValue(p.UserDbId, out var party)) return a;
        int i = party.IndexOf(p.UserDbId);
        if (i >= 0) party.Slots[i] = party.Slots[i]!.Value with { Online = false };
        foreach (var (t, _) in OnlineMembersOf(party, except: p.UserDbId))
            a.Client(ClientAction.Def(t, "S_LOGOUT_PARTY_MEMBER", new Dictionary<string, object>
            {
                ["serverId"] = (uint)PlanetId,
                ["playerId"] = (uint)p.UserDbId,
            }));
        return a;
    }

    public bool TryGetPlayer(uint ticket, out PartyPlayer p) => _byTicket.TryGetValue(ticket, out p);

    /// <summary>
    /// Online character by NAME. SA_JOIN_PARTY_IN_ARBITER carries no db-ids, so this is the only
    /// way in; the real handler uses the same table lookup (FUN_14082dc50(userTable, name, 3)).
    /// Character names are unique per server and the client upper-cases nothing, so the match is
    /// case-insensitive and ordinal - a name that differs only in case is the same character.
    /// </summary>
    public bool TryGetPlayerByName(string name, out PartyPlayer found)
    {
        found = default;
        if (string.IsNullOrEmpty(name)) return false;
        foreach (var p in _byTicket.Values)
        {
            if (!string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) continue;
            found = p;
            return true;
        }
        return false;
    }
    public bool TryGetTicket(int userDbId, out uint ticket) => _ticketByDbId.TryGetValue(userDbId, out ticket);

    private IEnumerable<(uint ticket, PartyPackets.PartyMember member)> OnlineMembersOf(Party p, int except = 0)
    {
        foreach (var m in p.Members())
        {
            if (m.UserDbId == except) continue;
            if (_ticketByDbId.TryGetValue(m.UserDbId, out uint t)) yield return (t, m);
        }
    }

    // ---- the parties ----

    private readonly Dictionary<long, Party> _byId = new();
    private readonly Dictionary<int, Party> _byMember = new();

    /// <summary>PartyManager::FindPartyByPDId (FUN_1409145a0). One planet, so the key is the db id.</summary>
    public Party? FindByMember(int userDbId) => _byMember.TryGetValue(userDbId, out var p) ? p : null;
    public Party? FindById(long id) => _byId.TryGetValue(id, out var p) ? p : null;
    public int PartyCount => _byId.Count;

    /// <summary>Pending "I applied to join your party" edges, applicant -> target.</summary>
    private readonly HashSet<(int applicant, int target)> _applications = new();
    public bool HasApplication(int applicant, int target) => _applications.Contains((applicant, target));

    private PartyPackets.PartyMember MemberOf(in PartyPlayer p, bool canInvite) => new(
        PlanetId: PlanetId, UserDbId: p.UserDbId, GameId: p.GameId, Level: p.Level, Class: p.Class,
        Race: p.Race, Gender: p.Gender, Role: -1, Name: p.Name, CanInvite: canInvite,
        Alive: true, Online: true, AchievementGrade: p.Laurel, AwakenGrade: p.AwakenGrade);

    // =========================================================================================
    // Client -> Arbiter
    // =========================================================================================

    public PartyActions OnClientPacket(uint ticket, ushort opcode, byte[] body)
    {
        var a = new PartyActions { Origin = Recipient.Ticket(ticket) };
        if (!_byTicket.TryGetValue(ticket, out var me))
            return a.Reject($"ticket {ticket} is not a registered player");

        switch (opcode)
        {
            case PartyPackets.C_APPLY_PARTY: return ApplyParty(a, me, body);
            case C_PARTY_APPLICATION_DENIED: return DenyApplication(a, me, body);
            case PartyPackets.C_DISMISS_PARTY: return DismissParty(a, me);
            case PartyPackets.C_PARTY_LOOTING_METHOD: return RequestLootingMethod(a, me, body);
            case PartyPackets.C_BAN_PARTY_MEMBER: return RequestBan(a, me, body);
            case PartyPackets.C_MERGE_PARTY_TO_RAID: return MergeToRaid(a, me, body);
            default: return a.Reject($"0x{opcode:X4} is not an Arbiter-side party packet");
        }
    }

    /// <summary>C_PARTY_APPLICATION_DENIED (0x4F00): [u32 pid]. Not in PartyPackets because T28
    /// only covered the seven the task named.</summary>
    public const ushort C_PARTY_APPLICATION_DENIED = 0x4F00;

    /// <summary>
    /// C_APPLY_PARTY (0xA889) - "let me into the party you have listed". Handler FUN_1404db920
    /// (Arb_part_040.c:19109) -> PartyMatchManager::ApplyParty (FUN_1408279e0): the target must
    /// exist, must not be me, and I must not already be in a party
    /// (`FindPartyByPDId(localPlanet, myDbId) == 0`). The answer is S_OTHER_USER_APPLY_PARTY to
    /// the TARGET, not to me.
    /// </summary>
    private PartyActions ApplyParty(PartyActions a, in PartyPlayer me, byte[] body)
    {
        int? targetId = PartyPackets.ParseCApplyParty(body);
        if (targetId == null) return a.Reject("C_APPLY_PARTY: short body");
        if (targetId.Value == me.UserDbId) return a.Reject("C_APPLY_PARTY: cannot apply to yourself");
        if (!_ticketByDbId.TryGetValue(targetId.Value, out uint targetTicket))
            return a.Reject($"C_APPLY_PARTY: player {targetId} is not online");   // real: system message 0x3EA
        if (FindByMember(me.UserDbId) != null)
            return a.Reject("C_APPLY_PARTY: applicant is already in a party");

        _applications.Add((me.UserDbId, targetId.Value));
        a.Client(ClientAction.Def(targetTicket, "S_OTHER_USER_APPLY_PARTY", new Dictionary<string, object>
        {
            ["unk1"] = (byte)0,                       // PartyMatchInfo+0x98; meaning unknown
            ["pid"] = me.UserDbId,
            ["class"] = (short)me.Class,              // the writer's order is class, race, gender, level
            ["race"] = (short)me.Race,
            ["gender"] = (short)me.Gender,
            ["level"] = (short)me.Level,
            ["unk2"] = (byte)0,                       // bit 4 of Account+0x2adc; meaning unknown
            ["name"] = me.Name,
        }));
        return a;
    }

    /// <summary>C_PARTY_APPLICATION_DENIED (0x4F00): drop the pending edge. The real Arbiter's
    /// handler (FUN_1404e3f60) queues an async job; nothing reaches World.</summary>
    private PartyActions DenyApplication(PartyActions a, in PartyPlayer me, byte[] body)
    {
        if (body.Length < 4) return a.Reject("C_PARTY_APPLICATION_DENIED: short body");
        int applicant = BitConverter.ToInt32(body, 0);
        if (!_applications.Remove((applicant, me.UserDbId)))
            return a.Reject($"C_PARTY_APPLICATION_DENIED: no application from {applicant}");
        return a;
    }

    /// <summary>
    /// C_DISMISS_PARTY (0xC8B9) - empty body. Handler FUN_1404def50 (Arb_part_041.c:1438) checks
    /// the caller IS the manager, then sends AS_DISMISS_PARTY (0x13BA) with the online member
    /// count. It is a REQUEST: World runs the vote and answers SA_DISMISS_PARTY. The real handler
    /// also enforces a world-majority rule we cannot evaluate here (it needs
    /// IsSameWorldPartyMember), so that check stays World's.
    /// </summary>
    private PartyActions DismissParty(PartyActions a, in PartyPlayer me)
    {
        var party = FindByMember(me.UserDbId);
        if (party == null) return a.Reject("C_DISMISS_PARTY: not in a party");
        if (!party.IsManager(me.UserDbId)) return a.Reject("C_DISMISS_PARTY: only the manager may dismiss");
        a.World(PartyPackets.AS_DISMISS_PARTY,
            PartyPackets.BuildAsDismissParty(PlanetId, me.UserDbId, OnlineCount(party)));
        return a;
    }

    /// <summary>C_PARTY_LOOTING_METHOD (0x5D24) -> AS_PARTY_LOOTING_METHOD (0x13BB). Also a
    /// request; World answers SA_CHANGE_LOOTING_METHOD and only then does anything change.</summary>
    private PartyActions RequestLootingMethod(PartyActions a, in PartyPlayer me, byte[] body)
    {
        var loot = PartyPackets.ParseCPartyLootingMethod(body);
        if (loot == null) return a.Reject("C_PARTY_LOOTING_METHOD: short body");
        var party = FindByMember(me.UserDbId);
        if (party == null) return a.Reject("C_PARTY_LOOTING_METHOD: not in a party");
        a.World(PartyPackets.AS_PARTY_LOOTING_METHOD,
            PartyPackets.BuildAsPartyLootingMethod(PlanetId, me.UserDbId, loot.Value, OnlineCount(party)));
        return a;
    }

    /// <summary>C_BAN_PARTY_MEMBER (0x59C1) -> AS_BAN_PARTY_MEMBER (0x13BC). World runs the vote
    /// and answers SA_KICK_PARTY.</summary>
    private PartyActions RequestBan(PartyActions a, in PartyPlayer me, byte[] body)
    {
        var t = PartyPackets.ParseCBanPartyMember(body);
        if (t == null) return a.Reject("C_BAN_PARTY_MEMBER: short body");
        var party = FindByMember(me.UserDbId);
        if (party == null) return a.Reject("C_BAN_PARTY_MEMBER: not in a party");
        if (party.IndexOf((int)t.Value.playerId) < 0) return a.Reject("C_BAN_PARTY_MEMBER: target is not a member");
        a.World(PartyPackets.AS_BAN_PARTY_MEMBER, PartyPackets.BuildAsBanPartyMember(
            PlanetId, me.UserDbId, (int)t.Value.serverId, (int)t.Value.playerId, OnlineCount(party)));
        return a;
    }

    /// <summary>
    /// C_MERGE_PARTY_TO_RAID (0xB8D0): [i64 partyId][u8 accept]. Handler FUN_1404e3870
    /// (Arb_part_041.c:4710) acts only when accept != 0, and does the whole merge INSIDE the
    /// Arbiter: LeavePartyMember(me, -1), FindPartyById(partyId), then
    /// AddPartyMember(thatManager, me, raid: true).
    /// </summary>
    private PartyActions MergeToRaid(PartyActions a, in PartyPlayer me, byte[] body)
    {
        var m = PartyPackets.ParseCMergePartyToRaid(body);
        if (m == null) return a.Reject("C_MERGE_PARTY_TO_RAID: short body");
        if (!m.Value.accept) return a;                                  // declined: nothing happens
        var target = FindById(m.Value.partyId);
        if (target == null) return a.Reject($"C_MERGE_PARTY_TO_RAID: no party 0x{m.Value.partyId:X}");

        var mine = FindByMember(me.UserDbId);
        if (mine != null && mine.Id == target.Id) return a.Reject("C_MERGE_PARTY_TO_RAID: already in that party");
        if (mine != null)
        {
            // Leave the old party properly first - LeavePartyMember(me, -1) in the real handler.
            RemoveMember(a, mine, me.UserDbId, tellLeaver: false);
            a.World(PartyPackets.AS_DO_REMOVE_PARTY_MEMBER,
                PartyPackets.BuildDoRemovePartyMember(mine.Id, PlanetId, me.UserDbId));
            FinishAfterRemoval(a, mine);
        }

        target.Raid = true;
        if (!AddMember(a, target, me)) return a.Reject("C_MERGE_PARTY_TO_RAID: target party is full");
        a.World(PartyPackets.AS_DO_EXTEND_PARTY, PartyPackets.BuildDoExtendParty(target.Id, partyToRaid: true));
        a.World(PartyPackets.AS_DO_ADD_PARTY_MEMBER,
            PartyPackets.BuildDoAddPartyMember(target.Id, MemberOf(me, canInvite: false)));
        BroadcastMemberList(a, target);
        return a;
    }

    // =========================================================================================
    // World -> Arbiter
    // =========================================================================================

    public PartyActions OnWorldFrame(ushort opcode, byte[] payload)
    {
        var a = new PartyActions();
        switch (opcode)
        {
            case PartyPackets.SA_JOIN_PARTY: return JoinParty(a, payload);
            case PartyPackets.SA_JOIN_PARTY_IN_ARBITER: return JoinPartyInArbiter(a, payload);
            case PartyPackets.SA_LEAVE_PARTY: return LeaveParty(a, payload);
            case PartyPackets.SA_DISMISS_PARTY: return DismissFromWorld(a, payload);
            case PartyPackets.SA_KICK_PARTY: return KickFromWorld(a, payload);
            case PartyPackets.SA_EXTEND_PARTY: return ExtendFromWorld(a, payload);
            case PartyPackets.SA_CHANGE_PARTY_MANAGER: return ChangeManagerFromWorld(a, payload);
            case PartyPackets.SA_CHANGE_LOOTING_METHOD: return ChangeLootingFromWorld(a, payload);
            case PartyPackets.SA_BYPASS_TO_GROUP: return BypassToGroup(a, payload);
            default: return a.Reject($"0x{opcode:X4} is not a party frame");
        }
    }

    /// <summary>
    /// SA_JOIN_PARTY (0x1395) - the authoritative "these two agreed". There is no C_ACCEPT_PARTY
    /// or S_ASK_JOIN_PARTY in 376012: the direct-invite dialogue runs through C_ASK_INTERACTIVE /
    /// S_ANSWER_INTERACTIVE and is completed by WORLD, which then tells us
    /// (PARTY-DESIGN.md section 6.1). So this is where a party is born.
    /// </summary>
    private PartyActions JoinParty(PartyActions a, byte[] payload)
    {
        var j = PartyPackets.ParseSaJoinParty(payload);
        if (j == null) return a.Reject("SA_JOIN_PARTY: frame shorter than 0x56");
        var v = j.Value;
        if (!_ticketByDbId.TryGetValue(v.MemberDbId, out _)) return a.Reject($"SA_JOIN_PARTY: inviter {v.MemberDbId} is not online");
        if (!_ticketByDbId.TryGetValue(v.InviteeDbId, out _)) return a.Reject($"SA_JOIN_PARTY: invitee {v.InviteeDbId} is not online");
        var inviter = _byTicket[_ticketByDbId[v.MemberDbId]];
        var invitee = _byTicket[_ticketByDbId[v.InviteeDbId]];
        if (FindByMember(invitee.UserDbId) != null) return a.Reject("SA_JOIN_PARTY: invitee is already in a party");

        _applications.Remove((invitee.UserDbId, inviter.UserDbId));
        _applications.Remove((inviter.UserDbId, invitee.UserDbId));

        return JoinCore(a, inviter, invitee, v.Raid, v.IsAnonymous, v.PartyType);
    }

    /// <summary>
    /// SA_JOIN_PARTY_IN_ARBITER (0x13AB) - the path TERA 100.02 actually takes. The direct-invite
    /// dialogue is completed by World, which then names the two characters BY NAME and leaves the
    /// party itself to us: cap_social.log has one 0x13AB (seq 747) and no SA_JOIN_PARTY at all,
    /// and seq 748 is the AS_DO_CREATE_PARTY that came straight back out of it.
    ///
    /// T65: before this case existed the frame fell through to the default reject
    /// ("0x13AB is not a party frame") and the party silently never formed even though the
    /// contract accept (0x280F/0x2810) had already gone out.
    /// </summary>
    private PartyActions JoinPartyInArbiter(PartyActions a, byte[] payload)
    {
        var j = PartyPackets.ParseSaJoinPartyInArbiter(payload);
        if (j == null) return a.Reject("SA_JOIN_PARTY_IN_ARBITER: frame shorter than 0x0F");
        var v = j.Value;
        if (!TryGetPlayerByName(v.MemberName, out var inviter))
            return a.Reject($"SA_JOIN_PARTY_IN_ARBITER: inviter '{v.MemberName}' is not online");
        if (!TryGetPlayerByName(v.InviteeName, out var invitee))
            return a.Reject($"SA_JOIN_PARTY_IN_ARBITER: invitee '{v.InviteeName}' is not online");
        if (inviter.UserDbId == invitee.UserDbId)
            return a.Reject("SA_JOIN_PARTY_IN_ARBITER: inviter and invitee are the same character");
        if (FindByMember(invitee.UserDbId) != null)
            return a.Reject("SA_JOIN_PARTY_IN_ARBITER: invitee is already in a party");

        _applications.Remove((invitee.UserDbId, inviter.UserDbId));
        _applications.Remove((inviter.UserDbId, invitee.UserDbId));

        // 0x13AB carries only Raid - no PartyType and no anonymity flag - so a party born this
        // way is an ordinary named one, which is what seq 748 shows.
        return JoinCore(a, inviter, invitee, v.Raid, isAnonymous: false, partyType: 0);
    }

    /// <summary>
    /// The half both join paths share: create-or-extend, then the AS_ mirror World needs.
    /// </summary>
    private PartyActions JoinCore(PartyActions a, PartyPlayer inviter, PartyPlayer invitee,
        bool raid, bool isAnonymous, int partyType)
    {
        var party = FindByMember(inviter.UserDbId);
        if (party == null)
        {
            // PartyManager::New_CreateParty refuses fewer than two members, and the manager is
            // the first member in the vector - the inviter.
            party = new Party { Id = NextPartyId(), Raid = raid, IsAnonymous = isAnonymous, PartyType = partyType };
            party.ManagerDbId = inviter.UserDbId;
            _byId[party.Id] = party;
            AddMember(a, party, inviter);
            if (!AddMember(a, party, invitee)) return a.Reject("SA_JOIN_PARTY: new party is full");

            // The mirror World needs for loot/exp/instances: the whole member list at once.
            a.World(PartyPackets.AS_DO_CREATE_PARTY, PartyPackets.BuildDoCreateParty(
                party.Id, party.OwnerPlanetId, party.ManagerPlanetId, party.ManagerDbId,
                party.MaxMembers, party.PartyType, dungeonClearCompensation: false, dungeonId: 0,
                raid: party.Raid, teamIndex: 0, battleFieldId: 0,
                members: party.Members().ToList()));

            // T64, from cap_social.log seq 748 -> 751..753: the real Arbiter follows
            // AS_DO_CREATE_PARTY with AS_CHANGE_EVENT_MATCHING_STATE(IsMatching = 0) and
            // AS_REQUEST_REFRESH_PARTY_INFO, per member, in that order. Joining a party ends
            // solo matching, and World will not refresh its party UI without the second one.
            // (It sent three 0x15CD for the first member and two for the second - one per
            // matching queue, we assume; we send one each, which is all the capture
            // justifies for members we never put in a queue. PARTY-DESIGN.md section 13.)
            foreach (var m in party.Members())
                a.World(PartyPackets.AS_CHANGE_EVENT_MATCHING_STATE,
                    PartyPackets.BuildAsChangeEventMatchingState(m.UserDbId, isMatching: false));
            foreach (var m in party.Members())
                a.World(PartyPackets.AS_REQUEST_REFRESH_PARTY_INFO,
                    PartyPackets.BuildAsRequestRefreshPartyInfo(m.UserDbId));

            _log.LogInformation("Party 0x{Id:X} created: {A} + {B}", party.Id, inviter.Name, invitee.Name);
        }
        else
        {
            if (!AddMember(a, party, invitee)) return a.Reject("SA_JOIN_PARTY: party is full");
            a.World(PartyPackets.AS_DO_ADD_PARTY_MEMBER,
                PartyPackets.BuildDoAddPartyMember(party.Id, MemberOf(invitee, canInvite: false)));
            _log.LogInformation("Party 0x{Id:X}: {B} joined ({N} members)", party.Id, invitee.Name, party.Count);
        }

        BroadcastMemberList(a, party);
        return a;
    }

    /// <summary>SA_LEAVE_PARTY (0x1396). C_LEAVE_PARTY has no Arbiter handler - the button goes
    /// to World and comes back here.</summary>
    private PartyActions LeaveParty(PartyActions a, byte[] payload)
    {
        var l = PartyPackets.ParseSaLeaveParty(payload);
        if (l == null) return a.Reject("SA_LEAVE_PARTY: short frame");
        var party = FindById(l.Value.PartyId) ?? FindByMember(l.Value.MemberDbId);
        if (party == null) return a.Reject($"SA_LEAVE_PARTY: no party 0x{l.Value.PartyId:X}");
        if (party.IndexOf(l.Value.MemberDbId) < 0) return a.Reject("SA_LEAVE_PARTY: not a member");

        RemoveMember(a, party, l.Value.MemberDbId, tellLeaver: true);
        a.World(PartyPackets.AS_DO_REMOVE_PARTY_MEMBER,
            PartyPackets.BuildDoRemovePartyMember(party.Id, PlanetId, l.Value.MemberDbId));
        FinishAfterRemoval(a, party);
        return a;
    }

    /// <summary>SA_DISMISS_PARTY (0x1397) - World accepted the dismiss request.</summary>
    private PartyActions DismissFromWorld(PartyActions a, byte[] payload)
    {
        var d = PartyPackets.ParseSaPartyActor(PartyPackets.SA_DISMISS_PARTY, payload);
        if (d == null) return a.Reject("SA_DISMISS_PARTY: short frame");
        var party = FindByMember(d.Value.MemberDbId);
        if (party == null) return a.Reject("SA_DISMISS_PARTY: not in a party");
        Dissolve(a, party, "dismissed");
        return a;
    }

    /// <summary>SA_KICK_PARTY (0x1398) - World accepted the ban vote.</summary>
    private PartyActions KickFromWorld(PartyActions a, byte[] payload)
    {
        var k = PartyPackets.ParseSaKickParty(payload);
        if (k == null) return a.Reject("SA_KICK_PARTY: short frame");
        var party = FindByMember(k.Value.MemberDbId);
        if (party == null) return a.Reject("SA_KICK_PARTY: requester is not in a party");
        int slot = party.IndexOf(k.Value.TargetDbId);
        if (slot < 0) return a.Reject("SA_KICK_PARTY: target is not a member");

        var target = party.Slots[slot]!.Value;
        party.Slots[slot] = null;
        _byMember.Remove(target.UserDbId);

        if (_ticketByDbId.TryGetValue(target.UserDbId, out uint tt))
            a.Client(ClientAction.Def(tt, "S_BAN_PARTY", new Dictionary<string, object>()));
        foreach (var (t, _) in OnlineMembersOf(party))
            a.Client(ClientAction.Def(t, "S_BAN_PARTY_MEMBER", new Dictionary<string, object>
            {
                ["serverId"] = (uint)PlanetId,
                ["playerId"] = (uint)target.UserDbId,
                ["unk1"] = -1,                        // "Always FFFFFFFF ?" per the .def
                ["name"] = target.Name,
            }));
        a.World(PartyPackets.AS_DO_REMOVE_PARTY_MEMBER,
            PartyPackets.BuildDoRemovePartyMember(party.Id, PlanetId, target.UserDbId));
        FinishAfterRemoval(a, party);
        return a;
    }

    /// <summary>SA_EXTEND_PARTY (0x1399) - party becomes a raid, capacity 5 -> 30.</summary>
    private PartyActions ExtendFromWorld(PartyActions a, byte[] payload)
    {
        var e = PartyPackets.ParseSaExtendParty(payload);
        if (e == null) return a.Reject("SA_EXTEND_PARTY: short frame");
        var party = FindByMember(e.Value.MemberDbId);
        if (party == null) return a.Reject("SA_EXTEND_PARTY: not in a party");
        party.Raid = e.Value.PartyToRaid;
        a.World(PartyPackets.AS_DO_EXTEND_PARTY, PartyPackets.BuildDoExtendParty(party.Id, party.Raid));
        BroadcastMemberList(a, party);
        return a;
    }

    /// <summary>SA_CHANGE_PARTY_MANAGER (0x139B).</summary>
    private PartyActions ChangeManagerFromWorld(PartyActions a, byte[] payload)
    {
        var c = PartyPackets.ParseSaChangeManager(payload);
        if (c == null) return a.Reject("SA_CHANGE_PARTY_MANAGER: short frame");
        var party = FindByMember(c.Value.MemberDbId);
        if (party == null) return a.Reject("SA_CHANGE_PARTY_MANAGER: not in a party");
        int slot = party.IndexOf(c.Value.NewManagerDbId);
        if (slot < 0) return a.Reject("SA_CHANGE_PARTY_MANAGER: the new manager is not a member");

        party.ManagerPlanetId = c.Value.NewManagerPlanetId;
        party.ManagerDbId = c.Value.NewManagerDbId;
        var nm = party.Slots[slot]!.Value;
        foreach (var (t, _) in OnlineMembersOf(party))
            a.Client(ClientAction.Def(t, "S_CHANGE_PARTY_MANAGER", new Dictionary<string, object>
            {
                ["serverId"] = (uint)party.ManagerPlanetId,
                ["playerId"] = (uint)party.ManagerDbId,
                ["name"] = nm.Name,
            }));
        a.World(PartyPackets.AS_DO_SET_PARTY_MANAGER,
            PartyPackets.BuildDoSetPartyManager(party.Id, party.ManagerPlanetId, party.ManagerDbId));
        return a;
    }

    /// <summary>SA_CHANGE_LOOTING_METHOD (0x139D) - the authoritative answer to our 0x13BB.</summary>
    private PartyActions ChangeLootingFromWorld(PartyActions a, byte[] payload)
    {
        var c = PartyPackets.ParseSaChangeLooting(payload);
        if (c == null) return a.Reject("SA_CHANGE_LOOTING_METHOD: short frame");
        var party = FindByMember(c.Value.MemberDbId);
        if (party == null) return a.Reject("SA_CHANGE_LOOTING_METHOD: not in a party");

        party.Loot = c.Value.Loot;
        foreach (var (t, _) in OnlineMembersOf(party))
            a.Client(ClientAction.Def(t, "S_PARTY_LOOTING_METHOD", LootFields(party.Loot)));
        a.World(PartyPackets.AS_DO_SET_LOOTING_METHOD,
            PartyPackets.BuildDoSetLootingMethod(party.Id, party.Loot));
        return a;
    }

    /// <summary>
    /// SA_BYPASS_TO_GROUP (0x13F8) - World handing us a finished client packet and asking us to
    /// fan it out to a party, because the Arbiter owns the member list.
    /// Handler FUN_140721360 (Arb_part_062.c:3820) -> PartyManager::BroadcastPacketToParty
    /// (FUN_14090ed50) -> Party::BroadcastPacket (FUN_1407b71e0, Arb_part_067.c:4370), which
    /// walks the 30 slots and unicasts to every member whose UserDbId is non-zero and whose
    /// PlanetId is this Arbiter's, skipping the originator unless GroupType == 1.
    /// The PlanetId guard means members on another planet are not unicast here - with one planet
    /// it is always true, but the check is kept so the behaviour is the real one.
    /// </summary>
    private PartyActions BypassToGroup(PartyActions a, byte[] payload)
    {
        var g = PartyPackets.ParseSaBypassToGroup(payload);
        if (g == null) return a.Reject("SA_BYPASS_TO_GROUP: short or malformed frame");
        var v = g.Value;
        var party = FindById(v.GroupId);
        if (party == null) return a.Reject($"SA_BYPASS_TO_GROUP: no party 0x{v.GroupId:X}");

        bool skipOriginator = v.GroupType != 1;
        int sent = 0;
        foreach (var m in party.Members())
        {
            if (m.PlanetId != PlanetId) continue;                            // another planet's member
            if (skipOriginator && m.PlanetId == v.ObjectPlanetId && m.UserDbId == v.ObjectId) continue;
            if (!_ticketByDbId.TryGetValue(m.UserDbId, out uint t)) continue; // offline: nothing to send to
            a.Client(ClientAction.Raw(t, v.Packet));
            sent++;
        }
        _log.LogDebug("0x13F8: party 0x{Id:X} fan-out to {N} member(s), {Len}-byte client packet",
            party.Id, sent, v.Packet.Length);
        return a;
    }

    // =========================================================================================
    // shared state changes
    // =========================================================================================

    private int OnlineCount(Party p)
    {
        int n = 0;
        foreach (var m in p.Members()) if (_ticketByDbId.ContainsKey(m.UserDbId)) n++;
        return n;
    }

    private bool AddMember(PartyActions a, Party party, in PartyPlayer p)
    {
        if (party.IndexOf(p.UserDbId) >= 0) return true;
        int slot = party.FindEmptySlot();
        if (slot < 0) return false;
        party.Slots[slot] = MemberOf(p, canInvite: party.IsManager(p.UserDbId));
        _byMember[p.UserDbId] = party;
        return true;
    }

    private void RemoveMember(PartyActions a, Party party, int userDbId, bool tellLeaver)
    {
        int slot = party.IndexOf(userDbId);
        if (slot < 0) return;
        var gone = party.Slots[slot]!.Value;
        party.Slots[slot] = null;
        _byMember.Remove(userDbId);

        if (tellLeaver && _ticketByDbId.TryGetValue(userDbId, out uint lt))
            a.Client(ClientAction.Def(lt, "S_LEAVE_PARTY", new Dictionary<string, object>()));
        foreach (var (t, _) in OnlineMembersOf(party))
            a.Client(ClientAction.Def(t, "S_LEAVE_PARTY_MEMBER", new Dictionary<string, object>
            {
                ["serverId"] = (uint)PlanetId,
                ["playerId"] = (uint)userDbId,
                ["name"] = gone.Name,
            }));
    }

    /// <summary>
    /// A party of one is not a party: PartyManager::New_CreateParty refuses to make one and the
    /// last member is dropped the same way a dismiss drops everyone.
    /// </summary>
    private void FinishAfterRemoval(PartyActions a, Party party)
    {
        if (party.Count < 2) { Dissolve(a, party, "fell below two members"); return; }
        if (party.ManagerDbId != 0 && party.IndexOf(party.ManagerDbId) < 0)
        {
            // The manager left. The real Arbiter promotes; the wire effect is the same packet
            // World's SA_CHANGE_PARTY_MANAGER would produce, so emit it and mirror it.
            var next = party.Members().First();
            party.ManagerDbId = next.UserDbId;
            party.ManagerPlanetId = next.PlanetId;
            int i = party.IndexOf(next.UserDbId);
            party.Slots[i] = party.Slots[i]!.Value with { CanInvite = true };
            foreach (var (t, _) in OnlineMembersOf(party))
                a.Client(ClientAction.Def(t, "S_CHANGE_PARTY_MANAGER", new Dictionary<string, object>
                {
                    ["serverId"] = (uint)party.ManagerPlanetId,
                    ["playerId"] = (uint)party.ManagerDbId,
                    ["name"] = next.Name,
                }));
            a.World(PartyPackets.AS_DO_SET_PARTY_MANAGER,
                PartyPackets.BuildDoSetPartyManager(party.Id, party.ManagerPlanetId, party.ManagerDbId));
        }
        BroadcastMemberList(a, party);
    }

    private void Dissolve(PartyActions a, Party party, string why)
    {
        foreach (var (t, _) in OnlineMembersOf(party))
            a.Client(ClientAction.Def(t, "S_LEAVE_PARTY", new Dictionary<string, object>()));
        foreach (var m in party.Members()) _byMember.Remove(m.UserDbId);
        for (int i = 0; i < party.Slots.Length; i++) party.Slots[i] = null;
        _byId.Remove(party.Id);
        a.World(PartyPackets.AS_DO_DISMISS_PARTY, PartyPackets.BuildDoDismissParty(party.Id));
        _log.LogInformation("Party 0x{Id:X} {Why}", party.Id, why);
    }

    private void BroadcastMemberList(PartyActions a, Party party)
    {
        var fields = MemberListFields(party);
        foreach (var (t, _) in OnlineMembersOf(party))
            a.Client(ClientAction.Def(t, "S_PARTY_MEMBER_LIST", fields));
    }

    // =========================================================================================
    // S_ field dictionaries (S_PARTY_MEMBER_LIST.8.def / S_PARTY_LOOTING_METHOD.1.def)
    // =========================================================================================

    public static Dictionary<string, object> LootFields(in PartyPackets.LootSettings s) => new()
    {
        ["methodLoot"] = s.Method,
        ["rareGrade"] = s.RareGradeForDicing,
        ["methodRare"] = s.RareItemDistributionMethod,
        ["rareEquipment"] = s.EquipmentForDicing,
        ["rareClass"] = s.FindClassForDicing,
        ["methodBound"] = s.BoundOnLootItemDistributionMethod,
        ["noCombat"] = s.ForbidLootingInBattle,
    };

    /// <summary>
    /// S_PARTY_MEMBER_LIST (0x8BC6). Encoder FUN_1407b39e0 (Arb_part_067.c:1939); the shipped
    /// .def to use is version 8, NOT 7. Sources: ims &lt;- Party+0x78, raid &lt;- +0x79,
    /// memberLimit &lt;- +0xD0, id &lt;- +0x80, leader &lt;- +0xC0/+0xC4,
    /// loot &lt;- +0xA8,+0xAC,+0xB4,+0xB5,+0xB0,+0xB8,+0xBC, anonymized &lt;- +0xD4.
    /// `slot` is the loop index for a party and Party::GetIndex(PDId) for a raid, chosen on the
    /// raid flag at Party+0x79 - which for us is the same number either way, because we never
    /// compact the slot array.
    /// </summary>
    public Dictionary<string, object> MemberListFields(Party party)
    {
        var members = new List<object>();
        int loopIndex = 0;
        for (int i = 0; i < party.Slots.Length; i++)
        {
            if (party.Slots[i] is not { } m) continue;
            members.Add(new Dictionary<string, object>
            {
                ["serverId"] = (uint)m.PlanetId,
                ["playerId"] = (uint)m.UserDbId,
                ["level"] = (uint)m.Level,
                ["class"] = (uint)m.Class,
                ["online"] = m.Online,
                ["gameId"] = m.GameId & 0x7FFFFFFFFFFFFFFFUL,
                ["slot"] = party.Raid ? i : loopIndex,
                ["canInvite"] = m.CanInvite,
                ["laurel"] = (uint)m.AchievementGrade,
                ["awakeningLevel"] = m.AwakenGrade,
                ["name"] = m.Name,
            });
            loopIndex++;
        }
        return new Dictionary<string, object>
        {
            ["ims"] = false,                          // Party+0x78 IsSysParty; matchmaking only
            ["raid"] = party.Raid,
            ["memberLimit"] = (uint)party.MaxMembers,
            ["id"] = (ulong)party.Id,
            ["leader"] = new Dictionary<string, object>
            {
                ["serverId"] = (uint)party.ManagerPlanetId,
                ["playerId"] = (uint)party.ManagerDbId,
            },
            ["lootSettings"] = new Dictionary<string, object>
            {
                ["mode"] = (uint)party.Loot.Method,
                ["distributeMinRarity"] = (uint)party.Loot.RareGradeForDicing,
                ["distributeAllEquipment"] = party.Loot.EquipmentForDicing,
                ["distributeOnlyReqClass"] = party.Loot.FindClassForDicing,
                ["distributeMode"] = (uint)party.Loot.RareItemDistributionMethod,
                ["distributeModeBoP"] = (uint)party.Loot.BoundOnLootItemDistributionMethod,
                ["disableCombatLooting"] = party.Loot.ForbidLootingInBattle,
            },
            ["anonymized"] = party.IsAnonymous,
            ["members"] = members,
        };
    }
}
