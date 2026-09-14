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
| 0x273B S_UPDATE_EXP_LEVEL (46 B)                  | 0x273C        | [u32 reqId][u8 ok]                            | 12    | real (T6), writes level/exp to the row |
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

## The Arbiter Contract family — one-way, T47

`0x2809` and `0x280E` turned up in the first two-client test (58-66 B each) and are the two-party
interactions the Arbiter brokers, which is why one player never sees them. **None of the family
carries a DlmId** — the dumpers name `ContractorDbId`, `ContractType`, `ContractId` and nothing
else — so an unanswered one cannot head-block the per-user DB queue the way a missing `DBS_` reply
does. They are in `WorldReplayTable.OneWayFromWorld` so the replay table cannot hand them somebody
else's reply.

| opcode | name | handler | sends |
|---|---|---|---|
| `0x2809` | SDB_FETCH_THROUGH_ARBITER_CONTRACT | `Arb_part_063.c:7017` | nothing — dispatches on ContractType (4, 5, 10, 0x23) into four managers |
| `0x280C` | SDB_ASK_THROUGH_ARBITER_CONTRACT | `Arb_part_063.c:810` | nothing (38 lines) |
| `0x280D` | SDB_SEND_BEGIN_THROUGH_ARBITER_CONTRACT | `Arb_part_064.c:2732` | only the CLIENT packet S_BEGIN_THROUGH_ARBITER_CONTRACT |
| `0x280E` | SDB_SEND_END_THROUGH_ARBITER_CONTRACT | `Arb_part_064.c:2983` | `0x280F`, but as a FAN-OUT, not a reply |

Request shape, both observed ones identical (guard `0x21 < len`, min frame 34):
`[6] Param ref` `[14] list ref` `[22] u32 ContractorDbId` `[26] u32 ContractType` `[30] u32 ContractId`.

`0x280E` is the one worth understanding: it walks the `AskUserList` and, for each other
participant that is in world, pushes `DBS_SEND_END_THROUGH_ARBITER_CONTRACT` to **that user's**
World session carrying `[ContractorDbId][ContractType][ContractId][thatUserDbId]`. Reproducing it
faithfully needs the contract/trade system we do not have, and inventing a reply would tell World
a contract completed that we never brokered.


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
