# TeraSharp — Status

Mirrors `CLAUDE.md` section 0. When the two disagree, **CLAUDE.md wins** and this file is stale.
Day-by-day state, live-debug recipes and the deploy loop are in `status/CHAT-HANDOFF.md`.

Read order for anyone new: `CLAUDE.md` (workspace rules at the top) -> `status/HANDOFF.md` §1
(DLMItems — the single failure mode behind every relog hang) -> `status/PERSISTENCE-MAP.md`
(every per-user W->A opcode and how it is answered) -> this file.

Build: `dotnet build TeraSharp.sln` — 0 warnings, 0 errors.
Tests: `dotnet run --project src\TeraSharp.Arbiter.Tests` — **566**.
Deploy check: `TeraSharp.Arbiter.exe --selftest` — one PASS/FAIL line per data dependency (T37).

---

## Status by area

| Area | Status |
|---|---|
| Client crypto / codec / login / char list / select / create / delete | real |
| Chat, client settings (persisted) | real |
| Whisper + private chat channels - layouts, the channel object, `ChatManager` | implemented (T43), RAM-only, **not wired** |
| The 18 client packets World rejects - tooltips, exploration, client log, the small acks | implemented (T45), **not wired** |
| Mail: the six `SDB_*_PARCEL` W<->A pairs, empty inbox byte-exact | real (T45) |
| Friends, friend groups, memos, block list — two-step requests, all from rows | real (T30) |
| World handshake, 0x147D promotion records (live timestamps), 0x1581 burst, post-handshake config burst | real |
| Enter-world, blob save/load, restriction, gameId per login | real, live-verified |
| Per-user DB writes during play (T15), quests (T17), skills in blob (T18) | real |
| Inventory — bag + worn slots are rows in the same `items` table as the warehouse; 0x27A4 rebuilt from them, atoms applied | real (T44) |
| Zone change / quest teleport (0x13BE/0x13C0 echoes) | real, live-verified |
| Relog into an instance (0x138D -> retry at the stored return point) | implemented (T21), **live test pending** |
| Achievements, tutorial tips, seren guide, dungeon history — rebuilt from rows | real (T22) |
| Reputation (0x2890), fatigability (0x2909) — rebuilt from rows | real (T26), pinned from the decompile |
| Dungeon cool times (0x13B6 write, 0x2868 load, 0x148D pushes) | real (T25) |
| Multiple players | blocked on a two-login capture |
| Account auth | accept-all by default; real tera-api validation behind `TERASHARP_AUTH=true` (T31) |
| GM commands (`/@`) — dispatcher, gate, World forward, 6 Arbiter commands | real (T32), needs the HandlerRegistry lines |
| Warehouse — 8 W->A requests answered from `items`/`warehouses` rows, `0x2754` sealed | real (T42) |
| Mail — the 3 Arbiter-owned client packets + `parcels` table; World's 6 `SDB_*_PARCEL` are not answered | half real (T42) |
| Parties — `PartyManager` (T35) wired through `World/PartyWiring.cs`, roster shared with chat | implemented (T49), needs the 2 human-owned lines |

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
| Mail, the warehouse, and which pocket id means what | `status/MAIL-WAREHOUSE.md` |
| Where an item is, and which atom op moved it | `status/INVENTORY-DESIGN.md` §7, `status/PERSISTENCE-MAP.md` |
| Why a warehouse move is not an `SDB_ITEM_SINGLE` atom | `status/MAIL-WAREHOUSE.md` §6 |
| Everything else in the 2026-09-13 relog capture | `status/RELOG-CAPTURE-NOTES.md` |
| Guilds - the object, the SQL schema, the opcodes and the .def corrections | `status/GUILD-DESIGN.md` |
| Guild rows, the Arbiter-side guild handlers, and the wiring they still need | `status/GUILD-DESIGN.md` section 10 |
| How a subsystem's action list becomes sends (parties, guilds, chat, and whatever is next) | `World/ActionDispatcher.cs`, and the wiring section of each design doc |
| Why World logs "handler has not been implemented yet!!!", and who owns each of those packets | `status/CLIENT-REJECTS.md` |
| Why a fresh mailbox showed 12 blank rows, and the 35-byte empty `DBS_LIST_PARCEL` | `status/CLIENT-REJECTS.md` section 4 |
| Whisper, private channels, the slot-not-an-id trap, and the chat .def corrections | `status/CHAT-DESIGN.md` |
| The `SocialHandlers.OnWhisper` -> `ChatManager` swap, and what it changes for blocked users | `status/CHAT-DESIGN.md` section 7.2 |

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
- **The `RegNoop` / `RegEmptyReply` in-world forward** (`HandlerRegistry`) sends Arbiter-owned
  packets to World, which drops them. Six packets are affected; the fix and the
  `PacketDispatcher` deny-list are in `status/CLIENT-REJECTS.md` section 7 (human-owned files).
- **The guild (T39) and chat (T43) wiring diffs pass a TOTAL packet length where
  `PacketDispatcher` wants a BODY length** - every packet would be rejected four bytes short.
  Subtract 4; `status/CLIENT-REJECTS.md` section 7.2c.
- Whether answering `C_SHOW_ITEM_TOOLTIP_EX` is what makes a used potion's count repaint - the
  packet is certainly the Arbiter's and certainly carries `Count`, but no capture has the
  exchange. `C_CLIENT_LOG` is now printed and is the next evidence
  (`status/CLIENT-REJECTS.md` section 2.4).
- Six `S_` reply shapes that need a capture before their requests can be answered properly:
  `S_DUNGEON_COOL_TIME_LIST`, `S_REPLY_GUILD_LIST`, `S_SHOW_PARTY_MATCH_INFO`,
  `S_MY_PARTY_MATCH_INFO`, `S_SHOW_CANDIDATE_LIST`, `S_VIEW_BATTLE_FIELD_RESULT`.
- Private channels: the member cap default (`DAT_140e315a8`), what the create/edit invite list
  actually sends each invitee, and the master-promotion rule - all three are ours, not the
  binary's (`status/CHAT-DESIGN.md` section 9). No capture contains a single channel packet.
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
- T49 needs two lines in human-owned files: one `foreach` over `PartyWiring.ClientOpcodes`
  in `HandlerRegistry.RegisterAll` (the seven `C_` party opcodes, body length = the decompile's
  frame guard MINUS 4) and `if (PartyWiring.TryHandleWorldFrame(op, payload)) return;` as the
  first line of `WorldBridge.HandleFrame`'s `default:` arm (that one covers `SA_BYPASS_TO_GROUP`
  0x13F8 and the eleven other W->A party opcodes). Exact text: `status/PARTY-DESIGN.md` §11.5.
  Nothing is needed in `WorldEntry` or `GameSession` - party registration rides the chat roster.
- Parties: four W->A opcodes are gated to `PartyManager` but have no case yet - `SA_SWAP_PARTY`
  0x139A, `SA_CHANGE_PARTY_MEMBER_AUTHORITY` 0x139C, `SA_JOIN_PARTY_IN_ARBITER` 0x13AB,
  `SA_MERGE_PARTY_TO_RAID` 0x13AC. They log a rejection and send nothing
  (`status/PARTY-DESIGN.md` §11.6). Party matching (`C_REQUEST_PARTY_INFO` ->
  `S_PARTY_MEMBER_INFO`) is registered but deliberately swallowed - §11.3.
- T47's two lines are IN (`WorldEntry.EnterWorld:47`, and `GameSession.OnWorldLeaveConfirmed` /
  `LeaveWorld` both call `SocialHandlers.UnregisterChat(this)`), which is what let T49 hang the
  party roster off the same two call sites.
- GM: the client only offers `/@` for a QA login (`S_LOGIN_ARBITER.status` 31/33) - LoginHandlers
  still sends 0, so the one-line diff in `status/GM-DESIGN.md` §6 is untested live. That is now
  the ONLY thing between us and trying the 416 World commands: the 0x2829 frame is verified
  byte-exact and World's handler does no admin check (`status/GM-DESIGN.md` §7).
- World logs `AdminLevel[0]` for a GM because `WorldEntry` hard-codes AS_ENTER_WORLD payload
  111. Three-line diff in `status/ENTER-WORLD-FALLBACK.md` §8d (WorldEntry is human-owned).
  Cosmetic - World stores the level but never tests it (§5a).
- Mail: World's six `SDB_*_PARCEL` requests are still unanswered, so the first mailbox a live
  player opens gives `no replay for 0x2777` and head-blocks that character
  (`status/MAIL-WAREHOUSE.md` §7). The client half and the `parcels` table are done (T42).
- T42 needs four lines in `HandlerRegistry.cs` (human-owned): the three `C_PARCEL_*`
  registrations and the `SendReadRecvStatus` push at `C_LOAD_TOPO_FIN` —
  `status/MAIL-WAREHOUSE.md` §8.
- Warehouse `MaxSlotCount` is 0 until a `warehouses` row exists; the real caps are in
  `ServerConfig.xml`, which we do not read.
- `status/*.txt` (15 decompile scratch files) should be deleted; Cowork has no delete tool
  in this session, so the human runs the `git rm` in the T24 report.
- The replayed `DBS_INIT_GUILD_DATA` (0x27ED) leaks two bytes of the real Arbiter's
  uninitialised stack padding (`0xB379` at GuildData+0x024A). Harmless - World never reads
  them - but `GuildPackets.BuildEmptyDbsInitGuildData()` builds the same frame with zeros, so
  the replay entry could be swapped for it (`status/GUILD-DESIGN.md` sections 2.1 and 9).
