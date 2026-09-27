# Setting up TeraSharp, end to end

> **If you just want it running, use [QUICKSTART.md](QUICKSTART.md): `tools\setup.ps1` does
> sections 2, 4 and 6 of this document for you, and writes `start.ps1`.** This page is the long
> version - what each step is for and what to do when it does not work.

From an empty folder to a character standing in Velika. Read `../README.md` first — in
particular "What you have to supply". If you do not already have a working 100.02 server,
nothing below will produce one.

Everything here is Windows. The Arbiter is .NET 8 and would run elsewhere; `WorldServer.exe`
would not, so there is no point pretending otherwise.

---

## 0. What has to exist before you start

| | Check |
|---|---|
| .NET 8 SDK | `dotnet --version` prints 8.x |
| PowerShell | 5.1 is enough; every script here targets it |
| `WorldServer.exe` and its `Executable\` tree | it starts and listens |
| The client definitions — `tera_v100_MASTER_FINAL\`, ~4550 `.def` files | the folder exists |
| An opcode map for protocol `376012` | `tera-server-proxy\data\data.json` has a `"376012"` key |
| A 100.02 client, and a proxy/command mod if you want chat commands | it launches |
| [`tera-api`](https://github.com/justkeepquiet/tera-api), if you want real accounts | its portal answers |

Put the first four under one root. That root is `TERASHARP_DATA`:

```
<TERASHARP_DATA>\
    Executable\              WorldServer.exe and friends
    Executable\Datasheet\    the client datasheets
    tera_v100_MASTER_FINAL\  the .def files
    tera-server-proxy\data\data.json
    WebApp\AppResource\ItemData\StrSheet_Item*.xml      (optional, admin-web item names)
```

Nothing in that tree comes from this repository and nothing in it should ever be committed
back into it — `.gitignore` already refuses the obvious paths.

---

## 1. Clone and build

```powershell
git clone <your fork or this repo> TeraSharp
cd TeraSharp
dotnet build TeraSharp.sln -nologo
```

A clean clone builds with no NuGet fetch beyond the SDK's own packages. The test project is a
plain console app on purpose, so there is no xUnit to restore.

```powershell
dotnet run --project src\TeraSharp.Arbiter.Tests
```

Expect a lot of `(skipped: data/... not found)`. That is correct: the byte-exact fixtures are
not in the repository and you have not generated any yet. The run must still end green.

## 2. Configure

Either one settings file, which is what `tools\setup.ps1` writes:

```powershell
.\tools\setup.ps1          # writes teras.json, with the two secrets generated
notepad teras.json
```

or the environment, which still overrides it:

```powershell
Copy-Item .env.example .env
notepad .env
```

Set at minimum `TERASHARP_DATA`, `TERASHARP_LOGS` and `TERASHARP_DB`. Leave `TERASHARP_BIND`
on `127.0.0.1` — the proxy is what faces the network, never the Arbiter.

`.env` is gitignored. Do not move a secret out of it into a tracked file, and do not put a real
one in `.env.example`.

Load it into the session before running from the command line:

```powershell
Get-Content .env | ForEach-Object {
    $t = $_.Trim()
    if ($t -and -not $t.StartsWith('#') -and $t.Contains('=')) {
        $i = $t.IndexOf('=')
        [Environment]::SetEnvironmentVariable($t.Substring(0,$i).Trim(), $t.Substring($i+1).Trim(), 'Process')
    }
}
```

`deploy.example.ps1` does the same thing on a real box; copy it to `deploy.ps1` and edit.

## 3. The runtime blobs

Four files in `data\` are required at runtime. The repository ships none of them, because each
one is bytes taken off a running server's wire:

```
data\starter_blob.bin          the template world blob for a new character
data\starter_inventory.bin     the starter kit
data\promotions_147E.bin       the promotion records
data\handshake_burst.bin       the 63 post-handshake config pushes
```

`data\README.md` has the format of each and how to cut them out of your own capture with
`tools\npcap-to-capture.ps1`, `tools\reframe-tap.ps1` and `tools\make-tsis.ps1`. Point
`TERASHARP_STARTER_BLOB` and `TERASHARP_STARTER_INVENTORY` elsewhere if you would rather keep
them outside the repo.

Until they exist, the Arbiter starts and existing characters work; **creating a character
fails**.

## 4. Check what the process actually resolved

```powershell
dotnet run --project src\TeraSharp.Arbiter -- --check-config
```

This prints the paths and ports as the process sees them, not as you meant them. Read every
line. Four of the settings are legal, silent and almost always wrong the first time —
`--check-config` flags those.

```powershell
dotnet run --project src\TeraSharp.Arbiter -- --selftest
```

`--selftest` opens every required file and folder, checks the opcode table parses and the
database schema migrates, and exits non-zero if a required dependency is missing. **Do not skip
this.** It is the difference between finding a bad path now and finding it when a player cannot
log in.

## 5. Start it

Order matters once, at the beginning:

1. `tera-api`, if you are using it
2. `WorldServer.exe` — give it its ~3 minutes
3. the Arbiter
4. the proxy
5. the client

```powershell
dotnet run --project src\TeraSharp.Arbiter
```

You want to see, in order: the opcode count, the definition count, the registered handler
count, then `World link` coming up. If World is not running yet the Arbiter waits and
reconnects — that is normal, and an Arbiter restart does not need a World restart.

Log in. If the client reaches the lobby but not the world, the enter-world path is where to
look; `status/ENTER-WORLD-FALLBACK.md` covers what the fallback does and why.

## 6. Ports

| Port | Who | Exposure |
|---|---|---|
| 7701 | Arbiter ← client (via the proxy) | **loopback** |
| 7801 / 7802 | World | as your setup requires |
| 8040 | tera-api gateway | loopback |
| 8050 | tera-api's own panel | loopback |
| 8051 | TeraSharp admin web | **loopback**, and only with `TERASHARP_ADMIN_TOKEN` set |

`tools\harden-netcup.ps1` replaces a hand-written port denylist with a default-deny inbound
policy. Read its help before running it on a remote box — default-deny closes RDP, and the
script refuses to run unless you tell it which address keeps it.

## 7. Before anyone else logs in

Work through `docs/GO-LIVE.md`. The short version:

- `TERASHARP_AUTH=true`, or every login is accepted
- `TERASHARP_GM_ACCOUNTS` takes **accountDBIDs** — numbers, not names
- `TERASHARP_ADMIN_TOKEN` — long, random, and not reused
- `TERASHARP_API_JWT_SECRET` — must equal tera-api's `API_PORTAL_SECRET`, or Alt+A breaks on
  every restart
- schedule `tools\backup-db.ps1`
- the Arbiter's phone-home inheritance: the retail ArbiterServer called out to a hardcoded
  address at startup. TeraSharp does not, but block outbound from the box anyway.

## 8. Contributing back

```powershell
.\tools\audit-release.ps1
```

Run it before every push to a public remote. It fails on retail blobs (by content hash, so a
rename does not help), captured ranking frames, credentials, public IP addresses and build
junk. Fix what it reports rather than working around it.

---

## When it does not work

| Symptom | Where to look |
|---|---|
| `Failed to load opcode table` | `TERASHARP_DATA`; `data.json` must have the `"376012"` key |
| `Failed to load definitions` | the `tera_v100_MASTER_FINAL\` path, and that it really holds `.def` files |
| Client reaches the lobby, never the world | `status/ENTER-WORLD-FALLBACK.md` |
| Character creation fails | the four runtime blobs, section 3 |
| Admin web does not answer | `TERASHARP_ADMIN_TOKEN` unset means it never starts |
| Alt+A rejects the token | `TERASHARP_API_JWT_SECRET` does not match tera-api |
| A packet the client ignores | `status/CLIENT-REJECTS.md` lists the ones World refuses and why |

`status/` has one note per subsystem, written while the thing was being built. It is the first
place to look for "why is it like this".
