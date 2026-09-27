# GM-ADMINLEVEL-TRACE — where `S_LOGIN_ARBITER.status` and `AdminLevel` actually come from

Traced for T48's side question. Source: the ArbiterServer 100.02 decompile.

## 0. The headline

**They are two unrelated things, and TeraSharp currently sets one of them.**

| | `S_LOGIN_ARBITER.status` | `AdminLevel` |
|---|---|---|
| the Arbiter's own name for it | **`Result1`** | `AdminLevel` |
| where it comes from | the **hub**, verbatim — the Arbiter never computes it | a **column on the user row** |
| who sets it | `arb_gw`, in `UserLoginAns` | `WA_MAKE_ADMIN_CHARACTER` (web admin) or the QA command `SetAdminLevel` |
| what reads it | the **client** — it decides whether to offer `/@` | the **server** — 23 sites gate on `AdminLevel >= 1` |
| where it goes next | nowhere | `AS_ENTER_WORLD[0x75]` to World, `S_QA_SET_ADMIN_LEVEL` to the client |
| TeraSharp today | `GmAccounts.IsListed(name) ? 31u : 0u` | stored, but **never sent to World** |

So `status = 31` makes the client *show* the GM UI; nothing in TeraSharp makes World *honour* it.

## 1. `status` is the hub's `Result1`, passed straight through

The dumper (`Arb_part_021.c:5142`) names the packet's fields, and they do not match the `.def`:

| packet offset | Arbiter's name | the `.def` calls it |
|---|---|---|
| 0x04 | `Passed` (bool) | `success` |
| 0x05 | `Waiting` (bool) | `loginQueue` |
| **0x06** | **`Result1` (i32)** | **`status`** |
| 0x0A | `Result2` (i32) | `unk` |
| 0x0E | `SubscriptionFeeType` (i32) | `language` ← **not the language** |
| 0x12 | `PveServer` (bool) | `pvpDisabled` ← **inverted sense** |
| 0x13 | `ExpLimitLevel` (i32) | `unk1` + `unk2` |

min frame 0x16.

The chain, backwards from the wire:

```
SendToSession<PDL::PKT_S_LOGIN_ARBITER_WRITE, bool, bool, int&, int&, unsigned long, bool&, ...>
  ^ the two int& are Result1, Result2
AuthManager::ResAuthenticationProc(ClientSession, __int64 accountDbId, std::pair<int,int>, ...)
  ^ the pair IS (Result1, Result2)                        Arb_part_058.c:19176
AuthManager::ResAuthentication(ClientSession, __int64, int, int, __int64, int, bool, bool, ...)
Handler_UserLoginAns(void*, const unsigned char*, int)    Arb_part_046.c:6166 (FUN_140572890)
  Result1 = first deserialised field;
  Result2 = second deserialised field;
  FUN_1406c9040(AuthManager, &session, accountDbId, Result1);
```

Result1's field is one the message `FUN_14080d4e0` deserialises out of the hub frame, and it is
**not** behind one of the `if ((hasFieldBits >> N & 1) != 0)` has-field guards that the optional fields
use — so it is a required field of `UserLoginAns`. The Arbiter does nothing to it.

The only other path that reaches `ResAuthentication` is `Handler_PA_REPLY_ENTER_LOBBY`
(`Arb_part_046.c:4896`), and there `Result1` is the literal `(cVar1 != '\0') - 1` — that is, **0 on
success and −1 on failure**. Nothing in the Arbiter ever produces 31, 32 or 33.

**Conclusion: 31/32/33 are `arb_gw`'s account-service codes.** They come out of the hub's own auth
database and mean something to the client's UI, not to the Arbiter. Reproducing them is a matter of
choosing what to put in `Result1`, which TeraSharp already does — there is no derivation to match.

## 2. `AdminLevel` is a DB column, 1..5, and it is what actually authorises

`User+0x3b98`. The setter:

```
void __cdecl User::UpdateAdminLevel(int)          FUN_1403b4320, Arb_part_030.c:9734
  *(int *)(user + 0x3b98) = level;
  ... dbo.spUpdateUserAdminLevel(user+0x120 /* userDbId */, user+0x3b98)
```

Two callers, both administrative — never the client:

| caller | link |
|---|---|
| `Handler_WA_MAKE_ADMIN_CHARACTER` (`Arb_part_043.c:4434`) | WebAdminServer → Arbiter |
| `ArbiterQACommandHandler::SetAdminLevel` (`Arb_part_044.c:2682`) | the QA command |

How it is used, counted across the decompile:

| test | sites | meaning |
|---|---|---|
| `AdminLevel < 1` → refuse | **23** | the GM gate: any level ≥ 1 is a GM |
| `0 < AdminLevel` → allow | 16 | the same test, the other way round |
| `AdminLevel == 5` | 1 | `Arb_part_029.c:3915` — level 5 is cleared to 0 unless `param_2 == 2` |
| `AdminLevel == 0` | 1 | |

So the range is 1..5, the gate is `>= 1`, and 5 is the only level with behaviour of its own.
TeraSharp's `GmAccounts.GmAdminLevel = 5` / `MinimumAdminLevel = 1` already match.

## 3. Where AdminLevel travels

Four packets carry it, from their dumpers:

| packet | direction | note |
|---|---|---|
| **`AS_ENTER_WORLD`** | Arbiter → World | **frame `[0x75]`** = payload `[111]`, min frame 0xAD. This is how World learns a character is a GM |
| `AS_TBA_ENTER_WORLD` | Arbiter → World | the TBA variant |
| `S_QA_SET_ADMIN_LEVEL` | Arbiter → client | tells the client its level, separate from `status` |
| `WA_MAKE_ADMIN_CHARACTER` | WebAdmin → Arbiter | sets it |
| `AS_QA_SET_GAME_OPERATOR` | Arbiter → World | the operator flag, a separate thing |

The neighbours pin the offset: `[0x6D] SubscriptionFeeType`, `[0x71] AccountRestrictionLevel`,
`[0x75] AdminLevel`. `World/DbProxyHandlers.cs` already documents exactly this
(`[103] SubscriptionFeeType [107] AccountRestrictionLevel [111] AdminLevel`), so the layout was
already right — it is simply never populated.

## 4. What to change

1. **`Handlers/WorldEntry.cs` (human-owned)** — write the character's admin level into the
   `AS_ENTER_WORLD` payload at `[111]`. It is currently left zero, so World treats every GM as an
   ordinary player no matter what `status` said. `GmAccounts.EffectiveLevel(accountName, stored)`
   already computes the right number.
2. **Send `S_QA_SET_ADMIN_LEVEL`** after enter-world for a GM, so the client's own level matches
   the server's rather than being inferred from `status`.
3. **Keep `status` as it is.** `31` is a reasonable stand-in for "the hub said this is a QA
   account"; it is not derived from `AdminLevel` on the real server either, and mapping the two
   together is a TeraSharp convention, not a protocol rule. Worth writing down as such: the
   tera-api privilege field should set **both** the stored `AdminLevel` (which authorises) and the
   `status` the client is told (which decorates).
4. **`S_LOGIN_ARBITER`'s def is mislabelled** — `language` is `SubscriptionFeeType` and
   `pvpDisabled` is `PveServer` (opposite sense). `LoginHandlers` echoes the client's language into
   `SubscriptionFeeType` today, which is harmless but wrong; worth a comment before someone relies
   on it.
