// SPDX-License-Identifier: MIT
// Copyright (c) 2026 the TeraSharp contributors

using Microsoft.Extensions.Logging;
using TeraSharp.Arbiter.Persistence;

namespace TeraSharp.Arbiter.World;

/// <summary>
/// Real implementations of the DB-proxy messages WorldServer sends to the Arbiter.
/// Layouts from the decompiled ArbiterServer (Handler_SDB_* in Arb_part_063/064.c).
/// All offsets are FRAME-relative (the 6-byte [len][op] header counts).
///
///   SDB_USER_ENTERWORLD (0x2711), 24 bytes:
///     [6] u32 nameOffset  [10] u32 replyId  [14] u32 playerId  [18] u32 unk  [22] wstr name
///   DBS_USER_ENTERWORLD (0x2738):
///     [6] u32 blobOffset=19  [10] u32 blobLen  [14] u32 replyId  [18] u8 found  [19] blob
///
///   SDB_UPDATE_USER_DATA (0x27CB):
///     [6] u32 blobOffset=31  [10] u32 blobLen  [14] u32 trailerOffset  [18] u32 trailerLen
///     [22] u32 reqId  [26] u32 playerId  [30] u8 flag  [31] blob  [trailerOffset] trailer
///   DBS_UPDATE_USER_DATA (0x27CC):
///     [6] u32 reqId  [10] u32 result
///
/// --- Login-time SDB_* request layouts (two shapes) ---
///
///   SDB shape (0x27xx–0x29xx): [6] u32 reqId  [10] u32 playerId  (8-byte payload)
///   SA  shape (0x13xx–0x16xx): [6] u64 gameId  [14] u32 reqId  [18] u32 playerId  (12–16 byte payload)
///
/// --- Empty-list reply patterns (from decompile, two orderings) ---
///
///   Type 1 (reqId-first): [u32 listOff=19][u32 listCount=0][u32 reqId][u8 ok]  — 13 bytes
///   Type 2 (ok-first):    [u32 listOff=19][u32 listCount=0][u8 ok][u32 reqId]  — 13 bytes
///
/// Type 1: 0x27A2, 0x2760, 0x2764, 0x27A7, 0x28BB, 0x28CF, 0x28FE, 0x28C9, 0x28C5, 0x2922, 0x2967
/// Type 2: 0x2912, 0x2916, 0x2895, 0x1521, 0x2833
/// </summary>
public sealed partial class DbProxyHandlers
{
    public const ushort SDB_USER_ENTERWORLD = 0x2711;
    public const ushort DBS_USER_ENTERWORLD = 0x2738;
    public const ushort SDB_UPDATE_USER_DATA = 0x27CB;
    public const ushort DBS_UPDATE_USER_DATA = 0x27CC;
    public const int WorldBlobSize = 0x3BD0; // 15312

    // Logout save sequence (World -> us -> reply). reqId is a per-session counter World
    // assigns, so it MUST be echoed from the live request, not replayed from the capture.
    // Offsets below are payload-relative (frame offset - 6) and verified against the
    // captured arb_world.log and the inline reply writers in the decompile.
    public const ushort SDB_SAVE_27FA = 0x27FA; public const ushort DBS_SAVE_27FB = 0x27FB; // reqId @280
    public const ushort SDB_SAVE_2924 = 0x2924; public const ushort DBS_SAVE_2925 = 0x2925; // reqId @8
    /// <summary>
    /// T64. SDB_UPDATE_LEFT_COOLTIME_PREMIUM_SLOT (0x28C1) -&gt; DBS (0x28C2). A per-user DB
    /// write that carries a DlmId, so it head-blocks the user's queue if nothing answers it.
    /// Request (dumper Arb_part_017.c:18676, guard 0x21): <c>u32 listOff@06, u32 listBytes@0A,
    /// i32 DlmId@0E, i64 ArbiterUser@12, i64 OwnerDbId@1A</c> then the list. Reply (dumper
    /// Arb_part_015.c:19226, guard 10): <c>i32 DlmId@06, u8 Success@0A</c> - the plain
    /// <see cref="BuildReqIdAck"/> shape, with DlmId at payload offset 8.
    /// cap_social.log seq 634 -&gt; 635 and 1894 -&gt; 1895.
    /// </summary>
    public const ushort SDB_UPDATE_LEFT_COOLTIME_PREMIUM_SLOT = 0x28C1;
    public const ushort DBS_UPDATE_LEFT_COOLTIME_PREMIUM_SLOT = 0x28C2;
    public const ushort SDB_SAVE_2768 = 0x2768; public const ushort DBS_SAVE_2769 = 0x2769; // reqId @16

    // --- T145: the two wedges cap_bag.log shows ---
    //
    // SDB_SET_QUESTLIST_INFO (0x2732) -> DBS_SET_QUESTLIST_INFO (0x2733). THE LOGIN WEDGE for a
    // character whose starting quests World sets in one batch. cap_bag.log record 314 (00:34:00.600,
    // character `test`, 9 quests, 759 B) is the last SDB_ frame for that character in the whole
    // capture: no 0x2930 hold-status load (every real spawn has one ~4 s later), no learn-skill
    // write for its nine C_SKILL_LEARN_REQUESTs, no bind/equip write - they all queued behind it.
    // arbiter-2026-09-20.log 14:29:10 / 14:31:20: "no replay for 0x2732", same wedge.
    //
    // Request, payload offsets: [u32 offA frame-rel][u32 lenA = 80 x n][u32 offB][u32 lenB]
    // [u32 offC][u32 lenC][u32 reqId][u32 playerId][u8 1] then n 80-byte quest records - the
    // record SDB_SET_QUEST_INFO carries (dbId +0, questId +4, status +8, step +12).
    // Reply (Handler_DBS_SET_QUESTLIST_INFO, frame >= 0x24): [offA][lenA = 8 x n][offB][0]
    // [offC][0][reqId][u8 ok][u8 apply] then n (questId, questDbId) pairs - World keys its quest
    // map with them. apply = 0 makes World ignore the reply and leave the transaction open, so
    // it is always 1. Byte-exact against cap_social4.log 2935->2936, 3244->3245, 7843->7844.
    public const ushort SDB_SET_QUESTLIST_INFO = 0x2732; public const ushort DBS_SET_QUESTLIST_INFO = 0x2733;
    public const int QuestListRequestHeader = 33;
    public const int SetQuestListReplyHeader = 30;

    // SDB_EQUIP_ITEM (0x2813) -> DBS_EQUIP_ITEM (0x2814). World's writer (FUN_140941b10) is the
    // same generated template as SDB_ITEM_SINGLE's (FUN_140a48c40): four ref slots, reqId,
    // playerId, then two lists of 856-byte transaction atoms - 886 B is one atom. And
    // Handler_DBS_EQUIP_ITEM reads exactly what Handler_DBS_ITEM_SINGLE does (frame >= 0x1b,
    // lists at +6/+0xe, reqId +0x16, ok +0x1a). So it is answered and applied by the same code.
    // No tap has one yet - cap_bag's never left World, it was behind 0x2732.
    public const ushort SDB_EQUIP_ITEM = 0x2813; public const ushort DBS_EQUIP_ITEM = 0x2814;

    /// <summary>
    /// DBS_SET_QUESTLIST_INFO from the request. <paramref name="assign"/> gets each record's
    /// questId and the 80-byte record and returns the row id World is to use for it.
    /// A request too short to read still gets a reply with no pairs - silence is the wedge.
    /// </summary>
    public static byte[] BuildDbs2733(byte[] request, Func<int, byte[], int> assign)
    {
        ArgumentNullException.ThrowIfNull(assign);
        var records = new List<byte[]>();
        uint reqId = 0;
        if (request != null && request.Length >= QuestListRequestHeader)
        {
            int at = (int)BitConverter.ToUInt32(request, 0) - 6;
            int n = (int)(BitConverter.ToUInt32(request, 4) / QuestRecordSize);
            reqId = BitConverter.ToUInt32(request, 24);
            for (int i = 0; i < n && at >= 0 && at + (i + 1) * QuestRecordSize <= request.Length; i++)
                records.Add(request.AsSpan(at + i * QuestRecordSize, QuestRecordSize).ToArray());
        }
        else if (request != null && request.Length >= 28) reqId = BitConverter.ToUInt32(request, 24);

        int pairs = records.Count * 8;
        uint end = (uint)(6 + SetQuestListReplyHeader + pairs);
        var r = new byte[SetQuestListReplyHeader + pairs];
        BitConverter.GetBytes(6u + SetQuestListReplyHeader).CopyTo(r, 0);
        BitConverter.GetBytes((uint)pairs).CopyTo(r, 4);
        BitConverter.GetBytes(end).CopyTo(r, 8);
        BitConverter.GetBytes(end).CopyTo(r, 16);
        BitConverter.GetBytes(reqId).CopyTo(r, 24);
        r[28] = 1;   // ok
        r[29] = 1;   // apply
        for (int i = 0; i < records.Count; i++)
        {
            int questId = BitConverter.ToInt32(records[i], QuestRecordQuestIdOffset);
            BitConverter.GetBytes(questId).CopyTo(r, SetQuestListReplyHeader + i * 8);
            BitConverter.GetBytes(assign(questId, records[i])).CopyTo(r, SetQuestListReplyHeader + i * 8 + 4);
        }
        return r;
    }

    // --- SDB_ITEM_SINGLE (0x2768) -> DBS_ITEM_SINGLE (0x2769), T13 ---
    // Not a logout-only save: this is every inventory write World makes (pick up, use, move,
    // combine, money). Handler_SDB_ITEM_SINGLE (ArbiterServer.exe.c FUN_14074aca0, tracer line
    // 1271688) needs frame >= 0x1E and reads, FRAME-relative:
    //   [6]  u32 offsetA  [10] u32 lengthA      list A, bytes; offsets are frame-relative
    //   [14] u32 offsetB  [18] u32 lengthB      list B, same shape, executed after A
    //   [22] u32 reqId    [26] u32 playerId
    // then lengthX / 0x358 records of 856 bytes, each an ItemTransactionAtom. The reply writer
    // (FUN_1406ec9e0) emits four backpatch slots, the id and the ok byte -- a 21-byte header --
    // then both lists, so the reply frame is the request frame minus 3.
    //
    // The reply is the atoms ECHOED BACK, not an empty list. Verified against cap_newchar.log:
    //   2072 -> 2073   one atom, op 7   only byte change in 856:  [16] 0 -> 15
    //   2211 -> 2213   five atoms       atoms 0..3 (ops 6,11,6,11) identical; atom 4 (op 7) [16] 0 -> 16
    //   2290/2306/3477 (ops 2, 2, 9)    byte-identical
    // So: copy the atoms through, and for an insert whose item DB id is still 0, fill in the id
    // the Arbiter allocated. World keys its in-memory item by that id -- returning 0, or dropping
    // the atoms entirely (what we did before T13), loses the item World thinks it just saved.
    //
    // Atom layout, atom-relative (status/INVENTORY-DESIGN.md has the rest):
    //   [0] u32 index in the list   [4] u32 operation   [16] u32 item DB id (0 = allocate one)
    //   [24] u32 item template id   [48] u32 slot       [0x50] i64 signed amount delta
    public const int ItemAtomSize = 0x358;              // 856
    public const int ItemAtomOpOffset = 4;
    public const int ItemAtomDbIdOffset = 16;
    /// <summary>0x2768 payload header: two [offset][length] pairs, reqId, playerId.</summary>
    public const int ItemSingleRequestHeader = 24;
    /// <summary>0x2769 payload header: the same two pairs, reqId, ok byte.</summary>
    public const int ItemSingleReplyHeader = 21;
    /// <summary>
    /// The insert operation -- the only op in the capture that arrives with item DB id 0 and
    /// comes back with one filled in. Derived from the World-side builders; see
    /// status/INVENTORY-DESIGN.md section 4 for the rest of the enum.
    /// </summary>
    public const uint TsInsertItem = 7;
    // --- SDB_ITEM_TRADE (0x276A) -> DBS_ITEM_TRADE (0x276B), T65 ---
    // The DB half of a completed player-to-player trade. It arrived live on 2026-09-16 (2306 B,
    // "no replay") and, because nothing answered it, head-blocked that character's DLM queue.
    //
    // Dumper FUN_1402329b0 (Arb_part_017.c:9283), GUARD 0x21 -> min frame 0x22; handler
    // FUN_1407... (Arb_part_063.c:11848). FRAME-relative:
    //   [06] u32 OwnerBinary offset   [0A] u32 OwnerBinary bytes
    //   [0E] u32 TargetBinary offset  [12] u32 TargetBinary bytes
    //   [16] i32 DlmId   [1A] i32 OwnerDBID   [1E] i32 TargetDBID
    // so the payload header is 28 bytes - SDB_ITEM_SINGLE's 24 plus the second db-id.
    //
    // The two lists are NOT 856-byte ItemTransactionAtoms. The handler divides by 0x238 and
    // passes the records to TransSQLExec::CanExecTrans(vector&lt;ItemTransactionGiveTake const *&gt;),
    // so the record is <b>ItemTransactionGiveTake, 568 bytes</b>. The head is the same as the
    // 856-byte atom - DO_TS_CHANGE_ITEM_OWNER (Arb_part_037.c:18245) reads +0x10 item db id,
    // +0x20/+0x28 src owner+inven, +0x38/+0x40/+0x48 dst owner+inven+slot, op at +0x04 - which
    // is why WarehouseHandlers can parse both with one reader and only the stride differs.
    // (2306 = 6 + 28 + 4 * 0x238 exactly, which is the check that the stride is right.)
    //
    // The reply writer FUN_1406ece60 (Arb_part_060.c:6123) emits four backpatch slots, the
    // DlmId and the ok byte - the same 21-byte header as DBS_ITEM_SINGLE - then both lists
    // copied back 0x238 bytes at a time. So the echo rule is T13's, at the other stride.
    public const ushort SDB_ITEM_TRADE = 0x276A;
    public const ushort DBS_ITEM_TRADE = 0x276B;
    /// <summary>ItemTransactionGiveTake - the 568-byte record SDB_ITEM_TRADE carries.</summary>
    public const int ItemGiveTakeSize = 0x238;          // 568
    /// <summary>0x276A payload header: two [offset][length] pairs, DlmId, OwnerDBID, TargetDBID.</summary>
    public const int ItemTradeRequestHeader = 28;

    public const ushort SDB_SAVE_2936 = 0x2936; public const ushort DBS_SAVE_2937 = 0x2937; // reqId @0
    public const ushort SDB_DAILY_QUEST = 0x2897; public const ushort DBS_DAILY_QUEST = 0x2898; // reqId @16

    // --- S_UPDATE_EXP_LEVEL (0x273B) -> D_UPDATE_EXP_LEVEL (0x273C) ---
    // World's only report of level and exp. Everything else about progression lives inside the
    // opaque world blob, so this is what makes the `level` the lobby shows (S_GET_USER_LIST)
    // and the `exp` column real instead of frozen at 1/0.
    //
    // Handler_S_UPDATE_EXP_LEVEL (ArbiterServer.exe.c FUN_1408f44b0, scope tracer at line
    // 1564123) requires frame length >= 0x2e (46 B, i.e. a 40-byte payload) and reads, all
    // FRAME-relative:
    //   [6]  u32 reqId       -> echoed in the reply
    //   [10] u32 playerId    -> user lookup; a miss means ok = 0 and no DB write
    //   [14] i32 level       -> < 1 : User::UpdateUserExpAndRestBonusPoint(exp, restBonus)
    //                          >= 1: User::UpdateUserLevel(level, exp, restBonus)
    //   [18] i64 exp         -> spUpdateUserTotalExpA
    //   [26] i64 restBonusPoint
    //   [34] i64  > 0 -> FUN_14057fab0()          (unused by us; 0 in every captured frame)
    //   [42] i32  > 0 -> FUN_140572d20(user, pid) (unused by us; 0 in every captured frame)
    // Reply writer: FUN_140350eb0(pkt, 0x273c), FUN_14013d0b0 = u32 reqId, FUN_1403513d0 = u8 ok.
    //
    // Ground truth (D:\packetlogs\cap_newchar.log, real ArbiterServer, playerId 2, reframed):
    //   1351 W->A 0x273B 46 B  5D 00 00 00 | 02 00 00 00 | 00 00 00 00 | C7 00 .. (exp 199)
    //   1352 A->W 0x273C 11 B  5D 00 00 00 01
    //   2687 W->A 0x273B        8A 00 00 00 | 02 00 00 00 | 02 00 00 00 | 6B 03 .. (level 2, exp 875)
    //   2688 A->W 0x273C        8A 00 00 00 01
    // Ten of these in that capture; the level field is non-zero only on the one level-up.
    public const ushort SDB_UPDATE_EXP_LEVEL = 0x273B; public const ushort DBS_UPDATE_EXP_LEVEL = 0x273C;
    /// <summary>Handler minimum frame length (0x2e) — a shorter frame is a PDL mismatch.</summary>
    public const int UpdateExpLevelMinPayload = 0x2e - 6;

    // SDB_UPDATE_DAILY_QUEST_SEED (0x2899) -> DBS_UPDATE_DAILY_QUEST_SEED (0x289A).
    // World fires this once per daily quest at enter-world (17x in lobby_tap.log) and each one
    // is a DLMItem serialised on the user's gameId, so an unanswered one head-blocks every
    // later per-user DB message for the life of the process. It is absent from arb_world.log,
    // so the replay table has no entry and TryHandle is the only thing that can answer it.
    //   request  90B frame: [0]u32 blobOff=22 [4]u32 blobLen=68 [8]u32 reqId [12]u32 playerId
    //                       [16] 68-byte daily-quest seed blob
    //   reply    11B frame: [0]u32 reqId [4]u8 ok=1
    // Ground truth (lobby_tap.log 02:51:10.8xx): req ...2b 00 00 00 01 00 00 00 59 02...
    //                                            rsp 2b 00 00 00 01
    public const ushort SDB_DAILY_QUEST_SEED = 0x2899; public const ushort DBS_DAILY_QUEST_SEED = 0x289A; // reqId @8

    // --- Login-time SDB_* (empty-list Type 1: [off=19][count=0][reqId][ok]) ---
    // Request: [u32 reqId][u32 playerId], reqId at payload[0]
    public const ushort SDB_USER_LOAD_INVENTORY = 0x27A2;       // -> 0x27A3
    public const ushort SDB_LOAD_ITEM_RECIPE = 0x2760;          // -> 0x2761
    public const ushort SDB_LOAD_SKILL_PROF = 0x2764;           // -> 0x2765
    public const ushort SDB_LOAD_TELEPORT_TO_POS_LIST = 0x27A7; // -> 0x27A8
    public const ushort SDB_LOAD_ACCOUNT_BENEFIT = 0x28BB;      // -> 0x28BC
    public const ushort SDB_LOAD_LEARNED_SOCIAL = 0x28CF;       // -> 0x28D0
    public const ushort SDB_LOAD_TOKEN_EXCHANGE = 0x28FE;       // -> 0x28FF
    public const ushort SDB_LOAD_SKILLPERIOD = 0x28C9;          // -> 0x28CA
    public const ushort SDB_LOAD_SERVANT_PERIOD = 0x28C5;       // -> 0x28C6
    public const ushort SDB_LOAD_PASSIVITY_COOLTIME = 0x2922;   // -> 0x2923
    public const ushort SDB_LOAD_ADDITIONAL_REWARD = 0x2967;    // -> 0x2968

    // --- Login-time SDB_* (empty-list Type 2: [off=19][count=0][ok][reqId]) ---
    public const ushort SDB_LOAD_PROMOTION_LIST = 0x2912;         // -> 0x2913
    public const ushort SDB_LOAD_PROMOTION_COND_LIST = 0x2916;   // -> 0x2917
    public const ushort SDB_LOAD_BATTLE_FIELD_LIST = 0x2895;     // -> 0x2896
    public const ushort SDB_LOAD_USER_RESTRICTION = 0x2833;      // -> 0x2834

    // --- SA_ shape: request [u64 gameId][u32 reqId] or [u64 gameId][u32 reqId][u32 playerId] ---
    public const ushort SA_LOAD_BATTLE_FIELD_COOL_TIME = 0x1521; // -> 0x1522 (Type 2, reqId @payload[8])
    public const ushort SA_LOAD_BATTLE_FIELD_ENTER_COUNT = 0x155D; // -> 0x155E
    // SA_CLEAR_BATTLE_FIELD_ENTER_COUNT (0x1562) -> AS_CLEAR_BATTLE_FIELD_ENTER_COUNT (0x1563).
    // World fires this on the daily battlefield-count reset, whenever that lands during a
    // session; it is a per-user DLMItem, so unanswered it head-blocks UserLeaveWorld.
    // Handler_SA_CLEAR_BATTLE_FIELD_ENTER_COUNT (Arb_part_062.c:4752):
    //   req 26B frame: [6]u64 gameId [14]u32 reqId [18]u64 arg   -> reqId at payload[8]
    //   rsp: [u8 ok][u32 reqId]   (ok = user valid & clear succeeded)
    public const ushort SA_CLEAR_BATTLE_FIELD_ENTER_COUNT = 0x1562; public const ushort AS_CLEAR_BATTLE_FIELD_ENTER_COUNT = 0x1563;

    // SA_UPDATE_BATTLE_FIELD_COOL_TIME (0x1523) -> AS_UPDATE_BATTLE_FIELD_COOL_TIME (0x1524).
    // T208. World sends one per user a second after a battleground entry completes, and it is
    // a per-user DLMItem exactly like 0x1562 above: unanswered it head-blocks UserLeaveWorld, so
    // "Leave battleground" never produces SA_LEAVE_WORLD and the player stays in the BF World.
    //   cap_bg1 12933 -> 12940 (01 5D 00 00 00) and 12944 -> 12945 (01 64 00 00 00): reply is
    //   [u8 ok][u32 reqId], reqId at payload[16] (after u32 vectorOffset, u32 vectorBytes, u64 gameId).
    //   cap_bg2 56491/56497 are the same frames unanswered; arbiter-bg2.log 20:04:04/20:04:07 then
    //   shows 0x13CD -> 0x1392 with no 0x1393 back, and the 20:04:50 relog is refused (reason 3).
    // The 24-byte record at payload[20] is (battleFieldId, OURS: unpinned u32, endTime, 0, 0, 0);
    // nothing is stored from it - SA_LOAD_BATTLE_FIELD_COOL_TIME still answers with an empty list.
    public const ushort SA_UPDATE_BATTLE_FIELD_COOL_TIME = 0x1523; public const ushort AS_UPDATE_BATTLE_FIELD_COOL_TIME = 0x1524;

    // SA_LEARN_ALL_CREST_ACQUIRABLE (0x1463) -> AS_LEARN_ALL_CREST_ACQUIRABLE (0x1464).
    // World sends it at first enter-world for classes that have level-1 crests (a warrior does, the
    // captured valkyrie did not - hence never seen in a capture). Per-user DLM item: unanswered = no
    // spawn. Handler_SA_LEARN_ALL_CREST_ACQUIRABLE (Arb_part_062.c:8760):
    //   req frame: [6]u32 count [10]u32 listOff [14]u64 gameId [22]u32 reqId, entries of 16 B at listOff:
    //              [u32 thisOff][u32 nextOff][i32 crestId][i32 value] (frame-relative offsets, 0 = end)
    //   rsp frame: [6]u32 count [10]u32 firstOff [14]u32 reqId [18]u8 ok, then entries in the same
    //              16-B shape - the crests World must NOT learn (T151, see BuildLearnAllCrest). Empty.
    public const ushort SA_LEARN_ALL_CREST_ACQUIRABLE = 0x1463; public const ushort AS_LEARN_ALL_CREST_ACQUIRABLE = 0x1464;

    // SDB_UPDATE_USER_ACTPOINT (0x297B) -> DBS_UPDATE_USER_ACTPOINT (0x297C). Per-user DLM, sent after
    // the first enter-world steps (cap_newchar.log seq 537/538: req 22 B [u32 reqId][u32 pid][u32][u32],
    // rsp 11 B [reqId][ok=1]). Unanswered it head-blocked everything behind it (skill learns) for the
    // first warrior login, 2026-09-14 02:16.
    public const ushort SDB_UPDATE_USER_ACTPOINT = 0x297B; public const ushort DBS_UPDATE_USER_ACTPOINT = 0x297C;

    // --- Zone change / dungeon / quest-teleport handshake (cap_newchar.log 05:52:35) ---
    // World: SA_REQUEST_ENTER_DUNGEON (0x13BE, 215 B) -> we: AS_REQUEST_ENTER_DUNGEON (0x13BF, 215 B)
    // World: SA_RESPONSE_ENTER_DUNGEON (0x13C0, 214 B) -> we: AS_RESPONSE_ENTER_DUNGEON (0x13C1, 214 B)
    // Then World sends 0x13C5 SA_ADD_DUNGEON_CHANNEL + 0x1499 SA_SAVE_ETC_DATA_FOR_MOVE_WORLD (no
    // reply), saves the blob, the client reloads and sends C_LOAD_TOPO_FIN, and our normal
    // 0x1439 + 0x1390 + 0x138F spawn it in the new zone.
    // Handler_SA_REQUEST_ENTER_DUNGEON (Arb_part_062.c:12933) / Handler_SA_RESPONSE_ENTER_DUNGEON
    // (:13707): reply = PDId [u32 worldId][u32 playerId] + DungeonEnterContext (176 B copied from
    // frame 0x0E) + DungeonOwnerInfo (frame 0xBE u64, 0xC6 u8, 0xCA u64, 0xD2 u32) [+ u8 bool from
    // 0xD6 for the request]. On the wire that is the request with the leading u64 user handle
    // replaced by the PDId; the request form additionally has three context fields the Arbiter
    // fills from its own state, all 0 in the capture (payload float@44, u32@68, float@146).
    public const ushort SA_REQUEST_ENTER_DUNGEON = 0x13BE;  public const ushort AS_REQUEST_ENTER_DUNGEON = 0x13BF;
    public const ushort SA_RESPONSE_ENTER_DUNGEON = 0x13C0; public const ushort AS_RESPONSE_ENTER_DUNGEON = 0x13C1;
    public const uint WorldId = 0x0AF0; // planet 2800 (DAT_140e2d020), first u32 of every PDId

    // --- DungeonEnterContext field offsets, in 0x13BE/0x13C0 PAYLOAD terms ---
    // The context is 176 bytes at payload 8 (frame 0x0E). Offsets recovered from
    // Handler_SA_RESPONSE_ENTER_DUNGEON in ArbiterServer.exe.c, cross-checked against the
    // padding gaps already documented for DungeonReplyPaddingBytes below (payload 51 is the
    // pad after the u8 at 50; payload 153..155 the pad after the u8 at 152), and against
    // D:\packetlogs\arb_world_2026-09-13T11-33-30-680Z.log seq 1137 (0x13BE) / 1159 (0x13C0).
    public const int DungeonCtxOffset = 8;                 // ctx+0
    public const int DungeonCtxDungeonId = 8;              // ctx+0    9827 in the capture
    public const int DungeonCtxWorldId = 28;               // ctx+20   0x0AF0
    public const int DungeonCtxPlayerId = 32;              // ctx+24   UserDbId
    public const int DungeonCtxEnterX = 36;                // ctx+28   where the player stood
    public const int DungeonCtxSetReturnPoint = 50;        // ctx+42   u8, "commit the return point"
    public const int DungeonCtxReturnX = 52;               // ctx+44   float
    public const int DungeonCtxReturnY = 56;               // ctx+48   float
    public const int DungeonCtxReturnZ = 60;               // ctx+52   float
    public const int DungeonCtxReturnZone = 64;            // ctx+56   u32 continent
    public const int DungeonCtxInstancePdId = 148;         // ctx+140  filled in by the 0x13C0 reply
    public const int DungeonCtxSuccess = 152;              // ctx+144  u8, 1 on the 0x13C0 reply

    // --- Enter-world failure + retry (T21) ---
    //
    // SA_ENTER_WORLD_FAIL (0x138D, 38 B fixed). Field names from the Arbiter's own packet
    // dumper for "SA_ENTER_WORLD_FAIL"; the reader is Handler_SA_ENTER_WORLD_FAIL, which
    // requires frame > 0x25 and takes ContinuousDungeonId from frame 0x1e and FailReason from
    // frame 0x22.  Ground truth: arb_world_2026-09-13T11-33-30-680Z.log seq 836.
    //   [0]  u64 ArbiterClient   (echo of AS_ENTER_WORLD [16])
    //   [8]  u64 ArbiterUser     (echo of AS_ENTER_WORLD [24] - our gameId)
    //   [16] u32 Ticket          (echo of AS_ENTER_WORLD [80] - our tunnel key)
    //   [20] u32 LastIndex
    //   [24] u32 ContinuousDungeonId  (the continent World refused: 9827)
    //   [28] u32 FailReason           (2 in the capture)
    //
    // The handler schedules a User::DoTimerJob 3000 ms out; the job is
    // User::EnterWorldFail(reason, continuousDungeonId), which
    //   - gives up (LeaveWorldType 3, disconnect) unless the reason is 1, 2 or 3;
    //   - for reason 1 or 2 sets ChannelInstanceId = -1 and takes continent + position from the
    //     continent fallback table, THEN overrides both from the stored SysReturnLoc when
    //     `0 < User+0x1a8`;
    //   - re-sends AS_ENTER_WORLD with a fresh Ticket and ContinuousDungeonId = the refused
    //     continent.
    // seq 836 -> 841/842/843 is exactly 3.014 s apart, which is that timer.
    public const ushort SA_ENTER_WORLD_FAIL = 0x138D;
    public const ushort AS_ENTER_WORLD = 0x138E;
    /// <summary>T161b. World has the user (Handler_SA_ENTER_WORLD -> User::EnterWorldEnd): a leave
    /// held while it loaded goes out now (LeaveGate). The user is the u64 at payload 16.</summary>
    public const ushort SA_ENTER_WORLD = 0x138C;
    /// <summary>T161b. World's <c>/@goto name</c>: [u32 nameOffset (frame)][u64 requester gameId][name].
    /// Answered with the T155 "go to" ask (GmAdminTool.OnSaCharLoc).</summary>
    public const ushort SA_CHAR_LOC = 0x1443;
    public const int EnterWorldFailMinPayload = 0x26 - 6;      // 32
    public const int EnterWorldFailArbiterClientOffset = 0;
    public const int EnterWorldFailArbiterUserOffset = 8;
    public const int EnterWorldFailTicketOffset = 16;
    public const int EnterWorldFailLastIndexOffset = 20;
    public const int EnterWorldFailDungeonIdOffset = 24;
    public const int EnterWorldFailReasonOffset = 28;
    /// <summary>The real Arbiter's retry delay (FUN_14003e660(..., job, 3000, 0)).</summary>
    public const int EnterWorldRetryDelayMs = 3000;

    // AS_ENTER_WORLD (0x138E, 183-byte payload) field offsets, from the Arbiter's own packet
    // dumper for "AS_ENTER_WORLD" (frame offsets 0x16..0xad converted to payload):
    //   [16] ArbiterClient u64   [24] ArbiterUser u64      [32] UserDbId
    //   [36] AccountDbId u64     [44] SessionKey           [48] ContinentId
    //   [52] ChannelInstanceId   [56..67] x/y/z            [68] EnterWorldType
    //   [72] Direction           [76] VisibleRange         [80] Ticket
    //   [84] GameId u64          [92] PcBangUser u8        [93] NewMemberAccount u8
    //   [94] PartyId u64         [102] IsSysParty u8       [103] SubscriptionFeeType
    //   [107] AccountRestrictionLevel                      [111] AdminLevel
    //   [115] TutorialUser u8    [116] LeaveParty u8       [117] BotPenalty
    //   [121] SharedDBTCat u64   [129] CharacterSocketNum  [133] MaxSharedIncCharSocketCount
    //   [137] MaxSharedIncWarePageCount                    [141] MaxSharedIncStyleWarePageCount
    //   [145] SharedIncCharSocketCount                     [149] SharedIncWarePageCount
    //   [153] SharedIncStyleWarePageCount                  [157] ContinuousDungeonId
    //   [161] ExCrestPoint       [165] UseOptionalItem u8  [166] MentoringReturnUser u8
    //   [167..182] EtcData
    // Only the six the retry touches are named here; Handlers/WorldEntry.cs owns the builder.
    public const int EnterWorldPayloadSize = 183;
    public const int EnterWorldContinentIdOffset = 48;
    public const int EnterWorldChannelInstanceIdOffset = 52;
    public const int EnterWorldPositionOffset = 56;
    public const int EnterWorldTicketOffset = 80;
    /// <summary>
    /// T46. The GM level World reads out of AS_ENTER_WORLD. The Arbiter takes it from
    /// <c>User+0x3b98</c> (the field <c>set_admin_level</c> writes and every <c>/@</c> gate tests
    /// with <c>level &lt; 1</c>), passes it as param_22 to the 0x138E writer FUN_140360710
    /// (Arb_part_027.c:12906), and it lands at frame 0x75 = payload 111.
    ///
    /// <para>World's side of the chain: its generated field-binding table FUN_140496c80
    /// (WorldServer.exe.c:847798) binds the name <c>adminLevel</c> to <c>User+0xA474</c>, and that
    /// is the value in <c>SpawnComplete [%s] %s(%d) AdminLevel[%d]</c> (:935500) and in
    /// <c>=== AdminLevel[%d], Status[%d], ...</c> (:866788). <b>Those two logs are the only reads
    /// in the whole binary</b> - World stores the level and prints it but never gates on it, so a
    /// wrong value here is a wrong log line, not a refused command.</para>
    /// </summary>
    public const int EnterWorldAdminLevelOffset = 111;
    public const int EnterWorldContinuousDungeonIdOffset = 157;

    // AS_CACHE_DUNGEON_COOL_TIME_TO_WORLD (0x148D). Writer:
    // SendToSession<PKT_AS_CACHE_DUNGEON_COOL_TIME_TO_WORLD_WRITE,int,int,const unsigned char*>
    //   [0] u32 blobOffset (frame-relative, always 18)   [4] u32 blobLength   [8] u32 playerId
    //   [12..] the blob
    // The blob is one 52-byte DungeonCoolTimeElem - the same record DBS_LOAD_DUNGEON_COOL_TIME
    // (0x2868) carries in its first list, byte for byte (capture seq 885 == seq 842's blob):
    //   +0  u32 dungeonId            +4  u32 channelInstanceId
    //   +8  16-byte DateTime         +24 16-byte DateTime
    //   +40 u32 / +44 u32 / +48 u32  counters
    // The two 16-byte DateTimes decode as u16 year, month, day, hour, minute, second + 4 pad
    // on the two values seen (1970-01-01 00:00 = "never", 2026-09-12 07:00), which is INFERRED
    // from those two points, not from a decompiled writer - so they are handled as opaque
    // blobs here and DungeonCoolTimeNever is the literal "never" bytes.
    public const ushort AS_CACHE_DUNGEON_COOL_TIME_TO_WORLD = 0x148D;

    // --- T25: the write side of the dungeon cool time. All four are one-way (World -> us, no
    // reply): none of their Arbiter handlers has a SendToSession. They are in the allow-list so
    // TryHandle can persist them, AND still in WorldReplayTable.OneWayFromWorld, which is a
    // different job - that set is used while BUILDING the replay table from the tap log, to stop
    // a one-way frame being paired with the next A->W frame as if it were its reply.
    //
    //   0x13B6 SA_UPDATE_DUNGEON_COOLTIME
    //     [0] u32 listOff=22  [4] u32 listLen=52  [8] u64 ArbiterUser  [16] CoolTimeElem (52 B)
    //     Handler: memcpy the element, DungeonInfoManager::UpdateCoolTime. cap_newchar seq 2907.
    //   0x13B7 SA_UPDATE_DUNGEON_CLEAR_COUNT
    //     [0] u64 ArbiterUser  [8] u32 ContinentId  [12] u32 ClearCount
    //     Handler: DungeonInfoManager::UpdateDungeonClearCount -> dbo.spUpdateDungeonClearCount.
    //   0x13B8 SA_UPDATE_DUNGEON_UI_HISTORY
    //     [0] u64 ArbiterUser  + a vector<int> DungeonIdList. Never seen on the wire.
    //   0x13BD SA_DELETE_DUNGEON_COOLTIME
    //     [0] u64 ArbiterUser  [8] u32 ContinentId
    public const ushort SA_UPDATE_DUNGEON_COOLTIME = 0x13B6;
    public const ushort SA_UPDATE_DUNGEON_CLEAR_COUNT = 0x13B7;
    public const ushort SA_UPDATE_DUNGEON_UI_HISTORY = 0x13B8;
    public const ushort SA_DELETE_DUNGEON_COOLTIME = 0x13BD;
    /// <summary>0x13B6: the element starts at payload 16 (frame 22).</summary>
    public const int DungeonCoolTimeUpdateHeader = 16;
    /// <summary>0x13B7/0x13BD: the continent id, right after the u64 ArbiterUser.</summary>
    public const int DungeonContinentIdOffset = 8;
    /// <summary>0x13B7: the clear count.</summary>
    public const int DungeonClearCountOffset = 12;
    /// <summary>DungeonCoolTimeElem+0.</summary>
    public const int DungeonCoolTimeDungeonIdOffset = 0;
    /// <summary>DBS_LOAD_DUNGEON_COOL_TIME (0x2868): three [offset][length] pairs, ok, reqId.</summary>
    public const int DungeonCoolTimeReplyHeader = 29;
    public const int CacheDungeonCoolTimeHeader = 12;
    public const int DungeonCoolTimeRecordSize = 52;
    public const int DungeonCoolTimeDateSize = 16;
    /// <summary>The 1970-01-01 00:00 DateTime the capture uses for "no cool time recorded".</summary>
    public static readonly byte[] DungeonCoolTimeNever =
    {
        0xB2, 0x07, 0x01, 0x00, 0x01, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
    };
    // Servant handlers: request [u64 gameId][u32 reqId][u32 playerId], reqId at payload[8]
    public const ushort SA_LOAD_SERVANT_DATA = 0x1539;             // -> 0x153A
    public const ushort SA_LOAD_SERVANT_ADVENTURE_DATA = 0x153B;   // -> 0x153C
    public const ushort SA_LOAD_SERVANT_STORAGE_DATA = 0x1537;     // -> 0x1538
    public const ushort SA_LOAD_SERVANT_AUTO_POTION_DATA = 0x152F; // -> 0x1530
    public const ushort SA_LOAD_SERVANT_AUTO_FEED_DATA = 0x1533;   // -> 0x1534
    public const ushort SA_PET_LOAD = 0x1415;                      // -> 0x1416
    // Extra point (SDB shape: [u32 reqId][u32 playerId])
    public const ushort SA_LOAD_EXTRAPOINT_DATA = 0x1554;          // -> 0x1555

    // --- Other login-time handlers with simple ack patterns ---
    public const ushort SDB_LOAD_QUEST_PROGRESS = 0x2902;       // -> 0x2903: [u32 reqId][u32 pid][u32 0][u32 0][u8 0]
    public const ushort SDB_LOAD_ACHIEVE_LIST = 0x2981;          // -> 0x2982: [u64 0][u32 reqId][u8 ok]
    public const ushort SDB_LOAD_WORLD_EVENT = 0x27B3;           // legacy alias: UPDATE_DAILY_LIMIT_EP_EXP
    // MISNAMED, kept as an alias because the live path and several tests use it: 0x2910 is
    // SDB_UPDATE_FATIGABILITY_POINT, not a friend-info load. WorldServer's own opcode table says
    // so, and the payload is a fatigue delta. Prefer SDB_FATIGABILITY_UPDATE in new code. T26.
    public const ushort SDB_LOAD_FRIEND_INFO = 0x2910;           // -> 0x2911: [u8 ok][u32 reqId] (5B)

    // --- Post-spawn per-user DB items (lobby_tap.log 02:52:07, right after SpawnComplete) ---
    // Real order: 0x2736 -> 0x2737, 0x27CB x2 -> 0x27CC, 0x2930 -> 0x2931, 0x27B3 -> 0x27B4.
    // 0x2736 has no replay entry (its captured 0x2737 was attributed to 0x15AE), so it was
    // never answered with a live id and head-blocked the user from spawn onward.
    public const ushort SDB_END_START_QUEST_LIST = 0x2736; public const ushort DBS_END_START_QUEST_LIST = 0x2737; // req [u32 reqId]; rsp [reqId][ok=1]

    // T142. SDB_SET_TASK_SHOW_TOGGLE (0x2734) -> DBS_SET_TASK_SHOW_TOGGLE (0x2735). THE LEVEL
    // WEDGE: it arrives exactly once, at the END of the quest burst a level jump kicks off
    // (arbiter-2026-09-20.log 14:31:11, the last W->A frame of that burst), it was in no table,
    // so WorldBridge fell through to the replay and found nothing. World's per-user DB queue
    // then waits for 0x2735 forever and every later command for that user no-ops - which is
    // exactly "perfect_level applies the LEVEL but the gear grant does not land".
    //
    // BYTE-EXACT against the real Arbiter: cap_social4.log records 3195-3238, twenty-two
    // request/reply pairs at 01:25:54.
    //   W->A 23 B  17 00 00 00 34 27 | A2 03 00 00 | 01 00 00 00 | 01 00 00 00 | 00 | 00 00 00 00
    //   A->W 19 B  13 00 00 00 35 27 | A2 03 00 00 | 01 00 00 00 | 01 00 00 00 | 01
    // request payload 17 B = [u32 reqId][i32 1][i32 taskId][u8 toggle][i32 0]
    // reply   payload 13 B = the request's first TWELVE bytes then 0x01.
    // The last byte is an OK flag, not the toggle echoed: record 3195 carries toggle 00 and is
    // answered 01, record 3233 carries 01 and is answered 01. (Handler_DBS_SET_TASK_SHOW_TOGGLE,
    // WorldServer.exe.c:3022585, guards frame >= 0x13 and reads that byte at frame+0x12 - the
    // decompile alone would have had us echo the toggle, which the capture disproves.)
    public const ushort SDB_SET_TASK_SHOW_TOGGLE = 0x2734; public const ushort DBS_SET_TASK_SHOW_TOGGLE = 0x2735;
    /// <summary>Payload of DBS_SET_TASK_SHOW_TOGGLE: 12 echoed bytes then the ok flag.</summary>
    public const int SetTaskShowToggleReplySize = 13;

    /// <summary>
    /// DBS_SET_TASK_SHOW_TOGGLE (0x2735) - the request's first twelve bytes and <c>01</c>.
    /// A request too short to fill them is answered zero-padded rather than not at all: an
    /// unanswered DB request is the wedge this exists to prevent.
    /// </summary>
    public static byte[] BuildSetTaskShowToggleReply(byte[] request)
    {
        var r = new byte[SetTaskShowToggleReplySize];
        if (request != null)
            Array.Copy(request, 0, r, 0, Math.Min(request.Length, SetTaskShowToggleReplySize - 1));
        r[SetTaskShowToggleReplySize - 1] = 1;
        return r;
    }
    // T148. 0x2930 is World's LoadHoldCharacterStatus, sent for EVERY character at spawn:
    // [reqId][u64 User+0xAA00][playerId]. Its reply is [reqId][u8 ok][u8 held]; World's
    // Handler_DBS_UPDATE_HOLD_CHARACTER_STATUS passes payload[5] to SetRecvData(bool), which
    // sets the hold flag (User+0xA610 -> +0x175, it freezes C_PLAYER_LOCATION) and sends the
    // client S_ADMIN_HOLD_CHARACTER. The real Arbiter answers held = 0 every time, GM or not
    // (cap_social4 x7, cap_final x16) except an active T180 panel hold (cap_final2b 51509).
    //
    // SDB_USER_VAPORIZED (0x282D) is World's User::SetVaporized telling the DB: [u32 playerId]
    // [u8 vaporized]. No DBS_ twin (0x282E is DBS_SIMULATE_ITEM_TOOLTIP), so fire-and-forget -
    // and nothing reads it back: World's vaporized flag is written ONLY by SetVaporized, which
    // User::EnterWorld calls with 1 when World's admin level is > 0. It is not persisted here.
    // It is used for one thing: it is World telling us the GM's real visibility, so
    // ArbiterClientHandlers.IsGmInvisible follows World instead of guessing.
    public const ushort SDB_USER_VAPORIZED = 0x282D;
    public const int UserVaporizedPayloadSize = 5;

    /// <summary>SDB_USER_VAPORIZED's two fields, or false on a short payload.</summary>
    public static bool TryReadUserVaporized(byte[]? payload, out int playerId, out bool vaporized)
    {
        playerId = 0; vaporized = false;
        if (payload == null || payload.Length < UserVaporizedPayloadSize) return false;
        playerId = BitConverter.ToInt32(payload, 0);
        vaporized = payload[4] != 0;
        return true;
    }

    /// <summary>World's report of a vaporize change: track it, answer nothing, store nothing.</summary>
    public static void OnUserVaporized(byte[] payload, ILogger? log = null)
    {
        if (!TryReadUserVaporized(payload, out int playerId, out bool vaporized))
        {
            log?.LogWarning("SDB_USER_VAPORIZED: {Len} B payload, want {Want} - ignored",
                payload?.Length ?? 0, UserVaporizedPayloadSize);
            return;
        }
        Handlers.ArbiterClientHandlers.SetGmInvisible(playerId, vaporized);
        log?.LogInformation("SDB_USER_VAPORIZED: player {Id} is {State} on World", playerId,
            vaporized ? "vaporized" : "visible");
    }

    public const ushort SDB_LOAD_2930 = 0x2930;              public const ushort DBS_LOAD_2931 = 0x2931;          // req [reqId][u64 gameId][pid]; rsp 82 00 00 00 01 00

    // --- Remaining login-time SDB_* (programmatic builders) ---
    public const ushort SDB_LOAD_2867 = 0x2867;            // -> 0x2868: three empty lists + [ok][reqId]
    // SDB_LOAD_DUNGEON_PHASE_LEVEL (0x2869). Handler_SDB_LOAD_DUNGEON_PHASE_LEVEL
    // (ArbiterServer.exe.c FUN_14074df40) calls DungeonInfoManager::GetDungeonPhaseInfoList()
    // (FUN_1407168f0) on user+0x6140, which calls CheckAndResetDungeonPhaseUser(DateTime::Now)
    // (FUN_14070d430). When the stored last-reset time is older than the daily reset boundary
    // that clears the user's phase list, stores the new reset time at mgr+0x70, and BROADCASTS
    // AS_REQUEST_DUNGEON_PHASE_USER_RESET (0x15E0) to every registered World session. The
    // handler then reads that same freshly-written reset time back (mgr+0x61b0) and puts it in
    // the 0x286A trailer -- which is why both carry the identical u64 in the capture.
    //   lobby_tap.log 02:51:10.705-.709, packets 172/173/174:
    //     172 W->A 0x2869  15 00 00 00  01 00 00 00
    //     173 A->W 0x15E0  01 00 00 00  00 00 00 00  9e 0f a6 6a 00 00 00 00
    //     174 A->W 0x286A  1b 00 00 00  00 00 00 00  01  15 00 00 00  9e 0f a6 6a 00 00 00 00
    //   0x6AA60F9E = 1789267870 = 2026-09-13T02:51:10Z, i.e. plain unix seconds, == capture time.
    // We keep no dungeon-phase state, so every 0x2869 is a reset from World's point of view and
    // the push always goes out.
    public const ushort SDB_LOAD_2869 = 0x2869;            // -> 0x15E0 push + 0x286A: empty list + [ok][reqId] + timestamp
    // World's Handler_AS_REQUEST_DUNGEON_PHASE_USER_RESET (WorldServer.exe.c FUN_1410...,
    // scope tracer at line 2995424) requires frame length >= 0x16 and reads
    //   [6] u32 playerId   -> user lookup; unknown user = no-op
    //   [10] u32 continentId -> 0 resets every continent, non-zero resets just that one
    //   [14] u64 resetTime -> stored as the user's dungeon-phase reset time
    // It sends no reply, so this is a fire-and-forget push and not a DLM item.
    public const ushort AS_REQUEST_DUNGEON_PHASE_USER_RESET = 0x15E0;
    public const ushort DBS_LOAD_DUNGEON_PHASE_LEVEL = 0x286A;
    public const ushort SDB_LOAD_2900 = 0x2900;            // -> 0x2901: two empty lists + [reqId][ok]
    public const ushort SDB_LOAD_28B7 = 0x28B7;            // -> 0x28B6: [ok][reqId][u32 0][u64 -1] (reply is op-1!)
    public const ushort SDB_LOAD_REFER_A_FRIEND = 0x28B0;  // -> 0x28B1: two empty lists + [ok][reqId] + 20 zeros

    // --- T69: the two refer-a-friend loads, promoted out of the replay table ---
    // Both carry a DlmId and both are per-login, so a replayed constant hands World the DlmId of
    // whoever logged in when the tap was recorded and the live user's queue never drains. Pinned
    // to cap_social2.log seq 124 -> 125 and 126 -> 127 (and again at 699/701 for the second
    // character, which is what proves the DlmId and the UserDbId are the only things that move).
    //
    // SDB_LOAD_REFER_A_FRIEND_LIST (0x28B0), dumper FUN_... Arb_part_017.c:10497, guard 0x0d:
    //   frame [06] i32 DlmId   [0A] i32 UserDbId                        min frame 0x0E
    // DBS_LOAD_REFER_A_FRIEND_LIST (0x28B1), Arb_part_015.c:10627, guard 0x2e, min frame 0x2F:
    //   payload [00] RafRefererUserDbIdList off  [04] bytes
    //           [08] RafRefereeUserDbIdList off  [12] bytes
    //           [16] u8 Success  [17] i32 DlmId  [21] i64 RefererAccountDbId
    //           [29] i32 RefererUserDbId  [33] i64 ExpireDate
    // Both lists are empty, and both empty lists use the offset = FRAME LENGTH convention.
    public const ushort DBS_LOAD_REFER_A_FRIEND = 0x28B1;
    public const int ReferAFriendReplySize = 41;           // frame 47

    // SDB_LOAD_INVITE_FRIEND (0x28B7), Arb_part_017.c:10119, guard 0x0d: same request shape.
    // DBS_LOAD_INVITE_FRIEND (0x28B6 - the reply opcode is REQUEST MINUS ONE), Arb_part_015.c:9836,
    // guard 0x16, min frame 0x17:
    //   payload [00] u8 Success  [01] i32 DlmId  [05] i32 InviteFriendDbId  [09] i64 AddInviteTime
    // The capture answers Success 1, InviteFriendDbId 0 and AddInviteTime -1 (never invited).
    public const ushort DBS_LOAD_28B6 = 0x28B6;
    public const int InviteFriendReplySize = 17;           // frame 23

    /// <summary>DBS_LOAD_REFER_A_FRIEND_LIST (0x28B1): nobody referred anybody.</summary>
    public static byte[] BuildDbsReferAFriendList(uint dlmId)
    {
        var p = new byte[ReferAFriendReplySize];
        uint frameLen = (uint)(6 + ReferAFriendReplySize);
        BitConverter.GetBytes(frameLen).CopyTo(p, 0);
        BitConverter.GetBytes(frameLen).CopyTo(p, 8);
        p[16] = 1;
        BitConverter.GetBytes(dlmId).CopyTo(p, 17);
        return p;
    }

    /// <summary>DBS_LOAD_INVITE_FRIEND (0x28B6): no invite on file, AddInviteTime = -1.</summary>
    public static byte[] BuildDbsInviteFriend(uint dlmId)
    {
        var p = new byte[InviteFriendReplySize];
        p[0] = 1;
        BitConverter.GetBytes(dlmId).CopyTo(p, 1);
        BitConverter.GetBytes(-1L).CopyTo(p, 9);
        return p;
    }
    public const ushort SDB_LOAD_2975 = 0x2975;            // -> 0x2976: 16 zeros + [reqId][ok] + 20 zeros
    public const ushort SDB_LOAD_2986 = 0x2986;            // -> 0x2987: 32 zeros + [reqId][ok] + trailing (16B request)
    public const ushort SDB_LOAD_290C = 0x290C;            // -> pushes 0x15B1 + 0x2847 + 0x1440 + 0x143E, then 0x290D

    // --- Remaining login-time SDB_* (static data from capture, reqId patched at runtime) ---
    // --- SDB_LOAD_TUTORIAL_SIMPLE_TIP (0x2872) -> DBS_LOAD_TUTORIAL_SIMPLE_TIP (0x2873), T22 ---
    // Fed by ADD (0x286E, count += 1) and DONT_REPEAT (0x2870, count += supplied delta).
    //   req  [0] u32 reqId  [4] u32 playerId                       (handler needs frame >= 0x0e)
    //   rsp  [0] u32 listOff=19 [4] u32 listLen [8] u32 reqId [12] u8 ok=1, then 8 B per tip
    //   tip  [i32 tipId][i32 popupCount], ascending tipId (Arb_part_028.c:19197).
    // cap_2man_b 17469 / 17491 and cap_final2b 34423 / 33788 pin six/seven entries, all count 1.
    public const ushort SDB_TUTORIAL_SIMPLE_TIP = 0x2872;   // -> 0x2873 (45B, reqId@8)
    public const ushort DBS_TUTORIAL_SIMPLE_TIP = 0x2873;
    public const int TutorialTipReplyHeader = 13;
    public const int TutorialTipRecordSize = 8;
    /// <summary>0x286E: [0] reqId [4] userDbId [8] tipId.</summary>
    public const int TutorialTipIdOffset = 8;
    public const ushort SDB_REPUTATION_LIST = 0x288F;       // -> 0x2890 (65B, reqId@9)
    public const ushort SDB_LOAD_293A = 0x293A;             // -> 0x293B (46B, reqId@8)
    public const ushort SDB_FATIGABILITY_LIST = 0x2908;     // -> 0x2909 (45B, reqId@9)
    // --- SDB_INIT_SEREN_GUIDE_INFO (0x2942) -> DBS_INIT_SEREN_GUIDE_INFO (0x2943), T22 ---
    // Fed by SDB_UPDATE_SEREN_GUIDE_INFO (0x2944), one (type, id) pair per write.
    //   req  [0] u32 reqId  [4] u32 playerId
    //   rsp  [0] u32 listOff=23 [4] u32 listLen [8] u32 reqId [12] u32 playerId [16] u8 ok=1,
    //        then 8 B per row: [u32 serenType][u32 serenId]
    // The row set is fixed at <see cref="SerenGuideTypes"/> and a character that has never
    // written one gets an EMPTY list (cap_newchar seq 380), not six zero rows. Test wrote
    // (type 2, id 1804) and (type 4, id 36) in cap_newchar; his relog reply carries the six
    // rows with type 4 = 36 and everything else 0 - type 2 is simply not in the served set.
    public const ushort SDB_SEREN_GUIDE = 0x2942;           // -> 0x2943 (65B, reqId@8)
    public const ushort DBS_SEREN_GUIDE = 0x2943;
    public const int SerenGuideReplyHeader = 17;
    public const int SerenGuideRecordSize = 8;
    /// <summary>0x2944: [0] reqId [4] userDbId [8] serenType [12] serenId.</summary>
    public const int SerenGuideTypeOffset = 8;
    public const int SerenGuideIdOffset = 12;
    /// <summary>
    /// The six seren-guide slots every 0x2943 with content carries, in wire order. Observed, not
    /// derived: both characters in the relog capture get exactly these, and no other type has
    /// ever appeared in a reply (Test's stored type 2 does not).
    /// </summary>
    public static readonly int[] SerenGuideTypes = { 4, 13, 14, 15, 16, 17 };
    public const ushort SDB_EP_PERK = 0x27B9;               // -> 0x27BA (113B, reqId@8)
    // --- Quests (T17). Layouts and the capture evidence: status/QUEST-DESIGN.md ---
    //
    // SDB_LOAD_QUEST_LIST (0x272C) -> DBS_LOAD_QUEST_LIST (0x272D)
    //   req  [0] u32 reqId  [4] u32 playerId
    //   rsp  53-byte header: six [u32 offset][u32 length] pairs, then [48] u8 ok, [49] u32 reqId
    //        list 0  vector<QuestData>                 80 B each   <- in-progress quests
    //        list 1  vector<QuestData>                 80 B each   <- always empty in every capture
    //        list 2  vector<int>                        4 B each   <- COMPLETED quest ids (T21)
    //        list 3  vector<DailyQuestSeed>            68 B each   <- GLOBAL, kept empty on purpose:
    //                World then generates the seeds itself and sends the 17 0x2899 items we answer
    //        list 4  vector<DailyQuestExCompleteCount>
    //        list 5  a fixed 20-byte [u32][DateTime] trailer
    //   Offsets are frame-relative, so an empty reply has every offset at 59 (= 6 + 53).
    //   Ground truth for the empty case: cap_newchar.log seq 342, a 73-byte payload.
    //   Ground truth for list 2: arb_world_2026-09-13T11-33-30-680Z.log seq 881, a 171-byte
    //   payload for "Test" (playerId 2) - list 0 one 80-byte record for quest 59904, list 2 the
    //   three ints 59901/59902/59903, everything else empty. T21.
    //
    // SDB_SET_QUEST_INFO (0x272E) -> DBS_SET_QUEST_INFO (0x272F)
    //   req  30-byte header: [0] u32 recordOff [4] u32 recordLen=80 [8] u32 atomOff
    //                        [12] u32 atomLen [16] u32 reqId [20] u32 sqlType [24] u32 playerId
    //                        [28] u8 [29] u8, then the 80-byte record, then reward atoms
    //   rsp  29-byte header: the same four slots, [16] reqId, [20] sqlType, [24] u8 ok,
    //                        [25] u32 questDbId, then the record and the atoms
    //   The reply is NOT a plain echo: on an INSERT write (sqlType 22) the Arbiter allocates the
    //   quest's DB id and returns it at [25]; World keeps it (DBStartQuestContext::SetQuestDbId)
    //   and puts it in record+0 on every later write. Every other sqlType returns 0 there.
    //   Reward atoms are the same 856-byte ItemTransactionAtom as 0x2768 and follow the same
    //   rule: an op-7 insert arriving with item id 0 gets a fresh one (capture seq 1804: 0 -> 13).
    //   Verified: all 28 request/response pairs in cap_newchar.log rebuild byte for byte.
    public const ushort SDB_SET_QUEST_INFO = 0x272E; public const ushort DBS_SET_QUEST_INFO = 0x272F;
    public const int QuestRecordSize = 80;
    public const int QuestSetRequestHeader = 30;
    public const int QuestSetReplyHeader = 29;
    /// <summary>sqlType 22 = INSERT: the only write that gets a questDbId back.</summary>
    public const uint QuestSqlTypeInsert = 22;
    public const int QuestRecordDbIdOffset = 0;
    public const int QuestRecordQuestIdOffset = 4;
    public const int QuestRecordStatusOffset = 8;
    public const int QuestRecordStepOffset = 12;

    public const int QuestListReplyHeader = 53;
    public const int QuestListOkOffset = 48;
    public const int QuestListReqIdOffset = 49;
    /// <summary>
    /// List 5 of 0x272D: [u32 0][16-byte DateTime]. The "never" (1970-01-01) form the real
    /// Arbiter sent for the brand-new character in cap_newchar.log seq 342. It is NOT a
    /// constant: the 2026-09-13 relog capture carries 2026-09-12 07:00 in both 0x272D replies,
    /// the same DateTime the 0x148D cool-time records carry, so it is a server-wide daily-reset
    /// stamp and not per character. We have nothing to put in it and World accepted "never" for
    /// a character that had not played, so "never" is the default; BuildDbs272D takes an
    /// override so the capture can be reproduced byte for byte.
    /// </summary>
    public static readonly byte[] QuestListTrailer =
    {
        0x00, 0x00, 0x00, 0x00, 0xB2, 0x07, 0x01, 0x00, 0x01, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
    };
    /// <summary>
    /// playerId 1 is "dob", whose 1377-byte reply is in DbProxyStaticData. Keep serving it until
    /// he has quest rows of his own, or his in-flight quest disappears from under him.
    /// </summary>
    public const int CapturedQuestPlayerId = 1;

    public const ushort SDB_QUEST_LIST = 0x272C;            // -> 0x272D
    public const ushort SDB_USER_ACHIEVEMENT = 0x27F8;      // -> 0x27F9 (1501B, reqId@304)
    public const ushort DBS_LOAD_USER_ACHIEVEMENT = 0x27F9; // reply to 0x27F8 (T22 builder)


    // =====================================================================
    // T15 - the per-user DB writes World sends DURING PLAY.
    //
    // Every one of these is a DLMItem serialised on the user's gameId: one unanswered or
    // mis-answered reply head-blocks that user's whole DB queue - the periodic blob save, the
    // logout saves and UserLeaveWorld itself - for the life of the World process
    // (status/HANDOFF.md section 1). Before T15 they were all "no replay for 0xNNNN".
    //
    // Ground truth: D:\packetlogs\cap_newchar.log (real ArbiterServer, new character "Test",
    // playerId 2, Island of Dawn, 05:49-05:53). The frames used by the tests are extracted into
    // data/cap_t15.bin (TSIS container, see data/cap_t15.md) so the tests do not need D:\packetlogs.
    // All offsets in the comments below are PAYLOAD-relative (the decompile's frame offset - 6).
    // =====================================================================

    // --- SDB_SET_QUEST_INFO (0x272E) -> DBS_SET_QUEST_INFO (0x272F) ---
    // ~30 per session: every quest accept, objective tick and completion. Three sizes in the
    // capture (116 / 972 / 3540 B frames) - the 80-byte quest record is always there, the tail
    // is a list of 856-byte ItemTransactionAtoms (quest reward items).
    //
    // Handler_SDB_SET_QUEST_INFO (Arb_part_064.c:3916, tracer at 3916) needs frame >= 0x24 and
    // reads, payload-relative:
    //   [0]  u32 recordOffset (frame-relative, 36)   [4]  u32 recordLength (80)
    //   [8]  u32 atomListOffset (frame-relative)     [12] u32 atomListLength (n * 856)
    //   [16] u32 reqId        (the DLM id)           [20] u32 sqlType
    //   [24] u32 playerId     [28] u8 flag  [29] u8 flag   -> 30-byte header
    // The reply writer is FUN_1406f4910 (Arb_part_060.c:11576): opcode 0x272f, four backpatch
    // slots, u32 reqId, u32 sqlType, u8 ok, u32 questDbId - a 29-byte header - then the 80-byte
    // record and the atoms. So the reply frame is the request frame MINUS ONE: the request's
    // u32 playerId + 2 flag bytes (6 bytes) are replaced by the u8 ok + u32 questDbId (5 bytes).
    //
    // World's Handler_DBS_SET_QUEST_INFO (WorldServer.exe.c:3022389) needs frame >= 0x23,
    // switches on the SAME sqlType, finds the DLM item by payload[16], takes ok from payload[24]
    // - and on sqlType 22 ONLY it calls DBStartQuestContext::SetQuestDbId(payload[25])
    // (FUN_140e7aa60, WorldServer.exe.c:2636646). That is the row id the Arbiter's INSERT
    // allocated; in the capture it is 2, 3, 4, 5 for the four sqlType-22 writes and 0 for every
    // other type. We keep no quest table yet, so DbProxyHandlers hands out its own monotonic
    // ids - they only have to be distinct within the process (see NextQuestDbId).
    //
    // The atoms follow the T13 rule exactly: an op-7 (insert) atom that arrives with item DB id 0
    // gets one allocated; everything else is echoed verbatim. Verified in the capture at
    // seq 1804 (atom op 7, id 0 -> 13) and 2364 (four atoms, ops 6/11/6/11, ids untouched).
    // (opcode constants SDB_SET_QUEST_INFO / DBS_SET_QUEST_INFO are declared in the T17 block above)
    /// <summary>0x272E header: two [offset][length] pairs, reqId, sqlType, playerId, 2 flag bytes.</summary>
    public const int QuestInfoRequestHeader = 30;
    /// <summary>0x272F header: the same two pairs, reqId, sqlType, ok, questDbId.</summary>
    public const int QuestInfoReplyHeader = 29;
    /// <summary>The quest record is a fixed 80-byte struct; the Arbiter always writes 0x50 bytes.</summary>
    public const int QuestInfoRecordSize = 0x50;
    /// <summary>
    /// sqlType 22 = INSERT: the only one where World reads the allocated quest row id back
    /// (DBStartQuestContext::SetQuestDbId). 23/24 are updates, 25-27 are deletes/cleanups.
    /// </summary>
    public const uint QuestSqlInsert = 22;

    // --- SDB_USER_LEARN_SKILL (0x278E) -> DBS_USER_LEARN_SKILL (0x278F) ---
    // cap_newchar.log seq 2750 -> 2751, 896 B -> 885 B. Sent when the character learns a skill
    // at a trainer; the request carries the skill id and ONE ItemTransactionAtom (the fee).
    //
    // Handler_SDB_USER_LEARN_SKILL (Arb_part_078.c:3384) needs frame >= 0x28 and reads:
    //   [0]  u32 atomListOffset (frame-relative, 40)  [4]  u32 atomListLength (856)
    //   [8]  u32 reqId   [12] u32 playerId   [16] u32 skillId
    //   [20] u8 learnAll flag   [21] u32 ...  [26] i32 ...  [30] i32 ...  -> 34-byte header
    // Reply writer FUN_1408e8230 (Arb_part_077.c:15265): opcode 0x278f, four backpatch slots,
    // u32 reqId, u8 ok, u8 hasSkillPeriodList, u8 wasAlreadyLearned - a 23-byte header - then
    // the atom list and a SkillPeriodData list (0x18 each, EMPTY in the capture).
    //
    // World's Handler_DBS_USER_LEARN_SKILL (WorldServer.exe.c:3027296) needs frame >= 0x1d,
    // finds the item by payload[16], takes ok from payload[20], and reads the SECOND list
    // (payload[8]/[12]) as the skill-period list - it never looks at the atoms. It stores
    // payload[21] and payload[22] on the context. Both are 0 in the capture (no skill-period
    // rows, and the learn was new), which is what we always send.
    //
    // So the echo rule is the same shape as T13's 0x2769: header + the request's atom list with
    // insert ids filled in, and an empty second list. The capture's one atom is op 9 with id 0,
    // which the T13 rule leaves alone - reply bytes are the request's atom verbatim.
    public const ushort SDB_USER_LEARN_SKILL = 0x278E; public const ushort DBS_USER_LEARN_SKILL = 0x278F;
    /// <summary>0x278E header: [offset][length] atom list, reqId, playerId, skillId, flags.</summary>
    public const int LearnSkillRequestHeader = 34;
    /// <summary>0x278F header: two [offset][length] pairs, reqId, ok, 2 flag bytes.</summary>
    public const int LearnSkillReplyHeader = 23;

    // --- SDB_ACCOMPLISH_USER_ACHIEVEMENT (0x2802) -> DBS_ACCOMPLISH_USER_ACHIEVEMENT (0x2803) ---
    // Two forms in the capture, and they are the SAME code path:
    //   seq 1369 -> 1370   46 B -> 43 B   achievement 5991, echoed back
    //   seq 2605 -> 2606   46 B -> 19 B   achievement 5991 AGAIN, empty list
    //   seq 2722 -> 2723   46 B -> 43 B   achievement 5992, echoed back
    // Handler_SDB_ACCOMPLISH_USER_ACHIEVEMENT (Arb_part_062.c:18927) needs frame >= 0x16 and
    // reads [0] u32 listOffset [4] u32 listLength [8] u32 reqId [12] u32 playerId, then
    // listLength/0x18 records of 24 bytes. For each record it calls User::AccomplishAchievement
    // and keeps ONLY the ones that returned true - the reply list is the achievements that were
    // NEWLY accomplished, which is why a repeat of 5991 comes back empty. ok is a hard-coded 1
    // in the writer either way, so both forms complete the DLM item.
    // World's Handler_DBS_ACCOMPLISH_USER_ACHIEVEMENT (WorldServer.exe.c:3008986) needs
    // frame >= 0x13 (the 19-byte short form is exactly the minimum), finds the item by
    // payload[8] and takes ok from payload[12].
    // We keep no achievement table, so every record looks new to us and we always echo. The
    // short form is reachable through the isNewlyAccomplished predicate, which is where an
    // achievement store would plug in.
    public const ushort SDB_ACCOMPLISH_USER_ACHIEVEMENT = 0x2802; public const ushort DBS_ACCOMPLISH_USER_ACHIEVEMENT = 0x2803;
    /// <summary>0x2802 header: [offset][length] record list, reqId, playerId.</summary>
    public const int AchievementRequestHeader = 16;
    /// <summary>0x2803 header: [offset][length], reqId, ok.</summary>
    public const int AchievementReplyHeader = 13;
    /// <summary>One accomplished-achievement record: [u32 achievementId][u32 0][7 x u16 date][u16 pad].</summary>
    public const int AchievementRecordSize = 0x18;
    /// <summary>The achievement id at record+0. The rest of the record is stored verbatim.</summary>
    public const int AchievementRecordIdOffset = 0;
    /// <summary>
    /// T203. record+4 - the field ACHIEVEMENTS.md section 1 called <c>[u32 0]</c> - is the
    /// sheet's <b>serverUnique</b>, copied there by World from <c>AchievementList.xml</c>
    /// (World decodes the string attribute to an int at <c>WorldServer.exe.c:364358</c>; the
    /// Arbiter reads the same attribute at <c>Arb_part_006.c:7670</c>). So the Arbiter never
    /// needs the sheet: World hands the flag over with every record.
    /// <list type="bullet">
    /// <item>0 - an ordinary achievement. Granted with no shared check at all.</item>
    /// <item>1 - server first. Exactly one character on the planet may ever hold it.</item>
    /// <item>2 / 3 - server first AND party-shared: once claimed, the party that claimed it may
    /// still be granted it (<c>CanAccomplishPartyAchievementNoLock</c>, Arb_part_056.c:16992).</item>
    /// </list>
    /// </summary>
    public const int AchievementRecordServerUniqueOffset = 4;
    /// <summary>The lowest <c>serverUnique</c> value that also shares the claim with a party.</summary>
    public const uint AchievementServerUniqueParty = 2;

    // --- SDB_LOAD_USER_ACHIEVEMENT (0x27F8) -> DBS_LOAD_USER_ACHIEVEMENT (0x27F9),
    //     fed by SDB_UPDATE_USER_ACHIEVEMENT (0x27FA) and SDB_ACCOMPLISH_USER_ACHIEVEMENT (0x2802).
    //     Full write-up and the capture evidence: status/ACHIEVEMENTS.md.
    //
    // Both messages are [u32 offset][u32 length] tables followed by the bodies, and the list
    // NAMES come from the Arbiter's own packet dumpers, which is what makes the mapping between
    // them provable rather than guessed:
    //
    //   0x27FA save: 35 pairs (payload 0..279), [280] u32 reqId, [284] u32 playerId, bodies at 288
    //   0x27F9 load: 38 pairs (payload 0..303), [304] u32 reqId, [308] u8 ok,       bodies at 309
    //
    // List 0 of both is `Data`, a fixed 1184-byte blob. The load has three lists the save does
    // not: 1 AchievementGrades and 16 BattleFieldRankList (empty in every capture) and 34
    // AccomplishedAchievementList, which comes from 0x2802 instead.
    public const int AchievementSaveListCount = 35;
    public const int AchievementSaveReqIdOffset = 280;
    public const int AchievementSavePlayerIdOffset = 284;
    public const int AchievementSaveHeader = 288;
    public const int AchievementLoadListCount = 38;
    public const int AchievementLoadReqIdOffset = 304;
    public const int AchievementLoadOkOffset = 308;
    public const int AchievementLoadHeader = 309;
    /// <summary>List 0 of both messages: the fixed-size opaque progress blob.</summary>
    public const int AchievementDataSize = 1184;
    /// <summary>Load list 34, the one 0x2802 feeds.</summary>
    public const int AchievementAccomplishedListIndex = 34;

    /// <summary>
    /// Save list index -> load list index, matched by the names the two packet dumpers print.
    /// Verified on the wire: the six non-empty lists of relog seq 1137 land byte for byte on the
    /// six non-empty lists of relog seq 883, including the two that are NOT a simple shift
    /// (save 23 AcquireCombatItemTypeList -> load 28 AcquireItemCombatTypeCountList, and
    /// save 31 CityWarRankList -> load 30).
    /// </summary>
    public static readonly int[] AchievementSaveToLoadList =
    {
        /*  0 Data                                */  0,
        /*  1 MonsterKillCountList                */  2,
        /*  2 MonsterKillByMyselfCountList        */  3,
        /*  3 MonsterKilledMeCountList            */  4,
        /*  4 PvpDoneCountList                    */  5,
        /*  5 PvpWinCountList                     */  6,
        /*  6 ItemLootCountList                   */  7,
        /*  7 ItemUseCountList                    */  8,
        /*  8 QuestClearCountByQidList            */  9,
        /*  9 SkillMonsterKillCountList           */ 10,
        /* 10 SkillUseCountList                   */ 11,
        /* 11 AccumulatedReputationPointList      */ 12,
        /* 12 DailyQuestCompletedCountList        */ 13,
        /* 13 EventMailReceivedInfoList           */ 14,
        /* 14 BattleFieldWinCountList             */ 15,
        /* 15 AbnormalityCounterList              */ 17,
        /* 16 BattleFieldWinContinuouslyCountList */ 18,
        /* 17 LootItemInContinentList             */ 19,
        /* 18 ItemUseInContinentList              */ 20,
        /* 19 KnockDownMonsterList                */ 21,
        /* 20 RearAttackMonsterList               */ 22,
        /* 21 CriticalAttackMonsterList           */ 23,
        /* 22 HealByItemSkillList                 */ 24,
        /* 23 AcquireCombatItemTypeList           */ 28,
        /* 24 SkillUserKillCountList              */ 25,
        /* 25 BattleFieldTopContribCountList      */ 26,
        /* 26 RunWorkObjectCountList              */ 27,
        /* 27 MountSkillUseCountList              */ 29,
        /* 28 NpcContactList                      */ 36,
        /* 29 ContentUserKillList                 */ 31,
        /* 30 ContentUserDeathList                */ 32,
        /* 31 CityWarRankList                     */ 30,
        /* 32 LeaderBoardRankList                 */ 33,
        /* 33 NoDieNpcKillList                    */ 35,
        /* 34 BattleFieldWinCountByHeroList       */ 37,
    };

    /// <summary>
    /// The 1184-byte Data blob the real Arbiter sends for a character that has never saved
    /// (cap_newchar.log seq 344). It is all zeros except two u16s, so it is built rather than
    /// carried as a data file. Both values look like Arbiter-side achievement-template counts;
    /// we send what the capture sent.
    /// </summary>
    public static byte[] FreshAchievementData()
    {
        var d = new byte[AchievementDataSize];
        BitConverter.GetBytes((ushort)64228).CopyTo(d, 1052);
        BitConverter.GetBytes((ushort)622).CopyTo(d, 1180);
        return d;
    }

    // --- SDB_UPDATE_REPUTATION_INFO (0x2891) -> DBS_UPDATE_REPUTATION_INFO (0x2892) ---
    // cap_newchar.log seq 413 -> 417, 78 B -> 11 B.
    // Handler_SDB_UPDATE_REPUTATION_INFO (Arb_part_064.c:13105) needs frame >= 0x1a and reads
    //   [0] u32 recordOffset  [4] u32 recordLength (52)  [8] u32 reqId  [12] u32 playerId
    //   [16] u32 op
    // op 1 -> ReputationList::Insert, op 2 or 4 -> ReputationList::Update; any other op leaves
    // ok = 0. The writer emits u8 ok FIRST, then u32 reqId - the ONLY reply in this batch with
    // that ordering (capture: 01 2B 00 00 00).
    public const ushort SDB_UPDATE_REPUTATION_INFO = 0x2891; public const ushort DBS_UPDATE_REPUTATION_INFO = 0x2892;
    public const ushort DBS_REPUTATION_LIST = 0x2890;
    /// <summary>0x2891: [0] recordOff [4] recordLen [8] reqId [12] ownerDbId [16] op, record at 20.</summary>
    public const int ReputationUpdateHeader = 20;
    public const int ReputationUpdateOpOffset = 16;
    /// <summary>0x2890: [0] listOff=19 [4] listLen [8] u8 Success [9] u32 DlmId.</summary>
    public const int ReputationReplyHeader = 13;
    /// <summary>ReputationData is 0x34 bytes; the id at +4 is the std::map key.</summary>
    public const int ReputationRecordSize = 0x34;
    public const int ReputationRecordIdOffset = 4;
    /// <summary>ReputationData+0: the owner, stamped by the Arbiter's DB loader, not by World.</summary>
    public const int ReputationRecordOwnerOffset = 0;
    /// <summary>ReputationData+32: see <see cref="ReputationLoaderResidue"/>.</summary>
    public const int ReputationRecordResidueOffset = 32;
    /// <summary>
    /// The value the real Arbiter leaves in ReputationData+32 on every load. It is NOT a field:
    /// ReputationDataManager::CacheReputationData binds eight output columns of
    /// <c>dbo.spLoadAllUserReputation</c> into its row buffer and the slot that ends up at +32
    /// (<c>(u32)local_80</c>) is never one of them, so what lands there is whatever the buffer
    /// held. Both captured replies carry 0x000107B2 because both came from the same code path
    /// on the first row; a second row would carry 0, since the loop zeroes the buffer between
    /// rows. We send the captured value for the same reason T13 clones the item record's
    /// uninitialised tail rather than synthesising it: it is what the real server sends, and
    /// World demonstrably does not read it.
    /// </summary>
    public const uint ReputationLoaderResidue = 0x000107B2;
    /// <summary>op 1 inserts; op 2 and 4 update. Anything else leaves ok = 0 and stores nothing.</summary>
    public static bool IsReputationWriteOp(uint op) => op == 1 || op == 2 || op == 4;

    // --- SDB_LOAD_FATIGABILITY_LIST (0x2908) -> DBS_LOAD_FATIGABILITY_LIST (0x2909),
    //     fed by SDB_UPDATE_FATIGABILITY_POINT (0x2910). T26.
    //
    //   0x2910 req [0] u32 DlmId [4] u32 UserDbId [8] u32 kind=1 [12] u32 DELTA [16] u8
    //          rsp 0x2911 [0] u8 ok=1 [1] u32 DlmId          (ok FIRST, like 0x2892)
    //   0x2909 rsp [0] u32 listOff=23 [4] u32 listLen=28 [8] u8 Success=1 [9] u32 DlmId
    //              [13] u32 AddtionalFatiguePoint, then ONE 28-byte element:
    //              [0] u32 kind=1  [4] u32 curPoint  [8] 16-B TIMESTAMP  [24] u32 (unexplained)
    //   World copies the 28 bytes into FatigabilityInfo verbatim (stride 0x1c in
    //   DBLoadFatigabilityContext::ExecuteCommit), so the element is opaque to it too.
    public const ushort SDB_FATIGABILITY_UPDATE = 0x2910;
    public const ushort DBS_FATIGABILITY_UPDATE = 0x2911;
    public const ushort DBS_FATIGABILITY_LIST = 0x2909;
    public const int FatigabilityReplyHeader = 17;
    public const int FatigabilityRecordSize = 28;
    public const int FatigabilityKind = 1;
    public const int FatigabilityUpdateKindOffset = 8;
    public const int FatigabilityUpdateDeltaOffset = 12;

    // ===================== T42: warehouse (status/MAIL-WAREHOUSE.md section 4) =====================
    // Numbers from data/dbproxy_opcodes.txt, each re-confirmed against the literal its sender
    // passes to the packet writer. Layouts, atom parsing and the reply builders are in
    // World/WarehouseHandlers.cs; only the opcodes live here, because
    // Dispatch_switch_and_the_allow_list_agree resolves `case NAME:` by reflecting over this class.
    //
    // NO CAPTURE CONTAINS A WAREHOUSE FRAME. Both A<->W taps are one login and nobody opened a
    // bank, so every offset comes from the Arbiter's PDL dumpers cross-checked against the
    // writers. Each handler's min-length guard lands exactly on the end of its last field, which
    // is what makes the offsets trustworthy without bytes on the wire.
    //
    // TRAP 1: SDB_INCREASE_WAREHOUSE_SIZE is answered with DBS_INCREASE_INVENTORY_SIZE (0x283E),
    //         NOT 0x2840. Handler_SDB_INCREASE_WAREHOUSE_SIZE (Arb_part_063.c:8588) and
    //         Handler_SDB_INCREASE_INVENTORY_SIZE (:8490) share the send helper FUN_1407ad2e0
    //         (Arb_part_066.c:17451), and that helper writes 0x283E. The two DBS layouts are
    //         byte-identical ([u8 Success][u32 DlmId]) so World's DLM matching still works.
    //         Replying 0x2840 would head-block the user. 0x2840 is declared and never sent.
    // TRAP 2: SDB_MOVE_WAREHOUSE_ITEM (0x2754) is a ten-line stub in the real Arbiter
    //         (FUN_1405b53c0, Arb_part_048.c:13050) that sends nothing. We match it: the opcode
    //         is in WorldReplayTable.OneWayFromWorld so it is never answered and never inherits
    //         somebody else's reply. It is NOT in the allow-list.
    public const ushort SDB_VIEW_WAREHOUSE = 0x274A;          public const ushort DBS_VIEW_WAREHOUSE = 0x274B;
    public const ushort SDB_STORE_WAREHOUSE = 0x274C;         public const ushort DBS_STORE_WAREHOUSE = 0x274D;
    public const ushort SDB_GET_WAREHOUSE = 0x274E;           public const ushort DBS_GET_WAREHOUSE = 0x274F;
    public const ushort SDB_MOVE_WAREHOUSE_ITEM = 0x2754;     // -> nothing (stub in the original)
    public const ushort DBS_MOVE_WAREHOUSE_ITEM_UNUSED = 0x2755;
    public const ushort SDB_CHANGE_WAREHOUSE_POS = 0x277F;    public const ushort DBS_CHANGE_WAREHOUSE_POS = 0x2780;
    public const ushort SDB_PAY_WAREHOUSE_COMMISION = 0x27C9; public const ushort DBS_PAY_WAREHOUSE_COMMISION = 0x27CA;
    public const ushort SDB_CLEAR_WAREHOUSE = 0x27E0;         public const ushort DBS_CLEAR_WAREHOUSE = 0x27E1;
    public const ushort SDB_WAREHOUSE_AUTO_SORT = 0x27E2;     public const ushort DBS_WAREHOUSE_AUTO_SORT = 0x27E3;
    public const ushort SDB_INCREASE_WAREHOUSE_SIZE = 0x283F;
    public const ushort DBS_INCREASE_INVENTORY_SIZE = 0x283E;
    public const ushort DBS_INCREASE_WAREHOUSE_SIZE_UNUSED = 0x2840;
    // T150b: the inventory twin, SDB_INCREASE_INVENTORY_SIZE (0x283D, 38 B, dumper guard 0x25):
    // ItemBinary ref@06, DlmId@0E, UserDbId@12, TabIndex@16, NewInvenSize@1A,
    // ExpandInvenCountDelta@1E, Reason@22 - payload 0, 8, 12, 16, 20, 24, 28. World sends it at
    // login when the character's level grants bag slots, and waits for 0x283E on the DLM queue.
    public const ushort SDB_INCREASE_INVENTORY_SIZE = 0x283D;
    public const int IncInvReqDlmId = 8, IncInvReqUserDbId = 12, IncInvReqTab = 16,
                     IncInvReqNewSize = 20, IncInvReqExpandDelta = 24, IncInvReqReason = 28,
                     IncInvReqFixed = 32;

    // T150b: the four generic acks with a live pair (DbAckTable). Opt-in, one case each.
    public const ushort SDB_GUILD_LEARN_PERK = 0x279A;        // -> 0x279B
    public const ushort SDB_CONDITIONAL_TELEPORT = 0x27C3;    // -> 0x27C4
    public const ushort SDB_ITEM_SIMPLE_ATOM = 0x27CD;        // -> 0x27CE
    public const ushort SDB_MAIN_MENU_COMMAND = 0x27DE;       // -> 0x27DF
    // T162: the level-jump family (DbAckTable, decompile-proven). The scroll's item goes; World
    // then levels the character itself and sends S_UPDATE_EXP_LEVEL.
    public const ushort SDB_INCREMENT_CHARACTER_LEVEL = 0x28D9;               // -> 0x28DA
    public const ushort SDB_INCREMENT_CHARACTER_LEVEL_JUMP = 0x28DB;          // -> 0x28DC
    public const ushort SDB_INCREMENT_CHARACTER_LEVEL_PERFECT_JUMP = 0x28DD;  // -> 0x28DE
    // T166: the enchanting family (DbAckTable + ItemEdits). SDB_ITEM_DECOMPOSE (0x275C) is left
    // out on purpose: dead on both sides (the Arbiter's handler returns without a reply, World
    // never builds it; DECOMPOSITION 0x2920 is the live one).
    public const ushort SDB_ITEM_EXTRACT = 0x275A, SDB_ITEM_ENCHANT = 0x276E, SDB_ITEM_ENCHANT_IDENTIFY = 0x2770,
                        SDB_ITEM_MERGE = 0x2774, SDB_ITEM_UNIDENTIFY = 0x28A1, SDB_ENCHANT_ITEM_BOOST = 0x28F4,
                        SDB_ITEM_DECOMPOSITION = 0x2920, SDB_ITEM_AWAKEN = 0x2932, SDB_ITEM_UNBIND = 0x2934,
                        SDB_EQUIPMENT_INHERITANCE = 0x295F;

    // ===================== T45: parcels (status/MAIL-WAREHOUSE.md section 3) =====================
    // The six W<->A pairs T42 left unimplemented. Layouts and reply builders are in
    // World/ParcelDbHandlers.cs; only the opcodes live here, because
    // Dispatch_switch_and_the_allow_list_agree resolves `case NAME:` by reflecting over this class.
    //
    // NO CAPTURE CONTAINS A PARCEL FRAME either - arb_world.log and cap_newchar.log were parsed
    // frame by frame for all twelve opcodes and neither has one. So before T45 nothing answered
    // SDB_LIST_PARCEL: not a handler, and not the replay table, which can only replay what it
    // captured. Opening the mailbox left World's per-user DLM queue holding an item that never
    // completes (status/HANDOFF.md section 1) and the client drew its own empty 12-row grid.
    //
    // SDB_RECV_PARCEL_EX shares the RECV path in the original (both reach the same
    // ParcelManager::RecvParcel), but the two replies have different layouts and different
    // opcodes, so they get separate cases here.
    public const ushort SDB_LIST_PARCEL = 0x2777;      public const ushort DBS_LIST_PARCEL = 0x2778;
    /// <summary>
    /// T61. SDB_LOAD_PREMIUM_SLOT_LEFT_COOLTIME (0x28BD) -&gt; DBS (0x28BE), a per-user login load
    /// that carries a DlmId. Request: <c>i32 DlmId@06, i64 ArbiterUser@0A, i64 OwnerDbId@12</c>,
    /// frame 26. Reply: <c>u32 listOff=19@06, u32 listBytes@0A, i32 DlmId@0E, u8 Success@12</c>
    /// then N x 16-byte records. cap_social.log seq 644 -&gt; 645 carries two of them.
    /// </summary>
    public const ushort SDB_LOAD_PREMIUM_SLOT_LEFT_COOLTIME = 0x28BD;
    public const ushort DBS_LOAD_PREMIUM_SLOT_LEFT_COOLTIME = 0x28BE;
    /// <summary>One premium-slot cooltime record: <c>i32 SlotId, i32 Index, i64 LeftCoolTime</c>.</summary>
    public const int PremiumSlotRecordSize = 16;
    /// <summary>Payload bytes before the list (frame 19).</summary>
    public const int PremiumSlotReplyHeader = 13;

    /// <summary>
    /// DBS_LOAD_PREMIUM_SLOT_LEFT_COOLTIME. <paramref name="records"/> is the concatenated
    /// 16-byte block; empty for a character with no premium slots, which is every character we
    /// have. The offset slot is written unconditionally, as every list writer in this protocol
    /// does.
    /// </summary>
    public static byte[] BuildDbsPremiumSlotCooltime(uint dlmId, byte[]? records = null, bool ok = true)
    {
        records ??= Array.Empty<byte>();
        var p = new byte[PremiumSlotReplyHeader + records.Length];
        BitConverter.GetBytes(6 + PremiumSlotReplyHeader).CopyTo(p, 0);   // frame-relative 19
        BitConverter.GetBytes(records.Length).CopyTo(p, 4);
        BitConverter.GetBytes(dlmId).CopyTo(p, 8);
        p[12] = (byte)(ok ? 1 : 0);
        records.CopyTo(p, PremiumSlotReplyHeader);
        return p;
    }
    public const ushort SDB_MAKE_PARCEL = 0x2779;      public const ushort DBS_MAKE_PARCEL = 0x277A;
    public const ushort SDB_RECV_PARCEL = 0x277B;      public const ushort DBS_RECV_PARCEL = 0x277C;
    public const ushort SDB_RECV_PARCEL_EX = 0x277D;   public const ushort DBS_RECV_PARCEL_EX = 0x277E;
    public const ushort SDB_RETURN_PARCEL = 0x2781;    public const ushort DBS_RETURN_PARCEL = 0x2782;
    public const ushort SDB_DELETE_PARCEL = 0x2811;    public const ushort DBS_DELETE_PARCEL = 0x2812;

    // ===================== T51: the guild boot load (status/GUILD-DESIGN.md section 4.3) ======
    // World asks once, at boot, with a ZERO-LENGTH SDB_INIT_GUILD and then waits for a
    // DBS_INIT_GUILD_DATA whose Success byte is 0. Until T51 that terminator came from the
    // replay table - i.e. from arb_world.log - which meant every boot re-sent the real
    // Arbiter's two bytes of uninitialised stack padding inside GuildData (0xB379 at blob
    // 0x024A; GUILD-DESIGN.md section 2.1). Answering it from the `guilds` table retires that
    // entry: TryHandle returns true before WorldBridge ever reaches the replay table.
    //
    // The frames themselves are built by World/GuildWiring.BuildInitGuildLoad; only the opcode
    // lives here, because Dispatch_switch_and_the_allow_list_agree resolves `case NAME:` by
    // reflecting over public const ushort fields on THIS class.
    public const ushort SDB_INIT_GUILD = 0x27CF;

    // ===================== T55: the broker DLM answers (status/BROKER-DESIGN.md) =============
    // Five of the seven SDB_TRADE_BROKER_* requests carry a DlmId, so each is a per-user
    // DLMItem: World waits for a reply carrying that same id before it lets the character's DB
    // queue move again (status/HANDOFF.md section 1). Nothing answered them before T55 and no
    // capture contains one, so the replay table could not cover either - opening the broker
    // window wedged the character for the life of the World process, exactly as opening the
    // mailbox did before T45.
    //
    // There is no listings table yet (that waits for a capture - BROKER-DESIGN.md section 7), so
    // every answer is the empty/refusal form: the ids echoed, both refs empty, Success = 0. That
    // is enough. World does not need the answer to be yes; it needs an answer with its own
    // DlmId in it.
    //
    // Layouts and builders are in World/BrokerPackets.cs; only the opcodes live here, because
    // Dispatch_switch_and_the_allow_list_agree resolves `case NAME:` by reflecting over public
    // const ushort fields on THIS class.
    public const ushort SDB_TRADE_BROKER_REGISTER_ITEM = 0x2817;
    public const ushort SDB_TRADE_BROKER_UNREGISTER_ITEM = 0x2819;
    public const ushort SDB_TRADE_BROKER_CALC_SOLD_ITEM = 0x281B;
    public const ushort SDB_TRADE_BROKER_CALC_BOUGHT_ITEM = 0x281D;
    public const ushort SDB_TRADE_BROKER_BUY_IT_NOW = 0x281F;

    // --- SDB_ADD_TUTORIAL_SIMPLE_TIP (0x286E) -> DBS_ADD_TUTORIAL_SIMPLE_TIP (0x286F) ---
    // cap_newchar.log seq 719/765/864/911, 18 B -> 11 B each.
    // Handler_SDB_ADD_TUTORIAL_SIMPLE_TIP (Arb_part_063.c:36) needs frame >= 0x12 and reads
    //   [0] u32 reqId  [4] u32 playerId  [8] u32 tipId
    // Reply: [u32 reqId][u8 ok] (capture: 51 00 00 00 01).
    public const ushort SDB_ADD_TUTORIAL_SIMPLE_TIP = 0x286E; public const ushort DBS_ADD_TUTORIAL_SIMPLE_TIP = 0x286F;
    public const ushort SDB_DONT_REPEAT_TUTORIAL_SIMPLE_TIP = 0x2870;
    public const ushort DBS_DONT_REPEAT_TUTORIAL_SIMPLE_TIP = 0x2871;

    // --- SDB_UPDATE_SEREN_GUIDE_INFO (0x2944) -> DBS_UPDATE_SEREN_GUIDE_INFO (0x2945) ---
    // cap_newchar.log seq 634 -> 635 and 2713 -> 2716, 22 B -> 15 B.
    // Handler_SDB_UPDATE_SEREN_GUIDE_INFO (Arb_part_064.c:13189) needs frame >= 0x16 and reads
    //   [0] u32 reqId  [4] u32 playerId  [8] u32 guideId  [12] u32 value
    // Reply: [u32 reqId][u32 playerId][u8 ok] (capture: 4B 00 00 00 02 00 00 00 01).
    // NOTE: the real Arbiter sends NOTHING when its update fails - which would head-block the
    // user. We always answer; ok = 1 matches every captured reply.
    public const ushort SDB_UPDATE_SEREN_GUIDE_INFO = 0x2944; public const ushort DBS_UPDATE_SEREN_GUIDE_INFO = 0x2945;

    // --- T88: the character rename, cap_final tap frames 6093/6094, 6118/6119, 6126/6127 ---
    // The rename is World-routed: the client sends C_REQUEST_USABLE_CHARACTER_NAME and
    // C_REQUEST_CHANGE_CHARACTER_NAME to World (cap_final_client4 1702/1703, 1721/1722,
    // 1726/1728), and World asks US. Offsets below are FRAME-relative in the decompile;
    // payload index = frame offset - 6.
    //
    //   SDB_ASK 0x2854  [u32 nameOff=18][u32 reqId][u32 charId][wchar name]   frame >= 0x12
    //   DBS_ASK 0x2855  [u32 reqId][u8 ok][u32 resultCode]                    payload 9
    //   SDB_DO  0x2856  [u32 nameOff][u32 blobOff][u32 blobLen][u32 reqId][u32 charId][wchar name][blob]
    //   DBS_DO  0x2857  [u32 blobOff=23][u32 blobLen][u32 reqId][u32 charId][u8 code][blob]
    //
    // <b>resultCode 0 means USABLE.</b> Tap 6094 refuses the three-letter "Dob" with ok=0 /
    // code=1 and tap 6119 accepts "dobb" with ok=1 / code=0, and the client frames carry the
    // same numbers the other way round (1703 = 01 00 00 00 for the refusal, 1722 = 00 00 00 00
    // for the acceptance). A naive bool reading of either packet inverts the answer.
    public const ushort SDB_ASK_CHANGE_CHAR_NAME = 0x2854; public const ushort DBS_ASK_CHANGE_CHAR_NAME = 0x2855;
    public const ushort SDB_DO_CHANGE_CHAR_NAME  = 0x2856; public const ushort DBS_DO_CHANGE_CHAR_NAME  = 0x2857;

    // --- T90: SA_UPDATE_RANK_USERNAME (0x161C), tap 6129 ---
    // The third leg of the rename. One-way: World tells us the ranking name changed and
    // expects nothing back. Payload [u32 nameOff=14][u32 charId][wchar name] - the same id and
    // name SDB_DO_CHANGE_CHAR_NAME just wrote, so applying it again is idempotent and makes
    // the rename survive a dropped 0x2856.
    public const ushort SA_UPDATE_RANK_USERNAME = 0x161C;

    // --- T104: SA_WATCH_MOVIE (0x155C) ---
    // One-way, and the write that was missing behind C_WATCHED_MOVIES: World tells us a
    // cinematic finished playing and the Arbiter files it on the ACCOUNT.
    //   _Handler_SA_WATCH_MOVIE (Arb_part_066.c:6455) guards on  0x11 < frameLength,  so the
    //   frame is at least 0x12 = 18 bytes, and takes the user by the u64 at frame +6.
    //   Handler_SA_WATCH_MOVIE (Arb_part_062.c:18768) reads the movie id at frame +0x0E and
    //   calls Account::InsertWatchedMovieWithLock on User+0x3f40.
    // Frame:  [u32 len][u16 0x155C][u64 arbiterUserId][u32 movieId]   - payload 12 bytes.
    // There is no AS_ partner: nothing is sent back.
    public const ushort SA_WATCH_MOVIE = 0x155C;
    /// <summary>Smallest legal SA_WATCH_MOVIE frame - the decompile's <c>0x11 &lt; len</c>.</summary>
    public const int WatchMovieMinFrame = 0x12;
    /// <summary>Where the movie id sits in the PAYLOAD (frame +0x0E, minus the 6-byte header).</summary>
    public const int WatchMovieIdOffset = 8;
    /// <summary>The smallest legal PAYLOAD: the frame guard minus <c>[u32 len][u16 opcode]</c>.</summary>
    public const int WatchMovieMinPayload = WatchMovieMinFrame - 6;

    // --- T90: SA_START_CHANGE_APPEARANCE (0x1498), tap 6145 ---
    // One-way, and the ONLY frame the appearance change puts on the World link. cap_final
    // client4 runs the whole flow - S_PREPARE (1748), S_RACE_CHANGE_RESTRICTION (1833),
    // S_START (1834), C_COMMIT_CHANGE_USER_APPEARANCE (1838), S_END (1839) - and none of it
    // reaches us. The new customize blob lands in the characters row through the ordinary
    // user save at the next world hand-off (tap 6232 0x27FA / 6246 0x27CB), not through any
    // appearance-specific write. So there is nothing here to persist; sealing the opcode only
    // keeps it off the replay table.
    public const ushort SA_START_CHANGE_APPEARANCE = 0x1498;

    // --- T90: SDB_GIVE_GUILD_MONEY_INCENTIVE (0x27A0) -> DBS (0x27A1) ---
    // cap_final tap 2500 -> 2501 (AS_UPDATE_GUILD_DATA 0x144E, 9134 B) -> 2504. The client
    // packet is C_REQUEST_GUILD_INCENTIVE (cap_final_gm_client 2269), so this is World-routed.
    // Handler_SDB_GIVE_GUILD_MONEY_INCENTIVE (Arb_part_063.c:7177) guards frame >= 0x16 and
    // reads reqId at frame 6, playerId at 10, guildId at 14 and the rate float at 18.
    //
    //   tap 2500  BB 00 00 00  EB 03 00 00  02 00 00 00  AE 47 E1 3D   (req 187, 1003, guild 2, 0.11f)
    //   tap 2504  BB 00 00 00  01
    //
    // Guild::GiveGuildMoneyIncentive (Arb_part_046.c:4489) gates on membership, a cooldown and
    // a rate cap, raising SMT 3880 / 3881 on the two failures. The payout itself is past the
    // part that could be read, and the capture shows the guild money AFTER (8898665) but not
    // before - so the amount is NOT modelled here. See status/CLIENT-REJECTS.md section 13.2.
    public const ushort SDB_GIVE_GUILD_MONEY_INCENTIVE = 0x27A0;
    public const ushort DBS_GIVE_GUILD_MONEY_INCENTIVE = 0x27A1;

    /// <summary>AS_UPDATE_GUILD_DATA (0x144E) - the full guild blob the Arbiter pushes after an
    /// incentive (tap 2501). Built by the guild wiring, not here; declared so the number has one
    /// home.</summary>
    public const ushort AS_UPDATE_GUILD_DATA = 0x144E;

    /// <summary>Seconds between incentives. The capture has one grant, so this is our floor,
    /// not an observed value - the real number comes from a config sheet we do not load.</summary>
    public const int GuildIncentiveCooldownSeconds = 82800;   // 23 h, the usual daily shape

    /// <summary>Shortest name tap 6094 accepts. "Dob" (three) is refused, "dobb" (four) is not.</summary>
    public const int MinCharacterNameLength = 4;
    /// <summary>Longest name the characters row is built for.</summary>
    public const int MaxCharacterNameLength = 20;
    /// <summary>The only refusal code the capture shows. The real server almost certainly has
    /// distinct codes per reason; nothing here can tell them apart, so every refusal uses 1.</summary>
    public const int NameRefusedCode = 1;

    // --- SDB_UPDATE_USER_DAILY_EVENT_COUNT (0x293C) -> DBS (0x293D) ---
    // cap_newchar.log seq 505 -> 507, 58 B -> 11 B.
    // Handler_SDB_UPDATE_USER_DAILY_EVENT_COUNT (Arb_part_064.c:14404) needs frame >= 0x26 and
    // reads [0] u32 recordOffset [4] u32 recordLength (20) [8] u32 reqId [12] u32 playerId
    //       [16] i64 extra-reward reset stamp  [24] i64 party id, then the 20-byte record.
    //       Reply: [u32 reqId][u8 ok] - reqId at payload[8]. T156: stored, and the handler pushes
    //       AS_EVENT_MATCHING_INFO_LIST first, as the real one does - see OnUpdateUserDailyEventCount.
    public const ushort SDB_UPDATE_USER_DAILY_EVENT_COUNT = 0x293C; public const ushort DBS_UPDATE_USER_DAILY_EVENT_COUNT = 0x293D;

    // --- SDB_UPDATE_GET_EXTRA_REWARD (0x293E) -> DBS_UPDATE_GET_EXTRA_REWARD (0x293F) ---
    // cap_newchar.log seq 503 -> 504, 20 B -> 11 B. This one was in
    // WorldReplayTable.OneWayFromWorld until T15 - CLAUDE.md section 3 had it filed as a
    // periodic push because it never appeared in arb_world.log. It IS a per-user DLM request
    // and it wedged a login on 2026-09-14; the set now only holds opcodes proven one-way.
    // Handler_SDB_UPDATE_GET_EXTRA_REWARD (Arb_part_064.c:11963) needs frame >= 0x14 and reads
    //   [0] u32 reqId  [4] u32 playerId  [8] u8 kind  [9] u32 value  [13] u8 flag
    // Reply: [u32 reqId][u8 ok] (capture: 3F 00 00 00 01).
    public const ushort SDB_UPDATE_GET_EXTRA_REWARD = 0x293E; public const ushort DBS_UPDATE_GET_EXTRA_REWARD = 0x293F;

    // --- SDB_CANCEL_NPC_ARENA_BET (0x2927): FIRE AND FORGET, NEVER REPLY ---
    // Handler_SDB_CANCEL_NPC_ARENA_BET (Arb_part_063.c:1315) needs frame >= 0xe, calls
    // NpcArenaManager::Cancel and returns - there is no SendToSession at all, and no DBS_ opcode
    // exists for it. All five occurrences in cap_newchar.log (seq 3466, 4116, 4138, 4151, 4198,
    // the logout-countdown ticks) are followed by no A->W frame. It lives in
    // WorldReplayTable.OneWayFromWorld so the replay table cannot hand the NEXT request's reply
    // to it; there is deliberately no handler here.
    public const ushort SDB_CANCEL_NPC_ARENA_BET = 0x2927;

    private readonly CharacterStore _store;
    private readonly ILogger _log;
    public WorldUserControls UserControls { get; } = new();
    // T165: unpinned generic acks already announced (one Information line per opcode).
    private readonly HashSet<ushort> _unpinnedSeen = new();

    public DbProxyHandlers(CharacterStore store, ILogger log)
    {
        _store = store;
        _log = log;
    }

    private bool OnLoadAccountBenefit(WorldLink link, byte[] payload)
    {
        // Request is [DlmId][UserDbId], NOT AccountDbId (Arb_part_063.c:12662).
        if (payload.Length < 8) return true;
        uint dlmId = BitConverter.ToUInt32(payload, 0);
        int userDbId = BitConverter.ToInt32(payload, 4);
        var character = _store.GetCharacter(userDbId);
        var account = character == null ? null : _store.GetAccountById(character.AccountId);
        IReadOnlyList<CharacterStore.AccountBenefitRow> rows = Array.Empty<CharacterStore.AccountBenefitRow>();
        if (account != null)
        {
            // Deliberately account names only, not character-name aliases or stored admin_level.
            bool listed = Handlers.GmAccounts.IsListed(account.Name);
            // T219c: the operator experiment is retired; this only cleans up rows it already wrote.
            _store.RemoveBenefitExperimentRows(account.Id);
            var stored = _store.GetAccountBenefits(account.Id);
            var now = DateTimeOffset.UtcNow;
            rows = stored.Where(r => AccountBenefitExperiment.IsLive(r, now)).ToList();
            _log.LogInformation("T181 account benefits: user {User} account {Account} operator={Operator} packages=[{Packages}]",
                userDbId, account.Name, listed, string.Join(",", rows.Select(r => r.PackageId)));
            if (rows.Count != stored.Count)
                _log.LogWarning("Account benefits: account {Account} still holds {N} EXPIRED package(s) [{Packages}] - "
                    + "withheld from World, which asserts on an expired package it cannot resolve as a user trait (T219c)",
                    account.Name, stored.Count - rows.Count,
                    string.Join(",", stored.Where(r => !AccountBenefitExperiment.IsLive(r, now)).Select(r => r.PackageId)));
        }
        link.SendFrame(0x28BC, AccountBenefitExperiment.BuildReply(dlmId, account != null, rows));
        return true;
    }

    // ---- Session hooks (T21) ----
    // SA_ENTER_WORLD_FAIL arrives with the two opaque handles we put in AS_ENTER_WORLD and
    // nothing else that identifies the player: no UserDbId, no gameId-to-character map. Only
    // the session layer has that, and WorldBridge._players is private, so the two facts this
    // handler needs come in through hooks. Both are optional: unwired, the handler still logs
    // the failure in full and simply cannot retry (the client sits on the loading screen, which
    // is what happens today anyway).

    /// <summary>
    /// Our AS_ENTER_WORLD ArbiterUser handle (the gameId) -> the character id it was sent for,
    /// or 0 when no live session owns it. Wire to WorldBridge's player map.
    /// </summary>
    public Func<ulong, int>? PlayerIdForGameId { get; set; }

    /// <summary>
    /// Re-send AS_ENTER_WORLD for that session with the stored fallback applied - the caller
    /// looks the return point up itself (CharacterStore.GetDungeonReturn) because it also owns
    /// every other field of the packet. Wire to Handlers/WorldEntry.ResendEnterWorld.
    /// </summary>
    public Action<EnterWorldFailure>? ResendEnterWorld { get; set; }

    /// <summary>
    /// The dispatch cases in <see cref="TryHandle"/> that are deliberately NOT in
    /// <see cref="IsHandledRequest"/>. Every one is a login-time load the replay table already
    /// serves byte-exact from the capture; the builder here exists for its unit test and for the
    /// day the reply has to become per-character. Because <c>TryHandle</c> returns early on
    /// anything the allow-list rejects, these cases never run.
    ///
    /// <para>This list is what makes that intentional rather than accidental:
    /// <c>Dispatch_switch_and_the_allow_list_agree</c> fails the build for any case that is in
    /// neither the allow-list nor here. Four per-character loads (0x27F8, 0x2872, 0x2942,
    /// 0x2867) sat in this state for months, quietly replaying dob's bytes to every character,
    /// until T22 - that is the bug this guard exists to catch.</para>
    ///
    /// <para>The list shrinks as loads become per-character: T22 took four out of it, T25 took
    /// 0x2867, and T26 took the last two known-wrong ones (0x288F reputations and 0x2908
    /// fatigability - status/REPUTATION-FATIGABILITY.md). What is left is either genuinely
    /// static or has no evidence of per-character content yet.</para>
    /// </summary>
    public static readonly ushort[] DispatchOnlyForTests =
    {
        SA_PET_LOAD,                                // 0x1415
        SA_LOAD_BATTLE_FIELD_COOL_TIME,             // 0x1521
        SA_LOAD_SERVANT_AUTO_POTION_DATA,           // 0x152F
        SA_LOAD_SERVANT_AUTO_FEED_DATA,             // 0x1533
        SA_LOAD_SERVANT_STORAGE_DATA,               // 0x1537
        // 0x1539 / 0x153B moved to the allow-list in T179: the reply carries UserDbId.
        // 0x1554 SA_LOAD_EXTRAPOINT_DATA moved to the allow-list in T77 (served from characters.ep_*)
        SA_LOAD_BATTLE_FIELD_ENTER_COUNT,           // 0x155D
        // 0x2760 / 0x2764 moved to the allow-list in T147 (per-character recipes and proficiencies)
        SDB_LOAD_TELEPORT_TO_POS_LIST,              // 0x27A7
        SDB_LOAD_USER_RESTRICTION,                  // 0x2833
        // 0x2895 moved to the allow-list in T201: native per-user BG statistics.
        // 0x28B0 / 0x28B7 moved to the allow-list in T69 (byte-exact against cap_social2)
        // 0x28BB moved to the allow-list in T181: account-backed benefits with live DLM echo.
        SDB_LOAD_SERVANT_PERIOD,                    // 0x28C5
        SDB_LOAD_SKILLPERIOD,                       // 0x28C9
        SDB_LOAD_LEARNED_SOCIAL,                    // 0x28CF
        SDB_LOAD_TOKEN_EXCHANGE,                    // 0x28FE
        // 0x2900 / 0x2981 moved to the allow-list in T168 (their builders are the empty, captured form)
        SDB_LOAD_QUEST_PROGRESS,                    // 0x2902
        SDB_LOAD_PROMOTION_LIST,                    // 0x2912
        SDB_LOAD_PROMOTION_COND_LIST,               // 0x2916
        SDB_LOAD_PASSIVITY_COOLTIME,                // 0x2922
        // 0x293A / 0x2967 moved to the allow-list in T156 (daily_event / event_matching_reward)
        // 0x2975 / 0x2986 moved to the allow-list in T167 (skill polishing / card data from the store)
    };

    /// <summary>
    /// THE ALLOW-LIST. True when <see cref="TryHandle"/> answers this opcode itself; false sends
    /// it to the replay table, which replies with the CAPTURED DLM id and wedges the user if the
    /// reply carries a live one (status/HANDOFF.md section 1).
    ///
    /// <para>Only intercept messages that need live per-session data. Every login-time SDB_* that
    /// is served byte-exact by the replay table stays off this list; the synthetic builders below
    /// desynced World once (0x27A2 returns a 3235-byte item list, not an empty list) and are kept
    /// only for their unit tests.</para>
    ///
    /// <para>This is a method rather than the inline switch it used to be so the T15 coverage test
    /// can ask it directly - see Every_per_user_request_opcode_is_answered in the test project.
    /// Adding an opcode here WITHOUT adding a case to the dispatch switch in TryHandle makes it
    /// fall through to the replay table; TryHandle logs an error if that ever happens.</para>
    /// </summary>
    public static bool IsHandledRequest(ushort op)
    {
        switch (op)
        {
            case 0x13E8: // T201: native local tournament request is one-way; never replay a guessed1518.
            case 0x162F: // T201: realm purchase count update, SQL plus AS1630 broadcast.
            case 0x2983: // T201: real account purchase-limit persistence; no longer a generic ack.
            case SDB_USER_ENTERWORLD:
            case SA_LOAD_SERVANT_DATA:                 // T179: cap_final2b 133 -> 134
            case SA_LOAD_SERVANT_ADVENTURE_DATA:        // T179: cap_final2b 135 -> 136
            case SDB_UPDATE_USER_DATA:
            case SDB_SAVE_27FA:
            case SDB_SAVE_2924:
            case SDB_UPDATE_LEFT_COOLTIME_PREMIUM_SLOT:   // 0x28C1, T64
            case SDB_LOAD_REFER_A_FRIEND:   // 0x28B0, T69
            case SDB_USER_FORGET_SKILL:                 // 0x2792, T79
            // --- T147: crafting (status/CRAFTING.md) ---
            case SDB_LOAD_ITEM_RECIPE:                  // 0x2760, was replay-served
            case SDB_LOAD_SKILL_PROF:                   // 0x2764, was replay-served
            case SDB_LEARN_ITEM_RECIPE:                 // 0x275E
            case SDB_DELETE_ITEM_RECIPE_LIST:           // 0x2762
            case SDB_SET_RECIPE_BOOKMARK:               // 0x288A
            case SDB_ITEM_PRODUCE_STEP1:                // 0x2756
            case SDB_ITEM_PRODUCE_STEP2:                // 0x2758
            case SDB_UPDATE_SKILL_PROF:                 // 0x2766
            case S_UPDATE_PROF_MINERAL:                 // 0x273D
            case S_UPDATE_PROF_BUG:                     // 0x273E
            case S_UPDATE_PROF_ENERGY:                  // 0x273F
            case S_UPDATE_PROF_HERB:                    // 0x2740
            // --- T77: cap_social4's fifteen ---
            case SDB_UPDATE_DAILY_EXTRA_POINT:
            case SDB_UPDATE_EXTRA_POINT:
            case SDB_USER_LEARN_EP_PERK:
            case SDB_USER_RESET_EP_PERK:
            case SDB_UPDATE_PRE_EP_INFO:
            case SDB_REGISTER_CARD:
            case SDB_MOUNT_CARD:
            case SDB_UNMOUNT_CARD:
            case SDB_INIT_LIMIT_REMAIN_REPUTATION:
            case SDB_LOAD_FEUDAL_LORD_FLAG:
            case SDB_TBA_REQUEST_BATTLEPASS_SEASONDATA:
            case SA_CREST_POINT:
            case SA_CREST_USE:             // 0x1469, T158: the glyph toggle that wedged test
            case SDB_PEGASUS_FEE:          // 0x27A5, T158: the flight master's fee
            case SDB_MARK_AS_QUEST_COMPLETED:        // 0x28AE, T164: the scroll's last request
            case SDB_USER_LEARN_SKILL_FOR_MULTIPLE:  // 0x2790, T164
            case SA_MAKE_SYS_PARCEL:
            case SDB_LOAD_28B7:             // 0x28B7, T69
            case GuildPackets.SDB_CREATE_GUILD2:   // 0x27D4, T69
            case SDB_SAVE_2768:
            case SDB_EQUIP_ITEM:           // 0x2814, T145: the 0x2769 shape, applied the same way
            case SDB_SET_QUESTLIST_INFO:   // 0x2733, T145: the login wedge - quest ids for the batch
            case SDB_ITEM_TRADE:   // 0x276A, T65
            case SDB_SAVE_2936:
            case SDB_DAILY_QUEST:
            case SDB_DAILY_QUEST_SEED:
            case SDB_UPDATE_EXP_LEVEL:   // 0x273C = [reqId][ok]; also writes level/exp to the row
            case SDB_SET_QUEST_INFO:     // 0x272F: quest write, allocates the questDbId
            case SDB_QUEST_LIST:
            // Post-seed-burst steps (lobby_tap.log 02:51:11.15x): 0x2910 -> 0x290C -> 0x27B9.
            // These MUST echo the live DLM id. The replay table attributed 0x290D to the
            // World push-reply 0x143F (which carries no id), so it went out with the captured
            // id, DLMExistManager::Find missed, and the 0x290C item head-blocked the user.
            case SDB_LOAD_FRIEND_INFO:   // 0x2911 = [01][reqId]  (capture: 01 3D 00 00 00)
            case SDB_LOAD_290C:          // 0x290D = [01][reqId]  (capture: 01 3E 00 00 00)
            case SDB_EP_PERK:            // 0x27BA = static, reqId@8 (capture: 05.. 27.. 3F 00 00 00)
            // Post-spawn steps (lobby_tap.log 02:52:07.09x-.11x).
            case SDB_SET_TASK_SHOW_TOGGLE: // 0x2735 = the request's first 13 bytes (T142 wedge)
            case SDB_END_START_QUEST_LIST: // 0x2737 = [reqId][01]
            case SDB_USER_LOAD_INVENTORY:  // 0x27A3 + 0x27A4: starter inventory for every character except the captured one
            case SDB_LOAD_ACCOUNT_BENEFIT: // T181: operator experiment, account-backed list
            case SDB_LOAD_2930:            // 0x2931 = [reqId][01][held]
            case SDB_USER_VAPORIZED:       // 0x282D, T148: one-way, tracked not stored
            case SDB_LOAD_WORLD_EVENT:     // 0x27B4 = [reqId][01]       (capture: 83 00 00 00 01)
            case SA_CLEAR_BATTLE_FIELD_ENTER_COUNT: // 0x1563 = [01][reqId@8]  (decompile Arb_part_062.c:4769)
            case SA_UPDATE_BATTLE_FIELD_COOL_TIME:  // 0x1524 = [01][reqId@16] (cap_bg1 12940/12945)
            case SA_LEARN_ALL_CREST_ACQUIRABLE:     // 0x1464 = learned-crest list (all of them)
            case SDB_UPDATE_USER_ACTPOINT:          // 0x297C = [reqId][01]
            case SA_REQUEST_ENTER_DUNGEON:          // 0x13BF (zone change step 1)
            case SA_RESPONSE_ENTER_DUNGEON:         // 0x13C1 (zone change step 2)
            case SA_ENTER_WORLD_FAIL:               // 0x148D pushes + a re-sent 0x138E (T21)
            case SA_ENTER_WORLD:                    // 0x138C, T161b: flush a held leave (LeaveGate)
            case SA_CHAR_LOC:                       // 0x1443, T161b: /@goto -> 0x2825 ask
            case SA_BROADCAST_SYSTEM_MESSAGE_TO_WHOLE_WORLD:        // 0x1436, T172: S_SYSTEM_MESSAGE to all
            case SA_BROADCAST_SYSTEM_MESSAGE_NOT_IN_SPECIAL_PLACE:  // 0x1437, T172
            case SA_CREST_USE_LIST:                 // 0x1467, T172: -> 0x1468 [DlmId][ok]
            case SA_SYNC_DATE_TIME:                 // 0x15BC, T172: World's clock echo, no reply
            case SDB_CHANGE_CITY_WAR_STATE:         // 0x2958, T172: -> 0x295A echo, ok 1
            case Handlers.ArbiterClientHandlers.SDB_SIMULATE_ITEM_TOOLTIP: // 0x282F, T169: World's simulated item -> the compare tooltip
            case SDB_LOAD_2869:          // 0x15E0 push + 0x286A, both carrying the live reset time
            case SDB_LOAD_BATTLE_FIELD_LIST: // T201: persisted native BG results (not custom rating)
            case SA_LOAD_GUARD:          // 0x147D -> 0x1484 election state + 0x1480 finish (no guards)
            // --- T15: the per-user writes World sends during play. Each one is a DLM item; a
            // missing reply head-blocks the user's queue for the life of the World process. ---
            case SDB_USER_LEARN_SKILL:            // 0x278F = echo of the fee atoms + empty skill-period list
            case SDB_ACCOMPLISH_USER_ACHIEVEMENT: // 0x2803 = the newly-accomplished records echoed
            case SDB_UPDATE_REPUTATION_INFO:      // 0x2892 = [ok][reqId]  (ok-first, the odd one out)
            case SDB_ADD_TUTORIAL_SIMPLE_TIP:     // 0x286F = [reqId][ok]
            case SDB_DONT_REPEAT_TUTORIAL_SIMPLE_TIP: // T191: persist popup-count delta before ack
            case SDB_UPDATE_SEREN_GUIDE_INFO:     // 0x2945 = [reqId][playerId][ok]
            case SDB_ASK_CHANGE_CHAR_NAME:        // 0x2854, T88 - the rename name check
            case SDB_DO_CHANGE_CHAR_NAME:         // 0x2856, T88 - the rename itself
            case SA_UPDATE_RANK_USERNAME:         // 0x161C, T90 - one-way rename echo
            case SA_WATCH_MOVIE:                  // 0x155C, T104 - one-way cinematic marker
            case SA_START_CHANGE_APPEARANCE:      // 0x1498, T90 - one-way, nothing to persist
            case SDB_GIVE_GUILD_MONEY_INCENTIVE:  // 0x27A0, T90
            case SDB_UPDATE_USER_DAILY_EVENT_COUNT: // 0x293D = [reqId][ok], reqId at payload[8]
            case SDB_UPDATE_GET_EXTRA_REWARD:     // 0x293F = [reqId][ok]
            // --- T22: the per-character login loads, rebuilt from rows instead of replaying
            // dob's captured reply to everyone. Each one is byte-exact against BOTH the
            // brand-new-character reply in cap_newchar.log and the with-progress reply in
            // arb_world_2026-09-13; playerId 1 still gets the capture. ---
            case SDB_USER_ACHIEVEMENT:            // 0x27F9 from the stored 0x27FA + 0x2802 records
            case SDB_TUTORIAL_SIMPLE_TIP:         // 0x2873 from the stored 0x286E tips
            case SDB_SEREN_GUIDE:                 // 0x2943 from the stored 0x2944 slots
            case SA_LOAD_EXTRAPOINT_DATA:         // 0x1554 -> 0x1555, T77: stored EP
            case SDB_LOAD_2867:                   // 0x2868, now rebuilt from dungeon_cooldowns
            // --- T25: one-way dungeon writes. They send nothing back; they are here so
            // TryHandle sees them at all and can persist them. ---
            case SA_UPDATE_DUNGEON_COOLTIME:      // 0x13B6
            case SA_UPDATE_DUNGEON_CLEAR_COUNT:   // 0x13B7
            case SA_DELETE_DUNGEON_COOLTIME:      // 0x13BD
            // --- T108: the instance registry. Both are one-way (the capture has no reply to
            // either) and were falling through to a replay lookup that has nothing for them. ---
            case DungeonChannels.SA_ADD_DUNGEON_CHANNEL:     // 0x13C5
            case DungeonChannels.SA_REMOVE_DUNGEON_CHANNEL:  // 0x13C6
            // --- T115: the five LogDB writes. All one-way - every Handler_SDB_*LOG* runs to
            // `return 1` with no packet writer - so handling them changes nothing on the wire;
            // it only stops the bytes being dropped. status/GAME-LOG.md. ---
            case GameLogPackets.SDB_ITEM_TRADE_LOG:           // 0x27DD
            case GameLogPackets.SDB_ADD_PVP_USER_LOG:         // 0x27FE
            case GameLogPackets.SDB_ADD_PK_USER_LOG:          // 0x27FF
            case GameLogPackets.SDB_ADD_GROUP_DUEL_USER_LOG:  // 0x2800
            case GameLogPackets.SDB_CASH_ITEM_LOG:            // 0x288C
            case BattlegroundResults.BSA_END_BATTLE_FIELD_RESULT_LIST: // T199, one-way
            case BattlegroundResults.BSA_UPDATE_BATTLE_FIELD_LOG:      // T199, one-way
            // --- T26: the last two per-character login loads, rebuilt from rows. ---
            case SDB_REPUTATION_LIST:             // 0x2890 from the stored 0x2891 records
            case SDB_FATIGABILITY_LIST:           // 0x2909 from the account's fatigue row
            // --- T23: city-war result. Not a DLM item (no reqId), but the reply echoes two
            // u32s out of the live request, so a replayed 0x295D would carry captured ones. ---
            case SDB_RESULT_CITY_WAR:             // 0x15ED + 0x295D, both echoing request+16/+20
            case SDB_LOAD_CITY_GUILD_INFO:        // 0x2954 -> 0x2955 from city_guild, T154
            // --- T156: the Vanguard Initiative, the Arbiter's half (status/PERSISTENCE-MAP.md T156) ---
            case SA_AVAILABLE_EVENT_MATCHING_LIST:              // 0x1507 -> 0x1591 for the live user
            case SA_LOAD_EVENT_MATCHING_INFO:                   // 0x1592 -> 0x1595 + 0x1582
            case SA_UPDATE_PLAYGUIDE_EXTRA_REWARD_RESETTIME:    // 0x1598 -> 0x1599, stored
            case SA_UPDATE_EVENT_MATCHING_ADD_REWARD_RESETTIME: // 0x159A -> 0x159B, stored, counts cleared
            case SDB_LOAD_293A:                                 // 0x293A -> 0x293B from daily_event
            case SDB_UPDATE_ADDITIONAL_REWARD_RECV_COUNT:       // 0x2965 -> 0x2966
            case SDB_LOAD_ADDITIONAL_REWARD:                    // 0x2967 -> 0x2968 from event_matching_reward
            // --- T33: World's dungeon-timeline broadcast. Not a DLM item and usually empty;
            // the non-empty form is echoed back one 0x1581 per record. ---
            case DSA_DUNGEON_TIMELINE_OPEN_INFO:  // 0x13F2 -> N x 0x1581
            // --- T42: the warehouse. Eight live requests; the ninth (0x2754) is a stub in the
            // real Arbiter and lives in WorldReplayTable.OneWayFromWorld instead, so it must NOT
            // be here. status/MAIL-WAREHOUSE.md section 4. ---
            case SDB_VIEW_WAREHOUSE:              // 0x274B, rebuilt from the item rows
            case SDB_STORE_WAREHOUSE:             // 0x274D, atoms applied then echoed
            case SDB_GET_WAREHOUSE:               // 0x274F, same
            case SDB_CHANGE_WAREHOUSE_POS:        // 0x2780, same shape as the two above
            case SDB_PAY_WAREHOUSE_COMMISION:     // 0x27CA = [reqId][ok][error]
            case SDB_CLEAR_WAREHOUSE:             // 0x27E1 = [reqId][ok]
            case SDB_WAREHOUSE_AUTO_SORT:         // 0x27E3, the request's fields echoed back
            case SDB_INCREASE_WAREHOUSE_SIZE:     // -> 0x283E, NOT 0x2840 (trap 1 above)
            case SDB_INCREASE_INVENTORY_SIZE:     // 0x283D -> 0x283E, T150b
            // --- T150b: generic acks, each proven by a live pair (DbAckTable) ---
            case SDB_GUILD_LEARN_PERK:
            case SDB_CONDITIONAL_TELEPORT:
            case SDB_ITEM_SIMPLE_ATOM:
            case SDB_MAIN_MENU_COMMAND:
            case SDB_INCREMENT_CHARACTER_LEVEL:                // T162
            case SDB_INCREMENT_CHARACTER_LEVEL_JUMP:
            case SDB_INCREMENT_CHARACTER_LEVEL_PERFECT_JUMP:
            case SDB_ITEM_EXTRACT:                              // T166
            case SDB_ITEM_ENCHANT:
            case SDB_ITEM_ENCHANT_IDENTIFY:
            case SDB_ITEM_MERGE:
            case SDB_ITEM_UNIDENTIFY:
            case SDB_ENCHANT_ITEM_BOOST:
            case SDB_ITEM_DECOMPOSITION:
            case SDB_ITEM_AWAKEN:
            case SDB_ITEM_UNBIND:
            case SDB_EQUIPMENT_INHERITANCE:

            // --- T45: the mailbox. Six live requests; before T45 none of them was answered at
            // all, and no capture has one either, so the replay table could not cover for us.
            case SDB_LIST_PARCEL:                 // 0x2778, the empty form is byte-exact
            case SDB_MAKE_PARCEL:                 // 0x277a, atoms echoed with allocated ids
            case SDB_LOAD_PREMIUM_SLOT_LEFT_COOLTIME:   // 0x28BD, T61
            case SDB_RECV_PARCEL:                 // 0x277c, atoms applied then echoed
            case SDB_RECV_PARCEL_EX:              // 0x277e, "receive all"
            case SDB_RETURN_PARCEL:               // 0x2782 = [DlmId][ok]
            case SDB_DELETE_PARCEL:               // 0x2812 = [DlmId][ok]
            // --- T51: the guild boot load. Zero-length request, and the answer is a SEQUENCE
            // ending in DBS_INIT_GUILD_DATA with Success = 0. It carries no DlmId, so an
            // unanswered one cannot head-block a user - but it does leave World with no guild
            // mirror at all, and until T51 the answer came from the capture, padding leak and
            // all.
            case SDB_INIT_GUILD:                  // 0x27CF -> 0x27ED (+ 0x27D0..0x27D3 per guild)
            // --- T55: the five broker requests that carry a DlmId. Each one wedges the
            // character's DB queue if it goes unanswered, and the broker window sends the first
            // of them the moment it opens.
            case SDB_TRADE_BROKER_REGISTER_ITEM:   // 0x2817 -> 0x2818, frame 0x16
            case SDB_TRADE_BROKER_UNREGISTER_ITEM: // 0x2819 -> 0x281A, frame 0x1E
            case SDB_TRADE_BROKER_CALC_SOLD_ITEM:  // 0x281B -> 0x281C, frame 0x22
            case SDB_TRADE_BROKER_CALC_BOUGHT_ITEM:// 0x281D -> 0x281E, frame 0x22
            case SDB_TRADE_BROKER_BUY_IT_NOW:      // 0x281F -> 0x2820, frame 0x27
            // --- T167 (World/DbProxyT167.cs): cards, EP pages, skill polishing, dungeon rank ---
            case SDB_LOAD_2986:                          // 0x2986 SDB_REQUEST_CARD_DATA
            case SDB_CHANGE_CARD_PRESET:
            case SDB_INCREASE_CARD_PRESET:
            case SDB_CREATE_CARD_INFO:
            case SDB_ACTIVATE_CARD_COMBINE_LIST:
            case SDB_DEACTIVATE_CARD_COMBINE_LIST:
            case SDB_CHANGE_EP_PAGE:
            case SDB_EXPAND_EP_PAGE:
            case SDB_RESET_EXTRA_POINT_DATA:
            case SDB_USER_INCREASE_EP_POINT_BY_ITEM:
            case SDB_LOAD_2975:                          // 0x2975 SDB_LOAD_SKILL_POLISHING
            case SDB_SKILL_POLISHING_ADD_EXP:
            case SDB_SKILL_POLISHING_CHANGE_OPTION:
            case SDB_SKILL_POLISHING_UNLOCK_OPTION:
            case SDB_SKILL_POLISHING_UPGRADE_LEVEL:
            case SDB_UPDATE_DUNGEON_RANK_RECORD:         // no DlmId, no reply
            // --- T168 (World/DbProxyT168.cs): the rest of group C ---
            case SDB_OPEN_DUAL_OPTION:
            case SDB_CHANGE_DUAL_OPTION_IDX:
            case SDB_ALCHEMY:
            case SDB_CHANGE_EQUIPMENT_EXP:
            case SDB_UPDATE_CUSTOMIZING_COMBINE_RESULT:
            case SDB_UPDATE_ITEM_CUSTOMEXITEM:
            case SDB_ITEM_CUSTOMIZING:
            case SDB_ITEM_POINT_STORE:
            case SDB_POLITICS_POINT_STORE:
            case SDB_ITEM_FLOATING_CASTLE_PASTS_STORE:
            case SDB_BUY_VIP_STORE_ITEM:
            case SDB_ITEM_GUILD_STORE:
            case SDB_ITEM_DELIVER:
            case SDB_CHANGE_ACCESSORY_TRANSFORM:
            case SDB_SERVANT_ADVENTURE_RECEIVE_REWARD:
            case SDB_RECEIVE_COLLECTION_BOOK_REWARD:
            case SDB_REQUEST_GUILD_QUEST_WEEKLY_REWARD_ITEM_TRANSACTION:
            case SDB_SHARED_ACCOUNT_DATA:
            case SDB_UPDATE_REDUCE_SERVANT_PERIOD:
            case SDB_UPDATE_REDUCE_SKILLPERIOD:
            case SDB_DELETE_USER_ACHIEVEMENT:
            case SDB_USE_RIGHT_ITEM:
            case SDB_SET_MONEY:
            case SDB_GET_MONEY:
            case SDB_CHANGE_GOLD_CONSUMPTION:
            case SDB_ADD_VIP_GAME_EXP:
            case SDB_LOAD_USER_VIP_INFO:
            case SDB_ADMIN_USER_DAILY_ATTENDANCE:
            case SDB_CHECK_PLAYTIME_REWARD:
            case SDB_LOAD_ADDITIONAL_FATIGUEPOINT:
            case SDB_INITIALIZE_LEFT_COOL_TIME_PREMIUM_SLOT:
            case SDB_USER_LEARN_HIDE_PASSIVE_SKILL:
            case SDB_ADD_SERVANT:
            case SDB_RIGHT_ITEM_LIST:
            case SDB_ADD_GUILDMEMBER2:
            case SDB_ASK_CHANGE_GUILD_NAME:
            case SDB_EQUIP_PARTNER_STYLE_ITEM:
            case SDB_UNEQUIP_PARTNER_STYLE_ITEM:
            case SDB_GROUP_DUEL_RETURN:
            case SDB_OPEN_FLOATING_CASTLE_PARTS_STORE:
            case SDB_TRADE_BROKER_START_DEAL:
            case SDB_TRADE_BROKER_CANCEL_DEAL:
            case SDB_LOAD_2900:                          // 0x2900 LOAD_LIMITED_DROP_POINT, empty (captured)
            case SDB_LOAD_ACHIEVE_LIST:                  // 0x2981 LOAD_PURCHASE_LIMIT, empty (captured)
            case SDB_USER_CLEAR_ALL_SKILL:               // 0x27E6, T170: cap_clearallskill 902/903
            case SDB_UPDATE_EVENTSYSTEM_PROGRESS:        // 0x2969, T170: decompile-derived, no DlmId
                return true;
            default:
                // T165 group A: DbAckTable's unpinned rows (it refuses any B/C opcode). Everything
                // else -> replay table.
                return DbAckTable.Covers(op);
        }
    }

    /// <summary>Returns true if handled (caller should not fall back to replay).</summary>
    public bool TryHandle(WorldBridge bridge, WorldLink link, ushort op, byte[] payload)
    {
        if (!IsHandledRequest(op)) return false;

        // Name the opcode in the log so a real handler is distinguishable from a replay at a
        // glance (DbProxyOpcodeNames is generated from WorldServer.exe.c; logging only).
        _log.LogDebug("DbProxy handling {Op} len={Len}", DbProxyOpcodeNames.Describe(op), payload.Length + 6);

        switch (op)
        {
            case 0x162F:
            case 0x2983:
                return Handlers.QaPurchaseCommands.Handle(_store, bridge, link, op, payload);
            case 0x13E8:
                // Arb062:10407-10565 -> Arb029:17877-18155. The local two-party branch
                // builds/discards the party vector without output. Cross-Arbiter queries
                // need the absent AA1773 transport; do not fabricate a successful creation.
                return true;
            case SDB_USER_ENTERWORLD: return OnUserEnterWorld(link, payload);
            case SDB_UPDATE_USER_DATA: return OnUpdateUserData(link, payload);
            case SDB_USER_LOAD_INVENTORY: return OnLoadInventory(link, payload);
            case SA_LOAD_GUARD: return OnLoadGuardRequest(link);
            case SDB_UPDATE_EXP_LEVEL: return OnUpdateExpLevel(link, payload);
            case SDB_SET_QUEST_INFO: return OnSetQuestInfo(link, payload);

            // --- Logout save sequence (reqId echoed from the live request) ---
            case SDB_SAVE_27FA: return OnSaveUserAchievement(link, payload);
            case SDB_SAVE_2924: link.SendFrame(DBS_SAVE_2925, BuildReqIdAck(payload, 8)); return true;
            case SDB_LOAD_REFER_A_FRIEND:
                link.SendFrame(DBS_LOAD_REFER_A_FRIEND,
                    BuildDbsReferAFriendList(payload.Length >= 8 ? U32(payload, 6) : 0)); return true;

            // --- T77: EP. The four plain acks, then the reset, which echoes its atoms. ---
            case SDB_USER_FORGET_SKILL:
                link.SendFrame(DBS_USER_FORGET_SKILL, BuildDbsUserForgetSkill(Ep32(payload, 0)));
                return true;
            case SDB_UPDATE_DAILY_EXTRA_POINT: return OnUpdateDailyExtraPoint(link, payload);
            case SDB_UPDATE_EXTRA_POINT:       return OnUpdateExtraPoint(link, payload);
            case SDB_UPDATE_PRE_EP_INFO:       return OnUpdatePreEpInfo(link, payload);
            case SDB_USER_LEARN_EP_PERK:
                // T167: EpPerkList ref@0, UserDbId@12, UseEpPoint@16 - into the current page.
                _store?.LearnEpPerks(Ep32i(payload, 12), ReadEpPerkList(payload), Ep32i(payload, 16));
                link.SendFrame(DBS_USER_LEARN_EP_PERK, BuildReqIdAck(payload, 8)); return true;
            case SDB_USER_RESET_EP_PERK:       return OnResetEpPerk(link, payload);

            // --- Collection cards: account quantities/book points; per-character mounts. ---
            case SDB_REGISTER_CARD:
                // T85b, from the dumpers rather than from the reply echo T77 guessed at.
                // SDB_REGISTER_CARD (Arb_part_017.c:12213, guard 0x19): DlmId@06,
                // AccountDbId@0A (i64), CardTemplateId@12, Amount@16 - payload 0, 4, 12, 16.
                // SDB_MOUNT_CARD / _UNMOUNT_CARD (Arb_part_017.c:11096 / 17206, guard 0x1D):
                // DlmId@06, AccountDbId@0A (i64), UserDbId@12, PresetIndex@16, CardTemplateId@1A
                // - payload 0, 4, 12, 16, 20. All three key on the ACCOUNT at payload 4; only
                // the mount pair also names a character, which is the split the table does not
                // model yet (status/STATUS.md, T85b).
                // T86: the account at +4 is an i64, and it is the ONLY owner this frame has.
                var cardSheet = CardCollectionSheet.Entry.Value;
                cardSheet.Templates.TryGetValue(Ep32i(payload, 12), out var cardTemplate);
                _store?.AddCard(Ep64(payload, 4), Ep32i(payload, 12), Ep32i(payload, 16),
                    cardTemplate?.BookPoints ?? 0,
                    cardTemplate != null && cardSheet.Available ? cardSheet.LevelFor : null);
                link.SendFrame(DBS_REGISTER_CARD, BuildDbsRegisterCard(
                    Ep32(payload, 0), Ep32i(payload, 12), Ep32i(payload, 16))); return true;
            case SDB_MOUNT_CARD:
                // T86: UserDbId@12, PresetIndex@16, CardTemplateId@20 - the mount is the one card
                // frame that names a character, and it names a preset slot with it.
                _store?.MountCard(Ep32i(payload, 12), Ep32i(payload, 16), Ep32i(payload, 20));
                link.SendFrame(DBS_MOUNT_CARD, BuildDbsMountCard(
                    Ep32(payload, 0), Ep32i(payload, 16), Ep32i(payload, 20))); return true;
            case SDB_UNMOUNT_CARD:
                // The unmount repeats the preset index the mount used rather than sending a
                // sentinel, so the row goes out the way it came in.
                _store?.UnmountCard(Ep32i(payload, 12), Ep32i(payload, 16), Ep32i(payload, 20));
                link.SendFrame(DBS_UNMOUNT_CARD, BuildDbsMountCard(
                    Ep32(payload, 0), Ep32i(payload, 16), Ep32i(payload, 20))); return true;

            // --- T167: cards, EP pages, skill polishing, dungeon rank (World/DbProxyT167.cs) ---
            case SDB_CHANGE_CARD_PRESET:            return OnChangeCardPreset(link, payload);
            case SDB_INCREASE_CARD_PRESET:          return OnIncreaseCardPreset(link, payload);
            case SDB_CREATE_CARD_INFO:              return OnCreateCardInfo(link, payload);
            case SDB_ACTIVATE_CARD_COMBINE_LIST:    return OnCardCombine(link, payload, activate: true);
            case SDB_DEACTIVATE_CARD_COMBINE_LIST:  return OnCardCombine(link, payload, activate: false);
            case SDB_CHANGE_EP_PAGE:                return OnChangeEpPage(link, payload);
            case SDB_EXPAND_EP_PAGE:                return OnExpandEpPage(link, payload);
            case SDB_RESET_EXTRA_POINT_DATA:        return OnResetExtraPointData(link, payload);
            case SDB_USER_INCREASE_EP_POINT_BY_ITEM: return OnIncreaseEpPointByItem(link, payload);
            case SDB_SKILL_POLISHING_ADD_EXP:       return OnPolishingAddExp(link, payload);
            case SDB_SKILL_POLISHING_CHANGE_OPTION: return OnPolishingChangeOption(link, payload);
            case SDB_SKILL_POLISHING_UNLOCK_OPTION: return OnPolishingUnlockOption(link, payload);
            case SDB_SKILL_POLISHING_UPGRADE_LEVEL: return OnPolishingUpgradeLevel(link, payload);
            case SDB_UPDATE_DUNGEON_RANK_RECORD:    return OnUpdateDungeonRankRecord(payload);

            // --- T168: the rest of group C (World/DbProxyT168.cs) ---
            case SDB_OPEN_DUAL_OPTION:
            case SDB_CHANGE_DUAL_OPTION_IDX:
            case SDB_ALCHEMY:
            case SDB_CHANGE_EQUIPMENT_EXP:
            case SDB_UPDATE_CUSTOMIZING_COMBINE_RESULT:
            case SDB_UPDATE_ITEM_CUSTOMEXITEM:
            case SDB_ITEM_CUSTOMIZING:
            case SDB_ITEM_POINT_STORE:
            case SDB_POLITICS_POINT_STORE:
            case SDB_ITEM_FLOATING_CASTLE_PASTS_STORE:
            case SDB_BUY_VIP_STORE_ITEM:
            case SDB_ITEM_GUILD_STORE:
            case SDB_ITEM_DELIVER:
            case SDB_CHANGE_ACCESSORY_TRANSFORM:
            case SDB_SERVANT_ADVENTURE_RECEIVE_REWARD:
            case SDB_RECEIVE_COLLECTION_BOOK_REWARD:
            case SDB_REQUEST_GUILD_QUEST_WEEKLY_REWARD_ITEM_TRANSACTION:
            case SDB_SHARED_ACCOUNT_DATA:
            case SDB_UPDATE_REDUCE_SERVANT_PERIOD:
            case SDB_UPDATE_REDUCE_SKILLPERIOD:
            case SDB_DELETE_USER_ACHIEVEMENT:
            case SDB_USE_RIGHT_ITEM: return OnT168Echo(link, op, payload);
            case SDB_SET_MONEY:                              return OnSetMoney(link, payload);
            case SDB_GET_MONEY:                              return OnGetMoney(link, payload);
            case SDB_CHANGE_GOLD_CONSUMPTION:                return OnChangeGoldConsumption(link, payload);
            case SDB_ADD_VIP_GAME_EXP:                       return OnAddVipGameExp(link, payload);
            case SDB_LOAD_USER_VIP_INFO:                     return OnLoadUserVipInfo(link, payload);
            case SDB_ADMIN_USER_DAILY_ATTENDANCE:            return OnAdminDailyAttendance(link, payload);
            case SDB_CHECK_PLAYTIME_REWARD:                  return OnCheckPlaytimeReward(link, payload);
            case SDB_LOAD_ADDITIONAL_FATIGUEPOINT:           return OnLoadAdditionalFatiguePoint(link, payload);
            case SDB_INITIALIZE_LEFT_COOL_TIME_PREMIUM_SLOT: return OnInitializePremiumSlotCooltime(link, payload);
            case SDB_USER_LEARN_HIDE_PASSIVE_SKILL:          return OnLearnHidePassiveSkill(link, payload);
            case SDB_ADD_SERVANT:                            return OnAddServant(link, payload);
            case SDB_RIGHT_ITEM_LIST:                        return OnRightItemList(link, payload);
            case SDB_ADD_GUILDMEMBER2:                       return OnAddGuildMember2(link, payload);
            case SDB_ASK_CHANGE_GUILD_NAME:                  return OnAskChangeGuildName(link, payload);
            case SDB_EQUIP_PARTNER_STYLE_ITEM:               return OnT168Refusal(link, op, payload);
            case SDB_UNEQUIP_PARTNER_STYLE_ITEM:             return OnT168Refusal(link, op, payload);
            case SDB_GROUP_DUEL_RETURN:                      return OnT168Refusal(link, op, payload);
            case SDB_OPEN_FLOATING_CASTLE_PARTS_STORE:       return OnT168Refusal(link, op, payload);
            case SDB_TRADE_BROKER_START_DEAL:                return OnBrokerDeal(op, payload);
            case SDB_TRADE_BROKER_CANCEL_DEAL:               return OnBrokerDeal(op, payload);

            // --- T77: the rest ---
            case SDB_INIT_LIMIT_REMAIN_REPUTATION:
                link.SendFrame(DBS_INIT_LIMIT_REMAIN_REPUTATION, BuildDbsInitLimitRemainReputation(
                    Ep32(payload, 0), DefaultRemainReputation, 0)); return true;
            case SDB_LOAD_FEUDAL_LORD_FLAG:
                link.SendFrame(DBS_LOAD_FEUDAL_LORD_FLAG, BuildDbsFeudalLordFlag()); return true;
            case SDB_TBA_REQUEST_BATTLEPASS_SEASONDATA:
                link.SendFrame(DBS_TBA_UPDATE_BATTLEPASS_SEASONDATA, BuildDbsBattlePassSeasonData());
                return true;
            case SA_CREST_POINT:
                // T83: NewPoint@20 / NewExPoint@24 are what S_CREST_INFO shows at +0x08 and
                // +0x0C (cap_social4_client frames 5105 and 5108 both read 10 there). T77
                // answered the frame and dropped both numbers.
                StoreCrestPoints(payload);
                link.SendFrame(AS_CREST_POINT, BuildAsCrestPoint(Ep32(payload, 16))); return true;
            case SA_CREST_USE:
                return OnCrestUse(link, payload);
            case SDB_PEGASUS_FEE: return OnPegasusFee(link, payload);
            case SDB_MARK_AS_QUEST_COMPLETED: return OnMarkAsQuestCompleted(link, payload);
            case SDB_USER_LEARN_SKILL_FOR_MULTIPLE: return OnUserLearnSkillForMultiple(link, payload);
            case SA_MAKE_SYS_PARCEL: return OnMakeSysParcel(link, payload);
            case SDB_LOAD_28B7:
                link.SendFrame(DBS_LOAD_28B6,
                    BuildDbsInviteFriend(payload.Length >= 8 ? U32(payload, 6) : 0)); return true;
            case GuildPackets.SDB_CREATE_GUILD2: return OnCreateGuild2(link, payload);
            case SDB_UPDATE_LEFT_COOLTIME_PREMIUM_SLOT:
                link.SendFrame(DBS_UPDATE_LEFT_COOLTIME_PREMIUM_SLOT, BuildReqIdAck(payload, 8)); return true;
            case SDB_SAVE_2768: return OnItemSingle(link, payload);
            case SDB_EQUIP_ITEM: return OnItemSingle(link, payload, DBS_EQUIP_ITEM, "SDB_EQUIP_ITEM");
            case SDB_SET_QUESTLIST_INFO: return OnSetQuestListInfo(link, payload);
            case SDB_ITEM_TRADE: return OnItemTrade(link, payload);
            case SDB_SAVE_2936: link.SendFrame(DBS_SAVE_2937, BuildDbs2937(payload)); return true;
            case SDB_DAILY_QUEST: link.SendFrame(DBS_DAILY_QUEST, BuildReqIdAck(payload, 16)); return true;
            case SDB_DAILY_QUEST_SEED: link.SendFrame(DBS_DAILY_QUEST_SEED, BuildReqIdAck(payload, 8)); return true;

            // --- T15: per-user writes during play (all echo the LIVE reqId) ---
            case SDB_USER_LEARN_SKILL:            return OnUserLearnSkill(link, payload);
            case SDB_ACCOMPLISH_USER_ACHIEVEMENT: return OnAccomplishUserAchievement(link, payload);
            case SDB_UPDATE_REPUTATION_INFO:        return OnUpdateReputation(link, payload);
            case SDB_ADD_TUTORIAL_SIMPLE_TIP:       return OnAddTutorialTip(link, payload);
            case SDB_DONT_REPEAT_TUTORIAL_SIMPLE_TIP: return OnDontRepeatTutorialTip(link, payload);
            case SDB_UPDATE_SEREN_GUIDE_INFO:       return OnUpdateSerenGuide(link, payload);
            case SDB_ASK_CHANGE_CHAR_NAME:          return OnAskChangeCharName(link, payload);
            case SDB_DO_CHANGE_CHAR_NAME:           return OnDoChangeCharName(link, payload);
            case SA_UPDATE_RANK_USERNAME:           return OnUpdateRankUsername(payload);
            case SA_WATCH_MOVIE:                    return OnWatchMovie(payload);
            case SA_START_CHANGE_APPEARANCE:        return OnStartChangeAppearance(payload);
            case SDB_GIVE_GUILD_MONEY_INCENTIVE:    return OnGiveGuildMoneyIncentive(link, payload);
            case SDB_UPDATE_USER_DAILY_EVENT_COUNT: return OnUpdateUserDailyEventCount(link, payload);   // T156: stored + 0x1591 push
            case SDB_UPDATE_GET_EXTRA_REWARD:       return OnUpdateGetExtraReward(link, payload);        // T156: stored

            // --- T23 ---
            case SDB_RESULT_CITY_WAR:               return OnResultCityWar(link, payload);
            case SDB_LOAD_CITY_GUILD_INFO:          return OnLoadCityGuildInfo(link, payload);

            // --- T156 ---
            case SA_AVAILABLE_EVENT_MATCHING_LIST:              return OnAvailableEventMatchingList(link, payload);
            case SA_LOAD_EVENT_MATCHING_INFO:                   return OnLoadEventMatchingInfo(link);
            case SA_UPDATE_PLAYGUIDE_EXTRA_REWARD_RESETTIME:    return OnUpdateExtraRewardResetTime(link, payload);
            case SA_UPDATE_EVENT_MATCHING_ADD_REWARD_RESETTIME: return OnUpdateAddRewardResetTime(link, payload);
            case SDB_UPDATE_ADDITIONAL_REWARD_RECV_COUNT:       return OnUpdateAdditionalRewardRecvCount(link, payload);

            // --- T33 ---
            case DSA_DUNGEON_TIMELINE_OPEN_INFO:    return OnDungeonTimelineOpenInfo(link, payload);

            // --- T42: warehouse ---
            case SDB_VIEW_WAREHOUSE:          return OnViewWarehouse(link, payload);
            case SDB_STORE_WAREHOUSE:         return OnWarehouseTransfer(link, payload, DBS_STORE_WAREHOUSE,
                                                     WarehouseHandlers.StoreReqDlmId, WarehouseHandlers.StoreReqBinaryRef, WarehouseHandlers.StoreRequestSize);
            case SDB_GET_WAREHOUSE:           return OnWarehouseTransfer(link, payload, DBS_GET_WAREHOUSE,
                                                     WarehouseHandlers.GetReqDlmId, WarehouseHandlers.GetReqBinaryRef, WarehouseHandlers.GetRequestSize);
            case SDB_CHANGE_WAREHOUSE_POS:    return OnWarehouseTransfer(link, payload, DBS_CHANGE_WAREHOUSE_POS,
                                                     WarehouseHandlers.ChangePosReqDlmId, WarehouseHandlers.ChangePosReqBinaryRef, WarehouseHandlers.ChangePosRequestSize);
            case SDB_CLEAR_WAREHOUSE:         return OnClearWarehouse(link, payload);
            case SDB_WAREHOUSE_AUTO_SORT:     return OnWarehouseAutoSort(link, payload);
            case SDB_PAY_WAREHOUSE_COMMISION: return OnPayWarehouseCommision(link, payload);
            case SDB_INCREASE_WAREHOUSE_SIZE: return OnIncreaseWarehouseSize(link, payload);
            case SDB_INCREASE_INVENTORY_SIZE: return OnIncreaseInventorySize(link, payload);
            case SDB_GUILD_LEARN_PERK:
            case SDB_CONDITIONAL_TELEPORT:
            case SDB_ITEM_SIMPLE_ATOM:
            case SDB_MAIN_MENU_COMMAND:
            case SDB_INCREMENT_CHARACTER_LEVEL:
            case SDB_INCREMENT_CHARACTER_LEVEL_JUMP:
            case SDB_INCREMENT_CHARACTER_LEVEL_PERFECT_JUMP: return OnGenericAck(link, op, payload);
            case SDB_ITEM_EXTRACT:
            case SDB_ITEM_ENCHANT:
            case SDB_ITEM_ENCHANT_IDENTIFY:
            case SDB_ITEM_MERGE:
            case SDB_ITEM_UNIDENTIFY:
            case SDB_ENCHANT_ITEM_BOOST:
            case SDB_ITEM_DECOMPOSITION:
            case SDB_ITEM_AWAKEN:
            case SDB_ITEM_UNBIND:
            case SDB_EQUIPMENT_INHERITANCE: return OnItemUpgrade(link, op, payload);   // T166, DbAckGroups Handled

            // --- T45: parcels ---
            case SDB_LIST_PARCEL:             return OnListParcel(link, payload);
            case SDB_MAKE_PARCEL:             return OnMakeParcel(link, payload);
            case SDB_LOAD_PREMIUM_SLOT_LEFT_COOLTIME:
                if (payload.Length < 0x12) { _log.LogWarning("0x28BD: {Len} B payload, want >= 18 - dropped", payload.Length); return true; }
                link.SendFrame(DBS_LOAD_PREMIUM_SLOT_LEFT_COOLTIME,
                    BuildDbsPremiumSlotCooltime(U32(payload, 0x0E))); return true;
            case SDB_RECV_PARCEL:             return OnRecvParcel(link, payload);
            case SDB_RECV_PARCEL_EX:          return OnRecvParcelEx(link, payload);
            case SDB_RETURN_PARCEL:           return OnReturnParcel(link, payload);
            case SDB_DELETE_PARCEL:           return OnDeleteParcel(link, payload);

            // --- T51: guilds ---
            case SDB_INIT_GUILD:              return OnInitGuild(link);

            // --- T55: broker. One handler; the five differ only in which ids they echo. ---
            case SDB_TRADE_BROKER_REGISTER_ITEM:
            case SDB_TRADE_BROKER_UNREGISTER_ITEM:
            case SDB_TRADE_BROKER_CALC_SOLD_ITEM:
            case SDB_TRADE_BROKER_CALC_BOUGHT_ITEM:
            case SDB_TRADE_BROKER_BUY_IT_NOW: return OnTradeBrokerRequest(link, op, payload);

            // --- Post-spawn (reqId at payload[0] for all three) ---
            case SDB_END_START_QUEST_LIST: link.SendFrame(DBS_END_START_QUEST_LIST, BuildReqIdAck(payload, 0)); return true;
            case SDB_SET_TASK_SHOW_TOGGLE: link.SendFrame(DBS_SET_TASK_SHOW_TOGGLE, BuildSetTaskShowToggleReply(payload)); return true;
            case SDB_LOAD_2930:
                link.SendFrame(DBS_LOAD_2931, Build2931(payload, payload.Length >= 16
                    && UserControls.IsHeld(BitConverter.ToUInt32(payload, 12), DateTimeOffset.UtcNow)));
                return true;
            case SDB_USER_VAPORIZED:       OnUserVaporized(payload, _log); return true;
            case SA_CLEAR_BATTLE_FIELD_ENTER_COUNT: link.SendFrame(AS_CLEAR_BATTLE_FIELD_ENTER_COUNT, BuildOkReqId(payload, 8)); return true;
            case SA_UPDATE_BATTLE_FIELD_COOL_TIME: link.SendFrame(AS_UPDATE_BATTLE_FIELD_COOL_TIME, BuildOkReqId(payload, 16)); return true;
            case SDB_UPDATE_USER_ACTPOINT: link.SendFrame(DBS_UPDATE_USER_ACTPOINT, BuildReqIdAck(payload, 0)); return true;
            case SA_LEARN_ALL_CREST_ACQUIRABLE:
            {
                var r = BuildLearnAllCrest(payload);
                if (r == null) return false;
                _log.LogInformation("SA_LEARN_ALL_CREST_ACQUIRABLE: {N} crest(s) granted (empty refusal list)",
                    ReadCrestEntries(payload).Count);
                StoreLearnedCrests(payload);
                link.SendFrame(AS_LEARN_ALL_CREST_ACQUIRABLE, r);
                return true;
            }
            case SA_REQUEST_ENTER_DUNGEON:
            {
                var r = BuildAsRequestEnterDungeon(payload);
                if (r == null) return false;
                _log.LogInformation("SA_REQUEST_ENTER_DUNGEON: player {Pid} -> dungeon/zone {Dg} ({Len} B)",
                    BitConverter.ToUInt32(payload, DungeonCtxPlayerId),
                    BitConverter.ToUInt32(payload, DungeonCtxDungeonId), payload.Length);
                RecordDungeonEntry(payload, response: false);
                // T108: 0x13BF goes to the World that owns the requested continent. With one
                // World - or none announced - that resolves to this link, which is the send
                // this line was before T108.
                int fromWorld = WorldRouting.WorldIdOf(link);
                DungeonRouting.Dispatch(link, fromWorld, DungeonRouting.RouteRequest(
                    DungeonRouting.Channels, DungeonRouting.Transfers, fromWorld, payload, r), _log);
                return true;
            }
            case SA_RESPONSE_ENTER_DUNGEON:
            {
                var r = BuildAsResponseEnterDungeon(payload);
                if (r == null) return false;
                RecordDungeonEntry(payload, response: true);
                // T108: 0x13C1 goes back to the World that sent the 0x13BE for this PDId - the
                // World the user is still in - not to the World that answered.
                int fromWorld = WorldRouting.WorldIdOf(link);
                DungeonRouting.Dispatch(link, fromWorld,
                    DungeonRouting.RouteResponse(DungeonRouting.Transfers, fromWorld, r), _log);
                return true;
            }
            case SA_ENTER_WORLD_FAIL: return OnEnterWorldFail(link, payload);
            case SA_ENTER_WORLD: return OnSaEnterWorld(bridge, payload);
            case SA_CHAR_LOC: Handlers.GmAdminTool.OnSaCharLoc(bridge, payload, _log); return true;
            case SA_BROADCAST_SYSTEM_MESSAGE_TO_WHOLE_WORLD:
            case SA_BROADCAST_SYSTEM_MESSAGE_NOT_IN_SPECIAL_PLACE: return OnWorldBroadcast(bridge, payload);
            case SA_CREST_USE_LIST: return OnCrestUseList(link, payload);
            case SA_SYNC_DATE_TIME: return true;   // DateTimeSync::CheckDateTime only compares clocks
            case SDB_CHANGE_CITY_WAR_STATE: link.SendFrame(DBS_CHANGE_CITY_WAR_STATE, BuildChangeCityWarStateReply(payload)); return true;
            case Handlers.ArbiterClientHandlers.SDB_SIMULATE_ITEM_TOOLTIP:
                return Handlers.ArbiterClientHandlers.OnSimulateItemTooltip(bridge, payload, _log);

            // --- Login-time: empty-list Type 1 [off=19][count=0][reqId][ok=1], reqId at payload[0] ---
            // (0x27A2 inventory is handled by OnLoadInventory above)
            // T147: the two artisan loads are per-character now (see OnLoadItemRecipe).
            case SDB_LOAD_ITEM_RECIPE:          return OnLoadItemRecipe(link, payload);
            case SDB_LOAD_SKILL_PROF:           return OnLoadSkillProf(link, payload);
            case SDB_LEARN_ITEM_RECIPE:         return OnLearnItemRecipe(link, payload);
            case SDB_DELETE_ITEM_RECIPE_LIST:   return OnDeleteItemRecipeList(link, payload);
            case SDB_SET_RECIPE_BOOKMARK:       return OnSetRecipeBookmark(link, payload);
            case SDB_ITEM_PRODUCE_STEP1:        return OnProduceStep1(link, payload);
            case SDB_ITEM_PRODUCE_STEP2:        return OnProduceStep2(link, payload);
            case SDB_UPDATE_SKILL_PROF:         return OnUpdateSkillProf(link, payload);
            case S_UPDATE_PROF_MINERAL:
            case S_UPDATE_PROF_BUG:
            case S_UPDATE_PROF_ENERGY:
            case S_UPDATE_PROF_HERB:            return OnUpdateGatheringProf(link, op, payload);
            case SDB_LOAD_TELEPORT_TO_POS_LIST: link.SendFrame((ushort)(op + 1), BuildEmptyListType1(payload, 0)); return true;
            case SDB_LOAD_ACCOUNT_BENEFIT:      return OnLoadAccountBenefit(link, payload);
            case SDB_LOAD_LEARNED_SOCIAL:       link.SendFrame((ushort)(op + 1), BuildEmptyListType1(payload, 0)); return true;
            case SDB_LOAD_TOKEN_EXCHANGE:       link.SendFrame((ushort)(op + 1), BuildEmptyListType1(payload, 0)); return true;
            case SDB_LOAD_SKILLPERIOD:          link.SendFrame((ushort)(op + 1), BuildEmptyListType1(payload, 0)); return true;
            case SDB_LOAD_SERVANT_PERIOD:       link.SendFrame((ushort)(op + 1), BuildEmptyListType1(payload, 0)); return true;
            case SDB_LOAD_PASSIVITY_COOLTIME:   link.SendFrame((ushort)(op + 1), BuildEmptyListType1(payload, 0)); return true;
            case SDB_LOAD_ADDITIONAL_REWARD:    return OnLoadAdditionalRewardRecvCount(link, payload);   // T156

            // --- Login-time: empty-list Type 2 [off=19][count=0][ok=1][reqId], reqId at payload[0] ---
            case SDB_LOAD_PROMOTION_LIST:       link.SendFrame((ushort)(op + 1), BuildEmptyListType2(payload, 0)); return true;
            case SDB_LOAD_PROMOTION_COND_LIST:  link.SendFrame((ushort)(op + 1), BuildEmptyListType2(payload, 0)); return true;
            case SDB_LOAD_BATTLE_FIELD_LIST:
                link.SendFrame((ushort)(op + 1), Handlers.QaDungeonCommands.BuildBattlefieldLoad(payload,
                    _store?.GetNativeBattlefieldResults(Ep32i(payload, 4)) ?? Array.Empty<byte[]>())); return true;
            case SDB_LOAD_USER_RESTRICTION:     link.SendFrame((ushort)(op + 1), BuildEmptyListType2(payload, 0)); return true;

            // --- SA_ shape: reqId at payload[8] (after u64 gameId) ---
            case SA_LOAD_BATTLE_FIELD_COOL_TIME:   link.SendFrame(0x1522, BuildEmptyListType2(payload, 8)); return true;
            case SA_LOAD_BATTLE_FIELD_ENTER_COUNT: link.SendFrame(0x155E, BuildBattleFieldEnterCount(payload)); return true;
            // Servant data handlers — empty servant/pet state
            case SA_LOAD_SERVANT_DATA:             link.SendFrame(0x153A, BuildServantData(payload)); return true;
            case SA_LOAD_SERVANT_ADVENTURE_DATA:   link.SendFrame(0x153C, BuildServantAdventureData(payload)); return true;
            case SA_LOAD_SERVANT_STORAGE_DATA:     link.SendFrame(0x1538, BuildServantStorageData(payload)); return true;
            case SA_LOAD_SERVANT_AUTO_POTION_DATA: link.SendFrame(0x1530, BuildServantAutoPotionData(payload)); return true;
            case SA_LOAD_SERVANT_AUTO_FEED_DATA:   link.SendFrame(0x1534, BuildServantAutoFeedData(payload)); return true;
            case SA_PET_LOAD:                      link.SendFrame(0x1416, BuildPetLoad(payload)); return true;
            case SA_LOAD_EXTRAPOINT_DATA:           link.SendFrame(0x1555, BuildExtrapointData(
                payload, _store?.GetCharacterEp(Ep32i(payload, 4)))); return true;

            // --- Other login-time handlers ---
            case SDB_LOAD_QUEST_PROGRESS: link.SendFrame(0x2903, BuildQuestProgress(payload)); return true;
            case SDB_LOAD_ACHIEVE_LIST: return Handlers.QaPurchaseCommands.Handle(_store, bridge, link, op, payload);
            case SDB_USER_CLEAR_ALL_SKILL: return OnUserClearAllSkill(link, payload);
            case SDB_UPDATE_EVENTSYSTEM_PROGRESS: return OnUpdateEventSystemProgress(link, payload);
            case SDB_LOAD_WORLD_EVENT:    return OnUpdateDailyLimitEpExp(link, payload);
            case SDB_LOAD_FRIEND_INFO:    return OnUpdateFatigability(link, payload);

            // --- Remaining login-time: programmatic builders ---
            case SDB_LOAD_2867: return OnLoadDungeonCoolTime(link, payload);
            case GameLogPackets.SDB_ITEM_TRADE_LOG:
                return FileGameLog(GameLogPackets.ParseItemTradeLog(payload), "SDB_ITEM_TRADE_LOG");
            case GameLogPackets.SDB_ADD_PVP_USER_LOG:
                return FileGameLog(GameLogPackets.ParseDuelUserLog(payload, "pvp.kill"),
                    "SDB_ADD_PVP_USER_LOG");
            case GameLogPackets.SDB_ADD_PK_USER_LOG:
                return FileGameLog(GameLogPackets.ParseDuelUserLog(payload, "pk.kill"),
                    "SDB_ADD_PK_USER_LOG");
            case GameLogPackets.SDB_ADD_GROUP_DUEL_USER_LOG:
                return FileGameLog(GameLogPackets.ParseGroupDuelUserLog(payload),
                    "SDB_ADD_GROUP_DUEL_USER_LOG");
            case GameLogPackets.SDB_CASH_ITEM_LOG:
                return FileGameLog(GameLogPackets.ParseCashItemLog(payload), "SDB_CASH_ITEM_LOG");
            case BattlegroundResults.BSA_END_BATTLE_FIELD_RESULT_LIST:
                return BattlegroundResults.FileResults(_store, payload);
            case BattlegroundResults.BSA_UPDATE_BATTLE_FIELD_LOG:
                return BattlegroundResults.FileScore(_store, payload);
            case DungeonChannels.SA_ADD_DUNGEON_CHANNEL:    return OnAddDungeonChannel(link, payload);
            case DungeonChannels.SA_REMOVE_DUNGEON_CHANNEL: return OnRemoveDungeonChannel(link, payload);
            case SA_UPDATE_DUNGEON_COOLTIME:    return OnUpdateDungeonCoolTime(payload);
            case SA_UPDATE_DUNGEON_CLEAR_COUNT: return OnUpdateDungeonClearCount(payload);
            case SA_DELETE_DUNGEON_COOLTIME:    return OnDeleteDungeonCoolTime(payload);
            case SDB_LOAD_2869:
            {
                uint pid = payload.Length >= 8 ? BitConverter.ToUInt32(payload, 4) : 0;
                var phase = _store?.GetDungeonPhases((int)pid);
                if (phase == null)
                {
                    // First observation retains the captured initial reset, then persists its
                    // epoch. Repeated logins must not erase QA phaselevel's saved rows.
                    long epoch = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    epoch = _store?.InitializeDungeonPhaseReset((int)pid, epoch) ?? epoch;
                    phase = new CharacterStore.DungeonPhaseState(epoch, Array.Empty<byte[]>());
                    link.SendFrame(AS_REQUEST_DUNGEON_PHASE_USER_RESET, Build15E0(pid, (ulong)epoch));
                }
                link.SendFrame(DBS_LOAD_DUNGEON_PHASE_LEVEL, Handlers.QaDungeonCommands.BuildPhaseLoad(payload, phase));
                return true;
            }
            case SDB_LOAD_2900: link.SendFrame(0x2901, Build2901_TwoEmptyLists(payload)); return true;
            // 0x28B7 / 0x28B0 answered byte-exact by the T69 cases above (Build28B6 / Build28B1_ReferAFriend retired).
            case SDB_LOAD_2975: return OnLoadSkillPolishing(link, payload);   // T167
            case SDB_LOAD_2986: return OnRequestCardData(link, payload);      // T167
            case SDB_LOAD_290C:
            {
                // Real Arbiter (lobby_tap.log 02:51:10.846-.870) pushes, in this order:
                //   0x15B1 AS_ACQUIRE_FRIENDSHIP_GAGE, 0x2847, 0x1440 AS_RESET_FIELD_POINT_COMPLETE,
                //   0x143E AS_USER_FIELD_POINT_INFO  (World answers 0x143F, no reply needed)
                // then 0x290D [01][reqId] (02:51:11.173: 01 3E 00 00 00).
                uint pid = payload.Length >= 8 ? BitConverter.ToUInt32(payload, 4) : 0;
                link.SendFrame(0x15B1, Build15B1(pid));
                link.SendFrame(0x2847, Build2847(pid));
                link.SendFrame(0x1440, Build1440(pid));
                link.SendFrame(0x143E, Build143E(pid));
                link.SendFrame(0x290D, BuildOkReqId(payload, 0));
                return true;
            }

            // --- Remaining login-time: static-data handlers (clone template, patch reqId) ---
            case SDB_TUTORIAL_SIMPLE_TIP: return OnLoadTutorialTips(link, payload);
            case SDB_REPUTATION_LIST:     return OnLoadReputationList(link, payload);
            case SDB_LOAD_293A:           return OnLoadUserDailyEvent(link, payload);   // T156: from daily_event
            case SDB_FATIGABILITY_LIST:   return OnLoadFatigability(link, payload);
            case SDB_SEREN_GUIDE:         return OnLoadSerenGuide(link, payload);
            case SDB_EP_PERK:             return OnLoadEpPerk(link, payload);   // T167: from the store, was the captured template
            case SDB_QUEST_LIST:          return OnLoadQuestList(link, payload);
            case SDB_USER_ACHIEVEMENT:    return OnLoadUserAchievement(link, payload);

            default:
                if (DbAckTable.Covers(op)) return OnGenericAck(link, op, payload);   // T165 group A
                // IsHandledRequest said yes and there is no case for it: the request now falls
                // through to the replay table, which answers with the CAPTURED DLM id. That is
                // a wedge (status/HANDOFF.md section 1), so say so rather than failing quietly.
                _log.LogError("DbProxy: {Op} is allow-listed but has no handler - falling through "
                    + "to the replay table, which will answer with a stale DLM id and head-block this user",
                    DbProxyOpcodeNames.Describe(op));
                return false;
        }
    }

    // ---- Pure reply builders (unit-tested against captured bytes) ----

    /// <summary>Generic "[u32 reqId][u8 ok]" ack (DBS_SAVE_27FB, DBS_SAVE_2925, DBS_DAILY_QUEST).</summary>

    // =====================================================================================
    // T77: cap_social4.log. Fifteen request/reply pairs the replay table was carrying and two
    // one-way writes. Every one of the fifteen is a per-user DLMItem unless noted, so an
    // unanswered one head-blocks that character (status/HANDOFF.md section 1); the replay table
    // could only ever hand them the DlmId of whoever was logged in when the tap was recorded.
    //
    // Layouts are the PDL dumpers', cross-checked against the captured bytes. Eight of the
    // replies are the plain [i32 DlmId][u8 Success] ack; the other seven are listed below.
    // =====================================================================================

    // ---- EP (elite points). /@perfect_level walks the whole family in one go. ----
    /// <summary>0x27AF, guard 0x19: `DlmId@06, OwnerDBID@0A, ReserveBonus@0E, ResetTime@12 (i64)`.</summary>
    public const ushort SDB_UPDATE_DAILY_EXTRA_POINT = 0x27AF;
    public const ushort DBS_UPDATE_DAILY_EXTRA_POINT = 0x27B0;
    /// <summary>0x27B1, guard 0x29: `DlmId@06, OwnerDBID@0A, NewEpExp@0E (i64), NewEpLevel@16,
    /// NewEpPoint@1A, NewDailyEpExp@1E, NewReserveBonus@22, NewDailyLimit@26`. This is the one
    /// that carries the numbers the EP panel shows.</summary>
    public const ushort SDB_UPDATE_EXTRA_POINT = 0x27B1;
    public const ushort DBS_UPDATE_EXTRA_POINT = 0x27B2;
    /// <summary>0x27BB, guard 0x19: `EpPerkList ref@06, DlmId@0E, UserDbId@12, UseEpPoint@16`.</summary>
    public const ushort SDB_USER_LEARN_EP_PERK = 0x27BB;
    public const ushort DBS_USER_LEARN_EP_PERK = 0x27BC;
    /// <summary>0x27BD, guard 0x16: `ItemBinary ref@06, DlmId@0E, UserDbId@12, IsByItem@16 (u8)`.
    /// Its reply is NOT the plain ack - see <see cref="BuildDbsResetEpPerk"/>.</summary>
    public const ushort SDB_USER_RESET_EP_PERK = 0x27BD;
    public const ushort DBS_USER_RESET_EP_PERK = 0x27BE;
    /// <summary>0x27C1, guard 0x15: `DlmId@06, UserDbId@0A, EpLevel@0E, EpPoint@12`.</summary>
    public const ushort SDB_UPDATE_PRE_EP_INFO = 0x27C1;
    public const ushort DBS_UPDATE_PRE_EP_INFO = 0x27C2;

    /// <summary>Payload bytes before the atom list in SDB_USER_RESET_EP_PERK (frame 0x17).</summary>
    public const int ResetEpPerkRequestHeader = 17;
    /// <summary>...and in its reply (frame 0x13).</summary>
    public const int ResetEpPerkReplyHeader = 13;

    // ---- collection cards ----
    /// <summary>0x2988, guard 0x19: `DlmId@06, AccountDbId@0A (i64), CardTemplateId@12, Amount@16`.</summary>
    public const ushort SDB_REGISTER_CARD = 0x2988;
    public const ushort DBS_REGISTER_CARD = 0x2989;
    /// <summary>0x298A / 0x298C, guard 0x1D: `DlmId@06, AccountDbId@0A (i64), UserDbId@12,
    /// PresetIndex@16, CardTemplateId@1A`.</summary>
    public const ushort SDB_MOUNT_CARD = 0x298A;
    public const ushort DBS_MOUNT_CARD = 0x298B;
    public const ushort SDB_UNMOUNT_CARD = 0x298C;
    public const ushort DBS_UNMOUNT_CARD = 0x298D;

    // ---- the rest ----
    /// <summary>0x2893, guard 0x19: `DlmId@06, NpcGuildId@0E, InitTime@12 (i64)`.</summary>
    public const ushort SDB_INIT_LIMIT_REMAIN_REPUTATION = 0x2893;
    public const ushort DBS_INIT_LIMIT_REMAIN_REPUTATION = 0x2894;
    /// <summary>0x28A9, guard 0x0D. World sends it SHORT - six bytes, no payload at all, in both
    /// captured runs - and the real Arbiter answers anyway. Its reply carries no DlmId.</summary>
    public const ushort SDB_LOAD_FEUDAL_LORD_FLAG = 0x28A9;
    public const ushort DBS_LOAD_FEUDAL_LORD_FLAG = 0x28AA;
    /// <summary>0x29C0, guard 0x11. Also arrives six bytes short of its own guard; its reply
    /// carries no DlmId either, only the season window.</summary>
    public const ushort SDB_TBA_REQUEST_BATTLEPASS_SEASONDATA = 0x29C0;
    public const ushort DBS_TBA_UPDATE_BATTLEPASS_SEASONDATA = 0x29C1;
    /// <summary>0x1465, guard 0x21: `OwnerBinary ref@06, ArbiterUser@0E (i64), DlmId@16,
    /// NewPoint@1A, NewExPoint@1E`. The guild crest point write.</summary>
    public const ushort SA_CREST_POINT = 0x1465;
    public const ushort AS_CREST_POINT = 0x1466;
    /// <summary>T158. 0x1469 SA_CREST_USE, guard 0x17: <c>ArbiterUser@06 (i64), DlmId@0E,
    /// CrestId@12, Apply@16 (u8)</c> - a glyph switched on or off. Handler_SA_CREST_USE runs
    /// User::UpdateUserCrestApplyNoLock (spUpdateCrestUse) and answers AS_CREST_USE
    /// <c>[DlmId][bool]</c>, which World's Handler_AS_CREST_USE reads at @06 / @0A. It is a DLM
    /// request: unanswered it shuts the user's DB queue. cap_play1 13074 (test, 05:37:19) is the
    /// last DB frame World ever sent for player 10 - the gathering item, fatigue, mail list and
    /// periodic saves after it were all queued behind it.</summary>
    public const ushort SA_CREST_USE = 0x1469;
    public const ushort AS_CREST_USE = 0x146A;
    public const int CrestUseReqDlmId = 8, CrestUseReqCrestId = 12, CrestUseReqApply = 16;
    /// <summary>T158. 0x27A5 SDB_PEGASUS_FEE: <c>[ref@0 -> one 568-byte give/take record]
    /// [DlmId@8][UserDbId@12]</c>. The record is a TS_CHANGE_MONEY (op 9) of minus the route's
    /// fee - cap_play1 31914 carries -1000, the 1000 the flight list (0x538e) showed. World's
    /// Handler_DBS_PEGASUS_FEE reads DlmId@06 and the ok byte @0A; unanswered, the flight never
    /// starts and the user's queue stays shut (31996 / 32127: two more C_RIDE_PEGASUS, no second
    /// fee request).</summary>
    public const ushort SDB_PEGASUS_FEE = 0x27A5;
    public const ushort DBS_PEGASUS_FEE = 0x27A6;
    public const int PegasusFeeReqDlmId = 8, PegasusFeeReqUserDbId = 12;
    /// <summary>T164. 0x28AE SDB_MARK_AS_QUEST_COMPLETED: <c>[ref @6 -> i32 quest ids][DlmId @0E]
    /// [UserDbId @12]</c>, frame &gt;= 0x16. The Arbiter (ArbiterServer.exe.c:1278022) runs every id
    /// not yet complete through dbo.spInsertQuestComplete and answers 0x28AF
    /// <c>[ref @6 -> the ids it newly completed][DlmId @0E]</c>; World's reader
    /// (WorldServer.exe.c:3019867, frame &gt;= 0x12) applies the list and calls it a success only
    /// when it is non-empty. World sends it at the end of a perfect level jump
    /// (DBIncrementCharacterLevelPerfectJump::ExecuteCommitSQL, WorldServer.exe.c:1130501).
    /// cap_scroll 18110 (player 11, dlm 0x33D) carries an EMPTY list; unanswered, it was the
    /// last DB request that user ever got an answer to.</summary>
    public const ushort SDB_MARK_AS_QUEST_COMPLETED = 0x28AE;
    public const ushort DBS_MARK_AS_QUEST_COMPLETED = 0x28AF;
    public const int MarkQuestReqDlmId = 8, MarkQuestReqUserDbId = 12, MarkQuestReqFixed = 16, MarkQuestReplyFixed = 12;
    /// <summary>T164. 0x2790 SDB_USER_LEARN_SKILL_FOR_MULTIPLE, 0x278E's list form
    /// (ArbiterServer.exe.c:1563583, frame &gt;= 0x30): <c>[ref @6 -> 856-byte atoms]
    /// [ref @0E -> 8-byte {skillId, flag} pairs][DlmId @16][UserDbId @1A][skillId @1E]
    /// [u8 flag @22][u32 @23][u8 @27][i32 period @28][i32 period @2C]</c>. Reply 0x2791
    /// (writer FUN_1408e7ca0): <c>[ref @6 atoms][ref @0E SkillPeriodData, 0x18 each]
    /// [ref @16 {skillId, result} pairs][DlmId @1E][u8 ok @22][u8 hasPeriods @23]
    /// [u8 alreadyLearned @24]</c> = a 31-byte fixed part, lists in that order behind it; World's
    /// reader (WorldServer.exe.c:3027355, frame &gt;= 0x25) reads the second and third lists, the
    /// DlmId and the three bytes. cap_scroll 14359: caludesucks (9) learns 111110 with flag 1,
    /// two atoms (op 6 + 11) using up item 1100 (template 70, bag slot 14), no pairs.</summary>
    public const ushort SDB_USER_LEARN_SKILL_FOR_MULTIPLE = 0x2790;
    public const ushort DBS_USER_LEARN_SKILL_FOR_MULTIPLE = 0x2791;
    public const int LearnMultiReqPairs = 8, LearnMultiReqDlmId = 16, LearnMultiReqUserDbId = 20,
                     LearnMultiReqSkillId = 24, LearnMultiReqFixed = 42, LearnMultiReplyFixed = 31;
    /// <summary>0x1479, guard 0x2A: two wstr refs (Writer, Title) then `DlmId@1A,
    /// ReceiverDbId@1E, SendMoney@22 (i64), ForceNotShowMessage@2A (u8)`. System mail - the
    /// levelling rewards in this capture.</summary>
    public const ushort SA_MAKE_SYS_PARCEL = 0x1479;
    public const ushort AS_MAKE_SYS_PARCEL = 0x147A;

    /// <summary>
    /// SDB_USER_FORGET_SKILL (0x2792) -&gt; DBS_USER_FORGET_SKILL (0x2793). Unlearning one skill.
    /// Six pairs in cap_social4.log (seq 5803, 5808, 5815, ...) and not one of them was answered,
    /// so the account's DB queue head-blocked on the first one every time a skill was forgotten.
    ///
    /// <para>Request, dumper guard 0x12 (Arb_part_018.c:459):
    /// <c>DlmId@06, UserDbId@0A, SkillTemplateId@0E, IsActive@12 (u8)</c> - 19 bytes.
    /// Reply, guard 0x13 (Arb_part_016.c:202):
    /// <c>SkillPeriodList ref@06, DlmId@0E, Success@12 (u8), DeletedSkillPeriod@13 (u8)</c> -
    /// 20 bytes, and the list is empty in all six: <c>14 00 00 00 00 00 00 00 &lt;dlm&gt; 01 00</c>,
    /// offset = frame length, count 0.</para>
    ///
    /// <para>The SkillPeriodList is the timed (rented) skills the refund would return, and
    /// <c>DeletedSkillPeriod</c> says whether one was consumed. TeraSharp models neither timed
    /// skills nor the learned-skill set, so the honest answer is the one the capture shows for a
    /// permanent skill: empty list, DeletedSkillPeriod 0.</para>
    /// </summary>
    public const ushort SDB_USER_FORGET_SKILL = 0x2792;
    public const ushort DBS_USER_FORGET_SKILL = 0x2793;

    /// <summary>DBS_USER_FORGET_SKILL (0x2793), frame 0x14 - cap_social4.log seq 5804.</summary>
    public static byte[] BuildDbsUserForgetSkill(uint dlmId, bool ok = true, bool deletedPeriod = false)
    {
        var p = new byte[14];
        BitConverter.GetBytes(20u).CopyTo(p, 0);        // offset = frame length, count 0
        BitConverter.GetBytes(dlmId).CopyTo(p, 8);
        p[12] = (byte)(ok ? 1 : 0);
        p[13] = (byte)(deletedPeriod ? 1 : 0);
        return p;
    }

    /// <summary>DBS_INIT_LIMIT_REMAIN_REPUTATION (0x2894), frame 0x13. Success comes FIRST here,
    /// ahead of the DlmId - the one reply in this batch that does.
    /// `Success@06 (u8), DlmId@07, RemainPoint@0B, HuntingRemainPoint@0F`.</summary>
    public static byte[] BuildDbsInitLimitRemainReputation(uint dlmId, int remainPoint, int huntingRemainPoint)
    {
        var p = new byte[13];
        p[0] = 1;
        BitConverter.GetBytes(dlmId).CopyTo(p, 1);
        BitConverter.GetBytes(remainPoint).CopyTo(p, 5);
        BitConverter.GetBytes(huntingRemainPoint).CopyTo(p, 9);
        return p;
    }

    /// <summary>The captured RemainPoint: 30000, with HuntingRemainPoint 0 (cap_social4.log).</summary>
    public const int DefaultRemainReputation = 30000;

    /// <summary>DBS_LOAD_FEUDAL_LORD_FLAG (0x28AA), frame 0x0E: one empty MemberList, written with
    /// the offset = frame length convention.</summary>
    public static byte[] BuildDbsFeudalLordFlag()
    {
        var p = new byte[8];
        BitConverter.GetBytes(14u).CopyTo(p, 0);
        return p;
    }

    /// <summary>DBS_TBA_UPDATE_BATTLEPASS_SEASONDATA (0x29C1), frame 0x21:
    /// `SeasonId@06, SeasonStartDate@0A (i64), SeasonEndDate@12 (i64), ShopOffDate@1A (i64)`.
    /// No season is running, which is the all-zero form the capture carries.</summary>
    public static byte[] BuildDbsBattlePassSeasonData(int seasonId = 0, long start = 0, long end = 0, long shopOff = 0)
    {
        var p = new byte[28];
        BitConverter.GetBytes(seasonId).CopyTo(p, 0);
        BitConverter.GetBytes(start).CopyTo(p, 4);
        BitConverter.GetBytes(end).CopyTo(p, 12);
        BitConverter.GetBytes(shopOff).CopyTo(p, 20);
        return p;
    }

    /// <summary>DBS_REGISTER_CARD (0x2989), frame 0x13 (cap_2man_b15075):
    /// `DlmId@06, Success@0A (u8), CardTemplateId@0B, Amount@0F`.</summary>
    public static byte[] BuildDbsRegisterCard(uint dlmId, int cardTemplateId, int amount)
        => CardReply(dlmId, cardTemplateId, amount);

    /// <summary>DBS_MOUNT_CARD (0x298B) and DBS_UNMOUNT_CARD (0x298D), same frame 0x13:
    /// `DlmId@06, Success@0A (u8), PresetIndex@0B, CardTemplateId@0F`. Note the order is the
    /// mirror of the register reply's - preset first, card second.</summary>
    public static byte[] BuildDbsMountCard(uint dlmId, int presetIndex, int cardTemplateId)
        => CardReply(dlmId, presetIndex, cardTemplateId);

    private static byte[] CardReply(uint dlmId, int first, int second)
    {
        var p = new byte[13];
        BitConverter.GetBytes(dlmId).CopyTo(p, 0);
        p[4] = 1;
        BitConverter.GetBytes(first).CopyTo(p, 5);
        BitConverter.GetBytes(second).CopyTo(p, 9);
        return p;
    }

    /// <summary>AS_CREST_POINT (0x1466), frame 0x13:
    /// `OwnerBinary ref@06, DlmId@0E, Success@12 (u8)`. The binary comes back empty.</summary>
    public static byte[] BuildAsCrestPoint(uint dlmId)
    {
        var p = new byte[13];
        BitConverter.GetBytes(19u).CopyTo(p, 0);        // offset = frame length, count 0
        BitConverter.GetBytes(dlmId).CopyTo(p, 8);
        p[12] = 1;
        return p;
    }

    /// <summary>AS_MAKE_SYS_PARCEL (0x147A), frame 0x0E: `DlmId@06, ParcelErrorNo@0A`.
    /// Zero is "sent".</summary>
    public static byte[] BuildAsMakeSysParcel(uint dlmId, int errorNo = 0)
    {
        var p = new byte[8];
        BitConverter.GetBytes(dlmId).CopyTo(p, 0);
        BitConverter.GetBytes(errorNo).CopyTo(p, 4);
        return p;
    }

    /// <summary>DBS_USER_RESET_EP_PERK (0x27BE), frame 0x13:
    /// `ItemBinary ref@06, DlmId@0E, Success@12 (u8)`, then the request's atoms echoed. The
    /// request's own header is four bytes longer (it has IsByItem), which is why the reply is
    /// four bytes shorter than the request in the capture: 875 against 879.</summary>
    public static byte[] BuildDbsResetEpPerk(uint dlmId, bool ok, byte[]? atoms)
    {
        var body = atoms ?? Array.Empty<byte>();
        var p = new byte[ResetEpPerkReplyHeader + body.Length];
        BitConverter.GetBytes((uint)(6 + ResetEpPerkReplyHeader)).CopyTo(p, 0);
        BitConverter.GetBytes((uint)body.Length).CopyTo(p, 4);
        BitConverter.GetBytes(dlmId).CopyTo(p, 8);
        p[12] = (byte)(ok ? 1 : 0);
        body.CopyTo(p, ResetEpPerkReplyHeader);
        return p;
    }


    /// <summary>
    /// T83. SA_CREST_POINT (0x1465), guard 0x21: <c>OwnerBinary ref@06, ArbiterUser@0E (i64),
    /// DlmId@16, NewPoint@1A, NewExPoint@1E</c> - payload 16, 20 and 24. The owner is the live
    /// session behind the ArbiterUser handle, the same lookup the dungeon writes use.
    /// </summary>
    private void StoreCrestPoints(byte[] payload)
    {
        if (_store is null || payload.Length < 28) return;
        ulong gameId = BitConverter.ToUInt64(payload, 8);
        int playerId = PlayerIdForGameId?.Invoke(gameId) ?? 0;
        if (playerId <= 0)
        {
            _log.LogWarning("SA_CREST_POINT: no live session owns gameId 0x{G:X} - points not stored", gameId);
            return;
        }
        int point = Ep32i(payload, 20), exPoint = Ep32i(payload, 24);
        _store.SetCrestPoints(playerId, point, exPoint);
        _log.LogInformation("SA_CREST_POINT: player {Pid} -> {P} point(s), {E} ex", playerId, point, exPoint);
    }

    /// <summary>
    /// T83. The learned-crest ids, read back out of the reply <see cref="BuildLearnAllCrest"/>
    /// just built - that reply IS the list of crests we granted, in the 16-byte
    /// <c>[u32 thisOff][u32 nextOff][i32 crestId][i32 value]</c> shape, so parsing it needs no
    /// second copy of the request walk. Reply header:
    /// <c>[u32 count][u32 firstOff][u32 reqId][u8 ok]</c>, offsets frame-relative.
    /// </summary>
    private void StoreLearnedCrests(byte[] request)
    {
        if (_store is null || request.Length < 16) return;
        ulong gameId = BitConverter.ToUInt64(request, 8);
        int playerId = PlayerIdForGameId?.Invoke(gameId) ?? 0;
        if (playerId <= 0)
        {
            _log.LogWarning("SA_LEARN_ALL_CREST_ACQUIRABLE: no live session owns gameId 0x{G:X} - "
                + "the crests are not stored", gameId);
            return;
        }

        // T151: from the REQUEST - the reply is now empty (see BuildLearnAllCrest).
        int stored = 0;
        foreach (var (crestId, value) in ReadCrestEntries(request))
            if (crestId != 0 && _store.AddCrest(playerId, crestId, value)) stored++;
        if (stored > 0)
            _log.LogInformation("SA_LEARN_ALL_CREST_ACQUIRABLE: player {Pid} learned {N} new crest(s)",
                playerId, stored);
    }

    /// <summary>The 16-byte entry SA_/AS_LEARN_ALL_CREST_ACQUIRABLE chains.</summary>
    public const int CrestEntrySize = 16;
    /// <summary>A walk bound, so a malformed next-pointer cannot loop.</summary>
    public const int CrestEntryMax = 512;

    /// <summary>A u32 at a PAYLOAD offset, 0 when the frame is short. The T77 family arrives
    /// short often enough - two of the fifteen are shorter than their own handler's guard in
    /// every captured run - that reading past the end has to be impossible rather than unlikely.</summary>
    private static uint Ep32(byte[] p, int at)
        => at >= 0 && at + 4 <= p.Length ? BitConverter.ToUInt32(p, at) : 0u;

    private static int Ep32i(byte[] p, int at)
        => at >= 0 && at + 4 <= p.Length ? BitConverter.ToInt32(p, at) : 0;

    private static long Ep64(byte[] p, int at)
        => at >= 0 && at + 8 <= p.Length ? BitConverter.ToInt64(p, at) : 0L;

    /// <summary>
    /// SDB_UPDATE_EXTRA_POINT (0x27B1) -&gt; DBS (0x27B2). The EP panel's numbers, and the one
    /// frame in the family worth persisting: T70 made AS_LOAD_EXTRAPOINT_DATA (0x1555) send the
    /// character's EP back on login, and until now it had nothing but zeros to send.
    /// </summary>
    private bool OnUpdateExtraPoint(WorldLink link, byte[] payload)
    {
        int owner = Ep32i(payload, 4);
        var ep = new CharacterStore.EpRow(
            Ep64(payload, 8), Ep32i(payload, 16), Ep32i(payload, 20),
            Ep32i(payload, 24), Ep32i(payload, 28), Ep32i(payload, 32));
        bool ok = _store?.SetCharacterEp(owner, ep) ?? true;
        _log.LogInformation("SDB_UPDATE_EXTRA_POINT: player {Owner} -> level {Lv}, {Exp} exp, {Pt} point(s)",
            owner, ep.EpLevel, ep.EpExp, ep.EpPoint);
        link.SendFrame(DBS_UPDATE_EXTRA_POINT, DlmOk(Ep32(payload, 0), ok));
        return true;
    }

    /// <summary>SDB_UPDATE_PRE_EP_INFO (0x27C1) -&gt; DBS (0x27C2): previous-login counters
    /// belong to User, not Account progress (Arb_part_030:13049-13112).</summary>
    private bool OnUpdatePreEpInfo(WorldLink link, byte[] payload)
    {
        int owner = Ep32i(payload, 4);
        _store?.SetEpPre(owner, Ep32i(payload, 8), Ep32i(payload, 12));   // T167: DBS_USER_LOAD_EP_PERK's PreEp pair
        link.SendFrame(DBS_UPDATE_PRE_EP_INFO, BuildReqIdAck(payload, 0));
        return true;
    }

    private bool OnUpdateDailyLimitEpExp(WorldLink link, byte[] payload)
    {
        // Arb_part_064:11154-11156; cap_final2b 1012->1013. This was only acknowledged.
        bool ok = _store?.SetCharacterEpDailyLimit(Ep32i(payload, 4), Ep32i(payload, 8)) ?? true;
        link.SendFrame(0x27B4, DlmOk(Ep32(payload, 0), ok));
        return true;
    }

    /// <summary>SDB_UPDATE_DAILY_EXTRA_POINT (0x27AF) -&gt; DBS (0x27B0): the daily reserve bonus
    /// and the time it resets.</summary>
    private bool OnUpdateDailyExtraPoint(WorldLink link, byte[] payload)
    {
        int owner = Ep32i(payload, 4);
        bool ok = _store?.SetCharacterEpDaily(owner, Ep32i(payload, 8), Ep64(payload, 12)) ?? true;
        link.SendFrame(DBS_UPDATE_DAILY_EXTRA_POINT, DlmOk(Ep32(payload, 0), ok));
        return true;
    }

    /// <summary>
    /// SDB_USER_RESET_EP_PERK (0x27BD) -&gt; DBS (0x27BE). Unlearning the whole perk tree: the
    /// request carries the refund as ItemTransactionAtoms and the reply echoes them, the same
    /// rule SDB_ITEM_SINGLE follows. Request header 17 bytes, reply header 13 - which is exactly
    /// the four-byte difference between the captured 879 and 875.
    /// </summary>
    private bool OnResetEpPerk(WorldLink link, byte[] payload)
    {
        uint dlmId = Ep32(payload, 8);
        int owner = Ep32i(payload, 12);
        byte[] atoms = Array.Empty<byte>();
        if (_store is not null)
        {
            var cloned = WarehouseHandlers.CloneAtomsWithIds(
                payload, 0, ResetEpPerkRequestHeader, _store.NextItemId);
            atoms = cloned.Atoms;
            WarehouseHandlers.Apply(_store, cloned.Parsed, _store.NextItemId, _log);
            _store.ResetEpPerks(owner);   // T167: the current page empties, used EP back to 0
        }
        _log.LogInformation("SDB_USER_RESET_EP_PERK: player {Owner} reset their perks ({N} atom(s))",
            owner, atoms.Length / ItemAtomSize);
        link.SendFrame(DBS_USER_RESET_EP_PERK, BuildDbsResetEpPerk(dlmId, ok: true, atoms));
        return true;
    }

    /// <summary>
    /// SA_MAKE_SYS_PARCEL (0x1479) -&gt; AS_MAKE_SYS_PARCEL (0x147A). System mail - in this
    /// capture the level-up reward parcels. It goes through the same parcels table a player
    /// parcel does, so it survives a relog and shows up in the inbox T61 fixed.
    /// </summary>
    /// <summary>SDB_PEGASUS_FEE -&gt; DBS_PEGASUS_FEE. T158: the fee is charged to the stored
    /// money the way any TS_CHANGE_MONEY is, then the flight is confirmed.</summary>
    private bool OnPegasusFee(WorldLink link, byte[] payload)
    {
        var applied = _store is null ? default : ApplyPegasusFee(_store, payload, _log);
        _log.LogInformation("SDB_PEGASUS_FEE: player {Pid} charged {Fee}",
            Ep32(payload, PegasusFeeReqUserDbId), -applied.CharacterMoneyDelta);
        link.SendFrame(DBS_PEGASUS_FEE, BuildReqIdAck(payload, PegasusFeeReqDlmId));
        return true;
    }

    /// <summary>The fee record, applied at the 568-byte give/take stride.</summary>
    public static WarehouseHandlers.ApplyResult ApplyPegasusFee(CharacterStore store, byte[] payload, ILogger? log = null)
        => WarehouseHandlers.Apply(store, WarehouseHandlers.ParseAtoms(payload, 0, ItemGiveTakeSize), store.NextItemId, log);

    /// <summary>SDB_MARK_AS_QUEST_COMPLETED -&gt; DBS_MARK_AS_QUEST_COMPLETED. T164: each quest is
    /// stored complete (so 0x272D serves it after a relog) and the reply lists only the new ones.</summary>
    private bool OnMarkAsQuestCompleted(WorldLink link, byte[] payload)
    {
        int user = Ep32i(payload, MarkQuestReqUserDbId);
        var ids = ReadI32List(payload, 0, MarkQuestReqFixed);
        var fresh = new List<int>();
        if (_store is not null)
            foreach (int q in ids)
                if (_store.MarkQuestCompleted(user, q)) fresh.Add(q);
        _log.LogInformation("SDB_MARK_AS_QUEST_COMPLETED: player {Pid} {N} quest(s) [{Ids}], {F} newly complete",
            user, ids.Count, string.Join(",", ids), fresh.Count);
        link.SendFrame(DBS_MARK_AS_QUEST_COMPLETED, BuildDbs28AF(Ep32(payload, MarkQuestReqDlmId), fresh));
        return true;
    }

    /// <summary>DBS_MARK_AS_QUEST_COMPLETED: <c>[ref][DlmId]</c> then the ids. An empty list's
    /// offset is the end of the fixed part, which is also the end of the frame.</summary>
    public static byte[] BuildDbs28AF(uint dlmId, IReadOnlyList<int> completed)
    {
        ArgumentNullException.ThrowIfNull(completed);
        var r = new byte[MarkQuestReplyFixed + completed.Count * 4];
        BitConverter.GetBytes((uint)(6 + MarkQuestReplyFixed)).CopyTo(r, 0);
        BitConverter.GetBytes((uint)(completed.Count * 4)).CopyTo(r, 4);
        BitConverter.GetBytes(dlmId).CopyTo(r, 8);
        for (int i = 0; i < completed.Count; i++)
            BitConverter.GetBytes(completed[i]).CopyTo(r, MarkQuestReplyFixed + i * 4);
        return r;
    }

    /// <summary>SDB_USER_LEARN_SKILL_FOR_MULTIPLE -&gt; DBS_. T164: 0x278E's rule for a list - the
    /// atoms go back with insert ids and are applied, every pair is answered as learned. The skills
    /// themselves persist the way 0x278E's do: in the blob World saves (SDB_UPDATE_USER_DATA).</summary>
    private bool OnUserLearnSkillForMultiple(WorldLink link, byte[] payload)
    {
        Func<int> alloc = _store is null ? () => 0 : _store.NextItemId;
        var reply = BuildDbs2791(payload, alloc);
        int rows = 0;
        if (_store is not null)
        {
            var r = BagItems.ApplyReplyAtoms(_store, reply, 0, _store.NextItemId, _log);
            rows = r.Inserted + r.Moved + r.AmountChanged + r.Deleted;
        }
        _log.LogInformation("SDB_USER_LEARN_SKILL_FOR_MULTIPLE: player {Pid} skill {Skill} + {N} listed, {A} atom(s), {R} item row(s)",
            Ep32(payload, LearnMultiReqUserDbId), Ep32(payload, LearnMultiReqSkillId),
            Ep32(reply, 20) / 8, Ep32(reply, 4) / ItemAtomSize, rows);
        link.SendFrame(DBS_USER_LEARN_SKILL_FOR_MULTIPLE, reply);
        return true;
    }

    /// <summary>DBS_USER_LEARN_SKILL_FOR_MULTIPLE (0x2791): the 31-byte fixed part, the request's
    /// atoms echoed with insert ids filled in, an empty SkillPeriodData list, and one
    /// {skillId, 1 = learned} pair per requested pair. ok 1, hasPeriods 0, alreadyLearned 0.</summary>
    public static byte[] BuildDbs2791(byte[] request, Func<int> allocateItemId)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(allocateItemId);
        byte[] atoms = CloneAtomList(request, 0, allocateItemId, LearnMultiReqFixed);
        var skills = ReadI32List(request, LearnMultiReqPairs, LearnMultiReqFixed, 8);

        var r = new byte[LearnMultiReplyFixed + atoms.Length + skills.Count * 8];
        uint at = 6 + LearnMultiReplyFixed;                            // 37, frame-relative
        BitConverter.GetBytes(at).CopyTo(r, 0);
        BitConverter.GetBytes((uint)atoms.Length).CopyTo(r, 4);
        at += (uint)atoms.Length;
        BitConverter.GetBytes(at).CopyTo(r, 8);                        // SkillPeriodData: empty
        BitConverter.GetBytes(at).CopyTo(r, 16);
        BitConverter.GetBytes((uint)(skills.Count * 8)).CopyTo(r, 20);
        BitConverter.GetBytes(Ep32(request, LearnMultiReqDlmId)).CopyTo(r, 24);
        r[28] = 1;                                                     // ok
        atoms.CopyTo(r, LearnMultiReplyFixed);
        for (int i = 0; i < skills.Count; i++)
        {
            int o = LearnMultiReplyFixed + atoms.Length + i * 8;
            BitConverter.GetBytes(skills[i]).CopyTo(r, o);
            BitConverter.GetBytes(1).CopyTo(r, o + 4);                 // learned
        }
        return r;
    }

    /// <summary>The first i32 of every <paramref name="stride"/>-byte element behind the
    /// <c>[u32 frame-relative offset][u32 length]</c> ref at <paramref name="refOffset"/>; empty
    /// for an absent or malformed ref, so the caller can always answer.</summary>
    public static List<int> ReadI32List(byte[] p, int refOffset, int minStart, int stride = 4)
    {
        ArgumentNullException.ThrowIfNull(p);
        var list = new List<int>();
        if (refOffset < 0 || refOffset + 8 > p.Length || stride < 4) return list;
        long start = (long)BitConverter.ToUInt32(p, refOffset) - 6;
        long len = BitConverter.ToUInt32(p, refOffset + 4);
        if (len == 0 || start < minStart || len % stride != 0 || start + len > p.Length) return list;
        for (long o = start; o < start + len; o += stride) list.Add(BitConverter.ToInt32(p, (int)o));
        return list;
    }

    private bool OnMakeSysParcel(WorldLink link, byte[] payload)
    {
        uint dlmId = Ep32(payload, 20);
        int receiver = Ep32i(payload, 24);
        long money = Ep64(payload, 28);
        int errorNo = 0;
        var character = _store?.GetCharacter(receiver);
        if (_store is null || character is null || !SystemParcelAttachments.TryRead(payload, out var items))
        {
            errorNo = 1;
            _log.LogWarning("SA_MAKE_SYS_PARCEL: player {To}, {Bytes} B request: missing owner or unsupported/malformed attachment list",
                receiver, payload.Length);
        }
        else
        {
            // T196: World has already evaluated AchievementList.xml and sent the reward here.
            // Creating it at 0x2802 as well would grant twice. cap_final2b 6186 -> 6189 -> 6190;
            // cap_instance1 139184 contains the four EP reward parcels whose items we dropped.
            var (writer, title, message) = ReadSysParcelText(payload);
            if (writer.Length == 0) writer = SystemParcelSender;
            for (int first = 0; first < Math.Max(1, items.Count); first += CharacterStore.MaxParcelAttachments)
            {
                // Native splits a longer reward across five-slot parcels, money on the first.
                var chunk = items.Skip(first).Take(CharacterStore.MaxParcelAttachments).ToArray();
                long parcelMoney = first == 0 ? money : 0;
                int id = _store.CreateParcel(0, writer, receiver, title, message, parcelMoney,
                    ParcelDbHandlers.ParcelTypeSystem);
                _store.SetParcelRecord(id, SystemParcelAttachments.BuildRecord(id, receiver, character.Name,
                    writer, title, message, parcelMoney, chunk));
                for (int slot = 0; slot < chunk.Length; slot++)
                    _store.AddParcelItem(id, slot, 0, BitConverter.ToInt32(chunk[slot], 8),
                        BitConverter.ToInt32(chunk[slot], 12));

                var session = global::TeraSharp.Arbiter.Program.World?.SessionForPlayerId(receiver);
                if (session is not null)
                {
                    var (unread, unclaimed) = _store.GetParcelCounts(receiver);
                    session.Send(Handlers.ParcelHandlers.BuildReadRecvStatus((uint)unread, (uint)unclaimed,
                        flag: payload[36] != 0));
                }
                _log.LogInformation("SA_MAKE_SYS_PARCEL: parcel {Id} to player {To}, {Money} money, {Items} attachment(s)",
                    id, receiver, parcelMoney, chunk.Length);
            }
        }
        link.SendFrame(AS_MAKE_SYS_PARCEL, BuildAsMakeSysParcel(dlmId, errorNo));
        return true;
    }

    /// <summary>
    /// T151. SA_MAKE_SYS_PARCEL's three strings: frame-relative offsets at payload 8 (Writer),
    /// 12 (Title) and 16 (Message), each a NUL-terminated UTF-16 string. cap_social4 seq 2962:
    /// "@Achievement:6903", "@2051", "@2052\vAchievementName\v@Achievement:6900". A missing or
    /// out-of-range ref reads as empty rather than throwing - the reply must always go out.
    /// </summary>
    public static (string Writer, string Title, string Message) ReadSysParcelText(byte[] payload)
    {
        string At(int refAt)
        {
            if (payload == null || refAt + 4 > payload.Length) return string.Empty;
            long o = (long)BitConverter.ToUInt32(payload, refAt) - 6;
            if (o < 0 || o >= payload.Length) return string.Empty;
            var sb = new System.Text.StringBuilder();
            for (long i = o; i + 1 < payload.Length && sb.Length < 1024; i += 2)
            {
                char c = (char)BitConverter.ToUInt16(payload, (int)i);
                if (c == '\0') break;
                sb.Append(c);
            }
            return sb.ToString();
        }
        return (At(8), At(12), At(16));
    }

    /// <summary>The sender name a system parcel is filed under. The capture's Writer string is a
    /// localisation key the client resolves, so ours is a plain marker rather than a guess at it.</summary>
    public const string SystemParcelSender = "System";

    public static byte[] BuildReqIdAck(byte[] request, int reqIdPayloadOffset)
    {
        uint reqId = reqIdPayloadOffset + 4 <= request.Length
            ? BitConverter.ToUInt32(request, reqIdPayloadOffset) : 0;
        var ack = new byte[5];
        BitConverter.GetBytes(reqId).CopyTo(ack, 0);
        ack[4] = 1;
        return ack;
    }

    // Both dungeon-enter replies are built the same way: the Arbiter copies
    // DungeonEnterContext (176 B) and DungeonOwnerInfo (24 B) out of the request into a
    // ZERO-INITIALISED packet buffer, field by field, and the copies skip the structs'
    // padding. Every skipped byte therefore leaves the buffer's zero on the wire while
    // every real field is passed through untouched.
    //
    // The field lists are FUN_1406d2180 (DungeonEnterContext, 0xB0) and FUN_1406d2430
    // (DungeonOwnerInfo, 0x18) in ArbiterServer.exe.c. Converting the u32 indices they
    // write into byte ranges and subtracting the ones they touch leaves these gaps
    // (context starts at payload 8, owner info at payload 184):
    //
    //   context byte 43        -> payload 51        (after a u16 + u8 at 40..42)
    //   context bytes 60..63   -> payload 68..71    (index 0x0F is never written)
    //   context bytes 138..139 -> payload 146..147  (after a u16 at 136..137)
    //   context bytes 145..147 -> payload 153..155  (after a lone u8 at 144)
    //   context bytes 156..159 -> payload 164..167  (before a u64 at 160)
    //   owner   bytes 9..11    -> payload 193..195  (after a u8 at 192)
    //
    // Verified against cap_newchar.log: for seq 2457 -> 2458 exactly the bytes at 51, 68,
    // 70, 146 and 147 change (the other gaps were already zero in the request), and
    // nothing else moves except the leading PDId. NOTE: an older comment here described
    // this as "three context fields zeroed at 44, 68, 146" and the code cleared payload
    // 44..47 — that range holds a live coordinate float (-4393.0 in the capture, kept
    // verbatim by the real Arbiter), so the old builder was not byte-exact. T10.
    private static readonly int[] DungeonReplyPaddingBytes =
    {
        51,
        68, 69, 70, 71,
        146, 147,
        153, 154, 155,
        164, 165, 166, 167,
        193, 194, 195,
    };

    /// <summary>Minimum payload for 0x13BE (frame > 0xD6, _Handler_SA_REQUEST_ENTER_DUNGEON).</summary>
    public const int RequestEnterDungeonMinPayload = 0xD7 - 6;   // 209
    /// <summary>Minimum payload for 0x13C0 (frame >= 0xD6, Handler_SA_RESPONSE_ENTER_DUNGEON).</summary>
    public const int ResponseEnterDungeonMinPayload = 0xD6 - 6;  // 208

    private static void ZeroDungeonReplyPadding(byte[] r)
    {
        foreach (int off in DungeonReplyPaddingBytes)
            if (off < r.Length) r[off] = 0;
    }

    /// <summary>
    /// AS_REQUEST_ENTER_DUNGEON (0x13BF) from SA_REQUEST_ENTER_DUNGEON (0x13BE).
    /// Request payload: [0] u64 userHandle, [8] DungeonEnterContext (176 B), [184]
    /// DungeonOwnerInfo (24 B), [208] u8 flag. The reply replaces the leading handle with the
    /// PDId the Arbiter builds itself — [u32 worldId][u32 playerId], worldId = DAT_140e2d020 =
    /// <see cref="WorldId"/> — and passes the rest through with the padding above zeroed.
    /// The real Arbiter takes playerId from its own User object (user+0x120); we take it from
    /// the PDId World embedded in the context at payload 32, which is the same value in the
    /// capture. Byte-exact to cap_newchar.log seq 2457 -> 2458.
    /// </summary>
    public static byte[]? BuildAsRequestEnterDungeon(byte[] req)
    {
        if (req.Length < RequestEnterDungeonMinPayload) return null;
        var r = (byte[])req.Clone();
        uint playerId = BitConverter.ToUInt32(req, 32);
        BitConverter.GetBytes(WorldId).CopyTo(r, 0);
        BitConverter.GetBytes(playerId).CopyTo(r, 4);
        ZeroDungeonReplyPadding(r);
        return r;
    }

    /// <summary>
    /// AS_RESPONSE_ENTER_DUNGEON (0x13C1) from SA_RESPONSE_ENTER_DUNGEON (0x13C0). Unlike the
    /// request, this one echoes the request's own PDId (Handler_SA_RESPONSE_ENTER_DUNGEON reads
    /// the u64 at frame+6 and writes it straight back), so the reply is the request with only
    /// the struct padding zeroed — a byte-identical echo whenever the padding is already zero,
    /// which it is in cap_newchar.log seq 2464 -> 2465 because those bytes came from our own
    /// 0x13BF. Min payload 208 per the handler's length check.
    /// </summary>
    public static byte[]? BuildAsResponseEnterDungeon(byte[] req)
    {
        if (req.Length < ResponseEnterDungeonMinPayload) return null;
        var r = (byte[])req.Clone();
        ZeroDungeonReplyPadding(r);
        return r;
    }

    // =====================================================================
    // T21 - enter-world failure and the fallback retry.
    // Research and capture evidence: status/ENTER-WORLD-FALLBACK.md.
    // =====================================================================

    /// <summary>One SA_ENTER_WORLD_FAIL (0x138D), parsed. Field names are the Arbiter's own.</summary>
    public readonly record struct EnterWorldFailure(
        ulong ArbiterClient, ulong ArbiterUser, uint Ticket, uint LastIndex,
        uint ContinuousDungeonId, uint FailReason);

    /// <summary>Null when the frame is shorter than the real handler's length check.</summary>
    public static EnterWorldFailure? ParseEnterWorldFail(byte[] payload)
    {
        if (payload is null || payload.Length < EnterWorldFailMinPayload) return null;
        return new EnterWorldFailure(
            BitConverter.ToUInt64(payload, EnterWorldFailArbiterClientOffset),
            BitConverter.ToUInt64(payload, EnterWorldFailArbiterUserOffset),
            BitConverter.ToUInt32(payload, EnterWorldFailTicketOffset),
            BitConverter.ToUInt32(payload, EnterWorldFailLastIndexOffset),
            BitConverter.ToUInt32(payload, EnterWorldFailDungeonIdOffset),
            BitConverter.ToUInt32(payload, EnterWorldFailReasonOffset));
    }

    /// <summary>
    /// User::EnterWorldFail opens with <c>if (2 &lt; reason - 1) { LeaveWorldType 3; disconnect; }</c>,
    /// i.e. it only ever retries reasons 1, 2 and 3. The capture's reason is 2.
    /// </summary>
    public static bool IsRetryableEnterWorldFailure(uint reason) => reason - 1 < 3;

    /// <summary>
    /// SA_ENTER_WORLD_FAIL. Sends the two AS_CACHE_DUNGEON_COOL_TIME_TO_WORLD pushes the real
    /// Arbiter sends for the refused instance, then asks the session layer to re-send
    /// AS_ENTER_WORLD with the stored return point. Both steps need facts only that layer has,
    /// so both are behind hooks; with neither wired this still logs the failure in full.
    /// </summary>
    /// <summary>
    /// T161b. SA_ENTER_WORLD (0x138C): World finished loading the user. A leave NotifyPlayerLeave
    /// held meanwhile is sent now - User::EnterWorldEnd's <c>+0x4008</c> branch calls LeaveWorldStart
    /// with the reserved type and reason. cap_crash 28475: the frame that should have released
    /// ...AF00003 (left at 28218).
    /// </summary>
    private bool OnSaEnterWorld(WorldBridge? bridge, byte[] payload)
    {
        ulong gameId = LeaveGate.GameIdOf(payload);
        var held = LeaveGate.Shared.Entered(gameId);
        if (held == null)
        {
            TeraSharp.Arbiter.Handlers.QaSocialCommands.OnWorldEntryComplete(bridge?.PlayerForGameId(gameId), _store);
            PartyWiring.OnWorldEntryComplete(bridge, gameId);
            return true;
        }
        _log.LogInformation("SA_ENTER_WORLD for gameId {G:X}: its session left while World was loading it - "
            + "sending the held AS_LEAVE_WORLD now", gameId);
        bridge?.NotifyPlayerLeave(held.WorldId, held.GameId, held.PlayerId, held.Mode);
        return true;
    }

    private bool OnEnterWorldFail(WorldLink link, byte[] payload)
    {
        var parsed = ParseEnterWorldFail(payload);
        if (parsed == null)
        {
            _log.LogError("SA_ENTER_WORLD_FAIL: {Len} B payload, the real handler needs {Need}",
                payload.Length, EnterWorldFailMinPayload);
            return false;
        }
        var f = parsed.Value;

        _log.LogWarning(
            "SA_ENTER_WORLD_FAIL: World refused enter-world - gameId 0x{G:X}, ticket {T}, "
            + "continent {C}, reason {R}",
            f.ArbiterUser, f.Ticket, f.ContinuousDungeonId, f.FailReason);

        if (!IsRetryableEnterWorldFailure(f.FailReason))
        {
            _log.LogError(
                "SA_ENTER_WORLD_FAIL: reason {R} is outside 1-3, which is where the real Arbiter "
                + "stops retrying and drops the user. Not retrying.", f.FailReason);
            LeaveGate.Shared.Forget(f.ArbiterUser);   // T161b: World never had the user - nothing to leave
            return true;
        }

        int playerId = PlayerIdForGameId?.Invoke(f.ArbiterUser) ?? 0;
        if (playerId <= 0)
        {
            _log.LogWarning("SA_ENTER_WORLD_FAIL: no live session owns gameId 0x{G:X} - "
                + "skipping the 0x148D cool-time pushes", f.ArbiterUser);
        }
        else
        {
            foreach (var push in BuildCacheDungeonCoolTimePushes(playerId, (int)f.ContinuousDungeonId))
                link.SendFrame(AS_CACHE_DUNGEON_COOL_TIME_TO_WORLD, push);
        }

        if (ResendEnterWorld is null)
        {
            _log.LogError(
                "SA_ENTER_WORLD_FAIL: ResendEnterWorld is not wired, so no AS_ENTER_WORLD retry "
                + "goes out and the client stays on the loading screen. "
                + "See status/ENTER-WORLD-FALLBACK.md for the one-line wiring.");
            return true;
        }
        ResendEnterWorld(f);
        return true;
    }

    /// <summary>
    /// The pair of 0x148D payloads the real Arbiter sends between the failure and the retry
    /// (capture seq 841/842). Both carry one 52-byte DungeonCoolTimeElem for the refused
    /// instance; in the capture they differ only in the two counters at +40/+44 (1,1 then 0,0),
    /// which is the cool-time list followed by the clear-count list.
    /// <para>Since T25 the element comes from <c>dungeon_cooldowns</c>. With no row for the
    /// dungeon both pushes are "never entered" - timestamps <see cref="DungeonCoolTimeNever"/>
    /// and counters 0 - which can only ever let a player back in, never lock one out.</para>
    ///
    /// <para>The capture's first push carries counters (1, 1) while the DB load two hundred
    /// frames later carries (0, 0) for the same dungeon: the push is the real Arbiter's LIVE
    /// in-memory element with the attempt already counted, not the stored row. We push the
    /// stored row twice instead, and that difference is the one thing here that is not
    /// byte-exact against the capture.</para>
    /// </summary>
    private List<byte[]> BuildCacheDungeonCoolTimePushes(int playerId, int dungeonId)
    {
        var stored = _store is null ? null : _store.GetDungeonCoolTime(playerId, dungeonId);
        byte[] record;
        if (stored != null && stored.Length == DungeonCoolTimeRecordSize)
        {
            record = stored;
        }
        else
        {
            var chr = _store is null ? null : _store.GetCharacter(playerId);
            record = BuildDungeonCoolTimeRecord(dungeonId, (uint)(chr?.InstancePdId ?? 0), null, null, 0, 0, 0);
        }
        return new List<byte[]>
        {
            BuildCacheDungeonCoolTime(playerId, record),
            BuildCacheDungeonCoolTime(playerId, record),
        };
    }

    /// <summary>
    /// AS_CACHE_DUNGEON_COOL_TIME_TO_WORLD (0x148D): [u32 blobOffset=18][u32 blobLen][u32 playerId]
    /// + one <see cref="DungeonCoolTimeRecordSize"/>-byte record.
    /// </summary>
    public static byte[] BuildCacheDungeonCoolTime(int playerId, byte[] record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (record.Length != DungeonCoolTimeRecordSize)
            throw new ArgumentException(
                $"cool-time record must be {DungeonCoolTimeRecordSize} bytes, got {record.Length}", nameof(record));
        var r = new byte[CacheDungeonCoolTimeHeader + record.Length];
        BitConverter.GetBytes((uint)(6 + CacheDungeonCoolTimeHeader)).CopyTo(r, 0);   // 18, frame-relative
        BitConverter.GetBytes((uint)record.Length).CopyTo(r, 4);
        BitConverter.GetBytes(playerId).CopyTo(r, 8);
        record.CopyTo(r, CacheDungeonCoolTimeHeader);
        return r;
    }

    /// <summary>
    /// One 52-byte DungeonCoolTimeElem. The two DateTimes are passed as raw 16-byte blobs
    /// (null = <see cref="DungeonCoolTimeNever"/>) because their field layout is inferred from
    /// two capture values, not from a decompiled writer.
    /// </summary>
    public static byte[] BuildDungeonCoolTimeRecord(
        int dungeonId, uint channelInstanceId, byte[]? firstEntry, byte[]? lastEntry,
        uint counter0, uint counter1, uint counter2)
    {
        var a = firstEntry ?? DungeonCoolTimeNever;
        var b = lastEntry ?? DungeonCoolTimeNever;
        if (a.Length != DungeonCoolTimeDateSize || b.Length != DungeonCoolTimeDateSize)
            throw new ArgumentException($"a DateTime is {DungeonCoolTimeDateSize} bytes");
        var r = new byte[DungeonCoolTimeRecordSize];
        BitConverter.GetBytes(dungeonId).CopyTo(r, 0);
        BitConverter.GetBytes(channelInstanceId).CopyTo(r, 4);
        a.CopyTo(r, 8);
        b.CopyTo(r, 24);
        BitConverter.GetBytes(counter0).CopyTo(r, 40);
        BitConverter.GetBytes(counter1).CopyTo(r, 44);
        BitConverter.GetBytes(counter2).CopyTo(r, 48);
        return r;
    }

    /// <summary>
    /// The retry form of an AS_ENTER_WORLD payload: the original with continent, channel
    /// instance, position, ticket and ContinuousDungeonId replaced and nothing else touched.
    /// That is exactly what User::EnterWorldFail produces - capture seq 835 -> seq 843 differ in
    /// those five fields and no others. Null for a payload that is not 183 bytes.
    /// </summary>
    public static byte[]? BuildEnterWorldRetryPayload(
        byte[] original, int zone, float x, float y, float z,
        uint channelInstanceId, uint ticket, uint continuousDungeonId)
    {
        if (original is null || original.Length != EnterWorldPayloadSize) return null;
        var r = (byte[])original.Clone();
        BitConverter.GetBytes((uint)zone).CopyTo(r, EnterWorldContinentIdOffset);
        BitConverter.GetBytes(channelInstanceId).CopyTo(r, EnterWorldChannelInstanceIdOffset);
        BitConverter.GetBytes(x).CopyTo(r, EnterWorldPositionOffset);
        BitConverter.GetBytes(y).CopyTo(r, EnterWorldPositionOffset + 4);
        BitConverter.GetBytes(z).CopyTo(r, EnterWorldPositionOffset + 8);
        BitConverter.GetBytes(ticket).CopyTo(r, EnterWorldTicketOffset);
        BitConverter.GetBytes(continuousDungeonId).CopyTo(r, EnterWorldContinuousDungeonIdOffset);
        return r;
    }

    /// <summary>
    /// Persist the return point a dungeon entry carries, so a relog into the instance has
    /// somewhere to fall back to. Called from both halves of the handshake: the request already
    /// carries the return continent and position, the response adds the ChannelInstanceId
    /// WorldServer allocated.
    /// <para>DEVIATION, on purpose: the real Arbiter only commits the return point when
    /// DungeonEnterContext+42 is set and CLEARS it otherwise (User::CleanSysReturnLoc). In the
    /// only capture we have that flag is 0 on both halves, yet the DB clearly held a valid
    /// return point at login - so some other call site set it and clearing here would leave us
    /// with no fallback at all. We store whatever the context carries and never clear;
    /// CharacterStore.ClearDungeonReturn is there for when that flag's source is found.</para>
    /// </summary>
    internal void RecordDungeonEntry(byte[] payload, bool response, int? resolvedPlayerId = null)
    {
        if (_store is null || payload.Length < ResponseEnterDungeonMinPayload) return;
        int playerId = resolvedPlayerId ?? (int)BitConverter.ToUInt32(payload, DungeonCtxPlayerId);
        if (playerId <= 0) return;

        int dungeonId = (int)BitConverter.ToUInt32(payload, DungeonCtxDungeonId);
        int returnZone = (int)BitConverter.ToUInt32(payload, DungeonCtxReturnZone);
        if (returnZone > 0)
            _store.SaveDungeonReturn(playerId, dungeonId, returnZone,
                BitConverter.ToSingle(payload, DungeonCtxReturnX),
                BitConverter.ToSingle(payload, DungeonCtxReturnY),
                BitConverter.ToSingle(payload, DungeonCtxReturnZ));

        if (!response) return;
        if (payload[DungeonCtxSuccess] == 0)
        {
            _log.LogWarning("SA_RESPONSE_ENTER_DUNGEON: player {Pid} was NOT admitted to {Dg}", playerId, dungeonId);
            return;
        }
        int pdId = (int)BitConverter.ToUInt32(payload, DungeonCtxInstancePdId);
        _store.SaveInstancePdId(playerId, pdId);
        _log.LogInformation("Player {Pid} is in dungeon {Dg}, instance 0x{Pd:X8}", playerId, dungeonId, pdId);
        MatchWiring.OnDungeonEntered(playerId, dungeonId);   // T161 (c): a matched member claims their entry
    }

    // T192: source save7903 still names zone7005; target load7911 contains the 1445 destination.
    // Arb029:3675-3712 applies SetPosNoLock/spUpdateUserLoc after source leave completes.
    internal void CommitTeleportLocation(int playerId, CrossWorldHandoff.Teleport move, int destinationWorld)
    {
        var chr = _store.GetCharacter(playerId);
        if (chr == null) return;
        if (chr.WorldBlob is { Length: >= 308 } blob)
            _store.SaveWorldBlob(playerId, CrossWorldHandoff.StampLocation(blob, move, destinationWorld));
        else
            _store.UpdateLevelAndPosition(playerId, chr.Level, move.Continent, move.X, move.Y, move.Z);
        _store.SaveInstancePdId(playerId, unchecked((int)move.Channel));
    }

    /// <summary>
    /// The (crestId, value) entries SA_LEARN_ALL_CREST_ACQUIRABLE asks for - the crests World
    /// has decided this character may learn at its level. Payload: [u32 count][u32 firstOff]
    /// [u64 gameId][u32 reqId] + 16-B entries with frame-relative links.
    /// </summary>
    public static List<(int id, int val)> ReadCrestEntries(byte[] req)
    {
        var entries = new List<(int id, int val)>();
        if (req == null || req.Length < 20) return entries;

        // T50. Three things here, all of them found by the fuzz or by the walk that follows it:
        //
        //   1. The offsets are LONG arithmetic. `off + 16` used to be int, so a listOff of
        //      int.MaxValue (and int.MinValue, which wraps to the same place) made the sum
        //      negative, the `<= req.Length` bound passed, and BitConverter.ToInt32 threw
        //      ArgumentOutOfRangeException. Out of a DB-proxy handler that is not a dropped
        //      packet: WorldLink.ReceiveLoop has no per-frame catch, so it closes the World link
        //      and disconnects every player. Two of the 21 DB-proxy failures were this line
        //      (status/FUZZ-FINDINGS.txt).
        //   2. The chain is packet-supplied, so an element whose `next` points at itself - or
        //      back up the chain - would be walked to the guard every time. The visited set ends
        //      it at the repeat instead.
        //   3. The offsets are frame-relative and unsigned on the wire, so they are read as uint
        //      and widened; the old `(int)` cast turned a large offset into a negative one, which
        //      happened to be safe but for the wrong reason.
        long off = (long)BitConverter.ToUInt32(req, 4) - 6;
        HashSet<long>? seen = null;
        int guard = 0;
        while (off > 0 && off + 16 <= req.Length && guard++ < 512)
        {
            seen ??= new HashSet<long>();
            if (!seen.Add(off)) break;
            int at = (int)off;
            entries.Add((BitConverter.ToInt32(req, at + 8), BitConverter.ToInt32(req, at + 12)));
            long next = BitConverter.ToUInt32(req, at + 4);
            if (next == 0) break;
            off = next - 6;
        }
        return entries;
    }

    /// <summary>
    /// AS_LEARN_ALL_CREST_ACQUIRABLE (0x1464): <c>[u32 0][u32 0][u32 reqId][u8 ok=1]</c>, no entries.
    ///
    /// <para><b>T151: the list is the crests World must NOT learn.</b> World's
    /// DBUserAutoLearnCrestContext::SetRecvData walks the reply's map and ERASES every id in it
    /// from the set it asked for; whatever is left is learned. So echoing the request - what we did
    /// since T50 - erased all of them, and every glyph stayed locked. The real Arbiter answers the
    /// 42-crest request after a level jump with an empty list: cap_social4.log seq 2820 -&gt; 2821,
    /// <c>13 00 00 00 64 14 00 00 00 00 00 00 00 00 dd 01 00 00 01</c>.</para>
    /// </summary>
    public static byte[]? BuildLearnAllCrest(byte[] req)
    {
        if (req == null || req.Length < 20) return null;
        var r = new byte[13];
        BitConverter.GetBytes(BitConverter.ToUInt32(req, 16)).CopyTo(r, 8);
        r[12] = 1;
        return r;
    }

    /// <summary>DBS 0x2931: [u32 reqId][u8 ok=1][u8 held] — 6 bytes (lobby_tap.log 02:52:07.112: 82 00 00 00 01 00).</summary>
    public static byte[] Build2931(byte[] request, bool held = false)
    {
        uint reqId = request.Length >= 4 ? BitConverter.ToUInt32(request, 0) : 0;
        var r = new byte[6];
        BitConverter.GetBytes(reqId).CopyTo(r, 0);
        r[4] = 1;
        r[5] = held ? (byte)1 : (byte)0;
        return r;
    }

    /// <summary>
    /// SDB_ITEM_SINGLE (0x2768) -> DBS_ITEM_SINGLE (0x2769). See the constants above.
    /// </summary>
    private bool OnItemSingle(WorldLink link, byte[] payload,
        ushort replyOp = DBS_SAVE_2769, string what = "SDB_ITEM_SINGLE")
    {
        int declaredA = DeclaredAtomCount(payload, 0), declaredB = DeclaredAtomCount(payload, 8);
        var reply = BuildDbs2769(payload, _store.NextItemId);
        int echoedA = (int)BitConverter.ToUInt32(reply, 4) / ItemAtomSize;
        int echoedB = (int)BitConverter.ToUInt32(reply, 12) / ItemAtomSize;
        uint playerId = payload.Length >= ItemSingleRequestHeader ? BitConverter.ToUInt32(payload, 20) : 0;

        if (echoedA != declaredA || echoedB != declaredB)
            _log.LogWarning(
                "{What}: could not echo the atom lists for player {Pid} (declared {DA}/{DB}, echoed {EA}/{EB}, "
                + "payload {Len} B) - World will lose the items it just wrote", what, playerId, declaredA, declaredB, echoedA, echoedB, payload.Length);
        else if (declaredA + declaredB > 0)
            _log.LogInformation("{What}: echoed {N} transaction atom(s) for player {Pid}",
                what, declaredA + declaredB, playerId);

        // T44: the atoms are applied to the item rows, not just echoed. Always from the REPLY -
        // it is the copy that has the allocated item DB ids in it, so the id World is handed and
        // the id the row gets are the same one. Both lists: World executes A then B.
        if (_store is not null)
        {
            var a = BagItems.ApplyReplyAtoms(_store, reply, 0, _store.NextItemId, _log);
            var b = BagItems.ApplyReplyAtoms(_store, reply, 8, _store.NextItemId, _log);
            int touched = a.Inserted + a.Moved + a.AmountChanged + a.Deleted
                        + b.Inserted + b.Moved + b.AmountChanged + b.Deleted;
            if (touched > 0)
                _log.LogInformation(
                    "{What}: player {Pid} -> {Ins} inserted, {Mov} moved, {Chg} amount, {Del} deleted ({Ign} atom(s) changed no row)",
                    what, playerId, a.Inserted + b.Inserted, a.Moved + b.Moved,
                    a.AmountChanged + b.AmountChanged, a.Deleted + b.Deleted, a.Ignored + b.Ignored);
        }

        link.SendFrame(replyOp, reply);
        return true;
    }


    /// <summary>
    /// SDB_CREATE_GUILD2 (0x27D4) -&gt; AS_GUILD_JOINED (0x2866) per joining member, then
    /// DBS_CREATE_GUILD2 (0x27D5). T69.
    ///
    /// <para><b>This was a live wedge.</b> Guild creation has no C_ packet and no SA_ frame - it
    /// arrives here - and nothing answered it, so the founder's DlmId went unanswered and their
    /// DB queue head-blocked. The replay table could never have covered it either: no guild was
    /// created in any tap it was built from.</para>
    ///
    /// <para>Order is the capture's (cap_social2.log seq 1268 -&gt; 1269 -&gt; 1270): the joins go
    /// out first, the reply last. The fee atoms in ItemBinary are echoed, not applied - World
    /// already took the money out of the founder's bag through the 0x2768 that preceded this.</para>
    /// </summary>
    private bool OnCreateGuild2(WorldLink link, byte[] payload)
    {
        var req = GuildPackets.ParseSdbCreateGuild2(payload);
        if (req == null)
        {
            _log.LogWarning("SDB_CREATE_GUILD2: {Len} B payload (want >= {Want}) - refusing",
                payload.Length, GuildPackets.CreateGuild2RequestHeader);
            link.SendFrame(GuildPackets.DBS_CREATE_GUILD2,
                GuildPackets.BuildDbsCreateGuild2Failure(0, 0, CreateGuildErrorGeneric));
            return true;
        }
        var r = req.Value;

        var made = GuildWiring.CreateGuildFromWorld(
            r.ChiefDbId, r.GuildName, r.MasterGroupName, r.MemberGroupName, r.MemberDbIds);

        if (made.GuildId == 0)
        {
            _log.LogWarning("SDB_CREATE_GUILD2: '{Name}' for chief {Chief} refused", r.GuildName, r.ChiefDbId);
            link.SendFrame(GuildPackets.DBS_CREATE_GUILD2,
                GuildPackets.BuildDbsCreateGuild2Failure(r.DlmId, r.ChiefDbId, CreateGuildErrorGeneric));
            return true;
        }

        foreach (int id in r.MemberDbIds)
        {
            if (id == 0 || id == r.ChiefDbId) continue;
            link.SendFrame(GuildPackets.AS_GUILD_JOINED, BitConverter.GetBytes(id));
        }

        _log.LogInformation(
            "SDB_CREATE_GUILD2: guild {Id} '{Name}' founded by {Chief} with {N} other member(s), {Fee} B of fee atoms",
            made.GuildId, r.GuildName, r.ChiefDbId, made.MemberBlobs.Count - 1, r.ItemBinary.Length);

        link.SendFrame(GuildPackets.DBS_CREATE_GUILD2, GuildPackets.BuildDbsCreateGuild2(
            r.GuildName, made.GroupBlobs, made.MemberBlobs, r.ItemBinary, made.FirstReplyName,
            success: true, made.GuildId, r.ChiefDbId, made.CreateTime, r.DlmId, errorNo: 0));
        return true;
    }

    /// <summary>ErrorNo in a refused DBS_CREATE_GUILD2. The enum is not in the decompile and the
    /// capture only ever carries 0, so this is the one value we invent - World shows the client a
    /// generic failure for anything non-zero.</summary>
    public const int CreateGuildErrorGeneric = 1;

    /// <summary>
    /// SDB_ITEM_TRADE (0x276A) -&gt; DBS_ITEM_TRADE (0x276B). T65. See the constants above: same
    /// echo-and-apply rule as SDB_ITEM_SINGLE, 568-byte records, two players.
    /// </summary>
    private bool OnItemTrade(WorldLink link, byte[] payload)
    {
        uint ownerId = payload.Length >= ItemTradeRequestHeader ? BitConverter.ToUInt32(payload, 20) : 0;
        uint targetId = payload.Length >= ItemTradeRequestHeader ? BitConverter.ToUInt32(payload, 24) : 0;
        int declaredA = DeclaredGiveTakeCount(payload, 0), declaredB = DeclaredGiveTakeCount(payload, 8);

        var reply = BuildDbsItemTrade(payload, _store.NextItemId);
        int echoedA = (int)BitConverter.ToUInt32(reply, 4) / ItemGiveTakeSize;
        int echoedB = (int)BitConverter.ToUInt32(reply, 12) / ItemGiveTakeSize;
        if (echoedA != declaredA || echoedB != declaredB)
            _log.LogWarning(
                "SDB_ITEM_TRADE: could not echo the give/take lists for {Owner} -> {Target} (declared {DA}/{DB}, "
                + "echoed {EA}/{EB}, payload {Len} B) - the trade will not be stored",
                ownerId, targetId, declaredA, declaredB, echoedA, echoedB, payload.Length);

        // Both lists are executed, in order, on the SAME item table - a trade moves rows between
        // two owners, so the second list must see what the first one did.
        if (_store is not null)
        {
            var a = BagItems.ApplyReplyAtoms(_store, reply, 0, _store.NextItemId, _log, ItemGiveTakeSize);
            var b = BagItems.ApplyReplyAtoms(_store, reply, 8, _store.NextItemId, _log, ItemGiveTakeSize);
            _log.LogInformation(
                "SDB_ITEM_TRADE: {Owner} <-> {Target}: {Ins} inserted, {Mov} moved, {Chg} amount, {Del} deleted "
                + "({Ign} record(s) changed no row)",
                ownerId, targetId, a.Inserted + b.Inserted, a.Moved + b.Moved,
                a.AmountChanged + b.AmountChanged, a.Deleted + b.Deleted, a.Ignored + b.Ignored);
        }

        link.SendFrame(DBS_ITEM_TRADE, reply);
        return true;
    }

    /// <summary>How many 568-byte give/take records the ref at <paramref name="headerOffset"/>
    /// declares. Same rounding World uses: <c>(bytes - 1) / 0x238 + 1</c>.</summary>
    public static int DeclaredGiveTakeCount(byte[] request, int headerOffset)
    {
        if (headerOffset + 8 > request.Length) return 0;
        int length = (int)BitConverter.ToUInt32(request, headerOffset + 4);
        return length <= 0 ? 0 : (length - 1) / ItemGiveTakeSize + 1;
    }

    /// <summary>
    /// DBS_ITEM_TRADE (0x276B): the request's two give/take lists echoed back under the same
    /// 21-byte header DBS_ITEM_SINGLE uses, with a freshly allocated item DB id written into
    /// every insert record that arrived with 0.
    /// </summary>
    public static byte[] BuildDbsItemTrade(byte[] request, Func<int> allocateItemId)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(allocateItemId);
        uint dlmId = request.Length >= ItemTradeRequestHeader ? BitConverter.ToUInt32(request, 16) : 0;

        byte[] listA = CloneAtomList(request, 0, allocateItemId, ItemTradeRequestHeader, ItemGiveTakeSize);
        byte[] listB = CloneAtomList(request, 8, allocateItemId, ItemTradeRequestHeader, ItemGiveTakeSize);

        var r = new byte[ItemSingleReplyHeader + listA.Length + listB.Length];
        uint offA = 6 + ItemSingleReplyHeader;                 // 27, frame-relative
        uint offB = offA + (uint)listA.Length;
        BitConverter.GetBytes(offA).CopyTo(r, 0);
        BitConverter.GetBytes((uint)listA.Length).CopyTo(r, 4);
        BitConverter.GetBytes(offB).CopyTo(r, 8);
        BitConverter.GetBytes((uint)listB.Length).CopyTo(r, 12);
        BitConverter.GetBytes(dlmId).CopyTo(r, 16);
        r[20] = 1;
        listA.CopyTo(r, ItemSingleReplyHeader);
        listB.CopyTo(r, ItemSingleReplyHeader + listA.Length);
        return r;
    }


    // ================================ T42: the warehouse ================================
    // status/MAIL-WAREHOUSE.md section 4. Layouts and builders are in World/WarehouseHandlers.cs;
    // these are the thin wrappers that read the live DlmId out of the request, touch the store
    // and send. Every one of them answers unconditionally, even on a malformed request: an
    // unanswered per-user DB item head-blocks that user's whole queue for the life of the World
    // process (status/HANDOFF.md section 1), and a warehouse the player cannot open is a much
    // smaller problem than a character that can never log out again.

    // Payload-relative and bounds-checked. The file already has U32/I64 taking a FRAME offset
    // (near BuildDbs2909); these take a payload index and return 0 rather than throwing, because
    // a short warehouse request still has to be answered.
    private static uint WhU32(byte[] p, int at) => at >= 0 && at + 4 <= p.Length ? BitConverter.ToUInt32(p, at) : 0u;
    private static long WhI64(byte[] p, int at) => at >= 0 && at + 8 <= p.Length ? BitConverter.ToInt64(p, at) : 0L;

    /// <summary>
    /// SDB_VIEW_WAREHOUSE (0x274A) -> DBS_VIEW_WAREHOUSE (0x274B), rebuilt from the item rows.
    ///
    /// <para>This is the warehouse's load. There is <b>no login-time warehouse load</b> — no
    /// SDB_LOAD_WAREHOUSE opcode exists in the 0x2700-0x29FF table, and neither capture contains
    /// a single warehouse frame — so this arrives the moment the player first talks to a bank
    /// NPC and never before.</para>
    ///
    /// <para>An owner with nothing banked gets the 45-byte empty form: Success = 1,
    /// ViewSize = 0x48 (the literal the real writer emits), everything else 0 and no items.</para>
    /// </summary>
    private bool OnViewWarehouse(WorldLink link, byte[] payload)
    {
        uint dlmId = WhU32(payload, WarehouseHandlers.ViewReqDlmId);
        long ownerDbId = WhI64(payload, WarehouseHandlers.ViewReqOwnerDbId);
        uint invenType = WhU32(payload, WarehouseHandlers.ViewReqInvenType);
        uint viewPos = WhU32(payload, WarehouseHandlers.ViewReqViewPos);

        if (payload.Length < WarehouseHandlers.ViewRequestSize)
        {
            _log.LogWarning("SDB_VIEW_WAREHOUSE too short ({Len} B) - answering with an empty warehouse", payload.Length);
            link.SendFrame(DBS_VIEW_WAREHOUSE,
                WarehouseHandlers.BuildDbsViewWarehouse(dlmId, true, 0, 0, 0, 0, 0, null));
            return true;
        }

        var records = new List<byte[]>();
        long money = 0;
        int slotCount = 0;

        if (_store is not null)
        {
            var (m, sc) = _store.GetWarehouse(ownerDbId, (int)invenType);
            money = m; slotCount = sc;
            foreach (var row in _store.GetItems(ownerDbId, (int)invenType))
            {
                // A row that came in with a real 536-byte ItemData keeps it; anything banked from
                // an atom has none, and a synthetic record is safe because the parts of the real
                // record we leave zero are uninitialised Arbiter heap that World has to ignore
                // (status/INVENTORY-DESIGN.md section 2).
                byte[]? stored = row.Record;
                records.Add(stored is not null && stored.Length == WarehouseHandlers.ItemRecordSize
                    ? stored
                    : WarehouseHandlers.BuildItemRecord(row.ItemDbId, row.TemplateId, (int)row.OwnerDbId,
                                                        (int)row.Amount, row.InvenType, row.Slot));
            }
        }

        // T69, from the nine captured views in cap_social2.log (seq 1982..2153): EndPos is the
        // index of the LAST item served, not one past it - 0 items -> 0, 1 -> 0, 2 -> 1, 3 -> 2.
        // We were sending one too many, so World's window ran a slot past the list.
        uint endPos = viewPos + (records.Count > 0 ? (uint)records.Count - 1 : 0);
        _log.LogInformation("SDB_VIEW_WAREHOUSE: owner {Owner} pocket {Pocket} -> {N} item(s), {Money} money",
            ownerDbId, invenType, records.Count, money);

        link.SendFrame(DBS_VIEW_WAREHOUSE, WarehouseHandlers.BuildDbsViewWarehouse(
            dlmId, ok: true, viewPos, endPos, (uint)records.Count, money,
            slotCount > 0 ? (ushort)Math.Clamp(slotCount, 0, ushort.MaxValue)
                          : WarehouseHandlers.DefaultMaxSlotCount, records));
        return true;
    }

    /// <summary>
    /// SDB_STORE_WAREHOUSE (0x274C), SDB_GET_WAREHOUSE (0x274E) and SDB_CHANGE_WAREHOUSE_POS
    /// (0x277F). One shape, three opcodes: a list of <c>ItemTransactionAtom</c>s applied to the
    /// item rows and echoed back with the ids we allocated.
    ///
    /// <para>The atoms are cloned <b>first</b> and the rows are built from the clone, so the id
    /// World gets back is the id the row has. Parsing the request instead would allocate a
    /// second id and hand World one we never stored.</para>
    /// </summary>
    private bool OnWarehouseTransfer(WorldLink link, byte[] payload, ushort replyOp,
                                     int dlmOffset, int refOffset, int minStart)
    {
        uint dlmId = WhU32(payload, dlmOffset);

        if (_store is null)
        {
            link.SendFrame(replyOp, WarehouseHandlers.BuildDbsTransfer(Array.Empty<byte>(), dlmId, true, 0, 0));
            return true;
        }

        var (atoms, parsed) = WarehouseHandlers.CloneAtomsWithIds(payload, refOffset, minStart, _store.NextItemId);
        if (atoms.Length == 0 && payload.Length > minStart)
            _log.LogWarning("{Op}: could not read the atom list ({Len} B payload) - echoing an empty one",
                DbProxyOpcodeNames.Describe(replyOp), payload.Length);

        WarehouseHandlers.ApplyResult r = default;
        bool ok = _store.TryApplyWarehouseTransfer(parsed.Where(a => a.Op == WarehouseHandlers.TsWareChangeMoney)
            .Select(a => (a.DstOwner != 0 ? a.DstOwner : a.SrcOwner, (int)(a.DstOwner != 0 ? a.DstInven : a.SrcInven), a.Delta)),
            TeraSharp.Arbiter.Handlers.GmCommandHandlers.WarehouseGoldMax,
            () => r = WarehouseHandlers.Apply(_store, parsed, _store.NextItemId, _log), out uint error);
        if (parsed.Count > 0)
            _log.LogInformation("{Op}: {N} atom(s) -> {Ins} inserted, {Mov} moved, {Chg} amount, {Del} deleted, "
                + "{Money} ware money, {Gold} character money, {Ign} ignored",
                DbProxyOpcodeNames.Describe(replyOp), parsed.Count, r.Inserted, r.Moved, r.AmountChanged,
                r.Deleted, r.MoneyDelta, r.CharacterMoneyDelta, r.Ignored);

        // WareCommision is 0 in this build: CommisionPayed() returns 1 and GetWareCommision()
        // returns 0 in the decompile, so the fee path is effectively disabled.
        link.SendFrame(replyOp, WarehouseHandlers.BuildDbsTransfer(atoms, dlmId, ok, error, wareCommision: 0));
        return true;
    }

    // =====================================================================================
    // T147: crafting (status/CRAFTING.md). The crafting window's two client packets,
    // S_ARTISAN_SKILL_LIST and S_ARTISAN_RECIPE_LIST, are built by WorldServer and tunnelled
    // through SA_BYPASS_TO_CLIENT - the Arbiter never writes them. What it owns is the data
    // behind them, and until T147 it answered only the two loads (always empty). The six
    // writes below carry a DlmId and had no answer at all, so the first recipe scroll read,
    // the first craft or the first bookmark head-blocked that character's DB queue.
    //
    // Record formats, request layouts and the builders are in ArtisanDb; these wrappers read
    // the live DlmId, touch the store and send. Success follows the real handlers
    // (ArbiterServer.exe.c, Handler_SDB_*): true once the user is loaded and the SQL ran,
    // except DELETE_ITEM_RECIPE_LIST, whose flag starts false and so stays false for an empty
    // list.
    // =====================================================================================

    public const ushort SDB_ITEM_PRODUCE_STEP1 = 0x2756;        // -> 0x2757, 16 B request
    public const ushort DBS_ITEM_PRODUCE_STEP1 = 0x2757;
    public const ushort SDB_ITEM_PRODUCE_STEP2 = 0x2758;        // -> 0x2759, 32 B request + atoms
    public const ushort DBS_ITEM_PRODUCE_STEP2 = 0x2759;
    public const ushort SDB_LEARN_ITEM_RECIPE = 0x275E;         // -> 0x275F, 21 B request + atoms
    public const ushort DBS_LEARN_ITEM_RECIPE = 0x275F;
    public const ushort DBS_LOAD_ITEM_RECIPE = 0x2761;
    public const ushort SDB_DELETE_ITEM_RECIPE_LIST = 0x2762;   // -> 0x2763, 16 B request + ids
    public const ushort DBS_DELETE_ITEM_RECIPE_LIST = 0x2763;
    public const ushort DBS_LOAD_SKILL_PROF = 0x2765;
    public const ushort SDB_UPDATE_SKILL_PROF = 0x2766;         // -> 0x2767, 16 B request
    public const ushort DBS_UPDATE_SKILL_PROF = 0x2767;
    public const ushort SDB_SET_RECIPE_BOOKMARK = 0x288A;       // -> 0x288B, 13 B request
    public const ushort DBS_SET_RECIPE_BOOKMARK = 0x288B;
    // Gathering proficiency: four one-int writes sharing one reply (ArtisanDb.GatheringKindOf).
    public const ushort S_UPDATE_PROF_MINERAL = 0x273D;         // -> 0x2741, 12 B request
    public const ushort S_UPDATE_PROF_BUG = 0x273E;             // -> 0x2741
    public const ushort S_UPDATE_PROF_ENERGY = 0x273F;          // -> 0x2741
    public const ushort S_UPDATE_PROF_HERB = 0x2740;            // -> 0x2741
    public const ushort D_UPDATE_PROF_RESULT = 0x2741;

    /// <summary>SDB_LOAD_ITEM_RECIPE (0x2760) -&gt; DBS (0x2761): the learned recipes, 28 bytes
    /// each. A character with none gets the same 13 bytes every capture shows.</summary>
    private bool OnLoadItemRecipe(WorldLink link, byte[] payload)
    {
        var q = ArtisanDb.ParseLoad(payload);
        List<ArtisanDb.Recipe>? rows = q is { } r ? _store?.GetItemRecipes(r.OwnerDbId) : null;
        if (rows is { Count: > 0 })
            _log.LogInformation("SDB_LOAD_ITEM_RECIPE: player {Owner} -> {N} recipe(s)", q!.Value.OwnerDbId, rows.Count);
        link.SendFrame(DBS_LOAD_ITEM_RECIPE, ArtisanDb.BuildDbsLoadItemRecipe(q?.DlmId ?? WhU32(payload, 0), rows));
        return true;
    }

    /// <summary>SDB_LOAD_SKILL_PROF (0x2764) -&gt; DBS (0x2765): the production proficiencies,
    /// 8 bytes each. Empty is byte-identical to the captures.</summary>
    private bool OnLoadSkillProf(WorldLink link, byte[] payload)
    {
        var q = ArtisanDb.ParseLoad(payload);
        List<ArtisanDb.SkillProf>? rows = q is { } r ? _store?.GetSkillProfs(r.OwnerDbId) : null;
        if (rows is { Count: > 0 })
            _log.LogInformation("SDB_LOAD_SKILL_PROF: player {Owner} -> {N} proficiency row(s)", q!.Value.OwnerDbId, rows.Count);
        link.SendFrame(DBS_LOAD_SKILL_PROF, ArtisanDb.BuildDbsLoadSkillProf(q?.DlmId ?? WhU32(payload, 0), rows));
        return true;
    }

    /// <summary>
    /// SDB_LEARN_ITEM_RECIPE (0x275E) -&gt; DBS (0x275F). Reading a recipe scroll: the scroll's
    /// consumption arrives as ItemTransactionAtoms and the reply echoes them (the ITEM_SINGLE
    /// rule). Once the atoms apply, the real handler calls User::LearnItemRecipeNoLock(recipe,
    /// extract), which stamps the current time and leaves the bookmark clear.
    /// </summary>
    private bool OnLearnItemRecipe(WorldLink link, byte[] payload)
    {
        var q = ArtisanDb.ParseLearn(payload);
        uint dlmId = q?.DlmId ?? WhU32(payload, ArtisanDb.LearnReqDlmId);
        byte[] atoms = Array.Empty<byte>();
        if (q is { } r && _store is not null)
        {
            var cloned = WarehouseHandlers.CloneAtomsWithIds(
                payload, ArtisanDb.LearnReqBinaryRef, ArtisanDb.LearnRequestSize, _store.NextItemId);
            atoms = cloned.Atoms;
            WarehouseHandlers.Apply(_store, cloned.Parsed, _store.NextItemId, _log);
            bool added = _store.LearnItemRecipe(r.OwnerDbId, r.RecipeId, r.Extract,
                DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            _log.LogInformation("SDB_LEARN_ITEM_RECIPE: player {Owner} learned recipe {R}{Ex}{Dup} ({N} atom(s))",
                r.OwnerDbId, r.RecipeId, r.Extract ? " (extract)" : "", added ? "" : " - already known",
                atoms.Length / ItemAtomSize);
        }
        WarnIfAtomsLost("SDB_LEARN_ITEM_RECIPE", payload, ArtisanDb.LearnReqBinaryRef, atoms);
        link.SendFrame(DBS_LEARN_ITEM_RECIPE, ArtisanDb.BuildRefReply(atoms, dlmId, ok: q is not null));
        return true;
    }

    /// <summary>SDB_DELETE_ITEM_RECIPE_LIST (0x2762) -&gt; DBS (0x2763). Forgetting recipes. The
    /// real handler's flag starts false and is set per id, so an empty list answers false.</summary>
    private bool OnDeleteItemRecipeList(WorldLink link, byte[] payload)
    {
        var q = ArtisanDb.ParseDeleteHeader(payload);
        uint dlmId = q?.DlmId ?? WhU32(payload, ArtisanDb.DeleteReqDlmId);
        var ids = ArtisanDb.ParseDeleteRecipeIds(payload);
        int removed = 0;
        if (q is { } r && _store is not null)
            foreach (int id in ids)
                if (_store.DeleteItemRecipe(r.OwnerDbId, id)) removed++;
        if (q is { } h)
            _log.LogInformation("SDB_DELETE_ITEM_RECIPE_LIST: player {Owner} -> {N} of {M} recipe(s) removed",
                h.OwnerDbId, removed, ids.Count);
        link.SendFrame(DBS_DELETE_ITEM_RECIPE_LIST, ArtisanDb.BuildDbsDeleteRecipeList(q is not null && ids.Count > 0, dlmId));
        return true;
    }

    /// <summary>SDB_SET_RECIPE_BOOKMARK (0x288A) -&gt; DBS (0x288B). The real
    /// SetItemRecipeBookmarkNoLock answers true whenever its SQL ran, found or not.</summary>
    private bool OnSetRecipeBookmark(WorldLink link, byte[] payload)
    {
        var q = ArtisanDb.ParseBookmark(payload);
        if (q is { } r && _store is not null && !_store.SetItemRecipeBookmark(r.OwnerDbId, r.RecipeId, r.Flag))
            _log.LogWarning("SDB_SET_RECIPE_BOOKMARK: player {Owner} has no stored recipe {R} - acknowledged, as the real server does",
                r.OwnerDbId, r.RecipeId);
        link.SendFrame(DBS_SET_RECIPE_BOOKMARK, ArtisanDb.BuildDlmThenSuccess(q?.DlmId ?? WhU32(payload, 0), q is not null));
        return true;
    }

    /// <summary>
    /// SDB_ITEM_PRODUCE_STEP1 (0x2756) -&gt; DBS (0x2757). The production-point charge. The real
    /// handler calls the same FatigabilityController function as SDB_UPDATE_FATIGABILITY_POINT
    /// with (Type, DeltaPoint), so the delta lands on the account's fatigability row exactly as
    /// that handler applies it (type not split out - T26 stores one bucket). Success is
    /// "the user was loaded".
    /// </summary>
    private bool OnProduceStep1(WorldLink link, byte[] payload)
    {
        var q = ArtisanDb.ParseStep1(payload);
        if (q is { } r && _store is not null && r.DeltaPoint != 0)
        {
            var chr = r.OwnerDbId > 0 ? _store.GetCharacter(r.OwnerDbId) : null;
            if (chr != null)
            {
                var row = _store.AddFatigabilityPoints(chr.AccountId, r.DeltaPoint, EncodeDbDateTime(DateTime.UtcNow));
                _log.LogInformation("SDB_ITEM_PRODUCE_STEP1: account {Acc} type {T} delta {D} fatigue -> {Total}",
                    chr.AccountId, r.Type, r.DeltaPoint, row.CurPoint);
            }
        }
        link.SendFrame(DBS_ITEM_PRODUCE_STEP1, ArtisanDb.BuildDlmThenSuccess(q?.DlmId ?? WhU32(payload, 0), q is not null));
        return true;
    }

    /// <summary>
    /// SDB_ITEM_PRODUCE_STEP2 (0x2758) -&gt; DBS (0x2759). The craft itself: materials out and
    /// product in as ItemTransactionAtoms, echoed back with fresh item ids. After the atoms the
    /// real handler calls User::UpdateSkillProfNoLock(id, value) when value &gt; 0 - an absolute
    /// set, not a delta. The ItemEnchantData ref is not stored (no enchant rows are modelled).
    /// </summary>
    private bool OnProduceStep2(WorldLink link, byte[] payload)
    {
        var q = ArtisanDb.ParseStep2(payload);
        uint dlmId = q?.DlmId ?? WhU32(payload, ArtisanDb.Step2ReqDlmId);
        byte[] atoms = Array.Empty<byte>();
        if (q is { } r && _store is not null)
        {
            var cloned = WarehouseHandlers.CloneAtomsWithIds(
                payload, ArtisanDb.Step2ReqBinaryRef, ArtisanDb.Step2RequestSize, _store.NextItemId);
            atoms = cloned.Atoms;
            var a = WarehouseHandlers.Apply(_store, cloned.Parsed, _store.NextItemId, _log);
            if (r.SkillProfValue > 0) _store.SetSkillProf(r.OwnerDbId, r.SkillProfId, r.SkillProfValue);
            _log.LogInformation(
                "SDB_ITEM_PRODUCE_STEP2: player {Owner} -> {Ins} inserted, {Chg} amount, {Del} deleted; proficiency {P} = {V}",
                r.OwnerDbId, a.Inserted, a.AmountChanged, a.Deleted, r.SkillProfId, r.SkillProfValue);
        }
        WarnIfAtomsLost("SDB_ITEM_PRODUCE_STEP2", payload, ArtisanDb.Step2ReqBinaryRef, atoms);
        link.SendFrame(DBS_ITEM_PRODUCE_STEP2, ArtisanDb.BuildRefReply(atoms, dlmId, ok: q is not null));
        return true;
    }

    /// <summary>SDB_UPDATE_SKILL_PROF (0x2766) -&gt; DBS (0x2767): one proficiency, set
    /// absolutely (UpdateSkillProfNoLock overwrites the value).</summary>
    private bool OnUpdateSkillProf(WorldLink link, byte[] payload)
    {
        var q = ArtisanDb.ParseUpdateProf(payload);
        if (q is { } r) _store?.SetSkillProf(r.OwnerDbId, r.SkillProfId, r.Value);
        link.SendFrame(DBS_UPDATE_SKILL_PROF, ArtisanDb.BuildDlmThenSuccess(q?.DlmId ?? WhU32(payload, 0), q is not null));
        return true;
    }

    /// <summary>
    /// S_UPDATE_PROF_MINERAL/BUG/ENERGY/HERB (0x273D-0x2740) -&gt; D_UPDATE_PROF_RESULT (0x2741).
    /// A gathering level, set absolutely (User::UpdateUserProf* assigns it and answers true), and
    /// stamped back into the world blob at the next enter-world - see OnUserEnterWorld.
    /// </summary>
    private bool OnUpdateGatheringProf(WorldLink link, ushort op, byte[] payload)
    {
        var q = ArtisanDb.ParseGatheringProf(payload);
        int kind = ArtisanDb.GatheringKindOf(op);
        if (q is { } r && kind >= 0)
        {
            _store?.SetGatheringProf(r.OwnerDbId, kind, r.Value);
            _log.LogInformation("{Op}: player {Owner} -> {V}", DbProxyOpcodeNames.Describe(op), r.OwnerDbId, r.Value);
        }
        link.SendFrame(D_UPDATE_PROF_RESULT, ArtisanDb.BuildDlmThenSuccess(q?.DlmId ?? WhU32(payload, 0), q is not null));
        return true;
    }

    /// <summary>World counts the atoms it sent from the ref's byte count; echoing fewer loses
    /// items on World's side, so say so (the SDB_ITEM_SINGLE warning, for the crafting ops).</summary>
    private void WarnIfAtomsLost(string what, byte[] payload, int refOffset, byte[] echoed)
    {
        int declared = DeclaredAtomCount(payload, refOffset);
        if (_store is not null && declared != echoed.Length / ItemAtomSize)
            _log.LogWarning("{What}: declared {D} atom(s), echoed {E} ({Len} B payload) - World will lose the items",
                what, declared, echoed.Length / ItemAtomSize, payload.Length);
    }

    /// <summary>SDB_CLEAR_WAREHOUSE (0x27E0) -> 0x27E1. dbo.spClearWarehouse.</summary>
    private bool OnClearWarehouse(WorldLink link, byte[] payload)
    {
        uint dlmId = WhU32(payload, WarehouseHandlers.ClearReqDlmId);
        long owner = WhU32(payload, WarehouseHandlers.ClearReqOwnerDbId);
        uint invenType = WhU32(payload, WarehouseHandlers.ClearReqInvenType);
        int removed = _store?.ClearWarehouse(owner, (int)invenType) ?? 0;
        _log.LogInformation("SDB_CLEAR_WAREHOUSE: owner {Owner} pocket {Pocket} -> {N} row(s) removed",
            owner, invenType, removed);
        link.SendFrame(DBS_CLEAR_WAREHOUSE, WarehouseHandlers.BuildDbsClearWarehouse(dlmId, ok: true));
        return true;
    }

    /// <summary>
    /// SDB_WAREHOUSE_AUTO_SORT (0x27E2) -> 0x27E3. The reply echoes the request's four fields
    /// back. We do not renumber slots: the client re-reads the page with SDB_VIEW_WAREHOUSE
    /// straight afterwards and our rows are already returned in slot order.
    /// </summary>
    private bool OnWarehouseAutoSort(WorldLink link, byte[] payload)
    {
        link.SendFrame(DBS_WAREHOUSE_AUTO_SORT, WarehouseHandlers.BuildDbsAutoSort(
            WhU32(payload, WarehouseHandlers.SortReqDlmId), ok: true,
            WhU32(payload, WarehouseHandlers.SortReqUserDbId),
            WhU32(payload, WarehouseHandlers.SortReqInvenType),
            WhU32(payload, WarehouseHandlers.SortReqBegin),
            WhU32(payload, WarehouseHandlers.SortReqEnd),
            errorMsg: 0, wareCommision: 0));
        return true;
    }

    /// <summary>
    /// SDB_PAY_WAREHOUSE_COMMISION (0x27C9) -> 0x27CA. The fee is disabled in this build
    /// (<c>CommisionPayed()</c> returns 1 and <c>GetWareCommision()</c> returns 0), so this is a
    /// success ack and nothing is charged.
    /// </summary>
    private bool OnPayWarehouseCommision(WorldLink link, byte[] payload)
    {
        link.SendFrame(DBS_PAY_WAREHOUSE_COMMISION, WarehouseHandlers.BuildDbsPayCommision(
            WhU32(payload, WarehouseHandlers.PayReqDlmId), ok: true, error: 0));
        return true;
    }

    /// <summary>
    /// SDB_INCREASE_WAREHOUSE_SIZE (0x283F) -> <b>DBS_INCREASE_INVENTORY_SIZE (0x283E)</b>.
    /// Not 0x2840 — see TRAP 1 in the constants block. Replying 0x2840 would head-block the user.
    /// </summary>
    private bool OnIncreaseWarehouseSize(WorldLink link, byte[] payload)
    {
        uint dlmId = WhU32(payload, WarehouseHandlers.IncReqDlmId);
        long owner = WhU32(payload, WarehouseHandlers.IncReqUserDbId);
        uint invenType = WhU32(payload, WarehouseHandlers.IncReqInvenType);
        int delta = (int)WhU32(payload, WarehouseHandlers.IncReqDeltaAmount);

        int newCount = _store?.AddWarehouseSlots(owner, (int)invenType, delta) ?? 0;
        _log.LogInformation("SDB_INCREASE_WAREHOUSE_SIZE: owner {Owner} pocket {Pocket} +{Delta} -> {Count} slot(s)",
            owner, invenType, delta, newCount);

        link.SendFrame(DBS_INCREASE_INVENTORY_SIZE, WarehouseHandlers.BuildDbsIncreaseSize(ok: true, dlmId));
        return true;
    }

    /// <summary>
    /// T150b. SDB_INCREASE_INVENTORY_SIZE (0x283D) -> DBS_INCREASE_INVENTORY_SIZE (0x283E,
    /// <c>[u8 Success][u32 DlmId]</c>, cap_social4.log seq 3040 <c>01 63 02 00 00</c>).
    /// The real handler (Arb_part_063.c:8426) stores the new size, rounded down to a multiple of 8,
    /// through User::UpdateMaxInvenSlotCountNoLock (Arb_part_030.c:12399) at User + 0x3BA0 =
    /// world blob + 0x3AF0 (User + 0xB0 is the blob), and the expand count at + 0x3B00.
    /// <para>Proven on the wire: in cap_social4.log character 1 enters with 40 at +0x3AF0
    /// (seq 250), asks for 48 (seq 3039), and every later enter-world blob (seq 5715; cap_final.log
    /// 411, 4377, 6413, 9576, 9981) carries 48 there, with level 70 at +0xCC untouched.</para>
    /// <para>Deviation: the original answers 0 when the stored size is already at least the new
    /// one; we answer 1 either way (World has already resized its bag). Pockets (tab != 0) are
    /// acked, not stored. Only the two i32s are written; the rest of the stored blob is untouched.</para>
    /// </summary>
    private bool OnIncreaseInventorySize(WorldLink link, byte[] payload)
    {
        uint dlmId = Ep32(payload, IncInvReqDlmId);
        int owner = Ep32i(payload, IncInvReqUserDbId);
        int tab = Ep32i(payload, IncInvReqTab);
        int newSize = Ep32i(payload, IncInvReqNewSize);
        int delta = Ep32i(payload, IncInvReqExpandDelta);
        int reason = Ep32i(payload, IncInvReqReason);

        if (tab == 0 && _store is not null && payload.Length >= IncInvReqFixed)
        {
            var (found, slots, expand) = _store.IncreaseInventorySize(owner, newSize, delta);
            _log.LogInformation("SDB_INCREASE_INVENTORY_SIZE: player {Id} bag -> {Req} (stored {Slots}, expand {Expand}, "
                + "reason {Reason}){Missing}", owner, newSize, slots, expand, reason,
                found ? "" : " - no world blob, nothing stored");
        }
        else
            _log.LogInformation("SDB_INCREASE_INVENTORY_SIZE: player {Id} tab {Tab} -> {Req} acked, not stored (reason {Reason})",
                owner, tab, newSize, reason);

        link.SendFrame(DBS_INCREASE_INVENTORY_SIZE, WarehouseHandlers.BuildDbsIncreaseSize(ok: true, dlmId));
        return true;
    }

    /// <summary>
    /// T150b. The generic ack for the opt-in rows of <see cref="DbAckTable"/> - each one proven by a
    /// live pair. Echoed item records are applied from the REPLY, as SDB_ITEM_SINGLE's are, so the
    /// ids World gets are the ids we store.
    /// </summary>
    /// <summary>
    /// T166 - the enchanting family, real handlers (DbAckGroups: Handled, not a DbAckTable row:
    /// the reply carries item state). On the real Arbiter each is one generic item transaction
    /// (ArbiterServer.exe.c:1266212..1272486): request [ref @6 -&gt; 856-byte atoms][DlmId @0E]
    /// [UserDbId @12], frame &gt;= 0x16; ExecTrans; reply [ref @6 -&gt; the atoms][DlmId @0E][ok @12]
    /// (writers FUN_1406eb9f0 &amp; co.), which World's Handler_DBS_* read at &gt;= 0x13. The echo gets
    /// the allocated ids (op 7/8 outputs) and identify's masterwork write-back, then is applied,
    /// record edits included (ItemEdits). 0x28A1 has a live pair (cap_multiworld 10859 -&gt; 10860):
    /// byte-exact but for the empty passive slots the real Arbiter fills from template data.
    /// SDB_ITEM_MERGE answers [DlmId @6][ok @0A] only; its atoms are applied, not echoed.
    /// Row syntax is DbAckTable's.
    /// </summary>
    public static readonly IReadOnlyDictionary<ushort, DbAckTable.Spec> ItemUpgradeSpecs = new[]
    {
        "275A>275B q22 r19 d14:14 o18 b6=6 k18",   // SDB_ITEM_EXTRACT
        "276E>276F q22 r19 d14:14 o18 b6=6 k18",   // SDB_ITEM_ENCHANT
        "2770>2771 q22 r19 d14:14 o18 b6=6 k18",   // SDB_ITEM_ENCHANT_IDENTIFY
        "28A1>28A2 q22 r19 d14:14 o18 b6=6 k18",   // SDB_ITEM_UNIDENTIFY (= item option reset, op 92)
        "28F4>28F5 q22 r19 d14:14 o18 b6=6 k18",   // SDB_ENCHANT_ITEM_BOOST
        "2920>2921 q22 r19 d14:14 o18 b6=6 k18",   // SDB_ITEM_DECOMPOSITION
        "2932>2933 q22 r19 d14:14 o18 b6=6 k18",   // SDB_ITEM_AWAKEN
        "2934>2935 q22 r19 d14:14 o18 b6=6 k18",   // SDB_ITEM_UNBIND
        "295F>2960 q22 r19 d14:14 o18 b6=6 k18",   // SDB_EQUIPMENT_INHERITANCE
        "2774>2775 q22 r11 d14:6 o18 k10 a6",       // SDB_ITEM_MERGE
    }.Select(r => DbAckTable.Parse(r)).ToDictionary(s => s.Op);

    /// <summary>The reply for one T166 request, with <paramref name="allocateItemId"/> filling op 7/8 ids.</summary>
    public static DbAckTable.Ack BuildItemUpgradeReply(ushort op, byte[] payload, Func<int>? allocateItemId)
    {
        var ack = DbAckTable.Build(ItemUpgradeSpecs[op], payload, allocateItemId);
        foreach (var a in ack.Atoms)
            if (a.RecordSize == ItemAtomSize) ItemEdits.PatchReply(ack.Reply, a.SlotPayloadOffset);
        return ack;
    }

    private bool OnItemUpgrade(WorldLink link, ushort op, byte[] payload)
    {
        var spec = ItemUpgradeSpecs[op];
        var ack = BuildItemUpgradeReply(op, payload, _store is null ? null : new Func<int>(_store.NextItemId));
        int rows = 0;
        if (_store is not null)
        {
            foreach (var a in ack.Atoms)
            {
                var r = BagItems.ApplyReplyAtoms(_store, ack.Reply, a.SlotPayloadOffset, _store.NextItemId, _log, a.RecordSize);
                rows += r.Inserted + r.Moved + r.AmountChanged + r.Deleted;
            }
            foreach (var at in spec.Applies)
            {
                var r = WarehouseHandlers.Apply(_store, WarehouseHandlers.ParseAtoms(payload, at.Req - 6, at.Stride), _store.NextItemId, _log);
                rows += r.Inserted + r.Moved + r.AmountChanged + r.Deleted;
            }
        }
        _log.LogInformation("{Op} -> 0x{Rop:X4}: player {Id} dlm {Dlm}, {Rows} item row(s)",
            DbProxyOpcodeNames.Describe(op), spec.ReplyOp, (int)DbAckTable.U32(payload, spec.ReqOwner - 6), ack.DlmId, rows);
        link.SendFrame(spec.ReplyOp, ack.Reply);
        return true;
    }

    private bool OnGenericAck(WorldLink link, ushort op, byte[] payload)
    {
        var spec = DbAckTable.For(op);
        if (spec is null || DbAckGroups.Refused(op)) return false;   // B/C: DbAckTable already refuses them
        if (!spec.Pinned)
            lock (_unpinnedSeen)
                if (_unpinnedSeen.Add(op))
                    _log.LogInformation("generic ack 0x{Op:X4} {Name} - capture a real pair to pin",
                        op, DbProxyOpcodeNames.Name(op) ?? "?");
        int owner = spec.ReqOwner >= 6 ? (int)DbAckTable.U32(payload, spec.ReqOwner - 6) : 0;
        var ack = DbAckTable.Build(spec, payload, _store is null ? null : new Func<int>(_store.NextItemId));
        int rows = 0;
        if (_store is not null)
            foreach (var a in ack.Atoms)
            {
                var r = BagItems.ApplyReplyAtoms(_store, ack.Reply, a.SlotPayloadOffset, _store.NextItemId, _log, a.RecordSize);
                rows += r.Inserted + r.Moved + r.AmountChanged + r.Deleted;
            }
        // T162: request atoms that are applied but not echoed (the level-jump scroll).
        if (_store is not null)
            foreach (var ap in spec.Applies)
            {
                var r = WarehouseHandlers.Apply(_store, WarehouseHandlers.ParseAtoms(payload, ap.Req - 6, ap.Stride), _store.NextItemId, _log);
                rows += r.Inserted + r.Moved + r.AmountChanged + r.Deleted;
            }
        if (ack.DroppedRefs > 0)
            _log.LogWarning("DbAck {Op}: {N} ref(s) pointed outside the request and went back empty",
                DbProxyOpcodeNames.Describe(op), ack.DroppedRefs);
        _log.LogInformation("DbAck {Op} -> 0x{Rop:X4}: player {Id} dlm {Dlm}{Rows}",
            DbProxyOpcodeNames.Describe(op), spec.ReplyOp, owner, ack.DlmId, rows > 0 ? $", {rows} item row(s)" : "");

        link.SendFrame(spec.ReplyOp, ack.Reply);
        return true;
    }

    // ---- Quests: SDB_SET_QUEST_INFO (0x272E) / SDB_LOAD_QUEST_LIST (0x272C) ----

    /// <summary>
    /// SDB_SET_QUESTLIST_INFO (0x2732), T145: a batch of quest records. Each is stored the way
    /// a single SDB_SET_QUEST_INFO record is (last-write-wins on playerId + questId) and the row
    /// id goes back in the reply. Always answered.
    /// </summary>
    private bool OnSetQuestListInfo(WorldLink link, byte[] payload)
    {
        int playerId = payload.Length >= QuestListRequestHeader ? (int)BitConverter.ToUInt32(payload, 28) : 0;
        if (payload.Length < QuestListRequestHeader)
            _log.LogWarning("SDB_SET_QUESTLIST_INFO: {Len} B payload, want >= {Want} - answering with no quests",
                payload.Length, QuestListRequestHeader);
        var reply = BuildDbs2733(payload, (questId, record) => _store?.UpsertQuest(playerId, questId,
            BitConverter.ToInt32(record, QuestRecordStatusOffset),
            BitConverter.ToInt32(record, QuestRecordStepOffset), record) ?? 0);
        _log.LogInformation("SDB_SET_QUESTLIST_INFO: player {Pid} - {N} quest(s) stored and answered",
            playerId, (reply.Length - QuestListReplyHeader) / 8);
        link.SendFrame(DBS_SET_QUESTLIST_INFO, reply);
        return true;
    }

    /// <summary>
    /// One quest write. Stores the 80-byte record last-write-wins on (playerId, questId) and
    /// replies with the questDbId the row got. See the constants above for the layout.
    /// </summary>
    private bool OnSetQuestInfo(WorldLink link, byte[] payload)
    {
        if (payload.Length < QuestSetRequestHeader)
        {
            _log.LogWarning("SDB_SET_QUEST_INFO too short ({Len} B)", payload.Length);
            return false;
        }
        int recordOffset = (int)BitConverter.ToUInt32(payload, 0) - 6;
        int recordLength = (int)BitConverter.ToUInt32(payload, 4);
        uint sqlType = BitConverter.ToUInt32(payload, 20);
        int playerId = (int)BitConverter.ToUInt32(payload, 24);

        if (recordLength != QuestRecordSize || recordOffset < 0 || recordOffset + recordLength > payload.Length)
        {
            // Reply anyway: an unanswered per-user DB item head-blocks the whole queue
            // (status/HANDOFF.md section 1). Just do not invent a row from a record we cannot read.
            _log.LogWarning("SDB_SET_QUEST_INFO: bad quest record (offset {Off}, length {Len}, payload {P}) for player {Pid} - acking without storing",
                recordOffset + 6, recordLength, payload.Length, playerId);
            link.SendFrame(DBS_SET_QUEST_INFO, BuildDbs272F(payload, questDbId: 0, _store.NextItemId));
            return true;
        }

        var record = new byte[QuestRecordSize];
        Array.Copy(payload, recordOffset, record, 0, QuestRecordSize);
        int questId = (int)BitConverter.ToUInt32(record, QuestRecordQuestIdOffset);
        int status = (int)BitConverter.ToUInt32(record, QuestRecordStatusOffset);
        int step = (int)BitConverter.ToUInt32(record, QuestRecordStepOffset);

        int questDbId = _store.UpsertQuest(playerId, questId, status, step, record);
        _log.LogDebug("SDB_SET_QUEST_INFO: player {Pid} quest {Q} sqlType {T} status {S} step {P} -> questDbId {Db}",
            playerId, questId, sqlType, status, step, questDbId);

        // Only an INSERT write is told the id; every other write already carries it in record+0.
        var questReply = BuildDbs272F(payload, sqlType == QuestSqlTypeInsert ? questDbId : 0, _store.NextItemId);

        // T44: a quest reward is items. The atoms ride in the same 856-byte form as 0x2768 and
        // land in the same rows; the ref pair is at reply[8]/[12], behind the quest record.
        var rewards = BagItems.ApplyReplyAtoms(_store, questReply, 8, _store.NextItemId, _log);
        if (rewards.Inserted + rewards.AmountChanged > 0)
            _log.LogInformation("SDB_SET_QUEST_INFO: quest {Q} rewarded player {Pid} {Ins} new item(s), {Chg} stack change(s)",
                questId, playerId, rewards.Inserted, rewards.AmountChanged);

        link.SendFrame(DBS_SET_QUEST_INFO, questReply);
        return true;
    }

    /// <summary>
    /// DBS_SET_QUEST_INFO (0x272F): the request's quest record and reward atoms under the
    /// 29-byte reply header, with <paramref name="questDbId"/> at [25] and freshly allocated item
    /// ids in any op-7 atom that arrived with 0 (the same rule as <see cref="BuildDbs2769"/>;
    /// <paramref name="allocateItemId"/> is called once per such atom).
    /// </summary>
    public static byte[] BuildDbs272F(byte[] request, int questDbId, Func<int> allocateItemId)
    {
        ArgumentNullException.ThrowIfNull(allocateItemId);
        byte[] record = SliceQuestRecord(request);
        byte[] atoms = CloneAtomList(request, 8, allocateItemId);

        var r = new byte[QuestSetReplyHeader + record.Length + atoms.Length];
        uint recordOffset = 6 + QuestSetReplyHeader;                  // 35, frame-relative
        BitConverter.GetBytes(recordOffset).CopyTo(r, 0);
        BitConverter.GetBytes((uint)record.Length).CopyTo(r, 4);
        BitConverter.GetBytes(recordOffset + (uint)record.Length).CopyTo(r, 8);
        BitConverter.GetBytes((uint)atoms.Length).CopyTo(r, 12);
        if (request.Length >= 24) Array.Copy(request, 16, r, 16, 8);  // reqId, sqlType
        r[24] = 1;                                                    // ok — 1 on every captured reply
        BitConverter.GetBytes(questDbId).CopyTo(r, 25);
        record.CopyTo(r, QuestSetReplyHeader);
        atoms.CopyTo(r, QuestSetReplyHeader + record.Length);
        return r;
    }

    private static byte[] SliceQuestRecord(byte[] request)
    {
        if (request.Length < QuestSetRequestHeader) return Array.Empty<byte>();
        int off = (int)BitConverter.ToUInt32(request, 0) - 6;
        int len = (int)BitConverter.ToUInt32(request, 4);
        if (len != QuestRecordSize || off < 0 || off + len > request.Length) return Array.Empty<byte>();
        var r = new byte[len];
        Array.Copy(request, off, r, 0, len);
        return r;
    }

    /// <summary>
    /// One quest-list load. Rebuilds the reply from the character's stored rows; falls back to
    /// the captured 1377-byte reply for dob (playerId 1) until he has rows of his own.
    /// </summary>
    private bool OnLoadQuestList(WorldLink link, byte[] payload)
    {
        uint reqId = payload.Length >= 4 ? BitConverter.ToUInt32(payload, 0) : 0;
        int playerId = payload.Length >= 8 ? (int)BitConverter.ToUInt32(payload, 4) : 0;

        if (_store is null || (playerId == CapturedQuestPlayerId && _store.CountQuests(playerId) == 0))
        {
            link.SendFrame(0x272D, BuildFromStaticData(
                DbProxyStaticData.QuestListEmpty, DbProxyStaticData.QuestListEmptyReqIdOffset, payload));
            return true;
        }

        var active = _store.GetActiveQuestRecords(playerId);
        var completed = _store.GetCompletedQuestIds(playerId);
        _log.LogInformation("SDB_LOAD_QUEST_LIST: player {Pid} -> {N} active quest(s), {C} completed",
            playerId, active.Count, completed.Count);
        link.SendFrame(0x272D, BuildDbs272D(active, completed, reqId));
        return true;
    }

    /// <summary>Quest-list reply with no completed quests. Kept for the callers that predate T21.</summary>
    public static byte[] BuildDbs272D(IReadOnlyList<byte[]> activeQuestRecords, uint reqId)
        => BuildDbs272D(activeQuestRecords, Array.Empty<int>(), reqId, null);

    /// <summary>
    /// DBS_LOAD_QUEST_LIST (0x272D) built from stored rows: list 0 is the in-progress quest
    /// records, list 2 the completed quest ids as plain u32s, lists 1/3/4 empty (list 3
    /// deliberately so — World then seeds itself and sends the 17 0x2899 items we answer),
    /// list 5 the daily-reset trailer (<see cref="QuestListTrailer"/> unless overridden).
    /// With no rows at all this is byte-identical to cap_newchar.log seq 342; with the capture's
    /// rows and trailer it is byte-identical to arb_world_2026-09-13T11-33-30-680Z.log seq 881.
    /// </summary>
    public static byte[] BuildDbs272D(
        IReadOnlyList<byte[]> activeQuestRecords, IReadOnlyList<int> completedQuestIds,
        uint reqId, byte[]? trailer = null)
    {
        ArgumentNullException.ThrowIfNull(activeQuestRecords);
        ArgumentNullException.ThrowIfNull(completedQuestIds);
        var tail = trailer ?? QuestListTrailer;
        int list0 = activeQuestRecords.Count * QuestRecordSize;
        int list2 = completedQuestIds.Count * 4;
        var r = new byte[QuestListReplyHeader + list0 + list2 + tail.Length];

        uint bodyStart = 6 + QuestListReplyHeader;                 // 59, frame-relative
        uint afterList0 = bodyStart + (uint)list0;
        uint afterList2 = afterList0 + (uint)list2;
        BitConverter.GetBytes(bodyStart).CopyTo(r, 0);
        BitConverter.GetBytes((uint)list0).CopyTo(r, 4);
        BitConverter.GetBytes(afterList0).CopyTo(r, 8);            // list 1: empty
        BitConverter.GetBytes(afterList0).CopyTo(r, 16);           // list 2: completed ids
        BitConverter.GetBytes((uint)list2).CopyTo(r, 20);
        BitConverter.GetBytes(afterList2).CopyTo(r, 24);           // list 3: empty (daily seeds)
        BitConverter.GetBytes(afterList2).CopyTo(r, 32);           // list 4: empty
        BitConverter.GetBytes(afterList2).CopyTo(r, 40);           // list 5: the trailer
        BitConverter.GetBytes((uint)tail.Length).CopyTo(r, 44);
        r[QuestListOkOffset] = 1;
        BitConverter.GetBytes(reqId).CopyTo(r, QuestListReqIdOffset);

        int at = QuestListReplyHeader;
        foreach (var rec in activeQuestRecords)
        {
            if (rec.Length != QuestRecordSize)
                throw new ArgumentException($"quest record must be {QuestRecordSize} bytes, got {rec.Length}", nameof(activeQuestRecords));
            rec.CopyTo(r, at);
            at += QuestRecordSize;
        }
        foreach (int id in completedQuestIds)
        {
            BitConverter.GetBytes(id).CopyTo(r, at);
            at += 4;
        }
        tail.CopyTo(r, at);
        return r;
    }

    /// <summary>
    /// Atom count the request's own header claims for the list at <paramref name="headerOffset"/>
    /// (0 or 8), counted the way the real handler does it: <c>(length - 1) / 0x358 + 1</c>. That
    /// rounds up, so a length that is not a whole number of atoms still reports the atoms World
    /// thinks it sent and the caller can see that we echoed fewer.
    /// </summary>
    public static int DeclaredAtomCount(byte[] request, int headerOffset)
    {
        if (headerOffset + 8 > request.Length) return 0;
        int length = (int)BitConverter.ToUInt32(request, headerOffset + 4);
        return length <= 0 ? 0 : (length - 1) / ItemAtomSize + 1;
    }

    /// <summary>
    /// DBS_ITEM_SINGLE (0x2769): the request's two atom lists echoed back under the 21-byte
    /// reply header, with a freshly allocated item DB id written into every insert atom that
    /// arrived with 0. <paramref name="allocateItemId"/> is called once per such atom, in list
    /// order — pass <c>CharacterStore.NextItemId</c>.
    ///
    /// <para>A list whose header does not describe a whole number of 856-byte atoms inside the
    /// payload is echoed as empty rather than half-copied; the caller compares
    /// <see cref="DeclaredAtomCount"/> against the reply and logs the mismatch. Replying with a
    /// short list is bad, but not replying at all head-blocks the user's DLM queue forever
    /// (status/HANDOFF.md section 1).</para>
    /// </summary>
    public static byte[] BuildDbs2769(byte[] request, Func<int> allocateItemId)
    {
        ArgumentNullException.ThrowIfNull(allocateItemId);
        uint reqId = request.Length >= ItemSingleRequestHeader ? BitConverter.ToUInt32(request, 16) : 0;

        byte[] listA = CloneAtomList(request, 0, allocateItemId);
        byte[] listB = CloneAtomList(request, 8, allocateItemId);

        var r = new byte[ItemSingleReplyHeader + listA.Length + listB.Length];
        uint offA = 6 + ItemSingleReplyHeader;                 // 27, frame-relative
        uint offB = offA + (uint)listA.Length;
        BitConverter.GetBytes(offA).CopyTo(r, 0);
        BitConverter.GetBytes((uint)listA.Length).CopyTo(r, 4);
        BitConverter.GetBytes(offB).CopyTo(r, 8);
        BitConverter.GetBytes((uint)listB.Length).CopyTo(r, 12);
        BitConverter.GetBytes(reqId).CopyTo(r, 16);
        r[20] = 1;                                             // ok — 1 on every captured reply
        listA.CopyTo(r, ItemSingleReplyHeader);
        listB.CopyTo(r, ItemSingleReplyHeader + listA.Length);
        return r;
    }

    /// <summary>
    /// Copy one ItemTransactionAtom list out of a request, allocating item DB ids for the
    /// inserts in it. Shared by 0x2768 (T13), 0x272E and 0x278E (T15) - all three carry the
    /// same 856-byte atoms behind an [offset][length] pair, they just have different headers,
    /// hence <paramref name="minStart"/>: an offset pointing inside the header is malformed.
    /// </summary>
    private static byte[] CloneAtomList(byte[] request, int headerOffset, Func<int> allocateItemId,
                                        int minStart = ItemSingleRequestHeader, int recordSize = 0)
    {
        if (recordSize <= 0) recordSize = ItemAtomSize;
        if (headerOffset + 8 > request.Length) return Array.Empty<byte>();
        int frameOffset = (int)BitConverter.ToUInt32(request, headerOffset);
        int length = (int)BitConverter.ToUInt32(request, headerOffset + 4);
        if (length <= 0) return Array.Empty<byte>();

        int start = frameOffset - 6;                            // offsets in the frame count the header
        // `length > request.Length - start` rather than `start + length > request.Length`: a
        // garbage offset can be big enough that the sum overflows int and passes the check.
        if (start < minStart || length % recordSize != 0 || length > request.Length - start)
            return Array.Empty<byte>();

        var atoms = new byte[length];
        Array.Copy(request, start, atoms, 0, length);
        for (int o = 0; o + recordSize <= atoms.Length; o += recordSize)
        {
            // T153: op 8 (the non-stackable insert) is allocated exactly like 7 - cap_final.log
            // 5914 -> 5915 is byte-exact with the id at +0x10 and nothing else changed.
            if (BitConverter.ToUInt32(atoms, o + ItemAtomOpOffset)
                    is not (TsInsertItem or WarehouseHandlers.TsInsertNonStackItem)) continue;
            // An insert that already carries an id is World re-stating one it knows; only a 0
            // means "give me one".
            if (BitConverter.ToUInt32(atoms, o + ItemAtomDbIdOffset) != 0) continue;
            BitConverter.GetBytes(allocateItemId()).CopyTo(atoms, o + ItemAtomDbIdOffset);
        }
        return atoms;
    }


    // =====================================================================
    // T15 builders. Every one is verified byte-for-byte against data/cap_t15.bin in the tests.
    // (0x272E/0x272F is handled by T17's OnSetQuestInfo above; the questDbId is the quests row id.)
    // =====================================================================

    /// <summary>SDB_USER_LEARN_SKILL (0x278E) -&gt; DBS_USER_LEARN_SKILL (0x278F).</summary>
    private bool OnUserLearnSkill(WorldLink link, byte[] payload)
    {
        uint skillId = payload.Length >= 20 ? BitConverter.ToUInt32(payload, 16) : 0;
        uint playerId = payload.Length >= 16 ? BitConverter.ToUInt32(payload, 12) : 0;
        int declaredAtoms = DeclaredAtomCount(payload, 0);
        var reply = BuildDbs278F(payload, _store.NextItemId);

        int echoedAtoms = (int)BitConverter.ToUInt32(reply, 4) / ItemAtomSize;
        if (echoedAtoms != declaredAtoms)
            _log.LogWarning("SDB_USER_LEARN_SKILL: could not echo the fee atoms (declared {D}, echoed {E}, "
                + "payload {Len} B)", declaredAtoms, echoedAtoms, payload.Length);

        _log.LogInformation("SDB_USER_LEARN_SKILL: player {Pid} learned skill {Skill} ({N} atom(s))",
            playerId, skillId, declaredAtoms);

        // T44: the fee atoms take the money and any consumed item out of the bag. Same rows,
        // same rule - applied from the reply, whose ref pair is at [0]/[4].
        if (_store is not null) BagItems.ApplyReplyAtoms(_store, reply, 0, _store.NextItemId, _log);

        link.SendFrame(DBS_USER_LEARN_SKILL, reply);
        return true;
    }

    /// <summary>
    /// SDB_ACCOMPLISH_USER_ACHIEVEMENT (0x2802) -&gt; DBS (0x2803). Every record is offered to the
    /// store, which keeps the first one per achievement; the reply carries only the ones that
    /// were actually new, which is what makes a re-submitted achievement come back as the
    /// 19-byte empty form the real Arbiter sent (cap_newchar.log seq 2606).
    ///
    /// <para><b>T203: server firsts.</b> A record whose <c>serverUnique</c>
    /// (<see cref="AchievementRecordServerUniqueOffset"/>) is non-zero is gated on the
    /// planet-wide claim table as well, because "Server's first Valkyrie to reach level 30" can
    /// only ever be true of one character. We were granting every one of them to everybody.
    /// There is <b>no refusal reply</b> to write: <c>0x2803</c>'s success byte is a hard-coded 1
    /// in the real Arbiter (Arb_part_062.c:19023, and all fifty replies in cap_final2b agree) and
    /// the refusal IS the record's absence from the list - World's
    /// <c>AchievementManager::SetAccomplishedAchievementList</c> (WorldServer.exe.c:2195421)
    /// walks only the records it was handed and has no failure branch, so the client hears
    /// nothing either. cap_final2b 3622 -&gt; 3623 is the pin: twelve ids offered for userDbId
    /// 1003, the seven with serverUnique 0 granted and exactly the five with serverUnique 1 -
    /// 730, 731, 732, 737, 2103 - left out, all five already held by userDbId 1.</para>
    /// </summary>
    private bool OnAccomplishUserAchievement(WorldLink link, byte[] payload)
    {
        int playerId = payload.Length >= AchievementRequestHeader
            ? (int)BitConverter.ToUInt32(payload, 12) : 0;

        var offeredRecords = SliceAchievementRecords(payload);
        var kept = offeredRecords;                     // no store: every record looks new
        int refused = 0;
        if (_store is not null && playerId > 0)
        {
            long partyId = PartyOfForAchievement(playerId);
            var offered = new List<(int Id, byte[] Record)>(offeredRecords.Count);
            foreach (var rec in offeredRecords)
            {
                int id = (int)BitConverter.ToUInt32(rec, AchievementRecordIdOffset);
                uint unique = BitConverter.ToUInt32(rec, AchievementRecordServerUniqueOffset);
                if (unique != 0 && !MayClaimServerFirst(id, unique, playerId, partyId)) { refused++; continue; }
                offered.Add((id, rec));
            }
            kept = _store.AddAccomplishedAchievements(playerId, offered);
        }

        if (refused > 0)
            _log.LogInformation("SDB_ACCOMPLISH_USER_ACHIEVEMENT: player {Pid} offered {O}, {N} newly accomplished, "
                + "{R} server-first already taken", playerId, offeredRecords.Count, kept.Count, refused);
        else
            _log.LogInformation("SDB_ACCOMPLISH_USER_ACHIEVEMENT: player {Pid} offered {O}, {N} newly accomplished",
                playerId, offeredRecords.Count, kept.Count);
        link.SendFrame(DBS_ACCOMPLISH_USER_ACHIEVEMENT, BuildDbs2803(payload, null, kept));
        return true;
    }

    /// <summary>
    /// T203. The gate on one server-first achievement, in the real Arbiter's order
    /// (<c>User::AccomplishAchievement</c>, Arb_part_027.c:16554): claim it if nobody holds it,
    /// pass it through when the holder is this character (the per-character table then decides,
    /// and refuses a repeat by itself), and for the party-shared kinds let the party that first
    /// claimed it through too. Everyone else is refused.
    /// </summary>
    private bool MayClaimServerFirst(int achievementId, uint serverUnique, int playerId, long partyId)
    {
        var claim = _store!.GetServerAchievementClaim(achievementId);
        if (claim is null) return _store.TryClaimServerAchievement(achievementId, playerId, partyId);
        if (claim.OwnerId == playerId) return true;
        return serverUnique >= AchievementServerUniqueParty && partyId != 0 && claim.PartyId == partyId;
    }

    /// <summary>The party this character is in, or 0. Only the party-shared kinds look at it.</summary>
    private static long PartyOfForAchievement(int playerId)
        => PartyWiring.Manager.FindByMember(playerId)?.Id ?? 0;

    /// <summary>The 24-byte records carried by a 0x2802 request, in order. Empty when malformed.</summary>
    public static List<byte[]> SliceAchievementRecords(byte[] request)
    {
        var recs = new List<byte[]>();
        if (request is null || request.Length < 8) return recs;
        int start = (int)BitConverter.ToUInt32(request, 0) - 6;      // frame-relative
        int len = (int)BitConverter.ToUInt32(request, 4);
        if (start < AchievementRequestHeader || len <= 0 || len % AchievementRecordSize != 0
            || start > request.Length || len > request.Length - start) return recs;
        for (int o = start; o + AchievementRecordSize <= start + len; o += AchievementRecordSize)
            recs.Add(request[o..(o + AchievementRecordSize)]);
        return recs;
    }

    /// <summary>
    /// SDB_UPDATE_USER_ACHIEVEMENT (0x27FA): store the whole payload verbatim, then ack. The ack
    /// is sent whether or not the store took it - an unanswered 0x27FA head-blocks the user's DB
    /// queue for the life of the World process (status/HANDOFF.md section 1).
    /// </summary>
    private bool OnSaveUserAchievement(WorldLink link, byte[] payload)
    {
        int playerId = payload.Length >= AchievementSavePlayerIdOffset + 4
            ? (int)BitConverter.ToUInt32(payload, AchievementSavePlayerIdOffset) : 0;
        if (_store is not null && playerId > 0)
        {
            if (_store.SaveAchievements(playerId, payload))
                _log.LogInformation("SDB_UPDATE_USER_ACHIEVEMENT: stored {Len} B for player {Pid}",
                    payload.Length, playerId);
        }
        link.SendFrame(DBS_SAVE_27FB, BuildReqIdAck(payload, AchievementSaveReqIdOffset));
        return true;
    }

    /// <summary>
    /// SDB_LOAD_USER_ACHIEVEMENT (0x27F8) -&gt; DBS_LOAD_USER_ACHIEVEMENT (0x27F9), rebuilt from the
    /// stored 0x27FA payload and the accomplished list. A character that has never saved gets the
    /// brand-new-character reply, byte-identical to cap_newchar.log seq 344.
    /// </summary>
    private bool OnLoadUserAchievement(WorldLink link, byte[] payload)
    {
        uint reqId = payload.Length >= 4 ? BitConverter.ToUInt32(payload, 0) : 0;
        int playerId = payload.Length >= 8 ? (int)BitConverter.ToUInt32(payload, 4) : 0;

        if (_store is null || ServesCapturedStatics(playerId))
        {
            link.SendFrame(DBS_LOAD_USER_ACHIEVEMENT, BuildFromStaticData(
                DbProxyStaticData.Achievement, DbProxyStaticData.AchievementReqIdOffset, payload));
            return true;
        }

        var saved = playerId <= 0 ? null : _store.GetAchievements(playerId);
        var done = playerId <= 0 ? new List<byte[]>() : _store.GetAccomplishedAchievements(playerId);

        _log.LogInformation(
            "SDB_LOAD_USER_ACHIEVEMENT: player {Pid} -> {What}, {N} accomplished achievement(s)",
            playerId, saved == null ? "brand-new-character reply" : $"{saved.Length} B of stored progress",
            done.Count);
        link.SendFrame(DBS_LOAD_USER_ACHIEVEMENT, BuildDbs27F9(saved, done, reqId));
        return true;
    }

    /// <summary>
    /// Split a "[u32 offset][u32 length] x N, then the bodies" payload into its N lists. Offsets
    /// are frame-relative. A list whose offset or length does not fit the payload comes back
    /// empty rather than throwing - the reply still has to go out.
    /// </summary>
    public static byte[][] SplitOffsetLengthLists(byte[] payload, int listCount)
    {
        var lists = new byte[listCount][];
        for (int i = 0; i < listCount; i++)
        {
            lists[i] = Array.Empty<byte>();
            if (payload is null || (i + 1) * 8 > payload.Length) continue;
            int off = (int)BitConverter.ToUInt32(payload, i * 8) - 6;
            int len = (int)BitConverter.ToUInt32(payload, i * 8 + 4);
            if (len <= 0 || off < 0 || off > payload.Length || len > payload.Length - off) continue;
            lists[i] = payload[off..(off + len)];
        }
        return lists;
    }

    /// <summary>
    /// DBS_LOAD_USER_ACHIEVEMENT (0x27F9) from the stored 0x27FA payload plus the accomplished
    /// records. Every one of the 38 offset slots carries the running body position, empty lists
    /// included - that is what the real Arbiter's backpatching produces.
    ///
    /// <para>Byte-exact against all three captured replies: cap_newchar.log seq 344 (brand-new,
    /// no save), and arb_world_2026-09-13 seq 397 (dob) and seq 883 (Test) rebuilt from their
    /// own 0x27FA saves.</para>
    ///
    /// <para>DEVIATION: the Data blob is served exactly as World last sent it. The real Arbiter
    /// fills in two u16s of it that World always sends as zero (Data+1052 and Data+1180 - the
    /// same value in both, and it tracks the character's achievement progress). We have no way
    /// to compute them, so a relog sees zeros there. Nothing in any capture shows World reading
    /// them back. status/ACHIEVEMENTS.md.</para>
    /// </summary>
    public static byte[] BuildDbs27F9(byte[]? savePayload, IReadOnlyList<byte[]> accomplished, uint reqId)
    {
        ArgumentNullException.ThrowIfNull(accomplished);
        var lists = new byte[AchievementLoadListCount][];
        for (int i = 0; i < lists.Length; i++) lists[i] = Array.Empty<byte>();
        lists[0] = FreshAchievementData();

        if (savePayload != null)
        {
            var saved = SplitOffsetLengthLists(savePayload, AchievementSaveListCount);
            if (saved[0].Length == AchievementDataSize) lists[0] = saved[0];
            for (int i = 1; i < AchievementSaveListCount; i++)
                lists[AchievementSaveToLoadList[i]] = saved[i];
        }

        int accomplishedBytes = 0;
        foreach (var rec in accomplished) accomplishedBytes += rec.Length;
        var accList = new byte[accomplishedBytes];
        int a = 0;
        foreach (var rec in accomplished) { rec.CopyTo(accList, a); a += rec.Length; }
        lists[AchievementAccomplishedListIndex] = accList;

        int body = 0;
        foreach (var l in lists) body += l.Length;
        var r = new byte[AchievementLoadHeader + body];

        uint at = 6 + (uint)AchievementLoadHeader;                   // 315, frame-relative
        for (int i = 0; i < lists.Length; i++)
        {
            BitConverter.GetBytes(at).CopyTo(r, i * 8);
            BitConverter.GetBytes((uint)lists[i].Length).CopyTo(r, i * 8 + 4);
            at += (uint)lists[i].Length;
        }
        BitConverter.GetBytes(reqId).CopyTo(r, AchievementLoadReqIdOffset);
        r[AchievementLoadOkOffset] = 1;

        int p = AchievementLoadHeader;
        foreach (var l in lists) { l.CopyTo(r, p); p += l.Length; }
        return r;
    }

    /// <summary>
    /// DBS_SET_QUEST_INFO (0x272F): the 29-byte header, the 80-byte quest record copied straight
    /// back, and the reward atom list with an item DB id filled into every op-7 atom that arrived
    /// with 0 (the T13 rule - see <see cref="BuildDbs2769"/>).
    ///
    /// <para><paramref name="allocateQuestDbId"/> is called ONLY for sqlType
    /// <see cref="QuestSqlInsert"/>; every other type gets 0, exactly as in the capture. That is
    /// the row id World reads back at payload[25] and hands to DBStartQuestContext::SetQuestDbId.</para>
    ///
    /// <para>The record length in the reply is always <see cref="QuestInfoRecordSize"/> because the
    /// Arbiter parses the request into a fixed 80-byte struct and writes all 80 bytes back. A
    /// request whose record is short, missing or out of bounds therefore gets zeros for the
    /// remainder rather than no reply - not replying head-blocks the user forever.</para>
    /// </summary>
    public static byte[] BuildDbs272F(byte[] request, Func<int> allocateItemId, Func<int> allocateQuestDbId)
    {
        ArgumentNullException.ThrowIfNull(allocateItemId);
        ArgumentNullException.ThrowIfNull(allocateQuestDbId);

        uint reqId   = request.Length >= 20 ? BitConverter.ToUInt32(request, 16) : 0;
        uint sqlType = request.Length >= 24 ? BitConverter.ToUInt32(request, 20) : 0;

        var record = new byte[QuestInfoRecordSize];
        if (request.Length >= 8)
        {
            int start = (int)BitConverter.ToUInt32(request, 0) - 6;   // frame-relative
            int len = Math.Min((int)BitConverter.ToUInt32(request, 4), QuestInfoRecordSize);
            // `len <= request.Length - start` rather than `start + len <= request.Length`: a
            // garbage offset can be big enough that the sum overflows and passes the check.
            if (start >= QuestInfoRequestHeader && len > 0 && len <= request.Length - start)
                Array.Copy(request, start, record, 0, len);
        }

        byte[] atoms = CloneAtomList(request, 8, allocateItemId, QuestInfoRequestHeader);
        uint questDbId = sqlType == QuestSqlInsert ? (uint)allocateQuestDbId() : 0;

        var r = new byte[QuestInfoReplyHeader + QuestInfoRecordSize + atoms.Length];
        uint offRecord = 6 + QuestInfoReplyHeader;                    // 35, frame-relative
        BitConverter.GetBytes(offRecord).CopyTo(r, 0);
        BitConverter.GetBytes((uint)QuestInfoRecordSize).CopyTo(r, 4);
        BitConverter.GetBytes(offRecord + (uint)QuestInfoRecordSize).CopyTo(r, 8);
        BitConverter.GetBytes((uint)atoms.Length).CopyTo(r, 12);
        BitConverter.GetBytes(reqId).CopyTo(r, 16);
        BitConverter.GetBytes(sqlType).CopyTo(r, 20);
        r[24] = 1;                                                    // ok - 1 on every captured reply
        BitConverter.GetBytes(questDbId).CopyTo(r, 25);
        record.CopyTo(r, QuestInfoReplyHeader);
        atoms.CopyTo(r, QuestInfoReplyHeader + QuestInfoRecordSize);
        return r;
    }

    /// <summary>
    /// DBS_USER_LEARN_SKILL (0x278F): the 23-byte header, the request's atom list echoed back with
    /// insert ids filled in, and an EMPTY SkillPeriodData list. World reads the second list, not
    /// the atoms, so the empty list is what tells it "no timed skills" - which is what the real
    /// Arbiter sent in the capture.
    /// </summary>
    public static byte[] BuildDbs278F(byte[] request, Func<int> allocateItemId)
    {
        ArgumentNullException.ThrowIfNull(allocateItemId);
        uint reqId = request.Length >= 12 ? BitConverter.ToUInt32(request, 8) : 0;
        byte[] atoms = CloneAtomList(request, 0, allocateItemId, LearnSkillRequestHeader);

        var r = new byte[LearnSkillReplyHeader + atoms.Length];
        uint offAtoms = 6 + LearnSkillReplyHeader;                    // 29, frame-relative
        BitConverter.GetBytes(offAtoms).CopyTo(r, 0);
        BitConverter.GetBytes((uint)atoms.Length).CopyTo(r, 4);
        BitConverter.GetBytes(offAtoms + (uint)atoms.Length).CopyTo(r, 8);
        // r[12..15] = 0  - the SkillPeriodData list is empty
        BitConverter.GetBytes(reqId).CopyTo(r, 16);
        r[20] = 1;   // ok
        // r[21] = 0  - hasSkillPeriodList; r[22] = 0 - wasAlreadyLearned. Both 0 in the capture.
        atoms.CopyTo(r, LearnSkillReplyHeader);
        return r;
    }

    /// <summary>
    /// DBS_ACCOMPLISH_USER_ACHIEVEMENT (0x2803): the 13-byte header plus the records the Arbiter
    /// considers NEWLY accomplished.
    ///
    /// <para><paramref name="keptRecords"/> is the set the reply should carry - what the store
    /// accepted as new (T22). Pass null and every record in the request goes back, which is what
    /// TeraSharp did before there was an achievement table. <paramref name="isNewlyAccomplished"/>
    /// is the older per-record predicate, kept for the tests that pin the two captured forms:
    /// return false for everything and the reply collapses to the 19-byte form the real Arbiter
    /// sent at cap_newchar.log seq 2606.</para>
    /// </summary>
    public static byte[] BuildDbs2803(byte[] request, Func<byte[], bool>? isNewlyAccomplished = null,
                                      IReadOnlyList<byte[]>? keptRecords = null)
    {
        uint reqId = request.Length >= 12 ? BitConverter.ToUInt32(request, 8) : 0;

        var kept = new List<byte[]>();
        foreach (var rec in keptRecords ?? SliceAchievementRecords(request))
            if (isNewlyAccomplished == null || isNewlyAccomplished(rec)) kept.Add(rec);

        int total = kept.Count * AchievementRecordSize;
        var r = new byte[AchievementReplyHeader + total];
        BitConverter.GetBytes(6u + (uint)AchievementReplyHeader).CopyTo(r, 0);   // 19, frame-relative
        BitConverter.GetBytes((uint)total).CopyTo(r, 4);
        BitConverter.GetBytes(reqId).CopyTo(r, 8);
        r[12] = 1;                                                   // ok - hard-coded 1 in the writer
        int p = AchievementReplyHeader;
        foreach (var rec in kept) { rec.CopyTo(r, p); p += AchievementRecordSize; }
        return r;
    }

    // ================================================================================
    // T26 - reputations and fatigability, the last two per-character login loads.
    // Full write-up: status/REPUTATION-FATIGABILITY.md.
    // ================================================================================

    /// <summary>
    /// SDB_UPDATE_REPUTATION_INFO (0x2891): store the 52-byte ReputationData, then ack.
    /// The real Arbiter keeps the struct verbatim in a std::map keyed on record+4
    /// (ReputationDataManager::AddNewReputationInfo / ::UpdateReputationInfo) and
    /// ::GetAllReputationData copies it straight back out, so storing the bytes is what it does.
    /// </summary>
    private bool OnUpdateReputation(WorldLink link, byte[] payload)
    {
        var record = SliceReputationRecord(payload);
        uint op = payload.Length >= ReputationUpdateOpOffset + 4
            ? BitConverter.ToUInt32(payload, ReputationUpdateOpOffset) : 0;
        int playerId = payload.Length >= 16 ? (int)BitConverter.ToUInt32(payload, 12) : 0;

        if (_store is not null && playerId > 0 && record != null && IsReputationWriteOp(op))
        {
            int reputationId = (int)BitConverter.ToUInt32(record, ReputationRecordIdOffset);
            _store.UpsertReputation(playerId, reputationId, record);
            _log.LogInformation("SDB_UPDATE_REPUTATION_INFO: player {Pid} reputation {Rep} (op {Op})",
                playerId, reputationId, op);
        }
        link.SendFrame(DBS_UPDATE_REPUTATION_INFO, BuildDbs2892(payload));
        return true;
    }

    /// <summary>The 52-byte ReputationData a 0x2891 request carries, or null when malformed.</summary>
    public static byte[]? SliceReputationRecord(byte[] request)
    {
        if (request is null || request.Length < ReputationUpdateHeader) return null;
        int off = (int)BitConverter.ToUInt32(request, 0) - 6;
        int len = (int)BitConverter.ToUInt32(request, 4);
        if (len != ReputationRecordSize || off < ReputationUpdateHeader
            || off > request.Length || len > request.Length - off) return null;
        return request[off..(off + len)];
    }

    /// <summary>SDB_LOAD_REPUTATION_LIST (0x288F) -&gt; 0x2890, rebuilt from the stored records.</summary>
    private bool OnLoadReputationList(WorldLink link, byte[] payload)
    {
        uint reqId = payload.Length >= 4 ? BitConverter.ToUInt32(payload, 0) : 0;
        int playerId = payload.Length >= 8 ? (int)BitConverter.ToUInt32(payload, 4) : 0;
        if (_store is null || ServesCapturedStatics(playerId))
        {
            link.SendFrame(DBS_REPUTATION_LIST, BuildFromStaticData(
                DbProxyStaticData.Reputation, DbProxyStaticData.ReputationReqIdOffset, payload));
            return true;
        }
        var rows = playerId <= 0 ? new List<byte[]>() : _store.GetReputations(playerId);
        if (rows.Count > 0)
            _log.LogInformation("SDB_LOAD_REPUTATION_LIST: player {Pid} -> {N} reputation(s)", playerId, rows.Count);
        link.SendFrame(DBS_REPUTATION_LIST, BuildDbs2890(rows, reqId, playerId));
        return true;
    }

    /// <summary>
    /// DBS_LOAD_REPUTATION_LIST (0x2890): [0] u32 listOff=19 [4] u32 listLen [8] u8 Success=1
    /// [9] u32 DlmId, then 52 bytes per reputation, ordered by reputation id.
    /// <para>Success is the Arbiter's "I found the User object", not a data flag, so it is 1
    /// whenever we answer at all.</para>
    ///
    /// <para>Two bytes of each record are put back the way the Arbiter's DB loader puts them,
    /// not the way World sent them: <see cref="ReputationRecordOwnerOffset"/> gets
    /// <paramref name="ownerDbId"/> - which is what makes dob's and Test's otherwise identical
    /// records differ in the capture - and <see cref="ReputationRecordResidueOffset"/> gets
    /// <see cref="ReputationLoaderResidue"/>.</para>
    ///
    /// <para>Byte-exact against all three captured replies: cap_newchar.log seq 336 (no rows),
    /// and arb_world_2026-09-13 seq 389 (dob) and seq 875 (Test) rebuilt from the one 0x2891
    /// write in cap_newchar.log.</para>
    /// </summary>
    public static byte[] BuildDbs2890(IReadOnlyList<byte[]> records, uint reqId, int ownerDbId)
    {
        ArgumentNullException.ThrowIfNull(records);
        int len = 0;
        foreach (var rec in records)
        {
            if (rec.Length != ReputationRecordSize)
                throw new ArgumentException(
                    $"ReputationData must be {ReputationRecordSize} bytes, got {rec.Length}", nameof(records));
            len += rec.Length;
        }
        var r = new byte[ReputationReplyHeader + len];
        BitConverter.GetBytes(6u + ReputationReplyHeader).CopyTo(r, 0);   // 19, frame-relative
        BitConverter.GetBytes((uint)len).CopyTo(r, 4);
        r[8] = 1;                                                          // Success
        BitConverter.GetBytes(reqId).CopyTo(r, 9);
        int p = ReputationReplyHeader;
        foreach (var rec in records)
        {
            rec.CopyTo(r, p);
            BitConverter.GetBytes(ownerDbId).CopyTo(r, p + ReputationRecordOwnerOffset);
            BitConverter.GetBytes(ReputationLoaderResidue).CopyTo(r, p + ReputationRecordResidueOffset);
            p += rec.Length;
        }
        return r;
    }

    /// <summary>
    /// SDB_UPDATE_FATIGABILITY_POINT (0x2910, the const is still called SDB_LOAD_FRIEND_INFO):
    /// add the delta to the ACCOUNT's running total, stamp the time, then ack.
    /// </summary>
    private bool OnUpdateFatigability(WorldLink link, byte[] payload)
    {
        if (_store is not null && payload.Length >= FatigabilityUpdateDeltaOffset + 4)
        {
            int playerId = (int)BitConverter.ToUInt32(payload, 4);
            int delta = (int)BitConverter.ToUInt32(payload, FatigabilityUpdateDeltaOffset);
            var chr = playerId > 0 ? _store.GetCharacter(playerId) : null;
            if (chr != null)
            {
                var row = _store.AddFatigabilityPoints(chr.AccountId, delta, EncodeDbDateTime(DateTime.UtcNow));
                _log.LogInformation(
                    "SDB_UPDATE_FATIGABILITY_POINT: account {Acc} +{D} fatigue -> {Total}",
                    chr.AccountId, delta, row.CurPoint);
            }
        }
        link.SendFrame(DBS_FATIGABILITY_UPDATE, BuildOkReqId(payload, 0));
        return true;
    }

    /// <summary>SDB_LOAD_FATIGABILITY_LIST (0x2908) -&gt; 0x2909, from the account's row.</summary>
    private bool OnLoadFatigability(WorldLink link, byte[] payload)
    {
        uint reqId = payload.Length >= 4 ? BitConverter.ToUInt32(payload, 0) : 0;
        int playerId = payload.Length >= 8 ? (int)BitConverter.ToUInt32(payload, 4) : 0;
        var chr = _store is null || playerId <= 0 ? null : _store.GetCharacter(playerId);
        if (chr == null)
        {
            link.SendFrame(DBS_FATIGABILITY_LIST, BuildDbs2909(0, null, reqId));
            return true;
        }
        var row = _store!.GetFatigability(chr.AccountId);
        _log.LogInformation("SDB_LOAD_FATIGABILITY_LIST: account {Acc} -> {P} fatigue point(s)",
            chr.AccountId, row.CurPoint);
        link.SendFrame(DBS_FATIGABILITY_LIST, BuildDbs2909(row.CurPoint, row.Timestamp, reqId));
        return true;
    }

    /// <summary>
    /// DBS_LOAD_FATIGABILITY_LIST (0x2909). Always exactly one 28-byte element, which is what
    /// every captured reply carries: [u32 kind=1][u32 curPoint][16-B TIMESTAMP][u32].
    ///
    /// <para>The trailing u32 is sent as 0: nothing in five captured replies explains it
    /// (630, 443, 426, 1626, 88 with no relation to the point, the timestamp or the elapsed
    /// time), and World copies the element into FatigabilityInfo without reading it.</para>
    /// </summary>
    public static byte[] BuildDbs2909(int curPoint, byte[]? timestamp, uint reqId)
    {
        var r = new byte[FatigabilityReplyHeader + FatigabilityRecordSize];
        BitConverter.GetBytes(6u + FatigabilityReplyHeader).CopyTo(r, 0);       // 23, frame-relative
        BitConverter.GetBytes((uint)FatigabilityRecordSize).CopyTo(r, 4);
        r[8] = 1;                                                               // Success
        BitConverter.GetBytes(reqId).CopyTo(r, 9);
        // [13] AddtionalFatiguePoint: 0 in every captured reply.
        int p = FatigabilityReplyHeader;
        BitConverter.GetBytes(FatigabilityKind).CopyTo(r, p);
        BitConverter.GetBytes(curPoint).CopyTo(r, p + 4);
        (timestamp is { Length: 16 } t ? t : DungeonCoolTimeNever).CopyTo(r, p + 8);
        return r;
    }

    /// <summary>
    /// The Arbiter's 16-byte wire DateTime - an ODBC <c>tagTIMESTAMP_STRUCT</c>:
    /// u16 year, month, day, hour, minute, second, then a u32 fraction. Proven by the
    /// accomplished-achievement records (2026-09-13 05:51:45 in a capture that starts 05:49:03)
    /// and by the fatigue timestamps tracking each 0x2910 write.
    /// </summary>
    public static byte[] EncodeDbDateTime(DateTime when)
    {
        var b = new byte[16];
        BitConverter.GetBytes((ushort)when.Year).CopyTo(b, 0);
        BitConverter.GetBytes((ushort)when.Month).CopyTo(b, 2);
        BitConverter.GetBytes((ushort)when.Day).CopyTo(b, 4);
        BitConverter.GetBytes((ushort)when.Hour).CopyTo(b, 6);
        BitConverter.GetBytes((ushort)when.Minute).CopyTo(b, 8);
        BitConverter.GetBytes((ushort)when.Second).CopyTo(b, 10);
        return b;                                                  // the fraction stays 0
    }

    /// <summary>
    /// DBS_UPDATE_REPUTATION_INFO (0x2892): [u8 ok][u32 reqId] — 5 bytes. The ok byte comes FIRST
    /// here; 0x2891 is the only request in this batch with that ordering.
    /// The real Arbiter leaves ok = 0 for an op it does not implement (only 1 = insert and
    /// 2 / 4 = update reach a ReputationList call), and ok = 0 still completes the DLM item -
    /// it just selects OnFail. Copy that rather than hardcoding 1.
    /// </summary>
    public static byte[] BuildDbs2892(byte[] request)
    {
        uint reqId = request.Length >= 12 ? BitConverter.ToUInt32(request, 8) : 0;
        uint op = request.Length >= 20 ? BitConverter.ToUInt32(request, 16) : 0;
        var r = new byte[5];
        r[0] = (byte)(op is 1u or 2u or 4u ? 1 : 0);
        BitConverter.GetBytes(reqId).CopyTo(r, 1);
        return r;
    }

    /// <summary>
    /// DBS_UPDATE_SEREN_GUIDE_INFO (0x2945): [u32 reqId][u32 playerId][u8 ok=1] — 9 bytes.
    /// Both are echoed from the request (payload[0] and payload[4]).
    /// </summary>
    // ---- Tutorial tips and the seren guide (T22) ----

    /// <summary>
    /// The captured character (playerId 1, "dob") keeps the recorded static reply for the loads
    /// T22 rebuilt, exactly as he keeps the captured quest list: his rows do not exist in our DB
    /// and a rebuilt reply would silently drop everything he has.
    /// </summary>
    private bool ServesCapturedStatics(int playerId) => playerId == CapturedQuestPlayerId;

    /// <summary>SDB_ADD_TUTORIAL_SIMPLE_TIP (0x286E): store the tip, then ack.</summary>
    private bool OnAddTutorialTip(WorldLink link, byte[] payload)
    {
        bool ok = false;
        if (_store is not null && payload.Length >= TutorialTipIdOffset + 4)
        {
            int playerId = (int)BitConverter.ToUInt32(payload, 4);
            int tipId = (int)BitConverter.ToUInt32(payload, TutorialTipIdOffset);
            ok = playerId > 0 && _store.AddTutorialTip(playerId, tipId);
            if (ok)
                _log.LogInformation("SDB_ADD_TUTORIAL_SIMPLE_TIP: player {Pid} saw tip {Tip}", playerId, tipId);
        }
        var ack = BuildReqIdAck(payload, 0);
        ack[4] = ok || (_store is null && payload.Length >= 12) ? (byte)1 : (byte)0;
        link.SendFrame(DBS_ADD_TUTORIAL_SIMPLE_TIP, ack);
        return true;
    }

    // Native Arb_part_063.c:5317 calls the same additive UpdateSimpleTip as ADD, passing
    // frame+18 (popup-count delta). There is no separate per-character don't-repeat flag.
    private bool OnDontRepeatTutorialTip(WorldLink link, byte[] payload)
    {
        bool ok = payload.Length >= 16 && _store is not null &&
            _store.AddTutorialTipCount(BitConverter.ToInt32(payload, 4),
                BitConverter.ToInt32(payload, 8), BitConverter.ToInt32(payload, 12));
        var ack = BuildReqIdAck(payload, 0);
        ack[4] = ok ? (byte)1 : (byte)0;
        link.SendFrame(DBS_DONT_REPEAT_TUTORIAL_SIMPLE_TIP, ack);
        return true;
    }

    /// <summary>SDB_LOAD_TUTORIAL_SIMPLE_TIP (0x2872) -&gt; 0x2873, rebuilt from the stored tips.</summary>
    private bool OnLoadTutorialTips(WorldLink link, byte[] payload)
    {
        uint reqId = payload.Length >= 4 ? BitConverter.ToUInt32(payload, 0) : 0;
        int playerId = payload.Length >= 8 ? (int)BitConverter.ToUInt32(payload, 4) : 0;
        var tips = _store?.GetTutorialTipCounts(playerId);
        if (tips is null || (tips.Count == 0 && ServesCapturedStatics(playerId) && _store?.TutorialWasCleared(playerId) != true))
        {
            link.SendFrame(DBS_TUTORIAL_SIMPLE_TIP, BuildFromStaticData(
                DbProxyStaticData.Tutorial, DbProxyStaticData.TutorialReqIdOffset, payload));
            return true;
        }
        _log.LogInformation("SDB_LOAD_TUTORIAL_SIMPLE_TIP: player {Pid} -> {N} tip(s)", playerId, tips.Count);
        link.SendFrame(DBS_TUTORIAL_SIMPLE_TIP, BuildDbs2873(tips, reqId));
        return true;
    }

    /// <summary>
    /// DBS_LOAD_TUTORIAL_SIMPLE_TIP (0x2873). Byte-exact to cap_newchar.log seq 176 with no tips
    /// and to arb_world_2026-09-13 seq 859 with the four tips cap_newchar's 0x286E writes add.
    /// </summary>
    public static byte[] BuildDbs2873(IReadOnlyList<int> tipIds, uint reqId)
    {
        ArgumentNullException.ThrowIfNull(tipIds);
        return BuildDbs2873(tipIds.Select(id => (TipId: id, PopupCount: 1)).ToArray(), reqId);
    }

    public static byte[] BuildDbs2873(IReadOnlyList<(int TipId, int PopupCount)> tips, uint reqId)
    {
        ArgumentNullException.ThrowIfNull(tips);
        int len = tips.Count * TutorialTipRecordSize;
        var r = new byte[TutorialTipReplyHeader + len];
        BitConverter.GetBytes(6u + TutorialTipReplyHeader).CopyTo(r, 0);   // 19, frame-relative
        BitConverter.GetBytes((uint)len).CopyTo(r, 4);
        BitConverter.GetBytes(reqId).CopyTo(r, 8);
        r[12] = 1;                                                          // ok
        int p = TutorialTipReplyHeader;
        foreach (var tip in tips.OrderBy(t => t.TipId))
        {
            BitConverter.GetBytes(tip.TipId).CopyTo(r, p);
            BitConverter.GetBytes(tip.PopupCount).CopyTo(r, p + 4);
            p += TutorialTipRecordSize;
        }
        return r;
    }

    /// <summary>SDB_UPDATE_SEREN_GUIDE_INFO (0x2944): store the slot, then ack.</summary>
    /// <summary>DBS_GIVE_GUILD_MONEY_INCENTIVE (0x27A1): <c>[u32 reqId][u8 ok]</c>, 5 bytes.
    /// Tap 2504 is <c>BB 00 00 00 01</c>.</summary>
    public static byte[] BuildDbsGiveGuildMoneyIncentive(uint reqId, bool ok)
    {
        var r = new byte[5];
        BitConverter.GetBytes(reqId).CopyTo(r, 0);
        r[4] = (byte)(ok ? 1 : 0);
        return r;
    }

    /// <summary>
    /// SA_UPDATE_RANK_USERNAME (0x161C). One-way. Re-applies the name to the row, which is a
    /// no-op after a successful SDB_DO_CHANGE_CHAR_NAME and a repair after a missed one.
    /// </summary>
    private bool OnUpdateRankUsername(byte[] payload)
    {
        if (payload.Length < 8) return true;
        int nameOff = (int)BitConverter.ToUInt32(payload, 0) - 6;
        int charId  = (int)BitConverter.ToUInt32(payload, 4);
        string name = ReadName(payload, nameOff);
        if (_store is null || charId <= 0 || name.Length == 0) return true;
        if (!string.Equals(_store.GetCharacterName(charId), name, StringComparison.Ordinal)
            && _store.RenameCharacter(charId, name))
            _log.LogInformation("SA_UPDATE_RANK_USERNAME: character {Cid} renamed to {Name}", charId, name);
        return true;
    }

    /// <summary>
    /// SA_WATCH_MOVIE (0x155C). One-way. The cinematic that just finished is filed on the
    /// ACCOUNT, which is the half C_WATCHED_MOVIES was missing: T62 could answer the question
    /// but nothing ever wrote an answer, so the list came back empty on every relog and the
    /// client replayed the intro.
    ///
    /// <para><b>No capture exercises this.</b> Neither SA_WATCH_MOVIE nor C_WATCHED_MOVIES,
    /// S_WATCHED_MOVIES, S_PLAY_MOVIE or C_END_MOVIE appears anywhere in the fourteen captures -
    /// the accounts in them had all watched the intro long before. The layout is the decompile's:
    /// the guard fixes the length and <c>Handler_SA_WATCH_MOVIE</c> fixes the one field's offset.</para>
    /// </summary>
    private bool OnWatchMovie(byte[] payload)
    {
        if (payload.Length < WatchMovieMinPayload) return true;
        ulong gameId = BitConverter.ToUInt64(payload, 0);
        int movieId = (int)BitConverter.ToUInt32(payload, WatchMovieIdOffset);
        if (_store is null || movieId <= 0) return true;

        int playerId = PlayerIdForGameId?.Invoke(gameId) ?? 0;
        var chr = playerId > 0 ? _store.GetCharacter(playerId) : null;
        if (chr is null)
        {
            _log.LogWarning("SA_WATCH_MOVIE: movie {Movie} for gameId {Game} - no character, not filed",
                movieId, gameId);
            return true;
        }
        if (_store.AddWatchedMovieForAccount(chr.AccountId, movieId))
            _log.LogInformation("SA_WATCH_MOVIE: account {Acct} has now watched movie {Movie}",
                chr.AccountId, movieId);
        return true;
    }

    /// <summary>
    /// SA_START_CHANGE_APPEARANCE (0x1498). One-way, and nothing in it needs storing - the new
    /// customize blob arrives with the ordinary user save at the next hand-off.
    /// </summary>
    private bool OnStartChangeAppearance(byte[] payload)
    {
        _log.LogDebug("SA_START_CHANGE_APPEARANCE ({Len} B) - appearance is saved with the user, not here",
            payload.Length);
        return true;
    }

    /// <summary>
    /// SDB_GIVE_GUILD_MONEY_INCENTIVE (0x27A0) -&gt; DBS (0x27A1). Gates on guild membership and
    /// the cooldown, stamps the cooldown, and answers the captured five-byte form.
    ///
    /// <para><b>The payout is deliberately not modelled.</b> The capture shows one grant and
    /// only the guild money AFTER it, so the amount cannot be derived from these bytes - see
    /// status/CLIENT-REJECTS.md section 13.2. Answering ok keeps World unblocked; inventing an
    /// amount would silently drain guild funds.</para>
    /// </summary>
    private bool OnGiveGuildMoneyIncentive(WorldLink link, byte[] payload)
    {
        uint reqId  = payload.Length >= 4  ? BitConverter.ToUInt32(payload, 0) : 0;
        int playerId = payload.Length >= 8  ? (int)BitConverter.ToUInt32(payload, 4) : 0;
        int guildId  = payload.Length >= 12 ? (int)BitConverter.ToUInt32(payload, 8) : 0;
        float rate   = payload.Length >= 16 ? BitConverter.ToSingle(payload, 12) : 0f;

        bool ok = false;
        if (_store is not null && playerId > 0 && guildId > 0)
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            long last = _store.GetGuildIncentiveTime(guildId);
            if (_store.GetGuildIdOf(playerId) != guildId)
                _log.LogInformation("SDB_GIVE_GUILD_MONEY_INCENTIVE: player {Pid} is not in guild {Gid}",
                    playerId, guildId);
            else if (now - last < GuildIncentiveCooldownSeconds)
                _log.LogInformation("SDB_GIVE_GUILD_MONEY_INCENTIVE: guild {Gid} still on cooldown", guildId);
            else
            {
                _store.SetGuildIncentiveTime(guildId, now);
                ok = true;
                // cap_final tap 2501: the whole guild is re-pushed BEFORE the 0x27A1 answer,
                // which is how World learns the new funds and tells the client.
                var push = GuildWiring.BuildGuildDataPush(_store, guildId);
                if (push != null) link.SendFrame(GuildPackets.AS_UPDATE_GUILD_DATA, push);
                _log.LogInformation("SDB_GIVE_GUILD_MONEY_INCENTIVE: guild {Gid} rate {Rate} granted by {Pid}",
                    guildId, rate, playerId);
            }
        }
        link.SendFrame(DBS_GIVE_GUILD_MONEY_INCENTIVE, BuildDbsGiveGuildMoneyIncentive(reqId, ok));
        return true;
    }

    /// <summary>DBS_ASK_CHANGE_CHAR_NAME (0x2855): <c>[u32 reqId][u8 ok][u32 code]</c>, 9 bytes.
    /// Tap 6094 is <c>99 00 00 00 00 01 00 00 00</c> and 6119 <c>9A 00 00 00 01 00 00 00 00</c>.</summary>
    public static byte[] BuildDbsAskChangeCharName(uint reqId, int code)
    {
        var r = new byte[9];
        BitConverter.GetBytes(reqId).CopyTo(r, 0);
        r[4] = (byte)(code == 0 ? 1 : 0);
        BitConverter.GetBytes(code).CopyTo(r, 5);
        return r;
    }

    /// <summary>DBS_DO_CHANGE_CHAR_NAME (0x2857):
    /// <c>[u32 blobOff=23][u32 blobLen][u32 reqId][u32 charId][u8 code][blob]</c>. The blob is
    /// the request s own, echoed unchanged - tap 6127 returns all 1712 bytes of tap 6126.</summary>
    public static byte[] BuildDbsDoChangeCharName(uint reqId, int charId, int code, ReadOnlySpan<byte> blob)
    {
        var r = new byte[17 + blob.Length];
        BitConverter.GetBytes(23).CopyTo(r, 0);
        BitConverter.GetBytes(blob.Length).CopyTo(r, 4);
        BitConverter.GetBytes(reqId).CopyTo(r, 8);
        BitConverter.GetBytes(charId).CopyTo(r, 12);
        r[16] = (byte)code;
        blob.CopyTo(r.AsSpan(17));
        return r;
    }

    /// <summary>Read a NUL-terminated UTF-16LE string at a PAYLOAD index.</summary>
    private static string ReadName(byte[] payload, int at)
    {
        if (at < 0 || at >= payload.Length) return string.Empty;
        var sb = new System.Text.StringBuilder();
        for (int i = at; i + 1 < payload.Length; i += 2)
        {
            ushort c = BitConverter.ToUInt16(payload, i);
            if (c == 0) break;
            sb.Append((char)c);
        }
        return sb.ToString();
    }

    /// <summary>
    /// The name rules the capture can actually pin: a length floor (three letters refused,
    /// four accepted) a ceiling, and the UNIQUE COLLATE NOCASE index on the row. Returns 0
    /// when the name is usable, <see cref="NameRefusedCode"/> otherwise.
    /// </summary>
    private int CheckCharacterName(string name, int charId)
    {
        if (name.Length < MinCharacterNameLength || name.Length > MaxCharacterNameLength)
            return NameRefusedCode;
        // Renaming a character to the name it already has is not a collision.
        if (_store is not null && _store.NameExists(name)
            && !string.Equals(_store.GetCharacterName(charId), name, StringComparison.OrdinalIgnoreCase))
            return NameRefusedCode;
        return 0;
    }

    /// <summary>
    /// SDB_ASK_CHANGE_CHAR_NAME (0x2854) -&gt; DBS (0x2855). The "is this name free" step behind
    /// the rename popup. Tap 6093/6094 and 6118/6119.
    /// </summary>
    private bool OnAskChangeCharName(WorldLink link, byte[] payload)
    {
        uint reqId = payload.Length >= 8 ? BitConverter.ToUInt32(payload, 4) : 0;
        int charId = payload.Length >= 12 ? (int)BitConverter.ToUInt32(payload, 8) : 0;
        int nameOff = payload.Length >= 4 ? (int)BitConverter.ToUInt32(payload, 0) - 6 : -1;
        string name = ReadName(payload, nameOff);

        int code = CheckCharacterName(name, charId);
        _log.LogInformation("SDB_ASK_CHANGE_CHAR_NAME: character {Cid} -> {Name} = {Code}",
            charId, name, code);

        link.SendFrame(DBS_ASK_CHANGE_CHAR_NAME, BuildDbsAskChangeCharName(reqId, code));
        return true;
    }

    /// <summary>
    /// SDB_DO_CHANGE_CHAR_NAME (0x2856) -&gt; DBS (0x2857). Tap 6126/6127: the request carries
    /// the new name plus a 1712-byte character blob, and the reply echoes that blob UNCHANGED
    /// behind its own header. So the only thing we own here is the row.
    /// </summary>
    private bool OnDoChangeCharName(WorldLink link, byte[] payload)
    {
        int nameOff  = payload.Length >= 4  ? (int)BitConverter.ToUInt32(payload, 0) - 6 : -1;
        int blobOff  = payload.Length >= 8  ? (int)BitConverter.ToUInt32(payload, 4) - 6 : -1;
        int blobLen  = payload.Length >= 12 ? (int)BitConverter.ToUInt32(payload, 8) : 0;
        uint reqId   = payload.Length >= 16 ? BitConverter.ToUInt32(payload, 12) : 0;
        int charId   = payload.Length >= 20 ? (int)BitConverter.ToUInt32(payload, 16) : 0;
        string name  = ReadName(payload, nameOff);

        if (blobOff < 0 || blobLen < 0 || blobOff > payload.Length || blobLen > payload.Length - blobOff) { blobOff = 0; blobLen = 0; }

        int code = CheckCharacterName(name, charId);
        if (code == 0 && _store is not null && !_store.RenameCharacter(charId, name))
            code = NameRefusedCode;
        _log.LogInformation("SDB_DO_CHANGE_CHAR_NAME: character {Cid} -> {Name} = {Code}",
            charId, name, code);

        link.SendFrame(DBS_DO_CHANGE_CHAR_NAME,
            BuildDbsDoChangeCharName(reqId, charId, code, payload.AsSpan(blobOff, blobLen)));
        return true;
    }

    private bool OnUpdateSerenGuide(WorldLink link, byte[] payload)
    {
        if (_store is not null && payload.Length >= SerenGuideIdOffset + 4)
        {
            int playerId = (int)BitConverter.ToUInt32(payload, 4);
            int type = (int)BitConverter.ToUInt32(payload, SerenGuideTypeOffset);
            int id = (int)BitConverter.ToUInt32(payload, SerenGuideIdOffset);
            if (playerId > 0)
            {
                _store.SetSerenGuide(playerId, type, id);
                _log.LogInformation("SDB_UPDATE_SEREN_GUIDE_INFO: player {Pid} seren type {T} = {I}",
                    playerId, type, id);
            }
        }
        link.SendFrame(DBS_UPDATE_SEREN_GUIDE_INFO, BuildDbs2945(payload));
        return true;
    }

    /// <summary>SDB_INIT_SEREN_GUIDE_INFO (0x2942) -&gt; 0x2943, rebuilt from the stored slots.</summary>
    private bool OnLoadSerenGuide(WorldLink link, byte[] payload)
    {
        uint reqId = payload.Length >= 4 ? BitConverter.ToUInt32(payload, 0) : 0;
        int playerId = payload.Length >= 8 ? (int)BitConverter.ToUInt32(payload, 4) : 0;
        if (_store is null || ServesCapturedStatics(playerId))
        {
            link.SendFrame(DBS_SEREN_GUIDE, BuildFromStaticData(
                DbProxyStaticData.SerenGuide, DbProxyStaticData.SerenGuideReqIdOffset, payload));
            return true;
        }
        var slots = _store.GetSerenGuide(playerId);
        _log.LogInformation("SDB_INIT_SEREN_GUIDE_INFO: player {Pid} -> {N} stored slot(s)", playerId, slots.Count);
        link.SendFrame(DBS_SEREN_GUIDE, BuildDbs2943(slots, reqId, (uint)playerId));
        return true;
    }

    /// <summary>
    /// DBS_INIT_SEREN_GUIDE_INFO (0x2943). A character with nothing stored gets an empty list -
    /// byte-exact to cap_newchar.log seq 380 - and any stored slot brings out the full
    /// <see cref="SerenGuideTypes"/> table, byte-exact to arb_world_2026-09-13 seq 920.
    /// </summary>
    public static byte[] BuildDbs2943(IReadOnlyDictionary<int, int> slots, uint reqId, uint playerId)
    {
        ArgumentNullException.ThrowIfNull(slots);
        int rows = slots.Count == 0 ? 0 : SerenGuideTypes.Length;
        int len = rows * SerenGuideRecordSize;
        var r = new byte[SerenGuideReplyHeader + len];
        BitConverter.GetBytes(6u + SerenGuideReplyHeader).CopyTo(r, 0);    // 23, frame-relative
        BitConverter.GetBytes((uint)len).CopyTo(r, 4);
        BitConverter.GetBytes(reqId).CopyTo(r, 8);
        BitConverter.GetBytes(playerId).CopyTo(r, 12);
        r[16] = 1;                                                          // ok
        int p = SerenGuideReplyHeader;
        for (int i = 0; i < rows; i++)
        {
            int type = SerenGuideTypes[i];
            BitConverter.GetBytes(type).CopyTo(r, p);
            BitConverter.GetBytes(slots.TryGetValue(type, out int id) ? id : 0).CopyTo(r, p + 4);
            p += SerenGuideRecordSize;
        }
        return r;
    }

    public static byte[] BuildDbs2945(byte[] request)
    {
        uint reqId = request.Length >= 4 ? BitConverter.ToUInt32(request, 0) : 0;
        uint playerId = request.Length >= 8 ? BitConverter.ToUInt32(request, 4) : 0;
        var r = new byte[9];
        BitConverter.GetBytes(reqId).CopyTo(r, 0);
        BitConverter.GetBytes(playerId).CopyTo(r, 4);
        r[8] = 1;
        return r;
    }

    /// <summary>DBS_SAVE_2937: [u32 reqId][u32 0x300][u32 0] (13 bytes), reqId at request offset 0.</summary>
    public static byte[] BuildDbs2937(byte[] request)
    {
        uint reqId = request.Length >= 4 ? BitConverter.ToUInt32(request, 0) : 0;
        var r = new byte[13];
        BitConverter.GetBytes(reqId).CopyTo(r, 0);
        BitConverter.GetBytes(0x300u).CopyTo(r, 4);
        return r;
    }

    // ---- Login-time empty-list builders (verified against decompile + captured bytes) ----

    /// <summary>
    /// Empty-list Type 1: [u32 listOff=19][u32 listCount=0][u32 reqId][u8 ok] — 13 bytes.
    /// Used by: 0x27A2/A3, 0x2760/61, 0x2764/65, 0x27A7/A8, 0x28BB/BC, 0x28CF/D0,
    /// 0x28FE/FF, 0x28C9/CA, 0x28C5/C6, 0x2922/23, 0x2967/68.
    /// The listOff (19 = 6-byte frame header + 13-byte payload) points past the end,
    /// meaning "no list data follows".
    /// </summary>
    public static byte[] BuildEmptyListType1(byte[] request, int reqIdPayloadOffset, byte ok = 1)
    {
        uint reqId = reqIdPayloadOffset + 4 <= request.Length
            ? BitConverter.ToUInt32(request, reqIdPayloadOffset) : 0;
        var r = new byte[13];
        BitConverter.GetBytes(19u).CopyTo(r, 0); // listOff = frame len
        // r[4..7] = 0 (listCount)
        BitConverter.GetBytes(reqId).CopyTo(r, 8);
        r[12] = ok;
        return r;
    }

    /// <summary>
    /// Empty-list Type 2: [u32 listOff=19][u32 listCount=0][u8 ok][u32 reqId] — 13 bytes.
    /// Used by: 0x2912/13, 0x2916/17, 0x2895/96, 0x2833/34, 0x1521/22.
    /// Same as Type 1 but ok and reqId are swapped.
    /// </summary>
    public static byte[] BuildEmptyListType2(byte[] request, int reqIdPayloadOffset, byte ok = 1)
    {
        uint reqId = reqIdPayloadOffset + 4 <= request.Length
            ? BitConverter.ToUInt32(request, reqIdPayloadOffset) : 0;
        var r = new byte[13];
        BitConverter.GetBytes(19u).CopyTo(r, 0); // listOff = frame len
        // r[4..7] = 0 (listCount)
        r[8] = ok;
        BitConverter.GetBytes(reqId).CopyTo(r, 9);
        return r;
    }

    /// <summary>[u8 ok=1][u32 reqId] — 5 bytes. Used by 0x2910→0x2911 (SDB_UPDATE_FATIGABILITY_POINT).</summary>
    public static byte[] BuildOkReqId(byte[] request, int reqIdPayloadOffset)
    {
        uint reqId = reqIdPayloadOffset + 4 <= request.Length
            ? BitConverter.ToUInt32(request, reqIdPayloadOffset) : 0;
        var r = new byte[5];
        r[0] = 1;
        BitConverter.GetBytes(reqId).CopyTo(r, 1);
        return r;
    }

    /// <summary>
    /// DBS_LOAD_QUEST_PROGRESS (0x2903): [u32 reqId][u32 playerId][u32 0][u32 0][u8 0] — 17 bytes.
    /// From capture frame 307: reqId=0x20, playerId=1, then 5 zero bytes (no quest progress data).
    /// Actually 13 bytes payload in the capture (the two trailing u32s are 0 + u8 0).
    /// </summary>
    public static byte[] BuildQuestProgress(byte[] request)
    {
        uint reqId = request.Length >= 4 ? BitConverter.ToUInt32(request, 0) : 0;
        uint playerId = request.Length >= 8 ? BitConverter.ToUInt32(request, 4) : 0;
        var r = new byte[13];
        BitConverter.GetBytes(reqId).CopyTo(r, 0);
        BitConverter.GetBytes(playerId).CopyTo(r, 4);
        // r[8..12] = 0
        return r;
    }

    /// <summary>
    /// DBS_LOAD_ACHIEVE_LIST (0x2982): [u64 0][u32 reqId][u8 ok=1] — 13 bytes.
    /// From capture frame 382: 8 zero bytes, then reqId=0x30, ok=1.
    /// </summary>
    public static byte[] BuildAchieveList(byte[] request)
    {
        uint reqId = request.Length >= 4 ? BitConverter.ToUInt32(request, 0) : 0;
        var r = new byte[13];
        // r[0..7] = 0 (u64)
        BitConverter.GetBytes(reqId).CopyTo(r, 8);
        r[12] = 1;
        return r;
    }

    /// <summary>
    /// AS_LOAD_BATTLE_FIELD_ENTER_COUNT (0x155E): [u32 listOff=27][u32 0][u8 ok=1][u32 reqId][u64 timestamp].
    /// SA_ shape: reqId at payload[8]. From capture frame 289: 21 bytes.
    /// </summary>
    public static byte[] BuildBattleFieldEnterCount(byte[] request)
    {
        uint reqId = request.Length >= 12 ? BitConverter.ToUInt32(request, 8) : 0;
        var r = new byte[21];
        BitConverter.GetBytes(27u).CopyTo(r, 0); // listOff = 6 + 21
        // r[4..7] = 0 (listCount)
        r[8] = 1; // ok
        BitConverter.GetBytes(reqId).CopyTo(r, 9);
        // r[13..20]: timestamp — we use 0 for now (no battle field enter count data)
        return r;
    }

    // ---- SA_ handler builders (request: [u64 gameId][u32 reqId][u32 playerId]) ----

    /// <summary>
    /// AS_LOAD_SERVANT_DATA (0x153A): [u32 count=0][u32 first=0][u32 reqId][u8 ok=1][u32 UserDbId].
    /// cap_final2b 133 -> 134: user 1003, not the replay fixture's user 1. Empty list: 17 bytes.
    /// </summary>
    public static byte[] BuildServantData(byte[] request)
    {
        uint reqId = request.Length >= 12 ? BitConverter.ToUInt32(request, 8) : 0;
        uint playerId = request.Length >= 16 ? BitConverter.ToUInt32(request, 12) : 0;
        var r = new byte[17];
        // r[0..7] = 0 (empty list count and first offset)
        BitConverter.GetBytes(reqId).CopyTo(r, 8);
        r[12] = 1;
        BitConverter.GetBytes(playerId).CopyTo(r, 13);
        return r;
    }

    /// <summary>
    /// AS_LOAD_SERVANT_ADVENTURE_DATA (0x153C): [u64 0][u32 reqId][u8 ok=1][u32 UserDbId][u32 0].
    /// cap_final2b 135 -> 136: the field at payload 13 is user 1003, not a constant 1.
    /// </summary>
    public static byte[] BuildServantAdventureData(byte[] request)
    {
        uint reqId = request.Length >= 12 ? BitConverter.ToUInt32(request, 8) : 0;
        uint playerId = request.Length >= 16 ? BitConverter.ToUInt32(request, 12) : 0;
        var r = new byte[21];
        BitConverter.GetBytes(reqId).CopyTo(r, 8);
        r[12] = 1; // ok
        BitConverter.GetBytes(playerId).CopyTo(r, 13);
        return r;
    }

    /// <summary>
    /// AS_LOAD_SERVANT_STORAGE_DATA (0x1538): [u32 reqId][u32 playerId][u8 0] — 9 bytes.
    /// Capture frame 265.
    /// </summary>
    public static byte[] BuildServantStorageData(byte[] request)
    {
        uint reqId = request.Length >= 12 ? BitConverter.ToUInt32(request, 8) : 0;
        uint playerId = request.Length >= 16 ? BitConverter.ToUInt32(request, 12) : 0;
        var r = new byte[9];
        BitConverter.GetBytes(reqId).CopyTo(r, 0);
        BitConverter.GetBytes(playerId).CopyTo(r, 4);
        r[8] = 0;
        return r;
    }

    /// <summary>
    /// AS_LOAD_SERVANT_AUTO_POTION_DATA (0x1530): [u32 reqId][u32 playerId][u8 0][u32 -1][u32 0][u32 -1] — 21 bytes.
    /// Capture frame 267. The -1 (0xFFFFFFFF) values represent "no item" in the auto-potion slots.
    /// </summary>
    public static byte[] BuildServantAutoPotionData(byte[] request)
    {
        uint reqId = request.Length >= 12 ? BitConverter.ToUInt32(request, 8) : 0;
        uint playerId = request.Length >= 16 ? BitConverter.ToUInt32(request, 12) : 0;
        var r = new byte[21];
        BitConverter.GetBytes(reqId).CopyTo(r, 0);
        BitConverter.GetBytes(playerId).CopyTo(r, 4);
        r[8] = 0;
        BitConverter.GetBytes(0xFFFFFFFFu).CopyTo(r, 9);
        // r[13..16] = 0
        BitConverter.GetBytes(0xFFFFFFFFu).CopyTo(r, 17);
        return r;
    }

    /// <summary>
    /// AS_LOAD_SERVANT_AUTO_FEED_DATA (0x1534): same shape as auto-potion. Capture frame 269.
    /// </summary>
    public static byte[] BuildServantAutoFeedData(byte[] request)
        => BuildServantAutoPotionData(request); // identical layout

    /// <summary>
    /// AS_PET_LOAD (0x1416): 175-byte reply with pet name "NONAME" and default empty state.
    /// Layout from the decompile writer + capture frame 271. Offsets are frame-relative:
    ///   [6] u32 listOff1=31  [10] u32 listOff2=45  [14] u32 nameOff=136
    ///   [18] u32 reqId  [22] u32 playerId  [26] u64 0  [34] u8 0
    ///   [35..41] wstr "NONAME\0" (at nameOff=136 frame = 130 payload)
    /// Trailing bytes at [163]: F7 7F 00 00 01 00 00 00 57 01 00 00 (static pet config).
    /// reqId at payload[8], playerId at payload[12] (SA_ shape request).
    /// </summary>
    public static byte[] BuildPetLoad(byte[] request)
    {
        uint reqId = request.Length >= 12 ? BitConverter.ToUInt32(request, 8) : 0;
        uint playerId = request.Length >= 16 ? BitConverter.ToUInt32(request, 12) : 0;
        var r = new byte[175];
        // Frame-relative offsets (subtract 6 for payload):
        BitConverter.GetBytes(31u).CopyTo(r, 0);  // listOff1 (frame 31 = payload 25)
        BitConverter.GetBytes(45u).CopyTo(r, 4);  // listOff2 (frame 45 = payload 39)
        BitConverter.GetBytes(136u).CopyTo(r, 8); // nameOff  (frame 136 = payload 130)
        BitConverter.GetBytes(reqId).CopyTo(r, 12);
        BitConverter.GetBytes(playerId).CopyTo(r, 16);
        // [20..24] = 0 (u32 unk + u8 hasPet=0)
        // Name at payload offset 130: "NONAME\0" as UTF-16LE
        var name = System.Text.Encoding.Unicode.GetBytes("NONAME\0");
        name.CopyTo(r, 25); // payload 25 = the actual name data location
        // Trailing static bytes at payload offset 163
        BitConverter.GetBytes((uint)0x7FF7).CopyTo(r, 163); // max food?
        BitConverter.GetBytes(1u).CopyTo(r, 167);
        BitConverter.GetBytes((uint)0x157).CopyTo(r, 171); // 343 = static config
        return r;
    }

    /// <summary>
    /// SA_LOAD_EXTRAPOINT_DATA (0x1554) -&gt; AS_LOAD_EXTRAPOINT_DATA (0x1555). The EP (elite
    /// point) panel's load. The request carries a DlmId, so an unanswered one head-blocks that
    /// character.
    ///
    /// <para><b>T70 fixed the field placement.</b> The old builder wrote
    /// <c>[u32 reqId][u8 playerId_low][u8 ok]</c>, which put the low byte of the player id where
    /// <c>Result</c> belongs and a 1 where the four-byte <c>UserDbId</c> starts - World read back
    /// user 1 for every character. The length (53 payload bytes, frame 0x3B) happened to be
    /// right. Dumpers: request guard 0x0d (<c>DlmId@06, UserDbId@0A</c>), reply guard 0x3a.
    /// cap_social3.log seq 111 -&gt; 112 and 314 -&gt; 315 are the two real pairs; every field past
    /// UserDbId is zero in both, which is what a character with no EP looks like.</para>
    /// <code>
    ///   [00] i32 DlmId   [04] u8 Result   [05] i32 UserDbId   [09] i32 EpLevel
    ///   [13] i64 EpExp   [21] i32 DailyEpExp   [25] i32 ReserveBonus
    ///   [29] i32 DailyLimitEpExp   [33] i64 DailyEpExpResetTime
    ///   [41] i64 GoldConsumption   [49] i32 TotalEp
    /// </code>
    /// </summary>
    public const int ExtrapointReplySize = 53;      // frame 0x3B

    public static byte[] BuildExtrapointData(byte[] request) => BuildExtrapointData(request, null);

    /// <summary>
    /// T77: the same reply, filled in from what SDB_UPDATE_EXTRA_POINT last stored. Before T77
    /// the Arbiter kept no EP at all, so every login answered zeros and the panel reset itself
    /// each time; the six numbers now round-trip through <c>account_ep</c>.
    ///
    /// <para>T193: TotalEp is the stored NewEpPoint from 0x27B1, not zero. Native account
    /// field +0x30C0 is copied by Arb_part_061:16560 and emitted last by Arb_part_062:9506;
    /// cap_final2b 519 contains 300. GoldConsumption remains the captured zero.</para>
    /// </summary>
    public static byte[] BuildExtrapointData(byte[] request, CharacterStore.EpRow? ep)
    {
        uint dlmId = request.Length >= 4 ? BitConverter.ToUInt32(request, 0) : 0;
        int userDbId = request.Length >= 8 ? BitConverter.ToInt32(request, 4) : 0;
        var r = new byte[ExtrapointReplySize];
        BitConverter.GetBytes(dlmId).CopyTo(r, 0);
        r[4] = 1;                                   // Result
        BitConverter.GetBytes(userDbId).CopyTo(r, 5);
        if (ep is null) return r;                   // no character, or no EP yet: all zeros
        BitConverter.GetBytes(ep.EpLevel).CopyTo(r, 9);
        BitConverter.GetBytes(ep.EpExp).CopyTo(r, 13);
        BitConverter.GetBytes(ep.DailyEpExp).CopyTo(r, 21);
        BitConverter.GetBytes(ep.ReserveBonus).CopyTo(r, 25);
        BitConverter.GetBytes(ep.DailyLimit).CopyTo(r, 29);
        BitConverter.GetBytes(ep.ResetTime).CopyTo(r, 33);
        BitConverter.GetBytes(ep.EpPoint).CopyTo(r, 49);
        return r;
    }

    // ---- Remaining login-time programmatic builders ----

    /// <summary>
    /// 0x2868: three empty lists + [u8 ok=1][u32 reqId] — 29 bytes.
    /// Three [u32 off=35][u32 count=0] headers (35 = 6+29), then ok + reqId.
    /// </summary>
    // ---- Dungeon cool times (T25) ----

    /// <summary>
    /// The character behind an <c>ArbiterUser</c> handle (our gameId), or 0. The dungeon writes
    /// carry no playerId at all, so without <see cref="PlayerIdForGameId"/> wired there is
    /// nothing to key a row on and the write is dropped with a warning.
    /// </summary>
    private int PlayerForDungeonWrite(byte[] payload, string what)
    {
        if (_store is null || payload.Length < 8) return 0;
        ulong gameId = BitConverter.ToUInt64(payload, 0);
        int playerId = PlayerIdForGameId?.Invoke(gameId) ?? 0;
        if (playerId <= 0)
            _log.LogWarning("{What}: no live session owns gameId 0x{G:X} - not stored", what, gameId);
        return playerId;
    }

    /// <summary>
    /// T115. Store decoded log lines and answer nothing, which is what the real Arbiter does:
    /// every <c>Handler_SDB_*LOG*</c> ends at <c>return 1</c> with no packet writer in it, and
    /// cap_social2/3/4 show four 0x288C frames with no A-&gt;W frame after any of them. Returning
    /// true only keeps the replay table out of it.
    ///
    /// <para>A frame shorter than the handler's own guard decodes to nothing and is dropped
    /// with a warning - the same thing the Arbiter does, minus the PDL-mismatch shutdown.</para>
    /// </summary>
    private bool FileGameLog(IReadOnlyList<GameLogPackets.GameLogEntry> rows, string what)
    {
        if (rows.Count == 0)
        {
            _log.LogWarning("{What}: frame too short for the handler guard - dropped", what);
            return true;
        }
        if (_store is null) return true;
        foreach (var e in rows)
            _store.AddGameLog(e.Category, e.Action, e.AccountId, e.CharacterId, e.TargetId,
                e.ItemDbId, e.TemplateId, e.Amount, e.Money, e.Extra);
        _log.LogInformation("{What}: {N} game_log row(s) filed", what, rows.Count);
        return true;
    }

    /// <summary>
    /// SA_ADD_DUNGEON_CHANNEL (0x13C5). One-way - the capture has no reply, and World sends it
    /// right after the 0x13C0 that finished an entry. T108 records it in the instance registry
    /// so the next entry into that continent can be routed to whichever World is hosting it.
    /// </summary>
    private bool OnAddDungeonChannel(WorldLink link, byte[] payload)
    {
        int worldId = WorldRouting.WorldIdOf(link);
        var ch = DungeonRouting.Channels.Add(worldId, payload);
        if (ch == null)
        {
            _log.LogWarning("SA_ADD_DUNGEON_CHANNEL: {Len} B payload, need {Need}",
                payload.Length, DungeonChannels.AddMinPayload);
            return true;
        }
        _log.LogInformation("SA_ADD_DUNGEON_CHANNEL: continent {C} channel {Ch} is on world {W} ({N} known)",
            ch.Value.ContinentId, ch.Value.ChannelId, worldId, DungeonRouting.Channels.Count);
        return true;
    }

    /// <summary>SA_REMOVE_DUNGEON_CHANNEL (0x13C6). One-way; the instance is gone.</summary>
    private bool OnRemoveDungeonChannel(WorldLink link, byte[] payload)
    {
        int worldId = WorldRouting.WorldIdOf(link);
        if (DungeonRouting.Channels.Remove(worldId, payload))
            _log.LogInformation("SA_REMOVE_DUNGEON_CHANNEL: continent {C} channel {Ch} gone from world {W}",
                BitConverter.ToInt32(payload, DungeonChannels.ContinentIdOffset),
                BitConverter.ToInt32(payload, DungeonChannels.ChannelIdOffset), worldId);
        return true;
    }

    /// <summary>
    /// SA_UPDATE_DUNGEON_COOLTIME (0x13B6). One-way: World expects no reply, and returning true
    /// only keeps the replay table out of it. The ArbiterUser handle is at payload 8 here, not 0,
    /// because the two list backpatch slots come first.
    /// </summary>
    private bool OnUpdateDungeonCoolTime(byte[] payload)
    {
        if (payload.Length < DungeonCoolTimeUpdateHeader) return true;
        int off = (int)BitConverter.ToUInt32(payload, 0) - 6;
        int len = (int)BitConverter.ToUInt32(payload, 4);
        if (len != DungeonCoolTimeRecordSize || off < DungeonCoolTimeUpdateHeader
            || off > payload.Length || len > payload.Length - off)
        {
            _log.LogWarning("SA_UPDATE_DUNGEON_COOLTIME: element at {Off} len {Len} does not fit a {N} B payload",
                off, len, payload.Length);
            return true;
        }

        ulong gameId = BitConverter.ToUInt64(payload, 8);
        int playerId = _store is null ? 0 : PlayerIdForGameId?.Invoke(gameId) ?? 0;
        if (playerId <= 0)
        {
            if (_store is not null)
                _log.LogWarning("SA_UPDATE_DUNGEON_COOLTIME: no live session owns gameId 0x{G:X} - not stored", gameId);
            return true;
        }

        var record = payload[off..(off + len)];
        int dungeonId = (int)BitConverter.ToUInt32(record, DungeonCoolTimeDungeonIdOffset);
        _store!.UpsertDungeonCoolTime(playerId, dungeonId, record);
        _log.LogInformation("SA_UPDATE_DUNGEON_COOLTIME: player {Pid} dungeon {Dg} cool time stored",
            playerId, dungeonId);
        return true;
    }

    /// <summary>SA_UPDATE_DUNGEON_CLEAR_COUNT (0x13B7). One-way.</summary>
    private bool OnUpdateDungeonClearCount(byte[] payload)
    {
        if (payload.Length < DungeonClearCountOffset + 4) return true;
        int playerId = PlayerForDungeonWrite(payload, "SA_UPDATE_DUNGEON_CLEAR_COUNT");
        if (playerId <= 0) return true;
        int dungeonId = (int)BitConverter.ToUInt32(payload, DungeonContinentIdOffset);
        int count = (int)BitConverter.ToUInt32(payload, DungeonClearCountOffset);
        _store!.SetDungeonClearCount(playerId, dungeonId, count);
        _log.LogInformation("SA_UPDATE_DUNGEON_CLEAR_COUNT: player {Pid} dungeon {Dg} cleared {N} time(s)",
            playerId, dungeonId, count);
        return true;
    }

    /// <summary>SA_DELETE_DUNGEON_COOLTIME (0x13BD). One-way.</summary>
    private bool OnDeleteDungeonCoolTime(byte[] payload)
    {
        if (payload.Length < DungeonContinentIdOffset + 4) return true;
        int playerId = PlayerForDungeonWrite(payload, "SA_DELETE_DUNGEON_COOLTIME");
        if (playerId <= 0) return true;
        int dungeonId = (int)BitConverter.ToUInt32(payload, DungeonContinentIdOffset);
        _store!.ClearDungeonCoolTime(playerId, dungeonId);
        _log.LogInformation("SA_DELETE_DUNGEON_COOLTIME: player {Pid} dungeon {Dg} cool time cleared",
            playerId, dungeonId);
        return true;
    }

    /// <summary>SDB_LOAD_DUNGEON_COOL_TIME (0x2867) -&gt; 0x2868, rebuilt from the stored rows.</summary>
    private bool OnLoadDungeonCoolTime(WorldLink link, byte[] payload)
    {
        uint reqId = payload.Length >= 4 ? BitConverter.ToUInt32(payload, 0) : 0;
        int playerId = payload.Length >= 8 ? (int)BitConverter.ToUInt32(payload, 4) : 0;
        // No dob exemption here, unlike the other login loads: his captured 0x2868 IS the empty
        // form, so the rebuild and the capture agree byte for byte with no rows, and once he has
        // rows serving them is strictly better.
        var rows = _store is null || playerId <= 0
            ? new List<byte[]>() : _store.GetDungeonCoolTimes(playerId);
        if (rows.Count > 0)
            _log.LogInformation("SDB_LOAD_DUNGEON_COOL_TIME: player {Pid} -> {N} cool time(s)", playerId, rows.Count);
        link.SendFrame(0x2868, BuildDbs2868(rows, reqId));
        return true;
    }

    /// <summary>
    /// DBS_LOAD_DUNGEON_COOL_TIME (0x2868): three [offset][length] pairs, [24] u8 ok,
    /// [25] u32 DlmId, then the bodies. List 0 is <c>CoolTimeList</c> (52-byte elements),
    /// list 1 <c>ClearCountList</c> and list 2 <c>UiHistoryList</c>.
    ///
    /// <para>Lists 1 and 2 are empty in every captured reply, so their element layouts have
    /// never been observed and nothing is served in them - the clear counts SA_UPDATE_DUNGEON_
    /// CLEAR_COUNT gives us are stored but not sent back. status/DUNGEON-COOLTIME.md.</para>
    ///
    /// <para>Byte-exact with no rows against cap_newchar.log seq 346 and
    /// arb_world_2026-09-13 seq 399, and with one row against seq 885.</para>
    /// </summary>
    public static byte[] BuildDbs2868(IReadOnlyList<byte[]> coolTimes, uint reqId)
    {
        ArgumentNullException.ThrowIfNull(coolTimes);
        int len = 0;
        foreach (var rec in coolTimes)
        {
            if (rec.Length != DungeonCoolTimeRecordSize)
                throw new ArgumentException(
                    $"cool-time element must be {DungeonCoolTimeRecordSize} bytes, got {rec.Length}", nameof(coolTimes));
            len += rec.Length;
        }

        var r = new byte[DungeonCoolTimeReplyHeader + len];
        uint start = 6 + (uint)DungeonCoolTimeReplyHeader;      // 35, frame-relative
        uint after = start + (uint)len;
        BitConverter.GetBytes(start).CopyTo(r, 0);
        BitConverter.GetBytes((uint)len).CopyTo(r, 4);
        BitConverter.GetBytes(after).CopyTo(r, 8);              // ClearCountList: empty
        BitConverter.GetBytes(after).CopyTo(r, 16);             // UiHistoryList: empty
        r[24] = 1;                                              // ok
        BitConverter.GetBytes(reqId).CopyTo(r, 25);
        int p = DungeonCoolTimeReplyHeader;
        foreach (var rec in coolTimes) { rec.CopyTo(r, p); p += rec.Length; }
        return r;
    }

    public static byte[] Build2868_ThreeEmptyLists(byte[] request)
        => BuildDbs2868(Array.Empty<byte[]>(), request.Length >= 4 ? BitConverter.ToUInt32(request, 0) : 0);

    /// <summary>
    /// 0x286A: [u32 off=27][u32 0][u8 ok=1][u32 reqId][u64 timestamp=0] — 21 bytes.
    /// Same shape as BuildBattleFieldEnterCount but SDB-shape request (reqId at payload[0]).
    /// Capture has a real timestamp; we use 0 (no data).
    /// </summary>
    /// <summary>
    /// DBS_LOAD_DUNGEON_PHASE_LEVEL (0x286A): [u32 listOff=27][u32 listByteLen=0][u8 ok=1]
    /// [u32 reqId][u64 resetTime] - 21 bytes. Field order and types are the writer in
    /// Handler_SDB_LOAD_DUNGEON_PHASE_LEVEL (FUN_14074df40): FUN_140350eb0(0x286a), two
    /// back-patched u32 slots, FUN_1403513d0 (u8 ok = user found), FUN_14013d0b0 (u32 reqId
    /// read from frame+6), FUN_140351270 (u64 reset time). Ground truth: lobby_tap.log pkt 174.
    /// </summary>
    public static byte[] Build286A_EmptyListTimestamp(byte[] request, ulong resetTimeUnixSeconds = 0)
    {
        uint reqId = request.Length >= 4 ? BitConverter.ToUInt32(request, 0) : 0;
        var r = new byte[21];
        BitConverter.GetBytes(27u).CopyTo(r, 0); // listOff = 6 + 21
        r[8] = 1; // ok
        BitConverter.GetBytes(reqId).CopyTo(r, 9);
        BitConverter.GetBytes(resetTimeUnixSeconds).CopyTo(r, 13);
        return r;
    }

    /// <summary>
    /// AS_REQUEST_DUNGEON_PHASE_USER_RESET (0x15E0): [u32 playerId][u32 continentId=0]
    /// [u64 resetTime] - 16 bytes, matching writer FUN_1407acf70 (u32, u32, u64) and the
    /// offsets World's handler reads (frame+6, frame+10, frame+14; min frame length 0x16).
    /// continentId 0 is what CheckAndResetDungeonPhaseUser(DateTime) passes, and tells World
    /// to reset every continent. One-way: World sends no reply. Ground truth: lobby_tap.log pkt 173.
    /// </summary>
    public static byte[] Build15E0(uint playerId, ulong resetTimeUnixSeconds)
    {
        var r = new byte[16];
        BitConverter.GetBytes(playerId).CopyTo(r, 0);
        // r[4..7] = continentId 0
        BitConverter.GetBytes(resetTimeUnixSeconds).CopyTo(r, 8);
        return r;
    }

    /// <summary>
    /// 0x2901: two empty lists + [u32 reqId][u8 ok=1] — 21 bytes.
    /// Two [u32 off=27][u32 count=0] headers (27 = 6+21), then reqId + ok.
    /// </summary>
    public static byte[] Build2901_TwoEmptyLists(byte[] request)
    {
        uint reqId = request.Length >= 4 ? BitConverter.ToUInt32(request, 0) : 0;
        var r = new byte[21];
        const uint off = 27; // 6 + 21
        BitConverter.GetBytes(off).CopyTo(r, 0);
        BitConverter.GetBytes(off).CopyTo(r, 8);
        BitConverter.GetBytes(reqId).CopyTo(r, 16);
        r[20] = 1; // ok
        return r;
    }

    /// <summary>
    /// 0x28B6 (reply to 0x28B7; opcode is op-1, anomaly):
    /// [u8 ok=1][u32 reqId][u32 0][u64 -1] — 17 bytes.
    /// </summary>
    public static byte[] Build28B6(byte[] request)
    {
        uint reqId = request.Length >= 4 ? BitConverter.ToUInt32(request, 0) : 0;
        var r = new byte[17];
        r[0] = 1; // ok
        BitConverter.GetBytes(reqId).CopyTo(r, 1);
        // r[5..8] = 0
        BitConverter.GetBytes(0xFFFFFFFFFFFFFFFFUL).CopyTo(r, 9);
        return r;
    }

    /// <summary>
    /// 0x28B1 (REFER_A_FRIEND): two empty lists + [u8 ok=1][u32 reqId] + 20 zeros — 41 bytes.
    /// Two [u32 off=47][u32 count=0] headers (47 = 6+41), then ok + reqId + padding.
    /// </summary>
    public static byte[] Build28B1_ReferAFriend(byte[] request)
    {
        uint reqId = request.Length >= 4 ? BitConverter.ToUInt32(request, 0) : 0;
        var r = new byte[41];
        const uint off = 47; // 6 + 41
        BitConverter.GetBytes(off).CopyTo(r, 0);
        BitConverter.GetBytes(off).CopyTo(r, 8);
        r[16] = 1; // ok
        BitConverter.GetBytes(reqId).CopyTo(r, 17);
        // r[21..40] = 0
        return r;
    }

    /// <summary>0x2976: [16 zeros][u32 reqId][u8 ok=1][20 zeros] — 41 bytes.</summary>
    public static byte[] Build2976(byte[] request)
    {
        uint reqId = request.Length >= 4 ? BitConverter.ToUInt32(request, 0) : 0;
        var r = new byte[41];
        BitConverter.GetBytes(reqId).CopyTo(r, 16);
        r[20] = 1; // ok
        return r;
    }

    /// <summary>
    /// 0x2987: [32 zeros][u32 reqId][u8 ok=1][u32 1][u32 0][u32 1][u32 0] — 53 bytes.
    /// Request is 16 bytes (non-standard SDB shape), reqId at payload[0].
    /// </summary>
    public static byte[] Build2987(byte[] request)
    {
        uint reqId = request.Length >= 4 ? BitConverter.ToUInt32(request, 0) : 0;
        var r = new byte[53];
        BitConverter.GetBytes(reqId).CopyTo(r, 32);
        r[36] = 1; // ok
        BitConverter.GetBytes(1u).CopyTo(r, 37);
        // r[41..44] = 0
        BitConverter.GetBytes(1u).CopyTo(r, 45);
        // r[49..52] = 0
        return r;
    }

    // ---- 0x290C multi-reply sub-frame builders ----

    /// <summary>AS_ACQUIRE_FRIENDSHIP_GAGE (0x15B1): [u32 playerId][u32 0] — 8 bytes.</summary>
    public static byte[] Build15B1(uint playerId)
    {
        var r = new byte[8];
        BitConverter.GetBytes(playerId).CopyTo(r, 0);
        return r;
    }

    /// <summary>AS_RESET_FIELD_POINT_COMPLETE (0x1440): [u32 playerId] — 4 bytes (capture: 01 00 00 00).</summary>
    public static byte[] Build1440(uint playerId) => BitConverter.GetBytes(playerId);

    /// <summary>0x2847: [u64 0][u32 playerId] — 12 bytes.</summary>
    public static byte[] Build2847(uint playerId)
    {
        var r = new byte[12];
        BitConverter.GetBytes(playerId).CopyTo(r, 8);
        return r;
    }

    /// <summary>
    /// AS_USER_FIELD_POINT_INFO (0x143E): [u32 pid][u64 0][u32 0][u32 0][u32 -1] — 24 bytes.
    /// Capture has a real timestamp at [12..15]; we write 0 (empty state).
    /// </summary>
    public static byte[] Build143E(uint playerId)
    {
        var r = new byte[24];
        BitConverter.GetBytes(playerId).CopyTo(r, 0);
        // r[4..19] = 0
        BitConverter.GetBytes(0xFFFFFFFFu).CopyTo(r, 20);
        return r;
    }

    // ---- Static-data handler builder ----

    // =========================================================================================
    // T113: play time. Keyed on playerId, beside GameIdByPlayer, for the same reason - the
    // Arbiter's per-player state that is not worth a column and not reachable from a handler.
    //
    // The real Arbiter keeps a live counter at User+0x1E4 and S_PLAY_TIME reads it straight off.
    // We keep the ENTER stamp here and commit the difference to characters.play_seconds at
    // leave-world, so a crash costs one session rather than the whole history.
    // =========================================================================================

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, long> EnteredAt = new();

    /// <summary>Stamp the moment this player entered the world. Called from SDB_USER_ENTERWORLD.</summary>
    public static void MarkEnteredWorld(int playerId, long nowUnix)
    {
        if (playerId > 0) EnteredAt[playerId] = nowUnix;
    }

    /// <summary>
    /// T188. Drop the enter stamp and the game id this player is registered under. Used by the
    /// admin reset, which is the repair for a session that ended without a clean leave-world;
    /// the play-time difference is deliberately NOT committed here, because a stamp left over
    /// from a dead World session would bank time the character did not play.
    /// </summary>
    public static bool ForgetPlayer(int playerId)
    {
        if (playerId <= 0) return false;
        bool had = EnteredAt.TryRemove(playerId, out _);
        had |= GameIdByPlayer.TryRemove(playerId, out _);
        return had;
    }

    /// <summary>Seconds since the enter stamp, or 0 when there is none (never entered, or
    /// already closed out). Does NOT clear the stamp - S_PLAY_TIME asks mid-session.</summary>
    public static long SecondsInWorld(int playerId, long nowUnix)
    {
        if (playerId <= 0 || !EnteredAt.TryGetValue(playerId, out long since)) return 0;
        long secs = nowUnix - since;
        return secs > 0 ? secs : 0;
    }

    /// <summary>
    /// Close the session out: take the stamp, clear it, and return the seconds to commit.
    /// Returns 0 when there was no open session, so calling this twice adds nothing twice.
    /// </summary>
    public static long TakeSessionSeconds(int playerId, long nowUnix)
    {
        if (playerId <= 0 || !EnteredAt.TryRemove(playerId, out long since)) return 0;
        long secs = nowUnix - since;
        return secs > 0 ? secs : 0;
    }

    /// <summary>
    /// The whole of it: close the session out and add it to the row. Safe to call for a player
    /// who never entered. This is what the leave-world path calls.
    /// </summary>
    public long CommitPlayTime(int playerId, long nowUnix)
    {
        long secs = TakeSessionSeconds(playerId, nowUnix);
        if (secs <= 0) return 0;
        long total = _store.AddCharacterPlaySeconds(playerId, secs);
        _log.LogInformation("play time: player {Id} played {Secs}s this session, {Total}s total",
            playerId, secs, total);
        return secs;
    }

    /// <summary>
    /// Clones a captured reply template and patches the live reqId at the documented offset.
    /// Used for data-bearing handlers where the rest of the payload is character-specific
    /// static data from the capture (tutorial tips, reputation, quests, etc.).
    /// </summary>
    public static byte[] BuildFromStaticData(byte[] template, int templateReqIdOffset, byte[] request, int requestReqIdOffset = 0)
    {
        uint reqId = requestReqIdOffset + 4 <= request.Length
            ? BitConverter.ToUInt32(request, requestReqIdOffset) : 0;
        var r = (byte[])template.Clone();
        BitConverter.GetBytes(reqId).CopyTo(r, templateReqIdOffset);
        return r;
    }

    // Payload offset = frame offset - 6.
    private static uint U32(byte[] p, int frameOff) => BitConverter.ToUInt32(p, frameOff - 6);
    private static long I64(byte[] p, int frameOff) => BitConverter.ToInt64(p, frameOff - 6);

    /// <summary>
    /// Diagnostic only: the blob is opaque and we never modify it, but position (x,y,z floats)
    /// sits at blob offset 220 in every capture (lobby_tap.log: logout blob == last client
    /// move packet). Logging it lets us verify save/restore without parsing anything else.
    /// </summary>
    private static string BlobPos(byte[] blob)
    {
        if (blob.Length < 232) return "?";
        float x = BitConverter.ToSingle(blob, 220), y = BitConverter.ToSingle(blob, 224), z = BitConverter.ToSingle(blob, 228);
        return $"({x:F1}, {y:F1}, {z:F1})";
    }

    private bool OnUserEnterWorld(WorldLink link, byte[] p)
    {
        if (p.Length < 18) { _log.LogWarning("SDB_USER_ENTERWORLD too short ({Len})", p.Length); return false; }
        uint replyId = U32(p, 10);
        int playerId = (int)U32(p, 14);

        // T113: a previous session that never saw SA_LEAVE_WORLD (a crash, a dropped link) is
        // closed out here rather than lost - this is the one point we are certain the player is
        // not in world any more, because they are entering it.
        long enterUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        CommitPlayTime(playerId, enterUnix);
        MarkEnteredWorld(playerId, enterUnix);
        _store.MarkCharacterEnteredWorld(playerId);   // T191f: the isNewCharacter record

        // T121: arm the Alt+A push again. This is the one event a zone change does NOT raise
        // and a relog does, which is exactly the difference between cap_final_gm_client2's
        // enter-world push (frame 99) and its /@teleport (548/549, no push).
        Handlers.ArbiterClientHandlers.ResetGmSkillPush(playerId);

        var chr = _store.GetCharacter(playerId);
        bool found = chr?.WorldBlob != null && chr.WorldBlob.Length == WorldBlobSize;
        // T147: gathering levels live in their own rows, like money; put them back where the
        // real Arbiter's column binding puts them (UserData +0x1C8..+0x1D4).
        if (found) ArtisanDb.StampGatheringProfs(chr!.WorldBlob, _store.GetGatheringProfs(playerId));
        if (found) _store.StampCrests(playerId, chr!.WorldBlob); // T197: separately committed glyph changes survive a World restart.
        if (!found)
            _log.LogWarning("SDB_USER_ENTERWORLD: player {Id} has no world blob (found={F}, len={L}) - replying not-found",
                playerId, chr != null, chr?.WorldBlob?.Length ?? 0);
        else
            // Debug, not Information: the sha256 is here to prove a save/restore round trip when
            // one is being investigated, and it costs a hash of 15 KB on every enter-world.
            _log.LogDebug("DBS_USER_ENTERWORLD: sent world blob for '{Name}' (id {Id}) from DB, pos {Pos}, sha256 {Sha}",
                chr!.Name, playerId, BlobPos(chr.WorldBlob!), Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(chr.WorldBlob!))[..16]);

        link.SendFrame(DBS_USER_ENTERWORLD, BuildDbsUserEnterWorld(replyId, found ? chr!.WorldBlob : null));

        // The real Arbiter follows the blob with DBS_USER_RESTRICTION (0x2830) ~3 ms later
        // (lobby_tap.log pkt 131): [u32 off=22][u32 count=0][u64 gameId]. This is the only A->W
        // frame that differs between two logins in the real capture, and chat/whisper bans live
        // in the restriction record World initialises from it. gameId form: 0x80000AF00000|id,
        // unmasked, as sent in AS_ENTER_WORLD [24] and captured (01 00 F0 0A 00 80 00 00).
        if (found)
        {
            ulong gameId = GameIdByPlayer.TryGetValue(playerId, out var g) ? g : 0x80000AF00000UL | (ulong)(uint)playerId;
            link.SendFrame(DBS_USER_RESTRICTION, BuildDbsUserRestriction(gameId));
        }
        return true;
    }

    public const ushort DBS_USER_RESTRICTION = 0x2830;

    // ---- Handshake: SA_LOAD_GUARD 0x147D -> AS_ELECTION_STATE + AS_LOAD_GUARD* + FINISH ----
    //
    // T209c RENAMED THIS FAMILY. It was AS_PROMOTION_* here, and it is not promotions: the
    // Arbiter's own opcode-name table (Arb_part_003.c:4978-4993) gives 0x147D SA_LOAD_GUARD,
    // 0x147E AS_LOAD_GUARD, 0x1480 AS_LOAD_GUARD_FINISH and 0x1484 AS_ELECTION_STATE, and World
    // consumes them in GuardManager::OnLoadGuard / OnLoadFinish. This is the castle-and-lord
    // Guard system. Promotions are the separate 0x291x family (SDB_LOAD_PROMOTION_LIST 0x2912,
    // handled above), which is why the old comment's PromotionController crash note was attached
    // to the wrong opcodes entirely.
    //
    // WHAT RETAIL SENDS. World::SendLoadGuard (Arb_part_074.c:4035) sends AS_ELECTION_STATE
    // carrying ONE u32 - the lord-election state, not a list header - then walks ten continent
    // slots and calls Guard::SendLoadGuard (Arb_part_081.c:3130) once per Guard object, then
    // sends AS_LOAD_GUARD_FINISH with an empty payload. The per-guard writer takes its length as
    // the compile-time constant 0x550 (1360), so an AS_LOAD_GUARD frame is never empty; and the
    // walk begins with a begin-not-equal-end test, so WITH NO GUARDS IT EMITS NO FRAME AT ALL.
    // Zero 0x147E frames is the correct wire for a server with no castles, which is what this
    // one is: nothing here models Guards, continents' guard trees or the lord election.
    //
    // WHY THE FILE IS GONE. data/promotions_147E.bin held 23 records of 1368 bytes (the old
    // comment said 24 - both 23x1368 and 24x1311 come to 31464, which is how that slipped past),
    // replayed from a capture of a server that DID have castles. Each 1360-byte payload is a
    // memcpy of a live C++ Guard struct - bytes 568..700 are that process's heap pointers - so it
    // can neither be generated nor meaningfully edited, and replaying it pushed 23 foreign
    // castles into a world that has none. Sending nothing is both retail-correct and truthful.
    public const ushort SA_LOAD_GUARD = 0x147D;
    public const ushort AS_ELECTION_STATE = 0x1484;
    public const ushort AS_LOAD_GUARD = 0x147E;
    public const ushort AS_LOAD_GUARD_FINISH = 0x1480;

    /// <summary>The lord-election state AS_ELECTION_STATE carries. 0 is "no election running",
    /// which is this server's permanent answer until the election system is modelled.</summary>
    public const uint ElectionStateNone = 0;

    /// <summary>
    /// SA_LOAD_GUARD (0x147D): the election state, then one AS_LOAD_GUARD per Guard, then FINISH.
    /// This server has no Guards, so the middle is empty - exactly what retail emits for a world
    /// with no castles. The two frames it does send are byte-identical to what it sent before
    /// T209c; all that changed is that the 23 replayed guard records are gone.
    /// </summary>
    private bool OnLoadGuardRequest(WorldLink link)
    {
        link.SendFrame(AS_ELECTION_STATE, BitConverter.GetBytes(ElectionStateNone));
        link.SendFrame(AS_LOAD_GUARD_FINISH, Array.Empty<byte>());
        _log.LogInformation("0x147D: no guards on this planet - election state {State}, 0 x 0x147E, finish", ElectionStateNone);
        return true;
    }

    /// <summary>[u16 year][u16 month][u16 day][u16 hour][u16 minute][u16 second][u32 nanoseconds] (16 B).</summary>
    public static void WriteArbTimestamp(byte[] b, int off, DateTime t)
    {
        BitConverter.GetBytes((ushort)t.Year).CopyTo(b, off);
        BitConverter.GetBytes((ushort)t.Month).CopyTo(b, off + 2);
        BitConverter.GetBytes((ushort)t.Day).CopyTo(b, off + 4);
        BitConverter.GetBytes((ushort)t.Hour).CopyTo(b, off + 6);
        BitConverter.GetBytes((ushort)t.Minute).CopyTo(b, off + 8);
        BitConverter.GetBytes((ushort)t.Second).CopyTo(b, off + 10);
        BitConverter.GetBytes((uint)(t.Ticks % TimeSpan.TicksPerSecond * 100)).CopyTo(b, off + 12);
    }

    /// <summary>Locate a data file the same way StarterBlob does (env TERASHARP_STARTER_BLOB's folder, walk-up from the binary, TERASHARP_DATA).</summary>
    private static byte[]? LoadDataFile(string name, ref byte[]? cache, int expectedSize)
    {
        if (cache != null) return cache;
        foreach (var candidate in DataFileCandidates(name))
        {
            if (candidate == null || !File.Exists(candidate)) continue;
            var bytes = File.ReadAllBytes(candidate);
            if (expectedSize > 0 && bytes.Length != expectedSize) continue;
            return cache = bytes;
        }
        return null;
    }

    private static IEnumerable<string?> DataFileCandidates(string name)
    {
        var blob = TerasConfig.Get("TERASHARP_STARTER_BLOB");
        if (!string.IsNullOrEmpty(blob)) yield return Path.Combine(Path.GetDirectoryName(blob) ?? ".", name);
        string? dir = AppContext.BaseDirectory;
        for (int i = 0; i < 8 && !string.IsNullOrEmpty(dir); i++)
        {
            yield return Path.Combine(dir, "data", name);
            dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar));
        }
        var root = TerasConfig.Get("TERASHARP_DATA") ?? @"D:\v100\TERA_SERVER.100";
        yield return Path.Combine(root, "TeraSharp", "data", name);
    }

    // ---- AS_DUNGEON_TIMELINE_ON_OFF (0x1581) and where the 98 ids really come from ----
    //
    // 1 s after the handshake completes (0x2955/0x2952), before any player, the real Arbiter
    // sends 98 x 0x1581. Without them a FIRST enter-world into a fresh World process (zone 5
    // character, i.e. any new character) is silently dropped after DBS_USER_ENTERWORLD and World
    // later crashes; after one successful login (dob, zone 7005) later enters proceed. Before T5
    // these pushes were replayed by accident (attributed to 0x15A8); T5 correctly sealed 0x15A8
    // and they vanished.
    //
    // T29/T33 corrected two things about them.
    //
    // (1) THE PAYLOAD SHAPE. Writer FUN_1407ace90 (Arb_part_066.c:17289) is
    //         FUN_140350eb0(pkt, 0x1581); FUN_14013d0b0(u32); FUN_1403513d0(u8); FUN_140351270(u64);
    //     and the dumper (Arb_part_011.c:10058) names the fields DungeonId @frame+6, IsOn
    //     @frame+10, NextChange @frame+0x0B. So the 13-byte payload is
    //         [u32 DungeonId][u8 IsOn][u64 NextChange]
    //     not the [u32 id][u32 1][u32 0][u8 0] this used to write. The bytes are identical while
    //     IsOn = 1 and NextChange = 0 (the whole of both captures), which is why nothing ever
    //     caught it - but a non-zero NextChange would have gone out as garbage.
    //
    // (2) THE LIST IS NOT OURS TO CHOOSE. The real Arbiter does not decide these 98 ids: it
    //     ECHOES them, one 0x1581 per record, out of the DSA_DUNGEON_TIMELINE_OPEN_INFO (0x13F2)
    //     frame World sends moments earlier. lobby_tap.log [122] -> [123..124] and
    //     cap_newchar.log [111] -> [112..113]: same 98 ids, same order, every record CurrOpen=1.
    //     Handler_DSA_DUNGEON_TIMELINE_OPEN_INFO (Arb_part_062.c:332) is a pure fan-out into
    //     DungeonManager::SetDungeonTimelineOpen (FUN_140786fd0, Arb_part_065.c:13433).
    //     status/HANDSHAKE-DATA.md section 0 has the evidence.
    //
    // PostHandshakeDungeonIds is therefore a FALLBACK, not the mechanism. It still goes out from
    // OnWorldReady because it is live-verified to unblock that first enter-world, and because
    // 0x1581 is an idempotent state push - World is told "dungeon X is on until T", so the echo
    // that follows simply restates it with World's own values. Set
    // SendPostHandshakeDungeonBurst = false once a live run shows "echoed N dungeon-timeline
    // states" in the log; after that the echo is the only source and the ids are World's, not a
    // 2026-09-13 capture's. status/HANDSHAKE-DATA.md section 2 also shows the list is exactly
    // reproducible from the datasheets (HandshakeData.LoadDungeonTimelineIds).
    public const ushort AS_DUNGEON_OPEN_1581 = 0x1581;
    public static readonly uint[] PostHandshakeDungeonIds =
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

    /// <summary>
    /// AS_DUNGEON_TIMELINE_ON_OFF (0x1581): [u32 DungeonId][u8 IsOn][u64 NextChange] - 13 bytes.
    /// Writer FUN_1407ace90 (Arb_part_066.c:17289); field names from the dumper at
    /// Arb_part_011.c:10058. The defaults reproduce the captured bytes exactly
    /// (34 08 00 00 01 00 00 00 00 00 00 00 00 for dungeon 0x834).
    /// </summary>
    public static byte[] Build1581(uint dungeonId, bool isOn = true, ulong nextChange = 0)
    {
        var p = new byte[13];
        BitConverter.GetBytes(dungeonId).CopyTo(p, 0);
        p[4] = (byte)(isOn ? 1 : 0);
        BitConverter.GetBytes(nextChange).CopyTo(p, 5);
        return p;
    }

    // ---- DSA_DUNGEON_TIMELINE_OPEN_INFO (0x13F2) -> N x AS_DUNGEON_TIMELINE_ON_OFF (0x1581) ----
    //
    // Layout, from the dumper at Arb_part_016.c:2781 (L"OpenInfo", L"DungeonId", L"CurrOpen",
    // L"NextChange", L"SendSystemMessage") and the handler's own bounds check, which decodes a
    // node only while the read cursor plus 0x16 is still <= the frame length:
    //
    //   payload [0] u32 count   [4] u32 listOffset (FRAME-relative, 14)
    //   then count x 0x16-byte nodes:
    //     [+0] u32 self  [+4] u32 next  [+8] u32 DungeonId  [+12] u8 CurrOpen
    //     [+13] i64 NextChange  [+21] u8 SendSystemMessage
    //
    // Almost every 0x13F2 is the EMPTY form - an 8-byte payload, count 0 - which is why the
    // whole opcode has been treated as a heartbeat (WorldBridge.OpHeartbeat14). Across the four
    // captures: 909 empty frames and exactly two non-empty ones, both carrying 98 records.
    // 0x13F2 stays in WorldReplayTable.OneWayFromWorld - it must never become a request entry -
    // and is handled here instead.
    public const ushort DSA_DUNGEON_TIMELINE_OPEN_INFO = 0x13F2;
    public const int TimelineNodeSize = 0x16;
    public const int TimelineNodeDungeonId = 8;
    public const int TimelineNodeCurrOpen = 12;
    public const int TimelineNodeNextChange = 13;
    /// <summary>Sanity cap on the record count; the real list is the ~200 DungeonData sheets.</summary>
    public const int MaxTimelineNodes = 4096;

    /// <summary>
    /// True once World has sent a non-empty 0x13F2 on this process and we echoed it. Purely
    /// diagnostic: it is what tells the human the echo path is live and the fallback burst can
    /// be switched off. The echo always arrives AFTER OnWorldReady in every capture, so it can
    /// never suppress a burst that has already gone out.
    /// </summary>
    public bool TimelineEchoed { get; private set; }

    /// <summary>
    /// Set false to stop OnWorldReady sending the captured 98-id fallback and rely on the echo
    /// alone - which is what the real Arbiter does. Left true until a live run confirms the echo
    /// fires, because the fallback is the live-verified fix for a first enter-world into a fresh
    /// World process.
    /// </summary>
    public static bool SendPostHandshakeDungeonBurst = true;

    /// <summary>Every (dungeonId, isOn, nextChange) in a 0x13F2 list, or null if malformed.
    /// An empty list parses to an empty array - that is the heartbeat form, not an error.</summary>
    public static List<(uint dungeonId, bool isOn, ulong nextChange)>? ParseTimelineOpenInfo(byte[] payload)
    {
        if (payload == null || payload.Length < 8) return null;
        int count = (int)BitConverter.ToUInt32(payload, 0);
        int start = (int)BitConverter.ToUInt32(payload, 4) - 6;      // frame-relative -> payload
        var outp = new List<(uint, bool, ulong)>(Math.Max(0, Math.Min(count, 256)));
        if (count == 0) return outp;
        if (count < 0 || count > MaxTimelineNodes || start < 0 ||
            (long)start + (long)count * TimelineNodeSize > payload.Length) return null;
        for (int i = 0; i < count; i++)
        {
            int o = start + i * TimelineNodeSize;
            outp.Add((BitConverter.ToUInt32(payload, o + TimelineNodeDungeonId),
                      payload[o + TimelineNodeCurrOpen] != 0,
                      BitConverter.ToUInt64(payload, o + TimelineNodeNextChange)));
        }
        return outp;
    }

    /// <summary>The 0x1581 frames a 0x13F2 payload should produce, in order.</summary>
    public static List<byte[]>? BuildTimelineEcho(byte[] payload)
    {
        var nodes = ParseTimelineOpenInfo(payload);
        if (nodes == null) return null;
        var frames = new List<byte[]>(nodes.Count);
        foreach (var (id, isOn, next) in nodes) frames.Add(Build1581(id, isOn, next));
        return frames;
    }

    private bool OnDungeonTimelineOpenInfo(WorldLink link, byte[] payload)
    {
        var frames = BuildTimelineEcho(payload);
        if (frames == null)
        {
            _log.LogWarning("0x13F2: malformed open-info list ({Len}-byte payload) - not echoed", payload.Length);
            return true;                       // handled: never fall through to the replay table
        }
        if (frames.Count == 0) return true;    // the heartbeat form
        foreach (var f in frames) link.SendFrame(AS_DUNGEON_OPEN_1581, f);
        TimelineEchoed = true;
        _log.LogInformation(
            "0x13F2: echoed {N} dungeon-timeline states back as 0x1581 (fallback burst {Burst})",
            frames.Count, SendPostHandshakeDungeonBurst ? "also sent - see HANDSHAKE-DATA.md section 6a" : "disabled");
        return true;
    }

    // ---- Post-handshake config burst (T23) ----
    // 63 one-way A->W pushes the real Arbiter sends straight after the handshake, right after the
    // 0x294F -> 0x2955 + 0x2952 replay and before any player exists
    // (D:\packetlogs\arb_world_2026-09-13T11-33-30-680Z.log seq 104-126, 11:42:22.403-.408).
    // Nothing in them carries a reqId or a DLM id, so none of it can head-block a user - which is
    // why login worked without them - but between them they configure VIP store slots, achievement
    // seasons, dark rift, GM events, play-guide rewards, five festivals, the in-game shop
    // catalogue and the continent channel counts. status/RELOG-CAPTURE-NOTES.md section 4 has the
    // full opcode table; data/handshake_burst.md is the per-frame index.
    //
    // T209b: the frames are BUILT from Protocol/InterServerDefinitions - 63 frames, 1082 frame
    // bytes, 50 distinct opcodes, every field named from the retail dumpers - see HandshakeBurst.
    // data/handshake_burst.bin (TSIS container, same shape as cap_t15.bin) is kept only as the
    // test's evidence; LoadHandshakeBurst/ParseBurst/BuildHandshakeBurst below still read it so
    // Tests/T209b.cs can compare against it, and nothing at runtime touches it any more.
    //
    // Four captures were diffed frame by frame - arb_world.log (2026-09-12 06:36), lobby_tap.log
    // (09-13 02:51), cap_newchar.log (09-13 05:49) and the 09-13 11:42 relog. The burst is
    // byte-identical in all four EXCEPT for exactly two u64s:
    //   0x15BD AS_SYNC_DATE_TIME              payload+0 = unix seconds at send time. World echoes
    //                                                     the value straight back as 0x15BC.
    //   0x14D1 AS_SET_DARK_RIFT_DAILY_COMPLETED payload+8 = the most recent daily reset.
    // Everything else is replayed verbatim, including 0x15DE AS_DUNGEON_PHASE_LAST_RESET_TIME,
    // whose u64 reads 2026-09-12T03:57:29Z in ALL FOUR captures - on both sides of a daily reset,
    // a day apart - so it is a stored value the Arbiter keeps in SQL, not a clock.
    public const ushort AS_SYNC_DATE_TIME = 0x15BD;
    public const ushort SA_SYNC_DATE_TIME = 0x15BC;                 // World's echo; one-way back
    public const ushort AS_DUNGEON_PHASE_LAST_RESET_TIME = 0x15DE;
    public const ushort AS_SET_DARK_RIFT_DAILY_COMPLETED = 0x14D1;
    public const ushort AS_INIT_TIMELINE_CHANGES = 0x1582;
    public const int SyncDateTimeOffset = 0;        // 0x15BD payload+0, u64 unix seconds
    public const int DarkRiftResetTimeOffset = 8;   // 0x14D1 payload+8, u64 unix seconds
    public const int HandshakeBurstFrameCount = 63;
    public const string HandshakeBurstFile = "handshake_burst.bin";

    /// <summary>T209: <c>economy.synthItemRecords</c>. True builds the starter kit's item records
    /// instead of copying them out of <c>starter_inventory.bin</c>, so the file is not needed at
    /// all. Read per call rather than cached, so it can be flipped without a restart.
    ///
    /// <para>T209c part 2: DEFAULT TRUE, live-verified - character 'newnew' was created with the
    /// setting on and <c>data/starter_inventory.bin</c> absent, entered world with its six starter
    /// items and still had them after a relog. Set the key to 0 / false to go back to copying the
    /// captured record.</para></summary>
    public static bool SynthItemRecords
    {
        get
        {
            string? v = TerasConfig.Get("TERASHARP_SYNTH_ITEM_RECORDS");
            // Unset (and empty, which is how an unset variable reads on Linux) means ON.
            if (string.IsNullOrWhiteSpace(v)) return true;
            return v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// The daily-reset instant the real Arbiter stamps into 0x14D1 - and into the SECOND u64 of
    /// the handshake's 0x1595 AS_EVENT_MATCHING_INFO, which carries the same value in every
    /// capture (the first u64 there is a constant 2026-09-09T14:50:00Z in all four).
    /// Observed: 2026-09-11T15:00:00Z in the 09-12 06:36 capture, 2026-09-12T15:00:00Z in all
    /// three 09-13 captures (02:51, 05:49, 11:42) - i.e. the most recent 15:00:00 UTC at or
    /// before now. 15:00 UTC is also midnight in UTC+9, which is what a KR-configured box calls
    /// local midnight; no capture was taken between 15:00 and 24:00 UTC, so the two readings
    /// cannot be told apart yet. That is why the hour is a parameter and not a local-midnight
    /// calculation - change DailyResetHourUtc, not the arithmetic, if a later capture settles it.
    /// </summary>
    public const int DailyResetHourUtc = 15;

    public static ulong DailyResetUnixSeconds(DateTimeOffset now, int resetHourUtc = DailyResetHourUtc)
    {
        var midnight = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero);
        var boundary = midnight.AddHours(resetHourUtc);
        if (boundary > now) boundary = boundary.AddDays(-1);
        return (ulong)boundary.ToUnixTimeSeconds();
    }

    /// <summary>One record of data/handshake_burst.bin: captured sequence number, opcode, payload.</summary>
    public readonly record struct BurstFrame(uint Seq, ushort Op, byte[] Payload);

    private static IReadOnlyList<BurstFrame>? _handshakeBurst;
    private static byte[]? _handshakeBurstBytes;

    /// <summary>Test seam. Pass null to force the next call to read from disk again.</summary>
    internal static void SetHandshakeBurstForTest(IReadOnlyList<BurstFrame>? burst)
    {
        _handshakeBurst = burst;
        if (burst == null) _handshakeBurstBytes = null;
    }

    /// <summary>
    /// Parse the TSIS container: "TSIS", u32 recordCount, then per record
    /// u32 seq | u16 opcode | u32 payloadLength | payload (little-endian throughout).
    /// Returns null for anything that is not a well-formed container, so a truncated or missing
    /// file degrades to "burst not sent" with a warning instead of taking the handshake down.
    /// </summary>
    public static IReadOnlyList<BurstFrame>? ParseBurst(byte[]? bytes)
    {
        if (bytes == null || bytes.Length < 8) return null;
        if (bytes[0] != (byte)'T' || bytes[1] != (byte)'S' || bytes[2] != (byte)'I' || bytes[3] != (byte)'S') return null;
        uint count = BitConverter.ToUInt32(bytes, 4);
        if (count > 4096) return null;
        var list = new List<BurstFrame>((int)count);
        int o = 8;
        for (uint i = 0; i < count; i++)
        {
            if (o + 10 > bytes.Length) return null;
            uint seq = BitConverter.ToUInt32(bytes, o);
            ushort op = BitConverter.ToUInt16(bytes, o + 4);
            uint len = BitConverter.ToUInt32(bytes, o + 6);
            o += 10;
            if (len > (uint)(bytes.Length - o)) return null;
            var payload = new byte[len];
            Buffer.BlockCopy(bytes, o, payload, 0, (int)len);
            o += (int)len;
            list.Add(new BurstFrame(seq, op, payload));
        }
        return o == bytes.Length ? list : null;
    }

    public static IReadOnlyList<BurstFrame>? LoadHandshakeBurst()
        => _handshakeBurst ??= ParseBurst(LoadDataFile(HandshakeBurstFile, ref _handshakeBurstBytes, 0));

    /// <summary>
    /// One burst frame as it should go out now: the captured payload with the live u64 replaced.
    /// Every other opcode comes back verbatim. Always a fresh array - the cached payload from the
    /// TSIS file is never handed to a caller.
    /// </summary>
    public static byte[] PatchBurstFrame(ushort op, byte[] payload, DateTimeOffset now, ulong dailyReset)
    {
        var p = (byte[])payload.Clone();
        switch (op)
        {
            case AS_SYNC_DATE_TIME:
                if (p.Length >= SyncDateTimeOffset + 8)
                    BitConverter.GetBytes((ulong)now.ToUnixTimeSeconds()).CopyTo(p, SyncDateTimeOffset);
                break;
            case AS_SET_DARK_RIFT_DAILY_COMPLETED:
                if (p.Length >= DarkRiftResetTimeOffset + 8)
                    BitConverter.GetBytes(dailyReset).CopyTo(p, DarkRiftResetTimeOffset);
                break;
        }
        return p;
    }

    /// <summary>The whole burst in captured order, stamped for <paramref name="now"/>.</summary>
    public static List<(ushort op, byte[] payload)> BuildHandshakeBurst(IReadOnlyList<BurstFrame> burst, DateTimeOffset now)
    {
        ulong reset = DailyResetUnixSeconds(now);
        var frames = new List<(ushort, byte[])>(burst.Count);
        foreach (var f in burst) frames.Add((f.Op, PatchBurstFrame(f.Op, f.Payload, now, reset)));
        return frames;
    }

    /// <summary>
    /// Called by WorldBridge once the handshake completes (on the link that carried 0x294F,
    /// exactly once per World process). Sends the config burst first, then the 0x1581
    /// dungeon-open pushes - the order in lobby_tap.log, the capture the live-verified login came
    /// from, and the order that puts AS_INITIALIZE_DUNGEON_ID / AS_DUNGEON_DISABLED_LIST before
    /// the per-dungeon opens. (cap_newchar.log has them the other way round only because that
    /// Arbiter stalled ~11 s on a DB query before its burst went out; the burst's own internal
    /// order is identical in all four captures.)
    /// </summary>
    public void OnWorldReady(WorldLink link)
    {
        // T209b: the burst is BUILT from Protocol/InterServerDefinitions now (see HandshakeBurst),
        // not replayed out of data/handshake_burst.bin. Every frame reproduces the capture byte for
        // byte from named fields, so there is no file to miss and nothing to warn about; the
        // capture is the test's evidence (Tests/T209b.cs), not a runtime dependency - T139.
        {
            var now = DateTimeOffset.UtcNow;
            ulong reset = DailyResetUnixSeconds(now);
            var burst = HandshakeBurst.Build(now, reset);
            foreach (var (op, payload) in burst)
                link.SendFrame(op, op == 0x157E && _store != null
                    ? Handlers.QaDungeonCommands.BuildDisabledDungeons(_store.GetDisabledDungeons())
                    : op == 0x14E9 ? Handlers.QaDungeonEvents.BuildRookieSnapshot(_store, now, rememberReplay: true)
                    : op == 0x156B ? Handlers.QaFestivalCommands.DailySnapshot(_store, now)
                    : op == 0x13B5 && Handlers.QaInventoryCommands.StyleWarehouseOverride is bool style ? new[] { style ? (byte)1 : (byte)0 }
                    : Handlers.QaAchievementCommands.BootFrame(_store, op, now) ?? payload);
            _log.LogInformation("Post-handshake: sent {N} config pushes built from defs (0x15BD = {Now:u}, 0x14D1 daily reset = {Reset:u})",
                burst.Count, now.UtcDateTime, DateTimeOffset.FromUnixTimeSeconds((long)reset).UtcDateTime);
        }

        // T201: restore an explicitly persisted contents(type33) selection after the baseline configuration.
        if (_store?.GetQaDecoUi() is int deco) link.SendFrame(0x1588, Handlers.QaUiCommands.BuildDecoContents(deco));
        Handlers.QaDungeonEvents.ReplayAbnormalities(_store, link, DateTimeOffset.UtcNow);
        Handlers.QaFestivalCommands.Replay(_store, link, DateTimeOffset.UtcNow);
        Handlers.QaUtilityCommands.ReplayNonPk(_store, link);
        Handlers.QaAwakenCommands.ReplayWorld(_store, link, DateTimeOffset.UtcNow);
        Handlers.QaStyleShopCommands.ReplayWorld(_store, link, DateTimeOffset.UtcNow);
        Handlers.QaItemPeriodCommands.Replay(_store, link, DateTimeOffset.UtcNow);
        Handlers.QaPurchaseCommands.Replay(_store, link);
        // T208d: the party table, for a World that came up after parties already existed. The real
        // Arbiter does this from its own registration path - PartyManager::OnConnectWorldServer
        // (Arb_part_079.c:16418) walks every party and unicasts its roster to the new World - and
        // without it a battleground or dungeon World resolves a party handle we hand it to zero and
        // builds nothing (cap_bg4 17:07:44; status/T208c-BG.md, status/T208d-PARTY-REPLAY.md).
        PartyWiring.ReplayToWorld(WorldRouting.WorldIdOf(link));
        Handlers.QaNpcShopCommands.Replay(_store, link);

        // The FALLBACK, not the mechanism - the real Arbiter only ever echoes World's 0x13F2.
        // See the comment on PostHandshakeDungeonIds and status/HANDSHAKE-DATA.md section 0.
        if (!SendPostHandshakeDungeonBurst)
        {
            _log.LogInformation("Post-handshake: 0x1581 fallback burst disabled - waiting for World's 0x13F2 echo");
            return;
        }
        // T159: the ids from the four sheets (DatasheetLoader.DungeonTimelineIds); PostHandshakeDungeonIds
        // is their built-in - identical, order included, when the sheets are the capture's.
        var ids = DatasheetLoader.DungeonTimelineIds.Value;
        foreach (var id in ids)
            link.SendFrame(AS_DUNGEON_OPEN_1581, Build1581((uint)id));
        _log.LogInformation("Post-handshake: sent {N} x 0x1581 dungeon-open pushes (fallback; World's 0x13F2 echo supersedes them)",
            ids.Length);
    }

    // ---- T154: SDB_LOAD_CITY_GUILD_INFO (0x2954) -> DBS_LOAD_CITY_GUILD_INFO (0x2955) ----
    // World asks for the guilds holding a Civil Unrest city once per boot and then on its own
    // schedule (~25 a day live). The boot one was only ever answered because the replay table
    // files the captured 0x2955 under 0x294F, the request that follows it; every later one was
    // "no replay". No DlmId, so it never wedged a user - it just went unanswered.
    // Request (Arbiter dumper, guard 0xd): LeagueId@06, SeasonId@0A - payload 0, 4.
    // Reply - written by FUN_1406526f0 (Arb_part_054.c:13406), read by
    // Handler_DBS_LOAD_CITY_GUILD_INFO (WorldServer.exe.c:3016303, frame >= 0x16):
    //   [u32 count][u32 first element, frame offset, 0 = none][u32 LeagueId][u32 SeasonId]
    //   then 44-byte elements [u32 self][u32 next, 0 = last][u32 GuildDbId]
    //   [i64 GuildTowerBuildTime][i64 GuildTowerDestroyTime][u32 TotalKill][u32 TotalDeath]
    //   [u32 TotalDestroy][u32 GuildTowerMaintainBonus].
    // World walks the elements from the first offset, checking each one's self offset, and
    // registers each guild; an empty list changes nothing, so the boot duplicate the 0x294F
    // replay still sends is harmless. Live, empty: arb_world.log / cap_invensize (league 1,
    // season 1) and cap_social4 / cap_final (1, 2) are all 00*8 + LeagueId + SeasonId.
    public const ushort SDB_LOAD_CITY_GUILD_INFO = 0x2954;
    public const ushort DBS_LOAD_CITY_GUILD_INFO = 0x2955;
    public const int CityGuildReplyHeader = 16, CityGuildElementSize = 0x2C;

    public static byte[] BuildDbsLoadCityGuildInfo(int leagueId, int seasonId,
                                                   IReadOnlyList<CharacterStore.CityGuildRow>? rows)
    {
        rows ??= Array.Empty<CharacterStore.CityGuildRow>();
        var r = new byte[CityGuildReplyHeader + rows.Count * CityGuildElementSize];
        BitConverter.GetBytes(rows.Count).CopyTo(r, 0);
        BitConverter.GetBytes(rows.Count == 0 ? 0 : 6 + CityGuildReplyHeader).CopyTo(r, 4);
        BitConverter.GetBytes(leagueId).CopyTo(r, 8);
        BitConverter.GetBytes(seasonId).CopyTo(r, 12);
        for (int i = 0; i < rows.Count; i++)
        {
            var g = rows[i];
            int at = CityGuildReplyHeader + i * CityGuildElementSize;
            int self = 6 + at;
            BitConverter.GetBytes(self).CopyTo(r, at);
            BitConverter.GetBytes(i + 1 < rows.Count ? self + CityGuildElementSize : 0).CopyTo(r, at + 4);
            BitConverter.GetBytes(g.GuildDbId).CopyTo(r, at + 8);
            BitConverter.GetBytes(g.TowerBuildTime).CopyTo(r, at + 12);
            BitConverter.GetBytes(g.TowerDestroyTime).CopyTo(r, at + 20);
            BitConverter.GetBytes(g.TotalKill).CopyTo(r, at + 28);
            BitConverter.GetBytes(g.TotalDeath).CopyTo(r, at + 32);
            BitConverter.GetBytes(g.TotalDestroy).CopyTo(r, at + 36);
            BitConverter.GetBytes(g.MaintainBonus).CopyTo(r, at + 40);
        }
        return r;
    }

    private bool OnLoadCityGuildInfo(WorldLink link, byte[] payload)
    {
        int league = Ep32i(payload, 0), season = Ep32i(payload, 4);
        var rows = _store is null ? null : _store.GetCityGuilds(league, season);
        if (rows is { Count: > 0 })
            _log.LogInformation("SDB_LOAD_CITY_GUILD_INFO: league {League} season {Season} -> {N} guild(s)",
                league, season, rows.Count);
        else
            _log.LogDebug("SDB_LOAD_CITY_GUILD_INFO: league {League} season {Season} -> no owning guild", league, season);
        link.SendFrame(DBS_LOAD_CITY_GUILD_INFO, BuildDbsLoadCityGuildInfo(league, season, rows));
        return true;
    }

    // ---- T156: the Vanguard Initiative (event matching) - the Arbiter's half ----
    // Every client packet of the window is World-built: S_AVAILABLE_EVENT_MATCHING_LIST (0x810D,
    // serializer FUN_140456950, WorldServer.exe.c:800772), S_ADD_NEW / S_REMOVE_EVENT_MATCHING_QUEST
    // and S_UPDATE_EVENT_MATCHING_BONUS_INFO all have their writers in WorldServer.exe and reach
    // the client through SA_BYPASS_TO_CLIENT; the Arbiter has no writer for any of them. World
    // fills the quest list from its own EventMatching datasheet. What it asks the Arbiter for:
    //
    //   0x1507 SA_AVAILABLE_EVENT_MATCHING_LIST [i64 ArbiterUser][i64 PartyId][u8 ByPlayer]
    //     -> 0x1591 AS_EVENT_MATCHING_INFO_LIST [BattleFieldList ref][DungeonList ref]
    //        [i32 UserDbId][u8 ByPlayer]. World (Handler_AS_EVENT_MATCHING_INFO_LIST,
    //        WorldServer.exe.c:2986781) finds the user by UserDbId and only then sends the
    //        client its list. The two lists are the ids the user is queued for in the Arbiter's
    //        match pool (WorldOfPartyMatchHelper::SendEventMatchingInfoToWorldServer); every
    //        capture has both empty. Until T156 the replay table answered with the captured
    //        UserDbId 1 - World looked up the wrong user and the window stayed empty.
    //   0x293C SDB_UPDATE_USER_DAILY_EVENT_COUNT -> the same 0x1591 push (ByPlayer 0), then 0x293D.
    //        That is the Arbiter's push on a quest state change (cap_social4 seq 421 -> 422, 423).
    //   0x1592 SA_LOAD_EVENT_MATCHING_INFO (once per boot) -> 0x1595 AS_EVENT_MATCHING_INFO
    //        [OffPairList ref][i64 ExtraRewardLastWeeklyResetTime][i64 AddRewardLastResetTime],
    //        then 0x1582 AS_INIT_TIMELINE_CHANGES - both in every capture, in that order.
    //   0x1598 / 0x159A: World moves those two stamps forward; answered [u8 Result][i64 time].
    //        Unanswered, World re-sends them - 80+ times in a TeraSharp session.
    //   0x2965 / 0x2967: per-character add-reward receive counts, DLM items.
    //   0x293A / 0x293E: the per-character daily_event row (see CharacterStore.DailyEventRow).
    public const ushort SA_AVAILABLE_EVENT_MATCHING_LIST = 0x1507;
    public const ushort AS_EVENT_MATCHING_INFO_LIST = 0x1591;
    public const ushort SA_LOAD_EVENT_MATCHING_INFO = 0x1592;
    public const ushort AS_EVENT_MATCHING_INFO = 0x1595;
    public const ushort SA_UPDATE_PLAYGUIDE_EXTRA_REWARD_RESETTIME = 0x1598;
    public const ushort AS_UPDATE_PLAYGUIDE_EXTRA_REWARD_RESETTIME = 0x1599;
    public const ushort SA_UPDATE_EVENT_MATCHING_ADD_REWARD_RESETTIME = 0x159A;
    public const ushort AS_UPDATE_EVENT_MATCHING_ADD_REWARD_RESETTIME = 0x159B;
    public const ushort SDB_UPDATE_ADDITIONAL_REWARD_RECV_COUNT = 0x2965;
    public const ushort DBS_UPDATE_ADDITIONAL_REWARD_RECV_COUNT = 0x2966;
    public const ushort DBS_LOAD_ADDITIONAL_REWARD_RECV_COUNT = 0x2968;
    public const ushort DBS_LOAD_USER_DAILY_EVENT = 0x293B;

    /// <summary>Handler_SA_AVAILABLE_EVENT_MATCHING_LIST's guard: frame 0x17.</summary>
    public const int AvailableEventMatchingListMinPayload = 0x17 - 6;
    /// <summary>The two reset-time updates' guard: frame 0xE.</summary>
    public const int ResetTimeUpdateMinPayload = 0xE - 6;
    /// <summary>SDB_UPDATE_ADDITIONAL_REWARD_RECV_COUNT's guard: frame 0x15.</summary>
    public const int UpdateAdditionalRewardMinPayload = 0x15 - 6;
    /// <summary>DailyEventCompletionInfo: five i32s, the record 0x293C carries and 0x293B returns.</summary>
    public const int DailyEventRecordLength = 20;

    /// <summary>counters keys for the two global stamps 0x1595 carries.</summary>
    public const string ExtraRewardWeeklyResetKey = "em_extra_reward_weekly_reset";
    public const string AddRewardResetKey = "em_add_reward_reset";
    /// <summary>
    /// What an empty DB answers 0x1592 with: arb_world.log seq 11 (2026-09-09T14:50Z weekly,
    /// 2026-09-11T15:00Z daily) - the bytes the replay table sent before T156, so a first boot
    /// changes nothing. World then sends 0x1598 / 0x159A with the current boundaries, and the
    /// stored values take over from there.
    /// </summary>
    public const long DefaultExtraRewardWeeklyReset = 0x6AA17218;
    public const long DefaultAddRewardReset = 0x6AA41770;

    /// <summary>
    /// 0x1582 as the real Arbiter sends it right after 0x1595 - identical in all seven captures
    /// that have the pair. (The handshake burst's 0x1582 is the all-zero variant.)
    /// </summary>
    public static readonly byte[] InitTimelineChangesOnLoad = { 0, 0, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0 };

    /// <summary>Test seam for the one timestamp T156 makes up: a new daily_event row's stamp.</summary>
    public Func<DateTimeOffset> EventClock { get; set; } = () => DateTimeOffset.UtcNow;

    /// <summary>
    /// AS_EVENT_MATCHING_INFO_LIST, 27-byte frame, both lists empty (offset = frame end, length 0).
    /// Byte-exact against cap_social seq 459 / 547 and cap_social4 seq 8039 (ByPlayer 1).
    /// </summary>
    public static byte[] BuildEventMatchingInfoList(int userDbId, byte byPlayer)
    {
        var p = new byte[21];
        BitConverter.GetBytes(0x1Bu).CopyTo(p, 0);    // BattleFieldList
        BitConverter.GetBytes(0x1Bu).CopyTo(p, 8);    // DungeonList
        BitConverter.GetBytes(userDbId).CopyTo(p, 16);
        p[20] = byPlayer;
        return p;
    }

    /// <summary>AS_EVENT_MATCHING_INFO, 30-byte frame, OffPairList empty. Byte-exact against every captured 0x1595.</summary>
    public static byte[] BuildEventMatchingInfo(long extraRewardWeeklyReset, long addRewardReset)
    {
        var p = new byte[24];
        BitConverter.GetBytes(0x1Eu).CopyTo(p, 0);    // OffPairList (8-byte records; none stored)
        BitConverter.GetBytes(extraRewardWeeklyReset).CopyTo(p, 8);
        BitConverter.GetBytes(addRewardReset).CopyTo(p, 16);
        return p;
    }

    /// <summary>0x1599 / 0x159B: [u8 Result][i64 the time World sent]. World applies the time only when Result != 0.</summary>
    public static byte[] BuildResetTimeReply(bool ok, long time)
    {
        var p = new byte[9];
        p[0] = (byte)(ok ? 1 : 0);
        BitConverter.GetBytes(time).CopyTo(p, 1);
        return p;
    }

    /// <summary>
    /// DBS_LOAD_ADDITIONAL_REWARD_RECV_COUNT: [list frame offset 0x13][list byte length]
    /// [DlmId][u8 Success] then 8-byte records [i32 EventId][i32 AcquireNum]. Success is 1 only
    /// when the character has records - the empty reply is cap_social4 seq 285 byte for byte.
    /// </summary>
    public static byte[] BuildDbsLoadAdditionalRewardRecvCount(uint dlmId, IReadOnlyList<(int EventId, int AcquireNum)>? rows)
    {
        int n = rows?.Count ?? 0;
        var p = new byte[13 + 8 * n];
        BitConverter.GetBytes(0x13u).CopyTo(p, 0);
        BitConverter.GetBytes((uint)(8 * n)).CopyTo(p, 4);
        BitConverter.GetBytes(dlmId).CopyTo(p, 8);
        p[12] = (byte)(n > 0 ? 1 : 0);
        for (int i = 0; i < n; i++)
        {
            BitConverter.GetBytes(rows![i].EventId).CopyTo(p, 13 + 8 * i);
            BitConverter.GetBytes(rows[i].AcquireNum).CopyTo(p, 17 + 8 * i);
        }
        return p;
    }

    /// <summary>
    /// DBS_LOAD_USER_DAILY_EVENT, 52-byte frame (Handler_SDB_LOAD_USER_DAILY_EVENT,
    /// ArbiterServer.exe.c:1277606): [record frame offset 0x20][record length 20][reqId]
    /// [u8 Success][i64 ExtraRewardLastResetTime][u8 GotExtraReward][i32 ExtraRewardValue]
    /// [20-byte record]. No row: Success 0 and zeros - cap_newchar seq 338.
    /// </summary>
    public static byte[] BuildDbsLoadUserDailyEvent(uint reqId, CharacterStore.DailyEventRow? row)
    {
        var p = new byte[26 + DailyEventRecordLength];
        BitConverter.GetBytes(0x20u).CopyTo(p, 0);
        BitConverter.GetBytes((uint)DailyEventRecordLength).CopyTo(p, 4);
        BitConverter.GetBytes(reqId).CopyTo(p, 8);
        if (row is null) return p;
        p[12] = 1;
        BitConverter.GetBytes(row.ExtraRewardReset).CopyTo(p, 13);
        p[21] = (byte)(row.GotExtraReward ? 1 : 0);
        BitConverter.GetBytes(row.ExtraRewardValue).CopyTo(p, 22);
        for (int i = 0; i < 5 && i < row.Counts.Length; i++)
            BitConverter.GetBytes(row.Counts[i]).CopyTo(p, 26 + 4 * i);
        return p;
    }

    /// <summary>
    /// The five counts out of 0x293C's record ref ([0] frame offset, [4] length). A ref that is
    /// zero, short, or points outside the frame reads as zeros - the real handler copies nothing
    /// from an offset it rejects.
    /// </summary>
    public static int[] ReadDailyEventRecord(byte[] payload)
    {
        var counts = new int[5];
        if (payload.Length < 8) return counts;
        uint frameOff = BitConverter.ToUInt32(payload, 0);
        uint len = Math.Min(BitConverter.ToUInt32(payload, 4), (uint)DailyEventRecordLength);
        if (frameOff < 6) return counts;
        ulong start = frameOff - 6u;
        uint n = len / 4;
        if (start + 4ul * n > (ulong)payload.Length) return counts;
        for (int i = 0; i < n; i++) counts[i] = BitConverter.ToInt32(payload, (int)start + 4 * i);
        return counts;
    }

    /// <summary>A World i64 stamp for the log. The fuzz test sends ones DateTimeOffset rejects, so never throw.</summary>
    private static string UnixText(long t)
        => t is >= 0 and <= 253402300799 ? DateTimeOffset.FromUnixTimeSeconds(t).UtcDateTime.ToString("u") : t.ToString();

    private bool OnAvailableEventMatchingList(WorldLink link, byte[] payload)
    {
        if (payload.Length < AvailableEventMatchingListMinPayload)
        {
            _log.LogWarning("SA_AVAILABLE_EVENT_MATCHING_LIST: {Len} B, shorter than the 0x17-byte frame World sends - dropped",
                payload.Length + 6);
            return true;
        }
        ulong gameId = (ulong)Ep64(payload, 0);
        byte byPlayer = payload[16];
        int userDbId = PlayerIdForGameId?.Invoke(gameId) ?? 0;
        if (userDbId <= 0)
        {
            // The real Arbiter does nothing for a user it cannot find. Not a DLM item, so
            // staying silent wedges nothing; a replayed reply would name the wrong user.
            _log.LogWarning("SA_AVAILABLE_EVENT_MATCHING_LIST: no character for ArbiterUser 0x{Game:X} - not answered", gameId);
            return true;
        }
        link.SendFrame(AS_EVENT_MATCHING_INFO_LIST, BuildEventMatchingInfoList(userDbId, byPlayer));
        _log.LogDebug("SA_AVAILABLE_EVENT_MATCHING_LIST: user {User} byPlayer {By} party 0x{Party:X} -> 0x1591",
            userDbId, byPlayer, Ep64(payload, 8));
        return true;
    }

    private bool OnLoadEventMatchingInfo(WorldLink link)
    {
        long weekly = _store?.GetCounterValue(ExtraRewardWeeklyResetKey, DefaultExtraRewardWeeklyReset) ?? DefaultExtraRewardWeeklyReset;
        long daily = _store?.GetCounterValue(AddRewardResetKey, DefaultAddRewardReset) ?? DefaultAddRewardReset;
        link.SendFrame(AS_EVENT_MATCHING_INFO, BuildEventMatchingInfo(weekly, daily));
        link.SendFrame(AS_INIT_TIMELINE_CHANGES, (byte[])InitTimelineChangesOnLoad.Clone());
        _log.LogInformation("SA_LOAD_EVENT_MATCHING_INFO: weekly extra-reward reset {Weekly}, add-reward reset {Daily}",
            UnixText(weekly), UnixText(daily));
        return true;
    }

    private bool OnUpdateExtraRewardResetTime(WorldLink link, byte[] payload)
    {
        if (payload.Length < ResetTimeUpdateMinPayload) return true;
        long t = Ep64(payload, 0);
        _store?.SetCounterValue(ExtraRewardWeeklyResetKey, t);
        link.SendFrame(AS_UPDATE_PLAYGUIDE_EXTRA_REWARD_RESETTIME, BuildResetTimeReply(true, t));
        _log.LogInformation("SA_UPDATE_PLAYGUIDE_EXTRA_REWARD_RESETTIME: weekly extra-reward reset -> {T}", UnixText(t));
        return true;
    }

    private bool OnUpdateAddRewardResetTime(WorldLink link, byte[] payload)
    {
        if (payload.Length < ResetTimeUpdateMinPayload) return true;
        long t = Ep64(payload, 0);
        int cleared = 0;
        if (_store is not null)
        {
            _store.SetCounterValue(AddRewardResetKey, t);
            cleared = _store.ClearEventMatchingRewards();   // ClearEventMatchingAddRewardCount, unconditional
        }
        link.SendFrame(AS_UPDATE_EVENT_MATCHING_ADD_REWARD_RESETTIME, BuildResetTimeReply(true, t));
        _log.LogInformation("SA_UPDATE_EVENT_MATCHING_ADD_REWARD_RESETTIME: add-reward reset -> {T}, {N} count(s) cleared",
            UnixText(t), cleared);
        return true;
    }

    private bool OnUpdateAdditionalRewardRecvCount(WorldLink link, byte[] payload)
    {
        int user = Ep32i(payload, 4), eventId = Ep32i(payload, 8), acquire = Ep32i(payload, 12);
        if (_store is not null && user > 0 && payload.Length >= UpdateAdditionalRewardMinPayload)
            _store.SetEventMatchingReward(user, eventId, acquire);
        link.SendFrame(DBS_UPDATE_ADDITIONAL_REWARD_RECV_COUNT, BuildReqIdAck(payload, 0));   // [DlmId][1]
        return true;
    }

    private bool OnLoadAdditionalRewardRecvCount(WorldLink link, byte[] payload)
    {
        int user = Ep32i(payload, 4);
        var rows = _store is null || user <= 0 ? null : _store.GetEventMatchingRewards(user);
        link.SendFrame(DBS_LOAD_ADDITIONAL_REWARD_RECV_COUNT, BuildDbsLoadAdditionalRewardRecvCount(Ep32(payload, 0), rows));
        return true;
    }

    private bool OnLoadUserDailyEvent(WorldLink link, byte[] payload)
    {
        int user = Ep32i(payload, 4);
        var row = _store is null || user <= 0 ? null : _store.GetDailyEvent(user);
        link.SendFrame(DBS_LOAD_USER_DAILY_EVENT, BuildDbsLoadUserDailyEvent(Ep32(payload, 0), row));
        return true;
    }

    /// <summary>
    /// 0x293C: store the five counts (and a positive extra-reward stamp), push 0x1591 for the
    /// user - the real handler calls User::SendAvailableEventMatchingListFromArbiter(false, party)
    /// between the store and the reply - then answer 0x293D.
    /// </summary>
    private bool OnUpdateUserDailyEventCount(WorldLink link, byte[] payload)
    {
        int user = Ep32i(payload, 12);
        if (_store is not null && user > 0)
            _store.SetDailyEventCounts(user, ReadDailyEventRecord(payload), Ep64(payload, 16), EventClock().ToUnixTimeSeconds());
        if (user > 0)
            link.SendFrame(AS_EVENT_MATCHING_INFO_LIST, BuildEventMatchingInfoList(user, 0));
        link.SendFrame(DBS_UPDATE_USER_DAILY_EVENT_COUNT, BuildReqIdAck(payload, 8));
        return true;
    }

    /// <summary>
    /// 0x293E: User::UpdateGetExtraReward(bool, int, bool). The bool and int are what the
    /// daily_event load returns at +21 / +22 (both zero in every capture, as every captured
    /// 0x293E writes 0 / 0); the trailing flag (1 in every capture) is not stored.
    /// </summary>
    private bool OnUpdateGetExtraReward(WorldLink link, byte[] payload)
    {
        int user = Ep32i(payload, 4);
        if (_store is not null && user > 0 && payload.Length >= 13)
            _store.SetDailyEventExtraReward(user, payload[8] != 0, Ep32i(payload, 9), EventClock().ToUnixTimeSeconds());
        link.SendFrame(DBS_UPDATE_GET_EXTRA_REWARD, BuildReqIdAck(payload, 0));
        return true;
    }

    // ---- SDB_RESULT_CITY_WAR (0x295C) -> 0x15ED + DBS_RESULT_CITY_WAR (0x295D), T23 ----
    // World sends 0x295C once per city-war result, 42-byte frame, shortly after the handshake
    // (cap_newchar.log seq 115, 05:48:54.119; the real Arbiter answered 9 s later at seq 128/129
    // because the handler does a DB round trip first). It is absent from arb_world.log, so the
    // replay table has no entry for it and today it produces a bare "no replay for 0x295C".
    //
    // Handler_SDB_RESULT_CITY_WAR (Arb_part_064.c:2367, min frame 0x2a) calls CityWarEnd
    // (FUN_14064f030, Arb_part_054.c:10714), which broadcasts 0x15ED through FUN_140641300, and
    // then writes its own reply:
    //   SendToSession<PKT_DBS_RESULT_CITY_WAR_WRITE,int,int>  ->  0x295D  [u32][u32]
    // with both ints read from the REQUEST frame at +0x16 and +0x1a, i.e. payload+16 and
    // payload+20. Capture: the request carries 1 and 1 there and the reply is
    //   01 00 00 00 01 00 00 00.
    // 0x15ED is PKT_AS_UPDATE_ADMIN_CITY_WAR_INTEREST_ONLY_WRITE<int&,bool&> = [u32 cityWarId]
    // [u8 interestOnly], and FUN_14064f030 passes (cityWarId, false) - cityWarId being that same
    // payload+16. Capture: 01 00 00 00 00. Neither frame carries a reqId or a DLM id, so neither
    // can head-block; they are made real because the capture shows the Arbiter sending them and
    // because a replayed 0x295D would carry the captured ints.
    //
    // NOT a pair: 0x15F9 SA_REWARD_CITYWAR_KILL_DEATH_COUNT arrives in the same millisecond
    // (cap_newchar.log seq 116) and looks like a request, but
    // Handler_SA_REWARD_CITYWAR_KILL_DEATH_COUNT (Arb_part_062.c:13831) contains no
    // SendToSession at all - it only awards CPOINT and messages the players. It is one-way and
    // belongs in WorldReplayTable.OneWayFromWorld; see the T23 section of
    // status/RELOG-CAPTURE-NOTES.md for the exact one-line change.
    public const ushort SDB_RESULT_CITY_WAR = 0x295C;
    public const ushort DBS_RESULT_CITY_WAR = 0x295D;
    public const ushort AS_UPDATE_ADMIN_CITY_WAR_INTEREST_ONLY = 0x15ED;
    public const ushort SA_REWARD_CITYWAR_KILL_DEATH_COUNT = 0x15F9; // one-way; no reply exists
    public const int CityWarIdOffset = 16;      // request payload+16 (frame+0x16)
    public const int CityWarResultOffset = 20;  // request payload+20 (frame+0x1a)

    /// <summary>0x15ED: [u32 cityWarId][u8 interestOnly=0] - 5 bytes.</summary>
    public static byte[] Build15ED(byte[] request)
    {
        var r = new byte[5];
        if (request.Length >= CityWarIdOffset + 4) Array.Copy(request, CityWarIdOffset, r, 0, 4);
        return r;
    }

    /// <summary>0x295D: [u32 request+16][u32 request+20] - 8 bytes.</summary>
    public static byte[] BuildDbs295D(byte[] request)
    {
        var r = new byte[8];
        if (request.Length >= CityWarIdOffset + 4) Array.Copy(request, CityWarIdOffset, r, 0, 4);
        if (request.Length >= CityWarResultOffset + 4) Array.Copy(request, CityWarResultOffset, r, 4, 4);
        return r;
    }

    private bool OnResultCityWar(WorldLink link, byte[] payload)
    {
        if (payload.Length < CityWarResultOffset + 4) return false;   // -> replay table
        link.SendFrame(AS_UPDATE_ADMIN_CITY_WAR_INTEREST_ONLY, Build15ED(payload));
        link.SendFrame(DBS_RESULT_CITY_WAR, BuildDbs295D(payload));
        _log.LogInformation("SDB_RESULT_CITY_WAR: city war {Id} result {Res} - sent 0x15ED + 0x295D",
            BitConverter.ToUInt32(payload, CityWarIdOffset), BitConverter.ToUInt32(payload, CityWarResultOffset));
        return true;
    }

    // ---- SDB_USER_LOAD_INVENTORY (0x27A2) -> DBS_USER_LOAD_POCKET_DATA (0x27A3) + DBS_USER_LOAD_INVENTORY (0x27A4) ----
    // The inventory reply is data-bearing and OWNED: every item carries the owner's playerId. The
    // replay table serves dob's captured list (owner 1, level-58 gear) to everyone, and World
    // answers a mismatch with SA_ENTER_WORLD_FAILED (0x138D) + "EnterWorld Failed" (seen live
    // for 'test' and 'testtwo', 2026-09-14 00:35). The real Arbiter sent a 6-item starter list
    // for the new character (cap_newchar.log 05:49:03.191, 3235-byte frame):
    //   0x27A3 payload: [u32 off=19][u32 count=0][u32 reqId][u8 ok=1]   (empty pocket list)
    //   0x27A4 payload: [u32 off=19][u32 len=3216][u32 reqId][u8 0][6 x 536-byte items]
    //     item+16 = u32 owner playerId (payload 29, 565, 1101, 1637, 2173, 2709)
    // data/starter_inventory.bin is that 3229-byte payload verbatim. Until inventory is persisted
    // (PERSISTENCE-MAP.md) every character other than the captured playerId 1 gets the starter
    // list with reqId + owner patched; playerId 1 keeps the replay-table capture.
    public const ushort DBS_USER_LOAD_POCKET_DATA = 0x27A3;
    public const ushort DBS_USER_LOAD_INVENTORY = 0x27A4;
    public const int StarterInventorySize = 3229;
    /// <summary>
    /// T105. Write the starter kit into rows with FRESH item db ids.
    ///
    /// <para>The kit's own ids are <see cref="StarterInventory.FirstStarterItemId"/>..+n - the
    /// same six numbers for every character, because before T44 there was no items table and
    /// they only had to be stable per login. With rows behind them those ids are a collision:
    /// <c>UpsertItem</c> is an upsert on <c>item_db_id</c>, so seeding character B MOVED
    /// character A's six rows to B, A came back with an empty bag, and the next
    /// SDB_USER_LOAD_INVENTORY re-seeded A - stealing them back. Two characters played tug of
    /// war over ids 7..12 and each lost the whole bag in turn, because
    /// <see cref="CharacterStore.ReplaceInventory"/> clears the pocket first. That is the live
    /// 2026-09-19 report of a character that had played being served six starter items.</para>
    ///
    /// <para>The ids are drawn from the same counter every other item uses, which is what the
    /// real Arbiter does - 7..12 in the capture is simply what its counter was at for the first
    /// character ever created. The reply World gets is rebuilt from the rows immediately after
    /// this, so it carries the new ids and never the kit's.</para>
    /// </summary>
    public static int SeedStarterRows(CharacterStore store, int playerId, byte[] payload)
    {
        ArgumentNullException.ThrowIfNull(store);
        var kit = BagItems.SplitPayload(payload, playerId);
        if (kit.Count == 0) return 0;

        int first = store.ReserveItemIds(kit.Count);
        var rows = new List<CharacterStore.ItemRow>(kit.Count);
        for (int i = 0; i < kit.Count; i++)
        {
            var src = kit[i];
            int id = first + i;
            byte[]? rec = src.Record;
            if (rec is not null && rec.Length >= BagItems.RecordIdOffset + 4)
            {
                rec = (byte[])rec.Clone();
                BitConverter.GetBytes(id).CopyTo(rec, BagItems.RecordIdOffset);
            }
            rows.Add(src with { ItemDbId = id, Record = rec });
        }
        store.ReplaceInventory(playerId, rows);
        return rows.Count;
    }

    public const int StarterInventoryItemStart = 13;
    public const int StarterInventoryItemSize = 536;
    public const int StarterInventoryOwnerOffset = 16;
    /// <summary>
    /// The playerId the 2026-09-13 replay capture belongs to. T142b: this is NO LONGER a
    /// special case in <c>OnLoadInventory</c>. Character 1 is dob, a live character with a
    /// level and gear, and short-circuiting her to the replay meant her store rows were never
    /// read OR seeded - so every QA grant landed in `items` where nothing read it back, and the
    /// admin API showed an empty bag that had in fact always been empty. The constant stays
    /// because the character-creation test asserts new characters do not land on it.
    /// </summary>
    public const int CapturedInventoryPlayerId = 1;
    private static byte[]? _starterInventory;

    private bool OnLoadInventory(WorldLink link, byte[] payload)
    {
        if (payload.Length < 8) return false;
        uint reqId = BitConverter.ToUInt32(payload, 0);
        int playerId = (int)BitConverter.ToUInt32(payload, 4);
        // T142b: character 1 used to return false here and be served the replay capture. See
        // CapturedInventoryPlayerId - she is a live character and is loaded like everyone else.

        // T201: clear_inven is an authoritative empty bag, never a request for a fresh starter kit.
        if (_store?.InventoryWasCleared(playerId) == true)
        {
            link.SendFrame(DBS_USER_LOAD_POCKET_DATA, BuildEmptyListType1(payload, 0));
            link.SendFrame(DBS_USER_LOAD_INVENTORY, BagItems.BuildPayload(_store.GetInventoryItems(playerId), reqId, playerId));
            return true;
        }

        // T209: the class kit comes from CreateCharData.xml either way; the file only supplies
        // the 536-byte record skeleton. With economy.synthItemRecords on - the default since
        // T209c part 2 was live-verified - the records are built instead
        // (StarterInventory.BuildSynthetic) and no file is needed. The file still wins when it is
        // there, so a deployment that keeps it is byte-for-byte unchanged.
        var template = LoadStarterInventory();
        if (template == null && !SynthItemRecords)
        {
            _log.LogWarning("SDB_USER_LOAD_INVENTORY: starter_inventory.bin not found and economy.synthItemRecords is off - falling back to replay (World will reject it for player {Pid}). Remove the setting to build the records instead.", playerId);
            return false;
        }
        // T14: the kit the real Arbiter would have created for this character's class
        // (CreateCharData.xml). Falls back to the captured glaiver list when we do not know the
        // class — an unknown class is better served the six items that are live-verified to get
        // past SA_ENTER_WORLD_FAILED than nothing at all.
        // _store is null in the pure-protocol unit tests; a class kit needs the row.
        var chr = _store is null ? null : _store.GetCharacter(playerId);
        int classId = chr?.Class ?? -1;
        var kit = template != null
            ? StarterInventory.Build(template, classId, playerId, reqId)
            : StarterInventory.BuildSynthetic(classId, playerId, reqId);

        // The kit is only the STARTING point now. T44: the inventory is rows in `items`, the
        // same table the warehouse uses, so a character who has picked anything up gets what
        // they actually have. The kit is written out once, the first time we see a character
        // with no rows, and from then on the reply is rebuilt from the rows.
        byte[] inventory;
        if (kit != null)
        {
            inventory = kit;
            _log.LogInformation("SDB_USER_LOAD_INVENTORY: player {Pid} -> {N} starter items for class {Cls} ({Name})",
                playerId, StarterInventory.ForClass(classId)!.Count, classId,
                classId >= 0 && classId < StarterInventory.ClassNames.Length ? StarterInventory.ClassNames[classId] : "?");
        }
        else if (template != null)
        {
            inventory = BuildStarterInventory(template, reqId, (uint)playerId);
            _log.LogWarning("SDB_USER_LOAD_INVENTORY: player {Pid} has no class kit (class {Cls}) - serving the captured glaiver list",
                playerId, classId);
        }
        else
        {
            // T209: no class kit AND no file. The captured glaiver list was the last resort and
            // it needs the file, so there is nothing left to serve but the truth.
            _log.LogWarning("SDB_USER_LOAD_INVENTORY: player {Pid} has no class kit (class {Cls}) and no starter_inventory.bin to fall back on",
                playerId, classId);
            return false;
        }

        if (_store is not null)
        {
            if (_store.CountInventoryItems(playerId) == 0)
            {
                int seeded = SeedStarterRows(_store, playerId, inventory);
                _log.LogInformation("SDB_USER_LOAD_INVENTORY: seeded {N} starter row(s) for player {Pid}",
                    seeded, playerId);
            }

            var rows = _store.GetInventoryItems(playerId);
            if (rows.Count > 0)
            {
                // Byte-identical to the kit for a character who has not touched anything: each
                // row keeps the 536-byte record it was seeded with, and GetInventoryItems
                // returns them in the (pocket, slot) order StarterInventory sorted them into.
                inventory = BagItems.BuildPayload(rows, reqId, playerId);
                _log.LogInformation("SDB_USER_LOAD_INVENTORY: player {Pid} -> {N} item row(s) from the store",
                    playerId, rows.Count);
            }
            else
            {
                // Seeding produced nothing (a malformed kit). Serve what we built rather than an
                // empty inventory - World rejects a mismatch, it does not tolerate a gap.
                _log.LogWarning("SDB_USER_LOAD_INVENTORY: player {Pid} has no item rows after seeding - serving the kit directly",
                    playerId);
            }
        }

        link.SendFrame(DBS_USER_LOAD_POCKET_DATA, BuildEmptyListType1(payload, 0));
        link.SendFrame(DBS_USER_LOAD_INVENTORY, inventory);
        return true;
    }

    /// <summary>Starter inventory payload with the live DLM id at [8] and the owner playerId in every item.</summary>
    public static byte[] BuildStarterInventory(byte[] template, uint reqId, uint playerId)
    {
        var r = (byte[])template.Clone();
        BitConverter.GetBytes(reqId).CopyTo(r, 8);
        for (int off = StarterInventoryItemStart + StarterInventoryOwnerOffset; off + 4 <= r.Length; off += StarterInventoryItemSize)
            BitConverter.GetBytes(playerId).CopyTo(r, off);
        return r;
    }

    /// <summary>Test seam.</summary>
    internal static void SetStarterInventoryForTest(byte[]? t) => _starterInventory = t;

    private static byte[]? LoadStarterInventory()
    {
        if (_starterInventory != null) return _starterInventory;
        foreach (var candidate in StarterInventoryCandidates())
        {
            if (candidate == null || !File.Exists(candidate)) continue;
            var bytes = File.ReadAllBytes(candidate);
            if (bytes.Length != StarterInventorySize) continue;
            return _starterInventory = bytes;
        }
        return null;
    }

    private static IEnumerable<string?> StarterInventoryCandidates()
    {
        yield return TerasConfig.Get("TERASHARP_STARTER_INVENTORY");
        var blob = TerasConfig.Get("TERASHARP_STARTER_BLOB");
        if (!string.IsNullOrEmpty(blob)) yield return Path.Combine(Path.GetDirectoryName(blob) ?? ".", "starter_inventory.bin");
        string? dir = AppContext.BaseDirectory;
        for (int i = 0; i < 8 && !string.IsNullOrEmpty(dir); i++)
        {
            yield return Path.Combine(dir, "data", "starter_inventory.bin");
            dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar));
        }
        var root = TerasConfig.Get("TERASHARP_DATA") ?? @"D:\v100\TERA_SERVER.100";
        yield return Path.Combine(root, "TeraSharp", "data", "starter_inventory.bin");
    }

    /// <summary>
    /// Live gameId per playerId, set by LoginHandlers.OnSelectUser. gameId is a per-login counter
    /// (0x80000AF00000 | n, n restarting at 1 with each World process — cap_newchar.log: playerId 2
    /// got ...0001), NOT derived from playerId. DBS_USER_RESTRICTION must carry the same value
    /// AS_ENTER_WORLD used.
    /// </summary>
    public static readonly System.Collections.Concurrent.ConcurrentDictionary<int, ulong> GameIdByPlayer = new();

    /// <summary>DBS_USER_RESTRICTION (0x2830): [u32 listOff=22][u32 count=0][u64 gameId] — 16 bytes (capture pkt 131).</summary>
    public static byte[] BuildDbsUserRestriction(ulong gameId)
    {
        var p = new byte[16];
        BitConverter.GetBytes(22u).CopyTo(p, 0);   // 6-byte header + 16-byte payload = 22 = empty list at end
        BitConverter.GetBytes(gameId).CopyTo(p, 8);
        return p;
    }

    /// <summary>
    /// DBS_USER_ENTERWORLD reply: [u32 blobOffset=19][u32 blobLen][u32 replyId][u8 found][blob].
    /// With no blob: found=0, blobLen=0, 13-byte body. Writer FUN_1407ada00 (Arb_part_066.c).
    /// </summary>
    public static byte[] BuildDbsUserEnterWorld(uint replyId, byte[]? blob)
    {
        int blobLen = blob?.Length ?? 0;
        var reply = new byte[13 + blobLen];
        BitConverter.GetBytes(19).CopyTo(reply, 0);
        BitConverter.GetBytes(blobLen).CopyTo(reply, 4);
        BitConverter.GetBytes(replyId).CopyTo(reply, 8);
        reply[12] = (byte)(blob != null ? 1 : 0);
        blob?.CopyTo(reply, 13);
        return reply;
    }

    private bool OnUpdateUserData(WorldLink link, byte[] p)
    {
        if (p.Length < 25) { _log.LogWarning("SDB_UPDATE_USER_DATA too short ({Len})", p.Length); return false; }
        int blobOff = (int)U32(p, 6);
        int blobLen = (int)U32(p, 10);
        uint reqId = U32(p, 22);
        int playerId = (int)U32(p, 26);

        int payloadBlobOff = blobOff - 6;
        if (blobLen == WorldBlobSize && payloadBlobOff >= 0 && payloadBlobOff + blobLen <= p.Length)
        {
            var blob = new byte[blobLen];
            Array.Copy(p, payloadBlobOff, blob, 0, blobLen);
            _log.LogInformation("SDB_UPDATE_USER_DATA: player {Id} blob pos {Pos}", playerId, BlobPos(blob));
            _store.SaveWorldBlob(playerId, blob);
        }
        else
        {
            _log.LogWarning("SDB_UPDATE_USER_DATA: unexpected blob (off {Off}, len {Len}, payload {P}) for player {Id} - not saved",
                blobOff, blobLen, p.Length, playerId);
        }

        // DBS_UPDATE_USER_DATA: [6] reqId [10] result
        var ack = new byte[8];
        BitConverter.GetBytes(reqId).CopyTo(ack, 0);
        BitConverter.GetBytes(1).CopyTo(ack, 4);
        link.SendFrame(DBS_UPDATE_USER_DATA, ack);
        return true;
    }

    /// <summary>
    /// S_UPDATE_EXP_LEVEL (0x273B) -> D_UPDATE_EXP_LEVEL (0x273C). Writes level and exp to the
    /// characters row and acks with the live DLM id. See the constant above for the layout and
    /// the captured bytes.
    /// </summary>
    private bool OnUpdateExpLevel(WorldLink link, byte[] p)
    {
        if (p.Length < UpdateExpLevelMinPayload)
        {
            // The real handler treats a short frame as a PDL version mismatch. Falling through
            // to the replay table would answer with the CAPTURED DLM id and head-block the user,
            // so refuse loudly instead and let the caller log the miss.
            _log.LogWarning("S_UPDATE_EXP_LEVEL too short ({Len} B payload, need {Need})", p.Length, UpdateExpLevelMinPayload);
            return false;
        }

        uint reqId = U32(p, 6);
        int playerId = (int)U32(p, 10);
        int level = (int)U32(p, 14);          // 0 on an exp-only update
        long exp = I64(p, 18);
        long restBonus = I64(p, 26);

        bool ok = _store.UpdateLevelAndExp(playerId, level >= 1 ? level : null, exp);
        // T76: restBonusPoint is the only rested-xp number that ever crosses the link, and
        // before T76 this handler read it and threw it away - so S_GET_USER_LIST always drew
        // 0%. The real Arbiter hands the same value to User::UpdateUserExpAndRestBonusPoint
        // (level < 1) or User::UpdateUserLevel (level >= 1), both of which persist it through
        // dbo.spSetRestBonusPoint. cap_social_client frame 11 reads it back: dob 419, Test 0.
        _store.SetRestBonus(playerId, restBonus);
        if (level >= 1)
            _log.LogInformation("S_UPDATE_EXP_LEVEL: player {Pid} level {Lvl}, exp {Exp} (rest {Rest})", playerId, level, exp, restBonus);
        else
            _log.LogDebug("S_UPDATE_EXP_LEVEL: player {Pid} exp {Exp} (rest {Rest})", playerId, exp, restBonus);

        link.SendFrame(DBS_UPDATE_EXP_LEVEL, BuildDbs273C(reqId, ok));
        return true;
    }

    /// <summary>
    /// D_UPDATE_EXP_LEVEL (0x273C): [u32 reqId][u8 ok]. ok is the real Arbiter's own
    /// "did the user exist and did the update land" flag (cVar5 in FUN_1408f44b0), not a
    /// constant — World runs OnFail on 0 and OnSuccess on 1, and completes the DLM item either
    /// way, so an honest 0 is safe and a lie is not.
    /// </summary>
    public static byte[] BuildDbs273C(uint reqId, bool ok)
    {
        var r = new byte[5];
        BitConverter.GetBytes(reqId).CopyTo(r, 0);
        r[4] = (byte)(ok ? 1 : 0);
        return r;
    }

    // ================================ T45: the mailbox ================================
    // status/MAIL-WAREHOUSE.md section 3 and status/CLIENT-REJECTS.md section 6. Layouts and
    // builders are in World/ParcelDbHandlers.cs; these are the thin wrappers that read the live
    // DlmId out of the request, touch the store and send.
    //
    // Every one of them answers unconditionally, even on a malformed request - an unanswered
    // per-user DB item head-blocks that user's whole queue for the life of the World process,
    // and a mailbox that shows nothing is a much smaller problem than a character that can
    // never log out again. Same rule the warehouse handlers follow.

    /// <summary>
    /// SDB_LIST_PARCEL (0x2777) -> DBS_LIST_PARCEL (0x2778). The whole point of T45's mail half:
    /// a fresh character gets <c>ParcelCount = 0</c>, <c>MaxPage = 1</c> and an empty list, which
    /// is byte-for-byte what the real Arbiter sends (see
    /// <see cref="ParcelDbHandlers.BuildDbsListParcel"/> for why that is knowable without a
    /// capture).
    /// </summary>
    private bool OnListParcel(WorldLink link, byte[] payload)
    {
        uint dlmId = ParcelDbHandlers.U32(payload, ParcelDbHandlers.ListReqDlmId);
        uint userDbId = ParcelDbHandlers.U32(payload, ParcelDbHandlers.ListReqUserDbId);
        uint viewType = ParcelDbHandlers.U32(payload, ParcelDbHandlers.ListReqViewType);
        uint curPage = ParcelDbHandlers.U32(payload, ParcelDbHandlers.ListReqCurPage);

        if (payload.Length < ParcelDbHandlers.ListRequestSize || _store is null)
        {
            _log.LogWarning("SDB_LIST_PARCEL: {Len} B payload (want {Want}) or no store - empty inbox",
                payload.Length, ParcelDbHandlers.ListRequestSize);
            link.SendFrame(DBS_LIST_PARCEL,
                ParcelDbHandlers.BuildEmptyDbsListParcel(dlmId, viewType, curPage));
            return true;
        }

        // T234: catch up on the rows that were collected before delete-on-collect existed.
        // This is the listing the mailbox is built from, so sweeping here means the player never
        // sees a row that is about to vanish - and an unclaimed parcel is never a candidate.
        if (viewType == 0 && ParcelDbHandlers.DeleteSystemParcelOnCollect)
        {
            int swept = _store.DeleteCollectedParcels((int)userDbId, ParcelDbHandlers.ParcelTypeSystem);
            if (swept > 0)
            {
                _log.LogInformation(
                    "SDB_LIST_PARCEL: user {User} - removed {N} already-collected system parcel(s) (T234)",
                    userDbId, swept);
                // T234b: the sweep is OURS, so this push has no counterpart in the decompile -
                // Handler_SDB_LIST_PARCEL has no SendReadRecvStatusInfo call site at all
                // (Arb_part_071.c:15318..15525), because retail's listing never changes a count.
                // Ours just did, so the badge has to be told.
                PushParcelBadge((int)userDbId, "a listing sweep");
            }
        }

        // T202: ViewType 0 is the inbox, anything else is the Sent box - see BuildParcelList.
        var body = ParcelDbHandlers.BuildParcelList(_store, (int)userDbId, viewType, out uint count, out uint maxPage);
        _log.LogInformation("SDB_LIST_PARCEL: user {User} view {View} page {Page} -> {N} parcel(s)",
            userDbId, viewType, curPage, count);
        link.SendFrame(DBS_LIST_PARCEL, ParcelDbHandlers.BuildDbsListParcel(
            dlmId, ok: true, viewType, curPage, maxPage, count, body.Length == 0 ? null : body));
        return true;
    }

    /// <summary>
    /// SDB_MAKE_PARCEL (0x2779) -> DBS_MAKE_PARCEL (0x277a). World has already taken the items
    /// out of the sender's bag and hands us both the ParcelData record and the transaction
    /// atoms; we allocate the row, keep the record verbatim so DBS_LIST_PARCEL can replay it,
    /// and echo the atoms with any insert ids filled in - the same rule as SDB_ITEM_SINGLE
    /// (INVENTORY-DESIGN.md section 4, "atoms are applied from the reply, never the request").
    /// </summary>
    private bool OnMakeParcel(WorldLink link, byte[] payload)
    {
        uint dlmId = ParcelDbHandlers.U32(payload, ParcelDbHandlers.MakeReqDlmId);
        var record = ParcelDbHandlers.Ref(payload, ParcelDbHandlers.MakeReqParcelDataRef);

        if (payload.Length < ParcelDbHandlers.MakeRequestSize || _store is null)
        {
            _log.LogWarning("SDB_MAKE_PARCEL: {Len} B payload or no store - refusing", payload.Length);
            link.SendFrame(DBS_MAKE_PARCEL,
                ParcelDbHandlers.BuildDbsMakeParcel(null, dlmId, ok: false, sendParcelError: 1, recverDbId: 0));
            return true;
        }

        // T61: the sending client only knows the NAME it typed - ParcelData arrives here with
        // ReceiverDbId = 0 at +0x50 and the name at +0x54 (cap_social.log seq 1485). Reading
        // +0x50 straight off the request filed every parcel under user 0.
        var pf = ParcelDbHandlers.ParseParcelData(record);
        int recverDbId = ParcelDbHandlers.ResolveReceiverDbId(_store, record);
        if (recverDbId <= 0)
        {
            _log.LogWarning("SDB_MAKE_PARCEL: receiver '{Name}' does not exist - refusing", pf.ReceiverName);
            link.SendFrame(DBS_MAKE_PARCEL,
                ParcelDbHandlers.BuildDbsMakeParcel(null, dlmId, ok: false, sendParcelError: 1, recverDbId: 0));
            return true;
        }

        int parcelType = record.Length >= ParcelDbHandlers.ParcelDataParcelType + 4
            ? BitConverter.ToInt32(record, ParcelDbHandlers.ParcelDataParcelType) : 0;
        if (!Handlers.QaMailBrokerCommands.CanMakeParcel(_store, pf.SenderDbId, recverDbId, parcelType,
            out uint sendError, out bool senderFull))
        {
            if (senderFull) global::TeraSharp.Arbiter.Program.World?.SessionForPlayerId(pf.SenderDbId)
                ?.Send(BuildSystemMessage("@1219"));
            link.SendFrame(DBS_MAKE_PARCEL, ParcelDbHandlers.BuildDbsMakeParcel(null, dlmId,
                ok: false, sendParcelError: sendError, recverDbId: (uint)recverDbId));
            return true;
        }
        var (atoms, parsed) = WarehouseHandlers.CloneAtomsWithIds(
            payload, ParcelDbHandlers.MakeReqTransListRef, ParcelDbHandlers.MakeRequestSize, _store.NextItemId);

        int parcelId = _store.CreateParcel(pf.SenderDbId, pf.SenderName, recverDbId,
            pf.Title, message: string.Empty, pf.Money, parcelType);
        if (record.Length > 0) _store.SetParcelRecord(parcelId, record);

        int slot = 0;
        foreach (var atom in parsed)
        {
            if (slot >= CharacterStore.MaxParcelAttachments) break;
            if (atom.ItemDbId == 0) continue;
            _store.AddParcelItem(parcelId, slot++, (int)atom.ItemDbId, atom.TemplateId, atom.Delta);
        }

        _log.LogInformation(
            "SDB_MAKE_PARCEL: parcel {Id} from {From} to {To} ({User}) '{Title}', {Money} money, {N} attachment(s), {B} B record",
            parcelId, pf.SenderName, pf.ReceiverName, recverDbId, pf.Title, pf.Money, slot, record.Length);
        link.SendFrame(DBS_MAKE_PARCEL,
            ParcelDbHandlers.BuildDbsMakeParcel(atoms, dlmId, ok: true, sendParcelError: 0, (uint)recverDbId));
        PushParcelBadge(recverDbId, "new mail");           // T234b, Arb_part_071.c:15656
        return true;
    }

    /// <summary>
    /// SDB_RECV_PARCEL (0x277b) -> DBS_RECV_PARCEL (0x277c). The attachments move into the
    /// receiver's bag; World sent the atoms that do it, so we apply them and echo them back.
    /// </summary>
    private bool OnRecvParcel(WorldLink link, byte[] payload)
    {
        uint dlmId = ParcelDbHandlers.U32(payload, ParcelDbHandlers.RecvReqDlmId);
        uint step = ParcelDbHandlers.U32(payload, ParcelDbHandlers.RecvReqStep);
        uint parcelId = ParcelDbHandlers.U32(payload, ParcelDbHandlers.RecvReqParcelId);

        if (payload.Length < ParcelDbHandlers.RecvRequestSize || _store is null)
        {
            _log.LogWarning("SDB_RECV_PARCEL: {Len} B payload or no store - refusing", payload.Length);
            link.SendFrame(DBS_RECV_PARCEL,
                ParcelDbHandlers.BuildDbsRecvParcel(null, null, dlmId, step, ok: false));
            return true;
        }

        var row = _store.GetParcel((int)parcelId);
        // T65: the record is served through ServedParcelRecord for the same reason the list is -
        // the stored SDB_MAKE_PARCEL bytes carry ParcelId 0 and ReceiverDbId 0.
        // T74: and it is the FULL record, not the 0x9e8 list form - see below.
        byte[]? record = row is null
            ? null : ParcelDbHandlers.ServedParcelRecord(_store, row, full: true);

        // T74: SDB_RECV_PARCEL is two-step, exactly like the broker. cap_social.log seq
        // 1553..1556: step 1 arrives with NO atoms and is answered with the parcel record alone;
        // World builds the attachment atoms from that record and sends them as step 2, which is
        // answered with the record AND those atoms echoed.
        //
        // We were ignoring Step: the step-1 reply carried the short 0x9e8 record (no attachment
        // slots in it) and the gold was paid immediately. World had nothing to build step 2 from,
        // so it never sent one - "0 inserted, 10300 gold" and the item never arrived.
        if (step <= ParcelStepRead)
        {
            _log.LogInformation("SDB_RECV_PARCEL: parcel {Id} step {Step} -> {Len} B record, no atoms yet",
                parcelId, step, record?.Length ?? 0);
            link.SendFrame(DBS_RECV_PARCEL,
                ParcelDbHandlers.BuildDbsRecvParcel(record, null, dlmId, step, ok: row is not null));
            return true;
        }

        var (atoms, parsed) = WarehouseHandlers.CloneAtomsWithIds(
            payload, ParcelDbHandlers.RecvReqTransListRef, ParcelDbHandlers.RecvRequestSize, _store.NextItemId);
        var applied = WarehouseHandlers.Apply(_store, parsed, _store.NextItemId, _log);

        // The gold is paid on the COMMIT pass, with the attachments - not on the read.
        long gold = ClaimParcelMoney(row, applied);
        bool removed = false;
        if (row is not null)
        {
            _store.SetParcelRecved((int)parcelId, row.ReceiverDbId);
            // T234. `record` was materialised above, before any of this, so the reply still
            // carries the parcel the client asked for even once the row is gone.
            removed = ParcelDbHandlers.ShouldDeleteOnCollect(row) && _store.DeleteParcel((int)parcelId);
        }

        _log.LogInformation(
            "SDB_RECV_PARCEL: parcel {Id} step {Step} -> {Ins} inserted, {Chg} amount, {Gold} gold{Removed}",
            parcelId, step, applied.Inserted, applied.AmountChanged, gold, removed ? ", row removed" : string.Empty);
        link.SendFrame(DBS_RECV_PARCEL,
            ParcelDbHandlers.BuildDbsRecvParcel(record, atoms, dlmId, step, ok: row is not null));
        // T234b, Arb_part_071.c:15957 - the owner's push is guarded by Step == 2 there, and step 1
        // is a pure read here too (nothing sets is_read, so no count moves).
        if (row is not null) PushParcelBadge(row.ReceiverDbId, removed ? "a collect that cleared the row" : "a collect");
        return true;
    }

    /// <summary>
    /// SDB_RECV_PARCEL_EX (0x277d) -> DBS_RECV_PARCEL_EX (0x277e). "Receive all": the same
    /// movement, but the request names an owner rather than one parcel and the reply carries the
    /// count it could not deliver.
    /// </summary>
    private bool OnRecvParcelEx(WorldLink link, byte[] payload)
    {
        uint dlmId = ParcelDbHandlers.U32(payload, ParcelDbHandlers.RecvExReqDlmId);
        uint step = ParcelDbHandlers.U32(payload, ParcelDbHandlers.RecvExReqStep);
        uint ownerDbId = ParcelDbHandlers.U32(payload, ParcelDbHandlers.RecvExReqOwnerDbId);

        if (payload.Length < ParcelDbHandlers.RecvExRequestSize || _store is null)
        {
            _log.LogWarning("SDB_RECV_PARCEL_EX: {Len} B payload or no store - refusing", payload.Length);
            link.SendFrame(DBS_RECV_PARCEL_EX,
                ParcelDbHandlers.BuildDbsRecvParcelEx(null, null, dlmId, step, noParcel: 0, ok: false));
            return true;
        }

        // ---- T202, the live bug ---------------------------------------------------------
        // "Receive all" is this opcode, not SDB_RECV_PARCEL, and it is two-step exactly like
        // its single-parcel sibling (Handler_SDB_RECV_PARCEL_EX, Arb_part_071.c:15981,
        // branches on Step at frame +0x1a). We were ignoring Step: step 1 was answered with
        // ParcelCount 0 and an empty list - so World concluded there was nothing to collect and
        // never sent step 2, the only frame that carries the insert atoms - while the same pass
        // already marked every parcel collected and paid the gold. Parcel consumed, item never
        // created (cap_mail1 21483/21484: 7 parcels claimed, 0 atoms, and no second 0x277d).
        if (step <= ParcelStepRead) return RecvParcelExRead(link, payload, dlmId, step, ownerDbId);

        var (atoms, parsed) = WarehouseHandlers.CloneAtomsWithIds(
            payload, ParcelDbHandlers.RecvExReqRefB, ParcelDbHandlers.RecvExRequestSize, _store.NextItemId);
        var applied = WarehouseHandlers.Apply(_store, parsed, _store.NextItemId, _log);

        // Which parcels this commit is for: the op-37 markers name them one each (T170), and the
        // request's own id chain is the fallback for a commit that carries none.
        var ids = ParcelDbHandlers.ParcelIdsFromAtoms(atoms);
        if (ids.Count == 0)
            ids = ParcelDbHandlers.ReadParcelIdChain(payload,
                ParcelDbHandlers.RecvExReqRefA, ParcelDbHandlers.RecvExReqRefA + 4);
        if (ids.Count == 0)
            ids = _store.GetParcelsFor((int)ownerDbId).Where(r => !r.IsRecved).Select(r => r.ParcelId).ToList();

        uint claimed = 0, removed = 0;
        long gold = 0;
        // T234b: "Receive all" names one owner, but the id chain is World's and a commit could in
        // principle carry parcels of more than one receiver - so the badge goes to whoever was
        // actually touched, plus the request's own owner.
        var badge = new HashSet<int> { (int)ownerDbId };
        foreach (int id in ids)
        {
            var row = _store.GetParcel(id);
            if (row is null || row.IsRecved) continue;
            badge.Add(row.ReceiverDbId);
            gold += ClaimParcelMoney(row, applied);
            _store.SetParcelRecved(row.ParcelId, row.ReceiverDbId);
            claimed++;
            // T234. "Receive all" is where the reward mail piles up, so this is the collect that
            // matters: claim it, then drop the row rather than re-listing it as received. The
            // gold and the atoms are already applied, so the row has nothing left to carry.
            if (ParcelDbHandlers.ShouldDeleteOnCollect(row) && _store.DeleteParcel(row.ParcelId)) removed++;
            // Only one parcel of a receive-all can be the one the atoms carried money for; the
            // rest are credited from their own row.
            applied = applied with { CharacterMoneyDelta = 0 };
        }

        _log.LogInformation(
            "SDB_RECV_PARCEL_EX: owner {Owner} step {Step} -> {N} parcel(s) claimed, {Rem} removed, {Ins} inserted, {Chg} amount, {Gold} gold",
            ownerDbId, step, claimed, removed, applied.Inserted, applied.AmountChanged, gold);
        link.SendFrame(DBS_RECV_PARCEL_EX,
            ParcelDbHandlers.BuildDbsRecvParcelEx(null, atoms, dlmId, step, noParcel: claimed, ok: true));
        // T234b, Arb_part_072.c:6733. This is the one the player notices: Receive all claims
        // without reading, so before T234 the rows kept is_read = 0 and the badge stayed on its
        // old count forever; now the rows are gone and the badge has to follow them down.
        foreach (int who in badge) PushParcelBadge(who, "a receive-all");
        return true;
    }

    /// <summary>
    /// T202. <c>SDB_RECV_PARCEL_EX</c> step 1: <b>enumerate, mutate nothing</b>. The reply carries
    /// one chain node per collectable parcel, each followed by that parcel's FULL 0xdd8 record -
    /// the attachment slots at +0xd8 + i*0x1b0 are what World reads to build the step-2 atoms -
    /// plus the total at <c>ParcelCount</c>, which is the field World tests before it bothers with
    /// a step 2.
    ///
    /// <para><c>IsAllParcel</c> means every uncollected parcel of the owner; otherwise the request
    /// names them in its own id chain. An already-collected row is skipped, which is what the real
    /// enumerator does with a record whose status is 2 (Arb_part_072.c:6028).</para>
    ///
    /// <para><b>One deliberate deviation.</b> The real writer pages at ten records per frame and
    /// sends the pages back to back; no capture holds the real Arbiter answering this opcode at
    /// all, so rather than repeat a DlmId on a second frame - the one mistake that head-blocks a
    /// user for the life of the World process - this sends a single page and logs the rest. The
    /// player presses the button again for them.</para>
    /// </summary>
    private bool RecvParcelExRead(WorldLink link, byte[] payload, uint dlmId, uint step, uint ownerDbId)
    {
        bool isAll = ParcelDbHandlers.U8(payload, ParcelDbHandlers.RecvExReqIsAllParcel) != 0;
        var rows = new List<CharacterStore.ParcelRow>();
        if (isAll)
        {
            foreach (var row in _store!.GetParcelsFor((int)ownerDbId))
                if (!row.IsRecved) rows.Add(row);
        }
        else
        {
            foreach (int id in ParcelDbHandlers.ReadParcelIdChain(payload,
                         ParcelDbHandlers.RecvExReqRefA, ParcelDbHandlers.RecvExReqRefA + 4))
                if (_store!.GetParcel(id) is { IsRecved: false } row) rows.Add(row);
        }

        int page = Math.Min(rows.Count, ParcelDbHandlers.RecvExRecordsPerFrame);
        var records = new List<byte[]>(page);
        for (int i = 0; i < page; i++) records.Add(ParcelDbHandlers.FullParcelRecord(_store!, rows[i]));
        var (chain, count, head) = ParcelDbHandlers.BuildRecvParcelExRecordList(records);

        if (rows.Count > page)
            _log.LogWarning("SDB_RECV_PARCEL_EX: owner {Owner} has {N} collectable parcel(s); serving {Page} this pass",
                ownerDbId, rows.Count, page);
        _log.LogInformation(
            "SDB_RECV_PARCEL_EX: owner {Owner} step {Step} all={All} -> {N} record(s), nothing claimed yet",
            ownerDbId, step, isAll, count);
        link.SendFrame(DBS_RECV_PARCEL_EX, ParcelDbHandlers.BuildDbsRecvParcelEx(
            count, head, chain, null, dlmId, step, noParcel: (uint)rows.Count, ok: true));
        return true;
    }

    /// <summary>
    /// T65: pay a claimed parcel's attached gold onto the receiver's character row.
    ///
    /// <para><c>parcels.money</c> is the Arbiter's own column - it is set from the ParcelData
    /// World sent with SDB_MAKE_PARCEL and World never sees it again, so nothing credited it and
    /// attached gold simply vanished when the mail was opened. The guard is
    /// <paramref name="applied"/>: if the request's transaction list already carried a
    /// TS_CHANGE_MONEY atom (op 9) then World is doing the crediting and we must not do it
    /// twice. Already-claimed parcels pay nothing.</para>
    /// </summary>
    private long ClaimParcelMoney(CharacterStore.ParcelRow? row, WarehouseHandlers.ApplyResult applied)
    {
        if (row is null || row.IsRecved || row.Money <= 0) return 0;
        if (applied.CharacterMoneyDelta != 0) return 0;
        if (_store is null || row.ReceiverDbId <= 0) return 0;
        _store.AddCharacterMoney(row.ReceiverDbId, row.Money);
        return row.Money;
    }

    /// <summary>
    /// T234b. Refresh one character's mail badge after a parcel moved.
    ///
    /// <para>World caches the counts in its own <c>ParcelManager</c>, so a row we add or drop
    /// behind its back leaves the client's badge on the old number until the next login. The real
    /// Arbiter never relies on World for this: it pushes
    /// <c>S_PARCEL_READ_RECV_STATUS</c> itself from every <c>SDB_*</c> handler that touches a
    /// parcel - see <see cref="Handlers.ParcelHandlers.PushReadRecvStatus"/> for the six call
    /// sites in the decompile. We sent none of them.</para>
    ///
    /// <para>One deliberate difference: <c>Handler_SDB_DELETE_PARCEL</c> pushes BEFORE its
    /// <c>DBS_</c> reply and we push after, uniformly. The badge goes to the client socket and
    /// the reply to the World link, so nothing observes the order - and an unanswered per-user DB
    /// request head-blocks that user for the life of the World process (HANDOFF.md section 1),
    /// which is reason enough to answer World first and always.</para>
    /// </summary>
    private void PushParcelBadge(int receiverDbId, string after)
    {
        if (receiverDbId <= 0) return;
        if (Handlers.ParcelHandlers.PushReadRecvStatus(receiverDbId))
            _log.LogInformation("S_PARCEL_READ_RECV_STATUS: {User}'s mail badge refreshed after {After} (T234b)",
                receiverDbId, after);
    }

    /// <summary>SDB_RETURN_PARCEL (0x2781) -> DBS_RETURN_PARCEL (0x2782): sender and receiver
    /// swap and the mail goes back unread.</summary>
    private bool OnReturnParcel(WorldLink link, byte[] payload)
    {
        uint dlmId = ParcelDbHandlers.U32(payload, ParcelDbHandlers.ReturnReqDlmId);
        uint parcelId = ParcelDbHandlers.U32(payload, ParcelDbHandlers.ReturnReqParcelId);

        bool ok = false;
        CharacterStore.ParcelRow? row = null;
        if (payload.Length >= ParcelDbHandlers.ReturnRequestSize && _store is not null)
        {
            row = _store.GetParcel((int)parcelId);
            ok = row is not null && _store.ReturnParcel((int)parcelId, row.ReceiverDbId);
        }
        _log.LogInformation("SDB_RETURN_PARCEL: parcel {Id} -> {Ok}", parcelId, ok);
        link.SendFrame(DBS_RETURN_PARCEL, ParcelDbHandlers.BuildDbsDlmAck(dlmId, ok));
        // T234b, Arb_part_071.c:16213 and :16219 - a return moves the row between two inboxes, so
        // the real Arbiter pushes to BOTH the old receiver and the old sender.
        if (ok && row is not null)
        {
            PushParcelBadge(row.ReceiverDbId, "a parcel returned to its sender");
            PushParcelBadge(row.SenderDbId, "a parcel returned to them");
        }
        return true;
    }

    /// <summary>
    /// SDB_DELETE_PARCEL (0x2811) -> DBS_DELETE_PARCEL (0x2812). The request carries a list of
    /// parcel ids as a binary ref; the real handler walks it as u32s.
    /// </summary>
    private bool OnDeleteParcel(WorldLink link, byte[] payload)
    {
        uint dlmId = ParcelDbHandlers.U32(payload, ParcelDbHandlers.DeleteReqDlmId);
        uint userDbId = ParcelDbHandlers.U32(payload, ParcelDbHandlers.DeleteReqUserDbId);
        bool isSendParcel = ParcelDbHandlers.U8(payload, ParcelDbHandlers.DeleteReqIsSendParcel) != 0;
        var list = ParcelDbHandlers.Ref(payload, ParcelDbHandlers.DeleteReqDelListRef);

        int deleted = 0;
        if (_store is not null)
        {
            for (int at = 0; at + 4 <= list.Length; at += 4)
                if (_store.DeleteParcel((int)BitConverter.ToUInt32(list, at))) deleted++;
        }
        _log.LogInformation("SDB_DELETE_PARCEL: {N} of {M} parcel(s) deleted for user {User}, sent box {Sent}",
            deleted, list.Length / 4, userDbId, isSendParcel);
        link.SendFrame(DBS_DELETE_PARCEL, ParcelDbHandlers.BuildDbsDlmAck(dlmId, ok: true));
        // T234b, Arb_part_071.c:15231. The real handler's guard is exactly this: the push happens
        // only for an INBOX delete, because the Sent box has no badge. Deleting by hand left the
        // badge stale here too, so this is the same bug as the delete-on-collect one.
        if (deleted > 0 && !isSendParcel) PushParcelBadge((int)userDbId, "a mailbox delete");
        return true;
    }

    // =======================================================================================
    // T51: the guild boot load. status/GUILD-DESIGN.md sections 4.3 and 11.
    // =======================================================================================

    /// <summary>
    /// SDB_INIT_GUILD (0x27CF, zero-length) -&gt; the whole guild mirror, from the `guilds` table.
    ///
    /// <para>Per guild: DBS_INIT_GUILD_DATA (0x27ED, Success = 1) -&gt; DBS_INIT_GUILD_GROUP
    /// (0x27D0) -&gt; DBS_INIT_GUILD_MEMBER (0x27D1, batched) -&gt; DBS_INIT_GUILD_PERK_LIST
    /// (0x27D2) -&gt; DBS_LOAD_GUILD_COMPLETE (0x27D3). Then one DBS_INIT_GUILD_DATA with
    /// Success = 0, which is the terminator World waits for. With no guilds the whole sequence
    /// collapses to that single terminator.</para>
    ///
    /// <para>This is what retires the replay entry for 0x27ED. The captured frame carries two
    /// bytes of the real Arbiter's uninitialised stack (0xB379 inside GuildData at blob 0x024A),
    /// which TeraSharp re-sent on every boot; <c>GuildWiring</c> writes zeros there. World never
    /// reads those bytes, so this is hygiene rather than a fix - the real win is that the answer
    /// now reflects guilds that actually exist.</para>
    /// </summary>
    private bool OnInitGuild(WorldLink link)
    {
        var frames = GuildWiring.BuildInitGuildLoad(_store);
        foreach (var (op, body) in frames) link.SendFrame(op, body);
        _log.LogInformation("SDB_INIT_GUILD: sent {N} frame(s) for {G} guild(s)",
            frames.Count, _store?.GetAllGuilds().Count ?? 0);
        return true;
    }

    // =======================================================================================
    // T55: the broker. status/BROKER-DESIGN.md sections 3 and 8.
    // =======================================================================================

    /// <summary>
    /// The five DlmId-carrying <c>SDB_TRADE_BROKER_*</c> requests, answered with the empty
    /// refusal form. One handler for all five: they differ only in where the DlmId and Step sit,
    /// and <see cref="BrokerPackets"/> knows that.
    ///
    /// <para><b>Why answer at all when there is nothing to sell.</b> Each of these is a per-user
    /// DLMItem. World holds the character's DB queue until a reply carrying that request's own
    /// DlmId arrives; <c>DLMExistManager::Find</c> matches on the id, so a reply with the wrong
    /// id is no better than none. Before T55 the broker window's first request got neither - no
    /// handler, and no capture for the replay table - and the character stopped for the life of
    /// the World process (status/HANDOFF.md section 1).</para>
    ///
    /// <para><b>Why Success = 0.</b> There is no listings table yet (BROKER-DESIGN.md section 7
    /// - it waits for a capture), so every one of these is a refusal. A refusal is a normal
    /// answer on this path: the item stays where it was and the client shows the failure. The
    /// wrong thing would be <c>Success = 1</c>, which tells World an item moved.</para>
    ///
    /// <para><b>Step is echoed, never invented.</b> Four of the five carry a multi-stage commit
    /// step whose values were not traced. Echoing the one we were given is the only safe
    /// answer.</para>
    /// </summary>
    private bool OnTradeBrokerRequest(WorldLink link, ushort op, byte[] payload)
    {
        uint dlmId = 0;
        int step = 0;
        int ownerDbId = 0;
        int tradeId = 0;
        bool parsed = true;

        switch (op)
        {
            case SDB_TRADE_BROKER_REGISTER_ITEM:
            {
                var r = BrokerPackets.ParseSdbRegisterItem(payload);
                if (r == null) { parsed = false; break; }
                dlmId = r.Value.dlmId; ownerDbId = r.Value.ownerDbId;
                break;
            }
            case SDB_TRADE_BROKER_UNREGISTER_ITEM:
            {
                var r = BrokerPackets.ParseSdbUnregisterItem(payload);
                if (r == null) { parsed = false; break; }
                dlmId = r.Value.dlmId; ownerDbId = r.Value.ownerDbId; step = r.Value.step;
                tradeId = r.Value.tradeId;
                break;
            }
            case SDB_TRADE_BROKER_CALC_SOLD_ITEM:
            case SDB_TRADE_BROKER_CALC_BOUGHT_ITEM:
            {
                var r = BrokerPackets.ParseSdbCalcItem(op, payload);
                if (r == null) { parsed = false; break; }
                dlmId = r.Value.dlmId; ownerDbId = r.Value.ownerDbId; step = r.Value.step;
                break;
            }
            default:   // SDB_TRADE_BROKER_BUY_IT_NOW
            {
                var r = BrokerPackets.ParseSdbBuyItNow(payload);
                if (r == null) { parsed = false; break; }
                dlmId = r.Value.dlmId; ownerDbId = r.Value.ownerDbId; step = r.Value.step;
                tradeId = r.Value.tradeId;
                break;
            }
        }

        if (!parsed)
        {
            // A frame shorter than the handler's own guard. The real Arbiter kills the World link
            // over this; we do not, because a malformed frame from a fuzzer must not take the
            // server down (status/SECURITY-AUDIT.md). Returning true still consumes it, which is
            // right: the replay table has no answer for it either.
            _log.LogWarning("{Name}: {Len}-byte frame is shorter than the handler's guard - dropped",
                DbProxyOpcodeNames.Describe(op), payload.Length + 6);
            return true;
        }

        if (_store is null)
        {
            var none = BrokerPackets.BuildEmptyRefusal(op, dlmId, step);
            if (none == null) return false;
            link.SendFrame(BrokerPackets.ReplyFor(op), none);
            _log.LogWarning("{Name}: no store open - refused for player {Owner}",
                DbProxyOpcodeNames.Describe(op), ownerDbId);
            return true;
        }

        switch (op)
        {
            case SDB_TRADE_BROKER_REGISTER_ITEM: return BrokerRegister(link, payload, dlmId, ownerDbId);
            case SDB_TRADE_BROKER_UNREGISTER_ITEM: return BrokerUnregister(link, payload, dlmId, step, tradeId);
            case SDB_TRADE_BROKER_BUY_IT_NOW: return BrokerBuyItNow(link, payload, dlmId, step, tradeId, ownerDbId);
            default: return BrokerCalc(link, op, payload, dlmId, step, ownerDbId);
        }
    }

    /// <summary>
    /// SDB_TRADE_BROKER_REGISTER_ITEM (0x2817) -&gt; DBS (0x2818). One pass, no Step: World
    /// already has the item, and the batch does the moving.
    ///
    /// <para>cap_social3.log seq 1514/1746/1767 are the three real registers. The batch is
    /// op 9 (the listing fee off the seller's gold), op 53 (the marker, and the only place the
    /// PRICE arrives), op 44 (the row moves to inven 6, the broker pocket) and an op 2 or an
    /// op 6+11 pair taking the stack out of the bag. Every atom is echoed verbatim.</para>
    /// </summary>
    private bool BrokerRegister(WorldLink link, byte[] payload, uint dlmId, int ownerDbId)
    {
        var (atoms, parsed) = WarehouseHandlers.CloneAtomsWithIds(
            payload, 0, BrokerPackets.RegisterRequestHeader, _store.NextItemId);
        var applied = WarehouseHandlers.Apply(_store, parsed, _store.NextItemId, _log);

        var reg = BrokerPackets.ReadRegisterAtom(atoms);
        int tradeId = 0;
        if (reg != null)
        {
            string seller = _store.GetCharacter(ownerDbId)?.Name ?? string.Empty;
            tradeId = _store.CreateBrokerListing(ownerDbId, seller, reg.Value.ItemDbId,
                reg.Value.TemplateId, reg.Value.Amount, reg.Value.Price);
        }
        else
        {
            _log.LogWarning("SDB_TRADE_BROKER_REGISTER_ITEM: no op-{Op} atom in {N} atom(s) for player "
                + "{Owner} - nothing listed", WarehouseHandlers.TsBrokerRegister, parsed.Count, ownerDbId);
        }

        _log.LogInformation(
            "SDB_TRADE_BROKER_REGISTER_ITEM: player {Owner} listed item {Item} (template {Tpl}) as trade {Id} "
            + "for {Price}; {Mov} row(s) moved, {Del} deleted, {Gold} gold",
            ownerDbId, reg?.ItemDbId ?? 0, reg?.TemplateId ?? 0, tradeId, reg?.Price ?? 0,
            applied.Moved, applied.Deleted, applied.CharacterMoneyDelta);

        link.SendFrame(BrokerPackets.DBS_TRADE_BROKER_REGISTER_ITEM,
            BrokerPackets.BuildDbsRegisterItem(dlmId, success: tradeId != 0, atoms));
        if (tradeId != 0) PushRegisteredItemList(ownerDbId);
        return true;
    }

    /// <summary>
    /// T104. After a register lands, the real Arbiter pushes the seller his Active Listings
    /// UNASKED - <c>cap_social3_client2.log</c> frame 1258 is an
    /// <c>S_TRADE_BROKER_REGISTERED_ITEM_LIST</c> of 74 bytes (one row) sitting directly between
    /// the <c>C_TRADE_BROKER_REGISTER_ITEM</c> at 1257 and the <c>S_INVEN_USERDATA</c> at 1260,
    /// with no <c>C_TRADE_BROKER_REGISTERED_ITEM_LIST</c> anywhere in front of it. Frames 1436
    /// (two rows) and 1457 (three) are the same push after the second and third listing.
    ///
    /// <para>The body is <c>BrokerHandlers.ReplyFor</c>'s, so the push and the tab the client
    /// asks for cannot drift apart.</para>
    ///
    /// <para>Nothing is sent when the seller is not in world - the list is a UI refresh, not
    /// state, and he gets it from the tab on the way back in.</para>
    /// </summary>
    private void PushRegisteredItemList(int sellerDbId)
    {
        if (_store is null || sellerDbId <= 0) return;
        var session = global::TeraSharp.Arbiter.Program.World?.SessionForPlayerId(sellerDbId);
        if (session is null) return;

        var frame = global::TeraSharp.Arbiter.Handlers.BrokerHandlers.ReplyFor(
            BrokerPackets.C_TRADE_BROKER_REGISTERED_ITEM_LIST, _store, sellerDbId);
        if (frame is null) return;
        session.Send(frame);
        _log.LogInformation("S_TRADE_BROKER_REGISTERED_ITEM_LIST: pushed {N} byte(s) to seller {Id}",
            frame.Length, sellerDbId);
    }

    /// <summary>
    /// SDB_TRADE_BROKER_UNREGISTER_ITEM (0x2819) -&gt; DBS (0x281A). Step 1 reads the listing back,
    /// step 2 commits the cancel. cap_social3.log seq 2000..2003: the step-2 reply carries a
    /// TradeData record that is present and entirely ZERO - the listing is gone.
    /// </summary>
    private bool BrokerUnregister(WorldLink link, byte[] payload, uint dlmId, int step, int tradeId)
    {
        var row = _store.GetBrokerListing(tradeId);
        if (step <= BrokerStepRead)
        {
            link.SendFrame(BrokerPackets.DBS_TRADE_BROKER_UNREGISTER_ITEM, BrokerPackets.BuildDbsStepAck(
                dlmId, step, row != null, TradeDataOf(row)));
            return true;
        }

        var (atoms, parsed) = WarehouseHandlers.CloneAtomsWithIds(
            payload, 0, BrokerPackets.UnregisterRequestHeader, _store.NextItemId);
        WarehouseHandlers.Apply(_store, parsed, _store.NextItemId, _log);
        bool ok = row != null
            && _store.SetBrokerListingState(tradeId, CharacterStore.BrokerListed, CharacterStore.BrokerCancelled);

        _log.LogInformation("SDB_TRADE_BROKER_UNREGISTER_ITEM: trade {Id} withdrawn -> {Ok}", tradeId, ok);
        link.SendFrame(BrokerPackets.DBS_TRADE_BROKER_UNREGISTER_ITEM, BrokerPackets.BuildDbsStepAck(
            dlmId, step, ok, BrokerPackets.BuildClearedTradeData(), atoms));
        return true;
    }

    /// <summary>
    /// SDB_TRADE_BROKER_BUY_IT_NOW (0x281F) -&gt; DBS (0x2820). Step 1 reads the listing, step 2
    /// takes the buyer's gold (the op-9 atom) and marks it sold. seq 1880..1883: unlike the
    /// cancel, the step-2 reply still carries the FULL listing record - the buyer's window needs
    /// it to show what was bought.
    /// </summary>
    private bool BrokerBuyItNow(WorldLink link, byte[] payload, uint dlmId, int step, int tradeId, int buyerDbId)
    {
        var row = _store.GetBrokerListing(tradeId);
        if (step <= BrokerStepRead)
        {
            bool onSale = row != null && row.State == CharacterStore.BrokerListed;
            link.SendFrame(BrokerPackets.DBS_TRADE_BROKER_BUY_IT_NOW, BrokerPackets.BuildDbsStepAck(
                dlmId, step, onSale, TradeDataOf(row)));
            return true;
        }

        var (atoms, parsed) = WarehouseHandlers.CloneAtomsWithIds(
            payload, 0, BrokerPackets.BuyItNowRequestHeader, _store.NextItemId);
        WarehouseHandlers.Apply(_store, parsed, _store.NextItemId, _log);
        // SellBrokerListing refuses unless the row is still on sale, so two buyers racing the
        // same TradeId cannot both win even though both got a step-1 yes.
        bool ok = _store.SellBrokerListing(tradeId, buyerDbId);

        _log.LogInformation("SDB_TRADE_BROKER_BUY_IT_NOW: player {Buyer} bought trade {Id} -> {Ok}",
            buyerDbId, tradeId, ok);
        link.SendFrame(BrokerPackets.DBS_TRADE_BROKER_BUY_IT_NOW, BrokerPackets.BuildDbsStepAck(
            dlmId, step, ok, TradeDataOf(row), atoms));
        return true;
    }

    /// <summary>
    /// SDB_TRADE_BROKER_CALC_SOLD_ITEM (0x281B) and _CALC_BOUGHT_ITEM (0x281D) - the two collect
    /// paths, one layout. The TradeIds come in the CalcList ref; both captured calcs carry one.
    ///
    /// <para>seq 1946..1949 and 1904..1907: the step-2 reply carries <b>no</b> TradeData at all -
    /// refA is offset 0x1F with length 0 - unlike the cancel, which sends a zeroed record. The
    /// difference is real and both are pinned.</para>
    /// </summary>
    private bool BrokerCalc(WorldLink link, ushort op, byte[] payload, uint dlmId, int step, int ownerDbId)
    {
        ushort reply = BrokerPackets.ReplyFor(op);
        bool sold = op == SDB_TRADE_BROKER_CALC_SOLD_ITEM;
        var ids = BrokerPackets.ParseCalcList(payload);
        var row = ids.Count > 0 ? _store.GetBrokerListing(ids[0]) : null;

        if (step <= BrokerStepRead)
        {
            // The CalcItemList form, not a plain TradeData: the same record with the settlement
            // block stamped on. CalcState is the only field the seller's and the buyer's copies
            // disagree on (3 vs 2).
            byte[]? rec = TradeDataOf(row);
            if (rec != null && row != null)
            {
                DateTime.TryParse(row.SoldAt, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out var soldAt);
                rec = BrokerPackets.BuildCalcItemData(rec,
                    sold ? BrokerPackets.CalcStateSold : BrokerPackets.CalcStateBought,
                    row.BuyerDbId, row.Price, soldAt);
            }
            link.SendFrame(reply, BrokerPackets.BuildDbsStepAck(dlmId, step, row != null, rec));
            return true;
        }

        var (atoms, parsed) = WarehouseHandlers.CloneAtomsWithIds(
            payload, 8, BrokerPackets.CalcRequestHeader, _store.NextItemId);
        WarehouseHandlers.Apply(_store, parsed, _store.NextItemId, _log);

        int done = 0;
        foreach (int id in ids)
        {
            if (_store.SetBrokerListingState(id, CharacterStore.BrokerSold,
                    sold ? CharacterStore.BrokerSellerPaid : CharacterStore.BrokerBuyerCollected)) done++;
        }

        // T74: Success is NOT "how many rows moved". A row that was already collected is not a
        // failure - the atoms in this request have been applied either way - and answering 0 made
        // World ask again, which is what produced the live "collected 1 of 2" then "0 of 2" loop.
        // The captured step-2 reply carries Success 1 (seq 1907, 1949), so a well-formed commit
        // gets Success 1 and the retry stops.
        bool ok = ids.Count > 0;
        if (done < ids.Count)
            _log.LogInformation("{Name}: {N} of {M} trade(s) were still uncollected for player {Owner}; "
                + "the rest had already been taken", DbProxyOpcodeNames.Describe(op), done, ids.Count, ownerDbId);
        else
            _log.LogInformation("{Name}: player {Owner} collected {N} trade(s)",
                DbProxyOpcodeNames.Describe(op), ownerDbId, done);
        link.SendFrame(reply, BrokerPackets.BuildDbsStepAck(dlmId, step, ok, refA: null, atoms: atoms));
        return true;
    }

    /// <summary>Step 1 is "read me the listing"; anything above it is the commit pass.</summary>
    public const int BrokerStepRead = 1;

    /// <summary>The same thing for SDB_RECV_PARCEL, which turns out to share the shape: step 1
    /// reads the parcel, step 2 arrives with the attachment atoms and commits.</summary>
    public const uint ParcelStepRead = 1;

    /// <summary>A listing as its 0x188-byte TradeData record, or null for no row.</summary>
    private static byte[]? TradeDataOf(CharacterStore.BrokerListingRow? row)
    {
        if (row == null) return null;
        DateTime.TryParse(row.RegisteredAt, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out var when);
        return BrokerPackets.BuildTradeData(row.TradeId, row.SellerDbId, row.SellerName,
            row.ItemDbId, row.TemplateId, row.Amount, row.Price, when);
    }
}
