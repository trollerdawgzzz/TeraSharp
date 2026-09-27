# Quickstart

Five steps from an unzipped TERA 100.02 server to players logging in. Longer version with the
reasoning: [SETUP.md](SETUP.md). Before anyone but you logs in: [GO-LIVE.md](GO-LIVE.md).

You must already have a working 100.02 server tree. Nothing here produces one.

```
<your server>\
    Executable\              WorldServer.exe, TopographyServer.exe, DeploymentConfig.xml, Datasheet\
    Topology\                the map data
    tera_v100_MASTER_FINAL\  the client .def files
    tera-server-proxy\       with data\data.json carrying protocol 376012
```

### 1. Drop TeraSharp next to the server and build it

```powershell
git clone <this repo> TeraSharp
cd TeraSharp
dotnet build TeraSharp.sln -nologo
```

### 2. Run setup once

```powershell
.\tools\setup.ps1
```

It asks four things - the server folder, the public IP or hostname players connect to, the planet
id (2800), and the server name - then does the rest: writes `teras.json`, generates the admin token
and the Alt+A signing key, makes `DeploymentConfig.xml` agree with it (ports, `ExternalIp`,
`<Topography folderName>`), points the proxy at the Arbiter, and leaves `start.ps1` and `stop.ps1`
behind. Run it again any time; it keeps your answers and your secrets, and `-Check` shows what it
would change without touching anything.

### 3. Supply the four runtime blobs

```
data\starter_blob.bin   data\starter_inventory.bin   data\promotions_147E.bin   data\handshake_burst.bin
```

They are bytes off a running server's wire, so this repository ships none of them.
`data\README.md` has the format of each and how to cut it out of your own capture. Until they
exist the server runs and existing characters work, but **creating a character fails**.

### 4. Check what the process actually resolved

```powershell
dotnet run --project src\TeraSharp.Arbiter -- --check-config
dotnet run --project src\TeraSharp.Arbiter -- --selftest
```

`--check-config` prints every setting, where it came from (`teras.json`, `env` or `default`), which
datasheets loaded from your tree rather than a built-in copy, and the handful of settings that are
legal, silent and almost always wrong. `--selftest` opens every required file and exits non-zero if
one is missing. Do not skip it.

### 5. Start

```powershell
.\start.ps1
```

Topography (`--sharedmemoryproducer=true`) -> TeraSharp -> World -> the proxy, each one waited for
before the next. Players connect to the host and port it prints. `.\stop.ps1` announces, kicks so
rows are written, and stops in reverse.

---

## Settings

One file, `teras.json`, beside the executable or at the repository root. `teras.example.json` is
the annotated copy; every key is also a `TERASHARP_*` environment variable and **the environment
always wins**, so you can override one setting for one run without editing anything. The full
table with defaults is [ARCHITECTURE.md](ARCHITECTURE.md) section 5.

`teras.json` holds the admin token and the Alt+A key. It is gitignored. Never commit it.

## When it does not work

| Symptom | Where to look |
|---|---|
| `setup.ps1` reports FAIL | the path it names; nothing else is checked until the tree is right |
| `Failed to load opcode table` | `paths.data`; `tera-server-proxy\data\data.json` needs the `"376012"` key |
| `Failed to load definitions` | the `tera_v100_MASTER_FINAL\` path, and that it holds `.def` files |
| World exits at once | the id has no `<WorldServer>` row in `ServerConfig.xml` - `setup.ps1 -Check` says so |
| Client reaches the lobby, never the world | `status/ENTER-WORLD-FALLBACK.md` |
| Character creation fails | the four blobs, step 3 |
| Admin web does not answer | `admin.token` unset means it never starts |
| Alt+A rejects the token | `gateway.jwtSecret` must equal tera-api's `API_PORTAL_SECRET` |
| No `/@` commands in chat | the proxy's `command` mod is not installed |
