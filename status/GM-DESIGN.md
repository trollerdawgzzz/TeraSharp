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
