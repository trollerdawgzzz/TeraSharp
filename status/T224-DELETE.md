# T224 — in-game character delete didn't stick

Reported: deleting from the lobby answers `S_DELETE_USER` ok, and the character is back in the
next `S_GET_USER_LIST`. Seen with `ServerConfig.xml` `<DeleteUser expireHour2="0">` and TeraSharp
restarted in between.

Not one bug. Three, and they compound.

| # | where | what |
|---|---|---|
| 1 | `CharacterHandlers.OnDeleteUser` | always scheduled a soft delete 72 h out, from the hardcoded `CharacterStore.DeleteExpireHours`. Nothing read `ServerConfig.xml`, so `expireHour2="0"` — *delete it now* — still parked the row for three days. |
| 2 | `CharacterStore.GetCharacters` | `SELECT * FROM characters WHERE account_id = $a` — **no `delete_at` predicate at all**. The parked row was listed again on the next login. This is the "reappears" symptom. |
| 3 | `CharacterStore.PurgeExpiredDeletes` | the only thing that ever hard-deleted a parked row, and **nothing in the server called it** — only tests. It also derives its cutoff from `deleted_at` plus one global hour count, so it could never honour a per-character window. |
| 4 | `Web/AdminApi.DeleteCharacterNow` | an unconditional *hard* delete while `C_DELETE_USER` was an unconditional *soft* one. The two disagreed about what "delete" means. |
| 5 | `OnDeleteUser` | removed the character from `s.Account.Characters` on a scheduled delete, and nothing ever re-adds it — so `C_CANCEL_DELETE_USER` in the same session could not bring it back, and retail keeps it listed anyway (below). |

So the answer to the brief's question — soft delete with an expiry the lobby ignores, or never
persisted? — is **soft delete, correctly persisted, and the lobby ignored it**; plus the expiry
was the wrong number and nothing ever collected it.

## 1. Retail, pinned

`cap_final_client2` frames 11 and 35 bracket a successful cancel (frame 32 `C_CANCEL_DELETE_USER`
→ frame 33 `S_CANCEL_DELETE_USER 01`). **Both `S_GET_USER_LIST` frames are 1183 bytes** and differ
in exactly six bytes, so a pending-delete character is *not* hidden from the list.

Element 0 (`S_GET_USER_LIST.18`, `isDeleting` at frame offset 107):

| field | frame 11 (pending) | frame 35 (cancelled) |
|---|---|---|
| `isDeleting` | 1 | **0** — the only semantic change |
| `deleteTime` | 1789879815 | 1789879815 — the stamp survives the cancel |
| `deleteRemainSec` | 259182 | 259150 — `deleteTime − now`, recomputed per send |
| `banRemainSec` | −1789620633 | −1789620665 — the same 32 s of wall clock |

Fixed part at frame offset 8: `veteran 0, bonusBufSec 0, maxCharacters 3, first 1, more 0,
leftDelTimeAccountOver 0, deletionSectionClassifyLevel 5, deleteCharacterExpireHour1 0,
deleteCharacterExpireHour2 72`.

Three things fall out. `deleteTime` is an **absolute** unix second and `deleteRemainSec` is
derived, the way `banRemainSec`/`lastLogoutTime` already were (T76). 259200 s is 72 h to the
second, so the delete was issued 18 s before the capture. And the three fixed fields are this
deployment's own `<DeleteUser expireHour1="0" expireHour2="72" deletionSectionClassifyLevel="5" />`
— retail sends the file's numbers, which is why they can't stay hardcoded.

`Handler_C_DELETE_USER` (Arb_part_079.c:9852) carries no hour argument: guard packet ≥ 8, read the
u32 at frame offset 4, refuse against the account's own character list, one call whose bool goes
into `S_DELETE_USER` (0xB80B) and then a `"DELETE_USER"` audit row. The window lives in the user
manager, i.e. in `ServerConfig.xml`. Nothing else could supply it.

Also visible there and **not** implemented (out of scope, worth a task): the handler refuses with
system message `0x2B2` (690) when `lVar6 != 0 && *(int*)(lVar6+0x1b54) >= 1 && *(int*)(lVar6+7000) == 1`
— a per-character state gate, almost certainly "can't delete a guild master", pairing with the
`0x2B3` (691) no-free-slot refusal T88 already models on the cancel side.

## 2. The fix

One path: `Persistence/CharacterDeletion.cs` (new).

| member | what |
|---|---|
| `Policy(ExpireHour1, ExpireHour2, ClassifyLevel)` | `ServerConfig.xml`'s `<DeleteUser>`, == `S_GET_USER_LIST`'s three delete fields |
| `Parse(xml)` / `Load(path)` / `Current` | read once and cached; a missing or broken file is the default, never an error (same contract as `WorldServerList.Parse`) |
| `Policy.WindowHoursFor(level)` | `level < ClassifyLevel ? ExpireHour1 : ExpireHour2` |
| `Delete(store, id, accountId, byWhom, level, now, policy, out deleteAt, log)` | window 0 → `DeleteCharacter` (hard). Otherwise → `SoftDeleteCharacter` with `now + hours`. Returns `Hard` / `Scheduled` / `Refused` |
| `LobbyFields(deleteAt, nowUnix)` | `(isDeleting, deleteTime, deleteRemainSec)`, clamped at 0 |

`OURS:` the **direction** of the classify-level split is inference. The three attributes sit in one
element and the capture pairs `classifyLevel 5` with hours `(0, 72)`, so a character below the
classify level is the throwaway that goes at once and one at or above it gets the full window.
Nothing in Arb_part_079 pins which way round. Set both hours the same to opt out.

Callers and edits:

| file | change |
|---|---|
| `Persistence/CharacterDeletion.cs` | new, above |
| `Persistence/CharacterStore.cs` | `GetCharacters`: purge pass, then `AND (delete_at = 0 OR delete_at > $now)`. New `PurgeDueDeletes(nowUnix, force)` purging by each row's **own** `delete_at`, called from `GetCharacters` (throttled to 30 s so the admin API's per-account loops don't re-scan). `PurgeExpiredDeletes` kept — T101b's tests pin it |
| `Handlers/CharacterHandlers.cs` | `OnDeleteUser` → `CharacterDeletion.Delete`; the character leaves `Account.Characters` only on a **hard** delete. `FillLobbyFields` now sets `isDeleting` / `deleteTime` / `deleteRemainSec` from `delete_at` |
| `Web/AdminApi.cs` | `/api/delete-character` → the same policy; `"hard":true` keeps the old immediate behaviour and says so in the audit row. New `JsonBool` helper |
| `status/T224-PATCH.diff` | `Handlers/LoginHandlers.cs` is human-owned: the three top-level fields from `CharacterDeletion.Current` instead of `5 / 0 / 72`. **One hunk, still to apply** |

`C_CANCEL_DELETE_USER` is untouched: it still goes `CancelCharacterDelete` → `RestoreDeletedCharacter`,
which clears `delete_at`/`deleted_at`/`deleted_by` and un-parks `deleted_items`. It now also
actually works end to end, because the character never left the session list.

## 3. Behaviour, before and after

| `<DeleteUser>` | character | before | after |
|---|---|---|---|
| `expireHour2="0"` | level 11 | parked 72 h, **listed again next login** | row and dependents gone at once, never listed |
| `expireHour1="0" expireHour2="72"` | level 3 | parked 72 h | gone at once (below `classifyLevel`) |
| `expireHour1="0" expireHour2="72"` | level 11 | parked 72 h, listed as *not* deleting | parked 72 h, listed **as deleting** with a real countdown; purged when `delete_at` comes due |
| any | window ran out while the server was down | listed again | excluded on sight by the `delete_at` predicate, then purged |
| `POST /api/delete-character` | any | always hard | follows the policy; `"hard":true` for test characters |

## 4. Tests

`src/TeraSharp.Arbiter.Tests/T224.cs`, six:

1. the policy is `ServerConfig.xml`'s — the shipped element, `expireHour2="0"`, null / broken XML /
   missing element / unparseable attribute all fall back rather than silently becoming 0, and the
   classify-level split
2. the countdown fields against `cap_final_client2` frame 11 — `(true, 1789879815, 259182)`, and
   259150 at +32 s
3. a zero window hard-deletes and `GetCharacters` never lists it again (the reported bug), plus the
   two refusal paths: deleting twice, and a null store
4. a scheduled delete stays listed, reports `isDeleting` with the full 72 h, parks its items, and
   `CancelCharacterDelete` undoes all of it
5. the window is per character (level 3 → now, level 12 → 72 h from the same policy) and
   `PurgeDueDeletes` is exact at the stamp: 0 one second before, 1 at it
6. `/api/delete-character` schedules under a 72 h policy and says restore is possible, `"hard":true`
   still drops the row, a second delete is a 404, and `JsonBool` reads true/false and 1/0

**Not run here.** This session has no .NET Framework/SDK path to the harness (NuGet restore is
blocked by the proxy, 403) and no shell on the deploy box. `dotnet run --project
src\TeraSharp.Arbiter.Tests` is still needed, and `status/T224-PATCH.diff` still needs applying.

## 5. T224b — two stale pins and one flag that never arrived

Three failures on master after the merge. Two were pins that T220b outgrew; one was a real bug in
T224's own new code.

| test | was | why |
|---|---|---|
| `T159_the_loader_reads_the_real_sheets` | `st.Entries == DefaultSkillSet.BuiltInTable.Count` | T220b added four Popori/Male rows to `DefaultSkillSet.xml`, 99 -> 103. The sheet is *allowed* to carry more than the transcribed table; the real invariant is the loop under it, which checks every transcribed row still resolves to the same ids. Now `>=`. |
| `DefaultSkills_table_covers_the_sheet_and_fits_the_regions` | `DefaultSkillSet.Count == 99` and `rows == 99` | **Not reported, same cause, order-dependent.** `DefaultSkillSet.Count` reads the LOADED table, and `DatasheetLoader.DefaultSkills` is process-wide static, so once `T159` has read the sheet in the same process this is 103 and the race/gender/class walk finds 103. Now: `BuiltInTable.Count == 99` exactly (that is the invariant worth pinning) plus `Count >= 99` and `rows >= Count`. |
| `T224_the_admin_delete_route_uses_the_same_policy` | `{"hard":true}` scheduled a soft delete | `JsonBool` was built on `AdminApi.RawValue`, whose scanner only walks number characters - `0-9 - + . e E`. It stops dead on the `t` of `true` and returns null, so the flag read as "absent" and the route fell through to the ServerConfig policy. `JsonBool` now walks the literal itself and takes `true`/`false`, `"true"`/`"false"` and `1`/`0`. |

One more thing the flag exposed: `CharacterStore.DeleteCharacter` cleared nineteen child tables but
not `deleted_items`, so a `"hard":true` on a character that was already parked left its parked items
behind as orphans keyed to an id SQLite can hand out again. `deleted_items` is now in the same batch,
which also makes `PurgeDueDeletes`/`PurgeExpiredDeletes` correct without their own pre-clear.

### Suite

Built and run here, in the cloud container, with Roslyn directly: NuGet is still blocked by the
proxy (`connect_rejected`), so `dotnet restore` cannot work - but the package DLLs are already in
`src\TeraSharp.Arbiter.Tests\bin\Debug\net8.0`, and both projects compile as ONE assembly with
`csc -main:TeraSharp.Arbiter.Tests.Program` plus `-resource:...,admin/<file>` for the ten
`Web\wwwroot` files the T206/T116 asset tests expect. `libe_sqlite3.so` is the container's own
`libsqlite3.so.0` under that name.

| run | result |
|---|---|
| no `Datasheet` folder (container only) | **1005 passed, 0 failed, 237 skipped** |
| `TERASHARP_DATASHEET` -> the real `DefaultSkillSet.xml` (103 rows) + the sheets the `Handshake_*` tests read | **1011 passed, 0 failed, 231 skipped** |

The second run is the one that matters: it reproduces the reported condition, and both
`T159_the_loader_reads_the_real_sheets` and `DefaultSkills_table_covers_the_sheet_and_fits_the_regions`
pass against the real 103-row sheet. All seven `T224*` tests pass in both.

The 231-237 skips are fixtures this container does not have - `D:\packetlogs` captures,
`data/*.bin`, `data.json`, per-task `frames.json`. One sheet had to be left out of the second run:
`DungeonNewbieBonus.xml`, whose copy in the container's mirror is from an older session and does not
match the staged `DungeonData_*.xml`, so the roster `DungeonClearCountSheet` derives from it came out
in a different order than `T134b` pins. That is the mirror, not the code; on the deploy box the
sheets are one matched set. `dotnet run --project src\TeraSharp.Arbiter.Tests` there is still the
authority, and `status/T224-PATCH.diff` still needs applying.
