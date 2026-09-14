# Friends, friend groups, memos and the block list — T30

Everything the client's Friends and Blocked-Users panels do, from rows instead of statics.
Sources: the eleven `Handler_C_*` in `Arb_part_*.c`, the `.def` files in `tera_v100_MASTER_FINAL`,
and the client-side capture `D:\packetlogs\cap_newchar_client.log` (frames 304-307).

**No build was possible in the Cowork container** — every byte claim below was reproduced in
Python against the capture, and the tests assert the same bytes.

---

## 1. The shape of the system

| what | where it lives | keyed on |
|---|---|---|
| friend rows (relation, group, my memo) | `friends` | **character**, one row per direction |
| friend groups | `friend_groups` | character + index |
| the profile message shown as `personalNote` | `characters.profile_message` | character |
| block rows (+ my memo) | `blocks` | character |

Friends are **per character**, not per account: every stored procedure the real Arbiter calls
(`spAddFriendOnList`, `spDeleteFriendOnList`, `spLoadAllFriendList`) is keyed on `playerId`.
The account appears only as a *guard* — `User::CanAddFriendNoLock` (Arb_part_028.c:6721) refuses
a request when `*(longlong *)(param_1 + 0xb0) == *(longlong *)(param_2 + 0xb0)`, i.e. two
characters of one account cannot be friends.

**None of this goes through the DB-proxy protocol.** There is no `SDB_ADD_FRIEND`; the real
Arbiter writes friends straight to SQL from inside `User::`. The only friend-ish `SDB_*` names in
the binary (`SDB_LOAD_INVITE_FRIEND`, `SDB_LOAD_REFER_A_FRIEND_LIST`, …) belong to the
refer-a-friend/invite-code feature, which is a different system. So nothing in T30 can wedge a
DLM item.

---

## 2. The one layout trap: S_FRIEND_LIST is a patch-101 def

`tera_v100_MASTER_FINAL\S_FRIEND_LIST.2.def` is marked `# majorPatchVersion >= 101` and carries
`dungeonGauntletDifficultyId` after `sectionId`. The 100.02 writer does not:

```
User::SendFriendListNoLock   (Arb_part_030.c:8)      *local_b0 = *local_b0 + 0x3f;   // 63 bytes
  here 2 | next 2 | name 2 | myNote 2 | theirNote 2
  playerId 4 | group 4 | level 4 | race 4 | class 4 | gender 4 | worldId 4 | guardId 4 | sectionId 4
  summonable 1 | lastOnline 8 | type 4 | bonds 4                                   = 63
```

With the extra int32 it would be 67, and every field after it would be shifted on the wire. The
same applies to `S_UPDATE_FRIEND_INFO.2` (also `>= 101`): `User::SendUpdateFriendListInfo`
(Arb_part_030.c:3300) writes **53**-byte elements, the def would give 57.

This never showed up because the list was always empty — for an empty array only the 4-byte
header goes out. The first real friend would have handed the client garbage.

`Protocol/V100Definitions.cs` registers both packets at version 100, above every file def, so
`DefinitionRegistry.Get` (highest version wins) picks them. `SocialHandlers` calls
`V100Definitions.EnsureRegistered` before it sends either packet, so no human-owned file has to
change for the fix to take effect.

Field order and names are otherwise exactly the def's; `type` is the relation
(0 friend, 1 request I sent, 2 request I received) which is `UserFriendInfo+0xE8` in the Arbiter.

---

## 3. The rules, as the decompile has them

`SocialHandlers.CanAddFriend` returns the FIRST failure, in `User::CanAddFriendNoLock` order:

| test | system message |
|---|---|
| target unknown | 430 (`0x1AE`) — the handler's own `FindUserWithLock` miss |
| self, or another character on my account | 441 (`0x1B9`) |
| I have blocked them | 1329 (`0x531`) `[UserName]` |
| they have blocked me | 435 (`0x1B3`) `[UserName]` |
| already on my list (a pending request counts) | 438 (`0x1B6`) |
| my list is full (100) | 440 (`0x1B8`) |
| their list is full | 3500 (`0xDAC`) |

`SocialHandlers.CanBlockUser` mirrors `User::CanBlockUserNoLock`: self/same-account/admin 1328
(`0x530`), already blocked 1322 (`0x52A`), list full (120) 1320 (`0x528`).

Limits, all from `User::Init` (Arb_part_027.c:13955) and the handlers:
**100 friends**, **120 blocks**, friend memo **20** chars, blocked-user memo **40**, group name
**40**, group index **2..10** (`User::UpdateFriendGroup`: `if (8 < index - 2U) drop`).

The system-message wire format is proven by the capture, not guessed —
`cap_newchar_client.log` frame 692 decodes to `@2977\vquestTemplateId\v59901\vtaskId\v1`.

---

## 4. Adding a friend is two steps

`C_ADD_FRIEND` does **not** create a friendship. `User::TryToAddFriend` writes my row with
`AddFriendType = 1` and the target's with `2`, storing the greeting string as the memo on
**both** sides, then sends both players a full `S_FRIEND_LIST` plus `S_UPDATE_FRIEND_INFO`.
There is no invitation packet: `S_ADD_FRIEND` exists as an opcode but **the Arbiter never writes
it**, and `S_ACCEPT_FRIEND` does not exist in 100.02. The client renders the type-2 row as the
invitation.

| step | rows | messages |
|---|---|---|
| `C_ADD_FRIEND` | me = 1, them = 2, memo on both, group 1 | 3450 to me `[UserName]`, 3451 to them `[UserName]` |
| `C_ACCEPT_FRIEND` | both -> 0, **both memos cleared** | 433 to the accepter, 432 to the requester `[UserName]` |
| `C_DELETE_FRIEND` on a type-2 row | both rows gone | 3452 to both `[RecvName][ReqName]` (declined) |
| `C_DELETE_FRIEND` on a type-1 row | both rows gone | 3453 to both `[ReqName][RecvName]` (cancelled) |
| `C_DELETE_FRIEND` on a type-0 row | both rows gone | 436 to the deleter only `[UserName]` |

Both sides also get their own `S_DELETE_FRIEND` (just the playerId) — `User::DeleteFromFriendListNoLock`
sends it per side; there is no list refresh on delete.

Accepting clearing the memo is not our simplification: `C_ACCEPT_FRIEND` carries no memo, the
Arbiter re-runs `TryToAddFriend` with `L""`, and `AddToFriendListNoLock` overwrites the record it
finds.

### Deliberate divergence
The real Arbiter requires the other player to be **online** for add / accept / delete
(`UserManager::FindUserWithLock`, SMT 430 otherwise). We look the name up in the store instead, so
these work against offline characters; packets to the other side are sent only when that player
has a live session. Without this a single-box server could never exercise the friend system at
all. Everything else follows the decompile.

---

## 5. Groups

* The **client** picks the index; nothing server-side allocates one. Only `2..10` are stored.
* **Group 1 is the implicit ungrouped bucket.** It is never a row and never appears in
  `S_FRIEND_GROUP_LIST`; new friends get it, and deleting a group moves its members back to it
  (`User::DeleteFriendGroup`, Arb_part_028.c:12838).
* **Group 2 is seeded once per character** by `User::ProvideSampleFriendGroup`, guarded by
  `dbo.spIsProvideSampleFriendGroup`. Its name is `StrFriendDataSheet` string 100 — not in the
  binary, but in the capture: `7D 59 CB 53` = 好友. The same routine sets the default profile
  message, string 200 = 今天也是愉快的一天! (`CA 4E 29 59 …` in frame 306).
  `CharacterStore.TryProvideSampleFriendGroup` is that guard; a player who deletes the sample
  group does not get it back.
* `C_ADD_FRIEND_GROUP` (8-byte array elements: one playerId each) and `C_EDIT_FRIEND_GROUP`
  (12-byte elements: playerId + its own target group) both **send nothing back** — the client has
  already updated its panel. So does `C_DELETE_FRIEND_GROUP`.

---

## 6. Memos

* `C_CHANGE_FRIEND_MEMO` -> `S_RESULT_CHANGE_FRIEND_MEMO` **in both outcomes**, carrying the memo
  *read back from storage*: a rejected change echoes the old text and the client reverts. Body
  order is string-offset first, then the int32 friend id — matching
  `Handler_C_CHANGE_FRIEND_MEMO` reading `[4]` and `[6]`.
* `C_EDIT_BLOCKED_USER_MEMO` has no length check in the handler and **no reply**; storage
  truncates at 40.
* The real Arbiter runs both memos through `InputRestrictionHelper::CheckCommunity` (a banned-word
  list, and the NetModerator service when enabled), answering SMT 1848 (`0x738`) on a hit. We have
  neither word list nor moderator service, so that stage is skipped — noted here rather than faked.

---

## 7. What is stubbed (cross-session)

`status/MULTIPLAYER-DESIGN.md` owns these; each is a TODO in `SocialHandlers`:

* `AS_ADD_BLOCKED_USER` (0x1475) / `AS_REMOVE_BLOCKED_USER` (0x1476) / the login block-id push
  (0x1474) to WorldServer.
* `S_CHANGE_FRIEND_STATE` (0xE887) on login and logout. `SocialHandlers.NotifyFriendsOfState` is
  written and works off the session registry; it needs a call from the login/logout path, which is
  human-owned.
* Friend online state in `S_FRIEND_LIST.lastOnline` / `S_UPDATE_FRIEND_INFO` is only as live as
  the session registry — with one player online it is always "everyone offline".

---

## 8. Files

* `Handlers/SocialHandlers.cs` — every handler, the pure rule functions (`CanAddFriend`,
  `CanBlockUser`, `WriteFriendRequest`, `AcceptFriendRequest`, `DeleteFriendPair`) and the three
  list builders (`BuildFriendListFields`, `BuildFriendGroupListFields`, `BuildBlockListFields`).
* `Protocol/V100Definitions.cs` — the two 100.02 layout overrides.
* `Persistence/CharacterStore.cs` — `friend_groups`, the `friends.group_id` / `friends.memo` /
  `blocks.memo` / `characters.profile_message` / `characters.sample_group_provided` columns, and
  their accessors.
* `src/TeraSharp.Arbiter.Tests/Program.cs` — the T30 block.
