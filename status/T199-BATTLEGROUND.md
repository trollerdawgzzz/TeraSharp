# T199 — captured battleground lifecycle

Base: `dca1a71`, `cowork/T8`. Evidence: full reframed `cap_bg1` tap and both client logs.
Tap citations are original record plus byte offset; client citations are reframed packet ordinals.

## Capture boundary

The run offers battlefield **38**, then **37**; the actual fight is **37**, continent115,
on registered World10. Owner IDs must come from registration/pending-instance data.
The GM commands force entrance offers; they do not capture normal MatchServer pool completion.

| Event | Client1 (`dobb`, user1) | Client2 (`New`, user1003) | Captured behavior |
|---|---:|---:|---|
| Forced offer38 |2906–2908|2665–2667|942F entrance,87AC event change,6470 FIN |
| Forced offer37 |2978–2979|2746–2747|942F entrance,6470 FIN; no second event change |
| First packet after FIN |2909 /2980|2668 /2748|C_VIEW_INTER_PARTY_MATCH_BATTLEFIELD_LIST9830 |
| Destination topo |3123|2895|Continent115 |
| Round initialization |3176|3014|C924,40B: battlefield37; team2/team1 |
| Three round starts |3588,4520,5172|3285,4160,5185|D9E5 state1 |
| Kills |3786,4605,5261|3475,4275,5271|6C76,54B; same event bytes per recipient |
| Result |5410|5559|765F,56B: native scalar−33 /0; winning team1 |
| End state |5411|5560|D9E5 state2 |
| Round board |5586|5747|97C6,161B |
| Reward/exit request |5604|5751|C_BATTLE_FIELD_REWARD E17C |
| Round finish |5620|5776|S_FIN_ROUND_PVP_BATTLE_FIELD8E90 |
| Return topo |5710|5879|Continent7005 |

Other tunneled families: AF11 scores (24B,48/46 frames),5101 NPC spawn (36B,two each),
BE8E NPC despawn (12B,two each),D687 user-load info (131B,one each),66DB enemy commander
(16B,client1 only). World owns these packets; Arbiter must relay them without duplicate builders.
INIT maps to tap12929/12932, results to16224/16223, finish to16373/16580.

## Transfer and party evidence

There are no13BE/13BF/13C0/13C1/13C2/13C3/13C5/13C6 or1445 frames in this run.
BG entry uses **13CB →1392(type2) →1393 →138E**, and return starts with **13CD**.
The13CB location is the saved town return point, not the battlefield destination.
Tap12546 carries continent7005/channel0/XYZ(−1149,7252,2172); pending1513 at12535+228
supplies battlefield37/continent115/channel0AF03922 and destination spawn used by138E12667.
Native: `Arb_part_062.c:6528–6546` → `Arb_part_028.c:14874–14925`.

| AS_ENTER_WORLD | User | Destination | Type | Normal party | IsSysParty |
|---:|---:|---:|---:|---|---:|
|12667|1|115|2|0AF0003200000001|0|
|12690|1003|115|2|0AF0003200000002|0|
|16385|1003|7005|2|0AF0003200000002|0|
|16604|1|7005|2|0AF0003200000001|0|

Both normal parties survive entry and return. Native `LeaveBattleField` calls system-party
removal, which returns immediately for a normal party (`Arb029:3495–3515`, `Arb079:13428–13446`).
There is **no13F5** in this capture. Abnormality999994 already exists at initial town login
(client1:418; client2:594/599), so it is not evidence of a BG completion/dropout penalty.
Normal MatchServer pool/popup completion, system-party restoration and premature BG dropout
remain decompile-only; this forced run does not pin those paths.

## Custom rating policy

Per the user's instruction, retain custom signed random rating movement and the zero floor;
do not replace it with retail MMR. The editable policy is
`data/custom-datasheets/TeraSharpBattlegroundRating.xml`:

```xml
<TeraSharpBattlegroundRating>
  <Rating minDelta="5" maxDelta="12" />
</TeraSharpBattlegroundRating>
```

Publishing copies it to `custom-datasheets/TeraSharpBattlegroundRating.xml` beside the
application. The configured `TERASHARP_DATASHEET` directory takes precedence. The published
custom XML supplies the startup fallback; the same XML is embedded if that file is absent
or invalid. Startup reports name the fallback source. There is no second numeric range in runtime C#.
This is a TeraSharp custom sheet, not a claimed retail BattleFieldData attribute.

The native result scalar cannot determine the outcome: the winner receives0 in client2:5559.
World's result writer emits a winning-party list (`WorldServer.exe.c:2100574–2100591`).
Compare that list with C924's team at packet+16; team1 wins and team2 loses here.
Keep native battle statistics separate from the custom leaderboard rating.

## Implementation and validation

| Area | Fix and evidence |
|---|---|
| Forced creation | Implement `/@battlefield` with1518, preserving the caller's party followed by the distinct named parties. Pin12388 (38,parties1/2) and12533 (37,parties2/1). Read `BattleFieldData.CommonData.continentId` for owner selection (`Arb040:14361` → `Arb046:2668–2698`).13DF carries open state, not owner identity. |
| Creation persistence | Answer1514 with the stored opaque unique ID;1512 persists issuance without a reply.13E0 creates a durable log identity and13E1 echoes it. Native SQL is in `GameDatabaseDefinition.xml:6991,7098,15844`; captured ID values are test state, not runtime constants. |
| Pending offers and transfer | Consume1513, emit942F/event reset/FIN and the native15-second1597 request. Persist13CB's return point, send27C6, then use the existing type2 continuation.13CD restores that point. Preserve normal parties; discard stale session offers/timers. A dropped return destination clears the in-progress gate so a retry is possible. |
| Result/rating | Track C924's team, decode765F's winning-party list, replace only the four-byte scalar with the sheet-driven custom delta. Preserve the zero floor; unknown zero outcomes and draws do not invent a loss. |
| Fight persistence | Consume13D5 and13E3 without ACKs. Store decoded native result rows and periodic/final team snapshots, plus their payloads, in `game_log`; keep this separate from custom rating. |

The13D5 frame at16228+22 contains user1003 win1/kills2/deaths1/nativeGradePoint+37 and
user1 loss0/kills1/deaths2/nativeGradePoint−33. The34 captured13E3 snapshots culminate in
16230+46 with Finish=true. Layout/handler citations: `Arb013:5001–5253,7285–7546` and
`Arb061:18169–18404,19863–19911`.

There are no13D6 or13D8 frames in this run; those unimplemented native result/analytics
paths remain unpinned. Native cumulative season tables and `S_VIEW_BATTLE_FIELD_RESULT`
are not added here; captured statistics are durably queryable in `game_log`.
The13DF open-state cache does not yet emit native global open/close announcements.

Changed production files: `World/{BattlefieldCreation,BattlefieldHandoff,BattlegroundResults,
BattlegroundRatingSheet,BattlegroundRating,BattleFieldSheet,CrossWorldHandoff,DatasheetLoader,
DbProxyHandlers}.cs`, `Handlers/GmCommands.cs`, `Persistence/CharacterStore.cs`, and
`TeraSharp.Arbiter.csproj`, all under `src/TeraSharp.Arbiter`.
Tests: `src/TeraSharp.Arbiter.Tests/{T199Creation,T199Transfer,T199Results}.cs`.
Evidence: `data/t199/*frames.json`, `data/t199-results/frames.json`, and
`tools/t199-creation-evidence.py`; all **88 selected frames** match the full original records.

Human-owned integration is in `status/T199-PATCH.diff`. Apply it **after** the T195B,
T197 and T198 patches, checking/applying each patch sequentially.

Final combined validation on2026-09-26: build succeeds; **1055 passed,0 failed,26 skipped**.
All eight T199 tests pass. Four existing nullable warnings remain. The34 changed/new source
files match the tested snapshot by SHA-256. All four human patches apply sequentially to
fresh source; their live worktree files remain untouched. The custom XML copied to output
matches the template and creates no partial native datasheet directory.

No commit, merge or deployment has been performed. Live validation remains necessary:
forced offer → enter/fight/result → return for both members; confirm the custom rating
movement and repeat entry after return. Ordinary BG pool/popup behavior remains unpinned.
T195–T198 changes and their separate validation are documented in
[T195-T198-CHANGES.md](T195-T198-CHANGES.md); the combined index is
[T195-T199-CHANGES.md](T195-T199-CHANGES.md).
