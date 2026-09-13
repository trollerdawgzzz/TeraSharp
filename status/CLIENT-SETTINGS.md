# Client settings — what the client saves and how we persist it (T19)

The client serialises its own UI state — keybinds, hotbars, chat tabs, window positions,
inventory sort — and hands it to the Arbiter as an opaque blob. The Arbiter stores it verbatim and
hands it back at the next login. Two scopes, two rows, no parsing.

Before T19 TeraSharp served one captured character's blob to everybody and threw every save away,
so nothing the player changed survived a relog.

---

## 1. The packets

All four are a single `bytes data` field. Body layout: **`[u16 offset][u16 count][count bytes]`**,
and the offset is **packet-relative** — it counts the 4-byte `[u16 length][u16 opcode]` header, so
a blob that starts immediately after the descriptor has offset **8** and its first byte is
`body[4]`. Get that off by four and the client silently discards the settings.

| packet | opcode | direction | scope |
|---|---|---|---|
| `C_SAVE_CLIENT_USER_SETTING` | 40143 / 0x9CCF | C→S | character |
| `S_LOAD_CLIENT_USER_SETTING` | 28404 / 0x6EF4 | S→C | character |
| `C_SAVE_CLIENT_ACCOUNT_SETTING` | (by name) | C→S | account |
| `S_LOAD_CLIENT_ACCOUNT_SETTING` | 52869 / 0xCE85 | S→C | account |

`.def` files in `tera_v100_MASTER_FINAL`: `C_SAVE_CLIENT_ACCOUNT_SETTING.1.def`,
`S_LOAD_CLIENT_USER_SETTING.1.def` and `S_LOAD_CLIENT_ACCOUNT_SETTING.1.def` all say `bytes data`,
which matches the capture exactly.

> **`C_SAVE_CLIENT_USER_SETTING.1.def` is wrong.** It says `int32 value` and `opcode=55077`, while
> the wire shows opcode 40143 and a `bytes` field. It is one of the auto-generated
> "typed+named from binary" defs and it mis-derived this one. Nothing reads it today — the handler
> takes the raw body — but do not wire this packet through the `.def` codec.

There is **no `C_LOAD_CLIENT_USER_SETTING` / `C_LOAD_CLIENT_ACCOUNT_SETTING`**: the client never
asks, the server pushes both unprompted. And there is **no `S_SAVE_*` reply** for either save —
neither capture has one, and the client does not wait. (Contrast `C_SAVE_CLIENT_UI_SETTING`, which
*does* get `S_SAVE_CLIENT_UI_SETTING`; that is a different, smaller packet and is already handled.)

## 2. When the client saves — not just at logout

`cap_newchar_client.log` has `C_SAVE_CLIENT_USER_SETTING` three times in ~4 minutes of play:

| packet | blob | right after |
|---|---|---|
| 350 | 991 B | first spawn |
| 2293 | 999 B | an inventory change |
| 2297 | 1002 B | achievement progress |

So the client saves whenever its UI state settles, at arbitrary moments, and the blob grows. The
store has to be an upsert and has to tolerate a save at any time — including mid-session.

`C_SAVE_CLIENT_ACCOUNT_SETTING` appears in **neither** capture: the account blob only changes when
the player edits account-scope options (graphics, sound, interface).

## 3. When the server sends

`S_LOAD_CLIENT_ACCOUNT_SETTING` goes out **twice**, `S_LOAD_CLIENT_USER_SETTING` only once:

1. at the lobby, right after `S_GET_USER_LIST` — account only
   (cap 12, 39, 3599; lobby_proxy 12, 709)
2. in the post-spawn burst, account first and user immediately after
   (cap 306-309; lobby_proxy 265-268 and 920-923):

```
S_FRIEND_GROUP_LIST
S_FRIEND_LIST
S_UPDATE_FRIEND_INFO
S_LOAD_CLIENT_ACCOUNT_SETTING
S_LOAD_CLIENT_USER_SETTING
```

TeraSharp already sends the user one at exactly that point, from `HandlerRegistry`'s
`C_LOAD_TOPO_FIN` in-world branch, and **T19 does not move it** — that placement is what gets the
chat window processing `S_CHAT` and it is live-verified. The account one is simply missing.

## 4. What the real Arbiter does

**Save** — `Handler_C_SAVE_CLIENT_USER_SETTING` (`Arb_part_041.c:11024`) and
`Handler_C_SAVE_CLIENT_ACCOUNT_SETTING` (`:10300`). Both require a packet of at least 8 bytes, take
`param_2` as the **packet** start, and read `param_2[2]` (offset) / `param_2[3]` (count). If the
offset is non-zero and inside the packet they pass `packetStart + offset` and the count to the
store; otherwise they pass NULL.

`User::SaveClientSetting(const unsigned char *, int)` (`Arb_part_029.c:18757`) and
`Account::SaveClientSetting` (`Arb_part_065.c:9779`) are the same function twice:

```c
if (param_3 == 0 || 9000 < param_3) { log_error(...); return; }   // dropped, nothing written
... spSaveClientSettingForUser / ForAccount (playerId|accountId, blob, len) ...
if (ok) { memcpy(user + 0x6290, blob, len); *(int*)(user + 0x85bc) = len; }
```

So: **blob of 0 bytes or more than 9000 is refused and whatever was stored stays stored.** Nothing
is parsed or validated beyond the length.

**Load** — `User::SendClientSetting(void)` (`Arb_part_029.c:19563`) writes opcode `0x6EF4`,
backpatches `[offset][count]`, appends the stored bytes — and does so **unconditionally**. A
character with nothing stored therefore gets an 8-byte packet with body `08 00 00 00`. That is
literally `cap_newchar_client.log` packet 309: "Test", created minutes earlier, first login.
`Account::SendClientSetting` (`Arb_part_065.c:10328`) is the same; it only logs a warning when the
stored length is zero and then sends anyway.

## 5. What T19 implements

**`Persistence/CharacterStore.cs`**

```sql
CREATE TABLE client_settings  (character_id INTEGER PRIMARY KEY, blob BLOB NOT NULL, updated_at TEXT);
CREATE TABLE account_settings (account_id   INTEGER PRIMARY KEY, blob BLOB NOT NULL, updated_at TEXT);
```

with `SaveClientSetting` / `LoadClientSetting` / `SaveAccountSetting` / `LoadAccountSetting`. Both
saves are upserts and both enforce the real Arbiter's rule: 0 or >`MaxClientSettingBytes` (9000) is
refused, returns false, and leaves the stored row alone.

**`Handlers/ClientSettingsHandlers.cs`** — `ParseSettingBlob` (packet-relative offset, bounds
checked), `BuildSettingBody`, `EmptySettingBody`, `OnSaveUserSetting`, `OnSaveAccountSetting`,
`SendUserSetting` (stored else default), `SendAccountSetting` (stored else empty).

### The one thing left as a judgement call: the default

For a character with nothing stored, the real Arbiter sends the **empty** form. TeraSharp instead
serves the captured 995-byte body it has always served, because `HandlerRegistry`'s comment says
this packet is what gets the chat window processing `S_CHAT`, and that is live-verified behaviour
that nobody should disturb on a hunch. The two are one line apart:

```csharp
public static byte[] DefaultUserSettingBody() => (byte[])UserSetting.Clone();  // today
public static byte[] DefaultUserSettingBody() => EmptySettingBody();           // parity with the real server
```

Worth testing live at some point — if chat still works with the empty form, take it, because the
captured default is one real player's keybinds being handed to every new character. The **account**
default is already the empty form: there was no incumbent default to preserve, and the only account
blob in the captures is the human's own graphics/sound/interface options — handing those to every
new account is the same mistake as the shared starter blob in T18.

## 6. HandlerRegistry — the exact diff (human-owned file, not edited here)

Two of these three are **required**, or nothing is ever saved.

**(a) Register the user save.** It is not registered at all today, and the dispatcher forwards
unregistered opcodes to WorldServer when the session is in-world (`PacketDispatcher.Dispatch`).
The client saves *while in world*, so right now every `C_SAVE_CLIENT_USER_SETTING` is being leaked
to World, which answers it with `handler has not been implemented yet!!!` — the exact leak
`CLAUDE.md`'s gotcha list warns about. Add, next to the other client-settings registrations:

```diff
     Reg("C_REQUEST_CLIENT_CHAT_OPTION_SETTING", 0, ClientSettingsHandlers.OnRequestChatOption);
     Reg("C_REQUEST_CLIENT_UI_SETTING", 0, ClientSettingsHandlers.OnRequestUiSetting);
     Reg("C_SAVE_CLIENT_CHAT_OPTION_SETTING", 0, ClientSettingsHandlers.OnSaveChatOption);
+    Reg("C_SAVE_CLIENT_USER_SETTING", 0, ClientSettingsHandlers.OnSaveUserSetting);
     RegEmptyReply("C_SAVE_CLIENT_UI_SETTING", "S_SAVE_CLIENT_UI_SETTING", new Dictionary<string, object> { ["result"] = (byte)1 });
```

**(b) Make the account save real** instead of a no-op:

```diff
     RegNoop("C_SET_VISIBLE_RANGE");
     RegNoop("C_HARDWARE_INFO");
-    RegNoop("C_SAVE_CLIENT_ACCOUNT_SETTING");
     RegNoop("C_CHANGE_USER_LOBBY_SLOT_ID");
```

and add it beside (a):

```diff
     Reg("C_SAVE_CLIENT_USER_SETTING", 0, ClientSettingsHandlers.OnSaveUserSetting);
+    Reg("C_SAVE_CLIENT_ACCOUNT_SETTING", 0, ClientSettingsHandlers.OnSaveAccountSetting);
```

**(c) Send the account blob back** — without this the account half is write-only. The real order
is account first, then user, in the same burst:

```diff
                 SocialHandlers.SendBlockList(s);
                 SocialHandlers.SendFriendGroupList(s);
                 SocialHandlers.SendFriendList(s);
+                ClientSettingsHandlers.SendAccountSetting(s);
                 ClientSettingsHandlers.SendUserSetting(s);
```

(c) is the only one that changes what goes out on the wire. It adds a packet the real server sends
at exactly that point in every capture, and with nothing stored it is the 8-byte empty form — but
it is still a live-path change, so it is yours to take or leave. (a) and (b) only stop packets
being dropped or leaked.

The real Arbiter also sends `S_LOAD_CLIENT_ACCOUNT_SETTING` at the **lobby**, right after
`S_GET_USER_LIST` (cap 12/39/3599). That would be a line in `LoginHandlers.OnGetUserList`, also
human-owned; it is not needed for settings to persist and is left out on purpose.

## 7. Tests

`src/TeraSharp.Arbiter.Tests`, against `data/cap_client_settings.bin`:

| test | what it pins |
|---|---|
| `ClientSettings_capture_frames_have_the_opcodes_and_shape_we_think` | all five frames: length field, opcode, offset 8, count = rest |
| `ClientSettings_parses_the_captured_save_packet` | 991 and 1002-byte blobs out of cap 350 / 2297, starting at body[4] |
| `ClientSettings_the_save_and_the_load_carry_the_same_bytes` | C_SAVE body ≡ S_LOAD body — the whole contract |
| `ClientSettings_S_LOAD_is_byte_exact_against_the_capture` | re-framing a stored blob reproduces lobby_proxy 268 exactly |
| `ClientSettings_default_body_is_the_captured_one_and_frames_byte_exact` | the incumbent default still frames to the captured packet, and is copied not shared |
| `ClientSettings_empty_form_matches_the_capture` | `08 00 00 00` ≡ cap 309, for null, empty and the account default |
| `ClientSettings_round_trip_through_the_store` | save → load → rebuild, second save replaces the first, other characters untouched |
| `ClientSettings_account_scope_is_a_separate_row` | 614-byte account blob round-trips; the two scopes do not interfere |
| `ClientSettings_store_refuses_what_the_real_Arbiter_refuses` | 9000 accepted, 0 and 9001 refused, refusal leaves the row alone |
| `ClientSettings_malformed_save_parses_to_empty_instead_of_throwing` | truncated / zero / out-of-range descriptors, and a 1-byte blob still survives |

## 8. Open

- `SendAccountSetting` is dead code until diff (c) lands.
- Settings are keyed on `SelectedCharacter.Id`, falling back to `PlayerId`. A save that arrives
  before a character is selected is dropped rather than written under a guessed id; the real
  Arbiter falls back to the session's lobby user (`FUN_1408876c0`), which TeraSharp has no
  equivalent of. Not reachable in either capture.
- Whether the empty default breaks chat rendering — see §5. One live test settles it.
