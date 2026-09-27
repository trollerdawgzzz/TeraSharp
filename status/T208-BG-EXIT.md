# T208 - battleground exit and the post-BG relog

Evidence: `cap_bg1` (retail Arbiter, BF World10, links #29 main / #56 BF) against `cap_bg2` +
`arbiter-bg2.log` (TeraSharp, links #31 main / #61+#63 World10). Citations are reframed tap
record numbers.

## 1. The retail exit sequence (cap_bg1, per player)

| Record | Frame | Note |
|---:|---|---|
|16352 / 16561|`W->A 0x13CD` BSA_EXIT_BATTLE_FIELD|handle only |
|16353 / 16562|`A->W 0x1392` AS_LEAVE_WORLD|`[gameId][type=2][reason=0][userId]` |
|16359 / 16574|`W->A 0x13AA`|World starts the leave |
|…|`27FA/2924/2768/13CC/28C1/2936` + `27CB` (15431 B)|World's leave-time saves |
|16383 / 16602|`W->A 0x1393` SA_LEAVE_WORLD|→ `A->W 0x1433` |
|16604 / 16385|`A->W#29 0x138E`|continent 7005, the13CB return point |

TeraSharp reproduced 0x13CD → 0x1392 byte-identically (cap_bg2 60456/60457, 60514/60515) and
then stopped: World10 sent 0x13AA (60481, 60521) and **no save chain and no 0x1393**. The forced
lobby-return 30 s later (60707, type3) also never got 0x1393 - `SA_LEAVE_WORLD not received in 5s`.

## 2. Root cause: one unanswered DLM item

Every `SA_*`/`AS_*` pair on the BF link, requests versus answers:

| Opcode | Name | retail #56 | TeraSharp #63 |
|---|---|---:|---:|
|0x1523|SA_UPDATE_BATTLE_FIELD_COOL_TIME|2 asked, **2 answered** (12933→12940, 12944→12945)|2 asked, **0 answered** (56491, 56497)|

Nothing else differs; every other unanswered opcode on the BF link is unanswered in retail too.
World sends one 0x1523 per user about a second after the entry completes, and it is a per-user
DLMItem - the same shape the code already documents for `SA_CLEAR_BATTLE_FIELD_ENTER_COUNT`
(0x1562, `Arb_part_062.c:4752`): unanswered it head-blocks `UserLeaveWorld`. Answering it is the
whole of the exit fix.

| Field | Offset | cap_bg1 12933 | cap_bg2 56491 |
|---|---:|---|---|
|vector offset / bytes|0 / 4|26 / 24|26 / 24|
|gameId|8|`0x270C857C020`|`0x800AF00001`|
|reqId (DlmId)|16|0x5D|0x89|
|battleFieldId|20|37|38|
|`OURS:` unpinned u32|24|292 / 0|0 / 0|
|endTime (unix)|28|0x6AB7E67F|0x6AB8870D|
|three u32 0|32..43|0|0|

Reply `AS_UPDATE_BATTLE_FIELD_COOL_TIME` (0x1524) = `[u8 ok=1][u32 reqId]`, i.e.
`BuildOkReqId(payload, 16)` - byte-exact against 12940 (`01 5D 00 00 00`) and 12945
(`01 64 00 00 00`). The record itself is **not stored**: its middle u32 is unpinned and
`SA_LOAD_BATTLE_FIELD_COOL_TIME` (0x1521) still answers with an empty list, so BG cooldown stays
World-side only, exactly as before.

## 3. Root cause: the post-BG relog

`arbiter-bg2.log 20:01:31` - `Saved world blob ... for character 9; zone 115 (9368.1, 99881.3,
6756.6)`. The BG transfer stamps the battlefield destination into the stored blob, which is
correct while the player is in the BF and stale the moment the session ends there. C_SELECT_USER
routes `AS_ENTER_WORLD` by that continent (`DungeonRouting.WorldForEnterWorld`, payload+48), so
the relog went to the BF World (cap_bg2 60743, continent 115) and was refused:
`SA_ENTER_WORLD_FAIL ... continent 0, reason 3` (60744). The existing fallback then retried with
zone 7005 - **to the same World** (60746), which does not own 7005; nothing answered and the
client sat on the loading screen.

| Stale thing | Cleared by | Where |
|---|---|---|
|blob/row continent+channel+world inside the BF|`BattlefieldHandoff.ReleaseEntered` stamps13CB's return point back|`ForgetUser` (disconnect), `ForgetWorld` (BF World gone)|
|T192 owner record, enter stamp, GM one-shot|`CharacterTransientState.Reset` from the same place|as above|
|`entered` / `offers` / offer timers|already removed by `ForgetUser`/`ForgetWorld`/`CompleteTransfer` (T199)|unchanged|
|retry sent to the refusing World|re-point the session + reallocate the Ticket|`status/T208-PATCH.diff` (WorldEntry.cs is human-owned)|

Nothing is released at the result packet: at that point 0x13CD has still to arrive and the
character is legitimately in the battlefield. A record is only released when its `ReturnWorld` is
still null, so a transfer already begun is left to `CompleteTransfer`. Only the position is
rewritten; items, skills, quests, money and the native BG result rows are untouched.

## 4. Changes

| File | Change |
|---|---|
|`World/DbProxyHandlers.cs`|`SA_/AS_UPDATE_BATTLE_FIELD_COOL_TIME` constants, allow-list entry, `BuildOkReqId(payload, 16)` reply|
|`World/BattlefieldHandoff.cs`|`ReleaseEntered` + calls from `ForgetUser`/`ForgetWorld`; `bridgeRef`/`storeRef` captured in `TryHandle`|
|`Tests/T208.cs`|two tests (byte-exact ack through `WorldBridge.HandleFrame`; the release path)|
|`data/t208/frames.json`|the six cited frames, whole frames `[len][op][payload]`|
|`status/T208-PATCH.diff`|`Handlers/WorldEntry.cs` retry re-routing (apply with `git apply --ignore-whitespace`)|

The frames come from the repo's own reframe of `cap_bg1.log` / `cap_bg2.log`
(`cap_bg1_ctl.txt`, `cap_bg2_ctl.txt`); both opcodes are far shorter than the reframe's byte cap,
so the recorded hex is the complete frame.

## 5. Not done / live check

- Ordinary MatchServer pool completion and premature BG dropout are still uncaptured (T199).
- BG cooldown is still not persisted or served (0x1521 empty, as before T208).
- No build or test run was performed by the assistant; no commit, merge or deploy.
- Live check: forced BG → fight → result → **Leave battleground** ports both players to the town
  return point (expect `0x1393` on the BF link then `0x138E` continent 7005), then relog both
  characters without `/api/reset-character`. Second check: kill the BF World mid-match and relog.
