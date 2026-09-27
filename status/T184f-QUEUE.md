# T184f — Queue packets, resets, and matching statistics

Audit inputs: `cap_2man_client1/2_ctl.txt`, `cap_queue4_client1/2_ctl.txt`, and both reassembled Arbiter–World taps. Client record numbers below are original client records. Tap references use the root audit's corrected starting chunk/offset/end chunk in `obj/t184f/frames.json`; that cache includes unrelated authentication traffic and must never be printed wholesale.

## Proven queue behavior

| Packet | Successful two-player capture | queue4 / current builder | Result |
|---|---|---|---|
| C730 pool | C1 #1231/#4246/#10439; C2 #820/#4287/#8068; 49 B, one application member, destination9781, matchingType0/free0, remainSec0, planet2800, role1, flag0 | Same layout and fields, different character IDs | No queue builder fix. All six successes carry zero remaining seconds. |
| 87AC queued | C1 #1232/#4247/#10440; C2 #821/#4288/#8069; 26 B, queued1/type1, event IDs2142/92139 | Byte-identical | No fix. |
| 8BE4 progress | C1 #1237→#1238; C2 #826→#827 (and later polls) | 36 B, destination9781,type0,free0,tank0,dealer1,healer0 | No layout/count-order fix. These polls occur before the second application forms the group. |
| 6470 FIN | C1 #1285/#4340/#10524; C2 #914/#4356/#8161; 16 B `[9781,0,0]` | Byte-identical | No fix. |
| 74C7 initial SYS | C1 #1286/#4341/#10525; C2 #915/#4357/#8162; 488 B /30 physical slots /two DPS | queue4 also has two DPS and30 slots | Five members are not required by the client. Successful C_ENTER_DUNGEON follows. Order changes1003,1 →1,1003 →1003,1 across the three real formations. |
| 87AC full dungeon reset | C1 #1283; C2 #912; 1066 B /132 entries | C1 queue4 #1182;1034 B /128 entries | **Proven loader defect:** TimeLine events2176/92176 are absent from both free-mode passes. |
| 87AC battleground reset | C1 #1284; C2 #913;554 B /68 entries | Same frame | No fix. |

The later World-originated40 B/two-slot SYS packets (C1 #2115/#4591/#10758; C2 #2358/#4763/#8389) are separate post-entry updates. They are not replacements for the initial488 B Arbiter roster.

## TimeLine classification fix

`DatasheetLoader.ReadEventMatchingTargets` previously accepted `Dungeon` and `BattleField` only. It now treats `TimeLine` as a dungeon matching event.

Proof chain:

- Arb_part_008.c:17618–17692 maps Dungeon=0, SoloDungeon=1, BattleField=2, Field=3, TimeLine=4.
- Arb_part_008.c:18111–18168 inserts types0/1/4 into the dungeon reverse index at+0x18;18170–18223 handles BattleField separately.
- Arb_part_008.c:11885–11928 is `GetEventIdListForDungeonTemplateId`.
- Arb_part_076.c:18485–18543 (`FUN_1408d11a0`) accepts destination types0 and4 through the+0x48/+0x88 target maps. It does not accept SoloDungeon's+0x58 map.
- Arb_part_077.c:5843–5866 emits that reverse index under the dungeon flag1.
- Live EventMatching.xml:1951–1955/4098–4102 contains active matching TimeLine events2176/92176 targeting3030. cap_2man includes both, in each free-mode pass.

No sheet edit is required for these two rows. Existing action/active/destination filters remain. SoloDungeon teleport events98321/98322 target9126 but are absent from the captured reset; they were not added to the matching-event set.

`T184fQueue_timeline_events_restore_full_captured_dungeon_reset` checks all1066 bytes against the unmodified C1#1283 HEX (also verified equal to C2#912). Its34 target groups/66 event IDs were verified against live XML, including the two TimeLine rows. Two additional tests pin both pool acknowledgments, queued state, FIN, and each solo DPS application's progress to the new capture. No changes to MatchQueueManager were necessary for these packet layouts.

## F732 browse statistics, exact new control

All24 real F732 replies are1412 B with25 dungeon rows; all11 queue4 replies are962 B with the same25 IDs. Native scalar names: Arb_part_024.c:11673–11910. Nested six-byte role layout and copy behavior: WorldServer.exe.c:694867–694905,694929,694943–694959. The installed packet definition omits these arrays and must not be used to infer field offsets.

| Field | cap_2man | cap_queue4 |
|---|---|---|
| Header UserPool/PartyPool/showDungeonWorkUI bytes8/9/10 | 1/0/0 in every reply | 1/0/1 in every reply |
| Role-status rows on every dungeon | three `(mask,status)` pairs `(1,1),(2,1),(4,1)` | empty |
| MatchTimeStatus on all dungeons except9781 | 3 in every reply | 0 in every reply |
| 9781 MatchTimeStatus | 3 or1 according to matching history, detailed below | 0 throughout |
| 9781 RequiredActPoint |110 |110; **not a mismatch** in this control |
| 9781 Convoke/NextOpenTime/Newbie/IsEvent |0/-1/0/1 |0/-1/0/1 |
| 9781 FailReason immediately before application | -1 | -1; C2's earlier#1558/#1565 were0 before later eligibility changes |

The25 IDs in wire order are3036,3126,3202,3203,3026,3027,3103,9044,3023,3102,3201,9920,9982,9735,9739,9794,9780,9781,9053,3030,9809,9727,9025,9026,9047. The450-byte size difference is exactly25×3×6 role-status bytes. Rows' absolute offsets change accordingly;9781 starts929 in real,623 in queue4. Its status is at+964 real/+658 queue4, its role descriptor at+941/+943 real and+635/+637 queue4. RequiredActPoint is+960 real/+654 queue4.

9781 time-status observations:

- C1 #1191/#1200/#1236=3; #4196/#4202/#4251/#4257=1; #4297/#10417/#10423=3.
- C2 #802/#808/#825/#903/#909=3; #4272/#4279=1; #4293/#4337=3; #8038/#8044/#8073/#8097=1; #8144=3.

Every listed frame retains all three role-status pairs. The selected9781 control disproves the earlier cross-dungeon hypothesis that RequiredActPoint must be zero or MatchTimeStatus must always be2. It does not establish that browse statistics alone cause the popup.

Real A→W0x1644 contains39 rows,2007 B, in all24 instances; queue4's23 instances are18 B with an empty array. Example real starting tap record1289+0 (ends1289), versus queue4 record5174+0 (ends5174). No0x1645 occurs in either tap. AS0x1644 is Arbiter→World, not a captured MatchServer message.

## Statistics producer and the absent AM/MA wire

No frame in the real Arbiter–World tap has opcode0x4650–0x46FF. No7803/AM–MA byte pair is available. Native AM/MA layouts and behavior below are **decompile-derived**, never capture pins.

- MA_MATCH_POOL_INFO=0x4668: Arb_part_003.c:6061–6062. Header14 B; outer array descriptor at6/10;29-byte rows contain child descriptor8/12, matchingId16, matchingType20, freeMatching24, MatchTimeStatus byte28. Children10 B contain role mask byte8/status byte9. Scalar names: Arb_part_016.c:4897–5012.
- Arb_part_069.c:10660–10680 dispatches to `FUN_140813bd0`. Arb_part_070.c:14574–14627 updates per-dungeon cached time/role statuses and stamps the receipt time. It updates supplied role entries; it does not clear missing role keys. Disconnect clears the map through Arb_part_069.c:3176–3187, called at Arb_part_076.c:15834.
- Arb_part_039.c:5230–5340 serializes the cache into0x1644 with21-byte dungeon rows and10-byte role rows. World writes the client-side six-byte equivalents.
- MatchServer decompile is `match_decompiled/ArbiterServer.exe.c` (the filename is misleading). M:230836–231011 (`FUN_14011d2f0`) publishes0x4668 for configured freeMatching0 pools. It calls `FUN_1400ddc90` at230895 to compute the statuses. The constructor writing opcode is M:258098–258119.
- Statistics store initialization M:177439–177461 and226968–226970 fixes1000ms buckets,600000ms span,600 buckets. M:227197–227200 repeats this initialization. These constants are not the XML refreshTime attributes.
- M:177742–177800 rotates buckets using `_time64()*1000`, only when elapsed is strictly greater than1000ms. First call sets last=`now-now%1000`, index=`(now%600000)/1000`. Later it advances/clears at most600 pending buckets, updating last by1000ms each step, then samples the current bucket once. The reviewed rotation clears pending-ID sets. **No completion-bucket clearing was found in this function; a ten-minute completion expiry must not be asserted from the ring size alone.**
- M:177873–177908 resets current tank/dealer/healer counts, walks applications excluding state4, accumulates their selected roles through `FUN_140141940`, inserts unique application IDs into the current pending bucket, and marks the cached display dirty. Role counts are current application counts, not sums across history buckets.
- M:177692–177695 initializes each configured role group's display status1 and time status3. Recalculation M:178263–178268 starts with the same defaults.
- M:178383–178403 builds each role mask from the template group; normal matching uses configured groups, free matching uses one combined{0,1,2} group. Expected group count is `floor(sum((roleMin+roleMax)*0.5)+0.5)` at178427. Actual count is the sum of current matching counts for that group.
- M:178452–178516 maps current count to status1/2/3/4 using strict `>` comparisons to `checkNum1Max/2Max/3Max * expectedGroupCount`. M:57953–57964 reads those maxima. The `checkNum*Min` and refreshTime attributes are read and discarded at that loader.
- Time status M:178250–178263 uses numerator `1000 * sum(pending bucket membership counts) + sum(completion wait seconds)`, denominator `distinct pending application IDs + completion count`. This is not a direct average of current wall-clock queue ages. With a positive denominator, status1 if average<=checkTime1, status2 if average<=checkTime2, else3 (178520–178574). With no samples, retain3. M:57966–57974 reads thresholds directly; no×1000 conversion.
- Completion M:178025–178038 adds one completed application's elapsed seconds and count to the current completion bucket, removes its application ID from every pending bucket, and marks dirty. The caller M:235373–235381/235469–235477 supplies `_time64()-applicationStartTime`, establishing seconds. This explains why a fresh queue can show3 and the successful destination later shows1.
- Native publication cadence: `WorldOfPartyMatchManager::OnTick` M:228298 reschedules itself after1000ms (228393), but calls `TryToPartyMatchOnTick` only when `last+10000<TLStick` (228363–228367). TLS tick is GetTickCount64 minus a process origin (15586–15587). `TryToPartyMatchOnTick` (231580/231632) forms parties before calling statistics rotation and publication at232022–232023. This fills one pending bucket per matching tick; it does not fill every elapsed second or refresh on every client browse.

All completion-history reset paths have not been exhaustively ruled out; the traced tick path retains completion counters. The new tap does not contain the source MA4668 packets or MatchServer clock, so tests for these internal rules must identify themselves as decompile-derived. Capture pins establish the resulting AS1644/F732 bytes separately. Browse implementation and its tests are coordinated in the entry agent's files.

## Additional sheet providers for the matched-party fix

Added `DatasheetLoader.RaidPartyNames.Value` (six strings): StrSheet_BattleField.xml String IDs10000001..10000006. Arb_part_067.c:14533–14574 selects these defaults; Arb_part_082.c:4830–4878 returns an empty string on missing row,9850–9872 reads this sheet/string attribute. Live lines185–190 contain the six localized names seen in C1#1280. No locale is hardcoded into production.

Added `DatasheetLoader.PartyLootDefaults.Value[0]`: WorldData.xml PartyLootingOption. Arb_part_067.c:2190–2203 copies native configuration into each party; Arb_part_058.c:13553–13568 loads the seven attributes. The live row1883 gives method1, grade1, distribution0, equipment=false, class=true, bound-item distribution1, combat-forbidden=false. Missing attributes on an existing row use native loader defaults. A missing file/row intentionally preserves legacy `Party.DefaultLoot` as the standalone compatibility fallback; it is explicitly **not** claimed to be the native WorldParameter constructor default.

Production changes in this fragment: TimeLine classification and the two sheet providers only. Root runs integrated T184f tests/full suite after all agents finish; no build was run by this agent while shared files were changing.

## Vanguard reward claim in the same capture

Client1 #10212 is `C_COMPLETE_DAILY_EVENT` (0x4FBB, 16 B), event2142;
client2 never sends this action. This is a reward claim. EventMatching.xml:1645
maps it to repeat quest700029/target9781. Tap8450 forwards the unchanged client
frame inside AS13F6. World owns validation and completion
(`WorldServer.exe.c:573589–573646`); the existing dispatcher fallback already
forwards this opcode correctly.

The existing DB handlers cover the captured ordinary rewards: tap8451→8452
(272E→272F, quest completion),8515→8516 (2736→2737),8517→8518 (2891→2892,
reputation),8521→8522 (2768→2769, money12,500,000 and items45474×1/1300×30),
8533→8534 (27B1→27B2, EP),8544→8545 (2732→2733, repeat quest re-offer).
Client1 #10303 displays those items/money plus EXP33,000,000 and EP1518;
#10305 applies reputation609 +50; #10317 re-offers event2142. The sheet's
reputation base is25, so World must retain authority over the actual reward.
No273B EXP,293C daily-count or2965 additional-count write appears in this
bounded claim sequence; none was synthesized.

`T184fVanguard.cs` adds three tests with13 exact full tap frames, exercising
the World tunnel, captured DB replies and persistent money/items/reputation/EP/
quest state. Allocated database IDs alone are normalized in the store test;
separate builder tests match the complete captured IDs and bytes. No ordinary
Vanguard production rewrite was justified by these captures.

One auxiliary gap remains: tap8455 is `SA_DAILY_EVENT_COMPLETE` (1593, 14 B).
Native Arb062:5110–5127 sends AccountServer promotion notification type2
(Arb072:2536–2579) and attempts an eligible stack-attendance campaign reward
(Arb050:12987 onward). TeraSharp lacks those campaign side effects. There is no
campaign response in this claim or AccountServer wire in this tap to pin an
active campaign; no invented reward or World ack was added. This does not
replace the ordinary reward path above. Detailed evidence is in
`obj/t184f_vanguard/status.md`; integrated test results are owned by the root run.
