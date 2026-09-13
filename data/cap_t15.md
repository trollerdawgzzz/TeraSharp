cap_t15.bin — the T15 request/reply frames from `D:\packetlogs\cap_newchar.log` (real
ArbiterServer, new character "Test" playerId 2, Island of Dawn, 05:49–05:53), reframed by u32
length and stored as payloads (frame length - 6). Extracted so the byte-exact tests do not depend
on `D:\packetlogs`. Sequence numbers match `D:\packetlogs\cap_newchar_ctl.txt`.

Container format (little-endian), identical to `cap_item_single.bin` (T13):

  "TSIS"            4 bytes magic
  u32 recordCount
  per record:  u32 seq | u16 opcode | u32 payloadLength | payloadLength bytes

| seq  | op     | frame  | what                                                                  |
|------|--------|--------|-----------------------------------------------------------------------|
| 540  | 0x272E |   116  | SDB_SET_QUEST_INFO, sqlType 22 (INSERT), no reward atoms              |
| 541  | 0x272F |   115  | the reply — quest row id 2 at payload[25]                             |
| 1303 | 0x272E |   972  | sqlType 23, one reward atom, op 9 (nothing is allocated for it)       |
| 1306 | 0x272F |   971  | the reply — atom echoed byte-identical                                |
| 1804 | 0x272E |   972  | sqlType 23, one reward atom, op 7 (insert) with item DB id 0          |
| 1807 | 0x272F |   971  | the reply — the Arbiter filled in item id 13 at atom+16               |
| 2364 | 0x272E |  3540  | sqlType 23, four atoms (ops 6, 11, 6, 11) that already carry ids      |
| 2367 | 0x272F |  3539  | the reply — all four echoed unchanged                                 |
| 2750 | 0x278E |   896  | SDB_USER_LEARN_SKILL, skill 150199, one op-9 fee atom                 |
| 2751 | 0x278F |   885  | the reply — atom echoed, skill-period list empty                      |
| 1369 | 0x2802 |    46  | SDB_ACCOMPLISH_USER_ACHIEVEMENT 5991, first time                      |
| 1370 | 0x2803 |    43  | the reply — the 24-byte record echoed (newly accomplished)            |
| 2605 | 0x2802 |    46  | achievement 5991 AGAIN (different timestamp)                          |
| 2606 | 0x2803 |    19  | the reply — EMPTY list: a repeat is not "newly accomplished"          |
| 2722 | 0x2802 |    46  | achievement 5992, first time                                          |
| 2723 | 0x2803 |    43  | the reply — echoed                                                    |
| 413  | 0x2891 |    78  | SDB_UPDATE_REPUTATION_INFO, op 1 (insert), 52-byte record             |
| 417  | 0x2892 |    11  | the reply — [u8 ok][u32 reqId], the only ok-first reply in this set   |
| 719  | 0x286E |    18  | SDB_ADD_TUTORIAL_SIMPLE_TIP, tip 1                                    |
| 728  | 0x286F |    11  | the reply — [u32 reqId][u8 ok]                                        |
| 634  | 0x2944 |    22  | SDB_UPDATE_SEREN_GUIDE_INFO                                           |
| 635  | 0x2945 |    15  | the reply — [u32 reqId][u32 playerId][u8 ok]                          |
| 505  | 0x293C |    58  | SDB_UPDATE_USER_DAILY_EVENT_COUNT, 20-byte record                     |
| 507  | 0x293D |    11  | the reply — [u32 reqId][u8 ok], reqId at request payload[8]           |
| 503  | 0x293E |    20  | SDB_UPDATE_GET_EXTRA_REWARD                                           |
| 504  | 0x293F |    11  | the reply — [u32 reqId][u8 ok]                                        |
| 3466 | 0x2927 |    14  | SDB_CANCEL_NPC_ARENA_BET — fire and forget, no reply exists           |

Note on 0x2927: all five occurrences in the capture (seq 3466, 4116, 4138, 4151, 4198 — the
logout-countdown ticks) are followed by no A->W frame, and
`Handler_SDB_CANCEL_NPC_ARENA_BET` (Arb_part_063.c:1315) has no `SendToSession` at all. Only one
is kept here; the others differ from it in two bytes of countdown state.

Layouts and the decompile line numbers are in the comments on the T15 constants in
`src/TeraSharp.Arbiter/World/DbProxyHandlers.cs`.
