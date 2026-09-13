cap_item_single.bin — the four SDB_ITEM_SINGLE / DBS_ITEM_SINGLE frames from
D:\packetlogs\cap_newchar.log (real ArbiterServer, character "Test" playerId 2), reframed by
u32 length and stored as payloads (frame length - 6). Extracted for the T13 byte-exact tests so
they do not depend on D:\packetlogs.

  seq 2072  0x2768  880 B   one atom, op 7 (insert): gathering pickup, item template 81251 x4
  seq 2073  0x2769  877 B   the Arbiter's reply — the same atom with the allocated id 15 at +16
  seq 2211  0x2768  4304 B  five atoms, ops 6, 11, 6, 11, 7: an item combine
  seq 2213  0x2769  4301 B  the reply — atoms 0..3 byte-identical, atom 4 (op 7) gets id 16

Container format (little-endian):

  "TSIS"            4 bytes magic
  u32 recordCount
  per record:  u32 seq | u16 opcode | u32 payloadLength | payloadLength bytes

The empty 30-byte form of 0x2768 (seq 519 / 2540 / 4179) is small enough to live inline in the
tests and is not in here. See status/INVENTORY-DESIGN.md for the layouts.
