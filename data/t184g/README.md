# T184g — queue5 versus retail cap_2man, match 1

**Unequal streams.** Both members lack retail's initial `S_SYSTEM_MESSAGE @2173`.
Queue5 advertises party capacity5 where retail advertises2, and its clear-count
roster omits9781. Full findings, field offsets, first three subsequent client
inputs and native/code citations: [MATCHSERVER.md, T184g](../../status/MATCHSERVER.md#t184g--queue5-against-the-retail-two-player-formation).

| Applicant | Retail client / request / SYS | Queue5 client / request / SYS | Received frames audited |
|---|---|---|---|
| First | client2 /818 /915 | client2 /1006 /1147 | 242 retail;210 queue5 |
| Second | client1 /1229 /1286 | client1 /1408 /1418 | 235 retail;130 queue5 |

`source-manifest.json` hashes the six original captures and `arbiter-queue5.log`.
Tap references always mean the original starting TCP record plus byte offset,
never the reassembled frame ordinal. Parsing validates frame lengths and complete
TCP tails independently for each direction/link. Client record numbers are unchanged.

`first-applicant.tsv` and `second-applicant.tsv` include every received frame once,
every changed byte range, and all unpaired frames. Same-opcode sequence alignment
is an indexing aid: it does not prove that two gameplay packets have the same cause.
No raw identity, timer or padding bytes are masked. Decoded core fields are in
`first-decoded.json` and `second-fields.json`; differing World payload fields that
were not independently decoded remain explicit byte differences.

`client-frames.json` stores every selected received frame, each initial application,
and the first boundary frame beyond the comparison. `client-inputs-after-sys.json`
lists the first three subsequent C packets with complete-frame hashes. Full source
frames remain available in the ignored local cache. `world-exchange.tsv` contains
571 complete internal frames: both admissions plus the formation window for each
run. `world-field-diff.json` compares all139E fields/ranges and the four15CD clears.
World-to-client reference columns list exact-byte candidates; identical repetitions
can have multiple candidates, so these columns do not invent unique delivery times.

**Timing limit:** client logs have no timestamps and Arbiter's direct SYS has no
tap timestamp. The reproducible endpoint is AS139E tap time+2s, not an asserted
client SYS-receipt+2s. `windows.json` and `second-window.json` retain the anchors
and adjacent boundary records. Retail anchor:1921+0,2026-09-25T23:36:41.981Z;
queue5 anchor:2558+0,2026-09-26T02:36:37.547Z. The earliest omitted notification,
missing dungeon ID and capacity differences all occur before SYS, unaffected by
this post-SYS timing limit.

Regenerate everything from the original local captures:

```powershell
python tools/t184g-evidence.py
```

The entry point uses the existing `t190-evidence.py` TCP reader, writes the private
cache under ignored `obj/t184g`, then runs the three comparison scripts. Matching
field decoding reuses the `decode_matching` function in `t184f-evidence.py` without
executing its exporter. No server code, sheets or deployment were changed.
