# data/classic-live - the live Classic+ leaderboard reference (T133)

Whole client frames, hex, one file per frame, lifted out of
`D:\packetlogs\classic_live.log` (which T130's `tools\npcap-to-capture.ps1` produced from
`classic_live.npcap`). They are the **first populated leaderboard** this project has: the
real Arbiter on 100.02 never answered `C_REQUEST_P*_RANKING` at all, so every byte of
`RankingBoards` up to T126 came from the decompile alone.

| File | Frame | What it pins |
|---|---|---|
| `S_PVP_LEADER_BOARD_INFO-5608.hex` | 5608, 60 B | season 15, 4 battleground ids, a 28-day window |
| `S_PVE_LEADER_BOARD_INFO-5609.hex` | 5609, 92 B | season 15, 8 dungeon ids, its own 28-day window |
| `S_PVE_RANKING_LIST-5809.hex` | 5809, 932 B | 20 rows, class-9 filter - the 31-byte PvE element |
| `S_PVE_RANKING_LIST-5856-class16.hex` | 5856, 4847 B | 105 rows, class 16 - the aggregate reply, and parties sharing a rank |
| `S_PVP_RANKING_LIST-5818.hex` | 5818, 3938 B | 100 rows - the 23-byte PvP element, and the only live `rookie` / `changedRank` values there are |

The tests read these and assert a full decode/re-encode round trip: every row is taken apart
at T126's offsets and put back, and the result has to be the live frame byte for byte. A
layout error anywhere - one field at the wrong offset, one length miscounted - breaks it.

Lines beginning `#` are comments; everything else is space-separated hex.
