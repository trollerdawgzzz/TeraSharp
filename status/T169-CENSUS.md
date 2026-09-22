# T169 - real-Arbiter census and pins (cap_final2a/2b, 2026-09-22)

Sources: `<captures>\cap_final2a.log` + `cap_final2b.log` (A<->W taps), `cap_final2_clients\*.log`
(9 client connections). Status is cowork/T8 at T169: DbProxy/DbAck/Party/Guild/Contract probe plus a
source scan (a const with no use counts as "never sent"). Frame counts are both taps together.

## Counts

| | distinct | TeraSharp |
|---|---|---|
| W->A | 163 | 119 own handler, 24 bridge / replay table, **20 none** |
| A->W | 224 | 154 sent, **70 never sent** |
| client C->S | 134 | 38 Arbiter-owned handled, 72 forwarded, **24 Arbiter-owned, no handler** |
| client S->C | 330 | 209 via 0x13F7, 76 built here, **45 neither** (may ride another World frame) |

## Pinned vs decompile-only (this session's share)

| system | real pairs | status |
|---|---|---|
| T167 SKILL_POLISHING_UPGRADE_LEVEL 0x2977/0x2978 | 180 | **pinned**: [DlmId][ok 1], request DlmId@8 User@12 Polishing@16 Effect@20 RequiredPoint@24 (=1 every time); T167's guess holds, no fix |
| T167 UNLOCK 0x2971/72, CHANGE 0x2973/74 | 6 + 2 | **pinned** (RequiredPoint@28 = 0 in all six) |
| T167 LOAD 0x2975/0x2976, non-empty | 1 (50067/50068, 191 B) | **pinned**: element layout + applied flags after the real unlocks/changes |
| Tooltip compare 0x282E/0x282F -> S_SHOW_ITEM_TOOLTIP | 117 (94 in 2b) | **pinned** (below); implementation is Session 2's - reference patch `T169-TOOLTIP-REFERENCE.diff` |
| 0x1439 AS_UPDATE_VISITED_SECTION_LIST | 52 | **pinned (T172)**: 16-B entries, 956 / 741 byte-exact |
| 0x1453 / 0x1631 | 88 / 48 | **pinned (T172)**: builders byte-exact; 0x1453 send is `T172-PATCH.diff` |

## 0x282E / 0x282F byte pin

- **Direction is the reverse of the brief.** 0x282E DBS_SIMULATE_ITEM_TOOLTIP is the *Arbiter's ask*,
  0x282F SDB_SIMULATE_ITEM_TOOLTIP is *World's answer*. User::AskItemCompareTooltip (Arb_part_067.c:3671).
- Ask (A->W, 32 B): `[u64 gameId & 0x7FFF..][i32 ToolTipType][i64 ContentId][i64 ItemDbId][i32 owner char]`
  (2b 3954: `0100f00a00800000 15000000 0000000000000000 5927000000000000 eb030000`).
  Sent instead of the tooltip when: own item, type != 27, template's equipment part maps to a worn slot
  that is occupied (ear 6->7, finger 8->9 fallback), and for type 21 not when the item itself is worn
  (InvenType 14, pos != 0). Part -> slot: weapon 1, body 3, hands 4, feet 5, ear 6, finger 8, neck 10,
  underwear 11, hair acc 12, belt 19, brooch 20, 0x15/0x16 -> 22/23; face/style: no ask.
- Answer (W->A): `[u32 0x12 (frame-relative)][u32 0x150][u32 char][CompareItemToolTip 0x150]`; record: type@0,
  worn item@0x10, worn tpl@0x18, hovered item@0x20, hovered tpl@0x28, 15 x (set0,set1) i32 stats@0x2C,
  f32 slot item level@0x140, char@0x14C. The Arbiter then sends the tooltip for (char, item) with it.
- S_SHOW_ITEM_TOOLTIP real shape (930 B with an empty bound owner): after the string, 2 option sets and
  2 compare sets interleaved and linked `[opt0 0x1C+15x8][cmp0 0xA4][opt1][cmp1]`. Record-sourced:
  Enchant rec+0x28, Durability +0x2C, IsBound +0x34, passives +0x54/+0x90 (15 i32 each), grade +0x134 ->
  0xC0. With the answer: 0x5F = 1, 0xC5 = record+0x140, compare stats from +0x2C; without: stats -1000
  (-1000f at 6-8, 13-15) and 0xFE-filled i64s. Sentinels 0xE5 -1, 0xED -2 (RemainPeriodInSec for period
  items), 0xF5/0xF9 -1, 0x111 -1, 0x115 -1.
- Byte-exact: all 59 non-equipment tooltips (the Forge material picker). Equipment differs only in the
  option sets' three item-level floats and CumulatedEnchantAmount / DecompositionCost - EquipmentItemLevel
  data, not among our datasheets (needs an export from the datacenter). Period items: RemainPeriodInSec.
- The Forge material picker's hovers are these tooltips (e.g. 89771, 10106 in the pin). TeraSharp
  answered them with every array empty (count 0); whether the picker also needs anything beyond the
  tooltip is not settled here.

## SA_/AS_ names (all A->W)

- **0x1439 AS_UPDATE_VISITED_SECTION_LIST** - sent (HandlerRegistry on C_LOAD_TOPO_FIN), but
  `BuildUpdateVisitedSectionList` writes 12-B entries; the real VisitedSectionInfo is 16 B
  `[mapId][guardId][sectionId][WorldDataSheet[guardId]+4]` (FUN_14035f5e0, FUN_1403c95f0; capture: guard 25 -> 5,
  guard 1 -> 1). Any non-empty list was misread by World. **T172**: fixed; the 4th is GuardData.xml's
  `<Continent id>` of the guard (the sheet the real WorldDataSheet reads; 1 and 25 pinned).
- **0x1453 AS_UPDATE_GUILD_QUEST_POINT_INFO** - **T172**: sent (via `T172-PATCH.diff`); `[guildId][0][900][0]`. Real: in every load-topo burst after
  0x14AF, `[i32 2|3][i32 0][i32 900][i32 0]`.
- **0x1631 AS_RESET_PURCHASE_LIMIT** - **T172**: sent. Correction: the i32 is a BuyMenu id, not a character -
  the 24 are exactly the `resetType="day"` menus of BuyMenuData*.xml; 09:00:54 was the first check after start.

## Client S->C neither tunnelled via 0x13F7 nor built here

S_ABNORMALITY_FAIL (148), S_ABNORMALITY_SCALE_UP (4), S_AIR_REACTION_END (8), S_ATTENDANCE_EVENT_REWARD_COUNT (36), S_BOSS_BATTLE_INFO (2), S_BROADCAST_FLOATING_CASTLE_NAMEPLATE (52), S_CHANGE_CITY_WAR_STATE (2), S_CHECK_TO_OPPOSITE_DECLARE_GUILD_WAR (1), S_COMPLETE_VOTE (2), S_CONFIRM_INVITE_CODE_BUTTON (11), S_CREATURE_LIFE (8), S_CUSTOM_MESSAGE (37), S_DEAD_LOCATION (4), S_DECO_UI_INFO (11), S_DESPAWN_PROJECTILE (5), S_DIALOG_CLOSE (6), S_DUNGEON_GUIDE_OFF (4), S_DUNGEON_GUIDE_ON (8), S_END_CHAT_BAN (6), S_EP_SYSTEM_DAILY_EVENT_EXP_ON_OFF (8), S_EVENT_GUIDE (2), S_GUILD_WAR_PENALTY_INFO (1), S_HUNTING_ZONE_EVENT_LIST (38), S_INVITE_CODE_EXPIRE_TIME (6), S_NOTIFY_TO_FRIENDS_WALK_INTO_SAME_AREA (8), S_PARTY_MEMBER_ABNORMAL_ADD (582), S_PARTY_MEMBER_ABNORMAL_CLEAR (2), S_PARTY_MEMBER_ABNORMAL_DEL (544), S_PARTY_MEMBER_ABNORMAL_REFRESH (108), S_PARTY_MEMBER_BUFF_UPDATE (54), S_PARTY_MEMBER_CHANGE_HP (1003), S_PARTY_MEMBER_CHANGE_MP (451), S_PARTY_MEMBER_CHANGE_STAMINA (402), S_PARTY_MEMBER_CHARM_RESET (2), S_PARTY_MEMBER_INTERVAL_POS_UPDATE (1013), S_PARTY_MEMBER_STAT_UPDATE (2871), S_RESET_CHARM_STATUS (2), S_SEND_PARTY_NAME_LIST (11), S_SHOW_DUNGEON_RETRY_UI (2), S_SPAWN_PROJECTILE (5), S_UPDATE_FRIENDSHIP_GAGE (2), S_UPDATE_GUILD_QUEST_STATUS (9), S_USER_DEATH (4), S_USER_PAPERDOLL_INFO (1), S_VOTE_RESET_ALL_DUNGEON (2)

## T172 - every row resolved

The T169 lists overstated the gaps: the client flags missed ArbiterOwned/accept-silently dispatch, and
46 of the 70 "never sent" A->W frames go out in `data/handshake_burst.bin` (the World-connect replay),
7 more as replay-table answers (arb_world.log). Client ops with no Arbiter handler and no 0x13F6 forward
were in a dungeon (that link is not in these taps). Tags: **T172** implemented + test; handled; burst;
replay; not needed (reason); deferred (a feature that needs its own task).

### W->A (20)

| op | name | frames | real Arbiter | resolution |
|---|---|---|---|---|
| 0x13A9 | SA_ADD_TO_INTER_PARTY_MATCH_POOL | 24 | pool add after a forwarded C_MATCH_ADD | not needed: TeraSharp owns C_MATCH_ADD (MatchQueueManager), so World never sends it |
| 0x1437 | SA_BROADCAST_SYSTEM_MESSAGE_TO_WHOLE_WORLD_NOT_IN_SPECIAL_PLACE | 1 | BroadcastSystemMessageToWholeWorldNotInSpecialPlace | **T172**: S_SYSTEM_MESSAGE to every player (with 0x1436) |
| 0x1445 | SA_TELEPORT | 6 | UpdateSysReturnLoc + move to another server (/@enter_dungeon) | not needed for play: GM /@enter_dungeon only; normal entry (0x13BE/0x13C0) is handled |
| 0x144C | SA_REGISTER_WORLD_COMMAND | 5 | ArbiterCommandDistributor::RegisterWorldCommands (55 KB table) | not needed: TeraSharp's GmCommands routes /@ commands itself |
| 0x144F | SA_GUILD_QUEST_EVENT_MESSAGE | 30 | guild-quest event to the guild | deferred: lands with guild-quest progress |
| 0x1467 | SA_CREST_USE_LIST | 4 | User::CrestApplyList -> 0x1468 | **T172**: [ref][0][DlmId][ok] ack (applied set not stored) |
| 0x14BE | SA_GATHER_USER_LIST_BY_DISTANCE | 1 | GM users-by-distance answer | not needed: C_ADMIN_GET_USER_INFO_LIST_BY_DISTANCE is answered by TeraSharp |
| 0x14C0 | SA_REG_PERIOD_ITEM | 1 | PeriodItemManager registers a timed item | deferred: period-item expiry (with 0x14C1/0x14C2) |
| 0x14C2 | SA_PERIOD_ITEM_EXPIRED_RESULT | 2 | period-item delete result | deferred: period-item expiry |
| 0x1558 | SA_LOAD_POLITICS_UNIT | 5 | World::SendPoliticsUnit -> 0x1559 | replay (arb_world.log pair) |
| 0x158A | SA_ADMIN_HOLD_CHARACTER | 1 | User::SetHoldCharacter -> 0x158C | not needed for play: GM hold (T107 dropped S_ADMIN_HOLD_CHARACTER) |
| 0x159E | SA_LOAD_FLOATINGCASTLE | 5 | FloatingCastleManager loads -> 0x159F | replay |
| 0x15BC | SA_SYNC_DATE_TIME | 5 | DateTimeSync::CheckDateTime, no reply | **T172**: accepted silently |
| 0x1600 | DSA_ADMIN_GET_DUNGEON_USER_LIST | 2 | GM dungeon user list answer | not needed: C_ADMIN_GET_DUNGEON_USER_LIST is answered by TeraSharp |
| 0x161E | SA_GIVE_FIELD_EVENT_CLEAR_REWARD | 8 | FieldDataSheet::SendFieldEventClearReward | deferred: field events not modelled |
| 0x162C | SA_REQUEST_AWESOMIUM_URL | 10 | AwesomiumUrlManager::GetUrl -> 0x162D | replay |
| 0x282F | SDB_SIMULATE_ITEM_TOOLTIP | 117 | AnswerItemCompareTooltipLock | Session 2 (T169-TOOLTIP-REFERENCE.diff); EquipmentItemLevelData.xml exists in Datasheet |
| 0x2848 | SDB_REQUEST_SEND_PAPERDOLL | 1 | builds S_USER_PAPERDOLL_INFO (inspect a player, 977 lines) | deferred: inspect-player needs its own task |
| 0x2849 | SDB_REQUEST_SEND_QUESTJOURNAL | 2 | User::SendAdminQuestJournal | not needed for play: GM quest journal |
| 0x294E | SDB_LOAD_CITY_WAR_SEASON_INFO | 5 | CityWarManager::LoadCityWarSeasonInfo -> 0x2953 | replay |

### Client C->S (24)

| op | name | frames | resolution |
|---|---|---|---|
| 0x787A | C_CHECK_TO_DECLARE_GUILD_WAR | 2 | handled (GuildWarManager, T80) |
| 0x9428 | C_CHECK_TO_OPPOSITE_DECLARE_GUILD_WAR | 1 | deferred: guild-war acceptance |
| 0xD366 | C_DECLARE_GUILD_WAR | 1 | handled (T80) |
| 0x82E2 | C_EVENT_GUIDE | 2 | handled (accepted silently) |
| 0x584B | C_GET_USER_GUILD_LOGO | 10 | handled (GuildHandlers) |
| 0xF5C5 | C_GIVE_UP_GUILD_WAR | 1 | deferred: guild-war surrender |
| 0x9092 | C_HARDWARE_INFO | 4 | not needed: RegNoop |
| 0xF7B8 | C_MATCH_PROGRESS | 70 | handled (MatchWiring) |
| 0x9822 | C_NOTIFY_LOCATION_IN_REACTION | 2 | not needed: no Arbiter handler; forwarded (dungeon link, not tapped) |
| 0x94F7 | C_OPEN_GUILD_WAR_WINDOW | 15 | handled (T80) |
| 0xEBBB | C_OPPOSITE_DECLARE_GUILD_WAR | 1 | deferred: guild-war acceptance |
| 0xA6D1 | C_REQUEST_CANCEL_GUILD_QUEST | 2 | handled (GuildHandlers) |
| 0x5E22 | C_REQUEST_GAMESTAT_PING | 2 | not needed: no Arbiter handler |
| 0x5B51 | C_REQUEST_GUILD_INFO | 9 | handled (ArbiterOwned) |
| 0xD3D8 | C_REQUEST_GUILD_WAR_PENALTY_INFO | 1 | deferred: with guild-war surrender |
| 0x77A9 | C_REQUEST_START_GUILD_QUEST | 3 | handled (GuildHandlers) |
| 0x5867 | C_RESET_ALL_DUNGEON | 1 | deferred: reset vote (accepted silently) |
| 0xD19B | C_REVIVE_NOW | 2 | not needed: no Arbiter handler; forwarded (dungeon link) |
| 0x5DE2 | C_SET_SERVANT_SEQUENCE | 3 | not needed: no Arbiter handler; RegNoop |
| 0xE7EF | C_SET_VISIBLE_RANGE | 8 | handled (accepted silently, T158) |
| 0xE282 | C_TEL_CAMP | 7 | deferred: tel-camp list (0x2832) |
| 0xB76C | C_UPDATE_CONTENTS_PLAYTIME | 40 | handled (accepted silently) |
| 0xF783 | C_VOTE_RESET_ALL_DUNGEON | 1 | not needed: no Arbiter handler; forwarded (dungeon link) |
| 0xFD66 | C_WITHDRAW_GUILD_WAR | 1 | handled (T80) |

### A->W never sent (70)

**burst (46):** 0x13C7, 0x149D, 0x149E, 0x149F, 0x14B3, 0x14D0, 0x14E1, 0x14E2, 0x14E6, 0x14E9, 0x14EC, 0x14EE, 0x14F2, 0x14F3, 0x14F7, 0x150A, 0x150D, 0x150E, 0x1510, 0x1511, 0x1529, 0x1556, 0x1567, 0x156B, 0x157E, 0x157F, 0x1589, 0x15B6, 0x15C2, 0x15C5, 0x15D4, 0x15D5, 0x1603, 0x1609, 0x160D, 0x160F, 0x1613, 0x161D, 0x1623, 0x162E, 0x28E3, 0x28E5, 0x28E6, 0x28EB, 0x28F8, 0x29E2.

**replay (7):** 0x1559 AS_POLITICS_UNIT_INFO, 0x159F AS_LOAD_FLOATINGCASTLE, 0x15FB AS_ADD_VILLAGER_DESPAWN, 0x162D AS_RESPONSE_AWESOMIUM_URL, 0x164C AS_REQUEST_WORLD_SERVER_STATUS, 0x2952 DBS_LOAD_CITY_TOWER_AND_PLAYER_INFO, 0x2953 DBS_CITY_WAR_INFO.

| op | name | frames | real Arbiter | resolution |
|---|---|---|---|---|
| 0x13B9 | AS_RESET_ALL_DUNGEON | 1 | party reset vote result (C_RESET_ALL_DUNGEON) | deferred: dungeon reset vote (C_RESET_ALL_DUNGEON is accepted silently) |
| 0x1444 | AS_CHAR_LOC | 4 | Handler_SA_CHAR_LOC, target on the GM's World | **T172**: [gm gameId][target gameId]; T161b's 0x2825 kept for another World |
| 0x1453 | AS_UPDATE_GUILD_QUEST_POINT_INFO | 88 | GuildQuestPointRewardManager::SendPointInfoToWorld | **T172**: builder + `T172-PATCH.diff` (HandlerRegistry, after 0x1439) |
| 0x1468 | AS_CREST_USE_LIST | 4 | reply to 0x1467 | **T172** |
| 0x14A4 | AS_OPPOSITE_DECLARE_GUILD_WAR | 2 | GuildWarManager::OppositeDeclareGuildWar | deferred: guild-war acceptance |
| 0x14B1 | AS_WITHDRAW_GUILD_WAR | 2 | GuildWarManager::WithdrawGuildWar | deferred: T80 answers the client only; the World push is unpinned |
| 0x14BD | AS_GATHER_USER_LIST_BY_DISTANCE | 1 | GM users-by-distance ask | not needed (GM; answered locally) |
| 0x14C1 | AS_PERIOD_ITEM_EXPIRED | 2 | PeriodItemManager::RequestDeletePeriodItem | deferred: period-item expiry |
| 0x1516 | ABS_ASK_ENTERABLE_BATTLE_FIELD_EXISTS | 4 | OnLoadTopoFin asks the BG server | not needed until battlegrounds (not captured) |
| 0x158C | AS_ADMIN_HOLD_CHARACTER | 2 | GM hold push | not needed for play (GM) |
| 0x15FF | ADS_ADMIN_GET_DUNGEON_USER_LIST | 2 | GM dungeon user list ask | not needed (GM; answered locally) |
| 0x1631 | AS_RESET_PURCHASE_LIMIT | 48 | StoreBuyLimitManager::SendResetLimitedStore | **T172**: day menus of BuyMenuData*.xml, minute timer |
| 0x27C6 | DBS_SYSRETURN_POSITION | 6 | UpdateSysReturnLoc before 0x13C1 (all 6 the clear form) | deferred: return-point clear on dungeon entry; World's use unpinned |
| 0x2828 | AS_ADMIN_REQUEST_SEND_INFO | 3 | GM user info | not needed for play (GM) |
| 0x282E | DBS_SIMULATE_ITEM_TOOLTIP | 117 | AskItemCompareTooltip | Session 2 (tooltip) |
| 0x2832 | AS_CAMP_TEL_ADDED | 5 | C_TEL_CAMP -> visited camp list | deferred: tel-camp list (which ids count is unsettled) |
| 0x295A | DBS_CHANGE_CITY_WAR_STATE | 4 | reply to 0x2958 (was sealed one-way) | **T172**: [a][ok 1][b][state], 4 pairs |

### Also fixed in T172

- **Cinematics**: `characters.id` is reused after a delete and `visited_sections` (plus 13 other
  per-character tables with no FK) kept the old rows, so a new character answered isFirstVisit 0.
  Purged on delete and on create (`CharacterStore.CharacterStateTables`); S_VISIT_NEW_SECTION pinned
  to cli_2026-09-22T09-25-01 2303 (first visit 1) and 09-24-54 (0).
- **0x1439**: 16-byte entries, 4th = GuardData.xml continent (`DatasheetLoader.GuardContinents`).
- **0x1436** whole-world broadcasts now reach players (was dropped); **0x2958** answered.
