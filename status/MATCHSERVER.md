# T184 — MatchServer protocol and matching audit

2026-09-23. Worktree `TeraSharp-cowork`, branch `cowork/T8`, starting commit `df77c8a`.

## Result and scope

**Two players did not fill role 17 because `minMatchingMember` is admission size, not completed group size.** World checks it against each applying party. The production completion size is `totalUser=5`, with one tank, three dealers and one healer. QA selects `totalUserQa=3` only when the QA switch is enabled; even that is not two. Changing the minimum to two also excludes solo applications. The local role-17 row does not contain the reported live `minMatchingMember=2` edit; the deployed file was not available to verify.

Proven fixes in this worktree: load dungeon totals/role bounds from the sheets; check application size; select whole fixed-role applications instead of a greedy prefix; send queued-pool/state frames to both members; correct progress counts; clear all matching destinations in both free-matching modes; preserve physical roster holes; stop interpreting `C_MATCH_DEL` as a rejection of an already formed party. The popup is **not live-verified**.

The protocol inventory covers all 33 AM/MA messages. It is decompile-derived, not a replay of a captured 7803 connection. Two nested statistics fields/groups are documented by width/offset with unresolved semantic names. The wait-role fallback consumer and post-FIN relog/expiry rules remain unverified. TeraSharp still has no MatchServer connection, confirmation-query implementation, supplement-room implementation, or complete retail admission pipeline. Those limits are not hidden by the passing tests.

## Sources and reproducibility

The supplied filename `match_decompiled/MatchServer.exe.c` does not exist locally. The new file is **`D:\v100\TERA_SERVER.100\match_decompiled\ArbiterServer.exe.c`**, but its `Handler_AM_*`, `MatchServerConfig`, and source paths identify MatchServer. It is abbreviated **M** below. SHA-256: `B1A6B4DA7042680E9ED651F061009CA048743FCD10935594D952EB7464A2A9BC`.

**Axxx:L** means `D:\v100\TERA_SERVER.100\Arb_part_xxx.c`, line L. **W:L** means `D:\v100\TERA_SERVER.100\world_decompiled\WorldServer.exe.c`, line L. **Sheet** means `D:\v100\TERA_SERVER.100\Executable\Datasheet`.

Client references are record numbers, not file line numbers, under `D:\packetlogs`. The `_ctl.txt` summaries are useful indices but truncate long payloads; complete `HEX:` records in the corresponding `.log` were used for full-frame comparisons. The SQLite T179 index was used to locate bytes, then these **33 records were independently checked against the original HEX text**:

- `classic_live3.log`: 8643, 8662, 8664, 8678, 10333, 10335, 10346, 10580, 10581, 10585, 10587, 11521, 12091.
- `classic_live2.log`: 9335.
- `cap_queue2_client1.log`: 917–920, 1666–1669.
- `cap_queue2_client2.log`: 1293, 1299, 1314, 1320–1323, 1527–1530.

Capture SHA-256 values:

| File | SHA-256 |
|---|---|
| classic_live3.log | `617CBFFE61B8E735C8DBC115F2E306AB723ED096B44839A8DA52B1F55EE60D36` |
| cap_queue2_client1.log | `FBDA03EA912A11370C633A7F0EDBE197A0CD28037CE6311BB043BDEBEE83755D` |
| cap_queue2_client2.log | `3D8FD4474C50621C60FA8890853DDC3EA9A94757AB0BE3D7E37C2FBB6C44AE54` |

## 1. MatchServer ↔ Arbiter wire inventory

Opcode names/numbers: A003:5983, 6015–6078. MatchServer's Arbiter listener configuration: M:206373; XML port default `0x1E7B=7803`: M:224753. **AM** travels Arbiter → MatchServer; **MA** travels MatchServer → Arbiter. The MA request for a battlefield ID followed by the AM answer is intentional.

All offsets here are **decimal, from the start of the complete frame**. Frame header is `[u32 length][u16 opcode]`. Fields are packed, without alignment. Unqualified numeric fields occupy four bytes. `q` means eight bytes; `b` one byte; `f` IEEE float32; `s` a four-byte frame-relative offset to UTF-16Z; `L` an eight-byte linked-list descriptor `[u32 count][u32 head]`. Every list element begins `[u32 here][u32 next]`. String storage and list elements follow the minimum frame. Do not substitute the DB blob offset/byte-length convention for these PDL linked lists.

### Arbiter → MatchServer

The consumer column is the MatchServer `Handler_AM_<name>`; there is no Arbiter receive handler for its own outgoing AM message. A011 ranges are the independent Arbiter-side dumpers.

| Op | Name after AM_ | Minimum B; full-frame fields | Arbiter dumper | MatchServer consumer |
|---|---|---|---|---|
| 4651 | REGISTER | 18; version@6, planet@10, result@14 | A011:1431–1476 | M:101502 |
| 4653 | GET_BATTLEFIELD_ID | 18; token:q@6, battlefield@14 | A011:1181–1215 | M:103360 |
| 4655 | ADD_TO_PARTY_MATCH_POOL | 50; matching:L@6, users:L@14, poolType@22, planet@26, party:q@30, room:q@38, sysPartyType@46 | A011:427–838 | M:101635 |
| 4658 | DEL_FROM_PARTY_MATCH_POOL | 38; matching:L@6, poolType@14, party:q@18, planet@26, user@30, delReason@34 | A011:967–1156 | M:102122 |
| 465A | MATCH_CANCEL | 26; party:q@6, planet@14, user@18, reason@22 | A011:1215–1269 | M:102417 |
| 465C | PRE_PARTY_MAKE_RESULT | 19; result:b@6, partyMakingId@7, userPlanet@11, userDb@15 | A011:1377–1431 | M:102229 |
| 465D | DICONNECT_BATTLE_FIELD_SERVER | 7; isConnect:b@6; spelling is the binary's | A011:1156–1181 | M:102215 |
| 465E | PARTY_MAKE_RESULT | 27; result:b@6, partyMakingId@7, party:q@11, inviteePlanet@19, inviteeDb@23 | A011:1269–1332 | M:102243 |
| 465F | PARTY_MAKE_RESULT_FROM_INSTANCE_SERVER | 22; party:q@6, inviteePlanet@14, inviteeDb@18 | A011:1332–1377 | M:102256 |
| 4661 | ASK_TO_JOIN_PARTY_RESULT | 27; result:b@6, party:q@7, userPlanet@15, userDb@19, queryId@23 | A011:838–901 | M:102283 |
| 4666 | DEL_FROM_ALL_POOL | 14; matchingId@6, reqReason@10 | A011:933–967 | M:102355 |
| 4669 | COMMAND | 10; command:s@6 | A011:901–933 | M:102469 |
| 466A | SUPPLEMENT_ADD | 30; parties:L@6, battlefield@14, planet@18, matchingId@22, matchingType@26 | A011:1476–1635 | M:102566 |
| 466B | SUPPLEMENT_CANCEL | 22; parties:L@6, battlefield@14, matchingId@18 | A011:1635–1747 | M:103086 |
| 466C | SUPPLEMENT_PING | 26; parties:L@6, world@14, battlefield@18, matchingId@22 | A011:1747–1868 | M:103171 |
| 4670 | WORLD_SERVER_STATUS | 30; instances:L@6, controlPlanet@14, planet@18, world@22, updateType@26 | A011:1868–2026 | M:103260 |

AM linked records (offsets below are relative to each record):

- **4655 Matching, 28 B**: links@0/4, members:L@8, matchingId@16, matchingType@20, freeMatching@24. Nested **member, 21 B**: links, planet@8, db@12, marker:b@16, role@17. Writer A076:11185–11234.
- **4655 User, 48 B**: links, choices:L@8, statistics:L@16, name:s@24, planet@28, db@32, class@36, trueItemLevel:f@40, level@44. **Choice, 16 B**: links, matchingId@8, role@12. **Statistics, 24 B**: links, matchingId@8, three four-byte values@12/16/20. Their copying is proved by M:101684–101710; the three semantic names were not independently resolved in this audit. A076:11162–11340 constructs the packet.
- **4658 Matching, 20 B**: links, matchingId@8, type@12, free@16.
- **466A Party, 28 B**: links, users:L@8, teamIndex@16, party:q@20. **User, 48 B**: links, name:s@8, planet@12, db@16, class@20, true-item-level bits@24, level@28, role@32, statistics words@36/40/44. M:102587–102613, 102758–102769, 102853–102880 independently consume these positions. Statistics names remain unresolved as above.
- **466B / 466C Party, 16 B**: links, party:q@8.
- **4670 Instance, 16 B**: links, instanceId@8, instanceCount@12.

### MatchServer → Arbiter

Every receiver is `Handler_MA_<name>` in A069 at the listed line. The independent field dumpers are A016.

| Op | Name after MA_ | Minimum B; full-frame fields | Dumper | Arbiter handler |
|---|---|---|---|---|
| 4650 | REGISTER | 10; matchVersion@6 | A016:5392–5417 | A069:10817 |
| 4652 | GET_BATTLEFIELD_ID | 14; token:q@6 | A016:4798–4823 | A069:10492 |
| 4654 | UPDATE_BATTLEFIELD_ID | 10; battlefield@6 | A016:5624–5649 | A069:11052 |
| 4656 | ADD_TO_PARTY_MATCH_POOL | 38; matching:L@6, users:L@14, poolType@22, party:q@26, remainSec@34 | A016:3428–3732 | A069:8365 |
| 4657 | ADD_TO_PARTY_MATCH_POOL_FAIL | 50; sysMsg@6, poolType@10, party:q@14, planet@22, db@26, room:q@30, matchingId@38, type@42, free@46 | A016:3732–3831 | A069:8723 |
| 4659 | DEL_FROM_PARTY_MATCH_POOL | 46; matching:L@6, poolType@14, party:q@18, planet@26, db@30, reason@34, reqReason@38, elapsedSec@42 | A016:4038–4240 | A069:9203 |
| 465B | MATCH_CANCEL | 26; party:q@6, planet@14, db@18, reason@22 | A016:4823–4877 | A069:10547 |
| 4660 | ASK_TO_JOIN_PARTY | 34; matchingId@6, party:q@10, userPlanet@18, userDb@22, queryId@26, remainingTime@30 | A016:3831–3903 | A069:8814 |
| 4662 | ASK_TO_JOIN_PARTY_ABORT | 30; matchingId@6, party:q@10, userPlanet@18, userDb@22, queryId@26 | A016:3903–3966 | A069:8949 |
| 4663 | FIN_PARTY_MATCH | 58; members:L@6, sysParty:q@14, ownerPlanet@22, matchingId@26, free@30, clearCompensation:b@34, sysPartyType@35, raid:b@39, maxMembers@40, supplement:b@44, anonymous:b@45, teamIndex@46, battlefield@50, selectedWorld@54 | A016:4240–4798 | A069:9428 |
| 4664 | SYSTEM_MESSAGE | 18; users:L@6, sysMsg@14 | A016:5462–5590 | A069:10959 |
| 4665 | SYSTEM_MESSAGE_USING_INTER_PARTY_MATCH | 18; sysMsg@6, party:q@10 | A016:5590–5624 | A069:11005 |
| 4667 | MATCH_ROOM_INFO | 59; users:L@6, parties:L@14, room:q@22, dungeon@30, free@34, tank@38, dealer@42, healer@46, createTime:q@50, isDelete:b@58 | A016:5139–5392 | A069:10784 |
| 4668 | MATCH_POOL_INFO | 14; pools:L@6 | A016:4877–5040 | A069:10667 |
| 466D | SUPPLEMENT_PONG | 15; world@6, battlefield@10, onMatching:b@14 | A016:5417–5462 | A069:10897 |
| 466E | MATCH_PROGRESS | 46; party:q@6, planet@14, db@18, matchingId@22, type@26, free@30, tankerCount@34, dealerCount@38, healerCount@42 | A016:5040–5139 | A069:10706 |
| 466F | ASK_TO_JOIN_PARTY_PROGRESS | 34; party:q@6, planet@14, db@18, queryId@22, totalCount@26, acceptCount@30 | A016:3966–4038 | A069:9044 |

MA linked records:

- **4656 Matching, 28 B**, and nested **member, 21 B**, use the same layout as AM 4655. **User, 20 B**: links, name:s@8, planet@12, db@16.
- **4659 Matching, 20 B**: links, matchingId@8, type@12, free@16.
- **4663 Member, 58 B**: links, name:s@8, planet@12, db@16, **role@20**, class@24, level@28, supplementCompensation:b@32, isSoloMatching:b@33, trueItemLevel:f@34, countDungeonClear@38, winRate@42, winCount@46, battlefieldScore@50, **elapsedSec@54**. Do not mislabel the last word as the role. A016:4408–4792.
- **4664 / 4667 User, 16 B**: links, planet@8, db@12. **4667 Party, 16 B**: links, party:q@8.
- **4668 Pool, 29 B**: links, nested:L@8, matchingId@16, type@20, free@24, matchTimeStatus:b@28. Nested records are **10 B**, links followed by two bytes@8/9; M:230896–230933 writes them. The first is a role-combination bit mask (M:178395–178403); the second is its status byte, initialized to 1 at M:178268. The complete status-enum meanings were not resolved; these are not two timestamp words or a variable-size opaque blob.

### WorldOfPartyMatchHelper path and 1644/1645

`C_MATCH_ADD` is normally processed by **World**, including leadership, eligibility and per-application checks (W:582280 onward; `MatchingBasicCheck`, W:3035930). World sends **SA_ADD_TO_INTER_PARTY_MATCH_POOL 0x13A9**; A062:1270 onward resolves/validates the party and member information, then WorldOfPartyMatchHelper constructs AM 4655 (A076:11162–11340). Names/numbers are independently mapped at A003:4558–4561.

MA 4656 dispatches back through the helper (A069:8365). The helper builds client C730 and queued 87AC, and sends them to **each local party member**, A076:14950–15114. MA 466E dispatches by party ID when nonzero, otherwise by PDId (A069:10717–10769), updating the application's three role counts (A077:9612–9672). `C_MATCH_PROGRESS` reads those counts (A077:8828–8955); no application means no response at A077:8848. The party-member path resolves by party ID (A077:9059–9116); T184 also fixes local progress lookup for nonleaders.

MA 4663's owning Arbiter constructs the party/member roles (`New_CreateParty`, A079:15162; call A069:9739), then runs `DoFinPartyMatch` (A069:9808 → A079:3223–3334). Cross-Arbiter completion also reaches this helper through AA_DO_FIN_PARTY_MATCH, A082:7619. Party creation has a minimum of two and rejects already-system-party members (A079:15165, 15228–15245).

**0x1644/0x1645 are Arbiter → World, not the 7803 protocol.** `C_VIEW_INTER_PARTY_MATCH_DUNGEON_LIST` / `...BATTLEFIELD_LIST` are forwarded to the user's World by A041:14307–14434. The captured empty-list request is 18 B: header, two zero descriptor words@6/10, UserDbId@14. World supplies the eligible client list from its own data. Existing `BuildAsViewInterPartyMatchList` already produces this empty request; no change was needed. The nonempty list's complete element schema was not re-derived here.

TeraSharp intercepts C_MATCH_ADD/DEL instead of routing through this World admission path and does not speak AM/MA. This patch corrects its local matcher; it does **not** make it a drop-in MatchServer client.

## 2. Formation rules, two players, and waiting

| Input | Evaluation in real code | Consequence |
|---|---|---|
| `Dungeon.matchingRoleId` | M:218010–218082 resolves DungeonMatching then the role's first team | The dungeon ID alone does not imply a five-person 1/1/3 party |
| `totalUser` | M:81177–81181; completion M:216390–216500 | Exact completed-team size |
| `totalUserQa`, `healerMinQa`, `healerMaxQa` | Used only with sheet QA flag set and value != -1, M:81164–81191; QA toggle/reload M:199072–199090 | Merely having QA attributes in XML changes nothing in production |
| tank/dealer/healer min/max | Enumerate integer compositions with T+D+H=total, all bounds satisfied, M:216390–216500 | Role 17 normal mode yields exactly (1,3,1) |
| `minMatchingMember` / `maxMatchingMember` | Loader M:81515–81534; World checks count against role+0x2C/+0x28, W:3035963–3035982 | Size of **one applying party**, not aggregate pool size |
| Per-role matching admission bounds | W:3035983–3036027 checks role+0x10/+0x1C arrays | Separate from completed-team role minima; not yet implemented in TeraSharp |
| Partially filled application | W:3036028–3036034 checks whether the premade can fit; complete applications must satisfy full rules | A two-DPS premade may join a five-person queue, but is not a completed team |
| FreeMatching != 0 | M:216390–216500 bypasses composition bounds, retaining total size | Free matching does not imply a smaller team |
| Whole queued parties | DP in M:217054–217975; skips busy/confirmation entries | Never split a premade to fill another party |
| Form/tick | M:231632–231738 tries nonempty destinations; `MatchForParty`, M:234769–235027, considers supplements and new complete groups | A full permitted combination is needed before a new group forms |

Role 17 in the local sheet (line 54) is normal total 5 / QA total 3, tank 1..1, dealer 1..3, healer 1..1. Role 27 (line 86) makes the distinction particularly explicit: **total 10, maximum application 3**. Setting only role17.minMatchingMember=2 cannot produce a two-player match. Two separately queued soloists are additionally below that admission minimum. The code test instead uses an explicit `totalUser=2, tankerMin/Max=0, dealerMin/Max=2, healerMin/Max=0` fixture and proves that two DPS form without the environment override. That fixture is a test rule, not a proposed retail default or a shipped XML edit.

### Wait-role fallback — what is and is not proved

M:81084–81107 parses `changeRoleTime`, `changeRoleId`, and `changeLimit` (tank=0, dealer=1, healer=2, unspecified=-1). The time and ID defaults are zero. Local role 23 explicitly says `changeRoleTime=30`, `changeRoleId=24`; role 17 has **only** `changeRoleId=24`. Role 24 permits broad composition but still has **totalUser=5**, QA 3. Even applying it would not allow two players.

The role23 XML comment describes changing after the wait, but **the runtime reader of the stored time/ID was not pinned**. The audited MatchServer dungeon constructor loads the original role directly (M:218043–218082); the matching tick/DP and World role-initialization reads did not establish a timer transition to role24. Do not infer a universal 30-second fallback, or “zero means immediate”, from these attributes. No speculative timer was added. A real 7803 capture across a role23 queue lasting more than 30 seconds, with progress and role choices recorded, is needed alongside continued references to role-base+4/+8/+12 to settle the trigger.

## 3. What each client receives; exact mismatches

Client frame header is `[u16 length][u16 opcode]`; client linked-array links are **u16**, unlike the interserver u32 links.

| Stage / frame | Real bytes and source | cap_queue2 / TeraSharp finding | T184 action |
|---|---|---|---|
| Queued C730 `S_ADD_INTER_PARTY_MATCH_POOL` | Header 12 B: count@4, head@6, **i32 RemainSec@8**. Matching row 20 B: links@0/2, member count/head@4/6, id/type/free@8/12/16. Member row 17 B: links, planet@4, db@8, marker:b@12, role@13. A076:14950–15035 | Runtime sent only to the requester and encoded only the first requested destination. Its default type was dungeon even for BG. The old comment misidentified RemainSec as a second array | Send pool then state to all members; include all requested rows; preserve remaining-time scalar; set type; order the nested PDId map |
| Queued 87AC | `classic_live3` 8664 / 10335: 34 B, queued=1, eventType=1, IDs **800029,2154,92151** | Data-driven T161b builder already handles nonempty lists; broadcast was missing for other members | Broadcast alongside pool |
| Progress 8BE4 | `classic_live3` 8678: 36 B, row **[9739,0,0,0,1,0]**; 10346 same player as tank **[9739,0,0,1,0,0]**. Row six i32 fields are id,type,free,tank,dealer,healer | `cap_queue2_client2` 1293/1299/1314: **[9781,0,0,1,1,1]** for one DPS. Pre-T184 wiring filled these from party size three times | Correct the three role counts and stop fabricating a row for a nonexistent application |
| Match-found 87AC dungeon reset | `classic_live3` 10580: **730 B, 90 IDs**, queued=0,type=1 | `cap_queue2_client1` 917 and client2 1320: empty 10 B frame. T161b subsequently suppressed empties, but pre-T184 still reset only the selected destination | Expand all destinations, not just the selected dungeon; two free-mode passes |
| Match-found 87AC BG reset | `classic_live3` 10581: **1290 B, 160 IDs**, queued=0,type=0 | `cap_queue2_client1` 918 and client2 1321: empty 10 B frame | Same expansion, separate nonempty BG list |
| FIN 6470 | `classic_live3` 10585: **16 B**, i32 `[9739,0,0]`. A079:3294–3314 | client1 919 / client2 1322: `[9781,0,0]`. Dungeon ID is state, not a structural error | Existing dungeon frame retained |
| SYS 74C7 | `classic_live3` 10587: **488 B**, 30 physical slots. Five occupied slots listed below, then 25 empty. A079:3223–3243 | client1 920 / client2 1323: same 30-slot format, first two (2800,9,1),(2800,10,1); correct for that formed roster. General builder compacted holes on later reconstruction | Preserve holes; no recipient rotation, role sorting, or extra flag invented |

**Pool order differs from roster order.** Pool members are traversed as a PDId-keyed map (A076:14990 onward). `classic_live2` 9335, a member-side 66 B pool, contains `(2800,3561,marker0,role2)` then `(2800,24468,marker0,role0)`, type=1, free=0, remain=0. The marker compares the member PDId with a separate MatchingInfo PDId; it is not established as “party leader”. Do not set it to one for the leader. Tests pin this full frame and `classic_live3` 8662 byte for byte.

**The SYS roster is the same physical slot order at every recipient**:

| Slot | Planet | UserDbId | Role |
|---|---:|---:|---:|
| 0 | 2800 | 4742 | 0 tank |
| 1 | 2800 | 138395 | 2 healer |
| 2 | 2800 | 100402 | 1 dealer |
| 3 | 2800 | 134744 | 1 dealer |
| 4 | 2800 | 117771 | 1 dealer |
| 5–29 | -1 | 0 | -1 |

Each SYS element is `[u16 here,next][i32 planet][i32 db][i32 role]`, 16 B. There is **no flags field**. The MA_FIN member sequence feeds party construction; this audit does not claim that MatchServer universally sorts that sequence tank/healer/DPS, by PDId, or by arrival time. `cap_queue2`'s second formation reverses the two DPS slots, identically at both recipients (client1 1669 / client2 1530); that alone is not a defect. The later World-produced `classic_live3` 12060 88 B five-slot SYS is not the initial Arbiter 488 B packet and must not be used as its fixture.

For a normal dungeon each local member sees the party member list first, both applicable event resets, FIN, then SYS. `classic_live3` has member lists at 10568–10579, resets 10580/10581, FIN 10585, private-chat interleaving 10586, SYS 10587. A079:3245–3249 expands the clear operation, 3264–3273 sends World AS_CHANGE_EVENT_MATCHING_STATE(false), and 3294–3334 sends FIN then SYS for normal system-party type. This is ordering, not an assertion that no unrelated packet can interleave. No teleport is performed by FIN: the captured client later sends C_ENTER_DUNGEON at 11521 and receives S_LOAD_TOPO at 12091.

### Exact source of the event IDs

EventMatchingDataSheet maps destination IDs to EventMatching event IDs (A008:11885–11929). These are not a dungeon ID, quest-progress count, or constant array. `DoFinPartyMatch` passes MatchingInfo(-9999,2,2). Expansion in A079:6934, 7274–7326 walks every matching dungeon and battlefield in key order for free=0 and free=1. **The repeated ID halves are intentional.** Both captured lists consist of two identical halves: 45 dungeon IDs and 80 battlefield IDs. The full literal frames, including all links, are checked into `T184.cs`; the capture-derived input fixture is explicitly labelled, rather than presented as the contents of the local sheet.

The runtime loads local EventMatching.xml and filters targets against DungeonMatching.xml / BattleFieldData.xml when those files exist. Without the destination files it retains the older target-map fallback. The local files produce **128 dungeon IDs / 1034 B** and **68 battlefield IDs / 554 B**, compared with retail **90 / 730 B** and **160 / 1290 B**. These are different data sets. The patch does not hardcode the retail IDs over the deployed data. The initial SYS and FIN in cap_queue2 were already structurally correct; the bytes do not prove that changing either would restore the popup.

## 4. Confirmation, decline, expiry, “enter later”, re-offer

There are two distinct phases. The following describes the phase boundaries found in code; it does not treat T161 comments as evidence.

| Event | Real implementation / evidence | TeraSharp status |
|---|---|---|
| Complete group with role `autoConfirm=true` | Default true, M:81116. Normal new dungeon group bypasses a confirmation query, M:235072–235080; continues to completion at M:235218 onward | Current immediate formation fits the captured normal dungeon path |
| Explicit confirmation required | `autoConfirm!=1`, or the identified supplement-owner pool-type-2 condition, M:235072–235074. Sends MA_ASK_TO_JOIN_PARTY with queryId and remainingTime | No implementation; FIN is not a substitute for this query |
| Confirmation timeout | DungeonMatching `ConfirmQuery.waitSec`, default 30 (M:57981–57984). Timer uses seconds*1000; BF has its own settings (M:120973–121020) | T161's post-FIN 300 seconds is unrelated |
| Accept | AM_ASK_TO_JOIN_PARTY_RESULT; query tracks accepted members, reports total/accepted via 466F, commits when all accepted (M:238271, 238441–238445) | No matching query state |
| Decline before formation | Deny validation M:121305–121389; M:238454–238458 removes the declining application and aborts that query | No retail query handler; do not map this onto C_MATCH_DEL after FIN |
| Query expires | Timeout marks the query, M:121468–121474. OnQueryConfirmAbort partitions participating/accepted entries, removes nonaccepting applications and aborts/resumes surviving ones, M:238504–238854 | Cancelling every formed party is not this algorithm |
| C_MATCH_DEL | World validates party authority and rate limit, W:583161–583220, then sends SA 13AA. A062:6233–6281 deletes queue applications through the helper | T184 removes the proven-wrong post-FIN `Decline` call. Existing local queue-leave reply behavior otherwise retained |
| “Enter later” after FIN | MA_FIN and S_FIN contain no post-FIN deadline. The party carries its destination. Actual entry is separate C_ENTER_DUNGEON → World, as captured | No separate “enter later” wire message was identified; a user can remain in the formed party without entering immediately |
| Relog/re-offer | Audited DoFinPartyMatch calls are MA completion and cross-Arbiter AA completion; no login re-offer caller was located | T161 FIN/SYS resend on enter-world remains **an unverified local policy**, not capture/decompile parity |
| Five-minute formed-match expiry; crash/leave cancels all waiting members | Not found in the audited MatchServer query/room completion code. Party withdrawal and World entry are separate paths | Existing T161 policy remains; this patch does not replace it with another guessed timer. It needs a targeted Arbiter/World lifecycle trace and real capture |

The existing **600-second queue timeout** also derives from a UI `MatchingTimeDisplay.standardTime` interpretation in T138, not a proved queue-expiry path here. No blanket claim of retail timeout parity is justified. Negative call-site searches are not proof that a runtime callback can never re-offer a match.

## 5. Changes and tests

Changed source files:

- `src/TeraSharp.Arbiter/World/DungeonMatchRules.cs` (new): production total/role bounds, QA parser fixture support, per-application size. First RoleData team only, as used by the audited dungeon path.
- `src/TeraSharp.Arbiter/World/DatasheetLoader.cs`: register that loader; exclude event targets absent from available destination sheets.
- `src/TeraSharp.Arbiter/World/MatchQueueManager.cs`: permitted-role templates; atomic fixed-role subset selection; proper role-count progress; wildcard event resets; multi-row, PDId-ordered pool frame and remaining-time field.
- `src/TeraSharp.Arbiter/World/MatchWiring.cs`: use these results, broadcast queued notifications to every member, enforce application sizes, remove the post-FIN queue-delete/decline coupling.
- `src/TeraSharp.Arbiter/World/PartyManager.cs`: preserve physical SYS slots, including holes.
- `src/TeraSharp.Arbiter.Tests/T184.cs` (new): eight regression tests, including direct original-byte pins.
- `src/TeraSharp.Arbiter.Tests/Program.cs`: update one T161b assertion that incorrectly required a reset limited to the selected destination.

No human-owned source file was edited; no seam patch is required. No sheet, production database, branch, or service was changed by deployment.

| Test | Evidence / failure it catches |
|---|---|
| `T184_queue_push_reaches_both_members_and_delete_does_not_decline_formed_party` | Replays classic_live3 8643 through OnMatchAdd; leader socket bytes equal other-member delivery; nonleader progress resolves the party; then queue deletion preserves a formed party. A076:15039–15114, A077:9059–9116, W:583220, A062:6233 |
| `T184_event_reset_uses_only_destinations_in_the_matching_sheets` | A stale event target must not become a matching destination; A079:7274–7326 |
| `T184_queued_pool_matches_member_capture_and_multi_destination_links` | Exact classic_live2 9335 and classic_live3 8662; multi-row links and nonzero remaining time are decompile-derived |
| `T184_fixed_role_subsets_keep_premades_atomic` | An earlier DPS solo must not prevent selecting a later complete 1/3/1 premade; progress cannot borrow half the premade. M:217054–217975 |
| `T184_progress_is_tank_dealer_healer_not_three_party_sizes` | Exact classic_live3 8678 and 10346; no application is silent |
| `T184_match_found_resets_all_destinations_in_both_free_modes` | Exact 730 B 10580, 1290 B 10581, 16 B 10585, in order, using explicitly capture-derived EventMatching inputs |
| `T184_retail_roster_and_physical_slot_holes` | Exact 488 B 10587 at each member; then remove slot1 and prove slots2–4 do not shift |
| `T184_role17_admission_is_not_completion_and_two_member_rule_forms` | Normal vs QA total; min/max applicant sizes; two DPS do not fill role17; explicit two-DPS rule forms; both receive the same derived 30-slot roster |

Validation: `dotnet build src\TeraSharp.Arbiter.Tests\TeraSharp.Arbiter.Tests.csproj --no-restore` succeeded with the same four pre-existing nullable warnings. `dotnet run --project src\TeraSharp.Arbiter.Tests --no-build`: **983 passed, 0 failed, 22 skipped**. All eight new T184 tests passed, none skipped. `git diff --check` passed. Test output is in ignored `obj/t184/tests-final.log`; the source-byte verification script/output is working material under `obj/t184`.

## 6. Remaining defects / next live experiment

1. **Popup still unproved.** Deploy this bounded patch, queue two players under an explicitly two-person test rule, and record both clients plus World↔Arbiter. Preserve deployed EventMatching/DungeonMatching/MatchingRoleTemplate files with the capture. Compare pool notifications to both recipients, party-list → full event clears → FIN → SYS, the World AS_CHANGE_EVENT_MATCHING_STATE, and each user's first subsequent input. Do not change the already-correct filled SYS slots merely because the popup is absent.
2. **Complete World admission is bypassed.** The direct local C_MATCH_ADD path still lacks World's full eligibility, per-role application bounds, and all mixed/free-mode semantics. Parsing supports only the audited normal dungeon rule here. Restoring the real 13A9/13AA path needs handler/handoff work and a concrete human-owned seam patch; this task does not silently activate unsupported SA replay.
3. **MatchServer scope remains larger than this patch.** Supplement rooms, confirmation queries, selected World assignment, BG matching/statistics policy, live progress caches, and random tie/priority ordering are not implemented. Dungeon fixed-role subset selection now finds compatible whole entries; ties are deterministic in local queue order, not claimed identical to retail randomness. Legacy callers without explicit choices retain the older flexible-class seating path. BG progress reports its own application's role counts, not a claimed reconstruction of MA pool progress.
4. **Wait fallback** requires the role-base+4/+8/+12 consumer and a >30-second real role23 trace. Both original and fallback role have total five; none of the unresolved timer semantics makes role17 a two-person rule.
5. **T161 lifecycle guesses remain explicit follow-up work:** re-offer, 300-second post-FIN expiry, crash/party departure effects, and the 600-second queue timeout. Capture a real completed match with one member entering later, one relogging, then withdrawal; distinguish confirmation-query cancellation from completed-party lifecycle. No unsupported lifecycle behavior was presented as decompile-proven.

Merge assessment: the implemented changes have source/capture evidence and passing regression tests. They are suitable as a bounded matching correction. **Do not mark full MatchServer parity, the popup, or post-FIN lifecycle semantics complete on that basis.**

## 7. T184b: queue3 live comparison (2026-09-23)

**Exact divergence: neither member receives a queued dungeon event update or the dungeon reset.** The supplied `D:\packetlogs\EventMatching.xml` contains **zero Dungeon event rows**, against 64 in the T184 reference sheet. Its eight matching targets reproduce the startup log and its complete battlefield reset matches queue3. This is not a retail-equivalent client stream; popup causality remains a live-test question.

`D:\packetlogs\queue3-sheets\` was absent during this audit. The three named XML files were found directly in `D:\packetlogs`; those are the copies compared here. DungeonMatching and MatchingRoleTemplate are byte-identical to `Executable\Datasheet` (SHA-256 respectively `d8dfce3ee8b848d9ccbc383428b7fc92b9c6eb480a9cdcdf901c54195ddaf87d`, `34c14df0dd629726c376c7b97341daf61a307d77283c9d6a688bbd32a7fbfb5d`). Their live formation used `TERASHARP_MATCH_MIN_MEMBERS=2`, explicitly logged at `arbiter-queue3.log:21`; this run does not test role-17 composition.

### 7.1. Received sequence, both members

Numbers below are original client `.log` record numbers; all comparisons use complete HEX bytes. World references use original tap TCP record numbers, also used by its `_ctl.txt`; concatenated/split World frames were reframed per link/direction before inspection.

| Frame / phase | queue3 client1, player 9 | queue3 client2, player 10 | Retail comparison |
|---|---|---|---|
| Pool C730 | **1878**, 49 B | **693**, 49 B | Both receive it. Each is a separate solo application: count=1, remain=0, dungeon=9781, type/free=0/0, one member `(2800,9 or 10,marker=0,role=1)`. Same bytes as classic_live3 **8662**, replacing dungeon 9739 and player 4742. |
| Queued 87AC | **Absent** | **Absent** | Retail **8664/10335**: 34 B, queued/type=1/1, event IDs `[800029,2154,92151]` for 9739. Queue3 has no event mapping for 9781. |
| Progress 8BE4 | **1885,1900,1906,1921**, 36 B | **Absent** | Rows `[9781,type=0,free=0,tank=0,dealer=1,healer=0]`; byte-equal to retail **8678** after dungeon substitution. Retail **10346** is `[9739,0,0,1,0,0]` after switching to tank. Client2's only progress request occurs after formation, when its queue entry has already been removed. |
| Party member list 8BC6 | **1925**, 172 B | **698**, 172 B | Identical between recipients. Retail **10568/10570/10573/10576/10578** builds 1/2/3/4/5 members, 102/172/230/288/366 B. Queue3 sends one completed two-member list. Fields compared below. |
| Party names CCBC | **Absent throughout capture** | **Absent throughout capture** | Retail sends 164 B after each list at **10569/10571/10574/10577/10579**. Additional sequence difference; popup significance unproved. |
| Dungeon reset 87AC | **Absent** | **Absent** | Retail **10580**: queued/type=0/1, 90 IDs, 730 B. The intact T184 local sheet predicts 128 IDs/1034 B; the supplied stripped sheet has zero IDs, so the builder omits this frame. |
| Battlefield reset 87AC | **1926**, **554 B** | **751**, **554 B** | Byte-identical between recipients; queued/type=0/0, 68 IDs = two identical 34-ID halves. Every ID/order/link matches the supplied EventMatching set. Retail **10581** has 160 IDs/1290 B from a different event set. |
| FIN 6470 | **1988**, 16 B | **752**, 16 B | Both `[9781,0,0]`; byte-equal to retail **10585** `[9739,0,0]` after dungeon substitution. World traffic interleaves before client1's FIN. |
| SYS 74C7 | **1989**, 488 B | **753**, 488 B | Byte-identical: 30 physical slots; slots 0/1 `(2800,9,1)`, `(2800,10,1)`, remaining 28 `(-1,0,-1)`. Same header/link/element layout as retail **10587**, whose five occupied roles are `[0,2,1,1,1]`. |
| First subsequent **client input** | **2002**, B4AA `C_SAVE_CLIENT_CHAT_OPTION_SETTING`, 967 B | **756**, F7B8 `C_MATCH_PROGRESS`, 24 B | Retail's first input after 10587 is **11351**, 99B5 `C_CLIENT_LOG`, 3222 B; its later **11521** is `C_ENTER_DUNGEON(9739)`. |
| First subsequent received frame | **1990**, D924 `S_QUEST_BALLOON` | **754**, 9D66 `S_DUNGEON_CLEAR_COUNT_LIST` | Retail **10588**, 92F6 `S_CHANGE_RELATION`. These are unrelated/interleaved pushes, not a popup acknowledgement. |

Client1 sends `C_MATCH_PROGRESS` at 1884/1899/1905/1920, each answered by the next progress frame. Client2 sends only 756, body `0100080008000000F1D8FFFF0000000002000000` (wildcard -9999/type 0/free 2), with no later 8BE4 reply. **Neither client sends C_ENTER_DUNGEON anywhere in queue3.** Neither receives an S_SYSTEM_MESSAGE after its pool frame.

### 7.2. Party fields: differences beyond IDs

`S_PARTY_MEMBER_LIST.8.def` and `PartyManager.MemberListFields` identify the following fields. Tuple order for loot is `mode,minRarity,allEquipment,onlyReqClass,distributeMode,distributeModeBoP,disableCombatLooting`.

| Header field | Retail 10578 | Queue3 1925 / 698 |
|---|---|---|
| ims / raid / memberLimit / anonymized | `1 / 0 / 5 / 0` | **Same**; limit remains 5 despite the two-member test override |
| Party ID; leader planet/DB | `788130832438005373`; `2800/4742` | `788129939084804097`; `2800/9` (live state) |
| Loot tuple | `1,3,0,1,0,0,0` | **`0,4,1,0,1,1,0`**; configured/default behavior difference, not a packet layout mismatch |
| Leader canInvite | **0** | **1**; other members are 0 in both captures |

Every member has planet=2800, online=1 and awakeningLevel=0. Full remaining member fields are `(slot,DB,name,level,class,gameId,canInvite,laurel)`:

| Retail 10578 | Queue3 (identical at both recipients) |
|---|---|
| `(0,4742,dob,65,0,140737671869648,0,1)` | `(0,9,caludesucks,70,12,140737671856131,1,0)` |
| `(1,138395,Mystipouette,65,7,140737671869392,0,0)` | `(1,10,test,70,12,140737671856132,0,0)` |
| `(2,100402,Lilmei,65,0,140737671859202,0,0)` | No third member |
| `(3,134744,Dallet,65,11,140737671869202,0,0)` | No fourth member |
| `(4,117771,tonehinexccsjahh,65,5,140737671861409,0,0)` | No fifth member |

All list links/string offsets validate against the complete packets. Names/IDs/levels/classes/roster size explain variable lengths; loot defaults, canInvite and missing party names are additional real differences. This audit does **not** establish any of those as the popup trigger.

### 7.3. World receives both clears; no error response captured

At **22:13:44.344Z**, tap **6227** sends AS_DO_CREATE_PARTY 139E (376 B). At **22:13:44.366Z**, **6287** sends `0B000000CD150900000000`, and **6288** sends `0B000000CD150A00000000`: AS_CHANGE_EVENT_MATCHING_STATE `[u32 UserDbId=9/10][u8 false]`, 11 B each. Party refresh 13AD follows at **6289/6308**, and client2's dungeon-list request is forwarded as 1644 at **6329**.

World responds with party stats/buffs/quests and other normal 13F8/13F7 tunnels, beginning **6290/6291** at .367Z. Through capture end **22:14:12.197Z**, the only other W->A opcodes are **13F2 DSA_DUNGEON_TIMELINE_OPEN_INFO**, **13E5 BSA_REQUEST_BOUNTY_HUNT_SEASON_INFO**, and **13FA SA_DUMMY_PACKET** (6341), not matching errors. Opcode names: `Arb_part_003.c`. No matching error or dedicated 15CD acknowledgement is observed. `WorldServer.exe.c:2980852-2980897` consumes the 11-byte clear, resolves the user, sets the matching-state byte, optionally updates party state, and has no explicit network acknowledgement.

### 7.4. Data-only experiment prepared

The supplied EventMatching XML is semantically the intact local file with **64 Dungeon and 10 SoloDungeon event rows removed**; all retained elements/attributes/text compare equal. Its SHA-256 is `ab67be4479a992431d2e8c3c8c0952975d3ef775d20e7f7ba493800db0f3259b`. The earlier 1034-byte prediction used the intact reference file (`473ab92452711c5c9e0df77617c07b126172710cf896f2183a8581eaca49cb8a`), not this deployed-copy set.

Prepared **`D:\packetlogs\T184b-EventMatching.xml`**, SHA-256 `b303a6d66a2d9b4aa6a7c6fd2e6f1e28f07aecdcd2b803c6d102c11f06d763f5`: restore only the **64 Dungeon event rows**, in original group/order, from `D:\v100\TERA_SERVER.100\Executable\Datasheet\EventMatching.xml`. All existing deployed XML data is preserved; SoloDungeon rows remain absent. Original capture XML files were not modified.

Test by backing up the active EventMatching.xml, installing this candidate in the sheet location read by **both Arbiter and World**, restarting both, and repeating the same two-player 9781 queue. Expected: startup target-map count **41** instead of 8; each client's queued update **26 B**, IDs **2142/92139**, queued/type=1/1; completed reset pair **1034 B / 554 B** before unchanged FIN/SYS. Restore the backup to roll back. If those frames are present and the popup still fails, this experiment excludes the missing dungeon event mapping as sufficient explanation; do not declare World-only causality while the additional party differences remain.

Validation: complete client frames and per-link reframed tap inspected; pool/progress/FIN equal retail after state substitutions; both clients' party/BG-reset/FIN/SYS bytes equal; all XML candidate data except the 64 additions unchanged. **No server code changed, no deployment performed, no code merge needed for this experiment.** Audit scripts/output are ignored working material in `obj/t184b`.

## 8. T184c: queue4, corrected event sheet

Both clients now receive the complete **1034 B dungeon reset before FIN**, followed by the **554 B battlefield reset**. Every reset ID/order/link matches the repaired EventMatching set, and both recipients' reset/FIN/SYS packets are byte-identical. `arbiter-queue4.log:13/16` confirms 41 matching targets. The missing events were a real divergence, but restoring them did not restore the popup.

| Before/after FIN | queue4 client1 (player 10, last applicant) | queue4 client2 (player 9, first applicant) | Retail classic_live3 |
|---|---|---|---|
| C730 pool | 1179, 49 B, `(9781,type0,free0,2800,10,marker0,role1)` | 2045, 49 B, same with player 9 | 8662 byte-equal after dungeon/player substitution; 10333 is the later tank-role application |
| 87AC queued | 1180, 26 B, `queued=1,type=1`, IDs `[2142,92139]` | 2046, identical | 10335, 34 B, same flags, `[800029,2154,92151]` for dungeon 9739 |
| Progress before FIN | None; first request 1297 occurs after SYS and receives no 8BE4 | Requests 2051/2068/2075, replies 2052/2069/2076: `[9781,0,0,tank0,dealer1,healer0]` | 10339 -> 10346: tank1/dealer0/healer0; earlier DPS 8678 matches queue4 after dungeon substitution |
| Party -> resets -> FIN -> SYS | **1181 -> 1182 (1034 B) -> 1183 (554 B) -> 1184 -> 1185** | **2083 -> 2084 (1034 B) -> 2085 (554 B) -> 2086 -> 2087** | 10578 -> 10580 (730 B) -> 10581 (1290 B) -> 10585 -> 10587, with unrelated interleaving |
| First C_ after SYS | **1186 C_DUNGEON_CLEAR_COUNT_LIST**, 16 B; then 1187 C_VIEW_INTER_PARTY_MATCH_DUNGEON_LIST | **2182 C_SAVE_CLIENT_CHAT_OPTION_SETTING**, 967 B | **11351 C_CLIENT_LOG**, then **11521 C_ENTER_DUNGEON(9739)** |

Neither queue4 client sends C_ENTER_DUNGEON. Missing progress on the last applicant alone cannot explain both users: the first applicant received three valid replies and still reported no popup. Retail's final observed progress is also only one member, so these bytes do not justify inventing an unsolicited full-group progress push. The World clear pair is present at **tap record 12315, 22:45:21.272Z, A->W#26**: two complete 11 B 15CD frames for players 9/10, state=false. The tap also contains the older queue3 segment; 6287/6288 are not queue4's clear pair.

**One concrete client UI difference to isolate next: `isShowDungeonWorkUI`.** Full F732 frame byte **10** is **01** in every pre-FIN queue4 list (client1 1150/1157/1175; client2 1558/1565/2021/2028/2050/2057/2081), versus **00** in retail 7964/7992/8676/8827/9009/9996/10343. Header bytes 8..10 are `(userPool=1,partyPool=0,isShowDungeonWorkUI=1)` versus `(1,0,0)`. Thus the client's entire pre-FIN state is **not** established as retail-equivalent, even though the audited Arbiter matching frames now have the expected layouts.

Source chain: `WorldServer.exe.c:3525104-3525105` loads **DungeonWorkData.xml `showTab`** into sheet+0x60. `DungeonWorkManager::IsShowTab`, **3523887-3523903**, returns false when that flag is false (otherwise also tests manager runtime state). User-pool list generation **1024785-1024788** passes this result as writer parameter 6; **693375-693386** writes F732 and the three header bytes. The local sheet's line 2 has `showTab="true"`. Setting **`showTab="false"` in World's active DungeonWorkData.xml**, then restarting World, is a bounded experiment forcing the captured retail UI byte. Verify F732+10 becomes 00 before repeating the same queue; leave matching/FIN/SYS untouched.

### T184d — full login-to-FIN state audit

[T184d-CLIENT-STATE.md](T184d-CLIENT-STATE.md) inventories all 11,499 S_ frames / 188 opcodes and links the full field ledger. Additional candidates: login status 0 versus 31, selected-character isNewCharacter=false versus hardcoded true, F732 MatchTimeStatus 2 / three role rows versus 0 / empty, and saved GFx/UI state. `/@apm_ask` emits the empty dark-rift regional prompt 0x88F3; neither capture has its consent exchange. No unique popup gate is proved; incomplete definitions and exact experimental bytes are listed explicitly.

**This identifies a proven UI-state divergence, not a proven popup condition.** There is no client-code trace here showing that this flag gates the popup. Queue4 also still has a two-DPS SYS roster under the logged minimum-members override, versus retail's five-member roster, and the earlier party-field differences remain. A unique remaining root cause cannot honestly be named from these captures alone. No production code or sheets changed; complete-byte checks passed, working output is `obj/t184c`.

### T184f — captured retail two-player matches (2026-09-25)

`cap_2man` proves a two-DPS popup with dungeon9781/role1/capacity2. [Evidence and field comparison](../data/t184f/README.md) includes source hashes, complete formation windows for both members, raw frames and decoded fields. References below are original client records; tap references use the starting TCP record and byte offset. This supersedes the earlier claim that the audited party stream was equivalent to retail.

| Step | client1, matches 1 / 2 / 3 | client2, matches 1 / 2 / 3 |
|---|---|---|
| Pool C730 / queued87AC | 1231/1232; 4246/4247; 10439/10440 | 820/821; 4287/4288; 8068/8069 |
| Final roster / names | 1281/1282; 4336/4337; 10520/10521 | 910/911; 4352/4353; 8157/8158 |
| Dungeon/BG reset | 1283/1284; 4338/4339; 10522/10523 | 912/913; 4354/4355; 8159/8160 |
| FIN / SYS | 1285/1286; 4340/4341; 10524/10525 | 914/915; 4356/4357; 8161/8162 |
| Enter-now | 1479; 4502; 10664 | 1078; no second-match C_ENTER; 8313 |

Proven corrections: World queued=true notification; matched roster capacity, loot configuration and invitation flag; incremental rosters and six localized party names; TimeLine events2176/92176 in the full **1066B** dungeon reset (BG remains554B); balanced matching-state clears and one withdrawal teardown. Pool remaining-time0, one-DPS progress, FIN(9781,0,0), and 30-slot SYS layout already match. Full field/state classifications are in `data/t184f/comparison.tsv`; IDs, names and party allocation are not replacement constants.

All three FINs follow fresh applications by both members. Client2 relogs after FIN4356 (lobby4506, select4745, topo-fin5010) with **no repeated FIN/SYS488**, then enters via NPC/dialog at5556. Automatic relog completion replay was removed. No distinct enter-later/decline action or expired-offer exchange is captured; existing timeout/disconnect policy remains unverified.

Reset: client1 C_RESET9822 → tap7888 AS13B9 → requester vote9823 / other-member vote7654 → client2 acceptance7668 → completion9846/7670 → port-out9955/7783. Arbiter sends the owner/count request to every World; World owns votes and port-out. The human registration seam is `T184f-PARTY-PATCH.diff`: remove the old C_RESET stub before enabling the new PartyWiring registration, otherwise registration throws for the duplicate opcode.

Withdrawal: client1 C_LEAVE11051 → resets11052/11053 → leave11054 → wildcard cancel11055; client2 receives8664–8668. Client1 abnormality11071 proves999994 for900000ms. All 25 tapped registrations are WorldId0, so the DungeonServer-side13F5 is **not captured**; its current-World routing is derived from A082:15914–15925. AM/MA port7803 is also absent; [statistics derivation](T184f-QUEUE.md) distinguishes native source evidence from resulting AS1644/F732 capture pins.

AS_CREATE139E now carries the native completed-pool compensation flag and full semantic member metadata: authoritative13CC item level frozen at application, stored dungeon clear count, and solo-application flag. The 376B tap1921 fixture excludes only native padding and party allocation state. AS1644 now supplies the full role/statistics descriptors from live sheets and native-derived rolling history; the captured 2007B descriptor and9781 status1/status3 samples are pinned. Neither status nor item level is copied as a production constant.

Vanguard: client1 C_COMPLETE_DAILY_EVENT10212(event2142) → tap8450 World tunnel → quest/reputation/item/EP/repeat-quest writes8451–8545. Existing handlers cover the normal claim; new exact-reply and persistence tests pin money12,500,000, items45474×1/1300×30 and the supplied reputation/EP records. World owns reward validation and client notifications. SA_DAILY_EVENT_COMPLETE1593 at8455 additionally notifies native attendance/promotion campaigns; those auxiliary campaign effects remain unimplemented and no response from an active campaign is captured.

Validation: **19 T184f tests passed**. An isolated source copy (`obj/t184f-validation`) with the exact human registry patch applied builds successfully and runs **1008 passed / 0 failed / 26 skipped**; skips name absent older capture fixtures. The worktree's human-owned HandlerRegistry remains unchanged, so apply the supplied patch before building/running or merging. All34 full-frame hex literals checked in the new tests exist verbatim in `cap_2man` or its two client captures; member-padding/state normalization is explicitly marked. Live popup verification on TeraSharp remains the deployment check.

### T190 — pending entry and both World links

[New-run frame evidence](../data/t190/README.md) excludes the exact earlier `cap_2man` prefix appended inside `cap_2man_b`. Links26/54 register asWorld0/13 at10299/11532. Client1 FIN1470→lobby1719→select1899→topo-fin2163 has no repeated completion. NPC/dialog2554–2594 later leads to9781 at2787: tap12571+0→14670+1357 measures155.902seconds. Existing pending behavior matches; `T190Matching.cs` pins the requests, completion bytes and lifecycle. Neither >300-second expiry nor socket disconnection is exercised; Decline cannot be assigned reliably to a popup and no separate decline packet is present.

**Routing correction to T184f:** `FUN_14056dbe0` is `GetDataSessionByServerId(int)` (Arb046:2704–2729). Matching admission, formation and leave pass literal0 (Arb076:15129,8270; Arb079:3261; Arb077:6151). Thus15CD goes toWorld0, including the leave clears17291/17292 and19036/19037 while users are insideWorld13. Party removal13A0 and dismissal13A1 broadcast to registered Worlds (Arb067:8245–8260,11180–11195; Arb066:18093–18126), matching17293–17296 and19038/19039→19048/19049. Dropout13F5 follows dismissal and targets the departing user's currentWorld13:19061, with client2 abnormality4672 proving999994/900000ms. Clear13F0 at16357 suppresses the earlier departure's penalty.

No286B ranked-record write or8B06 `S_DUNGEON_RANK_END_POINT` occurs in this run. Native rank-result emission (Arb064:11549–11647) remains decompile-only and the existing omitted client reply is not claimed implemented or capture-pinned.

Validation: solution build succeeded;1014 tests passed,0 failed,26 skipped for absent older fixtures. All6 T190 tests passed. No human-owned code seam remains; deployment/live verification is still performed by the human.

Creation139E also broadcasts to all registered Worlds (Arb079:15465–15491), matching identical frames12571/12572 and18232/18234 onWorld0/13. T190 corrects creation together with removal/dismissal; unrelated mirror opcodes were not broadened. Card writes and the GM collection command are documented in [PERSISTENCE-MAP.md](PERSISTENCE-MAP.md)'s T190 section.

### T190b — restoring a matched party after lobby return

Two captured relog inputs were missing: **AS_ENTER_WORLD's PartyId / IsSysParty** and **the post-entry13AD party refresh**. T190 proved that the local pending entry survives; it did not verify those World inputs. Client1's lobby1719 → select1899 → topo-fin2163 → NPC2554 interval contains **no F732 or C_VIEW_INTER_PARTY_MATCH_DUNGEON_LIST**. The last earlier F732 is1257; the next is4464. [Selected frames and complete matching-window index](../data/t190b/README.md) distinguish those controls from the relog window.

| Field / message | Retail cap_2man_b | TeraSharp before T190b | Correction / finding |
|---|---|---|---|
| AS_ENTER_WORLD PartyId, payload94 / full-frame100 | tap13799+0: `0x0AF0003000000001` | Hardcoded0 | Read the character's retained `PartyWiring.Manager` party. |
| AS_ENTER_WORLD IsSysParty, payload102 / full-frame108 | Same frame:1; combined nine bytes `010000003000F00A01` | Hardcoded0 | Send that party's `IsSys`; an unpartied character keeps0/false. |
| S_SYS_PARTY_INFO | Client1 record2012, **40B**, from World tunnel13914+275 | Emitted by World | Distinct from Arbiter's initial488B SYS; do not replay the latter. |
| Party roster | Client1 record2176 after topo-fin | Party membership is retained locally | World entry must also carry that membership. |
| AS_REQUEST_REFRESH_PARTY_INFO13AD | SA_ENTER_WORLD13913+15 → tap14041+0: `0A000000AD13EB030000` | Entry completion only released the leave gate | Request the retained member's data from their current World after entry completes. |
| F732 MatchTimeStatus / roles | No list in the relog window | Existing AS1644 supplies global statistics | No pending→2 override: World694867–694929 copies these from AS1644's statistics map. |
| FIN / pool / queued-event replay | No repeated completion during relog | Existing `TakeReoffer` sends none | Preserve this behavior. |

Native Arbiter source: `Arb_part_028.c:15773–15781` resolves the party and reads its ID / system-party flag;15847–15850 passes both to the AS_ENTER_WORLD writer. F732 row+20 (Convoke) is written0; row+35 is the statistics category, not a matched flag. List header userPool/partyPool is chosen from World's party/leader state (`WorldServer.exe.c:565483–565499`). These sources do not prove a particular client popup appearance; the client executable is not among the supplied decompiles.

World uses the received PartyId when recovering a party-owned channel with instance−1 (2985580–2985586), and passes PartyId / IsSys into `ItemEquipChanger` (2985691–2985694). These are proven uses; direct PartyView binding or popup restoration from those fields is not established.

World's `PartyView::SendSysPartyInfo` (`WorldServer.exe.c:1995438–1995553`) emits two occupied16B linked slots in SYS2012, with triples `(1,1,1)` and `(1003,1003,1)`; the writer repeats UserDbId in both identity fields. This40B variant contains **no dungeon ID, pending timer or enter-availability flag**. It proves a system-party view, not a new offer. The F732 controls1257/4464 have9781 statuses3/1 respectively, with every other9781 field equal; neither occurs during this relog interval.

| F7329781 control field | Record1257 / 4464 | Current producer / verdict |
|---|---|---|
| userPool / partyPool / showUI, full8/9/10 | `1/0/0` in both | World selects the pool from party/leader state; showUI is DungeonWorkManager state. No per-user pending override in Arbiter. |
| Convoke, row+20 | `0 / 0` | World694923 writes literal0; this is not a matched flag. |
| FailReason, row+21 | `−1 / −1` | World1024642–1024661 checks action points, item level, timeline and cooldown; party checks also examine member failures at2969639–2969649. No pending flag. |
| NextOpen / Newbie / IsEvent / RequiredActPoint | `−1/0/1/110` in both | World timeline/event data and DungeonController getters (694761–694865,2346872–2346916,2343943–2343955). |
| MatchTimeStatus, row+35 | `3 / 1` | `LeaderboardPackets.WorldListRequest` sends `MatchBrowseStatistics.Snapshot()` in AS1644. Global wait history, already implemented in T184f; queue4's empty-descriptor0 is historical. |
| Role mask/status pairs | `(1,1),(2,1),(4,1)` in both | Same AS1644 statistics provider; all schedule/text lists are empty. |

The pre-lobby AS1644 is tap12297+0; the later one is17997+44. Native MA statistics enter through Arb069:10660–10680 → Arb070:14574–14627 and are serialized atArb039:5230–5344. There is no TeraSharp relog capture to compare; the current-code comparison identifies producers and the proven missing inputs, not invented client output.

The AS_ENTER_WORLD change is supplied as **`status/T190b-PATCH.diff`** because `Handlers/WorldEntry.cs` is human-owned. Apply it before the new regression test or merge. `DbProxyHandlers.OnSaEnterWorld` also invokes a narrow `PartyWiring` refresh for an online party member after successful entry, preserving the held-leave branch. Native call chain: Arb029:12364 → Arb079:14553–14570 → Arb067:9181/13616–13639; refresh13AD resolves the member's current World and carries only UserDbId. No matching-list statistics constant or synthetic relog offer is added.

Validation: the actual entry builder fails the new regression before the patch (nine zero bytes instead of the existing party fields) and passes after it. An isolated copy with the exact patch builds the solution and runs **1016 passed / 0 failed / 26 skipped**; skips require absent older fixtures. `T190b_enter_world_restores_the_existing_party_without_reforming_it` covers no party, normal party and retained system party; only process-issued PartyId is normalized. `T190b_captured_world_entry_refreshes_retained_party_on_current_world` runs the complete captured15350B SA138C through the production handler/WorldLink and compares the exact10B13AD, including current-World routing and absent party/session cases. Worktree human-owned files remain unchanged. Live verification must capture a matched-party lobby return and any subsequent list/button action; no popup outcome is claimed from these tests.

### T184g — queue5 against the retail two-player formation

**The streams are unequal.** Both captures have client2 as the first applicant and client1 as the second. [Complete byte comparisons and selected source frames](../data/t184g/README.md) cover every received packet in the audited windows; no identity/timestamp bytes are silently masked. Native-equivalent core packets do not make the entire stream equivalent.

| Applicant position | Retail request → SYS | Queue5 request → SYS | First received-frame difference |
|---|---|---|---|
| First, client2 | 818 →915 | 1006 →1147 | Retail819 is18B `S_SYSTEM_MESSAGE @2173`; queue51007 is49B C730. First literal byte: `12` versus `31` at full0; message is absent in queue5. |
| Second, client1 | 1229 →1286 | 1408 →1418 | Retail1230 is the same18B message; queue51409 is49B C730. Same first-byte mismatch. |

Retail notification bytes: `12000EF30600400032003100370033000000`. Native Arb076:14928–14944 identifies a dungeon matching application and15123–15136 emits2173; current `MatchWiring.OnMatchAdd` sends pool/queued frames without that notification. This proves an omitted packet, not that its absence blocks a popup.

| Subsequent difference / equality | Capture proof / interpretation |
|---|---|
| Advertised party capacity **2 versus5** | Client roster full10: retailC1:1281/C2:910=`02`, queue5C1:1413/C2:1142=`05`. AS139E full34: retailtap1921+0=`02000000`, queue5tap2558+0=`05000000`. This is not an identity substitution. |
| Clear-count list uses a different dungeon set | First applicant retail824:337B/25rows including9781(clear1); queue51011:194B/14rows,9781 absent. `ArbiterClientHandlers.DungeonClearCountRoster` still hardcodes the older14 IDs; `OnDungeonClearCountList` iterates that array. Clear-count history may differ by character, but omission of the dungeon is a separate content-set difference. |
| Leader and seat order reverse | Retail leader/slot0 is second applicant1003; queue5 leader/slot0 is first applicant9. RetailC1 gets singleton1279/names1280 before final1281/1282; queue5C2 gets singleton1140/names1141 before final1142/1143. Incremental generation exists in both runs; different recipients follow the chosen seat order. |
| Core matching bytes | Pool differs only by member identity; queued26B, one-DPS progress36B, six-name116B packets, resets1066B/554B and FIN16B match. SYS488B differs by member IDs and seat order. Both occupied roles remainDPS1, with28 identical empty slots. |
| Browser state | First applicant's F7321412B lists are byte-exact. Second-applicant aligned lists have9781 global MatchTimeStatus3/2 and four other dungeons' failReason differences; packet positions differ around formation, so these are recorded as state differences, not a pending-offer flag. |

`arbiter-queue5.log:25` explicitly enables `TERASHARP_MATCH_MIN_MEMBERS=2`. `MatchQueueManager.TryFormDungeonTemplate` uses that override to form a two-member group, while `PartyManager.FormMatchedParty` obtains advertised capacity from `DungeonMatchRules.Total` (or the normal five-member default if no row exists). Thus the override does **not** reproduce retail's two-member dungeon configuration. The existing candidate in `D:\packetlogs\real-9781-two-player\candidate` has9781→role1, Role1.totalUser2 with role minima0 and DungeonData9781.maxMemberCount2; these are the relevant settings to reproduce in each server's actual data source. The deployed queue5 XML files were not supplied, so their exact paths/hashes are not asserted from the current local copies.

Both taps register control link1 asWorld0. AS139E is376B in each: the first literal changed byte is full18 (`2F`→`01`, party allocation state); capacity at34 is the first identified configuration scalar change. List offset56/length320, partyType0, compensation1, dungeon9781, raid0, team0 and battlefield0 match. Per-applicant IDs/names, race, item level, previous clear counts and native padding differ; their complete values and every raw changed range are retained in `world-field-diff.json`.

The four15CD false clears are correct: retail1924+0,1928+0/+11/+22 clears1003/1 twice; queue52619+0/+11/+22/+33 clears9/10 twice. Both13AD refreshes precede them. Clear dispatch is create+0–1ms retail versus+28ms queue5. World emits no matching error or dedicated15CD acknowledgement. Through the formation+2s boundary, retail W→A counts are13F7×320,13F8×17,13F2×2,27FA×2,2802×2,13BE×1; queue5 is13F7×185,13F8×16,13F2×2. All337/201 embedded client frames have exact-byte matches in the audited client windows. Both runs send party markers, HP, party info, stats, buffs and quests, but actual buff/HP/quest state and update counts differ. Retail additionally emits dungeon-entry request13BE at2147+0 after the client chooses Enter; queue5 has none in this window. Every internal frame is preserved in `world-exchange.tsv`.

| First three client inputs after SYS | Retail | Queue5 |
|---|---|---|
| First applicant, client2 | 1078 C_ENTER_DUNGEON;1109 C_SAVE_CLIENT_CHAT_OPTION_SETTING;1110 C_PLAYER_LOCATION | 1312 C_SAVE_CLIENT_CHAT_OPTION_SETTING;1314 C_PLAYER_LOCATION;1315 C_PLAYER_LOCATION |
| Second applicant, client1 | 1406 C_CLIENT_LOG;1479 C_ENTER_DUNGEON;1484 C_SAVE_CLIENT_CHAT_OPTION_SETTING | 1537 C_MATCH_PROGRESS;1538 C_DUNGEON_COOL_TIME_LIST;1549 C_MATCH_PROGRESS |

Client logs have no timestamps. The reproducible post-formation comparison uses AS139E tap time+2s (retail23:36:41.981Z; queue502:36:37.547Z), with boundary frames retained separately. It cannot claim an exact two seconds measured from client receipt of SYS. This limitation does not affect any of the pre-SYS discrepancies above. A Classic+ laptop run remains a possible control, but these captures do not establish that the fault is outside Arbiter. No production code or deployment changed in T184g.

Validation: `python tools/t184g-evidence.py` regenerated the artifacts successfully from all six original captures; frame lengths and reassembly tails passed. Per-member tables assert complete, nonduplicated coverage of477 retail and340 queue5 received frames, with all changed byte ranges preserved. The internal-frame export contains571 frames. No build or gameplay test was needed for this read-only capture comparison.

### T184h — application notification, sheet capacity and clear-count ownership

| Change | Proof / resulting behavior |
|---|---|
| Dungeon application notification | `MatchWiring.OnMatchAdd` sends `12000EF30600400032003100370033000000` to every admitted local member before C730. Pins: cap_2man client2:819/client1:1230; native Arb076:14928–14944/15123–15136. Battleground-only and refused applications do not receive this dungeon notification. |
| Retired partial-group override | `TERASHARP_MATCH_MIN_MEMBERS` no longer changes dungeon completion, roles or battleground composition. Dungeon completion follows `DungeonMatchRules.Total`; the existing party builder advertises that same sheet capacity in roster full10 and139E full34. A value2 cannot form a two-of-five group. |
| SYS stays native | SYS remains488B with30 physical slots. A two-member sheet rule fills two slots and leaves28 empty, byte-exact to cap_2man client1:1286/client2:915; there is no separate party-capacity field to change. |
| Clear-count request ownership | The connected handler forwards the original C5C98 to World. cap_2man client2:822 → tap1464+0 AS13F6 →1466+0 World13F7 → client824 proves ownership and the337B/25-row response. The old Arbiter interception substituted a different14-row list. |
| Standalone clear-count sheets | `DungeonClearCountSheet` reads DungeonMatching, corresponding DungeonData files and DungeonNewbieBonus. World576445–576501/1024060–1024231 select the named character, level bounds and completed-quest prerequisites, then order by descending minimum item level. The captured25-row eligible set/order includes9781. |

Newbie status also follows the sheet's `DungeonNewbieBonusCheck.clearCount`, not `clears==0`: World2346910–2346912 and3239977–3239980; cap_2man has9781 clear1 with newbie1 under threshold10. Native DungeonData `completeQuest` conditions are parsed at2354540–2354548. The <=32-row sort preserves ID-map order for equal item levels (727046–727081); the captured roster contains25 rows. Live eligibility/order remain authoritative in World; the historical14 IDs remain only the explicit T159 absent-sheet fallback.

Two-player testing now requires the actual two-player dungeon configuration: DungeonMatching9781 references the matching role whose `totalUser=2` and role bounds admit the two applicants; World's DungeonData9781 must also have `maxMemberCount=2`. The old environment override is not a substitute. Existing lifecycle tests now load explicit small sheet fixtures.

The human-owned startup warning update is `status/T184h-PATCH.diff` for `src/TeraSharp.Arbiter/Program.cs`: report the old variable as retired/ignored, instead of claiming it still bypasses formation rules. Production Program.cs remains unchanged in the worktree.

Validation: an isolated copy with that exact startup patch builds successfully and runs **1020 passed / 0 failed / 26 skipped**. The tests exercise both applicants and party-member notification, rejected/battleground applications, sheet-five refusal despite an environment value2, sheet-two formation, native roster/139E capacity and complete SYS bytes. Clear-count tests exercise current-World routing (World0 and13), exact captured request/response bytes, the sheet-derived25-row response, level/quest eligibility and newbie thresholds. Only the AS13F6 monotonic tick at full22–29 is normalized; all four stored capture frames were independently checked against the source records. [Fixture provenance and reproduction](../data/t184h/README.md). These checks do not establish a live popup outcome.
