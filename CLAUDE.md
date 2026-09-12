# TeraSharp — Handoff for Autonomous Work

TeraSharp is a from-scratch C# (.NET 8) replacement for TERA 100.02's `ArbiterServer.exe`.
It is **working**: a real client logs in through it, and it bridges to the **real** `WorldServer.exe`,
which drives gameplay (NPCs, combat, quests). Characters spawn in Velika with real NPCs, walk around,
and their state persists to SQLite across logins.

Read `STATUS.md` after this file — it's the previous session's report and supersedes anything here that
conflicts. Everything you need is on disk. **Read the decompile, don't guess from packet timing.**

---

## 0. State of play (updated after session 3 — see status/STATUS.md)

**Sessions 2–3 are merged into `D:\v100\TERA_SERVER.100\TeraSharp` and verified: `dotnet build TeraSharp.sln`
clean, `dotnet run --project src/TeraSharp.Arbiter.Tests` → 76 passed.** Nothing has been run live yet.

**IMPORTANT for Cowork sessions:** previous sessions edited a cloud workspace and only wrote back on request.
Before starting, print the absolute path you're editing and confirm it's `D:\v100\TERA_SERVER.100\TeraSharp`.
After every task, run the build and test runner *on D:\* and confirm the count increased. If files only exist
in your workspace, write them to D:\ immediately. `InternalsVisibleTo("TeraSharp.Arbiter.Tests")` is in the
main csproj — keep test-facing helpers `internal`, not `public`.

Done and tested (byte-exact vs capture): logout/exit/cancel flow; all login-time `SDB_*` handlers real
(replay table no longer serves any login-sequence request); `AS_ENTER_WORLD` built from `CharacterRecord`;
character create/delete/name-check; `S_PREPARE_EXIT`/`S_EXIT` inline defs; auth gate (`TERASHARP_AUTH`,
default off). Read `status/STATUS.md` for the per-task details and the unresolved decompile facts.

**Blocked on the human (live server):** logout acceptance test, create-a-character test (does World initialise
a fresh character from `DBS_USER_ENTERWORLD found=0`?), auth-reject test, and the two-client capture for task 2.

**Corrections settled by the decompile (don't undo):**
  - `AS_CANCEL_SKILL_STRICTLY` (0x1460) **is** sent before every `AS_LEAVE_WORLD`.
  - `AS_LEAVE_WORLD` (0x1392) = `[u64 gameId][u32 leaveWorldType][u32 logoutReason][u32 playerId]`;
    lobby = (3, 0), exit = (1, 8).
  - Lobby flow: `AS_USER_REQUEST_EXIT` (0x14FF `[u32 playerId]`) at button press → `S_PREPARE_RETURN_TO_LOBBY` →
    countdown → 0x1460 + 0x1392 → World saves → `SA_LEAVE_WORLD` (0x1393) → `AS_ARBITER_USER_DELETE`
    (0x1433 `[gameId][gameId]`, live gameId) → `S_RETURN_TO_LOBBY`. Cancel = `AS_USER_CANCEL_REQUEST_EXIT` (0x1500).
  - gameId low part is assigned by the Arbiter (capture: playerId 1, gameId low byte 6). Never derive it from
    characterId in World-facing messages; enter/leave must simply agree.
  - `0x13AA` = `SA_DEL_FROM_INTER_PARTY_MATCH_POOL`, no reply owed.
- Test project: `src/TeraSharp.Arbiter.Tests` — a **console runner** (nuget unreachable from the build box; no
  xUnit). Exits non-zero on failure. One `[Test]` per handler against bytes from `D:\packetlogs\arb_world.log`.

## What to do this session (no live server — build + test only)

Work in order. Build after every change; run the test runner before you stop.

**G. Second-pass hardening of what exists.**
- Every `DbProxyHandlers` case: confirm the request-id offset is read from the decompile handler, not inferred
  from the capture. Where a handler currently returns a static template (TUTORIAL, REPUTATION, QUEST_LIST,
  ACHIEVEMENT, FATIGABILITY, SEREN_GUIDE, GUILD_SEARCH, 0x293B), read the decompiled writer and document the
  field layout in a comment above the template so the next person can make it dynamic. Don't make them dynamic
  yet — just document.
- `WorldEntry.BuildEnterWorldPayload`: the two u64s at frame 22..37 are timestamps in the capture. Determine
  from the writer what they are (login time? last logout?) and fill them from real values. Same for the u32s
  at 50 (`7E F9 02 00` = 194942) and 78..89 (20446, 2000, 5).
- Remove the stale `0x27CC` from the `0x1507` replay entry (it now has a real handler) — check
  `WorldReplayTable` still needs to exist at all for the login path; if only the startup handshake uses it,
  say so in STATUS.md.

**H. Multi-player groundwork that doesn't need the 2-login capture.**
- Per-session reorder buffers in `WorldBridge` (currently one global seq/buffer). Key them by whatever
  `RegisterPlayer` uses. Keep the single-player path byte-identical (tests must still pass).
- `OnTunnelToClient` → `RouteToClient(key, packet)` with a fallback to broadcast when the key is unknown.
  Leave the *choice* of key (`conn` vs `idx` at 13F7 header [20]/[24]) as a single constant with a comment; the
  human's capture will settle it. Add a test that two registered sessions with different keys each receive only
  their own packets.
- `AS_BYPASS_FROM_CLIENT` already carries the gameId; confirm every C→W path uses the session's gameId.

**I. Social systems the Arbiter owns — read-only first.**
- Friends: `C_FRIEND_LIST`/`S_FRIEND_LIST`, `C_ADD_FRIEND`, `C_DEL_FRIEND`, block list. Currently
  `WorldEntry` sends hardcoded `S_FRIEND_LIST`/`S_USER_BLOCK_LIST`/`S_FRIEND_GROUP_LIST` bytes. Back them with
  tables in `CharacterStore` (`friends(character_id, friend_id)`, `blocks`) and generate the packets via the
  defs. Keep the sent bytes identical for an empty list (test).
- Whisper: `C_WHISPER` → `S_WHISPER` to the target session by name, `S_WHISPER_ERROR` if offline. Needs a
  name→session registry (`Program.Sessions` or on `WorldBridge`). Test with two fake sessions.
- Do NOT start guilds or parties. Party state drives World via `AS_DO_*_PARTY` and needs live verification.

**J. Chat channels.** `C_CHAT` already handles say. Add channel routing for `S_CHAT` by `channel` field
(say=0 to nearby — leave to World; global/trade/whisper/party are Arbiter-owned). Global chat = broadcast to all
in-world sessions. Read `Handler_C_CHAT` in the decompile for the channel enum and what gets forwarded to World
vs handled locally; document the enum in `ChatHandlers.cs`.

When you stop: **replace** `status/STATUS.md` with what changed, what's verified by test vs compiled only,
what's blocked on the human, and any decompile facts you couldn't resolve. Then run the build + tests on
`D:\` one final time and put the counts in STATUS.md.

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
                DbProxyHandlers.cs      REAL: enter-world, update-user-data, daily-quest-count, the 5 logout saves
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
| Enter-world, update-user-data, daily-quest-count, 5 logout saves | **real + tested** |
| ~40 other login-time `SDB_*` | replayed with id echo — task A |
| Logout/exit/cancel | real + tested, pending live run |
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
  Client-triggered opcodes are excluded in `WorldReplayTable`; keep that list current.
- Seeded character "dob" (id 1). The name is inside the blob; don't rename it.
- The client's "TERA / Battle Arena" picker after server select is the 100.02 client, not fixable server-side.
- Don't "fix" things the decompile contradicts. Two earlier assumptions (0x1460 unnecessary; gameId derivable)
  were wrong and cost time. When the notes and the decompile disagree, the decompile wins — and update the notes.
