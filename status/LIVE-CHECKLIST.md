# Live-session checklist — everything merged through T51

Two accounts, two clients, ~45 minutes. Sections 1-4 are the regression pass over what is already
live-proven; sections 5-11 are the six things merged since the last live session that have **never
run against a real client**. If time is short, do 0, 1, then jump to 5.

Watch two windows: **A** = the TeraSharp console, **W** = the WorldServer console
(`C:\TERA_SERVER.100\Executable\Logs\Console\WorldServerConsole_<date>_2800.log`). Log lines are
quoted as they appear with `{}` placeholders filled in. TeraSharp's default level is Debug; the
guild and party steps below need it, so do not raise it to Information for this pass.

The single most useful failure signature on the whole list: **`no replay for 0xNNNN`** (Debug
level, window A) immediately before the client or World goes quiet. It means a per-user request
reached neither a handler nor the replay table, and — if that opcode carries a DLM id — the user's
DB queue is now head-blocked for the life of the World process (`status/HANDOFF.md` §1).

**Environment for this pass:** `TERASHARP_GM_ACCOUNTS=<account A>` must be set (sections 5 and 9
both need it), and `deploy.ps1` has it commented out by default.

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
| `promotion records`, `handshake burst` | both are load-bearing for a FRESH World (section 1) |
| `world replay log` | without `arb_world.log` the replay table is empty and half the login sequence disappears |
| `DB schema` | 20 tables and 8 late-migration columns; a `terasharp.db` from before T42 is missing `items`, `warehouses`, `parcels` |

`SelfTest.RequiredTables` does **not** yet list `guilds`, `guild_members` or `visited_sections`
(T39, T45), so a database older than those migrations passes `--selftest` and then fails at
runtime. If the guild step in section 9 produces a SQLite error rather than a guild, that is why:
delete `terasharp.db` and let it be recreated.

---

## 1. Regression pass — proven on 2026-09-14/15, confirm it still holds

Everything in this section has been seen working live. Do not spend time on it beyond the lines
below; if one of them is missing, stop and fix that before going near sections 5-11.

**Do:** start TeraSharp, then WorldServer, then log in account A and enter the world with an
existing character.

| window | line | what it proves |
|---|---|---|
| A | `WorldServer handshake complete - READY for players` | handshake |
| A | `Post-handshake: sent 63 config pushes (0x15BD = {now}, 0x14D1 daily reset = {reset})` | the config burst |
| A | `0x147D: sent 23 x 0x147E promotion records stamped {now}` | a level-1 character will not crash a fresh World |
| A | `C_LOGIN_ARBITER: account '{Acct}' (id {Id}) language 6, {N} character(s)` | auth (accept-all) |
| A | `C_SELECT_USER: entering world as '{Name}' (gameId {G})` | select |
| A | `Handed session {Id} to WorldServer (gameId {G}, char '{Name}')` | AS_ENTER_WORLD built from rows |
| A | `C_LOAD_TOPO_FIN -> spawning '{Name}' at ({X},{Y},{Z}) in zone {Z}` | spawn at the stored position |
| W | `EnterWorld [1] {name}({id})` | World accepted it |

Then, in one five-minute run: kill a few mobs, take loot, advance a quest, change a keybind, use
the Island of Dawn teleport, Exit, and log back in. These are the lines that say the persistence
layer is still intact:

| line | feature |
|---|---|
| `S_UPDATE_EXP_LEVEL: player {Pid} level {L}, exp {E} (rest {R})` | level/exp on the row |
| `SDB_ITEM_SINGLE: echoed {N} transaction atom(s) for player {Pid}` | inventory writes |
| `SDB_UPDATE_USER_DATA: player {Pid} blob pos {Pos}` | the world blob |
| `SDB_LOAD_QUEST_LIST: player {Pid} -> {N} active quest(s), {C} completed` | quests, on the relog |
| `SDB_LOAD_USER_ACHIEVEMENT: player {Pid} -> {what}, {N} accomplished achievement(s)` | achievements |
| `SDB_LOAD_DUNGEON_COOL_TIME: player {Pid} -> {N} cool time(s)` | cool times |
| `SDB_LOAD_FATIGABILITY_LIST: account {Acct} -> {P} fatigue point(s)` | fatigability, per ACCOUNT |
| `SA_ENTER_WORLD_FAIL: World refused enter-world - gameId 0x{G:X}, ticket {T}, …` then a second `AS_ENTER_WORLD` | relog into a dead instance (T21) |

**Failures that end the session here**

* `Post-handshake: data/handshake_burst.bin missing or malformed - the 63-push config burst was NOT sent` → the deploy copied the exe without `data\`.
* `0x147D: promotions_147E.bin missing or malformed - falling back to replay (stale timestamps)` → a level-1 character entering a fresh World will die in `PromotionController::NewPromotion`.
* `SDB_USER_ENTERWORLD: player {Id} has no world blob (found=…) - replying not-found` → the character's state is gone, not merely stale.
* `Session {Id}: SA_LEAVE_WORLD not received in 5s - forcing delete` at logout → a DLM head-block; see section 11.

> **Auth pass (optional, 3 min).** Restart with `TERASHARP_AUTH=true` and log in through the
> launcher: expect `Auth provider: tera-api` and the same success lines. Log in twice from the
> launcher so the first ticket goes stale and expect
> `C_LOGIN_ARBITER: account '{Acct}' rejected by tera-api: 50011 authkey mismatch`.
> Put it back to accept-all before continuing. Full flow: `status/AUTH-DESIGN.md`.

---

## 2. Both clients in world

**Do:** with account A still in world, log account B in on the second client, same zone, in view
of A. This is the multiplayer milestone and it is proven — the point here is to get to the state
sections 7-9 need.

| window | line |
|---|---|
| A | a second `C_SELECT_USER: entering world as '{B}' (gameId {G2})` with a **different** gameId |
| A | `Handed session {Id2} to WorldServer (gameId {G2}, char '{B}')` |
| W | `EnterWorld [1] {b}({idB})` |

**Proof:** each client can see the other character move, and `/say` from one appears in the
other's chat window.

**Failure:** B enters but A's client freezes, or A sees nothing of B. That is the tunnel routing
(ticket allocation / N-recipient `SA_BYPASS_TO_CLIENT`) and it means
`status/MULTIPLAYER-DESIGN.md` §6 regressed. The tell in window A is a burst of
`Tunnel: dropped frame for unknown ticket {T}` while two sessions are in world.

---

## 3. A brand-new character

**Do:** on account B, back to the lobby, create a character of a class you have not used, enter
the world with it.

| line | what it proves |
|---|---|
| `C_CREATE_USER from {Id}: created '{Name}' id={N} template={T} … (identity patched into the blob)` | creation + blob identity |
| `SDB_USER_LOAD_INVENTORY: player {N} -> {M} starter items for class {C} ({Name})` | the class kit, not dob's |
| `SDB_LOAD_USER_ACHIEVEMENT: player {N} -> brand-new-character reply, 0 accomplished achievement(s)` | no captured statics leaked |

**Failure:** anything that shows dob's data for the new character (a full achievement list, dob's
quests, dob's inventory) is the "captured per-character data served to another character" bug
class. `EnterWorld Failed [..] [{name}] [1]` in W = World rejected the inventory because the item
owner is dob's playerId, not this character's.

---

## 4. Friends, groups, memos, blocks

**Do:** open the Friends panel on A. Create a friend group, rename it, delete it; block a name,
edit its memo, unblock; try to add your own other character (it must be refused). Then, with both
clients up, A adds B, B accepts, A deletes.

| window | line |
|---|---|
| A | `C_ADD_FRIEND: {A} -> {Target} refused (CannotAddSelf)` (same account) |
| A | `C_ADD_FRIEND: {A} -> {B} (request)` then `C_ACCEPT_FRIEND: {B} accepted {A}` |
| A | `C_ADD_FRIEND_GROUP: {A} group {Id}` / `C_DELETE_FRIEND_GROUP` |
| A | `C_BLOCK_USER: {A} -> {Target}` then `C_REMOVE_BLOCKED_USER: {A} x {Target}` |
| A | `C_DELETE_FRIEND: {A} x {B} (was type {Type})` |

**Failure:** an empty or garbled panel means the def override did not load —
`status/FRIENDS.md` §2. Note that the seeded group name and profile message are now
language-aware (T45): an EUR client (`language 6`) gets `Friends` and an empty profile message,
not the Taiwanese capture's 好友.

---

# The six unproven things — everything below here is new since the last live session

---

## 5. Potions decrement on screen  ← **start here if time is short**

This is the one known live break. `C_SHOW_ITEM_TOOLTIP_EX` (30152 / `0x75C8`) fires once per
potion use; the amount reaches the store either way, but without the Arbiter's
`S_SHOW_ITEM_TOOLTIP` reply the client never refreshes the count. T45 made TeraSharp answer it
and stopped forwarding it to World.

**Do:** use a stackable potion three times, watching the count in the quickslot.

| window | line |
|---|---|
| A | `SDB_ITEM_SINGLE: echoed {N} transaction atom(s) for player {Pid}` — once per use |
| client | the count goes 5 → 4 → 3 **on screen, without relogging** |

**Failures**

* `W` prints `handler has not been implemented yet!!! 30152 {len}` → the packet is still being forwarded; `ArbiterClientHandlers.ArbiterOwned` or the `C_SHOW_ITEM_TOOLTIP_EX` registration did not ship.
* `A` prints `30152 (0x75C8) is Arbiter-owned and has no handler - dropped, NOT forwarded` → the deny-list shipped but the handler registration did not. This is the half-applied state.
* `A` prints `C_SHOW_ITEM_TOOLTIP_EX: {Len} B body (want 38)` → the client's fixed part is not 0x26 on this build; re-derive it from the PDL dumper before touching anything else.
* `A` (Debug) prints `C_SHOW_ITEM_TOOLTIP_EX: item {Item} not owned by {Owner} - no reply` for **your own** potion → the item db id the client names is not the one in the `items` row; the atom application allocated a different id.
* The count only updates after a relog → the row is right and the reply is wrong; compare the emitted `S_SHOW_ITEM_TOOLTIP` against `status/CLIENT-REJECTS.md` §2.

Same step covers the other Arbiter-owned client packets T45 added: the World console must print
**no** `handler has not been implemented yet!!!` lines at all for 43407, 61398, 39349 or 46956
during this session.

## 6. The mailbox is empty, not twelve blank rows

Before T45 nothing answered `SDB_LIST_PARCEL` (0x2777), so the replay table handed every character
dob's captured list and the mailbox rendered 12 phantom rows and a `00`.

**Do:** open the mailbox on a character that has never received mail.

| window | line |
|---|---|
| A | `SDB_LIST_PARCEL: user {Pid} view {View} page {Page} -> 0 parcel(s)` |
| client | an empty mailbox — no rows at all |

**Failures**

* `no replay for 0x2777` → the allow-list entry did not ship, and that character's DB queue is now head-blocked: the next logout will hang (section 11).
* `SDB_LIST_PARCEL: {Len} B payload (want {Want}) or no store - empty inbox` (Warning) → the request shape is not what `ParcelDbHandlers.ListRequestSize` expects. The mailbox is still empty, so the client looks right, but the layout is wrong and MAKE/RECV will be wrong too.
* Twelve rows still appear → the replay table answered first; `DbProxyHandlers.IsHandledRequest` does not contain 0x2777 in the binary that is running.

## 7. Whisper between the two clients

Whisper now goes through `ChatManager` and resolves the recipient through
`WorldBridge.SessionForPlayerId` (T43 built it, T47 wired the roster). Before that it always said
"offline" with two players in world.

**Do:** A whispers B, B whispers back. Then A whispers a name that does not exist. Then B blocks A
and A whispers again.

| window | line |
|---|---|
| A | `Whisper from {A} delivered (1 client packet(s))` |
| A | `Whisper from {B} delivered (1 client packet(s))` |
| A | `C_WHISPER from {A}: {Why} ({N} in world)` for the three refusal cases |
| client | the text appears in the other client's whisper tab, with the sender's name |

**Failures**

* `C_WHISPER from {A}: … (0 in world)` while both clients are clearly in world → the chat roster is not being registered; `SocialHandlers.RegisterChat` is called from `WorldEntry` and from `GameSession`, and one of those two call sites is missing.
* `Whisper from {A} delivered (0 client packet(s))` → the recipient resolved but the session lookup returned nothing; window A also shows `chat: S_WHISPER for {To} dropped - no session` at Debug.
* Delivered but nothing renders in the client → `S_LOAD_CLIENT_USER_SETTING` never reached that client, so the chat window has no tabs configured. It is sent at `C_LOAD_TOPO_FIN`, not at select.

## 8. Party: invite, accept, leave

`PartyManager` (T35/T49) is wired through `PartyWiring`: 7 client opcodes registered, 12 `SA_`
opcodes gated out of the tunnel in `WorldBridge.HandleFrame`.

**Do:** A invites B, B accepts, both send party chat, A changes the looting method, B leaves.

| window | line |
|---|---|
| A | `Party 0x{Id:X} created: {A} + {B}` |
| A | `Party 0x{Id:X}: {B} joined ({N} members)` |
| A | `Party 0x{Id:X} {why}` on leave/dissolve |
| client | the party frame appears on both clients with both names and levels |

**Failures**

* Nothing at all in window A when A clicks invite → the `PartyWiring.ClientOpcodes` loop in `HandlerRegistry` did not ship; the packet is being forwarded and `W` prints `handler has not been implemented yet!!!`.
* A party packet reaches World and the World link drops → the `HandleFrame` gate did not ship; `status/PARTY-DESIGN.md` §11.5 has the exact line.
* Window A (Debug) prints `party: {reason}` — e.g. `C_APPLY_PARTY: player {id} is not online`, `C_APPLY_PARTY: applicant is already in a party` — and the client shows a system message. That is the designed refusal path, not a bug; the four `SA_` opcodes with no case yet (`0x139A`, `0x139C`, `0x13AB`, `0x13AC`) log and send nothing, per `status/PARTY-DESIGN.md` §11.6.
* Party chat works but the roster frame is empty → the client packet built, the member list did not; compare against `status/PARTY-DESIGN.md` §6.

## 9. Guild: create, invite, accept, announce

17 client guild packets are registered through `GuildWiring` (T51), the guild rows persist, and
`SDB_INIT_GUILD` (0x27CF) rebuilds World's guild table from those rows at boot.

**Note before you start:** guild handlers do **not** log at Information — there is no
`C_CREATE_GUILD: …` line to look for. Run at Debug and use the client plus the two proofs below.
(Adding an Information line per guild command is the obvious follow-up; it is the only subsystem
on this list you cannot watch from the console.)

**Do:** A creates a guild (needs the gold and level the client enforces), invites B, B accepts, A
sets the announcement, A opens the guild window.

| window | line / proof |
|---|---|
| client | the guild window renders with the name, the announcement, and both members |
| A (Debug) | `guild: DispatchResult(…)` lines only when something was dropped — silence here is success |
| DB | a row in `guilds` and two in `guild_members` |
| A, **after restarting WorldServer** | `SDB_INIT_GUILD: sent {N} frame(s) for 1 guild(s)` |

That last line is the real proof: it means the guild survived a World restart and was rebuilt from
rows rather than replayed from `arb_world.log`.

**Failures**

* `guild: 0x{Op:X4} dropped - no store open` → `Program.Store` was null when the packet arrived.
* `guild: World did not accept 0x{Op:X4} ({Len} B payload)` → a guild frame was rejected by World; this is the highest-risk area in the whole document because `SDB_CREATE_GUILD2`'s fixed part has never been seen on a tap (`status/GUILD-DESIGN.md` §8.1) and a wrong size kills the link.
* `guild: {Packet} for {To} dropped - no session` for an **online** member → the recipient lookup is wrong; for offline members this line is normal and expected.
* `SDB_INIT_GUILD: sent {N} frame(s) for 0 guild(s)` after a restart, with a guild in the DB → the boot load is not reading the rows.
* The guild window opens empty → `S_GUILD_INFO` / `S_GUILD_MEMBER_LIST` layout; 31 unaligned fields, `status/GUILD-DESIGN.md` §8.2.

## 10. GM: `/@teleport` and `AdminLevel[5]` in World

Two separate things that both have to be true before any of the 416 World GM commands work:
the Arbiter must classify the command as `ForwardToWorld` (T47), and World must have been told
the account's admin level in `AS_ENTER_WORLD[111]` (T46).

**Do:** with `TERASHARP_GM_ACCOUNTS=<account A>` set, log in as A and enter the world. Then type
`/@query_point`, `/@teleport {B's character name}`, and `/@nonsense_command`.

| window | line |
|---|---|
| W | `AdminLevel[5]` in the enter-world line for A — **not** `AdminLevel[0]` |
| A | `C_ADMIN from account '{A}' level 5: query_point -> Local` |
| client | `Can't request coin` (the real Arbiter's own reply — there is no billing service) |
| A | `C_ADMIN from account '{A}' level 5: teleport {name} -> ForwardToWorld` |
| client | A is standing next to B |
| A | `C_ADMIN from account '{A}' level 5: nonsense_command -> ForwardToWorld`, then World refuses it |

**Failures**

* `W` prints `AdminLevel[0]` → the env value matches neither the account name nor the character name. `GmCommandHandlers.LevelOf` matches both, so check the spelling of what you actually typed at the character screen.
* `A` prints `… level 0: teleport … -> NotAuthorised` and the client gets **nothing back** → same cause, seen from the Arbiter side. The silence is correct: the real Arbiter logs abuse and answers nothing.
* `A` prints `… -> Unknown` → a stale binary. `Classify` has forwarded by default since T47; `Unknown` is no longer produced.
* `A` prints `… -> ForwardToWorld` but nothing happens in game → the command reached World and World refused it. Check `AdminLevel` first, then the command's own arguments; `status/GM-COMMANDS-FULL.md` has all 608 names.
* No `C_ADMIN from account` line at all → the client is not in QA mode. `S_LOGIN_ARBITER.status` must be 31 or 33; 31 was confirmed live on 2026-09-14 (`status/GM-DESIGN.md` §6).

Finish with `/@set_admin_level {B's character} 5` and check the `accounts.admin_level` row — it is
the only GM command that writes to the DB:

| window | line |
|---|---|
| A | `GM set_admin_level: '{Target}' (account {Acct}) -> level 5 by '{A}'` (Warning level, deliberately) |

---

## 11. Logout, relog, and the DLM check

**Do:** A logs out to the lobby while B stays in world; A selects the other character and enters;
then A exits the client.

| window | line |
|---|---|
| A | `C_RETURN_TO_LOBBY from {Id} - starting lobby-return` |
| A | `Sent AS_USER_REQUEST_EXIT (0x14FF) for player {N}` |
| A | `Sent AS_CANCEL_SKILL_STRICTLY (0x1460) + AS_LEAVE_WORLD (0x1392) gameId={G:X} type=3 reason=0` |
| A | `SA_LEAVE_WORLD (0x1393) -> AS_ARBITER_USER_DELETE (0x1433) for gameId {G:X}` |
| A | `Session {Id}: sent S_RETURN_TO_LOBBY` |

**Proof B was not disturbed:** B's client keeps rendering the world throughout, and window A shows
no `WorldServer link #{Id} closed` line.

**The failure that matters:** `Session {Id}: SA_LEAVE_WORLD not received in 5s - forcing delete`
followed by `Session {Id}: forcing AS_ARBITER_USER_DELETE`. That is a **DLM head-block**: some
earlier per-user reply never arrived or carried a stale id, so `UserLeaveWorld` never reached the
head of World's queue. Scroll back for the last `no replay for 0x….` before it — that opcode is
the culprit. The next `C_SELECT_USER` will then stall at `0x1626` because World still holds the
character.

---

## Quick reference — the seven lines that mean "stop and read"

1. `no replay for 0xNNNN` — an unanswered per-user request; the next logout will hang.
2. `SA_LEAVE_WORLD not received in 5s` — the head-block already happened.
3. `{Opcode} (0x{Opcode:X4}) is Arbiter-owned and has no handler - dropped, NOT forwarded` — a half-applied T45; the client is waiting for a reply nobody will send.
4. `guild: World did not accept 0x{Op:X4}` — a guild frame World rejected; the link is one wrong size away from dying.
5. `promotions_147E.bin missing or malformed` — a fresh World will crash on a level-1 character.
6. `the 63-push config burst was NOT sent` — first enter-world into a fresh World gets dropped.
7. `DbProxy: {Op} is allow-listed but has no handler` — a dispatch/allow-list mismatch shipped.

And two more that are new since the last revision of this list:

- `Link #{Id}: handler for 0x{Op:X4} ({Len} B) threw - frame dropped` (window A, Error) — the
  per-frame try/catch T48 asked for is now in `WorldLink.ReceiveLoop`, so a throwing `SDB_`
  handler drops one frame instead of closing the link and disconnecting everyone. It is no longer
  fatal, which means it is now easy to miss: every one of these is a bug that used to end the
  session, and the frame it dropped was a DLM item, so that user is head-blocked anyway.
- In the World console: `handler has not been implemented yet!!! {opcode} {len}` — an
  Arbiter-owned client packet is still being forwarded. `status/CLIENT-REJECTS.md` names all 18.

---

## What this pass cannot cover

Even with two clients, these stay untested until the capture sessions in
`status/CAPTURE-PLAN.md` happen:

- **Warehouse** — no capture contains a single warehouse frame; every offset in
  `World/WarehouseHandlers.cs` comes from the decompile alone.
- **Mail with actual mail in it** — `ParcelDataNoMsg`'s 0x9e8-byte interior is unknown, so
  `ParcelCount = 0` is the only honest answer this build can give (section 6 tests exactly that).
- **Broker** — not implemented at all (T53 research).
- **Private chat channels** — `ChatManager` implements them but no client packet has ever been
  seen; section 7 exercises whisper only.
