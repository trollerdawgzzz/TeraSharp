# Persistence map — what World writes during play, and which login load it feeds

Source: `D:\packetlogs\cap_newchar.log` (real ArbiterServer, new character "Test" playerId 2,
Island of Dawn, 05:49–05:53: create, enter, quests, gathering, potions, kills, level-ups, skill
learning, item combine, tutorial dungeon enter/leave, logout). Condensed control listing in
`D:\packetlogs\cap_newchar_ctl.txt` (seq, dir, time, opcode, len, first 64 payload bytes).
Names from `data/dbproxy_opcodes.txt`. All frame offsets below are payload-relative.

Today TeraSharp persists ONLY the 15312-byte world blob (0x27CB). Everything in the table below is
answered with a bare ack and forgotten, so on relog the login-time loads serve the captured "dob"
snapshot and the character's progress is gone. Making a row persistent = store the write, serve it
back in the matching load.

## Per-user writes seen during play (all DLMItems: World waits for the reply before the next one)

| write (W->A)                                      | reply (A->W)  | reply shape                                   | count | feeds login load                          |
|---------------------------------------------------|---------------|-----------------------------------------------|-------|-------------------------------------------|
| 0x272E SDB_SET_QUEST_INFO (116 B / 972 B / 3540 B)| 0x272F        | ECHO of request with ok=1 inserted after reqId (len-1) | ~30 | 0x272C/0x272D SDB/DBS_LOAD_QUEST_LIST      |
| 0x273B S_UPDATE_EXP_LEVEL (46 B)                  | 0x273C        | [u32 reqId][u8 ok]                            | 12    | blob (level/exp) + Arbiter DB row (S_LOGIN, S_GET_USER_LIST level) |
| 0x2768 SDB_ITEM_SINGLE (30 B empty / 886 B / 4310 B) | 0x2769     | ECHO with ok inserted (len-3)                 | 8     | 0x27A2 SDB_USER_LOAD_INVENTORY -> 0x27A3 + 0x27A4 |
| 0x278E SDB_USER_LEARN_SKILL (896 B)               | 0x278F        | ECHO-ish (885 B)                              | 1     | unknown — S_SKILL_LIST comes from World; verify vs blob/0x27A4 |
| 0x27FA SDB_UPDATE_USER_ACHIEVEMENT (1478–1526 B)  | 0x27FB        | [reqId][ok]                                   | 4     | 0x27F8/0x27F9 SDB/DBS_LOAD_USER_ACHIEVEMENT |
| 0x2802 SDB_ACCOMPLISH_USER_ACHIEVEMENT (46 B)     | 0x2803        | ECHO with ok (43 B) — or 19 B short form      | 3     | 0x27F8/0x27F9                              |
| 0x2891 SDB_UPDATE_REPUTATION_INFO (78 B)          | 0x2892        | [u8 ok][u32 reqId]                            | 1     | 0x288F/0x2890 SDB/DBS_LOAD_REPUTATION_LIST |
| 0x286E SDB_ADD_TUTORIAL_SIMPLE_TIP (18 B)         | 0x286F        | [reqId][ok]                                   | 4     | 0x2872/0x2873 LOAD_TUTORIAL_SIMPLE_TIP     |
| 0x2944 SDB_UPDATE_SEREN_GUIDE_INFO (22 B)         | 0x2945        | [reqId][u32 pid][ok]                          | 2     | 0x2942/0x2943 INIT_SEREN_GUIDE_INFO        |
| 0x293C SDB_UPDATE_USER_DAILY_EVENT_COUNT (58 B)   | 0x293D        | [reqId][ok]                                   | 1     | 0x293A/0x293B LOAD_USER_DAILY_EVENT        |
| 0x293E SDB_UPDATE_GET_EXTRA_REWARD (20 B)         | 0x293F        | [reqId][ok]                                   | 1     | (login-time)                               |
| 0x297B SDB_UPDATE_USER_ACTPOINT (22 B)            | 0x297C        | [reqId][ok]                                   | 1     | act points (blob?)                         |
| 0x2910 SDB_UPDATE_FATIGABILITY_POINT (23 B)       | 0x2911        | [ok][reqId]                                   | 2     | 0x2908/0x2909 LOAD_FATIGABILITY_LIST       |
| 0x2924 SDB_UPDATE_PASSIVITY_COOLTIME              | 0x2925        | [reqId][ok]                                   | logout| 0x2922/0x2923                              |
| 0x2936 SDB_CHECK_DAILY_ATTENDANCE                 | 0x2937        | [reqId][u8][u32 3]...                         | login+logout | —                                   |
| 0x2930 SDB_UPDATE_HOLD_CHARACTER_STATUS           | 0x2931        | [reqId][ok][u8 0]                             | spawn | —                                          |
| 0x27B3 SDB_UPDATE_DAILY_LIMIT_EP_EXP              | 0x27B4        | [reqId][ok]                                   | spawn | —                                          |
| 0x2736 SDB_END_START_QUEST_LIST (10 B)            | 0x2737        | [reqId][ok]                                   | many  | marks end of a quest-list batch            |
| 0x27CB SDB_UPDATE_USER_DATA (blob)                | 0x27CC        | [reqId][u32 1]                                | spawn, zone change, logout | 0x2711/0x2738 — DONE (SQLite) |
| 0x2927 SDB_CANCEL_NPC_ARENA_BET (14 B)            | none          | fire-and-forget                               | periodic | ignore; do NOT reply                    |

"ECHO" replies: the Arbiter writes the request back with the ok byte inserted after reqId and the
list offsets shifted by 1 (e.g. 0x272F = 0x272E with `01` after reqId; 0x2769 = 0x2768 minus 3
bytes). Confirm each in the Arbiter writer before implementing — it is the same 4-byte-offset
backpatch pattern as the login-time empty lists.

## Non-DB control traffic seen (no reply, or Arbiter-initiated)

- 0x1491 SA_UPDATE_USER_STATUS `[u64 handle][u32 inCombat][u8]` — no reply.
- 0x156F SA_UPDATE_MAKRER_END `[u32 off][u32 0][u32 pid]` — no reply, paired with 0x273B.
- 0x13B6 SA_UPDATE_DUNGEON_COOLTIME, 0x13C5 SA_ADD_DUNGEON_CHANNEL, 0x13C6 SA_REMOVE_DUNGEON_CHANNEL,
  0x1499 SA_SAVE_ETC_DATA_FOR_MOVE_WORLD, 0x15FA, 0x162C->0x162D — no DB reply.
- **Zone change / dungeon enter flow** (05:52:35–41): W 0x13BE SA_REQUEST_ENTER_DUNGEON (215 B)
  -> A 0x13BF AS_REQUEST_ENTER_DUNGEON (echo, 215 B); W 0x13C0 SA_RESPONSE_ENTER_DUNGEON ->
  A 0x13C1 AS_RESPONSE_ENTER_DUNGEON (echo); W 0x13C5; W 0x1499 -> then World sends 0x2768 +
  0x27CB x2 (save), and the Arbiter re-sends **0x1439 AS_UPDATE_VISITED_SECTION_LIST + 0x1390 +
  0x138F** to spawn the player in the new zone. Then 0x13AA + 0x27FA/0x2802 achievement writes
  (0x13AA is NOT logout-specific). TeraSharp has none of this: entering an instance/zone will hang.
- 0x1439 AS_UPDATE_VISITED_SECTION_LIST is Arbiter-initiated from the client's C_VISIT_NEW_SECTION
  (the list grows: 18 B at first spawn, 34 B, 50 B). TeraSharp currently forwards C_VISIT_NEW_SECTION
  to World as a tunnel packet (RegNoop) — the real Arbiter owns it. Handler: Handler_C_VISIT_NEW_SECTION.
- 0x1493 AS_REQUEST_SPAWN_TERRITORY (Arbiter push, 22 B) once.
- Countdown after 0x14FF: W 0x2927 ticks `FF FF FF FF 04 04 01 00` / 03 / 01 (no reply).
- First-player-of-the-process burst after 0x2738 (seq 138–153): ~60 config pushes
  (0x15BD, 0x156B, 0x13C7, 0x157E, 0x15DE, 0x1582, 0x157F, 0x14B3, 0x150D/E, 0x1510/11, 0x14D0/1,
  0x14E1, 0x14EC, 0x1529, 0x28F8 x2, 0x1556, 0x150A, 0x14E6, 0x1623, 0x149D/E/F x5, 0x15B6, 0x15C2,
  0x15C5, 0x15D4/5, 0x1603, 0x160D/F, 0x1609, 0x14E9, 0x14E2, 0x14EE, 0x14F2/3/7, 0x1613, 0x161D,
  0x1589, 0x162E, 0x1567, 0x28E3/5/6/B, 0x29E2). Also 0x2956->0x2957 city-war league create and
  0x295C/0x15F9 -> 0x15ED/0x295D at World start. Static config; replay-table territory.

## Proposed implementation order

1. **Quests** — 0x272E/0x272F store + 0x272D built from stored state. Highest visible impact
   (quest progress survives relog). Needs the 0x272E layout from `Handler_SDB_SET_QUEST_INFO`
   (Arbiter) and the 0x272D writer; the 972 B / 3540 B forms carry a blob per quest.
2. **Inventory** — 0x2768/0x2769 store (item single ops: add/remove/move/stack) + 0x27A4 built from
   stored items. Layout: `Handler_SDB_ITEM_SINGLE` + `FUN_…(pkt,0x27a4)` writer. Also 0x2813
   SDB_EQUIP_ITEM (not seen this session — equip via C_EQUIP_ITEM may route differently).
3. **Level/exp** — 0x273B store into the characters row (level shows in S_GET_USER_LIST).
4. **Achievements** (0x27FA/0x2802 -> 0x27F9), **reputation** (0x2891 -> 0x2890), **tutorial
   tips** (0x286E -> 0x2873), **seren guide** (0x2944 -> 0x2943), **daily event** (0x293C -> 0x293B),
   **fatigability** (0x2910 -> 0x2909) — small, same pattern each.
5. **Skills** — 0x278E; first establish where World reads learned skills from at login.
6. **Zone change / dungeon flow** (0x13BE/0x13C0/0x1499 + re-spawn) — not persistence, but the next
   live blocker once players leave Island of Dawn.
