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
