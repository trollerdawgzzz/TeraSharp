# T192 — complete-frame handoff evidence

Regenerate from the original logs with `python tools/t192-evidence.py`. It reuses the checked T190 TCP reassembler, with a separate buffer per direction/link. References are **original TCP record + byte offset**, not reframed ordinals. `source-manifest.json` pins all inputs. Full private caches remain in ignored `obj/t192`; exports contain handoff/transition evidence only.

## Finding

The premise “no 13C0” is contradicted by the tap. Every attempt after World 13 linked receives **13C0**, and TeraSharp sends **13C1**. The incorrect field is the requesting character's **PDId**, full-frame bytes **6–13**: the builder sends `(planet 2800, user 1)` for users 10 and 9. The first wrong byte against the resolved requester is **full+10 = 01**, expected **0A** for user 10 or **09** for user 9. Retail's captured user really was 1, which concealed the hardcoded value in old fixture-only tests.

Both implementations preserve every request byte from **full+14 through full+214** verbatim in 13BF. `13bf-comparison.json` lists all literal differences and verifies this invariant for all six pairs. Thus the differing party identity, position, source-world state and opaque bytes in that tail are inherited from the source World; they are not serialization changes introduced by TeraSharp. A literal retail/live reply comparison first differs at **full+38**, the embedded party user 1 versus 10; that state difference is separate from the incorrectly unchanged target user at full+10.

| Capture / attempt | 13BE source | 13BF owner | 13C5 | 13C0 owner | 13C1 source |
|---|---:|---:|---:|---:|---:|
| multiworld3 first | 7827 #11 | 7828 #14 | 7829 #14 | 7830 #14 | 7831 #11 |
| multiworld3 second | 8550 #11 | 8552 #14 | 8553+0 #14 | 8553+38 #14 | 8554 #11 |
| multiworld3 third | 14530 #11 | 14531 #16 | 14532 #16 | 14533 #16 | 14534 #11 |
| handoff1 user 10, conditional teleport | 24732 #51 | 24733 #76 | 24734 #76 | 24735 #76 | 24736 #51 |
| handoff1 user 9, enter button | 25358 #51 | 25359 #76 | 25360 #76 | 25361 #76 | 25362 #51 |
| handoff1 user 10, enter button | 25381 #51 | 25382 #76 | none | 25383 #76 | 25385 #51 |

All references without `+offset` mean `+0`. These are **tap connection numbers**, not World IDs. Handoff1 #51 is World 0; #76 is World 13. The Arbiter log calls the latter its own connection #26. Do not confuse that with tap #26 from an earlier run in the appended file.

The source handle for user 10 is `0200f00a00800000`; user 9 is `0300f00a00800000`. `arbiter-handoff1.log:376,441` and `:769,835` associate those login GameIds with users 10 and 9. The nested party PDId at **full+34/38** remains 2800/10 even in user 9's request; it cannot substitute for resolving the actual requester.

## No missing owner acknowledgement

`handoff-windows.tsv` enumerates every complete frame in the first retail/live windows. Retail 7828 → 7830 contains **no A→W frame** between the 13BF and 13C0, only the owner's 13C5. There is no early AS_ENTER_WORLD, user blob, 13C3, or acknowledgement to 13C5.

| Retail continuation | References |
|---|---|
| Source transition / leave | 7882:1445, 7884:1392, 7906:1393, 7907:1433 on #11 |
| Owner enter | 7908:138E on #14; 7909:1626 then 7910:2711 from #14 |
| Owner topo finished | 8030:138F on #14 |
| Later departure from dungeon | 8164:13C2 → 8165:13C3 on #14, then 8193:138E on #11 |

The 1392 and 1393 frames carry **leave type 2**. The retail client sends no new C_SELECT_USER during this transfer (its only one in the whole client capture is initial-login record 33). The owner AS_ENTER_WORLD is therefore an Arbiter continuation of the source-world transfer/leave exchange, not another character selection.

`enter-world-transition.json` compares the complete initial source AS_ENTER_WORLD **6486** with owner AS_ENTER_WORLD **7908**, using the source transition **1445:7882**. Offsets below exclude the six-byte internal header:

| AS_ENTER_WORLD payload field | Source → owner |
|---|---|
| ArbiterClient +16, ArbiterUser +24, UserDbId +32, AccountDbId +36, SessionKey +44, GameId +84 | Retained byte-for-byte |
| Continent +48 / channel +52 | 7005 / 0 → 9781 / 183500813 |
| Position +56 | Copy the source 1445's full+42 float3 |
| EnterWorldType +68 | **1 → 2** |
| Direction +72 | `FFFF9BAE` → `FFFFE71C`; 1445 carries signed 16-bit `E71C` at full+54 |
| Ticket +80 | **0 → 1** |
| EtcData +167, 16 bytes | Copy 1445's raw data, full-frame offset 56 / length 16 |

1445 full+14 is the same ArbiterUser pointer previously supplied at AS_ENTER_WORLD payload+24; full+22 is the same GameId supplied at payload+84. SA_LEAVE_WORLD 1393 similarly starts with that pointer at payload+0 and GameId at payload+8. Those two identities are distinct in retail; TeraSharp uses its GameId as the ArbiterUser surrogate.

The 1433 reply at 7907 contains **GameId twice**, not ArbiterUser followed by GameId. Source world-blob save **7903** still contains the source position/zone. Destination blob reply **7911** contains the target position at blob+220, continent 9781 at +236, channel 183500813 at +240, World number 13 at +244 and direction `FFFFE71C` at +304. Both exact 15,312-byte blobs and every difference are exported. The complete blobs differ by 47 bytes; additional opaque changes are evidence, not guessed constants to stamp.

The 13C2/13C3 pair occurs **28 seconds after** the initial request and belongs to the later dungeon departure, not a prerequisite for first entry. The next such pair is 8320 → 8321.

`owner-after-entry.json` covers World 13 through capture end: 13C5/13C0 are answered/consumed; other owner input is only 13F2/13E5 heartbeats and 164D status. No owner SDB request is left unanswered. The visible `no replay for 164D` at Arbiter log line 1073 is periodic status, also present at retail 7861. 13C0 is consumed by the handoff path before the normal incoming-frame log, which explains its apparent absence in the console.

## Client and two-player control

`cap_handoff1_client:29` selects user 10. Its sole `C_ENTER_DUNGEON`, **2799**, is `08006c9a35260000`; the matching user-10 tunnel is **tap 25380**, followed by the third handoff attempt above. There is **no S_LOAD_TOPO after 2799 through EOF 2966**. All three earlier S_LOAD_TOPO records (77,1881,2075) load 7005. The first handoff attempt 24732 instead follows `SDB_CONDITIONAL_TELEPORT`; Arbiter log 1065 identifies user 10.

The real `cap_2man` tap has source-world requests (first popup entries **2147/2162**) but **does not capture the owner exchange**: no 13BF, 13C0 or 13C5 exists anywhere in that tap. Do not claim an exact owner-reply pin from it. Both real clients later receive S_LOAD_TOPO for 9781 (client1:2113, client2:2422), retained in `client-transition-frames.json` alongside every captured entry/topo-finish transition.

## Files

- `control-frames.json`: complete selected internal frames, reusable as test fixtures.
- `control-ledger.tsv`, `handoff-windows.tsv`: references, directions, timestamps, opcodes and sizes.
- `13bf-comparison.json`: byte ranges for retail/live comparison and each source-to-reply invariant.
- `enter-world-transition.json`: full source/target enter frames, transfer notification and offset-by-offset field comparison.
- `source-world-blob.bin`, `destination-world-blob.bin`: exact blobs from 7903 and 7911 for targeted transfer-state pins.
- `client-transition-frames.json`: complete entry/topo packets from the four client logs.
- `owner-after-entry.json`: complete non-heartbeat owner traffic plus opcode counts through EOF.
