# T184d — login-to-FIN client state, re-derived from captures

**No unique popup gate is proved.** There are several independent client-state differences; neither “the stream is retail-equivalent” nor “APM consent is the missing flag” is supported. The strongest additional candidates are the hardcoded lobby `isNewCharacter`, login status, and the missing dungeon-list matching status/role rows. Saved UI state also differs, but its unknown flags must not be renamed “consent.”

## Scope and complete field tables

Full HEX records, not the truncated `_ctl.txt` payloads:

| capture | inclusive window | S_ frames | distinct S_ opcodes | C_ frames |
|---|---|---:|---:|---:|
| `classic_live3.log` (R, working control) | C_LOGIN_ARBITER #2 through FIN #10585 | 10,415 | 171 | 169 |
| `cap_queue4_client1.log` (O, failing) | C_LOGIN_ARBITER #2 through FIN #1184 | 1,084 | 137 | 99 |

Union: **188 S_ opcodes, 11,499 S_ frames, 1,487 normalized decoded field paths** after the manual corrections below. Every S_ record is inventoried; none was filtered out because it appeared unrelated. These are different characters, dungeons, server sheets and play histories, so arbitrary packet ordinals/actor IDs cannot be meaningfully paired.

Local evidence under `data/t184d/`:

| artifact | contents |
|---|---|
| [OPCODES.md](../data/t184d/OPCODES.md) | Every opcode, counts, first/last record, all observed sizes, automatic definition coverage |
| [ALL-FIELDS.md](../data/t184d/ALL-FIELDS.md) | Requested `opcode / field / real / ours / verdict` index, including equal fields; array indices normalized only here |
| [field-ledger.tsv](../data/t184d/field-ledger.tsv) | Every decoded field occurrence with actual array index and capture record; full values, not the index's shortened summaries |
| [server-records.tsv](../data/t184d/server-records.tsv) | Every S_ record in original order, definition version and decode result |
| [SELECTED-CHARACTER.md](../data/t184d/SELECTED-CHARACTER.md) | Every field of R player 4742 versus O player 10, selected by C_SELECT_USER #34/#31, not by lobby array position |
| [UI-WINDOWS.md](../data/t184d/UI-WINDOWS.md) | All 56 retail window names and all three fields, compared to our three windows |
| [GFX-OPTIONS.md](../data/t184d/GFX-OPTIONS.md) | Every literal key/value in the decompressed account GFx manager, including the unnamed protobuf flag |
| [CLIENT-SETTINGS.md](../data/t184d/CLIENT-SETTINGS.md) | Every C_ settings request/save, records, sizes, account/descriptor fields |
| `settings-fields.tsv`, `settings-*.json` | Every captured account/user settings controller, compression marker, decompressed bytes and protobuf wire fields; semantic names unavailable for numeric fields |
| `manual.json`, `manual-field-ledger.tsv` | Source-derived corrections for the defective matching/UI definitions |
| `decoded.jsonl`, `schemas.json` | Original automatic decode, raw bytes, chosen definitions and writer traces; local/private, includes login credentials |

**Coverage limitation:** the supplied definitions re-encode exactly for 9,943 R frames and 936 O frames. They are incomplete/nonexact for 471 R + 148 O frames; R #7985 throws because the installed cooldown definition is for patch >=101. This is **620 frames whose supplied definitions do not establish full byte coverage**, not 620 server defects. `OPCODES.md` lists every affected opcode. The raw decoder's F732 values `15/25/11`, C730 `remainSec=786433`, and bonus-info flag `23` are misaligned definition output, not actual game state.

Manual corrections replace those misleading values in `ALL-FIELDS.md`: S_SELECT_USER, C730, 8BE4, FIN, S_REPLY_CLIENT_UI_SETTING, S_DUNGEON_COOL_TIME_LIST, F732 (including all three nested arrays), S_DUNGEON_UI_HIGHLIGHT and S_LOAD_HINT. Bonus-info correction covers its fixed header only. Remaining partial definitions, chat settings internals, and unnamed client protobuf semantics remain explicitly unverified. Exact re-encoding alone does not prove semantic field names.

## Candidate fields and exact changes

Offsets below are **zero-based from the complete client frame**, including its four-byte header. Changes are **ours -> control**, experiments rather than proven fixes. Do one experiment at a time and apply a recurring packet change to every relevant occurrence, otherwise a later packet can overwrite it. Generated `flip-*.hex` files are reviewable packet variants only; nothing was injected, deployed or persisted.

| opcode | field | real | ours | verdict / byte experiment and proof |
|---|---|---|---|---|
| 92A6 S_LOGIN_ARBITER | status | 0, R#7 | 31, O#7 | Account-mode candidate. **+6: `1F 00 00 00 -> 00 00 00 00`**; `flip-status0.hex`. All other fields equal. `GmCommands.cs:228` hardcodes normal=31, so the earlier “normal must be 31” assumption does not describe this working control. Client use as a popup gate is unproved. |
| 6759 S_GET_USER_LIST | selected character.isNewCharacter | false, R#11 player4742 at+1620 | true, O#11 player10 at+460 | New-character/tutorial-state candidate. **O+460: `01 -> 00`**, `flip-newcharacter0.hex`; locate by linked row/player ID on other packets. `LoginHandlers.cs:101` hardcodes true. `tutorialState=0` in both; this difference is separate from that field. |
| 6759 S_GET_USER_LIST | veteran | true, R#11 | false, O#11 | Account entitlement/UI branch candidate. **+8: `00 -> 01`**; `flip-veteran1.hex`. Does not grant account benefits or establish that veteran status controls matching. |
| 4F4B S_UPDATE_CONTENTS_ON_OFF | ContentsOff, type2 | true, R#17/#36 | false, O#16/#36 | Generic feature switch; feature's semantic name unresolved. **+8: `00 -> 01`** on both occurrences for type2. `flip-content2off.hex` uses O#36. |
| 4F4B | ContentsOff, type3 | true, R#18/#37 | false, O#17/#37 | Same experiment, **+8 `00 -> 01`**, `flip-content3off.hex`. |
| 4F4B | ContentsOff, type34 | true, R#26/#45 | false, O#25/#45 | Same experiment, **+8 `00 -> 01`**, `flip-content34off.hex`. Types4/8/9/20/21/22/23 already agree. Dumper A024:1449–1459 calls these ContentsType/ContentsOff; no claim that any is “matching consent.” |
| F732 S_VIEW_INTER_PARTY_MATCH_DUNGEON_LIST | isShowDungeonWorkUI | 0, all seven R lists #7964–10343 | 1, O#1150/#1157/#1175 | Known UI branch input. **+10 `01 -> 00`**, `flip-dungeonwork0.hex`; source chain in MATCHSERVER §8. World `DungeonWorkData.xml showTab=false` forces it. Popup causality still untested. |
| F732 | dungeon9739.MatchTimeStatus | 2, all seven R lists; R row starts659 | 0, all three O lists; O row starts515 | **O+550 `00 -> 02`**, `flip-dungeon9739-status2.hex`. For O's actually queued dungeon9781, row starts623 and byte is **+658**; transplanting 2 there is a cross-dungeon hypothesis, not a captured 9781 pair. |
| F732 | dungeon9739 role-status array | `(mask,status)=(1,1),(2,1),(4,1)`, R#10343 +695/+701/+707 | empty, O#1175 row515 | Missing MatchServer-derived data. **No one-byte fix:** O descriptor +527 count=0/+529 head=0 must point to three new six-byte linked entries; length/links must be rebuilt. Setting count alone produces an invalid packet. Same absence for O9781 (descriptor +635/+637). |
| F732 | dungeon9739.RequiredActPoint | 0, R#10343 +690 | 170, O#1175 +546 | Eligibility/work-resource candidate. **+546 `AA 00 00 00 -> 00 00 00 00`**, `flip-dungeon9739-actpoint0.hex`. O9781 has110 at+654, but R's list has no9781 control row. Do not label this item level: the dumper names it RequiredActPoint. |
| 99F7 S_DUNGEON_UI_HIGHLIGHT | type2 highlight | 0, R#7225 | 1, O#359/#754/#1158 | UI attention state candidate, not proved to gate entry. **+25 `01 -> 00`**, `flip-highlight2off.hex` from last O#1158. Type1 at+16 is initially1 in O, then0 at#1158; retail is0. Clear +16 also if testing from login. |
| CE85 S_LOAD_CLIENT_ACCOUNT_SETTING | S1UI_GFxManager protobuf field3 | 1; R#13/#5461, decompressed offset1297 | 0; O#689, decompressed offset41 | **Meaning unknown, not named consent.** Change decompressed `18 00 -> 18 01`, recompress and fix enclosing lengths. For exactly O#689, validated `flip-gfx-field3-1.hex` remains614 B and changes wire **+257 `01->11`, +260 `24->25`, +262 `64->65`**. All other decoded settings stay unchanged. These offsets are not portable to a different saved blob. |
| CE85 | GFx saved keys | many; e.g. `USDP="100"`, `CWMB="1"` | absent; final manager has only `USIV="100"`, `USPN="100"` and field3=0 | All keys compared in GFX-OPTIONS. Their expansions/defaults are unknown. **No justified single-byte switch**; absent override does not mean false. Do not rename CWMB or another abbreviation “matching consent.” |
| 5FAE S_REPLY_CLIENT_UI_SETTING | DungeonPartyMatching / DungeonPartyMatchingProcess saved windows | present R#29/#7248, IsLocked=1; positions `(13.802,2.754)` / `(49.908,31.597)` approximately | absent in O#26/#48/#423/#810 | Layout/default initialization candidate. **No existing byte to flip**; add linked rows, not a visibility boolean. `flip-ui-add-two-matching-windows.hex` retains our three rows and adds only these two, 116->250 B. Exact float values are in UI-WINDOWS. |
| E9A9 / 88E0 | account packages / benefits | 333/334/336, R#15 and cumulative #50/#51/#52; refreshed #7204 | empty O#15/#81 | Entitlement-dependent UI is possible but unproved. **No one-byte fix**; both are variable-length lists. These are this control's packages, not the previous GM capture's 533/534/1000. Do not make them global player defaults. |
| 6C50 S_RETURN_USER | packet presence | empty frame R#49: `04 00 50 6C` | absent | Low-confidence account/UI branch candidate. Packet insertion, no flag byte; captured presence alone gives no evidence it controls matching. |
| F266 S_LOGIN | level / equipment eligibility state | level65; enchant6, R#53 | level70; enchant999, O#51 | Different character progression, not a proved structural error. Literal level bytes **+65 `46 00 -> 41 00`**, enchant **+258 `E7 03 00 00 -> 06 00 00 00`** are known, but faking S_LOGIN alone conflicts with live World/item state. Use equivalent characters for a causal test. O#910 reports item level655.7919; no corresponding R packet to compare. |
| 90F5 S_SERVER_BUILD_INFO | revision | 375968, R#5 | 376056, O#5 | Low-confidence version-dependent branch. **+8 `F8 BC 05 00 -> A0 BC 05 00`**. This changes only the advertised revision, not client code/data; both streams use the same audited opcode map. |
| 8BC6 S_PARTY_MEMBER_LIST | loot policy / leader.canInvite | loot tuple `(1,3,0,1,0,0,0)`; leader false, R#10578 | `(0,4,1,0,1,1,0)`; leader true, O#1181 | Still nonidentical party UI state. Policy starts+30; fields at+30,+34,+38,+39,+40,+44,+45. Leader invite **+85 `01->00`** in O. No source proves loot or invitation permissions suppress FIN. |

`F732.MatchTimeStatus` and role-status provenance: W:694867 initializes status=0/empty map; W:694876–694905 copies status/map only when the per-dungeon supplied entry exists. W:694929 writes status at row+35; W:694943–694959 writes six-byte `(links,mask,status)` rows through descriptor row+12/+14. Thus **0/empty is a demonstrable consequence of missing list input**, not evidence of a client's declined consent. The complete enum meanings remain unresolved. A024:11673–11910 independently names the header and scalar fields. Timeline entries and reason strings are decoded too in `manual.json`.

The 99F7 layout is two nine-byte linked rows `(i32 type,u8 highlight)`, verified against W:979315–979331. Our last pre-FIN type1 value has already returned to retail's0; it cannot be reported as a remaining1. `S_LOAD_HINT` is **not consent**: A021:3453–3539 names each row HuntingZoneId/TemplateId; R#110 contains `(182,2029)`, ours is empty.

## Other requested state and exclusions

| opcode | field | real | ours | verdict |
|---|---|---|---|---|
| 8AFB S_SELECT_USER | accepted, adminLevel, error, firstToday, firstAccount | `(1,0,0,0,0)`, R#35 | identical O#33 | **Byte-identical all15 B.** No recurrence of the Alt+A admin-level difference. Correct packed layout is u8@4/i32@5/i32@9/u8@13/u8@14; shipped `unk1/2/3` labels are misleading. |
| 92A6 | success/loginQueue/language/pvpDisabled/unk/unk1/unk2 | true/false/6/false/0/0/0 | equal | Only status differs. |
| 6759, selected character | adminLevel/isBanned/canUseStatus/isSecondCharacter/tutorialState/isDeleting | 0/false/0/false/0/false | equal | No captured GM, ban, disabled-character or tutorial-state value explains the missing popup. isNewCharacter is separately different above. |
| 6759, account header | maxCharacters / deletion cutoff / deleteCharacterExpireHour2 | 14/65/0 | 8/5/72 | Lobby slot/deletion policy; no matching-path link found. All per-character fields, including equipment/rest XP/timers/cosmetics, are in SELECTED-CHARACTER and the full ledger. |
| F266 | actionMode/alive/status/visible/isSecondCharacter/isPkServer/chatBanEndTime/isWorldEventTarget | 0/true/0/true/false/true/0/false | equal | These obvious world-state flags agree. Identity, class, XP/EP, proficiency, movement speed, cosmetics and servants differ; all preserved in the field ledger. |
| 6B0D S_LOGIN_ACCOUNT_INFO | accountId / antiCheatChecksumSeed | 2267/917033 | 2/203396 | Account/session state. |
| 6B0D | dbServerName / apiServerAddress / token | PlanetDB_2800_classicplus / 127.0.0.1:8800 / present | PlanetDB_2800 / 192.0.2.10:8800 / present | Different deployments, not a proved popup flag. Credential values redacted in reports. No captured field identifies an API consent response. |
| CE85, account blob | load order / sizes | R#13,#5461:979 B | O#12:8 B(empty), #338:600 B, #689:614 B | O#30 saves the client's own592-B data, which O#338 echoes; #657 saves606 B, echoed #689. An empty initial account setting is not permanent absence. |
| 6EF4, user blob | load order / sizes | R#5462:2556 B | O#47,#339,#690:1083 B | O also sends user settings before S_LOGIN; retail's user setting here is post-spawn. Timing and contents differ. No evidence that the extra early copy prevents a later valid initialization. Controllers decompressed in settings-fields.tsv. |
| 5FAE / 7CF9 | saved window/chat settings | 2536/1160 B | 116/959 B | UI windows decoded fully above. Chat def is empty; internal fields remain opaque, not an invented consent bit. |
| D768 S_DUNGEON_COOL_TIME_LIST | dungeon entries | R#7985: seven rows,152 B | O#907/#1152: no rows,12 B | v100 rows20 B, not installed v3's21 B. All cooldown seconds0, daily counts-1; weekly counts vary with history. Neither queried match ID9739 nor9781 is blocked by a row here. |
| 9D66 S_DUNGEON_CLEAR_COUNT_LIST | clears/rookie | R#7963 etc:14 IDs;9739=(31,0) | O#908 etc: same14 IDs; all=(0,1) | New versus experienced character state. 9781 absent from **both** fixed14-ID rosters; don't infer one missing opcode. Row shape/ID list agrees. |
| F732 | userPool/partyPool/convoke/failReason/nextOpenTime/newbie/isEvent for9739 | 1/0/0/-1/-1/0/1 | equal | No additional selected-dungeon refusal/consent difference in these fields. RequiredActPoint/status/nested roles differ as above. |
| 810D S_AVAILABLE_EVENT_MATCHING_LIST | lists/limits | R#7256:2421 B, other sheet/account state | O#125:409 B, later #660:8478 B | Compare **latest** O state, not initial one-quest packet. Supplied definition is nonexact; raw unknown words cannot be called restriction flags. World data/event populations differ. |
| 93D2 S_UPDATE_EVENT_MATCHING_BONUS_INFO | fixed header | absent | #126 `(daily=1,left=1,exceeded=0,complete=0,max=3,received=0)`; #127 daily0/max16 | Corrected from A024:1493–1544. Daily reward state, not a demonstrated auto-match consent flag; nested reward-array semantic decode incomplete. |
| F1A1 / AB1A | account type/play time / F2P permissions | accountType6, minutesLeft0; permission frame28 B | equal decoded fields | No differing time-expired/premium-permission field. |
| A30E S_ADMIN_HOLD_CHARACTER | hold | false, R#7240 | false, O#595 | Both unheld before matching. |
| simple tips | IDs1/2/35/39/41 hide | all true, R#7382/#7465/#7536/#7610/#7780 | all true, O#608/#631/#643/#647/#649 | Same tutorial-tip suppression for these IDs. |
| 8BC6 | ims/raid/memberLimit/anonymized | true/false/5/false | equal | Two actual members versus retail five is still a different roster, not a missing IMS flag. |
| C730 / 87AC / 8BE4 / FIN | queue/match burst | retail has progress replies; FIN `(9739,0,0)` | O#1179 pool, #1180 queued1/1, #1182/#1183 resets, #1184 FIN `(9781,0,0)` | C730 and FIN match retail after player/dungeon substitution. Oclient1 never requests progress before FIN; client2 did and still had no popup (T184c). This is not enough to identify missing progress as the cause. |

The actual cooldown rows are `(id,type,seconds,day,week)`:
`(3012,0,0,-1,-1), (9043,0,0,-1,198), (9156,0,0,-1,193), (9168,0,0,-1,194), (9756,0,0,-1,199), (9768,0,0,-1,199), (9830,0,0,-1,-1)`.
No S_DUNGEON_WORK_LIST, rank-record list or rank-season list appears in either selected window; missing requests cannot be replaced by imaginary reply comparisons. Every other one-sided S_ opcode is listed in OPCODES, including retail's empty S_LOAD_SKILL_SCRIPT_LIST, return-user packet, guild/servant/premium state and our extra save acknowledgements. Presence differences alone do not show an unregistered handler.

## APM: the actual prompt and accept state

| step | byte/source evidence | conclusion |
|---|---|---|
| QA commands | W:2186391 `apm_ask -> FUN_140495720`; W:2186393 `apm_reset -> FUN_1405341f0` | Both are region-party operations. |
| Ask callback | W:846827–846850, `User::AskUsingRegionPartyMatch`; requires User+0xB0E5, sets +0xB0E8=2, checks pool type0 | Runtime regional consent state; not a general dungeon flag in S_LOGIN. |
| S_ prompt | W:675954–675962 writer emits **0x88F3 S_SUGGEST_DARK_RIFT_PARTY_MATCHING**, empty body | This is the S_ prompted by `/@apm_ask`. 376012 map confirms35059; autogenerated def comment36851 belongs to a different mapping. |
| C_ answer | **0xFA16 C_SUGGEST_DARK_RIFT_PARTY_MATCHING**,5 B, reply at+4; W:603189–603209 | Valid dark-rift pool/type required; zero sets +0xB0E8=2, nonzero sets1 and joins. The acceptance is in World's runtime state. |
| Reset | W:955648–955652 sets +0xB0E8=0 | No S_ flag packet emitted by this setter. |
| Capture test | Neither0x88F3 nor0xFA16 appears in R#2–10585 or O#2–1184 | **No capture proof that retail had accepted APM consent.** Absence does not reveal a pre-existing runtime value. No supported “consent byte” to transplant. |

S_REQUEST_CHANGE_PARTY_MATCH_RULE, S_REQUEST_DUNGEON_MATCHING_UI and S_MATCH_CHANGE_COND_TYPE are also absent from both windows. They cannot explain the difference as missing retail packets.

## C_ settings unique to ours

Only **C_SAVE_CLIENT_ACCOUNT_SETTING 0x8BDA** is unique among settings opcodes: O#30(600 B), #657(614 B); none in R's audited interval. This is a client-authored options save, **not a consent packet by its known schema** (`[u16 offset=8][u16 blobLength][blob]`). R already loads a saved account blob; O starts empty and creates one. Both streams send C_SAVE_CLIENT_USER_SETTING, C_SAVE_CLIENT_UI_SETTING, C_SAVE_CLIENT_CHAT_OPTION_SETTING and the two request-setting opcodes; frequencies/content differ. All records are in CLIENT-SETTINGS.

The other O-only C_ names are C_CANCEL_SKILL, C_DIALOG_EVENT, C_DUNGEON_UI_OPENED, C_NPCGUILD_LIST, C_PLAYER_LOCATION, C_REQUEST_REPUTATION_STORE_TELEPORT, C_REQUEST_USER_ITEMLEVEL_INFO, C_SHOW_ITEMLIST and C_START_SKILL. These reflect different play actions; no uniquely named matching-consent packet was captured.

## Interpretation, validation and remaining boundary

Start isolated live experiments with **isShowDungeonWorkUI**, **isNewCharacter**, then **login status**; each has a concrete bounded byte change. The F732 status/role rows are the clearest additional missing server-supplied matching data and should be tested together as a separate experiment, with live dungeon/role semantics preserved. Keep GFx field3/UI-row insertion as lower-confidence client-state experiments. A result identifies causality only after the same character/queue succeeds with the change and fails again when reverted; the two existing sessions alone cannot provide that proof.

The two literal UI matching windows carry **IsLocked**, not IsVisible: A022:7616; the actual writer A041:7300–7327 writes18-byte element headers then UTF-16 names. No source here establishes how the client defaults a missing window or what GFx protobuf field3 means. Client UI code or a controlled replay is needed to resolve those points. The local client has packaged S1Game.u/GPK assets, but no decoded UI implementation was used as evidence.

Validation: the .NET analysis helper ran the repository's DefinitionReader/Writer over every selected packet; manual linked-list decodes traversed the captured lists; fixed-offset candidates preserve length and change only their stated bytes; the GFx candidate decompresses with field3=1; UI insertion parses as five rows. **No production code changes, no runtime test, no popup claim.** Analysis helpers remain local under `obj/t184d`; raw evidence and generated tables stay ignored under `data/t184d`.

Capture SHA-256:

- R `617cbffe61b8e735c8dbc115f2e306ab723ed096b44839a8da52b1f55ee60d36`
- O `711eb2e94f488e3b10654197b7643afc98ad93e1c26beb2ae41aee75119cf8b7`
