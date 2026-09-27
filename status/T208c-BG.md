# T208c - /@battlefield 38 <leader> did nothing, silently

Evidence: `D:\packetlogs\cap_bg4.log` (tap, +7 h vs `arbiter-bg4.log` local), `arbiter-bg4.log`.

## 1. What actually happened

The command was NOT dropped on our side. The creation frame went out and World refused it.

| tap | local | direction | frame |
|---|---|---|---|
| 194977 | 10:07:44 | A->W | `0x1518 ABS_CREATE` template 38, isGm 1, parties `0xAF0000100000002`, `0xAF0000100000001` |
| 194978 | 10:07:44 | W->A | `0x13E0 BSA_CREATE_LOG` battlefieldId `0x0AF00002`, template 38, **party list `0` / `0`** |
| 194979 | 10:07:44 | W->A | `0x1512 BSA_CREATE_RESULT` (empty) |
| 194980 | 10:07:44 | A->W | `0x13E1 ABS_CREATE_LOG` log identity 3 |

No `0x1513` offer at all. The same command after both parties were re-formed:

| 196198 | 10:09:46 | A->W | `0x1518` template 38, parties `...004`, `...003` |
| 196199 | 10:09:46 | W->A | `0x13E0` battlefieldId `0x0AF00003`, **parties `...004` / `...003`** |
| 196200 | 10:09:46 | W->A | `0x1513` offer, 462 B |

So World resolved neither handle the first time and both the second.

## 2. The precondition

| party | formed | reached world 10? |
|---|---|---|
| `0xAF0000100000001` | 10:04:27 | no |
| `0xAF0000100000002` | 10:04:34 | no |
| `0xAF0000100000003` | 10:09:34 | yes (tap 195986) |
| `0xAF0000100000004` | 10:09:38 | yes (tap 196074) |

World 10's links came up at **10:07:01** (`World 10 claims 18 continents`, links #26/#27/#28).
`AS_DO_CREATE_PARTY` is sent when a party FORMS; nothing replays it to a World that links later.
The two stale parties were therefore unknown in world 10, and the handles resolved to zero.

Not the cause: the BF world choice (`WorldForContinent(115) = 10` in both), `HasLinks(10)` (true in
both), member sessions (both parties had 1 live session + 2 offline members in both), the 15 s
`bfEnterDelay` (that timer is on the entry path, after the offer, and never ran here).

## 3. What was silent

`CreateForGm` had three exits that sent no frame and still said `Enter battleField[38]`, and it
logged nothing at all on any path:

- the template has no continent,
- no World owns that continent,
- the owner World has no links (the 10:04:40 attempt, before world 10 linked).

And World's refusal itself was unreported: the zero party list was written straight into
`battlefield_log` as a battlefield with no parties.

## 4. The fix

- `CreateForGm` logs **every** exit at Information with its reason and answers the GM with a system
  message; `Enter battleField[id]` is said only when the frame actually left. The three silent
  exits above now each name the template, the continent and the world.
- The command arms a 30 s wait (`GmCreateWindow`). `BSA_CREATE_LOG` for that template is World's
  verdict: an all-zero party list logs the refusal with the reason and tells the GM
  *"world 10 does not know either party (they were formed before it came up). Re-form the parties
  and retry."* One resolved handle names the missing one. A resolved pair closes the wait quietly.
- `BSA_CREATE_RESULT` arriving inside an open wait is logged as the unique id being consumed
  without a battlefield being built.

Stale parties are now **refused explicitly**, which is one of the two outcomes asked for.

## 5. Making the stale case work instead - done in T208d

The party mirror is now replayed to a World that links after the parties formed, from the same place
the real Arbiter does it (`PartyManager::OnConnectWorldServer`, Arb_part_079.c:16418). See
status/T208d-PARTY-REPLAY.md. The refusal below stays as the backstop for any other reason World
might resolve a handle to zero.

Note for the record: the concern raised here about a second, unfound sender of the party mirror was
wrong - it came from a stale working copy. `PartyWiring.RouteWorldAction` broadcasts the mirror
opcodes to every connected World already, which is why parties 3/4 reached world 10 and 1/2 did not.

## 6. Tests

`Tests/T208c.cs`, 2 tests:

- `T208c_world_that_resolves_no_party_is_an_explicit_refusal` - the captured 36-byte
  `BSA_CREATE_LOG` shape with `0/0`, with one handle, with both, and for another template.
- `T208c_every_gm_refusal_answers_the_caller` - the three previously-silent exits each answer with
  their reason, and the success path still sends `ABS_CREATE` to the template's owner and only then
  says the native text.
