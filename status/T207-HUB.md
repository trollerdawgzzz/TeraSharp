# T207 - TERA Shop and item delivery: TeraSharp is the hub

## 1. What the retail stack actually does

The premise the brief started from - HTTP routes on the Arbiter's `<APIServer port="8800">` - is not
what the binary does.

| Claim | What the decompile says |
|---|---|
| The Arbiter serves an HTTP API on 8800 | It has no HTTP server at all. Its only URL is a retail phone-home, `http://%s/Default.aspx?v=%s` |
| `APIServer` is a listener | Two config values it FORWARDS to the client: `APIServerAddress` + `APIServerAccessToken`, in `S_LOGIN_ACCOUNT_INFO` (T124/T131) |
| The Arbiter calls a box/benefit HTTP route | tera-api's arbiter API (8080) is what the Arbiter calls: `ServiceTest`, `GetServerPermission`, `ServerDown`, `GetUserInfo`, `EnterGame`, `LeaveGame`, `CreateChar`, `ModifyChar`, `DeleteChar`, `UseChronoScroll`, `report_cheater`, plus `/authApi/GameAuthenticationLogin` |
| There is an Item Claim packet | There is not. No `C_`/`S_ITEM_CLAIM` and no box-list packet exists in the 376012 opcode map or in the Arbiter's own name tables |

Delivery in tera-api goes over a **protobuf socket** (`HUB_HOST`, `HUB_PORT`; 11001 on this stack,
answered by the retail `arb_gw_tw2_log.exe`). Its box calls carry the same argument names the
Arbiter's own box code builds - `boxSN`, `boxItemSNs`, `receiverUserSN`, `boxStateCode`,
`endActivationDateTime` (Arb_part_077.c) - so the hub, not HTTP, is the retail path.

T207 therefore answers that socket in TeraSharp: tera-api stays stock, `arb_gw` is not needed, and
the boxes live in our own database.

## 2. The wire format

`Web/HubProtocol.cs`. Frame: `[u16 size][u16 msgId][protobuf]`, **size counts itself** (2 + 2 + body).

| msgId | Message | Fields (number, type) | Direction |
|---|---|---|---|
| 1 | RegisterReq | 1 fixed32 serverId, 2 repeated fixed32 eventSub | tera-api -> us |
| 2 | RegisterAns | 1 bool result, 2 repeated fixed32 serverList | us -> tera-api |
| 3 | SendMessageReq | 1 fixed64 jobId, 2 fixed32 serverId, 3 bytes msgBuf | tera-api -> us |
| 4 | SendMessageAns | 1 fixed64 jobId, 2 fixed32 serverId, 3 fixed32 destCount, 4 bool result | us -> tera-api |
| 5 | RecvMessageReq | 1 fixed32 serverId, 2 fixed64 jobId, 3 bytes msgBuf | us -> tera-api (the answer) |
| 6 / 7 | PingReq / PingAns | empty | either way |
| 8 | ServerEvent | 1 fixed32 serverId, 2 int32 event | us -> tera-api |

`msgBuf` is `[u16 innerId][protobuf]`. A call is matched by `jobId`: tera-api decodes the answer
with the type it asked for, so the answer's `innerId` is never read.

`gusid = category << 24 | number`. tera-api registers as category 19 (webcstool); the box API is
`(16 << 24) + 1`; this server is category 0 (arbitergw) with the server number from
`planet.dbServerName` (`PlanetDB_2800` -> 2800). Calls addressed to `0xFF000000` are the user-entity
channel.

| innerId | Call | What TeraSharp does |
|---|---|---|
| 1 | `opmsg` (box API; the function is `gufid` field 5) | see section 3 |
| 2 | KickUserReq | closes that account's sessions |
| 4 | SendMessageReq | one `S_SYSTEM_MESSAGE` to that account (the text is UTF-16LE) |
| 6 | BulkKickReq | answered and ignored - TeraSharp has no maintenance kick |
| 15 | BoxNotiUserReq | delivers anything pending for that account, then tells an online buyer |
| 38 / 40 | AddBenefitReq / RemoveBenefitReq | `account_benefits` + a fresh `S_ACCOUNT_BENEFIT_LIST`; elite is benefit 533 |
| 1 on `0xFF000000` | QueryUserReq | `QueryUserAns { userSrl, serverId }`, serverId 0 when the account is offline |

## 3. The box API (`opmsg`)

`gufid` = `16 << 24 | function`; arguments are name/value byte strings (field 9, name 1, value 2).
The answer's `resultCode` (field 10) is a gusid whose **number half must be 0** or tera-api rejects
the call; a scalar answer is field 11, a table answer field 12.

| Function | Arguments read | Answer |
|---|---|---|
| 117 CreateServiceItem | `serviceItemMappingItemSN` (item template), `serviceItemName`, `serviceItemDescription`, `serviceItemEnableFlag`, `serviceItemRegisterUserSN` | the new `serviceItemSN` |
| 115 GetPageServiceItem | `serviceItemMappingItemSN`, `offset`, `count` | rows of `serviceItemSN`, `serviceItemMappingItemSN`, `serviceItemEnableFlag`, `serviceItemName` |
| 116 GetServiceItem | `serviceItemSN` | the same row, or none |
| 118 SetDisableServiceItem | `serviceItemSN` | the id, or a refusal |
| 107 CreateBox | `receiverUserSN` (accountDBID), `receiverCharacterSN`, `boxTagInfo`, `boxServiceItemInfo`, `startActivationDateTime`, `endActivationDateTime`, `externalTransactionKey` | the new `boxSN` |

Two string encodings, from tera-api's `convertBoxTagValue`:

```
boxTagInfo          N,tagSN,hexUtf8(value)...            1 = content, 2 = title, 3 = icon
boxServiceItemInfo  N,[serviceItemSN,externalItemKey,tagCount,[tagSN,hexUtf8(value)]...]...
                                                         item tag 1 = the stack count
```

A refusal (`resultCode` number 1) leaves the purchase in tera-api's queue, so it is retried rather
than lost. That is the answer for an unknown account, an empty item list, or a function we do not
implement.

## 4. Delivery: a box becomes a system parcel

`World/BoxDelivery.cs`. There is no box window in 100.02, so the in-game surface for "items you
have not collected" is the system parcel `SA_MAKE_SYS_PARCEL` writes: same `parcels` table, same
claim transaction, survives a relog, collected from the mailbox.

- Target character: the one tera-api named when it belongs to the paying account, else the
  account's most recently played one. A box is bought per account, and retail lets any of the
  account's characters claim it.
- Attachments are `SendItemInfo` records - template at +8, amount at +12, item db id left 0 for
  World's claim transaction to allocate (`SystemParcelAttachments`).
- More than five lines becomes more than one parcel; the record has five attachment slots.
- No character yet (a purchase before the buyer's first login): the box stays `state = 0` and the
  one-minute sweep, or the next `BoxNotiUser`, delivers it.
- `hub_boxes.parcel_id` keeps the link, so "where did my item go" is one query.

Tables (`Persistence/CharacterStore.Hub.cs`, created on first use): `hub_service_items`,
`hub_boxes`, `hub_box_items`. Deliberately no foreign key to `accounts`: tera-api can pay for an
account that has never logged in here, and a FOREIGN KEY failure would take the hub socket down.

## 5. The shop button

`Handlers/ShopUrl.cs`. `C_SHOW_AWESOMIUMWEB_SHOP` -> `S_SHOW_AWESOMIUMWEB_SHOP`, whose shipped def
is one field, `string link`. The link is `shop.url`, defaulting to tera-api's portal on the auth
host (`http://<auth host>:81/tera/ShopMain`).

`S_RESPONSE_SERVER_ADMINTOOL_AWESOMIUM_URL` is left exactly as it is: both of its strings were
empty in the capture where the Alt+A panel opened (T131/T132), and repurposing a working packet for
the shop is how a working panel stops working.

## 6. What is pinned, and the one guess

Pinned to the client we must satisfy (tera-api's `hubConnection.js`, `hubFunctions.js` and its
generated protobuf modules) and cross-checked against the Arbiter's own box argument names: every
frame, field number, argument name and both string encodings. A Python model of the whole
`CreateBox` round trip was run against these rules before the C# was written.

**Guess:** the answer `innerId`s (`BoxNotiUserAns` 16, `AddBenefitAns` 39, ...) are the request id
plus one. No capture of `arb_gw`'s own traffic exists. It cannot break tera-api, which matches on
`jobId` and decodes with the type it asked for - but it is not pinned, and a capture of a retail
hub would settle it.

## 7. Live check

1. tera-api `.env`: `HUB_HOST=127.0.0.1`, `HUB_PORT=11001`, `API_PORTAL_SHOP_ENABLE=true`. Stop
   `arb_gw_tw2_log.exe` - it holds the port.
2. Start TeraSharp; the log says `hub listening on 127.0.0.1:11001 as server 2800`, then
   `hub: <peer> connected` and `hub: register from 19:0` when tera-api attaches.
3. Buy something in the shop. Expect, in order: `hub: service item N = template T`,
   `hub: box N for account A`, `hub: box N delivered to character C as 1 parcel(s)`.
4. In game: the mail arrives from `Shop`; collecting it puts the item in the bag.
5. Elite: `hub: benefit 533 on account A for 2592000s`, and the account's benefit list refreshes.

## 8. T207b - what the first run got wrong

Two bugs, both found by running the suite rather than reading it.

| Symptom | Cause | Fix |
|---|---|---|
| `opcode 53135 (C_SHOW_AWESOMIUMWEB_SHOP) already registered` at start (and `T183_registry...` red whenever the real 376012 map is present) | `C_SHOW_AWESOMIUMWEB_SHOP` was already in T97's accept-silently list; T207 registered it a second time for `ShopUrl` | it leaves the silent list and keeps the real handler - `status/T207b-PATCH.diff` |
| Every hub call answered with silence: `expected one RegisterAns`, `every call gets at least a SendMessageAns` | `ReadFrame` stripped the size prefix while `Handle` read the function id from byte 0, so each call took its id out of its own length field, matched nothing and fell to the default case | `ReadFrame` returns the WHOLE frame, `Handle` reads it with the new `FrameId` / `FrameBody`, and the tests read replies the same way. Frame in, frame out - one shape everywhere |

The second one is the more interesting failure: the Python model of section 6 verified the *format*
and was right about every byte, but it modelled `Handle` as taking a frame while the C# fed it a
body. A model cannot catch a contract mismatch between two pieces of code it does not contain,
which is the argument for compiling and running rather than reasoning about it.

Run on master HEAD, Linux, no capture fixtures and no `.def` tree: **945 passed, 0 failed,
228 skipped**, with all six T207 tests passing; with the real opcode map also present,
`T183_registry_separates_camp_and_guild_title_in_376012` passes again. The 10 further failures
seen in that second configuration are this sandbox, not the code: no `tera_v100_MASTER_FINAL`
(so every shipped-def test fails) plus the two that want Windows paths and a real World.

## T207c - every call tera-api can make, and what it gets

`arbiter-bg3.log` has `hub: unhandled OpUent call 3 (0 B)` every ten seconds from 20:47:30. That is
`hubFunctions.js`'s `getServerStat`: inner id 3 to `gusid.userentity` with an empty
`GetServerStatReq`, which is why the payload is zero bytes, run on a ten-second schedule.

### OpUent (target `gusid.userentity`, 0xFF000000)

| Id | tera-api call | Answer |
|---|---|---|
| 1 | `queryUser` | **answered** (T207) - `QueryUserAns { fixed64 userSrl, fixed32 serverId }`, serverId 0 when the account is not on this server |
| 3 | `getServerStat` | **answered** (T207c) - one `ServerInfo { fixed32 serverId = 1, fixed32 userCnt = 2 }` at field 1 |
| 5 | `getAllServerStat` | **refused by name** - its Ans wants `{ serverId, lastMsg, ip, port }` for every server in the platform; TeraSharp is one server with no registry to enumerate, and tera-api reads login ip/port from its own `server_info` table |

**What 3 is actually for.** Not the admin panel's Online page - that uses `kickUser` / `bulkKick`
and its own database. It is `ServerCheckActions.all` (`src/actions/serverCheck.actions.js`), the
availability poll:

```js
stat?.serverList && stat.serverList.find(s => s.serverId == server.get("serverId"))
```

A match marks the server available with `method: "Hub"`; no match falls back to a TCP probe of
`loginPort`, or to unavailable. So `serverId` in the answer is the **plain server number** tera-api
keeps in `server_info` - `TERASHARP_HUB_SERVER_ID`, 2800 by default - **not a gusid**. Getting that
wrong is silent: the row simply never matches and the panel keeps port-probing.

`userCnt` is the number of **distinct accounts** in world; the message has no field for account
ids, so "count + account ids" is a count. The de-duplication is in `HubServer.OnlineAccountIds`, so
two characters of one account are one user whichever side supplies the list.

### OpArb (target this server's gusid, or the box API)

| Id | tera-api call | Answer |
|---|---|---|
| 1 | `opMsg` | **answered** - the box API: gufid 107 CreateBox, 115 GetPageServiceItem, 116 GetServiceItem, 117 CreateServiceItem, 118 SetDisableServiceItem |
| 2 | `kickUser` | **answered** |
| 4 | `sendMsg` | **answered** |
| 6 | `bulkKick` | **answered** (acked, nothing kicked - TeraSharp has no maintenance kick) |
| 15 | `boxNotiUser` | **answered** |
| 38 | `addBenefit` | **answered** |
| 40 | `removeBenefit` | **answered** |

Nothing in `hubFunctions.js` reaches an unhandled branch any more. The branch stays, because an id
tera-api does not have a function for is still worth a warning.

Tests: `T207c_the_ten_second_server_stat_poll_is_answered` drives the captured 0-byte call through
the real frame path; `T207c_an_empty_server_still_reports_itself`;
`T207c_every_opuent_call_tera_api_can_make_is_decided`.
