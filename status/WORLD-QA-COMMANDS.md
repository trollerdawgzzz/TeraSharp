# WORLD-QA-COMMANDS - WorldServer's QA command table (T233)

545 commands. This is WorldServer's own `/@` table, not the Arbiter's. The Arbiter forwards
an unrecognised `/@` name to World; World looks it up here, logs `QA Command: <name>` on a hit
and `[QA] Invalid Command: <name>` + "Invalid QA Command" to the caller on a miss.

## 1. Provenance

| | |
|---|---|
| Registrar | `FUN_140114090(registry, int type, const wchar_t* name, handler, int flag, const wchar_t* args, const wchar_t* usage)` |
| Source | every call to it in `world_decompiled/WorldServer.exe.c` - 545 |
| Dispatcher | `CommandDistributor::ProcessCommand`, keyed at `+0x48`; miss goes to `WorldCommandDistributor::OnUnregisteredCommand` |
| Miss log | `[%s] Invalid Command: %s`, label `OP` for type 0 and `QA` otherwise |
| String fixup | the decompile emits bare `undefined DAT_...`; 7 names, 231 arg strings and 48 usage strings were read from `Executable/WorldServer.exe` (image base `0x140000000`, RVA via section table, UTF-16LE) |
| No-arg sentinel | `&PTR_1419c2970` - its target is eight zero bytes, 231 occurrences. An empty **Args** below means that sentinel, not a failed resolve. |

Usage text is retail's own Korean, verbatim. English in this file is a gloss and marked `OURS:`.

## 2. Shape of the table

| Field | Values |
|---|---|
| `type` | `4` on 530 (the bulk QA set), `1` on 15 (battlefield + cheat set). Type `0` ("OP") is never registered on this build. |
| `flag` | `0` on 534, `1` on 11 (`idle_social_stop`, `flyspeed`, `showpos`, `playtimereward`, `profile_world`, `reset_profile_world`, `reload_datasheet_world`, `reload_huntingzone`, `reload_item`, `reload_dungeon`, `reload_flying`). Meaning not pinned. |
| duplicates | `bookmark` only - registered under both type 1 and type 4. |
| non-ASCII name | `부활` ("revive in place"). |

## 3. Absent on this build

`_helpworld`, `helpworld`, `help`, `_help` - **none** are registered. T201's `/@help` forwarded
`_helpworld <term>` to World for its second half; that could only ever come back as "Invalid QA
Command". T233 removes the forward and serves World's half from `WorldQaCommandData` instead.

Also absent, both asked about: `perfect_crest` and `perfect_skill`. `perfect_skill` turns up in
a search only as the prefix of `perfect_skillPolishing`, which is a different thing (skill
polishing, not skill learning). And there is no `glyph` command at all - **crest** is the glyph
family on this build; `perfect_level` is the one that learns skills.

## 4. The glyph/crest and learn-skills commands

| Command | Args | Usage (retail) | OURS: gloss |
|---|---|---|---|
| `crest_all` | - | 문장 모두 습득 | learn every glyph |
| `crest` | `[문장포인트]` | 문장 적용 해제 및 문장 포인트 적용. 인자 없으면 문장 적용 해제만 | unequip all glyphs; with an argument also set glyph points |
| `clear_all_crest` | `[문장포인트]` | (same text as `crest`) | same, separate registration |
| `crest_window` | - | 문장 창 열기 | open the glyph window |
| `perfect_level` | `새로운레벨` | 레벨 변경+스킬습득 | **set level AND learn that level's skills** - this is the learn-all-skills command |
| `perfect_skillPolishing` | - | 모든 스킬 연마 활성화 | enable all skill polishing |
| `learn_skill` | `skillTemplateId isActive` | - | learn one skill by template id |
| `reload_userskill` | - | 유저 스킬 다시 읽기 | re-read the user skill sheets |

`/@perfect_level 70` is what T228(B) was reaching for - level change and skill grant in one.

## 5. Full table

`T` = type. Args/Usage verbatim; empty Args = the no-argument sentinel.

| Command | T | Args | Usage |
|---|---|---|---|
| `abnormality` | 4 | 이상상태아이디 | 이상상태 강제 |
| `abnormality_off` | 4 | 이상상태아이디 | 이상상태 강제 |
| `abnormality_show` | 4 | 이상상태아이디 | 이상상태 강제 |
| `abnormality_time` | 4 | [이상상태Id] [duration] | duration 지정해서 이상상태 강제 |
| `accept_revival` | 4 |  | 부활 받았을 경우 부활 |
| `accomplish_achievement_rate` | 4 |  | 현재 시즌 달성률 넘을 때까지 업적 달성시키기 |
| `add_actpoint` | 4 | [actPoint값] | 모험의 주화 변경 |
| `add_battlecoin` | 4 | [금화수량] | 신규모드 전장 내 금화 획득 |
| `add_battlepass_exp` | 4 | [Amount] | 배틀패스 경험치 획득 |
| `add_battlepass_token` | 4 | [Amount] | 배틀패스 토큰 획득 |
| `add_bf_score` | 4 |  | 전장 거점 점령점수 가산 [점수] |
| `add_daily_clear_count` | 4 | [addBonusCount] | 발키온 지령서 일일 보너스 퀘스트 완료 횟수 증가 |
| `add_epexp` | 4 | [exp] | EP Exp 변경 |
| `add_exp` | 4 | [EXP값] | 경험치 더하기 |
| `add_hp` | 4 | [HP값] | HP 값 조정 |
| `add_mat_enchant` | 4 | [templateId] [enchantStep] [multiplier]  | 재료 강화 아이템의 재료 획득 |
| `add_mat_upgrade` | 4 | [templateId] [enchantStep] [masterpiece] [awakened] [multiplier] | 장비 승급 아이템의 재료 획득 |
| `add_maxactpoint` | 4 | [maxActPoint값] | 모험의 주화 max값 변경 |
| `add_mp` | 4 | [MP값] | MP 값 조정 |
| `add_npcguild` | 4 | [npcGuildId] | 평판 세력 등장 |
| `add_partner` | 4 | [파트너등록증templateId] | 파트너 생성 후 소환 |
| `add_partner_grade` | 4 |  | [버프 등급] |
| `add_partner_level` | 4 |  | [레벨] |
| `add_pet_exp` | 4 |  | [경험치] |
| `add_pet_level` | 4 |  | [레벨] |
| `add_route_epexp` | 4 | [route] [exp] | 특성 경험치 획득루트 지정해서 변경 |
| `add_servant_ad_time` | 4 | [필드 아이디] [더할 시간(분)] | 서번트 모험 시간 변경 |
| `add_servant_energy` | 4 | [얼만큼] | 펫 에너지 증감 |
| `add_skill_polishing_exp` | 4 | [exp] | skill polishing exp 변경 |
| `add_st` | 4 | [ST값] | ST 값 조정 |
| `addpoint_gmevent` | 4 | point | GM이벤트 추가 |
| `adjust_bonfire` | 4 | [시간] | 유저가 만든 모닥불 남은 시간 변경 |
| `aibehavior` | 4 | [on 거리] or [off] | 주위 Npc의 Ai 정보를 수집 |
| `aiskill` | 4 | [on 거리] or [off] | 주위 Npc의 스킬 정보를 수집 |
| `allabnormality` | 4 | [on/off] | 스킬에 의해 발생하는 이상상태 무조건 적용 |
| `allhit` | 4 | [on/off] | 스킬 적관계 무시하고 무조건 적중 |
| `allow_teleport` | 4 | on/off | 텔레포트 제한구역에서의 텔레포트 허용 여부 |
| `allreaction` | 4 | [0/1/2] | 스킬 적중시 무조건 리액션 발생 (0없음, 1미니, 2기본) |
| `angry` | 4 | [range] | 분노모드로 바꾸기 |
| `aoi` | 4 |  | 주위 world object 개수 |
| `apm_add` | 4 | [id] | 자동 파티 매칭 풀 진입 |
| `apm_ask` | 4 |  | 자동 파티 매칭 허용 팝업 띄움 |
| `apm_reset` | 4 |  | 자동 파티 매칭 허용 상태 초기화 |
| `apm_use` | 4 | [on/off] | 자동 파티 매칭 사용 모드 변경 |
| `assert` | 4 |  | 어서트정보 테스트 |
| `atk_speed` | 4 | [공속비율] | 비율대로 모든 스킬의 속도가 증가 |
| `attendance` | 4 | [YYYY] [MM] [DD] | 해당 날짜에 출석체크 |
| `autorevivaltime` | 4 | [분] | 자동 부활 시간 조절하기 |
| `ban_guild` | 4 | 캐릭명 | 길드원 추방 |
| `bf_chat` | 1 |  |  |
| `bf_end` | 1 |  |  |
| `bf_goto` | 1 |  |  |
| `bf_Invite` | 1 |  |  |
| `bf_kick` | 1 |  |  |
| `bf_leave` | 1 |  |  |
| `bf_match` | 1 |  |  |
| `block_load_topo_progress` | 4 | [max_percent] | 신규모드 진입할 때 topo 로딩이 이루어지고 있지 않은 것으로 간주 |
| `block_quest` | 4 | [questId] | 퀘스트를 시작할 수 없게 막기 |
| `bookmark` | 1 |  |  |
| `bookmark` | 4 | bookmark_id | 북마크(서버) |
| `box_prob` | 4 | tid | 가차 아이템 확률 정보 보기 |
| `breakshield` | 4 | [skillId] |  |
| `buy_styleshop_item` | 4 | [itemTid] | 스타일샵 아이템 구매 (TeraPuppet 이용 추천) |
| `calc_battle_field_ranking_next_time` | 4 |  | 다음 번 서버 시작시 전장 랭킹 생성 |
| `calc_battle_field_Season_next_time` | 4 |  | 다음 번 서버 시작시 전장 시즌 생성 |
| `calmdown` | 4 | [range] | 분노모드 해제 |
| `cancelaction` | 4 | [다음 액션번호] | 액션 강제 해제 |
| `card_collection_max_cost` | 4 | [maxCost] | 카드 도감 최대 코스트 설정 |
| `change_battlepass_level` | 4 | [Level] | 배틀패스 레벨 변경 |
| `change_daily_quest_record_reset_hour` | 4 | [id] | 일퀘 클리어 시간 변경 |
| `change_daily_quest_seed_reset_hour` | 4 | [id] | 일퀘 시드 재생성 시간 변경 |
| `change_epresettime` | 4 | changeResetDay | EpReset 날짜 변경 |
| `change_eventgage` | 4 | [eventName] [value:0 ~ max] | 던전 이벤트게이지 값 변경 |
| `change_exp_gain_data` | 4 | [on/off] or [level, ratio, on/off] | 월드 경험치 획득량 조절 명령어 |
| `change_guildchief` | 4 | 캐릭명 | 길드장 위임 |
| `change_interval_event_system_playtime` | 4 | [incSeconds] | 누적플레이타임 증가 인터벌 변경 (60: 1시간, 1: 1분) |
| `change_npc_ai` | 4 | [range] [huntingZoneId] [npcid] [patternListId] | NpcAi 조정하기 |
| `change_npc_stat` | 4 | [range] [ratio] [type] | NpcStat 비율 조정하기 |
| `change_npc_to_user` | 4 |  | 유저 변신모드 해제 |
| `change_tba_time_attack` | 4 | [남은시간] | 신규모드 전장 내 남은 시간 변경 |
| `change_user` | 4 | [CLASS] [RACE] [GENDER] | 클래스 종족 성별 바꾸기 |
| `change_user_status` | 4 | [battle/peace] | 평화/전투 상태 변경 |
| `change_user_to_npc` | 4 | [huntingZoneId] [templateId] | 유저 변신모드 |
| `change_worldspawn_lifetime` | 4 | [WorldSpawnId] [SpawnTerritoryGroupId] [lifeTime] | WorldSpawnData.xml에서 id가 WorldSpawnId인 WorldSpawn의 하위 노드 중 id가 SpawnTerritoryGroupId인 SpawnTerritoryGroup에 속한 SpawnTerritory들의 lifeTime을 수정한다. |
| `changefatiguepoint` | 4 |  | 생산력 강제 셋팅 |
| `changenpc` | 4 | [사냥터id] [NpcInstanceId] | Npc 교체 스폰 |
| `changenpcangermode` | 4 | [on/off] | 분노모드 on/off |
| `charin` | 4 | 캐릭이름 | 캐릭터 로그인 테스트 |
| `charm` | 4 | 부적아이디 | 부적 추가 강제 |
| `charout` | 4 | 캐릭이름 | 캐릭터 로그아웃 테스트 |
| `check_guildname` | 4 | 길드이름 | 길드명 중복체크 |
| `checkcell` | 4 |  | 셀 확인 (서버용) |
| `cl_reset` | 4 |  | 전투 로그 툴 리셋 |
| `clear_achievement` | 4 |  | 업적 초기화 |
| `clear_aggro` | 4 |  | 어그로 삭제 |
| `clear_all_achievement` | 4 |  | 모든 업적 초기화 |
| `clear_all_crest` | 4 | [문장포인트] | 문장 적용 해제 및 문장 포인트 적용. 인자 없으면 문장 적용 해제만 |
| `clear_all_social` | 4 |  | 습득한 모든 Social 삭제 |
| `clear_bonfire` | 4 | [거리] | 유저가 만든 모닥불 삭제 |
| `clear_build_object` | 4 | [거리] | 유저가 만든 빌드 오브젝트 삭제 |
| `clear_cont` | 4 | 채널 청소 | 채널 청소 |
| `clear_daily_quest_complete` | 4 |  | 일일퀘스트 완료 기록 클리어 |
| `clear_drevent` | 4 |  | 미숙련 던전매칭 이벤트를 데이터시트로 추가 |
| `clear_dungeonwork` | 4 |  | 던전 과제 초기화 |
| `clear_guild_skill` | 4 |  | 습득한 모든 길드 스킬 삭제 |
| `clear_quest` | 4 | [questId] [all] | 퀘스트 DB클리어하기 |
| `clear_recipe_world` | 4 |  | worldServer에서 RECIPE 삭제 |
| `clear_seren_guide` | 4 |  |  |
| `clear_withdrawer` | 4 |  | 겁쟁이 상태 해제 |
| `clearallabnormality` | 4 |  | 버프 다 지우기(유료화버프도 비활성화 시키지 않고 다 지운다. |
| `clearbuff` | 4 |  | 버프류 모두 삭제 |
| `clearcool` | 4 |  | 스킬쿨타임 모두 초기화 |
| `clearfieldpoint` | 4 |  | 필드점수 초기화 |
| `clearfp` | 4 |  | 필드점수 초기화 |
| `clearware` | 4 | 창고청소 | 창고청소 |
| `combatlog` | 4 | [on/off] | 전투 로그 활성화 |
| `combatwork` | 4 | [combat Work Id] | 선택된 대상 Npc의 combat 상태 work ID 를 강제로 실행 |
| `complete_all_quest` | 4 |  | 모든 자동 수주 퀘스트 완료 |
| `crash` | 4 | [파일명] | 크래시 |
| `create_channel` | 4 | 월드 | 채널 생성 |
| `crest` | 4 | [문장포인트] | 문장 적용 해제 및 문장 포인트 적용. 인자 없으면 문장 적용 해제만 |
| `crest_all` | 4 |  | 문장 모두 습득 |
| `crest_window` | 4 |  | 문장 창 열기 |
| `critical` | 4 | on/off | 크리티컬 on/off |
| `cube_prob` | 4 | tid | 큐브 아이템 확률 정보 보기 |
| `damage` | 4 |  | 주위 몬스터에게 데미지 주기 |
| `debugsend` | 4 | [on/off] | 디버그 정보 보내기 |
| `despawn_tbagroup` | 4 | [테레토리 ID] | 신규모드 전장 내 테레토리 디스폰 |
| `despawn_territory` | 4 | [사냥터id] [TerritoryId] [채널id] | 테리토리안에 있는거 전부 디스폰 |
| `despawn_worldspawn` | 4 | [WorldSpawnId] | WorldSpawnData.xml에서 id가 WorldSpawnId인 WorldSpawn에 속한 모든 스폰 테리토리를 디스폰시키고, 해당 월드스폰은 서버가 재실행했을 때처럼 처음부터 동작한다. |
| `despawnalleventnpc` | 4 |  | 모든 event npc 디스폰 |
| `despawnallnpc` | 4 |  | 모든 npc 디스폰 |
| `destroy_channel` | 4 | 월드 채널인스턴스 | 채널 삭제 |
| `destroy_guild` | 4 |  | 길드 해체 |
| `disable_citywar_enter_limit` | 4 |  | 길드 대전 입장 제한 조건 해제(무조건 입장 가능) |
| `disable_license` | 4 | [라이센스 이름] | 라이센스 해제 |
| `disconn_all_arbiter` | 4 |  | 모든 아비터 서버 세션 끊어버리기 |
| `dont_ban_battle_field` | 4 |  | 베틀 필드에서 팅김 방지 |
| `dr_available_count` | 4 |  | 검은 틈 퀘스트 수주량 조절 |
| `dr_balancing_usercount` | 4 | [userCount] | 현 위치의 검은 틈의 BalancingUserCount 수정 |
| `dr_close` | 4 | [hzid] [dr_tid] | 검은 틈 닫기 |
| `dr_expand` | 4 |  | 현 위치의 검은 틈을 강제로 확장 |
| `dr_fold` | 4 |  | 현 위치의 검은 틈을 강제로 수축 |
| `dr_goto` | 4 | [hzid] [dr_tid] | 검은 틈으로 이동 |
| `dr_here` | 4 |  | 검은 틈을 유저 위치에 소환 |
| `dr_inspect` | 4 |  | 현재 위치의 검은 틈 정보 출력 |
| `dr_list_reserve` | 4 |  | 예약된 검은 틈 EventGroup 정보 조회 |
| `dr_open` | 4 | [hzid] [dr_tid] | 검은 틈 열기 |
| `dr_open_group` | 4 | [egid] | 검은 틈 EventGroup 을 강제로 열기 |
| `dr_report` | 4 |  | 열려있는 검은 틈 전체 정보 출력 |
| `dr_rereserve_group` | 4 |  | 검은 틈 EventGroup 예약 정보를 초기화하고 다시 예약하기 |
| `dr_rightnow` | 4 |  | 모든 검은 틈의 Countdown 을 무시하고 바로 시작 |
| `dr_set_abnormality` | 4 | [abnormalityId] | 모든 검은 틈의 이상상태를 고정 |
| `dr_set_npcgroup` | 4 | [step] [groupTemplateId] | 모든 검은 틈의 n 단계 Spawn Npc 고정 |
| `dr_set_questtemplate` | 4 | [id1] [id2] [id3] | 모든 검은 틈의 퀘스트 TemplateId 를 고정 |
| `dr_spawnreport` | 4 |  | 검은 틈 소환 대기 목록을 출력 |
| `dr_start_quest` | 4 |  | 검은 틈 퀘스트 시작 |
| `dr_step` | 4 | [step] | 검은 틈 단계를 넘김 |
| `dr_time` | 4 | [sec] | 검은 틈 디펜스 시간 조정 |
| `dr_user_count` | 4 |  | 검은 틈 퀘스트 진행인원 변경 |
| `drop_all_items` | 4 | [on/off] | npc를 죽이면 모든 아이템 드롭 |
| `dropitem` | 4 | template_id amount | 아이템 드랍 시키기 |
| `dump_performance_data` | 4 |  |  |
| `dungeon_flag` | 4 | [flag-name] | 던전 플래그를 설정 |
| `dungeon_timer` | 4 | [timer-value] | 던전 타이머를 설정 |
| `enable_citywar_enter_limit` | 4 |  | 길드ㅐ전 입장 제한 조건 사용 |
| `enable_license` | 4 | [라이센스 이름] | 라이센스 취득 |
| `enable_ride_vehicle` | 4 | [on/off] | 탑승병기 무조건 탑승 가능 |
| `enchantitemsuccess` | 4 | [-1 : 항상 실패, 0 : 기본 상태, 1 : 항상 성공] | 아이템 강화할때 항상 성공되게 할건지 |
| `end_citywar` | 4 |  | 공방전 종료 |
| `end_gmevent` | 4 | gmEventId channelId | GM이벤트 종료 (보상 지급) |
| `end_npc_arena` | 4 |  | Npc투기장 종료 |
| `endfe` | 4 | [contId(all=전체)] [eventId(0=cont전체)] | 필드이벤트 종료 |
| `endfieldevent` | 4 | [contId(all=전체)] [eventId(0=cont전체)] | 필드이벤트 종료 |
| `enter_dungeon` | 4 | [contId] [enter-index] |  |
| `enter_dungeon_party` | 4 | [contId] [enter-index] | (같은월드에 있는 파티원 모두 같이)던전 입장 |
| `enter_dungeonwork` | 4 | [dungeonId] [randomMode] [workid] | 과제 선택형 솔로던전 입장 |
| `enter_gmevent` | 4 | gmEventId | GM이벤트 입장 |
| `epexp_allrev` | 4 | on/off | 모든 보정치 OnOff |
| `epexp_defaultrev` | 4 | on/off | 기본 보정치 OnOff |
| `epexp_extrabonus` | 4 | on/off | 기타 보정치 OnOff |
| `epexp_tsrev` | 4 | on/off | TS 보정치 OnOff |
| `exitclear_cool` | 4 | [1/0] | 1이면 비상탈출 시간 무시하고 항상 바로 가능 0이면 보통때 처럼 |
| `expdiv` | 4 | [EXP를 나눌 값] | 경험치 값을 파라미터 값으로 나누어서 획득 |
| `extend_party` | 4 | [partyToRaid] | 파티 ↔ 레이드 변경 |
| `extractitemfail` | 4 | [1이면 항상 실패 0이면 원래대로] | 아이템 추출할때 항상 실패되게 할건지 |
| `extractitemsuccess` | 4 | [1이면 항상 성공 0이면 원래대로] | 아이템 추출할때 항상 성공되게 할건지 |
| `fail_quest` | 4 |  | 퀘스트 실패처리 |
| `fast_box` | 4 |  | 가챠 및 eventSeed 애니메이션 스킵 |
| `fast_cube` | 4 | on / off | 큐브 재료 없이 열기 |
| `ferotation` | 4 | [on/off] | 필드 이벤트 로테이션 켜고 끄기 |
| `festival_end` | 4 | [festivalId] | 축제 비활성화 |
| `festival_start` | 4 | [festivalId] | 축제 활성화 |
| `fight_npc_arena` | 4 |  | Npc투기장 전투 시작 |
| `finish_battle_field` | 4 | [win/lose] | 강제로 전장 끝내기 |
| `finish_battle_field_round` | 4 |  | 라운드 종료 |
| `finish_tutorial` | 4 |  | 튜토리얼 종료 |
| `flyspeed` | 4 | 값 | 비행속도 조정 |
| `force_change_user_status` | 4 | [on/off] [userStatus/1=전투/0=일반/2=평화(옛 모닥불)] |  |
| `force_do_ep_passive` | 4 | on/off | 특성 패시브 100% 발동 on/off |
| `force_load_topo` | 4 | [username] | 해당 유저에게 강제로 SendLoadTopo |
| `forever_hero_select` | 4 |  | 진행 중인 영웅 선택 과정을 영원히 지속 |
| `forget_guild_skill` | 4 | [skillTemplateId] [isActive] | 길드 스킬 삭제 |
| `forget_social` | 4 | [socialMotionId] | Social 삭제 |
| `free_flight` | 4 | on/off | 비행에너지 안쓰고 비행할지 온오프 |
| `freeze_condition` | 4 |  | 컨디션 수치 고정 |
| `fulldump` | 4 | [파일명] | 풀덤프 |
| `gamble_test` | 4 |  | 도박 테스트 |
| `get` | 4 | key [id/name] | 프로퍼티 값 얻어오기 |
| `get_quest` | 4 | 개수 | 퀘스트 와장창 받기 |
| `get_return_user_reward` | 4 |  | 복귀유저 보상받기 |
| `get_rune` | 4 | [RuneId] [Amount] | 룬 획득(보관함) |
| `ghost` | 4 | [on] or [off] | 주위 스폰된 녀석들의 Server 좌표를 얻어옴 |
| `give_achievement` | 1 |  |  |
| `giveup_promotion` | 4 |  | 승급 시험 포기 |
| `gopos` | 4 | [x] [y] [z] | 특정 위치로 이동 |
| `goto` | 4 | [다른 캐릭터 이름] | 다른 캐릭터에게로 텔레포트 |
| `goto_battle_field_round` | 4 |  | 특정라운드로부터 시작 |
| `goto_least_entered_fe` | 4 | [onlyEmpty=1] | 사람이 가장 적은 이벤트로 이동 |
| `gotofe` | 4 | [contId] [eventId] | 해당 필드이벤트의 시작위치로 이동 |
| `guild_ware_range` | 4 | [startIndex] [endIndex] [itemDbId] | 길드 창고에 아이템 생성 |
| `guildwar_accept` | 4 | [상대 길드 이름] | 싸우자! XXX 길드 |
| `guildwar_giveup` | 4 | [상대 길드 이름] | 항ㅋ 벅ㅋ |
| `guildwar_raise` | 4 | [상대 길드 이름] [추가 베팅 전술칩] | 싸우자! XXX 길드 |
| `guildwar_start` | 4 | [상대 길드 이름] [베팅 전술칩] | 싸우자! XXX 길드 |
| `hero` | 4 | [HeroTemplateId] [HeroSkinId] | 신규모드 영웅 교체 |
| `holdabnormality_clear` | 4 |  | 보관된 버프 다 지우기 |
| `iamtheflashman` | 1 |  |  |
| `icanseedeadpeople` | 1 |  |  |
| `idle_social_stop` | 4 | idle 소셜 정지/시작 |  |
| `ignoreskilllos` | 4 | [on/off] | 스킬 타격시 los체크 여부 |
| `increaseinvensize` | 4 | [최소 48에서 88까지] | 캐릭인벤토리 크기 늘이기 증가만 된다 |
| `insert_item` | 4 | templateId itemCount | 아이템 획득 |
| `insert_Tcat_Product_Sale` | 4 | [아이템 ID] [할인 가격], [시간(분)] |  |
| `interest` | 4 | [on 거리] or [off] | 주위 녀석의 디버그 정보를 수집 |
| `invincible` | 4 | [on/off] | 무적상태 |
| `invisible` | 4 | [on/off] | NPC가 쌩까는 상태 |
| `invite_guild` | 4 | 캐릭명 | 길드원 초대 |
| `is_tutorial_user` | 4 |  | 튜토리얼 유저인지 확인 |
| `item_add` | 4 | [userName] [itemTemplateId] [amount] | 아이템 추가 |
| `item_del` | 4 | [userName] [itemTemplateId] [amount] | 아이템 삭제 |
| `item_list` | 4 | [userName] | 아이템 리스팅 |
| `item_multidel` | 4 |  |  |
| `item_trade` | 4 |  |  |
| `itisagooddaytodie` | 1 |  |  |
| `iwanttomove` | 1 |  |  |
| `join_party` | 4 | [planetId] [userDbId] | 파티 추가 |
| `jump_task` | 4 | [questId] [taskId] | 퀘스트의 특정 Task로 Jump |
| `jumpto` | 4 | [huntingZoneId] [templateId] | Npc에게 텔레포트 |
| `kick_party` | 4 | [planetId] [userDbId] | 파티 강퇴 |
| `kill` | 4 | 범위UU | 반경내 NPC 사살 |
| `kill_mypet` | 4 |  | 펫 소멸시키기 |
| `kill_summonee` | 4 | 캐릭명 | 길드원 초대 |
| `killme` | 4 |  | 자살 |
| `killme2` | 4 |  | 크리스탈 부시면서 자살 |
| `learn_all_epperk` | 4 |  | 모든 epPerk배우기 |
| `learn_epperk` | 4 | [skillId] [level] | Ep Perk(특성,효과) 배우기 |
| `learn_guild_skill` | 4 | [skillTemplateId] [isActive] | 길드스킬습득 |
| `learn_recommended_ep` | 4 |  | 각 클래스의 추천 특성 적용 |
| `learn_skill` | 4 | skillTemplateId isActive |  |
| `learn_social` | 4 | [socialMotionId] | Social 습득 |
| `leave_dungeon_party` | 4 |  | (같은월드에 있는 파티원 모두 같이)던전 퇴장 |
| `leave_party` | 4 |  | 파티 탈퇴 |
| `level` | 4 | 새로운레벨 | 레벨 변경 |
| `limited_drop_reset_point` | 4 | [gauge id] | 수량제어 드랍 포인트 리셋 |
| `limited_drop_set_point` | 4 | [gauge id] [capacity point] | 수량제어 드랍 포인트 조절 |
| `list` | 4 | key [id/name] | 프로퍼티 리스팅 |
| `load_drevent` | 4 |  | 미숙련 던전매칭 이벤트를 데이터시트로 추가 |
| `login` | 4 | [클래스] [마리수] | 로그인 테스트 |
| `lootitem` | 4 | [획득반경] | 획득반경 내 아이템 루팅, (range > 0: 범위만큼 루팅, range <= 0: 루팅안함, range 생략: 500 만큼 루팅) |
| `make_me_second` | 4 |  | 자신을 세컨캐릭터로 변경 |
| `make_party` | 4 | [대상 유저이름] | 파티 구성 |
| `make_party2` | 4 | [대상 유저DbId] | 파티 구성 |
| `make_vehicle_skill_book` | 4 |  | 탑승 스킬북 얻기 |
| `makeitem` | 4 | [템플릿ID] [수량] [강화] [명품(1)/각성(2)] | 아이템 만들기 |
| `makeitem_damage` | 4 | [템플릿ID] [강화] [수량] | 손상된 장비를 생성 |
| `makeitem_op` | 4 | [템플릿ID] [수량] [강화] [명품(1)/각성(2)] | 옵션 레벨이 높게 아이템 만들기 |
| `makeitem_range` | 4 | [tid_start] [tid_end] [수량] | 아이템 여러가지 만들기 |
| `makeitems` | 4 | [템플릿ID] [템플릿ID] [템플릿ID] .... [수량] | 아이템 여러가지 만들기 |
| `makematerial` | 4 | [제작아이템id] [개수] | 제작 아이템 재료 생성 |
| `makemoney` | 4 | [amount] | 돈 만들기 |
| `makeplaytestitem` | 4 | [위상] [강화 수치] | 한 번에 장비 셋팅 |
| `maketestitem` | 4 | [enchant] | 테스트 장비 아이템 생성 |
| `match_battle_field` | 4 |  | 강제로 두 파티를 전장에 매칭 [전장TID] [상대아이디] |
| `memstat` | 4 |  |  |
| `merge_item` | 4 | fromInvenPos toInvenPos | 아이템 머지하기 |
| `minigame_skip` | 4 | [true/false] | 낚시 미니게임 스킵 |
| `move_to_village` | 4 |  | 사망시 가까운 무덤 이동 |
| `mpfull` | 4 | [on/off] | MP 만땅 모드 |
| `msg_dungeon` | 4 |  | StrSheet_MonsterBehavior.xml 의 메세지 출력 |
| `msg_monsterbehavior` | 4 | [MonsterBehaviorId] [range(1000)] | StrSheet_MonsterBehavior.xml 의 메세지 출력 |
| `msg_system` | 4 | [readableId] | StrSheet_SystemMessage.xml 의 메세지 출력 |
| `next_quiz` | 4 | point | OX퀴즈 다음문제 진행 |
| `next_repequiz` | 4 | point | OX퀴즈 패자부활 진행 |
| `next_task` | 4 | [questId] | 다음 Task진행 |
| `nocool` | 4 | [on/off] | 스킬 쿨타임 무한모드 |
| `nodie` | 4 | [on/off] | 피격당하지만 안죽게 |
| `nomod` | 4 | [on/off] | 스킬중 위치보정 끄기모드 |
| `nonpk_list` | 4 |  | 이 월드 내에 NonPk 로 설정된 Section 목록 조회 |
| `nonrecovery_actpoint` | 4 | [on/off] | 모험의 주화 자연 회복 적용 |
| `nonrecovery_hp` | 4 | [on/off] | HP자동회복 on/off |
| `nonrecovery_mp` | 4 | [on/off] | MP자동회복 on/off |
| `nonrecovery_st` | 4 | [on/off] | ST자동회복 on/off |
| `notify` | 1 |  |  |
| `open_all_box` | 4 |  | 인벤토리 내 모든 EventSeed & Gacha 열기 |
| `open_bfstore` | 4 | [상점 메뉴 ID] | npc 없이 상점 열기 |
| `packettest` | 4 |  |  |
| `parcel_fee` | 4 |  | 소포 수수료 지불 |
| `parcel_recv` | 4 |  | 소포 받기 |
| `parcel_return` | 4 |  | 소포 반송 |
| `parcel_send` | 4 |  | 소포 보내기 |
| `passive_off` | 4 | 패시브아이디 | 패시브상태 끄기 |
| `passive_on` | 4 | 패시브아이디 | 패시브상태 켜기 |
| `passive_see` | 4 |  | 패시브상태 화면에 출력 |
| `passive_show` | 4 | 패시브아이디 | 패시브상태 끄기 |
| `pausenpc` | 4 | range | NPC 멈추기 |
| `pegasus` | 4 | pegasusId | 페가수스 |
| `perfect_level` | 4 | 새로운레벨 | 레벨 변경+스킬습득 |
| `perfect_skillPolishing` | 4 |  | 모든 스킬 연마 활성화 |
| `petevolution_success` | 4 |  | [-1 = 강제 실패, 0 = 노말, 1 = 강제 성공] |
| `petmix_success` | 4 |  | [-1 = 강제 실패, 0 = 노말, 1 = 강제 성공] |
| `pkmodeoff` | 4 |  | PK 상태 강제 해제 |
| `pkmodeOn` | 4 |  | PK |
| `playtimereward` | 4 | [daily/total] [보상ID] | 누적 접속시간 보상 지급 |
| `prisoner` | 4 | [on/off] | 감옥상태 세팅 |
| `producefatiguepoint` | 4 |  | 생산력 셋팅 |
| `produceitemfail` | 4 | [1이면 항상 실패 0이면 원래대로] | 아이템 제작할때 항상 실패되게 할건지 |
| `prof_bug` | 4 | 새로운숙련도 | 곤충 채집 숙련도 변경 |
| `prof_energy` | 4 | 새로운숙련도 | 기운 채집 숙련도 변경 |
| `prof_herb` | 4 | 새로운숙련도 | 약초 채집 숙련도 변경 |
| `prof_mineral` | 4 | 새로운숙련도 | 광물 채집 숙련도 변경 |
| `profile_world` | 4 |  | 월드서버 프로파일링 |
| `proxy_test` | 4 | [번호] | 주어진 번호의 프록시 테스트 |
| `pullmonster` | 4 |  | 몬스터를 내 위치로 땡겨오기 |
| `qatest_world` | 4 | on/off |  |
| `randomfe` | 4 |  | 함수내에서 지정한 로테이션에서 랜덤하게 1개의 이벤트씩을 시작 |
| `rank_add_point` | 4 | [point] | 기록 경쟁 인던 포인트 추가 |
| `rank_add_private_point` | 4 | [point] | 기록 경쟁 인던 개인 포인트 추가 |
| `rank_add_record` | 4 |  | 기록 경쟁 인던 record 1~10000점 랜덤 추가 |
| `rank_clear` | 4 |  | 기록 경쟁 인던 클리어 |
| `rank_log` | 4 | [on/off] | 기록 경쟁 인던 클리어 |
| `rank_start` | 4 |  | 기록 경쟁 인던 점수 카운트 시작 |
| `rank_test` | 4 |  | 기록 경쟁 리코드 막 생성 |
| `ranktimer` | 4 |  | 랭크 타이머 진행 시간 출력 |
| `ranktimer_reset` | 4 |  | 랭크 타이머 기록 초기화 |
| `ranktimer_state` | 4 | [on/off] | 랭크타이머 시작 종료 메시지 출력여부 |
| `re` | 4 |  | 제자리부활 |
| `reaction` | 4 | [리액션모션번호] | 리액션 강제 발생 |
| `recalc_stat` | 4 |  | 스탯 재계산 |
| `recv_all_parcel` | 4 |  | 모든 우편 수령(서버팀 테스트용) |
| `recv_daily_bonus` | 4 |  | 발키온 지령서 일일 보너스 강제로 수령 |
| `recv_weekly_bonus` | 4 |  | 발키온 지령서 주간 보너스 강제로 수령 |
| `refresh_quest` | 4 |  | 퀘스트 DB로부터 다시 받아오기 |
| `register_card` | 4 | [cardTemplateId] [amount] | 카드 등록 |
| `registerParts` | 4 |  |  |
| `reload_critical_adjust_datasheet` | 4 |  | critical adjust datasheet reload |
| `reload_datasheet_world` | 4 |  | 데이터시트를 모두 리로드 |
| `reload_dr` | 4 |  | 검은 틈 데이터시트 리로드 |
| `reload_dungeon` | 4 | [던전ID] [DungeonRetry 리로드 여부] | 지정 던전만 리로드 |
| `reload_flying` | 4 |  | 비행 관련 리로드 |
| `reload_herodata` | 4 |  | 신규모드 데이터시트 리로드 |
| `reload_huntingzone` | 4 | [헌팅존ID] | 지정 헌팅존만 리로드 |
| `reload_item` | 4 |  | ItemTemplate 리로드 |
| `reload_limited_drop_datasheet` | 4 |  | limited drop data sheet reload |
| `reload_masstige_datasheet` | 4 |  | masstige datasheet reload |
| `reload_quest` | 4 |  | 퀘스트 데이타 시트를 모두 리로드 |
| `reload_rank` | 4 | [id] | 기록 경쟁 인던 데이터 xml 리로드 |
| `reload_token_exchange_datasheet` | 4 |  | token exchange data sheet reload |
| `reload_userskill` | 4 |  | 유저 스킬 다시 읽기 |
| `remove_guildtower` | 4 |  | 길드 타워 제거 |
| `remove_target_guildtower` | 4 |  |  |
| `reputation_time_init` | 4 | [npcGuildId] | 평판제한 시간 리셋  |
| `reset_add_compensation_count` | 4 |  | 이벤트 매칭 TASK 추가 보상을 모두 받지 않은 것으로 리셋 |
| `reset_all_phaselevel_world` | 4 |  | 모든 유저의 던전 페이즈 정보 초기화(월드) |
| `reset_all_user_vip_store` | 4 |  |  |
| `reset_attendance` | 4 |  | 출석체크 리셋 |
| `reset_daily_epexp` | 4 |  | 일일 획득 경험치 초기화 |
| `reset_daily_event_quest` | 4 |  | 일일 플레이 가이드 이벤트 리셋 |
| `reset_daily_quest` | 4 |  | 일일퀘스트 시드 재설정 |
| `reset_delivery_list` | 4 |  | 납품 리스트 리셋 |
| `reset_dungeon_clearcount` | 4 |  |  |
| `reset_dungeon_enter_count` | 4 |  | 던전 입장횟수 기록 삭제 |
| `reset_dungeonhistory` | 4 |  | 모든 던전 쿨타임 리셋 |
| `reset_ep` | 4 |  | Ep 시스템 초기화 |
| `reset_epperk` | 4 |  | Ep Perk(특성,효과) 초기화 |
| `reset_masstige_status` | 4 |  | 국민강화 목표템 초기화 |
| `reset_profile_world` | 4 |  | 리셋 월드서버 프로파일데이타 |
| `resumenpc` | 4 | range | NPC 다시 플레이 |
| `resurrection` | 4 |  | 제자리부활 |
| `rightbaseitem` | 4 | [RightID] [ItemTID] | RightBaseItem 받기 |
| `rightnow_gmevent` | 4 | gmEventId | 대기중인 GM이벤트 바로 시작 |
| `sealitemnow` | 4 | [1이면 바로 0이면 원래대로] | 아이템 귀속시킬때 바로 귀속되게 |
| `send_ask_code_hash` | 4 |  | 코드해시 요청 보내기 |
| `set` | 4 | key value [id/name] | 프로퍼티 값 세팅 |
| `set_achievement_accomplished` | 4 |  | 업적 달성시키기 |
| `set_achievement_cond_value` | 4 |  | 업적Condition값 설정 |
| `set_add_compensation_count` | 4 | [eventId] [acquireNum] | 이벤트 매칭 TASK 추가 보상 획득 횟수 조작 |
| `set_all_achievement_accomplished` | 4 |  | 모든 업적 달성시키기 |
| `set_autowatingtime` | 4 | [millisecond] | 자낚 대기 시간 설정 |
| `set_bitetime` | 4 | [millisecond] | 입질 대기 시간 설정 |
| `set_condition` | 4 | value | 유저의 컨디션수치 설정 |
| `set_custom_string` | 4 |  | [itemDbid] [String] 아이템 커스텀 이름 변경 |
| `set_dlm_delay` | 4 | min [max] | 월드-아비터간 작업 수행 시 랜덤 딜레이 주기 (단위: ms) |
| `set_dr_clear_each` | 4 | [RewardId] | 시공의 균열 단일 보상 설정 |
| `set_dr_clear_fail` | 4 | [RewardId] | 시공의 균열 실패 보상 설정 |
| `set_dr_clear_general` | 4 | [RewardId] | 시공의 균열 전역 보상 설정 |
| `set_dr_clear_personal` | 4 | [RewardId] | 시공의 균열 개인 보상 설정 |
| `set_dr_hp` | 4 | [hpRate] | 시공의 균열 오브젝트 초기 Hp 비율 설정 |
| `set_dungeon_clear` | 4 |  | 던전 클리어 설정 |
| `set_dungeon_reset_time` | 4 |  | 던전 입장횟수 삭제 시간 설정 |
| `set_dungeonwork_value` | 4 | [hit/combo/timer] [value] | 시험의던전 과제 값 설정 |
| `set_equipexp` | 4 | [숙련도 exp] | 장비 숙련도 경험치 변경 |
| `set_exp` | 4 | [EXP값] | 경험치 값 세팅 |
| `set_fishing_reward_table` | 4 | [tableId] | 보상 테이블 id 설정 |
| `set_gamble_master` | 4 |  | 도신이 되기 |
| `set_gamble_property` | 4 |  | 도신 프로퍼티 설정 |
| `set_hurryup_gmevent` | 4 | [hurryUpRatio] [hurryUpCounts(csv)] | GM이벤트 마감임박 변수설정 |
| `set_login_day` | 4 | [period] [loginDay] | 일일 플레이 가이드 출석 체크 |
| `set_minigamelevel` | 4 | [level] | 미니게임 레벨 설정 |
| `set_money` | 4 | [amount] |  돈 세팅 |
| `set_npcguild_exp` | 4 | [npcGuildId] [grade] [exp] | 평판 세력 경험치 설정 |
| `set_npcguild_point` | 4 | [npcGuildId] [point] | 평판 포인트 설정 |
| `set_on_dungeon_event` | 4 |  | 던전 모든 이벤트 시작 |
| `set_on_field_event` | 4 |  | 필드이벤트의 모든 EventGroup 시작 |
| `set_pc_level` | 4 | 캐릭명 레벨 | 캐릭 레벨 세팅 |
| `set_pc_loc` | 4 | 캐릭명 위치스트링 | 캐릭 위치 세팅 |
| `set_petdur` | 4 | [dur] | changing pet-dur temporarily |
| `set_petprof` | 4 | [숙련도] | 펫 숙련 임의 변경 |
| `set_pkpoint` | 4 | [pkPoint] | pK포인트 변경 |
| `set_promotion` | 4 |  | 승급 달성 |
| `set_round_pve_kill_point` | 4 |  | PveKill_Point[point] |
| `set_round_pve_skill_point` | 4 |  | PveSkill_Point[point] |
| `set_skill_prof` | 4 | skillId skillProf | 스킬 숙련도 설정 |
| `set_temper_ratio` | 4 | 인챈트확률 | 인챈트확률고정 |
| `set_token_exchange_point` | 4 | [tokenExchangeId] [point] | 토큰 점수 조정 |
| `set_tutorial_user` | 4 | [on/off] | 튜토리얼 유저 여부 설정 |
| `set_walk_speed` | 4 |  | 걷기 속도 설정 |
| `set_world_worldparam` | 4 | [param-name] [param-value] | WorldParam 수정 |
| `setfeprogress` | 4 | [contId] [eventId] [setvalue] | 필드이벤트 진행도 설정 |
| `setfieldeventprogress` | 4 | [contId] [eventId] [setvalue] | 필드이벤트 진행도 설정 |
| `setmoney` | 4 | [이름] [돈] | 돈 세팅 |
| `setrestbonus` | 4 | rest_bonus |  |
| `show_bonfire_info` | 4 |  | 현재 유저의 모닥불 정보 보여주기 |
| `show_chapter` | 4 |  | 집회소 보기 |
| `show_condition` | 4 |  | 유저의 컨디션수치 보기 |
| `show_FinalConditionalSkillProb` | 4 |  | 파트너 스킬 최종 확률 확인(스킬 발동 후 사용) |
| `show_incubator` | 4 |  | 펫 인큐베이터 열기 |
| `show_items` | 4 | [tabIdx] [beginIdx] [endIdx] | 인벤 item 정보 확인 |
| `show_limited_drop_info` | 4 | [gauge id] | 수량제어 게이지 정보 보여주기 |
| `show_petmanager` | 4 |  | 펫 메니저 열기 |
| `show_phaselevel_world` | 4 |  | 내 페이즈 정보 |
| `show_skill_learn` | 4 | huntingZoneId npcId |  |
| `show_social_learn` | 4 |  | 습득한 Social 보이기 |
| `show_temper_window` | 4 |  | 항가~ |
| `show_time` | 4 |  | 현재 시간 보기 |
| `show_user_status` | 4 |  | 유저의 상태 보기 |
| `show_vs` | 4 |  | 탑승병기 속도 출력 |
| `showfei` | 4 | [on/off] | 필드이벤트 상황정보 텍스트 출력 |
| `showfeinfo` | 4 | [on/off] | 필드이벤트 상황정보 텍스트 출력 |
| `showfep` | 4 |  | 현재 참여중인 fieldEvent 내부값 보여주기 |
| `showfeprogress` | 4 |  | 현재 참여중인 fieldEvent 내부값 보여주기 |
| `showinven` | 4 |  |  |
| `showpos` | 4 | [x] [y] [z] [showTime] | 디버깅용 벡터 위치 표시 |
| `shuttle` | 4 | [on/off] shuttleId | 동적지오(셔틀) 키고 끄기 |
| `skill_cheater_world` | 4 | [on/off] [termSeconds(int)] [countThreashold(int)] [kick/nokick] | 스킬 치터 설정변경 |
| `skill_miss_limit` | 4 |  | 몬스터가 타겟에게 높이 차이등으로 스킬적중을 몇 회 실패했을 때 리셋할건지 |
| `skillLog` | 4 |  | 서버 파일로그 남기기 |
| `skip_hero_select` | 4 |  | 진행 중인 영웅 선택 과정 스킵 |
| `sm` | 4 | [시스템메시지번호] | 시스템메시지 |
| `social` | 4 | [socialMotionId] | 소셜 |
| `spawn_bonfire` | 4 | typeid | 모닥불 소환하기 |
| `spawn_territory` | 4 | [사냥터Id] [TerritoryId] [채널id] | 테리토리안에 있는거 전부 스폰 |
| `spawn_worldspawn` | 4 | [WorldSpawnId] [SpawnTerritoryGroupId] | WorldSpawnData.xml에서 id가 WorldSpawnId인 WorldSpawn의 현재 스폰 테리토리 그룹을 id가 SpawnTerritoryGroupId인 SpawnTerritoryGroup으로 변경한다. |
| `spawnalleventnpc` | 4 |  | 모든 event npc 스폰 |
| `spawnallnpc` | 4 |  | 모든 npc 스폰 |
| `spawncollection` | 4 |  | 유저 채널의 모든 채집물 리스폰 |
| `spawnnpc` | 4 | [사냥터Id] [NpcTemplateId] [Count] | 스폰 시켜보자 |
| `spawnworkobject` | 4 | [huntingZoneId] [templateId] | Work Object 스폰 |
| `speed` | 4 | multiplier | 이동 속도 증가 |
| `speed_hack_check` | 4 |  | 스팩 체크 [on/off] |
| `start_battle` | 4 |  | 전장 즉시 시작 |
| `start_citywar` | 4 |  | 공방전 시작 |
| `start_eventgroup` | 4 | [id] | 던전 이벤트그룹 강제 시작 |
| `start_npc_arena` | 4 |  | Npc투기장 시작 |
| `start_promotion` | 4 |  | 승급 시험 시작 |
| `start_quest` | 4 | 퀘스트아이디 | 퀘스트 시작 |
| `start_seren_guide` | 4 | [type, id] | 세렌가이드 시작 |
| `start_tbagroup` | 4 | [테레토리 ID] | 신규모드 전장 내 테레토리 스폰 |
| `startfe` | 4 | [contId] [eventId] | 필드이벤트 시작 |
| `startfieldevent` | 4 | [contId] [eventId] | 필드이벤트 시작 |
| `startnpcskill` | 4 | skill_template_id | NPC 스킬실행 시키기 |
| `stfull` | 4 | [on/off] | ST 만땅 모드 |
| `stopFloatingCastleUserCheck` | 4 |  |  |
| `store_changeinfo` | 4 | [index] | 상점 아이템 판매 수정 사항 보여주기 (WorldServer) |
| `stwork` | 4 | [combat Work Id] | 선택된 대상 Npc의 combat 상태 work ID 를 강제로 실행 |
| `style_ware_range` | 4 | [startIndex] [endIndex] [itemDbId] | 스타일 창고에 아이템 생성 |
| `summonParty` | 4 |  | 파티원 모두를 내 위치로 소환 |
| `swap_party` | 4 | [slotIndex1] [slotIndex2] |  |
| `sysconfig_world` | 4 |  |  |
| `task_goal` | 4 | [questId] [taskIdx] [flag] |  |
| `tba_account_level` | 4 | [레벨] | 신규모드 계정 레벨 |
| `tba_add_account_exp` | 4 | [경험치] | 신규모드 계정 경험치 추가 |
| `tba_change_kda` | 4 | [k,d,a] | 신규모드 전장 KDA 수정 (델타 값 아님) |
| `tba_grade_reward` | 4 | 리워드 테스트용도 |  |
| `tba_update_battlefield_daily_reward_count` | 4 | [newCount] | 신규모드 전장 일일보상제한횟수 변경 |
| `teleport` | 4 | [worldId] [x] [y] [z] | 텔레토비 |
| `teleport_channel` | 4 |  |  |
| `temper_item` | 4 | temperItemSlot materialItemSlot | 아이템 제련 |
| `test_parts_store` | 4 |  |  |
| `testoff` | 4 |  | 프로그램테스트 종료 |
| `teston` | 4 | 테스트종류, 기타인자 | 프로그램테스트 시작 |
| `this` | 4 |  | 내 계정의 캐릭터들의 위치를 지금 캐릭터 위치로 강제 조작해준다 |
| `toggle_epInfo` | 4 |  | 특성 Info |
| `track_dynamic` | 4 |  | 현재 타겟으로 잡혀 있는 Npc 하나 만 추적 리스트에서 트랙킹 |
| `track_target` | 4 |  | 선택된 대상 Npc를 추적 리스트에 추가 |
| `trade_accept` | 4 |  |  |
| `trade_additem` | 4 |  |  |
| `trade_request` | 4 |  |  |
| `unidentifysuccess` | 4 |  | 재봉인 무조건 성공[on/off] |
| `unRegisterParts` | 4 |  |  |
| `untrack_target` | 4 |  | 선택된 대상 Npc를 추적 리스트에서 제거 |
| `update_battlepass_mission_count` | 4 | [MissionKind] [Count] | 배틀패스 미션 카운트 추가 |
| `use_exp_gain_data` | 4 | [on/off] |  |
| `userawake` | 4 | [grade] | [0 = 미각성 1 = 각성 |
| `vaporize` | 4 |  | 진정한 투명화 |
| `vehicle_avatar` | 4 | [huntingZoneId] [templateId] | 쿠마스(avatar) 탑승 |
| `vehicle_clear_cooltime` | 4 |  | 탑승병기 스킬 쿨타임 초기화 |
| `vehicle_dettach` | 4 |  | 쿠마스/탱크 강제 하차 |
| `vehicle_killself` | 4 |  | 탑승병기 자폭 |
| `vehicle_nocool` | 4 | [on/off] | 탑승병기 스킬 쿨타임 없애기 |
| `view_channel` | 4 |  | 채널 보기 |
| `virtual_latency` | 4 | [min] [max] | 가상 레이턴시 |
| `visit_all_sections` | 4 |  | 모든지역방문 |
| `vm_buy` | 4 | [vm인벤#,개수] [...] | 구매 |
| `vm_edit_buy` | 4 | [vm인벤#,유저인벤#,tempId,살개수,회수할개수,가격] [...] | 구매기 세팅 |
| `vm_edit_sell` | 4 | [회수금액] [vm인벤#,유저인벤#,tempId,팔개수,회수할개수,가격] [...] | 판매기 세팅 |
| `vm_sell` | 4 | [vm인벤#,개수] [...] | 판매 |
| `vm_show_buy` | 4 |  | 구매목록보기 |
| `vm_show_sell` | 4 |  | 판매목록보기 |
| `ware_list` | 4 |  | 창고 보기 |
| `ware_range` | 4 | [startIndex] [endIndex] [itemDbId] | 계정 창고에 아이템 생성 |
| `whereis` | 1 |  |  |
| `win_battle_field` | 4 |  | 강제로 전장에서 승리 |
| `write_gcfunctions` | 4 |  |  |
| `부활` | 4 |  | 제자리부활 |
