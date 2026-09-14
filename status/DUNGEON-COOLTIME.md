# Dungeon cool times (T25)

> **What this fixes.** Dungeon entry cooldowns were not stored at all. `DBS_LOAD_DUNGEON_COOL_TIME`
> always answered with three empty lists, so a relog forgot every cooldown and every entry count.

The record layout came out of T21/T22 (`status/ENTER-WORLD-FALLBACK.md` §4). T25 is the write side.

## 1. The write is four one-way `SA_` messages, not an `SDB_` pair

That is why it took a while to find: everything else per-character on this path is an `SDB_`
request with a `DBS_` reply carrying a DLM id. These four carry no DlmId, and none of their
Arbiter handlers has a `SendToSession` — they are fire-and-forget writes.

| opcode | name | payload |
|---|---|---|
| `0x13B6` | `SA_UPDATE_DUNGEON_COOLTIME` | `[0] u32 listOff=22 [4] u32 listLen=52 [8] u64 ArbiterUser [16] CoolTimeElem` |
| `0x13B7` | `SA_UPDATE_DUNGEON_CLEAR_COUNT` | `[0] u64 ArbiterUser [8] u32 ContinentId [12] u32 ClearCount` |
| `0x13B8` | `SA_UPDATE_DUNGEON_UI_HISTORY` | `[0] u64 ArbiterUser` + `vector<int> DungeonIdList` |
| `0x13BD` | `SA_DELETE_DUNGEON_COOLTIME` | `[0] u64 ArbiterUser [8] u32 ContinentId` |

Names and field names from the Arbiter's own packet dumpers. Offsets confirmed against the
handlers:

* `Handler_SA_UPDATE_DUNGEON_COOLTIME` reads the list offset at frame 6 and length at frame 10,
  memcpys the element and calls `DungeonInfoManager::UpdateCoolTime`.
* `Handler_SA_UPDATE_DUNGEON_CLEAR_COUNT` reads frame `0x0e` (ContinentId) and frame `0x12`
  (ClearCount) and calls `DungeonInfoManager::UpdateDungeonClearCount(int,int)`, which runs
  `{ call dbo.spUpdateDungeonClearCount }` with `(userDbId, continentId, clearCount)`.
* `Handler_SA_DELETE_DUNGEON_COOLTIME` reads frame `0x0e` and calls `FUN_140713220` on
  `User+0x6140` (the DungeonInfoManager).

**None of these carries a playerId** — only the `ArbiterUser` handle, which for us is the gameId we
put in `AS_ENTER_WORLD [24]`. The handlers resolve it through `DbProxyHandlers.PlayerIdForGameId`,
the hook T21 added; unwired, the write is dropped with a warning rather than guessed at.

They stay in `WorldReplayTable.OneWayFromWorld` **and** go in `IsHandledRequest`. Those are
different jobs: the first stops the replay-table builder pairing a one-way frame with the next
`A->W` frame as if it were its reply; the second is what lets `TryHandle` see the frame at all.

## 2. What is on the wire

Across all three captures there is exactly **one** dungeon write:

| capture | seq | opcode | what |
|---|---|---|---|
| cap_newchar.log | 2907 | `0x13B6` | dungeon 9827, entered 2026-09-13 05:52:56, counters (1, 1) |
| cap_newchar.log | 345/346 | `0x2867`/`0x2868` | empty, 35 B |
| lobby_tap.log | 170/171, 953/954 | `0x2867`/`0x2868` | empty |
| arb_world 2026-09-13 | 398/399 | `0x2867`/`0x2868` | dob, empty |
| arb_world 2026-09-13 | 884/885 | `0x2867`/`0x2868` | Test, **one 52-byte element for 9827** |
| arb_world 2026-09-13 | 841/842 | `0x148D` | the two pushes on the enter-world failure |

`0x13B7`, `0x13B8` and `0x13BD` have never appeared.

### The element travels intact, but not unchanged

cap_newchar's write and the relog capture's load are the same dungeon, six hours apart:

```
written 2026-09-13 05:52   9827 0AF00001 [2026-09-13 05:52:56] [2026-09-12 07:00] 1 1 0
served  2026-09-13 11:45   9827 0AF00001 [1970-01-01 00:00:00] [2026-09-12 07:00] 0 0 0
```

The first DateTime and both counters were reset in between — the daily reset
(`CheckAndResetDungeonPhaseUser`, the same boundary the `0x272D` list-5 trailer stamps). We do not
model that reset; a stored element stays as World last wrote it until World overwrites it. That is
the conservative direction: a stale *expired* cooldown is impossible, a stale *active* one is what
World would re-derive anyway on the next entry.

## 3. `DBS_LOAD_DUNGEON_COOL_TIME` (0x2868)

```
[0]  u32 off / [4]  u32 len    list 0  CoolTimeList     52 B per element
[8]  u32 off / [12] u32 len    list 1  ClearCountList
[16] u32 off / [20] u32 len    list 2  UiHistoryList
[24] u8  ok = 1
[25] u32 DlmId
[29] bodies
```

The writer's element stride for list 0 is `0x34` = 52, matching the element the pushes carry.

**Lists 1 and 2 are empty in all six captured replies**, so their element layouts have never been
observed. The clear counts `0x13B7` gives us are therefore **stored but not served** — the same
call the reputation and fatigability replies get in `status/ACHIEVEMENTS.md` §6. One capture of a
character with a cleared dungeon settles it.

## 4. What TeraSharp does now

`Persistence/CharacterStore.cs` — `dungeon_cooldowns(owner_id, dungeon_id, record, clear_count)`,
primary key `(owner, dungeon)`, with `UpsertDungeonCoolTime`, `SetDungeonClearCount`,
`GetDungeonCoolTimes`, `GetDungeonCoolTime` and `ClearDungeonCoolTime`. `record` is nullable
because a dungeon can have a clear count and no cool time, and because `0x13BD` clears the cool
time while leaving the count — the two live in different containers inside `DungeonInfoManager`.

`World/DbProxyHandlers.cs`

* `OnUpdateDungeonCoolTime` / `OnUpdateDungeonClearCount` / `OnDeleteDungeonCoolTime` — one-way,
  they send nothing and return true only so the replay table stays out of it.
* `OnLoadDungeonCoolTime` (0x2867) rebuilds `0x2868` from the rows. No dob exemption here, unlike
  the other login loads: his captured reply *is* the empty form, so the rebuild and the capture
  agree byte for byte.
* `BuildDbs2868` — byte-exact with no rows against cap_newchar seq 346 and relog seq 399, and with
  one row against relog seq 885. `Build2868_ThreeEmptyLists` now just calls it.
* The two `0x148D` pushes on `SA_ENTER_WORLD_FAIL` carry the stored element instead of T21's
  synthesized "never entered" one.

### The one thing that is not byte-exact

The capture's first `0x148D` push carries counters (1, 1) while the DB load two hundred frames
later carries (0, 0) for the same dungeon. The push is the real Arbiter's **live in-memory**
element with the attempt already counted; the load is the stored row. We push the stored row
twice. Nothing in the capture shows World reacting to the difference, and pushing a lower count
can only let a player in.

## 5. The client side

`C_DUNGEON_COOL_TIME_LIST` (0xD3F7) -> `S_DUNGEON_COOL_TIME_LIST` (0xD768), plus
`C_DUNGEON_CLEAR_COUNT_LIST` (0x5C98) and `C_VOTE_RESET_ALL_DUNGEON` (0xF783), are
client<->Arbiter and go through the `.def` registry, not this file. **Not implemented**: no
client capture on disk contains `0xD3F7`, so there is nothing to build the reply against, and
registering a handler needs `Handlers/HandlerRegistry.cs`, which is human-owned. If the client
does ask and gets no answer, the cooldown UI is empty — it cannot wedge World, because this
never reaches the DLM queue.

To wire it when a capture exists: add `C_DUNGEON_COOL_TIME_LIST` to the registry pointing at a
new handler in a Cowork-editable file, read the character's rows with
`CharacterStore.GetDungeonCoolTimes`, and encode them with the `.def` for `S_DUNGEON_COOL_TIME_LIST`
from `D:\v100\TERA_SERVER.100\tera_v100_MASTER_FINAL\`.
