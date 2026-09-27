# T195–T199 changes ready to merge

Worktree `TeraSharp-cowork`, branch `cowork/T8`, base/master `dca1a71`.
No assistant commit, merge or deployment. Final isolated build with human patches:
**1055 passed, 0 failed, 26 skipped**; four existing nullable warnings.

| Task | Change | Evidence, files and limits |
|---|---|---|
| T195 remainder | Current-World routing for character actions, native party fan-out, stable internal party recipients, return to the configured main World after reset/leave/Unstuck. | [T195–T198 detail](T195-T198-CHANGES.md), [World routing](MULTIWORLD-DESIGN.md) |
| T196 | Preserve World-requested achievement reward parcel attachments and notify; existing claim grants the items. | [Achievement evidence](ACHIEVEMENTS.md); old lost rewards are not backfilled. |
| T197 | Persist glyph unlock/use and points; remove the false zero-use crest push after topo-fin. | [Persistence evidence](PERSISTENCE-MAP.md) |
| T198 | Serialize delivery per player after tunnel reordering, preventing artisan packets overtaking login. | [Crafting evidence](CRAFTING.md); J on the GM still needs live confirmation. |
| T199 | Forced BG creation, offer/timer, entry/return, native result/score persistence, correct win/loss selection, custom rating bounds from XML. | [Full BG audit](T199-BATTLEGROUND.md); ordinary MatchServer pool/popup and premature dropout are not captured. |

## Rating sheet

Edit `data/custom-datasheets/TeraSharpBattlegroundRating.xml` (`minDelta="5"`, `maxDelta="12"`).
Publishing places it at `custom-datasheets/TeraSharpBattlegroundRating.xml` beside the Arbiter.
A copy in the configured `TERASHARP_DATASHEET` directory takes precedence. Restart the Arbiter
after changing it. Wins add the configured random amount; losses subtract it with the existing
zero floor. Native MMR remains separate from this custom leaderboard policy.

## Human integration

CLAUDE.md reserves WorldBridge/HandlerRegistry integration for the human. Apply each patch
sequentially, checking it immediately before application, then build/test/commit/merge:

1. `status/T195B-PATCH.diff`
2. `status/T197-PATCH.diff`
3. `status/T198-PATCH.diff`
4. `status/T199-PATCH.diff`

Do not check all four against the original source at once: T199 applies after T198.
The isolated validation copy uses exactly this sequence. Its34 changed/new source files
match the worktree by SHA-256;252 selected T195–T199 evidence frames were verified.

Live checks: instance chat/menus/reset/leave/Unstuck and GM commands; new achievement mail
and claim; glyph relog/restart; GM crafting J before/after dungeon entry; forced BG entry,
fight/result, both returns and the XML-configured rating movement.
