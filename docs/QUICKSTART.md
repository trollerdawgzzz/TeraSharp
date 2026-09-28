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

### 3. Nothing to supply

There is no runtime blob left. All four files this step used to ask for are generated:

| File | Retired by | Built from |
|---|---|---|
| `starter_blob.bin` | T209 | `CreateCharData.xml`, `DefaultSkillSet.xml` and the create request |
| `promotions_147E.bin` | T209c | nothing - a server with no castles is supposed to send none |
| `handshake_burst.bin` | T209b | `Protocol/InterServerDefinitions`, every field named from the retail dump helpers |
| `starter_inventory.bin` | T209c part 2 | `CreateCharData.xml` plus built 536-byte item records |

Each is still an override: drop a capture of your own into `data\` and it wins, byte for byte.
`data\README.md` has the formats. Set `economy.synthItemRecords` to `false` in `teras.json` to go
back to copying item records out of a `starter_inventory.bin` you still have.

### 4. Check what the process actually resolved

```powershell
dotnet run --project src\TeraSharp.Arbiter -- --check-config
dotnet run --project src\TeraSharp.Arbiter -- --selftest
```

`--check-config` prints every setting, where it came from (`teras.json`, `env` or `default`), which
datasheets loaded from your tree rather than a built-in copy, and the handful of settings that are
legal, silent and almost always wrong. `--selftest` opens every required file and exits non-zero if
one is missing. Do not skip it.

The first line to read in either is **`TERASHARP_AUTH`**. `setup.ps1` writes `auth.enabled = true`
with an `auth.url`, and with those two off or empty every login is accepted whatever the name -
`start.ps1` will not boot without them. The second is `TERASHARP_BIND`: off `127.0.0.1` it is a
failed `--selftest`, because the client port does not check GM privilege on `C_ADMIN`.

### 5. Start

```powershell
.\start.ps1
```

Topography (`--sharedmemoryproducer=true`) -> TeraSharp -> World -> the proxy, each one waited for
before the next. Players connect to the host and port it prints. `.\stop.ps1` announces, kicks so
rows are written, and stops in reverse.

Once it is up and someone else is playing on it, [OPERATIONS.md](OPERATIONS.md) is the runbook:
backups and the restore drill, how to restart without losing a character, what the Dashboard tiles
and `worldReady false` mean, what needs a World restart and what does not, and the settings to clear
before anyone logs in. [GO-LIVE.md](GO-LIVE.md) is the one-time hardening that comes first.

---

## Enable the shop (optional)

The in-game shop button and the items a purchase delivers both go through
[tera-api](https://github.com/justkeepquiet/tera-api). TeraSharp answers tera-api's hub socket
itself, so there is no `arb_gw` to run:

1. In tera-api's `.env`, point the hub at TeraSharp and turn the shop on:

   ```
   HUB_HOST=127.0.0.1
   HUB_PORT=11001
   API_PORTAL_SHOP_ENABLE=true
   ```

   Nothing else in tera-api changes. If `arb_gw_tw2_log.exe` is running, stop it - it holds 11001.

2. In `teras.json`, leave `shop.hubListen` at `127.0.0.1:11001` and set `shop.url` only if your
   shop page is not tera-api's default (`http://<auth host>:81/tera/ShopMain`).

3. Restart TeraSharp. The log says `hub listening on 127.0.0.1:11001 as server 2800`.

A purchase then arrives as a system parcel in the buyer's mailbox, and elite (benefit 533) is
granted on the account while the player is online. Boxes bought before the account has a character
wait in `hub_boxes` and are delivered within a minute of the first character existing.
`status/T207-HUB.md` is the whole path, including why it is a parcel and not an Item Claim window.

---

## Settings

One file, `teras.json`, beside the executable or at the repository root. `teras.example.json` is
the annotated copy; every key is also a `TERASHARP_*` environment variable and **the environment
always wins**, so you can override one setting for one run without editing anything. The full
table with defaults is [ARCHITECTURE.md](ARCHITECTURE.md) section 5.

`teras.json` holds the admin token and the Alt+A key. It is gitignored. Never commit it.

The admin web tool is at `http://127.0.0.1:8051/` - paste `admin.token` from `teras.json` to sign
in. Seven screens: Dashboard, Accounts, Characters, Mail, Guilds, Server, Settings. `docs/ADMIN.md`
says what each one does and what it replaces in the retail GM tool.

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
