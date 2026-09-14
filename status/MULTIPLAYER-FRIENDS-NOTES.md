# Multiplayer GÇö what is stubbed, and what it will take

TeraSharp has only ever been driven by one client at a time. Everything below is written,
parked or deliberately simplified because a second player has never existed in a capture.
This file is the single place those stubs point at; each one names what it needs.

**Status:** design notes only. Nothing here is implemented.

---

## 1. The tunnel

`WorldBridge.HandleFrame` has a single-player fast path: with exactly one registered session,
every frame from World goes to that session without looking at the routing key. Multi-player
routing needs the per-user key (`GameSession.TunnelKey` / the gameId World stamps into pushes)
to select the session, and a capture with two logins to prove which frames carry which id.
This is the blocker the status table has called "blocked on a two-login capture" since T10.

## 2. Social (T30)

Written, but only as live as the session registry (`SocialHandlers.Sessions`, name -> session):

| stub | what it needs |
|---|---|
| `S_CHANGE_FRIEND_STATE` (0xE887) on login/logout | a call to `SocialHandlers.NotifyFriendsOfState(s, 0)` after the friend list is sent and `(s, 2)` on the way out. Both sites are in human-owned files (`HandlerRegistry` / `GameSession`). `User::ChangeFriendStateWithLock` only notifies rows whose relation is 0, which `NotifyFriendsOfState` already does. |
| `AS_ADD_BLOCKED_USER` (0x1475), `AS_REMOVE_BLOCKED_USER` (0x1476) | one `Program.World?.SendFrame(...)` each. World uses the block list to suppress invites and whispers it routes itself; with one player nothing observable changes. Layouts: `AS_ADD_BLOCKED_USER.1.def`, `AS_REMOVE_BLOCKED_USER.1.def`. |
| the login block-id push (0x1474) | `User::SendBlockedUserListNoLock` sends it only when the list is non-empty (Arb_part_029.c:19498). Same shape as above. |
| `lastOnline` in `S_FRIEND_LIST` | today: 0 for an online friend, seconds-since-`last_logout` otherwise. The real Arbiter diffs two timestamps and the client shows "last seen". Good enough until two players exist. |
| whisper | already routes through `Sessions` and is block-aware; it is the one social feature that will work the day a second session connects. |

## 3. What a two-login capture must contain

To unblock both of the above in one pass:

1. Two clients logged in at once, both spawned in the same zone.
2. One whisper each way.
3. One friend request, accepted, then deleted (so `S_CHANGE_FRIEND_STATE` appears with a real
   state value on login, logout and zone change).
4. One block and unblock while both are online (so the `AS_*` block pushes are on the wire).
5. Both clients zoning at the same time, to see how World's per-user frames interleave and which
   field the Arbiter uses to demultiplex them.

Capture both sides: the Arbiter<->World tap (`arb_world*.log`) AND the client tap
(`cap_*_client.log`); the social packets only exist on the client side.
