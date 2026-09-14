# Reputation (0x2890) and fatigability (0x2909) — T26

The last two per-character login loads that were still replaying dob's captured bytes to every
character. `status/ACHIEVEMENTS.md` §6 declared both unpinnable from the captures; that was
right about the *captures* and wrong about the conclusion — the decompile pins both. This file
replaces §6.

Everything below is verified byte-for-byte in Python against the captures named in each section.
**No build was possible here**; the C# is written to match the verified bytes.

---

## 1. Reputation

### 1.1 The messages

```
SDB_UPDATE_REPUTATION_INFO  0x2891  (frame 78 B, payload 72 B)   cap_newchar.log seq 413
  [0]  u32 recordOffset  (frame-relative, 26 -> payload 20)
  [4]  u32 recordLength  (0x34 = 52)
  [8]  u32 DlmId
  [12] u32 OwnerDbId
  [16] u32 UpdateType
  [20] ReputationData, 52 bytes
  -> DBS_UPDATE_REPUTATION_INFO 0x2892: [u8 ok][u32 DlmId]   (ok FIRST; capture: 01 2B 00 00 00)

SDB_LOAD_REPUTATION_LIST    0x288F  ->  DBS_LOAD_REPUTATION_LIST 0x2890
  [0]  u32 listOffset = 19 (frame-relative; payload 13)
  [4]  u32 listLength = 52 * n
  [8]  u8  Success = 1
  [9]  u32 DlmId
  [13] n x 52-byte ReputationData, ordered by reputation id
```

`UpdateType` selects the Arbiter's path: **1** -> `ReputationDataManager::AddNewReputationInfo`
(`FUN_1404857c0`), **2** or **4** -> `::UpdateReputationInfo` (`FUN_1404a6480`). Anything else
falls out of the switch, leaves `ok = 0`, and stores nothing — `BuildDbs2892` copies that.
`ok = 0` still completes the DLM item (it just selects `OnFail`), so the reply must be sent either
way.

### 1.2 There is no transform — the record is stored verbatim

Both write paths end the same way: the incoming 52 bytes are memcpy'd into the `std::map` node on
`User+0x60c0`, keyed on **record+4** (the reputation id). `::GetAllReputationData`
(`FUN_1404999e0`) walks that map with stride `0x34` and copies each node straight into the reply.
So the load record *is* the stored record. What made §6 think otherwise was two words that differ
between the write and the load:

```
0x2891 write  02000000 62020000 06000000 00000000 00000000 18790000 3F420F00 00000000 04030500 ...
0x2890 load   01000000 62020000 06000000 00000000 00000000 18790000 3F420F00 00000000 B2070100 ...
              ^^ +0                                                          ^^ +32
```

Neither is written by World. Both come from the *loader*, not from the map:
`ReputationDataManager::CacheReputationData` runs `dbo.spLoadAllUserReputation` and binds **eight**
output columns into a row buffer, then copies 52 bytes of that buffer out.

* **record+0** is the buffer slot the userDbId was written into before the query — the **OwnerDbId**.
  §6 said it was 1 for both dob and Test and therefore not the owner. That was a mis-read: a
  byte-level diff of the two captured replies shows they differ at **exactly one offset, +0**, with
  dob = 1 and Test = 2. It is the owner.
* **record+32** is a slot that is **never bound to a column**. Whatever the row buffer held lands
  there. Both captured replies carry `0x000107B2` because both came from the same code path on the
  first (and only) row. This is the same situation as the item record's uninitialised tail in T13:
  not a field, but what the real server sends, and World demonstrably never reads it.

### 1.3 What TeraSharp does

`reputations(owner_id, reputation_id, record BLOB)` — the raw 52 bytes, keyed on record+4, last
write wins (which is what both Arbiter paths amount to). `BuildDbs2890(records, reqId, ownerDbId)`
re-stamps the two loader-owned words: `+0` with the owner, `+32` with `ReputationLoaderResidue`
(`0x000107B2`). Everything else passes through untouched.

Verified byte-exact:

| reply | capture | rebuilt from |
|---|---|---|
| empty list | `cap_newchar.log` seq 336 | no rows |
| dob's list | relog seq 389 | the one 0x2891 write, owner 1 |
| Test's list | relog seq 875 | the same record, owner 2 |

The last two lines are the point: **one** stored record reproduces **both** captured replies, which
is only possible if +0 is the owner.

---

## 2. Fatigability

### 2.1 The write is 0x2910, and the C# const was misnamed

`SDB_LOAD_FRIEND_INFO = 0x2910` is wrong — the opcode is **SDB_UPDATE_FATIGABILITY_POINT**.
`SDB_FATIGABILITY_UPDATE` is now the name to use; the old const stays as a documented alias because
the allow-list, the dispatch switch and the T15 tests all reference it. §6 said "there is no write
opcode for it in either capture"; there are five, and they were being answered by name as a friend
load the whole time.

```
SDB_UPDATE_FATIGABILITY_POINT 0x2910 (frame 23 B, payload 17 B)
  [0]  u32 DlmId   [4] u32 UserDbId   [8] u32 kind = 1   [12] u32 DELTA   [16] u8
  -> 0x2911: [u8 ok = 1][u32 DlmId]           (ok first, like 0x2892)

DBS_LOAD_FATIGABILITY_LIST 0x2909 (45 B)
  [0]  u32 listOffset = 23 (payload 17)   [4] u32 listLength = 28   [8] u8 Success = 1
  [9]  u32 DlmId                          [13] u32 AddtionalFatiguePoint = 0
  [17] ONE 28-byte element:
       [0] u32 kind = 1  [4] u32 curPoint  [8] 16-byte TIMESTAMP  [24] u32 (unexplained)
```

World copies the 28 bytes into `FatigabilityInfo` verbatim (`DBLoadFatigabilityContext::
ExecuteCommit`, stride `0x1c`), so the element is opaque on that side too.

### 2.2 The payload is a delta, and the total belongs to the ACCOUNT

The three replies §6 called contradictory are a single running total, and the arithmetic closes
three times — twice **across characters** on the same account:

```
lobby_tap   2505  + 15                 = 2520   (seq 376 reads 2520)
cap_newchar 2520  + 135 (seq 511) + 0 (seq 2068) = 2655   (relog seq 430 reads 2655, dob)
relog       2655  + 270 (seq 549) + 15 (seq 748) = 2940   (relog seq 916 reads 2940, Test)
```

The second chain is written by Test (playerId 2) and read by dob (playerId 1); the third is written
by dob and read by Test. Per-character storage cannot produce that. Hence
`fatigability(account_id PRIMARY KEY, cur_point, updated_at, tail)`.

The 16-byte timestamp is the ODBC `tagTIMESTAMP_STRUCT` this project has now proven three ways
(u16 year, month, day, hour, minute, second, u32 fraction); it tracks the last write, which is why
it was the login time for one character and not the other. A never-written account gets the
1970-01-01 form, the same blob as `DungeonCoolTimeNever`.

### 2.3 The one thing still unexplained — and why it is safe

The element's trailing u32 is 630 / 443 / 426 / 1626 / 88 across the five captured replies. It has
no relation to the point, the timestamp, the elapsed time, or any monotonic counter, and World
never reads it. **We send 0.** Every other byte of all three tested replies reproduces exactly;
the tests assert that and assert that the captures really did carry a non-zero value there, so the
day the meaning turns up the test says so.

What would settle it: a capture where the same account is read twice inside one session with a
known amount of play between the reads.

---

## 3. Files

* `World/DbProxyHandlers.cs` — `OnUpdateReputation`, `OnLoadReputationList`, `BuildDbs2890`,
  `SliceReputationRecord`, `OnUpdateFatigability`, `OnLoadFatigability`, `BuildDbs2909`,
  `EncodeDbDateTime`; `SDB_REPUTATION_LIST` and `SDB_FATIGABILITY_LIST` moved from
  `DispatchOnlyForTests` into `IsHandledRequest`.
* `Persistence/CharacterStore.cs` — `reputations` and `fatigability` tables, `UpsertReputation`,
  `GetReputations`, `AddFatigabilityPoints`, `GetFatigability`, `FatigabilityRow`.
* `src/TeraSharp.Arbiter.Tests/Program.cs` — 10 tests against `data/cap_t26.bin`.
* `data/cap_t26.bin` — the 11 frames the tests need (`data/cap_t26.md`).

### Who gets what

Reputation keeps the T22 convention: **playerId 1 (dob) is still served the captured static**
(`ServesCapturedStatics`), every other character is rebuilt from rows. Dob's captured reply is
reproducible from a stored record — the test proves it — so the static is a convenience, not a
crutch, and it can go the day dob's rows are seeded.

Fatigability has **no** static path: the total is per account, so serving dob a captured 2655 while
his account row says something else would be a lie. Every character, dob included, is served the
account's row (0 and the 1970 timestamp until the first 0x2910 write).
