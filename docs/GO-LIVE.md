# Going live with TeraSharp

The order matters. Everything above "Open the ports" is reversible; the firewall step
is where strangers can reach you.

Existing notes this builds on, all in `D:\v100\TERA_SERVER.100\`:
`!SECURITY_TODO_before_going_public.txt`, `!Lock_Services_To_Localhost.txt`,
`!Firewall_Lockdown.bat`. Read them; they cover the leaked stack. This file covers
**TeraSharp's** half and the things that only matter once both are running together.

---

## 0. The one thing to understand first

**The Arbiter does not check GM privilege on `C_ADMIN`.** The proxy on 7801 is the
only gate. TeraSharp changes nothing about that: port **7701 must never be reachable
from the internet**, and the firewall is what forces every player through the proxy.

If you take one item from this document, take that one.

---

## 0.5 Check what the process actually resolved

Before every other step, and again after the last one:

```
TeraSharp.Arbiter.exe --check-config
```

It prints every `TERASHARP_*` variable, the paths and ports the process resolved from
them, and a short notes block - then exits without touching the database or a socket.
The admin token is printed as `(set, 48 chars)`, never as itself.

Lines beginning `!` are the traps that are legal, silent and almost always wrong:

- `TERASHARP_GM_ACCOUNTS` holding a display name instead of an accountDBID
- auth OPEN
- a short or unset `TERASHARP_ADMIN_TOKEN`
- the admin web still on 8050, where tera-api's own panel lives
- `TERASHARP_BIND` off `127.0.0.1`

A clean config prints no `!` line. `--selftest` is the sibling: that one asks whether the
DEPLOY is complete, this one asks what the CONFIGURATION came out as.

- [ ] `--check-config` run on the server itself, and no `!` line left

---

## 1. Turn auth on

Off by default, which is right for a laptop and wrong for the internet.

```
setx /M TERASHARP_AUTH true
setx /M TERASHARP_AUTH_URL http://127.0.0.1:8080
```

With `TERASHARP_AUTH=true` the Arbiter validates the launcher's `authKey` against
tera-api's `/authApi/GameAuthenticationLogin`. Without it, `AcceptAllAuthProvider`
lets any name in.

- [ ] `TERASHARP_AUTH=true`
- [ ] tera-api is actually up on that URL (`arbiter_api` service, port 8080)
- [ ] log in with a real launcher account, then try a made-up one and confirm it is refused
- [ ] the refusal is visible in the log: `C_LOGIN_ARBITER: account ... rejected by ...`

---

## 2. GM accounts

Two routes, and **you probably want the second**.

```
setx /M TERASHARP_GM_ACCOUNTS 2800,2801
```

**The value is the numeric tera-api `accountDBID`, not a display name.** The launcher
puts the account id in `C_LOGIN_ARBITER.name`, so a username in that variable never
matches and the account silently gets a normal login. This is the single most common
way to set this up and see nothing happen.

The other route is `accounts.admin_level` in `terasharp.db`, which
`set_admin_level` and the admin web's `POST /api/gm-level` both write, and which
survives without an environment variable.

- [ ] GM accounts listed by **id**, or given `admin_level >= 1`
- [ ] log in and press **Alt+A** - the In-Game Operation Tool should open
- [ ] a non-GM account logs in and Alt+A does nothing

If Alt+A does nothing for an account you believe is a GM, check in this order:
`S_LOGIN_ARBITER.status` should be 33 and not 31 (that is the client's switch), and
`C_REQUEST_SERVER_ADMINTOOL_AWESOMIUM_URL` must be answered - see
`status/GM-DESIGN.md`, T104 and T107.

---

## 3. Proxy command mod

The proxy is the GM gate, so its mods are security code, not conveniences.

- [ ] `tera-server-proxy\mods\command\index.js` is the **fixed** version: hooks
      filtered by direction (`incoming:true` for `S_*`, `incoming:false` for `C_*`)
      and access reset on every `C_LOGIN_ARBITER`. The original trusted any packet
      carrying the `S_LOGIN_ARBITER` opcode, so a client-side proxy could forge one
      and unlock `C_ADMIN`.
- [ ] `mods\spoof-guard\` is present and loading - it drops client packets that carry
      a server opcode. Watch for `[spoof-guard] DROPPED` with an account id; repeats
      from one account are someone probing, and that account should be banned.
- [ ] `mods\exploit-fix\` is present. Run `build-area-allowlist.js` once so the
      `C_VISIT_NEW_SECTION` check uses real AreaData instead of sanity bounds only.
- [ ] the proxy actually starts, and a client connecting to **7801** reaches the game

Repeated `[exploit-fix]` lines from one account: same conclusion, ban it.

---

## 4. Admin web

TeraSharp's admin web binds `127.0.0.1` only and refuses to start without a token.

```
setx /M TERASHARP_ADMIN_TOKEN <48+ random characters>
setx /M TERASHARP_ADMIN_PORT 8051
```

**Change the port.** The default is 8050, and `!SECURITY_TODO` lists 8050 as
tera-api's own admin panel (`imsadmin`). Two services cannot both have it, and the
one that loses is whichever starts second.

- [ ] `TERASHARP_ADMIN_TOKEN` set to something you did not invent by hand
- [ ] `TERASHARP_ADMIN_PORT` moved off 8050
- [ ] `http://127.0.0.1:8051/` opens **on the server**, prompts for the token, and
      the Status tab fills in
- [ ] from outside, that port is filtered (step 7 does this)

### The Alt+A panel (T124)

The In-Game Operation Tool is an embedded web view. It needs two fields in
`S_LOGIN_ACCOUNT_INFO` - the second frame of every session - and a GM account:

```
setx /M TERASHARP_API_GATEWAY      127.0.0.1:8040
setx /M TERASHARP_DB_SERVER_NAME   PlanetDB_2800
setx /M TERASHARP_API_JWT_SECRET   <tera-api .env API_PORTAL_SECRET, verbatim>
```

- [ ] the port is tera-api's `API_GATEWAY_LISTEN_PORT`, **not** the arbiter API on 8080 that
      `TERASHARP_AUTH` uses - check your `.env` and mirror it here
- [ ] tera-api's **gateway_api** component is actually running (`start_gateway_api.bat`)
- [ ] the account is a GM (`TERASHARP_GM_ACCOUNTS`, or `accounts.admin_level >= 1`), which is
      what makes `S_LOGIN_ARBITER.status` 33 - both halves are needed
- [ ] `--check-config` shows `TERASHARP_API_JWT_SECRET  (set, N chars)` and no `!` line

Nothing on this stack verifies the token today (tera-api has no `jwt.verify`), so the panel
opens even with the secret unset - it is signed properly so that enabling verification later
is a config change. Leaving it unset is a one-line note in `--check-config`, not a failure.

Reach it from your desk over an SSH/RDP tunnel, never by opening the port.

---

## 5. Logging and the low-memory profile

The box is a VPS. The defaults assume a workstation.

```
setx /M TERASHARP_LOG_LEVEL Warning
setx /M TERASHARP_LOGS D:\packetlogs
```

Item names in the admin web come from the client's own strsheet, loaded lazily on the
first lookup. Both files under `WebApp\AppResource\ItemData\` are read and merged -
they cover different id ranges and neither is a superset - so nothing needs generating:

```
setx /M TERASHARP_ITEM_STRSHEET D:\v100\TERA_SERVER.100\WebApp\AppResource\ItemData\StrSheet_Item.xml
```

Only needed if `TERASHARP_DATA` does not point at the folder holding `WebApp\`. Roughly
9 MB of strings once something opens an inventory page; a server whose admin web is never
opened never loads it.

Console is `Warning` by default since T106; the daily file under `TERASHARP_LOGS`
still gets everything from Debug. Set `Information` only while chasing something,
and put it back.

The memory is mostly not TeraSharp's:

- [ ] **SQL Server max server memory** - the single biggest win. Unset, it takes
      nearly all RAM and then fights World for it. On a 8 GB box start at 2048 MB:
      `sp_configure 'max server memory', 2048; RECONFIGURE;`
- [ ] **MySQL** `innodb_buffer_pool_size` - the Laragon default is generous for this
      workload. 256 MB is plenty for `teraapi`, `box2db`, `steer3db`.
- [ ] **tera-api (node)** - one process per service; do not run the four services you
      are not using.
- [ ] **Rotate `D:\packetlogs`** - the tap logs are hundreds of MB each. Keep the
      `arbiter-*.log` files, delete the `cap_*` captures once a pass is committed.
- [ ] **Turn the packet tap off in production.** It writes every frame to disk.
- [ ] Leave the Arbiter's own GC alone. It is the small one here.

Sanity check after an hour with players on: the Status tab's *working set* and
*managed* tiles should be flat, not climbing.

---

## 6. Backups

```powershell
.\tools\backup-db.ps1 -Register -At 04:30 -OffsiteDir \\nas\tera -KeepDays 30
```

- [ ] `sqlite3.exe` on PATH, so the snapshot uses `VACUUM INTO` and is safe to take
      while the Arbiter is running. Without it the script falls back to a file copy,
      which is only trustworthy with the Arbiter stopped - TeraSharp uses SQLite's
      default rollback journal, not WAL.
- [ ] `-OffsiteDir` set to something that is not this disk
- [ ] run it once by hand and read the output
- [ ] **do the restore drill**: expand the zip, stop the Arbiter, copy
      `terasharp.db` over the live one, start it, log a character in. A backup you
      have never restored is a hypothesis.
- [ ] add `-MySqlDump` and `-SqlServer` if you are still running the leaked stack's
      databases - `!SECURITY_TODO` has had that item open since 2026-09-11

---

## 7. Open the ports

Everything above should be done first.

```powershell
.\tools\harden-netcup.ps1 -AdminIp <your.ip> -WhatIf     # look first
.\tools\harden-netcup.ps1 -AdminIp <your.ip>
```

This replaces `!Firewall_Lockdown.bat`'s denylist with a **default-deny** inbound
policy: 81 and 7801 are allowed, RDP is allowed from your address, and anything else
is closed because it was never opened. The .bat blocks 26 named ports, which is
correct until something starts listening on a 27th.

Loopback is never filtered, so the proxy still reaches `127.0.0.1:7701`, World still
reaches `127.0.0.1:7802`, and the admin web stays private.

The .bat's own rules are left in place: its `TERA-BLOCK-*` entries are redundant once
the default is Block, and its allow rules for 81/7801 say the same thing. Nothing
breaks either way; delete them by hand if you want a tidy rule list.

- [ ] `-AdminIp` is your real address. Default-deny closes RDP; the script refuses to
      run without `-AdminIp` or `-AllowRdpFromAnywhere` for exactly this reason.
- [ ] if your netcup product has a provider-side firewall as well, mirror the rules there
- [ ] **verify from a machine that is not the server:**
      `nmap -Pn -p 81,3389,7701,7801,7802,8051,1433,3306 YOUR_PUBLIC_IP`
      81 and 7801 open; 3389 open only from your address; everything else filtered.
- [ ] **if 7701 answers, stop and fix it before you give anyone the address**

Undo: `.\tools\harden-netcup.ps1 -Rollback`

---

## 8. Restarting World

World is the process that dies. Keep a one-key restart rather than a procedure.

`tools\restart-world.ps1` is not in the repo because the paths depend on where the
folder lives; this is the whole thing:

```powershell
$root = 'D:\v100\TERA_SERVER.100'
Get-Process WorldServer -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 3
Start-Process -FilePath (Join-Path $root 'Executable\WorldServer.exe') `
              -WorkingDirectory (Join-Path $root 'Executable')
```

- [ ] the numbered `.lnk` shortcuts hardcode `D:\TERA_SERVER.100\...` and **do not
      work** from `D:\v100\...`. Use `!TERA_Launcher.bat`, which resolves paths from
      wherever the folder actually is, or move the folder to `D:\TERA_SERVER.100`.
- [ ] TeraSharp survives a World restart: the bridge listens on 7802 and World
      reconnects. Players in world are dropped and can log back in.
- [ ] the Status tab's *world links* tile goes to 0 and back to 1 - that is the
      quickest way to confirm a restart actually took

---

## 9. First hour with players

- [ ] watch `arbiter-<date>.log` for `WARN` and `ERROR`, not for traffic
- [ ] `[spoof-guard] DROPPED` or repeated `[exploit-fix]` from one account: ban it
- [ ] Status tab: working set flat, world links 1, online count sane
- [ ] one real player logs in, makes a character, enters world, relogs
- [ ] the admin log (`GET /api/admin-log`) shows your own actions with reasons

---

## Still open, deliberately

These are known and unfixed, not oversights:

- ~~**Play time** reads 0~~ - **fixed (T113)**: stamped at leave-world into
  `characters.play_seconds` and `accounts.play_time_sec`. It cannot appear in the lobby
  though: `S_GET_USER_LIST.18.def` has no play-time field.
- ~~**Item names** show template ids~~ - **fixed (T113)**: read from the client's own
  `StrSheet_Item*.xml`. See `status/WEBADMIN-DESIGN.md` 11.1.1.
- **`S_ADMIN_GM_SKILL` and `S_ADMIN_HOLD_CHARACTER` are pushed to every player**, not
  just GMs (`HandlerRegistry.cs`, T89). The real server sends neither to a non-GM.
  It is not what opens Alt+A, so it is not a privilege hole - but it should be gated.
- **`S_CONFIRM_INVITE_CODE_BUTTON`** sends `UtcNow` where the real server sends a
  future expiry, so the countdown reads as expired. `status/CLIENT-REJECTS.md`, T106.
- **The SQL Server `sa` and MySQL `root` passwords** are still the defaults from
  `!SECURITY_TODO`. If you are running the leaked stack's databases at all, that item
  outranks everything in this file.
