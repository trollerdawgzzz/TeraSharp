# Live-session checklist — everything merged since T19

One pass, ~30 minutes, **two characters on one account** (`dob` = playerId 1, the capture
character, and a second character you create in step 4). Work straight down: each step leaves the
server in the state the next one needs.

Watch two windows: **A** = the TeraSharp console, **W** = the WorldServer console
(`C:\TERA_SERVER.100\Executable\Logs\Console\WorldServerConsole_<date>_2800.log`). Log lines are
quoted as they appear with `{}` placeholders filled in.

The single most useful failure signature on the whole list: **`no replay for 0xNNNN`** (Debug
level, window A) immediately before the client or World goes quiet. It means a per-user request
reached neither a handler nor the replay table, and — if that opcode carries a DLM id — the user's
DB queue is now head-blocked for the life of the World process (`status/HANDOFF.md` §1).

---

## 0. Before you start — `--selftest`

```
TeraSharp.Arbiter.exe --selftest
```

Ten `[PASS]` lines and `selftest: 10/10 PASS - the deploy is complete`. Any `[FAIL]` names the
file and the path it looked at — fix that before starting WorldServer. `[WARN]` lines
(`spawn replay`, `Datasheet folder`) are informational: the server runs without them.

| checks | why it is on the list |
|---|---|
| `opcode map`, `packet defs` | a wrong `TERASHARP_DATA` is otherwise only visible as "no def for S_…" at login |
| `starter blob`, `starter inventory` | a short copy makes character creation and the first inventory silently wrong |
| `promotion records`, `handshake burst` | both are load-bearing for a FRESH World (steps 1-2) |
| `world replay log` | without `arb_world.log` the replay table is empty and half the login sequence disappears |
| `DB schema` | a `terasharp.db` from before T30/T32 is missing `friend_groups`, `friends.memo`, `accounts.admin_level` |

## 1. Start TeraSharp, then WorldServer — the handshake

**Do:** start TeraSharp, then WorldServer. Do not touch the client yet.

| window | line |
|---|---|
| A | `TeraSharp Arbiter starting (protocol 376012, patch 100)` |
| A | `Auth provider: accept-all` (or `tera-api` — step 3) |
| A | `Loaded {N} opcodes for protocol 376012` and `Loaded 4548 schema defs ({N} distinct packets), skipped 1` |
| A | `WorldBridge listening on 127.0.0.1:{Port} for WorldServer` |
| A | `WorldServer link #1 connected (1 active)` |
| A | `WorldServer handshake complete - READY for players` |
| A | `Post-handshake: sent 63 config pushes (0x15BD = {now}, 0x14D1 daily reset = {reset})` |
| A | `0x147D: sent 23 x 0x147E promotion records stamped {now}` |
| A | `0x13F2: echoed {N} dungeon-timeline states back as 0x1581 (fallback burst also sent …/disabled)` |
| W | one `… open` line per dungeon id (DungeonManager), then World idles |

**Failures**

* `Post-handshake: data/handshake_burst.bin missing or malformed - the 63-push config burst was NOT sent` → step 0 lied to you or the deploy copied the exe without `data\`.
* `0x147D: promotions_147E.bin missing or malformed - falling back to replay (stale timestamps)` → the level-1 crash is back: a new character entering a fresh World dies in `PromotionController::NewPromotion`.
* `0x13F2: malformed open-info list ({N}-byte payload) - not echoed` → World changed the push shape; the fallback burst still covers it, but say so before blaming anything else.
* No `READY for players` → the client will get `C_SELECT_USER while World not ready - rejecting`.

## 2. Log in (auth)

**Do:** launcher → client → login screen.

| window | line |
|---|---|
| A | `C_LOGIN_ARBITER: account '1' (id 1) language 6, {N} character(s)` |
| A | `C_GET_USER_LIST -> {N} character(s)` |

**Failure:** `C_LOGIN_ARBITER: account '1' rejected by {provider}: {code} {msg}` — with
`accept-all` this cannot happen, so it means `TERASHARP_AUTH` is set. Codes:
`50011 authkey mismatch` (stale launcher ticket — log in through the launcher again),
`50000 account does not exist`, `50012 account banned`, `-1 tera-api unreachable`.
Full flow: `status/AUTH-DESIGN.md`.

> **Auth pass (optional, 3 min).** Stop TeraSharp, restart with `TERASHARP_AUTH=true`, log in
> through the launcher: expect the same success lines plus `Auth provider: tera-api`. Then edit
> the ticket (or log in twice from the launcher so the first ticket goes stale) and expect
> `rejected by tera-api: 50011 authkey mismatch` and the client stuck at the login screen. Put it
> back to accept-all before continuing.

## 3. Select `dob` — enter world, blob, gameId

**Do:** pick `dob`, enter the world.

| window | line |
|---|---|
| A | `C_SELECT_USER: entering world as 'dob' (gameId {G})` |
| A | `Handed session {Id} to WorldServer (gameId {G}, char 'dob')` |
| A | `SDB_USER_ENTERWORLD` … then the login loads below |
| A | `C_LOAD_TOPO_FIN -> spawning 'dob' at ({X},{Y},{Z}) in zone {Z}` |
| W | `EnterWorld [1] dob(1)` |

**Failures:** `SDB_USER_ENTERWORLD: player {Id} has no world blob (found=…) - replying not-found`
(the character never entered, or the blob column is empty); `Session {Id}: SA_LEAVE_WORLD not
received in 5s - forcing delete` at logout (see step 11); a silent client after
`DBS_USER_ENTERWORLD` on a **fresh** World is the promotion-record crash from step 1.

## 4. The login loads — achievements, reputation, fatigability, tips, seren, cool times

These all fire inside step 3, in this order. Tick them off in window A:

| line | feature |
|---|---|
| `SDB_LOAD_USER_ACHIEVEMENT: player 1 -> {what}, {N} accomplished achievement(s)` | T22 achievements |
| `SDB_LOAD_TUTORIAL_SIMPLE_TIP: player 1 -> {N} tip(s)` | T22 tips |
| `SDB_INIT_SEREN_GUIDE_INFO: player 1 -> {N} stored slot(s)` | T22 seren |
| `SDB_LOAD_REPUTATION_LIST: player 1 -> {N} reputation(s)` | T26 reputation |
| `SDB_LOAD_FATIGABILITY_LIST: account 1 -> {P} fatigue point(s)` | T26 fatigability (per ACCOUNT) |
| `SDB_LOAD_DUNGEON_COOL_TIME: player 1 -> {N} cool time(s)` | T25 cool times |
| `SDB_LOAD_QUEST_LIST: player 1 -> {N} active quest(s), {C} completed` | T17/T21 quests |
| `SDB_USER_LOAD_INVENTORY: player {Pid} -> {N} starter items for class {Cls} ({Name})` | inventory rows (new characters only) |
| W | `LoadFatigability …` |

**Failures:** any of these missing → look for `no replay for 0x27F8 / 0x288F / 0x2908 / 0x2867`.
`SDB_USER_LOAD_INVENTORY: starter_inventory.bin not found - falling back to replay` plus
`EnterWorld Failed [..] [{name}] [1]` in W = World rejected the inventory because the item owner
is dob's playerId, not this character's.

## 5. Client settings

**Do:** open Options, change one chat-window setting and one UI setting, close the client's option
window. Then `/logout` to the lobby and back in (step 11 is the full relog; a quick one is enough
here).

**Proof:** the settings are still there after the relog. The handlers are deliberately silent —
there is no arbiter line for a successful save. What you are looking for is the **absence** of
`SaveClientSetting: refusing a {N}-byte blob for character {Id}`; that warning means the client
sent 0 bytes or more than the cap, and the row was not written.

## 6. Play for five minutes — the per-user writes

**Do:** kill a few mobs, pick up loot, complete one quest step, learn a skill if you can, level up
if you can.

| line | feature |
|---|---|
| `S_UPDATE_EXP_LEVEL: player 1 level {L}, exp {E} (rest {R})` | level/exp on the row |
| `SDB_ITEM_SINGLE: echoed {N} transaction atom(s) for player 1` | inventory writes |
| `SDB_SET_QUEST_INFO` … then `SDB_LOAD_QUEST_LIST` on the next login | quests |
| `SDB_ACCOMPLISH_USER_ACHIEVEMENT: player 1 offered {O}, {N} newly accomplished` | achievements |
| `SDB_UPDATE_USER_ACHIEVEMENT: stored {N} B for player 1` | achievements (on zone change/logout) |
| `SDB_ADD_TUTORIAL_SIMPLE_TIP: player 1 saw tip {T}` | tips |
| `SDB_UPDATE_REPUTATION_INFO: player 1 reputation {R} (op {Op})` | reputation (needs a faction action) |
| `SDB_UPDATE_USER_DATA: player 1 blob pos {Pos}` | the world blob |

**Failures:** `SDB_SET_QUEST_INFO: bad quest record … - acking without storing`;
`SDB_UPDATE_USER_DATA: unexpected blob (off …) for player 1 - not saved` — the blob did not
persist, so the next login restores the old position.

## 7. A dungeon — cool times and the enter-world fallback

**Do:** enter any instanced dungeon, then **log out while still inside it**, then log back in.

| window | line |
|---|---|
| A | `SA_REQUEST_ENTER_DUNGEON: player 1 -> dungeon/zone {Dg} ({N} B)` |
| A | `Player 1 is in dungeon {Dg}, instance 0x{Pd:X8}` |
| A | `SA_UPDATE_DUNGEON_COOLTIME: player 1 dungeon {Dg} cool time stored` |
| A (on the relog) | `SA_ENTER_WORLD_FAIL: World refused enter-world - gameId 0x{G:X}, ticket {T}, …` |
| A | then a second `AS_ENTER_WORLD` and a normal spawn at the stored return point |

This is the T21 path and it has **never been live-verified** — it is the highest-value item on
this list.

**Failures:**
* `EnterWorld retry for '{Name}': no stored return point for continent {C}` → the 0x13BE that took you into the dungeon never stored one; you land in the fallback continent instead.
* `SA_ENTER_WORLD_FAIL: ResendEnterWorld is not wired, so no AS_ENTER_WORLD retry` → a Program.cs regression (the hook block).
* `SA_ENTER_WORLD_FAIL: reason {R} is outside 1-3` → a failure the real Arbiter does not retry either; read the reason before retrying by hand.
* `{What}: no live session owns gameId 0x{G:X} - not stored` → the cool time was dropped because the session had already gone.

## 8. Friends, groups, memos, blocks

**Do:** open the Friends panel. With one account you can exercise everything except an actual
friendship (the Arbiter refuses two characters of one account — that refusal is itself a test):

1. Friends panel opens → the three login lists were accepted by the client.
2. `/@`-free: add your own second character as a friend → refused.
3. Create a friend group, rename it, delete it.
4. Block a name, edit its memo, unblock.

| window | line |
|---|---|
| A | `C_ADD_FRIEND: dob -> {Target} refused (CannotAddSelf)` (same account) |
| A | `C_ADD_FRIEND_GROUP: dob group {Id}` / `C_DELETE_FRIEND_GROUP` |
| A | `C_BLOCK_USER: dob -> {Target}` then `C_REMOVE_BLOCKED_USER: dob x {Target}` |

**Proof the lists are right:** the panel shows the seeded group 好友 and your profile message
(今天也是愉快的一天!) on a character that has never touched the panel. An empty or garbled panel
means the def override did not load — see `status/FRIENDS.md` §2.

> **Two-account pass (optional, 5 min).** Log a second client in as account `2` (accept-all takes
> any name), create a character, then: add friend → accept → whisper → delete. Expect
> `C_ADD_FRIEND: {A} -> {B} (request)`, `C_ACCEPT_FRIEND: {B} accepted {A}`,
> `Whisper: {A} -> {B}`, `C_DELETE_FRIEND: {A} x {B} (was type 0)`. Everything cross-session is
> listed in `status/MULTIPLAYER-DESIGN.md`.

## 9. GM commands

**Do:** restart TeraSharp with `TERASHARP_GM_ACCOUNTS=1` (the account name you log in with), log
in, and type `/@query_point`, then `/@warehousegold_max 100`, then `/@nonsense_command`.

| window | line |
|---|---|
| A | `C_ADMIN from account '1' level 5: query_point -> Local` |
| client | `Can't request coin` (the real Arbiter's own reply — there is no billing service) |
| A | `C_ADMIN from account '1' level 5: warehousegold_max 100 -> Local` |
| A | `C_ADMIN from account '1' level 5: nonsense_command -> Unknown` → client shows `Invalid QA Command` |
| A | `C_ADMIN from account '1' level 5: {world command} -> ForwardToWorld` for anything in the World catalogue |

**Failures:** nothing happens at all and there is no `C_ADMIN from account` line → the client is
not in QA mode; `S_LOGIN_ARBITER.status` must be 31 or 33 for a GM login
(`status/GM-DESIGN.md` §6), and that one-line change may not be in yet. A line ending
`-> NotAuthorised` means the account is not in `TERASHARP_GM_ACCOUNTS` — and note the client gets
**nothing** back, which is exactly what the real Arbiter does.

Then `/@set_admin_level {yourOtherCharacter} 5` and check the `accounts.admin_level` row —
that is the only GM command that writes to the DB.

## 10. The second character

**Do:** back to the lobby, create a new character (any class), enter the world with it.

This re-runs steps 3-5 on a character with **no** history, which is the case that broke most
often:

| line | what it proves |
|---|---|
| `C_CREATE_USER from {Id}: created '{Name}' id={N} template={T} … (identity patched into the blob)` | creation + blob identity |
| `SDB_USER_LOAD_INVENTORY: player {N} -> {M} starter items for class {C} ({Name})` | the class kit, not dob's |
| `SDB_LOAD_USER_ACHIEVEMENT: player {N} -> brand-new-character reply, 0 accomplished achievement(s)` | no captured statics leaked |
| `SDB_LOAD_FATIGABILITY_LIST: account 1 -> {P} fatigue point(s)` | the SAME total as dob — it is per account |
| W | `EnterWorld [1] {name}({N})` |

**Failure:** anything that shows dob's data for the new character (a full achievement list, dob's
quests, dob's inventory) is the "captured per-character data served to another character" bug
class — `status/STATUS.md` rule 3.

## 11. Logout, relog, and the DLM check

**Do:** Logout button → lobby → select the other character → enter → Exit.

| window | line |
|---|---|
| A | `C_RETURN_TO_LOBBY from {Id} - starting lobby-return` |
| A | `Sent AS_USER_REQUEST_EXIT (0x14FF) for player {N}` |
| A | `Sent AS_CANCEL_SKILL_STRICTLY (0x1460) + AS_LEAVE_WORLD (0x1392) gameId={G:X} type=3 reason=0` |
| A | `SDB_UPDATE_FATIGABILITY_POINT: account 1 +{D} fatigue -> {Total}` (the logout write) |
| A | `Session {Id}: sent S_RETURN_TO_LOBBY` |

**The failure that matters:** `Session {Id}: SA_LEAVE_WORLD not received in 5s - forcing delete`
followed by `Session {Id}: forcing AS_ARBITER_USER_DELETE`. That is a **DLM head-block**: some
earlier per-user reply never arrived or carried a stale id, so `UserLeaveWorld` never reached the
head of World's queue. Scroll back for the last `no replay for 0x….` before it — that opcode is
the culprit. The next `C_SELECT_USER` will then stall at `0x1626` because World still holds the
character.

## 12. Party (merged alongside, not in the T19-T37 list)

If two clients are up: invite, accept, leave. Window A shows
`Party 0x{Id:X} created: {A} + {B}`, `Party 0x{Id:X}: {B} joined ({N} members)`,
`Party 0x{Id:X} {why}` on dissolve.

---

## Quick reference — the six lines that mean "stop and read"

1. `no replay for 0xNNNN` — an unanswered per-user request; the next logout will hang.
2. `SA_LEAVE_WORLD not received in 5s` — the head-block already happened.
3. `promotions_147E.bin missing or malformed` — a fresh World will crash on a level-1 character.
4. `the 63-push config burst was NOT sent` — first enter-world into a fresh World gets dropped.
5. `DbProxy: {Op} is allow-listed but has no handler` — a dispatch/allow-list mismatch shipped.
6. `has no world blob (found=…) - replying not-found` — the character's state is gone, not merely stale.
