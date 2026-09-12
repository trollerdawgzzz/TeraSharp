# TeraSharp — Work Status (session 2026-09-12, context 3)

Scope this session: **Tasks A–F from CLAUDE.md section 0** (build + test only, no live server).

Build: `dotnet build TeraSharp.sln` → 0 warnings, 0 errors.
Tests: `dotnet run --project src/TeraSharp.Arbiter.Tests` → 76 passed, 0 failed.

---

## Completed this session

### Task A — Real handlers for ~14 remaining login-time SDB_* (prior context)
All 14 remaining SDB handlers now real (were on replay table). Implemented:
- 6 programmatic builders: 0x2868 (3 empty lists), 0x286A (empty list+timestamp),
  0x2901 (2 empty lists), 0x28B6 (ok+reqId+u64), 0x28B1 (refer_a_friend),
  0x2976, 0x2987, 0x290C multi-reply (0x15B1 + 0x2847 + 0x143E + 0x290D).
- 8 static-data templates (reproduced byte-exact from capture, live reqId echoed):
  TUTORIAL, REPUTATION, QUEST_LIST (1377B), ACHIEVEMENT (1501B),
  FATIGABILITY, SEREN_GUIDE, GUILD_SEARCH (113B), 0x293B (46B).
- **All verified by test** against arb_world.log captured bytes (20+ tests).
- No SDB handlers remain on the replay table for the login sequence.

### Task B — Build AS_ENTER_WORLD from CharacterRecord (prior context)
`WorldEntry.BuildEnterWorldPayload` generates the 183-byte payload from live character
fields (zone, position, level, templateId, gameId). Replaces the replayed-with-patch approach.
Verified by test: length, offsets, all known field positions match the capture frame.

### Task C — Character creation from decompile
Files: `Handlers/CharacterHandlers.cs` (rewritten), `Handlers/HandlerRegistry.cs` (registrations added).

- `C_CREATE_USER`: reads fields via ReadByDef, validates name (2–16 chars, globally unique via
  `CharacterStore.NameExists`), enforces 8-char limit, computes templateId (10101 + race×200 +
  gender×100 + class), creates DB row with no world blob (WorldServer initialises on first enter
  when DBS_USER_ENTERWORLD returns found=0). Default starting position: Velika (zone 7005).
- `C_DELETE_USER`: reads character ID (ReadByDef or raw u32 fallback), verifies ownership, deletes
  from store and in-memory list.
- `C_CHECK_USERNAME`: added global uniqueness check via `Program.Store.NameExists`.
- `C_CAN_CREATE_USER`: unchanged (count < 8).
- **Verified by test:** TemplateId formula (4 tests), name validation (4 tests). Compiled only:
  the full create/delete flow (needs a game client to send the packets).

### Task D — S_EXIT / S_PREPARE_EXIT def files
Files: `Protocol/DefinitionRegistry.cs` (added `Register` + `RegisterIfMissing`), `Program.cs` (inline defs).

- `DefinitionRegistry.RegisterIfMissing(name, params (type, name)[])`: creates a PacketDef from
  type/name tuples if no def is already loaded. Maps type strings to FieldKind.
- Program.cs now registers:
  - `S_PREPARE_EXIT` → `(int32, time)` — countdown before client close.
  - `S_EXIT` → `(int32, category)` — final close signal.
- The exit flow's `if (defs.Has(preparePacket))` and `SendByDef` calls now find these defs.
- **Verified by test:** RegisterIfMissing creates def with correct fields; does not overwrite
  existing def (2 tests).

### Task E — Account auth with default-off flag
Files: `Program.cs` (config), `Handlers/LoginHandlers.cs` (auth gate + ValidateAccount).

- `Program.AuthEnabled`: static bool, set from env var `TERASHARP_AUTH` (`"true"` or `"1"` to enable).
  Default: **false** (accept all logins, current behavior unchanged).
- `Program.AuthApiUrl`: string, from `TERASHARP_AUTH_URL` (default `http://127.0.0.1:8080`).
- `LoginHandlers.ValidateAccount(accountName)`: HTTP GET to `{AuthApiUrl}/auth/validate?account=...`.
  Returns true on 200, false on error/non-200 (fail-closed).
- When `AuthEnabled` is true and validation fails, `OnLoginArbiter` sends `S_LOGIN_ARBITER` with
  `success=false` and returns without loading the account or sending further packets.
- **Verified by test:** AuthEnabled defaults to false; ValidateAccount rejects when API is
  unreachable (2 tests). Compiled only: the full reject-then-client-disconnects flow
  (needs tera-api running + a game client).

---

## Carried forward from prior sessions (unchanged)

### Section 0 — logout button. FIXED (verified by test; not yet run live)
Root cause: three divergences from the real Arbiter (missing AS_USER_REQUEST_EXIT, wrong
AS_LEAVE_WORLD trailer values, stale gameId in AS_ARBITER_USER_DELETE). All corrected.
Byte-exact test coverage for all leave/save payloads.

### Task 2 — Multi-player tunnel routing: groundwork only
GameId player registry exists; SA_LEAVE_WORLD routes to the right session. The W→C tunnel
(0x13F7) still broadcasts. Needs a 2-login capture to determine which 0x13F7 header field
(`conn` vs `idx`) identifies the target.

---

## Blocked on user / needs a live environment

- **End-to-end live test** of logout, character creation, and auth reject (needs WorldServer +
  game client + optionally tera-api).
- **Multi-player routing** (needs 2-login `arb_world.log`).
- **Remaining SDB_* as they appear** in logs (task 7) — all login-time ones are real now;
  new ones will surface when other game systems are exercised.

---

## Unresolved decompile facts

- `AS_ENTER_WORLD` gameId low byte is 6 while playerId is 1 in the capture. GameId is
  `0x80000AF00000 | X` where X is assigned by the Arbiter (possibly a session counter, not
  necessarily characterId). Harmless for one player; multi-player work should pin down the
  assignment scheme.
- `AS_ARBITER_USER_DELETE` second u64: the decompiled writer sources it from struct offset
  `+0x4038`, distinct from gameId at `+0x5718`. It equals gameId in this capture; if a future
  case shows them differing, model `+0x4038` separately.
- `0x2937` reply middle word `0x00000300` and `0x2769` empty-list offsets are replicated from
  the capture (fixed ack fields for an empty-delta save), not independently derived from the
  decompiled writers.

---

## Test summary

76 tests total. All verified against `arb_world.log` captured bytes where applicable.
Categories: leave/save payloads (15), login-time SDB handlers (30), AS_ENTER_WORLD builder (9),
character data builder (2), character creation logic (8), inline def registration (2),
account auth (2), static-data templates (8).
