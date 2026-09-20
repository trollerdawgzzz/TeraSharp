# Game log (T115)

World writes its LogDB rows to the Arbiter and waits for nothing back. Until T115 we sealed the
frames and threw the bytes away. Now they decode into one `game_log` table.

## 1. The set is five, and that is all of it

Grepping the Arbiter for `Handler_SDB_*LOG*` finds exactly five
(`SDB_INIT_LOSS_LOGINTIME_REVISION_SECOND` matches the grep and is not a log):

| Op | Name | Guard | Payload | Capture |
|---|---|---|---|---|
| 0x27DD | `SDB_ITEM_TRADE_LOG` | `0x41 <` | 60 | none |
| 0x27FE | `SDB_ADD_PVP_USER_LOG` | `0xd <` | 8 | none |
| 0x27FF | `SDB_ADD_PK_USER_LOG` | `0xd <` | 8 | none |
| 0x2800 | `SDB_ADD_GROUP_DUEL_USER_LOG` | `0x29 <` | 36 | none |
| 0x288C | `SDB_CASH_ITEM_LOG` | `0xd <` | 8 | **4 frames** |

The SA_ family has two more - `SA_LOG_QUEST_END` and `SA_RELAY_LOG` - outside the 0x27xx-0x29xx
range this task covers. Neither is in `dbproxy_opcodes.txt`.

## 2. Layouts (payload index = frame offset - 6)

Two reference shapes, both frame-relative: a **list ref** is `[i32 firstElementOffset][i32
byteLength]` (8 bytes) and a **wstring ref** is one `i32` offset (4 bytes). The strides in the
group-duel dumper - 6, 10, 0x0E, 0x16 - are what pin the two apart.

| Op | Fields |
|---|---|
| 0x27DD | `+0/8/16/24` list refs RequestorSend, RequesteeSend, RequestorTooltip, RequesteeTooltip; `+32 u32 DlmId`; `+36 u32 OwnerDBID`; `+40 u32 TargetDBID`; `+44 i64 RequestorSendMoney`; `+52 i64 RequesteeSendMoney` |
| 0x27FE / 0x27FF | `+0 u32 UserDbId`, `+4 u32 OpponentDbId` - byte-identical frames, one decoder |
| 0x2800 | `+0/+4` wstring refs Blue/RedTeamLeaderName; `+8/+16` list refs Blue/RedTeamMembers; `+24 u32 BlueTeamLeaderDbId`; `+28 u32 RedTeamLeaderDbId`; `+32 u32` (the dumper leaves it unnamed) |
| 0x288C | `+0` list ref CashItemList -> N x 40-byte records |

`CashItemLog` is 0x28 bytes, pinned from both ends: WorldServer's vector strides 0x28, and the
captured frame is 54 = 6 header + 8 reference + 40. Fields from the producer,
`DBIncreaseUserInvenSize::ExecuteCommitSQL` (WorldServer.exe.c:1129672):

| Off | Type | Source | Capture |
|---|---|---|---|
| +0 | i64 | written 0 literally | 0 |
| +8 | i64 | item+0x358 | 0 |
| +16 | i64 | `FUN_1404ce180(user)` | 1003 / 2 / 1003 / 1 |
| +24 | u32 | item+0x360, datasheet-indexed, guarded `< 1000000` | 170003..170005 |
| +28 | u32 | item+0x50 | 1 |
| +32 | u32 | item+0x374 | 0 |
| +36 | u8 | a literal per producer | 2 |
| +37..39 | - | struct padding, **not always zero** | `00 00 00` once, `01 00 00` three times |

## 3. What is deliberately not decoded

- **The item lists inside 0x27DD and 0x2800.** No capture has either frame, and both dumpers
  hand the list to the generic reference printer without naming an element field. The byte
  length is recorded; an element layout would be invention.
- **`CashItemLog +16`.** The capture has 1003, 2, 1003 and 1, and 1003 matches no character in
  those sessions, so it is an account id, or a user id from another space. It is stored as
  `character_id` and echoed into `extra.rawUserId`, so a capture that settles it can re-file the
  rows without re-decoding them. Guessing it into `account_id` would silently mis-file every
  cash row.

## 4. The table and the query

`game_log(log_id, logged_at, category, action, account_id, character_id, target_id, item_db_id,
template_id, amount, money, extra)`, indexed on character, account, category and time. `extra` is
a flat JSON object for whatever a frame carried that has no column. The frames carry **no
timestamp**, so `logged_at` is arrival time, recorded at insert rather than derived later.

Categories are the retail tool's groups: `user item trade guild party mail warehouse pvp`. Five
are produced today; the other three exist so the next log opcode adds an action, not a table.

`CharacterStore.QueryGameLog(accountId, characterId, category, fromUnix, toUnix, page, pageSize)`
- every filter optional, newest first, `pageSize` clamped to 200 and `page` checked as unsigned.
A `characterId` matches **actor or target**, so a trade a character received shows on their page.

## 5. Still one-way

Every handler ends at `return 1` with no packet writer, and cap_social2/3/4 show four 0x288C
frames with no A->W frame after any of them. `FileGameLog` stores and answers nothing.

0x27DD, 0x27FE and 0x288C came **out** of `WorldReplayTable.OneWayFromWorld` to make this work:
a sealed opcode never reaches a handler. `T115_the_log_opcodes_are_handlers_not_sealed` pins the
invariant for all five.
