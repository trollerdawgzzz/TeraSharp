# GUILD-DESIGN — guilds are Arbiter-owned AND Arbiter-persisted, and World only gets a mirror

Research for T36. Sources: the ArbiterServer 100.02 decompile
(`D:\v100\TERA_SERVER.100\Arb_part_0*.c`), the `.def` files in `tera_v100_MASTER_FINAL`,
`tera-server-proxy/data/data.json` map `376012`, `status/arbiter_c_handlers.txt`,
`data/dbproxy_opcodes.txt`, and the one guild frame that exists in a capture:
`data/cap_guild.bin`.

Companion to `status/PARTY-DESIGN.md`. Read that first — the two systems are deliberately
contrasted throughout, and the framing conventions (frame-relative vs packet-relative offsets,
ref slots, the `SendToSession<PDL::PKT_*_WRITE,...>` trick) are explained there.

**No guild has ever been created against the tap.** Five guild opcodes appear across all four
captures — `0x27CF`, `0x27ED`, `0x2954`, `0x2955`, `0x14B3` — and all five are boot-time init
with an empty answer. Everything below that is not marked *(capture)* is decompile-derived.

---

## 0. The headline

| | party | guild |
|---|---|---|
| lives in | Arbiter RAM only | Arbiter RAM **and** SQL |
| persisted by | nobody | the Arbiter itself, ~109 stored procedures |
| World's copy | the full mirror (`AS_DO_*`) | a read-only mirror pushed at boot + deltas |
| client packets | Arbiter answers most | **split**: 17 Arbiter, 10 World |
| fan-out | `Party::BroadcastPacket`, one function | inlined at every call site, no single helper |
| `.def` quality | 7 files wrong | 10 files wrong, but the two big ones are right |

Two consequences for TeraSharp:

1. **A `GuildManager` cannot be RAM-only.** Parties vanish on restart and nobody notices;
   a guild that vanishes is a bug report. The SQLite schema in §3.3 is the minimum.
2. **The client surface is split, so a `GuildManager` must not answer everything.** Ten guild
   `C_` packets have no Arbiter handler at all — `C_LEAVE_GUILD`, `C_BANISH_GUILD_MEMBER`,
   `C_DESTROY_GUILD`, `C_CHANGE_GUILD_CHIEF`, the four `*GUILDGROUP*` ones, and the two
   name-check ones. WorldServer handles those and then asks the Arbiter with an `SA_` frame.
   Answering one in the Arbiter as well means the client gets two answers.
   `GuildPackets.ArbiterHandlesClientPacket(op)` is that list in code.

---

## 1. Where the code is

| thing | function | file:line |
|---|---|---|
| `GuildManager::LoadManager` (boot, `spLoadAllGuild`) | `FUN_1407f…` | `Arb_part_069.c:14822` |
| `GuildManager::LoadInitGuild` (answer `SDB_INIT_GUILD`) | `FUN_1407f8210` | `Arb_part_069.c:13407` |
| `GuildManager::CreateGuildData` (`spCreateGuild`) | | `Arb_part_069.c:3945` |
| `GuildManager::LoadAllGuildMemberData` | | `Arb_part_069.c:12059` |
| `GuildManager::LoadGuildGroup` | | `Arb_part_069.c:13127` |
| `Guild::AddUserToGuildMemberNoLock` | | `Arb_part_045.c:17809` |
| `Guild::RemoveGuildMemberNoLock` (`spLeaveGuildMember`) | | `Arb_part_046.c:10587` |
| `Guild::UpdateGuildLogo` | `FUN_140580730` | `Arb_part_046.c:15532` |
| `Guild::UpdateGuildAnnounce` | | `Arb_part_046.c:14742` |
| `Guild::SendGuildInfoToUser` (`S_GUILD_INFO` array half) | `FUN_14057bf50` | `Arb_part_046.c:12624` |
| `Guild::SendGuildMembersToUser` | `FUN_14057caa0` | `Arb_part_046.c:13345` |
| `GuildJoinManager::SendGuildApplyList` | `FUN_140839a30` | `Arb_part_072.c:1790` |
| `Handler_SDB_CREATE_GUILD2` | | `Arb_part_072.c:15278` |
| `Handler_SDB_ADD_GUILDMEMBER2` | | `Arb_part_072.c:15070` |
| `Handler_SA_LOAD_GUILD` | | `Arb_part_072.c:14167` |
| `GuildLog::AddLog` (`spCreateGuildLog`) | | `Arb_part_045.c:17211` |

The opcode→name switch for the whole client protocol is `FUN_140068890`, `Arb_part_003.c:4452`
(2705 `case 0xNNNN: return "NAME";` arms). That table is how §5's opcodes were cross-checked.

---

## 2. The `Guild` object

`sizeof(Guild) = 0x24A0`.

| offset | member |
|---|---|
| `+0x58` | `std::map<int, GuildMemberData>` — members, element **0xF0** |
| `+0x68` | `std::set<User*>` — logged-in members, node 0x28. **This is the fan-out list.** |
| `+0x78` | `std::vector<GuildGroupData>` — ranks, element **0x28** |
| `+0x88` | the embedded **`GuildData`**, 0x23A0 bytes |
| `+0x2460` | `std::vector<GuildPerk>`, element 0x0C |

There is **no `Guild::BroadcastPacket`.** Every guild broadcast inlines its own walk of
`Guild+0x68`. `SA_BROADCAST_SYSTEM_MESSAGE_TO_GUILD` (0x1438) is World asking the Arbiter to do
one — the reverse of the party case, where World owns nothing and the Arbiter has one helper.

### 2.1 `GuildData` — 0x23A0 bytes, the thing that crosses the wire

Size is not inferred: `Guild::SetGuildAddAccountLimitValue` broadcasts it by explicit length —

```c
  FUN_14001bdc0(local_50,"void __cdecl Guild::BroadcastGuildData(struct GuildData &)",0);
  local_68[0] = 0x23a0;
  local_60 = (undefined4 *)(param_1 + 0x88);
```

Offsets are blob-relative. Names come from the `spLoadAllGuild` column binds and the named
`Guild::` accessors; the ones marked *(capture)* are additionally confirmed by the empty
`DBS_INIT_GUILD_DATA` in `data/cap_guild.bin`, whose only non-zero bytes land exactly here.

| off | type | name | sp col |
|---|---|---|---|
| `0x0000` | i32 | GuildDbId | 1 |
| `0x0004` | wchar[37] | GuildName | 2 |
| `0x0050` | i32 | ChiefDbId | 3 |
| `0x0054` | TIMESTAMP(16) | GuildCreateDate | 4 |
| `0x0064` | i32 | GuildLevel *(capture: 1)* | 5 |
| `0x0068` | i64 | GuildExp | 6 |
| `0x0070` | i64 | GuildPoint | 7 |
| `0x0078` | i64 | GuildMoney | 8 |
| `0x0080` | TIMESTAMP | LastIncentiveTime *(capture: 1970-01-01)* | 28 |
| `0x0090` | wchar[201] | GuildAnnounce | 9 |
| **`0x0222`** | — | **2 B alignment hole** | — |
| `0x0224` | i32 | RecommendationPoint | 10 |
| `0x0228` | i32 | *(loaded, never read)* | 11 |
| `0x022C` | wchar[15] | GuildTitle | 12 |
| **`0x024A`** | — | **2 B alignment hole** | — |
| `0x024C` | i32 | GuildLogoLength | — |
| `0x0250` | i32 | GuildLogoId | 14 |
| `0x0254` | byte[8000] | GuildLogo (raw image) | 13 |
| `0x2194` | wchar[201] | GuildPromotion (recruit blurb) | 15 |
| `0x2326` | u8 | NeedChangeGuildName | — |
| `0x2327` | u8 | IsGuildWarAcceptable | 16 |
| `0x2328` | TIMESTAMP | GuildWarAcceptableToggleTime *(capture: epoch)* | 17 |
| `0x2338` | i32 | GuildGeneralCoin | 19 |
| `0x2340` | i32 | ForeverEmblemId | 20 |
| `0x2344` | i32 | EmblemId | 21 |
| `0x2348` | TIMESTAMP | *unnamed* *(capture: epoch)* | — |
| `0x2358` | i32 | GuildPreference | 22 |
| `0x235C` | i32 | JoinMinLevel *(capture: 1)* | 23 |
| `0x2360` | i32 | JoinMaxLevel *(capture: 70)* | 24 |
| `0x2364` | i32 | GuildJoinType *(capture: 1)* | 25 |
| `0x2368` | i64 | LastWeekPlayTime | 26 |
| `0x2370` | i64 | ThisWeekPlayTime | 27 |
| `0x2388` | i32 | LordCandidacyParcelGeneration | 18 |
| `0x238C` | i32 | GuildAddAccountLimitValue | — |

Three fixed buffers account for 8804 of the 9120 bytes: GuildAnnounce (402), the logo blob
(8000) and GuildPromotion (402). **There are no arrays of records in `GuildData`** —
`MemberCount`, `AccountCount`, `MaxAccountCount` and `GuildSize` are all computed live
(`MaxAccountCount = GuildConfigDataSheet+0x20 + GuildData+0x238C`; `GuildSize` from
`GuildLevel`), `PolicyPoint` comes from a separate manager, and quest points live in their own
tables.

#### The two padding holes — and a real bug in what TeraSharp replays today

`0x0222` and `0x024A` are the two-byte alignment holes after `GuildAnnounce` and after
`GuildTitle`. The real Arbiter ships a **default-constructed** `GuildData` without clearing
them, so they carry whatever was on the stack. They are also **the only bytes that differ
between the four captures** of the empty `DBS_INIT_GUILD_DATA`:

```
  arb_world    @0x222 = 0000   @0x24A = B379
  lobby_tap    @0x222 = FAB7   @0x24A = E9C9
  cap_newchar  @0x222 = 0000   @0x24A = 0000
  09-13        @0x222 = 0000   @0x24A = 0000
```

TeraSharp replays `arb_world.log`'s copy verbatim, so **it currently leaks `0xB379` at blob
`0x024A` on every login**. World never reads those bytes, so nothing breaks, but it is
non-deterministic output for no reason. `GuildPackets.BuildEmptyGuildDataBlob()` writes zeros
there; `GuildPackets.GuildDataPaddingHoles` names them. Swapping the replay entry for the
builder is a one-line change and is **not** made here (T36 was scoped "no GuildManager") —
see §8.

### 2.2 `GuildMemberData` — 0xF0 bytes

| off | type | name |
|---|---|---|
| `0x00` | i32 | UserDbId *(the map key)* |
| `0x04` | wchar[37] | Name |
| `0x50` | i32 | WorldId |
| `0x54` | i32 | GuardId |
| `0x58` | i32 | SectionId |
| `0x5C` | i32 | GuildGroupId *(the rank; the authority check keys off this)* |
| `0x60` | i32 | UserLevel |
| `0x64` | i32 | Race |
| `0x68` | i32 | UserClass |
| `0x6C` | i32 | Gender |
| `0x70` | i32 | State — 0 online / 2 offline, **forced to 2 on load, not persisted** |
| `0x74` | i32 | WeeklyContributionPoint |
| `0x78` | i64 | TotalContributionPoint |
| `0x80` | i32 | *(col 14; `-1` on load, `1` on insert)* |
| `0x90` | wchar[31] | Introduce |
| `0xD0` | i64 DateTime | LastLogoutTime |
| `0xD8` | u8 | CanGuildWar *(derived from the account, not persisted)* |
| `0xE0` | i64 | AccountId |
| `0xE8` | i64 DateTime | GuildJoinDate |

### 2.3 `GuildGroupData` — 0x28 bytes

`{ i32 GuildGroupId @0x00; wchar Name[16] @0x04; i32 Authority @0x24 }`. `Authority` is a
bitmask; the check is `(wanted & group->Authority) != 0`. New members default to
`GuildGroupId = 2`.

---

## 3. Persistence — the opposite of parties

`status/PARTY-DESIGN.md` §4 proves parties are never written anywhere. Guilds are written
constantly, by the **Arbiter**, over ODBC, through stored procedures. This matters for TeraSharp
because it means guild persistence is *our* job, not World's, and it is not reachable through
the DB-proxy channel we already impersonate.

### 3.1 The procedure catalogue

**Core** `spCreateGuild`, `spLoadAllGuild`, `spDeleteGuild`, `spChangeGuildChief`,
`spUpdateGuildName`
**Scalar updates** `spUpdateGuildAnnounce`, `spUpdateGuildTitle`, `spUpdateGuildPromotionStr`,
`spUpdateGuildJoinCondition`, `spUpdateGuildLevel`, `spUpdateGuildMoney`, `spUpdateGuildPoint`,
`spUpdateGuildIncentive`, `spUpdateGuildGeneralCoin`, `spSetGuildGeneralCoin`,
`spSetGuildRecommendationPoint`, `spResetGuildRecommendCount`, `spUpdateGuildWarAcceptable`,
`spUpdateGuildContributionPoint`, `spResetGuildContributionPoint`, `spUpdateGuildWeekPlayTime`,
`spAddGuildThisWeekPlayTime`, `spLoadRecentlyLastWeekSetDate`, `spUpdateRecentlyLastWeekSetDate`,
`spUpdateGuildLevelRanking`, `spLoadGuildLevelRanking`, `spDeleteAllGuildLevelRanking`
**Members** `spAddGuildMember`, `spUpdateGuildMember`, `spLeaveGuildMember`,
`spLoadAllGuildMemberData`, `spDeleteGuildMemberDataOfUser`, `spDeleteGuildMemberDataOfGuild`,
`spUpdateUserGuildIntroduce`
**Groups / ranks** `spCreateGuildGroup`, `spDeleteGuildGroup`, `spLoadGuildGroup`,
`spUpdateGuildGroupAuthority`
**Join / apply / invite** `spInsertGuildApply`, `spLoadGuildApplyList`, `spDeleteGuildApply`,
`spDeleteGuildApplyForGuildSide`, `spDeleteGuildApplyForUserSide`, `spAddInviteUserToGuild`,
`spLoadInviteUserToGuild`, `spDeleteInviteUserToGuild`, `spDeleteInviteUserToGuildForGuildSide`,
`spDeleteInviteUserToGuildForUserSide`
**Perks** `spLoadGuildPerkList`, `spLearnGuildPerk`, `spDeleteGuildPerk`, `spResetGuildPerk`,
`spUpdateGuildPerkOmitted`
**Bank / warehouse** `spCreateGuildWarehouseLog`, `spLoadGuildWarehouseLog` *(log only — item
rows go through the shared item path)*
**Quests** `spAddGuildQuest`, `spDeleteGuildQuest`, `spDeleteAllGuildQuest`, `spResetGuildQuest`,
`spLoadGuildQuest`, `spLoadGuildQuestManager`, `spUpdateGuildQuestGoal`,
`spUpdateGuildQuestStatus`, `spUpdateGuildQuestPoint`, `spDeleteGuildQuestPoint`,
`spLoadAllGuildQuestPoint`, `spUpdateGuildQuestRewardHistory`,
`spDeleteGuildQuestRewardHistory`, `spLoadAllGuildQuestRewardHistory`
**Wars** `spCreateGuildWar`, `spLoadGuildWar`, `spUpdateGuildWarInfo`, `spUpdateGuildWarKillInfo`,
`spUpdateGuildWarStartInfo`, `spUpdateGuildWarEndInfo`, `spUpdateGuildWarMoney`,
`spLoadGuildWarEndInfo`, `spDeleteOldGuildWarEndInfo`, `spInsertGuildWarHistory`,
`spLoadGuildWarHistory`, `spDeleteGuildWarHistory`, `spDeleteGuildWarHistoryByDbId`,
`spAddGuildWarAggressiveCounter`, `spLoadGuildWarAggressiveCounter`,
`spDeleteGuildWarAggressiveCounter`, `spResetGuildWarAggressiveCounter`,
`spDeleteGuildWarUserScore`, `spUpdateGuildWarCoolTime`, `spLoadAllGuildWarCoolTime`,
`spLoadGuildWarSystemAdminInfo`, `spUpdateGuildWarSystemAdminInfo`,
`spLoadGuildWarAdminDeclareCost`, `spUpdateGuildWarAdminDeclareCost`,
`spLoadGuildWarAdminMaintainCost`, `spUpdateGuildWarAdminMaintainCost`, plus the five
`*LordGuildWarPoint` ones
**Wanted board** `spUpdateGuildWantedWriting`, `spLoadGuildWantedWriting`,
`spDeleteGuildWantedWritingCompletely`, `spDeleteGuildWantedWritingShowList`,
`spDeleteAllGuildWantedWriting`
**Towers / city war** `spInsertCityWarGuildTower`, `spLoadCityWarGuildTower`,
`spDeleteCityWarGuildTower`, `spDeleteAllCityWarGuildTower`, `spUpdateGuildTowerBuildTime`,
`spUpdateGuildTowerDestroyTime`, `spUpdateGuildTowerRemainHpRate`, `spLoadCityWarGuildInfo`,
`spUpdateCityWarGuildInfo`, `spUpdateCityWarGuildDetailInfo`, `spAddCityWarBanGuild`,
`spDelCityWarBanGuild`, `spLoadCityWarBanGuild`
**Logo / flag / emblem** `spUpdateGuildLogo`, `spUpdateGuildEmblem`, `spUpdateGuildForeverEmblem`,
`spUpdateGuildFlag`, `spLoadGuildFlag`
**History / log / events** `spCreateGuildLog`, `spLoadGuildLog`, `spAddGuildEventInfo`,
`spLoadGuildEventInfo`, `spDelGuildEventInfo`, `spDelAllGuildEventInfo`

Names that do **not** exist, despite looking obvious: `spAddGuildMember2`,
`spBanishGuildMember`, `spDestroyGuild`, `spRemoveGuildGroup`, `spSetGuildGroupAuthority`,
`spAddGuildGroup`, `spInsertGuildLog`, `spAddGuildHistory`. Leaving and being kicked both go
through `spLeaveGuildMember`.

### 3.2 The bound parameters that matter

```c
/* spCreateGuild — GuildManager::CreateGuildData, Arb_part_069.c:3945 */
FUN_140108af0(local_f8,param_2,&local_138,0xffffffff);          // P1 nvarchar  guildName
FUN_140108660(local_f8,local_130,&local_140,0xffffffff);        // P2 int       chiefDbId
FUN_1401089a0(local_f8,&stack0x00000030,&local_148,0xffffffff); // P3 bit       isGuildWarAcceptable
FUN_140108140(local_f8,local_158,&local_150,0xffffffff);        // C1 int OUT   guildDbId
```

| proc | params in order |
|---|---|
| `spCreateGuild` | `nvarchar name, int chiefDbId, bit warAcceptable` → **OUT `int guildDbId`** |
| `spAddGuildMember` | `int userDbId, int guildDbId, int guildGroupId` → OUT `int rows`, OUT `TIMESTAMP joinDate` |
| `spUpdateGuildMember` | `int userDbId, int guildDbId, int newGuildGroupId` → OUT `int rows` |
| `spLeaveGuildMember` | `int userDbId` → OUT `bigint rows`, OUT `TIMESTAMP leaveTime` |
| `spDeleteGuildMemberDataOfUser` | `int guildDbId, int userDbId` |
| `spDeleteGuildMemberDataOfGuild` | `int guildDbId` |
| `spUpdateGuildAnnounce` | `int guildDbId, nvarchar announce` |
| `spUpdateGuildLogo` | `int guildDbId, varbinary logo, int newLogoId` |
| `spChangeGuildChief` | `int guildDbId, int newChiefDbId` → OUT `int` |
| `spDeleteGuild` | `int guildDbId` → OUT `int` |
| `spCreateGuildGroup` | `int guildDbId, int guildGroupId, nvarchar name, int authority` → OUT `int` |
| `spDeleteGuildGroup` | `int guildDbId, int guildGroupId` → OUT `int` |
| `spUpdateGuildGroupAuthority` | `int guildDbId, int guildGroupId, int authority, nvarchar name` → OUT `int` |
| `spLoadGuildPerkList` | `int guildDbId` → cols `int perkId, tinyint, tinyint` |
| `spCreateGuildLog` | 13 params: `int guildDbId, int, int, int, int, nvarchar, nvarchar, nvarchar, nvarchar, int, int, bigint, TIMESTAMP` |

`spLoadAllGuild` takes no parameters and binds 28 columns straight into a stack `GuildData` —
the column→offset map is §2.1's `sp col` column, and it is the authority for that table.
`spLoadAllGuildMemberData(int guildDbId)` binds 17 columns into a `GuildMemberData`;
`spLoadGuildGroup(int guildDbId)` binds 3 into a `GuildGroupData`.

The binding ABI, for anyone re-deriving this: `SQLBindCol`/`SQLBindParameter` wrappers where
slot `0xffffffff` means auto-increment, and **columns and parameters use separate counters**
(`stmt+0x10` and `stmt+0x12`), so call order is slot order. `FUN_140108140` = i32 col,
`FUN_140108340` = i64 col, `FUN_1401082c0` = TIMESTAMP col, `FUN_1401083c0` = wchar col,
`FUN_140108040` = binary col; `FUN_140108660` = i32 param, `FUN_140108900` = i64 param,
`FUN_1401089a0` = bit param, `FUN_140108af0` = nvarchar param, `FUN_140108a40` = varbinary param.

### 3.3 SQLite schema proposal

Derived column-by-column from §3.2 and §2.1. It deliberately stops at the point TeraSharp
actually needs — core, members, ranks, apply/invite, log. Wars, quests, towers, the warehouse
and the wanted board are listed in §3.1 and left out here; they are not reachable until a guild
exists at all.

```sql
-- one row per guild; column order follows spLoadAllGuild's 28 binds
CREATE TABLE guilds (
    guild_id                INTEGER PRIMARY KEY AUTOINCREMENT,  -- spCreateGuild OUT C1
    name                    TEXT    NOT NULL UNIQUE,            -- GuildData+0x0004, wchar[37]
    chief_db_id             INTEGER NOT NULL,                   -- +0x0050
    create_date             INTEGER NOT NULL,                   -- +0x0054, unix seconds
    level                   INTEGER NOT NULL DEFAULT 1,         -- +0x0064
    exp                     INTEGER NOT NULL DEFAULT 0,         -- +0x0068
    point                   INTEGER NOT NULL DEFAULT 0,         -- +0x0070
    money                   INTEGER NOT NULL DEFAULT 0,         -- +0x0078
    announce                TEXT    NOT NULL DEFAULT '',        -- +0x0090, <= 200 chars
    recommendation_point    INTEGER NOT NULL DEFAULT 0,         -- +0x0224
    title                   TEXT    NOT NULL DEFAULT '',        -- +0x022C, <= 14 chars
    logo                    BLOB,                               -- +0x0254, <= 8000 bytes
    logo_id                 INTEGER NOT NULL DEFAULT 0,         -- +0x0250
    promotion               TEXT    NOT NULL DEFAULT '',        -- +0x2194, <= 200 chars
    war_acceptable          INTEGER NOT NULL DEFAULT 0,         -- +0x2327
    war_toggle_time         INTEGER NOT NULL DEFAULT 0,         -- +0x2328
    lord_candidacy_parcel   INTEGER NOT NULL DEFAULT 0,         -- +0x2388
    general_coin            INTEGER NOT NULL DEFAULT 0,         -- +0x2338
    forever_emblem_id       INTEGER NOT NULL DEFAULT 0,         -- +0x2340
    emblem_id               INTEGER NOT NULL DEFAULT 0,         -- +0x2344
    preference              INTEGER NOT NULL DEFAULT 0,         -- +0x2358
    join_min_level          INTEGER NOT NULL DEFAULT 1,         -- +0x235C
    join_max_level          INTEGER NOT NULL DEFAULT 70,        -- +0x2360
    join_type               INTEGER NOT NULL DEFAULT 1,         -- +0x2364
    last_week_play_time     INTEGER NOT NULL DEFAULT 0,         -- +0x2368
    this_week_play_time     INTEGER NOT NULL DEFAULT 0,         -- +0x2370
    last_incentive_time     INTEGER NOT NULL DEFAULT 0,         -- +0x0080
    add_account_limit       INTEGER NOT NULL DEFAULT 0          -- +0x238C
);
-- deliberately NOT columns: member_count, account_count, max_account_count, guild_size,
-- policy_point, quest points. The real Arbiter computes all of them; storing them invites drift.

-- spLoadAllGuildMemberData's 17 columns; state/can_guild_war are runtime-only and omitted
CREATE TABLE guild_members (
    user_db_id              INTEGER PRIMARY KEY,                -- GuildMemberData+0x00
    guild_id                INTEGER NOT NULL REFERENCES guilds(guild_id) ON DELETE CASCADE,
    name                    TEXT    NOT NULL,                   -- +0x04
    world_id                INTEGER NOT NULL DEFAULT 0,         -- +0x50
    guard_id                INTEGER NOT NULL DEFAULT 0,         -- +0x54
    section_id              INTEGER NOT NULL DEFAULT 0,         -- +0x58
    guild_group_id          INTEGER NOT NULL DEFAULT 2,         -- +0x5C
    user_level              INTEGER NOT NULL DEFAULT 1,         -- +0x60
    race                    INTEGER NOT NULL DEFAULT 0,         -- +0x64
    user_class              INTEGER NOT NULL DEFAULT 0,         -- +0x68
    gender                  INTEGER NOT NULL DEFAULT 0,         -- +0x6C
    introduce               TEXT    NOT NULL DEFAULT '',        -- +0x90, <= 30 chars
    last_logout_time        INTEGER NOT NULL DEFAULT 0,         -- +0xD0
    account_id              INTEGER NOT NULL DEFAULT 0,         -- +0xE0
    guild_join_date         INTEGER NOT NULL DEFAULT 0,         -- +0xE8
    weekly_contribution     INTEGER NOT NULL DEFAULT 0,         -- +0x74
    total_contribution      INTEGER NOT NULL DEFAULT 0          -- +0x78
);
CREATE INDEX ix_guild_members_guild ON guild_members(guild_id);

-- spLoadGuildGroup's 3 columns
CREATE TABLE guild_groups (
    guild_id                INTEGER NOT NULL REFERENCES guilds(guild_id) ON DELETE CASCADE,
    guild_group_id          INTEGER NOT NULL,                   -- GuildGroupData+0x00
    name                    TEXT    NOT NULL,                   -- +0x04, <= 15 chars
    authority               INTEGER NOT NULL DEFAULT 0,         -- +0x24, bitmask
    PRIMARY KEY (guild_id, guild_group_id)
);

-- spInsertGuildApply / spLoadGuildApplyList / spDeleteGuildApply*
CREATE TABLE guild_applies (
    guild_id                INTEGER NOT NULL REFERENCES guilds(guild_id) ON DELETE CASCADE,
    user_db_id              INTEGER NOT NULL,
    join_msg                TEXT    NOT NULL DEFAULT '',
    applied_at              INTEGER NOT NULL,
    PRIMARY KEY (guild_id, user_db_id)
);

-- spAddInviteUserToGuild / spLoadInviteUserToGuild / spDeleteInviteUserToGuild*
CREATE TABLE guild_invites (
    guild_id                INTEGER NOT NULL REFERENCES guilds(guild_id) ON DELETE CASCADE,
    user_db_id              INTEGER NOT NULL,
    invitor_db_id           INTEGER NOT NULL,
    invited_at              INTEGER NOT NULL,
    PRIMARY KEY (guild_id, user_db_id)
);

-- spCreateGuildLog / spLoadGuildLog; the 13 bound params collapse to these
CREATE TABLE guild_log (
    id                      INTEGER PRIMARY KEY AUTOINCREMENT,
    guild_id                INTEGER NOT NULL REFERENCES guilds(guild_id) ON DELETE CASCADE,
    action_type             INTEGER NOT NULL,
    log_time                INTEGER NOT NULL,
    actor_db_id             INTEGER NOT NULL DEFAULT 0,
    actor_name              TEXT    NOT NULL DEFAULT '',
    target_name             TEXT    NOT NULL DEFAULT '',
    param_int               INTEGER NOT NULL DEFAULT 0,
    param_i64               INTEGER NOT NULL DEFAULT 0,
    detail                  TEXT    NOT NULL DEFAULT ''
);
CREATE INDEX ix_guild_log_guild_time ON guild_log(guild_id, log_time DESC);

-- spLoadGuildPerkList: {int perkId, tinyint, tinyint}
CREATE TABLE guild_perks (
    guild_id                INTEGER NOT NULL REFERENCES guilds(guild_id) ON DELETE CASCADE,
    perk_id                 INTEGER NOT NULL,
    flag_a                  INTEGER NOT NULL DEFAULT 0,
    flag_b                  INTEGER NOT NULL DEFAULT 0,
    PRIMARY KEY (guild_id, perk_id)
);
```

Two deliberate deviations from the original:

* **Timestamps are unix seconds, not `tagTIMESTAMP_STRUCT`.** The 16-byte struct only exists
  because ODBC wanted it; `GuildPackets.BuildTimestamp` converts at the wire edge. Everything
  already in `Persistence/CharacterStore.cs` uses unix seconds.
* **`guild_members.user_db_id` is the primary key, not `(guild_id, user_db_id)`.** That is the
  real invariant — `spLeaveGuildMember` takes only `userDbId`, so a character is in at most one
  guild — and it makes "which guild is this character in" a point lookup.

---

## 4. The World-facing opcodes

Frame is `[u32 len][u16 opcode][payload]`; offsets below are **frame-relative**
(payload = frame − 6). `string`/`bytes` are a u32 offset (and for `bytes`, a u32 count) written
**before** the fixed fields, and the offset is frame-relative — confirmed by the capture, where
`DBS_INIT_GUILD_DATA` advertises its blob at 19 and the blob starts at payload index 13.

### 4.1 Arbiter → World (`AS_`)

| op | name | payload |
|---|---|---|
| `0x13F9` | `AS_LOAD_GUILD` | `u32 nameOff@06, i32 GuildDbId@0A, i32 ChiefDbId@0E, i64 Money@12, i64 CreateTime@1A` (0x22). **Declared, never sent in 100.02.** |
| `0x13FD` | `AS_DESTROY_GUILD` | `i32 GuildDbId@06` (0x0A) |
| `0x13FF` | `AS_LEAVE_GUILD` | `i32 GuildDbId@06, i32 MemberDbId@0A` (0x0E) |
| `0x1401` | `AS_BANISH_GUILD_MEMBER` | `i32 GuildDbId@06, BanisherDbId@0A, BanisheeDbId@0E` (0x12). **Never sent** — a kick goes out as `AS_LEAVE_GUILD`. |
| `0x1403` | `AS_CHANGE_GUILD_CHIEF` | `i32 GuildDbId@06, i32 NewChiefDbId@0A` (0x0E) |
| `0x1405` | `AS_SET_GUILDGROUP_AUTHORITY` | `u32 newNameOff@06, i32 GuildDbId@0A, i32 GuildGroupId@0E, i32 Authority@12` (0x16) |
| `0x1407` | `AS_CREATE_GUILD_GROUP` | same shape, string is GroupName (0x16) |
| `0x1409` | `AS_REMOVE_GUILD_GROUP` | `i32 GuildDbId@06, i32 GroupId@0A` (0x0E) |
| `0x140A` | `AS_ADD_GUILDMEMBER` | `u32 nameOff@06`, then GuildDbId, AddeeDbId, WorldId, GuardId, SectionId, Level, Race, UserClass, **Gender@2A**, State, GuildGroupId, `i64 LogoutTime@36`, **`u8 IsWorldEventTarget@3E`**, `i64 AccountDbId@3F`, **`i64 LastJoinGuildTime@47`** (0x4F) |
| `0x140C` | `AS_CHANGE_GUILDGROUP` | `i32 GuildDbId@06, MemberDbId@0A, GuildGroupId@0E` (0x12) |
| `0x1410` | `AS_UPDATE_GUILD_MEMBER` | seven i32: GuildDbId, MemberDbId, WorldId, GuardId, SectionId, Level, State (0x22) |
| `0x1411` | `AS_SET_GUILD_RECOMMENDATION_POINT` | |
| `0x1412` | `AS_UPDATE_GUILD_TITLE` | `u32 titleOff@06, i32 GuildDbId@0A` + wstring (0x0E) |
| `0x1413` | `AS_UPDATE_GUILD_LOGO` | same shape; the string is the **logo ID**, not the image — the 8000-byte blob never crosses this link |
| `0x144D` | `AS_LOAD_GUILD_DATA` | `u32 blobOff@06, u32 blobLen@0A` + **raw `GuildData`**, length hard-coded `0x23A0` (0x0E) |
| `0x144E` | `AS_UPDATE_GUILD_DATA` | identical, caller-supplied length |
| `0x1453` | `AS_UPDATE_GUILD_QUEST_POINT_INFO` | |
| `0x145D` | `AS_PUSH_GUILD_BUFF` | |
| `0x145E` | `AS_LEARN_GUILD_PERK` | |
| `0x145F` | `AS_RESET_GUILD_PERK` | |
| `0x1490` | `AS_UPDATE_GUILD_NAME` | `u32 nameOff@06, i32 GuildDbId@0A` + wstring (0x0E) |
| `0x15A4` | `AS_UPDATE_GUILD_GENERAL_COIN` | |
| `0x15A5` | `AS_SET_GUILD_GENERAL_COIN` | |
| `0x15AF` | `AS_UPDATE_GUILD_EMBLEM` | `i32 GuildDbId@06, u8 IsForever@0A, i32 EmblemId@0B` (0x0F) |
| `0x2866` | `AS_GUILD_JOINED` | `i32 UserDbId@06` (0x0A). **Sent only to the joining user's own world session**, not broadcast — the exception to the rule below. |

Every other `AS_*GUILD*` is **broadcast to all connected world servers**, by the same 32-slot
`state == 2` loop the party `AS_DO_*` frames use.

`AS_LOAD_GUILD_DATA`'s and `AS_UPDATE_GUILD_DATA`'s `.def` files both declare
`string guildData`. They are wrong: the writer reserves an (offset, count) pair and calls the
raw-bytes helper `FUN_1403c98b0`, never the wstring helper.

```c
/* AS_UPDATE_GUILD_DATA, Arb_part_045.c:4839 */
  FUN_140350eb0(&local_98,0x144e);
  *local_80 = 0;  FUN_14013d0b0(&local_98,*local_80);   /* offset slot @frame+6  */
  *local_78 = 0;  FUN_14013d0b0(&local_98,*local_78);   /* count  slot @frame+10 */
  *local_80 = *local_90;   *local_78 = uVar1;
  FUN_1403c98b0(&local_98,uVar1,uVar3);                 /* raw bytes */
```

### 4.2 World → Arbiter (`SA_`)

There are **no `SA_*GUILD*` `.def` files** in `tera_v100_MASTER_FINAL` — this whole direction is
undocumented there. It exists in the binary. Every one starts with `i64 ArbiterUser` (the handle
of the user who asked), which is the routing key, and every one kills the link on a short frame
exactly like the party `SA_` frames.

| op | name | payload | frame |
|---|---|---|---|
| `0x13FB` | `SA_LOAD_GUILD` | `i64 ArbiterUser@06, i32 GuildDbId@0E` | 0x12 |
| `0x13FC` | `SA_DESTROY_GUILD` | same | 0x12 |
| `0x13FE` | `SA_LEAVE_GUILD` | `u32 nameOff@06, i64 ArbiterUser@0A, i32 GuildDbId@12, i32 MemberDbId@16` | 0x1A |
| `0x1400` | `SA_BANISH_GUILD_MEMBER` | same shape | 0x1A |
| `0x1402` | `SA_CHANGE_GUILD_CHIEF` | `i64 ArbiterUser@06, i32 GuildDbId@0E, i32 NewChiefDbId@12` | 0x16 |
| `0x1404` | `SA_SET_GUILDGROUP_AUTHORITY` | `u32 newNameOff@06, i64 ArbiterUser@0A, i32 GuildDbId@12, i32 GuildGroupId@16, i32 Authority@1A` | 0x1E |
| `0x1406` | `SA_CREATE_GUILD_GROUP` | `u32 groupNameOff@06, i64 ArbiterUser@0A, i32 GuildDbId@12` | 0x16 |
| `0x1408` | `SA_REMOVE_GUILD_GROUP` | `i64 ArbiterUser@06, i32 GuildDbId@0E, i32 GroupId@12` | 0x16 |
| `0x140B` | `SA_CHANGE_GUILDGROUP` | `i64 ArbiterUser@06, i32 GuildDbId@0E, i32 MemberDbId@12, i32 GuildGroupId@16` | 0x1A |
| `0x140F` | `SA_UPDATE_GUILD_MEMBER` | `i32 GuildDbId@06, i32 MemberDbId@0A` | 0x0E |
| `0x1414` | `SA_INC_GUILD_ACCOUNT_LIMIT` | | |
| `0x145C` | `SA_PUSH_GUILD_BUFF` | | |

`SA_UPDATE_GUILD_TITLE` and `SA_UPDATE_GUILD_LOGO` **do not exist** — title and logo changes
only ever travel Arbiter → World.

### 4.3 The DB-proxy channel, and the `0x27CF → 0x27ED` handshake load decoded

These are on the same link and TeraSharp already impersonates this role
(`World/DbProxyHandlers.cs`). `SDB_` = World → Arbiter, `DBS_` = Arbiter → World.

| op | name |
|---|---|
| `0x27CF` | `SDB_INIT_GUILD` — World asks for every guild, at boot. **Zero-length payload.** |
| `0x27ED` | `DBS_INIT_GUILD_DATA` |
| `0x27D0` | `DBS_INIT_GUILD_GROUP` |
| `0x27D1` | `DBS_INIT_GUILD_MEMBER` (batched, 31 members per frame) |
| `0x27D2` | `DBS_INIT_GUILD_PERK_LIST` |
| `0x27D3` | `DBS_LOAD_GUILD_COMPLETE` |
| `0x27D4` / `0x27D5` | `SDB_CREATE_GUILD2` / `DBS_CREATE_GUILD2` |
| `0x27DB` / `0x27DC` | `SDB_ADD_GUILDMEMBER2` / `DBS_ADD_GUILDMEMBER2` |
| `0x2785` / `0x2786` | `SDB_CHECK_NEW_GUILD_NAME` / `DBS_CHECK_NEW_GUILD_NAME` |

`DBS_INIT_GUILD_DATA` payload (frame-relative offsets in brackets):

```
  [06] u32  guildDataOffset   = 19       (payload index 13)
  [0A] u32  guildDataLength   = 0x23A0
  [0E] u32  guildLogoIdOffset = 9139
  [12] u8   success
  [13] byte[0x23A0]  GuildData
  [23B3] wchar_t[]   GuildLogoId
```

The captured frame is 9141 bytes, `Success = 0` — **it is the "no guilds" terminator**, one
default-constructed record and an empty logo id. The non-empty sequence is
`0x27ED` (one per guild, `Success = 1`) → `0x27D0` → `0x27D1`(×⌈n/31⌉) → `0x27D2` → `0x27D3`,
then a final `0x27ED` with `Success = 0`.

`GuildPackets.BuildEmptyDbsInitGuildData()` reproduces the captured frame byte for byte apart
from the two padding holes of §2.1 — that is the test
`Guild_DBS_INIT_GUILD_DATA_reproduces_the_capture_except_the_padding_holes`.

`0x2954`/`0x2955` (`SDB_LOAD_CITY_GUILD_INFO` / `DBS_LOAD_CITY_GUILD_INFO`) and the unnamed
`0x14B3` round out the guild traffic in the captures; all three are already handled and all
three are empty-ish. They are in `data/cap_guild.bin` too.

### 4.4 `AW_` is **not** the World direction

`tera_v100_MASTER_FINAL` ships 32 `AW_*GUILD*` `.def` files, which looks like the missing
World → Arbiter half. It is not. `AW_` is **Arbiter → WebAdminServer**:

* every `AW_` is emitted from a handler whose session type is `WebAdminServerSession`, e.g.
  `"bool __cdecl Handler_WA_DELETE_GUILD(class WebAdminServerSession *,const unsigned char *,int)"`
  at `Arb_part_042.c:14009` → `FUN_140055430(local_48,0x1f77)` = `AW_RESULT_DELETE_GUILD`;
* the reverse prefix `WA_` is what the Arbiter *handles*, and there is no `Handler_AW_*` anywhere;
* the framing is different: `[u16 len][u16 opcode]`, first field at **+4**, writer
  `FUN_140055430`/`FUN_1400554e0`, buffer `0xffff` — versus the World link's `[u32 len][u16 op]`,
  first field at +6, writer `FUN_140350eb0`/`FUN_14013d0b0`, buffer `0x20000`.

So a TeraSharp that does not implement the web admin console can ignore the entire `AW_`/`WA_`
family. Opcodes, for the record: `AW_RESULT_DELETE_GUILD` 0x1F77, `…CHANGE_GUILD_LEVEL` 0x1F79,
`…CHANGE_GUILD_MONEY` 0x1F7B, `…CHANGE_GUILD_ANNOUNCE` 0x1F83, `…CLEAR_GUILD_LOGO` 0x1FA9,
`…CHANGE_GUILD_CHIEF` 0x1FAB, `…CHANGE_GUILD_NAME` 0x1FBB, `…DELETE_GUILD_LOGO` 0x1FBD,
`AW_DEL_GUILD_FLAG` 0x20CE, `AW_ADD_GUILD_EVENT` 0x21BA.

---

## 5. Client packets

Opcodes are from `data.json` map **`376012`**, and that is not a guess: the Arbiter writes the
PDL id straight into the packet, and **all 31 guild ids that appear in a writer or a dumper
guard match `376012` exactly**. (The `opcode=NNNNN` comments inside the MASTER_FINAL `.def`
files are from a different build and are wrong — same finding as the party set.)

Offsets below are **packet-relative**: the `[u16 len][u16 opcode]` header is included, so the
first ref slot is `0x04`. Handler minimum lengths are **total packet length**, matching the
`if (local_res18[0] < N)` guard.

### 5.1 Client → Arbiter — the 17 the Arbiter answers

| opcode | packet | body | min | handler |
|---|---|---|---|---|
| `0xA0DF` | `C_APPLY_GUILD` | `u16 guildNameOff, u16 joinMsgOff` | 8 | `FUN_1404db730` |
| `0xDBE4` | `C_ACCEPT_GUILD_APPLY` | `u8 accept, u32 userDbId` *(unaligned)* | 9 | `FUN_1404d8590` |
| `0x716B` | `C_GUILD_APPLY_LIST` | *(empty)* | 4 | `FUN_1404e1380` |
| `0xDB53` | `C_GUILD_APPLY_LIST_PAGE` | `i32 pageNumber` | 8 | `FUN_1404e14a0` |
| `0xEF92` | `C_INVITE_USER_TO_GUILD` | `u16 nameOff, u32 userDbId, u8 fromWantedList` | 0x0B | `FUN_1404e1890` |
| `0xDE54` | `C_REJECT_INVITE_USER_TO_GUILD` | `i32 guildDbId` | 8 | `FUN_1404e5940` |
| `0xFC1C` | `C_CHANGE_GUILDNAME` | `u16 nameOff` | 6 | `FUN_1404dc700` |
| `0xB77C` | `C_CHECK_CHANGE_GUILDNAME` | `u16 nameOff` | 6 | `FUN_1404dce30` |
| `0x60C1` | `C_UPDATE_GUILD_LOGO` | `u16 logoOff, u16 logoLen` + **bytes** | 8 | `FUN_1404f1010` |
| `0x584B` | `C_GET_USER_GUILD_LOGO` | `i32 userDbId, i32 guildDbId` | 0x0C | `FUN_1404e11a0` |
| `0x807B` | `C_UPDATE_GUILD_TITLE` | `u16 titleOff` | 6 | `FUN_1404f1150` |
| `0x5B51` | `C_REQUEST_GUILD_INFO` | `i32 guildDbId, i32 windowType` | 0x0C | `FUN_1404e7e80` |
| `0x6657` | `C_REQUEST_GUILD_MEMBER_LIST` | *(empty)* | 4 | `FUN_1404e8ab0` |
| `0xDC60` | `C_GET_GUILD_HISTORY` | `i32 viewPage` | 8 | `FUN_1404e0c80` |
| `0xFFDB` | `C_SET_GUILD_JOIN_CONDITION` | `u16 introOff, i32 min, i32 max, i32 joinType, i32 preference` | 0x16 | `FUN_1404ed2d0` |
| `0xC046` | `C_REQUEST_GUILD_INFO_BEFORE_APPLY_GUILD` | `u16 nameOff, i32 guildDbId` | 0x0A | `FUN_1404e8100` |
| `0xC9C4` | `C_REQUEST_COOLTIME_TO_JOIN_GUILD` | *(empty)* | 4 | `FUN_1404e75f0` |

`C_REQUEST_GUILD_INFO`'s `windowType` is the whole guild window in one switch:
**1** probe only · **2** `S_GUILD_INFO` + `S_GUILD_HISTORY` page 1 (members) ·
**3** `S_GUILD_INFO` (non-members) · **5** `S_GUILD_MEMBER_LIST` · **6** guild quests ·
**0x0B** `S_GUILD_APPLY_LIST` page 1. A missing guild answers `S_EMPTY_GUILD_WINDOW` and, for
any type but 1, `S_SYSTEM_MESSAGE` code `0x127`.

### 5.2 Client → WorldServer — the 10 the Arbiter must **not** answer

`C_LEAVE_GUILD` 0x7C83 (4) · `C_BANISH_GUILD_MEMBER` 0xE303 (6, `u16 nameOff`) ·
`C_DESTROY_GUILD` 0xC4CC (4) · `C_CHANGE_GUILD_CHIEF` 0x8C0F (6) ·
`C_CREATE_GUILDGROUP` 0xBC8A (6) · `C_CHANGE_GUILDGROUP` 0x8148 (0x0C) ·
`C_REMOVE_GUILDGROUP` 0x73F1 (8) · `C_SET_GUILDGROUP_AUTHORITY` 0xE885 (0x0E) ·
`C_CHECK_NEW_GUILDNAME` 0xA6EF (6) · `C_REQUEST_USABLE_GUILD_NAME` 0x99CC (6).

Each has a matching `SA_`/`AS_` pair in §4, which is how the answer comes back. The lengths
here come from the Arbiter's packet *dumper* guards rather than a handler guard — the dumper
constant matched the handler constant on all 17 Arbiter-handled packets, so it is reliable.

### 5.3 Arbiter → client

| opcode | packet | shape |
|---|---|---|
| `0xE8B3` | `S_GUILD_INFO` | 8 ref slots, **31 fixed fields, size 0x98**, then the `GuildGroups` array (stride 0x0E) |
| `0xE501` | `S_GUILD_MEMBER_LIST` | 4 ref slots, 13 fixed, size 0x47, `Members` array **stride 0x49** |
| `0xED1B` | `S_UPDATE_GUILD_MEMBER` | 1 ref slot, size **0x39** |
| `0xAB18` | `S_ADD_GUILD_MEMBER` | 1 ref slot, size **0x38** |
| `0x8033` | `S_GUILD_APPLY_LIST` | 2 ref slots, size 0x11, element stride 0x1C, 13 rows/page |
| `0xC89A` | `S_GUILD_APPLY_COUNT` | `i32 Count` |
| `0xF1DF` | `S_REQUEST_JOIN_GUILD_NOTICE` | **empty** — the `.def` is right |
| `0x7E14` | `S_REQUEST_INVITE_GUILD_TAG` | `i32 Count` |
| `0x7252` | `S_EMPTY_GUILD_WINDOW` | empty |
| `0xFC5C` | `S_DESTROY_GUILD` | empty |
| `0xD4D5` | `S_ADD_GUILD_GROUP` | `u16 nameOff, i32 GroupId, i32 Authority`, size 0x0E |
| `0x8A39` | `S_UPDATE_GUILD_GROUP` | byte-identical to `S_ADD_GUILD_GROUP` |
| `0xF787` | `S_REMOVE_GUILD_GROUP` | `i32 GroupId` |
| `0x7A76` | `S_GUILD_ANNOUNCE` | `u16 off` + wstring (field is called `GuildNotice`) |
| `0xE9CC` | `S_UPDATE_GUILD_ANNOUNCE` | same, field `GuildAnnounce` |
| `0xAB7E` | `S_CHANGE_GUILD_CHIEF` | `i32 NewChiefDbId` |
| `0x7DFA` | `S_GET_USER_GUILD_LOGO` | `u16 logoOff, u16 logoCount, i32 UserDbId, i32 GuildDbId` + raw bytes, size 0x10 |
| `0x89C9` | `S_GUILD_HISTORY` | 2 ref slots, size 0x10, element stride 0x14, 0x14 rows/page |
| `0xAA8B` | `S_SET_GUILD_JOIN_CONDITION` | `bool Success` only — the values come back in `S_GUILD_INFO` |
| `0xDDAD` | `S_CHECK_CHANGE_GUILDNAME` | `u16 nameOff, u8 result` |
| `0xF222` | `S_REQUEST_COOLTIME_TO_JOIN_GUILD` | `u8 onCooltime, i64 endTimestamp` |
| `0x5A69` | `S_REQUEST_GUILD_INFO_BEFORE_APPLY_GUILD` | `u16 nameOff, i32 declareCount, u8 warAcceptable` |

A `bytes` field reserves **two** slots in the order **(offset, count)** — the reverse of an
array's (count, offset). `S_GET_USER_GUILD_LOGO` is the proof:

```c
/* Arb_part_046.c:12743 */  *local_90 = *local_a0;            /* slot A <- data OFFSET */
/* Arb_part_046.c:12744 */  *local_88 = (short)uVar2;         /* slot B <- byte COUNT  */
/* Arb_part_046.c:12745 */  FUN_1403c9790(&local_a8,uVar2 & 0xffff,uVar4);
```

`Protocol/DefinitionWriter.cs` already does exactly this, which is a nice independent
confirmation that the existing codec's conventions are the binary's.

### 5.4 WorldServer → client — the 11 the Arbiter never builds

Each has a PDL dumper and a size validator in the Arbiter (so it relays them) but **zero**
construction sites: `S_LEAVE_GUILD` 0xC1D5 (empty) · `S_BANISH_GUILD_MEMBER` 0x8CF9
(`u16 charNameOff`, 0x06) · `S_REMOVE_GUILD_MEMBER` 0xC24D (`i32 MemberDbId`, 0x08) ·
`S_CREATE_GUILD_RESULT` 0x8A6E (`u16 nameOff, u16 firstReplyNameOff, bool Result`, 0x09) ·
`S_CHECK_NEW_GUILDNAME` 0x71FD · `S_RESULT_USABLE_GUILD_NAME` 0x6A53 ·
`S_CHANGE_GUILDNAME` 0x85D9 · `S_RESULT_RENAME_GUILD` 0x594E (all four:
`u16 nameOff, bool usable`, 0x07) · `S_GUILD_EMBLEM` 0xE24D (`i64 GameId, i32 GuildEmblemId`,
0x10) · `S_GUILD_NAME` 0xF741 (4 string refs + `i64 ServerId`, 0x14) ·
`S_GUILD_LOG` 0xD2E3 (2 ref slots, `i32 MenuId`, array stride 0x0E).

### 5.5 `.def` files that are wrong

The guild set is in better shape than the party set — **`S_GUILD_INFO.1.def` and
`S_GUILD_MEMBER_LIST.1.def` get every fixed-field boundary right**, which is worth knowing
before anyone rewrites them. These ten do not. Corrected text lives in
`GuildPackets.CorrectedDefs`, and every correction below is pinned by
`Guild_corrected_defs_produce_the_decompiled_offsets`.

| file | problem | proof |
|---|---|---|
| `S_ADD_GUILD_MEMBER.1.def` | missing `int32 gender` at 0x2A **and** the trailing `byte cityWarCompensationStatus`; computes 0x33, real is 0x38 | dumper guard `if (0x37 < param_2)`, `Arb_part_018.c:6159` |
| `S_UPDATE_GUILD_MEMBER.1.def` | two trailing bools, not three — missing `isInGuildWarCombatState`; computes 0x38, real is 0x39 | dumper `Arb_part_024.c:2629` |
| `S_GUILD_MEMBER_LIST.1.def` | member element missing the trailing `byte cityWarCompensationStatus` (stride 0x48 vs **0x49**), and `contributionCurrent`+`contributionTotal`+`unk3` is really `i32` + `i64` | writer `Arb_part_046.c:13421-13432` |
| `S_CHECK_NEW_GUILDNAME.1.def` | missing the trailing `bool usable`; 0x06 vs 0x07 | dumper `Arb_part_019.c:2846` |
| `S_RESULT_USABLE_GUILD_NAME.1.def` | same | `Arb_part_022.c:15015` |
| `S_CHECK_CHANGE_GUILDNAME.1.def` | same | handler writes `u16 off, u8 result` at `Arb_part_041.c:1` |
| `S_CHANGE_GUILDNAME.1.def` | same | shares `&DAT_140afee60` with the four above |
| `S_RESULT_RENAME_GUILD.1.def` | same | dumper guard `if (6 < param_2)`, `Arb_part_022.c:14772` |
| `S_GUILD_LOG.1.def` | stops at `int32 menuId`; the whole `LogList` array (stride 0x0E) is missing | dumper `Arb_part_020.c:8791` |
| `C_UPDATE_GUILD_LOGO.1.def` | `string logoImage` should be `bytes logoImage`; 0x06 vs the handler's 0x08 | `FUN_1404f1010` passes `param_2[3]` as the byte count and rejects `8000 < len` |

Two field names Ghidra could not recover, both used in several packets:
`&DAT_140aff368` is the `i32` gender/sex slot (it sits where `S_GUILD_MEMBER_LIST` writes the
literal `L"Gender"`), and `&DAT_140afee60` is the `bool` usable/success flag of the five
name-check packets.

---

## 6. The flows, end to end

**Guild creation.** World → `SDB_CREATE_GUILD2` (0x27D4; `GuildName, GuildMasterGroupName,
GuildMemberGroupName, ChiefDbId, Member[], ItemBinary[], DlmId`; fixed 0x2A) →
`Handler_SDB_CREATE_GUILD2` runs name moderation (`InputRestrictionHelper::CheckGuildName`),
then `spCreateGuild`, then `GuildCreateWorker::Response`, which **broadcasts
`DBS_CREATE_GUILD2` (0x27D5) to every connected world with `DlmId = 0`, then sends it once more
to the requesting world with the real `DlmId`** so it can match its pending request. The founder
also gets `S_REQUEST_JOIN_GUILD_NOTICE` (0xF1DF) tunnelled to them.

**A member joins.** World → `SDB_ADD_GUILDMEMBER2` (0x27DB; `DlmId, GuildDbId, AddeeDbId,
GuildGroupId, InvitorDbId`; fixed 0x1A) → `GuildUtil::UserJoinToGuild` emits, in order:

1. `AS_ADD_GUILDMEMBER` (0x140A) broadcast to every world with `state == 2`;
2. a guild-history row (`spCreateGuildLog`, action 0x0B) — DB only;
3. `S_REQUEST_JOIN_GUILD_NOTICE` to the guild's online members;
4. `AS_GUILD_JOINED` (0x2866) to **only** the joining user's own world session;
5. `DBS_ADD_GUILDMEMBER2` (0x27DC; `i32 DlmId, u8 Success`, 11 bytes) back to the requester.

**Apply.** `C_APPLY_GUILD` → `GuildManager::GetGuildWithLock(name)` →
`InputRestrictionHelper::CheckGuildApplyMsg` → `spInsertGuildApply`, and every online officer
gets `S_GUILD_APPLY_COUNT`. `C_GUILD_APPLY_LIST` / `…_PAGE` → `S_GUILD_APPLY_LIST`.
`C_ACCEPT_GUILD_APPLY` → `Guild::AcceptGuildApplyList(User*, bool, int)` → `spDeleteGuildApply`,
then the apply list is re-sent forced to page 1.

**Invite.** `C_INVITE_USER_TO_GUILD` resolves the target by `userDbId`, or by name when
`userDbId == 0`, and refuses with `S_SYSTEM_MESSAGE` 0xE2B (no such user), 0xE2C (no authority),
0xE2E (not in the wanted list) or 0xE36 (already in a guild). On success →
`spAddInviteUserToGuild` and `S_REQUEST_INVITE_GUILD_TAG`. `C_REJECT_INVITE_USER_TO_GUILD` →
`spDeleteInviteUserToGuild`.

**Leave / kick.** Both are WorldServer-side on the client half: `C_LEAVE_GUILD` /
`C_BANISH_GUILD_MEMBER` reach World, which sends `SA_LEAVE_GUILD` / `SA_BANISH_GUILD_MEMBER`.
The Arbiter runs `spLeaveGuildMember` (the same proc for both) plus
`spDeleteGuildMemberDataOfUser`, and answers with `AS_LEAVE_GUILD`. World builds
`S_LEAVE_GUILD` / `S_BANISH_GUILD_MEMBER` / `S_REMOVE_GUILD_MEMBER` for the clients.

**Logo.** `C_UPDATE_GUILD_LOGO` carries the raw image (≤ 8000 B) to the Arbiter, which stores it
with `spUpdateGuildLogo` and bumps `GuildLogoId`. Only the **ID** is pushed to World
(`AS_UPDATE_GUILD_LOGO`); other clients fetch the image with `C_GET_USER_GUILD_LOGO` →
`S_GET_USER_GUILD_LOGO`.

---

## 7. What T36 implemented

`World/DbProxyStaticData.cs` → `public static class GuildPackets`:

* **109 opcode constants** — client (`376012`), `AS_`/`SA_`, `SDB_`/`DBS_` — split into the
  Arbiter half and the World half.
* `MinFrameLength(op)` for the ten `SA_` guild frames (exact — a short frame kills the real
  link) and `MinClientLength(op)` for the 17 Arbiter-handled `C_` packets, with
  `ArbiterHandlesClientPacket(op)` as the routing predicate.
* The `GuildData` / `GuildMemberData` / `GuildGroupData` offset maps, `BuildTimestamp`,
  `BuildEmptyGuildDataBlob()`, `BuildDbsInitGuildData(...)` / `ParseDbsInitGuildData(...)`, and
  `GuildDataPaddingHoles`.
* Parsers for 12 Arbiter-handled `C_` packets and builders for 13 Arbiter-built `S_` packets,
  including the two array packets (`S_GUILD_APPLY_LIST`, `S_GUILD_HISTORY`).
* Builders for 14 `AS_` frames and parsers for 8 `SA_` frames.
* `CorrectedDefs` — the ten corrected `.def` texts of §5.5.

`data/cap_guild.bin` (TSIS, 5 records, 9225 B) + `data/cap_guild.md`: the only guild frames that
exist in any capture.

20 tests in `src/TeraSharp.Arbiter.Tests/Program.cs`, all prefixed `Guild_`. The two that
carry the most weight:

* `Guild_DBS_INIT_GUILD_DATA_reproduces_the_capture_except_the_padding_holes` — our builder is
  byte-identical to the real Arbiter's frame apart from the two uninitialised holes.
* `Guild_shipped_defs_agree_with_the_hand_written_builders` — `S_ADD_GUILD_GROUP`,
  `S_GET_USER_GUILD_LOGO`, `S_GUILD_APPLY_LIST` and `S_GUILD_HISTORY` driven through the real
  `DefinitionWriter` must equal the hand-written builders, so the two halves of the codec cannot
  drift apart silently.

**No `GuildManager`, no wiring, no changes to `WorldBridge`/`GameSession`.** Guilds depend on
routing exactly as parties do (`status/MULTIPLAYER-DESIGN.md` §6).

---

## 8. What needs a capture

Nothing here has ever been observed live except the empty `DBS_INIT_GUILD_DATA`. In rough order
of how much a capture would change:

1. **Guild creation against the tap.** `SDB_CREATE_GUILD2`'s 0x2A-byte fixed part and the
   `DBS_CREATE_GUILD2` broadcast-then-unicast `DlmId` trick are the highest-risk claims in this
   document — they are the only place where getting a size wrong kills the World link.
2. **`S_GUILD_INFO` and `S_GUILD_MEMBER_LIST` with a real guild.** The layouts are believed
   right (the shipped `.def`s and the dumpers agree), but 31 unaligned fields is a lot of
   surface, and only a real client rendering the guild window proves it.
3. **The join sequence's ordering.** §6's five steps come from reading `UserJoinToGuild`
   top-to-bottom; a capture would confirm that `AS_GUILD_JOINED` really is unicast.
4. **`C_UPDATE_GUILD_LOGO`.** The `bytes`-not-`string` correction is well evidenced but is the
   only `C_` correction, and a wrong guess here means a malformed 8000-byte read.
5. **The padding holes.** Four captures agree they are uninitialised; a fifth would be nice but
   is not needed.

## 9. Open

* **The replay leak.** `WorldReplayTable` answers `0x27CF` with `arb_world.log`'s bytes, which
  carry `0xB379` in a padding hole. Replacing that entry with
  `GuildPackets.BuildEmptyDbsInitGuildData()` is a one-line change in an editable file and would
  make the handshake deterministic. Not made here because T36 was scoped to research + codec.
* **`GuildData+0x0228`** is loaded from `spLoadAllGuild` column 11 and never read anywhere.
* **`GuildData+0x2348`** is a 16-byte `tagTIMESTAMP_STRUCT` that carries the epoch default in
  the capture but has no SQL column and no accessor.
* **`GuildMemberData+0x80`** is column 14, `-1` on load and `1` on insert; purpose unknown.
* **The `GuildGroupId = 2` default** for new members is hardcoded in
  `Guild::AddUserToGuildMemberNoLock`. Groups 0 and 1 are presumably chief and officer, created
  by `SDB_CREATE_GUILD2`'s `GuildMasterGroupName` / `GuildMemberGroupName`, but that is
  inference, not observation.
* **Guild fan-out has no helper**, so a TeraSharp `GuildManager` should add one rather than
  copying the real Arbiter's inlined `Guild+0x68` walks — the party code already has the shape
  (`PartyManager.BypassToGroup`).
