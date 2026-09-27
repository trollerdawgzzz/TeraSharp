# Contract broker — the two-party interactions the Arbiter brokers

**What this unblocks.** On 2026-09-15 04:24, two players in world, one invited the other to a
party. Nothing happened. The tap shows why: World sent `SDB_FETCH_THROUGH_ARBITER_CONTRACT`
(0x2809, 58 B) and later `SDB_SEND_END_THROUGH_ARBITER_CONTRACT` (0x280E, 58 B), both of which T47
had sealed in `WorldReplayTable.OneWayFromWorld`, so the Arbiter answered neither and the invite
never reached the target. `PartyManager` never saw a packet, because the packet it waits for
(`SA_JOIN_PARTY` 0x1395) is only sent by World *after* the contract completes. Guild creation is
contract type 10 and is gated on being in a party, so the same seal blocks guilds.

**Why T47 sealed them.** The note in `WorldReplayTable` says 0x2809 "sends NOTHING at all - no
World frame, no client packet". That reading is wrong, and this document is the correction. The
handler itself sends nothing — it dispatches on `ContractType` into one of four `FetchWork`
objects, and **those** send. `FetchWork::ResponseFailure` and `FetchWork::ResponseSuccess` both
emit `DBS_FETCH_THROUGH_ARBITER_CONTRACT` (0x280A); the success path additionally fans
`DBS_ASK_THROUGH_ARBITER_CONTRACT` (0x280B) out to every opponent. T47 stopped at the handler and
never followed the four calls.

Everything below is from the decompile: `Arb_part_*.c` for the Arbiter, with the function address
given for each claim. Nothing here has been seen on a tap except the two request opcodes and their
lengths.

---

## 1. The eight opcodes

The family is contiguous, 0x2809-0x2810, and splits evenly: four `SDB_` requests from World, four
`DBS_` pushes back.

| opcode | name | dir | who it goes to |
|---|---|---|---|
| 0x2809 | `SDB_FETCH_THROUGH_ARBITER_CONTRACT` | W->A | — the initiator's World asks the Arbiter to broker |
| 0x280A | `DBS_FETCH_THROUGH_ARBITER_CONTRACT` | A->W | the **initiator's** World session — the verdict |
| 0x280B | `DBS_ASK_THROUGH_ARBITER_CONTRACT` | A->W | **each opponent's** World session — a fan-out, one frame each |
| 0x280C | `SDB_ASK_THROUGH_ARBITER_CONTRACT` | W->A | the opponent's World answers `CanContract` |
| 0x280D | `SDB_SEND_BEGIN_THROUGH_ARBITER_CONTRACT` | W->A | — becomes the CLIENT packet `S_BEGIN_THROUGH_ARBITER_CONTRACT` |
| 0x280E | `SDB_SEND_END_THROUGH_ARBITER_CONTRACT` | W->A | — becomes `S_END_THROUGH_ARBITER_CONTRACT` plus a 0x280F fan-out |
| 0x280F | `DBS_SEND_END_THROUGH_ARBITER_CONTRACT` | A->W | each participant's World session |
| 0x2810 | `DBS_REPLY_THROUGH_ARBITER_CONTRACT` | A->W | the **requestor's** World session — the answer |

Names from `World/DbProxyOpcodeNames.cs`, which was built from World's own opcode->name switch.

**None of them carries a DlmId.** Every dumper names `ContractorDbId` / `ContractType` /
`ContractId` and nothing else, so an unanswered contract frame cannot head-block a user's DB queue
the way a missing `DBS_` reply does (`status/HANDOFF.md` §1). It just silently does nothing, which
is exactly what was observed.

## 2. Contract types

`Handler_SDB_FETCH_THROUGH_ARBITER_CONTRACT` (`FUN_140743930`, Arb_part_063.c:7017) reads
`ContractType` at frame [0x1A] and switches on four values, each constructing a different
`FetchWork`. The class names are in the RTTI strings of their `operator()`:

| type | `FetchWork` | what it is |
|---|---|---|
| **4** | `ContractPartyFetchWork` (`FUN_14099e130`) | **party invite** — "join my party" |
| **5** | `ContractPartyApplyFetchWork` (`FUN_14099dd30`) | **party apply** — "let me join yours" |
| **10** | `CreateGuildFetchWork` (`FUN_14099e610`) | **guild creation** — needs the co-founders, hence the party gate |
| **0x23 (35)** | `TradeBrokerOpenDealFetchWork` (`FUN_14099e730`) | **trade broker open deal** |

Anything else falls off the end of the `if` chain and the Arbiter sends nothing at all — no error,
no reply. That is the real Arbiter's behaviour for an unknown type and it is worth knowing, because
it is indistinguishable from the bug this task fixes.

**Duel and player-to-player trade are NOT in this list.** That matches the live session: trade
between two players in the same zone worked, because World brokered it locally and never asked the
Arbiter. The Arbiter is only involved when the two parties might be on different World processes —
which is what "through Arbiter" means.

## 3. Frame layouts

Inter-server conventions, both confirmed against these dumpers:

- **`bytes` ref** = 8 bytes, `[u32 frame-relative offset][u32 byte count]`.
- **`wstr` ref** = **4 bytes**, `[u32 frame-relative offset]` only; the data is null-terminated
  UTF-16LE. (`DBS_ASK`'s dumper reads `ContractorName` from the offset at [6] and `OpponentName`
  from the one at [10] and never reads a count — `FUN_14016be20` takes a pointer.)

Offsets are FRAME-relative, so payload index = frame offset − 6, as everywhere else in this
protocol.

### 3.1 `SDB_FETCH_THROUGH_ARBITER_CONTRACT` 0x2809 — W->A

Dumper `FUN_14022f640` (Arb_part_017.c:6732), guard `0x21 < len` so the minimum frame is **34**.

```
[0x06] u32  Param offset            \ bytes ref
[0x0A] u32  Param count             /
[0x0E] u32  FetchDataList offset    \ bytes ref
[0x12] u32  FetchDataList count     /
[0x16] i32  ContractorDbId          the initiator's character db id
[0x1A] i32  ContractType            4 / 5 / 10 / 0x23 - see section 2
[0x1E] i32  ContractId              World's id for this attempt
```

The live frames were 58 bytes: 34 fixed plus 24 bytes of `Param` and/or `FetchDataList`. For a
party invite the target is named inside one of those two blocks — `ContractPartyFetchWork` reads it
as a **name string** (`FUN_14082dc50(DAT_141214fe8, name, 3)` is a by-name user lookup), not as a
db id. See section 7 for what that costs us.

### 3.2 `DBS_FETCH_THROUGH_ARBITER_CONTRACT` 0x280A — A->W, to the initiator

Dumper `FUN_1401f3e60` (Arb_part_015.c:5921), fixed part **34**. Writer `FUN_14090aee0`
(Arb_part_079.c:908) and `FetchWork::ResponseFailure` `FUN_1409cde70` (Arb_part_085.c:17031).

```
[0x06] u32  AskList offset     \ bytes ref: N x u32 user db ids
[0x0A] u32  AskList count      /  (the writer masks the vector size & 0xFFFFFFFC)
[0x0E] i32  ContractorDbId
[0x12] i32  ContractType
[0x16] i32  ContractId
[0x1A] i32  ContractIndex      the Arbiter's handle for the live contract
[0x1E] i32  ErrorNo            0 = brokered, non-zero = refused
[0x22..]    AskList data
```

`ResponseFailure` backpatches the offset slot to the frame length (34) and leaves the count at 0 —
so a refusal is exactly 34 bytes with `[6] = 34, [10] = 0`. That asymmetry is worth copying
verbatim: an offset of 34 with a count of 0 is what the real Arbiter puts on the wire, not an
offset of 0.

### 3.3 `DBS_ASK_THROUGH_ARBITER_CONTRACT` 0x280B — A->W, one per opponent

Dumper `FUN_1401eea30` (Arb_part_015.c:2291), fixed part **34**. Writer: the loop at
Arb_part_079.c:14312, which walks the opponent vector and, for each opponent with a live World
session, builds one frame and sends it to **that** session.

```
[0x06] u32  ContractorName offset   wstr ref (offset only)
[0x0A] u32  OpponentName offset     wstr ref (offset only)
[0x0E] i32  ContractIndex
[0x12] i32  ContractorDbId
[0x16] i32  ContractType
[0x1A] i32  ContractId
[0x1E] i32  OpponentDbId
[0x22..]    ContractorName, then OpponentName, each null-terminated UTF-16LE
```

The writer emits the names in that order, backpatching each offset slot immediately before writing
its string.

### 3.4 `SDB_ASK_THROUGH_ARBITER_CONTRACT` 0x280C — W->A

Dumper `FUN_14022a6d0` (Arb_part_017.c:3192), guard `0x1A < len`, fixed **27**, no refs.

```
[0x06] i32  ContractIndex
[0x0A] i32  ContractorDbId
[0x0E] i32  ContractType
[0x12] i32  ContractId
[0x16] i32  OpponentDbId
[0x1A] u8   CanContract      the opponent's World says whether it will allow this
```

`Handler_SDB_ASK_THROUGH_ARBITER_CONTRACT` (Arb_part_063.c:810) is 38 lines and sends nothing: it
records the answer against the contract index and lets the contract proceed or die.

### 3.5 `SDB_SEND_BEGIN` 0x280D and `SDB_SEND_END` 0x280E — W->A

`SDB_SEND_BEGIN` dumper `FUN_1402398b0` (Arb_part_017.c:13785), guard `0x25 < len`, fixed **38**:

```
[0x06] u32  Param offset         \ bytes ref
[0x0A] u32  Param count          /
[0x0E] u32  AskUserList offset   \ bytes ref: N x u32 db ids
[0x12] u32  AskUserList count    /
[0x16] i32  ContractorDbId
[0x1A] i32  ContractType
[0x1E] i32  ContractId
[0x22] i32  ContractIndex
```

`SDB_SEND_END` dumper `FUN_140239c50` (Arb_part_017.c:13942), guard `0x21 < len`, fixed **34** —
the same layout **without** `ContractIndex`. The live 0x280E frames were 58 bytes, so 24 bytes of
`Param` + `AskUserList`, the same shape as the 0x2809 that started it.

`Handler_SDB_SEND_BEGIN` (Arb_part_064.c:2732) sends only the client packet
`S_BEGIN_THROUGH_ARBITER_CONTRACT`. `Handler_SDB_SEND_END` (Arb_part_064.c:2983) sends the client
packet `S_END_THROUGH_ARBITER_CONTRACT` **and** walks `AskUserList` pushing 0x280F to each other
participant's World session.

### 3.6 `DBS_SEND_END` 0x280F and `DBS_REPLY` 0x2810 — A->W

`DBS_SEND_END` dumper `FUN_140200fc0` (Arb_part_015.c:14971), fixed **22**, no refs:

```
[0x06] i32  ContractorDbId
[0x0A] i32  ContractType
[0x0E] i32  ContractId
[0x12] i32  OpponentDbId
```

`DBS_REPLY` dumper `FUN_1401fead0` (Arb_part_015.c:13467), fixed **26**, no refs:

```
[0x06] i32  ContractRequestorDbId
[0x0A] i32  ContractRequesteeDbId
[0x0E] i32  ContractType
[0x12] i32  ContractId
[0x16] i32  Reply
```

## 4. Accept, reject, cancel — the client half

`Handler_C_REPLY_THROUGH_ARBITER_CONTRACT` (Arb_part_041.c, the `GET_CLIENT_BUFFER_BUFSIZE_MISMATCH`
guard is `< 0x1e`) is the whole accept/reject path, and it is the one place both `DBS_` pushes are
built side by side. Reading it settles the direction question the brief asks:

1. The replier is the session's own user (`lVar9`). The other party is looked up **by name** out of
   the packet (`lVar10`); a name that resolves to nobody is answered with the system message
   `CR_PACKET_FORGED` and nothing else happens.
2. `0x280F DBS_SEND_END` goes to the **replier's** World session, carrying
   `ContractorDbId = the other party`, `OpponentDbId = the replier`. "This contract is finished
   for you."
3. `0x2810 DBS_REPLY` goes to the **other party's** World session, carrying
   `ContractRequestorDbId = the other party`, `ContractRequesteeDbId = the replier`, and `Reply`
   straight off the client packet. "Here is your answer."

Then World does the actual work: for a party invite it creates the party and tells the Arbiter with
`SA_JOIN_PARTY` (0x1395), which is where `PartyManager` takes over
(`status/PARTY-DESIGN.md` §6.1). **The contract broker never creates a party itself** — it is a
matchmaker, not a party manager, and that separation is the whole reason `PartyManager` saw nothing.

### `C_REPLY_THROUGH_ARBITER_CONTRACT` (0x5B7C / 23420), min total 30

Read by absolute offset, from the handler, **not** from the `.def`:

```
[0x04] 8 bytes   neither the handler nor the dumper reads these
[0x0C] u16       ContractRequestorName offset (packet-relative)
[0x0E] i32       ContractType
[0x12] i32       ContractId
[0x16] i32       ContractIndex
[0x1A] i32       Reply
```

**`C_REPLY_THROUGH_ARBITER_CONTRACT.1.def` is wrong** and must not be used. It declares
`uint32 type / uint64 id / uint32 response / string recipient`, which is 22 bytes and puts every
field in the wrong place; the handler's own reads and its 30-byte guard say otherwise. This is the
same class of trap as the patch-101 `S_FRIEND_LIST` def (`status/FRIENDS.md` §2) and the ten wrong
guild defs (`status/GUILD-DESIGN.md` §5.5). `ContractBroker` parses it with fixed offsets, the way
`ArbiterClientHandlers` parses `C_SHOW_ITEM_TOOLTIP_EX`.

The other three client packets in the family — `C_ACCEPT_CONTRACT` (0xADE6),
`C_REJECT_CONTRACT` (0x9655), `C_CANCEL_CONTRACT` (0xC366) — are all `int32 type, int32 id` and are
**World's**, not the Arbiter's: they have no `Handler_C_*` in ArbiterServer.exe. Only the
"through Arbiter" reply is ours.

`S_BEGIN_THROUGH_ARBITER_CONTRACT` (0x7E3F, dumper `FUN_14025aaa0`, 22 bytes) and
`S_END_THROUGH_ARBITER_CONTRACT` (0xC7D7, dumper `FUN_14027c6d0`, 14 bytes) have **no def file**,
so they are built by hand too:

```
S_BEGIN   [0x04] u16 name offset  [0x06] u16 Param offset  [0x08] u16 Param count
          [0x0A] i32 ContractType [0x0E] i32 ContractId    [0x12] i32 ContractIndex   -> 22 + data
S_END     [0x04] u16 name offset  [0x06] i32 ContractType  [0x0A] i32 ContractId      -> 14 + data
```

## 5. Error codes

`FetchWork::ResponseFailure(enum ArbiterContractErrorType, long)` carries the code in
`DBS_FETCH.ErrorNo`. There is no name table for the enum in the binary — only the RTTI string — so
what follows is **what each branch tests**, read out of `ContractPartyFetchWork::operator()`
(`FUN_14099e130`, Arb_part_084.c:5749), not a decoded enum.

| ErrorNo | the branch that produces it |
|---|---|
| **0** | success — `FetchWork::ResponseSuccess` registered the contract |
| **2** | the initial value: the target name resolved to nobody, or the target's state is not 2 (in world) |
| 3 | the target was found but the initiator object is gone |
| 5, 6, 7 | three successive `FUN_14038c6b0` / flag checks between the two users |
| 8 | a block-list hit — `FUN_1406bfc10(DAT_141094630, a, b, 0)` in both directions |
| 0xA | a party-state flag on the target is false |
| 0xB, 0xC | the target already has a party (0xC when a further flag at +0x79 is set) |
| 0xF | the target has the "no invites" flag at +0x3c2a |
| 0x10 | adding to the existing party was refused |
| 0x12 | the tail of the success path — see below |

**0x12 is unresolved.** The path that reaches `LAB_14099e549` (the success case) also assigns
`uVar10 = 0x12` a few lines later, and both fall into the same `ResponseFailure` call. Either 0x12
is "already asked, still pending" or the decompiler has merged two blocks. `ContractBroker` never
emits it; **2** is what it sends for every refusal it can detect, because 2 is the value the real
Arbiter uses for the overwhelmingly common case (target not online) and it is the one value whose
meaning is certain.

## 6. The single-World simplification

The whole family exists because a contract can span two World processes. TeraSharp runs **one**
World, so:

- "the initiator's World session" and "each opponent's World session" are the **same link**.
  `ContractBroker` still resolves the target with `WorldBridge.SessionForPlayerId` — because that
  is what proves the target is *in world*, which is the check that produces ErrorNo 2 — but every
  frame goes out through the one `Program.World.SendFrame`.
- The fan-out in 0x280B and 0x280F is still a fan-out: one frame per opponent, in order. With one
  World that is several frames down one socket, which is what World expects either way — it
  demultiplexes on the db ids in the frame, not on the socket.
- `PlanetId` is 2800 in every captured frame and TeraSharp serves one planet, so the
  `DAT_140e2d020` the real Arbiter pairs with each db id is a constant.

What does **not** simplify: the Arbiter still has to own `ContractIndex`, because it is the
Arbiter's handle and both Worlds quote it back. `ContractBroker` allocates it from a per-process
counter starting at 1, the same way `TicketAllocator` and the quest-row ids work.

## 7. What TeraSharp implements, and what it deliberately does not

**Implemented** (`World/ContractBroker.cs`):

- 0x2809 for **ContractType 4 (party invite)**, **5 (party apply)** and, since T76,
  **10 (guild creation)**: resolve the opponents, and answer 0x280A + fan 0x280B out, or
  0x280A with ErrorNo 2.
- 0x280C: record `CanContract`; no frame out, matching the real handler.
- 0x280D: build and send `S_BEGIN_THROUGH_ARBITER_CONTRACT` to the opponents.
- 0x280E: build and send `S_END_THROUGH_ARBITER_CONTRACT`, then fan 0x280F out.
- `C_REPLY_THROUGH_ARBITER_CONTRACT`: 0x280F to the replier, 0x2810 to the requestor.

**Refused, with ErrorNo 2 and a log line, rather than half-done:**

- **ContractType 0x23 (trade broker open deal)**. It needs the broker's own deal state, which
  this class does not have, and a made-up verdict would tell World a contract was brokered that
  never was.

> **T76 corrected the type-10 half of this paragraph.** It used to say guild creation was
> refused because `CreateGuildFetchWork` runs `InputRestrictionHelper::CheckGuildName`
> (Arb_part_083.c:5846) first and the broker cannot invent a verdict for it. That check exists,
> but it runs entirely inside World: nothing of it crosses the link. Section 11 has the frames.

**The target-by-name problem.** `ContractPartyFetchWork` resolves the target from a **name** inside
`Param`/`FetchDataList`, and no capture contains a decoded one — the two live 0x2809 frames were
recorded as lengths, not bytes. `ContractBroker` therefore reads the target two ways and takes the
first that resolves: a u32 db id at the start of `FetchDataList` (the shape the AskList uses
everywhere else in the family), else a null-terminated UTF-16LE name in `Param`. Both are tried,
the miss is logged with the raw bytes, and `status/CAPTURE-PLAN.md` A.2.1 is the step that settles
it — one captured party invite decides which branch is real and the other can be deleted.

## 8. `C_ADD_TRADE_BAG` — the other half of the live report

Client opcode **58817 = 0xE5C1 = `C_ADD_TRADE_BAG`**, and it is the **Arbiter's**:
`Handler_C_ADD_TRADE_BAG` (Arb_part_040.c:17084), guard `param_3 < 0x34`, so min total **52** —
exactly the 52 bytes the live session saw World reject.

```
[0x04] 8 bytes   not read by the handler or the dumper
[0x0C] i64  TradeRequestor
[0x14] i64  TradeRequestee
[0x1C] i32  ContractId
[0x20] i32  TabIndex
[0x24] i32  InvenPos
[0x28] i32  MoveAmount
[0x2C] i64  Money
```

The handler does two things. First a trade-restriction check: if the item's template is restricted
(`RestrictionPeriodItemTradeDataSheet`), and the account is younger than the sheet's hour count, it
answers the client with system message `0x115f` carrying an `hour` parameter and stops. Then it
forwards the packet to World as **`AS_ADD_TRADE_BAG` (0x1637)**:

```
[0x06] u64  (PlanetId, the sender's character db id)   built by the Arbiter, not copied
[0x0E] u64  TradeRequestor
[0x16] u64  TradeRequestee
[0x1E] i32  ContractId
[0x22] i32  TabIndex
[0x26] i32  InvenPos
[0x2A] i32  MoveAmount
[0x2E] i64  Money
```
= 54 bytes.

So World's "handler has not been implemented yet" is correct behaviour on World's part and a bug on
ours: the client sends `C_ADD_TRADE_BAG` to the **Arbiter**, TeraSharp did not register it, and
`PacketDispatcher`'s forward-when-in-world fallback sent it to a server that only knows the `AS_`
form. T60 registers it, skips the restriction check (we have no
`RestrictionPeriodItemTradeDataSheet` and no account age), and forwards the `AS_` frame — which is
what the real Arbiter does for an unrestricted item.

`C_ADD_TRADE_BAG.1.def` exists and its field list matches the dumper exactly, but it starts at
[0x04] where the binary starts at [0x0C] and its header comment names a different opcode
(65204). The 8 unexplained bytes at [0x04..0x0B] are the same gap
`C_REPLY_THROUGH_ARBITER_CONTRACT` has. Read by absolute offset; do not use the def.

## 9. Open

- **The 24 bytes of `Param` / `FetchDataList`.** Section 7. One captured party invite settles it.
- **`ErrorNo` beyond 0 and 2.** Section 5 lists the branches; the enum has no name table.
- **`Reply`'s values.** The handler branches on `Reply == 1` taking a no-argument check and
  anything else taking a contract-index ownership check, which reads backwards for accept/reject.
  Recorded as observed; not relied on — `ContractBroker` passes `Reply` through untouched.
- **`ContractIndex` allocation.** Ours is a per-process counter. The real Arbiter's comes from
  `ContractManager` (`FUN_14091d340`) and may be reused after a contract ends.
- **Type 0x23** is refused, not implemented (section 7). Type 10 was, until T76 (section 11).
- **maxRestBonusXp and the rest-bonus datasheet** are not a contract question, but they came out
  of the same T76 pass - see `status/PERSISTENCE-MAP.md` and `RestBonusDataSheet::Load`
  (Arb_part_006.c:5206).
- **The 8 bytes at [0x04] of both client packets.** Unread by the binary, so unread by us.

---

## 10. T64 — the capture, and the two things it changed

`D:\packetlogs\cap_social.log` is a two-player tap of the **real** ArbiterServer. It contains one
complete party contract: "Test" (playerId **2**, the contractor) invites "two" (playerId **1002**,
the opponent). Sequence numbers below are that log's, as `cap_social_ctl.txt` numbers them.

| seq | dir | opcode | bytes |
|---|---|---|---|
| 650 | W→A | `SDB_FETCH_THROUGH_ARBITER_CONTRACT` 0x2809 | 44 |
| 652 | A→W | `DBS_ASK_THROUGH_ARBITER_CONTRACT` 0x280B | 52 |
| 653 | W→A | `SDB_ASK_THROUGH_ARBITER_CONTRACT` 0x280C | 27 |
| 655 | A→W | `DBS_FETCH_THROUGH_ARBITER_CONTRACT` 0x280A | 38 |
| 656 | W→A | `SDB_SEND_BEGIN_THROUGH_ARBITER_CONTRACT` 0x280D | 52 |
| 744 | A→W | `DBS_SEND_END` 0x280F + `DBS_REPLY` 0x2810 | 22 + 26 |

**Every layout in section 3 survived contact with the wire** — the writer-derived offsets, the
four-byte offset-only wstr refs in 0x280B, the `[u32 offset][u32 count]` bytes refs, and the
"empty list = offset is the frame length, count 0" convention. `T64_the_contract_replies_are_byte_exact`
rebuilds seq 652, 655, 744 and 744 from the builders and compares them byte for byte.

Two things did change.

### 10.1 The target is a NAME in `Param`, and `FetchDataList` is empty

Section 3.1 called this "the one guess in the file". Seq 650 settles it:

```
[06] Param offset          = 34            [0E] FetchDataList offset = 44 (= frame length)
[0A] Param count           = 10            [12] FetchDataList count  = 0
[16] ContractorDbId = 2    [1A] ContractType = 4    [1E] ContractId = 1
[22] Param                 = 74 00 77 00 6F 00 00 00 00 00      "two\0" + 2 bytes of padding
```

`ContractBroker.ReadTargetHint` now reads the **name first**; the db-id list is kept as a fallback
but can no longer shadow a name that is present, which is what it did before. The same `Param`
block comes back unchanged in the 0x280D at seq 656, alongside an `AskUserList` of exactly one
db id — so World echoes the Arbiter's own resolution rather than re-deriving it.

The two trailing zero bytes are not a second field we can name; the client's own
`C_REQUEST_CONTRACT` (client capture seq 475) carries the name with one trailing byte, so World
pads. `DecodeWString` stops at the terminator, so it does not matter — but a parser that trusted
`Param count` as a character count would be wrong by one.

### 10.2 The Arbiter asks **before** it answers

The order is `0x2809 → 0x280B → 0x280C → 0x280A`, and the gap between 653 and 655 is 0.4 ms: the
opponent's World is asked first, and the initiator's World hears nothing until `CanContract` is
in. TeraSharp used to send 0x280A immediately, with `ErrorNo 0`, and then record a
`CanContract = 0` that arrived afterwards and do nothing with it — telling the initiator a
contract was brokered before anyone had agreed to it.

`ContractBroker.OnFetch` now sends only the 0x280B fan-out and parks the contract;
`ContractBroker.OnAsk` sends the 0x280A, with the AskList on `CanContract = 1` and the
`ResponseFailure` empty form (offset = frame length, count 0) on 0. `T64_the_broker_asks_before_it_answers`
pins the order.

### 10.3 Two names the capture also fixed

* `SA_JOIN_PARTY_IN_ARBITER` (0x13AB, seq 747) carries **two names and no db ids** —
  `u32 MemberNameOff@06, u32 InviteeNameOff@0A, u8 Raid@0E`, frame 0x0F. That is why the contract
  in front of it has to have resolved both sides already.
* The brief that commissioned this pass had the two players the other way round. The capture is
  unambiguous: seq 652 pairs `ContractorName "Test"` with `ContractorDbId 2`, and
  `OpponentName "two"` with `OpponentDbId 1002`; the friend list in the client tap agrees.

---

## 11. T76 - guild creation is brokered after all

Live report: the founder clicked **Create** on the guild-name dialog and nothing happened, while
the party member never got an accept/decline popup. T60 had refused ContractType 10 on the
reasoning quoted in section 7. Two captures settle it.

### 11.1 What the founder sends

`cap_social3_client.log` is the **founder's** client (character `New`; frame 611 is an
`S_BEGIN_THROUGH_ARBITER_CONTRACT` naming `Test` as the requestor, so this client is the one being
invited by `Test` and the one that later creates the guild). Frame **2388** is the click:

```
2388 C->S 0x8853 C_REQUEST_CONTRACT len=174
  [08] u16 0x22   name offset   -> empty string
  [0C] u16 0x24   Param offset
  [0E] u16 0x8A   Param count = 138
  [10] i32 10     ContractType
  Param = "sdg" in a 138-byte block - the guild name typed in the dialog
```

The dialog itself is client-side; the server's only answer is `S_REPLY_REQUEST_CONTRACT` (frame
2390, `0A 00 00 00`) and then `S_ACCEPT_CONTRACT` (2396) and `S_CREATE_GUILD_RESULT` (2402).

### 11.2 What reaches the Arbiter

`cap_social2_ctl.txt` is the World<->Arbiter tap for a second guild create (guild `test`, founder
`New` db id 1003, one member `Test` db id 2). Between the click at 19:48:19.375 and the popup, the
ONLY frames on the link are the contract handshake - `C_REQUEST_CONTRACT` never crosses it:

| tap | dir | opcode | payload |
|---|---|---|---|
| 1181 | W->A | `SDB_FETCH` 0x2809 | ParamOff 0x22, ParamCount 0x8A, ListOff 0xAC, ListCount 4, contractor 1003, type 10, contractId 4; Param = `"test"` |
| 1182 | A->W | `DBS_ASK` 0x280B | nameOffs 0x22 / 0x2A, index 2, 1003, type 10, contractId 4, opponent 2, `"New"` `"Test"` |
| 1183 | W->A | `SDB_ASK` 0x280C | index 2, 1003, type 10, contractId 4, opponent 2, CanContract 1 |
| 1185 | A->W | `DBS_FETCH` 0x280A | listOff 0x22, listCount 4, 1003, type 10, contractId 4, index 2, ErrorNo 0, AskList [2] |
| 1186 | W->A | `SDB_SEND_BEGIN` 0x280D | ParamOff 0x26, ParamCount 0x8A, ListOff 0xB0, ListCount 4, 1003, type 10, contractId 4, index 2; Param = `"test"` |
| 1265 | A->W | `0x280F` + `0x2810` | after the member replies |

Frames 1182, 1185, 1265 are **byte-for-byte** what `BuildDbsAsk` / `BuildDbsFetch` /
`BuildDbsSendEnd` / `BuildDbsReply` already produced, and 1186 becomes
`cap_social2_client.log` frame **896**, a 168-byte `S_BEGIN_THROUGH_ARBITER_CONTRACT` that
`BuildSBegin("New", 10, 4, 2, param)` reproduces exactly. So the whole fix was one predicate.

### 11.3 Two things the type-10 frame does differently

1. **`Param` is the GUILD NAME, not a target name.** Feeding it to the by-name lookup finds
   nobody, so `ResolveOpponents` skips that route for type 10 and reads `FetchDataList` instead.
2. **`FetchDataList` is a party, not a single target.** Its count is a BYTE count (4 bytes = one
   db id here), and the 0x280B the real Arbiter sent names opponent 2 - the other member. A larger
   party is asked one 0x280B per member, and 0x280A is held back until every 0x280C is in
   (`Contract.Answered`); for a party invite, where there is exactly one opponent, that is the same
   behaviour T64 pinned.

### 11.4 Not settled

- The 138-byte `Param` block is a fixed-size buffer; only the leading null-terminated name is read
  here and the rest is passed through untouched to `S_BEGIN`.
- Whether the real Arbiter derives the opponent list from `FetchDataList` or from the contractor's
  party is not visible: the capture's list already holds exactly the one member. Reading the list
  is the narrower assumption and is what T76 implements.
