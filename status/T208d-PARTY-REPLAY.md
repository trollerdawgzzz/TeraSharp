# T208d - replay the party table to a World that links late

T208c left the stale-party case refused. This makes it work.

## 1. What was missing

`PartyWiring.RouteWorldAction` already broadcasts the party mirror opcodes to **every World that is
connected at the time** (`AS_DO_CREATE_PARTY`, `AS_DO_ADD_PARTY_MEMBER`, the removes, the dismisses,
`AS_DO_SET_LOOTING_METHOD`, …). Nothing replayed the table to a World that connected **later**, so in
cap_bg4 world 10 - up at 10:07:01, parties formed 10:04:27 and 10:04:34 - had never heard of either
handle and answered our `ABS_CREATE` with `BSA_CREATE_LOG` party list `0 / 0`.

## 2. The native path

| Function | Where | What |
|---|---|---|
| `PartyManager::OnConnectWorldServer(int)` | Arb_part_079.c:16418 (`FUN_140920480`) | takes the party-table lock, walks the whole party list, calls the next one per party |
| `Party::UnicastPartyInfoToSpecialWorldServer(int)` | Arb_part_067.c:15448 (`FUN_1407c7580`) | collects the party's live member slots - all 30, skipping empties - into a `vector<PartyMemberBasicInfo>` |
| `FUN_1407ae9d0` | Arb_part_066.c:18282 | **one `AS_DO_CREATE_PARTY`** carrying that whole roster (`PKT_AS_DO_CREATE_PARTY_WRITE<vector<PartyMemberBasicInfo>>`) |
| `FUN_1407af2b0` | Arb_part_066.c | then **`AS_DO_SET_LOOTING_METHOD`** for the same party |

Two things worth noting:

- **No per-member `AS_DO_ADD_PARTY_MEMBER`.** The roster inside the create frame *is* the member
  list. That is also how our own live create path already works.
- The guard is `worldServerId == 0 || party.OwnerPlanetId == thisPlanet` (Arb_part_067.c:15452
  against `DAT_140e2d020`). One planet here, so every party qualifies.

## 3. What landed

| File | Change |
|---|---|
| `World/PartyManager.cs` | `BuildWorldConnectReplay()` - one `AS_DO_CREATE_PARTY` (full roster) + one `AS_DO_SET_LOOTING_METHOD` per live party, ordered by party id. A party with no members left describes nothing. |
| `World/PartyWiring.cs` | `ReplayToWorld(int worldId)` - unicast, through `WorldBridge.SendFrame(worldId, …)`, the same per-World send the live mirror uses. Logs once at Information with the party and frame counts. |
| `World/DbProxyHandlers.cs` | `OnWorldReady` calls it, beside the other per-World replays, with `WorldRouting.WorldIdOf(link)`. |

## 4. Why this cannot double-create

It runs **once, at the moment a World connects**, so it can only describe parties that already
exist. A party formed afterwards is mirrored by the live broadcast path instead. No World is ever
told about the same party twice, which is exactly what made the connect hook the right place and a
per-(party, world) ledger unnecessary - the open question T208c §5 stopped on.

## 5. Tests

`Tests/T208d.cs`, 2 tests:

- `T208d_party_formed_before_a_world_links_is_replayed_on_connect` - two parties form while only
  world 0 is up, world 10 links afterwards, the replay hands it both rosters (create then looting,
  by party id, the whole three-member roster in the one create frame), nothing goes to world 0, then
  the BG create names those two handles and the `BSA_CREATE_LOG` that comes back with them is
  **not** a refusal.
- `T208d_world_connect_replay_is_one_create_and_one_loot_per_party` - the builder alone: frame
  count, opcode order, party ids, member bytes.

## 6. Not verified by a build

The suite was not run for this change: this session has no shell on the deploy box, and the cloud
container cannot restore NuGet, so `TeraSharp.Arbiter` will not build there. Everything above is
read from the source and the decompile.

## T208d-b - the roster size the test asserted was wrong, the builder was not

`T208d_party_formed_before_a_world_links_is_replayed_on_connect` failed on master HEAD at
"the whole three-member roster travels in the one create frame". The frame carried
**320 B in a 376 B frame** - 2 x 0xA0 - and that is correct: each party in the fixture is a leader
plus one QA dummy.

**Does a QA dummy count as a live slot?** Yes - occupancy is the only test.
`Party::UnicastPartyInfoToSpecialWorldServer` (Arb_part_067.c:15455-15478) walks `party+0x1c8`
for `0x1e` = 30 slots at stride 0x30 ints and accepts a slot when
`slot[0] != -1 && slot[1] != 0`. There is no online, session, dummy or planet test inside that
loop; each accepted slot appends one 0xA0-byte `PartyMemberBasicInfo` to the vector handed to the
single `AS_DO_CREATE_PARTY` write (`FUN_1407ae9d0`), and `AS_DO_SET_LOOTING_METHOD`
(`FUN_1407af2b0`) follows for the same party under `param_2 < 0x20` and session state 2.

`PartyManager.BuildWorldConnectReplay` already does exactly that - `party.Members()` yields every
non-null slot with no filter - and its own unit test
(`T208d_world_connect_replay_is_one_create_and_one_loot_per_party`) asserts 2 members for the same
construction and always passed. So the fix is in the test, not the code.

The assertion now reads the roster size off the party table
(`PartyWiring.Manager.FindById(partyId)!.Count`) instead of hard-coding one, so it proves the frame
dropped nobody whatever the fixture holds, and reports the observed byte count when it fails.
`0x38 + count * MemberBasicInfoSize` is kept as the frame-length check.

Suite on master HEAD with this change: **1120 passed, 0 failed, 84 skipped**, 4 pre-existing warnings.
