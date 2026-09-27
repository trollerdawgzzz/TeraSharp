# `data/` — you generate this yourself

**This folder is empty in a clone, and that is deliberate.** TeraSharp ships no captured game
traffic. Everything that used to live here was lifted out of a running 100.02 server's wire, so
none of it can be redistributed. `.gitignore` excludes `data/*.bin` and `data/*.hex`, and
`tools/audit-release.ps1` fails if one is staged anyway.

What that costs you, and what it does not:

| | |
|---|---|
| Tests | the byte-exact tests print `(skipped: data/<name> not found)` and pass. The suite is green on a fresh clone. |
| Runtime | **no file is required to start** - T209, T209b and T209c retired all four. Each is still an override. See "Runtime blobs" below. |

`TeraSharp.Arbiter.exe --selftest` names every missing required file and exits non-zero, so you
find this out before a player does, not after.

---

## The container format

Most fixtures are **TSIS**, a nine-line container invented so the tests need neither a log path
nor tens of kilobytes of hex literals. All little-endian:

```
"TSIS"                  4 bytes, magic
u32 recordCount
per record:
    u32 seq             the frame number the reframer printed
    u16 opcode
    u32 payloadLength
    payload
```

Records are keyed by `seq`, so a test cites the same frame number you can search for in your own
listing. Payloads are stored **without** the frame header, except in `cap_client_settings.bin`,
which keeps whole client frames — see the table.

## Making one

Three steps, all from `tools/`:

```powershell
# 1. If you captured with Noctenium, turn the .npcap into a text capture first.
.\npcap-to-capture.ps1 -Npcap <your capture>.npcap -Out <your capture>.log

# 2. Reframe. Use reframe-tap for an Arbiter<->World tap log, reframe-client for a
#    decrypted client capture. Ask for the opcodes the fixture needs; you get
#    <log>_ctl.txt to read and <log>_frames.txt to build from.
.\reframe-tap.ps1    -Log <your tap log>    -Opcodes 0x2890,0x2891,0x2909,0x2910
.\reframe-client.ps1 -Log <your client log> -Packets S_LOAD_CLIENT_USER_SETTING

# 3. Pack the frames you want into a container.
.\make-tsis.ps1 -Frames <your log>_frames.txt -Out ..\data\cap_t26.bin -Seq 336,413,376,511,2068
```

`make-tsis.ps1 -Seq` errors rather than writing a short file when a frame number is not in the
listing, so a typo cannot quietly produce a fixture that half-tests something. Omit `-Seq` to
take every frame in the file. `-HeaderBytes` says how much to strip: **6** for a tap frame
(`[u32 length][u16 opcode]`), **4** for a client packet (`[u16 length][u16 opcode]`), **0** to
keep the frame whole.

Read `tools/README.md` first — it explains the frame numbering (by arriving chunk, not stream
position), what `-Skip` drops by default, and the two PowerShell traps both reframers work
around.

---

## Fixtures

Every one of these is optional. Its tests skip if it is absent.

| File | Source | Header | What it pins |
|---|---|---|---|
| `cap_client_settings.bin` | client capture | **0** — whole frames | client settings load/save round-trip |
| `cap_guild.bin` | tap | 6 | guild packets |
| `cap_item_single.bin` | tap | 6 | `DBS_ITEM_SINGLE`, the allocated-id echo |
| `cap_t15.bin` | tap | 6 | the DB-proxy reply shapes |
| `cap_t22_newchar.bin`, `cap_t22_relog.bin` | tap | 6 | character creation vs. relog |
| `cap_t26.bin` | tap | 6 | reputation and fatigability records |
| `cap_t38.bin` | tap | 6 | the bypass tunnel — ticket and sequence fields |
| `cap_timeline.bin` | tap | 6 | dungeon timeline |
| `cap_t153.bin` | tap | 6 | T153: atom op 8 creates, their replies and the real stored records (see `cap_t153.md`) |
| `cap_t150.bin` | tap | 6 | T150b: `DBS_INCREASE_INVENTORY_SIZE`, the bag-size blobs, the four generic acks (see `cap_t150.md`) |
| `cap_t157.bin` | tap | 6 | T157: the Instance Matching list requests (0x1644 / 0x1645) and World's tunnelled tabs (see `cap_t157.md`) |
| `cap_t156.bin` | tap | 6 | T156: the Vanguard Initiative's A<->W pairs - 0x1507/0x1591, daily event, add-reward counts, reset stamps (see `cap_t156.md`) |
| `t93_admin_inven_frame724.bin` | tap | raw, one frame | the admin inventory frame |

A `<name>.md` beside each one used to record which capture and which sequence numbers it came
from. Write your own as you go; future-you will want it, and the tests cite frame numbers that
only mean something against a listing.

## Runtime blobs

These four **were** the runtime blobs `DbProxyHandlers` and `CharacterStore` read while the server
is running. T209, T209b and T209c retired all of them: the server starts and runs with an empty
`data\` folder. Every one is still an override - drop a capture in and it wins.

| File | Shape | Needed? | What it is |
|---|---|---|---|
| `starter_blob.bin` | raw, 15312 B | **no** (T209) | the world blob `DBS_USER_ENTERWORLD` (`0x2738`) carries for a never-entered character. Only 232 of its 15312 bytes are non-zero, and `StarterBlob.Generate` now builds all of them from `CreateCharData.xml`, `DefaultSkillSet.xml` and the create request - byte-identical to this capture except eleven bytes of padding above a bool or u8 field, which the real Arbiter never initialises. A file here still wins if you drop one in. |
| `starter_inventory.bin` | raw payload | **no** (T209c part 2) | the starter kit, as a `0x27A4` payload. The item LIST comes from `CreateCharData.xml`; what this file supplied is the 536-byte record skeleton each item is cut from, and `StarterInventory.BuildSynthetic` now builds those instead. Live-verified: a character created with the file absent entered world with its six starter items and still had them after a relog. Set `economy.synthItemRecords` to `false` to go back to copying a file you still have. |
| `promotions_147E.bin` | **no** (T209c) | 23 records of 1368 bytes - and never promotions. The Arbiter's own opcode table names `0x147D`/`0x147E`/`0x1480`/`0x1484` `SA_LOAD_GUARD`, `AS_LOAD_GUARD`, `AS_LOAD_GUARD_FINISH` and `AS_ELECTION_STATE` (`Arb_part_003.c:4978-4993`): the castle-and-lord Guard system. Retail's own walk emits no `AS_LOAD_GUARD` frame when the guard tree is empty, and this server has no castles, so it now sends the election state and the finish marker and nothing between them. Each record was a `memcpy` of another process's live Guard struct - heap pointers included - so replaying it pushed 23 foreign castles into a world that has none. Delete the file; nothing reads it. |
| `handshake_burst.bin` | TSIS, 63 records | **no** (T209b) | the 63 config pushes the Arbiter sends World right after the startup handshake. `World/HandshakeBurst` builds all 63 from `Protocol/InterServerDefinitions` - 50 defs whose every field name comes from the retail Arbiter's own dump helpers - and the result is byte-identical to this capture, two clock-driven `u64`s aside. Nothing reads it at runtime; it is the evidence `Tests/T209b.cs` compares against, and that test skips when it is absent. |

Each has an environment-variable override — `TERASHARP_STARTER_BLOB`,
`TERASHARP_STARTER_INVENTORY` — so they can live outside the repo. See `.env.example`.

To produce the one that is still needed: capture your own server's startup and one character
creation, reframe, and cut the payload out - it is a single payload written straight to a file.
`handshake_burst.bin`, if you want the T209b comparison to run rather than skip, is a TSIS
container like the fixtures above (`make-tsis.ps1` with the burst's frame numbers).

## `classic-live/`

A second, separate folder for reference frames captured from a *live* server. It is subject to
the same rule and then some — see `data/classic-live/README.md`.
