# `data/` — you generate this yourself

**This folder is empty in a clone, and that is deliberate.** TeraSharp ships no captured game
traffic. Everything that used to live here was lifted out of a running 100.02 server's wire, so
none of it can be redistributed. `.gitignore` excludes `data/*.bin` and `data/*.hex`, and
`tools/audit-release.ps1` fails if one is staged anyway.

What that costs you, and what it does not:

| | |
|---|---|
| Tests | the byte-exact tests print `(skipped: data/<name> not found)` and pass. The suite is green on a fresh clone. |
| Runtime | **four files are required to start.** Without them a new character cannot be created. See "Runtime blobs" below. |

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

These four are **not** test fixtures. `DbProxyHandlers` and `CharacterStore` read them while the
server is running, and character creation fails without them.

| File | Shape | What it is |
|---|---|---|
| `starter_blob.bin` | raw, 15312 B | the world blob `DBS_USER_ENTERWORLD` (`0x2738`) carries for a never-entered character. Used as the template for every new character, then patched per character: playerId at 112, UTF-16LE name after it, HP at 208, x/y/z at 220/224/228, zone at 236. |
| `starter_inventory.bin` | raw | the starter kit, as a `0x27A4` payload |
| `promotions_147E.bin` | raw | `AS_PROMOTION_RECORD` (`0x147E`) records of 1368 bytes each, concatenated. The loader rejects a file whose length is not a multiple of 1368 and falls back to the replay table. |
| `handshake_burst.bin` | TSIS | the 63 config pushes the Arbiter sends World right after the startup handshake. Two `u64`s in it are timestamps the Arbiter restamps at send time, so a stale capture is still usable. |

Each has an environment-variable override — `TERASHARP_STARTER_BLOB`,
`TERASHARP_STARTER_INVENTORY` — so they can live outside the repo. See `.env.example`.

To produce them: capture your own server's startup and one character creation, reframe, and cut
the payloads out. `handshake_burst.bin` is a TSIS container like the fixtures above
(`make-tsis.ps1` with the burst's frame numbers). The three raw ones are a single payload each,
written straight to a file.

## `classic-live/`

A second, separate folder for reference frames captured from a *live* server. It is subject to
the same rule and then some — see `data/classic-live/README.md`.
