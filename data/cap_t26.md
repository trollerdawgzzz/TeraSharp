# cap_t26.bin

TSIS container (`"TSIS"`, u32 record count, then per record `u32 seq | u16 opcode |
u32 payloadLength | payload`) holding the frames T26's byte-exact tests need, so the tests do not
need `D:\packetlogs`. Payloads only — the 6-byte `[u32 length][u16 opcode]` frame header is
stripped.

## From `D:\packetlogs\cap_newchar.log` — "Test", playerId 2, first login of a new character

| seq | opcode | what |
|-----|--------|------|
| 336  | `0x2890` | reputation list, empty (13 B) |
| 413  | `0x2891` | the ONLY reputation write in any capture: id 610, UpdateType 1, owner 2 |
| 376  | `0x2909` | fatigability, 2520 points |
| 511  | `0x2910` | fatigue write, delta 135 |
| 2068 | `0x2910` | fatigue write, delta 0 |

## From `D:\packetlogs\arb_world_2026-09-13T11-33-30-680Z.log` — dob (1) and "Test" (2) relogging

| seq | opcode | what |
|-----|--------|------|
| 389 | `0x2890` | dob's reputation list, one record, owner stamped 1 |
| 875 | `0x2890` | Test's reputation list — the same record, owner stamped 2 |
| 430 | `0x2909` | fatigability at dob's login, 2655 points |
| 916 | `0x2909` | fatigability at Test's login, 2940 points |
| 549 | `0x2910` | fatigue write, delta 270 |
| 748 | `0x2910` | fatigue write, delta 15 |

Layouts and the evidence: `status/REPUTATION-FATIGABILITY.md`.
