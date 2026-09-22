# Contributing to TeraSharp

Read `README.md` "What you have to supply" first. Everything below assumes a working 100.02
server of your own.

## 1. Branches, worktrees and Cowork

`master` belongs to the maintainer. Every task is one branch in its own git worktree, and every
change reaches `master` through a `--no-ff` merge after a green build.

```powershell
# maintainer, once per task
git worktree add ..\TeraSharp-cowork -b cowork/Tn master
# ... the task writes into ..\TeraSharp-cowork only ...
cd ..\TeraSharp-cowork; git add -A; git commit -m "Tn: ..."
dotnet build TeraSharp.sln; dotnet run --project src\TeraSharp.Arbiter.Tests
cd ..\TeraSharp; git merge --no-ff cowork/Tn; git worktree remove ..\TeraSharp-cowork; git branch -d cowork/Tn
```

Rules for an AI session (Cowork) or anyone working the same way:

- **One session per worktree.** Two sessions at once means two worktrees (`TeraSharp-cowork`,
  `TeraSharp-cowork2`). Never write into the `master` working tree or outside the repo.
- **The session cannot run git.** It writes files; the maintainer commits, builds, tests, merges.
  Commit in the worktree *before* merging and check the branch moved.
- **Never copy a file from `master` into a worktree by hand.** If the worktree is behind, stop and
  ask for a rebase.
- **Re-read the current file before every write.** Never re-apply an edit from a stale copy; a
  rejected write means re-read and redo, never force.
- **Live-path files are describe-only:** `World/WorldBridge.cs`, `Network/*`,
  `Handlers/WorldEntry.cs`, `Handlers/HandlerRegistry.cs`, `Handlers/LoginHandlers.cs`,
  `Program.cs`, `World/TunnelFrames.cs`. Deliver the change as `status/Tn-PATCH.diff`; the
  maintainer applies it (`git apply`) and verifies it on the live server.
- Conflicts are resolved by the maintainer at merge time.

A pull request from outside follows the same shape: one task, tests green, audit clean (section 7).

## 2. The budget rules (`CLAUDE.md` section 7)

They apply to every task, human or AI. Do the work, not the theatre.

| # | Rule |
|---|---|
| 1 | Read only what the task needs. Find the handler by name; never sweep a whole decompile or test file. |
| 2 | Verify once, cheaply: one check per layout or frame. Do not re-derive goldens or simulate the build. |
| 3 | Reports: 15 lines max. Files touched, test names, lines for the maintainer to apply, at most three findings. |
| 4 | Docs: extend the existing design note with a short section. Tables over prose. Quote no more than 5 lines of a decompile. |
| 5 | Tests: one byte-exact test per frame or layout, one round-trip per store feature, one refusal path. |
| 6 | Scope: implement what was asked. Note anything else in one line and stop. |
| 7 | Ask before expanding past ~2x the obvious work (a second capture, a second subsystem). |
| 8 | Never rewrite a `STATUS.md` / `CLAUDE.md` section wholesale; append a row or a line. |

## 3. Capture first

**No handler without bytes or a decompile citation.**

- **Evidence order.** A capture beats the binary; the binary beats the notes. A claim with neither
  behind it is a guess and is labelled as one (`OURS:` in the comment).
- **Bytes.** A handler that answers a real exchange gets a byte-exact test against the captured
  frame, loaded from a TSIS fixture (`LoadTsisOrSkip("cap_xxx.bin")`) and cited by capture name
  and frame number.
- **Decompile.** A layout no capture holds cites the function it came from (handler, writer, the
  length guard) in its doc comment. Cite; do not paste.
- **Never answer what World did not ask.** A `DBS_*` reply carries an id World looks up; an
  unsolicited one completes the wrong request. Every per-user request is answered, allow-listed or
  one-way, and `status/PERSISTENCE-MAP.md` says which; a test enforces it.
- **Bounds.** Every packet-derived index is checked unsigned; never add a packet length to an
  offset before comparing (`len > buf.Length - off`).
- The recipe for a DB-proxy handler is `CLAUDE.md` section 5.

## 4. Adding a datasheet loader

Server policy lives in `Executable\Datasheet`, never in a constant (`status/DATASHEETS.md`). A
value copied out of a sheet goes through `World/DatasheetLoader.cs`:

1. `ReadX(string dir)` parses the sheet and returns `null` when it is missing or does not parse.
   It never throws.
2. `BuiltInX()` returns the transcribed value, used only as the fallback. Make it a method, not a
   static field: static initialisers run in file order, and a field read too early is null.
3. Declare it:
   `public static readonly SheetValue<T> X = new("Sheet.xml <Element>", "consumer", BuiltInX(), ReadX, r => r.Count);`
4. Add `X` to `DatasheetLoader.All`. That gives it a startup line and a `--check-config` line.
5. Consumers read `X.Value`, never the built-in.
6. Add one row to `status/DATASHEETS.md`.
7. Tests run on built-ins (`UseBuiltIns()`). Add one test that the built-in is what the code
   expects, and one that loads the real sheet (skip when the folder is absent) and checks it
   reproduces the built-in.

## 5. Recording traffic: the tap

Fixtures come from your own server, never from the repo. `docs/SETUP.md` section 14 is the full
procedure. In short:

1. Set `logPath` in `tools/arbiter-world-tap.js` to a folder you own.
2. `DeploymentConfig.xml`: `<ArbiterServer port="7812"/>`. Start the tap, then TeraSharp (7802),
   then World.
3. Play the flow. Then `tools\reframe-tap.ps1` -> `<log>_frames.txt` -> `tools\make-tsis.ps1`
   -> `data\cap_xxx.bin` (`data/README.md`).
4. The same tap in front of a stock `ArbiterServer.exe` gives the reference to compare against.

Put the port back to 7802 afterwards.

## 6. Tests

```powershell
dotnet run --project src\TeraSharp.Arbiter.Tests
$env:TERASHARP_TEST_FILTER = 'T172_'; dotnet run --project src\TeraSharp.Arbiter.Tests
```

A test is a `public static void` method tagged `[Test]` that throws on failure. When its fixture is
missing, it calls `Skip.Because("data/cap_xxx.bin not found")` and returns: the runner reports
`SKIP`, not `PASS`, and a skip is never a failure. The exit code is the failure count.

## 7. Before you push

```powershell
.\tools\audit-release.ps1 -Strict
```

It must exit 0. No captured bytes, datasheets, `.def` files, decompiled source or real
addresses in the repo: facts derived from them (offsets, field orders, opcodes) are fine, the
material is not. `.gitignore` keeps `data/*.bin` and `data/*.hex` out; do not force-add them.
