starter_blob.bin — 15312-byte world blob the real ArbiterServer sent in DBS_USER_ENTERWORLD (0x2738)
for a never-entered character ("Test", playerId 2, cap_newchar.log 05:49:03). Use as the template for
character creation. Per-character fields (diff vs dob's blob):
  112  u32 playerId, then UTF-16LE name (null-terminated)
  208..238  HP at 208 (u32), 216 u32, x,y,z floats at 220/224/228, u32 at 232, ZONE at 236 (u32;
            Test: 5 = Island of Dawn, later 9827; dob: 7005 Velika). NOT 208 as earlier notes said.
  192  u32 race, 196 u32 gender, 200 u32 class   (Test: 4 / 1 / 12 = Elin female valkyrie)
  288  appearance 8 B, 296 u32 appearance2 (100), 312 details 32 B, 344 shape 64 B
       -- these seven are byte-for-byte the values C_CREATE_USER carried (cap_newchar_client.log
          packet 35). StarterBlob.Build patches all of them (T12); before that every character
          spawned as the template's Elin valkyrie whatever the player picked.
  304  u32 NOT per-character -- WorldEntry copies it into AS_ENTER_WORLD payload[72]. Leave it.
  416, 430..445  level/exp/misc counters
  6772  two timestamps (1970-01-01 = never)
  15053..15310  trailing flags/counters
Everything else is identical between a level-1 and a level-58 blob.
Also from this capture: gameId is a per-login counter (0x80000AF00001 for playerId 2), NOT 0xAF00000|playerId.
