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

1. Live-verify the promotion-timestamp fix on a fresh World (see above). Delete old `*_full.dmp`.
2. Merge Cowork T11 (INVENTORY-DESIGN.md) and T12 (identity block in StarterBlob.Build), then
   create a human warrior and confirm it looks right.
3. Live-test a quest teleport / dungeon enter (zone-change echoes).
4. Persistence: quests first (`status/PERSISTENCE-MAP.md` order), then inventory per T11's design.
5. Two-login capture for multi-player; account auth last.
