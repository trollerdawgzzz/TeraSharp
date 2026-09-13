# Inventory persistence — design (T11, research only)

**Status: design note, no code.** This is the plan for replacing the starter/captured inventory
(`DbProxyHandlers.OnLoadInventory`, `data/starter_inventory.bin`) with real per-character storage.

Sources, in the order they win when they disagree:

- `D:\v100\TERA_SERVER.100\ArbiterServer.exe.c` — `Handler_SDB_ITEM_SINGLE` (`FUN_14074aca0`,
  scope tracer line 1271688), the `0x2769` writer (`FUN_1406ec9e0`, 1205877) and the `0x27A4`
  writer (`FUN_1406f6f40`, 1213482).
- `D:\v100\TERA_SERVER.100\world_decompiled\WorldServer.exe.c` — the `Inventory::Prepare*`
  functions that build the transaction atoms, which is where the operation enum comes from.
- `D:\packetlogs\cap_newchar.log` — real ArbiterServer, new character "Test" playerId 2, Island of
  Dawn: create, enter, gather, combine, drink two potions, kill, zone change, logout. Reframed by
  u32 length. Every byte quoted below was checked against it with Python (no build here).

All offsets are **payload**-relative (frame offset − 6) unless a line says "frame".

---

## 1. The three opcodes

### 1a. Login: `SDB_USER_LOAD_INVENTORY` 0x27A2 → `0x27A3` + `0x27A4`

```
0x27A2  req  14 B frame /  8 B payload   [u32 reqId][u32 playerId]
0x27A3  rsp  19 B frame / 13 B payload   [u32 listOff=19][u32 listLen][u32 reqId][u8 ok]
0x27A4  rsp        frame / 13 + n*536    [u32 listOff=19][u32 listLen=n*536][u32 reqId][u8 flag=0]
                                          then n item records of 536 (0x218) bytes
```

`0x27A3` is `DBS_USER_LOAD_POCKET_DATA` — in the capture it is an **empty list** (`listLen = 0`,
`ok = 1`), i.e. exactly `BuildEmptyListType1(payload, 0)`. Pockets (extra bag tabs) are a separate
table we do not model yet.

Capture, seq 135 → 136 + 137:

```
135 W->A 0x27A2   02 00 00 00  02 00 00 00                        reqId 2, playerId 2
136 A->W 0x27A3   13 00 00 00  00 00 00 00  02 00 00 00  01
137 A->W 0x27A4   13 00 00 00  90 0C 00 00  02 00 00 00  00  ...  3216 = 6 x 536
```

`data/starter_inventory.bin` is seq 137's payload verbatim (3229 B), verified byte-for-byte.

Both replies carry the live DLM id, so both must be real handlers — they are (T10 covers them).

### 1b. Play: `SDB_ITEM_SINGLE` 0x2768 → `0x2769`

`Handler_SDB_ITEM_SINGLE` requires frame ≥ 0x1E and reads, frame-relative:

```
[6]  u32 offsetA   [10] u32 lengthA      list A, bytes, offset is frame-relative
[14] u32 offsetB   [18] u32 lengthB      list B, same shape
[22] u32 reqId     [26] u32 playerId
```

so a 24-byte payload header, then `lengthX / 856` records of **856 (0x358) bytes** each. The
record count is computed as `(length - 1) / 0x358 + 1` and the walk steps `piVar9 + 0xD6` ints.

The reply writer `FUN_1406ec9e0` emits four backpatch slots, then the id and the ok byte, then both
lists — a **21-byte** payload header:

```
[0] u32 offsetA=27  [4] u32 lengthA  [8] u32 offsetB  [12] u32 lengthB  [16] u32 reqId  [20] u8 ok
```

Reply frame = request frame − 3 (24-byte header → 21-byte header). Confirmed on all 8 exchanges in
the capture. `DbProxyHandlers.BuildDbs2769` already emits this header with both lists empty.

Traffic in `cap_newchar.log` (all playerId 2):

| seq | frame | list A | what the player did |
|---|---|---|---|
| 519 | 30 | empty | right after enter-world — a flush, nothing to commit |
| 2072 | 886 | 1 atom, op 7 | picked up a gathering node (item 81251 ×4) |
| 2211 | 4310 | 5 atoms, ops 6, 11, 6, 11, 7 | item combine: two stacks consumed, one produced |
| 2290 | 886 | 1 atom, op 2 | drank a potion (item 6560, delta −1) |
| 2306 | 886 | 1 atom, op 2 | drank a potion (item 6550, delta −1) |
| 2540 | 30 | empty | zone change |
| 3477 | 886 | 1 atom, op 9 | money +84 from a kill |
| 4179 | 30 | empty | logout |

**List B was empty in every frame of this capture.** Both lists are
`vector<ItemTransactionAtom const*>` — the handler runs `TransSQLExec::CanExecTrans` over A then B,
then `TransSQLExec::ExecuteTrans` over A then B — so B is a second, later-executed batch, not a
different record type. Nothing here tells us what World puts in it; do not assume it stays empty.

---

## 2. The 536-byte item record (`0x27A4`)

Column-diffed across the six starter items, then cross-checked against the ids the 0x2768 atoms
refer to.

| off | size | field | starter values | confidence |
|---|---|---|---|---|
| 0 | u32 | **item DB id** | 11, 12, 7, 8, 9, 10 | high — the same id space the atoms use, and the ids the Arbiter hands back on insert (§4) |
| 4 | u32 | 0 | | |
| 8 | u32 | **item template id** | 6550, 6560, 59053, 15004, 15005, 15006 | high — matches atom+24 |
| 12 | u32 | *uninitialised* | varies, UTF-16 text | high — see §3 |
| 16 | u32 | **owner playerId** | 2 | certain — patching it is what stops `SA_ENTER_WORLD_FAILED` |
| 20 | u32 | 0 | | |
| 24 | u32 | **amount** | 20, 20, 1, 1, 1, 1 | high |
| 28 | u32 | **pocket / tab** | 0, 0, 14, 14, 14, 14 | medium — 0 = main bag, 14 = equipped |
| 32 | u32 | 0 | | |
| 36 | u32 | **slot in pocket** | 0, 1, 1, 3, 4, 5 | medium — with 28 it makes a unique position; 1/3/4/5 in tab 14 read as weapon/body/gloves/boots |
| 40 | u32 | 0 | | |
| 44 | u32 | 30 | 30 on all six | unknown constant |
| 48 | u32 | 0 (byte 54 is garbage) | | |
| 56..275 | | zero | | |
| 276 | 16 B | DateTime "never" | `u16 1970, 1, 1, 0, 0, 0, 0, 0` | high |
| 292 | 16 B | DateTime "never" | same | high |
| 464 | 16 B | DateTime "never" | same | high |
| 484 | f32 | 1.0 | `00 00 80 3F` on all six | high |
| rest | | zero or *uninitialised* | | |

The two potions are template 6550/6560 ×20 in bag slots 0 and 1; the other four are equipped gear.
Item DB ids 7..12 were allocated at character creation, and the play session goes on to allocate
13, 15 and 16 — one monotonic sequence per… something (see §6, open question 1).

### The tail is uninitialised Arbiter heap

`FUN_1406f6f40` copies 0x218 bytes straight out of the Arbiter's item object, and that object is
not fully initialised. Decoding the varying bytes from offset ~314 as UTF-16 recovers fragments of
the Arbiter's own SQL:

```
item0  "@lastUpdatePlanetId int" … "declare"
item1  "olock) where ownerAccountDbId = @"
item2  "( @accessPlanetId <> -1 and  @ac"
item4  "begin  insert into SharedTCat (o"
item5  "elId,isSender,ownerDBID) VALUE"
```

Six items, six different fragments, in bytes that are otherwise structurally identical. This is
leaked heap, not data. **World therefore ignores those bytes** — it has to, since they are
different on every run. That is what makes §5 possible, and it is also why a byte-exact test
against the capture can only ever be a test of the *head*.

Which bytes: the tail garbage lives in the ranges that differ between items — 12..15, 54, 314,
324..327, 338..342, every even byte in 382..446, and 462, 482, 530, 532, 534. Everything else in
56..535 is zero or one of the constants in the table.

---

## 3. The 856-byte `ItemTransactionAtom` (`0x2768`)

Atom-relative offsets, from the nine atoms in the capture plus the World-side builders:

| off | field | observed |
|---|---|---|
| 0 | index within the list | 0, 1, 2, 3, 4 |
| 4 | **operation** (see §4) | 2, 6, 7, 9, 11 |
| 8 | u32 flag | 1 on ops 6 and 7, 0 on 2, 9, 11 |
| 16 | **item DB id** | 0 on insert (Arbiter fills it in), otherwise 11, 12, 13, 15 |
| 24 | **item template id** | 81251, 81253, 81255, 6550, 6560; 0 for money |
| 32 | playerId | 2 |
| 48 | **slot** | matches the record's slot for the item being touched |
| 56 | playerId | 2 |
| 72 | slot (destination) | same as 48 in every captured atom |
| 80 | **i64 signed delta** | −1 per potion, +84 for money, +1/+4/+2 on inserts |
| 0x28..0x30 | source pocket/slot triple | written by `PrepareChangeInvenPos` |
| 0x40..0x48 | destination pocket/slot triple | written by `PrepareChangeInvenPos` |

`TransSQLExec::OnTransError` logs the atom's `+0x20` and `+0x38`, which are the two playerId slots —
source owner and destination owner. A cross-character move is presumably where they differ.

The i64 at +0x50 is unambiguous: `PrepareChangeInvenPos` literally writes
`*(longlong *)(atom + 0x50) = (longlong)iVar4;` and `= (longlong)-iVar4;` for the two halves of a
stack merge.

Everything past ~offset 100 in the atom is the same kind of mostly-zero-plus-garbage as the item
record, including three of the same `{1970-01-01}` DateTime blocks and the same 1.0f.

---

## 4. Sub-operations

The operation is a plain **enum index at atom+4**, dispatched through four function-pointer tables
in the Arbiter — `DAT_140f02f30` / `DAT_140f03260` for `TransSQLExec::CanExecTrans`, and
`DAT_140f03590` / `DAT_140f038c0` for `TransSQLExec::ExecuteTrans`. Index < 100. Each entry is a
`DO_TS_*` function; the Arbiter contains 82 of them (`DO_TS_CHANGE_ITEM_AMOUNT`,
`DO_TS_CHANGE_ITEM_POS`, `DO_TS_DELETE_ITEM`, `DO_TS_INSERT_STACKABLE_ITEM`, … the full list is in
the decompile, `grep -o 'DO_TS_[A-Z_0-9]*'`).

The tables are data, so Ghidra's text does not give the index → name pairing. It can be recovered
from the **World** side instead, where every `Inventory::Prepare*` writes the index into a fresh
atom. Scanning `WorldServer.exe.c` for `*(undefined4 *)(X + 4) = N;` followed by the atom push
`FUN_140bf27a0` gives:

| op | World builder | reads as |
|---|---|---|
| 2 | `PrepareChangeInvenPos`, `PrepareSendStackableItem` | change amount by the i64 delta |
| 3 | `PrepareChangeInvenPos` | move to an empty slot |
| 4 | `PrepareChangeEnchant`, `PrepareEnchantIdentifyItem` | enchant |
| 5 | `PrepareChangeCustomizing` | customise |
| 6 | `PrepareChangeInvenPos`, `PrepareSendStackableItem` | detach a fully-consumed stack (always paired with 11) |
| 7 | *(insert path)* | **insert item** — the only op that arrives with DB id 0 |
| 9 | `Inventory::GetAddableMoney` | change money (template id 0) |
| 11 | `PrepareSendNonStackItem`, `PrepareChangeInvenPos` | delete the row |
| 13, 18 | warehouse | `DO_TS_WARE_*` |
| 20, 37 | parcel | send / receive |
| 36 | `PrepareChangeInvenPos` | swap two occupied slots |
| 44, 53–57 | trade broker | |
| 51, 63 | `PrepareChangeSealItem` | bind / unbind |
| 59, 60, 65, 70, 73–77, 80, 81, 90, 91, 93–96 | the `PrepareChange*` family | per-attribute item edits |
| 66, 85, 88 | reputation, guild coin, guild money | |
| 97 | `PrepareCombineItem` | combine |

Names line up one-to-one with the Arbiter's `DO_TS_*` set wherever both sides name the same thing
(`PrepareCombineItem` ↔ `DO_TS_COMBINE_ITEM`, `PrepareAwakenItem` ↔ `DO_TS_AWAKEN_ITEM`, and so on),
which is what makes the table above trustworthy — but the pairing is *inferred*, not read out of a
table, so treat any row we have not seen on the wire as a lead rather than a fact.

The four ops the game actually issued in an hour of level-1 play are **2, 6+11, 7, 9**. Those four
plus 3 and 36 (moving things in the bag) are the whole of a starter character's inventory.

### Reply echo rule

`0x2769` is the request's atoms **echoed verbatim**, with the 24-byte header swapped for the
21-byte one — except that on an **insert (op 7)** the Arbiter writes the item DB id it allocated
into `atom+16`:

```
seq 2072 -> 2073   atom0 op 7   [16] 0 -> 15     (only difference in 856 bytes)
seq 2211 -> 2213   atom4 op 7   [16] 0 -> 16     (only difference in 4280 bytes)
                   atoms 0..3 (ops 6, 11, 6, 11) byte-identical
seq 2290/2306/3477 (ops 2, 2, 9)                 byte-identical
```

So the rule is precisely: **copy the atoms through; for every atom whose op allocates a row, fill
in the new id at +16; set `ok`.** That is the contract we have to honour, and note that it means
TeraSharp's current empty-list `0x2769` is wrong the moment World inserts anything — World would
get its item back with DB id 0 and no atoms at all. It has not bitten us because nothing has
successfully picked an item up yet.

---

## 5. Proposed storage

One table, keyed by the DB id the Arbiter allocates, plus a per-owner id sequence.

```sql
CREATE TABLE IF NOT EXISTS items (
  id           INTEGER PRIMARY KEY,           -- the item DB id echoed at atom+16 / record+0
  owner_id     INTEGER NOT NULL REFERENCES characters(id),
  template_id  INTEGER NOT NULL,              -- record+8  / atom+24
  amount       INTEGER NOT NULL DEFAULT 1,    -- record+24
  pocket       INTEGER NOT NULL DEFAULT 0,    -- record+28 (0 = bag, 14 = equipped)
  slot         INTEGER NOT NULL DEFAULT 0,    -- record+36
  blob         BLOB    NOT NULL,              -- the 536-byte record, see below
  created_at   TEXT    NOT NULL DEFAULT (datetime('now'))
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_items_pos ON items(owner_id, pocket, slot);
CREATE INDEX        IF NOT EXISTS ix_items_owner ON items(owner_id);
```

`money` is not an item — op 9 carries template id 0. It belongs on the `characters` row next to
`level`/`exp` from T6, not here.

### Why a `blob` column as well as the fields

Because §2 is honest about what we know: of 536 bytes we can name about 40, roughly 400 are zero,
and the remainder is uninitialised Arbiter heap we cannot reconstruct and World provably ignores.
Synthesising a record from the six named fields alone means asserting that every unnamed byte is
zero — which is *probably* right and is exactly the assumption that desynced World the last time
someone made it (`status/HANDOFF.md`, the empty-`0x27A2` incident).

So: store the 536-byte record we were given, and patch the named fields into it on the way out. A
new item's blob starts as the matching record from `data/starter_inventory.bin` (or, better, as a
zeroed 536-byte buffer once a test proves World accepts one — see §6). The blob is an
implementation detail of the DB-proxy layer, not a schema commitment; the day every field is named,
drop the column.

### Rebuilding `0x27A4`

```
rows = items WHERE owner_id = playerId ORDER BY pocket, slot
payload  = [u32 19][u32 rows.Count * 536][u32 liveReqId][u8 0]
for each row:
    rec = row.blob (536 B)
    rec[0]  = row.id
    rec[8]  = row.template_id
    rec[16] = playerId
    rec[24] = row.amount
    rec[28] = row.pocket
    rec[36] = row.slot
    append rec
```

With zero rows this is the 13-byte empty list — which is what `0x27A3` already sends, so the shape
is known-good. `OnLoadInventory` keeps its current job of choosing between this and the replay
table.

### Applying `0x2768`

```
for each atom in list A, then list B:
    switch atom.op:
      7            -> INSERT; allocate id; write it into the REPLY atom at +16
      2            -> UPDATE items SET amount = amount + (i64 at atom+0x50)
      6            -> no row change on its own; always followed by 11
      11           -> DELETE
      3, 36        -> UPDATE pocket/slot (from atom+0x28.. and +0x40..)
      9            -> characters.money += delta   (not an item)
      anything else-> apply nothing, still echo the atom and reply ok=1
reply = the atoms, with insert ids filled in, under the 21-byte header
```

The last line matters more than the rest: **an unknown op must still get a complete, correct-shaped
reply.** A `DBS_*` that never arrives, or arrives without the live DLM id, head-blocks the user
forever (`status/HANDOFF.md` §1) — losing one enchant is recoverable, wedging the queue is not.

Implementation order, smallest useful step first:

1. Echo `0x2769` properly (atoms through, insert ids filled). No storage yet. This alone unblocks
   picking anything up, and is testable byte-exact against seq 2072/2211/2290/2306/3477.
2. `items` table + rebuild `0x27A4` from it, seeded at character creation with the six starter
   records. Replaces `BuildStarterInventory`.
3. Apply ops 7, 2, 11, 3, 36 to the table. Money (op 9) onto the characters row.
4. Everything else stays echo-only until a capture shows it mattering.

---

## 6. Open questions

1. **What is the item DB id scoped to?** The starter items are 7..12 for playerId 2 and the session
   allocates 13, 15, 16 — 14 went somewhere we did not see. It is at least account-wide, plausibly
   global. If it is global, `items.id` must not be `characters`-scoped. One capture of a second
   character being created answers this.
2. **`record+44 = 30`** on all six starter items, and 30 also shows up at atom+88 and atom+568 on
   the potion writes. Unidentified.
3. **What goes in list B of `0x2768`?** Empty in every captured frame. Same record type, executed
   after list A.
4. **Is a zeroed tail accepted?** Everything in §5 would get simpler if we could send a record whose
   unnamed bytes are all zero. The capture cannot answer it — the real Arbiter never sent one. The
   cheap experiment: serve one character a `0x27A4` built from zeroed 536-byte records with only
   the named fields set, and watch for `SA_ENTER_WORLD_FAILED` (0x138D). If it enters the world,
   drop the blob column.
5. **`0x27A3` pockets.** Empty for a starter character. A character who has bought bag slots will
   have a non-empty pocket list and we have never seen one.
6. **`0x2813 SDB_EQUIP_ITEM`** was never sent in this session — equipping on the Island of Dawn went
   through `0x2768` as a pocket/slot change. Whether the client's `C_EQUIP_ITEM` ever produces
   0x2813 is untested.
