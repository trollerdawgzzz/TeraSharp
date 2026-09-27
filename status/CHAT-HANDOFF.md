# Chat handoff - for the Claude that pairs with the human (NOT Cowork)

Last rewritten: 2026-09-22, T174, against master @ T172. The checkpoint log this file used to be
(2026-09-14 .. 09-21) is in git history; everything still true from it is folded in below.
Read order: this file -> `CLAUDE.md` -> `status/HANDOFF.md` section 1 (DLM items) ->
`status/PERSISTENCE-MAP.md` -> `status/STATUS.md` (area table, then the per-task log).

## 1. Roles

| Who | Does | Cannot |
|---|---|---|
| Human | runs netcup (WorldServer, tera-api, proxy, TeraSharp), builds/ships (`ship.ps1`), commits, merges, rebases the worktrees, applies human-owned patches | - |
| This chat | live pair: reads D: via the filesystem tool, edits Cowork-editable files on master, hands PowerShell patches for human-owned files (full paths: `[IO.File]` resolves against system32) | run commands |
| Cowork session 1 | worktree `TeraSharp-cowork`, branch `cowork/T8` | run git; edit human-owned files |
| Cowork session 2 | worktree `TeraSharp-cowork2`, branch `cowork/T27` | same |

Both Cowork sessions build and run the full test suite in their own Linux sandbox since T150b (the
reports say "Built and run in a Linux sandbox (N passed)"); the human's `dotnet build` is still the
merge gate. Human-owned files (CLAUDE.md section 0): `Program.cs`, `Network/*`,
`World/WorldBridge.cs`, `World/TunnelFrames.cs`, `Handlers/WorldEntry.cs`,
`Handlers/HandlerRegistry.cs`, `Handlers/LoginHandlers.cs`. Cowork ships changes to them as
`status/Tnnn-PATCH.diff`: T161, T161b, T172 are applied on master; `MULTIWORLD-PATCH.diff` is the
multi-World step, applied as T138b.

## 2. Architecture in one screen (details: `docs/ARCHITECTURE.md`)

```
client -> tera-server-proxy :7801 (public, the GM gate) -> TeraSharp :7701 (loopback)
WorldServer.exe --id=1 [--id=13] -> TeraSharp :7802 (25 links per World: 1 control + 24 bypass)
TeraSharp admin web :8051 (loopback, X-Admin-Token)   tera-api :81 public, :8080 auth API (loopback)
```

- TeraSharp replaces `ArbiterServer.exe` only; World is the retail binary. World dies when the
  Arbiter link drops and never reconnects, so every Arbiter restart is a World restart (~3 min).
- Client packets the Arbiter does not own go to World inside `AS_BYPASS_FROM_CLIENT` 0x13F6;
  World's client packets come back in `SA_BYPASS_TO_CLIENT` 0x13F7 (N-recipient, Ticket-routed,
  T38; departed tickets never borrowed, T161).
- DB traffic: every `SDB_*` World sends is answered by `DbProxyHandlers` (+ `DbProxyT167/168/170/172`
  partials), a `DbAckTable` row, or the `WorldReplayTable` (captured pair, id patched). Persistence
  is one SQLite file (`TERASHARP_DB`, default `<logs>\terasharp.db`), schema in `CharacterStore.cs`.
- Multi-World: `WorldRegistration` / `WorldInstances` / `ContinentRouting` (T103-T112, T138/T138b);
  a continent is owned by the World whose 0x164D roster lists it.
- Datasheets: `World/DatasheetLoader.cs` (T159) - section 5.

## 3. What is verified, and how

| Level | Meaning |
|---|---|
| live | seen working against a real client on netcup |
| pinned | byte-exact against a real-Arbiter capture, in a test (`data/*.bin` TSIS fixtures) |
| decompile-only | layout from the Arbiter writer + World reader; no capture has the frame |

| System | State | Tasks |
|---|---|---|
| Login, lobby, create/delete, enter world, relog, logout, chat, keybinds | live | T1-T21, T30 |
| Two+ players in one World, whisper, party, trade (items + money), friends/blocks | live (09-15/09-16 passes) | T38, T47, T49, T60, T64 |
| Inventory rows, money, loot, starter kit item ids | live | T44, T59, T105 |
| Broker, mail, bank | live 09-16 for buy/collect and deposit; the T74 / T141 fixes (tabs, attachments, withdraw, TotalPaid) not re-verified live | T71-T81, T141 |
| Guild create/ranks/leave, LFG, guild war declare/withdraw | wired, pinned to cap_social2-4 | T39-T57, T80 |
| Guild war declare back / withdraw one side / surrender, guild quests start/cancel | pinned (cap_final2a/2b), not live | T170 |
| GM `/@`, Alt+A panel, GM tool buttons | live (Alt+A after T144b) | T32, T46, T89, T144b, T148, T155 |
| Leaderboards, dungeon cool times, matching window | live (Classic+ client) | T118-T136 |
| Dungeon / BG queue -> matched system party -> enter | two live queue sessions (cap_queue1/2) drove T161/T163; the fixes are not re-verified live | T138b-T138d, T161, T163 |
| Multi-World hand-off (Velika 9781 -> World 13) | wired, pinned to cap_multiworld3; live test pending | T138b |
| Crafting / gathering | pinned client-side (classic_craft); A<->W writes decompile-only | T147, T147b |
| Enchanting family, cards, EP pages, polishing, dungeon rank | enchant: decompile-only; polishing + EP writes + card loads pinned | T166, T167, T169, T170 |
| Item tooltip + compare (0x282E ask / 0x282F answer) | pinned: 59 material tooltips byte-exact; equipment differs in 3 item-level floats | T169, T170 |
| Group C remainder (VIP, servants, money GM, gold, attendance, ...) | decompile-only, store-backed | T168, T168b |
| Clear all skill, event-system progress | clear: pinned (cap_clearallskill); progress: decompile-only | T171 (= T170 parts 5-6) |
| Visited sections (cinematics), guild-quest points, purchase-limit reset, whole-world broadcasts | pinned | T172 |

The per-opcode truth is `status/PERSISTENCE-MAP.md` (a test parses it); the census of every
opcode in the two newest taps is `status/T169-CENSUS.md` (T172 resolved every row).

## 4. The wedge story, and DbAckGroups

- **The failure mode.** Every `SDB_*` with a DlmId is a DLM item in World's per-user DB queue. One
  unanswered request freezes that character's DB writes for the session: skills, equips, loot,
  mail all stop, silently (`status/HANDOFF.md` section 1). One unsolicited `DBS_*` is worse: it
  completes whichever item holds that id - the 2026-09-14 fresh-World crash.
- **How they were found.** Tap in front of TeraSharp, then the first request with no reply:
  cap_bag 0x2732 quest-list batch (T145), cap_play1 `SA_CREST_USE` + `PEGASUS_FEE` (T158), cap_scroll
  0x28AE / 0x2790 (T164), 0x283D bag grow (T150b), twelve crafting writes (T147), 0x2734 (T142).
- **Generic acks.** T150 answered ~180 opcodes with a `[DlmId][ok]` table and was reverted after a live
  regression (a level-70 loaded as level 1). T150b made `DbAckTable` opt-in: only rows proven by a
  live pair.
- **The groups (T165, `World/DbAckGroups.cs`).** All 300 request/reply twins sorted, each checked
  against World's `Handler_DBS_*` reader: A = unpinned table rows (log "capture a real pair to
  pin" once), B = denied on purpose, C = needs a real handler, Elsewhere = answered elsewhere.
  `DbAckTable` refuses to load a B/C row. Since T165b the pending lists are computed from
  `IsHandledRequest`, so a new real handler needs no table edit.
- **Now.** Group C landed in T166 (enchant), T167, T168, T170. Pending C = **0x275C ITEM_DECOMPOSE
  only** (dead on both sides: World's reader returns false). The walk test pins that.
- **Guards.** `Fuzz_dbproxy_dispatch_survives_hostile_frames` throws every allow-listed opcode
  76k hostile payloads: a handler exception closes the World link, so it must stay green (T168b
  found a FK throw that way). Store writes check owner/guild/account first (`NoSuch*`).

## 5. Datasheets

| Rule | Detail |
|---|---|
| Source of truth | World's `Executable\Datasheet` (`TERASHARP_DATASHEET` overrides). Every value copied into code is a `SheetValue` in `DatasheetLoader`; the copy is only the fallback (T159) |
| Loaded today | GuildConfig sizes + reparation rates, DungeonMatching ClassPosition, DefaultSkillSet, CreateCharData kits + createdLevel, BattleFieldData, DungeonRankRecorder ids, dungeon timeline ids, EventMatching targets, GuardData, BuyMenuData, ItemTemplate equip slots (T159-T172; table in `status/DATASHEETS.md`) |
| Startup | `DatasheetLoader.LoadAll` (Program.cs, applied) logs one line per sheet; `--check-config` lists them |
| New constant | not without a loader (CLAUDE.md) |
| Editing a sheet | `docs/GO-LIVE.md` section 10 |

## 6. GM and Alt+A - what was learned

| Finding | Task |
|---|---|
| `S_LOGIN_ARBITER.status` 33 = GM client switch (31 = normal) | T32, T104 |
| `TERASHARP_GM_ACCOUNTS` takes numeric accountDBIDs; `accounts.admin_level` is the durable route | T46, T101 |
| **The Alt+A gate is `S_SELECT_USER` body 1 = int32 adminLevel** (the shipped def is mis-typed); the control capture (same GM account, a 0-level character) proved it. WorldEntry still sent the old literal | T144b |
| `apiServerAddress` / the 8800 probe / S_VERSION_INFO are NOT the gate; the replay bisection could not find it | T129, T131, T132, T143 |
| World vaporizes a GM at spawn when adminLevel > 0; the panel's Invisible toggle is World's (0x2827 action 0x65); our spawn-time S_ADMIN_GM_SKILL contradicted World and froze learns | T148, T152 |
| A GM spawns held: `S_ADMIN_HOLD_CHARACTER 00` right after `S_SPAWN_ME`, operators only | T144b |
| World's `User+0xA474` admin level is not settable from the Arbiter; "AdminLevel[0]" in the World log is cosmetic | T142b |
| Tool buttons: teleport / map teleport / go to / summon / resurrect / delete monster wired via 0x2825-0x2827; kick is logged only | T155 |
| Real-Arbiter GM needs `qaServer=true`; tera-api privilege 33 puts the character in GM-invisible mode (use 0 for players) | 09-15/09-16 capture notes |

## 7. Capture files (`D:\packetlogs`)

`*.log` = raw frames; `*_ctl.txt` = reframed one-line-per-frame listing (payload cut at 64 B -
never diff from it); `*_client*.log` = the client side of the same session. Tap = World<->Arbiter.

| File | Server | What it holds | Consumers |
|---|---|---|---|
| `lobby_tap.log`, `lobby_proxy.log` | real | login / logout / relog | T1-T5 |
| `arb_world.log` | real | the World conversation `WorldReplayTable` is built from (required at runtime) | replay |
| `cap_newchar*` | real | create -> Island of Dawn -> quests, loot, level, teleport, logout | T8-T22 |
| `cap_social*` (1-4, + client/client2) | real | party + contracts, mail with attachment, whisper, friends/blocks, guild window (1); guild ranks/announce, warehouse, mail (2); broker with listings, LFG, guild war window, wanted board (3); broker seller, guild war declare/withdraw, EP, card pushes (4) | T61-T82 |
| `cap_final*` (+ client1-4, gm_client, gm_client2) | real | the GM session: Alt+A opening (gm_client2) and its control (gm_client), makeitem (op 8), card/EP loads | T89, T124-T153, T170 |
| `cap_final2a*`, `cap_final2b*`, `cap_final2_clients\` | real | census taps: tooltip compare, guild war declare/accept/withdraw/surrender, guild quests, EP writes, visited sections | T169, T170, T172 |
| `cap_clearallskill*` | real | `/@clear...` skills: 0x27E6 -> 0x27E7 (902/903) | T171 |
| `cap_multiworld`, `2`, `3` (+ client) | real | DungeonServer `--id=13`; #3 is the definitive cross-World hand-off | T138, T138b |
| `classic_live`, `2`, `3`, `4`, `classic_craft` (+ `.npcap`) | live Classic+ (client-side) | leaderboards, BG/dungeon queue, guild quests (1-3); broker, mail, trade (4); crafts + gathers | T118-T141, T147b |
| `cap_t124`, `cap_altA_gm`, `cap_altA_probe`, `cap_ts_gm2` (+ client) | TeraSharp | our side of the Alt+A hunt | T129-T144b |
| `cap_bag`, `cap_invensize`, `cap_skills2/3`, `cap_play1`, `cap_scroll`, `cap_makeitem` | TeraSharp (tap in front) | the wedge hunts: login batch, bag size, learns after relog, crest/pegasus, level scroll, makeitem | T145-T164 |
| `cap_queue1`, `cap_queue2` (+ clients) | TeraSharp | first live dungeon queues | T161, T163 |
| `cap_crash`, `arbiter-crash.log` | TeraSharp | the leave-during-load crash (LeaveGate) and /@goto | T161b |
| `terasharp.db` | - | the local instance's SQLite file (the default `TERASHARP_DB`) | - |

Tools: `tools/reframe-tap.ps1` / `reframe-client.ps1` (the `_ctl.txt`), `tools/make-tsis.ps1`
(fixture), `tools/npcap-to-capture.ps1` (Classic+), `tools/arbiter-world-tap.js` (listens 7812,
forwards to 7802; point DeploymentConfig's `ArbiterServer port` at 7812 for a tapped session).
`data/classic-live/*.hex` stays local, never committed.

## 8. How to get facts

- Decompiles: `Arb_part_*.c` / `ArbiterServer.exe.c` (Arbiter), `world_decompiled\WorldServer.exe.c`
  (125 MB - never read whole; Select-String for the line, then a 200-line window). Ghidra base
  0x140000000: `WorldServer+0xNNN` = `FUN_1400NNN`.
- World console: `Executable\Logs\Console\WorldServerConsole_<date>_2800.log`; minidumps next to
  WorldServer.exe (delete `*_full.dmp`, they are ~30 GB). Stack-walk recipe: parse MINIDUMP streams
  3/4/6 in PowerShell, map addresses to `FUN_`.
- Tests: `TERASHARP_TEST_FILTER=T170_` runs a subset (T170).

## 9. Rules that bit us

- Never send a `DBS_*` World did not ask for; pushes (AS_*) are fine.
- Every deploy = World restart. Keep the order: SQL -> Laragon (MySQL) -> tera-api -> proxy ->
  TopographyServer (`--sharedmemoryproducer=true`, via its .bat) -> TeraSharp -> World `--id=1`
  -> `--id=13`. World and Topography need an elevated shell.
- `DeploymentConfig.xml`: `<Topography folderName=".\Topology">`, not `..\..\Topology`. It also holds
  the SQL passwords - never quote it, nor the capture-tune backup copy.
- `ServerConfig.xml` must keep its UTF-8 BOM, or the real Arbiter exits 1 silently.
- Two Worlds fit in 32 GB only with the low-mem config (`D:\ServerConfig.lowmem.xml`,
  `D:\ContinentData.lowmem.xml`). Session A (real Arbiter + World) needs it too.
- Diff full payloads, never `_ctl.txt`; when two sessions differ, find a control session and diff
  every field on the LIVE path (T144b's lesson).
- Tap link numbers climb on every World reconnect: grep by link, not by `#3`.
- `git worktree add` into an existing folder fails silently; check `git worktree list`.
- Master is CRLF; apply LF patches with `--ignore-whitespace`.

## 10. Open items

| Item | State | Next step |
|---|---|---|
| Card-preset writes 0x2990 / 0x2998 (and all of 0x2988-0x2998) | decompile-only, store-backed (T167); no capture has one (T170 checked cap_final, cap_final2b) | tap session: change preset, add preset, mount/unmount a card |
| Civil Unrest (CU) | city owners 0x2954 (T154), state 0x2958/0x295A (T172), capture tuning ready (T149 `capture-tune.ps1`); the rest is in the not-started lord / city-war set (`status/MISSING-HANDLERS.txt`) | a CU capture with `capture-tune.ps1`, then a design task |
| Menu pop | a two-account queue via the Instance Matching menu never formed on the real Arbiter (MatchingRoleTemplate edits didn't take), so the real pop sequence for both members is unpinned; classic_live3 (leader only) is the reference (T174b) | get a two-member queue to form on the real Arbiter and tap both clients |
| Guild war / guild quests live | T170 pinned; live pass pending | two guilds, declare -> declare back -> surrender |
| Multi-World live | T138b wired | World `--id=1` + `--id=13`, walk into Velika 9781, queue with `TERASHARP_MATCH_MIN_MEMBERS=2` (unset after) |
| Equip tap | `SDB_EQUIP_ITEM` answered in the ITEM_SINGLE shape, no tap holds one (T145/T151) | one equip on a tapped session |
| Equipment tooltip item level | 3 floats need EquipmentItemLevel data (T169) | export from the datacenter |
| Event-system progress | stored (T171) but not served back at World connect (`AS_LOAD_EVENTSYSTEM_PROGRESS_INFO`) | a task when an event is actually run |
| Go-live | `docs/GO-LIVE.md` | work down it; section 9 is the maintenance loop |
| Publish | `D:\TeraSharp-public` (branch `public`), audit gate `tools/audit-release.ps1` (T139); T140 is replacing verbatim decompile quotes | `audit-release.ps1 -Strict` exit 0, then push |
| T146 | paused | - |
