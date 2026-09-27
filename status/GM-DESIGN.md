# GM commands — how `/@` really works — T32

The `/@name args` channel, from the decompile: where a command enters, who decides whether the
Arbiter or the World runs it, the admin gate, and the reply path. Command lists:
`status/GM-COMMANDS-ARBITER.md` (192 the Arbiter owns) and `status/GM-COMMANDS-FULL.md`
(416 the World owns) — 608 together.

**No build was possible in the Cowork container.** The forward frame is reproduced from the
writer instruction by instruction and checked in Python; there is no capture of a GM command.

---

## 1. Entry path — the `/@` never reaches the server

**The client strips it.** Proven by exhaustion in ArbiterServer.exe:

* `Handler_C_CHAT` (`FUN_1404dca20`, Arb_part_040.c:19858) does **no** command detection — it
  checks mute/ban and the channel and hands the text to normal chat delivery
  (`Arb_part_040.c:19899`).
* There is no `L'/'` or `L'@'` comparison anywhere in the 96 decompile files. The `0x2f`/`0x40`
  comparisons that do exist are base64 and packet-validator code.
* `CommandDistributor::ProcessCommand` copies the string from index 0 at full length
  (Arb_part_007.c:3237-3244) — no prefix is skipped.
* Registered names are bare (`L"set_admin_level"`), so a token still carrying `/@` could never
  match.

What arrives is one of two packets, each carrying a single wide string `command`:

| packet | opcode | CommandType passed to the distributor |
|---|---|---|
| `C_ADMIN` | `0xA45C` | **1** (Arb_part_040.c:17233) |
| `C_OP_COMMAND` | `0xE394` | **0** (Arb_part_041.c:4878) |

Both handlers are three lines: resolve the `User`, read the string at the u16 offset, call
`ArbiterCommandDistributor::ProcessCommand(user, type, text, false)`.

The line is then split on whitespace by a `std::basic_stringstream` (Arb_part_007.c:3300-3325) —
**no quoting, no escapes**. `TeraSharp`'s parser adds double-quoted arguments on top and tolerates
a leading `/@`, `/` or `@` so a raw test client can send either form; everything else matches.

> The client only *enables* the `/@` channel for a QA account. The human's catalogue records the
> trigger as `S_LOGIN_ARBITER` returning **status 31 or 33** — TeraSharp currently sends
> `status = 0`, so see §6.

## 2. Mine vs World's

Two mechanisms, and they are not "unknown means forward":

**Registered World commands.** WorldServer tells the Arbiter its command list at runtime:
`Handler_SA_REGISTER_WORLD_COMMAND` (opcode **0x144C**, Arb_part_062.c:12062) parses 104-byte
entries and calls `ArbiterCommandDistributor::RegisterWorldCommands` (Arb_part_085.c:16540),
which registers every name with the **same** stub function pointer
(`ArbiterBypassCommandHandler::NullHandler`, an empty body at Arb_part_067.c:11608). The bucket,
not the pointer, does the work: buckets 4 and 1 are `ArbiterBypassCommandHandler` instances
(Arb_part_033.c:13685/13691), whose ctor argument lands at `this+0x24` — 1 for bucket 4, 0 for
bucket 1.

**`ArbiterBypassCommandHandler::HandleCommand`** (Arb_part_067.c:6707) rebuilds the line (name,
then each argument prefixed with a single space, `:6759`) and sends it one of two ways:

* **no User** (only allowed for the mode-1 bucket): `AS_ADMIN` **0x1473** to *every* connected
  World — `[u32 stringOffset][wchar_t* line]`.
* **with a User**: opcode **0x2829** to that user's World only.

**Truly unknown commands are not forwarded at all.** `ArbiterCommandDistributor::OnUnregisteredCommand`
(the `vtbl+0x10` slot, Arb_part_085.c:12894) logs `Invalid Command`, sends the client
`S_SYSTEM_MESSAGE_CUSTOM` with the literal **`Invalid QA Command\n`** (Arb_part_085.c:12916-12923),
then ships near-matches as `S_COMMAND_HELP` (0x4F79). TeraSharp sends the first, not the help list.

### The forward frame — 0x2829, byte for byte

```c
FUN_140350eb0(&pkt,0x2829);                 // [u32 frameLen][u16 opcode]
slot = bufferBase + frameLen;               // remember where the offset u32 lands
*slot = 0;  FUN_14013d0b0(&pkt,*slot);      // u32 string offset, reserved as 0
FUN_14013d0b0(&pkt,(int)playerId);          // u32 userId   (User+0x120 = playerId)
FUN_14013d0b0(&pkt,(int)bypassMode);        // u32 bypass mode (1 = bucket 4)
*slot = frameLen;                           // backpatch: 18, frame-relative
FUN_140351030(&pkt,line);                   // wcscpy_s -> UTF-16LE + a u16 0
```

So the payload (frame minus its 6-byte header) is

```
[0]  u32 stringOffset = 18   (frame-relative, like every offset on the A<->W protocol)
[4]  u32 userId
[8]  u32 bypassMode
[12] the rebuilt line, UTF-16LE, null-terminated
```

`GmCommandHandlers.BuildWorldForward` writes exactly that, and the test pins the bytes for
`abnormality 4000`. Opcode 0x2829 has **no symbolic name** — it is absent from the opcode→name
table in Arb_part_003.c (as are its siblings 0x2823/0x2825/0x2827).

**What World echoes back: nothing, through us.** There is no Arbiter handler that receives command
*output* from World and relays it. Since the forward carries the userId and World holds its own
client link, World answers the client directly. (Not provable from this binary — the World
decompile would settle it — but no relay path exists on our side to implement.)

## 3. The admin gate

The level lives at `User+0x3b98`, is loaded at login from the DB column **`AdminLevel`**
(Arb_part_011.c:10446) and written back by `User::UpdateAdminLevel` (Arb_part_030.c:9742) through
**`dbo.spUpdateUserAdminLevel`**.

* **Threshold: `>= 1`, uniformly.** Every gate is `if (level < 1)` — the dedicated GM packets
  (`Arb_part_040.c:17955/18066/18181`) and the chat path (`Arb_part_085.c:14812`). **There is no
  per-command level**, which is why the human's catalogues are risk-tiered A/B/C by hand rather
  than by a number in the binary.
* **The top level the binary itself ever assigns is 5**: `set_go on` writes 5 into `User+0x3b98`
  (`ArbiterQACommandHandler::SetGameOperator`, Arb_part_044.c:3653). That is what
  `GmAccounts.GmAdminLevel` uses.
* **A refused command answers NOTHING.** The failure path calls
  `LogWrapperEx::LogAbuseCommandUser` (Arb_part_054.c:15496) — a server-side abuse log — and
  returns. No packet. TeraSharp does the same, and logs a warning.
* Worth knowing: as decompiled, the *chat* gate is short-circuited by a dev flag
  (`distributor+0x74`, constructed as 1 at Arb_part_033.c:13661), so on this build a `User` is
  enough for a text command. The binary GM packets ignore that flag. TeraSharp does **not** copy
  the short-circuit — it always gates.

**TeraSharp's gate:** `TERASHARP_GM_ACCOUNTS` (comma/semicolon/space separated account names)
grants level 5; everyone else gets `accounts.admin_level`, which `set_admin_level` writes. The
allow-list is the bootstrap — without it nobody could ever run `set_admin_level` the first time.

## 4. The reply path

**`S_SYSTEM_MESSAGE_CUSTOM` (0x994C)** — one wide string, `string formatted` in the def, always a
**plain literal**, never an `@id`. Two writers: a fixed-string one (`FUN_1400eff60`,
Arb_part_007.c:9253) and the printf-style helper every command uses (`FUN_1404d3c60`,
Arb_part_040.c:13453 — `vswprintf_s` into a 2048-wchar buffer, then send to the client session).

`S_SYSTEM_MESSAGE` (0xF30E, the `@id` one) is NOT the GM channel; it carries quest and petition
messages. `S_ADMIN_NOTICE` (0xA306) is the yellow/orange broadcast banner
(colour constants `0xffff00` / `0xff9900`, Arb_part_071.c:4710-4735), not command output.

## 5. The six commands implemented

| command | real behaviour (decompile) | TeraSharp |
|---|---|---|
| `set_admin_level [char] [level]` | 2 args; find the CHARACTER by name; `UpdateAdminLevel` → `spUpdateUserAdminLevel`. Bad args or unknown name: **silent no-op**. No reply on success either. | same rules; the level is stored on that character's **account** row so a login can read it before a character is picked; we add a confirmation line |
| `create_user [username]` | 0 args → `create_user [username]`; existing name → `User[%s] already exists!`; else create → `Create a new user[%s]!` / `Cannot create a new user[%s]!` | same four literals, verbatim; creates a real character on the caller's account through the same `BuildRecord` + `StarterBlob` path as C_CREATE_USER (race 0 / gender 0 / class 1) |
| `testitem` | **empty stub** — the whole body is the scope tracer (Arb_part_044.c:6765) | kept as a no-op; says so instead of inventing an item grant (items are World's during play) |
| `clear_inven` | ignores its arguments, queues an async job on the CALLER (Arb_part_040.c:8714) | TeraSharp has no Arbiter-side inventory (it lives in the world blob World owns), so it reports that rather than pretending |
| `warehousegold_max [amount]` | exactly 1 arg, int64, negatives clamped to 0, into a process global. No DB, no World notify, no reply | same, in `GmCommandHandlers.WarehouseGoldMax` |
| `query_point` | `Account::RequestUpdateCoin`; **only the failure branch exists** — `Can't request coin` | we have no billing service, so that failure is exactly our case: the same literal |

Anything else in the Arbiter catalogue answers "not implemented yet"; anything in the World
catalogue is forwarded; anything in neither gets `Invalid QA Command`.

## 6. What the human has to wire

`Handlers/HandlerRegistry.cs` (human-owned):

```csharp
// --- GM commands (Arbiter-owned; status/GM-DESIGN.md) ---
var gm = new GmCommandHandlers(loggerFactory.CreateLogger<GmCommandHandlers>());
Reg("C_ADMIN", 4, gm.OnAdminCommand);
Reg("C_OP_COMMAND", 4, gm.OnOpCommand);
```

And, to make the client offer the channel at all, `Handlers/LoginHandlers.cs` has to send a QA
status for a GM account instead of the constant 0:

```csharp
["status"] = GmAccounts.IsListed(s.Account.Name) ? 31u : 0u,
```

31/33 is from the human's own catalogue (`gm_commands_full.md`: "Requires QA status — granted when
S_LOGIN_ARBITER returns status 31 or 33"); it is a client-side rule, so it is not visible in the
Arbiter decompile and has not been tested live. If the client still refuses the channel, 33 is the
other value to try.

`TERASHARP_GM_ACCOUNTS=1` (or whatever account name you log in with) is what turns any of this on.

---

## 7. The forwarded frame, verified (T46)

Before trying `/@teleport` and the rest of the 416 World commands, the frame `GmCommandHandlers.
BuildWorldForward` builds was checked against the decompile. **It is byte-exact**; only two names
were wrong.

**0x2829 is `AS_ADMIN_COMMAND`**, not `AS_BYPASS_COMMAND` — that is the name in World's own opcode
table (`WorldServer.exe.c:246833`). T32 named it after the Arbiter-side handler class that builds
it. `GmCommandHandlers.AS_BYPASS_COMMAND` is kept as an alias.

**Layout**, from the Arbiter's PDL dumper `FUN_14017bdf0` (`Arb_part_011.c:5334`), which names the
fields and their frame offsets, with a `0x11 < len` guard:

| frame | payload | type | name |
|---|---|---|---|
| 6 | 0 | u32 | `Command` — string offset, frame-relative, backpatched to 18 |
| 10 | 4 | u32 | `UserDbId` — from `User+0x120` |
| 0x0E | 8 | u32 | **`CommandType`** |
| 0x12 | 12 | wchar[] | the command line, UTF-16LE, NUL-terminated |

The third field is `CommandType`, not a "bypass mode", but T32's **value** is right and for the
right reason: the writer fills it from the *handler object's* own constant
at `this+0x24` (`Arb_part_067.c:6898`), not from the client packet.
`ArbiterBypassCommandHandler` is constructed twice, with 1 and 0
(`Arb_part_033.c:13685`/`:13691`), and World commands live in the bucket that carries **1**. So
`ForwardToWorld` ignoring its own `commandType` argument is correct, not an oversight.

Cross-checked against the second writer (`Arb_part_040.c:8820`, the `clear_recipe_world` path),
which emits the same four fields in the same order.

**World's side.** `Handler_AS_ADMIN_COMMAND` (`WorldServer.exe.c:2977905`):

1. drops the frame — and logs `Arbiter <-> World PDL Version Mismatch! Bye :(` — when the frame is
   under `0x12`. Our shortest possible frame is 20 bytes (an empty line), so this can never fire;
   `T46_admin_command_always_clears_the_world_handler_guard` pins it.
2. looks the user up by the u32 at frame 10. **No user, no command, silently** — so the `UserDbId`
   has to be the character id World knows, which is what `ForwardToWorld` sends.
3. bounds-checks the string ref at frame 6 against the frame length.
4. reads `CommandType` at frame `0x0E` and dispatches.

It performs **no admin-level check of its own**, and World never tests the `AdminLevel` it stores
from AS_ENTER_WORLD either (`status/ENTER-WORLD-FALLBACK.md` §5a). So nothing on the World side
gates `/@`; what still has to be right for a live test is the client offering the `/@` channel at
all — §6, `S_LOGIN_ARBITER.status`.

---

## 8. Forward by default (T47)

`/@teleport warriortwo` and bare `/@teleport` both came back **Unknown** in the live test, with
`teleport` sitting in `GM-COMMANDS-FULL.md` line 384 the whole time. Two causes, both in
`GmCommandHandlers.Classify`:

1. **`GmCommandCatalog` reads the catalogue off disk and leaves both sets EMPTY when it cannot
   find the files** — which is the normal case for a deployed binary with no `status/` folder
   beside it. Every command then fell past both `IsWorldCommand` and `IsArbiterCommand` to
   `Unknown`.
2. `GM-COMMANDS-FULL.md` is **all 608 names, the Arbiter's included**, and the World check came
   first — so an Arbiter-owned command that we had not implemented would have been forwarded to
   World instead of answered.

New order, and the forward is now the default:

```
Implemented        -> Local
IsArbiterCommand   -> NotImplemented
otherwise          -> ForwardToWorld
```

**This diverges from the original on purpose.** The real Arbiter has both tables compiled in, so
a name in neither is a typo and `ArbiterCommandDistributor::OnUnregisteredCommand`
(`Arb_part_085.c:12895`) answers "Invalid QA Command" plus `S_COMMAND_HELP` with near matches. We
only know the names when the markdown is on disk, so refusing locally meant refusing every real
World command too. Forwarding costs one wasted frame on a typo — World rejects it — and buys the
416 World commands. `GmDispatch.Unknown` is no longer produced by `Classify`; the enum value stays
so its tests remain meaningful.

The frame itself was verified byte-exact in T46 (§7), and World's `Handler_AS_ADMIN_COMMAND` does
no admin check of its own, so nothing else stands between this and a live `/@teleport`.

### `LevelOf`

`GmCommandHandlers.LevelOf` returned 0 at enter-world for an account in `TERASHARP_GM_ACCOUNTS`,
so World logged `AdminLevel[0]` (T46 wired payload 111 to it). It now:

* takes the **max** of the stored `accounts.admin_level` and what the allow-list grants, rather
  than letting the list override — being listed must not demote an account stored above 5; and
* matches the list against the **character** name as well as the account name. The variable is
  called GM_ACCOUNTS, but the name a person has to hand is the one on the character-select
  screen, and putting that in the list is the obvious thing to do.

If it still logs 0 after this, the value in the env matches neither name — that is the thing to
check first.

## 9. The chat wiring (T47)

`ChatManager` (T43) had the whole whisper rule and was wired to nothing. `SocialHandlers.OnWhisper`
now hands `C_WHISPER` straight to it and dispatches the result through `ActionDispatcher`, so
recipients resolve via `WorldBridge.SessionForPlayerId`.

The old body looked the target up in `SocialHandlers.Sessions`, a map whose only writer —
`RegisterSession` — **was never called**. It was always empty, so every whisper answered
"offline". The same empty map silently killed every cross-session friend push in that file
(the online flag in `S_FRIEND_LIST`, and the request / accept / delete notifications);
`SessionForCharacter` now asks the bridge first and those start working too.

Two lines are still the human's, and they are what make registration exact rather than
self-healing:

```csharp
// Handlers/WorldEntry.cs, in EnterWorld, after the session is registered with the bridge:
SocialHandlers.RegisterChat(s);

// Network/GameSession.cs, in Close() (or LeaveWorld), before the socket goes:
SocialHandlers.UnregisterChat(this);
```

Until they are added, `OnWhisper` calls `SocialHandlers.SyncChatRoster()` on every whisper, which
registers everyone `WorldBridge.InWorldSessions()` reports. That is a dictionary write per session
per whisper — correct, just wasteful — and it is why whisper works without the diff.
`UnregisterChat` also dispatches the private-channel leave announcements `ChatManager.Unregister`
returns; without the line, a character who logs out stays in their channels until the server
restarts.

## T104 - why Alt+A did not open, and the one lobby value that decides it

Diffing the lobby of `cap_final_gm_client2.log` - the one captured session where the In-Game
Operation Tool opened - against `cap_final_client2.log`, `cap_final_client.log` and
`cap_final_gm_client.log`, frame by frame over frames **3..49**:

| frame | packet | GM session | the other three |
|---|---|---|---|
| 7 | `S_LOGIN_ARBITER` | 23 B, `status` = **0x21 (33)** | 23 B, `status` = **0x1F (31)** |
| 8 | `S_LOGIN_ACCOUNT_INFO` | 544 B | 544 B - differs only in the account id and the random session strings |
| 11 | `S_GET_USER_LIST` | 1181 B | 1181-1183 B, no per-character admin flag (the only byte that separates the lists sits inside `restBonusXp`) |
| 16-25 | `S_UPDATE_CONTENTS_ON_OFF` x10 | contents 2,3,4,8,9,22,23,20,21,34 | identical, same order, same on/off bytes |
| 13, 14, 15, 48, 49 | `S_DECO_UI_INFO`, `S_ACCOUNT_PACKAGE_LIST`, `S_CONFIRM_INVITE_CODE_BUTTON`, `S_BROCAST_GUILD_FLAG`, `S_CURRENT_ELECTION_STATE` | identical | identical |

**One u32 separates them:** `S_LOGIN_ARBITER` body +2 (packet offset 6). Note that
`cap_final_gm_client.log` is a GM ACCOUNT whose panel never opened, and it reads 31 - so 33 is the
switch, not the account. Nothing later matters either: the client opens the panel on its own at
frame 524 of the GM session, with no server packet in front of it, and the
`S_RESPONSE_SERVER_ADMINTOOL_AWESOMIUM_URL` that answers `C_REQUEST_SERVER_ADMINTOOL_AWESOMIUM_URL`
appears in the non-GM sessions too.

### Why TeraSharp sends 31 even with tera-api privilege 33

`LoginHandlers` (T89b) writes `GmAccounts.IsListed(s.Account.Name) ? 33u : 31u`, i.e. it reads only
`TERASHARP_GM_ACCOUNTS`. Two traps:

1. **The env value must hold the numeric tera-api `accountDBID`** ("1", "2800"), because that is
   what the launcher puts in `C_LOGIN_ARBITER.name` - see `AuthRequest.AccountName`. A display name
   in there never matches, and the account silently gets 31.
2. **tera-api's privilege cannot reach this code at all.** `GameAuthenticationLogin` answers
   `{Return, ReturnCode, Msg}`; `AuthResult` has no privilege field, and `LoginHandlers` never
   consults `accounts.admin_level` either.

T104 adds `GmAccounts.LoginStatusFor(accountName, storedAdminLevel)`, which returns 33 when the
allow-list matches **or** the stored `admin_level` is >= 1 - the column `set_admin_level` and the
admin web tool's `POST /api/gm-level` (T101b) both write. `LoginHandlers.cs` is human-owned; the
change there is one line:

```csharp
["status"] = GmAccounts.LoginStatusFor(s.Account.Name,
                 Program.Store?.GetAdminLevel((long)s.Account.AccountId) ?? 0),
```

## T107 - and why it STILL did not open: nobody answered Alt+A

T104/T106 made the lobby byte-equal to cap_final_gm_client2 and the panel stayed shut, so the
diff moved past it - frames 50-450, enter-world through the first Alt+A.

**The GM tool is an Awesomium web view. Alt+A does not open a window; it asks the server where to
point one.**

| frame | | packet |
|---|---|---|
| 99 | S->C | `S_ADMIN_GM_SKILL` `09 00 BE 64 00 00 00 00 01` - unprompted, in the enter-world burst, right before `S_LOAD_TOPO` |
| 402 | S->C | `S_ADMIN_HOLD_CHARACTER` `05 00 0E A3 00` |
| **405** | **C->S** | **`C_REQUEST_SERVER_ADMINTOOL_AWESOMIUM_URL` `04 00 D9 8B` - four bytes, no body. This is Alt+A.** |
| **412** | **S->C** | **`S_RESPONSE_SERVER_ADMINTOOL_AWESOMIUM_URL` `0C 00 CE F2 08 00 0A 00 00 00 00 00`** |
| 524 | C->S | `C_ADMIN_REQUEST_CUSTOM_BOOKMARK` - the panel is open |

The second enter-world repeats it (2445 GM_SKILL, 2741 request, 2747 reply, 2924 first `C_ADMIN_*`),
so the client asks once per Alt+A and **waits for the answer**.

**TeraSharp never sent that answer.** `C_REQUEST_SERVER_ADMINTOOL_AWESOMIUM_URL` (0x8BD9) was
registered nowhere and was absent from `ArbiterClientHandlers.ArbiterOwned`, so `PacketDispatcher`
forwarded it to a World that has no handler for it. The only mention of the reply in the whole
codebase was an unprompted push in `LoginHandlers`' **standalone (no-World) path** - which is why
this never showed up in replay testing and always failed live.

### What the reply carries: nothing

`08 00 0A 00 00 00 00 00` is `[u16 off=8][u16 off=10]` then **two empty wide strings**. That server
had no admin-tool URL configured and the panel opened regardless - so the window is waiting for the
REPLY, not for its contents. `BuildAdminToolUrl()` sends the empty form (byte-exact against 412);
pass a URL and it goes in the first slot.

### Two notes, not fixed here

* `S_ADMIN_GM_SKILL` and `S_ADMIN_HOLD_CHARACTER` are pushed at `C_LOAD_TOPO_FIN` to **every
  player**, with no GM gate (`HandlerRegistry.cs:82-83`, T89). The real server sends neither to a
  non-GM - they appear in no non-GM capture. Worth gating on `GmAccounts.LevelFor >= 1`.
* Ordering: the real server pushes `S_ADMIN_GM_SKILL` at frame 99, *before* `S_LOAD_TOPO` (100) and
  170 frames before `C_LOAD_TOPO_FIN` (271); TeraSharp pushes it after. No capture proves that
  matters, and the URL reply is sufficient on its own, so this is left alone.

---

## T120 - and it was the ordering after all

`cap_ts_gm_client.log` is the experiment T107 did not have: a TeraSharp session with status 33, a
byte-identical `S_RESPONSE_SERVER_ADMINTOOL_AWESOMIUM_URL`, and **the panel still shut**. T107's
last note above - "the URL reply is sufficient on its own, so this is left alone" - is wrong, and
this is the capture that shows it.

**The diff.** `cap_ts_gm_client.log` against `cap_final_gm_client2.log`, C_SELECT_USER to the
first `C_ADMIN_*`, as an ordered sequence of S->C names. Everything is equal except world content
the two sessions could not share (different characters, different zone). The one Arbiter-owned
difference:

| | real (gm_client2) | TeraSharp (cap_ts_gm_client) |
|---|---|---|
| `C_SELECT_USER` | 36 | 731 |
| `S_LOGIN` | 50 | 750 |
| `S_USER_ITEM_EQUIP_CHANGER` | 97 | 772 |
| `S_FESTIVAL_LIST` | 98 | 773 |
| **`S_ADMIN_GM_SKILL`** | **99** | **975** |
| `S_LOAD_TOPO` | 100 | 774 |
| `C_LOAD_TOPO_FIN` | 271 | 970 |
| `S_SPAWN_ME` | 296 | 986 |
| `S_ADMIN_HOLD_CHARACTER` | 402 | 974 |

The real Arbiter sends `S_ADMIN_GM_SKILL` **inside the `S_LOGIN` burst, in the slot between
`S_FESTIVAL_LIST` and `S_LOAD_TOPO`**.
TeraSharp sends it on `C_LOAD_TOPO_FIN` - 201 frames later and on the far side of the topo
transition, after the client has built the in-game UI.

**The three things that are NOT the difference**, all checked byte for byte:

* `S_LOGIN_ARBITER.status` - `01 00 21 00 ...` (33) at frame 7 in both. T89b's fix holds.
* `S_RESPONSE_SERVER_ADMINTOOL_AWESOMIUM_URL` - `08 00 0A 00 00 00 00 00` in both. **The real
  Arbiter answers with an empty Title and an empty Url as well.** Its PDL dumper
  (`Arb_part_022.c:13286`, guard `7 < len`) names the two `u16` refs *Title* and *Url*, and the
  offsets 8 and 10 point at two bare terminators. There is no
  `Handler_C_REQUEST_SERVER_ADMINTOOL_AWESOMIUM_URL` in any of the 96 decompile parts, so on a
  real stack this reply comes from **World**, not the Arbiter - T107's local answer is harmless
  but was never load-bearing.
* `S_ADMIN_HOLD_CHARACTER` - `05 00 0E A3 00` in both, and the real one arrives ~106 frames
  *after* `S_SPAWN_ME` (402, 675, 914). It is not part of the enter-world burst at all.

**The control.** `cap_final_gm_client` is the fourth capture and the one nobody used: a GM account
that pressed Alt+A (frame 323), got the same empty reply (438) - and got **no panel**. Its
`S_LOGIN_ARBITER.status` is `0x1F` = 31, and it carries **zero** `S_ADMIN_GM_SKILL`. So across
four real captures, the panel opened in exactly the session that received `S_ADMIN_GM_SKILL`
before `S_LOAD_TOPO`, and in no other.

**The fix.** `ArbiterClientHandlers.SendAdminGmSkillIfOperator(s, level)` (T120) plus the two
constants `AdminGmSkillFollows` / `AdminGmSkillPrecedes` that name the slot. Two human-owned
one-liners put it there - see the T120 report. T107's `LevelOf(s, store) >= 1` gate moves with it,
so the "not fixed here" note above is now closed on both counts.

### T121 - correction, and where the push actually has to go

**T120 read frame 546 wrong.** It is not a second enter-world push. Frames 542-549 read:

```
542 C->S C_ADMIN_GM_SKILL        02 00 F0 0A 00 80 00 00 00 00 00 00   <- the GM toggles it in the tool
546 S->C S_ADMIN_GM_SKILL        00 00 00 00 00                        <- the REPLY, enabled = 0
547 S->C S_SYSTEM_MESSAGE
548 S->C S_FESTIVAL_LIST                                               <- a /@teleport starts here
549 S->C S_LOAD_TOPO
```

546 answers 542 (`GmCommands.cs:1107` already does this), and the teleport at 548/549 carries **no
push at all**. So the real Arbiter pushes `S_ADMIN_GM_SKILL` **once per world entry**, not once per
topo load - and a per-topo push would re-enable a skill toggle the GM had just turned off.

**And T120's fix site is unreachable on a live server.** `LoginHandlers.OnSelectUser` returns at
`if (WorldEntry.EnterWorld(s, _log)) return true;`, so the whole `S_LOGIN` ... `S_FESTIVAL_LIST`
... `S_LOAD_TOPO` burst below that line is the **standalone** path. With a World link up those
frames come from World through `SA_BYPASS_TO_CLIENT`, and the T120 line never runs.
`WorldEntry`'s pre-hand-off call does run, but it fires before World has sent `S_LOGIN` - the
client has no user object yet and drops it.

**The fix (T121): inject in front of the tunnelled `S_LOAD_TOPO`.**

`ArbiterClientHandlers.DeliverTunnelled(s, packet)` becomes the tunnel's delivery action, so every
W->A client packet for a session passes through it on its way to `GameSession.Send`. When the
packet is an `S_LOAD_TOPO` (0xE828), the session is an operator, and the one-shot has not been
taken since the last `SDB_USER_ENTERWORLD`, it sends `S_ADMIN_GM_SKILL` first and then forwards.
The client sees `... S_FESTIVAL_LIST, S_ADMIN_GM_SKILL, S_LOAD_TOPO ...` - cap_final_gm_client2
98 / 99 / 100 exactly.

Two design notes worth keeping:

* **Anchor on the packet you must precede, not the one you must follow.** `S_FESTIVAL_LIST` is
  where frame 99 sits *after*, but if World ever stops sending it the push silently disappears;
  anchoring on `S_LOAD_TOPO` cannot fail that way, and it is the frame the client acts on.
* **The one-shot is cleared in `DbProxyHandlers.OnUserEnterWorld`**, beside T113's
  `MarkEnteredWorld`. That is the one event a zone change does not raise and a relog does - which
  is precisely the 99-versus-548/549 distinction above.

Cost: two bytes compared and one dictionary miss per tunnelled packet; the admin-level lookup
(which reads the accounts row) only runs on an `S_LOAD_TOPO` that has not pushed yet.

**Still to remove**: `HandlerRegistry.cs:85`'s `BuildAdminGmSkill` push on `C_LOAD_TOPO_FIN` (~200
frames too late), and `WorldEntry.cs:33`'s pre-hand-off call (too early). `LoginHandlers.cs:233`
stays - standalone has no World to tunnel through.

---

## T123 - found it: `S_LOGIN_ACCOUNT_INFO.apiServerAuthToken` is empty

The first capture pair that isolates the panel: **`cap_final_gm_client2`** (real Arbiter, status
33, client sends `C_ADMIN_REQUEST_CUSTOM_BOOKMARK` at 524 - **panel opened**) against
**`cap_classic_client`** (TeraSharp, status 33, no `C_ADMIN_REQUEST_*` ever - **panel did not
open**). Both GM, both status `0x21`. Diffing every S->C frame from `S_LOGIN_ARBITER` (7) to the
first `C_ADMIN_REQUEST_*` / `C_REQUEST_PVE_RANKING`, with World content filtered out, the
account-scoped gap is one packet - and it is the **second** frame of the session.

```
S_LOGIN_ACCOUNT_INFO.3.def   (majorPatchVersion >= 100 - this build)
    uint64 accountId
    int32  antiCheatChecksumSeed
    string dbServerName
    string apiServerAddress
    string apiServerAuthToken
```

| field | real (frame 8, 544 B) | TeraSharp (frame 8, 64 B) |
|---|---|---|
| accountId | 1 | 1 |
| antiCheatChecksumSeed | 619351 | **0** |
| dbServerName | `PlanetDB_2800` | `TeraSharp` |
| apiServerAddress | `127.0.0.1:8800` | `127.0.0.1` - **no port** |
| apiServerAuthToken | a 231-char JWT | **empty string** |

The token is an HS256 JWS the Arbiter mints per login:

```
header  {"alg":"HS256","typ":"JWS"}
payload {"accountDbId":1,"aud":"api","exp":1789619471,"iat":1789619351,
         "iss":"arbiter","nbf":1789619351,"planetId":2800}
```

`iss: arbiter`, `aud: api`, **120-second lifetime**, carrying the account db id and the planet id.
`127.0.0.1:8800` is tera-api's **gateway API** (`API_GATEWAY_LISTEN_PORT`,
`src/servers/gatewayApi.server.js`), not the arbiter API on 8080.

**Why this is the gate.** The In-Game Operation Tool is an embedded Awesomium web view. T120
proved the address does **not** come from `S_RESPONSE_SERVER_ADMINTOOL_AWESOMIUM_URL` - the real
server answers that with an empty Title and an empty Url too. It comes from these two fields, which
is exactly why the `.3` def (patch >= 100) added them over `.2`. With no port and no credential the
view has nothing to open, so nothing opens, and the client never reaches the packets the tool
sends. Across all four real captures the account fields are populated and only `status` varies -
`cap_final_gm_client` has the token and status **31**, and got no panel either. So the gate is
**status 33 AND a usable `apiServerAddress` + `apiServerAuthToken`**; TeraSharp has had the first
since T89b and neither of the others ever.

**The work this implies** (not done here - T123 is analysis):

* TeraSharp must mint the JWT with the same secret tera-api verifies, or fetch one from tera-api
  per login. It cannot be faked: HS256 with a key only tera-api holds.
* `apiServerAddress` must carry the port (`127.0.0.1:8800`), and the gateway API component has to
  be running - it is a separate tera-api component from the arbiter API that `TERASHARP_AUTH` uses.
* The 120 s expiry means the token is minted at `C_LOGIN_ARBITER` time, not cached at startup.
* `antiCheatChecksumSeed` is 0 in TeraSharp. Harmless while the proxy runs with
  `"integrity": false` (`status/PROXY-DESIGN.md` section 1), but it is the same packet and the same
  fix.

**Second, unrelated bug found in the same diff.** `S_SELECT_USER` (`byte unk1 / uint16 unk2 /
uint64 unk3`, 11-byte body) is the same length in both and differently laid out:

```
real     37   01 | 01 00 | 00 00 00 00 00 00 00 00      unk1=1 unk2=1 unk3=0
TeraSharp 33  01 | 00 00 | 00 00 00 00 00 01 01         unk1=1 unk2=0 unk3=0x0101000000000000
```

TeraSharp writes the two trailing bytes at the end of `unk3` instead of into `unk2` - the fields
are being emitted out of declaration order. Not the Alt+A gate (the panel is decided long before
`S_SELECT_USER`), but it is wrong bytes on the wire and should be its own task.

### T124 - the fix

`Auth/ApiGatewayToken.cs` (new) mints the credential; `ArbiterClientHandlers` builds the two
packets; three one-line calls in `LoginHandlers` (human-owned) put them on the wire.

**The token is byte-reproducible.** With `accountDbId = 1` and `iat = 1789619351`, `Mint` produces
the capture's first two segments character for character and a 231-char token - the same length
frame 8 carries. Signing differs only in the key, which is the one thing we cannot read off the
wire.

```
header  eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXUyJ9
claims  eyJhY2NvdW50RGJJZCI6MSwiYXVkIjoiYXBpIiwiZXhwIjoxNzg5NjE5NDcxLCJpYXQiOjE3ODk2
        MTkzNTEsImlzcyI6ImFyYml0ZXIiLCJuYmYiOjE3ODk2MTkzNTEsInBsYW5ldElkIjoyODAwfQ
```

**`antiCheatChecksumSeed` is the login instant's low six digits.** The capture's 619351 is
`1789619351 % 1000000` - its own `iat`. One sample, so not a proof, but it is stable for a login,
never 0, and free. 0 is what we sent before and the one value the client could read as "no seed".

**The address is repaired, not trusted.** `Address()` strips a pasted scheme and appends the
default port when none is given, because `127.0.0.1` with no port is precisely the value that did
not work and a config that forgets the port must not reproduce the bug.

**Nothing verifies the signature.** tera-api has no `jwt.verify` in `src`, so the gateway takes
whatever arrives - a well-formed token is enough. It is signed properly anyway so that turning
verification on is a config change. `TERASHARP_API_JWT_SECRET` has **no hard-coded default**: a
signing key does not belong in source. Unset, it mints with a per-process random key and
`--check-config` says so.

`S_SELECT_USER` is fixed in the same pass: `unk2 = 1, unk3 = 0`, byte-exact against frame 37.

**Three human-owned one-liners** (`Handlers/LoginHandlers.cs`):

```csharp
// line 61 - replace the 64-byte stub
s.SendByDef("S_LOGIN_ACCOUNT_INFO", ArbiterClientHandlers.BuildLoginAccountInfoFields(
    s.Account.AccountId, DateTimeOffset.UtcNow.ToUnixTimeSeconds()));   // T124
// line 176 - the World-not-ready refusal
s.SendByDef("S_SELECT_USER", ArbiterClientHandlers.BuildSelectUserFields(accepted: false));
// line 183 - the accepted path
s.SendByDef("S_SELECT_USER", ArbiterClientHandlers.BuildSelectUserFields());
```

Also fixed here: `SelfTest.IsSecret` matched only `_TOKEN` and `_PASSWORD`, so `--check-config`
would have printed the new signing key in full. It now masks `_SECRET` and `_KEY` too.

## T195 — GM routing after a cross-World transfer

| Path | Evidence and result |
|---|---|
| `0x2829` command | Fixed `ForwardToWorld`'s default-World overload: use the session's `CurrentWorldId` through the shared admin sender. Native `Arb_part_067.c:6893–6912` uses `User::GetBypassSession`; `Arb_part_028.c:17295–17311` selects the user's current continent/instance World. |
| Capture pin | `cap_2man_b` raw16350: `kill 1000000`, user1, mode1, 44 B on physical link#54; registration11532/11533 identifies World13. `cap_final2b` raw30646 also pins the 30 B `nodie` frame on World0/link#51. The requested `cap_final2b` tap contains no literal `kill` or dungeon-owner link. |
| `0x2827` T148/T155 panels | Already use the GM session's `CurrentWorldId`; unchanged builders. |
| `0x2825` ask / `0x2826` result | Ask already uses the target session's World; filled result already forwards as `0x2827` to the requester's current World. |
| Missing owner link | Shared sender now checks `HasLinks(CurrentWorldId)`; another connected World cannot make the send succeed or receive a fallback frame. |

Regression `T195_admin_frames_follow_current_world_and_target_without_main_fallback` uses both
World0 and World13 sockets, pins both complete captured command frames, exercises skill/teleport
panels and the target/requester ask cycle, and checks authorization and absent-owner refusal.
No human-owned patch or wire-layout change is required.

Files: `src/TeraSharp.Arbiter/Handlers/GmCommands.cs`,
`src/TeraSharp.Arbiter/Handlers/ArbiterClientHandlers.cs`, `src/TeraSharp.Arbiter.Tests/T195.cs`,
`tools/t195-evidence.py`, `data/t195/selected.json`, `data/t195/source-manifest.json`, this section.
The evidence exporter records the six relevant registration/entry/command frames and source hashes;
the regression embeds only the two command frames and needs no optional binary fixture.

Validation: isolated source copy based on master `dff8c98` builds successfully; **1032 passed /
0 failed / 26 skipped**, including the new routing regression. All edited C# files match the tested
copy by SHA-256. Live command use inside9781 remains for the next deployed run.
