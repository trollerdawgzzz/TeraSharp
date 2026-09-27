# Leaderboard (T118 step 1, T119 step 2, T126 corrections, T133 vs the live server)

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

- **Past season**, Arb_part_050.c:10630 - `row = base + (class + 1) * 24 bytes`, with `class` taken raw from the packet.
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
| `C_REQUEST_PVE_RANKING` | `S_PVE_RANKING_LIST` empty list (hand-built - see below); **now filled, section 6** |
| `C_REQUEST_PVP_RANKING` | `S_PVP_RANKING_LIST`, empty `players`; **now filled, section 6** |
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

## 6. Answering from our own data (T119)

The real Arbiter never answers `C_REQUEST_P*_RANKING` on 100.02 - confirmed live, no capture
exists. So both frames are built from the binary and the shipped defs, and both are filled from
TeraSharp's own tables.

### 6.1 Where the two boards come from

| Board | Source | Fed by |
|---|---|---|
| PvE | `SUM(dungeon_cooldowns.clear_count)` per character | `SA_UPDATE_DUNGEON_CLEAR_COUNT` (0x13B7) |
| PvP | `COUNT(*)` of `game_log` rows with `category='pvp' AND action='pvp.kill'` | `SDB_ADD_PVP_USER_LOG` (0x27FE), T115 |

The brief asked for a `game_log` **dungeon** category. There is none: T115 created eight
categories (user, item, trade, guild, party, mail, warehouse, pvp) and none of the five decoded
log opcodes carries a dungeon clear. `clear_count` is the only dungeon progress TeraSharp
stores, and it is live, so it is the board. `pk.kill` is the outlaw counter and is **not**
counted - only rows where the character is the actor score.

Both queries drop `deleted_at != 0` characters and a zero score, cap at
`CharacterStore.RankingScoreLimit` (500), and order `score DESC, id`.

### 6.2 Ranking, filtering, paging

`RankingBoards.Rank` gives **competition** ranks - equal scores share a rank and the next row
skips past the whole tied group (1, 1, 3) - and breaks ties by character id so two calls give
the same order. T119's comments called this "dense"; section 8.2 corrects that against the
live board, whose ranks run 1, 11, 21. The class filter is T118's range: `0..14` is an
exact class, `0x10` is the aggregate. Anything else never gets here; the handler refuses it.

`RankingBoards.Page` returns one page of 50 and **appends the requester's own row** when the
page does not already hold it, which is what "my rank" on the leaderboard window means. The
request has no page field - it is `season`/`id`/`class` and nothing else - so the handler always
sends page 0 plus that row.

`season` must equal `RankingBoards.CurrentSeason` (1), the season
`S_P*_LEADER_BOARD_INFO` advertises (T91). The real handler serves `== current` from the live
tree and `< current` from an archive we do not keep, and does nothing at all above it, so any
other season is an empty board rather than a wrong one.

### 6.3 S_PVE_RANKING_LIST (0xBEDC) - 31-byte element

> **Superseded by section 7.2.** The offsets below are right; three of the *assignments* were
> not - +11 is the rank and +15 the stageLevel, not the other way round. T126 names all six.

`S_PVE_RANKING_LIST.1.def` has **no fields**, so the layout is read off the writer,
`PVERankingSystemManager::SendRankList` (Arb_part_050.c:9747, stamping 0xBEDC at :9758):

| Offset | Size | Writer's store | We send |
|---|---|---|---|
| +0 | u16 | `*puVar24` | here |
| +2 | u16 | `puVar24[1]` | next (0 = last) |
| +4 | u16 | `puVar24[2]` | name offset |
| +6 | u8 | `IsRookie(..)` | computed from +7/+11 (always 0, see below) |
| +7 | i32 | `rankInfo+0x1C` | 0 - level-band **width** |
| +11 | i32 | `rankInfo+0x18` | the entry's level - level-band **base** |
| +15 | i32 | `rankInfo+0x08` | rank |
| +19 | i64 | `rankInfo+0x10` | score (clears) |
| +27 | i32 | `node+0x18` | class |
| +31 | | | NUL-terminated UTF-16 name |

Three of those scalars have no name anywhere - not in the def, not in a dumper, not in a
capture. What the binary does say is the relation:
`PVERankingSystemManager::IsRookie(int,int,int)` is `(myLevel < B + A) && (B <= myLevel)` over
the +11 and +7 values, so those two are a level **band**, not the entry's own level. We are the
data source, so the choice is ours and it is written down rather than guessed at: width 0, base
= the entry's level, and the rookie flag computed the way the server computes it from what we
wrote - so the frame is self-consistent whatever the client does with it.

### 6.4 S_PVP_RANKING_LIST (0x62FA) - 23-byte element

> **See section 7.3**: the def and the writer disagree on the first two scalars. The bytes we
> send are unchanged; the field shape is corrected.

From the shipped def (`int32 unk; byte unk2; int32 rank; int32 rating; int32 class; string
name`): here/next/nameRef, then `unk` at +6, `unk2` at +10, rank +11, rating +15, class +19,
name at +23. `rating` carries the kill count. The def's own comments call `unk` "probably
previous rank" and `unk2` the up/down icon; we send 0 for both, because we keep no history and
an invented arrow is worse than none.

This one has an independent witness: the test writes the same rows through the shared
`DefinitionWriter` and asserts the def's body is byte-identical to the hand-built one.

### 6.5 What changed from T118

| Request | T118 | T119 |
|---|---|---|
| `C_REQUEST_PVE_RANKING` | empty list | ranked clears, byte-exact to the writer |
| `C_REQUEST_PVP_RANKING` | empty `players` | ranked kills, cross-checked against the def |

The registry is unchanged - both opcodes still point at
`LeaderboardPackets.OnRequestPveRanking` / `OnRequestPvpRanking`. An **empty** board is still
byte-identical to the frame T118 shipped, which is the only form of these two packets that has
been in front of a live client; a test asserts that.

## 7. The class dropdown, and what was actually wrong (T126)

### 7.1 Nothing on the wire populates a class list

The reported symptom was a class dropdown showing `undefined`. It is not server data. Four
places were checked and none carries class names or a filter list:

| Source | Result |
|---|---|
| `data.json` map 376012 | 29 `RANK`/`LEADER` opcodes; none is a class list |
| The full def set (4550 defs) | no def declares a class array; `S_PVE_RANKING_LIST` still has **no fields** |
| Both `S_PVE_RANKING_LIST` writers (`SendNowSeasonRank` Arb_part_050.c:9759, `SendPrevSeasonRank` :10594) | the header is `[u16 count][u16 firstElementOffset]` and nothing else - there is no room for one |
| `cap_final_gm_client2`, every S->C packet | no packet with a class or name list anywhere in the capture |

`S_P*_LEADER_BOARD_INFO` **is** a selector feed, but it is the *board* selector: PvE ids
3126 / 3203 / 9126 (dungeons), PvP 10 / 30 / 37 (battlegrounds), and frame 1989 of the capture
shows the client echoing one straight back as `id` (`0x0C83` = 3203). TeraSharp already sends
both, byte-identical to the capture (T91).

So the class dropdown is client-side, and there is **no packet change that can fill it**.

### 7.2 What the same research did find: three slots in the wrong place

T119 said three PvE scalars had no name. They do. The element source is a
`ReturnRankInfo<T>`, and the two boards share its shape:

```
ReturnRankInfo<int>        0x10 B:  ?(+0)  int score(+4)                    a(+8)     b(+0xC)
ReturnRankInfo<LevelTime>  0x20 B:  ?(+0)  int level(+8)  i64 time(+0x10)   a(+0x18)  b(+0x1C)
```

Both writers copy those four the same way - `score -> +0x0F`, `a -> +0x0B`, `b -> +0x07`
(Arb_part_050.c:9812-9821 and :10266-10270). The PvP `.def` names two of them: `rank` at +11
and `rating` at +15. **So +11 is the rank and +15 is the score, on both boards** - and T119
shipped them the other way round on the PvE board.

The i64 at +19 is `LevelTime.time`. `S_USER_PVE_RANKING`'s def - the companion packet, written
from the same `Score()` call (`*(lVar18+0x38)` i32 then `*(+0x40)` i64, :10054-10058) - names
that pair `stageLevel` and `clearTime`. So the corrected PvE element is:

| Offset | Size | Field | We send |
|---|---|---|---|
| +0 / +2 / +4 | u16 | here / next / nameOffset | |
| +6 | u8 | `rookie` | `IsRookie(viewerRank, rank, changedRank)` - always 0 |
| +7 | i32 | `changedRank` | 0 - no previous season to diff against |
| +11 | i32 | `rank` | **moved from +15** |
| +15 | i32 | `stageLevel` | the character's level (**moved from +11**) |
| +19 | i64 | `clearTime` | the clear COUNT - see below |
| +27 | i32 | `class` | |
| +31 | | NUL-terminated UTF-16 name | |

`clearTime` is the one field whose units the client will read differently from how we mean
them: the retail PvE board ranks by fastest clear time, and TeraSharp stores clear counts.
That is stated in the code rather than hidden.

### 7.3 The PvP def and the PvP writer disagree

`S_PVP_RANKING_LIST.1.def` declares `int32 unk; byte unk2`, which encodes as i32@6 + u8@10.
The writer stores the **byte first**: `*(u8 *)(puVar27 + 3)` at +6 and `*(u32 *)(+7)` at +7
(:10266-10267). The decompile wins, so the byte sits at +6 and `changedRank` at +7. Both
fields are 0 in every frame we send, so the bytes are unchanged and the def cross-check in the
tests still passes - only the shape is corrected.

### 7.4 The missing second frame

`SendNowSeasonRank` sends **two** packets, not one: the list, then the requester's own line.
T119 sent only the first, so the "my record" row under the board had nothing to fill it.

| Packet | Opcode | Layout (def + writer, in agreement) | Size |
|---|---|---|---|
| `S_USER_PVE_RANKING` | 0xE748 | `byte rookie, i32 changedRank, i32 rank, i32 stageLevel, i64 clearTime` | 25 B |
| `S_USER_PVP_RANKING` | 0xC844 | `byte rookie, i32 changedRank, i32 rank, i32 score` | 17 B |

Written at Arb_part_050.c:10074 and :10518, as
`SendToSession<PKT_S_USER_PVE_RANKING_WRITE, bool, int&, int&, int&, __int64&>` and the PvP
four-argument form. An unranked requester gets rank 0 and zeroes rather than no frame, which
is what the server does when `Rank()` misses its tree.

## 8. Against the live Classic+ server (T133)

`D:\packetlogs\classic_live_ctl.txt` (T130) is the first capture of a leaderboard that
**answers**. The real Arbiter on 100.02 never did, so everything in sections 6 and 7 came from
the decompile alone. Five frames are kept in `data/classic-live/` and the tests round-trip
them.

### 8.1 The two pushes now carry the live sets

| | T91 / T119 sent | classic_live sends |
|---|---|---|
| season | 1 | **15** |
| PvP ids | 10, 30, 37 | **10, 26, 30, 37** (frame 5608, 60 B) |
| PvE ids | 3126, 3203, 9126 | **9043, 9056, 9068, 9156, 9168, 9507, 9756, 9768** (frame 5609, 92 B) |
| window | one shared pair | **one per board**, both exactly 28 days |

The ids are not decoration: the client echoes one back as `C_REQUEST_P*_RANKING`'s `id`
(frame 5807 asks for 9768, which is 5609's last entry), so a board we do not list is a board
the player cannot ask for. The season matters the same way - we advertise it and then refuse
any other, so advertising 1 while the client asks for 15 is a silently empty board.

`RankingBoards.CurrentSeason` is now one property, read from `TERASHARP_RANKING_SEASON` and
defaulting to 15, used by both the pushes and the handler. T91's 2022 frames are still
asserted byte for byte through the explicit `BuildLeaderBoardInfo` overload.

### 8.2 The element layouts, confirmed on 316 real rows

| Frame | Rows | What it settles |
|---|---|---|
| 5809, 932 B | 20 | the 31-byte PvE element, class-9 filter |
| 5856, 4847 B | 105 | the aggregate (class 16) reply |
| 5818, 3938 B | 100 | the 23-byte PvP element |

Every row decodes at T126's offsets and re-encodes to the same bytes. Row 1 of 5809 is
`rank 1, stageLevel 3, clearTime 173140` - two minutes fifty-three, a real dungeon clear -
which is the layout T126 re-pointed to and **not** the `rank@15 / score@19` T119 shipped.
5809's rows ascend by clearTime (low is good) and 5818's descend by rating; both are what the
field names say they are.

**The `.def`'s first two PvP scalars are definitively wrong.** T126 chose the writer's order
(`u8` at +6, `i32` at +7) over the def's (`i32` at +6, `u8` at +10) and could not prove it,
because every frame we send has both as 0. The live rows decide it: across all nine ranking
frames, +6 is 0 on 315 rows and 1 on exactly one, and the i32 at +7 runs -17..+12 around 0 -
a rank delta. Read the def's way the same bytes are 768, 256, -256 with a trailing 0 or 255.

**A dungeon record belongs to the group, and the board is competition-ranked** (corrected in
T133b - the first reading of 5856 guessed at both halves of this and got both wrong). Ten
rows share rank 1 and the same 359952 ms, and two of their classes appear twice, so it is a
ten-player raid record and not "one row per class". The next rank is **11**, not 2: the
ranks run 1, 11, 21, 31, 41, 51, 61, 69, 78, 87, 96 - each group pushes the next rank past
all of it, and the short steps (69, 78) are groups of eight and nine. That is competition
ranking, and it is what `RankingBoards.Rank` already does - `rank = i + 1` on a change of
score - whatever the older comments called it.

The `rookie` byte is also real: 315 of classic_live's 316 rows carry 0 and exactly one
carries 1 - row 97 of frame 5818, rank 96, "BFG", up seven places.

### 8.3 The self-rank frame is conditional

`SendNowSeasonRank` guards the second frame with
`requestedClass == viewerClass || requestedClass == 0x10` (viewerClass read at `+0x2f`; Arb_part_050.c:9961): the class
asked for is the requester's own, or the aggregate. T126 sent it unconditionally. All nine
requests in classic_live come from a class-9 player and agree:

| Frame | class asked | `S_USER_P*_RANKING`? |
|---|---|---|
| 5807, 5817, 5830, 5835, 5867, 5871, 5876 | 9 | yes |
| 5855 | 16 | yes |
| **5846** | **0** | **no - the list alone** |

`RankingBoards.SendsSelfRank(classFilter, viewerClass)` is that rule.

### 8.4 Nothing else in the exchange

Every `C_REQUEST_P*_RANKING` in classic_live is answered by exactly
`S_P*_RANKING_LIST` and then `S_USER_P*_RANKING`, in that order, and nothing else - the other
S→C frames interleaved around them are ordinary World traffic (`S_NPC_LOCATION`, `S_SOCIAL`,
`S_UPDATE_GUILD_QUEST_STATUS`). All six leaderboard opcodes are ones we now send.

### 8.5 Open: the live server does not paginate

5818 carries 100 rows and 5856 carries 105, in one frame. `RankingBoards.PageSize` is 50 and
the request has no page field to ask for the rest, so a 51st row is currently a row nobody can
see. Raising it moves T119's paging tests, so it is left as its own task; a test asserts the
divergence rather than letting it be forgotten.
