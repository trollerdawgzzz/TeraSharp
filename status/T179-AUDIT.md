# T179 — independent capture audit

Baseline: `cowork/T8`, master `269f795`, 2026-09-22. Captures were read-only.
**Two servant-load defects fixed. Teleport and queue-popup causes remain unproven.**
Full suite: **958 passed, 0 failed, 22 skipped** because original capture fixtures are missing.
All three new T179 tests pass without external fixtures.

This is a byte census and executable handler audit, not proof of complete server equivalence.
Every observed opcode and mismatch is indexed; differences without equivalent database/session
state remain qualified. No captured benefits or character state were granted.

## 1. World request/reply and outgoing-frame table

| Coverage / result | Count | Evidence and qualification |
|---|---:|---|
| Real W->A opcodes | 203 | [Request table](../data/t179/world-census.json): names, counts, sizes, references and candidate replies. |
| Real A->W opcodes / frames | 274 / 20,574 | [Every outgoing frame](../data/t179/world-outgoing.jsonl), including unmatched boot/load bursts and pushes. Previous request/delay is context, not asserted causality. |
| Direction/opcode/size layouts | 1,225 | [Full example bytes and all varying spans](../data/t179/world-layouts.json). Unknown field meanings remain unknown. |
| Requests executed | 12,892 / 198 opcodes | [RunHandler/configured replay output](../data/t179/handler-results.jsonl). Per-capture memory store, captured initial world blobs, empty other state, synthetic account/name mapping. |
| Supplementary routes | 147,230 records / 5 opcodes | [Results](../data/t179/supplemental-routes.json). All 127,638 real 0x13F7 frames round-trip through the production codec without a mismatch. |
| Timeline 0x13F2 | 15,907 requests | 20 nonempty requests build 1,547 replies; every payload appears exactly in its same-link one-second capture window. [Checks](../data/t179/timeline-verification.json). Empty forms build none. |
| Broadcast / silent routes | 73 / 3,612 records | Distinct 0x1436 payloads run through RunHandler: no World reply. 0x13E5 (3,196) is WorldBridge's silent heartbeat; 0x15A8 (416) is excluded from replay. The last two are source-verified, not simulated sessions. |
| Initial FIFO/name pairs | 6,428 | [Original candidates](../data/t179/world-pairs.jsonl); names and timing alone do not prove causality. |
| Reverse-direction/asynchronous exclusions | 144 | AS_ENTER_WORLD/SA_ENTER_WORLD, AS_LEAVE_WORLD/SA_LEAVE_WORLD, tooltip and contract asks/answers. A later matching name is not their reply. |
| Interleaving corrections | 14 | [Corrected pairs](../data/t179/pair-corrections.json): varying u32 echoes, including achievement DLM at request payload +280. |
| Exact after correction/focused check | 5,090 | Complete payload equality, no masks. Includes all 182 registration replies through WorldRegistration.Reply instead of the baseline harness's replay-only route. |
| Different payloads | 1,156 | 920 state-dependent; 14 league/season replay; 53 pet-field unresolved; 53 replay reset timestamps; 21 absent benefit lists; 95 attendance result/state unresolved. |
| No matching harness reply | 38 | 34 require existing guild/contract/session paths. Four are confirmed missing routes: three dungeon-leave requests and one GM-hold request. |

[Every reviewed mismatch](../data/t179/reviewed-world-diffs.jsonl) includes opcode, exact capture
references, complete real/our bytes, differing spans, classification and uncertainty.
[Per-opcode summary](../data/t179/reviewed-comparison-summary.json),
[DbAckGroups/runtime routes](../data/t179/runtime-world-routes.json).

The replay source is `D:/packetlogs/arb_world.log`. The baseline harness did not execute WorldBridge's
registration override; the focused run corrects that and verifies the two servant fixes.
GuildWiring, ContractBroker, GmAdminTool and client-triggered bridge paths were inspected but not
reconstructed with all live sessions. Their absent isolated replies are not established production defects.
The audit-only negative expected-frame mode collects for 25 ms; existing positive-count tests are unchanged.

## 2. Client request/response table

| Coverage / flag | Result | Evidence and interpretation |
|---|---|---|
| Real C_ opcodes | 270 | [Complete routing/source table](../data/t179/client-routes.json). |
| C_ without explicit handler | 129 | Individually flagged. PacketDispatcher forwards them while InWorld. Zero currently ArbiterOwned opcodes are unregistered; ownership completeness is not assumed. |
| Real S_ opcodes | 515 | Every S_ has references, source-code locations and observed real tunnel count. |
| S_ without named code or observed real tap tunnel | 136 | Flagged candidates, not 136 proven omissions: generic tunneling accepts arbitrary S_ opcodes; Classic has no paired World tap; numeric builders can evade a name search. |
| Request/response windows | Every C_ record | [Per-request windows](../data/t179/client-windows.jsonl): actual 100 ms windows for five Classic `.npcap` streams. Text client logs are sequence-only, with no invented timestamps. |
| Transport | All real 0x13F7 frames | Production parser/builder preserves recipient lists and complete embedded client packets. Does not test encryption or live session delivery. |
| Local response behavior | Source locations per C_ | RegNoop/RegEmptyReply forward in-world; local behavior is indexed. Stateful client handlers were not all executed against a running World. |

Unregistered C_REQUEST_REPUTATION_STORE_TELEPORT, C_RIDE_PEGASUS and skills can legitimately use
0x13F6 forwarding. No missing-handler claim is based solely on registry or S_ name absence.

## 3. Open problems and prioritized defects

| Priority / issue | Raw evidence | Result / fix shape |
|---|---|---|
| **P1 fixed: 0x1539 -> 0x153A** | cap_final2b 133->134. Real payload `00000000000000000900000001eb030000`; replay `0000000000000000090000000101000000`. | Payload +13 is UserDbId=1003, not 1. Activated the existing builder, placed success at +12 and full user ID at +13. All 53 real pairs now exact. |
| **P1 fixed: 0x153B -> 0x153C** | cap_final2b 135->136. Real `00000000000000000a00000001eb03000000000000`; replay `00000000000000000a000000010100000000000000`. | Same identity error. Activated/corrected builder; all 53 exact. Arb_part_030.c:2411-2424 and :2622-2632 independently write DlmId, bool, UserDbId. |
| **P1 open: 0x13C2 -> 0x13C3** | cap_multiworld 12832.2->12834; cap_multiworld3 8164->8165,8320->8321. Real payloads `f00a00000100000063260000` / `f00a00000100000035260000`; ours none. | No handler, DbAckGroups coverage or configured replay reply. Arb_part_062.c:13016-13099 schedules departure work and sends `[PDId][continent]` to the owning World. Requires user-handle resolution, departure state and destination routing, not an unconditional same-link ACK. |
| **P2 open: 0x158A -> 0x158C** | cap_final2b 51507 client tunnel ->51509 request `2000c2c91802000001` ->51510 reply `0100000001` ->51511 S_ADMIN_HOLD_CHARACTER=1. Ours none. | Resolve target handle, maintain hold/expiry state, send `[u32 UserDbId][u8 held]`. Arb_part_062.c:2303-2313 -> Arb_part_030.c:5062-5109. WorldServer.exe.c:2978164-2978177 applies it. Separate from login 0x2930. |
| **P2 open: account benefit state** | cap_final2b 170->171 user1003: 67-byte reply, benefits 533/534/1000 in 16-byte entries. 496->497 user1: successful empty 19-byte reply. | Replay is empty. Needs account-package ownership/expiry state. Request frame +0x0A is UserDbId, resolved to User+0x3F40 account (Arb_part_063.c:12662 onward). Does not establish teleport causation; do not grant captured benefits universally. |
| Teleport full entry comparison | [32 entry windows](../data/t179/entry-sequences.json), [36 comparisons](../data/t179/entry-diffs.json), [all AS_ENTER_WORLD fields](../data/t179/entry-fields.json). Real cap_final2b versus cap_crash/bag/scroll. | All outgoing bytes retained. Real user1 vs bag user10: enter 189B and blob reply 15,331B each; quests 4,539 vs79B; inventory 15,563 vs2,163B; both benefits empty; bag has an extra crest-point push. Different database state prevents treating these as structural defects. |
| Teleport @3301 | WorldServer.exe.c:1010679-1010697: class/quest and TeleportRestrictionAgent can each emit 0xCE5. :846385-846391 bypasses both. | Captured entry subjects all have blob class 12, levels 1/20/70. Local ClassException.xml restricts soulless teleport, not glaiver. This weakens the class-gate lead for these subjects; live loaded sheets remain unobserved. |
| Teleport benefit/agent connection | WorldServer.exe.c:1471935-1471956 checks agent mode and User+42000; :1886840-1886844 sets that flag from HuddleAdding; :707688-707705 reads trait 0x3A. | Base AccountTrait.xml has HuddleAdding disabled. Live agent mode/callback result is absent from captures. Missing package IDs alone do not prove this gate's result. Root cause remains unproven. |
| Queue: both members | cap_queue2_client1 917/918/919/920; client2 1320/1321/1322/1323: empty 10B state frames, FIN 9781, party info488B. Real classic_live3 10580/10581/10585/10587:90/160 IDs, FIN 9739,30 party slots. | Both test clients received FIN. Lists, instance, party size/IDs/roles differ; these are not controlled equivalent sessions. [All queue frames](../data/t179/queue-frames.json). |
| Queue: current T161b | [Current builders](../data/t179/current-match-frames.json):9739 sends IDs 2154/92151 then FIN;9781 sends2142/92139 then FIN.26-byte lists, no empty second frame. | cap_queue2 shows older behavior. Current lists still differ from the real match-found full rosters. T161 pins serialization of supplied IDs, not production roster selection. Cannot assign popup failure exclusively to World/EventMatching data. |
| Cards 0x2988-0x2998 | cap_social4 7032->7033,7078->7079,7107->7108,7130->7131,7140->7141. | **Five real pairs exist:** register2, mount2, unmount1. Existing replies exact; added a capture sequence test. No real0x298E-0x2998 frame found. Absence is limited to this corpus. |
| Remaining differences | Pet payload+171, battlefield reset timestamp, attendance result, league/season and database-dependent loads/writes. | Every mismatch is recorded. No further production fix follows from an unequal empty-store trial alone. |

The missing SA paths need session/routing/state work. They were not replaced with unconditional success.
Human-owned WorldBridge, WorldEntry and Network files were not edited. The servant fix is not claimed
to solve teleport or the queue popup; populated servant persistence remains unverified.

## 4. Capture-pin verification table

Checked **33 capture-related tests** across T134-T172, plus one incidental T172 store test in the machine
inventory. [Per-input hashes/matches](../data/t179/pin-verification.json).
All **39 explicitly numbered citation checks** match their expected literals at the cited source,
not merely an identical frame elsewhere: [checks](../data/t179/explicit-citation-checks.json).
Missing original fixtures were not regenerated to claim success.

| Test / claim | Source record | Result |
|---|---|---|
| T134 dungeon windows | classic_live2 13158,13163,19420 | Capture forms exact; populated synthetic cool-time form expressly is not a capture pin. |
| T136 queue frames | classic_live3 8662,10333,8664,9487,8678,10346,9489,7337,53967,10585 | Exact expected literals/request bodies. |
| T138b handoff | cap_multiworld3 7829,8162; roster/crossing context | Shortened roster/crossing examples; does not pin the complete 208-byte ready frame. Full-frame wording overstates coverage. |
| T135 guild quest frames | classic_live2 12218,12254,12220 | Expected literals exact. |
| T135 finishing reward | classic_live2 12218 | Five-byte reply exact; reward/state assertions separate. |
| T138d party info | classic_live3 10587 | Full 488-byte literal exact. |
| T142 task toggle | cap_social4 3195/3196,3233/3234 | Exact; does not cover real failure=0 cases elsewhere. |
| T142b empty item-single | cap_social4 434/435,658/659 | Exact. |
| T147 crafting loads | cap_social4 260/261,262/263 | Exact. |
| T147b skill list | classic_craft95 citation | Original .hex absent; skipped. |
| T147b recipe list | classic_craft96 citation | Original .hex absent; skipped. |
| T157 World requests | cap_social 1699/1723; cap_final 7940/8098/8217 | Existing cap_t157.bin records match raw; fixture keys add 100000/200000. |
| T157 live tabs | classic_live2 9364; classic_live3 7964 | Both existing full .hex fixtures exact. |
| T156 Vanguard list | classic_live3 7256/53330/53429/53367/53331 citations | Original .hex fixtures absent; skipped. |
| T154 city guild | cap_final/cap_social4 league1 season2 | Captured forms found exactly; zero-request fallback synthetic. |
| T167 three loads | cap_social4 329/330,431/432,446/447 | Raw records exist; cap_t167.bin absent, original cannot be compared. |
| T168 two loads | cap_bag 178/179; cap_final 288/289 | Raw records exist; cap_t168.bin absent. **Bag is TeraSharp, not a real-Arbiter pin.** |
| T170 clear skill | cap_clearallskill 902/903 | Real 0x27E7 frame 4,347B; cap_t170_skill.bin absent. |
| T170 EP/cards | cap_final2b682/683,700/701,5716/5717 | These raw records exist; cap_t170_ep.bin/final.bin absent. |
| T170 collect | cap_social 1555/1556 | Raw exists; cap_t170_mail.bin absent. |
| T170 guild war | cap_final2b6801,6901,7776 | Raw exists; cap_t170_guild.bin absent. |
| T170 guild quest | cap_final2b14883,14989 | Raw exists; cap_t170_guild.bin absent. |
| T161 match frames | classic_live3 10580,10581,10585 | Raw exists; original .hex fixtures absent. Serializer round-trip does not prove production event-ID selection. |
| T169 polishing | cap_final2b50067/50068 | Full 191-byte expected reply exact; unlock inputs intentionally omit transaction atoms. |
| T172 first visits | final2 client09:25:01 record2303;09:24:54 record3624 | Expected payloads exact; identity/state assertions separate. |
| T172 visited World list | cap_final2a/2b956; cap_final 310 | Expected layouts exact. |
| T172 guild quest points | cap_final 312/606 and other loads | Expected 16-byte payloads exact. |
| T169 material tooltip | final2 client09:49:53 record608 | Full 930-byte expected tooltip exact; item record is an embedded structure. |
| T169 compare tooltip | cap_final2b49640/49641; client09:49:53 record619 | Expected ask/answer/tooltip exist; test intentionally permits six item-level float differences. |
| T172 broadcasts | cap_final2b53725; clients09:49:53/09:50:23 records3417/3046 | World payload and client message literals exact. |
| T172 crest list | cap_final2b4746/4747 | Complete request/reply literals exact. |
| T172 city-war state | cap_final2b57115/57116,57117/57119 | Both complete pairs exact. |
| T172 goto | cap_final2b7277/7278 | Expected 16-byte reply exact. |

No present full expected-frame fixture in this sample was shown to have drifted. This excludes
missing originals, shortened structures, synthetic inputs and allowed masks. The full suite also
skips missing cap_t150.bin, cap_t153.bin and cap_t156.bin dependent tests; all 22 names are in
[test-results.txt](../data/t179/test-results.txt).

## Capture integrity and limits

All **73** `*_ctl.txt` files were inventoried. **45,015/45,024** rows with raw sources match
length/opcode/payload prefix. Nine exceptions are cap_newchar's empty 6-byte frames: the old condenser
prints the opcode high byte as a fictitious payload byte. Full raw frames remain valid.
cap_relog9827_ctl.txt has no same-name raw log; complete bytes could not be verified.
[Listing checks](../data/t179/listing-verification.json), [source hashes](../data/t179/inventory.json).

Classification follows the supplied filename families. Identical files are deduplicated, preferring
named real captures over generic aliases. Overlapping prefixes in differently sized files
(final2a/2b, crash/scroll) remain separately indexed and are not independent sessions.
Unrelated chat_capture.log has 131 truncated records; no real-qualified source has parse errors.
Untimed client logs cannot establish 100 ms windows. Capture gaps cannot prove a packet is never sent.

Entry comparisons preserve every outgoing byte within connection windows, but may interleave users;
opcode-occurrence alignment is not a per-user state reconstruction. Unknown blob fields, live restriction
mode, equivalent retail database snapshots, populated servant lists and live socket effects remain unverified.

## Changes and reproduction

Production: `src/TeraSharp.Arbiter/World/DbProxyHandlers.cs` only: dispatch and UserDbId for two servant loads.
Tests: audit collection mode plus `T179_servant_load_echoes_the_captured_user`,
`T179_servant_adventure_load_echoes_the_captured_user`, `T179_card_writes_match_all_five_real_pairs`.
Tools: `tools/t179-audit.py`, `tools/t179-evidence.py`, `tools/T179Audit`.
No commit, merge, deployment or public push was performed.

Build: `dotnet build tools/T179Audit/T179Audit.csproj`.
Tests: `dotnet run --project src/TeraSharp.Arbiter.Tests --no-build`.
Initial build: four existing nullable warnings; subsequent incremental build clean.
See [reproduction order](../data/t179/README.md). Generated raw-byte evidence stays ignored under
`data/t179/`; report, tools and compact tests are reviewable changes.
