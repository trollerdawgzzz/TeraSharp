cap_guild.bin - every guild frame that exists in any capture. There are five, all from the
boot-time init, and all of them are the *empty* answer: **no guild has ever been created against
the tap**, so this file is the entire guild ground truth we have.

Reframed by u32 length and stored as payloads (frame length - 6).

Container format (little-endian), identical to `cap_t15.bin` / `handshake_burst.bin`:

  "TSIS"            4 bytes magic
  u32 recordCount
  per record:  u32 seq | u16 opcode | u32 payloadLength | payload

| seq | op       | bytes | name | what |
|-----|----------|-------|------|------|
| 1   | `0x27CF` |     0 | `SDB_INIT_GUILD` | World asks for every guild, at boot. Zero-length. |
| 2   | `0x27ED` |  9135 | `DBS_INIT_GUILD_DATA` | the "no guilds" terminator - one default-constructed `GuildData` |
| 93  | `0x2954` |     8 | `SDB_LOAD_CITY_GUILD_INFO` | `01 00 00 00 01 00 00 00` |
| 94  | `0x2955` |    16 | `DBS_LOAD_CITY_GUILD_INFO` | `00000000 00000000 01000000 01000000` |
| 101 | `0x14B3` |     8 | *(unnamed)* | all zero |

All five are from `D:\packetlogs\arb_world.log` (the real ArbiterServer login sequence); seq is
that log's chunk index.

### seq 2 decoded

Frame-relative offsets in brackets; payload index = frame offset - 6.

    [06] u32  guildDataOffset   = 19        -> payload index 13
    [0A] u32  guildDataLength   = 0x23A0    = 9120
    [0E] u32  guildLogoIdOffset = 9139      -> payload index 9133
    [12] u8   success           = 0         <- the "no guilds" terminator
    [13] byte[0x23A0]  GuildData
    [23B3] wchar_t[]   GuildLogoId          = L""

The 9120-byte `GuildData` is a default-constructed record. Its only meaningful non-zero bytes:

| blob off | value | field |
|---|---|---|
| `0x0064` | 1 | GuildLevel |
| `0x0080` | `B2 07 01 00 01 00 ...` | LastIncentiveTime, `tagTIMESTAMP_STRUCT` = 1970-01-01 |
| `0x2328` | same | GuildWarAcceptableToggleTime |
| `0x2348` | same | an unnamed timestamp - no SQL column, no accessor |
| `0x235C` | 1 | JoinMinLevel |
| `0x2360` | 70 | JoinMaxLevel (the level cap) |
| `0x2364` | 1 | GuildJoinType |

...plus **two bytes of uninitialised alignment padding** at blob `0x0222` (after
`GuildAnnounce`, a `wchar[201]`) and blob `0x024A` (after `GuildTitle`, a `wchar[15]`). Those
two holes are the ONLY bytes that differ between the four captures of this frame:

    arb_world    @0x222 = 0000   @0x24A = B379
    lobby_tap    @0x222 = FAB7   @0x24A = E9C9
    cap_newchar  @0x222 = 0000   @0x24A = 0000
    09-13        @0x222 = 0000   @0x24A = 0000

TeraSharp replays this capture verbatim, so it leaks `0xB379` on every login. World never reads
those bytes, but `GuildPackets.BuildEmptyDbsInitGuildData()` writes zeros there, and
`Guild_DBS_INIT_GUILD_DATA_reproduces_the_capture_except_the_padding_holes` asserts the two
differ in exactly those two bytes and nowhere else.

Full decode: `status/GUILD-DESIGN.md` sections 2.1 and 4.3.
