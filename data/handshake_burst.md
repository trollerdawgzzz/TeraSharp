handshake_burst.bin - the 63 post-handshake config pushes the real ArbiterServer sends to
WorldServer straight after the startup handshake, extracted from
`D:\packetlogs\arb_world_2026-09-13T11-33-30-680Z.log` seq 104-126 (11:42:22.403-.408), reframed
by u32 length and stored as payloads (frame length - 6). Sent by
`DbProxyHandlers.OnWorldReady`, before the ~100 `0x1581` dungeon-open pushes.

Container format (little-endian), identical to `cap_t15.bin` / `cap_item_single.bin`:

  "TSIS"            4 bytes magic
  u32 recordCount
  per record:  u32 seq | u16 opcode | u32 payloadLength | payload

63 records, 704 payload bytes, 1082 frame bytes, 50 distinct opcodes. `seq` is the capture
sequence number and is NOT unique - five frames share seq 117 - so this file must be read as an
ordered list, not keyed by seq the way `LoadTsisOrSkip` does it.

Nothing here carries a reqId or a DLM id, so none of it can head-block a user (status/HANDOFF.md
section 1); that is why login worked without the burst.

## Live fields

Four captures were diffed frame by frame - `arb_world.log` (2026-09-12 06:36), `lobby_tap.log`
(09-13 02:51), `cap_newchar.log` (09-13 05:49) and the 09-13 11:42 relog above. The burst is
byte-identical in all four **except two u64s**:

| frame | opcode | offset | value |
|-------|--------|--------|-------|
| 0  | `0x15BD AS_SYNC_DATE_TIME` | payload+0 | unix seconds at send time. 09-13: 1789299742 = 11:42:22Z; 09-12: 1789195013 = 06:36:53Z. Matches the tap's own wall clock to the second in all four captures. |
| 13 | `0x14D1 AS_SET_DARK_RIFT_DAILY_COMPLETED` | payload+8 | the most recent 15:00:00 UTC. 09-13 captures: 1789225200 = 2026-09-12T15:00:00Z; 09-12 capture: 1789138800 = 2026-09-11T15:00:00Z. The handshake's `0x1595 AS_EVENT_MATCHING_INFO` carries the SAME value at frame+22 (and a constant 2026-09-09T14:50:00Z at frame+14). |

15:00 UTC is midnight in UTC+9, so "most recent 15:00 UTC" and "most recent local midnight on a
KR-configured box" are indistinguishable from these four samples - none was taken between 15:00
and 24:00 UTC. `DbProxyHandlers.DailyResetHourUtc` is the knob if a later capture settles it.

Everything else is replayed verbatim. In particular `0x15DE AS_DUNGEON_PHASE_LAST_RESET_TIME`
reads 2026-09-12T03:57:29Z in **all four** captures - a day apart and on both sides of a daily
reset - so it is a value the real Arbiter keeps in SQL, not a clock.

## Frames

| # | seq | opcode | frame | name | note |
|---|-----|--------|-------|------|------|
|  0 | 104 | `0x15BD` |  15 | AS_SYNC_DATE_TIME                          | **live** payload+0 u64 = unix seconds now; World echoes it back as 0x15BC |
|  1 | 105 | `0x156B` |  10 | AS_UPDATE_DAILY_EVENT_DAY                  |  |
|  2 | 105 | `0x13C7` |  10 | AS_INITIALIZE_DUNGEON_ID                   |  |
|  3 | 105 | `0x157E` |  14 | AS_DUNGEON_DISABLED_LIST                   |  |
|  4 | 106 | `0x15DE` |  18 | AS_DUNGEON_PHASE_LAST_RESET_TIME           | payload+4 u64 = 2026-09-12T03:57:29Z in all four captures - stored, not a clock |
|  5 | 108 | `0x1582` |  18 | AS_INIT_TIMELINE_CHANGES                   | also sent by the replay table as the 2nd response to 0x1592; the two differ (see below) |
|  6 | 109 | `0x157F` |  26 | AS_CONTENTS_ON_OFF_LIST                    |  |
|  7 | 110 | `0x14B3` |  14 | AS_GUILD_WAR_ADMIN_MAINTAINCOST            |  |
|  8 | 111 | `0x150D` |  14 | AS_VIP_STORE_SLOT_ON_OFF                   |  |
|  9 | 112 | `0x150E` |  14 | AS_VIP_STORE_SLOT_SALE                     |  |
| 10 | 112 | `0x1510` |  10 | AS_CHANGE_ACHIEVEMENT_SEASON               |  |
| 11 | 113 | `0x1511` |  14 | AS_ACHIEVEMENT_SEASON_LIST                 |  |
| 12 | 114 | `0x14D0` |  14 | DISABLE_DARK_RIFT_HUNTING_ZONE             |  |
| 13 | 115 | `0x14D1` |  22 | AS_SET_DARK_RIFT_DAILY_COMPLETED           | **live** payload+8 u64 = most recent 15:00:00 UTC |
| 14 | 115 | `0x14E1` |  14 | AS_RESET_ADMIN_NON_PK_SECTION              |  |
| 15 | 115 | `0x14EC` |  14 | AS_SET_HUNTING_EVENT_VALUE                 |  |
| 16 | 116 | `0x1529` |  10 | AS_BOT_CONFIGURATION                       |  |
| 17 | 116 | `0x28F8` |  52 | AS_EVENT_HUNTINGBONUS                      | two hunting-bonus events (the pair differs at payload+32..33) |
| 18 | 117 | `0x28F8` |  52 | AS_EVENT_HUNTINGBONUS                      | two hunting-bonus events (the pair differs at payload+32..33) |
| 19 | 117 | `0x1556` |  14 | AS_SET_EP_SYSTEM_CONTENTS_ON_OFF_TABLE     |  |
| 20 | 117 | `0x150A` |  74 | AS_SET_EP_SYSTEM_EVENT                     |  |
| 21 | 117 | `0x14E6` |  14 | AS_SET_JACKPOT_EVENT_VALUE_LIST            |  |
| 22 | 117 | `0x1623` |  14 | AS_SEND_RESTRICTION_ITEM_LIST              |  |
| 23 | 117 | `0x149D` |  10 | AS_STOP_FESTIVAL                           | festival id 1..5 |
| 24 | 117 | `0x149E` |  11 | AS_FESTIVAL_NPC_SPAWN                      | festival id 1..5 |
| 25 | 117 | `0x149F` |  11 | AS_FESTIVAL_OBJECT_SPAWN                   | festival id 1..5 |
| 26 | 117 | `0x149D` |  10 | AS_STOP_FESTIVAL                           | festival id 1..5 |
| 27 | 117 | `0x149E` |  11 | AS_FESTIVAL_NPC_SPAWN                      | festival id 1..5 |
| 28 | 117 | `0x149F` |  11 | AS_FESTIVAL_OBJECT_SPAWN                   | festival id 1..5 |
| 29 | 117 | `0x149D` |  10 | AS_STOP_FESTIVAL                           | festival id 1..5 |
| 30 | 117 | `0x149E` |  11 | AS_FESTIVAL_NPC_SPAWN                      | festival id 1..5 |
| 31 | 117 | `0x149F` |  11 | AS_FESTIVAL_OBJECT_SPAWN                   | festival id 1..5 |
| 32 | 117 | `0x149D` |  10 | AS_STOP_FESTIVAL                           | festival id 1..5 |
| 33 | 117 | `0x149E` |  11 | AS_FESTIVAL_NPC_SPAWN                      | festival id 1..5 |
| 34 | 117 | `0x149F` |  11 | AS_FESTIVAL_OBJECT_SPAWN                   | festival id 1..5 |
| 35 | 117 | `0x149D` |  10 | AS_STOP_FESTIVAL                           | festival id 1..5 |
| 36 | 117 | `0x149E` |  11 | AS_FESTIVAL_NPC_SPAWN                      | festival id 1..5 |
| 37 | 117 | `0x149F` |  11 | AS_FESTIVAL_OBJECT_SPAWN                   | festival id 1..5 |
| 38 | 117 | `0x15B6` |  14 | AS_LOAD_PRODUCT_SALE_INFO                  |  |
| 39 | 117 | `0x15C2` |  14 | AS_BATTLE_FIELD_DISABLED_LIST              |  |
| 40 | 118 | `0x15C5` |  14 | AS_LOAD_ENCHANT_PROB_EVENT_INFO            |  |
| 41 | 119 | `0x15D4` |  14 | AS_ADD_AWAKEN_ENCHANT_DATA                 |  |
| 42 | 119 | `0x15D5` |  14 | AS_ADD_AWAKEN_CHANGE_DATA                  |  |
| 43 | 119 | `0x1603` |  14 | AS_LOAD_GMEVENT_DATA                       |  |
| 44 | 119 | `0x160D` |  14 | AS_GMEVENT_LOAD_JOIN_COUNT                 |  |
| 45 | 119 | `0x160F` |  10 | AS_GMEVENT_CHANGE_MAX_JOIN_COUNT           |  |
| 46 | 119 | `0x1609` |   7 | AS_GMEVENT_ONOFF                           |  |
| 47 | 119 | `0x14E9` |  14 | AS_SET_DUNGEON_ROOKIE_EVENT_VALUE_LIST     |  |
| 48 | 120 | `0x14E2` |  14 | AS_SET_DUAL_OPTION_OPEN_MATERIAL_LIST      |  |
| 49 | 121 | `0x14EE` |  14 | AS_SET_PLAY_GUIDE_EVENT_LIST               |  |
| 50 | 122 | `0x14F2` |   8 | AS_SET_PLAY_GUIDE_WEB_ADMIN_SETTINGS       |  |
| 51 | 122 | `0x14F3` |  14 | AS_SET_PLAY_GUIDE_EXTRA_BASE_REWARD_EVENT  |  |
| 52 | 122 | `0x14F7` |  14 | AS_SET_PLAY_GUIDE_EXTRA_DAILY_REWARD_EVENT |  |
| 53 | 122 | `0x1613` |  14 | AS_LOAD_EVENTSYSTEM_INFO                   |  |
| 54 | 122 | `0x161D` |  14 | AS_SET_FIELD_POINT_ADMIN_REWARD            |  |
| 55 | 122 | `0x1589` |  15 | AS_ACCESSORY_TRANSFORM_COST_INFO           |  |
| 56 | 122 | `0x162E` |  15 | AS_WORLD_PURCHASE_LIMIT                    |  |
| 57 | 123 | `0x1567` |  14 | AS_LOAD_CONTINENT_CHANNEL_COUNT            |  |
| 58 | 124 | `0x28E3` |   7 | DBS_INGAMESHOP_CATEGORY_BEGIN              |  |
| 59 | 124 | `0x28E5` |   7 | DBS_INGAMESHOP_CATEGORY_END                |  |
| 60 | 124 | `0x28E6` |   7 | DBS_INGAMESHOP_PRODUCT_BEGIN               |  |
| 61 | 125 | `0x28EB` |   7 | DBS_INGAMESHOP_PRODUCT_END                 |  |
| 62 | 126 | `0x29E2` | 158 | DBS_TBA_UPDATE_ROTATION                    |  |

## The one overlap with the replay table

`0x1582 AS_INIT_TIMELINE_CHANGES` (frame 5) is the only burst opcode the replay table also emits:
it is the second response to `0x1592` in `arb_world.log`. That is **not** a double-send to fix -
the real Arbiter sends both, and they are different frames. The handshake one ends `01 00 00 00`,
the burst one `00 00 00 00`, in all four captures. See the T23 section of
`status/RELOG-CAPTURE-NOTES.md` for the full audit.
