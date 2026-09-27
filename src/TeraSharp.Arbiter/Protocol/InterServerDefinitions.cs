// SPDX-License-Identifier: MIT
// Copyright (c) 2026 the TeraSharp contributors

using System.Runtime.CompilerServices;

namespace TeraSharp.Arbiter.Protocol;

/// <summary>
/// Layouts for the Arbiter-&gt;World frames we build ourselves - today the 50 opcodes of the
/// post-handshake configuration burst (T209b). Inter-server frames use the same record model as
/// client packets, so <see cref="DefinitionWriter"/> in <see cref="PacketFraming.InterServer"/>
/// writes them from these defs; see <see cref="World.HandshakeBurst"/> for the values.
///
/// <para>Names and field names are not guesses: every one of these opcodes has a dump helper in
/// the retail Arbiter that prints its record field by field, with the field names as wide string
/// literals next to the type-specific printer (<c>FUN_14016bb70</c> u32, <c>FUN_14016c4c0</c> u64,
/// <c>FUN_14016c570</c> u8/bool, <c>FUN_14016bd70</c> ref/list). The opcode-&gt;name pairing and the
/// per-frame index are in data/handshake_burst.md; status/T209b-HANDSHAKE.md has the derivation and
/// the six frames whose layout came from the writer functions rather than a schema.</para>
///
/// <para>No captured bytes: the burst reproduces byte for byte from named fields alone
/// (Tests/T209b.cs asserts that against all 63 captured records), which is what let
/// data/handshake_burst.bin stop being a runtime dependency - T139.</para>
/// </summary>
public static class InterServerDefinitions
{
    /// <summary>Same version as <see cref="V100Definitions"/>: above anything in the def folder.</summary>
    public const int Version = 100;

    /// <summary>
    /// Every inter-server def: packet name, opcode, def text. Field ORDER is the wire order and
    /// the implicit ref-header order both - no <c>ref</c> lines, so the header block is the
    /// bytes/array fields in the order they appear, which is what the retail writer emits.
    /// </summary>
    private static readonly (string Name, ushort Op, string Def)[] Table =
    {
        ("AS_SYNC_DATE_TIME", 0x15BD, @"
int64 ArbiterTime
bool  ExecuteSync
"),
        ("AS_UPDATE_DAILY_EVENT_DAY", 0x156B, @"
int32 DayOfWeek
"),
        ("AS_INITIALIZE_DUNGEON_ID", 0x13C7, @"
int32 DungeonId
"),
        ("AS_DUNGEON_DISABLED_LIST", 0x157E, @"
bytes DungeonContinentIdList
"),
        ("AS_DUNGEON_PHASE_LAST_RESET_TIME", 0x15DE, @"
int32 ContinentId
int64 LastResetTime
"),
        ("AS_INIT_TIMELINE_CHANGES", 0x1582, @"
array<int64> Entries
int32        TimelineType
"),
        ("AS_CONTENTS_ON_OFF_LIST", 0x157F, @"
bytes ContentsOnOffList
"),
        ("AS_GUILD_WAR_ADMIN_MAINTAINCOST", 0x14B3, @"
array<int32> CostList
"),
        ("AS_VIP_STORE_SLOT_ON_OFF", 0x150D, @"
array<int32> SlotList
"),
        ("AS_VIP_STORE_SLOT_SALE", 0x150E, @"
array<int32> SlotList
"),
        ("AS_CHANGE_ACHIEVEMENT_SEASON", 0x1510, @"
int32 CurrentSeasonId
"),
        ("AS_ACHIEVEMENT_SEASON_LIST", 0x1511, @"
array<int32> SeasonList
"),
        ("DISABLE_DARK_RIFT_HUNTING_ZONE", 0x14D0, @"
bytes DisabledHuntingZoneIdList
"),
        ("AS_SET_DARK_RIFT_DAILY_COMPLETED", 0x14D1, @"
bytes DailyCompletedList
int64 LastResetDateTime
"),
        ("AS_RESET_ADMIN_NON_PK_SECTION", 0x14E1, @"
bytes SectionIdList
"),
        ("AS_SET_HUNTING_EVENT_VALUE", 0x14EC, @"
array<int32> EventValueList
"),
        ("AS_BOT_CONFIGURATION", 0x1529, @"
int32 CheckMode
"),
        ("AS_EVENT_HUNTINGBONUS", 0x28F8, @"
bytes Begin_serverUTCTime
bytes End_serverUTCTime
bytes NpcList
bytes ItemList
bool  AddBegin
bool  AddEnd
int32 ArbiterServerPlanetId
int32 Identity
int32 IsOn
"),
        ("AS_SET_EP_SYSTEM_CONTENTS_ON_OFF_TABLE", 0x1556, @"
bytes OffContents
"),
        ("AS_SET_EP_SYSTEM_EVENT", 0x150A, @"
bytes ValueTable
"),
        ("AS_SET_JACKPOT_EVENT_VALUE_LIST", 0x14E6, @"
array<int32> JackpotEvent
"),
        ("AS_SEND_RESTRICTION_ITEM_LIST", 0x1623, @"
array<int32> RestrictionItemTemplateIdList
"),
        ("AS_STOP_FESTIVAL", 0x149D, @"
int32 FestivalId
"),
        ("AS_FESTIVAL_NPC_SPAWN", 0x149E, @"
int32 FestivalId
bool  Start
"),
        ("AS_FESTIVAL_OBJECT_SPAWN", 0x149F, @"
int32 FestivalId
bool  Start
"),
        ("AS_LOAD_PRODUCT_SALE_INFO", 0x15B6, @"
array<int32> ProductSaleList
"),
        ("AS_BATTLE_FIELD_DISABLED_LIST", 0x15C2, @"
bytes BattleFieldTemplateIdList
"),
        ("AS_LOAD_ENCHANT_PROB_EVENT_INFO", 0x15C5, @"
array<int32> EventList
"),
        ("AS_ADD_AWAKEN_ENCHANT_DATA", 0x15D4, @"
array<int32> EnchantList
"),
        ("AS_ADD_AWAKEN_CHANGE_DATA", 0x15D5, @"
array<int32> ChangeList
"),
        ("AS_LOAD_GMEVENT_DATA", 0x1603, @"
bytes GmEventDataList
"),
        ("AS_GMEVENT_LOAD_JOIN_COUNT", 0x160D, @"
array<int64> UserJoinCountList
"),
        ("AS_GMEVENT_CHANGE_MAX_JOIN_COUNT", 0x160F, @"
int32 MaxJoinCount
"),
        ("AS_GMEVENT_ONOFF", 0x1609, @"
bool IsOn
"),
        ("AS_SET_DUNGEON_ROOKIE_EVENT_VALUE_LIST", 0x14E9, @"
array<int32> DungeonRookieEvent
"),
        ("AS_SET_DUAL_OPTION_OPEN_MATERIAL_LIST", 0x14E2, @"
array<int32> OpenMaterialList
"),
        ("AS_SET_PLAY_GUIDE_EVENT_LIST", 0x14EE, @"
array<int32> EventInfoList
"),
        ("AS_SET_PLAY_GUIDE_WEB_ADMIN_SETTINGS", 0x14F2, @"
bool CanRecvDailyBonus
bool CanRecvWeeklyBonus
"),
        ("AS_SET_PLAY_GUIDE_EXTRA_BASE_REWARD_EVENT", 0x14F3, @"
array<int32> EventInfoList
"),
        ("AS_SET_PLAY_GUIDE_EXTRA_DAILY_REWARD_EVENT", 0x14F7, @"
array<int32> EventInfoList
"),
        ("AS_LOAD_EVENTSYSTEM_INFO", 0x1613, @"
array<int64> EventList
"),
        ("AS_SET_FIELD_POINT_ADMIN_REWARD", 0x161D, @"
bytes RewardList
"),
        ("AS_ACCESSORY_TRANSFORM_COST_INFO", 0x1589, @"
bool  IsFirstFree
int32 ItemTemplateId
int32 Amount
"),
        ("AS_WORLD_PURCHASE_LIMIT", 0x162E, @"
array<int32> PurchaseLimitInfoList
bool         Success
"),
        ("AS_LOAD_CONTINENT_CHANNEL_COUNT", 0x1567, @"
bytes ContinentList
"),
        ("DBS_INGAMESHOP_CATEGORY_BEGIN", 0x28E3, @"
bool Success
"),
        ("DBS_INGAMESHOP_CATEGORY_END", 0x28E5, @"
bool Success
"),
        ("DBS_INGAMESHOP_PRODUCT_BEGIN", 0x28E6, @"
bool Success
"),
        ("DBS_INGAMESHOP_PRODUCT_END", 0x28EB, @"
bool Success
"),
        ("DBS_TBA_UPDATE_ROTATION", 0x29E2, @"
array<int32> RotationHeroList
"),
    };

    private static readonly Dictionary<string, ushort> ByName = BuildOpcodes();
    private static readonly Dictionary<ushort, string> ByOp = BuildNames();
    private static readonly Dictionary<string, PacketDef> Parsed = BuildDefs();

    private static Dictionary<string, ushort> BuildOpcodes()
    {
        var d = new Dictionary<string, ushort>(Table.Length, StringComparer.Ordinal);
        foreach (var (name, op, _) in Table) d[name] = op;
        return d;
    }

    private static Dictionary<ushort, string> BuildNames()
    {
        var d = new Dictionary<ushort, string>(Table.Length);
        foreach (var (name, op, _) in Table) d[op] = name;
        return d;
    }

    private static Dictionary<string, PacketDef> BuildDefs()
    {
        var d = new Dictionary<string, PacketDef>(Table.Length, StringComparer.Ordinal);
        foreach (var (name, _, text) in Table) d[name] = DefinitionParser.ParseText(name, text, Version);
        return d;
    }

    /// <summary>Inter-server name -&gt; opcode. data.json carries CLIENT opcodes only.</summary>
    public static IReadOnlyDictionary<string, ushort> Opcodes => ByName;

    /// <summary>Opcode for an inter-server packet name. Throws if the name is not one of ours.</summary>
    public static ushort Opcode(string name) => ByName[name];

    /// <summary>Name for an inter-server opcode, or null.</summary>
    public static string? Name(ushort op) => ByOp.TryGetValue(op, out var n) ? n : null;

    /// <summary>The parsed layout for an inter-server packet name. Throws if the name is not ours.</summary>
    public static PacketDef Def(string name) => Parsed[name];

    /// <summary>How many inter-server defs there are (the burst's distinct opcode count).</summary>
    public static int Count => Table.Length;

    private static readonly ConditionalWeakTable<DefinitionRegistry, object> Done = new();

    /// <summary>
    /// Also publish these on a client-packet registry, so a tap or a test that resolves layouts
    /// by name can decode an inter-server frame. Nothing at runtime needs this - the burst goes
    /// through <see cref="Def(string)"/> - and the AS_/DBS_ names cannot collide with S_/C_ ones.
    /// </summary>
    public static void EnsureRegistered(DefinitionRegistry defs)
    {
        if (defs is null || Done.TryGetValue(defs, out _)) return;
        lock (Done)
        {
            if (Done.TryGetValue(defs, out _)) return;
            foreach (var def in Parsed.Values) defs.Register(def);
            Done.Add(defs, new object());
        }
    }
}
