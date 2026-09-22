# Persistence map — what World writes during play, and which login load it feeds

Source: `<captures>\cap_newchar.log` (real ArbiterServer, new character "Test" playerId 2,
Island of Dawn, 05:49–05:53: create, enter, quests, gathering, potions, kills, level-ups, skill
learning, item combine, tutorial dungeon enter/leave, logout). Condensed control listing in
`<captures>\cap_newchar_ctl.txt` (seq, dir, time, opcode, len, first 64 payload bytes).
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

## T147 - crafting and gathering (`item_recipes`, `skill_profs`, `gathering_profs`)

The two artisan loads were replay-served (empty, byte-exact) and the ten writes behind them had no
answer at all - the first recipe, craft, bookmark or gathered node head-blocked the character.
All twelve are answered now; layouts and sources are in `status/CRAFTING.md`. Request sizes are
the handlers' guards; no capture has any of the ten writes.

| write (W->A) | reply | shape | feeds |
|---|---|---|---|
| 0x2760 SDB_LOAD_ITEM_RECIPE (14 B) | 0x2761 | ref 19, 28-B RecipeInfo each, DlmId, Success | `item_recipes` |
| 0x2764 SDB_LOAD_SKILL_PROF (14 B) | 0x2765 | ref 19, 8-B [id][value] each, DlmId, Success | `skill_profs` |
| 0x275E SDB_LEARN_ITEM_RECIPE (27 B + atoms) | 0x275F | ref 19 + the atoms echoed with ids, DlmId, Success | `item_recipes`, `items` |
| 0x2762 SDB_DELETE_ITEM_RECIPE_LIST (22 B + ids) | 0x2763 | [u8 Success][u32 DlmId], Success 0 for an empty list | `item_recipes` |
| 0x288A SDB_SET_RECIPE_BOOKMARK (19 B) | 0x288B | [u32 DlmId][u8 Success] | `item_recipes.bookmark` |
| 0x2756 SDB_ITEM_PRODUCE_STEP1 (22 B) | 0x2757 | [u32 DlmId][u8 Success] | `fatigability` (account) |
| 0x2758 SDB_ITEM_PRODUCE_STEP2 (38 B + atoms) | 0x2759 | ref 19 + the atoms echoed with ids, DlmId, Success | `items`, `skill_profs` |
| 0x2766 SDB_UPDATE_SKILL_PROF (22 B) | 0x2767 | [u32 DlmId][u8 Success] | `skill_profs` |
| 0x273D S_UPDATE_PROF_MINERAL (18 B) | 0x2741 | [u32 DlmId][u8 Success] | `gathering_profs` kind 0, blob +0x1C8 |
| 0x273E S_UPDATE_PROF_BUG (18 B) | 0x2741 | [u32 DlmId][u8 Success] | `gathering_profs` kind 1, blob +0x1CC |
| 0x273F S_UPDATE_PROF_ENERGY (18 B) | 0x2741 | [u32 DlmId][u8 Success] | `gathering_profs` kind 2, blob +0x1D0 |
| 0x2740 S_UPDATE_PROF_HERB (18 B) | 0x2741 | [u32 DlmId][u8 Success] | `gathering_profs` kind 3, blob +0x1D4 |

## T150b - the bag size, and four generic acks proven by live pairs

T150 (0x283D plus a blanket ack for 181 decompile-only twins) regressed live and was reverted:
a level-70 character showed level 1, could not move, and Invisible OFF no longer reloaded. The
cause was not pinned (no tap of that run). T150b re-lands only what a real capture proves.

| write (W->A) | reply | shape | feeds |
|---|---|---|---|
| 0x283D SDB_INCREASE_INVENTORY_SIZE (38 B) | 0x283E | [u8 Success][u32 DlmId], cap_social4.log 3039->3040 | world blob +0x3AF0 MaxInvenSlotCount (cap_social4 250: 40, 5715: 48), +0x3B00 expandInvenCount |
| 0x279A SDB_GUILD_LEARN_PERK (46 B) | 0x279B | [u32 DlmId][u8 Success], cap_social3.log 3348->3350 | nothing (DbAckTable) |
| 0x27C3 SDB_CONDITIONAL_TELEPORT (27 B) | 0x27C4 | ref 19, DlmId, Success, cap_social2 2668->2670 / cap_social3 3627->3628 | `items` if records (DbAckTable) |
| 0x27CD SDB_ITEM_SIMPLE_ATOM (22 B + 568-B records) | 0x27CE | ref 19 + records echoed with ids, DlmId, Success, cap_social3.log 1203->1204 | `items` (DbAckTable) |
| 0x27DE SDB_MAIN_MENU_COMMAND (19 B) | 0x27DF | [u32 DlmId][u8 Success][u32 ResultNum 0], cap_social2.log 2964->2965 | nothing (DbAckTable) |

`DbAckTable` was opt-in: one row and one `case` per opcode, and a test fails for any pinned row
without a live pair it reproduces byte-exact. The candidate list that stood here is replaced by
the three T165 tables at the end of this file.

## T154 - `city_guild`, the Civil Unrest city owners

| write (W->A) | reply | shape | feeds |
|---|---|---|---|
| 0x2954 SDB_LOAD_CITY_GUILD_INFO (14 B, no DlmId) | 0x2955 | [u32 count][u32 first][LeagueId][SeasonId] + 44-B linked elements; empty = 00*8 + ids (arb_world, cap_social4, cap_final) | `city_guild` (league, season, guild) |

Asked at boot and then on World's own schedule; only the boot one was ever answered, because the
replay table files the captured 0x2955 under the 0x294F that follows it. Now answered every time
from `city_guild` - empty ("no owning guild") until a Civil Unrest result writes a row
(`CharacterStore.SetCityGuild`). The element's two i64 times are stored as World sends them; their
encoding is not pinned. 0x28B8 SDB_PUBLISH_INVITE_CODE stays one-way (no DBS_ twin; the Arbiter's
handler sends nothing back).

## T156 - the Vanguard Initiative (`daily_event`, `event_matching_reward`, two `counters`)

World builds the whole window from its own EventMatching datasheet; the Arbiter keeps progress
and reset stamps. S_AVAILABLE_EVENT_MATCHING_LIST (0x810D) is decoded in the tests: 94-byte
fixed part (3 array headers, 10 u32, 3 u8, 3 u32, 3 u8, 5 u32 - the last is the level), 47-byte
quest elements (conditions [cur][max], rewards [i32 item][i64 amount], two always-empty arrays,
one 12-byte triple list; then i32 id, u32 state, u32, 3 u8, u32, u32) and 16-byte bonus elements.

| write (W->A) | reply | shape | feeds |
|---|---|---|---|
| 0x1507 SA_AVAILABLE_EVENT_MATCHING_LIST (23 B) | 0x1591 | [BattleFieldList ref][DungeonList ref][i32 UserDbId][u8 ByPlayer], lists empty; cap_social 459/547, cap_social4 1529/8039 | nothing (UserDbId from the session) |
| 0x293C SDB_UPDATE_USER_DAILY_EVENT_COUNT (58 B) | 0x1591 push, then 0x293D | [reqId][1]; cap_social4 421 -> 422, 423 | `daily_event` counts, stamp if > 0 |
| 0x293A SDB_LOAD_USER_DAILY_EVENT (14 B) | 0x293B | [ref 0x20][20][reqId][ok][i64 stamp][u8][i32][5 x i32]; cap_newchar 338, cap_social 283, cap_social4 283/5748 | `daily_event` (no row = ok 0) |
| 0x293E SDB_UPDATE_GET_EXTRA_REWARD (20 B) | 0x293F | [reqId][1] | `daily_event` got/value |
| 0x2965 SDB_UPDATE_ADDITIONAL_REWARD_RECV_COUNT (22 B) | 0x2966 | [DlmId][1] | `event_matching_reward` |
| 0x2967 SDB_LOAD_ADDITIONAL_REWARD_RECV_COUNT (14 B) | 0x2968 | [ref 0x13][8n][DlmId][ok = any][i32 event, i32 count]...; cap_social4 285 | `event_matching_reward` |
| 0x1592 SA_LOAD_EVENT_MATCHING_INFO (6 B, boot) | 0x1595 + 0x1582 | [OffPairList ref 0x1E][i64 weekly][i64 add-reward]; arb_world 11/12, cap_social4 12 | `counters` em_extra_reward_weekly_reset / em_add_reward_reset |
| 0x1598 SA_UPDATE_PLAYGUIDE_EXTRA_REWARD_RESETTIME (14 B) | 0x1599 | [u8 1][i64 time] | `counters` weekly |
| 0x159A SA_UPDATE_EVENT_MATCHING_ADD_REWARD_RESETTIME (14 B) | 0x159B | [u8 1][i64 time] | `counters` add-reward; clears `event_matching_reward` |

A new `daily_event` row is stamped with the time it is created: cap_newchar's new character sends
0x293C with stamp 0, and its next login (cap_social 283) reads back the time of those writes.
Empty DB: 0x1595 carries arb_world.log's stamps, the bytes the replay sent before, and World's
0x1598 / 0x159A move them. 0x2965 leaves the T150b candidate list above. Not modelled: 0x1591's
match-pool lists (the ids the user is queued for), 0x1599 to every World (the real Arbiter
broadcasts; we answer the asker), 0x1595's OffPairList (SA_UPDATE_EVENT_ONOFF_STATE), and the
trailing flag of 0x293E (1 in every capture).

## T165 - every unanswered twin in one of three groups

The input is the human's list: SDB_ names in `DbProxyOpcodeNames.cs` with a DBS_ twin that appear
in no other .cs - 189 names. Five of them are already answered under another constant name (last
table), so 184 are classified here, each against World's `Handler_DBS_<twin>` (WorldServer.exe.c
line: its size guard and every offset it reads) and the Arbiter's PDL dumpers (ArbiterServer.exe.c:
field names, offsets, sizes) and `Handler_SDB_*` (what it runs and what it writes back).
`T165_every_twin_is_in_exactly_one_group` walks all 300 twins in the opcode table; it found 20
the name search missed (a name in a comment or a const hides it) - 4 go to C, 16 are answered or
sealed elsewhere.

**A (51)** - World reads only [DlmId][ok]. 26 are "bare" (the Arbiter runs its SQL and answers
[DlmId][ok]); 7 run request atoms and answer bare (`a`, T162's shape - the atoms are applied
here); 18 hand their writer the request's own atom vector (`b6=6`, ITEM_SIMPLE_ATOM's shape -
echoed with ids and applied; for 15 of them World also reads that list back). Everything else in the reply goes out
zero, unread refs empty. Decompile-only: each logs `generic ack 0x.... NAME - capture a real
pair to pin` the first time; a capture moves it to DbAckTable's pinned rows.
Brief examples that did NOT land in A: VIP exp (World sets the TOTAL from `NewResult`),
attendance / playtime checks (bitmap, reward ids), hide-passive learn (a result list), quest
MARK_AS_COMPLETED (the newly-completed list) - all C; "hold status" and "EP daily limit" were
already answered (0x2930 since T148, 0x27B3).

**B (62)** - never answered. `DbAckTable` throws while loading if any row names a B or C opcode,
and `OnGenericAck` refuses them again: a generic ack cannot rebuild a character (T150).

**Pending is read from the allow-list (T165b).** The B, C and Elsewhere tables are T165's
classification and stay as written; `DbAckGroups.Deny` / `RealHandler` / `Elsewhere` are those
tables minus every opcode `DbProxyHandlers.IsHandledRequest` now answers with its own case, and
`DbAckGroups.Landed` is the "real handler on master" group. Registering a handler (T164: 0x2790,
0x28AE; T166: the enchanting ops) moves the name there with no table edit. `DbAckTable` still
refuses every B/C row, landed or not: a real handler may answer them, a generic ack never.

**C (75)** - the T166 spec. Most item ones are ExecTrans echoes like group A's; they are here
because the atoms change item properties (enchant, awaken, dual option, ...) whose ops
`WarehouseHandlers.Apply` does not model, or because the DB also moves a currency.

### A - plain success, answered now (DbAckTable, unpinned: first use logs "generic ack 0x.... NAME - capture a real pair to pin")

| op | name | reply | World `Handler_DBS_*` (WorldServer.exe.c) reads | row | Arbiter handler (ArbiterServer.exe.c) |
|---|---|---|---|---|---|
| 0x2719 | CONSUME_EXP_ITEM_FOR_PET | 0x271A | :3011089 DlmId@E ItemBinary@6 ok@12 | `2719>271A q42 r19 d14:14 o18 k18 b6=6` | :1263588 runs + echoes its atoms |
| 0x271B | USE_SERVANT_FEED | 0x271C | :3028300 DlmId@E ItemBinary@6 ok@12 | `271B>271C q22 r19 d14:14 o18 k18 b6=6` | :1295897 runs + echoes its atoms |
| 0x271D | USE_SERVANT_STORAGE_ITEM | 0x271E | :3028356 DlmId@E ItemBinary@6 ok@12 | `271D>271E q26 r19 d14:14 o18 k18 b6=6` | :1296025 runs + echoes its atoms |
| 0x271F | SERVANT_ADVENTURE_REDUCE_TIME | 0x2720 | :3021970 DlmId@E ItemBinary@6 ok@12 | `271F>2720 q38 r19 d14:14 o18 k18 b6=6` | :1283289 runs + echoes its atoms |
| 0x2730 | GIVE_QUEST_BACKUP_ITEM | 0x2731 | :3013587 DlmId@E OwnerTransactions@6 ok@12 | `2730>2731 q22 r19 d14:14 o18 k18 b6=6` | :1267286 runs + echoes its atoms |
| 0x2783 | CLEAR_QUEST | 0x2784 | :3010904 DlmId@6 ok@12 | `2783>2784 q18 r19 d6:6 o10 k18` | :1263057 SQL, bare reply |
| 0x2794 | USER_UPDATE_LAST_MOUNT_SKILL | 0x2795 | :3028096 DlmId@6 ok@A | `2794>2795 q26 r11 d14:6 o18 k10` | :1563970 SQL, bare reply |
| 0x2796 | USER_DELETE_LAST_MOUNT_SKILL | 0x2797 | :3026940 DlmId@6 ok@A | `2796>2797 q22 r11 d6:6 o10 k10` | :1562859 SQL, bare reply |
| 0x279C | GUILD_RESET_PERK | 0x279D | :3013829 DlmId@6 ok@A | `279C>279D q18 r11 d6:6 o10 k10` | :1268072 SQL, bare reply |
| 0x279E | START_GUILD_PERK_AND_COST_GUILD_MONEY | 0x279F | :3022916 DlmId@6 ok@A | `279E>279F q26 r11 d6:6 o10 k10` | :1285399 SQL, bare reply |
| 0x27A9 | ADD_TELEPORT_TO_POS_LIST | 0x27AA | :3009230 DlmId@6 ok@A | `27A9>27AA q34 r11 d10:6 o14 k10` | :1259907 SQL, bare reply |
| 0x27AB | DELETE_TELEPORT_TO_POS_LIST | 0x27AC | :3012718 DlmId@6 ok@A | `27AB>27AC q34 r11 d10:6 o14 k10` | :1264930 SQL, bare reply |
| 0x27AD | RENAME_TELEPORT_TO_POS_LIST | 0x27AE | :3020889 DlmId@6 ok@A | `27AD>27AE q38 r11 d14:6 o18 k10` | :1279414 SQL, bare reply |
| 0x27E8 | GROUP_DUEL_BET_MONEY | 0x27E9 | :3013688 DlmId@6 ok@A | `27E8>27E9 q26 r15 d6:6 o10 k10` | :1267576 SQL, bare reply |
| 0x27FC | GROUP_DUEL_BETTING_ITEM | 0x27FD | :3013645 DlmId@E ok@12 | `27FC>27FD q42 r19 d22:14 o26 k18 b6=6` | :1267429 runs + echoes its atoms |
| 0x2806 | APPLY_TITLE | 0x2807 | :3009544 DlmId@6 ok@A | `2806>2807 q18 r11 d6:6 o10 k10` | :1260599 SQL, bare reply |
| 0x2837 | RESET_VIP_STORE | 0x2838 | :3021474 DlmId@7 ok@6 | `2837>2838 q34 r11 d14:7 o18 k6` | :1282006 SQL, bare reply |
| 0x2841 | SET_INVEN_POCKET_GET_FILTER | 0x2842 | :3022028 DlmId@7 ok@6 | `2841>2842 q22 r11 d6:7 o10 k6` | :1283418 SQL, bare reply |
| 0x2843 | APPLY_INVEN_POCKET_SORT | 0x2844 | :3009458 DlmId@7 ok@6 | `2843>2844 q26 r11 d14:7 o18 k6` | :1260421 SQL, bare reply |
| 0x2845 | CHANGE_POCKET_NAME_AND_COST_USER_MONEY | 0x2846 | :3010475 DlmId@7 ok@6 | `2845>2846 q30 r11 d18:7 o22 k6 a10` | :1262671 runs its atoms, bare reply |
| 0x285A | DO_CHANGE_GUILD_NAME | 0x285B | :3012948 DlmId@E ok@12 | `285A>285B q26 r19 d18:14 o22 k18 b6 a10` | :1265451 runs its atoms, bare reply |
| 0x285C | DO_CHANGE_LOOK | 0x285D | :3012991 DlmId@6 ok@A | `285C>285D q34 r11 d22:6 o26 k10 a14` | :1265935 runs its atoms, bare reply |
| 0x2860 | CHANGE_FACE_CUSTOM | 0x2861 | :3010233 DlmId@6 ok@A | `2860>2861 q30 r11 d14:6 o18 k10` | :1561614 SQL, bare reply |
| 0x2870 | DONT_REPEAT_TUTORIAL_SIMPLE_TIP | 0x2871 | :3012862 DlmId@6 ok@A | `2870>2871 q22 r11 d6:6 o10 k10` | :1265285 SQL, bare reply |
| 0x287E | CHANGE_ITEM_EXTERIOR | 0x287F | :3010417 DlmId@E ItemBinary@6 ok@12 | `287E>287F q22 r19 d14:14 o18 k18 b6=6` | :1262545 runs + echoes its atoms |
| 0x2880 | RESTORE_ITEM_EXTERIOR | 0x2881 | :3021559 DlmId@E ItemBinary@6 ok@12 | `2880>2881 q22 r19 d14:14 o18 k18 b6=6` | :1282190 runs + echoes its atoms |
| 0x2882 | CHANGE_ITEM_COLORING | 0x2883 | :3010359 DlmId@E ItemBinary@6 ok@12 | `2882>2883 q22 r19 d14:14 o18 k18 b6=6` | :1262419 runs + echoes its atoms |
| 0x2884 | DECREASE_ITEM_COLORING_LEFTTIME | 0x2885 | :3012062 DlmId@E ItemBinary@6 ok@12 | `2884>2885 q22 r19 d14:14 o18 k18 b6=6` | :1264089 runs + echoes its atoms |
| 0x2886 | REVIVE_FOR_DUNGEON_RETRY | 0x2887 | :3021740 DlmId@E ItemBinary@6 ok@12 | `2886>2887 q22 r19 d14:14 o18 k18 b6=6` | :1282577 runs + echoes its atoms |
| 0x2888 | TURN_ON_WORK_OBJECT | 0x2889 | :3025007 DlmId@E OwnerTransactions@6 ok@12 | `2888>2889 q22 r19 d14:14 o18 k18 b6=6` | :1290401 runs + echoes its atoms |
| 0x288D | USE_CHRONOSCROLL | 0x288E | :3028139 DlmId@E ok@12 | `288D>288E q34 r19 d14:14 o18 k18 b6=6` | :1436232 runs + echoes its atoms |
| 0x28A3 | DELETE_QUEST_COMPLETED | 0x28A4 | :3012620 DlmId@6 ok@A | `28A3>28A4 q22 r11 d14:6 o18 k10` | :1264821 SQL, bare reply |
| 0x28B5 | CHECK_SUMMON_FRIEND | 0x28B4 | :3010657 DlmId@7 ok@6 | `28B5>28B4 q18 r11 d6:7 k6` | :1262983 SQL, bare reply |
| 0x28BF | UPDATE_PREMIUM_SLOT | 0x28C0 | :3025837 DlmId@6 ok@A | `28BF>28C0 q47 r11 d6:6 o18 k10` | :1562573 SQL, bare reply |
| 0x28CD | CHANGE_ENCHAT_SCROLL_PASSIVE | 0x28CE | :3010091 DlmId@6 ok@A | `28CD>28CE q22 r11 d14:6 o18 k10 a6.568` | :1261860 runs its atoms, bare reply |
| 0x28D1 | USER_LEARN_SOCIAL | 0x28D2 | :3027405 DlmId@E ItemBinary@6 ok@12 | `28D1>28D2 q26 r19 d14:14 o18 k18 b6=6` | :1563823 runs + echoes its atoms |
| 0x28D3 | USER_FORGET_SOCIAL | 0x28D4 | :3027096 DlmId@6 ok@A | `28D3>28D4 q18 r11 d6:6 o10 k10` | :1563128 SQL, bare reply |
| 0x28D5 | USER_CLEAR_ALL_SOCIAL | 0x28D6 | :3026897 DlmId@6 ok@A | `28D5>28D6 q14 r11 d6:6 o10 k10` | :1562791 SQL, bare reply |
| 0x28D7 | INCREMENT_CHARACTER_SOCKET | 0x28D8 | :3014186 DlmId@6 ok@A | `28D7>28D8 q22 r11 d14:6 o18 k10 a6` | :1269113 runs its atoms, bare reply |
| 0x28DF | COMPLETE_STORY_QUEST | 0x28E0 | :3010988 DlmId@6 ok@A | `28DF>28E0 q22 r11 d14:6 o18 k10 a6` | :1263285 runs its atoms, bare reply |
| 0x28FC | EXCHANGE_TOKEN_ITEM_TO_POINT | 0x28FD | :3013300 DlmId@E OwnerBinary@6 ok@12 | `28FC>28FD q22 r19 d14:14 o18 k18 b6=6` | :1266740 runs + echoes its atoms |
| 0x2904 | SET_TOKEN_POINT_QA | 0x2905 | :3022616 DlmId@E OwnerBinary@6 ok@12 | `2904>2905 q22 r19 d14:14 o18 k18 b6=6` | :1284309 runs + echoes its atoms |
| 0x290E | NEW_FATIGABILITY | 0x290F | :3019968 DlmId@7 ok@6 | `290E>290F q22 r11 d6:7 o10 k6` | :1278268 SQL, bare reply |
| 0x292C | COLLECT_USEITEM_IN_FLOATING_CASTLE | 0x292D | :3010945 DlmId@E ok@12 | `292C>292D q22 r19 d14:14 o18 k18 b6=6` | :1263159 runs + echoes its atoms |
| 0x2938 | RESET_DAILY_ATTENDANCE | 0x2939 | :3021330 DlmId@6 ok@A | `2938>2939 q14 r11 d6:6 o10 k10` | :1281558 SQL, bare reply |
| 0x2946 | REQUEST_MEGAPHONE | 0x2947 | :3021186 DlmId@6 ok@A | `2946>2947 q26 r11 d18:6 o22 k10 a10` | :1280123 runs its atoms, bare reply |
| 0x294C | RECEIVE_PLAYTIME_REWARD | 0x294D | :3020331 DlmId@E ItemBinary@6 ok@12 | `294C>294D q34 r19 d14:14 o18 k18 b6=6` | :1279099 runs + echoes its atoms |
| 0x296F | UPDATE_USERAWAKENGRADE | 0x2970 | :3026422 DlmId@6 ok@A | `296F>2970 q18 r11 d6:6 o10 k10` | :1293296 SQL, bare reply |
| 0x297D | APPLY_MAXACTPOINT_ACCOUNTTRAIT | 0x297E | :3009501 DlmId@6 ok@A | `297D>297E q18 r11 d6:6 o10 k10` | :1260514 SQL, bare reply |
| 0x297F | DISAPPLY_MAXACTPOINT_ACCOUNTTRAIT | 0x2980 | :3012819 DlmId@6 ok@A | `297F>2980 q14 r11 d6:6 o10 k10` | :1265189 SQL, bare reply |
| 0x2983 | UPDATE_PURCHASE_LIMIT | 0x2984 | :3026005 DlmId@6 ok@A | `2983>2984 q38 r11 d6:6 o10 k10` | :1292548 SQL, bare reply |

### B - DENY, never answered (DbAckGroups.Deny; DbAckTable refuses a row for any of them)

| op | name | reply | World reads | why |
|---|---|---|---|---|
| 0x2712 | TBA_GET_USER_RECORDS | 0x2713 | :3023244 DlmId@12 ok@16 Point@1B Grade@1F ?@23 UserLevel@27 UserExp@2B | TBA (hero-arena account) - no TBA data here |
| 0x2723 | VENDINGMACHINE_LOAD | 0x27EC | :3029113 DlmId@2A SellInvenInfo@6 BuyInvenInfo@E SellList@16 BuyList@1E VendingMachineMsg@26 Money@2E | vending machine - money, sell/buy lists and inventories we do not keep |
| 0x272A | VENDINGMACHINE_DESTROY | 0x272B | :3029070 DlmId@6 ok@A | vending machine - money, sell/buy lists and inventories we do not keep |
| 0x27EE | VENDINGMACHINE_SET | 0x27EF | :3029269 DlmId@2E UpdatedMoney@33 SellInvenInfo@6 BuyInvenInfo@E SellList@16 BuyList@1E TransResult@26 ok@32 | vending machine - money, sell/buy lists and inventories we do not keep |
| 0x27F0 | VENDINGMACHINE_BUYSELL | 0x27F1 | :3028414 DlmId@2E UpdatedMoney@33 SellInvenInfo@6 BuyInvenInfo@E SellList@16 BuyList@1E ItemBinary@26 ok@32 | vending machine - money, sell/buy lists and inventories we do not keep |
| 0x27F2 | VENDINGMACHINE_BUY_SET | 0x27F3 | :3028721 DlmId@26 UpdatedMoney@2B SellInvenInfo@6 BuyInvenInfo@E SellList@16 BuyList@1E ok@2A | vending machine - money, sell/buy lists and inventories we do not keep |
| 0x27F4 | VENDINGMACHINE_BUY_EX | 0x27F5 | :3028570 DlmId@26 UpdatedMoney@2B SellInvenInfo@6 BuyInvenInfo@E SellList@16 BuyList@1E ok@2A | vending machine - money, sell/buy lists and inventories we do not keep |
| 0x27F6 | VENDINGMACHINE_CLEAR | 0x27F7 | :3029012 DlmId@E ItemBinary@6 ok@12 | vending machine - money, sell/buy lists and inventories we do not keep |
| 0x284A | COUPON_LIST | 0x284B | :3011147 DlmId@E CurrentPage@12 LastPage@16 TimeOut@1A CouponList@6 | coupons - no coupon table |
| 0x284C | USE_COUPON | 0x284D | :3028180 DlmId@16 Step@22 CouponData@6 CouponItemList@E ok@26 | coupons - no coupon table |
| 0x284E | DELETE_COUPON | 0x284F | :3012120 DlmId@6 ok@12 | coupons - no coupon table |
| 0x2874 | VENDINGMACHINE_CALCULATE_SELLER | 0x2875 | :3028945 DlmId@E SellList@6 ok@12 | vending machine - money, sell/buy lists and inventories we do not keep |
| 0x2876 | VENDINGMACHINE_CALCULATE_BUYER | 0x2877 | :3028872 DlmId@16 ItemBinary@6 BuyList@E ok@1A | vending machine - money, sell/buy lists and inventories we do not keep |
| 0x287A | OPERATOR_TERRITORY_NPC_KILL | 0x2878 | :1732449 HuntingZoneId@6 TerritoryId@A NpcTemplateId@E (no generic completion) | operator territory - territory/NPC respawn state (no DlmId) |
| 0x289F | TUTORIAL_END | 0x28A0 | :3025064 DlmId@16 InvenInfo@E UserData@6 ok@1A | reply hands World UserData + InvenInfo: the character is rebuilt from it (T150 suspect) |
| 0x28A5 | INSERT_FEUDAL_LORD_FLAG | 0x28A6 | :3014784 DlmId@6 FlagKey@A ok@12 | feudal lord flag - FlagKey / KillConfirmed only a real table can give |
| 0x28A7 | DECREASE_FEUDAL_LORD_FLAG_LIFE_TIME | 0x28A8 | :3012018 DlmId@6 KillConfirmed@B ok@A | feudal lord flag - FlagKey / KillConfirmed only a real table can give |
| 0x28E1 | INCREMENT_CHARACTER_EXP | 0x28E2 | :3014014 DlmId@6 ok@A | level family: on ok World commits exp/level itself; pinned unanswered by T150b, only 0x28D9/DB/DD have T162 rows |
| 0x28FA | DO_LIMITED_GACHA_WORK | 0x28FB | :3013032 DlmId@6 ok@A IsWinAtGacha@F ItemTemplateId@10 BroadCastArea@14 IsBroadCastLeftNumber@18 LeftTotalTargetNumber@19 | gacha - win/broadcast/left-count computed by the DB |
| 0x2902 | LOAD_MASSTIGE_INFO | 0x2903 | :3017371 DlmId@6 ObtainedId@B TargetId@F ok@A | masstige - no masstige state |
| 0x2906 | RESET_MASSTIGE_STATUS | 0x2907 | :3021416 DlmId@E OwnerBinary@6 ok@12 | masstige - no masstige state |
| 0x2914 | UPDATE_PROMOTION | 0x2915 | :3025921 DlmId@7 ok@6 | promotions (0x147D is served static; stale data crashes NewPromotion) |
| 0x2916 | LOAD_PROMOTION_CONDITION_LIST | 0x2917 | :3017589 DlmId@F Condition@6 ok@E | promotions (0x147D is served static; stale data crashes NewPromotion) |
| 0x2918 | CREATE_PROMOTION_CONDITION | 0x2919 | :3011932 DlmId@7 ok@6 | promotions (0x147D is served static; stale data crashes NewPromotion) |
| 0x291A | UPDATE_PROMOTION_CONDITION | 0x291B | :3025962 DlmId@7 ok@6 | promotions (0x147D is served static; stale data crashes NewPromotion) |
| 0x291C | DELETE_PROMOTION | 0x291D | :3012475 DlmId@7 ok@6 | promotions (0x147D is served static; stale data crashes NewPromotion) |
| 0x291E | DELETE_PROMOTION_CONDITION | 0x291F | :3012516 DlmId@7 ok@6 | promotions (0x147D is served static; stale data crashes NewPromotion) |
| 0x2948 | BUILD_GUILD_TOWER | 0x2949 | :3009747 DlmId@6 ok@A | Civil Unrest towers - tower/league state beyond T154 city_guild; acking a build alone makes a tower no load returns |
| 0x294F | LOAD_CITY_TOWER_AND_PLAYER_INFO | 0x2952 | :3016410 see reader (169 lines) | Civil Unrest towers - tower/league state beyond T154 city_guild; acking a build alone makes a tower no load returns |
| 0x2950 | DELETE_GUILD_TOWER | 0x2951 | :3012161 LeagueId@7 SeasonId@B UserDbId@13 ok@6 RequestDespawn@1F TowerGameId@17 GuildDbId@F AttackerUserDbId@24 Reason@20 (no generic completion) | Civil Unrest towers - tower/league state beyond T154 city_guild; acking a build alone makes a tower no load returns |
| 0x2956 | CREATE_NEW_CITY_WAR_LEAGUE | 0x2957 | :3011806 ?@26 ?@22 ?@1E ?@A OffState@2B ?@2A ?@16 SeasonId@12 LeagueId@E (no generic completion) | city war league create - LeagueId/SeasonId and league rows |
| 0x29A0 | OPERATOR_TERRITORY_NPCTEMPLATE_RESPAWN | 0x287D | :1732358 HuntingZoneId@6 TerritoryId@A NpcTemplateId@E RespawnTime@12 (no generic completion) | operator territory - territory/NPC respawn state (no DlmId) |
| 0x29A1 | OPERATOR_TERRITORY_NPCPARTY_RESPAWN | 0x287C | :1732270 HuntingZoneId@6 TerritoryId@A PartyId@E RespawnTime@12 (no generic completion) | operator territory - territory/NPC respawn state (no DlmId) |
| 0x29A2 | REQUEST_CHECK_CITYWAR_ENTER_GUILD_QUEST_POINT | 0x29A3 | :3020973 LeagueId@16 GuildSizeRank@1A CompletedPoint@1E PlanetId@6 UserDbId@A Level@E GuildId@12 (no generic completion) | Civil Unrest towers - tower/league state beyond T154 city_guild; acking a build alone makes a tower no load returns |
| 0x29A4 | REQUEST_HERO_DATA | 0x29A5 | :3021145 DlmId@1E | hero / hero skin (TBA) - no hero data here |
| 0x29A6 | REGISTER_HERO | 0x29A7 | :3020807 DlmId@6 ok@A | hero / hero skin (TBA) - no hero data here |
| 0x29A8 | DELETE_HERO | 0x29A9 | :3012309 DlmId@6 ok@A | hero / hero skin (TBA) - no hero data here |
| 0x29AA | REQUEST_HEROSKIN_DATA | 0x29AB | :3021102 DlmId@16 | hero / hero skin (TBA) - no hero data here |
| 0x29AC | REGISTER_HEROSKIN | 0x29AD | :3020848 DlmId@6 ok@A | hero / hero skin (TBA) - no hero data here |
| 0x29AE | DELETE_HEROSKIN | 0x29AF | :3012350 DlmId@6 ok@A | hero / hero skin (TBA) - no hero data here |
| 0x29B0 | TBA_UPDATE_BATTLEFIELD_REWARD_COUNT | 0x29B1 | :3023898 DlmId@6 ok@A | TBA (hero-arena account) - no TBA data here |
| 0x29B2 | TBA_RESET_BATTLEFIELD_REWARD_COUNT | 0x29B3 | :3023771 DlmId@6 ok@A | TBA (hero-arena account) - no TBA data here |
| 0x29B5 | TBA_CHANGE_ACCOUNT_LEVEL | 0x29B6 | :3023070 DlmId@6 ok@A | TBA (hero-arena account) - no TBA data here |
| 0x29B7 | REQUEST_TBAUSER_DATA | 0x29B8 | :3021227 DlmId@E ok@12 | TBA (hero-arena account) - no TBA data here |
| 0x29BA | CHANGE_HERO | 0x29BB | :3010318 DlmId@E ok@12 | hero / hero skin (TBA) - no hero data here |
| 0x29BC | REQUEST_TBA_STORE_BUY_ITEM | 0x29BD | :3021272 DlmId@16 ItemBinary@6 ok@1A | TBA (hero-arena account) - no TBA data here |
| 0x29BE | TBA_USER_ENTERWORLD | 0x29BF | :3024230 DlmId@E UserData@6 HeroTemplateId@13 HeroSkinId@17 ok@12 | TBA; reply hands World a whole UserData blob (reload) |
| 0x29C2 | TBA_REQUEST_BATTLEPASS_DATA | 0x29C3 | :3023681 DlmId@1E ok@22 | TBA (hero-arena account) - no TBA data here |
| 0x29C4 | TBA_UPDATE_BATTLEPASS_LEVEL | 0x29C5 | :3023984 DlmId@E ok@12 | TBA (hero-arena account) - no TBA data here |
| 0x29C6 | TBA_RECEIVE_BATTLEPASS_REWARD | 0x29C7 | :3023531 DlmId@16 ItemBinary@6 ok@1A | TBA (hero-arena account) - no TBA data here |
| 0x29C8 | TBA_RECEIVE_BATTLEPASS_MISSION_REWARD | 0x29C9 | :3023488 DlmId@E ok@12 | TBA (hero-arena account) - no TBA data here |
| 0x29CA | TBA_UPDATE_BATTLEPASS_MISSION_COUNT | 0x29CB | :3024027 DlmId@E ok@12 | TBA (hero-arena account) - no TBA data here |
| 0x29CC | TBA_RESET_DAILY_BATTLEPASS_MISSION | 0x29CD | :3023814 DlmId@E ok@12 | TBA (hero-arena account) - no TBA data here |
| 0x29CE | TBA_UPDATE_BATTLEPASSTYPE | 0x29CF | :3023941 DlmId@E ok@12 | TBA (hero-arena account) - no TBA data here |
| 0x29D0 | TBA_UPDATE_BATTLEPASS_TOKEN_AMOUNT | 0x29D1 | :3024128 DlmId@E ok@12 | TBA (hero-arena account) - no TBA data here |
| 0x29D3 | TBA_REQUEST_RUNE_DATA | 0x29D4 | :3023726 DlmId@1E ok@22 | TBA (hero-arena account) - no TBA data here |
| 0x29D5 | TBA_REGISTER_RUNE | 0x29D6 | :3023640 DlmId@6 ok@12 | TBA (hero-arena account) - no TBA data here |
| 0x29D7 | TBA_CHANGE_USING_RUNEPAGE | 0x29D8 | :3023156 DlmId@6 ok@12 | TBA (hero-arena account) - no TBA data here |
| 0x29D9 | TBA_CHANGE_RUNEPAGENAME | 0x29DA | :3023113 DlmId@A ok@16 | TBA (hero-arena account) - no TBA data here |
| 0x29DB | TBA_ADD_RUNEPAGE | 0x29DC | :3023014 DlmId@12 ItemBinary@A ok@22 | TBA (hero-arena account) - no TBA data here |
| 0x29DD | TBA_SAVE_RUNEPAGE | 0x29DE | :3023857 DlmId@6 ok@12 | TBA (hero-arena account) - no TBA data here |
| 0x29DF | TBA_CLEAR_RUNEPAGE | 0x29E0 | :3023199 DlmId@6 ok@16 | TBA (hero-arena account) - no TBA data here |

### C - needs a real handler (the T166 spec; DbAckGroups.RealHandler; a bare ok would be wrong)

| op | name | reply | World reads | reply layout (Arbiter PDL dumper, frame offsets) | what the handler must do |
|---|---|---|---|---|---|
| 0x2717 | ADD_SERVANT | 0x2718 | :3009167 DlmId@2A ItemBinary@1A ServantPeriodList@22 ok@2E | ServantName:str@16 ItemBinary:bin@1A ServantPeriodList:bin@22 DlmId@2A Success:u8@2E ServantDbId:i64@2F ServantType@37 TemplateId@3B Level@43 BuffGrade@4B ConditionalSkillId1@4F ConditionalSkillId2@53 ConditionalSkillId3@57 (91B) | servant created: ServantDbId, type, template, name, skills, period list, atoms |
| 0x2721 | SERVANT_ADVENTURE_RECEIVE_REWARD | 0x2722 | :3021911 DlmId@E ItemBinary@6 ?@13 ok@12 | ItemBinary:bin@6 DlmId@E Success:u8@12 (23B) | atoms echoed + i32@13 World reads |
| 0x2746 | SET_MONEY | 0x2748 | :3022071 DlmId@6 CurrentMoney@B ok@A | DlmId@6 Success:u8@A CurrentMoney:i64@B (19B) | [DlmId@6][ok@0A][CurrentMoney i64@0B] - World sets the money from it |
| 0x2747 | GET_MONEY | 0x2749 | :3013530 nothing (no-op reader) | DlmId@6 Success:u8@A CurrentMoney:i64@B (19B) | [DlmId@6][ok@0A][CurrentMoney@0B]; World reader is a no-op |
| 0x2750 | EQUIP_PARTNER_STYLE_ITEM | 0x2751 | :1147676 DlmId@E StoreBinary@6 Error@13 ok@12 | StoreBinary:bin@6 DlmId@E Success:u8@12 Error@13 (23B) | StoreBinary echoed + Error@13 (+ WareCommision) |
| 0x2752 | UNEQUIP_PARTNER_STYLE_ITEM | 0x2753 | :1147925 DlmId@E StoreBinary@6 Error@13 ok@12 | StoreBinary:bin@6 DlmId@E Success:u8@12 Error@13 WareCommision:i64@17 (31B) | StoreBinary echoed + Error@13 (+ WareCommision) |
| 0x275A | ITEM_EXTRACT | 0x275B | :3015236 DlmId@E ItemBinary@6 ok@12 | ItemBinary:bin@6 DlmId@E Success:u8@12 (19B) | atoms echoed: [ref@6 -> 856-B atoms][DlmId@0E][ok@12]; the Arbiter runs ExecTrans and writes back the SAME vector - needs the item-upgrade atom ops modelled |
| 0x275C | ITEM_DECOMPOSE | 0x275D | :3014978 see reader (24 lines) | ItemBinary:bin@6 DlmId@E Success:u8@12 (19B) | World reader (no read, returns false) - any reply is refused; the Arbiter handler is a stub. Never answer |
| 0x276C | ITEM_DELIVER | 0x276D | :3015060 DlmId@16 OwnerBinary@6 TargetBinary@E ok@1A | OwnerBinary:bin@6 TargetBinary:bin@E DlmId@16 Success:u8@1A (27B) | two atom lists (owner, target) - the target is another character |
| 0x276E | ITEM_ENCHANT | 0x276F | :3015122 DlmId@E OwnerBinary@6 ok@12 | OwnerBinary:bin@6 DlmId@E Success:u8@12 (19B) | atoms echoed: [ref@6 -> 856-B atoms][DlmId@0E][ok@12]; the Arbiter runs ExecTrans and writes back the SAME vector - needs the item-upgrade atom ops modelled |
| 0x2770 | ITEM_ENCHANT_IDENTIFY | 0x2771 | :3015178 DlmId@E OwnerBinary@6 ok@12 | OwnerBinary:bin@6 DlmId@E Success:u8@12 (19B) | atoms echoed: [ref@6 -> 856-B atoms][DlmId@0E][ok@12]; the Arbiter runs ExecTrans and writes back the SAME vector - needs the item-upgrade atom ops modelled |
| 0x2772 | ITEM_CUSTOMIZING | 0x2773 | :3014886 UpdateType@12 OwnerBinary@6 DlmId@E ok@16 | OwnerBinary:bin@6 DlmId@E UpdateType@12 Success:u8@16 (23B) | [ref@6 atoms][DlmId@0E][UpdateType@12 echo][ok@16]; atoms echoed |
| 0x2774 | ITEM_MERGE | 0x2775 | :3015406 DlmId@6 ok@A | DlmId@6 Success:u8@A (11B) | [DlmId@6][ok@0A]; request atoms (ref@6) must be applied - merge ops not modelled |
| 0x2790 | USER_LEARN_SKILL_FOR_MULTIPLE | 0x2791 | :3027339 DlmId@1E SkillPeriodList@E AddLearnSkillResult@16 AddedSkillPeriod@23 ok@22 ?@24 | ItemBinary:bin@6 SkillPeriodList:bin@E AddLearnSkillResult:bin@16 DlmId@1E Success:u8@22 AddedSkillPeriod:u8@23 (37B) | atoms + SkillPeriodList + AddLearnSkillResult lists **Landed: T164 real handler.** |
| 0x2798 | USER_LEARN_HIDE_PASSIVE_SKILL | 0x2799 | :3027223 DlmId@E ResultList@6 ok@12 | ResultList:bin@6 DlmId@E Success:u8@12 (19B) | [ref@6 ResultList (id,bool) pairs][DlmId@0E][ok@12] |
| 0x27B5 | RESET_EXTRA_POINT_DATA | 0x27B6 | :3021373 DlmId@6 ok@A | DlmId@6 Success:u8@A (11B) | [DlmId@6][ok@0A]; EP pages/points live in characters.ep_* (T77) and must change with it |
| 0x27B7 | CHANGE_GOLD_CONSUMPTION | 0x27B8 | :3010275 DlmId@6 ok@A | DlmId@6 Success:u8@A (11B) | [DlmId@6][ok@0A]; the gold total must change |
| 0x27BF | USER_INCREASE_EP_POINT_BY_ITEM | 0x27C0 | :3027138 DlmId@6 ok@A | DlmId@6 Success:u8@A (11B) | [DlmId@6][ok@0A]; EP pages/points live in characters.ep_* (T77) and must change with it |
| 0x27E6 | USER_CLEAR_ALL_SKILL | 0x27E7 | :3026833 DlmId@16 PassiveLearned@E SkillLearned@6 ok@1A | SkillLearned:bin@6 PassiveLearned:bin@E DlmId@16 Success:u8@1A (27B) | [ref SkillLearned][ref PassiveLearned][DlmId@16][ok@1A] |
| 0x2804 | DELETE_USER_ACHIEVEMENT | 0x2805 | :3012761 DlmId@E AchievementList@6 ok@12 | AchievementList:bin@6 DlmId@E Success:u8@12 (19B) | AchievementList |
| 0x2815 | GROUP_DUEL_RETURN | 0x2816 | :3013731 DlmId@E SentAllByParcel@17 ItemBinary@6 ok@12 | ItemBinary:bin@6 DlmId@E Success:u8@12 ErrorCode@13 SentAllByParcel:u8@17 (24B) | atoms + SentAllByParcel@17 |
| 0x282F | SIMULATE_ITEM_TOOLTIP | 0x282E | :3022673 GameId@6 SendToUserDbId@22 CompareItemDbId@1A ContentId@12 ToolTipType@E (no generic completion) | GameId:i64@6 ToolTipType@E ContentId:i64@12 CompareItemDbId:i64@1A SendToUserDbId@22 (38B) | item tooltip from the item store (no generic completion) |
| 0x2835 | LOAD_USER_VIP_INFO | 0x2836 | :3019662 DlmId@F SlotList@6 PubExp@13 GameExp@17 TokenAmount@1B LastResetTime@23 ResetCount@2B ok@E | SlotList:bin@6 Success:u8@E DlmId@F PubExp@13 GameExp@17 TokenAmount:i64@1B LastResetTime:i64@23 ResetCount@2B (47B) | VIP exp/pub exp/tokens/slot list |
| 0x2839 | BUY_VIP_STORE_ITEM | 0x283A | :3009788 DlmId@E ItemBinary@6 ok@12 | ItemBinary:bin@6 DlmId@E Success:u8@12 (19B) | store purchase: 568-B GiveTake records echoed (as 0x27CD) + the point/currency the DB deducts |
| 0x283B | ADD_VIP_GAME_EXP | 0x283C | :3009316 DlmId@6 NewResult@B ok@A | DlmId@6 Success:u8@A NewResult@B (15B) | [DlmId@6][ok@0A][NewResult i32@0B] - World sets VIP exp to the new TOTAL |
| 0x2850 | RIGHT_ITEM_LIST | 0x2851 | :3021798 DlmId@E RightItemList@6 | RightItemList:bin@6 DlmId@E (18B) | right-item list |
| 0x2852 | USE_RIGHT_ITEM | 0x2853 | :3028244 DlmId@E RightItemList@6 ok@1E | RightItemList:bin@6 DlmId@E OwnerDbId@12 RightIndex@16 ItemTemplateId@1A Success:u8@1E (31B) | right-item list |
| 0x285E | CHANGE_ACCESSORY_TRANSFORM | 0x285F | :3009845 DlmId@16 ItemBinary@6 ItemChange@E ok@1A | ItemBinary:bin@6 ItemChange:bin@E DlmId@16 Success:u8@1A (27B) | ItemChange atom + atoms |
| 0x286B | UPDATE_DUNGEON_RANK_RECORD | 0x286C | :3025439 nothing (no-op reader) | Success:u8@6 (7B) | no DlmId ([ok@6]); World reader is a no-op - persistence only (ranking boards) |
| 0x289B | ITEM_POINT_STORE | 0x289C | :3015446 DlmId@E ItemBinary@6 ok@12 | ItemBinary:bin@6 DlmId@E Success:u8@12 (19B) | store purchase: 568-B GiveTake records echoed (as 0x27CD) + the point/currency the DB deducts |
| 0x289D | ITEM_GUILD_STORE | 0x289E | :3015350 DlmId@E ItemBinary@6 ok@12 | ItemBinary:bin@6 DlmId@E Success:u8@12 (19B) | store purchase: 568-B GiveTake records echoed (as 0x27CD) + the point/currency the DB deducts |
| 0x28A1 | ITEM_UNIDENTIFY | 0x28A2 | :3015847 DlmId@E OwnerBinary@6 ok@12 | OwnerBinary:bin@6 DlmId@E Success:u8@12 (19B) | atoms echoed: [ref@6 -> 856-B atoms][DlmId@0E][ok@12]; the Arbiter runs ExecTrans and writes back the SAME vector - needs the item-upgrade atom ops modelled |
| 0x28AB | POLITICS_POINT_STORE | 0x28AC | :3020213 DlmId@E ItemBinary@6 ok@12 | ItemBinary:bin@6 DlmId@E Success:u8@12 (19B) | store purchase: 568-B GiveTake records echoed (as 0x27CD) + the point/currency the DB deducts |
| 0x28AE | MARK_AS_QUEST_COMPLETED | 0x28AF | :3019850 CompleteQuestList@6 DlmId@E | CompleteQuestList:bin@6 DlmId@E (18B) | [ref@6 CompleteQuestList][DlmId@0E] - the quests NEWLY completed (already-complete ones are dropped) **Landed: T164 real handler.** |
| 0x28C3 | INITIALIZE_LEFT_COOL_TIME_PREMIUM_SLOT | 0x28C4 | :3014229 DlmId@6 ok@A SlotSetId@B SlotPos@F | DlmId@6 Success:u8@A SlotSetId@B SlotPos@F (19B) | [DlmId@6][ok@0A][SlotSetId@0B][SlotPos@0F] - request fields echoed |
| 0x28C7 | UPDATE_REDUCE_SERVANT_PERIOD | 0x28C8 | :3026115 DlmId@E ServantPeriodList@6 ok@12 | ServantPeriodList:bin@6 DlmId@E Success:u8@12 (19B) | period lists |
| 0x28CB | UPDATE_REDUCE_SKILLPERIOD | 0x28CC | :3026173 DlmId@E SkillPeriodList@6 ok@12 | SkillPeriodList:bin@6 DlmId@E Success:u8@12 (19B) | period lists |
| 0x28EE | SHARED_ACCOUNT_DATA | 0x28EF | :1643095 SharedTaskType@A DlmId@6 ok@E | DlmId@6 SharedTaskType@A Success:u8@E (15B) | [DlmId@6][SharedTaskType@0A echo][ok@0E] + request atoms |
| 0x28F4 | ENCHANT_ITEM_BOOST | 0x28F5 | :3013079 DlmId@E OwnerBinary@6 ok@12 | OwnerBinary:bin@6 DlmId@E Success:u8@12 (19B) | atoms echoed: [ref@6 -> 856-B atoms][DlmId@0E][ok@12]; the Arbiter runs ExecTrans and writes back the SAME vector - needs the item-upgrade atom ops modelled |
| 0x28F6 | UPDATE_ITEM_CUSTOMEXITEM | 0x28F7 | :3025708 DlmId@E ok@12 | OwnerBinary:bin@6 DlmId@E Success:u8@12 (19B) | [ref@6 atoms echoed][DlmId@0E][ok@12]; World reads only DlmId/ok, but the atoms change the custom-ex item |
| 0x2900 | LOAD_LIMITED_DROP_POINT | 0x2901 | :3017237 DlmId@16 LimitedDropGaugeInfoList@6 LimitedDropInfoList@E ok@1A | LimitedDropGaugeInfoList:bin@6 LimitedDropInfoList:bin@E DlmId@16 Success:u8@1A (27B) | limited-drop info/gauge lists |
| 0x290A | LOAD_ADDITIONAL_FATIGUEPOINT | 0x290B | :3016121 DlmId@7 AdditionalPoint@B ok@6 | Success:u8@6 DlmId@7 AdditionalPoint@B (15B) | [ok@6][DlmId@7][AdditionalPoint@0B] |
| 0x2920 | ITEM_DECOMPOSITION | 0x2921 | :3015003 DlmId@E ItemBinary@6 ok@12 | ItemBinary:bin@6 DlmId@E Success:u8@12 (19B) | atoms echoed: [ref@6 -> 856-B atoms][DlmId@0E][ok@12]; the Arbiter runs ExecTrans and writes back the SAME vector - needs the item-upgrade atom ops modelled |
| 0x2928 | ITEM_FLOATING_CASTLE_PASTS_STORE | 0x2929 | :3015292 DlmId@E ItemBinary@6 ok@12 | ItemBinary:bin@6 DlmId@E Success:u8@12 (19B) | store purchase: 568-B GiveTake records echoed (as 0x27CD) + the point/currency the DB deducts |
| 0x292A | OPEN_FLOATING_CASTLE_PARTS_STORE | 0x292B | :3020065 DlmId@E PartsItemList@6 ok@12 | PartsItemList:bin@6 DlmId@E Success:u8@12 (19B) | PartsItemList |
| 0x292E | UPDATE_CUSTOMIZING_COMBINE_RESULT | 0x292F | :3025211 DlmId@E ItemBinary@6 ok@12 | ItemBinary:bin@6 DlmId@E Success:u8@12 (19B) | atoms echoed: [ref@6 -> 856-B atoms][DlmId@0E][ok@12]; the Arbiter runs ExecTrans and writes back the SAME vector - needs the item-upgrade atom ops modelled |
| 0x2932 | ITEM_AWAKEN | 0x2933 | :3014830 DlmId@E OwnerBinary@6 ok@12 | OwnerBinary:bin@6 DlmId@E Success:u8@12 (19B) | atoms echoed: [ref@6 -> 856-B atoms][DlmId@0E][ok@12]; the Arbiter runs ExecTrans and writes back the SAME vector - needs the item-upgrade atom ops modelled |
| 0x2934 | ITEM_UNBIND | 0x2935 | :3015791 DlmId@E ItemBinary@6 ok@12 | ItemBinary:bin@6 DlmId@E Success:u8@12 (19B) | atoms echoed: [ref@6 -> 856-B atoms][DlmId@0E][ok@12]; the Arbiter runs ExecTrans and writes back the SAME vector - needs the item-upgrade atom ops modelled |
| 0x2940 | ADMIN_USER_DAILY_ATTENDANCE | 0x2941 | :3009359 DlmId@6 AttendBitmap@B ok@A | DlmId@6 Success:u8@A AttendBitmap:i64@B (19B) | [DlmId@6][ok@0A][AttendBitmap i64@0B] |
| 0x294A | CHECK_PLAYTIME_REWARD | 0x294B | :3010611 DlmId@6 EventId@B ItemTid@F ItemCount@13 ok@A | DlmId@6 Success:u8@A EventId@B ItemTid@F ItemCount@13 (23B) | [DlmId@6][ok@0A][EventId][ItemTid][ItemCount] |
| 0x295F | EQUIPMENT_INHERITANCE | 0x2960 | :3013179 DlmId@E ItemBinary@6 ok@12 | ItemBinary:bin@6 DlmId@E Success:u8@12 (19B) | atoms echoed: [ref@6 -> 856-B atoms][DlmId@0E][ok@12]; the Arbiter runs ExecTrans and writes back the SAME vector - needs the item-upgrade atom ops modelled |
| 0x2961 | OPEN_DUAL_OPTION | 0x2962 | :3020009 DlmId@E ItemBinary@6 ok@12 | ItemBinary:bin@6 DlmId@E Success:u8@12 (19B) | atoms echoed: [ref@6 -> 856-B atoms][DlmId@0E][ok@12]; the Arbiter runs ExecTrans and writes back the SAME vector - needs the item-upgrade atom ops modelled |
| 0x2963 | CHANGE_DUAL_OPTION_IDX | 0x2964 | :3010033 DlmId@E ItemBinary@6 ok@12 | ItemBinary:bin@6 DlmId@E Success:u8@12 (19B) | atoms echoed: [ref@6 -> 856-B atoms][DlmId@0E][ok@12]; the Arbiter runs ExecTrans and writes back the SAME vector - needs the item-upgrade atom ops modelled |
| 0x2969 | UPDATE_EVENTSYSTEM_PROGRESS | 0x296A | :3025455 see reader (79 lines) | IsRewardUpdate:u8@E (15B) | no DlmId; World re-reads its progress list from the reply |
| 0x296B | ALCHEMY | 0x296C | :3009404 DlmId@E ItemBinary@6 ok@12 | ItemBinary:bin@6 DlmId@E Success:u8@12 (19B) | atoms echoed: [ref@6 -> 856-B atoms][DlmId@0E][ok@12]; the Arbiter runs ExecTrans and writes back the SAME vector - needs the item-upgrade atom ops modelled |
| 0x296D | CHANGE_EQUIPMENT_EXP | 0x296E | :3010175 DlmId@E ItemBinary@6 ok@12 | ItemBinary:bin@6 DlmId@E Success:u8@12 (19B) | atoms echoed: [ref@6 -> 856-B atoms][DlmId@0E][ok@12]; the Arbiter runs ExecTrans and writes back the SAME vector - needs the item-upgrade atom ops modelled |
| 0x2971 | SKILL_POLISHING_UNLOCK_OPTION | 0x2972 | :3022830 DlmId@6 ok@A | DlmId@6 Success:u8@A (11B) | [DlmId@6][ok@0A]; skill-polishing level/exp/options persist here and come back in LOAD_SKILL_POLISHING |
| 0x2973 | SKILL_POLISHING_CHANGE_OPTION | 0x2974 | :3022787 DlmId@6 ok@A | DlmId@6 Success:u8@A (11B) | [DlmId@6][ok@0A]; skill-polishing level/exp/options persist here and come back in LOAD_SKILL_POLISHING |
| 0x2975 | LOAD_SKILL_POLISHING | 0x2976 | :3018519 DlmId@16 ok@1A ?@A ?@12 SkillPolishingLevel@1B SkillPolishingPoint@1F ?@23 SkillPolishingExp@27 | DlmId@16 Success:u8@1A SkillPolishingLevel@1B SkillPolishingPoint@1F SkillPolishingExp:i64@27 (47B) | [ref][..][DlmId@0E][ok@1A][Point][Level][Exp]... - the polishing state |
| 0x2977 | SKILL_POLISHING_UPGRADE_LEVEL | 0x2978 | :3022873 DlmId@6 ok@A | DlmId@6 Success:u8@A (11B) | [DlmId@6][ok@0A]; skill-polishing level/exp/options persist here and come back in LOAD_SKILL_POLISHING |
| 0x2979 | SKILL_POLISHING_ADD_EXP | 0x297A | :3022744 DlmId@6 ok@A | DlmId@6 Success:u8@A (11B) | [DlmId@6][ok@0A]; skill-polishing level/exp/options persist here and come back in LOAD_SKILL_POLISHING |
| 0x2981 | LOAD_PURCHASE_LIMIT | 0x2982 | :3017704 DlmId@E ?@A ok@12 | DlmId@E Success:u8@12 (19B) | purchase-limit list |
| 0x298E | CREATE_CARD_INFO | 0x298F | :3011276 DlmId@6 ok@A | DlmId@6 Success:u8@A CurrentPresetAmount@B CurrentPresetIndex@F CollectionBookLevel@13 CollectionBookPoint@17 (27B) | card / collection book state |
| 0x2990 | INCREASE_CARD_PRESET | 0x2991 | :3013870 DlmId@E ItemBinary@6 ok@12 | ItemBinary:bin@6 DlmId@E Success:u8@12 (23B) | card / collection book state |
| 0x2992 | ACTIVATE_CARD_COMBINE_LIST | 0x2993 | :3009029 DlmId@6 ok@A | DlmId@6 Success:u8@A CombineListId@B Level@F (19B) | card / collection book state |
| 0x2994 | DEACTIVATE_CARD_COMBINE_LIST | 0x2995 | :3011975 DlmId@6 ok@A | DlmId@6 Success:u8@A CombineListId@B Level@F (19B) | card / collection book state |
| 0x2996 | RECEIVE_COLLECTION_BOOK_REWARD | 0x2997 | :3020271 ?@13 DlmId@E ItemBinary@6 ok@12 | ItemBinary:bin@6 DlmId@E Success:u8@12 RewardId@17 (27B) | card / collection book state |
| 0x2998 | CHANGE_CARD_PRESET | 0x2999 | :3009911 DlmId@6 ok@A | DlmId@6 Success:u8@A CurrentPresetIndex@B (15B) | card / collection book state |
| 0x299A | EXPAND_EP_PAGE | 0x299B | :3013358 DlmId@6 ok@A | DlmId@6 Success:u8@A (11B) | [DlmId@6][ok@0A]; EP pages/points live in characters.ep_* (T77) and must change with it |
| 0x299C | CHANGE_EP_PAGE | 0x299D | :3010134 DlmId@6 ok@A | DlmId@6 Success:u8@A (11B) | [DlmId@6][ok@0A]; EP pages/points live in characters.ep_* (T77) and must change with it |
| 0x299E | REQUEST_GUILD_QUEST_WEEKLY_REWARD_ITEM_TRANSACTION | 0x299F | :3021044 DlmId@12 RewardTransactions@6 Isok@1E | RewardTransactions:bin@6 UserDbID@E DlmId@12 RewardStep@16 GuildSizeRank@1A IsSuccess:u8@1E (31B) | RewardTransactions + IsSuccess |
| 0x27DB | ADD_GUILDMEMBER2 | 0x27DC | :3009072 | reads DlmId@6 ok@0A; reply [DlmId@6][ok@0A] (11B); guild membership is in our guild store - add the member first (walk-found) | walk-found (a name in another .cs hid it from the name search) |
| 0x2858 | ASK_CHANGE_GUILD_NAME | 0x2859 | :3009629 | reads DlmId@6 ok@0A; reply [DlmId@6][ok@0A][ErrorType@0B] (15B); the uniqueness verdict comes from the guild store (walk-found) | walk-found (a name in another .cs hid it from the name search) |
| 0x2821 | TRADE_BROKER_START_DEAL | 0x2822 | :3024674 | no DlmId (never wedges); reply names, item and prices of the deal (76B) from the broker store (walk-found) | walk-found (a name in another .cs hid it from the name search) |
| 0x2824 | TRADE_BROKER_CANCEL_DEAL | 0x282C | :3024558 | no DlmId (never wedges); reply [UserDbId@6] (10B) (walk-found) | walk-found (a name in another .cs hid it from the name search) |

### Already answered under another name (the name search counted them)

| op | table name | answered as |
|---|---|---|
| 0x27B3 | UPDATE_DAILY_LIMIT_EP_EXP | 0x27B3 SDB_LOAD_WORLD_EVENT (T-early, [reqId][ok]) |
| 0x27B9 | USER_LOAD_EP_PERK | 0x27B9 SDB_EP_PERK (static EpPerk) |
| 0x2924 | UPDATE_PASSIVITY_COOLTIME | 0x2924 SDB_SAVE_2924 |
| 0x2930 | UPDATE_HOLD_CHARACTER_STATUS | 0x2930 (T148 LoadHoldCharacterStatus) |
| 0x2936 | CHECK_DAILY_ATTENDANCE | 0x2936 SDB_SAVE_2936 |

### Elsewhere - twins the name search skipped, answered or sealed outside DbProxyHandlers (DbAckGroups.Elsewhere)

| op | name | where |
|---|---|---|
| 0x2754 | MOVE_WAREHOUSE_ITEM | sealed one-way (WorldReplayTable): the real Arbiter handler is a stub, T42 |
| 0x2785 | CHECK_NEW_GUILD_NAME | needs no answer: World reader :3010595 is a no-op; GuildHandlers checks names |
| 0x27A7 | LOAD_TELEPORT_TO_POS_LIST | replay table, login load (DispatchOnlyForTests) |
| 0x2809 | FETCH_THROUGH_ARBITER_CONTRACT | ContractBroker via WorldBridge (T60) |
| 0x280C | ASK_THROUGH_ARBITER_CONTRACT | ContractBroker via WorldBridge (T60) |
| 0x280E | SEND_END_THROUGH_ARBITER_CONTRACT | ContractBroker via WorldBridge (T60) |
| 0x2833 | LOAD_USER_RESTRICTION | replay table, login load (DispatchOnlyForTests) |
| 0x2895 | LOAD_BATTLE_FIELD_LIST | replay table, login load (DispatchOnlyForTests) |
| 0x28BB | LOAD_ACCOUNT_BENEFIT | replay table, login load (DispatchOnlyForTests) |
| 0x28C5 | LOAD_SERVANT_PERIOD | replay table, login load (DispatchOnlyForTests) |
| 0x28C9 | LOAD_SKILLPERIOD | replay table, login load (DispatchOnlyForTests) |
| 0x28CF | LOAD_LEARNED_SOCIAL | replay table, login load (DispatchOnlyForTests) |
| 0x28FE | LOAD_TOKEN_EXCHANGE | replay table, login load (DispatchOnlyForTests) |
| 0x2912 | LOAD_PROMOTION_LIST | replay table, login load (DispatchOnlyForTests) |
| 0x2922 | LOAD_PASSIVITY_COOLTIME | replay table, login load (DispatchOnlyForTests) |
| 0x2958 | CHANGE_CITY_WAR_STATE | sealed one-way (WorldReplayTable, CLAUDE.md section 3) |

## T167 - cards, EP pages, skill polishing, dungeon rank (T165 group C, slice 2)

Real handlers in `World/DbProxyT167.cs`; the 15 names now read as landed in `DbAckGroups`. Only the
three login loads have captured pairs (cap_social4, all empty state) and are byte-exact
(`data/cap_t167.bin`); every other layout is from the Arbiter's dumper + writer and World's reader.

| write (W->A) | reply | shape | feeds |
|---|---|---|---|
| 0x2986 SDB_REQUEST_CARD_DATA (22 B) | 0x2987 | 4 lists [count][first] (cards, presets, combines, rewards), DlmId@32, ok, amount, index, book level, book point; 16-B linked elements; cap_social4 446->447 | reads `cards`, `card_mounts`, `card_info`, `card_combines`, `characters.card_preset_index` |
| 0x298E SDB_CREATE_CARD_INFO (34 B) | 0x298F | [DlmId][ok][amount][index][level][point] | `card_info` (index echoed: the frame names no character) |
| 0x2998 SDB_CHANGE_CARD_PRESET (26 B) | 0x2999 | [DlmId][ok][index] | `characters.card_preset_index` |
| 0x2990 SDB_INCREASE_CARD_PRESET (30 B + atoms) | 0x2991 | ref 23 + atoms echoed, DlmId, ok, new amount | `card_info.preset_amount` + 1, `items` |
| 0x2992 / 0x2994 SDB_(DE)ACTIVATE_CARD_COMBINE_LIST (26 B) | +1 | [DlmId][ok][id][level]; deactivating an inactive list = ok 0 | `card_combines` |
| 0x27B9 SDB_USER_LOAD_EP_PERK (14 B) | 0x27BA | 5 pages [count 5][first], DlmId@8, ok, UsedEp, PreEpLevel, PreEpTotalPoint, CurrentPage, MaxPage; each page [self][next][n][first] then its perks (id, level); cap_social4 431->432 | reads `ep_perks`, `characters.ep_*` |
| 0x27BB SDB_USER_LEARN_EP_PERK (T77) | 0x27BC | unchanged | now also `ep_perks` (current page), `ep_used_point` += UseEpPoint |
| 0x27BD SDB_USER_RESET_EP_PERK (T77) | 0x27BE | unchanged | now also clears the current page, `ep_used_point` = 0 |
| 0x27C1 SDB_UPDATE_PRE_EP_INFO (T77) | 0x27C2 | unchanged | now also `ep_pre_level`, `ep_pre_total_point` |
| 0x299C SDB_CHANGE_EP_PAGE (22 B) | 0x299D | [DlmId][ok] | `ep_current_page`, `ep_used_point` |
| 0x299A SDB_EXPAND_EP_PAGE (22 B + atoms) | 0x299B | [DlmId][ok] | `ep_max_page` + 1 = current page, `items` |
| 0x27B5 SDB_RESET_EXTRA_POINT_DATA (14 B) | 0x27B6 | [DlmId][ok] | T77's ep exp/level/point/daily exp/reserve/reset -> 0 (daily limit kept) |
| 0x27BF SDB_USER_INCREASE_EP_POINT_BY_ITEM (18 B) | 0x27C0 | [DlmId][ok] | `ep_point` += gain |
| 0x2975 SDB_LOAD_SKILL_POLISHING (14 B) | 0x2976 | option / level lists, DlmId@16, ok, level, point, total, exp i64; option 17 B (id, effect, applied), level 16 B (id, effect); cap_social4 329->330 | reads `skill_polishing*` |
| 0x2979 SDB_SKILL_POLISHING_ADD_EXP (42 B + atoms) | 0x297A | [DlmId][ok] | `skill_polishing` = World's four values, `items` |
| 0x2971 SDB_SKILL_POLISHING_UNLOCK_OPTION (38 B + atoms) | 0x2972 | [DlmId][ok]; ok 0 when points are short | option (id, effect) created + applied, old one unapplied, points spent |
| 0x2973 SDB_SKILL_POLISHING_CHANGE_OPTION (26 B) | 0x2974 | [DlmId][ok]; ok 0 for an option never unlocked | the applied flag moves |
| 0x2977 SDB_SKILL_POLISHING_UPGRADE_LEVEL (34 B + atoms) | 0x2978 | [DlmId][ok]; ok 0 when points are short | `skill_polishing_levels`, points spent |
| 0x286B SDB_UPDATE_DUNGEON_RANK_RECORD (63 B) | none | no DlmId; the real Arbiter answers World nothing and sends the client S_DUNGEON_RANK_END_POINT (0x8B06, not built: no capture) | `dungeon_rank_records`; the PvE board ranks by summed best TopPointRecord, clear counts for characters without one |

Not modelled: ReceivedCollectionBookRewards (always empty until RECEIVE_COLLECTION_BOOK_REWARD
lands), the client's S_DUNGEON_RANK_END_POINT, per-season filtering on the PvE board.

## T168 - group C, slice 3 (World/DbProxyT168.cs)

Decompile-derived unless marked; the two loads are byte-exact against cap_bag / cap_final (data/cap_t168.bin).

| Ops | Reply | Feeds |
|---|---|---|
| Echo family (DbAckTable-syntax rows, T166 path): 2961 OPEN_DUAL_OPTION, 2963 CHANGE_DUAL_OPTION_IDX, 296B ALCHEMY, 296D CHANGE_EQUIPMENT_EXP, 292E/28F6/2772 customizing, 289B ITEM_POINT_STORE, 289D ITEM_GUILD_STORE, 28AB POLITICS_POINT_STORE, 2928 floating castle store, 2839 VIP store, 276C ITEM_DELIVER, 285E CHANGE_ACCESSORY_TRANSFORM, 2721 servant adventure reward, 2996 RECEIVE_COLLECTION_BOOK_REWARD, 299E guild quest reward, 28EE SHARED_ACCOUNT_DATA, 28C7/28CB reduce period, 2804 DELETE_USER_ACHIEVEMENT, 2852 USE_RIGHT_ITEM | [atoms echoed][DlmId][ok 1] + the request fields World reads back | `items` via the atoms; 2996 also `card_book_rewards` (served by the 0x2986 card load) |
| 2746 SET_MONEY / 2747 GET_MONEY (GM) | [DlmId][ok][money i64] | characters.money |
| 27B7 CHANGE_GOLD_CONSUMPTION | [DlmId][ok] | characters.gold_consumption |
| 283B ADD_VIP_GAME_EXP / 2835 LOAD_USER_VIP_INFO | [DlmId][1][total] / 41 B: slot list, ok, DlmId, pub, game, token i64, reset i64, count | `vip_info` (per account, clamped to int) |
| 2940 ADMIN_USER_DAILY_ATTENDANCE | [DlmId][ok] | characters.attend_bitmap (bit per login day) |
| 2798 USER_LEARN_HIDE_PASSIVE_SKILL | [ref (id, learned) x n][DlmId][1] | `hidden_passives` |
| 2717 ADD_SERVANT | 85 B: name, atoms, empty period list, DlmId, ok, servant id i64, type, template, level 1, 3 skills | `servants`, `items` |
| 27DB ADD_GUILDMEMBER2 / 2858 ASK_CHANGE_GUILD_NAME | [DlmId][ok] / [DlmId][ok][ErrorType 1 = taken] | guild members / name check |
| 2850 RIGHT_ITEM_LIST, 290A additional fatigue, 28C3 premium-slot cooltime, 292A floating castle parts | empty list / zero / echo, ok 1 | nothing kept |
| 2900 / 2981 loads | captured empty builders, now allow-listed | - |
| Refusals, ok 0: 294A CHECK_PLAYTIME_REWARD, 2750/2752 partner style, 2815 GROUP_DUEL_RETURN | fixed-size zero reply + DlmId | no state kept |
| 2821 / 2824 broker deals | none (no DlmId; logged) | T55 has no listings |

Left in group C on purpose (walk test pins these four):

| Op | Why |
|---|---|
| 0x275C ITEM_DECOMPOSE | dead on both sides; World's reader returns false |
| 0x27E6 USER_CLEAR_ALL_SKILL | T150b pin; the reply rebuilds the skill lists |
| 0x282F SIMULATE_ITEM_TOOLTIP | no DlmId; needs the item-link system |
| 0x2969 UPDATE_EVENTSYSTEM_PROGRESS | no DlmId; World re-schedules event jobs from it |

Brief names already answered elsewhere: USE_CHRONOSCROLL, RECEIVE_PLAYTIME_REWARD, servant feed /
storage / adventure, GIVE_QUEST_BACKUP_ITEM (group A rows); CHECK_DAILY_ATTENDANCE (captured builder).

Not modelled: VIP store slot list, guild money for ITEM_GUILD_STORE, 2721/2996 @0x13 (0),
servant loads (still replayed), premium-slot cooltimes, purchase limits.

## T170 - real-Arbiter pins and the last two C ops

| Item | Status | Pin / source |
|---|---|---|
| 0x27E6 USER_CLEAR_ALL_SKILL | **pinned** - both blob skill regions zeroed (6880, 4320 B), reply 21 + 4000 + 320 zero bytes | cap_clearallskill 902/903 |
| 0x2969 UPDATE_EVENTSYSTEM_PROGRESS | decompile-only - `eventsystem_progress` (event, user, account): value set, flag1 kept unless overwrite; reply to the asking link (the real one broadcasts) | Arbiter handler + UpdateProgressInfo |
| 0x282F SIMULATE_ITEM_TOOLTIP | T169's reference patch applied (World's answer to the 0x282E ask) | cap_final2a/2b |
| EP writes 27C1 / 27BB x4 / 299A / 299C / 27BD + the loads after relog | **pinned**, replayed in order through the store | cap_final2b 682..6313, 29643 |
| 0x2986 card loads (empty, 1 mounted card, 1 unmounted) | **pinned** with the card seeded (no card write in any capture) | cap_final2b 359 / 701 / 29661, cap_final 2931 |
| 0x2988-0x2998 card writes | decompile-only - not in cap_final or cap_final2b | - |
| Mail collect atom op 37 | modelled: DO_TS_RECV_PARCEL marker, parcel id @0x278, inert (the recv path marks the parcel) | cap_social 1555/1556 (every collect has one) |
| Guild war accept / surrender | **pinned** - see GUILD-WAR.md section 8; `guild_wars` gains both sides' flags and money | cap_final2a + 2b |
| Guild quest start / cancel | **pinned** - concurrent quests, SMT 3809 / 3909, S_FAIL_GUILD_QUEST, 0x1453 | cap_final2a 1549..4656, tap 7776 |
| Guild quest timeout | not captured | - |

Group C left: 0x275C ITEM_DECOMPOSE only (dead on both sides).
