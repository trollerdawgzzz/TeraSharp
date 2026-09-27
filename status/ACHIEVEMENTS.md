# Per-character login loads (T22)

> **What this fixes.** Seven login replies were dob's captured bytes replayed to every character.
> Four of them are now rebuilt from that character's own rows and are byte-exact against both a
> brand-new character and one with progress. Two could **not** be pinned from the captures and are
> unchanged — see §6. One needs nothing.

**Ground truth**

| file | what |
|---|---|
| `D:\packetlogs\cap_newchar.log` | "Test" (playerId 2), FIRST login of a brand-new character |
| `D:\packetlogs\arb_world_2026-09-13T11-33-30-680Z.log` | dob (playerId 1) and "Test" logging in WITH progress |
| `data/cap_t22_newchar.bin` | the 15 frames of the first that the tests need (TSIS container) |
| `data/cap_t22_relog.bin` | the 10 frames of the second |

All offsets are **payload**-relative (frame − 6). Every layout below comes from the Arbiter's own
packet dumper — the function that prints a packet as named XML nodes — cross-checked byte for byte
against both captures. Nothing here was inferred from field order alone.

| opcode | fresh | with progress | state after T22 |
|---|---|---|---|
| `0x27F8` → `0x27F9` `DBS_LOAD_USER_ACHIEVEMENT` | 1499 B | 1547 B (dob) / 1627 B (Test) | **rebuilt** |
| `0x2872` → `0x2873` `DBS_LOAD_TUTORIAL_SIMPLE_TIP` | 19 B | 51 B | **rebuilt** |
| `0x2942` → `0x2943` `DBS_INIT_SEREN_GUIDE_INFO` | 23 B | 71 B | **rebuilt** |
| `0x2867` → `0x2868` `DBS_LOAD_DUNGEON_COOL_TIME` | 35 B | 87 B (Test) | **real handler, live id** |
| `0x288F` → `0x2890` `DBS_LOAD_REPUTATION_LIST` | 19 B | 71 B | **static — not pinnable, §6.1** |
| `0x2908` → `0x2909` `DBS_LOAD_FATIGABILITY_LIST` | 51 B | 51 B | **static — not pinnable, §6.2** |
| `0x27B9` → `0x27BA` `DBS_USER_LOAD_EP_PERK` | 119 B | 119 B | **static is already correct, §5** |

---

## 1. Achievements: `0x27F9`, and the 35 → 38 list map

Both messages are `[u32 offset][u32 length]` tables followed by the bodies. Offsets are
frame-relative and **every** slot carries the running body position, empty lists included.

```
SDB_UPDATE_USER_ACHIEVEMENT (0x27FA), World -> us, on every zone change and logout
  [0..279]   35 x [u32 offset][u32 length]
  [280]      u32 DlmId
  [284]      u32 UserDbId
  [288..]    bodies, in list order

DBS_LOAD_USER_ACHIEVEMENT (0x27F9), our reply to 0x27F8
  [0..303]   38 x [u32 offset][u32 length]
  [304]      u32 DlmId
  [308]      u8  Success
  [309..]    bodies, in list order
```

The writer confirms both: `FUN_1406eeae0` lays down 76 backpatch slots (builder words 3 through
0x4e), then writes DlmId (`FUN_14013d0b0`) and the Success byte (`FUN_1403513d0`), then stores the
running frame length into the first slot and the blob's byte count into the second before
`FUN_1403c98b0` appends list 0 as that many raw bytes — and `Handler_SDB_LOAD_USER_ACHIEVEMENT`
passes `0x4a0` = **1184** as that count.

**List 0 of both is `Data`**, a fixed 1184-byte blob. Every other list is a counter vector.

### The map

The two messages carry *different* list sets in *different* orders. The names make the mapping
provable:

| save | name | load |
|---|---|---|
| 0 | `Data` | 0 |
| 1..14 | `MonsterKillCountList` … `BattleFieldWinCountList` | 2..15 |
| 15 | `AbnormalityCounterList` | 17 (`AbnormalityCountList`) |
| 16..22 | `BattleFieldWinContinuouslyCountList` … `HealByItemSkillList` | 18..24 |
| **23** | `AcquireCombatItemTypeList` | **28** (`AcquireItemCombatTypeCountList`) |
| 24 | `SkillUserKillCountList` | 25 |
| 25 | `BattleFieldTopContribCountList` | 26 (`BattleFieldTopCoutribCountList`, the Arbiter's typo) |
| 26 | `RunWorkObjectCountList` | 27 |
| 27 | `MountSkillUseCountList` | 29 |
| **28** | `NpcContactList` | **36** (`NpcContractInfoList`) |
| 29 | `ContentUserKillList` | 31 |
| 30 | `ContentUserDeathList` | 32 |
| **31** | `CityWarRankList` | **30** |
| 32 | `LeaderBoardRankList` | 33 |
| 33 | `NoDieNpcKillList` | 35 |
| 34 | `BattleFieldWinCountByHeroList` | 37 |

Three load lists have no save counterpart: **1 `AchievementGrades`** and **16 `BattleFieldRankList`**
(empty in all five captured replies) and **34 `AccomplishedAchievementList`**, which `0x2802` feeds.

It is not a shift, and the exceptions are the proof: Test's save at relog seq 1137 has six non-empty
lists and they land on load seq 883's six non-empty lists **byte for byte** at exactly the indices
the names predict, including 23 → 28 and 24 → 25 crossing over each other in the body.

### What the lists actually say

Test's relog reply, decoded through the names:

```
 7 ItemLootCountList     6550:0, 6560:0        the two starter items
 8 ItemUseCountList      6550:1, 6560:1
10 SkillMonsterKillCount 13006:0
11 SkillUseCountList     13006:5               he used skill 13006 five times
25 SkillUserKillCount    13006:0
28 AcquireItemCombatType 6 u32s
34 Accomplished          5991 @2026-09-13 05:51:45, 5992 @05:52:46
```

### `AccomplishedAchievementList` and the first-write-wins rule

`SDB_ACCOMPLISH_USER_ACHIEVEMENT` (0x2802) adds 24-byte records
(`[u32 achievementId][u32 0][u16 year, month, day, hour, minute, second][u32 pad]`), and the reply
carries **only the records that were new**:

| capture | request | reply |
|---|---|---|
| cap_newchar seq 1369 | 5991 @05:51:45 | 43 B — the record comes back |
| cap_newchar seq 2605 | 5991 again, @05:52:41 | **19 B — empty** |
| cap_newchar seq 2722 | 5992 @05:52:46 | 43 B |
| relog seq 1157 / 1247 | 5991 + 5992 again, @11:46:01 | 19 B — empty |

and the relog's `AccomplishedAchievementList` still carries **05:51:45 and 05:52:46** — the first
timestamps, across two sessions. Hence `INSERT OR IGNORE` on `(owner, achievement)`, and the reply
is what the insert accepted. That also pins the 16-byte DateTime beyond doubt: 2026-09-13 05:51:45
in a capture that starts at 05:49:03.

### The brand-new-character `Data` blob

1184 bytes, all zero except two u16s — `Data+1052 = 64228` and `Data+1180 = 622`. Built in code
(`FreshAchievementData`) rather than carried as a data file.

### The one deviation

`Data` is served exactly as World last sent it. The real Arbiter fills in **`Data+1052` and
`Data+1180`** on the way out — World sends both as zero and the Arbiter's replies always carry the
same value in both (433 for dob and Test, 622/64228 for a fresh character). They look like
achievement-progress counts the Arbiter recomputes from its own template data. Nothing in any
capture shows World reading them back, and we cannot compute them, so a relog sees whatever World
last stored there. Everything else round-trips verbatim.

## 2. Tutorial tips: `0x2873` — fully pinned

```
0x286E SDB_ADD_TUTORIAL_SIMPLE_TIP   [0] u32 DlmId  [4] u32 UserDBID  [8] u32 TipId
0x2873 DBS_LOAD_TUTORIAL_SIMPLE_TIP  [0] u32 listOff=19  [4] u32 listLen
                                     [8] u32 DlmId  [12] u8 Success=1
                                     then 8 B per tip: [u32 tipId][u32 1]
```

cap_newchar adds tips **1, 2, 35, 39** (seq 719/765/864/911) and the relog capture serves exactly
those four, in that order. The second word of each record is 1 in every record of every capture;
its meaning is unknown and it is written as a literal 1.

## 3. Seren guide: `0x2943` — pinned, with one observed constant

```
0x2944 SDB_UPDATE_SEREN_GUIDE_INFO  [0] DlmId [4] UserDbId [8] SerenType [12] SerenId
0x2943 DBS_INIT_SEREN_GUIDE_INFO    [0] u32 listOff=23 [4] u32 listLen
                                    [8] u32 DlmId [12] u32 UserDbId [16] u8 Success=1
                                    then 8 B per row: [u32 serenType][u32 serenId]
```

A character with nothing stored gets an **empty list** (cap_newchar seq 380). Any stored slot brings
out a fixed six-row table, types **4, 13, 14, 15, 16, 17**, each with its stored id or 0. Test wrote
(type 2, id 1804) and (type 4, id 36); his relog reply carries the six rows with type 4 = 36 and
**type 2 is not served at all**.

The six types are **observed, not derived** — both characters in the relog capture get exactly
these and no other type has ever appeared in a reply. If a seventh ever shows up, this table is
where it goes.

## 4. Dungeon history: `0x2868`

Layout was already decoded in T21 (`status/ENTER-WORLD-FALLBACK.md` §4). The empty three-list form
is byte-exact for both captures and `Build2868_ThreeEmptyLists` already produced it; T22 only moves
`0x2867` onto the handler allow-list so the reply carries the **live** DLM id instead of the
replay table's captured one. Serving real cool-time records needs a `dungeon_cooldowns` table; the
record layout is known, the counters are not.

## 5. EP perks: `0x27BA` needs nothing

All three captured replies — fresh, dob, Test — are **byte-identical apart from the DlmId at [8]**.
The replay path already patches that. There is nothing per-character in this message; leave it.

## 6. What could NOT be pinned — SUPERSEDED by T26

This section said reputation `0x2890` and fatigability `0x2909` could not be pinned. That was
true of the captures and false of the binary: **both are pinned and both are now rebuilt from
rows** — `status/REPUTATION-FATIGABILITY.md`. Two claims made here were wrong and are worth
naming, because both were mis-reads rather than missing evidence:

* "record+0 is 1 for both dob and Test, so it is not the owner." A byte-level diff of the two
  captured replies shows they differ at **exactly one offset, +0**: dob 1, Test 2. It is the
  OwnerDbId, stamped by the Arbiter's DB loader. record+32 is an unbound row-buffer slot —
  residue, not a field.
* "There is no write opcode for fatigability in either capture." There are five: `0x2910`, which
  the C# had under the wrong name (`SDB_LOAD_FRIEND_INFO`). It carries a **delta**, and the total
  belongs to the **account**, which is why the three replies looked contradictory.

The lesson for the next stuck layout: when the captures run out, the Arbiter's own loader and the
packet dumpers still have the answer.

## 7. What TeraSharp does now

`Persistence/CharacterStore.cs` — four new tables and their accessors:

* `achievements(owner_id, payload)` — the raw `0x27FA` payload, last write wins.
  `SaveAchievements` / `GetAchievements`.
* `achievements_done(owner_id, achievement_id, record)` — `INSERT OR IGNORE`, so the first record
  for an achievement is the one kept. `AddAccomplishedAchievements` returns what it accepted, which
  is exactly the `0x2803` reply list. `GetAccomplishedAchievements` returns them in earned order.
* `tutorial_tips(owner_id, tip_id)` — `AddTutorialTip` / `GetTutorialTips`.
* `seren_guide(owner_id, seren_type, seren_id)` — `SetSerenGuide` / `GetSerenGuide`.

`World/DbProxyHandlers.cs`

* `OnSaveUserAchievement` (0x27FA) stores then acks; `OnLoadUserAchievement` (0x27F8) rebuilds.
* `OnAccomplishUserAchievement` (0x2802) now offers every record to the store and replies with
  what was accepted — the 19-byte empty form now happens for the right reason.
* `OnAddTutorialTip` / `OnLoadTutorialTips`, `OnUpdateSerenGuide` / `OnLoadSerenGuide`.
* Builders with byte-exact tests: `BuildDbs27F9`, `BuildDbs2873`, `BuildDbs2943`,
  `SplitOffsetLengthLists`, `SliceAchievementRecords`, `FreshAchievementData`.
* `0x27F8`, `0x2872`, `0x2942` and `0x2867` moved onto the `IsHandledRequest` allow-list. They were
  dispatch cases with no allow-list entry, i.e. dead code, and the replay table was answering them.

**playerId 1 (dob) keeps the captured statics** for all four, exactly as he keeps the captured
quest list: his rows do not exist in our DB and a rebuilt reply would silently drop everything he
has. `DbProxyHandlers.ServesCapturedStatics` is the one place that decides this.

## 8. T203 — server-first achievements (research)

The marker is **`serverUnique`**, a string attribute on `<Achievement>` in **`AchievementList.xml`**
(`Executable/ServerConfig.xml:68` lists the sheet; both binaries decode it to an int —
`WorldServer.exe.c:364358`, `Arb_part_006.c:7670`). 0 = normal, 1 = server-unique, 2 and 3 =
server-unique **and** party-shared. The sheet itself is not on this machine, and **we do not need
it**: World copies the decoded value into the **second u32 of each 24-byte `0x2802` record** — the
field section 1 above calls `[u32 0]`.

### What the real Arbiter does

`Handler_SDB_ACCOMPLISH_USER_ACHIEVEMENT` = `FUN_1407375c0` (`Arb_part_062.c:18927`); the per-record
gate is `User::AccomplishAchievement` = `FUN_140366070` (`Arb_part_027.c:16554`):

1. per-character dedupe (`User+0x6010`) — already held, drop it;
2. `serverUnique == 0` -> grant, **no shared check at all**;
3. otherwise lock `GServerAchievementManager` and call `ServerAchievementManager::IsAccopmlishedNoLock`
   (the binary's own spelling, `Arb_part_057.c:6374`);
4. unclaimed -> `SetAccomplishedNoLock` (`Arb_part_057.c:16258`) -> `dbo.spInsertServerAchievement`;
5. claimed and kind 2/3 -> `CanAccomplishPartyAchievementNoLock` (`Arb_part_056.c:16992`) grants it
   to the **same party** that first claimed it;
6. otherwise refuse.

**There is no refusal reply.** `0x2803`'s `success` byte is a hard-coded 1 (`Arb_part_062.c:19023`,
and all 50 replies in `cap_final2b` agree): the refusal *is* the record's absence from the returned
list, which is the same 19-byte empty frame section 1 already documents. World's
`AchievementManager::SetAccomplishedAchievementList` (`WorldServer.exe.c:2195421`) walks only the
records it received — no else-branch — so nothing reaches the client either.

The shared table is retail's `ServerAchievement` (`GameDatabaseDefinition.xml:1829`, desc
"서버최초 업적리스트"), columns `achievementId`, `userDbId`, planet-wide with no world scoping;
SPs `spLoadServerAchievement` / `spInsertServerAchievement` / `spClearServerAchievement`.

### The pin, and one opcode we are missing

`cap_final2b` **3622 -> 3623** (dlm 491, userDbId 1003): 12 ids requested, the 7 with
`serverUnique == 0` granted and **exactly the 5 with `serverUnique == 1`** — 730, 731, 732, 737,
2103 — refused, all five already held by userDbId 1 in the `0x2801` snapshot. `cap_newchar.log`
pins the empty-table case.

**`0x2801 DBS_UPDATE_SERVER_ACHIEVEMENT`** is a one-way A->W push of the whole table
(`[u32 off][u32 bytes]` + N x `[u32 achievementId][u32 userDbId]`). The real Arbiter sends it once
per World link on connect (`cap_final2b`: 75 links, 75 pushes, 54 bytes each) and again after a
grant. We only silence it (`WorldBridge.cs:665`); World has a real handler
(`WorldServer.exe.c:3026332`).

### What T203 implements

`OnAccomplishUserAchievement` deduped per character only and never read the record's second u32.
Now:

* **`server_achievements(achievement_id PRIMARY KEY, owner_id, party_id)`** - planet-wide, no
  foreign key, because retail's table is keyed on the achievement and deleting a character does not
  release a server first. `TryClaimServerAchievement` is `INSERT OR IGNORE` plus the row count,
  which IS the race-free first-claimant-wins rule; `GetServerAchievementClaim`,
  `GetServerAchievements` and `ClearServerAchievements` round it out.
* **The gate**, in the real Arbiter's order: `serverUnique == 0` grants with no shared check;
  otherwise claim it if nobody holds it, pass it through when the holder is this character (the
  per-character table then refuses the repeat by itself), and for kinds 2/3 let the party that first
  claimed it through. Everyone else is dropped from the offered list, which IS the refusal.
* **Admin API**: `GET /api/server-achievements` lists every claim with its holder's name, and
  `POST /api/clear-server-achievement` with an id or all releases one or all - retail's
  `ClearServerAchievement` / `ClearAllServerAchievement`.

Tests (`T203.cs`, fixture `data/cap_t203.bin`): the capture's split is exactly `serverUnique` in
both directions and our reply to `cap_final2b` 3622 is **byte-identical to 3623** once the five ids
are held elsewhere; the second claimant is refused while the ordinary achievement still lands; and a
claim survives a store reopen, shows in the admin list, and is winnable again after a clear.

**Left out on purpose.** `0x2801 DBS_UPDATE_SERVER_ACHIEVEMENT` is a one-way A->W push of the whole
table (`[u32 off][u32 bytes]` + N x `[u32 achievementId][u32 userDbId]`) that the real Arbiter sends
once per World link on connect (cap_final2b: 75 links, 75 pushes, 54 bytes each) and again after a
grant; World has a real handler (`WorldServer.exe.c:3026332`) and we only silence it
(`WorldBridge.cs:665`). Sending it is a DBS_* World never asked for, which CLAUDE.md forbids without
a task that says so, and nothing in the refusal path needs it - the Arbiter is the only authority on
the claim. It is the next step if World ever has to know the table.

**Not verified:** the three literal `serverUnique` spellings (the sheet is absent from this machine;
the decompile only gives lengths 5/6/5); kinds 2 and 3 appear in no capture, so the party branch is
decompile-only; `Party+0x79`, the flag that makes kind 2 refuse outright, is unnamed. Retail's
claimant list is multi-valued for party grants, so `achievement_id` as the sole key diverges if true
party sharing is ever implemented.
