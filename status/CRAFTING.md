# Crafting and gathering (T147)

## Why the window did not work

The crafting window reads two client packets, and neither is ours:
`S_ARTISAN_SKILL_LIST` (0x57AB) and `S_ARTISAN_RECIPE_LIST` (0x61FE) are built by
WorldServer and reach the client inside `SA_BYPASS_TO_CLIENT` (0x13F7) on bypass link 2,
about 15 ms after the Arbiter answers the two artisan loads (cap_social4.log seq 337, 560,
4978, 5603, 5802). The Arbiter has no writer for either, only the packet-log dumper.

What the Arbiter owns is the data behind them. Before T147 it answered the two loads (always
empty, replay-served, byte-identical to the real server) and **nothing else**. Six crafting
writes and four gathering writes carry a DlmId and had no answer, so the first recipe read,
craft, bookmark or gathered node head-blocked that character's DB queue for the life of the
World process (status/HANDOFF.md section 1).

## The client packet (World-built, for reference)

`S_ARTISAN_RECIPE_LIST`: @4 count, @6 first offset, @8 Update u8, @9 EndOfList u8. Element
46 bytes: +8 recipe id, +12 skill-prof id (the one STEP2 updates), +20 product template,
+24 amount, +28 -1, +32 u8, +33 u32 needed proficiency, +37 u64 the session's enter-world
time (not the learn time - T147b), +45 u8, then its 17-byte materials. The populated form is in
classic_live (a real player); our server always sends the empty
`0A 00 FE 61 00 00 00 00 00 01`. `S_ARTISAN_SKILL_LIST` is byte-identical between our server
and Classic+. Once the DB rows exist, World fills both itself.

## DB messages

Offsets are payload-relative (frame - 6); ref slots hold frame-relative offsets. Layouts are
from each message's packet-log dumper and handler in ArbiterServer.exe.c; the two loads are
also pinned by capture (cap_social4 seq 260-263, cap_newchar 171-174, lobby_tap 140-143).

| request | reply | request layout | reply layout | real behaviour |
|---|---|---|---|---|
| 0x2760 SDB_LOAD_ITEM_RECIPE | 0x2761 | DlmId@0 Owner@4 | ref@0/4, DlmId@8, Success@12, then 28-B RecipeInfo each | dbo.spLoadItemRecipe |
| 0x2764 SDB_LOAD_SKILL_PROF | 0x2765 | DlmId@0 Owner@4 | as above, 8-B [id][value] records | dbo.spLoadSkillProf |
| 0x275E SDB_LEARN_ITEM_RECIPE | 0x275F | ItemBinary ref@0/4, DlmId@8, Owner@12, RecipeId@16, Extract u8@20 | ref@0/4, DlmId@8, Success@12 + atoms echoed | atoms, then User::LearnItemRecipeNoLock (time = now, bookmark 0) |
| 0x2762 SDB_DELETE_ITEM_RECIPE_LIST | 0x2763 | u32 ids ref@0/4 (BYTE count), DlmId@8, Owner@12 | **Success@0**, DlmId@1 | dbo.spDeleteItemRecipe per id; flag starts false, so an empty list answers 0 |
| 0x288A SDB_SET_RECIPE_BOOKMARK | 0x288B | DlmId@0 Owner@4 RecipeId@8 Flag u8@12 | DlmId@0 Success@4 | dbo.spSetItemRecipeBookmark; true whenever the SQL ran, found or not |
| 0x2756 SDB_ITEM_PRODUCE_STEP1 | 0x2757 | DlmId@0 Owner@4 Type@8 DeltaPoint@12 | DlmId@0 Success@4 | the same FatigabilityController call as SDB_UPDATE_FATIGABILITY_POINT; Success = user loaded |
| 0x2758 SDB_ITEM_PRODUCE_STEP2 | 0x2759 | ItemBinary ref@0/4, ItemEnchantData ref@8/12, DlmId@16, Owner@20, SkillProfId@24, SkillProfValue@28 | ref@0/4, DlmId@8, Success@12 + atoms echoed | atoms, then User::UpdateSkillProfNoLock(id, value) when value > 0 |
| 0x2766 SDB_UPDATE_SKILL_PROF | 0x2767 | DlmId@0 Owner@4 Id@8 Value@12 | DlmId@0 Success@4 | dbo.spUpdateSkillProf; the value is assigned, not added |
| 0x273D-0x2740 S_UPDATE_PROF_MINERAL / BUG / ENERGY / HERB | 0x2741 D_UPDATE_PROF_RESULT | DlmId@0 Owner@4 Value@8 | DlmId@0 Success@4 | User::UpdateUserProf* assigns User+0x278 / +0x27C / +0x280 / +0x284 |

RecipeInfo (28 B): RecipeId i32 @0, Extract u8 @4, learn time 16-B ODBC TIMESTAMP @8,
Bookmark u8 @24.

**Gathering is not a skill_profs row.** The four gathering levels are UserData fields - the
world blob - bound from the `profMineral` / `profBug` / `profEnergy` / `profHerb` columns when
the enter-world record is filled (Arb_part_032.c:17830-17833, blob +0x1C8, +0x1CC, +0x1D0,
+0x1D4; money is at +0x1C0, line 17798). All four read 0 in every captured 0x2738 and 0x27CB
blob (nobody gathered), so whether World writes them back on save is unknown.

## The crafting exchange

`C_START_PRODUCE` is handled inside World; the Arbiter has no handler for it. What the Arbiter
sees is the two DB steps: STEP1 charges production points (fatigability, on the account), then
STEP2 moves the materials out and the product in as ItemTransactionAtoms and sets the
proficiency. No capture contains either step - nobody crafted in any tap - so both are
dumper/handler-derived, not capture-verified.

## What TeraSharp does now

- `World/ArtisanDb.cs`: record formats, request layouts, builders, bounds-checked parsers,
  the gathering blob stamp.
- `Persistence/CharacterStore.cs`: tables `item_recipes`, `skill_profs`, `gathering_profs`;
  all three are cleared by `DeleteCharacter(id, accountId)`.
- `World/DbProxyHandlers.cs`: the two loads are per-character (moved out of
  `DispatchOnlyForTests`); all ten writes are allow-listed and answered with the live DlmId,
  including on a truncated request. LEARN and STEP2 clone their atoms with fresh item ids,
  apply them to `items` and echo them. STEP1 adds its delta to the account's fatigability row
  exactly as the T26 handler does. Gathering values are stamped into the blob in
  `OnUserEnterWorld`, only for kinds that have a stored row, so a character that never
  gathered is sent its blob unchanged.

All twelve have rows in status/PERSISTENCE-MAP.md, so the T15 gap guard
(`Every_per_user_request_opcode_is_answered`) now fails if any of them loses its answer.

Tests: `T147_*` in the test project (empty loads byte-exact vs cap_social4 261/263; learn,
bookmark and proficiency round trip; delete-list parsing and bounds; STEP2 atoms and
proficiency; STEP1 fatigability; truncated requests; gathering answer and blob stamp).

## Open

- An A<->W tap of a craft and a gather on our own server would pin the request bytes; the
  client side is pinned (T147b below).
- STEP1's Type is always 1 (T147b), the one bucket T26 stores.
- STEP2's ItemEnchantData is not stored (no enchant rows are modelled).
- `S_PLAYER_CHANGE_ALL_PROF` (0x70CC, World-built) not examined.

## T147b - checked against live Classic+ (classic_craft.log)

A client-side capture of a character that crafts (C_START_PRODUCE for recipe 286040 twice and
286050 twice) and gathers (two plant nodes, one ore node). It cannot show the A<->W link, so
what it pins is World's output and every value the Arbiter feeds into it. Fixtures:
`data/classic-live/S_ARTISAN_SKILL_LIST-95.hex`, `S_ARTISAN_RECIPE_LIST-96.hex`; tests
`T147b_*`.

| What | Live evidence | Result |
|---|---|---|
| S_ARTISAN_SKILL_LIST | frame 95, 169 B: count@4, off@6, update u8@8; 32-B element: id@4, same id@8, value@12, flag@16, state@20, @24, tier@28 | decode and re-encode byte-exact |
| skill-prof rows | skills 6/21/22/23/24 read 500/555/590/1/9; an empty load lists all five at 1 (cap_social4_client 63), so 1 is World's default | our 8-B [id][value] record is element +4 / +12 |
| S_ARTISAN_RECIPE_LIST | frame 96, 3451 B, 36 recipes of 46 B + 17-B materials; closing frame 97 `0A 00 FE 61 00 00 00 00 01 01` | decode and re-encode byte-exact |
| recipe id | C_START_PRODUCE 8212 / 9333 ask for 286040 / 286050, both listed at +8 under skill 6; loot lines 8232 / 9358 name products 423020 / 423021 (= +20) | +8 is RecipeInfo.RecipeId; our store keeps list order |
| +37 u64 | all 36 read 1790035042 = S_SEND_USER_PLAY_TIME's second field (frame 112) | enter-world time, not the learn time - T147's note corrected |
| +33 u32 | 0 / 250 / 300 / 450 / 500 | needed proficiency from the datasheet, not DB-fed |
| exchange | C_START_PRODUCE {recipe, 0} -> S_START_PRODUCE {5000 ms} -> S_REQUEST_CONTRACT (type 31) -> S_END_PRODUCE {1} -> C_CANCEL_CONTRACT -> loot -> S_FATIGABILITY_POINT -> S_ARTISAN_SKILL_LIST -> recipe list + closing frame | all World-side; ContractMakeProduct makes no through-Arbiter call |
| STEP1 | World's ExecuteTransaction writes Type 1 and DeltaPoint = minus the cost. Fatigue 1340 -> 1335 -> 1330 (286040, 5 each), 1230 -> 1210 and 1150 -> 1130 (286050, 20) | our handler reproduces all four totals |
| STEP2 | World writes the recipe's skill-prof id and the NEW absolute value (current + gain, capped); skill 6 stayed 500 through all four crafts | these carried (6, 500); the upsert is right |
| gathering | ALL_PROF energy 350 / herb 350 / bug 0 / mineral 350; S_PLAYER_CHANGE_PROF type 1 after the plant nodes (templates 3, 1), type 2 after the ore node (101). World's ProficiencyType: 1 herb 0x2740, 2 mineral 0x273D, 3 bug 0x273E, 4 energy 0x273F; SetProficiency writes even when the value does not move | three writes of 350 (herb, herb, mineral); stamped at +0x1D4 / +0x1C8 |

The -60 per gather is not a produce step; it is presumably the ordinary fatigability update
(0x2910, T26).

**No mismatch in TeraSharp's code.** One correction to the notes (+37 is not a learn time).
Added `ArtisanDb.ProfHerb..ProfEnergy` and `ArtisanDb.GatheringOpFor(type)`.

**Still capture-less on the A<->W link** - all twelve: the populated loads (36 RecipeInfo rows,
four or five SkillProf rows), LEARN, DELETE_LIST, SET_BOOKMARK, STEP1, STEP2 and its atoms,
UPDATE_SKILL_PROF and the four S_UPDATE_PROF_*. RecipeInfo's Extract, Bookmark and learn time
have no client-visible trace here: nothing was learned, extracted or bookmarked.
