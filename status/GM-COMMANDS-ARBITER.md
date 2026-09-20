# Arbiter QA command reference

192 commands registered via `ArbiterQACommandHandler` in `ArbiterServer.exe` on this build.
Companion to the 416 world-server commands — **608 total**. Same syntax: `/@<name>`.

**Risk key** — 🔴 A = owner only · 🟠 B = senior staff · 🟢 C = safe for support · ⚪ undocumented

---

## 🔴 A — Currency, accounts, privilege

| Command | Args | Korean | English |
|---|---|---|---|
| `set_admin_level` | `캐릭명 레벨` | 디버그/어드민 커맨드를 위해서 | **Set a character's admin level.** Grants every other command. Block first. |
| `fund_coin` | `[Funding amount]` | 현금 충전 | **Top up cash balance** |
| `create_bill_account` | — | 구좌 생성 | Create a billing account |
| `purchase_prod` | `[ProductID]` | 상품 구매 | Purchase a cash-shop product |
| `query_point` | — | Cash point 조회 | Query cash points |
| `set_floating_castle_coin` | `추가 포인트` | 파츠 상점 코인 추가 | Add parts-shop coins |
| `battlechip_add` | — | 전술칩 지급 | Grant tactical chips |
| `battlechip_pay` | — | 조건에 맞게 길드 전술칩 지급 | Grant guild tactical chips |
| `gc_add_battle_chip` | `[guildCount]` | 길드들에 전술칩 넣기 | Give tactical chips to guilds |
| `gc_renewal_coin` | — | 길드 경쟁 코인 정산 | Settle guild competition coins |
| `add_package` | `[PackageId] [ExpireSecond] {[UserName]}` | Account Trait Package 설정 | Grant an account trait package |
| `set_account_res_level` | `[level]` | 제한계정 레벨 세팅 | Set restricted-account level |
| `create_user` | `[username]` | 새로운 캐릭터를 생성 | Create a character |
| `import_characters` | — | — | Import characters |
| `testitem` | `[testItem]` | TestItem | Create a test item |
| `insert_floating_castle_parts` | `파츠Id` | 파츠인벤에 파츠 추가 | Add parts to the parts inventory |
| `warehousegold_max` | `[amount]` | 100은 창고에 100원까지 보관 가능, 0은 원래대로 | Set warehouse gold cap |
| `set_guild_rec` | — | 길드 추천수 조절 | Adjust guild recommendation count |
| `addcompetitionpoint` | — | — | Add competition points |
| `ps_add_point` | `[point]` | 경쟁포인트 추가 | Add lord-system competition points |
| `ps_policy_point` | `[point]` | 정책포인트 변경 | Change policy points |
| `ps_gw_point` | `[Point]` | 영주길드전 경쟁 포인트 변경 | Change lord guild-war points |
| `ps_maketax` | `[value]` | 세금 총액변경 | Change total tax amount |
| `ps_vote_count` | `[count]` | 투표수 조작 | **Manipulate vote counts** |
| `ps_im_king` | `[guardId]` | 영주즉위 | **Crown yourself lord** |
| `add_allunionguild_seasoncp` | — | — | Add season CP to all union guilds |
| `add_allunionmember_seasoncp` | — | — | Add season CP to all union members |
| `addunioncommanderpolicyhistory` | — | — | Add union commander policy history |
| `addunionconsulpolicyhistory` | — | — | Add union consul policy history |
| `addunionelitepolicyhistory` | — | — | Add union elite policy history |
| `addunionmemberhistory` | — | — | Add union member history |
| `gc_add_many_score` | `[contentInfoId] [score] [count]` | 길드 리그 참가 및 점수 추가 | Join guild league and add score |
| `gc_add_score` | `[contentInfoId] [score]` | 컨텐츠 점수 추가 | Add content score |
| `gc_set_score` | `[guildName] [score]` | 길드경쟁 종합점수 설정 | Set guild competition total score |
| `add_guild_skill` | `[스킬ID] [길마스킬:0\|1] [영구스킬:0\|1]` | 길드 스킬 추가 | Add a guild skill |
| `assign_floating_castle` | — | 부유성 지급 | Grant a floating castle |
| `masterpiece` | — | — | Force masterpiece |
| `set_bf_result` | `[전장ID] [승] [무] [패] [평점]` | — | Set battleground win/draw/loss record |
| `rank_next_season` | `[dungeonId]` | 기록 경쟁 해당 던전 시즌 넘김 | Advance ranked-dungeon season |
| `set_referer` | `[RefererName] [RefereeName]` | Refer-A-Friend 초대자 설정 | Set refer-a-friend inviter |

## 🟠 B — Destructive, server state, config

| Command | Args | Korean | English |
|---|---|---|---|
| `crash_arbiter` | — | 아비터 크래시 시키기 | **Crash the arbiter.** Takes the whole server down. |
| `dbg_break` | — | debug - 강제로 Break Point Exception 발생 | Force a breakpoint exception |
| `testautomation_clear_allusers_completely` | — | 현재 Account의 모든 유저들을 완전히 삭제 (복구불가) | **Permanently delete all users on the account. Unrecoverable.** |
| `clear_inven` | — | 인벤토리 모두 비우기 | Empty a character's inventory |
| `clear_parcel` | `[캐릭터 이름]` | 대상 캐릭터의 소포 비우기 | Empty a character's parcels |
| `clear_recipe` | — | RECIPE 비우기 | Clear recipes |
| `clear_package` | `{[UserName]}` | Account Trait Package 초기화 | Reset account trait packages |
| `clear_board` | — | — | Clear board data |
| `clear_serverachievement` | `[id]` | 서버최초 업적 초기화 | Reset a server-first achievement |
| `clear_all_serverachievement` | — | 전체 서버최초 업적 초기화 | Reset all server-first achievements |
| `clear_bf_cool` | — | 전장 쿨타임 및 입장 횟수 모두 삭제 | Clear battleground cooldowns and entry counts |
| `clear_vote_cool` | — | 투표 쿨타임 모두 삭제 | Clear all vote cooldowns |
| `create_guild` | `[guild-name] [first-username]` | 새로운 길드를 생성 | Create a guild |
| `create_many_guilds` | `[count:1~99] [unionId]` | 길드 많이 만들기 | Create many guilds |
| `create_system_guild` | `[guild-name]` | 새로운 시스템 길드를 생성 | Create a system guild |
| `add_guildmember` | `[count]` | 길드원 추가 | Add guild members |
| `Guildaddmax` | `[count]` | 100은 최대 100명, 0은 원래대로 | Set guild member cap |
| `guild_join_cooltime` | — | 길드 재가입 쿨타임 설정 | Set guild rejoin cooldown |
| `guildlevelexpire` | `[분]` | 길드 레벨 유지 기간 바꾸기 | Change guild level retention period |
| `guildlevelextendexpire` | `[분]` | 길드 레벨 유지 연장 기간 바꾸기 | Change guild level extension period |
| `guildwar_immediate` | — | 진행 대기중인 모든 길드전 즉시 시작 | Start all pending guild wars immediately |
| `toggle_guildwar_acceptable` | — | 길드전 허용 상태 토글 | Toggle guild war acceptance |
| `reset_guildwar_toggle_cool` | — | 길드전 허용 토글 쿨타임 초기화 | Reset guild war toggle cooldown |
| `set_union` | — | 연맹 셋팅 | Configure union |
| `leave_union` | — | — | Leave union |
| `union_season` / `union_season_admin` | — | — | Union season control |
| `setunionstatus` | — | — | Set union status |
| `ps_activate_election` | — | 영주 시스템 활성화 | Activate the lord/election system |
| `ps_clear_election` | — | 영주 및 선출정보 초기화 | Reset lord and election data |
| `ps_go_next` | — | 영주 선출단계 강제로 다음으로 | Force election to next stage |
| `ps_period_minute` | `[minute]` | 영주 선출 주기 분단위로 바꾸기 | Change election cycle to minutes |
| `ps_calc_tax` | — | 세금 강제정산 | Force tax settlement |
| `ps_planet_vote` | `[on/off]` | 전 서버 1계정 1투표 | One-vote-per-account server-wide |
| `ps_candidacy_membercount` | `[member-count]` | 후보자 등록 필수 길드 회원수 조정 | Adjust candidacy member requirement |
| `ps_world_tick` | `[sec]` | 월드 틱 변경 | Change world tick rate |
| `ps_gw_reset` | — | 영주길드전 포인트 제한 초기화 | Reset lord guild-war point limits |
| `ps_gw_time` | `[Hour]` | 영주길드전 포인트 제한 초기화 시간 변경 | Change lord guild-war reset hour |
| `set_arbiter_worldparam` | `[param-name] [param-value]` | WorldParam 수정 | **Modify arbiter world parameters** |
| `sysconfig` | `[new config value]` | 시스템 설정 바꾸기 | Change system configuration |
| `publisher` | `[publisher name]` | 퍼블리셔 변경 | Change publisher |
| `set_bypass` | — | Bypass 껐다 켜기 | Toggle the client-packet bypass |
| `set_play_limit` | `[제한수]` | 대기열 생기도록 최대 | Set max players (creates a queue) |
| `set_chat_ban` | `[set_chat_ban]` | — | Set chat ban |
| `blocklist_max` | `[count]` | 차단가능한 유저 수 변경 | Change max blocked users |
| `pchannel_max` | `[count]` | 개인채팅채널 입장 가능한 유저 수 변경 | Change private chat channel cap |
| `pk_section` | `[area id] [section id] [on/off]` | Section 별 Pk OnOff | Toggle PK per section |
| `reload_ir` | — | InputRestrictionData.xml Reload | Reload input restriction data |
| `reload_netm` | — | NetModeratorConfig.xml Reload | Reload net moderator config |
| `reload_shop_data` | — | Reload shop data | Reload shop data |
| `turn_ir` | — | Input Restriction 켜고 끄기 | Toggle input restriction |
| `turn_netm` | — | Net Moderator 켜고 끄기 | Toggle net moderator |
| `change_shop_status` | — | change shop status | Change shop status |
| `change_loading_screen_status` | `[status]` | Loading Screen Control | Control the loading screen |
| `change_achievement_season` | `[시즌ID]` | 업적 시즌 변경 | Change achievement season |
| `change_pr` | — | 홍보문구 바꾸기 | Change the promotional text |
| `dailyevent` | `[dayOfWeek]` | 요일이벤트 날짜 변경 (0없음 1일 … 7토) | Change the day-of-week event |
| `reserve_festival` | `[EventId] [StartTime] [EndTime]` | 월드 이벤트 예약 | Schedule a world event |
| `start_festival` / `stop_festival` | — | 월드 이벤트 시작/종료 | Start / stop a world event |
| `set_festival_spawn_object_count` | `[EventId] [HzId] [NpcTid] [Count]` | 월드 이벤트 스폰 오브젝트 카운트 설정 | Set world event spawn counts |
| `huntingevent_add` | `[type] [hz] [val] [duration]` | 사냥터 이벤트 추가 | Add a hunting-zone event |
| `huntingevent_remove` | `[event id]` | 사냥터 이벤트 제거 | Remove a hunting-zone event |
| `reset_dungeon` | — | 모든 인던 리셋 | Reset all instances |
| `set_dungeoncool` | `[dungeonId] [minutes]` | 던전 쿨타임 설정 | Set dungeon cooldown |
| `reset_event_mail` | `[이벤트ID]` | 이벤트메일 리셋 | Reset event mail |
| `reset_admin_reward` | — | 어드민 보상 기록 초기화 | Reset admin reward records |
| `reset_floating_castle_parts_cooltime` | — | 부유성 파츠 쿨타임 리셋 | Reset floating castle parts cooldown |
| `collect_floating_castle` | — | 부유성 회수 | Reclaim a floating castle |
| `send_mass_parcel` | — | 대규모 우편 테스트용 | Mass-mail test |
| `set_max_recv_mail_cnt` | — | 받은편지함 최대 갯수 설정 | Set inbox cap |
| `set_max_send_mail_cnt` | — | 보낸편지함 최대 갯수 설정 | Set outbox cap |
| `set_escrow_return_wait` | — | 에스크로우편 반송 대기 시간 설정 | Set escrow mail return wait |
| `parcelreturn` | — | 소포반송시간 | Parcel return time |
| `ware_duration` | `[seconds]` | 창고 수수료 기간 조절 | Adjust warehouse fee period |
| `set_trade_broker_remain_hours` | — | 거래중개소 아이템 남은 만료시간 설정 | Set broker item expiry |
| `tb_avg_price` | — | 거래중개소 평균 거래가 다시 계산 | Recalculate broker average prices |
| `set_board_clear_time` | `[시간/분]` | 등산게시판 작성횟수 리셋 시간 설정 | Set board post-count reset time |
| `set_logout_time` | `캐릭명 년 월 일` | 로그아웃 시간지정 | Set a character's logout time |
| `set_pcbang` | `[on/off]` | pc방 유저인지 설정 | Set PC-bang user flag |
| `set_new_member` | `[on/off]` | 신규 유저인지 설정 | Set new-user flag |
| `set_go` | — | GO 캐릭터 설정 | Set as GO (game operator) character |
| `dis` | — | 그냥 client 연결 끊어버리기 테스트 | Disconnect a client |
| `escape` | `캐릭명` | 어딘가로 이동 | Move a character somewhere |
| `init_limited_gacha` | `[gachaId]` | init LimitedGacha | Initialise limited gacha |
| `limited_gacha_go_next_bucket` | `[gachaId] [1/0]` | go next bucket | Advance gacha bucket |
| `init_awesomium` | — | Awesomium 초기화 | Initialise embedded browser |
| `open_awesomium` | `[URL]` | Awesomium dialog open & navigate | **Open a URL in the embedded browser** |
| `set_awesomium_web_url` | `[url]` | awesomium URL 설정 | Set embedded browser URL |
| `set_awesomium_debug_mode` | — | awesomium DebugMode 설정 | Set embedded browser debug mode |
| `test_on` / `test_off` | `테스트종류, 기타인자` | 프로그램테스트 시작/종료 | Start / end program test |
| `nexus_broadcast` / `nexus_bypass` | — | — | Nexus server operations |
| `BOT_check` / `BOT_recvmsg` | — | — | Bot detection operations |
| `unlock_all_movies` | — | 모든 영상을 unlock 합니다 | Unlock all cutscenes |
| `nounionskillcooltime` | — | — | Remove union skill cooldowns |
| `gc_new_season` | — | 길드 경쟁 시즌 넘기기 | Advance guild competition season |
| `gc_renewal` | — | 길드 경쟁 중간 정산 | Interim guild competition settlement |
| `gc_calc_contents_rank` | — | 길드 경쟁 컨텐츠 랭크 재계산 | Recalculate guild content ranks |
| `rank_sort` | — | 기록 경쟁 순위 바로 계산 | Recalculate ranked standings |
| `dr_enable_event` | `[ezid] [option]` | 검은 틈 EventZone 활/비활성화 | Enable/disable dark rift event zone |
| `dr_enable_time` | `[groupId] [baseTime]` | 검은 틈 예약 허용 시간대 추가 | Add dark rift reservation window |
| `assign_guild_emblem` / `collect_guild_emblem` | — | — | Assign / reclaim guild emblem |
| `write_board` | — | — | Write to board |
| `chrono_debug` | `[mode]` | 크로노스크롤 디버그 모드 | Chronoscroll debug mode |

## 🟢 C — Safe for support

| Command | Args | Korean | English |
|---|---|---|---|
| `help` | `[keyword]` | QA 명령어 검색 | Search QA commands |
| `connection` | — | 동접 | Concurrent user count |
| `usage` | — | IO관련 사용률 | I/O usage stats |
| `profile` / `reset_profile` | — | 프로파일링 / 리셋 | Profiling, reset profiling data |
| `logout` | — | 로그아웃 테스트 | Logout test |
| `party` | `[userName]` | 파티 만들기 | Create a party |
| `apply_party` | — | 파티지원 | Apply to party match |
| `reg_party` / `unreg_party` | — | 파티매치 등록/삭제 | Register / deregister party match |
| `show_party` | — | 파티매치 목록보기 | View party match list |
| `world_of_party_match` | — | — | Party match world info |
| `change_partymatch_pr_text` | — | 홍보문구 | Change party match ad text |
| `battlefield` | `[battleFieldId other-username]` | 전장 진입 | Enter a battleground |
| `observer_mode` | — | 관전 모드 활성화/비활성화 | Toggle observer mode |
| `info_dungeon` | — | [기본정보]>[인던] 출력내용 표시 | Show dungeon info |
| `dungeon_log` | — | 던전 로그 사용 설정 | Toggle dungeon logging |
| `char_record` / `char_record_end` | — | 캐릭터 녹화 / 중지 | Start / stop character recording |
| `check_simple_tip` | `simpleTipId` | 간단팁 본것으로 체크 | Mark a tip as seen |
| `clear_simple_tip` | — | 간단팁 체크 정보 초기화 | Reset tip flags |
| `show_cand` | — | 지원자 목록보기 | View candidate list |
| `ps_com_window` | `[대륙번호] [0투표 1전장]` | 경쟁창 보기 | Open competition window |
| `ps_guard_window` | — | 가드 정보창 열기 | Open guard info window |
| `ps_guard_info` | `[guardId]` | 가드정보 보기 | View guard info |
| `ps_reg_window` | — | 영주 후보 등록창 열기 | Open lord candidacy window |
| `ps_vote_window` | — | 영주 투표창 열기 | Open lord voting window |
| `gc_enter_guild` | — | 길드 경쟁 참여 | Join guild competition |
| `gc_guildscore_view` | `[contentsId] [guildName]` | 길드 점수 보기 | View guild score |
| `gc_score_view` | `[contentsId] [userName]` | 길드경쟁 개인점수 보기 | View personal competition score |
| `gc_show_rank` | — | 리그 랭킹 점수 보기 | View league rankings |
| `show_union` / `showcompetitionpoint` / `showplaypoint` | — | — | View union / competition / play points |
| `nextseasonstatus` / `nextuniondungeonschedule` / `nextuniondungeonstatus` | — | — | Union season and dungeon schedule info |
| `lord_behavior` | `[behavior-type-id]` | 영주 업적 행동 테스트 메시지 송신 | Send lord achievement test message |
| `tutorial` | `[on/off]` | 튜토리얼 플레이 on/off | Toggle tutorial play |
| `tutorialPlayer` | `[on/off]` | 튜토리얼 필요 여부 강제 세팅 | Force tutorial-required flag |
| `refresh_inven` | — | — | Refresh inventory |
| `change_voice` | — | — | Change voice |
| `sticktogether` | — | — | Stick together |
| `devdebug` | — | — | Developer debug |
| `qatest` | — | — | QA test toggle |
| `add_guildmember` | `[count]` | 길드원 추가 | Add guild members |

---

## Notes

**Two crash commands, one per process.** `crash_arbiter` here, `crash` on the world server.
Either lets any QA account take the server down. Block both.

**`set_admin_level` is the master key.** Anything else you block is undone by a GM who can
raise their own level. If you restrict one command, restrict this.

**`open_awesomium [URL]`** drives an embedded Chromium browser in the server process.
Pointing that at arbitrary URLs from a GM command is worth disabling outright.

**`ps_*` is the lord/political system** — elections, taxes, guards, voting.
`ps_im_king` and `ps_vote_count` let a GM install themselves as lord or fake an election.

**Enforcement caveat, again.** Filtering `C_ADMIN` in your proxy only stops GMs using your
proxy. Real control is who gets QA status at login.

---

## 🟢 C — TeraSharp additions (not in ArbiterServer.exe)

Commands TeraSharp answers Arbiter-side that the retail Arbiter does not register. They are
listed here because this file is also the catalogue `GmCommandCatalog` loads, and
`GmCommandHandlers.Implemented` must be a subset of it.

| Command | Args | Korean | English |
|---|---|---|---|
| `vis` | — | — | **T128. Make yourself visible and able to cast** — sends `S_ADMIN_GM_SKILL` skill 0 / enabled 0, cap_final_gm_client2 frame 546. |
| `invis` | — | — | **T128. Make yourself invisible again** — the same frame with enabled 1, which is the enter-world push (frame 99). |

`vaporize` and `invisible` are **not** here: they stay World commands
(`GM-COMMANDS-FULL.md` lines 417 and 194). T128 handles them Arbiter-side as well — they flip
the same client-side switch, the way Alt+A does — and then still forwards them to World, since
being hidden from other players is World's half of the job and has no client-facing packet of
its own. `GmCommandHandlers.AlsoForwarded` is that list.
