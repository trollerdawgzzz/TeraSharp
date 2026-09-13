cap_client_settings.bin — the client-settings packets from the two DECRYPTED client captures,
stored as whole client frames (including the 4-byte `[u16 length][u16 opcode]` header, unlike the
Arbiter<->World containers, which store payloads). Extracted for the T19 tests so they do not need
`D:\packetlogs`.

Container format (little-endian), the same TSIS shape as `cap_item_single.bin` and `cap_t15.bin`:

  "TSIS"            4 bytes magic
  u32 recordCount
  per record:  u32 key | u16 opcode | u32 frameLength | frameLength bytes

Keys are the packet numbers from the source log. Records from `lobby_proxy.log` are offset by
100000 so the two logs cannot collide.

| key    | source                     | packet | opcode          | frame | what                                             |
|--------|----------------------------|--------|-----------------|-------|--------------------------------------------------|
| 308    | cap_newchar_client.log     | 308    | 52869 (0xCE85)  | 622   | S_LOAD_CLIENT_ACCOUNT_SETTING — 614-byte blob     |
| 309    | cap_newchar_client.log     | 309    | 28404 (0x6EF4)  | 8     | S_LOAD_CLIENT_USER_SETTING — EMPTY (`08 00 00 00`) |
| 350    | cap_newchar_client.log     | 350    | 40143 (0x9CCF)  | 999   | C_SAVE_CLIENT_USER_SETTING — the first save        |
| 2297   | cap_newchar_client.log     | 2297   | 40143 (0x9CCF)  | 1010  | C_SAVE_CLIENT_USER_SETTING — a later, bigger save  |
| 100268 | lobby_proxy.log            | 268    | 28404 (0x6EF4)  | 999   | S_LOAD_CLIENT_USER_SETTING — dob's stored blob     |

Two things these five frames pin down:

* **309 is the "nothing stored" answer.** "Test" was created minutes earlier in that same capture,
  so its first login is the only look we get at what the real Arbiter sends a character with no
  saved settings: an 8-byte packet, `[u16 offset=8][u16 count=0]`.
* **350 and 100268 have byte-identical bodies.** The blob the client saves and the blob the server
  replays are the same bytes — the Arbiter stores it verbatim and never parses it. (They are
  different characters on different days; the blob is simply what a client saves once it has drawn
  its default UI, which is also why it works as TeraSharp's default.)

Layouts, decompile references and the reasoning are in `status/CLIENT-SETTINGS.md`.
