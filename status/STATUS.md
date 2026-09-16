# TeraSharp — Status

Mirrors `CLAUDE.md` section 0. When the two disagree, **CLAUDE.md wins** and this file is stale.
Day-by-day state, live-debug recipes and the deploy loop are in `status/CHAT-HANDOFF.md`.

Read order for anyone new: `CLAUDE.md` (workspace rules at the top) -> `status/HANDOFF.md` §1
(DLMItems — the single failure mode behind every relog hang) -> `status/PERSISTENCE-MAP.md`
(every per-user W->A opcode and how it is answered) -> this file.

Build: `dotnet build TeraSharp.sln` — 0 warnings, 0 errors.
Tests: `dotnet run --project src\TeraSharp.Arbiter.Tests` — **605**.
Deploy check: `TeraSharp.Arbiter.exe --selftest` — one PASS/FAIL line per data dependency (T37).

**Where the project is (2026-09-15).** T1-T51 are merged. Two accounts on two clients have been in
the world at once, seeing each other and chatting (the multiplayer milestone, 2026-09-15 00:25), and
a single player's whole loop — create, play, loot, quest, teleport, exit, relog — is live-verified.
Everything merged since that milestone (T45-T51: whisper, the 18 Arbiter-owned client packets,
parties, guilds, GM forwarding, AdminLevel, the security audit) is **implemented, unit-tested and
live-untested**. `status/LIVE-CHECKLIST.md` is the pass that closes that gap and is the next thing
to do.

---

## Status by area

Three states, and the distinction is the point of this table: **live** = seen working against a
real client; **wired** = registered and reachable, never run live; **designed** = code and tests
exist, nothing routes to it.

| Area | State |
|---|---|
| Client crypto / codec / login / char list / select / create / delete | **live** |
| World handshake, 0x147D promotion records (live timestamps), 0x1581 burst, 63-push config burst | **live** |
| Enter-world, blob save/load, restriction, gameId per login | **live** |
| Chat (say / area / global), client settings, keybinds | **live** |
| Per-user DB writes during play (T15), quests (T17), skills in blob (T18) | **live** |
| Inventory — bag + worn slots as rows in `items`; 0x27A4 rebuilt from them, atoms applied | **live** (T44) |
| Character money — `characters.money` from the op-9 atom delta, served back at blob + 448 | **wired** (T59) |
| Achievements, tutorial tips, seren guide, reputation, fatigability, dungeon cool times — from rows | **live** (T22/T25/T26) |
| Zone change / quest teleport (0x13BE/0x13C0 echoes) | **live** |
| Relog into a dead instance (0x138D -> retry at the stored return point) | **live** (T21, verified twice) |
| Friends, friend groups, memos, block list — two-step requests, all from rows | **live** (T30) |
| Character delete, cascading every per-character table | **live** |
| Multiple players — TicketAllocator, N-recipient `SA_BYPASS_TO_CLIENT`, per-ticket reorder | **live** (T38 routing) |
| Account auth | **live** as accept-all; tera-api validation behind `TERASHARP_AUTH=true` (T31) |
| GM `/@` recognised by the client (`S_LOGIN_ARBITER.status` 31) | **live** (T32) |
| Whisper through `ChatManager`, recipients via `WorldBridge.SessionForPlayerId` | **wired** (T43/T47) |
| The 18 client packets World rejects — tooltip reply, visited sections, client log, small acks | **wired** (T45) |
| Mail — 3 Arbiter-owned client packets + the 6 `SDB_*_PARCEL` W<->A pairs, empty inbox byte-exact | **wired** (T42/T45) |
| Warehouse — 8 W->A requests answered from `items`/`warehouses` rows, `0x2754` sealed | **wired** (T42) |
| Parties — `PartyManager` via `PartyWiring`: 7 `C_` registered, 12 `SA_` gated out of the tunnel | **wired** (T35/T49), byte-exact against cap_social.log (T64) |
| The party contract broker — the 0x2809..0x2810 handshake, target resolved by name | **wired** (T60), order and layout from the capture (T64) |
| Friend / block pushes to World — 0x2862, 0x1475, 0x1476 | **wired** (T64) |
| Guilds — 17 `C_` via `GuildWiring`, rows persisted, `0x27CF` boot load rebuilt from them; 10 of the 12 `SA_` answered | **wired** (T39/T51/T52/T57) |
| GM World forward (anything not Arbiter-owned -> `AS_ADMIN_COMMAND` 0x2829) + `AS_ENTER_WORLD[111]` AdminLevel | **wired** (T46/T47) |
| Packet-handling security — bounds, pagination, allocate-by-count, 37k-input fuzz suite | **wired** (T48/T50), 2 human-owned items open |
| Private chat channels — the channel object, join/leave/kick/password | **designed** (T43), no client packet ever seen |
| Trade broker — 57 opcodes mapped, codec, 4 corrected `.def`s; the 5 DLM requests answered with the refusal form; 15 `C_` packets answer empty | **half real** (T53 research, T55 answers). No listings table — waits on a capture |
| Lord / election / city war, petitions, rankings, appearance & name change, TBA battlepass | **not started** |
| Multi-World / multi-planet | **not started** |
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
| What to capture next, and which task consumes it | `status/CAPTURE-PLAN.md` |
| The two tunnel frame layouts, and the Ticket | `status/MULTIPLAYER-DESIGN.md` §6, `World/TunnelFrames.cs` |
| Every GM command, by side and risk tier | `status/GM-COMMANDS-ARBITER.md`, `status/GM-COMMANDS-FULL.md` |
| Dungeon cool times and entry counts | `status/DUNGEON-COOLTIME.md` |
| Mail, the warehouse, and which pocket id means what | `status/MAIL-WAREHOUSE.md` |
| Where an item is, and which atom op moved it | `status/INVENTORY-DESIGN.md` §7, `status/PERSISTENCE-MAP.md` |
| What a real party looks like on the wire, end to end | `status/PARTY-DESIGN.md` section 13, `status/CONTRACT-DESIGN.md` section 10 |
| Why a warehouse move is not an `SDB_ITEM_SINGLE` atom | `status/MAIL-WAREHOUSE.md` §6 |
| Everything else in the 2026-09-13 relog capture | `status/RELOG-CAPTURE-NOTES.md` |
| Guilds - the object, the SQL schema, the opcodes and the .def corrections | `status/GUILD-DESIGN.md` |
| Guild rows, the Arbiter-side guild handlers, and the wiring they still need | `status/GUILD-DESIGN.md` section 10 |
| What each World -> Arbiter guild frame does, and the two that are still gated only | `status/GUILD-DESIGN.md` sections 12-13 |
| Where character money lives on each side of the wire, and why it is not in 0x27A4 | `status/INVENTORY-DESIGN.md` section 8 |
| How a subsystem's action list becomes sends (parties, guilds, chat, and whatever is next) | `World/ActionDispatcher.cs`, and the wiring section of each design doc |
| Why World logs "handler has not been implemented yet!!!", and who owns each of those packets | `status/CLIENT-REJECTS.md` |
| Whether a packet field is bounds-checked, and what the fuzz suite covers | `status/SECURITY-AUDIT.md` |
| Where `S_LOGIN_ARBITER.status` and `AdminLevel` come from, and why they are unrelated | `status/GM-ADMINLEVEL-TRACE.md` |
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
  `World/WorldReplayTable.cs`, `World/PartyManager.cs`, `World/StarterInventory.cs`,
  `Persistence/CharacterStore.cs`, `Handlers/CharacterHandlers.cs`, `Handlers/SocialHandlers.cs`,
  `Handlers/ChatHandlers.cs`, `Handlers/GmCommands.cs`, `Auth/*`, `Protocol/*`,
  `src/TeraSharp.Arbiter.Tests/`, `status/*.md`, `data/*` — plus any new file a task brief names
  explicitly. That last clause is how `World/ChatManager.cs`, `World/ActionDispatcher.cs`,
  `World/ParcelDbHandlers.cs`, `World/PartyWiring.cs`, `World/GuildWiring.cs`,
  `Handlers/GuildHandlers.cs` and `Handlers/ArbiterClientHandlers.cs` came to exist; once created
  they stay editable.
- **Human-owned — describe the change, never edit:** `World/WorldBridge.cs`,
  `World/TunnelFrames.cs`, `Network/*`, `Handlers/WorldEntry.cs`, `Handlers/HandlerRegistry.cs`,
  `Handlers/LoginHandlers.cs`, `Program.cs`.

## Cowork task queue

- [x] **T1-T51 merged.** `git log --oneline` is the record; the last merge is
  `Merge cowork T48+T50: security audit + fuzz suite (37k hostile inputs green)`.
- [ ] **T52** — guild `SA_` direction + `C_INVITE_USER_TO_GUILD` (in flight).
- [x] **T53** — trade broker research + codec (`status/BROKER-DESIGN.md`, `World/BrokerPackets.cs`).
- [x] **T55** — broker DLM answers: the five `SDB_TRADE_BROKER_*` that carry a DlmId are
  answered, so opening the broker no longer wedges the character. Needs the one
  `HandlerRegistry` loop (`status/BROKER-DESIGN.md` §8.2).
- [ ] **T54** — docs and the live checklist brought up to master (this pass).
- [ ] The live pass itself: `status/LIVE-CHECKLIST.md` sections 5-11, two clients.
- [ ] Then the two capture sessions in `status/CAPTURE-PLAN.md`, in that order.

## Open

**Human-owned, and the only two things the audit left unfixed**

- `World/TunnelFrames.ParseBypassToClient` still adds a packet-supplied length to an offset before
  comparing (`packetStart + packetLength > payload.Length`). For a `packetLength` near
  `int.MaxValue` that sum overflows negative, the bound passes, and `new byte[packetLength]`
  throws `OutOfMemoryException`. Compare on the safe side instead:
  `packetLength > payload.Length - packetStart`. This is H1 from `status/SECURITY-AUDIT.md` §5.1,
  which moved into `TunnelFrames` when the routing was applied — the file the audit named no
  longer contains the code. 0x13F7 is the highest-volume frame on the link, and the fuzz suite
  does not reach this parser (it drives `DbProxyHandlers.TryHandle`, not `WorldBridge.HandleFrame`).
- `Program.Store` has a private setter, so the client fuzz runs against a null store and only
  covers the shallow half of each handler. `internal set` would fix it
  (`status/SECURITY-AUDIT.md` §5.4). Five real `Convert.ToInt32` bugs in `SocialHandlers` were
  found by reading in T50 precisely because the suite could not reach them.
- `Network/PacketReader.cs` is dead code and its `ReadOffsetString` uses a body-relative offset
  where the protocol is packet-relative — delete it or fix it before anyone wires it up.

**No arbiter log line to watch**

- Guild handlers log nothing at Information. Every other subsystem announces itself
  (`Party 0x{Id:X} created`, `Whisper from {Name} delivered`, `C_ADD_FRIEND: …`), so a live guild
  test has to be judged from the client and the DB. One `_log.LogInformation` per guild command in
  `GuildWiring` would close it; `status/LIVE-CHECKLIST.md` §9 says so at the top of the step.

**Needs a live session (`status/LIVE-CHECKLIST.md`)**

- Whether answering `C_SHOW_ITEM_TOOLTIP_EX` is what repaints a used potion's count. The packet is
  certainly the Arbiter's and certainly carries `Count`, but no capture has the exchange
  (`status/CLIENT-REJECTS.md` §2.4).
- Everything else in §§5-11 of the checklist: the empty mailbox, whisper between two clients,
  party invite/accept/leave, guild create/invite/accept/announce, `/@teleport` and `AdminLevel[5]`.

**Needs a capture (`status/CAPTURE-PLAN.md`)**

- **Warehouse**: no capture contains a single warehouse frame; every offset in
  `World/WarehouseHandlers.cs` comes from the PDL dumpers cross-checked against the writers.
- **`ParcelDataNoMsg`** (0x9e8 B): the interior is unknown, so `ParcelCount = 0` is the only
  honest `DBS_LIST_PARCEL` this build can send (`status/MAIL-WAREHOUSE.md` §9).
- **`SDB_CREATE_GUILD2`'s 0x2A-byte fixed part** and the `DBS_CREATE_GUILD2`
  broadcast-then-unicast `DlmId` trick — the highest-risk claims in `status/GUILD-DESIGN.md` (§8),
  and the only place where a wrong size kills the World link.
- **`S_GUILD_INFO` / `S_GUILD_MEMBER_LIST`** with a real guild: 31 unaligned fields.
- **No captured party bytes exist at all** (`status/PARTY-DESIGN.md` §11.1); the A->W frames are
  golden against the decompiled writers and nothing else.
- **No private channel packet has ever been seen** (`status/CHAT-DESIGN.md` §9): the member cap
  default, what the create/edit invite list sends each invitee, and the master-promotion rule are
  all ours, not the binary's.
- Six `S_` reply shapes whose requests cannot be answered properly until a client sends them:
  `S_DUNGEON_COOL_TIME_LIST`, `S_REPLY_GUILD_LIST`, `S_SHOW_PARTY_MATCH_INFO`,
  `S_MY_PARTY_MATCH_INFO`, `S_SHOW_CANDIDATE_LIST`, `S_VIEW_BATTLE_FIELD_RESULT`.
- Trade broker: no listings table, so every answer is an empty form or a refusal. The five
  DLM-carrying requests ARE answered (T55) — that was the part that wedged characters. A real
  broker needs the two tables of `BROKER-DESIGN.md` §6.1 and a manager, both of which wait on
  a capture. `SDB_TRADE_BROKER_START_DEAL` and `_CANCEL_DEAL` are still unanswered on purpose:
  neither carries a DlmId, so neither can head-block anyone.

**Known-incomplete, by choice**

- Parties: four W->A opcodes are gated to `PartyManager` but have no case — `SA_SWAP_PARTY`
  0x139A, `SA_CHANGE_PARTY_MEMBER_AUTHORITY` 0x139C, `SA_JOIN_PARTY_IN_ARBITER` 0x13AB,
  `SA_MERGE_PARTY_TO_RAID` 0x13AC. They log a rejection and send nothing
  (`status/PARTY-DESIGN.md` §11.6). Party matching is registered and deliberately swallowed (§11.3).
- `DBS_LOAD_DUNGEON_COOL_TIME` lists 1 and 2 (clear counts, UI history): stored, not served,
  layouts unobserved (`status/DUNGEON-COOLTIME.md` §3).
- The trailing u32 of the fatigability element (630 / 1626 / 88 in the captures) is sent as 0;
  nothing explains it and World never reads it (`status/REPUTATION-FATIGABILITY.md` §2.3).
- Warehouse `MaxSlotCount` is 0 until a `warehouses` row exists; the real caps are in
  `ServerConfig.xml`, which we do not read.
- Continent fallback table for a character with no stored return point
  (`status/ENTER-WORLD-FALLBACK.md` §9).
- Exit countdown: `S_PREPARE_EXIT` is not in the def registry.
- Friend/blocked memos skip the Arbiter's banned-word + NetModerator stage (we have neither).
- `S_CHANGE_FRIEND_STATE` on login/logout and the `AS_*` block-list pushes are not sent
  (`status/MULTIPLAYER-DESIGN.md`).

**Closed since the last revision of this file** — kept for one cycle so a stale note is recognisable

- T21 relog-into-instance: live-verified twice on 2026-09-14.
- `AS_ENTER_WORLD[111]` AdminLevel: populated from `GmCommandHandlers.LevelOf` (T46).
- The six `SDB_*_PARCEL` requests: answered (T45), so the first mailbox no longer head-blocks.
- The `RegNoop` in-world forward of Arbiter-owned packets: replaced by real handlers plus
  `ArbiterClientHandlers.ArbiterOwned` and the `PacketDispatcher` deny-list (T45).
- The guild/chat/party wiring length bug (TOTAL vs BODY): the registry now calls
  `GuildWiring.MinBodyLength` / `PartyWiring.MinBodyLength`.
- `WorldLink.ReceiveLoop` per-frame try/catch: applied — a throwing handler now logs
  `Link #{Id}: handler for 0x{Op:X4} ({Len} B) threw - frame dropped` instead of closing the link.
- `C_CHECK_ALIVE`: registration dropped (it is not in opcode map 376012).
- The replayed `DBS_INIT_GUILD_DATA` stack-padding leak: retired by allow-listing `0x27CF` (T51).
- `status/*.txt` decompile scratch files: down to two (`arbiter_c_handlers.txt`,
  `arbiter_s_packets.txt`), both still referenced by the scope notes.
- T65 (live 2026-09-16): `SDB_ITEM_TRADE` 0x276A answered (0x238 records, not 0x358) — it was
  head-blocking a character's DLM queue; `SA_JOIN_PARTY_IN_ARBITER` 0x13AB now reaches PartyManager,
  which is the only way a party forms in this build; parcel rows carry their parcel id and receiver
  again and attached gold is paid on claim; friend-accepted system message is SMT 433.

- T69 (cap_social2.log, real Arbiter): `SDB_CREATE_GUILD2` 0x27D4 answered — guild creation was
  unanswered and head-blocked the founder; `SDB_LOAD_REFER_A_FRIEND_LIST` 0x28B0 and
  `SDB_LOAD_INVITE_FRIEND` 0x28B7 promoted out of the replay table (both echo a DlmId); warehouse
  MaxSlotCount is 0x48 and EndPos is the last index, not one past it; TS op 0x10 applied. The guild
  boot terminator and the whole warehouse request/reply layout are confirmed byte-exact.
