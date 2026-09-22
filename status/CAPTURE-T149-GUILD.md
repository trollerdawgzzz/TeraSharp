# T149 - capture-enabling datasheet tweaks: guild war, guild quests, Civil Unrest

**Temporary and World-side.** These edits exist only to let two or three GM characters reach the
guild-war, guild-quest and Civil Unrest flows in one evening on the **real** ArbiterServer +
WorldServer, with both taps running (status/CAPTURE-PLAN.md, Session A). Apply before the session,
revert after. `tools/capture-tune.ps1` does both.

```powershell
# servers stopped
cd D:\v100\TERA_SERVER.100\TeraSharp\tools
.\capture-tune.ps1 -WhatIf          # preview: every edit, its original and its new value
.\capture-tune.ps1                  # apply (backups + SHA-256 manifest in Executable\capture-tune-backup)
# start Arbiter + World, run the session below, stop them
.\capture-tune.ps1 -Revert          # byte-identical restore, verified against the manifest
```

Options: `-Part GuildWar,GuildQuest,CityWar`, `-CityWarLeadMinutes 15`, `-CityWarPlayMinutes 30`,
`-BattleChipAllDay` (widen every BattleChipTime window to 00-24), `-QaServer` (flip
`DeploymentConfig.xml` `qaServer` - global, off by default), `-Force`.

---

## 1. The edits

All files are UTF-8 **with BOM** and CRLF; the script keeps both and changes only the attribute
value. Sheet paths are under `Executable\Datasheet\`. Every sheet here is listed in **both** the
Arbiter and the World `<Datasheet>` blocks of `ServerConfig.xml`, so both binaries read them at boot.

### 1a. Guild war

| sheet | element @ attribute | original | capture | what it gates |
|---|---|---|---|---|
| GuildConfig.xml | `GuildWarTime@combatReadyMin` | 5 | 1 | declare -> combat (min) |
| GuildConfig.xml | `GuildWarTime@surrenderMin` | 30 | 1 | earliest give-up after being declared on (min) |
| GuildConfig.xml | `GuildWarTime@protectiMin` | 1440 | 1 | no new declares on a guild that just gave up (min) |
| GuildConfig.xml | `GuildWarTime@reDeclareAbleMin` | 1440 | 1 | re-declare on the same guild (min) |
| GuildConfig.xml | `GuildWarTime@maintainCheckMin` | 60 | 1 | maintenance charge interval (min) |
| GuildConfig.xml | `GuildSize[rank=0]@declareCost` | 1500 | 100 | declare cost, small guild |
| GuildConfig.xml | `GuildSize[rank=0]@limitDeclareCost` | 15000 | 100 | declare cost past `declareLimitCount` (10) |
| GuildConfig.xml | `GuildSize[rank=0]@maintainCost` | 250 | 10 | per maintenance check |
| GuildConfig.xml | `GuildSize[rank=0]@limitMaintainCost` | 2500 | 10 | the same, past the limit |
| GuildConfig.xml | `GuildJoin@reJoinCoolTime` | 24 | 0 | hours before a character may join another guild |
| GuildWar.xml | `RuleVar@coolTime` | 72000 | 300 | same-guild declare cooldown (s) - the sheet's own `coolTimeQa` |
| GuildWar.xml | `RuleVar@preWarPeriod` | 1790 | 60 | pre-war wait (s) - `preWarPeriodQa` |
| GuildWar.xml | `RuleVar@warPeriod` | 72000 | 600 | war length (s) |
| GuildWar.xml | `RuleVar@declareWaitTime` | 1800 | 120 | time the declared guild has to answer (s) |
| GuildWar.xml | `KillPoint@winKillPoint` | 100 | 3 | kills to win |
| GuildWar.xml | `KillPoint@underScoreDraw` | 30 | 1 | final score under which it is a draw |
| GuildWar.xml | `Guild@minCountOfGuildMember` | 2 | 1 | members a guild needs to war |
| GuildWar.xml | `Acceptable@toggleCoolTime` | 14400 | 60 | accept-wars toggle cooldown (s) |
| GuildWar.xml | `LeaderBoard@warRestriction` | 8 | 2 | the sheet's `warRestrictionQa` |
| BattleChipData.xml | `Restriction@minUserAccount` | 30 | 1 | accounts a guild needs to declare or be declared on (chip war) |
| BattleChipData.xml | `Restriction@countLevel` | 65 | 1 | level a character needs for its account to count |
| GuildWar.xml *(opt.)* | every `BattleChipTime@openHour` / `@closeHour` | 22/18 and 00 / 24 and 02 | 00 / 24 | the battle-chip windows, only with `-BattleChipAllDay` |

The five `GuildWarTime` values are exactly the sheet's own commented-out QA line
(`<!-- GuildWarTime combatReadyMin="1" surrenderMin="1" protectiMin="1" reDeclareAbleMin="1"
maintainCheckMin="1" /-->`, just above the live one); the script skips comments, so the live line
is the one edited.

**Side finding for TeraSharp.** T80 left the declare limit (10), the cost (1500) and the
unexplained `250` in both `S_OPEN_GUILD_WAR_WINDOW` sides as constants "read from config or a
datasheet, nothing shows which". It is `GuildConfig.xml` `GuildSizeTable/GuildSize[rank=0]`:
`declareCost="1500"`, `declareLimitCount="10"`, `maintainCost="250"`. The Arbiter reads a DB
admin override first (`GuildWarManager::GetGuildWarDeclareCost`, set by `WA_CHANGE_GUILDWAR_DECLARECOST`)
and falls back to this row. The two cooldown getters (`GetGuildWarDeclareCoolTime`,
`GetGuildWarGiveUpCoolTime`) read two of the `GuildWarTime` minutes x 60, unless the QA override
from `/@guildwar_cooltime` is set.

### 1b. Guild quests

| sheet | element @ attribute | original | capture | why |
|---|---|---|---|---|
| GuildQuest\GuildQuestConfig.xml | `WeeklyLimit[guildSize=0]@balderionCoin` | 900 | 99999 | a full weekly cap refuses every new quest (`IsFullPoint`) |
| GuildQuest\GuildQuestConfig.xml | `ContributionPointconfig@weeklyLimit` | 1000 | 99999 | weekly contribution cap |
| GuildQuest\GuildQuestData.xml | `Quest[10001]/Collection@count` | 600 | 3 | gather 3 |
| GuildQuest\GuildQuestData.xml | `Quest[10002]/Npc@count` | 300 | 3 | kill 3 monsters |
| GuildQuest\GuildQuestData.xml | `Quest[10003]/Npc@count` | 15 | 1 | kill 1 boss |
| GuildQuest\GuildQuestData.xml | `Quest[10003]/condition@limitMin` | 720 | 5 | a 5-minute limit, to capture a timeout |
| GuildQuest\GuildQuestData.xml | `Quest[10004]/Fishing@count` | 50 | 1 | catch 1 |

The two guild-quest sheets live in the **`Datasheet\GuildQuest\`** subfolder, not beside the others.

**The membership time is not in any sheet.** `GuildQuestManager::CanRequestAcceptQuest` refuses a
member for whom `GuildQuestPointRewardManager::IsFirstSeasonUser` is true, which compares a
per-member date stamp; `MakeUsableSeasonUser` - behind `/@guild_quest_usable on` - backdates the
calling character's stamp by exactly 7 days. That is the "7 days in the guild" rule, and the GM
command is the switch: **each account runs it for itself**. Each normal quest can then be done
once a day (`IsTodayQuest`, reset at `dailyQuestResetHour="5"`); `/@reset_guild_quest` replays.

### 1c. Civil Unrest (`CityWar.xml`, league 1, continent 152)

| element @ attribute | original | capture | why |
|---|---|---|---|
| `OpenTime@day` | saturday | today (computed) | battle day |
| `OpenTime@startHour` | 20 | the first full hour >= now + 15 min | battle start, server local time |
| `OpenTime@playTimeMin` | 120 | 30 | battle length (min) |
| `DestroyTowerWhenUserCountIsBelow@active` | true | false | a guild with fewer than 4 inside loses its tower |
| `UseLimitEntryWithGuildPoint/GuildSize[rank=0]@amount` | 700 | 10 | entry cost for a small guild - lowered, not disabled, so the `SDB_REQUEST_CHECK_CITYWAR_ENTER_GUILD_QUEST_POINT` exchange still happens |
| `RankReward@stayingTimeInGuild` | 168 | 0 | hours in the guild before rank rewards count |

`advanceEntryTimeMin` stays 120, so with a start at most 75 minutes away the entry button opens on
the next World tick. World's `CityWar::CheckChangeCityWarState` runs state 1 -> 2 at
`start - advanceEntryTimeMin`, 2 -> 3 at `start`, 3 -> 4 after `playTimeMin`; it only leaves state 1
when **at least one day has passed since the previous season ended**, and it prefers an admin open
time over the sheet (`/@set_city_war_open_time`, stored in the DB - avoid it, the script cannot
revert it). **No minimum guild or participant count** exists in the sheet or in
`CityWarTemplate::PostValidate`'s attribute list beyond the tower rule and the entry gate above.

### 1c'. `-QaServer` (optional, off by default)

`DeploymentConfig.xml` `ArbiterServerConfig@qaServer` false -> true. Both binaries read a
`qaServer` flag, and the loaders above take each `*Qa` attribute only when a per-sheet QA byte is
set; that the byte comes from this flag is inferred, not traced. It is global (matching-rule team
sizes and the floating castle have `*Qa` values too), which is why the explicit edits are the
default. `DeploymentConfig.xml` holds the SQL passwords, and so does its backup copy - delete the
retired backup folder after `-Revert` if that matters.

### 1d. Guild level, points, money

No sheet edit needed - the Arbiter registers these QA commands (level 3; usage strings as
registered, behaviour not tested):

| command | usage | use |
|---|---|---|
| `/@add_guild_money` | `[coin]` | pays the declare cost and maintenance |
| `/@add_guild_point` | `[point]` | guild points |
| `/@add_guild_exp` | `[exp]` | guild exp (`GuildLevelTable`) |
| `/@guild_level` | `[level]` | set the guild level |
| `/@add_guild_contribution_point` | `[point]` | member contribution |
| `/@increase_guild_reward_point` | `[point]` | guild-quest reward points - probably the currency of the Civil Unrest entry gate and the weekly reward |
| `/@guild_quest_usable` | `[on/off]` | the 7-day member rule above, for the caller |
| `/@guild_quest_start` / `_complete` / `_clear` | `[questTemplateId]` | drive a quest without the field work |
| `/@reset_guild_quest` | - | replay quests the same day |
| `/@guildwar_cooltime` | `[minute]` | override of both war cooldowns (the Arbiter also has `UpdateGuildWarCoolTimeOnDb`, so it may persist - prefer the sheet) |
| `/@guildwar_immediate` | - | start every pending war now |
| `/@toggle_guildwar_acceptable`, `/@reset_guildwar_toggle_cool` | - | the accept-wars flag and its cooldown |
| `/@guild_cooltime` | `on/off` | whether the rejoin cooldown is checked at all |
| `/@battlechip_add`, `/@battlechip_pay` | - | battle chips for the chip war |

World-side: `/@start_citywar`, `/@end_citywar`, `/@disable_citywar_enter_limit`,
`/@guildwar_start [guild] [chips]`, `/@guildwar_accept [guild]`, `/@guildwar_raise [guild] [chips]`,
`/@guildwar_giveup [guild]`, `/@makeitem [id] [n]`, `/@makemoney [n]`. Several of the Arbiter ones
(`add_guild_point`, `guild_level`, `guild_quest_usable`, `guildwar_cooltime`, ...) are missing from
`GM-COMMANDS-ARBITER.md`; the registration table in the binary has them.

---

## 2. Before the session

1. Servers stopped; `.\capture-tune.ps1`; check the table - every row `set`, none `SKIPPED`.
2. **Load continent 152.** `ServerConfig.capture.xml` loads only 5, 7005 and 9827; Civil Unrest
   needs its own map. Use the full `ServerConfig.xml`, or add `<Continent id="152" />` to the
   capture list. If the Datasheet folder was trimmed (T63), restore 152's files first
   (`restore-datasheets.ps1`).
3. Both taps as in CAPTURE-PLAN.md A.0 (A<->W tap with link ids, client proxy log), wall clock noted.
4. **Accounts.** A and B, both GM (QA level 3) in tera-api. Character **GA** on A, **GB** on B,
   level >= 11 (`/@perfect_level 65` if you like). Optional third character **GC** on a third
   account, to be a second member of GA's guild - it is what makes guild broadcasts visible.
5. Guilds: GA creates guild **CapA**, GB creates **CapB** (`/@makemoney 2000000` first - creation
   costs 1,000,000). Then in each guild: `/@add_guild_money 100000`, `/@add_guild_point 1000`,
   and on every character `/@guild_quest_usable on`.

## 3. The session - actions and the packets to look for

Leave ~10 s between steps. Packet names are from the opcode table; which side sends which is what
the capture is for.

### A. Guild war (GA vs GB)

| # | who | action | expect |
|---|---|---|---|
| 1 | GA | open the war window | `C_OPEN_GUILD_WAR_WINDOW` / `S_OPEN_GUILD_WAR_WINDOW` - if T80's unexplained `+33` is `maintainCost`, it now reads 10 |
| 2 | GA | check CapB | `C_CHECK_TO_DECLARE_GUILD_WAR` / `S_CHECK_TO_DECLARE_GUILD_WAR` (cost 100) |
| 3 | GA | declare | `C_DECLARE_GUILD_WAR`, `S_NOTIFY_GUILD_WAR_STATUS_CHANGE`, `AS_DECLARE_GUILD_WAR` |
| 4 | GB | answer: counter-declare | `C_CHECK_TO_OPPOSITE_DECLARE_GUILD_WAR` / `S_...`, `C_OPPOSITE_DECLARE_GUILD_WAR`, `AS_OPPOSITE_DECLARE_GUILD_WAR` |
| 5 | - | wait 1 min | combat start: `AS_START_GUILD_WAR` / `SA_START_GUILD_WAR`, `S_START_GUILD_WAR`, `S_NOTIFY_GUILD_WAR_INFO` |
| 6 | both | GA kills GB three times | score traffic (`SA_GUILD_WAR_INFO`, `S_TOTAL_GUILD_WAR_DATA`), win at 3 |
| 7 | GA, GB | declare again; GB **gives up** after 1 min | `C_GIVE_UP_GUILD_WAR`, `SA_GIVE_UP_GUILD_WAR`, `AS_END_GUILD_WAR`, `S_END_GUILD_WAR`, then `C_REQUEST_GUILD_WAR_PENALTY_INFO` / `S_GUILD_WAR_PENALTY_INFO` |
| 8 | GA | re-declare **at once** (refused), then after 1 min (accepted) | the refusal message, then step 3 again |
| 9 | GA | tracking and teleport buttons | `C_REQUEST_GUILD_WAR_TRACKING_INFO`, `C_REQUEST_GUILD_WAR_TELEPORT` |
| 10 | GB | toggle accept-wars off, then on again at once (refused) and after 60 s | the toggle and its cooldown refusal |
| 11 | both | **chip war**: `/@battlechip_add` on both; if the UI offers accept / raise use it, otherwise `/@guildwar_start CapB 60`, `/@guildwar_accept CapA`, `/@guildwar_raise CapB 30`, `/@guildwar_giveup CapA` | `C_ACCEPT_GUILD_WAR`, `C_CHECK_TO_RAISE_GUILD_WAR` / `S_...`, `C_RAISE_GUILD_WAR`, `AS_RAISE_GUILD_WAR` / `SA_RAISE_GUILD_WAR`, `AS_RESTART_GUILD_WAR` |

Step 11 may need the battle-chip window (weekdays 22:00-02:00, weekends 18:00-02:00 server
time) - apply with `-BattleChipAllDay` if the session is outside it.

### B. Guild quests (in CapA; GC watching if present)

| # | who | action | expect |
|---|---|---|---|
| 1 | GA | open the board | `C_SHOW_GUILD_QUEST_LIST`, `S_SHOW_GUILD_QUEST_LIST` / `S_GUILD_QUEST_LIST` |
| 2 | GA | start 10002 (3 monsters) | `C_REQUEST_START_GUILD_QUEST`, `SA_`/`AS_REQUEST_START_GUILD_QUEST`, `S_START_GUILD_QUEST`, `S_UPDATE_GUILD_QUEST_STATUS` |
| 3 | GA | kill 3 monsters | `SA_GUILD_QUEST_EVENT_MESSAGE` x3, `S_UPDATE_GUILD_QUEST_STATUS` |
| 4 | GA | finish it | `C_REQUEST_FINISH_GUILD_QUEST`, `S_FINISH_GUILD_QUEST`, guild money / exp, `AS_UPDATE_GUILD_QUEST_POINT_INFO` |
| 5 | GA | start 10004 and **cancel** | `C_REQUEST_CANCEL_GUILD_QUEST` |
| 6 | GA | start 10003 and let 5 minutes pass | `S_FAIL_GUILD_QUEST` |
| 7 | GC | try to start a quest **without** `/@guild_quest_usable on` | the refusal (the 7-day rule) |
| 8 | GA | `/@increase_guild_reward_point 200`, claim the weekly reward | `C_GET_GUILD_QUEST_WEEKLY_REWARD`, `SA_`/`AS_REQUEST_GUILD_QUEST_WEEKLY_REWARD`, `SDB_`/`DBS_REQUEST_GUILD_QUEST_WEEKLY_REWARD_ITEM_TRANSACTION` |

Urgent quests (10005-10010, `S_NOTIFY_GUILD_QUEST_URGENT`) spawn in hunting zones 29 and 34,
which the capture continent list does not load - out of scope for this session.

### C. Civil Unrest (CapA vs CapB, 2-3 players)

| # | who | action | expect |
|---|---|---|---|
| 1 | - | the entry window opens (next World tick) | `SA_CHANGE_CITY_WAR_STATE`, `SDB_`/`DBS_CHANGE_CITY_WAR_STATE`, first season: `SDB_`/`DBS_CREATE_NEW_CITY_WAR_LEAGUE`, `S_CHANGE_CITY_WAR_STATE` |
| 2 | GA, GB | enter from the UI | `SA_REQUEST_ENTER_CITY_WAR`, `SDB_`/`DBS_REQUEST_CHECK_CITYWAR_ENTER_GUILD_QUEST_POINT` (gate 10), `AS_REQUEST_ENTER_CITY_WAR`, `SA_`/`AS_TELEPORT_CITY_WAR`, `S_ENTER_CITY_WAR` |
| 3 | GA | build a tower (`/@makeitem 9301 1` first - `CityWarGuildTower.xml`) | the tower build and the Arbiter's guild-tower DB insert |
| 4 | GB | open the map | `C_REQUEST_CITY_WAR_MAP_INFO`, `S_REQUEST_CITY_WAR_MAP_INFO` / `_DETAIL` / `_END` |
| 5 | - | battle starts | state 3 |
| 6 | both | fight, attack the tower, die once | `S_CITYWAR_WARNING_UNDERATTACK` / `SA_CITYWAR_WARNING_UNDERATTACK`, `SA_REWARD_CITYWAR_KILL_DEATH_COUNT`, `S_SHOW_DEAD_CITY_WAR_UI`, `S_CITY_WAR_NPC_INFO` / `_KILL` |
| 7 | GB | leave and re-enter | `S_LEAVE_CITY_WAR`, step 2 again; `SA_`/`AS_REQUEST_ENTER_CITY_WAR_GUILD_TOWER_BY_ITEM` if it offers the tower |
| 8 | - | after 30 min | `SDB_`/`DBS_RESULT_CITY_WAR`, reward mail, `SA_BROADACST_CITY_WAR_SEASON_HISTROY` |
| 9 | GA | open the history | `C_CITY_WAR_SEASON_HISTROY` / `S_...`, `C_CITY_WAR_INFO_HISTROY` / `S_...`, `S_OPEN_CITY_WAR_SEASON_HISTORY` |

A second Civil Unrest run needs a day's gap after this one ends (the DiffDay check).

## 4. After

Stop both servers, `.\capture-tune.ps1 -Revert` (it refuses to overwrite a sheet that changed
since tuning, and checks every restored file against its original SHA-256), then reframe both
logs as CAPTURE-PLAN.md A.0 step 7 describes. GM-command effects that live in the DB - guild
money, points, level, quest state, the chip balances - are not reverted by the script; they
belong to the throwaway capture guilds.
