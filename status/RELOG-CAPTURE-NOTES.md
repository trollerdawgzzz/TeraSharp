# Relog capture — notes only (T21, part C)

Findings from `D:\packetlogs\arb_world_2026-09-13T11-33-30-680Z.log` that are recorded but **not
implemented**. Condensed listing `cap_relog9827_ctl.txt`, full frames `cap_relog9827_frames.txt`.
Two logins: dob (playerId 1, seq 355-828) and "Test" (playerId 2, seq 835-2036, saved inside
instance 9827). The enter-world fallback that capture is really about is in
`status/ENTER-WORLD-FALLBACK.md`.

All offsets are **payload**-relative (frame − 6) unless a line says "frame".

---

## 1. `0x1439 AS_UPDATE_VISITED_SECTION_LIST` — a real list, not the stub we send

Opcode name from the Arbiter's `case 0x1439:`. Writer `FUN_14035f5e0`:

```c
FUN_140350eb0(param_1, 0x1439);
param_1[3] = <slot>; write 0;      // [0] u32 list offset  (backpatched)
param_1[4] = <slot>; write 0;      // [4] u32 list byte length (backpatched)
FUN_14013d0b0(param_1, param_3);   // [8] u32 playerId
*(u32*)param_1[3] = <current len>; // list starts here (always frame 18)
... 0x10 bytes reserved per element ...
*(u32*)param_1[4] = <bytes written>;
```

So: `[0] u32 listOffset = 18` (frame-relative), `[4] u32 listByteLength`, `[8] u32 playerId`, then
**16 bytes per entry**.

| seq | player | frame | entries | bytes |
|-----|--------|-------|---------|-------|
| 631  | 1 (dob)  | 34 | 1 | `01 00 00 00  01 00 00 00  01 00 00 00  01 00 00 00` |
| 1096 | 2 (Test) | 50 | 2 | `01 00 00 00  19 00 00 00  D9 23 09 00  05 00 00 00`<br>`0F 27 00 00  19 00 00 00  63 26 00 00  05 00 00 00` |
| 1205 | 2 (Test) | 50 | 2 | identical to seq 1096 |

Four u32s per entry. Decoding the fields is **not** settled: `25` and `5` are the same in both of
Test's entries, `599001` (`0x000923D9`) and `9827` sit in the third slot, and `9827` is the
instance he had just entered — so the third slot is plausibly a continent/section id and the
fourth the continent it was visited from (Velika = 5). dob's all-ones entry fits nothing. The
Arbiter builds the list from `User+…` state we do not model.

TeraSharp currently sends a fixed `0x1439` during spawn (`WorldBridge`); it has never been the
real per-character list and this capture is the first evidence of what one looks like. Worth
persisting the day sections matter (fast-travel unlocks, most likely).

## 2. `0x2868 DBS_LOAD_DUNGEON_COOL_TIME` — carries the dungeon-entry history

Reply to `0x2867 SDB_LOAD_DUNGEON_COOL_TIME`. Names from `WorldServer.exe.c`; the writer is
`SendToSession<PKT_DBS_LOAD_DUNGEON_COOL_TIME_WRITE<vector<DungeonCoolTimeElem>,
vector<DungeonClearCountElem>, vector<int>>, bool&, int, …>`.

```
payload [0]  off/len   list 0  vector<DungeonCoolTimeElem>     52 B each
        [8]  off/len   list 1  vector<DungeonClearCountElem>   52 B each
        [16] off/len   list 2  vector<int>
        [24] u8  ok
        [25] u32 reqId
        [29] bodies
```

| seq | player | frame | list 0 |
|-----|--------|-------|--------|
| 399 | 1 | 35 | empty — three empty lists, ok=1, reqId 0x14 |
| 885 | 2 | 87 | **one 52-byte record**, ok=1, reqId 0x65 |

Seq 885's record is byte-identical to the blob in the `0x148D` push at seq 842, so
`DungeonCoolTimeElem` is the record documented in `status/ENTER-WORLD-FALLBACK.md` §4:

```
+0  u32 dungeonId 9827   +4  u32 channelInstanceId 0x0AF00001
+8  16 B DateTime 1970-01-01 ("never")
+24 16 B DateTime 2026-09-12 07:00
+40 u32 0   +44 u32 0   +48 u32 0
```

We answer `0x2867` today with three empty lists, which is correct for a server with no cool-time
state. Implementing it means a `dungeon_cooldowns` table keyed (character, dungeon) — and then the
`0x148D` pushes in the enter-world fallback would carry real counters instead of zeros.

## 3. `0x27C6 DBS_SYSRETURN_POSITION` — the Arbiter telling World it changed the return point

Not a reply: `User::UpdateSysReturnLoc` (`FUN_1403bced0`) pushes it right after the DB write
succeeds. Writer
`SendToSession<PKT_DBS_SYSRETURN_POSITION_WRITE,int,int&,int&,int&,int&,int&>` — six u32s, a
24-byte payload:

```
[0] u32 userDbId   [4] u32 continentId   [8] u32 channelInstanceId
[12] u32 x         [16] u32 y            [20] u32 z          (ints, not floats)
```

seq 1165 (frame 30): `02 00 00 00` then five zeros — that is
`User::CleanSysReturnLoc`, i.e. the Arbiter **clearing** Test's return point on the dungeon
response at seq 1159, because `DungeonEnterContext+42` was 0 there. See
`status/ENTER-WORLD-FALLBACK.md` §3 and §7 for why TeraSharp does not clear.

The related request opcode `0x27C5` is `SDB_RETURN_HOME_WITH_SYSRETURN`; it does not appear in any
capture we have.

## 4. The post-handshake config burst — 64 pushes at seq 100-126, before any login

**DONE (T23)** — `0x2952` was already the replayed answer to `0x294F`; the other 63 frames
(seq 104-126) now go out from `DbProxyHandlers.OnWorldReady`, before the `0x1581` burst, from
`data/handshake_burst.bin`. See section 7 below for what was verified and what was left alone.

Sent immediately after the handshake, straight after `0x2952` and before the first
`AS_ENTER_WORLD` at seq 355. Everything here is **global server configuration**, nothing is
per-user, and World asks for none of it — these are all one-way `A->W` pushes. Before T23
TeraSharp sent none of them except the ~100 `0x1581` dungeon-open pushes
(`DbProxyHandlers.OnWorldReady`), which are **not** in this window.

| first seq | opcode | name | frame bytes |
|-----------|--------|------|-------------|
| 100 | `0x2952` | `DBS_LOAD_CITY_TOWER_AND_PLAYER_INFO` | 30 |
| 104 | `0x15BD` | `AS_SYNC_DATE_TIME` | 15 |
| 105 | `0x156B` | `AS_UPDATE_DAILY_EVENT_DAY` | 10 |
| 105 | `0x13C7` | `AS_INITIALIZE_DUNGEON_ID` | 10 |
| 105 | `0x157E` | `AS_DUNGEON_DISABLED_LIST` | 14 |
| 106 | `0x15DE` | `AS_DUNGEON_PHASE_LAST_RESET_TIME` | 18 |
| 108 | `0x1582` | `AS_INIT_TIMELINE_CHANGES` | 18 |
| 109 | `0x157F` | `AS_CONTENTS_ON_OFF_LIST` | 26 |
| 110 | `0x14B3` | `AS_GUILD_WAR_ADMIN_MAINTAINCOST` | 14 |
| 111 | `0x150D` | `AS_VIP_STORE_SLOT_ON_OFF` | 14 |
| 112 | `0x150E` | `AS_VIP_STORE_SLOT_SALE` | 14 |
| 112 | `0x1510` | `AS_CHANGE_ACHIEVEMENT_SEASON` | 10 |
| 113 | `0x1511` | `AS_ACHIEVEMENT_SEASON_LIST` | 14 |
| 114 | `0x14D0` | `DISABLE_DARK_RIFT_HUNTING_ZONE` | 14 |
| 115 | `0x14D1` | `AS_SET_DARK_RIFT_DAILY_COMPLETED` | 22 |
| 115 | `0x14E1` | `AS_RESET_ADMIN_NON_PK_SECTION` | 14 |
| 115 | `0x14EC` | `AS_SET_HUNTING_EVENT_VALUE` | 14 |
| 116 | `0x1529` | `AS_BOT_CONFIGURATION` | 10 |
| 116 | `0x28F8` | `AS_EVENT_HUNTINGBONUS` | 52 x2 |
| 117 | `0x1556` | `AS_SET_EP_SYSTEM_CONTENTS_ON_OFF_TABLE` | 14 |
| 117 | `0x150A` | `AS_SET_EP_SYSTEM_EVENT` | 74 |
| 117 | `0x14E6` | `AS_SET_JACKPOT_EVENT_VALUE_LIST` | 14 |
| 117 | `0x1623` | `AS_SEND_RESTRICTION_ITEM_LIST` | 14 |
| 117 | `0x149D` | `AS_STOP_FESTIVAL` | 10 x5 |
| 117 | `0x149E` | `AS_FESTIVAL_NPC_SPAWN` | 11 x5 |
| 117 | `0x149F` | `AS_FESTIVAL_OBJECT_SPAWN` | 11 x5 |
| 117 | `0x15B6` | `AS_LOAD_PRODUCT_SALE_INFO` | 14 |
| 117 | `0x15C2` | `AS_BATTLE_FIELD_DISABLED_LIST` | 14 |
| 118 | `0x15C5` | `AS_LOAD_ENCHANT_PROB_EVENT_INFO` | 14 |
| 119 | `0x15D4` | `AS_ADD_AWAKEN_ENCHANT_DATA` | 14 |
| 119 | `0x15D5` | `AS_ADD_AWAKEN_CHANGE_DATA` | 14 |
| 119 | `0x1603` | `AS_LOAD_GMEVENT_DATA` | 14 |
| 119 | `0x160D` | `AS_GMEVENT_LOAD_JOIN_COUNT` | 14 |
| 119 | `0x160F` | `AS_GMEVENT_CHANGE_MAX_JOIN_COUNT` | 10 |
| 119 | `0x1609` | `AS_GMEVENT_ONOFF` | 7 |
| 119 | `0x14E9` | `AS_SET_DUNGEON_ROOKIE_EVENT_VALUE_LIST` | 14 |
| 120 | `0x14E2` | `AS_SET_DUAL_OPTION_OPEN_MATERIAL_LIST` | 14 |
| 121 | `0x14EE` | `AS_SET_PLAY_GUIDE_EVENT_LIST` | 14 |
| 122 | `0x14F2` | `AS_SET_PLAY_GUIDE_WEB_ADMIN_SETTINGS` | 8 |
| 122 | `0x14F3` | `AS_SET_PLAY_GUIDE_EXTRA_BASE_REWARD_EVENT` | 14 |
| 122 | `0x14F7` | `AS_SET_PLAY_GUIDE_EXTRA_DAILY_REWARD_EVENT` | 14 |
| 122 | `0x1613` | `AS_LOAD_EVENTSYSTEM_INFO` | 14 |
| 122 | `0x161D` | `AS_SET_FIELD_POINT_ADMIN_REWARD` | 14 |
| 122 | `0x1589` | `AS_ACCESSORY_TRANSFORM_COST_INFO` | 15 |
| 122 | `0x162E` | `AS_WORLD_PURCHASE_LIMIT` | 15 |
| 123 | `0x1567` | `AS_LOAD_CONTINENT_CHANNEL_COUNT` | 14 |
| 124 | `0x28E3` | `DBS_INGAMESHOP_CATEGORY_BEGIN` | 7 |
| 124 | `0x28E5` | `DBS_INGAMESHOP_CATEGORY_END` | 7 |
| 124 | `0x28E6` | `DBS_INGAMESHOP_PRODUCT_BEGIN` | 7 |
| 125 | `0x28EB` | `DBS_INGAMESHOP_PRODUCT_END` | 7 |
| 126 | `0x29E2` | `DBS_TBA_UPDATE_ROTATION` | 158 |

`x5` on the three festival opcodes is five festivals; `x2` on `AS_EVENT_HUNTINGBONUS` is two
events. Nothing in the burst carries a reqId or a DLM id, so none of it can head-block a user —
which is why login works without it. What it plausibly gates: VIP store slots, achievement
seasons, dark rift, GM events, play-guide rewards, festivals, the in-game shop catalogue, and
`AS_LOAD_CONTINENT_CHANNEL_COUNT`. If a content system turns out to be dead in TeraSharp, its
push is probably in this table.

`0x15BD AS_SYNC_DATE_TIME` (seq 104, 15 B) is the first one out and the one most likely to matter:
World's clock for daily resets comes from the Arbiter.

---

## 5. `0x295C SDB_RESULT_CITY_WAR` — a real request/reply pair that no replay entry covers

Only `cap_newchar.log` has it: seq 115 (`W->A`, 42-byte frame, 05:48:54.119), answered at seq 128
and 129 nine seconds later, in that order:

```
0x15ED AS_UPDATE_ADMIN_CITY_WAR_INTEREST_ONLY   01 00 00 00 00
0x295D DBS_RESULT_CITY_WAR                      01 00 00 00 01 00 00 00
```

`Handler_SDB_RESULT_CITY_WAR` (`Arb_part_064.c:2367`, min frame `0x2a`) calls CityWarEnd
(`FUN_14064f030`, `Arb_part_054.c:10714`), which broadcasts `0x15ED` through `FUN_140641300`
(`PKT_AS_UPDATE_ADMIN_CITY_WAR_INTEREST_ONLY_WRITE<int&,bool&>` = `[u32 cityWarId][u8]`, passed
`(cityWarId, false)`), and then writes its own reply,
`SendToSession<PKT_DBS_RESULT_CITY_WAR_WRITE,int,int>` — **both ints read out of the request
frame at `+0x16` and `+0x1a`** (payload+16 and payload+20), which is why it must be a real
handler and not a replay entry. Real as of T23.

`0x15F9 SA_REWARD_CITYWAR_KILL_DEATH_COUNT` arrives in the same millisecond (seq 116) and looks
like a request, but `Handler_SA_REWARD_CITYWAR_KILL_DEATH_COUNT` (`Arb_part_062.c:13831`) has no
`SendToSession` at all — it only awards CPOINT and messages the players. It is **one-way**, and
it wants this line in `WorldReplayTable.OneWayFromWorld` (not added by T23: the audit below was
"report, don't change", and it is a no-op on `arb_world.log`, which has no `0x15F9` frame):

```csharp
        0x15F9, // SA_REWARD_CITYWAR_KILL_DEATH_COUNT - Handler (Arb_part_062.c:13831) has no
                //   SendToSession; the Arbiter's 0x15ED/0x295D at cap_newchar.log seq 128/129
                //   belong to 0x295C, which arrived one frame earlier.
```

---

## 6. Timestamps in the handshake and the burst

| where | field | 09-12 06:36 | 09-13 02:51 | 09-13 05:49 | 09-13 11:42 | verdict |
|-------|-------|-------------|-------------|-------------|-------------|---------|
| `0x15BD` burst | payload+0 | 06:36:53Z | 02:51:08Z | 05:49:03Z | 11:42:22Z | **live** — the wall clock, to the second |
| `0x14D1` burst | payload+8 | 09-11T15:00Z | 09-12T15:00Z | 09-12T15:00Z | 09-12T15:00Z | **live** — most recent 15:00:00 UTC |
| `0x1595` handshake | frame+22 | 09-11T15:00Z | 09-12T15:00Z | 09-12T15:00Z | 09-12T15:00Z | **live, and we get it wrong** — see below |
| `0x1595` handshake | frame+14 | 09-09T14:50Z | 09-09T14:50Z | 09-09T14:50Z | 09-09T14:50Z | constant |
| `0x15DE` burst | payload+4 | 09-12T03:57:29Z | 09-12T03:57:29Z | 09-12T03:57:29Z | 09-12T03:57:29Z | stored in SQL, not a clock |

`0x1595 AS_EVENT_MATCHING_INFO` is the **second** response the replay table serves for `0x1592`,
so it goes out with `arb_world.log`'s bytes — i.e. the daily-reset boundary frozen at
2026-09-11T15:00:00Z. It is the same value `0x14D1` carries, and `DbProxyHandlers` now computes
it (`DailyResetUnixSeconds`). Not fixed by T23 because it means patching a replay response;
the one-line fix is to give `0x1592` a real handler, or to special-case it in
`WorldReplayTable.GetResponses`, writing `DailyResetUnixSeconds(now)` into payload+16 of the
`0x1595` body (frame+22). Whether World cares is unknown — nothing in the captures reacts to it.

---

## 7. T23 audit: does the burst double-send anything the replay table already sends?

`WorldReplayTable.Load` was transliterated and run against `D:\packetlogs\arb_world.log` (the file
`Program.cs` loads it from): 1239 frames, **70 request opcodes**. Every response opcode of every
one of those 70 entries was intersected with the 50 distinct burst opcodes. Result:

**Exactly one overlap.**

| replay entry | responses | burst opcode among them |
|--------------|-----------|-------------------------|
| `0x1592 SA_EVENT_MATCHING_INFO_REQ` | `0x1595`, `0x1582` | `0x1582 AS_INIT_TIMELINE_CHANGES` |

So after T23 World receives `0x1582` twice per handshake — and **that is correct**: the real
Arbiter sends it twice too, in all four captures, and the two frames differ.

```
handshake (response to 0x1592):  12 00 00 00 82 15  00 00 00 00 00 00 00 00  01 00 00 00
burst     (seq 108):             12 00 00 00 82 15  00 00 00 00 00 00 00 00  00 00 00 00
```

Nothing else collides. In particular:

- `0x2952 DBS_LOAD_CITY_TOWER_AND_PLAYER_INFO` (seq 100) is listed in section 4's table but is
  **not** in `handshake_burst.bin`: it is already the second response to `0x294F`, so the burst
  file starts at seq 104. That is why 64 pushes became 63.
- `0x1581` is not a response of any replay entry (T5 sealed `0x15A8`, which used to inherit it)
  and is not in the burst file either — the two bursts are disjoint.
- The 63 burst frames themselves are attributed to nothing today: in `arb_world.log` they are
  preceded by a run of `0x15A8`, which is in `OneWayFromWorld` and therefore seals the pending
  request, so `WorldReplayTable.Load` drops them. They have never been replayed by accident.

Order: the burst goes out **before** the `0x1581` pushes, which is the order in `lobby_tap.log`
(the capture the live-verified login came from) and in the 09-12 and 09-13 relog captures, and it
puts `AS_INITIALIZE_DUNGEON_ID` / `AS_DUNGEON_DISABLED_LIST` ahead of the per-dungeon opens.
`cap_newchar.log` has them the other way round because that Arbiter stalled ~11 s on a DB query
before its burst went out (the same stall that delayed `0x15ED`/`0x295D` by 9 s); the burst's own
internal order is identical in all four captures.

`ship.ps1` (human-owned) needs one more line next to the other data files, or a published build
logs `data/handshake_burst.bin missing or malformed - the 63-push config burst was NOT sent`:

```powershell
Copy-Item D:\v100\TERA_SERVER.100\TeraSharp\data\handshake_burst.bin D:\TeraSharp-publish\data\ -Force
```

(while in there: `promotions_147E.bin` is copied three times, lines 3-5 of that block.)
