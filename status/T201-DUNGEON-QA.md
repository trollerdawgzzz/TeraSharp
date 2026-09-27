# T201 dungeon and battleground QA commands

Implementation: `QaDungeonCommands.cs`, `QaDungeonInfo.cs`, `CharacterStore.QaDungeon.cs`; existing central operator authorization runs first. All paths below are decompile-derived unless a capture is named. No custom T138 rating behavior changes.

| Command | Native evidence | Implemented effect |
|---|---|---|
| reset_dungeon | Arb044:1134–1206; writer039:10042–10110 | Broadcast13B9 with current party or solo DungeonOwnerInfo; reuse capture-pinned reset builder. |
| set_dungeoncool id minutes | Arb044:3424–3505;046:2545 | Validate ContinentData; mutate process cooldown and send13B4 PDId/id/minutes to configured continent owner. Offline known owner does not fall back. |
| phaselevel continent level | Arb028:7595–7675;065:9819–9957; SQL19259–19290 | Persist24B phase row and reset epoch; currentWorld15E1. Login2869 now serves rows via286A instead of resetting them on every login. |
| clear_bf_cool | Arb072:11204–11373 | AllWorld1525 cooldown clear, then currentWorld1561 entry-count clear. Existing load state is empty. |
| set_bf_result | Arb044:2887–3070;071:8712–8784;045:3918–3967 | Seven required ints: template/wins/draws/losses/grade/field8/field10; optional kill/death/assist. Persist native counters and send13C9 to currentWorld. Native's six-argument out-of-bounds read is safely refused. |
| update_bf_score template grade | Arb044:7211–7323 | Native quirk: sets all eight statistic values to5 and the requested grade; currentWorld13C9. |
| get_bf_result_resettime | Arb071:14634–14693,16627–16709; SQL14520–14537 | Initial UTC season date is persisted once (native season type2). Getter emits native formatted custom message. |
| set_bf_result_resettime / set_dg_result_resettime | Arb071:18683–18732 /065:5612; setters072:2786–2808 /061:2841 | Centrally denied: moving these clocks can trigger realm-wide season/ranking resets. No unused runtime setter is advertised as implemented. |
| dungeon_log on/off | Arb044:7872–7989 | Broadcast14FE boolean; native custom confirmation. Also accepts1/0. |
| dungeon_onoff id on/off | Arb040:14174–14255;061:7753–7870;076:12999–13145 | Persist spUpdateDungeonOff equivalent; broadcast1580. Boot157E reflects saved disabled IDs; new applications reject them. Remove only disabled destination from current applications, preserving others; native S_DEL pool frame. |
| info_dungeon | Arb029:15616–16155;060:18249;001:5090 | Read stored52B cooldown rows plus DungeonData name/Constraint. Emit3566(seconds),3567(available),3568(entries); matching cooldown variant and phaseSave count behavior included. |
| reset_bf_result / reset_all_phaselevel | Arb044:905–929 /872–884 | Centrally denied: realm-wide ranking or persisted phase-progress wipe. |

Native BG result persistence is separate from `bg_rating`: login2895/2896 serves64B rows in native category order0–6 (Arb071:13822–13914). No change to the user's custom match rating formula. This adds QA setter persistence; it does not claim that every normal battle's aggregate season update is implemented.

Limits: automatic calendar phase reset is not implemented; only the existing first-observation reset and persistent per-user QA phase state are covered. `info_dungeon` uses sheet baseline durations/counts; active account-trait and hunting-event percentage modifiers are not reconstructed. The existing BG enter-count loads are empty, so there are no nonzero BG usage rows to produce3569. These diagnostics are not live-capture proof of those missing branches.

Tests: `T201_phase_and_native_BG_result_survive_restart_and_serve_native_loads`, `T201_dungeon_QA_commands_authorization_routing_and_native_state`, `T201_info_dungeon_native_seconds_counts_and_matching_duration`. These exercise central nonoperator refusal for every command, World0/13 routing, native layouts, malformed args, persisted phase/result/disabled state across store reopen, and sheet-derived diagnostics. Validation is run by the coordinator; no build or git mutation was performed by this worker.

## Timeline, competition and event controls

| Command | Native evidence | Effect |
|---|---|---|
| add/show/reset_timeline_dungeon | Arb040:5523–5603;061:2707;065:7359 | Process QA weekly intervals;32-bit linked30B1583 patches; day0 means all days,7 means Sunday. Show merges sheet and QA intervals; reset1582 keeps sheet baseline. |
| add/show/reset_timeline_eventmatching | Arb040:5607–5699;049:10060;050:7261 | Same interval mechanics, type1; native BattleField event rejection and exact Korean feedback. |
| sim_match_progress | Arb044:6227;077:8205,8930 | Ten-minute display override, no queue formation changes; invalid argument count clears it. |
| show_cand | Arb072:739–922;028:7515;041:14472 | F12D applicants with current map/guard/section location, not invented combat statistics. |
| update_pverank | Arb065:19031–19441;049:16343,19957; SQL20324 | Validate ContinentData, CompetitionDungeon and Leaderboards dungeon row/seasonOut. Persist native stage/time competition records separately from T167 points; higher stage then faster time wins. Send29B F247 with attempted/old values. |
| clear_pverank_player | Arb061:2191,11279 | Delete only caller/current season/continent competition result, no direct packet. |
| rank_sort | Arb079:12686 | Native flushes pending rank vectors. TeraSharp synchronously persists and queries these records; no pending vector or direct packet. |
| start_rookie_event | Arb044:2002–2095;037:13205–13340,14793;038:13486,14537,18426,18825; SQL5268–5320 | Four args continent/hours/item/amount. Persist schedule and target1 reward, reject overlapping same-continent events and invalid items. Native14EA start,14EB expiry,14E9 reconnect snapshot; World consumes reward data. |
| add_dungeon_abnormality | Arb044:703–783;004:887;008:14180;076:18081;077:8423,11447;070:15411,15835; SQL4388–4430 | Parse UTC YYYYMMDDHHMM, allocate fresh native-style event identity, persist reservation. Start1588(type14,false) then161A(continent,action1,abnormality); end1588(true) then161B and delete. Replay active records to reconnected World. Duplicate abnormality values have independent identities, as native SQL permits. |

Competition command records are used for eligible dungeon ranking reads; existing points-based records remain separate. Native automatic season rollover is not implemented and its destructive QA controls are denied. Other denied global ranking controls: clear_all_pverank, clear_all_pvprank, clear_pverank, init_battlefield_season, init_dungeon_season, makeuser_pverank, makeuser_pvprank, rank_next_season. The external `match` command requires MatchServer AM_COMMAND transport, absent from TeraSharp; it is explicitly classified with that dependency.

Event timers run at one-second intervals through the human-owned WorldBridge patch; command-triggered due abnormality starts are immediate. Events and reward rows survive store reopen. None of these event branches has a captured QA pair in the indexed capture set; tests are marked decompile-derived and check explicit native layouts, authorization, scheduling, overlap, expiry and restart. Tests: T201TimelineCompetition.cs and T201DungeonEvents.cs.

## Achievement season and festival controls

`change_achievement_season id [true]` reads the maximum Season ID from AchievementGradeInfo.xml (Arb006:7169–7294; inactive rows still contribute). Native Arb040:7065–7156 permits only an advance; an existing/older season prints the exact native text and refreshes caller via broadcast150F. An advance updates process state, persists only when optional argument equals true, broadcasts1510 current then1511 linked20B season/start rows, and broadcasts150F after3seconds. The native builders are Arb045:18883/19646 and046:13147; SQL18951 is the optional persistence operation. Login/bootstrap uses the managed state once present. This does not implement arbitrary future season scheduling from external web administration.

`start_festival`, `stop_festival`, `reserve_festival` use WorldFestival*.xml EventObject IDs and broadcastToWorldServer (defaultfalse). Native149A[eventId,UnixStart] and149B[eventId] go only to World0 unless that flag is true. Schedules persist, reject inclusive overlap for the same event, start/expire on the native10second timer and replay active events after World reconnect. A DailyEvent child enables/disables the shared daily manager; `dailyevent 0..7` enables an explicit process day override,156B broadcasts only when the effective day changes. Automatic days respect DailyEvent.xml local-time resetHour. Native evidence: Arb044:789,3396,6266,6469;061:588;065:4108,5046,14552,15173,19476;066:17353;072:17476,17756–17858.

Festival delivery covers World event activation and DailyEvent state. Separate Arbiter LevelEvent reward-mail/account-claim machinery remains absent; this patch does not claim those rewards are implemented merely because149A is sent. Tests: T201AchievementSeason.cs and T201Festival.cs, decompile-derived.

## Commands with a missing native subsystem

These commands are explicitly classified by their actual dependency, with no success response or unrelated-table mutation:

| Commands | Missing subsystem and evidence |
|---|---|
| tutorial / tutorialPlayer | Account tutorial eligibility and character TutorialPlaying lifecycle. Arb061:14378 DoIMustPlayTutorial, PA lobby046:4885, character persistence030:5492. Existing simple tutorial tips are distinct. |
| set_play_limit | AuthManager admission waiting queue;044:4639 persists capacity,058:10645 drains queue and sends S_WAITING_LIST. It is not a playtime limit. |
| clear_level_reward | Account LevelEventRewardInfo claim/grant manager,061:13747/SQL12049; unrelated to achievement rewards. |
| reset_attendance_event_reward | AccountAttendanceEventCompensation event mask and reward lifecycle,065:7291; unrelated to T168 character daily attendance bitmap. |
| setplaytime / getplaytime / resetplaytimereward | Active DB/web PlayTimeEvent definition, promotion-time and claim/reward manager.055:17347/17380;054:11647/12328;056:148/217;SQL5965–6095. General account.play_time_sec is a different counter. |
| load_saevent / resettime_saevent | StackAttendanceEvent event/participant/progress/reward lifecycle. Assets are present; live SampleEventList is not missing.043:16859→049:8071; reset049:18215→050:11621 sendsC508 only with active event. Existing empty74EA does not supply this lifecycle. |

Global deletion commands reset_admin_reward (SQL8848–8849), clear_achievement_season (SQL18950), clear_serverachievement/clear_all_serverachievement (Arb040:8583/8448), and clear_saevent (050:8038 deletes events and participant progress) are centrally policy-denied.

## T201 utility commands and exact unavailable event dependencies

`QaUtilityCommands` adds eleven operator-gated commands. None of these QA branches has a captured command/reply pair in the indexed T201 command captures; the tests below are explicitly decompile-marked.

| Command | Native behavior implemented | Evidence |
|---|---|---|
| `escape <name>` | Save target location: continent1, channel0, XYZ float bits47A92D80/C6AEEE00/44A20000. Native does not send an immediate teleport. | Arb040:14395; Arb030:5288–5337 |
| `sticktogether <name>` | Offline target saves caller's location. OP command sends13B1 to target's current World and `Summoned [name]`; QA command calls the existing type2 handoff with source1392, the target's live GameId/EtcData and caller's location. | Arb033:6533–6620; Arb031:7899; Arb044:6400–6465; Arb030:6925–6989 |
| `clear_vote_cool` | Broadcast1528[userDbId] to every registered World. TeraSharp has no separate Arbiter vote cooldown cache to clear. | Arb028:690–759 |
| `pk_section <continent> <areaName> <section> [on]` | Persist non-PK tuple and broadcast14E0[stringRef19,continent,section,nonPk,UTF16 area]. Optional `on`/`1` removes the restriction; otherwise adds it. | Arb043:19714–19894; Arb045:4135; Arb047:4294/19194; SQL13457/13482/13563 |
| `help [substring]` | Search native bucket3 name, argument help and description; exclude names beginning `_`; send the exact heading and `%s %s // %s &#xa;` rows, then current-World `_helpworld`. Compiled metadata comes from the full native registration inventory. | Arb044:2099; Arb007:3688–3748; Arb005:13240–13258 |
| `devdebug ...` | `ping`: current-World1389[user,tick]. Other ordinary text: client8C6F[ref6,text]. `connection` and `set_play_limit` report the missing AuthManager admission subsystem. | Arb031:18796–19068 and7975–8015 |
| `i_want_server_language_and_revision` | Native custom-message format using the same6/376056 values as current S_SERVER_BUILD_INFO. | Arb032:502–545; obj/t201/language-revision.asm |
| `change_loading_screen_status <integer>` | Persist nonzero/zero state, broadcast clientC588, and load the saved flag on next login. | Arb044:3948; Arb067:14633; Arb071:9968; Arb079:10094; SQL10008/10100 |
| `unlock_all_movies` | Account-scoped watched-movie ledger receives every ReplayMovie.xml Movie id; existing watched list serves it. Native custom message is `Unlock all moives.` | Arb044:7125; Arb064:16757 |
| `reset_charsock` | Reject accounts with4+ characters; otherwise reset account capacity to default AccountTrait package0 `expandCharacterSlot.slot` (3 in deployed sheet). Login user list, create and undelete checks consume persisted capacity. | Arb044:1074; Arb058:9682–9750 task4 resets expansion, not capacity4; Arb065:18582; Arb008:14584; Arb045:6300 |
| `gmevent_notice <text>` | Current-World1611[ref10,UTF16 text]. QA parser appends a space after each argument. Panel path passes its original text. Notice has no event-running gate. | Arb040:15999; Arb049:10198; Arb048:18514 |

Limitations and deliberate protocol handling:

- Caller position for `sticktogether` is the latest saved World blob (220/224/228 XYZ,236 continent,240 channel,304 direction), not the immutable login selection. It can lag a movement not yet saved by World. The handoff retains the target's live World GameId and EtcData. It does not forge an incoming SA_TELEPORT.
- Native14E1 reconnect serializes raw48-byte `std::wstring` triples (Arb046:18039, Arb047:2508); World2997143/37380 dereferences the remote pointer when the area name exceeds7 characters. World1764862 only adds rows: an empty14E1 does not clear. Reconnection safely reconstructs saved restrictions through the same native14E0 add consumer. This is an explicit wire difference from the defective native pointer representation.
- GM notices preserve wire ownership/text; the native publisher-specific word filter is not implemented.
- `devdebug` supports its ordinary non-publisher-specific branch. Its AuthManager-only subcommands explicitly report the unavailable counters/queue; no invented numeric counters are sent.
- `qatest` requires a coherent QA profile across WorldParameter, GuildWar, BattleChip and FloatingCastle managers plus the external MatchServer AM_COMMAND transport. Native changes material sheet values (GuildWar cooldown72000→300, BattleChip minimum30→2, etc.); merely forwarding1473 `qatest_world on/off` is not a complete implementation. Classified with this exact absent dependency.
- `connection` requires AuthManager MaxActive/Active/Play counters. TeraSharp's in-world session count cannot replace all three; the command reports that absent dependency.
- `load_gmevent`/`gmevent_change_maxjoincount` require GmEventManager's persisted current event, participants and rewards. The XML assets exist; absence is the lifecycle, not the sheets. Native load sends1604; changing maximum sends to the active event's owning World (Arb043:16623→050:2560; Arb040:15958→049:10621).
- `load_saevent`/`resettime_saevent` require the StackAttendanceEventManager activation/progress/reward lifecycle. StackAttendanceEvent.xml has valid2016–2050 sample entries, but the existing74EA empty-list builder has no such manager. Reset sends clientC508[epoch] only for an active event. `clear_saevent` is centrally denied because it deletes all event and participant progress (Arb043:16859;049:8071/18215;050:8038/12178).

Tests: `T201_utility_commands_gate_operators_and_route_native_notice_debug_vote_help`, `T201_utility_persistent_movies_slots_loading_and_non_pk_have_consumers`, `T201_escape_is_saved_location_and_sticktogether_uses_distinct_OP_QA_paths`. Auth checks go through central C_ADMIN/C_OP_COMMAND dispatch; positive cases exercise sockets, persistence, existing slot/watch consumers and real cross-World state. Human seams are recorded in `obj/t201/utility-human-seams.txt` and incorporated by the coordinating patch owner into `status/T201-PATCH.diff`.

## T201 guild ranking calculation

`calc_guild_level_ranking` now invokes the native forced calculation instead of leaving the command without a consumer. Native Arb040:6316 calls GuildManager::CalcGuildLevelRanking(true), Arb069:639–975. It filters by GuildConfig.xml `GuildRanking.minAccountNum` (distinct accounts, not characters), then sorts experience descending, account count descending, and creation date ascending (Arb067:19251 comparator). All eligible rows are persisted, including previous rank and experience; only the top100 are published. SQL17364–17385 proves the full durable snapshot.

`GuildBoard.Ranking` now serves that snapshot; before the first calculation it is empty, matching all four T95 retail records. The DFA0 consumer uses snapshot rank, previous rank, account count and level, with live name, chief, preference and creation date (Arb070:11644–11810). The earlier T95 test's live-name sort was an unsupported assumption and is replaced with the captured empty-before-calculation behavior. New tests cover the native sort keys, duplicate-account exclusion, client offsets, stale-until-recalculated state, restart, and previous rank for a guild entering the top100 from rank105.

Limitations: the QA command is the explicit refresh trigger; the native daily GuildManager refresh timer is not added by this bounded command change. Floating-castle occupancy remains false because this build has no occupation manager. Exactly equal native sort keys have no guaranteed stable order; this implementation preserves guild-ID input order.

Loading-screen utility clarification: updates reach current in-world sessions and the caller. TeraSharp exposes no complete lobby-session registry; clients already waiting in the lobby receive the new persisted flag on their next login, rather than an immediate broadcast. This is a precise limitation of `change_loading_screen_status`, not an all-account broadcast claim.

## T201 temporary awakening data overlays

`addawakenchange`, `addawakenenchant`, `deleteawakenchange` and `deleteawakenenchant` now operate on durable native overlay reservations. Change requires exactly3 arguments; enchant requires exactly6 and reorders the argument fields to `[enchantStep,combatRank,combatType,scrollTID,scrollCount,materialCount]`. Item existence, positive material counts, combat types1/3/4/5 and inclusive overlap checks follow Arb081:16139, Arb083:2933. QA additions start now and end one day later, with separate SQL identity sequences for change/enchant (SQL5217–5247).

Active change pushes clientD319 (20-byte linked entries) and World0 AS15D5 (24-byte entries); enchant pushes client9E6F (32-byte entries) and World0 AS15D4 (36-byte entries). Deletion/expiry sends clientE86A/5621 and World0 AS15D7/15D6. Pending deletion removes SQL only. A60-second timer activates and expires reservations, with reconnect replay only to World0, matching Arb082:3354/3501,13737,16149,18553/18656,18994/19168 and the60000ms constructor at Arb081:13546. These World0 destinations are native explicit owner choices, not residual default-link routing bugs.

ContentsOnOff type11/id0 gates client packets but not World's overlay. Native absent state is enabled (Arb069:11391); TeraSharp currently has no general ContentsOnOff controller, so this uses that proven default and keeps an internal gate seam tested separately. No client login push is invented: every native D319/9E6F writer belongs to an update/broadcast path, no login callback was found, and cap_2man_client1/cap_final2b_client1 have neither opcode. World reconnect replay is proven; the retail mechanism for redisplaying an already-active overlay after client relog remains unverified.

Tests: `T201_awaken_QA_add_delete_validation_and_expiry_use_native_client_and_World0_packets` and `T201_awaken_pending_restart_reconnect_and_client_gate_preserve_native_lifecycle`. The former pins complete decompile-derived change/enchant/add/delete frames and checks every command's operator gate. The latter proves pending/active distinction, restart, inclusive overlap, World0 scope and client-only suppression. No captured awaken QA command pair is claimed.

## addstyleshop — persistent product preview, sale and expiry

Implemented in `QaStyleShopCommands` / `CharacterStore.QaStyleShop`. Native QA handler Arb040:5187 accepts an item ID; exactly six arguments select `item saleStartYYYYMMDDHHMM saleEnd previewStart label price`. Every other nonempty argument count uses now/one day/now, `test by QA`, price10. Native valid `combatItemType` values6/14/79–82 resolve to EQUIP_UNDERWEAR, SKILLBOOK and the four EQUIP_STYLE_* types; the executable table proof is `data/t201/native-proof/styleshop-combat-item-types.json` (Arb009:13306,004:1983,008:14303). No guessed category/subtype mapping.

The command requires operator authorization, an existing accepted item, label shorter than16 UTF16 units and ordered sale/mark dates. SaleType2 permits nonpositive prices. `ValidateInsertProductList` (Arb083:3391) rejects duplicate/date/label failures before `AdminInsertProductMark` increments the runtime event ID (081:18101); rejected QA applications therefore do not consume IDs. SQL stores all twelve native ProductMarkInfo columns (GameDatabaseDefinition.xml:3373/6242), and startup restores current visibility and maximum remaining event ID (082:1666–1814). Insertion emits only native custom success text, not an immediate product push.

The native30-second timer (083:1444;082:13332) advances hidden→preview→sale and expires/deletes products. World `15B9` contains stringRef/remove/eventId/itemId/markType/saleStart64/discount/price/preview/saleType/UTF16 label. It broadcasts to **every ready World**, including dungeon Worlds: Arb081:19526 calls WorldSessionManager::Broadcast at045:18750. Connected clients receive `AB44` with preview label only during preview, then an empty label during sale; expiry emits `84B1[itemId]` (081:19790/19022). Expiry's World removal retains event/item and clears the used fields. Native delete leaves the unused saleType field uninitialized; TeraSharp writes deterministic zero there and does not claim byte-exact native stack garbage.

Reconnect sends `15B8` to the **requesting World**, containing visible persisted rows as45-byte linked elements plus UTF16 labels (081:15771/19189,082:14116). Count is bounded to native1000 plus the transport frame-length limit. There is no invented Arbiter login push: World's ContractStyleShop::BeginningContract builds the client list from its mirrored ProductEventManager (WorldServer.exe.c:1223946). QA style types cannot select native product category1/2, so their ContentsOnOff22/23 gate is inapplicable here. MarkOnTick treats any1970-01-01 preview date as empty (Arb000:18859), whereas the wire writers compare timestamps directly; tests preserve this native distinction.

`T201StyleShop.cs` covers operator refusal, default and optional syntax, invalid and duplicate input, persisted fields,30-second scheduling, literal decompile-derived preview/sale/removal packets on both World0 and13, client broadcasts, SQL restart, per-link snapshot replay and variable-length list references. No matching live QA capture is claimed; native citations and the executable enum proof are the evidence. Existing52-command metadata was audited for arguments, action, emitted frames, citations and limitations. The party command test additionally invokes the actual authorized sim_match_progress set/clear dispatch and verifies its live progress consumer.

Final reconnect review: item-period creation/deletion now derives a persisted row's active stage when the runtime timer has not seen it yet. It does not seed the broadcast ledger, so the next native timer still informs other Worlds; newly inserted QA rows retain pending stage until that tick. Regression: reopen→14C3 replay→immediate op8 expiry calculation→delete14C4 before first tick. Rookie14E9 replay now remembers advertised IDs separately from started IDs; a first tick at expiry sends14EB even when no14EA occurred in this process. Its regression covers reconnect one second before expiry, SQL deletion and no repeated end. Human patch defaults reviewed unchanged without QA overrides: bypass on, leave5 seconds, loading custom false, existing character-slot capacity, zero deco UI and untouched unknown enter flags.
