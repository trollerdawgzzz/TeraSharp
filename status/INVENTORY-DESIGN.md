# Inventory persistence — design (T11 research; **implemented in T44**, see §7)

**Status: design note, no code.** This is the plan for replacing the starter/captured inventory
(`DbProxyHandlers.OnLoadInventory`, `data/starter_inventory.bin`) with real per-character storage.

Sources, in the order they win when they disagree:

- `D:\v100\TERA_SERVER.100\ArbiterServer.exe.c` — `Handler_SDB_ITEM_SINGLE` (`FUN_14074aca0`,
  scope tracer line 1271688), the `0x2769` writer (`FUN_1406ec9e0`, 1205877) and the `0x27A4`
  writer (`FUN_1406f6f40`, 1213482).
- `D:\v100\TERA_SERVER.100\world_decompiled\WorldServer.exe.c` — the `Inventory::Prepare*`
  functions that build the transaction atoms, which is where the operation enum comes from.
- `<captures>\cap_newchar.log` — real ArbiterServer, new character "Test" playerId 2, Island of
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

The i64 at +0x50 is unambiguous: `PrepareChangeInvenPos` writes the moved amount into `atom+0x50`
for one half of a stack merge and its negation for the other.

Everything past ~offset 100 in the atom is the same kind of mostly-zero-plus-garbage as the item
record, including three of the same `{1970-01-01}` DateTime blocks and the same 1.0f.

---

## 4. Sub-operations

The operation is a plain **enum index at atom+4**, dispatched through four function-pointer
tables in the Arbiter — two for `TransSQLExec::CanExecTrans`, at data symbols
`DAT_140f02f30`
and `DAT_140f03260`, and two for `TransSQLExec::ExecuteTrans`, at
`DAT_140f03590`
and `DAT_140f038c0`. Index < 100. Each entry is a
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
| 8 | `ItemInserter::AddNonStackNormalRecvToTransactionAtoms` | **insert a non-stackable item** (GM makeitem, reward gear) - also arrives with DB id 0; T153 |
| 9 | `Inventory::PrepareMoneyTransaction` | change money by a signed DELTA (template id 0) — section 8.1 |
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

### Op 8 - the non-stackable insert (T153)

World writes it in `Inventory::ItemInserter::AddNonStackNormalRecvToTransactionAtoms`
(WorldServer.exe.c:1432708); the Arbiter runs it as `DO_TS_INSERT_NONSTACKABLE_ITEM`
(Arb_part_038.c:771), which allocates the id into atom `+0x10` and fills a local ItemData - the
536-byte record 0x27A4 serves - from the atom. `World/ItemCreate.cs` `Map` is that copy (record
offset = the local's distance from the struct base: template +0x18 -> +0x08, owner +0x38 -> +0x10,
slot +0x48 -> +0x24, +0x5C -> +0x28, +0x58 -> +0x2C, bound +0x260 -> +0x34, the 16-byte timestamp
+0x1F0 -> +0x1D0, the custom string +0x17C -> +0x17C, and 25 more). Proven against the four created
items the real Arbiter served back later (cap_social2 10018, cap_social3 10030, cap_multiworld
10052/10053): every mapped field matches.

On the wire the reply is the request with the id at +0x10 (cap_final 5914 -> 5915, 5974 -> 5975
byte-exact). For some templates the real Arbiter also writes a template-derived value at +0x104
(1 for 17000/17004/17005/72277, 0 for 168010/168011/156258/156259), and for period items a computed
expiry at +0x1F0; both need item template data we do not load, so we leave World's values.

Atom ops seen in the captures and still not modelled: **37** (RECV_PARCEL / _EX, 9 atoms, parcel
receive), **40** (ITEM_SIMPLE_ATOM, 1), **92** (ITEM_UNIDENTIFY, 2). None has reached TeraSharp's
log yet.

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

## 5. Proposed storage *(superseded by §7 — the shape changed, see there)*

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

`money` is not an item — op 9 carries template id 0. It lives on the `characters` row next to
`level`/`exp` from T6, not here. **T59 built that column — section 8.**

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

1. ~~Echo `0x2769` properly (atoms through, insert ids filled). No storage yet.~~ **Done (T13).**
   `DbProxyHandlers.BuildDbs2769` copies both atom lists through under the 21-byte header and
   fills `atom+16` from `CharacterStore.NextItemId` for every op-7 atom that arrives with 0; the
   counter lives in a `counters` table so ids keep climbing across restarts. Byte-exact against
   seq 2072→2073 and 2211→2213 (`data/cap_item_single.bin`). Still no item storage: World's
   writes are acknowledged and forgotten, so the items are gone on the next login.
2. `items` table + rebuild `0x27A4` from it, seeded at character creation with the six starter
   records. Replaces `BuildStarterInventory`. **Half done (T14):** `World/StarterInventory.cs`
   renders the payload from a per-class table instead of the captured glaiver list, so a warrior
   gets a warrior's gear. Still no storage — the kit is recomputed from the class on every login,
   so anything the player does to it is lost. The renderer is the piece the items table will
   reuse; only the source of the rows changes.

   The table is `Executable\Datasheet\CreateCharData.xml`, which is on disk next to the server.
   The real Arbiter reads it in `Handler_C_CREATE_USER` → `CreateUserCallback` →
   `DatasheetManager::GetCreateCharData(classId, CreateCharData&)` →
   `AccountManager::CreateUser_FillInitData` → `AccountManager::ExecCreateInitItems`, which writes
   the rows to its own DB — that is why the six items are already in the first `0x27A4` with no
   `0x2768` before it. It is keyed by **class alone**; race and gender do not enter into it.
   Placement comes from `ItemTemplate.xml`'s `combatItemType` (EQUIP_WEAPON / EQUIP_ARMOR_BODY /
   _ARM / _LEG) through `GetInvenTypeFromEquipPart`, and the INVTYPE numbering is documented in
   the header comment of `ItemEquipRestriction.xml`:
   `NON_EQUIP 0, WEAPON 1, HEAD 2, BODY 3, HANDS 4, FEET 5, …`. Two orders matter and they
   differ: ids are allocated in datasheet order, the payload is emitted sorted by (pocket, slot).
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

---

## 7. What T44 actually built

§5 proposed a dedicated inventory table. What shipped is one table for **every** container,
because T42 had already created it for the warehouse and two tables would have meant two answers
to "where is item 1001".

```sql
CREATE TABLE items (
  item_db_id  INTEGER PRIMARY KEY,   -- the id the Arbiter hands World; never reused
  owner_db_id INTEGER NOT NULL,      -- character id, or account id for an account-keyed pocket
  inven_type  INTEGER NOT NULL,      -- enum INVEN_TYPE = the pocket id at record+28
  slot        INTEGER NOT NULL,
  template_id INTEGER NOT NULL,
  amount      INTEGER NOT NULL,
  record      BLOB                   -- the 536-byte ItemData, when we were given one
);
```

`inven_type` is the whole design. 0 is the bag, 14 the worn slots, 1/3/9/12 the warehouses
(`status/MAIL-WAREHOUSE.md` §6 pins the values); the inventory load is every pocket that is **not**
a warehouse, and the warehouse view is one pocket at a time. That is exactly how the real server's
`Items` table works — `spUpdateItemOwner` moves a row between containers and `spUpdateItemPos`
moves it inside one, and there is no second table.

### Why the `record` blob stayed

§5's "Why a blob column as well as the fields" was right, and for a sharper reason than it gave:
keeping the exact 536 bytes is what makes the seeded `0x27A4` **byte-identical** to the captured
one. A row that never had a record (anything that arrived as a transaction atom) gets a
synthesised one — safe, because the bytes we leave zero are the uninitialised Arbiter heap §2
describes, which World has to ignore.

### Seeding

The starter kit is written into rows the first time a character's inventory is loaded and never
again; `CountInventoryItems(playerId) == 0` is the test. The rebuild concatenates the rows in
(pocket, slot) order, which is the order `StarterInventory` sorts the kit into and the order the
capture lists it in — verified: the six captured records sort back into exactly their captured
order, and the rebuild is the original file byte for byte.

### Applying, and the one rule that matters

Atoms are applied **from the reply, never from the request**. The reply is the copy with the
allocated item DB ids filled in; parsing the request would allocate a second set and store ids
World was never given. Four messages carry atoms — 0x2768, 0x272E (quest rewards), 0x278E (skill
fees) and the warehouse transfers — and all four go through the same code.

Ops modelled: 2 amount, 3 move-to-empty-slot, 6 detach (a deliberate no-op; the paired 11 does the
delete), 7 insert, 9 character money (a `characters.money` delta, not an item row — section 8),
11 delete, 36 swap two occupied slots, plus
the warehouse ops 13/14/15/17. **Everything else is echoed but not applied**, and logged. An atom
that names no item at all is dropped rather than given a fresh id: a row under an id World has
never seen is worse than a missing row, because the next operation on it misses.

### The atom triples, checked against captured bytes at last

T42 pinned the source/destination triples from World's `PrepareWareSendTransaction`. T44 decoded
the five atoms in `data/cap_item_single.bin` with them and they fit exactly:

```
seq 2072  op  7  id 0   tpl 81251  src=(2,0,4) dst=(2,0,4) delta 1     pick one up into bag slot 4
seq 2211  op  6  id 15  tpl 81251  src=(2,0,4) dst=(2,0,4)   \
          op 11  id 15                                        |  consume two,
          op  6  id 13  tpl 81255  src=(2,0,2) dst=(2,0,2)    |  insert the result
          op 11  id 13                                        |
          op  7  id 0   tpl 81253  src=(2,0,2) dst=(2,0,2) delta 1   /
```

Both triples are populated and equal for an op that stays in one container, which is what §3's
"same as 48 in every captured atom" was seeing.

### Still open

- ~~**Character money** (op 9) is counted and dropped.~~ **Done (T59)** - see section 8.
- **`0x27A3` pocket data** is still the empty form. A character who has bought bag slots would have
  a non-empty list and we have never seen one.
- **Expanded bag tabs.** Everything that is not a warehouse pocket is served as inventory, so an
  unknown tab id would come back in the load; that is the safe direction, but it is untested.

---

## 8. T59 — character money

The live symptom was one log line per kill:

```
items: character money 10000000000 for owner 4 - not stored
```

§7 "Still open" had it as *counted and dropped*. It is stored now, and served back at login.

### 8.1 The atom is a signed DELTA, not a new total

`Inventory::PrepareMoneyTransaction(__int64 amount, enum ChangeMoneyReason, ...)`
(WorldServer.exe.c:1407fa050) is the only builder of an op-9 atom, and it settles the question
three ways over:

```c
if (param_2 != 0) {                                    // amount 0 -> NO atom at all
    // a negative amount is refused when it would take Inventory+0x78 (the money already
    // held) below zero -> error 0x18/0x1a; a positive one is refused when GetAddableMoney
    // reports less room than the amount -> error 0x19
    atom = new;
    *(u32     *)(atom + 4)     = 9;                    // op
    *(longlong*)(atom + 0x20)  = playerId;             // srcOwner
    *(longlong*)(atom + 0x38)  = playerId;             // dstOwner
    *(u32     *)(atom + 0x28)  = 0;                    // srcInven
    *(u32     *)(atom + 0x40)  = 0;                    // dstInven
    *(longlong*)(atom + 0x50)  = param_2;              // <- the delta, verbatim
    *(u32     *)(atom + 0x204) = reason;               // ChangeMoneyReason
}
```

* the guard is `current + amount < 0`, i.e. the value is *added* to what the inventory holds;
* an amount of 0 emits nothing, which an absolute update could never do;
* item db id (+0x10) and template id (+0x18) stay 0 — op 9 is identified by the op alone.

`ChangeMoneyReason` at **atom + 0x204** is new here; §3's table stops well before it. Nothing
reads it yet.

The capture agrees: `cap_newchar_client.log`'s `S_ITEMLIST` (48029) carries `int64 money` at body
+28, and for "Test" it runs 0 → 19 → 67 → 30 → 114 → 230 across the session. Every step is the
previous total plus one atom's delta, and the 30 → 114 step is the `+84` §1 already recorded from
a kill.

### 8.2 Money reaches World inside the enter-world blob, at 448

Not in `0x27A4`. `DBS_USER_LOAD_INVENTORY`'s whole layout is `InvenItems`, `DlmId`,
`ContinueReceive` (dumper `FUN_140209730`, Arb_part_016.c:760), and its array is a plain run of
0x218-byte `ItemData` records with no money entry (writer Arb_part_064.c:15328, stride `0x218`,
byte length `count * 0x218`). Not in a separate load either: `Handler_DBS_GET_MONEY`
(WorldServer.exe.c:3013535) is an empty stub, and neither `SDB_GET_MONEY` (0x2747) nor
`DBS_UPDATE_USER_MONEY` (0x27EA) appears anywhere in the three captures.

It is a field of the 15312-byte blob, i64 at **448 (0x1C0)**, proven from both sides:

| side | evidence |
|---|---|
| Arbiter | `FUN_140044d50(recordset, L"money", UserData + 0x1c0)` — Arb_part_032.c:17798, in the same bind list as `gender` at +0xC4 and `class` at +0xC8, which are our `GenderOffset` / `ClassOffset` |
| World | `Inventory::SetMoney(User + 0xA478, *(__int64 *)(context + 0x268))` — WorldServer.exe.c:1663604; the blob is memcpy'd to `context + 0xA8` by `UserEnterWorldContext::SetRecvData` (:1661252), so `0x268 - 0xA8 = 0x1C0` |

`Inventory::SetMoney` is `*(__int64 *)(inv + 0x78) = money` (:1484331) — a plain assignment, so the
blob value is **absolute**. `DBS_UPDATE_USER_MONEY` (0x27EA, `[06] u32 OwnerDbId, [0A] i64 Money`,
frame 0x12) ends in the same setter through `User::UpdateMoneynShow(money, true)`, so a future GM
`set_money` on another character has an unsolicited A→W push available. We do not send it yet.

### 8.3 Why it needs its own column

**World never writes the field back.** Blob + 0x1C0 is zero in all six `SDB_UPDATE_USER_DATA`
(0x27CB) frames of `cap_newchar.log` — including the last one, sent after the character had earned
230 gold. Storing money only inside the blob would therefore lose it on the first save.

So `characters.money` is authoritative and the blob is stamped from it on the way out:

```
SDB_ITEM_SINGLE op-9 atom   ->  characters.money = MAX(0, money + delta)
CharacterStore.Read(row)    ->  StarterBlob.WriteMoney(WorldBlob, money)   (blob + 448)
DBS_USER_ENTERWORLD 0x2738  ->  [u32 19][u32 15312][u32 replyId][u8 1][blob]
```

Stamping in `Read` rather than in a builder means both 0x2738 senders get it — `OnUserEnterWorld`
and the human-owned `WorldEntry.BuildCharacterDataPayload` — with no change to either.

The clamp at 0 is ours: World refuses a negative result before it ever builds the atom, so a
negative can only come from a frame World did not send.

`T59_money_round_trips_into_the_enter_world_blob_byte_exact` pins it against
`data/starter_blob.bin`: with money 0 the served blob is the capture byte for byte, and with money
set exactly the eight bytes at 448 differ.

### 8.4 Still open after T59

- **`ChangeMoneyReason`** (atom + 0x204) is parsed by nobody. It is what a money log would key on.
- **`DBS_UPDATE_USER_MONEY` (0x27EA)** is not sent. Until it is, a money change made outside
  World's own inventory (a GM command against an offline character, a parcel payout) will not show
  up until that character relogs.
- **`S_ITEMLIST.money`** is built by WorldServer, not by us, so there is nothing to serve there.

## 9. T65 — `SDB_ITEM_TRADE` (0x276A) and the 568-byte record

The DB half of a completed player trade. It arrived live on 2026-09-16 as a 2306-byte frame with
"no replay" and, unanswered, head-blocked that character's DLM queue.

| | `SDB_ITEM_SINGLE` 0x2768 | `SDB_ITEM_TRADE` 0x276A |
| --- | --- | --- |
| Guard / min frame | 0x1E | 0x22 |
| Payload header | 24 B: `[offA][lenA][offB][lenB][reqId][playerId]` | 28 B: the same four slots, then `DlmId`, `OwnerDBID`, `TargetDBID` |
| Record | `ItemTransactionAtom`, **0x358** | `ItemTransactionGiveTake`, **0x238** |
| Reply | 0x2769, 21-B header `[offA][lenA][offB][lenB][id][ok]` | 0x276B, **the same 21-B header** |

The stride is the handler's own divisor (`(len - 1) / 0x238 + 1`, Arb_part_063.c:11880) and it checks
out against the live frame: `6 + 28 + 4 x 0x238 = 2306` exactly. The record's *head* is shared with the
856-byte atom — `DO_TS_CHANGE_ITEM_OWNER` (Arb_part_037.c:18245) reads `+0x10` item db id,
`+0x20`/`+0x28` src owner+inven, `+0x38`/`+0x40`/`+0x48` dst owner+inven+slot, op at `+0x04` — so one
reader serves both and only the stride is parameterised (`WarehouseHandlers.ParseAtoms(.., recordSize)`).

Echo rule is T13's: both lists copied back, with a freshly allocated item db id written into every
insert that arrived with 0. Both lists are then applied in order, A then B, to the same `items` table.

**Gap.** The op index for `TS_CHANGE_ITEM_OWNER` is not known — the dispatch is a data jump table
(`DAT_140f02f30`) that the decompile does not spell out, and no capture of a 0x276A exists. Records
carrying that op are echoed but change no row, which is the standing rule for an unmodelled op
(section 4). Money (op 9) and inserts (op 7) in a trade DO apply. One live 0x276A capture closes this.

## 10. T69 — TS op 0x10

`TS_WARE_GET_ITEM` (16): the whole row moves OUT of a warehouse pocket to
`(dstOwner, dstInven, dstSlot)` — the withdraw twin of `TS_WARE_MOVE_ITEM` (0x0E). One example in
cap_social2.log, seq 2085, alone in its list: item 10018, template 17000, (2, inven 1, slot 2) →
(1003, inven 0, slot 2), delta 0. It is applied by the same case as 0x0E and 0x03.

That brings the ops a normal session issues to: 2, 3, 6+11, 7, 9, 0x0D, 0x0E, **0x10**, 0x0F, 0x11.
`TS_CHANGE_ITEM_OWNER` (the player-trade op behind `SDB_ITEM_TRADE`, T65 section 9) is still the one
whose index is unknown.


---

## T145 - the 886 B grant frame is not in `arbiter-2026-09-20.log`

Stopped before fixing anything: three of the brief's four load-bearing facts are absent from
the named log, and the one that is attributable says the opposite of what the brief reads into
it. Recorded so the next session does not re-derive it.

### What the log does and does not contain

`<captures>\arbiter-2026-09-20.log` (183 KB, UTF-16LE, 974 lines, 14:27:42 - 14:32:40):

| the brief says | the log says |
|---|---|
| an 886 B `SDB_ITEM_SINGLE` grant frame | the **only** `0x2768` in the file is **len=30**, at 14:31:00. No 886-byte frame of any opcode |
| it logged `0 inserted, 0 moved, 1 amount, 0 deleted (0 atoms changed no row)` | the words `inserted`, `moved`, `deleted`, `amount`, `atoms changed` and `no row` appear **zero** times |
| the grant happened at the level jump | the `level 70` jump (14:31:11, account 2, player 3) produced **no `0x2768` at all** - not before it and not after it |
| `level` / `perfect_level` | one `level 70`; no `perfect_level` anywhere |

`world-2026-09-20.log` has no `2768`, `ITEM_SINGLE`, `inserted` or `atoms` either.

### What the level jump actually did (14:31:11, player 3)

`0x273B S_UPDATE_EXP_LEVEL` -> level 70 stored; `0x1465 SA_CREST_POINT` -> 60 points;
`0x1463 SA_LEARN_ALL_CREST_ACQUIRABLE` -> 61 new crests; `0x272E SDB_SET_QUEST_INFO` x13 ->
questDbId 36..48; `0x2899` x18 and `0x2891` x13. **No item frame.** So on this run the level
command granted no gear through the DB proxy, and a failed grant cannot be what broke the bag.

### Why the one 30-byte `0x2768` logged nothing, and why that is correct

`OnItemSingle` (`DbProxyHandlers.cs:2418`) logs the echo line only when
`declaredA + declaredB > 0` and the accounting line only when `touched > 0`. A 24-byte payload
carries no atoms, so both counts are 0 and the handler is silent by design. Nothing is wrong
with that frame.

### The accounting line is real, and it does not say what the brief reads into it

The format string exists at `DbProxyHandlers.cs:2446`, so the user did see that line - from a
run other than this file. But `0 inserted ... 1 amount ... (0 atom(s) changed no row)` means:
one atom changed an item's **amount**, and **zero atoms failed to find a row**. `Ignored` is
the "changed no row" counter and it is 0. That is a successful single amount update, not a
rejected batch of inserts. An 886-byte frame carrying one amount atom is unremarkable - the
size is the item binary, not the atom count.

### Also checked and cleared

`SDB_USER_LOAD_INVENTORY` logging two sixes for player 3 at 14:31:00 (`6 starter items for
class 0 (warrior)` then `6 item row(s) from the store`) is not a mismatch: the handler builds
the kit, seeds rows only when the character has none, and then **rebuilds the reply from the
rows** with `BagItems.BuildPayload`, so the ids World is handed are the row ids. No
`seeded N starter row(s)` line appears for player 3, i.e. the rows already existed.

### What is needed to finish this

1. **Bytes.** The arbiter log records opcode and length only; it has no hex. Decoding the 886 B
   frame needs the A-W tap for that session (`tools/reframe-tap.ps1` over an
   `arb_world_*.log`), and no tap log covers 2026-09-20.
2. **Which run.** The console text around the `0 inserted ... 1 amount` line, or the arbiter log
   for the day it actually happened - it is not 2026-09-20.
3. **Which character and when.** The brief names dob / warrior / testagain; the only level jump
   in this file is player 3 at 14:31:11, and it moved no items.

Without (1) there is nothing to decode and no way to tell an unknown atom type from an owner
mismatch from a slot conflict - which is the whole of part 1, and parts 2 and 3 are built on its
answer.

---

## T145 - cap_bag: the equip and learn "wedges" are one login wedge (0x2732)

| cap_bag.log | what | before | now |
|---|---|---|---|
| 314 W->A 0x2732 SDB_SET_QUESTLIST_INFO, 759 B, `test` (10), 9 quests | batch quest write at login | "no replay" - never answered | 0x2733, quests stored (UpsertQuest) |
| after 314 | no SDB_ for `test` for the rest of the capture: no 0x2930 hold load, no 0x278E learn, no bind/equip write | queued behind 314 inside World | released |
| client 809..940 | 9 x C_SKILL_LEARN_REQUEST, **no server reply at all** | DBUserLearnSkillContext never leaves World | - |
| client 697..764 | C_BIND_ITEM_BEGIN_PROGRESS / EXECUTE x2 (the equip attempt: bind-on-equip) | same | - |
| 120, 200 W->A 0x13CC SA_EQUIP_ITEM_LEVEL, 18 B | one-way push; 0x13CD is BSA_EXIT_BATTLE_FIELD, no reply in any real tap | - | unchanged |
| (none) 0x1491 SA_UPDATE_USER_STATUS | one-way (0x1492 is AS_REQUEST_SPAWN_NPC); cap_final / cap_newchar never answer it | - | unchanged |

- **0x2733** (Handler_DBS_SET_QUESTLIST_INFO, frame >= 0x24): `[offA][8n][offB][0][offC][0][reqId][ok][apply]`
  + n x (questId, questDbId). `apply` = 0 makes World ignore the reply and leave the transaction
  open. Byte-exact against all three real pairs (cap_social4 2935/3244/7843).
- **0x2813 SDB_EQUIP_ITEM** is written by the same template as SDB_ITEM_SINGLE (886 B = one
  856-byte atom), and Handler_DBS_EQUIP_ITEM reads exactly what Handler_DBS_ITEM_SINGLE reads. It is
  answered with 0x2814 in the 0x2769 shape and its atoms applied to the items table by the same
  code. **No tap contains one** - cap_bag's never left World - so recapture one equip to pin it.
- **The skill refusal is not vaporize**: the learn requests fall in the window World had `test`
  visible (0x282D 0 at 00:34:15, 1 again at 00:35:09). AS_ENTER_WORLD[111] was 5. The capture holds
  no refusal message; the requests simply go unanswered, which is what a stuck DB queue does.

---

## T151 - three live-run follow-ups

| # | symptom | cause (bytes / decompile) | fix |
|---|---|---|---|
| 1 | "atom op 51 not modelled (item 1068, 0:2 -> 0:2)" in SDB_EQUIP_ITEM | op 51 is **bind**, not a move: World's `PrepareChangeSealItem` copies the item's current position into both triples and sets `atom+0x168 = GetDbId()`; the Arbiter's `DO_TS_BIND_ITEM` is `ItemUtil::UpdateItemBound(item, owner, owner, item+0x1c4)` -> ItemData **+0x34 bound, +0x38 owner**. Op 63 (`DO_TS_UNBIND_ITEM`) = `UpdateItemBound(item, 0, atom+0x168, atom+0x1c4)` | both applied to the stored 536-B record (a synthesised one if the row had none), position untouched |
| 2 | system mail listed with blank sender / subject | `OnMakeSysParcel` filed Title and Message as `""`. SA_MAKE_SYS_PARCEL carries Writer / Title / Message refs at payload 8 / 12 / 16 (cap_social4 2962: `@Achievement:6903`, `@2051`, `@2052\vAchievementName\v@Achievement:6900`); the real rows list them verbatim (seq 4560: +0x04, +0x960) with type **102** at +0xA4 and the receiver's name at +0x54 | strings stored; the synthesised row gets type and receiver name |
| 3 | every glyph locked | AS_LEARN_ALL_CREST_ACQUIRABLE's list is the crests World must **not** learn: `DBUserAutoLearnCrestContext::SetRecvData` erases each id in it from the set it asked for. We echoed all 42, so none were learned. Real Arbiter: cap_social4 2820 (42 crests) -> 2821, **empty** | reply always empty; the request's crests are stored |

Not done: (1) no tap yet holds an SDB_EQUIP_ITEM, so whether an equip also moves the row (bag -> inven 14) in the same frame is unconfirmed - the live log only prints unmodelled ops. (2) System-parcel attachments (row +0xE0, template/amount per 0x1B0 slot) are still not listed or delivered - that needs SDB_RECV_PARCEL_EX on a system parcel (cap_social4 has six). (3) World re-asks for the crests at every login because our enter-world data carries none; with the empty reply it simply learns them again, and tier unlocks go through the item transactions already handled.


## T166 - enchanting: ten generic transactions, eight record-edit ops

Every op below is the same handler on the real Arbiter: request `[ref @6 -> 856-byte atoms][DlmId @0E][UserDbId @12]` (frame >= 0x16), ExecTrans, reply `[ref -> atoms][DlmId][ok]` (World reads >= 0x13) - `DbAckTable` rows with `b6=6`. SDB_ITEM_MERGE replies `[DlmId][ok]` only (`a6`). SDB_ITEM_DECOMPOSE 0x275C is dead on both sides (Arbiter stub, no World builder) and stays unanswered. **World rolls; the Arbiter never does**: success, penalty and "failed, level kept" are all expressed in the atoms World sends, so applying them is following World's result.

| SDB | World context | atoms |
|---|---|---|
| 0x276E ITEM_ENCHANT | DBItemEnchantContext (ContractEnchant::ExecuteTemper) | 2 / 11+6 materials, 80, 4 (success or penalty level), 73, 75 |
| 0x2770 ITEM_ENCHANT_IDENTIFY | DBItemEnchantIdentifyContext | scroll, 4 or 86 (+0x348 masterwork, +0x349 awaken), 73 |
| 0x28F4 ENCHANT_ITEM_BOOST | DBItemEnchantBoostContext | material, 75 on target, 4 (=0) on source |
| 0x2932 ITEM_AWAKEN | DBItemAwakenContext | materials, 80, 81 |
| 0x2934 ITEM_UNBIND | DBItemUnbindContext | scroll, 63, 93 |
| 0x295F EQUIPMENT_INHERITANCE | DBEquipmentInheritContext | 8 (new item, enchant at +0x5C), 11/6 sources |
| 0x28A1 ITEM_UNIDENTIFY | DBItemOptionResetContext - **op 92 is option reset** | materials, 92 |
| 0x275A ITEM_EXTRACT / 0x2920 ITEM_DECOMPOSITION | DBItemExtractTransaction / DBItemDecomposeContext | 9 cost, source 11/2, outputs 7/8/2 |
| 0x2774 ITEM_MERGE | DBMergeItemContext | 67 period extend, consumed item |

Record edits (`World/ItemEdits.cs`, DO_TS pairing inferred from the fields each side touches): 4 +0x28 <- +0x5C; 86 the same plus masterwork/grade/passive (+0x138/+0x134/+0x13C) and awaken +0x139; 73 +0x160 <- +0x16C; 75 +0x16C/+0x168 <- +0x174/+0x178; 80 +0x1C0; 81 +0x139 = 1; 92 +0x54..0xCB / +0xD0..0xE7 / +0x134 <- +0x60..0xD7 / +0xEC..0x103 / +0x104; 93 +0x170 <- +0x1D0. Op 6 reads as DO_TS_POP_ITEM (medium).

**Pinned:** 0x28A1 against cap_multiworld 10859/10860 and 11043/11044 - our reply equals the real one except atom +0x74..0xC6, the empty passive slots the real Arbiter fills from template/EnchantData (not loaded). Everything else is decompile-derived; no capture holds another of these ops. **Not modelled:** that fill, op 86's rolled passive, op 67's time transfer, and the Arbiter's owner/old-level checks (logged, not enforced - our record can lag World's item).
