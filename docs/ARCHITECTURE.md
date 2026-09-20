# TeraSharp — architecture on one page

What runs, where it listens, which file owns which system, and what has to be true on disk.
Written T114 from the tree at 2026-09-20. For *how well* each system works see
`status/STATUS.md`; for *why a packet is shaped the way it is* see that system's design doc.

TeraSharp is a from-scratch C# (.NET 8) reimplementation of TERA 100.02's `ArbiterServer.exe`.
It is the login/lobby/social/persistence half of the server. `WorldServer.exe` — the original
binary, unmodified — is still the game.

---

## 1. Processes and ports

```
  player
    |  encrypted client protocol
    v
  tera-server-proxy            :7801   public.  Node. Rewrites nothing on the happy path;
    |                                           it is also the ONLY gate in front of C_ADMIN.
    |  same protocol, loopback
    v
  TeraSharp.Arbiter            :7701   LOOPBACK ONLY, and this matters - see below
    ^                          :7802   WorldServer connects IN here (plaintext, 25 links)
    |                          :8051   admin web, loopback, TERASHARP_ADMIN_TOKEN required
    |
  WorldServer.exe                      27 GB RSS. Dies if the Arbiter link drops and does
                                       not reconnect - every deploy needs a World restart.

  tera-api (Node, separate stack)
       :81     launcher / account web, public
       :8050   its own admin panel (imsadmin) - which is why TeraSharp's admin web moves to 8051
       :8080   ARBITER API, loopback. POST /authApi/GameAuthenticationLogin is the ticket check.
  MSSQL 1433 / MySQL 3306               tera-api's, loopback.
```

**7701 must never be reachable from the internet.** The Arbiter does not check GM privilege on
`C_ADMIN`; the proxy on 7801 does. A player who can reach 7701 directly is a GM.
`tools/harden-netcup.ps1` makes inbound default-deny with 81 and 7801 the only holes.

The 25 World links are one control link plus 24 bypass links (`SA_REGISTER` 0x138A announces
which is which). Client packets ride the bypass links inside `SA_BYPASS_TO_CLIENT` (0x13F7) and
`AS_BYPASS_FROM_CLIENT` (0x13F6); everything else is a control message on the main link.

---

## 2. File map — `src/TeraSharp.Arbiter/`

| Folder | What lives there |
|---|---|
| `Network/` | Sockets, SHA-0 crypto, framing, the per-session leave state machine, and `PacketDispatcher` — client opcode to handler, with **unregistered packets forwarded to World** |
| `Protocol/` | The `.def`-driven client codec (`DefinitionParser/Reader/Writer`), the opcode map, item names |
| `Handlers/` | Everything that answers a **client** packet. `HandlerRegistry` is the one place registrations happen |
| `World/` | Everything that answers **WorldServer**. `WorldBridge` is the link and the tunnel; `DbProxyHandlers` is the 672-message DB proxy |
| `Persistence/` | `CharacterStore.cs` — the whole SQLite schema and every query, one file |
| `Web/` | The admin web: `AdminApi` (JSON), `AdminServer` (the single-file HTML page), `ArbiterLog` (daily file + ring buffer) |
| `Auth/` | Login ticket validation: accept-all by default, tera-api behind `TERASHARP_AUTH=true` |
| `Game/` | `FakeAccount.cs` — the session's view of an account, loaded from the store |

`Program.cs` wires it together: env, logging, opcode/def load, store, `WorldBridge`, `TcpServer`,
admin web. It is human-owned, like `WorldBridge`, `TunnelFrames`, `Network/*`, `WorldEntry`,
`HandlerRegistry` and `LoginHandlers` (`CLAUDE.md` section 0).

---

## 3. Where each game system lives

| System | File | Design doc |
|---|---|---|
| Login, character list / select / create / delete | `Handlers/LoginHandlers.cs`, `CharacterHandlers.cs` | `status/HANDSHAKE-DATA.md` |
| Entering the world, and the retry when the instance is dead | `Handlers/WorldEntry.cs` | `status/ENTER-WORLD-FALLBACK.md` |
| The world blob (15312 B of character state) and every per-login load | `World/DbProxyHandlers.cs` | `status/PERSISTENCE-MAP.md` |
| Inventory, the 536-byte item record, the atom ops | `World/BagItems.cs`, `Persistence/CharacterStore.cs` | `status/INVENTORY-DESIGN.md` |
| Starter kit and level-1 skills | `World/StarterInventory.cs`, `StarterBlob` | `status/SKILLS.md` |
| Chat: say / area / global / whisper / channels | `World/ChatManager.cs`, `Handlers/ChatHandlers.cs` | `status/CHAT-DESIGN.md` |
| Friends, blocks, groups, memos, profile | `Handlers/SocialHandlers.cs` | `status/FRIENDS.md` |
| Parties, and party matching | `World/PartyManager.cs` + `PartyWiring.cs`, `PartyMatchManager.cs` | `status/PARTY-DESIGN.md`, `PARTY-MATCH.md` |
| The party contract handshake | `World/ContractBroker.cs` | `status/CONTRACT-DESIGN.md` |
| Guilds, the guild board, guild war | `Handlers/GuildHandlers.cs` + `World/GuildWiring.cs`, `GuildWarManager.cs` | `status/GUILD-DESIGN.md`, `GUILD-WAR.md` |
| Mail and the warehouse | `Handlers/ParcelHandlers.cs`, `World/ParcelDbHandlers.cs`, `WarehouseHandlers.cs` | `status/MAIL-WAREHOUSE.md` |
| Trade broker | `World/BrokerPackets.cs`, `Handlers/BrokerHandlers.cs` | `status/BROKER-DESIGN.md` |
| Cards, crests, achievements, reputation, dungeon cool times | `World/DbProxyHandlers.cs`, `DbProxyStaticData.cs` | `status/ACHIEVEMENTS.md`, `REPUTATION-FATIGABILITY.md`, `DUNGEON-COOLTIME.md` |
| GM: `/@` commands, the In-Game Operation Tool (Alt+A) | `Handlers/GmCommands.cs` | `status/GM-DESIGN.md`, `GM-COMMANDS-*.md` |
| The small client packets World would otherwise reject (tooltips, acks, boards, events, reports) | `Handlers/ArbiterClientHandlers.cs` | `status/CLIENT-REJECTS.md` |
| Client settings, keybinds, UI blobs | `Handlers/ClientSettingsHandlers.cs` | `status/CLIENT-SETTINGS.md` |
| Multi-World routing (registration, instances, continent owners) | `World/WorldRegistration.cs`, `WorldInstances.cs`, `WorldServerList.cs` | `status/MULTIWORLD-DESIGN.md` |
| Admin web | `Web/AdminApi.cs`, `Web/AdminServer.cs` | `status/WEBADMIN-DESIGN.md` |

Anything not in that table is answered from `World/WorldReplayTable.cs` — a table of captured
World request/response pairs with the request id patched. That is what "replayed" means in
`status/STATUS.md`, and replacing replayed answers with real ones is most of the remaining work.

---

## 4. Data files

**Required to start** (`--selftest` checks every one of these and exits non-zero if a required
one is missing):

| Path | What it is |
|---|---|
| `tera-server-proxy\data\data.json` | the client opcode map, key `"376012"` |
| `tera_v100_MASTER_FINAL\` | 4548 `.def` files — the client packet codec |
| `D:\packetlogs\arb_world.log` | the captured World conversation `WorldReplayTable` is built from |
| `data\starter_blob.bin` (15312 B) | a new character's world blob, patched per character |
| `data\starter_inventory.bin` | the starter kit as a `0x27A4` payload |
| `data\promotions_147E.bin`, `data\handshake_burst.bin` | the login-time pushes, byte-exact |

**Optional, and worth setting.** `WebApp\AppResource\ItemData\StrSheet_Item.xml` **and**
`StrSheet_Item_NAEU.xml` give item names in the admin web. Both are read and merged: they are
disjoint (34,539 ids and 19,110 ids, none in both), so loading one labels roughly half the
catalogue and none of the items in the captures. Loading is lazy — a server whose admin page is
never opened never pays for it.

`data\*.bin` + `data\*.md` pairs are capture fixtures for byte-exact tests, not runtime data.
The SQLite database is `D:\packetlogs\terasharp.db`, created and migrated on start.

---

## 5. Environment

Every one of these is read by `Program.cs`, `SelfTest` or the subsystem named. Run
`TeraSharp.Arbiter.exe --check-config` to print what the process actually resolved, with the
warnings for the four settings that are legal, silent and almost always wrong.

| Variable | Default | Effect |
|---|---|---|
| `TERASHARP_DATA` | `D:\v100\TERA_SERVER.100` | root for defs, opcodes, strsheets |
| `TERASHARP_LOGS` | `D:\packetlogs` | capture input and the daily `arbiter-<date>.log` |
| `TERASHARP_DB` | `<logs>\terasharp.db` | the SQLite file |
| `TERASHARP_BIND` | `127.0.0.1` | client listener address. **Leave it on loopback** |
| `TERASHARP_AUTH` | unset (off) | `true` validates the login ticket against tera-api, fail closed |
| `TERASHARP_AUTH_URL` | `http://127.0.0.1:8080` | tera-api's ARBITER API root |
| `TERASHARP_GM_ACCOUNTS` | unset | comma-separated **accountDBIDs** — numbers, not display names |
| `TERASHARP_ADMIN_TOKEN` | unset (web off) | the only thing in front of the admin write endpoints |
| `TERASHARP_ADMIN_PORT` | `8050` | **move it to 8051**; 8050 is tera-api's own panel |
| `TERASHARP_LOG_LEVEL` | `Warning` | console only; the daily file always takes Debug |
| `TERASHARP_ITEM_STRSHEET` | unset | explicit path to a `StrSheet_Item*.xml` |
| `TERASHARP_ITEM_NAMES` | unset | explicit path to a two-column `templateId<TAB>name` file |
| `TERASHARP_DATASHEET` | unset | datasheet folder override (`--selftest`) |
| `TERASHARP_STARTER_BLOB` | `data\starter_blob.bin` | new-character blob |
| `TERASHARP_STARTER_INVENTORY` | `data\starter_inventory.bin` | starter kit payload |
| `TERASHARP_START_OVERRIDE` | unset | `"zone,x,y,z"` — forces where a new character starts. Experiment knob |

---

## 6. The loops

**Build / test / ship** (the human; Cowork cannot run git or dotnet):

```
dotnet build TeraSharp.sln                        0 warnings, 0 errors
dotnet run --project src\TeraSharp.Arbiter.Tests  one console runner, ~800 [Test] methods
.\ship.ps1 "message"                              build + tests + commit + self-contained publish
                                                  to D:\TeraSharp-publish + 7z
C:\deploy.ps1                                     on netcup. Runs --selftest first and aborts on
                                                  a required failure. RESTART WORLD AFTER, always.
```

**Cowork.** Two autonomous sessions run in parallel, each in its own git worktree on its own
branch — `TeraSharp-cowork` (`cowork/T8`) and `TeraSharp-cowork2` (`cowork/T27`). Cowork cannot
run git: it writes files, the human commits, merges to master and rebases the worktrees. The
editable/human-owned split is in `CLAUDE.md` section 0 and `status/STATUS.md` "Scope"; the short
version is that Cowork owns the handler and persistence bodies and never touches the socket,
registry or entry-point files, because those are where a wrong edit takes the link down.

**Where to look when something is wrong.** `status/CHAT-HANDOFF.md` has the live-debug recipes
(minidump stack walk without WinDbg, the World console log, the tap). `status/LIVE-CHECKLIST.md`
is what to click. `docs/GO-LIVE.md` is the ordered checklist for putting this in front of players.
