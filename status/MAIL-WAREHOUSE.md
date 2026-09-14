# MAIL-WAREHOUSE — parcels are Arbiter-owned SQL, warehouse is nine dedicated RPCs

T40 research; **T42 implemented the Arbiter half** — §7 says exactly what landed and what did
not. Same shape as `status/PARTY-DESIGN.md` (T28) and `status/DUNGEON-COOLTIME.md` (T25): pin every
layout from the decompile, say which of them a capture confirms, and list what breaks the first
time a live player touches the feature.

Sources: the decompiled `ArbiterServer.exe` (`D:\v100\TERA_SERVER.100\Arb_part_*.c`), the
decompiled `WorldServer.exe` (`world_decompiled\WorldServer.exe.c`), client opcode numbers from
`tera-server-proxy\data\data.json` `maps."376012"`, and W<->A opcode numbers from
`data/dbproxy_opcodes.txt` / `World/DbProxyOpcodeNames.cs` (both generated from the opcode->name
switch in `WorldServer.exe.c` `FUN_140146f50`). Every opcode below was cross-checked two ways: the
name table, and the literal the sending function passes to the packet writer.

**Exactly one frame in any capture belongs to this whole area**: `S_PARCEL_READ_RECV_STATUS` in
`cap_newchar_client.log` frame 312. Everything else here is decompile-only, and that is stated
per-section rather than glossed.

---

## 0. The two corrections to the T40 brief

The brief said:

> the Arbiter owns parcels (`C_SHOW_PARCEL_MESSAGE`, `C_PARCEL_READ_RECV_STATUS`,
> `C_RETURN_USER_GIFT`); warehouse item moves go through `SDB_ITEM_SINGLE` atoms we already store
> (pocket ids)

**1. `C_RETURN_USER_GIFT` is not a parcel packet.** It is the returning-player reward claim.
`Handler_C_RETURN_USER_GIFT` (`FUN_1404eb830`, `Arb_part_041.c:10155`) reads no body at all and
calls `LogWrapperEx::LogReturnUserForDebugging` then `ReturnUserManager::SendReturnUserReward`
(`FUN_14040f320`, `Arb_part_033.c:5773`), which sends **AS opcode `0x15c0`** to World. It never
touches `ParcelManager`. The packet the brief was reaching for is `C_RETURN_PARCEL` (`0xf294`) —
return-to-sender — and **World** handles that one (`Handler_C_RETURN_PARCEL` exists only in
`WorldServer.exe.c`), then asks the Arbiter with `SDB_RETURN_PARCEL` `0x2781`. The Arbiter owns
**three** client parcel packets, not the three named: `C_SHOW_PARCEL_MESSAGE`,
`C_PARCEL_READ_RECV_STATUS` and `C_PARCEL_REPORT`.

**2. Bag<->warehouse moves do not go through `SDB_ITEM_SINGLE`, and cannot.** `SDB_ITEM_SINGLE`
binds exactly one inventory — the bag — so an atom naming a warehouse container resolves to NULL
inside the transaction. §6 is the proof chain. The dedicated `SDB_STORE_WAREHOUSE` /
`SDB_GET_WAREHOUSE` pair is the only path.

The half of the brief that **is** right, and matters: the pocket id in an item row *is*
`enum INVEN_TYPE`, and it is exactly what selects the container. §6.3 pins the values from the
decompile. Our stored pocket ids are the right key — they are just read by a different message.

---

## 1. Where the code is

| area | file |
|---|---|
| `Handler_C_*` parcel handlers (Arbiter-owned client packets) | `Arb_part_041.c:4888`, `:4926`, `:11654` |
| `Handler_SDB_*_PARCEL` (World -> Arbiter) | `Arb_part_071.c:15145–16230` |
| parcel packet builders (`DBS_MAKE/RECV/RECV_EX`) | `Arb_part_071.c:4827`, `:4968`, `:5118` |
| `ParcelManager` (all methods) | `Arb_part_082.c` |
| `Handler_SDB_*_WAREHOUSE` (World -> Arbiter) | `Arb_part_048.c:12070–14100` |
| `Handler_SDB_INCREASE_WAREHOUSE_SIZE` | `Arb_part_063.c:8503` |
| `Handler_SDB_ITEM_SINGLE` / `_DELIVER` / `_TRADE` | `Arb_part_063.c:11656` / `:10088` / `:11815` |
| `TransSQLExec` (ctors, `GetInven`, `IsAccountDbIdInvenType`) | `Arb_part_037.c:11701`, `Arb_part_038.c:5918`, `:11173` |
| PDL dumpers — the authoritative field names + offsets | `Arb_part_015.c`–`Arb_part_018.c` |

Singletons: `DAT_141299b00` = `ParcelManager`, `DAT_141214fe8` = `UserManager`,
`DAT_140f02a78` = `ReturnUserManager`.

Every offset below is **frame-relative** for `SDB_`/`DBS_` messages (payload offset = frame − 6,
the usual TeraSharp convention) and **packet-absolute** for client packets (body index = offset − 4).

---

## 2. The three client packets the Arbiter answers itself

### 2.1 `C_SHOW_PARCEL_MESSAGE` `0xfa59` -> `S_SHOW_PARCEL_MESSAGE` `0xabd3`

`Handler_C_SHOW_PARCEL_MESSAGE` = `FUN_1404edc40` (`Arb_part_041.c:11654`, tracer `:11681`).
Guard: `param_3 < 8` -> `GET_CLIENT_BUFFER_BUFSIZE_MISMATCH`, so a minimum 8-byte packet.

```
C_SHOW_PARCEL_MESSAGE   [u16 len=8][u16 0xfa59][u32 parcelId]
S_SHOW_PARCEL_MESSAGE   [u16 len][u16 0xabd3][u16 msgOffset=10][u32 parcelId][wchar message][u16 0]
```

The handler reads `parcelId` at packet+4, calls `ParcelManager::GetParcelData` (`FUN_140965a90`)
into a 0x430-wchar buffer, and writes the reply with the standard backpatched string ref: a `u16`
slot set to 0, then the `u32 parcelId`, then the slot is backpatched to the running frame length
(always 10) and the wide string is appended with its terminator.

> **Security finding, worth writing down before we copy the behaviour.** The ownership test comes
> **after** the reply is already sent:
> ```
> ... send S_SHOW_PARCEL_MESSAGE ...
> if (*(int *)(user + 0x120) == parcelData.RecverDbId /* +0x50 */) {
>     ParcelManager::SetParcelRead(parcelId);
>     ParcelManager::SendReadRecvStatusInfo(user, 0);
> }
> ```
> So on the real server any client can read **any** player's mail body by iterating parcel ids; the
> check only gates the read-flag and the counter push. TeraSharp should check ownership **before**
> building the reply and send nothing when it fails. That is a deliberate divergence from the
> original and is noted here so nobody "fixes" it back later.

### 2.2 `C_PARCEL_READ_RECV_STATUS` `0xe292` -> `S_PARCEL_READ_RECV_STATUS` `0xf26e`

`Handler_C_PARCEL_READ_RECV_STATUS` = `FUN_1404e3ca0` (`Arb_part_041.c:4888`, tracer `:4899`).
Guard `param_3 < 4` — i.e. **no body at all**, a bare 4-byte packet. It calls
`ParcelManager::SendReadRecvStatusInfo(user, false)` = `FUN_140979060` (`Arb_part_082.c:18805`),
which writes:

```
S_PARCEL_READ_RECV_STATUS  [u16 len=13][u16 0xf26e][u32 a][u32 b][u8 c]
```

**This is the one frame a capture confirms.** `cap_newchar_client.log` frame 312:

```
0D 00 6E F2  00 00 00 00  00 00 00 00  00
```

…an all-zero form for a character with no mail, sent unprompted right after
`S_LOAD_CLIENT_USER_SETTING` (frame 309). `a` and `b` are the two counters the mail button badge
uses (unread / unreceived); `c` is the bool the caller passes — every call site in the Arbiter
passes `0`.

### 2.3 `C_PARCEL_REPORT` `0xc09a` -> `S_PARCEL_REPORT` `0x73fc`

`Handler_C_PARCEL_REPORT` = `FUN_1404e3d60` (`Arb_part_041.c:4926`, tracer `:4951`). Guard
`param_3 < 0xe`.

```
C_PARCEL_REPORT  [u16 len][u16 0xc09a][u16 nameOffset][u32 parcelId][u32 reason][wchar name][u16 0]
S_PARCEL_REPORT  [u16 len=5][u16 0x73fc][u8 ok]
```

The handler looks the parcel up and, on failure, answers `0x73fc` with `ok = 0` immediately. The
success answer (`ok = 1`) comes back asynchronously from `ReportManager::ResponseParcelReport` after
the report round-trips. We have no report pipeline at all, so this is "reply 0 and log" for us.

### 2.4 Login: the Arbiter pushes `0xf26e` unprompted

`User::OnLoadTopoFin` (`Arb_part_029.c:12140`) calls `SendReadRecvStatusInfo` at `:12574`. That is
where capture frame 312 comes from — it is **not** a reply to anything the client sent.

**TeraSharp does not send it today.** `HandlerRegistry`'s in-world `C_LOAD_TOPO_FIN` branch ends at
`ClientSettingsHandlers.SendUserSetting`; the capture continues
`S_TRADE_BROKER_CALC_NOTIFY (0x6af1, 12 B, all zero)` then `S_SYSTEM_MESSAGE` then the 13-byte
`0xf26e`. Adding the 13 zero bytes is a two-line change in a human-owned file — §8 has the diff.

### 2.5 Everything else client-side belongs to World

`WorldServer.exe.c` has `Handler_C_` for: `C_LIST_PARCEL` `0xcd05`, `C_SEND_PARCEL` `0xd5b9`,
`C_SET_SEND_PARCEL_TYPE` `0x63e6`, `C_SET_SEND_PARCEL_ITEM` `0xca3f`, `C_SET_SEND_PARCEL_MONEY`
`0x82b1`, `C_CLEAR_SEND_PARCEL` `0xfdd9`, `C_CLOSE_SEND_PARCEL` `0x6e54`, `C_RECV_PARCEL` `0x5b4e`,
`C_RECV_PARCEL_ALL` `0xab24`, `C_RECV_PARCEL_EX` `0x532a`, `C_DELETE_PARCEL` `0xefd4`,
`C_RETURN_PARCEL` `0xf294`, `C_WAREHOUSE_AUTO_SORT` `0xe117`, `C_PAY_WAREHOUSE_COMMISION` `0x53c8`.
All of those reach us only as `SDB_*` over the World link, which is what §3–§7 are about.

---

## 3. Parcels: the six W<->A pairs

Opcode numbers confirmed twice — the `WorldServer.exe.c` name switch (already in
`data/dbproxy_opcodes.txt`) and the literal each sender passes to the writer.

| SDB (W->A) | DBS (A->W) | Arbiter handler | opcode literal read at |
|---|---|---|---|
| `0x2777` SDB_LIST_PARCEL | `0x2778` | `FUN_14082fc50` `Arb_part_071.c:15271` | `:15377` |
| `0x2779` SDB_MAKE_PARCEL | `0x277a` | `FUN_140830140` `:15470` | builder `FUN_140821b70` `:4847` |
| `0x277b` SDB_RECV_PARCEL | `0x277c` | `FUN_140830970` `:15797` | builder `FUN_140822150` `:5138` |
| `0x277d` SDB_RECV_PARCEL_EX | `0x277e` | (shares the RECV path) | builder `FUN_140821e40` `:4989` |
| `0x2781` SDB_RETURN_PARCEL | `0x2782` | `FUN_140831240` `:16136` | `:16191` |
| `0x2811` SDB_DELETE_PARCEL | `0x2812` | `FUN_14082f950` `:15145` | `:15246` |

Watch out: `0x27a6` appears a few lines below the `DBS_MAKE_PARCEL` tracer in `Arb_part_071.c`, but
it belongs to `Handler_SDB_PEGASUS_FEE` (`FUN_140830640`, `:15669`) — a neighbouring function, not
the parcel reply. Pairing a tracer string with "the next opcode literal" is wrong here; pair by
enclosing function.

### 3.1 Layouts (from the PDL dumpers, which emit `L"FieldName"` + offset)

```
SDB_LIST_PARCEL      0x2777   min 23 B    dumper FUN_140233850  Arb_part_017.c:9634
  [6]  u32 DlmId          [10] u32 UserDbId     [14] u32 ViewType
  [18] u32 CurPage        [22] u8  UncheckedOnly

DBS_LIST_PARCEL      0x2778   min 35 B    dumper FUN_1401f7f30  Arb_part_015.c:8801
  [6]  u32 DataList offset (frame-rel)    [10] u32 DataList bytes
  [14] u32 DlmId          [18] u8  Success      [19] u32 ViewType
  [23] u32 CurPage        [27] u32 MaxPage      [31] u32 ParcelCount
  [35] N x ParcelDataNoMsg (0x9e8 = 2536 B each)

SDB_MAKE_PARCEL      0x2779   min 26 B    dumper FUN_140235830  Arb_part_017.c:10975
  [6]  ParcelData ref (offset+count)      [14] ParcelTransList ref     [22] u32 DlmId

DBS_MAKE_PARCEL      0x277a   min 27 B    dumper FUN_1401fcd50  Arb_part_015.c:12172
  [6]  ParcelTransList ref                [14] u32 DlmId        [18] u8 Success
  [19] u32 SendParcelError                [23] u32 ParcelRecverDbId

SDB_RECV_PARCEL      0x277b   min 26 B    dumper FUN_140236f90  Arb_part_017.c:11999
  [6]  ParcelTransList ref                [14] u32 DlmId        [18] u32 Step
  [22] u32 ParcelId

DBS_RECV_PARCEL      0x277c   min 31 B    dumper FUN_1401fdfa0  Arb_part_015.c:12981
  [6]  ParcelData ref     [14] ParcelTransList ref
  [22] u32 DlmId          [26] u32 Step         [30] u8 Success

SDB_RECV_PARCEL_EX   0x277d   min 35 B    dumper FUN_1402370f0  Arb_part_017.c:12060
  [6]  ref A              [14] ref B (the dumper names only ParcelTransList)
  [22] u32 DlmId          [26] u32 Step         [30] u32 OwnerDBID   [34] u8 IsAllParcel

DBS_RECV_PARCEL_EX   0x277e   min 35 B    dumper FUN_1401fe150  Arb_part_015.c:13056
  [6]  ref A              [14] ref B
  [22] u32 DlmId          [26] u32 Step         [30] u32 No_parcel   [34] u8 Success

SDB_RETURN_PARCEL    0x2781   min 14 B    dumper FUN_1402395f0  Arb_part_017.c:13649
  [6]  u32 DlmId          [10] u32 ParcelId

DBS_RETURN_PARCEL    0x2782   min 11 B    dumper FUN_140200ce0  Arb_part_015.c:14832
  [6]  u32 DlmId          [10] u8 Success

SDB_DELETE_PARCEL    0x2811   min 23 B    dumper FUN_14022dd70  Arb_part_017.c:5606
  [6]  DelList ref        [14] u32 DlmId        [18] u32 UserDbId    [22] u8 IsSendParcel

DBS_DELETE_PARCEL    0x2812   min 11 B    dumper FUN_1401f2880  Arb_part_015.c:4949
  [6]  u32 DlmId          [10] u8 Success
```

A "ref" is the usual 8-byte pair `[u32 frame-relative offset][u32 byte count]`, both backpatched by
the writer after the fixed fields; the writer sets the offset slot to the running frame length
(`*slot = *cursor`) exactly as `TunnelFrames`/`SocialHandlers` already do.

`ParcelTransList` elements are **`ItemTransactionAtom`, 0x358 = 856 bytes each** — the same atom
`SDB_ITEM_SINGLE` carries, so `CloneAtomList` and the insert-id allocation in `DbProxyHandlers`
apply unchanged. Proof of the stride: both the parcel and the warehouse handlers size the vector
with `(byteCount - 1) / 0x358 + 1` (e.g. `Arb_part_063.c:8550`).

`ParcelDataNoMsg` is **0x9e8 = 2536 bytes** — read directly from the `DBS_LIST_PARCEL` writer's
bounds check `if (local_d0 < *local_d8 + 0x9e8)` (`Arb_part_071.c:15396`). The full `ParcelData`
with the message body is larger (a `memset` of `0xdd8` appears at `Arb_part_082.c:11099`); the
receiver dbId sits at `+0x50` (§2.1). **The rest of the struct's interior is not pinned** — it is
the Arbiter's own SQL row shape, and for a byte-exact `DBS_LIST_PARCEL` we would have to pin all
2536 bytes from a capture we do not have. See §9.

### 3.2 Storage — the Arbiter owns parcels in SQL, like friends

Stored procedures, all called from the Arbiter and none from World:

| SP | params | used by |
|---|---|---|
| `dbo.spLoadAllParcelLog` | — | boot; `ParcelManager::InitParcelLog` loads the whole table into RAM |
| `dbo.spCreateParcel` | **30** | `MakeParcel` |
| `dbo.spUpdateParcelRecved` | 3 | `SetParcelRecved` |
| `dbo.spUpdateParcelStatus` | 3 | `UpdateParcelStatus` |
| `dbo.spUpdateParcelTraceSendOff` | 1 | trace/GM |
| `dbo.spQADeleteAllParcels` | — | GM |
| `dbo.spQASubParcelDays` | 2 | GM (ages parcels for expiry testing) |

`ParcelManager` methods worth knowing about when we build ours: `MakeParcel`, `RecvParcel`,
`RecvParcelEx`, `ReturnParcel`, `DeleteParcel`, `DeleteSentParcel`, `ClearRecvedParcel`,
`DeleteExpiredParcels`, `SeizeParcel`, `HaveItemOrMoneyInParcel`, `CanSetParcelRecved`,
`SetParcelRead`, `SetParcelRecved`, `GetParcelList`, `GetParcelListAll`, `GetParcelData`,
`SendReadRecvStatusInfo`, `FlushPendedEscrowReturnParcels`, and seven `SendSystemParcel*`
entry points (escrow, escrow-accept, escrow-return, normal, present-new, present-move,
present-new-to-guild-member). Locking is a per-parcel-owner read/write lock
(`GetParcelWithReadLock` / `GetParcelWithWriteLock` / `GetOrCreateParcelOwnerWithLock`).

Config keys read for parcels: `escrowReturnWait`, `expireDay`, `maxRecvListCnt`, `maxSendListCnt`,
`sendHomunParcelTax`, `escrowMoney`. (Key **names** are verified; the **values** in this build's
`ServerConfig.xml` are not — read them off the live config rather than trusting any number quoted
elsewhere.)

---

## 4. Warehouse: nine dedicated W<->A pairs

| SDB (W->A) | DBS (A->W) | Arbiter handler |
|---|---|---|
| `0x274a` SDB_VIEW_WAREHOUSE | `0x274b` | `FUN_1405b6490` `Arb_part_048.c:13696` |
| `0x274c` SDB_STORE_WAREHOUSE | `0x274d` | `FUN_1405b5760` `:13188` |
| `0x274e` SDB_GET_WAREHOUSE | `0x274f` | `FUN_1405b4850` `:12624` |
| `0x2754` SDB_MOVE_WAREHOUSE_ITEM | `0x2755` | `FUN_1405b53c0` `:13050` — **dead stub** |
| `0x277f` SDB_CHANGE_WAREHOUSE_POS | `0x2780` | `FUN_1405b39d0` `:12070` |
| `0x27c9` SDB_PAY_WAREHOUSE_COMMISION | `0x27ca` | `FUN_1405b5400` `:13064` |
| `0x27e0` SDB_CLEAR_WAREHOUSE | `0x27e1` | `FUN_1405b4200` `:12387` |
| `0x27e2` SDB_WAREHOUSE_AUTO_SORT | `0x27e3` | `FUN_1405b6a60` `:13904` |
| `0x283f` SDB_INCREASE_WAREHOUSE_SIZE | **`0x283e`** (!) | `FUN_140745e30` `Arb_part_063.c:8503` |

### 4.1 Two traps in that table

**`SDB_MOVE_WAREHOUSE_ITEM` `0x2754` is a stub that sends nothing.** `FUN_1405b53c0` is ten lines
long: enter the scope tracer, leave the scope tracer, `return 1`. No DB work, no `DBS_` reply. If
World ever sends `0x2754`, the real Arbiter head-blocks that user's DLM queue forever — so either
World never sends it in this build, or it is a latent bug in the original. Either way TeraSharp
must **not** helpfully answer it: matching the original means logging and dropping it. Do not put
it in the replay table as a request.

**`SDB_INCREASE_WAREHOUSE_SIZE` is answered with `DBS_INCREASE_INVENTORY_SIZE` `0x283e`, not
`0x2840`.** `Handler_SDB_INCREASE_WAREHOUSE_SIZE` (`Arb_part_063.c:8588`) and
`Handler_SDB_INCREASE_INVENTORY_SIZE` (`:8490`) both reply through the same helper
`FUN_1407ad2e0` (`Arb_part_066.c:17451`), and that helper writes the literal `0x283e`. The two DBS
layouts are byte-identical (`[6] u8 Success`, `[7] u32 DlmId`), so World's DLM matching still
works. `0x2840 DBS_INCREASE_WAREHOUSE_SIZE` exists in the name table and has a dumper, but nothing
sends it. **Replying `0x2840` would hang the user.**

### 4.2 Layouts

`u64` fields are the ones the dumper prints with `FUN_14016c4c0`.

```
SDB_VIEW_WAREHOUSE   0x274a  min 34 B   dumper FUN_1402478d0  Arb_part_018.c:3394
  [6]  u32 DlmId       [10] u64 OwnerDBID     [18] u64 ArbiterUser
  [26] u32 InvenType   [30] u32 ViewPos

DBS_VIEW_WAREHOUSE   0x274b  min 45 B   dumper FUN_14020b4d0  Arb_part_016.c:2106
  [6]  ItemList ref (offset+bytes)
  [14] u32 DlmId       [18] u8  Success       [19] u32 ViewPos
  [23] u32 ViewSize    -- hard-coded literal 0x48 = 72 at Arb_part_048.c:13834
  [27] u32 EndPos      [31] u32 TotalItemNum  [35] u64 CurrentMoney
  [43] u16 MaxSlotCount
  [45] N x ItemData (0x218 = 536 B each -- the same record DBS_USER_LOAD_INVENTORY 0x27A4 uses,
                     see status/INVENTORY-DESIGN.md)

SDB_STORE_WAREHOUSE  0x274c  min 63 B   dumper FUN_14023b6e0  Arb_part_017.c:15088
  [6]  StoreBinary ref
  [14] u32 DlmId       [18] u64 OldOwnerDBID  [26] u64 NewOwnerDBID
  [34] u32 InvenType   [38] u8  ByQAC         [39] u32 ItemTemplateId
  [43] u32 ItemAmountDelta                    [47] u64 ItemDbId
  [55] u64 MoneyDelta

SDB_GET_WAREHOUSE    0x274e  min 62 B   dumper FUN_14022f8b0  Arb_part_017.c:6825
  [6]  StoreBinary ref
  [14] u32 DlmId       [18] u64 OldOwnerDBID  [26] u32 WareInvenType
  [30] u64 NewOwnerDBID                       [38] u32 ItemTemplateId
  [42] u32 ItemAmountDelta                    [46] u64 ItemDbId
  [54] u64 MoneyDelta

DBS_STORE_WAREHOUSE  0x274d  |  DBS_GET_WAREHOUSE 0x274f  |  DBS_CHANGE_WAREHOUSE_POS 0x2780
                     all three min 31 B, identical shape
  [6]  binary ref      [14] u32 DlmId         [18] u8  Success
  [19] u32 Error       [23] u64 WareCommision

SDB_CHANGE_WAREHOUSE_POS 0x277f  min 34 B   dumper FUN_14022bea0  Arb_part_017.c:4239
  [6]  ItemBinary ref  [14] u32 DlmId         [18] u64 OwnerDBID
  [26] u32 InvenType   [30] u32 OwnerUserDbId

SDB_CLEAR_WAREHOUSE  0x27e0  min 18 B   dumper FUN_14022c540  Arb_part_017.c:4531
  [6]  u32 DlmId       [10] u32 OwnerDBID     [14] u32 InvenType
DBS_CLEAR_WAREHOUSE  0x27e1  min 11 B
  [6]  u32 DlmId       [10] u8  Success

SDB_WAREHOUSE_AUTO_SORT 0x27e2  min 26 B  dumper FUN_140247b40  Arb_part_018.c:3498
  [6]  u32 DlmId       [10] u32 UserDbId      [14] u32 InvenType
  [18] u32 SortBegin   [22] u32 SortEnd
DBS_WAREHOUSE_AUTO_SORT 0x27e3  min 39 B  dumper FUN_14020b750  Arb_part_016.c:2212
  [6]  u32 DlmId       [10] u8  Success       [11] u32 UserDbId
  [15] u32 InvenType   [19] u32 SortBegin     [23] u32 SortEnd
  [27] u32 ErrorMsg    [31] u64 WareCommision

SDB_PAY_WAREHOUSE_COMMISION 0x27c9  min 26 B  dumper FUN_140236750  Arb_part_017.c:11640
  [6]  u32 DlmId       [10] u32 OwnerDBID     [14] u32 InvenType   [18] u64 Commision
DBS_PAY_WAREHOUSE_COMMISION 0x27ca  min 15 B  dumper FUN_1401fd9f0  Arb_part_015.c:12727
  [6]  u32 DlmId       [10] u8  Success       [11] u32 Error

SDB_INCREASE_WAREHOUSE_SIZE 0x283f  min 34 B  dumper FUN_140230a00  Arb_part_017.c:7577
  [6]  ItemBinary ref  [14] u32 DlmId         [18] u32 UserDbId
  [22] u32 InvenType   [26] u32 DeltaAmount   [30] u32 ExpandItemTemplateId
reply: DBS_INCREASE_INVENTORY_SIZE 0x283e  min 11 B  dumper FUN_1401f4b80  Arb_part_015.c:6483
  [6]  u8  Success     [7]  u32 DlmId          -- NOTE: Success FIRST, like 0x2892/0x2911

SDB_MOVE_WAREHOUSE_ITEM 0x2754  min 38 B  dumper FUN_140235c00  Arb_part_017.c:11147
  [6]  u32 DlmId       [10] u32 SendCharDBID  [14] u32 RecvCharDBID
  [18] u64 ItemDBID    [26] u32 ItemAmount    [30] u64 Money
DBS_MOVE_WAREHOUSE_ITEM 0x2755  min 23 B  -- declared, never sent (see §4.1)
  [6]  u32 DlmId       [10] u8  Success       [11] u32 Error      [15] u64 WareCommision
```

### 4.3 Storage

| SP | params |
|---|---|
| `dbo.spLoadWarehouse` | 2 |
| `dbo.spClearWarehouse` | 2 |
| `dbo.spUpdateWarehouseSlotCount` | 3 |
| `dbo.spUpdateWarehousePay` | — |
| `dbo.spUpdateWareMoney` | — |
| `dbo.spUpdateWareInvenSort` | — |

Items are **not** in a separate warehouse table. They live in the same `Items` table the bag uses,
keyed `(OwnerDbId, InvenType)`; `spUpdateItemOwner` moves a row between containers,
`spUpdateItemPos` moves it within one. That is exactly the shape `CharacterStore` already has for
inventory rows — which is why the brief's instinct was half right even though the message is not
`SDB_ITEM_SINGLE`.

Config keys: `useStyleWarehouse` / `IsUseStyleWarehouse`, `warehouseStorable`,
`userWarehouseStorable`, `guildWarehouseStorable`, `wareMaxStack`, `MaxSharedIncWarePageCount`,
`SharedIncWarePageCount`, `MaxSharedIncStyleWarePageCount`, `SharedIncStyleWarePageCount`.
Names verified; values not.

---

## 5. `ItemTransactionAtom` vs `ItemData` — two records, do not mix them

| record | size | carried by |
|---|---|---|
| `ItemTransactionAtom` | `0x358` = 856 B | `SDB_ITEM_SINGLE` `0x2768`, `SDB_ITEM_TRADE`, `SDB_ITEM_DELIVER`, and every parcel/warehouse **transfer** message (`ParcelTransList`, `StoreBinary`, `ItemBinary`) |
| `ItemData` | `0x218` = 536 B | `DBS_USER_LOAD_INVENTORY` `0x27A4` and `DBS_VIEW_WAREHOUSE` `0x274b` — the **listing** messages |

Fields of the atom we already rely on, re-confirmed here from `TransSQLExec::GetInven`
(`Arb_part_038.c`, `FUN_140497f00`): `+0x20` = `OwnerDbId` (`int64`), `+0x28` = `InvenType`
(`u32`), `+0x1d8` / `+0x1dc` = the two halves of `ItemPos`.

---

## 6. Why warehouse moves cannot ride `SDB_ITEM_SINGLE`

### 6.1 `TransSQLExec` has two constructors

```
FUN_1404832f0   Arb_part_037.c:11720   TransSQLExec::TransSQLExec(class BaseInventory *)
FUN_140483290   Arb_part_037.c:11701   TransSQLExec::TransSQLExec(class BaseInventory *, class BaseInventory *)
```

### 6.2 `GetInven` resolves an atom against the bound inventories only

`TransSQLExec::GetInven(__int64 ownerDbId, enum INVEN_TYPE)` = `FUN_140497f00`
(`Arb_part_038.c`, tracer `:5918`), decompiled:

```c
plVar2 = (longlong *)*param_1;                       // bound inventory #1
if (ownerDbId == plVar2[0xc]) {                      // OwnerDbId matches?
    if ((**(code **)(*plVar2 + 0x20))(plVar2, invenType)) return plVar2;   // accepts this INVEN_TYPE?
}
plVar2 = (longlong *)param_1[1];                     // bound inventory #2
if (ownerDbId == plVar2[0xc]) {
    if ((**(code **)(*plVar2 + 0x20))(plVar2, invenType)) return plVar2;
}
return 0;                                            // -> NULL -> transaction fails
```

There is no third lookup and no global inventory registry. An atom whose `(OwnerDbId, InvenType)`
pair matches neither bound inventory resolves to NULL.

### 6.3 `Handler_SDB_ITEM_SINGLE` binds exactly one — the bag

`Handler_SDB_ITEM_SINGLE` = `FUN_14074aca0`, `Arb_part_063.c:11656–11811`. The only
`TransSQLExec` construction in the whole function is at `:11756`:

```c
FUN_1404832f0(local_c8, lVar6 + 0x3c80);   // TransSQLExec(BaseInventory*) -- User+0x3c80 = the bag
```

Contrast the siblings, which bind two:

```c
Arb_part_063.c:10201  Handler_SDB_ITEM_DELIVER   FUN_140483290(.., userA + 0x3c80, userB + 0x3c80)
Arb_part_063.c:11928  Handler_SDB_ITEM_TRADE     FUN_140483290(.., userA + 0x3c80, userB + 0x3c80)
Arb_part_048.c:12869  Handler_SDB_GET_WAREHOUSE  FUN_140483290(.., inven + 0xf20, inven + 0xf6c)
Arb_part_048.c:13423  Handler_SDB_STORE_WAREHOUSE FUN_140483290(.., inven + 0xf20, inven + 0xf6c)
Arb_part_048.c:12925 / 13368                     FUN_140483290(.., user + 0x3c80, account-side inven)
```

So: send a warehouse `InvenType` inside a `0x2768` atom and it resolves to NULL. `SDB_STORE_WAREHOUSE`
/ `SDB_GET_WAREHOUSE` are the only bag<->warehouse path, and they exist precisely because the
single-inventory transaction cannot express the move.

### 6.4 `INVEN_TYPE` — the pocket ids, pinned

`Handler_SDB_INCREASE_WAREHOUSE_SIZE` (`Arb_part_063.c:8568–8581`) dispatches on the `InvenType`
field and is the clearest proof in the binary:

```c
iVar3 = *(int *)(req + 0x16);                                  // InvenType
if (iVar3 == 1)    inven = *(longlong *)(user + 0x3f40) + 0x88;      // Account + 0x88
else if (iVar3 == 9)   inven = user + 0x3db0;
else if (iVar3 == 0xc && styleWarehouseEnabled)
                       inven = *(longlong *)(user + 0x3f40) + 0x150; // Account + 0x150
```

Combined with `User + 0x3c80` = the bag (§6.3) and the two bitmasks:

* `TransSQLExec::IsAccountDbIdInvenType` (`FUN_14049ac90`, `Arb_part_038.c:11173`):
  `type < 0xd && ((0x1132 >> type) & 1)` -> **{1, 4, 5, 8, 12}** are keyed by **AccountDbId**;
  everything else by CharacterDbId.
* mask `0x120a`, used at `Arb_part_047.c:13295`, `:14012`, `:15429`, `:16006`, `:16118`, `:16165`,
  `:16203` and `Arb_part_048.c:5257` -> **{1, 3, 9, 12}** are the warehouse family.

| INVEN_TYPE | container | keyed by | warehouse family |
|---|---|---|---|
| 0 | bag (`User+0x3c80`) | character | no |
| **1** | **account warehouse** (`Account+0x88`) | **account** | **yes** |
| **3** | **guild warehouse** | guild | **yes** |
| 4, 5 | vending / broker escrow (account-keyed, not warehouses) | account | no |
| 8 | (account-keyed, unidentified) | account | no |
| **9** | **character warehouse** (`User+0x3db0`) | character | **yes** |
| **12 (0xC)** | **style / wardrobe warehouse** (`Account+0x150`) | **account** | **yes** |

The values **1, 9 and 12 are proven** by the dispatch above; the membership of 3, 4, 5 and 8 in the
masks is proven, and the *labels* for 3/4/5/8 are inference from surrounding call sites — flagged
rather than asserted. Nothing in either binary contains an `INVEN_TYPE` name table.

`ViewSize` is a hard-coded `0x48` = **72 slots per warehouse page**: the literal is written
straight into `DBS_VIEW_WAREHOUSE` at `Arb_part_048.c:13834`. Page/slot caps and the
commission rate come from `ServerConfig.xml`, not from the binary — read them live.

---

## 7. What T42 implemented

T40 was research; **T42 built the Arbiter half**. What landed:

| piece | where | state |
|---|---|---|
| the three Arbiter-owned client packets + the login push | `Handlers/ParcelHandlers.cs` (new) | real |
| `parcels` / `parcel_items` tables | `Persistence/CharacterStore.cs` | real |
| `items` / `warehouses` tables — one row per item, keyed `(owner_db_id, inven_type)` | `Persistence/CharacterStore.cs` | real |
| the eight live warehouse requests | `World/WarehouseHandlers.cs` (new) + `DbProxyHandlers` | real |
| `0x2754` sealed one-way | `World/WorldReplayTable.cs` | real |
| the six `SDB_*_PARCEL` requests World sends | — | **not done** |

So the bank works end to end — `SDB_VIEW_WAREHOUSE` is rebuilt from the item rows, and
`SDB_STORE_WAREHOUSE` / `SDB_GET_WAREHOUSE` apply the atoms to those rows and echo them back with
the ids we allocated — and mail does not: opening the mailbox still sends `SDB_LIST_PARCEL`
`0x2777`, which nothing answers.

**There is no login-time warehouse load.** No `SDB_LOAD_WAREHOUSE` opcode exists anywhere in the
0x2700-0x29FF table, and neither capture contains a single warehouse frame; `SDB_VIEW_WAREHOUSE`
arrives the first time the player talks to a bank NPC and never before. That also means the empty
form cannot be checked byte-for-byte against a capture the way `S_PARCEL_READ_RECV_STATUS` can — it
is checked against the writer and the guard instead, which is stated plainly in the tests.

The failure signature for what is still missing is the usual one from `status/HANDOFF.md` §1: the
first mailbox a live player opens produces

```
no replay for 0x2777
```

and head-blocks that character's DLM queue for the life of the World process.

Two rules that must survive any later edit, both from §4.1:

1. `SDB_INCREASE_WAREHOUSE_SIZE` `0x283F` is answered with **`0x283E`**, never `0x2840`.
2. `SDB_MOVE_WAREHOUSE_ITEM` `0x2754` is answered with **nothing**, and stays in
   `WorldReplayTable.OneWayFromWorld` so the replay table cannot hand it somebody else's reply.

### Deliberate divergences from the original

- **Mail bodies are authorised before they are sent.** The real handler sends the body first and
  tests ownership afterwards (§2.1), so on the real server any client can read any player's mail
  by walking parcel ids. `ParcelHandlers.OnShowParcelMessage` checks first and sends nothing when
  the parcel is not the caller's.
- **The bag is not a tracked container.** `WarehouseHandlers.Apply` skips any atom that touches no
  warehouse pocket, because the login inventory still comes from `data/starter_inventory.bin`
  (T20) and a bag row here would be a second source of truth that disagrees with it. The flip
  side: an atom that banks an item whose id we have never seen **inserts** the row rather than
  failing, which is what makes banking a starter item work.

---

## 8. The human-owned diff

Two lines in **`Handlers/HandlerRegistry.cs`**. Everything else T42 touched is Cowork-editable.

**(a)** In the in-world `C_LOAD_TOPO_FIN` branch, after `ClientSettingsHandlers.SendUserSetting(s);`:

```csharp
                ClientSettingsHandlers.SendUserSetting(s);
                // Real Arbiter: User::OnLoadTopoFin -> ParcelManager::SendReadRecvStatusInfo
                // (Arb_part_029.c:12574). cap_newchar_client.log frame 312, 13 B, all zero.
                ParcelHandlers.SendReadRecvStatus(s);
```

**(b)** With the other `Reg(...)` calls, next to the social handlers:

```csharp
        var parcels = new ParcelHandlers(loggerFactory.CreateLogger<ParcelHandlers>());
        Reg("C_SHOW_PARCEL_MESSAGE", 4, parcels.OnShowParcelMessage);
        Reg("C_PARCEL_READ_RECV_STATUS", 0, parcels.OnParcelReadRecvStatus);
        Reg("C_PARCEL_REPORT", 10, parcels.OnParcelReport);
```

The `minLen` values are the real handler's guard minus the 4-byte header: `frame >= 8` -> 4,
`frame >= 4` -> 0, `frame >= 0x0E` -> 10.

(Frame 310 in the capture is also a 12-byte all-zero `S_TRADE_BROKER_CALC_NOTIFY` `0x6AF1`, sent
between the user setting and the system message. Not parcel-related; mentioned so the burst can be
completed in one edit if the human wants byte-parity with the capture.)

---

## 9. Open questions

- **`ParcelDataNoMsg` interior (0x9e8 B).** Needed for a `DBS_LIST_PARCEL` the Arbiter builds
  itself. Neither capture contains one - T45 parsed `arb_world.log` (950 frames) and
  `cap_newchar.log` (4218 frames) for all twelve parcel opcodes and found zero. The known anchors are: receiver dbId at `+0x50` (from the ownership test in
  `Handler_C_SHOW_PARCEL_MESSAGE`), and the record is the message-less prefix of the full
  `ParcelData`. A live session that sends one parcel and opens the mailbox would pin it; until
  then, `ParcelCount = 0` is the only honest answer.
- **The `RECV_PARCEL_EX` second ref.** Both the request and the reply carry two 8-byte ref slots,
  but the dumper names only `ParcelTransList`. The other slot is unnamed in the binary.
- **`SDB_MOVE_WAREHOUSE_ITEM` `0x2754`.** Is it dead in World too, or does some path (guild
  warehouse transfer?) still send it? `WorldServer.exe.c` mentions the name, but it was not traced
  to a live call site. If World does send it, the real server hangs there, and we need to decide
  whether to match that.
- **`INVEN_TYPE` 3, 4, 5, 8 labels.** Mask membership is proven; the names are inference.
- **Config values.** Every key name in §3.2 and §4.3 is verified from the binary; none of the
  values is. Read them from the live `ServerConfig.xml` before hard-coding anything.
- ~~**The six `SDB_*_PARCEL` requests are still unanswered**~~ - **done in T45**
  (`World/ParcelDbHandlers.cs`, `status/CLIENT-REJECTS.md` §4). The empty `DBS_LIST_PARCEL` turned
  out to be byte-exact from the decompile alone: every field is either echoed from the request or
  a default `Handler_SDB_LIST_PARCEL` sets at Arb_part_071.c:15334 (`local_ec = 1` MaxPage,
  `local_f0 = 0` ParcelCount), and the writer backpatches the list-offset slot to the running
  frame length unconditionally while the byte-count slot is only written in the non-empty branch.
  A fresh inbox is 35 bytes with offset 35 and bytes 0. The interior question above still stands
  for a non-empty list, which is why T45 keeps World's own `ParcelData` in `parcels.record` and
  replays it verbatim.
- **`MaxSlotCount` starts at 0** for an owner with no `warehouses` row, because the real caps
  (576 account / 360 character / 288 guild) live in `ServerConfig.xml`, which we do not read. If
  the client refuses to open a 0-slot bank, seed the row instead of guessing a constant.

---

## 10. Method note — pairing tracers with opcodes

Two near-misses in this task, both worth remembering:

1. In `Arb_part_071.c`, "the next `FUN_140350eb0` literal after the `PKT_DBS_MAKE_PARCEL_WRITE`
   tracer" is `0x27a6`, which belongs to `Handler_SDB_PEGASUS_FEE` in the **next** function. Always
   bound the search by the enclosing function.
2. The `SDB_*` opcode numbers are **not** in the Arbiter decompile at all — the Arbiter's handler
   registration goes through a data table. They are in `WorldServer.exe.c`, both as the literal each
   sender writes and as the `case 0xNNNN: return "SDB_..."` switch that
   `data/dbproxy_opcodes.txt` and `World/DbProxyOpcodeNames.cs` were already generated from. Both
   extractions were re-run for this task and agree on all 712 entries in `0x2700–0x29FF`; when a
   `SDB_`/`DBS_` number is wanted, that file is the answer and the Arbiter decompile is the wrong
   place to look.
