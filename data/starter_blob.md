starter_blob.bin — 15312-byte world blob the real ArbiterServer sent in DBS_USER_ENTERWORLD (0x2738)
for a never-entered character ("Test", playerId 2, cap_newchar.log 05:49:03). Use as the template for
character creation. Per-character fields (diff vs dob's blob):
  112  u32 playerId, then UTF-16LE name (null-terminated)
  208..238  start zone/section + x,y,z floats at 220/224/228 (Test: zone 5, 16260, 1253, -4410)
  304, 416, 430..445  level/exp/misc counters
  6772  two timestamps (1970-01-01 = never)
  15053..15310  trailing flags/counters
Everything else is identical between a level-1 and a level-58 blob.
Also from this capture: gameId is a per-login counter (0x80000AF00001 for playerId 2), NOT 0xAF00000|playerId.
