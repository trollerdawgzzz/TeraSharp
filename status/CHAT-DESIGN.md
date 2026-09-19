# CHAT-DESIGN — whisper and private chat channels are Arbiter-owned and RAM-only

Research and implementation for **T43**. Sources: the ArbiterServer 100.02 decompile
(`D:\v100\TERA_SERVER.100\Arb_part_0*.c`), the `.def` files in `tera_v100_MASTER_FINAL`,
`tera-server-proxy/data/data.json` map `376012`, and `status/arbiter_c_handlers.txt`.

Companion to `status/PARTY-DESIGN.md` and `status/GUILD-DESIGN.md`. Read PARTY-DESIGN first —
the framing conventions (frame-relative vs packet-relative offsets, ref slots, the
`SendToSession<PDL::PKT_*_WRITE,...>` trick) are explained there and are not repeated here.

**No private channel has ever been created against the tap.** No capture contains any of the
packets below. Everything here is decompile-derived and pinned by the codec, not by bytes off a
wire. Section 9 lists what a capture would settle.

---

## 0. The headline

Private channels sit exactly where parties sit and nowhere near where guilds sit:

| | parties | **private channels** | guilds |
|---|---|---|---|
| owned by | Arbiter | **Arbiter** | Arbiter |
| persisted | no | **no** | yes, 7 tables |
| survives restart | no | **no** | yes |
| World told | yes, `AS_`/`SA_` | **never** | yes, mirror |
| addressed by client as | — | **a slot 0..7, not an id** | db id |

The two things worth carrying away:

1. **Channels are RAM. Nothing about them touches SQL** (§3). A restart drops every channel,
   and every logout drops every membership.
2. **The client never names a channel by its id.** `C_CHAT.Type` 0x0B..0x12 and
   `C_LEAVE_PRIVATE_CHANNEL.Index` are the *sender's own slot* in an eight-entry array at
   `User+0x56F8`; the server looks the id up. Only the `S_` direction carries a real id (§5.2).
   Getting this backwards produces a server that works with one channel and silently crosses
   wires with two, which is the kind of bug that takes a week to see.

---

## 1. Opcodes (data.json map `376012`)

| name | opcode | who answers |
|---|---|---|
| `C_WHISPER` | 0xE8E5 | Arbiter |
| `S_WHISPER` | 0x96F1 | Arbiter → client |
| `C_CHAT` | 0xEB77 | Arbiter (routes by `Type`) |
| `S_CHAT` | 0x7D6B | World, via `AS_REQUEST_NORMAL_CHAT` |
| `S_PRIVATE_CHAT` | 0xBD2B | Arbiter → client |
| `C_CREATE_PRIVATE_CHANNEL` | 0xB868 | Arbiter |
| `C_EDIT_PRIVATE_CHANNEL` | 0x7DA0 | Arbiter |
| `C_JOIN_PRIVATE_CHANNEL` | 0x7E16 | Arbiter |
| `C_LEAVE_PRIVATE_CHANNEL` | 0x561E | Arbiter |
| `C_KICK_CHANNEL_MEMBER` | 0xA531 | Arbiter |
| `C_CHANGE_CHANNEL_PASSWORD` | 0x96E4 | Arbiter |
| `C_REQUEST_PRIVATE_CHANNEL_INFO` | 0x73BD | Arbiter |
| `C_REUQUEST_JOINED_CHANNEL_LIST` | 0x7F78 | Arbiter *(the misspelling is the protocol's)* |
| `S_JOIN_PRIVATE_CHANNEL` | 0x81E7 | Arbiter → client |
| `S_LEAVE_PRIVATE_CHANNEL` | 0x67AD | Arbiter → client |
| `S_PRIVATE_CHANNEL_NOTICE` | 0x879A | Arbiter → client |
| `S_REQUEST_PRIVATE_CHANNEL_INFO` | 0x846F | Arbiter → client |
| `S_REQUEST_JOINED_CHANNEL_LIST` | 0x51EC | Arbiter → client |
| `S_CANNOT_USE_CHAT_CHANNEL` | 0x6D00 | Arbiter → client |
| `S_SYSTEM_MESSAGE` | 0xF30E | Arbiter → client |
| `S_CREATE_PRIVATE_CHANNEL` | 0x76E5 | **dead — see §5.4** |
| `C_LIST_CHANNEL` | 0x661B | **no handler anywhere in the binary** |

All 22 were checked against the ids the Arbiter writes into its own packets, the same way T36
checked the 31 guild ones. `376012` is this build; `367081`, which the def folder's README names,
is a different one — see `CLAUDE.md`.

**There is no `S_PRIVATE_CHANNEL_INFO` in this build.** The brief for T43 named one; the reply to
`C_REQUEST_PRIVATE_CHANNEL_INFO` is `S_REQUEST_PRIVATE_CHANNEL_INFO` (0x846F).

---

## 2. What World is told

Nothing. There is no `AS_`/`SA_` pair for private channels, no `AS_` opcode with CHANNEL in its
name, and `ChatManager::ChatMessageHandler` hands a private-channel message straight to
`PrivateChatChannel::BroadcastChatMessage`, which walks the member list and writes
`S_PRIVATE_CHAT` down each member's tunnel. World never learns a private channel exists.

Whisper is the same: `Handler_C_WHISPER` resolves the target through
`UserManager::GetCachedUserWithLock` and writes `S_WHISPER` twice, with no World round trip.

The only chat type that *does* go to World is ordinary say/area chat — `AS_REQUEST_NORMAL_CHAT`
(0x1449), which `Handlers/ChatHandlers.cs` already sends and which is not this document's subject.

---

## 3. Are channels persisted? No — four proofs

1. **No stored procedure.** Every persisted Arbiter subsystem calls a `sp*` name through the ODBC
   wrappers (`spCreateGuild`, `spLeaveGuildMember`, …). Walking the 167 functions reachable from
   `ChatManager::*` and `PrivateChatChannel::*` reaches **no** `SQLBindParameter`, no
   `SQLExecDirect`, and no `sp` string.
2. **No `SDB_`/`DBS_` opcode.** `data/dbproxy_opcodes.txt` has no channel opcode, and nothing in
   the chat call graph reaches the DB-proxy writer `FUN_140350eb0`.
3. **`ChatManager::StartManager` rehydrates nothing.** It arms a 1000 ms timer
   (`ChatManager::CheckEmptyChannel`) and returns. A persisted collection would be loaded here;
   compare the guild path, which issues `SDB_INIT_GUILD_DATA` at exactly this point.
4. **Logout destroys memberships unconditionally.** `AccountManager::DeleteUser` calls
   `User::LeaveAllPrivateChannel` with no condition and no save. A channel whose last member logs
   out is stamped and then erased ten minutes later (§4).

So `World/ChatManager.cs` holds channels in a `Dictionary<int, PrivateChannel>` and touches
`CharacterStore` for exactly two things: resolving a whisper target by name, and reading the
block list.

---

## 4. The channel object

`PrivateChatChannel`, 0xB8 bytes:

| offset | type | field |
|---|---|---|
| +0x78 | `User*` | master — **a live user pointer, so the master is always online** |
| +0x80 | `wchar_t[9]` | name — `wcsncpy_s(ch+0x80, 9, name, -1)`, so 8 chars + NUL |
| +0x92 | `short` | password |
| +0x98 | `int64` | empty-since, unix ms; 0 while occupied |
| +0xA0 | `std::list` | members; node is `{next, prev, User*}` |
| +0xA8 | `size_t` | member count |
| +0xB0 | `int32` | channel id |

Limits, all from the decompile:

- **Ids** are a plain counter at `ChatManager+0xC8`, pre-incremented, so the first id issued is
  **1**. Not a handle, not a hash — a restart starts over at 1.
- **8 channels per user.** `User::CanJoinPrivateChannel` scans exactly eight slots at
  `User+0x56F8`; `-1` means free.
- **Password must be 1000..9999.** `if (8999 < (ushort)(password - 1000U))` → system message
  0x3C2. A four-digit number, nothing else.
- **Member cap** is `DAT_140e315a8`, whose only writer is the QA command
  `ArbiterQACommandHandler::SetMaxPrivateChatChannelMemberCount`, clamped to 1..99. The
  compiled-in default lives in `.data` and is not visible in the decompile, so
  `ChatManager.MaxMembersPerChannel = 24` is **ours** and is a settable property.
- **Empty channels die after 600000 ms.** `ChatManager::CheckEmptyChannel`, on the 1 s timer.
  `ChatManager.SweepEmptyChannels(nowMs)` is that, made pure so the tests own the clock.

The master leaving is the one rule the decompile does **not** settle: `S_PRIVATE_CHANNEL_NOTICE`
has an event for it (0xE01), so the real server clearly promotes somebody, but the choice of
successor is not visible. Ours promotes the oldest remaining member.

---

## 5. Layouts

### 5.1 The table

Offsets are **packet-relative** (the `[u16 len][u16 opcode]` header included, so the first ref
slot is at 0x04). "fixed" is the fixed part, i.e. where variable data starts — and it is also the
minimum TOTAL packet length the real handler's `GET_CLIENT_BUFFER_BUFSIZE_MISMATCH` guard
enforces for the `C_` rows.

| packet | op | fixed | layout |
|---|---|---|---|
| `C_WHISPER` | 0xE8E5 | 0x08 | ref To@04, ref Talk@06 |
| `S_WHISPER` | 0x96F1 | 0x15 | ref FromName@04, ref To@06, ref Talk@08, u64 FromGameId@0A, bool IsWorldEventTarget@12, bool IsAdmin@13, bool IsExistingUser@14 |
| `C_CHAT` | 0xEB77 | 0x0A | ref Talk@04, i32 Type@06 |
| `S_PRIVATE_CHAT` | 0xBD2B | 0x14 | ref FromName@04, ref Talk@06, i32 ChannelId@08, u64 FromGameId@0C |
| `C_JOIN_PRIVATE_CHANNEL` | 0x7E16 | 0x08 | ref Name@04, u16 Password@06 |
| `S_JOIN_PRIVATE_CHANNEL` | 0x81E7 | 0x12 | arr UserList count@04/off@06, ref Name@08, i32 Index@0A, i32 ChannelId@0E; element `{i32 UserDbId}`, stride 8 |
| `C_LEAVE_PRIVATE_CHANNEL` | 0x561E | 0x06 | i16 Index@04 — **a SLOT** |
| `S_LEAVE_PRIVATE_CHANNEL` | 0x67AD | 0x08 | i32 ChannelId@04 |
| `C_REQUEST_PRIVATE_CHANNEL_INFO` | 0x73BD | 0x08 | i32 ChannelId@04 |
| `S_REQUEST_PRIVATE_CHANNEL_INFO` | 0x846F | 0x0F | arr FriendList count@04/off@06, arr MemberList count@08/off@0A, bool IsMaster@0C, u16 Password@0D |
| `S_PRIVATE_CHANNEL_NOTICE` | 0x879A | 0x0E | ref Value@04, i32 ChannelId@06, i32 SysMsgId@0A |
| `C_KICK_CHANNEL_MEMBER` | 0xA531 | 0x08 | ref UserName@04, i16 Index@06 — **corrected, see 5.3** |
| `C_CHANGE_CHANNEL_PASSWORD` | 0x96E4 | 0x0A | i16 Index@04, i16 Current@06, i16 New@08 |
| `C_CREATE_PRIVATE_CHANNEL` | 0xB868 | 0x0C | arr Members count@04/off@06, ref Name@08, u16 Password@0A |
| `C_EDIT_PRIVATE_CHANNEL` | 0x7DA0 | 0x0C | same as create |

Every fixed size was read off a dumper guard (`if (0xNN < param_2)` → fixed part `0xNN + 1`) and
then reproduced through the codec — `Chat_min_client_lengths_match_the_handler_guards` and
`Chat_corrected_defs_land_on_the_decompiled_offsets`.

### 5.2 The slot, not the id

`ChatManager::ChatMessageHandler`, for `0xb <= type <= 0x12`:

```
iVar6 = User::GetPrivateChannelId(user, type - 0xb);
```

`User::GetPrivateChannelId(user, i)` returns `*(int *)(user + 0x56f8 + i*4)`, i.e. the i-th of
eight slots, `-1` when free. So:

- `C_CHAT.Type` 0x0B..0x12 → **slot** 0..7 of the sender.
- `C_LEAVE_PRIVATE_CHANNEL.Index`, `C_KICK_CHANNEL_MEMBER.Index`,
  `C_CHANGE_CHANNEL_PASSWORD.Index` → the same slot space. The `.def` comment "0-7" is right.
- `S_JOIN_PRIVATE_CHANNEL` carries **both**: `Index` is the recipient's slot, `ChannelId` is the
  real id.
- `S_PRIVATE_CHAT`, `S_LEAVE_PRIVATE_CHANNEL` and `S_PRIVATE_CHANNEL_NOTICE` carry the real id
  only.

Two users in the same channel will normally hold it in **different** slots.
`Chat_chat_type_is_a_slot_not_a_channel_id` fills all eight slots of one character and checks
that 0x0B+i selects channel i+1; `Chat_join_tells_the_joiner_then_every_member` checks that a
third character joining an existing channel still gets *its own* slot 0.

`CheckChannelLimitLevel` rejects `0xDC <= type`, so the ChatType space is 0..0xDB.

### 5.3 `.def` corrections (`ChatPackets.CorrectedDefs`)

**`C_KICK_CHANNEL_MEMBER.1.def`** declares

```
string   index
string   userName
```

which is wrong twice: the order is reversed, and `index` is not a string. The handler reads a
name ref at packet 0x04 and a **raw i16** slot at 0x06. Both defs happen to compute a 0x08 fixed
part, so this is the worst kind of wrong — it encodes and decodes without complaint and puts the
name where the slot goes.

**`S_JOIN_PRIVATE_CHANNEL.2.def`**'s `array unk` has **no element type**, so `DefinitionWriter`
emits an element header and no payload (a 4-byte stride). The real element is a single
`int32 UserDbId` → stride 8. The corrected def also names the fields.

### 5.4 Byte-correct but renamed (`ChatPackets.NamedDefs`)

`S_WHISPER.3.def`, `S_PRIVATE_CHAT.1.def`, `S_PRIVATE_CHANNEL_NOTICE.2.def` and
`S_REQUEST_PRIVATE_CHANNEL_INFO.2.def` all produce exactly the right bytes; their field names are
just not the Arbiter's (`gm`/`founder` for `IsAdmin`/`IsExistingUser`, `event` for `SysMsgId`,
`owner` for `IsMaster`, and so on). They are re-stated with the dumper's names so the handlers
read as English. `Chat_named_defs_are_byte_identical_to_the_shipped_ones` writes the same values
through both and compares bytes, so the rename can never quietly become a change.

Two more files are wrong but not implemented:

- **`S_REQUEST_JOINED_CHANNEL_LIST.1.def`** says "no fields". It really has a 0x08 fixed part and
  a `ChannelInfo` array of `{i16 Index, i16 Password, i32 MemberCount, wstring MasterName}`.
  Documented here; `C_REUQUEST_JOINED_CHANNEL_LIST` has no handler in `ChatManager` yet.
- **`S_CREATE_PRIVATE_CHANNEL.1.def`** is also wrong, and it does not matter: **the packet is
  dead in 100.02.** It has a dumper and a size validator, but the binary contains no construction
  site for it. A create is answered with `S_PRIVATE_CHANNEL_NOTICE(0xDFE)` followed by
  `S_JOIN_PRIVATE_CHANNEL`. `Chat_create_emits_the_created_notice_then_the_join` pins that.

### 5.5 Whisper sends S_WHISPER twice

`ChatManager::ProcessWhisperMessage` writes the packet to the receiver **and** back to the
sender, and **both copies carry the sender** as `FromName`/`FromGameId` and the receiver as `To`.
The client uses the direction to decide which window the line belongs in. One field dictionary
therefore serves both sends; `Chat_whisper_reaches_the_receiver_then_echoes_to_the_sender`
asserts the two actions share it by reference, so a future "fix" that swaps the names on the echo
fails loudly.

---

## 6. System messages and notices

`S_SYSTEM_MESSAGE` (0xF30E) is one wide string: `@<id>` then `\v`-separated key/value pairs, the
same encoding `SocialHandlers.Smt` already produces.

| id | dec | when |
|---|---|---|
| 0x6F | 111 | whispering yourself |
| 0x71 | 113 | restricted user *(the existing `SocialHandlers.OnWhisper` uses this for blocks)* |
| 0x116 | 278 | kicking yourself |
| 0x33F | 831 | whisper target not found / offline |
| 0x3C0 | 960 | channel name taken |
| 0x3C2 | 962 | password outside 1000..9999 |
| 0x3C3 | 963 | no such channel (join / leave / password) |
| 0x3C4 | 964 | no such channel (chat) |
| 0x3C5 | 965 | kick target is not a member |
| 0x3C6 | 966 | wrong password |
| 0x3C7 | 967 | already in eight channels |
| 0x3C8 | 968 | not the channel master |
| 0x3C9 | 969 | channel is full |
| 0x3CA | 970 | joined — `_ChannelName` |
| 0x3CB | 971 | kicked — `_ChannelName` |
| 0x3CC | 972 | left — `_ChannelName` |
| 0x3D1 | 977 | that slot is not joined |
| 0x3D3 | 979 | password changed — `_ChannelName` |
| 0x53A | 1338 | the target has blocked you — `UserName` |
| 0x5B7 | 1463 | you have blocked the target — `UserName` |

`S_PRIVATE_CHANNEL_NOTICE.SysMsgId` is a separate, smaller space — the four channel events:

| id | event |
|---|---|
| 0xDFE | channel created |
| 0xDFF | member joined |
| 0xE00 | member left |
| 0xE01 | new master |

---

## 7. T43 — what was implemented, and the wiring the human has to do

`World/ChatManager.cs` (new, Cowork-editable by the T43 brief) holds two things: `ChatPackets`
(opcodes, handler minimum lengths, eight parsers, the field dictionaries, and the two `.def`
dictionaries) and `ChatManager` itself. They share a file because the whole subsystem is one
file's worth of work and `DbProxyStaticData.cs`, where `PartyPackets` and `GuildPackets` live, is
already 130 KB.

`ChatManager` is pure and session-agnostic, `PartyManager`-shaped:

```
OnClientPacket(characterId, opcode, body) -> ArbiterActions
Unregister(characterId)                   -> ArbiterActions
```

`ArbiterActions` and `ClientPacket` are new in `World/ActionDispatcher.cs` — the ready-made
`IArbiterActions`/`IArbiterClientAction` pair that T41 deliberately did not add, because at the
time only `PartyActions` and `GuildActions` existed and both already had their own. Any subsystem
written after T43 should use these rather than add a fourth.

Answered: `C_WHISPER`, `C_CHAT` (private types only), `C_CREATE_PRIVATE_CHANNEL`,
`C_JOIN_PRIVATE_CHANNEL`, `C_LEAVE_PRIVATE_CHANNEL`, `C_KICK_CHANNEL_MEMBER`,
`C_CHANGE_CHANNEL_PASSWORD`, `C_REQUEST_PRIVATE_CHANNEL_INFO`.

Recipients are **character db ids**, which is the dispatcher's second resolver — the same one
`GuildHandlers` uses.

### 7.0 `Program.cs` — the dispatcher, extended by one clause

The block is already in `status/PARTY-DESIGN.md` §10 and `status/GUILD-DESIGN.md` §10. Chat adds
its own corrections to the resolver chain and nothing else:

```csharp
        { ResolveDef = n => GuildHandlers.ResolveDef(null, n) ?? ChatManager.ResolveDef(null, n) };
```

`ResolveDef(null, name)` returns the overrides and nothing else, so every other packet still
takes the ordinary shipped-registry path. Without the chat clause a `S_JOIN_PRIVATE_CHANNEL`
would go out with a 4-byte element stride and the client would read garbage member ids.

### 7.1 `Handlers/HandlerRegistry.cs` — the eight packets

```csharp
        var chat2 = new ChatManager(Program.Store!, loggerFactory.CreateLogger<ChatManager>());
        foreach (var name in new[]
        {
            "C_WHISPER", "C_CHAT", "C_CREATE_PRIVATE_CHANNEL", "C_JOIN_PRIVATE_CHANNEL",
            "C_LEAVE_PRIVATE_CHANNEL", "C_KICK_CHANNEL_MEMBER", "C_CHANGE_CHANNEL_PASSWORD",
            "C_REQUEST_PRIVATE_CHANNEL_INFO",
        })
        {
            if (!opcodes.TryGetCode(name, out ushort op)) { log.LogError("{Name} not in opcode map", name); continue; }
            dispatcher.Register(op, name, ChatPackets.MinClientLength(op), (s, body) =>
            {
                actions.Dispatch(chat2.OnClientPacket((int)s.PlayerId, op, body.ToArray()), "chat");
                return true;
            });
        }
```

`C_CHAT` is the one that needs thought: `ChatManager` answers **only** types 0x0B..0x12 and
rejects everything else with a reason. `ChatHandlers.OnChat` is what handles say/area/trade today
and must keep doing so. Two ways to do it, and the second is the right one:

- register `C_CHAT` to `ChatManager` and have the rejection fall through — **no**, the rejection
  would be relayed to the client as a system message;
- keep `ChatHandlers.OnChat` registered for `C_CHAT` and have it hand the private range over:

```csharp
        // in ChatHandlers.OnChat, right after `channel` is read:
        if (ChatManager.IsPrivateChatType((int)channel))
        {
            Program.Actions?.Dispatch(Program.Chat!.OnClientPacket((int)s.PlayerId,
                ChatPackets.C_CHAT, body.ToArray()), "chat");
            return true;
        }
```

so drop `"C_CHAT"` from the list above and add the three lines to `ChatHandlers.OnChat`.
`ChatHandlers.cs` is Cowork-editable, so that change can be made here whenever the human wants
it; it is **not** made yet, because it needs `Program.Chat` and `Program.Actions` to exist first
and `Program.cs` is human-owned.

### 7.2 The whisper swap — the one existing handler this replaces

The T43 brief called it `ChatHandlers.OnWhisper`. It is actually **`SocialHandlers.OnWhisper`**
(`Handlers/SocialHandlers.cs:855`), registered at `Handlers/HandlerRegistry.cs:111`:

```csharp
        Reg("C_WHISPER", 4, social.OnWhisper);
```

It works and it stays until the human swaps it. What changes when they do:

| | `SocialHandlers.OnWhisper` (today) | `ChatManager.Whisper` (T43) |
|---|---|---|
| target lookup | `SocialHandlers.Sessions[name]` — a name → session map | `CharacterStore.GetCharacterByName` then the manager's own online map |
| min length | `Reg(..., 4, ...)` | `ChatPackets.MinClientLength` → 0x08, the handler's real guard |
| self-whisper | SMT 0x6F | SMT 0x6F — same |
| offline / unknown | SMT 0x33F | SMT 0x33F — same |
| **blocked** | SMT 0x71 for both directions | **SMT 0x53A / 0x5B7**, each with a `UserName` parameter |
| send order | sender first, then target | **target first, then the sender's echo** |
| def | shipped `S_WHISPER.3.def` field names | `NamedDefs`, byte-identical (§5.4) |

The block messages are the real difference: 0x71 is "restricted user", which is what the real
Arbiter uses for a *chat-restricted* account, not for a block. Swapping is one line —
drop `Reg("C_WHISPER", 4, social.OnWhisper);` and let the loop in §7.1 register it — but it
changes what a blocked user sees, so it is called out rather than done quietly.

`SocialHandlers.OnWhisper` also needs its `Sessions` map only for this; nothing else in T43
depends on it.

### 7.3 Login and logout

```csharp
        // after C_LOAD_TOPO_FIN, where the friend and block lists already go:
        Program.Chat?.Register(new ChatPlayer((int)chr.Id, chr.Name, s.GameId, chr.Level, chr.Class, false));

        // wherever the session is torn down:
        if (Program.Chat != null) Program.Actions?.Dispatch(Program.Chat.Unregister((int)chr.Id), "chat");
```

`Unregister` is what makes §3's fourth proof true: it leaves every channel the character was in,
announcing each one, which is `User::LeaveAllPrivateChannel`.

And the 1 s timer, wherever the Arbiter's housekeeping tick lives:

```csharp
        Program.Chat?.SweepEmptyChannels(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
```

---

## 8. Tests

16 `Chat_*` tests in `src/TeraSharp.Arbiter.Tests/Program.cs`. The layout ones
(`Chat_named_defs_*`, `Chat_corrected_defs_*`) need `tera_v100_MASTER_FINAL` and skip without it;
the behaviour ones need nothing.

Every behaviour test reads `ArbiterActions.Ordered`, because emission order is the thing
`ActionDispatcher` preserves and the thing a future refactor is most likely to break. The three
the brief asked for:

- **whisper** — `Chat_whisper_reaches_the_receiver_then_echoes_to_the_sender` plus
  `Chat_whisper_rejects_self_offline_unknown_and_blocked` (five rejection paths).
- **join/leave** — `Chat_create_emits_the_created_notice_then_the_join`,
  `Chat_join_tells_the_joiner_then_every_member`,
  `Chat_leave_tells_the_leaver_then_the_remaining_members`,
  `Chat_master_leaving_promotes_the_oldest_member`.
- **channel chat to three members** — `Chat_channel_chat_reaches_three_members_with_the_real_id`.

`Chat_every_emitted_packet_encodes_through_the_codec` walks an eleven-step scenario and writes
every emitted packet through `DefinitionWriter` and back through `DefinitionReader` — 25 packets,
7 distinct names, all seven asserted present.

**No build was run.** There is no `dotnet` in the Cowork container. Everything above was verified
by transliterating `DefinitionParser`/`DefinitionWriter` and `ChatManager` into Python and
running the same scenarios there (`defmodel.py`, `sim.py`, `golden.py` in the scratchpad): the
four `NamedDefs` come out byte-identical to the shipped files, all 14 layouts land on the offsets
in §5.1, and the golden scenarios produce the packet sequences the C# tests assert. The human
still has to build.

---

## 9. Open — what a capture would settle

- **The member cap.** `DAT_140e315a8`'s compiled-in default is not in the decompile.
  `ChatManager.MaxMembersPerChannel` is 24 and settable.
- **The invite list.** `C_CREATE_PRIVATE_CHANNEL` and `C_EDIT_PRIVATE_CHANNEL` both carry an
  array of user db ids. `ChatManager` validates it against the cap and otherwise ignores it: the
  packet the real Arbiter sends each invitee is not identified, so invitees join by name and
  password like anyone else. `C_EDIT_PRIVATE_CHANNEL` has no handler for the same reason.
- **Master promotion.** The decompile has the 0xE01 event but not the rule. Ours promotes the
  oldest remaining member.
- **`S_REQUEST_PRIVATE_CHANNEL_INFO.FriendList`** is the online-friends picker the create dialog
  shows. It is emitted empty; filling it means joining `ChatManager` to `SocialHandlers`, which
  is a wiring decision, not a protocol one.
- **`C_REUQUEST_JOINED_CHANNEL_LIST` / `S_REQUEST_JOINED_CHANNEL_LIST`** — the real layout is in
  §5.4 but no handler is written.
- **`S_CANNOT_USE_CHAT_CHANNEL` (0x6D00)** — `CheckChannelLimitLevel` can send it (a level gate on
  some channels); the gate's table is not read yet.
- **Whether `IsExistingUser` and `IsWorldEventTarget` are ever true.** Both go out false. The
  existing `SocialHandlers.OnWhisper` does the same and nobody has complained.

## T94 — the private channels against cap_final

T43 built the whole subsystem from the decompile. cap_final_client3 / _client4 are the first
bytes, and they confirm every def and correct three values.

| frame | packet | verdict |
|---|---|---|
| c3 938 | `S_REQUEST_PRIVATE_CHANNEL_INFO` defaults | **isMaster was wrong** — see below |
| c4 3485 | `S_REQUEST_PRIVATE_CHANNEL_INFO` with data | def right, **writer order matters** |
| c3 963, 4664 | `S_JOIN_PRIVATE_CHANNEL` | def right, **userList was wrong** |
| c3 965, c4 4161 | `S_PRIVATE_CHANNEL_NOTICE` | def right |
| c3 4717 | `S_LEAVE_PRIVATE_CHANNEL` | right |
| c4 3967, 4111 | `C_EDIT_PRIVATE_CHANNEL` | shares C_CREATE’s layout; **now handled** |

### T94.1 Ref-slot order and data order are not the same order

`S_REQUEST_PRIVATE_CHANNEL_INFO` declares `ref friendList` then `ref memberList`, but its array
BLOCKS are declared memberList first. Frame 3485 settles which governs what:

```
01 00 39 00   slot 1 = friendList : count 1, offset 57
02 00 0F 00   slot 2 = memberList : count 2, offset 15
...           data at 15 is the TWO-member roster; data at 57 is the ONE friend
```

So **the slots follow the `ref` lines and the data follows the array blocks**. A writer that
emitted data in ref order would swap the two sections and still produce a packet that parses.
`DefinitionWriter` already does this correctly — it walks its data fields in field order,
independently of the ref slots — but nothing had ever pinned it, and a hand model written from
the T78 note ("headers in field-appearance order", which is about the IMPLICIT case) gets it
wrong. Pinned by `T94_private_channel_info_matches_cap_final`.

### T94.2 Three corrections

* **`isMaster` is 1 in the defaults reply.** The create dialog asks with channelId = -1 and
  frame 938 answers `00 00 00 00 00 00 00 00 01 E8 03`. T43 sent 0. It reads as "you would be
  the master of the channel you are about to make".
* **`S_JOIN_PRIVATE_CHANNEL.userList` is always empty.** All six captured frames (c3 963, 3725,
  4126, 4664; c4 3464, 4558) carry `00 00 00 00`, including 4664 where the channel already had
  two members. T43 filled it from the roster because the def has the array. The roster reaches
  the client through `S_REQUEST_PRIVATE_CHANNEL_INFO` instead, so the parameter is gone.
* **`C_EDIT_PRIVATE_CHANNEL` (0x7DA0) now has a handler.** It is byte-identical to
  `C_CREATE_PRIVATE_CHANNEL` and shares its parser — T43 had already noted that. The frame
  carries **no channel id**, so the target is the channel the caller masters. No reply: frame
  3967 is answered by silence, and the notices at 4112 / 4161 belong to a member joining and
  leaving, not to the edit.

### T94.3 Still open

`friendList` is **populated on the wire** (c4 3485 one entry, c4 4105 two) and we still send it
empty. Its element is `[u16 charNameOff][u32 userDbId][u32 userClass][u32 level][u32 groupId]`
plus the name, and the def for that is right. Filling it needs the chat layer wired to the
friend list; what the entries actually select is not settled by these two frames, so nothing is
invented here.

## T96 — the private channels are registered at last

`ChatManager` has answered eight client packets since T43, but **nothing ever registered them**,
so every one fell through `PacketDispatcher`’s forward-to-World path to a server with no handler
for any of them. They are now on `ArbiterClientHandlers.ArbiterOwned` and the manager exposes the
`PartyMatchManager` surface:

```
ChatManager.ClientOpcodes            (Name, Opcode)[] - the eight, in registration order
ChatManager.MinBodyLength(op)        ChatPackets.MinClientLength minus the 4-byte header
ChatManager.IsArbiterSide(op)        is it one of the eight
ChatManager.OnClientPacket(session, op, body)   static, dispatches through ActionDispatcher
ChatManager.Instance                 the live manager the static entry uses
```

`C_WHISPER` and `C_CHAT` are deliberately **not** in the list: they stay with `SocialHandlers`.
`C_REUQUEST_JOINED_CHANNEL_LIST` (0x7F78 — the misspelling is the real one) had no case at all;
it is now accepted and answered with nothing, because nothing in cap_final asks for it and the
reply shape is therefore unknown. Registering it is still the point: it keeps it off the World link.

### T96.1 Three T43 expectations moved to the captured behaviour

Code unchanged; only the tests. `Chat_join_tells_the_joiner_then_every_member` now asserts an
**empty** `userList`; `Chat_channel_info_answers_the_create_dialog_defaults_for_minus_one` asserts
`isMaster` is **1**; and `Chat_every_emitted_packet_encodes_through_the_codec` checks the 0x12
fixed part at the **name** ref (body[4]) with the list words at body[0..3] both zero — the same
fact, moved to the word that still carries it now the list is empty. See T94.2 for the bytes.
