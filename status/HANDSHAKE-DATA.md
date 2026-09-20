# HANDSHAKE-DATA — where the hardcoded handshake lists actually come from

T29. Two lists that `TeraSharp` carries as capture-derived constants, plus the 63-push config
burst, traced to their sources. Both lists turned out to be **exactly reproducible from the
Datasheet XMLs**, and one of them turned out not to be an Arbiter-computed list at all.

Sources: the decompiled `ArbiterServer.exe`, `Executable\ServerConfig.xml`,
`Executable\Datasheet\*.xml`, and the four A↔W taps. `WorldServer.exe.c` (125 MB, one file)
could not be staged into the research container; the two claims that depend on it are marked.

---

## 0. The headline, and a behaviour correction

`DbProxyHandlers.PostHandshakeDungeonIds` is **not a list the Arbiter decides**. The real
Arbiter sends those 98 `0x1581` pushes as a **per-record echo** of a single
`0x13F2 DSA_DUNGEON_TIMELINE_OPEN_INFO` frame that World sends it moments earlier.

`lobby_tap.log`, consecutive frames:

```
[122] W->A 0x13F2 DSA_DUNGEON_TIMELINE_OPEN_INFO  len=2170   count=98
[123] A->W 0x1581 AS_DUNGEON_TIMELINE_ON_OFF      len=19     34 08 00 00 01 00 00 00 00 00 00 00 00
 …98 contiguous, nothing interleaved…
[124] W->A 0x1436 SA_BROADCAST_SYSTEM_MESSAGE_TO_WHOLE_WORLD
```

Verified mechanically against the capture: **the 98 reply ids equal the 98 request ids, in the
same order**, and every request record has `CurrOpen = 1`.

That is also why the burst is absent from two of the four captures:

| capture | big `0x13F2` | `0x1581` sent |
|---|---|---|
| `lobby_tap.log` (09-13 02:51) | 1 × 2170 B, count 98 | 98 |
| `cap_newchar.log` (09-13 05:49) | 1 × 2170 B, count 98 | 98 |
| `arb_world.log` (09-12 06:36) | **none** (174 × 14 B heartbeats only) | **0** |
| 09-13 11:42 relog | **none** (325 × 14 B heartbeats only) | **0** |

The 14-byte `0x13F2` frames are the same packet with an **empty** list —
`[u32 count=0][u32 offset=14]`. So `0x13F2` is not a heartbeat that happens to carry data; it
is the dungeon-open broadcast, usually empty.

**Login worked in both captures where the real Arbiter sent zero `0x1581`.** That is worth
knowing before anyone treats the 98-push burst as load-bearing — `DbProxyHandlers`' comment
says a first enter-world into a fresh World process is dropped without it, which was observed
live, but the real Arbiter demonstrably does not always send them. The two facts can coexist
(World may only need them when it *has* registered dungeons, which is also when it sends the
big `0x13F2`), and the safest reading is: **echo what World asks for, and keep the datasheet
list as the fallback for the case where World asks for nothing.**

### The payload shape was also slightly wrong

Writer `FUN_1407ace90` (`Arb_part_066.c:17289`): it opens the packet with opcode `0x1581`, then
appends its three arguments as a u32, a u8 and a u64, in that order.

Dumper field names (`Arb_part_011.c:10058`): `DungeonId` @frame+6, `IsOn` @frame+10,
`NextChange` @frame+0x0B. So the 13-byte payload is

```
[u32 DungeonId][u8 IsOn][u64 NextChange]
```

not `[u32 id][u32 1][u32 0][u8 0]` as `Build1581` writes. **The bytes are identical today**
(`IsOn = 1` lands in the low byte of the second u32 and everything else is zero) and the T23
tests still pass, but the moment `NextChange != 0` the current builder emits garbage. §5 has
the fix.

`DungeonManager::SetDungeonTimelineOpen(int,bool,__int64,bool)` = `FUN_140786fd0`
(`Arb_part_065.c:13433`) is what the `0x13F2` handler calls, once per record, and it broadcasts
to all 0x20 world-session slots (`Arb_part_065.c:13600`). The handler itself
(`Handler_DSA_DUNGEON_TIMELINE_OPEN_INFO`, `Arb_part_062.c:332`) is a pure fan-out:

It reads the record's fields straight out of the frame and hands them to the broadcaster
unchanged; the layout is below.

`DSA_DUNGEON_TIMELINE_OPEN_INFO` (0x13F2) layout, from the dumper at `Arb_part_016.c:2781`
(`L"OpenInfo"`, `L"DungeonId"`, `L"CurrOpen"`, `L"NextChange"`, `L"SendSystemMessage"`):

```
[u32 count][u32 offset=14]  then count × 0x16-byte nodes:
  [u32 self][u32 next][u32 DungeonId][u8 CurrOpen][i64 NextChange][u8 SendSystemMessage]
```

Node size confirmed by the handler's own guard `(longlong)iVar2 + 0x16U <= …`.

---

## 1. Where the Arbiter reads datasheets from

`ServerConfig.xml` carries a **name → filename-glob table** read at boot
(`Arb_part_077.c:3434`, which asks the config object for its `Datasheet` section):

```xml
<Datasheet rootFolder=".\Datasheet\" …>
  <DungeonTemplate     fileName="DungeonData_*.xml" />
  <DungeonConstraint   fileName="DungeonConstraint.xml" />
  <DungeonMatching     fileName="DungeonMatching.xml" />
  <ContinentData       fileName="ContinentData.xml" />
  <PoliticsTemplate    fileName="PoliticsData.xml" />
  …167 entries…
</Datasheet>
```

Only **8** `.xml` filenames are hardcoded in the whole binary (`ServerConfig.xml`,
`DeploymentConfig.xml`, `DefineDefine.xml`, `Version.xml`, `NetModeratorConfig.xml` and three
DB-definition files); everything else resolves through that table by logical name, e.g.
`Arb_part_085.c:989`, which resolves the logical name `PoliticsTemplate` through it.

**They are plain XML in `Executable\Datasheet\`. There is no packed or compiled form.**

Boot order (`Arb_part_033.c:13892`–`13926`):

| phase | function | loads |
|---|---|---|
| `Datasheets PreLoad...` | `DatasheetManager::PreLoad` `FUN_1409c6d80` (`Arb_part_085.c:14482`) | only `AreaList` + `ContinentData`, for `PlanetInfo` |
| `PlanetInfo PostProcessing...` | `PlanetInfo::PostProcess` `FUN_140131990` | `ServerConfig.xml` → `WorldServerList` |
| `Datasheets Load...` | `DatasheetManager::Load` `FUN_1409b1d00` (`Arb_part_085.c:738`) | four parallel `FutureJob`s, `_Load_Group1..4` (`Arb_part_086.c:5125/5680/5842/6015`) |
| `Datasheets Validating...` | `DatasheetManager::Validate` `FUN_1409d43f0` | |
| `Datasheets Post-Processing...` | `DatasheetManager::PostProcess` `FUN_1409c6c40` | |

Each sheet reports by name via `FUN_14002b970(L"<SheetName>", ok)` — an 80-entry ordered boot
manifest if anyone needs it later.

One sheet in the config does not exist on disk: `TBARotation → Rotation.xml`. Its load is a
no-op in this deployment (relevant to `0x29E2`, §4).

---

## 2. List (a) — the 98 dungeon ids

### The rule: four sheets

| role | sheet | element / attribute | n |
|---|---|---|---|
| the dungeon exists | `DungeonTemplate` = `DungeonData_<id>.xml` | the filename id | 224 |
| the continent exists | `ContinentData.xml` | `<Continent id="3016" channelType="dungeon" …>` | 254 |
| **not** served by the dedicated dungeon WorldServers | `DungeonMatching.xml` | `<Dungeon id="9087" … matchingRoleId="32">` | 39 |
| the content is switched on | `DungeonConstraint.xml` | `<Constraint isActive="true" … continentId="9827" …/>` | 137 |

```
(DungeonData ∩ ContinentData) − DungeonMatching   = 173   ==  SA_WORLD_SERVER_STATUS InstanceList
                                        ∩ active  =  98   ==  the 0x1581 ids
```

Both equalities hold **set-wise and in order** (see §3). The 173 intermediate is not a fitted
constant — it is independently observable: `SA_WORLD_SERVER_STATUS` (0x164D, 2790 B) decodes as
`[u32 count=173][u32 offset][u32 PlanetId=2800][u32 WorldId=0]` + 173 × 16-byte
`{InstanceId, InstanceCount}` nodes (dumper names at `Arb_part_017.c:2005`), and that list
matches `(DungeonData ∩ ContinentData) − DungeonMatching` exactly, in order.

`isActive` is parsed World-side in `DungeonBaseTemplate::ParseConstraint`
(`WorldServer.exe.c:266453`):

It reads the `isActive` attribute off the parsed node and stores it as the byte at
`DungeonBaseTemplate+0x95`.

and consumed by `DungeonOffManager::IsDisabled(int)` (`:3151768`) as
`(*(char *)(puVar3[5] + 0x95) != '\0')`.

The `DungeonMatching` exclusion is consistent with `ServerConfig.xml`:

```xml
<WorldServerList>
  <WorldServer id="0" loadAllContinents="true" />
  …
  <WorldServer id="12" type="dungeon"> <Continent id="9920"/><Continent id="3023"/><Continent id="3027"/><Continent id="3126"/><Continent id="3026"/> </WorldServer>
  <WorldServer id="13" type="dungeon" />
</WorldServerList>
```

— all five of world 12's continents are inside the 39, and the capture is world 0.

**Comments matter.** `DungeonConstraint.xml` contains 210 `<Constraint` tokens, 41 of them
inside `<!-- … -->`. Parsing without stripping comments produces the wrong set.

**Order** is ascending numeric, which is `std::map<int,…>` iteration order in World's
`UpdateDungeonOpenInfo`. The captured 98 are already sorted.

---

## 3. List (b) — the `0x1559` reply

`0x1558 = SA_LOAD_POLITICS_UNIT`, `0x1559 = AS_POLITICS_UNIT_INFO`. Present in all four
captures at the same position (`arb_world.log` chunks 6→7, 76-byte frame).

Writer `World::SendPoliticsUnit(class Session *)` (`Arb_part_074.c:4349`) emits an
offset/length pair then a raw `int[]`:

It opens the packet with opcode `0x1559`, reserves the `dataOffset` and `byteLength` u32 slots
by writing zero into each, backpatches the offset once the data begins, appends the ints one at
a time, and finally backpatches the length as the element count times four.

⇒ `[u32 dataOffset=14][u32 byteLength=68][int[17]]`. Decoded from `lobby_tap.log` seq 7:

```
off=14  byteLen=68  ids=[2, 3, 4, 5, 6, 7, 11, 12, 13, 14, 15, 18, 19, 20, 21, 22, 23]
```

Source: `PolicyDataSheet::GetPoliticsUnitIdList` (`Arb_part_008.c:13003`) walking the
`std::map` at `PolicyDataSheet+0x50` — hence ascending order — filled by
`PolicyDataSheet::Load(bool)` (`Arb_part_009.c:1702`) from sheet `PoliticsTemplate`
(`:1713`) = `PoliticsData.xml`, node `PoliticsUnitData` (`:2005`), attribute `politicsUnitId`
(`:2021`):

```xml
<PoliticsUnitData>
	<PoliticsUnit politicsUnitId="2" guardNumber="2" />
	<PoliticsUnit politicsUnitId="3" guardNumber="3" />
	…17 entries…
```

### Reproduction

Run against `D:\v100\TERA_SERVER.100\Executable\Datasheet` (224 `DungeonData_*.xml` +
`ContinentData.xml` + `DungeonMatching.xml` + `DungeonConstraint.xml` + `PoliticsData.xml`):

```
sheets=224 continents=254 matching=39 active=137 worldInstances=173
(a) produced 98, captured 98
(a) captured list already ascending: True
(a) ORDER identical: True   SET identical: True   extra [] missing []
(b) produced 17: [2, 3, 4, 5, 6, 7, 11, 12, 13, 14, 15, 18, 19, 20, 21, 22, 23]
```

**Both lists: identical, including order. Zero delta in either direction.**

---

## 4. The 63-push burst: datasheet or SQL?

Classification of the config burst in `data/handshake_burst.md`. **DS** datasheet · **SQL**
PlanetDB / web-admin state · **CALC** computed at send time · **EXT** external service.

43 of the 50 distinct opcodes carry an **empty list** in the capture — the two encodings are
`[u32 count=0][u32 offset=0]` and `[u32 off=14][u32 len=0]` — i.e. a virgin admin database.

| class | opcodes | notes |
|---|---|---|
| **CALC** | `0x15BD` (wall clock), `0x156B` (calendar day index), `0x13C7` (`DungeonManager::IssueDungeonUniqueId` counter, 0 at boot), `0x150A` (15 × `1.0f` identity multipliers), `0x14D1` (SQL list + a computed reset instant) | the only genuinely live fields; T23 already patches `0x15BD` and `0x14D1` |
| **DS** | `0x149D` / `0x149E` / `0x149F` × 5 festivals | `FestivalSeasonManager::LoadManager` → `FestivalSeasonDataSheet::GetFestivalSeasonTemplateMap` → **`FestivalSeason.xml`**. The five ids 1..5 are that sheet's rows; runtime start/stop state overlays from `LoadFestival` / `DBUpdateFestivalInfo` |
| **EXT** | `0x28E3` / `0x28E5` / `0x28E6` / `0x28EB` | in-game-shop begin/end markers; the catalogue comes from the AGW/API server at `<APIServer ip="127.0.0.1" port="8800">` in `DeploymentConfig.xml`. The burst carries only the four markers because the cache was empty |
| **SQL** | everything else — `0x157E 0x15DE 0x1582 0x157F 0x14B3 0x150D 0x150E 0x1510 0x1511 0x14D0 0x14E1 0x14EC 0x1529 0x28F8 0x1556 0x14E6 0x1623 0x15B6 0x15C2 0x15C5 0x15D4 0x15D5 0x1603 0x160D 0x160F 0x1609 0x14E9 0x14E2 0x14EE 0x14F2 0x14F3 0x14F7 0x1613 0x161D 0x1589 0x162E 0x1567 0x29E2` | each has a `*Manager::OnConnectWorldServer` that pushes a cache loaded by a `DBHelper` call at boot |
| **CFG** | *(none)* | no burst push is read from a config file |

The three non-empty SQL pushes are worth naming:

- **`0x157F AS_CONTENTS_ON_OFF_LIST`** — one record `(1, 0, 11)`. Provenance
  `ContentsOnOffManager::LoadManager` → `DBLoadContentsOnOff` + `DBLoadReserveContentsOnOff`.
  The record schema was not decoded.
- **`0x28F8 AS_EVENT_HUNTINGBONUS` × 2** — four empty sublists then
  `01 00 | f0 0a (=2800, the PlanetId) | …`; the pair differs only in two flag bytes.
  `InitCache_NPCBonusItem`.
- **`0x29E2 DBS_TBA_UPDATE_ROTATION`** — a 12-node list, 12-byte nodes, hero ids
  `20300, 21000, 21500, 22300, 22400, 22500, 20100, 20400, 20600, 20700, 20900, 21100`.
  `TBARotationManager::LoadRotationData` (DBHelper). The nominal sheet
  (`TBARotation → Rotation.xml`) **does not exist on disk**, so in this deployment the rotation
  is database-only.

**Practical conclusion:** replaying the burst verbatim (T23) is the right call and stays the
right call. Only two pushes are datasheet-derived in a way TeraSharp could regenerate (the
festivals), and regenerating them would change nothing — the captured bytes already are what
the sheet produces. The rest is another server's admin database.

`0x15DE`'s constant timestamp is now explained: it is `DungeonManager::SetDungeonPhaseLastResetTime`
persisted state, not a clock — consistent with it reading `2026-09-12T03:57:29Z` in all four
captures.

---

## 5. What T29 implemented

`World/DbProxyStaticData.cs`, `public static class HandshakeData`:

- `LoadDungeonTimelineIds(string datasheetDir)` — the four-sheet rule, comment-stripped,
  returning the ids ascending. Returns `null` when the directory is absent or any required
  sheet is missing, so a deployment without the Datasheet folder degrades to the captured list
  rather than failing.
- `LoadPoliticsUnitIds(string datasheetDir)` — `PoliticsData.xml` → the 17 ids, ascending.
- `DatasheetDirectory()` — `TERASHARP_DATASHEET`, else `<TERASHARP_DATA>\Executable\Datasheet`,
  else `D:\v100\TERA_SERVER.100\Executable\Datasheet`.
- `CapturedDungeonTimelineIds` / `CapturedPoliticsUnitIds` — the capture-derived lists, kept as
  the fallback **and** as the regression target.

Tests (`Handshake_*`): the sheets reproduce both captured lists exactly and in order (skipped
with a printed note when the Datasheet folder is not reachable from the test binary); the
intermediate 173-instance set matches `SA_WORLD_SERVER_STATUS`; commented-out `<Constraint>`
rows are excluded; a missing directory yields `null` rather than an exception; and the captured
constants still equal `DbProxyHandlers.PostHandshakeDungeonIds` so the two cannot drift.

**Not wired into startup.** Reading the sheets at boot means a runtime dependency on a folder
that belongs to the WorldServer install and is not part of `ship.ps1`'s payload, and the
fallback list is byte-identical anyway. The exact wiring, when the human wants it, is in §6.

---

## 6. Recommended follow-ups (not implemented — human-owned or behaviour-changing)

### (a) `0x1581` as an echo of `0x13F2` — **DONE (T33)**, except one line in WorldBridge

Implemented in `World/DbProxyHandlers.cs`:

- `Build1581(uint dungeonId, bool isOn = true, ulong nextChange = 0)` now writes the real shape,
  `[u32 DungeonId][u8 IsOn][u64 NextChange]`. The defaults reproduce the captured bytes exactly,
  so nothing on the wire changed.
- `ParseTimelineOpenInfo` / `BuildTimelineEcho` / `OnDungeonTimelineOpenInfo` turn a non-empty
  `0x13F2` into one `0x1581` per record, in order, and `0x13F2` is in the `TryHandle`
  allow-list. A malformed list is consumed with a warning, never passed to the replay table.
- `PostHandshakeDungeonIds` is now documented as a **fallback**. `OnWorldReady` still sends it,
  gated on `DbProxyHandlers.SendPostHandshakeDungeonBurst` (default `true`).
- `TimelineEchoed` says whether World has ever sent the list on this process.

Ground truth is `data/cap_timeline.bin` — both captured `0x13F2` frames and both 98-frame
`0x1581` bursts — and the `Timeline_*` tests rebuild the bursts from the requests byte for byte.

**The one thing left is human-owned.** `WorldBridge.HandleFrame` swallows every `0x13F2` before
`DbProxy.TryHandle` is reached, so the handler is unreachable as things stand:

```csharp
            case OpHeartbeat14:
            case OpHeartbeat6:
                return;
```

becomes

```csharp
            case OpHeartbeat6:
                return;

            case OpHeartbeat14:
                // DSA_DUNGEON_TIMELINE_OPEN_INFO. 909 of the 911 frames across the four captures
                // are the empty 14-byte form - a real heartbeat - but the first one after World
                // registers its dungeons carries the open-state list the real Arbiter echoes back
                // as 0x1581, one frame per record (status/HANDSHAKE-DATA.md section 0).
                if (payload.Length > 8) DbProxy?.TryHandle(this, link, op, payload);
                return;
```

The `payload.Length > 8` test keeps the heartbeat path free and means the replay table is never
consulted for `0x13F2` — it stays sealed in `WorldReplayTable.OneWayFromWorld`, which is correct
and unchanged.

**Then, in order:**

1. Apply the diff above and run a login. The log should carry
   `0x13F2: echoed 98 dungeon-timeline states back as 0x1581 (fallback burst also sent ...)`.
2. Once that line appears, set `DbProxyHandlers.SendPostHandshakeDungeonBurst = false`. From then
   on the ids are World's own rather than a 2026-09-13 capture's, which is what the real Arbiter
   does — it never sends an unsolicited `0x1581`.
3. Keep the fallback available. In the two captures where World sent no list the real Arbiter
   sent no `0x1581` at all and login still worked, but `CLAUDE.md` records a live-verified case
   where the burst was what unblocked a first enter-world into a fresh World process. Until a
   live run settles that, the flag is the way to move between the two behaviours in one line.

Sending both is safe in the meantime: `0x1581` is an idempotent state push (`dungeon X is on
until T`), and the echo carries World's values, so it supersedes anything the fallback got wrong.

### (b) Feed the burst list from the sheets at startup

`Program.cs` is human-owned. One line after the store is created:

```csharp
        DbProxyHandlers.SetDungeonTimelineIds(HandshakeData.LoadDungeonTimelineIds(HandshakeData.DatasheetDirectory()));
```

with a setter on `DbProxyHandlers` that ignores `null` and keeps
`PostHandshakeDungeonIds`. Worth doing only if the human ever changes
`DungeonConstraint.xml`; until then the two are byte-identical.

### (c) `0x1559`

It is replayed from `arb_world.log` and the sheet reproduces it exactly, so there is nothing to
fix. `HandshakeData.LoadPoliticsUnitIds` exists so that a changed `PoliticsData.xml` can be
detected by the test rather than shipping a stale reply.

---

## 7. Open questions

1. **Why do two of the four captures have no non-empty `0x13F2` at all?** Both have a complete
   handshake, so World registered dungeons in two runs and not in the other two. The likely
   cause is whether the dungeon WorldServers (`3. DungeonServer.bat` / `4. DungeonOther.bat`,
   ids 12 and 13) were running, but `WorldServer.exe.c` could not be staged to confirm it.
   This matters: it decides whether the fallback burst in §6(a) is ever the right thing to send.
2. **`DungeonManager::RegisterDungeon(int)` has zero textual callers** in the World decompile —
   it is reached through a function pointer the dump does not render — so the `DungeonMatching`
   exclusion is proven by exact set equality (39/173 split, zero delta over 212 candidates) but
   not by reading the line that applies it.
3. **`0x157F`'s three ints `(1, 0, 11)`** — provenance established (`DBLoadContentsOnOff`),
   record schema not decoded.
4. **`0x150A`'s 15 floats** are read as identity defaults because the DB is empty; the code that
   writes `1.0f` into that table was not found.
5. **`DarkRiftData.xml`** carries the reset hour
   (`DatasheetManager::LoadDarkRiftDailyCompletedResetHour`, `Arb_part_085.c:5759`) and would
   settle the "15:00 UTC or local midnight" question left open by T23. The sheet was not read.
6. **`EnchantProbEventManager::LoadManager`** (`Arb_part_080.c:18346`) contains no loader call
   at all; `0x15C5` was classified SQL from its sibling methods, not proven. Same for
   `DetectBOTCacheManager::LoadManager` / `0x1529`.
