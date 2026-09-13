# TeraSharp — Status (2026-09-13)

Read `status/HANDOFF.md` first if you are new. Workspace rules are at the top of `CLAUDE.md`.

Build: `dotnet build TeraSharp.sln` -> 0 warnings, 0 errors.
Tests: `dotnet run --project src/TeraSharp.Arbiter.Tests` -> **115 passed, 0 failed**
(105 baseline + 4 replay id-echo + 6 daily-quest/quest-list).

---

## 0. The open bug and the diagnosis

**Symptom.** Logout button -> World logs `LeaveWorldStart` then `DespawnComplete`, then sends
nothing: no `0x2924`, no `0x2936`, no `0x27CB`, no `SA_LEAVE_WORLD` (0x1393). The character is
never deleted from World's user map, so the next `C_SELECT_USER` hangs. Only a World restart
clears it.

**It is not a logout bug.** Diffing `D:\packetlogs\ts-logout.log` (TeraSharp) against
`D:\packetlogs\lobby_tap.log` (real ArbiterServer, a working logout + relog) shows the two runs are
frame-for-frame identical from `AS_ENTER_WORLD` through `SA_ENTER_WORLD` (0x138C) - and then:

```
real:       W 0x2899 -> A 0x289A   x17     then 0x2910, 0x290C, 0x27B9, 0x2768, 0x2936,
                                            0x2981, 0x2986, spawn, 0x2736, 0x27CB x2,
                                            0x2930, 0x27B3 ... and later the logout chain
TeraSharp:  W 0x2897                       and then NEVER another per-user DB message,
                                            ~19 seconds before the Logout button was pressed
```

In the whole TeraSharp run, `0x2736 0x27CB 0x2910 0x2981 0x2986 0x290C 0x27B9 0x2930 0x27B3 0x2768
0x2936` appear **zero** times. The world blob is never saved during play either. Non-DB traffic
(`0x2958`, `0x159A`, `0x13AA`, `0x15B5`, `0x15AE`, `0x1507`) keeps flowing throughout.

That is the signature of a head-blocked per-user DLM queue (see `status/HANDOFF.md` section 1). The
gate is `DLManager::Execute` (`WorldServer.exe.c` 534211): an item whose predecessor never called
`CompleteMyself` waits silently forever, and with it every later per-user DB message including
`UserLeaveWorld`, which is the only thing that emits `SA_LEAVE_WORLD`.

**Why the daily-quest step diverges.** `0x272C` -> `0x272D` (`DBS_LOAD_QUEST_LIST`) is served from a
captured template. The captured form is 1383 bytes and carries 17 daily-quest seeds stamped
**2026-09-12**. Serving that to World on any later day makes its quest manager take the "these
dailies are stale, reset the completed count" branch - `SDB_UPDATE_DAILY_QUEST_COMPLETE_COUNT`
(0x2897) - instead of the "seed today's dailies" branch the real server drives. The real Arbiter
returns a **159-byte** form with an empty daily-seed section on a first login, and World then emits
`SDB_UPDATE_DAILY_QUEST_SEED` (0x2899) 17 times.

`0x2899` is the **only** W->A opcode present in `lobby_tap.log` and absent from `arb_world.log`, so
the replay table has no entry for it and TeraSharp answered it with nothing.

---

## 1. What changed in this commit

Three files, all in the Cowork-editable set. Pure logic + data; no live-path files touched.

### `World/DbProxyStaticData.cs`
- Added `QuestListEmpty` (153-byte payload / 159-byte frame) + `QuestListEmptyReqIdOffset = 49`.
  Bytes are verbatim from `lobby_tap.log` 02:51:10.648Z, real ArbiterServer, first login of the day
  for playerId 1. Same 53-byte header as the existing `QuestList`, but `dailyQuestSeedSize` at [28]
  is **0**. The old 1383-byte `QuestList` template is kept, unused, for reference.

### `World/DbProxyHandlers.cs`
- Added `SDB_DAILY_QUEST_SEED = 0x2899` / `DBS_DAILY_QUEST_SEED = 0x289A` and a real handler:
  reply `[u32 reqId][u8 1]`, reqId read from request **payload[8]**. Verified against World's
  writer and the capture (`req ...2b 00 00 00 01 00 00 00 59 02...` -> `rsp 2b 00 00 00 01`).
- Added `SDB_DAILY_QUEST_SEED` and `SDB_QUEST_LIST` to the `TryHandle` allow-list (the first switch;
  anything not listed there falls through to the replay table).
- `SDB_QUEST_LIST` now builds from `QuestListEmpty` instead of the stale `QuestList`.

### `src/TeraSharp.Arbiter.Tests/Program.cs`
- 6 new tests: `DailyQuestSeed_ack_matches_capture`, `DailyQuestSeed_echoes_live_reqId_not_capture`,
  `DailyQuestSeed_opcodes_are_2899_289A`, `QuestListEmpty_matches_capture_bytes`,
  `QuestListEmpty_has_no_daily_quest_seeds`, `QuestListEmpty_echoes_live_reqId_at_49`.
- (From the previous commit: 4 tests pinning the `WorldReplayTable` id-echo contract.)

**The two changes must ship together.** Serving the empty quest list is what makes World emit the 17
seeds; without the `0x2899` handler that is strictly worse than before.

---

## 2. How to test this live

1. Publish and deploy as usual.
2. **Restart WorldServer** before the test - a previous run may have left a wedged DLM queue.
3. Log in. In the Arbiter log you should now see `W->A 0x2899` **17 times** during enter-world,
   with no `no replay for 0x2899`, and then `0x2910 / 0x290C / 0x27B9 / 0x2768 / 0x2936 / 0x2981 /
   0x2986`, and after spawn `0x2736` and `0x27CB`.
4. `W->A 0x27CB` appearing at all is the first real proof the queue is draining - it never appeared
   before.
5. Then press Logout and look for `0x27FA -> 0x2924 -> 0x2768 -> 0x2936 -> 0x27CB -> 0x1393 -> 0x1433`.

If `0x2899` appears but the chain still stops after it, capture the WorldServer console for the
enter-world window - `Critical Error`, `Fail to get User`, and quest-manager lines land there.

### To revert
`git revert <this commit>` - or `git checkout <prev> -- src/TeraSharp.Arbiter/World/DbProxyHandlers.cs src/TeraSharp.Arbiter/World/DbProxyStaticData.cs`.
Nothing else depends on these two symbols.

---

## 3. Applied by hand this session (WorldBridge.cs, human-owned)

### a) APPLIED - `World/WorldBridge.cs` line 339: the replay id echo never ran

```csharp
var responses = _replay.GetResponses(op);          // current: 1-arg overload
var responses = _replay.GetResponses(op, payload); // fix
```

`WorldReplayTable` has two overloads; the 1-arg one passes `liveRequest = null`, and with null the
`IdMap` patch loop is skipped entirely. It is the only call site, so **every replayed `DBS_*` has
always gone out carrying the captured DLM id**. On a freshly started World the live ids happen to
reproduce the captured sequence (the counter starts at 1 in both), which is why the first login
after a World restart works; any divergence after that is permanent until the next restart.
`payload` is already in scope in that `default:` branch. 4 tests pin this contract.

### b) APPLIED - `World/WorldBridge.cs`: `LeaveValues(Lobby)` now returns (3,0)

`lobby_tap.log` confirms the lobby return uses **(3,0)**:

```
A->W 0x1392  01 00 f0 0a 00 80 00 00 | 03 00 00 00 | 00 00 00 00 | 01 00 00 00
```

and the same capture's disconnect leave uses (1,0), while the older capture's disconnect used (1,8).
Exit/Disconnect keep (1,8) - the pair World accepted in both captures. The three `LeaveValues`
lobby tests were flipped to match.

## 3b. Still open (human-owned files)

### c) `DBS_USER_RESTRICTION` (0x2830) is never sent

The real Arbiter sends it ~3 ms after `DBS_USER_ENTERWORLD`:
`[u32 off=22][u32 count=0][u64 LIVE gameId]`. In TeraSharp, `DbProxy.TryHandle` intercepts `0x2711`
and returns true, so the replay pair (`0x2738` + `0x2830`) is skipped and only the blob goes out.
Unproven whether World needs it; the real Arbiter always sends it.

### d) gameId and tunnelKey should increment per login

A full byte-diff of the two `AS_ENTER_WORLD` (0x138E) frames in `lobby_tap.log` - both 183-byte
payloads - shows the real Arbiter varies exactly these fields between login #1 and the relog:

```
payload[80]      tunnelKey   0        -> 1          (WorldEntry pins it to 5)
payload[84..91]  gameId      ...0001  -> ...0002    (WorldEntry reuses ...0001 every time)
payload[52..73]  zone/position floats (the character had moved; expected)
```

So `WorldEntry.BuildEnterWorldPayload` puts the gameId in the right place - it just never changes.
Both look like per-session counters on the real server.

### e) The 5 s `SA_LEAVE_WORLD` fallback is too tight

The real countdown between the final `0x14FF` and `0x1392` is ~10 s, and the save chain takes ~80 ms
once it runs. `Session ...: SA_LEAVE_WORLD not received in 5s - forcing delete` will mask real
failures; consider 15 s and log loudly.

---

## 4. Also true, lower priority

- `0x2736` has no replay entry, so `0x15AE` inherited its `0x2737` response - we send a bogus
  `DBS_END_START_QUEST_LIST` on every spawn.
- `0x15B1` / `0x2847` / `0x143E` are Arbiter-**initiated** pushes during the seed burst (World
  answers `0x143F`), not responses to `0x290C`. The replay table attributes them to `0x290C`, so we
  send them late; we never send `0x1440` (`AS_RESET_FIELD_POINT_COMPLETE`) at all.
- `0x147D` is answered with `0x1484`, not the `0x147E x23 + 0x1480` the notes describe.
- `BuildDbs2937` hardcodes `ok = 0`. That byte-matches the logout capture but not the login one
  (`2f 00 00 00 01 03 ...` = ok 1), so World runs `DBCheckDailyAttendance::OnFail` every login.
  Harmless today; make it explicit when someone models daily attendance.
- Multi-player tunnel routing is still single-player fast-path only; needs a two-login capture.
- Character creation, real account auth (tera-api), guild/party/mail: not implemented.

---

## 5. Note on tooling

We can capture **both** protocols and should do so before any protocol guesswork:
`arbiter-world-tap.js` for Arbiter<->World (plaintext), and `tera-server-proxy` (or a decrypted dump
inside `GameSession`) for client<->server. All 4548 client `.def` files are on disk, so any client
packet can be decoded by name. See `status/HANDOFF.md` section 5.
