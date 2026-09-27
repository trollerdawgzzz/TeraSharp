# World / Arbiter partial continent loading (T63)

Running the real `ArbiterServer.exe` + `WorldServer.exe` with only **Island of Dawn (continent 5)**
and the three intro instances (**9827 Tiarania, 9828 the burning village, 9829 the world tree**), so
that the pair fits beside MSSQL on the 32 GB netcup box during capture sessions.

Everything here is either cited to the decompile (`Arb_part_NNN.c:LINE` for the Arbiter,
`world_decompiled\WorldServer.exe.c:LINE` for World), measured against the shipped datasheets, or
explicitly marked as inferred. Where an earlier reading was wrong, the correction says so.

Tooling: `tools\trim-datasheets.ps1` and `tools\restore-datasheets.ps1` (section 8).

---

## 1. The short version

| | |
|---|---|
| **What works** | `loadAllContinents="true"` plus a **trimmed `Datasheet\ContinentData.xml`** (and `AreaList.xml`, and the sheets and per-continent files that reference the continents you dropped). |
| **What does not** | `<WorldServer loadAllContinents="false">` with `<Continent id="…"/>` children. It is not a bug in the data - the code cannot select a dungeon continent that way at all. Section 2. |
| **What does not either** | `Datasheet\PartialLoadingConfig.xml`. It is a real, designed partial-load feature, but it is **hunting-zone** granular and it never gates `LoadTopo`, so it cannot save the terrain memory. Section 2.3. |
| **Current blocker** | `[LeaderBoardDataSheet] Post-Process Error!` - `Leaderboards.xml` has 7 `<ContentInfo>` rows under `<ContentsTypeList type="dungeon">`, and every one of them resolves against **both** `DungeonTemplate` and `CompetitionDungeonData`. None of the 7 survives the keep-list. Section 7. |
| **Memory** | The per-continent terrain grid predicted by the decompiled allocator is **21.7 MiB** for the four kept continents and **5.91 GiB** for all 254 shipped continents. Section 9. |
| **Velika** | Not cheap. Continent 1 plus its five channel continents is **1.30 GiB** of terrain grid alone - roughly **60x** the whole current keep-list - before any content. Section 10. |

---

## 2. The mechanism, and the two things that are not it

### 2.1 Why the `ServerConfig.xml` `<Continent>` list selects nothing useful

`PlanetInfo::LoadPlanetInfo` (`WorldServer.exe.c:378779`) runs as **PreLoad step 5**, after
`ContinentData.xml` has been read at step 3. It first sorts every continent it already knows about
into three buckets by `channelType` (the literal mapping is read at `:368532-368568` -
`none`=0, `channelingZone`=1, `huntingZone`=2, `dungeon`=3, `battleField`=4, `citywar`=5, `field`=6):

* `channelType == 3` -> the **dungeon** set (`:379033`), and only if the id is also in the
  DungeonMatching set built during PreLoad;
* `channelType == 4` -> the **battlefield** set (`:379155`);
* anything else -> the **normal** set (`:379313-379325`).

Then, per `<WorldServer>` element:

* it reads the element's `loadAllContinents` attribute (`:379371`) and its declared `type`;
* if `type == "normal"` **and** `loadAllContinents` is FALSE **and** the element has children (its
  child-list begin and end pointers, element slots `3` and `4`, differ), it walks those children;
* per child it reads the `id` attribute, then `lower_bound`s that id **in the normal set only**;
* if the id is present - the hit flag is set and the returned position is not the set's end - the id
  is pushed onto that server's continent list (`:379574-379584`).

**The lookup is against the normal set only.** A `<Continent id="9827"/>` under a `type="normal"`
`<WorldServer>` is silently discarded, because 9827 is `channelType="dungeon"` and therefore lives
in a different bucket. Of the keep-list, only continent 5 is even eligible (it is
`channelType="channelingZone"`, bucket 1). There is no log line for the discard.

The `loadAllContinents="true"` branch (`:379691-379706`) instead walks the **whole normal set** into
the first normal server's list - i.e. it hands the server exactly the continents that survived
`ContinentData.xml`. That is why trimming the sheet is the mechanism and the `<Continent>` list is
not.

### 2.2 Correction: `Continent load time[0]` is **seconds, not a count**

`ContinentManager::LoadAndInitializeContinent` (`:2076019`) brackets its loop with
`FUN_1400118b0()`, which is `GetTickCount64() - _DAT_141ee9578` (`:10712`), and prints
`L"Continent load time[%d]\n"` through the level-10 logger with the argument computed as
`(endTick - startTick) / 1000` - elapsed milliseconds divided by 1000 (`:2076092`).

So `[0]` means "finished in under a second". It is consistent with "nothing loaded", but it is not
evidence of it - with only Island of Dawn it would print `[0]` on a healthy boot too. The line that
actually tells you a continent was skipped is
`L"LoadAndInitializeContinent Error! continentId = %d , worldServerId = %d \n"` (`:2076079`), and the
per-continent size meter is `L"LoadWorld continentId[%d] squarecount[%d]\n"` (`:3346222`).

### 2.3 `PartialLoadingConfig.xml` - real, but not this

`<PartialLoadingConfig fileName="PartialLoadingConfig.xml" />` sits in the `<WorldServerConfig>`
datasheet block. `PartialLoadingConfig::LoadConfig` (`:2694683`) reads:

```xml
<PartialLoadingConfig enable="…">
  <User enable="…"><User race="…" class="…" gender="…"/></User>
  <HuntingZone enable="…"><HuntingZone id="…"/></HuntingZone>
</PartialLoadingConfig>
```

There is **no continent selector**. Two predicates - `IsLoadTargetHuntingZone` (`:2690539`) and
`IsLoadTargetUser` (`:2713491`) - are consulted by about twenty loaders and validators, and a third,
`IsPartialLoadingOFF` (`:2696025`), suppresses the terrain-completeness assertions
(`L"없는 지형이 존재합니다. …"`, `:2076885`) so the server can run on an incomplete dataset.

It cannot replace the trim, because `ContinentManager::LoadTopo(const struct PlanetInfo *)`
(`:2076907`, called first at `:636667`) walks the **full** `ContinentData.xml` continent list with no
partial-loading gate and builds a TopoMap for every entry. It could be layered **on top of** the trim
to prune NPC/AI/skill/territory content per hunting zone.

**Caveat, stated plainly:** `rg` finds no call site for `PartialLoadingConfig::LoadConfig` in the
decompile, and it is the only writer of the master enable flag. Either Ghidra missed an indirect
call, or the feature is inert in this build. Every predicate defaults to "load everything" when the
flag is 0, so a `PartialLoadingConfig.xml` that is never read is harmless. Cheap test: drop one in
with `enable="true"` and see whether the `없는 지형이 존재합니다` line stops appearing.

---

## 3. The datasheet boot pipeline

Both binaries run `DatasheetManager::PreLoad -> Load -> Validate -> PostProcess`, each stage
snapshotting a TLS error counter on entry and returning `counter unchanged`. **Any single logged
datasheet error anywhere in a stage aborts the boot** - there is no "merely logged" error inside the
pipeline. The counter bumper is `FUN_14002b950` on the Arbiter (`Arb_part_000.c:17935`, TLS+0x90) and
`FUN_140028520` on World (`:28628`, TLS+0xc0).

### Arbiter (`Arb_part_033.c:13891-13930`)

| stage | function | console |
|---|---|---|
| PreLoad | `FUN_1409c6d80` (`Arb_part_085.c:14443`) | `Datasheets PreLoad...` |
| PlanetInfo::PostProcess | `FUN_140131990` (`Arb_part_009.c:14437`) | `PlanetInfo PostProcessing...` |
| Load (4 threads) | `FUN_1409b1d00` (`Arb_part_085.c:705`) | `Datasheets Load... ` |
| Validate | `FUN_1409d43f0` (`Arb_part_086.c:1808`) | `Datasheets Validating... ` |
| PostProcess | `FUN_1409c6c40` (`Arb_part_085.c:14399`) | `Datasheets Post-Processing... ` |

Arbiter PreLoad order, which is the order that matters here:
**AreaList -> ShieldTerritory -> DungeonMatching -> DungeonWork -> ContinentData -> World Structure.**

Arbiter PostProcess order: `CommonPostProcess`, `ItemTemplte` *(sic)*, `PveServerSetting`,
`DungeonDataSheet`, `EventMatchingDataSheet`, `PremiumSlotDataSheet`, **`LeaderBoardDataSheet`** -
the last one is the current blocker.

Per-sheet wrappers and their messages: `[%s] Loading Error!`, `[%s] Validation Error!`,
`[%s] Post-Process Error!` (`Arb_part_000.c:17947 / ~18100 / 18013`). World's equivalents are at
`:28640 / 28673 / 28706` and it also prints `Loading [%s]` / `Validation Check [%s]` /
`Post-Processing Start [%s]` per sheet, which is the fastest way to see how far it got.

### World (`WorldServer.exe.c`)

| stage | function | lines |
|---|---|---|
| PreLoad | `FUN_140377280` | 636352-636624 |
| Load (7 threads) | `FUN_140344af0` | 609575-609953 |
| Validate | `FUN_140380ee0` | 643448-643689 |
| PostProcess | `FUN_140375330` | 634597-635430 |
| ProcessWhenThreadReady | `FUN_140377a60` | 636631-637020 |
| Validate2 | `FUN_140381c90` | 644030 |

World PreLoad order is the same five steps. `ProcessWhenThreadReady` is where the terrain is built:
`ContinentManager::LoadTopo` (`:636667`), `BuildObjectDataSheet` (`:636701`),
`ContinentManager::LoadAndInitializeContinent` (`:636726`), `TerritoryDataSheet` (`:636758`),
`AreaDataSheet` (`:636810`), `NpcArenaDataSheet` (`:636987`).

---

## 4. What a continent *is*, in the loader's terms

Three registries decide everything else:

| registry | built by | key | notes |
|---|---|---|---|
| **continents** | `PlanetInfo::LoadAreaList` **only** | continent id | Arbiter `FUN_14011f6f0` (`Arb_part_009.c:3132`), World `FUN_1401f1490` (`:365962`). The insert is `try_emplace` at `Arb_part_009.c:4015` and `:4043` - **the only two call sites in the binary.** |
| **dungeons** | `DungeonTemplate` (`DungeonData_<id>.xml`) | the template's `continentId` | Arbiter `Arb_part_026.c:18265` reads `+0x24`, written from `L"continentId"` at `Arb_part_007.c:1743`. So a "dungeon id" anywhere else in the data **is a continent id**. |
| **huntingZone -> continent** | `PlanetInfo::LoadContinentData`, `<HuntingZone id>` children | hunting-zone id | insert only; never read back for validation at boot, but it is what lets a tool resolve `DynamicSpawn_<hz>.xml` to a continent. |

**`AreaList.xml` holds two kinds of continent record.** Top-level `<Continent id desc originZoneX
originZoneY>` with `<Area><Zones><Zone x y/></Zones></Area>` children, **and** nested
`<ChannelContinent id>` children inside those, each naming a subset of its parent's `<Area>`s and
inheriting the parent's origin zone (`Arb_part_009.c:3853`, World `:366830`). On the shipped 100.02
data:

* 252 top-level `<Continent>` + **18 nested `<ChannelContinent>`** = 270 continent records;
* the 18 are exactly `2000, 2050, 2052, 2054, 7001-7005, 7011-7015, 7021-7023, 7031`, owned by
  continents 6, 1, 2, 3 and 4 respectively;
* `ContinentData.xml` has 254 rows, and all 254 are in that set of 270.

This matters because a schema-naive parse of `AreaList.xml` that only looks at root-level elements
reports those 18 as missing and makes the sheet look inconsistent. It is not.

**Direction of the dependency:** `ContinentData` **requires**, never creates. `LoadContinentData`
looks the id up in the map and bumps the error counter if it is absent
(`Arb_part_009.c:4342-4349`, World `:368520-368527`), with no guard and no exemption by
`channelType`. The converse does not hold - 16 AreaList ids have no ContinentData row on the shipped
data and the server boots fine.

> **Therefore: `ContinentData ids` must stay a subset of `AreaList ids`.** The safest trim leaves
> `AreaList.xml` alone entirely and only cuts `ContinentData.xml`; cutting both (what
> `trim-datasheets.ps1` does by default) is also fine as long as they are cut with the same
> keep-list, which the tool's consistency check verifies.

A continent whose `<Area>` list is empty, or whose every `<Area>` is `underConstruction` /
`clientOnly` / has no `<Zones>`, is **discarded** by `LoadAreaList` (`Arb_part_009.c:3313`, `:3343`,
`:3828`). An AreaList stub with no zones is therefore equivalent to deleting the entry - and then its
`ContinentData` row becomes a fatal error.

---

## 5. The complete cross-reference table

### 5a. Sheets whose ROWS must be trimmed

These are the seven the tool edits. Every one is fatal on a dangling reference.

| sheet | element | attribute | resolved against | validator | error |
|---|---|---|---|---|---|
| `AreaList.xml` | `<Continent>`, nested `<ChannelContinent>` | `id` | *creates* the continent | `PlanetInfo::LoadAreaList` `Arb_part_009.c:3132` / W `:365962` | `AreaList Loading Error!` |
| `ContinentData.xml` | `<Continent>` | `id` | must exist from AreaList | `Arb_part_009.c:4342` / W `:368520` | `ContinentData Loading Error!` |
| `ContinentData.xml` | `<Continent>` | `inChannelingContinent` | the continent map | `PlanetInfo::PostProcess` `Arb_part_009.c:14530` / W `:390254` | `Invalid Channeling Continent Info. ContinentId [%d], Channeling ContinentId [%d]` |
| `ContinentData.xml` | `<HuntingZone>` | `id` | uniqueness across continents | `Arb_part_009.c:4595` / W `:368800` | `duplicated huntingZoneId [%d] at continent : [%d] and continent : [%d]` |
| `DungeonConstraint.xml` | `<Constraint>` | `continentId` | `DungeonDataSheet` | A `FUN_1400c7e70` `Arb_part_006.c:2475`; W `:257088`, attribute name confirmed at `:264557` | unresolved `&DAT_1416f4590` |
| `DungeonMatching.xml` | `<Dungeon>` | `id` (a dungeon continent id) | consumed by PreLoad and by EventMatching's validator | A `Arb_part_008.c:14882` | `DungeonMatching Loading Error!` + minidump |
| `CompetitionDungeon.xml` | `<Dungeon>` | `id` | `active="true"` gates insertion (`Arb_part_064.c:17537`) | - | feeds the Leaderboards check below |
| `Leaderboards.xml` | `<ContentsTypeList type="dungeon"><ContentInfo>` | `id` | **both** `DungeonDataSheet` and `CompetitionDungeonDataSheet` | `LeaderBoardDataSheet::PostProcess` `FUN_1405e1b00` `Arb_part_050.c:5978` | `[LeaderBoardDataSheet] Post-Process Error!` |
| `Leaderboards.xml` | `<ContentsTypeList type="battlefield"><ContentInfo>` | `id` | `BattleFieldTemplate` (not continent-scoped) | same | same |
| `EventMatching.xml` | `<Event><TargetList><Target>` | `id` (by Event `type`), `teleportContinentId` | `DungeonDataSheet` for `Dungeon`/`SoloDungeon`, `BattleFieldTemplate`, `DungeonMatching` | A `FUN_140134470` `Arb_part_009.c:16320`; W `FUN_140218930` `:392302` (19 fatal sites) | unresolved `&DAT_140adac50` etc. |
| `EventMatching.xml` | `<EnableDay><EnableTime>` | `eventId` | the events above | - | follows its event |

Note on the error text: most of these diagnostics are unresolved `&DAT_140xxxxxx` in the Ghidra
output, so the exact wording is not recoverable from the decompile. The `%d` argument is always the
offending id, and the line immediately before it is `last read[<file>]`, which names the XML file.

### 5b. Sheets that store a continent id and never resolve it

Confirmed by reading each parser: **no action needed.** `HeroWorldData` (`DefaultTownSpwanLocation
/@continent`), `RestrictionOpenData` (`ContinentList/continentId`), `ItemEquipRestriction`
(`continentId`), `GmEvent` (`continentId`, `exitContinentId`, `ignoreContinent`), `StatueData`
(`dungeonContinentId`, `spawnContinentId` - only checked non-zero, `Arb_part_073.c:19675`),
`FloatingCastleTemplate`, `WorldParameter`, `PolicyData`, `ServantData`, `GuildQuestData`,
`ReputationSystemData`, `CityWarGuildTower`, `FestivalSeasonTemplate`, `GuardSpawnPolicyTemplate`,
`DungeonWorkData` (`dungeonId` is checked for duplicates in its **own** map, not for existence -
`Arb_part_064.c:17991`), `ReplayMovieData`, `DungeonClearCount`, `DungeonCoolTime`,
`DungeonRankHallOfHonor`, `UserAchievementDarkRiftClose`, `PegasusPath` (its validator checks stage
counts only, W `:647018`).

**`huntingZoneId` is never resolved against anything at boot** on either binary. The only consumer of
the hunting-zone map is the duplicate check inside `LoadContinentData` itself.

### 5c. Per-id file families

Filename patterns come from the `<Datasheet>` blocks in `ServerConfig.xml` - there are **three**
(ArbiterServerConfig: 167 entries / 19 wildcards; WorldServerConfig: 287 / 51; a third section: 13 / 2).
`trim-datasheets.ps1 -ServerConfig <path>` reads them so a changed mapping is picked up, and reports
any wildcard sheet it has no verdict for instead of guessing.

**The number in the filename is never parsed by the server.** Every loader takes the id from an XML
attribute; the filename is only used in error text. The table below says what that attribute is, which
is what makes the number *usable* as a selector by an external tool.

| family | pattern | number is | self-filters? | tool tier |
|---|---|---|---|---|
| AreaData | `AreaData_*.xml` | **continent** (`continentId` W `:2831380`) | no | Minimal |
| DungeonTemplate | `DungeonData_*.xml` | **continent** (`continentId` W `:3162629`) | no | Minimal |
| DungeonRankRecorderTemplate | `DungeonRankRecorder_*.xml` | **continent** (W `:3161758`) | no | Minimal |
| FieldTemplate | `FieldData_*.xml` | **continent** (W `:2269279`) | no | Minimal |
| DynamicGeoTemplate | `DynamicGeo_*.xml` | **continent** (W `:3426285`) | no | Minimal |
| CollectionTerritory | `CollectionTerritory_*.xml` | **continent** (W `:2694117`) | no | Minimal |
| ShieldTerritory | `ShieldTerritory_*.xml` | **continent** (A `Arb_part_009.c:7722`, W `:380595`) | no | Minimal |
| ClimbingTemplate | `ClimbingTerritory_*.xml` | continent (W `:3426057`) | **yes** (`:3426058`) | Standard |
| SwimmingTerritoryTemplate | `SwimmingTerritory_*.xml` | continent (W `:3429362`) | **yes** (`:3429364`) | Standard |
| TeleportTerritoryTemplate | `TeleportTerritory_*.xml` | continent (W `:3429560`) | **yes** (`:3429562`) | Standard |
| TerritoryTemplate | `TerritoryData_*.xml` | hunting zone (W `:3430113`) | **yes** (`:3430119-21`) | Standard |
| FishingTerritoryTemplate | `FishingTerritory_*.xml` | hunting zone (W `:3426965`) | **yes** (`:3426967`) | Standard |
| AiTemplate | `AiData_*.xml` | hunting zone (W `:2260444`) | no | Aggressive |
| ActiveMoveTemplate | `ActiveMove_*.xml` | hunting zone (W `:610039`) | no | Aggressive |
| ActiveRotateTemplate | `ActiveRotate_*.xml` | hunting zone (W `:610411`) | no | Aggressive |
| DynamicSpawnTemplate | `DynamicSpawn_*.xml` | hunting zone (W `:617325`) | no | Aggressive |
| FormationData | `FormationData_*.xml` | hunting zone (W `:618824`) | no | Aggressive |
| BonfireSpawnData | `BonfireData_*.xml` | hunting zone (W `:1836860`) | no | Aggressive |
| WorkObjectSpawnData | `WorkObjectTerritory_*.xml` | hunting zone (W `:2263847`) | no | Aggressive |
| QuestCompensationData | `QuestCompensationData_*.xml` | hunting zone (W `:3246535`, range-checked 1..4095) | no | Aggressive |
| NpcPartTemplate | `NpcPartData_*.xml` | hunting zone (W `:1736541`) | no | Aggressive |
| BehaviorRecorderTemplate | `BehaviorRecorder_*.xml` | - **dead sheet**: `rg` finds zero references in the World binary | - | Aggressive |
| **NpcTemplate** | `NpcData_*.xml` | hunting zone (W `:620956`) | no | **Never** - NPC templates are the resolution target of the spawn, AI, quest and compensation validators; removing them breaks checks that have nothing to do with continents |
| **NpcSkillTemplate** | `NpcSkillData_*.xml` | hunting zone, **inferred from consumers only** - the shared parser `FUN_14097ad90` (`:1744327`) did not decompile | - | **Never** |
| **UserSkillTemplate / HeroSkillTemplate** | `UserSkillData_*.xml`, `HeroSkillData_*.xml` | not an id | - | **Never** |
| **S1ActionScript** | `S1ActionScripts_*.xml` | not an id (row `id` + `playingTime` only, W `:626486`) | - | **Never** |
| **PegasusPath** | `PegasusPath_*.xml` | not a continent - `continentId` is read per `<Stage>` (W `:624333`) | - | **Never** |

"Self-filters" means the loader calls `PlanetInfo::DoIHaveThisContinent` (`FUN_1401d57a0`, W
`:345165`) and silently skips a file belonging to a continent this world server does not own. Those
families are safe to leave in place; moving them only saves directory-walk time.

### 5d. World validators that resolve an id against the loaded TopoMap array

`ContinentManager::GetTopoMap(int)` (`:2072030`) is a flat `void*[9999]` indexed by continent id.
Four post-processes read it with **no guard** and one with a guard:

| check | line | message |
|---|---|---|
| PostProcessClimbingTerritory | `:3437076` | `Climbing Template Error. [id=%d] : Invalid world. [continent id=%d]` |
| PostProcessFishingTerritory | `:3437615` | `Fishing Territory Template Error. [id=%d] : Invalid world. [continent id=%d]` |
| PostProcessSwimmingTerritory | `:3438546` | `Swimming Territory Template Error. [id=%d] : Invalid world. [continent id=%d]` |
| PostProcessTeleportTerritory | `:3438641` | `Teleport Territory Template Error. [id=%d] : Invalid world. [continent id=%d]` |
| ValidateTopo (**guarded**) | `:3452660` | `[Territory] Cannot find topoMap (hzid=%d, continentId=%d)` |

The four unguarded ones only stay quiet because their **loaders** already discarded foreign-continent
rows via `DoIHaveThisContinent`. If one of them fires, the real fault is upstream: a kept continent
whose `LoadTopo` failed.

Also fatal, and worth recognising in a log: `DungeonTemplate continentId[%d] :: duplicated dungeon
continent Id!` (`:3162804`), `DungeonRankRecorderTemplate continentId[%d] :: duplicated dungeon
continent Id!` (`:3161944`, A `Arb_part_026.c:18395`), `FieldEventTemplate continent/eventId[%d,%d]
:: duplicated field event continent Id!` (`:2269418`), `MiniGameRankRecorderTemplate id[%d] ::
duplicated dungeon continent Id!` (`:1837232`).

---

## 6. ShieldTerritory - the rule, corrected

`ShieldTerritory::PostProcess` runs for **every** continent, unconditionally
(`Arb_part_009.c:14471`, W `:390383`), and prints `ShieldTerritory PostProcess Error!!` with **no
continent id**, which is why it is hard to localise.

What it does (`FUN_140131d50`, `Arb_part_009.c:14583`): if the continent's fence list has **fewer
than 3 points**, it copies the continent's own area bounding box into the shield box; otherwise it
takes the bbox of the fence points and clamps it into the area box. Then it fails if
`maxX < minX` or `maxY < minY`.

The area box starts at `min = 999999`, `max = -1` (`FUN_1400fea10`, `Arb_part_008.c:444`) and is only
narrowed by `<Area><Zones><Zone>` entries. So:

> **A continent with at least one `<Zone>` in `AreaList.xml` always has a non-inverted box and passes
> with no ShieldTerritory file at all.** A continent with zero zones keeps the inverted sentinel box
> and fails - and for that case it needs a ShieldTerritory file naming it, containing a `<Territory>`
> with **at least three `<Fence>` points** (the file must have the `<Territory>` node to parse at
> all; `>= 3` fences is what makes the post-process pass).

That sharpens the empirical "one file per kept continent with at least one Territory". On the shipped
data the keep-list needs **no** ShieldTerritory file: continent 5 has 2 zones and 9827/9828/9829 have
1 each. The 4-fence stub you built works because 4 >= 3, not because 4 is special.

The other half of the rule is the dangerous one: **a ShieldTerritory file naming a continent you
deleted is a hard PreLoad failure** (`Arb_part_009.c:7754-7763`, W `:380596-380606`). Those files must
be moved aside. `trim-datasheets.ps1` does it, and re-checks every surviving one at the end.

The loader finds them by walking `Datasheet\` **recursively** - `DataXmlManager::SearchFilesInDirectory`
(`Arb_part_009.c:15240`, W `FUN_140114660` `:204601`) recurses into subdirectories at
`Arb_part_009.c:15551` / `:204952`, skipping only `.`, `..` and `.svn`, and registers every match under
the same sheet key. **An "aside" folder inside `Datasheet\` would still be loaded.** The tool refuses
an `-AsideRoot` under the Datasheet tree for exactly this reason.

---

## 7. The channeling rule - why continent 1 drags in 7001-7005

`inChannelingContinent` is parsed as a comma-separated int list onto the continent record at `+0xb8`
(`Arb_part_009.c:4449`, W `:368627`) and validated in `PlanetInfo::PostProcess`:

It walks the record's channeling-id list - `uint` entries from record slot `0x1a` up to slot `0x1b` -
and looks each id up in the continent hash map. A miss (the lookup yields 0) formats
`L"Invalid Channeling Continent Info. ContinentId [%d], Channeling ContinentId [%d]"` into a
`0x3ff`-char buffer, passing the owning continent's own id from record slot `3` first and the
offending channeling id second (`Arb_part_009.c:14488-14570`, World `:390254`). Failure is fatal.

On the shipped data exactly **five** rows carry the attribute:

| continent | `inChannelingContinent` |
|---|---|
| 1 (Arun) | `7001,7002,7003,7004,7005` |
| 2 (Heria) | `7011,7012,7013,7014,7015` |
| 3 (Shara) | `7021,7022,7023` |
| 4 (Ruin) | `7031` |
| 6 (Floating Dragon Island) | `2000,2050,2052,2054` |

So keeping continent 1 requires keeping 7001-7005 **or editing the attribute**.
`trim-datasheets.ps1` rewrites it automatically: any listed id that is not in the keep-list is dropped
from the list, and the attribute is removed entirely when nothing survives. None of the keep-list
rows carries it, so on `{5, 9827, 9828, 9829}` there is nothing to rewrite.

One more per-row check to know about: `((initChannelCount == 0) || (initChannelCountByQA == 0))` is
an error for `channelType` 0, 1 and 6 (`Arb_part_009.c:4400-4413`). Continent 5 is
`channelingZone` with `initChannelCount="3"`, so it is fine - but a hand edit that zeroes it would
fail the boot, and the tool's consistency check flags it.

---

## 8. The Leaderboards blocker, concretely

`LeaderBoardDataSheet::PostProcess` (`FUN_1405e1b00`, `Arb_part_050.c:5978-6471`) walks two maps and
returns `errorCounter unchanged` - one bad row fails the whole stage. On this build the XML is:

```xml
<Leaderboards>
  <ContentsTypeList type="dungeon" ...>
    <ContentInfo sort="1" id="3203" maxShowTotalRank="100" maxShowClassRank="20" .../>
    ...
  </ContentsTypeList>
  <ContentsTypeList type="battlefield" ...> ... </ContentsTypeList>
</Leaderboards>
```

For each **dungeon** row, `id` is a **dungeon continent id** and must resolve in

1. `DungeonDataSheet` - inlined `GetDungeonTemplate` at `:6270-6307`, miss -> `&DAT_140c039a0`;
2. `CompetitionDungeonDataSheet` - `FUN_140716350` at `:6357-6365`, miss -> `&DAT_140c03a20`;

and then `maxShowTotalRank`/`maxShowClassRank` must be `<= 100` and consistent with the competition
row's `total`/`class` flags, and `seasonOut` must match. Battlefield rows resolve against
`BattleFieldTemplate`, which is not continent-scoped.

**Measured against the shipped files for keep-list `{5, 9827, 9828, 9829}`:**

| | rows | survive |
|---|---|---|
| `Leaderboards.xml` `type="dungeon"` | 7 (`3203, 3126, 9044, 3201, 9982, 9920, …`) | **0** |
| `Leaderboards.xml` `type="battlefield"` | 4 (`37, 26, 10, 30`) | 4 - left alone |
| `CompetitionDungeon.xml` `<Dungeon>` | 8 | **0** |
| `DungeonMatching.xml` `<Dungeon>` | 39 | **0** |

So the fix is: **delete all 7 `<ContentInfo>` rows from the dungeon list** (and the matching
`CompetitionDungeon` rows, in that order - trimming CompetitionDungeon without trimming Leaderboards
produces exactly the same error from the second check instead of the first).
`trim-datasheets.ps1` does both, and trims CompetitionDungeon first for that reason.

An empty `<ContentsTypeList type="dungeon">` is fine: the post-process iterates the map and an empty
map means zero checks.

---

## 9. The tools

```
tools\trim-datasheets.ps1      make a trimmed Datasheet folder for a keep-list of continent ids
tools\restore-datasheets.ps1   undo it, from the manifest the trim wrote
```

### 9.1 Trim

```powershell
# 1. look first - changes nothing
.\tools\trim-datasheets.ps1 `
    -DatasheetRoot D:\v100\TERA_SERVER.100\Executable\Datasheet `
    -KeepContinents 5,9827,9828,9829 `
    -AsideRoot     D:\v100\TERA_SERVER.100\DatasheetAside `
    -ServerConfig  D:\v100\TERA_SERVER.100\Executable\ServerConfig.xml `
    -WhatIf

# 2. a trimmed COPY, original untouched (best for a first run)
.\tools\trim-datasheets.ps1 `
    -DatasheetRoot D:\v100\TERA_SERVER.100\Executable\Datasheet `
    -KeepContinents 5,9827,9828,9829 `
    -Destination   D:\v100\scratch\Datasheet `
    -AsideRoot     D:\v100\scratch\aside

# 3. in place, reversible
.\tools\trim-datasheets.ps1 `
    -DatasheetRoot D:\v100\TERA_SERVER.100\Executable\Datasheet `
    -KeepContinents 5,9827,9828,9829 `
    -AsideRoot     D:\v100\TERA_SERVER.100\DatasheetAside
```

What it does, in order: optionally copy the tree; read `AreaList.xml` + `ContinentData.xml` and build
the continent set and the huntingZone->continent map; trim rows in the seven sheets of section 5a
(backing each one up first); move per-continent files aside; optionally write ShieldTerritory stubs;
run a consistency check; write `trim-manifest.json`.

**`-Aggressiveness`** picks how many families move:

| tier | families | rationale |
|---|---|---|
| `Minimal` (default) | the 7 continent-keyed families whose loader resolves `continentId` fatally | the minimum that boots |
| `Standard` | + the 5 self-filtering territory families | saves walk time, cannot break anything |
| `Aggressive` | + 9 hunting-zone families resolved through `ContinentData`'s `<HuntingZone>` children | biggest saving; never touches `NpcData_*`, the skill sheets, `S1ActionScripts_*` or `PegasusPath_*` |

Other switches: `-StubShieldTerritory` (writes a 4-fence stub only for a kept continent that has no
zones and no existing file), `-ServerConfig` (take the filename patterns from the real config),
`-Manifest` (where the manifest goes), and the standard `-WhatIf` / `-Confirm`.

**Guards it enforces**: `-AsideRoot` must not be inside the Datasheet tree (the loader walks it
recursively); `-Destination` must not be either, and must not already exist; a kept continent with no
`AreaList` record is refused up front with the reason. At the end it re-reads the trimmed sheets and
reports, without fixing: a `ContinentData` id with no `AreaList` record, a dangling
`inChannelingContinent` target, a zero `initChannelCount` on a channelType that rejects it, and a
`ShieldTerritory_*.xml` naming a deleted continent or missing its `<Territory>`.

### 9.2 Restore

```powershell
.\tools\restore-datasheets.ps1 -Manifest D:\v100\TERA_SERVER.100\DatasheetAside\trim-manifest.json -WhatIf
.\tools\restore-datasheets.ps1 -Manifest D:\v100\TERA_SERVER.100\DatasheetAside\trim-manifest.json
```

Restores each trimmed sheet from its backup, moves every file back, deletes the stubs, tidies the
empty aside folders (keeping the manifest and the backups), and verifies. It **refuses** to overwrite
a file that reappeared in the Datasheet tree, and refuses to delete a stub that has been edited, in
both cases unless `-Force`. A trim run made with `-Destination` needs no restore and the script says
so and stops.

### 9.3 What was actually executed, and what was not

**This session has no shell on the Windows box** (the remote-device bridge exposes file staging and
listing, not command execution), so the scripts could not be run against
`D:\v100\TERA_SERVER.100\Executable\Datasheet` itself. They were run in the Linux container under
**PowerShell 7.4.6**, against a reconstructed mirror of that tree:

* **real, byte-for-byte**: `AreaList.xml` (110,516 B), `ContinentData.xml` (102,072 B),
  `DungeonConstraint.xml`, `EventMatching.xml` (306,767 B), `DungeonMatching.xml`, `Leaderboards.xml`,
  `CompetitionDungeon.xml`, `DungeonWorkData.xml`, and `ServerConfig.xml` - i.e. **every file the
  trim actually edits**, and the config it reads the patterns from;
* **real filenames, placeholder contents**: the 1,996 files the directory listing returned for the
  `Datasheet\` root;
* **synthesised filenames** derived from the real continent and hunting-zone ids, for the families the
  2,000-entry listing cap cut off (`ShieldTerritory_*`, `TerritoryData_*`, `NpcData_*` and the rest) -
  deliberately placed in subfolders so the recursive walk is exercised.

3,229 files total. Results:

| run | tier | files left in `Datasheet\` | moved aside | rows removed |
|---|---|---|---|---|
| copy (`-Destination`) | Standard | 1,991 | 1,238 | see below |
| in place | Aggressive | 411 | 2,818 | see below |
| in place | Minimal | 2,325 | 904 | see below |

Rows removed, identical in all three runs: `AreaList.xml` 248 `<Continent>`; `ContinentData.xml` 250
`<Continent>`; `DungeonConstraint.xml` 166 `<Constraint>`; `DungeonMatching.xml` 39 `<Dungeon>`;
`CompetitionDungeon.xml` 8 `<Dungeon>`; `Leaderboards.xml` 7 `<ContentInfo>`; `EventMatching.xml` 154
`<Event>` + 6 `<EnableTime>` + 5 now-empty `<EnableDay>`.

Verified after each run:

* `AreaList` and `ContinentData` both reduced to exactly `{5, 9827, 9828, 9829}`, and
  `ContinentData ⊆ AreaList`;
* no `inChannelingContinent` left anywhere; continent 5 keeps `initChannelCount="3"`;
* `DungeonConstraint` keeps exactly the three intro instances;
* `Leaderboards` dungeon list empty, battlefield list untouched;
* 60 `EventMatching` events survive with **zero** references to a dropped continent;
* UTF-8 BOM preserved on every edited sheet, comments preserved, and a comment that labelled a
  removed row is removed with it rather than drifting up to label the wrong one;
* `-WhatIf` on both scripts: 3,229 files, **0 changed**;
* **trim -> restore round trip at all three tiers: byte-identical, 3,229 files, 0 differing.**

What that does **not** prove: that the resulting Datasheet folder boots. Only the real binaries can
say that. Run the copy-mode command above and point a test server at it.

---

## 10. Memory - where the 27 GB went

### 10.1 The lever

`ContinentManager::LoadTopo(const struct PlanetInfo *)` (`WorldServer.exe.c:2076907`) is the **first**
thing `ProcessWhenThreadReady` does (`:636667`), and it iterates **`PlanetInfo`'s full continent
list** - not the per-server list:

It starts from `PlanetInfo`'s own continent list head (`planetInfo+8`, dereferenced twice) - the
**full** list - and walks it node by node through each record's `next` pointer. Per record it stores
the TopoMap pointer into the manager's table at `manager + 0x13878 + continentId * 8`, where
`continentId` is the record's slot `3`, then calls `TopoMap::LoadWorld` (`FUN_141254b60`) on it with a
fixed global constant. The loop body carries no filter and no early exit.

There is **no** `DoIHaveThisContinent` filter here. A TopoMap is built for every `<Continent>` in
`ContinentData.xml`, whether or not this world server owns it. That single fact is why trimming the
sheet - and nothing else - moves the number. (`LoadAndInitializeContinent`, which builds the
`Continent` object, `PathFinder` and NPC spawns, *does* respect the per-server list.)

### 10.2 The allocation formula

From `TopoMap::TopoMap` (`:3306018`), `TopoMap::LoadWorld` (`:3346080`), `FUN_1412547d0` (`:3345937`)
and `PathFinder::LoadPathNodes` (`:1334446`):

```
tilesX = maxZoneX - minZoneX + 1          # <Zone x y/> span from AreaList.xml
tilesY = maxZoneY - minZoneY + 1
cells  = (tilesX * 120) * (tilesY * 120)  # 0x78 = 120 squares per zone tile

eager grid   = cells        * 164 bytes   # int[] 4 B + Square[] 152 B + void*[] 8 B, all up front
real squares = zoneCount    * 14400 * 152 bytes   # per square that has a .geo/.idx tile
pathnodes    = tilesX*tilesY * 86400 bytes + 76 B per node   # SHARED between continents by map name
```

Two consequences worth internalising:

* the eager grid is charged for the **whole bounding rectangle**, so an L-shaped or sparse continent
  pays for empty space; it is **quadratic in the zone span**;
* the boot log prints the real-square term directly:
  `LoadWorld continentId[%d] squarecount[%d]` (`:3346222`), where `squarecount = 14400 x zoneCount`.

### 10.3 Applied to the shipped `AreaList.xml`

| set | continents | eager grid | real squares | pathnode | total |
|---|---|---|---|---|---|
| keep-list `{5, 9827, 9828, 9829}` | 4 | 11.3 MiB | 10.4 MiB | 0.4 MiB | **21.7 MiB** |
| continent 1 + 7001-7005 (Velika cluster) | 6 | 792.8 MiB | 540.6 MiB | 29.0 MiB | **1.30 GiB** |
| everything in `ContinentData.xml` | 254 | - | - | - | **5.91 GiB** |

Per continent in the keep-list: continent 5 has 2 zones -> 28,800 cells -> 8.8 MiB; 9827/9828/9829
have 1 zone each -> 14,400 cells -> 4.4 MiB each.

### 10.4 So what were the other ~18 GB?

The formula accounts for **5.91 GiB** of the ~23 GB the trim saved. The remainder is the per-continent
*content* the same trim removes, and it is not modelled here: `AreaData_<id>_*.xml` geometry,
`DynamicSpawn` / `ActiveMove` / `AiData` / `FormationData` per hunting zone,
`CollectionTerritory`, `TerritoryData`, NPC spawn tables, plus the `Continent` objects and
`PathFinder` state that `LoadAndInitializeContinent` builds for the continents this server owns, plus
a flat 250 MB slab (`VirtualAlloc(0, 0xfa00000, MEM_COMMIT, PAGE_READWRITE)`, `:19217`).

The honest summary: **terrain grid is the biggest single line item and the only one with a closed-form
model, but it is roughly a quarter of the saving.** The rest is content, which is why the Aggressive
tier exists. If you want the real split, boot once untrimmed and grep the log for
`LoadWorld continentId[%d] squarecount[%d]` and for
`Current Memory status availphys[%d] totphys[%d]` (`:2619501`, values in MB).

---

## 11. Can Velika come back cheaply?

**No - it is the single most expensive thing you could add back, and the cost is structural.**

Velika is continent **7005**, and 7005 is not a standalone continent: it is a
`<ChannelContinent>` nested inside continent **1 (Arun)** in `AreaList.xml`, alongside 7001, 7002,
7003 and 7004. Three facts combine badly:

1. **Each channel continent is a full, independent `ContinentInfo`** with its own zone bounding box,
   created by the same `try_emplace` as a top-level continent (`Arb_part_009.c:4015`). `LoadTopo`
   walks the continent *list*, not unique geometries, so six records means **six separate TopoMaps**
   over overlapping terrain. Pathnode data is shared by map name; the square grids are not.
2. **Continent 1's `inChannelingContinent="7001,7002,7003,7004,7005"` is validated fatally**
   (section 7). Keeping 1 without all five means editing that attribute; keeping 7005 without 1 means
   7005 loses the parent whose `<Area>`s it names, and an `<Area name>` that resolves to nothing
   leaves the channel continent with no zones - which then fails `ShieldTerritory::PostProcess`
   unless you give it a >= 3-fence stub.
3. **Continent 1 is enormous**: 139 `<Zone>` entries spanning a bounding box of 3,182,400 cells.
   That is 497.7 MiB of eager grid on its own, before its channels.

Measured cost of the cluster: **1.30 GiB of terrain grid**, against **21.7 MiB** for the entire
current keep-list - about **60x**. Add the content (AreaData, NPC spawns, AI, dynamic spawns,
collection territories for 139 zones) and a World that currently sits near 3.9 GB should be expected
somewhere in the **6-8 GB** range. On a 32 GB box that also runs MSSQL that is affordable, but it is
not free, and it is not a tweak - it changes what the box can do while a capture is running.

**If you do want it**, the cheapest shapes, in order:

1. **Velika only, one channel.** Keep continent 1, drop 7001-7004, rewrite
   `inChannelingContinent="7005"` - which `trim-datasheets.ps1` does automatically when you pass
   `-KeepContinents 5,9827,9828,9829,1,7005`. Predicted terrain: 497.7 + 75.4 = **573 MiB**.
2. **Velika without Arun** is not available: 7005 names Arun's areas.
3. Run Velika captures in a **second, separate session** with its own keep-list, rather than making
   one server carry both. The tools make switching keep-lists a two-command operation.

Cross-check before committing: continent 1's real `squarecount` from a boot log, since the eager term
above assumes the full bounding rectangle and the real-square term assumes every zone has a `.geo`
tile.

---

## 12. Status of each claim

**Verified in the decompile** (cited above): the ServerConfig `<Continent>` bucket rule; `Continent
load time` being seconds; the four-stage pipeline and the all-or-nothing error counter; AreaList as
the sole creator of continent records and the nested `<ChannelContinent>` form; ContinentData
requiring an existing record; the channeling check; the ShieldTerritory fence-count rule; the
Leaderboards double resolution; the recursive datasheet folder walk; `LoadTopo` ignoring the
per-server list; the TopoMap allocation formula; the id attribute of every family in section 5c
except where noted.

**Verified against the shipped datasheets**: the 252 + 18 = 270 continent records; the five
`inChannelingContinent` rows and their values; `ContinentData ⊆ AreaList`; the 7 dungeon / 4
battlefield Leaderboards rows and the 8 CompetitionDungeon rows, none of which survive the keep-list;
continent 5 = `channelingZone`, `initChannelCount="3"`, 1 area, 2 zones, hunting zone 599;
9827/9828/9829 = `dungeon`, 1 zone each, hunting zones 827/828/829.

**Established empirically on the live server** (2026-09-15, not re-derivable from the decompile):
that the trimmed data actually boots; that the ServerConfig `<Continent>` route died at the Arbiter
handshake; the 27 GB -> 3.9 GB figure.

**Inferred, not proven:**

* `ServerConfig.xml`'s sheet-name -> filename mapping is read from the `<Datasheet>` blocks - the
  element names there match the `LoadEachFile` keys, but the decompile renders the key constant as
  `&DAT_1419c346c` in several places, so the `id` attribute name in `<WorldServer><Continent id=…/>`
  is an inference from every other use of the same constant.
* `NpcSkillData_*.xml`'s number is a hunting-zone id - taken from its consumers
  (`[Skill Id = %d, HuntingZone Id = %d]`, `:1747170`), because the shared parser `FUN_14097ad90`
  (`:1744327`) did not decompile. It is in the **Never** tier, so nothing depends on this.
* The ~18 GB of non-terrain memory is attributed to per-continent content by elimination, not
  measurement.
* `PartialLoadingConfig` may be inert in this build (section 2.3).

**Open:**

1. Does the trimmed folder boot? Nobody has run these scripts against the real tree yet - this
   session has no shell on that machine. Copy mode first.
2. `BehaviorRecorderTemplate` is configured but has zero references in the World binary. Harmless
   either way; noted in case it matters for a later patch level.
3. `DungeonConstraintDataSheet::Load` is not in any of the Arbiter's four Load groups, so on a stock
   Arbiter boot it may never run - yet `DungeonConstraint.xml` rows were empirically implicated. The
   World definitely loads it (`:256990`). Worth confirming which binary the error came from.
4. The 16 AreaList ids with no ContinentData row (`2104, 9084, 9085, 9099, 9121-9123, 9151, 9771,
   10000-10006`) are harmless today. If a future trim keeps one of them, remember it has no
   `channelType` and will not be classified into any `LoadPlanetInfo` bucket.
