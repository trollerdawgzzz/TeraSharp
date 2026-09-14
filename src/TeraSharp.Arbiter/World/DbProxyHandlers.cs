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
    public const ushort SDB_LOAD_WORLD_EVENT = 0x27B3;           // -> 0x27B4: [u32 reqId][u8 ok] (5B)
    // MISNAMED, kept as an alias because the live path and several tests use it: 0x2910 is
    // SDB_UPDATE_FATIGABILITY_POINT, not a friend-info load. WorldServer's own opcode table says
    // so, and the payload is a fatigue delta. Prefer SDB_FATIGABILITY_UPDATE in new code. T26.
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
    // --- SDB_LOAD_TUTORIAL_SIMPLE_TIP (0x2872) -> DBS_LOAD_TUTORIAL_SIMPLE_TIP (0x2873), T22 ---
    // Fed by SDB_ADD_TUTORIAL_SIMPLE_TIP (0x286E), one tip id per write.
    //   req  [0] u32 reqId  [4] u32 playerId                       (handler needs frame >= 0x0e)
    //   rsp  [0] u32 listOff=19 [4] u32 listLen [8] u32 reqId [12] u8 ok=1, then 8 B per tip
    //   tip  [u32 tipId][u32 1]   - the second word is 1 in every record of every capture
    // cap_newchar.log adds tips 1, 2, 35 and 39 (seq 719/765/864/911); the relog capture serves
    // exactly those four, in that order, to both characters. Empty list for a fresh character
    // (cap_newchar seq 176).
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
        SA_LOAD_SERVANT_DATA,                       // 0x1539
        SA_LOAD_SERVANT_ADVENTURE_DATA,             // 0x153B
        SA_LOAD_EXTRAPOINT_DATA,                    // 0x1554
        SA_LOAD_BATTLE_FIELD_ENTER_COUNT,           // 0x155D
        SDB_LOAD_ITEM_RECIPE,                       // 0x2760
        SDB_LOAD_SKILL_PROF,                        // 0x2764
        SDB_LOAD_TELEPORT_TO_POS_LIST,              // 0x27A7
        SDB_LOAD_USER_RESTRICTION,                  // 0x2833
        SDB_LOAD_BATTLE_FIELD_LIST,                 // 0x2895
        SDB_LOAD_REFER_A_FRIEND,                    // 0x28B0
        SDB_LOAD_28B7,                              // 0x28B7
        SDB_LOAD_ACCOUNT_BENEFIT,                   // 0x28BB
        SDB_LOAD_SERVANT_PERIOD,                    // 0x28C5
        SDB_LOAD_SKILLPERIOD,                       // 0x28C9
        SDB_LOAD_LEARNED_SOCIAL,                    // 0x28CF
        SDB_LOAD_TOKEN_EXCHANGE,                    // 0x28FE
        SDB_LOAD_2900,                              // 0x2900
        SDB_LOAD_QUEST_PROGRESS,                    // 0x2902
        SDB_LOAD_PROMOTION_LIST,                    // 0x2912
        SDB_LOAD_PROMOTION_COND_LIST,               // 0x2916
        SDB_LOAD_PASSIVITY_COOLTIME,                // 0x2922
        SDB_LOAD_293A,                              // 0x293A
        SDB_LOAD_ADDITIONAL_REWARD,                 // 0x2967
        SDB_LOAD_2975,                              // 0x2975
        SDB_LOAD_ACHIEVE_LIST,                      // 0x2981
        SDB_LOAD_2986,                              // 0x2986
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
            case SA_ENTER_WORLD_FAIL:               // 0x148D pushes + a re-sent 0x138E (T21)
            case SDB_LOAD_2869:          // 0x15E0 push + 0x286A, both carrying the live reset time
            case AS_PROMOTION_LIST_REQ:  // 0x147D -> 0x1484 + 24 x 0x147E (timestamps = now) + 0x1480
            // --- T15: the per-user writes World sends during play. Each one is a DLM item; a
            // missing reply head-blocks the user's queue for the life of the World process. ---
            case SDB_USER_LEARN_SKILL:            // 0x278F = echo of the fee atoms + empty skill-period list
            case SDB_ACCOMPLISH_USER_ACHIEVEMENT: // 0x2803 = the newly-accomplished records echoed
            case SDB_UPDATE_REPUTATION_INFO:      // 0x2892 = [ok][reqId]  (ok-first, the odd one out)
            case SDB_ADD_TUTORIAL_SIMPLE_TIP:     // 0x286F = [reqId][ok]
            case SDB_UPDATE_SEREN_GUIDE_INFO:     // 0x2945 = [reqId][playerId][ok]
            case SDB_UPDATE_USER_DAILY_EVENT_COUNT: // 0x293D = [reqId][ok], reqId at payload[8]
            case SDB_UPDATE_GET_EXTRA_REWARD:     // 0x293F = [reqId][ok]
            // --- T22: the per-character login loads, rebuilt from rows instead of replaying
            // dob's captured reply to everyone. Each one is byte-exact against BOTH the
            // brand-new-character reply in cap_newchar.log and the with-progress reply in
            // arb_world_2026-09-13; playerId 1 still gets the capture. ---
            case SDB_USER_ACHIEVEMENT:            // 0x27F9 from the stored 0x27FA + 0x2802 records
            case SDB_TUTORIAL_SIMPLE_TIP:         // 0x2873 from the stored 0x286E tips
            case SDB_SEREN_GUIDE:                 // 0x2943 from the stored 0x2944 slots
            case SDB_LOAD_2867:                   // 0x2868, now rebuilt from dungeon_cooldowns
            // --- T25: one-way dungeon writes. They send nothing back; they are here so
            // TryHandle sees them at all and can persist them. ---
            case SA_UPDATE_DUNGEON_COOLTIME:      // 0x13B6
            case SA_UPDATE_DUNGEON_CLEAR_COUNT:   // 0x13B7
            case SA_DELETE_DUNGEON_COOLTIME:      // 0x13BD
            // --- T26: the last two per-character login loads, rebuilt from rows. ---
            case SDB_REPUTATION_LIST:             // 0x2890 from the stored 0x2891 records
            case SDB_FATIGABILITY_LIST:           // 0x2909 from the account's fatigue row
            // --- T23: city-war result. Not a DLM item (no reqId), but the reply echoes two
            // u32s out of the live request, so a replayed 0x295D would carry captured ones. ---
            case SDB_RESULT_CITY_WAR:             // 0x15ED + 0x295D, both echoing request+16/+20
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

            // --- T45: the mailbox. Six live requests; before T45 none of them was answered at
            // all, and no capture has one either, so the replay table could not cover for us.
            case SDB_LIST_PARCEL:                 // 0x2778, the empty form is byte-exact
            case SDB_MAKE_PARCEL:                 // 0x277a, atoms echoed with allocated ids
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
            case SDB_SAVE_27FA: return OnSaveUserAchievement(link, payload);
            case SDB_SAVE_2924: link.SendFrame(DBS_SAVE_2925, BuildReqIdAck(payload, 8)); return true;
            case SDB_SAVE_2768: return OnItemSingle(link, payload);
            case SDB_SAVE_2936: link.SendFrame(DBS_SAVE_2937, BuildDbs2937(payload)); return true;
            case SDB_DAILY_QUEST: link.SendFrame(DBS_DAILY_QUEST, BuildReqIdAck(payload, 16)); return true;
            case SDB_DAILY_QUEST_SEED: link.SendFrame(DBS_DAILY_QUEST_SEED, BuildReqIdAck(payload, 8)); return true;

            // --- T15: per-user writes during play (all echo the LIVE reqId) ---
            case SDB_USER_LEARN_SKILL:            return OnUserLearnSkill(link, payload);
            case SDB_ACCOMPLISH_USER_ACHIEVEMENT: return OnAccomplishUserAchievement(link, payload);
            case SDB_UPDATE_REPUTATION_INFO:        return OnUpdateReputation(link, payload);
            case SDB_ADD_TUTORIAL_SIMPLE_TIP:       return OnAddTutorialTip(link, payload);
            case SDB_UPDATE_SEREN_GUIDE_INFO:       return OnUpdateSerenGuide(link, payload);
            case SDB_UPDATE_USER_DAILY_EVENT_COUNT: link.SendFrame(DBS_UPDATE_USER_DAILY_EVENT_COUNT, BuildReqIdAck(payload, 8)); return true;
            case SDB_UPDATE_GET_EXTRA_REWARD:       link.SendFrame(DBS_UPDATE_GET_EXTRA_REWARD, BuildReqIdAck(payload, 0)); return true;

            // --- T23 ---
            case SDB_RESULT_CITY_WAR:               return OnResultCityWar(link, payload);

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

            // --- T45: parcels ---
            case SDB_LIST_PARCEL:             return OnListParcel(link, payload);
            case SDB_MAKE_PARCEL:             return OnMakeParcel(link, payload);
            case SDB_RECV_PARCEL:             return OnRecvParcel(link, payload);
            case SDB_RECV_PARCEL_EX:          return OnRecvParcelEx(link, payload);
            case SDB_RETURN_PARCEL:           return OnReturnParcel(link, payload);
            case SDB_DELETE_PARCEL:           return OnDeleteParcel(link, payload);

            // --- T51: guilds ---
            case SDB_INIT_GUILD:              return OnInitGuild(link);

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
                    BitConverter.ToUInt32(payload, DungeonCtxPlayerId),
                    BitConverter.ToUInt32(payload, DungeonCtxDungeonId), payload.Length);
                RecordDungeonEntry(payload, response: false);
                link.SendFrame(AS_REQUEST_ENTER_DUNGEON, r);
                return true;
            }
            case SA_RESPONSE_ENTER_DUNGEON:
            {
                var r = BuildAsResponseEnterDungeon(payload);
                if (r == null) return false;
                RecordDungeonEntry(payload, response: true);
                link.SendFrame(AS_RESPONSE_ENTER_DUNGEON, r);
                return true;
            }
            case SA_ENTER_WORLD_FAIL: return OnEnterWorldFail(link, payload);

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
            case SDB_LOAD_FRIEND_INFO:    return OnUpdateFatigability(link, payload);

            // --- Remaining login-time: programmatic builders ---
            case SDB_LOAD_2867: return OnLoadDungeonCoolTime(link, payload);
            case SA_UPDATE_DUNGEON_COOLTIME:    return OnUpdateDungeonCoolTime(payload);
            case SA_UPDATE_DUNGEON_CLEAR_COUNT: return OnUpdateDungeonClearCount(payload);
            case SA_DELETE_DUNGEON_COOLTIME:    return OnDeleteDungeonCoolTime(payload);
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
            case SDB_TUTORIAL_SIMPLE_TIP: return OnLoadTutorialTips(link, payload);
            case SDB_REPUTATION_LIST:     return OnLoadReputationList(link, payload);
            case SDB_LOAD_293A:           link.SendFrame(0x293B, BuildFromStaticData(DbProxyStaticData.Load293B, DbProxyStaticData.Load293BReqIdOffset, payload)); return true;
            case SDB_FATIGABILITY_LIST:   return OnLoadFatigability(link, payload);
            case SDB_SEREN_GUIDE:         return OnLoadSerenGuide(link, payload);
            case SDB_EP_PERK:             link.SendFrame(0x27BA, BuildFromStaticData(DbProxyStaticData.EpPerk, DbProxyStaticData.EpPerkReqIdOffset, payload)); return true;
            case SDB_QUEST_LIST:          return OnLoadQuestList(link, payload);
            case SDB_USER_ACHIEVEMENT:    return OnLoadUserAchievement(link, payload);

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
    private void RecordDungeonEntry(byte[] payload, bool response)
    {
        if (_store is null || payload.Length < ResponseEnterDungeonMinPayload) return;
        int playerId = (int)BitConverter.ToUInt32(payload, DungeonCtxPlayerId);
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
                    "SDB_ITEM_SINGLE: player {Pid} -> {Ins} inserted, {Mov} moved, {Chg} amount, {Del} deleted ({Ign} atom(s) changed no row)",
                    playerId, a.Inserted + b.Inserted, a.Moved + b.Moved,
                    a.AmountChanged + b.AmountChanged, a.Deleted + b.Deleted, a.Ignored + b.Ignored);
        }

        link.SendFrame(DBS_SAVE_2769, reply);
        return true;
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

        // ViewPos/EndPos are the window into the page the client asked for. With one page of
        // 0x48 slots and no paging UI of our own, the honest answer is "the whole list".
        uint endPos = viewPos + (uint)records.Count;
        _log.LogInformation("SDB_VIEW_WAREHOUSE: owner {Owner} pocket {Pocket} -> {N} item(s), {Money} money",
            ownerDbId, invenType, records.Count, money);

        link.SendFrame(DBS_VIEW_WAREHOUSE, WarehouseHandlers.BuildDbsViewWarehouse(
            dlmId, ok: true, viewPos, endPos, (uint)records.Count, money,
            (ushort)Math.Clamp(slotCount, 0, ushort.MaxValue), records));
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

        var r = WarehouseHandlers.Apply(_store, parsed, _store.NextItemId, _log);
        if (parsed.Count > 0)
            _log.LogInformation("{Op}: {N} atom(s) -> {Ins} inserted, {Mov} moved, {Chg} amount, {Del} deleted, {Money} money, {Ign} ignored",
                DbProxyOpcodeNames.Describe(replyOp), parsed.Count, r.Inserted, r.Moved, r.AmountChanged,
                r.Deleted, r.MoneyDelta, r.Ignored);

        // WareCommision is 0 in this build: CommisionPayed() returns 1 and GetWareCommision()
        // returns 0 in the decompile, so the fee path is effectively disabled.
        link.SendFrame(replyOp, WarehouseHandlers.BuildDbsTransfer(atoms, dlmId, ok: true, error: 0, wareCommision: 0));
        return true;
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
    /// </summary>
    private bool OnAccomplishUserAchievement(WorldLink link, byte[] payload)
    {
        int playerId = payload.Length >= AchievementRequestHeader
            ? (int)BitConverter.ToUInt32(payload, 12) : 0;

        var offeredRecords = SliceAchievementRecords(payload);
        var kept = offeredRecords;                     // no store: every record looks new
        if (_store is not null && playerId > 0)
        {
            var offered = new List<(int Id, byte[] Record)>(offeredRecords.Count);
            foreach (var rec in offeredRecords)
                offered.Add(((int)BitConverter.ToUInt32(rec, AchievementRecordIdOffset), rec));
            kept = _store.AddAccomplishedAchievements(playerId, offered);
        }

        _log.LogInformation("SDB_ACCOMPLISH_USER_ACHIEVEMENT: player {Pid} offered {O}, {N} newly accomplished",
            playerId, offeredRecords.Count, kept.Count);
        link.SendFrame(DBS_ACCOMPLISH_USER_ACHIEVEMENT, BuildDbs2803(payload, null, kept));
        return true;
    }

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
        if (_store is not null && payload.Length >= TutorialTipIdOffset + 4)
        {
            int playerId = (int)BitConverter.ToUInt32(payload, 4);
            int tipId = (int)BitConverter.ToUInt32(payload, TutorialTipIdOffset);
            if (playerId > 0 && _store.AddTutorialTip(playerId, tipId))
                _log.LogInformation("SDB_ADD_TUTORIAL_SIMPLE_TIP: player {Pid} saw tip {Tip}", playerId, tipId);
        }
        link.SendFrame(DBS_ADD_TUTORIAL_SIMPLE_TIP, BuildReqIdAck(payload, 0));
        return true;
    }

    /// <summary>SDB_LOAD_TUTORIAL_SIMPLE_TIP (0x2872) -&gt; 0x2873, rebuilt from the stored tips.</summary>
    private bool OnLoadTutorialTips(WorldLink link, byte[] payload)
    {
        uint reqId = payload.Length >= 4 ? BitConverter.ToUInt32(payload, 0) : 0;
        int playerId = payload.Length >= 8 ? (int)BitConverter.ToUInt32(payload, 4) : 0;
        if (_store is null || ServesCapturedStatics(playerId))
        {
            link.SendFrame(DBS_TUTORIAL_SIMPLE_TIP, BuildFromStaticData(
                DbProxyStaticData.Tutorial, DbProxyStaticData.TutorialReqIdOffset, payload));
            return true;
        }
        var tips = _store.GetTutorialTips(playerId);
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
        int len = tipIds.Count * TutorialTipRecordSize;
        var r = new byte[TutorialTipReplyHeader + len];
        BitConverter.GetBytes(6u + TutorialTipReplyHeader).CopyTo(r, 0);   // 19, frame-relative
        BitConverter.GetBytes((uint)len).CopyTo(r, 4);
        BitConverter.GetBytes(reqId).CopyTo(r, 8);
        r[12] = 1;                                                          // ok
        int p = TutorialTipReplyHeader;
        foreach (int tip in tipIds)
        {
            BitConverter.GetBytes(tip).CopyTo(r, p);
            BitConverter.GetBytes(1).CopyTo(r, p + 4);
            p += TutorialTipRecordSize;
        }
        return r;
    }

    /// <summary>SDB_UPDATE_SEREN_GUIDE_INFO (0x2944): store the slot, then ack.</summary>
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
    // L"NextChange", L"SendSystemMessage") and the handler's own bounds check
    // `(longlong)iVar2 + 0x16U <= (ulonglong)(longlong)*piVar9`:
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
    // The bytes live in data/handshake_burst.bin (TSIS container, same shape as cap_t15.bin)
    // rather than in a literal here: 63 frames, 1082 frame bytes, 50 distinct opcodes.
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
    /// exactly once per World process). Sends the captured config burst first, then the 0x1581
    /// dungeon-open pushes - the order in lobby_tap.log, the capture the live-verified login came
    /// from, and the order that puts AS_INITIALIZE_DUNGEON_ID / AS_DUNGEON_DISABLED_LIST before
    /// the per-dungeon opens. (cap_newchar.log has them the other way round only because that
    /// Arbiter stalled ~11 s on a DB query before its burst went out; the burst's own internal
    /// order is identical in all four captures.)
    /// </summary>
    public void OnWorldReady(WorldLink link)
    {
        var burst = LoadHandshakeBurst();
        if (burst == null)
        {
            _log.LogWarning("Post-handshake: data/{File} missing or malformed - the {N}-push config burst was NOT sent",
                HandshakeBurstFile, HandshakeBurstFrameCount);
        }
        else
        {
            var now = DateTimeOffset.UtcNow;
            ulong reset = DailyResetUnixSeconds(now);
            foreach (var (op, payload) in BuildHandshakeBurst(burst, now))
                link.SendFrame(op, payload);
            _log.LogInformation("Post-handshake: sent {N} config pushes (0x15BD = {Now:u}, 0x14D1 daily reset = {Reset:u})",
                burst.Count, now.UtcDateTime, DateTimeOffset.FromUnixTimeSeconds((long)reset).UtcDateTime);
        }

        // The FALLBACK, not the mechanism - the real Arbiter only ever echoes World's 0x13F2.
        // See the comment on PostHandshakeDungeonIds and status/HANDSHAKE-DATA.md section 0.
        if (!SendPostHandshakeDungeonBurst)
        {
            _log.LogInformation("Post-handshake: 0x1581 fallback burst disabled - waiting for World's 0x13F2 echo");
            return;
        }
        foreach (var id in PostHandshakeDungeonIds)
            link.SendFrame(AS_DUNGEON_OPEN_1581, Build1581(id));
        _log.LogInformation("Post-handshake: sent {N} x 0x1581 dungeon-open pushes (fallback; World's 0x13F2 echo supersedes them)",
            PostHandshakeDungeonIds.Length);
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
        else
        {
            inventory = BuildStarterInventory(template, reqId, (uint)playerId);
            _log.LogWarning("SDB_USER_LOAD_INVENTORY: player {Pid} has no class kit (class {Cls}) - serving the captured glaiver list",
                playerId, classId);
        }

        if (_store is not null)
        {
            if (_store.CountInventoryItems(playerId) == 0)
            {
                int seeded = BagItems.Seed(_store, playerId, inventory);
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

        var body = ParcelDbHandlers.BuildParcelList(_store, (int)userDbId, out uint count, out uint maxPage);
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

        // The receiver is the one field of ParcelData we have pinned (+0x50, from
        // Handler_C_SHOW_PARCEL_MESSAGE's ownership test).
        uint recverDbId = record.Length >= ParcelDbHandlers.ParcelDataReceiverDbId + 4
            ? BitConverter.ToUInt32(record, ParcelDbHandlers.ParcelDataReceiverDbId)
            : 0u;

        var (atoms, parsed) = WarehouseHandlers.CloneAtomsWithIds(
            payload, ParcelDbHandlers.MakeReqTransListRef, ParcelDbHandlers.MakeRequestSize, _store.NextItemId);

        int parcelId = _store.CreateParcel(senderDbId: 0, senderName: "", receiverDbId: (int)recverDbId,
            title: "", message: "", money: 0);
        if (record.Length > 0) _store.SetParcelRecord(parcelId, record);

        int slot = 0;
        foreach (var atom in parsed)
        {
            if (slot >= CharacterStore.MaxParcelAttachments) break;
            if (atom.ItemDbId == 0) continue;
            _store.AddParcelItem(parcelId, slot++, (int)atom.ItemDbId, atom.TemplateId, atom.Delta);
        }

        _log.LogInformation("SDB_MAKE_PARCEL: parcel {Id} for user {User}, {N} attachment(s), {B} B record",
            parcelId, recverDbId, slot, record.Length);
        link.SendFrame(DBS_MAKE_PARCEL,
            ParcelDbHandlers.BuildDbsMakeParcel(atoms, dlmId, ok: true, sendParcelError: 0, recverDbId));
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

        var (atoms, parsed) = WarehouseHandlers.CloneAtomsWithIds(
            payload, ParcelDbHandlers.RecvReqTransListRef, ParcelDbHandlers.RecvRequestSize, _store.NextItemId);
        var applied = WarehouseHandlers.Apply(_store, parsed, _store.NextItemId, _log);

        var row = _store.GetParcel((int)parcelId);
        var record = _store.GetParcelRecord((int)parcelId);
        if (row is not null) _store.SetParcelRecved((int)parcelId, row.ReceiverDbId);

        _log.LogInformation("SDB_RECV_PARCEL: parcel {Id} step {Step} -> {Ins} inserted, {Chg} amount",
            parcelId, step, applied.Inserted, applied.AmountChanged);
        link.SendFrame(DBS_RECV_PARCEL,
            ParcelDbHandlers.BuildDbsRecvParcel(record, atoms, dlmId, step, ok: row is not null));
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

        var (atoms, parsed) = WarehouseHandlers.CloneAtomsWithIds(
            payload, ParcelDbHandlers.RecvExReqRefB, ParcelDbHandlers.RecvExRequestSize, _store.NextItemId);
        WarehouseHandlers.Apply(_store, parsed, _store.NextItemId, _log);

        uint remaining = 0;
        foreach (var row in _store.GetParcelsFor((int)ownerDbId))
        {
            if (!row.IsRecved) { _store.SetParcelRecved(row.ParcelId, row.ReceiverDbId); remaining++; }
        }

        _log.LogInformation("SDB_RECV_PARCEL_EX: owner {Owner} step {Step} -> {N} parcel(s) claimed",
            ownerDbId, step, remaining);
        link.SendFrame(DBS_RECV_PARCEL_EX,
            ParcelDbHandlers.BuildDbsRecvParcelEx(null, atoms, dlmId, step, noParcel: 0, ok: true));
        return true;
    }

    /// <summary>SDB_RETURN_PARCEL (0x2781) -> DBS_RETURN_PARCEL (0x2782): sender and receiver
    /// swap and the mail goes back unread.</summary>
    private bool OnReturnParcel(WorldLink link, byte[] payload)
    {
        uint dlmId = ParcelDbHandlers.U32(payload, ParcelDbHandlers.ReturnReqDlmId);
        uint parcelId = ParcelDbHandlers.U32(payload, ParcelDbHandlers.ReturnReqParcelId);

        bool ok = false;
        if (payload.Length >= ParcelDbHandlers.ReturnRequestSize && _store is not null)
        {
            var row = _store.GetParcel((int)parcelId);
            ok = row is not null && _store.ReturnParcel((int)parcelId, row.ReceiverDbId);
        }
        _log.LogInformation("SDB_RETURN_PARCEL: parcel {Id} -> {Ok}", parcelId, ok);
        link.SendFrame(DBS_RETURN_PARCEL, ParcelDbHandlers.BuildDbsDlmAck(dlmId, ok));
        return true;
    }

    /// <summary>
    /// SDB_DELETE_PARCEL (0x2811) -> DBS_DELETE_PARCEL (0x2812). The request carries a list of
    /// parcel ids as a binary ref; the real handler walks it as u32s.
    /// </summary>
    private bool OnDeleteParcel(WorldLink link, byte[] payload)
    {
        uint dlmId = ParcelDbHandlers.U32(payload, ParcelDbHandlers.DeleteReqDlmId);
        var list = ParcelDbHandlers.Ref(payload, ParcelDbHandlers.DeleteReqDelListRef);

        int deleted = 0;
        if (_store is not null)
        {
            for (int at = 0; at + 4 <= list.Length; at += 4)
                if (_store.DeleteParcel((int)BitConverter.ToUInt32(list, at))) deleted++;
        }
        _log.LogInformation("SDB_DELETE_PARCEL: {N} of {M} parcel(s) deleted", deleted, list.Length / 4);
        link.SendFrame(DBS_DELETE_PARCEL, ParcelDbHandlers.BuildDbsDlmAck(dlmId, ok: true));
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
}
