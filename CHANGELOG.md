# Changelog

TeraSharp was built as a numbered task series. This is T1-T172 grouped by area, not by date;
the numbers in brackets are the tasks. `status/STATUS.md` has the per-task detail and each
`status/<AREA>.md` the design notes.

"Live" below means seen working against a real client. Most of the breadth is only **wired**
(registered, tested, never run live); `status/STATUS.md` "Status by area" is the honest table.

## T173 - public release refresh

- Public tree refreshed from master; captures, retail material and capture paths stripped;
  `tools/audit-release.ps1 -Strict` clean.
- Tests whose fixture is absent report `SKIP` instead of passing silently; the summary line is
  `N passed, F failed, S skipped`.
- Docs for everything since T139; SPDX licence headers on every source file.

## Client protocol, login and the lobby

- Client crypto, codec, login, character list, select, create and delete - live.
- The mode-select screen is `S_LOGIN_ARBITER.status` (T89b); the rest of the lobby packet for
  packet (T104, T106, T106b, T106c).
- Character select shows the stored level, not the login snapshot (T122); the `[GM]` tag on
  select (T144).
- Watched movies stored per account, so the intro stops replaying (T104).
- First-visit cinematics play: visited sections per character, a new character starts empty,
  16-byte list entries (T172).

## The World link and the DB proxy

- Handshake, promotion records, the post-handshake bursts - live. Enter-world, blob save and
  load, relog into a dead instance (T21) - live.
- The captured replay table replaced by real handlers, pair by pair: per-user writes (T15),
  then T65, T69, T77, T79, T150b, T154, T164, T166-T168 and T170.
- Every DB-proxy request/reply twin classified - answered, denied, or real handler (T165,
  T165b). An unknown guild no longer closes the World link (T168b).
- The opcode census closed: every unhandled or never-sent opcode is implemented or filed with a
  reason (T169, T172; `status/T169-CENSUS.md`).
- Multi-World groundwork: world registration (T103), enter-dungeon routing (T108), the
  ServerConfig world list (T111), per-player control frames to the right World (T112). The
  human-owned half waits as `status/MULTIWORLD-PATCH.diff`.

## Characters, items and progression

- Quests (T17); skills in the blob (T18); achievements, tips, reputation, fatigability and
  dungeon cool times from rows (T22, T25, T26).
- Inventory as rows with the item record rebuilt from them (T44); money (T59); starter-kit ids
  from the shared counter (T105); bag size (T150b); non-stackable inserts, GM makeitem (T153).
- Play time (T113). A level set outside World reaches World, so skills are learned (T152b).
- Cards and crests (T83-T86); EP pages, skill polishing, dungeon rank (T167); clear all skills
  (T170).
- Crafting and gathering (T147), checked against a live server (T147b).
- A new or deleted character's per-character state tables are purged (T172).

## Social

- Friends, groups, memos and blocks (T30); whisper and private channels (T43, T47).
- Parties (T35, T49, T64) and the party contract broker (T60). Matched parties are system
  parties (T163).
- Guilds: creation, members and the boot load (T39, T51, T52, T57, T69); perks, crests, board,
  search, ranking and flag (T83, T95); guild quests playable (T135); guild war (T170); quest
  points in the load burst (T172).
- Mail and the warehouse (T42, T45, T74, T79).

## Economy

- The trade broker: listings, two-step register / cancel / buy / collect, every tab (T53-T81),
  checked against a live server (T141). Fee is `TERASHARP_BROKER_FEE_PERCENT`.
- Daily purchase-limit reset, from the shop sheets (T172).

## Matchmaking and PvP

- Leaderboards from our own data (T118, T119, T126), checked against a live server (T133,
  T133b).
- Dungeon and battleground matching: composition and role caps (T138), Instance Matching lists
  from World (T157), the Vanguard Initiative window (T156), the first live queue (T161, T161b).
  `TERASHARP_MATCH_MIN_MEMBERS` is a test knob only (T138d).

## GM and administration

- `/@` commands (T32, T46, T47); GM invisibility as a toggle (T128); goto within one World
  (T172).
- The In-Game Operation Tool, Alt+A (T89, T91, T93, T99, T107, T124).
- The admin web and its JSON API: accounts, characters, bans, mutes, announces, grants,
  set-level, give-item, teleport, hard delete, the admin log (T101-T101d, T106); game-log search
  (T115, T116).

## Datasheets

- The server's datasheets are the source of truth, with built-in fallbacks (T159):
  `CreateCharData` start level (T162), battlegrounds (T163), guards and shop resets (T172).
- Level-70 start from data, `tools/level70-start.ps1` (T160, T162). Capture-only sheet tuning,
  `tools/capture-tune.ps1` (T149).

## Security and operations

- Packet bounds, pagination and a fuzz suite (T48, T50).
- Ticket auth against tera-api, fail closed (T31); `--selftest` (T37); `--check-config` (T113).
- Daily log file, console at Warning (T106); `docs/ARCHITECTURE.md` (T114); the go-live kit:
  firewall, backups, checklist.
- Open-source release: audit, strip and release gate (T139, T140).

## Tooling

- The Arbiter<->World tap with link ids (T56), both reframers, `make-tsis.ps1`, the `.npcap`
  converter (T130), partial continent loading (T63), `count-handlers.ps1`.
