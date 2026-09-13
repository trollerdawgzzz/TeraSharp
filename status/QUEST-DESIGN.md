# Quest persistence — design (T17)

> ## ONE CAPTURE WOULD SETTLE THE REST
>
> Log the existing **"Test"** character in against the real `ArbiterServer.exe` with
> `arbiter-world-tap.js` running, and read the single `0x272D` reply it sends. That character has
> three completed quests (59901, 59902, 59903) and one in progress (59904), so its reply is the
> only thing that can tell us:
>
> - whether **list 1** is the completed-quest list and **list 2** the completed ids,
> - whether list 0 is active-only or active+complete,
> - and probably what record field **`[24]`** means.
>
> Until then TeraSharp **stores completed quests but does not serve them**: World will offer those
> three quests again on the next login. Everything else in T17 is implemented and byte-exact.

**Status (updated after T17 part 2): the active-quest half is implemented.** `0x272C -> 0x272D` is
rebuilt from stored rows and `0x272E -> 0x272F` is a real write handler; see §8. Two things in the
original T17 brief did not survive contact with the captures — see §7.

Sources, in the order they win:

- `D:\v100\TERA_SERVER.100\ArbiterServer.exe.c` — `Handler_SDB_SET_QUEST_INFO` (tracer line
  1283916), `Handler_SDB_LOAD_QUEST_LIST` (1275742), and the `0x272D` writer `FUN_1406edac0`
  (1206774, opcode at 1206795).
- `D:\packetlogs\cap_newchar.log` — real ArbiterServer, brand-new character "Test" playerId 2.
  One login, 28 × `0x272E`, one `0x272C`.
- `D:\packetlogs\lobby_tap.log` — "dob", a played character: login + relog, so two `0x272D`
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

Lists 1, 2 and 4 are empty in all three. **List 3 is global, not per-character** — the handler
fills it from `GlobalDailyQuestSeed::GetGlobalDailyQuestSeed`, which is why it is empty on the
first login of a World process and 18 records on the relog. Sending it empty is what makes World
generate the seeds itself and send the 17 `0x2899` items we already answer
(`status/STATUS.md` §1), so **empty is the behaviour we want**, not a gap.

The 20-byte list-5 blob is `[u32 ?][DateTime 16 B]`, the DateTime in the Arbiter's packed
`{u16 year, month, day, hour, minute, second, …}` form:

```
new character   00 00 00 00 | B2 07 01 00 01 00 00 00 | 00*8     -> 1970-01-01, i.e. never
dob relog       00 00 7F 90 | EA 07 09 00 0C 00 07 00 | 00*8     -> 2026-09-12 07:xx
```

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
80-byte record echoed back by pointer+length (`local_1a8 = &record; local_1b0 = 0x50`).

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

**Where completed quests go in the reply.** Lists 1 and 2 are empty in all three captured
`0x272D` replies, and neither capture contains a login by a character with a completed quest —
"Test" completes three quests and then the capture ends; dob's only quest is active at step 1
both times. So there is no evidence for:

- whether list 0 is active-only or active+complete,
- whether list 1 is the completed-quest list and list 2 the completed **ids** (the names
  `RawQuestHelper::InsertRawCompletedQuestData` and
  `QuestDataManager::RefreshCacheCompletedQuestData` in the Arbiter suggest exactly that, but
  suggestion is not evidence),
- what `[24]` and `[64]` in the record mean.

A capture that settles it is cheap: take the "Test" character, log in against the real
ArbiterServer with the tap running, and read the one `0x272D`. Everything in §5 works for active
quests today; completion is guesswork until that capture exists.

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
- `OnLoadQuestList` / `BuildDbs272D` — list 0 from the stored active rows, lists 1-4 empty
  (list 3 deliberately, so World seeds itself), list 5 the "never" trailer. dob (playerId 1) keeps
  the captured 1377-byte reply until he has rows of his own.
- Completed quests are counted and named in a warning on every load, pointing at this file.

Verified in Python against `cap_newchar.log`, not built: all 28 `0x272F` replies rebuild byte for
byte (given the capture's questDbIds 2-5), and `BuildDbs272D` with no rows reproduces seq 342's
73-byte payload exactly.
