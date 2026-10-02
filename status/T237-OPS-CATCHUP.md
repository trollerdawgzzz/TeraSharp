T237 - Operations catch-up
==========================

Three parts: the docs caught up to this week, and the two scripts I kept hand-writing.

1. Docs
-------

| File | What went in |
|---|---|
| `docs/OPERATIONS.md` | 2.2 gained the tera-api bullet (inbound hub connection, it redials; restart tera-api, not the Arbiter). New 5.1 "Four more, added since T230": SYNTH_ITEM_RECORDS, PARCEL_DELETE_ON_COLLECT, API_GATEWAY_SERVE/:8800, ServerConfig `<DeleteUser>`. New section 7 "Sheets, the box, and the pre-restart check": 7.1 two copies, 7.2 backup conventions, 7.3 copying to the box, 7.4 the validator, 7.5 check-box/push-sheets. 20,221 -> 25,935 B. |
| `docs/QUICKSTART.md` | Settings section: four-row table of the switches added after the file was written, two of them on by default, pointing at OPERATIONS 5.1. 6,613 -> 7,472 B. |
| `docs/GO-LIVE.md` | 8 gained the tera-api-is-not-in-the-restart bullet. 10 gained the pre-restart validator pair and the check-box/push-sheets handoff, plus the never-hash-sync-over-RDP line. 19,098 -> 20,643 B. |
| `.env.example` | `TERASHARP_SYNTH_ITEM_RECORDS` was missing. Added with its T209 comment. 4,611 -> 4,908 B. |

All four are pure additions - no existing line changed.

Backup conventions, as 7.2 now states them
------------------------------------------

| Suffix | Who writes it | Restores |
|---|---|---|
| `.m1.bak` / `.m5.bak` | the M1 / M5 sheet sets | that milestone's edit |
| `.t2xx.bak` | `patch_class_animationdata.py`, `unlock-*.ps1`, `push-sheets.ps1 -Tag` | one job, byte for byte |
| `.stock` | `copy_classic_ui.ps1` | the file as it shipped |

2. tools/check-box.ps1
----------------------

`check-box.ps1 <local copy of the box's Datasheet> <PC's Datasheet> [-PushList out.txt] [-IncludeBackups]`

Size first, SHA-256 only when sizes match. One table: SAME / DIFFERS / PC-only / BOX-only.
`.bak`, `.orig`, `.stock` excluded unless `-IncludeBackups`. Exit = differing + missing.
`-PushList` writes the names worth pushing, one per line, for push-sheets to read.

Fixture run: EXIT=4 - `StaleThing.xml` BOX-only, `AnimationData.xml` DIFFERS (size),
`DefaultSkillSet.xml` DIFFERS (same size, different hash), `UserSkillData_Glaiver_Popori_M.xml`
PC-only; the `.bak` beside it ignored.

3. tools/push-sheets.ps1
------------------------

`push-sheets.ps1 -From <dir> -To <dir> (-Files a.xml,b.xml | -ListFile drift.txt) -Tag t237 [-Apply]`

Dry run unless `-Apply`. One `.<tag>.bak` per file per tag, taken before the first write, so
re-running a tag never buries the pre-edit copy. `-ListFile` scrapes sheet names out of any
script's printed output, so check-class-rows' or check-box's table can be piped to a file and
fed straight in. A name you TYPED that is not in `-From` is fatal; a name that was SCRAPED is
reported and skipped, because the scrape sees names that were never files.

Round trip: the noisy `drift.txt` skipped `StaleThing.xml` ("not in -From, skipped");
`-ListFile -Tag t236c -Apply` copied 3, wrote `.t236c.bak` for the 2 that existed; re-running
check-box left only the genuine BOX-only row.

Not done
--------

`check-box` needs the box's folder copied to the PC by hand - there is no shell on the box from
here, and 7.3 says why hash-sync over RDP is the wrong tool. The copy step stays manual.
