# Chat handoff — for the Claude that pairs with the human (NOT Cowork)

Last updated: 2026-09-13 ~21:45 local. If you are reading this, the previous chat session died.
Read this file, then `CLAUDE.md`, then `status/HANDOFF.md` §1 (DLMItems), then `status/STATUS.md`.

## Roles

- **This chat (you):** the human's live pair. You have the `filesystem` tool on the human's D: drive.
  You edit files directly in `D:\v100\TERA_SERVER.100\TeraSharp` on `master`, together with the
  human, who commits. You may touch human-owned files (WorldBridge, WorldEntry, GameSession…) but
  prefer giving the human a PowerShell patch for those so he stays in control. You CAN'T run
  commands (no git, no dotnet) — the human runs what you give him and pastes output.
- **Cowork:** a separate autonomous session that works only on `cowork/Tn` git branches, one task
  at a time from the "Cowork task queue" in `CLAUDE.md`. The human merges. Do not duplicate Cowork
  tasks; if you need something from the queue urgently, tell the human to pull the branch.
- **Human:** runs the real WorldServer + client on a netcup Windows VM, deploys binaries there, and
  pastes arbiter/World logs back. Terse, wants short answers, no fluff. Preference: get to the point.

## Where we are (chronological, today)

1. Original bug: Logout → relog hung; WorldServer crashed `Critical Error LeaveWorld planet[..]`.
   Root cause (from `status/HANDOFF.md` §1): World serialises per-user DB items (DLMItems); one
   `DBS_*` reply with a captured/wrong id or no reply head-blocks that user forever, including
   `UserLeaveWorld` → no `SA_LEAVE_WORLD` → our 5 s fallback fakes lobby → relog stalls at `0x1626`.
2. Fixed by making these real in `World/DbProxyHandlers.cs`, all echoing the LIVE reqId, each
   verified against `D:\packetlogs\lobby_tap.log` bytes or the Arbiter decompile:
   - `0x2899→0x289A` daily-quest seeds (+ empty 159 B `0x272D` so World takes the seed branch)
   - `0x2910→0x2911`, `0x290C→0x290D` (+ pushes `0x15B1 0x2847 0x1440 0x143E`), `0x27B9→0x27BA`
   - post-spawn `0x2736→0x2737`, `0x2930→0x2931`, `0x27B3→0x27B4`
   - `0x1562→0x1563` (`SA_CLEAR_BATTLE_FIELD_ENTER_COUNT`, daily reset, fires mid-session)
   - `World/WorldReplayTable.cs`: `0x143F` is never treated as a request (it was mis-attributing a
     stale `0x290D`).
   All in the `TryHandle` allow-list (the FIRST switch). Anything not in that list falls to replay.
3. **Logout → lobby → relog now works live** (21:13 run): full chain
   `0x13AA → 0x27FA → 0x2924 → 0x2768 → 0x2936 → 0x27CB(saved) → 0x1393 → 0x1433`, no "forcing
   delete", no World crash, relog spawns.
4. Remaining live bug at time of writing: **respawn position** was the seed default
   (-449, 6239, 1956) instead of the logout spot. Diagnosed with `blob pos` log lines added to
   `DbProxyHandlers` (blob offset 220 = x,y,z floats — diagnostic only, blob is never modified):
   logout save had the right pos, relog sent the right blob, first save after respawn = default.
   Cause: `Handlers/WorldEntry.BuildEnterWorldPayload` wrote `param_9` (payload [52]) = 0 and x/y/z
   from the `characters` row (seed default). Real Arbiter (`lobby_tap.log` pkt 127) sends
   `param_9 = 0xFFFFFFFF` and World then restores from the blob.
   **Fix applied and committed** (WorldEntry: `param_9 = 0xFFFFFFFF`, x/y/z taken from blob offset
   220 when a blob exists, old-signature overload kept for tests; 115 tests green).
   **Fix applied, committed, and LIVE-VERIFIED (21:46 run):** logout save (516.5, 7080.1, 1941.0) ==
   relog enter-world == first save after respawn. Respawn position is done.

## Tooling the human uses

- Local: `.\ship.ps1 "msg"` in the repo root = build + tests + commit + publish to
  `D:\TeraSharp-publish` + `D:\TeraSharp-bin.7z`. `npx serve -l 8888` runs from `D:\` to serve
  the 7z. (ship.ps1 may not be committed yet — check.)
- VM (netcup): `C:\deploy.ps1` (or the pasted block): wipe `C:\TeraSharp-deploy\bin`, curl
  `http://104.62.81.162:8888/TeraSharp-bin.7z`, 7z extract, set `TERASHARP_DATA=C:\TERA_SERVER.100`,
  `TERASHARP_LOGS=C:\TeraSharp-deploy\packetlogs`, run `TeraSharp.Arbiter.exe`.
- **Always restart WorldServer before a live test.** A wedged DLM queue from the previous run
  makes a correct fix look broken.
- Log lines to grep after a run: `no replay for 0x` (per-user opcode = future wedge), `forcing
  delete` (leave never completed), `blob pos`, `0x1393`.

## How to read the big files from this chat

- `D:\packetlogs\lobby_tap.log` is >1 MB; `read_text_file` fails on it whole. Use `head=N` /
  `tail=N` (each returns ≤1 MB; results land in `/mnt/user-data/tool_results/*.json`), then parse
  with the bash tool: lines are `[seq] [W->A|A->W] ts len=N` followed by a hex line; opcode is
  bytes 4–5 LE; reframe by u32 length. Windows that worked: `head=400`, `head=720`, `head=1000`,
  `tail=700`, `tail=1150`. The 02:51:40–02:52:00 window (packets ~340–1130) is unreachable that
  way; use the decompile instead.
- Decompile: `Select-String -Path "D:\v100\TERA_SERVER.100\Arb_part_*.c" -Pattern 'Handler_X\('`
  (human runs it), then you `read_text_file head=<line+80>` on that part file and print the
  window from the stored json. Reply layout = the `FUN_140350eb0(&pkt, OPCODE)` writer: u32 =
  `FUN_14013d0b0`, u8 = `FUN_1403513d0`. Request offsets are frame-relative (−6 for payload).
- `filesystem:search_files` on `D:\` times out; use `read_text_file` on known paths.

## Rules that bit us today (don't repeat)

- Log/notes claimed things the tree didn't have (a "committed" fix that was only in the working
  tree, `(1,8)` vs `(3,0)`). Always `git log` / `git status` / grep the file before trusting docs.
- PowerShell `Rep "a", "b"` passes an array → use named params (`Rep -old $a -new $b`).
- Pasting console output with `>> ` prompts into PowerShell breaks (`& not allowed`).
- DbProxy `link.SendFrame` replies are NOT logged as `A->W` — silence ≠ not sent.

## Next steps (in order)

1. (done) Respawn position verified. The `blob pos` log lines can stay; they're cheap and useful.
2. Human-owned cleanups: `GameSession` 5 s fallback → 15 s + loud log; gameId increment per
   login; `Program.cs` log level Trace → Debug.
3. Merge Cowork branches as they come (T1 tests first). Update `status/STATUS.md` (T9).
4. Then: character creation (T8), account auth (tera-api), multi-player routing (needs a
   two-login capture with `arbiter-world-tap.js`).
