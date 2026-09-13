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
public sealed class DbProxyHandlers
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
    public const ushort SDB_SAVE_2768 = 0x2768; public const ushort DBS_SAVE_2769 = 0x2769; // reqId @16

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

    // SA_LEARN_ALL_CREST_ACQUIRABLE (0x1463) -> AS_LEARN_ALL_CREST_ACQUIRABLE (0x1464).
    // World sends it at first enter-world for classes that have level-1 crests (a warrior does, the
    // captured valkyrie did not - hence never seen in a capture). Per-user DLM item: unanswered = no
    // spawn. Handler_SA_LEARN_ALL_CREST_ACQUIRABLE (Arb_part_062.c:8760):
    //   req frame: [6]u32 count [10]u32 listOff [14]u64 gameId [22]u32 reqId, entries of 16 B at listOff:
    //              [u32 thisOff][u32 nextOff][i32 crestId][i32 value] (frame-relative offsets, 0 = end)
    //   rsp frame: [6]u32 count [10]u32 firstOff [14]u32 reqId [18]u8 ok, then the LEARNED entries in
    //              the same 16-B shape (User::LearnAllCrest output). We learn everything requested.
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
    public const ushort SDB_LOAD_WORLD_EVENT = 0x27B3;           // -> 0x27B4: [u32 reqId][u8 ok] (5B)
    public const ushort SDB_LOAD_FRIEND_INFO = 0x2910;           // -> 0x2911: [u8 ok][u32 reqId] (5B)

    // --- Post-spawn per-user DB items (lobby_tap.log 02:52:07, right after SpawnComplete) ---
    // Real order: 0x2736 -> 0x2737, 0x27CB x2 -> 0x27CC, 0x2930 -> 0x2931, 0x27B3 -> 0x27B4.
    // 0x2736 has no replay entry (its captured 0x2737 was attributed to 0x15AE), so it was
    // never answered with a live id and head-blocked the user from spawn onward.
    public const ushort SDB_END_START_QUEST_LIST = 0x2736; public const ushort DBS_END_START_QUEST_LIST = 0x2737; // req [u32 reqId]; rsp [reqId][ok=1]
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
    public const ushort SDB_LOAD_2975 = 0x2975;            // -> 0x2976: 16 zeros + [reqId][ok] + 20 zeros
    public const ushort SDB_LOAD_2986 = 0x2986;            // -> 0x2987: 32 zeros + [reqId][ok] + trailing (16B request)
    public const ushort SDB_LOAD_290C = 0x290C;            // -> pushes 0x15B1 + 0x2847 + 0x1440 + 0x143E, then 0x290D

    // --- Remaining login-time SDB_* (static data from capture, reqId patched at runtime) ---
    public const ushort SDB_TUTORIAL_SIMPLE_TIP = 0x2872;   // -> 0x2873 (45B, reqId@8)
    public const ushort SDB_REPUTATION_LIST = 0x288F;       // -> 0x2890 (65B, reqId@9)
    public const ushort SDB_LOAD_293A = 0x293A;             // -> 0x293B (46B, reqId@8)
    public const ushort SDB_FATIGABILITY_LIST = 0x2908;     // -> 0x2909 (45B, reqId@9)
    public const ushort SDB_SEREN_GUIDE = 0x2942;           // -> 0x2943 (65B, reqId@8)
    public const ushort SDB_EP_PERK = 0x27B9;               // -> 0x27BA (113B, reqId@8)
    // --- Quests (T17). Layouts and the capture evidence: status/QUEST-DESIGN.md ---
    //
    // SDB_LOAD_QUEST_LIST (0x272C) -> DBS_LOAD_QUEST_LIST (0x272D)
    //   req  [0] u32 reqId  [4] u32 playerId
    //   rsp  53-byte header: six [u32 offset][u32 length] pairs, then [48] u8 ok, [49] u32 reqId
    //        list 0  vector<QuestData>                 80 B each   <- what we serve
    //        list 1  vector<QuestData>                             <- layout unverified
    //        list 2  vector<int>                                   <- layout unverified
    //        list 3  vector<DailyQuestSeed>            68 B each   <- GLOBAL, kept empty on purpose:
    //                World then generates the seeds itself and sends the 17 0x2899 items we answer
    //        list 4  vector<DailyQuestExCompleteCount>
    //        list 5  a fixed 20-byte [u32][DateTime] trailer
    //   Offsets are frame-relative, so an empty reply has every offset at 59 (= 6 + 53).
    //   Ground truth for the empty case: cap_newchar.log seq 342, a 73-byte payload.
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
    /// List 5 of 0x272D: [u32 0][DateTime]. The "never" (1970-01-01) form the real Arbiter sent
    /// for the brand-new character in cap_newchar.log seq 342. dob's relog carries a real date
    /// there; we have nothing to put in it, and World accepted "never" for a character that had
    /// not played.
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
    public const ushort SDB_SET_QUEST_INFO = 0x272E; public const ushort DBS_SET_QUEST_INFO = 0x272F;
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

    // --- SDB_UPDATE_REPUTATION_INFO (0x2891) -> DBS_UPDATE_REPUTATION_INFO (0x2892) ---
    // cap_newchar.log seq 413 -> 417, 78 B -> 11 B.
    // Handler_SDB_UPDATE_REPUTATION_INFO (Arb_part_064.c:13105) needs frame >= 0x1a and reads
    //   [0] u32 recordOffset  [4] u32 recordLength (52)  [8] u32 reqId  [12] u32 playerId
    //   [16] u32 op
    // op 1 -> ReputationList::Insert, op 2 or 4 -> ReputationList::Update; any other op leaves
    // ok = 0. The writer emits u8 ok FIRST, then u32 reqId - the ONLY reply in this batch with
    // that ordering (capture: 01 2B 00 00 00).
    public const ushort SDB_UPDATE_REPUTATION_INFO = 0x2891; public const ushort DBS_UPDATE_REPUTATION_INFO = 0x2892;

    // --- SDB_ADD_TUTORIAL_SIMPLE_TIP (0x286E) -> DBS_ADD_TUTORIAL_SIMPLE_TIP (0x286F) ---
    // cap_newchar.log seq 719/765/864/911, 18 B -> 11 B each.
    // Handler_SDB_ADD_TUTORIAL_SIMPLE_TIP (Arb_part_063.c:36) needs frame >= 0x12 and reads
    //   [0] u32 reqId  [4] u32 playerId  [8] u32 tipId
    // Reply: [u32 reqId][u8 ok] (capture: 51 00 00 00 01).
    public const ushort SDB_ADD_TUTORIAL_SIMPLE_TIP = 0x286E; public const ushort DBS_ADD_TUTORIAL_SIMPLE_TIP = 0x286F;

    // --- SDB_UPDATE_SEREN_GUIDE_INFO (0x2944) -> DBS_UPDATE_SEREN_GUIDE_INFO (0x2945) ---
    // cap_newchar.log seq 634 -> 635 and 2713 -> 2716, 22 B -> 15 B.
    // Handler_SDB_UPDATE_SEREN_GUIDE_INFO (Arb_part_064.c:13189) needs frame >= 0x16 and reads
    //   [0] u32 reqId  [4] u32 playerId  [8] u32 guideId  [12] u32 value
    // Reply: [u32 reqId][u32 playerId][u8 ok] (capture: 4B 00 00 00 02 00 00 00 01).
    // NOTE: the real Arbiter sends NOTHING when its update fails - which would head-block the
    // user. We always answer; ok = 1 matches every captured reply.
    public const ushort SDB_UPDATE_SEREN_GUIDE_INFO = 0x2944; public const ushort DBS_UPDATE_SEREN_GUIDE_INFO = 0x2945;

    // --- SDB_UPDATE_USER_DAILY_EVENT_COUNT (0x293C) -> DBS (0x293D) ---
    // cap_newchar.log seq 505 -> 507, 58 B -> 11 B.
    // Handler_SDB_UPDATE_USER_DAILY_EVENT_COUNT (Arb_part_064.c:14404) needs frame >= 0x26 and
    // reads [0] u32 recordOffset [4] u32 recordLength (20) [8] u32 reqId [12] u32 playerId
    //       [16] i64 flag  [24] u64 timestamp.  Reply: [u32 reqId][u8 ok] - reqId at payload[8].
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

    public DbProxyHandlers(CharacterStore store, ILogger log)
    {
        _store = store;
        _log = log;
    }

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
            case SDB_USER_ENTERWORLD:
            case SDB_UPDATE_USER_DATA:
            case SDB_SAVE_27FA:
            case SDB_SAVE_2924:
            case SDB_SAVE_2768:
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
            case SDB_END_START_QUEST_LIST: // 0x2737 = [reqId][01]
            case SDB_USER_LOAD_INVENTORY:  // 0x27A3 + 0x27A4: starter inventory for every character except the captured one
            case SDB_LOAD_2930:            // 0x2931 = [reqId][01][00]   (capture: 82 00 00 00 01 00)
            case SDB_LOAD_WORLD_EVENT:     // 0x27B4 = [reqId][01]       (capture: 83 00 00 00 01)
            case SA_CLEAR_BATTLE_FIELD_ENTER_COUNT: // 0x1563 = [01][reqId@8]  (decompile Arb_part_062.c:4769)
            case SA_LEARN_ALL_CREST_ACQUIRABLE:     // 0x1464 = learned-crest list (all of them)
            case SDB_UPDATE_USER_ACTPOINT:          // 0x297C = [reqId][01]
            case SA_REQUEST_ENTER_DUNGEON:          // 0x13BF (zone change step 1)
            case SA_RESPONSE_ENTER_DUNGEON:         // 0x13C1 (zone change step 2)
            case SDB_LOAD_2869:          // 0x15E0 push + 0x286A, both carrying the live reset time
            case AS_PROMOTION_LIST_REQ:  // 0x147D -> 0x1484 + 24 x 0x147E (timestamps = now) + 0x1480
            // --- T15: the per-user writes World sends during play. Each one is a DLM item; a
            // missing reply head-blocks the user's queue for the life of the World process. ---
            case SDB_SET_QUEST_INFO:              // 0x272F = echo + ok + allocated quest row id
            case SDB_USER_LEARN_SKILL:            // 0x278F = echo of the fee atoms + empty skill-period list
            case SDB_ACCOMPLISH_USER_ACHIEVEMENT: // 0x2803 = the newly-accomplished records echoed
            case SDB_UPDATE_REPUTATION_INFO:      // 0x2892 = [ok][reqId]  (ok-first, the odd one out)
            case SDB_ADD_TUTORIAL_SIMPLE_TIP:     // 0x286F = [reqId][ok]
            case SDB_UPDATE_SEREN_GUIDE_INFO:     // 0x2945 = [reqId][playerId][ok]
            case SDB_UPDATE_USER_DAILY_EVENT_COUNT: // 0x293D = [reqId][ok], reqId at payload[8]
            case SDB_UPDATE_GET_EXTRA_REWARD:     // 0x293F = [reqId][ok]
                return true;
            default:
                return false;        // -> replay table
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
            case SDB_USER_ENTERWORLD: return OnUserEnterWorld(link, payload);
            case SDB_UPDATE_USER_DATA: return OnUpdateUserData(link, payload);
            case SDB_USER_LOAD_INVENTORY: return OnLoadInventory(link, payload);
            case AS_PROMOTION_LIST_REQ: return OnPromotionListRequest(link);
            case SDB_UPDATE_EXP_LEVEL: return OnUpdateExpLevel(link, payload);
            case SDB_SET_QUEST_INFO: return OnSetQuestInfo(link, payload);

            // --- Logout save sequence (reqId echoed from the live request) ---
            case SDB_SAVE_27FA: link.SendFrame(DBS_SAVE_27FB, BuildReqIdAck(payload, 280)); return true;
            case SDB_SAVE_2924: link.SendFrame(DBS_SAVE_2925, BuildReqIdAck(payload, 8)); return true;
            case SDB_SAVE_2768: return OnItemSingle(link, payload);
            case SDB_SAVE_2936: link.SendFrame(DBS_SAVE_2937, BuildDbs2937(payload)); return true;
            case SDB_DAILY_QUEST: link.SendFrame(DBS_DAILY_QUEST, BuildReqIdAck(payload, 16)); return true;
            case SDB_DAILY_QUEST_SEED: link.SendFrame(DBS_DAILY_QUEST_SEED, BuildReqIdAck(payload, 8)); return true;

            // --- T15: per-user writes during play (all echo the LIVE reqId) ---
            case SDB_SET_QUEST_INFO:              return OnSetQuestInfo(link, payload);
            case SDB_USER_LEARN_SKILL:            return OnUserLearnSkill(link, payload);
            case SDB_ACCOMPLISH_USER_ACHIEVEMENT: return OnAccomplishUserAchievement(link, payload);
            case SDB_UPDATE_REPUTATION_INFO:        link.SendFrame(DBS_UPDATE_REPUTATION_INFO, BuildDbs2892(payload)); return true;
            case SDB_ADD_TUTORIAL_SIMPLE_TIP:       link.SendFrame(DBS_ADD_TUTORIAL_SIMPLE_TIP, BuildReqIdAck(payload, 0)); return true;
            case SDB_UPDATE_SEREN_GUIDE_INFO:       link.SendFrame(DBS_UPDATE_SEREN_GUIDE_INFO, BuildDbs2945(payload)); return true;
            case SDB_UPDATE_USER_DAILY_EVENT_COUNT: link.SendFrame(DBS_UPDATE_USER_DAILY_EVENT_COUNT, BuildReqIdAck(payload, 8)); return true;
            case SDB_UPDATE_GET_EXTRA_REWARD:       link.SendFrame(DBS_UPDATE_GET_EXTRA_REWARD, BuildReqIdAck(payload, 0)); return true;

            // --- Post-spawn (reqId at payload[0] for all three) ---
            case SDB_END_START_QUEST_LIST: link.SendFrame(DBS_END_START_QUEST_LIST, BuildReqIdAck(payload, 0)); return true;
            case SDB_LOAD_2930:            link.SendFrame(DBS_LOAD_2931, Build2931(payload)); return true;
            case SA_CLEAR_BATTLE_FIELD_ENTER_COUNT: link.SendFrame(AS_CLEAR_BATTLE_FIELD_ENTER_COUNT, BuildOkReqId(payload, 8)); return true;
            case SDB_UPDATE_USER_ACTPOINT: link.SendFrame(DBS_UPDATE_USER_ACTPOINT, BuildReqIdAck(payload, 0)); return true;
            case SA_LEARN_ALL_CREST_ACQUIRABLE:
            {
                var r = BuildLearnAllCrest(payload);
                if (r == null) return false;
                _log.LogInformation("SA_LEARN_ALL_CREST_ACQUIRABLE: learned {N} crest(s)", BitConverter.ToUInt32(r, 0));
                link.SendFrame(AS_LEARN_ALL_CREST_ACQUIRABLE, r);
                return true;
            }
            case SA_REQUEST_ENTER_DUNGEON:
            {
                var r = BuildAsRequestEnterDungeon(payload);
                if (r == null) return false;
                _log.LogInformation("SA_REQUEST_ENTER_DUNGEON: player {Pid} -> dungeon/zone {Dg} ({Len} B)",
                    BitConverter.ToUInt32(payload, 32), BitConverter.ToUInt32(payload, 8), payload.Length);
                link.SendFrame(AS_REQUEST_ENTER_DUNGEON, r);
                return true;
            }
            case SA_RESPONSE_ENTER_DUNGEON:
            {
                var r = BuildAsResponseEnterDungeon(payload);
                if (r == null) return false;
                link.SendFrame(AS_RESPONSE_ENTER_DUNGEON, r);
                return true;
            }

            // --- Login-time: empty-list Type 1 [off=19][count=0][reqId][ok=1], reqId at payload[0] ---
            // (0x27A2 inventory is handled by OnLoadInventory above)
            case SDB_LOAD_ITEM_RECIPE:          link.SendFrame((ushort)(op + 1), BuildEmptyListType1(payload, 0)); return true;
            case SDB_LOAD_SKILL_PROF:           link.SendFrame((ushort)(op + 1), BuildEmptyListType1(payload, 0)); return true;
            case SDB_LOAD_TELEPORT_TO_POS_LIST: link.SendFrame((ushort)(op + 1), BuildEmptyListType1(payload, 0)); return true;
            case SDB_LOAD_ACCOUNT_BENEFIT:      link.SendFrame((ushort)(op + 1), BuildEmptyListType1(payload, 0)); return true;
            case SDB_LOAD_LEARNED_SOCIAL:       link.SendFrame((ushort)(op + 1), BuildEmptyListType1(payload, 0)); return true;
            case SDB_LOAD_TOKEN_EXCHANGE:       link.SendFrame((ushort)(op + 1), BuildEmptyListType1(payload, 0)); return true;
            case SDB_LOAD_SKILLPERIOD:          link.SendFrame((ushort)(op + 1), BuildEmptyListType1(payload, 0)); return true;
            case SDB_LOAD_SERVANT_PERIOD:       link.SendFrame((ushort)(op + 1), BuildEmptyListType1(payload, 0)); return true;
            case SDB_LOAD_PASSIVITY_COOLTIME:   link.SendFrame((ushort)(op + 1), BuildEmptyListType1(payload, 0)); return true;
            case SDB_LOAD_ADDITIONAL_REWARD:    link.SendFrame((ushort)(op + 1), BuildEmptyListType1(payload, 0, ok: 0)); return true;

            // --- Login-time: empty-list Type 2 [off=19][count=0][ok=1][reqId], reqId at payload[0] ---
            case SDB_LOAD_PROMOTION_LIST:       link.SendFrame((ushort)(op + 1), BuildEmptyListType2(payload, 0)); return true;
            case SDB_LOAD_PROMOTION_COND_LIST:  link.SendFrame((ushort)(op + 1), BuildEmptyListType2(payload, 0)); return true;
            case SDB_LOAD_BATTLE_FIELD_LIST:    link.SendFrame((ushort)(op + 1), BuildEmptyListType2(payload, 0)); return true;
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
            case SA_LOAD_EXTRAPOINT_DATA:           link.SendFrame(0x1555, BuildExtrapointData(payload)); return true;

            // --- Other login-time handlers ---
            case SDB_LOAD_QUEST_PROGRESS: link.SendFrame(0x2903, BuildQuestProgress(payload)); return true;
            case SDB_LOAD_ACHIEVE_LIST:   link.SendFrame(0x2982, BuildAchieveList(payload)); return true;
            case SDB_LOAD_WORLD_EVENT:    link.SendFrame(0x27B4, BuildReqIdAck(payload, 0)); return true;
            case SDB_LOAD_FRIEND_INFO:    link.SendFrame(0x2911, BuildOkReqId(payload, 0)); return true;

            // --- Remaining login-time: programmatic builders ---
            case SDB_LOAD_2867: link.SendFrame(0x2868, Build2868_ThreeEmptyLists(payload)); return true;
            case SDB_LOAD_2869:
            {
                // The real Arbiter pushes 0x15E0 first, then answers 0x286A, and both carry the
                // SAME reset time (see the comment on SDB_LOAD_2869 above).
                ulong resetTime = (ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                uint pid = payload.Length >= 8 ? BitConverter.ToUInt32(payload, 4) : 0;
                link.SendFrame(AS_REQUEST_DUNGEON_PHASE_USER_RESET, Build15E0(pid, resetTime));
                link.SendFrame(DBS_LOAD_DUNGEON_PHASE_LEVEL, Build286A_EmptyListTimestamp(payload, resetTime));
                return true;
            }
            case SDB_LOAD_2900: link.SendFrame(0x2901, Build2901_TwoEmptyLists(payload)); return true;
            case SDB_LOAD_28B7: link.SendFrame(0x28B6, Build28B6(payload)); return true; // reply opcode is op-1!
            case SDB_LOAD_REFER_A_FRIEND: link.SendFrame(0x28B1, Build28B1_ReferAFriend(payload)); return true;
            case SDB_LOAD_2975: link.SendFrame(0x2976, Build2976(payload)); return true;
            case SDB_LOAD_2986: link.SendFrame(0x2987, Build2987(payload)); return true;
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
            case SDB_TUTORIAL_SIMPLE_TIP: link.SendFrame(0x2873, BuildFromStaticData(DbProxyStaticData.Tutorial, DbProxyStaticData.TutorialReqIdOffset, payload)); return true;
            case SDB_REPUTATION_LIST:     link.SendFrame(0x2890, BuildFromStaticData(DbProxyStaticData.Reputation, DbProxyStaticData.ReputationReqIdOffset, payload)); return true;
            case SDB_LOAD_293A:           link.SendFrame(0x293B, BuildFromStaticData(DbProxyStaticData.Load293B, DbProxyStaticData.Load293BReqIdOffset, payload)); return true;
            case SDB_FATIGABILITY_LIST:   link.SendFrame(0x2909, BuildFromStaticData(DbProxyStaticData.Fatigability, DbProxyStaticData.FatigabilityReqIdOffset, payload)); return true;
            case SDB_SEREN_GUIDE:         link.SendFrame(0x2943, BuildFromStaticData(DbProxyStaticData.SerenGuide, DbProxyStaticData.SerenGuideReqIdOffset, payload)); return true;
            case SDB_EP_PERK:             link.SendFrame(0x27BA, BuildFromStaticData(DbProxyStaticData.EpPerk, DbProxyStaticData.EpPerkReqIdOffset, payload)); return true;
            case SDB_QUEST_LIST:          return OnLoadQuestList(link, payload);
            case SDB_USER_ACHIEVEMENT:    link.SendFrame(0x27F9, BuildFromStaticData(DbProxyStaticData.Achievement, DbProxyStaticData.AchievementReqIdOffset, payload)); return true;

            default:
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

    /// <summary>
    /// AS_LEARN_ALL_CREST_ACQUIRABLE (0x1464): every requested (crestId, value) echoed back as learned.
    /// Payload: [u32 count][u32 firstOff=19][u32 reqId][u8 ok=1] + 16-B entries with frame-relative links.
    /// </summary>
    public static byte[]? BuildLearnAllCrest(byte[] req)
    {
        if (req.Length < 20) return null;
        uint count = BitConverter.ToUInt32(req, 0);
        int listOff = (int)BitConverter.ToUInt32(req, 4);
        uint reqId = BitConverter.ToUInt32(req, 16);
        var entries = new List<(int id, int val)>();
        int off = listOff - 6;
        int guard = 0;
        while (off > 0 && off + 16 <= req.Length && guard++ < 512)
        {
            entries.Add((BitConverter.ToInt32(req, off + 8), BitConverter.ToInt32(req, off + 12)));
            int next = (int)BitConverter.ToUInt32(req, off + 4);
            if (next == 0) break;
            off = next - 6;
        }
        var r = new byte[13 + 16 * entries.Count];
        BitConverter.GetBytes((uint)entries.Count).CopyTo(r, 0);
        BitConverter.GetBytes(entries.Count > 0 ? 19u : 0u).CopyTo(r, 4);
        BitConverter.GetBytes(reqId).CopyTo(r, 8);
        r[12] = 1;
        for (int i = 0; i < entries.Count; i++)
        {
            int p = 13 + 16 * i;
            BitConverter.GetBytes((uint)(19 + 16 * i)).CopyTo(r, p);
            BitConverter.GetBytes(i + 1 < entries.Count ? (uint)(19 + 16 * (i + 1)) : 0u).CopyTo(r, p + 4);
            BitConverter.GetBytes(entries[i].id).CopyTo(r, p + 8);
            BitConverter.GetBytes(entries[i].val).CopyTo(r, p + 12);
        }
        return r;
    }

    /// <summary>DBS 0x2931: [u32 reqId][u8 ok=1][u8 0] — 6 bytes (lobby_tap.log 02:52:07.112: 82 00 00 00 01 00).</summary>
    public static byte[] Build2931(byte[] request)
    {
        uint reqId = request.Length >= 4 ? BitConverter.ToUInt32(request, 0) : 0;
        var r = new byte[6];
        BitConverter.GetBytes(reqId).CopyTo(r, 0);
        r[4] = 1;
        return r;
    }

    /// <summary>
    /// SDB_ITEM_SINGLE (0x2768) -> DBS_ITEM_SINGLE (0x2769). See the constants above.
    /// </summary>
    private bool OnItemSingle(WorldLink link, byte[] payload)
    {
        int declaredA = DeclaredAtomCount(payload, 0), declaredB = DeclaredAtomCount(payload, 8);
        var reply = BuildDbs2769(payload, _store.NextItemId);
        int echoedA = (int)BitConverter.ToUInt32(reply, 4) / ItemAtomSize;
        int echoedB = (int)BitConverter.ToUInt32(reply, 12) / ItemAtomSize;
        uint playerId = payload.Length >= ItemSingleRequestHeader ? BitConverter.ToUInt32(payload, 20) : 0;

        if (echoedA != declaredA || echoedB != declaredB)
            _log.LogWarning(
                "SDB_ITEM_SINGLE: could not echo the atom lists for player {Pid} (declared {DA}/{DB}, echoed {EA}/{EB}, "
                + "payload {Len} B) - World will lose the items it just wrote", playerId, declaredA, declaredB, echoedA, echoedB, payload.Length);
        else if (declaredA + declaredB > 0)
            _log.LogInformation("SDB_ITEM_SINGLE: echoed {N} transaction atom(s) for player {Pid}",
                declaredA + declaredB, playerId);

        link.SendFrame(DBS_SAVE_2769, reply);
        return true;
    }

    // ---- Quests: SDB_SET_QUEST_INFO (0x272E) / SDB_LOAD_QUEST_LIST (0x272C) ----

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
        link.SendFrame(DBS_SET_QUEST_INFO,
            BuildDbs272F(payload, sqlType == QuestSqlTypeInsert ? questDbId : 0, _store.NextItemId));
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
        if (completed.Count > 0)
            _log.LogWarning(
                "SDB_LOAD_QUEST_LIST: player {Pid} has {N} completed quest(s) ({Ids}) STORED BUT NOT SERVED - "
                + "the 0x272D list 1/2 layout is unverified (status/QUEST-DESIGN.md); World will offer them again",
                playerId, completed.Count, string.Join(", ", completed));

        _log.LogInformation("SDB_LOAD_QUEST_LIST: player {Pid} -> {N} active quest(s)", playerId, active.Count);
        link.SendFrame(0x272D, BuildDbs272D(active, reqId));
        return true;
    }

    /// <summary>
    /// DBS_LOAD_QUEST_LIST (0x272D) built from stored rows: list 0 is the active quest records,
    /// lists 1-4 are empty (list 3 deliberately so — World then seeds itself and sends the 17
    /// 0x2899 items we answer), list 5 is the "never" trailer. With no rows this is
    /// byte-identical to cap_newchar.log seq 342.
    /// </summary>
    public static byte[] BuildDbs272D(IReadOnlyList<byte[]> activeQuestRecords, uint reqId)
    {
        ArgumentNullException.ThrowIfNull(activeQuestRecords);
        int listLength = activeQuestRecords.Count * QuestRecordSize;
        var r = new byte[QuestListReplyHeader + listLength + QuestListTrailer.Length];

        uint bodyStart = 6 + QuestListReplyHeader;                 // 59, frame-relative
        uint afterList0 = bodyStart + (uint)listLength;
        BitConverter.GetBytes(bodyStart).CopyTo(r, 0);
        BitConverter.GetBytes((uint)listLength).CopyTo(r, 4);
        for (int slot = 1; slot <= 4; slot++)                      // lists 1-4: empty, same offset
            BitConverter.GetBytes(afterList0).CopyTo(r, slot * 8);
        BitConverter.GetBytes(afterList0).CopyTo(r, 40);           // list 5: the trailer
        BitConverter.GetBytes((uint)QuestListTrailer.Length).CopyTo(r, 44);
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
        QuestListTrailer.CopyTo(r, at);
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
                                        int minStart = ItemSingleRequestHeader)
    {
        if (headerOffset + 8 > request.Length) return Array.Empty<byte>();
        int frameOffset = (int)BitConverter.ToUInt32(request, headerOffset);
        int length = (int)BitConverter.ToUInt32(request, headerOffset + 4);
        if (length <= 0) return Array.Empty<byte>();

        int start = frameOffset - 6;                            // offsets in the frame count the header
        // `length > request.Length - start` rather than `start + length > request.Length`: a
        // garbage offset can be big enough that the sum overflows int and passes the check.
        if (start < minStart || length % ItemAtomSize != 0 || length > request.Length - start)
            return Array.Empty<byte>();

        var atoms = new byte[length];
        Array.Copy(request, start, atoms, 0, length);
        for (int o = 0; o + ItemAtomSize <= atoms.Length; o += ItemAtomSize)
        {
            if (BitConverter.ToUInt32(atoms, o + ItemAtomOpOffset) != TsInsertItem) continue;
            // An insert that already carries an id is World re-stating one it knows; only a 0
            // means "give me one".
            if (BitConverter.ToUInt32(atoms, o + ItemAtomDbIdOffset) != 0) continue;
            BitConverter.GetBytes(allocateItemId()).CopyTo(atoms, o + ItemAtomDbIdOffset);
        }
        return atoms;
    }


    // =====================================================================
    // T15 builders. Every one is verified byte-for-byte against data/cap_t15.bin in the tests.
    // =====================================================================

    /// <summary>
    /// Quest row ids for 0x272F. The real Arbiter returns the identity of the row its INSERT
    /// created; World stores it (DBStartQuestContext::SetQuestDbId) and uses it to address the
    /// row later. We keep no quest table yet - status/PERSISTENCE-MAP.md step 1 - so this is a
    /// process-wide counter. It only has to be distinct within the World process: nothing
    /// persists, so a restart that begins again at 1 collides with nothing. When quests become
    /// real this moves into CharacterStore next to the item-id sequence.
    /// </summary>
    private int _nextQuestDbId;
    private int NextQuestDbId() => Interlocked.Increment(ref _nextQuestDbId);

    /// <summary>SDB_SET_QUEST_INFO (0x272E) -&gt; DBS_SET_QUEST_INFO (0x272F).</summary>
    private bool OnSetQuestInfo(WorldLink link, byte[] payload)
    {
        uint sqlType = payload.Length >= 24 ? BitConverter.ToUInt32(payload, 20) : 0;
        int declaredAtoms = DeclaredAtomCount(payload, 8);
        int questDbId = 0;
        var reply = BuildDbs272F(payload, _store.NextItemId,
            () => { questDbId = NextQuestDbId(); return questDbId; });

        int echoedAtoms = (int)BitConverter.ToUInt32(reply, 12) / ItemAtomSize;
        if (echoedAtoms != declaredAtoms)
            _log.LogWarning("SDB_SET_QUEST_INFO: could not echo the reward atoms (declared {D}, echoed {E}, "
                + "payload {Len} B) - World will lose the items the quest just granted",
                declaredAtoms, echoedAtoms, payload.Length);

        _log.LogInformation("SDB_SET_QUEST_INFO: sqlType {Sql}, {N} reward atom(s){Quest}",
            sqlType, declaredAtoms, questDbId != 0 ? $", quest row id {questDbId}" : "");
        link.SendFrame(DBS_SET_QUEST_INFO, reply);
        return true;
    }

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
        link.SendFrame(DBS_USER_LEARN_SKILL, reply);
        return true;
    }

    /// <summary>SDB_ACCOMPLISH_USER_ACHIEVEMENT (0x2802) -&gt; DBS (0x2803).</summary>
    private bool OnAccomplishUserAchievement(WorldLink link, byte[] payload)
    {
        var reply = BuildDbs2803(payload);
        int n = (int)BitConverter.ToUInt32(reply, 4) / AchievementRecordSize;
        _log.LogInformation("SDB_ACCOMPLISH_USER_ACHIEVEMENT: echoed {N} record(s) as newly accomplished", n);
        link.SendFrame(DBS_ACCOMPLISH_USER_ACHIEVEMENT, reply);
        return true;
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
    /// <para><paramref name="isNewlyAccomplished"/> is called once per 24-byte record; null means
    /// "everything is new", which is what TeraSharp does today because it keeps no achievement
    /// table. Return false for a record the character already has and the reply collapses to the
    /// 19-byte form the real Arbiter sent at cap_newchar.log seq 2606 - that is the hook an
    /// achievement store plugs into.</para>
    /// </summary>
    public static byte[] BuildDbs2803(byte[] request, Func<byte[], bool>? isNewlyAccomplished = null)
    {
        uint reqId = request.Length >= 12 ? BitConverter.ToUInt32(request, 8) : 0;

        var kept = new List<byte[]>();
        if (request.Length >= 8)
        {
            int start = (int)BitConverter.ToUInt32(request, 0) - 6;   // frame-relative
            int len = (int)BitConverter.ToUInt32(request, 4);
            if (start >= AchievementRequestHeader && len > 0 && len % AchievementRecordSize == 0
                && len <= request.Length - start)
            {
                for (int o = start; o + AchievementRecordSize <= start + len; o += AchievementRecordSize)
                {
                    var rec = request[o..(o + AchievementRecordSize)];
                    if (isNewlyAccomplished == null || isNewlyAccomplished(rec)) kept.Add(rec);
                }
            }
        }

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

    /// <summary>[u8 ok=1][u32 reqId] — 5 bytes. Used by 0x2910→0x2911 (SDB_LOAD_FRIEND_INFO).</summary>
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
    /// AS_LOAD_SERVANT_DATA (0x153A): [u32 listOff1=0][u32 listOff2=0][u32 reqId][u8 playerId_low][u8 ok=1][u16 0][u8 0].
    /// Capture frame 261: 17 bytes. No servants → empty reply with reqId echo.
    /// </summary>
    public static byte[] BuildServantData(byte[] request)
    {
        uint reqId = request.Length >= 12 ? BitConverter.ToUInt32(request, 8) : 0;
        byte pid = request.Length >= 16 ? (byte)(BitConverter.ToUInt32(request, 12) & 0xFF) : (byte)0;
        var r = new byte[17];
        // r[0..7] = 0 (two null list offsets)
        BitConverter.GetBytes(reqId).CopyTo(r, 8);
        r[12] = pid;
        r[13] = 1; // ok
        // r[14..16] = 0
        return r;
    }

    /// <summary>
    /// AS_LOAD_SERVANT_ADVENTURE_DATA (0x153C): [u64 0][u32 reqId][u8 ok=1][u32 unk=1][u32 0].
    /// Capture frame 263: 21 bytes. The u32 at [13] is 1 in the capture (default/base entry count).
    /// </summary>
    public static byte[] BuildServantAdventureData(byte[] request)
    {
        uint reqId = request.Length >= 12 ? BitConverter.ToUInt32(request, 8) : 0;
        var r = new byte[21];
        BitConverter.GetBytes(reqId).CopyTo(r, 8);
        r[12] = 1; // ok
        BitConverter.GetBytes(1u).CopyTo(r, 13); // unk=1 (matches capture)
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
    /// AS_LOAD_EXTRAPOINT_DATA (0x1555): [u32 reqId][u8 playerId_low][u8 ok=1][47 zeros] — 53 bytes.
    /// Capture frame 319. SDB shape request (reqId at payload[0]).
    /// </summary>
    public static byte[] BuildExtrapointData(byte[] request)
    {
        uint reqId = request.Length >= 4 ? BitConverter.ToUInt32(request, 0) : 0;
        byte pid = request.Length >= 8 ? (byte)(BitConverter.ToUInt32(request, 4) & 0xFF) : (byte)0;
        var r = new byte[53];
        BitConverter.GetBytes(reqId).CopyTo(r, 0);
        r[4] = pid;
        r[5] = 1; // ok
        // rest zeros (no extra point data)
        return r;
    }

    // ---- Remaining login-time programmatic builders ----

    /// <summary>
    /// 0x2868: three empty lists + [u8 ok=1][u32 reqId] — 29 bytes.
    /// Three [u32 off=35][u32 count=0] headers (35 = 6+29), then ok + reqId.
    /// </summary>
    public static byte[] Build2868_ThreeEmptyLists(byte[] request)
    {
        uint reqId = request.Length >= 4 ? BitConverter.ToUInt32(request, 0) : 0;
        var r = new byte[29];
        const uint off = 35; // 6 + 29
        BitConverter.GetBytes(off).CopyTo(r, 0);
        BitConverter.GetBytes(off).CopyTo(r, 8);
        BitConverter.GetBytes(off).CopyTo(r, 16);
        r[24] = 1; // ok
        BitConverter.GetBytes(reqId).CopyTo(r, 25);
        return r;
    }

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
        _log.LogInformation("SDB_USER_ENTERWORLD raw: {Hex} (replyId {R}, playerId {P})", Convert.ToHexString(p), replyId, playerId);

        var chr = _store.GetCharacter(playerId);
        bool found = chr?.WorldBlob != null && chr.WorldBlob.Length == WorldBlobSize;
        if (!found)
            _log.LogWarning("SDB_USER_ENTERWORLD: player {Id} has no world blob (found={F}, len={L}) - replying not-found",
                playerId, chr != null, chr?.WorldBlob?.Length ?? 0);
        else
            _log.LogInformation("DBS_USER_ENTERWORLD: sent world blob for '{Name}' (id {Id}) from DB, pos {Pos}, sha256 {Sha}",
                chr!.Name, playerId, BlobPos(chr.WorldBlob!), Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(chr.WorldBlob!))[..16]);

        link.SendFrame(DBS_USER_ENTERWORLD, BuildDbsUserEnterWorld(replyId, found ? chr!.WorldBlob : null));

        // The real Arbiter follows the blob with DBS_USER_RESTRICTION (0x2830) ~3 ms later
        // (lobby_tap.log pkt 131): [u32 off=22][u32 count=0][u64 gameId]. This is the only A->W
        // frame that differs between two logins in the real capture, and chat/whisper bans live
        // in the restriction record World initialises from it. gameId form: 0x80000AF00000|id,
        // unmasked, as sent in AS_ENTER_WORLD [24] and captured (01 00 F0 0A 00 80 00 00).
        if (found && Environment.GetEnvironmentVariable("TERASHARP_NO_RESTRICTION") != "1")
        {
            ulong gameId = GameIdByPlayer.TryGetValue(playerId, out var g) ? g : 0x80000AF00000UL | (ulong)(uint)playerId;
            link.SendFrame(DBS_USER_RESTRICTION, BuildDbsUserRestriction(gameId));
        }
        else if (found)
            _log.LogWarning("TERASHARP_NO_RESTRICTION=1: skipping DBS_USER_RESTRICTION (experiment)");
        return true;
    }

    public const ushort DBS_USER_RESTRICTION = 0x2830;

    // ---- Handshake: 0x147D -> 0x1484 + 24 x 0x147E + 0x1480 (promotion definitions) ----
    // World's PromotionController keeps the promotion datasheet it receives here. Each 0x147E record
    // (1368-byte payload) carries two 16-byte timestamps at [16] and [32]: u16 year, month, day, hour,
    // minute, second + u32 nanoseconds, stamped "now" by the real Arbiter (arb_world.log: 2026-09-12
    // 04:47:51; cap_newchar.log: 2026-09-13 05:47:09). Replaying yesterday's records left World with
    // stale promotion entries, and the first level-1 character to qualify for a newbie promotion
    // crashed World in PromotionController::NewPromotion -> vector<PromotionConditionDataHead> copy
    // (WorldServer+0x128F4B0, minidump 2026-09-14 01:03:25). Level-58 dob never triggers it, which
    // is why the crash only hit new characters on a fresh World.
    // Bytes 568..700 of each record are raw Arbiter heap pointers memcpy'd into the struct; harmless.
    // data/promotions_147E.bin = the 24 records from cap_newchar.log concatenated.
    public const ushort AS_PROMOTION_LIST_REQ = 0x147D;
    public const ushort AS_PROMOTION_LIST_BEGIN = 0x1484;
    public const ushort AS_PROMOTION_RECORD = 0x147E;
    public const ushort AS_PROMOTION_LIST_END = 0x1480;
    public const int PromotionRecordSize = 1368;
    private static byte[]? _promotions;

    private bool OnPromotionListRequest(WorldLink link)
    {
        var data = LoadDataFile("promotions_147E.bin", ref _promotions, 0);
        if (data == null || data.Length % PromotionRecordSize != 0)
        {
            _log.LogWarning("0x147D: promotions_147E.bin missing or malformed - falling back to replay (stale timestamps)");
            return false;
        }
        link.SendFrame(AS_PROMOTION_LIST_BEGIN, new byte[4]);
        var now = DateTime.UtcNow;
        int n = 0;
        for (int off = 0; off + PromotionRecordSize <= data.Length; off += PromotionRecordSize)
        {
            var rec = new byte[PromotionRecordSize];
            Array.Copy(data, off, rec, 0, PromotionRecordSize);
            WriteArbTimestamp(rec, 16, now);
            WriteArbTimestamp(rec, 32, now);
            link.SendFrame(AS_PROMOTION_RECORD, rec);
            n++;
        }
        link.SendFrame(AS_PROMOTION_LIST_END, Array.Empty<byte>());
        _log.LogInformation("0x147D: sent {N} x 0x147E promotion records stamped {Now:u}", n, now);
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
        var blob = Environment.GetEnvironmentVariable("TERASHARP_STARTER_BLOB");
        if (!string.IsNullOrEmpty(blob)) yield return Path.Combine(Path.GetDirectoryName(blob) ?? ".", name);
        string? dir = AppContext.BaseDirectory;
        for (int i = 0; i < 8 && !string.IsNullOrEmpty(dir); i++)
        {
            yield return Path.Combine(dir, "data", name);
            dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar));
        }
        var root = Environment.GetEnvironmentVariable("TERASHARP_DATA") ?? @"D:\v100\TERA_SERVER.100";
        yield return Path.Combine(root, "TeraSharp", "data", name);
    }

    // ---- Post-handshake burst (cap_newchar.log seq 112-113, lobby_tap.log 107-113) ----
    // 1 s after the handshake completes (0x2955/0x2952), before any player, the real Arbiter sends
    // ~100 x 0x1581 [u32 dungeonId][u32 1][u32 0][u8 0]. World's DungeonManager logs "<id> open"
    // for each. Without it a FIRST enter-world into a fresh World process (zone 5 character, i.e.
    // any new character) is silently dropped after DBS_USER_ENTERWORLD and World later crashes;
    // after one successful login (dob, zone 7005) later enters proceed. Before T5 these pushes were
    // replayed by accident (attributed to 0x15A8); T5 correctly sealed 0x15A8 and they vanished.
    // Opcode name unverified (0x1581 is not in world_opcodes.txt as of 2026-09-14).
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

    public static byte[] Build1581(uint dungeonId)
    {
        var p = new byte[13];
        BitConverter.GetBytes(dungeonId).CopyTo(p, 0);
        BitConverter.GetBytes(1u).CopyTo(p, 4);
        return p;
    }

    /// <summary>Called by WorldBridge once the handshake completes. Sends the 0x1581 burst on that link.</summary>
    public void OnWorldReady(WorldLink link)
    {
        foreach (var id in PostHandshakeDungeonIds)
            link.SendFrame(AS_DUNGEON_OPEN_1581, Build1581(id));
        _log.LogInformation("Post-handshake: sent {N} x 0x1581 dungeon-open pushes", PostHandshakeDungeonIds.Length);
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
    public const int StarterInventoryItemStart = 13;
    public const int StarterInventoryItemSize = 536;
    public const int StarterInventoryOwnerOffset = 16;
    public const int CapturedInventoryPlayerId = 1;
    private static byte[]? _starterInventory;

    private bool OnLoadInventory(WorldLink link, byte[] payload)
    {
        if (payload.Length < 8) return false;
        uint reqId = BitConverter.ToUInt32(payload, 0);
        int playerId = (int)BitConverter.ToUInt32(payload, 4);
        if (playerId == CapturedInventoryPlayerId) return false;     // dob: replay-table capture

        var template = LoadStarterInventory();
        if (template == null)
        {
            _log.LogWarning("SDB_USER_LOAD_INVENTORY: starter_inventory.bin not found - falling back to replay (World will reject it for player {Pid})", playerId);
            return false;
        }
        // T14: the kit the real Arbiter would have created for this character's class
        // (CreateCharData.xml). Falls back to the captured glaiver list when we do not know the
        // class — an unknown class is better served the six items that are live-verified to get
        // past SA_ENTER_WORLD_FAILED than nothing at all.
        // _store is null in the pure-protocol unit tests; a class kit needs the row.
        var chr = _store is null ? null : _store.GetCharacter(playerId);
        int classId = chr?.Class ?? -1;
        var kit = StarterInventory.Build(template, classId, playerId, reqId);

        link.SendFrame(DBS_USER_LOAD_POCKET_DATA, BuildEmptyListType1(payload, 0));
        if (kit != null)
        {
            link.SendFrame(DBS_USER_LOAD_INVENTORY, kit);
            _log.LogInformation("SDB_USER_LOAD_INVENTORY: player {Pid} -> {N} starter items for class {Cls} ({Name})",
                playerId, StarterInventory.ForClass(classId)!.Count, classId,
                classId >= 0 && classId < StarterInventory.ClassNames.Length ? StarterInventory.ClassNames[classId] : "?");
        }
        else
        {
            link.SendFrame(DBS_USER_LOAD_INVENTORY, BuildStarterInventory(template, reqId, (uint)playerId));
            _log.LogWarning("SDB_USER_LOAD_INVENTORY: player {Pid} has no class kit (class {Cls}) - serving the captured glaiver list",
                playerId, classId);
        }
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
        yield return Environment.GetEnvironmentVariable("TERASHARP_STARTER_INVENTORY");
        var blob = Environment.GetEnvironmentVariable("TERASHARP_STARTER_BLOB");
        if (!string.IsNullOrEmpty(blob)) yield return Path.Combine(Path.GetDirectoryName(blob) ?? ".", "starter_inventory.bin");
        string? dir = AppContext.BaseDirectory;
        for (int i = 0; i < 8 && !string.IsNullOrEmpty(dir); i++)
        {
            yield return Path.Combine(dir, "data", "starter_inventory.bin");
            dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar));
        }
        var root = Environment.GetEnvironmentVariable("TERASHARP_DATA") ?? @"D:\v100\TERA_SERVER.100";
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
}
