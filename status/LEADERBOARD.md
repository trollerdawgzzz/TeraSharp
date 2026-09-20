# Leaderboard, step 1 (T118)

## 1. The brief's premise, corrected

The task asked for the **pagination** crash in `Handler_C_REQUEST_P*_RANKING` and a safe
page/size range. There is no page and no size on either packet. Both defs are
`int32 season / int32 id / int32 class`, and the handlers (Arb_part_041.c:9474 PVE, :9543 PVP)
read exactly those three at body +0/+4/+8 behind a `param_3 < 0x10` guard before queueing
`PVERankingSystemManager::SendRank(int,int,int,enum ClassType)` (Arb_part_050.c:10910).

The `count + page * -10 + 9` pagination crash is **`C_VIEW_GUILD_WAR`**, which the proxy's
exploit-fix mod already bounds correctly and which this task does not touch. The paginated
neighbour among the leftovers is `C_REQUEST_PARTY_MATCH_INFO_PAGE` (`int16 page`).

## 2. The ranking crash, field by field

| Field | Off | Guard in the Arbiter | Verdict |
|---|---|---|---|
| `season` | +4 | `0 < season` in `SendRankValidate`; `== current` -> now-path, `< current` -> past-season path (gated `(current - season) - 1 < 3`), `> current` -> nothing | inert |
| `id` | +8 | `0 < id`, then `DungeonDataSheet::GetDungeonTemplate(int)` (Arb_part_003.c:3388) - a red-black-tree **find** returning 0 for an unknown key, checked by the caller | **safe** |
| `class` | +0xC | **none** | **the crash** |

`class` reaches two places unchecked:

- **Past season**, Arb_part_050.c:10630 - `puVar13 = puVar13 + ((longlong)param_5 + 1) * 3;`
  A raw pointer index, 24-byte stride, no bound of any kind. `0x7fffffff` is a ~51 GB offset; a
  negative value walks backwards. A wild read, i.e. a real fault.
- **Current season** - `RankTree<LevelTime,...>::ClassRank(enum ClassType,int)`
  (Arb_part_049.c:11647) opens with
  `if (0xe < (ulonglong)(int)param_2) { /* WARNING: Subroutine does not return */ }`, a fatal
  abort. The cast sign-extends, so negatives take the same path.
- `class == 0x10` selects the aggregate branch in both and never reaches the index.

**Safe set: `class` in 0..14, or 16.** Note 16, not the 15 the mod's old comment assumed - 15 is
an index the past-season path walks off the end with, and it was on the old allow-list.

That the function at Arb_part_050.c:9658/10550 is this packet's writer is confirmed
independently: at :10593 it stamps opcode `0xBEDC`, which `data.json`'s 376012 map names
`S_PVE_RANKING_LIST`.

## 3. What the mod did, and what it does now

Before: validated `event.id` (the safe field) against `{0..12, 15}`, and dropped **all** PVE
traffic because "this build ships no `C_REQUEST_PVE_RANKING` def". The def exists - in
`tera_v100_MASTER_FINAL`, 34 bytes, exactly the three fields the mod's own comment said to
create. It is only missing from the proxy's own `data\definitions\` (918 defs, PVP only).

After (`status/EXPLOIT-FIX-RANKING.diff`, plus copying that one def): both packets take the same
field check - `class` in the safe set, `season > 0`, `id > 0` - and both outcomes are logged,
pass and drop. In-range requests now reach the Arbiter.

## 4. Capture plan

Real Arbiter, fixed mod, tap on the client link. One character, one session.

| # | Do | Expect |
|---|---|---|
| 1 | Open the leaderboard | `C_REQUEST_PVE_RANKING` (0xA024) or `C_REQUEST_PVP_RANKING` (0x573B) with the tab's `class`; one `[exploit-fix] ... PASSED` line each |
| 2 | Read the reply | `S_PVE_RANKING_LIST` (0xBEDC) / `S_PVP_RANKING_LIST` (0x62FA) - **the row layout is the point of the capture** |
| 3 | Switch tab (PVE <-> PVP) | the other request, and whether `season`/`id` change with it |
| 4 | Page 2 | whether a second request is sent at all, and with WHICH field changed - if none is, the list is one-shot and the client pages locally |
| 5 | Pick a class filter | the `class` values a real client sends: this is what confirms 0..14 and 16 against live traffic rather than against the decompile alone |
| 6 | Open party matching | `C_VIEW_INTER_PARTY_MATCH_DUNGEON_LIST` (0xBA83) -> 0xF732, `..._BATTLEFIELD_LIST` (0x9830) -> 0x7A2C |
| 7 | Page the party-match board | `C_REQUEST_PARTY_MATCH_INFO_PAGE` (0xA25F) and whether anything answers it |
| 8 | Change the match rule | `C_REQUEST_CHANGE_PARTY_MATCH_RULE` (0xBE34) -> 0xBCA0 with the echoed type |

**Separately, on the isolated test Arbiter only:** point `ranking-crash-probe` at the `class`
field, not `id`. As it ships it fires `id`, which per section 2 cannot crash anything - that is
why the probe has never reproduced on demand. `class = 15` with `season < current` is the
cheapest reproduction; `class = 99` on the current season should abort instead of faulting, and
telling those two apart in the crash dump confirms both halves of the analysis at once.

## 5. What TeraSharp answers today

All nine were unregistered, so the leaderboard and party-match windows opened onto silence.
`Handlers/ArbiterClientHandlers.cs` section `LeaderboardPackets` answers each with the empty
form its .def gives, bounds-checked:

| Request | Reply |
|---|---|
| `C_REQUEST_PVE_RANKING` | `S_PVE_RANKING_LIST` empty list (hand-built - see below) |
| `C_REQUEST_PVP_RANKING` | `S_PVP_RANKING_LIST`, empty `players` |
| `C_VIEW_INTER_PARTY_MATCH_DUNGEON_LIST` | `S_..._DUNGEON_LIST` {0,0,0} |
| `C_VIEW_INTER_PARTY_MATCH_BATTLEFIELD_LIST` | `S_..._BATTLEFIELD_LIST` {0,0} |
| `C_REQUEST_PARTY_MATCH_INFO_PAGE` | ack - no S_ opcode exists; page still clamped (T48) |
| `C_REQUEST_MY_PARTY_MATCH_INFO` | ack - no listing to describe |
| `C_REQUEST_CHANGE_PARTY_MATCH_RULE` | `S_REQUEST_CHANGE_PARTY_MATCH_RULE`, type echoed |
| `C_GROUP_DUEL_RECORD` | `S_GROUP_DUEL_RECORD`, all zero |
| `C_CHANGE_USER_NAME` | ack - no S_ opcode; **logged, not applied** (the rename path is T88's) |

`S_PVE_RANKING_LIST` is built by hand because its shipped .def has no fields at all - it says so
in its own comment - while the writer lays down two u16 back-patch slots before any element. The
def's field-less form would be a 4-byte frame, four bytes short of the header the client reads.

An out-of-range `class` is refused by TeraSharp too. Nothing here indexes with it, but answering
it as though it were valid would teach a probing client that the value is accepted, and the same
value against the real Arbiter is the crash in section 2.
