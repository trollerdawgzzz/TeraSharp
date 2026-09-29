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


## T191e - the enabled byte is right; the STATE behind it is what differs

`cap_wasd2_client` (caludesucks, id 9, operator, two logins after `/api/reset-client-settings`,
T219c build) clears every other suspect:

| check | result |
|---|---|
| S_ADMIN_GM_SKILL before the tunnelled S_LOAD_TOPO | sent on both entries - 80/81 and 1557/1558 |
| its enabled byte vs World's SDB_USER_VAPORIZED | 00, and World has this GM visible - correct |
| C_SIMPLE_TIP_REPEAT_CHECK | the client sends none (retail sends 22) - nothing to answer |
| S_LOAD_CLIENT_USER_SETTING size | login 1: 8 B (the reset). login 2: 1133 B, far under 9000 |
| served blob vs the last save | byte-identical to the 1133 B C_SAVE at 1247 |

One byte differs from retail, and it is the last one in the frame:

```
  retail  cap_final_gm_client2  99    09 00 BE 64 00 00 00 00 01
  ours    cap_wasd2_client      80    09 00 BE 64 00 00 00 00 00
                                                           ^^  payload+4, frame offset 8
```

Retail's `01` is not a different opinion about the same state, it is a different state: T152
recorded that World vaporizes a GM at spawn at **adminLevel 1** (`cap_final` 495
`SDB_USER_VAPORIZED 01 00 00 00 01`, then 496 carries the `01` to the client) and never at the 5 we
send (`cap_bag`, `cap_skills2`). Retail's GM entered the world vaporized - which is also why the
tool's first toggle at 542 turns invisibility **off** rather than on.

**Landed.** Writing `01` into a frame World disagrees with is the T152 desync, so instead the
Arbiter asks World for the same toggle the panel asks for, once per world entry, anchored on
S_SPAWN_ME (`ArbiterClientHandlers.RequestSpawnVaporize`). World answers with its own
`SDB_USER_VAPORIZED` and its own tunnelled `S_ADMIN_GM_SKILL 01` - the exchange the operator
performs by hand today, which is what clears the prompt. The GM therefore enters the world
invisible, exactly as retail's does; pressing Invisible still turns it off, and the one-shot does
not re-arm until the next world entry. Four T191e tests.


### T191e-b - the spawn request was silent either way

The live run logged nothing at spawn, and a `bool` return could not tell "the block never ran" from
"the request went out and World ignored it". The spawn block now logs one Information line per
operator spawn, whatever happens:

```
S_SPAWN_ME for operator 9: hold False, spawn vaporize Sent
```

`RequestSpawnVaporize` returns `SpawnVaporizeResult`: `Sent`, `AlreadyInvisible` (World already has
them vaporized - asking again would make them visible), `AlreadyAsked` (one per world entry,
re-armed by SDB_USER_ENTERWORLD), `NoWorldLink` (standalone, not in world, or the link is down).
**No line at all on a relog means the tunnelled S_SPAWN_ME is not reaching `DeliverTunnelled`** -
and that is the next thing to chase, not the gates.

One real defect went with it: the one-shot was taken BEFORE the send, so a send that failed burned
the entry's only attempt silently. It is taken after a successful send now.

`T191e_b_a_tunnelled_spawn_me_reaches_the_operator_block_and_says_so` drives the captured 28-byte
S_SPAWN_ME through `DeliverTunnelled` - the delegate `WorldBridge.RegisterPlayer` installs - for an
operator, and asserts the spawn frame, the S_ADMIN_HOLD_CHARACTER that proves the block ran, the log
line, and the unspent one-shot.


### T191e-c - World drops a 0x2827 whose target it cannot look up yet

`arbiter-spawnvap.log` 15:38:10 logged `spawn vaporize Sent` and World answered **nothing**: no
`SDB_USER_VAPORIZED`, no tunnelled `S_ADMIN_GM_SKILL`. `cap_spawnvap_client` 341/342 is S_SPAWN_ME
followed by our own S_ADMIN_HOLD_CHARACTER and then straight into the S_SPAWN_NPC burst; the only
later S_ADMIN_GM_SKILL in the whole capture is the panel's.

`Handler_AS_ADMIN_REQUEST_USERACTION` says why. Every case in its switch - ours is 0x65,
`UserActionGmSkill` - opens with

```c
FUN_140d9bfd0(&user, linkId, targetDbId);   // UserManager lookup
if (user != 0) { ... }                      // and nothing at all when it is null
```

At S_SPAWN_ME the user is not in that manager yet, so the frame is dropped without a word. The
panel's identical 0x2827 seconds later is answered, so the request was right and only the moment was
wrong. Retail never had to time this: its World vaporizes the GM itself during enter-world
(`cap_final` 495 -> 496, adminLevel 1).

**Landed.** S_SPAWN_ME now *arms* the request instead of sending it, and every later tunnelled frame
pumps it: at most `SpawnVaporizeMaxTries` (5) attempts, `SpawnVaporizeRetryGap` (750 ms) apart,
stopping the instant `SDB_USER_VAPORIZED` sets `IsGmInvisible`. The log reads

```
S_SPAWN_ME for operator 9: hold False, spawn vaporize armed
Spawn vaporize for player 9: attempt 1 Sent
Spawn vaporize for player 9: World confirmed after 2 attempt(s)
```

and, if World never answers, one Warning naming the count instead. Two T191e-c tests cover the pump
(retry, bound, confirmation, and never arming a GM World already has vaporized).


## T191f - two lobby fields, and what the settings frames are NOT

`S_GET_USER_LIST` (0x6759) is a walked record list: body `[u16 count][u16 firstOffset]`, each record
`[u16 here][u16 next]`. The brief's "+460" is entry 0 (which starts at 35) plus **425**, and 425 is
`isNewCharacter`:

| capture | count | +425 per entry |
|---|---|---|
| classic_live3 record 11 (retail) | 7 | `01 00 00 00 00 00 00` |
| cap_queue4_client1 record 11 | 2 | `01 01` |
| cap_wasd2_client record 11 | 5 | `01 01 01 01 01` |

We hard-coded `true` for every character on every login. It now follows `characters.entered_world`,
a new column written by `SDB_USER_ENTERWORLD` and backfilled once from `play_seconds > 0` or a
`visited_sections` row.

`S_LOGIN_ARBITER` record 7 differs in exactly one field, packet offset 6 (body +2, u32):

```
classic_live3        17 00 A6 92 01 00 00 00 00 00 ...   status 0
cap_queue4_client1   17 00 A6 92 01 00 1F 00 00 00 ...   status 31
```

T89b read 31 off proxy captures and called 0 "a brand-new account"; the live Classic+ server is the
better witness, so `LoginStatusNormal` is 0 and 33 still opens the tool for an operator (T104's pin
keeps the captured 31 as a capture, not as the constant).

### What the settings frames are not

Two suspects checked and cleared, so the next brief does not re-open them:

* **Position.** Ours is already retail's. `cap_2man_b_client1` 312 C_LOAD_TOPO_FIN -> 322
  S_LOAD_CLIENT_ACCOUNT_SETTING -> 323 S_LOAD_CLIENT_USER_SETTING -> 334 S_SPAWN_ME;
  `cap_wasd2_client` 327 -> 336 -> 337 -> 341. Same pair, same order, same slot.
* **Re-entries.** The pair is sent from the `C_LOAD_TOPO_FIN` handler with no one-shot, so it goes
  out on every world entry AND every re-entry, including the one the Invisible toggle causes.
  Retail is in fact narrower: `cap_2man_b_client1` sends it on entries 1 and 2 (322/323, 2173/2174)
  and on none of the five after that.

Neither is the reason a UI layout does not come back. The remaining difference in that pair is the
ACCOUNT blob - 895 B ours against retail's 663 B - which is T191b's ground, not this one's.


## T191g - there are THREE client blobs, and the third one was never stored

The hotbar survives a relog and a moved window does not, because they are not in the same blob:

| blob | packets | stored? |
|---|---|---|
| per character (hotbar, shortcuts, presets) | C_SAVE_CLIENT_USER_SETTING -> S_LOAD_CLIENT_USER_SETTING | yes (T19) |
| per account (graphics, sound, interface) | C_SAVE_CLIENT_ACCOUNT_SETTING -> S_LOAD_CLIENT_ACCOUNT_SETTING | yes (T191b) |
| **window layout** | **C_SAVE_CLIENT_UI_SETTING (0xA98F) -> S_REPLY_CLIENT_UI_SETTING (0x5FAE)** | **no** |

`OnSaveClientUiSetting` acked the save and dropped it, and `SendUiSetting` handed back a captured
constant. cap_wasd2_client replies 116 B at packets 26, 32, 50 and 463 - identical every time, and
unchanged by the client's own 115-byte save at 33. Retail's reply is the account's own and moves
with it: cap_2man_b_client1 replies 486 B (29, 513, 1894) and saves 461 B (33, 539).

The two shapes differ in one field:

```
C_SAVE_CLIENT_UI_SETTING body   [u16 count][u16 firstOffset][u32 unk]
  record  [u16 here][u16 next][u16 nameOffset][u8  flag][f32 x][f32 y][UTF-16 name + NUL]
S_REPLY_CLIENT_UI_SETTING body  [u16 count][u16 firstOffset]
  record  [u16 here][u16 next][u16 nameOffset][u32 flag][f32 x][f32 y][UTF-16 name + NUL]
```

so a reply is the save plus 3 bytes per record minus the 8-byte preamble - ours 115 + 9 - 8 = 116,
retail 461 + 33 - 8 = 486. Both captures agree. Every offset is frame-relative.

**Landed.** New `ui_settings(account_id, blob, updated_at)` - per account, because the client asks
for it before character select (cap_2man_b_client1 28, cap_wasd2_client 30).
`OnSaveClientUiSetting` stores the save body verbatim once it walks, `SendUiSetting` rebuilds the
reply from it with `ClientSettingsHandlers.BuildUiSettingReply`, and a blob that does not walk
serves the captured default rather than half a layout. Four T191g tests, including the byte-exact
conversion of cap_wasd2_client 33.

The ACCOUNT blob's 895 B against retail's 663 B is not a fault: both are constant across their
captures, ours is account 1's own stored 887 B (`account_settings`, last written 2026-09-28), and
neither client sent a `C_SAVE_CLIENT_ACCOUNT_SETTING` in these sessions. Different accounts, not a
broken round trip.
