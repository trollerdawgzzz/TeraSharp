# Duplicating a battlefield or a dungeon (T177)

2026-09-22, research plus tooling; no server behaviour changed. The subjects are Champions' Skyring
(BG **37**) and Velik's Sanctuary (dungeon **9781**).

Sources:
- the real sheets in `Executable\Datasheet` (unedited, mtimes 2020);
- `ServerConfig.xml`;
- the client DataCenter `tera-api\data\datasheets\DataCenter_Final_EUR.dat`, read with tera-api's own loader.

Citation keys:
- **W** `:line` = `world_decompiled\WorldServer.exe.c`;
- **A** `NNN:line` = `Arb_part_NNN.c`;
- **M** = a string in `MatchServer.exe`;
- **D** = the data itself.

No MatchServer decompile exists, so every MatchServer finding comes from its strings only. Error
texts shown as `&DAT_…` are unresolved in the decompile; their `%d` is the offending id.

## 1. Short answer

| | Battlefield (copy of 37) | Dungeon (copy of 9781) |
|---|---|---|
| New id | BG id 1..999. It can **share continent 115**: 37/38/39/40 already do (D). | A **new continent id** (dungeon id = continent id, and it must be unique, W :3162804) **and a new hunting zone** (W :368800). |
| Map / Topography | nothing to do: same continent | reused. Tiles are keyed by AreaList zone, pathdata by area name (§4). TopographyServer must be restarted so it builds the new continent. |
| NPCs, spawns, AI | shared | copied under the new hunting zone. This is exactly how the shipped hard mode 9981/981 was made from 9781/781 (D). |
| Team / party size | `MatchingRoleTemplate` role `totalUser` (the matcher's number) plus `maxTeamMember` (TeraSharp's) | role `totalUser` must **equal** DungeonData `maxMemberCount`, or World refuses to boot (W :270568) |
| Client | needs a DC `BattleFieldData` row for the tab (§5) | needs DC AreaList/ContinentData to load the map, plus DungeonMatching/StrSheet rows for the tab (§5) |
| Tool | `tools\dupe-battlefield.ps1` | `tools\dupe-dungeon.ps1` |

**Who does the matching:** on the real stack it is `MatchServer.exe`. The Arbiter only relays
AM_/MA_ frames to it (A 076:11166). World validates the sheets at boot. TeraSharp replaces the
Arbiter and runs its own matcher (§7).

## 2. Who loads what

| process | sheets | note |
|---|---|---|
| ArbiterServer | 167 entries in `ArbiterServerConfig` | never loads MatchingRoleTemplate, although it is listed (A: no `totalUser` anywhere) |
| WorldServer (0, 10-13) | 287 entries | TopographyServer is the same binary (same size, 32,194,560 bytes) run with `--sharedmemoryproducer=true` |
| MatchServer | 13 entries: AreaList, BattleFieldData, WorldData, FieldEvent, FieldData_\*, DungeonData_\*, DungeonConstraint, ContinentData, DungeonMatching, MatchingRoleTemplate, DailyEvent, EventMatching, BuildVersion | reads the role table at boot, so a role edit needs a MatchServer restart |

Wildcard families recurse into subfolders. `AreaData\`, `ActiveRotate\` and `CompensationData\`
are loaded that way. So nothing else, including backups, may live under `Datasheet\`.

## 3. Battlefield 37: every sheet that names it or its continent

| sheet / field | 37's value | copy needs it? | validation (fatal = boot stops) |
|---|---|---|---|
| BattleFieldData `<BattleField id>` | 37, `type="Round_PvP"` | **required** | A 085:3071 id < 0 is fatal. A 085:3079 a **duplicate id is silently dropped**. A 085:3886 type enum is fatal. A 085:3099 banRule outside 1..100 is fatal (the message says 0..99, but 37 ships 100). A 085:3108 `<CommonData>` must exist. W's own parser `FUN_140346f20` did **not decompile** (W :611159), so its internal checks are unknown. |
| `EnableTimeList` | 7 days, 0-24 | **required** (non-empty) | W :644380 `OpenTime 정보가 없습니다` |
| `RuleData@ruleId` | 107 | **required** | must be a MatchingRoleTemplate `Role@id`, W :646672 `MatchingRuleTemplate가 없습니다` (Validation, fatal) |
| MatchingRoleTemplate `<Role id="107">` | totalUser 3, min/maxMatchingMember 3 | **required** for a new size | Match size = `totalUser`, not `maxTeamMember` (W :2828539, :2828576; M). Healer/tanker/dealer min ≤ max (W :2771763-817). M `RoleId[%d]: mMinClass <= mTotalUser`. A duplicate Role id silently overwrites (W :2730735). 0 or more than 2 team children crashes the server on purpose (W :2771658). |
| `CommonData@maxTeamMember` | 3 | set to the new size | nothing compares it (A 085:3125). TeraSharp's matcher reads it (§7). |
| `CommonData@continentId` | 115 | **keep** | not checked at load (A 085:3141); runtime branches on channelType battleField (W :2754737). Shipped data shares continents: 115 (37-40), 116 (10/11), 112 (26-29), 110 (70/71), 118 (118/119). |
| `Team@doorId` | 1151 / 1152 | keep | runtime only (W :2084042); the doors are in `DynamicGeo_115` |
| `EloData@eloId` | 1 | optional | runtime only; a missing one gives grade 0 (W :2060814). EloSystem has ids 1 and 2. |
| `RankingCompetition` | active | optional (tool sets `active="false"`) | the Arbiter's Round_PvP board is the **lowest** Round_PvP id (A 084:14682); TeraSharp lists every active one (§7) |
| ItemEquipRestriction `<AllowedItems battleFieldId>` | none for 37; 38 and 40 have one | optional: the "unified / equalized gear" set | no cross-check (W :360171). `-EqualizedFrom 38` copies it. |
| BattleFieldData `BFMatchingUISortPriority` | lists 37 | optional | UI order. The sheet's own comment says unlisted ids are shown in id order. |
| Leaderboards `<ContentInfo id="37">`, `<ContentReward>` | present | **do not add** | A 050:6040-6161: fatal if the flags do not match RankingCompetition; buys nothing, because the board is 37's |
| EventMatching `Event type="BattleField"` Target | none for 37 | optional | exactly one Target, and it must exist (A 009:16830, W :392632) |
| StrSheet_BattleField (server) | name 32, desc 31, teams 25/26 | optional | Arbiter only, no check. The client shows its own DC strings. |
| AreaList / ContinentData / AreaData / Npc / Ai / Territory `_115` | continent 115, zone 115 | untouched | the continent is shared |

**Id rules:**
- 1..999: TeraSharp treats instance ids under 1000 as battlegrounds.
- Not an existing BG id.
- Not a DungeonData or DungeonMatching id. The Arbiter lists an id as a dungeon if a DungeonTemplate exists (A 079:8358). World takes the DungeonMatching rule before the BG rule (W :2828466-578).
- Above the lowest id of its type (A 084:14682).

## 4. Dungeon 9781: every sheet that names it or its hunting zone 781

| sheet / file | 9781's value | copy needs it? | validation |
|---|---|---|---|
| AreaList `<Continent id="9781">` | area `Velik_Palace_DG_P`, zones (1002,991) (1002,992), origin 1000,1000 | **required** | creates the continent (W :365962). ContinentData ids must exist here (A 009:4349, W :368520, fatal). |
| ContinentData `<Continent id="9781" channelType="dungeon">` | `<HuntingZone id="781">` | **required**, with a **new zone** | a zone may belong to one continent only (W :368800, A 009:4612, fatal). There is no channel exemption: the shipped sheet has 498 zones and no duplicates. |
| `DungeonData_9781.xml` | continentId 9781, `maxMemberCount` 5, 340 × `huntingZoneId="781"`, 342 × `"781,<id>"` pairs | **required** | unique continentId (W :3162804 `duplicated dungeon continent Id!`). EventTask `npcAi` / `turnAi` resolve NPC, AI and territory by zone (W :3183468-800, fatal). `spawn` / `despawn` are not checked at boot. `@dungeon:9781…` strings are client strings and stay unchanged. |
| per-zone files `_781` | TerritoryData, NpcData, NpcSkillData, AIData, ActiveMove, `ActiveRotate\`, DynamicSpawn, FormationData, WorkObjectTerritory, `CompensationData\ECompensation` | **required**, copied as the new zone | NPC = (template, the file's root zone) (W :620956). A territory's `<Npc>` uses the file's zone (W :3428617). AI = (NPC zone, aiId) (W :2469159). A channel builds territories only for its own zones (W :1233314). Only the root `huntingZoneId`, other `huntingZoneId="781"` and `"781,<id>"` pairs change, the same as 981 (D). |
| DungeonMatching `<Dungeon id="9781" matchingRoleId="17">` | levels 68-70, item level 453 | **required to match** | id must have a DungeonData (W :270564). Role must exist (W :270459). **Role totalUser must equal maxMemberCount** (W :270568). All fatal. The Arbiter's own Validate returns 1 (A 009:16243). S_MATCH_ROOM_LIST shows only rows inside the level range (A 070:11145). A row also removes the id from World 0's InstanceList (HANDSHAKE-DATA.md). |
| MatchingRoleTemplate `<Role id="17" changeRoleId="24">` | 5 = 1 tank / 1 healer / 1-3 dealers | **required** for a new size | M `Dungeon[%d], RuleId[%d]: MatchingRuleTemplate not found`. The tool drops `changeRoleId`, because role 24 is sized 5. |
| DungeonConstraint `<Constraint continentId="9781" huntingZoneId="781">` | act point 110 | optional (the tool copies it) | if present, DungeonData_<id> must exist (A 006:2534, W :257095, fatal); `huntingZoneId` is not read (W :266442) |
| `AreaData\AreaData_9781_Velik_Palace_DG_P.xml` | Area id 781, region strings 9781xxx | optional (the tool copies it) | no presence check. If present, continentId and areaName must resolve (W :2831380-486, fatal). |
| `DynamicGeo_9781.xml` | empty DoorList | optional (the tool copies it) | none |
| EventMatching Targets (2), DungeonNewbieBonus | present | not copied | EventMatching: the Target must exist (A 009:16772) |
| DungeonRankRecorder, Leaderboards, CompetitionDungeon | none for 9781 | not copied | a recorder would add a PvE board |
| Topology `x1002y991/992.geo/.idx`, `pathdata_Velik_Palace_DG_P.gdi/.nod` | on disk | **reused, nothing copied** | tiles by zone `x%dy%d.geo` (W :3346287); pathdata by area name (W :1334526), shared (W :1334594); tiles are loaded again per continent (about 2 zones of memory) |

**Topography, directly:**
- A duplicate cannot reuse continent id 9781. The dungeon-id check is fatal, and the TopoMap slot is taken.
- It can reuse the map, as 9981 already does.
- A continent the producer did not build fails in World with `Load Topo Fail, continent=%d` (W :2076895).

**Id rules:**
- Continent 1000..9998 (TopoMap has 9999 slots, W :2072030), unused.
- Zone 1..4095 (QuestCompensation range, W :3246535), unused in ContinentData and in any file name.
- Free on the shipped sheets: continents 9784-9789, zones 784-788.

## 5. What the client must know (DataCenter rows)

Measured in the EUR DataCenter.

**Battlefield:**

| DC section | holds for 37 | needed for |
|---|---|---|
| `BattleFieldData` | row 37 (id, name 32, type, CommonData sizes/levels, CommonUIData map image, EnableTimeList; **no continentId**) | the battlefield tab (inferred: a new id without a row cannot be listed or labelled) |
| `BattleFieldGlobalData/BFMatchingUISortPriority` | lists 37 | tab order |
| `StrSheet_BattleField` | 32 "[Personal Gear] Champions' Skyring", 31 desc, 25/26 teams | labels; reusing ids is fine |
| `MatchRole` | role 107 (the client's copy of the role table) | the role display for a new rule id (inferred) |

**Dungeon:**

| DC section | holds for 9781 | needed for |
|---|---|---|
| `AreaList`, `ContinentData`, `HuntingZoneAreaList` | 9781 / zone 781 | loading the zone on entry (inferred: the client resolves continent to area) |
| `DungeonMatching`, `StrSheet_Dungeon` (9781 "Velik's Sanctuary") | row, name | the Instance Matching tab |
| `StrSheet_ZoneName` (781), `StrSheet_Region` (9781, 9781001…) | names | zone and region labels |
| `Dungeon` (client DungeonData), `DungeonConstraint`, `ContinentCacheData`, `DungeonRecommend`, `EventMatching` | rows | UI extras |

**Server-only is enough for:** resizing an **existing** id in place (role `totalUser` and
`maxTeamMember` or `maxMemberCount`). The client just shows the old numbers.

**A new id needs a DC repack either way.** A BG needs its tab row. A dungeon needs its tab row and
the map lookup. The tools do not write the DC. The tree has tera-api's unpacker (`packer.js` /
`reader.js`) and no repacker.

## 6. The tools

Both tools:
- dot-source `tools\dupe-common.ps1`;
- edit as text, keeping each sheet's BOM, CRLF line endings and comments;
- fence every added row with `<!-- dupe:<kind>:<id> begin/end -->` and mark every created file;
- remove exactly those with `-Revert`;
- back up changed sheets to `Executable\dupe-backup\` (outside `Datasheet\`);
- refuse bad ids and re-run the checks from §3 and §4 on the result before writing.

`-DryRun` (or `-WhatIf`) lists every edit and runs the checks.

```powershell
.\tools\dupe-battlefield.ps1 -NewId 41 -TeamSize 2 -EqualizedFrom 38 -DryRun
.\tools\dupe-dungeon.ps1     -NewId 9785 -MaxMembers 3 -DryRun      # zone defaults to 785
.\tools\dupe-dungeon.ps1     -NewId 9785 -Revert
```

Tested in the sandbox with PowerShell 7.4, on copies of the real sheets, and checked by an
independent parser (well-formed, ContinentData ⊆ AreaList, unique zones, rules resolve,
totalUser = maxMemberCount):

| run | edits | result |
|---|---|---|
| dungeon 9781 → 9785, zone 785, size 3 | 5 sheets, plus 13 files (DungeonData, 10 per-zone files, AreaData, DynamicGeo); role 17 → 35 with totalUser 3 | checks ok; `-Revert` gives a byte-identical tree |
| dungeon → 9786, size 2 | role 35: totalUser 2, dealerMin lowered to 0 | ok; revert identical |
| BG 37 → 41, size 2, `-EqualizedFrom 38` | BattleFieldData (row, sort list), role 107 → 118 (totalUser 2, matching members 2), AllowedItems | checks ok; revert identical |
| refusals | existing id, zone owned or files present, id < lowest of type, id ≥ 1000 for BG, a role whose totalUser does not match, a source without a DungeonMatching row, a second apply | all refused, nothing written |

The tools have not been run on Windows PowerShell 5.1. They are ASCII only, with no PowerShell 7
syntax.

## 7. TeraSharp's side

| what | where | effect on a copy |
|---|---|---|
| BG team size | `MatchComposition` reads `maxTeamMember` | the tool sets it |
| BG vs dungeon | `MatchQueueManager.IsBattleground`: id < 1000 | this is the source of the id ranges above |
| dungeon group size | `MatchQueueManager.RaidSizes`, configured, default 5 | a 3-member copy still forms at 5 until `RaidSizes[id]` is set. The comment there says the role table "lives in the MatchServer binary", but it is `MatchingRoleTemplate.xml` (finding) |
| PvP boards | `DatasheetLoader.ReadPvpBoardIds`: every active RankingCompetition | this is why the tool turns it off |
| post-handshake dungeon ids | `DbProxyStaticData`: (DungeonData ∩ ContinentData) − DungeonMatching | picks the copy up automatically |

## 8. Not verified

- World's BattleFieldData parser did not decompile. What it checks internally (continentId, doors,
  a duplicate id) is covered only by the Arbiter's parser.
- The client-side needs are inferred from the DC contents; there is no client decompile. A live
  test with a repacked DC is the proof.
- Restarting MatchServer after a MatchingRoleTemplate edit is required by how it loads. It may
  explain T174b's "MatchingRoleTemplate edits didn't take", but that is not verified.
