cap_timeline.bin - the two non-empty `DSA_DUNGEON_TIMELINE_OPEN_INFO` (0x13F2) frames and the
`AS_DUNGEON_TIMELINE_ON_OFF` (0x1581) bursts the real ArbiterServer echoed back from them.
Reframed by u32 length and stored as payloads (frame length - 6).

Container format (little-endian), identical to `cap_t15.bin` / `handshake_burst.bin`:

  "TSIS"            4 bytes magic
  u32 recordCount
  per record:  u32 seq | u16 opcode | u32 payloadLength | payload

| seq | op       | bytes | source | what |
|-----|----------|-------|--------|------|
| 122 | `0x13F2` |  2164 | `lobby_tap.log` frame 122, 02:51:09.399Z | count 98, listOffset 14, 98 x 0x16-byte nodes |
| 123 | `0x1581` |  1274 | `lobby_tap.log` frames 123-124 | the 98 reply payloads **concatenated**, 13 B each |
| 111 | `0x13F2` |  2164 | `cap_newchar.log` frame 111, 05:48:53.026Z | byte-identical to seq 122 |
| 112 | `0x1581` |  1274 | `cap_newchar.log` frames 112-113 | byte-identical to seq 123 |

The `0x1581` records are a concatenation, not a single frame: 98 replies x 13 bytes. Split them
on `DbProxyHandlers.Build1581`'s 13-byte stride. Storing them whole is what makes
`Timeline_echo_reproduces_*` a capture-to-capture test rather than a test against a layout we
wrote down ourselves.

## The two frames

`0x13F2` payload:

```
[0] u32 count = 98
[4] u32 listOffset = 14   (FRAME-relative, so payload+8)
then 98 x 0x16 bytes:
  [+0]  u32 self          [+4]  u32 next
  [+8]  u32 DungeonId     [+12] u8  CurrOpen      (1 in every record of both frames)
  [+13] i64 NextChange    (0 in every record)     [+21] u8 SendSystemMessage (0)
```

Field names from the PDL dumper at `Arb_part_016.c:2781` (`L"OpenInfo"`, `L"DungeonId"`,
`L"CurrOpen"`, `L"NextChange"`, `L"SendSystemMessage"`); the 0x16 node size is the handler's own
bounds check `(longlong)iVar2 + 0x16U <= (ulonglong)(longlong)*piVar9`.

`0x1581` payload, 13 bytes:

```
[0] u32 DungeonId   [4] u8 IsOn   [5] i64 NextChange
```

Writer `FUN_1407ace90` (`Arb_part_066.c:17289`); dumper names at `Arb_part_011.c:10058`.

## Why this file exists

The 98 ids are not a list the Arbiter chooses - it echoes World's, one 0x1581 per record, in
order. `Handler_DSA_DUNGEON_TIMELINE_OPEN_INFO` (`Arb_part_062.c:332`) is a pure fan-out into
`DungeonManager::SetDungeonTimelineOpen` (`FUN_140786fd0`, `Arb_part_065.c:13433`).
`status/HANDSHAKE-DATA.md` section 0 has the evidence; `DbProxyHandlers.OnDungeonTimelineOpenInfo`
is the implementation.

Across the four captures there are 911 `0x13F2` frames: 909 of the empty 14-byte "heartbeat" form
and exactly these two. `arb_world.log` (09-12) and the 09-13 relog contain **no** non-empty
`0x13F2` and **no** `0x1581` at all, and login worked in both - which is why the captured 98-id
burst in `DbProxyHandlers.PostHandshakeDungeonIds` is a fallback and not the mechanism.
