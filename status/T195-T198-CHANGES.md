# T195–T198 changes

Worktree: `TeraSharp-cowork`, branch `cowork/T8`, base/master `dca1a71`.
No commit, merge or deployment was performed by the assistants.

| Task | Proven defect and change | Evidence and detail |
|---|---|---|
| T195 routing | Route character actions to their current World; deliver party chat directly to party members; use character IDs for internal party recipients because tunnel tickets overlap and swap across Worlds. Preserve native broadcasts and main-only matching messages. Guild-load snapshots reply only to the requesting link. | [MULTIWORLD-DESIGN.md](MULTIWORLD-DESIGN.md#t195-remainder--instance-chat-party-ui-and-return-transfer). Live tickets: `cap_instance1`136991/137332→146976/147327; menu/chat examples152568/152756/153239/153535. |
| T195 return | Add the configured catch-all World after explicit World/channel/continent lookup. This permits return to open-world7005/7021 even when those continents have no channel roster entry. Preserve World's return point and type-2 handoff; a known offline owner still fails. | Retail `cap_multiworld3`8164–8193 and8279+702; live reset153328/153336+150, leave153967/153977, Unstuck154360/154433. Existing departure13C2/13C3 already worked. |
| T196 rewards | World already requests achievement reward mail through1479. Store its attachment records and notify; let the existing claim transaction grant the items. Do not grant again from2802. | [ACHIEVEMENTS.md](ACHIEVEMENTS.md#t196--achievement-rewards-the-parcel-existed-its-attachments-were-discarded). Live139184 contains EP rewards201100 x1/x1/x2/x3. Retail6185–6190 and28054/28067–28070 pin grant/list/claim. |
| T197 glyphs | Persist unlock/apply state before ACK, restore it at entry, and remove the false zero-use S_CREST_INFO injected after topo-fin. Restore separately committed base/extra points to their native entry fields. Preserve existing preset settings. | [PERSISTENCE-MAP.md](PERSISTENCE-MAP.md#t197--glyph-persistence-and-the-false-reset-after-topo-fin). Client2:12494 has19 applied;13037→13039 falsely clears them. Native base points are blob+3AEC; extra points are AS_ENTER_WORLD payload+161. |
| T198 crafting | Per-player ordered delivery prevents packets from parallel World links overtaking each other after sequence sorting. Normal arrivals and watchdog flushing use the same delivery queue; resets discard queued old packets. Benefits and artisan records remain unchanged. | [CRAFTING.md](CRAFTING.md). Live World137078/137095/137096 sequence0/16/17 became client2:52/67/72 sequence16/17/0. Retail GM and both live accounts receive identical artisan/fatigue bytes. |

## Files

| Group | Files under repository root |
|---|---|
| T195 | `src/TeraSharp.Arbiter/Handlers/{ArbiterClientHandlers,BrokerHandlers,ChatHandlers,SocialHandlers}.cs`; `World/{ContractBroker,CrossWorldHandoff,GuildWarManager,GuildWiring,PartyManager,PartyWiring}.cs` |
| T196 | `World/SystemParcelAttachments.cs`, `World/ParcelDbHandlers.cs`, system-parcel handler in `World/DbProxyHandlers.cs` |
| T197 | Crest region of `Persistence/CharacterStore.cs`, `World/CrestDb.cs`, crest handlers/entry load in `World/DbProxyHandlers.cs`, explanatory comment in `World/DbProxyT172.cs` |
| T198 | `World/OrderedTunnelDelivery.cs` |
| Tests | `src/TeraSharp.Arbiter.Tests/{T195Instance,T195RoutingExtras,T196,T197,T198}.cs` |
| Evidence | Selected frames under `data/t195`–`data/t198`; `tools/t197-evidence.py` |

`World/` and `Persistence/` above are relative to `src/TeraSharp.Arbiter/`.
The earlier T195 GM slice is already on master and is not duplicated here.

## Required human-owned patches

CLAUDE.md reserves these live files for the human. Their source files were left untouched;
all patches are applied to the isolated validation copy and must be applied before your build:

1. `status/T195B-PATCH.diff`: WorldBridge destination lookup, per-character exit/cancel, guild reply link.
2. `status/T197-PATCH.diff`: remove HandlerRegistry's false crest push; restore extra points on World entry.
3. `status/T198-PATCH.diff`: WorldBridge queue delivery and lifecycle integration.

## Verification and live limits

Final combined validation on 2026-09-26: isolated copy builds successfully with all three
human patches; **1047 passed, 0 failed, 26 skipped**. Four existing nullable warnings remain.
The modified C# sources match the tested copy by SHA-256. The worktree's human-owned files
remain unchanged; apply the patches before building or committing.
All **164 selected frames** were checked against their original capture record and offset.
Packet tests normalize only the stated runtime identifiers; native parcel padding is zeroed,
not copied from uninitialized captured memory. Nonzero extra crest points are decompile-marked.

Live checks after shipping: Say/party chat, party menus, reset vote, leave/Unstuck return,
GM actions before/after transfer, a new achievement reward mail and claim, glyph relog/restart,
and J on the GM both before and after dungeon entry. T198 fixes a captured ordering violation;
the crafting-window symptom still needs that live confirmation.

Previously lost achievement rewards are not backfilled. System rewards with nonzero unidentified
item grades remain unsupported and receive a failure ACK. Existing1458 broker-close bytes are
routed correctly, but native emission of that close frame remains unverified.
