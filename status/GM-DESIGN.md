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
local_e8 = (int *)(*local_f8 + local_100);
*local_e8 = 0;  FUN_14013d0b0(&pkt,*local_e8);   // u32 string offset, reserved as 0
FUN_14013d0b0(&pkt,(int)lVar6);                   // u32 userId   (User+0x120 = playerId)
FUN_14013d0b0(&pkt,(int)lVar18);                  // u32 bypass mode (1 = bucket 4)
*local_e8 = *local_f8;                            // backpatch: 18, frame-relative
FUN_140351030(&pkt,line);                         // wcscpy_s -> UTF-16LE + a u16 0
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
(`lVar18 = param_1[0x24]`, `Arb_part_067.c:6898`), not from the client packet.
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
