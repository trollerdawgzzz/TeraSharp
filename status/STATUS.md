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
