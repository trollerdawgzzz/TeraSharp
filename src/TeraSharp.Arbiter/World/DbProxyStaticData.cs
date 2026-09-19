using System.Text.RegularExpressions;

namespace TeraSharp.Arbiter.World;

/// <summary>
/// Static reply payloads from arb_world.log (the real ArbiterServer login sequence).
/// Each builder clones the template and patches the live reqId at the documented offset.
/// Contains character-specific data from the seeded capture; replace with DB-backed data when available.
///
/// Field layouts below are from the decompiled ArbiterServer writers (Arb_part_063/064.c).
/// reqId offsets are confirmed against the decompile handlers, not inferred from the capture.
/// </summary>
internal static class DbProxyStaticData
{
    // -----------------------------------------------------------------------
    // 0x2873 DBS_LOAD_TUTORIAL_SIMPLE_TIP — reply to 0x2872
    // Handler: Handler_SDB_LOAD_TUTORIAL_SIMPLE_TIP (Arb_part_063.c:17226)
    // Writer:  FUN_140350eb0 (inline)
    //
    // Layout (45B with 4 list entries):
    //   [0]  u32  listOffset     — byte offset to list data (frame-relative)
    //   [4]  u32  listSize       — N * 8 (count of u64s × 8)
    //   [8]  u32  reqId          — echoed from request payload[0]  ← CONFIRMED from decompile
    //   [12] u8   success        — 1 if player found
    //   [13] u64[]  tipIds       — list of tutorial tip IDs (from user+0x90 tutorial mutex)
    //
    // To make dynamic: read user's tutorial-tip set, serialize each as u64.
    // -----------------------------------------------------------------------
    internal const int TutorialReqIdOffset = 8;
    internal static readonly byte[] Tutorial = new byte[]
    {
        0x13, 0x00, 0x00, 0x00, 0x20, 0x00, 0x00, 0x00, 0x07, 0x00, 0x00, 0x00, 0x01, 0x01, 0x00, 0x00,
        0x00, 0x01, 0x00, 0x00, 0x00, 0x02, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x23, 0x00, 0x00,
        0x00, 0x01, 0x00, 0x00, 0x00, 0x27, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00
    };

    // -----------------------------------------------------------------------
    // 0x2890 DBS_LOAD_REPUTATION_LIST — reply to 0x288F
    // Handler: Handler_SDB_LOAD_REPUTATION_LIST (Arb_part_063.c:16080)
    //
    // Layout (65B with 1 ReputationData entry of 52 bytes):
    //   [0]  u32  listOffset     — byte offset to list data
    //   [4]  u32  listSize       — N * 0x34 (52 bytes per entry)
    //   [8]  u8   success        — 1 if player found
    //   [9]  u32  reqId          — echoed from request payload[0]  ← CONFIRMED
    //   [13] ReputationData[]    — reputation entries (from user+0x6090)
    //
    // ReputationData (52 bytes):
    //   [+0]  u64 field0   [+8]  u64 field1   [+16] u64 field2   [+24] u64 field3
    //   [+32] u32 field4   [+36] u32 field5   [+40] u32 field6   [+44] u32 field7
    //   [+48] u32 field8
    //
    // To make dynamic: read user's reputation factions from user+0x6090, serialize each.
    // -----------------------------------------------------------------------
    internal const int ReputationReqIdOffset = 9;
    internal static readonly byte[] Reputation = new byte[]
    {
        0x13, 0x00, 0x00, 0x00, 0x34, 0x00, 0x00, 0x00, 0x01, 0x0F, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00,
        0x00, 0x62, 0x02, 0x00, 0x00, 0x06, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x18, 0x79, 0x00, 0x00, 0x3F, 0x42, 0x0F, 0x00, 0x00, 0x00, 0x00, 0x00, 0xDA, 0xE5, 0x02,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00
    };

    // -----------------------------------------------------------------------
    // 0x293B DBS_LOAD_USER_DAILY_EVENT — reply to 0x293A
    // Handler: Handler_SDB_LOAD_USER_DAILY_EVENT (Arb_part_063.c:17570)
    //
    // Layout (46B):
    //   [0]  u32  rawDataOffset  — byte offset to raw event bytes
    //   [4]  u32  rawDataSize    — fixed 0x14 (20 bytes)
    //   [8]  u32  reqId          — echoed from request payload[0]  ← CONFIRMED
    //   [12] u8   success        — result of daily event query
    //   [13] u64  timestamp      — event timestamp (FUN_140034d90)
    //   [21] u8   flag           — daily event flag
    //   [22] u32  dailyEventParam
    //   [26] byte[20]  rawEventData — raw daily event bytes
    //
    // To make dynamic: query user's daily event state from FUN_140390640.
    // -----------------------------------------------------------------------
    internal const int Load293BReqIdOffset = 8;
    internal static readonly byte[] Load293B = new byte[]
    {
        0x20, 0x00, 0x00, 0x00, 0x14, 0x00, 0x00, 0x00, 0x10, 0x00, 0x00, 0x00, 0x01, 0x0E, 0xDA, 0xA4,
        0x6A, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00
    };

    // -----------------------------------------------------------------------
    // 0x2909 DBS_LOAD_FATIGABILITY_LIST — reply to 0x2908
    // Handler: Handler_SDB_LOAD_FATIGABILITY_LIST (Arb_part_063.c:13976)
    //
    // Layout (45B with 1 FatigabilityInfo entry of 28 bytes):
    //   [0]  u32  listOffset     — byte offset to list data
    //   [4]  u32  listSize       — N * 0x1C (28 bytes per entry)
    //   [8]  u8   success        — 1 if player found
    //   [9]  u32  reqId          — echoed from request payload[0]  ← CONFIRMED
    //   [13] u32  maxFatigability — from user.fatigability+0x305C
    //   [17] FatigabilityInfo[]  — fatigability entries
    //
    // FatigabilityInfo (28 bytes):
    //   [+0] u32  [+4] u32  [+8] u32  [+12] u32  [+16] u64  [+24] u32
    //
    // Source: user+0x3f40 → sub-object+0x3018 (fatigability list data)
    // To make dynamic: read user's fatigability sub-object, maxFatigability from +0x305C.
    // -----------------------------------------------------------------------
    internal const int FatigabilityReqIdOffset = 9;
    internal static readonly byte[] Fatigability = new byte[]
    {
        0x17, 0x00, 0x00, 0x00, 0x1C, 0x00, 0x00, 0x00, 0x01, 0x23, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x01, 0x00, 0x00, 0x00, 0x27, 0x06, 0x00, 0x00, 0xEA, 0x07, 0x09, 0x00, 0x0C, 0x00, 0x05,
        0x00, 0x35, 0x00, 0x1C, 0x00, 0x00, 0x00, 0x00, 0x00, 0x54, 0x00, 0x00, 0x00
    };

    // -----------------------------------------------------------------------
    // 0x2943 DBS_INIT_SEREN_GUIDE_INFO — reply to 0x2942
    // Handler: Handler_SDB_INIT_SEREN_GUIDE_INFO (Arb_part_063.c:9466)
    // Writer:  FUN_14035fa80 (Arb_part_027.c:12364) via User::SendSerenGuideInfo
    //
    // Layout (65B with 6 list entries of 8 bytes each):
    //   [0]  u32  listOffset     — byte offset to list data
    //   [4]  u32  listSize       — N * 8 (u64 list)
    //   [8]  u32  reqId          — echoed from request payload[0]  ← CONFIRMED
    //   [12] u32  serverId       — from user+0x120
    //   [16] u8   success        — 1
    //   [17] u64[]  guideIds     — seren guide IDs (from user+0x88C0 tree)
    //
    // To make dynamic: iterate user's seren guide tree at +0x88C0, extract u64 at node+0x1C.
    // -----------------------------------------------------------------------
    internal const int SerenGuideReqIdOffset = 8;
    internal static readonly byte[] SerenGuide = new byte[]
    {
        0x17, 0x00, 0x00, 0x00, 0x30, 0x00, 0x00, 0x00, 0x26, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00,
        0x01, 0x04, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x0D, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x0E, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x0F, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x10, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x11, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00
    };

    // -----------------------------------------------------------------------
    // 0x27BA DBS_USER_LOAD_EP_PERK — reply to 0x27B9
    // Handler: Handler_SDB_USER_LOAD_EP_PERK (Arb_part_064.c:14987)
    // NOTE: previously labelled "GUILD_SEARCH" — decompile says EP (Elite Points) Perk.
    //
    // Layout (113B with 5 PerkGroup elements, 0 children):
    //   [0]  u32  groupCount     — number of perk groups (5 in capture)
    //   [4]  u32  firstGroupOffset — byte offset to first PerkGroup element
    //   [8]  u32  reqId          — echoed from request payload[0]  ← CONFIRMED
    //   [12] u8   hasPerk        — FUN_140390890(user) && FUN_140397320(user)
    //   [13] u32  epField1       — user+0x8910
    //   [17] u32  epField2       — user+0x8914
    //   [21] u32  epField3       — user+0x8918
    //   [25] u32  epField4       — user+0x891C
    //   [29] u32  epField5       — user+0x8920
    //   [33] PerkGroup[5]        — linked list of groups (16B each)
    //
    // PerkGroup (16 bytes):
    //   [+0] u32 nextOffset  [+4] u32 firstChildOffset  [+8] u32 childCount  [+12] u32 reserved
    // PerkChild (16 bytes):
    //   [+0] u32 nextOffset  [+4] u32 reserved  [+8] u32 perkId  [+12] u32 perkValue
    //
    // To make dynamic: call FUN_140386c60(user, groups) to populate the 5-group perk array.
    // -----------------------------------------------------------------------
    internal const int EpPerkReqIdOffset = 8;
    internal static readonly byte[] EpPerk = new byte[]
    {
        0x05, 0x00, 0x00, 0x00, 0x27, 0x00, 0x00, 0x00, 0x2D, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x27, 0x00, 0x00, 0x00, 0x37, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x37, 0x00, 0x00, 0x00, 0x47, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x47, 0x00, 0x00, 0x00, 0x57, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x57, 0x00, 0x00, 0x00, 0x67, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x67, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00
    };

    // -----------------------------------------------------------------------
    // 0x272D DBS_LOAD_QUEST_LIST — reply to 0x272C
    // Handler: Handler_SDB_LOAD_QUEST_LIST (Arb_part_063.c:15691)
    // Writer:  FUN_1406edac0 (Arb_part_060.c:6773)
    //
    // Layout (1377B, fixed header 53B + data sections):
    //   [0]  u32  questList1Offset       — offset to QuestData list 1
    //   [4]  u32  questList1Size         — N * 0x50 (80 bytes per quest)
    //   [8]  u32  questList2Offset       — offset to QuestData list 2
    //   [12] u32  questList2Size         — N * 0x50
    //   [16] u32  completedQuestIdsOff   — offset to completed quest IDs
    //   [20] u32  completedQuestIdsSize  — N * 4 (u32 per ID)
    //   [24] u32  dailyQuestSeedOff      — offset to DailyQuestSeed list
    //   [28] u32  dailyQuestSeedSize     — N * 0x44 (68 bytes per entry)
    //   [32] u32  dailyQuestExCompleteOff
    //   [36] u32  dailyQuestExCompleteSize — N * 8 (u64 per entry)
    //   [40] u32  rawBytesOffset         — offset to raw byte data
    //   [44] u32  rawBytesSize           — fixed 0x14 (20 bytes)
    //   [48] u8   success                — 1 if player found
    //   [49] u32  reqId                  — echoed from request payload[0]  ← CONFIRMED
    //   [53+] Data sections follow (quests, completions, daily seeds, raw bytes)
    //
    // QuestData (0x50 = 80 bytes): quest state, progress counters, timestamps.
    // DailyQuestSeed (0x44 = 68 bytes): daily quest generation seeds.
    //
    // Source: user+0x90 via FUN_140715cf0 (quest data), GlobalDailyQuestSeed.
    // To make dynamic: iterate user's quest lists, serialize each QuestData/seed.
    // -----------------------------------------------------------------------
    internal const int QuestListReqIdOffset = 49;
    internal static readonly byte[] QuestList = new byte[]
    {
        0x3B, 0x00, 0x00, 0x00, 0x50, 0x00, 0x00, 0x00, 0x8B, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x8B, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x8B, 0x00, 0x00, 0x00, 0xC8, 0x04, 0x00, 0x00,
        0x53, 0x05, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x53, 0x05, 0x00, 0x00, 0x14, 0x00, 0x00, 0x00,
        0x01, 0x12, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0xFD, 0xE9, 0x00, 0x00, 0x01, 0x00, 0x00,
        0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0x59, 0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0xEA, 0x07, 0x09,
        0x00, 0x0B, 0x00, 0x07, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x5A, 0x02, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0xEA, 0x07, 0x09, 0x00, 0x0B, 0x00, 0x07, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x5B, 0x02, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0xEA, 0x07, 0x09, 0x00, 0x0B, 0x00, 0x07, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x5C, 0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0xEA, 0x07, 0x09, 0x00, 0x0B, 0x00, 0x07,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x5D, 0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0xEA, 0x07, 0x09,
        0x00, 0x0B, 0x00, 0x07, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x5E, 0x02, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0xEA, 0x07, 0x09, 0x00, 0x0B, 0x00, 0x07, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x5F, 0x02, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0xEA, 0x07, 0x09, 0x00, 0x0B, 0x00, 0x07, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x60, 0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0xEA, 0x07, 0x09, 0x00, 0x0B, 0x00, 0x07,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x61, 0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0xEA, 0x07, 0x09,
        0x00, 0x0B, 0x00, 0x07, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x64, 0x02, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0xEA, 0x07, 0x09, 0x00, 0x0B, 0x00, 0x07, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x65, 0x02, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0xEA, 0x07, 0x09, 0x00, 0x0B, 0x00, 0x07, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x66, 0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0xEA, 0x07, 0x09, 0x00, 0x0B, 0x00, 0x07,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x68, 0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0xEA, 0x07, 0x09,
        0x00, 0x0B, 0x00, 0x07, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x69, 0x02, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0xEA, 0x07, 0x09, 0x00, 0x0B, 0x00, 0x07, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x6C, 0x02, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0xEA, 0x07, 0x09, 0x00, 0x0B, 0x00, 0x07, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x6E, 0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0xEA, 0x07, 0x09, 0x00, 0x0B, 0x00, 0x07,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x74, 0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0xEA, 0x07, 0x09,
        0x00, 0x0B, 0x00, 0x07, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x85, 0x03, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0xEA, 0x07, 0x09, 0x00, 0x0B, 0x00, 0x07, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x0F,
        0xB8, 0xEA, 0x07, 0x09, 0x00, 0x0B, 0x00, 0x07, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00
    };

    // -----------------------------------------------------------------------
    // 0x27F9 DBS_LOAD_USER_ACHIEVEMENT — reply to 0x27F8
    // Handler: Handler_SDB_LOAD_USER_ACHIEVEMENT (Arb_part_063.c:17360)
    // Writer:  FUN_1406eeae0 (Arb_part_060.c:7480, 43 params!)
    //
    // Layout (1501B, fixed header 309B):
    //   [0..303]  76 × u32 = 38 (offset, size) pairs for data sections:
    //     pair 0:  raw achievement blob    — 0x4A0 (1184) bytes from user+0x5770
    //     pair 1:  achievement set (tree)  — N * 8 (u64 each) from user+0x6000
    //     pairs 2-4:  lists A-C           — 0xC (12) bytes each
    //     pair 5:     list D              — 8 bytes (u64) each
    //     pairs 6-37: lists E-AK          — 8 bytes (u64) each
    //   [304] u32  reqId          — echoed from request payload[0]  ← CONFIRMED
    //   [308] u8   success        — 1 if player found
    //   [309+] Data sections: raw blob (1184B) + achievement tree entries (8B each)
    //
    // In capture: 1501 = 309 header + 1184 blob + 8 tree entry = 1501B. ✓
    //
    // Source: FUN_1406fd620 (achievement blob), FUN_1406fda20 (special data),
    //         FUN_140385560 (achievement stats), user+0x6000 (achievement set tree).
    // To make dynamic: read user's achievement blob and tree, build sections.
    // -----------------------------------------------------------------------
    internal const int AchievementReqIdOffset = 304;
    internal static readonly byte[] Achievement = new byte[]
    {
        0x3B, 0x01, 0x00, 0x00, 0xA0, 0x04, 0x00, 0x00, 0xDB, 0x05, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0xDB, 0x05, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0xDB, 0x05, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0xDB, 0x05, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0xDB, 0x05, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0xDB, 0x05, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0xDB, 0x05, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0xDB, 0x05, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0xDB, 0x05, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0xDB, 0x05, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0xDB, 0x05, 0x00, 0x00, 0x08, 0x00, 0x00, 0x00,
        0xE3, 0x05, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0xE3, 0x05, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0xE3, 0x05, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0xE3, 0x05, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0xE3, 0x05, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0xE3, 0x05, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0xE3, 0x05, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0xE3, 0x05, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0xE3, 0x05, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0xE3, 0x05, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0xE3, 0x05, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0xE3, 0x05, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0xE3, 0x05, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0xE3, 0x05, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0xE3, 0x05, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0xE3, 0x05, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0xE3, 0x05, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0xE3, 0x05, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0xE3, 0x05, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0xE3, 0x05, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0xE3, 0x05, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0xE3, 0x05, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0xE3, 0x05, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0xE3, 0x05, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0xE3, 0x05, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0xE3, 0x05, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x13, 0x00, 0x00, 0x00, 0x01, 0x84, 0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x84, 0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x10, 0x93, 0x0F,
        0xB8, 0x58, 0x01, 0x00, 0x00, 0xB0, 0x92, 0x0F, 0xB8, 0x57, 0x01, 0x00, 0x00, 0x50, 0x92, 0x0F,
        0xB8, 0x57, 0x01, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0xF0, 0xA6, 0xA3,
        0x6A, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x57, 0x01, 0x00, 0x00, 0xCE, 0x32, 0x00, 0x00, 0x02, 0x00, 0x00, 0x00
    };

    // -----------------------------------------------------------------------
    // 0x272D DBS_LOAD_QUEST_LIST — the EMPTY-DAILY-SEED form (153B payload, 159B frame).
    //
    // Ground truth: D:\packetlogs\lobby_tap.log, real ArbiterServer, 2026-09-13T02:51:10.648Z,
    // first login of the day for playerId 1. Same 53-byte header as `QuestList` above, but the
    // dailyQuestSeed section is EMPTY:
    //     [24] dailyQuestSeedOff = 0x8B (139)   [28] dailyQuestSeedSize = 0
    // and questList1 holds exactly one entry ([0] off=59, [4] size=0x50), the starting quest
    // 0xE9FD. rawBytes sit at 139..158 (0x14 bytes), so the frame is 139 + 20 = 159.
    //
    // WHY THIS EXISTS. The `QuestList` template above is the 1383-byte form, captured
    // 2026-09-12, which carries 17 daily-quest seeds stamped with that date. Serving it to a
    // live World on any later day makes World's quest manager take the "these dailies are
    // stale, reset the completed count" branch and emit SDB_UPDATE_DAILY_QUEST_COMPLETE_COUNT
    // (0x2897) — a code path no capture we own has ever seen the real Arbiter answer, and the
    // point at which the per-user DLM queue head-blocks (see status/HANDOFF.md).
    //
    // Serving the empty-seed form instead makes World take the branch the real server takes on
    // a first login: it emits SDB_UPDATE_DAILY_QUEST_SEED (0x2899) once per daily quest — 17
    // times in lobby_tap.log — each answered with DBS_UPDATE_DAILY_QUEST_SEED (0x289A).
    // That is why DbProxyHandlers must handle 0x2899 for this template to be safe.
    //
    // reqId is at payload[49] (u8 success at [48] first), same as `QuestList`.
    // -----------------------------------------------------------------------
    internal const int QuestListEmptyReqIdOffset = 49;
    internal static readonly byte[] QuestListEmpty = new byte[]
    {
        0x3B, 0x00, 0x00, 0x00, 0x50, 0x00, 0x00, 0x00, 0x8B, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x8B, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x8B, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x8B, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x8B, 0x00, 0x00, 0x00, 0x14, 0x00, 0x00, 0x00,
        0x01, 0x12, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0xFD, 0xE9, 0x00, 0x00, 0x01, 0x00, 0x00,
        0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x70, 0xFD, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0x00, 0x00, 0x7F, 0x90, 0xEA, 0x07, 0x09, 0x00, 0x0C, 0x00, 0x07,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
    };

}

// ===========================================================================================
// T28 — the party packet layer. Research: status/PARTY-DESIGN.md.
//
// Parties are ARBITER-owned: the Arbiter keeps membership in RAM, fans S_PARTY_* out to the
// members itself, and mirrors the party to World only so World can do loot / exp / instance
// rules. Nothing about a party is persisted (PARTY-DESIGN.md section 4 proves it), so there is
// no store here and no PERSISTENCE-MAP row.
//
// NOTHING IS WIRED UP. This is the pure codec; the PartyManager and the session fan-out need
// T27's per-session routing and a two-client capture. No capture contains a single party frame,
// so the tests below are golden tests against the decompiled writers and the .def files, not
// byte-exact tests against captured bytes. That is the one weakness of the task and it is
// stated here rather than hidden.
//
// Frame is [u32 len][u16 opcode][payload]; every offset in the comments is FRAME-relative
// (payload offset = frame offset - 6), matching how the decompile quotes them.
// ===========================================================================================
public static class PartyPackets
{
    /// <summary>This Arbiter's PlanetId. ServerConfig.xml planetId; DAT_140e2d020 in the
    /// decompile; 0x0AF0 = 2800 in every capture, and the same 2800 sits inside every gameId.</summary>
    public const int PlanetId = 2800;

    /// <summary>Non-raid party capacity. Party::New_AddMember: `4 &lt; memberCount` rejects.</summary>
    public const int MaxPartyMembers = 5;
    /// <summary>Raid capacity, and the fixed size of Party's member table (Party+0xD8, 30 slots).</summary>
    public const int MaxRaidMembers = 30;
    /// <summary>PartyMemberBasicInfo, the record AS_DO_CREATE_PARTY carries N of, raw.</summary>
    public const int MemberBasicInfoSize = 0xA0;

    // ---- Arbiter -> World ----
    public const ushort AS_DO_CREATE_PARTY = 0x139E;
    public const ushort AS_DO_ADD_PARTY_MEMBER = 0x139F;
    public const ushort AS_DO_REMOVE_PARTY_MEMBER = 0x13A0;
    public const ushort AS_DO_DISMISS_PARTY = 0x13A1;
    public const ushort AS_DO_EXTEND_PARTY = 0x13A2;
    public const ushort AS_DO_SWAP_PARTY = 0x13A3;
    public const ushort AS_DO_SET_PARTY_MANAGER = 0x13A4;
    public const ushort AS_DO_CHANGE_PARTY_MEMBER_AUTHORITY = 0x13A5;
    public const ushort AS_DO_SET_LOOTING_METHOD = 0x13A6;
    public const ushort AS_DO_SET_PARTY_OWNER = 0x13A7;
    public const ushort AS_DISMISS_PARTY = 0x13BA;
    public const ushort AS_PARTY_LOOTING_METHOD = 0x13BB;
    public const ushort AS_BAN_PARTY_MEMBER = 0x13BC;
    /// <summary>T64. Sent per member right after AS_DO_CREATE_PARTY - cap_social.log seq 753.</summary>
    public const ushort AS_REQUEST_REFRESH_PARTY_INFO = 0x13AD;
    /// <summary>T64. Sent per member alongside it, with IsMatching = 0.</summary>
    public const ushort AS_CHANGE_EVENT_MATCHING_STATE = 0x15CD;
    /// <summary>T64. The dungeon half of the party-match window push - cap_social.log seq 1699.</summary>
    public const ushort AS_VIEW_INTER_PARTY_MATCH_DUNGEON_LIST_EXTENDED = 0x1644;
    /// <summary>T64. The battleground half - cap_social.log seq 1723.</summary>
    public const ushort AS_VIEW_INTER_PARTY_MATCH_BATTLEFIELD_LIST_EXTENDED = 0x1645;

    // ---- World -> Arbiter ----
    public const ushort SA_JOIN_PARTY = 0x1395;
    public const ushort SA_LEAVE_PARTY = 0x1396;
    public const ushort SA_DISMISS_PARTY = 0x1397;
    public const ushort SA_KICK_PARTY = 0x1398;
    public const ushort SA_EXTEND_PARTY = 0x1399;
    public const ushort SA_SWAP_PARTY = 0x139A;
    public const ushort SA_CHANGE_PARTY_MANAGER = 0x139B;
    public const ushort SA_CHANGE_PARTY_MEMBER_AUTHORITY = 0x139C;
    public const ushort SA_CHANGE_LOOTING_METHOD = 0x139D;
    public const ushort SA_JOIN_PARTY_IN_ARBITER = 0x13AB;
    public const ushort SA_MERGE_PARTY_TO_RAID = 0x13AC;
    public const ushort SA_BYPASS_TO_GROUP = 0x13F8;

    // ---- Client opcodes (data.json maps."376012"; the opcode= comments in the MASTER_FINAL
    //      .def files are from another build and are WRONG) ----
    public const ushort C_APPLY_PARTY = 0xA889;
    public const ushort C_REPLY_INTER_PARTY_MAKE = 0xBD04;
    public const ushort C_DISMISS_PARTY = 0xC8B9;
    public const ushort C_BAN_PARTY_MEMBER = 0x59C1;
    public const ushort C_PARTY_LOOTING_METHOD = 0x5D24;
    public const ushort C_MERGE_PARTY_TO_RAID = 0xB8D0;
    public const ushort C_REQUEST_PARTY_INFO = 0xFD35;
    public const ushort S_PARTY_MEMBER_LIST = 0x8BC6;
    public const ushort S_LEAVE_PARTY = 0x9A8E;
    public const ushort S_PARTY_LOOTING_METHOD = 0x63C0;

    /// <summary>
    /// Minimum FRAME length each SA_ handler demands. A short frame is not a dropped packet on
    /// the real Arbiter - Handler_SA_JOIN_PARTY logs
    /// "Arbiter &lt;-&gt; World PDL Version Mismatch! Bye :(" and kills the connection - so these
    /// sizes have to be exact. Our parsers return null instead, which is the safe analogue.
    /// </summary>
    public static int MinFrameLength(ushort op) => op switch
    {
        SA_JOIN_PARTY => 0x56,
        SA_LEAVE_PARTY => 0x1A,
        SA_DISMISS_PARTY => 0x12,
        SA_KICK_PARTY => 0x1F,
        SA_EXTEND_PARTY => 0x13,
        SA_SWAP_PARTY => 0x1A,
        SA_CHANGE_PARTY_MANAGER => 0x1A,
        SA_CHANGE_PARTY_MEMBER_AUTHORITY => 0x1B,
        SA_CHANGE_LOOTING_METHOD => 0x25,
        SA_JOIN_PARTY_IN_ARBITER => 0x0F,
        SA_MERGE_PARTY_TO_RAID => 0x16,
        SA_BYPASS_TO_GROUP => 0x22,
        _ => 0,
    };

    // ---- PDId: the cross-server player key {int PlanetId, int UserDbId}, packed into an i64
    //      with PlanetId in the LOW dword. AS_DISMISS_PARTY / AS_PARTY_LOOTING_METHOD /
    //      AS_BAN_PARTY_MEMBER carry it that way (dumper calls the field UserPDId). ----

    public static long PackPDId(int planetId, int userDbId)
        => (long)(((ulong)(uint)userDbId << 32) | (uint)planetId);

    public static (int planetId, int userDbId) UnpackPDId(long pdId)
        => ((int)(uint)(ulong)pdId, (int)(uint)((ulong)pdId >> 32));

    // -------------------------------------------------------------------------------------
    // PartyMemberBasicInfo - 0xA0 bytes, the wire form of a member.
    // The first 0xA0 bytes of Party's own PartyMemberInfo (Party+0x1C8, stride 0xC0); the
    // stride is confirmed twice in PartyManager::New_CreateParty (Arb_part_079.c) - the
    // encoder bound check `(int)param_1[2] < *(int *)param_1[1] + 0xa0` and `puVar25 + 0x28`.
    //   [0x00] i32 PlanetId   [0x04] i32 UserDbId   [0x08] i64 GameId (masked 0x7FFF...)
    //   [0x10] i32 Level      [0x14] i32 Class      [0x18] i32 Race    [0x1C] i32 Gender
    //   [0x20] i32 Role (-1)  [0x24] wchar Name[0x25]
    //   [0x6E] u8 AuthorityAboutInvitation  [0x6F] u8 Alive  [0x70] u8 Online
    //   [0x74] i32 AchievementGrade  [0x78] i32 UserAwakenGrade
    // -------------------------------------------------------------------------------------
    public const int MemberNameOffset = 0x24;
    public const int MemberNameMaxChars = 0x25;

    public readonly record struct PartyMember(
        int PlanetId, int UserDbId, ulong GameId, int Level, int Class, int Race, int Gender,
        int Role, string Name, bool CanInvite, bool Alive, bool Online,
        int AchievementGrade, int AwakenGrade);

    /// <summary>One 0xA0-byte PartyMemberBasicInfo. The name is UTF-16LE, NUL-terminated,
    /// truncated to 0x24 characters so the terminator always fits the 0x25-wchar field.</summary>
    public static byte[] BuildMemberBasicInfo(in PartyMember m)
    {
        var b = new byte[MemberBasicInfoSize];
        BitConverter.GetBytes(m.PlanetId).CopyTo(b, 0x00);
        BitConverter.GetBytes(m.UserDbId).CopyTo(b, 0x04);
        BitConverter.GetBytes(m.GameId & 0x7FFFFFFFFFFFFFFFUL).CopyTo(b, 0x08);
        BitConverter.GetBytes(m.Level).CopyTo(b, 0x10);
        BitConverter.GetBytes(m.Class).CopyTo(b, 0x14);
        BitConverter.GetBytes(m.Race).CopyTo(b, 0x18);
        BitConverter.GetBytes(m.Gender).CopyTo(b, 0x1C);
        BitConverter.GetBytes(m.Role).CopyTo(b, 0x20);
        WriteName(b, MemberNameOffset, m.Name, MemberNameMaxChars);
        b[0x6E] = (byte)(m.CanInvite ? 1 : 0);
        b[0x6F] = (byte)(m.Alive ? 1 : 0);
        b[0x70] = (byte)(m.Online ? 1 : 0);
        BitConverter.GetBytes(m.AchievementGrade).CopyTo(b, 0x74);
        BitConverter.GetBytes(m.AwakenGrade).CopyTo(b, 0x78);
        return b;
    }

    /// <summary>Inverse of <see cref="BuildMemberBasicInfo"/>; null when the record is short.</summary>
    public static PartyMember? ParseMemberBasicInfo(byte[] b, int off)
    {
        if (b.Length < off + MemberBasicInfoSize) return null;
        return new PartyMember(
            BitConverter.ToInt32(b, off + 0x00), BitConverter.ToInt32(b, off + 0x04),
            BitConverter.ToUInt64(b, off + 0x08),
            BitConverter.ToInt32(b, off + 0x10), BitConverter.ToInt32(b, off + 0x14),
            BitConverter.ToInt32(b, off + 0x18), BitConverter.ToInt32(b, off + 0x1C),
            BitConverter.ToInt32(b, off + 0x20),
            ReadName(b, off + MemberNameOffset, MemberNameMaxChars),
            b[off + 0x6E] != 0, b[off + 0x6F] != 0, b[off + 0x70] != 0,
            BitConverter.ToInt32(b, off + 0x74), BitConverter.ToInt32(b, off + 0x78));
    }

    private static void WriteName(byte[] b, int off, string name, int maxChars)
    {
        if (string.IsNullOrEmpty(name)) return;
        int n = Math.Min(name.Length, maxChars - 1);
        for (int i = 0; i < n; i++) BitConverter.GetBytes((ushort)name[i]).CopyTo(b, off + i * 2);
    }

    private static string ReadName(byte[] b, int off, int maxChars)
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < maxChars && off + i * 2 + 1 < b.Length; i++)
        {
            ushort c = BitConverter.ToUInt16(b, off + i * 2);
            if (c == 0) break;
            sb.Append((char)c);
        }
        return sb.ToString();
    }

    // -------------------------------------------------------------------------------------
    // Loot settings - the seven fields that travel together in AS_DO_SET_LOOTING_METHOD,
    // AS_PARTY_LOOTING_METHOD, SA_CHANGE_LOOTING_METHOD, C_/S_PARTY_LOOTING_METHOD and inside
    // S_PARTY_MEMBER_LIST. Party fields +0xA8, +0xAC, +0xB0, +0xB4, +0xB5, +0xB8, +0xBC.
    // BoundOnLootItemDistributionMethod is MISSING from AS_DO_SET_LOOTING_METHOD.1.def and
    // SA_CHANGE_LOOTING_METHOD.1.def - see PARTY-DESIGN.md section 6.3.
    // -------------------------------------------------------------------------------------
    public readonly record struct LootSettings(
        int Method, int RareGradeForDicing, int RareItemDistributionMethod,
        bool EquipmentForDicing, bool FindClassForDicing,
        int BoundOnLootItemDistributionMethod, bool ForbidLootingInBattle);

    // ---------------------------------- A -> W builders ----------------------------------

    /// <summary>
    /// AS_DO_CREATE_PARTY (0x139E). Fixed part 0x38 frame bytes, then N x 0xA0 member records.
    ///   [06] u32 memberListOffset (frame-rel)   [0A] u32 memberListBYTES
    ///
    /// <para><b>T64: [0A] is a BYTE LENGTH, not an element count.</b> The 376-byte frame in
    /// cap_social.log seq 748 carries two members and puts 0x140 = 320 = 2 x 0xA0 there, and
    /// World reads it as a byte length: <c>Handler_AS_DO_CREATE_PARTY</c>
    /// (WorldServer.exe.c:2984602) computes <c>(*(int *)(pkt + 10) - 1) / 0xa0 + 1</c>, the same
    /// ceiling-divide <c>SDB_ITEM_SINGLE</c> does with 0x358. Before T64 we wrote the element
    /// count, so World saw a two-member party as ONE member (2 -&gt; (2-1)/160+1 = 1) and the
    /// second member was never in its mirror.</para>
    ///   [0E] i64 PartyId  [16] i32 OwnerPlanetId  [1A] i32 ManagerPlanetId  [1E] i32 ManagerDbId
    ///   [22] i32 MaxMemberCount  [26] i32 PartyType  [2A] u8 DungeonClearCompensation
    ///   [2B] i32 DungeonId  [2F] u8 Raid  [30] i32 TeamIndex  [34] i32 BattleFieldId
    /// Writer FUN_1407aebe0 (Arb_part_066.c:18323).
    /// </summary>
    public static byte[] BuildDoCreateParty(
        long partyId, int ownerPlanetId, int managerPlanetId, int managerDbId,
        int maxMemberCount, int partyType, bool dungeonClearCompensation, int dungeonId,
        bool raid, int teamIndex, int battleFieldId, IReadOnlyList<PartyMember> members)
    {
        const int fixedPayload = 0x38 - 6;                 // 0x32
        var p = new byte[fixedPayload + members.Count * MemberBasicInfoSize];
        BitConverter.GetBytes((uint)0x38).CopyTo(p, 0x00); // list offset, frame-relative
        BitConverter.GetBytes((uint)(members.Count * MemberBasicInfoSize)).CopyTo(p, 0x04);
        BitConverter.GetBytes(partyId).CopyTo(p, 0x08);
        BitConverter.GetBytes(ownerPlanetId).CopyTo(p, 0x10);
        BitConverter.GetBytes(managerPlanetId).CopyTo(p, 0x14);
        BitConverter.GetBytes(managerDbId).CopyTo(p, 0x18);
        BitConverter.GetBytes(maxMemberCount).CopyTo(p, 0x1C);
        BitConverter.GetBytes(partyType).CopyTo(p, 0x20);
        p[0x24] = (byte)(dungeonClearCompensation ? 1 : 0);
        BitConverter.GetBytes(dungeonId).CopyTo(p, 0x25);
        p[0x29] = (byte)(raid ? 1 : 0);
        BitConverter.GetBytes(teamIndex).CopyTo(p, 0x2A);
        BitConverter.GetBytes(battleFieldId).CopyTo(p, 0x2E);
        for (int i = 0; i < members.Count; i++)
            BuildMemberBasicInfo(members[i]).CopyTo(p, fixedPayload + i * MemberBasicInfoSize);
        return p;
    }

    /// <summary>
    /// AS_DO_ADD_PARTY_MEMBER (0x139F), 0x43 frame bytes plus the name.
    ///   [06] u32 nameOffset (frame-rel)  [0A] i64 PartyId  [12] i32 MemberPlanetId
    ///   [16] i32 MemberDbId  [1A] i64 GameId  [22] i32 Level  [26] i32 Class  [2A] i32 Race
    ///   [2E] i32 Gender  [32] i32 Role  [36] u8 AuthorityAboutInvitation  [37] u8 Alive
    ///   [38] u8 Online  [39] i32 AchievementGrade  [3D] i32 UserAwakenGrade
    ///   [41] u8 SupplementCompensation  [42] u8 IsSoloMatching  [43] wstr Name
    /// Writer FUN_1407ae450 (Arb_part_067.c:10121). The .def is missing [36] and [41].
    /// </summary>
    public static byte[] BuildDoAddPartyMember(
        long partyId, in PartyMember m, bool supplementCompensation = false, bool isSoloMatching = false)
    {
        const int fixedPayload = 0x43 - 6;                 // 0x3D
        var name = m.Name ?? string.Empty;
        var p = new byte[fixedPayload + (name.Length + 1) * 2];
        BitConverter.GetBytes((uint)0x43).CopyTo(p, 0x00); // name offset, frame-relative
        BitConverter.GetBytes(partyId).CopyTo(p, 0x04);
        BitConverter.GetBytes(m.PlanetId).CopyTo(p, 0x0C);
        BitConverter.GetBytes(m.UserDbId).CopyTo(p, 0x10);
        BitConverter.GetBytes(m.GameId & 0x7FFFFFFFFFFFFFFFUL).CopyTo(p, 0x14);
        BitConverter.GetBytes(m.Level).CopyTo(p, 0x1C);
        BitConverter.GetBytes(m.Class).CopyTo(p, 0x20);
        BitConverter.GetBytes(m.Race).CopyTo(p, 0x24);
        BitConverter.GetBytes(m.Gender).CopyTo(p, 0x28);
        BitConverter.GetBytes(m.Role).CopyTo(p, 0x2C);
        p[0x30] = (byte)(m.CanInvite ? 1 : 0);
        p[0x31] = (byte)(m.Alive ? 1 : 0);
        p[0x32] = (byte)(m.Online ? 1 : 0);
        BitConverter.GetBytes(m.AchievementGrade).CopyTo(p, 0x33);
        BitConverter.GetBytes(m.AwakenGrade).CopyTo(p, 0x37);
        p[0x3B] = (byte)(supplementCompensation ? 1 : 0);
        p[0x3C] = (byte)(isSoloMatching ? 1 : 0);
        for (int i = 0; i < name.Length; i++)
            BitConverter.GetBytes((ushort)name[i]).CopyTo(p, fixedPayload + i * 2);
        return p;
    }

    /// <summary>AS_DO_REMOVE_PARTY_MEMBER (0x13A0): [i64 PartyId][i32 PlanetId][i32 UserDbId].</summary>
    public static byte[] BuildDoRemovePartyMember(long partyId, int planetId, int userDbId)
    {
        var p = new byte[16];
        BitConverter.GetBytes(partyId).CopyTo(p, 0);
        BitConverter.GetBytes(planetId).CopyTo(p, 8);
        BitConverter.GetBytes(userDbId).CopyTo(p, 12);
        return p;
    }

    /// <summary>AS_DO_DISMISS_PARTY (0x13A1): [i64 PartyId].</summary>
    public static byte[] BuildDoDismissParty(long partyId) => BitConverter.GetBytes(partyId);

    /// <summary>AS_DO_EXTEND_PARTY (0x13A2): [i64 PartyId][u8 PartyToRaid].</summary>
    public static byte[] BuildDoExtendParty(long partyId, bool partyToRaid)
    {
        var p = new byte[9];
        BitConverter.GetBytes(partyId).CopyTo(p, 0);
        p[8] = (byte)(partyToRaid ? 1 : 0);
        return p;
    }

    /// <summary>AS_DO_SWAP_PARTY (0x13A3): [i64 PartyId][i32 SlotIndex1][i32 SlotIndex2].
    /// Slot indices are the Party+0xD8 table indices and are wire-visible, so they must be
    /// stable - see PARTY-DESIGN.md section 7 rule 1.</summary>
    public static byte[] BuildDoSwapParty(long partyId, int slot1, int slot2)
    {
        var p = new byte[16];
        BitConverter.GetBytes(partyId).CopyTo(p, 0);
        BitConverter.GetBytes(slot1).CopyTo(p, 8);
        BitConverter.GetBytes(slot2).CopyTo(p, 12);
        return p;
    }

    /// <summary>AS_DO_SET_PARTY_MANAGER (0x13A4): [i64 PartyId][i32 PlanetId][i32 UserDbId].</summary>
    public static byte[] BuildDoSetPartyManager(long partyId, int planetId, int userDbId)
        => BuildDoRemovePartyMember(partyId, planetId, userDbId);   // identical shape

    // ---------------------------------- T64: the four A->W pushes the capture added ----

    /// <summary>
    /// AS_REQUEST_REFRESH_PARTY_INFO (0x13AD): <c>i32 UserDbId@06</c>, frame 10. Dumper guard
    /// <c>9 &lt; len</c> (Arb_part_011.c:19327). The real Arbiter sends exactly one per member
    /// immediately after AS_DO_CREATE_PARTY - cap_social.log seq 753 carries UserDbId 2 then
    /// 1002, the two members of the party created at seq 748.
    /// </summary>
    public static byte[] BuildAsRequestRefreshPartyInfo(int userDbId) => BitConverter.GetBytes(userDbId);

    /// <summary>
    /// AS_CHANGE_EVENT_MATCHING_STATE (0x15CD): <c>i32 UserDbId@06, u8 IsMatching@0A</c>,
    /// frame 11. Dumper guard <c>10 &lt; len</c> (Arb_part_011.c:6886).
    ///
    /// <para>Joining a party takes you out of solo matching, which is why every one of these in
    /// the capture carries IsMatching = 0. The real Arbiter sent <b>three</b> for member 2 and
    /// <b>two</b> for member 1002 (seq 751-753) - one per matching queue the member was in, we
    /// assume, since nothing else distinguishes them. We send one per member, which is the only
    /// count the capture justifies for a member we never registered in a queue.</para>
    /// </summary>
    public static byte[] BuildAsChangeEventMatchingState(int userDbId, bool isMatching)
    {
        var p = new byte[5];
        BitConverter.GetBytes(userDbId).CopyTo(p, 0);
        p[4] = (byte)(isMatching ? 1 : 0);
        return p;
    }

    /// <summary>
    /// AS_VIEW_INTER_PARTY_MATCH_DUNGEON_LIST_EXTENDED (0x1644) and
    /// AS_VIEW_INTER_PARTY_MATCH_BATTLEFIELD_LIST_EXTENDED (0x1645) are one shape:
    /// <c>u32 listOffset@06, u32 listBytes@0A, i32 UserDbId@0E</c>, frame 0x12. The dumpers
    /// (Arb_part_012.c:8657 / :8529) name only UserDbId, at +0x0E, with guard
    /// <c>0x11 &lt; len</c>.
    ///
    /// <para>Both captured frames carry an <b>empty list with offset 0</b>, not offset = frame
    /// length. That is the opposite of the <c>FetchWork::ResponseFailure</c> convention
    /// (CONTRACT-DESIGN.md section 3.2) and is pinned by a test, because the two conventions
    /// coexist in this protocol and guessing wrong is a silent read of the wrong bytes.</para>
    /// </summary>
    public static byte[] BuildAsViewInterPartyMatchList(int userDbId)
    {
        var p = new byte[12];
        // [06] offset 0, [0A] count 0 - exactly what cap_social.log seq 1699/1723 put there.
        BitConverter.GetBytes(userDbId).CopyTo(p, 8);
        return p;
    }

    /// <summary>Frame minimums for the four T64 pushes, for the tests and the fuzz suite.</summary>
    public static int MinFrameLengthArbiterPush(ushort op) => op switch
    {
        AS_REQUEST_REFRESH_PARTY_INFO => 0x0A,
        AS_CHANGE_EVENT_MATCHING_STATE => 0x0B,
        AS_VIEW_INTER_PARTY_MATCH_DUNGEON_LIST_EXTENDED => 0x12,
        AS_VIEW_INTER_PARTY_MATCH_BATTLEFIELD_LIST_EXTENDED => 0x12,
        _ => 0,
    };

    /// <summary>AS_DO_CHANGE_PARTY_MEMBER_AUTHORITY (0x13A5):
    /// [i64 PartyId][i32 PlanetId][i32 UserDbId][u8 AuthorityAboutInvitation].</summary>
    public static byte[] BuildDoChangeMemberAuthority(long partyId, int planetId, int userDbId, bool canInvite)
    {
        var p = new byte[17];
        BitConverter.GetBytes(partyId).CopyTo(p, 0);
        BitConverter.GetBytes(planetId).CopyTo(p, 8);
        BitConverter.GetBytes(userDbId).CopyTo(p, 12);
        p[16] = (byte)(canInvite ? 1 : 0);
        return p;
    }

    /// <summary>
    /// AS_DO_SET_LOOTING_METHOD (0x13A6), 0x21 frame bytes:
    ///   [06] i64 PartyId  [0E] i32 Method  [12] i32 RareGrade  [16] i32 RareDistribution
    ///   [1A] u8 EquipmentForDicing  [1B] u8 FindClassForDicing
    ///   [1C] i32 BoundOnLootItemDistributionMethod  [20] u8 ForbidLootingInBattle
    /// </summary>
    public static byte[] BuildDoSetLootingMethod(long partyId, in LootSettings s)
    {
        var p = new byte[0x21 - 6];
        BitConverter.GetBytes(partyId).CopyTo(p, 0x00);
        BitConverter.GetBytes(s.Method).CopyTo(p, 0x08);
        BitConverter.GetBytes(s.RareGradeForDicing).CopyTo(p, 0x0C);
        BitConverter.GetBytes(s.RareItemDistributionMethod).CopyTo(p, 0x10);
        p[0x14] = (byte)(s.EquipmentForDicing ? 1 : 0);
        p[0x15] = (byte)(s.FindClassForDicing ? 1 : 0);
        BitConverter.GetBytes(s.BoundOnLootItemDistributionMethod).CopyTo(p, 0x16);
        p[0x1A] = (byte)(s.ForbidLootingInBattle ? 1 : 0);
        return p;
    }

    /// <summary>AS_DO_SET_PARTY_OWNER (0x13A7): [i64 PartyId][i32 OwnerPlanetId].</summary>
    public static byte[] BuildDoSetPartyOwner(long partyId, int ownerPlanetId)
    {
        var p = new byte[12];
        BitConverter.GetBytes(partyId).CopyTo(p, 0);
        BitConverter.GetBytes(ownerPlanetId).CopyTo(p, 8);
        return p;
    }

    /// <summary>AS_DISMISS_PARTY (0x13BA): [i64 UserPDId][i32 PartyMemberCount].
    /// A request, not a mirror - World runs the vote and answers with SA_DISMISS_PARTY.</summary>
    public static byte[] BuildAsDismissParty(int planetId, int userDbId, int onlineMemberCount)
    {
        var p = new byte[12];
        BitConverter.GetBytes(PackPDId(planetId, userDbId)).CopyTo(p, 0);
        BitConverter.GetBytes(onlineMemberCount).CopyTo(p, 8);
        return p;
    }

    /// <summary>AS_PARTY_LOOTING_METHOD (0x13BB): [i64 UserPDId] + the seven loot fields +
    /// [i32 PartyMemberCount]. Also a request; World answers SA_CHANGE_LOOTING_METHOD.</summary>
    public static byte[] BuildAsPartyLootingMethod(int planetId, int userDbId, in LootSettings s, int onlineMemberCount)
    {
        var p = new byte[0x25 - 6];
        BitConverter.GetBytes(PackPDId(planetId, userDbId)).CopyTo(p, 0x00);
        BitConverter.GetBytes(s.Method).CopyTo(p, 0x08);
        BitConverter.GetBytes(s.RareGradeForDicing).CopyTo(p, 0x0C);
        BitConverter.GetBytes(s.RareItemDistributionMethod).CopyTo(p, 0x10);
        p[0x14] = (byte)(s.EquipmentForDicing ? 1 : 0);
        p[0x15] = (byte)(s.FindClassForDicing ? 1 : 0);
        BitConverter.GetBytes(s.BoundOnLootItemDistributionMethod).CopyTo(p, 0x16);
        p[0x1A] = (byte)(s.ForbidLootingInBattle ? 1 : 0);
        BitConverter.GetBytes(onlineMemberCount).CopyTo(p, 0x1B);
        return p;
    }

    /// <summary>AS_BAN_PARTY_MEMBER (0x13BC):
    /// [i64 UserPDId][i32 BanUserPlanetId][i32 BanUserDbId][i32 PartyMemberCount].</summary>
    public static byte[] BuildAsBanPartyMember(int planetId, int userDbId, int banPlanetId, int banDbId, int onlineMemberCount)
    {
        var p = new byte[20];
        BitConverter.GetBytes(PackPDId(planetId, userDbId)).CopyTo(p, 0);
        BitConverter.GetBytes(banPlanetId).CopyTo(p, 8);
        BitConverter.GetBytes(banDbId).CopyTo(p, 12);
        BitConverter.GetBytes(onlineMemberCount).CopyTo(p, 16);
        return p;
    }

    // ---------------------------------- W -> A parsers ----------------------------------
    // Every parser takes the PAYLOAD (frame minus the 6-byte header) and returns null when it
    // is shorter than MinFrameLength(op) - 6.

    private static bool TooShort(ushort op, byte[] payload) => payload.Length < MinFrameLength(op) - 6;

    public readonly record struct SaJoinParty(
        int OwnerPlanetId, int MemberPlanetId, int MemberDbId, int InviteePlanetId, int InviteeDbId,
        ulong InviteeGameId, int InviteeLevel, int InviteeClass, int InviteeRace, int InviteeGender,
        int InviteeRole, bool Alive, bool Online, int AchievementGrade, int AwakenGrade,
        long PartyId, int PartyType, bool IsAnonymous, bool Raid, int MaxMemberCount);

    /// <summary>SA_JOIN_PARTY (0x1395), fixed frame 0x56. Handler FUN_140727b90 (Arb_part_062.c:8300).</summary>
    public static SaJoinParty? ParseSaJoinParty(byte[] p)
    {
        if (TooShort(SA_JOIN_PARTY, p)) return null;
        return new SaJoinParty(
            BitConverter.ToInt32(p, 0x00), BitConverter.ToInt32(p, 0x04), BitConverter.ToInt32(p, 0x08),
            BitConverter.ToInt32(p, 0x0C), BitConverter.ToInt32(p, 0x10), BitConverter.ToUInt64(p, 0x18),
            BitConverter.ToInt32(p, 0x20), BitConverter.ToInt32(p, 0x24), BitConverter.ToInt32(p, 0x28),
            BitConverter.ToInt32(p, 0x2C), BitConverter.ToInt32(p, 0x30),
            p[0x34] != 0, p[0x35] != 0,
            BitConverter.ToInt32(p, 0x36), BitConverter.ToInt32(p, 0x3A),
            BitConverter.ToInt64(p, 0x3E), BitConverter.ToInt32(p, 0x46),
            p[0x4A] != 0, p[0x4B] != 0, BitConverter.ToInt32(p, 0x4C));
    }

    public readonly record struct SaJoinPartyInArbiter(string MemberName, string InviteeName, bool Raid);

    /// <summary>
    /// SA_JOIN_PARTY_IN_ARBITER (0x13AB), min frame 0x0F. Dumper FUN_14021bb90 (Arb_part_016.c:13076),
    /// handler FUN_140728080 (Arb_part_062.c:8534): [u32 MemberName ref @frame 06]
    /// [u32 InviteeName ref @frame 0A][u8 Raid @frame 0E]. The two refs are FRAME offsets to
    /// NUL-terminated UTF-16LE, and the handler resolves BOTH BY NAME
    /// (FUN_14082dc50(userTable, name, 3)) before calling PartyManager::JoinParty - there are no
    /// db-ids in this frame at all.
    ///
    /// T65: this - not SA_JOIN_PARTY (0x1395) - is how a party is actually born. cap_social.log
    /// holds one 0x13AB (seq 747, "Test" + "two", Raid = 0) and zero 0x1395; seq 748 is the
    /// AS_DO_CREATE_PARTY it produced.
    /// </summary>
    public static SaJoinPartyInArbiter? ParseSaJoinPartyInArbiter(byte[] p)
    {
        if (TooShort(SA_JOIN_PARTY_IN_ARBITER, p)) return null;
        return new SaJoinPartyInArbiter(
            PartyWString(p, BitConverter.ToUInt32(p, 0)),
            PartyWString(p, BitConverter.ToUInt32(p, 4)),
            p[8] != 0);
    }

    /// <summary>Reads a NUL-terminated UTF-16LE string at a FRAME offset out of a PAYLOAD buffer.
    /// 0 and out-of-range offsets give "", which is what the real handler does (it substitutes the
    /// shared empty string at DAT_140d3e020). The offset is read UNSIGNED so a negative-looking
    /// value can never index backwards.</summary>
    private static string PartyWString(byte[] payload, uint frameOffset)
    {
        if (frameOffset < 6) return string.Empty;
        long at = (long)frameOffset - 6;
        if (at >= payload.Length) return string.Empty;
        var sb = new System.Text.StringBuilder();
        for (long i = at; i + 1 < payload.Length; i += 2)
        {
            char ch = (char)(payload[i] | (payload[i + 1] << 8));
            if (ch == '\0') break;
            sb.Append(ch);
        }
        return sb.ToString();
    }

    public readonly record struct SaLeaveParty(int OwnerPlanetId, long PartyId, int MemberPlanetId, int MemberDbId);

    /// <summary>SA_LEAVE_PARTY (0x1396): [i32 OwnerPlanetId][i64 PartyId][i32 PlanetId][i32 UserDbId].</summary>
    public static SaLeaveParty? ParseSaLeaveParty(byte[] p)
    {
        if (TooShort(SA_LEAVE_PARTY, p)) return null;
        return new SaLeaveParty(BitConverter.ToInt32(p, 0), BitConverter.ToInt64(p, 4),
            BitConverter.ToInt32(p, 12), BitConverter.ToInt32(p, 16));
    }

    public readonly record struct SaPartyActor(int OwnerPlanetId, int MemberPlanetId, int MemberDbId);

    /// <summary>SA_DISMISS_PARTY (0x1397): [i32 OwnerPlanetId][i32 PlanetId][i32 UserDbId].
    /// The same three-int prefix opens 0x1398 / 0x1399 / 0x139A / 0x139B / 0x139D.</summary>
    public static SaPartyActor? ParseSaPartyActor(ushort op, byte[] p)
    {
        if (TooShort(op, p)) return null;
        return new SaPartyActor(BitConverter.ToInt32(p, 0), BitConverter.ToInt32(p, 4), BitConverter.ToInt32(p, 8));
    }

    public readonly record struct SaKickParty(
        int OwnerPlanetId, int MemberPlanetId, int MemberDbId,
        int TargetPlanetId, int TargetDbId, int AgreeCount, bool ByPlayer);

    /// <summary>SA_KICK_PARTY (0x1398), fixed frame 0x1F.</summary>
    public static SaKickParty? ParseSaKickParty(byte[] p)
    {
        if (TooShort(SA_KICK_PARTY, p)) return null;
        return new SaKickParty(
            BitConverter.ToInt32(p, 0), BitConverter.ToInt32(p, 4), BitConverter.ToInt32(p, 8),
            BitConverter.ToInt32(p, 12), BitConverter.ToInt32(p, 16), BitConverter.ToInt32(p, 20), p[24] != 0);
    }

    public readonly record struct SaExtendParty(int OwnerPlanetId, int MemberPlanetId, int MemberDbId, bool PartyToRaid);

    /// <summary>SA_EXTEND_PARTY (0x1399), fixed frame 0x13.</summary>
    public static SaExtendParty? ParseSaExtendParty(byte[] p)
    {
        if (TooShort(SA_EXTEND_PARTY, p)) return null;
        return new SaExtendParty(BitConverter.ToInt32(p, 0), BitConverter.ToInt32(p, 4),
            BitConverter.ToInt32(p, 8), p[12] != 0);
    }

    public readonly record struct SaSwapParty(int OwnerPlanetId, int MemberPlanetId, int MemberDbId, int Slot1, int Slot2);

    /// <summary>SA_SWAP_PARTY (0x139A), fixed frame 0x1A.</summary>
    public static SaSwapParty? ParseSaSwapParty(byte[] p)
    {
        if (TooShort(SA_SWAP_PARTY, p)) return null;
        return new SaSwapParty(BitConverter.ToInt32(p, 0), BitConverter.ToInt32(p, 4),
            BitConverter.ToInt32(p, 8), BitConverter.ToInt32(p, 12), BitConverter.ToInt32(p, 16));
    }

    public readonly record struct SaChangeManager(int OwnerPlanetId, int MemberPlanetId, int MemberDbId, int NewManagerPlanetId, int NewManagerDbId);

    /// <summary>SA_CHANGE_PARTY_MANAGER (0x139B), fixed frame 0x1A.</summary>
    public static SaChangeManager? ParseSaChangeManager(byte[] p)
    {
        if (TooShort(SA_CHANGE_PARTY_MANAGER, p)) return null;
        return new SaChangeManager(BitConverter.ToInt32(p, 0), BitConverter.ToInt32(p, 4),
            BitConverter.ToInt32(p, 8), BitConverter.ToInt32(p, 12), BitConverter.ToInt32(p, 16));
    }

    public readonly record struct SaChangeAuthority(int OwnerPlanetId, int ManagerPlanetId, int ManagerDbId, int MemberPlanetId, int MemberDbId, bool CanInvite);

    /// <summary>SA_CHANGE_PARTY_MEMBER_AUTHORITY (0x139C), fixed frame 0x1B.</summary>
    public static SaChangeAuthority? ParseSaChangeAuthority(byte[] p)
    {
        if (TooShort(SA_CHANGE_PARTY_MEMBER_AUTHORITY, p)) return null;
        return new SaChangeAuthority(BitConverter.ToInt32(p, 0), BitConverter.ToInt32(p, 4),
            BitConverter.ToInt32(p, 8), BitConverter.ToInt32(p, 12), BitConverter.ToInt32(p, 16), p[20] != 0);
    }

    public readonly record struct SaChangeLooting(int OwnerPlanetId, int MemberPlanetId, int MemberDbId, LootSettings Loot);

    /// <summary>SA_CHANGE_LOOTING_METHOD (0x139D), fixed frame 0x25. The .def is missing
    /// BoundOnLootItemDistributionMethod at frame+0x20.</summary>
    public static SaChangeLooting? ParseSaChangeLooting(byte[] p)
    {
        if (TooShort(SA_CHANGE_LOOTING_METHOD, p)) return null;
        return new SaChangeLooting(
            BitConverter.ToInt32(p, 0), BitConverter.ToInt32(p, 4), BitConverter.ToInt32(p, 8),
            new LootSettings(
                BitConverter.ToInt32(p, 12), BitConverter.ToInt32(p, 16), BitConverter.ToInt32(p, 20),
                p[24] != 0, p[25] != 0, BitConverter.ToInt32(p, 26), p[30] != 0));
    }

    public readonly record struct SaBypassToGroup(int GroupType, long GroupId, int ObjectPlanetId, int ObjectId, byte[] Packet);

    /// <summary>
    /// SA_BYPASS_TO_GROUP (0x13F8), fixed frame 0x22 - World asking the ARBITER to fan a client
    /// packet out to a party. Handler FUN_140721360 (Arb_part_062.c:3820) ->
    /// PartyManager::BroadcastPacketToParty. TeraSharp ignores this opcode today, so a party
    /// would silently receive nothing.
    ///   [06] u32 PacketOffset (frame-rel)  [0A] u32 PacketLength  [0E] i32 GroupType
    ///   [12] i64 GroupId  [1A] i32 ObjectPlanetId  [1E] i32 ObjectId  then the raw client packet
    /// </summary>
    public static SaBypassToGroup? ParseSaBypassToGroup(byte[] p)
    {
        if (TooShort(SA_BYPASS_TO_GROUP, p)) return null;
        int start = (int)BitConverter.ToUInt32(p, 0) - 6;       // frame-relative -> payload
        int len = (int)BitConverter.ToUInt32(p, 4);
        if (start < 0 || len < 0 || start + len > p.Length) return null;
        var pkt = new byte[len];
        Array.Copy(p, start, pkt, 0, len);
        return new SaBypassToGroup(BitConverter.ToInt32(p, 8), BitConverter.ToInt64(p, 12),
            BitConverter.ToInt32(p, 20), BitConverter.ToInt32(p, 24), pkt);
    }

    // ------------------------------- client -> Arbiter parsers -------------------------------
    // Hand-written rather than .def-driven: C_REPLY_INTER_PARTY_MAKE.1.def is missing its
    // trailing bool, and the opcode= comments in every MASTER_FINAL .def are from another
    // build. Each takes the client packet BODY (frame minus the 4-byte [len][opcode] header),
    // which is what PacketDispatcher hands a handler.

    /// <summary>C_APPLY_PARTY (0xA889): [i32 playerId]. Handler FUN_1404db920 needs len &gt;= 8.</summary>
    public static int? ParseCApplyParty(byte[] body)
        => body.Length < 4 ? null : BitConverter.ToInt32(body, 0);

    /// <summary>C_REQUEST_PARTY_INFO (0xFD35): [i32 playerId]. Handler FUN_1404e9b50 needs len &gt;= 8.</summary>
    public static int? ParseCRequestPartyInfo(byte[] body)
        => body.Length < 4 ? null : BitConverter.ToInt32(body, 0);

    /// <summary>C_BAN_PARTY_MEMBER (0x59C1): [u32 serverId][u32 playerId]. Handler needs len &gt;= 0xC.</summary>
    public static (uint serverId, uint playerId)? ParseCBanPartyMember(byte[] body)
        => body.Length < 8 ? null : (BitConverter.ToUInt32(body, 0), BitConverter.ToUInt32(body, 4));

    /// <summary>
    /// C_REPLY_INTER_PARTY_MAKE (0xBD04): [i32 partyMakingId][u8 accept].
    /// Handler FUN_1404e5b30 (Arb_part_041.c:6228) needs len &gt;= 9 and reads body+0 and body+4;
    /// the shipped .def stops after partyMakingId and is WRONG.
    /// </summary>
    public static (int partyMakingId, bool accept)? ParseCReplyInterPartyMake(byte[] body)
        => body.Length < 5 ? null : (BitConverter.ToInt32(body, 0), body[4] != 0);

    /// <summary>C_MERGE_PARTY_TO_RAID (0xB8D0): [i64 partyId][u8 accept]. Handler needs len &gt;= 0xD.</summary>
    public static (long partyId, bool accept)? ParseCMergePartyToRaid(byte[] body)
        => body.Length < 9 ? null : (BitConverter.ToInt64(body, 0), body[8] != 0);

    /// <summary>
    /// C_PARTY_LOOTING_METHOD (0x5D24): the seven loot fields, same order as the .def and as
    /// S_PARTY_LOOTING_METHOD. Handler FUN_1404e40a0 needs len &gt;= 0x17 and reads body+0, +4,
    /// +8, +0xC, +0xD, +0xE, +0x12.
    /// </summary>
    public static LootSettings? ParseCPartyLootingMethod(byte[] body)
    {
        if (body.Length < 0x13) return null;
        return new LootSettings(
            BitConverter.ToInt32(body, 0x00), BitConverter.ToInt32(body, 0x04), BitConverter.ToInt32(body, 0x08),
            body[0x0C] != 0, body[0x0D] != 0, BitConverter.ToInt32(body, 0x0E), body[0x12] != 0);
    }

    /// <summary>The body of S_PARTY_LOOTING_METHOD (0x63C0) - the same seven fields, no ref
    /// block (writer Arb_part_067.c:5317 emits u32,u32,u32,u8,u8,u32,u8 straight).</summary>
    public static byte[] BuildSPartyLootingMethodBody(in LootSettings s)
    {
        var b = new byte[0x13];
        BitConverter.GetBytes(s.Method).CopyTo(b, 0x00);
        BitConverter.GetBytes(s.RareGradeForDicing).CopyTo(b, 0x04);
        BitConverter.GetBytes(s.RareItemDistributionMethod).CopyTo(b, 0x08);
        b[0x0C] = (byte)(s.EquipmentForDicing ? 1 : 0);
        b[0x0D] = (byte)(s.FindClassForDicing ? 1 : 0);
        BitConverter.GetBytes(s.BoundOnLootItemDistributionMethod).CopyTo(b, 0x0E);
        b[0x12] = (byte)(s.ForbidLootingInBattle ? 1 : 0);
        return b;
    }
}

// ===========================================================================================
// T29 — the two handshake lists, rebuilt from the Datasheet XMLs. Research: status/HANDSHAKE-DATA.md.
//
// Both lists were hardcoded from captures. Both turn out to be exactly reproducible - set AND
// order, zero delta - from Executable\Datasheet:
//
//   98 dungeon ids (the 0x1581 AS_DUNGEON_TIMELINE_ON_OFF burst)
//       = (DungeonData_<id>.xml INTERSECT ContinentData.xml)
//         MINUS DungeonMatching.xml                       (those go to the dungeon WorldServers)
//         INTERSECT DungeonConstraint.xml[isActive=true]  (the content switch)
//       ascending. The intermediate 173 is independently observable as
//       SA_WORLD_SERVER_STATUS (0x164D)'s InstanceList.
//
//   17 politics unit ids (the 0x1559 AS_POLITICS_UNIT_INFO reply)
//       = PoliticsData.xml <PoliticsUnit politicsUnitId="..."/>, ascending.
//       PolicyDataSheet::Load (Arb_part_009.c:1702) -> GetPoliticsUnitIdList (Arb_part_008.c:13003).
//
// NOT wired into startup: reading the sheets means depending on a folder that belongs to the
// WorldServer install and is not in ship.ps1's payload, and the fallback list is byte-identical.
// The captured lists below are both the fallback and the regression target - the tests assert
// the sheets still reproduce them, so a changed sheet is caught instead of silently shipping a
// stale reply. HANDSHAKE-DATA.md section 6 has the exact wiring if the human wants it.
//
// The 98 ids are ALSO not really an Arbiter-computed list: the real Arbiter echoes them back
// one at a time from World's DSA_DUNGEON_TIMELINE_OPEN_INFO (0x13F2). See HANDSHAKE-DATA.md
// section 0 and the recommended handler in section 6(a).
// ===========================================================================================
public static class HandshakeData
{
    /// <summary>Where Executable\Datasheet lives. TERASHARP_DATASHEET wins; then
    /// &lt;TERASHARP_DATA&gt;\Executable\Datasheet; then the dev-box default.</summary>
    public static string DatasheetDirectory()
    {
        var explicitDir = Environment.GetEnvironmentVariable("TERASHARP_DATASHEET");
        if (!string.IsNullOrEmpty(explicitDir)) return explicitDir;
        var root = Environment.GetEnvironmentVariable("TERASHARP_DATA") ?? @"D:\v100\TERA_SERVER.100";
        return Path.Combine(root, "Executable", "Datasheet");
    }

    /// <summary>
    /// The 98 dungeon ids, from the four sheets. Null when the directory or any required sheet
    /// is missing, so a deployment without the Datasheet folder falls back to the captured list
    /// instead of failing.
    /// </summary>
    public static int[]? LoadDungeonTimelineIds(string datasheetDir)
    {
        if (string.IsNullOrEmpty(datasheetDir) || !Directory.Exists(datasheetDir)) return null;

        var templates = new HashSet<int>();
        const string prefix = "DungeonData_";
        foreach (var f in Directory.EnumerateFiles(datasheetDir, prefix + "*.xml"))
        {
            var stem = Path.GetFileNameWithoutExtension(f);
            if (stem.Length > prefix.Length && int.TryParse(stem.Substring(prefix.Length), out int id))
                templates.Add(id);
        }
        if (templates.Count == 0) return null;

        var continents = ReadIds(datasheetDir, "ContinentData.xml", "Continent", "id");
        var matching = ReadIds(datasheetDir, "DungeonMatching.xml", "Dungeon", "id");
        var active = ReadActiveConstraints(datasheetDir);
        if (continents == null || matching == null || active == null) return null;

        templates.IntersectWith(continents);
        templates.ExceptWith(matching);
        templates.IntersectWith(active);
        var ids = templates.ToArray();
        Array.Sort(ids);                 // World iterates a std::map<int,...>, so ascending
        return ids;
    }

    /// <summary>
    /// The intermediate set: every dungeon world 0 actually serves, i.e. the sheets minus the
    /// ones the dedicated dungeon WorldServers own. Observable on the wire as
    /// SA_WORLD_SERVER_STATUS (0x164D)'s InstanceList - 173 entries in every capture - which is
    /// what makes the four-sheet rule checkable rather than fitted.
    /// </summary>
    public static int[]? LoadWorldInstanceIds(string datasheetDir)
    {
        if (string.IsNullOrEmpty(datasheetDir) || !Directory.Exists(datasheetDir)) return null;
        var templates = new HashSet<int>();
        const string prefix = "DungeonData_";
        foreach (var f in Directory.EnumerateFiles(datasheetDir, prefix + "*.xml"))
        {
            var stem = Path.GetFileNameWithoutExtension(f);
            if (stem.Length > prefix.Length && int.TryParse(stem.Substring(prefix.Length), out int id))
                templates.Add(id);
        }
        var continents = ReadIds(datasheetDir, "ContinentData.xml", "Continent", "id");
        var matching = ReadIds(datasheetDir, "DungeonMatching.xml", "Dungeon", "id");
        if (templates.Count == 0 || continents == null || matching == null) return null;
        templates.IntersectWith(continents);
        templates.ExceptWith(matching);
        var ids = templates.ToArray();
        Array.Sort(ids);
        return ids;
    }

    /// <summary>The 17 politics unit ids from PoliticsData.xml, ascending. Null if absent.</summary>
    public static int[]? LoadPoliticsUnitIds(string datasheetDir)
    {
        var set = ReadIds(datasheetDir, "PoliticsData.xml", "PoliticsUnit", "politicsUnitId");
        if (set == null) return null;
        var ids = set.ToArray();
        Array.Sort(ids);
        return ids;
    }

    /// <summary>
    /// Every &lt;element attribute="N"&gt; in a sheet, with XML comments stripped first.
    /// Stripping matters: DungeonConstraint.xml has 210 &lt;Constraint&gt; tokens and 41 of them
    /// are commented out; keeping those produces the wrong set.
    /// </summary>
    private static HashSet<int>? ReadIds(string dir, string file, string element, string attribute)
    {
        var text = ReadSheet(dir, file);
        if (text == null) return null;
        var set = new HashSet<int>();
        var rx = new Regex("<" + element + "\\b[^>]*\\b" + attribute + "=\"(\\d+)\"");
        foreach (Match m in rx.Matches(text)) set.Add(int.Parse(m.Groups[1].Value));
        return set;
    }

    /// <summary>continentIds of the DungeonConstraint.xml rows with isActive="true".</summary>
    private static HashSet<int>? ReadActiveConstraints(string dir)
    {
        var text = ReadSheet(dir, "DungeonConstraint.xml");
        if (text == null) return null;
        var set = new HashSet<int>();
        foreach (Match m in Regex.Matches(text, "<Constraint\\b([^>]*?)/?>"))
        {
            var attrs = m.Groups[1].Value;
            var active = Regex.Match(attrs, "\\bisActive=\"([^\"]*)\"");
            if (!active.Success || !active.Groups[1].Value.Equals("true", StringComparison.OrdinalIgnoreCase)) continue;
            var cid = Regex.Match(attrs, "\\bcontinentId=\"(\\d+)\"");
            if (cid.Success) set.Add(int.Parse(cid.Groups[1].Value));
        }
        return set;
    }

    private static string? ReadSheet(string dir, string file)
    {
        if (string.IsNullOrEmpty(dir)) return null;
        var path = Path.Combine(dir, file);
        if (!File.Exists(path)) return null;
        try { return Regex.Replace(File.ReadAllText(path), "<!--.*?-->", string.Empty, RegexOptions.Singleline); }
        catch (IOException) { return null; }
    }

    /// <summary>
    /// The 98 ids as the real Arbiter sent them (lobby_tap.log seq 123-124, cap_newchar.log
    /// seq 112-113). Identical to LoadDungeonTimelineIds(), order included; kept as the fallback
    /// and as the regression target. Same values as DbProxyHandlers.PostHandshakeDungeonIds.
    /// </summary>
    public static readonly int[] CapturedDungeonTimelineIds =
    {
        0x834, 0x835, 0x836, 0x837, 0x839,
        0x9C5, 0x9C6, 0x9C7, 0x9C8, 0x9E2,
        0xBB9, 0xBBA, 0xBBB, 0xBBC, 0xBBD, 0xBBE, 0xBBF, 0xBC0, 0xBC1, 0xBC2, 0xBC3, 0xBC4,
        0xBC8, 0xBCC, 0xBD0, 0xBD1, 0xBD4, 0xBD5, 0xBD7, 0xBD8, 0xBD9, 0xBDB, 0xBDD,
        0xC20, 0xC84,
        0x2327, 0x2329, 0x232A, 0x232B, 0x232D, 0x232F, 0x2330, 0x2332, 0x2333, 0x2334, 0x2335,
        0x2336, 0x2339, 0x233A, 0x233B, 0x233C, 0x233D, 0x233E, 0x233F, 0x2344, 0x2347, 0x2348,
        0x2349, 0x234C, 0x234D, 0x234E, 0x234F, 0x2350, 0x2351, 0x2352, 0x2356, 0x235B, 0x235C,
        0x2365, 0x2366, 0x2367, 0x2368, 0x2369, 0x236D, 0x2372, 0x2382, 0x2383, 0x2384,
        0x251F, 0x2521, 0x2522, 0x2523, 0x2524, 0x2525, 0x25D1,
        0x2643, 0x264C, 0x2656, 0x265A, 0x265D, 0x265E, 0x265F, 0x2662, 0x2663, 0x2664, 0x2665,
        0x2666, 0x2669,
    };

    /// <summary>The 17 ids in the captured 0x1559 reply (arb_world.log chunk 7,
    /// [u32 off=14][u32 byteLen=68][int[17]]). Identical to LoadPoliticsUnitIds().</summary>
    public static readonly int[] CapturedPoliticsUnitIds =
    {
        2, 3, 4, 5, 6, 7, 11, 12, 13, 14, 15, 18, 19, 20, 21, 22, 23,
    };
}

// ===========================================================================================
// T36 - the guild codec. Research: status/GUILD-DESIGN.md.
//
// Guilds are the mirror image of parties. A party lives only in Arbiter RAM and is never
// persisted; a guild is persisted BY THE ARBITER, in direct SQL, across ~109 stored procedures,
// and World holds only a read-only mirror that the Arbiter pushes at it (DBS_INIT_GUILD_* at
// boot, AS_*GUILD* deltas afterwards).
//
// The client surface is split. 17 of the guild C_ packets are handled inside the Arbiter; the
// other 14 are handled by WorldServer, which then asks the Arbiter to do the work with an SA_
// frame. TeraSharp only has to build/parse the Arbiter half - the World half travels through
// the bypass tunnel untouched. MinClientLength() / MinFrameLength() mark which is which.
//
// Client opcodes are from data.json maps."376012". That map is not a guess: the Arbiter
// decompile writes the PDL id straight into the packet, and all 31 guild ids that appear in a
// writer or a dumper guard match 376012 exactly (test Guild_client_opcodes_match_the_decompile).
// The `opcode=NNNNN` comments inside the MASTER_FINAL .def files are from another build.
//
// Offsets in the comments below are PACKET-relative for client packets (so the first ref slot
// is 0x04, after the [u16 len][u16 opcode] header) and FRAME-relative for inter-server frames
// (so the first field is 0x06, after [u32 len][u16 opcode]) - matching how the decompile quotes
// them. The parsers/builders here take and return the BODY / PAYLOAD, i.e. header removed.
// ===========================================================================================
public static class GuildPackets
{
    // ---- sizes the real Arbiter enforces ----
    /// <summary>GuildData, the record at Guild+0x88 that DBS_INIT_GUILD_DATA and
    /// AS_LOAD_GUILD_DATA carry raw. Guild::BroadcastGuildData sends exactly this many bytes
    /// (`local_68[0] = 0x23a0;`, Arb_part_046.c:14001).</summary>
    public const int GuildDataSize = 0x23A0;
    /// <summary>GuildMemberData, the element of the std::map at Guild+0x58. The load loop
    /// advances the vector by 0xF0 per row (Arb_part_069.c, LoadAllGuildMemberData).</summary>
    public const int GuildMemberDataSize = 0xF0;
    /// <summary>GuildGroupData = {i32 GuildGroupId, wchar Name[16], i32 Authority}.</summary>
    public const int GuildGroupDataSize = 0x28;
    /// <summary>Guild::UpdateGuildLogo rejects `8000 &lt; len` before it binds the varbinary.</summary>
    public const int GuildLogoMaxBytes = 8000;
    public const int GuildNameMaxChars = 37;
    public const int GuildAnnounceMaxChars = 201;
    public const int GuildPromotionMaxChars = 201;
    public const int GuildTitleMaxChars = 15;
    public const int GuildGroupNameMaxChars = 16;
    public const int MemberIntroduceMaxChars = 31;
    /// <summary>GuildJoinManager::SendGuildApplyList pages by 13 (`param_4 * 0xd`).</summary>
    public const int ApplyListPageSize = 13;

    // ---- client -> Arbiter (handled inside the Arbiter) ----
    public const ushort C_APPLY_GUILD = 0xA0DF;
    public const ushort C_ACCEPT_GUILD_APPLY = 0xDBE4;
    public const ushort C_GUILD_APPLY_LIST = 0x716B;
    public const ushort C_GUILD_APPLY_LIST_PAGE = 0xDB53;
    public const ushort C_INVITE_USER_TO_GUILD = 0xEF92;
    public const ushort C_REJECT_INVITE_USER_TO_GUILD = 0xDE54;
    public const ushort C_CHANGE_GUILDNAME = 0xFC1C;
    public const ushort C_CHECK_CHANGE_GUILDNAME = 0xB77C;
    public const ushort C_UPDATE_GUILD_LOGO = 0x60C1;
    public const ushort C_GET_USER_GUILD_LOGO = 0x584B;
    public const ushort C_UPDATE_GUILD_TITLE = 0x807B;
    public const ushort C_REQUEST_GUILD_INFO = 0x5B51;
    public const ushort C_REQUEST_GUILD_MEMBER_LIST = 0x6657;
    public const ushort C_GET_GUILD_HISTORY = 0xDC60;
    public const ushort C_SET_GUILD_JOIN_CONDITION = 0xFFDB;
    public const ushort C_REQUEST_GUILD_INFO_BEFORE_APPLY_GUILD = 0xC046;
    public const ushort C_REQUEST_COOLTIME_TO_JOIN_GUILD = 0xC9C4;

    // ---- client -> WorldServer (NO Arbiter handler; World turns these into SA_ frames) ----
    public const ushort C_LEAVE_GUILD = 0x7C83;
    public const ushort C_BANISH_GUILD_MEMBER = 0xE303;
    public const ushort C_DESTROY_GUILD = 0xC4CC;
    public const ushort C_CHANGE_GUILD_CHIEF = 0x8C0F;
    public const ushort C_CREATE_GUILDGROUP = 0xBC8A;
    public const ushort C_CHANGE_GUILDGROUP = 0x8148;
    public const ushort C_REMOVE_GUILDGROUP = 0x73F1;
    public const ushort C_SET_GUILDGROUP_AUTHORITY = 0xE885;
    public const ushort C_CHECK_NEW_GUILDNAME = 0xA6EF;
    public const ushort C_REQUEST_USABLE_GUILD_NAME = 0x99CC;

    // ---- Arbiter -> client ----
    public const ushort S_GUILD_INFO = 0xE8B3;
    public const ushort S_GUILD_MEMBER_LIST = 0xE501;
    public const ushort S_UPDATE_GUILD_MEMBER = 0xED1B;
    public const ushort S_ADD_GUILD_MEMBER = 0xAB18;
    public const ushort S_GUILD_APPLY_LIST = 0x8033;
    public const ushort S_GUILD_APPLY_COUNT = 0xC89A;
    public const ushort S_REQUEST_JOIN_GUILD_NOTICE = 0xF1DF;
    public const ushort S_REQUEST_INVITE_GUILD_TAG = 0x7E14;
    public const ushort S_EMPTY_GUILD_WINDOW = 0x7252;
    public const ushort S_DESTROY_GUILD = 0xFC5C;
    public const ushort S_ADD_GUILD_GROUP = 0xD4D5;
    public const ushort S_UPDATE_GUILD_GROUP = 0x8A39;
    public const ushort S_REMOVE_GUILD_GROUP = 0xF787;
    public const ushort S_GUILD_ANNOUNCE = 0x7A76;
    public const ushort S_UPDATE_GUILD_ANNOUNCE = 0xE9CC;
    public const ushort S_CHANGE_GUILD_CHIEF = 0xAB7E;
    public const ushort S_GET_USER_GUILD_LOGO = 0x7DFA;
    public const ushort S_GUILD_HISTORY = 0x89C9;
    public const ushort S_SET_GUILD_JOIN_CONDITION = 0xAA8B;
    public const ushort S_CHECK_CHANGE_GUILDNAME = 0xDDAD;
    public const ushort S_REQUEST_COOLTIME_TO_JOIN_GUILD = 0xF222;
    public const ushort S_REQUEST_GUILD_INFO_BEFORE_APPLY_GUILD = 0x5A69;

    // ---- WorldServer -> client (the Arbiter never builds these; listed so the tunnel and the
    //      design doc agree on who owns what) ----
    public const ushort S_LEAVE_GUILD = 0xC1D5;
    public const ushort S_BANISH_GUILD_MEMBER = 0x8CF9;
    public const ushort S_REMOVE_GUILD_MEMBER = 0xC24D;
    public const ushort S_CREATE_GUILD_RESULT = 0x8A6E;
    public const ushort S_CHECK_NEW_GUILDNAME = 0x71FD;
    public const ushort S_RESULT_USABLE_GUILD_NAME = 0x6A53;
    public const ushort S_CHANGE_GUILDNAME = 0x85D9;
    public const ushort S_RESULT_RENAME_GUILD = 0x594E;
    public const ushort S_GUILD_EMBLEM = 0xE24D;
    public const ushort S_GUILD_NAME = 0xF741;
    public const ushort S_GUILD_LOG = 0xD2E3;

    // ---- Arbiter -> World ----
    public const ushort AS_LOAD_GUILD = 0x13F9;             // declared, never sent in 100.02
    public const ushort AS_DESTROY_GUILD = 0x13FD;
    public const ushort AS_LEAVE_GUILD = 0x13FF;
    public const ushort AS_BANISH_GUILD_MEMBER = 0x1401;    // declared, never sent in 100.02
    public const ushort AS_CHANGE_GUILD_CHIEF = 0x1403;
    public const ushort AS_SET_GUILDGROUP_AUTHORITY = 0x1405;
    public const ushort AS_CREATE_GUILD_GROUP = 0x1407;
    public const ushort AS_REMOVE_GUILD_GROUP = 0x1409;
    public const ushort AS_ADD_GUILDMEMBER = 0x140A;
    public const ushort AS_CHANGE_GUILDGROUP = 0x140C;
    public const ushort AS_UPDATE_GUILD_MEMBER = 0x1410;
    public const ushort AS_SET_GUILD_RECOMMENDATION_POINT = 0x1411;
    public const ushort AS_UPDATE_GUILD_TITLE = 0x1412;
    public const ushort AS_UPDATE_GUILD_LOGO = 0x1413;
    public const ushort AS_LOAD_GUILD_DATA = 0x144D;

    /// <summary>
    /// AS_UPDATE_GUILD_DATA (0x144E) - the same payload as <see cref="AS_LOAD_GUILD_DATA"/>,
    /// one opcode along: <see cref="BuildAsGuildData"/> builds both. cap_final tap 2501 is
    /// 9134 bytes - <c>[u32 blobOff=14][u32 blobLen=0x23A0][GuildData]</c> - pushed straight
    /// after an incentive grant (2500 -&gt; 2501 -&gt; 2504), and it is what makes World send
    /// the client S_GUILD_MONEY_INFO_CHANGED.
    /// </summary>
    public const ushort AS_UPDATE_GUILD_DATA = 0x144E;
    public const ushort AS_UPDATE_GUILD_QUEST_POINT_INFO = 0x1453;
    public const ushort AS_PUSH_GUILD_BUFF = 0x145D;
    public const ushort AS_LEARN_GUILD_PERK = 0x145E;
    public const ushort AS_RESET_GUILD_PERK = 0x145F;
    public const ushort AS_UPDATE_GUILD_NAME = 0x1490;
    public const ushort AS_UPDATE_GUILD_GENERAL_COIN = 0x15A4;
    public const ushort AS_SET_GUILD_GENERAL_COIN = 0x15A5;
    public const ushort AS_UPDATE_GUILD_EMBLEM = 0x15AF;
    public const ushort AS_GUILD_JOINED = 0x2866;

    // ---- World -> Arbiter ----
    public const ushort SA_LOAD_GUILD = 0x13FB;
    public const ushort SA_DESTROY_GUILD = 0x13FC;
    public const ushort SA_LEAVE_GUILD = 0x13FE;
    public const ushort SA_BANISH_GUILD_MEMBER = 0x1400;
    public const ushort SA_CHANGE_GUILD_CHIEF = 0x1402;
    public const ushort SA_SET_GUILDGROUP_AUTHORITY = 0x1404;
    public const ushort SA_CREATE_GUILD_GROUP = 0x1406;
    public const ushort SA_REMOVE_GUILD_GROUP = 0x1408;
    public const ushort SA_CHANGE_GUILDGROUP = 0x140B;
    public const ushort SA_UPDATE_GUILD_MEMBER = 0x140F;
    public const ushort SA_INC_GUILD_ACCOUNT_LIMIT = 0x1414;
    public const ushort SA_PUSH_GUILD_BUFF = 0x145C;

    // ---- the DB-proxy channel TeraSharp already impersonates (data/dbproxy_opcodes.txt) ----
    public const ushort SDB_INIT_GUILD = 0x27CF;
    public const ushort DBS_INIT_GUILD_GROUP = 0x27D0;
    public const ushort DBS_INIT_GUILD_MEMBER = 0x27D1;
    public const ushort DBS_INIT_GUILD_PERK_LIST = 0x27D2;
    public const ushort DBS_LOAD_GUILD_COMPLETE = 0x27D3;
    public const ushort SDB_CREATE_GUILD2 = 0x27D4;
    public const ushort DBS_CREATE_GUILD2 = 0x27D5;
    public const ushort SDB_ADD_GUILDMEMBER2 = 0x27DB;
    public const ushort DBS_ADD_GUILDMEMBER2 = 0x27DC;
    public const ushort DBS_INIT_GUILD_DATA = 0x27ED;
    public const ushort SDB_CHECK_NEW_GUILD_NAME = 0x2785;
    public const ushort DBS_CHECK_NEW_GUILD_NAME = 0x2786;


    // =====================================================================================
    // T69: SDB_CREATE_GUILD2 (0x27D4) -> DBS_CREATE_GUILD2 (0x27D5). THE GUILD IS BORN HERE.
    //
    // Guild creation has no C_ packet and no SA_ frame: World finishes the founding contract and
    // writes the guild through the DB proxy. Nothing in TeraSharp answered 0x27D4 before T69 -
    // it fell through to the replay table, which has no entry for it because no guild was ever
    // created in the taps the table was built from - so the DlmId went unanswered and the
    // founder's DB queue head-blocked for the rest of the session.
    //
    // Pinned to cap_social2.log seq 1268 -> 1269 -> 1270: character 1003 founds "test" with
    // member 2 ("Test") as the first reply. The two group names come from the request, so the
    // ranks the client shows ("Guild Master" / "Recruit") are World's strings, not ours.
    //
    // SDB_CREATE_GUILD2, dumper Arb_part_017.c:4973, guard 0x29 -> min frame 0x2A:
    //   [06] u32 GuildName off            (wstr, offset only)
    //   [0A] u32 GuildMasterGroupName off
    //   [0E] u32 GuildMemberGroupName off
    //   [12] u32 Member off               [16] u32 Member bytes   (i32 UserDbId each)
    //   [1A] u32 ItemBinary off           [1E] u32 ItemBinary bytes (856-byte atoms: the fee)
    //   [22] i32 ChiefDbId                [26] i32 DlmId
    //
    // DBS_CREATE_GUILD2, dumper Arb_part_015.c:4192, guard 0x3e -> min frame 0x3F:
    //   [06] u32 GuildName off
    //   [0A] u32 GuildGroup off           [0E] u32 GuildGroup bytes   (GuildGroupData, 0x28 each)
    //   [12] u32 Member off               [16] u32 Member bytes       (GuildMemberData, 0xF0 each)
    //   [1A] u32 ItemBinary off           [1E] u32 ItemBinary bytes   (the fee atoms, echoed)
    //   [22] u32 FirstReplyName off
    //   [26] u8  Success   [27] i32 GuildDbId   [2B] i32 ChiefDbId
    //   [2F] i64 CreateTime  [37] i32 DlmId  [3B] i32 ErrorNo
    // then the blocks in slot order: name, groups, members, itemBinary, firstReplyName.
    // seq 1270 checks out exactly: 63 / (73,80) / (153,480) / (633,856) / 1489, payload 1493.
    // =====================================================================================

    /// <summary>Payload bytes before the first block in DBS_CREATE_GUILD2 (frame 0x3F).</summary>
    public const int CreateGuild2ReplyHeader = 57;
    /// <summary>Payload bytes before the first block in SDB_CREATE_GUILD2 (frame 0x2A).</summary>
    public const int CreateGuild2RequestHeader = 36;

    public readonly record struct SdbCreateGuild2(
        string GuildName, string MasterGroupName, string MemberGroupName,
        IReadOnlyList<int> MemberDbIds, byte[] ItemBinary, int ChiefDbId, uint DlmId);

    /// <summary>Reads an SDB_CREATE_GUILD2 payload. Null when it is shorter than the guard.</summary>
    public static SdbCreateGuild2? ParseSdbCreateGuild2(byte[] p)
    {
        if (p == null || p.Length < CreateGuild2RequestHeader) return null;

        var members = new List<int>();
        int mOff = (int)BitConverter.ToUInt32(p, 12) - FrameHeader;
        int mLen = (int)BitConverter.ToUInt32(p, 16);
        if (mOff >= CreateGuild2RequestHeader && mLen > 0 && mLen <= p.Length - mOff)
            for (int at = 0; at + 4 <= mLen; at += 4) members.Add(BitConverter.ToInt32(p, mOff + at));

        var fee = Array.Empty<byte>();
        int iOff = (int)BitConverter.ToUInt32(p, 20) - FrameHeader;
        int iLen = (int)BitConverter.ToUInt32(p, 24);
        if (iOff >= CreateGuild2RequestHeader && iLen > 0 && iLen <= p.Length - iOff)
        {
            fee = new byte[iLen];
            Array.Copy(p, iOff, fee, 0, iLen);
        }

        return new SdbCreateGuild2(
            ReadGuildWString(p, BitConverter.ToUInt32(p, 0)),
            ReadGuildWString(p, BitConverter.ToUInt32(p, 4)),
            ReadGuildWString(p, BitConverter.ToUInt32(p, 8)),
            members, fee,
            BitConverter.ToInt32(p, 28), BitConverter.ToUInt32(p, 32));
    }

    /// <summary>
    /// DBS_CREATE_GUILD2 (0x27D5). <paramref name="groups"/> are 0x28-byte GuildGroupData blobs
    /// and <paramref name="members"/> 0xF0-byte GuildMemberData blobs, in the order World is to
    /// see them; <paramref name="itemBinary"/> is the request's fee atom list, echoed.
    /// </summary>
    public static byte[] BuildDbsCreateGuild2(
        string guildName, IReadOnlyList<byte[]>? groups, IReadOnlyList<byte[]>? members,
        byte[]? itemBinary, string firstReplyName, bool success, int guildDbId, int chiefDbId,
        long createTime, uint dlmId, int errorNo)
    {
        groups ??= Array.Empty<byte[]>();
        members ??= Array.Empty<byte[]>();
        itemBinary ??= Array.Empty<byte>();
        guildName ??= string.Empty;
        firstReplyName ??= string.Empty;

        int nameBytes = (guildName.Length + 1) * 2;
        int groupBytes = 0; foreach (var g in groups) groupBytes += g.Length;
        int memberBytes = 0; foreach (var m in members) memberBytes += m.Length;
        int replyNameBytes = (firstReplyName.Length + 1) * 2;

        var p = new byte[CreateGuild2ReplyHeader + nameBytes + groupBytes + memberBytes
                         + itemBinary.Length + replyNameBytes];

        int at = CreateGuild2ReplyHeader;
        uint nameOff = (uint)(FrameHeader + at);
        WriteGuildWString(p, at, guildName); at += nameBytes;
        uint groupOff = (uint)(FrameHeader + at);
        foreach (var g in groups) { g.CopyTo(p, at); at += g.Length; }
        uint memberOff = (uint)(FrameHeader + at);
        foreach (var m in members) { m.CopyTo(p, at); at += m.Length; }
        uint itemOff = (uint)(FrameHeader + at);
        itemBinary.CopyTo(p, at); at += itemBinary.Length;
        uint replyNameOff = (uint)(FrameHeader + at);
        WriteGuildWString(p, at, firstReplyName);

        BitConverter.GetBytes(nameOff).CopyTo(p, 0);
        BitConverter.GetBytes(groupOff).CopyTo(p, 4);
        BitConverter.GetBytes((uint)groupBytes).CopyTo(p, 8);
        BitConverter.GetBytes(memberOff).CopyTo(p, 12);
        BitConverter.GetBytes((uint)memberBytes).CopyTo(p, 16);
        BitConverter.GetBytes(itemOff).CopyTo(p, 20);
        BitConverter.GetBytes((uint)itemBinary.Length).CopyTo(p, 24);
        BitConverter.GetBytes(replyNameOff).CopyTo(p, 28);
        p[32] = (byte)(success ? 1 : 0);
        BitConverter.GetBytes(guildDbId).CopyTo(p, 33);
        BitConverter.GetBytes(chiefDbId).CopyTo(p, 37);
        BitConverter.GetBytes(createTime).CopyTo(p, 41);
        BitConverter.GetBytes(dlmId).CopyTo(p, 49);
        BitConverter.GetBytes(errorNo).CopyTo(p, 53);
        return p;
    }

    /// <summary>The refusal form: no blocks, Success 0 and an ErrorNo, DlmId echoed so the
    /// founder's DB queue drains either way.</summary>
    public static byte[] BuildDbsCreateGuild2Failure(uint dlmId, int chiefDbId, int errorNo)
        => BuildDbsCreateGuild2(string.Empty, null, null, null, string.Empty,
                                success: false, guildDbId: 0, chiefDbId, createTime: 0, dlmId, errorNo);

    /// <summary>A NUL-terminated UTF-16LE string at a FRAME offset, read out of a payload. The
    /// offset is read UNSIGNED so a garbage value can never index backwards.</summary>
    private static string ReadGuildWString(byte[] payload, uint frameOffset)
    {
        if (frameOffset < FrameHeader) return string.Empty;
        long at = (long)frameOffset - FrameHeader;
        if (at >= payload.Length) return string.Empty;
        var sb = new System.Text.StringBuilder();
        for (long i = at; i + 1 < payload.Length; i += 2)
        {
            char ch = (char)(payload[i] | (payload[i + 1] << 8));
            if (ch == '\0') break;
            sb.Append(ch);
        }
        return sb.ToString();
    }

    private static void WriteGuildWString(byte[] p, int at, string value)
    {
        foreach (char ch in value) { p[at++] = (byte)ch; p[at++] = (byte)(ch >> 8); }
        p[at] = 0; p[at + 1] = 0;
    }

    /// <summary>
    /// Minimum FRAME length each SA_ guild handler demands. Like the party frames, a short one
    /// is not a dropped packet on the real Arbiter - the handler logs
    /// "Arbiter &lt;-&gt; World PDL Version Mismatch! Bye :(" and kills the link - so these have
    /// to be exact. Our parsers return null instead, which is the safe analogue.
    /// </summary>
    public static int MinFrameLength(ushort op) => op switch
    {
        SA_LOAD_GUILD => 0x12,
        SA_DESTROY_GUILD => 0x12,
        SA_LEAVE_GUILD => 0x1A,
        SA_BANISH_GUILD_MEMBER => 0x1A,
        SA_CHANGE_GUILD_CHIEF => 0x16,
        SA_SET_GUILDGROUP_AUTHORITY => 0x1E,
        SA_CREATE_GUILD_GROUP => 0x16,
        SA_REMOVE_GUILD_GROUP => 0x16,
        SA_CHANGE_GUILDGROUP => 0x1A,
        SA_UPDATE_GUILD_MEMBER => 0x0E,
        _ => 0,
    };

    /// <summary>
    /// Minimum TOTAL client packet length (including the 4-byte [u16 len][u16 opcode] header)
    /// the real Arbiter's handler enforces before it touches the body. Returns 0 for the guild
    /// packets the Arbiter does not handle at all - those belong to WorldServer and must be
    /// tunnelled, not answered.
    /// </summary>
    public static int MinClientLength(ushort op) => op switch
    {
        C_APPLY_GUILD => 0x08,
        C_ACCEPT_GUILD_APPLY => 0x09,
        C_GUILD_APPLY_LIST => 0x04,
        C_GUILD_APPLY_LIST_PAGE => 0x08,
        C_INVITE_USER_TO_GUILD => 0x0B,
        C_REJECT_INVITE_USER_TO_GUILD => 0x08,
        C_CHANGE_GUILDNAME => 0x06,
        C_CHECK_CHANGE_GUILDNAME => 0x06,
        C_UPDATE_GUILD_LOGO => 0x08,
        C_GET_USER_GUILD_LOGO => 0x0C,
        C_UPDATE_GUILD_TITLE => 0x06,
        C_REQUEST_GUILD_INFO => 0x0C,
        C_REQUEST_GUILD_MEMBER_LIST => 0x04,
        C_GET_GUILD_HISTORY => 0x08,
        C_SET_GUILD_JOIN_CONDITION => 0x16,
        C_REQUEST_GUILD_INFO_BEFORE_APPLY_GUILD => 0x0A,
        C_REQUEST_COOLTIME_TO_JOIN_GUILD => 0x04,
        _ => 0,
    };

    /// <summary>True if the Arbiter - not WorldServer - owns this guild C_ packet.</summary>
    public static bool ArbiterHandlesClientPacket(ushort op) => MinClientLength(op) != 0;

    // =======================================================================================
    // GuildData - the 0x23A0-byte blob. Offsets are blob-relative.
    // Names and offsets come from the spLoadAllGuild column binds and the named Guild::
    // accessors; the five marked (capture) are additionally confirmed by the empty
    // DBS_INIT_GUILD_DATA in data/cap_guild.bin, whose only non-zero bytes land exactly here.
    // =======================================================================================
    public const int GdGuildDbId = 0x0000;                      // i32
    public const int GdGuildName = 0x0004;                      // wchar[37]
    public const int GdChiefDbId = 0x0050;                      // i32
    public const int GdGuildCreateDate = 0x0054;                // tagTIMESTAMP_STRUCT
    public const int GdGuildLevel = 0x0064;                     // i32   (capture: 1)
    public const int GdGuildExp = 0x0068;                       // i64
    public const int GdGuildPoint = 0x0070;                     // i64
    public const int GdGuildMoney = 0x0078;                     // i64
    public const int GdLastIncentiveTime = 0x0080;              // TIMESTAMP (capture: 1970-01-01)
    public const int GdGuildAnnounce = 0x0090;                  // wchar[201]
    public const int GdPadAfterAnnounce = 0x0222;               // 2 B alignment hole - see below
    public const int GdRecommendationPoint = 0x0224;            // i32
    public const int GdUnknown0228 = 0x0228;                    // i32, loaded from col 11, never read
    public const int GdGuildTitle = 0x022C;                     // wchar[15]
    public const int GdPadAfterTitle = 0x024A;                  // 2 B alignment hole - see below
    public const int GdGuildLogoLength = 0x024C;                // i32
    public const int GdGuildLogoId = 0x0250;                    // i32
    public const int GdGuildLogo = 0x0254;                      // byte[8000]
    public const int GdGuildPromotion = 0x2194;                 // wchar[201]
    public const int GdNeedChangeGuildName = 0x2326;            // u8
    public const int GdIsGuildWarAcceptable = 0x2327;           // u8
    public const int GdGuildWarAcceptableToggleTime = 0x2328;   // TIMESTAMP (capture: 1970-01-01)
    public const int GdGuildGeneralCoin = 0x2338;               // i32
    public const int GdForeverEmblemId = 0x2340;                // i32
    public const int GdEmblemId = 0x2344;                       // i32
    public const int GdUnknownTime2348 = 0x2348;                // TIMESTAMP (capture: 1970-01-01), unnamed
    public const int GdGuildPreference = 0x2358;                // i32
    public const int GdJoinMinLevel = 0x235C;                   // i32   (capture: 1)
    public const int GdJoinMaxLevel = 0x2360;                   // i32   (capture: 70)
    public const int GdGuildJoinType = 0x2364;                  // i32   (capture: 1)
    public const int GdLastWeekPlayTime = 0x2368;               // i64
    public const int GdThisWeekPlayTime = 0x2370;               // i64
    public const int GdLordCandidacyParcelGeneration = 0x2388;  // i32
    public const int GdAddAccountLimitValue = 0x238C;           // i32

    /// <summary>
    /// The two bytes of alignment padding GuildData carries after GuildAnnounce and after
    /// GuildTitle. They are the ONLY bytes that differ between the four captures of the empty
    /// DBS_INIT_GUILD_DATA, because the real Arbiter ships a default-constructed record without
    /// clearing them: arb_world.log leaks 0xB379 at 0x024A, lobby_tap.log leaks 0xB7FA / 0xC9E9.
    /// World never reads them. Our builder writes zeros.
    /// </summary>
    public static readonly int[] GuildDataPaddingHoles = { GdPadAfterAnnounce, GdPadAfterTitle };

    /// <summary>tagTIMESTAMP_STRUCT: `i16 year; u16 month, day, hour, minute, second; u32 fraction`.
    /// The default-constructed value is 1970-01-01, which on the wire is 00 00 -> B2 07 01 00 01 00.</summary>
    public static byte[] BuildTimestamp(int year, int month, int day, int hour, int minute, int second, uint fraction = 0)
    {
        var t = new byte[16];
        BitConverter.GetBytes((short)year).CopyTo(t, 0);
        BitConverter.GetBytes((ushort)month).CopyTo(t, 2);
        BitConverter.GetBytes((ushort)day).CopyTo(t, 4);
        BitConverter.GetBytes((ushort)hour).CopyTo(t, 6);
        BitConverter.GetBytes((ushort)minute).CopyTo(t, 8);
        BitConverter.GetBytes((ushort)second).CopyTo(t, 10);
        BitConverter.GetBytes(fraction).CopyTo(t, 12);
        return t;
    }

    /// <summary>The epoch default the real Arbiter ships in every unset TIMESTAMP: 1970-01-01.</summary>
    public static byte[] BuildEpochTimestamp() => BuildTimestamp(1970, 1, 1, 0, 0, 0);

    /// <summary>
    /// The default-constructed GuildData the real Arbiter sends when there is no guild:
    /// GuildLevel 1, three epoch timestamps, JoinMinLevel 1, JoinMaxLevel 70, JoinType 1, and
    /// zero everywhere else - including the two padding holes the real one leaks.
    /// Byte-identical to data/cap_guild.bin's DBS_INIT_GUILD_DATA blob apart from those holes.
    /// </summary>
    public static byte[] BuildEmptyGuildDataBlob()
    {
        var b = new byte[GuildDataSize];
        BitConverter.GetBytes(1).CopyTo(b, GdGuildLevel);
        BuildEpochTimestamp().CopyTo(b, GdLastIncentiveTime);
        BuildEpochTimestamp().CopyTo(b, GdGuildWarAcceptableToggleTime);
        BuildEpochTimestamp().CopyTo(b, GdUnknownTime2348);
        BitConverter.GetBytes(1).CopyTo(b, GdJoinMinLevel);
        BitConverter.GetBytes(70).CopyTo(b, GdJoinMaxLevel);
        BitConverter.GetBytes(1).CopyTo(b, GdGuildJoinType);
        return b;
    }

    /// <summary>
    /// DBS_INIT_GUILD_DATA (0x27ED) payload - the answer to World's SDB_INIT_GUILD (0x27CF).
    /// Frame-relative: `u32 guildDataOff@06, u32 guildDataLen@0A, u32 guildLogoIdOff@0E,
    /// u8 success@12`, then the blob, then the logo-id wstring. Payload-relative that is
    /// 0/4/8/12 and data at 13. `success = false` is the terminator World waits for.
    /// </summary>
    public static byte[] BuildDbsInitGuildData(byte[] guildData, string guildLogoId, bool success)
    {
        var logo = guildLogoId ?? string.Empty;
        var p = new byte[13 + guildData.Length + (logo.Length + 1) * 2];
        BitConverter.GetBytes(6 + 13).CopyTo(p, 0);                          // frame-relative offset
        BitConverter.GetBytes(guildData.Length).CopyTo(p, 4);
        BitConverter.GetBytes(6 + 13 + guildData.Length).CopyTo(p, 8);
        p[12] = (byte)(success ? 1 : 0);
        guildData.CopyTo(p, 13);
        int at = 13 + guildData.Length;
        foreach (char ch in logo) { p[at++] = (byte)ch; p[at++] = (byte)(ch >> 8); }
        return p;
    }

    /// <summary>The empty answer: one default-constructed GuildData, empty logo id, success 0.</summary>
    public static byte[] BuildEmptyDbsInitGuildData()
        => BuildDbsInitGuildData(BuildEmptyGuildDataBlob(), string.Empty, false);

    public readonly record struct InitGuildData(int GuildDataOffset, int GuildDataLength, int GuildLogoIdOffset,
        bool Success, byte[] GuildData, string GuildLogoId);

    /// <summary>Reads a DBS_INIT_GUILD_DATA payload back. Null if it is short or self-inconsistent.</summary>
    public static InitGuildData? ParseDbsInitGuildData(byte[] payload)
    {
        if (payload == null || payload.Length < 13) return null;
        int off = BitConverter.ToInt32(payload, 0), len = BitConverter.ToInt32(payload, 4);
        int logoOff = BitConverter.ToInt32(payload, 8);
        bool success = payload[12] != 0;
        if (len < 0 || off < 6 || off - 6 + len > payload.Length) return null;
        var blob = new byte[len];
        Array.Copy(payload, off - 6, blob, 0, len);
        return new InitGuildData(off, len, logoOff, success, blob, ReadWStringAtPayload(payload, logoOff - 6));
    }

    // =======================================================================================
    // GuildMemberData (0xF0) and GuildGroupData (0x28) - blob-relative offsets, from the
    // spLoadAllGuildMemberData / spLoadGuildGroup column binds.
    // =======================================================================================
    public const int GmUserDbId = 0x00;                  // i32
    public const int GmName = 0x04;                      // wchar[37]
    public const int GmWorldId = 0x50;                   // i32
    public const int GmGuardId = 0x54;                   // i32
    public const int GmSectionId = 0x58;                 // i32
    public const int GmGuildGroupId = 0x5C;              // i32
    public const int GmUserLevel = 0x60;                 // i32
    public const int GmRace = 0x64;                      // i32
    public const int GmUserClass = 0x68;                 // i32
    public const int GmGender = 0x6C;                    // i32
    public const int GmState = 0x70;                     // i32, 0 online / 2 offline, not persisted
    public const int GmWeeklyContributionPoint = 0x74;   // i32
    public const int GmTotalContributionPoint = 0x78;    // i64
    public const int GmUnknown0080 = 0x80;               // i32, col 14, -1 on load
    public const int GmIntroduce = 0x90;                 // wchar[31]
    public const int GmLastLogoutTime = 0xD0;            // i64 DateTime
    public const int GmCanGuildWar = 0xD8;               // u8, not persisted
    public const int GmAccountId = 0xE0;                 // i64
    public const int GmGuildJoinDate = 0xE8;             // i64 DateTime

    public const int GgGuildGroupId = 0x00;              // i32
    public const int GgName = 0x04;                      // wchar[16]
    public const int GgAuthority = 0x24;                 // i32 bitmask

    // =======================================================================================
    // client -> Arbiter parsers. Each takes the packet BODY (frame minus the 4-byte header),
    // which is what PacketDispatcher hands a handler; a u16 field offset inside the packet is
    // therefore `body[i] - 4`. Every one returns null rather than throwing on a short body -
    // the real Arbiter answers a short body with GET_CLIENT_BUFFER_BUFSIZE_MISMATCH and drops it.
    // =======================================================================================

    /// <summary>C_APPLY_GUILD (0xA0DF): `[u16 guildNameOff][u16 joinMsgOff]` + strings. Handler
    /// FUN_1404db730 needs total len &gt;= 8.</summary>
    public static (string guildName, string joinMsg)? ParseCApplyGuild(byte[] body)
        => body.Length < 4 ? null : (ReadWString(body, 0), ReadWString(body, 2));

    /// <summary>C_ACCEPT_GUILD_APPLY (0xDBE4): `[u8 accept][u32 userDbId]` - unaligned, no
    /// padding after the bool. Handler FUN_1404d8590 needs total len &gt;= 9.</summary>
    public static (bool accept, int userDbId)? ParseCAcceptGuildApply(byte[] body)
        => body.Length < 5 ? null : (body[0] != 0, BitConverter.ToInt32(body, 1));

    /// <summary>C_GUILD_APPLY_LIST_PAGE (0xDB53): `[i32 pageNumber]`.</summary>
    public static int? ParseCGuildApplyListPage(byte[] body)
        => body.Length < 4 ? null : BitConverter.ToInt32(body, 0);

    /// <summary>C_INVITE_USER_TO_GUILD (0xEF92): `[u16 nameOff][u32 userDbId][u8 fromWantedList]`.
    /// The name is only consulted when userDbId is 0. Handler FUN_1404e1890 needs len &gt;= 0x0B.</summary>
    public static (string name, int userDbId, bool fromWantedList)? ParseCInviteUserToGuild(byte[] body)
        => body.Length < 7 ? null : (ReadWString(body, 0), BitConverter.ToInt32(body, 2), body[6] != 0);

    /// <summary>C_REJECT_INVITE_USER_TO_GUILD (0xDE54): `[i32 guildDbId]`.</summary>
    public static int? ParseCRejectInviteUserToGuild(byte[] body)
        => body.Length < 4 ? null : BitConverter.ToInt32(body, 0);

    /// <summary>C_CHANGE_GUILDNAME / C_CHECK_CHANGE_GUILDNAME / C_UPDATE_GUILD_TITLE: one
    /// `[u16 off]` + wstring. Handlers need total len &gt;= 6.</summary>
    public static string? ParseCSingleString(byte[] body)
        => body.Length < 2 ? null : ReadWString(body, 0);

    /// <summary>C_UPDATE_GUILD_LOGO (0x60C1): `[u16 logoOff][u16 logoLen]` + raw bytes.
    /// The shipped .def calls this a `string`; the handler passes the pointer and the u16 at
    /// body+2 straight to `Guild::UpdateGuildLogo(User*, const unsigned char*, int)` and rejects
    /// `8000 &lt; len`, so it is a BYTES field with the (offset, count) slot order.</summary>
    public static byte[]? ParseCUpdateGuildLogo(byte[] body)
    {
        if (body.Length < 4) return null;
        int off = BitConverter.ToUInt16(body, 0) - 4, len = BitConverter.ToUInt16(body, 2);
        if (off < 0 || len < 0 || len > GuildLogoMaxBytes || off + len > body.Length) return null;
        var d = new byte[len];
        Array.Copy(body, off, d, 0, len);
        return d;
    }

    /// <summary>C_GET_USER_GUILD_LOGO (0x584B): `[i32 userDbId][i32 guildDbId]`. The handler
    /// silently drops the request when guildDbId is 0.</summary>
    public static (int userDbId, int guildDbId)? ParseCGetUserGuildLogo(byte[] body)
        => body.Length < 8 ? null : (BitConverter.ToInt32(body, 0), BitConverter.ToInt32(body, 4));

    /// <summary>C_REQUEST_GUILD_INFO (0x5B51): `[i32 guildDbId][i32 guildWindowType]`.
    /// WindowType 1 = probe, 2 = info + history page 1 (members), 3 = info (non-members),
    /// 5 = member list, 6 = guild quests, 0x0B = apply list page 1.</summary>
    public static (int guildDbId, int windowType)? ParseCRequestGuildInfo(byte[] body)
        => body.Length < 8 ? null : (BitConverter.ToInt32(body, 0), BitConverter.ToInt32(body, 4));

    /// <summary>C_GET_GUILD_HISTORY (0xDC60): `[i32 viewPage]`.</summary>
    public static int? ParseCGetGuildHistory(byte[] body)
        => body.Length < 4 ? null : BitConverter.ToInt32(body, 0);

    /// <summary>C_SET_GUILD_JOIN_CONDITION (0xFFDB): `[u16 introOff][i32 minLevel][i32 maxLevel]
    /// [i32 joinType][i32 preference]` + wstring. Handler FUN_1404ed2d0 needs len &gt;= 0x16 and
    /// rejects anyone who is not the guild chief.</summary>
    public static (string introduction, int minLevel, int maxLevel, int joinType, int preference)?
        ParseCSetGuildJoinCondition(byte[] body)
        => body.Length < 0x12 ? null
            : (ReadWString(body, 0), BitConverter.ToInt32(body, 2), BitConverter.ToInt32(body, 6),
               BitConverter.ToInt32(body, 0x0A), BitConverter.ToInt32(body, 0x0E));

    /// <summary>C_REQUEST_GUILD_INFO_BEFORE_APPLY_GUILD (0xC046): `[u16 nameOff][i32 guildDbId]`.
    /// The name is only consulted when guildDbId is 0.</summary>
    public static (string guildName, int guildDbId)? ParseCRequestGuildInfoBeforeApply(byte[] body)
        => body.Length < 6 ? null : (ReadWString(body, 0), BitConverter.ToInt32(body, 2));

    // =======================================================================================
    // Arbiter -> client builders. Each returns the BODY; the transport prepends
    // [u16 totalLen][u16 opcode]. Offsets in the doc comments are packet-relative, so a body
    // index i is packet offset i + 4.
    // =======================================================================================

    /// <summary>S_GUILD_APPLY_COUNT (0xC89A) and S_REQUEST_INVITE_GUILD_TAG (0x7E14):
    /// a single `i32 Count`. Both writers are one FUN_1400554e0 after the opcode.</summary>
    public static byte[] BuildCountBody(int count) => BitConverter.GetBytes(count);

    /// <summary>S_CHANGE_GUILD_CHIEF (0xAB7E): `i32 NewChiefDbId`.</summary>
    public static byte[] BuildSChangeGuildChiefBody(int newChiefDbId) => BitConverter.GetBytes(newChiefDbId);

    /// <summary>S_REMOVE_GUILD_GROUP (0xF787): `i32 GroupId`.</summary>
    public static byte[] BuildSRemoveGuildGroupBody(int groupId) => BitConverter.GetBytes(groupId);

    /// <summary>S_SET_GUILD_JOIN_CONDITION (0xAA8B): a single `bool Success`. The values
    /// themselves come back in S_GUILD_INFO.</summary>
    public static byte[] BuildSSetGuildJoinConditionBody(bool success) => new[] { (byte)(success ? 1 : 0) };

    /// <summary>S_REQUEST_JOIN_GUILD_NOTICE (0xF1DF), S_DESTROY_GUILD (0xFC5C) and
    /// S_EMPTY_GUILD_WINDOW (0x7252) carry nothing at all - the writers send straight after
    /// the opcode. The client reacts to the arrival, not to a field.</summary>
    public static byte[] BuildEmptyBody() => Array.Empty<byte>();

    /// <summary>S_REQUEST_COOLTIME_TO_JOIN_GUILD (0xF222): `[u8 onCooltime][i64 endTimestamp]`.</summary>
    public static byte[] BuildSRequestCooltimeToJoinGuildBody(bool onCooltime, long endTimestamp)
    {
        var b = new byte[9];
        b[0] = (byte)(onCooltime ? 1 : 0);
        BitConverter.GetBytes(endTimestamp).CopyTo(b, 1);
        return b;
    }

    /// <summary>S_ADD_GUILD_GROUP (0xD4D5) and S_UPDATE_GUILD_GROUP (0x8A39) are byte-identical:
    /// `[u16 nameOff][i32 GroupId][i32 Authority]` + wstring, fixed part 0x0E.</summary>
    public static byte[] BuildSGuildGroupBody(int groupId, int authority, string name)
    {
        var w = new Body();
        int slot = w.Reserve();
        w.I32(groupId);
        w.I32(authority);
        w.Str(slot, name);
        return w.ToArray();
    }

    /// <summary>S_GUILD_ANNOUNCE (0x7A76, field GuildNotice) and S_UPDATE_GUILD_ANNOUNCE
    /// (0xE9CC, field GuildAnnounce): `[u16 off]` + wstring, fixed part 0x06.</summary>
    public static byte[] BuildSingleStringBody(string text)
    {
        var w = new Body();
        int slot = w.Reserve();
        w.Str(slot, text);
        return w.ToArray();
    }

    /// <summary>
    /// S_GET_USER_GUILD_LOGO (0x7DFA): `[u16 logoOff][u16 logoLen][i32 UserDbId][i32 GuildDbId]`
    /// + raw bytes, fixed part 0x10. Note the bytes header is (offset, count) - the reverse of
    /// an array's (count, offset).
    /// </summary>
    public static byte[] BuildSGetUserGuildLogoBody(int userDbId, int guildDbId, byte[] logo)
    {
        var w = new Body();
        int slot = w.Reserve(); w.Reserve();
        w.I32(userDbId);
        w.I32(guildDbId);
        w.Bytes(slot, logo ?? Array.Empty<byte>());
        return w.ToArray();
    }

    /// <summary>A guild member as S_ADD_GUILD_MEMBER / S_UPDATE_GUILD_MEMBER carry one.</summary>
    public readonly record struct GuildMemberWire(
        int MemberDbId, string Name, int WorldId, int GuardId, int SectionId, int GroupId,
        int UserLevel, int Race, int UserClass, int Gender, int State,
        long LastLogoutTime, bool IsWorldEventTarget, bool CityWarCompensationStatus,
        bool IsInGuildWarCombatState);

    /// <summary>
    /// S_ADD_GUILD_MEMBER (0xAB18): `[u16 nameOff]` then ten i32, an i64 and two bools;
    /// fixed part 0x38. The shipped .def is missing `int32 gender` (at 0x2A) and the trailing
    /// `byte cityWarCompensationStatus`, which is why it computes 0x33.
    /// </summary>
    public static byte[] BuildSAddGuildMemberBody(in GuildMemberWire m)
    {
        var w = new Body();
        int slot = w.Reserve();
        w.I32(m.MemberDbId); w.I32(m.WorldId); w.I32(m.GuardId); w.I32(m.SectionId); w.I32(m.GroupId);
        w.I32(m.UserLevel); w.I32(m.Race); w.I32(m.UserClass); w.I32(m.State); w.I32(m.Gender);
        w.I64(m.LastLogoutTime);
        w.Bool(m.IsWorldEventTarget);
        w.Bool(m.CityWarCompensationStatus);
        w.Str(slot, m.Name);
        return w.ToArray();
    }

    /// <summary>
    /// S_UPDATE_GUILD_MEMBER (0xED1B): the same shape plus `bool IsInGuildWarCombatState`
    /// between IsWorldEventTarget and CityWarCompensationStatus; fixed part 0x39. The shipped
    /// .def has only two trailing bytes and computes 0x38.
    /// </summary>
    public static byte[] BuildSUpdateGuildMemberBody(in GuildMemberWire m)
    {
        var w = new Body();
        int slot = w.Reserve();
        w.I32(m.MemberDbId); w.I32(m.WorldId); w.I32(m.GuardId); w.I32(m.SectionId); w.I32(m.GroupId);
        w.I32(m.UserLevel); w.I32(m.Race); w.I32(m.UserClass); w.I32(m.State); w.I32(m.Gender);
        w.I64(m.LastLogoutTime);
        w.Bool(m.IsWorldEventTarget);
        w.Bool(m.IsInGuildWarCombatState);
        w.Bool(m.CityWarCompensationStatus);
        return w.ToArray();
    }

    /// <summary>One row of S_GUILD_APPLY_LIST's array.</summary>
    public readonly record struct GuildApplyRow(int UserDbId, int ClassType, int UserLevel, long DateTime,
        string UserName, string JoinMsg);

    /// <summary>
    /// S_GUILD_APPLY_LIST (0x8033): `[u16 count][u16 offset][bool InviteAuthority]
    /// [i32 CurPageNum][i32 TotalPageCount]`, fixed part 0x11, element stride 0x1C.
    /// </summary>
    public static byte[] BuildSGuildApplyListBody(bool inviteAuthority, int curPage, int totalPages,
        IReadOnlyList<GuildApplyRow> rows)
    {
        var w = new Body();
        int cnt = w.Reserve(), off = w.Reserve();
        w.Bool(inviteAuthority);
        w.I32(curPage);
        w.I32(totalPages);
        w.BeginArray(cnt, off, rows.Count);
        foreach (var r in rows)
        {
            w.BeginElement();
            int nameSlot = w.Reserve(), msgSlot = w.Reserve();
            w.I32(r.UserDbId); w.I32(r.ClassType); w.I32(r.UserLevel); w.I64(r.DateTime);
            w.Str(nameSlot, r.UserName);
            w.Str(msgSlot, r.JoinMsg);
        }
        return w.ToArray();
    }

    /// <summary>One row of S_GUILD_HISTORY's array.</summary>
    public readonly record struct GuildHistoryRow(long LogTime, int ActionType, string ActorName, string LogString);

    /// <summary>
    /// S_GUILD_HISTORY (0x89C9): `[u16 count][u16 offset][i32 ViewPage][i32 LastPage]`,
    /// fixed part 0x10, element stride 0x14, 0x14 rows per page.
    /// </summary>
    public static byte[] BuildSGuildHistoryBody(int viewPage, int lastPage, IReadOnlyList<GuildHistoryRow> rows)
    {
        var w = new Body();
        int cnt = w.Reserve(), off = w.Reserve();
        w.I32(viewPage);
        w.I32(lastPage);
        w.BeginArray(cnt, off, rows.Count);
        foreach (var r in rows)
        {
            w.BeginElement();
            int actorSlot = w.Reserve(), logSlot = w.Reserve();
            w.I64(r.LogTime); w.I32(r.ActionType);
            w.Str(actorSlot, r.ActorName);
            w.Str(logSlot, r.LogString);
        }
        return w.ToArray();
    }

    // =======================================================================================
    // Arbiter -> World frame builders. These return the PAYLOAD (frame minus [u32 len][u16 op]),
    // so a field the decompile quotes at frame+0x06 is payload index 0. Strings on this link
    // are a u32 offset written BEFORE the fixed fields, and the offset is frame-relative.
    // =======================================================================================
    private const int FrameHeader = 6;

    /// <summary>AS_GUILD_JOINED (0x2866): `i32 UserDbId`. Sent only to the joining user's own
    /// world session, not broadcast.</summary>
    public static byte[] BuildAsGuildJoined(int userDbId) => BitConverter.GetBytes(userDbId);

    /// <summary>AS_DESTROY_GUILD (0x13FD): `i32 GuildDbId`.</summary>
    public static byte[] BuildAsDestroyGuild(int guildDbId) => BitConverter.GetBytes(guildDbId);

    /// <summary>AS_LEAVE_GUILD (0x13FF): `i32 GuildDbId, i32 MemberDbId`.</summary>
    public static byte[] BuildAsLeaveGuild(int guildDbId, int memberDbId) => Pair(guildDbId, memberDbId);

    /// <summary>AS_CHANGE_GUILD_CHIEF (0x1403): `i32 GuildDbId, i32 NewChiefDbId`.</summary>
    public static byte[] BuildAsChangeGuildChief(int guildDbId, int newChiefDbId) => Pair(guildDbId, newChiefDbId);

    /// <summary>AS_REMOVE_GUILD_GROUP (0x1409): `i32 GuildDbId, i32 GroupId`.</summary>
    public static byte[] BuildAsRemoveGuildGroup(int guildDbId, int groupId) => Pair(guildDbId, groupId);

    /// <summary>AS_BANISH_GUILD_MEMBER (0x1401): `i32 GuildDbId, i32 BanisherDbId, i32 BanisheeDbId`.
    /// Declared but never sent in 100.02 - the kick travels as AS_LEAVE_GUILD.</summary>
    public static byte[] BuildAsBanishGuildMember(int guildDbId, int banisherDbId, int banisheeDbId)
    {
        var p = new byte[12];
        BitConverter.GetBytes(guildDbId).CopyTo(p, 0);
        BitConverter.GetBytes(banisherDbId).CopyTo(p, 4);
        BitConverter.GetBytes(banisheeDbId).CopyTo(p, 8);
        return p;
    }

    /// <summary>AS_CHANGE_GUILDGROUP (0x140C): `i32 GuildDbId, i32 MemberDbId, i32 GuildGroupId`.</summary>
    public static byte[] BuildAsChangeGuildGroup(int guildDbId, int memberDbId, int guildGroupId)
    {
        var p = new byte[12];
        BitConverter.GetBytes(guildDbId).CopyTo(p, 0);
        BitConverter.GetBytes(memberDbId).CopyTo(p, 4);
        BitConverter.GetBytes(guildGroupId).CopyTo(p, 8);
        return p;
    }

    /// <summary>AS_UPDATE_GUILD_MEMBER (0x1410): `i32 GuildDbId, MemberDbId, WorldId, GuardId,
    /// SectionId, Level, State` - seven i32, no strings, fixed part 0x22.</summary>
    public static byte[] BuildAsUpdateGuildMember(int guildDbId, int memberDbId, int worldId, int guardId,
        int sectionId, int level, int state)
    {
        var p = new byte[28];
        int[] v = { guildDbId, memberDbId, worldId, guardId, sectionId, level, state };
        for (int i = 0; i < v.Length; i++) BitConverter.GetBytes(v[i]).CopyTo(p, i * 4);
        return p;
    }

    /// <summary>AS_UPDATE_GUILD_EMBLEM (0x15AF): `i32 GuildDbId, u8 IsForever, i32 EmblemId` -
    /// unaligned, fixed part 0x0F. Despite the name, no string.</summary>
    public static byte[] BuildAsUpdateGuildEmblem(int guildDbId, bool isForever, int emblemId)
    {
        var p = new byte[9];
        BitConverter.GetBytes(guildDbId).CopyTo(p, 0);
        p[4] = (byte)(isForever ? 1 : 0);
        BitConverter.GetBytes(emblemId).CopyTo(p, 5);
        return p;
    }

    /// <summary>AS_UPDATE_GUILD_NAME (0x1490), AS_UPDATE_GUILD_TITLE (0x1412) and
    /// AS_UPDATE_GUILD_LOGO (0x1413) share one shape: `u32 stringOff@06, i32 GuildDbId@0A` then
    /// the wstring, fixed part 0x0E. AS_UPDATE_GUILD_LOGO's string is the logo ID, not the
    /// image - the image itself never crosses this link.</summary>
    public static byte[] BuildAsGuildString(int guildDbId, string value)
    {
        var s = value ?? string.Empty;
        var p = new byte[8 + (s.Length + 1) * 2];
        BitConverter.GetBytes(FrameHeader + 8).CopyTo(p, 0);
        BitConverter.GetBytes(guildDbId).CopyTo(p, 4);
        int at = 8;
        foreach (char ch in s) { p[at++] = (byte)ch; p[at++] = (byte)(ch >> 8); }
        return p;
    }

    /// <summary>AS_CREATE_GUILD_GROUP (0x1407): `u32 groupNameOff@06, i32 GuildDbId@0A,
    /// i32 GuildGroupId@0E, i32 Authority@12` then the wstring, fixed part 0x16.</summary>
    public static byte[] BuildAsCreateGuildGroup(int guildDbId, int guildGroupId, string groupName, int authority)
        => GuildGroupFrame(guildDbId, guildGroupId, authority, groupName);

    /// <summary>AS_SET_GUILDGROUP_AUTHORITY (0x1405): the same shape as AS_CREATE_GUILD_GROUP,
    /// with the string called NewName.</summary>
    public static byte[] BuildAsSetGuildGroupAuthority(int guildDbId, int guildGroupId, int authority, string newName)
        => GuildGroupFrame(guildDbId, guildGroupId, authority, newName);

    private static byte[] GuildGroupFrame(int guildDbId, int guildGroupId, int authority, string name)
    {
        var s = name ?? string.Empty;
        var p = new byte[16 + (s.Length + 1) * 2];
        BitConverter.GetBytes(FrameHeader + 16).CopyTo(p, 0);
        BitConverter.GetBytes(guildDbId).CopyTo(p, 4);
        BitConverter.GetBytes(guildGroupId).CopyTo(p, 8);
        BitConverter.GetBytes(authority).CopyTo(p, 12);
        int at = 16;
        foreach (char ch in s) { p[at++] = (byte)ch; p[at++] = (byte)(ch >> 8); }
        return p;
    }

    /// <summary>AS_LOAD_GUILD_DATA (0x144D) and AS_UPDATE_GUILD_DATA (0x144E):
    /// `u32 blobOff@06, u32 blobLen@0A` then the raw GuildData - NOT a wstring, despite what
    /// the .def says. AS_LOAD_GUILD_DATA hard-codes the length to 0x23A0.</summary>
    public static byte[] BuildAsGuildData(byte[] guildData)
    {
        var p = new byte[8 + guildData.Length];
        BitConverter.GetBytes(FrameHeader + 8).CopyTo(p, 0);
        BitConverter.GetBytes(guildData.Length).CopyTo(p, 4);
        guildData.CopyTo(p, 8);
        return p;
    }

    /// <summary>AS_ADD_GUILDMEMBER (0x140A): `u32 nameOff@06`, nine i32, i64 LogoutTime@36,
    /// u8 IsWorldEventTarget@3E, i64 AccountDbId@3F, i64 LastJoinGuildTime@47, then the
    /// wstring; fixed part 0x4F. The .def is missing IsWorldEventTarget and LastJoinGuildTime.</summary>
    public static byte[] BuildAsAddGuildMember(int guildDbId, int addeeDbId, string name, int worldId,
        int guardId, int sectionId, int level, int race, int userClass, int gender, int state,
        int guildGroupId, long logoutTime, bool isWorldEventTarget, long accountDbId, long lastJoinGuildTime)
    {
        var s = name ?? string.Empty;
        var p = new byte[0x49 + (s.Length + 1) * 2];
        BitConverter.GetBytes(FrameHeader + 0x49).CopyTo(p, 0);
        int[] v = { guildDbId, addeeDbId, worldId, guardId, sectionId, level, race, userClass, gender, state, guildGroupId };
        for (int i = 0; i < v.Length; i++) BitConverter.GetBytes(v[i]).CopyTo(p, 4 + i * 4);
        BitConverter.GetBytes(logoutTime).CopyTo(p, 0x30);
        p[0x38] = (byte)(isWorldEventTarget ? 1 : 0);
        BitConverter.GetBytes(accountDbId).CopyTo(p, 0x39);
        BitConverter.GetBytes(lastJoinGuildTime).CopyTo(p, 0x41);
        int at = 0x49;
        foreach (char ch in s) { p[at++] = (byte)ch; p[at++] = (byte)(ch >> 8); }
        return p;
    }

    // =======================================================================================
    // World -> Arbiter parsers. Each takes the frame PAYLOAD; a field the decompile quotes at
    // frame+N is payload index N-6. Every SA_ guild frame after the opcode starts with
    // `i64 ArbiterUser` - the handle of the user who asked - which is the routing key the
    // Arbiter uses to find the session, exactly like the party SA_ frames' OwnerPlanetId.
    // =======================================================================================

    /// <summary>SA_LOAD_GUILD (0x13FB) and SA_DESTROY_GUILD (0x13FC): `i64 ArbiterUser@06,
    /// i32 GuildDbId@0E`, frame 0x12.</summary>
    public static (long arbiterUser, int guildDbId)? ParseSaGuildAction(byte[] payload)
        => payload.Length < 12 ? null : (BitConverter.ToInt64(payload, 0), BitConverter.ToInt32(payload, 8));

    /// <summary>SA_LEAVE_GUILD (0x13FE) and SA_BANISH_GUILD_MEMBER (0x1400):
    /// `u32 memberNameOff@06, i64 ArbiterUser@0A, i32 GuildDbId@12, i32 MemberDbId@16`,
    /// frame 0x1A.</summary>
    public static (string memberName, long arbiterUser, int guildDbId, int memberDbId)? ParseSaLeaveGuild(byte[] payload)
    {
        if (payload.Length < 20) return null;
        int nameOff = BitConverter.ToInt32(payload, 0);
        return (ReadWStringAtPayload(payload, nameOff - FrameHeader), BitConverter.ToInt64(payload, 4),
                BitConverter.ToInt32(payload, 12), BitConverter.ToInt32(payload, 16));
    }

    /// <summary>SA_CHANGE_GUILD_CHIEF (0x1402): `i64 ArbiterUser@06, i32 GuildDbId@0E,
    /// i32 NewChiefDbId@12`, frame 0x16.</summary>
    public static (long arbiterUser, int guildDbId, int newChiefDbId)? ParseSaChangeGuildChief(byte[] payload)
        => payload.Length < 16 ? null
            : (BitConverter.ToInt64(payload, 0), BitConverter.ToInt32(payload, 8), BitConverter.ToInt32(payload, 12));

    /// <summary>SA_REMOVE_GUILD_GROUP (0x1408): `i64 ArbiterUser@06, i32 GuildDbId@0E,
    /// i32 GroupId@12`, frame 0x16.</summary>
    public static (long arbiterUser, int guildDbId, int groupId)? ParseSaRemoveGuildGroup(byte[] payload)
    {
        var v = ParseSaChangeGuildChief(payload);
        return v == null ? null : (v.Value.arbiterUser, v.Value.guildDbId, v.Value.newChiefDbId);
    }

    /// <summary>SA_CHANGE_GUILDGROUP (0x140B): `i64 ArbiterUser@06, i32 GuildDbId@0E,
    /// i32 MemberDbId@12, i32 GuildGroupId@16`, frame 0x1A.</summary>
    public static (long arbiterUser, int guildDbId, int memberDbId, int guildGroupId)? ParseSaChangeGuildGroup(byte[] payload)
        => payload.Length < 20 ? null
            : (BitConverter.ToInt64(payload, 0), BitConverter.ToInt32(payload, 8),
               BitConverter.ToInt32(payload, 12), BitConverter.ToInt32(payload, 16));

    /// <summary>SA_CREATE_GUILD_GROUP (0x1406): `u32 groupNameOff@06, i64 ArbiterUser@0A,
    /// i32 GuildDbId@12`, frame 0x16.</summary>
    public static (string groupName, long arbiterUser, int guildDbId)? ParseSaCreateGuildGroup(byte[] payload)
    {
        if (payload.Length < 16) return null;
        int nameOff = BitConverter.ToInt32(payload, 0);
        return (ReadWStringAtPayload(payload, nameOff - FrameHeader), BitConverter.ToInt64(payload, 4),
                BitConverter.ToInt32(payload, 12));
    }

    /// <summary>SA_SET_GUILDGROUP_AUTHORITY (0x1404): `u32 newNameOff@06, i64 ArbiterUser@0A,
    /// i32 GuildDbId@12, i32 GuildGroupId@16, i32 Authority@1A`, frame 0x1E.</summary>
    public static (string newName, long arbiterUser, int guildDbId, int guildGroupId, int authority)?
        ParseSaSetGuildGroupAuthority(byte[] payload)
    {
        if (payload.Length < 24) return null;
        int nameOff = BitConverter.ToInt32(payload, 0);
        return (ReadWStringAtPayload(payload, nameOff - FrameHeader), BitConverter.ToInt64(payload, 4),
                BitConverter.ToInt32(payload, 12), BitConverter.ToInt32(payload, 16), BitConverter.ToInt32(payload, 20));
    }

    /// <summary>SA_UPDATE_GUILD_MEMBER (0x140F): `i32 GuildDbId@06, i32 MemberDbId@0A`, frame 0x0E.</summary>
    public static (int guildDbId, int memberDbId)? ParseSaUpdateGuildMember(byte[] payload)
        => payload.Length < 8 ? null : (BitConverter.ToInt32(payload, 0), BitConverter.ToInt32(payload, 4));

    // =======================================================================================
    // Corrected .def text. The MASTER_FINAL guild defs are mostly right - S_GUILD_INFO and
    // S_GUILD_MEMBER_LIST get every field boundary correct, which is a better record than the
    // party set managed. These ten do not, and the tests pin each correction against the
    // offsets the Arbiter's own PDL dumper guards prove.
    // =======================================================================================
    public static IReadOnlyDictionary<string, string> CorrectedDefs { get; } = new Dictionary<string, string>
    {
        // stride 0x48 -> 0x49: the member element ends with a bool the .def never had, and
        // contributionCurrent/contributionTotal is i32 + i64, not i32 + i32 + i32.
        ["S_GUILD_MEMBER_LIST"] = @"ref members
ref guildName
ref guildMaster

int32  guildDbId
int32  chiefDbId
int32  guildLevel
int64  guildExp
int64  guildNextExp
int64  guildMoney
int32  memberCount
int32  accountCount
int32  guildSize
int64  guildCreateDate
bool   ended
bool   clearCache
bool   showGuildWindow
string guildName
string guildMaster

array members
- ref userName
- ref userAnnounce
- int32 userDbId
- int32 memberType
- int32 worldId
- int32 guardId
- int32 sectionId
- int32 groupId
- int32 userLevel
- int32 race
- int32 userClass
- int32 gender
- int32 state
- int32 weeklyContributionPoint
- int64 totalContributionPoint
- int64 lastLogoutTime
- bool  cityWarCompensationStatus
- string userName
- string userAnnounce
",
        // 0x33 -> 0x38: gender at 0x2A and a trailing bool.
        ["S_ADD_GUILD_MEMBER"] = @"int32  memberDbId
string name
int32  worldId
int32  guardId
int32  sectionId
int32  groupId
int32  userLevel
int32  race
int32  userClass
int32  state
int32  gender
int64  lastLogoutTime
bool   isWorldEventTarget
bool   cityWarCompensationStatus
",
        // 0x38 -> 0x39: three trailing bools, not two.
        ["S_UPDATE_GUILD_MEMBER"] = @"int32  memberDbId
int32  worldId
int32  guardId
int32  sectionId
int32  groupId
int32  userLevel
int32  race
int32  userClass
int32  status
int32  gender
int64  lastLogoutTime
bool   isWorldEventTarget
bool   isInGuildWarCombatState
bool   cityWarCompensationStatus
string name
",
        // 0x06 -> 0x07: the usable/success flag the name-check family all carry.
        ["S_CHECK_NEW_GUILDNAME"] = "string guildName\nbool   usable\n",
        ["S_RESULT_USABLE_GUILD_NAME"] = "string guildName\nbool   usable\n",
        ["S_CHECK_CHANGE_GUILDNAME"] = "string guildName\nbool   usable\n",
        ["S_CHANGE_GUILDNAME"] = "string guildName\nbool   usable\n",
        ["S_RESULT_RENAME_GUILD"] = "string guildName\nbool   usable\n",
        // the .def stops at menuId and drops the list entirely.
        ["S_GUILD_LOG"] = @"ref logList

int32 menuId

array logList
- string logString
- int64  dateTime
",
        // 0x06 -> 0x08: a bytes field (offset, count), not a string. The handler passes the
        // u16 at +0x06 to Guild::UpdateGuildLogo as the byte count and rejects 8000 < len.
        ["C_UPDATE_GUILD_LOGO"] = "bytes logoImage\n",
    };

    // =======================================================================================
    // NamedDefs (T39) - defs whose LAYOUT the shipped file already gets right, re-stated with
    // the field names the Arbiter's own PDL dumper uses. Nothing here changes a byte; the test
    // Guild_named_defs_are_byte_identical_to_the_shipped_ones proves it for the same data.
    //
    // Purely for the code that fills them in: the shipped S_GUILD_INFO.1.def calls 20 of its 31
    // fields unk1..unk20, and a handler assembling that dictionary by number is unreadable and
    // unreviewable. Two fields also merge or split: unk1+unk2 are one i64 GuildInfo, and unk12
    // is really LordBehaviorRank + LordBehaviorPoints.
    // =======================================================================================
    public static IReadOnlyDictionary<string, string> NamedDefs { get; } = new Dictionary<string, string>
    {
        ["S_GUILD_INFO"] = @"ref guildGroups
ref guildName
ref chiefName
ref guildAnnounce
ref guildGroupName
ref guildPromotion
ref guildLogoId

int32 guildDbId
int64 guildInfo
int32 chiefDbId
int64 guildCreateDate
int32 guildLevel
int64 guildExp
int64 guildNextExp
int64 guildMoney
int32 recommendationPoint
int32 policyPoint
bool  needChangeGuildName
int32 battleChip
int32 guildSize
int32 playingMemberCount
int32 memberCount
int32 accountCount
int32 maxAccountCount
int32 accountCountCanGuildWar
bool  isGuildWarAcceptable
int64 guildWarAcceptableToggleTime
int32 lordBehaviorRank
int32 lordBehaviorPoints
bool  isHaveFloatingCastle
int32 floatingCastleCoinAmount
int32 guildPreference
int32 joinMinLevel
int32 joinMaxLevel
int32 joinType
int32 guildWindowType
bool  isOccupation

string guildName
string chiefName
string guildAnnounce
string guildGroupName
string guildPromotion
string guildLogoId

array guildGroups
- ref groupName
- int32 groupId
- int32 groupAuthority
- string groupName
",
        ["S_GUILD_APPLY_LIST"] = @"ref guildApplyList

bool  inviteAuthority
int32 curPageNum
int32 totalPageCount

array guildApplyList
- ref userName
- ref joinMsg
- int32 userDbId
- int32 classType
- int32 userLevel
- int64 dateTime
- string userName
- string joinMsg
",
    };

    // ---------------------------------- private helpers ----------------------------------

    /// <summary>Reads a NUL-terminated UTF-16LE string whose u16 PACKET offset sits at
    /// body[slotIndex]. Returns "" for the 0/out-of-range offsets the real handlers fall back
    /// on (they substitute the shared empty string at DAT_140d3e020).</summary>
    private static string ReadWString(byte[] body, int slotIndex)
    {
        if (slotIndex + 2 > body.Length) return string.Empty;
        int off = BitConverter.ToUInt16(body, slotIndex) - 4;
        return ReadWStringAtPayload(body, off);
    }

    private static string ReadWStringAtPayload(byte[] buf, int at)
    {
        if (at < 0 || at >= buf.Length) return string.Empty;
        var sb = new System.Text.StringBuilder();
        for (int i = at; i + 1 < buf.Length; i += 2)
        {
            char ch = (char)(buf[i] | (buf[i + 1] << 8));
            if (ch == '\0') break;
            sb.Append(ch);
        }
        return sb.ToString();
    }

    private static byte[] Pair(int a, int b)
    {
        var p = new byte[8];
        BitConverter.GetBytes(a).CopyTo(p, 0);
        BitConverter.GetBytes(b).CopyTo(p, 4);
        return p;
    }

    /// <summary>
    /// The client packet body layout the Arbiter's writers produce: reserve every ref slot
    /// first, then the fixed scalars, then the variable data, backpatching PACKET-relative
    /// offsets. Same rules as Protocol/DefinitionWriter, small enough to keep the guild
    /// builders readable.
    /// </summary>
    private sealed class Body
    {
        private const int Header = 4;
        private readonly List<byte> _b = new(64);

        public int PacketOffset => _b.Count + Header;
        public int Reserve() { int p = _b.Count; U16(0); return p; }
        public void U16(ushort v) { _b.Add((byte)v); _b.Add((byte)(v >> 8)); }
        public void Bool(bool v) => _b.Add(v ? (byte)1 : (byte)0);
        public void I32(int v) => _b.AddRange(BitConverter.GetBytes(v));
        public void I64(long v) => _b.AddRange(BitConverter.GetBytes(v));
        public void Patch(int slot, ushort v) { _b[slot] = (byte)v; _b[slot + 1] = (byte)(v >> 8); }

        public void Str(int slot, string? s)
        {
            Patch(slot, (ushort)PacketOffset);
            foreach (char ch in s ?? string.Empty) U16(ch);
            U16(0);
        }

        public void Bytes(int slot, byte[] d)
        {
            Patch(slot, (ushort)PacketOffset);
            Patch(slot + 2, (ushort)d.Length);
            _b.AddRange(d);
        }

        /// <summary>Fills an array's (count, offset) header. Offset stays 0 for an empty array.</summary>
        public void BeginArray(int countSlot, int offsetSlot, int count)
        {
            Patch(countSlot, (ushort)count);
            Patch(offsetSlot, count == 0 ? (ushort)0 : (ushort)PacketOffset);
            _prevNextSlot = -1;
        }

        /// <summary>
        /// Starts an element: `[u16 self][u16 next]`. `next` points at the element AFTER this
        /// one, so it is patched when the next BeginElement runs; the last element keeps 0.
        /// </summary>
        public void BeginElement()
        {
            if (_prevNextSlot >= 0) Patch(_prevNextSlot, (ushort)PacketOffset);
            U16((ushort)PacketOffset);
            _prevNextSlot = _b.Count;
            U16(0);
        }

        private int _prevNextSlot = -1;

        public byte[] ToArray() => _b.ToArray();
    }
}
