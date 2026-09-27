// SPDX-License-Identifier: MIT
// Copyright (c) 2026 the TeraSharp contributors

namespace TeraSharp.Arbiter.World;

/// <summary>
/// T165 - where every SDB_ request with a DBS_ twin stands, so none is left to wedge by accident.
/// Each twin is in exactly one group (the test walks <see cref="DbProxyOpcodeNames"/>):
/// <list type="bullet">
/// <item><b>Handled</b> - a dedicated case in <see cref="DbProxyHandlers.IsHandledRequest"/>. A name
/// T165 put in B, C or Elsewhere joins it on its own the moment its handler is registered there
/// (<see cref="Landed"/>, "real handler on master" - T164's 0x2790 and 0x28AE were the first):
/// the tables below are T165's classification, the allow-list decides what is still pending.</item>
/// <item><b>A</b> - World's Handler_DBS_* reads only [DlmId][ok] (plus, for some, the atom list the
/// real Arbiter echoes): a <see cref="DbAckTable"/> row.</item>
/// <item><b>B</b> - <see cref="Deny"/>: World's reader rebuilds the character from the reply or
/// parses state we do not keep. Never answered.</item>
/// <item><b>C</b> - <see cref="RealHandler"/>: the reply carries item / skill / money state World
/// applies. A bare ok would corrupt it; each needs its own handler (T166, layouts in
/// status/PERSISTENCE-MAP.md T165).</item>
/// <item><b>Elsewhere</b> - answered (or deliberately sealed) outside DbProxyHandlers.</item>
/// </list>
/// <para>B and C cannot become generic acks by accident: <see cref="DbAckTable"/> refuses to load
/// a row for any of them, and DbProxyHandlers.OnGenericAck refuses them again. That is what makes
/// T150's regression (a generic ack handing World a zeroed character) unreachable.</para>
/// <para>Row format: <c>OOOO|NAME|W:line why</c> - W is the World reader, WorldServer.exe.c line.</para>
/// </summary>
public static class DbAckGroups
{
    public enum Group { None, Handled, A, B, C, Elsewhere }

    private static readonly string[] DenyRows =
    {
        "2712|TBA_GET_USER_RECORDS|W:3023244 TBA (hero-arena account) - no TBA data here",
        "2723|VENDINGMACHINE_LOAD|W:3029113 vending machine - money, sell/buy lists and inventories we do not keep",
        "272A|VENDINGMACHINE_DESTROY|W:3029070 vending machine - money, sell/buy lists and inventories we do not keep",
        "27EE|VENDINGMACHINE_SET|W:3029269 vending machine - money, sell/buy lists and inventories we do not keep",
        "27F0|VENDINGMACHINE_BUYSELL|W:3028414 vending machine - money, sell/buy lists and inventories we do not keep",
        "27F2|VENDINGMACHINE_BUY_SET|W:3028721 vending machine - money, sell/buy lists and inventories we do not keep",
        "27F4|VENDINGMACHINE_BUY_EX|W:3028570 vending machine - money, sell/buy lists and inventories we do not keep",
        "27F6|VENDINGMACHINE_CLEAR|W:3029012 vending machine - money, sell/buy lists and inventories we do not keep",
        "284A|COUPON_LIST|W:3011147 coupons - no coupon table",
        "284C|USE_COUPON|W:3028180 coupons - no coupon table",
        "284E|DELETE_COUPON|W:3012120 coupons - no coupon table",
        "2874|VENDINGMACHINE_CALCULATE_SELLER|W:3028945 vending machine - money, sell/buy lists and inventories we do not keep",
        "2876|VENDINGMACHINE_CALCULATE_BUYER|W:3028872 vending machine - money, sell/buy lists and inventories we do not keep",
        "287A|OPERATOR_TERRITORY_NPC_KILL|W:1732449 operator territory - territory/NPC respawn state (no DlmId)",
        "289F|TUTORIAL_END|W:3025064 reply hands World UserData + InvenInfo: the character is rebuilt from it (T150 suspect)",
        "28A5|INSERT_FEUDAL_LORD_FLAG|W:3014784 feudal lord flag - FlagKey / KillConfirmed only a real table can give",
        "28A7|DECREASE_FEUDAL_LORD_FLAG_LIFE_TIME|W:3012018 feudal lord flag - FlagKey / KillConfirmed only a real table can give",
        "28E1|INCREMENT_CHARACTER_EXP|W:3014014 level family: on ok World commits exp/level itself; pinned unanswered by T150b, only 0x28D9/DB/DD have T162 rows",
        "28FA|DO_LIMITED_GACHA_WORK|W:3013032 gacha - win/broadcast/left-count computed by the DB",
        "2902|LOAD_MASSTIGE_INFO|W:3017371 masstige - no masstige state",
        "2906|RESET_MASSTIGE_STATUS|W:3021416 masstige - no masstige state",
        "2914|UPDATE_PROMOTION|W:3025921 promotions (0x147D is served static; stale data crashes NewPromotion)",
        "2916|LOAD_PROMOTION_CONDITION_LIST|W:3017589 promotions (0x147D is served static; stale data crashes NewPromotion)",
        "2918|CREATE_PROMOTION_CONDITION|W:3011932 promotions (0x147D is served static; stale data crashes NewPromotion)",
        "291A|UPDATE_PROMOTION_CONDITION|W:3025962 promotions (0x147D is served static; stale data crashes NewPromotion)",
        "291C|DELETE_PROMOTION|W:3012475 promotions (0x147D is served static; stale data crashes NewPromotion)",
        "291E|DELETE_PROMOTION_CONDITION|W:3012516 promotions (0x147D is served static; stale data crashes NewPromotion)",
        "2948|BUILD_GUILD_TOWER|W:3009747 Civil Unrest towers - tower/league state beyond T154 city_guild; acking a build alone makes a tower no load returns",
        "294F|LOAD_CITY_TOWER_AND_PLAYER_INFO|W:3016410 Civil Unrest towers - tower/league state beyond T154 city_guild; acking a build alone makes a tower no load returns",
        "2950|DELETE_GUILD_TOWER|W:3012161 Civil Unrest towers - tower/league state beyond T154 city_guild; acking a build alone makes a tower no load returns",
        "2956|CREATE_NEW_CITY_WAR_LEAGUE|W:3011806 city war league create - LeagueId/SeasonId and league rows",
        "29A0|OPERATOR_TERRITORY_NPCTEMPLATE_RESPAWN|W:1732358 operator territory - territory/NPC respawn state (no DlmId)",
        "29A1|OPERATOR_TERRITORY_NPCPARTY_RESPAWN|W:1732270 operator territory - territory/NPC respawn state (no DlmId)",
        "29A2|REQUEST_CHECK_CITYWAR_ENTER_GUILD_QUEST_POINT|W:3020973 Civil Unrest towers - tower/league state beyond T154 city_guild; acking a build alone makes a tower no load returns",
        "29A4|REQUEST_HERO_DATA|W:3021145 hero / hero skin (TBA) - no hero data here",
        "29A6|REGISTER_HERO|W:3020807 hero / hero skin (TBA) - no hero data here",
        "29A8|DELETE_HERO|W:3012309 hero / hero skin (TBA) - no hero data here",
        "29AA|REQUEST_HEROSKIN_DATA|W:3021102 hero / hero skin (TBA) - no hero data here",
        "29AC|REGISTER_HEROSKIN|W:3020848 hero / hero skin (TBA) - no hero data here",
        "29AE|DELETE_HEROSKIN|W:3012350 hero / hero skin (TBA) - no hero data here",
        "29B0|TBA_UPDATE_BATTLEFIELD_REWARD_COUNT|W:3023898 TBA (hero-arena account) - no TBA data here",
        "29B2|TBA_RESET_BATTLEFIELD_REWARD_COUNT|W:3023771 TBA (hero-arena account) - no TBA data here",
        "29B5|TBA_CHANGE_ACCOUNT_LEVEL|W:3023070 TBA (hero-arena account) - no TBA data here",
        "29B7|REQUEST_TBAUSER_DATA|W:3021227 TBA (hero-arena account) - no TBA data here",
        "29BA|CHANGE_HERO|W:3010318 hero / hero skin (TBA) - no hero data here",
        "29BC|REQUEST_TBA_STORE_BUY_ITEM|W:3021272 TBA (hero-arena account) - no TBA data here",
        "29BE|TBA_USER_ENTERWORLD|W:3024230 TBA; reply hands World a whole UserData blob (reload)",
        "29C2|TBA_REQUEST_BATTLEPASS_DATA|W:3023681 TBA (hero-arena account) - no TBA data here",
        "29C4|TBA_UPDATE_BATTLEPASS_LEVEL|W:3023984 TBA (hero-arena account) - no TBA data here",
        "29C6|TBA_RECEIVE_BATTLEPASS_REWARD|W:3023531 TBA (hero-arena account) - no TBA data here",
        "29C8|TBA_RECEIVE_BATTLEPASS_MISSION_REWARD|W:3023488 TBA (hero-arena account) - no TBA data here",
        "29CA|TBA_UPDATE_BATTLEPASS_MISSION_COUNT|W:3024027 TBA (hero-arena account) - no TBA data here",
        "29CC|TBA_RESET_DAILY_BATTLEPASS_MISSION|W:3023814 TBA (hero-arena account) - no TBA data here",
        "29CE|TBA_UPDATE_BATTLEPASSTYPE|W:3023941 TBA (hero-arena account) - no TBA data here",
        "29D0|TBA_UPDATE_BATTLEPASS_TOKEN_AMOUNT|W:3024128 TBA (hero-arena account) - no TBA data here",
        "29D3|TBA_REQUEST_RUNE_DATA|W:3023726 TBA (hero-arena account) - no TBA data here",
        "29D5|TBA_REGISTER_RUNE|W:3023640 TBA (hero-arena account) - no TBA data here",
        "29D7|TBA_CHANGE_USING_RUNEPAGE|W:3023156 TBA (hero-arena account) - no TBA data here",
        "29D9|TBA_CHANGE_RUNEPAGENAME|W:3023113 TBA (hero-arena account) - no TBA data here",
        "29DB|TBA_ADD_RUNEPAGE|W:3023014 TBA (hero-arena account) - no TBA data here",
        "29DD|TBA_SAVE_RUNEPAGE|W:3023857 TBA (hero-arena account) - no TBA data here",
        "29DF|TBA_CLEAR_RUNEPAGE|W:3023199 TBA (hero-arena account) - no TBA data here",
    };

    private static readonly string[] RealHandlerRows =
    {
        "2717|ADD_SERVANT|W:3009167 servant created: ServantDbId, type, template, name, skills, period list, atoms",
        "2721|SERVANT_ADVENTURE_RECEIVE_REWARD|W:3021911 atoms echoed + i32@13 World reads",
        "2746|SET_MONEY|W:3022071 [DlmId@6][ok@0A][CurrentMoney i64@0B] - World sets the money from it",
        "2747|GET_MONEY|W:3013530 [DlmId@6][ok@0A][CurrentMoney@0B]; World reader is a no-op",
        "2750|EQUIP_PARTNER_STYLE_ITEM|W:1147676 StoreBinary echoed + Error@13 (+ WareCommision)",
        "2752|UNEQUIP_PARTNER_STYLE_ITEM|W:1147925 StoreBinary echoed + Error@13 (+ WareCommision)",
        "275A|ITEM_EXTRACT|W:3015236 atoms echoed: [ref@6 -> 856-B atoms][DlmId@0E][ok@12]; the Arbiter runs ExecTrans and writes back the SAME vector - needs the item-upgrade atom ops modelled",
        "275C|ITEM_DECOMPOSE|W:3014978 World reader (no read, returns false) - any reply is refused; the Arbiter handler is a stub. Never answer. LEFT (T168): dead on both sides",
        "276C|ITEM_DELIVER|W:3015060 two atom lists (owner, target) - the target is another character",
        "276E|ITEM_ENCHANT|W:3015122 atoms echoed: [ref@6 -> 856-B atoms][DlmId@0E][ok@12]; the Arbiter runs ExecTrans and writes back the SAME vector - needs the item-upgrade atom ops modelled",
        "2770|ITEM_ENCHANT_IDENTIFY|W:3015178 atoms echoed: [ref@6 -> 856-B atoms][DlmId@0E][ok@12]; the Arbiter runs ExecTrans and writes back the SAME vector - needs the item-upgrade atom ops modelled",
        "2772|ITEM_CUSTOMIZING|W:3014886 [ref@6 atoms][DlmId@0E][UpdateType@12 echo][ok@16]; atoms echoed",
        "2774|ITEM_MERGE|W:3015406 [DlmId@6][ok@0A]; request atoms (ref@6) must be applied - merge ops not modelled",
        "2790|USER_LEARN_SKILL_FOR_MULTIPLE|W:3027339 atoms + SkillPeriodList + AddLearnSkillResult lists",
        "2798|USER_LEARN_HIDE_PASSIVE_SKILL|W:3027223 [ref@6 ResultList (id,bool) pairs][DlmId@0E][ok@12]",
        "27B5|RESET_EXTRA_POINT_DATA|W:3021373 [DlmId@6][ok@0A]; EP pages/points live in characters.ep_* (T77) and must change with it",
        "27B7|CHANGE_GOLD_CONSUMPTION|W:3010275 [DlmId@6][ok@0A]; the gold total must change",
        "27BF|USER_INCREASE_EP_POINT_BY_ITEM|W:3027138 [DlmId@6][ok@0A]; EP pages/points live in characters.ep_* (T77) and must change with it",
        "27DB|ADD_GUILDMEMBER2|W:3009072 reads DlmId@6 ok@0A; reply [DlmId@6][ok@0A] (11B); guild membership is in our guild store - add the member first (walk-found)",
        "27E6|USER_CLEAR_ALL_SKILL|W:3026833 [ref SkillLearned][ref PassiveLearned][DlmId@16][ok@1A]; T170: both arrays come back zeroed (cap_clearallskill 903, byte-exact)",
        "2804|DELETE_USER_ACHIEVEMENT|W:3012761 AchievementList",
        "2815|GROUP_DUEL_RETURN|W:3013731 atoms + SentAllByParcel@17",
        "2821|TRADE_BROKER_START_DEAL|W:3024674 no DlmId (never wedges); reply names, item and prices of the deal (76B) from the broker store (walk-found)",
        "2824|TRADE_BROKER_CANCEL_DEAL|W:3024558 no DlmId (never wedges); reply [UserDbId@6] (10B) (walk-found)",
        "282F|SIMULATE_ITEM_TOOLTIP|W:3022673 the answer to the Arbiter's own 0x282E ask (no DlmId): T169 sends the compare tooltip (ArbiterClientHandlers.OnSimulateItemTooltip)",
        "2835|LOAD_USER_VIP_INFO|W:3019662 VIP exp/pub exp/tokens/slot list",
        "2839|BUY_VIP_STORE_ITEM|W:3009788 store purchase: 568-B GiveTake records echoed (as 0x27CD) + the point/currency the DB deducts",
        "283B|ADD_VIP_GAME_EXP|W:3009316 [DlmId@6][ok@0A][NewResult i32@0B] - World sets VIP exp to the new TOTAL",
        "2850|RIGHT_ITEM_LIST|W:3021798 right-item list",
        "2852|USE_RIGHT_ITEM|W:3028244 right-item list",
        "2858|ASK_CHANGE_GUILD_NAME|W:3009629 reads DlmId@6 ok@0A; reply [DlmId@6][ok@0A][ErrorType@0B] (15B); the uniqueness verdict comes from the guild store (walk-found)",
        "285E|CHANGE_ACCESSORY_TRANSFORM|W:3009845 ItemChange atom + atoms",
        "286B|UPDATE_DUNGEON_RANK_RECORD|W:3025439 no DlmId ([ok@6]); World reader is a no-op - persistence only (ranking boards)",
        "289B|ITEM_POINT_STORE|W:3015446 store purchase: 568-B GiveTake records echoed (as 0x27CD) + the point/currency the DB deducts",
        "289D|ITEM_GUILD_STORE|W:3015350 store purchase: 568-B GiveTake records echoed (as 0x27CD) + the point/currency the DB deducts",
        "28A1|ITEM_UNIDENTIFY|W:3015847 atoms echoed: [ref@6 -> 856-B atoms][DlmId@0E][ok@12]; the Arbiter runs ExecTrans and writes back the SAME vector - needs the item-upgrade atom ops modelled",
        "28AB|POLITICS_POINT_STORE|W:3020213 store purchase: 568-B GiveTake records echoed (as 0x27CD) + the point/currency the DB deducts",
        "28AE|MARK_AS_QUEST_COMPLETED|W:3019850 [ref@6 CompleteQuestList][DlmId@0E] - the quests NEWLY completed (already-complete ones are dropped)",
        "28C3|INITIALIZE_LEFT_COOL_TIME_PREMIUM_SLOT|W:3014229 [DlmId@6][ok@0A][SlotSetId@0B][SlotPos@0F] - request fields echoed",
        "28C7|UPDATE_REDUCE_SERVANT_PERIOD|W:3026115 period lists",
        "28CB|UPDATE_REDUCE_SKILLPERIOD|W:3026173 period lists",
        "28EE|SHARED_ACCOUNT_DATA|W:1643095 [DlmId@6][SharedTaskType@0A echo][ok@0E] + request atoms",
        "28F4|ENCHANT_ITEM_BOOST|W:3013079 atoms echoed: [ref@6 -> 856-B atoms][DlmId@0E][ok@12]; the Arbiter runs ExecTrans and writes back the SAME vector - needs the item-upgrade atom ops modelled",
        "28F6|UPDATE_ITEM_CUSTOMEXITEM|W:3025708 [ref@6 atoms echoed][DlmId@0E][ok@12]; World reads only DlmId/ok, but the atoms change the custom-ex item",
        "2900|LOAD_LIMITED_DROP_POINT|W:3017237 limited-drop info/gauge lists",
        "290A|LOAD_ADDITIONAL_FATIGUEPOINT|W:3016121 [ok@6][DlmId@7][AdditionalPoint@0B]",
        "2920|ITEM_DECOMPOSITION|W:3015003 atoms echoed: [ref@6 -> 856-B atoms][DlmId@0E][ok@12]; the Arbiter runs ExecTrans and writes back the SAME vector - needs the item-upgrade atom ops modelled",
        "2928|ITEM_FLOATING_CASTLE_PASTS_STORE|W:3015292 store purchase: 568-B GiveTake records echoed (as 0x27CD) + the point/currency the DB deducts",
        "292A|OPEN_FLOATING_CASTLE_PARTS_STORE|W:3020065 PartsItemList",
        "292E|UPDATE_CUSTOMIZING_COMBINE_RESULT|W:3025211 atoms echoed: [ref@6 -> 856-B atoms][DlmId@0E][ok@12]; the Arbiter runs ExecTrans and writes back the SAME vector - needs the item-upgrade atom ops modelled",
        "2932|ITEM_AWAKEN|W:3014830 atoms echoed: [ref@6 -> 856-B atoms][DlmId@0E][ok@12]; the Arbiter runs ExecTrans and writes back the SAME vector - needs the item-upgrade atom ops modelled",
        "2934|ITEM_UNBIND|W:3015791 atoms echoed: [ref@6 -> 856-B atoms][DlmId@0E][ok@12]; the Arbiter runs ExecTrans and writes back the SAME vector - needs the item-upgrade atom ops modelled",
        "2940|ADMIN_USER_DAILY_ATTENDANCE|W:3009359 [DlmId@6][ok@0A][AttendBitmap i64@0B]",
        "294A|CHECK_PLAYTIME_REWARD|W:3010611 [DlmId@6][ok@0A][EventId][ItemTid][ItemCount]",
        "295F|EQUIPMENT_INHERITANCE|W:3013179 atoms echoed: [ref@6 -> 856-B atoms][DlmId@0E][ok@12]; the Arbiter runs ExecTrans and writes back the SAME vector - needs the item-upgrade atom ops modelled",
        "2961|OPEN_DUAL_OPTION|W:3020009 atoms echoed: [ref@6 -> 856-B atoms][DlmId@0E][ok@12]; the Arbiter runs ExecTrans and writes back the SAME vector - needs the item-upgrade atom ops modelled",
        "2963|CHANGE_DUAL_OPTION_IDX|W:3010033 atoms echoed: [ref@6 -> 856-B atoms][DlmId@0E][ok@12]; the Arbiter runs ExecTrans and writes back the SAME vector - needs the item-upgrade atom ops modelled",
        "2969|UPDATE_EVENTSYSTEM_PROGRESS|W:3025455 no DlmId; World re-reads its progress list from the reply; T170: stored per (event, user, account), decompile-derived",
        "296B|ALCHEMY|W:3009404 atoms echoed: [ref@6 -> 856-B atoms][DlmId@0E][ok@12]; the Arbiter runs ExecTrans and writes back the SAME vector - needs the item-upgrade atom ops modelled",
        "296D|CHANGE_EQUIPMENT_EXP|W:3010175 atoms echoed: [ref@6 -> 856-B atoms][DlmId@0E][ok@12]; the Arbiter runs ExecTrans and writes back the SAME vector - needs the item-upgrade atom ops modelled",
        "2971|SKILL_POLISHING_UNLOCK_OPTION|W:3022830 [DlmId@6][ok@0A]; skill-polishing level/exp/options persist here and come back in LOAD_SKILL_POLISHING",
        "2973|SKILL_POLISHING_CHANGE_OPTION|W:3022787 [DlmId@6][ok@0A]; skill-polishing level/exp/options persist here and come back in LOAD_SKILL_POLISHING",
        "2975|LOAD_SKILL_POLISHING|W:3018519 [ref][..][DlmId@0E][ok@1A][Point][Level][Exp]... - the polishing state",
        "2977|SKILL_POLISHING_UPGRADE_LEVEL|W:3022873 [DlmId@6][ok@0A]; skill-polishing level/exp/options persist here and come back in LOAD_SKILL_POLISHING",
        "2979|SKILL_POLISHING_ADD_EXP|W:3022744 [DlmId@6][ok@0A]; skill-polishing level/exp/options persist here and come back in LOAD_SKILL_POLISHING",
        "2981|LOAD_PURCHASE_LIMIT|W:3017704 purchase-limit list",
        "298E|CREATE_CARD_INFO|W:3011276 card / collection book state",
        "2990|INCREASE_CARD_PRESET|W:3013870 card / collection book state",
        "2992|ACTIVATE_CARD_COMBINE_LIST|W:3009029 card / collection book state",
        "2994|DEACTIVATE_CARD_COMBINE_LIST|W:3011975 card / collection book state",
        "2996|RECEIVE_COLLECTION_BOOK_REWARD|W:3020271 card / collection book state",
        "2998|CHANGE_CARD_PRESET|W:3009911 card / collection book state",
        "299A|EXPAND_EP_PAGE|W:3013358 [DlmId@6][ok@0A]; EP pages/points live in characters.ep_* (T77) and must change with it",
        "299C|CHANGE_EP_PAGE|W:3010134 [DlmId@6][ok@0A]; EP pages/points live in characters.ep_* (T77) and must change with it",
        "299E|REQUEST_GUILD_QUEST_WEEKLY_REWARD_ITEM_TRANSACTION|W:3021044 RewardTransactions + IsSuccess",
    };

    /// <summary>Twins the name search missed because another class answers them.</summary>
    private static readonly string[] ElsewhereRows =
    {
        "2754|MOVE_WAREHOUSE_ITEM|sealed one-way (WorldReplayTable): the real Arbiter handler is a stub, T42",
        "2785|CHECK_NEW_GUILD_NAME|needs no answer: World reader :3010595 is a no-op; GuildHandlers checks names",
        "27A7|LOAD_TELEPORT_TO_POS_LIST|replay table, login load (DispatchOnlyForTests)",
        "2809|FETCH_THROUGH_ARBITER_CONTRACT|ContractBroker via WorldBridge (T60)",
        "280C|ASK_THROUGH_ARBITER_CONTRACT|ContractBroker via WorldBridge (T60)",
        "280E|SEND_END_THROUGH_ARBITER_CONTRACT|ContractBroker via WorldBridge (T60)",
        "2833|LOAD_USER_RESTRICTION|replay table, login load (DispatchOnlyForTests)",
        "2895|LOAD_BATTLE_FIELD_LIST|replay table, login load (DispatchOnlyForTests)",
        "28BB|LOAD_ACCOUNT_BENEFIT|real account-backed loader; T181 operator experiment",
        "28C5|LOAD_SERVANT_PERIOD|replay table, login load (DispatchOnlyForTests)",
        "28C9|LOAD_SKILLPERIOD|replay table, login load (DispatchOnlyForTests)",
        "28CF|LOAD_LEARNED_SOCIAL|replay table, login load (DispatchOnlyForTests)",
        "28FE|LOAD_TOKEN_EXCHANGE|replay table, login load (DispatchOnlyForTests)",
        "2912|LOAD_PROMOTION_LIST|replay table, login load (DispatchOnlyForTests)",
        "2922|LOAD_PASSIVITY_COOLTIME|replay table, login load (DispatchOnlyForTests)",
        "2958|CHANGE_CITY_WAR_STATE|sealed one-way (WorldReplayTable, CLAUDE.md section 3)",
    };

    // T165's classification, fixed. Static data only: DbAckTable's own static initialiser reads
    // Refused, so nothing here may call back into DbProxyHandlers / DbAckTable at load time.
    /// <summary>B as T165 classified it. Value = the reason.</summary>
    public static readonly IReadOnlyDictionary<ushort, string> DenySpec = Load(DenyRows);
    /// <summary>C as T165 classified it - the T166 spec. Value = World's reader and what the handler must supply.</summary>
    public static readonly IReadOnlyDictionary<ushort, string> RealHandlerSpec = Load(RealHandlerRows);
    /// <summary>Elsewhere as T165 found it. Value = which class answers it.</summary>
    public static readonly IReadOnlyDictionary<ushort, string> ElsewhereSpec = Load(ElsewhereRows);

    // What is still pending: the classification minus every opcode the allow-list now answers
    // with a dedicated handler. Read at call time, so registering a real handler (T164, T166, ...)
    // needs no edit here.
    /// <summary>B still unanswered.</summary>
    public static IReadOnlyDictionary<ushort, string> Deny => Pending(DenySpec);
    /// <summary>C still waiting for its handler.</summary>
    public static IReadOnlyDictionary<ushort, string> RealHandler => Pending(RealHandlerSpec);
    /// <summary>Still answered (or sealed) outside DbProxyHandlers.</summary>
    public static IReadOnlyDictionary<ushort, string> Elsewhere => Pending(ElsewhereSpec);
    /// <summary>"Real handler on master": classified B, C or Elsewhere by T165, answered now by a
    /// dedicated case in <see cref="DbProxyHandlers.IsHandledRequest"/>.</summary>
    public static IReadOnlyDictionary<ushort, string> Landed =>
        DenySpec.Concat(RealHandlerSpec).Concat(ElsewhereSpec)
                .Where(kv => HasRealHandler(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value);

    /// <summary>True for everything T165 put in B or C, handled since or not: no generic ack may
    /// ever answer these (a real handler may).</summary>
    public static bool Refused(ushort op) => DenySpec.ContainsKey(op) || RealHandlerSpec.ContainsKey(op);

    /// <summary>A dedicated handler - allow-listed and not by a DbAckTable row.</summary>
    public static bool HasRealHandler(ushort op) => DbProxyHandlers.IsHandledRequest(op) && !DbAckTable.Covers(op);

    /// <summary>The group of a request opcode; None when it is in no group (the walk test fails).</summary>
    public static Group Of(ushort op)
    {
        if (DbAckTable.Covers(op)) return Group.A;
        if (DbProxyHandlers.IsHandledRequest(op)) return Group.Handled;
        if (DenySpec.ContainsKey(op)) return Group.B;
        if (RealHandlerSpec.ContainsKey(op)) return Group.C;
        if (ElsewhereSpec.ContainsKey(op)) return Group.Elsewhere;
        return Group.None;
    }

    private static IReadOnlyDictionary<ushort, string> Pending(IReadOnlyDictionary<ushort, string> spec)
        => spec.Where(kv => !HasRealHandler(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value);

    private static Dictionary<ushort, string> Load(string[] rows)
    {
        var d = new Dictionary<ushort, string>();
        foreach (var row in rows)
        {
            var p = row.Split('|', 3);
            if (p.Length != 3 || p[0].Length != 4) throw new FormatException(row);
            ushort op = ushort.Parse(p[0], System.Globalization.NumberStyles.HexNumber);
            if (DbProxyOpcodeNames.Name(op) != "SDB_" + p[1])
                throw new FormatException($"{row}: 0x{op:X4} is {DbProxyOpcodeNames.Name(op) ?? "unnamed"}");
            d.Add(op, p[1] + " - " + p[2]);
        }
        return d;
    }
}
