# T218 - the Arbiter already sends AdminLevel 5

## The measurement

`cap_makeitem3.log` has exactly one `AS_ENTER_WORLD` (0x138E), for player 9 `caludesucks` - the same
session whose console printed `SpawnComplete caludesucks(9) AdminLevel[0]`:

```
payload 103..120:  06 00 00 00 | 00 00 00 00 | 05 00 00 00 | 00 00 00 00 | 00 00
                   ^103 SubFee   ^107 Restrict  ^111 AdminLevel = 5
```

**payload+111 = 5.** `WorldEntry.cs:51` already builds it with
`GmCommandHandlers.LevelOf(s, Program.Store)`, and the `C_ADMIN` line for the same session resolves
5 as well, so both halves of the Arbiter agree and the wire carries the agreed value.

## Against retail

`cap_final` is the retail stack whose GM was adminLevel 1 (T46). Its `0x138E` for player 1 carries
`01` at **the same offset 111**, and the window around it is byte-identical to ours:

```
ours   103..120:  06 00 00 00  00 00 00 00  05 00 00 00  00 00 00 00  00 00
retail 103..120:  06 00 00 00  00 00 00 00  01 00 00 00  00 00 00 00  00 00
```

A full 183-byte diff of the two frames leaves **eleven differing runs, all of them per-session
values** - ArbiterClient/ArbiterUser handles (16, 24), UserDbId (32), SessionKey (44), continent
(48), instance and x/y/z (52-63), direction (65), VisibleRange (72), Ticket (80), GameId (84) - and
then 111. Nothing structural differs, so no field is shifted and World's reader sees the level where
it saw retail's.

## World's side

`WorldServer.exe.c:847798-847802` registers `adminLevel` in the generated field-binding table at
User offset **0xA474**, and `:935500` is the line that prints it:

```c
local_3c0 = *(undefined4 *)((longlong)param_1 + 0xa474);
... L"[%4d,%4d] [%4d] SpawnComplete [%s] %s(%d) AdminLevel[%d]\n"
```

The only other write to 0xA474 in the whole binary is `:803532`, `= 0` - the initialiser. So
`AdminLevel[0]` means the field was never filled from the frame, on a frame that carries 5 exactly
where retail's carries 1.

## What that leaves

The Arbiter side of this brief is already satisfied and is now pinned by tests. What it does **not**
explain is the console line, and the honest reading is that the quoted `AdminLevel[0]` came from a
session entered before that account was listed in `TERASHARP_GM_ACCOUNTS` - `LevelOf` returns the
stored row level (0) until the env list or the account row says otherwise, and both the C_ADMIN log
and this capture show 5 once it does.

To settle it, capture one enter-world and the console line from the **same** session: if a frame
with `05 00 00 00` at payload+111 still prints `AdminLevel[0]`, the fault is in World's PDL
definition for this build and the next step is diffing its `AS_ENTER_WORLD` field list against the
Arbiter's writer, not the byte offsets.

Note from T46, unchanged: those two log lines are the **only** reads of 0xA474 in WorldServer - it
stores the level and prints it but never gates on it. So a 0 there is a wrong log line and does not
by itself explain a refused `makeitem`; T217/T217b hold the rest of that thread.

## Tests

- `T218_admin_level_five_lands_at_payload_111` - a level-5 session's payload reads 5 at 111, a
  level-0 one reads 0, and the fields either side are unmoved.
- `T218_the_captured_enter_world_frames_agree_on_the_offset` - our captured window against retail's.
- `T218_enter_world_passes_the_resolved_level` - `WorldEntry` passes `LevelOf`, not a literal, and
  a listed accountDBID resolves to the GM level with nothing stored on the row.
