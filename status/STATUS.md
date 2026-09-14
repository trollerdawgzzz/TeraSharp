# TeraSharp — Status

Mirrors `CLAUDE.md` section 0. When the two disagree, **CLAUDE.md wins** and this file is stale.
Day-by-day state, live-debug recipes and the deploy loop are in `status/CHAT-HANDOFF.md`.

Read order for anyone new: `CLAUDE.md` (workspace rules at the top) -> `status/HANDOFF.md` §1
(DLMItems — the single failure mode behind every relog hang) -> `status/PERSISTENCE-MAP.md`
(every per-user W->A opcode and how it is answered) -> this file.

Build: `dotnet build TeraSharp.sln` — 0 warnings, 0 errors.
Tests: `dotnet run --project src\TeraSharp.Arbiter.Tests` — **429**.
Deploy check: `TeraSharp.Arbiter.exe --selftest` — one PASS/FAIL line per data dependency (T37).

---

## Status by area

| Area | Status |
|---|---|
| Client crypto / codec / login / char list / select / create / delete | real |
| Chat, client settings (persisted) | real |
| Friends, friend groups, memos, block list — two-step requests, all from rows | real (T30) |
| World handshake, 0x147D promotion records (live timestamps), 0x1581 burst, post-handshake config burst | real |
| Enter-world, blob save/load, restriction, gameId per login | real, live-verified |
| Per-user DB writes during play (T15), quests (T17), inventory (T20), skills in blob (T18) | real |
| Zone change / quest teleport (0x13BE/0x13C0 echoes) | real, live-verified |
| Relog into an instance (0x138D -> retry at the stored return point) | implemented (T21), **live test pending** |
| Achievements, tutorial tips, seren guide, dungeon history — rebuilt from rows | real (T22) |
| Reputation (0x2890), fatigability (0x2909) — rebuilt from rows | real (T26), pinned from the decompile |
| Dungeon cool times (0x13B6 write, 0x2868 load, 0x148D pushes) | real (T25) |
| Multiple players | blocked on a two-login capture |
| Account auth | accept-all by default; real tera-api validation behind `TERASHARP_AUTH=true` (T31) |
| GM commands (`/@`) — dispatcher, gate, World forward, 6 Arbiter commands | real (T32), needs the HandlerRegistry lines |

## Where the truth lives

| Question | File |
|---|---|
| Why a relog hangs | `status/HANDOFF.md` §1 |
| Is opcode 0xNNNN answered, and how | `status/PERSISTENCE-MAP.md` (a test parses this table) |
| Quest list / quest writes | `status/QUEST-DESIGN.md` |
| Inventory and the 536-byte item record | `status/INVENTORY-DESIGN.md` |
| Level-1 skills, and where they live in the blob | `status/SKILLS.md` |
| Client option blobs | `status/CLIENT-SETTINGS.md` |
| Enter-world failure and the fallback retry | `status/ENTER-WORLD-FALLBACK.md` |
| The per-character login loads, and the two that resisted | `status/ACHIEVEMENTS.md` |
| Reputation and fatigability, and why the captures alone could not pin them | `status/REPUTATION-FATIGABILITY.md` |
| Friends, groups, memos, blocks — and the patch-101 def trap | `status/FRIENDS.md` |
| What a second player still needs | `status/MULTIPLAYER-DESIGN.md` |
| The login ticket, and who actually checks it | `status/AUTH-DESIGN.md` |
| How `/@` commands reach the server and who runs them | `status/GM-DESIGN.md` |
| What to click, and which log line proves it worked | `status/LIVE-CHECKLIST.md` |
| The two tunnel frame layouts, and the Ticket | `status/MULTIPLAYER-DESIGN.md` §6, `World/TunnelFrames.cs` |
| Every GM command, by side and risk tier | `status/GM-COMMANDS-ARBITER.md`, `status/GM-COMMANDS-FULL.md` |
| Dungeon cool times and entry counts | `status/DUNGEON-COOLTIME.md` |
| Everything else in the 2026-09-13 relog capture | `status/RELOG-CAPTURE-NOTES.md` |
| Guilds - the object, the SQL schema, the opcodes and the .def corrections | `status/GUILD-DESIGN.md` |
| Guild rows, the Arbiter-side guild handlers, and the wiring they still need | `status/GUILD-DESIGN.md` section 10 |

## The three rules that cost the most to learn

1. **Never send a `DBS_*` reply World did not ask for.** Every `DBS_` carries a DLM id World looks
   up; an unsolicited one completes whichever item currently holds that id.
2. **Every per-user W->A request must be answered** — allow-listed in
   `DbProxyHandlers.IsHandledRequest`, or in `WorldReplayTable.OneWayFromWorld`, or with a replay
   entry. `no replay for 0xNNNN` right before silence is the tell.
3. **Captured per-character data must never be served to another character.** Quests, inventory,
   the world blob, skills and the T22 loads each bit us as "every new character got dob's X".
   playerId 1 (dob) is the one character that still gets the captures, on purpose —
   `DbProxyHandlers.ServesCapturedStatics` is the single place that decides it.

Two guards enforce this and both fail the build:
`Every_per_user_request_opcode_is_answered` (from PERSISTENCE-MAP.md) and
`Dispatch_switch_and_the_allow_list_agree` (T24 — a dispatch case that is in neither
`IsHandledRequest` nor `DispatchOnlyForTests` is dead code, which is how four per-character loads
went on replaying dob's bytes until T22).

## Scope — who may edit what

From `CLAUDE.md` section 0. Cowork works only inside a `cowork/*` worktree and cannot run git.

- **Cowork may edit:** `World/DbProxyHandlers.cs`, `World/DbProxyStaticData.cs`,
  `World/WorldReplayTable.cs`, `Persistence/CharacterStore.cs`, `Handlers/CharacterHandlers.cs`,
  `Handlers/SocialHandlers.cs`, `Handlers/ChatHandlers.cs`, `Protocol/*`,
  `src/TeraSharp.Arbiter.Tests/`, `status/*.md`, `data/*`.
- **Human-owned — describe the change, never edit:** `World/WorldBridge.cs`, `Network/*`,
  `Handlers/WorldEntry.cs`, `Handlers/HandlerRegistry.cs`, `Handlers/LoginHandlers.cs`,
  `Program.cs`.

## Open

- Live test of the relog-into-instance fallback (T21).
- The trailing u32 of the fatigability element (630 / 1626 / 88 in the captures) is sent as 0;
  nothing explains it and World never reads it (`status/REPUTATION-FATIGABILITY.md` §2.3).
- Continent fallback table for a character with no stored return point
  (`status/ENTER-WORLD-FALLBACK.md` §9).
- `DBS_LOAD_DUNGEON_COOL_TIME` lists 1 and 2 (clear counts, UI history): stored but not served,
  layouts unobserved (`status/DUNGEON-COOLTIME.md` §3).
- `C_DUNGEON_COOL_TIME_LIST` reply: needs a client capture and a registry entry
  (`status/DUNGEON-COOLTIME.md` §5).
- Exit countdown: `S_PREPARE_EXIT` is not in the def registry (human-owned).
- Two-login capture -> multi-player tunnel routing, `S_CHANGE_FRIEND_STATE` on login/logout and
  the `AS_*` block-list pushes (`status/MULTIPLAYER-DESIGN.md`).
- Friend/blocked memos skip the Arbiter's banned-word + NetModerator stage (we have neither).
- GM: the client only offers `/@` for a QA login (`S_LOGIN_ARBITER.status` 31/33) - LoginHandlers
  still sends 0, so the one-line diff in `status/GM-DESIGN.md` §6 is untested live.
- `status/*.txt` (15 decompile scratch files) should be deleted; Cowork has no delete tool
  in this session, so the human runs the `git rm` in the T24 report.
- The replayed `DBS_INIT_GUILD_DATA` (0x27ED) leaks two bytes of the real Arbiter's
  uninitialised stack padding (`0xB379` at GuildData+0x024A). Harmless - World never reads
  them - but `GuildPackets.BuildEmptyDbsInitGuildData()` builds the same frame with zeros, so
  the replay entry could be swapped for it (`status/GUILD-DESIGN.md` sections 2.1 and 9).
