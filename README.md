# TeraSharp

A from-scratch C# (.NET 8) replacement for TERA 100.02's `ArbiterServer.exe`, written to talk
to the real, unmodified `WorldServer.exe`.

TeraSharp is **only the Arbiter**. It is not a TERA server, not an emulator of the game
simulation, and not a redistribution of anything from TERA. It speaks two protocols — the
client's and the World server's — and it replaces exactly one process in a working 100.02
server stack. Everything else in that stack you already have to own.

---

## What it replaces

In a stock 100.02 deployment the Arbiter is the process that sits between the game client and
`WorldServer.exe`. It owns everything the World simulation deliberately does not: accounts and
the character list, persistence, the lobby, friends and blocklists, guilds, parties and
matchmaking, chat routing, mail and the warehouse, the broker, achievements and quest
bookkeeping, leaderboards, GM commands, and the DB-proxy conversation World uses to read and
write all of it.

TeraSharp reimplements that process:

| | |
|---|---|
| Client listener | port 7701, TERA's own framing and crypto |
| World link | the `[u32 len][u16 opcode][payload]` Arbiter↔World tunnel |
| Persistence | SQLite, schema created and migrated on start |
| Admin | a loopback web panel and a JSON API, both token-gated |
| Auth | accept-all by default; optional ticket validation against `tera-api` |

`docs/ARCHITECTURE.md` is the one-page map: processes, ports, file layout, every environment
variable, and where each game system lives.

## What it does not do

The World simulation — movement, combat, skills, NPCs, zones, loot — is `WorldServer.exe`'s job
and stays that way. TeraSharp does not touch it, patch it or emulate it.

---

## What you have to supply

Nothing in the list below ships with this repository, and none of it can be obtained from it.
If you do not already have a working 100.02 server, TeraSharp will not give you one.

| You provide | Why | Where it goes |
|---|---|---|
| `WorldServer.exe` and its `Executable\` tree, from your own 100.02 files | the other half of the server | wherever you already run it |
| The client datasheets from the same build | item names, create-character data | `<TERASHARP_DATA>\Executable\Datasheet` |
| The client packet definitions (`.def`, ~4550 of them) | the client codec | `<TERASHARP_DATA>\tera_v100_MASTER_FINAL\` |
| An opcode map for protocol `376012` | client opcode ↔ name | `<TERASHARP_DATA>\tera-server-proxy\data\data.json` |
| [`tera-api`](https://github.com/justkeepquiet/tera-api) | accounts, the launcher portal, the Alt+A panel | its own service, reached over HTTP |
| A TERA proxy / command mod | client-side chat commands | client side |
| Your own capture of your own server, if you want the byte-exact tests to run | test fixtures | `data/` — see `data/README.md` |

The first four come out of files you must already legally possess. They are not linked here,
not mirrored here, and not requested from anyone here.

### Captured traffic and the fixtures

This repository contains **no captured packet bytes**. The byte-exact tests are still in the
suite; they load their fixtures from `data/` and print `(skipped: … not found)` and pass when
the folder is empty, which is what a fresh clone looks like. `data/README.md` explains how to
regenerate every fixture from a capture of your own server with `tools/npcap-to-capture.ps1`,
`tools/reframe-tap.ps1` and `tools/reframe-client.ps1`.

Four of those files (`starter_blob.bin`, `starter_inventory.bin`, `promotions_147E.bin`,
`handshake_burst.bin`) are **runtime** data, not just fixtures — a new character cannot be
created without them. `data/README.md` says how to produce each one. `--selftest` tells you
which are missing before the first login does.

---

## Getting it running

`docs/SETUP.md`, end to end, from a clone to a character in the world. In short:

```
git clone <this repo>
cd TeraSharp
copy .env.example .env          # then edit every value in it
dotnet build TeraSharp.sln
dotnet run --project src\TeraSharp.Arbiter -- --check-config
dotnet run --project src\TeraSharp.Arbiter -- --selftest
dotnet run --project src\TeraSharp.Arbiter
```

`--check-config` prints what the process actually resolved. `--selftest` verifies every file,
folder and schema dependency and exits non-zero if a required one is missing. Run both before
you let anyone log in.

`docs/GO-LIVE.md` covers the things that matter once it is public: turning auth on, GM
accounts, the admin panel, logging, backups and the firewall.

`docs/OPERATIONS.md` is the runbook for after that - the 30-minute backup task and the restore
drill, announce/kick/stop and the boot order, log rotation and what the Dashboard tiles mean,
ship-then-deploy, and which changes need a World restart rather than an Arbiter one.

## Tests

```
dotnet run --project src\TeraSharp.Arbiter.Tests
```

A self-contained console runner — no xUnit, no NuGet. It exits non-zero on any failure, so it
doubles as the CI gate. Tests whose fixtures are absent skip and say so.

## Layout

```
src/TeraSharp.Arbiter/        the server
src/TeraSharp.Arbiter.Tests/  the test runner
src/TeraSharp.TestClient/     a minimal protocol client for poking at it
docs/                         ARCHITECTURE, SETUP, GO-LIVE, OPERATIONS
status/                       design notes, one per subsystem
tools/                        capture conversion, backups, firewall, release audit
data/                         fixture + runtime blobs you generate yourself (empty on clone)
```

`status/` is the working record: what each packet does, which offsets were confirmed against
what, and what is deliberately still unimplemented. It is verbose on purpose.

## Contributing

Two rules carry most of the weight:

1. **Evidence order.** When notes and the binary disagree, the binary wins. When the binary and
   a capture disagree, the capture wins. An assertion with no evidence behind it is a guess and
   gets written down as one.
2. **No retail bytes in the repo.** No captured packets, no datasheets, no decompiled source, no
   game assets. Facts derived from them are fine — offsets, field orders, opcode numbers. The
   material itself is not. `tools/audit-release.ps1` enforces this; run it before you open a PR.

## Licence

MIT — see `LICENSE`, including the paragraph on what the licence does and does not cover.
