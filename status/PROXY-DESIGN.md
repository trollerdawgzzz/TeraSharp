# tera-server-proxy and the mod ecosystem

What sits between the player and TeraSharp, what it is allowed to do to a packet, and which
community mods a player may run. Written T117 from the tree at 2026-09-20.

Sources read: `D:\v100\TERA_SERVER.100\tera-server-proxy` (our fork, including the
`tera-network-proxy` / `tera-data-parser` / `tera-mod-management` packages under `node_modules`),
`D:\mods` (40 community mods), and `TeraSharp-cowork\src` for the server half.

**Two corrections to the brief before anything else.**

1. **There is no `tera-server-proxy-new` on this box.** Not under `D:\v100\TERA_SERVER.100`, not at
   `D:\`, not under `D:\100.02` or `D:\v31`. Upstream is identified instead from
   `package.json` - `github.com/justkeepquiet/tera-proxy-server`, author Caali, "Based on
   TeraToolbox" - and the fork delta is measured from file mtimes, which is exact (section 10).
2. **`D:\mods` is not the proxy's mod folder.** The proxy loads `tera-server-proxy\mods`, which
   holds seven mods. `D:\mods` is a 40-mod library of **TeraToolbox client-side** mods - the kind
   a player runs on their own machine. The two are the same format and the same API, but one
   runs once for everybody and the other runs once per player, and that distinction is what the
   whole policy in section 12 turns on.

---

## 1. Connection flow

```
   TERA client
       |  encrypted client protocol, TCP
       v
   tera-server-proxy      0.0.0.0:7801        <- the only port players reach
       |  same protocol, re-encrypted
       v
   TeraSharp.Arbiter      127.0.0.1:7701
```

`config.json` -> `bin/proxy.js` -> `net.createServer` per entry in `servers[]`. One
`ConnectionManager.start()` per accepted socket; it builds a `Connection`, a `RealClient` around
the player's socket, and dials `serverIp:serverPort`. `socket.setNoDelay(true)` on both sides.

### The session key exchange

`Connection.state` is the whole of it, and the proxy is **transparent** until state 2:

| state | server -> proxy | proxy does |
|---|---|---|
| -1 | 4 bytes, `u32 == 1` (the hello) | forwards verbatim, state := 0 |
| 0 | 128-byte server key 1 | copies into `session.serverKeys[0]`, forwards, state := 1 |
| 0/1 | (client sends 128-byte client key) | `setClientKey` copies into `session.clientKeys[n]` and forwards it on |
| 1 | 128-byte server key 2 | copies into `serverKeys[1]`, `session.init()`, forwards, state := 2 |
| 2 | ciphertext | decrypt, reframe, dispatch, re-encrypt, forward |

So the proxy does not mint keys. It relays the four real keys untouched and derives the same
session the two endpoints derive. `Session.init()` (`connection/encryption/index.js`) is TERA's
own: shift `serverKeys[0]` by -67, xor with `clientKeys[0]`, xor with `clientKeys[1]` shifted +29,
expand that through SHA-0 into the decryptor; then `serverKeys[1]` shifted -41, run through the
decryptor, expanded the same way into the encryptor. (`-31/+17/-79` are the pre-patch-45
constants; `majorPatchVersion` 100 takes the modern set.)

**The proxy holds two independent cipher states.** `RealClient.onData` clones the keys
(`session.cloneKeys()`) the first time it forwards at state 2, so the client-facing stream and
the server-facing stream advance separately. A cipher is a stream cipher, so this is what makes
dropping or resizing a packet possible at all: if the proxy silences a packet, only one of the two
streams skips those bytes and the other never knows.

### Framing

`packetBuffer.js` reassembles `[u16 totalLength][u16 opcode][body]` out of the TCP stream, with
the two-byte-straddle case handled explicitly. `dispatch.handle()` reads the opcode at offset 2.
A packet that survives is re-emitted whole; `false` means it is dropped.

### Packet integrity, and why it is off

`config.json` has `"integrity": false`, which `proxy.js` passes as `noIntegrity` to every
`Connection`. With integrity **on** and `majorPatchVersion >= 100`, the connection hooks
`S_LOGIN_ACCOUNT_INFO.3` at `order: -Infinity` and takes `antiCheatChecksumSeed` from it; from
then on, every client->server packet whose name is in
`tera-network-proxy/lib/connection/integrity/data/padding.json` gets a counter at body +4 and a
checksum at body +8 restamped by `PacketIntegrity.apply()` before it is re-encrypted. **33
packets are padded on protocol 376012** - the skill, item-use, enchant, contract and warehouse
ones (full list in that file).

It can be off because nothing on this stack checks the checksum: the seed comes from
`S_LOGIN_ACCOUNT_INFO`, XignCode validates it on retail, and neither the modified
`ArbiterServer_m1.exe` nor TeraSharp reads bytes 4..11 of a padded packet at all. Turning it on
would cost a hook and a per-packet loop over the whole body for those 33 opcodes and buy nothing
here.

**But the padding is still on the wire**, and that matters to TeraSharp:

> `C_REPLY_THROUGH_ARBITER_CONTRACT` is one of the 33, and TeraSharp registers a handler for it -
> the only padded packet it registers. `World/ContractBroker.cs` says
> "`C_REPLY_THROUGH_ARBITER_CONTRACT.1.def` is wrong and must not be used", and reads by absolute
> offset instead: `nameOff@0x0C, type@0x0E, id@0x12, index@0x16, reply@0x1A`, total `0x1E` = 30.
> The def declares `uint32 type / uint64 id / uint32 response / string recipient`, which is
> `2 + 4 + 8 + 4 = 18` bytes of fixed part. **4 (header) + 8 (pad) + 18 = 30.** The def is not
> wrong; it is the same layout, and the eight bytes it appears to be missing are the anti-cheat
> pad, which the `.def` format has no way to express. TeraSharp's parser already skips them
> because it was derived from the handler. (It also reads the def's `uint64 id` as two `int32`s,
> `id` and `index`.) The comment in `ContractBroker.cs` should say that instead.
>
> The live rule that falls out: **if TeraSharp ever registers a handler for one of the other 32
> padded packets, its body starts at +12, not +4.** TeraSharp has no notion of padding anywhere
> in `Protocol/` or `Network/`, so this will be silent when it happens.

---

## 2. Opcodes and definitions

`LoadProtocolMap(dataFolder, 376012)` in `bin/proxy.js`:

1. `data/data.json` -> `maps["376012"]`, **2167** `name -> opcode` entries. The file also carries
   `maps` for 366226, 367078, 367080 and 367081; the one used is
   `config.json`'s `protocolVersion`.
2. `data/opcodes/protocol.376012.map`, if present, is parsed and **merged under** the base map
   (`Object.assign(customMap, baseMap)` - the base wins on a clash, so the custom file only adds
   names the bundle does not have). No such file exists today; the folder holds only a README.

Definitions load separately, in `tera-data-parser`'s `protocol.load(dataFolder)`:

1. `data.json`'s `protocol` object - **857** base64-encoded `.def` files - is the base bundle;
2. `data/definitions/*.def` - **918** files on disk - overlays it, and wins.

Both maps are then turned into `protocolMap.name` / `protocolMap.code` and a 0x10000-entry
`padding` array, and `latestDefVersion` records the highest def version per packet.

**What breaks when they are wrong.**

- **Empty or missing map for the version.** `proxy.js` logs eight `warning-unmapped-protocol-*`
  lines and **carries on**: `metadata.protocol` is never constructed, so `dispatch.handle` finds
  no hooks and every packet is passed through unread. The proxy becomes a dumb TCP relay - which
  means `C_ADMIN` is no longer gated (section 5). This is a silent, fail-open failure and is the
  single most dangerous misconfiguration in the file.
- **Wrong `protocolVersion` in `config.json`.** Same as above, or worse: a *different* valid
  version loads a map whose opcodes mean other packets, so mods hook the wrong ones.
- **Missing def for a hooked packet.** `dispatch.createHook` throws
  `hook: unmapped packet "X"` (no opcode) or `hook: obsolete definition (X.n)` (def not
  readable). `mod.hook` propagates that and the **mod fails to load for that connection**;
  `mod.tryHook` swallows it and returns null, which is why `exploit-fix` uses `tryHook` and then
  checks the result.
- **A def that parses but has the wrong fields.** The worst case, because nothing errors: the
  mod reads garbage, and if it returns `true` the packet is *re-serialised from that garbage* and
  sent on. The T95/T51 lesson - the shipped defs disagree with the 100.02 binary in at least
  eleven places (`status/GUILD-DESIGN.md`, `status/CLIENT-REJECTS.md`) - applies to the proxy
  exactly as it applies to TeraSharp.
- **Stale `latestDefVersion`.** Only a `log.debug` line; harmless.

---

## 3. The mod system

### Load order

`ModManager.loadAll()` (`bin/mod-manager.js`), once at proxy start:

1. `listModules(mods/)` - every entry that is a directory or a bare `.js`, skipping names that
   start with `.` or `_`.
2. `loadModuleInfo` per entry, merging `module.json` with `module.config.json` (the latter is
   what a GUI toggle writes: `disabled`, `disableAutoUpdate`, `drmKey`). `"disabled": true` drops
   it here.
3. **Dependency and conflict resolution, in a loop until nothing changes.** A mod whose
   `dependencies` are not all installed is removed with an error; a mod whose `conflicts` names
   an installed mod is removed with an error. The loop is order-dependent for a mutual conflict:
   of a pair that names each other, the first one visited is removed and the second survives.
4. **Core mods first**, then the rest. "Core" is a hardcoded list in `tera-mod-management`:
   exactly `command` and `tera-game-state`. Everything else loads in `Map` insertion order, i.e.
   directory order.
5. `mod.loadCache()` `require()`s the folder. A throw here is caught, logged, and the mod is
   simply not loaded - **the proxy still starts**.

Per connection, `ConnectionManager` calls `modManager.loadAllNetwork(dispatch)`, which repeats the
core-first ordering and constructs one `NetworkMod` instance per mod per connection. Unload
reverses it: non-core first, then core.

### The three mod shapes

`module.exports` may export `GlobalMod` (one per process), `ClientMod` (one per game client, via
the client interface) and/or `NetworkMod` (one per connection). A module that exports none of
these - a bare `module.exports = function Foo(mod) {...}` - is wrapped by
`bin/mod-legacy-wrapper.js` as a **NetworkMod**, unless its keywords include `client`. Every mod
in `tera-server-proxy\mods` except `tera-game-state` is in that legacy form.

### The hook API

`mod.hook(name, version, opts, callback)` -> `dispatch.createHook` -> `addHook`.

- `version` is a number (parse with that def version), `'raw'` (get the whole buffer), or
  `'event'` (fire-and-forget, no parse). `'*'` as the *name* hooks every packet.
- `opts.order` is an integer, default 0; hooks run in ascending order, global (`*`) hooks and
  per-opcode hooks merged by order. Negative = earlier.
- `opts.filter` defaults to `{ fake: false, incoming: null, modified: null, silenced: false }`.
  `null` means "don't care". **`incoming` is the only direction check there is** - the dispatcher
  fires by opcode regardless of which way the packet travelled, which is the hole `spoof-guard`
  exists to close (section 5).

### What a callback may return, and what the client sees

| return | effect |
|---|---|
| `undefined` | nothing; the packet continues unchanged |
| `true` (parsed hook) | the event object is **re-serialised** over the packet; `modified = true`, and `silenced` is reset to false |
| `false` | `silenced = true`; at the end `handle()` returns `false` and the packet is **never written to the other side** |
| a `Buffer` (raw hook) | replaces the packet if it differs; `modified = true` |

`handle()` ends with `return (!silenced ? data : false)`. A dropped packet is dropped for the
**recipient only**: a silenced `C_*` never reaches TeraSharp, and a silenced `S_*` never reaches
the client. Both sides' cipher streams stay consistent because each direction is encrypted
separately (section 1).

Injection is `dispatch.write(outgoing, name, version, data)`, exposed as
`mod.send(name, version, data)` (direction inferred from the `C_`/`S_`/`I_` prefix),
`mod.toServer`, `mod.toClient`. An injected packet is built from the def, then **run through
`handle()` with `fake = true`** - so other mods can see and drop it, and a hook with the default
`filter.fake === false` will not - and only then written. This is how `fake-ping` answers a ping
the server never saw, and how `command` prints chat lines that never existed.

Other surface: `mod.game` (the `tera-game-state` instance), `mod.command` (the `command`
instance), `mod.settings` + `saveSettings()` (backed by `options.settingsFile`),
`mod.setTimeout/setInterval` (tracked and cleared on unload), `mod.log/warn/error`,
`mod.require.<other mod>` via `RequireInterface`.

---

## 4. The bundled mods

Seven mods in `tera-server-proxy\mods`. They run **for every player** on this server.

| Mod | Core? | What it does | Packets it touches |
|---|---|---|---|
| `command` | **yes** | Chat-command framework, and the `C_ADMIN` gate (section 5). Intercepts `~`-prefixed chat and whispers, runs them as commands, prints replies as fake `S_CHAT`/private-channel messages. | hooks `S_LOGIN`, `C_LOGIN_ARBITER`, `S_LOGIN_ARBITER.3`, `C_ADMIN.1`, `S_SPAWN_ME`, `C_CHAT.1` (order -10 and +10), `C_WHISPER.1` (same pair), `S_JOIN_PRIVATE_CHANNEL`, `S_PRIVATE_CHAT`, `S_REQUEST_PRIVATE_CHANNEL_INFO`; sends `S_CHAT`, `C_LEAVE_PRIVATE_CHANNEL`, `C_REQUEST_PRIVATE_CHANNEL_INFO` |
| `tera-game-state` | **yes** | Read-only state tracker (`mod.game`): account, language, lobby/ingame/loading state. Every hook is `order: -9999` with all filters `null`, and none of them returns anything. | `C_LOGIN_ARBITER.2`, `S_LOGIN_ACCOUNT_INFO.3`, `S_GET_USER_LIST`, `S_RETURN_TO_LOBBY`, `S_LOGIN`, `S_LOAD_TOPO`, `S_SPAWN_ME`, `S_EXIT`, `S_SELECT_USER`, `S_TBA_SELECT_USER` (+ `lib/me,party,inventory,glyphs,talents,contract` submodules) |
| `exploit-fix` | no | Drops client packets that crash the Arbiter. Five checks, all derived from the decompile and documented inline. | `C_REQUEST_PVP_RANKING.1` (field check on `id`), `C_REQUEST_PVE_RANKING` (**raw, dropped wholesale**), `C_CHECK_VERSION.1` (array count 1..8, index 0..16), `C_VISIT_NEW_SECTION.1` (`guardId < 64`, WorldId/SectionId sanity, AreaData allowlist), `C_VIEW_GUILD_WAR.1` (page 0..1000) |
| `spoof-guard` | no | `order: -1000`, `incoming: false`: drops any **client-sent packet whose opcode maps to an `S_*` name**. Closes the direction hole in the dispatcher. | `*` raw |
| `fake-ping` | no | Answers `C_REQUEST_GAMESTAT_PING` locally with an empty `S_RESPONSE_GAMESTAT_PONG` and drops both the request and any real pong. | `C_REQUEST_GAMESTAT_PING.1` (dropped), `S_RESPONSE_GAMESTAT_PONG` raw (dropped) |
| `packet-logger` | no | `order: -9999`, `filter {fake:false}`: appends name/opcode/direction/length/full hex to `packet-logs/capture_<ISO timestamp>.log`, flushed every 2 s. Drops nothing. | `*` raw |
| `ranking-crash-probe` | no | **`"disabled": true`** - test-only. Adds a `~rankprobe` command that sends one crafted `C_REQUEST_PVP_RANKING` to reproduce the crash on an isolated Arbiter. | sends `C_REQUEST_PVP_RANKING.1` |

**One finding on `exploit-fix`.** Its comment says "this build ships a def for
`C_REQUEST_PVP_RANKING` but NOT for `C_REQUEST_PVE_RANKING`", and on that basis it drops **every**
PvE ranking request. That is not true of this tree: `C_REQUEST_PVE_RANKING.1.def` is in
`data.json`'s bundle, with the same three fields as the PvP one
(`int32 season / int32 id / int32 class`), and `C_REQUEST_PVE_RANKING` is in the 376012 map at
40996. It is only missing from the `data/definitions/` overlay folder, which is where the author
looked. The code never calls `tryHook` on it to find out. Consequence: the PvE ranking window is
dead for every player on the server, silently. Switching it to the same field check as PvP is a
four-line change in a Cowork-editable file - **not made here, because T117 is docs-only.**

---

## 5. The `C_ADMIN` gate, and everything else the proxy drops or rewrites

### The gate

`mods/command/index.js`, `CommandBase`:

```js
mod.hook('C_LOGIN_ARBITER', 'event', { filter: { incoming: false } }, () => { this.access = false; });
mod.hook('S_LOGIN_ARBITER', 3,       { filter: { incoming: true  } }, event => {
    if (event.status === 31 || event.status === 33) this.access = true;
    return true;
});
mod.hook('C_ADMIN', 1,               { filter: { incoming: false } }, event => this.access);
```

`C_ADMIN.1.def` is `string command`. The hook returns `this.access`: `false` silences the packet,
so a non-privileged account's `/@` command never reaches the server at all; `true` re-serialises
and forwards it. `access` is reset on every `C_LOGIN_ARBITER`, and only the **server's**
`S_LOGIN_ARBITER` can set it - both hooks are pinned to a direction, and `spoof-guard` drops a
client-sent `S_LOGIN_ARBITER` before this hook ever sees it. Those two pins are the local security
fix (section 10); upstream had neither, and a forged `S_LOGIN_ARBITER` with `status: 33` unlocked
GM commands for anyone.

`status` is `S_LOGIN_ARBITER` body +2, a u32. **31** = QA commands, **32** = GM panel (Alt+A),
**33** = both; TeraSharp sends 31 or 33 from `GmAccounts.LoginStatusFor` and never 32. Note the
proxy's check is `=== 31 || === 33`, so an account sent 32 would get the Alt+A panel from the
client but would **not** pass the proxy's `C_ADMIN` gate.

### Everything the stock stack drops or rewrites

| Packet | Direction | Mod | What happens |
|---|---|---|---|
| any `S_*` opcode | C -> S | spoof-guard | dropped, logged `[spoof-guard] DROPPED ...` |
| `C_ADMIN` | C -> S | command | dropped unless `status` was 31/33 |
| `C_CHAT`, `C_WHISPER` | C -> S | command | dropped when the text starts with `~` (a command); otherwise untouched |
| `C_REQUEST_GAMESTAT_PING` | C -> S | fake-ping | dropped; a fake `S_RESPONSE_GAMESTAT_PONG` is injected to the client |
| `S_RESPONSE_GAMESTAT_PONG` | S -> C | fake-ping | dropped |
| `C_REQUEST_PVP_RANKING` | C -> S | exploit-fix | dropped when `id` is not in `{0..12, 15}` |
| `C_REQUEST_PVE_RANKING` | C -> S | exploit-fix | **always dropped** (see the finding above) |
| `C_CHECK_VERSION` | C -> S | exploit-fix | dropped on bad array count or index |
| `C_VISIT_NEW_SECTION` | C -> S | exploit-fix | dropped on `guardId >= 64`, insane WorldId/SectionId, or a world:guard pair absent from `area-allowlist.json` |
| `C_VIEW_GUILD_WAR` | C -> S | exploit-fix | dropped when `page < 0` or `> 1000` |
| `S_CHAT` / private-channel frames | S -> C | command | **injected**, never rewritten - command replies are fake packets |

Nothing else is touched. In particular the proxy does **not** rewrite `S_LOGIN_ARBITER`,
`S_LOGIN_ACCOUNT_INFO`, or anything in the lobby.

---

## 6. `config.json`

One object, `servers: [ ... ]`, one entry per listener. All keys are read in `bin/proxy.js`.

| Key | Value here | Meaning |
|---|---|---|
| `listenIp` | `0.0.0.0` | bind address for the player-facing listener |
| `listenPort` | `7801` | the public port |
| `serverIp` | `127.0.0.1` | where to forward - TeraSharp |
| `serverPort` | `7701` | " |
| `name` | `Tera Private 100.02` | log line only |
| `serverId` | `2800` | exposed as `mod.serverId`; mods use it to pick per-server config |
| `publisher` | `GF` | metadata; selects publisher-specific defs where any exist |
| `language` | `eu` | metadata, exposed as `mod.language` |
| `patchVersion` | `100.02` | split into `majorPatchVersion` 100 / `minorPatchVersion` 2 - **100 is what selects the modern crypto constants and the `S_LOGIN_ACCOUNT_INFO.3` integrity seed** |
| `protocolVersion` | `376012` | selects `maps["376012"]` and the padding table |
| `integrity` | `false` | inverted into `noIntegrity` (section 1) |

Two more keys are read but absent from this file: `devmode` (top level, sets
`global.TeraProxy.DevMode`) and `branch` (top level, read by `index-cli.js` and then unused - it
is a leftover from the auto-updating client toolbox).

There is **no auto-update in this fork.** `node-fetch` is a declared dependency and nothing in
`bin/` calls it. Each mod's `module.json` still carries a `servers` array pointing at a GitHub or
GitLab raw URL; nothing reads it here. That is the safe state, and it should stay that way.

---

## 7. Multi-client behaviour

- **One `Connection`, one `Dispatch`, one `RealClient` and one hook table per TCP connection.**
  They are held in `ConnectionManager.activeConnections` (a `Set`) and removed on `close` or
  `error`.
- **One `NetworkMod` instance per mod per connection**, constructed in `loadAllNetwork` on the
  server socket's `connect` event and destroyed in `unloadAllNetwork` on `close`/`error`. Mod
  state is therefore per-player by default; anything shared has to live in a `GlobalMod` or in
  module scope.
- **`packet-logger` writes one file per connection**, because its log path is computed in the
  `NetworkMod` constructor from `new Date()`. Two clients connecting in the same millisecond
  would collide on the filename; in practice they do not. It also means the file count grows with
  every relog - it is a capture tool, not a production logger.
- **No concurrency limit anywhere.** No max-connections, no per-IP cap, no rate limit, no
  timeout: `net.createServer` accepts everything, and every accepted socket immediately opens a
  second socket to 7701. The only backpressure is the OS. See section 9.
- The proxy raises its own process priority (`setHighestProcessPriority`) so a busy game client on
  the same box cannot starve it. On the server that is pointless but harmless.
- **`console.log` is globally reassigned** in `index-cli.js` to prefix `[pid] [HH:MM:SS.mmm]`, and
  `console.error`/`console.warn` are aliased to it. Every mod's output lands on the same stdout,
  interleaved across all connections, with no connection id. A mod that wants to say which player
  it is talking about has to say so itself - `spoof-guard` and `exploit-fix` do, via
  `${mod.game.serverId}-${mod.game.accountId}`.

---

## 8. Failure modes

| What fails | What happens |
|---|---|
| **Proxy process dies / is not started** | Port 7801 refuses. Players cannot connect at all. TeraSharp is untouched and keeps running; so does World. Nothing is lost - restart the proxy and players reconnect. |
| **Proxy is running, TeraSharp is down** | `net.connect` to 127.0.0.1:7701 fails with `ECONNREFUSED`; `onConnectionError` logs it, the connection is dropped from `activeConnections`, and the player's socket is closed by `Connection.close()`. The player sees a normal disconnect. No crash. |
| **TeraSharp dies mid-session** | `serverConnection` `close` -> `Connection.close()` -> `client.close()`. Every player is disconnected cleanly. World, per `status/CHAT-HANDOFF.md`, does **not** survive the Arbiter link dropping and has to be restarted too. |
| **A mod throws in its constructor** | `loadNetworkInstance` catches it, logs `mod-network-instance-load-error`, calls the interface destructor and returns null. **That one mod is absent for that one connection**; every other mod and the connection itself continue. If it is `command`, that connection has no `C_ADMIN` gate - see section 9. |
| **A mod throws inside a hook** | `dispatch.handle` catches per hook and logs the packet hex, the hook name and the stack. The packet continues to the next hook **unmodified and unsilenced**. A crash in `exploit-fix` therefore fails **open**. |
| **A mod fails to `require()` at startup** | `loadCache` catches, logs, and the mod is never installed. The proxy starts without it, and prints nothing further about it. |
| **A hook is registered for an unmapped packet** | `createHook` throws; with `mod.hook` that propagates into the constructor and takes the whole mod down for that connection (previous row), with `mod.tryHook` it is swallowed. |
| **Uncaught exception anywhere else** | `index-cli.js` installs `process.on('uncaughtException')` which logs and **keeps the process alive**, with `process.stdin.resume()` holding it open. A wedged proxy will therefore look alive. |
| **Protocol map empty** | Logged as warnings, then pass-through mode: no parsing, no hooks, no gate (section 2). |

---

## 9. The security surface on 7801, and what the proxy does not protect

What is exposed: one TCP listener on `0.0.0.0:7801`, plus - through it - everything TeraSharp
answers on 7701.

**What the proxy does protect.**

- `C_ADMIN` from a non-privileged account (`command`).
- Client-sent `S_*` opcodes (`spoof-guard`), which is what stops a player forging the packet that
  unlocks the gate above.
- Five Arbiter crash vectors (`exploit-fix`).
- The real server IP and port: 7701 is never advertised.

**What it does not.**

- **No rate limit, connection cap or timeout.** Anyone can open sockets on 7801 until the box runs
  out of handles, and each one costs a second socket to 7701 and a full set of mod instances.
  Nothing logs an offender beyond the per-connection `connected`/`disconnected` lines.
- **No authentication of its own.** Every byte before `C_LOGIN_ARBITER` is forwarded blind; the
  login ticket is checked by TeraSharp (`TERASHARP_AUTH`), not here.
- **Mods fail open.** A throwing hook drops the check, not the packet (section 8).
- **`exploit-fix` only covers what has been traced.** Five packets out of 273 the Arbiter
  handles. It is a patch list, not a validator.
- **It cannot see client-side mods at all.** A player running TeraToolbox on their own machine
  sits *in front of* 7801. Their packets are ordinary, well-formed, correctly-encrypted client
  packets. Nothing in this proxy, and nothing in TeraSharp, can distinguish them from a vanilla
  client. Section 12's policy is enforceable only by server-side validation and by observing
  outcomes - never by inspection.
- **The player's real IP is lost.** Every connection TeraSharp sees is 127.0.0.1, so IP bans in
  tera-api do not work and `AuthRequest.ClientIp` is meaningless. The proxy's README says so.
- **`packet-logger`, if left enabled, writes every packet of every player to disk** - chat,
  whispers, account ids - in plain hex, forever. It is a capture tool for the rewrite. It must
  not run in production.

### If a player runs NO proxy and connects to 7701 directly

This matters because the README frames the proxy as the GM gate, and `CLAUDE.md` section 0
repeats it. Against **TeraSharp** the picture is different from the real ArbiterServer:

- **They can connect and play normally.** 7701 speaks the same protocol; the proxy adds nothing
  to the handshake. Only the firewall stops this (`tools/harden-netcup.ps1` - 7701 is loopback
  only, and `docs/GO-LIVE.md` step 0 exists for exactly this reason).
- **They would lose** the five `exploit-fix` checks, so the ranking, version, visited-section and
  guild-war crash vectors are reachable again - these crash the *Arbiter*, i.e. everyone.
  TeraSharp's own hardening (T48/T50 bounds and pagination checks) still applies, but it was
  written against a different threat list.
- **They would lose `spoof-guard`**, but a client-sent `S_*` opcode is harmless to TeraSharp:
  `PacketDispatcher` registers handlers for `C_*` names only, and an unregistered opcode is
  forwarded to World, which logs "handler has not been implemented yet!!!" and drops it.
- **`C_ADMIN` is still gated.** This is the correction. The real 100.02 Arbiter does not check
  privilege on `C_ADMIN` - that is why the proxy exists - but **TeraSharp does**:
  `GmCommands.Handle` calls `Classify(hasUser, level, line)`, which returns `NotAuthorised` and
  sends nothing whenever `level < GmAccounts.MinimumAdminLevel` (1). The level comes from
  `TERASHARP_GM_ACCOUNTS` (numeric accountDBIDs) or the `accounts.admin_level` column, never from
  the packet. So a player who bypasses the proxy on TeraSharp gets no GM commands.

  The sentence in `CLAUDE.md` section 0 - "the Arbiter does not check GM privilege on `C_ADMIN`;
  the proxy on 7801 is the only gate" - is true of the binary this project replaces and **false
  of TeraSharp today**. Keep 7701 on loopback anyway, for the `exploit-fix` reason above and
  because there is no reason to offer a second front door.

---

## 10. What our fork changed

Measured from mtimes - the stock tree is dated 2023-11-11, and exactly twelve files are newer:

| File | Date | What |
|---|---|---|
| `mods/command/index.js` | 2026-09-11 | **the security fix**: `S_LOGIN_ARBITER` hook pinned to `incoming: true`, `C_ADMIN` pinned to `incoming: false`, and `access` reset on every `C_LOGIN_ARBITER`. Upstream had none of that, and a forged `S_LOGIN_ARBITER` unlocked GM commands. |
| `mods/spoof-guard/*` | 2026-09-11 | new - the direction guard |
| `mods/exploit-fix/*` | 2026-09-11 | new - the five crash-vector checks, plus `build-area-allowlist.js` |
| `mods/ranking-crash-probe/*` | 2026-09-11 | new - disabled test harness |
| `mods/packet-logger/*` | 2026-09-12 | new - the capture tool the TeraSharp rewrite is built on |
| `data/definitions/S_SHOW_ITEM_TOOLTIP.19.def`, `definitions.zip`, `definitions/README.md` | 2026-07/08 | def overlay |

`bin/`, `node_modules/` and the other three bundled mods are stock.

---

## 11. Every mod under `D:\mods`

40 entries. **Class** is the policy in section 12. **Obf** is measured, not guessed: a file is
called obfuscated when it is machine-mangled - `_0x`-style identifiers, `\xNN` string arrays,
whole files on one line. Everything else is ordinary readable source.

| Mod | What it does | Main packets hooked | Obf | Class | Conflicts / notes |
|---|---|---|---|---|---|
| AFKer | blocks the client's own AFK return-to-lobby after 1 h idle | `C_PLAYER_LOCATION`, `C_RETURN_TO_LOBBY` (dropped) | no | **block** | defeats a server rule; parks characters in world indefinitely |
| anti-bodyblock | forges party-info so allies stop blocking movement | `S_PARTY_INFO`, `S_PARTY_MEMBER_LIST` | no | player-choice | cosmetic/collision only |
| anywhere | opens banker/broker/merchants from anywhere | `C_NPC_CONTACT`, `C_REQUEST_CONTRACT`, `S_NPC_MENU_SELECT`, `S_SPAWN_NPC` | no | **block** | removes a location constraint the server enforces |
| Auto-Bank | auto-deposits items matching bank contents | `C_PUT_WARE_ITEM`, `C_VIEW_WARE`, `C_CANCEL_CONTRACT`, `S_VIEW_WARE_EX`, `S_REQUEST_CONTRACT`, `S_SYSTEM_MESSAGE` | no | player-choice | declares conflict `auto-banker`; **both are present** |
| auto-banker | the same job, smaller | `C_PUT_WARE_ITEM`, `C_VIEW_WARE`, `S_VIEW_WARE_EX`, `S_REQUEST_CONTRACT` | no | player-choice | declares conflict `auto-bank`; pick one |
| auto-camera | unlocks max view distance | `S_DUNGEON_CAMERA_SET` | no | player-choice | client-side view only |
| auto-cutscene | skips cutscenes | `S_PLAY_MOVIE` (dropped), sends `C_END_MOVIE` | no | player-choice | self-disables at patch >= 105; interacts with T104's watched-movies work |
| auto-loot | auto-picks up drops | `C_TRY_LOOT_DROPITEM`, `S_SPAWN_DROPITEM`, `S_DESPAWN_DROPITEM`, `C_PLAYER_LOCATION` | no | **block** | automates a timed, ranged action the server rate-limits |
| Auto-Guildquest | auto-completes vanguard/guild/field-event turn-ins | 17, incl. `C_COMPLETE_DAILY_EVENT`, `C_REQUEST_START_GUILD_QUEST`, `C_REQUEST_FINISH_GUILD_QUEST`, `C_REQUEST_RECV_DAILY_TOKEN` | no | **block** | farms rewards without playing |
| Auto-negotiate-master-main | auto-accepts/declines broker negotiations | 13 broker/contract packets | no | **block** | trades on the player's behalf while away |
| auto-pet-master | auto-summons and feeds the pet | `C_REQUEST_SPAWN_SERVANT`, `C_START_SERVANT_ACTIVE_SKILL`, `C_USE_ITEM`, + 8 | no | player-choice | convenience; consumes the player's own items |
| battle-notify-update | on-screen text alerts on configurable events | `S_LOGIN`, `S_RETURN_TO_LOBBY`, `C_CHAT`, `S_PRIVATE_CHAT` (+ per-class config) | no | player-choice | notification only |
| boo3 (`name: "idk"`) | "Fast Script" - skill-cast acceleration | `C_START_SKILL`, `C_PRESS_SKILL`, `C_START_TARGETED_SKILL`, `C_CANCEL_SKILL`, `S_ACTION_STAGE`, `S_ACTION_END`, + 3 | no | **block** | animation-cancel / cast-rate manipulation |
| broocheffect.js | loose script, brooch effect tweak | - | no | player-choice | single file, 848 B; cosmetic |
| bugfix | fixes four client bugs (chat sanitizer, gear-upgrade crash, private-channel, swim) | `S_CHAT`, `S_WHISPER`, `S_PRIVATE_CHAT`, `S_JOIN_PRIVATE_CHANNEL`, `S_RESULT_EQUIPMENT_INHERITANCE`, `C_PLAYER_LOCATION`, + 3 | no | **server-side candidate** | conflicts `swim-fix`, `chat-sanitizer`; the crash fixes are worth running for everyone |
| cmd-channel | switches channel and skips the loading screen | `C_SELECT_CHANNEL`, `S_PREPARE_SELECT_CHANNEL`, `S_CANCEL_SELECT_CHANNEL`, `S_LOAD_TOPO`, `S_SPAWN_ME`, `S_INSTANT_MOVE`, + 2 | no | **block** | forging past the load screen desyncs the zone handshake TeraSharp answers |
| command | the chat-command framework (same mod as the bundled one) | see section 4 | no | server-side-for-all | already bundled; do not double-install |
| dynamic-day-master | day/night cycle based on local time | `S_AERO`, `S_SPAWN_NPC`, `S_DESPAWN_NPC`, `S_LOGIN`, `S_SPAWN_ME`, `S_RETURN_TO_LOBBY` | no | player-choice | visual only |
| endless-crafting | repeats the last craft, auto-uses crit items | `C_START_PRODUCE`, `C_USE_ITEM`, `S_END_PRODUCE`, `S_PRODUCE_CRITICAL`, `S_FATIGABILITY_POINT`, + 2 | no | **block** | automated production loop |
| exit-instantly-master | forges `S_EXIT` on `S_PREPARE_EXIT` | `S_PREPARE_EXIT`, sends `S_EXIT` | no | **block** | turns a 10 s graceful exit into an instant drop - combat-logging |
| external-interface | feeds packets to external apps over TCP/HTTP | `C_SELECT_USER`; `require('net')`, own listener | no | player-choice | dependency of `shinra-meter-asura`; opens a local port |
| fps-utils | large FPS pack: hides effects, despawns, UI | **67**, incl. `C_ADMIN`, `C_RESET_ALL_DUNGEON`, `C_SET_VISIBLE_RANGE`, most `S_*` spawn/abnormality packets | no | player-choice, **with a caveat** | it hooks `C_ADMIN` to intercept `;`-separated GM commands locally, and sends `C_RESET_ALL_DUNGEON`. On this server both are gated server-side, so it degrades harmlessly - but check it after any change to the gate |
| generic-box-opener-item-user | mass-opens boxes | `C_GACHA_TRY`, `C_GACHA_CANCEL`, `C_USE_ITEM`, `S_GACHA_START`, `S_GACHA_END`, + 5 | no | **block** | automated item consumption loop |
| GS Sucks | drops one priest skill's animation (310200) | `S_ACTION_STAGE`, `S_ACTION_END` | no | player-choice | 456 B; cosmetic, single skill |
| instant-everything | removes enchant/upgrade/soulbind/dismantle/merge animations | `C_REQUEST_ENCHANT`, `C_REQUEST_EVOLUTION`, `C_BIND_ITEM_EXECUTE`, `C_MERGE_ITEM_EXECUTE`, `C_RQ_COMMIT_DECOMPOSITION_CONTRACT`, `S_CANCEL_CONTRACT` | no | player-choice | conflicts five older `instant-*` mods; **five of its six packets are in the padded set** - it re-serialises them, which is fine here only because integrity is off |
| JustSpamF | rewrites dialog buttons so F progresses everything | `S_DIALOG` (rewritten) | no | player-choice | UI convenience |
| library | shared entity/player/packet helper used by other mods | `C_LOGIN_ARBITER` raw (load trigger only) | no | dependency | required by macro-maker, ping, ping-remover-asura, tera-guide-core |
| macro-maker | AHK-driven skill macros | `C_PRESS_SKILL`, `C_CANCEL_SKILL`, `S_ACTION_STAGE`, `S_ACTION_END`, `S_START_COOLTIME_SKILL`, + 4 | no | **block** | input automation tied to server state |
| no-more-wasted-backstabs-master | blocks targeted teleports with no target | `C_START_TARGETED_SKILL`, `S_ACTION_STAGE`, `S_NPC_LOCATION`, + 5 | no | player-choice | prevents a wasted cast; does not create one |
| ping | measures real latency via a bundled `ConnectionStats.exe` | `C_REQUEST_GAMESTAT_PING`, `S_RESPONSE_GAMESTAT_PONG`, `S_ACTION_STAGE`, `S_ACTION_END`, + 3 | no | player-choice | spawns a child process; **conflicts with the bundled `fake-ping`**, which drops both ping packets |
| ping-remover-asura | skill-prediction: pre-plays actions before the server confirms | *(unreadable)* | **YES** - one 16 kB line, 433 `_0x` identifiers, 1894 `\xNN` escapes | **block** | obfuscated; conflicts `skill-prediction`, `ngsp*`, other ping-removers; depends on `library` + `ping`. Cannot be reviewed, and the category is latency-compensation - say so rather than guess at the detail |
| shinra-meter-asura | launches the ShinraMeter DPS meter | none directly; `keywords: ["client"]`, spawns `ShinraMeter.exe` | no | player-choice | depends on `external-interface`; reads combat packets through that |
| tera-game-state | state tracker (same mod as the bundled one) | see section 4 | no | server-side-for-all | already bundled |
| tera-guide | dungeon mechanic guide with TTS and on-screen zones | via `tera-guide-core` | no | player-choice | 96 guide files; depends on `tera-guide-core` |
| tera-guide-core | the guide engine | via `library` | no | dependency | depends on `library` |
| tera-settings-saver | saves/restores client settings per character | `C_SAVE_CLIENT_ACCOUNT_SETTING`, `C_SAVE_CLIENT_USER_SETTING`, `S_LOAD_CLIENT_*_SETTING`, `S_LOAD_TOPO`, + 2 | no | player-choice | **its `data/` folder ships 25 real characters' saved settings from this server** (`Anel-2800.json` and friends) - clean that before distributing anything |
| tera-swear-words | strips the `<FONT>` profanity filter from chat | `S_CHAT.3`, `S_WHISPER.3`, `S_PRIVATE_CHAT.1`, `C_CHAT.1`, `C_WHISPER.1` | no | player-choice | display only; an operator may still not want it |
| translate-chat | Google-Translate incoming/outgoing chat | via `src/index.js` + language detector | no | player-choice | sends chat text to a third party - a privacy call, not a fairness one |
| ui | in-game Awesomium web UI library (Express server) | `S_OPEN_AWESOMIUM_WEB_URL` | no | dependency | runs a local Express server; shares the Alt+A web-view surface T107 worked on |
| uw ("kappa") | large combat pack | **43**, incl. `C_START_SKILL`, `C_PRESS_SKILL`, `C_START_TARGETED_SKILL`, `C_NOTIFY_LOCATION_IN_ACTION`, `S_ACTION_STAGE`, `S_EACH_SKILL_RESULT`, `C_LOGIN_ARBITER` | **YES** - `index.js` 6374 `_0x` identifiers, `2.js` one 9.8 kB line | **block, highest priority** | also fetches `https://raw.githubusercontent.com/Reverb9/addr/main/addr.txt` and `data.txt` at runtime and requires `net`. Obfuscated code that downloads its own configuration is not reviewable at any price. `1_original.js` / `3_original.js` sit beside it unobfuscated - read those if you want to know what it does |
| warrior | class rotation helper ("uwu") | `C_START_SKILL`, `S_ABNORMALITY_BEGIN/END`, `S_ACTION_STAGE`, `S_CANNOT_START_SKILL`, `S_PLAYER_STAT_UPDATE` | no | **block** | rotation automation; depends on `library` at runtime (`mod.require.library`) but does not declare it |
| yarm | relog helper | `C_RETURN_TO_LOBBY`, `C_SELECT_USER`, `S_GET_USER_LIST`, `S_PREPARE_RETURN_TO_LOBBY`, `S_RETURN_TO_LOBBY`, `C_REQUEST_NONDB_ITEM_INFO` | no | player-choice | drives the lobby path T101/T104 rebuilt - test it against a real relog before blessing it |
| shaker-lite.js | loose 173 B script, screen-shake removal | - | no | player-choice | cosmetic |

Seven of these also sit beside the folder as `.7z` archives (`GS Sucks`, `anywhere`,
`battle-notify-update`, `dynamic-day-master`, `tera-settings-saver`, `uw`, `warrior`) - the
originals they were unpacked from. They are not loaded; delete or archive them so a future reader
does not treat them as a second copy to review.

**Two conflicts are live right now**: `Auto-Bank` vs `auto-banker` (each names the other, so the
mod manager silently drops one of them at load), and `ping` vs the bundled `fake-ping` (the
server-side mod drops the ping packets the client-side mod needs). Everything else declares
conflicts against mods that are not present.

---

## 12. Policy

**The rule.** A player may run a client-side mod that changes what they see, hear or have to
click. A player may not run one that changes what the server decides - the outcome of a skill,
a trade, a loot roll, a timer, a location check - or that plays the game while they are not there.

**Why the line is there and not elsewhere.** Section 9: the server cannot see client-side mods.
Enforcement is server-side validation plus observed behaviour, so the policy has to be one a
person can apply from the outside. "Did it change the outcome?" is that question. "Did it change
the pixels?" is not worth policing and cannot be policed anyway.

**Block list** (14): `uw`, `ping-remover-asura`, `boo3`, `warrior`, `macro-maker`, `auto-loot`,
`Auto-Guildquest`, `Auto-negotiate-master-main`, `endless-crafting`,
`generic-box-opener-item-user`, `anywhere`, `AFKer`, `exit-instantly-master`, `cmd-channel`.

Three reasons, in order of how much they matter:

1. **Unreviewable.** `uw` and `ping-remover-asura` are machine-obfuscated, and `uw` downloads its
   own data at runtime from a third-party repository. Nobody can say what they do; that alone is
   the decision.
2. **Outcome-changing.** `boo3`, `warrior`, `macro-maker` and `ping-remover-asura` manipulate
   skill timing; `anywhere` removes a location check; `cmd-channel` forges past the channel
   handshake; `exit-instantly-master` is a combat-log button.
3. **Playing for you.** `auto-loot`, `Auto-Guildquest`, `Auto-negotiate-master-main`,
   `endless-crafting`, `generic-box-opener-item-user`, `AFKer`.

**Server-side, for everyone** (in `tera-server-proxy\mods`): `command`, `tera-game-state`,
`exploit-fix`, `spoof-guard`. Candidate: `bugfix`'s three crash fixes - they patch client crashes
for everybody and change no outcome, but they have not been run here. `packet-logger` is
development-only; `ranking-crash-probe` must be deleted before going live.

**Everything else is the player's business**, with two honest caveats to state publicly rather
than enforce: `translate-chat` sends chat to Google, and `tera-settings-saver` stores settings
per character (and the copy in `D:\mods` already contains 25 real players' files).

---

## 13. Production checklist

1. `config.json`: `listenPort` 7801, `serverIp` 127.0.0.1, `serverPort` 7701,
   `protocolVersion` 376012, `patchVersion` "100.02" - a wrong version fails **open**, not shut.
2. On start, confirm the log line `protocol-loaded ... 376012` and **no** `warning-unmapped-protocol`.
3. `tera-server-proxy\mods` contains exactly `command`, `tera-game-state`, `exploit-fix`,
   `spoof-guard` - and nothing else.
4. **Delete `ranking-crash-probe`** and disable `packet-logger` (it logs every player's chat to
   disk forever).
5. Run `mods\exploit-fix\build-area-allowlist.js` so `area-allowlist.json` exists - without it
   the visited-section check is sanity-only, and it says so in the log.
6. Log in with a non-GM account, type `/@vsync`, confirm `QA Command Failed` on the proxy console
   and nothing in the game.
7. Log in with a GM account (numeric accountDBID in `TERASHARP_GM_ACCOUNTS`, or
   `accounts.admin_level >= 1`), confirm `QA Login` then `QA Command Success`.
8. From outside the box: 7801 open, **7701 filtered**. `tools\harden-netcup.ps1` does this;
   `docs\GO-LIVE.md` step 7 verifies it with nmap.
9. Watch for `[spoof-guard] DROPPED` and repeated `[exploit-fix]` lines from one account - that is
   an attacker, and the account id is on the line.
10. Remember the proxy hides every player's IP: TeraSharp sees 127.0.0.1 for everyone, so tera-api
    IP bans do nothing and account bans are the only ban you have.
