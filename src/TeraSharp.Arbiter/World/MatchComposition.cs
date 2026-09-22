// SPDX-License-Identifier: MIT
// Copyright (c) 2026 the TeraSharp contributors

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
// T157: the battleground ids and team sizes are no longer a table here - they are read from
// BattleFieldData.xml (BattleFieldSheet), and only the stated per-type rules stay in code.
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
    /// class id (Warrior 0 .. Glaiver 12 - <c>FUN_140065e00</c>, Arb_part_003.c:2179). T159:
    /// read from the sheet (<see cref="DatasheetLoader.ClassPositions"/>); this is the table as
    /// transcribed, used only when the sheet is missing.
    /// </summary>
    public static ClassPosition[] ClassPositions => DatasheetLoader.ClassPositions.Value;

    /// <summary>The transcribed <c>&lt;ClassPosition&gt;</c> - the built-in for <see cref="ClassPositions"/>.</summary>
    public static readonly ClassPosition[] BuiltInClassPositions =
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

    // ---- the queue window's position choice (T138f) ------------------------------------

    /// <summary>
    /// C_MATCH_ADD's trailing int32, decoded. T138c read it as "1 on the first queue, 0 on the
    /// second"; it is the POSITION the player picked in the matching window, in the datasheet's
    /// own numbering, and the coincidence was that the same player queued twice.
    ///
    /// <para>Four captured frames, and one of them settles it:</para>
    /// <code>
    ///   classic_live3  8643   Elin Warrior (11001)    1   DPS   -> cancelled at 9476
    ///   classic_live3 10322   the SAME character      0   Tank  -> matched at 10585
    ///   cap_multiworld 3889   Castanic Glaiver        1   DPS
    ///   cap_social4    5294   Human Warrior           1   DPS
    /// </code>
    /// <para>The Warrior's two queues differ in this field and nothing else, and
    /// S_SYS_PARTY_INFO record 10587 puts him in the party as position <b>0, a tank</b> - his
    /// SECOND position, not his default. He cancelled a DPS queue and re-queued as a tank, which
    /// is both the confirmation and the reason the choice is binding: the server did not move
    /// him, he had to ask again.</para>
    /// <para><b>2 (healer) is never observed</b> - the only healers in the captures are Priest
    /// and Elementalist, which have no second position to choose - so that row is the numbering,
    /// not a sample. This table is the one line to change if a later capture disagrees.</para>
    /// </summary>
    public static readonly MatchRole[] ChoiceToRole = { MatchRole.Tank, MatchRole.Dps, MatchRole.Healer };

    /// <summary>The value the client sends when it states no position. Ours, not the wire's -
    /// every captured frame names one, so this is what an absent or short array reads as.</summary>
    public const int NoChoice = -1;

    /// <summary>
    /// The position <paramref name="value"/> names, or null when it names none.
    /// </summary>
    public static MatchRole? ChosenRole(int value)
        => (uint)value < (uint)ChoiceToRole.Length ? ChoiceToRole[value] : null;

    /// <summary>
    /// Whether this class may be put in <paramref name="role"/> given what the player ASKED for.
    /// A stated position the class can actually fill is binding - a Warrior who queued as DPS is
    /// not seated as a tank, which is what classic_live3 shows. A stated position the class
    /// cannot fill is ignored rather than obeyed: a client may narrow its own options, never
    /// widen them.
    /// </summary>
    public static bool CanFill(int classId, MatchRole role, int level, int chosen)
    {
        if (!CanFill(classId, role, level)) return false;
        return ChosenRole(chosen) is not MatchRole pick
               || !CanFill(classId, pick, level)
               || pick == role;
    }

    /// <summary>
    /// The position a queuer is treated as when nothing is matching on it - the S_SYS_PARTY_INFO
    /// slot for a battleground team, and the pool-add tail the real server echoes back (record
    /// 8662 carries 1 for the DPS queue, record 10333 carries 0 for the tank one). Their own
    /// choice when they made one they can fill, else the class default.
    /// </summary>
    public static MatchRole EffectiveRole(int classId, int level, int chosen)
        => ChosenRole(chosen) is MatchRole pick && CanFill(classId, pick, level)
            ? pick : RoleOf(classId);

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
    /// T157. The team rules the T138 brief stated, by <c>BattleFieldData.xml</c> TYPE - the one
    /// part of a battleground's rule the sheet does not carry. Ids, team sizes and whether a
    /// battleground exists at all come from the sheet (<see cref="BattleFieldSheet"/>), so the
    /// four Skyrings, four Corsairs and two Fraywinds the sheet has today all take their type's
    /// rule, and one taken out of the sheet is out of the matcher.
    /// <para>Round_PvP (Champions' Skyring): at most one lancer and EXACTLY one healer - the
    /// brief's "1 mystic or 1 priest per team" - and <c>MaxPerClass = 2</c>, the other half of
    /// the same rule ("every other class can have doubles"). StrongholdOccupation (Corsairs'
    /// Stronghold): at least three healers. Cannon (Fraywind Canyon): at most two lancers, at
    /// least two healers. Any other type takes <see cref="MaxHealersVariable"/> /
    /// <see cref="MaxTanksVariable"/>.</para>
    /// </summary>
    public static readonly IReadOnlyDictionary<string, (int MinHealers, int MaxHealers, int MaxLancers, int MaxPerClass)> TypeRules =
        new Dictionary<string, (int, int, int, int)>(StringComparer.Ordinal)
        {
            ["Round_PvP"] = (1, 1, 1, 2),
            ["StrongholdOccupation"] = (3, 0, 0, 0),
            ["Cannon"] = (2, 0, 2, 0),
        };

    /// <summary>
    /// The battlegrounds with a stated rule of their own, one per sheet row whose type is in
    /// <see cref="TypeRules"/>, in sheet order. Empty when the sheet could not be read.
    /// </summary>
    public static IReadOnlyList<BattlegroundRule> Battlegrounds
    {
        get
        {
            var list = new List<BattlegroundRule>();
            foreach (var e in BattleFieldSheet.Current ?? Array.Empty<BattleFieldEntry>())
                if (TypeRules.ContainsKey(e.Type)) list.Add(RuleFrom(e));
            return list;
        }
    }

    private static BattlegroundRule RuleFrom(BattleFieldEntry e)
    {
        string name = e.Type + " " + e.Id;
        if (TypeRules.TryGetValue(e.Type, out var t))
            return new BattlegroundRule(e.Id, name, e.TeamSize, t.MinHealers, t.MaxHealers, t.MaxLancers, t.MaxPerClass);
        return new BattlegroundRule(e.Id, name, e.TeamSize,
            MinHealers: 0,
            MaxHealers: EnvCount(MaxHealersVariable, DefaultMaxHealers),
            MaxLancers: 0,
            MaxPerClass: 0);
    }

    private static int EnvCount(string name, int fallback)
    {
        var raw = Environment.GetEnvironmentVariable(name);
        return !string.IsNullOrWhiteSpace(raw) && int.TryParse(raw.Trim(), out int v) && v >= 0
            ? v : fallback;
    }

    /// <summary>
    /// The rule for <paramref name="battleFieldId"/>: its sheet row's team size with its type's
    /// stated rule, or - for a type with none (Kumas, Free_Fight, anything a future sheet adds) -
    /// the two environment caps and no floor. The environment is read on every call, so a
    /// restartless change takes. A battleground the sheet does not have (or no sheet at all)
    /// gets team size 0, which never forms: it is not offered, so it is not matched.
    /// </summary>
    public static BattlegroundRule RuleFor(int battleFieldId)
    {
        var e = BattleFieldSheet.Find(battleFieldId);
        return e != null ? RuleFrom(e)
            : new BattlegroundRule(battleFieldId, "battleground " + battleFieldId + " (not in " + BattleFieldSheet.FileName + ")",
                0, 0, 0, 0, 0);
    }

    /// <summary>Whether the sheet offers <paramref name="battleFieldId"/> at all.</summary>
    public static bool IsOffered(int battleFieldId) => BattleFieldSheet.Find(battleFieldId) != null;

    /// <summary>Whether a battleground's rule is one of <see cref="TypeRules"/> rather than the environment fallback.</summary>
    public static bool HasRule(int battleFieldId)
    {
        var e = BattleFieldSheet.Find(battleFieldId);
        return e != null && TypeRules.ContainsKey(e.Type);
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
