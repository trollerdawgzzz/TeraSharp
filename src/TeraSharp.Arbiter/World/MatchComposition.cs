namespace TeraSharp.Arbiter.World;

// =============================================================================================
// MatchComposition - who is allowed in which slot, T138c. Pure data and pure functions: no
// packets, no state, no session. MatchQueueManager calls it; the tests call it directly.
//
// WHERE THE ROLES COME FROM. The T138c brief asked for "1 tank / 1 healer / 3 DPS from
// C_MATCH_ADD's class fields". C_MATCH_ADD CARRIES NO CLASS. Four captured frames settle it:
//
//   classic_live3   8643   instances [9739]  second array = (4742, 1) TWICE
//   classic_live3  10322   instances [9739]  second array = (4742, 0) TWICE
//   cap_multiworld  3889   instances [9047]  second array = (1, 1)    TWICE
//   cap_social4     5294   instances [9047]  second array = (1003, 1) TWICE
//
// and 4742 is the PLAYER id - S_ADD_INTER_PARTY_MATCH_POOL record 8662 carries planet 2800 and
// player 0x1286 = 4742 for the same queue. The second array is the queuing player's own id,
// written twice, in a capture where he was solo; it is not a per-member class list and there is
// no field in the 55-byte frame that could be one. So the role is resolved SERVER-SIDE from
// characters.class, which TeraSharp already stores and which cannot be spoofed by a client.
//
// THE CLASS -> ROLE TABLE IS NOT INVENTED EITHER. It is
// Executable\Datasheet\DungeonMatching.xml's own <ClassPosition> block, which the real client
// and the real MatchServer share:
//
//   <!-- 1은 딜러, 0는 탱커, 2은 힐러 -->        1 = DPS, 0 = TANK, 2 = HEALER
//   <Class name="Warrior"      defaultPosition="1" secondPosition="0" thirdPosition="1"/>
//   <Class name="Lancer"       defaultPosition="0" secondPosition="0" thirdPosition="0"/>
//   <Class name="Berserker"    defaultPosition="1" secondPosition="0" thirdPosition="1"
//                              secondPositionLevel="65"/>
//   <Class name="Priest"       defaultPosition="2" secondPosition="2" thirdPosition="2"/>
//   <Class name="Elementalist" defaultPosition="2" secondPosition="2" thirdPosition="2"/>
//   <Class name="Fighter"      defaultPosition="0" secondPosition="1" thirdPosition="0"
//                              secondPositionLevel="69"/>
//   ... the remaining seven are 1/1/1.
//
// so Warrior and Berserker really can tank, Fighter (Brawler) really is a tank by default, and
// the two healers really are Priest and Elementalist (Mystic). <see cref="RoleOf"/> is the
// default position; <see cref="CanFill"/> is the whole row, including the level gate.
//
// WHAT IS STILL OURS. Each <Dungeon> row carries a `matchingRoleId` (17 for 53 of the 82 rows,
// then 23, 32, 26, 29 and eleven one-offs) and the table those ids index is inside the
// MatchServer binary, which this stack does not have. So the COMPOSITION - how many of each
// role - is <see cref="DungeonTemplate"/>'s rule and is stated as such, not passed off as
// datasheet truth.
// =============================================================================================

/// <summary>
/// A matching position, in the datasheet's own numbering: <c>0</c> tank, <c>1</c> DPS,
/// <c>2</c> healer. The numbers are the wire/datasheet values, so do not renumber them.
/// </summary>
public enum MatchRole
{
    /// <summary>탱커 - defaultPosition 0.</summary>
    Tank = 0,
    /// <summary>딜러 - defaultPosition 1.</summary>
    Dps = 1,
    /// <summary>힐러 - defaultPosition 2.</summary>
    Healer = 2,
}

/// <summary>How many of each role one formed group wants.</summary>
/// <param name="Tanks">Exact number of tanks.</param>
/// <param name="Healers">Exact number of healers.</param>
/// <param name="Dps">Everyone else.</param>
public readonly record struct RoleTemplate(int Tanks, int Healers, int Dps)
{
    /// <summary>Members in a full group.</summary>
    public int Size => Tanks + Healers + Dps;

    /// <summary>How many of <paramref name="role"/> this template wants.</summary>
    public int Want(MatchRole role) => role switch
    {
        MatchRole.Tank => Tanks,
        MatchRole.Healer => Healers,
        _ => Dps,
    };
}

/// <summary>
/// One battleground's team rule. <paramref name="MinHealers"/> and
/// <paramref name="MaxLancers"/> are the human-stated rules from the T138 brief; the sizes and
/// ids are <c>BattleFieldData.xml</c>'s.
/// </summary>
/// <param name="BattleFieldId">The <c>&lt;BattleField id&gt;</c> from the datasheet.</param>
/// <param name="Name">For log lines only.</param>
/// <param name="TeamSize"><c>&lt;CommonData maxTeamMember&gt;</c>.</param>
/// <param name="MinHealers">Refuse to start a team with fewer. 0 means no floor.</param>
/// <param name="MaxHealers">Never put more on one team. 0 means no cap.</param>
/// <param name="MaxLancers">Lancers specifically, not tanks - the brief names the class.</param>
/// <param name="MaxPerClass">At most this many of ANY one class. 0 means no limit.</param>
public readonly record struct BattlegroundRule(
    int BattleFieldId, string Name, int TeamSize,
    int MinHealers, int MaxHealers, int MaxLancers, int MaxPerClass);

/// <summary>Roles, templates and the battleground table. See the file header.</summary>
public static class MatchComposition
{
    // ---- classes -------------------------------------------------------------------------

    /// <summary>Lancer, the class the battleground rules name by name.</summary>
    public const int ClassLancer = 1;
    /// <summary>Priest.</summary>
    public const int ClassPriest = 6;
    /// <summary>Elementalist - the client calls her Mystic.</summary>
    public const int ClassElementalist = 7;
    /// <summary>Fighter - the client calls him Brawler.</summary>
    public const int ClassFighter = 10;
    /// <summary>One past the last class id (Glaiver = 12).</summary>
    public const int ClassCount = 13;

    /// <summary>
    /// One <c>&lt;Class&gt;</c> row. <paramref name="SecondLevel"/> is
    /// <c>secondPositionLevel</c>: below it the second position is not offered, which is why a
    /// level-64 Berserker cannot be slotted as a tank and a level-70 one can.
    /// </summary>
    public readonly record struct ClassPosition(
        MatchRole Default, MatchRole Second, MatchRole Third, int SecondLevel);

    /// <summary>
    /// <c>DungeonMatching.xml</c>'s <c>&lt;ClassPosition&gt;</c>, indexed by the Arbiter's own
    /// class id (Warrior 0 .. Glaiver 12 - <c>FUN_140065e00</c>, Arb_part_003.c:2179).
    /// </summary>
    public static readonly ClassPosition[] ClassPositions =
    {
        /*  0 Warrior      */ new(MatchRole.Dps,    MatchRole.Tank,   MatchRole.Dps,    0),
        /*  1 Lancer       */ new(MatchRole.Tank,   MatchRole.Tank,   MatchRole.Tank,   0),
        /*  2 Slayer       */ new(MatchRole.Dps,    MatchRole.Dps,    MatchRole.Dps,    0),
        /*  3 Berserker    */ new(MatchRole.Dps,    MatchRole.Tank,   MatchRole.Dps,   65),
        /*  4 Sorcerer     */ new(MatchRole.Dps,    MatchRole.Dps,    MatchRole.Dps,    0),
        /*  5 Archer       */ new(MatchRole.Dps,    MatchRole.Dps,    MatchRole.Dps,    0),
        /*  6 Priest       */ new(MatchRole.Healer, MatchRole.Healer, MatchRole.Healer, 0),
        /*  7 Elementalist */ new(MatchRole.Healer, MatchRole.Healer, MatchRole.Healer, 0),
        /*  8 Soulless     */ new(MatchRole.Dps,    MatchRole.Dps,    MatchRole.Dps,    0),
        /*  9 Engineer     */ new(MatchRole.Dps,    MatchRole.Dps,    MatchRole.Dps,    0),
        /* 10 Fighter      */ new(MatchRole.Tank,   MatchRole.Dps,    MatchRole.Tank,  69),
        /* 11 Assassin     */ new(MatchRole.Dps,    MatchRole.Dps,    MatchRole.Dps,    0),
        /* 12 Glaiver      */ new(MatchRole.Dps,    MatchRole.Dps,    MatchRole.Dps,    0),
    };

    /// <summary>
    /// The role a class is slotted as unless something else is needed. An id outside the table
    /// reads as DPS rather than throwing: a corrupt class is a body, not a crash.
    /// </summary>
    public static MatchRole RoleOf(int classId)
        => (uint)classId < ClassPositions.Length ? ClassPositions[classId].Default : MatchRole.Dps;

    /// <summary>
    /// Whether this class can be put in <paramref name="role"/> at
    /// <paramref name="level"/> - the default position always, the third position always, and
    /// the second only at or above <c>secondPositionLevel</c>.
    /// </summary>
    public static bool CanFill(int classId, MatchRole role, int level)
    {
        if ((uint)classId >= ClassPositions.Length) return role == MatchRole.Dps;
        var p = ClassPositions[classId];
        if (p.Default == role || p.Third == role) return true;
        return p.Second == role && level >= p.SecondLevel;
    }

    // ---- dungeon templates -----------------------------------------------------------------

    /// <summary>The five-man shape every <c>matchingRoleId="17"</c> dungeon runs.</summary>
    public static readonly RoleTemplate FiveMan = new(Tanks: 1, Healers: 1, Dps: 3);

    /// <summary>A full party before it is a raid - <c>PartyPackets.MaxPartyMembers</c>.</summary>
    public const int PartySize = 5;

    /// <summary>
    /// The composition for a group of <paramref name="size"/>. Five is 1/1/3; larger groups
    /// are RAIDS and keep the same one-in-five ratio - ten is 2/2/6, twenty is 4/4/12, thirty
    /// is 6/6/18 - which is what "raid templates by their counts" means here. Under five, the
    /// tank and the healer are the last two to go, because a group with neither is not a group.
    ///
    /// <para>This ratio is OURS. The real composition is behind <c>matchingRoleId</c>, whose
    /// table lives in the MatchServer binary (see the file header); the ratio matches what
    /// every listed dungeon actually runs, but it is a rule, not a decoded datasheet.</para>
    /// </summary>
    public static RoleTemplate DungeonTemplate(int size)
    {
        if (size <= 0) return new RoleTemplate(0, 0, 0);
        if (size == 1) return new RoleTemplate(0, 0, 1);
        if (size == 2) return new RoleTemplate(1, 1, 0);
        if (size < PartySize) return new RoleTemplate(1, 1, size - 2);
        int groups = size / PartySize;
        return new RoleTemplate(groups, groups, size - 2 * groups);
    }

    // ---- battlegrounds ---------------------------------------------------------------------

    /// <summary>Per-team healer cap when a battleground has no row of its own.</summary>
    public const string MaxHealersVariable = "TERASHARP_BG_MAX_HEALERS";
    /// <summary>Per-team tank cap when a battleground has no row of its own.</summary>
    public const string MaxTanksVariable = "TERASHARP_BG_MAX_TANKS";
    /// <summary>Default for <see cref="MaxHealersVariable"/> - the T138 brief's 2.</summary>
    public const int DefaultMaxHealers = 2;
    /// <summary>Default for <see cref="MaxTanksVariable"/> - the T138 brief's 3.</summary>
    public const int DefaultMaxTanks = 3;

    /// <summary>
    /// The battlegrounds with a rule of their own, keyed by <c>BattleFieldData.xml</c>'s
    /// <c>&lt;BattleField id&gt;</c>. The ids and the team sizes are that file's; the healer
    /// and lancer numbers are the human-stated rules from the T138 brief.
    /// <code>
    ///   Round_PvP            37, 38 (3v3) and 39, 40 (5v5)   Champions' Skyring
    ///   StrongholdOccupation 26, 27, 28, 29 (15v15)          Corsairs' Stronghold
    ///   Cannon               10, 11 (20v20)                  Fraywind Canyon
    /// </code>
    /// <para>Skyring: at most one lancer and EXACTLY one healer, which is the brief's
    /// "1 mystic or 1 priest per team" - the two healer classes share the one slot.
    /// Corsairs: at least three healers, no cap and no lancer rule. Fraywind: at most two
    /// lancers and at least two healers.</para>
    /// <para>Skyring also carries <c>MaxPerClass = 2</c>, the other half of the same stated
    /// rule - "every other class can have doubles" - so a team cannot be three brawlers either.
    /// The two big battlegrounds have no such cap: at 15 and 20 a side, capping every class at
    /// two would leave most queues unstartable.</para>
    /// </summary>
    public static readonly BattlegroundRule[] Battlegrounds =
    {
        new(37, "Champions' Skyring 3v3", 3, MinHealers: 1, MaxHealers: 1, MaxLancers: 1, MaxPerClass: 2),
        new(38, "Champions' Skyring 3v3", 3, MinHealers: 1, MaxHealers: 1, MaxLancers: 1, MaxPerClass: 2),
        new(39, "Champions' Skyring 5v5", 5, MinHealers: 1, MaxHealers: 1, MaxLancers: 1, MaxPerClass: 2),
        new(40, "Champions' Skyring 5v5", 5, MinHealers: 1, MaxHealers: 1, MaxLancers: 1, MaxPerClass: 2),
        new(26, "Corsairs' Stronghold", 15, MinHealers: 3, MaxHealers: 0, MaxLancers: 0, MaxPerClass: 0),
        new(27, "Corsairs' Stronghold", 15, MinHealers: 3, MaxHealers: 0, MaxLancers: 0, MaxPerClass: 0),
        new(28, "Corsairs' Stronghold", 15, MinHealers: 3, MaxHealers: 0, MaxLancers: 0, MaxPerClass: 0),
        new(29, "Corsairs' Stronghold", 15, MinHealers: 3, MaxHealers: 0, MaxLancers: 0, MaxPerClass: 0),
        new(10, "Fraywind Canyon", 20, MinHealers: 2, MaxHealers: 0, MaxLancers: 2, MaxPerClass: 0),
        new(11, "Fraywind Canyon", 20, MinHealers: 2, MaxHealers: 0, MaxLancers: 2, MaxPerClass: 0),
    };

    private static int EnvCount(string name, int fallback)
    {
        var raw = Environment.GetEnvironmentVariable(name);
        return !string.IsNullOrWhiteSpace(raw) && int.TryParse(raw.Trim(), out int v) && v >= 0
            ? v : fallback;
    }

    /// <summary>
    /// The rule for <paramref name="battleFieldId"/>. A battleground with no row of its own -
    /// Kumas Royale, Gridiron, anything a future datasheet adds - falls back to the two
    /// environment caps and no floor, which is the T138 brief's
    /// <see cref="MaxHealersVariable"/>/<see cref="MaxTanksVariable"/> path. The environment is
    /// read on every call, not cached, so a restartless change takes.
    /// </summary>
    public static BattlegroundRule RuleFor(int battleFieldId, int teamSize = 0)
    {
        foreach (var r in Battlegrounds) if (r.BattleFieldId == battleFieldId) return r;
        return new BattlegroundRule(battleFieldId, "battleground " + battleFieldId,
            teamSize > 0 ? teamSize : 0,
            MinHealers: 0,
            MaxHealers: EnvCount(MaxHealersVariable, DefaultMaxHealers),
            MaxLancers: 0,
            MaxPerClass: 0);
    }

    /// <summary>Whether a rule came from <see cref="Battlegrounds"/> rather than the fallback.</summary>
    public static bool HasRule(int battleFieldId)
    {
        foreach (var r in Battlegrounds) if (r.BattleFieldId == battleFieldId) return true;
        return false;
    }

    /// <summary>The per-team tank cap for a battleground with no row of its own. 0 = no cap.</summary>
    public static int FallbackMaxTanks() => EnvCount(MaxTanksVariable, DefaultMaxTanks);

    /// <summary>
    /// Whether one more player of <paramref name="classId"/> may join a team that already holds
    /// <paramref name="healers"/> healers, <paramref name="tanks"/> tanks,
    /// <paramref name="lancers"/> lancers and <paramref name="sameClass"/> of this player's
    /// own class. Only the CAPS are tested here - a floor like Corsairs' three healers cannot
    /// be checked one player at a time and is <see cref="TeamSatisfies"/>'s job.
    /// </summary>
    public static bool TeamAccepts(in BattlegroundRule rule, int classId,
                                   int healers, int tanks, int lancers, int sameClass = 0)
    {
        var role = RoleOf(classId);
        int maxTanks = HasRule(rule.BattleFieldId) ? 0 : FallbackMaxTanks();
        if (rule.MaxHealers > 0 && role == MatchRole.Healer && healers >= rule.MaxHealers) return false;
        if (maxTanks > 0 && role == MatchRole.Tank && tanks >= maxTanks) return false;
        if (rule.MaxLancers > 0 && classId == ClassLancer && lancers >= rule.MaxLancers) return false;
        if (rule.MaxPerClass > 0 && sameClass >= rule.MaxPerClass) return false;
        return true;
    }

    /// <summary>
    /// Whether a finished team meets the rule's FLOORS. A team that does not is not started -
    /// the queue keeps waiting for a healer rather than dropping fifteen people into a
    /// Corsairs' match with one.
    /// </summary>
    public static bool TeamSatisfies(in BattlegroundRule rule, IReadOnlyList<int> classIds)
    {
        if (classIds == null) return rule.MinHealers <= 0;
        int healers = 0;
        foreach (int c in classIds) if (RoleOf(c) == MatchRole.Healer) healers++;
        return healers >= rule.MinHealers;
    }
}
