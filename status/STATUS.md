# TeraSharp — Status

Mirrors `CLAUDE.md` section 0. When the two disagree, **CLAUDE.md wins** and this file is stale.
Day-by-day state, live-debug recipes and the deploy loop are in `status/CHAT-HANDOFF.md`.

Read order for anyone new: `CLAUDE.md` (workspace rules at the top) -> `status/HANDOFF.md` §1
(DLMItems — the single failure mode behind every relog hang) -> `status/PERSISTENCE-MAP.md`
(every per-user W->A opcode and how it is answered) -> this file.

Build: `dotnet build TeraSharp.sln` — 0 warnings, 0 errors.
Tests: `dotnet run --project src\TeraSharp.Arbiter.Tests` — **~975** `[Test]` methods. (967 passing at T170; T172 added 8.) `TERASHARP_TEST_FILTER=T170_` runs a subset.
Deploy check: `TeraSharp.Arbiter.exe --selftest` — one PASS/FAIL line per data dependency (T37).
Config check: `TeraSharp.Arbiter.exe --check-config` — every `TERASHARP_*`, the resolved paths
and ports, and the four settings that are legal, silent and wrong (T113).
One page on processes, ports, the file map and the env: `docs/ARCHITECTURE.md` (T114).

**Where the project is (2026-09-22, master @ T172).** Master is at T172 (T146 paused). The
single-player loop, two players in one world, parties, trade, the broker's buy/collect, GM `/@`
and the Alt+A panel (T144b: the gate is `S_SELECT_USER`'s adminLevel) are live-verified. Since T141
the work has been **proof**: real-Arbiter taps (cap_final, cap_final2a/2b, cap_clearallskill) and
TeraSharp taps (cap_bag, cap_play1, cap_scroll, cap_queue1/2) turned into byte-exact pins, every
request World can leave waiting sorted into T165's groups (only 0x275C left, dead on both sides),
and the values TeraSharp had copied from datasheets read from the sheets instead (T159-T172).

What is pinned but not yet live: guild war declare-back and surrender, guild quest start/cancel
(T170), the matchmaker fixes (T161/T163), the multi-World hand-off (T138b), the tooltip compare
(T169). `status/CHAT-HANDOFF.md` has the verified/pinned/decompile-only split and the open items;
`docs/GO-LIVE.md` is the ordered checklist and the maintenance loop.

---

## Status by area

The distinction is the point of this table: **live** = seen working against a real client;
**pinned** = byte-exact against a real capture, in a test, but not run live; **wired** = registered
and reachable, layout from the decompile; **designed** = code and tests exist, nothing routes to it.

| Area | State |
|---|---|
| Client crypto / codec / login / char list / select / create / delete | **live** |
| World handshake, promotion records (live timestamps), 0x1581 burst, config burst | **live** |
| Enter-world, blob save/load, restriction, gameId per login, relog into a dead instance | **live** (T21) |
| Chat, client settings, keybinds, visited sections / first-visit cinematics | **live**; the 16-byte 0x1439 entry and the id-reuse purge are **pinned** (T172) |
| Per-user DB writes, quests incl. the login batch 0x2732, skills in the blob | **live** (T15/T17/T18, T145) |
| Inventory rows, money, loot, starter kit ids | **live** (T44/T59/T105) |
| Atom ops beyond move/amount - 8 makeitem, 51/63 bind, 37 parcel marker, enchant record edits | **pinned** (T153, T151, T170); enchant ops **decompile-only** (T166) |
| Achievements, tips, seren guide, reputation, fatigability, dungeon cool times | **live** (T22/T25/T26) |
| Multiple players - ticket routing, N-recipient bypass, departed tickets never borrowed | **live** (T38); T161's rule **wired** |
| Friends, groups, memos, blocks, whisper | **live** (T30/T47) |
| Parties, the contract handshake, trade | **live** (T49/T60/T64) |
| Mail, warehouse, trade broker | **live** for the 09-16 paths; the T74/T141 fixes (tabs, attachments, withdraw, TotalPaid) not re-verified live |
| Guilds - 17 `C_`, rows, boot load, perks, board, search, flag | **wired**, pinned to cap_social2-4 (T39-T57, T83/T95) |
| Guild war - declare, withdraw (T80); declare back, one-side withdraw, penalty, surrender (T170) | **pinned** (cap_social4, cap_final2a/2b); not live |
| Guild quests - board, start (several at once), cancel, finish, 0x1453 points | **pinned** (T135, T170, T172) |
| GM - `/@` forward, AdminLevel in AS_ENTER_WORLD, Alt+A panel, vaporize/invisible, tool buttons | **live** (T46/T89/T144b/T148/T152/T155) |
| Admin web - pages, search, grants, bans, kick, announces, status tab, game log | **wired**, loopback behind `TERASHARP_ADMIN_TOKEN` (T101-T116) |
| Leaderboards, dungeon windows, matching window | **live** on the Classic+ client (T118-T136) |
| Dungeon / BG matchmaker, system parties, dropout debuff | **tested live twice** (cap_queue1/2); T161/T163 fixes not re-verified |
| Matched-party relog restoration (T190b) | **pinned** to cap_2man_b: AS_ENTER_WORLD party fields + post-entry13AD; apply `status/T190b-PATCH.diff`; client popup behavior still needs live verification |
| Vanguard Initiative (event matching) | **wired**, the lists pinned (T156, T161b) |
| Multi-World hand-off | **wired** (T138b), pinned to cap_multiworld3; live test pending |
| Crafting / gathering | **pinned** client-side (T147b); A<->W writes decompile-only (T147) |
| Cards, crests, EP pages, skill polishing, dungeon rank | loads, EP writes, polishing **pinned** (T169/T170); captured card pairs **pinned** (T190); CREATE_CARD_INFO and ranked-result emission remain unpinned |
| DB proxy completeness | every twin in a T165 group; pending group C = 0x275C only (T166-T170) |
| Item tooltips and the compare ask | **pinned** (T169/T170); equipment item-level floats not reproduced |
| Datasheets | 14 values read from World's sheets, built-ins as fallback (T159-T172, `status/DATASHEETS.md`) |
| Purchase-limit reset, whole-world broadcasts, city war state, crest-use list | **pinned** (T172) |
| Lord / election / Civil Unrest, petitions, TBA battlepass, appearance change | **not started** (`status/MISSING-HANDLERS.txt`: 58 unregistered, regenerated T174b) |
| Packet-handling security, fuzz suite | **wired** (T48/T50/T168b) |
| Play time, logging, item names in the admin web | **wired** (T106/T113) |
| Go-live kit - firewall, backup, checklist | **wired** (`tools/harden-netcup.ps1`, `tools/backup-db.ps1`, `docs/GO-LIVE.md`) |
| Open-source release | audit gate (T139); the verbatim-quote pass (T140) in progress |

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
  the past-season path does `classTable + ((int64)class + 1) * 3` (Arb_part_050.c:10630) - a
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
  it with `requestedClass == user.class || requestedClass == 0x10` (Arb_part_050.c:9961); all nine live
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

- T141: **the economy, checked against a server that answers** (`status/BROKER-DESIGN.md`, the
  T141 section). `classic_live4.log` is a client capture of a live Classic+ 100.02 server
  driven through a whole broker episode, a mail episode, warehouse moves and two player
  trades. Ten of the S_ packets in it are ours; every one was rebuilt from the frame's own
  decoded values and diffed byte-for-byte.

  **Eight came back byte-exact first time**: `S_TRADE_BROKER_HIGHEST_ITEM_LEVEL`,
  `CALC_NOTIFY`, `INPUT_PRICE`, `REGISTERED_ITEM_LIST` (empty and one row), `BUY_IT_NOW`,
  `SOLD_ITEM_LIST` (empty), `S_SHOW_PARCEL_MESSAGE` (all five) and
  `S_PARCEL_READ_RECV_STATUS` (all seven). Two of those answered a question the old captures
  could not: CALC_NOTIFY's pair is SoldCount then BoughtCount (seq 6019 reads (0,1) with one
  purchase uncollected and 6071 reads (0,0) after it), and READ_RECV_STATUS seq 7164 is the
  first frame anywhere with the second u32 set, so it is a field and not padding.

  **One real bug.** `BlTotalPaid` (+68) was the listing price; it is the price **with the
  broker's cut on it**. The live purchase proves it inside one frame set - the bought row at
  seq 6056 reads Price 6562 and TotalPaid 7546, and the same trade's search row at seq 5938
  reads TotalPriceWithTax 7546. T74 read it as the price because its only row was priced at 1,
  where the two are the same number. Fixed in both the bought and the sold builder;
  `SlSellerProceeds` (+76) is deliberately left as the price.

  **The fee rate is server configuration.** All 80 live search rows satisfy
  `price + price * 15 / 100` exactly and none satisfy the hard-coded tenth. Our own 100.02
  charges a tenth (10001 lists at 11001), so 10 stays the default and
  `TERASHARP_BROKER_FEE_PERCENT` overrides it.

  **Not ours, so not diffed**: `S_LIST_PARCEL_EX`, `S_SEND_PARCEL_TAX`, `S_VIEW_WARE_EX`,
  `S_ADD_TRADE_BAG`, `S_TRADE_BOX`, `S_TRADE_ACCEPT`, `S_TRADE_BAG_DONE`. TeraSharp has no
  builder for any of them - World writes them and we only feed the `DBS_` rows behind them.
  Checking the Arbiter-fed fields needs an A-W tap, and this capture is a client-side one of
  someone else's server, so that is left open.

  **Left open**: two u8 flags in the waiting element, `+51` (1 on 40 of 80 live rows, 0 on the
  other 40, correlating with nothing we can see) and `+83` (1 on all 80). Both are 0 on all
  five of our own rows, which is what we send. Named `WlUnknownFlagA` / `WlUnknownFlagB` so
  the element tail is not mistaken for padding.

  A correction worth recording: my first pass reported both flags as "1 on every live row" -
  it came from collecting the set of values seen at each offset instead of the value per row,
  and the rebuild-and-diff caught it. Aggregate over rows is not the same as the row.

- T147: **crafting and gathering** (`status/CRAFTING.md`). The crafting window's two packets,
  `S_ARTISAN_SKILL_LIST` and `S_ARTISAN_RECIPE_LIST`, are World-built and tunnelled through
  `SA_BYPASS_TO_CLIENT`; the Arbiter only owns the rows behind them. It answered the two loads
  (empty, replay-served) and none of the ten writes: `SDB_LEARN_ITEM_RECIPE`,
  `SDB_DELETE_ITEM_RECIPE_LIST`, `SDB_SET_RECIPE_BOOKMARK`, `SDB_ITEM_PRODUCE_STEP1/2`,
  `SDB_UPDATE_SKILL_PROF` and the four gathering `S_UPDATE_PROF_*` (0x273D-0x2740). Each
  carries a DlmId, so the first recipe, craft or gathered node head-blocked the character.

  All twelve are handled now. Recipes, skill proficiencies and gathering levels persist per
  character (`item_recipes`, `skill_profs`, `gathering_profs`). LEARN and STEP2 apply and echo
  their item atoms, STEP1 charges the account's fatigability as the T26 handler does, and the
  gathering levels are stamped into the enter-world blob at +0x1C8..+0x1D4, where the real
  Arbiter binds `profMineral..profHerb`. Empty loads stay byte-exact against cap_social4.
  `C_START_PRODUCE` itself is World-side; no capture has a craft or a gather, so those layouts
  are from the dumpers and handlers, not the wire. Needs `dotnet build` and the test run.

- T147b: **crafting and gathering checked against live Classic+** (`status/CRAFTING.md`, the
  T147b section; `classic_craft.log`: four crafts, three gathers). Both artisan lists decode
  and re-encode byte-exact (36 recipes, five skills), the recipe ids C_START_PRODUCE asks for
  are the list's +8, the skill list's id/value pairs are our SkillProf records, and all four
  STEP1 fatigue totals (1340 -> 1335 -> 1330, 1230 -> 1210, 1150 -> 1130) come out of our
  handler. Gathering: ProficiencyType 1 = herb, 2 = mineral, confirmed client-side, and the
  three 350 writes land at the right blob offsets. No code mismatch; one note corrected (the
  recipe's u64 at +37 is the enter-world time, not the learn time). Every A<->W write is still
  capture-less - a client capture cannot show them.

- T149: **capture tuning for guild war, guild quests and Civil Unrest** (`tools/capture-tune.ps1`,
  `status/CAPTURE-T149-GUILD.md`). Temporary edits to the real servers' datasheets so two or three GM
  characters can reach every flow in one session: 21 guild-war values (`GuildConfig.xml`
  `GuildWarTime` and `GuildSizeTable`, `GuildWar.xml`, `BattleChipData.xml`), 7 guild-quest values
  (`Datasheet\GuildQuest\`), 6 Civil Unrest values (`CityWar.xml`: today's battle at the next full
  hour, 30 minutes, no tower-destroy rule, entry gate 10). The script backs up, hashes and changes
  only attribute values, keeping the BOM; `-Revert` restores byte for byte (tested on copies of the
  real sheets). Two rules are not in any sheet: a member's 7-day guild-quest wait
  (`/@guild_quest_usable on`, per account) and guild points / level (`/@add_guild_point`,
  `/@guild_level`, ...). Side finding for T80: the war's 1500 cost, limit 10 and the unexplained
  250 are `GuildConfig.xml` `GuildSize[rank=0]` `declareCost` / `declareLimitCount` / `maintainCost`.

- T150b: **0x283D re-landed alone, plus four proven generic acks** (T150 was reverted after a
  live regression: level-70 character at level 1, frozen, Invisible OFF not reloading).
  Bag offsets proven by the real Arbiter's own effect: cap_social4.log character 1 enters with 40
  at blob +0x3AF0 (seq 250), asks 48 (3039 -> 3040), enters with 48 (5715; cap_final 411+). Against
  the wedged TeraSharp tap (cap_invensize.log 103 / 683) the next enter-world blob differs from
  World's in exactly one byte (+0x3AF0, 40 -> 48); level, money and length untouched. DbAckTable is
  now opt-in per opcode: only GUILD_LEARN_PERK, CONDITIONAL_TELEPORT, ITEM_SIMPLE_ATOM and
  MAIN_MENU_COMMAND, each byte-exact against its live pair and matched to what World's handler
  reads. The other 177 stay unanswered (candidates listed in PERSISTENCE-MAP.md T150b). Caution:
  answering 0x283D releases whatever World queued behind it (skill learns, equips via T145's
  0x2813, mail) - none of that is new code here, but it was hidden by the wedge. Built and run
  in a Linux sandbox (890 passed); needs `dotnet build` and a tapped live test.

- T153: **atom op 8 (non-stackable insert, GM makeitem) lands** (`World/ItemCreate.cs`,
  status/INVENTORY-DESIGN.md "Op 8"). It is allocated an id like op 7 (ITEM_SINGLE's CloneAtomList
  and WarehouseHandlers.CloneAtomsWithIds), inserted by Apply, and its row gets the 536-byte record
  the Arbiter's DO_TS_INSERT_NONSTACKABLE_ITEM builds from the atom - so enchant and the rest survive
  relog. Proven: cap_final 5914 -> 5915 / 5974 -> 5975 byte-exact; the record matches the real
  Arbiter's stored item for 10018, 10030, 10052, 10053; the live makeitem (cap_makeitem 15152) now
  gets an id, a row at 0:1 and a record. 0x27A4 now patches id/amount/pocket/slot from the row onto
  a stored record (a move only updates the row). Not reproduced: the template-derived +0x104 byte
  and period expiry (need item template data). Ops seen but still unmodelled: 37, 40, 92 (real
  captures only). Built and run in a Linux sandbox (893 passed); this branch lacks master's T151.

- T154: **SDB_LOAD_CITY_GUILD_INFO (0x2954) answered** from a new `city_guild` table (league,
  season, guild, tower build/destroy time, kill/death/destroy, maintain bonus). Reply decoded from
  the Arbiter's writer (Arb_part_054.c:13406) and World's reader (WorldServer.exe.c:3016303): a
  count, the first element's frame offset, LeagueId, SeasonId, then 44-byte self/next-linked
  elements. Empty table = "no owning guild", byte-exact against every live pair (league 1, season
  1 and 2). One explicit allow-list case, like the T150b opt-ins. 0x28B8 SDB_PUBLISH_INVITE_CODE
  confirmed one-way and left sealed. Built and run in a Linux sandbox (899 passed).

- T156: **Vanguard Initiative window - the Arbiter's half** (PERSISTENCE-MAP.md T156). Every
  client packet (S_AVAILABLE_EVENT_MATCHING_LIST, S_ADD_NEW / S_REMOVE_EVENT_MATCHING_QUEST,
  S_UPDATE_EVENT_MATCHING_BONUS_INFO) is World-built from World's own EventMatching datasheet and
  reaches the client through 0x13F7; C_AVAILABLE_EVENT_MATCHING_LIST already reaches World
  (HandlerRegistry RegNoop forwards in-world). The empty window: World asks 0x1507 and the replay
  table answered 0x1591 naming captured user 1, so World looked up the wrong character. Now
  answered for the live user; 0x293C stores the daily counts and pushes 0x1591 before its ack (the
  real push on quest progress); 0x293A/0x293E/0x2965/0x2967 are per-character rows; 0x1592 is
  served from stored stamps and 0x1598 / 0x159A (re-sent 80+ times a session) are answered and
  stored - 0x159A is no longer sealed one-way. The live 2421-byte list is decoded and tiled byte
  for byte. Not done: the match-pool id lists in 0x1591 (always empty in captures), the 0x1599
  broadcast to other Worlds, the 0x293E trailing flag. Built and run in a Linux sandbox (907 passed).

- T157: **Instance Matching lists come from World** (ArbiterClientHandlers T157 block). The real
  Arbiter never writes S_VIEW_INTER_PARTY_MATCH_DUNGEON_LIST / _BATTLEFIELD_LIST: it sends World
  0x1644 / 0x1645 [empty pool list][UserDbId], and World builds the tab from its own
  DungeonMatching.xml / BattleFieldData.xml, the character's level and item level, and tunnels it
  (cap_social4: 25 dungeons, 17 battlegrounds for a level 70). We answered the client ourselves
  with a 3-byte body - "No available dungeons". Now the request goes to World; with no World linked
  the client gets World's empty shape. Live tabs (classic_live3 7964, classic_live2 9364) and this
  server's are decoded byte for byte in the tests. T138c's battleground table is gone: ids and team
  sizes are read from BattleFieldData.xml (BattleFieldSheet, TERASHARP_DATASHEET or
  TERASHARP_DATA\Executable\Datasheet); only the stated healer/lancer rules stay, per type. A
  battleground missing from the sheet is neither listed by World nor queued/matched by us; no sheet
  = no battlegrounds. Not modelled: the pool list (role queue indicators). Built and run in a Linux
  sandbox (915 passed).
- T159: **datasheets are the source of truth** (`World/DatasheetLoader.cs`, audit in
  status/DATASHEETS.md). Seven values TeraSharp had copied from Executable\Datasheet are read from
  the sheet now, the copy kept only as the built-in: GuildConfig `<GuildSize>` (war declare cost /
  limit / maintain cost), DungeonMatching `<ClassPosition>`, DefaultSkillSet, CreateCharData,
  BattleFieldData `<RankingCompetition>` (PvP board ids), DungeonRankRecorder_*.xml (PvE board ids)
  and the 98 dungeon-timeline ids. Each reproduces its old table from the real sheet, except the
  two board sets, which were a live server's and now follow ours. `--check-config` lists every
  sheet; `DatasheetLoader.LoadAll` logs one line each - the call belongs in Program.cs (human-owned,
  one line, see DATASHEETS.md); without it each sheet still loads on first use. The battleground
  table is T157's (cowork/T8). CLAUDE.md: no new sheet constant without a loader. Built and run in a
  Linux sandbox (911 passed).
- T161: **first live queue** (cap_queue1 + cap_queue1_client, two level-70s on 3036). (1) BUG: FIN
  went out before the party existed and with no state pair; neither client opened the enter
  window. Now each member gets classic_live3's order - party member list, S_CHANGE_EVENT_MATCHING_
  STATE (queued 0, flag 1) then (0, 0), FIN, S_SYS_PARTY_INFO (10568-10587; FUN_14090df50's per-
  member order). Our pair is the empty form: the live one carries 90/160 event ids from a per-user
  roster we do not keep. World 13 being down does not affect the window. (2) NOT an Arbiter bug:
  every frame World builds Vanguard from is byte-identical to the real Arbiter's (0x1595, 0x1582 x2,
  0x157E, 0x157F, empty 0x1591 - arb_world.log vs cap_queue1). The list is the character's daily
  event quests; World's login reset added 9 (client frame 129) - only events EventMatchingManager
  has started, which needs the quest in World's QuestDataSheet plus a timeline. No dungeon event is
  started, so the cause is World data; classic_live3 is another data version (5 of its ids are not
  in our EventMatching.xml). (3) BUG, human-owned: after a leave the single-session fallback put the
  leaver's frames in the survivor's reorder buffer (seq 2730 vs 1362 - the stall), then delivered
  them. Rule in World/TunnelRouting.cs; WorldBridge change in status/T161-PATCH.diff (test PENDING
  until applied). "SA_LEAVE_WORLD: no session" is the client closing first - harmless. (4) A formed
  match is state (MatchWiring.PendingMatch): re-offered once per world entry (FIN + party info,
  riding SocialHandlers.SendBlockList), C_MATCH_ADD refused, claimed on SA_RESPONSE_ENTER_DUNGEON,
  voided with the 53967 cancel pair on leaving the party / C_MATCH_DEL / TERASHARP_MATCH_ENTRY_
  SECONDS (default 300). classic_live3's match was entered at once: no re-offer or expiry frame
  exists, so (4) is decompile-guided and ours. Built and run in a Linux sandbox (927 passed).
- T163: **matched parties are system parties** (decompile: PartyManager keeps a normal and a system
  map; New_CreateParty, KickPartyMember, Party::OnDungeonClear, the leave job). (1) BattleFieldSheet
  is in DatasheetLoader.All (--check-config lists it; missing sheet = no battleground). (2) A
  match now always gets a NEW party - SysPartyInfoType 0 + dungeon id in AS_DO_CREATE_PARTY - so
  C_ENTER_DUNGEON works for a queued party too; the queued party is suspended, not extended: its
  clients get S_LEAVE_PARTY (classic_live2 19463), World is told nothing (it suspends its own
  copy). S_PARTY_MEMBER_LIST ims = 1 for it (classic_live3 10568). (3) Leaving the matched party
  restores the pre-queue party (S_PARTY_MEMBER_LIST to the returner); a dissolve restores
  everyone; each leave carries S_CANCEL_PARTY_MATCH_POOL(-9999, 2) (classic_live3 53966/53967,
  53993/53994). The teleport out is World's. (4) The dropout debuff (999994, 180 s,
  DungeonMatching.xml) is World's, applied on our AS_NOTIFY_ABOUT_SYS_PARTY_WITHDRAWAL 0x13F5
  [planet][dbId] - sent on SA_LEAVE_PARTY from a dungeon match until World's
  DSA_NOTIFY_ABOUT_DUNGEON_CLEAR 0x13F0 [partyId][dungeonId] disarms it (now handled). No A<->W
  capture has either frame: layouts from the writers/handlers; classic_live3 pins the no-debuff
  after clear. Kick: restore only, no penalty (the decompile's second job is unclear). Replaced
  T138d's two extend/merge tests. Sandbox: 929 passed, 1 failed - T162_level_jump_scroll, which
  fails the same way on the untouched rebased tree.

- T165: **no twin left unclassified** (World/DbAckGroups.cs, PERSISTENCE-MAP.md T165). The 189
  names, each checked against World's Handler_DBS_* (guard + every offset read) and the Arbiter's
  dumpers and Handler_SDB_*: 5 were already answered under other names; A 51 answered now as
  unpinned DbAckTable rows (26 bare, 7 apply request atoms, 18 echo them - the real writer gets
  the request's own vector, ITEM_SIMPLE_ATOM's shape), each logging "generic ack 0x.... NAME -
  capture a real pair to pin" once; B 62 denied; C 71 + 4 the walk found = the T166 spec with
  reply layouts. Not A despite the brief: VIP exp, attendance/playtime checks, hide-passive learn,
  MARK_AS_QUEST_COMPLETED (their readers take data). DbAckTable refuses to load a B/C row and
  OnGenericAck refuses them again, so T150's reload cannot come back. Tests walk all 300 twins
  (one group each, including 16 answered or sealed elsewhere). Sandbox: 933 passed, 1 failed -
  T162_level_jump_scroll, the same pre-existing failure as T163.

- T165b: **DbAckGroups follows the allow-list.** B, C and Elsewhere stay T165's tables
  (DenySpec / RealHandlerSpec / ElsewhereSpec); what is pending is those minus every opcode
  IsHandledRequest answers with its own case, read at call time. DbAckGroups.Landed is the "real
  handler on master" group: T164's 0x2790 and 0x28AE today, T166's enchanting ops with no table
  edit (checked by registering two C ops in a scratch copy: the walk passes and lists them).
  DbAckTable still refuses every B/C row, landed or not. Sandbox, on the rebased tree: 936
  passed, 0 failed.

- T167: **cards, EP pages, skill polishing, dungeon rank - real handlers** (World/DbProxyT167.cs,
  PERSISTENCE-MAP.md T167). 15 group-C names plus the three loads that feed them back: 0x2986 card
  data, 0x27B9 EP perks, 0x2975 polishing - byte-exact against cap_social4 (data/cap_t167.bin). The
  brief's other pins were not SDB frames (the 45 are S_CHANGE_CARD_PRESET); the rest is
  decompile-derived. New tables card_info, card_combines, ep_perks, skill_polishing(+_options,
  _levels), dungeon_rank_records; characters gets card_preset_index and five ep_* columns. T77's
  learn / reset / pre-EP now write the pages. Dungeon rank is not answered (the real Arbiter sends
  World nothing) and ranks the PvE board. All 15 show as landed. Sandbox: 942 passed, 0 failed.

- T168: **group C slice 3 - the rest of PERSISTENCE-MAP's C table** (World/DbProxyT168.cs,
  PERSISTENCE-MAP.md T168). 22 echo rows on the T166 path, store-backed money / gold / VIP /
  attendance / hidden passives / servants / book rewards / guild member + name, refusals (ok 0)
  where nothing is kept, broker deals taken silently. New tables vip_info, hidden_passives,
  servants, card_book_rewards; characters gets gold_consumption, attend_bitmap, attend_set. The
  walk's pending C is the four left on purpose (275C, 27E6, 282F, 2969). Sandbox: 954 passed, 0 failed.

- T168b: **ADD_GUILDMEMBER2 no longer throws on an unknown guild** (fuzz: 0x27DB FOREIGN KEY
  constraint failed, which closed the World link). The handler refuses a short payload or a missing
  guild with ok 0 and a Warning; CharacterStore gets NoSuchGuild / NoSuchAccount next to NoSuchOwner,
  and every T168 INSERT is guarded (AddGuildMember, AddServant, LearnHiddenPassives, AddVipGameExp,
  AddCardBookReward; servant / passive / VIP now answer ok 0 when dropped). Only guild_members has
  FKs among them.
  Sandbox, on the rebased tree: 958 passed, 0 failed.

- T170: **real-Arbiter pins + the last two C ops** (World/DbProxyT170.cs, PERSISTENCE-MAP.md T170,
  GUILD-WAR.md section 8). 0x27E6 clear-all-skill byte-exact (cap_clearallskill), 0x2969 event
  progress stored (decompile-only), T169's tooltip patch applied (0x282F). EP writes and the
  loads after relog replay byte-exact from cap_final2b; card loads pinned with the card seeded
  (no card write exists in either capture). Mail op 37 is DO_TS_RECV_PARCEL, now a known marker.
  T170-guildwar: declare back, one-side withdraw, penalty info and surrender, pinned to
  cap_final2a/2b; T80's declare now pays and notifies both guilds; guild quests may run side by
  side and cancel answers S_FAIL_GUILD_QUEST. Group C: 0x275C only. Sandbox: 967 passed, 0 failed.

- T174: **docs to current state** (docs only). `status/CHAT-HANDOFF.md` rewritten from its
  2026-09-21 checkpoint log into ten sections: roles, architecture, verified / pinned /
  decompile-only by system, the wedge story and DbAckGroups, datasheets, the GM / Alt+A findings,
  every capture file and its consumer, how to get facts, rules, open items. This file's header and
  area table refreshed; `docs/ARCHITECTURE.md` (file map, env table) and `docs/GO-LIVE.md`
  (maintenance loop, production switches, ports, backups, datasheet edits) brought to T172.

- T174b: **doc fixes** (docs only). T171 = T170 parts 5-6 (0x27E6 clear-all-skill, 0x2969 event
  progress), cited so in `status/CHAT-HANDOFF.md`. `status/MISSING-HANDLERS.txt` regenerated with
  `tools/count-handlers.ps1`: 273 / 58 unregistered (was 62; T170's four guild war packets left the
  list). "Menu pop" defined in the handoff's open items: a two-account Instance Matching queue
  never formed on the real Arbiter (MatchingRoleTemplate edits didn't take), so the pop sequence for
  both members is unpinned; classic_live3 (leader only) is the reference.

- T174c: **STATUS.md encoding** (docs only). 57 mis-decoded sequences restored (35 em dashes, 21 section
  signs, 1 ellipsis; UTF-8 read as cp1252 since ~T161); BOM dropped, CRLF kept. T174b's STATUS.md and
  CHAT-HANDOFF.md edits had not reached the worktree (the commit sent the T174 copies); re-committed here.

- T177: **duplicate BG / dungeon research + tools** (analysis and tooling only). `status/DUPLICATE-CONTENT.md`:
  every sheet that names BG 37 / continent 115 and dungeon 9781 / zone 781, required vs optional, with World,
  Arbiter and MatchServer (strings only) validations. A BG copy shares its continent (37-40 share 115) and needs
  a MatchingRoleTemplate role whose totalUser is the team size. A dungeon copy needs a new continent id AND a
  new hunting zone with copied per-zone NPC files (as 9981/981), reuses the map (tiles by zone, pathdata by
  area), and World refuses to boot unless role totalUser = maxMemberCount. The client DC needs rows either way.
  New `tools/dupe-battlefield.ps1`, `tools/dupe-dungeon.ps1` (+ `dupe-common.ps1`): -DryRun, -Revert, fenced
  rows. Tested with pwsh 7.4 on copies of the real sheets: checks pass, revert byte-identical.

- T187: **an equip now persists** (World/WarehouseHandlers.cs, tests T187.cs, data/cap_t187.bin).
  SDB_EQUIP_ITEM carries a PAIR of op-36 atoms in list B, one per direction (worn 14:slot <-> bag slot);
  Apply swapped by POSITION, so the second atom undid the first and the worn set reverted to the starter
  kit on the next load. Op 36 is now anchored on the item the atom names. Pinned byte-exact to cap_final2b
  4162/4163 and 53820/53821 (real Arbiter) and applied against cap_queue4 5034 (ours); 5 tests incl. a
  relog round trip. status/INVENTORY-DESIGN.md has the atom table.

- T188: **first-login wedge - diagnosis + repair** (status/T188-FIRST-LOGIN.md, World/CharacterTransientState.cs,
  Web/AdminApi.cs, tests T188.cs). The three logins of `test` in cap_queue4 have IDENTICAL enter-world
  bursts and no unanswered SDB_* anywhere in the capture, so the wedge is not a T145-style DLM wedge and
  is not visible on the wire; the blob region that differs is play state a clean character lacks too.
  Cause still open - the doc says what the next capture must hold. New POST /api/reset-character drops the
  per-character state that outlives a World session (enter stamp, game id, Alt+A one-shot, GM invisibility,
  queued match, party listing, T180 hold) and nothing durable; 3 tests.

- T191b: **the account settings blob is served empty at the lobby** (tests T191b.cs, data/cap_t191b.bin,
  CLIENT-SETTINGS.md section 9). `LoginHandlers.OnGetUserList` sends `S_LOAD_CLIENT_ACCOUNT_SETTING` as a
  hardcoded empty body; the client reads that as "nothing stored", initialises its own defaults and pushes
  them back, overwriting the player's options (cap_queue1_client 12 -> 31 -> 322 -> 1329). The real Arbiter
  serves the same blob at all three send points of every login (cap_final2b_client2 12 / 286 / 846 / 1119,
  659 B each) and sends the USER blob only in the post-spawn pair - we send it a fourth time before
  `S_LOGIN`. Two human-owned lines in `LoginHandlers.cs` (section 9); 3 tests pin the real lobby packet,
  the recorded bug and the save -> relog round trip. NOT the tutorial tips: `User::CheckTutorialSimpleTip`
  (`WorldServer.exe.c:2685492`) answers `S_SIMPLE_TIP_REPEAT_CHECK [tipId][!show]`, and we already answer
  1 ("do not show") for tips 1/2/35/39/41 exactly like the real Arbiter (cap_final2a_client1 443-488,
  cap_social_client 600/641 pins the polarity).

- T202: **mail collect - Receive all now delivers** (World/DbProxyHandlers.cs, World/ParcelDbHandlers.cs,
  Persistence/CharacterStore.cs, tests T202.cs, data/cap_t202.bin, MAIL-WAREHOUSE.md section 14). The
  button is `SDB_RECV_PARCEL_EX` (0x277D), not `SDB_RECV_PARCEL`, and it is two-step: we ignored `Step`,
  answered step 1 with `ParcelCount 0` and an empty list - so World decided there was nothing to collect
  and never sent the step that carries the atoms - while the same pass marked all 7 parcels collected and
  paid the gold (cap_mail1 21483/21484). Step 1 now enumerates only, as a chain of
  `[here][next][dataOffset][0xdd8]` nodes each followed by the full record; step 2 allocates ids, applies
  the atoms, marks the named parcels collected and credits `row.Money` when no op-9 atom did. The list-A
  field pair is a chain, not a binary ref, so an empty list writes 0 there, not the frame length.
  `SDB_LIST_PARCEL` now honours `ViewType` (0 inbox, anything else Sent, via new `GetParcelsSentBy`),
  which is why every reward mail also showed up in Sent. 4 tests; the id rule is byte-exact against
  cap_final2b 10032->10033 and cap_social2 2508->2509. Two deviations, both documented: step 1 serves one
  page of ten, and step 2 marks rather than deletes (T196 pinned status 2 surviving the claim).

- T203: **server-first achievements are gated** (World/DbProxyHandlers.cs, Persistence/CharacterStore.cs,
  Web/AdminApi.cs, tests T203.cs, data/cap_t203.bin, ACHIEVEMENTS.md section 8). The marker is
  `serverUnique` from AchievementList.xml, and World hands it over in the **2nd u32 of every 24-byte
  0x2802 record** - the field T22 called `[u32 0]` - so no datasheet is needed. New planet-wide
  `server_achievements(achievement_id PRIMARY KEY, owner_id, party_id)`; `INSERT OR IGNORE` plus the row
  count is the whole first-claimant-wins rule. There is **no refusal reply**: 0x2803's success byte is a
  hard-coded 1 in the real Arbiter and the refusal is the record's absence from the list, so our answer to
  cap_final2b 3622 is byte-identical to 3623 once the five server firsts are held elsewhere. New
  `GET /api/server-achievements` and `POST /api/clear-server-achievement`. 3 tests. 0x2801
  DBS_UPDATE_SERVER_ACHIEVEMENT is left unsent on purpose - a DBS_* World did not ask for - and written up.

- T200: **nothing to do - T201 already covers it.** `/@party <n>` exists as `QaPartyCommands` (`party` /
  `raid`, `PartyManager.JoinForQa`, `PartyPlayer.QaDummy`, offline dummies that count toward the roster,
  capacity refusal), pinned in T201PartyCommands.cs against retail's own party frames. Three of the
  brief's premises were also wrong: the real command is `@party <userName>` (one dummy per invocation, no
  count), the chat line is `Create a new invitor[%s]!` carrying the typed NAME and no index, and the
  dummies DO appear in A->W frames (AS_DO_CREATE_PARTY, AS_REQUEST_ENTER_BATTLEFIELD, and World names them
  back in BSA_NOTIFY_USER_BATTLE_FIELD_ENTERABLE) - what they never get is runtime traffic.
  Sandbox, on the tree after this morning's rebase: 1054 passed, 20 failed, all of them gitignored
  `data/**` fixtures that are absent from the sandbox.

- T204: **turnkey setup - one settings file and one command** (Config/TerasConfig.cs, tools/setup.ps1,
  teras.example.json, docs/QUICKSTART.md, tests T204.cs; 18 source files now read settings through
  TerasConfig). Every `TERASHARP_*` setting is also a key in **teras.json**, resolved in one fixed order:
  **the environment wins, then the file, then the built-in default** - so a box that already exports
  variables behaves identically with and without a file, and a single `$env:` still overrides one value for
  one run. A malformed file is one warning and then ignored. `--check-config` now prints which file it
  loaded and the source of every value (`teras.json` / `env` / `default`), and `KnownVariables` was
  extended to the full set, so eight settings that were readable and unreported are now both.
  `tools/setup.ps1` (PS 5.1, idempotent, `-Check` = `-WhatIf`) asks four questions - server folder, public
  host, planet id, server name - generates the admin token and the Alt+A key once and never again, writes
  teras.json (MIN_MEMBERS deliberately absent), patches DeploymentConfig.xml text-level (planet id x3,
  portForClient/portForWorld, ExternalIp, `<Topography folderName=".\Topology">`) and ServerConfig.xml
  BOM- and CRLF-safely with one .bak per file, points tera-server-proxy/config.json at the Arbiter,
  installs the two mods this repo ships and names the ones it does not, runs `--check-config`, and writes
  start.ps1 (Topography `--sharedmemoryproducer=true` -> TeraSharp -> World per id -> proxy, each waited
  for) and stop.ps1 (announce -> kick -> reverse stop). `deployment.worldIds` is derived from the tree's
  own `WorldServerList` - a stock 100.02 tree's main world is id **0**, not 1, and an id with no row boots
  nothing. `Program.cs` is human-owned: apply `status/T204-Program.diff` (5 lookups, one warning sink, one
  log line). Verified: pwsh 7.4 against a fixture tree - virgin `-Check` 11 pending / 0 problems, real run
  changes exactly 2 lines of DeploymentConfig.xml with the BOM intact, third run 0 pending; and
  `--check-config` proves the override chain on the wire. Sandbox: 1058 passed, 20 failed (all of them
  gitignored `data/**` fixtures absent from the sandbox), 0 warnings.
  `tools/audit-release.ps1` over T204's own files: **clean**. Over the whole changed set it reports one
  blocking finding that predates this task - `Web/ApiGatewayServer.cs:17` quotes the retail phone-home
  address `<retail-phone-home-address>` in a comment, which the public tree rewrites to "a hardcoded retail address" -
  plus 12 non-blocking REVIEW lines, none of them from T202/T203/T204.
  **Not done: the D:\TeraSharp-public refresh.** It needs git (clone/copy/commit in a second repository)
  and this session cannot run git. The T173 strip is now characterised from the two trees - prepend the
  2-line SPDX header, rewrite retail literals to prose, drop the capture fixtures, decompiles, dupe/evidence
  tools and the private status notes - so it can be scripted; say the word and it becomes tools/sync-public.ps1.

- T204b: **the public-mirror refresh is a script** (tools/sync-public.ps1, tools/README.md). One-way file
  sync master -> D:\TeraSharp-public with the T173 strip, ending in `audit-release.ps1 -Strict` inside the
  public tree; it runs **no git commands**, so the review and the commit stay with the operator. Policy is
  four tables at the top, each entry with its reason: `$Scope` (src, status, docs, tools, the listed root
  files, and the two parts of data\ that are ours - `**/README.md` and `custom-datasheets\`, which T199 made
  a build dependency), `$Drop` (capture fixtures and evidence under data\, the per-task `.py` research
  scripts, tools\T179Audit, status/T105-fail.txt, ship.ps1, teras.json/start.ps1/stop.ps1, build output),
  `$Keep` (LICENSE, CHANGELOG.md, CONTRIBUTING.md, .env.example, .gitattributes, .gitignore, .git - never
  touched), and `$Rewrites`.
  **The strip, measured rather than assumed**, by diffing the two trees: the public files carry two SPDX
  lines plus a blank; `Program.cs`'s `TERASHARP_LOGS` default is the one path rewritten (`D:\packetlogs` ->
  `logs`); the retail phone-home address in `ApiGatewayServer.cs`'s comment becomes prose. Everything else
  is copied byte-for-byte, line endings included, because the public tree is a mix and its `.gitattributes`
  (`* text=auto`) normalises on commit. Three assumptions in the brief turned out to be wrong and are not
  implemented: T173 dropped **nothing** from status/ or tools/ except `T105-fail.txt` (every other
  "missing" file simply postdates T173), and it did **not** scrub developer paths in general - the public
  tree still has `D:\...` in comments, so a blanket scrub would touch a hundred files nobody asked about.
  A generic net covers what `$Rewrites` cannot: any routable public IPv4 surviving the strip fails the run
  and names the **master** file, before the audit does. RFC 5737 documentation addresses pass.
  The address in the rule table is spelled in octets on purpose - written as a literal, the script would
  itself carry a routable public IPv4 and the audit would refuse to publish it.
  Verified with pwsh 7.4 against a master/public fixture pair: `-Check` lists 17 writes / 6 drops / 1 stale
  and runs the audit read-only; the real run applies them, `-Prune` removes the stale file, the audit is
  clean, and a second run writes 0 files. Negative test: a new file quoting an unknown public address fails
  with the master path named. `status/T204-Program.diff` is still the only human-owned change outstanding.
- T205: **the public tree is releasable** (33 files in src/, status/, data/cap_timeline.md; 12 test files).
  Two jobs, both measured with the audit's own rules rather than by eye.
  **(1) 200 REVIEW findings rewritten in master** (199 of them reach the public tree; `data/cap_timeline.md`
  is dropped by the strip). The rule that fires is two or more decompiler-only identifiers on one line -
  `param_N`, a 1-3 letter `...VarN`, `local_XX`, `DAT_xxxxxx`, `auStack_xx` - or a line opening with a Ghidra
  type declaration; matching is case-insensitive, and `FUN_...` names and `Arb_part_NNN.c:LINE` citations are
  always allowed. So the fix is never a rename: every pasted block became prose, a table row, or pseudo-code
  using the real names of what it touches, with every offset, size, write order, comparison direction and
  citation carried over. Heaviest: MULTIPLAYER-DESIGN.md 36, GUILD-DESIGN.md 23, PARTY-DESIGN.md 22,
  ENTER-WORLD-FALLBACK.md 16, HANDSHAKE-DATA.md 14, WORLD-PARTIAL-LOAD.md 13; 30 were `//` and `///`
  comments in src/. `status/EXPLOIT-FIX-RANKING.diff` keeps its line count and leading characters so the
  hunk headers stay valid. No file was added to the audit's exception list and `tools/audit-release.ps1` was
  not touched.
  **(2) Every fixture-gated test now skips instead of failing.** One helper, `FixtureOrSkip(relative, what)`
  in T184hCapacity.cs, resolves the fixture with `FindRepoFile`, calls `Skip.Because` when it is absent and
  returns null; 22 tests open with a one-line gate on it (T184h x3, T190b, T190Cards x3, T192 x3, T195 x3,
  T199 x5, T201 x4). `T201_complete_native_registry...` had a bare `!` on the path, which is where the
  `Value cannot be null. (Parameter 'path')` came from; it now uses the gate's return value. The `?? throw`
  inside each frame helper stays as an internal invariant - unreachable once the test is gated.
  Verified with the real tools, not by inspection: `audit-release.ps1 -Strict` under pwsh 7.4 over a
  reconstructed master tree reports **REVIEW 0** (the 7 ADDRESS findings are the retail phone-home literals
  that `sync-public.ps1` rewrites on the way out, and are master-only). Build 0 errors, and the same 4
  pre-existing nullable warnings in the test Program.cs as before the change - none new. Suite with the
  fixtures present: **1078 passed, 0 failed, 80 skipped**; with every gated fixture removed:
  **1058 passed, 0 failed, 100 skipped** - exactly the 20 that used to fail now skip, and none of them
  fails either way. `sync-public.ps1 -Check` and the re-commit on the public branch are the human's step.
  `status/T204-Program.diff` is still the only human-owned change outstanding.
- T206: **the admin tool is an application, not a page** (13 new files, 9 changed; docs/ADMIN.md).
  T106's UI was one `const string` at the bottom of AdminServer.cs, written with single-quoted HTML
  attributes so no double quote would land in a verbatim string. It is gone: the UI is now
  `src/TeraSharp.Arbiter/Web/wwwroot/` - index.html, app.css, app.js and one ES module per screen -
  embedded by the csproj as `admin/<file>` and served by the new `Web/AdminAssets.cs` for any GET
  outside `/api/`. No framework, no bundler, no build step, nothing extra to deploy; AdminServer.cs
  went from 41757 bytes to 7246 and is back to moving bytes only. The files are **flat on purpose**:
  a `views/` folder embeds as `admin/views\x.js` on Windows and `admin/views/x.js` on Linux, so the
  same build would serve different URLs on the two machines.
  **Seven screens** (Dashboard, Accounts, Characters, Mail, Guilds, Server, Settings) over **21 new
  endpoints**: `/api/db` `/api/queue` `/api/settings` `/api/account-logins` `/api/item-search`
  `/api/achievements` `/api/parcels` `/api/guilds` `/api/guild` and the writes `set-position`
  `remove-item` `reset-skills` `set-ep` `send-mail` `delete-parcel` `guild-money` `guild-level`
  `guild-disband` `restart-notice` `reload-datasheets`. Store additions: `GetDatabaseStats` (size
  from the page geometry, which is right while a WAL is open), `GetParcelItems` (attachments could
  be counted but not listed), `SetGuildLevel`/`SetGuildMoney` (only deltas existed),
  `GetAllCharacters`, `DbPath`; plus `ItemNames.Search` so the item picker replaces knowing template
  ids by heart. Mail attachments are a flat `"template:amount,..."` string because this file's body
  scanner reads one key at a time and cannot parse arrays - a real parser is a bigger change than
  one form justifies, and `TryParseAttachments` refuses a malformed pair rather than dropping it.
  **Two things the brief did not ask for but the work needed.** (1) Every write now lands in
  `game_log` under the category `admin`, keyed on the account and character it touched, as well as
  in `admin_log`; before this an operator's edits were invisible from the player's side. (2) There
  was no login history at all - only an overwritten `characters.last_login` - so `SocialHandlers`
  writes one `game_log` login row where it already stamps the login, and the Accounts screen shows
  both. The token moved from `localStorage` to `sessionStorage`: it dies with the tab.
  **Secrets never leave the process.** `/api/settings` masks any name containing TOKEN, SECRET,
  PASSWORD, PASSWD, `_KEY` or APIKEY to `set, N characters`, so a screenshot of that screen is safe
  to attach to a bug report, and a setting added later that is named like a credential is masked by
  default rather than by someone remembering.
  **The brief's premise was half right.** `WebApp\ContentsControl*` is not the page inventory - it is
  the feature-toggle section (25 screens). The real inventory is `WebApp\AppResource\NavigateMenu.xml`
  (25 groups, 222 URLs) plus 5,910 labels in DisplayString.xml, and every `.aspx` is a ~200-byte stub
  whose markup is compiled into `bin\WebApp.dll`, so reading the .aspx files yields nothing. The seven
  areas map to `Server/`, `Account/`, `Users/` (34 detail tabs), `Announce/`, `Guild/` and `Log/`;
  docs/ADMIN.md names the retail screen behind each one and what was deliberately left out.
  Verified: 9 new tests, **1087 passed, 0 failed**; build 0 errors and 4 warnings, all pre-existing
  in the test Program.cs - the 2 in AdminApi.cs (`ClearServerAchievement` taking non-nullable
  `string body, string ip` from a nullable call site) are fixed here, so master goes from 6 to 4. The
  test that earns its keep is `T206_every_endpoint_the_UI_calls_is_a_route_the_server_answers`: it
  greps every `/api/` path out of the shipped JavaScript and asks the real router about each, keying
  on the router's own "no such endpoint" message rather than on a 404 (a known route may legitimately
  answer "no such character"). A mistyped path in a view module is otherwise a button nobody finds
  broken until an operator clicks it. `status/T204-Program.diff` is still the only human-owned change
  outstanding; `/api/warn` and `/api/teleport` stay unwired in Program.cs, so no screen offers them.
- T206b: **the UI refused a token the API accepted** (app.js, T206.cs, tools/admin-ui-probe.mjs).
  Nothing was wrong with the token, the header name or its case, the trim, or any endpoint - the
  listener accepted all of it, which is why `Invoke-RestMethod` worked. `app.js` armed the header
  poll with `setInterval(health, 5000)` in `start()`, at page LOAD. While nobody was signed in that
  called `/api/status` with `X-Admin-Token: ''`, earned a real 401, and `api()` signed out on any
  401 at all - so the message "the token was refused" was written onto a form nobody had submitted,
  and, when a poll was in flight at the moment the form WAS submitted, its reply landed after the
  sign-in had succeeded and threw the operator straight back to the login screen.
  Reproduced before fixing, by loading the shipped wwwroot into node behind a minimal DOM and
  driving the real sign-in path against a real AdminServer: `timers armed before sign-in : 1`,
  then `after an idle tick signedIn=false error='the token was refused'`, then
  `after raced sign-in signedIn=false` - the reported symptom, with curl on the same server
  answering 200 for the same token.
  **Three guards**, all in app.js. (1) `api()` answers `{status:401,"not signed in"}` locally when
  there is no token instead of sending an empty one. (2) A session `generation` is bumped on every
  sign-in and sign-out; a 401 only signs out when `issued === generation`, so a reply from a
  superseded session is discarded. (3) The poll is owned by `startHealth`/`stopHealth`, called from
  sign-in and sign-out, so nothing touches the API until a token exists - `start()` arms no timer.
  Also: `signIn` clears the stale login error, and setting the hash no longer routes twice.
  **`tools/admin-ui-probe.mjs`** is the throwaway harness kept: 12 checks over the real sign-in
  path against a running server, node 18+, deliberately outside the C# suite so the suite needs no
  node. Verified it catches the original defect (3 of 12 fail on a copy with the guards removed)
  and passes on the fix, including `wrapper GET /api/status|online|db|queue -> 200` through the UI's
  own wrapper and a wrong token still refused.
  Committed tests: `T206b_the_token_the_api_accepts_signs_in_through_the_UI_wrapper` boots a real
  AdminServer on a free loopback port and replays what the wrapper sends - skipping rather than
  failing when HttpListener will not bind, which on Windows needs a URL ACL - and found one
  premise wrong: **surrounding whitespace was never the cause**, because optional whitespace is not
  part of an HTTP header's field value, so an untrimmed paste reaches the server clean (app.js trims
  anyway, so what sessionStorage holds is the secret and nothing else).
  `T206b_the_shipped_app_never_calls_the_api_without_a_token` pins the three guards in the shipped
  module; verified it FAILS when the defect is put back. **1089 passed, 0 failed**, 0 warnings.
- T209: **the creation record is generated, not cloned** (StarterBlobTemplate.cs new, 7 files changed).
  Scoped to `starter_blob.bin` after the survey refuted the brief on the other three - see the
  findings below, they are the more useful half of this task.
  **starter_blob.bin is retired.** 15312 bytes, of which **15080 are zero** and only 232 are
  non-zero in 92 runs. `StarterBlob.Generate` builds all of it from zeros: 173 bytes from
  CreateCharData.xml (`InitLoc` pos/zone and `dir="-18"` -> i32 -3276 at +304, `createdLevel`,
  `firstInvenSize` 40), DefaultSkillSet.xml (68 bytes of skill ids), the C_CREATE_USER request and
  six constants. The rest was named against `ImportCharacterManager::Import_Users`
  (Arb_part_032.c:17732), which parses a character export straight into this record and therefore
  labels every column against its offset: `accountDBID` +0, `accountName` +8, `isAlive` +0xE8,
  `condition` +0x1A40 (World restamps 120.0 over it anyway), `profPet` +0x1ABC, `pegasusStage`
  +0x3ACC, `totalExp` +0x3AD0, `guildRecommendCount` +0x3B58, ActPoint +0x3B70, the flag group at
  +0x3B78 including `isTutorialPlaying` (**UpdateUserData refuses to save at all while it is set**),
  `ObserverType` +0x3B7C, the older-account flag +0x3BCC, and the nine 16-byte ODBC
  `SQL_TIMESTAMP_STRUCT` dates - confirmed as nanoseconds because the capture's two live fractions
  are exactly 840 ms and 793 ms.
  **Eleven bytes in seven runs are irreducible, and that is the honest result rather than a
  literal patch table.** Every one is the padding ABOVE a bool or u8 field, which the real Arbiter
  never initialises: two captures of identical state carry 114 and 193 at +0x3AF4 and four
  different values at +0x3B7B. Neither binary reads them. `UninitializedRuns` lists all seven and
  the test asserts the set is exactly that, so a field that goes missing later cannot hide behind
  a widening exception. The three wall-clock dates are reproducible in shape, not value, so
  `Generate` takes the time as a parameter.
  **A latent bug fixed on the way.** The captured template carried the CAPTURE's account on it, so
  every character this server ever created claimed to be account 1's second character. `WriteAccount`
  now stamps the real account id, name and slot at creation and again after the row id is known.
  **Inventory, behind a setting as asked.** `economy.synthItemRecords` (default false) builds the
  starter kit's 536-byte records with `WarehouseHandlers.BuildItemRecord` instead of cutting them
  out of `starter_inventory.bin`, so the file becomes optional. Off by default because the copied
  record is the one live-verified to get past `SA_ENTER_WORLD_FAILED`; the test proves both paths
  agree on the wire header and on all six named fields of all six records.
  `--selftest`: the starter-blob check is now WARN, and starter-inventory is WARN only when the
  setting is on. No Program.cs patch needed - it only consumes `Report`'s int.
  **Three findings that contradict the brief, each verified against the binaries, not inferred.**
  (1) `promotions_147E.bin` is **not promotions**: the Arbiter's own opcode table
  (Arb_part_003.c:4978-4993) names 0x147D/0x147E/0x1480/0x1484 `SA_LOAD_GUARD`, `AS_LOAD_GUARD`,
  `AS_LOAD_GUARD_FINISH`, `AS_ELECTION_STATE` - the castle-guard system. `DbProxyHandlers` has the
  whole family misnamed `AS_PROMOTION_*`, so its comment block is attached to the wrong opcodes.
  There is no empty-push path either: the writer takes a compile-time count of 0x550 and with no
  guards the tree walk never fires, so the real server sends ZERO 0x147E frames, not one empty one.
  Each 1360-byte payload is a memcpy of a live C++ Guard struct with heap pointers in it.
  `PromotionRecordSize = 1368`, so the file is **23** records - the source comment says 24, and
  both divisors happen to be exact, which is how that went unnoticed.
  (2) `starter_inventory.bin` was never dead code: `DbProxyHandlers.cs:6675` loaded it
  **unconditionally, before the store was consulted**, so until this task **no character could
  enter the world without it** - not just new ones. QUICKSTART said the opposite; fixed.
  (3) `handshake_burst.bin` is not def-buildable today at all: the opcode map carries 2167 names,
  none of them `AS_` or `DBS_`, and `DefinitionWriter` emits client framing (4-byte header, u16
  offsets) where A->W needs 6-byte/u32. ~53 of 63 frames would build after those two fixes; six
  need the capture regardless (0x29E2's 152 bytes have no schema at all).
  Verified: **1094 passed, 0 failed**, 0 new warnings; the generated blob is byte-identical to the
  capture outside the eleven padding bytes and the three clock structs, proven by building with the
  captured character's own inputs. Docs: QUICKSTART step 3 is three files instead of four (it does
  not leave - that was contingent on all four), `data/README.md`'s runtime table now says which are
  still needed and why. `status/T204-Program.diff` is still the only human-owned change outstanding.
- T209c: **the guard family is named correctly and promotions_147E.bin is retired** (4 files, 2 tests).
  Part 1 of the brief only - part 2 is waiting on the live test, see the end of this entry.
  **The rename.** `0x147D`/`0x147E`/`0x1480`/`0x1484` were `AS_PROMOTION_LIST_REQ` /
  `AS_PROMOTION_RECORD` / `AS_PROMOTION_LIST_END` / `AS_PROMOTION_LIST_BEGIN`. The Arbiter's own
  opcode-name table (Arb_part_003.c:4978-4993) gives `SA_LOAD_GUARD`, `AS_LOAD_GUARD`,
  `AS_LOAD_GUARD_FINISH` and `AS_ELECTION_STATE` - the castle-and-lord Guard system, consumed by
  World's `GuardManager::OnLoadGuard`. Promotions are the separate 0x291x family this file already
  handles, so the old comment's `PromotionController` crash story was attached to the wrong
  opcodes entirely. `OnPromotionListRequest` is now `OnLoadGuardRequest`.
  **Sending nothing is retail, not a shortcut.** `World::SendLoadGuard` (Arb_part_074.c:4035) sends
  the election state, walks ten continent slots calling `Guard::SendLoadGuard`
  (Arb_part_081.c:3130) once per Guard, then sends the finish marker. The per-guard writer takes
  its length as the compile-time constant 0x550, so a frame is never empty, and the walk starts
  with a begin-not-equal-end test - **with no guards it emits no frame at all**. This server models
  no Guards, so zero `0x147E` frames is the correct wire. Verified the `0x1484` payload in the
  decompile rather than trusting the old comment: it writes ONE u32 (the lord-election state), not
  a 4-byte list header - so the two frames we still send are **byte-identical to before**, and all
  that changed is that 23 replayed records are gone.
  **Why the file had to go, not just be made optional.** Each 1360-byte payload is a `memcpy` of a
  live C++ Guard struct - bytes 568..700 are that process's heap pointers - so it can be neither
  generated nor edited, and replaying it pushed 23 of another server's castles into a world with
  none. The old comment said 24 records; it is 23 of 1368 bytes. Both 23x1368 and 24x1311 come to
  exactly 31464, which is how that went unnoticed.
  Dropped with it: the `promotion records` `--selftest` check, `PromotionRecordSize`, the cached
  `_promotions` buffer, and the `ship.ps1` copy. `SelfTest.CheckRecordFile` stays - T37 tests it -
  but on a file of its own now that its only caller is gone. Nothing in `src/` reads
  `promotions_147E.bin`; the file on disk is inert and can be deleted.
  Verified: **1104 passed, 0 failed**, 0 new warnings. The new tests pin the wire
  (`0x1484` carrying u32 0, then `0x1480` empty, and not one `0x147E`), that the reply is
  byte-identical across calls and ignores the request bytes, and that the source no longer loads
  the file.
  **Part 2 is not done and must not be assumed.** Flipping `economy.synthItemRecords` to true by
  default needs the live test the brief describes, which only the human can run: set it true, move
  `data\starter_inventory.bin` out of the way, create a character, enter world, check the six
  starter items are in the bag, then relog and check they are still there. When that passes, the
  flip is one default in `TerasConfig`/`teras.example.json` plus dropping the
  `starter inventory` selftest check and the QUICKSTART line. Until then the file is still
  required and the setting stays off.
  `status/T204-Program.diff` is still the only human-owned change outstanding.

## T209d - the creation hang was continent routing, not the generated blob (2026-09-27)

`cap_t209.log` refutes the brief's premise: after both `AS_ENTER_WORLD` frames (continent 5,
instance -1) World sent **no** `SDB_USER_ENTERWORLD`, so it never read the generated record and no
named field was rejected. The creation start position is byte-identical before and after T209
(`start=zone 5 (16260,1253,-4410)` in `arbiter-mail1.log` and `arbiter-t209.log`). The battleground
World's `0x164D` roster claims continent 5, that claim was folded into the configured-owner table,
and the fresh character was routed to world 10. Roster claims now live in their own table and
answer only the dungeon hand-off; a plain enter-world resolves through config and announced
channels, then the catch-all World. Details and the evidence table: `MULTIWORLD-DESIGN.md` T209d.

`0x2956 SDB_CREATE_NEW_CITY_WAR_LEAGUE` is unchanged and still unanswered - see the T209d report;
it arrives once per World start with payload `01 00 00 00`, and it is equally unanswered in the
working runs, so it is not the hang.

## T209c part 2 - synthItemRecords is the default and starter_inventory.bin is retired (2026-09-27)

Live test passed: character 'newnew', `economy.synthItemRecords` on, `data\starter_inventory.bin`
absent - entered world with its six starter items and still had them after a relog. This supersedes
the "Part 2 is not done and must not be assumed" note in the T209 entry above.

| Change | Where |
|---|---|
| default flipped to true (unset or empty = on; `0` / `false` = off) | `DbProxyHandlers.SynthItemRecords` |
| `starter inventory` selftest check dropped | `World/SelfTest.cs` |
| `"synthItemRecords": true` | `teras.example.json` |
| step 3 is now "Nothing to supply" - a table of all four retired files | `docs/QUICKSTART.md` |
| runtime table: every file **no** | `data/README.md` |
| the env-var table expects on-by-default | `Tests/T209.cs` |

The file still wins when present (`template != null` takes the copy path), so a deployment that
keeps its capture is byte-for-byte unchanged. **Human-owned:** delete `data\starter_inventory.bin`
from the working tree - nothing reads it now.

## T191d - the WASD guide on every login: the GM-skill switch, not tutorial state (2026-09-27)

Operator-only (`S_LOGIN_ARBITER.status` 0x21 in `cap_wasd_client`, 0x1F in `cap_2man_b_client1/2`).
Tips, the user-setting round-trip, `S_LOGIN`, `S_USER_STATUS` and `AS_ENTER_WORLD.TutorialUser` all
match retail byte for byte. The missing frame is `S_ADMIN_GM_SKILL` (0x64BE)
`[i32 skill=0][u8 enabled]`, which the real Arbiter sends once per world entry between
`S_FESTIVAL_LIST` and `S_LOAD_TOPO` (`cap_final_gm_client2` 99 and 2445) and which
`cap_wasd_client` never receives in three world entries - so the client's GM-skill switch is never
initialised. T152 removed it because the value was a guess; it is back carrying World's own
`SDB_USER_VAPORIZED` state, which is what T152 actually required. Details:
`T191c-CLIENT-SETTINGS.md` T191d.

## T208d-b - T208d's roster assertion, not the replay builder (2026-09-27)

The failing expectation was "three-member roster"; the fixture's parties are a leader plus one QA
dummy, and the frame carried 320 B / 376 B, which is right.
`Party::UnicastPartyInfoToSpecialWorldServer` (Arb_part_067.c:15455-15478) accepts a member slot on
`slot[0] != -1 && slot[1] != 0` alone - no online, session or dummy test - so dummies and offline
members both travel, exactly as `party.Members()` yields them.
`PartyManager.BuildWorldConnectReplay` needed no change. The test now reads the roster size off the
party table instead of hard-coding it. Suite on master HEAD: 1120 passed, 0 failed, 84 skipped.
Details: `T208d-PARTY-REPLAY.md` T208d-b.

## T207c - the 10-second hub poll is GetServerStat, and it is answered (2026-09-28)

`unhandled OpUent call 3 (0 B)` in `arbiter-bg3.log` is `hubFunctions.js`'s `getServerStat` -
`GetServerStatReq` has no fields, hence the 0-byte payload. It is tera-api's **availability poll**
(`ServerCheckActions.all`), not the Online page, so the answer's `serverId` is the plain server
number in tera-api's `server_info` (2800), not a gusid; a match flips the server to available
through the hub instead of a TCP probe. Answered with one
`ServerInfo { serverId, userCnt }`, `userCnt` = distinct accounts in world (the message has no
field for account ids). `getAllServerStat` (5) is refused by name rather than left unhandled.
Every OpArb call was already answered. Full table: `T207-HUB.md` T207c.

## T215 - auth and the listener bind are boot blockers, not warnings (2026-09-28)

| Change | Where |
|---|---|
| `auth.enabled` forced true on every run; `auth.url` keeps an existing answer | `tools/setup.ps1` |
| `start.ps1` exits 2 with nothing started when `auth.enabled` is false, `auth.url` is empty, or `listener.bind` is off loopback; `-Insecure` overrides | `tools/setup.ps1` (`$startBody`) |
| `listener bind` is a real self-test now - `--selftest` exits non-zero off loopback | `World/SelfTest.cs` `CheckLoopbackBind` |
| auth on with an empty `TERASHARP_AUTH_URL` is its own note | `World/SelfTest.cs` `Notes` |
| `TERASHARP_AUTH` listed first | `docs/GO-LIVE.md`, `docs/QUICKSTART.md`, `docs/OPERATIONS.md` |

The PowerShell was not executed: this session has no shell on the deployment host. The generated
`start.ps1` was checked structurally (the preflight and `-Insecure` are inside the `@'...'@` body,
no stray here-string terminator) but not run - `.\setup.ps1 -NonInteractive` then `.\start.ps1` on a
tree with `auth.enabled=false` is the one-minute confirmation.

## T217 - 0x161E is a field-event timer; the makeitem forward never left the Arbiter (2026-09-28)

0x161E is `SA_GIVE_FIELD_EVENT_CLEAR_REWARD` (`Arb_part_003.c:5812`). Its handler reads
`[u32 offset @6][u32 byteLength @10][u32 fieldEventId @14]` plus a vector of 16-byte rows, hands
them to a `FieldDataSheet` holder and returns 1 **with no packet writer** - one-way. It is periodic:
7m20s apart in `cap_makeitem` and in `arbiter-makeitem2.log`, where two of the three arrive before
any character logged in. Not the create, not a pre-notice.

The create pair is still `A->W 0x2829 AS_ADMIN_COMMAND` -> `W->A 0x2768 SDB_ITEM_SINGLE` one
millisecond later, and `makeitem 88384 1` (two arguments, like the failing one) works in the
capture. What broke is that **no 0x2829 left the Arbiter**: `SendToWorld` returns false on a closed
gate and `ForwardToWorld` discarded it, so the command logged `-> ForwardToWorld` and vanished.
That return is now checked, the gate is named in the log and told to the GM. Which gate closed on
22:14:25 is not in that log - one `/@makeitem` on this build prints it. Details:
`T217-MAKEITEM.md`.

## T217b - the makeitem forward reached World; the capture just ended first (2026-09-28)

`cap_makeitem3` frame 1295 is `A->W#1 05:48:10.871 0x2829 len=52` carrying `makeitem 88384 1` -
byte-identical to the working `cap_makeitem` 15690 except the player id (9 vs 10). The forward was
never lost, and T217's gate check was right to print nothing. No `0x2768` follows because **that
frame is the last one in the tap** and `arbiter-makeitem3.log` ends on the same line: both were
stopped the instant the command was issued. A capture that runs a few seconds longer settles
whether World answers.

Tracing the path did find a genuine silent drop: `WorldLink.SendFrame` began
`if (!_sock.Connected) return;` with no log and a void return, so a link that had dropped but not
yet been reaped passed `HasLinks`, swallowed the frame, and `SendToWorld` still reported success.
`WorldBridge.cs` is human-owned - the fix is `status/T217b-PATCH.diff` (new `TrySendFrame` on link
and bridge, the refusal logged, `SendToWorld` returning the real answer); applied it builds clean
and the suite is 1126 / 0 / 84. Landed directly: `ForwardToWorld` logs the successful send too, so
the log alone now distinguishes "never sent" from "sent, no answer". Details: `T217-MAKEITEM.md`.

## T218 - the Arbiter already sends AdminLevel 5 (2026-09-28)

`cap_makeitem3`'s `AS_ENTER_WORLD` for player 9 - the session whose console said
`SpawnComplete caludesucks(9) AdminLevel[0]` - carries **`05 00 00 00` at payload+111**.
`WorldEntry.cs:51` already builds the payload with `GmCommandHandlers.LevelOf(s, Program.Store)`,
and the `C_ADMIN` line for that session resolves 5 too. Retail's own GM frame (`cap_final`, T46's
adminLevel 1) puts `01` at the same offset, and a full 183-byte diff of the two frames leaves only
per-session values differing - handles, UserDbId, SessionKey, continent, position, Ticket, GameId -
so nothing is shifted.

World's side: `WorldServer.exe.c:847798` binds `adminLevel` to User+0xA474 and `:935500` prints it;
the only other write to 0xA474 in the binary is the `= 0` initialiser at `:803532`. So the Arbiter
is not the one getting it wrong, and the likeliest reading is that the quoted console line came from
a session entered before that account was listed in `TERASHARP_GM_ACCOUNTS`. One capture of the
enter-world frame and the console line from the SAME session settles it. Pinned by three T218
tests. Details: `T218-ADMINLEVEL.md`.

### T219b - the item rows, not the allocator

The live DB disproves the brief: no duplicate or out-of-range `item_db_id`, and
`counters.item_id` == `max(item_db_id)` == 1313. A record-less row is not it either - the
synthesised record matches a stored one in every named field. What it does show is eight rows on
character 9 (one on character 10, none on the fresh character 16) whose stored 536-byte record names
the pocket and slot the item sat in BEFORE a move: `MoveItem` wrote only the columns, and
`UpsertItem` keeps the old blob when called with no record. Both now re-stamp the record from the
row, and `POST /api/repair-inventory` fixes rows written earlier. Four T219b tests; suite
1133/0/84. Still unproven as the cause of the refused create - see the T219b section of `INVENTORY-DESIGN.md`
for the capture that would settle it.

### T219c - the expired benefit, not the item

`cap_makeitem` 15690/15691 vs `cap_item_new` 76558: the same `makeitem 88384 1` gets an 886-byte
`0x2768` back for player 10 (account 2) and nothing at all for player 9 (account 1). World's
create path reads no benefit, but its account tick does: `User::OnTickAccountTrait` ->
`GetExpiredPackges` -> `DeletePropertyList`, which resolves each expired id in the UserTrait
datasheet, logs `Unknown user trait 1000`, asserts at `AccountTrait.cpp(523)` and leaves the loop -
every tenth tick, forever. T181 seeded 533/534/1000 on operator accounts and 1000 expired
2026-08-15. Per account, and DB state, which is why the rollback changed nothing.

The seeding is retired, the rows are swept on the next benefit load (or by
`POST /api/remove-benefit`), and an expired benefit is withheld from 0x28BC with a warning naming
it. Five T219c tests; suite 1138/0/84. Details: `T217-MAKEITEM.md`.
