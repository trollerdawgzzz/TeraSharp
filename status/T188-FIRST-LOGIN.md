# T188 - "wedged until relog": what the capture does and does not show

2026-09-23. Subject: character `test` (id 10), reported unusable (items refused, skills not
learnable) on its first login after a World restart, fixed by a relog. Sources:
`D:\packetlogs\cap_queue4.log` (tap) and `arbiter-queue4.log`.

## 1. The three logins in cap_queue4

| login | tap seq | time (UTC) | link | what it is |
|---|---|---|---|---|
| A | 915 | 22:05:58 | 1 | first login of `test` after World started |
| B | 5424 | 22:13:10 | 1 | the relog, same World session |
| C | 9092 | 22:43:26 | 26 | first login after the World restart |

`arbiter-queue4.log` covers only the 22:43 session, so the Arbiter's own view of A and B is not
in this capture set.

## 2. Frame-by-frame, the bursts are the same

Comparing the three enter-world bursts (every A<->W frame with a DlmId in the burst's range):

| | A (first) | B (relog) | C (first after restart) |
|---|---|---|---|
| burst shape | identical | identical | identical |
| DBS_USER_ENTERWORLD | 15331 B | 15331 B | 15331 B |
| DBS_USER_RESTRICTION | present | present | present |
| DBS_USER_LOAD_INVENTORY | 6451 B (12 rows) | 6987 B (13) | 7523 B (14) |
| SDB_SET_QUESTLIST_INFO | 2279 B | - | 3879 B |

- **No SDB_\* went unanswered** in the whole capture: every request op has a reply op, so this is
  not a T145-style DLM wedge (the four apparent gaps - REQUEST_CARD_DATA, INIT_GUILD,
  LOAD_CITY_WAR_SEASON_INFO, TBA_REQUEST_BATTLEPASS_SEASONDATA - are the ops whose reply is named
  differently, and each has one).
- The world blob differs between logins in ~87 runs, but a **clean** character (`bobber`, id 11)
  shows the same zeros in the same places on both of its logins, so that region is play state
  World fills in, not the wedge.
- The inventory grows by one row per login: World's own `SDB_SET_QUEST_INFO` inserts template
  203201 (op 8) at every login (seq 1850, 6000, 9806 for id 10). That is a World-side quest
  reward repeating, and worth its own task; it is not a refusal.

**So the first login is complete on the wire.** Nothing the Arbiter sends or fails to send in the
login burst distinguishes A/C from B in this capture.

## 3. What is still open

The capture does not contain the refusal itself: no client log for `test` covering a refused item
or a failed skill learn is in this set. To pin it, the next capture needs, for the SAME login:

1. the tap (`reframe-tap`) **and** the client log for `test`, so the refusal message id is visible;
2. `TERASHARP_LOG_LEVEL=Debug` on the Arbiter, so per-request handling for that player is logged;
3. the repro: restart World, log `test` in, try the item and the skill, then relog and try again.

## 4. The repair: POST /api/reset-character

T188 adds the admin action the brief asked for. It drops every piece of per-character state the
Arbiter keeps between World sessions and nothing else:

| cleared | where it lives | why it can outlive a session |
|---|---|---|
| `enter-world` | `DbProxyHandlers.EnteredAt`, `GameIdByPlayer` | only a clean leave-world clears them |
| `gm-push` | `ArbiterClientHandlers` GmSkillPushed / GmInvisible / LastTopo | the Alt+A one-shot and the vaporize World last reported |
| `match-queue` | `MatchQueueManager` | a queued or re-offered match (T161) |
| `party-listing` | `PartyMatchManager` | the board entry (`OnLeaveWorld`) |
| `hold` | `WorldUserControls` (T180) | a panel hold or leave-dungeon departure keyed by the dead session's handle |

```
curl -X POST http://127.0.0.1:8051/api/reset-character -H "X-Admin-Token: $T" ^
     -d "{\"id\":10,\"reason\":\"wedged after a World restart\"}"
-> {"result":0,"cleared":["enter-world","match-queue"]}
```

Nothing durable is touched - no items, skills, quests, money or character row - so it is safe on a
character that is behaving, and running it twice clears nothing the second time.
