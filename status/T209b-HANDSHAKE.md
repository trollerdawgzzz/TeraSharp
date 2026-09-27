# T209b - the handshake burst, built instead of replayed

T23 shipped the post-handshake configuration burst as 63 captured payloads in
`data/handshake_burst.bin`. T209b builds all 63 from named fields, so the repo carries no captured
retail bytes for it (T139) and `--selftest` no longer needs the file.

## 1. What landed

| File | Change |
|---|---|
| `Protocol/DefinitionWriter.cs` | `PacketFraming.{Client,InterServer}`: header base 4/u16 offsets or 6/u32. One constructor argument; every write path already went through `WriteOffset`/`PatchOffset`. |
| `Protocol/InterServerDefinitions.cs` | **new.** The burst's 50 opcodes as def texts, plus the AS_/DBS_ name↔opcode map (`data.json` has client opcodes only). |
| `World/HandshakeBurst.cs` | **new.** Frame order + the non-zero values. `Build(now, darkRiftReset?)` → 63 `(op, payload)`. |
| `World/DbProxyHandlers.cs` | `OnWorldReady` builds the burst; the "file missing, burst NOT sent" branch is gone. `LoadHandshakeBurst`/`ParseBurst`/`BuildHandshakeBurst` stay for the test. |
| `World/SelfTest.cs` | `CheckHandshakeBurstBuild()` (63 frames build) replaces `CheckHandshakeBurst(file)` in `Report`. The file check keeps a `required: false` mode for T37. |
| `Tests/T209b.cs` | **new.** 4 tests, headline one byte-exact over all 63 captured records. |

## 2. The gate

63 frames built at the capture's own instant vs all 63 records of `data/handshake_burst.bin`:
**63/63 byte-identical, 1082 frame bytes, 0 mismatches.** Opcodes, order and payloads all.

## 3. Where the field names came from

Not guesses. Each of the 50 opcodes has a dump helper in the retail Arbiter that prints its record
field by field, with the field name as a wide string literal next to a type-specific printer:

| Helper | Prints |
|---|---|
| `FUN_14016bb70` | u32 |
| `FUN_14016c4c0` | u64 |
| `FUN_14016c570` | u8 / bool |
| `FUN_14016bd70` | ref (bytes / list) |

The six frames with no schema got their layout from the Arbiter's own writer instead - it is the
builder, so it names the fields:

| Frame | Layout | Payload |
|---|---|---|
| `0x29E2 DBS_TBA_UPDATE_ROTATION` | `array<int32> RotationHeroList` | 152 B = 8 header + 12 × (4+4+4) |
| `0x157F AS_CONTENTS_ON_OFF_LIST` | `bytes ContentsOnOffList` | 12 B = int32 `1, 0, 11` |
| `0x150A AS_SET_EP_SYSTEM_EVENT` | `bytes ValueTable` | 60 B = 15 × float `1.0` (no event running) |
| `0x14D1 AS_SET_DARK_RIFT_DAILY_COMPLETED` | `bytes DailyCompletedList; int64 LastResetDateTime` | 16 B |
| `0x28F8 AS_EVENT_HUNTINGBONUS` (×2) | 4 × `bytes` (`Begin_serverUTCTime`, `End_serverUTCTime`, `NpcList`, `ItemList`) then `bool AddBegin`@32, `bool AddEnd`@33, `int32 ArbiterServerPlanetId`@34, `int32 Identity`@38, `int32 IsOn`@42 | 46 B, fully accounted |

## 4. Why no captured bytes are needed

Every field the captures show as zero, false or empty is simply absent from the value table; the
writer's own empty forms are what retail sends:

| Absent | On the wire (inter-server) |
|---|---|
| `array` | `count=0, offset=0` → 8 zero bytes |
| `bytes` | `offset=<end of fixed part>, length=0` → `0e 00 00 00 00 00 00 00`, or `16 …` for `0x14D1`'s trailing u64 |
| fixed field | zeroed in place |

So the burst's entire non-zero content is five constants and two lists: `ContentsOnOff` `1,0,11`;
15 × float `1.0`; `ArbiterServerPlanetId` 2800; `MaxJoinCount` 1; `CurrentSeasonId` -1; festival ids
1-5; the 12 TBA hero template ids. Plus three timestamps - see below.

## 5. The three timestamps

| Frame | Value |
|---|---|
| `0x15BD AS_SYNC_DATE_TIME.ArbiterTime` | unix seconds at send time. World echoes it back as `0x15BC`. |
| `0x14D1 …LastResetDateTime` | `DailyResetUnixSeconds(now)` - the most recent 15:00 UTC. |
| `0x15DE …LastResetTime` | **constant** `1789185449` = 2026-09-12T03:57:29Z. All four captures carry this same instant, a day apart and on both sides of a daily reset, so retail is echoing a value it keeps in SQL, not reading a clock. Ours stays a constant until a capture shows it moving. |

## 6. What the capture file is for now

Nothing at runtime. `Tests/T209b.cs` compares against it and skips when it is absent, so
`data/handshake_burst.bin` and `data/handshake_burst.md` can be `git rm`'d whenever you like - the
build, `--selftest` and login are unaffected, and the only cost is losing that comparison.

QUICKSTART step 3 is down to one file (`starter_inventory.bin`, still read on every world entry
unless `economy.synthItemRecords` is on), so it could not go away entirely.
