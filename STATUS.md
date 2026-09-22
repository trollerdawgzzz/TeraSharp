# TeraSharp — Work Status (session 2026-09-12)

Scope this session: **Section 0 (logout-button bug)** plus the parts of tasks 1 and 3 that
the logout path needs. Build + unit tests are green; nothing is deployed.

Build: `dotnet build TeraSharp.sln` → 0 warnings, 0 errors.
Tests: `dotnet run --project src/TeraSharp.Arbiter.Tests` → 15 passed, 0 failed.
(The test project is a self-contained console runner — xUnit/MSTest can't be restored here because
nuget.org is unreachable from the build box and the frameworks aren't in the offline package cache.
It exits non-zero on failure, so it works as a CI gate. One `[Test]` method per handler, asserting
against bytes from `<captures>\arb_world.log`.)

---

## Section 0 — logout button. FIXED (verified by test + decompile; not yet run live)

### Root cause (three divergences from the real Arbiter, all now corrected)

I read `Handler_C_RETURN_TO_LOBBY` → `User::OnRequestReturnToLobby` (FUN_14039c7c0),
`User::OnLeaveWorldTick` (FUN_14039a3d0), `User::SendASLeaveWorld` (FUN_1403a4820) and
`Handler_SA_LEAVE_WORLD` (Arb_part_062.c) + `FUN_140832f20`, and cross-checked every frame in
`arb_world.log`. The real lobby-return sequence is:

1. `AS_USER_REQUEST_EXIT` (0x14FF) `[u32 playerId]` — sent **immediately** on button press.
2. `S_PREPARE_RETURN_TO_LOBBY` to the client (countdown).
3. after the countdown: `AS_CANCEL_SKILL_STRICTLY` (0x1460) `[u32 playerId]`, then
   `AS_LEAVE_WORLD` (0x1392) `[u64 gameId][u32 leaveWorldType][u32 logoutReason][u32 playerId]`.
4. World saves (0x27FA/0x2924/0x2768/0x2936/0x27CB → acks) and sends `SA_LEAVE_WORLD` (0x1393).
5. we reply `AS_ARBITER_USER_DELETE` (0x1433) `[u64 gameId][u64 gameId]`, then send the client
   `S_RETURN_TO_LOBBY`.

TeraSharp was diverging in three places:

- **Never sent `AS_USER_REQUEST_EXIT` (0x14FF).** The real Arbiter sends it the instant the
  button is pressed (OnRequestReturnToLobby / OnRequestExit). This is the most likely reason World
  "replied 0x13AA and then nothing": World was never told the user had requested to leave.
- **`AS_LEAVE_WORLD` trailer was wrong.** Old code wrote `[gameId][playerId][8][1]` — the
  `playerId` sat where `leaveWorldType` belongs, and it used exit values (1,8) for every path.
  The decompile gives lobby = (type 3, reason 0), exit = (type 1, reason 8). (For the seeded
  player id 1 the old bytes happened to equal the captured *exit*, which masked the bug.)
- **`AS_ARBITER_USER_DELETE` carried a stale gameId.** 0x1393 fell through to the replay table,
  which can't patch the gameId (a gameId > 10M is filtered out of its id-echo heuristic), so the
  replayed 0x1433 deleted the *captured* player, not the live one → World keeps ours in-world →
  next `C_SELECT_USER` stalls (the exact described symptom). Now handled for real with the live
  gameId read from the 0x1393 frame.

### On the four leads (in order)

1. **"Remove 0x1460" — REJECTED.** Verified by grep it was never removed; then verified from the
   decompile *and* the capture (frame 1011) that the real Arbiter sends `AS_CANCEL_SKILL_STRICTLY`
   (0x1460) `[playerId]` before `AS_LEAVE_WORLD` on **every** logout (OnLeaveWorldTick calls
   FUN_140353310 then LeaveWorldStart). It is part of logout, not a stray skill-cancel. Kept it.
   This was the "read the decompile, don't guess from timing" case the handoff warned about.
2. **AS_LEAVE_WORLD trailer — CONFIRMED & FIXED**, plus found the real missing piece
   (AS_USER_REQUEST_EXIT). The `0x13aa` case (`SA_DEL_FROM_INTER_PARTY_MATCH_POOL`) gets **no**
   reply in the capture (next A→W frame is the save ack), so no answer is owed — we have no party
   system, so it is ignored.
3. **Client packets 62335 / 55963 = `C_SELECT_CHANNEL` / `C_DIALOG_EVENT`.** Ordinary in-world
   gameplay packets, not lobby-control packets the Arbiter must answer. They forward to World
   while `InWorld` is still true; not part of the logout handshake.
4. **Tunnel unsubscribe timing — FIXED.** The tunnel now stays subscribed until `SA_LEAVE_WORLD`
   (0x1393) so the client receives World's post-leave cleanup burst (frames 1013–1068), then it is
   torn down and `S_RETURN_TO_LOBBY` is sent. The disconnect path still unsubscribes immediately
   (client is already gone), preserving the behavior that already worked.

### Files changed
- `World/WorldBridge.cs` — `LeaveMode`; `SendUserRequestExit`/`SendUserCancelRequestExit`;
  `BuildLeaveWorldPayload` (correct trailer + lobby/exit values); `NotifyPlayerLeave(…, mode)`;
  gameId player registry; real `SA_LEAVE_WORLD` → `AS_ARBITER_USER_DELETE` with the live gameId.
- `Network/GameSession.cs` — leave state machine: `BeginLeaveToWorld` (0x14FF at press),
  `CompleteLeaveToWorld` (0x1460 + 0x1392, tunnel kept), `OnWorldLeaveConfirmed` (tear down +
  `S_RETURN_TO_LOBBY`/`S_EXIT`, idempotent, 5 s fallback), disconnect `LeaveWorld` unchanged in spirit.
- `Handlers/HandlerRegistry.cs` — `C_RETURN_TO_LOBBY` / `C_EXIT` use the new flow;
  `C_CANCEL_RETURN_TO_LOBBY` sends `AS_USER_CANCEL_REQUEST_EXIT` (0x1500) + the `byInterrupt` field
  the def requires (the old empty send was malformed).
- `World/DbProxyHandlers.cs` — real logout-save handlers (0x27FA/0x2924/0x2768/0x2936/0x2897),
  reqId echoed from the live request; `BuildDbsUserEnterWorld` extracted as a pure, testable builder.
- `Handlers/LoginHandlers.cs` — removed a duplicate `using` (the only prior build warning).
- `src/TeraSharp.Arbiter.Tests/` — new console test project (added to the .sln).

### Verified by test vs. only compiled
- **By test (byte-exact vs capture):** `AS_LEAVE_WORLD` payload (lobby/exit/disconnect),
  `AS_ARBITER_USER_DELETE`, gameId parse from `SA_LEAVE_WORLD`, all five save acks
  (0x27FB/0x2925/0x2769/0x2937/0x2898) incl. a live-reqId-echo regression, `DBS_USER_ENTERWORLD`
  found/not-found, and the leave-values table.
- **Compiled only (NOT yet exercised live):** the end-to-end button→countdown→save→char-select
  flow. I can't run it here — there's no WorldServer or game client in this environment. Needs a
  live run to confirm the acceptance criteria (`Saved world blob` during leave, `0x1393 → 0x1433`
  in the log, re-select spawns where you logged out). `C_EXIT` is wired the same way but
  `S_PREPARE_EXIT` / `S_EXIT` have **no .def in this build**, so the World-side save/leave runs but
  the client close is left to the client; confirm against a real client.

---

## Couldn't determine from the decompile / needs a live capture

- **Task 2 (multi-player tunnel routing): groundwork only.** Added a `Dictionary<ulong,GameSession>`
  keyed by gameId (`RegisterPlayer`/`UnregisterPlayer`), used now to route `SA_LEAVE_WORLD`
  completion to the right session. The W→C tunnel (0x13F7) still broadcasts via `OnTunnelToClient`
  — correct for one player. To finish it I need **two simultaneous logins**: the capture here has
  only one, so I can't tell whether `conn` (=157) or `idx` (=5) in the 0x13F7 header
  (`[16]serverId [20]conn [24]idx [28]seq`) identifies the target. Get a 2-login `arb_world.log`,
  see which field differs per player, then route by it + give each session its own reorder buffer.
- **Task 3 (remaining per-login SDB_*):** the logout-save handlers are now real; the ~40 login-time
  `SDB_*` are still served by the replay table (they work). `AS_ENTER_WORLD` low-byte gameId in the
  capture is 6 while playerId is 1, so gameId is **not** `0x80000AF00000 | characterId` as the notes
  assume — it's assigned independently (session/connection index). Harmless for one player (we just
  need enter/leave to agree), but the multi-player work should pin down how World assigns it.
- The `0x2937` reply's middle word is `0x00000300` and `0x2769` carries two empty lists — replicated
  exactly from the capture (they're fixed ack fields for an empty-delta save); not independently
  derived from the decompiled writers.
- `AS_ARBITER_USER_DELETE` (0x1433) second u64: the writer (FUN_140832f20) sources it from struct
  `+0x4038`, a field distinct from the gameId at `+0x5718`. It merely *equals* the gameId in this
  capture, so we send gameId twice (byte-exact to the capture). If a future case shows them
  differing, model `+0x4038` separately.
