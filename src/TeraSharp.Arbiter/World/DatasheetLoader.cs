// SPDX-License-Identifier: MIT
// Copyright (c) 2026 the TeraSharp contributors

using System.Xml;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TeraSharp.Arbiter.Persistence;

namespace TeraSharp.Arbiter.World;

// =============================================================================================
// DatasheetLoader - the World datasheets are the source of truth, T159.
//
// Every value TeraSharp once copied out of Executable\Datasheet\*.xml into code is read from the
// sheet here, and the copied table survives only as the BUILT-IN: what is used when the sheet
// is not on this machine. The audit is status/DATASHEETS.md; the short version:
//
//   GuildConfig.xml   <GuildSize>          guild war declare cost / declare limit / maintain cost
//   DungeonMatching.xml <ClassPosition>    the tank / healer / dps position of every class
//   DefaultSkillSet.xml <Default>          a new character's skills (99 race/gender/class rows)
//   CreateCharData.xml  <Char>/<InitItem>  a new character's items
//   CreateCharData.xml  <Char createdLevel> a new character's level (T162)
//   BattleFieldData.xml <RankingCompetition active="true">   the PvP leaderboard ids
//   BattleFieldData.xml <BattleField>   battleground ids / team sizes (T157's BattleFieldSheet, listed since T163)
//   DungeonRankRecorder_<id>.xml (names)   the PvE leaderboard ids
//   DungeonData_<id> + ContinentData + DungeonMatching + DungeonConstraint  the 98 dungeon ids
//
// Located like every other sheet: TERASHARP_DATASHEET, else <TERASHARP_DATA>\Executable\Datasheet
// (HandshakeData.DatasheetDirectory). Files are read with File.ReadAllText, which drops a BOM,
// and parsed with XDocument, which skips comments - the WorldServerList style. A sheet that is
// missing or will not parse gives the built-in, and says so: LoadAll logs one line per sheet at
// startup ("loaded X: N entries" / "X not found, using built-in") and --check-config lists them.
//
// Rule (CLAUDE.md): no new sheet-derived constant without a SheetValue here.
// =============================================================================================

/// <summary>What one sheet came out as.</summary>
public readonly record struct SheetStatus(string Sheet, bool FromSheet, int Entries, string Consumer)
{
    /// <summary>The startup / --check-config line.</summary>
    public string Line => FromSheet
        ? "loaded " + Sheet + ": " + Entries + " entries -> " + Consumer
        : Sheet + " not found, using built-in (" + Entries + " entries) -> " + Consumer;
}

/// <summary>The non-generic face of a <see cref="SheetValue{T}"/>, for the list.</summary>
public interface ISheetValue
{
    string Sheet { get; }
    SheetStatus Load(string datasheetDir);
    SheetStatus Probe(string datasheetDir);
    SheetStatus Status { get; }
    void UseBuiltIn();
}

/// <summary>
/// One sheet-backed value: read from the datasheet on first use (or by
/// <see cref="DatasheetLoader.LoadAll"/> at startup) and kept; the transcribed
/// <see cref="BuiltIn"/> when the sheet is absent or unreadable.
/// </summary>
public sealed class SheetValue<T> : ISheetValue where T : class
{
    private readonly Func<string, T?> _read;
    private readonly Func<T, int> _count;
    private readonly object _gate = new();
    private T? _value;
    private bool _loaded, _fromSheet;

    public SheetValue(string sheet, string consumer, T builtIn, Func<string, T?> read, Func<T, int> count)
    {
        Sheet = sheet; Consumer = consumer; BuiltIn = builtIn; _read = read; _count = count;
    }

    public string Sheet { get; }
    public string Consumer { get; }
    public T BuiltIn { get; }

    /// <summary>The value in use - the sheet's when it was read, else <see cref="BuiltIn"/>.</summary>
    public T Value
    {
        get
        {
            lock (_gate)
            {
                if (!_loaded)
                {
                    LoadLocked(DatasheetLoader.Directory());
                    DatasheetLoader.Report(StatusLocked());
                }
                return _value!;
            }
        }
    }

    /// <summary>Read <paramref name="datasheetDir"/> now, replacing whatever was loaded.</summary>
    public SheetStatus Load(string datasheetDir)
    {
        lock (_gate) { LoadLocked(datasheetDir); return StatusLocked(); }
    }

    /// <summary>What <paramref name="datasheetDir"/> would give, without changing the value in use.</summary>
    public SheetStatus Probe(string datasheetDir)
    {
        var v = TryRead(datasheetDir);
        return new SheetStatus(Sheet, v != null, _count(v ?? BuiltIn), Consumer);
    }

    public SheetStatus Status
    {
        get { lock (_gate) { if (!_loaded) LoadLocked(DatasheetLoader.Directory()); return StatusLocked(); } }
    }

    /// <summary>Use the built-in as if the sheet were missing (the test suite's default).</summary>
    public void UseBuiltIn()
    {
        lock (_gate) { _value = BuiltIn; _fromSheet = false; _loaded = true; }
    }

    private T? TryRead(string dir)
    {
        try { return string.IsNullOrEmpty(dir) ? null : _read(dir); }
        catch (Exception e) when (e is XmlException or IOException or UnauthorizedAccessException
                                   or FormatException or OverflowException) { return null; }
    }

    private void LoadLocked(string dir)
    {
        T? v = TryRead(dir);
        _fromSheet = v != null;
        _value = v ?? BuiltIn;
        _loaded = true;
    }

    private SheetStatus StatusLocked() => new(Sheet, _fromSheet, _count(_value!), Consumer);
}

/// <summary>One <c>&lt;GuildSize&gt;</c> row of GuildConfig.xml.</summary>
public readonly record struct GuildSizeRow(int Rank, int AccountNumOver, int DeclareCost, int MaintainCost, int DeclareLimitCount);

/// <summary>The sheets, their readers, and the startup / --check-config report.</summary>
public static class DatasheetLoader
{
    private static ILogger _log = NullLogger.Instance;

    /// <summary>Executable\Datasheet, resolved the one way every sheet reader resolves it.</summary>
    public static string Directory() => HandshakeData.DatasheetDirectory();

    /// <summary>Class names in the Arbiter's class-id order (FUN_140065e00): Warrior 0 .. Glaiver 12.</summary>
    public static readonly string[] ClassNames =
    {
        "Warrior", "Lancer", "Slayer", "Berserker", "Sorcerer", "Archer", "Priest",
        "Elementalist", "Soulless", "Engineer", "Fighter", "Assassin", "Glaiver",
    };

    /// <summary>Race names in the Arbiter's race-id order (FUN_1400c4e20).</summary>
    public static readonly string[] RaceNames = { "Human", "Highelf", "Aman", "Castanic", "Popori", "Baraka" };

    // ---- the sheets ----------------------------------------------------------------------

    /// <summary>The transcribed rank-0 row (cap_social4_client 3382 / 3389 carry 1500, 250 and 10).</summary>
    public static readonly GuildSizeRow[] BuiltInGuildSizes = { new(0, 1, 1500, 250, 10) };

    public static readonly SheetValue<GuildSizeRow[]> GuildSizes = new(
        "GuildConfig.xml <GuildSize>", "guild war declare cost / limit / maintain cost (GuildWarManager)",
        BuiltInGuildSizes, ReadGuildSizes, r => r.Length);

    public static readonly SheetValue<MatchComposition.ClassPosition[]> ClassPositions = new(
        "DungeonMatching.xml <ClassPosition>", "matching positions per class (MatchComposition)",
        MatchComposition.BuiltInClassPositions, ReadClassPositions, r => r.Length);

    public static readonly SheetValue<IReadOnlyDictionary<(int Race, int Gender, int Class), (int[] Active, int[] Passive)>> DefaultSkills = new(
        "DefaultSkillSet.xml <Default>", "new character skills (DefaultSkillSet)",
        DefaultSkillSet.BuiltInTable, ReadDefaultSkills, r => r.Count);

    public static readonly SheetValue<StarterItem[][]> StarterKits = new(
        "CreateCharData.xml <Char>", "new character items (StarterInventory)",
        StarterInventory.BuiltInKits, ReadStarterKits, r => r.Length);

    /// <summary>T162. <c>&lt;Char createdLevel&gt;</c> per class id - the level a new character is made at.</summary>
    public static readonly SheetValue<int[]> CreatedLevels = new(
        "CreateCharData.xml <Char createdLevel>", "new character level (CharacterHandlers)",
        StarterInventory.BuiltInCreatedLevels, ReadCreatedLevels, r => r.Length);

    public static readonly SheetValue<int[]> PvpBoardIds = new(
        "BattleFieldData.xml <RankingCompetition>", "PvP leaderboard ids (S_PVP_LEADER_BOARD_INFO)",
        Handlers.ArbiterClientHandlers.LeaderBoardLivePvp, ReadPvpBoardIds, r => r.Length);

    public static readonly SheetValue<int[]> PveBoardIds = new(
        "DungeonRankRecorder_*.xml", "PvE leaderboard ids (S_PVE_LEADER_BOARD_INFO)",
        Handlers.ArbiterClientHandlers.LeaderBoardLivePve, ReadPveBoardIds, r => r.Length);

    public static readonly SheetValue<int[]> DungeonTimelineIds = new(
        "DungeonData_* / ContinentData / DungeonMatching / DungeonConstraint", "post-handshake 0x1581 dungeon ids (DbProxyHandlers)",
        Array.ConvertAll(DbProxyHandlers.PostHandshakeDungeonIds, id => (int)id), HandshakeData.LoadDungeonTimelineIds, r => r.Length);

    /// <summary>T161b. Per instance id, the matching events that target it (dungeon, battleground), file order.</summary>
    public static readonly SheetValue<IReadOnlyDictionary<int, (int[] Dungeon, int[] BattleField)>> EventMatchingTargets = new(
        "EventMatching.xml <Event><TargetList>", "S_CHANGE_EVENT_MATCHING_STATE id lists (MatchQueueManager)",
        new Dictionary<int, (int[] Dungeon, int[] BattleField)>(), ReadEventMatchingTargets, r => r.Count);

    /// <summary>T184f. Default raid-group names, slots 1..6; native missing lookup returns an empty string.</summary>
    public static readonly SheetValue<string[]> RaidPartyNames = new(
        "StrSheet_BattleField.xml <String id=10000001..10000006>", "S_SEND_PARTY_NAME_LIST raid names (PartyManager)",
        Enumerable.Repeat(string.Empty, 6).ToArray(), ReadRaidPartyNames, r => r.Count(s => s.Length != 0));

    /// <summary>T184f. One party-loot row; the array keeps the value in SheetValue's reference-type cache.</summary>
    public static readonly SheetValue<PartyPackets.LootSettings[]> PartyLootDefaults = new(
        "WorldData.xml <PartyLootingOption>", "new party looting settings (PartyManager)",
        // Compatibility fallback for standalone/old capture fixtures; not the native WorldParameter constructor default.
        new[] { Party.DefaultLoot }, ReadPartyLootDefaults, r => r.Length);

    /// <summary>T172. GuardData.xml: guard id -> its &lt;Continent id&gt; (VisitedSectionInfo's 4th field).</summary>
    public static readonly SheetValue<IReadOnlyDictionary<int, int>> GuardContinents = new(
        "GuardData.xml <Continent><Guard>", "AS_UPDATE_VISITED_SECTION_LIST continent (ArbiterClientHandlers)",
        BuiltInGuardContinents(), ReadGuardContinents, r => r.Count);

    /// <summary>T172. BuyMenuData*.xml: every &lt;BuyMenu resetType="day"&gt; (id, resetTime hour), by id.</summary>
    public static readonly SheetValue<(int Id, int Hour)[]> DailyBuyMenus = new(
        "BuyMenuData*.xml <BuyMenu resetType=\"day\">", "AS_RESET_PURCHASE_LIMIT (PurchaseLimitReset)",
        BuiltInDailyMenus(), ReadDailyBuyMenus, r => r.Length);

    /// <summary>The guard's continent, -1 for a guard the sheet does not list (the real lookup's miss).</summary>
    public static int GuardContinentOf(int guardId) => GuardContinents.Value.TryGetValue(guardId, out int c) ? c : -1;
    /// <summary>T169. Per equipment template, the worn slot the tooltip compare looks at.</summary>
    public static readonly SheetValue<IReadOnlyDictionary<int, int>> EquipSlots = new(
        "ItemTemplate.xml combatItemType/category", "tooltip compare slot (ArbiterClientHandlers.CompareSlotFor)",
        new Dictionary<int, int>(), ReadEquipSlots, r => r.Count);

    public static int EquipSlotOf(int templateId) => EquipSlots.Value.TryGetValue(templateId, out int s) ? s : 0;

    /// <summary>T170. GuildConfig.xml's &lt;SurrenderReparationRate&gt;: (surrenderSize, winnerSize) -&gt;
    /// (reparationRate, limitExceedReparationRate). The built-in is the shipped sheet's 16 rows.</summary>
    public static readonly IReadOnlyDictionary<(int, int), (float Rate, float LimitExceed)> BuiltInReparationRates =
        BuildReparation(new float[,] { { 0.5f, 0.4f, 0.3f, 0.2f }, { 0.6f, 0.5f, 0.4f, 0.3f },
                                       { 0.7f, 0.6f, 0.5f, 0.4f }, { 0.8f, 0.7f, 0.6f, 0.5f } });

    public static readonly SheetValue<IReadOnlyDictionary<(int, int), (float Rate, float LimitExceed)>> ReparationRates = new(
        "GuildConfig.xml <SurrenderReparationRate>", "guild war give-up penalty (GuildWarManager.GiveUpPenalty)",
        BuiltInReparationRates, ReadReparationRates, r => r.Count);

    /// <summary>The surrendering guild pays <c>rate x the winner's declared money</c>
    /// (GuildWarManager::GetGiveUpPenaltyInfo); 0 for a pair the sheet does not list.</summary>
    public static float ReparationRate(int surrenderSize, int winnerSize, bool limitExceeded = false)
        => ReparationRates.Value.TryGetValue((surrenderSize, winnerSize), out var r) ? (limitExceeded ? r.LimitExceed : r.Rate) : 0f;

    private static IReadOnlyDictionary<(int, int), (float, float)> BuildReparation(float[,] rate)
    {
        var d = new Dictionary<(int, int), (float, float)>();
        for (int s = 0; s < 4; s++) for (int w = 0; w < 4; w++) d[(s, w)] = (rate[s, w], 0f);
        return d;
    }

    /// <summary>Every sheet, in report order.</summary>
    public static IReadOnlyList<ISheetValue> All => new ISheetValue[]
    {
        GuildSizes, ClassPositions, DefaultSkills, StarterKits, CreatedLevels, PvpBoardIds, PveBoardIds, DungeonTimelineIds,
        EventMatchingTargets, GuardContinents, DailyBuyMenus, RaidPartyNames, PartyLootDefaults,
        EventMatchingTargets, EquipSlots, ReparationRates,
        BattleFieldSheet.Entry,   // T163: T157's reader, on master since T161
        BattlegroundRatingSheet.Entry, // T199: custom rating bounds; not native MMR
        DungeonMatchRules.Entry,
        DungeonClearCountSheet.Entry,
        MatchBrowseStatistics.Entry,
        CardCollectionSheet.Entry,
        GuildLevelSheet.Entry, // T201: QA guild progression follows the deployed curve.
        GuildLevelSheet.QuestEntry,
        QaAccountSheet.Entry,
        VipSystemSheet.Entry,
        QaCommerceSheet.Entry,
        QaItemSheet.Entry,
        QaGuildSheet.Entry,
        QaGuildSheet.Emblems,
        QaGuildSheet.Mode,
        GuildIncentiveSheet.Entry,
        QaSocialSheet.Entry,
        Handlers.QaInventoryCommands.CommissionSheet,
        Handlers.QaPurchaseCommands.Menus,
        Handlers.QaNpcShopCommands.Menus,
        TeraSharp.Arbiter.Handlers.QaAchievementCommands.Sheet,
        TeraSharp.Arbiter.Handlers.QaFestivalCommands.Sheet,
        TeraSharp.Arbiter.Handlers.QaUtilityCommands.Sheet,
        TeraSharp.Arbiter.Handlers.QaGuildRankingCommands.Sheet,
    };

    /// <summary>
    /// Read every sheet now and log one line each - call once at startup. Returns the lines'
    /// source so --check-config and the tests can show them.
    /// </summary>
    public static IReadOnlyList<SheetStatus> LoadAll(ILogger? log = null, string? datasheetDir = null)
    {
        if (log != null) _log = log;
        var dir = datasheetDir ?? Directory();
        var list = new List<SheetStatus>();
        foreach (var s in All)
        {
            var st = s.Load(dir);
            list.Add(st);
            Report(st);
        }
        return list;
    }

    /// <summary>The --check-config section. Probes only: the values in use are not changed.</summary>
    public static IEnumerable<string> Describe(string? datasheetDir = null)
    {
        var dir = datasheetDir ?? Directory();
        yield return "folder: " + dir + (System.IO.Directory.Exists(dir) ? "" : "  (MISSING - every sheet below is the built-in)");
        foreach (var s in All) yield return s.Probe(dir).Line;
    }

    /// <summary>Test seam: every sheet on its built-in, as if no Datasheet folder existed.</summary>
    public static void UseBuiltIns()
    {
        foreach (var s in All) s.UseBuiltIn();
    }

    internal static void Report(SheetStatus st)
    {
        if (st.FromSheet) _log.LogInformation("Datasheet: {Line}", st.Line);
        else _log.LogWarning("Datasheet: {Line}", st.Line);
    }

    // ---- readers: null means "not usable, take the built-in" ----------------------------

    private static XDocument? ReadXml(string dir, string file)
    {
        var path = Path.Combine(dir, file);
        return File.Exists(path) ? XDocument.Parse(File.ReadAllText(path)) : null;
    }

    private static int Int(XElement e, string name, int fallback = 0)
        => int.TryParse(((string?)e.Attribute(name))?.Trim(), out int v) ? v : fallback;

    private static int IndexOf(string[] names, string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return -1;
        for (int i = 0; i < names.Length; i++)
            if (string.Equals(names[i], name.Trim(), StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    /// <summary>GuildConfig.xml's &lt;GuildSize&gt; rows, ordered by rank.</summary>
    public static GuildSizeRow[]? ReadGuildSizes(string dir)
    {
        var doc = ReadXml(dir, "GuildConfig.xml");
        if (doc == null) return null;
        var rows = doc.Descendants("GuildSize")
            .Select(e => new GuildSizeRow(Int(e, "rank"), Int(e, "accountNumOver"), Int(e, "declareCost"),
                                          Int(e, "maintainCost"), Int(e, "declareLimitCount")))
            .OrderBy(r => r.Rank).ToArray();
        return rows.Length > 0 ? rows : null;
    }

    /// <summary>DungeonMatching.xml's &lt;ClassPosition&gt;, over the built-in for any class it leaves out.</summary>
    public static MatchComposition.ClassPosition[]? ReadClassPositions(string dir)
    {
        var doc = ReadXml(dir, "DungeonMatching.xml");
        var block = doc?.Descendants("ClassPosition").FirstOrDefault();
        if (block == null) return null;
        var table = (MatchComposition.ClassPosition[])MatchComposition.BuiltInClassPositions.Clone();
        int found = 0;
        foreach (var c in block.Elements("Class"))
        {
            int id = IndexOf(ClassNames, (string?)c.Attribute("name"));
            int d = Int(c, "defaultPosition", -1), s = Int(c, "secondPosition", -1), t = Int(c, "thirdPosition", -1);
            if ((uint)id >= (uint)table.Length || (uint)d > 2 || (uint)s > 2 || (uint)t > 2) continue;
            table[id] = new MatchComposition.ClassPosition((MatchRole)d, (MatchRole)s, (MatchRole)t, Int(c, "secondPositionLevel"));
            found++;
        }
        return found > 0 ? table : null;
    }

    /// <summary>DefaultSkillSet.xml, keyed by the Arbiter's race / gender / class ids.</summary>
    public static IReadOnlyDictionary<(int Race, int Gender, int Class), (int[] Active, int[] Passive)>? ReadDefaultSkills(string dir)
    {
        var doc = ReadXml(dir, "DefaultSkillSet.xml");
        if (doc == null) return null;
        var map = new Dictionary<(int, int, int), (int[], int[])>();
        foreach (var e in doc.Descendants("Default"))
        {
            int race = IndexOf(RaceNames, (string?)e.Attribute("race"));
            string? g = ((string?)e.Attribute("gender"))?.Trim();
            int gender = string.Equals(g, "Male", StringComparison.OrdinalIgnoreCase) ? 0
                       : string.Equals(g, "Female", StringComparison.OrdinalIgnoreCase) ? 1 : -1;
            int cls = IndexOf(ClassNames, (string?)e.Attribute("class"));
            if (race < 0 || gender < 0 || cls < 0) continue;
            map[(race, gender, cls)] = (Ids((string?)e.Attribute("activeSkillIdList")), Ids((string?)e.Attribute("passiveSkillIdList")));
        }
        return map.Count > 0 ? map : null;
    }

    private static int[] Ids(string? list)
        => string.IsNullOrWhiteSpace(list) ? Array.Empty<int>()
         : list.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(int.Parse).ToArray();

    /// <summary>The worn-item slots in datasheet order: weapon, body, hands, feet - the INVTYPEs
    /// every row of the sheet produces (StarterInventory's remarks). A fifth worn item goes to the
    /// bag: its slot needs ItemTemplate's equipment part, which is not loaded.</summary>
    public static readonly int[] WornSlots = { 1, 3, 4, 5 };

    /// <summary>CreateCharData.xml, per class id, over the built-in for any class it leaves out.
    /// Rows naming a class the Arbiter cannot parse (the sheet's "hero") are skipped, as it skips them.</summary>
    public static StarterItem[][]? ReadStarterKits(string dir)
    {
        var doc = ReadXml(dir, "CreateCharData.xml");
        if (doc == null) return null;
        var kits = (StarterItem[][])StarterInventory.BuiltInKits.Clone();
        int found = 0;
        foreach (var ch in doc.Descendants("Char"))
        {
            int cls = IndexOf(ClassNames, (string?)ch.Attribute("class"));
            if ((uint)cls >= (uint)kits.Length) continue;
            var kit = new List<StarterItem>();
            int worn = 0, bag = 0;
            foreach (var it in ch.Elements("InitItem"))
            {
                int template = Int(it, "itemTemplateId"), amount = Int(it, "amount", 1);
                if (template <= 0) continue;
                bool wear = string.Equals(((string?)it.Attribute("initWear"))?.Trim(), "true", StringComparison.OrdinalIgnoreCase);
                kit.Add(wear && worn < WornSlots.Length
                    ? new StarterItem(template, amount, StarterInventory.EquippedPocket, WornSlots[worn++])
                    : new StarterItem(template, amount, StarterInventory.BagPocket, bag++));
            }
            kits[cls] = kit.ToArray();
            found++;
        }
        return found > 0 ? kits : null;
    }

    /// <summary>
    /// T162. CreateCharData.xml's <c>createdLevel</c> per class id, over the built-in for any class
    /// it leaves out. Read as the Arbiter reads it (ArbiterServer.exe.c:141515): default 1, and
    /// anything outside 1..127 becomes 1.
    /// </summary>
    public static int[]? ReadCreatedLevels(string dir)
    {
        var doc = ReadXml(dir, "CreateCharData.xml");
        if (doc == null) return null;
        var levels = (int[])StarterInventory.BuiltInCreatedLevels.Clone();
        int found = 0;
        foreach (var ch in doc.Descendants("Char"))
        {
            int cls = IndexOf(ClassNames, (string?)ch.Attribute("class"));
            if ((uint)cls >= (uint)levels.Length) continue;
            int level = Int(ch, "createdLevel", 1);
            levels[cls] = (uint)(level - 1) > 0x7Eu ? 1 : level;
            found++;
        }
        return found > 0 ? levels : null;
    }

    /// <summary>
    /// T161b/T184f. EventMatching.xml: every active <c>&lt;Event type="Dungeon|TimeLine|BattleField"&gt;</c> with an
    /// <c>&lt;Action type="matching"&gt;</c>, filed under each <c>&lt;Target id&gt;</c> it lists.
    /// </summary>
    public static IReadOnlyDictionary<int, (int[] Dungeon, int[] BattleField)>? ReadEventMatchingTargets(string dir)
    {
        var doc = ReadXml(dir, "EventMatching.xml");
        if (doc == null) return null;
        // T184: the wildcard reset expands the destination sheets, not arbitrary event
        // targets (Arb079:7274-7326). With no destination sheet, retain the old fallback.
        var dungeonIds = ReadXml(dir, "DungeonMatching.xml")?.Descendants("Dungeon")
            .Select(e => Int(e, "id", -1)).ToHashSet();
        var battleIds = ReadXml(dir, "BattleFieldData.xml")?.Descendants("BattleField")
            .Select(e => Int(e, "id", -1)).ToHashSet();
        var d = new Dictionary<int, (List<int> D, List<int> B)>();
        foreach (var ev in doc.Descendants("Event"))
        {
            int id = Int(ev, "id", -1);
            string type = ((string?)ev.Attribute("type"))?.Trim() ?? "";
            // Arb008:17688-17693,18111-18168: TimeLine=4 shares the dungeon event index.
            // Arb076:18485-18543 accepts types 0/4; cap_2man_client1 #1283 includes 2176/92176.
            bool dungeon = type.Equals("Dungeon", StringComparison.OrdinalIgnoreCase)
                || type.Equals("TimeLine", StringComparison.OrdinalIgnoreCase);
            if (id <= 0 || !(dungeon || type.Equals("BattleField", StringComparison.OrdinalIgnoreCase))) continue;
            if (string.Equals(((string?)ev.Attribute("active"))?.Trim(), "false", StringComparison.OrdinalIgnoreCase)) continue;
            if (!ev.Elements("Action").Any(a => string.Equals(((string?)a.Attribute("type"))?.Trim(), "matching", StringComparison.OrdinalIgnoreCase))) continue;
            foreach (var t in ev.Descendants("Target"))
            {
                int target = Int(t, "id", -1);
                if (target <= 0) continue;
                var offered = dungeon ? dungeonIds : battleIds;
                if (offered != null && !offered.Contains(target)) continue;
                if (!d.TryGetValue(target, out var lists)) d[target] = lists = (new List<int>(), new List<int>());
                (dungeon ? lists.D : lists.B).Add(id);
            }
        }
        return d.Count == 0 ? null : d.ToDictionary(kv => kv.Key, kv => (kv.Value.D.ToArray(), kv.Value.B.ToArray()));
    }

    /// <summary>Arb067:14533-14574 / Arb082:4830-4878,9850-9872. Strings remain in the sheet's locale.</summary>
    public static string[]? ReadRaidPartyNames(string dir)
    {
        var doc = ReadXml(dir, "StrSheet_BattleField.xml");
        if (doc == null) return null;
        var names = Enumerable.Repeat(string.Empty, 6).ToArray();
        foreach (var row in doc.Descendants().Where(e => e.Name.LocalName.Equals("String", StringComparison.OrdinalIgnoreCase)))
        {
            int slot = Int(row, "id", -1) - 10000001;
            if ((uint)slot < names.Length) names[slot] = (string?)row.Attribute("string") ?? string.Empty;
        }
        return names;
    }

    /// <summary>Arb058:13553-13568 supplies defaults for missing attributes when the row exists.</summary>
    public static PartyPackets.LootSettings[]? ReadPartyLootDefaults(string dir)
    {
        var row = ReadXml(dir, "WorldData.xml")?.Descendants("PartyLootingOption").FirstOrDefault();
        if (row == null) return null;
        bool Flag(string name, bool fallback) => bool.TryParse((string?)row.Attribute(name), out bool value) ? value : fallback;
        return new[] { new PartyPackets.LootSettings(
            Method: Int(row, "lootingType", 1),
            RareGradeForDicing: Int(row, "exceptionGradeForDistribution", 1),
            EquipmentForDicing: Flag("appliedToGearOnly", false),
            FindClassForDicing: Flag("onlyAppropriateClassCanLoot", true),
            RareItemDistributionMethod: Int(row, "exceptionItemDistributionType", 0),
            BoundOnLootItemDistributionMethod: Int(row, "nonSoulboundItems", 1),
            ForbidLootingInBattle: Flag("lootingNotAllowedDuringCombat", false)) };
    }

    /// <summary>GuardData.xml as shipped (6 continents, 27 guards) - the built-in.</summary>
    private static Dictionary<int, int> BuiltInGuardContinents()
    {
        var d = new Dictionary<int, int>();
        foreach (var (c, guards) in new[] { (1, new[] { 1, 2, 3, 4, 5, 6, 7 }), (2, new[] { 10, 11, 12, 13, 14, 15 }),
                                            (3, new[] { 18, 19, 20, 21, 22, 23 }), (4, new[] { 24 }), (5, new[] { 25 }), (6, new[] { 27, 28 }) })
            foreach (int g in guards) d[g] = c;
        return d;
    }

    public static IReadOnlyDictionary<int, int>? ReadGuardContinents(string dir)
    {
        var doc = ReadXml(dir, "GuardData.xml");
        if (doc == null) return null;
        var d = new Dictionary<int, int>();
        foreach (var c in doc.Descendants("Continent"))
        {
            int cid = Int(c, "id", -1);
            if (cid < 0) continue;
            foreach (var g in c.Elements("Guard")) { int gid = Int(g, "id", -1); if (gid >= 0) d[gid] = cid; }
        }
        return d;
    }

    /// <summary>
    /// T169. The worn slot (INVTYPE, ItemEquipRestriction.xml's list) of an equipment part, as
    /// User::AskItemCompareTooltip's switch (Arb_part_067.c:3712) maps it: weapon 1, body 3, hands 4,
    /// feet 5, ear 6, finger 8, neck 10, underwear 11, hair accessory 12, belt 19, brooch 20, relic 23.
    /// Face accessories, style and inheritance items fall to its default: no compare.
    /// </summary>
    public static int WornSlotOf(string? combatItemType, string? category) => combatItemType switch
    {
        "EQUIP_WEAPON" => 1, "EQUIP_ARMOR_BODY" => 3, "EQUIP_ARMOR_ARM" => 4, "EQUIP_ARMOR_LEG" => 5,
        "EQUIP_UNDERWEAR" => 11,
        "EQUIP_ACCESSORY" => category switch
        {
            "earring" => 6, "ring" => 8, "necklace" => 10, "accessoryHair" => 12,
            "belt" => 19, "brooch" => 20, "relic" => 23, _ => 0,
        },
        _ => 0,
    };

    /// <summary>T170. GuildConfig.xml's ReparationRate rows.</summary>
    public static IReadOnlyDictionary<(int, int), (float Rate, float LimitExceed)>? ReadReparationRates(string dir)
    {
        var doc = ReadXml(dir, "GuildConfig.xml");
        if (doc == null) return null;
        var d = new Dictionary<(int, int), (float, float)>();
        foreach (var e in doc.Descendants("ReparationRate"))
            d[(Int(e, "surrenderSize"), Int(e, "winnerSize"))] =
                (Flt(e, "reparationRate"), Flt(e, "limitExceedReparationRate"));
        return d.Count == 0 ? null : d;
    }

    private static float Flt(System.Xml.Linq.XElement e, string name)
        => float.Parse((string?)e.Attribute(name) ?? "0", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>T169. ItemTemplate.xml (44 MB, streamed): template id -> worn slot, equipment only.</summary>
    public static IReadOnlyDictionary<int, int>? ReadEquipSlots(string dir)
    {
        var path = Path.Combine(dir, "ItemTemplate.xml");
        if (!File.Exists(path)) return null;
        var d = new Dictionary<int, int>();
        using var xr = XmlReader.Create(path, new XmlReaderSettings
            { DtdProcessing = DtdProcessing.Ignore, IgnoreComments = true, IgnoreWhitespace = true });
        while (xr.Read())
        {
            if (xr.NodeType != XmlNodeType.Element || xr.Name != "Item") continue;
            var type = xr.GetAttribute("combatItemType");
            if (type == null || !type.StartsWith("EQUIP_", StringComparison.Ordinal)) continue;
            int slot = WornSlotOf(type, xr.GetAttribute("category"));
            if (slot > 0 && int.TryParse(xr.GetAttribute("id"), out int id)) d[id] = slot;
        }
        return d.Count == 0 ? null : d;
    }

    /// <summary>The 24 day-reset menus of the shipped BuyMenuData.xml + _EU/_JP/_KR/_RUS - exactly the 24
    /// AS_RESET_PURCHASE_LIMIT frames of cap_final2a/2b 09:00:54, in the same (ascending) order.</summary>
    public static (int Id, int Hour)[] BuiltInDailyBuyMenus => DailyBuyMenus.BuiltIn;

    private static (int Id, int Hour)[] BuiltInDailyMenus() =>
        new[] { 1005, 1006, 1007, 1009, 1010, 1011, 1015, 1016, 1019, 1020, 1035, 1049, 10070 }.Select(i => (i, 5))
        .Concat(new[] { 20000, 20001, 20003, 20004, 30002, 30006, 30008, 30011, 30020, 50001, 60000 }.Select(i => (i, 9)))
        .ToArray();

    /// <summary>BuyMenuData.xml and every regional BuyMenuData_*.xml: the day-reset menus, ascending id.</summary>
    public static (int Id, int Hour)[]? ReadDailyBuyMenus(string dir)
    {
        if (!System.IO.Directory.Exists(dir)) return null;
        var files = System.IO.Directory.GetFiles(dir, "BuyMenuData*.xml");
        if (files.Length == 0) return null;
        var d = new SortedDictionary<int, int>();
        foreach (var f in files.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            var doc = XDocument.Parse(File.ReadAllText(f));
            foreach (var m in doc.Descendants("BuyMenu"))
                if (string.Equals(((string?)m.Attribute("resetType"))?.Trim(), "day", StringComparison.OrdinalIgnoreCase))
                {
                    int id = Int(m, "id", -1);
                    if (id > 0) d[id] = Int(m, "resetTime", 0);
                }
        }
        return d.Select(kv => (kv.Key, kv.Value)).ToArray();
    }

    /// <summary>BattleFieldData.xml's battlegrounds with &lt;RankingCompetition active="true"&gt;, ascending.</summary>
    public static int[]? ReadPvpBoardIds(string dir)
    {
        var doc = ReadXml(dir, "BattleFieldData.xml");
        if (doc == null) return null;
        var ids = doc.Descendants("BattleField")
            .Where(b => b.Elements("RankingCompetition").Any(r =>
                string.Equals(((string?)r.Attribute("active"))?.Trim(), "true", StringComparison.OrdinalIgnoreCase)))
            .Select(b => Int(b, "id", -1)).Where(id => id > 0).Distinct().OrderBy(id => id).ToArray();
        return ids.Length > 0 ? ids : null;
    }

    /// <summary>The dungeons with a DungeonRankRecorder_&lt;id&gt;.xml, ascending.</summary>
    public static int[]? ReadPveBoardIds(string dir)
    {
        if (!System.IO.Directory.Exists(dir)) return null;
        const string prefix = "DungeonRankRecorder_";
        var ids = new SortedSet<int>();
        foreach (var f in System.IO.Directory.EnumerateFiles(dir, prefix + "*.xml"))
        {
            var stem = Path.GetFileNameWithoutExtension(f);
            if (stem.Length > prefix.Length && int.TryParse(stem.Substring(prefix.Length), out int id) && id > 0) ids.Add(id);
        }
        return ids.Count > 0 ? ids.ToArray() : null;
    }
}
