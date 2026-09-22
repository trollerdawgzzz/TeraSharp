# Why a new warrior's skills are locked — T18

**Answer: it is not the weapon and it is not a missing or empty reply. It is the starter blob.**

A level-1 character's skills live *inside* the 15312-byte world blob. The real ArbiterServer
writes them there at **character creation**, from `Executable\Datasheet\DefaultSkillSet.xml`,
keyed on race/gender/class. `data/starter_blob.bin` — the template TeraSharp copies for every new
character — is a **Popori-female Glaiver's** blob (race 4 / gender 1 / class 12 = Elin valkyrie),
so until T18 every character we created shipped *the valkyrie's* skill ids. A warrior's own skills
were never in `S_SKILL_LIST`, so the client drew his whole tree as not-yet-learned: icons present,
all locked.

Fix: `StarterBlob.Build` now writes the class's own list into the blob's two skill regions
(`StarterBlob.ApplyDefaultSkills`), from a table generated out of that datasheet.

---

## 1. Where the skills actually live

Two arrays inside the blob, 8 bytes per slot:

| blob offset | slots | contents |
|---|---|---|
| **6880** (0x1AE0) | 40 | passive skills |
| **7200** (0x1C20) | 500 | active skills |

Slot layout is `[u32 skillId][u8 flag = 0][2 bytes padding]`; an id of 0 terminates the list, and
everything past the last entry is zero in the captured blob. The two regions are adjacent
(6880 + 40x8 = 7200) and end at 11200, well inside 15312.

Both offsets are proven from **both** sides of the wire, independently:

**Arbiter — writes them.** `AccountManager::ExecCreateDefaultSkills(int, UserData *)`
(`Arb_part_080.c:12875`) looks the character's (race, class, gender) up in the `DefaultSkillInfo`
datasheet, then for each active id runs `spInsertSkillLearned` and, on success, does
`*(u32*)(p-4) = skillId; *p = 0; p += 8` starting at `UserData + 0x1C24 - 4` = **UserData+0x1C20**;
the passive ids go the same way through `spInsertPassiveSkill` at **UserData+0x1AE0**. `UserData`
*is* the blob struct, so those are blob offsets 7200 and 6880.

The sheet itself is loaded by the **Arbiter**, not World: `DatasheetManager::LoadDefaultSkillInfo`
(`Arb_part_085.c:5992`) registers it as `L"DefaultSkillInfo"` and reads the `race`, `gender` and
`class` attributes. `WorldServer.exe.c` has no `DefaultSkill*` string at all. So this is our job.

**World — reads them back.** `UserEnterWorldContext::SetRecvData(unsigned char *, int)`
(`WorldServer.exe.c:1661252`) memcpys the blob from our `DBS_USER_ENTERWORLD` (0x2738) to
`context + 0xA8`. `UpdateRecvedData` then copies `context + 0x1B88` (40 entries) and
`context + 0x1CC8` (500 entries) — i.e. blob 6880 and 7200 — into `User + 0x8320` and
`User + 0x8460`. `User::SendMySkillList` (`WorldServer.exe.c:2193096`) walks exactly those two
arrays, actives first, and that is `S_SKILL_LIST`.

## 2. The proof, end to end

From `<captures>\cap_newchar.log` + `cap_newchar_client.log` (the same session — a character
created and played from scratch by the *real* ArbiterServer):

- `data/starter_blob.bin` holds **7 active** ids at 7200 and **17 passive** at 6880:
  `10199 60199 140199 160199 9020100 9030100 60401301` and
  `10002 19500 19501 94001 94002 94003 94005..94015`.
- `DefaultSkillSet.xml`, row `race="Popori" gender="Female" class="Glaiver"`, lists **exactly**
  those 7 and those 17, in the same order.
- `cap_newchar_client.log` packet **81** — the first `S_SKILL_LIST` that character ever received —
  carries **24 entries**: the same 7 (trailing byte 1 = active) followed by the same 17
  (trailing byte 0 = passive), in the same order.
- All six `SDB_UPDATE_USER_DATA` (0x27CB) saves in the session carry the skill regions byte-identical
  to the starter blob, **until the last one** (seq 4189), where `150199` has been appended to the
  active list. That is the skill learned at the trainer at 05:52:49 — the one
  `SDB_USER_LEARN_SKILL` (0x278E, seq 2750) in the whole capture — and the second `S_SKILL_LIST`
  (client packet 2287) has 25 entries, the extra one being 150199.

So: skills in -> blob -> `S_SKILL_LIST`, and newly learned skills are persisted back through the
blob. **This also closes T15's open question 3** ("where does World load learned skills at login?"):
it is the blob, which is why `DBS_USER_LEARN_SKILL` (0x278F) does not have to carry them — World's
handler only stores the skill-period list and two flags.

## 3. What was ruled out

- **A missing or empty reply.** Every skill-flavoured load opcode we answer with an empty list is
  *also* empty in the capture, so none of them is the source:
  `0x2764 SDB_LOAD_SKILL_PROF` -> `0x2765`, `0x28C9 SDB_LOAD_SKILLPERIOD` -> `0x28CA` and
  `0x2922 SDB_LOAD_PASSIVITY_COOLTIME` -> `0x2923` are all the 13-byte empty-list reply in
  `arb_world.log` too, and the character they were answered for had a full skill bar. There is no
  data-bearing reply we are flattening.
- **`0x278E / 0x278F`.** One occurrence in five minutes of play, at a skill trainer. It is not a
  login-time bulk grant, and T15 already answers it correctly.
- **`CreateCharData.xml`.** Confirmed: it carries only `InitItem` / `InitMoney` per class and no
  skill entries (its own header comment says it is loaded by the Arbiter alone). `learnAllSkills`
  is unset, and nothing in that sheet would grant skills if it were.
- **The weapon.** A wrong-class weapon greys out the skills that *require* it; it cannot remove a
  class's skills from `S_SKILL_LIST`, and here the warrior's ids were not in the packet at all.
  `ItemEquipRestriction.xml` is about per-continent/battleground slot overrides, not class locks.
  T14's per-class starter kit is still right and still needed — a warrior with a runeglaive would
  be a *second*, separate problem — but it does not explain this one.
- **The tutorial path.** `Handler_AS_ENTER_WORLD` (`WorldServer.exe.c` ~2985534) branches on the
  byte at **frame+0x79 = AS_ENTER_WORLD payload[115]**: non-zero builds a
  `TutorialUserEnterWorldContext`, which *does* add default skills from a datasheet
  (`UpdateRecvedData`, `:1663323`) and write them back. Tempting, but that byte is **0** in the
  capture (packet 130) for the brand-new character, so the real server did not use it either.
  Do not set it: `DLMItem::CheckGameObjectRestrction` drops DLM items for tutorial users
  (`status/HANDOFF.md` section 1), which is a wedge waiting to happen.

## 4. The fix

`Persistence/CharacterStore.cs`:

- `DefaultSkillSet` — the 99-row sheet, embedded as a table (`"race,gender,class|active;ids|passive;ids"`)
  so nothing new has to be shipped or found at runtime. Regenerate from
  `Executable\Datasheet\DefaultSkillSet.xml` if the datasheet changes.
- `StarterBlob.PassiveSkillsOffset` / `ActiveSkillsOffset` / slot counts / `SkillEntrySize`.
- `StarterBlob.ApplyDefaultSkills(blob, race, gender, cls)` — clears both regions, then writes one
  8-byte entry per id. Called from `StarterBlob.Build`, so both creation paths in
  `CharacterHandlers` get it with no change there.
- An unknown race/gender/class combination **clears** both regions rather than leaving the
  template's. The real Arbiter's lookup finds no row and inserts nothing; a character with no
  skills is a visible bug, a character with the valkyrie's is a confusing one.

The numeric ids are the Arbiter's own, decompiled from its name->id lookups rather than assumed:

- race, `FUN_1400c4e20` (`Arb_part_006.c:523`): Human 0, HighElf 1, Aman 2, Castanic 3, Popori 4, Baraka 5
- class, `FUN_140065e00` (`Arb_part_003.c:2179`): Warrior 0, Lancer 1, Slayer 2, Berserker 3,
  Sorcerer 4, Archer 5, Priest 6, Elementalist 7, Soulless 8, Engineer 9, Fighter 10, Assassin 11,
  **Glaiver 12**
- gender: Male 0, Female 1 (`LoadDefaultSkillInfo` maps the literal `"Female"` to 1)

Cross-check: the captured character is race 4 / gender 1 / class 12 in the blob, and
Popori/Female/Glaiver is the row whose skills it has.

## 5. Tests

In `src/TeraSharp.Arbiter.Tests`:

| test | what it pins |
|---|---|
| `DefaultSkills_template_holds_the_captured_valkyrie_list` | the 7+17 ids at 7200/6880 of `data/starter_blob.bin` |
| `DefaultSkills_rebuild_reproduces_the_captured_blob_byte_for_byte` | `ApplyDefaultSkills(blob, 4, 1, 12)` returns the real Arbiter's blob **unchanged** — the table, the offsets and the entry layout all at once |
| `DefaultSkills_human_warrior_gets_warrior_skills_not_the_valkyrie_s` | `10100;20100;9020100;60401301` + `10001;19100;19101;19102`, and none of the four Glaiver-only actives |
| `DefaultSkills_unknown_combination_clears_both_regions` | no valkyrie leakage, and nothing outside 6880..11200 moves |
| `DefaultSkills_entry_layout_is_id_then_a_zero_flag_and_padding` | `[u32 id][4 zero bytes]`, terminated by an empty slot |
| `DefaultSkills_regions_are_adjacent_and_inside_the_blob` | 6880 + 40x8 == 7200, 7200 + 500x8 <= 15312 |
| `DefaultSkills_table_covers_the_sheet_and_fits_the_regions` | all 99 rows present, none overflows its region |
| `DefaultSkills_every_creatable_class_gets_a_list` | 11 race/gender combinations, Baraka male-only |

`StarterBlob_rebuilds_the_captured_blob_exactly` (T12) now covers the skill regions for free, and
`PatchedWindows()` gained the 6880..11200 run so
`StarterBlob_patches_only_the_per_character_fields` still means what it says.

Note for whoever reads the ids: `9020100` and `60401301` are in **all 99** rows — every character
gets them — so they are not evidence of a class mix-up. The Glaiver-only actives are
`10199 60199 140199 160199`.

## 6. Still open

- **Live verification.** Create a human warrior on a fresh World and check `S_SKILL_LIST` carries
  `10100 20100 9020100 60401301`. Characters created *before* this change keep the valkyrie ids in
  their stored blob — delete and recreate them, there is no migration.
- **Levelling.** World auto-learns on level-up in `DBLevelExpContext::AutoLearnSkills`
  (`WorldServer.exe.c:1121775`), which reads its own datasheet and pushes the result to us; the
  new ids reach the blob through `0x27CB`, so nothing more should be needed. Unverified live.
- **`SkillGetConList.xml`** (skill acquisition conditions per class/level) is what World's
  `FUN_140bd6070` "can learn" check consults. We never need it as long as World owns levelling —
  noted here so the next session does not go looking for it again.
- **Race 2 has no name string in the decompile** (`&DAT_140ac7cb8` -> 2); it is Aman by
  elimination from the datasheet's own `race=` values, and the table uses that.

---

## T152b - a level set outside World never auto-learns

| | real dob, lvl 70 (cap_final 411) | `test`, lvl 70, session A (cap_skills3 714) | `test`, session C 03:48 (15197) |
|---|---|---|---|
| blob level (+204) | 70 | 70 | 70 |
| active / passive skill entries | 164 / 22 | 7 / 17 (the creation set) | 54 / 18 (A's 48 manual learns) |
| SDB_UPDATE_EXP_LEVEL (0x273B) ever | yes | never | never |
| crest reply | - | 691 B echo (pre-T151) | 19 B; World asked for 0 crests (it has them) |

- World learns ranks itself: `DBLevelExpContext::ExecuteCommitSQL` -> `AutoLearnSkills` learns every type-11 row
  `IsSkillLearnable` allows, on every level commit. cap_social4: `perfect_level 65` (1401) -> 168 SDB_USER_LEARN_SKILL in
  0.8 s; `perfect_level 20` (7539) -> 43.
- `test` got to 70 through /api/set-level, which only wrote the store. No level commit ever ran in World, so no ranks
  were granted: the Learned Skills window's "[Click to obtain]" rows.
- The clicks this session: C_SKILL_LEARN_REQUEST (60401301, isActive 0) at 573 and (60199, isActive 0) at 589 ->
  S_SYSTEM_MESSAGE @3534 (0xDCE, IsSkillLearnable false) each, before any GM toggle (visible). Both skills are already
  in the list (60199 behind 60299); the 13 rows World does offer (S_SKILL_LEARN_LIST, e.g. 60399, 150299) are
  isActive 1 and were not clicked.
- Fix: /api/set-level also hands World `perfect_level N` (AS_ADMIN_COMMAND, cap_social4 1401 shape) - at once if online,
  else behind the next S_SPAWN_ME. For `test` now: run set-level 70 again, or `/@perfect_level 70` in game; if World
  does not commit an unchanged level, 69 then 70.

