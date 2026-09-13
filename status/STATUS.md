# TeraSharp — Status (2026-09-13)

Read `status/HANDOFF.md` first if you are new — especially section 1 (DLMItems). Workspace rules
are at the top of `CLAUDE.md`.

Build: `dotnet build TeraSharp.sln` -> 0 warnings, 0 errors.
Tests: `dotnet run --project src/TeraSharp.Arbiter.Tests` -> **149** (134 before the T2/T3/T4/T5
branches, +15 from them). Re-run to confirm the count after merging.

---

## 0. State of play — logout/relog is FIXED

**Live-verified 2026-09-13, 21:13 and 21:46 runs.** Logout button -> countdown -> char select ->
select again -> spawn where you logged out. The full chain runs:

```
0x13AA -> 0x27FA -> 0x2924 -> 0x2768 -> 0x2936 -> 0x27CB (blob saved) -> 0x1393 -> 0x1433
```

No `forcing delete`, no World crash, relog spawns at the logout position.

**The root cause of every "hangs on relog / Critical Error LeaveWorld" was one thing:** a per-user
WorldServer **DLMItem head-block** (`status/HANDOFF.md` section 1). World serialises every per-user
DB message; one `DBS_*` reply that carries a stale or captured DLM id — or never arrives — silently
blocks every later item for that user, including `UserLeaveWorld`, which is the only thing that
emits `SA_LEAVE_WORLD`. The 5 s fallback then fakes the lobby return and the next `C_SELECT_USER`
stalls at `0x1626` because World still holds the character.

It was never a logout bug, never a leave-reason bug, and never a missing trigger. **Any new
`no replay for 0xNNNN` on a per-user opcode is the next wedge waiting to happen.**

### Two facts that used to be wrong in these notes

- `AS_LEAVE_WORLD` (0x1392) = `[u64 gameId][u32 leaveWorldType][u32 logoutReason][u32 playerId]`.
  Lobby return is **(3,0)**, disconnect is (1,0) — both from `lobby_tap.log`; the older
  `arb_world.log` disconnect used (1,8). The type/reason are only read once `UserLeaveWorld`
  reaches the head of the DLM queue, so they were never the blocker.
- Respawn position is **done**. `WorldEntry.BuildEnterWorldPayload` now sends `param_9 = 0xFFFFFFFF`
  (payload[52]) and takes x/y/z from blob offset 220 when a blob exists, so World restores from the
  blob. Live-verified: logout save == relog enter-world == first save after respawn.

---

## 1. What is REAL (not replayed)

All in `World/DbProxyHandlers.cs`, all echoing the **live** DLM id from the request, each verified
against `D:\packetlogs\lobby_tap.log` bytes or the decompiled writer. The `TryHandle` allow-list
(the FIRST switch) is the source of truth for this list — anything not in it falls through to the
replay table.

| Opcode | Name | Reply | Note |
|---|---|---|---|
| 0x2711 | `SDB_USER_ENTERWORLD` | 0x2738 + **0x2830** | world blob from SQLite; `DBS_USER_RESTRICTION` follows it (T3) |
| 0x27CB | `SDB_UPDATE_USER_DATA` | 0x27CC | the periodic + logout blob save |
| 0x272C | `SDB_LOAD_QUEST_LIST` | 0x272D | **empty** 159 B form, so World takes the seed branch |
| 0x2899 | `SDB_UPDATE_DAILY_QUEST_SEED` | 0x289A | fires 17x at enter-world; absent from `arb_world.log` |
| 0x2897 | `SDB_UPDATE_DAILY_QUEST_COMPLETE_COUNT` | 0x2898 | |
| 0x2910 | `SDB_UPDATE_FATIGABILITY_POINT` | 0x2911 | |
| 0x290C | `SDB_INIT_LOSS_LOGINTIME_REVISION_SECOND` | 0x290D | also pushes 0x15B1, 0x2847, 0x1440, 0x143E |
| 0x27B9 | `SDB_USER_LOAD_EP_PERK` | 0x27BA | |
| 0x2869 | `SDB_LOAD_DUNGEON_PHASE_LEVEL` | **0x15E0** + 0x286A | T4 — both carry the same live reset time |
| 0x2736 | `SDB_END_START_QUEST_LIST` | 0x2737 | post-spawn |
| 0x2930 | `SDB_UPDATE_HOLD_CHARACTER_STATUS` | 0x2931 | post-spawn |
| 0x27B3 | `SDB_UPDATE_DAILY_LIMIT_EP_EXP` | 0x27B4 | post-spawn |
| 0x1562 | `SA_CLEAR_BATTLE_FIELD_ENTER_COUNT` | 0x1563 | daily reset, can fire mid-session |
| 0x27FA | `SDB_UPDATE_USER_ACHIEVEMENT` | 0x27FB | logout save |
| 0x2924 | `SDB_UPDATE_PASSIVITY_COOLTIME` | 0x2925 | logout save |
| 0x2768 | `SDB_ITEM_SINGLE` | 0x2769 | logout save |
| 0x2936 | `SDB_CHECK_DAILY_ATTENDANCE` | 0x2937 | logout save |

Names are from the opcode switch in `WorldServer.exe.c` (see `data/dbproxy_opcodes.txt`, T2). Several
C# constants in `DbProxyHandlers` still carry older guessed names (`SDB_LOAD_FRIEND_INFO` for 0x2910,
`SDB_LOAD_WORLD_EVENT` for 0x27B3, `SDB_LOAD_2930`, `SDB_LOAD_290C`) — the table above is correct.

`World/WorldReplayTable.cs` also now refuses to make a request entry out of any one-way World
opcode (`OneWayFromWorld`, T5), which kills the `no replay for` noise and the mis-attribution that
caused the 0x143F -> 0x290D wedge.

### Still replayed, and fine

- **World startup handshake** — static config, never varies.
- **Client settings blobs** — client defaults.
- **~30 login-time `SDB_*` whose reply carries no live id.** Convert one to a real handler only if
  it wedges. **Do not** replace a data-bearing replayed reply with a synthetic empty one:
  `SDB_USER_LOAD_INVENTORY` (0x27A2) returns a 3235-byte item list, and a past session desynced
  World by emptying it.
- `AS_ENTER_WORLD` (0x138E) is built for real by `WorldEntry`, but from a captured template with
  gameId, tunnelKey and position patched in.

---

## 2. Human TODO (human-owned files — Cowork cannot touch these)

1. **`Network/GameSession.cs`: raise the `SA_LEAVE_WORLD` fallback from 5 s to 15 s and log loudly.**
   The real countdown between the final `0x14FF` and `0x1392` is ~10 s and the save chain takes
   ~80 ms once it runs, so 5 s will mask real failures as "forcing delete".
2. **`Handlers/WorldEntry.cs`: gameId and tunnelKey should increment per login.** A byte-diff of the
   two `AS_ENTER_WORLD` frames in `lobby_tap.log` (both 183-byte payloads) shows the real Arbiter
   varies exactly these between login #1 and the relog:
   `payload[80]` tunnelKey `0 -> 1` (we pin 5); `payload[84..91]` gameId `...0001 -> ...0002`
   (we reuse `...0001`). Both look like per-session counters.
3. **`Program.cs`: log level back from `Trace` to `Debug`.**
4. Optional, one line: `World/WorldBridge.cs` line ~337 logs `W->A #{Id} 0x{Op:X4}`. Swapping the
   opcode for `DbProxyOpcodeNames.Describe(op)` names every DB-proxy opcode in the log.

Done and no longer open: (3a) the replay id echo now passes the live payload; (3b) `LeaveValues`
lobby returns (3,0); (3c) `DBS_USER_RESTRICTION` (0x2830) is sent after `DBS_USER_ENTERWORLD`.

---

## 3. Also true, lower priority

- `BuildDbs2937` hardcodes `ok = 0`. That byte-matches the logout capture but not the login one
  (`2f 00 00 00 01 03 ...` = ok 1), so World runs `DBCheckDailyAttendance::OnFail` every login.
  Harmless today; make it explicit when someone models daily attendance.
- `0x147D` is answered with `0x1484`, not the `0x147E x23 + 0x1480` the older notes describe.
- The real Arbiter sends `0x15E0` only when a dungeon-phase reset actually fires. We keep no phase
  state, so we send it on every `0x2869` — harmless (World just restamps the reset time) but not
  byte-identical on a second login the same day.
- Multi-player tunnel routing is single-player fast-path only (`WorldBridge.HandleFrame` ignores the
  routing key when exactly one session is registered). Needs a real two-login capture.
- Character creation (T8), real account auth via `tera-api`, guild/party/friends/mail: not started.

---

## 4. Testing and tooling

- **Always restart WorldServer before a live test.** A wedged DLM queue from the previous run makes
  a correct fix look broken.
- Log lines to grep after a run: `no replay for 0x` (a per-user opcode here is a future wedge),
  `forcing delete` (the leave never completed), `blob pos`, `0x1393`.
- `arbiter-world-tap.js` taps Arbiter<->World (plaintext); `tera-server-proxy` (or a decrypted dump
  inside `GameSession`) taps client<->server. All 4548 client `.def` files are on disk, so any
  client packet decodes by name. See `status/HANDOFF.md` section 5.
- Both captures are TCP chunks — reframe by u32 length; a chunk can hold several frames and a frame
  can span chunks. `lobby_tap.log` (login + working logout + relog + second leave) is newer and much
  more complete than `arb_world.log` — but note the **replay table is loaded from `arb_world.log`**,
  so an opcode present only in `lobby_tap.log` has no replay entry at all.
