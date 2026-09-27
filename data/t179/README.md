# T179 local evidence

Run `tools/t179-audit.py ingest`, then `census`, then the `tools/T179Audit` console
project from the worktree root. Python uses only its standard library. The console
project references the existing test harness and runs its actual `RunHandler`.

All generated files here are ignored: they contain full captured traffic, identifiers,
and opaque character state. Source captures in `D:\packetlogs` are never modified.
`captures.sqlite` retains complete frame bytes and start/end chunk references.
`inventory.json` records hashes, duplicates, malformed records and missing raw logs.
Candidate pair timing is not sufficient to prove causality; see the audit report.

Reproduction order (from the worktree root):

1. Python: `tools/t179-audit.py ingest`, `census`, `inspect-tests`.
2. Build `tools/T179Audit/T179Audit.csproj`; run its DLL with argument `.`.
3. Python: `tools/t179-audit.py compare`, `clients`.
4. Run the audit DLL with `. --focused`, then `. --routes`.
5. Python: `tools/t179-evidence.py ctl`, `pins`, `queue`, `entry`, `entry_fields`,
   `layouts`, `refine`, `client_routes`, `timeline_check`, `citations`.

The default handler run uses the checkout's current code. The report's baseline run was
made before the two T179 servant changes; `focused-results.jsonl` contains their recheck
and the actual registration builder, replacing the baseline's replay-only registration.
The console project loads local datasheets and the configured reference replay file
`D:/packetlogs/arb_world.log`; it uses only in-memory character databases.

Offsets in `world-diffs.jsonl` / `reviewed-world-diffs.jsonl` are payload-relative;
offsets in `world-layouts.json` / `entry-diffs.json` are full-frame-relative.
Byte spans are half-open `[start, end)`; full expected and actual bytes are retained.
The original FIFO/name candidates are kept separately from corrected/qualified comparisons.
`world-outgoing.jsonl` indexes every real A->W frame, including frames not assigned to a request.
