# cap_t38.bin

TSIS container (`"TSIS"`, u32 record count, then per record `u32 seq | u16 opcode |
u32 payloadLength | payload`) with the tunnel frames T38's byte-exact tests need. Payloads only —
the 6-byte `[u32 length][u16 opcode]` frame header is stripped, so the frame-relative offsets
inside (`22`, `22+16N`, `30`) are 6 more than the payload index they point at.

| seq | opcode | capture | what |
|-----|--------|---------|------|
| 387  | `0x13F7` | `cap_newchar.log` | SA_BYPASS_TO_CLIENT, ticket 0, sequence 0, 423-B client packet |
| 388  | `0x13F7` | `cap_newchar.log` | the next one: same ticket, **sequence 1** — the `>> 19` proof |
| 926  | `0x13F7` | `arb_world_2026-09-13T11-33-30-680Z.log` | **ticket 2** — the field really is read, not assumed |
| 927  | `0x13F7` | same | ticket 2, sequence 1 |
| 549  | `0x13F6` | `cap_newchar.log` | AS_BYPASS_FROM_CLIENT, worldClient `0x80000AF00001` |
| 1050 | `0x13F6` | same relog capture | worldClient `0x80000AF00002` — the second login's own gameId |

Every frame in the file has exactly **one** recipient, because no capture has two players in it.
The two-recipient tests in the T38 block are synthetic and labelled as such.

Outside the build, the parser and builder in `World/TunnelFrames.cs` were run over *every* tunnel
frame in both captures (reframed by the u32 length, since the tap logs coalesced reads):
**4169 × 0x13F7 and 606 × 0x13F6 — all parsed, all rebuilt byte for byte, zero exceptions.**
Invariants that held across all of them: `userListOffset == 22`, `userListBytes == 16`,
`packetOffset - 6 == 16 + userListBytes`, `planetId == 2800`, the low 19 bits of the sequence word
always zero, and `24 + packetLength == payload length` for 0x13F6.

Layouts and the call-site swap: `status/MULTIPLAYER-DESIGN.md` §6.
