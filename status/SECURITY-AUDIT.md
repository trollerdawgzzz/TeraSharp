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
