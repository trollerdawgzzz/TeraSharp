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
    public const ushort SDB_QUEST_LIST = 0x272C;            // -> 0x272D (1377B, reqId@49)
    public const ushort SDB_USER_ACHIEVEMENT = 0x27F8;      // -> 0x27F9 (1501B, reqId@304)

    private readonly CharacterStore _store;
    private readonly ILogger _log;

    public DbProxyHandlers(CharacterStore store, ILogger log)
    {
        _store = store;
        _log = log;
    }

    /// <summary>Returns true if handled (caller should not fall back to replay).</summary>
    public bool TryHandle(WorldBridge bridge, WorldLink link, ushort op, byte[] payload)
    {
        // Only intercept messages that need live per-session data (character blob + logout
        // saves). Every login-time SDB_* is served byte-exact by the replay table; the
        // synthetic builders below desynced World (0x27A2 returns a 3235-byte item list,
        // not an empty list) so they are bypassed here and kept only for their unit tests.
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
            case SA_REQUEST_ENTER_DUNGEON:          // 0x13BF (zone change step 1)
            case SA_RESPONSE_ENTER_DUNGEON:         // 0x13C1 (zone change step 2)
            case SDB_LOAD_2869:          // 0x15E0 push + 0x286A, both carrying the live reset time
                break;               // handled by the real switch below
            default:
                return false;        // -> replay table
        }

        // Name the opcode in the log so a real handler is distinguishable from a replay at a
        // glance (DbProxyOpcodeNames is generated from WorldServer.exe.c; logging only).
        _log.LogDebug("DbProxy handling {Op} len={Len}", DbProxyOpcodeNames.Describe(op), payload.Length + 6);

        switch (op)
        {
            case SDB_USER_ENTERWORLD: return OnUserEnterWorld(link, payload);
            case SDB_UPDATE_USER_DATA: return OnUpdateUserData(link, payload);
            case SDB_USER_LOAD_INVENTORY: return OnLoadInventory(link, payload);
            case SDB_UPDATE_EXP_LEVEL: return OnUpdateExpLevel(link, payload);

            // --- Logout save sequence (reqId echoed from the live request) ---
            case SDB_SAVE_27FA: link.SendFrame(DBS_SAVE_27FB, BuildReqIdAck(payload, 280)); return true;
            case SDB_SAVE_2924: link.SendFrame(DBS_SAVE_2925, BuildReqIdAck(payload, 8)); return true;
            case SDB_SAVE_2768: return OnItemSingle(link, payload);
            case SDB_SAVE_2936: link.SendFrame(DBS_SAVE_2937, BuildDbs2937(payload)); return true;
            case SDB_DAILY_QUEST: link.SendFrame(DBS_DAILY_QUEST, BuildReqIdAck(payload, 16)); return true;
            case SDB_DAILY_QUEST_SEED: link.SendFrame(DBS_DAILY_QUEST_SEED, BuildReqIdAck(payload, 8)); return true;

            // --- Post-spawn (reqId at payload[0] for all three) ---
            case SDB_END_START_QUEST_LIST: link.SendFrame(DBS_END_START_QUEST_LIST, BuildReqIdAck(payload, 0)); return true;
            case SDB_LOAD_2930:            link.SendFrame(DBS_LOAD_2931, Build2931(payload)); return true;
            case SA_CLEAR_BATTLE_FIELD_ENTER_COUNT: link.SendFrame(AS_CLEAR_BATTLE_FIELD_ENTER_COUNT, BuildOkReqId(payload, 8)); return true;
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
            case SDB_QUEST_LIST:          link.SendFrame(0x272D, BuildFromStaticData(DbProxyStaticData.QuestListEmpty, DbProxyStaticData.QuestListEmptyReqIdOffset, payload)); return true;
            case SDB_USER_ACHIEVEMENT:    link.SendFrame(0x27F9, BuildFromStaticData(DbProxyStaticData.Achievement, DbProxyStaticData.AchievementReqIdOffset, payload)); return true;

            default: return false;
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

    /// <summary>Copy one atom list out of the request, allocating ids for the inserts in it.</summary>
    private static byte[] CloneAtomList(byte[] request, int headerOffset, Func<int> allocateItemId)
    {
        if (headerOffset + 8 > request.Length) return Array.Empty<byte>();
        int frameOffset = (int)BitConverter.ToUInt32(request, headerOffset);
        int length = (int)BitConverter.ToUInt32(request, headerOffset + 4);
        if (length <= 0) return Array.Empty<byte>();

        int start = frameOffset - 6;                            // offsets in the frame count the header
        if (start < ItemSingleRequestHeader || length % ItemAtomSize != 0 || start + length > request.Length)
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
        if (found)
        {
            ulong gameId = GameIdByPlayer.TryGetValue(playerId, out var g) ? g : 0x80000AF00000UL | (ulong)(uint)playerId;
            link.SendFrame(DBS_USER_RESTRICTION, BuildDbsUserRestriction(gameId));
        }
        return true;
    }

    public const ushort DBS_USER_RESTRICTION = 0x2830;

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
        link.SendFrame(DBS_USER_LOAD_POCKET_DATA, BuildEmptyListType1(payload, 0));
        link.SendFrame(DBS_USER_LOAD_INVENTORY, BuildStarterInventory(template, reqId, (uint)playerId));
        _log.LogInformation("SDB_USER_LOAD_INVENTORY: player {Pid} -> starter inventory (6 items, owner patched)", playerId);
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
