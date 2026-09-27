# T191c - why the WASD prompt survived T191b

Short version: no settings byte differs. TeraSharp pushed `S_LOAD_CLIENT_USER_SETTING` twice per
world entry; the client merges that list instead of replacing it, so its own shortcut list doubled
every entry until the blob passed the 9000-byte save cap - after which **nothing** the client
stored could be written any more, tutorial/first-run state included.

## 1. The chain

| Step | Evidence |
|---|---|
|Retail loads the per-character blob **once** per entry, after `C_LOAD_TOPO_FIN`|`cap_2man_b_client1` 312 fin -> 322 `S_LOAD_CLIENT_ACCOUNT_SETTING`, 323 `S_LOAD_CLIENT_USER_SETTING`, and nowhere else|
|TeraSharp also pushed one at character select|`cap_bg2_client1` 37 `S_SELECT_USER` -> **51** `S_LOAD_CLIENT_USER_SETTING` (before `S_LOGIN` 54), then **342** after fin 332. The two frames are byte-identical|
|The client merges, it does not replace|`S1ShortCutController` holds 131072 records, all copies of just two: `(9999, 65000001, slot 10, 0)` x65536 and `(9999, 65000002, slot 18, 0)` x65536. The same two constants appear on **both** characters and in every capture, so they are not per-character item db ids (TeraSharp allocates those from 1000 upward, `CharacterStore.FirstItemId`) and they appear in no packet in any capture|
|It doubles per world entry|saves in `cap_bg2_client1`: 3554 = 2x131072, 4862 = 3x, 5575 = 4x, while every load still serves the stored 131072|
|Past 9000 bytes every save is refused|`arbiter-bg2.log` 20:01:33 / 20:03:17 / 20:04:02 `SaveClientSetting: refusing a 10094/14557/19004-byte blob for character 9`|
|So the served blob is frozen|`S_LOAD_CLIENT_USER_SETTING` is 5657 B at every login in `cap_bg2_client1`; retail's is 1189 B|

Growth timeline (`S1ShortCutController` field-2 records at login):

| Capture | records |
|---|---:|
|`cap_play1_client` (TeraSharp, 09-22)|0|
|`cap_queue5_client1`, `cap_handoff1_client`|0|
|`cap_polish_client` (both characters)|1024|
|`cap_instance1_client1`|1024 -> 2048 on save|
|`cap_bg2_client1`|131072|
|`cap_2man_b_client1` (retail)|0|

`S_SIMPLE_TIP_REPEAT_CHECK` is byte-identical in both captures (ids 1, 2, 0x23, 0x27, 0x29, all
answered `01`), and the account-scope blobs differ only in the size of the client's own zlib
`S1UI_GFxManager` value (TeraSharp `USIV`/`USPP`, retail those plus `USPB`/`USWH`/`USTD`/`USWM`/`USPN`
- UI scale entries, not tutorial flags). Neither is the cause.

## 2. The 9000-byte cap is retail; it cannot be raised

Both save paths open with the same bound, so a bigger blob is dropped by the real Arbiter too:

| Function | Decompile | Code |
|---|---|---|
|`User::SaveClientSetting(const unsigned char *, int)`|`Arb_part_029.c:18757`|rejects a zero length and any length above 9000, logging the refusal|
|`Account::SaveClientSetting(const unsigned char *, int)`|`Arb_part_065.c:9793`|same test|

It is the physical buffer size, not a policy number: on success the user path copies the blob into
a fixed field of the user record and stores the length in the field 9004 bytes further on; the account path
copies into a pointer-indexed field (0x7a8 bytes) and stores the length
at byte 0x2ad4, again 9004 apart. The refusal branch is a log call and no reply, which is what
TeraSharp already does. So `CharacterStore.MaxClientSettingBytes` stays 9000: raising it would
store blobs the real Arbiter rejects, and with the duplicate send gone the blob stays near a
kilobyte, as it did in `cap_play1_client` and as retail's does.

## 3. Changes

| File | Change |
|---|---|
|`status/T191c-PATCH.diff`|`Handlers/WorldEntry.cs` (human-owned): drop `ClientSettingsHandlers.SendUserSetting(s)` at character select. The UI/chat pushes beside it stay - they carry no growing list, and the client re-requests both anyway (`cap_bg2_client1` 408/409 -> 410/411; retail 511/512 -> 513/515)|
|`Persistence/CharacterStore.cs`|`ClearClientSetting(characterId)`|
|`Web/AdminApi.cs`|`POST /api/reset-client-settings {"id":N}`|
|`Tests/T191c.cs`, `data/t191c/frames.json`|the two loads from each capture; asserts retail has 0 records, ours 131072 in two kinds, the three refused sizes are over the cap, and that clear-then-save works|

`HandlerRegistry`'s pair on `C_LOAD_TOPO_FIN` is the retail-shaped send and is left alone.

## 4. Recovery for the characters already poisoned

The stored blob is what keeps the client's list huge, so it has to go once per affected character:

```
POST /api/reset-client-settings {"id":9}      # or: DELETE FROM client_settings WHERE character_id=9;
```

Hotkeys and UI layout for that character go back to defaults; the client writes a fresh small blob
on the next login. Characters 9 and 10 in the live DB need this.

## 5. Not established

The **seed** - what created the first one or two of those records - is not in any capture: the
count was already 1024 when `cap_polish_client` starts, and the session that created them
(between `cap_handoff1`, 0 records, and `cap_polish`) was not captured. `9999` and
`65000001`/`65000002` are `OURS:` unidentified; they are client-side constants, not wire values.
Doubling needs an existing record, so with the duplicate send gone a character that starts from a
cleared blob should not grow at all - that is what the live check has to confirm.

No build or test run was performed by the assistant; no commit, merge or deployment.

Live check: clear the blob for one character, log in, play, relog - the WASD prompt should not
return, `S_LOAD_CLIENT_USER_SETTING` should appear once per entry, and the Arbiter log should show
no `SaveClientSetting: refusing` lines. Watch the chat window's channel tabs after the first entry
(the reason the UI/chat pushes were added).

## T191d - the WASD guide is the GM-skill switch, not tutorial state

The prompt is operator-only. `S_LOGIN_ARBITER.status` is `0x21` (33) in `cap_wasd_client` and
`0x1F` (31) in `cap_2man_b_client1/2`; the ordinary characters never show it.

Everything the brief listed was checked first and matches retail byte for byte:

| Checked | Ours | Retail | Verdict |
|---|---|---|---|
| `S_SIMPLE_TIP_REPEAT_CHECK` tips 1, 2, 0x23, 0x27, 0x29 | `[tipId][01]` (`cap_wasd_client` 1260-1299) | `[tipId][01]` (`classic_live` 5669-5699) | identical, same ids, same order |
| `S_LOAD_CLIENT_USER_SETTING` | save at 1423 served at 1812; save at 2774 served at 3271 (same sha) | - | T191c round-trip is correct |
| `S_LOGIN` | `visible` 1, `isSecondCharacter` 0 | same | `S_LOGIN.14.def` has no tutorial or first-login field |
| `S_USER_STATUS` | 17 B, status 0 | 17 B, status 0 | identical |
| `AS_ENTER_WORLD` `TutorialUser` (payload 115) | 0 | - | `Handlers/WorldEntry.cs:236` |
| `C_DONT_REPEAT_TUTORIAL_SIMPLE_TIP` | never sent by the client | - | nothing to persist |

**The byte:** `S_ADMIN_GM_SKILL` (0x64BE) `[i32 skill=0 Invisible][u8 enabled]`, the `enabled` byte.
The real Arbiter sends this frame once per world entry, between `S_FESTIVAL_LIST` and
`S_LOAD_TOPO` - `cap_final_gm_client2` 98/99/100 and 2444/2445/2446, both `09 00 BE 64 00 00 00 00
01`. Its 542 -> 546 exchange is the panel's own toggle, not a second push. `cap_wasd_client` has
three world entries as an operator and **no `S_ADMIN_GM_SKILL` in 4304 frames**, so the client's
GM-skill switch is never initialised and the movement guide stays up every login. Pressing the
panel's Invisible toggle produces the first one the client ever receives, which is why it clears
the prompt at once.

T152 had removed the push because the VALUE was a guess: it told the client "invisible" while
World had the GM visible, so the panel's OFF vaporized them. The frame is back with World's own
value - `enabled` is `IsGmInvisible`, which starts visible at `SDB_USER_ENTERWORLD` and only moves
on World's `SDB_USER_VAPORIZED` (0x282D) - so it cannot desync, and when World does vaporize
(`cap_final` 495 -> 496) it carries `01` exactly as retail's frame 99 does.

Injected in `ArbiterClientHandlers.DeliverTunnelled`, immediately before the tunnelled
`S_LOAD_TOPO`, gated by the existing `TryTakeGmSkillPush` one-shot so a zone change does not
re-arm GM skill and a relog does. Tests: `T191d_the_gm_skill_switch_matches_the_captured_frames`,
`T191d_an_operator_gets_the_switch_before_the_tunnelled_topo` (fails without the injection).

Live check: log in as the operator - the guide should not appear, and the panel's Invisible toggle
should now start from the state World actually has.
