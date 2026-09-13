# TeraSharp — Handoff

Read this, then `status/STATUS.md`, then `CLAUDE.md` (the workspace rules at the top of CLAUDE.md
are non-negotiable).

---

## 0. What this project is

**Goal: rebuild TERA 100.02's `ArbiterServer.exe` from scratch in C# (.NET 8).**

The retail TERA server stack is a set of C++ binaries. `ArbiterServer.exe` is the one that owns
everything *around* gameplay — client connections, crypto, auth, the character list, character
select, chat, social, settings — and it acts as the **database proxy** for `WorldServer.exe`, which
owns the actual game (NPCs, combat, quests, movement).

TeraSharp replaces only ArbiterServer. `WorldServer.exe` and the rest of the retail stack stay as
they are. So TeraSharp has to speak two protocols perfectly:

```
   TERA client  <--- encrypted client protocol, :7701 --->  TeraSharp
                                                                |
                                     plaintext internal protocol, :7802
                                                                |
                                                          WorldServer.exe
```

It already works end to end: a real client logs in through TeraSharp, the character spawns in Velika
with real NPCs, walks around, and state persists to SQLite across logins. The remaining work is
replacing the pieces that are still **replayed from a packet capture** with real implementations —
above all the **DB-proxy layer**: 672 `SDB_*` messages World sends us, each expecting a `DBS_*` reply.

There is no server-side source code. Everything is reconstructed from decompiles and captures.

---

## 1. The critical mental model: DLMItems

Almost every hard bug in this project has been the same bug wearing a different hat. Understand this
or you will waste days.

WorldServer wraps every database operation in a **DLMItem** (`DLManager` = its DB layer manager).
The lifecycle, with `WorldServer.exe.c` line numbers:

```
1. Someone constructs a context (DBUpdateUserData, UserLeaveWorld, DBSetDailyQuestSeed, ...).
   The ctor calls DLMItem::AddToLockObject(gameId)   (FUN_141327d70, 3500208)
     -> appends the user's gameId to the item's lock list and does item+0x68 += 1

2. FUN_1411adfa0 (3233908) "push":
     item->Validate()                     false -> item DROPPED, never runs, no SDB_ on the wire
     DLMItem::CheckGameObjectRestrction   true  -> item DROPPED  (tutorial-user flag, 3505993)
     -> posts DLManager::Push onto DLManager's own job queue

3. DLManager::Push (FUN_140378460, 637024):
     appends the item to a per-game-object deque at DLManager+0x70, decrements item+0x68 for each
     deque where it is ALREADY at the head; then DLMExistManager::Insert stamps
     item+0x60 = ++counter   <-- a per-World-PROCESS monotonic id, starting at 1

4. DLManager::Execute (FUN_1402d18b0, 534211):
     if (item+0x68 != 0)                          -> WAIT silently   (head-blocked)
     if (!ArbiterSessionManager::IsOnline(+0x64))  -> return silently (item stuck forever)
     if (!item->Validate())                        -> return silently (item stuck forever)
     -> BeginTransaction -> ExecuteTransaction  <-- ONLY here does SDB_ reach the wire

5. Our DBS_ reply arrives. Handler_DBS_* does:
     item = DLMExistManager::Find(u32 at frame+6 ... offset varies per opcode)
     if (item == 0) { return false; }   <-- NOTHING HAPPENS, EVER
     ReceiveFromArbiter(item, okByte)   (FUN_1411ca7a0, 3254039)
       ok==0 -> OnFail, ok!=0 -> OnSuccess, then EITHER WAY posts the pop
     DLMItem::CompleteMyself (FUN_1413369d0, 3510311) -> releases the lock, next item runs
```

### Consequences you must internalise

- **Per-user DB messages are strictly serialised.** World sends the next `SDB_*` only after our
  `DBS_*` for the previous one. You can see this plainly in any capture: request, reply, request,
  reply, never two requests in flight.
- **One unanswered or mis-answered `DBS_*` head-blocks that user forever.** Every later per-user DB
  message — the periodic world-blob save, the logout saves, and `UserLeaveWorld` itself (which is
  what emits `SA_LEAVE_WORLD`) — silently never executes. Non-DB traffic keeps flowing, so the
  server looks alive. Only a **World restart** clears it (the id counter resets to 1).
- **"No SDB_ on the wire" does not mean "World never created the item."** It almost always means the
  item is queued and blocked. Do not go looking for a missing trigger; go looking for the last DB
  message that got a reply and ask what the reply did wrong.
- **ok=0 vs ok=1 is not "error vs success".** Both complete the item. They select `OnFail` vs
  `OnSuccess`, which are often two legitimate branches (e.g. `DBCheckDailyAttendance::OnFail` at
  1151799 is where the "already attended" answer is applied). Copy the real Arbiter's value.
- **The DLM id is a per-process counter, not per-session.** It keeps climbing across logins
  (`lobby_tap.log`: login #1 ends at 0x4D, login #2's enter-world is 0x50). Any reply that echoes a
  **captured** id instead of the **live** one will miss `DLMExistManager::Find` on every session
  except, by coincidence, the first one after a fresh World start. That is why bugs here look like
  "works once, hangs on relog, fixed by restarting World".

---

## 2. Layout

```
D:\v100\TERA_SERVER.100\
  TeraSharp\                         <- THE REPO. git lives here. This is the only correct tree.
    CLAUDE.md                        workspace rules + protocol facts. Read first.
    status\STATUS.md                 current state
    status\HANDOFF.md                this file
    TeraSharp.sln
    src\TeraSharp.Arbiter\
      Program.cs                     entry; env TERASHARP_DATA / TERASHARP_LOGS / TERASHARP_DB
      Network\    GameSession.cs     per-client: crypto, framing, tunnel hooks, leave state machine
                  PacketDispatcher.cs, TcpServer.cs, Crypto\ (SHA-0 + 3-block cryptor, done)
      Protocol\   Definition{Parser,Reader,Writer}.cs   .def-driven client codec (done)
                  OpcodeTable.cs     client opcodes from tera-server-proxy data.json
      Handlers\   LoginHandlers.cs, WorldEntry.cs, HandlerRegistry.cs, ChatHandlers.cs,
                  ClientSettingsHandlers.cs, CharacterHandlers.cs, SocialHandlers.cs
      World\      WorldBridge.cs     :7802, 25 links, tunnel + reorder, control msgs, leave
                  WorldReplayTable.cs  captured request->response table with request-id echo
                  DbProxyHandlers.cs   REAL DB-proxy handlers
                  DbProxyStaticData.cs captured reply templates with a live reqId patched in
      Persistence\CharacterStore.cs  SQLite: accounts, characters (+ 15312-byte world_blob)
    src\TeraSharp.Arbiter.Tests\     console test runner; `dotnet run` exits non-zero on failure

  Arb_part_000.c .. Arb_part_095.c   DECOMPILED ArbiterServer.exe (~60 MB total). The spec for
                                     what WE must do. Grep it.
  world_decompiled\WorldServer.exe.c DECOMPILED WorldServer.exe. ONE 125 MB file, 4.47 M lines.
                                     The authority for what World EXPECTS. Never open it whole.
  tera_v100_MASTER_FINAL\            4548 client packet .def files — we have the complete set
  tera-server-proxy\data\data.json   client opcode map (maps."376012")
  tera-api\                          Node auth/API server (for real account auth, not wired yet)
  arbiter-world-tap.js               the A<->W packet tap (see section 5)

D:\packetlogs\
  arb_world.log      A<->W tap: one login + one DISCONNECT logout. The replay table is built
                     from THIS file at startup. Captured 2026-09-12.
  lobby_tap.log      A<->W tap: login + working LOGOUT-BUTTON lobby return + relog + second
                     leave. Captured 2026-09-13. Newer and much more complete — prefer it.
  lobby_proxy.log    client<->server proxy log, DECRYPTED and opcode-named
  full_capture.log   older client<->Arbiter capture (has C_CREATE_USER)
  world_opcodes.txt  "0xNNNN|NAME" for AS_/SA_/DSA_/BSA_
  dbproxy_names.txt  672 SDB_/DBS_ names
  ts-logout.log      TeraSharp's own log from a failing run
```

### Decompile tricks worth knowing

- **`WorldServer.exe.c` contains a complete opcode -> name table** as a giant `switch` around line
  **247000** (`case 0x2898: return "DBS_UPDATE_DAILY_QUEST_COMPLETE_COUNT";`). This is the fastest
  way to name any opcode in either direction. Grep `case 0xNNNN:` and read the next line.
- Ghidra emits a scope tracer at the top of most functions:
  `FUN_140011960(local_XX, "void __cdecl Class::Method(args)", this)`. Grep the demangled signature
  to find a function; grep `FUN_140xxxxxx` to find its callers.
- Packet writers: `FUN_140070120(pkt, 0xNNNN)` writes the opcode (World side);
  `FUN_140350eb0(pkt, 0xNNNN)` is the Arbiter-side equivalent. The `u32` writer is `FUN_140066180`
  (World) / `FUN_14013d0b0` (Arbiter); `u8` is `FUN_1403513d0`; raw bytes `FUN_1403c98b0`.
- **All offsets inside `Arbiter<->World` messages are frame-relative** — they count the 6-byte
  `[u32 len][u16 opcode]` header. Our C# code works in *payload* offsets (frame - 6). When the
  decompile says `*(int *)((longlong)frame + 0x16)`, that is payload[16].
- Offsets written as `0` and back-patched later (`*local = *pos`) are list offsets/lengths, not data.

---

## 3. The two protocols

### Client <-> TeraSharp (:7701)
Frame `[u16 len][u16 opcode][body]`, len includes the header. Encrypted after key exchange
(SHA-0 based 3-block cryptor — **done, do not touch**). Bodies are decoded by a `.def`-driven codec:
ref-header block first (explicit `ref` order, else field order), then fixed fields, then variable
data. `string` = offset(u16); `bytes` = offset(u16)+count(u16); `array` = count(u16)+offset(u16).
Offsets are packet-relative. C# field types must match the def exactly (float vs int, ushort vs int).

### TeraSharp <-> WorldServer (:7802)
Frame `[u32 len][u16 opcode][payload]`, len includes the 6-byte header. **Plaintext.** World opens
**25 links**: 1 main (`SA_REGISTER` worker -1) + 24 workers. Control messages go on the main link
(`_links[0]`); tunnel packets round-robin across all 25.

Once the player is in world TeraSharp is a **tunnel**: World->client packets arrive wrapped in
`SA_BYPASS_TO_CLIENT` (0x13F7), client->World go out in `AS_BYPASS_FROM_CLIENT` (0x13F6).

Player lifecycle (main link):

| step | message | notes |
|---|---|---|
| select | `AS_ENTER_WORLD` 0x138E (183-byte payload) | built for real by `WorldEntry.BuildEnterWorldPayload`. gameId at payload[84], tunnelKey at payload[80] - the real Arbiter increments both per login (`…0001`->`…0002`, `0`->`1`); we pin them |
| World asks | `SDB_USER_ENTERWORLD` 0x2711 | `[6]u32 nameOff [10]u32 replyId [14]u32 playerId [18]u32 unk [22]wstr name` |
| we reply | `DBS_USER_ENTERWORLD` 0x2738 | `[6]u32 off=19 [10]u32 blobLen [14]u32 replyId [18]u8 found [19]blob(15312)` |
| we also send | `DBS_USER_RESTRICTION` 0x2830 | `[u32 off=22][u32 count=0][u64 LIVE gameId]` — the real Arbiter always sends this right after 0x2738. **We currently never send it**; unproven whether World needs it. |
| World confirms | `SA_ENTER_WORLD` 0x138C | one-way, 15350 B |
| ~40 queries | `SDB_*` | serialised; most still replayed |
| client loaded | `C_LOAD_TOPO_FIN` -> `AS_FORCE_ENTER_DUNGEON_ID` 0x1390 + `AS_LOAD_TOPO_FIN` 0x138F | main link only |
| saves | `SDB_UPDATE_USER_DATA` 0x27CB -> `0x27CC` | the 15312-byte world blob. **Never parse the blob.** |
| logout button | `AS_USER_REQUEST_EXIT` 0x14FF `[u32 playerId]` | sent at button press; `AS_USER_CANCEL_REQUEST_EXIT` 0x1500 on cancel |
| after countdown | `AS_CANCEL_SKILL_STRICTLY` 0x1460 then `AS_LEAVE_WORLD` 0x1392 | 0x1460 is part of logout, not a stray |
| World saves | 0x27FA, 0x2924, 0x2768, 0x2936, 0x27CB | then `SA_LEAVE_WORLD` 0x1393 |
| we finish | `AS_ARBITER_USER_DELETE` 0x1433 `[u64 gameId][u64 gameId]` | with the LIVE gameId |

`AS_LEAVE_WORLD` payload is `[u64 gameId][u32 leaveWorldType][u32 logoutReason][u32 playerId]`.
Observed on the wire:

| path | type | reason | source |
|---|---|---|---|
| lobby return (Logout button) | **3** | **0** | lobby_tap.log 02:51:52 |
| disconnect | 1 | 0 | lobby_tap.log 02:52:52 |
| disconnect (older capture) | 1 | 8 | arb_world.log 06:37:41 |

The real countdown between the final `0x14FF` and `0x1392` is **~10 s**, not 5.

---

## 4. Recipe: implementing a DB-proxy handler

`0x27xx`–`0x29xx` opcodes are set inline by writers, not in a case table. Names are in
`dbproxy_names.txt`, and the opcode->name switch in `WorldServer.exe.c` ~247000 is faster.

1. **Name it.** Grep `case 0xNNNN:` in `WorldServer.exe.c` ~247000.
2. **Request layout — from World's writer.** Grep `FUN_140070120(\w*,0xNNNN)` in
   `WorldServer.exe.c`; read the `FUN_140066180` (u32) calls in order. Back-patched slots written as
   `0` first are `[offset][length]` pairs. Then find the `*Context::ExecuteTransaction` that calls
   that writer and see which argument is `*(u32 *)(this + 0x60)` — **that is the DLM id**, and its
   position in the writer tells you the payload offset.
3. **Reply layout — from the real Arbiter's handler.** Grep `Handler_SDB_NAME\(` in `Arb_part_*.c`.
   Read the minimum-length check, the request offsets it reads, and then its
   `SendToSession<struct PDL::PKT_DBS_NAME_WRITE, ...>` block: `FUN_140350eb0(&pkt, 0xNNNN)` is the
   reply opcode, then `FUN_14013d0b0` = u32, `FUN_1403513d0` = u8, `FUN_1403c98b0` = raw bytes.
   **Copy the ok/success value the real Arbiter computes — do not hardcode 1.**
4. **Cross-check against World's `Handler_DBS_NAME`** in `WorldServer.exe.c`: it gives you the
   minimum frame length, the offset it reads the DLM id from (`FUN_1402fbad0(DAT_141d8e738, &out,
   *(u32*)(frame + N))`), and the ok-byte offset. If our reply does not satisfy this exactly, the
   item never completes.
5. **Ground-truth bytes.** Reframe `lobby_tap.log` / `arb_world.log` by u32 length (they are raw TCP
   chunks: one chunk can hold several frames, one frame can span chunks).
6. **Implement** in `DbProxyHandlers.TryHandle`. Add the opcode to the allow-list in the *first*
   switch or it will fall through to the replay table.
7. **Test** in `src/TeraSharp.Arbiter.Tests` against the captured bytes, including a live-reqId-echo
   assertion. Run `dotnet run --project src/TeraSharp.Arbiter.Tests`.

### Hard-won rules

- **Do not replace a working replayed reply with a synthetic empty one.** A past session did that to
  the ~40 login-time `SDB_*` and desynced World (`SDB_USER_LOAD_INVENTORY` 0x27A2 returns a
  3235-byte item list, not an empty list) and login hung. If you must change a login-time reply, use
  **real captured bytes from a real ArbiterServer**, not something you constructed.
- **Captured templates go stale.** Anything carrying a date, a gameId, or a DLM id must be patched
  from the live request, or it will work on the day it was captured and fail after.
- **Don't infer meaning from packet timing — grep the decompile.** Two earlier assumptions
  ("0x1460 is unnecessary", "gameId is derivable from characterId") were wrong and cost days.

---

## 5. Tooling: capturing packets (both sides)

We can log **both** protocols. Do this before any protocol guesswork.

### A. Arbiter <-> World (plaintext) — `arbiter-world-tap.js`

A tiny Node TCP proxy that sits between WorldServer and the Arbiter and dumps every byte both ways.
Already written, at `D:\v100\TERA_SERVER.100\arbiter-world-tap.js`.

```
node arbiter-world-tap.js          # listens 127.0.0.1:7812, forwards to 127.0.0.1:7802
```

Then point World at the tap instead of the Arbiter — in `DeploymentConfig.xml`, under
`WorldServerConfig`, set `<ArbiterServer port="7812"/>`. Output format:

```
[<seq>] [W->A|A->W] <ISO timestamp> len=<n>
<hex bytes, space separated>
```

That is exactly the format `WorldReplayTable.Load` parses and the format of `arb_world.log` and
`lobby_tap.log`. **Reframe by u32 length — chunk boundaries are not frame boundaries.**

To capture against the *real* ArbiterServer (to learn what we should be doing), run the retail
`ArbiterServer.exe` on 7802 with the tap in front of it. To capture TeraSharp's own behaviour, run
TeraSharp on 7802 instead. Diffing the two is the single most productive thing in this project.

### B. Client <-> server (encrypted on the wire)

A raw TCP tap here only yields ciphertext after the key exchange. Two working options:

1. **`tera-server-proxy` / tera-toolbox** (present at
   `D:\v100\TERA_SERVER.100\tera-server-proxy\`, opcode map in `data\data.json` under `maps."376012"`).
   It terminates the client crypto and logs decrypted, opcode-named packets. This produced
   `D:\packetlogs\lobby_proxy.log`:

   ```
   [1] [C->S] C_CHECK_VERSION (19900) len=32
   HEX: 20 00 BC 4D 02 00 08 00 ...
   ```

2. **Log inside TeraSharp**, which is simplest and always correct: `GameSession` already has the
   decrypted frame in hand. Dump it post-decrypt on receive and pre-encrypt on send, in the same
   `[seq] [C->S|S->C] NAME (op) len=` format, to `TERASHARP_LOGS`. `GameSession.cs` is human-owned —
   propose the patch, do not edit it yourself.

With the 4548 `.def` files in `tera_v100_MASTER_FINAL\` we can decode any client packet by name, so
a decrypted client log is directly actionable.

---

## 6. Where things stand

See `status/STATUS.md` for the current state, what changed last, and what to test next.
