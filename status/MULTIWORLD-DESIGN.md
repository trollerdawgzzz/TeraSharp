# Multi-World (T102, research - no code changed)

Sources: `Executable/ServerConfig.xml`, `Executable/DeploymentConfig.xml`, the `.bat` launchers,
`ArbiterServer.exe.c` (Arb_part_*), `world_decompiled/WorldServer.exe.c`, and the current
`World/WorldBridge.cs`. Opcodes are from `D:\packetlogs\world_opcodes.txt`, layouts from the PDL
dumpers (offsets below are FRAME-relative: payload index = offset - 6).

## 0. There is only one WorldServer.exe

Every "extra server" is the same binary with `--id=N`, and `ServerConfig.xml/WorldServerList` says
what that id is:

| Launcher | argv | WorldServerList | type | Continents |
|---|---|---|---|---|
| `2. WordlServer.bat` | (none) = 0 | `<WorldServer id="0" loadAllContinents="true">` | normal | all |
| `6. BattleField.bat` | `--id=10` | `type="battlefield"` | battlefield | 102,103,110,112,113,115-118,1200 |
| `5. PartyMatching.bat` | `--id=11` | `type="partyMatching"` | partyMatching | (none listed) |
| `4. DungeonOther.bat` | `--id=12` | `type="dungeon"` | dungeon | 9920,3023,3027,3126,3026 |
| `3. DungeonServer.bat` | `--id=13` | `type="dungeon"` | dungeon | (none listed = the rest) |
| - | - | `<WorldServer id="31" type="battlefield">` | battlefield | 156 |

The type string is parsed into a **bitmask** (`WorldServer.exe.c:193013`): `normal=1`,
`battlefield=2`, `partyMatching=4`, `dungeon=8`. It is a World-side setting only - it never appears
on the wire. **The Arbiter learns nothing but the WorldId**, and infers the rest from its own copy of
`WorldServerList`.

Connection counts differ by type (`ServerConfig.xml/WorldServerConfig/Capacity`):
`arbiterClient="24"` (normal), `dungeonArbiterClient="4"`, `bfArbiterClient="2"`;
the Arbiter accepts `worldSessions="64"` in total. Every one of those sockets is a full
Arbiter<->World link that registers separately.

`DeploymentConfig.xml` points the main World at `ArbiterServer port="7812"` (the human's tap), but
`DungeonServerConfig` and `BattleFieldServerConfig` have their own `<ArbiterServerList>` pointing
straight at **7802** - so today a dungeon/BF world would bypass the tap entirely. Capture plan
below fixes that first.

## 1. Registration: the WorldId is already on the wire

| Op | Name | Dir | Fields (frame offsets) |
|---|---|---|---|
| 0x138A | `SA_REGISTER` | W->A | `IsBypass u8 @6`, `PlanetId i32 @7`, **`WorldId i32 @0x0B`**, `TotalBypassCount i32 @0x0F`, `BypassIndex i32 @0x13`, `WorldVersion i32 @0x17` (fixed 27) |
| 0x138B | `AS_REGISTER` | A->W | `IsBypass u8 @6`, **`WorldId i32 @7`**, `BypassIndex i32 @0x0B`, `ArbiterVersion i32 @0x0F`, `Result i32 @0x13` (fixed 23) |
| 0x164C / 0x164D | `AS_REQUEST_WORLD_SERVER_STATUS` / `SA_WORLD_SERVER_STATUS` | both | per-World load, the input to "which World hosts this instance" |
| 0x157C / 0x157D | `AS_/SA_AM_I_WORLD_CONTROL_ARBITER` | both | `ContextId i32 @6` - which Arbiter owns a shared World |

Each of the 24 links sends its own `SA_REGISTER` carrying the **same WorldId** and its own
`BypassIndex` of `TotalBypassCount`. That is the whole handshake difference between a main World, a
dungeon World and a BF World: **same messages, different WorldId, fewer links**.

`0x13F2 DSA_DUNGEON_TIMELINE_OPEN_INFO` -> N x `0x1581 AS_DUNGEON_TIMELINE_ON_OFF` is not
type-specific either: any World that owns dungeon continents registers its open-state list once and
then sends the 14-byte empty form as a heartbeat (909 of 911 frames in our captures). A dungeon
World will send a *populated* first one for its own continents only - that is the thing to capture.
Note the prefixes name a subsystem, not a process: `DSA_`/`BSA_` frames arrive on the main World's
links today, and `0x13E5` - which `WorldBridge` calls `OpHeartbeat6` and drops - is really
`BSA_REQUEST_BOUNTY_HUNT_SEASON_INFO`.

## 2. Routing a player into an instance on another World

**The destination World is chosen by which socket the Arbiter writes to.** `AS_ENTER_WORLD` has no
worldId field; it has the placement inside that World:

| Op | Name | Dir | Key fields |
|---|---|---|---|
| 0x13BE | `SA_REQUEST_ENTER_DUNGEON` | W->A | `ArbiterUser i64 @6`, `EnterContext`, `DungeonOwnerInfo`, `DontIssueDungeonUniqueId u8 @0xD6` (min 215) |
| 0x13BF | `AS_REQUEST_ENTER_DUNGEON` | A->W | same shape, to the *target* World |
| 0x13C1 | `AS_RESPONSE_ENTER_DUNGEON` | A->W | `UserPDId pdid @6`, `EnterContext`, `DungeonOwnerInfo` (min 214) |
| 0x13C5 / 0x13C6 | `SA_ADD_/REMOVE_DUNGEON_CHANNEL` | W->A | `ContinentId i32 @6`, `ChannelId i32 @0x0A`, `DungeonOwnerInfo` - **this is how an instance is announced**; the Arbiter's channel table is built from these |
| 0x138E | `AS_ENTER_WORLD` | A->W | `ContinentId i32 @0x36`, **`ChannelInstanceId i32 @0x3A`**, `EnterWorldType i32 @0x4A`, `Ticket i32 @0x56` (= payload 80, what we already send), `GameId i64 @0x5A`, `PartyId i64 @0x32` |
| 0x138C / 0x138D | `SA_ENTER_WORLD` / `_FAIL` | W->A | ack: `ArbiterClient i64 @0x0E`, `ArbiterUser i64 @0x16` |
| 0x1392 / 0x1393 | `AS_LEAVE_WORLD` / `SA_LEAVE_WORLD` | both | the hand-off out of the old World, already implemented |
| 0x13C2 / 0x13C3 | `SA_/AS_REQUEST_LEAVE_DUNGEON` | both | the way back |
| 0x1390 | `AS_FORCE_ENTER_DUNGEON_ID` | A->W | already sent by us at enter-world |
| 0x157A | `AS_RESET_AND_ENTER_DUNGEON` | A->W | reset + enter in one |
| 0x13CB | `SA_ENTER_BATTLE_FIELD` | W->A | the BF equivalent of 0x13BE |

So the sequence is: World A sends `SA_REQUEST_ENTER_DUNGEON`; the Arbiter picks a World that
registered a matching `SA_ADD_DUNGEON_CHANNEL` (or asks one to make an instance), runs the normal
`AS_CANCEL_SKILL_STRICTLY` -> `AS_LEAVE_WORLD` -> `SA_LEAVE_WORLD` -> `AS_ARBITER_USER_DELETE`
teardown on World A, then sends `AS_ENTER_WORLD` **on World B's links** with B's ContinentId and
ChannelInstanceId. The client is told nothing directly: it stays on the same Arbiter TCP session and
simply receives `S_LOAD_TOPO` and the enter-world burst through the tunnel, now originating from
World B. **There is no client-visible server switch** - which is why the tunnel key must stay unique
across Worlds.

## 3. PartyMatching is two different things

- **World id 11 (`type="partyMatching"`)** is an ordinary World on the AS_/SA_ link. Its traffic is
  `SA_ADD_TO_INTER_PARTY_MATCH_POOL` (0x13A9), `SA_DEL_FROM_INTER_PARTY_MATCH_POOL` (0x13AA),
  `AS_VIEW_INTER_PARTY_MATCH_DUNGEON_LIST_EXTENDED` (0x1644),
  `..._BATTLEFIELD_LIST_EXTENDED` (0x1645), `AS_MATCH_ROOM_JOIN_EXTENDED` (0x1643),
  `SA_CHECK_MANUAL_PARTY_MATCH_ENTER_DUNGEON` (0x1620),
  `SA_PARTY_MAKE_RESULT_FROM_INSTANCE_SERVER` (0x13EF).
- **The AM_/MA_ family is NOT that process.** It is the separate `MatchServer.exe` (0x46xx range) on
  its own socket: `DeploymentConfig.xml` gives Arbiter `<Match ip="127.0.0.1" port="7803">` and
  `MatchServerConfig/Listen portForArbiter="7803"`, `Capacity arbiterSessions="10"`. The Arbiter
  dials out. Messages: `AM_REGISTER` (0x4651) / `MA_REGISTER`, `AM_ADD_TO_PARTY_MATCH_POOL` /
  `MA_ADD_TO_PARTY_MATCH_POOL` + `_FAIL`, `AM_DEL_FROM_PARTY_MATCH_POOL` /
  `MA_DEL_FROM_PARTY_MATCH_POOL`, `MA_MATCH_PROGRESS`, `MA_MATCH_POOL_INFO`, `MA_MATCH_ROOM_INFO`,
  `MA_MATCH_CANCEL`, `MA_FIN_PARTY_MATCH`, `AM_PRE_PARTY_MAKE_RESULT` (0x465C),
  `AM_PARTY_MAKE_RESULT` (0x465E), `AM_PARTY_MAKE_RESULT_FROM_INSTANCE_SERVER` (0x465F),
  `AM_GET_BATTLEFIELD_ID` (0x4653), `AM_WORLD_SERVER_STATUS`, `AM_DICONNECT_BATTLE_FIELD_SERVER`
  (sic), `MA_SYSTEM_MESSAGE_USING_INTER_PARTY_MATCH`.

TeraSharp's own party-matching board (T78) is RAM-only and speaks neither family, so nothing here is
regressed by leaving MatchServer unimplemented - but cross-World matching cannot work without it.

## 4. What breaks in WorldBridge today

Every one of these is a single-World assumption, not a bug:

1. `_links` is a flat `List<WorldLink>` (`WorldBridge.cs:86`) with no WorldId. A second World's
   links land in the same list.
2. `SendFrame` picks `_links[0]` as "the primary" (`:410`). With two Worlds that is whichever
   process connected first.
3. `IsReady` is one global flag set by the first `0x294F`, and it resets `_gameIdSeq` and fires
   `DbProxy.OnWorldReady(link)` (`:400`). A second World's handshake would re-zero the game-id
   sequence under a live main World.
4. `SA_REGISTER` (0x138A) is answered from the **capture replay table**, so every link gets the
   captured `AS_REGISTER` - captured WorldId and BypassIndex included. World 13 would be told it is
   world 0.
5. The tunnel map `_tunnels` is keyed by Ticket alone, and `AllocateTunnelKey` is one global
   `TicketAllocator`. Tickets are per-World in the real protocol (they index that World's bypass
   slots), so two Worlds will collide, and the "broadcast to all links on unknown key" fallback
   would deliver another World's packet.
6. `_players` (gameId -> GameSession) has no record of which World a player is in, so leave/enter
   hand-off has nowhere to read the source World from.
7. `SA_WORLD_SERVER_STATUS` (0x164D) is in the do-not-log list and dropped - that is the load feed
   an instance allocator needs.
8. `SA_ADD_DUNGEON_CHANNEL` / `_REMOVE` are unhandled, so there is no channel table at all.

## 5. Capture plan

One session, tap on every link (the point is to see two Worlds at once):

1. Edit `DeploymentConfig.xml`: point `DungeonServerConfig/ArbiterServerList` and
   `BattleFieldServerConfig/ArbiterServerList` at the tap port (7812), as `WorldServerConfig`
   already is. Keep a `.orig` copy - there is one already.
2. Start in order: ArbiterServer, TopographyServer, WorldServer (id 0), **DungeonServer (id 13)**,
   then PartyMatching (id 11). Leave BattleField out of run 1 - three Worlds is harder to read.
3. Reframe with the existing `tools/reframe-tap.ps1`; expect ~24 + ~4 registrations.
4. In game, with one character: (a) log in and stand in a town - baseline; (b) enter a solo dungeon
   from the world map; (c) run out / use the exit button; (d) re-enter the same dungeon; (e) with a
   second client, party up and enter together.
5. What to pull out of it: every `SA_REGISTER` (WorldId, TotalBypassCount, BypassIndex per link);
   the first populated `0x13F2` from id 13 vs from id 0; every `SA_ADD_DUNGEON_CHANNEL`; the whole
   0x13BE/0x13BF/0x13C1 exchange; both `AS_ENTER_WORLD` frames (town vs dungeon) diffed at
   ContinentId/ChannelInstanceId/Ticket/EnterWorldType; and whether the Ticket values from world 0
   and world 13 overlap - that single fact decides point 5 above.

## 6. WorldBridge changes - diff outline (no code yet)

- **`WorldLink`**: add `int WorldId` and `int BypassIndex`, filled from `SA_REGISTER` before any
  other frame is processed; refuse frames on a link that has not registered.
- **`WorldBridge` -> `World` + `WorldRegistry`**: split the class. A `World` owns what is per
  process today - `_links`, `IsReady`, `_gameIdSeq`, the tunnel map, the `TicketAllocator`, the
  `DbProxy` "ready" hook. `WorldRegistry` owns `Dictionary<int, World>` plus the channel table
  (`continentId, channelId -> worldId`) built from `SA_ADD_/REMOVE_DUNGEON_CHANNEL`.
- **`SendFrame(op, payload)`** becomes `SendFrame(worldId, ...)`; the `_links[0]` primary goes away.
  Per-player sends resolve the World from the session.
- **`GameSession`**: add `CurrentWorldId` (human-owned file - describe, do not edit). Tunnel
  delivery keys on `(worldId, ticket)`, and the unknown-key broadcast is scoped to one World.
- **`AS_REGISTER`** comes out of the replay table and is built: echo the link's own WorldId and
  BypassIndex, our ArbiterVersion, `Result = 0`.
- **New handlers**: 0x13C5/0x13C6 (channel table), 0x164D (per-World load), 0x13BE ->
  pick a World -> 0x13BF/0x13C1, and the leave/enter hand-off reusing the existing
  0x1460/0x1392/0x1393/0x1433 teardown before `AS_ENTER_WORLD` on the new World's links.
- **`OnWorldReady`** fires per World; the dungeon-open burst is answered per World from that
  World's own `0x13F2`, not from the static 98-id list.

None of this is worth starting before the capture: the ticket-collision question (point 5) decides
whether the tunnel map needs a compound key or just a per-World allocator.

## 7. Step 2 (T108) - what is built, and what still needs the capture

New `World/WorldInstances.cs` (Cowork-owned) plus `WorldRuntime` in `World/WorldRegistration.cs`:

| Piece | What it is |
|---|---|
| `WorldRouting` | Two hooks - `WorldIdOfLink`, `SendToWorld` - that `WorldBridge` fills in. Null = every link is world 0 and nothing can be routed, i.e. the tree as it stands. |
| `DungeonChannels` | The instance registry. `(ContinentId, ChannelId) -> WorldId` from 0x13C5, dropped by 0x13C6, plus `MapContinent` for the static `WorldServerList` half. |
| `DungeonTransfers` | `PDId -> the World that sent the 0x13BE`, so 0x13C1 goes back to the World the user is still in. |
| `DungeonRouting` | `RouteRequest` / `RouteResponse` / `Dispatch`, and `WorldForEnterWorld` for the 0x138E that follows. |
| `WorldRuntime` | Per-World `IsReady`, the game-id counter, and the one-shot `MarkReady` that `DbProxy.OnWorldReady` hangs off. |

Layouts pinned this task (payload index = frame offset - 6):

| Op | Payload |
|---|---|
| 0x13C5 `SA_ADD_DUNGEON_CHANNEL` | `+0 i32 ContinentId`, `+4 i32 ChannelId`, `+8 DungeonOwnerInfo` (24 B: u64, u8, 3 pad, u64, i32); min 32 |
| 0x13C6 `SA_REMOVE_DUNGEON_CHANNEL` | `+0 i32 ContinentId`, `+4 i32 ChannelId`; min 8 |
| 0x13BE/0x13BF/0x13C1 | already pinned in T10; `DungeonEnterContext[0]` (payload 8) is the ContinentId the real handler routes on |

`Handler_SA_REQUEST_ENTER_DUNGEON` (Arb_part_062.c:12933) picks the target with
`FUN_14082e730(ContinentId)` and builds the PDId as `CONCAT44(User+0x120, DAT_140e2d020)` -
`[u32 planet][u32 playerId]`, which is what `BuildAsRequestEnterDungeon` already writes.
`Handler_SA_RESPONSE_ENTER_DUNGEON` (:13707) finds the user from the PDId's HIGH 32 bits and
answers on **that user's own** World session - the source World, not the responder.

**Single-World is unchanged by construction.** Both tables start empty and both hooks start null,
so every routing decision resolves to the link the frame arrived on - the exact call
`DbProxyHandlers` made before T108. `T108_with_one_world_the_dungeon_handshake_is_unchanged`
pins that against the same capture bytes the T10 tests use.

**Still open, and still waiting on the capture in section 5.**

1. **Nobody allocates an instance.** Routing only finds a World that has already announced a
   channel (or is configured for the continent). The first entry into a continent no World has
   announced still resolves to the asking World. The real allocator reads
   `WorldServerList`/`AS_REQUEST_WORLD_SERVER_STATUS` (0x164C/0x164D), which is dropped today.
2. **The ticket space is per World but the tunnel map is not.** `_tunnels` is still keyed by
   Ticket alone. Whether that needs a compound key is section 5 point 5, unanswered.
3. **`GameSession` has no `CurrentWorldId`.** Until it does, the leave/enter hand-off cannot name
   its source World, and `TunnelFromClient` still sends to world 0.

### 7.1 There is no allocator - 0x164C/0x164D feeds MatchServer, not the Arbiter

Section 7's open item 1 said "nobody allocates an instance" and pointed at the per-World load
feed. That was the wrong guess. **The Arbiter does not balance anything: a continent belongs to
exactly one World, from config.**

`WorldSessionManager::GetDataSession(int continentId)` (Arb_part_046.c:2545) is the lookup
`Handler_SA_REQUEST_ENTER_DUNGEON` uses to pick the target World, and it is a config read:

| Step | Behaviour |
|---|---|
| hash the continentId in the PlanetInfo registry | miss -> `GetDataSession(%d): invalid continentId`, null |
| read that continent's `worldServerInfo` list | empty -> `GetDataSession(%d): no worldServerInfo in PlanetInfo`, null |
| **count != 1** | assert at `WorldSessionManager.cpp(356)`, null - **one continent, one World** |
| `worldServerInfo[0]` | the worldId, guarded `< 0x20`, else `GetDataSession(%d): invalid worldServerId=%d` |
| | then the World session for that id |

(There is a fourth path for `TBA` worlds: `TBA world session should be accessed through worldId,
not continentId`.)

The only health notion the Arbiter keeps is boolean. `WorldSessionManager` holds a fixed **32-slot
array** (base +0x30, stride 0x38); `GetWorldServerStatus(worldId)` is `slot.state == 2`, and 2 is
written in exactly one place - Arb_part_046.c:9455, when a World's last bypass session registers,
next to `WorldServerSession Registered [id=%2d]`. That 0x20 ceiling is the same one T103 pinned in
`Handler_SA_REGISTER`.

**So what is the status feed for?** MatchServer.

| Op | Dir | Shape |
|---|---|---|
| 0x164C `AS_REQUEST_WORLD_SERVER_STATUS` | A->W | **no payload at all** - 6-byte frame. Its dumper (FUN_140191710, Arb_part_011.c:19764) writes the name and returns. |
| 0x164D `SA_WORLD_SERVER_STATUS` | W->A | guard `param_3 < 0x16` -> min frame 22. `+6 i32 (list count, the handler never reads it)`, `+0x0A i32 first element offset (frame-relative)`, `+0x0E i32 PlanetId`, `+0x12 i32 WorldId`, then N x 16-byte `InstanceList` elements `[i32 here][i32 next][i32 A][i32 B]` |
| 0x4670 `AM_WORLD_SERVER_STATUS` | A->Match | what the handler actually produces |

The Arbiter sends 0x164C from two places and stores nothing from the answer:

1. **Arb_part_046.c:9494** - one shot on that World's own link, the instant its last bypass
   session registers and the slot goes to state 2.
2. **Arb_part_070.c:8723** - broadcast to every World session when the MatchServer link comes up
   (it sits immediately after an `AM_COMMAND` 0x4669 send).

`Handler_SA_WORLD_SERVER_STATUS` (Arb_part_062.c:18812) looks up the MatchServer session, returns
0 and sends nothing if there is none, and otherwise writes
`[i32 count][i32 firstOffset][i32 ourPlanetId = DAT_140e2d020 = 0x0AF0][i32 PlanetId]
[i32 WorldId][i32 2]` plus every InstanceList element copied through as `[here][next][A][B]`.
It is a relay. Nothing in the Arbiter consumes the numbers.

**What this changes for TeraSharp**

1. `DungeonChannels.MapContinent` is the **primary** mechanism, not a fallback. Load it from
   `ServerConfig.xml/WorldServerList` at startup and cross-World dungeon entry works on the first
   attempt, with no announced channel and no allocator. That is now the next step, and it needs no
   capture - the file is on disk.
2. `WorldForContinent` prefers an announced channel over the configured owner. The two can only
   disagree in a configuration the real Arbiter rejects outright (two Worlds on one continent), so
   it is harmless; swap the two lookups if we ever want to mirror the binary exactly.
3. **Do not implement 0x164D yet.** Dropping it (it is in `QuietInLog` today) costs nothing while
   there is no MatchServer, and answering it would mean sending 0x4670 to a session that does not
   exist. It is the right shape to build with MatchServer, not before.
4. Still unknown, and still the capture's job: the two i32s in each `InstanceList` element, and
   whether a dungeon World sends a populated list at all.

### 7.2 Step 3 (T111): the config seed, owner preference, and the reviewed patch

**The allocator is now wired.** New `World/WorldServerList.cs` parses ServerConfig.xml's
`<WorldServerList>` and seeds `DungeonChannels.MapContinent`. The deployment's own list, and
what the seed makes of it:

| Row | type | continents | effect |
|---|---|---|---|
| id 0 `loadAllContinents="true"` | - | none listed | becomes `CatchAllWorldId` |
| id 10 | battlefield | 102 103 110 112 113 115 116 117 118 1200 | 10 mappings |
| id 11 | partyMatching | none | owns nothing |
| id 12 | dungeon | 9920 3023 3027 3126 3026 | 5 mappings |
| id 13 | dungeon | none | owns nothing |
| id 31 | battlefield | 156 | 1 mapping |

16 continents mapped, catch-all world 0. A continent claimed twice keeps the FIRST owner and
logs; the real Arbiter asserts at `WorldSessionManager.cpp(356)` and routes the continent
nowhere, which would strand players on a config typo. An id past the `0x20` ceiling
`Handler_SA_REGISTER` enforces is ignored. A missing or unparsable file seeds nothing - that is
a single-World server, not an error. The path is `TERASHARP_SERVERCONFIG`, else
`<TERASHARP_DATA>\Executable\ServerConfig.xml`; the default mirrors `Program.DataRoot`, which is
human-owned, so it is spelled out a second time rather than threaded through a constructor.

**`WorldForContinent` now prefers the configured owner** over an announced channel, which is
what section 7.1 said the binary does. The channel table is still exact about *which* instance a
World announced (`WorldForChannel`); it is only the continent-level question that config wins.

**A third hook, and the reason for it.** `WorldRouting.IsLive(worldId)` - `WorldBridge.HasLinks`
once the patch lands, and `false` for everything before it. Seeding the config without it would
be a live regression: ServerConfig.xml gives continent 102 to world 10 whether or not anyone
started world 10, and routing AS_ENTER_WORLD to a World with no sockets means the player simply
never loads. Every routing decision - `RouteRequest`, `Dispatch`, `WorldForEnterWorld` - now
requires the target to be connected and otherwise stays with the asker or the catch-all. That is
what makes "seed the config on a single-World server" a no-op rather than a gamble.

**`status/MULTIWORLD-PATCH.diff` is regenerated** against master of 2026-09-20 and now covers
three human-owned files: `WorldBridge.cs` (631 lines), `Handlers/WorldEntry.cs` (316) and
`Network/GameSession.cs` (355). 24 hunks, `git apply --check -p1` and `patch -p1 --dry-run`
both clean, and the applied result byte-compared against the intended files. New in it beyond
T109:

- `GameSession.CurrentWorldId`, defaulting to world 0, set by `WorldEntry` before the Ticket is
  allocated and carried into `UnregisterPlayer` and `TunnelFromClient`.
- **The tunnel map is keyed `(WorldId, Ticket)`** - section 4 item 5. Every `uint`-keyed entry
  point (`RegisterTunnelRoute`, `UnregisterTunnelRoute`, `ResetTunnelSequence`, `RouteToClient`,
  `UnregisterPlayer`, `TunnelFromClient`) keeps its old signature as an overload onto world 0,
  so no existing tunnel test moves.
- `WorldEntry` builds AS_ENTER_WORLD with no Ticket, reads the destination World out of it
  (continent at payload 48, instance at 52), allocates the Ticket in that World's space and
  stamps it at payload 80. The bytes are unchanged: `tunnelKey` is written in exactly one place,
  and `EnterWorldTicketOffset` is the constant T21's retry builder already uses.

**Step 4, and it is small.** The per-player control frames still go out on world 0:
`AS_CANCEL_SKILL_STRICTLY` (0x1460), `AS_LEAVE_WORLD` (0x1392), `AS_ARBITER_USER_DELETE`
(0x1433), `AS_LOAD_TOPO_FIN` (0x138F), `AS_FORCE_ENTER_DUNGEON_ID` (0x1390) and
`AS_UPDATE_VISITED_SECTION_LIST` (0x1439). Each is a one-line `WorldBridge` method that needs a
worldId threaded from the session, and none of them matters until a player is actually in a
second World.
