# T190 capture evidence

Sources: `cap_2man_b.log`, both client logs and its Arbiter console. The tap contains the **exact 15,604,093-byte `cap_2man.log` prefix**; the export excludes that earlier run. `source-manifest.json` records hashes, counts and the boundary. Tap references are original starting TCP record plus byte offset, never reframe ordinal. `streams.json` contains selected complete frames; `frame-ledger.tsv` indexes them. Authentication packets are excluded.

Regenerate with `python tools/t190-evidence.py` from the repository root. Private complete frames and condensed listings go to ignored `obj/t190`; only the selected evidence goes here. Client logs have record order but no timestamps; matching-time measurements below use the tap.

| Path | New-run evidence | Result |
|---|---|---|
| First pop | client1 1162→1470/1471; client2 994→1110/1111; tap12571+0 | One completion per member. |
| Lobby while pending | client1 1719→1899→2012→2163 | No repeated FIN or initial488-byte SYS. |
| Delayed entry | client1 NPC/dialog2554–2594→SYS2728→topo2787; tap14670+1357 | 155.902seconds after formation; no C_ENTER on this path. |
| World identities | tap10299 registers link26 asWorld0;11532 registers link54 asWorld13 | Routes can now be distinguished. |
| Party creation | tap12571/12572;18232/18234 | Identical139E creation goes to bothWorld0 and13. |
| Clear then leave | clear13F0 at16357; leave17290; remove/dismiss17293–17296 | No dropout packet follows the cleared party's departure. |
| Uncleared departure | client2 4652; tap19038/19039 remove,19048/19049 dismiss,19061 penalty | Removal/dismissal broadcast toWorld0/13;13F5 goes onlyWorld13. Client2 abnormality4672 is999994 for900000ms. |
| Ranked result | No286B in tap; noS_DUNGEON_RANK_END_POINT/8B06 in eitherclient | Ordinary clear is captured; ranked-result behavior remains unpinned. |

Decline's account/popup is not remembered reliably. No distinct decline/C_MATCH_DEL packet occurs, so no button behavior is inferred from silence. The155.902-second wait does not verify expiry after300seconds or socket-disconnect policy. Card references and proven fixes are recorded in `status/PERSISTENCE-MAP.md`'s T190 section.

Validation: solution build succeeds; **1014 passed,0 failed,26 skipped** (absent older capture fixtures), including all6 new T190 tests.25 card fixture frames match their exact new-run source record/offset/opcode;14 complete C# frame literals also exist in the new run. Compact card-sheet attributes match the live sources. No human-owned code patch is required.
