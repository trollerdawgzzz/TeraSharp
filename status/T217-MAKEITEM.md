# T217 - /@makeitem creates nothing, and 0x161E is not why

## 1. What 0x161E is

`SA_GIVE_FIELD_EVENT_CLEAR_REWARD`, from the Arbiter's own opcode-to-name table
(`Arb_part_003.c:5812`). It is an A<->W control opcode, not a DB-proxy twin, so it is named in
`World/WorldReplayTable.cs` rather than `DbProxyOpcodeNames` - that table only covers the
`SDB_`/`DBS_` pairs in 0x27xx-0x29xx.

### The 18 bytes

`Handler_SA_GIVE_FIELD_EVENT_CLEAR_REWARD` (`FUN_140726070`) guards on `param_3 < 0x12` and reads
the frame, header included:

| Frame offset | Field | Captured value |
|---|---|---|
| 0 | `u32` frame length | `12 00 00 00` = 18 |
| 4 | `u16` opcode | `161E` |
| 6 | `u32` variable-field offset, frame-relative | `0` |
| 10 | `u32` variable-field byte length | `0` |
| 14 | `u32` field-event id | `79 93 2C 04` or `35 52 2F 04` |

The variable field is a vector of **16-byte** rows (`lVar7 = ((byteLength - 1) >> 4) + 1`, and the
copy advances `piVar6 + 4` ints per row). 18 bytes is the fixed part with an empty vector, which is
the only form any capture has.

The handler hands `(fieldEventId, vector)` to a `FieldDataSheet` holder (`FUN_1405e5d80`) and
`return 1`. **There is no packet writer anywhere in it** - it is one-way, like the T74 audit-log
writes.

### It is a timer, not an answer

| Capture | Arrivals | Gap |
|---|---|---|
| `cap_makeitem` | 04:15:37.750, 04:22:57.835, 04:30:17.796, 04:37:37.964, 04:44:57.861 | 7m20s, to the second |
| `arbiter-makeitem2.log` | 21:59:57, 22:07:16, 22:14:37 | 7m19s, 7m21s |

Two payloads alternate and repeat. In `arbiter-makeitem2.log` the **first two arrive before any
character logged in** (22:08:12), and the third is 6 seconds after the second `/@makeitem` but on
the cadence it would have kept anyway. It is neither the create nor a pre-notice.

## 2. The create pair is unchanged

`cap_makeitem` has it twice, one millisecond apart each time:

```
15690 A->W 04:46:18.113 0x2829 AS_ADMIN_COMMAND   "makeitem 88384 1"
15691 W->A 04:46:18.114 0x2768 SDB_ITEM_SINGLE    886 B, atom op 8   (T153)
```

Note `makeitem 88384 1` - **two arguments**, the same shape as the failing `makeitem 69001 1`. The
argument count is not the difference, and World answers a two-argument makeitem fine.

## 3. What actually broke

In `arbiter-makeitem2.log` the two commands at 22:14:25 and 22:14:31 are logged
`-> ForwardToWorld` and **no `0x2829` leaves the Arbiter** - zero occurrences in a log that carries
21 other A->W frames, so it is not a logging gap. World never saw the command, which is why there
is no `0x2768` and no item.

`ArbiterClientHandlers.SendToWorld` returns `false` without sending when the session is not in
world, has no character, or its World has no link. `GmCommands.ForwardToWorld` discarded that
return, so the operator got an acknowledging log line and silence.

**Fixed:** the return is checked. A refused forward now logs a warning naming the gate that closed
and answers the GM with `S_SYSTEM_MESSAGE_CUSTOM`, so the next `/@makeitem` says which of the four
conditions it failed instead of doing nothing.

**Still open:** which gate closed at 22:14:25 is not recoverable from that log - the character was
in world (logged in 22:08:12, playing at 22:08:30, returned to lobby only at 22:15:13) and world 0
was the only World up and had a link, so none of the four obviously applied. One `/@makeitem` on a
build with this change prints the answer.

## 4. Tests

- `T217_the_field_event_tick_is_one_way_and_quiet` - the captured 18-byte frame, the `0x12` its own
  guard tests, and that 0x161E is sealed one-way and logged at Debug.
- `T217_a_command_that_cannot_reach_world_says_so` - every gate names itself rather than falling
  through to the catch-all.
- `Replay_one_way_set_is_the_documented_list` grew by 0x161E.

## T217b - the forward was not lost: it is in the tap

`cap_makeitem3_ctl.txt` frame 1295:

```
A->W#1 05:48:10.871 0x2829 len=52
12 00 00 00 | 09 00 00 00 | 01 00 00 00 | "makeitem 88384 1" (UTF-16, NUL-terminated)
```

Against the working call, `cap_makeitem` 15690:

```
A->W#1 04:46:18.113 0x2829 len=52
12 00 00 00 | 0A 00 00 00 | 01 00 00 00 | "makeitem 88384 1"
```

**One byte differs** - the player id at payload+4, 9 against 10. The frame is well formed, it is
the frame the working run sent, and it left the Arbiter. `SendToWorld` returned true and T217's
gate check was right to stay silent.

Why no `0x2768` follows it: **frame 1295 is the last frame in the tap**, and
`arbiter-makeitem3.log` ends on the same line at 22:48:10. Both the capture and the log were
stopped the instant the command was issued, so neither covers the millisecond in which the working
run answered. The evidence does not show World ignoring the command; it stops before World could
have answered. A capture that runs ten seconds past the command settles it.

### What was still worth fixing

Tracing the path did find a real silent drop, one step further down than the gate:
`WorldLink.SendFrame`'s first line was `if (!_sock.Connected) return;` - **no log, void return**.
A link that had dropped but had not yet been reaped from `_links` passes `HasLinks`, is chosen by
`WorldBridge.SendFrame`, takes the frame and discards it; `SendToWorld` then returns `true`
because it never looked. That is precisely "accepted, never sent, nobody told", and it is
intermittent in exactly the way the brief describes.

`WorldBridge.cs` is human-owned, so the fix ships as **`status/T217b-PATCH.diff`**: new
`TrySendFrame` on both `WorldLink` and `WorldBridge` returning whether the bytes went (the void
`SendFrame` stays, because PartyWiring and GuildWiring pass it as an `Action`), the
not-connected refusal logged rather than swallowed, and `SendToWorld` returning the bridge's answer
instead of `true`. Applied on master it builds clean and the suite is 1126 / 0 / 84.

Landed without the patch: `ForwardToWorld` now logs the send as well as the refusal, so the log
alone separates "never sent" from "sent, no answer" - the question this brief had to use a tap to
answer.

Test: `T217b_the_forwarded_admin_command_is_the_captured_frame` pins both captured frames and
asserts they differ in exactly that one byte.


## T219c - what actually swallowed the command: an expired account benefit

The A/B the taps give, with T217b's patch applied so the forward is proven on the wire:

| tap | command | player / account | World's answer |
|---|---|---|---|
| `cap_makeitem` 15690 | `makeitem 88384 1` | 10 / account 2 | `W->A 0x2768 len=886` after 1 ms |
| `cap_item_new` 76558 | `makeitem 88384 1` | 9 / account 1 | nothing for 40 s |

Same command, same template, so the template is not the problem. World's create path reads no
benefit either - `WorldQACommandHandler::MakeItem(User, MakeItemInfo)` (`FUN_1404f15e0`) guards only
on `templateId < 1000000`, `ItemTemplate[templateId] != null`, `amount > 0` and
`Item::IsStackableItem`, and `AdminLevel` at `User+0xA474` is still only read by the two console
printfs T218 found. What differs is the ACCOUNT, and the benefit is read on the account tick:

```
User::OnTickAccountTrait
  -> AccountTrait::CheckAccountBenefitInterval    every tenth tick
  -> AccountTrait::GetExpiredPackges              collects ids whose expiry has passed
  -> AccountTrait::DeletePropertyList             resolves each id in the UserTrait datasheet
     -> "Unknown user trait %d", assert AccountTrait.cpp(523), leave the delete loop
```

The package is not removed, so the next interval asserts again. T181 seeded 533, 534 and 1000 on
every listed operator account, and 1000's captured expiry is `0x6A800E6F` = **2026-08-15**, already
past. That is DB state - which is why the ae1b8b5 rollback changed nothing - and it is per account,
which is why every character on account 1 failed and account 2 was fine.

**Landed.** The seeding is gone (T182 found the teleport rule elsewhere, so it proved nothing);
`CharacterStore.RemoveBenefitExperimentRows` sweeps the rows it wrote, and the benefit load calls it,
so an affected account repairs itself on the next login. `OnLoadAccountBenefit` also withholds any
expired row from 0x28BC and logs a warning naming the packages. `POST /api/remove-benefit` takes
`{"account":N,"package":P}` for one row, or no package to sweep one account, or no target at all to
sweep every account. The one-shot SQL, if the rows are wanted gone before a login:

```sql
DELETE FROM account_benefits WHERE teleport_experiment = 1;
```

Five T219c tests; the cap_final2b frame-171 pin moved onto `BuildReply` itself, since nothing seeds
those three packages any more and the handler would now withhold the expired one.
