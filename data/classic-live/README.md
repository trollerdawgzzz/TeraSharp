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
