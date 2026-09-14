# PARTY-DESIGN — the party system is Arbiter-owned, and what that means for TeraSharp

T28. Research + the pure packet layer (implemented, see §8). No session wiring: that depends on
T27 (`MULTIPLAYER-DESIGN.md`), and a party is meaningless with one player.

Sources: the decompiled `ArbiterServer.exe`, the `.def` files in
`D:\v100\TERA_SERVER.100\tera_v100_MASTER_FINAL\`, and client opcode numbers from
`tera-server-proxy\data\data.json` `maps."376012"`. **No capture contains a single party
frame** — all four A↔W taps are one login — so every layout here comes from the decompile and
the `.def` files, and the tests in §8 are golden tests against those layouts, not against
captured bytes. That is stated plainly because it is the one weakness of this task.

---

## 0. The headline

**Parties live in the Arbiter's RAM and nowhere else.** The Arbiter owns membership, fans
party packets out to clients itself, and is the only thing that ever sends `S_PARTY_*`. World
gets a **mirror** of the party purely so it can do in-world rules — loot ownership, exp share,
instance gating, raid capacity — and asks the Arbiter to fan out to a party when it needs to.

Two consequences for TeraSharp:

- **Nothing to persist.** No `party` table, no `SDB_*`/`DBS_*` write. §4 proves it.
- **A single-process `PartyManager` is the natural shape**, because that is exactly what the
  real Arbiter is. The multi-planet machinery (`AA_DO_*`, `PDId`) exists only to keep several
  Arbiters in sync and can be dropped — but the *wire formats* still carry `PlanetId` in every
  member record, so a stable PlanetId (2800, from `ServerConfig.xml`) has to go everywhere.

---

## 1. Where the code is

| area | file |
|---|---|
| `class Party` (all methods) | `Arb_part_067.c` |
| `class PartyManager`, `PartyInOneWorldOperator` | `Arb_part_079.c` |
| `Handler_C_*` party handlers | `Arb_part_040.c:19109+`, `Arb_part_041.c:1438–14500` |
| `Handler_SA_*` (World → Arbiter) | `Arb_part_062.c` |
| `AS_DO_*` senders (Arbiter → World) | `Arb_part_066.c:18048–18780` |
| PDL dumpers (field names + offsets) | `Arb_part_010.c`–`Arb_part_016.c` |

Singletons: `DAT_141299948` = `PartyManager`, `DAT_141214fe8` = `UserManager`,
`DAT_141094468` = the world-session array, `DAT_140e2d020` = **PlanetId** (2800).

---

## 2. The `Party` object

`sizeof(Party) = 0x1878` — `PartyManager::New_CreateParty` (`Arb_part_079.c:15298`):
`lVar14 = FUN_14002a7a0(DAT_14131d858,0x1878);`. Constructor `FUN_1407b40d0`
(`Arb_part_067.c:2118`), signature
`Party::Party(__int64 partyId, int ownerPlanetId, bool isSysParty, bool raid, int maxMemberCount, bool)`.

| off | type | field |
|---|---|---|
| 0x58 | 0x20 B | recursive R/W spinlock |
| 0x78 | bool | `IsSysParty` (a matchmade/system party) |
| 0x79 | bool | **`Raid`** |
| 0x7A / 0x7B | bool | `WithdrawalPenalty` / `DungeonClear` |
| **0x80** | i64 | **`PartyId`** |
| 0x88 | int | `OwnerPlanetId` |
| 0x8C | int | `MatchingId` (A→A) / `DungeonId` (A→W) |
| 0x90 | int | `FreeMatching` |
| 0x94 | int | `PartyType` (−1 default) |
| 0x98 | bool | `DungeonClearCompensation` |
| 0x9C / 0xA0 / 0xA4 | int | `TeamIndex` / `BattleFieldId` / `WorldId` |
| **0xA8** | int | `ItemLootingMethod` |
| 0xAC | int | `ItemRareGradeForDicing` |
| 0xB0 | int | `RareItemDistributionMethod` |
| 0xB4 / 0xB5 | bool | `EquipmentForDicing` / `FindClassForDicing` |
| 0xB8 | int | `BoundOnLootItemDistributionMethod` |
| 0xBC | bool | `ForbidLootingInBattle` |
| **0xC0 / 0xC4** | int,int | **Manager `PDId` {PlanetId, UserDbId}** |
| 0xC8 | int | `MemberCount` |
| 0xCC | int | join-order counter |
| **0xD0** | int | `MaxMemberCount` — ctor: `uVar2 = 5; if (raid) uVar2 = 0x1e;` |
| 0xD4 | bool | `IsAnonymous` |
| **0xD8** | `{int PlanetId; int UserDbId}[30]` | **member key table**, stride 8, empty = `{-1, 0}` |
| **0x1C8** | `PartyMemberInfo[30]`, stride **0xC0** | member records |
| 0x1848 | map | friendship / exp-share |
| 0x1868 | map<int,wstring> | raid sub-party names |

The 30-slot table is literal — ctor (`Arb_part_067.c` ≈ 2160):

```c
    puVar3 = param_1 + 0x1b; lVar4 = 0x1e;
    do { *(undefined4 *)puVar3 = 0xffffffff;
         *(undefined4 *)((longlong)puVar3 + 4) = 0;
         puVar3 = puVar3 + 1; lVar4 = lVar4 + -1; } while (lVar4 != 0);
```

and `Party::BroadcastPacket` = `FUN_1407b71e0` (`Arb_part_067.c:4370`) iterates it directly:

```c
      piVar6 = (int *)(param_1 + 0xdc);
      do {
        if ((*piVar6 != 0) && (piVar6[-1] == DAT_140e2d020)) { …send to that user's ClientSession… }
        uVar7 = uVar7 + 1; piVar6 = piVar6 + 2;
      } while (uVar7 < 0x1e);
```

Occupied ⇔ `UserDbId != 0`. Note the `piVar6[-1] == DAT_140e2d020` guard: **only members on
this planet get a unicast**; everyone else is reached by re-sending the change to their
Arbiter. In one process that guard is always true.

Capacity, `Party::New_AddMember`:
`if (((raid == 0) && (4 < memberCount)) || ((raid == 1) && (0x1d < memberCount)))` → **5 for a
party, 30 for a raid**.

### `PartyMemberInfo` (0xC0 B, at `Party+0x1C8 + i*0xC0`)

| off | field | off | field |
|---|---|---|---|
| 0x00 / 0x04 | PlanetId (−1 empty) / UserDbId | 0x6E | `AuthorityAboutInvitation` |
| 0x08 | `GameId` (masked `& 0x7fff…`) | 0x6F | `Alive` |
| 0x10 | `Level` | 0x70 | `Online` |
| 0x14 | `Class` | 0x74 | `AchievementGrade` (laurel) |
| 0x18 | `Race` | 0x78 | `UserAwakenGrade` |
| 0x1C | `Gender` | 0x98 | `std::wstring` secondary name |
| 0x20 | `Role` (−1 default) | 0xA0 | join order |
| 0x24 | `wchar_t Name[0x25]` | 0xA4/0xA8/0xAC | party-match stats |
| | | 0xB0 | flag, bit 4 of `Account+0x2adc` |

### `PartyMemberBasicInfo` — **0xA0 bytes**, the wire form

The first 0xA0 bytes of `PartyMemberInfo` are also the record `AS_DO_CREATE_PARTY` carries,
N of them, raw. Built by `PartyInOneWorldOperator::AddPartyMember` = `FUN_14090d540`
(`Arb_part_079.c:2557`); the 0xA0 stride is confirmed twice — the encoder's bounds check
`if ((int)param_1[2] < *(int *)param_1[1] + 0xa0)` and `puVar25 = puVar25 + 0x28;` (int-stride
0xA0) in `New_CreateParty`.

---

## 3. `PartyManager`

| off | type | purpose |
|---|---|---|
| 0x10 | `map<PDId, TPointer<Party>>` | **normal-party index, keyed by member** |
| 0x20 | `map<PDId, TPointer<Party>>` | **sys-party index, keyed by member** |
| 0x30–0x68 | `unordered_map<i64, TPointer<Party>>` | **index by PartyId** (FNV-1a 64) |
| 0x70 | u16 | `PlanetInnerId`, issued once from SQL |
| 0x74 | int | PartyId sequence counter |
| 0x88 | 0x20 B | **the single global R/W lock** |

Lookup when a member acts — `PartyManager::FindParty(int,int)` = `FUN_1409142f0`
(`Arb_part_079.c:7688`): sys index first, then normal.

```c
  plVar3 = (longlong *)FUN_140914600(param_1,&local_40,param_3,param_4,0);   // FindSysParty
  if (lVar2 == 0) { plVar3 = (longlong *)FUN_1409141e0(param_1,&local_40,param_3,param_4,lVar4); } // FindNormalParty
```

Party id allocation (`Arb_part_079.c:15283`):

```c
  uVar12 = DAT_140e2d020 << 0x10;
  LOCK(); piVar23 = (int *)(param_1 + 0x74); iVar1 = *piVar23; *piVar23 = *piVar23 + 1; UNLOCK();
  local_2b8 = (ushort *)CONCAT44(uVar12 | *(ushort *)(param_1 + 0x70),iVar1 + 1);
```

⇒ **`PartyId = ((i64)((PlanetId << 16) | PlanetInnerId) << 32) | (++counter)`**. `PlanetInnerId`
comes from `dbo.spIssuePartyInnerId` once at boot
(`PartyManager::SetPlanetInnerId` = `FUN_14091d210`, `Arb_part_079.c:14078`) — the only party
SQL in the binary, and it allocates an *identity*, not any party content.

`New_CreateParty` refuses fewer than 2 members and ejects anyone already in a party
(inlining an `S_LEAVE_PARTY` send, `FUN_140055430(local_128,0x9a8e)`).

Mutators, all `Arb_part_079.c`: `New_AddPartyMember` `FUN_14091e170`, `New_ChangePartyManager`
`FUN_14091e4b0`, `New_DismissParty` `FUN_14091f670`, `New_LeavePartyMember` `FUN_14091fb60`,
`KickPartyMember` `FUN_14091bb00`, `ChangeLootingMethod` `FUN_1409104e0`, `ExtendParty`
`FUN_140913510`, `SwapPartyMember` `FUN_140922e40`, `MemberEnterWorld` `FUN_14091dad0`,
`MemberLeaveWorld` `FUN_14091dd20`, `MemberUpdateLevel` `FUN_14091df50`, `RefreshMemberInfo`
`FUN_1409218f0`, `UpdateMemberWorldInfo` `FUN_1409235c0`, `UnsetSysParty` `FUN_140923160`.

Each takes a leading `const bool` that is **1 when the call came from another Arbiter** and **0**
for local/World-driven, which suppresses echoing the change back to its origin. In one process
it is always 0.

---

## 4. Persistence: none. Proof.

1. `grep -c "SDB_\|DBS_\|DbProxy"` → **0** in `Arb_part_079.c` (all of `PartyManager`), **0** in
   `Arb_part_041.c` (all party `Handler_C_*`), **0** in `Arb_part_082.c` (all `Handler_AA_*`).
   The one hit in `Arb_part_067.c` is `PKT_DBS_TBA_UPDATE_BATTLEPASS_SEASONDATA_WRITE`, an
   unrelated battlepass function in the same translation unit.
2. The only `*PARTY*` DB-proxy names in the binary are
   `SDB_/DBS_OPERATOR_TERRITORY_NPCPARTY_RESPAWN` — territory **NPC** spawn groups
   (`FUN_1406bf860`, `Arb_part_058.c:12228`), not player parties.
3. The only party stored procedure is `dbo.spIssuePartyInnerId`.
4. Everything party-shaped that *is* written out goes to the **log** server:
   `LogWrapperEx::LogCreateParty`, `LogDismissParty`, `LogJoinParty`, `LogLeaveParty`,
   `LogKickParty`, `LogKickedParty`, `LogChangeManagerParty`, `LogChangeLootingMethodParty`,
   `LogChangeAuthorityParty`.

⇒ **No `PERSISTENCE-MAP.md` row is needed for parties.** A party dies with the process, which
is also what players expect: relogging drops you from your party.

---

## 5. The World-facing opcodes

Frame is `[u32 len][u16 opcode][payload]`; offsets below are **frame-relative** (payload = frame − 6),
matching how the decompile quotes them. `string` is a u32 offset written *before* the fixed
fields; arrays are u32 offset + u32 count.

### 5.1 Arbiter → World

| op | name | layout | sent by |
|---|---|---|---|
| **0x139E** | `AS_DO_CREATE_PARTY` | `u32 memberListOff@06, u32 memberListCount@0A, i64 PartyId@0E, i32 OwnerPlanetId@16, i32 ManagerPlanetId@1A, i32 ManagerDbId@1E, i32 MaxMemberCount@22, i32 PartyType@26, bool DungeonClearCompensation@2A, i32 DungeonId@2B, bool Raid@2F, i32 TeamIndex@30, i32 BattleFieldId@34`, then N × 0xA0 `PartyMemberBasicInfo`. Fixed part 0x38. | `FUN_1407aebe0` (`Arb_part_066.c:18323`) |
| **0x139F** | `AS_DO_ADD_PARTY_MEMBER` | `u32 nameOff@06, i64 PartyId@0A, i32 MemberPlanetId@12, i32 MemberDbId@16, i64 GameId@1A, i32 Level@22, i32 Class@26, i32 Race@2A, i32 Gender@2E, i32 Role@32, bool AuthorityAboutInvitation@36, bool Alive@37, bool Online@38, i32 AchievementGrade@39, i32 UserAwakenGrade@3D, bool SupplementCompensation@41, bool IsSoloMatching@42` (0x43) | `FUN_1407ae450` (`Arb_part_067.c:10121`) |
| **0x13A0** | `AS_DO_REMOVE_PARTY_MEMBER` | `i64 PartyId@06, i32 MemberPlanetId@0E, i32 MemberDbId@12` (0x16) | `FUN_1407af170` |
| **0x13A1** | `AS_DO_DISMISS_PARTY` | `i64 PartyId@06` (0x0E) | `FUN_1407aef30` |
| **0x13A2** | `AS_DO_EXTEND_PARTY` | `i64 PartyId@06, bool PartyToRaid@0E` (0x0F) | `FUN_1407af040` |
| **0x13A3** | `AS_DO_SWAP_PARTY` | `i64 PartyId@06, i32 SlotIndex1@0E, i32 SlotIndex2@12` | `FUN_1407af6b0` |
| **0x13A4** | `AS_DO_SET_PARTY_MANAGER` | `i64 PartyId@06, i32 ManagerPlanetId@0E, i32 ManagerDbId@12` | `FUN_1407af450` |
| **0x13A5** | `AS_DO_CHANGE_PARTY_MEMBER_AUTHORITY` | `i64 PartyId@06, i32 MemberPlanetId@0E, i32 MemberDbId@12, bool Authority@16` | `FUN_1407aede0` |
| **0x13A6** | `AS_DO_SET_LOOTING_METHOD` | `i64 PartyId@06, i32 LootingMethod@0E, i32 RareGradeForDicing@12, i32 RareItemDistributionMethod@16, bool EquipmentForDicing@1A, bool FindClassForDicing@1B, i32 BoundOnLootItemDistributionMethod@1C, bool ForbidLootingInBattle@20` (0x21) | `FUN_1407af2b0` |
| **0x13A7** | `AS_DO_SET_PARTY_OWNER` | `i64 PartyId@06, i32 OwnerPlanetId@0E` | `FUN_1407af590` |
| **0x13BA** | `AS_DISMISS_PARTY` | `i64 UserPDId@06, i32 PartyMemberCount@0E` | `Handler_C_DISMISS_PARTY`, `Arb_part_041.c:1517` |
| **0x13BB** | `AS_PARTY_LOOTING_METHOD` | `i64 UserPDId@06, i32 Method@0E, i32 RareGrade@12, i32 DistributionForRare@16, bool EquipmentForDicing@1A, bool FindClassForDicing@1B, i32 BoundOnLoot@1C, bool ForbidLootingInBattle@20, i32 PartyMemberCount@21` | `Handler_C_PARTY_LOOTING_METHOD` |
| **0x13BC** | `AS_BAN_PARTY_MEMBER` | `i64 UserPDId@06, i32 BanUserPlanetId@0E, i32 BanUserDbId@12, i32 PartyMemberCount@16` | `Handler_C_BAN_PARTY_MEMBER`, `Arb_part_039.c:1996` |

`i64 UserPDId` is the `{PlanetId, UserDbId}` pair packed little-endian: PlanetId in the low
dword, UserDbId in the high dword.

Every `AS_DO_*` is **broadcast to all connected world servers** — the sender loops 32 slots of
0x38 bytes and sends where state == 2:

```c
    local_a8 = 0x20;
    do { if ((int)param_1[6] == 2) { … send … } param_1 = param_1 + 7; … } while (…);
```

### 5.2 World → Arbiter

| op | name | handler | fixed size |
|---|---|---|---|
| **0x1395** | `SA_JOIN_PARTY` | `FUN_140727b90` (`Arb_part_062.c:8300`) | **0x56** |
| **0x1396** | `SA_LEAVE_PARTY` | `FUN_1407288d0` | `i32 OwnerPlanetId@06, i64 PartyId@0A, i32 MemberPlanetId@12, i32 MemberDbId@16` |
| **0x1397** | `SA_DISMISS_PARTY` | `FUN_140724e20` | `i32 OwnerPlanetId@06, i32 MemberPlanetId@0A, i32 MemberDbId@0E` |
| **0x1398** | `SA_KICK_PARTY` | `FUN_140728190` | `… + i32 TargetPlanetId@12, i32 TargetDbId@16, i32 AgreeCount@1A, bool ByPlayer@1E` (0x1F) |
| **0x1399** | `SA_EXTEND_PARTY` | `FUN_140725a40` | `… + bool PartyToRaid@12` |
| **0x139A** | `SA_SWAP_PARTY` | `FUN_140731870` | `… + i32 SlotIndex1@12, i32 SlotIndex2@16` |
| **0x139B** | `SA_CHANGE_PARTY_MANAGER` | `FUN_140721d40` | `… + i32 NewManagerPlanetId@12, i32 NewManagerDbId@16` |
| **0x139C** | `SA_CHANGE_PARTY_MEMBER_AUTHORITY` | `FUN_1407220d0` | `… + i32 MemberPlanetId@12, i32 MemberDbId@16, bool Authority@1A` |
| **0x139D** | `SA_CHANGE_LOOTING_METHOD` | `FUN_1407219a0` | `… + i32 Method@12, i32 RareGrade@16, i32 DistributionForRare@1A, bool Equip@1E, bool Class@1F, i32 BoundOnLoot@20, bool ForbidInBattle@24` (0x25) |
| **0x13AB** | `SA_JOIN_PARTY_IN_ARBITER` | `FUN_140728080` | `u32 memberNameOff@06, u32 inviteeNameOff@0A, bool Raid@0E` |
| **0x13AC** | `SA_MERGE_PARTY_TO_RAID` | `FUN_14072add0` | `u32 invitorNameOff@06, u32 inviteeNameOff@0A, i64 PartyId@0E` |
| **0x13AF** | `SA_SEND_PARTY_MEMBER_UPDATE_INFO` | | `i64 ArbiterUser@06, i32 MemberDbId@0E, i32 ?@12, i32 ?@16, i32 MaxHp@1A, i32 MaxMp@1E, i16 Level@22, i16 UserStatus@24, i16 ConditionStatus@26, bool IsAlive@28` |
| **0x13F8** | `SA_BYPASS_TO_GROUP` | `FUN_140721360` (`Arb_part_062.c:3820`) | `u32 PacketOff@06, u32 PacketLen@0A, i32 GroupType@0E, i64 GroupId@12, i32 ObjectPlanetId@1A, i32 ObjectId@1E` (0x22) |

**A short `SA_*` frame kills the connection**, it is not a dropped packet —
`Handler_SA_JOIN_PARTY`:

```c
  if (param_3 < 0x56) { … }
  (**(code **)(*DAT_14131d200 + 0x30))(DAT_14131d200,8,L"Arbiter <-> World PDL Version Mismatch! Bye :(\n");
```

So the fixed-part sizes above must be exact.

`SA_BYPASS_TO_GROUP` is the one World → Arbiter fan-out request: `FUN_140721360` hands it to
`PartyManager::BroadcastPacketToParty` = `FUN_14090ed50` (`Arb_part_079.c:3602`) →
`Party::BroadcastPacket`. **TeraSharp ignores 0x13F8 today** — it is not in the `TryHandle`
allow-list and has no replay entry, so a party would silently receive nothing.

### 5.3 What World actually needs, and what it does not

World needs the **mirror** (`0x139E`–`0x13A7`) for loot ownership and dicing
(`AS_DO_SET_LOOTING_METHOD` carries all seven settings), exp sharing and "is X in my party"
(the `PartyMemberBasicInfo` blob), instance/battleground gating (`DungeonId`, `TeamIndex`,
`BattleFieldId`, `PartyType`, `DungeonClearCompensation`) and raid capacity (`MaxMemberCount`,
`Raid`).

`AS_DISMISS_PARTY` / `AS_PARTY_LOOTING_METHOD` / `AS_BAN_PARTY_MEMBER` are the opposite
direction of causality: they are **requests**, carrying `PartyMemberCount` so World can run the
in-world vote, and World answers with the authoritative `SA_DISMISS_PARTY` /
`SA_CHANGE_LOOTING_METHOD` / `SA_KICK_PARTY`.

World never sees `MatchingId`/`FreeMatching`, `WithdrawalPenalty`, `DungeonClear`,
`IsAnonymous` or `WorldId` — those appear only in the Arbiter↔Arbiter `AA_DO_CREATE_PARTY`
(0x1776) / `AA_DO_ADD_PARTY_MEMBER` (0x1777). **A single-process TeraSharp can drop the whole
`AA_*` family.**

---

## 6. Client packets

Opcodes verified against `data.json` `maps."376012"`. **The `opcode=NNNNN` comments inside the
MASTER_FINAL `.def` files are from a different protocol build and are wrong** (e.g.
`C_MERGE_PARTY_TO_RAID.1.def` says `opcode=31432` = 0x7AC8; 376012 uses 0xB8D0). Ignore them.

### 6.1 Client → Arbiter

| opcode | packet | body | Arbiter handler |
|---|---|---|---|
| **0xA889** | `C_APPLY_PARTY` | `int32 playerId` | `FUN_1404db920` (`Arb_part_040.c:19109`), min len 8 |
| **0xBD04** | `C_REPLY_INTER_PARTY_MAKE` | `int32 partyMakingId`, **`bool accept`** | `FUN_1404e5b30` (`Arb_part_041.c:6228`), **min len 9** |
| **0xC8B9** | `C_DISMISS_PARTY` | *(empty)* | `FUN_1404def50` (`Arb_part_041.c:1438`) |
| **0x59C1** | `C_BAN_PARTY_MEMBER` | `uint32 serverId`, `uint32 playerId` | `FUN_1404dbcb0` (`Arb_part_040.c:19235`), min len 0xC |
| **0x5D24** | `C_PARTY_LOOTING_METHOD` | `int32 methodLoot`, `int32 rareGrade`, `int32 methodRare`, `bool rareEquipment`, `bool rareClass`, `int32 methodBound`, `bool noCombat` | `FUN_1404e40a0` (`Arb_part_041.c:5058`), min len 0x17 |
| **0xB8D0** | `C_MERGE_PARTY_TO_RAID` | `int64 partyId`, `byte accpet` *(sic)* | `FUN_1404e3870` (`Arb_part_041.c:4710`), min len 0xD |
| **0xFD35** | `C_REQUEST_PARTY_INFO` | `int32 playerId` | `FUN_1404e9b50` (`Arb_part_041.c:8899`), min len 8 |
| **0xCBC2**→ | `C_VIEW_PARTY_INVITE` | `(empty)` | `FUN_1404f1e70` (`Arb_part_041.c:14439`) |
| **0xFFB6** | `C_LEAVE_PARTY` | *(empty)* | **none — World-side** |
| **0x60D6** | `C_CHANGE_PARTY_MANAGER` | `uint32 serverId`, `uint32 playerId` | **none — World-side** |

`C_REPLY_INTER_PARTY_MAKE.1.def` is **incomplete** — the handler requires 9 bytes and reads
`*(undefined4 *)(param_2 + 4)` and `*(undefined1 *)(param_2 + 8)`:

```c
  if ((local_res18[0] < 9) || (param_2 == 0)) { … L"GET_CLIENT_BUFFER_BUFSIZE_MISMATCH" … }
  …  uVar2 = *(undefined4 *)(param_2 + 4);   uVar1 = *(undefined1 *)(param_2 + 8);
```

Also World-side (no Arbiter handler): `C_CHANGE_PARTY_MEMBER_AUTHORITY` 0x5553,
`C_EXTEND_PARTY` 0xB67A, `C_SWAP_PARTY` 0xAE96, `C_VOTE_DISMISS_PARTY` 0x6D46,
`C_ANSWER_BANNING_PARTY_MEMBER` 0x791F, `C_PARTY_LOOTING_METHOD_VOTE` 0xB509,
`C_CHECK_TO_READY_PARTY` 0x66C2, `C_CHECK_TO_READY_PARTY_ANSWER` 0x5080, `C_PARTY_MARKER` 0xE8C1,
`C_DUNGEON_SUMMON_PARTY` 0x65B8, `C_PARTY_NOTIFY_MY_POSITION` 0xDD9C.

**There is no `C_ACCEPT_PARTY`, `S_ASK_JOIN_PARTY`, `S_JOIN_PARTY` or `S_PARTY_SETTING` in
376012.** The invite handshake runs through the generic interactive-target path
(`C_ASK_INTERACTIVE` 0x5639 → `S_ANSWER_INTERACTIVE` 0x85C8, which carries a "target is already
in a party" bit — `FUN_140351320(&local_60,bVar11)` with
`bVar11 = FindPartyByPDId(...) != 0`) and is **completed by World**, which then tells the
Arbiter with `SA_JOIN_PARTY` 0x1395. The Arbiter-side "apply to a listed party" flow is
`C_APPLY_PARTY` → `S_OTHER_USER_APPLY_PARTY`.

### 6.2 Arbiter → client

| opcode | packet | notes |
|---|---|---|
| **0x8BC6** | `S_PARTY_MEMBER_LIST` | **use `S_PARTY_MEMBER_LIST.8.def`**, not `.7`. Encoder `FUN_1407b39e0` (`Arb_part_067.c:1939`). Two reserved u16 (array count, array offset) then 14 scalars, then 0x2C-byte member elements + names. Sources: `ims`←`+0x78`, `raid`←`+0x79`, `memberLimit`←`+0xD0`, `id`←`+0x80`, leader←`+0xC0/+0xC4`, loot←`+0xA8,+0xAC,+0xB4,+0xB5,+0xB0,+0xB8,+0xBC`, `anonymized`←`+0xD4`. `slot` is the loop index for a party and `Party::GetIndex(PDId)` for a raid. |
| **0x9A8E** | `S_LEAVE_PARTY` | empty |
| **0x5808** | `S_LEAVE_PARTY_MEMBER` | `uint32 serverId, uint32 playerId, string name` |
| **0xFCD9** | `S_LOGOUT_PARTY_MEMBER` | `uint32 serverId, uint32 playerId` (no ref block) |
| **0x8A48** | `S_BAN_PARTY_MEMBER` | `uint32 serverId, uint32 playerId, int32 unk1, string name` |
| **0x59A5** | `S_BAN_PARTY` | empty |
| **0x5795** | `S_CHANGE_PARTY_MANAGER` | `uint32 serverId, uint32 playerId, string name` |
| **0x8A1B** | `S_CHANGE_PARTY_MEMBER_AUTHORITY` | `int32, int32, byte` — **no ref block** |
| **0x63C0** | `S_PARTY_LOOTING_METHOD` | same seven fields as `C_PARTY_LOOTING_METHOD`, no ref block |
| **0x99E3** | `S_MERGE_PARTY_TO_RAID` | `string invitorName, int64 partyId` |
| **0xFD76** | `S_OTHER_USER_APPLY_PARTY` | `byte unk1, int32 pid, int16 class, int16 race, int16 gender, int16 level, byte unk2, string name`. Writer `PartyMatchManager::ApplyParty` = `FUN_1408279e0` (`Arb_part_071.c:9146`); the four u16 come from `User+0x178/+0x170/+0x174/+0x17C` in that order. |
| **0xBEC0** | `S_PARTY_MEMBER_INFO` | **none of the three shipped `.def` versions matches the binary.** See §6.3. |
| **0xCBC2** | `S_VIEW_PARTY_INVITE` | `.def` claims "no fields" — wrong; `User::SendPartyInvitableList` (`Arb_part_030.c:1482`) reserves **four** u16 (two arrays). |
| **0x74C7** | `S_SYS_PARTY_INFO` | `.def` claims "no fields" — wrong; writer at `Arb_part_079.c:3216` is `[u16 count][u16 offset]` + one array over **all 30 slots unfiltered**, element 0x10 B: `[u16 self][u16 next][u32 serverId][u32 playerId][u32 role]`. |

Not sent by the Arbiter at all (zero `FUN_140055430(x, op)` sites) — World-side:
`S_PARTY_INFO` 0x8B6D, `S_ASK_BANNING_PARTY_MEMBER` 0xD43A, `S_VOTE_DISMISS_PARTY` 0x6836,
`S_CANCEL_VOTE_DISMISS_PARTY` 0xF64E, `S_PARTY_LOOTING_METHOD_VOTE` 0x6718,
`S_CANCEL_PARTY_LOOTING_METHOD_VOTE` 0xEFDB, `S_CHECK_TO_READY_PARTY` 0xCA21,
`S_CHECK_TO_READY_PARTY_FIN` 0x55D0, `S_PARTY_MARKER` 0x69A6,
`S_PARTY_MEMBER_INTERVAL_POS_UPDATE` 0xD04F, `S_PARTY_MEMBER_STAT_UPDATE` 0xACB0,
`S_DUNGEON_SUMMON_PARTY` 0xB380.

### 6.3 `.def` files that are wrong — do not build a codec from these as shipped

| file | problem |
|---|---|
| `C_REPLY_INTER_PARTY_MAKE.1.def` | missing trailing `bool accept` (@+8); handler requires 9 bytes |
| `AS_DO_CREATE_PARTY.1.def` | missing `bool dungeonClearCompensation` @0x2A; `memberList` is not a `string` but a raw array of N × 0xA0 records with a u32 offset + u32 count header |
| `AS_DO_ADD_PARTY_MEMBER.1.def` | missing `bool authorityAboutInvitation` @0x36 and `bool supplementCompensation` @0x41 |
| `AS_DO_SET_LOOTING_METHOD.1.def` | missing `int32 boundOnLootItemDistributionMethod` @0x1C |
| `SA_CHANGE_LOOTING_METHOD.1.def` | missing `int32 boundOnLootItemDistributionMethod` @0x20 |
| `S_PARTY_MEMBER_INFO.1/.2/.3.def` | none matches the v100 writer; the element stride is 0x22 and the field boundaries differ from `.3` |
| `S_VIEW_PARTY_INVITE.1.def`, `S_SYS_PARTY_INFO.1.def`, `S_SEND_PARTY_NAME_LIST.1.def`, `S_ANSWER_PARTY_NAME.1.def`, `S_ANSWER_CHANGE_PARTY_NAME.1.def` | all claim "no fields"; at least the first two have array bodies |
| every MASTER_FINAL `.def` | the `opcode=` comment is from another build |

Binary-derived `S_PARTY_MEMBER_INFO` (0xBEC0), element stride 0x22:

```
uint16 members.count          # reserved ref slot
uint16 members.offset         # reserved ref slot
bool   raid                   # Party+0x79
bool   <flag>                 # caller arg
array members                 # stride 0x22
- uint16 <self>   uint16 <next>   uint16 name.offset
- uint32 playerId             # member+0x04
- uint16 class                # member+0x14
- uint16 race                 # member+0x18
- uint16 gender               # member+0x1C
- uint16 level                # member+0x10
- uint32 stat0 stat1 stat2    # member+0xA4/A8/AC  (offline) or User+0x3b88/8c/90 (online)
- bool   isLeader             # PDId == Party+0xC0/0xC4
- uint16 <zero>
- byte   flag                 # member+0xB0
- string name
```

---

## 7. `PartyManager` design for one TeraSharp process

The real Arbiter's design *is* the single-process design; the only thing to remove is the
cross-Arbiter sync. Proposed shape (naming matches the decompile so the two stay comparable):

```
PartyManager                              // one instance, owned by Program
  ConcurrentDictionary<long, Party>        _byId          // PartyId -> Party
  ConcurrentDictionary<int,  Party>        _byMember      // UserDbId -> Party  (PlanetId is constant)
  int _idSeq                                              // -> PartyId low dword
  const int PlanetId = 2800                               // ServerConfig.xml planetId
  const int PlanetInnerId = 1                             // spIssuePartyInnerId; any stable value

Party
  long  Id                                                // (PlanetId<<16 | PlanetInnerId) << 32 | seq
  bool  Raid, IsAnonymous
  int   MaxMembers                                        // 5, or 30 when Raid
  PDId  Manager
  LootSettings Loot                                       // the seven fields
  PartyMember?[30] Members                                // fixed 30 slots; index IS the wire slot
  object _lock                                            // per-party, taken after the manager lock
```

Rules taken straight from the decompile, each of which a naive rewrite gets wrong:

1. **Fixed 30 slots, indices are wire-visible.** `slot` appears in `S_PARTY_MEMBER_LIST` and
   `SlotIndex1/2` in `AS_DO_SWAP_PARTY`. A `List<Member>` cannot replace the array without
   preserving stable indices. `FindEmptyIndex` scans for `UserDbId == 0`.
2. **Lock order: manager, then party.** `New_CreateParty` holds the exclusive manager lock while
   calling into `Party::…`, which takes `Party+0x58`. Invert it anywhere and you deadlock.
3. **Two indices, one membership.** A member is in the normal index *or* the sys index, never
   both; `FindParty` probes sys first. TeraSharp can collapse to one dictionary until
   matchmaking exists, but keep the "already in a party" rejection — `New_CreateParty` logs
   `Already in Party: PartyId=%lld PlanetId=%d DbId=%d` and refuses.
4. **Minimum 2 members to create.** `if ((memberVector.size()) < 2) fail;`
5. **Party ids must not repeat across restarts.** The real counter is process-lifetime and
   nothing detects a duplicate — `FUN_14090ab40` silently overwrites the by-id entry. Either
   persist the counter or seed it from the clock.
6. **Members that vanish.** `MemberLeaveWorld` / `Online` at `+0x70` exist because a party
   survives a member logging out. `S_LOGOUT_PARTY_MEMBER` (0xFCD9) is the notification.
7. **Party chat never reaches World** (ChatType 1, 0x15, 0x19, 0x20 →
   `PartyManager::BroadcastPartyChatMessage`), so it must be fanned out here, over the same
   session registry whisper uses.
8. **`SA_BYPASS_TO_GROUP` (0x13F8) must become a real handler** once parties exist, or World's
   party-scoped client packets are dropped on the floor.
9. Invite timeouts are **not** the Arbiter's problem — the direct-invite dialogue is entirely
   World-side. The only Arbiter timeouts are on party *matching*
   (`S_ASK_INTER_PARTY_MAKE_PROGRESS` 0xDBA7 / `S_ASK_INTER_PARTY_MAKE_ABORT` 0x889D).

Wiring order, once T27 lands: `SA_JOIN_PARTY` → `PartyManager.AddMember` → broadcast
`S_PARTY_MEMBER_LIST` to every online member → mirror to World with `AS_DO_CREATE_PARTY` /
`AS_DO_ADD_PARTY_MEMBER`.

---

## 8. What T28 implemented

Pure packet layer only, in `World/DbProxyStaticData.cs` (editable), class `PartyPackets`:

- **Builders** for the 13 A→W opcodes in §5.1, each taking plain arguments and returning the
  payload. `BuildMemberBasicInfo` writes one 0xA0-byte `PartyMemberBasicInfo`.
- **Parsers** for the 11 W→A opcodes in §5.2, each returning a `readonly record struct` and
  `null` when the frame is shorter than the fixed size the real handler demands (so a short
  frame declines instead of inventing zeros — the real Arbiter drops the connection there, and
  declining is the closest safe analogue).
- **Parsers** for the 7 client-→-Arbiter party packets in §6.1, hand-written rather than
  `.def`-driven because two of the `.def` files are wrong (§6.3).
- The `PDId` pack/unpack helper.

Tests (`src/TeraSharp.Arbiter.Tests/Program.cs`, `Party_*`): every builder is asserted
byte-for-byte against the layout in §5, every parser round-trips its builder, the short-frame
paths return null, and one test pins each of the six `.def` corrections so a future codec
change cannot silently re-adopt the wrong layout.

**Not implemented, deliberately**: any `PartyManager`, any session wiring, any `S_PARTY_*`
send. Those need T27's per-session routing and a live two-client test.

---

## 9. Open questions

1. **No captured party bytes exist.** Everything here is decompile-derived. Step 6–9 of the
   capture checklist in `MULTIPLAYER-DESIGN.md` §8 is what turns these golden tests into
   byte-exact ones.
2. **`S_VIEW_PARTY_INVITE`'s first array element layout** was not traced (the second is
   `[u16 self][u16 next][u16 nameOff][int32][int32]` + name).
3. **`SA_SEND_PARTY_MEMBER_UPDATE_INFO` fields at +0x12 and +0x16** have their names in
   `&DAT_140ae7bac` / `&DAT_140ae7bb4`, which the dump does not resolve. Almost certainly
   CurHp/CurMp given `MaxHp`/`MaxMp` follow.
4. **The party-matching timeout duration** (`MA_ASK_TO_JOIN_PARTY_PROGRESS` 0x466F /
   `MA_ASK_TO_JOIN_PARTY_ABORT` 0x4662) was not traced. Only matters if matchmaking is built.
5. **`S_OTHER_USER_APPLY_PARTY`'s `unk1`/`unk2`** are `PartyMatchInfo+0x98` and bit 4 of
   `Account+0x2adc`; what they mean to the client is unknown.
