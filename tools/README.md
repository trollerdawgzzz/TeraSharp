# tools/ — capture tooling

| | input | what it does |
|---|---|---|
| `reframe-tap.ps1` | `arbiter-world-tap.js` log | reassembles the TCP stream and cuts it into frames |
| `reframe-client.ps1` | packet-logger `capture_*.log` | validates each record and resolves names from `data.json` |
| `npcap-to-capture.ps1` | a Noctenium `.npcap` | unpacks it into the `capture_*.log` text the two reframers read |
| `trim-datasheets.ps1` | the server's `Datasheet\` folder | cuts it down to a keep-list of continent ids so the real servers fit in memory |
| `restore-datasheets.ps1` | a trim manifest | puts it all back |
| `capture-tune.ps1` | the server's `Executable\` folder | T149: temporary guild-war / guild-quest / Civil Unrest tweaks for a capture session, `-Revert` restores byte for byte (status/CAPTURE-T149-GUILD.md) |
| `level70-start.ps1` | the server's `Executable\` folder | T160: comments out ClassException's TeleportRestriction rows; `-StarterScroll` adds jump scroll 207631 to CreateCharData (ArbiterServer only); `-Revert` restores byte for byte (status/QUEST-DESIGN.md T160) |

## The two reframers

One per side of a capture session. Both take a log and write the same two
shapes, so the Arbiter<->World tap and the client packet log can be read side by side and
lined up by wall clock.

Outputs, for either script:

- **`<log>_ctl.txt`** — one line per frame, minus the noise, with the first 64 payload bytes.
  This is the file to read first; it is small enough to scan and big enough to find anything.
- **`<log>_frames.txt`** — the complete hex of every frame whose opcode you asked for.

```powershell
cd D:\v100\TERA_SERVER.100\TeraSharp\tools

.\reframe-tap.ps1    -Log <captures>\arb_world_2026-09-15T20-00-00-000Z.log `
                     -Opcodes 0x139E,0x139F,0x13BB,0x13F8

.\reframe-client.ps1 -Log <captures>\capture_2026-09-15T20-00-05-000Z.log `
                     -Packets S_GUILD_INFO,S_PARTY_MEMBER_LIST
```

Both take `-Ctl` / `-Frames` to place the outputs elsewhere, `-Bytes` to change how much
payload the listing shows, and `-Skip` to change what it leaves out. `-Verbose` reports how
many opcode names were loaded.

---

## The tap and its link ids

`D:\v100\TERA_SERVER.100\arbiter-world-tap.js` is the TCP proxy that sits between WorldServer
and the real ArbiterServer. It logs raw chunks:

```
[1] [W->A#1] 2026-09-13T11:42:21.682Z len=6
06 00 00 00 CF 27
```

**The `#1` is new (T56).** It is the World socket the chunk arrived on, counted from 1 in
connect order, and without it a frame cannot be attributed to a link — World opens about 25 of
them, so with two players in world there is no way to tell whose traffic is whose. That was
the blocker on `status/MULTIPLAYER-DESIGN.md` §8 Q2 and on the party half of
`status/CAPTURE-PLAN.md`. The console lines are prefixed `link #N:` to match.

Nothing else about the format changed, so a reframer that splits on whitespace still works.
One that matches the literal `[W->A]` does not: use

```
\[(W->A|A->W)(#\d+)?\]
```

Both scripts here accept either spelling. `reframe-tap.ps1 -Link 2` restricts a run to one
socket.

The tap lives outside the repo because it runs on the capture box next to `DeploymentConfig.xml`,
which has to point `<ArbiterServer port="7812"/>` at it. Copy it back to `D:\v100\TERA_SERVER.100\`
if it is ever lost; the version there is the one with link ids.

---

## Frame layouts, so the numbers below make sense

| | header | length field |
|---|---|---|
| Arbiter<->World | `[u32 totalLen][u16 opcode][payload]` | **includes** the 6 header bytes, so an empty frame is 6 |
| client<->Arbiter | `[u16 totalLen][u16 opcode][body]` | **includes** the 4 header bytes, and the header is inside the logged HEX |

A chunk is not a frame in either direction, but only the tap needs reassembly: the packet
logger has already split the client stream for you. The tap has not, and cannot — the 23
`0x147E` promotion records arrive as **one 30234-byte write**, and a long frame can equally
well be split across several chunks.

## How a frame is numbered

By the **chunk its first byte arrived in**, not by its position in the stream. So `355` in the
listing is the `[355]` you can search for in the raw log, and several frames can share a
number when they arrived together. This is what the original ad-hoc reframer did and it is
kept, because every existing note that cites a frame number cites one of these.

## What gets skipped

`-Skip` defaults to the ten opcodes that would otherwise drown the listing:

```
0x13F7 SA_BYPASS_TO_CLIENT   0x13F6 AS_BYPASS_TO_WORLD   0x13F2 dungeon timeline
0x13E5                       0x15A8                      0x138A SA_REGISTER
0x138B AS_REGISTER           0x2801                      0x147E promotion record
0x1436
```

On `arb_world_2026-09-13T11-33-30-680Z.log` that is 1845 frames of 2366 — the listing goes from
unreadable to 521 lines. The two existing listings in `<captures>` used slightly different
lists; `cap_newchar_ctl.txt` kept `0x147E` and dropped `0x13CC`, `0x164D` and `0x2958` instead.
Pass `-Skip` explicitly to reproduce that one exactly, or `-Skip @()` to keep everything.

## Verified against the existing listings

`reframe-tap.ps1` was run against the log `cap_relog9827_*.txt` was generated from:

| | original | regenerated |
|---|---|---|
| `cap_relog9827_ctl.txt` | 521 lines | 521 lines, **9 differ** |
| `cap_relog9827_frames.txt` | 58 frames | 58 frames, **byte-identical** |

All nine differences are the same thing, and the original is the one that is wrong. Its listing
printed a stray byte after every `len=     6`:

```
    1 W->A 11:42:21.682 0x27CF len=     6 27
                                          ^^ the opcode's high byte, not payload
```

`$frame[6..$end]` where `$end` is 5 does not produce an empty slice in PowerShell — a
**descending** range is walked backwards, so `6..5` means indices 6 then 5, and index 5 is the
high byte of the opcode. Every zero-payload frame in the old listing has that byte glued to it.
`reframe-tap.ps1` computes the count first and prints nothing. `cap_newchar_ctl.txt` has it too
(2 lines).

## Rejects

Neither script throws on a bad log; both count what they could not use and print it at the end.

- **tap**: a chunk whose hex byte count does not match its `len=`; a frame length outside
  6..1 MB (the stream has desynced — the buffer is dropped and framing restarts); bytes left
  over at the end of the log (the capture stops mid-frame).
- **client**: a record whose HEX is shorter than its `len=`; a packet under 4 bytes; a `len=`
  or opcode that disagrees with the header inside the HEX; a name that disagrees with the
  protocol map.

Feeding the wrong script a log produces a warning naming the other one, not a stack trace.

## Dry run over `<captures>`, 2026-09-15

Every existing capture, both scripts, PowerShell 7.4.

| log | frames / packets | rejects |
|---|---|---|
| `arb_world.log` | 950 chunks -> 1239 frames | none |
| `arb_world_2026-09-13T11-33-30-680Z.log` | 2043 chunks -> 2366 frames | none |
| `cap_newchar.log` | 4218 chunks -> 4561 frames | none |
| `lobby_tap.log` | 1499 chunks -> 1871 frames | none |
| `cap_newchar_client.log` | 3616 packets | none |
| `capture_2026-09-13T02-32-40-508Z.log` | 1710 packets | none |
| `capture_2026-09-13T11-42-27-513Z.log` | 1521 packets | none |
| `capture.log` | 928 packets | none |
| `full_capture.log` | 479 packets | none |
| `lobby_proxy.log` | 1205 packets | none |
| **`chat_capture.log`** | 557 packets | **131 truncated** |
| `ts-logout.log` | — | not a tap log |

Two things worth knowing:

- **`chat_capture.log` is truncated.** Every `HEX:` line stops at 200 bytes, so 131 of its 557
  packets are incomplete — `S_LOGIN` says 421 bytes and carries 200, `S_ITEMLIST` says 885 and
  carries 200. It is fine for *sequence* questions and useless for layout work. Whatever
  produced it was capped; the later captures are not.
- **`ts-logout.log` is a TeraSharp console log**, UTF-16 text, not a tap. The script says so
  instead of writing an empty listing.

Every opcode in every client capture resolves against `data.json` map `376012`, and no name
disagrees — so the captures and the map are from the same build.

## `-Names` on the tap side is advisory

`-Names <captures>\world_opcodes.txt` adds a name column. That table covers `AS_`/`SA_`
(0x1389-0x16xx) and **not** the `SDB_`/`DBS_` range above 0x2700, and it disagrees with the
repo in places — it calls `0x147D` `SA_LOAD_GUARD` where `DbProxyHandlers` calls it
`AS_PROMOTION_LIST_REQ`, and `0x1484` `AS_ELECTION_STATE`. Use it to skim, not to cite. The
authority is `World/DbProxyOpcodeNames.cs` and the opcode->name switch in `WorldServer.exe.c`
around line 247000.

## Two PowerShell traps both scripts had to work around

Recorded because they cost a run each and neither fails loudly:

**1. A `string[]` separator does not split.** The wrong `String.Split` overload binds and the
whole line comes back as one token, which then fails in `[Convert]::ToByte`. `[char[]]` is what
makes it split:

```powershell
$s.Split(@(' ', "`t"), $opt)          # one token - wrong overload
$s.Split([char[]]@(' ', "`t"), $opt)  # six tokens
```

**2. Inside a method call, the comma belongs to the method.** So the format operator is left with
one argument and throws "Index (zero based) must be greater than or equal to zero". Wrap the
format in its own parentheses:

```powershell
$w.WriteLine('{0} {1}' -f $a, $b)     # passes $b to WriteLine, not to -f
$w.WriteLine(('{0} {1}' -f $a, $b))   # correct
```

And one that is not PowerShell's fault: a parameter named `-Link` and a local `$link` are the
**same variable**, because names are case-insensitive. The local is `$linkId`.

Paths: every `[IO.*]` call here runs through `Resolve-Path` first, because `[IO.File]` and
`[IO.StreamReader]` resolve relative paths against the process working directory — usually
`system32`, never the shell's.

---

## `trim-datasheets.ps1` / `restore-datasheets.ps1` — partial continent loading (T63)

Boots the real `ArbiterServer.exe` + `WorldServer.exe` with only the continents you name, so the
pair fits beside MSSQL on the 32 GB capture box. The full reasoning, the complete list of sheets
that reference continent or dungeon ids, and the memory model are in
`status/WORLD-PARTIAL-LOAD.md`; this is the operating summary.

The mechanism is `loadAllContinents="true"` plus a trimmed `Datasheet\ContinentData.xml`. The
`<WorldServer loadAllContinents="false"><Continent id=.../></WorldServer>` list is **not** the
mechanism: that lookup only searches the *normal*-channelType bucket, so a dungeon continent such as
9827 is discarded from it silently.

```powershell
cd D:\v100\TERA_SERVER.100\TeraSharp\tools

# look first - changes nothing
.\trim-datasheets.ps1 -DatasheetRoot D:\v100\TERA_SERVER.100\Executable\Datasheet `
                      -KeepContinents 5,9827,9828,9829 `
                      -AsideRoot     D:\v100\TERA_SERVER.100\DatasheetAside `
                      -ServerConfig  D:\v100\TERA_SERVER.100\Executable\ServerConfig.xml -WhatIf

# a trimmed COPY - the original is never touched. Do this first.
.\trim-datasheets.ps1 -DatasheetRoot D:\v100\TERA_SERVER.100\Executable\Datasheet `
                      -KeepContinents 5,9827,9828,9829 `
                      -Destination   D:\v100\scratch\Datasheet `
                      -AsideRoot     D:\v100\scratch\aside

# in place, reversible
.\trim-datasheets.ps1 -DatasheetRoot D:\v100\TERA_SERVER.100\Executable\Datasheet `
                      -KeepContinents 5,9827,9828,9829 `
                      -AsideRoot     D:\v100\TERA_SERVER.100\DatasheetAside
.\restore-datasheets.ps1 -Manifest D:\v100\TERA_SERVER.100\DatasheetAside\trim-manifest.json
```

It does three kinds of thing, all recorded in `trim-manifest.json` and all reversible:

1. **row trims** in the seven sheets that resolve continent or dungeon ids at boot — `AreaList.xml`,
   `ContinentData.xml`, `DungeonConstraint.xml`, `DungeonMatching.xml`, `CompetitionDungeon.xml`,
   `Leaderboards.xml`, `EventMatching.xml`. Each is backed up before it is touched;
2. **file moves** for the per-continent families (`AreaData_<id>_*.xml`, `DungeonData_<id>.xml`,
   `ShieldTerritory_*.xml`, …) into an aside folder;
3. **ShieldTerritory stubs**, only for a kept continent that has no `<Zone>` of its own.

`-Aggressiveness Minimal` (default) moves only the families whose loader resolves `continentId`
fatally; `Standard` adds the self-filtering territory families; `Aggressive` adds hunting-zone
families, resolved through `ContinentData`'s `<HuntingZone>` children. `NpcData_*`, the skill sheets,
`S1ActionScripts_*` and `PegasusPath_*` are never moved at any tier.

Two guards worth knowing. **`-AsideRoot` must be outside the `Datasheet\` tree** —
`DataXmlManager::SearchFilesInDirectory` recurses into every subfolder under `rootFolder`, so an
aside folder inside it would still be loaded; the script refuses. And **`ContinentData` ids must stay
a subset of `AreaList` ids** — `LoadContinentData` requires a record that only `LoadAreaList`
creates, so a ContinentData row with no AreaList entry is a fatal PreLoad error. The script trims
both with the same keep-list and re-checks at the end.

### The AreaList trap

`AreaList.xml` holds **two** kinds of continent record: 252 top-level `<Continent>` and 18
`<ChannelContinent>` **nested inside them** (2000/2050/2052/2054 under continent 6; 7001-7005 under
1; 7011-7015 under 2; 7021-7023 under 3; 7031 under 4). A parse that only looks at root-level
elements reports those 18 as missing and makes the sheet look broken. It is not; the script reads
both forms.

### Dry run

Run in the container under PowerShell 7.4.6 against a reconstructed mirror of the real
`Datasheet\` tree (3,229 files: the real bytes of every sheet the trim edits, the real filenames of
the `Datasheet\` root, and synthesised names for the families the directory listing cap cut off).
Keep-list `5,9827,9828,9829`:

| tier | files left | moved aside |
|---|---|---|
| Minimal | 2,325 | 904 |
| Standard | 1,991 | 1,238 |
| Aggressive | 411 | 2,818 |

Rows removed, the same at every tier: 248 `<Continent>` from AreaList, 250 from ContinentData, 166
`<Constraint>`, 39 + 8 `<Dungeon>`, 7 `<ContentInfo>`, 154 `<Event>` + 6 `<EnableTime>` + 5 empty
`<EnableDay>`. `-WhatIf` changed 0 of 3,229 files; **trim -> restore was byte-identical at all three
tiers**. What that does not prove is that the result boots — only the real binaries can say that.

## `npcap-to-capture.ps1` — Noctenium `.npcap` in, `capture_*.log` out (T130)

Noctenium (the Classic+ launcher's proxy) writes its own captures to
`...\noctenium\logs\packet-captures\capture-<date>-<id>.npcap`. They are the only client-side
capture we get from a **live** server, so they are worth reading; the container is small and
was reversed in T130 from `classic_live.npcap` (3 318 599 B, 7 391 records, parses to the byte).

```
file header, 16 B
  [0]  char[4]  "NPCP"
  [4]  u32      version, 2
  [8]  u64      capture start, UNIX NANOSECONDS

record, 14 B header + payload
  [0]  u16      type
  [2]  u64      nanoseconds since the client started
  [10] u32      payload length
  [14] payload
```

`type` is both the direction and the view:

| type | direction | view |
|---|---|---|
| 0 | S→C | wire — one socket read, **zero or more whole frames back to back** |
| 1 | C→S | wire |
| 2 | S→C | packet — exactly one frame |
| 3 | C→S | packet |

Both views of the same session are in the same file. The u64s are what identify the layout
without guessing: they are monotonic across all 7 391 records, and the file header's
`1789912872310233200` ns is `2026-09-20 14:01:12 UTC`, which is the `-100112` in the file's own
name at UTC-4.

**The two views are not copies of each other.** Noctenium is a proxy, so a packet one of its
mods rewrites or injects reaches the packet view without ever being on the socket. In
`classic_live.npcap` they are byte-identical for the first 5 433 S→C frames and the first 27
C→S frames and then diverge; the packet view is the longer one (6 467 frames vs 6 441). The
script always walks **both** and prints where they part, because a silent difference between
them is the one thing that would make the capture lie to you.

```powershell
# the packet view (default) - one record per frame, and the complete list
.\npcap-to-capture.ps1 -Npcap <captures>\classic_live.npcap

# the socket view instead
.\npcap-to-capture.ps1 -Npcap <captures>\classic_live.npcap -Stream Raw -Out raw.log

# then read it like any other client capture
.\reframe-client.ps1 -Log <captures>\classic_live.log
```

Runs on **Windows PowerShell 5.1** as well as pwsh 7. Two things in the first cut did not:
`Measure-Object -Property { … }` (a calculated property, PS 6+ only) and a `List[object]`
assigned out of an `if` expression, which the pipeline unrolls — an empty one lands as `$null`
and `.Count` then throws under `Set-StrictMode`. Both are fixed and commented in place; the
second only ever showed on a capture with no records.

Opcode names come from `data.json` (`-Protocol`, default 376012), never from the file — an
`.npcap` stores no names. An opcode the map does not know is written `UNKNOWN_0xNNNN` so the
record still parses and `reframe-client.ps1` can rename it. On `classic_live.npcap` every one
of the 6 467 frames resolved and `reframe-client.ps1` reported **no rejects and no renames**,
which is also how we know the live Classic+ server speaks the same 376012 map this build does.

---

## `make-tsis.ps1` — frames in, a `data\*.bin` fixture out (T139)

The byte-exact tests read TSIS containers out of `data/`. None of them are in the repository
any more — each is generated from the operator's own capture. This is the last step of that:

```powershell
.\reframe-tap.ps1 -Log <tap log> -Opcodes 0x2890,0x2891,0x2909,0x2910
.\make-tsis.ps1   -Frames <tap log>_frames.txt -Out ..\data\cap_t26.bin -Seq 336,413,376,511,2068
```

It parses the `=== <seq> ... 0x<OP> ... len=<n>` / hex-line pairs a reframer writes, strips
`-HeaderBytes` off the front of each frame (**6** for a tap frame, **4** for a client packet,
**0** to keep it whole — `cap_client_settings.bin` is the one that keeps whole frames), and
writes `"TSIS"`, `u32 count`, then `u32 seq | u16 opcode | u32 len | payload` per record, all
little-endian.

`-Seq` throws rather than writing a short file when a frame number is not in the listing, so a
typo cannot quietly produce a fixture that half-tests something. Omit it to take every frame.

Verified by rebuilding an existing container from its own bytes: unpack `cap_t26.bin`, re-add
the 6-byte header to each of its 11 payloads, write a synthetic `_frames.txt`, run the script
over it — the output is byte-identical to `cap_t26.bin`.

### A third PowerShell trap

Two were already listed above. This one cost an hour:

- **`@($list)` on a `[System.Collections.Generic.List[T]]` throws "Argument types do not
  match".** The array subexpression accepts a pipeline, not a generic list. Use
  `$list.ToArray()`. `@($list | Where-Object { ... })` is fine, because that is a pipeline.
- **A local `$out` silently overwrites an `[string] $Out` parameter.** Variable names are
  case-insensitive, so every `$out.AddRange(...)` then fails on a string. The local here is
  called `$blob`.

---

## `audit-release.ps1` — the gate before a public push (T139)

```powershell
.\audit-release.ps1                        # walk the working tree
.\audit-release.ps1 -Tracked                # only what git tracks, for CI
.\audit-release.ps1 -AllowAddress 203.0.113.9
```

Exits non-zero on anything that must not be published:

| Severity | What | Blocking |
|---|---|---|
| `RETAIL` | a file whose SHA-256 is a known capture-derived blob (under any name), plus datasheets, decompiles, `.def`, `.npcap` | yes |
| `PII` | a captured ranking frame — a list of other people's character names | yes |
| `SECRET` | a credential assigned to a literal, a private key, `.env`, `deploy.ps1` | yes |
| `ADDRESS` | a routable public IPv4. Loopback, RFC1918, CGNAT and the RFC 5737 documentation ranges pass | yes |
| `JUNK` | `bin/`, `obj/`, databases, logs, archives, `.bak`/`.orig` | yes |
| `REVIEW` | a line of verbatim decompiler output — two or more Ghidra identifiers on one line. Citing an address in prose is fine and is not reported | no, unless `-Strict` |

The hash list is the point: renaming `starter_blob.bin` does not get it past the check. Add a
line when a new blob is identified; never remove one.
