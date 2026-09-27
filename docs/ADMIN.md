# The admin web tool

A single-page app served by the Arbiter itself, on loopback, over the JSON API in
`src/TeraSharp.Arbiter/Web/AdminApi.cs`. It replaces the retail GM tool (`WebApp`) for the
operations a private server actually needs.

## Getting in

| | |
|---|---|
| URL | `http://127.0.0.1:<port>/` — `admin.port` in `teras.json`, **8051** as `setup.ps1` writes it (`AdminServer.DefaultPort` is 8050, which collides with tera-api's own panel, so the file value is the one that matters) |
| Token | `admin.token` in `teras.json` (`TERASHARP_ADMIN_TOKEN` overrides). Unset means the tool does not start at all — an admin surface that defaults to open is the one mistake that cannot be walked back |
| Binding | `127.0.0.1` only, never `+` or `*`, and every request re-checks that the peer is loopback. Reach it from another machine with an SSH tunnel, not by changing the prefix |
| Sign-in | paste the token on the landing page. It is kept in `sessionStorage`, so it dies with the browser tab; a 401 signs you out |
| Audit | every write is recorded twice — in `admin_log` (the tool's own feed, on the Server screen) and in `game_log` under the category `admin`, keyed on the account and character it touched, so a player's own history search shows what an operator did to them |

The page itself is served without a token: a browser typing the address has nowhere to put a
header, and the shell carries no data. Everything under `/api/` is gated.

**If the form says the token was refused but `Invoke-RestMethod` with the same token works**, the
DLL and the embedded UI are out of step - rebuild, and hard-reload the page. That was T206b's
symptom for a different reason (the UI polled before sign-in and read its own empty-token 401 as a
refusal, fixed in `app.js`), and `tools/admin-ui-probe.mjs` is the check: it drives the shipped
sign-in path against a running server and names the step that fails.

## The seven screens

### 1. Dashboard
World links and whether the link is ready, online player count and the list itself (character,
account, level, zone — each linking into Accounts or Characters), process uptime, database size
and schema version, the matchmaking pool with each entry's wait time, live party count, and the
process figures (managed bytes, working set, GC counts, threads, log file, console level).

Retail equivalent: `Server/ServerMonitor.aspx` + `ChannelMonitor.aspx`. Retail reported a
concurrent-user *number* per server and had no uptime figure and no list of who is on; both are
additions here.

### 2. Accounts
Search by account id or name. Each account shows its GM level, play time, character count and
last login; its characters; its restrictions (ban and mute, with the reason and the expiry); and
its login history — each character's `last_login`/`last_logout` pair plus the `game_log` login
rows this build started writing.

Actions: set GM level; per character kick, ban / unban, mute / unmute. Ban and mute take hours
from one shared input; **0 means permanent**. Retail asked for an absolute start/end datetime
pair instead, and for a mandatory reason — the reason is mandatory here too (the prompt every
write shows), the duration is relative.

Retail equivalent: `Account/Default.aspx`, `Users/GMInfo.aspx`, `Log/LogAccount.aspx`,
`Users/UserRestrict.aspx`, `Restrict/Default.aspx`.

### 3. Characters
Search by name, plus a recently-deleted list with a Restore button for a pending delete. A
character opens as tabs over one fetch:

| Tab | What is there |
|---|---|
| Overview | level, money, zone, exp; position (zone/x/y/z/world/guard/section); quest and achievement progress; guild (links to Guilds); pending delete time |
| Inventory | the bag with template, StrSheet name, count and item id, each row removable; then each warehouse tab with its own money and slot count |
| Skills | the reset button. There is no per-skill list: skills live inside `characters.world_blob`, not in a table |
| Achievements | the count, each completed achievement decoded from World's 24-byte record (id, server-unique flag, completion date), and the server firsts this character holds |
| EP | the EP row, and a form to set level and points |
| Restrictions | the same rows the Accounts screen shows, for this character alone |

Actions: set level (1–70), set money, set position, rename, give item, reset character, delete
character. Give item and mail attachments share an **item picker** — type part of a name or an
id, search, choose from the results — because the StrSheet is loaded for inventory names anyway
and retail made the operator know the template id by heart.

Retail equivalent: `Users/Default.aspx` and the subset of its 34 detail tabs this tool covers,
plus the right-hand operator-command menu. **Not carried over:** the item option block (enchant
step, cumulated enchant, random passives, unbind count, period, masterpiece, awaken step) — a
give-item here is a plain template and amount.

### 4. Mail
Send system mail to one character, to everyone online, or to every character (capped at
`AdminApi.BulkMailMaxReceivers`). Sender defaults to `GM`; a title is required; money and up to
five attachments are optional. Each parcel goes out as parcel type 102 — the system parcel, so
there is no sent-box entry and no reply.

View a character's parcels: unread and read-unclaimed counts, then the inbox and the sent box
with every attachment spelled out (retail's grid showed one item name per parcel). Inbox rows can
be deleted, which takes the attachments with them.

Retail equivalent: `Users/ATO_SendNormalParcel.aspx`, `Users/ParcelInfo.aspx`, and the broadcast
forms `WorldFestival/TeraTimeRegister` (everyone online) and `MailEventRegister`. Retail's form
capped attachments at 4; this server's protocol has five slots, so the cap here is 5.

### 5. Guilds
Search by id or name (substring, unlike retail's exact match). A guild shows its level, money,
chief, announce, title, promotion, join level range; its full roster with contribution figures
and the chief marker; its open wars and its war history.

Actions: set money, set level (0–20), disband. War `state` and `result` are shown as the raw
integers — retail's 19 result names have not been transcribed, and a guessed label is worse than
a number.

Retail equivalent: `Guild/Default.aspx`, `Users/GuildInfo.aspx`, `Guild/GuildWarManagement.aspx`.
**Not carried over:** change announce, change chief, banish one member, guild-war cost settings.

### 6. Server
| Section | What it does |
|---|---|
| Announce now | one message to everyone in world |
| Scheduled announcements | add one with a start, an end and a repeat interval; the list, with delete |
| Restart notice | a countdown. One announcement now and one at each of 30/15/10/5/3/1 minutes that still falls inside the window. **It schedules warnings; it does not stop the server** — an admin page should not be able to |
| Datasheets | re-run the loader and report every sheet: whether it came from a file, how many entries, which consumer. A sheet that will not parse keeps its built-in and says so |
| Server-first achievements | the claim table, clear one or clear all |
| Game log search | T115's log: who (account or character, name or id), category, action, a date range, paging |
| Admin audit log | the tool's own feed — when, action, target, reason, source IP, result |
| Live log tail | the last N lines of the arbiter log from the in-memory ring, with a 3-second auto-refresh |

Retail equivalent: `Announce/IngameAnnounce.aspx`, `Log/default.aspx`,
`Log/SearchFromAuditLog.aspx`, `Server/AchievementSeason.aspx`. The restart notice, the datasheet
reload and the live tail have no retail screen — they were console operations.

### 7. Settings
Read-only. The effective configuration, `teras.json` and environment together, each setting with
where its value came from; the startup report; the datasheet directory; the item-name sheet and
how many names loaded; the embedded UI's file count and build fingerprint; and every database
table with its row count.

**Secrets are masked by the server, not by the page.** Anything whose name contains `TOKEN`,
`SECRET`, `PASSWORD`, `PASSWD`, `_KEY` or `APIKEY` is reported as `set, N characters` and its
value never leaves the process — so a screenshot of this screen is safe to attach to a bug
report. A new setting that is named like a credential is masked by default rather than by someone
remembering to add it to a list.

Retail had no Settings group at all.

## How it is built

No framework, no bundler, no build step. `Web/wwwroot/` holds `index.html`, `app.css`, `app.js`
and one ES module per screen; `TeraSharp.Arbiter.csproj` embeds them with the logical name
`admin/<file>`, and `Web/AdminAssets.cs` serves them for any GET outside `/api/`. So the UI ships
inside `TeraSharp.Arbiter.dll` — there is nothing extra to copy and `deploy.ps1` is unchanged —
while still being ordinary editable files in the repo.

The files are **flat on purpose**: a `views/` subdirectory would embed as `admin/views\x.js` on
Windows and `admin/views/x.js` on Linux, and the same build would serve different URLs on the two
machines.

`app.js` owns the token, the `fetch` wrapper, the DOM helpers and the hash router. Every mutating
call goes through its `write()` helper, which prompts for the audit reason and posts it, so no
screen can skip the audit trail. Assets are served with `Cache-Control: no-store` and an ETag:
after an upgrade the operator reloads and gets the new modules, rather than a cached one calling
an endpoint that moved.

## What is deliberately not here

Retail's GM tool has 222 screens. Its biggest workflows that this tool does not attempt:

- **Two-tier permissions and an approval queue.** Retail's `NavigateMenu.xml` carries 977
  permission tokens — 495 direct-execute and 482 `*_request_only` — so a junior GM's action
  becomes a Task someone else confirms (`Task/ViewTask.aspx`). Here the token is all-or-nothing.
- **Item and money restoration** (`Restore/`, `Item/SearchDeletedItem.aspx`) and **seizure** as a
  state distinct from deletion (`SEIZURE_ITEM` / `RETURNSEIZURE_ITEM`).
- **Case notes and ticketing** (`UserNote/`, `Account/AccountMemo.aspx`, `Users/UserMemo.aspx`).
- **Event authoring and feature kill-switches** — `WorldFestival/` (35 screens),
  `EventSystem/` (8) and `ContentsControl/` (25), which is where the per-feature on/off toggles
  live.
- **Analytics** — `Report/` and `Statistics/`, 38 screens.
- `POST /api/warn` and `POST /api/teleport` exist but their delegates are never wired in
  `Program.cs`, so they answer 404 and 501; no screen offers them.
