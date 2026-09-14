# Chat handoff — for the Claude that pairs with the human (NOT Cowork)

Last updated: 2026-09-14 ~01:40 local. If you are reading this, the previous chat session died.
Read this file, then `CLAUDE.md`, then `status/HANDOFF.md` §1 (DLMItems), then
`status/PERSISTENCE-MAP.md`, then `status/STATUS.md`.

## Roles

- **This chat (you):** the human's live pair. You have the `filesystem` tool on the human's D:.
  You edit Cowork-editable files directly on `master` (World/DbProxyHandlers.cs, Handlers/Chat*,
  Character*, Social*, Persistence/*, Tests, status/*, data/*). Human-owned files (WorldBridge,
  WorldEntry, GameSession, LoginHandlers, HandlerRegistry, Program.cs): give the human a PowerShell
  patch (`[IO.File]::ReadAllText` with FULL paths — `.\x` resolves against system32). You can't run
  commands; the human runs `.\ship.ps1 "msg"` (build+tests+commit+publish+7z) and pastes output.
- **Cowork:** separate autonomous session. It CANNOT run git. The human creates a git worktree
  `D:\v100\TERA_SERVER.100\TeraSharp-cowork` on a `cowork/Tn` branch; Cowork writes only there; the
  human commits/rebases/merges. Rules are in `CLAUDE.md`. Cowork can't build either (no dotnet in
  its container) — it verifies bytes with Python; the human builds before merging. Its work today
  has been good (it found a real bug in my 0x13BF builder).
- **Human:** runs WorldServer + client on a netcup VM (`C:\deploy.ps1` = kill arbiter, curl the 7z
  from `http://104.62.81.162:8888/`, extract, set env vars incl. `TERASHARP_STARTER_BLOB`, run with
  Tee to `C:\TeraSharp-deploy\arbiter.log`). Terse; wants commands, not prose. World takes ~3 min
  to restart; a fresh World process is needed for many tests (see below).

## State of the server (what works live)

- Login → play → Logout button → lobby → relog: works, position restored (blob offset 220).
- Chat: works (AS_REQUEST_NORMAL_CHAT 0x1449 to World; S_LOAD_CLIENT_USER_SETTING + block/friend
  lists must be sent after C_LOAD_TOPO_FIN, not at select).
- Character creation (T8, Cowork): works. `data/starter_blob.bin` = the real Arbiter's 15312-byte
  blob for a never-entered character; `StarterBlob.Build` patches playerId@112, name@116,
  x/y/z@220/224/228, zone@236. Field map in `data/starter_blob.md` (ZONE IS 236, NOT 208; 208=HP).
  Still missing: race/gender/class@192/196/200 and appearance 288 / appearance2 296 / details 312 /
  shape 344 (Cowork T12 in flight) — a human warrior currently looks like an Elin valkyrie.
- New characters get the real starter inventory: `0x27A2` → `0x27A3` + `0x27A4` built from
  `data/starter_inventory.bin` (6 × 536 B items, owner playerId at item+16, reqId at [8]).
  playerId 1 (dob) still gets the replay-table capture. `EnterWorld Failed [..] [name] [1]` in the
  World console = World rejected the inventory (owner mismatch).
- gameId is a per-login counter reset per World process (`WorldBridge.AllocateGameId`), NOT
  `0xAF00000|playerId`. `DbProxyHandlers.GameIdByPlayer` carries it to 0x2830.
- Zone change / quest teleport: `0x13BE→0x13BF`, `0x13C0→0x13C1` echoes (Cowork T10 fixed the
  padding rule: only struct-padding bytes are zeroed; 44..47 is a coordinate). `0x1439` (empty
  visited-section list) sent on every C_LOAD_TOPO_FIN. Live test of a teleport still pending.
- Post-handshake: 98 × `0x1581 [dungeonId][1][0]` pushes (`OnWorldReady`), and `0x147D` is answered
  with the 23 captured `0x147E` promotion records re-stamped with the current UTC time
  (`data/promotions_147E.bin`). See "the crash" below.
- T6 (Cowork): characters row now gets zone/x/y/z from every blob save and level/exp from
  `0x273B S_UPDATE_EXP_LEVEL` (level 0 = exp-only update; ok = row-updated).

## The crash we were fixing when this was written — SOLVED 02:02

Root cause: `WorldEntry.EnterWorld` step 5 sent a pre-emptive `0x2738` right after `AS_ENTER_WORLD`
with `[8] = playerId` where World expects a DLM id. On a fresh World the low ids belong to the
load contexts World creates right after the real enter-world completes, so the stray frame
"completed" e.g. `DBLoadPromotionContext` with an enter-world payload -> crash in its
`ExecuteCommit`. Removed; `DbProxyHandlers.OnUserEnterWorld` answers `0x2711` with the live id.
Minidump analysis recipe (PowerShell, no WinDbg) is in this chat's history: parse streams 4
(modules), 6 (exception), 3 (threads); scan the faulting thread's stack for addresses inside
WorldServer.exe; map `WorldServer+0xNNN` to `FUN_1400NNN` in the decompile.

Old notes from the hunt (kept for context):

Symptom: a **level-1 character entering a FRESH World process** → World silent after our
`DBS_USER_ENTERWORLD`, minidump written the same second, process dies ~45 s later (writing a 30 GB
full dump — delete `*_full.dmp` in `C:\TERA_SERVER.100\Executable\`, they eat the disk).
After dob (level 58) has entered once, the same character enters fine ("warm" path). Not the
zone, not the blob bytes (sha256-verified byte-identical to the real one), not gameId.
Minidump (parsed with the PowerShell snippet in this chat's history — MINIDUMP streams 4 and 6):
access violation at `WorldServer+0x128F4B0` = `FUN_14128f410` = vector<PromotionConditionDataHead>
copy ctor, called from `PromotionController::NewPromotion` (WorldServer.exe.c ~3531130). Newbie
promotions are evaluated for level-1 characters only, and the promotion datasheet World uses comes
from the Arbiter at handshake (`0x147D → 0x1484 + N×0x147E + 0x1480`); we were replaying the
2026-09-12 records with stale timestamps. Fix shipped (commit 51d635e + merge): live timestamps.
**Not yet live-verified** — next step is: deploy, restart World, select `testtwo` (zone 5) as the
first login. Expect `0x147D: sent 23 x 0x147E promotion records stamped …` in the handshake and
`EnterWorld [1] testtwo(3)` in World. If it still crashes, get the new minidump address the same way
and look for the next caller.

## How to get facts

- Captures in `D:\packetlogs\`: `lobby_tap.log` (login/logout/relog, real Arbiter), `cap_newchar.log`
  + `cap_newchar_client.log` (create char → Island of Dawn → quests/gathering/kills/level-up/teleport
  → logout). `cap_newchar_ctl.txt` = condensed control-channel listing (seq, dir, time, op, len,
  first 64 B) — read this first, it's small. `cap_newchar_zone.txt` = full 0x13BE/0x13C0 frames.
- Files >1 MB: `read_text_file` with `head=`/`tail=` (≤1 MB each) → results land in
  `/mnt/user-data/tool_results/*.json` → parse with the bash tool (`parse_tap.py` pattern: reframe
  by u32 length per direction). The middle of a big file is unreachable that way; have the human
  produce a condensed listing with the PowerShell reframer instead.
- Decompiles: `Arb_part_*.c` (Arbiter, readable), `world_decompiled\WorldServer.exe.c` (125 MB —
  NEVER read whole; human runs `Select-String` for a line number, then
  `Get-Content $W | Select-Object -Skip N -First 200 | Set-Content status\x.txt`; each pass ~30 s).
  Ghidra base 0x140000000 → `WorldServer+0xNNN` = `FUN_1400NNN`.
- World console log: `C:\TERA_SERVER.100\Executable\Logs\Console\WorldServerConsole_<date>_2800.log`
  (rolls at midnight). Minidumps next to WorldServer.exe.

## Rules that bit us

- **Never send a `DBS_*` reply World didn't ask for.** Every DBS_ carries a DLM id that World looks up
  in DLMExistManager; an unsolicited one (the pre-emptive 0x2738 WorldEntry used to send with
  playerId in the id slot) completes whichever item currently holds that id. It only "worked" for
  playerId 1 by coincidence and crashed a fresh World for every other character (found via minidump
  stack walk, 2026-09-14 01:55). Pushes (AS_*, no id) are fine; replies are not.

- `[IO.File]` in PowerShell uses the process CWD (system32) — always full paths.
- Two `git worktree add` with the same folder: the second fails silently and Cowork writes into
  the old branch. Check `git worktree list` + `git branch -v` before assuming where a commit went.
- Cowork's worktree can be stale vs master: rebase it (`git rebase master` in the worktree) and
  resolve `DbProxyHandlers.cs` conflicts (usually both `case` lines are wanted).
- Replay-table accidents: T5 sealing `0x15A8` removed the `0x1581` burst that used to be replayed
  by luck. Anything the real Arbiter pushes unprompted must be sent explicitly, not hoped for.
- `LoginHandlers` `maxCharacters` now follows `CharacterHandlers.MaxCharactersPerAccount` (8).
- `TERASHARP_START_OVERRIDE="zone,x,y,z"` env var forces new-character start (experiment knob).

## Next steps (in order)

**Update 2026-09-14 late.** Merged T24-T34 (361 tests). Master now also has: dungeon cool-times,
reputation + per-account fatigability, MULTIPLAYER-DESIGN.md (routing = Ticket index into a session
table; SA_BYPASS_TO_CLIENT is N x 16-byte recipients, packet at payload[8]-6 - our parser is wrong
for 2+ players; six Routing_* tests report PENDING until section 6's WorldBridge/GameSession diffs
are applied), PARTY-DESIGN.md + the party packet codec (no PartyManager yet), HANDSHAKE-DATA.md
(0x1581 burst = echo of World's non-empty 0x13F2; HandleFrame now dispatches it; fallback burst
still on via DbProxyHandlers.SendPostHandshakeDungeonBurst - flip to false once the log shows
`0x13F2: echoed 98 ...`). Cowork session 1 has T30 friends / T31 auth / T32 GM commands queued.
EVERYTHING since T19 is live-untested - the first live session is a fresh World + new character
+ 9827 relog + keybind check, then the routing diffs and the two-client capture (checklist in
MULTIPLAYER-DESIGN.md section 8: the tap must label frames by client socket).

**Scope reference (2026-09-14 ~06:20).** `status/arbiter_c_handlers.txt` (~270 `C_` handlers the real
Arbiter serves) and `status/arbiter_s_packets.txt` (~200 `S_` it builds via the PDL template writer -
a lower bound). HandlerRegistry registers 44. By area: login/lobby/characters/settings/exit (done),
chat+block (done; whisper/private channels not), friends (lists only), party + matching (30, none),
guild (60, none), trade broker (20, none), lord/election/city war (15, none), petition/reports (12),
admin (25), events/attendance/shop/VIP (20), TBA battlepass (10), appearance/name change/rankings (25).
Priority for a populated server: multiplayer routing -> party -> guild -> broker -> friends/whisper.

**Update 2026-09-14 ~05:40.** T21 merged + wired (WorldBridge.PlayerForGameId, WorldEntry.ResendEnterWorld,
Program.cs hooks; AS_ENTER_WORLD [68] is EnterWorldType=1 not level, [52] = stored instance PDId when the
saved zone is that instance). 278 tests, shipped 71437b1. **Live test pending**: new character -> teleport
into 9827 -> Exit -> relog; expect `EnterWorld retry for '...': continent 9827 refused, falling back to zone 5`,
spawn on Island of Dawn, World re-teleports into the instance. (letustry has no stored return point - delete it.)
Cowork in flight: **T22** (`TeraSharp-cowork`, cowork/T8): per-character loads from rows - achievements,
reputation, tutorial tips, seren, fatigability, EP, dungeon history; **T23** (`TeraSharp-cowork2`,
cowork/T23): post-handshake config burst + 0x15ED/0x295D. Prompts are in this chat's history; CLAUDE.md
section 0 has the queue. Merge order irrelevant; both touch DbProxyHandlers.cs so expect one conflict.

**Update 2026-09-14 ~05:00.** Merged since the milestone: T19 (client settings persist -
`client_settings`/`account_settings`, `C_SAVE_CLIENT_*` registered, `S_LOAD_CLIENT_ACCOUNT_SETTING`
sent before the user setting after C_LOAD_TOPO_FIN), T20 (inventory persistence - `items` table
keyed (owner_id, id), atoms applied for ops 7/2/6/11/9, 0x27A4 rebuilt from rows, starter kit
inserted on first login). 272 tests. **Capture taken**: `D:\packetlogs\arb_world_2026-09-13T11-33-30-680Z.log`
(+ `capture_2026-09-13T11-42-27-513Z.log` client side), condensed `cap_relog9827_ctl.txt`, full
enter-world frames `cap_relog9827_frames.txt`. It shows the real relog-into-instance behaviour:
AS_ENTER_WORLD into 9827 with [52]=PDId -> World 0x138D (fail) -> Arbiter pushes 0x148D x2 and
re-sends AS_ENTER_WORLD at the return position saved from the earlier 0x13BE context, with
[52]=-1 -> spawn on Island of Dawn -> World re-runs the teleport quest itself. Also: 0x272D list 2
= completed quest ids; the post-handshake config burst is sent right after the handshake.
**Cowork T21** (worktree `TeraSharp-cowork`, cowork/T8, rebased on master) implements both.
Netcup is back on TeraSharp (DeploymentConfig `<WorldServerConfig><ArbiterServer port>` must be
7802; 7812 = the tap). Real-Arbiter capture recipe is in item 1 below - and it needs MS SQL
(`MSSQL$SQL2022` listens on 1433 when started).

**MILESTONE 2026-09-14 ~03:30: a brand-new character works end-to-end on a fresh World — creation,
per-class kit, default skills, quests — "everything works like it did on Arbiter" (human).**
Merged: T14/T15/T16/T17/T18 (248 tests). Merge notes: T15's 0x272E handler was dropped for
T17's; three tests needed fixture rows because playerId 1 is reserved for dob's captures and
`quests` has an FK on `characters`.

0. **Where things stand at end of 2026-09-14 (~02:30):** live-verified today: fresh-World first login of a
   new character (testtwo, zone 5), elinwarrior spawned after the 0x1463 crest handler. Shipped but
   NOT live-verified: 0x297B/0x297C ack, T13 (0x2769 echo + item id counter), T11/T12 (starter blob
   identity block). Two Cowork sessions in flight: **T14** (per-class starter inventory from
   `Executable\Datasheet\CreateCharData.xml` + `ItemTemplate*.xml`, worktree `TeraSharp-cowork`,
   branch cowork/T8) and **T15** (answer every per-user DB write from PERSISTENCE-MAP.md + a test
   that every request opcode is handled/one-way/replayed, worktree `TeraSharp-cowork2`, branch
   cowork/T15). Merge order: T15 first (smaller blast radius), then T14; expect a DbProxyHandlers.cs
   conflict between them (both add allow-list cases - keep both). Known open: warrior skills locked
   (valkyrie starter gear + maybe `learnAllSkills` on the CreateCharData row); characters created
   with TERASHARP_START_OVERRIDE set sit in Velika (testthree, elinwarrior) - delete them.
   **Overnight results (all in worktrees, unmerged):** T14 per-class starter kit from
   `CreateCharData.xml` + `ItemTemplate.xml` (glaiver output byte-identical to the capture, ids
   allocated in datasheet order, records emitted sorted by pocket/slot); T16 start position from the
   datasheet (only soulless differs); T17 quest persistence (`quests` table, rowid = questDbId at
   0x272F payload[25]; 0x272D rebuilt from rows, empty case == 73-byte capture; completed quests
   stored but not served until a capture of "Test" relogging on the real Arbiter shows list 1/2);
   T15 every per-user DB write answered + coverage test (`IsHandledRequest` refactor); T18 SKILLS.md:
   **level-1 skills live in the blob** - 40 passive slots @6880, 500 active @7200, 8 B each
   `[u32 skillId][u8 0][pad]`, written by `AccountManager::ExecCreateDefaultSkills` from
   `DefaultSkillSet.xml` (race,gender,class), read back by `User::SendMySkillList`; learned skills
   persist through the blob save. `StarterBlob.ApplyDefaultSkills` now patches them. Characters
   created before that keep valkyrie skills - delete and recreate. Live-bug fixed by T17: every
   character was being handed dob's quest 59901 + 18 stale seeds via `QuestListEmpty`.
   Merge: cowork/T15 (T15+T18) first, then cowork/T8 (T14+T16+T17); conflicts expected in
   `DbProxyHandlers.cs` (`IsHandledRequest` case list: keep 0x297B inside it, drop the
   `HandledOnMasterNotOnThisBranch` exemption; `0x272E`: take T17's handler) and in `PatchedWindows()`
   in the tests (T12/T18 both extend it - union).

1. **Relog into an instanced zone fails** (04:03, character 'letustry' saved in zone 9827 = the
   instanced intro area): World sends 0x138C, the client never finishes loading, World times the
   user out ~60 s later (0x2927 ticks, then 0x13AA..0x1393 with "no session for gameId"). The
   real Arbiter must set up the dungeon channel for a login into an instance (0x13BE/0x13C0 and/or
   the 16-byte world-session state at AS_ENTER_WORLD [167] that we zero) - never captured. Options:
   (a) capture a real-Arbiter relog from inside 9827 (same capture as the "Test" quest one!).
   HOW: on netcup stop TeraSharp; `DeploymentConfig.xml` `<ArbiterServer port="7812">`; `node
   C:\TERA_SERVER.100\arbiter-world-tap.js` (listens 7812 -> 7802, logs to
   C:\TERA_SERVER.100\arb_world_<stamp>.log); start the real ArbiterServer from
   C:\TERA_SERVER.100\Executable (it needs MS SQL Server listening on 1433 - on 2026-09-14 04:30
   both instances `MSSQLSERVER` and `MSSQL$SQL2022` were STOPPED and the Arbiter died instantly
   with no console log; check `Get-NetTCPConnection -LocalPort 1433 -State Listen` first); restart
   World; client proxy; log in "Test" (the real Arbiter's playerId 2, saved in 9827); spawn, walk,
   Logout, Exit. Copy tap log + proxy log to D:\packetlogs\cap_relog9827[_client].log. Revert the
   port to 7802 and `C:\deploy.ps1`.
   (b) short-term: on login, if the saved zone is an instance, spawn at the continent's return
   position instead (the real Arbiter has AS_SAVE_ETC_DATA_FOR_MOVE_WORLD 0x1499 data for that).
2. **Exit Game button**: timing is 5 s already (C_EXIT -> leave -> S_EXIT); only the countdown
   display is missing because `S_PREPARE_EXIT` is sent via `defs.Has(..)` and is not in the
   registry - send it raw (`[u32 time]`, same shape as S_PREPARE_RETURN_TO_LOBBY).
3. `no replay for 0x156F` still logged after the T15 merge - check `WorldReplayTable.OneWayFromWorld`
   kept T15's additions (0x156F, 0x1491, 0x13B6, 0x13C5, 0x13C6, 0x1499, 0x15FA, 0x2927).
4. Live-verify the promotion-timestamp fix on a fresh World (see above). Delete old `*_full.dmp`.
2. Merge Cowork T11 (INVENTORY-DESIGN.md) and T12 (identity block in StarterBlob.Build), then
   create a human warrior and confirm it looks right.
3. Live-test a quest teleport / dungeon enter (zone-change echoes).
4. Persistence: quests first (`status/PERSISTENCE-MAP.md` order), then inventory per T11's design.
5. Two-login capture for multi-player; account auth last.
