# Quest persistence — design (T17)

> ## THE CAPTURE ARRIVED — LIST 2 IS THE COMPLETED IDS
>
> `<captures>\arb_world_2026-09-13T11-33-30-680Z.log` **seq 881** is the "Test" login this
> file was asking for: three completed quests (59901, 59902, 59903) and one in progress (59904).
> The answers:
>
> - **list 0** is active-only — one 80-byte record, quest 59904;
> - **list 1** is empty even for a character with completed quests, so it is not the completed
>   list;
> - **list 2** is `vector<int>` of the **completed quest ids**, 12 bytes for those three;
> - **list 5** is *not* a constant: it carries 2026-09-12 07:00 in both `0x272D` replies of that
>   capture, the same daily-reset stamp the `0x148D` cool-time records carry, so it is
>   server-wide and not per character.
>
> Served since T21. Still unknown: what record fields **`[24]`** and **`[64]`** mean, and what
> list 1 and list 4 are ever used for.

**Status (updated after T21): both halves are implemented, completed quests included.**
`0x272C -> 0x272D` is rebuilt from stored rows with the completed ids in list 2, and
`0x272E -> 0x272F` is a real write handler; see §8. Two things in the original T17 brief did not
survive contact with the captures — see §7.

Sources, in the order they win:

- `D:\v100\TERA_SERVER.100\ArbiterServer.exe.c` — `Handler_SDB_SET_QUEST_INFO` (tracer line
  1283916), `Handler_SDB_LOAD_QUEST_LIST` (1275742), and the `0x272D` writer `FUN_1406edac0`
  (1206774, opcode at 1206795).
- `<captures>\cap_newchar.log` — real ArbiterServer, brand-new character "Test" playerId 2.
  One login, 28 × `0x272E`, one `0x272C`.
- `<captures>\lobby_tap.log` — "dob", a played character: login + relog, so two `0x272D`
  replies, one of which actually has content.

All offsets are **payload**-relative (frame offset − 6) unless a line says "frame". Everything
below was checked with Python against the reframed captures; nothing was built.

---

## 1. `SDB_LOAD_QUEST_LIST` 0x272C → `DBS_LOAD_QUEST_LIST` 0x272D

Request, min frame 0x0E:

```
[0] u32 reqId      [4] u32 playerId          (8-byte payload)
```

The reply writer lays down **twelve** u32 backpatch slots, then the ok byte, then the id — so a
**53-byte header** carrying six `[offset][length]` pairs:

```
[0]  off/len  list 0   vector<QuestData>                 80 B per record
[8]  off/len  list 1   vector<QuestData>                 80 B per record
[16] off/len  list 2   vector<int>                        4 B per record
[24] off/len  list 3   vector<DailyQuestSeed>            68 B per record
[32] off/len  list 4   vector<DailyQuestExCompleteCount>
[40] off/len  list 5   a fixed 20-byte blob
[48] u8  ok
[49] u32 reqId                    <- the "reqId at payload 49" in the brief, confirmed
[53] bodies, in slot order
```

Offsets are frame-relative, so an empty reply has every offset at 59 (= 6 + 53).

Observed replies:

| capture | frame | list 0 | list 3 | list 5 | what it is |
|---|---|---|---|---|---|
| cap_newchar seq 342 | 79 | 0 | 0 | 20 B | **the brand-new character** |
| lobby_tap seq 167 | 159 | 1 rec | 0 | 20 B | dob, first login of the World process |
| lobby_tap seq 950 | 1383 | 1 rec | 18 recs | 20 B | dob, relog |
| arb_world 2026-09-13 seq 395 | 159 | 1 rec | 0 | 20 B | dob again |
| arb_world 2026-09-13 seq 881 | **171** | 1 rec | 0 | 20 B | **Test: list 2 = 3 ints** |

Lists 1 and 4 are empty in all five, list 2 in all but seq 881. **List 3 is global, not per-character** — the handler
fills it from `GlobalDailyQuestSeed::GetGlobalDailyQuestSeed`, which is why it is empty on the
first login of a World process and 18 records on the relog. Sending it empty is what makes World
generate the seeds itself and send the 17 `0x2899` items we already answer
(`status/STATUS.md` §1), so **empty is the behaviour we want**, not a gap.

The 20-byte list-5 blob is `[u32 ?][DateTime 16 B]`, the DateTime in the Arbiter's packed
`{u16 year, month, day, hour, minute, second, …}` form:

```
new character   00 00 00 00 | B2 07 01 00 01 00 00 00 | 00*8     -> 1970-01-01, i.e. never
dob relog       00 00 7F 90 | EA 07 09 00 0C 00 07 00 | 00*8     -> 2026-09-12 07:00
seq 395 / 881   00 00 00 00 | EA 07 09 00 0C 00 07 00 | 00*8     -> 2026-09-12 07:00
```

The same `2026-09-12 07:00` appears in the `0x148D` `DungeonCoolTimeElem` records of that capture
(`status/ENTER-WORLD-FALLBACK.md` §4), so it is a **server-wide daily-reset stamp**, not per
character. `BuildDbs272D` defaults to the "never" form and takes an override so seq 881 can be
reproduced byte for byte; nothing we have lets us compute the real value, and World accepted
"never" for a character that had not played.

### List 2: completed quest ids

`arb_world_2026-09-13T11-33-30-680Z.log` seq 881, "Test" (playerId 2), 171-byte frame:

```
payload  [0]  59 / 80     list 0   one 80-byte QuestData: dbid 5, quest 59904, status 1, step 1
         [8]  139 / 0     list 1   empty
         [16] 139 / 12    list 2   FD E9 00 00  FE E9 00 00  FF E9 00 00   = 59901, 59902, 59903
         [24] 151 / 0     list 3   empty
         [32] 151 / 0     list 4   empty
         [40] 151 / 20    list 5   00 00 00 00 | 2026-09-12 07:00
         [48] ok = 1      [49] reqId = 0x63
```

Plain little-endian u32s in ascending quest id, which is also insertion order. Completed quests do
**not** also appear in list 0.
## 2. `SDB_SET_QUEST_INFO` 0x272E → `DBS_SET_QUEST_INFO` 0x272F

Request, min frame 0x24 (36):

```
[0]  u32 questOffset   (frame-relative; 36 in every captured frame)
[4]  u32 questLength   (80 in every captured frame)
[8]  u32 atomOffset    [12] u32 atomLength     ItemTransactionAtom list, 856 B each
[16] u32 reqId
[20] u32 op            22 / 23 / 24 — see below
[24] u32 playerId
[28] u8  [29] u8
```

so a 30-byte payload header, then the 80-byte quest record, then the reward atoms.

### The three sizes are not three record types

They are one 80-byte record plus a variable number of the **same 856-byte
`ItemTransactionAtom`** the inventory path uses (`status/INVENTORY-DESIGN.md` §3) — the quest's
reward items, handed to `TransSQLExec::ExecTrans` by the same code:

```
 116 = 36 + 80 + 0 × 856     22 frames    no reward
 972 = 36 + 80 + 1 × 856      5 frames    one reward item
3540 = 36 + 80 + 4 × 856      1 frame     four reward items
```

That also means a quest write can allocate item ids exactly like `0x2768` does (T13), and the
reply echoes the atoms back the same way — `PKT_DBS_SET_QUEST_INFO_WRITE` is templated on
`vector<ItemTransactionAtom const*>` and its `SendToSession` argument list is
`int, long, bool&, int, const unsigned char*, int&, vector<Atom>*`, i.e. reqId, op, ok, and the
80-byte record echoed back as a pointer plus the length 0x50.

### The 80-byte QuestData record

Column-diffed across all 28 captured records; only seven u32 slots are ever non-zero.

| off | field | evidence |
|---|---|---|
| 0 | **journal slot** | 0 on the first write of each quest, then a stable per-quest number (2, 3, 4) |
| 4 | **questId** | 59901, 59902, 59903, 59904 |
| 8 | **status** | 1 = in progress, 2 = complete (2 only ever appears on the last write of a quest) |
| 12 | **step** | 1, 2, 3 … 10, reset to 0 when status becomes 2 |
| 24 | u32 0/1 | a sub-flag; moves in lockstep with op 24 |
| 64 | u32 | large and unstable, frequently 0 — reads like the uninitialised tail in the item record |
| 76 | u32 | `0xFFFFFFFF` in every record |

### The `op` field at payload 20

| op | meaning | record shape |
|---|---|---|
| 22 | quest accepted | slot 0, status 1, step 1 |
| 23 | step advanced | slot set, step incremented, or status → 2 |
| 24 | sub-flag write | same slot/step, `[24]` flips to 1 |

## 3. Every `0x272E` in the capture

28 writes, four quests, in order:

```
seq   time          op  slot  quest  status  step  [24]   size
 540  05:51:08.861  22    0   59901    1       1     0     116
1014  05:51:28.964  23    2   59901    1       2     0     116
1185  05:51:38.734  24    2   59901    1       2     1     116
1187  05:51:38.734  23    2   59901    1       3     0     116
1191  05:51:38.744  24    2   59901    1       3     1     116
1303  05:51:44.930  23    2   59901    2       0     0     972   <- complete, 1 reward
1406  05:51:46.050  22    0   59902    1       1     0     116
1508  05:51:49.651  23    3   59902    1       2     0     116
1588  05:51:52.446  23    3   59902    1       3     0     116
1695  05:51:58.361  24    3   59902    1       3     1     116
1697  05:51:58.362  23    3   59902    1       4     0     116
1804  05:52:03.903  23    3   59902    1       5     0     972   <- step reward
1975  05:52:12.195  23    3   59902    1       6     0     972   <- step reward
2085  05:52:17.251  24    3   59902    1       6     1     116
2088  05:52:17.255  23    3   59902    1       7     0     116
2231  05:52:25.026  24    3   59902    1       7     1     116
2233  05:52:25.026  23    3   59902    1       8     0     116
2238  05:52:25.035  24    3   59902    1       8     1     116
2364  05:52:30.713  23    3   59902    1       9     0    3540   <- 4 rewards
2453  05:52:35.118  24    3   59902    1       9     1     116
2455  05:52:35.119  23    3   59902    1      10     0     116
2660  05:52:46.167  23    3   59902    2       0     0     972   <- complete
2796  05:52:51.485  22    0   59903    1       1     0     116
2829  05:52:52.474  23    4   59903    1       2     0     116
2997  05:52:58.299  24    4   59903    1       2     1     116
2999  05:52:58.299  23    4   59903    1       3     0     116
4009  05:53:25.008  23    4   59903    2       0     0     972   <- complete
4061  05:53:26.142  22    0   59904    1       1     0     116
```

**Last state per quest — what the next login must reproduce:**

| quest | slot | status | step | |
|---|---|---|---|---|
| 59901 | 2 | 2 | 0 | complete |
| 59902 | 3 | 2 | 0 | complete |
| 59903 | 4 | 2 | 0 | complete |
| 59904 | 0 | 1 | 1 | in progress, accepted 12 s before logout |

Keying on `(playerId, questId)` and keeping the last write is therefore the right storage model:
every later write for a quest supersedes the earlier ones, and no two quests share a journal slot
while active.

## 4. How the load reply is assembled

`Handler_SDB_LOAD_QUEST_LIST` calls one function to fill the first three lists —
`FUN_140715cf0(questDataManager, &list0, &list1, &list2)` — then appends the global daily seeds,
then writes everything out. So lists 0-2 are the per-character quest state and are the only part
we have to produce from storage.

```
payload  = 53-byte header
list 0   = active quest records, 80 B each
list 1   = (empty in every capture)
list 2   = (empty in every capture)
list 3   = empty  -> World generates the seeds and sends 17 x 0x2899, which we already answer
list 4   = empty
list 5   = the 20-byte trailer, "never" form for a character that has not played
```

With no stored rows this is exactly the 73-byte payload of cap_newchar seq 342.

## 5. Proposed storage

```sql
CREATE TABLE IF NOT EXISTS quests (
  owner_id  INTEGER NOT NULL REFERENCES characters(id),
  quest_id  INTEGER NOT NULL,          -- record+4
  slot      INTEGER NOT NULL,          -- record+0
  status    INTEGER NOT NULL,          -- record+8   1 = active, 2 = complete
  step      INTEGER NOT NULL,          -- record+12
  record    BLOB    NOT NULL,          -- the raw 80 bytes, last write wins
  updated_at TEXT   NOT NULL DEFAULT (datetime('now')),
  PRIMARY KEY (owner_id, quest_id)
);
```

The raw record is stored for the same reason the item record is (§2 of
`status/INVENTORY-DESIGN.md`): of 80 bytes we can name five fields, `[64]` looks like heap, and
serving World a record we synthesised field-by-field is the move that desynced it last time. The
parsed columns are there so the load can filter without decoding blobs, and so the state is
greppable in the DB.

`OnSetQuestInfo` upserts on `(owner_id, quest_id)`; `OnLoadQuestList` selects the rows that
belong in list 0 and concatenates their `record` blobs.

## 6. What is NOT resolvable from the captures

**Settled by T21** (`arb_world_2026-09-13T11-33-30-680Z.log` seq 881): list 0 is active-only and
list 2 carries the completed quest ids as plain u32s. `RawQuestHelper::InsertRawCompletedQuestData`
and `QuestDataManager::RefreshCacheCompletedQuestData` were the right smell after all.

**Still unresolved:**

- **What list 1 and list 4 are for.** Both are empty in all five captured replies, including the
  one from a character with three completed quests. `vector<QuestData>` and
  `vector<DailyQuestExCompleteCount>` are the template argument names; nothing has ever filled
  them.
- **Record fields `[24]` and `[64]`.**
- **The list-5 DateTime.** Server-wide, and we cannot compute it. We send "never".

## 7. Two corrections to the T17 brief

1. **The new-character `0x272D` is 79 bytes on the wire (73-byte payload), not 1377.** The
   1377-byte payload is `lobby_tap.log` seq 950 — **dob's relog**, of which 1224 bytes are the 18
   **global** daily-quest seeds and 80 bytes are dob's own active quest. It is not a
   new-character reply and it is not per-character data. The byte-exact regression target for
   "a character with no rows" is cap_newchar seq 342:

   ```
   3B 00 00 00 00 00 00 00  3B 00 00 00 00 00 00 00  3B 00 00 00 00 00 00 00
   3B 00 00 00 00 00 00 00  3B 00 00 00 00 00 00 00  3B 00 00 00 14 00 00 00
   01 12 00 00 00 00 00 00  00 B2 07 01 00 01 00 00  00 00 00 00 00 00 00 00
   00
   ```

   Note what this means for today's behaviour: `DbProxyStaticData.QuestListEmpty` is that 1377-byte
   relog reply, so every character currently gets **dob's quest 59901 at step 1** plus 18 stale
   daily seeds. Switching to a rebuilt reply fixes that as a side effect.

2. **There is no `OnSetQuestInfo` to extend.** `0x272E` appears nowhere in
   `World/DbProxyHandlers.cs` on either `master` or the worktree — T15 is not in the tree. T17's
   write half therefore has to build the `0x272E → 0x272F` handler as well, including the atom
   echo and reward-item id allocation described in §2, which is the same shape as `BuildDbs2769`
   from T13 and should reuse it.

---

## 8. What T17 implemented

`Persistence/CharacterStore.cs`

- a `quests` table keyed `UNIQUE (owner_id, quest_id)` with an `INTEGER PRIMARY KEY id`. That row
  id **is** the `questDbId`: it is allocated on the first write for a quest, never changes, and
  survives a restart — a process counter would hand World a different id for the same quest after
  a bounce.
- `UpsertQuest` (last write wins, returns the id), `GetActiveQuestRecords`, `GetCompletedQuestIds`,
  `CountQuests`.

`World/DbProxyHandlers.cs`

- `OnSetQuestInfo` / `BuildDbs272F` — the 29-byte reply header, `questDbId` at [25] on sqlType 22
  and 0 otherwise, reward atoms echoed through `CloneAtomList` with T13's op-7 allocation rule.
  A record that cannot be sliced is still acked (an unanswered per-user DB item head-blocks the
  queue) but nothing is stored.
- `OnLoadQuestList` / `BuildDbs272D` — list 0 from the stored active rows, **list 2 the completed
  quest ids** (T21), lists 1/3/4 empty (list 3 deliberately, so World seeds itself), list 5 the
  "never" trailer unless the caller overrides it. dob (playerId 1) keeps the captured 1377-byte
  reply until he has rows of his own.
- The "STORED BUT NOT SERVED" warning is gone: completed quests are served.

Verified in Python against both captures, not built: all 28 `0x272F` replies rebuild byte for byte
(given the capture's questDbIds 2-5); `BuildDbs272D` with no rows reproduces cap_newchar seq 342's
73-byte payload exactly; and with the capture's one active record, the three completed ids and its
trailer it reproduces arb_world seq 881's 165-byte payload exactly.

## T158 - cap_play1: what stopped test and caludesucks

All pins are cap_play1.log (A<->W) frame numbers.

| Symptom | Cause | Verdict |
|---|---|---|
| test: gathering gives nothing, mailbox empty | 13074 SA_CREST_USE (glyph applied, DLM 0x4C5) never answered. After it World sent no DB frame for player 10: the ten PICKENDs (result 3, same node template 402 and quest step as caludesucks) had no SDB_ITEM_SINGLE, five C_LIST_PARCEL had no SDB_LIST_PARCEL, the 05:42 save never came | bug - AS_CREST_USE `[DLM][1]` |
| caludesucks: quest 59911 stuck at step 5, pegasus refused | 31914 SDB_PEGASUS_FEE (one 568-byte TS_CHANGE_MONEY of -1000, the fee 0x538e lists) never answered; 31996 / 32127 retries sent nothing | bug - fee charged, DBS_PEGASUS_FEE `[DLM][1]` |
| Village Atlas / `teleport 7005` "@3301" | World's User::CheckTeleportClassException: while the class task (questId/task in the datasheet) is incomplete, any other continent is refused. No 0x13BE is sent, so the Arbiter is not asked | expected; clears when the chain is done |
| every spawn | replayed DBS_END_START_QUEST_LIST with captured DLM 50 after SA_USER_ON_SPAWN_COMPLETE (694 ... 30805) | bug - 0x15AE sealed |
| C_SET_VISIBLE_RANGE | forwarded; World has no handler | accept-silently |

Checked and fine: the three 0x13BE -> 0x13BF / 0x13C0 -> 0x13C1 instance entries (18469, 20973, 29259) stay on link 1 via the T108 path; world 0's 0x164D roster (82, 173 continents) is applied under world 0 and has no 7005. SDB_SET_QUESTLIST_INFO / 0x2733 and SDB_SET_QUEST_INFO all answered. caludesucks' mailbox lists both system parcels correctly; player-to-player rows still come from the stored MAKE record (T151 touched only the synthesized branch). Not fixed: system-mail attachments (SA_MAKE_SYS_PARCEL carries 202089 + 202238) are not delivered on receive - no captured DBS_RECV_PARCEL_EX with atoms, so op 37 is still unmodelled.

## T160 - level-70 start from data (tools/level70-start.ps1)

**Gate.** `User::Teleport` -> `CheckTeleportClassException` = `ClassExceptionDataSheet::GetClassExceptionData(class, level)`; a row with `TeleportRestriction@continentId` refuses (@3301, 0xCE5) any other continent until `questId`/`taskId` is done. This server's `Datasheet\ClassException.xml` has exactly two such rows, both Reaper (`soulless`, levels 1-57 and 58-70): `continentId="7087" questId="8708" taskId="3"`. The script comments both out. No other class has one: test and caludesucks are class 12 (`glaiver`, blob+200), so their @3301 in cap_play1 is not this sheet. The only other @3301 in `User::Teleport` is `RestrictionProcess<TeleportRestrictionAgent>` (agent mode defaults to -1 = off, per-user flag User+42000 from `AddHuddleAddingPackage`). No sheet sets that mode - see T162 for `RestrictionOpenData`.

**Scroll.** `207631` "70等級跳躍卷軸": `PERFECT_LEVEL_JUMPING_UP`, `combatItemArg1="70"` (target level), usable at levels 1-64, not tradable or destroyable. Same target, levels 1-69: `207301`, `207472`-`207474` (`LEVEL_JUMPING_UP`, but `periodInMinute="1" periodByWebAdmin="True"`, so they expire unless web-admin grants them). Lower: `98836` (65), `206532`-`206539` (26-68), `209170` (68). World side: using one builds `DBIncrementCharacterLevelJump` / `...PerfectJump(user, itemId)`, which deletes the item and sends `SDB_INCREMENT_CHARACTER_LEVEL_JUMP` 0x28DB / `..._PERFECT_JUMP` 0x28DD. The level (item template +0x314 = combatItemArg1) is committed only on the DBS reply (0x28DC / 0x28DE). **TeraSharp answers neither**, so the scroll wedges that character's DB queue, the same failure as T158's SA_CREST_USE. (Answered since T162.)

**Handing it out.**

| Route | Sheet | Works on TeraSharp? |
|---|---|---|
| Starter bag | `CreateCharData.xml` `<Char><InitItem itemTemplateId="207631" initWear="false" amount="1"/>` (`-StarterScroll`) | yes since T159 (items) - it reads the sheet, `StarterInventory.cs` is the fallback |
| Start at 70 outright | `CreateCharData.xml` `<Char createdLevel="70">` (soulless already uses 50) | yes since T162 |
| UserAdditionalItem / PCBangItem | not loaded by this WorldServer (no such names in it) | - |
| World-side grants (quest reward, NPC buy list) | QuestData / BuyList | the item arrives via SDB_SET_QUEST_INFO / SDB_ITEM_SINGLE atoms, which TeraSharp applies; not traced further |

**Client DC.** The gate is enforced by World alone: no repack. `207631` must exist in the client's ItemData + StrSheet_Item (it is in this server's 100.02 sheets, zh-TW strings): check the DC; repack those two rows only if they are missing. `CreateCharData` is not in the client DC.

## T162 - data-driven start, the level-jump answers, RestrictionOpen

**createdLevel.** `DatasheetLoader.CreatedLevels` reads `<Char createdLevel>` per class the way the Arbiter does (ArbiterServer.exe.c:141515: default 1, outside 1..127 -> 1); `StarterInventory.BuiltInCreatedLevels` (all 1, soulless 50) is the fallback. `CharacterHandlers.BuildRecord` makes the row at that level (the blob gets it on the way out, T105); above 1 it is queued for `WorldLevelSync`, so World runs its own level commit - skills - at the first spawn. `<InitItem>` rows were already read (T159); the scroll lands in the bag behind the potions.

**0x28D9 / 0x28DB / 0x28DD.** All three have one layout: request `[ref @6 -> 856-byte atoms][DlmId @0E][UserDbId @12]` (World FUN_1405f3b00 / 3db0), reply `[DlmId @6][ok @0A]`. The real Arbiter runs the atoms (the used-up item) and never touches the level. World levels the character itself after the reply: `DBIncrementCharacterLevelPerfectJump::ExecuteCommitSQL` starts a `DBLevelExpContext` to item template +0x314 (combatItemArg1), which arrives as S_UPDATE_EXP_LEVEL 0x273B - already persisted. So no Arbiter-side level sync for the scroll (it would level twice). DbAckTable rows with the new `a6` token; the only rows without a live pair.

**RestrictionOpenData = `Datasheet\RestrictionOpen.xml`** (top level; ServerConfig.xml `<RestrictionOpenData fileName="RestrictionOpen.xml"/>` in both server sections). Eight `<RestrictionOpen>` blocks:

| Block | Applies when | Content |
|---|---|---|
| `level="1"` (CBT: continent A only) | WorldServerConfig `restrictionOpenLevel` == 1 | PegasusException 21,29,30,93,121,132,130,113,103,27; BuyList 141,142,151,159; HuntingZone 61,361,4,204,304,8,208,308,10,210,310; Territory 212/21200007; Collection 7004 |
| `pve="true"` | PvE server | GuardPolicy 1; BuyList 352,150005,219000; Quest 7289,7299,6389 |
| `server="dungeon"` / `"battlefield"` | that server type | Quest 7289,7299,6389 |
| `publisher="FOG"` / `"EME"` / `"RUS"` | that publisher | Npc (T-Cat, summer event, Pandora), Quest 6382,4180,6378,477,6377, BuyList |
| `level="0"` | always | Quest 6378 |

`IsAppliableRestriction` (WorldServer.exe.c:187788): level 0 = always, else equal to `restrictionOpenLevel` (config +0x44 = DAT_141dd1504). This server has `restrictionOpenLevel="0"`, so the CBT row - the only one that closes travel (pegasus routes) - is off. **No row gates teleport**, so it is not the Valkyrie @3301 either; `level70-start.ps1` now pins `restrictionOpenLevel="0"` (0 edits here). The @3301 left is `TeleportRestrictionAgent` (mode -1 = off unless set at runtime); re-check with a capture on T158+ code, where the pegasus fee no longer wedges.

## T164 - the two requests that wedged cap_scroll

| Request | cap_scroll | Layout (frame offsets) | Answer |
|---|---|---|---|
| 0x28AE SDB_MARK_AS_QUEST_COMPLETED, 22 B | 18110, player 11, after the jump's level/skill burst | `[ref @6 -> i32 quest ids][DlmId @0E][UserDbId @12]` | 0x28AF `[ref @6 -> ids newly complete][DlmId @0E]`; each id stored complete (`CharacterStore.MarkQuestCompleted`, served in 0x272D list 2) |
| 0x2790 SDB_USER_LEARN_SKILL_FOR_MULTIPLE, 1760 B | 14359, caludesucks, skill 111110, item 1100 (template 70) used up | `[ref @6 atoms][ref @0E {skillId, flag}][DlmId @16][UserDbId @1A][skillId @1E][u8 @22][u32 @23][u8 @27][i32 @28][i32 @2C]` | 0x2791 `[ref atoms][ref periods][ref {skillId, result}][DlmId @1E][ok][hasPeriods][alreadyLearned]`; atoms applied |

**The scroll's quest list is empty.** World builds 0x28AE from a list of flagged entries after the level commit (`DBIncrementCharacterLevelPerfectJump::ExecuteCommitSQL`, WorldServer.exe.c:1130501); on this server it came out empty, so the request completes nothing and is not the missing teleport gate. World calls the reply a success only when its list is non-empty, so this one ends in OnFail - as it would on the real Arbiter - but the queue moves on. Skills learned through 0x2790 persist like 0x278E's: in the blob World saves. No real pair for either op in cap_social4 or cap_final.
