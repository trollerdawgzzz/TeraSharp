# Capture plan — the two sessions, and what each one unblocks

Everything TeraSharp still gets wrong is a layout nobody has ever seen on the wire. Parties, guilds,
the warehouse, mail with mail in it, the broker and private chat channels are all built from the
decompiled writers alone — good evidence, but the four existing captures are **one account, one
login, no second player, no bank, no guild, no mailbox**, so none of it has ever been checked
against bytes.

Two sessions fix that, and they are not interchangeable:

| | **Session A — real Arbiter, both taps** | **Session B — populated server, client logger** |
|---|---|---|
| what you get | Arbiter<->World **and** client<->Arbiter | client<->server only |
| what it proves | the internal `SDB_`/`AS_`/`SA_` layouts we build | the `C_`/`S_` layouts the client renders |
| needs | the 100.02 server stack + two GM accounts in tera-api | a live 100.02 server with other people on it |
| cost | an evening of setup, then ~40 minutes | whatever an account on that server costs |
| blocked by | nothing | finding a server |

**Do A first.** It answers more and it answers it about our own build. B is what fills in the
things two accounts on an empty server cannot produce: a guild that already has 40 members, a
broker with listings, a mailbox with attachments.

Neither session needs TeraSharp running. Session A runs the **real** `ArbiterServer.exe`; that is
the whole point.

---

# Session A — real Arbiter with the Arbiter<->World tap

## A.0 Setup

1. **The tap already labels the socket** — done in T56. Every line is now
   `[seq] [W->A#3] <iso> len=N`, where 3 is the World socket the chunk arrived on, counted from 1
   in connect order. Confirm it by looking at the first few lines of a fresh log: if the direction
   token has no `#N`, the box is running an old copy of
   `D:\v100\TERA_SERVER.100\arbiter-world-tap.js` and Q2 below cannot be answered at all.
   `tools/README.md` has the format and the regex any other parser needs.
2. **Record each socket's `SA_REGISTER` (0x138A)** as it arrives: `IsBypass` u8 @+6, `PlanetId`
   @+7, `WorldId` @+0x0B, `TotalBypassCount` @+0x0F, `BypassIndex` i32 @+0x13. That is what turns
   a connection id into "the bypass link" or "the data link".
3. **Client-side capture as well.** The A<->W tap gives none of the `S_` packets the client
   renders. Run `tera-server-proxy` in front of the real Arbiter for the same session so the two
   logs can be lined up by wall clock — that pairing is what pinned `S_PARCEL_READ_RECV_STATUS`
   in T42.
4. **Two accounts, two characters, both GM in tera-api**, with `UserDbId` 1 and 2 if you can
   arrange it (different `UserDbId % 24`, so link selection decorrelates). GM on both is what makes
   guild creation and gold-gated actions possible without grinding.
5. **Start World fresh** so ticket allocation starts from 0, and **note the wall-clock time** you
   start — every step below is found in the log by timestamp.
6. Keep each step separated by ~10 seconds of standing still. The single most expensive thing about
   the existing captures is that everything overlaps.
7. **Afterwards, reframe both logs before reading either.** A tap chunk is not a frame — the 23
   `0x147E` promotion records arrive as one 30234-byte write — so anything that reads a chunk as a
   packet is wrong:

   ```powershell
   cd D:\v100\TERA_SERVER.100\TeraSharp\tools
   .\reframe-tap.ps1    -Log <captures>\arb_world_<stamp>.log -Opcodes 0x139E,0x139F,0x13F8
   .\reframe-client.ps1 -Log <captures>\capture_<stamp>.log   -Packets S_GUILD_INFO
   ```

   Each writes `<log>_ctl.txt` (one line per frame, noise removed — read this first) and
   `<log>_frames.txt` (full hex of the opcodes you named). Both print a reject count at the end;
   a non-zero one means part of the capture is unusable and it is worth knowing that before the
   server is torn down. `tools/README.md`.

## A.1 Two players, in view of each other

| # | do this | expect on the tap | consumed by |
|---|---|---|---|
| 1 | log in A, walk to a quiet spot | `AS_ENTER_WORLD` Ticket=0; every 0x13F7 has `UserListBytes=16` | baseline |
| 2 | log in B while A stays in world, same zone, in view | second `AS_ENTER_WORLD` Ticket=1 | baseline |
| 3 | **A walks in a circle while B watches** | ★ 0x13F7 with `UserListBytes=32` — two entries, two Tickets, two seqs | **Q1** below; `World/TunnelFrames.cs` round-trip tests |
| 4 | A types in area chat, B replies | one 0x1449 each, each answered by a 0x13F7 carrying **both** tickets | confirms the fan-out shape |
| 5 | A whispers B, B whispers back | **nothing on the A<->W tap** | confirms whisper is Arbiter-local — the premise `ChatManager` is built on |
| 6 | A logs out to lobby, B stays | `SA_LEAVE_WORLD` for A only, carrying A's Ticket and `LastIndex` | leave path with 2 sessions |
| 7 | A logs back in while B is still in world | new `AS_ENTER_WORLD` — **is the Ticket 0 again, or 2?** | **Q3** below; `TicketAllocator` |

**Q1 — the unnamed dword at `UserList` entry+4.** The Arbiter never reads it. If it **differs
between the two entries of one frame** it is per-destination; if it is **identical** it is
per-source. Either way, record it.
**Q2 — bypass link or data link?** Needs A.0 step 1. If the two players' 0x13F7 frames arrive on
different sockets, World is using `UserDbId % 24` and TeraSharp's single-link send needs revisiting.
**Q3 — is a Ticket reused after a leave?** The Arbiter's cursor is monotonic modulo `maxUsers*4`,
so it should not come back as 0.

## A.2 Party — **no captured party bytes exist anywhere**

`status/PARTY-DESIGN.md` §11.1 is blunt about it: every A->W party frame TeraSharp sends is golden
against a decompiled writer and nothing else. This is the highest-value half-hour in the plan.

| # | do this | expect | consumed by |
|---|---|---|---|
| 1 | A invites B to a party, B accepts | `W->A SA_JOIN_PARTY` 0x1395, then `A->W AS_DO_CREATE_PARTY` 0x139E + `AS_DO_ADD_PARTY_MEMBER` 0x139F | `PartyManager` golden tests; PARTY-DESIGN §6 |
| 2 | party chat, both ways | **nothing on the A<->W tap** | confirms party chat is Arbiter-local |
| 3 | kill one mob as a party, loot it | `W->A SA_BYPASS_TO_GROUP` 0x13F8 — the only W->A frame that names a `GroupId` instead of tickets | `PartyWiring.TryHandleWorldFrame` |
| 4 | change the looting method | `A->W AS_PARTY_LOOTING_METHOD` 0x13BB, then `W->A SA_CHANGE_LOOTING_METHOD` 0x139D | PARTY-DESIGN §7 |
| 5 | A promotes B to leader | whichever of `SA_CHANGE_PARTY_MEMBER_AUTHORITY` 0x139C / `SA_SWAP_PARTY` 0x139A fires | the four gated-but-unmodelled opcodes, PARTY-DESIGN §11.6 |
| 6 | B leaves; then A re-invites and **B declines** | the leave and the decline paths | PARTY-DESIGN §8 |
| 7 | on the **client** log: the whole exchange | `S_PARTY_MEMBER_LIST`, `S_PARTY_MEMBER_INFO` with real values | the party UI frames we emit blind |

## A.3 Guild — the highest-risk layouts in the codebase

`status/GUILD-DESIGN.md` §8 ranks these. `SDB_CREATE_GUILD2`'s 0x2A-byte fixed part is the one
place where a wrong size kills the World link outright.

| # | do this | expect | consumed by |
|---|---|---|---|
| 1 | A creates a guild | ★ `W->A SDB_CREATE_GUILD2` — **measure the fixed part**; then `DBS_CREATE_GUILD2` broadcast-then-unicast with its `DlmId` trick | GUILD-DESIGN §8.1; `GuildWiring` |
| 2 | A opens the guild window | client-side `S_GUILD_INFO` — 31 unaligned fields | GUILD-DESIGN §8.2; T52 |
| 3 | A invites B, B accepts | the five-step join sequence; **confirm `AS_GUILD_JOINED` is unicast, not broadcast** | GUILD-DESIGN §6 and §8.3; **T52** |
| 4 | A sets the announcement, then the guild message | the two update paths | `GuildHandlers` |
| 5 | A changes a member's rank, then kicks B | rank + leave | GUILD-DESIGN §7 |
| 6 | A uploads a guild logo | `C_UPDATE_GUILD_LOGO` — **confirm it is `bytes`, not `string`** | GUILD-DESIGN §8.4 (the only `C_` correction we have made) |
| 7 | **restart WorldServer with the guild in the DB** | the whole `0x27CF -> 0x27ED -> 0x27D0 -> 0x27D1 -> 0x27D2 -> 0x27D3 -> 0x27ED(Success=0)` boot sequence with a non-empty guild | the boot load we only have the empty form of |

## A.4 Warehouse — nine pairs, zero captured frames

Every offset in `World/WarehouseHandlers.cs` comes from the PDL dumpers cross-checked against the
writers. Each handler's min-length guard lands exactly on the end of its last field, which is good
evidence and not proof.

| # | do this | expect | consumed by |
|---|---|---|---|
| 1 | open a bank | `0x274A SDB_VIEW_WAREHOUSE -> 0x274B` with real `ItemData` | T42 layouts |
| 2 | store one item, then get it back | `0x274C -> 0x274D`, `0x274E -> 0x274F`, atoms with real ids | T42; `BagItems.ApplyReplyAtoms` |
| 3 | drag an item within the bank | `0x277F SDB_CHANGE_WAREHOUSE_POS -> 0x2780` | T42 |
| 4 | buy a slot expansion | ★ `0x283F` answered with **`0x283E`, not `0x2840`** — confirm | T42's most surprising claim |
| 5 | use auto-sort | `0x27E2 -> 0x27E3` | T42 |
| 6 | anything that could send `0x2754 SDB_MOVE_WAREHOUSE_ITEM` (guild bank?) | **does World ever send it?** If it does, the real server hangs there too | MAIL-WAREHOUSE §9 |

## A.5 Mail with mail in it

| # | do this | expect | consumed by |
|---|---|---|---|
| 1 | B sends A a parcel with an item and gold | `0x2779 SDB_MAKE_PARCEL -> 0x277A` | T45 `ParcelDbHandlers` |
| 2 | A opens the mailbox | ★ `0x2777 -> 0x2778` with **`ParcelCount = 1`** — this is the only way to see `ParcelDataNoMsg`'s 0x9e8-byte interior | MAIL-WAREHOUSE §9; the one thing blocking a non-empty inbox |
| 3 | A reads the message, then claims the attachment | `C_SHOW_PARCEL_MESSAGE`, then `0x277B -> 0x277C` | T42/T45 |
| 4 | A claims all, on a second parcel | `0x277D SDB_RECV_PARCEL_EX -> 0x277E` — **and what the unnamed second ref slot holds** | MAIL-WAREHOUSE §9 |
| 5 | A returns a third parcel to sender | `0x2781 -> 0x2782` | T45 |
| 6 | A deletes an empty one | `0x2811 -> 0x2812` | T45 |

## A.6 Cheap extras while everything is already running

| # | do this | expect | consumed by |
|---|---|---|---|
| 1 | A creates a private chat channel and B joins by name + password | the create/join/leave packets — **no capture contains a single one** | `status/CHAT-DESIGN.md` §9; the member cap, the invite list, the master-promotion rule |
| 2 | both walk into the same dungeon instance and out again | zone-change frames per player | `ChannelInstanceId` with two users |
| 3 | B kills the client outright while A stays | the disconnect path for B only | leave path |
| 4 | open each of: dungeon cool-time list, guild list, party-match window, candidate list, battlefield result | six `S_` replies we cannot currently build | the six shapes listed in `status/STATUS.md` Open |
| 5 | run A.1 step 4 again with the two characters in **different zones** | the 0x13F7 should carry only the sender's Ticket | proves World, not the Arbiter, filters by visibility |

---

# Session B — a populated 100.02 server with a client-side packet logger

What this gives that Session A cannot: **content**. Two accounts on an empty server produce a
two-member guild with no history, an empty broker and a mailbox you filled yourself. A live server
produces a 40-member guild list, a broker with real listings and real price history, guild wars,
rankings and a lord election — the shapes where our list-building is most likely to be wrong in a
way a two-row test would never show.

What it cannot give: **any `SDB_`/`AS_`/`SA_` frame at all.** The internal protocol never leaves the
server. Do not plan any internal layout work around this session.

## B.0 Setup

1. A 100.02 client with a packet logger (toolbox-style mod, or `tera-server-proxy` pointed at that
   server if its login flow allows it). Log **both** directions with timestamps.
2. Protocol map `376012` must match — check one known opcode (`C_CHECK_VERSION`) before trusting
   anything.
3. Record the account's own `playerId`, so "is this field me or them" is answerable.
4. Respect the server's rules. This is a passive read of your own session's traffic.

## B.1 The runs

| # | do this | expect | consumed by |
|---|---|---|---|
| 1 | join a large guild; open the guild window and the member list | `S_GUILD_INFO` + `S_GUILD_MEMBER_LIST` with 40+ members, real ranks, real timestamps | GUILD-DESIGN §8.2 — 31 unaligned fields, paginated |
| 2 | open the guild log and page through it | `S_GUILD_HISTORY` (or equivalent) past page 1 | the pagination clamp T48 added — `page*size < count` on real data |
| 3 | open guild war history | the packet that crashed the **real** Arbiter on page 0 of an empty history | `GuildHandlers.ClampPage`; the one bug the security notes found in the original |
| 4 | **open the broker; search; list an item; check price history; cancel** | the whole broker family, request and reply | **T53** — there is nothing at all in the repo for this |
| 5 | receive a real mail with an attachment from another player | `S_*PARCEL*` with a populated `ParcelData` | cross-check against A.5 step 2 |
| 6 | open rankings, then the lord/election window | list shapes | `status/STATUS.md` "not started" |
| 7 | join a real party, then a raid; watch the roster change | `S_PARTY_MEMBER_LIST` at 5+ members, and the raid variant | PARTY-DESIGN — our list building is untested past 2 |
| 8 | use a stackable item from the quickslot | ★ `C_SHOW_ITEM_TOOLTIP_EX` and the `S_SHOW_ITEM_TOOLTIP` that answers it | the potion counter — the exact exchange no capture has (`status/CLIENT-REJECTS.md` §2.4) |
| 9 | idle in a city for two minutes | whatever the client sends unprompted | the remaining "handler has not been implemented yet!!!" opcodes |

---

# What each open task gets

| task / doc | from | step |
|---|---|---|
| **T52** guild `SA_` direction + `C_INVITE_USER_TO_GUILD` | A | A.3.3 |
| **T53** trade broker | B | B.1.4 |
| `PartyManager` golden tests (PARTY-DESIGN §11.1) | A | A.2 all |
| `SDB_CREATE_GUILD2` fixed part (GUILD-DESIGN §8.1) | A | A.3.1 |
| `S_GUILD_INFO` / `S_GUILD_MEMBER_LIST` (§8.2) | A + B | A.3.2, B.1.1 |
| `WarehouseHandlers` offsets (MAIL-WAREHOUSE §4) | A | A.4 all |
| `ParcelDataNoMsg` interior (MAIL-WAREHOUSE §9) | A | A.5.2 |
| `RECV_PARCEL_EX`'s unnamed second ref | A | A.5.4 |
| `SDB_MOVE_WAREHOUSE_ITEM` 0x2754 — dead or not | A | A.4.6 |
| Private channels (CHAT-DESIGN §9) | A | A.6.1 |
| Tunnel Q1/Q2/Q3 (MULTIPLAYER-DESIGN §8) | A | A.1.3, A.1.7 |
| The six unbuildable `S_` replies (STATUS Open) | A | A.6.4 |
| The potion tooltip exchange (CLIENT-REJECTS §2.4) | B | B.1.8 |
| Guild-war pagination on real data (SECURITY-AUDIT §3) | B | B.1.3 |

# What neither session gives

- **`ServerConfig.xml` values.** Every config key name in MAIL-WAREHOUSE §3.2 and §4.3 is verified
  from the binary; none of the values is. Read them off the live config file — that is a file copy,
  not a capture.
- **The private-channel member cap** (`DAT_140e315a8`'s compiled-in default). A capture shows what
  one server was configured with, not the default.
- **Anything about a second World process.** Multi-World is not started and neither session touches it.

# Before you start either one

Do `status/LIVE-CHECKLIST.md` first. It costs 45 minutes, it needs no new infrastructure, and it
tells you which of the six wired-but-untested subsystems already work — which changes what is worth
capturing. Capturing guild frames to debug a guild feature that is simply not registered would be
a wasted evening.
