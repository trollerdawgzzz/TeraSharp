# Web Admin Tool against TeraSharp (T73, research)

The shipped ASP.NET tool (`D:\v100\TERA_SERVER.100\WebApp`, IIS port 5050, `Live-100.02 TW #9`,
WebAppRevision 487289) driving TeraSharp instead of the real ArbiterServer. Research only — no code.

Evidence: `WebApp\AppResource\WebAppConfig.xml`, `WebAppDatabaseDefinition.xml`, strings from
`WebApp\bin\WebApp.dll`, and the Arbiter decompile (`Arb_part_*.c`). Every inference is labelled.

---

## 1. What the tool actually talks to

The brief's premise needs one correction: **the tool does have a DB connection, and it is not the
game DB.** `WebAppConfig.xml` is 351 bytes and declares exactly:

```
<WebAdminDB odbcconn="...Server=127.0.0.1,1433;Database=WebAppDB_2800;UID=sa;..." />
<SteerServer addr="127.0.0.1" port="8105" />
<Admin id="gadmin" />
```

So there are **four** dependencies, not two:

| # | dependency | carries | needed for phase 1? |
|---|---|---|---|
| 1 | `WebAppDB_2800` (SQL Server) | the tool's OWN state — 23 tables, none of them accounts or characters | **yes** |
| 2 | Steer, `127.0.0.1:8105` | operator login for `gadmin` | **yes** (see §5) |
| 3 | Arbiter web-admin port (`listenPortForWebAdmin`, 8081) | live operations on loaded users | **yes** (read-only subset) |
| 4 | `ODBCCon/SharedDB` | per-server game data (accounts, characters) | yes for lookups |

`WebApp.dll` reads `/WebAppConfig/ODBCCon/SharedDB` and has a `GetSharedDBConnStringList`, but the
**shipped `WebAppConfig.xml` has no `SharedDB` element at all** — only `WebAdminDB`. That absence,
not a missing feature, is why account/character pages come up empty. It is also the lever: whatever
we point `SharedDB` at is where the tool reads game data from.

`fcgi_gw` is **not** the tool's data API. Its `config_fcgi_gw.txt` is `hub_ip=127.0.0.1
hub_port=11001 port=10002 server_srl=1` — it is a hub gateway on 10002, and `WebApp.dll` contains no
HTTP client for it (the only `http://` string in the assembly is a W3C doctype URL). Nothing in the
tool calls port 8020. **Correction to the brief.**

### `WebAppDB_2800` — the 23 tables

| group | tables |
|---|---|
| announces | `Announce`, `IngameAnnounce` |
| restore workflow | `RestoreCandidateData`, `RestoreItemMoney`, `DeletedItemData` |
| bulk ops | `BulkOperationFile`, `BulkOperationItem` |
| server registry | `ServerList`, `ServerStatus`, `ServerStatusLog` |
| events | `EventSystemType`, `EventSystemReward`, `GmEventRewardTemplate`, `OxQuizTemplate`, `NpcSpawnEvent_Npc/_Reward/_Zone` |
| misc | `Memo`, `WatchAccount`, `Task`, `LogPreset`, `SingleQueries`, `DbInfo` |

105 stored procedures, 385 columns. **No account or character table** — which settles the
architecture: the tool never owned game data, it always read it from `SharedDB` and acted on it
through the Arbiter.

---

## 2. The web-admin wire protocol (port 8081)

Confirmed against the decompile; this **supersedes** T36's note with the exact layout.

The port is read from config key `listenPortForWebAdmin` into `DAT_14121675c`
(`Arb_part_033.c:13277`) and bound with `WebAdminServerSessionFactory`
(`Arb_part_033.c:14109-14115`, banner `web admin Server listen at [%d]`). The session is
`PdlSession<class Session, unsigned short>` (`Arb_part_071.c:17877`) and the shared PDL reader is
`FUN_1400904a0` (`Arb_part_003.c:19819`).

**Framing — 4-byte header, both fields u16 little-endian:**

| bytes | field |
|---|---|
| 0-1 | `u16 size` — TOTAL frame length **including** these 4 bytes; `< 4` is rejected |
| 2-3 | `u16 opcode` |
| 4.. | payload |

Handlers receive `(session, framePtr, size)` and their minimum-length guards are against the
**total** size, header included. The opcode is u16 here; the World link is the same `PdlSession`
template instantiated with `int` (`Arb_part_071.c:17913`), which is why the two channels differ.

Dispatch is a **numeric table** indexed by opcode — `handlerTable[opcode]` at
`Arb_part_003.c:19882-19893`, out-of-range logs `Pdl packet Id is incorrect.`, and a structurally
bad frame logs `invalid web admin packet recved invalid web packetId[%d]` /
`invalid webAdmin packet check PDL_Admin.xml.cs` (`FUN_14052d180`, `Arb_part_043.c:15185`).

Replies use the **client-side** writer family (`FUN_140055430` u16, `FUN_1400554e0` u32,
`FUN_140351320` bool, `FUN_1403511c0` u64, `FUN_140350f60` wstr), **not** the inter-server family.
Strings are offset-only `u16` slots pointing at NUL-terminated UTF-16LE inside the frame.

### Two things that make this easier than expected

1. **There is no authentication on 8081.** No handshake, no version exchange, no IP allowlist
   (`WebAdminServerSessionFactory::OnConnectSession`, `Arb_part_047.c:17692`, only logs the peer IP
   — contrast `TextAdminSession::OnConnect`, `Arb_part_071.c:17853`, which does enforce a mask), and
   no per-session logged-in flag: none of the 14 handlers examined reads any auth state off the
   session. **`WA_CHECK_LOGIN` is not the tool's login** — it is "is game user-id X currently loaded
   in this Arbiter?" (`FUN_14050a9d0`, `Arb_part_042.c:11667`: `UserManager::FindUserWithLock(userId)`
   then the user's login-state at `+0x3fc0`). It stores nothing on the session and therefore cannot
   be a gate. Operator auth lives entirely in the ASP.NET app.
2. **The audit trail is a free-text reason string inside each mutating packet**, not an
   authenticated operator identity. The Arbiter never stamps who did it; the log helpers
   (`FUN_140660ff0` money, `FUN_14065fed0` item, `FUN_140666b70` undelete) take that string and
   nothing else. A reimplementation should log the reason **and** the source IP/session, which is
   strictly better than the original.

### Message catalogue

Reply opcodes are read verbatim from the writers. **Request opcodes are not recoverable from the
decompile** — they are u16 indices into a data-section table, and their names live in
`WebApp.dll`'s `PDL_Admin` enum (the Arbiter's own error string names `PDL_Admin.xml.cs`).

> **Working hypothesis, cheap to test:** requests and replies are allocated as adjacent pairs, so
> `WA_x = AW_x - 1`. `AW_RESULT_FORCE_KICK 0x1f59` / `AW_RESULT_FORCE_KICK_ACCOUNT 0x1f5b` being two
> apart, with a request between them, is what that pattern looks like. **Test:** connect to 8081,
> send `[u16 8][u16 0x1f56][u32 userId]`, and see whether `0x1f57` comes back. One packet settles
> the whole numbering. Until then every number below is a *reply* opcode only.

| message | handler | min size | request fields (offset: type) | reply | reply fields |
|---|---|---|---|---|---|
| `WA_CHECK_LOGIN` | `FUN_14050a9d0` `Arb_part_042.c:11667` | 8 | 4: u32 userId | `0x1f57` | u32 result (0 found / 2 not), u32 userId, bool isLoggedIn |
| `WA_GET_USER_POSITION` | `FUN_140519370` `Arb_part_043.c:1546` | 10 | 4: u16 (unread), 6: u32 userId | `0x1fdb` | u32 notFound, u32 zone, u32 X, u32 Y, u32 Z (floats truncated to int) |
| `WA_FORCE_KICK` | `FUN_1405186e0` `Arb_part_043.c:1020` | 10 | 4: u16 reason-offset, 6: u32 userId | `0x1f59` | u32 result, u32 userId |
| `WA_FORCE_KICK_ACCOUNT` | `FUN_140518850` `Arb_part_043.c:1081` | 12 | 4: u64 accountId | `0x1f5b` | u32 result (hard-coded 0) |
| `WA_INSTANT_INGAME_ANNOUNCE` | `FUN_14051c710` `Arb_part_043.c:3957` | 28 | 6: u16 msg-offset, 16: u32, 20: int type (1..3), 24: u32 | `0x2176` | u32 result (0 / 3 invalid) |
| `WA_ADD_INGAME_ANNOUNCE` | `FUN_1404f7530` `Arb_part_041.c:18174` | 48 | 6: u16 msg-offset, 16/20/24/28: u32, 32: u64, 40: u64 | `0x2174` | u32 result |
| `WA_REQUEST_RESERVED_ANNOUNCE` | `FUN_140522700` `Arb_part_043.c:8024` | 4 | none (a query) | `0x1f4e` | list of { u32 id, wstr, wstr } |
| `WA_DELETE_USER` | `FUN_140510930` `Arb_part_042.c:15741` | 18 | 14: u32 userId (4-13 unread here) | `0x20e6` | u32 result (0 / 2 not found) |
| `WA_UNDELETE_USER` | `FUN_14052ab20` `Arb_part_043.c:13601` | 26 | 4: u16 reason-offset, 14: u64 accountId, 22: u32 userId | `0x1f89` | u32 result (0 / 2 / 0x16), u32 userId |
| `WA_CHANGE_MONEY` | `FUN_140505460` `Arb_part_042.c:7996` | 26 | 4: u16 reason-offset, 14: u32 userId, 18: u64 target | `0x1f4b` | u32 result, u32 userId, u64 newMoney, u64 oldMoney |
| `WA_ADD_ITEM` | `Arb_part_041.c:18370` | 262 | 6/8/10: u16 name/custom/desc offsets, 14: u32 userId, 32: u32 templateId, 36: int count, + option block | `0x1f45` | u16, u16, u32 result, u32, u32 itemId |
| `WA_ADD_CHARACTER_RESTRICTION` | `FUN_1404f45b0` `Arb_part_041.c:16181` | 34 | 4: u16 reason-offset, 14: u32 userId, 18: int type, 22: u32, 26: u64 (type 3) | `0x1f85` | u32 result, u32 userId, int type, u32 |
| `WA_DEL_CHARACTER_RESTRICTION` | `FUN_140511b10` `Arb_part_042.c:16480` | 34 | 4: u16 reason-offset, 14: u32 userId, 18: u32, 22: int type, 26: u64 | `0x1f87` | u32 result, u32 userId, u32 |

The Arbiter has ~452 `Handler_WA_*` and ~453 `AW_*` writers; `WebApp.dll` mentions 909 distinct
`WA_`/`AW_` tokens, i.e. essentially all of them. The thirteen above are the ones the brief's
features need; the rest are content-ops (festivals, gacha, dark rift, city war, battlefields) that
TeraSharp has no subsystem for and that phase 4 can refuse cleanly.

---

## 3. The killer constraint: almost everything needs the character LOADED

Grouping the thirteen by what they touch (this is what decides the phasing):

| group | messages | behaviour |
|---|---|---|
| **A. live query** | CHECK_LOGIN, GET_USER_POSITION | `FindUserWithLock`; answer from memory. No DB. |
| **B. live action** | FORCE_KICK, FORCE_KICK_ACCOUNT, INSTANT_INGAME_ANNOUNCE | act on the live session. No DB. |
| **C. live mutation** | CHANGE_MONEY, ADD_ITEM, ADD/DEL_CHARACTER_RESTRICTION | **require the character to be online** — offline returns result 2 — mutate it in memory, and rely on the normal user-save for durability |
| **D. explicit DB** | ADD_INGAME_ANNOUNCE (`spAddIngameAnnounceInfo`), REQUEST_RESERVED_ANNOUNCE (`spLoadIngameAnnounceInfo` at boot), DELETE_USER (`spDeleteTBAUserAdmin`), UNDELETE_USER (`spUnDeleteUser`) | call a stored procedure directly |

Group C is the trap. The original tool cannot give an offline player an item or money at all. A
TeraSharp implementation **can** — our `CharacterStore` is the live store — so we get to choose:
match the original's refusal, or write straight to SQLite and be strictly more useful. Recommend the
latter, with the same result codes so the tool's UI stays happy, and a note in the log line.

---

## 4. What TeraSharp can serve today

Against the current schema (the `RequiredTables` list in `World/SelfTest.cs` plus T59/T62 additions):

| need | TeraSharp today | verdict |
|---|---|---|
| character by id/name, level, class, race, zone, position | `characters` | **have** |
| account, admin level | `accounts` (+ `admin_level`, T32) | **have** |
| money | `characters.money` (T59) | **have** |
| items / inventory | `items`, `warehouses` | **have** |
| parcels / mail | `parcels`, `parcel_items` | **have** |
| guild membership | `guilds`, `guild_members`, `guild_log` | **have** |
| friends / blocks | `friends`, `blocks`, `friend_groups` | **have** |
| quests, achievements, reputations, cooldowns | `quests`, `achievements*`, `reputations`, `dungeon_cooldowns` | **have** |
| who is online, and where | `WorldBridge.InWorldSessions()` / `SessionForPlayerId` | **have** (in memory, which is what group A wants) |
| **deleted characters** (restore) | — | **new**: soft-delete columns on `characters` (`deleted_at`, `deleted_by`) + `deleted_items` |
| **character restrictions / bans** | — | **new**: `restrictions(character_id, type, level, until, reason, set_at)` |
| **in-game announces** (scheduled + instant) | — | **new**: `announces(id, text, type, start_at, end_at, interval, enabled)` |
| **admin audit log** | — | **new**: `admin_log(at, source_ip, message, target, reason, result)` |
| **operator accounts for the tool** | — | **new**, or reuse `accounts.admin_level` (see §5) |
| account memos / watch list / tasks | — | **not needed** — those live in `WebAppDB_2800` and stay there |

Four new tables. Nothing in group A or B needs any of them; phase 1 is genuinely a read-only slice
of what already exists.

---

## 5. Login without Steer

`WebApp.dll` does its own cookie/session auth — `CheckLogin`, `CheckLoginAndRefreshCookie`,
`CheckLoginGetSessionKey`, `UpdateSessionKey`, `GetFunctionPrivilege` — and reaches Steer through
`SteerHelper` / `SteerConnectionManager` / `MakeSteerPacketToByte` (binary, to `127.0.0.1:8105`)
with `SendCheckLogIn`. The configured operator is `<Admin id="gadmin" />`.

Three ways to satisfy it, cheapest first:

1. **Stub Steer.** Write a ~100-line listener on 8105 that answers `SendCheckLogIn` with success for
   `gadmin` and whatever `GetFunctionPrivilege` needs. The packet shape has to come from a capture —
   point `SteerServer addr` at a logging proxy, click Login once, and read the two frames. This is
   the recommended path: it is the smallest surface, it leaves `WebApp.dll` untouched, and the
   capture is five minutes of work. **Not yet done — no Steer capture exists.**
2. **Patch the assembly.** `CheckLogin` is a named method in `WebApp.dll`; a dnSpy edit to return
   success unconditionally removes the dependency entirely. One-way, and it makes future tool
   updates painful, but it needs no protocol knowledge.
3. **Front it with IIS auth.** Put the tool behind Windows/basic auth and neuter only the Steer call.
   Same patching problem as (2) with an extra moving part.

Whichever we pick, the tool's *privileges* (`GetFunctionPrivilege`) also come back from that call, so
the stub must answer with a privilege mask wide enough for the pages we want. Expect to iterate once.

**Separately:** `accounts.admin_level` already exists in TeraSharp (T32), so once the tool is in, the
same value can gate which operations the Arbiter side accepts — the original has no such check
(§2), so this is an improvement worth making rather than a compatibility risk.

---

## 6. Phased plan

### Phase 1 — login + read-only lookups
*Goal: the tool opens, `gadmin` logs in, and Account/Users pages show real TeraSharp data.*

1. Capture the two Steer frames behind a proxy; build the 8105 stub that answers `SendCheckLogIn`
   for `gadmin` with a full privilege mask.
2. Add a `SharedDB` entry to `WebAppConfig.xml` pointing at a SQL Server view layer over TeraSharp's
   SQLite — or, simpler, teach the tool nothing and accept that character lookups come from the
   Arbiter channel in phase 2. **Decide this first; it is the fork in the road.**
3. Stand up a TCP listener on `listenPortForWebAdmin` in TeraSharp with the §2 framing and a dispatch
   table, answering exactly two messages: `WA_CHECK_LOGIN` and `WA_GET_USER_POSITION`, both from
   `WorldBridge.InWorldSessions()`. Everything else replies with a failure result rather than
   dropping, so the tool shows an error instead of hanging.
4. Settle the request-opcode numbering with the one-packet test in §2.
5. Create `WebAppDB_2800` from `WebAppDatabaseDefinition.xml` (23 tables, 105 procedures — it is a
   plain schema file, so this is a script, not a port).

**Exit criteria:** login works, the server list renders, a character lookup for an online player
returns name/level/zone/position.

### Phase 2 — live operations (no new tables)
`WA_FORCE_KICK`, `WA_FORCE_KICK_ACCOUNT`, `WA_INSTANT_INGAME_ANNOUNCE`. All three are group A/B —
they act on live sessions and need nothing persisted. Add the `admin_log` table here, because this is
the first phase where an operator changes something.

### Phase 3 — mutations
`WA_CHANGE_MONEY`, `WA_ADD_ITEM`, `WA_ADD/DEL_CHARACTER_RESTRICTION`. Needs the `restrictions` table
and the decision from §3 about offline targets. `WA_ADD_ITEM`'s 262-byte request carries a full item
option block that has to be mapped onto our `items` schema — budget a task for that alone.

### Phase 4 — restore and announces
`WA_DELETE_USER`, `WA_UNDELETE_USER` (soft-delete columns + `deleted_items`), `WA_ADD_INGAME_ANNOUNCE`
and `WA_REQUEST_RESERVED_ANNOUNCE` (`announces` table, and the boot load that the original does with
`spLoadIngameAnnounceInfo`). The tool's `RestoreCandidateData` / `RestoreItemMoney` /
`DeletedItemData` tables are the UI's side of this and need populating from our data.

### Out of scope
The remaining ~440 `WA_*` messages — festivals, gacha, dark rift, city war, battlefield, politics,
NPC spawn events, in-game store. TeraSharp has no subsystem behind any of them. The dispatch table
should answer them with a clean failure result so the tool reports "not supported" rather than
timing out.

---

## 7. Open questions

1. **Request opcode numbers.** Blocked on either the one-packet test (§2) or a dnSpy read of
   `PDL_Admin` in `WebApp.dll`. Everything else in phase 1 is ready.
2. **`SharedDB` or the Arbiter channel for lookups?** The tool was built to read game data over ODBC.
   Emulating SQL Server over SQLite is the larger job but unlocks dozens of read-only pages at once;
   going through 8081 means implementing a lookup message per page. Worth one experiment before
   committing.
3. **The Steer frame shape** — needs a live capture; nothing in the decompile describes it, and
   `WebApp.dll`'s Steer code is the only reference.
4. **`WA_DELETE_USER` offsets 4-13 are unread by the handler.** Minimum length 18 implies a field
   there (probably accountId u64 at 6); the handler only reads userId at 14. A capture would confirm.

---

## 8. T101 — our own admin web, phase 1

Sections 1–7 above are about making the **retail** `WebApp.dll` work (Steer stub, `WebAppDB_2800`,
the binary `WA_*` protocol on 8081). T101 takes a different road for the same features: a small
`HttpListener` **inside the Arbiter** serving one HTML page and JSON, with no Steer, no IIS and no
second database. Sections 1–7 stay as the record of the retail tool — this section is the
reimplementation, and it borrows the doc’s **feature list and result codes**, not its phase plan.

```
Web/AdminApi.cs      pure: routing + JSON over CharacterStore. No socket. Every test drives this.
Web/AdminServer.cs   the HttpListener shell + the static page. Holds no decisions.
```

* **127.0.0.1 only.** The prefix is the loopback literal, never `+` or `*`, and `Serve` re-checks
  `IPAddress.IsLoopback` in case anything is ever put in front.
* **Fails closed.** No `TERASHARP_ADMIN_TOKEN` means the tool does not start, and `AdminApi`
  answers 503 even to a caller who guessed the token. Port from `TERASHARP_ADMIN_PORT`, default
  **8050**. The token is compared length-first then constant-time.
* **Result codes are the retail ones** from section 2: `0` ok, `2` not found, `3` invalid,
  `0x16` refused.
* **Every write is audited** in the new `admin_log` table — and section 2 notes the original logs
  only a free-text reason and never stamps who did it, so this keeps the source IP and the result
  code as well.

### 8.1 What phase 1 serves

| endpoint | answers |
|---|---|
| `GET /` | the one page |
| `GET /api/accounts?q=` | accounts by name substring or id, each with its characters |
| `GET /api/character?id=` \| `?name=` | the row, money, guild, items, `deleteAt` |
| `GET /api/online` | `WorldBridge.InWorldSessions()`, through a delegate |
| `GET /api/admin-log?limit=` | the audit trail |
| `POST /api/restore-character` | clears a scheduled delete |

That is section 6’s phase 1 — "read-only lookups" — plus the restore the brief adds.

### 8.2 Restore is narrower than `WA_UNDELETE_USER`, and this matters

The retail message brings back a character whose **row is gone**. That needs the soft-delete
columns and the `deleted_items` table section 4 lists as new, and TeraSharp has neither.

What TeraSharp does have is `characters.delete_at` (T88) — a **scheduled** delete with a grace
window — and T88 deliberately left `OnDeleteUser` hard-deleting the row. So
`POST /api/restore-character` can only rescue a character *inside* its window; a truly deleted one
answers `2` (not found) rather than pretending. Closing that gap is phase 2 work with the new
tables, and it is the one place where this tool is visibly less capable than the original.

(The brief said "`delete_at` -> null"; the column is `INTEGER NOT NULL DEFAULT 0`, so the cleared
value is **0**, which is what T88’s `CancelCharacterDelete` already writes.)

### 8.3 Phase 2, and why those endpoints already exist as refusals

`set-money`, `set-level`, `give-item`, `ban`, `unban`, `kick`, `announce`, `gm-level` are routed
today and answer **501 with result `0x16`** rather than 404 — section 6’s out-of-scope note asks
for exactly that, so the page shows "not in this phase" instead of looking broken. They map to the
doc’s phases 2–3: `kick` and `announce` act on live sessions and need nothing persisted;
`set-money` / `give-item` are the `WA_CHANGE_MONEY` / `WA_ADD_ITEM` mutations (the latter’s
262-byte option block is still a task of its own); `ban` / `unban` need the `restrictions` table.

> **Superseded by section 9.** T101b implemented all eight, and closed 8.2's gap: the delete is a
> soft delete now, so restore *is* `WA_UNDELETE_USER`. What still refuses is phase 3.

## 9. T101b - phase 2

Eight endpoints stopped being refusals, and the delete stopped being a hard delete.

### 9.1 The endpoints

| endpoint | body | answers | retail |
|---|---|---|---|
| `POST /api/set-money` | `{id\|name, money, reason}` | `{result, oldMoney, newMoney}` | `WA_CHANGE_MONEY` |
| `POST /api/set-level` | `{id\|name, level, reason}` | `{result, message}`, 1..`MaxLevel` (70) | - |
| `POST /api/give-item` | `{id\|name, templateId, amount, reason}` | `{result, itemDbId, slot}` | `WA_ADD_ITEM` |
| `POST /api/ban` | `{id\|name, hours, reason}` | `{result, until}`, `hours` 0 = permanent | `WA_ADD_CHARACTER_RESTRICTION` |
| `POST /api/unban` | `{id\|name, reason}` | `{result, message}`, `2` when not banned | `WA_DEL_CHARACTER_RESTRICTION` |
| `POST /api/kick` | `{id, reason}` | `{result, message}`, `2` when not in world | `WA_FORCE_KICK` |
| `POST /api/announce` | `{text, reason}` | `{result, sent}` | `WA_INSTANT_INGAME_ANNOUNCE` |
| `POST /api/gm-level` | `{accountId, level, reason}` | `{result, message}` | - |
| `GET /api/deleted?limit=` | - | the characters waiting out the window | - |

Result codes are section 2's throughout: `0` ok, `2` not found, `3` invalid, `0x16` refused. Every
one of them, refusals included, writes an `admin_log` row.

`give-item` does **not** carry the 262-byte option block; section 6 budgets that separately. The
item lands in the first free slot of inventory 0.

`kick` and `announce` have nothing in the store behind them, so they go through two delegates the
wiring sets (`AdminApi.KickPlayer`, `AdminApi.Announce`). Unset, `kick` answers `2` rather than
reporting a disconnect that never happened.

### 9.2 The soft delete - what 8.2 said could not be done

`CharacterHandlers.OnDeleteUser` **schedules** now instead of dropping the row:

* the `characters` row stays, with `delete_at`, `deleted_at` and `deleted_by` stamped - which is
  what `S_GET_USER_LIST.deleteRemainSec` has been counting down since T76;
* the character's `items` rows move to `deleted_items` and come back on a restore;
* `CharacterStore.PurgeExpiredDeletes` hard-deletes past `deleteCharacterExpireHour2` (72 h, from
  `LoginHandlers`' S_GET_USER_LIST) - it needs a timer in `Program.cs`;
* `CancelCharacterDelete` (the client's `C_CANCEL_DELETE_USER`) and `POST /api/restore-character`
  both land on `RestoreDeletedCharacter`, so the two paths cannot drift.

One wrinkle left deliberately: `OnDeleteUser` still does `s.Account.Characters.Remove(chr)`, so the
character vanishes from the lobby list for the rest of that session and reappears with its
countdown on the next login. The real client is told the delete succeeded either way.

`DeleteCharacter` gained `DELETE FROM restrictions` in its children list - `restrictions.character_id`
references `characters(id)` and Microsoft.Data.Sqlite enforces foreign keys, so a purge of a banned
character would have failed with SQLite error 19 (the same way quests did on 2026-09-14).

### 9.3 What still refuses

`AdminApi.PhaseThreePaths` is down to three: `/api/bulk-mail`, `/api/event`, `/api/festival`.
Section 6's phase 3.

## 10. T106 - the page opens, and the console is readable again

### 10.1 The page is served without a token

T101 put `GET /` behind the same 401 as the data, which made the tool **unusable from a browser**:
typing `http://127.0.0.1:8050/` has nowhere to put an `Authorization` header, so the only way in
was curl. The page is now served before the token check (still behind `Enabled`, so an unset
`TERASHARP_ADMIN_TOKEN` serves nothing at all). It carries no data: it prompts for the token, keeps
it in that browser's localStorage, and sends it as **`X-Admin-Token`** on every API call - each of
which is still gated exactly as before. The listener binds 127.0.0.1 and `Serve()` re-checks the
peer is loopback, so serving a static shell costs nothing.

`AdminServer.Api` is now public. `TryStart` built the `AdminApi` inside the constructor call and
nothing ever got a reference back, so T101b's `KickPlayer` and `Announce` delegates were
unreachable from `Program.cs`.

### 10.2 Logging: two sinks, not one level

`Web/ArbiterLog.cs`. `Program.cs` asked for `LogLevel.Debug` on a console sink and `WorldBridge`
logs one line per W->A frame, so the console was unreadable on a live server.

* **Console** is `Warning` by default; `TERASHARP_LOG_LEVEL` overrides it with any
  Microsoft.Extensions.Logging level name, case-insensitive. Unset, empty or unparseable all fall
  back to Warning - a typo must not silently turn logging off.
* **File** takes everything from Debug up: `arbiter-yyyy-MM-dd.log` under `TERASHARP_LOGS`, rolled
  at midnight, opened `FileShare.ReadWrite` so it can be tailed while the server runs. A sink that
  throws would take the server with it, so a failed write disables the file and keeps going.
* The provider also keeps the last 2000 lines in memory, which is what the Status tab reads - the
  tail never touches the file and cannot collide with the writer.

The seven noisy pushes (`0x13FA`, `0x159A`, `0x1598`, `0x13CC`, `0x1562`, `0x1626`, `0x2927`) move
to Debug through `WorldReplayTable.QuietInLog` / `LogsAtDebug(op)`, which also absorbs the four
`WorldBridge` was suppressing with an inline literal list (`0x138A`, `0x15A8`, `0x1436`, `0x164D`).

**It is a separate set from `OneWayFromWorld` on purpose.** `0x1562`
(`SA_CLEAR_BATTLE_FIELD_ENTER_COUNT`) has a real `AS_` reply (0x1563) and a real handler - sealing
it as one-way to quieten it would break the battlefield counter. Quiet is not the same as one-way.

### 10.3 The Status tab

| endpoint | answers |
|---|---|
| `GET /api/status` | uptime, world links + ready, online count, working set, managed bytes, gc0/gc2, threads, the log file path and the console level |
| `GET /api/log?lines=N` | the newest N lines from the ring, oldest first (default 200, capped at 2000) |

The World figures come through an `AdminApi.WorldStatus` delegate, so this file still knows nothing
about `WorldBridge`; unwired it reports zero links and not-ready rather than pretending. The page
polls both every 2 s while the live tail is on, and keeps the tail pinned to the bottom unless the
reader has scrolled up.
