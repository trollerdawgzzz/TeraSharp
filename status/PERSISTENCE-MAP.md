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

| write (W->A)                                      | reply (A->W)  | reply shape                                   | count | state |
|---------------------------------------------------|---------------|-----------------------------------------------|-------|-------|
| 0x272E SDB_SET_QUEST_INFO (116 / 972 / 3540 B)    | 0x272F        | 29-B header + 80-B quest record + reward atoms; ok at [24], allocated quest row id at [25] | ~30 | **real (T15)** |
| 0x273B S_UPDATE_EXP_LEVEL (46 B)                  | 0x273C        | [u32 reqId][u8 ok]                            | 12    | real (T6), writes level/exp to the row |
| 0x2768 SDB_ITEM_SINGLE (30 / 886 / 4310 B)        | 0x2769        | 21-B header + both atom lists, insert ids filled in | 8 | real (T13) |
| 0x278E SDB_USER_LEARN_SKILL (896 B)               | 0x278F        | 23-B header + the fee atoms + EMPTY SkillPeriodData list | 1 | **real (T15)** |
| 0x27FA SDB_UPDATE_USER_ACHIEVEMENT (1478-1558 B)  | 0x27FB        | [u32 reqId][u8 ok], reqId at [280]            | 4     | **real (T22)**, the whole payload persisted and served back by 0x27F9 |
| 0x2802 SDB_ACCOMPLISH_USER_ACHIEVEMENT (46 B)     | 0x2803        | 13-B header + the NEWLY accomplished records (43 B) or none (19 B) | 3 | **real (T22)**, persisted first-write-wins; the 19-B form now happens for the right reason |
| 0x2891 SDB_UPDATE_REPUTATION_INFO (78 B)          | 0x2892        | [u8 ok][u32 reqId] - ok FIRST                 | 1     | **real (T15)** |
| 0x286E SDB_ADD_TUTORIAL_SIMPLE_TIP (18 B)         | 0x286F        | [u32 reqId][u8 ok]                            | 4     | **real (T22)**, persisted and served back by 0x2873 |
| 0x2944 SDB_UPDATE_SEREN_GUIDE_INFO (22 B)         | 0x2945        | [u32 reqId][u32 playerId][u8 ok]              | 2     | **real (T22)**, persisted and served back by 0x2943 |
| 0x293C SDB_UPDATE_USER_DAILY_EVENT_COUNT (58 B)   | 0x293D        | [u32 reqId][u8 ok], reqId at [8]              | 1     | **real (T15)** |
| 0x293E SDB_UPDATE_GET_EXTRA_REWARD (20 B)         | 0x293F        | [u32 reqId][u8 ok]                            | 1     | **real (T15)** |
| 0x297B SDB_UPDATE_USER_ACTPOINT (22 B)            | 0x297C        | [u32 reqId][u8 ok]                            | 1     | real (master) |
| 0x2910 SDB_UPDATE_FATIGABILITY_POINT (23 B)       | 0x2911        | [u8 ok][u32 reqId]                            | 2     | real (the C# const is misnamed SDB_LOAD_FRIEND_INFO) |
| 0x2924 SDB_UPDATE_PASSIVITY_COOLTIME              | 0x2925        | [u32 reqId][u8 ok]                            | logout| real |
| 0x2936 SDB_CHECK_DAILY_ATTENDANCE                 | 0x2937        | [u32 reqId][u32 0x300][u32 0]                 | login+logout | real |
| 0x2930 SDB_UPDATE_HOLD_CHARACTER_STATUS           | 0x2931        | [u32 reqId][u8 ok][u8 0]                      | spawn | real |
| 0x27B3 SDB_UPDATE_DAILY_LIMIT_EP_EXP              | 0x27B4        | [u32 reqId][u8 ok]                            | spawn | real |
| 0x2736 SDB_END_START_QUEST_LIST (10 B)            | 0x2737        | [u32 reqId][u8 ok]                            | many  | real |
| 0x27CB SDB_UPDATE_USER_DATA (blob)                | 0x27CC        | [u32 reqId][u32 1]                            | spawn, zone change, logout | real, persisted (SQLite) |
| 0x2927 SDB_CANCEL_NPC_ARENA_BET (14 B)            | none          | fire-and-forget - the Arbiter handler has no SendToSession | periodic | **one-way (T15)**, in WorldReplayTable.OneWayFromWorld |
| 0x138D SA_ENTER_WORLD_FAIL (38 B)                 | 0x148D        | two AS_CACHE_DUNGEON_COOL_TIME_TO_WORLD pushes, then a re-sent 0x138E with the stored return point | relog into an instance | **real (T21)**, status/ENTER-WORLD-FALLBACK.md |

The login LOADS those writes feed are in `status/ACHIEVEMENTS.md`: `0x27F8` -> `0x27F9`,
`0x2872` -> `0x2873`, `0x2942` -> `0x2943` and `0x2867` -> `0x2868` are rebuilt from rows as of
T22 and byte-exact for a brand-new character and for one with progress. `0x288F` -> `0x2890`
(reputation) and `0x2908` -> `0x2909` (fatigability) are still dob's captured bytes: neither
layout is pinnable from the captures we have, and that file says exactly what would settle each.

Every row above is covered by a test: `Every_per_user_request_opcode_is_answered` parses this
table and fails the build if an opcode is neither in `DbProxyHandlers.IsHandledRequest` nor in
`WorldReplayTable.OneWayFromWorld` nor in the replay table. Keep the first column in the shape
`| 0xNNNN NAME ... | 0xNNNN |` (or `| ... | none |`) or the guard stops guarding.

### The "ECHO" replies are not byte-shifts — T15 corrected this

The earlier guess ("the request written back with `01` inserted after reqId, offsets shifted by 1")
is wrong, and it would have produced garbage. What actually happens is the same pattern in all four
of 0x2769, 0x272F, 0x278F and 0x2803: the Arbiter **re-serialises** the reply from its own writer
with a different, usually shorter header, and the request's variable-length blocks are copied
through verbatim behind it. The lengths line up by coincidence:

| reply             | request header | reply header | why the frame shrinks                       |
|-------------------|----------------|--------------|---------------------------------------------|
| DBS 0x2769        | 24 B           | 21 B         | `[u32 playerId]` -> `[u8 ok]`                |
| DBS 0x272F        | 30 B           | 29 B         | `[u32 playerId][u8][u8]` -> `[u8 ok][u32 questDbId]` |
| DBS 0x278F        | 34 B           | 23 B         | the skill fields are dropped; `[u8 ok][u8][u8]` added |
| DBS 0x2803        | 16 B           | 13 B         | `[u32 playerId]` -> `[u8 ok]`                |

Two fields in those copied-through blocks are NOT echoes and must be produced:

- **item DB ids** — an `ItemTransactionAtom` with operation 7 (insert) and id 0 gets a freshly
  allocated id at atom+16. Applies to 0x2769, 0x272F and 0x278F; `CloneAtomList` does it for all
  three.
- **the quest row id** in 0x272F at payload[25], on sqlType 22 only. World feeds it to
  `DBStartQuestContext::SetQuestDbId`, so two quests must never get the same one.

And one reply is a genuine filter, not an echo: 0x2803 returns only the achievements that were
NEWLY accomplished, so a repeat comes back as an empty 19-byte frame.

## Non-DB control traffic seen (no reply, or Arbiter-initiated)

- 0x1491 SA_UPDATE_USER_STATUS `[u64 handle][u32 inCombat][u8]` — no reply.
- 0x156F SA_UPDATE_MAKRER_END `[u32 off][u32 0][u32 pid]` — no reply, paired with 0x273B.
- 0x13B6 SA_UPDATE_DUNGEON_COOLTIME, 0x13C5 SA_ADD_DUNGEON_CHANNEL, 0x13C6 SA_REMOVE_DUNGEON_CHANNEL,
  0x1499 SA_SAVE_ETC_DATA_FOR_MOVE_WORLD, 0x15FA — no reply at all (T15 put all of these, plus
  0x1491, 0x156F and 0x2927, into `WorldReplayTable.OneWayFromWorld`). 0x162C is different: the
  Arbiter DOES answer it with 0x162D (capture seq 640), so it stays a normal replayed request.
- 0x156F is the one that matters most in that set: it arrives BETWEEN 0x273B and its 0x273C
  (capture seq 2994), so without the one-way entry the replay table makes 0x156F a request and
  hands it S_UPDATE_EXP_LEVEL's reply — the exact mis-attribution that wedges a user.
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

1. **Quests** — 0x272E is answered correctly as of T15, but nothing is stored. What is left:
   persist the 80-byte quest record keyed by the quest row id we hand out, and build 0x272D from
   it at login. The layouts are done (see the T15 comments in `DbProxyHandlers.cs`); the two open
   pieces are the meaning of the 80-byte record's fields and moving the quest-id sequence out of
   `DbProxyHandlers._nextQuestDbId` into `CharacterStore` so it survives a restart. The
   972 B / 3540 B forms are the same record plus reward `ItemTransactionAtom`s, which the
   inventory work (item 2) already knows how to store.
2. **Inventory** — 0x2768/0x2769 store (item single ops: add/remove/move/stack) + 0x27A4 built from
   stored items. Layout: `Handler_SDB_ITEM_SINGLE` + `FUN_…(pkt,0x27a4)` writer. Also 0x2813
   SDB_EQUIP_ITEM (not seen this session — equip via C_EQUIP_ITEM may route differently).
3. **Level/exp** — 0x273B store into the characters row (level shows in S_GET_USER_LIST).
4. **Achievements** (0x27FA/0x2802 -> 0x27F9), **reputation** (0x2891 -> 0x2890), **tutorial
   tips** (0x286E -> 0x2873), **seren guide** (0x2944 -> 0x2943), **daily event** (0x293C -> 0x293B),
   **fatigability** (0x2910 -> 0x2909) — small, same pattern each.
5. **Skills** — 0x278E is answered as of T15 (the reply is the fee atoms plus an empty
   SkillPeriodData list). Still open: where World reads learned skills from at login. It is not
   0x278F — World's handler only stores the skill-period list and two flags — so the learned set
   is either inside the world blob or comes from a load opcode we have not identified.
6. **Zone change / dungeon flow** (0x13BE/0x13C0/0x1499 + re-spawn) — not persistence, but the next
   live blocker once players leave Island of Dawn.
