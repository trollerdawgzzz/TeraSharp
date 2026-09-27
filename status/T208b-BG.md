# T208b - the post-battleground relog, and the 15-second entrance

Evidence: `cap_bg3` + `arbiter-bg3.log` (TeraSharp on the T208 build) against `cap_bg1` (retail).

## 1. What the relog got, and why T208's re-route missed it

The T208 pieces both worked where they applied. The BG exit is fixed: `arbiter-bg3.log` 20:58:37 and
20:58:39 `Transferred player 9/10 from World 10 to 0 (7005/0)`, and both clients end on
`S_LOAD_TOPO zone=7005` (cap_bg3_client1 4182, client2 5682). The re-route fired too: 20:55:13
`zone 7005 belongs to World 0, not 10 - re-pointed the session`, and that login recovered.

The hang is the login **before** it:

| Time | What happened |
|---|---|
|20:53:19 / 20:53:21|`C_SELECT_USER` for both characters. The stored blob still said continent 115 (battlefield), so `WorldForEnterWorld` routed by that continent|
|—|World 10 had **no links yet** (it registers at 20:54:13), so the frame went to a World that does not own 115. World answered **nothing**: no `SA_ENTER_WORLD`, no `SA_ENTER_WORLD_FAIL`|
|20:54:31 / 20:54:32|the players gave up and closed the client: `AS_LEAVE_WORLD for gameId 80000AF0000x held until SA_ENTER_WORLD (World is still loading it)` - the leave is held for an enter that will never be answered|
|20:54:51|`/api/reset-character` x2 - the only thing that clears the held leave|
|20:55:13 / 20:55:24|relog: **`SA_ENTER_WORLD_FAIL ... continent 0, reason 3` from World 10** (by then registered and claiming 18 continents incl. 115), stored return point **zone 7005** (-1017, 7046, 2166) and (-1109, 7131, 2172). T208's re-route + retry then worked|

So the uncovered case is the one where **World never answers at all**: T208's fix hangs off
`SA_ENTER_WORLD_FAIL`, and there is no such frame when the continent's owner World is down. The
first `AS_ENTER_WORLD` must not carry the battlefield continent in the first place.

`WorldEntry.BuildEnterWorldPayload` already has the machinery for this - it replaces the blob's
continent with the stored return point when `GetDungeonReturn().DungeonId == zone` (the
"return-from-instance" path a dungeon relog takes). T199 stored the return point with
`SaveSystemReturn`, which writes `return_zone/channel/x/y/z` but **not** `dungeon_id`, so for a
battleground that test never matched.

| Change | File |
|---|---|
|`SaveBattlefieldReturn(characterId, battlefieldContinent, point)` - one UPDATE writing `dungeon_id` = the BF continent plus 13CB's return point|`Persistence/CharacterStore.cs`|
|the 13CB branch calls it instead of `SaveSystemReturn`|`World/BattlefieldHandoff.cs`|
|`CompleteTransfer` and `ReleaseEntered` call `ClearDungeonReturn` once the character is out (native `User::CleanSysReturnLoc` on a normal zone change)|`World/BattlefieldHandoff.cs`|

No `WorldEntry.cs` change: the existing pre-check does the work once `dungeon_id` is right, so
there is no patch to apply for this part.

## 2. The 15-second `@battlefield` wait is native

`AS_REQUEST_ENTER_BATTLEFIELD` (0x1597) is the per-user answer to the entrance offer
`BSA_NOTIFY_USER_ENTERABLE` (0x1513), and in retail it is **not** sent on receipt - it is sent 15
seconds later, measured twice in cap_bg1 to the millisecond for user 1:

| Offer | Request | Delta |
|---|---|---:|
|12390 15:36:15.135 (bf 38)|12545 15:36:30.123|14.988 s|
|12535 15:36:29.939 (bf 37)|13168 15:36:44.938|14.999 s|
|cap_bg3 3259 03:55:47.944 (bf 38)|3513 03:56:03.005|15.061 s|

cap_bg1 only *looks* fast because the GM issued `/@battlefield` twice 14.8 s apart, so the first
offer's timer fired 0.18 s after the second command. Our single command waits the same 15 s
(`arbiter-bg3.log` C_ADMIN 20:55:47 -> `Transferred` 20:56:03), and retail sends the request for all
six offered users while we send it only for the two real players - neither changes the timing.

That 15 seconds is the window the entrance popup gives a player on the normal MatchServer path. A
forced `/@battlefield` has no popup, so it is pure waiting. It is now a knob rather than a literal:

| Key | Variable | Default |
|---|---|---|
|`matchmaking.bfEnterDelay`|`TERASHARP_BF_ENTER_DELAY`|15 (retail), clamped 0..600; **0 sends the request on receipt** |

At 0 the request goes out in the same handler as the entrance info and the FIN, so `/@battlefield`
ports the party immediately. The default leaves cap_bg1's timing untouched, and the native timer
still handles every offer we did not force.

## 3. Changes

| File | Change |
|---|---|
|`Persistence/CharacterStore.cs`|`SaveBattlefieldReturn`|
|`World/BattlefieldHandoff.cs`|`EnterDelayVariable`/`DefaultEnterDelaySeconds`/`EnterDelaySeconds()`, immediate send at 0, `SaveBattlefieldReturn`, `ClearDungeonReturn` on return/release|
|`Config/TerasConfig.cs`, `World/SelfTest.cs`, `teras.example.json`|the new key, the known-variable list and the documented default (T204's drift test needs all three)|
|`Tests/T208b.cs`, `data/t208b/frames.json`|the two retail intervals and ours, the 0x1597 frame bytes, the env-var parse, and the store round-trip|

## 4. Live check

Forced BG on a server with `bfEnterDelay: 0`: the party should port in at once. Then stop the
Arbiter **while the players are inside the battleground**, restart it and log both in - the first
`AS_ENTER_WORLD` should carry zone 7005, there should be no `SA_ENTER_WORLD_FAIL`, no held leave and
no `/api/reset-character`. Then leave a BG normally and confirm `dungeon_id` is back to 0.

No build or test run was performed by the assistant; no commit, merge or deployment.
