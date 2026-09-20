# Persistence map — what World writes during play, and which login load it feeds

Source: `D:\packetlogs\cap_newchar.log` (real ArbiterServer, new character "Test" playerId 2,
Island of Dawn, 05:49–05:53: create, enter, quests, gathering, potions, kills, level-ups, skill
learning, item combine, tutorial dungeon enter/leave, logout). Condensed control listing in
`D:\packetlogs\cap_newchar_ctl.txt` (seq, dir, time, opcode, len, first 64 payload bytes).
Names from `data/dbproxy_opcodes.txt`. All frame offsets below are payload-relative.

Today TeraSharp persists the 15312-byte world blob (0x27CB), the item rows (T42/T44) and the rows the loads below are rebuilt from. Everything in the table below is
answered with a bare ack and forgotten, so on relog the login-time loads serve the captured "dob"
snapshot and the character's progress is gone. Making a row persistent = store the write, serve it
back in the matching load.

## Per-user writes seen during play (all DLMItems: World waits for the reply before the next one)

| write (W->A)                                      | reply (A->W)  | reply shape                                   | count | state |
|---------------------------------------------------|---------------|-----------------------------------------------|-------|-------|
| 0x272E SDB_SET_QUEST_INFO (116 / 972 / 3540 B)    | 0x272F        | 29-B header + 80-B quest record + reward atoms; ok at [24], allocated quest row id at [25] | ~30 | **real (T15)** |
| 0x273B S_UPDATE_EXP_LEVEL (46 B)                  | 0x273C        | [u32 reqId][u8 ok]                            | 12    | real (T6), writes level/exp and (T76) rest bonus to the row |
| 0x2768 SDB_ITEM_SINGLE (30 / 886 / 4310 B)        | 0x2769        | 21-B header + both atom lists, insert ids filled in | 8 | **real (T44)**, the atoms are now applied to the `items` rows, not only echoed |
| 0x278E SDB_USER_LEARN_SKILL (896 B)               | 0x278F        | 23-B header + the fee atoms + EMPTY SkillPeriodData list | 1 | **real (T15)** |
| 0x27FA SDB_UPDATE_USER_ACHIEVEMENT (1478-1558 B)  | 0x27FB        | [u32 reqId][u8 ok], reqId at [280]            | 4     | **real (T22)**, the whole payload persisted and served back by 0x27F9 |
| 0x2802 SDB_ACCOMPLISH_USER_ACHIEVEMENT (46 B)     | 0x2803        | 13-B header + the NEWLY accomplished records (43 B) or none (19 B) | 3 | **real (T22)**, persisted first-write-wins; the 19-B form now happens for the right reason |
| 0x2891 SDB_UPDATE_REPUTATION_INFO (78 B)          | 0x2892        | [u8 ok][u32 reqId] - ok FIRST, ok=0 for an UpdateType that is neither 1, 2 nor 4 | 1 | **real (T26)**, the 52-B record persisted and served back by 0x2890 |
| 0x286E SDB_ADD_TUTORIAL_SIMPLE_TIP (18 B)         | 0x286F        | [u32 reqId][u8 ok]                            | 4     | **real (T22)**, persisted and served back by 0x2873 |
| 0x2944 SDB_UPDATE_SEREN_GUIDE_INFO (22 B)         | 0x2945        | [u32 reqId][u32 playerId][u8 ok]              | 2     | **real (T22)**, persisted and served back by 0x2943 |
| 0x293C SDB_UPDATE_USER_DAILY_EVENT_COUNT (58 B)   | 0x293D        | [u32 reqId][u8 ok], reqId at [8]              | 1     | **real (T15)** |
| 0x293E SDB_UPDATE_GET_EXTRA_REWARD (20 B)         | 0x293F        | [u32 reqId][u8 ok]                            | 1     | **real (T15)** |
| 0x297B SDB_UPDATE_USER_ACTPOINT (22 B)            | 0x297C        | [u32 reqId][u8 ok]                            | 1     | real (master) |
| 0x2910 SDB_UPDATE_FATIGABILITY_POINT (23 B)       | 0x2911        | [u8 ok][u32 reqId]                            | 2     | **real (T26)**, carries a DELTA added to the ACCOUNT total, served back by 0x2909 (const `SDB_FATIGABILITY_UPDATE`; the old `SDB_LOAD_FRIEND_INFO` name is a documented alias) |
| 0x2924 SDB_UPDATE_PASSIVITY_COOLTIME              | 0x2925        | [u32 reqId][u8 ok]                            | logout| real |
| 0x28C1 SDB_UPDATE_LEFT_COOLTIME_PREMIUM_SLOT      | 0x28C2        | [u32 DlmId][u8 ok]                            | play  | real |
| 0x28BD SDB_LOAD_PREMIUM_SLOT_LEFT_COOLTIME        | 0x28BE        | [u32 off=19][u32 bytes][u32 DlmId][u8 ok]+recs | login | real |
| 0x2936 SDB_CHECK_DAILY_ATTENDANCE                 | 0x2937        | [u32 reqId][u32 0x300][u32 0]                 | login+logout | real |
| 0x2930 SDB_UPDATE_HOLD_CHARACTER_STATUS           | 0x2931        | [u32 reqId][u8 ok][u8 0]                      | spawn | real |
| 0x27B3 SDB_UPDATE_DAILY_LIMIT_EP_EXP              | 0x27B4        | [u32 reqId][u8 ok]                            | spawn | real |
| 0x2736 SDB_END_START_QUEST_LIST (10 B)            | 0x2737        | [u32 reqId][u8 ok]                            | many  | real |
| 0x27CB SDB_UPDATE_USER_DATA (blob)                | 0x27CC        | [u32 reqId][u32 1]                            | spawn, zone change, logout | real, persisted (SQLite) |
| 0x2927 SDB_CANCEL_NPC_ARENA_BET (14 B)            | none          | fire-and-forget - the Arbiter handler has no SendToSession | periodic | **one-way (T15)**, in WorldReplayTable.OneWayFromWorld |
| 0x13B6 SA_UPDATE_DUNGEON_COOLTIME (74 B)          | none          | one-way; the 52-B CoolTimeElem is persisted | 1 | **real (T25)**, served back by 0x2868 and 0x148D |
| 0x13B7 SA_UPDATE_DUNGEON_CLEAR_COUNT (22 B)       | none          | one-way; stored, not served (ClearCountElem layout unobserved) | 0 | **real (T25)** |
| 0x13BD SA_DELETE_DUNGEON_COOLTIME (18 B)          | none          | one-way; clears the cool time, keeps the clear count | 0 | **real (T25)** |
| 0x138D SA_ENTER_WORLD_FAIL (38 B)                 | 0x148D        | two AS_CACHE_DUNGEON_COOL_TIME_TO_WORLD pushes, then a re-sent 0x138E with the stored return point | relog into an instance | **real (T21)**, status/ENTER-WORLD-FALLBACK.md |

The login LOADS those writes feed are in `status/ACHIEVEMENTS.md`: `0x27F8` -> `0x27F9`,
`0x2872` -> `0x2873` and `0x2942` -> `0x2943` are rebuilt from rows as of T22, and
`0x2867` -> `0x2868` from `dungeon_cooldowns` as of T25 (`status/DUNGEON-COOLTIME.md`); all are
byte-exact for a brand-new character and for one with progress. `0x288F` -> `0x2890`
(reputation) and `0x2908` -> `0x2909` (fatigability) followed in T26, pinned from the decompile
rather than the captures — `status/REPUTATION-FATIGABILITY.md`. Fatigability is the one load
keyed on the ACCOUNT, not the character.

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
4. **Achievements** (0x27FA/0x2802 -> 0x27F9), **tutorial tips** (0x286E -> 0x2873), **seren
   guide** (0x2944 -> 0x2943) — done in T22; **reputation** (0x2891 -> 0x2890) and
   **fatigability** (0x2910 -> 0x2909) — done in T26. Still open: **daily event**
   (0x293C -> 0x293B).
5. **Skills** — 0x278E is answered as of T15 (the reply is the fee atoms plus an empty
   SkillPeriodData list). Still open: where World reads learned skills from at login. It is not
   0x278F — World's handler only stores the skill-period list and two flags — so the learned set
   is either inside the world blob or comes from a load opcode we have not identified.
6. **Zone change / dungeon flow** (0x13BE/0x13C0/0x1499 + re-spawn) — not persistence, but the next
   live blocker once players leave Island of Dawn.

## Inventory — T44, the bag is rows in the same table

`SDB_USER_LOAD_INVENTORY` 0x27A2 -> `0x27A3` + `0x27A4` was served from
`data/starter_inventory.bin` on every login until T44, so a relog handed the character the same
six starter items whatever they had done. Now the kit is written into `items` rows the first time
the inventory is loaded and the reply is rebuilt from those rows; for a character who has not
touched anything it is still the captured 3235 bytes, byte for byte (`T44_seeded_inventory_
rebuilds_byte_identical`).

The bag is INVEN_TYPE 0 and the worn slots are 14, in the same table and keyed the same way as the
warehouse pockets; the inventory load is every pocket that is not a warehouse
(`inven_type NOT IN (1,3,9,12)`), ordered by pocket then slot - the order the capture lists them in.

Four messages carry `ItemTransactionAtom`s and all four now land in those rows, always applied from
the **reply** (the copy with the allocated item DB ids in it, so the id World is handed and the id
the row gets are the same one):

| message | atom list ref in the reply | what it does to the rows |
|---|---|---|
| 0x2768 SDB_ITEM_SINGLE | `[0]` and `[8]` (two lists, A then B) | pickups, drops, moves, stack changes |
| 0x272E SDB_SET_QUEST_INFO | `[8]`, behind the quest record | quest rewards |
| 0x278E SDB_USER_LEARN_SKILL | `[0]` | the fee and anything it consumes |
| 0x274C/0x274E warehouse transfer | `[0]` | bag <-> bank |

The five atoms in `data/cap_item_single.bin` decode cleanly against the source/destination triples
T42 pinned from the World decompile - seq 2072 is `op 7, id 0, tpl 81251, (owner 2, pocket 0, slot 4),
delta 1`, and seq 2211 is the 6+11 / 6+11 / 7 combine - which is the first time those offsets have
been checked against captured bytes rather than the decompile alone.

Ops modelled: 2 (amount), 3 (move to an empty slot), 6 (detach, a deliberate no-op - the paired 11
does the delete), 7 (insert), 9 (character money, not an item row), 11 (delete), 36 (swap two
occupied slots), and the warehouse ops 13/14/15/17. Anything else is **echoed but not applied** and
logged; guessing at an op's semantics would corrupt the row.

## Warehouse — T42, answered from the item rows

The same guarded shape as the table at the top of this file; `Every_per_user_request_opcode_is_answered`
reads both. **No capture contains a warehouse frame** — both A->W taps are one login and nobody
opened a bank — so every offset here comes from the Arbiter's PDL dumpers cross-checked against
the writers, and each handler's min-length guard lands exactly on the end of its last field.
Layouts, atom parsing and the builders are in `World/WarehouseHandlers.cs`;
`status/MAIL-WAREHOUSE.md` section 4 has the derivation.

| write (W->A)                                      | reply (A->W)  | reply shape                                   | count | state |
|---------------------------------------------------|---------------|-----------------------------------------------|-------|-------|
| 0x274A SDB_VIEW_WAREHOUSE (34 B)                 | 0x274B        | 39-B header + N x 536-B ItemData; ViewSize is a hard-coded 0x48 | 0 | **real (T42)**, rebuilt from the `items` rows |
| 0x274C SDB_STORE_WAREHOUSE (63 B)                | 0x274D        | 25-B header + the atoms echoed with allocated ids | 0 | **real (T42)**, atoms applied to `items` |
| 0x274E SDB_GET_WAREHOUSE (62 B)                  | 0x274F        | same 25-B shape                               | 0     | **real (T42)** |
| 0x277F SDB_CHANGE_WAREHOUSE_POS (34 B)           | 0x2780        | same 25-B shape                               | 0     | **real (T42)** |
| 0x27C9 SDB_PAY_WAREHOUSE_COMMISION (26 B)        | 0x27CA        | [u32 DlmId][u8 ok][u32 error] - the fee is disabled in this build | 0 | **real (T42)** |
| 0x27E0 SDB_CLEAR_WAREHOUSE (18 B)                | 0x27E1        | [u32 DlmId][u8 ok]                            | 0     | **real (T42)** |
| 0x27E2 SDB_WAREHOUSE_AUTO_SORT (26 B)            | 0x27E3        | the request's four fields echoed + [u32 err][i64 commision] | 0 | **real (T42)** |
| 0x283F SDB_INCREASE_WAREHOUSE_SIZE (34 B)        | 0x283E        | [u8 ok][u32 DlmId] - **0x283E, not 0x2840**; both handlers share the send helper FUN_1407ad2e0 | 0 | **real (T42)** |
| 0x2754 SDB_MOVE_WAREHOUSE_ITEM (38 B)            | none          | the real handler is a ten-line stub that sends nothing; in WorldReplayTable.OneWayFromWorld | 0 | **real (T42)**, matched deliberately |

Two things that will hang a user if they are ever "fixed":

- **0x283F is answered with 0x283E.** `DBS_INCREASE_WAREHOUSE_SIZE` 0x2840 exists in the opcode
  table and has a dumper, and nothing in the binary ever sends it.
- **0x2754 must stay unanswered.** It is sealed in `WorldReplayTable.OneWayFromWorld` so the replay
  table cannot hand it somebody else's reply.

## Mail — Arbiter-owned, both halves implemented

T42 added the three client packets the Arbiter answers itself and the `parcels` table behind them
(`Handlers/ParcelHandlers.cs`); **T45 added the six `SDB_*_PARCEL` requests World sends**
(`World/ParcelDbHandlers.cs`) — they are tabled in §C.2 at the bottom of this file.

- Implemented, client-side: `C_SHOW_PARCEL_MESSAGE` 0xFA59 -> `S_SHOW_PARCEL_MESSAGE` 0xABD3,
  `C_PARCEL_READ_RECV_STATUS` 0xE292 -> `S_PARCEL_READ_RECV_STATUS` 0xF26E (byte-exact against
  `cap_newchar_client.log` frame 312), `C_PARCEL_REPORT` 0xC09A -> `S_PARCEL_REPORT` 0x73FC.
  The 13-byte 0xF26E frame is also pushed unprompted at `C_LOAD_TOPO_FIN`, which is what the real
  Arbiter does from `User::OnLoadTopoFin`.
- Answered as of T45: `0x2777` LIST, `0x2779` MAKE, `0x277B` RECV, `0x277D` RECV_EX, `0x2781`
  RETURN, `0x2811` DELETE - each by the next opcode up (§C.2). Before T45 the first mailbox a live
  player opened produced `no replay for 0x2777` and head-blocked that user's DLM queue; the
  12-phantom-row mailbox in the 2026-09-14 live session was the replay table answering instead.
- `C_RETURN_USER_GIFT` 0xF7FD is **not** a parcel packet - it is the returning-player reward claim
  (-> AS 0x15C0). Return-to-sender is `C_RETURN_PARCEL` 0xF294, handled by World.
- Warehouse moves do **not** ride `SDB_ITEM_SINGLE` 0x2768 - that handler binds only the bag
  inventory, so an atom naming a warehouse pocket resolves to NULL. The pocket id is
  `enum INVEN_TYPE` and is what picks the container (0 bag, 1 account bank, 3 guild, 9 character
  bank, 12 style); `status/MAIL-WAREHOUSE.md` section 6 has the proof.

## The Arbiter Contract family — brokered, T60 (was: one-way, T47)

`0x2809` and `0x280E` turned up in the first two-client test (58-66 B each) and are the two-party
interactions the Arbiter brokers, which is why one player never sees them. **None of the family
carries a DlmId** — the dumpers name `ContractorDbId`, `ContractType`, `ContractId` and nothing
else — so an unanswered one cannot head-block the per-user DB queue the way a missing `DBS_` reply
does.

**T47 sealed the four W→A opcodes in `WorldReplayTable.OneWayFromWorld`; T60 unsealed them.** The
seal was based on a wrong reading, and it is what made a party invite between two in-world players
do nothing at all on 2026-09-15: `Handler_SDB_FETCH_THROUGH_ARBITER_CONTRACT` sends nothing, but it
dispatches on `ContractType` into one of four `FetchWork` objects and **those** send —
`FetchWork::ResponseFailure` / `::ResponseSuccess` (`FUN_1409cde70`) emit `0x280A`, and the success
path fans `0x280B` out to every opponent. T47 stopped at the handler. Guild creation is
`ContractType` 10 and is gated on being in a party, so the same seal blocked guilds too.

The four are now gated off `WorldBridge.HandleFrame`'s `default:` arm into `World/ContractBroker.cs`,
the way `PartyWiring` and `GuildWiring` are — a **sealed** opcode never reaches a gate, so the two
cannot both be true. Full flow, layouts and citations: `status/CONTRACT-DESIGN.md`.

| opcode | name | handler | ours does |
|---|---|---|---|
| `0x2809` | SDB_FETCH_THROUGH_ARBITER_CONTRACT | `Arb_part_063.c:7017` | `OnFetch` — brokers types 4/5, refuses 10 and 0x23 with ErrorNo 2 |
| `0x280C` | SDB_ASK_THROUGH_ARBITER_CONTRACT | `Arb_part_063.c:810` | `OnAsk` — records `CanContract`, sends nothing (38-line handler) |
| `0x280D` | SDB_SEND_BEGIN_THROUGH_ARBITER_CONTRACT | `Arb_part_064.c:2732` | `OnSendBegin` — only the CLIENT packet `S_BEGIN_THROUGH_ARBITER_CONTRACT` (0x7E3F) |
| `0x280E` | SDB_SEND_END_THROUGH_ARBITER_CONTRACT | `Arb_part_064.c:2983` | `OnSendEnd` — `S_END` (0xC7D7) to each participant, then a `0x280F` FAN-OUT, one frame each |

The A→W half is ours to send and never arrives: `0x280A` DBS_FETCH (the verdict, with the AskList
and an ErrorNo), `0x280B` DBS_ASK (one per opponent, both names as **offset-only** wstr refs),
`0x280F` DBS_SEND_END, `0x2810` DBS_REPLY. Plus the client's `C_REPLY_THROUGH_ARBITER_CONTRACT`
(0x5B7C), which is read by absolute offset — **its `.def` is wrong** and must not be used.

Request shape, both observed ones identical (guard `0x21 < len`, min frame 34):
`[6] Param ref` `[14] list ref` `[22] u32 ContractorDbId` `[26] u32 ContractType` `[30] u32 ContractId`.

`0x280E` is the one worth understanding: it walks the `AskUserList` and, for each other
participant that is in world, pushes `DBS_SEND_END_THROUGH_ARBITER_CONTRACT` to **that user's**
World session carrying `[ContractorDbId][ContractType][ContractId][thatUserDbId]`. A fan-out, not
an answer.

**Still refused on purpose:** `ContractType` 10 (guild creation) and 0x23 (trade broker deal).
`CreateGuildFetchWork` runs the guild-name restriction check before anything else and
`TradeBrokerOpenDealFetchWork` needs the broker's own deal state; inventing a verdict would tell
World a contract was brokered that never was. One open guess remains — whether the target is named
by a db id in `FetchDataList` or by a name in `Param`; both readings are tried and
`status/CAPTURE-PLAN.md` A.2.1 is the step that settles it.

The first cells above stay backticked on purpose: the coverage guard
(`Every_per_user_request_opcode_is_answered`) parses rows whose first cell starts with a bare `0x`,
and these opcodes are answered by a WorldBridge gate rather than by the allow-list, `OneWayFromWorld`
or a replay entry — none of the guard's three arms. A parseable row here would fail it.


## Guilds — the boot load, T51

Guild state is persisted (the opposite of parties — `GUILD-DESIGN.md` §3 and §4) but it does not
travel as per-user DB messages, so none of this is a DLMItem and none of it can head-block a
user. There is exactly one request: World asks once, at boot.

| request (W->A) | reply | shape | count | feeds |
|---|---|---|---|---|
| 0x27CF SDB_INIT_GUILD (0 B)                       | 0x27ED        | zero-length request; the answer is a SEQUENCE per guild, ending in DBS_INIT_GUILD_DATA with Success = 0 | 1 per World boot | **real (T51)**, from the `guilds` table |

The answer, per guild, is `GUILD-DESIGN.md` §4.3's sequence — `0x27ED` (Success = 1) ->
`0x27D0 DBS_INIT_GUILD_GROUP` -> `0x27D1 DBS_INIT_GUILD_MEMBER` (31 members per frame) ->
`0x27D2 DBS_INIT_GUILD_PERK_LIST` -> `0x27D3 DBS_LOAD_GUILD_COMPLETE` — then one final `0x27ED`
with `Success = 0`. With no guilds in the DB that collapses to the single terminator, which is
`GuildPackets.BuildEmptyDbsInitGuildData()`.

Before T51 the terminator came from the replay table, i.e. from `arb_world.log`, which meant
every World boot re-sent two bytes of the real Arbiter's uninitialised stack from inside
`GuildData` (`0xB379` at blob `0x024A`; `GUILD-DESIGN.md` §2.1). Adding `0x27CF` to
`DbProxyHandlers.IsHandledRequest` retires that entry outright — `TryHandle` returns true before
`WorldBridge` ever reaches the replay table.

The four one-way `AS_`/`SA_` guild frames are not in this table because none of them is a
DB-proxy request: they are in `GUILD-DESIGN.md` §4.1 and §4.2.

## Coverage — every opcode `TryHandle` answers, T54 reconciliation

The tables above grew one subsystem at a time and drifted: 27 of the 59 opcodes in
`DbProxyHandlers.IsHandledRequest` had no row, and 11 of those were not mentioned anywhere in this
file. `Every_per_user_request_opcode_is_answered` never caught it because it checks the
implication in one direction only — every row in this file must be answered, not every answer must
have a row. The three tables below close the gap; together with the ones above they now cover all
59.

### C.1 Login-time and spawn-time loads answered from rows

These are per-user DLM items exactly like the writes at the top of this file: an unanswered one
head-blocks that character's queue for the life of the World process.

| request (W->A)                                   | reply (A->W)  | reply shape                                   | count | state |
|---------------------------------------------------|---------------|-----------------------------------------------|-------|-------|
| 0x2711 SDB_USER_ENTERWORLD                        | 0x2738        | the 15312-B world blob, or the 13-B not-found form | 1 per enter | **real**, from `characters.world_blob` |
| 0x272C SDB_QUEST_LIST                             | 0x272D        | active quests in list 1, completed ids in list 2 | 1 per enter | **real (T17)**, from `quests` |
| 0x27A2 SDB_USER_LOAD_INVENTORY                    | 0x27A3        | then 0x27A4, N x 536-B ItemData from the rows | 1 per enter | **real (T44)**, from `items` |
| 0x27F8 SDB_USER_ACHIEVEMENT                       | 0x27F9        | the stored 0x27FA payload + the accomplished list, reqId@304 | 1 per enter | **real (T22)** |
| 0x2872 SDB_TUTORIAL_SIMPLE_TIP                    | 0x2873        | 45 B, reqId@8, tips in first-seen order       | 1 per enter | **real (T22)** |
| 0x2942 SDB_SEREN_GUIDE                            | 0x2943        | 65 B, reqId@8, six fixed rows                 | 1 per enter | **real (T22)** |
| 0x288F SDB_REPUTATION_LIST                        | 0x2890        | 65 B, reqId@9, the stored 52-B records        | 1 per enter | **real (T26)** |
| 0x2908 SDB_FATIGABILITY_LIST                      | 0x2909        | 45 B, reqId@9 — keyed on the ACCOUNT, not the character | 1 per enter | **real (T26)** |
| 0x2867 SDB_LOAD_2867                              | 0x2868        | three lists; list 0 is the stored cool times  | 1 per enter | **real (T25)**, from `dungeon_cooldowns` |
| 0x2869 SDB_LOAD_2869                              | 0x286A        | a 0x15E0 push first, then an empty list + [ok][reqId] + the SAME reset time | 1 per enter | real |
| 0x290C SDB_LOAD_290C                              | 0x290D        | four pushes first (0x15B1, 0x2847, 0x1440, 0x143E), then [01][reqId] | 1 per enter | real |
| 0x27B9 SDB_EP_PERK                                | 0x27BA        | 113 B, reqId@8, static                        | 1 per enter | real |
| 0x27B3 SDB_LOAD_WORLD_EVENT                       | 0x27B4        | [u32 reqId][u8 ok]                            | spawn | real |
| 0x2897 SDB_DAILY_QUEST                            | 0x2898        | [u32 reqId][u8 ok], reqId@16                  | login | real |
| 0x2899 SDB_DAILY_QUEST_SEED                       | 0x289A        | [u32 reqId][u8 ok], reqId@8 — plus the empty 159-B 0x272D that makes World take the seed branch | login | real |
| 0x295C SDB_RESULT_CITY_WAR                        | 0x295D        | request+16 / +20 echoed; 0x15ED goes with it  | World start | real |

### C.2 Mail — the six `SDB_*_PARCEL` pairs (T45 answered them; this file still said they were open)

Every one is a DLM item. Before T45 nothing answered `0x2777`, so the first mailbox a live player
opened produced `no replay for 0x2777` and head-blocked that character — which is exactly what the
12-phantom-row mailbox in the 2026-09-14 live session was.

| request (W->A)                                   | reply (A->W)  | reply shape                                   | count | state |
|---------------------------------------------------|---------------|-----------------------------------------------|-------|-------|
| 0x2777 SDB_LIST_PARCEL                            | 0x2778        | 35-B empty form byte-exact from the decompile; MaxPage = 1, ParcelCount = 0 | 1 per mailbox open | **real (T45)**, from `parcels` |
| 0x2779 SDB_MAKE_PARCEL                            | 0x277A        | [DlmId][ok] + the new parcel id               | per send | **real (T45)** |
| 0x277B SDB_RECV_PARCEL                            | 0x277C        | the claim step, atoms applied to `items`      | per claim | **real (T45)** |
| 0x277D SDB_RECV_PARCEL_EX                         | 0x277E        | the claim-all step                            | per claim-all | **real (T45)** |
| 0x2781 SDB_RETURN_PARCEL                          | 0x2782        | [DlmId][ok]                                   | per return | **real (T45)** |
| 0x2811 SDB_DELETE_PARCEL                          | 0x2812        | [DlmId][ok]                                   | per delete | **real (T45)** |

`ParcelDataNoMsg`'s 0x9e8-byte interior is still unknown — no capture contains one — so
`ParcelCount = 0` is the only honest answer a non-empty inbox could get today.
`status/MAIL-WAREHOUSE.md` §9 and `status/CAPTURE-PLAN.md` §B.4.

### C.3 Session and handshake requests that are not per-user DB items

These carry no DlmId, so an unanswered one cannot head-block a user's queue — but three of them
block something else entirely, which is why they are handlers and not replay entries.

| request (W->A)                                   | reply (A->W)  | reply shape                                   | count | state |
|---------------------------------------------------|---------------|-----------------------------------------------|-------|-------|
| 0x147D AS_PROMOTION_LIST_REQ                      | 0x1484        | then 23 x 0x147E promotion records re-stamped with the current UTC time, then 0x1480 | 1 per World boot | **real**, stale timestamps crash `PromotionController::NewPromotion` for a level-1 character |
| 0x13F2 DSA_DUNGEON_TIMELINE_OPEN_INFO             | 0x1581        | World's own open-info list echoed back, one push per dungeon | 1 per World boot | real |
| 0x1463 SA_LEARN_ALL_CREST_ACQUIRABLE              | 0x1464        | every requested (crestId, value) echoed as learned | first login of a class with level-1 crests | real |
| 0x1562 SA_CLEAR_BATTLE_FIELD_ENTER_COUNT          | 0x1563        | [u8 ok][u32 reqId], reqId@8                   | daily reset mid-session | real |
| 0x13BE SA_REQUEST_ENTER_DUNGEON                   | 0x13BF        | the 215-B request echoed, struct padding zeroed | per zone change | real, live-verified |
| 0x13C0 SA_RESPONSE_ENTER_DUNGEON                  | 0x13C1        | the same echo rule                            | per zone change | real, live-verified |

### C.4 Keeping this honest

`Every_per_user_request_opcode_is_answered` reads the first column of every table in this file and
fails the build for an opcode nothing answers. It does **not** check the other direction, so a
handler added without a row here is invisible to it. Until that second check exists, the rule is:
**adding an opcode to `DbProxyHandlers.IsHandledRequest` means adding a row to this file in the
same commit.** The reconciliation that produced C.1-C.3 is three lines of Python — extract the
`case` labels from `IsHandledRequest`, extract the leading `0xNNNN` of every table row here, and
diff the two sets.


## Trade broker — T55, the five DLM answers

Five of the seven `SDB_TRADE_BROKER_*` requests carry a **DlmId**, so each is a per-user DLMItem
and an unanswered one head-blocks that character's DB queue for the life of the World process.
**The broker window sends the first of them the moment it opens**, so before T55 walking up to a
broker NPC wedged the character — the same failure the mailbox had before T45, and for the same
reason: no handler, and no capture for the replay table to fall back on.

| request (W->A)                                    | reply (A->W)  | reply shape                                   | count | state |
|---------------------------------------------------|---------------|-----------------------------------------------|-------|-------|
| 0x2817 SDB_TRADE_BROKER_REGISTER_ITEM             | 0x2818        | frame 0x13: `[u32 off=0x13][u32 len=0][u32 DlmId][u8 Success=0]` | per listing | **refusal (T55)**, no listings table yet |
| 0x2819 SDB_TRADE_BROKER_UNREGISTER_ITEM           | 0x281A        | frame 0x1F: two empty refs, DlmId, Step echoed, Success=0 | per delist | **refusal (T55)** |
| 0x281B SDB_TRADE_BROKER_CALC_SOLD_ITEM            | 0x281C        | frame 0x1F, same shape                        | per collect | **refusal (T55)** |
| 0x281D SDB_TRADE_BROKER_CALC_BOUGHT_ITEM          | 0x281E        | frame 0x1F, same shape                        | per collect | **refusal (T55)** |
| 0x281F SDB_TRADE_BROKER_BUY_IT_NOW                | 0x2820        | frame 0x1F, same shape                        | per purchase | **refusal (T55)** |

`SDB_TRADE_BROKER_START_DEAL` (0x2821) and `_CANCEL_DEAL` (0x2824) carry **no DlmId**, so an
unanswered one cannot head-block anyone; they are left alone until there is a deal to have.
`DBS_TRADE_BROKER_ACCEPT_DEAL` (0x2823) has no `SDB_` partner at all — it is an Arbiter->World
push, not a reply.

### Why a refusal is the right answer, and what would be wrong

World does not need the answer to be yes. It needs an answer carrying that request's own DlmId,
because `DLMExistManager::Find` matches on the id — a reply with the wrong one is no better than
none. `Success = 0` means the item stays where it was and the client shows the failure, which is
true: there is no listings table (`status/BROKER-DESIGN.md` §7, waiting on a capture).
`Success = 1` would be the wrong thing, because it tells World an item moved.

Two details the writers settle, both of which a hand-written empty form gets wrong:

* **The ref offset slots are backpatched unconditionally**, before the emptiness check, so an
  empty answer carries `0x13` (or `0x1F`) in the offset and `0` in the length — not two zeros.
  Same convention as `DBS_INIT_GUILD_DATA` and the guild init arrays.
* **`ItemBinary` is an `ItemTransactionAtom` array**, stride `0x358` — the same 856-byte record
  the warehouse and item paths already use, not the `ItemData` listing T53 guessed at. The proof
  is the copy loop's stride and the `count * 0x358` byte-length slot in all five writers.

**`Step` is echoed, never invented.** Four of the five carry a multi-stage commit step whose
values were never traced; echoing the one we were given is the only safe answer.

---

## T62 — three live leftovers

Two client opcodes the Arbiter owns and one W→A frame that turned out not to be what it looked like.
First cells stay backticked: none of these is a per-user DLM request, so a parseable row here would
fail the coverage guard (`Every_per_user_request_opcode_is_answered`).

| opcode | name | direction | shape | answered by |
|---|---|---|---|---|
| `0x5639` | C_ASK_INTERACTIVE (22073) | C→A | `[04] u16 nameOff` `[06] i32 AskType` `[0A] i32 TargetPlanetId` + wstr; guard 0x0E | `ArbiterClientHandlers.OnAskInteractive` → `0x85C8` |
| `0x85C8` | S_ANSWER_INTERACTIVE (34248) | A→C | `[04] u16 nameOff=24` `[06] AskType` `[0A] TargetTemplateId` `[0E] TargetLevel` `[12] u8 TargetInParty` `[13] u8 TargetInGuild` `[14] TargetPlanetId` + wstr | — |
| `0xA5DA` | C_WATCHED_MOVIES (42458) | C→A | header only; guard 4, reads nothing | `OnWatchedMovies` → `0x97A4` |
| `0x97A4` | S_WATCHED_MOVIES (38820) | A→C | `[04] u16 count` `[06] u16 firstOff`, entries `[u16 here][u16 next][u32 movieId]` | — |
| `0x7B75` | C_FINDNAME (31605) | C→A | `[04] u16 queryOff` `[06] i32 FindType` + wstr; guard 10, +2 B per keystroke | `OnFindName` → `0xF95D` |
| `0xF95D` | S_FINDNAME (63837) | A→C | `[04] u16 queryOff=12` `[06] u16 resultOff` `[08] i32 FindType` + query wstr + joined-matches wstr | — |
| `0x27FE` | SDB_ADD_PVP_USER_LOG | W→A | 14 B: `[06] u32 killerDbId` `[0A] u32 victimDbId`; guard `param_3 < 0xE` | nothing — sealed in `OneWayFromWorld` |

**Persistence.** `watched_movies(character_id, movie_id)` — the list `S_WATCHED_MOVIES` serves back, so
the intro cutscene stops replaying. The real Arbiter keeps this per ACCOUNT
(`Account::CachedWatchedMoviesWithLock` → `spLoadUserWatchedMovies`, merged with the `ReplayMovieData`
sheet); per character is the row TeraSharp owns, and `CharacterStore.AddWatchedMovie` is the one place
to change if two characters on an account should share it.

**Corrections to the brief.** `0x27FE` is not the cinematic flag — it is the PvP kill log, and the
cutscene replay is `C_WATCHED_MOVIES` going unanswered. `C_FINDNAME` matches are served from the
`characters` table by prefix; the real `User::FindNameLog` searches the friend list, then a recent-name
log, then the guild roster, all capped at ten (`list.size() < 10`, re-tested in each pass). The one-char
separator between matches (`DAT_140b41838`) is untyped in the decompile — `FindNameSeparator` is a comma
and is the single guessed byte in T62.

T65 adds one row to the item path: `SDB_ITEM_TRADE` (0x276A) -> `DBS_ITEM_TRADE` (0x276B) applies a
completed player trade's `ItemTransactionGiveTake` records (0x238 each, two lists, owner then target)
to `items` and `characters.money`, the same way 0x2768 does for a single player. Parcel gold now
leaves `parcels.money` and lands on `characters.money` when the mail is claimed
(`SDB_RECV_PARCEL` / `SDB_RECV_PARCEL_EX`); before T65 it was stored and never paid.
`GetCharacterByName` is `COLLATE NOCASE`, so a mail addressed to `Two` reaches `two`.

T69 adds three rows. `SDB_CREATE_GUILD2` (0x27D4) → `AS_GUILD_JOINED` (0x2866) per joining member
then `DBS_CREATE_GUILD2` (0x27D5) writes the `guilds`, `guild_members` and `guild_groups` rows for a
new guild; before T69 it was unanswered and wedged the founder. `SDB_LOAD_REFER_A_FRIEND_LIST`
(0x28B0 → 0x28B1) and `SDB_LOAD_INVITE_FRIEND` (0x28B7 → **0x28B6**, the reply opcode is
request-minus-one) are per-login loads answered from nothing — we keep no refer-a-friend state —
but both echo a DlmId, so they were promoted out of the replay table where a replayed constant
would have handed World a stale id. Warehouse rows now also follow TS op 0x10, the withdraw twin of
0x0E.

---

## T76 - five columns the lobby and the friend panel were reading as zero

No new opcode. Three packets were shipping real fields as zeroes because nothing stored the
values; `characters` gained five columns (`AddColumnIfMissing`, so an existing db migrates).

| column | written by | read by |
|---|---|---|
| `last_login` | `SocialHandlers.RegisterChat` -> `NotifyFriendsOfState(state 0)` -> `CharacterStore.StampLogin` | `S_FRIEND_LIST.lastOnline`, `S_UPDATE_FRIEND_INFO.lastOnline` (both = now - this, in seconds) |
| `last_world`, `last_guard`, `last_section` | `ArbiterClientHandlers.OnVisitNewSection` -> `CharacterStore.SetLastSection` | `S_FRIEND_LIST`, `S_UPDATE_FRIEND_INFO`, `S_GET_USER_LIST` (worldId/guardId/sectionId) |
| `rest_bonus` | `DbProxyHandlers.OnUpdateExpLevel` -> `CharacterStore.SetRestBonus` | `S_GET_USER_LIST.restBonusXp` |

### Rest bonus: 0x273B frame offset 26 is the only source

`SDB_UPDATE_EXP_LEVEL` (0x273B) carries `[26] i64 restBonusPoint`, and the real Arbiter feeds it
to `User::UpdateUserExpAndRestBonusPoint` (level < 1) or `User::UpdateUserLevel` (level >= 1),
both of which persist it through `dbo.spSetRestBonusPoint` / `spUpdateRestBonusPoint`. Nothing
else on the link mentions rested xp - there is no `SDB_*REST*` opcode at all. Before T76 the
handler read the field and only logged it.

### `S_GET_USER_LIST`, decoded against cap_social_client frame 11

A real 1169-byte list of two characters. The def is `S_GET_USER_LIST.18` (header:
`majorPatchVersion >= 95 && majorPatchVersion < 101`) and its element stride is 472 bytes, which
the frame confirms - element one at packet 35, element two at 601.

| element offset | field | dob | Test |
|---|---|---|---|
| +52 / +56 / +60 | worldId, guardId, sectionId | 1, 1, 1 | 1, 25, 599001 |
| +64 | lastLogoutTime (int64) | 1789387775 | 1789393881 |
| +81, +155 | deleteRemainSec, banRemainSec | -1789393912 | -1789393912 |
| +287 | restBonusXp (int64) | 419 | 0 |
| +295 | maxRestBonusXp (int64) | 419 | 1523 |

`lastLogoutTime` is **absolute unix seconds**, not an elapsed count: the two remain-seconds fields
are `0 - now`, which pins the capture at unix 1789393912 and makes `Test`'s logout 31 seconds old.
That is also what distinguishes it from `S_FRIEND_LIST.lastOnline`, which IS elapsed - the two
packets carry different fields (`status/FRIENDS.md` section 10).

`Test`'s `(1, 25, 599001)` here is byte-identical to the same character's location in
`S_FRIEND_LIST` frames 1417/1437, which is what ties the three packets to one stored trio.

### Still open

- **`maxRestBonusXp` is per LEVEL** (419 at level 1, 1523 at level 3) and comes from
  `RestBonusDataSheet` (`RestBonusDataSheet::Load`, Arb_part_006.c:5206; the writer reads a field
  named `MaxRestBonusPoint`, Arb_part_021.c:4532). TeraSharp does not load that sheet, so T76
  leaves the existing constant 419 alone rather than guessing a formula from two samples.
- **`position` is 0 in both captured elements**, while we send the lobby slot (1, 2), and
  **`appearance2` is 1 and 2** where we send 100. Observed, not acted on.

## T84 - `account_benefits`, the cash-shop rows the lobby was sending empty

`S_ACCOUNT_PACKAGE_LIST` (cap_social4_client frame 14) and `S_ACCOUNT_BENEFIT_LIST` (frames 47
and 48) both read the same thing, and `LoginHandlers` was sending both with an empty field set.
The brief proposed columns on the account row; the capture rules that out - it already shows
**three** packages for one account and the list is variable-length. So:

```sql
CREATE TABLE account_benefits (
  account_id  INTEGER NOT NULL REFERENCES accounts(id),
  package_id  INTEGER NOT NULL,
  expires_at  INTEGER NOT NULL DEFAULT 0,   -- unix seconds
  value       INTEGER NOT NULL DEFAULT 0,   -- the slot the benefit def calls unk1
  PRIMARY KEY (account_id, package_id)
);
```

`GetAccountBenefits` / `GrantAccountBenefit` (upsert) / `RevokeAccountBenefit`.
`ORDER BY package_id` reproduces the capture order: 533 (expires 1791961199, value 0x23E726),
534 (2100409199, 0x12867226), 1000 (1786777199, 0).

One column serves both packets: the slot `S_ACCOUNT_BENEFIT_LIST.1.def` calls `timeRemaining`
is not a countdown - frame 47 puts the same absolute 1791961199 there that the package list
gives 533 as `expirationDate`. See `status/CLIENT-REJECTS.md` section 11.

### Not persisted, deliberately

- **`S_SEND_USER_PLAY_TIME`** (frames 78, 2957) is session play time plus the server clock -
  237 s at 1789608265, then 1446 s at 1789608607, 342 seconds apart in *both* slots. Nothing to
  store; it is computed at send time.
- **`S_ENABLE_DISABLE_SELLABLE_ITEM_LIST`** (frames 441, 442) is server config, not per-account:
  both captured frames are identical and neither follows a request. Kept as
  `ArbiterClientHandlers.DefaultSellableItems` = 1164, 1167, 1170.

## T88 - `characters.delete_at`, the scheduled delete

`C_CANCEL_DELETE_USER` (cap_final_client2 frames 32/33) undoes a delete, which only means
anything if the delete was scheduled rather than immediate. The real Arbiter keeps the doomed
character LISTED while the timer runs - that is what `S_GET_USER_LIST.deleteRemainSec` carries -
so the stamp is a column, not a row removal.

```sql
ALTER TABLE characters ADD COLUMN delete_at INTEGER NOT NULL DEFAULT 0;  -- unix seconds, 0 = none
```

`ScheduleCharacterDelete(id, accountId, at)` / `CancelCharacterDelete(id, accountId)` /
`GetCharacterDeleteAt(id)`. Both mutators are ownership-checked, and the cancel returns FALSE
when nothing was pending - the reply byte is that bool, so a cancel of an unscheduled character
must not report success.

### Still open

- **`OnDeleteUser` still hard-deletes the row.** Switching it to stamp `delete_at` and letting a
  sweeper remove the row changes behaviour the existing T50 delete tests assert, and changes what
  the lobby puts in `deleteRemainSec` (today `0 - now`). Left alone deliberately; the store side
  is ready for it.

## T88 (second pass) - the character rename

World-routed: `SDB_ASK_CHANGE_CHAR_NAME` (0x2854) asks whether a name is free and
`SDB_DO_CHANGE_CHAR_NAME` (0x2856) performs it. Both land on `characters.name`, whose
`UNIQUE COLLATE NOCASE` index is the real gate - `RenameCharacter` catches the constraint
violation and returns false rather than throwing. `GetCharacterName(id)` exists so a rename to
the name the character already has is not counted as a collision.

Name rules the capture can pin: **four characters minimum** (tap 6094 refuses the three-letter
"Dob", 6119 accepts "dobb"). The ceiling and the per-reason codes are not observable here, so
every refusal answers code 1 - the only refusal code in the capture.

## T90 - `guilds.last_incentive_at`

```sql
ALTER TABLE guilds ADD COLUMN last_incentive_at INTEGER NOT NULL DEFAULT 0;  -- unix seconds
```

`GetGuildIncentiveTime(guildId)` / `SetGuildIncentiveTime(guildId, when)`. Read by
`SDB_GIVE_GUILD_MONEY_INCENTIVE` (0x27A0) to reproduce the cooldown
`Guild::CanGiveGuildMoneyIncentive` checks. The cooldown LENGTH is not observable - the capture
has a single grant - so `DbProxyHandlers.GuildIncentiveCooldownSeconds` is a 23 h floor, marked
as our choice rather than an observed value.

**No appearance column was added.** cap_final shows the appearance change reaching the row
through the ordinary user save, not through any appearance-specific write - see
`status/CLIENT-REJECTS.md` section 14.1.

## T98 - `guild_quests`

```sql
CREATE TABLE guild_quests (
  guild_id      INTEGER NOT NULL REFERENCES guilds(guild_id),
  quest_id      INTEGER NOT NULL,
  status        INTEGER NOT NULL DEFAULT 0,   -- 0 available, 1 running
  started_at    INTEGER NOT NULL DEFAULT 0,
  ends_at       INTEGER NOT NULL DEFAULT 0,   -- remainSec on the wire is this minus now
  starter_db_id INTEGER NOT NULL DEFAULT 0,
  progress      INTEGER NOT NULL DEFAULT 0,
  PRIMARY KEY (guild_id, quest_id)
);
```

`GetGuildQuests` / `GetRunningGuildQuest` / `SetGuildQuest`. One row per quest, not per run.

**The catalogue is NOT in the database.** Both captured frames list the same six quests with the
same ids, targets and rewards for two different guilds - it is sheet data, and it lives in
`GuildPackets.GuildQuestCatalogue`. The only per-guild numbers are the header (`guilds.point`,
`guilds.money`, the starter) and the running quest s countdown, which is computed from
`ends_at`. See `status/GUILD-DESIGN.md` T98.1.

## T101 - `admin_log`

```sql
CREATE TABLE admin_log (
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  at INTEGER NOT NULL, source_ip TEXT, action TEXT NOT NULL,
  target TEXT, reason TEXT, result INTEGER NOT NULL DEFAULT 0
);
```

`AddAdminLog` / `GetAdminLog(limit)` (newest first). Every write the admin web tool makes lands
here. `WEBADMIN-DESIGN.md` section 2 notes the retail tool logs only a free-text reason and never
stamps who did it - this keeps the source IP and the result code too.

Also added: `SearchAccounts(term, limit)` - by id when the term parses as one, otherwise a name
substring with LIKE wildcards escaped, the same way `GetNamesStartingWith` does it.

## T101b - `restrictions`, `deleted_items`, and the soft delete

```sql
CREATE TABLE restrictions (
  character_id INTEGER NOT NULL REFERENCES characters(id),
  type INTEGER NOT NULL,              -- 1 ban, 2 mute
  level INTEGER NOT NULL DEFAULT 0,
  until INTEGER NOT NULL DEFAULT 0,   -- unix seconds; 0 = permanent
  reason TEXT NOT NULL DEFAULT (''), set_at INTEGER NOT NULL DEFAULT 0,
  PRIMARY KEY (character_id, type)
);
CREATE TABLE deleted_items (
  item_db_id INTEGER PRIMARY KEY, character_id INTEGER NOT NULL,
  inven_type INTEGER NOT NULL DEFAULT 0, slot INTEGER NOT NULL DEFAULT 0,
  template_id INTEGER NOT NULL DEFAULT 0, amount INTEGER NOT NULL DEFAULT 0,
  record BLOB, deleted_at INTEGER NOT NULL DEFAULT 0
);
```

Plus two columns on `characters`: `deleted_at` and `deleted_by` (both added with
`AddColumnIfMissing`, so an existing db upgrades in place).

`AddRestriction` / `RemoveRestriction` / `GetRestrictions` / `IsRestricted(id, type, now)` - one row
per `(character_id, type)`, so a second ban replaces the first. An expired `until` stops counting on
its own; `until` 0 never lapses.

`SoftDeleteCharacter(id, accountId, byWhom, deleteAt, now)` / `RestoreDeletedCharacter(id)` /
`GetDeletedCharacters(limit)` / `PurgeExpiredDeletes(now, expireHours = DeleteExpireHours)`. Both
mutators are transactional and keep item ids and slots, so a restore is byte-identical to what was
parked. `DeleteExpireHours = 72` is `deleteCharacterExpireHour2` from `LoginHandlers`'
S_GET_USER_LIST.

`DeleteCharacter` also gained `DELETE FROM restrictions WHERE character_id = $id` - the table has a
real foreign key on `characters(id)` and Microsoft.Data.Sqlite enforces them, so purging a banned
character would otherwise fail with SQLite error 19.

`CancelCharacterDelete` now checks ownership and a pending stamp, then delegates to
`RestoreDeletedCharacter`, so `C_CANCEL_DELETE_USER` and the admin tool's restore are one code path.
See `status/WEBADMIN-DESIGN.md` section 9.

## T104 - `watched_movies_account`, the write behind C_WATCHED_MOVIES

```sql
CREATE TABLE watched_movies_account (
  account_id INTEGER NOT NULL, movie_id INTEGER NOT NULL,
  watched_at TEXT NOT NULL DEFAULT (datetime('now')),
  PRIMARY KEY (account_id, movie_id)
);
```

`AddWatchedMovieForAccount(accountId, movieId)` / `GetWatchedMoviesForAccount(accountId)`. The read
UNIONs T62's per-character `watched_movies` rows in, so an existing database does not replay the
intro once more on the way past.

**T62 keyed this on the character; the real Arbiter keys it on the ACCOUNT.** The whole chain hangs
off the Account object: `Handler_SA_WATCH_MOVIE` (Arb_part_062.c:18768) takes `User+0x3f40` and
calls `Account::InsertWatchedMovieWithLock(movieId)`, which de-duplicates against the `std::set` at
`Account+0x3000` and only then runs `dbo.spInsertUserWatchedMovie`; the read is
`Account::CachedWatchedMoviesWithLock` (Arb_part_061.c:6450) behind a once-per-account latch at
`Account+0x2ff8`, running `dbo.spLoadUserWatchedMovies`.

T62 built the reply and the table but **nothing ever wrote a row** - `AddWatchedMovie` had no
caller. The missing writer is `SA_WATCH_MOVIE` (0x155C), now handled in `DbProxyHandlers`.

Not implemented: `Account::InsertWatchedMoviesWithNoLock` (Arb_part_064.c:16853) merges the
`ReplayMovieData` datasheet on top of the loaded set - rows whose level or dungeon-clear condition
the account already meets get marked so the cinematic REPLAY menu offers them. That is the replay
list, not the first-watch gate, and it needs a sheet TeraSharp does not carry.

## T113 - `characters.play_seconds`, and where the clock runs

```sql
ALTER TABLE characters ADD COLUMN play_seconds INTEGER NOT NULL DEFAULT 0;   -- AddColumnIfMissing
```

`accounts.play_time_sec` has existed since T101c and **nothing fed it**. Now one writer feeds both:
`CharacterStore.AddCharacterPlaySeconds(characterId, delta)` updates the character row and the
owning account row inside one transaction, so the two cannot drift, and ignores a delta <= 0 - a
clock that went backwards must not eat a player's history.

The live clock is **not** in the database. The real Arbiter keeps a running counter on the User
object at `User+0x1E4` and `Handler_C_PLAY_TIME` reads it straight off; TeraSharp keeps the ENTER
stamp in `DbProxyHandlers.EnteredAt` (a `ConcurrentDictionary<int,long>` beside `GameIdByPlayer`,
for the same reason: per-player Arbiter state that is not worth a column and is not reachable from
a handler), and commits the difference once, at leave-world:

| call | what it does |
| --- | --- |
| `MarkEnteredWorld(playerId, now)` | stamp, from `SDB_USER_ENTERWORLD` |
| `SecondsInWorld(playerId, now)` | live difference, **does not clear** - `S_PLAY_TIME` asks mid-session |
| `TakeSessionSeconds(playerId, now)` | difference and clear; 0 on a second call, so a double leave cannot bank twice |
| `CommitPlayTime(playerId, now)` | the whole of it: take, add to the row, log |

`OnUserEnterWorld` commits before it stamps, so a previous session that never saw `SA_LEAVE_WORLD`
(a crash, a dropped link) is closed out at the one moment we are certain the player is not in world
any more - because they are entering it. A crash therefore costs one session, not the history.

**Where it surfaces.** `S_SEND_USER_PLAY_TIME.totalPlaytime` (`BuildUserPlayTimeFields`),
`S_PLAY_TIME` via `ArbiterClientHandlers.PlayTimeLookup`, and the admin character page's
`progress.playSeconds`. **Not** `S_GET_USER_LIST`: the shipped `S_GET_USER_LIST.18.def` has no
play-time field - `lastLogoutTime`, `deleteTime` and `banEndTime` are the only times in it - so the
lobby cannot show it however the figure is stored.
