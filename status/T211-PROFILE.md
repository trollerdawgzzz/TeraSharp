# T211 - Profile > Dungeon/PvP Stats: both tabs were answered with nothing

## 1. The two requests

| Tab | Client asks | Server answered | Now |
|---|---|---|---|
| PvP Record | `C_VIEW_BATTLE_FIELD_RESULT` `0xEC3D` | registered to `OnAcceptSilently` - **no reply at all** | `S_VIEW_BATTLE_FIELD_RESULT` `0xE459` |
| Dungeon History | `C_DUNGEON_RANK_RECORD_LIST` `0xF679` | answered, but the board was hardcoded empty | filled from `dungeon_rank_records` |

No capture in the tree opens either tab - `cap_bg1`, `cap_bg4` and the client logs contain no
`0xEC3D`, `0xE459`, `0xF679` or `0xC9E3` at all - so both layouts are **decompile-marked**. The one
member of the family that IS captured, `C_DUNGEON_CLEAR_COUNT_LIST` (cap_bg1_client1 586/588, a
337-byte reply of 25 rows), already worked and is untouched.

## 2. `C_VIEW_BATTLE_FIELD_RESULT` - the request

`Handler_C_VIEW_BATTLE_FIELD_RESULT`, Arb_part_041.c:14181-14232: guards `param_3 < 6`, treats
`param_2[2]` as a string ref, resolves that **character name** to a user
(`FUN_14082d170(DAT_141214fe8, name)`) and queues `BattleFieldResultManager::DoAsyncJob`. So the
body is `[u16 offset][wstring name]` - the same shape `C_DUNGEON_CLEAR_COUNT_LIST` carries, and the
window can be opened on somebody else.

## 3. `S_VIEW_BATTLE_FIELD_RESULT` - the reply

Dumper `FUN_140307ba0`, Arb_part_024.c:10014-10479. Every name below is a `L"..."` literal in it and
every offset is the one it reads from.

```
packet +4  u16 count / +6 u16 firstOffset      ResultList
packet +8  i32 UserDbId          (puVar11 + 4)
packet +12 i32 SeasonPeriod      (puVar11 + 6)   -> body 12; guard is 0xf < len
element, 64 B: u16 here / u16 next then fifteen i32
  +4  BfId           +8  BfType         +12 CountOfWin       +16 CountOfLoss
  +20 CountOfDraw    +24 CountOfKill    +28 CountOfDeath     +32 CountOfGiveHelp
  +36 CurrRankScore  +40 CurrRank       +44 BestRank         +48 CountOfDestroy
  +52 GradeScore     +56 Grade          +60 CountOfCapture
```

## 4. Where the numbers come from

T199 already files everything: `BattlegroundResults.FileResults` writes one `game_log` row per
participant per finished match (`category = pvp`, `action = battleground.result`,
`template_id = BattleFieldId`) whose `extra` JSON holds the whole native `Result` record. So the tab
is a fold, not a new table - `World/BattlefieldRecords.cs`.

**Outcome, pinned:** `User::ProcessBattleFieldResult(int,int)`, Arb_part_029.c:15355 returns
immediately when the outcome is `2`, then :15359 computes `isWin = (outcome == 1)` and takes
`User::GetBFWinCount` for a win and `User::GetBFLoseCount` otherwise. **1 = win, 2 = draw the native
record ignores, anything else = loss.**

| Field | Source |
|---|---|
| `BfId`, `BfType` | `Result.BattleFieldId`, `Result.BattleFieldType` |
| `CountOfWin` / `Loss` / `Draw` | outcome, per the pin above |
| `CountOfKill` / `Death` / `GiveHelp` / `Capture` / `Destroy` | summed per battleground |
| `GradeScore` | best `Result.NativeGradePoint` |
| `CurrRankScore` | `characters.bg_rating` (T138c) - the number `S_PVP_RANKING_LIST` already ranks by |
| `CurrRank` | new `CharacterStore.GetBgRatingRank` - position on that board, 1-based, 0 for no rating |
| `BestRank` | **OURS:** repeats `CurrRank`; no rank history is kept |
| `Grade` | **OURS:** 0; the native end-of-match record carries one grade value, which goes to `GradeScore` |
| `SeasonPeriod` | **OURS:** 0; the server runs no battleground season and nothing says what "none" looks like |

## 5. Dungeon History

`OnDungeonRankRecordList` said "nothing in this build records a dungeon run" - but T167 does:
`SDB_UPDATE_DUNGEON_RANK_RECORD` fills `dungeon_rank_records` with World's own `TopPointRecord` and
`TopTimeRecord`. New `CharacterStore.GetDungeonRankBoard(dungeonId, season)` reads them back, joined
to `characters` for name/class/race/gender and LEFT JOINed to `guilds` for the guild name, ordered
the way the window ranks them (highest point, then fastest time). `Rank` is the 1-based position,
the caller's own row becomes `myRecord` so `HasMyRecord` is set and the header fills in, and
`lastSortTime` is the newest `play_date`. An empty table still answers, so the window says
"no record" instead of waiting.

`S_DUNGEON_CLEAR_COUNT_LIST` (the clears column) was already wired to
`dungeon_cooldowns.clear_count` via `SA_UPDATE_DUNGEON_CLEAR_COUNT` (0x13B7) and needed nothing -
it reads 0 for every dungeon simply because no dungeon was cleared in that session.

## 6. Files

| File | Change |
|---|---|
| `World/BattlefieldRecords.cs` | **new.** The fold, the Outcome pin, the page bound. |
| `Handlers/ArbiterClientHandlers.cs` | `S_VIEW_BATTLE_FIELD_RESULT` + builder + handler; `OnDungeonRankRecordList` now reads the store. |
| `Persistence/CharacterStore.cs` | `GetDungeonRankBoard`, `GetBgRatingRank`. |
| `status/T211-PATCH.diff` | **apply this** - `HandlerRegistry.cs` is human-owned: it moves `C_VIEW_BATTLE_FIELD_RESULT` out of the silent list and registers the real handler. Until it is applied the tab stays empty. |
| `Tests/T211.cs` | 3 tests. |

## 7. Tests

- `T211_view_battle_field_result_is_the_dumpers_layout` - the 16-byte empty head, and every one of
  the fifteen element fields at the offset the dumper reads it from.
- `T211_pvp_record_folds_the_filed_match_results` - three real `BSA_END_BATTLE_FIELD_RESULT_LIST`
  payloads through `FileResults`, folded to 1-1-0 with summed counters and the best grade; draw
  counted as neither win nor loss; the loser's own row separate; the rating rank.
- `T211_dungeon_record_board_is_the_stored_rank_rows` - ordering, season filter, identity columns,
  and the framed head with `HasMyRecord`.
