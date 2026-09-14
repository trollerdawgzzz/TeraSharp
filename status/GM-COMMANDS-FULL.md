# GM / QA command reference — complete

Extracted from the command registration table in `WorldServer.exe` on this server build.
416 commands. Use in game as `/@<name>`, or send via `C_ADMIN`:

```javascript
mod.send('C_ADMIN', 1, { command: 'abnormality 4000' });
```

Requires QA status — granted when `S_LOGIN_ARBITER` returns status 31 or 33.

**Risk key**
- 🔴 **A** — creates value or grants power from nothing. Owner only.
- 🟠 **B** — destructive, affects other players, or changes server state. Senior staff only.
- 🟢 **C** — safe for normal support work.
- ⚪ — undocumented in the binary, test/internal.

---

| Command | Args | Korean (from binary) | English | Risk |
|---|---|---|---|---|
| `_helpworld` | `[keyword]` | — | Command help | 🟢 C |
| `abnormality` | `abnormalityId` | 이상상태 강제 | Force an abnormality (buff/debuff) | 🟢 C |
| `abnormality_off` | `abnormalityId` | 이상상태 강제 | Turn off an abnormality | 🟢 C |
| `abnormality_show` | `abnormalityId` | 이상상태 강제 | Show abnormality | 🟢 C |
| `accept_revival` | — | 부활 받았을 경우 부활 | Accept a revival when offered | 🟢 C |
| `accomplish_achievement_rate` | — | 현재 시즌 달성률 넘을 때까지 업적 달성시키기 | Complete achievements until past this season's rate | 🔴 A |
| `add_bf_score` | `[점수]` | 전장 거점 점령점수 가산 | Add battleground capture points | 🟠 B |
| `add_exp` | `[EXP값]` | 경험치 더하기 | Add experience | 🔴 A |
| `add_hp` | `[HP값]` | HP 값 조정 | Adjust HP | 🟢 C |
| `add_mp` | `[MP값]` | MP 값 조정 | Adjust MP | 🟢 C |
| `add_npcguild` | `[npcGuildId]` | 평판 세력 등장 | Make a reputation faction appear | 🟠 B |
| `add_servant_energy` | `[얼만큼]` | 펫 에너지 증감 | Increase/decrease pet energy | 🟢 C |
| `add_st` | `[ST값]` | ST 값 조정 | Adjust ST (stamina) | 🟢 C |
| `adjust_bonfire` | `[시간]` | 유저가 만든 모닥불 남은 시간 변경 | Change remaining time on a user-made bonfire | 🟢 C |
| `aibehavior` | `[on 거리]` or `[off]` | 주위 Npc의 Ai 정보를 수집 | Collect AI info on nearby NPCs | 🟢 C |
| `allabnormality` | `[on/off]` | 스킬에 의해 발생하는 이상상태 무조건 적용 | Always apply skill-caused abnormalities | 🟠 B |
| `allhit` | `[on/off]` | 스킬 적관계 무시하고 무조건 적중 | Skills always hit, ignoring hostility | 🟠 B |
| `allreaction` | `[0/1/2]` | 스킬 적중시 무조건 리액션 발생 (0없음, 1미니, 2기본) | Always trigger reaction on hit (0 none, 1 mini, 2 normal) | 🟢 C |
| `angry` | `[range]` | 분노모드로 바꾸기 | Put NPCs into rage mode | 🟢 C |
| `aoi` | — | 주위 world object 개수 | Count nearby world objects | 🟢 C |
| `apm_add` | `[id]` | 자동 파티 매칭 풀 진입 | Enter auto party matching pool | 🟢 C |
| `apm_ask` | — | 자동 파티 매칭 허용 팝업 띄움 | Show auto party matching consent popup | 🟢 C |
| `apm_reset` | — | 자동 파티 매칭 허용 상태 초기화 | Reset auto party matching consent | 🟢 C |
| `apm_use` | `[on/off]` | 자동 파티 매칭 사용 모드 변경 | Toggle auto party matching | 🟢 C |
| `assert` | — | 어서트정보 테스트 | Assert info test | 🟠 B |
| `atk_speed` | `[공속비율]` | 비율대로 모든 스킬의 속도가 증가 | Increase all skill speeds by a ratio | 🟠 B |
| `autorevivaltime` | `[분]` | 자동 부활 시간 조절하기 | Adjust auto-revival time | 🟢 C |
| `ban_guild` | `캐릭명` | 길드원 추방 | Expel a guild member | 🟠 B |
| `bf_Invite` | — | — | Battleground invite | ⚪ |
| `bf_chat` | — | — | Battleground chat | ⚪ |
| `bf_end` | — | — | End battleground | 🟠 B |
| `bf_goto` | — | — | Go to battleground | 🟢 C |
| `bf_kick` | — | — | Kick from battleground | 🟠 B |
| `bf_leave` | — | — | Leave battleground | 🟢 C |
| `bf_match` | — | — | Battleground matching | 🟢 C |
| `block_quest` | `[questId]` | 퀘스트를 시작할 수 없게 막기 | Prevent a quest from being started | 🟠 B |
| `bookmark` | `bookmark_id` | 북마크(서버) | Bookmark (server) | 🟢 C |
| `calc_battle_field_Season_next_time` | — | 다음 번 서버 시작시 전장 시즌 생성 | Generate battleground season on next server start | 🟠 B |
| `calc_battle_field_ranking_next_time` | — | 다음 번 서버 시작시 전장 랭킹 생성 | Generate battleground ranking on next server start | 🟠 B |
| `calmdown` | `[range]` | 분노모드 해제 | Cancel rage mode | 🟢 C |
| `cancelaction` | `[다음 액션번호]` | 액션 강제 해제 | Force-cancel an action | 🟢 C |
| `change_daily_quest_record_reset_hour` | `[id]` | 일퀘 클리어 시간 변경 | Change daily quest clear-record reset hour | 🟠 B |
| `change_daily_quest_seed_reset_hour` | `[id]` | 일퀘 시드 재생성 시간 변경 | Change daily quest seed regeneration hour | 🟠 B |
| `change_exp_gain_data` | `[on/off]` or `[level, ratio, on/off]` | 월드 경험치 획득량 조절 명령어 | Adjust world EXP gain rate | 🔴 A |
| `change_guildchief` | `캐릭명` | 길드장 위임 | Transfer guild leadership | 🟠 B |
| `change_npc_ai` | `[range] [huntingZoneId] [npcid] [patternListId]` | NpcAi 조정하기 | Adjust NPC AI | 🟠 B |
| `change_npc_stat` | `[range] [ratio] [type]` | NpcStat 비율 조정하기 | Adjust NPC stats by ratio | 🟠 B |
| `change_npc_to_user` | — | 유저 변신모드 해제 | Cancel user transform mode | 🟢 C |
| `change_party_manager` | `[planetId] [userDbId]` | 파티장 변경 | Change party leader | 🟢 C |
| `change_user` | `[CLASS] [RACE] [GENDER]` | 클래스 종족 성별 바꾸기 | Change class, race, gender | 🟠 B |
| `change_user_status` | `[battle/peace]` | 평화/전투 상태 변경 | Change peace/combat state | 🟢 C |
| `change_user_to_npc` | `[huntingZoneId] [templateId]` | 유저 변신모드 | User transform mode | 🟢 C |
| `changenpc` | `[사냥터id] [NpcInstanceId]` | Npc 교체 스폰 | Replace-spawn an NPC | 🟢 C |
| `changenpcangermode` | `[on/off]` | 분노모드 on/off | Toggle NPC rage mode | 🟢 C |
| `charin` | `캐릭이름` | 캐릭터 로그인 테스트 | Character login test | 🟠 B |
| `charm` | `부적아이디` | 부적 추가 강제 | Force-add a charm | 🔴 A |
| `charout` | `캐릭이름` | 캐릭터 로그아웃 테스트 | Character logout test | 🟠 B |
| `check_guildname` | `길드이름` | 길드명 중복체크 | Check guild name availability | 🟢 C |
| `checkcell` | — | 셀 확인 (서버용) | Check cell (server-side) | 🟢 C |
| `cl_reset` | — | 전투 로그 툴 리셋 | Reset combat log tool | 🟢 C |
| `clear_achievement` | — | 업적 초기화 | Reset achievements | 🟠 B |
| `clear_aggro` | — | 어그로 삭제 | Clear aggro | 🟢 C |
| `clear_all_achievement` | — | 업적 초기화 | Reset all achievements | 🟠 B |
| `clear_all_crest` | `[문장포인트]` | 문장 적용 해제 및 문장 포인트 적용 | Clear crests, apply crest points | 🔴 A |
| `clear_all_skill` | — | 습득한 모든 스킬 삭제 | Delete all learned skills | 🟠 B |
| `clear_all_social` | — | 습득한 모든 Social 삭제 | Delete all learned socials | 🟠 B |
| `clear_bonfire` | `[거리]` | 유저가 만든 모닥불 삭제 | Delete user-made bonfires | 🟢 C |
| `clear_build_object` | `[거리]` | 유저가 만든 빌드 오브젝트 삭제 | Delete user-made build objects | 🟠 B |
| `clear_cont` | — | 채널 청소 | Clean channel | 🟠 B |
| `clear_daily_quest_complete` | — | 일일퀘스트 완료 기록 클리어 | Clear daily quest completion records | 🟠 B |
| `clear_quest` | `[questId] [all]` | 퀘스트 DB클리어하기 | Clear quest database | 🟠 B |
| `clear_recipe_world` | — | worldServer에서 RECIPE 삭제 | Delete recipes on the world server | 🟠 B |
| `clear_seren_guide` | — | — | Clear Seren guide | ⚪ |
| `clear_withdrawer` | — | 겁쟁이 상태 해제 | Clear "coward" state | 🟢 C |
| `clearallabnormality` | — | 버프 다 지우기 (유료화버프도 다 지운다) | Remove all buffs, including cash-shop buffs | 🟢 C |
| `clearbuff` | — | 버프류 모두 삭제 | Remove all buffs | 🟢 C |
| `clearcool` | — | 스킬쿨타임 모두 초기화 | Reset all skill cooldowns | 🟢 C |
| `clearware` | — | 창고청소 | Clear warehouse | 🟠 B |
| `combatlog` | `[on/off]` | 전투 로그 활성화 | Enable combat logging | 🟢 C |
| `combatwork` | `[combat Work Id]` | 선택된 대상 Npc의 combat work ID 강제 실행 | Force-run a combat work ID on the target NPC | 🟢 C |
| `complete_all_quest` | — | 모든 자동 수주 퀘스트 완료 | Complete all auto-accepted quests | 🟠 B |
| `complete_extractor_quest` | — | — | Complete extractor quest | 🟠 B |
| `crash` | `[파일명]` | 크래시 | **Crash the server** | 🟠 B |
| `create_channel` | `월드` | 채널 생성 | Create a channel | 🟠 B |
| `create_item` | `[아이템템플릿번호]` | 월드에 아이템 생성해서 드랍 | Create an item in the world and drop it | 🔴 A |
| `crest` | `[문장포인트]` | 문장 적용 해제 및 문장 포인트 적용 | Clear crests, apply crest points | 🔴 A |
| `crest_all` | — | 문장 모두 습득 | Learn all crests | 🔴 A |
| `crest_window` | — | 문장 창 열기 | Open crest window | 🟢 C |
| `damage` | — | 주위 몬스터에게 데미지 주기 | Damage nearby monsters | 🟢 C |
| `debugsend` | `[on/off]` | 디버그 정보 보내기 | Send debug info | 🟢 C |
| `delete_Tcat_Product_Sale` | `[아이템 ID]` | Tcat 상품 할인 해제 | Remove a cash-shop discount | 🔴 A |
| `despawn_all_extractor` | `[unionId] [point]` | 분쟁 대륙 추출기 디스폰 | Despawn all conflict-continent extractors | 🟠 B |
| `despawn_territory` | `[사냥터id] [TerritoryId] [채널id]` | 테리토리안에 있는거 전부 디스폰 | Despawn everything in a territory | 🟠 B |
| `despawnalleventnpc` | — | 모든 event npc 디스폰 | Despawn all event NPCs | 🟠 B |
| `despawnallnpc` | — | 모든 npc 디스폰 | Despawn all NPCs | 🟠 B |
| `destroy_channel` | `월드 채널인스턴스` | 채널 삭제 | Delete a channel | 🟠 B |
| `destroy_guild` | — | 길드 해체 | Disband guild | 🟠 B |
| `disable_license` | `[라이센스 이름]` | 라이센스 해제 | Revoke a license | 🟠 B |
| `dont_ban_battle_field` | — | 베틀 필드에서 팅김 방지 | Prevent battleground disconnection | 🟠 B |
| `dr_available_count` | — | 검은 틈 퀘스트 수주량 조절 | Adjust dark rift quest availability | 🟢 C |
| `dr_balancing_usercount` | `[userCount]` | 현 위치의 검은 틈의 BalancingUserCount 수정 | Modify dark rift balancing user count | 🟢 C |
| `dr_close` | `[hzid] [dr_tid]` | 검은 틈 닫기 | Close a dark rift | 🟢 C |
| `dr_cloud` | `[step]` | 검은 틈 연출 열기 | Open dark rift visual effect | 🟢 C |
| `dr_cloud_off` | — | 검은 틈 연출 닫기 | Close dark rift visual effect | 🟢 C |
| `dr_dungeon_reset` | — | 검은 틈 던전 재입장 가능하게 설정 | Allow dark rift dungeon re-entry | 🟢 C |
| `dr_expand` | — | 현 위치의 검은 틈을 강제로 확장 | Force-expand the dark rift here | 🟢 C |
| `dr_fold` | — | 현 위치의 검은 틈을 강제로 수축 | Force-shrink the dark rift here | 🟢 C |
| `dr_goto` | `[hzid] [dr_tid]` | 검은 틈으로 이동 | Move to a dark rift | 🟢 C |
| `dr_here` | — | 검은 틈을 유저 위치에 소환 | Summon a dark rift at your position | 🟢 C |
| `dr_inspect` | — | 현재 위치의 검은 틈 정보 출력 | Print info on the dark rift here | 🟢 C |
| `dr_list_reserve` | — | 예약된 검은 틈 EventGroup 정보 조회 | List reserved dark rift event groups | 🟢 C |
| `dr_open` | `[hzid] [dr_tid]` | 검은 틈 열기 | Open a dark rift | 🟢 C |
| `dr_open_group` | `[egid]` | 검은 틈 EventGroup 강제로 열기 | Force-open a dark rift event group | 🟢 C |
| `dr_report` | — | 열려있는 검은 틈 전체 정보 출력 | Print info on all open dark rifts | 🟢 C |
| `dr_rereserve_group` | — | 검은 틈 EventGroup 예약 초기화 후 재예약 | Reset and re-reserve dark rift event groups | 🟢 C |
| `dr_rightnow` | — | 모든 검은 틈의 Countdown 무시하고 바로 시작 | Start all dark rifts immediately | 🟠 B |
| `dr_set_abnormality` | `[abnormalityId]` | 모든 검은 틈의 이상상태를 고정 | Fix the abnormality for all dark rifts | 🟢 C |
| `dr_set_npcgroup` | `[step] [groupTemplateId]` | 모든 검은 틈의 n단계 Spawn Npc 고정 | Fix spawn NPC group per dark rift step | 🟢 C |
| `dr_set_questtemplate` | `[id1] [id2] [id3]` | 모든 검은 틈의 퀘스트 TemplateId 고정 | Fix dark rift quest template IDs | 🟢 C |
| `dr_spawnreport` | — | 검은 틈 소환 대기 목록 출력 | Print dark rift spawn queue | 🟢 C |
| `dr_start_quest` | — | 검은 틈 퀘스트 시작 | Start dark rift quest | 🟢 C |
| `dr_step` | `[step]` | 검은 틈 단계를 넘김 | Advance dark rift stage | 🟢 C |
| `dr_user_count` | — | 검은 틈 퀘스트 진행인원 변경 | Change dark rift quest participant count | 🟢 C |
| `dropitem` | `template_id amount` | 아이템 드랍 시키기 | Make an item drop | 🔴 A |
| `dump_performance_data` | — | — | Dump performance data | 🟢 C |
| `dungeon_flag` | `[flag-name]` | 던전 플래그를 설정 | Set a dungeon flag | 🟢 C |
| `dungeon_timer` | `[timer-value]` | 던전 타이머를 설정 | Set the dungeon timer | 🟢 C |
| `enable_license` | `[라이센스 이름]` | 라이센스 취득 | Grant a license | 🟠 B |
| `enable_ride_vehicle` | `[on/off]` | 탑승병기 무조건 탑승 가능 | Always allow vehicle boarding | 🟢 C |
| `enchantitemsuccess` | `[1이면 항상 성공]` | 아이템 강화할때 항상 성공되게 | Always succeed when enchanting | 🔴 A |
| `end_npc_arena` | — | Npc투기장 종료 | End NPC arena | 🟢 C |
| `enter_dungeon` | `[contId] [enter-index]` | 던전 입장 | Enter dungeon | 🟢 C |
| `exit_union` | — | — | Leave union | 🟠 B |
| `exitclear_cool` | `[1/0]` | 비상탈출 시간 무시 | Ignore escape cooldown | 🟢 C |
| `expdiv` | `[EXP를 나눌 값]` | 경험치 값을 파라미터 값으로 나누어서 획득 | Divide EXP gained by a value | 🔴 A |
| `extend_party` | `[partyToRaid]` | 파티 ↔ 레이드 변경 | Convert party to raid and back | 🟢 C |
| `extractitemfail` | `[1이면 항상 실패]` | 아이템 추출할때 항상 실패되게 | Always fail when extracting | 🟠 B |
| `extractitemsuccess` | `[1이면 항상 성공]` | 아이템 추출할때 항상 성공되게 | Always succeed when extracting | 🔴 A |
| `fail_quest` | — | 퀘스트 실패처리 | Force quest failure | 🟠 B |
| `fast_box` | — | 가챠 및 eventSeed 애니메이션 스킵 | Skip gacha and event-seed animations | 🟢 C |
| `festival_end` | `[festivalId]` | 축제 비활성화 | Deactivate a festival | 🟠 B |
| `festival_start` | `[festivalId]` | 축제 활성화 | Activate a festival | 🟠 B |
| `fight_npc_arena` | — | Npc투기장 전투 시작 | Start NPC arena combat | 🟢 C |
| `finish_battle_field` | `[win/lose]` | 강제로 전장 끝내기 | Force a battleground to end | 🟠 B |
| `finish_battle_field_round` | — | 라운드 종료 | End the round | 🟠 B |
| `finish_tutorial` | — | 튜토리얼 종료 | Finish tutorial | 🟢 C |
| `forget_skill` | `skillTemplateId isActive` | 스킬삭제 | Remove a skill | 🟠 B |
| `forget_social` | `[socialMotionId]` | Social 삭제 | Remove a social | 🟠 B |
| `freeze_condition` | — | 컨디션 수치 고정 | Freeze condition value | 🟢 C |
| `fulldump` | `[파일명]` | 풀덤프 | Full memory dump | 🟠 B |
| `gamble_test` | — | 도박 테스트 | Gambling test | 🟠 B |
| `get` | `key [id/name]` | 프로퍼티 값 얻어오기 | Get a property value | 🟢 C |
| `get_region_info` | — | 현재 위치한 지역 정보 출력하기 | Print current region info | 🟢 C |
| `ghost` | `[on]` or `[off]` | 주위 스폰된 녀석들의 Server 좌표를 얻어옴 | Get server coordinates of nearby spawns | 🟢 C |
| `give_achievement` | — | — | Give an achievement | 🔴 A |
| `giveup_promotion` | — | 승급 시험 포기 | Give up the promotion exam | 🟢 C |
| `goto` | `[캐릭터 이름]` | 다른 캐릭터한테로 텔레포트 | Teleport to another character | 🟢 C |
| `goto_battle_field_round` | — | 특정 라운드부터 시작 | Start from a specific round | 🟠 B |
| `goto_extractor` | — | — | Go to extractor | 🟢 C |
| `guildwar_accept` | `[상대 길드 이름]` | 싸우자! XXX 길드 | Accept a guild war | 🟠 B |
| `guildwar_giveup` | `[상대 길드 이름]` | 항ㅋ 벅ㅋ | Surrender a guild war | 🟠 B |
| `guildwar_raise` | `[상대 길드] [추가 베팅 전술칩]` | 싸우자! XXX 길드 | Raise the guild war bet | 🟠 B |
| `guildwar_start` | `[상대 길드] [베팅 전술칩]` | 싸우자! XXX 길드 | Start a guild war | 🟠 B |
| `holdabnormality_clear` | — | 보관된 버프 다 지우기 | Clear all stored buffs | 🟢 C |
| `icanseedeadpeople` | — | — | (see dead people — debug view) | ⚪ |
| `idle_social_stop` | — | idle 소셜 정지/시작 | Stop/start idle socials | 🟢 C |
| `ignoreskilllos` | `[on/off]` | 스킬 타격시 los체크 여부 | Toggle line-of-sight check on skill hits | 🟠 B |
| `increaseinvensize` | `[48~88]` | 캐릭인벤토리 크기 늘이기 (증가만) | Increase inventory size (increase only) | 🔴 A |
| `insert_Tcat_Product_Sale` | `[아이템 ID] [할인 가격] [시간(분)]` | Tcat 상품 할인 등록 | Register a cash-shop discount | 🔴 A |
| `insert_item` | `templateId itemCount` | 아이템 획득 | Acquire an item | 🔴 A |
| `interest` | `[on 거리]` or `[off]` | 주위 녀석의 디버그 정보를 수집 | Collect debug info on nearby entities | 🟢 C |
| `invincible` | `[on/off]` | 무적상태 | Invincibility | 🟢 C |
| `invisible` | `[on/off]` | NPC가 쌩까는 상태 | NPCs ignore you | 🟢 C |
| `invite_guild` | `캐릭명` | 길드원 초대 | Invite a guild member | 🟢 C |
| `is_tutorial_user` | — | 튜토리얼 유저인지 확인 | Check if user is in tutorial | 🟢 C |
| `item_add` | `[userName] [itemTemplateId] [amount]` | 아이템 추가 | Add items to a named user | 🔴 A |
| `item_del` | `[userName] [itemTemplateId] [amount]` | 아이템 삭제 | Delete items from a named user | 🟠 B |
| `item_list` | `[userName]` | 아이템 리스팅 | List a user's items | 🟢 C |
| `item_multidel` | — | — | Multi-delete items | 🟠 B |
| `item_trade` | — | — | Item trade | 🟠 B |
| `iwanttomove` | — | — | (movement debug) | ⚪ |
| `join_party` | `[planetId] [userDbId]` | 파티 추가 | Join a party | 🟢 C |
| `join_union` | — | — | Join union | 🟠 B |
| `jumpto` | `[huntingZoneId] [templateId]` | Npc에게 텔레포트 | Teleport to an NPC | 🟢 C |
| `kick_party` | `[planetId] [userDbId]` | 파티 강퇴 | Kick from party | 🟠 B |
| `kill` | `범위` | 반경내 NPC 사살 | Kill NPCs within radius | 🟠 B |
| `kill_mypet` | — | 펫 소멸시키기 | Destroy your pet | 🟢 C |
| `kill_summonee` | `캐릭명` | (설명 오기: 길드원 초대) | Kill summoned entity | 🟢 C |
| `killme` | — | 자살 | Suicide | 🟢 C |
| `killme2` | — | 크리스탈 부시면서 자살 | Suicide while breaking crystals | 🟢 C |
| `learn_skill` | `skillTemplateId isActive` | 스킬습득 | Learn a skill | 🔴 A |
| `learn_social` | `[socialMotionId]` | Social 습득 | Learn a social | 🟢 C |
| `leave_dungeon` | — | 던전 퇴장 | Leave dungeon | 🟢 C |
| `leave_party` | — | 파티 탈퇴 | Leave party | 🟢 C |
| `level` | `새로운레벨` | 레벨 변경 | Change level | 🔴 A |
| `limited_drop_reset_point` | `[gauge id]` | 수량제어 드랍 포인트 리셋 | Reset quantity-controlled drop points | 🔴 A |
| `limited_drop_set_point` | `[gauge id] [capacity point]` | 수량제어 드랍 포인트 조절 | Adjust quantity-controlled drop points | 🔴 A |
| `list` | `key [id/name]` | 프로퍼티 리스팅 | List properties | 🟢 C |
| `login` | `[클래스] [마리수]` | 로그인 테스트 | Login test | 🟠 B |
| `make_me_second` | — | 자신을 세컨캐릭터로 변경 | Set yourself as a second character | 🟢 C |
| `make_party` | `[대상 유저이름]` | 파티 구성 | Form a party | 🟢 C |
| `make_party2` | `[대상 유저DbId]` | 파티 구성 | Form a party by DbId | 🟢 C |
| `make_vehicle_skill_book` | — | 탑승 스킬북 얻기 | Get a vehicle skill book | 🟢 C |
| `makeitem` | `[템플릿ID] [수량] [강화] [명품(1)/각성(2)]` | 아이템 만들기 | **Create items** | 🔴 A |
| `makeitem_range` | `[tid_start] [tid_end] [수량]` | 아이템 여러가지 만들기 | Create a range of items | 🔴 A |
| `makeitems` | `[템플릿ID]... [수량]` | 아이템 여러가지 만들기 | Create several items | 🔴 A |
| `makemoney` | `[amount]` | 돈 만들기 | **Create money** | 🔴 A |
| `maketestitem` | `[enchant]` | 테스트 장비 아이템 생성 | Create test equipment | 🔴 A |
| `match_battle_field` | `[전장TID] [상대아이디]` | 강제로 두 파티를 전장에 매칭 | Force two parties into a battleground | 🟠 B |
| `memstat` | — | — | Memory statistics | 🟢 C |
| `merge_item` | `fromInvenPos toInvenPos` | 아이템 머지하기 | Merge items | 🔴 A |
| `move_to_village` | — | 사망시 가까운 무덤 이동 | Move to nearest graveyard | 🟢 C |
| `mpfull` | `[on/off]` | MP 만땅 모드 | Full MP mode | 🟢 C |
| `next_task` | — | 다음 Task진행 | Advance to next task | 🟢 C |
| `nocool` | `[on/off]` | 스킬 쿨타임 무한모드 | No skill cooldowns | 🟢 C |
| `nodie` | `[on/off]` | 피격당하지만 안죽게 | Take damage but never die | 🟢 C |
| `nomod` | `[on/off]` | 스킬중 위치보정 끄기모드 | Disable position correction during skills | 🟢 C |
| `nonpk_list` | — | 이 월드 내에 NonPk로 설정된 Section 목록 조회 | List non-PK sections in this world | 🟢 C |
| `nonrecovery_hp` | `[on/off]` | HP자동회복 on/off | Toggle HP auto-recovery | 🟢 C |
| `nonrecovery_mp` | `[on/off]` | MP자동회복 on/off | Toggle MP auto-recovery | 🟢 C |
| `nonrecovery_st` | `[on/off]` | ST자동회복 on/off | Toggle ST auto-recovery | 🟢 C |
| `notify` | — | — | Notify | ⚪ |
| `open_all_box` | — | 인벤토리 내 모든 EventSeed & Gacha 열기 | Open all event seeds and gacha in inventory | 🟠 B |
| `packettest` | — | — | Packet test | ⚪ |
| `parcel_fee` | — | 소포 수수료 지불 | Pay parcel fee | 🟢 C |
| `parcel_recv` | — | 소포 받기 | Receive parcel | 🟢 C |
| `parcel_return` | — | 소포 반송 | Return parcel | 🟢 C |
| `parcel_send` | — | 소포 보내기 | Send parcel | 🟢 C |
| `passive_off` | `패시브아이디` | 패시브상태 끄기 | Turn off a passive | 🟢 C |
| `passive_on` | `패시브아이디` | 패시브상태 켜기 | Turn on a passive | 🟢 C |
| `passive_see` | — | 패시브상태 화면에 출력 | Display passive states | 🟢 C |
| `passive_show` | `패시브아이디` | 패시브상태 끄기 | Show passive state | 🟢 C |
| `pausenpc` | `range` | NPC 멈추기 | Pause NPCs | 🟢 C |
| `pegasus` | `pegasusId` | 페가수스 | Pegasus mount | 🟢 C |
| `perfect_level` | `새로운레벨` | 레벨 변경 + 스킬습득 | Change level and learn skills | 🔴 A |
| `pkmodeoff` | — | PK 상태 강제 해제 | Force PK mode off | 🟢 C |
| `prisoner` | `[on/off]` | 감옥상태 세팅 | Set jail state | 🟠 B |
| `producefatiguepoint` | — | 피로도 셋팅 | Set fatigue points | 🟠 B |
| `produceitemfail` | `[1이면 항상 실패]` | 아이템 제작할때 항상 실패되게 | Always fail when crafting | 🟠 B |
| `produceitemsuccess` | `[1이면 항상 성공]` | 아이템 제작할때 항상 성공되게 | Always succeed when crafting | 🔴 A |
| `prof_bug` | `새로운숙련도` | 곤충 채집 숙련도 변경 | Change insect-gathering proficiency | 🔴 A |
| `prof_energy` | `새로운숙련도` | 기운 채집 숙련도 변경 | Change energy-gathering proficiency | 🔴 A |
| `prof_herb` | `새로운숙련도` | 약초 채집 숙련도 변경 | Change herb-gathering proficiency | 🔴 A |
| `prof_mineral` | `새로운숙련도` | 광물 채집 숙련도 변경 | Change mineral-gathering proficiency | 🔴 A |
| `profile_world` | — | 월드서버 프로파일링 | World server profiling | 🟢 C |
| `pullmonster` | — | 몬스터를 내 위치로 땡겨오기 | Pull monsters to your position | 🟢 C |
| `qatest_world` | `on/off` | — | Toggle QA test mode (world) | 🟠 B |
| `rank_add_point` | `[point]` | 기록 경쟁 인던 포인트 추가 | Add ranked-dungeon points | 🔴 A |
| `rank_add_private_point` | `[point]` | 기록 경쟁 인던 개인 포인트 추가 | Add personal ranked-dungeon points | 🔴 A |
| `rank_add_record` | — | 기록 경쟁 인던 record 랜덤 추가 | Add random ranked-dungeon records | 🔴 A |
| `rank_clear` | — | 기록 경쟁 인던 클리어 | Clear ranked dungeon | 🟠 B |
| `rank_log` | `[on/off]` | 기록 경쟁 인던 로그 | Ranked-dungeon logging | 🟢 C |
| `rank_start` | — | 기록 경쟁 인던 점수 카운트 시작 | Start ranked-dungeon scoring | 🟢 C |
| `rank_test` | — | 기록 경쟁 리코드 막 생성 | Generate junk ranked records | 🟠 B |
| `reaction` | `[리액션모션번호]` | 리액션 강제 발생 | Force a reaction | 🟢 C |
| `recalc_stat` | — | 스탯 재계산 | Recalculate stats | 🟢 C |
| `registerParts` | — | — | Register parts | ⚪ |
| `reload_critical_adjust_datasheet` | — | critical adjust datasheet reload | Reload critical adjust datasheet | 🟠 B |
| `reload_datasheet` | — | 데이터시트를 모두 리로드 | Reload all datasheets | 🟠 B |
| `reload_dr` | — | 검은 틈 데이터시트 리로드 | Reload dark rift datasheets | 🟠 B |
| `reload_dungeon` | `[던전ID]` | 지정 던전만 리로드 | Reload a specific dungeon | 🟠 B |
| `reload_huntingzone` | `[헌팅존ID]` | 지정 헌팅존만 리로드 | Reload a specific hunting zone | 🟠 B |
| `reload_item` | — | ItemTemplate 리로드 | Reload item templates | 🟠 B |
| `reload_limited_drop_datasheet` | — | limited drop data sheet reload | Reload limited-drop datasheet | 🟠 B |
| `reload_masstige_datasheet` | — | masstige datasheet reload | Reload masstige datasheet | 🟠 B |
| `reload_noctan` | — | — | Reload Noctan data | 🟠 B |
| `reload_quest` | — | 퀘스트 데이타 시트를 모두 리로드 | Reload all quest datasheets | 🟠 B |
| `reload_rank` | `[id]` | 기록 경쟁 인던 데이터 xml 리로드 | Reload ranked-dungeon XML | 🟠 B |
| `reload_token_exchange_datasheet` | — | token exchange data sheet reload | Reload token exchange datasheet | 🟠 B |
| `reload_userskill` | — | 유저 스킬 다시 읽기 | Re-read user skills | 🟠 B |
| `reputation_time_init` | `[npcGuildId]` | 평판제한 시간 리셋 | Reset reputation time limit | 🟠 B |
| `reset_daily_event_quest` | — | 일일 플레이 가이드 이벤트 리셋 | Reset daily play-guide event | 🟠 B |
| `reset_daily_quest` | — | 일일퀘스트 시드 재설정 | Reset daily quest seed | 🟠 B |
| `reset_dungeon_clearcount` | — | 모든 던전 쿨타임 리셋 | Reset all dungeon cooldowns | 🟢 C |
| `reset_dungeon_enter_count` | — | 던전 입장횟수 기록 삭제 | Delete dungeon entry-count records | 🟢 C |
| `reset_dungeonhistory` | — | 모든 던전 쿨타임 리셋 | Reset all dungeon cooldowns | 🟢 C |
| `reset_masstige_status` | — | 국민강화 목표템 초기화 | Reset masstige target item | 🟠 B |
| `reset_pcbang_inven` | — | PC방 인벤토리 리셋 | Reset PC-bang inventory | 🟠 B |
| `reset_profile_world` | — | 리셋 월드서버 프로파일데이타 | Reset world server profiling data | 🟢 C |
| `reset_promotion` | — | 승급 리셋 | Reset promotion | 🟠 B |
| `resumenpc` | `range` | NPC 다시 플레이 | Resume NPCs | 🟢 C |
| `resurrection` | — | 제자리부활 | Resurrect in place | 🟢 C |
| `rightbaseitem` | `[RightID] [ItemTID]` | RightBaseItem 받기 | Receive a RightBaseItem | 🔴 A |
| `sealitemnow` | `[1이면 바로]` | 아이템 귀속시킬때 바로 귀속되게 | Bind items instantly | 🔴 A |
| `send_ask_code_hash` | — | 코드해시 요청 보내기 | Send code-hash request | 🟢 C |
| `set` | `key value [id/name]` | 프로퍼티 값 세팅 | Set a property value | 🟠 B |
| `set_Season_Ranker` | `[전장 ID]` | 전 시즌 랭커로 설정 | Set as previous-season ranker | 🟠 B |
| `set_achievement_accomplished` | — | 업적 달성시키기 | Complete an achievement | 🔴 A |
| `set_achievement_cond_value` | — | 업적Condition값 설정 | Set achievement condition value | 🔴 A |
| `set_all_achievement_accomplished` | — | 모든 업적 달성시키기 | Complete all achievements | 🔴 A |
| `set_condition` | `value` | 유저의 컨디션수치 설정 | Set user condition value | 🟢 C |
| `set_custom_string` | `[itemDbid] [String]` | 아이템 커스텀 이름 변경 | Change an item's custom name | 🟠 B |
| `set_dungeon_clear` | — | 던전 클리어 설정 | Mark dungeon as cleared | 🟠 B |
| `set_dungeon_reset_time` | — | 던전 입장횟수 삭제 시간 설정 | Set dungeon entry-count reset time | 🟠 B |
| `set_dungeon_user_count` | — | 테스트를 위해 던전 안의 유저 수 조정 | Adjust dungeon user count for testing | 🟢 C |
| `set_ep` | — | EP변경 | Change EP | 🔴 A |
| `set_exp` | `[EXP값]` | 경험치 값 세팅 | Set experience | 🔴 A |
| `set_gamble_master` | — | 도신이 되기 | Become the gambling god | 🔴 A |
| `set_gamble_property` | — | 도신 프로퍼티 설정 | Set gambling properties | 🔴 A |
| `set_login_day` | `[period] [loginDay]` | 일일 플레이 가이드 출석 체크 | Set attendance check day | 🟠 B |
| `set_money` | `[amount]` | 돈 세팅 | **Set money** | 🔴 A |
| `set_noctan_count` | — | 녹탄 성능 변경 | Change Noctan performance | 🟠 B |
| `set_npcguild_exp` | `[npcGuildId] [grade] [exp]` | 평판 세력 경험치 설정 | Set reputation faction EXP | 🔴 A |
| `set_npcguild_point` | `[npcGuildId] [point]` | 평판 포인트 설정 | Set reputation points | 🔴 A |
| `set_on_dungeon_event` | — | 던전 모든 이벤트 시작 | Start all dungeon events | 🟢 C |
| `set_pc_level` | `캐릭명 레벨` | 캐릭 레벨 세팅 | Set a named character's level | 🔴 A |
| `set_pc_loc` | `캐릭명 위치스트링` | 캐릭 위치 세팅 | Set a named character's position | 🟠 B |
| `set_petdur` | `[dur]` | changing pet-dur temporarily | Temporarily change pet durability | 🟢 C |
| `set_petprof` | `[숙련도]` | 펫 숙련 임의 변경 | Change pet proficiency | 🔴 A |
| `set_pkpoint` | `[pkPoint]` | pK포인트 변경 | Change PK points | 🔴 A |
| `set_promotion` | — | 승급 달성 | Complete promotion | 🔴 A |
| `set_round_pve_kill_point` | `[point]` | PveKill_Point | Set PvE kill points for the round | 🟠 B |
| `set_round_pve_skill_point` | `[point]` | PveSkill_Point | Set PvE skill points for the round | 🟠 B |
| `set_skill_prof` | `skillId skillProf` | 스킬 숙련도 설정 | Set skill proficiency | 🔴 A |
| `set_temper_ratio` | `인챈트확률` | 인챈트확률고정 | Fix enchant probability | 🔴 A |
| `set_token_exchange_point` | `[tokenExchangeId] [point]` | 토큰 점수 조정 | Adjust token exchange points | 🔴 A |
| `set_tutorial_user` | `[on/off]` | 튜토리얼 유저 여부 설정 | Set tutorial user flag | 🟢 C |
| `set_walk_speed` | — | 걷기 속도 설정 | Set walk speed | 🟢 C |
| `set_world_worldparam` | `[param-name] [param-value]` | WorldParam 수정 | **Modify world parameters** | 🟠 B |
| `setmoney` | `[이름] [돈]` | 돈 세팅 | **Set a named character's money** | 🔴 A |
| `setnoctan` | — | 녹탄 성능 변경 | Change Noctan performance | 🟠 B |
| `setrestbonus` | `rest_bonus` | — | Set rested bonus | 🔴 A |
| `show_bonfire_info` | — | 현재 유저의 모닥불 정보 보여주기 | Show your bonfire info | 🟢 C |
| `show_chapter` | — | 집회소 보기 | View the guild hall | 🟢 C |
| `show_condition` | — | 유저의 컨디션수치 보기 | View condition value | 🟢 C |
| `show_extractor` | — | — | Show extractor | 🟢 C |
| `show_incubator` | — | 펫 인큐베이터 열기 | Open pet incubator | 🟢 C |
| `show_items` | — | 인벤확인 | Check inventory | 🟢 C |
| `show_limited_drop_info` | `[gauge id]` | 수량제어 게이지 정보 보여주기 | Show quantity-control gauge info | 🟢 C |
| `show_petmanager` | — | 펫 메니저 열기 | Open pet manager | 🟢 C |
| `show_skill_learn` | `huntingZoneId npcId` | 스킬습득창 보이기 | Show skill-learning window | 🟢 C |
| `show_social_learn` | — | 습득한 Social 보이기 | Show learned socials | 🟢 C |
| `show_temper_window` | — | 항가~ | Open the refining window | 🟢 C |
| `show_user_status` | — | 유저의 상태 보기 | View user status | 🟢 C |
| `show_vs` | — | 탑승병기 속도 출력 | Print vehicle speed | 🟢 C |
| `showinven` | — | — | Show inventory | 🟢 C |
| `shuttle` | `[on/off] shuttleId` | 동적지오(셔틀) 키고 끄기 | Toggle dynamic geometry (shuttle) | 🟢 C |
| `skillLog` | — | 서버 파일로그 남기기 | Write skill log to server file | 🟢 C |
| `skill_miss_limit` | — | 스킬적중 실패 횟수 리셋 기준 | Miss count before reset | 🟢 C |
| `sm` | `[시스템메시지번호]` | 시스템메시지 | Send a system message | 🟢 C |
| `social` | `[socialMotionId]` | 소셜 | Play a social | 🟢 C |
| `sort_inven` | — | 인벤토리 자동 소팅 | Auto-sort inventory | 🟢 C |
| `spawn_all_extractor` | `[unionId]` | 분쟁 대륙 추출기 스폰 | Spawn all conflict-continent extractors | 🟠 B |
| `spawn_bonfire` | `typeid` | 모닥불 소환하기 | Summon a bonfire | 🟢 C |
| `spawn_one_extractor` | `[unionId]` | 분쟁 대륙 추출기 한개 스폰 | Spawn one extractor | 🟠 B |
| `spawn_territory` | `[사냥터Id] [TerritoryId] [채널id]` | 테리토리안에 있는거 전부 스폰 | Spawn everything in a territory | 🟠 B |
| `spawnalleventnpc` | — | 모든 event npc 스폰 | Spawn all event NPCs | 🟠 B |
| `spawnallnpc` | — | 모든 npc 스폰 | Spawn all NPCs | 🟠 B |
| `spawnnpc` | `[사냥터Id] [NpcTemplateId] [Count]` | 스폰 시켜보자 | Spawn NPCs | 🟢 C |
| `speed` | `multiplier` | 이동 속도 증가 | Increase movement speed | 🟢 C |
| `speed_hack_check` | `[on/off]` | 스팩 체크 | Speed hack check | 🟠 B |
| `start_extractor` | `[unionId] [point]` | 분쟁 대륙 추출기 시작 | Start conflict-continent extractor | 🟠 B |
| `start_npc_arena` | — | Npc투기장 시작 | Start NPC arena | 🟢 C |
| `start_promotion` | — | 승급 시험 시작 | Start the promotion exam | 🟢 C |
| `start_quest` | `퀘스트아이디` | 퀘스트 시작 | Start a quest | 🟢 C |
| `start_seren_guide` | `[type, id]` | 세렌가이드 시작 | Start the Seren guide | 🟢 C |
| `startnpcskill` | `skill_template_id` | NPC 스킬실행 시키기 | Make an NPC use a skill | 🟢 C |
| `stfull` | `[on/off]` | ST 만땅 모드 | Full ST mode | 🟢 C |
| `stopFloatingCastleUserCheck` | — | — | Stop floating castle user check | 🟠 B |
| `swap_party` | `[slotIndex1] [slotIndex2]` | 레이드 슬롯 변경 | Swap raid slots | 🟢 C |
| `sysconfig_world` | — | — | World system config | 🟠 B |
| `task_goal` | `[questId] [taskIdx] [flag]` | 태스크골설정 | Set a task goal | 🟠 B |
| `teleport` | `[worldId] [x] [y] [z]` | 텔레토비 | Teleport (dev pun on "Teletubby") | 🟢 C |
| `teleport_channel` | — | — | Teleport to channel | 🟢 C |
| `temper_item` | `temperItemSlot materialItemSlot` | 아이템 제련 | Refine an item | 🔴 A |
| `test_parts_store` | — | — | Parts store test | ⚪ |
| `testoff` | — | 프로그램테스트 종료 | End program test | 🟢 C |
| `teston` | `테스트종류, 기타인자` | 프로그램테스트 시작 | Start program test | 🟠 B |
| `this` | — | 내 계정의 캐릭터들의 위치를 지금 위치로 강제 조작 | Force all your account's characters to your position | 🟢 C |
| `track_dynamic` | — | 현재 타겟 Npc 하나만 추적 리스트에서 트랙킹 | Track only the current target NPC | 🟢 C |
| `track_target` | — | 선택된 대상 Npc를 추적 리스트에 추가 | Add target NPC to tracking list | 🟢 C |
| `trade_accept` | — | — | Accept trade | 🟠 B |
| `trade_additem` | — | — | Add item to trade | 🟠 B |
| `trade_cancel` | — | — | Cancel trade | 🟢 C |
| `trade_delitem` | — | — | Remove item from trade | 🟠 B |
| `trade_deny` | — | — | Deny trade | 🟢 C |
| `trade_request` | — | — | Request trade | 🟢 C |
| `unRegisterParts` | — | — | Unregister parts | ⚪ |
| `unidentifysuccess` | `[on/off]` | 재봉인 무조건 성공 | Always succeed at re-sealing | 🔴 A |
| `union_addplaypoint` | — | — | Add union play points | 🔴 A |
| `union_addpolicypoint` | — | — | Add union policy points | 🔴 A |
| `union_addseasoncp` | — | — | Add union season CP | 🔴 A |
| `union_battle_time` | `[battleState]` | 연맹 영지 전투 시작/종료 | Start/end union territory battle | 🟠 B |
| `union_changeclasstype` | — | — | Change union class type | 🟠 B |
| `union_changedesc` | — | — | Change union description | 🟠 B |
| `union_changeelitetype` | — | — | Change union elite type | 🟠 B |
| `union_changenotice` | — | — | Change union notice | 🟠 B |
| `union_post_count_many` | `[num]` | 1인당 점유율 x num 셋팅 | Set per-person occupancy multiplier | 🟠 B |
| `unionmember_addpolicypoint` | — | — | Add member policy points | 🔴 A |
| `untrack_target` | — | 선택된 대상 Npc를 추적 리스트에서 제거 | Remove target NPC from tracking list | 🟢 C |
| `up_battle_time` | `[battleState] [battleDurationInHour]` | 연맹 주둔지 공방전 시작/종료 | Start/end union garrison siege | 🟠 B |
| `up_complete_quest` | `[range]` | 연맹 기본 보상 완료 | Complete union basic rewards | 🟠 B |
| `up_goto_force_stone` | — | 랜덤하게 연맹 보호석으로 접근 | Move to a random union protection stone | 🟢 C |
| `up_kill_force_stone` | `[range]` | 연맹 보호석 강제 파괴 | Force-destroy union protection stones | 🟠 B |
| `use_exp_gain_data` | `[on/off]` | — | Toggle EXP gain data | 🔴 A |
| `vaporize` | — | 진정한 투명화 | True invisibility | 🟢 C |
| `vehicle_clear_cooltime` | — | 탑승병기 스킬 쿨타임 초기화 | Reset vehicle skill cooldowns | 🟢 C |
| `vehicle_killself` | — | 탑승병기 자폭 | Self-destruct vehicle | 🟢 C |
| `vehicle_nocool` | `[on/off]` | 탑승병기 스킬 쿨타임 없애기 | Remove vehicle skill cooldowns | 🟢 C |
| `view_channel` | — | 채널 보기 | View channels | 🟢 C |
| `view_festival` | `[EventId]` | 3M 월드 이벤트 정보 출력 | Print world event info | 🟢 C |
| `virtual_latency` | `[min] [max]` | 가상 레이턴시 | Simulate latency | 🟢 C |
| `visit_all_sections` | — | 모든지역방문 | Mark all regions as visited | 🟠 B |
| `vm_buy` | `[vm인벤#,개수] [...]` | 구매 | Vending machine buy | 🟢 C |
| `vm_edit_buy` | `[vm인벤#,유저인벤#,tempId,살개수,회수할개수,가격]` | 구매기 세팅 | Configure buying machine | 🟠 B |
| `vm_edit_sell` | `[회수금액] [vm인벤#,...]` | 판매기 세팅 | Configure selling machine | 🟠 B |
| `vm_sell` | `[vm인벤#,개수] [...]` | 판매 | Vending machine sell | 🟢 C |
| `vm_show_buy` | — | 구매목록보기 | View buy list | 🟢 C |
| `vm_show_sell` | — | 판매목록보기 | View sell list | 🟢 C |
| `ware_get` | — | 창고에서 빼내기 | Take from warehouse | 🟠 B |
| `ware_list` | — | 창고 보기 | View warehouse | 🟢 C |
| `ware_move` | — | 창고에서 창고로 이동 | Move between warehouses | 🟠 B |
| `ware_store` | — | 창고에 저장 | Store in warehouse | 🟠 B |
| `whereis` | — | — | Locate | ⚪ |
| `win_battle_field` | — | 강제로 전장에서 승리 | Force battleground victory | 🟠 B |
| `write_gcfunctions` | — | — | Write GC functions | ⚪ |

---

## Notes

**Not in this list.** The arbiter registers no commands of its own — everything runs on the
world server. Guild war, union and city war commands are all here.

**Names differ from older builds.** The commonly circulated 92.03 list uses names like
`guildwar_declare` that do not exist in this build. Trust this list over anything online.

**Blocking is not security.** Filtering `C_ADMIN` in your proxy only stops GMs who use your
proxy. Anyone with a plain client bypasses it. Real control is who receives QA status
(`S_LOGIN_ARBITER` status 31 or 33) in the first place.

**Log everything.** Your command module already prints `QA Command Success: <cmd>`. Write
that to a file with account name and timestamp — most GM problems are discovered afterwards.
