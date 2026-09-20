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

#### T112 - the control frames, and a note on applying the patch

Step 4 from the end of 7.2 is done, in the patch. The six per-player control frames now carry
`GameSession.CurrentWorldId`:

| Op | Name | Where it goes now |
|---|---|---|
| 0x1460 | `AS_CANCEL_SKILL_STRICTLY` | `NotifyPlayerLeave(worldId, ...)` |
| 0x1392 | `AS_LEAVE_WORLD` | same call |
| 0x1390 | `AS_FORCE_ENTER_DUNGEON_ID` | `NotifyTopoLoaded(worldId, ...)` |
| 0x138F | `AS_LOAD_TOPO_FIN` | same call |
| 0x1439 | `AS_UPDATE_VISITED_SECTION_LIST` | `HandlerRegistry`'s C_LOAD_TOPO_FIN arm, `SendFrame(s.CurrentWorldId, ...)` |
| 0x1433 | `AS_ARBITER_USER_DELETE` | **already right** - the SA_LEAVE_WORLD reply goes out on the arriving link, and GameSession's copy took CurrentWorldId in T111 |

Every one addresses the user by id *inside* that World, so the wrong World is not a misroute but
a lookup miss; for 0x1392 that is the `Critical Error LeaveWorld` crash path the `LeaveValues`
comment in WorldBridge already warns about. All the new signatures are overloads - the old
no-worldId forms remain and delegate to world 0, so nothing outside the patch changes.

That adds `Handlers/HandlerRegistry.cs` to the patch: four human-owned files now (WorldBridge
631 lines, WorldEntry 316, HandlerRegistry 408, GameSession 355), 28 hunks.

**Still on world 0, deliberately:** `AS_USER_REQUEST_EXIT` (0x14FF) and
`AS_USER_CANCEL_REQUEST_EXIT` (0x1500). Same one-line change, but one of their callers -
`Handlers/ArbiterClientHandlers.cs:1896` - is Cowork-owned. Giving them a worldId parameter in
the patch would break the build for everyone between now and the moment the patch is applied,
so they move in the same commit as that file.

**Apply the patch with `git apply`, not `patch`.** The file is stored with CRLF throughout,
because its context lines come from CRLF sources. GNU patch strips trailing CRs by default and
then reports `different line endings` on every hunk; `git apply` handles it, and so does
`patch -p1 --binary`. Both of those are checked on every regeneration, against a copy normalised
the way the file actually lands on disk.

## T134 - matchmaking and the battleground lifecycle (classic_live2)

Source: `D:\packetlogs\classic_live2.log` - a LIVE Classic+ reference, 364273 records, carrying a
Corsairs battleground queue -> enter -> play -> leave and a Kelsaik dungeon queue. Its opcode map is
the same 376012 map our 100.02 captures use (spot-checked on nine known opcodes), so the frames
below are directly comparable to ours.

### Decoded

| packet | op | n | decode |
|---|---|---|---|
| `C_DUNGEON_COOL_TIME_LIST` | `0xD3F7` | 9 | EMPTY body - the 4-byte frame is the packet |
| `S_DUNGEON_COOL_TIME_LIST` | `0xD768` | 9 | `array dungeons{u32 id,u32 type,u32 cooldown,i16 entriesDay,i16 entriesWeek}` + `array battlegrounds{u32 id,u16 entries}`. **All nine replies are empty** (`00 00 00 00 00 00 00 00`) |
| `C_DUNGEON_CLEAR_COUNT_LIST` | `0x5C98` | 17 | `string name` - a CHARACTER NAME ("cat"), so the window can be opened on somebody else |
| `S_DUNGEON_CLEAR_COUNT_LIST` | `0x9D66` | 17 | `u32 pid` + `array{i32 id,i32 clears,byte rookie}`. Record 13158 = pid 24468, the fixed 14-id roster; record 19420 = pid 108865, one row |
| `C_MATCH_PROGRESS` | `0xF7B8` | 8 | `array{i32 a,i32 b,i32 c}`, always one element `-10001, 1, 0`. No def ships for it |
| `S_MATCH_PROGRESS` | `0x8BE4` | 8 | `array{i32 x6}`, one element. Record 9386 `10,1,0,1,0,1`; record 12554 `10,1,0,8,17,8` - fields 4 and 5 are **current of total** (8 of 17 in the pool), field 6 tracks field 4 |
| `S_MATCH_ROOM_DELETE` | `0x74F5` | 125 | 16 B body: `array{i32 roomId}` - one room id per frame, a steady drip as other people's rooms close |
| `S_FIN_INTER_PARTY_MATCH` | `0x6470` | 2 | 12 B body `i32 zone / i32 unk1 / i32 unk2`. Record 19524 `10,1,0` (the BG), record 326748 `9075,0,0` (Kelsaik). **The shipped def declares only `int32 zone` and under-declares the other two** |
| `S_ADD_INTER_PARTY_MATCH_POOL` | `0xC730` | 2 | 62 / 79 B. The shipped `.1.def` does NOT decode this build's frame (it yields `type = 2097154`), so the layout is NOT pinned - see below |
| `S_BATTLE_FIELD_USER_LOAD_INFO` | `0xD687` | 58 | 127 B body, fixed size, one per participant during the load screen. Leading `i32 gameId`-ish, then four `i32` ids, then a 4-byte id and a fixed tail |
| `S_RETURN_USER` | `0x6C50` | 9 | **EMPTY** - the 4-byte frame is the whole packet. Fires at logout/zone-out boundaries |
| `S_INSTANCE_ARROW` | `0xC644` | 961 | 48/64/80/96/112 B bodies - two arrays, the second carrying `vec3` waypoints. Pure world content |
| `S_AVAILABLE_EVENT_MATCHING_LIST` | `0x810D` | 3 | 1882 B, all three the same size. `.2.def` decodes it: 12 `uint32`, 3 bytes, 2 `uint32`, `vanguardCredits`, 3 bytes, 4 limit ints, level, then a `quests` array whose elements nest four more arrays |

### CORRECTION: classic_live2's matchmaking frames are the MEMBER side

The capture is from a PARTY MEMBER. The LEADER queued both the battleground and the dungeon, so
every matchmaking frame above is a push the member received - `S_ADD_INTER_PARTY_MATCH_POOL` and
`S_MATCH_PROGRESS` included. The leader's `C_` request and the pool-add as it looked on HIS session
were never on this wire.

That changes what the table above is evidence for. It pins the REPLY half of the contract - what a
party member is told while the leader queues - and pins nothing about the request half. In
particular it explains why `S_ADD_INTER_PARTY_MATCH_POOL` carries the member's own pid (24468, the
same pid `S_DUNGEON_CLEAR_COUNT_LIST` answers with): it is describing the pool to him, not echoing
his request.

**Still needed: a LEADER-SIDE capture** - you queue, from your own client - for
`C_ADD_INTER_PARTY_MATCH_POOL`, `C_DEL_INTER_PARTY_MATCH_POOL` and whatever the leader's own
`S_ADD_INTER_PARTY_MATCH_POOL` looks like. Until that exists, do not infer the request layout from
the frames above, and treat the `.1.def` mismatch noted below as unresolved rather than as evidence
about this build.

### Not implemented, and why

`S_FIN_INTER_PARTY_MATCH` and the instance hand-off need a real match server: the Arbiter does not
decide when a pool fills, and `zone` in FIN is the instance the client is then told to load. Same
for `S_ADD_INTER_PARTY_MATCH_POOL` and `S_MATCH_PROGRESS` - the numbers in them (avg wait, 8 of 17)
are match-server state. They are decoded above as the CLIENT-SIDE CONTRACT: what the client expects
to receive, so that when a match server exists it has a specification rather than a guess.

`S_ADD_INTER_PARTY_MATCH_POOL` is the one gap. Its `.1.def` does not fit this build - decoding
record 9335 with it gives `type = 2097154` and an empty `players` array, while the frame plainly
contains `F0 0A 00 00` (planet 2800) twice and `94 5F 00 00` (pid 24468, the same pid
S_DUNGEON_CLEAR_COUNT_LIST carries). Do not implement it from that def. It needs either a newer def
or a hand trace against `Handler_C_ADD_INTER_PARTY_MATCH_POOL`.

`S_AVAILABLE_EVENT_MATCHING_LIST` decodes, but it is 1882 bytes of vanguard-quest content with a
four-deep nested array. It is a content table, not Arbiter state; serving it means modelling the
vanguard system, which is its own task.

### Implemented (T134)

`ArbiterClientHandlers.BuildDungeonCoolTimeList` and `BuildDungeonClearCountList`, plus
`ReadDungeonClearCountName` and `CharacterStore.GetDungeonClearCounts`. Byte-exact against records
13163 (empty cool time), 13158 (14 rows) and 19420 (1 row).

**NOT APPLIED - `Handlers/HandlerRegistry.cs` is human-owned.** Two lines:

```
        Reg("C_DUNGEON_COOL_TIME_LIST", 0, (s, body) =>
        {
            s.Send(ArbiterClientHandlers.BuildDungeonCoolTimeList());      // T134: all 9 live replies are empty
            return true;
        });
        Reg("C_DUNGEON_CLEAR_COUNT_LIST", 0, (s, body) =>
        {
            var who = Program.Store?.FindCharacterByName(
                ArbiterClientHandlers.ReadDungeonClearCountName(body));     // name -> the character asked about
            ...
            s.Send(ArbiterClientHandlers.BuildDungeonClearCountList(pid, rows));
            return true;
        });
```

The clear-count reply needs the fixed 14-id roster the client expects (9068, 9056, 9168, 9156,
9507, 9043, 9768, 9756, 9868, 9856, 9830, 9810, 9739, 9075) merged with
`GetDungeonClearCounts(ownerId)`: every id in the roster gets a row, `clears` from the store or 0,
`rookie = clears == 0`. The roster is client content, so it belongs beside the other static tables
rather than in the store.

### Cool time: what is still missing

The stored `dungeon_cooldowns.record` is the 52-byte World element
(`status/DUNGEON-COOLTIME.md` section 2), not the client's 20-byte one. Mapping one to the other
needs the DateTime -> `cooldown` seconds rule and the daily-reset boundary, neither of which any
capture pins. Until then `BuildDungeonCoolTimeList()` is called with no entries, which is what the
live server sent in all nine samples - correct for an account with nothing locked, and honest about
the rest.

### T134b - the clear-count reply is wired

`ArbiterClientHandlers.OnDungeonClearCountList(GameSession, ReadOnlyMemory<byte>)` now does the
whole job: read the name, resolve the character (falling back to the session's own on an empty or
unknown name), merge `DungeonClearCountRoster` with `CharacterStore.GetDungeonClearCounts`, and
send. `pid` is the character db id - `LoginHandlers` sets `s.PlayerId = chr.Id` on select, so one
value serves both the session's own character and anyone else's row.

The roster lives in `ArbiterClientHandlers.DungeonClearCountRoster` because it is the same fourteen
ids for every character in every captured reply - client content, not a row set.

**STILL NOT APPLIED - `Handlers/HandlerRegistry.cs` is human-owned.** One line, beside the
cool-time line already there at :285:

```
        Reg("C_DUNGEON_CLEAR_COUNT_LIST", 0, (s, b) => ArbiterClientHandlers.OnDungeonClearCountList(s, b));
```

The cool-time half was applied by hand at :285 and needs nothing further.

## T136 - the leader side, from classic_live3

The leader-side capture T134b said was needed now exists: `D:\packetlogs\classic_live3.log`, the
human queueing Kelsaik (instance **9739**) as party leader, matching, and cancelling once. 56085
records. It pins the REQUEST half, which classic_live2 could not.

### The lifecycle, by record

| # | dir | packet | what |
|---|---|---|---|
| 8643 | C->S | `C_MATCH_ADD` (0xCE5F, 55 B) | the leader queues |
| 8662 | S->C | `S_ADD_INTER_PARTY_MATCH_POOL` (49 B) | the pool holds this party |
| 8664 | S->C | `S_CHANGE_EVENT_MATCHING_STATE` (34 B) | `queued = 1` |
| 8670 | C->S | `C_MATCH_PROGRESS` | asks about instance **-9999** = any |
| 8678 | S->C | `S_MATCH_PROGRESS` (36 B) | |
| 8826 | C->S | `C_MATCH_ROOM_LIST` (0x88D0) | the browse window |
| 8838 | S->C | `S_MATCH_ROOM_LIST` (0x68E0, 456 B) | ten rooms |
| 9183 | C->S | `C_MATCH_PROGRESS` | asks about 9739 by name |
| 9476 | C->S | `C_MATCH_DEL` (0xC057) | the leader cancels |
| 9487 | S->C | `S_CHANGE_EVENT_MATCHING_STATE` | `queued = 0` |
| 9489 | S->C | `S_DEL_INTER_PARTY_MATCH_POOL` (0x72DC) | |
| 9491 | S->C | `S_CHANGE_EVENT_MATCHING_STATE` | `queued = 0` **again** - the client is told twice |
| 10322 | C->S | `C_MATCH_ADD` | queued a second time |
| 10585 | S->C | `S_FIN_INTER_PARTY_MATCH` (16 B) | **match found** |
| 53967 | S->C | `S_CANCEL_PARTY_MATCH_POOL` (0xD756, 12 B) | the separate board-side cancel |

### Corrections this capture forces

1. **`-9999`, not `-10001`.** `F1 D8 FF FF` is `0xFFFFD8F1` = **-9999**, the client's "any instance"
   sentinel in `C_MATCH_PROGRESS` and the value `S_CANCEL_PARTY_MATCH_POOL` echoes. T134's note on
   classic_live2 read it as -10001.
2. **`S_ADD_INTER_PARTY_MATCH_POOL`'s shipped `.1.def` is wrong, and here is the real layout.** The
   def has `players` as a FLAT array beside `instances` plus two leading int32; the wire has
   `players` NESTED inside each instance element, and no leading scalars at all:
   ```
   body 0  u16 count instances / 2 u16 offset instances
        4  u16 count (a second array, 0 in both captured frames) / 6 u16 offset
   instance element 20 B:  +0 u16 here / +2 u16 next
                           +4 u16 count players / +6 u16 offset players   <- NESTED
                           +8 i32 instanceId / +12 i32 / +16 i32
   player element 17 B:    +0 u16 here / +2 u16 next
                           +4 i32 planetId (2800) / +8 i32 playerId / +12 byte / +13 i32
   ```
   Decoding classic_live2 record 9335 with the def gave `type = 2097154`; that is the nested
   count/offset pair (`0x0020`, `0x0002`) being read as one int32. The def is not merely a version
   behind - it is a different shape.
3. **`S_FIN_INTER_PARTY_MATCH`'s field 1 is the INSTANCE id, not a zone.** 9739 here, 9075 and 10
   in classic_live2 - all instance ids from the dungeon roster. The def calls it `zone` and
   declares only that one int32; the body is 12 B, three int32.
4. **`S_CHANGE_EVENT_MATCHING_STATE` is where the queued flag lives.** `[u16 count][u16 offset]`
   quests, then `byte queued`, `byte unk` (1 in every frame), then 8 B elements `[here][next][i32
   questId]`. Between records 8664 and 9487 the ONLY byte that changes is that flag.

### Implemented (T136): `World/MatchQueueManager.cs`

RAM-only. Tracks one `Entry` per party leader (`InstanceIds`, `MemberPlayerIds`, `QueuedAt`),
answers `Progress` from that state, and builds every frame above byte-exact:
`BuildAddInterPartyMatchPool`, `BuildChangeEventMatchingState`, `BuildMatchProgress`,
`BuildDelPool`, `BuildMatchRoomDelete`, `BuildCancelPartyMatchPool`, `BuildFinInterPartyMatch`,
plus `ReadInstanceIds` for the shared `C_MATCH_DEL` / `C_MATCH_PROGRESS` request layout.

**Matchmaking is a stub and says so.** There is no MatchServer, so nothing decides that strangers
belong together: a party that queues with at least `MembersForInstantMatch` members and a non-empty
instance list matches ITSELF immediately and gets `S_FIN_INTER_PARTY_MATCH`. Anything else sits in
the dictionary.

### NOT IMPLEMENTED - the instance hand-off, and it is the next step

`S_FIN_INTER_PARTY_MATCH` is a signal, not a teleport. After it the real stack must:

1. create (or pick) an instance of 9739 on a DungeonServer,
2. tell every party member which server and channel it is on,
3. push the `S_LOAD_TOPO` that moves them into it, and
4. hold the party together across the move, so `C_LOAD_TOPO_FIN` from each member lands in the same
   instance.

None of that exists. TeraSharp has one World and no DungeonServer, so step 1 has nowhere to go -
which is the same gap `status/WORLD-PARTIAL-LOAD.md` describes from the other end. Sending FIN
today makes the client's matching window close and show "match found"; it will then wait for a
load that never comes. **Do not wire FIN into a live server until the hand-off exists.** The
builder is there so the frame is pinned and testable, not so it can be fired.

### NOT DECODED

`S_MATCH_ROOM_LIST` (456 B, record 8838) is ten 44-byte room rows - room id, a dungeon id
(`53 23` = 9043, `73 23` = 9075, `C4 23`, `D0 23`), a timestamp-looking `6A AF F6 67`, and three
trailing int32 flags. The row stride and the id fields are clear; the flags are not, and one
capture of ten rows is not enough to name them. Left for a task with a second sample.

`S_CHANGE_EVENT_MATCHING_STATE`'s big forms (730 B / 1290 B at records 10580/10581) are the same
layout with 90 and 160 quest ids - the full vanguard roster. The builder handles any count.

### NOT APPLIED - registry lines, `Handlers/HandlerRegistry.cs` is human-owned

```
        Reg("C_MATCH_ADD", 0, (s, b) => MatchWiring.OnMatchAdd(s, b));
        Reg("C_MATCH_DEL", 0, (s, b) => MatchWiring.OnMatchDel(s, b));
        Reg("C_MATCH_PROGRESS", 0, (s, b) => MatchWiring.OnMatchProgress(s, b));
        Reg("C_MATCH_ROOM_LIST", 0, (s, b) => MatchWiring.OnMatchRoomList(s, b));
```

`MatchWiring` does not exist yet - the four handlers that glue `MatchQueueManager` to a session
(read the ids, look up the party, send the pair of frames) are the obvious next slice, and they
are small now that every frame is pinned. Wiring `C_MATCH_ADD` before the hand-off exists is the
one that should wait; `C_MATCH_PROGRESS` and `C_MATCH_ROOM_LIST` are read-only and safe.

## T137 - the hand-off capture does NOT contain a cross-server hand-off

`D:\packetlogs\cap_multiworld.log` (tap, 1207 reframed frames) plus `cap_multiworld_client.log`
(14349 client records). The brief's premise was that a player entered a dungeon instance on a
SECOND linked World and was handed over. **The capture does not show that.** Everything below is
measured, and it changes what parts 2 and 3 of T137 can honestly be.

### The links

| link | frames | window | what it is |
|---|---|---|---|
| **#1** | 1087 | 15:52:00 - 16:17:01 | the main World. All gameplay, and **all ten hand-off frames**. |
| **#3** | 117 | 15:56:13 - 16:17:01 | the second server. Gets the full link-up burst (`0x27CF` -> `0x27ED`/`0x27D0`/`0x27D1`/`0x27D2`/`0x27D3`, `0x1592`/`0x1595`, `0x1582`) that #1 got at 15:52:00, then only broadcasts: `0x1581` x34, `0x1453` x8, `0x15FF`/`0x1600` x7. |
| #2, #4, #6 | 1 each | - | `0x164C` twice, `0x27CF` once. Stubs of aborted connections. |

**Link #3 never receives a single per-player frame.** The DungeonServer is linked and idle for the
whole capture. Not one of `0x13BE`, `0x13BF`, `0x13C0`, `0x13C1`, `0x13C5`, `0x13C6`, `0x1460` went
to it.

### What actually happened, byte-exact

All frames below are link **#1**, main World.

| time | dir | op | body (first 32 B) |
|---|---|---|---|
| 16:10:40.824 | A->W#1 | `0x1460` | `01 00 00 00` |
| 16:15:12.446 | W->A#1 | `0x13BE` (215 B) | `20 00 AC CC 7A 02 00 00 63 26 00 00 01 00 00 00 ...` |
| 16:15:12.446 | A->W#1 | `0x13BF` (215 B) | `F0 0A 00 00 01 00 00 00 63 26 00 00 01 00 00 00 ...` |
| 16:15:12.446 | W->A#1 | `0x13C5` (38 B) | `63 26 00 00 08 00 F0 0A 00 00 00 00 ...` |
| 16:15:12.446 | W->A#1 | `0x13C0` (214 B) | same prefix as `0x13BF` |
| 16:15:12.448 | A->W#1 | `0x13C1` (214 B) | same prefix as `0x13BF` |
| 16:16:36.012 | W->A#1 | `0x13C6` (14 B) | `63 26 00 00 08 00 F0 0A` |
| 16:16:55.368 | W->A#1 | `0x13BE` | as above |
| 16:16:55.369 | A->W#1 | `0x13BF` | as above |
| 16:16:55.369 | W->A#1 | `0x13C5` | `63 26 00 00 09 00 F0 0A 00 00 00 00 00 00 00 00 00 00 00 6A ...` |
| 16:16:55.369 | W->A#1 | `0x13C0` | as above |
| 16:16:55.369 | A->W#1 | `0x13C1` | as above |

Decoded:

* `0x13C5` **SA_ADD_DUNGEON_CHANNEL** = `i32 dungeonId / u16 channel / u16 planetId` + 24 B tail.
  `63 26 00 00` = **9827**, the dungeon. Channel **8** the first time, **9** the second.
  `F0 0A` = 2800, the planet.
* `0x13C6` is its counterpart, **REMOVE**: the same `dungeonId / channel / planetId` and nothing
  else - 8 B of body. Channel 8 is added at 16:15:12 and removed at 16:16:36, then 9 is added.
* `0x13BE` -> `0x13BF` and `0x13C0` -> `0x13C1` are two request/reply PAIRS, both on link #1, both
  carrying the same `63 26 00 00` dungeon and `F0 0A 00 00` planet. `0x13BF` and `0x13C1` are the
  Arbiter's replies and go back **to the same link the request came from**.
* `0x1460` fires once, 4.5 minutes before any dungeon, body `01 00 00 00`. It is not part of the
  per-entry sequence.

### What the client saw - and this is the proof

The client stayed on ONE connection the whole time. It entered the dungeon three times:

```
[10471] S_LOAD_TOPO       zone 63 26 00 00 = 9827   coords 00 B8 3D C6 / 00 1C D9 C6 / 00 48 89 C5
[10481] C_LOAD_TOPO_FIN
[10486] S_CURRENT_CHANNEL 63 26 00 00 | 09 00 F0 0A | 00 00 00 00 | 01 00 00 00
...     back to zone 05 00 00 00, channel 01 00 00 00
[12218] S_CURRENT_CHANNEL 63 26 00 00 | 0A 00 F0 0A | ...      channel 10
[14217] S_CURRENT_CHANNEL 63 26 00 00 | 0B 00 F0 0A | ...      channel 11
```

`S_CURRENT_CHANNEL`'s second int32 is a COMPOSITE: low u16 = channel, high u16 = planet id.
Overworld frames carry a plain `01 00 00 00` there; dungeon frames carry `09 00 F0 0A`,
`0A 00 F0 0A`, `0B 00 F0 0A`. That is the only packet that tells the client which dungeon channel
it is in, and an ordinary `S_LOAD_TOPO` is what moves it.

**There is no second S_LOAD_TOPO to another address, no reconnect, and no S_SELECT_USER in the
middle.** Entering a dungeon on this stack is a zone change inside the main World, plus a channel
number. The World creates the channel and TELLS the Arbiter about it (`0x13C5`); the Arbiter does
not route the player anywhere.

### Consequences for parts 2 and 3 of T137 - NOT DONE, on purpose

* **`status/MULTIWORLD-PATCH.diff` cannot be corrected against this capture.** The patch predicts a
  cross-link hand-off; the capture contains none, so there is nothing here to check its bytes
  against. Correcting it from this evidence would mean inventing the disagreement. The patch is
  left exactly as it is.
* **`WorldBridge` / `WorldEntry` / `GameSession` / `HandlerRegistry` are unchanged.** There is no
  corrected patch to apply, and those four are human-owned in any case.
* **`MatchWiring.OnMatchAdd` still refuses and still never emits FIN.** T136b's guard was that FIN
  must not fire until the instance hand-off exists. This capture does not supply it - it shows
  something simpler and different - so the guard stands unchanged.

### What a real cross-server capture would need

The DungeonServer has to actually host something. In this session it linked at 15:56:13 and then
sat idle, so either the box routes all instances to the main World by configuration, or the
dungeon the player picked (9827) is not one the DungeonServer is configured to own. Before
re-capturing, check `Executable\DeploymentConfig.xml`'s `DungeonServerConfig` /
`ArbiterServerList` against which dungeon ids each server claims, then enter one the DungeonServer
owns. If every instance really does run on the main World here, then the same-link sequence above
IS the hand-off, and TeraSharp's single-World design already matches it - in which case
MULTIWORLD-PATCH.diff is solving a problem this deployment does not have.

## T137b - a real cross-World hand-off exists. Half of it was captured.

`D:\packetlogs\cap_multiworld2_ctl.txt` (216 frames) + `cap_multiworld2_client.log` (1389
records). The player entered **Velik's Sanctuary, dungeon `35 26 00 00` = 9781**, hosted on the
DungeonServer. This **supersedes T137's conclusion**: T137 saw dungeon 9827 served by the main
World and concluded no cross-server hand-off happens on this box. It does - for the dungeons the
DungeonServer actually owns.

### The DungeonServer is link #3, and it says so

| time | dir | op | what |
|---|---|---|---|
| 16:44:44.319 | W->A#3 | `0x27CF` | DungeonServer links up; Arbiter answers with the full static burst ON #3 |
| 16:44:44.351 | W->A#3 | `0x294E` | body `00 00 00 00 00 00 00 00 \| 0D 00 00 00` - **13**, i.e. `--id=13`. This is how a World announces its server id. |
| 16:44:45.314 | W->A#3 | `0x164D` (566 B) | `22 00 00 00 16 00 00 00 F0 0A 00 00 **0D 00 00 00** 16 00 00 00 26 00 00 00 D6 0B ...` |
| 16:44:45.707 | **A->W#3** | `0x13BF` (215 B) | `F0 0A 00 00 01 00 00 00 **35 26 00 00** 01 00 00 00 ...` - the Arbiter **PUSHES** to the DungeonServer |
| 16:44:45.707 | W->A#3 | `0x13C5` (38 B) | `35 26 00 00 \| 0C 00 \| F0 0A ...` - dungeon 9781, channel **12**, planet 2800 |
| 16:44:45.708 | W->A#3 | `0x13C0` (214 B) | same prefix as `0x13BF` |
| 16:44:47.314 | W->A#3 | `0x164D` (38 B) | `01 00 00 00 16 00 00 00 F0 0A 00 00 0D 00 00 00 16 00 00 00 00 00 00 00 35 26 00 00` |
| 16:44:49.971 | A->W#3 | `0x138E` (189 B) | the Arbiter hands the player's data over |
| 16:44:49.976 | W->A#3 | `0x2711` | **SDB_USER_ENTERWORLD**: `16 00 00 00 \| 01 00 00 00 \| 01 00 00 00 \| 0C 00 F0 0A 00 00` - userDbId 22, channel 12, planet 2800 |
| 16:44:49.977+ | A->W#3 | `0x2738`, `0x2830`, `0x27A4`, `0x272D`, `0x27F9`, ... | the **entire** per-player DB load burst, repeated on #3 |

**Correction to T137's reading of `0x13BF`.** On the main-World link it was the Arbiter's REPLY to a
`W->A 0x13BE` request. Here there is no `0x13BE` on #3 at all - the Arbiter sends `0x13BF`
unprompted. So `0x13BF` is not "the reply to 13BE"; it is the hand-off push, and on the same-link
path it merely happens to follow a request.

### What the client saw: ONE socket, one S_LOAD_TOPO, no reconnect

```
[   2] C->S C_LOGIN_ARBITER      <- the only login in the whole capture
[  52] S->C S_SELECT_USER
[ 140] S->C S_LOAD_TOPO          zone 5D 1B 00 00 = 7005 (the overworld)
[1192] S->C S_LOAD_TOPO          zone 35 26 00 00 = 9781   <- into the dungeon
[1210] C->S C_LOAD_TOPO_FIN
[1215] S->C S_CURRENT_CHANNEL    35 26 00 00 | 0D 00 F0 0A | 00 00 00 00 | 01 00 00 00
```

**No second `C_LOGIN_ARBITER`, no `S_SELECT_USER` in the middle, no reconnect.** The client never
learns there are two World processes. Crossing to another World is, from the client's side,
byte-identical to T137's same-World zone change: one `S_LOAD_TOPO` plus one `S_CURRENT_CHANNEL`.

### A number that does not match, and matters

`S_CURRENT_CHANNEL` tells the client channel **13** (`0D 00 F0 0A`). The Arbiter<->World frames
carry **12** (`0C 00` in both `0x13C5` and `0x2711`). 13 is also the DungeonServer's `--id`
(`0x294E`). The most likely reading is that the client's field is the **server id**, while the
`0x13C5`/`0x2711` number is an internal channel index - but that is one sample and the two values
are adjacent, so it could equally be an off-by-one in one direction. **Do not hard-code either
until a second dungeon on a differently-numbered server settles it.**

### Parts 2 and 3 still cannot be done - link #1 is not in this capture

`cap_multiworld2_ctl.txt` contains **exactly one frame on link #1**: `W->A#1 0x27CF` at
16:41:58.879. Nothing else, for the entire 3.5 minutes including the dungeon entry. The Arbiter
never even answers that `0x27CF` on #1, while the identical request on #3 gets the full burst
0.002 s later - so the tap lost the #1 stream after its first packet.

That means the capture does not contain:

* the entry request on #1 (whatever the main World sent to start the hand-off),
* which link `AS_ENTER_WORLD` went to on the sending side,
* the leave on #1,
* any Arbiter reply on #1.

So `status/MULTIWORLD-PATCH.diff`'s predictions about **WorldForContinent routing, ticket
handling, which link gets AS_ENTER_WORLD, and the leave on #1** have nothing to be checked
against. The patch is left unchanged for the second time, and `WorldBridge` / `WorldEntry` /
`GameSession` / `HandlerRegistry` are untouched. `MatchWiring.OnMatchAdd` still refuses and still
never emits FIN.

### To finish this, the tap needs both streams

Re-capture with the tap attached BEFORE the main World connects, and confirm both links carry
traffic before entering the dungeon - `0x294E` on each link names its server id, so a quick check
is: both links should show a `0x294E`, one with `00 00 00 00` (main) and one with `0D 00 00 00`.
Then enter Velik's Sanctuary again. Everything else in this capture is reusable; only the #1 half
is missing.

## T137c - THE HAND-OFF, both links, complete

`D:\packetlogs\cap_multiworld3.log` (47436 raw chunks -> **19046 frames** after splitting the
coalesced `[u32 len][u16 op]` stream) + `cap_multiworld3_client.log` (7928 records). The player
entered **Velik's Sanctuary (9781)** three times and came back each time.

Note: `cap_multiworld3_ctl.txt` was never generated - only the raw `.log` exists. Everything below
is parsed from the raw stream directly; the chunks are coalesced, so a naive one-frame-per-chunk
read gets ~5000 of the 19046 frames and misses most of the hand-off.

### Which link is which

`0x294E` is how a World announces itself, and its shape differs by role:

| link | `0x294E` body | role |
|---|---|---|
| #1, #7, #11 | `01 00 00 00 12 00 00 00 00 00 00 00 12 00 00 00 00 00 00 00 01 00 00 00` (24 B) | **main World, id 0** |
| #4 | `00 00 00 00 00 00 00 00 \| 0C 00 00 00` (12 B) | a DungeonServer, **id 12** |
| #9, #14, #16 | `00 00 00 00 00 00 00 00 \| 0D 00 00 00` (12 B) | the DungeonServer, **id 13** |

The main World reconnected as #1 -> #7 -> #11; the DungeonServer as #9 -> #14 -> #16. The live pair
for the first full cycle is **#11 (main) and #14 (dungeon)**.

### The complete cycle, 17:35:38 - 17:36:06

```
17:35:38.020  W->A#11  0x13BE  215  20 00 92 CA 6A 02 00 00 | 35 26 00 00 | 01 00 00 00 ...
17:35:38.020  A->W#14  0x13BF  215  F0 0A 00 00 01 00 00 00 | 35 26 00 00 | 01 00 00 00 ...
17:35:38.021  W->A#14  0x13C5   38  35 26 00 00 | 0D 00 | F0 0A | ...
17:35:38.021  W->A#14  0x13C0  214  F0 0A 00 00 01 00 00 00 | 35 26 00 00 | ...
17:35:38.022  A->W#11  0x13C1  214  F0 0A 00 00 01 00 00 00 | 35 26 00 00 | ...
17:35:39.600  W->A#14  0x164D   38  01 00 00 00 16 00 00 00 F0 0A 00 00 0D 00 00 00 ...
17:35:42.296  A->W#14  0x138E  189
17:35:42.301  W->A#14  0x2711   24  16 00 00 00 01 00 00 00 01 00 00 00 | 0D 00 F0 0A 00 00
   ... the player is in the dungeon, on link #14 ...
17:36:06.142  W->A#14  0x13C6   14  35 26 00 00 0D 00 F0 0A            <- channel released
17:36:06.186  A->W#11  0x138E  189
17:36:06.187  W->A#11  0x2711   24  16 00 00 00 4A 00 00 00 01 00 00 00 | 00 00 00 00 00 00
```

**This is the routing, and it is the thing three captures were trying to show:**

1. The **main World asks** (`0x13BE` on #11). Its first 8 bytes `20 00 92 CA 6A 02 00 00` are the
   session handle/ticket; then the destination dungeon `35 26 00 00` = 9781.
2. **The Arbiter answers on a DIFFERENT link.** `0x13BF` goes to **#14**, the DungeonServer that
   owns 9781 - not back to #11. That one routing decision *is* WorldForContinent.
3. The receiving World registers a channel (`0x13C5`, channel `0D 00` = 13) and sends `0x13C0`.
4. **The Arbiter's `0x13C1` goes back to #11**, the link that asked. So a hand-off is two
   request/reply pairs that cross: `13BE`(#11) -> `13BF`(#14), and `13C0`(#14) -> `13C1`(#11).
5. `AS_ENTER_WORLD` / `SDB_USER_ENTERWORLD` (`0x2711`) fires **on the destination link**, carrying
   `0D 00 F0 0A` - the channel and planet. The whole per-player DB burst repeats there.
6. The leave is the mirror: `0x13C6` on the dungeon link releases the channel, then `0x138E` +
   `0x2711` with channel `00 00 00 00` **on #11** puts the player back on the main World.

The `0x13BF` body is the `0x13BE` body with its first 8 bytes replaced by `F0 0A 00 00 01 00 00 00`
(planet 2800, world 1) - the rest, from `35 26 00 00` onwards including the spawn coordinates
`00 AC 2B 47 / 40 90 03 C8 / 00 1C E3 46`, is copied through unchanged. `0x13C0` and `0x13C1` are
the same 214 bytes in both directions.

### The channel question - RESOLVED, and T137b's guess was wrong

Three entries in this capture, plus cap_multiworld2:

| entry | `0x13C5` / `0x2711` channel | client `S_CURRENT_CHANNEL` |
|---|---|---|
| mw2 16:44 | 12 | 13 |
| mw3 17:35:38 | 13 | 14 |
| mw3 17:36:34 | 14 | 15 |
| mw3 17:50:50 | 15 | 16 |

**The client's number is always the internal channel + 1.** It is a 1-based vs 0-based off-by-one,
NOT the server id - T137b guessed "server id" off a single sample where 13 happened to be both.
The counter also increments per entry and is never reused within a session.

`S_CURRENT_CHANNEL` = `i32 zone / u16 channel / u16 planetId / i32 / i32`. Overworld frames are
`zone 7005, channel 1, planet 0`; dungeon frames `zone 9781, channel 14/15/16, planet 2800`.

### The client, again: one socket, no reconnect

`C_LOGIN_ARBITER` appears once, at record 2. Across three dungeon entries and three returns there
is no second login, no `S_SELECT_USER`, no reconnect - each move is one `S_LOAD_TOPO` plus one
`S_CURRENT_CHANNEL`. **The client cannot tell a cross-World move from a same-World zone change.**
Whatever TeraSharp does here, it must not disturb the client connection.

### Part 2: the patch is not wrong - it is incomplete

`status/MULTIWORLD-PATCH.diff` is unchanged, and this time for a different reason than T137/T137b.
Grepped for `13BE`, `13BF`, `13C0`, `13C1`, `13C5`, `13C6`, `0x2711`, `WorldForContinent`: **none
appear in it**. The patch is entirely about ticket-space partitioning - `PerWorld<TicketAllocator>`,
`_tunnels` keyed `(World, Ticket)`, `GameSession.CurrentWorldId`, `DeliverTunnelPacket(worldId,
...)`. Nothing the capture shows contradicts any of that; per-World ticket spaces are exactly what
step 5 above requires once two links carry tunnelled frames for the same player.

So there is nothing to correct. What the patch does NOT contain is the hand-off protocol itself -
the six-step exchange above. That is a separate, now fully specified piece of work.

### Part 3: what it needs, now that the sequence is pinned

Not done in this task. The remaining work, in order:

1. `WorldBridge`: a `WorldForContinent(int dungeonId)` map built from each link's `0x294E` id plus
   the `0x164D` roster each World sends (the DungeonServer's 566-byte `0x164D` lists the ids it
   owns; the main World's is 2790 bytes).
2. `WorldBridge`: route `0x13BE` from link A to `0x13BF` on link B, and `0x13C0` from B back to
   `0x13C1` on A. Two cross-link pairs, both byte-copies apart from the 8-byte prefix.
3. `WorldEntry`: emit `AS_ENTER_WORLD` on the DESTINATION link with the channel from that link's
   `0x13C5`, and the mirror on leave.
4. `GameSession.CurrentWorldId` + the `(World, Ticket)` tunnel key - **that is what the existing
   patch already does**, so apply it as-is first.
5. Only then wire `MatchWiring.OnMatchAdd` to emit FIN, because only then does FIN lead anywhere.
   The T136b guard stays until step 4 is in.
