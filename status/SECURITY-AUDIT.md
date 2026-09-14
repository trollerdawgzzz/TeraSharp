# SECURITY-AUDIT — TeraSharp's own packet handling, audited against the real Arbiter's bug classes

**T48.** Method: `status/ARBITER-SECURITY-NOTES.md` is the reverse-engineering write-up of the real
ArbiterServer's bugs. Its three findings and its nine "clean" bug classes are used here as the
checklist, because the question worth answering is not "is TeraSharp perfect" but **"did we
reintroduce the things that were wrong with the thing we are replacing, and did we keep the things
it got right?"**

Answer, up front:

| | real Arbiter | TeraSharp before T48 |
|---|---|---|
| Bug #1 — pre-auth, `C_CHECK_VERSION` | signed table index, OOB read → crash | **a different bug, same packet**: unbounded array count → ~13 MB of allocation per 16-byte packet, pre-auth |
| Bug #2 — pagination, one-sided bound | `C_VIEW_GUILD_WAR` only | **`SendApplyListFor`**, and worse: the bound is defeated by an integer overflow |
| Bug #3 — lock across blocking I/O | 15 functions, none client-reachable | every `CharacterStore` method, all client-reachable — but SQLite-local (§6) |
| "Integer overflow in allocations — none" | clean | **not clean** (two sites) |
| "Unclamped buffer copies — none" | clean | clean |
| "Unbounded string scans — none" | clean | clean |
| "Null-deref after lookup — none" | clean | clean |

Four fixes landed in Cowork-editable files. Four findings are in human-owned files and are written
out as diffs in §5. The fuzz suite in §7 is what keeps this honest after today.

---

## 1. What was actually found

| # | Where | Class | Severity | Reachable | Owner | Status |
|---|---|---|---|---|---|---|
| **F1** | `Protocol/DefinitionReader.ReadArray` | allocate-by-count + unbounded element chain | **High** | **pre-auth** | Cowork | **fixed** |
| **F2** | `Handlers/GuildHandlers.SendApplyListFor` | pagination overflow → negative index → throw | **High** | post-auth, latent | Cowork | **fixed** |
| **F3** | `Persistence/CharacterStore.GetGuildLog` | pagination overflow → negative SQL OFFSET | Medium | post-auth, latent | Cowork | **fixed** |
| **F4** | `World/ChatManager.ParseCCreatePrivateChannel` | allocate-by-count + no cycle guard | Low | post-auth, latent | Cowork | **fixed** |
| **H1** | `World/WorldBridge` tunnel unwrap (0x13F7) | signed length + overflow in the bound → link kill | **High** | World link | human | §5.1 |
| **H2** | `World/WorldBridge.ReceiveLoop` | no per-frame try/catch → one bad frame drops every player | **High** | World link | human | §5.2 |
| **H3** | `Network/PacketReader` | dead code; `ReadOffsetString` is off by 4 | Info | — | human | §5.3 |
| **H4** | `Network/GameSession` / `Program` | `Program.Store` has a private setter, so the fuzz cannot inject a store | Low | — | human | §5.4 |

"Latent" means the handler is written but `HandlerRegistry` does not register it yet — F2 and F3
go live the moment the T39 guild wiring diff is applied, and F4 when the T43 chat one is.

---

## 2. F1 — the pre-auth one, in detail

### The packet

`C_CHECK_VERSION` is the **first** handler `HandlerRegistry` registers, it runs **before
authentication**, and its `MinBodyLength` is **4**. Its def is an array:

```
array   version
- int32 index
- int32 value
```

### The code

`DefinitionReader.ReadArray` took the element count straight from the packet's `[u16 count]` header:

```csharp
var list = new List<object>(count > 0 ? count : 0);   // <- allocate-by-count
...
for (int i = 0; i < count && elemBody >= 0; i++)
{
    ...
    elemBody = ToBody(next);                          // <- next is also from the packet
}
```

Three separate problems in five lines:

1. **`new List<object>(count)`** — a u16 count means up to 65535, so a **four-byte body** with
   `FF FF 00 00` allocates a 65535-slot list (512 KB on x64) before the offset is even looked at.
2. **No cycle detection on the element chain.** `next` is packet-supplied. Point it at the element's
   own offset and the loop runs the full `count` times over the same eight bytes.
3. **A `ReadRecord` per element** — a `Dictionary<string, object>` each, plus a string scan per
   string field, which is O(body) each.

### Measured

Transliterated the reader and ran it (`scratchpad/t48/reader.py`, `verify_fix.py`):

| body | elements walked | `ReadRecord` calls | list capacity |
|---|---|---|---|
| `FF FF 00 00` (4 B) | 0 | 1 | **65535** |
| 16 B, `next` → itself | **65535** | **65536** | 65535 |

The 16-byte one is ~13 MB of allocation per packet at ~200 B per dictionary — an **850,000×
amplification**, pre-auth, repeatable as fast as a socket can write. That is a GC-death DoS, not a
crash, which is why nothing had noticed it.

Nesting would make it exponential rather than linear, but no client def we read has a nested
array, so the linear case is what is actually reachable — worth saying precisely rather than
overclaiming.

### The fix

Three defences, because each covers a gap the others do not:

```csharp
var list = new List<object>();                  // never pre-size from the count
int maxByBody = _body.Length / 4;               // an element is >= its own here+next
if (count > maxByBody) count = maxByBody;
...
if (_elementBudget <= 0) break;                 // shared across every array in the packet
_elementBudget--;
seen ??= new HashSet<int>();
if (!seen.Add(elemBody)) break;                 // the chain looped back
```

`MaxElementsPerPacket = 4096` — far above the largest client array we answer (a 24-entry
private-channel invite list), far below anything that hurts. Truncation is silent rather than
throwing, which matches every other malformed-packet path in the reader.

After: the same two packets yield 0 and 1 elements. A real two-entry `C_CHECK_VERSION` parses
byte-identically (`Reader_rejects_a_hostile_array_count` asserts all four cases).

---

## 3. F2/F3 — pagination, the bug class the notes warned about

`ARBITER-SECURITY-NOTES.md` bug #2: *"always validate page index against BOTH bounds:
`page >= 0 && page * pageSize < totalCount`"*. TeraSharp had the first half:

```csharp
if (page < 1) page = 1;
for (int i = (page - 1) * pageSize; i < all.Count && i < page * pageSize; i++)
    var row = all[i];
```

With `pageSize = 13` (`GuildPackets.ApplyListPageSize`) and `page = 165191051`:

```
(page - 1) * 13 = 2147483650  ->  wraps to  -2147483646
 page      * 13 = 2147483663  ->  wraps to  -2147483633
```

`-2147483646 < all.Count` ✓ and `-2147483646 < -2147483633` ✓, so the loop **runs** and evaluates
`all[-2147483646]` → `ArgumentOutOfRangeException`. The lower-bound clamp is not wrong, it is
simply upstream of an overflow that recreates the thing it was clamping.

`CharacterStore.GetGuildLog` has the same shape with `pageSize = 0x14`: `page = int.MaxValue` gives
`OFFSET -40`, which SQLite silently treats as 0 — no crash, but the client asking for page
2147483647 is served page 1.

### The fix

One helper, used before any multiply:

```csharp
public static int ClampPage(int page, int totalPages)
{
    if (totalPages < 1) totalPages = 1;
    if (page < 1) return 1;
    return page > totalPages ? totalPages : page;
}
```

applied in `SendApplyListFor` and `SendHistory`, plus `CharacterStore.MaxPageNumber = 1_000_000` as
a second line of defence in the store itself, because the store is called from more than one place.
`Pagination_clamps_before_it_multiplies` pins the exact hostile value and checks that
`page * pageSize` cannot overflow for any clamped page at any page size this server uses.

### Every paginated path, checked

| path | page source | before | after |
|---|---|---|---|
| `C_GUILD_APPLY_LIST_PAGE` → `SendApplyListFor` | `[i32]` | **negative index** | clamped to `totalPages` |
| `C_GET_GUILD_HISTORY` → `SendHistory` → `GetGuildLog` | `[i32]` | negative OFFSET | clamped twice |
| `C_GUILD_APPLY_LIST` → `SendApplyList` | always page 1 | safe | unchanged |
| `SDB_LIST_PARCEL` → `BuildParcelList` (T45) | `[u32 CurPage]` | echoed, never multiplied | safe |
| guild war history | — | **not implemented** | n/a |
| broker, rankings | — | **not implemented** | n/a |

The last two rows matter for later: `C_VIEW_GUILD_WAR` is the *one* packet the notes' detector
flagged in the whole real Arbiter, and TeraSharp does not implement it yet. When it is,
`ClampPage` is the thing to use.

---

## 4. The per-handler table

Every registered `C_` handler and every hand-written parser. "Packet-derived" lists values used as
an index, count, offset or length — the only kind that can go wrong this way. Everything unlisted
reads fixed-position scalars that the reader's own `Need()` already bounds.

### 4.1 Client packets — the def codec path

These go through `GameSession.ReadByDef` → `DefinitionReader`, so their bounds are the reader's.

| packet | packet-derived | checked | note |
|---|---|---|---|
| `C_CHECK_VERSION` | `version` array count + element chain | **F1, now yes** | pre-auth |
| `C_LOGIN_ARBITER` | `name` offset, `ticket` offset+count | yes (`ToBody`, `ReadBytesAt`) | pre-auth |
| `C_SELECT_USER` | — | — | two fixed scalars |
| `C_CREATE_USER` | `name` offset; `appearance`/`details`/`shape` byte counts | yes | `ReadBytesAt` throws past the end; `ReadBlock` re-clamps to the fixed size |
| `C_DELETE_USER`, `C_CHECK_USERNAME` | string offsets | yes | |
| `C_CHAT` | `message` offset | yes | |
| `C_WHISPER` | two string offsets | yes | |
| `C_ADD_FRIEND`, `C_ACCEPT_FRIEND`, `C_DELETE_FRIEND`, `C_BLOCK_USER`, `C_REMOVE_BLOCKED_USER` | string offsets | yes | |
| `C_ADD_FRIEND_GROUP`, `C_EDIT_FRIEND_GROUP`, `C_DELETE_FRIEND_GROUP` | group index `[i32]` | **yes** — `MinGroupIndex..MaxGroupIndex` (2..10), the real `User::UpdateFriendGroup` range | |
| `C_CHANGE_FRIEND_MEMO`, `C_EDIT_BLOCKED_USER_MEMO` | string offsets | yes | memo length is not capped — §6 |
| `C_PARCEL_REPORT` | `[u32 parcelId]` | id, not an index | ownership-checked |

### 4.2 Client packets — hand-written parsers

| parser | packet-derived | checked | note |
|---|---|---|---|
| `ClientSettingsHandlers.ParseSettingBlob` | blob `offset` + `count` | **yes, exemplary** | `start > body.Length \|\| count > body.Length - start`, no overflow possible (both u16) |
| `CharacterHandlers.ReadBlock` | `offset`, `count` | yes | `offset + count > packet.Length`; both u16 so no overflow |
| `CharacterHandlers.ReadWideString` | offset | yes | `offset >= packet.Length`, terminator-bounded scan |
| `ParcelHandlers.OnShowParcelMessage` | `[u32 parcelId]` | id, not an index | |
| `ArbiterClientHandlers.ParseTooltipRequest` (T45) | `ItemDbId`, `ItemOwnerDbId` | ids, not indices | length-guarded first |
| `ArbiterClientHandlers.OnVisitNewSection` (T45) | `guardId` | **yes** — `(uint)guardId >= 0x40`, unsigned so negatives are caught | matches the real handler's `uVar4 < 0x40` |
| `ArbiterClientHandlers.ExtractWideRuns` (T45) | — | bounded by `body.Length` | |
| `ChatPackets.ParseCCreatePrivateChannel` | `count`, `offset`, element chain | **F4, now yes** | |
| `ChatPackets.ParseCWhisper/CChat/CJoin/CLeave/CKick/CChpw/CInfo` | string offsets, `slot` | yes | `ReadWString` clamps; slot range-checked at every use (see below) |
| `GuildPackets.ParseC*` (12) | string offsets, `[i32]` ids and pages | offsets yes, **page F2** | |
| `PartyPackets.ParseC*` | string offsets, ticket ids | yes | |

### 4.3 Collection indices from packets

The crash class. Every site, and what bounds it:

| site | index | bound |
|---|---|---|
| `ChatManager.Chat` → `SlotsOf(sender)[slot]` | `C_CHAT.Type - 0x0B` | `IsPrivateChatType` → 0x0B..0x12 → slot 0..7 ✓ |
| `ChatManager.LeaveChannelPacket` | `C_LEAVE_PRIVATE_CHANNEL.Index` | `slot < 0 \|\| slot >= 8` ✓ |
| `ChatManager.KickMember` | `C_KICK_CHANNEL_MEMBER.Index` | same, short-circuited before the index ✓ |
| `ChatManager.ChangePassword` | `C_CHANGE_CHANNEL_PASSWORD.Index` | same ✓ |
| `ChatManager.LeaveChannel` (private) | `slot` | no guard of its own, but all three callers guard — a defensive `if ((uint)slot >= MaxChannelsPerUser) return false;` would cost nothing |
| `GuildHandlers.SendApplyListFor` → `all[i]` | derived from `page` | **F2** |
| `SocialHandlers` group index | `[i32]` | 2..10 ✓ |

### 4.4 World side — `SDB_`/`SA_` parsers

| parser | packet-derived | checked |
|---|---|---|
| `WarehouseHandlers.TrySliceAtoms` | binary ref `offset`, `bytes` | **yes** — `bytes > 0 && start >= minStart && bytes % 0x358 == 0 && start <= payload.Length - bytes`. Note both are `(int)`-cast u32s: negatives fall out at `bytes > 0` / `start >= minStart`, and `(int)0x80000000 - 6` wrapping to a large positive is caught by the final range check. Safe, but see §6 |
| `WarehouseHandlers.ParseAtoms` | element count = `bytes / 0x358` | bounded by the slice ✓ |
| `ParcelDbHandlers.Ref` (T45) | `frameOff`, `count` | **yes** — `long start = (long)frameOff - 6; start + count > p.Length` in 64-bit, allocation only after the check ✓ |
| `ParcelDbHandlers.U32/U8` | any offset | return 0 past the end ✓ |
| `DbProxyHandlers.ParseTimeline` | `count`, `start` | **exemplary** — `count < 0 \|\| count > MaxTimelineNodes \|\| start < 0 \|\| (long)start + (long)count * TimelineNodeSize > payload.Length` |
| `DbProxyHandlers.ParseBurst` | TSIS `recordCount`, per-record `len` | `count > 4096` reject; `len > (uint)(bytes.Length - o)` reject ✓ — and it is a local file, not packet data |
| `DbProxyHandlers.DeclaredAtomCount` | length field | used only for a log comparison, never for an allocation ✓ |
| `DbProxyHandlers` — the other ~120 `BitConverter.To*(payload, N)` | fixed offsets | **each throws past the end**, which is fine in itself and catastrophic in context — see **H2** |
| `WorldBridge` 0x13F7 tunnel unwrap | `clientLen` | **H1 — no** |

---

## 5. Human-owned findings

### 5.1 H1 — the tunnel unwrap trusts a signed length (`World/WorldBridge.cs`, ~line 288)

```csharp
int clientLen = BitConverter.ToInt32(payload, 12);
if (32 + clientLen > payload.Length) clientLen = payload.Length - 32;
var clientPkt = new byte[clientLen];
```

Two ways through:

- **`clientLen` negative** (`0xFFFFFFFF` → −1): `32 + (−1) = 31`, and `31 > payload.Length` is false
  because the payload is at least 32 bytes — so the clamp does not fire and `new byte[-1]` throws.
- **`clientLen` huge** (`0x7FFFFFFF`): `32 + 0x7FFFFFFF` **overflows** to negative, the clamp again
  does not fire, and `new byte[0x7FFFFFFF]` throws `OutOfMemoryException`.

Either one unwinds into H2 and takes the World link down. 0x13F7 is the tunnel — the
highest-volume frame on the link — so this is the most-executed unchecked length in the codebase.

```csharp
                int clientLen = BitConverter.ToInt32(payload, 12);
                // T48: clientLen is a signed i32 off the link. The old bound was
                // `32 + clientLen > payload.Length`, which misses a negative clientLen entirely
                // (31 > 32 is false) and overflows for a large one. Compare on the safe side.
                if (clientLen < 0 || clientLen > payload.Length - 32) clientLen = payload.Length - 32;
                var clientPkt = new byte[clientLen];
```

(`payload.Length >= 32` is already guaranteed by the guard above it, so the right-hand side cannot
go negative.)

### 5.2 H2 — one bad frame closes the World link (`World/WorldBridge.cs`, `WorldLink.ReceiveLoop`)

```csharp
_bridge.HandleFrame(this, op, payload);     // no try/catch
...
catch (Exception ex) { _log.LogWarning("Link #{Id} error: {Msg}", Id, ex.Message); }
finally { try { _sock.Close(); } catch { } }
```

There is no per-frame guard, so **any** exception from **any** `SDB_`/`SA_` handler unwinds to the
loop's outer catch, which falls straight into `finally { _sock.Close(); }` — **every player
disconnects**. `DbProxyHandlers` contains ~120 raw `BitConverter.To*(payload, N)` calls, each of
which throws on a short payload; a single truncated frame is enough.

This is the same failure mode as the DLM head-block in `status/HANDOFF.md` §1, reached a different
way, and it is why `Fuzz_dbproxy_dispatch_survives_hostile_frames` exists.

```csharp
                    ushort op = BitConverter.ToUInt16(_rx, pos + 4);
                    var payload = new byte[len - 6];
                    Array.Copy(_rx, pos + 6, payload, 0, payload.Length);
                    // T48: a throwing handler must not take the link down with it. Without this,
                    // one malformed frame unwinds to the outer catch and the finally closes the
                    // socket - every player on this World drops at once.
                    try { _bridge.HandleFrame(this, op, payload); }
                    catch (Exception ex)
                    {
                        _log.LogError(ex, "Link #{Id}: handler for 0x{Op:X4} ({Len} B) threw - frame dropped",
                            Id, op, payload.Length);
                    }
                    pos += len;
```

Dropping the frame is not free — an unanswered per-user DB request head-blocks that one user — but
it is bounded to one user instead of all of them, and the `LogError` is the thing that makes it
findable.

### 5.3 H3 — `Network/PacketReader.cs` is dead code, and wrong (`Network/PacketReader.cs`)

Its own header calls it *"the single most important defensive component in the server"*. Nothing
calls it: every handler uses `ReadByDef` or hand-written `BitConverter` reads. Two consequences:

- The bounds-checking it provides is not actually in the request path anywhere.
- `ReadOffsetString` treats the offset it reads as **body-relative**, but every TERA string offset
  on the wire is **packet-relative** (`DefinitionReader.ToBody` subtracts 4, `ChatPackets.ReadWString`
  subtracts 4, `CharacterHandlers` subtracts 4). Whoever wires this up next gets strings four bytes
  early, silently.

Either delete it or fix the offset convention and route the hand-written parsers through it. The
latter is the better end state — it is the one place bounds checking could be centralised — but it
is a refactor, not an audit fix.

### 5.4 H4 — `Program.Store` cannot be injected (`Program.cs`)

`public static CharacterStore? Store { get; private set; }`. The client fuzz therefore runs with a
null store, so handlers that early-out on `store == null` are only shallowly covered.
`internal set` (the test project already has `InternalsVisibleTo`) would let
`Fuzz_client_dispatch_survives_hostile_bodies` point it at a scratch store and cover the full
handler bodies. Same for `Program.World`.

---

## 6. Hardening, not bugs

- **`TrySliceAtoms`'s `(int)` casts.** Safe today only because the final range check catches what
  the casts let through. `uint`/`long` throughout would make it safe by construction rather than by
  argument.
- **`ChatManager.LeaveChannel`** takes an unvalidated `slot`; all callers validate. One
  `if ((uint)slot >= MaxChannelsPerUser) return false;` removes the dependency on that staying true.
- **Blob and memo sizes are uncapped.** `C_SAVE_CLIENT_USER_SETTING` stores up to ~64 KB per
  character and `C_CHANGE_FRIEND_MEMO` whatever fits in a packet; both are per-row overwrites, so
  this is disk growth rather than a leak, but a cap (the real server's `MaxGuildIntroduce` style)
  costs nothing.
- **Bug #3, the lock-across-I/O pattern.** Every `CharacterStore` method is
  `lock (_lock) { ...SQLite... }` and every one of them *is* client-reachable — the opposite of the
  real Arbiter, where none of the 15 were. It is much less dangerous here (SQLite is in-process,
  no pool, no network) but it is the same shape: a slow query blocks every session, because
  handlers run on the socket thread. Worth knowing before the store ever moves off SQLite.
- **`GameSession.AppendToBuffer`** doubles without an explicit ceiling. Bounded in practice: a
  packet declares its own u16 size, so at most ~64 KB can sit unparsed. Not a finding, but the
  ceiling is implicit and would be worth asserting.

---

## 7. The fuzz suite

Six tests, in `src/TeraSharp.Arbiter.Tests/Program.cs`.

**What "no exception escapes" means here.** `PacketDispatcher` already catches everything, so
asserting that nothing reaches the caller proves nothing. What the client fuzz asserts is that the
dispatcher never took its `catch (Exception)` branch — the one that logs at **Error**. A
`PacketReadException` is the codec correctly saying "malformed"; an `IndexOutOfRange`, `Overflow`,
`ArgumentOutOfRange` or `NullReference` is a finding. A capturing `ILogger` makes that distinction
observable, which is the whole trick.

| test | what it drives | bodies |
|---|---|---|
| `Fuzz_client_dispatch_survives_hostile_bodies` | the real `PacketDispatcher` + the real `HandlerRegistry` set, on a `GameSession` over an unconnected socket | 200+ × every registered `C_` opcode |
| `Fuzz_subsystem_parsers_survive_hostile_bodies` | `GuildHandlers` / `ChatManager` / `PartyManager` `OnClientPacket` against a scratch store | 200+ × every opcode each answers |
| `Fuzz_dbproxy_dispatch_survives_hostile_frames` | `DbProxyHandlers.TryHandle` for every allow-listed `SDB_` | 200+ × ~40 opcodes |
| `Reader_rejects_a_hostile_array_count` | F1, all four cases including the unchanged good packet | — |
| `Pagination_clamps_before_it_multiplies` | F2/F3, including the exact hostile page number | — |
| `Chat_invite_list_parser_bounds_a_hostile_count` | F4 | — |

The body generator is seeded per opcode, so a failure reproduces from the message alone. Its shapes
are the ones that actually found things: all-zero and all-`0xFF` at every length, `count`/`offset`
extremes in the first four u16 slots (where every array and bytes header lives), i32 extremes
including `165191051` at each of the first four i32 slots, and random bodies at three length
scales. Zero-length is included deliberately.

Each store-backed test ends by reading the store back, so a fuzzed write that half-committed shows
up as a failure rather than as silence.

**No build was run** — there is no `dotnet` in the Cowork container. F1 was verified by
transliterating `DefinitionReader` into Python and running the attack packets against both the old
and new logic (`scratchpad/t48/reader.py`, `verify_fix.py`); F2/F3's overflow was verified by
reproducing the exact wrap arithmetic. **The fuzz suite is the real check, and it only runs when
the human builds.**

---

## 8. Open

- **The client fuzz runs with a null `Program.Store`** until H4 is applied, so the deep half of
  several handlers is uncovered. This is the single highest-value follow-up.
- **`C_VIEW_GUILD_WAR` is not implemented.** It is the one packet the notes' detector flagged in
  the entire real Arbiter. When it is written, `GuildHandlers.ClampPage` is what it must use.
- **Broker and rankings** are not implemented; same note.
- **Nested arrays** in the def reader are bounded by the shared budget but never exercised, because
  no client def we read has one. If one ever appears, the budget is the only thing between it and
  `count^depth`.
- **The `PacketReader` decision** (H3) — delete or adopt — is a judgement call, not an audit finding.

---

## 9. T50 — the suite was run for real, and what came out of it

T48 wrote the fuzz suite without being able to run it. The human ran it. Two tests failed:

```
FAIL Fuzz_client_dispatch_survives_hostile_bodies: 126 handler error(s) out of 20400 hostile
     packets across 68 opcodes
FAIL Fuzz_dbproxy_dispatch_survives_hostile_frames: 21 DB-proxy failure(s) over 10387 hostile
     payloads
PASS Fuzz_subsystem_parsers_survive_hostile_bodies                  (9600 bodies, clean)
```

147 lines of output, **three** findings. The report printed its first 20 lines and all 20 were the
same one, which is the first thing fixed below.

### 9.0 The report itself

`FuzzSummary` collapses the failures to one line per distinct `(opcode, exception type)` pair with
a count and one sample, sorted most-frequent-first, and the DB-proxy test no longer stops at the
21st failure. That cap was not just a display limit: `ops` is walked in ascending opcode order, so
breaking out at failure 21 stopped the run inside `0x27FA` and **23 of the 58 allow-listed opcodes
were never fuzzed at all** — everything above `0x27FA`. They are audited by hand in 9.4 below, but
the next run is the first time they are actually exercised.

Registration-time errors are also separated out now. `HandlerRegistry.RegisterAll` logs at Error
for any name missing from the opcode map, the capturing factory collected that, and it counted as
a "handler error": `C_CHECK_ALIVE` is not in build 376012's map (the client never sends it), and
that one line was the 126th error. It is printed as a note before the fuzz starts.

### 9.1 Finding 1 — `Convert.ToInt32` on a `uint32` def field (125 of the 126)

`C_DELETE_USER.1` is one field, `uint32 id`. `DefinitionReader.ReadPrimitive` returns `FieldKind`
`UInt32` as a boxed **`uint`**, and `Convert.ToInt32(uint)` throws `OverflowException` for every
value at or above `0x80000000` — half of all four-byte values. `PacketDispatcher` catches it in its
`catch (Exception)` arm and logs at **Error**, which is what the test counts.

Reproduced exactly: porting `FuzzBodies` and .NET's `Random` to Python and counting the bodies
whose first four bytes are >= `0x80000000` gives **125**, and 125 + the `C_CHECK_ALIVE`
registration line = 126. The whole failure was two lines of code.

This is not a malformed packet, it is a large id, and the real handler does not refuse it:
`Handler_C_DELETE_USER` copies the DWORD into a signed slot and compares it against the account's
character ids, where `0xFFFFFFFF` matches nothing and the reply is `success = 0`.

**Fix.** `Protocol/DefinitionReader.cs` gains `DefField` — total narrowing helpers (`I32`, `U32`,
`I64`, `U64`, `Bool`, `Str`, `List`, plus dictionary overloads) that reinterpret the bits instead
of range-checking them, clamp floats, and never throw for any input of any type.

| Call site | Def type | Was | Now |
|---|---|---|---|
| `CharacterHandlers.OnDeleteUser` | `uint32 id` | `Convert.ToInt32` | `DefField.I32` |
| `CharacterHandlers.FromFields` x7 | mixed | `Convert.To*` | `DefField.*` |
| `SocialHandlers.I32` helper | used by 5 handlers | `Convert.ToInt32` | `DefField.I32` |
| `SocialHandlers.OnAddFriendGroup` | `array<uint32> friends` | `Convert.ToInt32(raw)` | `DefField.I32(raw)` |
| `SocialHandlers.OnEditFriendGroup` | `{uint32 playerId, uint32 id}` | `Convert.ToInt32(e["playerId"])` | `DefField.I32(e, ...)` |

The five `SocialHandlers` paths never appeared in the fuzz output for one reason only: the fuzz
runs with `Program.Store == null` (H4 is still open), and every one of them returns before the
conversion. They are reachable in a live session from `C_ADD_FRIEND_GROUP`,
`C_EDIT_FRIEND_GROUP`, `C_DELETE_FRIEND_GROUP`, `C_CHANGE_FRIEND_MEMO` and
`C_EDIT_BLOCKED_USER_MEMO`. The `e["playerId"]` indexer was a second bug in the same line — a
`KeyNotFoundException` for any element the reader could not fill.

`LoginHandlers` (human-owned) was checked and needs nothing: `language` is `uint32` read with
`Convert.ToUInt32`, `patchVersion` and `C_SELECT_USER.id` are `int32` read with `Convert.ToInt32`.
`ChatHandlers` and `ParcelHandlers` are clean for the same reason.

### 9.2 Finding 2 — `off + 16` wraps in `BuildLearnAllCrest` (2 of the 21)

`SA_LEARN_ALL_CREST_ACQUIRABLE` (0x1463) walks a chain of 16-byte entries at a frame-relative
offset the packet supplies. The bound was `off + 16 <= req.Length` in **int** arithmetic, so a
`listOff` of `int.MaxValue` — and `int.MinValue`, which wraps to the same place — made the sum
negative, the bound passed, and `BitConverter.ToInt32(req, off + 8)` threw
`ArgumentOutOfRangeException`. Simulated over the 300 bodies for that opcode: exactly 2, which is
exactly what the run reported.

Out of a DB-proxy handler that is not a dropped packet. `WorldLink.ReceiveLoop` has no per-frame
catch (H2), so one such frame closes the World link and disconnects every player at once.

**Fix.** The offsets are `long` (they are unsigned on the wire, so they are read as `uint` and
widened — the old `(int)` cast happened to be safe for `0xFFFFFFFF` but for the wrong reason), and
the walk keeps a visited set so a `next` that points at itself ends the chain instead of running
to the 512 guard. The dead `uint count` local went with it.

### 9.3 Finding 3 — foreign keys are enforced, and the schema assumed they were not (19 of the 21)

Every `0x27FA` failure was `SQLite Error 19: FOREIGN KEY constraint failed` — a
`SDB_UPDATE_USER_ACHIEVEMENT` whose payload named a player id with no `characters` row.

The comment above the guild tables says the REFERENCES clauses are "documentation, not
enforcement: this DB never sets PRAGMA foreign_keys". **That is wrong.**
`Microsoft.Data.Sqlite` issues `PRAGMA foreign_keys = 1` on open unless the connection string says
otherwise, and the connection string here is `Data Source={path}` and nothing else. Fifteen
columns across thirteen tables reference `characters(id)`, and every INSERT into one of them can
throw.

**Fix.** `CharacterStore` gains `CharacterExists(long)` and a private `NoSuchOwner(what, ids...)`
guard, and every per-character INSERT calls it first and drops the write with a warning:

`UpsertQuest`, `SaveAchievements`, `AddAccomplishedAchievements`, `AddTutorialTip`,
`SetSerenGuide`, `UpsertDungeonCoolTime`, `SetDungeonClearCount`, `UpsertReputation`,
`UpsertFriend`, `AddFriend`, `UpsertFriendGroup`, `AddBlock`, `AddGuildMember`,
`InsertGuildApply`, `AddGuildInvite`.

`AddFriend`, `UpsertFriend` and `AddBlock` check **both** ids — both columns are foreign keys.
Pure-UPDATE writers (`SetFriendMemo`, `SetFriendGroup`, `SetBlockMemo`, `DeleteFriendGroup`,
`SetGuildMemberGroup`, `UpdateGuildMember*`, `AddGuildContribution`, `DeleteGuildGroup`,
`ClearDungeonCoolTime`) are left alone: an UPDATE that does not set a foreign-key column cannot
violate one, and matching no row is already the right answer.

Dropping rather than throwing is what the caller needs. `OnSaveUserAchievement` sends its
`DBS_SAVE_27FB` ack whether or not the store took the write, and it has to: an unanswered per-user
DB item head-blocks that user's whole World queue for the life of the World process
(`status/HANDOFF.md` section 1).

**Note for the human, not a T50 fix:** if foreign keys are enforced, then every DELETE that leaves
children behind can fail the same way. `DeleteGuild` already deletes its children explicitly, but
`DeleteCharacter` is worth a look — a character with quests, achievements, friends or a guild row
is the normal case, not the edge case.

### 9.4 The 23 opcodes the aborted run never reached

Audited by hand, since the next run is the first that will actually fuzz them.

| Opcode | Handler | Verdict |
|---|---|---|
| 0x2802 `SDB_ACCOMPLISH_USER_ACHIEVEMENT` | `OnAccomplishUserAchievement` | `SliceAchievementRecords` rejects a wrapped `start` via `start > request.Length`; store write now guarded |
| 0x286E `SDB_ADD_TUTORIAL_SIMPLE_TIP` | `OnAddTutorialTip` | length-guarded; store write now guarded |
| 0x2891 `SDB_UPDATE_REPUTATION_INFO` | `OnUpdateReputation` | length-guarded; store write now guarded |
| 0x2944 `SDB_UPDATE_SEREN_GUIDE_INFO` | `OnUpdateSerenGuide` | length-guarded; store write now guarded |
| 0x2811 `SDB_DELETE_PARCEL` | `OnDeleteParcel` | `ParcelDbHandlers.Ref`/`U32` are bounds-safe; DELETE cannot break a foreign key |
| 0x283F `SDB_INCREASE_WAREHOUSE_SIZE` | `OnIncreaseWarehouseSize` | `WhU32` is bounds-safe; no `characters` foreign key on the warehouse rows |
| 0x2867, 0x2869, 0x2872, 0x288F, 0x2908, 0x290C, 0x2930, 0x2942 | loads | every read is behind a `payload.Length >=` check |
| 0x2897, 0x2899, 0x2924, 0x2936, 0x293C, 0x293E, 0x297B | acks | `BuildReqIdAck` / `BuildOkReqId` / `BuildDbs2937` all bound the reqId read themselves |
| 0x295C `SDB_RESULT_CITY_WAR` | `OnResultCityWar` | guarded by `payload.Length < CityWarResultOffset + 4` |

No further finding, but that is a reading, not a run.

### 9.5 What the next run should say

- `Fuzz_client_dispatch_survives_hostile_bodies` — pass, with one `(registration: ...)` note for
  `C_CHECK_ALIVE`.
- `Fuzz_dbproxy_dispatch_survives_hostile_frames` — pass, and this time over all 58 opcodes
  (roughly 17400 payloads rather than 10387).
- `Fuzz_subsystem_parsers_survive_hostile_bodies` — still pass.

If anything new appears, it will be in the 23 opcodes above and the summary now names it in one
line.

### 9.6 Still human-owned

- **H2** — `WorldLink.ReceiveLoop` still has no per-frame `try/catch`. The human is adding it.
  Findings 2 and 3 are each a total disconnect *because* of H2; the per-frame catch is what turns
  the next one into a dropped frame.
- **H4** — `Program.Store` still has a private setter, so the client fuzz runs against a null
  store and the deep half of `SocialHandlers`, `ParcelHandlers` and `CharacterHandlers` is still
  uncovered. The five `Convert.ToInt32` sites in 9.1 are fixed, but they were found by reading, not
  by the suite, and the suite still cannot reach them.
- **`C_CHECK_ALIVE` is registered but not in map 376012.** `HandlerRegistry` logs an Error for it
  at every startup. Either drop the registration or let `Reg` log at Warning for a name the map
  does not have — both are one line in a human-owned file.
