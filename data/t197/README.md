# T197 crest evidence

`frames.json` contains complete frames, selected by original raw tap record (plus offset0) or
reframed client ordinal. `sources.json` records the raw input SHA256 hashes. Regenerate with
`tools/t197-evidence.py`; existing ignored frame caches are optional acceleration.

- `cap_final2b`3379/3380: points60/0;3383/3384:31 newly learned crests;4746/4747:19 applied.
- `cap_2man_b`177: AS_ENTER_WORLD, user1, zero extra crest points at full+0xA7;
  179/180: complete retail load,42 learned,19 applied,60 base points at blob+0x3AEC.
- `cap_instance1`1248–1250:19 applied;22699–22701:client explicitly requests an empty active set.
- `cap_instance1_client2`380/12494:World's correct42/60/60/19 state;9055/13039:Arbiter's injected
  all-inactive state immediately after topo-fin. The second point counter is USED points, not extra points.

The one-crest1469 acknowledgement in the test is explicitly decompile-derived; these selected
captures contain no retail1469 pair. The test's initial blob removes the31 requested learned
ids and base points from the retail blob, then verifies the complete retail load after learning,
applying, the captured60/0 point write and reopening. Positive extras7/11 (with a smaller write3)
are explicitly synthetic, pinned to Arb031:1188–1213's increase-only rule and the native entry field,
not claimed as captured values. Unknown legacy SQL zeroes preserve their original blob points.
