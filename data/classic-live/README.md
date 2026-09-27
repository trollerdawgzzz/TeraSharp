# `data/classic-live/` — leaderboard reference frames

Empty in a clone. The five `.hex` files that the T133 leaderboard tests read are not in this
repository and must not be put in it.

## Why this folder has a rule of its own

The frames these tests use are `S_*_RANKING_LIST` and `S_*_LEADER_BOARD_INFO` replies. A ranking
list is, by construction, **a list of other people's character names** — a hundred of them per
frame, plus guild names. Captured off a public server, that is a roster of third parties who did
not agree to be in anyone's git history, and it stays out regardless of how small the file is or
how useful the bytes are.

The same applies to any other capture you take on a server that is not yours: fine to test
against locally, never committed.

## What the tests need

Each file is one server→client frame, whole, as space-separated hex. Blank lines and lines
starting with `#` are ignored, so a header comment saying where the frame came from is welcome.

| File | Frame |
|---|---|
| `S_PVE_LEADER_BOARD_INFO-<n>.hex` | the PvE board header — the dungeon id set and the season window |
| `S_PVP_LEADER_BOARD_INFO-<n>.hex` | the same for PvP |
| `S_PVE_RANKING_LIST-<n>.hex` | a PvE ranking page |
| `S_PVE_RANKING_LIST-<n>-class16.hex` | the aggregate reply for class filter 16 |
| `S_PVP_RANKING_LIST-<n>.hex` | a PvP ranking page |
| `S_CHANGE_EVENT_MATCHING_STATE-10580.hex` / `-10581.hex` | T161: classic_live3's state pair before a FIN (90 / 160 event ids) |
| `S_FIN_INTER_PARTY_MATCH-10585.hex` | T161: the FIN of classic_live3's formed match |

`<n>` is the frame number in your own listing; `LoadLiveFrame` takes the exact file name, so if
you regenerate these, the test's file names have to match what you wrote. Absent, the tests
print `(skipped: data/classic-live/<name> not found)` and pass.

## Generating them

Capture a session on **your own** server, open a leaderboard in the client, then:

```powershell
cd tools
.\npcap-to-capture.ps1 -Npcap <your capture>.npcap -Out <your capture>.log
.\reframe-client.ps1   -Log  <your capture>.log `
                       -Packets S_PVE_RANKING_LIST,S_PVP_RANKING_LIST,S_PVE_LEADER_BOARD_INFO,S_PVP_LEADER_BOARD_INFO
```

`<log>_ctl.txt` lists the frames with their numbers; `<log>_frames.txt` has the full hex. Copy
the hex line for one frame into a file here and name it as above.

## What the tests assert, so you know what a usable frame looks like

The element layouts are pinned in `World/RankingBoards.cs` and described in
`status/LEADERBOARD.md`. The decoder walks the list structurally — every element's `here` must
equal the offset it was found at, every `next` must point at the following element, and the walk
must end exactly at the frame's end — so a frame from a different build will fail loudly rather
than decode into nonsense. Ranks are competition ranks (1, 1, 3), not dense.

A page with only your own characters on it is enough to exercise all of that.

## T147b - crafting (`D:\packetlogs\classic_craft.log`)

A second live character, one that crafts and gathers. These are World-built packets, so the
tests pin their layout by decode and re-encode and check the fields the Arbiter feeds.

| File | Frame | What it pins |
|---|---|---|
| `S_ARTISAN_SKILL_LIST-95.hex` | 95, 169 B | five skills, the 32-byte element, proficiency values 500 / 555 / 590 / 1 / 9 |
| `S_ARTISAN_RECIPE_LIST-96.hex` | 96, 3451 B | 36 learned recipes, the 46-byte element and its 17-byte materials |

## T156 - the Vanguard Initiative window (`D:\packetlogs\classic_live3.log`)

World-built too: the tests decode them and check every byte is accounted for.

| File | Frame | What it pins |
|---|---|---|
| `S_AVAILABLE_EVENT_MATCHING_LIST-7256.hex` | 7256, 2421 B | 21 quests, one bonus entry, the 94-byte fixed part (level 65), 47-byte quest elements and their nested arrays |
| `S_AVAILABLE_EVENT_MATCHING_LIST-53330.hex` | 53330, 2421 B | the same list after quest 800029's condition went 0/1 -> 1/1 |
| `S_UPDATE_EVENT_MATCHING_BONUS_INFO-53331.hex` | 53331, 39 B | 23-byte fixed part + one 16-byte bonus element |
| `S_REMOVE_EVENT_MATCHING_QUEST-53367.hex` | 53367, 8 B | quest id 800029 |
| `S_ADD_NEW_EVENT_MATCHING_QUEST-53429.hex` | 53429, 16 B | one 8-byte element, quest id 800029 |

## T157 - the Instance Matching tabs (`classic_live3.log`, `classic_live2.log`)

World-built as well; the tests decode them and check every byte is accounted for.

| File | Frame | What it pins |
|---|---|---|
| `S_VIEW_INTER_PARTY_MATCH_DUNGEON_LIST-7964.hex` | classic_live3 7964, 803 B | 15 dungeons, 36-byte elements, [role][level] queue indicators |
| `S_VIEW_INTER_PARTY_MATCH_BATTLEFIELD_LIST-9364.hex` | classic_live2 9364, 380 B | 7 battlegrounds and one reward-guild entry |
