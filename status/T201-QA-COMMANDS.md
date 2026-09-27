# T201 — Arbiter QA command audit and implementation

Worktree: `TeraSharp-cowork`, branch `cowork/T8`. Native reference: the TERA 100.02 Arbiter executable and `Arb_part_*.c` beside this repository.

## Scope and evidence

The native registration function `FUN_1409c9320`, `Arb_part_085.c:16122–16539`, contains **319 registrations and 318 distinct names**. `sticktogether` has separate operator and QA registrations. The old catalogue was not a complete command inventory. The extraction, executable/decompile hashes, parsers and original native help are in `data/t201/native-commands.json` and `inventory-summary.json`.

The completed classification is **179 implemented, 46 denied, 93 not applicable, zero pending**. Not applicable means that the named native subsystem is absent; those commands return an explanation rather than pretending to change state. The seven requested World-owned commands are documented separately and retain operator-only forwarding.

The command-by-command checklist is [GM-COMMANDS-ARBITER.md](GM-COMMANDS-ARBITER.md). It records arguments, actual native behavior, frames, citations, implementation status and missing dependencies. `data/t201/command-status.json` is its machine-readable counterpart. A pending row remains unfinished; it is never automatically classified as not applicable.

`data/t201/captured-commands.json` indexes complete client command bytes, including the additional raw `cap_final2_clients` files. A captured request proves that the command was used; it does **not** prove an output byte fixture. Tests explicitly distinguish a captured reply from a layout derived from native code. `additional-capture-manifest.json` records source hashes.

Examples of actual output pins include both retail QA parties, the `add_guild_money 10000000` reply at `cap_final2_clients/capture_2026-09-22T08-58-07-796Z.log` records 2118→2119, and the card refresh on the current World link from `cap_2man_b`. Native party roster fixtures normalize only unused fixed-name tails and native alignment garbage; semantic fields remain checked.

The World-owned `match_battle_field` command also exposed a missing Arbiter reply: `BSA_CREATE_BATTLE_FIELD` (`1517`) must receive `ABS_CREATE_BATTLE_FIELD` (`1518`) on the requesting link, preserving the raw party handles and setting the GM flag to zero. Four complete retail pairs are pinned: `cap_bg1` 7237→7238, 7270→7271, 7344→7345 and 7396→7397. The preceding `2829` command forwards, full frames, raw record locations and source hashes are in `data/t201/battlefield-create-pairs.json`. These prove the creation handshake, not a successful popup or completed match. The separate Arbiter-owned `battlefield` command retains its T199 creation path and GM flag one.

## Changes

- Compiled native command ownership prevents a missing Markdown file from forwarding an Arbiter command to World. All command routes retain the operator gate. Explicit BOT, shutdown/crash, political exploit and destructive realm-wide reset denials take precedence over routing.
- Native guild/card/account/VIP, friends/chat restrictions, parcel/broker, dungeon/ranking, event schedules, UI, inventory and diagnostic commands are grouped into small `Qa*` helpers. Persistent changes feed their existing load/query/packet paths; native-only runtime flags are identified as such.
- Account-specific and character-specific World messages use the current World. Genuine native global broadcasts and explicit World 0 messages retain that routing.
- `party N` is the requested T200 extension: ephemeral party dummies, with the retail named-member syntax also supported. It does not create persistent accounts or characters. APM and other World-owned commands still use the native command forward.
- Native event timers, expiry and World reconnect replay are integrated through the proposed human-owned patch. The detailed party/dungeon/event findings are in [T201-DUNGEON-QA.md](T201-DUNGEON-QA.md).
- NPC-shop overrides, purchase-limit resets and temporary awakening/style-shop products have persistent state and native World update/reconnect consumers. They do not substitute for the unavailable F2P billing shop.
- Review corrected guild-emblem assignment to the native `EA4E` reward notification and synchronized both existing guild-incentive cooldown columns. The warehouse QA cap now has a transaction consumer; it is no longer an unused setting.

## Deliberate compatibility details

The table names unavailable native subsystems rather than treating them as no-ops. These include political election/Guard tax state, TBA heroes/runes/battlepass, the general Board engine, the InputRestriction rule interpreter, native admission queues, publisher billing, F2P shop catalog/purchases and limited-gacha stock. Existing XML files are not described as missing when the missing part is their runtime consumer.

Some native commands really have no observable effect in this build. `add_event_system` reaches an empty native callee. `masterpiece` writes a flag with no readers in the executable. `ware_duration` changes legacy commission durations while both native commission-paid checks unconditionally return true. These facts do not justify manufacturing new gameplay effects.

Existing project behavior is identified explicitly: `set_admin_level` remains account-scoped with a confirmation, whereas native updates one character silently. `create_user` keeps TeraSharp's starter-template creation rather than claiming the native QA appearance/stat defaults. `query_point` uses the native request-failure message because no billing transport is available. The requested World-owned commands have a separate seven-command table with their native parsers, effects and capture status.

`clear_board` clears writer quota counters, not posts. `reset_charsock` requires fewer than four existing characters, clears the purchased expansion count, and restores `AccountTrait.xml` default package 0's `expandCharacterSlot` value; task enum 4 is not a four-slot capacity.

Native non-PK section reconnect packet `14E1` copies raw C++ strings. World's reader dereferences remote heap pointers for names longer than seven UTF-16 characters. Its empty form clears nothing. The proposed reconnect replay uses native `14E0` setters, which invoke the same World add operation with serialized strings. This is a documented wire difference, not a byte-exact `14E1` claim. Evidence: `Arb_part_047.c:2508`; `WorldServer.exe.c:2997143`, `37380`, `1764862`.

The native festival lifecycle is separate from its LevelEvent reward-mail account ledger. The latter remains unavailable where noted. Native profiler broadcasts are implemented; C++ allocator/function instrumentation does not exist in this runtime. QA `sticktogether` uses the last persisted World location, so a live test must verify location freshness during movement.

## Human-owned integration and validation

Human-owned production files are not edited in this worktree. Apply [T201-PATCH.diff](T201-PATCH.diff) before the final build. Validation runs in `obj/t201/validation`, a disposable source copy with that exact patch applied.

Final validation: **build passed; 1118 passed, 0 failed, 26 skipped**, including **all 63 T201 tests**. The build has four existing nullable warnings in the test runner. Logs: `obj/t201/build-final-green.log` and `obj/t201/tests-final-green.log`.

The final checks corrected a missing `GuildRanking` XML row being reported as loaded, and moved the now-persistent `2983` purchase-limit write out of the generic acknowledgement count. Restart regressions also verify immediate item-period creation/deletion and rookie-event expiry after replay. All 213 unpatched C# files match the validated snapshot; the remaining four files differ only by the proposed human-owned patch. `git diff --check` passed. Master was clean at `541a74a`; the worktree remains on `cowork/T8` at `94d218c` with these uncommitted changes.

Recommendation: apply the patch, run the build/tests on the actual worktree, then commit and merge. Live behavior has not been tested by this task.

No live server deployment, Git commit, merge or shipment has been performed by this task.
