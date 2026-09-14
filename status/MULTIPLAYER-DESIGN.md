# MULTIPLAYER-DESIGN — how the real Arbiter routes two players, and the minimal change to match it

T27, research only. Nothing in this document is implemented. `World/WorldBridge.cs`,
`Network/GameSession.cs` and `Handlers/WorldEntry.cs` are human-owned; §6 gives the exact
diffs rather than editing them.

Sources: the decompiled `ArbiterServer.exe` (`Arb_part_000.c` … `Arb_part_095.c`), the four
Arbiter↔World taps in `D:\packetlogs\`, and `ServerConfig.xml`. Every claim carries a
`FUN_…` / `file:line`. `WorldServer.exe.c` is a single 125 MB file and could not be staged
into the research container, so every "what World expects" answer below is derived from the
Arbiter's side of the contract and is flagged where that matters.

---

## 0. The one-line answer

**A tunnel frame is addressed by `Ticket`, an index into a flat `ClientSession*` table owned
by `PacketBypassManager`.** The Arbiter allocates a Ticket per login, sends it to World in
`AS_ENTER_WORLD`, and World echoes it in every `SA_BYPASS_TO_CLIENT` header. `TeraSharp`
already reads the right field. What it does **not** do is (a) allocate a distinct Ticket per
session, (b) parse the recipient **list** — `SA_BYPASS_TO_CLIENT` addresses N recipients, not
one, and the client packet does not start at a fixed offset when N > 1.

That second point is the actual blocker: **with two players in view of each other the current
frame parser reads the wrong bytes as the client packet.** It is not a routing refinement, it
is a decode bug that only ever fires with two logins.

---

## 1. `SA_BYPASS_TO_CLIENT` (0x13F7) — inbound

### 1.1 Real layout

From the PDL dumper `FUN_1402152d0` (`Arb_part_016.c:8636`):

```c
  wcscpy_s(param_3,0x100,L"SA_BYPASS_TO_CLIENT");
  iVar1 = *(int *)((longlong)piVar4 + 6);            // UserList offset
  local_28[0] = FUN_14016bd70(param_3,L"UserList",piVar5);
  iVar1 = *(int *)((longlong)piVar4 + 0xe);          // Packet offset
  local_28[0] = FUN_14016bd70(param_3,L"Packet",piVar3);
```

| frame off | payload off | u32 | meaning |
|---|---|---|---|
| +0x06 | 0 | `UserListOffset` | frame-relative, always **22** |
| +0x0A | 4 | `UserListBytes` | **16 × recipientCount** — *not* the constant 16 |
| +0x0E | 8 | `PacketOffset` | frame-relative, **22 + UserListBytes** |
| +0x12 | 12 | `PacketLength` | client packet bytes |
| +0x16 | 16 | `UserList[]` | 16 bytes per recipient |

`UserList` entry, from `Handler_SA_BYPASS_TO_CLIENT` = `FUN_140720f60` (`Arb_part_062.c:3755`):

```c
          local_80  = *(int *)puVar10;                            // +0  PlanetId
          uStack_7c = *(undefined4 *)(puVar10 + 2);               // +4  (never read)
          uStack_78 = (undefined4)*(ulonglong *)(puVar10 + 4);    // +8  Ticket
          uStack_74 = *(uint *)(puVar10 + 6);                     // +12 Seq << 19
```

So for **one** recipient the header is 32 bytes and the packet starts at payload+32 — which
is exactly what `WorldBridge.HandleFrame` hardcodes, and why single-player works. For **two**
recipients the header is 48 bytes and the packet starts at payload+48.

### 1.2 The lookup

`Arb_part_062.c:3762`:

```c
          if (local_80 == DAT_140e2d020) {                 // entry+0 == our PlanetId
            uVar13 = uStack_74 >> 0x13;                    // seq = entry+12 >> 19
            plVar6 = (longlong *)
                     FUN_1405b3840(DAT_141094508,&local_b0,*(ulonglong *)(puVar10 + 4) & 0xffffffff);
```

`FUN_1405b3840` is `PacketBypassManager::GetSession(int)` (`Arb_part_048.c:11975`) — a flat
array indexed by Ticket, no bounds check:

```c
  lVar1 = *(longlong *)(*(longlong *)(param_1 + 0x60) + (longlong)param_3 * 8);
```

`DAT_140e2d020` is this Arbiter's **PlanetId**, from `ServerConfig.xml`'s `planetId`
(`Arb_part_033.c:13299`, `:13511`). Every capture shows `0x0AF0` = 2800, and the same 2800
appears inside every `gameId` (`0x80000AF0000n` = `(0x8000 << 32) | (planetId << 20) | n`).

### 1.3 TeraSharp is already reading the right field

`WorldBridge.TunnelKeyOffset = 24` lands on payload+24 = frame+30 = **entry+8 = Ticket**. The
comment in the file calls it "idx (slot index from BypassStart)" and hedges that the two-login
capture will settle it — it is settled, and the guess was right. `CLAUDE.md` §3's
`[16]u32 serverId [20]u32 conn [24]u32 idx [28]u32 seqField` maps to
`PlanetId / unread / Ticket / Seq<<19`; only the names are wrong.

The sequence extraction is right too: `Seq << 19` as a u32 means the u16 at payload+30 is
`Seq << 3`, so `>> 3` recovers it. Confirmed: the low 19 bits are zero in every 0x13F7 frame
of all four captures, and the reorder slot array in `PacketBypassController` is 0x10000 bytes
= 8192 pointers, i.e. exactly 13 bits of sequence (`FUN_14049a5a0`, `Arb_part_038.c:10806`).

### 1.4 One frame, several recipients

The Arbiter allocates **one** `BypassPacketInfo` per 0x13F7 and refcounts it by recipient
count (`Arb_part_062.c:3736`):

```c
      lVar12 = FUN_14002a7a0(DAT_14131d858,0x18);
      *(TypeDescriptor **)(lVar12 + -8) = &BypassPacketInfo::RTTI_Type_Descriptor;
      FUN_140679590(lVar12,&local_d0,uVar13);       // buf @+0, len @+8
      FUN_14063f4b0(lVar12,uVar14 & 0xffffffff);    // refcount(+0xc) += recipientCount
```

then posts one job per live recipient onto that session's own queue and backs out the refcount
for recipients with no live session (`Arb_part_062.c:3800`). Fan-out is the designed case, not
an edge case.

`param_1` — the `WorldServerSession` the frame arrived on — **is never referenced** in
`FUN_140720f60`. The Arbiter does not care which of the 25 links a tunnel frame comes in on.

**No capture contains a multi-recipient frame**: all four are single logins, and every one of
the 5810 0x13F7 frames across them has `UserListBytes == 16`, and every one has its client packet at
`payload[8] - 6 == 16 + UserListBytes` (checked mechanically).

---

## 2. `AS_BYPASS_FROM_CLIENT` (0x13F6) — outbound

Written by `Handler_CA_Default` = `FUN_1404d7fb0` (`Arb_part_040.c:16610`), which is the
**default entry of the Arbiter's 65536-slot client-opcode table** (`Arb_part_000.c:5493`:
`for (lVar1 = 0x10000; …) *puVar2 = FUN_1404d7fb0;`). Anything the Arbiter has no handler for
is tunnelled — the same rule `PacketDispatcher` already implements.

Dumper `FUN_14017d9c0` (`Arb_part_011.c:6532`): `Packet` offset @+6, `Packet` length @+0x0A,
**`WorldClient`** u64 @+0x0E, **`SendTick`** u64 @+0x16. `TeraSharp.TunnelFromClient` builds
exactly this.

Two corrections to the note in `CLAUDE.md` §3:

- the u64 at payload+8 is `WorldClient`, sourced from `User+0x4038`, which is set from
  `SA_ENTER_WORLD`'s own `WorldClient` field (`User::EnterWorldEnd` = `FUN_140382180`,
  `Arb_part_028.c:15031`: `*(undefined8 *)(param_1 + 0x4038) = param_3;`). It is **not** the
  `GameId` field at `User+0x5718`. They hold the same value in this build — `AS_ARBITER_USER_DELETE`
  sends both side by side and the captures show them equal — but they are different fields.
  Feeding `GameId` is correct today and would stay correct as long as World keeps echoing it.
- client packets ≥ 0x1F41 bytes are **not** tunnelled; the real Arbiter takes the else branch
  and kicks (`Arb_part_040.c:16700`).

### Link selection — per-user affinity, not round-robin

`User::GetBypassSession` = `FUN_140385c80` (`Arb_part_028.c:17295`) → … →
`WorldSessionManager::GetBypassSessionByServerId` = `FUN_14056d570` (`Arb_part_046.c:2413`):

```c
    lVar2 = *(longlong *)
             (*(longlong *)(lVar2 + 8 + param_1) +
             (longlong)(param_4 % *(int *)(lVar2 + 0x20 + param_1)) * 8);
```

→ **`bypassLinks[ UserDbId % TotalBypassCount ]`**, `TotalBypassCount` = 24 from `SA_REGISTER`.
Fixed for a user's whole session; there is no stored link index and no round-robin. (What *is*
round-robin is CPU-thread assignment, `Arb_part_048.c:15327` — a different thing.)

Control messages go elsewhere: `User::GetWorldServerSession` = `FUN_140388d60`
(`Arb_part_028.c:19691`) → `WorldSessionManager::GetDataSession(int)` = `FUN_14056d7d0`
(`Arb_part_046.c:2545`), the `IsBypass=0, BypassIndex=-1` link. Chat, `AS_ARBITER_USER_DELETE`,
every `AS_DO_*_PARTY` and ~20 other senders use it.

`TeraSharp.SendFrame` always writes `_links[0]`, which is the main link, so **control traffic
is already on the right link**. Tunnel traffic is also on `_links[0]` rather than a bypass
link. Whether World minds is the one genuinely open question (§8) — it cannot be settled from
the Arbiter binary.

---

## 3. The Ticket: lifetime and the rest of the handshake

### Allocation

`PacketBypassManager::BypassStart(TPointer<ClientSession,5>,int)` = `FUN_1405afaf0`
(`Arb_part_048.c:9296`) — linear probe from a monotonic cursor:

```c
  uVar8 = *(uint *)(param_1 + 0x58);            // cursor
  uVar3 = *(uint *)(param_1 + 0x5c);            // table size = maxUsers * 4
  uVar9 = uVar8 % uVar3;
  while (lVar5 != 0) { uVar8 = uVar8 + 1; … }   // probe
  *plVar2 = lVar4;                              // store the ClientSession
  return uVar9;
```

Called from the enter-world path (`Arb_part_028.c:15398`) and passed as parameter 14 of the
`AS_ENTER_WORLD` writer — the dumper names it **`Ticket`** at frame+0x56 = **payload+80**,
which is exactly the field `WorldEntry.BuildEnterWorldPayload` writes as `tunnelKey`.

Observed Tickets: `cap_newchar` 0; `lobby_tap` 0 then 1; the 09-13 relog 0, 1, 2. Tickets come
out 0,1,2,… — the cursor is monotonic, so a freed slot is not reused immediately.

### Release

`SA_LEAVE_WORLD` carries the Ticket. Dumper `Arb_part_016.c:13466`: `ArbiterUser` u64 @+6,
`GameId` u64 @+0x0E, `LeaveWorldType` @+0x16, `LogoutReason` @+0x1A, **`Ticket`** @+0x1E,
**`LastIndex`** @+0x22. `Handler_SA_LEAVE_WORLD` = `FUN_140728c20` (`Arb_part_062.c:9038`):

```c
  plVar5 = (longlong *)FUN_1405b3840(DAT_141094508,local_68,*(undefined4 *)(lVar3 + 0x1e));
  …
    local_58 = FUN_140883d90;                                   // ClientSession::BypassEnd(int,uint)
    FUN_1406d4490(lVar3,&local_58,*(undefined4 *)(*param_3 + 0x1e),*(undefined4 *)(*param_3 + 0x22));
```

`LastIndex` is the final sequence World emitted on that Ticket; the controller drains up to it,
then `PacketBypassManager::BypassEnd(int)` = `FUN_1405afa20` (`Arb_part_048.c:9262`) clears the
slot. `SA_ENTER_WORLD_FAIL` carries the same `Ticket`/`LastIndex` pair (`Arb_part_016.c:11444`).

**So the Ticket is freed by the `SA_LEAVE_WORLD` handler, not by the Arbiter deciding to log
the player out.** In TeraSharp terms: `UnregisterPlayer` must run off 0x1393, which it already
does.

### Two queues per session

`PacketBypassController` (at `ClientSession+0x168`) holds **two** 8192-slot reorder arrays
(`+8` and `+0x10020`), each tagged with its own Ticket at `+0x10004`, and a current-index int
flipped by `PacketBypassController::ChangeQueueIndex`. `SetBypassPacket` = `FUN_1408dee30`
(`Arb_part_077.c:8157`) matches the incoming Ticket against queue 0, then queue 1:

```c
  iVar2 = FUN_14049a5a0(lVar3);  if (iVar2 == param_2) { … }
  else { lVar3 = param_1 + 0x10020; iVar2 = FUN_14049a5a0(lVar3); if (iVar2 == param_2) goto …; }
  …  L"Bypass ticket error. [LtId : %d] [Seq : %d] [SessionId : %d]\n"
```

That is how a user can have an old Ticket draining while a new one is already live — i.e. a
world transfer. TeraSharp has no equivalent and does not need one until zone servers exist,
but it explains why a stale Ticket produces a log line rather than a crash.

### `ArbiterUser` / `ArbiterClient`

`AS_ENTER_WORLD` carries `ArbiterClient` u64 @frame+0x16 and `ArbiterUser` u64 @frame+0x1E, and
they are literally heap pointers — `*puVar15` (the `ClientSession*`) and `param_1` (the `User*`)
at `Arb_part_028.c:15488`. The captures show `ArbiterClient=0x0000027057c028e0`,
`ArbiterUser=0x0000026f4cfba020`.

World echoes `ArbiterUser` back in every user-scoped `SA_*`, and the Arbiter validates it
before dereferencing — `_Handler_SA_LEAVE_WORLD` = `FUN_140795980` (`Arb_part_066.c:3083`):

```c
    lVar1 = *(longlong *)(lVar3 + 6);                                  // ArbiterUser
    if ((lVar1 != 0) && (cVar2 = FUN_14083c170(DAT_141214ff0,lVar1), cVar2 != '\0')) {
        puVar4[0xe] = lVar1;  puVar4[0xf] = FUN_140728c20;
        FUN_14003c4c0(lVar1 + 0x3f60,puVar4);                          // the User's own job queue
```

`FUN_14083c170` (`Arb_part_072.c:3618`) is a 16-shard hash set of live `User*` values — a
pointer-liveness check. **Every user-scoped W→A message is serialised onto that user's own
queue at `User+0x3F60`.** That is the Arbiter-side analogue of World's DLMItem queue: it is
per-user, so two players cannot head-block each other, and TeraSharp gets the same property
for free by keying its handlers off the payload rather than a global.

TeraSharp sends zeros for both handles today. Nothing in the captures shows World doing
anything with `ArbiterClient` other than echoing it, and the Arbiter never resolves it back to
a session (client-directed routing uses the Ticket). **Open:** whether World validates
`ArbiterUser` non-zero. Not observable from the Arbiter binary; a two-login capture will not
answer it either — only trying it will.

### `AS_ARBITER_USER_DELETE` (0x1433)

Dumper `Arb_part_011.c:5856`: `GameId` u64 @+6, `WorldUser` u64 @+0x0E. Sent by
`UserManager::NotiWorldToReleaseUser` = `FUN_140832f20` (`Arb_part_071.c:17484`) on the **data**
link, from exactly one call site — `User::LeaveWorldEnd` (`Arb_part_029.c:3595`), *after* the
leave is accepted:

```c
  *(undefined4 *)(param_1 + 0x3fc0) = 0;      // LoginState = NONE
  *(undefined4 *)(param_1 + 0x3fc8) = 0;      // LeaveWorldType = NONE
  FUN_140832f20(DAT_141214fe8,param_1);       // -> AS_ARBITER_USER_DELETE
```

Order in every capture: `AS_LEAVE_WORLD` → World tears down → `SA_LEAVE_WORLD` (frees the
Ticket) → `AS_ARBITER_USER_DELETE`. It carries no Ticket — it is purely "you may drop the
World-side User object now". TeraSharp already sends it from the 0x1393 handler.

### `WorldSession+0x3108` — not a routing key

The user's brief named this as "the routing key". There are exactly six references to `0x3108`
in the Arbiter (`Arb_part_028.c:15486/15538/15815/15843`, `Arb_part_030.c:6805/6833`), all of
the shape `*(undefined4 *)(*(longlong *)(param_1 + 0x3f40) + 0x3108)` where `param_1` is a
`User*` and `User+0x3f40` is the `Account*` (proved by `Account::GetClientSession` =
`FUN_1407162c0`, `Arb_part_061.c:15686`). The value goes on the wire as `AS_ENTER_WORLD`'s
**`SessionKey`** at frame+0x32 — dumper `Arb_part_011.c:10304`:

```c
  local_48 = (int *)FUN_14016bb70(param_3,L"SessionKey",*(undefined4 *)((longlong)piVar7 + 0x32));
```

`lobby_tap.log` seq 127, bytes at frame+0x32: `45 16 04 00` = 267845, the same value the client
got in `S_LOGIN_ACCOUNT_INFO`. It is the **login/auth session key**, echoed to World for
validation — not a map, not a counter, not a link index. If the brief meant an offset inside
`WorldServer.exe`'s own `WorldSession`, that is unanswerable from here.

---

## 4. Per-session routing state

| where | offset | field | note |
|---|---|---|---|
| `PacketBypassManager` | +0x58 | ticket cursor | monotonic |
| | +0x5C | table size | `maxUsers * 4` |
| | +0x60 | `ClientSession*[]` | **the Ticket → session table** |
| `ClientSession` | +0x0CC | SessionId | log only |
| | +0x138 | `Account*` | |
| | +0x168 | `PacketBypassController` | two 8192-slot reorder queues |
| | +0x201A8 | ThreadId | round-robin at connect |
| | +0x201C0 | async job queue | where 0x13F7 lands |
| `User` | +0x120 | **UserDbId** | selects the bypass link |
| | +0x19C / +0x1A0 | ContinentId / ChannelInstanceId | whisper/party visibility checks |
| | +0x3F40 | `Account*` | |
| | +0x3F60 | **the User's own job queue** | per-user serialisation |
| | +0x3FC4 | cached SessionKey | copy of `Account+0x3108` |
| | +0x4038 | **WorldClient** | goes in every 0x13F6 |
| | +0x5718 | **GameId** | masked `& 0x7fff…` on the wire |
| `WorldSessionManager` per-world | +0x00 | DataSession | the control link |
| | +0x08..+0x18 | `vector<Session>` bypass links | **in connect order** |
| | +0x20 | TotalBypassCount | 24 |

Note what is **not** there: no stored Ticket on the `User`, no stored link index. The Ticket
lives only in the reorder-queue header and the manager's table; the link is recomputed from
`UserDbId % 24` on every send.

---

## 5. What changes when two players are online

### 5.1 Chat — `0x1449` has no recipient field

`AS_REQUEST_NORMAL_CHAT` (0x1449), dumper `FUN_14018fcb0` (`Arb_part_011.c:19173`):
`u32 TalkOffset @+6, u32 SenderDbId @+0x0A, u32 ChatType @+0x0E, UTF-16LE Talk @+0x12`. Writer
at `Arb_part_047.c:7971`. **There is no recipient list.** World resolves who can hear it and
sends the result back as `SA_BYPASS_TO_CLIENT` with an N-entry `UserList`.

`lobby_tap.log` shows the round trip:

```
[761] A->W 0x1449 len=82  TalkOff=18 SenderDbId=1 ChatType=0 '<FONT>hey whats up hello</FONT>'
[762] W->A 0x13F7 entries=1  entry=(2800, 231, Ticket=0, seq=460)  clientOp=0x7D6B (S_CHAT) len=95
```

With two players in range, `entries` becomes 2. That single frame is the most informative thing
a two-client capture can produce.

The dispatcher is `ChatManager::ChatMessageHandler(User*,ChatType,const wchar_t*)` =
`FUN_140591f70` (`Arb_part_047.c:7852`), and only two ChatType groups reach World at all:

| ChatType | route |
|---|---|
| 0, 9, 0xA, 0xD4 | `AS_REQUEST_NORMAL_CHAT` 0x1449 → World data link (`Arb_part_047.c:7971`) |
| 5, 0x16 | `AS_REQUEST_TEAM_CHAT` 0x144A → World data link (`:8046`) |
| 1, 0x15, 0x19, 0x20 | `PartyManager::BroadcastPartyChatMessage` = `FUN_14090ee50` (`Arb_part_079.c:3639`) — **Arbiter-local** |
| 2, 0xD6 | `Guild::BroadcastChatMessage` = `FUN_140568aa0` (`Arb_part_045.c:18796`) — **Arbiter-local** |
| 3, 0x17 | `Guard::BroadcastChatMessage` = `FUN_140930a30` (`Arb_part_080.c:8241`) — **Arbiter-local** |
| 4, 0x1B, 0xD5 | `ChatManager::BroadcastChatMessage` = `FUN_14058e840` (`Arb_part_047.c:5694`) — **every online user** |
| 0x0B–0x12 | `BroadcastToPrivateChannel` — **Arbiter-local** |

The broadcast forms build **one** `S_CHAT` (client opcode 0x7D6B) and loop over sessions
(`Arb_part_047.c:5694`):

```c
    FUN_14082f2f0(DAT_141214ff0,&local_90,1);           // UserManager::GetAllUsers
    … FUN_140055430(&local_c0,0x7d6b); …
    for (plVar12 = local_90; plVar12 != plVar9; plVar12 = plVar12 + 1) {
      … FUN_140388310(lVar11,&local_98);                 // User::GetClientSession
      … (*pcVar7)(plVar8,&local_c8,uVar2,1);             // send
```

**Whisper never touches World.** `Handler_C_WHISPER` = `FUN_1404f23a0` (`Arb_part_041.c:14662`)
resolves the target **by name** against the Arbiter's own `UserManager`
(`FUN_14082e460` = `GetCachedUserWithLock(const wchar_t*)`), then
`ChatManager::ProcessWhisperMessage` = `FUN_1405a1110` (`Arb_part_047.c:18678`) checks blocks
and `ContinentId`/`ChannelInstanceId` equality and sends `S_WHISPER` to both sessions. In a
two-client capture a whisper produces **zero** A↔W frames.

TeraSharp already has the right shape for this: `SocialHandlers.Sessions` is a name→session
map and `ChatHandlers` owns `C_CHAT`. Nothing there assumes one player. The only chat work
multiplayer needs is making sure the **world** chat types (0/9/0xA/0xD4, 5, 0x16) go out as
0x1449/0x144A rather than being answered locally — check `ChatHandlers` before the two-login
test.

### 5.2 W→A frames that name several players

- `SA_BYPASS_TO_CLIENT` 0x13F7 — `UserList[]`, §1.
- `SA_BYPASS_TO_GROUP` 0x13F8 — `PacketOffset @+6, PacketLen @+0x0A, GroupType @+0x0E,
  GroupId u64 @+0x12, ObjectPlanetId @+0x1A, ObjectId @+0x1E`, then the raw client packet.
  Handler `FUN_140721360` (`Arb_part_062.c:3820`) hands it to
  `PartyManager::BroadcastPacketToParty` — World asks the **Arbiter** to fan out to a party.
  TeraSharp does not handle 0x13F8 at all; see `PARTY-DESIGN.md`.
- `DSA_SYSTEM_MESSAGE` 0x13F3 — a `UserList` whose element schema is explicitly
  `{int PlanetId; int DbId}` (`Arb_part_016.c:3141`). This is the only place the cross-server
  id type `PDId` is named on the wire.

### 5.3 Party, guild, friends

All Arbiter-local (see `PARTY-DESIGN.md`). Relevant to routing only in that they fan a single
built packet across N sessions, which needs a session registry — TeraSharp has one
(`SocialHandlers.Sessions`) and `WorldBridge._players` is a second, keyed by gameId.

---

## 6. The minimal change — exact diffs

Five changes. (1) and (2) are the correctness fixes; (3)–(5) make two logins actually distinct.

### (1) `World/WorldBridge.cs` — parse the recipient list, not a fixed 32-byte header

The current `OpTunnelToClient` case assumes one recipient. Replace:

```csharp
            case OpTunnelToClient:
            {
                if (payload.Length < 32) { _log.LogWarning("Short 13F7 on link #{Id}", link.Id); return; }
                int clientLen = BitConverter.ToInt32(payload, 12);
                if (32 + clientLen > payload.Length) clientLen = payload.Length - 32;
                var clientPkt = new byte[clientLen];
                Array.Copy(payload, 32, clientPkt, 0, clientLen);

                uint seq = (uint)(BitConverter.ToUInt16(payload, 30) >> 3);
                uint key = BitConverter.ToUInt32(payload, TunnelKeyOffset);
```

with:

```csharp
            case OpTunnelToClient:
            {
                // SA_BYPASS_TO_CLIENT addresses N recipients, not one:
                //   [0] u32 userListOffset (frame-rel, 22)   [4] u32 userListBytes = 16 * N
                //   [8] u32 packetOffset (frame-rel, 22+16N) [12] u32 packetLength
                //   [16] UserList[N], 16 B each: [+0 planetId][+4 unused][+8 ticket][+12 seq<<19]
                // Arb_part_016.c:8636 (dumper) and Arb_part_062.c:3755 (handler).
                if (payload.Length < 16) { _log.LogWarning("Short 13F7 on link #{Id}", link.Id); return; }
                int userListBytes = BitConverter.ToInt32(payload, 4);
                int recipients = userListBytes / UserListEntrySize;
                int pktStart = BitConverter.ToInt32(payload, 8) - 6;     // frame-relative -> payload
                int clientLen = BitConverter.ToInt32(payload, 12);
                if (recipients <= 0 || recipients > MaxTunnelRecipients ||
                    16 + userListBytes > payload.Length ||
                    pktStart < 0 || clientLen < 0 || pktStart + clientLen > payload.Length)
                {
                    _log.LogWarning("Malformed 13F7 on link #{Id}: n={N} pktStart={S} len={L} payload={P}",
                        link.Id, recipients, pktStart, clientLen, payload.Length);
                    return;
                }
                var clientPkt = new byte[clientLen];
                Array.Copy(payload, pktStart, clientPkt, 0, clientLen);
```

then replace the single-recipient delivery block with a loop over the entries. Each recipient
carries its **own** sequence number, so the reorder buffer lookup has to happen per entry:

```csharp
                for (int e = 0; e < recipients; e++)
                {
                    int b = 16 + e * UserListEntrySize;
                    uint key = BitConverter.ToUInt32(payload, b + 8);            // Ticket
                    uint seq = (uint)(BitConverter.ToUInt16(payload, b + 14) >> 3);
                    DeliverTunnelPacket(key, seq, recipients == 1 ? clientPkt : (byte[])clientPkt.Clone());
                }
                return;
```

with the existing solo/keyed/fallback logic moved verbatim into a new private
`DeliverTunnelPacket(uint key, uint seq, byte[] clientPkt)`. Two new constants next to
`TunnelKeyOffset`:

```csharp
    /// <summary>Size of one SA_BYPASS_TO_CLIENT UserList entry (Arb_part_062.c:3755).</summary>
    internal const int UserListEntrySize = 16;
    /// <summary>Sanity cap on recipients in one tunnel frame; the real cap is the party size (30).</summary>
    internal const int MaxTunnelRecipients = 64;
```

`TunnelKeyOffset` stays 24 and keeps working for the one-recipient case (16 + 8 = 24), but the
loop above should be the only reader; leave the constant for the tests that reference it.

The **clone** matters: the current code hands the same `byte[]` to one session. Two sessions
each get it queued in their own reorder buffer and `GameSession.Send` encrypts in place with a
stateful cipher, so they must not share an array.

### (2) `World/WorldBridge.cs` — one Ticket per session

```csharp
    private uint _nextTunnelKey = 5;
    …
    internal uint AllocateTunnelKey() { lock (_reorderLock) return _nextTunnelKey; }  // pinned to 5 …
```

becomes:

```csharp
    /// <summary>
    /// Next Ticket to hand out. The real Arbiter's PacketBypassManager::BypassStart
    /// (FUN_1405afaf0, Arb_part_048.c:9296) probes linearly from a monotonic cursor over a
    /// maxUsers*4 table, so tickets come out 0,1,2,… and a freed slot is not reused
    /// immediately. Captures: cap_newchar 0; lobby_tap 0,1; 09-13 relog 0,1,2.
    /// TeraSharp starts at 5 because that is what the login capture it was built from used;
    /// the value is arbitrary, only uniqueness matters.
    /// </summary>
    private uint _nextTunnelKey = 5;

    /// <summary>Allocate a unique Ticket for a new player session.</summary>
    internal uint AllocateTunnelKey()
    {
        lock (_reorderLock)
        {
            // Skip anything still in use (a relog before SA_LEAVE_WORLD frees the old slot).
            while (_tunnels.ContainsKey(_nextTunnelKey)) _nextTunnelKey++;
            return _nextTunnelKey++;
        }
    }
```

The `_tunnels` entry is created by `RegisterPlayer` and removed by `UnregisterPlayer`, which
already runs off `SA_LEAVE_WORLD` — so the "still in use" test is honest.

### (3) `World/WorldBridge.cs` — drop the single-player fast path once a second session exists

No diff needed: the fast path is already gated on `_tunnels.Count == 1`. But the comment
should stop saying the per-key path is "deferred until two-login capture" — it is the path
this document specifies. Suggested replacement for that comment block:

```csharp
                // SINGLE-PLAYER FAST PATH: one session -> ignore the Ticket, one shared queue.
                // Byte-identical to the broadcast tunnel that worked for login AND logout, and
                // a safety net for the window between AS_ENTER_WORLD and RegisterPlayer.
                // With 2+ sessions the per-Ticket path below takes over (MULTIPLAYER-DESIGN.md).
```

### (4) `World/WorldBridge.cs` — `ResetTunnelSequence` must not touch other players

```csharp
    public void ResetTunnelSequence()
    {
        lock (_reorderLock)
        {
            foreach (var buf in _tunnels.Values) { buf.Pending.Clear(); buf.NextSeq = 0; … }
        }
    }
```

resets **every** session's sequence. Called on one player's enter-world it would drop the other
player's in-flight packets. Add a keyed overload and call that instead:

```csharp
    /// <summary>Reset one session's reorder state. Sequence numbers are per-Ticket
    /// (SA_BYPASS_TO_CLIENT entry+12 >> 19), so a second player must not be touched.</summary>
    public void ResetTunnelSequence(uint tunnelKey)
    {
        lock (_reorderLock)
        {
            if (!_tunnels.TryGetValue(tunnelKey, out var buf)) return;
            buf.Pending.Clear();
            buf.NextSeq = 0;
            buf.LastDelivery = DateTime.UtcNow;
        }
    }
```

and change the call site in `Handlers/WorldEntry.cs` (line ~45) from
`w.ResetTunnelSequence();` to `w.ResetTunnelSequence(s.TunnelKey);`. Keep the parameterless
version for the "World restarted" case.

### (5) `Network/GameSession.cs` — nothing, but check one thing

`GameSession` already carries `GameId` and `TunnelKey` per session and `Send` is locked
("tunnel packets arrive on 25 threads; cipher is stateful"). With two sessions each gets its
own lock, which is correct. The only thing to verify before the live test is that
`SocialHandlers.RegisterSession` / `UnregisterSession` are actually called on select/leave —
whisper and every Arbiter-local chat fan-out depend on that map, and with one player it has
never mattered.

### Not changed, deliberately

- **`TunnelFromClient` keeps using `_links[0]`.** The real Arbiter would use
  `bypassLinks[UserDbId % 24]`, but the Arbiter's own inbound handler ignores which link a
  frame arrives on, and TeraSharp's reorder state is keyed by Ticket, not by link. Changing it
  is a bigger diff into `WorldLink` selection for no benefit we can demonstrate. §8 lists it as
  the open question the capture should answer.
- **`ArbiterUser` / `ArbiterClient` stay zero** in `AS_ENTER_WORLD` until something shows World
  cares.

---

## 7. Tests a two-login capture would make possible

These are the tests to write *after* the capture exists; each names the capture evidence it
needs. All of them fit the existing `RunHandler`-style harness in
`src/TeraSharp.Arbiter.Tests/Program.cs` plus a TSIS data file (`data/cap_twologin.bin`).

| test | asserts | needs from the capture |
|---|---|---|
| `Tunnel_two_recipient_frame_splits_to_both_sessions` | one captured 2-entry 0x13F7 → two deliveries, same bytes, to the two Tickets | **the 32-byte `UserList` frame** |
| `Tunnel_packet_offset_follows_the_user_list_length` | `pktStart == payload[8]-6 == 16 + userListBytes` for both the 1- and 2-entry frames | both frames |
| `Tunnel_each_recipient_has_its_own_sequence` | the two entries' `seq` differ, and each session's reorder buffer advances independently | the 2-entry frame |
| `Tunnel_out_of_order_two_players_do_not_block_each_other` | feed A.seq 0,2 and B.seq 0,1: B delivers both while A holds | synthetic, no capture needed |
| `Tunnel_recipient_with_no_session_is_dropped_not_broadcast` | a frame naming an unknown Ticket alongside a known one delivers once | the 2-entry frame, or synthetic |
| `EnterWorld_two_sessions_get_distinct_tickets` | `AllocateTunnelKey()` twice → different values; both appear at payload+80 of the two `AS_ENTER_WORLD` frames | the two `AS_ENTER_WORLD` frames |
| `EnterWorld_ticket_is_reusable_after_leave` | register/unregister/register → the freed Ticket is not handed out while still in `_tunnels` | synthetic |
| `LeaveWorld_frees_only_the_leaving_ticket` | `UnregisterPlayer(gameIdA, ticketA)` leaves B's buffer intact | synthetic |
| `ResetTunnelSequence_is_per_ticket` | resetting A does not clear B's `Pending` | synthetic |
| `Chat_world_type_goes_out_as_0x1449_with_no_recipient` | the built 0x1449 payload is `[u32 talkOff=18][u32 senderDbId][u32 chatType][wstr]` byte-exact | the captured 0x1449 from either player |
| `Chat_whisper_produces_no_world_traffic` | `C_WHISPER` between two registered sessions sends two `S_WHISPER` and zero A→W frames | the capture, as a negative |
| `Tunnel_frame_rejects_impossible_header` | malformed `userListBytes` / `packetOffset` logs and returns instead of throwing | synthetic |

The synthetic ones can be written **now** — they do not need the capture and they pin the
diffs in §6 before the live test.

**Eight of them exist as of T34**, in `src/TeraSharp.Arbiter.Tests/Program.cs` under
`Routing_*`. Six are red against the current `WorldBridge` — they are the executable form of
§6's diffs — and report `PENDING` rather than throwing while
`Tests.RoutingDiffsApplied` is `false`, so the test run stays usable as a merge gate. Flip that
flag when §6 lands; a pending test that unexpectedly passes fails loudly, which is the reminder.

What the six actually catch today:

| test | current behaviour |
|---|---|
| `Routing_two_recipient_frame_splits_to_both_sessions` | A gets 1 packet, B gets 0 |
| `Routing_packet_offset_follows_the_user_list_length` | the "client packet" is `f0 0a 00 00` — the second UserList entry's PlanetId, read from the hardcoded offset 32 |
| `Routing_each_recipient_has_its_own_sequence` | B gets nothing; only entry 0's ticket and sequence are read |
| `Routing_recipient_with_no_session_is_dropped_not_broadcast` | an unknown ticket is broadcast to **both** live sessions |
| `Routing_two_sessions_get_distinct_tickets` | both get Ticket 5 |
| `Routing_reset_for_one_player_keeps_the_others_pending_packets` | the global reset throws away the other player's queue |

`Routing_out_of_order_two_players_do_not_block_each_other` and
`Routing_leave_frees_only_the_leaving_ticket` already pass; they are regression locks so §6
cannot break what works.

**Two existing tests contradict the new ones and must go when §6 is applied**:
`TunnelRouting_unknown_key_broadcasts` pins the broadcast fallback (delete it, or narrow it to
the single-session case, which is the only place the fallback is wanted), and
`AllocateTunnelKey_pinned_to_5` pins the constant Ticket.

---

## 8. Capture checklist for the human

The four existing taps are all one account, one login. The tap format is
`[N] [W->A|A->W] <iso> len=NNN` + hex, which does **not** record which of the 25 sockets a
frame arrived on — that is the single biggest gap.

**Before capturing**

1. **Label the socket.** Add the socket/link id to the tap line, and record each socket's
   `BypassIndex` from its `SA_REGISTER` (0x138A: `IsBypass` u8 @+6, `PlanetId` @+7, `WorldId`
   @+0x0B, `TotalBypassCount` @+0x0F, `BypassIndex` i32 @+0x13). Without it, "is the tunnel on
   the bypass link or the data link" cannot be answered.
2. **Two different accounts, two different characters**, so `AccountDbId` and `UserDbId`
   decorrelate. Ideally give them `UserDbId` 1 and 2 — different `UserDbId % 24`.
3. Start World fresh so ticket allocation starts from 0.

**The runs, in this order** (each line is one thing to do, and what it should produce)

| # | do this | expect on the tap |
|---|---|---|
| 1 | log in player A, walk to a quiet spot | `AS_ENTER_WORLD` Ticket=0, gameId `…0001`; 0x13F7 frames all `UserListBytes=16`, Ticket 0 |
| 2 | log in player B **while A stays in world**, same zone, **in view of A** | second `AS_ENTER_WORLD` Ticket=1, gameId `…0002` |
| 3 | **A walks in a circle while B watches** | ★ the money frame: 0x13F7 with `UserListBytes=32`, two entries, two different Tickets, two different seqs |
| 4 | A types in **area chat**; then B replies | `A->W 0x1449` from each; each answered by a 0x13F7 whose `UserList` holds **both** tickets, client opcode 0x7D6B |
| 5 | A **whispers** B, B whispers back | **nothing on the A↔W tap.** Confirms whisper is Arbiter-local |
| 6 | A invites B to a **party**, B accepts | `W->A SA_JOIN_PARTY` 0x1395, then `A->W AS_DO_CREATE_PARTY` 0x139E + `AS_DO_ADD_PARTY_MEMBER` 0x139F on the **data** link |
| 7 | **party chat** both ways | **nothing on the A↔W tap** |
| 8 | kill one mob as a party, loot it | `W->A SA_BYPASS_TO_GROUP` 0x13F8 — the only W→A frame that names a `GroupId` instead of tickets |
| 9 | change the party **looting method** | `A->W AS_PARTY_LOOTING_METHOD` 0x13BB, then `W->A SA_CHANGE_LOOTING_METHOD` 0x139D |
| 10 | **A logs out to lobby**, B stays in world | `SA_LEAVE_WORLD` for A only, carrying A's Ticket and `LastIndex`; B's 0x13F7 stream continues uninterrupted |
| 11 | **A logs back in** while B is still in world | new `AS_ENTER_WORLD` — note whether the Ticket is 0 again or 2 |
| 12 | both walk into the **same dungeon instance**, then both out | zone-change frames per player; confirms `ChannelInstanceId` handling with two users |
| 13 | **B disconnects** (kill the client) while A stays | disconnect path for B only |

**The three questions the capture must settle**

- **Q1 — the unnamed dword at `UserList` entry+4.** The Arbiter never reads it. Across the
  single-login captures it correlates with the *client opcode*, not with the session, which
  suggests a World-side source object id. Step 3's two-entry frame decides it: if the value
  **differs between the two entries of the same frame** it is per-destination; if it is
  **identical** it is per-source. Either way, record it.
- **Q2 — does the tunnel arrive on the bypass link or the data link, and does World care which
  link we send 0x13F6 on?** Needs the socket labelling from step 1. If the two players' 0x13F7
  frames arrive on different sockets, World is using `UserDbId % 24` and TeraSharp's
  single-link send is worth revisiting.
- **Q3 — is a Ticket reused after a leave?** Step 11. The Arbiter's cursor is monotonic modulo
  `maxUsers*4`, so it should **not** come back as 0.

**One more, cheap:** run step 4 with the two characters in **different zones**. The 0x13F7 for
that chat should carry only the sender's Ticket — that pins down that World, not the Arbiter,
does the visibility filtering.

---

## 9. Corrections to existing notes

| where | says | actually |
|---|---|---|
| `CLAUDE.md` §3 tunnel | `[0]u32=22 [4]u32=16` | `[4]` is `16 × recipientCount`, not a constant |
| `CLAUDE.md` §3 tunnel | `[8]u32 off=38` | `22 + userListBytes`; 38 only when there is one recipient |
| `CLAUDE.md` §3 tunnel | `[16]u32 serverId [20]u32 conn [24]u32 idx` | `PlanetId` / unread / **Ticket** |
| `CLAUDE.md` §3 tunnel | "Tunnel packets round-robin across all links" | per-user affinity, `bypassLinks[UserDbId % 24]`; round-robin is CPU-thread assignment |
| `CLAUDE.md` §3 0x13F6 | `[8]u64 gameId` | the PDL field is `WorldClient` (`User+0x4038`), distinct from `GameId` (`User+0x5718`) though equal in this build |
| `WorldBridge.TunnelKeyOffset` comment | "the two-login capture will settle which field" | settled: it is the Ticket, and 24 is right |
| `WorldEntry` `[80..83]` comment | "AllocateTunnelKey() assigns 5, 6, 7..." | it returns a constant 5; diff (2) makes the comment true |

---

## 10. Open questions

1. **Does World validate `ArbiterUser`/`ArbiterClient`?** We send zeros. The Arbiter validates
   them on the way back with a pointer-liveness set; World's side is unreadable here.
2. **Does World require the tunnel on the bypass link?** Q2 above.
3. **`UserList` entry+4.** Q1 above.
4. **Is `maxUsers * 4` the ticket table size in a deployment we care about?** Only matters if
   TeraSharp ever hands out a Ticket larger than World's table — `GetSession` has no bounds
   check, so an out-of-range Ticket is an OOB read on the real Arbiter. Starting at 5 and
   incrementing is safe for any realistic player count, but do not seed it from a database id.
5. **Cross-planet (`PlanetId != 2800`) recipients.** The Arbiter drops entries whose PlanetId
   is not its own. TeraSharp should do the same rather than treating it as a Ticket; with one
   planet it never fires.
