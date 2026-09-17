# Party matching - the manual party board (LFG), T78

Ground truth: `D:\packetlogs\cap_social4_client.log` frames **5194..5247** and
`cap_social4_ctl.txt`, the World<->Arbiter tap of the same session. Implementation:
`World/PartyMatchManager.cs`. Tests: `T78_party_match_frames_match_cap_social4`,
`T78_party_match_client_bodies_parse_at_the_handler_offsets`,
`T78_publish_link_and_cancel_follow_the_capture`.

This is the MANUAL board - the panel where a player types a message and appears in a list. It is
not the instance matchmaking queue (`INTER_PARTY_MATCH_POOL`, `S_CANCEL_PARTY_MATCH_POOL`), and
it is not the candidate list behind `C_REQUEST_PARTY_INFO` (0xFD35), which
`PartyWiring.NotModelled` still swallows.

---

## 1. The episode, frame by frame

| client frame | dir | packet | what |
|---|---|---|---|
| 5194, 5195 | C->S | `C_REQUEST_PARTY_MATCH_INFO` 0xEFD6 | browse, filter levels 15..25 |
| 5196 | C->S | `C_REQUEST_MY_PARTY_MATCH_INFO` 0x6E66 | what am I advertising? |
| 5197, 5198 | S->C | `S_SHOW_PARTY_MATCH_INFO` 0xDF65 | empty board |
| 5199 | S->C | `S_MY_PARTY_MATCH_INFO` 0xB5A3 | `07 00 00 00 00` - nothing |
| 5206, 5211 | C->S | `C_REQUEST_PARTY_MATCH_LINK` 0xA7DF | link button |
| 5207, 5212 | S->C | `S_SYSTEM_MESSAGE` | `@1582` - you have no listing |
| **5219** | C->S | `C_REGISTER_PARTY_INFO` 0xD902 | **publish**: isRaid 0, message `321` |
| 5220 | C->S | `C_REQUEST_PARTY_MATCH_INFO` | the client refreshes itself |
| 5221 | S->C | `S_SYSTEM_MESSAGE` | `@997` - published |
| 5222 | S->C | `S_SHOW_PARTY_MATCH_INFO` | one listing: leader 1003 `New`, `321` |
| 5225, 5228 | C->S | `C_REQUEST_PARTY_MATCH_LINK` | link button, now with a listing |
| 5226, 5229 | S->C | `S_PARTY_MATCH_LINK` 0xE2A4 | id 1003, channel 20, `New`, `321` |
| 5230 | C->S | `C_REQUEST_PARTY_MATCH_LINK` | third press |
| 5231 | S->C | `S_MUTE` | `14 00 00 00 01` - channel 20 cool time |
| **5245** | C->S | `C_UNREGISTER_PARTY_INFO` 0xD3D9 | **cancel** |
| 5246 | S->C | `S_SYSTEM_MESSAGE` | `@994` - withdrawn |
| 5247 | S->C | `S_SHOW_PARTY_MATCH_INFO` | empty again - **unrequested** |
| 5908 | C->S | `C_PARTY_MATCH_WINDOW_CLOSED` 0xFA6D | panel closed, no reply |

Two orderings fall out of this and are what the implementation follows:

* **Publish answers with the SMT and nothing else.** Frame 5222 is the reply to the browse the
  CLIENT sent at 5220, not a push - there is exactly one `S_SHOW_PARTY_MATCH_INFO` in the group.
* **Cancel answers with the SMT AND a page.** Nothing was requested between 5245 and 5247, and
  `Handler_C_UNREGISTER_PARTY_INFO` (Arb_part_041.c:13507) queues two jobs: `FUN_140838d70` (the
  unregister) at :13519 and then `FUN_140837540` (the same send-page job the browse queues) at
  :13528.

---

## 2. It never leaves the Arbiter

`cap_social4_ctl.txt` carries **no frame at all** between `01:34:11.150` (seq 7889) and
`01:34:19.420` (seq 7940), and the whole client episode above sits inside that window. The
correlation is exact: the client sends `C_VIEW_INTER_PARTY_MATCH_DUNGEON_LIST` fourteen times
starting at frame 5251, and the tap has exactly fourteen `AS_VIEW_INTER_PARTY_MATCH_DUNGEON_LIST_EXTENDED`
(0x1644) A->W frames for player 1003 starting at seq 7940 - so 5251 is 7940 and everything before
it in the group happened in the silence.

That matches the binary. The pool opcodes that would leave the process are
`AM_ADD_TO_PARTY_MATCH_POOL` / `MA_ADD_TO_PARTY_MATCH_POOL` (0x4655 / 0x4656), which address the
MATCHING server, not World; TeraSharp serves one planet and has no matching server, so the Arbiter
is the whole of it.

**And it is RAM.** `PartyMatchManager` has no stored procedure of its own - the only party-ish SPs
in the binary are `spIssuePartyInnerId` and a bounty-hunt ranking one. The listing lives in
`PartyMatchManager`'s table plus a per-User cache (`User::CachePartyMatchInfo`,
`GetCachedPartyMatchInfo`, `ClearPartyMatchInfoCache`, Arb_part_028.c), and
`PartyMatchManager::OnLeaveWorld` drops it when the leader logs out. Nothing goes in
`CharacterStore`.

---

## 3. The six client packets

| opcode | packet | frame guard | body |
|---|---|---|---|
| 0xEFD6 | `C_REQUEST_PARTY_MATCH_INFO` | `< 0x14` (Arb_part_041.c:8952) | the filter, below |
| 0xD3D9 | `C_UNREGISTER_PARTY_INFO` | `< 0x14` (:13513) | **the same filter** |
| 0xD902 | `C_REGISTER_PARTY_INFO` | `< 7` (:6053) | `[04] u16 msgOff [06] u8 isRaid` + message |
| 0x6E66 | `C_REQUEST_MY_PARTY_MATCH_INFO` | `< 4` (:8719) | none |
| 0xA7DF | `C_REQUEST_PARTY_MATCH_LINK` | none (:9051) | none |
| 0xFA6D | `C_PARTY_MATCH_WINDOW_CLOSED` | none (:5159) | none |

Guards are FRAME lengths, header included; `PacketDispatcher` compares BODY length, so
`PartyMatchManager.MinBodyLength` is each minus four.

The filter, packet-relative, exactly as both handlers read it:

```
[04] u16 purpose offset   param_2[2], bounds-checked against the packet length
[06] u16 unk1             param_2[3]      0 in every captured frame
[08] u16 minLevel         param_2[4]      15
[10] u16 maxLevel         param_2[5]      25
[12] i32 unk2             *(int *)(param_2 + 6)   3
[16] i32 unk3             *(int *)(param_2 + 8)   0
```

### `C_UNREGISTER_PARTY_INFO.1.def` is wrong

It declares `int32 unk1 / int16 minLevel / int16 maxLevel / int32 unk3 / int32 unk4 / byte / byte`
- no string ref, and an `int32` straddling the two-byte string offset and `unk1`. It happens to
total 18 bytes, which is why it looks plausible against frame 5245. The two handlers settle it:
`Handler_C_UNREGISTER_PARTY_INFO` and `Handler_C_REQUEST_PARTY_MATCH_INFO` read the identical six
fields from the identical offsets and queue the identical job. Read either packet with
`C_REQUEST_PARTY_MATCH_INFO.1`. Same class of trap as the patch-101 `S_FRIEND_LIST` def and the
ten wrong guild defs.

---

## 4. The three replies - and two fields the defs only guess at

All three shipped defs are otherwise RIGHT and are the highest versions present
(`S_SHOW_PARTY_MATCH_INFO.1`, `S_MY_PARTY_MATCH_INFO.2`, `S_PARTY_MATCH_LINK.2`), so no
`V100Definitions` override is needed.

**`S_SHOW_PARTY_MATCH_INFO`** - `[u16 count][u16 offset][i16 pageCurrent][i16 pageCount]`, then
15-byte elements `[u16 here][u16 next][u16 message][u16 leader][i32 leaderId][u8 isRaid]
[i16 playerCount]` with each element's two strings right after it. Frame 5222 is byte-for-byte
that. `pageCount` is 0, so the client never sends `C_REQUEST_PARTY_MATCH_INFO_PAGE`.

**`S_MY_PARTY_MATCH_INFO.unk` is NOT isRaid.** The writer is
`PartyMatchManager::SendPartyPRText(User *, bool, const wchar_t *)` (Arb_part_072.c:2490) and its
only caller `RequestMyPartyInfo` (Arb_part_072.c:523) decides the bool: the no-listing branch
passes `0` with the empty string, the other passes `1` with the stored message. It means **do I
have a listing**.

**`S_PARTY_MATCH_LINK.unk2` is NOT a level.** The def guesses `always 65? Possibly level limit`;
the wire has 20 and the character was level 20, which is a coincidence.
`PartyMatchManager::BroadcastPartyPR(int, User *, const wchar_t *, bool)` (Arb_part_071.c:10081)
uses `0x14` as the **ChatType** in all three branches, including the `S_CANNOT_USE_CHAT_CHANNEL`
(0x6D00) it sends when the channel is blocked. It is the party-matching chat channel.
The first byte (`unk`) is a by-value literal at the call site and is 1 on the wire; the second
(`raid`) is the listing's isRaid, read from match info + 0x98.

### The system messages

| id | when | decompile |
|---|---|---|
| 997 (0x3E5) | listing published | Arb_part_071.c:8141 |
| 994 (0x3E2) | listing withdrawn | Arb_part_072.c:1238 |
| 1582 (0x62E) | link pressed with no listing | Arb_part_072.c:596, in `RequestPartyPR` |
| 2284 (0x8EC) | link pressed while the user flag at +0x3c2a is set | Arb_part_041.c:9059 |
| 2303 (0x8FF) | link pressed inside the cool time | Arb_part_072.c:583 |

---

## 5. The filter is parsed and not applied

A listing carries leaderId, isRaid, playerCount and two strings - **no level**. There is nothing
on the board to compare the 15..25 window against. The real Arbiter matches it against the
LEADER's level, which it has because it holds the `User`; `PartyMatchManager` deliberately holds
no character state, so it parses the filter, carries it, and returns every listing. Showing a few
extra rows is the failure that loses nothing; inventing a level and hiding rows is the one that
does. `unk1`, `unk2`, `unk3` and `purpose` are parsed for the same reason and are not guessed at.

---

## 6. What is not modelled

* **The link BROADCASTS.** `BroadcastPartyPR` puts `S_PARTY_MATCH_LINK` in chat channel 20 for
  everyone in it; `PartyMatchManager` sends it to the requester, which is all a one-client tap can
  prove. The per-user cool time behind it (cool-time slot 0x13; the third press in the capture,
  frame 5230, got `S_MUTE` `14 00 00 00 01` instead of a link) is not modelled either.
* **Applying to a listing.** `PartyMatchManager::ApplyParty`, `DeleteCandidate`,
  `OnApplicationDenied` and `RequestSendCandidateList` are all real methods, and the board's
  "Apply" button is `C_APPLY_PARTY` (0xA889), which `PartyWiring` already owns. **cap_social4
  contains no apply** - the brief described one, but between the listing appearing at 5222 and the
  cancel at 5245 the publisher received only `S_SOCIAL`, `S_PARTY_MATCH_LINK`, `S_MUTE`,
  `S_UPDATE_ACHIEVEMENT_PROGRESS` and `S_QUEST_BALLOON`. Nothing to be byte-exact against.
* **Paging.** `C_REQUEST_PARTY_MATCH_INFO_PAGE` (guard `< 0xe`, Arb_part_041.c:9002) is never sent
  because `pageCount` is 0.
* **`OnPartyJoined` / `OnPartyMemberFull` / `OnPartyDismissed` / `OnPartyManagerChanged`.** The
  real manager reacts to party events; here `playerCount` is read live through
  `PartyMatchManager.PartySize`, which the wiring points at `PartyWiring.Manager`, so the column
  stays right without the callbacks.

---

## 7. The human-owned diff

In `Handlers/HandlerRegistry.cs`, remove `C_REQUEST_PARTY_MATCH_INFO`,
`C_REQUEST_MY_PARTY_MATCH_INFO` and `C_PARTY_MATCH_WINDOW_CLOSED` from the `OnAcceptSilently`
list, and add:

```csharp
foreach (var (matchName, matchOp) in PartyMatchManager.ClientOpcodes)
    Reg(matchName, PartyMatchManager.MinBodyLength(matchOp),
        (s, body) => PartyMatchManager.OnClientPacket(s, matchOp, body));
PartyMatchManager.PartySize = id => PartyWiring.Manager.FindByMember(id)?.Count ?? 1;
```

Nothing in `WorldBridge` (no World frame belongs to the board), and nothing in `WorldEntry` or
`GameSession`: the leave-world drop rides `SocialHandlers.UnregisterChat`, the same edge T49, T51
and T76 ride.
