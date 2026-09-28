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
| `TERASHARP_API_GATEWAY_SERVE` | **unset** | Starts the T132 probe listener at `apiServerAddress`. Unlike the admin web it is allowed to bind beyond loopback (`TERASHARP_API_GATEWAY_BIND`), it answers **every path**, and it logs each request's full headers and cookies. It is a measuring instrument, not the Alt+A gate (T144b) - on a public box it is an open HTTP port that writes what it is sent into your log. |
| `TERASHARP_LOG_LEVEL` | `Warning` | Anything lower puts a console line on every World frame, which is what made the console unusable before T106. It does not lose you anything to leave at `Warning`: the daily file keeps Debug regardless. Set `Information` to chase something, then put it back - the Dashboard shows the live value so you can catch yourself. |
| `TERASHARP_MATCH_MIN_MEMBERS` | **unset** | **Retired and ignored since T184h** - it no longer changes behaviour, and startup warns when it is still set. Clear it anyway: a setting that looks like it lowers the match floor and does not is how somebody ends up debugging the matcher for an hour. Dungeon capacity and roles come from `MatchingRoleTemplate.xml`, not from a variable. |

Two more from the same table in GO-LIVE ("Production switches (T174)") that bite harder than the
third row above, and belong on the same pre-flight:

- **the packet tap** (`arbiter-world-tap.js`) not running, and `DeploymentConfig.xml`'s
  `<ArbiterServer port>` back to **7802**. The tap writes every World frame to disk.
- `TERASHARP_START_OVERRIDE` unset. It forces where every new character starts.

`--check-config` prints every setting and where it came from (`teras.json`, `env` or `default`).
Read it after a deploy; it is the only thing that tells you what the process actually resolved.
