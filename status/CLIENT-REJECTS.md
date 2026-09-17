# CLIENT-REJECTS — the client packets we forward that World throws away

Research and implementation for **T45**. Sources: the ArbiterServer 100.02 decompile
(`D:\v100\TERA_SERVER.100\Arb_part_0*.c`), `tera-server-proxy/data/data.json` map `376012`,
the `.def` files in `tera_v100_MASTER_FINAL`, and `D:\packetlogs\{arb_world,cap_newchar,
cap_newchar_client}.log`.

A live WorldServer prints

```
handler has not been implemented yet!!! <opcode> <len>
```

for every client packet TeraSharp forwards that it has no handler for. Eighteen opcodes showed up.
**All eighteen are the Arbiter's** — every one has a `Handler_C_*` in ArbiterServer.exe, and
several read state only the Arbiter has. Forwarding them was never going to work.

---

## 0. Two corrections to the brief, before anything else

**The numbers are TOTAL packet lengths, not body lengths.** `23377/12` is `C_REQUEST_GUILD_INFO`
and its handler's guard is `param_3 < 0xc` — 12 exactly. `57252/16` is `C_VISIT_NEW_SECTION`,
guard `0x10`. `27265/4` is `C_SERVER_TIME`, whose shipped def is empty. Three independent
confirmations; World is printing the packet's own length field. This matters for the registry
diff in §7, because `PacketDispatcher` compares against a **body** length.

**Two different bugs produce that one log line.** Twelve of the eighteen were simply unregistered,
so `PacketDispatcher`'s "forward unregistered opcodes to World when in-world" fallback sent them
on. The other six **were registered** — through `HandlerRegistry`'s `RegNoop` / `RegEmptyReply`
helpers, both of which begin

```csharp
if (s.InWorld) { Forward(s, op, body); return true; }
```

so they answer correctly standalone and forward in-world. That hedge made sense when nobody knew
who owned these packets. It is wrong for all six.

---

## 1. The eighteen, named

`data.json` map `376012`, each cross-checked against the Arbiter's own opcode→name switch
(`Arb_part_003.c`) — `0x75c8` really does return `"C_SHOW_ITEM_TOOLTIP_EX"` there, so the map is
this build's.

| dec | hex | name | observed len | handler | why World rejects it |
|---|---|---|---|---|---|
| 30152 | 0x75C8 | `C_SHOW_ITEM_TOOLTIP_EX` | 54, 62 | `FUN_1404edab0` | needs the **item table** |
| 43407 | 0xA98F | `C_SAVE_CLIENT_UI_SETTING` | 115–172 | `FUN_1404ec8b0` | client settings are Arbiter-stored |
| 61398 | 0xEFD6 | `C_REQUEST_PARTY_MATCH_INFO` | 22 | `FUN_1404e9c20` | the match pool is the Arbiter's |
| 23377 | 0x5B51 | `C_REQUEST_GUILD_INFO` | 12 | `FUN_1404e7e80` | needs the **guild rows** |
| 28262 | 0x6E66 | `C_REQUEST_MY_PARTY_MATCH_INFO` | 4 | `FUN_1404e9670` | as above |
| 64109 | 0xFA6D | `C_PARTY_MATCH_WINDOW_CLOSED` | 4 | `FUN_1404e42c0` | as above |
| 33506 | 0x82E2 | `C_EVENT_GUIDE` | 4 | `FUN_1404e0020` | the event manager is the Arbiter's |
| 57252 | 0xDFA4 | `C_VISIT_NEW_SECTION` | 16 | `FUN_1404f1f20` | needs the **visited-section list** |
| 46956 | 0xB76C | `C_UPDATE_CONTENTS_PLAYTIME` | 24–72 | `FUN_1404f09b0` | playtime accounting is per-account |
| 39349 | 0x99B5 | `C_CLIENT_LOG` | 644–2894 | `FUN_1404dd9c0` | it is a log sink, and the Arbiter owns the log |
| 60155 | 0xEAFB | `C_TRADE_BROKER_HIGHEST_ITEM_LEVEL` | 4 | `FUN_1404ef750` | the broker is the Arbiter's |
| 27265 | 0x6A81 | `C_SERVER_TIME` | 4 | `FUN_1404ed1d0` | trivially the Arbiter's clock |
| 54263 | 0xD3F7 | `C_DUNGEON_COOL_TIME_LIST` | 4 | `FUN_1404df1f0` | needs the **cooldown rows** |
| 60477 | 0xEC3D | `C_VIEW_BATTLE_FIELD_RESULT` | 30 | `FUN_1404f1860` | battleground records are the Arbiter's |
| 33239 | 0x81D7 | `C_REQUEST_CANDIDATE_LIST` | 4 | `FUN_1404e61a0` | the lord election is the Arbiter's |
| 34411 | 0x866B | `C_REQUEST_GUILD_LIST` | 24 | `FUN_1404e87a0` | needs the **guild rows** |
| 53135 | 0xCF8F | `C_SHOW_AWESOMIUMWEB_SHOP` | 4 | `FUN_1404ed790` | the shop URL is Arbiter config |
| 22631 | 0x5867 | `C_RESET_ALL_DUNGEON` | 4 | `FUN_1404eb590` | the party vote is the Arbiter's |

Four of them ask for something **only the Arbiter has a table for**, which is the sharpest way to
put "World rejects it": there is no code World could run.

---

## 2. `C_SHOW_ITEM_TOOLTIP_EX` — the potion counter

### 2.1 What it is

Not what the name suggests at a glance. `Handler_C_SHOW_ITEM_TOOLTIP_EX` (`FUN_1404edab0`,
`Arb_part_041.c:11581`) resolves the item's owner — by name when the request carries one,
otherwise by `ItemOwnerDbId` — and calls one of

```
bool User::ShowItemToolTip(class User *, enum RequestToolTipType, __int64, __int64)
bool User::SendItemToolTipLock(class User *, enum RequestToolTipType, __int64, __int64, struct CompareItemToolTip *)
```

both of which end in `User::SendToClientItemToolTip`, i.e. **S_SHOW_ITEM_TOOLTIP built from the
item's DB row**.

The request names an **`ItemDbId`**. Item db ids are the Arbiter's to allocate
(`INVENTORY-DESIGN.md` §7 — `items.item_db_id`, handed to World in the `0x2769` echo). World has
no item table at all, which is precisely why it prints "handler has not been implemented yet!!!".

### 2.2 Why the count never moved

`S_SHOW_ITEM_TOOLTIP` carries **`Count`** and **`SavedCount`** — the stack size — alongside
`InvenType`, `TabIndex` and `InvenPos`, the slot it belongs to. The client asks for the tooltip of
the item it just used, by db id, and the reply is what refreshes its cached copy of that row.
Never answering leaves the cached copy stale.

The persistence side was already right, and that is worth stating plainly so the next person does
not go looking there: `SDB_ITEM_SINGLE` (0x2768) is handled, the amount atoms are applied to the
`items` rows, and the `0x2769` echo carries the atoms back with allocated ids (T13/T44). The user
observed exactly that — "the atoms reach us and are stored". The gap was one unanswered client
packet.

### 2.3 The layouts

**`C_SHOW_ITEM_TOOLTIP_EX` (0x75C8)**, from the PDL dumper `FUN_1401e2680`
(`Arb_part_014.c:14071`), guard `0x25 < param_2` → fixed part **0x26**:

| packet offset | type | field |
|---|---|---|
| 0x04 | ref | `ItemOwnerName` |
| 0x06 | i32 | `ToolTipType` |
| 0x0A | i64 | `ItemDbId` |
| 0x12 | i64 | `ContentId` |
| 0x1A | i32 | `CompareTemplateId` |
| 0x1E | i32 | `ItemOwnerPlanetId` |
| 0x22 | i32 | `ItemOwnerDbId` |

The two live lengths, 54 and 62, leave 16 and 24 bytes of string — a 7- and an 11-character owner
name. That is the player's own character name, which is what the handler's
`FUN_14082dc50(UserManager, name, 3)` lookup expects.

**`S_SHOW_ITEM_TOOLTIP` (0x5646)** has **no `.def` file in `tera_v100_MASTER_FINAL`** — neither
half of the pair does — so the whole layout comes from the dumper `FUN_1402da6b0`
(`Arb_part_023.c:~800`), guard `0x12f < param_2` → fixed part **0x130**.

Ref block, 9 slots at 0x04..0x15, read by the dumper at `puVar14[3]/[5]/[7]/[9]/[10]`:

| slots | field | element |
|---|---|---|
| 0x04 / 0x06 | `CustomizingItemList` | `{i32 CustomizingItemTemplateId}` |
| 0x08 / 0x0A | `OptionSetInfoList` | `{i32 CurOptionIndex, i32 MasterpieceStatReviseIndex, f32 ItemLevel, f32 ItemMinLevel, f32 ItemMaxLevel}` |
| 0x0C / 0x0E | `CombinePassiveList` | `{i32 PassiveId}` |
| 0x10 / 0x12 | `CompareStatSetInfoList` | 24 × i32 (`CurCompareStatIndex` … `CompareDecreasePassive4`) then 8 × i64 remain-times |
| 0x14 | `ItemBoundOwner` | wstring |

then fifty fixed fields from 0x16 to 0x12F. **The reason to trust the reconstruction** is that
they tile that range with no gap and no overlap and the last one, `Damaged`, sits at 0x12F —
exactly one byte short of the guard. `Tooltip_reply_fixed_part_ends_exactly_on_the_dumper_guard`
walks all fifty and fails on a single mis-read offset. The ones that matter:

| offset | field | |
|---|---|---|
| 0x16 | `ToolTipType` | echoed from the request |
| 0x1A | `ItemDbId` | i64 |
| 0x22 | `TemplateId` | |
| 0x2E | `OwnerDbId` | i64 |
| 0x36 | `InvenType` | the pocket |
| 0x3E | `InvenPos` | the slot |
| 0x42 | `SavedCount` | **the stack size** |
| 0x46 | `Count` | **the stack size** |

Everything else is zero for a stack of consumables: no enchant, no options, no colouring, not
bound. `Dbid` (0x26, i64) is set to the item db id as well; it is the one field whose meaning is
not pinned, and nothing we send depends on the choice.

### 2.4 Honest limits

A tooltip reply is what refreshes the client's cached row; it is **not** proof that the on-screen
badge repaints from that cache, because no capture contains this exchange. Two things would
settle it, in order: (1) `C_CLIENT_LOG` is now printed (§5) and the client says what it is unhappy
about in 644–2894 bytes per message; (2) if the count still sticks, the next suspect is World's
own `S_INVEN` refresh, which is World's to send and would point at a per-user DLM head-block
rather than at a packet.

---

## 3. `C_VISIT_NEW_SECTION` — and why a quest stops advancing after a relog

This is the second-priority item and it has a concrete mechanism.

`HandlerRegistry` already sends `AS_UPDATE_VISITED_SECTION_LIST` (0x1439) to World on **every**
`C_LOAD_TOPO_FIN` — with a hard-coded **empty** list, because nothing tracked visited sections:

```csharp
Program.World?.SendFrame(WorldBridge.OpUpdateVisitedSection,
    new byte[] { 18,0,0,0, 0,0,0,0, (byte)s.PlayerId, ... });
```

So after every relog World is told the character has explored **nothing**. Anything gated on
exploration resets: quest steps that require a section visited, and the teleport-scroll /
`S_VISIT_NEW_TEL_CAMP` destination list.

`C_VISIT_NEW_SECTION` (0xDFA4) is `[u32 mapId][u32 guardId][u32 sectionId]`, guard 0x10, and
`Handler_C_VISIT_NEW_SECTION` (`FUN_1404f1f20`) rejects `guardId >= 0x40` before recording
anything. The reply `S_VISIT_NEW_SECTION` (0x5A23) is the shipped def:
`[bool isFirstVisit][u32 mapId][u32 guardId][u32 sectionId]`, and `isFirstVisit` is what the
exploration quests key on.

T45 adds a `visited_sections` table, answers the packet, and gives `HandlerRegistry` a builder for
the push. The empty form of that push is **byte-identical** to the hard-coded one, so wiring it
cannot regress anything (`Visited_section_push_matches_the_hard_coded_empty_frame`).

`SDB_VIEW_ALL_SECTIONS`/`SDB_VISIT_ALL_SECTIONS` (0x2831) exists in `data/dbproxy_opcodes.txt` but
appears nowhere in the Arbiter decompile and in neither capture — it is a GM/QA path, not the
normal one, and is not implemented.

---

## 4. The mailbox — 12 blank rows and "00"

### 4.1 What actually produced it

Neither. It is **not** the replay table handing out dob's captured list, and **not** a client-side
`S_*PARCEL*` replay. Both captures were parsed frame by frame for all twelve parcel opcodes:

```
arb_world.log    950 frames   0 parcel frames
cap_newchar.log 4218 frames   0 parcel frames
```

Nobody opened a mailbox during either capture. So `SDB_LIST_PARCEL` (0x2777) was answered by
**nothing at all**: it is not in `DbProxyHandlers.IsHandledRequest`, and the replay table can only
replay what it captured. The 12 rows and the "00" are the client's own empty mailbox grid, drawn
because World never received a `DBS_LIST_PARCEL` to turn into a list.

That also makes it a **wedge, not a cosmetic bug**: an unanswered per-user DB request leaves
World's DLM queue for that character holding an item that never completes, and every later per-user
message for that user — `UserLeaveWorld` included — queues behind it (`status/HANDOFF.md` §1).
Opening the mailbox and then trying to log out is the reproduction.

The client-side half was already correct and is unchanged: `S_PARCEL_READ_RECV_STATUS` goes out at
login with both counters zero (T42, byte-exact against `cap_newchar_client.log` frame 312).

### 4.2 The empty reply, byte-exact without a capture

`Handler_SDB_LIST_PARCEL` is `FUN_14082fc50` (`Arb_part_071.c:15271`), guard `param_3 < 0x17`.
Frame-relative request: `[6] DlmId [10] UserDbId [14] ViewType [18] CurPage [22] UncheckedOnly`.

Its reply writer, read line by line at `:15376`:

```
FUN_140350eb0(&pkt, 0x2778)              opcode
slotA = cursor; *slotA = 0; u32 0        DataList offset
slotB = cursor; *slotB = 0; u32 0        DataList bytes
u32 uVar3  = *(u32*)(request + 6)        DlmId, echoed
u8  uVar11                               Success
u32 uVar4  = *(u32*)(request + 0xe)      ViewType, echoed
u32 uVar10 = *(u32*)(request + 0x12)     CurPage, echoed
u32 uVar9  = local_ec                    MaxPage
u32 uVar8  = local_f0                    ParcelCount
*slotA = *local_d8;                      offset := the running FRAME LENGTH - unconditionally
if (list non-empty) { append N x 0x9e8; *slotB = N * 0x9e8; }
```

and at `:15334`, before it consults the parcel manager at all:

```
local_ec = 1;      // MaxPage
local_f0 = 0;      // ParcelCount
```

So every field of the empty reply is either echoed from the request or a compiled-in default, and
the offset slot is backpatched even when the list is empty. The empty inbox is exactly 35 bytes:

```
23 00 00 00  78 27 | 23 00 00 00 | 00 00 00 00 | <dlm> | 01 | <view> | <page> | 01 00 00 00 | 00 00 00 00
```

`Parcel_empty_inbox_is_the_35_byte_frame` pins that literal.

### 4.3 The other five pairs

| SDB | DBS | request guard | reply fixed |
|---|---|---|---|
| `0x2777` LIST | `0x2778` | 23 | 35 |
| `0x2779` MAKE | `0x277a` | 26 | 27 |
| `0x277b` RECV | `0x277c` | 26 | 31 |
| `0x277d` RECV_EX | `0x277e` | 35 | 35 |
| `0x2781` RETURN | `0x2782` | 14 | 11 |
| `0x2811` DELETE | `0x2812` | 23 | 11 |

Layouts are in `status/MAIL-WAREHOUSE.md` §3.1 and repeated as named constants in
`World/ParcelDbHandlers.cs`. `ParcelTransList` elements are `ItemTransactionAtom`, 0x358 bytes —
the same atom `SDB_ITEM_SINGLE` carries — so `WarehouseHandlers.CloneAtomsWithIds` and `Apply`
are reused unchanged, and the insert-id rule is the same one T44 established: **atoms are applied
from the reply, never the request**.

`ParcelDataNoMsg` is 0x9e8 = 2536 bytes, read off the writer's own bounds check
(`if (local_d0 < *local_d8 + 0x9e8)`). **Its interior is not pinned by anything we have.** Only
two fields are: the parcel id at +0 and the receiver db id at +0x50 (the latter from
`Handler_C_SHOW_PARCEL_MESSAGE`'s ownership test, MAIL-WAREHOUSE §2.1). So T45 does what T44 did
for item records — `SDB_MAKE_PARCEL` hands us the real `ParcelData` and we keep it verbatim in
`parcels.record`, and `DBS_LIST_PARCEL` lists it back byte-for-byte. A parcel with no stored
record gets the synthesised two-field form.

---

## 5. `C_CLIENT_LOG` — the diagnostic nobody was reading

`Handler_C_CLIENT_LOG` (`FUN_1404dd9c0`) has no length guard and sends no reply: it hands the
payload to one sink (`FUN_140790640`) and returns. The bodies seen live are **644 to 2894 bytes**,
which is the client telling the server what went wrong on its side.

So the useful thing is not to ack it but to **print it**. T45 logs every UTF-16 run of four
characters or more. Given §2.4, this is the cheapest next step on the potion question: use a
potion with the tooltip open and read what the client says.

---

## 6. The rest, and what each gets

| opcode | T45 | why |
|---|---|---|
| `C_SERVER_TIME` | **answered in both modes** | `S_SERVER_TIME` (0x58CA) is one i64; the handler is eight lines |
| `C_SAVE_CLIENT_UI_SETTING` | **answered in both modes** | `S_SAVE_CLIENT_UI_SETTING` (0xF751) is one `byte result` |
| `C_TRADE_BROKER_HIGHEST_ITEM_LEVEL` | **answered in both modes**, 0 | one f32; we have no broker, and 0 is what an empty broker means |
| `C_VISIT_NEW_SECTION` | **implemented** (§3) | |
| `C_SHOW_ITEM_TOOLTIP_EX` | **implemented** (§2) | |
| `C_CLIENT_LOG` | **logged** (§5) | |
| `C_REQUEST_GUILD_INFO` | **already implemented — just unwired** | `GuildHandlers.OnClientPacket` has answered this since T39; the T39 registry diff was never applied. Its guard (0xc) matches the observed 12 exactly. |
| `C_DUNGEON_COOL_TIME_LIST` | accepted silently | the rows exist (T25 `dungeon_cooldowns`); the `S_DUNGEON_COOL_TIME_LIST` (0xD768) layout still needs a capture — `status/DUNGEON-COOLTIME.md` §5, already an open item |
| `C_REQUEST_PARTY_MATCH_INFO`, `C_REQUEST_MY_PARTY_MATCH_INFO`, `C_PARTY_MATCH_WINDOW_CLOSED` | accepted silently | the LFG pool does not exist; `S_SHOW_PARTY_MATCH_INFO` / `S_MY_PARTY_MATCH_INFO` shapes are unverified |
| `C_REQUEST_GUILD_LIST` | accepted silently | the guild browser; `S_REPLY_GUILD_LIST` (0x5F75) is a paged list we have no shape for |
| `C_REQUEST_CANDIDATE_LIST` | accepted silently | lord election |
| `C_VIEW_BATTLE_FIELD_RESULT` | accepted silently | no battleground records |
| `C_SHOW_AWESOMIUMWEB_SHOP` | accepted silently | the real handler sends a configured URL (`DAT_14121924a`) and skips the packet entirely when the shop is off (`DAT_141219248 == 0`), which is our case |
| `C_RESET_ALL_DUNGEON` | accepted silently | a party vote; needs a party |
| `C_EVENT_GUIDE`, `C_UPDATE_CONTENTS_PLAYTIME` | accepted silently | already `RegNoop`; the change is that they stop being forwarded |

**"Accepted silently" is deliberate, not laziness.** These are all UI-initiated reads with no
client-side timeout: the panel just stays empty. Inventing a reply shape we cannot verify is how a
client gets a packet it cannot parse — and a malformed `S_` is a disconnect, not an empty panel.
What matters for every one of them is that they stop being forwarded to a server that logs them
as errors.

---

## 7. The wiring — human-owned files, not applied

### 7.1 `Network/PacketDispatcher.cs` — should the forward still apply?

**Yes for genuinely unknown opcodes; no for opcodes we know are the Arbiter's.** The fallback is a
reasonable default — most unregistered client packets really are World's — but it turned eighteen
"the Arbiter is not answering this" bugs into eighteen lines of World-side noise that look like
World's fault. One clause fixes that:

```csharp
        if (!_handlers.TryGetValue(opcode, out var reg))
        {
            // T45: never forward a packet we know the Arbiter owns. World has no handler for any
            // of them and prints "handler has not been implemented yet!!!", which reads as a
            // World bug and hides ours. status/CLIENT-REJECTS.md.
            if (ArbiterClientHandlers.ArbiterOwned.Contains(opcode))
            {
                _log.LogError("{Opcode} (0x{Opcode:X4}) is Arbiter-owned and has no handler - dropped, NOT forwarded",
                    opcode, opcode);
                return;
            }
            if (session.InWorld) { session.ForwardToWorld(packet.ToArray()); return; }
            ...
        }
```

### 7.2 `Handlers/HandlerRegistry.cs`

**a. `RegNoop` and `RegEmptyReply` must stop forwarding.** Both begin
`if (s.InWorld) { Forward(s, op, body); return true; }`. For everything currently registered
through them that is an Arbiter-owned packet, delete that line. The six in this report are
`C_SAVE_CLIENT_UI_SETTING`, `C_TRADE_BROKER_HIGHEST_ITEM_LEVEL`, `C_UPDATE_CONTENTS_PLAYTIME`,
`C_EVENT_GUIDE`, `C_VISIT_NEW_SECTION` and the inline `C_SERVER_TIME`. The cleanest change is to
give both helpers an `alsoInWorld: true` argument rather than editing each call site.

**b. Register the new handlers.** Note the `- 4`: `PacketDispatcher` compares against a **body**
length and every `*PacketSize` constant here is a TOTAL length (§0).

```csharp
        // --- T45: Arbiter-owned client packets World was rejecting (status/CLIENT-REJECTS.md) ---
        var misc = loggerFactory.CreateLogger("ArbiterClient");
        Reg("C_SHOW_ITEM_TOOLTIP_EX", ArbiterClientHandlers.TooltipRequestBodySize,
            (s, b) => ArbiterClientHandlers.OnShowItemTooltipEx(s, b, misc));
        Reg("C_VISIT_NEW_SECTION", ArbiterClientHandlers.VisitPacketSize - 4,
            (s, b) => ArbiterClientHandlers.OnVisitNewSection(s, b, misc));
        Reg("C_CLIENT_LOG", 0, (s, b) => ArbiterClientHandlers.OnClientLog(s, b, misc));
        Reg("C_SERVER_TIME", 0, (s, b) => ArbiterClientHandlers.OnServerTime(s, b, misc));
        Reg("C_SAVE_CLIENT_UI_SETTING", 0, (s, b) => ArbiterClientHandlers.OnSaveClientUiSetting(s, b, misc));
        Reg("C_TRADE_BROKER_HIGHEST_ITEM_LEVEL", 0,
            (s, b) => ArbiterClientHandlers.OnTradeBrokerHighestItemLevel(s, b, misc));
        foreach (var name in new[]
        {
            "C_REQUEST_PARTY_MATCH_INFO", "C_REQUEST_MY_PARTY_MATCH_INFO", "C_PARTY_MATCH_WINDOW_CLOSED",
            "C_REQUEST_GUILD_LIST", "C_DUNGEON_COOL_TIME_LIST", "C_VIEW_BATTLE_FIELD_RESULT",
            "C_REQUEST_CANDIDATE_LIST", "C_SHOW_AWESOMIUMWEB_SHOP", "C_RESET_ALL_DUNGEON",
            "C_UPDATE_CONTENTS_PLAYTIME", "C_EVENT_GUIDE",
        })
            Reg(name, 0, (s, b) => ArbiterClientHandlers.OnAcceptSilently(s, b, misc));
```

`C_VISIT_NEW_SECTION`, `C_UPDATE_CONTENTS_PLAYTIME` and `C_EVENT_GUIDE` are currently `RegNoop`
and `C_SAVE_CLIENT_UI_SETTING` is `RegEmptyReply` and `C_SERVER_TIME` is inline — remove those
five lines when adding these, or `PacketDispatcher.Register` throws on the duplicate.

**c. `C_REQUEST_GUILD_INFO` is not new work.** It is item one of the T39 wiring diff
(`status/GUILD-DESIGN.md` §10), which has never been applied. The same `- 4` correction applies
there: that diff passes `GuildPackets.MinClientLength(op)`, a TOTAL length, into a body-length
parameter, so every guild packet would be rejected four bytes short. The same bug is in the T43
chat diff (`status/CHAT-DESIGN.md` §7.1).

**d. Feed the visited-section push.** In the `C_LOAD_TOPO_FIN` branch, replace the hard-coded
empty frame with:

```csharp
                Program.World?.SendFrame(WorldBridge.OpUpdateVisitedSection,
                    ArbiterClientHandlers.BuildUpdateVisitedSectionList(s.PlayerId,
                        Program.Store?.GetVisitedSections((int)s.SelectedCharacter!.Id)
                            ?? Array.Empty<CharacterStore.VisitedSection>()));
```

With no rows this produces the identical twelve bytes, so it is safe to apply before anything has
been explored.

---

## 8. The social seed, language-aware

`SocialHandlers` seeded the friend group `好友` and the profile message `今天也是愉快的一天!` onto
every character. Both are `StrFriendDataSheet` ids 100 and 200, captured in
`cap_newchar_client.log` frames 305/306.

**The mechanism in the brief was not quite right, and the correction is worth keeping.** That
capture's own `C_LOGIN_ARBITER` (frame 2) carries `language = 6`, i.e. **EUR** — the same value
this server's client sends. The Chinese strings did not come from the packet; they came from the
TW ArbiterServer's own installed string sheet. The field is the client's language, not the
server's.

We have no string sheet, so the field is the only signal there is. T45 keys off it and **defaults
to English**:

| language | group name | profile message |
|---|---|---|
| 7 (TW) | `好友` | `今天也是愉快的一天!` |
| everything else, incl. 6 (EUR) | `Friends` | *(empty)* |

The values are the shipped `C_LOGIN_ARBITER.2.def`'s own comment:
`0 = INT, 1 = KOR, 2 = USA, 3 = JPN, 4 = GER, 5 = FRA, 6 = EUR, 7 = TW, 8 = RUS`.

Getting the language from the login handler to `SocialHandlers` needed no human-owned change:
`LoginHandlers` already hands every login to `AuthProviders.Authenticate` inside an `AuthRequest`
whose `Region` **is** that field, and `Auth/*` is Cowork-editable. `Auth/LoginLanguage.cs` records
it there and `SocialHandlers.SendFriendGroupList` reads it back.

---

## 9. Open

- **Whether the tooltip reply is what repaints the stack badge** (§2.4). `C_CLIENT_LOG` is the
  next evidence.
- **`ParcelDataNoMsg`'s 2536-byte interior** — two fields pinned out of however many. Everything
  works for an empty inbox and for parcels World created (their bytes are kept verbatim); a parcel
  the Arbiter invents would be mostly zeroes.
- **`S_DUNGEON_COOL_TIME_LIST`, `S_REPLY_GUILD_LIST`, `S_SHOW_PARTY_MATCH_INFO`,
  `S_MY_PARTY_MATCH_INFO`, `S_SHOW_CANDIDATE_LIST`, `S_VIEW_BATTLE_FIELD_RESULT`** — six reply
  shapes, all needing a capture. Their requests are accepted silently until then.
  **T64 checked `cap_social.log` / `cap_social_client.log` for them and they are not there.**
  Not one of the six `S_` opcodes (0xD768, 0x5F75, 0xDF65, 0xB5A3, 0xF12D, 0xE459) appears in
  the client tap, and neither do the three `C_` opcodes that would have asked for them
  (`C_DUNGEON_COOL_TIME_LIST` 0xD3F7, `C_REQUEST_GUILD_LIST` 0x866B,
  `C_VIEW_BATTLE_FIELD_RESULT` 0xEC3D) — the two players never opened those windows. So the
  accept-silently handlers stay exactly as they are; there is nothing to replace them with,
  and a reply invented from the `.def` alone is the kind of guess that desynced World before
  (`status/HANDOFF.md` §1). `T64_the_six_silent_windows_are_still_unanswered` pins the list so
  the next capture pass knows exactly what it is looking for.

  What the same capture DID show in that family, and what T64 built from it:
  `C_DUNGEON_CLEAR_COUNT_LIST` (0x5C98, body `[u16 nameOff][wstr name]`) makes the Arbiter
  push `AS_VIEW_INTER_PARTY_MATCH_DUNGEON_LIST_EXTENDED` (0x1644) to World carrying the named
  user’s db id, and `AS_VIEW_INTER_PARTY_MATCH_BATTLEFIELD_LIST_EXTENDED` (0x1645) is the same
  shape — `status/PARTY-DESIGN.md` §13.3. Builders exist; nothing calls them, because the
  Arbiter half of `C_DUNGEON_CLEAR_COUNT_LIST` is not modelled and inventing a trigger would
  be a guess of the same kind.
- **`SDB_VISIT_ALL_SECTIONS` (0x2831)** — in the opcode table, in no capture, and nowhere in the
  Arbiter decompile. A GM/QA path.
- **`C_LIST_CHANNEL` (0x661B)** — turned up while naming these and has no handler anywhere in the
  binary, so it is dead in 100.02 and not in the eighteen.

---

## 10. T82 — the UI packets cap_social4 still shows unanswered

Two client captures of the same session: `cap_social4_client_ctl.txt` (character `New`) and
`cap_social4_client2_ctl.txt` (the second client, already reframed — 8773 frames, and the only one
of the two that opens the mailbox or the warehouse). `S_LOGIN` is frame **50** in client1, so
frames 1–49 are pure lobby.

### 10.1 Built in this pass

| packet | frames | verdict |
|---|---|---|
| `C_GET_USER_GUILD_LOGO` 0x584B → `S_GET_USER_GUILD_LOGO` 0x7DFA | 2910 / 2911 | **Arbiter** — the crest is a `guilds` column and the Arbiter owns guild storage outright; no tap traffic at all |
| `C_RQ_SKILL_POLISHING_LIST` 0x7983 → `S_RP_SKILL_POLISHING_LIST` 0xDA7E | 145 | **Arbiter** — answered in the lobby burst |
| `C_RQ_SKILL_POLISHING_EXP_INFO` 0xAD37 → `S_RP_SKILL_POLISHING_EXP_INFO` 0xEA50 | 146 | **Arbiter** — same burst, next frame |

All three shipped defs are RIGHT, so no `V100Definitions` override is needed. The skill-polishing
pair was on the **`RegNoop` list**, which is not the same as being handled: a noop registers the
opcode so it is not forwarded, then sends **nothing**, and the panel never populates. Both real
answers are the all-zero form (8 bytes and 36 bytes), which is what every TeraSharp character
would produce anyway, so serving the zeros is the whole fix.

The crest answer has one byte worth naming: a `bytes` ref is `[u16 offset][u16 count]`, and an
**empty** blob still gets a real offset — 16, the packet length — because the writer patches the
slot to the current end whether or not data follows. Frame 2911 is `10 00 00 00 …`, not `00 00 00 00`.

### 10.2 Already correct — verified against the capture, not changed

| packet | frame | ours |
|---|---|---|
| `S_REMAIN_PLAY_TIME` | 6 | `06 00 00 00 00 00 00 00` = `accountType 6, minutesLeft 0` — exactly what `LoginHandlers` sends |
| `S_RESPONSE_SERVER_ADMINTOOL_AWESOMIUM_URL` | 404 | `08 00 0A 00 00 00 00 00` — the raw body `LoginHandlers` already writes |
| `S_FESTIVAL_LIST` | 74 | `00 00 00 00`, one empty array — our empty field set produces the same four bytes |
| `S_REQUEST_SERVANT_INFO_LIST` | 263 | `00 00 00 00 01 0A 00 00 00` = count 0, `hidden 1`, `slots 10` — matches `RegEmptyReply`'s dictionary exactly |
| `S_RESPONSE_SERVANT_ADVENTURE_LIST` | 264 | `00 00 00 00 01 FF FF FF FF 03 00 00 00 00 00 00 00` = count 0, `unk1 1`, `unk2 -1`, `maxSlots 3` — matches too |

`C_SET_SERVANT_SEQUENCE` (frame 2888, body `01 00 08 00 08 00 00 00` + eight `FF`) is already on
the `RegNoop` list and the capture shows **no reply to it**, so a noop is the right shape — the
T80 report listed it as unregistered, which was wrong.

### 10.3 Gaps, with frame numbers — not built

| packet | frames | what the real one carries | why not now |
|---|---|---|---|
| `S_ACCOUNT_PACKAGE_LIST` 0xE9A9 | 14, 2894 | **three** entries: ids 0x215/0x216/…, each with an expiry `6F 28 CF 6A` (unix 1791165551) | we send an empty list; the entries are account-benefit state nothing in the tree models |
| `S_ACCOUNT_BENEFIT_LIST` 0x88E0 | 47, 48 (12 in client1, 8 in client2) | 1–2 entries with a benefit id, a value `0x23E726` and the same expiry | same |
| `S_SEND_USER_PLAY_TIME` 0xA7E0 | 78, 2957 | `ED 00 00 00` + an int64 unix time (1789608777) — seconds played and when | we send an empty field set (zeros); harmless but not exact |
| `S_ENABLE_DISABLE_SELLABLE_ITEM_LIST` 0x667C | 441, 442 | 39-byte body, **3 entries**, each `[u16 here][u16 next][i32 id]` + flags | we send it not at all |
| `S_DARK_RIFT_JOIN_LIST` 0x9DE2 | 2881, 5984 | `00 00 00 00` — one empty array | post-world-entry both times, so Arbiter vs World is not settled by position alone |
| `S_START_COOLTIME_SERVANT_SKILL` 0x9128 | 71, 2950 | `00 00 00 00` | in the enter-world burst; almost certainly Arbiter, but nothing in the capture ties it to a request |
| `S_INGAMESHOP_*` | 147 onward | `CATEGORY_BEGIN` ×6, `CATEGORY_DATA` ×36, `PRODUCT_BEGIN` ×6, **`PRODUCT_DATA` ×636**, two `_END` | the shop catalogue — a whole subsystem, not a UI packet |

### 10.4 Deliberately left to their own tasks

* **Mail UI** — `cap_social4_client2` frames **5360–5670**: `C_LIST_PARCEL` → `S_LIST_PARCEL_EX`
  (5361 empty 20-byte body, 5620/5670 a 159-byte one with a parcel in it),
  `C_SET_SEND_PARCEL_TYPE` → `S_SET_SEND_PARCEL_ITEM` + `S_SET_SEND_PARCEL_MONEY` (5370–5372),
  `S_SEND_PARCEL_TAX` (5377, 5426, 5440, 5458 — the tax moves 0x32 → 0xFA → 0x12C as money and an
  item are added), `C_SEND_PARCEL` → `S_SEND_PARCEL` (5464/5466),
  `C_SHOW_PARCEL_MESSAGE` → `S_SHOW_PARCEL_MESSAGE` (5642/5643, body `0A 00 04 00 00 00 00 00`).
  All Arbiter-built — this is `ParcelHandlers`/`MAIL-WAREHOUSE.md` territory and wants that task's
  tables, not a bolt-on here.
* **Warehouse UI** — `cap_social4_client2` frames **6761–6877**: `S_VIEW_WARE_EX` 0x9F9A, 18
  occurrences, 42-byte empty body and 133-byte with two slots, driven by `C_PUT_WARE_ITEM` /
  `C_GET_WARE_ITEM`. `World/WarehouseHandlers.cs` was being edited while T82 ran, so it is hands-off.

## 11. T84 — the leftovers with data in them

T82 listed six mail packets, `S_VIEW_WARE_EX` and four account/config packets as "left to their
own task". T84 took them with the capture open. **Four of the eleven are ours; the rest are
World-built and want nothing from us.**

### 11.1 Mail and warehouse — World-built, and the tap proves it

T82 called the mail UI "all Arbiter-built". That was wrong, and the Arbiter↔World tap says so
plainly. `cap_social4_client2` frames 5360–5670 are the mail episode; in `cap_social4` at exactly
those seconds:

| tap seq | time | frame | meaning |
|---|---|---|---|
| 4559/4560 | 01:28:41.037 | `SDB_LIST_PARCEL` 0x2777 → `DBS_LIST_PARCEL` 0x2778 (12715 B) | World asks us for the mailbox |
| 4579/4580 | 01:28:43.572 | same pair | again on re-open |
| 4639/4640 | 01:28:52.005 | same pair | again |
| 4653…4672 | 01:28:53.38–41 | `SDB_RECV_PARCEL_EX` 0x277D → `DBS_RECV_PARCEL_EX` 0x277E ×6 | the claim |
| 4676/4677 | 01:28:53.457 | `SDB_LIST_PARCEL` → `DBS_LIST_PARCEL` (35 B — now empty) | the re-list after claiming |

World would not ask us for the rows if it were not the one building the packet. So
`S_LIST_PARCEL_EX`, `S_SEND_PARCEL`, `S_SEND_PARCEL_TAX`, `S_SET_SEND_PARCEL_ITEM` and
`S_SET_SEND_PARCEL_MONEY` are **World-built — leave them**. Our share of the mailbox is the
`SDB_` answer `ParcelHandlers`/`DbProxyHandlers` already give.

The one exception is `S_SHOW_PARCEL_MESSAGE` 0xABD3, which we do build —
`cap_social4_client2` frame 5643 is `0C 00 D3 AB 0A 00 04 00 00 00 00 00`, which is
`ParcelHandlers.BuildShowParcelMessage(4, "")` byte for byte. Pinned in
`T84_opcodes_and_the_one_arbiter_built_mail_packet`.

`S_VIEW_WARE_EX` 0x9F9A goes the same way, by the absence: `SDB_VIEW_WAREHOUSE` 0x274A fires
**zero** times in the whole 15-minute tap, yet `cap_social4_client2` shows 18 `S_VIEW_WARE_EX`
frames — so World held the container itself and built them. What DOES fire in that window is
`SDB_ITEM_SINGLE` ×16, the persist-one-item atom `DbProxyHandlers` already answers. That is the
whole shape of it: World owns the live container, we are the store. **World-built — leave.**

### 11.2 The four that are ours

All four shipped defs are RIGHT. What was missing was the data — `LoginHandlers` sends the first
three with an empty field set and the fourth not at all.

| packet | frames | on the wire | served from |
|---|---|---|---|
| `S_ACCOUNT_PACKAGE_LIST` 0xE9A9 | 14, 2894 | 52 B — 16-byte element, `uint32 packageId` + `int64 expirationDate` | new `account_benefits` table |
| `S_ACCOUNT_BENEFIT_LIST` 0x88E0 | 47, 48 | 34 B / 63 B — 29-byte element behind a 1-byte header field (1) | the same rows |
| `S_SEND_USER_PLAY_TIME` 0xA7E0 | 78, 2957 | 12 B — `uint32 totalPlaytime` + `uint64 localServerTime` | session play time + clock |
| `S_ENABLE_DISABLE_SELLABLE_ITEM_LIST` 0x667C | 441, 442 | 39 B — 15-byte header + 3×8-byte elements | config (`DefaultSellableItems`) |

All four arrive in the lobby burst, frames 14/47/48/78 before `S_LOGIN` at frame 50, so no
character is picked and World is not in the picture at all.

Three findings worth keeping:

* **`timeRemaining` is misnamed.** Frame 47 carries `6F 28 CF 6A` = **1791961199** in that slot —
  the same absolute unix second `S_ACCOUNT_PACKAGE_LIST` gives package 533 as its
  `expirationDate`. It is an expiry, not a countdown, so one `expires_at` column serves both.
* **Storage is a table, not account columns.** The brief suggested account-row columns; the
  capture already shows three packages for one account and the list is variable-length, so
  `account_benefits(account_id, package_id, expires_at, value)` it is. `ORDER BY package_id`
  reproduces the capture order (533, 534, 1000).
* **An empty `array` leaves BOTH ref words zero** — frame 441 `00 00 00 00` for lists 1 and 3 —
  unlike an empty `bytes` ref, which §10 showed still gets a real offset. And an
  `array<uint32>` element is the bare number after `[u16 here][u16 next]`, not a record.

The three expiries in frame 14 are 1791961199, 2100409199 and 1786777199; the brief quoted
1791165551, which does not round-trip to the capture hex.

## 12. T87 — telemetry, acks and the lobby’s housekeeping

Sixteen client packets, each with a real `Handler_C_*` in ArbiterServer.exe and none with a
World handler. **Only `C_PONG` appears in any capture** (`cap_social4_client` 32 and 4390,
answering `S_PING` at 31; both are bare four-byte frames), so the rest is decompile-derived.

The handlers’ length guards are **packet** lengths. `PacketDispatcher`’s `minLen` is a **body**
length, so every registry number below is the guard minus four.

| opcode | packet | guard | what the real handler does | ours |
|---|---|---|---|---|
| 0x8091 | `C_PONG` | none | nothing — the handler takes no arguments and returns 1 | noop |
| 0xF290 | `C_CHECK_RTT` | ≥4 | writes `S_CHECK_RTT` 0x52B0 and sends, with no payload write | reply `04 00 B0 52` |
| 0x7CFE | `C_PLAY_TIME` | none | `S_PLAY_TIME` 0xA2BE + the u32 at User+0x1E4 | reply, seconds from a hook |
| 0xBC06 | `C_REQUEST_PLAYTIME` | ≥4 | stamps now onto the account tracker (Account+0x30F0); **no reply** | noop |
| 0xD71E | `C_REQUEST_PERF` | ≥8 | asks every World (`AS_REQUEST_PERF` 0x1446) and reports to the **admin tool**; the client gets nothing | noop |
| 0xACE5 | `C_SEND_UI_LOG` | ≥8 | body word 0 selects one of three counters (1/2/3); **returns FALSE otherwise** | noop + that rejection |
| 0xAD47 | `C_GET_MY_IP` | ≥4 | `%d.%d.%d.%d` of the peer into `S_GET_MY_IP` 0xA9DF | reply |
| 0x9219 | `C_XIGNCODE_SECURITY_DATA` | ≥8 | wraps the blob as `AX_PONG_SECURITY_DATA` 0x426B **to the anti-cheat server**, not the client | noop |
| 0x7F08 | `C_INVALID_BUILD_VERSION` | ≥4 | logs and disconnects unconditionally | log + `Close()` |
| 0x779E | `C_REQUEST_LATEST_UPDATE_NOTIFICATION` | ≥4 | `S_ANNOUNCE_UPDATE_NOTIFICATION` 0x5C2E | reply, empty form |
| 0xFE00 | `C_CONFIRM_UPDATE_NOTIFICATION` | ≥8 | compares the id with the account’s; **no reply** | noop |
| 0x871F | `C_SECOND_PASSWORD_AUTH` | ≥6 | string ref → second-password manager, async result | noop |
| 0x761E | `C_SECOND_PASSWORD_REGISTER` | ≥6 | same, register path | noop |
| 0xC2BA | `C_REFRESH_API_ACCESS_TOKEN` | ≥4 | mints a token, `S_REFRESH_API_ACCESS_TOKEN` 0x55E4; **no packet at all on failure** | reply, empty token |
| 0xE488 | `C_CANCEL_EXIT` | none | queues the cancel job; **there is no `S_CANCEL_EXIT` opcode** | cancel, no reply |
| 0xEB4D | `C_CANCEL_RETURN_TO_LOBBY` | none | queues the cancel; the job sends 0x5E0E + one byte | **already done** (T33) |

### 12.1 The three reply layouts

None of the sixteen has a shipped `.def` — all are raw writers.

```
S_CHECK_RTT      [u16 len=4][u16 op]
S_PLAY_TIME      [u16 len=8][u16 op][u32 seconds]
S_GET_MY_IP      [u16 len][u16 op][u16 offset=6][wchar ip][u16 0]
S_REFRESH_API_ACCESS_TOKEN   same shape as S_GET_MY_IP
S_ANNOUNCE_UPDATE_NOTIFICATION
                 [u16 len][u16 op][u16 offTitle=12][u16 offBody][u32 id][wchar][wchar]
```

Both string slots of `S_ANNOUNCE_UPDATE_NOTIFICATION` are reserved **before** the u32 — headers
first, then scalars, the same order the def codec uses — so the first string always starts at 12.

Three findings worth keeping:

* **`C_REQUEST_PERF` and `C_XIGNCODE_SECURITY_DATA` are not client replies at all.** One talks to
  the admin tool, the other to the anti-cheat server. A future task that "implements" them by
  answering the client would be wrong.
* **`C_CANCEL_EXIT` has no reply**, while its lobby twin does — there is no `S_CANCEL_EXIT`
  opcode in the table. The asymmetry is real, not a gap in the capture.
* **`RegNoop` is the wrong helper for every one of these.** It forwards to World when in-world,
  which is the §7 bug all over again; they are registered with `Reg` and listed in `ArbiterOwned`.
