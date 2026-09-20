# TeraSharp — Status

Mirrors `CLAUDE.md` section 0. When the two disagree, **CLAUDE.md wins** and this file is stale.
Day-by-day state, live-debug recipes and the deploy loop are in `status/CHAT-HANDOFF.md`.

Read order for anyone new: `CLAUDE.md` (workspace rules at the top) -> `status/HANDOFF.md` §1
(DLMItems — the single failure mode behind every relog hang) -> `status/PERSISTENCE-MAP.md`
(every per-user W->A opcode and how it is answered) -> this file.

Build: `dotnet build TeraSharp.sln` — 0 warnings, 0 errors.
Tests: `dotnet run --project src\TeraSharp.Arbiter.Tests` — **808** `[Test]` methods.
Deploy check: `TeraSharp.Arbiter.exe --selftest` — one PASS/FAIL line per data dependency (T37).
Config check: `TeraSharp.Arbiter.exe --check-config` — every `TERASHARP_*`, the resolved paths
and ports, and the four settings that are legal, silent and wrong (T113).
One page on processes, ports, the file map and the env: `docs/ARCHITECTURE.md` (T114).

**Where the project is (2026-09-20).** T1-T113 are merged. The single-player loop and two players
in one world are live-verified; so are the T105 fixes that came out of a real session (the starter
kit was handing every character the same six item db ids, and `/@perfect_level` wrote a row the blob
then overwrote). Since T64 the work has been **breadth**: every client packet the real Arbiter
handles is now either registered or deliberately left out — 201 of 273, with the remaining 72 listed
in `status/MISSING-HANDLERS.txt` and all of them belonging to systems nobody has started (lord and
city war, petitions, guild quests, the TBA battlepass, rankings, name and appearance change).

Almost all of that breadth is **live-untested**. `status/LIVE-CHECKLIST.md` is still the pass that
closes the gap, and `docs/GO-LIVE.md` is the ordered checklist for putting this in front of players.
The one piece of finished work that is not in master is `status/MULTIWORLD-PATCH.diff` — T103/T108/
T111/T112's changes to the four human-owned files — which waits on the section 5 capture.

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
| Account auth | **live** as accept-all; tera-api validation behind `TERASHARP_AUTH=true` (T31), its reply shape and fail-closed timeout tested, startup banner names the mode (T113) |
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
| Trade broker — 57 opcodes mapped, codec, 4 corrected `.def`s, the 5 DLM requests answered | **wired** (T53/T55/T71/T72/T74/T81). Search, my-listings and sold tabs are served from real `BrokerListed`/`BrokerSold` rows; only the seller-side capture of T81 pinned the last two layouts |
| Lord / election / city war, petitions, guild quests, rankings, appearance & name change, TBA battlepass | **not started** — and these are precisely the 72 in `status/MISSING-HANDLERS.txt` |
| Multi-World / multi-planet | **designed** (T103/T108/T109/T111/T112) — the Cowork-owned half is in master; the four human-owned files are not, and wait as `status/MULTIWORLD-PATCH.diff` |
| Cards and crests — `cards` per ACCOUNT, `card_mounts` per character, `crests` from `SA_LEARN_ALL_CREST_ACQUIRABLE` | **wired** (T83/T85/T85b/T86) |
| Guild perks, crest windows, the guild board, guild search, level ranking, the flag image | **wired** (T83/T95) |
| The In-Game Operation Tool (Alt+A) — 9 `C_ADMIN_*`, user-info tabs, the 400-byte inventory record, bookmarks, the panel's own URL reply | **wired** (T89/T91/T93/T99/T107) |
| The lobby, packet for packet — `S_LOGIN_ARBITER.status` 31/33, `S_DECO_UI_INFO`, `S_CONFIRM_INVITE_CODE_BUTTON`, `S_CURRENT_ELECTION_STATE` | **wired** (T89b/T104/T106/T106b/T106c) |
| The ack / small-reply batch — 26 packets read out of their own `Handler_C_*`, none in any capture | **wired** (T97) |
| Item strings, board posts, item preview, trade log, dungeon ranking, the GM tool tail | **wired** (T99) |
| Watched movies — stored per ACCOUNT, so the intro cutscene stops replaying every relog | **wired** (T104) |
| Admin web — account and character pages, search, online list, grants, bans, announces, admin log, status tab with a live log tail | **wired** (T101/T101b/T101c/T101d/T106), served on loopback behind `TERASHARP_ADMIN_TOKEN` |
| Item names in the admin web — the client's own `StrSheet_Item*.xml`, both sheets merged, loaded lazily | **wired** (T101c/T113) |
| Play time — `characters.play_seconds` stamped at leave-world, account total in the same transaction | **wired** (T113). Not in `S_GET_USER_LIST`: that def has no field for it |
| Logging — console at Warning, a daily `arbiter-<date>.log` taking everything, the spam pushes at Debug | **wired** (T106) |
| Starter kit item db ids drawn from the shared counter instead of a fixed 7..12 | **live** (T105, found and fixed from a real session) |
| Go-live kit — default-deny firewall, scheduled SQLite backup with a verified restore, the checklist | **wired** (`tools/harden-netcup.ps1`, `tools/backup-db.ps1`, `docs/GO-LIVE.md`) |
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
| What runs, on which port, and which file owns which system | `docs/ARCHITECTURE.md` |
| Which client packets are still unregistered, and how that number is produced | `status/MISSING-HANDLERS.txt`, `tools/count-handlers.ps1` |
| Turning this on for real players, in order | `docs/GO-LIVE.md` |
| Multi-World: the design, and the patch the human-owned files still need | `status/MULTIWORLD-DESIGN.md`, `status/MULTIWORLD-PATCH.diff` |

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
  `Handlers/GuildHandlers.cs`, `Handlers/ArbiterClientHandlers.cs`, `World/GuildWarManager.cs`,
  `World/WorldRegistration.cs`, `World/WorldInstances.cs`, `World/WorldServerList.cs`,
  `Web/AdminApi.cs`, `Web/AdminServer.cs`, `Web/ArbiterLog.cs`, `Protocol/ItemNames.cs`,
  `tools/*` and `docs/*` came to exist; once created they stay editable.
- **Human-owned — describe the change, never edit:** `World/WorldBridge.cs`,
  `World/TunnelFrames.cs`, `Network/*`, `Handlers/WorldEntry.cs`, `Handlers/HandlerRegistry.cs`,
  `Handlers/LoginHandlers.cs`, `Program.cs`.

## Cowork task queue

- [x] **T1-T113 merged.** `git log --oneline` is the record; the last merge is
  `Merge cowork T113 + wiring: play time, item names, check-config`.
- [x] **T114** — docs refreshed from git: this table, `CLAUDE.md` section 0,
  `status/MISSING-HANDLERS.txt` (regenerated, 126 -> 72) and `docs/ARCHITECTURE.md`.
- [ ] **The live pass.** `status/LIVE-CHECKLIST.md` sections 5-11, two clients, ~45 minutes.
  It has been the next thing to do since T51 and the untested surface has grown every task
  since; almost everything in the table above marked **wired** is waiting on it.
- [ ] **Apply `status/MULTIWORLD-PATCH.diff`** (`git apply`, not `patch -p1` — T112) once the
  `status/CAPTURE-PLAN.md` section 5 capture settles the ticket-overlap question.
- [ ] Then the two capture sessions in `status/CAPTURE-PLAN.md`, in that order.
- [ ] **Go live**: `docs/GO-LIVE.md`, in order, ending at the firewall.

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

- T70 (cap_social3.log): the broker's TradeData/CalcItemList record (0x188) decoded at last, and
  the two-step Step 1 = read / Step 2 = commit protocol pinned — BROKER-DESIGN.md says what is
  left (the listings table). `AS_LOAD_EXTRAPOINT_DATA` 0x1555 was writing Result and UserDbId at
  the wrong offsets, so World read back user 1 for every character; fixed and byte-exact.
  Of the 31 opcodes in T70's item-2 list, 25 are Arbiter -> World pushes and cannot wedge; of the
  six real requests only 0x1554 carries a DlmId.

- T71 (cap_social3.log): the broker listings table exists and the five two-step DB-proxy handlers
  run on it — register (price read from the op-53 atom at +0x288), cancel, buy, and the two
  collects, with the three different Step-2 reply shapes pinned. All twelve captured broker frames
  reproduce byte for byte. A listed item lives in inven 6, the broker pocket, not in limbo. The
  client half (nine `C_TRADE_BROKER_*` windows) is still the empty forms.

- T72: master is green again — `T71_registering_creates_a_listing_and_pockets_the_item` was the
  test, not the code (`AddCharacterMoney` clamps at zero and the test's seller had none). The
  broker's two client list bodies are decoded and served from the listings table:
  `S_TRADE_BROKER_WAITING_ITEM_LIST` (92-byte elements) and `_BOUGHT_ITEM_LIST` (98-byte), plus
  the float 469.0 `S_TRADE_BROKER_HIGHEST_ITEM_LEVEL` that T45 was answering as 0. REGISTERED and
  SOLD stay on the empty form: the capture never shows a populated one.

- T74 (live 2026-09-16 economy pass): mail attachments arrive again — `SDB_RECV_PARCEL` is
  two-step and its reply carries the full 3544-byte record, not the 0x9e8 list form, so World can
  build the step-2 atoms; warehouse withdraw no longer multiplies the stack (TS op 0x11 is a
  magnitude to remove, not a signed delta); an already-collected broker row answers Success 1 so
  World stops re-asking; `SDB_ITEM_TRADE_LOG` 0x27DD and `SDB_CASH_ITEM_LOG` 0x288C named and
  sealed as one-way. The broker's Active Listings / Sold tabs stay empty: no capture holds a
  populated one and the shipped def is provably wrong about the list we did verify.

- T77 (cap_social4.log): thirteen previously unanswered W->A request/reply pairs are named,
  decoded and answered, and two one-way writes are sealed — the DB-proxy half of EP, character
  cards, crest points, limit reputation, the feudal-lord flag, the battle-pass season and system
  mail. EP is now **persisted**: the six numbers `SDB_UPDATE_EXTRA_POINT` writes survive a relog
  and come back in `AS_LOAD_EXTRAPOINT_DATA`, which answered 53 zero bytes on every login before.
  Guild war, LFG/party matching, servants, the wanted board and the guild logo are **A->W pushes
  only** in this capture (0x14A3/0x14AC/0x14AF/0x14B3, 0x1413, 0x14E1/0x14E2/0x14E6/0x14E9, ...) —
  nothing to pin a handler against, so they are reported rather than built.

- T79 (live 2026-09-16, part 2): `SDB_USER_FORGET_SKILL` 0x2792 named and answered — six pairs in
  cap_social4 went unanswered and head-blocked the account's DB queue on the first forgotten
  skill. Warehouse withdraw no longer duplicates: the op-17 atom carries ItemDbId 0 **and src
  slot 0** in every capture, so the row is resolved by template id — by slot it only worked when
  the bank row happened to sit at slot 0, which is why it failed on a second character. The
  served `ParcelData` record now carries its own send date (+0xAC, six u16s: year, month, day,
  hour, minute, second) and read flag (+0xA8); replaying World's zeros there is what produced
  "cannot claim now" and a deletion date in 2013. The intro cinematic is **not** driven by
  `S_VISITED_SECTION_LIST` — the client reads `S_VISIT_NEW_SECTION.isFirstVisit` (1 in
  cap_newchar_client frame 436 where the intro plays, 0 in cap_social_client frame 374 where it
  does not); the list is built from the stored rows and is byte-exact, but we send it **eighth**
  in the C_LOAD_TOPO_FIN burst where the real Arbiter sends it **first** (frame 257, ahead of
  S_REQUEST_INVITE_GUILD_TAG).

- T81 (cap_social3_client2.log): the broker's last two tabs are real. That log is the **seller's**
  client of the cap_social3 session — the one T71/T72/T74 worked from was the buyer's, which is
  why both tabs were empty there and stayed on the empty form for three tasks.
  `S_TRADE_BROKER_REGISTERED_ITEM_LIST` has a 66-byte element that carries **no name at all** and
  opens with two u16s rather than three (frames 1258 / 1436 / 1457, one, two and three rows,
  oldest first); `S_TRADE_BROKER_SOLD_ITEM_LIST` has a 20-byte fixed part and an element that is
  the bought-list element **plus one i64** at +76 (frame 1572, with seq 1486 as an independent
  witness for everything before it). Both are served from the listings table — Active Listings
  from `BrokerListed`, Sold from `BrokerSold` until the proceeds are taken. T74's "the defs
  disagree, so these two wait for a capture" note in BROKER-DESIGN.md is replaced by the layouts.

- T83 (cap_social4_client + cap_social4_client2): the card page and the guild perk / crest
  windows. Seven client packets decoded and byte-exact - `S_CARD_DATA`,
  `S_ACTIVATE_CARD_COMBINE_LIST_DATA`, `S_CHANGE_CARD_PRESET`, `S_GUILD_PERK_LIST`,
  `S_CREST_INFO`, `S_SHOW_CREST_LEARN`, `S_GUILD_APPLY_COUNT` - across fifteen captured frames
  from two accounts. The eight bytes in the middle of both card packets are a **pdid**: the
  world half (`0x80000AF0`) is the same in all five captures, and the u16 in front of it is a
  per-enter-world serial, not a character id - cap_social4 gives the same character 1 on its
  first login and 5 on its second. Three writes that were being answered and discarded now
  persist: `SDB_REGISTER_CARD` / `_MOUNT_` / `_UNMOUNT_CARD` into a new `cards` table,
  `SA_CREST_POINT`'s NewPoint/NewExPoint into `characters.crest_point/crest_ex_point`, and
  `SA_LEARN_ALL_CREST_ACQUIRABLE`'s ids into a new `crests` table - which is exactly what
  `S_CREST_INFO` reads back. `S_GUILD_PERK_LIST`'s 10-byte element is `spLoadGuildPerkList`'s
  `{int perkId, tinyint, tinyint}`, i.e. the existing `guild_perks` table row for row; its
  fourteen scalars have two instances each, enough to say they are real and not enough to name
  them, so they stay parameters with the captured defaults.

- T85: the two T83 tests that went red when they were actually run. `guild_perks.guild_id`
  REFERENCES `guilds(guild_id)` and the test hung its perk row off guild 7, which was nobody's -
  it creates the guild now, and `UpsertGuildPerk` drops a row for a guild it does not have with a
  warning instead of throwing FOREIGN KEY out of the store. `AddCard` was the one write in T83
  that never landed, and the only one in the class that reuses a bound parameter inside an
  `ON CONFLICT ... DO UPDATE` clause; it is an UPDATE-then-INSERT pair now, with the
  `NoSuchOwner` guard every other packet-derived write has had since T30. The card test asserts
  one field per line so the next failure names itself.

- T85b: the card dumpers, read at last. `SDB_REGISTER_CARD` (Arb_part_017.c:12213, guard 0x19) is
  `DlmId@06, AccountDbId@0A (i64), CardTemplateId@12, Amount@16`; the mount pair
  (11096 / 17206, guard 0x1D) is `DlmId@06, AccountDbId@0A (i64), UserDbId@12, PresetIndex@16,
  CardTemplateId@1A`. Two corrections fall out. **0x4BEFA is 311034, not 310010** - T77 read the
  offsets off the reply echo and then mis-converted the hex, and the wrong decimal was copied into
  three comments and a test expectation. And **the field at payload +4 is an account, not a
  character**: the register frame carries no UserDbId at all, so T85's `NoSuchOwner` guard was
  checking the wrong table and would have dropped every live register whose account id is not also
  a character id.

  **Still open**: the card collection is account-wide and the mount is per character - only the
  mount pair names a `UserDbId`. The `cards` table keys on `character_id` and stores the account
  id in it, which is right only while the two ids coincide (they do in cap_social4, both 1). The
  fix is a schema change - `cards(account_id, card_template_id, amount)` plus a per-character
  mount row - and it wants a capture where the two ids differ before it is worth making.

- T86: that schema change, made. `cards(account_id, card_template_id, amount)` is the collection
  and `card_mounts(character_id, preset_index, card_template_id)` is the arrangement, because the
  three frames name two different owners: SDB_REGISTER_CARD carries an `AccountDbId` and no
  character at all, while SDB_MOUNT_CARD carries `UserDbId@12, PresetIndex@16, CardTemplateId@1A`
  - so one card can sit in several characters' presets at once, and mounting takes nothing out of
  the collection. `MigrateCardsToAccount` rebuilds an existing T83-shaped table: the old
  `character_id` is read as the account it always was, and any row with a preset other than -1
  becomes a mount. (`ix_cards_account` is created there rather than in the DDL - on an upgrade
  `cards` still has the old columns when that block runs.) S_CARD_DATA's array B now carries the
  requesting character's mounts as `[i32 presetIndex][i32 cardTemplateId]`, and
  S_CHANGE_CARD_PRESET names the lowest preset that character uses.

  **Still unpinned**: array A of S_CARD_DATA, which is where the account collection would go -
  no captured frame has an element in it, so its stride is unknown and it stays empty. Every
  captured page is a character with nothing mounted and an empty collection, which is exactly the
  form a character with no mounts still produces, so frame 135 is still byte-exact.

- T89 (cap_final_gm_client2): the In-Game Operation Tool's own packets. Nine C_ADMIN_* requests
  and the S_ADMIN_* replies, all byte-exact against the capture and all gated on the same admin
  level the `/@` chat commands use - the tool is only opened by a client that already passed that
  gate, but these are ordinary client packets and an ordinary client can send them. New table
  `gm_bookmarks`, per account, because the tool is opened from an account and not a character.

  Two things the frames say that no def would have: an empty bookmark list is **page 1 of 1**,
  not page 0 of 0 (frames 527 / 528), and a bookmark's coordinates come back **truncated to whole
  numbers** - frame 1167 saves 16920.03 / 1232.46 / -4427.045 and frame 1168 lists 16920 / 1232 /
  -4427, while the live positions in the by-distance list (frame 1320) keep their fractions.
  `S_ADMIN_WARNING_MESSAGE` is the one tool packet whose reply does not go back to the tool: the
  GM sends it at frame 1399 and the warned player receives it in the OTHER capture, at
  cap_final_gm_client frame 1424.

- T89b: the mode-select screen is `S_LOGIN_ARBITER.status`, and TeraSharp has it backwards.
  The field is an u32 at body +2 and nine captures agree: **31 for every ordinary account**
  (cap_final_client, _client2, _gm_client, cap_social_client, cap_social2/3/4_client - eight of
  them), **33 for the account running the In-Game Operation Tool** (cap_final_gm_client2), and
  **0 exactly once** - cap_newchar_client, the first login of a brand-new account. TeraSharp
  sends `GmAccounts.IsListed(account) ? 31 : 0`, i.e. the brand-new-account value to every real
  player and the ordinary-player value to GMs. 31 is 0b11111 and 33 is 0b100001, so they are not
  a scale - they are different bits, and a GM is not "an ordinary account plus something".

  The lobby is otherwise packet-for-packet identical to cap_final_client frames 3-25, in the
  same order and with the same sizes, except that TeraSharp sends neither `S_DECO_UI_INFO`
  (0x57B3, frame 13, 8 B, body all zero) nor `S_CONFIRM_INVITE_CODE_BUTTON` (0xD41D, frame 15,
  17 B); both sit between `S_LOAD_CLIENT_ACCOUNT_SETTING` and the ten content flags.
  `SendContentFlags` already sends those ten with the right values at the right point - the T89
  report was wrong to call that a timing bug.

- T91 (cap_final_gm_client2): the tool's user-info tabs, its action row, and the two leaderboard
  pushes. `C_ADMIN_REQUEST_USERINFO` (0x9A56) is one packet with an i32 **kind at packet +22**,
  and the capture presses six of them - 1 (inventory, frame 723), 6 (warehouse, 746 and four
  more) and 5 (skill, 1427) are answered, 2 / 3 / 14 are answered by nothing. So kinds 6 and 5
  are served and everything else is logged and dropped, which is what the real Arbiter did.

  `S_ADMIN_GET_USERINFO_SKILL` (0xA3B4, frame 1428) is **two arrays in one packet** -
  20 + 42*13 + 11*9 = 665 - and that is why a single-list reading never closed: the last skill
  entry's `next` is **0, not the crest list's offset**. Each list terminates on its own. The
  eleven ids in the second list are the same eleven `S_CREST_INFO` carries at
  cap_social4_client frame 5108, i.e. T83's `crests` table, so the crest half is served from our
  own rows; the skill half goes out empty (T79: no skill row is ever written).

  `C_ADMIN_REQUEST_USERACTION` (0xA3DB) has an i32 **action at packet +18** and the capture
  presses two: **12 is a teleport** (frame 786 is followed by S_ABNORMALITY_END, S_LOAD_TOPO,
  S_INVEN_USERDATA and two S_ITEMLIST - a full zone reload) and **13 is unnamed**. Both log and
  return; every other id is refused rather than guessed at.

  `S_PVP/PVE_LEADER_BOARD_INFO` (0xB724 / 0x819F, 52 B) are one layout under two opcodes, pushed
  back to back at enter-world. The two i64s are unix 1660205710 and 1662624910 - 2022-08-11 and
  2022-09-08, **exactly four weeks apart to the second** - a closed season whose three values
  (10/30/37 PvP, 3126/3203/9126 PvE) never move across any capture. `C_REQUEST_PVE_RANKING`
  stays unanswered, exactly as the real server leaves it.

  **Not built here**: `S_ADMIN_GET_USERINFO_INVEN` (0xBBED, frame 724, 4439 B) - eleven
  400-byte item records behind a 39-byte head, a decode of its own. Kind 1 fell through to the
  default branch until T93.

- T93 (cap_final_gm_client2 frame 724): `S_ADMIN_GET_USERINFO_INVEN` (0xBBED), kind 1's reply -
  39-byte head + 11 * 400, byte-exact. The 400 is **not a flat record**: a 104-byte item
  (the 22 fields the PDL dumper at Arb_part_018.c:7506 names, ending at `Damaged` @0x67) carries
  a list of **two 28-byte stat blocks**, and each of those carries its own list of **fifteen
  8-byte option slots** - 104 + 2 * (28 + 15*8) = 400. The dumper stops at `Damaged` because a
  PDL dumper skips arrays nested inside an array element, so the two inner lists came out of the
  writer (`User::_Send_S_ADMIN_GET_USERINFO_INVEN`, Arb_part_031.c:5254) and the capture.

  Four head fields are **literals in the writer**: CreatureId is a bare `0` - the packet is
  entirely about one character and never says which, the id is repeated as `OwnerDbId` in every
  element - and ShowInven 1 / IsFirstPacket 1 / NeedNextPacket 0. Money is the character's
  `money` column; MaxInvenSlotCount (0x28) and TCatAmount are World's, so they are parameters
  with the captured defaults.

  **Order**: the writer appends two containers, inventory then equipment, and inside each the
  capture is in **item-db-id order, not slot order** (the bag runs slots 1, 2, 0, 3, 4, 5, 6
  while its ids run 10016..10032). `GmAdminTool.InvenRowsFor` re-sorts `GetInventoryItems` that
  way; four of the eleven elements then come out byte-identical to the capture from the
  `items` row alone, which is what the round-trip test asserts.

  **Left blank**: the enchant / bind / option / durability fields are World's item object and
  the three floats in each stat block are the item template sheet's (121/121/149.8 for the two
  weapons, 5/5/5 and 1/1/1 for the worn armour, 0 for everything stackable). The Arbiter's
  `items` table holds neither, so they are per-item parameters defaulting to zero -
  `CumulatedEnchantAmount` to -1, which is what nine of the eleven captured rows carry.
  The capture is committed as `data/t93_admin_inven_frame724.bin` for the byte-exact test.

- T95 (cap_social3_client2 + cap_social4_client): the guild-search window, the wanted board, the
  level ranking, the guild-bank log and the flag - eleven client packets that were either
  FORWARDED to World (one "handler has not been implemented yet!!!" each) or, for
  C_REQUEST_GUILD_LIST, accepted silently since T51, which is why the window always came up
  empty. All eleven are answered here now; guild storage is the Arbiter's outright.

  **Five replies are byte-exact against a frame**: S_REPLY_GUILD_LIST (cap_social4_client 1640,
  two guilds, and cap_social3_client2 2000, empty), S_REPLY_GUILD_WANTED_WRITING_LIST (2035
  before the post and 2140 after), S_REPLY_SET_GUILD_WANTED_WRITING (2138),
  S_REPLY_INVITE_GUILD_LIST (2040), S_GUILD_LEVEL_RANKING_LIST (2011) and S_BROCAST_GUILD_FLAG
  (46, eighteen instances, all the empty form). S_GUILD_WARE_HISTORY, S_UPDATE_GUILD_FLAG and
  S_REQUEST_GUILD_FLAG_IMAGE_DATA have no captured instance and follow their PDL dumpers.

  **The shipped .def files were checked and NOT used.** S_GUILD_LEVEL_RANKING_LIST.def is missing
  `IsOccupation`, so its element is 38 bytes where the dumper's guard demands 0x27 = 39;
  S_REPLY_INVITE_GUILD_LIST.def and S_REPLY_GUILD_WANTED_WRITING_LIST.def carry the head and no
  array at all; S_BROCAST_GUILD_FLAG.def calls the whole body one int32 when it is a list of
  `[string GuildFlagId][i32 FloatingCastleId]`; S_GUILD_WARE_HISTORY.def has two extra i32 and
  MoneyDelta as an i32, which does not add up to the 0x32 its guard demands. Their opcode
  comments are from another protocol version too. The decompile won every one of those.

  **What the frames say that no def would**: an empty board is page 1 of **ZERO** pages (four
  captured replies agree); the wanted board's RemainTime is **86400 - one day to the second** -
  and CanBeWriting drops to 0 the moment a character posts (2137 -> 2140); and `GuildLogoId` is
  a **string** image id, the same one S_UPDATE_GUILD_FLAG and each S_BROCAST_GUILD_FLAG element
  carry, not the `logo_id` int of the guilds row. New table `guild_wanted`, one row per
  character, cleared when they join a guild and when the character is deleted.

  **Unpinned, and left alone deliberately**: the page size (no capture has more than two rows -
  ours is 20); `GuildSortCriteria` (no capture sends C_REQUEST_GUILD_LIST_SORT); and
  `GuildSize`, which is -1 = "any" in both captured searches and is a dropdown bucket rather
  than a member count - filtering on a guess would hide guilds from the window, so it is read
  and ignored. The ranking is served live, because all four captured replies are empty even
  though two guilds existed: the real Arbiter publishes that list from a scheduled job.

- T97: twenty-six ack/small-reply client packets, read out of their own `Handler_C_*` in the
  decompile. None of them appears in any capture, so nothing here is pinned to a frame - every
  layout is its PDL dumper's, cross-checked against the handler's own `param_3 <` length guard.
  Registering them matters because the fallback FORWARDS an unregistered client packet to World,
  which answers "handler has not been implemented yet!!!" and drops it.

  **Eight reply.** S_ANSWER_PARTY_NAME is **six** entries - the handler's loop is literally
  `while (i < 6)` over Party::GetPartyName(i) - of `[here][next][nameOffset][i32 PartyIndex]`;
  the preset names are a sheet we do not have, so they go out empty. S_VIEW_PARTY_INVITE is TWO
  lists (friends, then guild mates) served from our own tables. S_GET_EVENT_DETAIL,
  S_SEND_VIP_SYSTEM_INFO (53 bytes fixed), S_UPDATE_STACK_ATTENDANCE_EVENT_INFO and
  S_EVENT_MATCHING_BATTLEFIELD_DETAIL_INFO all answer empty - no event, attendance or VIP data
  exists on this server - and the battlefield one echoes the EventId the request asked about.

  **The two report packets are the surprise**: S_CHAT_REPORT / S_USER_REPORT are the FAILURE
  path. Handler_C_CHAT_REPORT looks the reported name up, files the report and returns having
  sent NOTHING; it only builds the five-byte frame, with a literal 0, when the name is unknown.
  Silence means the report was taken.

  **Three store**: C_CHANGE_MY_PROFILE into T30's `profile_message`, and two new columns -
  `characters.description` (C_UPDATE_MY_DESCRIPTION) and `characters.player_state`
  (C_CHANGE_MY_STATE, `User::ChangeUserState`). None of the three replies, and neither does the
  real handler.

  **The rest are accepted and dropped**, which is exactly what their handlers do -
  Handler_C_SAVE_CHAT_SETTING and Handler_C_PARTY_NOTIFY_MY_POSITION are four and five lines of
  nothing but the trace guard - so they are registered on `OnAcceptSilently` rather than given a
  handler. C_LOGIN_WORLD is the one exception worth a line: its whole body is a length check
  that logs `Arbiter <-> World PDL Version Mismatch! Bye :(` under 0x12 bytes.

  **Not done, listed for a later task**: C_REQUEST_CHANGE_PARTY_MATCH_RULE has no
  `Handler_C_*` under that name in the decompile at all and was left unregistered;
  C_GET_ATTENDANCE_REWARD, C_REQUEST_STACK_ATTENDANCE_EVENT_REWARD, C_REQUEST_RECV_DAILY_TOKEN
  and C_QUERY_COIN reach managers (AttendanceEvent, VipSystemManager::TryToRecvDailyToken,
  Account::RequestUpdateCoin) that would need a reward/coin table before they can do more than
  ack; C_REQUEST_COUPON_DATA goes to CouponManager::RequestCouponData, which is an async DB
  round trip; and C_CUSTOM_USER_CUSTOMIZING / the two appearance cancels unwind an appearance
  change this build never starts.

- T99: the items/board tail, the rest of the GM tool and the dungeon ranking - twenty client
  packets, decompile-only again (no capture holds one). Every fixed size below was computed
  from the PDL dumper's fields and then checked against that dumper's own guard; all of them
  agree, which is the cross-check this batch had instead of a frame.

  **Five replies built**: S_BOARD_ITEM_LIST (12-byte head, 16-byte elements, guard 0xb),
  S_REPLY_NONDB_ITEM_INFO (flat 38, guard 0x25), S_PREVIEW_ITEM (26-byte elements, guard 0x1a),
  S_SHOW_TRADE_LOG (16 + 20, guard 0xf), S_IMAGE_DATA, plus S_ADMIN_GET_DUNGEON_USER_LIST
  (22-byte elements, guard 0x16) and the two ranking lists. **S_DUNGEON_RANK_RECORD_LIST is the
  awkward one**: its head carries the caller's OWN record as loose fields behind `HasMyRecord`
  at +0x1C - rank, class, race, gender, record, date, and the two strings - which is how it
  reaches 65 bytes, exactly the guard's 0x40, with 36-byte elements (0x24) for the board.

  **S_IMAGE_DATA is the guild crest again**: C_REQUEST_IMAGE_DATA ends in
  `Guild::SendGuildLogoNoLock`, so it is T95's flag image under a third opcode, and it resolves
  the same way - image id string -> the guild whose `logo_id` it is -> that blob.

  **Three store**: new `item_strings` (C_SET_ITEM_STRING and C_REWRITE_ITEM_STRING both write
  it - the rewrite packet exists to write over one already there) and `board_posts`
  (C_WRITE_BOARD), plus `DeleteGmBookmark` for C_ADMIN_REMOVE_CUSTOM_BOOKMARK, which deletes
  and then re-sends the whole list exactly as T89's add does. New `GetItem(itemDbId)` too:
  C_PREVIEW_ITEM names items by db id alone.

  **Four GM packets are World's work, not ours**: C_ADMIN_GM_TELEPORT, _MAPTELEPORT,
  C_ADMIN_REMOVE_NPC and C_ADMIN_VANISH_PET all end in the same forward - the teleport one
  hands World inter-server message **0xd0** - and the Arbiter never moves anything itself. All
  four are gated on the same admin level (`*(int *)(user + 0x3b98) < 1` in the binary) and
  logged. C_ADMIN_LOBBY ends somebody else's session, which is the human-owned half of this
  build, so it is logged too.

  **Not built, with the reason**: S_SHOW_TRADE_ITEM (C_SHOW_TRADE_ITEM) is a full both-sides
  tooltip of a past trade and there is no trade log to read - answering it with an empty trade
  would show a GM two empty inventories as if that were the trade. C_ADMIN_GMEVENT_NOTICE is
  logged rather than broadcast: it drives the GM-event manager (the OX quiz, the summons) and
  no event is running to notice about.

- T103 (multi-world step 1, status/MULTIWORLD-DESIGN.md section 4 item 4): SA_REGISTER (0x138A) is
  answered for real instead of replayed. New `World/WorldRegistration.cs` parses the request
  (`IsBypass u8 @0`, `PlanetId @1`, **`WorldId @5`**, `TotalBypassCount @9`, `BypassIndex @13`,
  `WorldVersion @17`, payload-relative) and builds AS_REGISTER (0x138B) the way
  `Handler_SA_REGISTER` does: echo IsBypass / WorldId / BypassIndex, then our own version and the
  result. **All 25 replies in the tap reproduce byte for byte** (one control link with
  IsBypass 0 / BypassIndex -1, then 24 bypass links 0..23), so single-World behaviour is unchanged.

  Three things the binary settles that the capture could not. **ArbiterVersion is a constant, not
  an echo** - the handler is `if (WorldVersion == 0x5bc07)`, and echoing would defeat the field;
  ours happens to match, which is why the capture cannot tell. **Result is 1 only when the version
  matches AND `WorldId < 0x20`**, and 31 is the highest id in ServerConfig.xml. **An id that is not
  in the config is NOT refused** - the handler logs `Unknown WorldServer [id=%d]` and carries on -
  so only the version and the ceiling are refusals, and a refused link still gets a reply telling
  it which link it is.

  Also new: `PerWorld<T>`, one instance per WorldId created on first use. The ticket space is the
  reason - a Ticket indexes one World's bypass slots, so two Worlds hand out the same numbers and
  today's single global `TicketAllocator` would give two players in different Worlds the same
  tunnel key. World 0 still gets a fresh allocator on first use, so the existing tunnel tests are
  untouched. `TicketAllocator` itself is not changed (and not ours to change); `PerWorld` takes a
  factory so it needs to know nothing about it.

  **Not wired yet** - `World/WorldBridge.cs` is human-owned, and the T103 report carries the diff:
  the per-World link set, `LinksOf(worldId)`, `WorldLink.WorldId/BypassIndex`, handling 0x138A
  before the replay lookup, and `AllocateTunnelKey(worldId)`. Until that lands, the replay table
  still answers 0x138A and nothing behaves differently.

- T105 (live 2026-09-19): **the starter kit gave every character the SAME six item db ids**, and
  since T44 made the bag real rows that was a collision, not a convenience. `UpsertItem` is an
  upsert on `item_db_id`, so seeding character B MOVED character A's six rows to B; A came back
  with an empty bag, `SDB_USER_LOAD_INVENTORY` re-seeded it, and the rows went back the other way.
  Two characters played tug of war over ids 7..12 and each lost its whole bag in turn - the pocket
  is cleared before a seed - which is the live "player 3 -> 6 starter items ... seeded 6 starter
  row(s)" for a character that had played. Reproduced in sqlite against the real DDL: seed 3, seed
  4, and owner 3 goes from six rows to none.

  Fix: `DbProxyHandlers.SeedStarterRows` draws the ids from `ReserveItemIds` - the same counter
  every other item uses, which is what the real Arbiter does (7..12 in the capture is just where
  its counter stood for the first character ever created) - and patches the id inside the row's
  536-byte record so the rebuilt 0x27A4 agrees. `StarterInventory.Build` is untouched, so every
  byte-exact starter test still passes: those ids are the payload's, and the reply is rebuilt from
  the rows straight after the seed. `MigrateSharedStarterItemIds` renumbers what is already in the
  file (anything below `FirstItemId`), keeping the owner, so an existing database stops colliding.
  Nothing else since T74 loses rows: the T101b soft delete parks items in `deleted_items` and
  `RestoreDeletedCharacter` puts them back, and it has no production caller yet; T86, T95 and T99
  add tables and a cascade that only run on a real delete.

  Also: **/@perfect_level wrote the row and the blob kept the old level** - the lobby reads the row
  (70) and enter-world serves the saved blob (3). `StarterBlob.LevelOffset = 204` is stamped on the
  way out next to T59's money, pinned against six real 0x2738 blobs (1/1/1 fresh, 8 for two that
  had levelled, 70 for "dob" after the command); +208 and +216 move with it but are hp and mp -
  1953 hp at level 1, 85956 at 70. **Exp is deliberately not stamped**: /@perfect_level leaves it
  at the level's base, the level-1 and level-70 blobs of the same character differ in 640 runs, and
  none reads as a total-exp counter, so guessing an offset would overwrite hp or mp.

- T108 (multi-world step 2, status/MULTIWORLD-DESIGN.md section 7): the enter-dungeon handshake
  is **routed** instead of echoed. New `World/WorldInstances.cs` holds the instance registry
  (`DungeonChannels`: `(ContinentId, ChannelId) -> WorldId`, fed by `SA_ADD_DUNGEON_CHANNEL`
  0x13C5 and emptied by 0x13C6 - both were falling through to a replay table that has nothing
  for them), the in-flight `PDId -> asking World` map, and `DungeonRouting`, which sends 0x13BF
  to the World that owns the requested continent and 0x13C1 back to the World the user is still
  in. `WorldRuntime` (in `World/WorldRegistration.cs`) is the per-World `IsReady` + game-id
  counter + one-shot `MarkReady` that `DbProxy.OnWorldReady` hangs off.

  Pinned this task: 0x13C5 is `[i32 ContinentId][i32 ChannelId][24 B DungeonOwnerInfo]`, min
  payload 32 (the owner struct is memcpy'd with its padding, which is why the frame is 0x26 and
  not 0x23); 0x13C6 is the first eight bytes of that, min payload 8. The routing key is
  `DungeonEnterContext[0]` = payload 8, the value `Handler_SA_REQUEST_ENTER_DUNGEON` hands its
  own continent-to-World lookup. `Handler_SA_RESPONSE_ENTER_DUNGEON` finds the user from the
  PDId's HIGH 32 bits and answers on that user's **own** World session - so the responder is not
  the recipient, which is invisible with one World and wrong with two.

  **Single-World is unchanged by construction**, not by care: both tables start empty and the two
  `WorldRouting` hooks start null, so every decision resolves to the link the frame arrived on -
  the same `link.SendFrame` as before. The T10 capture tests are untouched and
  `T108_with_one_world_the_dungeon_handshake_is_unchanged` re-pins them with a channel registered.

  **Not built, with the reason**: nothing *allocates* an instance. Routing can only find a World
  that has already announced a channel or is configured for the continent, so the first entry into
  a continent no World owns still goes to the asking World. The real allocator reads the per-World
  load feed (0x164C/0x164D), which is dropped today, and section 5's capture has not been taken.

  **Not wired yet** - `World/WorldBridge.cs` is human-owned, and this is now a **cumulative**
  patch: the T103 diff was never applied, so the T108 report carries both (per-World link sets,
  `WorldLink.WorldId/PlanetId/BypassIndex`, 0x138A answered before the replay lookup,
  `SendFrame(worldId, ...)`, `PerWorld<WorldRuntime>`, `AllocateTunnelKey(worldId)`, and the two
  `WorldRouting` hooks). Until it lands, nothing behaves differently.

- T109 (multi-world, proposal + research). Two things, no production code changed.

  **`status/MULTIWORLD-PATCH.diff`** is the cumulative T103 + T108 `WorldBridge` / `WorldEntry`
  patch as a file, generated against master of 2026-09-20 and verified to apply cleanly with
  **both** `git apply -p1` and `patch -p1` (and the applied result compared byte for byte against
  the intended files). It is 12 hunks: `WorldLink.WorldId/PlanetId/BypassIndex/Registered` from a
  real SA_REGISTER instead of the replayed one, `PerWorld<TicketAllocator>`,
  `PerWorld<WorldRuntime>` for IsReady + the game-id counter, `SendFrame(worldId, ...)` with the
  old form delegating to world 0, `LinksOf`, per-World teardown on the last link, the two
  `WorldRouting` hooks in the constructor, and the one `WorldEntry` line that targets
  AS_ENTER_WORLD at the World owning the instance. Its header says what it does and, in one line,
  that it should land **after** the section 5 capture, because the ticket-overlap question is the
  one thing that could still change the tunnel map.

  **MULTIWORLD-DESIGN.md section 7.1** - and it corrects section 7's open item 1. **There is no
  allocator.** `WorldSessionManager::GetDataSession(continentId)` (Arb_part_046.c:2545), the
  lookup `Handler_SA_REQUEST_ENTER_DUNGEON` routes on, is a config read: the continent's
  `worldServerInfo` list from PlanetInfo, and a count other than exactly 1 is an assert at
  `WorldSessionManager.cpp(356)`. One continent, one World, from `ServerConfig.xml`. The only
  health state the Arbiter keeps is a 32-slot array whose entry is `== 2` once a World's last
  bypass link registered - the same 0x20 ceiling T103 pinned.

  0x164C/0x164D is therefore **not** an allocator feed. 0x164C has no payload at all (6-byte
  frame); 0x164D is `[count][firstOffset][PlanetId][WorldId]` + N x 16 B InstanceList elements,
  min frame 22; and `Handler_SA_WORLD_SERVER_STATUS` stores none of it - it relays the lot to
  MatchServer as 0x4670 `AM_WORLD_SERVER_STATUS` and sends nothing at all if no MatchServer
  session exists. **Next step needs no capture**: load `WorldServerList` into
  `DungeonChannels.MapContinent` at startup and cross-World entry works on the first attempt.
  0x164D stays dropped until MatchServer exists - answering it would mean sending 0x4670 to a
  session that is not there.

- T111 (multi-world step 3, MULTIWORLD-DESIGN.md section 7.2). New
  `World/WorldServerList.cs` reads ServerConfig.xml's `<WorldServerList>` - the six rows, their
  `<Continent>` children and `loadAllContinents` - into `DungeonChannels.MapContinent`: 16
  continents mapped (world 10 gets 102/103/110/112/113/115/116/117/118/1200, world 12 gets
  9920/3023/3027/3126/3026, world 31 gets 156), catch-all world 0, and continent 9827 - the
  dungeon in cap_newchar - is in no row, so it stays with the catch-all. **That file is the
  whole allocator** (section 7.1): a continent claimed twice keeps the first owner and logs,
  because the real Arbiter asserts at `WorldSessionManager.cpp(356)` and routes it nowhere, and
  stranding players on a config typo is worse. A missing file seeds nothing.

  `WorldForContinent` now prefers the **configured** owner over an announced channel, mirroring
  `WorldSessionManager::GetDataSession`. `WorldForChannel` is unchanged - config decides the
  continent, the channel table still says which instance a World announced.

  A third hook, `WorldRouting.IsLive`, guards every routing decision, and it is what makes
  seeding the config safe: ServerConfig.xml hands continent 102 to world 10 whether or not
  anyone started world 10, and routing AS_ENTER_WORLD to a World with no sockets means the
  player never loads. Unlive target -> the asker, or the catch-all World. With no per-World link
  sets nothing is live, so **the tree before the patch behaves exactly as it did.**

  `status/MULTIWORLD-PATCH.diff` regenerated against master of 2026-09-20: 24 hunks over
  `WorldBridge.cs` (631 lines), `Handlers/WorldEntry.cs` (316) and now `Network/GameSession.cs`
  (355), `git apply --check -p1` and `patch -p1` both clean, applied result byte-compared.
  New beyond T109: `GameSession.CurrentWorldId`; the tunnel map keyed **(WorldId, Ticket)**
  (section 4 item 5) with every uint-keyed entry point kept as an overload onto world 0 so no
  tunnel test moves; and WorldEntry building AS_ENTER_WORLD with no Ticket, reading the
  destination World out of it, allocating in that World's space and stamping the Ticket at
  payload 80 - the same bytes, since `tunnelKey` is written in exactly one place.

  Tests: `T111_the_world_server_list_seeds_one_owner_per_continent` (six rows, 16 continents,
  duplicate claim, id past the 0x20 ceiling, unparsable config),
  `T111_the_configured_owner_beats_an_announced_channel`,
  `T111_two_links_route_0x13BE_to_the_owner_and_0x13C1_back` (0x13BF to world 13, 0x13C1 back to
  world 0, then the same two with world 13 not running and both staying on the asker).

  **Step 4**: the per-player control frames still go out on world 0 - 0x1460, 0x1392, 0x1433,
  0x138F, 0x1390, 0x1439. Each is a one-line WorldBridge method needing a worldId from the
  session; none matters until a player is actually in a second World.

- T112 (multi-world step 4, MULTIWORLD-DESIGN.md section 7.2). The six per-player control
  frames now go to the player's own World, threaded from `GameSession.CurrentWorldId`:
  `NotifyPlayerLeave(worldId, ...)` for 0x1460 + 0x1392, `NotifyTopoLoaded(worldId, ...)` for
  0x1390 + 0x138F, and `SendFrame(s.CurrentWorldId, ...)` for 0x1439 in HandlerRegistry's
  C_LOAD_TOPO_FIN arm. 0x1433 needed nothing: the SA_LEAVE_WORLD reply already goes out on the
  arriving link and GameSession's copy took CurrentWorldId in T111. Each frame addresses the
  user by id INSIDE that World, so the wrong World is a lookup miss, not a misroute - for
  0x1392 it is the `Critical Error LeaveWorld` crash path. Every new signature is an overload;
  the old no-worldId forms delegate to world 0, so nothing outside the patch moves.

  `status/MULTIWORLD-PATCH.diff` regenerated: **four** human-owned files now -
  `Handlers/HandlerRegistry.cs` joins WorldBridge (631 lines), WorldEntry (316) and
  GameSession (355) - 28 hunks, applied result byte-compared against the intended files.

  **Two findings worth keeping.** (1) The patch must be applied with `git apply`. The file is
  stored all-CRLF (its context lines come from CRLF sources, and the write path normalises the
  rest), and GNU patch strips trailing CRs by default and then fails every hunk with
  `different line endings`; `git apply` and `patch -p1 --binary` both work, and both are now
  checked on every regeneration against a copy normalised the way it lands on disk. The T109
  and T111 headers recommended plain `patch -p1`, which would not have worked. (2) 0x14FF and
  0x1500 stay on world 0 on purpose: same one-line change, but one caller
  (`Handlers/ArbiterClientHandlers.cs:1896`) is Cowork-owned, so adding the parameter in the
  patch would break the build for everyone until the patch lands. They move with that file.

  Also: CA2017 in `World/WorldServerList.cs` - the duplicate-continent warning had four
  placeholders (`{A}` twice) for three arguments. Reworded to three. Every Log* call in that
  file re-checked: placeholders == arguments throughout.

- T115 (game log). The five LogDB writes World sends and the Arbiter threw away now decode into
  a `game_log` table. New `World/GameLogPackets.cs` + `status/GAME-LOG.md`.

  **The set is exactly five** - grepping the Arbiter for `Handler_SDB_*LOG*` finds
  SDB_ITEM_TRADE_LOG (0x27DD), SDB_ADD_PVP_USER_LOG (0x27FE), SDB_ADD_PK_USER_LOG (0x27FF),
  SDB_ADD_GROUP_DUEL_USER_LOG (0x2800) and SDB_CASH_ITEM_LOG (0x288C);
  SDB_INIT_LOSS_LOGINTIME_REVISION_SECOND matches the grep and is not a log. SA_LOG_QUEST_END
  and SA_RELAY_LOG are the SA_ family, outside 0x27xx-0x29xx.

  Two reference shapes run through these frames, and the group-duel dumper's strides (6, 10,
  0x0E, 0x16) are what separate them: a **list ref** is `[i32 firstOffset][i32 byteLength]`, a
  **wstring ref** is one `i32` offset. 0x27FE and 0x27FF are byte-identical frames and share a
  decoder. **0x288C is the only one with a capture** - cap_social2 seq 1849, cap_social3 seq
  1037/1364, cap_social4 seq 8660, all 54 bytes - and its 40-byte CashItemLog is pinned from
  both ends: WorldServer's vector strides 0x28 and 54 - 6 - 8 is 40. Field names come from the
  producer, DBIncreaseUserInvenSize::ExecuteCommitSQL.

  `game_log(logged_at, category, action, account_id, character_id, target_id, item_db_id,
  template_id, amount, money, extra)`, indexed four ways, `extra` a flat JSON object. The frames
  carry no timestamp, so `logged_at` is arrival time, recorded at insert.
  `QueryGameLog(account, character, category, from, to, page, pageSize)` - all optional, newest
  first, pageSize clamped to 200, page checked unsigned, and a character matches **actor or
  target** so a received trade shows on their page.

  **Not decoded, with the reason.** The item lists inside 0x27DD and 0x2800 are recorded as byte
  counts: no capture has either frame and both dumpers hand the list to the generic reference
  printer without naming an element, so a layout would be invention. And `CashItemLog +16` -
  `FUN_1404ce180(user)` - reads 1003, 2, 1003, 1 across the captures, and 1003 matches no
  character in those sessions; it is stored as character_id AND echoed to `extra.rawUserId`, so
  a capture that settles it can re-file without re-decoding. Guessing it into account_id would
  mis-file every cash row.

  **Still one-way** - every handler ends at `return 1` with no writer, and the four captured
  0x288C frames draw no A->W frame. 0x27DD, 0x27FE and 0x288C came OUT of
  `WorldReplayTable.OneWayFromWorld` to make it work (a sealed opcode never reaches a handler),
  which is the same invariant T108b tripped over; `T115_the_log_opcodes_are_handlers_not_sealed`
  now pins it for all five.

- T116 (admin web: game log search). `GET /api/game-log` on `AdminApi` - **no Program.cs
  change**, the route hangs off `Handle` and `AdminServer` already dispatches there - plus the
  page. status/GAME-LOG.md section 6.

  One `who` box takes an account name, a character name, an account id or a character id;
  digits resolve as a **character id first** and only then as an account id, because every one
  of the five decoded frames names a character and only some name an account. A term that
  matches nothing answers 404 / result 2 rather than quietly returning the whole log. The reply
  carries `total` from `CountGameLog` with the same clauses in the same order, `who` as
  resolved, `categories` from `GameLogPackets.Categories` so the dropdown is not a second copy
  of the list, and per row the actor and target NAMES beside their ids, `item` as
  `{templateId, name}` through `Protocol.ItemNames`, amount, money and the extra JSON as an
  opaque string.

  `QueryGameLog` / `CountGameLog` gained an `action` filter - `LIKE <prefix>%` with
  `ESCAPE '\'`, since SQLite has no default escape character and without it an action
  containing a percent sign would still act as a wildcard. Both take the same six filters now,
  so the page's total and its pages cannot disagree. Every existing call site passes named
  arguments, so inserting the parameter moved nothing.

  The page grows one nav entry, **Logs**, with two panels: the game-log search (who / category /
  action / date range / page size, prev-next paging) and the admin-log viewer, which moves in
  from its own tab rather than being duplicated. The page literal still has zero double quotes.

  Tests: `T116_the_game_log_endpoint_filters_and_pages`,
  `T116_the_game_log_rows_name_the_actor_target_and_item`,
  `T116_the_game_log_endpoint_refuses_cleanly` (unknown subject, no token, token unset, clamped
  size, absurd page, unparsable numbers) and `T116_the_admin_page_carries_both_logs_in_one_tab`.

- T118 (leaderboard, step 1). status/LEADERBOARD.md. **The brief's premise was wrong and the
  live proxy mod was guarding the wrong field** - both corrected, with citations.

  There is no page and no size on `C_REQUEST_PVE_RANKING` / `C_REQUEST_PVP_RANKING`: both defs
  are `int32 season / int32 id / int32 class` and the handlers (Arb_part_041.c:9474 / :9543)
  read exactly those behind a `param_3 < 0x10` guard. The `count + page*(-10) + 9` pagination
  crash is `C_VIEW_GUILD_WAR`, which the mod already bounds.

  Of the three fields, `id` (+8) - the one the mod validated - is **safe**: it goes to
  `DungeonDataSheet::GetDungeonTemplate` (Arb_part_003.c:3388), a red-black-tree find that
  returns 0 for an unknown key, checked by the caller. The crash is `class` (+0xC), unchecked:
  the past-season path advances a cursor by `(class + 1) * 3` pointers (Arb_part_050.c:10630) - a
  raw pointer index, 24-byte stride, no bound - and the current-season path reaches
  `RankTree<..>::ClassRank` (Arb_part_049.c:11647) whose `if (0xe < (ulonglong)(int)param_2)`
  calls a no-return function, with a sign-extending cast so negatives abort too. **Safe set:
  0..14 or 16** - 16, not the 15 the mod allowed, and 15 is an index the past-season path walks
  off the end with. (The writer at :10593 stamps 0xBEDC = S_PVE_RANKING_LIST, which is what
  identifies the function.)

  `status/EXPLOIT-FIX-RANKING.diff` (2 hunks, applies with **`patch -p1`** - index.js is
  LF-only and the file lands all-CRLF, so plain patch's CR-stripping is what makes it match;
  `git apply` needs `--ignore-whitespace`, `--binary` fails; applied result byte-compared and
  `node --check`ed) makes both packets take the same field check and logs pass and drop.
  `data/proxy-defs/C_REQUEST_PVE_RANKING.1.def` is the 34 bytes to copy into the proxy's
  `data\definitions\` - it exists in tera_v100_MASTER_FINAL, which is why the "no def, drop all
  PVE" rationale was stale.

  New `LeaderboardPackets` in `Handlers/ArbiterClientHandlers.cs` answers all nine opcodes,
  none of which was registered: the two ranking requests (class-validated, empty lists), the
  two inter-party-match lists, the match rule (type echoed), the group-duel record (all zero),
  and three acks - the party-match page and `C_REQUEST_MY_PARTY_MATCH_INFO`, plus
  `C_CHANGE_USER_NAME`, which is **logged and not applied**: no S_ opcode exists for it and the
  rename path with checks is T88's. `S_PVE_RANKING_LIST` is hand-built because its .def has no
  fields while the writer emits a `[u16 count][u16 offset]` header.

  Tests: `T118_the_ranking_class_range_is_the_one_the_arbiter_survives`,
  `T118_the_pve_ranking_list_is_an_empty_client_list`,
  `T118_the_body_sizes_are_the_handler_guards`,
  `T118_the_leftover_replies_write_through_their_defs` (every reply written with the real
  registry, which is what catches a mistyped field name),
  `T118_the_two_acks_send_nothing_and_change_nothing`.

- T122 (live 2026-09-20): **character select showed the login snapshot, not the row** - level 8
  in world, level 1 on the way back out. `LoginHandlers.OnGetUserList` builds each
  S_GET_USER_LIST element from `s.Account.Characters`, which `FakeAccount.LoadFromStore`
  snapshots once at login; everything that happened in world changed the `characters` row and
  left the snapshot alone.

  Fixed in `CharacterHandlers.FillLobbyFields` - the per-element `GetCharacter` call T76 already
  made - so it refreshes the ELEMENT rather than the cache: **no change to the human-owned
  `OnGetUserList`, and no extra query.** It now also sets level, name, position, gender/race/
  class, weapon/body/hand/feet and the appearance/details/shape blobs from the row, on top of
  T76's worldId/guardId/sectionId/lastLogoutTime/restBonusXp. A character id the store does not
  know still leaves every field alone rather than zeroing the entry.

  **hp and mp have no column**, so the saved world blob is their only source: `StarterBlob`
  gains `HpOffset = 208` / `MpOffset = 216` and read-only `ReadHp` / `ReadMp`, the two
  neighbours of `LevelOffset` that the T105 note already identified (+208 reads 1953 at level 1,
  2878 at 8, 85956 at 70 across the same six blobs). Read-only on purpose - that note calls +208
  runtime state World owns, and stamping it out would overwrite the hp a player actually has.
  A missing or short blob leaves the caller's template values, so a character who has never
  entered the world does not appear with 0 hp.

  **Not refreshed, with the reason**: `isNewCharacter` (the caller hard-codes true and no column
  contradicts it), `maxRestBonusXp` (RestBonusDataSheet, which we do not load - T76's note
  stands), and exp, which S_GET_USER_LIST has no field for. The character SET still comes from
  the cache, which is correct: create and delete both update it.

  One existing assertion changed: `T76_lobby_fields_match_the_captured_user_list` asserted "and
  no other field is touched" against `level`, which is exactly what T122 now touches - it checks
  the row's level instead.

  Tests: `T122_the_lobby_shows_the_stored_level_not_the_login_snapshot` (level 11 from the row,
  then 8 after `UpdateLevelAndExp`, with T76's five fields still landing and an unknown id
  changing nothing) and `T122_the_lobby_hp_and_mp_come_from_the_saved_blob`.

- T119: **the leaderboard answers from our own data** (`status/LEADERBOARD.md` section 6).
  T118 left both ranking replies as empty forms; they are now filled. New
  `World/RankingBoards.cs` builds `S_PVE_RANKING_LIST` (0xBEDC) and `S_PVP_RANKING_LIST`
  (0x62FA), and `CharacterStore` gained the two sources.

  **No capture exists** - the real Arbiter never answers `C_REQUEST_P*_RANKING` on 100.02,
  confirmed live - so the PvP layout is the shipped def and the PvE layout is read off the
  writer `PVERankingSystemManager::SendRankList` (Arb_part_050.c:9747, stamping 0xBEDC at
  :9758), whose own def has no fields at all: 31 fixed bytes, then a NUL-terminated UTF-16
  name.

  **Sources.** PvE = `SUM(dungeon_cooldowns.clear_count)`, which `SA_UPDATE_DUNGEON_CLEAR_COUNT`
  (0x13B7) writes on every clear. PvP = `game_log` rows with `category='pvp'` and
  `action='pvp.kill'`, counted per actor (`pk.kill` is the outlaw counter and is not the
  board). The brief asked for a game_log **dungeon** category: there is none - T115 created
  eight and no decoded log opcode carries a clear - so the clear counter is the nearest real
  source rather than an invented one, and a test says so.

  **Three PvE scalars have no name** in the def, any dumper or any capture. The binary gives
  only the relation: `IsRookie(int,int,int)` is `(myLevel < B + A) && (B <= myLevel)` over the
  +11 and +7 values, so those two are a level band, not the entry's level. Our choice is
  written down in `BuildPveRankingList`: +15 rank, +19 score (i64), +27 class, +11 the entry's
  level, +7 a band width of 0, and the rookie flag computed from what we wrote - so it is
  always 0, which is "we do not model the rookie band" rather than a flag that means nothing.

  Ranks (competition - see T133b), ties broken by character id, class filter on T118's range (`0..14` exact, `0x10`
  aggregate), 50 a page, and the requester's own row appended when the page does not hold it.
  The request has no page field, so the handler sends page 0 plus that row. Only
  `season == 1` - the season `S_P*_LEADER_BOARD_INFO` advertises (T91) - has rows; anything
  else is an empty board rather than a wrong one. Both queries drop deleted characters and
  zero scores and cap at `RankingScoreLimit = 500`.

  Registry unchanged: both opcodes still point at `LeaderboardPackets.OnRequestPveRanking` /
  `OnRequestPvpRanking`.

  Tests: `T119_the_pve_list_is_the_writers_thirty_one_byte_element`,
  `T119_the_pvp_list_matches_its_def_through_the_writer` (the same rows written through the
  shared `DefinitionWriter` must be byte-identical - the only independent witness this packet
  has), `T119_an_empty_board_is_the_form_t118_already_sent`,
  `T119_ranks_are_dense_stable_and_class_filtered`,
  `T119_the_page_always_carries_the_requesters_own_row`,
  `T119_the_two_boards_come_from_real_stored_progress`.

- T126: **the leaderboard class dropdown is not ours to fill - but three PvE fields were in
  the wrong slots** (`status/LEADERBOARD.md` section 7).

  **The reported bug is a no-op.** Nothing on the wire carries a class name or filter list:
  not the 376012 map (29 RANK/LEADER opcodes, none of them a list), not the 4550-def set, not
  either `S_PVE_RANKING_LIST` writer (the header is `[u16 count][u16 firstElementOffset]` and
  nothing else), and no S->C packet in `cap_final_gm_client2`. `S_P*_LEADER_BOARD_INFO` is a
  selector feed, but for the **board** - PvE dungeon ids 3126/3203/9126, PvP battleground ids
  10/30/37, which the client echoes straight back as `id` (capture frame 1989 sends
  `0x0C83` = 3203). TeraSharp already sends both byte-identical to the capture. The class
  dropdown is client-side.

  **What the research did find.** The element source is a `ReturnRankInfo<T>` and the two
  boards share its shape - `<int>` is 0x10 B (score +4, a +8, b +0xC), `<LevelTime>` is 0x20 B
  (level +8, i64 time +0x10, a +0x18, b +0x1C) - and both writers copy them the same way:
  score to +0x0F, a to +0x0B, b to +0x07 (Arb_part_050.c:9812-9821, :10266-10270). The PvP
  def names two of those: `rank` at +11, `rating` at +15. **So T119 had the PvE rank and score
  swapped**: +11 is the rank, +15 the stageLevel, +19 the i64 clearTime, +7 changedRank. The
  names come from `S_USER_PVE_RANKING`'s def, written from the same `Score()` call. Our clear
  COUNT now goes in `clearTime`, which is the one field whose units the client reads
  differently from how we mean them - said in the code rather than hidden.

  **The PvP def is misordered.** It declares `int32 unk; byte unk2` (i32@6, u8@10); the writer
  stores the byte at +6 and the i32 at +7. The decompile wins. Both are 0 in every frame we
  send, so no byte changes and the def cross-check still passes - only the shape is corrected.

  **The missing second frame.** `SendNowSeasonRank` sends the list AND the requester's own
  line; T119 sent only the list. Added `S_USER_PVE_RANKING` (0xE748, 25 B: `byte rookie, i32
  changedRank, i32 rank, i32 stageLevel, i64 clearTime`) and `S_USER_PVP_RANKING` (0xC844,
  17 B, the same minus the i64), each pinned to both its def and the writer's argument order
  (:10074 / :10518). An unranked requester gets rank 0 and zeroes rather than no frame, which
  is what the server does when `Rank()` misses its tree.

  Registry unchanged; the two handlers now send two frames each.

  Tests: `T126_the_pve_rank_and_score_slots_were_swapped`,
  `T126_the_user_ranking_companion_is_byte_exact`,
  `T126_self_finds_the_requesters_row_on_the_whole_board`, plus the two T119 byte-exact tests
  updated to the corrected element and the PvP one extended with the def/writer disagreement.

- T128: **GM invisibility is a toggle now** - `/@vis`, `/@invis`, and `/@vaporize` fixed.

  A GM spawns vaporized because the Arbiter pushes `S_ADMIN_GM_SKILL 00 00 00 00 01` at
  enter-world (T120/T121, cap_final_gm_client2 frames 99 and 2445). Nothing turned it back
  off except Alt+A: `/@vaporize` and `/@invisible` are WORLD commands and never touch this
  client-side switch.

  **The switch, pinned.** `S_ADMIN_GM_SKILL` (0x64BE) is `[i32 skill][u8 enabled]`; `skill` is
  the index `C_ADMIN_GM_SKILL.1.def` names (`0 = Invisible, 1 = Invincible, 2 = Hide from
  Mobs`) and `enabled` is that skill's new state. Frame 546 - `09 00 BE 64 00 00 00 00 00` -
  is the real Arbiter answering the tool's 542, and it is exactly what `/@vis` now sends.

  **There is no client-facing vaporize packet.** `SDB_USER_VAPORIZED` (0x282D) is
  World->DbProxy (`i32 userDbId, u8 vaporized`) and never reaches a client; nothing else in
  the 376012 map carries visibility. `S_ADMIN_GM_SKILL` is the whole client-side story, which
  is why `/@vaporize` alone could not work.

  **Wiring.** `vis` / `invis` / `vaporize` / `invisible` join
  `GmCommandHandlers.Implemented`, so `Classify` returns `Local` before the catalogue is
  consulted. `/@vis` sends enabled 0, `/@invis` enabled 1, and `/@vaporize` + `/@invisible`
  FLIP the tracked state - what Alt+A does - and are **also still forwarded to World**
  (`GmCommandHandlers.AlsoForwarded`), because being hidden from other players is World's and
  nothing that worked before should stop. `/@vis` and `/@invis` are ours alone and are not
  forwarded. `Execute` took a `commandType` parameter to do that. **HandlerRegistry is
  unchanged.**

  State lives in `ArbiterClientHandlers.GmInvisible`, keyed by the same `s.PlayerId` the
  push's one-shot uses, and `ResetGmSkillPush` (SDB_USER_ENTERWORLD and leave-world) clears
  it - so **the default spawn stays invisible** and no toggle survives a relog.

  **T89's C_ADMIN_GM_SKILL handler was wrong and is fixed.** It read the trailing i32 as an
  enable flag and replied `skill=0, enabled=(value != 0)`. It is the skill INDEX, and the
  packet is a toggle: frame 546 came out right only because skill 0 and value 0 are both
  zero, while pressing "Hide from Mobs" (1210, `skill=2`) made TeraSharp answer
  `enabled=1` for INVISIBILITY. Now skill 0 toggles the tracked state, answers
  `S_ADMIN_GM_SKILL` plus `S_SYSTEM_MESSAGE @1436` (547 / 2949), skill 2 answers `@1439`
  alone (1211 sends no S_ADMIN_GM_SKILL), and skill 1 - which has no sample - answers
  nothing rather than a message id we would be inventing.

  Tests: `T128_the_visibility_switch_is_frames_99_and_546`,
  `T128_the_toggle_state_is_per_player_and_resets_on_world_entry`,
  `T128_vis_and_invis_are_arbiter_side_commands`.

- T130: **Noctenium `.npcap` converter** (`tools/npcap-to-capture.ps1`, `tools/README.md`).
  `D:\packetlogs\classic_live.npcap` is a client-side capture of the **live Classic+ server** -
  the first working non-GM reference this project has had - and nothing could read it.

  **The container, reversed from the first records and then verified over all 7 391:**

  ```
  file header 16 B:  "NPCP" | u32 version=2 | u64 capture start, UNIX NANOSECONDS
  record      14 B:  u16 type | u64 ns since client start | u32 payloadLen | payload
  ```

  `type` is direction AND view: 0 = S->C wire, 1 = C->S wire, 2 = S->C packet, 3 = C->S
  packet. A **wire** record is one socket read and holds zero or more whole frames back to
  back; a **packet** record is exactly one frame. The chain covers the file to the byte, the
  u64s are monotonic with no exceptions, and the header's `1789912872310233200` ns decodes to
  `2026-09-20 14:01:12 UTC` - the `-100112` in the file's own name at UTC-4, which is what
  pins the field as nanoseconds rather than ticks or FILETIME.

  **The two views are not copies.** Noctenium is a proxy, so a packet a mod rewrites or
  injects is in the packet view and never on the socket. Here they are byte-identical for the
  first 5 433 S->C frames and 27 C->S frames, then diverge: 6 467 packet frames vs 6 441 wire.
  The script walks both whichever you ask for and prints where they part.

  **Ran it.** 7 391 records -> `classic_live.log`, 6 467 frames (6 300 S->C, 167 C->S);
  `reframe-client.ps1` on that: **6 467 packets, no rejects, no renames**, so the live
  Classic+ server speaks the same 376012 map this build does.

  **It confirms T126 and refutes T119.** The live `S_PVE_RANKING_LIST` element reads
  `here=8 next=57 nameRef=39 rookie=0 changedRank=0 rank=1 stageLevel=3 clearTime=173140
  class=9` - rank at +11 and the i64 at +19 being a real clear time (2m53s), exactly the
  layout T126 re-pointed to and not the rank@15 / score@19 T119 shipped. `S_PVP_RANKING_LIST`
  likewise: rank at +11, rating at +15, class at +19, 23-byte element. `S_USER_PVE_RANKING`
  is 25 B and `S_USER_PVP_RANKING` 17 B, the sizes T126 built, and the PvE one is all zeroes
  for an unranked viewer - the "answer with rank 0 rather than skip the frame" T126 chose.

  Two divergences from what TeraSharp sends, both worth a later task: the live season is
  **15**, not 1, and `S_PVE_LEADER_BOARD_INFO` carries **8** dungeon ids where T91's captured
  frame carried 3.

- T133: **the leaderboard, checked against a server that answers** (`status/LEADERBOARD.md`
  section 8). T130's `classic_live` is the first populated leaderboard this project has - the
  real Arbiter on 100.02 never answered `C_REQUEST_P*_RANKING`, so T118-T126 were built from
  the decompile alone. Five frames now live in `data/classic-live/` and the tests round-trip
  them.

  **The two pushes were wrong in three ways.** Season 1 where the live server says **15**;
  three PvP ids where it lists **four** (10, 26, 30, 37); three PvE ids where it lists
  **eight** (9043, 9056, 9068, 9156, 9168, 9507, 9756, 9768); and one shared time window where
  each board has its own, both 28 days. Both matter for more than tidiness: the client echoes
  an id straight back as the request's `id`, so a board we do not list cannot be asked for,
  and we refuse any season but the one we advertise, so advertising 1 was a silently empty
  board. `BuildPve/PvpLeaderBoardInfo` now emit frames 5609 / 5608 byte for byte.
  `RankingBoards.CurrentSeason` is one property behind `TERASHARP_RANKING_SEASON` (default
  15) driving both the pushes and the handler; T91's 2022 frames are still asserted through
  the explicit `BuildLeaderBoardInfo` overload.

  **T126's element layouts are confirmed on 316 live rows**, and T119's refuted. Frames 5809
  (20 rows), 5856 (105, aggregate class 16) and 5818 (100) each decode at T126's offsets and
  re-encode to the same bytes. Row 1 of 5809 is `rank 1, stageLevel 3, clearTime 173140` - a
  real 2m53s clear - where T119 would have read the rank as 3.

  **The PvP `.def` is definitively wrong about its first two scalars.** T126 preferred the
  writer (`u8`@6, `i32`@7) over the def (`i32`@6, `u8`@10) and could not prove it, because we
  send both as 0. The live rows decide: +6 is 0 on 315 of 316 rows and 1 on exactly one, and
  the i32 at +7 runs -17..+12 - a rank delta. The def's reading gives 768, 256, -256.

  **The self-rank frame is conditional, and T126 sent it always.** `SendNowSeasonRank` guards
  it on the requested class matching the user's own class or 0x10 (Arb_part_050.c:9961); all nine live
  requests agree, including frame 5846, which asked for class 0 from a class-9 player and got
  the list alone. `RankingBoards.SendsSelfRank` is the rule.

  **Nothing else to send**: every request is answered by `S_P*_RANKING_LIST` then
  `S_USER_P*_RANKING` and nothing more; the S->C frames around them are ordinary World
  traffic. Registry unchanged.

  **Left open**: the live server sends the whole board in one frame (100 and 105 rows) and
  `PageSize` is 50, with no page field in the request to ask for the rest. Raising it moves
  T119's paging tests, so it is its own task - a test asserts the divergence.

  Tests: `T133_the_leader_board_pushes_match_the_live_frames`,
  `T133_the_season_is_configurable_and_used_on_both_sides`,
  `T133_the_live_pve_ranking_list_round_trips`,
  `T133_the_live_pvp_ranking_list_round_trips`,
  `T133_the_aggregate_class_sixteen_reply_round_trips`,
  `T133_the_self_rank_frame_only_follows_your_own_board`, plus T91's two assertions moved onto
  the explicit overload.

  T133 addendum - `tools\npcap-to-capture.ps1` is **PowerShell 5.1 compatible**. Two things
  in the T130 cut were pwsh-7-only in practice: `Measure-Object -Property { $_.Data.Length }`
  (calculated properties on Measure-Object arrived in PS 6), now a `ForEach-Object`
  projection with an `[int64]` cast so the empty case is 0 rather than `$null`; and
  `$chosen = if (...) { $wire } else { $split }`, where the pipeline unrolls the
  `List[object]` and an EMPTY one lands as `$null`, so `$chosen.Count` threw under
  `Set-StrictMode`. That second one was found by running the tool against a 16-byte
  header-only .npcap and never showed on a real capture. Re-run on `classic_live.npcap`:
  byte-identical to the committed `classic_live.log`, and `reframe-client.ps1` still reports
  6 467 packets with no rejects.

- T133b: **two T133 assertions were guesses; the frames say otherwise.** Both round trips
  still hold - the layouts were never in question, only what I claimed the values were.

  `S_PVP_RANKING_LIST-5818`: I asserted the `rookie` byte at +6 was "0 on every row of this
  frame". It is **1 on exactly one row** - row 97 of 100, rank 96, `BFG`, rating 969, up
  seven places. That is the single set flag in all 316 rows of classic_live, and it is in
  this frame, which is the whole reason the byte can be called a flag. The test now pins
  every row's value, not just the count.

  `S_PVE_RANKING_LIST-5856-class16`: I asserted "N rows share rank 1, one per class". Wrong
  twice over. Ten rows share rank 1, and only **eight distinct classes** among them (8 and 2
  each appear twice) - it is a ten-player raid record, and every member carries the group's
  own 359952 ms and stage 5. And the rank after that group is **11, not 2**: the ladder runs
  1, 11, 21, 31, 41, 51, 61, 69, 78, 87, 96, where the short steps are groups of eight and
  nine.

  **Which means `RankingBoards.Rank` is COMPETITION ranking, not dense** - `rank = i + 1` on
  a change of score, so a tied pair is 1, 1, 3. The behaviour was always right and matches
  the live board; the word "dense" in T119's comments, `RankingRow`'s summary,
  `status/LEADERBOARD.md` 6.2 and the STATUS entry was wrong, and is corrected. The test
  name `T119_ranks_are_dense_stable_and_class_filtered` is left alone so its history stays
  findable; its comments no longer claim the wrong thing.

- T135: **the guild-quest board can be played** - start, finish and cancel
  (`status/GUILD-DESIGN.md`, the T135 section). T98 built the board, the `guild_quests` table
  and `S_GUILD_QUEST_LIST`, and its own DDL comment said start and finish were "not modelled -
  the capture never exercised them". `classic_live2` exercises finish.

  **The brief expected three captured verbs; there is one.** Across `classic_live2` and
  `classic_live3` the only guild-quest packet any client sent is
  `C_REQUEST_FINISH_GUILD_QUEST`, three times. No start, no cancel - they happened outside the
  window, or accept goes through the guild NPC on this build. So finish is byte-for-byte the
  live server's and the other two are their `.def` plus the board refresh, marked as such at
  each handler.

  **Finish, whole:** `12185 -> 12218 / 12220 / 12221 / 12222 / 12223`, again at 13322 and
  64897. All five reply frames match their defs byte for byte. The reward comes out of the
  deltas rather than a guess: guild exp `157620 -> 157640 -> 157660` and funds
  `711 -> 712 -> 713`, so **+20 exp and +1 funds**, while point sat at 14 through all three -
  the point push is a refresh, and `CharacterStore.AddGuildQuestReward` leaves it alone.

  **`S_START_GUILD_QUEST`'s string is the GUILD's name** ("Candlelight" in all three), and the
  frame arrives ~35 frames AFTER each finish carrying the id just finished, with no client
  request before it - the server re-arming the board, not a reply to an accept.

  One quest runs per guild (T98's single status-1 row). Start refuses a second, refuses an id
  not in `GuildQuestCatalogue`, and takes its countdown from the catalogue row's own 43200 s.
  Cancel has **no S_ opcode in the 376012 map at all** - the board refresh is the answer - and
  is restricted to the starter or the chief, which is a choice and is labelled as one.

  **Registry: `HandlerRegistry` unchanged.** Guild registration is the
  `GuildWiring.ClientOpcodes` table it already loops; T135 adds three rows and three
  `GuildPackets.MinClientLength` guards (8 B each - `[u16 len][u16 op][i32 questId]`, which is
  frame 12185's own length).

  Tests: `T135_the_guild_quest_frames_match_classic_live2` (all five replies byte-exact plus
  the request parse), `T135_finishing_a_quest_pays_the_captured_reward`,
  `T135_start_and_cancel_move_the_running_row`.

  **My error, worth recording**: I first reported this task blocked because
  `GuildQuestCatalogue` and friends looked missing. They were not - I grepped an upload of
  `DbProxyStaticData.cs` from an earlier session instead of staging the file this session, and
  the stale copy predated T98. CLAUDE.md already says to read every file in the session it is
  changed; it applies just as much to a file only being READ to decide whether something exists.

- T139: **open-source release prep** - the audit, the scaffolding, and a gate that keeps it
  that way. `bin/` and `obj/` turned out to be gitignored already, so no build output was ever
  tracked; the `.gitignore` that did it was four lines long and is now a full one.

  **Added.** `LICENSE` (MIT, plus a paragraph saying what the licence does not cover: no
  assets, no executables, no datasheets, no captured traffic), root `README.md`,
  `docs/SETUP.md` (clone to a character in the world, and a symptom table), `.env.example`
  (every `TERASHARP_*` variable, placeholders only), `deploy.example.ps1`, `data/README.md`,
  a rewritten `data/classic-live/README.md`, `tools/make-tsis.ps1`, `tools/audit-release.ps1`.

  **Redacted, in place.** `Tests/Program.cs` line 12128 - the one routable IPv4 in a test
  string is now `203.0.113.9` (RFC 5737). `status/CHAT-HANDOFF.md` line 21 - the build host's
  address is now "the build host". Both were the operator's own infrastructure.

  **`tools/audit-release.ps1`** keys on the SHA-256 of all 19 capture-derived blobs, so
  renaming `starter_blob.bin` does not get it past the gate; it also refuses datasheets,
  `.def`, `.npcap`, credentials assigned to literals, private keys, `.env`, `deploy.ps1`, any
  routable public IPv4 (loopback, RFC1918, CGNAT and the RFC 5737 ranges pass), and build
  junk. Verified both ways: 245 findings on the current worktree - 237 RETAIL, 3 PII,
  4 ADDRESS, 1 JUNK - and 0 blocking on a simulated clean tree.

  **`tools/make-tsis.ps1`** is what makes "regenerate the fixtures yourself" true rather than
  a slogan: reframer output in, a TSIS container out. Verified by unpacking `cap_t26.bin`,
  re-adding the 6-byte header to each of its 11 payloads, writing a synthetic `_frames.txt`
  and rebuilding - byte-identical to the original.

  **Cowork cannot delete files, so these are left for the human**: `data/*.bin` (14),
  `data/classic-live/*.hex` (5), `data/*.md` (11 fixture notes), `data/dbproxy_opcodes.txt`,
  `data/proxy-defs/`, `ship.ps1` (hardcodes `D:\v100\...` and a 7-Zip path;
  `deploy.example.ps1` replaces it), `src/TeraSharp.Arbiter.Tests/Program.cs.bak`,
  `status/T105-fail.txt`.

  **The one item with a reason beyond copyright**: `data/classic-live/*.hex` carries roughly
  114 real character names and guild names captured from a live third-party server. It is the
  clearest must-not-ship thing in the tree and `audit-release.ps1` files it as `PII`, not
  `RETAIL`.

  **Open, deliberately.** 198 `REVIEW` findings remain - lines of verbatim decompiler output
  pasted into comments. Mostly `status/` (MULTIPLAYER-DESIGN 36, GUILD-DESIGN 23, PARTY-DESIGN
  22, ENTER-WORLD-FALLBACK 16, HANDSHAKE-DATA 14, WORLD-PARTIAL-LOAD 13), and a few source
  comments (PartyManager 6, ParcelDbHandlers 5, RankingBoards 4, ArbiterClientHandlers 4).
  Offsets, opcode numbers and `FUN_` addresses cited in prose are facts and stay; pasted
  Ghidra is a separate pass over 32 files. The gate reports them and does not fail on them
  until someone passes `-Strict`.

  Two correctable claims I made before building the tool: I first read the tree as having
  committed `bin/`/`obj/` (it does not - they are ignored), and I first concluded there was no
  verbatim decompiled C anywhere. A 4-consecutive-lines heuristic said so; a
  two-identifiers-on-one-line rule found 198. The tool is right and the eyeball was not.
