# Guild war - declare, view, withdraw (T80)

Ground truth: `<captures>\cap_social4_client.log` frames **1621..4453** and
`cap_social4_ctl.txt` taps **6023** and **7468**. Implementation: `World/GuildWarManager.cs`,
`Persistence/CharacterStore.cs` (tables `guild_wars`, `guild_war_history`),
`Protocol/V100Definitions.cs` (two corrected defs). Tests:
`T80_guild_war_frames_match_cap_social4`, `T80_guild_war_defs_are_the_100_02_layouts`,
`T80_the_guild_war_episode_follows_the_capture`.

---

## 1. The episode, frame by frame

| client frame | dir | packet | what |
|---|---|---|---|
| 1621 | C->S | `C_OPEN_GUILD_WAR_WINDOW` 0x94F7 | open the panel |
| 1623 | S->C | `S_OPEN_GUILD_WAR_WINDOW` 0x713D | 12 B: no wars, 0 declared, limit 10 |
| 1627, 1646, 1670 | C->S | `C_VIEW_GUILD_WAR` 0x74C5 | page 1 of the history |
| 1628, 1647, 1671 | S->C | `S_VIEW_GUILD_WAR` 0x6E5F | 12 B: page 1, 0 pages, no battles |
| **1691** | C->S | `C_CHECK_TO_DECLARE_GUILD_WAR` 0x787A | names `sdg` - **the player's own guild** |
| | | | **no reply at all** |
| 3214 | S->C | `S_TOTAL_GUILD_WAR_DATA` 0x7858 | login push, `00 00 00 00` |
| 3381 | C->S | `C_CHECK_TO_DECLARE_GUILD_WAR` | names `fdh` |
| 3382 | S->C | `S_CHECK_TO_DECLARE_GUILD_WAR` 0x5CC9 | 35 B: yes, 1500 gold, 0/10, `@1788` |
| **3384** | C->S | `C_DECLARE_GUILD_WAR` 0xD366 | `fdh` |
| 3387 | S->C | `S_NOTIFY_GUILD_WAR_STATUS_CHANGE` 0xAC1D | **header only**, body length 0 |
| | A->W | `AS_DECLARE_GUILD_WAR` 0x14A3 | tap **6023** at 01:30:18.924 |
| 3388/3389 | | `C_/S_OPEN_GUILD_WAR_WINDOW` | 104 B: the war |
| **4449** | C->S | `C_WITHDRAW_GUILD_WAR` 0xFD66 | `03 00 00 00` - the OPPONENT's guild id |
| 4450 | S->C | `S_NOTIFY_GUILD_WAR_STATUS_CHANGE` | header only again |
| | A->W | `AS_END_GUILD_WAR` 0x14AC | tap **7468** at 01:33:58.597 |
| 4452/4453 | | `C_/S_OPEN_GUILD_WAR_WINDOW` | 12 B: no wars, but **1** declared |

Three orderings fall out and are what the implementation follows.

* **Checking your own guild answers with silence.** Frame 1691 named `sdg`, the player's own
  guild, and nothing came back. Frame 3381, two guild names later, got its answer two frames later.
* **Declare and withdraw answer with an EMPTY packet.** `S_NOTIFY_GUILD_WAR_STATUS_CHANGE` is four
  bytes - header, no body. The refreshed window at 3389 is the answer to the
  `C_OPEN_GUILD_WAR_WINDOW` the client sends itself at 3388, not a push.
* **`thisGuildDeclareCount` counts declarations, not live wars.** 1 at frame 3389 and still 1 at
  4453 after the war was withdrawn - so it is read back from `guild_wars` plus `guild_war_history`.

---

## 2. What crosses the link

| tap | dir | opcode | payload |
|---|---|---|---|
| 6023 | A->W | `AS_DECLARE_GUILD_WAR` 0x14A3 | 17 B: `i64 warId=1`, `i32 2`, `i32 3`, `u8 0` |
| 7468 | A->W | `AS_END_GUILD_WAR` 0x14AC | 20 B: `i64 1`, `i32 2`, `i32 3`, `i32 reason=1` |
| 5094, 5846, 5950 | A->W | `AS_NOTIFY_GUILD_WAR_INFO` 0x14AF | 5 B: `i32 playerId`, `u8 flag` |
| 29 | A->W | `AS_GUILD_WAR_ADMIN_MAINTAINCOST` 0x14B3 | 8 zero bytes, in the World handshake burst |

All four are pushes - World never answers any of them.

`0x14AF` is an **enter-world** push, not a war notification: the three occurrences are at
01:29:21, 01:30:11 and 01:30:14, all before the declare at 01:30:18, and the byte is 0 for
character 2 and 1 for characters 1003 and 1, the two members of guild 2. It tracks "this
character is in a guild".

`0x14B3` goes out once, at tap seq 29 inside the handshake burst, with eight zero bytes. It is a
boot-time admin setting, not a runtime guild-war frame, so it belongs with the handshake data and
not in this manager.

`reason` on `AS_END_GUILD_WAR` is 1, which is `S_VIEW_GUILD_WAR.result`'s "Withdrew" - the shipped
def names the enum: `0 = declare, 1 = Withdrew, 2 = Surrendered`.

---

## 3. Two wrong defs

### `S_OPEN_GUILD_WAR_WINDOW.1` has no war list at all

It declares two `int32` and stops - eight bytes. Frame 1623 is twelve and frame 3389 is 104. The
writer (`Arb_part_059.c:2659`) settles it: it advances the element cursor by 0x48 - **72 bytes
per element** - and it writes, in order,

```
+0  u16 here      +2  u16 next
+4  u16 attackName   +6 u16 attackEmblem   +8 u16 defendName   +10 u16 defendEmblem
+12 i64 attackGuildId   +20 u8   +21 i32   +25 i64 money   +33 i32   +37 u8
+38 i64 defendGuildId   +46 u8   +47 i32   +51 i64 money   +59 i32   +63 u8
+64 i64 date
```

4 + 8 + 26 + 26 + 8 = 72. The two `i64` guild ids are the reason the naive reading (four `i32`
where there are two `i64`) drifts: the writer casts each `int` id to `longlong` before storing it.
The header is `[u16 count][u16 offset][i32 declareCount][i32 declareLimit]`.

Frame 3389 decodes to attacker guild 2 `sdg` with flag 1 and money 1500, defender guild 3 `fdh`
with flag 0 and money 0, both emblems empty, both third slots 250, and date **1789608618** =
2026-09-17T01:30:18Z, the exact second the declare crossed the tap.

### `S_CHECK_TO_DECLARE_GUILD_WAR.1` has one `int32` where the writer has three

The send at `Arb_part_058.c:8220` is templated
`<PKT_S_CHECK_TO_DECLARE_GUILD_WAR_WRITE, bool, const wchar_t*, const wchar_t*, const wchar_t*, int&, int&, int&>`
and the body it writes is three `u16` string slots, a bool, then **three** `u32`, then the three
strings: 19 fixed bytes. With the shipped def it would be 11, and every string offset after it
would point into the middle of a number. Frame 3382's `@1788` starts at packet 23 = body 19.

The two extra ints are 0 and 10 in that frame - the same `thisGuildDeclareCount` /
`thisGuildDeclareLimit` pair `S_OPEN_GUILD_WAR_WINDOW` carries seven frames later, which is where
their names come from.

---

## 4. Reproduced, not explained

* **The `u8` flags at +20 and +46.** The writer derives them from the war record's `+0xcc`:
  `== 6` sets the attacker's, `== 7` or `== 8` sets both. Frame 3389 has (1, 0), so a freshly
  declared war is state 6. `GuildWarManager` writes (1, 0) and `guild_wars.state` defaults to 6.
* **The `i32` at +33 and +59.** 250 for BOTH sides in frame 3389 - the attacker who paid 1500 and
  the defender who paid nothing - so it is not a per-side amount.
* **The declare limit (10) and cost (1500).** Constants here; the real Arbiter reads them from
  config or a datasheet, and nothing we have shows which.
* **Emblems.** Both string slots are empty in the capture. The real Arbiter fills them from a
  logo-image datasheet lookup (`FUN_1406becd0` on each side); we have no such sheet, so both go
  out empty, which is what the capture shows anyway.

---

## 5. Not modelled

* **Accepting, surrendering, and the war actually running.** The capture only has declare and
  withdraw; results 0 and 2 of `S_VIEW_GUILD_WAR.result` never appear, and no kill/point traffic
  does either.
* **Paging `S_VIEW_GUILD_WAR`.** The capture only ever has page 1 of an empty history.
* **`war_acceptable` / `war_toggle_time`.** The columns exist on `guilds` and nothing in this
  capture reads or writes them.

---

## 6. The human-owned diff

```csharp
foreach (var (warName, warOp) in GuildWarManager.ClientOpcodes)
    Reg(warName, GuildWarManager.MinBodyLength(warOp),
        (s, body) => GuildWarManager.OnClientPacket(s, warOp, body));
```

Nothing in `WorldBridge` (no World frame belongs to guild war), and nothing in `WorldEntry` or
`GameSession`: the enter-world push rides `SocialHandlers.RegisterChat`, the edge T49, T51, T76
and T78 all ride.

---

## 7. The three systems T80 deferred, with frame numbers

Reported so the next pass starts from bytes, not from a search.

### Wanted board

Three A->W pushes, all in the **handshake burst at tap seq 29** (01:21:27.822), all with zero
payloads, plus one runtime opcode:

| tap | opcode | payload |
|---|---|---|
| 29 | `AS_SET_PLAY_GUIDE_WEB_ADMIN_SETTINGS` 0x14F2 | `01 01` |
| 29 | `AS_SET_PLAY_GUIDE_EXTRA_BASE_REWARD_EVENT` 0x14F3 | 8 zero bytes |
| 29 | `AS_SET_PLAY_GUIDE_EXTRA_DAILY_REWARD_EVENT` 0x14F7 | 8 zero bytes |
| 4623, 8533 | `AS_USER_CANCEL_REQUEST_EXIT` 0x1500 | `01 00 00 00` - a playerId, at 01:28:49 and 01:35:32 |

Note these are the **play-guide** opcodes, not a wanted board; 0x1500 is a cancel-exit push and
belongs with the logout path, not with either.

### Guild logo

`AS_UPDATE_GUILD_LOGO` 0x1413, tap **7366** and **7383** (01:33:44.091, 01:33:46.422), 10-byte
payload `0E 00 00 00 03 00 00 00 00 00`. Client side: `C_GET_USER_GUILD_LOGO` /
`S_GET_USER_GUILD_LOGO`, one each. The `guilds` table already has `logo`, `logo_id` and
`emblem_id`, so the persistence half exists.

### Servants

Four A->W pushes, all in the handshake burst at **tap seq 29**, each a single `01` byte:
`0x28E3`, `0x28E5`, `0x28E6`, `0x28EB`. Plus `0x28F8` **twice** at seq 29, 46-byte payloads
differing only in two bytes (`01 00 F0 0A` vs `00 01 F0 0A`). Client side:
`C_REQUEST_SERVANT_INFO_LIST` / `S_REQUEST_SERVANT_INFO_LIST` (x2),
`C_REQUEST_SERVANT_ADVENTURE_LIST` / `S_RESPONSE_SERVANT_ADVENTURE_LIST` (x2),
`C_SET_SERVANT_SEQUENCE` (x1) and `S_START_COOLTIME_SERVANT_SKILL` (x2). The two request pairs are
already answered by `RegEmptyReply` in `HandlerRegistry`; `C_SET_SERVANT_SEQUENCE` is not.

### Cards - the premise was wrong

`0x28A9` / `0x28AA` are **`SDB_LOAD_FEUDAL_LORD_FLAG` / `DBS_LOAD_FEUDAL_LORD_FLAG`**, not cards,
and they fire exactly once in cap_social4: tap seq 9/10 at 01:21:27.75, in the World handshake.

Item **311034** (`3A BF 04 00`) appears **nowhere** - not in the 10.9 MB raw W<->A tap and not in
the client log, whose frames are logged in full. The only card traffic in the whole capture is
read-side: `C_REQUEST_MY_ACTIVATE_CARD_COMBINE_LIST_DATA` (x2),
`C_REQUEST_OTHERS_CARD_DATA_WITH_GAMEID`, `C_REQUEST_OTHERS_ACTIVATE_CARD_COMBINE_LIST_DATA_WITH_GAMEID`
and the pushes `S_CARD_DATA` (x3), `S_ACTIVATE_CARD_COMBINE_LIST_DATA` (x3),
`S_CHANGE_CARD_PRESET` (x45). There is no `C_MOUNT_CARD`, no `C_ACTIVATE_CARD_COMBINE_LIST` and no
`C_CHANGE_CARD_PRESET` - so there is no learn/equip write in this capture to verify against, and
nothing was added on a guess.

## 8. T170 - declare back, withdraw one side, surrender

cap_final2a_client1 (sdg, guild 2) / client2 (fdh, guild 3) + the cap_final2b tap; pinned in
`T170_guild_war_accept_and_surrender_match_the_capture`.

| Client | Arbiter answers | World gets | State |
|---|---|---|---|
| C_DECLARE_GUILD_WAR (client2 2174) | S_GUILD_MONEY_INFO_CHANGED (paid), S_NOTIFY to both guilds | AS_UPDATE_GUILD_DATA, AS_DECLARE (6800, 6801) | 6 |
| C_CHECK_TO_OPPOSITE_DECLARE_GUILD_WAR [opp] (4067) | S_CHECK_TO_OPPOSITE_DECLARE_GUILD_WAR [opp][cost] | - | - |
| C_OPPOSITE_DECLARE_GUILD_WAR [opp] (4072) | money (paid), S_NOTIFY to both | AS_UPDATE_GUILD_DATA, AS_OPPOSITE_DECLARE (6900, 6901) | 8 |
| C_WITHDRAW_GUILD_WAR, war mutual (10341) | S_NOTIFY to both | AS_WITHDRAW [war][att][def][new state] (14883) | 6 or 7 |
| C_WITHDRAW_GUILD_WAR, only mine declared | S_NOTIFY to both | AS_END reason 1 (T80) | history 1 |
| C_REQUEST_GUILD_WAR_PENALTY_INFO [opp] (10378) | S_GUILD_WAR_PENALTY_INFO [opp][i64] = rate x winner's money | - | - |
| C_GIVE_UP_GUILD_WAR [opp] (10380) | money both guilds (reparation moves), S_NOTIFY to both | 2x AS_UPDATE_GUILD_DATA, AS_END reason 4 (14987-14989) | history 4 |

- The window is viewer-relative: first block = the viewer's guild (flag = declared, money = its
  declaration), second = the opponent. T80's attacker-first rows were the attacker's view.
- T80 missed the payment: cap_social4_client 3385 and tap 6022 are the same money + guild-data pair.
- S_START_GUILD_WAR (client1 4616) and the SMT 3891/3892/3893 notices are World's (GuildWar
  ticks; SA_BROADCAST_SYSTEM_MESSAGE 0x1436, which WorldBridge still drops).
- The capture's costs (100 / 10) are an admin override (spLoadGuildWarAdmin*); TeraSharp uses the sheet.
