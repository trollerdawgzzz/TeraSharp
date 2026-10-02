# T230 — shop boxes via Item Claim: there is no Item Claim packet in 376012

**Verdict: cannot be built as specified.** The 100.02 client has no item-claim list, claim or delete
packet, so there is nothing to pin, nothing to serve at login, and nothing to consume on claim. T207's
system-parcel delivery is not a fallback for a client that "can't display" boxes — it is the only
in-game surface this protocol has. This confirms, from the authoritative map rather than from reading
the client, what `World/BoxDelivery.cs:9` already asserted in T207.

## 1. Evidence

Three independent sources, all negative.

| source | what was searched | result |
|---|---|---|
| `tera-server-proxy/data/data.json`, `maps."376012"` — the same file `OpcodeTable.LoadFromFile` reads, so by definition the opcodes this client uses | 2167 opcode names for `CLAIM` | **0** |
| the same file, all five protocol versions it carries (366226, 367078, 367080, 367081, 376012) | `CLAIM`, `ITEM_BOX`, `ITEMBOX` | **0 in every version** |
| `tera-server-proxy/data/definitions.zip` — 917 `.def` schemas | `CLAIM`, `GIFT` | **0 defs** |

`tera-server-proxy/data/opcodes/` holds only a README, so `data.json` is the whole map set.

## 2. The near misses, so nobody searches twice

| name | opcode | what it actually is |
|---|---|---|
| `C_RETURN_USER_GIFT` | `0xF7FD` | the returning-user system, with `S_RETURN_USER_REWARD_END`. `Handlers/ParcelHandlers.cs:12` already notes it "looks like it belongs here and does not" |
| `S_SPAWN_GAMBLE_BOX`, `S_DESPAWN_GAMBLE_BOX`, `S_UPDATE_GAMBLE_BOX_STATE` | `0x8D2F`, `0x9783`, `0xB3B2` | world objects, not an inbox |
| `S_TRADE_BOX` | `0xDAFD` | player-to-player trade window |
| `C_CHECK_EVENT_MATCHING_LIST_CHECKBOX` | `0xA8C2` | a UI checkbox |
| `C_GROUP_DUEL_CHANGE_REPRESENT` | `0x7A02` | matched `PRESENT` as a substring |
| 27 × `*_REWARD*` | — | each belongs to one named system: attendance, battlepass, collection book, field point, servant adventure, dark rift, GM event, event system, mento, playtime, guild quest weekly. None is a generic "items you have bought and not collected" list |

## 3. Why there is no packet: the retail button is the web view

The shop surface 376012 does have is `C_SHOW_AWESOMIUMWEB_SHOP` / `S_SHOW_AWESOMIUMWEB_SHOP`
(`0xCF8F` / `0xDFAE`) plus the catalogue pair `S_INGAMESHOP_CATEGORY_{BEGIN,DATA,END}` and
`S_INGAMESHOP_PRODUCT_{BEGIN,DATA,END,ITEMID,SUBPROD,TAG}`. The catalogue packets describe products;
the *transaction* and the *collection* live in the Awesomium panel, which is HTTP, not packets. The
Arbiter already says so at startup when the panel is unserved:

> `api-gateway probe: TERASHARP_API_GATEWAY_SERVE is not set - nothing is serving <server-ip>:8800,
> so the Alt+A panel will stay blank`

So "Item Claim" in retail 100.02 is a web page, and the items reach the character by whatever the
platform does server-side — which for us is the hub's OpArb calls.

## 4. Options

| # | option | cost | notes |
|---|---|---|---|
| 1 | **Serve Item Claim on the api-gateway** (`TERASHARP_API_GATEWAY_SERVE`, the Alt+A panel on :8800) | medium | uses the surface the client really has. The page lists `hub_boxes`; claiming POSTs back to the Arbiter, which inserts the items through the normal bag path and reports consumption to tera-api. No invented packets, and it is what retail does |
| 2 | **Keep T207's parcel delivery** and polish it | small | already works, survives a relog, uses the existing claim transaction. The weakness is cosmetic: `BoxDelivery.Sender` is the plain string `"Shop"` because the localisation keys are the client's own |
| 3 | Reuse an existing `*_REWARD*` system's packets for boxes | — | **not recommended.** Each is keyed to its own sheet and its own server-side state; the client would render shop purchases as, say, attendance rewards, and the claim would have to lie to that system |

## 5. What would change this answer

A 100.02 capture in which the client sends or receives an opcode that is **not** in `maps."376012"`
around a shop claim. That is the only way a packet absent from the map could exist. Searching the
Classic+ client's captures cannot settle it either way: Classic+ is a different build, its map is not
among the five in `data.json`, and an opcode it has that 376012 lacks still cannot be sent to our
client.

`OURS:` everything above is from the map and the def set, not from a capture of a claim attempt —
because there is no packet to capture.

## 6. Option 1, scoped: two clauses of the brief have no target

Option 1 (serve the page on the api-gateway) is mostly buildable. Two clauses are not, and both need a
decision before the code is worth writing.

### 6.1 "the page uses the session ticket the client already presents to the gateway"

The gateway is still T132's **probe**, and the thing it was built to measure has never been measured:

- `ApiGatewayServer.cs` says the client builds the Alt+A URL itself and "the PATH is decided in the
  client, and T132's research could not read it". The probe answers *every* path and logs the request
  so one Alt+A press reveals it. `TERASHARP_API_GATEWAY_SERVE` has never been on, so that press has
  never happened — the live log still prints "nothing is serving <server-ip>:8800".
- Whether a ticket arrives at all, and where, is equally unknown: `TokenFrom` tries `Authorization:
  Bearer`, then ten query-string spellings, then Cookie, precisely because nobody knows which.
- The ticket we mint carries `accountDbId` (good - that is exactly the scope key the page needs) but
  `LifetimeSeconds = 120`. It is minted at S_LOGIN_ACCOUNT_INFO, so by the time a player opens the
  panel and clicks claim it is almost always expired.
- `ApiGatewayToken.Verify` is documented "Not used in production - nothing on this stack verifies".

**Decision needed.** Either (a) press Alt+A once with SERVE=1 and send the probe's log line, which
settles path + ticket placement in one measurement and lets the page be built against fact; or (b)
pick a ticket policy now and accept it may not match: verify the HS256 signature (mandatory, and
refuse every request when `TERASHARP_API_JWT_SECRET` is unset, fail-closed like the auth provider),
read `accountDbId`, and treat `exp` as advisory up to a configurable max age because 120 s is
unusable. I would not ship (b) blind - the page's whole security rests on the ticket.

### 6.2 "consumption is reported to tera-api on the OpArb call it expects"

There is no such call. `tera-api/src/lib/hubFunctions.js` is the whole interface, and its box
functions are all addressed to **BoxAPI**, not to the Arbiter:

| function | inner id | addressed to |
|---|---|---|
| `kickUser`, `sendMsg`, `bulkKick`, `boxNotiUser`, `addBenefit`, `removeBenefit` | 2, 4, 6, 15, 38, 40 | the Arbiter (us) |
| `createBox` | 107 | `gusid.boxapi` |
| `getPageServiceItem`, `getServiceItem`, `createServiceItem`, `removeServiceItem` (SetDisableServiceItem) | 115, 116, 117, 118 | `gusid.boxapi` |

Nothing reports or queries box state, and `boxNotiUser` is inbound - tera-api telling us a user has a
box. Box state in retail belongs to the **BoxAPI** service, which this stack does have:
`ItemClaim/box/BoxAPI.py` (Python 2.6) against its own MySQL `box2db`, configured to the same hub at
`127.0.0.1:11001` that TeraSharp now answers (`ItemClaim/box/BoxConfig.ini`). `ItemClaim/` also holds
`boxadminweb` (CherryPy, :8070) and the Steer* suite - and notably **no player-facing claim page**,
which is further evidence for section 1.

So TeraSharp has displaced BoxAPI on 11001: tera-api's `createBox` lands on us, we store the box in
`hub_boxes`, and there is no second party that wants to hear about consumption.

**Decision needed.** (a) Drop the clause - record consumption in `hub_boxes` (a `claimed` state plus
who and when) and treat our DB as the record of truth, which is what the displacement implies; (b)
report to BoxAPI over the hub as a category-16 call, which means running `BoxAPI.py` and finding the
box-state gufid, neither of which is pinned yet; or (c) report out of band, e.g. an Arbiter log line
plus an admin-API endpoint, so support can reconcile without a protocol commitment.

### 6.3 What is buildable regardless

The page and JSON endpoints on the gateway (the API paths are ours, so the unknown client path only
affects the initial document), scoping by a verified ticket's `accountDbId`, a third `hub_boxes` state
for claimed-through-the-page distinct from delivered-as-parcel, consumption into the bag through the
normal insert path (live when the character is online, next login when not), the parcel path as the
fallback when the gateway is off, tests for list/claim, and the env in OPERATIONS.md.

## 7. Option 1, built

Served on the api-gateway, which is the surface section 3 says retail uses. `Web/ItemClaimApi.cs`
is the page and the two endpoints, `World/BoxClaim.cs` is the claim, and `hub_boxes` gained a third
state plus `claimed_by` / `claimed_at`.

| | |
|---|---|
| `GET /itemclaim` | the panel. Also served for **any** unrecognised path, because the client's own initial path is still unmeasured - whatever it asks for, it gets the panel. T132's probe page moved to `/probe` and its log line is unchanged, so one Alt+A press still reveals path and ticket placement |
| `GET /itemclaim/list` | `{"accountDbId":N,"boxes":[{box,title,content,state,claimable,items:[{templateId,amount}]}]}` |
| `POST /itemclaim/claim?box=N` | `{"ok":true,"via":"bag"\|"mail","items":N,"characterId":N,"parcelId":N,"message":"..."}` |

The page is ES5 with no external anything: Awesomium 1.6/1.7 has no `fetch`, no `let`/`const` and
no arrow functions, and the panel has no route to a CDN, so it is `XMLHttpRequest`, string
concatenation and an inline stylesheet. Loose retail look - dark plate, a gold header rule, one row
per box with a Collect button.

### 7.1 The ticket (6.1 answered: policy (b), hardened)

`ItemClaimApi.AccountFor` verifies the HS256 signature, reads `accountDbId`, and **refuses every
request while `TERASHARP_API_JWT_SECRET` is unset** - fail-closed like `AuthProvider`, because the
ticket is the only thing between one account's purchases and another's. `exp` is advisory:
`LifetimeSeconds` is 120 and the ticket is minted at `S_LOGIN_ACCOUNT_INFO`, so no player opening a
panel can beat it; `iat + TERASHARP_ITEM_CLAIM_MAX_AGE` (default 3600) is enforced instead. A
ticket from the future is refused.

Measured by running the real `ApiGatewayToken` against `AccountFor`: no key refused; good accepted;
null, three-segment junk and a payload re-pointed at account 1 with the original signature all
refused; 119 s old (past its own `exp`) accepted; 3601 s old refused; max age 60 honoured; +600 s
refused.

### 7.2 The consumption report (6.2 answered: (a), and (b) is impossible)

**There is no OpArb call to report on, in either direction.** Section 6.2 established that every
box function in tera-api's `hubFunctions.js` addresses `gusid.boxapi`; this pass adds the other
half - `HubServer` is purely a **callee**. `HubServer.Call(target, innerId, body)` answers inbound
calls and there is no origination path at all: tera-api connects to us, we answer. So we could not
place a consumption call even if one were defined.

What was built instead is 6.2 option (a) with a piece of (c): `hub_boxes` state 2 plus `claimed_by`
and `claimed_at` is the record of truth - we displaced BoxAPI on 11001, so our DB is where box
state lives - and every claim logs one line naming the box, the account, the character, the path
and the items, so support can reconcile without a protocol commitment. `MarkHubBoxClaimed`'s
`WHERE state=0` is also the double-claim guard: two concurrent claims cannot both win.

### 7.3 "Live via the item create path" has no target either

The brief asked for a live bag insert. The Arbiter cannot do one: `SDB_USER_LOAD_INVENTORY`
(0x27A2) is the enter-world load, World owns the bag from then on, and World **originates** the
insert atoms - we only answer them. Rows written into `items` behind a live World are lost on its
next save (the same ownership problem T234b hit with the mail badge, one layer down).

So the claim splits on whether the target character is in world: offline goes straight into `items`
at free bag slots and the next login serves it, which is the brief's own "offline: on next login";
online goes through the T207 system parcel, which is collectable immediately, without a relog, and
has World's own claim transaction create the item - the normal insert path, performed by the side
that owns the bag. `Result.Via` carries which ran so the page can say so.

### 7.4 Still unmeasured

The client's initial path and where it puts the ticket. Neither blocks the page any more - the API
paths are ours and every other path serves the panel - but until one Alt+A press is logged with
`SERVE=1`, "the client presents a ticket at all" is an assumption. If it presents none, the panel
loads and both endpoints answer 401 with "no session ticket", which is the symptom to look for.
