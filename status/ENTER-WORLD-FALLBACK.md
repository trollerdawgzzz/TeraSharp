# Enter-world failure and the fallback retry (T21, part A)

> **What this fixes.** A character saved inside an instance cannot log back in. We send
> `AS_ENTER_WORLD` with the instance's continent, WorldServer answers `SA_ENTER_WORLD_FAIL`,
> nothing in TeraSharp handles it, and the client sits on the loading screen forever. The real
> Arbiter waits three seconds and re-sends `AS_ENTER_WORLD` pointed at the character's stored
> return point.

**Ground truth**: `D:\packetlogs\arb_world_2026-09-13T11-33-30-680Z.log`, the "Test" (playerId 2)
login at seq 835-2036. Condensed listing `cap_relog9827_ctl.txt`, full frames
`cap_relog9827_frames.txt`. Decompile: `ArbiterServer.exe.c` (the one-file Ghidra output staged
for this session; the same functions are in `Arb_part_*.c`).

| seq | dir | opcode | len | what |
|-----|-----|--------|-----|------|
| 835 | A->W | 0x138E `AS_ENTER_WORLD` | 189 | continent **9827**, ChannelInstanceId **0x0AF00001**, pos (-12040, -27422, -4435), Ticket 1 |
| 836 | W->A | 0x138D `SA_ENTER_WORLD_FAIL` | 38 | Ticket 1, ContinuousDungeonId 9827, FailReason 2 |
| 841 | A->W | 0x148D `AS_CACHE_DUNGEON_COOL_TIME_TO_WORLD` | 70 | +3.014 s |
| 842 | A->W | 0x148D | 70 | same record, counters cleared |
| 843 | A->W | 0x138E | 189 | continent **5**, ChannelInstanceId **-1**, pos **(16260, 1253, -4410)**, Ticket **2**, ContinuousDungeonId **9827** |

dob's login in the same capture (seq 355) succeeded first time: continent 7005,
ChannelInstanceId **0**, and no 0x138D.

---

## 1. `SA_ENTER_WORLD_FAIL` (0x138D) — 38-byte fixed frame

Field names are the Arbiter's own, from its packet dumper for `"SA_ENTER_WORLD_FAIL"`, which
labels the frame with that string and rejects anything whose length is not above `0x25`.
Offsets below are **payload** (frame - 6), which is what `DbProxyHandlers` works in.

| payload | frame | type | name | seq 836 |
|---------|-------|------|------|---------|
| 0  | 0x06 | u64 | `ArbiterClient` | echoes `AS_ENTER_WORLD` [16] |
| 8  | 0x0e | u64 | `ArbiterUser` | echoes `AS_ENTER_WORLD` [24] — **our gameId** |
| 16 | 0x16 | u32 | `Ticket` | 1 — echoes `AS_ENTER_WORLD` [80], our tunnel key |
| 20 | 0x1a | u32 | `LastIndex` | 0 |
| 24 | 0x1e | u32 | `ContinuousDungeonId` | 9827 — the continent World refused |
| 28 | 0x22 | u32 | `FailReason` | 2 |

`Handler_SA_ENTER_WORLD_FAIL(User*, WorldServerSession*, SafePacket<PKT_SA_ENTER_WORLD_FAIL_READ>)`
reads frame `0x1e` and `0x22`, logs
`[ERROR] Handler_SA_ENTER_WORLD_FAIL (DbId:%d, Name:%s, LoginState:%d, continent:%d reason:%d)`,
and schedules a `User::DoTimerJob` **3000 ms** out:

The job it queues carries `User::EnterWorldFail` as its entry point and the `User*` alongside
it, and copies two fields out of the frame into the job: `ContinuousDungeonId` from frame+0x1E
into job+0x80, and `FailReason` from frame+0x22 into job+0x84. The job is then scheduled 3000 ms
out.

seq 836 is at 11:45:56.587 and seq 841/842/843 at 11:45:59.601-.604 — **3.014 s**, i.e. that timer.
We fire immediately instead; nothing in World depends on the delay.

## 2. `User::EnterWorldFail(EnterWorldFailReason reason, int continuousDungeonId)`

Scope tracer `"void __cdecl User::EnterWorldFail(enum EnterWorldFailReason,int)"`. In order:

1. Logs
   `[%4d] EnterWorldFail: %s(UserDbId:%d) failReason:[%d] LoginState:[%d] ContinentId:[%d] Pos:[%f %f %f]`.
2. **`if (2 < reason - 1U)`** — anything outside 1..3 → `LoginState = 4`, `PlayerState = 2`,
   `LeaveWorldType = 3`, disconnect. **No retry.**
3. `User+0x3c5c = User+0x19c` — remembers the continent that just failed.
4. For reason 1 or 2:
   * `User+0x1a4 = -1`
   * `User+0x19c = FUN_140717110(ContinentManager, failedContinent)` — the *fallback continent* for
     the refused one, from the continent table
   * `User+0x1a0 = -1` — **ChannelInstanceId**
   * `FUN_140717090(ContinentManager, &pos, failedContinent)` → `User+0x18c/0x190/0x194` — the
     fallback position from the same table
   * `User+0x3b74 = 0` (u64), `User+0x3b7c = 0` (u8)
5. **Then the override that actually produced seq 843:**

When `User+0x1a8` holds a stored SysReturnLoc greater than zero, it overrides the five fields
just set: continent from `+0x1a8`, channelInstanceId from `+0x1ac`, and x/y/z widened from the
ints at `+0x1b0`, `+0x1b4` and `+0x1b8` into the floats at `+0x18c`, `+0x190` and `+0x194`. It
then clears the stored return point.

The coordinates are stored as **ints** and widened back with `(float)(int)`, which is why the
capture's fallback is exactly `(16260, 1253, -4410)` and never a fraction.

6. Re-sends `AS_ENTER_WORLD` through the same writer the first attempt used (`FUN_140360710`),
   with `param_3` — the `continuousDungeonId` from step 0 — landing in the packet's
   `ContinuousDungeonId` field.

## 3. Where `User+0x1a8..0x1bb` comes from

`User::UpdateSysReturnLoc(int continentId, int channelInstanceId, int x, int y, int z)`
(`FUN_1403bced0`) writes those five ints **and** persists them through
`{ call dbo.spUpdateSysReturnLoc }`, then pushes `DBS_SYSRETURN_POSITION` (0x27C6) to the World
session. `User::CleanSysReturnLoc` is `UpdateSysReturnLoc(0,0,0,0,0)`.

The call that matters is in `Handler_SA_RESPONSE_ENTER_DUNGEON`:

It copies the 176-byte `DungeonEnterContext` out of the frame at +0x0E, and then branches on
two of its bytes. When either the "commit the return point" byte at ctx+42 or the byte at ctx+144
is zero it calls `CleanSysReturnLoc` — unless the type field at ctx+4 is 1. Otherwise it calls
`UpdateSysReturnLoc` with the context's coordinates and the user's current
`channelInstanceId` from `User+0x1a0`.

Ghidra split the context into consecutive stack slots, so `local_XX` is `ctx + (0xf8 - XX)`:

| local | ctx+ | payload | meaning | seq 1137 / 1159 |
|-------|------|---------|---------|-----------------|
| `local_f8` | 0   | 8   | dungeonId | 9827 |
| `local_f4` | 4   | 12  | a type field | 0 |
| `local_e0` | 24  | 32  | owner UserDbId | 2 |
| `local_ce` | 42  | 50  | **u8** "commit the return point" | **0** |
| `local_cc` | 44  | 52  | float **return x** | 16260 |
| `local_c8` | 48  | 56  | float **return y** | 1253 |
| `local_c4` | 52  | 60  | float **return z** | -4410 |
| `local_c0` | 56  | 64  | u32 **return continent** | 5 |
| —          | 140 | 148 | u32 **ChannelInstanceId** World allocated | 0 (req) / **0x0AF00001** (rsp) |
| `local_68` | 144 | 152 | **u8** success | 0 (req) / **1** (rsp) |
| `local_60` | 152 | 160 | -> `User+0x4024` | 0 |

Two independent confirmations that ctx+42 and ctx+144 are single bytes: the T10 padding list,
which was derived from the *copy* functions, has a gap at payload 51 ("after a u16 + u8 at
40..42") and at payload 153..155 ("after a lone u8 at 144").

`x` is ctx+44, `y` is ctx+48 and `z` is ctx+52: the last two arguments go on the stack in that
address order.

**The channelInstanceId stored as the return one is `User+0x1a0` at dungeon-entry time**, i.e. the
open-world value (-1) — which is exactly what seq 843 carries.

## 4. `AS_CACHE_DUNGEON_COOL_TIME_TO_WORLD` (0x148D)

Opcode name from the Arbiter's `case 0x148d: return "AS_CACHE_DUNGEON_COOL_TIME_TO_WORLD";`.
Writer: `SendToSession<PKT_AS_CACHE_DUNGEON_COOL_TIME_TO_WORLD_WRITE,int,int,const unsigned char*>`.

```
payload [0] u32 blobOffset (frame-relative, always 18)
        [4] u32 blobLength
        [8] u32 playerId
        [12..] the blob
```

The blob is one **52-byte `DungeonCoolTimeElem`** — byte for byte the same record
`DBS_LOAD_DUNGEON_COOL_TIME` (0x2868) carries in its first list; capture seq 885's list record
and seq 842's blob are identical.

```
+0   u32  dungeonId              9827
+4   u32  channelInstanceId      0x0AF00001
+8   16 B DateTime               1970-01-01 00:00   ("never")
+24  16 B DateTime               2026-09-12 07:00
+40  u32  counter                1 (seq 841) / 0 (seq 842)
+44  u32  counter                1 (seq 841) / 0 (seq 842)
+48  u32                         0
```

The `PKT_DBS_LOAD_DUNGEON_COOL_TIME_WRITE<vector<DungeonCoolTimeElem>,
vector<DungeonClearCountElem>, vector<int>>` template says why there are two pushes: the
cool-time list and the clear-count list, same record shape.

The 16-byte DateTime decodes as `u16 year, month, day, hour, minute, second` + 4 pad on the two
values seen (`B2 07 01 00 01 00 ...` = 1970-01-01 00:00, `EA 07 09 00 0C 00 07 00 ...` =
2026-09-12 07:00). **That is inferred from two data points, not from a decompiled writer**, so
the code treats the DateTimes as opaque 16-byte blobs and only names the "never" constant.
The same 2026-09-12 07:00 stamp appears in the `0x272D` list-5 trailer in both quest replies of
this capture, so it is a server-wide daily-reset time, not per character.

## 5. `AS_ENTER_WORLD` (0x138E) — the full field map

From the Arbiter's packet dumper for `"AS_ENTER_WORLD"` (`FUN_140183050`, min length `0xac <
param_2`). Payload offsets:

| off | type | name | seq 835 |
|-----|------|------|---------|
| 0/4/8/12 | u32 x4 | header: two unused list slots, then EtcData offset (173) and length (16) | |
| 16 | u64 | `ArbiterClient` | |
| 24 | u64 | `ArbiterUser` | |
| 32 | u32 | `UserDbId` | 2 |
| 36 | u64 | `AccountDbId` | 1 |
| 44 | u32 | **`SessionKey`** | 299748 |
| 48 | u32 | `ContinentId` | 9827 |
| 52 | u32 | `ChannelInstanceId` | 0x0AF00001 |
| 56..67 | f32 x3 | position (unnamed in the dumper) | |
| 68 | u32 | **`EnterWorldType`** | 1 |
| 72 | u32 | **`Direction`** | -18852 |
| 76 | u32 | `VisibleRange` | 2000 |
| 80 | u32 | `Ticket` | 1 |
| 84 | u64 | `GameId` | |
| 92 | u8 | `PcBangUser` | 0 |
| 93 | u8 | `NewMemberAccount` | 0 |
| 94 | u64 | `PartyId` | 0 |
| 102 | u8 | `IsSysParty` | 0 |
| 103 | u32 | `SubscriptionFeeType` | 6 |
| 107 | u32 | `AccountRestrictionLevel` | 0 |
| 111 | u32 | `AdminLevel` | 0 |
| 115 | u8 | `TutorialUser` | 0 |
| 116 | u8 | `LeaveParty` | 0 |
| 117 | u32 | `BotPenalty` | 0 |
| 121 | u64 | `SharedDBTCat` | 0 |
| 129 | u32 | `CharacterSocketNum` | 3 |
| 133 | u32 | `MaxSharedIncCharSocketCount` | 20 |
| 137 | u32 | `MaxSharedIncWarePageCount` | 7 |
| 141 | u32 | `MaxSharedIncStyleWarePageCount` | 3 |
| 145 | u32 | `SharedIncCharSocketCount` | 0 |
| 149 | u32 | `SharedIncWarePageCount` | 0 |
| 153 | u32 | `SharedIncStyleWarePageCount` | 0 |
| **157** | u32 | **`ContinuousDungeonId`** | 0 -> **9827** on the retry |
| 161 | u32 | `ExCrestPoint` | 0 |
| 165 | u8 | `UseOptionalItem` | 0 |
| 166 | u8 | `MentoringReturnUser` | 0 |
| 167..182 | 16 B | `EtcData` | zeros |

Three of these correct comments in `Handlers/WorldEntry.cs`:

* **[44] is `SessionKey`**, not "world session state". Source is `ClientSession+0x3108`, i.e. per
  *client connection*, not per character: dob (playerId 1) and Test (playerId 2) log in on the
  same client in this capture and both carry **299748**; `lobby_tap.log` (a different client
  connection) carries 194942. Whatever we hand the client at login is what belongs here.
* **[68] is `EnterWorldType`**, not the character level. `WorldEntry` currently sends
  `chr.Level`; the real Arbiter sends the enum (`User::EnterWorldStart(TPointer<WorldServerSession>,
  enum EnterWorldType, __int64)`), which is **1** in all three 0x138E frames of this capture. A
  level-1 character hides the bug; a level-20 one would send `EnterWorldType = 20`.
* **[72] is `Direction`**, not "maxHP base" — `WorldEntry` already sources it from world blob
  offset 304, which is right; only the name is wrong.

## 5a. `[111] AdminLevel` — where World gets the number it logs (T46)

WorldServer prints `SpawnComplete [...] AdminLevel[0]` for an account whose Arbiter-side level is
5. The field in the table above is the one it means, and the chain is complete on both sides.

**Arbiter → wire.** `User+0x3b98` is the Arbiter's admin level: it is what
`ArbiterQACommandHandler::SetAdminLevel` writes (`Arb_part_030.c:9743`), what every `/@` gate tests
with `level < 1`, and what `Arb_part_029.c:3915` compares against 5. The `AS_ENTER_WORLD` caller
reads it into a local (`Arb_part_028.c:15470` and `:15522`) and hands it to the writer
`FUN_140360710` (`Arb_part_027.c:12906`) as **param_22**, the 22nd value it serialises — which
lands at frame `0x75`, payload **111**. The Arbiter's own dumper agrees: `AdminLevel` at frame
`0x75` (`Arb_part_011.c:10446`).

**Wire → World.** `FUN_140496c80` (`WorldServer.exe.c:847717`) is World's generated field-binding
table; at `:847798` it binds the name `adminLevel` to offset **`0xA474`** on its `User`. That
offset is what both log sites print — `SpawnComplete ... AdminLevel[%d]` at `:935500` and
`=== AdminLevel[%d], Status[%d], ...` at `:866788`.

**The part that matters for `/@`:** `0xA474` appears **four times in the whole World binary** — set
to 0 in the constructor (`:803532`), bound in that table, and read by those two log lines. World
never compares it against anything. So the wrong value is a wrong log line and a wrong GM-info
dump; it is **not** what refuses a command. World's `Handler_AS_ADMIN_COMMAND` does no
admin check either (see `status/GM-DESIGN.md` §7). The gates that do exist are the Arbiter's own
`User+0x3b98` (already working — `query_point` answers) and the client's, which keys off
`S_LOGIN_ARBITER.status` (§6 of GM-DESIGN, still unwired).

## 6. `[52]` on a first-time enter: 0, -1, or the PDId

Three observed values, all `User+0x1a0` at login time:

| character | saved zone | [52] |
|-----------|-----------|------|
| dob, seq 355 | 7005 (open world) | **0** |
| Test, seq 835 | 9827 (instance) | **0x0AF00001** — the instance's ChannelInstanceId |
| Test, seq 843 (retry) | 5 (open world) | **0xFFFFFFFF** |

`WorldEntry` currently hardcodes `0xFFFFFFFF`, which is live-verified for the open world and must
not change. What must change is the instance case: when the character's saved zone is the instance
we recorded, send the stored PDId.

## 7. What TeraSharp does now

`Persistence/CharacterStore.cs` — six new columns on `characters`
(`return_zone`, `return_x/y/z`, `dungeon_id`, `instance_pdid`), added idempotently, plus
`SaveDungeonReturn`, `SaveInstancePdId`, `ClearDungeonReturn`, `GetDungeonReturn`.
Coordinates are truncated to int on the way in, exactly as `spUpdateSysReturnLoc` stores them.

`World/DbProxyHandlers.cs`
* `SA_REQUEST_ENTER_DUNGEON` (0x13BE) and `SA_RESPONSE_ENTER_DUNGEON` (0x13C0) now call
  `RecordDungeonEntry`, which stores the return point from the context and, on a successful
  response, the ChannelInstanceId World allocated.
* `SA_ENTER_WORLD_FAIL` (0x138D) is a real handler: parse, log every field, refuse to retry a
  reason outside 1..3, send the two `0x148D` pushes, then call the retry hook.
* Pure builders with byte-exact tests: `ParseEnterWorldFail`, `BuildCacheDungeonCoolTime`,
  `BuildDungeonCoolTimeRecord`, `BuildEnterWorldRetryPayload`.

**Deviation, on purpose.** The real Arbiter only commits the return point when
`DungeonEnterContext+42` is set and *clears* it otherwise. In the only capture we have, that flag
is 0 on both halves of the handshake — yet the DB plainly held `(5, 16260, 1253, -4410)` at login,
so some other `UpdateSysReturnLoc` call site (there are eight: `EnterBattleField`,
`Handler_SA_ADMIN_CALL_DIFFERENT_WORLD_USER`, and five more) had set it in an earlier session.
Clearing here would leave us with no fallback at all, so we store whatever the context carries and
never clear. `ClearDungeonReturn` exists for when that flag's source is found.

**Deviation, on purpose.** We keep no dungeon cool-time state, so both `0x148D` pushes go out as
"never entered" — both DateTimes `1970-01-01`, all three counters 0. That can only ever let a
player back in, never lock one out.

## 8. The two human-owned changes (exact diffs)

### 8a. `Handlers/WorldEntry.cs`

Add the resend entry point and use the stored instance handle. Nothing else in the file moves.

```csharp
    // [52] param_9 (User+0x1a0). -1 for the open world (live-verified). A character saved inside
    // an instance must carry that instance's ChannelInstanceId instead - the real Arbiter sends
    // 0x0AF00001 for exactly this case (arb_world_2026-09-13 seq 835). status/ENTER-WORLD-FALLBACK.md.
-   w.U32(0xFFFFFFFF);
+   var saved = Program.Store?.GetDungeonReturn((int)chr.Id);
+   w.U32(saved != null && saved.DungeonId == chr.Zone && saved.InstancePdId != 0
+           ? (uint)saved.InstancePdId
+           : 0xFFFFFFFF);
```

and, at the end of the class:

```csharp
    /// <summary>
    /// WorldServer refused AS_ENTER_WORLD (SA_ENTER_WORLD_FAIL). Re-send it pointed at the
    /// character's stored return point, exactly as User::EnterWorldFail does.
    /// status/ENTER-WORLD-FALLBACK.md.
    /// </summary>
    public static void ResendEnterWorld(GameSession s, DbProxyHandlers.EnterWorldFailure f, ILogger log)
    {
        var w = Program.World;
        var chr = s.SelectedCharacter;
        if (w == null || chr == null) return;

        var back = Program.Store?.GetDungeonReturn((int)chr.Id);
        if (back == null)
        {
            log.LogError("EnterWorld retry for '{Name}': no stored return point for continent {C}; "
                + "the character is stuck. Give it one in the DB (characters.return_zone/x/y/z).",
                chr.Name, f.ContinuousDungeonId);
            return;
        }

        var record = Program.Store?.GetCharacter((int)chr.Id);
        var first = BuildEnterWorldPayload(s.GameId, chr, record?.WorldBlob, s.TunnelKey);
        var retry = DbProxyHandlers.BuildEnterWorldRetryPayload(
            first, back.Zone, back.X, back.Y, back.Z,
            channelInstanceId: 0xFFFFFFFF,
            ticket: s.TunnelKey,
            continuousDungeonId: f.ContinuousDungeonId);
        if (retry == null) return;

        log.LogWarning("EnterWorld retry for '{Name}': continent {C} refused, falling back to "
            + "zone {Z} ({X:F0}, {Y:F0}, {Zz:F0})", chr.Name, f.ContinuousDungeonId,
            back.Zone, back.X, back.Y, back.Z);
        w.SendFrame(WorldBridge.OpPlayerEnter, retry);
    }
```

`BuildEnterWorldRetryPayload` is the byte-exact transform: capture seq 835 -> seq 843 differ in
continent, channelInstanceId, position, Ticket and ContinuousDungeonId **and in nothing else**
(test `EnterWorld_retry_touches_only_the_five_fallback_fields`).

If `WorldEntry` also fixes `[68]` to a literal `1` (see section 5), do it in the same pass — it
is a one-character change and a real bug above level 1.

### 8b. `World/WorldBridge.cs`

Expose the gameId -> session map the handler needs (three lines):

```csharp
    /// <summary>The session that owns a gameId, or null. Used by the 0x138D handler.</summary>
    public GameSession? PlayerForGameId(ulong gameId)
    {
        lock (_playersLock) return _players.TryGetValue(gameId, out var s) ? s : null;
    }
```

### 8c. `Program.cs`

Wire the two hooks wherever `DbProxyHandlers` is constructed:

```csharp
    dbProxy.PlayerIdForGameId = gameId =>
        (int)(world.PlayerForGameId(gameId)?.SelectedCharacter?.Id ?? 0);
    dbProxy.ResendEnterWorld = f =>
    {
        var s = world.PlayerForGameId(f.ArbiterUser);
        if (s != null) WorldEntry.ResendEnterWorld(s, f, log);
    };
```

Until they are wired the handler still runs: it logs the failure in full and says, at error
level, that no retry will go out.

### 8d. `Handlers/WorldEntry.cs` — AdminLevel (T46)

Three edits. `DbProxyHandlers.EnterWorldAdminLevelOffset` (= 111) already exists, and
`GmCommandHandlers.LevelOf` already resolves the allow-list-or-stored level; both are
Cowork-editable and landed with T46.

```csharp
-    internal static byte[] BuildEnterWorldPayload(ulong gameId, FakeCharacter chr, uint tunnelKey = 5)
-        => BuildEnterWorldPayload(gameId, chr, null, tunnelKey);
+    internal static byte[] BuildEnterWorldPayload(ulong gameId, FakeCharacter chr, uint tunnelKey = 5,
+                                                  int adminLevel = 0)
+        => BuildEnterWorldPayload(gameId, chr, null, tunnelKey, adminLevel);

-    internal static byte[] BuildEnterWorldPayload(ulong gameId, FakeCharacter chr, byte[]? worldBlob, uint tunnelKey = 5)
+    internal static byte[] BuildEnterWorldPayload(ulong gameId, FakeCharacter chr, byte[]? worldBlob,
+                                                  uint tunnelKey = 5, int adminLevel = 0)
```

```csharp
-        // [111..114] From User+0x3b98 (param_22). Capture=0.
-        w.U32(0);
+        // [111..114] AdminLevel (param_22, from User+0x3b98). World binds this to its own
+        //            User+0xA474 and prints it as "AdminLevel[%d]" in the SpawnComplete line.
+        //            status/ENTER-WORLD-FALLBACK.md 5a.
+        w.U32((uint)adminLevel);
```

and both call sites in this file — `EnterWorld` and `ResendEnterWorld` — pass it:

```csharp
-        var enterPayload = BuildEnterWorldPayload(s.GameId, chr, record?.WorldBlob, s.TunnelKey);
+        var enterPayload = BuildEnterWorldPayload(s.GameId, chr, record?.WorldBlob, s.TunnelKey,
+                                                  GmCommandHandlers.LevelOf(s, Program.Store));
```

```csharp
-        var first = BuildEnterWorldPayload(s.GameId, chr, record?.WorldBlob, s.TunnelKey);
+        var first = BuildEnterWorldPayload(s.GameId, chr, record?.WorldBlob, s.TunnelKey,
+                                           GmCommandHandlers.LevelOf(s, Program.Store));
```

`GmCommandHandlers` is in the same namespace, so no `using` changes. The optional parameter keeps
the dozen existing `BuildEnterWorldPayload(GameId, chr)` test call sites compiling.
`BuildEnterWorldRetryPayload` clones and patches only zone/instance/position/ticket/dungeon, so the
level carries into the retry frame by itself — `T46_the_enter_world_retry_carries_admin_level_through`
pins that.

When the diff lands, flip `T46_world_entry_still_sends_admin_level_zero` to assert the account's
level instead; it exists to document the bug, not to defend it.

## 9. Still open

* `DungeonEnterContext+42` — which call path sets it. Until then we never clear the return point.
* The continent fallback table (`FUN_140717110` / `FUN_140717090` on `DAT_141114760`) — the
  per-continent default return the Arbiter uses when there is no `SysReturnLoc`. Ours is
  "log an error and give up"; a table would be better. `Datasheet\ContinentData.xml` is the
  likely source.
* `LastIndex` (0x138D payload 20) is 0 in the only sample.
* Whether World cares about the 3000 ms delay. We answer immediately.
