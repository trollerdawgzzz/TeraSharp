# Running a live TeraSharp server

The day-to-day runbook: what you run, in what order, and what the numbers mean.

One-time hardening - turning auth on, GM accounts, the firewall, the low-memory profile - is
[GO-LIVE.md](GO-LIVE.md) and is not repeated here. This document is what you do *after* that, every
day and every deploy. Where a section overlaps, GO-LIVE is the checklist and this is the procedure.

---

## 1. Backups

`terasharp.db` is the only copy of every account, character, item, guild and mailbox. Nothing else
in the tree can recreate it.

### 1.1 The 30-minute snapshot

`tools\backup-db.ps1` takes the snapshot; two scheduled tasks use it for two different jobs. The
frequent one has no `-OffsiteDir` - it is there so that a rollback costs at most half an hour.

```powershell
$args = '-NoProfile -ExecutionPolicy Bypass -File "C:\TeraSharp\tools\backup-db.ps1" ' +
        '-Destination "D:\backups\terasharp" -KeepDays 7'
$act = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument $args
$trg = New-ScheduledTaskTrigger -Once -At (Get-Date).Date -RepetitionInterval (New-TimeSpan -Minutes 30)
$set = New-ScheduledTaskSettingsSet -StartWhenAvailable -MultipleInstances IgnoreNew `
         -ExecutionTimeLimit (New-TimeSpan -Minutes 20)
$prn = New-ScheduledTaskPrincipal -UserId 'SYSTEM' -LogonType ServiceAccount -RunLevel Highest
Register-ScheduledTask -TaskName 'TeraSharp snapshot (30 min)' -Action $act -Trigger $trg `
         -Settings $set -Principal $prn -Force
```

- The script's own `-Register` makes a **daily** task, which is the nightly job below - it cannot
  make this one, which is why the trigger is spelled out here.
- `-MultipleInstances IgnoreNew` matters: a snapshot that runs long must not stack.
- On older PowerShell the repetition may not stick without an explicit duration. Check
  `(Get-ScheduledTask 'TeraSharp snapshot (30 min)').Triggers.Repetition` and, if it is empty, add
  `-RepetitionDuration (New-TimeSpan -Days 3650)` to the trigger.
- **`sqlite3.exe` on PATH is not optional here.** With it the snapshot is `VACUUM INTO`, which is
  safe on a live database and is verified with `PRAGMA integrity_check`. Without it the script falls
  back to a file copy, and TeraSharp uses SQLite's default rollback journal, not WAL - a copy taken
  while the Arbiter is writing can be a torn page. Every 30 minutes is exactly when you are copying
  a live database.
- `-KeepDays 7` prunes `terasharp-*.zip` older than seven days by last-write time. At 48 a day that
  is ~336 archives; budget disk for 336 x the compressed size of your database.

### 1.2 The nightly off-box copy

The archive the script makes holds the database (plus the MySQL/SQL Server dumps if you ask for
them) and **nothing else**. Datasheets and config are not in it, so the nightly job is two steps.

```powershell
# 1. database, off the box, from the script's own scheduler
.\tools\backup-db.ps1 -Register -At 04:30 -OffsiteDir \\nas\tera -KeepDays 7
```

```powershell
# 2. datasheets + config, off the box, nightly - a second task running this
$off  = '\\nas\tera\config'
robocopy 'D:\v100\TERA_SERVER.100\Executable\Datasheet' "$off\Datasheet" /MIR /R:1 /W:1 /NFL /NDL
foreach ($f in 'C:\TeraSharp\teras.json', 'C:\TeraSharp\.env',
               'D:\v100\TERA_SERVER.100\Executable\ServerConfig.xml',
               'D:\v100\TERA_SERVER.100\Executable\DeploymentConfig.xml') {
    if (Test-Path $f) { Copy-Item $f $off -Force }
}
```

- Adjust the paths to yours; `TERASHARP_DATA` is the root the Datasheet folder sits under, and
  `--check-config` prints the one the process actually resolved.
- `teras.json` and `.env` hold the admin token, the Alt+A key and your database passwords. The
  off-box copy needs the same access control as the box.
- A backup that only exists on the disk it protects is not a backup. If `-OffsiteDir` is unset the
  script says so on every run - do not learn to ignore that line.

### 1.3 Before every deploy

`deploy.ps1` already does it: its order is **env -> stop -> backup -> install -> verify -> start**,
and nothing is installed before the snapshot exists. The backup sits after the stop deliberately -
with the Arbiter stopped, even the `sqlite3.exe`-less fallback is trustworthy. Moving it to the
literal first line would make it a hot copy, which is only safe with `sqlite3.exe` present.

What matters is the guarantee, and it holds as written: **a schema migration runs on the next start
and is not reversible without that snapshot.**

### 1.4 The restore drill

Do this once, now, on a quiet evening - not during an outage. It takes ten minutes.

1. Pick an archive: `Get-ChildItem D:\backups\terasharp\terasharp-*.zip | Sort LastWriteTime | Select -Last 1`
2. Expand it somewhere scratch: `Expand-Archive <zip> C:\restore-drill`
3. Check the copy before you trust it:
   `sqlite3 C:\restore-drill\terasharp.db "PRAGMA integrity_check;"` - it must print `ok`.
4. Announce and empty the server (section 2.1), then stop the Arbiter.
5. Move the live database aside - do not delete it:
   `Move-Item D:\packetlogs\terasharp.db D:\packetlogs\terasharp.db.before-restore`
6. Copy the restored file into place: `Copy-Item C:\restore-drill\terasharp.db D:\packetlogs\`
7. `TeraSharp.Arbiter.exe --selftest` - it opens the database and checks the schema. Non-zero means
   stop and think, not start anyway.
8. Start the Arbiter, start World, log a character in, open a bag and the mailbox.
9. Write down how long steps 4-8 took. That number is your real recovery time.

**What a restore costs you:** everything since that snapshot - up to 30 minutes of play, or a whole
day if you are restoring the off-box copy. Say so publicly when you do it; players who lose an
hour's progress and are told why are far less angry than players who are not.

A backup you have never restored is a hypothesis.

---

## 2. Restarts

### 2.1 Never with players in-world

The Arbiter writes a character's state when the player leaves cleanly. Killing the process with
players in the world loses whatever World had not yet sent - position, and anything since the last
periodic save. The sequence is **announce -> kick -> stop**, and `stop.ps1` does exactly that:
it announces, kicks so the rows are written, and stops in reverse boot order.

By hand, when you want the warnings spaced out (the admin web takes the token as `X-Admin-Token`):

```powershell
$h = @{ 'X-Admin-Token' = $env:TERASHARP_ADMIN_TOKEN }
$api = 'http://127.0.0.1:8051/api'
Invoke-RestMethod -Method Post "$api/announce" -Headers $h -ContentType 'application/json' `
    -Body '{"text":"Server restart in 10 minutes.","reason":"deploy"}'
# ... again at 2 minutes, then:
(Invoke-RestMethod "$api/online" -Headers $h) | ForEach-Object {
    Invoke-RestMethod -Method Post "$api/kick" -Headers $h -ContentType 'application/json' `
        -Body (@{ id = $_.id; reason = 'restart' } | ConvertTo-Json) }
```

Then confirm the Dashboard's **online** count is 0 before you stop anything.

### 2.2 The boot order

Start (`start.ps1`, each one waited for before the next):

```
Topography (--sharedmemoryproducer=true)  ->  TeraSharp Arbiter  ->  WorldServer  ->  the proxy
```

Stop is the reverse: proxy, World, Arbiter, Topography. Two things follow from the order:

- The Arbiter's bridge **listens** on 7802 and World connects to it, so the Arbiter must be up
  first. It does not matter how long World takes; a World that connects late is handled
  (`status/T208d-PARTY-REPLAY.md`).
- Topography is a shared-memory producer for World. Starting World without it, or restarting
  Topography under a running World, is not supported - take World with it.
- **tera-api is not in that chain, and does not need stopping.** It is a *client* of ours: the hub
  connection is inbound, and TeraSharp answers hub port 11001 in BoxAPI's place (section 6, and
  `status/T230-ITEM-CLAIM.md` 7.2). So an Arbiter restart drops that link and nothing on our side
  re-opens it - tera-api redials. If shop or box traffic is still dead a minute after a restart,
  restart **tera-api**, not the Arbiter: it is the side that owns the connection.

### 2.3 Restarting World alone

World is the process that dies. Restarting it does not need the Arbiter touched:

```powershell
Get-Process WorldServer -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 3
Start-Process -FilePath 'D:\v100\TERA_SERVER.100\Executable\WorldServer.exe' `
              -WorkingDirectory 'D:\v100\TERA_SERVER.100\Executable'
```

Players in the world are dropped and can log straight back in. Watch the Dashboard's **world
links** tile go to 0 and back - that is the quickest confirmation the restart took.

---

## 3. Monitoring

### 3.1 The log file

| | |
|---|---|
| Where | `<TERASHARP_LOGS>\arbiter-yyyy-MM-dd.log` (default `D:\packetlogs`) |
| Rolls | daily, on the first line written after midnight |
| Level | **always Debug in the file**, whatever the console is set to |
| Console | `Warning` by default; `TERASHARP_LOG_LEVEL` overrides the console only |
| Pruned | **never - the process does not delete old logs.** That is your job |

```powershell
# keep 30 days of arbiter logs; the cap_* captures are not needed once a pass is committed
Get-ChildItem D:\packetlogs -Filter 'arbiter-*.log' |
    Where-Object LastWriteTime -lt (Get-Date).AddDays(-30) | Remove-Item
```

A busy day's file is large because the file keeps Debug. Check free space on the log disk weekly;
running it out stops the Arbiter writing, and the tap captures (`cap_*.log`, hundreds of MB each)
are usually what actually fills it.

### 3.2 The Dashboard

`http://127.0.0.1:8051/` - paste `admin.token` from `teras.json`. `docs/ADMIN.md` describes all
seven screens; these are the tiles to look at daily:

| Tile | Healthy | Worth a look |
|---|---|---|
| world links | 1 per running World | 0 means no World is connected at all |
| world ready | `ready` | see below |
| online | matches who is actually playing | a count that never drops can mean sessions that did not clean up |
| uptime | climbing | a reset you did not do is a crash - read the log around it |
| working set / managed | flat after an hour with players | climbing steadily is a leak; note the figures and when |
| log file / console level | the file you expect, `Warning` | `Debug` or `Information` here means somebody left it on |

### 3.3 What "worldReady false" means

`worldReady` is one thing: **World 0 has not completed the startup handshake.** The Arbiter is up
and listening, but no World has registered and finished its configuration exchange, so it cannot
accept players - a client that reaches the lobby will sit on the loading screen.

It is normal for the seconds between starting the Arbiter and World finishing its handshake. If it
stays false:

1. Is `WorldServer.exe` running at all?
2. Is **world links** 0 as well? Then World never connected - check that its
   `DeploymentConfig.xml` `<ArbiterServer port>` is **7802** and that no packet tap is sitting on
   7812 in front of it.
3. Links but not ready means World connected and the handshake did not finish. The log's
   `World {id} handshake complete - READY for players` line is the one that should be there; what
   is logged instead of it is the fault.
4. World exiting at once usually means its id has no `<WorldServer>` row in `ServerConfig.xml`.
   `tools\setup.ps1 -Check` says so.

---

## 4. Updating

### 4.1 Ship, then deploy

```powershell
.\ship.ps1 "what changed"     # build + tests + commit + publish + 7z. Refuses on a red test.
.\deploy.ps1 -From \\build\share\TeraSharp -WhatIf   # read it once with -WhatIf
.\deploy.ps1 -From \\build\share\TeraSharp
```

`deploy.ps1` runs `--check-config` and `--selftest` and **refuses to start on a failure** - that is
the gate, and it is the reason to deploy with the script rather than by copying files. It ends by
running the Arbiter with its console tee'd to `<LogDir>\arbiter-<stamp>.log`, so the shell you
started it from is now the server's console; use a service wrapper or a detached window if you need
that shell back.

### 4.2 Arbiter-only, or World too?

| What changed | Restart |
|---|---|
| Anything under `src\TeraSharp.Arbiter` - handlers, packets, the store, the admin web | **Arbiter only** |
| `teras.json` / `.env` / a `TERASHARP_*` variable | **Arbiter only** (nothing re-reads them live) |
| A database schema change (a migration) | **Arbiter only** - it migrates on start, hence the snapshot first |
| A datasheet under `Executable\Datasheet` | **both** - World loads its copy at startup |
| `ServerConfig.xml`, `DeploymentConfig.xml`, ports, world ids | **both** |
| Topography's shared memory | **Topography and World**, in that order |

World is built to outlive the Arbiter: `WorldServer.exe` carries a matched pair of
`OnDisconnectArbiterServer(int)` / `OnConnectArbiterServer(int)` handlers on every one of its
managers, which is a process designed to clean up when the Arbiter goes and re-announce when it
comes back - not one designed to exit. `UserManager::OnDisconnectArbiterServer` gathers the sessions
bound to that Arbiter, so **the players on it are dropped** even though World stays up.

**Verify it once on your box**, because the procedure depends on it and GO-LIVE section 9 states the
opposite: stop the Arbiter, and see whether `WorldServer.exe` is still in `Get-Process` thirty
seconds later, and whether **world links** returns to 1 by itself once the Arbiter is back. If it
does, an Arbiter-only deploy is one restart. If World exits, treat every deploy as both and say so
in GO-LIVE.

---

## 5. Three settings a public server must never leave on

Before those three, the two that `start.ps1` will not boot without (T215) - it exits 2 and starts
nothing, and `-Insecure` is the override:

| Setting | Production | What happens without it |
|---|---|---|
| `TERASHARP_AUTH` / `auth.enabled` | **true**, with `TERASHARP_AUTH_URL` pointing at tera-api | Every login is accepted whatever the name: `AcceptAllAuthProvider` does not ask anybody. This is the one that costs you the account table, which is why it is the first line of `--check-config` worth reading. |
| `TERASHARP_BIND` / `listener.bind` | **`127.0.0.1`** | The client port faces the network, and it does not check GM privilege on `C_ADMIN` - the proxy on 7801 is the only gate. A FAILED `--selftest` since T215, not a warning. |


| Setting | Production | What it does if left on |
|---|---|---|
| `TERASHARP_API_GATEWAY_SERVE` | **on, with a key** - or unset | Serves the Item Claim panel at `apiServerAddress` (section 6). It is allowed to bind beyond loopback (`TERASHARP_API_GATEWAY_BIND`), it answers **every path**, and it logs each request's full headers and cookies - so on a public box it is an open HTTP port that writes what it is sent into your log, and T132's probe page is still on `/probe`. Turn it on only with `TERASHARP_API_JWT_SECRET` set: without a key the panel refuses every request, which is safe but useless. Leave it unset and shop purchases still arrive, as mail. |
| `TERASHARP_LOG_LEVEL` | `Warning` | Anything lower puts a console line on every World frame, which is what made the console unusable before T106. It does not lose you anything to leave at `Warning`: the daily file keeps Debug regardless. Set `Information` to chase something, then put it back - the Dashboard shows the live value so you can catch yourself. |
| `TERASHARP_MATCH_MIN_MEMBERS` | **unset** | **Retired and ignored since T184h** - it no longer changes behaviour, and startup warns when it is still set. Clear it anyway: a setting that looks like it lowers the match floor and does not is how somebody ends up debugging the matcher for an hour. Dungeon capacity and roles come from `MatchingRoleTemplate.xml`, not from a variable. |

### 5.1 Four more, added since T230

| Setting | Default | What it is |
|---|---|---|
| `TERASHARP_SYNTH_ITEM_RECORDS` / `economy.synthItemRecords` | **true** (T209c part 2, live-verified) | Builds the starter kit's 536-byte item records instead of copying them out of `data\starter_inventory.bin`, so that file is not needed at all. Set `0`/`false` only to go back to copying a captured record you still have. |
| `TERASHARP_PARCEL_DELETE_ON_COLLECT` (T234) | **true** | Removes a SYSTEM reward parcel (ParcelType 102) once its attachments are claimed, instead of leaving it in the mailbox as "received" forever. `OURS:` retail keeps the row - `cap_final2b`'s receive-all shows the same two parcels before and after the collect - but a reward mail the player cannot clear is worse than the divergence. `0`/`false` for retail behaviour. |
| `TERASHARP_API_GATEWAY_SERVE` (T230) | unset | Serves the Item Claim panel at `apiServerAddress`, which on this deployment is **:8800** - the address the client's Alt+A view opens. Read the row in the table above before turning it on: it answers every path and logs what it is sent. |
| `ServerConfig.xml` `<DeleteUser expireHour1 expireHour2 deletionSectionClassifyLevel>` (T224) | the deployment's own values | The character-delete windows, and since T224 TeraSharp actually reads them: `expireHour2="0"` means *delete now* rather than parking the row for the hardcoded 72 hours. The three numbers are what `S_GET_USER_LIST` carries, so a client that shows the wrong countdown is usually this. |

Two more from the same table in GO-LIVE ("Production switches (T174)") that bite harder than the
third row above, and belong on the same pre-flight:

- **the packet tap** (`arbiter-world-tap.js`) not running, and `DeploymentConfig.xml`'s
  `<ArbiterServer port>` back to **7802**. The tap writes every World frame to disk.
- `TERASHARP_START_OVERRIDE` unset. It forces where every new character starts.

`--check-config` prints every setting and where it came from (`teras.json`, `env` or `default`).
Read it after a deploy; it is the only thing that tells you what the process actually resolved.

---

## 6. The Item Claim panel (T230)

Alt+A in the client opens an Awesomium web view at `apiServerAddress`. There is no item-claim
packet in 100.02 at all - zero `CLAIM` opcodes in all five protocol maps, zero `CLAIM`/`GIFT` defs
in 917 schemas (`status/T230-ITEM-CLAIM.md` section 1) - so that web view **is** retail's Item
Claim surface, and this is what serves it.

### 6.1 Turning it on

| Setting | Value | Why |
|---|---|---|
| `TERASHARP_API_GATEWAY_SERVE` / `gateway.serve` | `1` | Nothing listens without it and the panel stays blank. |
| `TERASHARP_API_JWT_SECRET` / `gateway.jwtSecret` | tera-api's `API_PORTAL_SECRET` | **Mandatory.** The panel refuses every request while this is unset. The ticket is the only thing separating one account's purchases from another's, so it fails closed, the way `TERASHARP_AUTH` does. |
| `TERASHARP_API_GATEWAY` / `gateway.address` | the address the client is told | The listener's port always comes from here; a listener on another port is not the thing the client opens. |
| `TERASHARP_API_GATEWAY_BIND` / `gateway.bind` | `0.0.0.0` when the client is on another machine | A `+` or `0.0.0.0` prefix needs an urlacl: `netsh http add urlacl url=http://+:8800/ user=%USERNAME%`. The bind failure is logged, not thrown. |
| `TERASHARP_ITEM_CLAIM_MAX_AGE` / `gateway.claimTokenMaxAge` | `3600` (default) | How old the Alt+A ticket may be, in seconds. The minted `exp` is 120 s and the ticket is minted at login, so no player opening a panel can beat it; the panel enforces `iat` + this instead and treats `exp` as advisory. Lower it if you prefer. |

Then `--check-config` prints all five and where each came from.

### 6.2 What the player sees, and where the items go

`GET /itemclaim` is the panel; it fetches `GET /itemclaim/list` and posts `POST
/itemclaim/claim?box=N`. Both endpoints are scoped to the `accountDbId` of the verified ticket, so
a box number belonging to someone else answers exactly like a box that does not exist.

A claim takes one of two paths, and the panel says which:

| the character is | the items go | the player is told |
|---|---|---|
| **not in world** | straight into the bag, at the lowest free slots | "Added to your inventory. It will be there next time you log in." |
| **in world** | into a system parcel in the in-game mailbox | "Sent to your in-game mailbox - open the mail window to collect it." |

The split is not a preference. World owns the bag from `SDB_USER_LOAD_INVENTORY` onward and
originates the insert atoms - the Arbiter only answers them - so rows written behind a live World
are lost on its next save. The mailbox is the one path that is both immediate and safe: the item is
created by World's own claim transaction.

A claim that cannot be honoured changes nothing. A full bag says so and the box stays claimable; a
box already claimed, already delivered, expired or not yet started is refused.

### 6.3 With the panel off

Nothing breaks. `BoxDelivery` still sweeps every pending box into a system parcel on
`BoxNotiUser` and at startup, which is the T207 path, and the buyer is told in chat. The two
outcomes are recorded as different states on purpose, so "where did my item go" has one answer:

| `hub_boxes.state` | meaning | the rest of the row |
|---|---|---|
| 0 | pending | nothing yet |
| 1 | swept into a parcel (T207) | `parcel_id` |
| 2 | claimed from the panel (T230) | `claimed_by`, `claimed_at`, and `parcel_id` when it went by mail |

**That row is the whole consumption record.** Nothing else on this stack tracks box state:
tera-api's box functions (`createBox` 107, `getServiceItem` 115/116, `SetDisableServiceItem` 118)
all address `gusid.boxapi`, and TeraSharp answers hub port 11001 in BoxAPI's place, so there is no
second party to report consumption to - and the hub connection is inbound only, so there is no call
we could originate if there were. See `status/T230-ITEM-CLAIM.md` section 7.2.

---

## 7. Sheets, the box, and the pre-restart check

### 7.1 Two copies, and which one is authoritative

The PC's `D:\v100\TERA_SERVER.100\Executable\Datasheet` is where sheets are edited. The box runs
its own copy. Everything in this section exists because those two drift.

### 7.2 Backup conventions

Every sheet tool takes one backup per **job**, never per run, so re-running is safe and the first
backup is always the untouched file:

| suffix | written by | revert |
|---|---|---|
| `.m1.bak`, `.m5.bak` | the milestone sheet passes, over the set their own doc names (M5's is `QuestData\*.quest`, `TaskDef.xml`, `QuestGroupList.xml`, `ItemTemplate*.xml`, `EquipmentTemplate.xml`, `CreateCharData.xml`, `ServerConfig.xml`, `DungeonMatching.xml`, `EventMatching.xml`, `BuyList.xml`, `ItemMedalExchange.xml`) | that pass's own `-Revert` |
| `.t220.bak`, `.t222.bak`, `.t236b.bak`, ... | the task tools - `unlock-classes.ps1`, `unlock-popori-male.ps1`, `patch_class_animationdata.py --job`, `fix-animset-paths.ps1` | `--revert --job <tag>`, or copy the `.bak` back |
| `.stock` | `tools\ue3\copy_classic_ui.ps1` (client tree only) | `-Revert` |
| `.orig` | hand copies | by hand |

**An existing backup is never overwritten.** If a second pass finds one it keeps it, because the
first one is the real pre-change file.

### 7.3 Copying to the box

- **Never hash-sync or mirror the whole folder over RDP.** The Datasheet is ~6,000 files and tens
  of GB with the `.quest` tree; a mirror also carries the `.bak` files over and can delete things
  the box needs. Copy a **named list**.
- The list is whatever the tool that made the change printed. Every sheet tool ends by naming
  exactly the files it touched - feed that to `push-sheets.ps1` rather than retyping it.
- World must be **stopped** for a Datasheet push: it reads the sheets at start-up and does not
  reload them.

### 7.4 The pre-restart validator

Two commands, in this order, before every restart that follows a sheet change:

```powershell
.\tools\fix-animset-paths.ps1 -Datasheet D:\v100\TERA_SERVER.100\Executable\Datasheet   # dry run
.\tools\check-class-rows.ps1  D:\v100\TERA_SERVER.100\Executable\Datasheet
```

`check-class-rows.ps1` exits with the problem count, so it gates a deploy. It checks, per unlocked
template, UserData / UserShape / UserBasicAction / `UserSkillData_*` / AnimationData coverage /
DefaultSkillSet / SkillGetConList / CreateCharData, plus duplicate ids across the three id sheets
(`status/T236-CHECK-CLASS-ROWS.md`). `fix-animset-paths.ps1` is what makes the animset column pass
(`status/T236b-ANIMSET-PATHS.md`, `status/T236c-SHARE-ANIMSETS.md`).

Run the checker again against the **box's** copy after the push. Same command, different folder.

### 7.5 `check-box.ps1` and `push-sheets.ps1`

```powershell
# 1. what drifted - copy the box's Datasheet to a local folder first
.\tools\check-box.ps1 -Box D:\packetlogs\box-datasheet -PushList drift.txt

# 2. push what the first step named, with a .bak per file
.\tools\push-sheets.ps1 -To D:\packetlogs\box-datasheet -ListFile drift.txt -Tag t236c -Apply
```

`check-box` compares size first and SHA-256 only when sizes match, excludes backups unless
`-IncludeBackups`, and exits with the number of files that differ or are missing on one side.
`push-sheets` also accepts `-Files a.xml,b.xml`; with `-ListFile` it scrapes sheet names out of
whatever text is in the file, so a saved `check-box` run or a pasted console log both work. A name
you **typed** that is not in `-From` stops the run; a name merely **scraped** is reported and
skipped. Dry run unless `-Apply`.
