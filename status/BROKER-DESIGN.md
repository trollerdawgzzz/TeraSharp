# BROKER-DESIGN.md — the trade broker (T53, research + codec)

The last big Arbiter-owned social system. This is research and a codec only: there is **no
manager**, no store tables and no handler layer, exactly the shape `PARTY-DESIGN.md` and
`GUILD-DESIGN.md` had after their first task. What is here is pinned to the binary; what is not
here is named as missing.

Code: `src/TeraSharp.Arbiter/World/BrokerPackets.cs`. Tests: `Broker_*`.

---

## 0. The headline

**The broker is Arbiter-owned state with a World-owned item half.** Of the 21 `C_TRADE_BROKER_*`
packets in 376012, the Arbiter answers **16** itself and never sees the other **5** — and the
five it never sees are exactly the five that move an item between an inventory and the broker.
Those go to WorldServer, which turns each into an `SDB_TRADE_BROKER_*` on the DB-proxy link and
waits for the Arbiter's `DBS_` answer. The split is not a guess: the C_ name and the SDB_ name are
the same word.

| the five World owns | the DB-proxy pair it becomes |
|---|---|
| `C_TRADE_BROKER_REGISTER_ITEM` 0x7E74 | `SDB_..._REGISTER_ITEM` 0x2817 → `DBS_` 0x2818 |
| `C_TRADE_BROKER_UNREGISTER_ITEM` 0xB8BA | `SDB_..._UNREGISTER_ITEM` 0x2819 → `DBS_` 0x281A |
| `C_TRADE_BROKER_CALC_SOLD_ITEM` 0x7516 | `SDB_..._CALC_SOLD_ITEM` 0x281B → `DBS_` 0x281C |
| `C_TRADE_BROKER_CALC_BOUGHT_ITEM` 0x5C10 | `SDB_..._CALC_BOUGHT_ITEM` 0x281D → `DBS_` 0x281E |
| `C_TRADE_BROKER_BUY_IT_NOW` 0xF66D | `SDB_..._BUY_IT_NOW` 0x281F → `DBS_` 0x2820 |

Every one of those `SDB_` requests carries a **DlmId**, which makes each of them a per-user
DLMItem: an unanswered one head-blocks that character's DB queue for the life of the World
process (`status/HANDOFF.md` §1). **Opening the broker on a live TeraSharp today wedges the
character**, the same way opening the mailbox did before T45 — and for the same reason, because
no capture contains a broker frame either, so the replay table cannot cover for us.

That is the finding that matters most in this document.

---

## 1. Where the code is

| what | where |
|---|---|
| the 16 Arbiter-side `C_` handlers | `Arb_part_041.c:12715`–`13500`, one contiguous block |
| the 7 `SDB_` handlers | `Arb_part_064.c:8385`–`10140` |
| the PDL dumpers (**the field names**) | `C_` `Arb_part_014.c`, `DBS_` `Arb_part_015.c`, `SA_` `Arb_part_016.c`, `SDB_` `Arb_part_017.c`, `S_` `Arb_part_023.c`, `AS_` `Arb_part_012.c` |
| `TradeBroker::*` — the manager | `Arb_part_057.c` |
| `TradeBrokerSearchAgent::*` — search, sort, paging | `Arb_part_057.c`, `Arb_part_058.c` |

**Method.** Every layout below comes from the packet's own PDL dumper: a function that begins
`wcscpy_s(name, 0x100, L"<PACKET>")` and then emits one `L"FieldName"` per field with the offset
it reads. 57 of them exist — the whole family, every direction. Two independent checks make them
trustworthy:

1. each dumper's guard (`if (0xNN < param_2)`) lands exactly on the end of its last field, and
2. for all seven `SDB_` packets the dumper's guard equals the *handler's* guard, byte for byte.

The same pair of checks is what `GUILD-DESIGN.md` §5 rests on.

---

## 2. The opcodes — 57 of them

Client opcodes are from `data.json` `maps."376012"` **and** independently from WorldServer's own
opcode→name switch; all 36 client-facing ids agree. The inter-server ids are World's table only
(the client never sees them).

### 2.1 Client → Arbiter — the 16 the Arbiter answers

| opcode | packet | frame guard | **body** |
|---|---|---|---|
| 0xABCA | `C_TRADE_BROKER_BOUGHT_ITEM_LIST` | 4 | **0** |
| 0x961B | `C_TRADE_BROKER_CLOSE` | *none* | **0** |
| 0x9FA3 | `C_TRADE_BROKER_DEAL_CONFIRM` | 0x0C | **8** |
| 0xDB7F | `C_TRADE_BROKER_DEAL_PRICE_UPDATE` | 0x10 | **0x0C** |
| 0xEAFB | `C_TRADE_BROKER_HIGHEST_ITEM_LEVEL` | 4 | **0** |
| 0x76B4 | `C_TRADE_BROKER_HISTORY_ITEM_LIST_NEW` | **0x6C** | **0x68** |
| 0xE53F | `C_TRADE_BROKER_HISTORY_ITEM_LIST_PAGE` | 8 | **4** |
| 0x983A | `C_TRADE_BROKER_HISTORY_ITEM_LIST_SORT` | 9 | **5** |
| 0x6F34 | `C_TRADE_BROKER_INPUT_PRICE` | 0x10 | **0x0C** |
| 0xB981 | `C_TRADE_BROKER_REGISTERED_ITEM_LIST` | *none* | **0** |
| 0xB33F | `C_TRADE_BROKER_REJECT_SUGGEST` | 0x0C | **8** |
| 0x92E5 | `C_TRADE_BROKER_SOLD_ITEM_LIST` | 4 | **0** |
| 0xA788 | `C_TRADE_BROKER_SUGGEST_DEAL` | 0x10 | **0x0C** |
| 0x8DC7 | `C_TRADE_BROKER_WAITING_ITEM_LIST_NEW` | **0x75** | **0x71** |
| 0x8CFB | `C_TRADE_BROKER_WAITING_ITEM_LIST_PAGE` | 8 | **4** |
| 0x8863 | `C_TRADE_BROKER_WAITING_ITEM_LIST_SORT` | 9 | **5** |

The **body** column is the frame guard minus the 4-byte `[u16 len][u16 opcode]` header — the
number `PacketDispatcher.Register` wants. The same −4 that bit the guild and chat wirings
(`CLIENT-REJECTS.md` §7.2c); `BrokerPackets.MinBodyLength` does the subtraction once.

Five of the sixteen carry no fields at all — `_BOUGHT_ITEM_LIST`, `_SOLD_ITEM_LIST`,
`_REGISTERED_ITEM_LIST`, `_CLOSE`, `_HIGHEST_ITEM_LEVEL` — so their dumpers have no guard to
give. Three of those five still guard on 4 in the handler and two (`_CLOSE`,
`_REGISTERED_ITEM_LIST`) do not guard at all; either way the answer is the same, because a
4-byte frame is just the header. **The rule the codec follows: take the dumper's guard when the
dumper has one, otherwise 4.** Where both exist they agree on all eleven.

### 2.2 Client → WorldServer — the 5 the Arbiter must NOT answer

`C_TRADE_BROKER_REGISTER_ITEM` 0x7E74 (frame 0x2C) · `C_TRADE_BROKER_UNREGISTER_ITEM` 0xB8BA
(0x10) · `C_TRADE_BROKER_CALC_SOLD_ITEM` 0x7516 (8) · `C_TRADE_BROKER_CALC_BOUGHT_ITEM` 0x5C10
(8) · `C_TRADE_BROKER_BUY_IT_NOW` 0xF66D (0x11).

They have **dumpers but no `Handler_C_*`** in ArbiterServer.exe, which is the same evidence
`GUILD-DESIGN.md` §5.2 uses. They must stay unregistered and be tunnelled.

### 2.3 Arbiter → client — the 15

| opcode | packet | frame | fields |
|---|---|---|---|
| 0x53C0 | `S_TRADE_BROKER_BOUGHT_ITEM_LIST` | 8 | one array, `ItemList` |
| 0xEC54 | `S_TRADE_BROKER_BUY_IT_NOW` | 5 | `u8 Result` |
| 0xDEC6 | `S_TRADE_BROKER_CALC_BOUGHT_ITEM` | 5 | `u8 Result` |
| 0x6AF1 | `S_TRADE_BROKER_CALC_NOTIFY` | 0x0C | `i32 SoldCount, i32 BoughtCount` |
| 0x97ED | `S_TRADE_BROKER_CALC_SOLD_ITEM` | 5 | `u8 Result` |
| 0xB9E0 | `S_TRADE_BROKER_DEAL_INFO_UPDATE` | 0x18 | `i64 Price, i32 SellerDealStatus, i32 BuyerDealStatus` |
| 0xED80 | `S_TRADE_BROKER_DEAL_SUGGESTED` | 0x2A | ref `UserName`, then 5 i32 and 2 i64 |
| 0x7E53 | `S_TRADE_BROKER_HIGHEST_ITEM_LEVEL` | 8 | `i32 HighestLevel` |
| 0x9372 | `S_TRADE_BROKER_HISTORY_ITEM_LIST` | 0x10 | `i32 CurrentPage, i32 TotalPage` + array |
| 0x4FE6 | `S_TRADE_BROKER_INPUT_PRICE` | 0x2C | 5 × i64 (see 4.2) |
| 0xCDEA | `S_TRADE_BROKER_REGISTERED_ITEM_LIST` | 8 | one array |
| 0xD0E6 | `S_TRADE_BROKER_REQUEST_DEAL_RESULT` | 5 | `u8 Success` |
| 0x5587 | `S_TRADE_BROKER_SOLD_ITEM_LIST` | 0x18 | `i64 TotalCalcMoney, i64 TotalCalcTCatMoney` + array |
| 0x7066 | `S_TRADE_BROKER_SUGGEST_DEAL` | 5 | `u8 Result` |
| 0xFD2C | `S_TRADE_BROKER_WAITING_ITEM_LIST` | 0x10 | `i32 CurrentPage, i32 TotalPage` + array |

### 2.4 The inter-server six

| op | name | frame | payload |
|---|---|---|---|
| 0x1457 | `SA_TRADE_BROKER_OPEN` | 0x0E | `i64 ArbiterUser@06` |
| 0x1458 | `AS_TRADE_BROKER_CLOSE` | 0x0A | `i32 UserDbId@06` |
| 0x1459 | `SA_TRADE_BROKER_DEAL_OPEN` | 0x13 | `i64 ArbiterUser@06, i32 TradeId@0E, u8 Result@12` |
| 0x145A | `AS_TRADE_BROKER_DEAL_OPEN` | 0x12 | `i32 BuyerDbId@06, i32 TradeId@0A, i32 OpenType@0E` |
| 0x145B | `AS_TRADE_BROKER_DEAL_CLOSE` | 0x0A | `i32 UserDbId@06` |
| 0x286D | `AS_TRADE_BROKER_ITEM_SOLD` | 0x0B | `i32 UserDbId@06, u8 InstantBuy@0A` |

`SA_TRADE_BROKER_OPEN` is World saying "this player walked up to a broker NPC"; the Arbiter
answers by pushing the player's lists. The `DEAL_OPEN` pair is the bargaining window.

---

## 3. The DB-proxy half — the seven that wedge a character

All frame-relative. Every one of the five that carries a `DlmId` is a DLMItem.

| op | request | frame | fields |
|---|---|---|---|
| 0x2817 | `SDB_TRADE_BROKER_REGISTER_ITEM` | 0x16 | `i32 DlmId@0E, i32 OwnerDbId@12, ref ItemBinary` |
| 0x2819 | `SDB_TRADE_BROKER_UNREGISTER_ITEM` | 0x1E | `i32 DlmId@0E, i32 OwnerDbId@12, i32 Step@16, i32 TradeId@1A, ref ItemBinary` |
| 0x281B | `SDB_TRADE_BROKER_CALC_SOLD_ITEM` | 0x22 | `i32 DlmId@16, i32 OwnerDbId@1A, i32 Step@1E, ref CalcList, ref ItemBinary` |
| 0x281D | `SDB_TRADE_BROKER_CALC_BOUGHT_ITEM` | 0x22 | same shape as CALC_SOLD |
| 0x281F | `SDB_TRADE_BROKER_BUY_IT_NOW` | 0x27 | `i32 DlmId@0E, i32 OwnerDbId@12, i32 Step@16, i32 TradeId@1A, u8 InstantBuy@1E, ref ItemBinary, i64 TotalPriceWithTax@1F` |
| 0x2821 | `SDB_TRADE_BROKER_START_DEAL` | 0x12 | `i32 BuyerDbId@06, i32 SellerDbId@0A, i32 TradeId@0E` — **no DlmId** |
| 0x2824 | `SDB_TRADE_BROKER_CANCEL_DEAL` | 0x0E | `i32 UserDbId@06, i32 TradeId@0A` — **no DlmId** |

| op | reply | frame | fields |
|---|---|---|---|
| 0x2818 | `DBS_TRADE_BROKER_REGISTER_ITEM` | 0x13 | `i32 DlmId@0E, u8 Success@12, ref ItemBinary` |
| 0x281A | `DBS_TRADE_BROKER_UNREGISTER_ITEM` | 0x1F | `i32 DlmId@16, i32 Step@1A, u8 Success@1E, ref TradeData, ref ItemBinary` |
| 0x281C | `DBS_TRADE_BROKER_CALC_SOLD_ITEM` | 0x1F | `i32 DlmId@16, i32 Step@1A, u8 Success@1E, ref CalcItemList, ref ItemBinary` |
| 0x281E | `DBS_TRADE_BROKER_CALC_BOUGHT_ITEM` | 0x1F | same |
| 0x2820 | `DBS_TRADE_BROKER_BUY_IT_NOW` | 0x1F | `i32 DlmId@16, i32 Step@1A, u8 Success@1E, ref TradeData, ref ItemBinary` |
| 0x2822 | `DBS_TRADE_BROKER_START_DEAL` | **0x4C** | the whole deal: ids, item, both names, prices — see below |
| 0x2823 | `DBS_TRADE_BROKER_ACCEPT_DEAL` | 0x1E | `i32 UserDbId@06, i32 BuyerDbId@0A, i32 SellerDbId@0E, i32 TradeId@12, i64 AgreedPrice@16` |
| 0x282C | `DBS_TRADE_BROKER_CANCEL_DEAL` | 0x0A | `i32 UserDbId@06` |

`DBS_TRADE_BROKER_ACCEPT_DEAL` (0x2823) is the odd one: it has **no `SDB_` partner** — it is an
Arbiter→World push, not a reply, sent when both sides of a bargain confirm.

`DBS_TRADE_BROKER_START_DEAL` (0x2822), frame-relative: two ref slots first, then
`i32 UserDbId@0E, BuyerDbId@12, SellerDbId@16, TradeId@1A, ItemTemplateId@1E, ItemAmount@22,
ItemEnchantCount@26`, `u8 Masterpiece@2E`, `i64 RegTotalPrice@2F`, `i64 SuggestPrice@37`,
`u8 Awakened@47`, `i32 UnbindCount@48`, then the two wide strings `BuyerName` and `SellerName`.

**`Step`** appears in five of them and is the multi-stage commit the item transfer runs through —
the same idea as the warehouse's atom steps (`MAIL-WAREHOUSE.md` §5). Its values were not traced;
a handler must echo the `Step` it was given rather than invent one.

**`ItemBinary`** is a ref to the same `ItemData`-shaped record `DBS_USER_LOAD_INVENTORY` carries
(`INVENTORY-DESIGN.md` §3). That is what makes the broker an inventory operation: the item
physically leaves the seller's bag and comes back to the buyer's, and the atoms are what World
applies.

---

## 4. The two search packets

### 4.1 `C_TRADE_BROKER_HISTORY_ITEM_LIST_NEW` (0x76B4), fixed part **0x6C**

Four ref slots, then 25 scalars. Packet-relative:

```
[04] ref Keyword      [06] ref Category   [08] ref ItemTemplateIdList   [0A] ref SecondaryKeyword
[0C] i32 MinLevel     [10] i32 MaxLevel   [14] i32 Grade    [18] i32 UnidentifiedItem
[1C] u8  Masterpiece  [1D] u8 Enchantable
[1E] i32 MinItemLevel [22] i32 MaxItemLevel
[26] i32 OptionPassivityType    [2A] i64 OptionValue   [32] i32 OptionSearchCompareType
[36] i32 MinExtractLevel [3A] i32 MaxExtractLevel
[3E] i32 MinEnchantLevel [42] i32 MaxEnchantLevel
[46] i64 MinPrice     [4E] i64 MaxPrice
[56] u8 ExactMatch    [57] u8 EquipmentSet
[58] i64 MinTCatPrice [60] i64 MaxTCatPrice
[68] u8 UseDetailSearch [69] u8 Awakened  [6A] u8 Unbindable  [6B] u8 Wearable
```

`0x6B + 1 = 0x6C` — the guard lands exactly on the end of `Wearable`, which is the check that
says the field list is complete.

`WAITING_ITEM_LIST_NEW` (0x8DC7, fixed part **0x75**) is the same filter with
`CanBargainItemsOnly` added and a different scalar order; `BrokerPackets` carries both.

### 4.2 Paging and sorting

`*_PAGE` is `[i32 PageNo]`; `*_SORT` is `[i32 Criteria][u8 IsAscend]`. Both exist twice, once for
the waiting list (things for sale) and once for the history (things sold), and the Arbiter keeps
the result set **cached per user** — `TradeBrokerSearchAgent::CacheWaitingSearch` /
`CacheHistorySearch`, cleared by `ClearUserCache`. So a `_PAGE` with no preceding `_NEW` has
nothing to page, which is the one piece of cross-packet state the broker has.

`S_TRADE_BROKER_INPUT_PRICE` (0x4FE6) answers `C_TRADE_BROKER_INPUT_PRICE` with the five numbers
the "suggest a price" box shows: `MinPrice, AvgPrice, MinTCatPrice, AvgTCatPrice, RegisterFeeRate`,
all i64, computed by `TradeBrokerSearchAgent::CalcMinAvgPrice`.

---

## 5. `.def` files that are wrong

Four of the 57, checked field-by-field against the dumpers. The first is the one that matters,
because it is a live DB-proxy request.

| file | problem |
|---|---|
| `SDB_TRADE_BROKER_BUY_IT_NOW.1.def` | **missing `i64 TotalPriceWithTax` @0x1F** — 8 bytes short, and it is the price the buyer actually paid |
| `S_TRADE_BROKER_DEAL_SUGGESTED.1.def` | missing `ItemEnchantCount`, and its field order is wrong: the binary writes the name's ref slot FIRST |
| `S_TRADE_BROKER_DEAL_INFO_UPDATE.1.def` | an extra `int32 unk` between `price` and `sellerStage` — 4 bytes too long |
| `S_TRADE_BROKER_SOLD_ITEM_LIST.1.def` | missing `i64 TotalCalcTCatMoney`; the guard is 0x18 = header + two i64 |

`BrokerPackets.CorrectedDefs` carries all four, and `BrokerPackets.ResolveDef(defs, name)` has the
same contract as `GuildHandlers.ResolveDef` / `ChatManager.ResolveDef`, so a future wiring can
chain them with `??`.

Two more look wrong at a glance and are **not**: `C_TRADE_BROKER_CALC_BOUGHT_ITEM.1.def` and
`S_TRADE_BROKER_BOUGHT_ITEM_LIST.1.def` are array packets whose dumper emits the array through a
different helper, so a naive field count disagrees. Their layouts are fine.

---

## 6. What the manager does — for whoever builds it

`TradeBroker::*` (Arb_part_057.c) is ~40 methods. The shape worth copying:

* **Guards are their own methods** — `CanRegisterItem`, `CanUnregisterItem`, `CanBuyItNow`,
  `CanStartDeal`, `CanSetSoldItemCalculated`, `CanSetBoughtItemCalculated`,
  `CanUpdateItemSetSold`. Each answers before any row is touched.
* **The search agent is separate** from the item store (`TradeBrokerSearchAgent`), holds the
  per-user result cache, and is what `_NEW` / `_PAGE` / `_SORT` talk to.
* **Expiry is a timer**, not a query: `TradeBrokerTimer::OnExecute` →
  `TradeBroker::OnExpireWaitingItemsTick`, plus `ProcessExpiredTradeBrokerItemJob` and
  `CreateNewTradeBrokerItemsJob` as async jobs.
* **Bargaining is a lock**: `LockSuggest` / `AddSuggest` / `DeleteDeal`, with the deal window
  opened by `AS_TRADE_BROKER_DEAL_OPEN` and closed by `AS_TRADE_BROKER_DEAL_CLOSE`.

### 6.1 The stored procedures

```
dbo.spAddTradeBrokerItem            dbo.spLoadTradeBrokerItems
dbo.spDeleteTradeBrokerItem         dbo.spUpdateTradeBrokerItem
dbo.spAddTradeBrokerHistoryItem     dbo.spLoadTradeBrokerHistoryItems
dbo.spLoadTradeBrokerRandomPassive  dbo.spLoadTradeBrokerHistoryRandomPassive
dbo.spUpdateUserAchievementEtcOfTradeBrokerSellByDealCount
dbo.spUpdateUserAchievementEtcOfTradeBrokerSellInstantCount
```

Two tables, then: a live listing table and a history table, plus a "random passive" load which is
the rolled item option the listing carries. A future schema is two tables shaped like
`GUILD-DESIGN.md` §3.3's, keyed by `TradeId`.

---

## 7. What T53 did NOT do

* **No manager, no tables, no handlers.** By design — the task was research and a codec.
* **No capture.** No broker frame exists in any log on disk; every layout here is golden against
  the decompiled dumpers and handler guards, the same footing the party and guild codecs stand on.
  `MULTIPLAYER-DESIGN.md` §8's checklist is what would change that: open a broker NPC on the tap
  with the real Arbiter running and keep the log.
* **`Step` values are unknown** (§3). A handler must echo, never invent.
* **The `ItemBinary` / `CalcList` / `TradeData` ref payloads are named but not decoded.** They are
  `ItemData`-shaped (`INVENTORY-DESIGN.md` §3) but the exact record for a broker listing was not
  traced.

## 8. T55 — the five DLM answers, done

This section used to say "answer the five, even with `Success = 0`, before anything else."
T55 did it. `DbProxyHandlers.OnTradeBrokerRequest` answers all five; the rows are in
`status/PERSISTENCE-MAP.md` under "Trade broker — T55", so the coverage guard holds them.

**Walking up to a broker NPC no longer wedges the character.** The window opens, draws an empty
grid, and every request gets a refusal carrying its own DlmId.

### 8.1 Two things the writers settled that §3 and §7 had wrong

Reading the reply writers rather than only the dumpers corrected two guesses:

1. **The ref offset slots are backpatched unconditionally**, before the emptiness check. An
   empty answer therefore carries `0x13` (or `0x1F`) in the offset slot and `0` in the length —
   not two zeros, which is what T53's stub builders wrote. Same convention as
   `DBS_INIT_GUILD_DATA` and the guild init arrays (`GUILD-DESIGN.md` §11.4).
2. **`ItemBinary` is an `ItemTransactionAtom` array**, stride `0x358` — the same 856-byte record
   the warehouse and item paths already use (`INVENTORY-DESIGN.md`, `MAIL-WAREHOUSE.md`), not
   the `ItemData` listing §7 guessed at. The proof is in all five writers: the copy loop
   advances by `0x358` and the byte-length slot is filled with `count * 0x358`. So when the
   listings table does arrive, the item half is already decoded.

The 0x1F family also has **two** ref pairs, not one: `refA` (the dumper's first ref —
`TradeData`, or `CalcItemList` on the two calc replies) is a raw blob written with
`FUN_1403c98b0`, and `refB` is `ItemBinary`, the atom array. That is why the fixed part ends at
`0x1F` and not `0x17`.

### 8.2 What the client half answers

`Handlers/BrokerHandlers.cs` registers **fifteen** of the sixteen Arbiter-answered `C_` packets
and answers eleven of them with the empty form — the three list packets, the two search
families, `C_TRADE_BROKER_INPUT_PRICE`, and `C_TRADE_BROKER_SUGGEST_DEAL`.

The sixteenth is `C_TRADE_BROKER_HIGHEST_ITEM_LEVEL` (0xEAFB): **T45 already answers and
registers it**, and `PacketDispatcher.Register` throws on a duplicate, so registering it again
would take the server down at startup rather than fix anything.

Four send nothing to the client:

* `C_TRADE_BROKER_CLOSE` has no client reply at all — what it sends is `AS_TRADE_BROKER_CLOSE`
  (0x1458) to World, which opened the window with `SA_TRADE_BROKER_OPEN` and would otherwise
  believe the player is still standing at the NPC.
* `C_TRADE_BROKER_DEAL_CONFIRM`, `_DEAL_PRICE_UPDATE` and `_REJECT_SUGGEST` each reach a
  `TradeBroker::` method that looks the deal up first and returns before any writer — and with
  no listings there is never a deal. `C_TRADE_BROKER_SUGGEST_DEAL` is the one deal packet that
  IS answered, because its failure packet is pinned: `TradeBrokerOpenDealFetchWork::operator()`
  (`Arb_part_084.c:6070`) writes `S_TRADE_BROKER_REQUEST_DEAL_RESULT` with a single bool, and a
  suggestion against a listing that does not exist is exactly the false case.

The paged lists answer **page 1 of 1**, not page 0 of 0. Page 0 of an empty history is the bug
that crashed the real Arbiter's `C_VIEW_GUILD_WAR`, and `CLAUDE.md`'s hard rules name this
shape specifically.

### 8.3 Still open

* **No listings table.** Everything above is a refusal. A real broker needs the two tables of
  §6.1 and a manager; both wait for a capture, because no broker frame exists in any log on
  disk.
* **`Step` values are still unknown.** The handlers echo what they were given, which is safe but
  is not the same as knowing what stage 2 means.
* **`SDB_TRADE_BROKER_START_DEAL` (0x2821) and `_CANCEL_DEAL` (0x2824) are still unanswered.**
  Neither carries a DlmId, so neither can head-block anyone; they are left alone until there is
  a deal to have.
* **The `refA` payloads** (`TradeData`, `CalcItemList`) are named and sized but not decoded.

## T70 — the TradeData record, and the two-step protocol

cap_social3.log is the first capture with a real broker session in it: browse, search, three
listings (one refused as unlistable), reprice, cancel, buy, collect. Every frame layout T53 and
T55 derived from the dumpers survives it unchanged — the guards, the offsets, the 856-byte
ItemBinary atom, the backpatch-the-offset-slot-unconditionally convention. What the capture adds
is the thing T55 could not get: **what the other ref actually contains**.

### The TradeData / CalcItemList record — 0x188 bytes

`DBS_TRADE_BROKER_UNREGISTER_ITEM` calls it TradeData; the two CALC replies call it CalcItemList;
it is one record. Three of them appear (listing 1, listing 3, and the cleared form Step 2 of an
unregister returns), and cross-checking the three is what separates a field from heap.

| Offset | Type | Field | listing 1 / 3 / cleared |
| --- | --- | --- | --- |
| +0x000 | i32 | TradeId | 1 / 3 / 0 |
| +0x004 | i32 | SellerDbId | 2 / 2 / 0 |
| +0x008 | wstr | SellerName, 0x25 wchars | "Test" / "Test" / "" |
| +0x052 | u16 | *uninitialised stack* | 25119 / 25103 / **25119** |
| +0x05C | u16 | *uninitialised stack* | 530 / 529 / **530** |
| +0x060 | i64 | ItemDbId | 10027 / 10029 / 0 |
| +0x068 | i32 | TemplateId | 200997 / 139093 / 0 |
| +0x06C | i32 | Amount | 1 / 1 / 0 |
| +0x0B8 | 8×u16 | RegisterTime {y,m,d,h,mi,s,0,0} | 2026-09-16 22:32:16 / 22:32:47 / zero |
| +0x0C8 | i64 | Price | 10001 / 1 / 0 |

The two u16s are stack, not fields: they hold the same values across different listings **and stay
set in the cleared record where every real field is zero**. `BuildTradeData` writes zeros there,
the same call T51 made for the guild blob.

Price is pinned from the other end of the trade: `SDB_TRADE_BROKER_BUY_IT_NOW`'s
`TotalPriceWithTax` for listing 3 is 1, and listing 3's +0xC8 is 1.

### The two-step protocol

Every broker operation except REGISTER runs **twice**, and `Step` says which pass it is
(seq 1880..1883 buy, 1946..1949 calc-sold, 2000..2003 unregister):

| | request | reply |
| --- | --- | --- |
| Step 1 | no atoms — the ItemBinary ref is `offset = frame length, count 0` | the TradeData record, no atoms |
| Step 2 | the atoms World built from what Step 1 returned | the TradeData record **and** those atoms echoed |

So Step 1 is "read me the listing" and Step 2 is "commit". **This is why T55's empty forms could
never complete a purchase**: an empty Step-1 answer gives World nothing to build the Step-2 atoms
from, so the second half never comes.

REGISTER is the exception — one pass, no Step field (its reply is the odd 13-byte header), because
World already has the item.

### What is still missing

The listings table. `BuildTradeData` can now produce a real record, and the two-step shape is
known, but there is nowhere to keep a listing between Step 1 and Step 2, let alone between
sessions. The remaining work, in order:

1. `broker_listings` (trade_id, seller_db_id, seller_name, item_db_id, template_id, amount, price,
   registered_at, state) plus the sold/bought queues the two CALC pairs drain.
2. The five handlers applying to it: REGISTER inserts from the request's atoms; UNREGISTER Step 2
   deletes and returns the item; BUY_IT_NOW Step 2 moves item and money; the two CALC pairs pay
   out proceeds and hand over bought items.
3. The client half: nine distinct `C_TRADE_BROKER_*` requests and six `S_` replies are in
   cap_social3_client.log — `S_TRADE_BROKER_WAITING_ITEM_LIST` ×9 is the search result page,
   `S_TRADE_BROKER_CALC_NOTIFY` ×4 the collect notifications.

None of that is guesswork any more; it is bookkeeping against a decoded record.

## T71 — the listings table, and the five handlers on top of it

Steps 1 and 2 of the previous section are done. Step 3, the client half, is not.

### The table

`broker_listings`: `trade_id` (the TradeId every frame keys on), seller db id and name, item db id,
template, amount, price, buyer db id, `state`, `registered_at`, `sold_at`. States are
`Listed 0 → Sold 1 → SellerPaid 2 / BuyerCollected 3`, plus `Cancelled 4`.

**A listed item is still a row in `items`.** The op-44 atom in every register batch moves it to
inventory type 6 — the broker's holding pocket — and a cancel or a collect moves it back out.
Nothing is deleted, so `items` stays the single answer to "where is this thing".

### Where the price comes from

`SDB_TRADE_BROKER_REGISTER_ITEM` has no price field: its dumper lists DlmId, OwnerDbId and
ItemBinary and nothing else. The price arrives **inside the op-53 atom, at +0x288 (i64)**. All
three registers in cap_social3.log confirm it — the two that carry 10001 there come back with
Price 10001 in their TradeData, and the one that carries 1 comes back 1.

### The register batch, decoded

| op | what it does |
| --- | --- |
| 9 | the listing fee off the seller's gold (−500 in all three captures) |
| **53** | the marker, and the only carrier of the price |
| **44** | the whole row moves to `(seller, inven 6, 0)` |
| 2, or 6+11 | the stack leaves the bag: change-amount, or detach+delete |

Ops 53–57 are markers: 53 register, 54 cancel, 55 buy, 56 collect-proceeds, 57 collect-item. They
change no row themselves and are echoed. They are **not** unmodelled-op gaps — the broker handler
is what acts on them — so `Apply` logs them at Debug rather than as a gap.

### The three shapes of Step-2 reply

This is the detail a hand-written implementation would get wrong, and all three are pinned:

| opcode | Step 1 refA | Step 2 refA |
| --- | --- | --- |
| UNREGISTER 0x281A | the listing | the record, **entirely zeroed** (seq 2003) |
| CALC_SOLD 0x281C | the CalcItemList form | **absent** — offset 0x1F, length 0 (seq 1949) |
| CALC_BOUGHT 0x281E | the CalcItemList form | **absent** (seq 1907) |
| BUY_IT_NOW 0x2820 | the listing | the listing, **unchanged** (seq 1883) |

### CalcItemList — TradeData plus a settlement block

The two CALC replies carry the same 0x188 struct with six more fields set. Diffing seq 1947 and
1905 against seq 1881 (the same listing 3, minutes apart) isolates them:

| Offset | Field | seq 1947 / 1905 |
| --- | --- | --- |
| +0x058 | CalcState | **3** (seller collecting) / **2** (buyer collecting) |
| +0x0D0 | BuyerDbId | 1003 |
| +0x0D4 | InstantBuy | 1 |
| +0x0D8 | SoldTime, 8×u16 | 2026-09-16 22:33:02 |
| +0x0E8 | SoldPrice | 1 |
| +0x100 | CalcMoney — what the collector receives | 1 |

The seller's and the buyer's records differ in **one field**, +0x58. `CalcMoney` equals the price
in this capture, so it shows no broker tax; if a later capture shows one, it goes here.

### Verified

All twelve captured broker frames are reproduced byte for byte by the builders — three registers,
and step 1 and step 2 of each of unregister, calc-sold, calc-bought and buy — with two documented
exceptions: the two uninitialised u16s at +0x52/+0x5C, and the item db id the Arbiter allocated in
seq 1907 (ours allocates its own, which is the point).

### What is still missing

The client half. Nine `C_TRADE_BROKER_*` requests and six `S_` replies are in
cap_social3_client.log and still answered with the empty forms: `S_TRADE_BROKER_WAITING_ITEM_LIST`
(×9, the search result page), `S_TRADE_BROKER_REGISTERED_ITEM_LIST`, `_SOLD_ITEM_LIST`,
`_BOUGHT_ITEM_LIST`, `_CALC_NOTIFY` (×4) and `_HIGHEST_ITEM_LEVEL` (×2). The store side is ready —
`SearchBrokerListings`, `CountBrokerListings`, `GetBrokerListingsOf` and `GetBrokerPurchasesOf`
exist and are tested — so what remains is decoding the per-row body each `S_` list carries.

## T72 — the client windows, and the red test

### The red test first

`T71_registering_creates_a_listing_and_pockets_the_item` asserted the seller lost 500 gold to the
listing fee. The atom **did** reach `AddCharacterMoney` — the test's character simply had no gold,
and `AddCharacterMoney` clamps (`MAX(0, money + $d)`), so −500 landed as a no-op. The test was
wrong, not the code: the real seller in seq 1515 had gold, and so does the test's now.

### The two list bodies

Both use TERA's ordinary array encoding with **packet-relative** offsets:

```
body     [u16 count][u16 firstElementOffset]  then the packet's own scalars
element  [u16 here][u16 next (0 = last)][u16 nameOffset]  fields...  name at nameOffset
```

An element is `fixedSize + (name.Length + 1) * 2` bytes.

`S_TRADE_BROKER_WAITING_ITEM_LIST` (0xFD2C) — fixed part **92**, body scalars `[u32 page][u32 totalPage]`:

| Offset | Field |
| --- | --- |
| +6 | TradeId |
| +10 | ItemDbId (i64) |
| +18 | TemplateId |
| +22 | Amount |
| +35 | Price (i64) |
| +43 | TotalPriceWithTax (i64) |
| +55 | SellerDbId |

`S_TRADE_BROKER_BOUGHT_ITEM_LIST` (0x53C0) — fixed part **98**, and **no page scalars at all**:

| Offset | Field |
| --- | --- |
| +6 | TradeId |
| +14 | ItemDbId (i64) |
| +22 | TemplateId |
| +26 | Amount |
| +39 | RegisterTime, UNIX seconds (i64) |
| +47 | Price (i64) |
| +55 | SellerDbId |
| +59 | u8, 1 |
| +60 | SoldTime, UNIX seconds (i64) |
| +68 | TotalPaid (i64) |

The two element shapes differ — four extra bytes before ItemDbId in the bought list — so they are
not interchangeable despite the overlapping names. The waiting-list offsets were cross-checked
against all three listings in seq 1419 (three templates, two prices); every one lands at the same
element-relative offset in all three.

**The broker's cut is a tenth, truncated**: 10001 comes back 11001 and 1 comes back 1, and that is
the same number `SDB_TRADE_BROKER_BUY_IT_NOW` carries as `TotalPriceWithTax`.

**Tie-break**: at equal price the capture lists the newest first (trade 2 before trade 1), so
`SearchBrokerListings` orders `price ASC, trade_id DESC`.

### The small ones

| Packet | Body | Capture |
| --- | --- | --- |
| `S_TRADE_BROKER_HIGHEST_ITEM_LEVEL` | one **float**, 469.0 | seq 134, 3217 |
| `S_TRADE_BROKER_BUY_IT_NOW` | one byte, 1 | seq 1469 |
| `S_TRADE_BROKER_CALC_NOTIFY` | `[u32 soldWaiting][u32 boughtWaiting]` | seq 272 / 1462 / 1492 |

T45 answered HIGHEST_ITEM_LEVEL with **0** because there was no broker; the real Arbiter answers
469.0 both times it is asked, for both characters, so it is static config — the cap the search
window's item-level slider runs to. A 0 collapses that slider to nothing.

### What is still on the empty form, and why

* `S_TRADE_BROKER_REGISTERED_ITEM_LIST` — the capture contains **one**, and it is empty (seq 1276).
* `S_TRADE_BROKER_SOLD_ITEM_LIST` — the client asked twice and the real Arbiter answered
  **neither**; the packet never appears.

Filling either by analogy with the two lists we did see is the guess that desyncs a window. They
wait for a capture that holds a populated one.

`C_TRADE_BROKER_CALC_SOLD_ITEM`, `_CALC_BOUGHT_ITEM` and `C_TRADE_BROKER_BUY_IT_NOW` are **World's**
client packets, not ours — they are not in `BrokerHandlers.ClientOpcodes`, and it is World relaying
them to us as the DB-proxy 0x281B / 0x281D / 0x281F that does the work. `CalcNotifyBodyFor` exists
for a caller that wants the badge counts, but nothing answers those three here: doing so would
double up on what World already told the client.
