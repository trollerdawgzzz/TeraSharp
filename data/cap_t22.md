# cap_t22_newchar.bin / cap_t22_relog.bin

TSIS containers (the format T13 introduced: `"TSIS"`, u32 record count, then per record
`u32 seq | u16 opcode | u32 payloadLength | payload`) holding just the frames T22's byte-exact
tests need, so the tests do not need `D:\packetlogs`.

Payloads only — the 6-byte `[u32 length][u16 opcode]` frame header is stripped.

## cap_t22_newchar.bin — `D:\packetlogs\cap_newchar.log`
"Test", playerId 2, the first login of a brand-new character.

| seq | opcode | what |
|-----|--------|------|
| 176  | `0x2873` | tutorial tips, empty |
| 344  | `0x27F9` | achievements, brand-new character (1493 B payload) |
| 346  | `0x2868` | dungeon cool time, empty |
| 380  | `0x2943` | seren guide, empty |
| 719, 765, 864, 911 | `0x286E` | the four tip writes: 1, 2, 35, 39 |
| 634, 2713 | `0x2944` | seren writes: (type 2, id 1804) and (type 4, id 36) |
| 1369, 2605, 2722 | `0x2802` | accomplish 5991, 5991 again, 5992 |
| 1367, 4173 | `0x27FA` | the first and last achievement saves |

## cap_t22_relog.bin — `D:\packetlogs\arb_world_2026-09-13T11-33-30-680Z.log`
dob (playerId 1, seq 355-828) and "Test" (playerId 2, seq 835-2036), both with progress.

| seq | opcode | what |
|-----|--------|------|
| 373, 397, 399, 434 | `0x2873` `0x27F9` `0x2868` `0x2943` | dob's login loads |
| 811 | `0x27FA` | dob's logout save |
| 859, 883, 885, 920 | `0x2873` `0x27F9` `0x2868` `0x2943` | Test's login loads |
| 1137 | `0x27FA` | Test's save at the zone change |

Layouts and the evidence: `status/ACHIEVEMENTS.md`.
