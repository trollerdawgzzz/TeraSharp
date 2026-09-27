# T210b - reconciling T190's card test with the captures

T190's `T190Cards_perfect_collection_matches_captured_refresh_and_full_load` asserted that the
command **deletes** the arrangement ("native reset clears the old collection arrangement and
rewards"). The captures say it does not. T190 asserted the wrong half; the test is rewritten, not
T210 reverted.

## What each phase actually carries

| Phase | Evidence | cards | presets | combines | rewards | preset amount |
|---|---|---:|---:|---:|---:|---:|
|the refresh the command asks for|tap `cap_2man_b` 15530 -> 15533 -> **15535** (3541 payload bytes), client `cap_2man_b_client2` 2705 (2680 B)|218|0 (World shows 1 empty)|0|0|1|
|the next load, same account|client `cap_2man_b_client2` 3634 (2792 B)|218|**3**|-|**8**|**3**|
|a later session, same account (dobb)|client `cap_bg1_client1` 182 (2792 B), first load of the session|218|**3**|-|**8**|**3**|

The third row is what settles it: `cap_bg1` is a different, later retail session and its **first**
`S_CARD_DATA` for that account already carries 3 presets and 8 claimed rewards. Nothing in that
session could have re-created them before the first load, so the rows were durable across the
perfect in `cap_2man_b`. Native `Account::ResetCardCollectionBook` resets the in-memory Account -
which is exactly what frame 15535 describes - and leaves the tables alone.

Both phases are therefore true at once, and T210's two-phase implementation is what produces them:
`ReplaceCardCollection` replaces only the collection, and `MarkPerfectCardRefresh` makes the one
reply after the command report the reset view.

## The test

`T190Cards_perfect_collection_matches_captured_refresh_and_full_load` keeps both byte assertions
unchanged - `PerfectCardCollection`'s refresh still equals frame 15530, and the reply to frame
15533 still equals all 3541 payload bytes of frame 15535, because the one-shot view reports preset
amount 1, preset index 0 and three empty lists exactly as the capture does. What changed is the
state assertion after it:

- the stored combine, claimed reward, mount and preset amount 3 all survive the command;
- a **second** `SDB_REQUEST_CARD_DATA` - the next load - reports one preset, one combine, one
  claimed reward and preset amount 3, and is 44 bytes longer than the first (one 16-byte preset,
  one 16-byte combine, one 12-byte reward);
- the one-shot view is reset at the top of the test so it cannot leak between tests.

No production change. `data/t210/frames.json` already holds the two client frames the comment
cites, and T210's own tests pin them.

No build or test run was performed by the assistant.
