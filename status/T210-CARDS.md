# T210 - /@perfect_card_collection

The reply was never wrong. The command destroyed the account's presets and claimed book rewards,
and the card panel opens on the preset page.

## 1. The byte-by-byte comparison

`S_CARD_DATA` is what World builds from our `DBS_RESPONSE_CARD_DATA` (0x2987), so it is the
comparison that matters. Ours after `/@perfect_card_collection` (`cap_bg3_client1` 5663) against
retail's after its own perfect (`cap_2man_b_client2` 2705):

| | length | cards | presets | rewards | preset amount | book level | book point |
|---|---:|---:|---:|---:|---:|---:|---:|
|ours|2680|218 @52|1 @2668|0|1|3|10980|
|retail|2680|218 @52|1 @2668|0|1|3|10980|

**Five bytes differ, all identity:** offset 18 (the gameId's low byte, 03 vs 01) and offsets 42, 44,
46, 48 (the UTF-16 name, "test" vs "dobb"). Every card element is identical: 218 of them, templates
300000..360495, amount 20 each.

So the perfect write lands where the load reads it (`cards` + `card_info`, both account-keyed), the
reply is post-command state, and the client receives all 218 cards. The tap agrees:
`cap_bg3_ctl` 11932 `A->W 0x2985` (refresh, account 1) -> 11935 `W->A 0x2986` (DlmId 0x198,
AccountDbId 1, UserDbId 10) -> 11937 `A->W 0x2987` **3547 bytes**, count 218 at offset 59.

## 2. What is actually wrong

The next load on the same retail account (`cap_2man_b_client2` 3634, and `cap_bg1_client1` 182 for a
character that was already perfect) is **2792 bytes**:

| | length | cards | presets | rewards | preset amount |
|---|---:|---:|---:|---:|---:|
|retail, refresh right after the command|2680|218|1|0|1|
|retail, the very next login|2792|218|**3**|**8**|**3**|

Both are true at once only if the command reset the **in-memory Account** and left the stored rows
alone - which is what `Account::ResetCardCollectionBook` does, and why that refresh reply reports 1
preset and no rewards while the login reports 3 and 8.

T190 persisted the reset: `ReplaceCardCollection` called `ResetCardCollection`, which deletes
`card_mounts`, `card_combines` and `card_book_rewards` and rewrites `card_info` to
`DefaultCardInfo`, and then wrote `PresetAmount = 1`. So after one `/@perfect_card_collection` the
account has one empty preset for good - and the card window opens on the preset page, which is what
"shows no cards" is. The collection book behind it was full the whole time.

| Change | File |
|---|---|
|`ReplaceCardCollection` replaces only the `cards` rows and keeps the stored `preset_amount` (it *is* the CardPresetIndex value the native reset leaves intact, as that method's own citation says)|`Persistence/CharacterStore.cs`|
|`MarkPerfectCardRefresh` / `TakePerfectCardRefresh`: a one-shot view so the refresh after the command still reports preset amount 1, no presets, no combines and no claimed rewards - byte-identical to cap_2man_b_client2 2705|`World/DbProxyT167.cs`|
|`PerfectCardCollection` arms it|`Handlers/GmCommands.cs`|

`ResetCardCollection` itself is untouched: it is still what `SDB_...` reset paths use.

## 3. Tests

`Tests/T210.cs` + `data/t210/frames.json` (the three client frames):

- ours and retail's post-perfect loads differ in exactly bytes 18, 42, 44, 46, 48; both carry 218
  cards, the first being template 300000 amount 20, preset amount 1, level 3, 10980 points, no rewards.
- retail's next login carries 218 cards, preset amount 3, 3 presets, 8 claimed rewards - the state a
  perfect must not destroy.
- store round-trip: a mounted card, preset amount 3 and a claimed reward survive
  `ReplaceCardCollection`, while the collection is replaced and level/points updated; the one-shot
  refresh view arms once and is consumed by one reply.

## 4. Not established, and the live check

Whether retail also keeps the **mount** rows is not decidable from these captures: retail's next
login shows 3 preset elements, but that packet's preset element layout (36 bytes, not the 12 our DB
reply uses) is World's, not ours. Mounts are still deleted on the reset paths; only the perfect no
longer deletes them, because it no longer resets anything durable.

Live check: on an account with a mounted card and a claimed book reward, run
`/@perfect_card_collection` - the collection book should fill (218 cards) and the card panel should
still hold its presets after a relog. No build or test run was performed by the assistant.
