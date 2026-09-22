# Datasheet values in code (T159)

The World datasheets under `Executable\Datasheet` are the source of truth. Every value TeraSharp
copied out of one is read through `World/DatasheetLoader.cs`; the copy is the built-in fallback
used only when the sheet is missing or does not parse. Folder: `TERASHARP_DATASHEET`, else
`<TERASHARP_DATA>\Executable\Datasheet`.

## Loaded (T159)

| Sheet | Replaces (built-in) | Used by | From |
|---|---|---|---|
| GuildConfig.xml `<GuildSize>` rank 0 | declareCost 1500, declareLimitCount 10, maintainCost 250 | `GuildWarManager.DeclareCost / DeclareLimit / GuildBlockUnk2` | T80 |
| GuildConfig.xml `<SurrenderReparationRate>` | the shipped 16 rates (rank 0 vs 0 = 0.5) | `GuildWarManager.GiveUpPenalty` | T170 |
| DungeonMatching.xml `<ClassPosition>` | the 13-class position table | `MatchComposition.ClassPositions` (RoleOf, CanFill) | T138c |
| DefaultSkillSet.xml `<Default>` | `DefaultSkillSet.Rows` (99 rows) | new-character skills | T18 |
| CreateCharData.xml `<Char>/<InitItem>` | `StarterInventory.BuiltInKits` (13 classes) | new-character items | T16 |
| CreateCharData.xml `<Char createdLevel>` (T162) | `StarterInventory.BuiltInCreatedLevels` (1, soulless 50) | new-character level (+ `WorldLevelSync` above 1) | T162 |
| BattleFieldData.xml `<RankingCompetition active="true">` | `LeaderBoardLivePvp` 10 26 30 37 | S_PVP_LEADER_BOARD_INFO | T133 |
| BattleFieldData.xml `<BattleField>` (T157 reader, listed T163) | none - missing sheet = no battleground | battleground tab + queue (`MatchComposition`) | T157 |
| DungeonRankRecorder_&lt;id&gt;.xml (file names) | `LeaderBoardLivePve` (8 live ids) | S_PVE_LEADER_BOARD_INFO | T133 |
| DungeonData_&lt;id&gt; / ContinentData / DungeonMatching / DungeonConstraint | `PostHandshakeDungeonIds` (98) | the 0x1581 fallback burst | handshake |
| EventMatching.xml `<Event type="Dungeon/BattleField"><Action type="matching">` per `<Target>` (T161b) | none - no sheet = no S_CHANGE_EVENT_MATCHING_STATE frame | `MatchQueueManager.EventMatchingFrames` (9739: 2154 92151; 9781: 2142 92139) | T161b |
| GuardData.xml `<Continent id><Guard id>` (T172) | the shipped 27 guards / 6 continents | `ArbiterClientHandlers.BuildUpdateVisitedSectionList` (0x1439 entry +12) | T172 |
| BuyMenuData.xml + BuyMenuData_*.xml `<BuyMenu resetType="day" resetTime>` (T172) | the 24 menus of cap_final2a/2b | `PurchaseLimitReset` (AS_RESET_PURCHASE_LIMIT) | T172 |

The first six reproduce their built-ins exactly from this server's sheets (tested). The two
board sets were copied from a live Classic+ server; from our sheets they are 10 26 30 37 38 and
the dungeons that have a rank recorder.

## Not loaded, and why

| Value | Where | Why |
|---|---|---|
| PoliticsData.xml ids (17), world instances (173) | inside the captured handshake burst | Captured frames, not a table; `HandshakeData` reproduces them in tests. |
| `DungeonClearCountRoster` (14 ids) | ArbiterClientHandlers | From classic_live2; the source sheet is not identified. |
| `GuildIncentiveCooldownSeconds` 82800 | DbProxyHandlers | A guess, not a copy. GuildConfig `<GuildIncentiveRate coolTimeDay="7">` is the likely source; unproven. |
| GuildWar.xml, EventMatching.xml, CityWar.xml | - | Nothing copied from them (T156 is capture-pinned; World reads EventMatching itself). |
| StrSheet_Item / ItemTemplate, ServerConfig.xml | ItemNames, WorldServerList | Already loaders. |

## Startup log (Program.cs is human-owned)

After `log.LogInformation("Auth provider: {Name}", Auth.Name);` add:

```csharp
DatasheetLoader.LoadAll(loggerFactory.CreateLogger("Datasheets"));
```

It logs `loaded X: N entries -> consumer` or `X not found, using built-in (...)` once per sheet.
Without it every sheet still loads on first use; only the startup lines are missing.
`--check-config` lists them either way (a `datasheets` section).
