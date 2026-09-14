# Account auth — how the ticket really works, and what TeraSharp does with it — T31

TeraSharp accepted every account name that asked. This is what the real stack does instead, and
the provider that now implements it.

Sources: `Handler_C_LOGIN_ARBITER` and `AuthManager::*` in `Arb_part_*.c`, the packet dumper at
`Arb_part_014.c:239-303`, tera-api's own source on disk (`D:\v100\TERA_SERVER.100\tera-api`), and
`D:\packetlogs\cap_newchar_client.log` frames 2 and 7.
**No build was possible in the Cowork container**; the packet claims were re-derived in Python
from the capture and the tests assert them.

---

## 1. The chain

```
launcher  --(portal API, session cookie)-->  tera-api  GetAuthKeyAction
                                             authKey = uuid v4  ->  account_info.authKey
          <--------------------------------  { AuthKey }

client    --C_LOGIN_ARBITER------------------>  Arbiter
             name   = accountDBID as text ("1")
             ticket = the authKey, 36 ASCII bytes

Arbiter   --GwArb UserLoginReq (type id 3)--->  hub
hub       --POST /authApi/GameAuthenticationLogin-->  tera-api
             { authKey, clientIP, userNo }
          <--{ Return, ReturnCode, Msg }
Arbiter   <--UserLoginAns (AuthStatus)--------  hub
client    <--S_LOGIN_ARBITER { Passed, Result1 = AuthStatus }
```

Two things matter for us:

* **The Arbiter does not validate the ticket itself.** `Handler_C_LOGIN_ARBITER`
  (Arb_part_079.c:10058) hands the raw bytes to
  `AuthManager::ReqAuthentication` (Arb_part_058.c:18148), which sends `UserLoginReq` to the hub
  and returns. `S_LOGIN_ARBITER` is only written later, by `AuthManager::ResAuthentication`
  (Arb_part_058.c:18659), from `Handler_UserLoginAns` (Arb_part_046.c:6225). A rejected ticket
  gets `S_LOGIN_ARBITER { Passed = 0, Result1 = AuthStatus }` followed by `Session::Disconnect`.
* **There is no auth URL in the binary.** The API address is configuration, shipped to the
  *client* as `APIServerAddress` / `APIServerAccessToken` in S_LOGIN_ACCOUNT_INFO. The endpoint
  that actually checks the key lives in tera-api, and TeraSharp (which has no hub) calls it
  directly - the same request the hub would make.

## 2. The ticket is a byte array, not a string

`C_LOGIN_ARBITER.2.def`:

```
int32  unk1          # = AccountId per the dumper (0 in every capture)
byte   unk2          # = IsBot
uint32 language      # = LocalizeRegion (6 = EUR)
int32  patchVersion  # = BuildVersion (10002)
string name          # = AccountName -- the accountDBID in decimal
bytes  ticket        # = the authKey, ASCII
```

The dumper reads `AccountName` with the wide-string reader and `Ticket` with the byte-array reader
(position **and** length), and the handler passes it on as `const unsigned char* + int`. So while
every other string on this protocol is UTF-16, the ticket is **36 raw ASCII bytes**:
`34 35 38 32 34 65 34 35 2D …` = `45824e45-4757-4c6d-8285-37ac8ce5eae4` (capture frame 2).
`AuthTicket.Decode` handles both anyway.

`userNo` for tera-api comes from the **name** field, not from `unk1`/AccountId: the launcher puts
the accountDBID in the name and leaves AccountId 0 (capture frame 2 has `name = "1"`,
`unk1 = 0`).

## 3. The endpoint

tera-api `src/controllers/arbiterAuth.controller.js`, mounted at `/authApi` by
`src/routes/arbiter.index.js` on the **Arbiter API** server
(`API_ARBITER_LISTEN_HOST/PORT` — `127.0.0.1:8080` in the human's `.env`):

```
POST /authApi/GameAuthenticationLogin
{ "authKey": "<uuid>", "clientIP": "127.0.0.1", "userNo": "1" }

200 { "Return": true,  "ReturnCode": 0,     "Msg": "success" }
200 { "Return": false, "ReturnCode": 50000, "Msg": "account not exist" }
200 { "Return": false, "ReturnCode": 50011, "Msg": "authkey mismatch" }
200 { "Return": false, "ReturnCode": 50012, "Msg": "account banned" }
200 { "Return": false, "ReturnCode": 2,     "Msg": "invalid parameter: ..." }
500 { "Return": false, "ReturnCode": 1,     "Msg": "internal server error" }
```

It looks the account up by `userNo` (= `accountDBID`), compares `authKey`, then checks the ban
table by account and by IP. Note every failure is an **HTTP 200 with `Return: false`** — status
code alone tells you nothing, which is why the provider parses the body. The sibling endpoint
`/authApi/RequestAuthkey` reads the stored key back out and is not part of login.

The key is **not** consumed: `GameAuthenticationLogin` never clears `authKey`, so the same ticket
works until the launcher mints a new one.

## 4. What TeraSharp does now

```
Auth/AuthProvider.cs        AuthRequest, AuthResult, IAuthProvider,
                            AcceptAllAuthProvider, AuthProviders (selection + blocking call)
Auth/TeraApiAuthProvider.cs the POST above
Auth/AuthTicket.cs          ticket decoding and the uuid shape check
```

| env | provider | behaviour |
|---|---|---|
| unset, or `TERASHARP_AUTH` not `true`/`1` | `AcceptAllAuthProvider` | **the default** — today's behaviour exactly |
| `TERASHARP_AUTH=true` | `TeraApiAuthProvider` | POSTs to `TERASHARP_AUTH_URL` (default `http://127.0.0.1:8080`) |

**Fail closed.** A timeout, a refused connection, a non-200, a body that will not parse, a missing
ticket, or a name that is not an accountDBID all reject the login. `AuthProviders.Authenticate`
also turns any unexpected exception into a rejection, so a bug in the provider cannot open the
door. The rejection path sends the same `S_LOGIN_ARBITER { success = false }` TeraSharp already
sends.

What is deliberately NOT implemented: the GwArb hub protocol (protobuf `UserLoginReq`, type id 3),
because TeraSharp has no hub connection and the HTTP endpoint is the same authority; and the
outbound API access token (the Arbiter mints a JWT with issuer `arbiter` through `APITokenIssuer`
and ships it as `apiServerAuthToken`) — we send an empty token and nothing has asked for it.

## 5. The wiring the human owns

`Handlers/LoginHandlers.cs` currently calls `ValidateAccount`, which GETs
`{AuthApiUrl}/auth/validate?account=…` — **an endpoint that does not exist in tera-api**; with
`TERASHARP_AUTH=true` every login would fail closed on a 404. Replacing it is the whole diff:

```csharp
// top of the file
using TeraSharp.Arbiter.Auth;

// delete ValidateAccount entirely, and in OnLoginArbiter replace the auth gate with:
string ticket = AuthTicket.Decode(f != null && f.TryGetValue("ticket", out var tk) ? tk : null);
int patch = f != null && f.TryGetValue("patchVersion", out var pv) ? Convert.ToInt32(pv) : 0;
var verdict = AuthProviders.Authenticate(Program.Auth,
    new AuthRequest(s.Account.Name, (long)s.Account.AccountId, ticket, "", language, patch));
if (!verdict.Accepted)
{
    _log.LogWarning("C_LOGIN_ARBITER: account '{Name}' rejected by {Provider}: {Code} {Msg}",
        s.Account.Name, Program.Auth.Name, verdict.Code, verdict.Message);
    s.SendByDef("S_LOGIN_ARBITER", new Dictionary<string, object>
    {
        ["success"] = false, ["loginQueue"] = false, ["status"] = (uint)verdict.Code, ["unk"] = 0u,
        ["language"] = language, ["pvpDisabled"] = false, ["unk1"] = (ushort)0, ["unk2"] = (ushort)0,
    });
    return true;
}
```

and in `Program.cs`:

```csharp
public static IAuthProvider Auth { get; internal set; } = new AcceptAllAuthProvider();
// in Main, after the logger exists:
Auth = AuthProviders.FromEnvironment(log);
log.LogInformation("Auth provider: {Name}", Auth.Name);
```

`Program.AuthEnabled` / `Program.AuthApiUrl` can then go; `AuthProviders.FromEnvironment` reads the
same two variables.

Optional, also human-owned: expose the peer address on `GameSession` (e.g.
`public string RemoteIp => ((IPEndPoint?)_socket.RemoteEndPoint)?.Address.ToString() ?? ""`) and
pass it as the `clientIp` of `AuthRequest`. tera-api only uses it for the ban-by-IP check, and it
is configured with `API_ARBITER_USE_IP_FROM_LAUNCHER=false`, so until then we send `127.0.0.1`.

## 6. Turning it on

1. Log in through the launcher so tera-api writes a fresh `account_info.authKey`.
2. Start TeraSharp with `TERASHARP_AUTH=true` (and `TERASHARP_AUTH_URL` if tera-api is not on
   `127.0.0.1:8080`).
3. A wrong or stale ticket now logs `50011 authkey mismatch` and the client stops at the login
   screen instead of reaching the lobby.
