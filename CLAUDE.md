# TeraSharp — Handoff for Autonomous Work

## ⚠ READ THIS BLOCK FIRST — WORKSPACE RULES (non-negotiable)

**Cowork works ONLY on its own git branch, never on `master`, never outside the repo.** Two Cowork sessions
may run at once, each in its OWN worktree (`TeraSharp-cowork` and `TeraSharp-cowork2`) - never two sessions
in one worktree. The human works on `master` and merges each branch when it's green.

1. Start every session with:
   `cd D:\v100\TERA_SERVER.100\TeraSharp; git status --short; git log --oneline -3`. The tree must be clean.
   If `git status` shows changes you did not make, STOP and tell the human — do not touch them.
2. **You cannot run git** (no shell on the human's machine). The human creates a git worktree for your task
   before you write anything: `git worktree add D:\v100\TERA_SERVER.100\TeraSharp-cowork -b cowork/Tn master`.
   **Write only under `D:\v100\TERA_SERVER.100\TeraSharp-cowork\`** (same layout as the main repo). Never
   write into `D:\v100\TERA_SERVER.100\TeraSharp\` — that is the human's `master` working tree. When done,
   tell the human which files you wrote; he commits on the branch, builds, tests, merges, removes the worktree.
   If the worktree folder does not exist, STOP and ask for it — do not write anywhere else.
3. Generated data (opcode tables, extracted bytes) goes in the worktree under `data/` or `status/`, not in
   `D:\packetlogs\` or anywhere else on D:. Never create new top-level folders on D:.
4. Only the files in the "diffs welcome" list below. Human-owned files: describe the change in chat, do not
   edit.
5. You may build/test in your own container by copying sources out, but say so explicitly — the human
   re-runs `dotnet build` + tests on his box before merging. Small, single-purpose tasks; one per worktree.
6. Before you stop: list every file you wrote (full paths), the test names you added, and the notes you did
   NOT act on. Nothing else is required of you on the git side.
7. Read the files you're changing in the same session you change them — master may have moved since the
   docs were written.

Human side (for reference):
```
cd D:\v100\TERA_SERVER.100\TeraSharp
git worktree add ..\TeraSharp-cowork -b cowork/Tn master      # before starting Cowork
# ... Cowork writes into ..\TeraSharp-cowork ...
cd ..\TeraSharp-cowork; git add -A; git commit -m "Tn: ..."; dotnet build TeraSharp.sln; dotnet run --project src\TeraSharp.Arbiter.Tests
cd ..\TeraSharp; git merge --no-ff cowork/Tn; git worktree remove ..\TeraSharp-cowork; git branch -d cowork/Tn
```
Conflicts are resolved by the human at merge time, never by Cowork.

These rules exist because four sessions in a row reported "done" with work that never reached disk, or
landed in a stray `D:\TeraSharp` copy and had to be merged by hand. Branch-only + prove-it-with-git ends that.

### Decompiles on disk (full Ghidra output, both binaries)
- **ArbiterServer.exe**: `D:\v100\TERA_SERVER.100\Arb_part_000.c` … `Arb_part_095.c` (~60 MB total). The
  spec for what WE must send. `Select-String -Path "Arb_part_*.c" -Pattern 'Handler_SDB_NAME\('`, then read a
  ~100-line window around the hit.
- **WorldServer.exe**: `D:\v100\TERA_SERVER.100\world_decompiled\WorldServer.exe.c` (ONE 125 MB file, 4.47 M
  lines). The authority for what World EXPECTS (`Handler_DBS_*`, DLM lifecycle, opcode->name switch ~line
  247000). NEVER open it whole; `Select-String -SimpleMatch` for the line, then a small windowed read.
- Both are complete. When notes and decompile disagree, the decompile wins.

### Packet definitions on disk (client protocol)
- `D:\v100\TERA_SERVER.100\tera_v100_MASTER_FINAL\` — all 4548 client packet `.def` files for protocol
  376012, one per packet (`C_*` / `S_*`), with field names and types. This is the authority for any
  client<->Arbiter packet layout; the codec in `Protocol/` is driven by these files. Opcode numbers come from
  `tera-server-proxy\data\data.json` (`maps."376012"`). Use both before decoding anything by hand.
- Arbiter<->World opcodes: `D:\packetlogs\world_opcodes.txt` (`AS_/SA_/DSA_/BSA_`) and the opcode->name switch
  in `WorldServer.exe.c` ~line 247000 (covers `SDB_/DBS_` too). There are no `.def` files for the internal
  protocol — layouts come from the decompiled writers/handlers (section 5 recipe).

---

TeraSharp is a from-scratch C# (.NET 8) replacement for TERA 100.02's `ArbiterServer.exe`.
It is **working**: a real client logs in through it, and it bridges to the **real** `WorldServer.exe`,
which drives gameplay (NPCs, combat, quests). Characters spawn in Velika with real NPCs, walk around,
and their state persists to SQLite across logins.

Read `status/STATUS.md` after this file. Everything you need is on disk. **Read the decompile, don't guess.**

---

## 0. State of play (updated 2026-09-14 ~05:30 - after the new-character milestone)

**Milestone: a brand-new character works end-to-end on a fresh WorldServer** - creation, per-class
starter kit and default skills, quests, inventory, client settings all persist through TeraSharp's SQLite.
"Everything works like it did on Arbiter" (human, live). 278 tests. Two Cowork sessions run in parallel now,
each in its own worktree (`TeraSharp-cowork` on `cowork/T8`, `TeraSharp-cowork2` on `cowork/T<n>`); the
human rebases a worktree on master before a new task if master moved. The current task queue is at the
bottom of this section; `status/CHAT-HANDOFF.md` has the day-by-day state and the live-debug recipes.

Cowork-editable files also include: `Auth/*`, `Handlers/GmCommands.cs`, `Protocol/V100Definitions.cs`,
`World/PartyManager.cs`, `World/StarterInventory.cs`, `World/DbProxyStaticData.cs` (added 2026-09-14).

### Hard rules learned the expensive way (2026-09-14)
- **No `"` inside C# verbatim strings** (`@"..."`, e.g. the SQL DDL in CharacterStore) - a lone double quote
  terminates the string and the build fails with 300+ errors. Use single quotes or no quotes in SQL comments.
  Cowork cannot build, so this has broken three merges in a row.
- **Never send a `DBS_*` reply World did not ask for.** Every DBS_ carries a DLM id World looks up; an
  unsolicited one completes whichever item currently holds that id. A pre-emptive `0x2738` with playerId in the
  id slot crashed a fresh World for every character except playerId 1 (found via minidump stack walk).
  Pushes (AS_*, no id) are fine; replies are not.
- **Every per-user W->A request must be answered** (allow-list in `DbProxyHandlers.IsHandledRequest`, or
  `WorldReplayTable.OneWayFromWorld`, or a replay entry) - the test `Every_per_user_request_opcode_is_answered`
  enforces it from `status/PERSISTENCE-MAP.md`. `no replay for 0xNNNN` right before silence is the tell.
- **Captured per-character data must never be served to other characters.** Quests (0x272D), inventory
  (0x27A4), the world blob, skills - each of these bit us as "every new character got dob's X". The remaining
  statics (achievements 0x27F9, reputation 0x2890, tutorial tips 0x2873, seren 0x2943, fatigability 0x2909,
  EP 0x27BA, dungeon history 0x2868) are Cowork T22.
- **The world blob is per-character state we DO patch at creation** (playerId, name, identity block, skills,
  position - see `StarterBlob`) and otherwise store verbatim; zone is u32@236 (208 is HP).
- The real Arbiter's DLM ids are what World looks up; the blob-derived `questDbId` (0x272F payload[25]) is
  the `quests` row id; item ids come from `counters` (starter kit ids 7.. are deterministic per character).
- Minidump analysis without WinDbg: parse streams 4/6/3 with PowerShell, scan the faulting thread's stack for
  addresses inside WorldServer.exe, map `WorldServer+0xNNN` to `FUN_1400NNN` in the decompile (recipe in
  CHAT-HANDOFF.md). Delete `*_full.dmp` (30 GB each) immediately.

### Status by area
| Area | Status |
|---|---|
| Client crypto/codec/login/char list/select/create/delete | real |
| Chat, client settings (persisted), social lists | real |
| World handshake + 0x147D promotion records (live timestamps) + 0x1581 burst | real |
| Enter-world, blob save/load, restriction, gameId per login | real, live-verified |
| Per-user DB writes during play (T15), quests (T17), inventory (T20), skills in blob (T18) | real |
| Relog into an instance (0x138D -> retry at stored return point) | implemented (T21), **live test pending** |
| Zone change / quest teleport (0x13BE/0x13C0 echoes) | real, live-verified (via T21 capture) |
| Remaining per-character statics (achievements, reputation, tips, seren, fatigability, EP, dungeon history) | replayed from dob - T22 |
| Post-handshake ~64-push config burst | not sent explicitly - T23 |
| Multiple players | blocked on a 2-login capture |
| Account auth | accept-all |

### Cowork task queue (current)
- [x] T1-T21 done and merged (see git log).
- [ ] **T22** - per-character loads from rows (achievements first). Prompt in CHAT-HANDOFF.md.
- [ ] **T23** - post-handshake config burst as a real step + 0x15ED/0x295D replies.
- [ ] Exit countdown display: `S_PREPARE_EXIT` not in the def registry (human-owned HandlerRegistry).
- [ ] Continent fallback table for characters with no stored return point (ENTER-WORLD-FALLBACK.md section 9).
- [ ] Two-login capture -> multi-player tunnel routing.

---

## 0-old. State of play (updated after session 4 + live testing) - kept for history

**The repo is under git.** Cowork commits only on `cowork/*` branches; the human merges. Start every session
with `git status` (clean) and `git log --oneline -3`.

### FILE OWNERSHIP
This list says which files Cowork may **edit on its branch** and which it may only **describe changes to**.
The live network path is tested only against the real WorldServer + client, which Cowork cannot run; unit
tests pass in isolation while the live path breaks.

- **Editable on a `cowork/*` branch (pure logic, unit-testable, no live-behavior risk):**
  `World/DbProxyHandlers.cs`, `World/DbProxyStaticData.cs`, `World/WorldReplayTable.cs`,
  `Persistence/CharacterStore.cs`, `Handlers/CharacterHandlers.cs`, `Handlers/SocialHandlers.cs`,
  `Handlers/ChatHandlers.cs`, `Protocol/*` (codec), `src/TeraSharp.Arbiter.Tests/`, `status/*.md`.
- **Describe only, never edit (live path — the human owns these, verified on the server):**
  `World/WorldBridge.cs`, `Network/*` (GameSession, TcpServer, PacketDispatcher, Crypto),
  `Handlers/WorldEntry.cs`, `Handlers/HandlerRegistry.cs`, `Handlers/LoginHandlers.cs`, `Program.cs`.

### Live-verified truths (supersede everything below; do not "fix" these)
- **Login works end-to-end** against the real WorldServer: character spawns in Velika with real NPCs, and the
  world blob persists across logins (SQLite). Verified live.
- **The WorldServer decompile is now on disk** at `D:\v100\TERA_SERVER.100\world_decompiled\WorldServer.exe.c`
  (ONE 125 MB file). NEVER open it whole (`cat`/Get-Content -Raw will hang). Use targeted line reads with a
  StreamReader or `Select-String -SimpleMatch <name>` to get line numbers, then read a small window. This is
  the authority for anything World does (enter, leave, spawn, DB-proxy expectations) — `Arb_part_*.c` is the
  Arbiter only.
- **Logout -> lobby -> relog WORKS (live-verified 2026-09-13, 21:13 run).** Root cause of every "hangs on
  relog / Critical Error LeaveWorld" was the same thing: a per-user WorldServer **DLMItem head-block**
  (read `status/HANDOFF.md` §1). World serialises every per-user DB message; one `DBS_*` reply that carries a
  stale/captured id (or never arrives) silently blocks every later item for that user, including
  `UserLeaveWorld` — so `SA_LEAVE_WORLD` never comes, our 5 s fallback fakes the lobby return, and the next
  `C_SELECT_USER` stalls at `0x1626` because World still has the character. Fixed by making these real, all
  echoing the LIVE reqId, in `DbProxyHandlers` (each verified against `lobby_tap.log` bytes or the decompile):
  `0x2899` seeds (+ empty 159 B `0x272D` so World takes the seed branch), `0x2910`, `0x290C` (+ pushes
  `0x15B1 0x2847 0x1440 0x143E`), `0x27B9`, `0x2736`, `0x2930`, `0x27B3`, `0x1562`, plus the 5 logout saves;
  and `WorldReplayTable` no longer treats `0x143F` as a request. **Any new `no replay for 0xNNNN` on a
  per-user opcode is a wedge waiting to happen — make it a real handler with live id echo.**
- **`AS_LEAVE_WORLD` (0x1392) = `[u64 gameId][u32 type][u32 reason][u32 playerId]`**; lobby = (3,0),
  disconnect = (1,0) (both from `lobby_tap.log`; older capture's disconnect was (1,8)). `LeaveValues` is (3,0)
  for lobby. The type/reason are only read once `UserLeaveWorld` reaches the head of the DLM queue — they were
  never the blocker.
- **Login-time `SDB_*`: replay is fine when the reply carries no live id; anything that echoes a DLM id must be
  a real handler.** The earlier "synthetic empty list desynced World" lesson still stands for data-bearing
  replies (0x27A2 returns a 3235 B item list) — never replace those with constructed empties. But the replay
  table's id-echo can only patch ids it can find in BOTH captured request and response; where it can't
  (0x290D after 0x143F, 0x2737 after 0x15AE) the replayed reply goes out with the CAPTURED id and wedges the
  user. The `TryHandle` allow-list (first switch) is the source of truth for what is real.
- **Single-player tunnel fast path is in `WorldBridge.HandleFrame`:** with exactly one registered session the
  tunnel ignores the routing key and uses one shared reorder queue (the broadcast behavior that worked for
  login and logout). The per-key routing a session added stalled the logout despawn burst. Multi-player routing
  stays deferred until a real two-login capture exists.

### Repo is under git
Human workflow per Cowork branch: review `git diff master..cowork/Tn`, build, tests, live test if it touches
a handler, `git merge --no-ff cowork/Tn` — or `git branch -D cowork/Tn`.

### Known open items
- **Respawn position.** After relog the character spawns at the captured default (-449, 6239, 1956), not where
  it logged out. The real server restores from the blob (blob offset 220 = x,y,z floats; `lobby_tap.log` 2nd
  login spawned at the 1st session's logout pos while `AS_ENTER_WORLD` was byte-identical). `DbProxyHandlers`
  now logs `blob pos` on every save and on enter-world; the next live run decides whether World isn't writing
  the position into the blob on our side (suspect: missing `0x2830` after `0x2738`, STATUS §3c) or ignores it.
- 5 s `SA_LEAVE_WORLD` fallback in `GameSession` should be 15 s and log loudly (human-owned).
- gameId should increment per login (real: `...0001` then `...0002`); we reuse `...0001` (human-owned).
- Log level is `Trace` in Program.cs — back to `Debug` (human-owned).

---

## Cowork task queue (safe, self-contained, all inside the editable set)

Pick the first unchecked task. Each one: decompile/capture first, unit test against captured bytes, commits
on `cowork/Tn`, then paste `git log --oneline master..HEAD` + `git diff --stat master..HEAD` + test output in
chat. If a task needs a human-owned file, describe the exact change in chat instead of editing it.

- [x] **T1 — Tests for the DLM-unblock handlers.** DONE (branch cowork/T1, +18 tests).
- [ ] **T2 — Opcode names for `0x27xx–0x29xx`.** Generate `data/dbproxy_opcodes.txt` in the repo (`0xNNNN|NAME`)
  from the `case 0xNNNN: return "DBS_/SDB_...";` switch in `WorldServer.exe.c` (~line 247000; use
  `Select-String`, never open the file whole). Then add a tiny `DbProxyOpcodeNames` lookup in
  `World/DbProxyStaticData.cs` (or a new file in `World/`) that `DbProxyHandlers` uses in its log lines.
  Pure data, no live behaviour.
- [x] **T3 — `0x2830 DBS_USER_RESTRICTION` after `0x2738`.** DONE on master (DbProxyHandlers.OnUserEnterWorld,
  `BuildDbsUserRestriction`). Still wants a byte-exact test against `lobby_tap.log` pkt 131.
- [ ] **T4 — `0x15E0 AS_REQUEST_DUNGEON_PHASE_USER_RESET` push.** Real Arbiter sends `0x15E0`
  `[u32 playerId][u32 0][u64 timestamp]` right before `0x286A` (`lobby_tap.log` packet 173). We never send it.
  Decide from the decompile whether it belongs in the `0x2869` handler (then allow-list `0x2869` — its builder
  `Build286A_EmptyListTimestamp` already exists; verify the timestamp field against the capture first).
- [ ] **T5 — Replay-table hygiene.** In `World/WorldReplayTable.cs`, add an explicit set of one-way World
  opcodes that must never become request entries: `0x1436 0x15A8 0x159A 0x2958 0x13FA 0x13CC 0x1626 0x1441
  0x143F 0x15B5 0x13AA 0x13F2 0x13E5 0x164D 0x293E`. Same "seal pending, skip" treatment as `0x143F`. Add a
  test. Kills the `no replay for` noise and prevents mis-attribution.
- [ ] **T6 — Per-session position from the blob (read-only).** Extend `CharacterStore.SaveWorldBlob` to also
  update `zone/x/y/z` on the `characters` row from blob offsets **236 (u32 zone)**, 220/224/228 (x,y,z) so the
  character-select screen and future `AS_ENTER_WORLD` builder have real data. Do NOT modify the blob.
  (208 is HP, not zone — verified in cap_newchar.log by T8.)
- [ ] **T7 — Remaining post-spawn / periodic `SDB_*`.** Grep `lobby_tap.log` for every W->A opcode in
  `0x27xx–0x29xx` and `0x13xx–0x16xx` that is not in the `TryHandle` allow-list, list them in STATUS.md with
  the reply layout from the decompile, and implement the ones that are pure ack/echo (`[reqId][ok]`,
  `[ok][reqId]`). Skip anything data-bearing.
- [x] **T8 — Character creation.** DONE (cowork/T8): `CharacterHandlers` + `StarterBlob` in `CharacterStore`;
  starter blob from `data/starter_blob.bin` (must be shipped: `ship.ps1` copies it to `publish/data/`,
  `TERASHARP_STARTER_BLOB` env on the VM). Every class starts at zone 5 (16260, 1253, -4410) until a per-class
  start table exists. Open: deferred delete timer; `LoginHandlers` hardcodes `maxCharacters=3`,
  `isNewCharacter=true` (human-owned); gameId still derived from playerId (human-owned).
- [ ] **T9 — Docs.** Update `status/STATUS.md` §0–§1 to reflect that logout/relog is fixed, list the handlers
  above as real, and move the remaining human-owned items (3a is done; 3b done; 3c=T3; 3d, 3e still open) into
  a short "human TODO" list.

---

## Reference: earlier task list (most of this is DONE — see "Real vs. replayed" and STATUS.md)

---

## 1. Layout

```
D:\v100\TERA_SERVER.100\
  TeraSharp\
    TeraSharp.sln
    STATUS.md                           previous session's report — read it
    src\TeraSharp.Arbiter\              the server
      Program.cs                        entry; env TERASHARP_DATA / TERASHARP_LOGS / TERASHARP_DB; seeds DB
      Network\  GameSession.cs          per-client: crypto, framing, tunnel hooks, leave state machine
                PacketDispatcher.cs     client opcode -> handler; unmapped in-world packets forward to World
                TcpServer.cs
                Crypto\                 SHA-0 / 3-block cryptor (DONE, don't touch)
      Protocol\ DefinitionParser/Reader/Writer.cs   client packet codec driven by .def files (DONE)
                OpcodeTable.cs          client opcodes from tera-server-proxy data.json
      Handlers\ LoginHandlers.cs        C_LOGIN_ARBITER, C_GET_USER_LIST, C_SELECT_USER (rejects if World not ready)
                WorldEntry.cs           hands a session to WorldServer (AS_ENTER_WORLD + settings blobs)
                HandlerRegistry.cs      all client handler registrations
                ChatHandlers.cs         C_CHAT -> S_CHAT, "!" commands
                ClientSettingsHandlers.cs  replayed UI/chat/user settings blobs (Arbiter-owned)
                InventoryHandlers.cs    standalone-mode only
                SpawnReplay.cs          standalone-mode only
      World\    WorldBridge.cs          :7802, 25 links, tunnel+reorder, control msgs, IsReady, leave, player registry
                WorldReplayTable.cs     captured World request->response table with request-id echo
                DbProxyHandlers.cs      REAL: enter-world, update-user-data, quest list/seeds, the post-seed and
                                        post-spawn DLM items, the 5 logout saves, 0x1562 (see section 0)
      Persistence\ CharacterStore.cs    SQLite: accounts, characters (+ 15312-byte world_blob)
      Game\     FakeAccount.cs          session view of account/characters, loaded from store
    src\TeraSharp.Arbiter.Tests\        console test runner (see section 0)
  tera_v100_MASTER_FINAL\               4548 client packet .def files
  Arb_part_000.c .. Arb_part_095.c      DECOMPILED ArbiterServer.exe -- the spec. grep it.
  tera-server-proxy\data\data.json      client opcode map (maps."376012")
  tera-api\                             Node auth/API server (for task E)

D:\packetlogs\
  arb_world.log            raw Arbiter<->World tap (TCP chunks; one login + one DISCONNECT logout)
  full_capture.log         client<->Arbiter capture of the real server (has C_CREATE_USER)
  world_opcodes.txt        2705 lines "0xNNNN|NAME" for AS_/SA_/DSA_/BSA_ (Arb_part_003.c case table)
  dbproxy_names.txt        672 SDB_/DBS_ names (no opcodes — set inline; section 5)
  terasharp.db             SQLite (auto-created; seeded with "dob" id=1 account "1")
```

Build: `cd D:\v100\TERA_SERVER.100\TeraSharp && dotnet build TeraSharp.sln`
Tests: `dotnet run --project src/TeraSharp.Arbiter.Tests`
Publish (human does deploy): `dotnet publish src/TeraSharp.Arbiter -c Release -r win-x64 --self-contained -o D:\TeraSharp-publish`

---

## 2. Architecture in one paragraph

Client <-> **TeraSharp** (:7701, encrypted client protocol) <-> **WorldServer** (:7802, plaintext internal protocol).
TeraSharp owns: auth, character list/select, chat, settings, social. WorldServer owns: the game.
After `C_SELECT_USER`, TeraSharp sends `AS_ENTER_WORLD`; World asks for the character blob (`SDB_USER_ENTERWORLD`),
we answer from SQLite; then TeraSharp is a **tunnel**: World->client packets arrive in `SA_BYPASS_TO_CLIENT`
(0x13F7), client->World go out in `AS_BYPASS_FROM_CLIENT` (0x13F6). World persists through **672 DB-proxy
messages** (`SDB_X` from World -> `DBS_X` from us). That DB-proxy layer is most of the remaining work.

---

## 3. Protocol facts (verified against captures + decompile)

### Client protocol (done)
- Frame `[u16 len][u16 opcode][body]`, len includes header. Encrypted after key exchange.
- `.def`-driven codec: ref-header block first (explicit `ref` order, else field order), then fixed fields,
  then variable data. string=offset(u16); bytes=offset(u16)+count(u16); array=count(u16)+offset(u16).
  Offsets are packet-relative. Field C# types must match the def (float vs int, ushort vs int).
- `GameSession.Send` is locked (tunnel packets arrive on 25 threads; cipher is stateful).

### Arbiter<->World protocol
- Frame `[u32 len][u16 opcode][payload]`, len includes 6-byte header. **Plaintext. Offsets inside messages are
  frame-relative (count the 6-byte header).**
- World opens **25 links**: 1 main (`SA_REGISTER` worker -1) + 24 workers. Control messages on main (`_links[0]`).
  Tunnel packets round-robin across all links.
- Startup handshake (replayed static config): `0x27CF->0x27ED`, `SA_LOAD_GUARD 0x147D -> 0x147E x23 + 0x1480`,
  `0x1558->0x1559`, `0x28A9->0x28AA`, `0x1592->0x1595+0x1582`, `0x159E->0x159F`, `0x144C` (55KB, one-way),
  `SA_REGISTER 0x138A -> AS_REGISTER 0x138B + 0x2801` x25, `0x294E->0x2953`, `0x29C0->0x29C1`, `0x294F->0x2955+0x2952`.
  `WorldBridge.IsReady` flips on `0x294F`. `C_SELECT_USER` before ready is rejected (`S_SYSTEM_MESSAGE @769`
  + `S_SELECT_USER unk1=0`), as the real Arbiter does.
- One-way periodic from World (ignore): `0x164D`, `0x1436`, `0x15A8`, `0x13FA`, `0x13F2`/`0x13E5`, `0x159A`, `0x293E`, `0x13AA`.

### Tunnel
- `SA_BYPASS_TO_CLIENT` 0x13F7 payload: 32-byte header then raw client packet.
  `[0]u32=22 [4]u32=16 [8]u32 off=38 [12]u32 clientLen [16]u32 serverId [20]u32 conn [24]u32 idx [28]u32 seqField`.
  **seq = `u16 at payload[30] >> 3`**; reorder by it (done; watchdog skips after 150 ms).
- `AS_BYPASS_FROM_CLIENT` 0x13F6 payload: `[0]u32 off=30 [4]u32 clientLen [8]u64 gameId [16]u64 tickMs` then packet.

### Player lifecycle (control messages on main link)
| Step | Message | Payload |
|---|---|---|
| select | `AS_ENTER_WORLD` 0x138E (189 B) | replayed, gameId patched — **task B makes this real** |
| World asks | `SDB_USER_ENTERWORLD` 0x2711 | `[6]u32 nameOff [10]u32 replyId [14]u32 playerId [18]u32 unk [22]wstr name` |
| we reply | `DBS_USER_ENTERWORLD` 0x2738 | `[6]u32 off=19 [10]u32 blobLen [14]u32 replyId [18]u8 found [19]blob(15312)` |
| World confirms | `SA_ENTER_WORLD` 0x138C | one-way |
| ~40 queries | `SDB_*` | replayed with id echo — **task A makes these real** |
| client loaded | `C_LOAD_TOPO_FIN` -> `AS_FORCE_ENTER_DUNGEON_ID` 0x1390 `[1][0]` + `AS_LOAD_TOPO_FIN` 0x138F `[u32 playerId]` | World spawns |
| spawned | `SA_USER_ON_SPAWN_COMPLETE` 0x15AE -> `0x2737` | replayed |
| saves | `SDB_UPDATE_USER_DATA` 0x27CB | `[6]u32 blobOff=31 [10]u32 blobLen [14]u32 trOff [18]u32 trLen [22]u32 reqId [26]u32 playerId [30]u8 [31]blob` -> `0x27CC [6]u32 reqId [10]u32 1` |
| lobby/exit | see section 0 | real (STATUS.md) |

**The world blob is a fixed 15312-byte (0x3BD0) opaque struct.** Never parse it. `0x1439` is
`AS_UPDATE_VISITED_SECTION_LIST`, not a spawn trigger.

---

## 4. Real vs. replayed

| Area | Status |
|---|---|
| Client crypto/codec/login/char list/select | real |
| Chat | real (settings blobs sent by `WorldEntry`) |
| Client settings blobs | replayed bytes (client defaults — fine) |
| World handshake | replayed (static — fine) |
| `AS_ENTER_WORLD` | replayed with gameId patch — task B |
| Enter-world, update-user-data, quest list + 17 seeds, post-seed/post-spawn items, 5 logout saves, 0x1562 | **real + live-verified** |
| ~30 other login-time `SDB_*` (no live id in reply) | replayed with id echo — fine, convert only if a wedge appears |
| Logout/exit/cancel | **real + live-verified** (relog works; respawn position open, see section 0) |
| Char create/delete | not implemented — task C |
| Account auth | accept-all — task E |
| Multiple players | blocked on 2-login capture |
| Guild/party/friends/mail | not implemented |

---

## 5. Implementing a DB-proxy handler (recipe)

`0x27xx-0x29xx` opcodes are set inline by writers, not in a case table. Names in `dbproxy_names.txt`.

**A.** Find the handler: `Select-String -Path "Arb_part_*.c" -Pattern 'Handler_SDB_NAME\('`.
**B.** Request layout: reads like `*(int *)((longlong)piVar + 0xNN)` — `piVar` is the **frame start**, `0xNN` a
frame offset. Strings: an earlier u32 holds the offset to a null-terminated wstr.
**C.** Reply: the `SendToSession<struct PDL::PKT_DBS_NAME_WRITE, T1, T2...>` string lists field types in order;
the writer it calls does `FUN_140350eb0(&pkt, OPCODE)` (opcode), `FUN_14013d0b0` (u32), `FUN_1403513d0` (u8),
`FUN_1403c98b0(&pkt, len, ptr)` (raw bytes); `*local = *pos` after writes is an offset backpatch.
**D.** Reply opcode is the `0xNNNN` in that writer. Request opcode is what World sent.
**E.** Ground-truth bytes: `arb_world.log` is TCP chunks — reframe by u32 length (see `WorldReplayTable.Load`).
**F.** Implement in `DbProxyHandlers.TryHandle` with `U32(p, frameOff)`.
**G.** Test in `TeraSharp.Arbiter.Tests`: feed captured request payload with a fake `WorldLink`, byte-compare
to the captured response (ignoring id/timestamp fields). See existing tests for the pattern.

**Worked examples:** `SDB_USER_ENTERWORLD` (`DbProxyHandlers.OnUserEnterWorld`; decompile Arb_part_064.c
~14630–14690, writer `FUN_1407ada00` Arb_part_066.c:17716) and the five logout saves added in session 2.

**Request-id echo (stopgap in `WorldReplayTable`):** copies a u32 present in both captured request and
response into the replayed reply. It **cannot** patch gameIds (values > 10M are filtered) — anything carrying
a gameId must be a real handler.

---

## 6. Gotchas that cost hours

- PowerShell `Set-Content` strips UTF-8 BOM; TERA XML needs it (`[IO.File]::WriteAllText(p, t, UTF8Encoding(true))`).
- PowerShell `.Replace()` patches fail silently on whitespace mismatch. **Verify with `Select-String` after patching.**
- `arb_world.log` is TCP chunks; one chunk can hold several frames, a large frame can span chunks. Reframe.
- `0x27CB` and `0x2738` both carry the 15312-byte blob with different leading fields (31 vs 19 bytes).
- Arbiter-owned client packets, never forward to World: `C_LOAD_TOPO_FIN`, `C_CHAT`, `C_REQUEST_CLIENT_*_SETTING`,
  `C_RETURN_TO_LOBBY`, `C_CANCEL_RETURN_TO_LOBBY`, `C_EXIT`. World logs `handler has not been implemented yet!!! <op>`
  when one leaks through.
- `S_SELECT_USER` must go to the client before the World handoff.
- Replay-table attribution: a World frame followed by an Arbiter frame is not always request/response.
  Client-triggered opcodes are excluded in `WorldReplayTable`; keep that list current. **A replayed `DBS_*`
  attributed to the wrong request goes out with the captured DLM id and wedges the user forever** (that was
  0x290D-after-0x143F and 0x2737-after-0x15AE). Restart WorldServer before every live test — a wedged queue
  from the previous run makes a correct fix look broken.
- Seeded character "dob" (id 1). The name is inside the blob; don't rename it.
- The client's "TERA / Battle Arena" picker after server select is the 100.02 client, not fixable server-side.
- Don't "fix" things the decompile contradicts. Two earlier assumptions (0x1460 unnecessary; gameId derivable)
  were wrong and cost time. When the notes and the decompile disagree, the decompile wins — and update the notes.
