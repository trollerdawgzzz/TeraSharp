# TERA v100 Arbiter — Known Bugs & What To Fix When Building From Scratch

---

## Bug #1: C_CHECK_VERSION — Pre-Auth Remote Crash

### What's Wrong

`C_CHECK_VERSION` is the very first packet a client sends — the version handshake,
before authentication. The handler reads a "module index" from the packet and uses
it to look up version info in two small static tables.

The bounds check only guards the upper side:

```c
iVar3 = *(int *)(packet + offset);       // module index — FROM PACKET, signed int
if (1 < iVar3) { reject; }               // ONLY rejects index > 1
// the index is then used twice unchecked: once to read a version out of a table of ints,
// and once to index a table of pointers whose entry is printed with %s
```

The index is a **signed int**. The check `if (1 < iVar3)` rejects values greater than
1, but a **negative** value (e.g. `0xFFFFFFFF` = -1) passes the check. The negative
index reads garbage from before the tables, then dereferences it as a wide-string
pointer for logging → **crash**.

### Why It's Dangerous

- **Pre-auth.** No login required. Anyone who opens a TCP socket can crash the arbiter.
- **Trivial to exploit.** Send a version-check packet with a negative module index.
- **Widest attack surface.** Reachable by any connection attempt.

### How We Found It

1. Started from the user's knowledge that `C_CHECK_VERSION` is the first packet
2. Decompiled the handler (`FUN_1404dd5a0`) in Ghidra
3. Identified `MOVSXD RDX, [RBX+0x4]` (sign-extends the index) followed by
   `CMP EDX, 0x2; JGE` (signed comparison, only guards upper bound)
4. Confirmed the tables are small (2 entries for versions, pointer table for names)
5. Confirmed negative index → reads before table → garbage pointer → crash

### The Fix (Binary)

Change the signed comparison to unsigned so negative values (huge unsigned) also
get rejected:

```
JGE (signed, 0F 8D) → JAE (unsigned, 0F 83)
```

Two sites (entry count check and module index check):

| Site | File offset | Byte to change | From → To |
|------|-------------|----------------|-----------|
| Entry count | `0x4dca4b` | 2nd byte of JGE | `8D` → `83` |
| Module index | `0x4dca58` | 2nd byte of JGE | `8D` → `83` |

Both `JGE` instructions are the near form (`0F 8D rel32`, 6 bytes). Only the second
byte changes. The `0F` prefix and the 4-byte relative offset remain the same.

### What To Do When Building From Scratch

When implementing `C_CHECK_VERSION`:
- Validate the module index as **unsigned** and check `index < MAX_MODULES` (currently 2)
- Or validate both bounds: `if (index < 0 || index >= 2) reject`
- Never use a signed comparison to bound an index from untrusted input

---

## Bug #2: C_VIEW_GUILD_WAR — Post-Auth Pagination OOB Read

### What's Wrong

The `ViewGuildWarHistory` action function computes a page offset for displaying
guild war history entries. It calculates:

```c
iVar8 = entry_count + page * (-10) + 9;
```

It checks `if (iVar8 < 0) break` (lower bound only), but **never checks the upper
bound** against the actual list size. With empty or small war history and `page: 0`,
`iVar8` exceeds the list size and the subsequent do-while loop reads past the end
of the array → **crash**.

### Why It's Dangerous

- Post-auth but requires only being in a guild (common)
- Crash script is one line: `mod.toServer('C_VIEW_GUILD_WAR', 1, { page: 0 })`
- Reliably crashes the arbiter

### How We Found It

1. User had an existing crash exploit script for this packet
2. Traced the handler (`FUN_1404f19d0`) → action function (`FUN_1406cfb00`)
3. Identified the pagination math: `entry_count + page*(-10) + 9` with only a
   lower-bound guard
4. Confirmed no upper-bound check against the list's actual size
5. Built a self-tested Ghidra detector for this pattern (ptrdiff list-size +
   lower-only guard + no upper bound)
6. The detector found C_VIEW_GUILD_WAR as the **only** instance of this bug pattern
   across all 273 client handlers

### The Fix (Proxy)

```javascript
mod.hook('C_VIEW_GUILD_WAR', 1, () => false);  // drop the packet
```

### What To Do When Building From Scratch

When implementing paginated list views:
- Always validate page index against BOTH bounds: `page >= 0 && page * pageSize < totalCount`
- The pattern to avoid: computing an offset and only checking `if (offset < 0)` without
  checking `if (offset >= listSize)`
- Apply this to ALL paginated handlers (guild war history, dungeon rankings, trade
  history, etc.)

---

## Bug #3: Freeze — Lock Held Across Synchronous DB Calls

### What's Wrong

15 functions hold an `EnterCriticalSection` lock while making synchronous database
stored-procedure calls. If the database stalls (deadlock, slow query, connection pool
exhaustion), the critical section is held indefinitely. Other threads waiting on the
same lock block → **entire arbiter freezes**.

### The Functions

All are admin/inter-server/scheduled operations, not player-initiated:

```
spUpdateStoreBuyLimitResetTime        spLoadAdminStoreBuyLimitOff
spUpdateWorldStoreBuyLimitInfo        spLoadWorldStoreBuyLimitInfo
spDeleteAdminStoreBuyLimitOff         spLoadStoreBuyLimitResetTime
spAddServantAdventureEventInfo        spDeleteServantAdventureEventInfo
spInsertAdminStoreBuyLimitOff         spLoadAllServantAdventureEventInfo
spDeleteAllServantAdventureEventInfo  spDeleteAdminStoreBuyLimitReset
spInsertAdminStoreBuyLimitReset       spLoadAdminStoreBuyLimitReset
```

### Why It Matters But Isn't An Attack

**None of these functions are reachable from client packets.** Verified by building
the complete call graph from all 273 client `C_` handlers (depth 4, covering 2262
reachable functions). None of the 15 lock-across-DB functions appear in that set.

This means the freeze is **operational** (triggered by DB performance problems), not
a client-triggerable attack. A player cannot send a packet that reaches these functions.

### How We Found It

1. Identified all functions containing both `EnterCriticalSection` and synchronous
   stored-procedure calls (database operations that block until complete)
2. Found 15 functions matching this pattern
3. Built the client-handler reachability set (all functions callable from any `C_` packet)
4. Cross-referenced: zero overlap between the lock-across-DB set and the client-reachable set
5. Conclusion: freeze is DB-operational, not client-triggered

### What To Do When Building From Scratch

- **Never hold a lock across a blocking I/O operation.** This is the core antipattern.
  Acquire lock → do DB call → release lock means any DB stall blocks all other threads
  waiting on that lock.
- **Use async DB operations** or release the lock before the DB call and re-acquire after
- **Set query timeouts** on all database connections so a stalled query returns an error
  instead of blocking forever
- **Connection pooling** with proper sizing to prevent pool exhaustion under load
- If you must lock across a DB call, use a timeout on the lock acquisition
  (`TryEnterCriticalSection` with a timeout, or `WaitForSingleObject` with a timeout)
  so threads don't block indefinitely

---

## Bug Classes That Were Clean

These were all systematically scanned with self-tested detectors (each detector was
verified to catch a known-positive before trusting its "no bugs found" result):

| Bug class | What was checked | Result |
|-----------|-----------------|--------|
| Pagination OOB (one-sided bound) | Every handler's action function for ptrdiff + lower-only guard | Only C_VIEW_GUILD_WAR |
| Direct packet-index OOB | Packet value used as array index without bounds check | Only C_CHECK_VERSION |
| Signed-index into static tables | Signed CMP + scaled memory access, no negative guard | Only C_CHECK_VERSION |
| Unbounded string scans | String scan/strlen without null-termination guarantee | None |
| Unclamped buffer copies | Copy size not validated against destination buffer | None (all allocate-to-size) |
| Lock-order deadlocks | Inconsistent critical-section acquisition ordering | None |
| Infinite waits from packets | `WaitForSingleObject(INFINITE)` reachable from client handler | None |
| Integer overflow in allocations | Packet count × element size used in allocation | None (all server-controlled or guarded) |
| Null-deref after lookup | Packet-supplied ID → lookup → deref without null check | None (all use default-return or server keys) |

### What This Means For Building From Scratch

The arbiter's packet parsing and handler code is actually **well-defended** against
most common bug classes. The two bugs found were specific lapses:

1. A signed comparison where unsigned was needed (C_CHECK_VERSION)
2. A missing upper-bound check in pagination math (C_VIEW_GUILD_WAR)

The codebase consistently:
- Validates packet lengths before reading fields
- Allocates buffers to the exact size needed (no fixed-size copies)
- Null-checks lookup results or returns default objects on miss
- Uses bounded string operations
- Has consistent lock ordering (no deadlock risk)

When building from scratch, the main things to get right:
- **All packet-derived indices must be unsigned-bounded on BOTH sides**
- **All pagination must check against actual collection size, not just negativity**
- **Never hold locks across blocking I/O** (the freeze pattern)
- **Pre-auth handlers deserve extra scrutiny** (C_CHECK_VERSION is the first thing
  an attacker can reach — it should be the most hardened, not the least)
