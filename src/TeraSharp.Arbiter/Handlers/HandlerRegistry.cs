using Microsoft.Extensions.Logging;
using TeraSharp.Arbiter.Network;
using TeraSharp.Arbiter.Protocol;
using TeraSharp.Arbiter.World;

namespace TeraSharp.Arbiter.Handlers;

public static class HandlerRegistry
{
    public static void RegisterAll(
        PacketDispatcher dispatcher,
        OpcodeTable opcodes,
        DefinitionRegistry defs,
        ILoggerFactory loggerFactory)
    {
        var log = loggerFactory.CreateLogger("HandlerRegistry");
        var noopLog = loggerFactory.CreateLogger("Noop");

        void Reg(string name, int minLen, PacketHandler handler)
        {
            if (opcodes.TryGetCode(name, out ushort op))
                dispatcher.Register(op, name, minLen, handler);
            else
                log.LogError("{Name} not in opcode map - handler not registered", name);
        }

        // Forward a framed client packet to WorldServer.
        static void Forward(GameSession s, ushort op, ReadOnlyMemory<byte> body)
        {
            var pkt = new byte[body.Length + 4];
            pkt[0] = (byte)pkt.Length; pkt[1] = (byte)(pkt.Length >> 8);
            pkt[2] = (byte)op; pkt[3] = (byte)(op >> 8);
            body.Span.CopyTo(pkt.AsSpan(4));
            s.ForwardToWorld(pkt);
        }

        // No-reply when standalone; forwarded to World when in-world.
        void RegNoop(string name)
        {
            if (!opcodes.TryGetCode(name, out ushort op)) return;
            dispatcher.Register(op, name, 0, (s, body) =>
            {
                if (s.InWorld) { Forward(s, op, body); return true; }
                noopLog.LogTrace("{Name} received ({Len} bytes) - no reply", name, body.Length);
                return true;
            });
        }

        // Empty reply when standalone; forwarded to World when in-world.
        void RegEmptyReply(string clientPacket, string serverPacket, Dictionary<string, object>? fields = null)
        {
            if (!opcodes.TryGetCode(clientPacket, out ushort op)) return;
            dispatcher.Register(op, clientPacket, 0, (s, body) =>
            {
                if (s.InWorld) { Forward(s, op, body); return true; }
                s.SendByDef(serverPacket, fields ?? new Dictionary<string, object>());
                return true;
            });
        }

        // --- Login chain (always Arbiter-owned) ---
        var checkVersion = new CheckVersionHandler(loggerFactory.CreateLogger<CheckVersionHandler>());
        Reg("C_CHECK_VERSION", CheckVersionHandler.MinBodyLength, checkVersion.Handle);

        var login = new LoginHandlers(loggerFactory.CreateLogger<LoginHandlers>());
        Reg("C_LOGIN_ARBITER", 4, login.OnLoginArbiter);
        Reg("C_GET_USER_LIST", 0, login.OnGetUserList);
        Reg("C_SELECT_USER", 4, login.OnSelectUser);

        // C_LOAD_TOPO_FIN: standalone spawns fake world; in-world forwards to World.
        Reg("C_LOAD_TOPO_FIN", 0, (s, body) =>
        {
            if (s.InWorld)
            {
                // Arbiter-owned: tell World the player finished loading (0x1439), triggers spawn.
                // Real Arbiter sends AS_UPDATE_VISITED_SECTION_LIST (0x1439) before 0x1390/0x138F on every
                // C_LOAD_TOPO_FIN (first spawn AND after a zone change). T45: built from the visited_sections
                // rows - the hard-coded empty list told World "nothing explored" on every relog.
                s.Send(ArbiterClientHandlers.BuildVisitedSectionListFor(s));   // T75/T79: FIRST in the burst, as frame 257 - S_VISITED_SECTION_LIST from visited_sections (stops the intro cutscene replaying)
                s.Send(ArbiterClientHandlers.BuildCrestInfoFor(s));            // T83: crest points + learned crests (frames 304/314)
                s.Send(ArbiterClientHandlers.BuildChangeCardPreset());         // T83: card preset (frames 329/375)
                if (GmCommandHandlers.LevelOf(s, Program.Store) >= 1)   // T107: the real server sends these only to GMs
                {
                    s.Send(ArbiterClientHandlers.BuildAdminHoldCharacter());       // T89: frame 402
                    // S_ADMIN_GM_SKILL moved to the S_LOGIN burst (T120: frame 99, before S_LOAD_TOPO - the Alt+A gate)
                }
                s.Send(ArbiterClientHandlers.BuildPvpLeaderBoardInfo());       // T91: frame 288
                s.Send(ArbiterClientHandlers.BuildPveLeaderBoardInfo());       // T91: frame 289
                Program.World?.SendFrame(WorldBridge.OpUpdateVisitedSection,
                    ArbiterClientHandlers.BuildUpdateVisitedSectionList(s.PlayerId,
                        Program.Store?.GetVisitedSections((int)s.SelectedCharacter!.Id)
                            ?? Array.Empty<TeraSharp.Arbiter.Persistence.CharacterStore.VisitedSection>()));
                Program.World?.NotifyTopoLoaded(s.PlayerId);
                // Real Arbiter sends these to the client right after C_LOAD_TOPO_FIN (lobby_proxy.log
                // 263-268), in this order. S_LOAD_CLIENT_USER_SETTING here is what makes the chat
                // window process S_CHAT; sent at character select it is discarded on zone load.
                SocialHandlers.SendBlockList(s);
                SocialHandlers.SendFriendGroupList(s);
                SocialHandlers.SendFriendList(s);
                SocialHandlers.SendUpdateFriendInfo(s);      // real Arbiter sends it too (cap_newchar_client frame 307)
                ClientSettingsHandlers.SendAccountSetting(s);
                ClientSettingsHandlers.SendUserSetting(s);
                s.Send(ArbiterClientHandlers.BuildVersionInfo());   // T131: cap frame 286, before the parcel status
                ParcelHandlers.SendReadRecvStatus(s);        // T42: 13-byte S_PARCEL_READ_RECV_STATUS (cap frame 312)
                return true;
            }
            return login.OnLoadTopoFin(s, body);
        });

        var character = new CharacterHandlers(loggerFactory.CreateLogger<CharacterHandlers>());
        Reg("C_CAN_CREATE_USER", 0, character.OnCanCreateUser);
        Reg("C_CHECK_USERNAME", 0, character.OnCheckUsername);
        Reg("C_CREATE_USER", 0, character.OnCreateUser);
        Reg("C_DELETE_USER", 0, character.OnDeleteUser);
        Reg("C_CANCEL_DELETE_USER", CharacterHandlers.CancelDeleteUserBodySize, character.OnCancelDeleteUser);   // T88

        // --- Chat: Arbiter-owned in both modes (real Arbiter handles chat too) ---
        var chat = new ChatHandlers(loggerFactory.CreateLogger<ChatHandlers>());
        Reg("C_CHAT", 4, chat.OnChat);

        // --- Social: friends, blocks, whisper (Arbiter-owned) ---
        var social = new SocialHandlers(loggerFactory.CreateLogger<SocialHandlers>());
        Reg("C_ADD_FRIEND", 4, social.OnAddFriend);
        Reg("C_DELETE_FRIEND", 4, social.OnDeleteFriend);
        Reg("C_BLOCK_USER", 4, social.OnBlockUser);
        Reg("C_REMOVE_BLOCKED_USER", 4, social.OnRemoveBlockedUser);
        Reg("C_WHISPER", 4, social.OnWhisper);
        // T30 - friends, groups, memos, blocks (status/FRIENDS.md)
        Reg("C_ACCEPT_FRIEND", 4, social.OnAcceptFriend);
        Reg("C_UPDATE_FRIEND_INFO", 0, social.OnUpdateFriendInfo);
        Reg("C_ADD_FRIEND_GROUP", 4, social.OnAddFriendGroup);
        Reg("C_EDIT_FRIEND_GROUP", 4, social.OnEditFriendGroup);
        Reg("C_DELETE_FRIEND_GROUP", 4, social.OnDeleteFriendGroup);
        Reg("C_CHANGE_FRIEND_MEMO", 4, social.OnChangeFriendMemo);
        Reg("C_EDIT_BLOCKED_USER_MEMO", 4, social.OnEditBlockedUserMemo);

        // --- GM commands (T32, status/GM-DESIGN.md): the client sends /@name args as C_ADMIN or C_OP_COMMAND ---
        var gm = new GmCommandHandlers(loggerFactory.CreateLogger<GmCommandHandlers>());
        Reg("C_ADMIN", 4, gm.OnAdminCommand);
        Reg("C_OP_COMMAND", 4, gm.OnOpCommand);

        // --- Parcels (T42, status/MAIL-WAREHOUSE.md): the three client packets the Arbiter owns ---
        var parcels = new ParcelHandlers(loggerFactory.CreateLogger<ParcelHandlers>());
        Reg("C_SHOW_PARCEL_MESSAGE", 4, parcels.OnShowParcelMessage);
        Reg("C_PARCEL_READ_RECV_STATUS", 0, parcels.OnParcelReadRecvStatus);
        Reg("C_PARCEL_REPORT", 10, parcels.OnParcelReport);

        // --- Parties (T49, status/PARTY-DESIGN.md section 11): body lengths, not frame lengths ---
        foreach (var (partyName, partyOp) in PartyWiring.ClientOpcodes)
            Reg(partyName, PartyWiring.MinBodyLength(partyOp),
                (s, body) => PartyWiring.OnClientPacket(s, partyOp, body));

        // --- Guilds (T51, status/GUILD-DESIGN.md section 11): the 17 Arbiter-owned guild packets ---
        foreach (var (guildName, guildOp) in GuildWiring.ClientOpcodes)
            Reg(guildName, GuildWiring.MinBodyLength(guildOp),
                (s, body) => GuildWiring.OnClientPacket(s, guildOp, body));

        // --- Trade broker (T55, status/BROKER-DESIGN.md section 8.2): 15 C_ packets
        //     (C_TRADE_BROKER_HIGHEST_ITEM_LEVEL is T45's, registered below) ---
        var broker = new BrokerHandlers(loggerFactory.CreateLogger<BrokerHandlers>());
        foreach (var (brokerName, brokerOp) in BrokerHandlers.ClientOpcodes)
            Reg(brokerName, BrokerHandlers.MinBodyLength(brokerOp),
                (s, body) => broker.Handle(s, brokerOp, body));

        // --- Client settings (Arbiter-owned) ---
        Reg("C_REQUEST_CLIENT_CHAT_OPTION_SETTING", 0, ClientSettingsHandlers.OnRequestChatOption);
        Reg("C_REQUEST_CLIENT_UI_SETTING", 0, ClientSettingsHandlers.OnRequestUiSetting);
        Reg("C_SAVE_CLIENT_CHAT_OPTION_SETTING", 0, ClientSettingsHandlers.OnSaveChatOption);
        Reg("C_SAVE_CLIENT_USER_SETTING", 0, ClientSettingsHandlers.OnSaveUserSetting);
        Reg("C_SAVE_CLIENT_ACCOUNT_SETTING", 0, ClientSettingsHandlers.OnSaveAccountSetting);

        // --- T45: Arbiter-owned client packets World was rejecting (status/CLIENT-REJECTS.md).
        //     Note the "- 4": PacketDispatcher compares BODY length; the *PacketSize constants are totals. ---
        var misc = loggerFactory.CreateLogger("ArbiterClient");
        Reg("C_SHOW_ITEM_TOOLTIP_EX", ArbiterClientHandlers.TooltipRequestBodySize,
            (s, b) => ArbiterClientHandlers.OnShowItemTooltipEx(s, b, misc));
        Reg("C_VISIT_NEW_SECTION", ArbiterClientHandlers.VisitPacketSize - 4,
            (s, b) => ArbiterClientHandlers.OnVisitNewSection(s, b, misc));
        Reg("C_CLIENT_LOG", 0, (s, b) => ArbiterClientHandlers.OnClientLog(s, b, misc));
        Reg("C_SERVER_TIME", 0, (s, b) => ArbiterClientHandlers.OnServerTime(s, b, misc));
        Reg("C_SAVE_CLIENT_UI_SETTING", 0, (s, b) => ArbiterClientHandlers.OnSaveClientUiSetting(s, b, misc));
        Reg("C_TRADE_BROKER_HIGHEST_ITEM_LEVEL", 0,
            (s, b) => ArbiterClientHandlers.OnTradeBrokerHighestItemLevel(s, b, misc));
        // T60: party invites/applies are World-side contracts brokered through the Arbiter; the
        // target's accept/reject comes back as C_REPLY_THROUGH_ARBITER_CONTRACT. C_ADD_TRADE_BAG
        // (58817) is Arbiter-owned and forwards as AS_ADD_TRADE_BAG.
        Reg("C_REPLY_THROUGH_ARBITER_CONTRACT", ContractBroker.ReplyMinBodyLength,
            (s, b) => ContractBroker.OnClientReply(s, b, misc));
        Reg("C_ADD_TRADE_BAG", ArbiterClientHandlers.AddTradeBagBodySize,
            (s, b) => ArbiterClientHandlers.OnAddTradeBag(s, b, misc));
        // T62: friend right-click menu, cutscene-seen flags, name completion
        Reg("C_ASK_INTERACTIVE", ArbiterClientHandlers.AskInteractiveBodySize, (s, b) => ArbiterClientHandlers.OnAskInteractive(s, b, misc));
        Reg("C_WATCHED_MOVIES",  ArbiterClientHandlers.WatchedMoviesBodySize,  (s, b) => ArbiterClientHandlers.OnWatchedMovies(s, b, misc));
        Reg("C_FINDNAME",        ArbiterClientHandlers.FindNameBodySize,       (s, b) => ArbiterClientHandlers.OnFindName(s, b, misc));
        ArbiterClientHandlers.PartyLookup = id => PartyWiring.Manager.FindByMember(id) != null;
        // T82: the skill-polishing panel (all-zero forms, cap_social4_client2 145/146).
        // C_GET_USER_GUILD_LOGO is already registered by GuildWiring (T51).
        Reg("C_RQ_SKILL_POLISHING_LIST", 0, (s, b) => ArbiterClientHandlers.OnRqSkillPolishingList(s, b, misc));
        Reg("C_RQ_SKILL_POLISHING_EXP_INFO", 0, (s, b) => ArbiterClientHandlers.OnRqSkillPolishingExpInfo(s, b, misc));
        // T83: cards + guild perks
        Reg("C_REQUEST_MY_ACTIVATE_CARD_COMBINE_LIST_DATA", 0,
            (s, b) => ArbiterClientHandlers.OnRequestMyActivateCardCombineList(s, b, misc));
        Reg("C_REQUEST_OTHERS_ACTIVATE_CARD_COMBINE_LIST_DATA_WITH_GAMEID", 8,
            (s, b) => ArbiterClientHandlers.OnRequestOthersActivateCardCombineList(s, b, misc));
        Reg("C_REQUEST_OTHERS_CARD_DATA_WITH_GAMEID", 8,
            (s, b) => ArbiterClientHandlers.OnRequestOthersCardData(s, b, misc));
        Reg("C_REQUEST_GUILD_PERK_LIST", 0,
            (s, b) => ArbiterClientHandlers.OnRequestGuildPerkList(s, b, misc));
        ArbiterClientHandlers.PlayerIdForGameId = gameId => (int)(Program.World?.PlayerForGameId(gameId)?.SelectedCharacter?.Id ?? 0);
        // T87: telemetry / acks. All Arbiter-owned; never forward (RegNoop would forward in-world).
        Reg("C_PONG", 0, (s,b) => ArbiterClientHandlers.OnPong(s,b,misc));
        Reg("C_CHECK_RTT", ArbiterClientHandlers.CheckRttBodySize, (s,b) => ArbiterClientHandlers.OnCheckRtt(s,b,misc));
        Reg("C_PLAY_TIME", 0, (s,b) => ArbiterClientHandlers.OnPlayTime(s,b,misc));
        Reg("C_REQUEST_PLAYTIME", ArbiterClientHandlers.RequestPlaytimeBodySize, (s,b) => ArbiterClientHandlers.OnRequestPlaytime(s,b,misc));
        Reg("C_REQUEST_PERF", ArbiterClientHandlers.RequestPerfBodySize, (s,b) => ArbiterClientHandlers.OnRequestPerf(s,b,misc));
        Reg("C_SEND_UI_LOG", ArbiterClientHandlers.SendUiLogBodySize, (s,b) => ArbiterClientHandlers.OnSendUiLog(s,b,misc));
        Reg("C_GET_MY_IP", ArbiterClientHandlers.GetMyIpBodySize, (s,b) => ArbiterClientHandlers.OnGetMyIp(s,b,misc));
        Reg("C_XIGNCODE_SECURITY_DATA", ArbiterClientHandlers.XignCodeSecurityDataBodySize, (s,b) => ArbiterClientHandlers.OnXignCodeSecurityData(s,b,misc));
        Reg("C_INVALID_BUILD_VERSION", ArbiterClientHandlers.InvalidBuildVersionBodySize, (s,b) => ArbiterClientHandlers.OnInvalidBuildVersion(s,b,misc));
        Reg("C_REQUEST_LATEST_UPDATE_NOTIFICATION", ArbiterClientHandlers.LatestUpdateNotificationBodySize, (s,b) => ArbiterClientHandlers.OnRequestLatestUpdateNotification(s,b,misc));
        Reg("C_CONFIRM_UPDATE_NOTIFICATION", ArbiterClientHandlers.ConfirmUpdateNotificationBodySize, (s,b) => ArbiterClientHandlers.OnConfirmUpdateNotification(s,b,misc));
        Reg("C_SECOND_PASSWORD_AUTH", ArbiterClientHandlers.SecondPasswordBodySize, (s,b) => ArbiterClientHandlers.OnSecondPassword(s,b,misc));
        Reg("C_SECOND_PASSWORD_REGISTER", ArbiterClientHandlers.SecondPasswordBodySize, (s,b) => ArbiterClientHandlers.OnSecondPassword(s,b,misc));
        Reg("C_REFRESH_API_ACCESS_TOKEN", ArbiterClientHandlers.RefreshApiAccessTokenBodySize, (s,b) => ArbiterClientHandlers.OnRefreshApiAccessToken(s,b,misc));
        Reg("C_CANCEL_EXIT", 0, (s,b) => ArbiterClientHandlers.OnCancelExit(s,b,misc));
        // T89: In-Game Operation Tool (Alt+A) - gated on the GM level inside GmAdminTool
        Reg("C_ADMIN_REQUEST_CUSTOM_BOOKMARK", 4,  (s, b) => GmAdminTool.OnRequestCustomBookmark(s, b, misc));
        Reg("C_ADMIN_REQUEST_DEFAULT_BOOKMARK", 4, (s, b) => GmAdminTool.OnRequestDefaultBookmark(s, b, misc));
        Reg("C_ADMIN_ADD_CUSTOM_BOOKMARK", 22,     (s, b) => GmAdminTool.OnAddCustomBookmark(s, b, misc));
        Reg("C_ADMIN_GMEVENT_STATUS", 0,           (s, b) => GmAdminTool.OnGmEventStatus(s, b, misc));
        Reg("C_ADMIN_CHECK_USERNAME", 14,          (s, b) => GmAdminTool.OnCheckUsername(s, b, misc));
        Reg("C_ADMIN_GET_USER_INFO_BY_DBID", 8,    (s, b) => GmAdminTool.OnGetUserInfoByDbId(s, b, misc));
        Reg("C_ADMIN_GET_USER_INFO_LIST_BY_DISTANCE", 4, (s, b) => GmAdminTool.OnGetUserInfoListByDistance(s, b, misc));
        Reg("C_ADMIN_WARNING_MESSAGE", 6,          (s, b) => GmAdminTool.OnWarningMessage(s, b, misc));
        Reg("C_ADMIN_GM_SKILL", 12,                (s, b) => GmAdminTool.OnGmSkill(s, b, misc));
        Reg("C_ADMIN_REQUEST_USERINFO",   22, (s, b) => GmAdminTool.OnRequestUserInfo(s, b, misc));     // T91
        Reg("C_ADMIN_REQUEST_USERACTION", 18, (s, b) => GmAdminTool.OnRequestUserAction(s, b, misc));   // T91
        Reg("C_REQUEST_SERVER_ADMINTOOL_AWESOMIUM_URL", 0, (s,b) => ArbiterClientHandlers.OnRequestAdminToolUrl(s,b,misc));   // T107: Alt+A waits for this reply
        // T118: the leaderboard and the leftovers (status/LEADERBOARD.md). C_REQUEST_MY_PARTY_MATCH_INFO stays with PartyMatchManager.
        Reg("C_REQUEST_PVE_RANKING",       LeaderboardPackets.RankingBodySize,        (s,b) => LeaderboardPackets.OnRequestPveRanking(s, b, log));
        Reg("C_REQUEST_PVP_RANKING",       LeaderboardPackets.RankingBodySize,        (s,b) => LeaderboardPackets.OnRequestPvpRanking(s, b, log));
        Reg("C_VIEW_INTER_PARTY_MATCH_DUNGEON_LIST",     LeaderboardPackets.NoBodySize, (s,b) => LeaderboardPackets.OnViewInterPartyMatchDungeonList(s, b, log));
        Reg("C_VIEW_INTER_PARTY_MATCH_BATTLEFIELD_LIST", LeaderboardPackets.NoBodySize, (s,b) => LeaderboardPackets.OnViewInterPartyMatchBattlefieldList(s, b, log));
        Reg("C_REQUEST_PARTY_MATCH_INFO_PAGE",  LeaderboardPackets.PartyMatchPageBodySize, (s,b) => LeaderboardPackets.OnRequestPartyMatchInfoPage(s, b, log));
        Reg("C_REQUEST_CHANGE_PARTY_MATCH_RULE",LeaderboardPackets.PartyMatchRuleBodySize, (s,b) => LeaderboardPackets.OnRequestChangePartyMatchRule(s, b, log));
        Reg("C_GROUP_DUEL_RECORD",              LeaderboardPackets.NoBodySize,             (s,b) => LeaderboardPackets.OnGroupDuelRecord(s, b, log));
        Reg("C_CHANGE_USER_NAME",               LeaderboardPackets.ChangeUserNameBodySize, (s,b) => LeaderboardPackets.OnChangeUserName(s, b, log));
        // T99: GM tool tail
        Reg("C_ADMIN_GM_TELEPORT",              GmAdminTool.GmTeleportBodySize,     (s,b) => GmAdminTool.OnGmTeleport(s,b,misc));
        Reg("C_ADMIN_GM_MAPTELEPORT",           GmAdminTool.GmMapTeleportBodySize,  (s,b) => GmAdminTool.OnGmMapTeleport(s,b,misc));
        Reg("C_ADMIN_LOBBY",                    GmAdminTool.AdminLobbyBodySize,     (s,b) => GmAdminTool.OnAdminLobby(s,b,misc));
        Reg("C_ADMIN_REMOVE_NPC",               GmAdminTool.GameIdBodySize,         (s,b) => GmAdminTool.OnAdminRemoveNpc(s,b,misc));
        Reg("C_ADMIN_VANISH_PET",               GmAdminTool.GameIdBodySize,         (s,b) => GmAdminTool.OnAdminVanishPet(s,b,misc));
        Reg("C_ADMIN_GET_DUNGEON_USER_LIST",    GmAdminTool.DungeonIdBodySize,      (s,b) => GmAdminTool.OnAdminGetDungeonUserList(s,b,misc));
        Reg("C_ADMIN_REMOVE_CUSTOM_BOOKMARK",   GmAdminTool.BookmarkIndexBodySize,  (s,b) => GmAdminTool.OnRemoveCustomBookmark(s,b,misc));
        Reg("C_ADMIN_GMEVENT_NOTICE",           GmAdminTool.GmEventNoticeBodySize,  (s,b) => GmAdminTool.OnGmEventNotice(s,b,misc));
        // T99: item strings, boards, previews, trade log, dungeon rank
        Reg("C_SET_ITEM_STRING",        ItemBoardPackets.SetItemStringBodySize,     (s,b) => ItemBoardPackets.OnSetItemString(s,b,misc));
        Reg("C_REWRITE_ITEM_STRING",    ItemBoardPackets.RewriteItemStringBodySize, (s,b) => ItemBoardPackets.OnRewriteItemString(s,b,misc));
        Reg("C_WRITE_BOARD",            ItemBoardPackets.WriteBoardBodySize,        (s,b) => ItemBoardPackets.OnWriteBoard(s,b,misc));
        Reg("C_REQUEST_WRITE_BOARD",    ItemBoardPackets.BoardIdBodySize,           (s,b) => ItemBoardPackets.OnRequestWriteBoard(s,b,misc));
        Reg("C_BOARD_ITEM_LIST",        ItemBoardPackets.BoardIdBodySize,           (s,b) => ItemBoardPackets.OnBoardItemList(s,b,misc));
        Reg("C_PREVIEW_ITEM",           4,                                          (s,b) => ItemBoardPackets.OnPreviewItem(s,b,misc));
        Reg("C_REQUEST_NONDB_ITEM_INFO",ItemBoardPackets.NonDbItemInfoBodySize,     (s,b) => ItemBoardPackets.OnRequestNonDbItemInfo(s,b,misc));
        Reg("C_SHOW_TRADE_LOG",         ItemBoardPackets.PageBodySize,              (s,b) => ItemBoardPackets.OnShowTradeLog(s,b,misc));
        Reg("C_SHOW_TRADE_ITEM",        0,                                          (s,b) => ItemBoardPackets.OnShowTradeItem(s,b,misc));
        Reg("C_REQUEST_IMAGE_DATA",     ItemBoardPackets.ImageIdBodySize,           (s,b) => ItemBoardPackets.OnRequestImageData(s,b,misc));
        Reg("C_DUNGEON_RANK_RECORD_LIST", ItemBoardPackets.RankRecordBodySize,      (s,b) => ItemBoardPackets.OnDungeonRankRecordList(s,b,misc));
        Reg("C_DUNGEON_RANK_SEASON_LIST", ItemBoardPackets.RankSeasonBodySize,      (s,b) => ItemBoardPackets.OnDungeonRankSeasonList(s,b,misc));
        GmAdminTool.OnlineSessions = () => Program.World?.InWorldSessions() ?? new List<GameSession>();
        foreach (var name in new[]
        {
            "C_VIEW_BATTLE_FIELD_RESULT",
            "C_REQUEST_CANDIDATE_LIST", "C_SHOW_AWESOMIUMWEB_SHOP", "C_RESET_ALL_DUNGEON",
            "C_UPDATE_CONTENTS_PLAYTIME", "C_EVENT_GUIDE",
            // T97: real handlers that parse nothing / store nothing / reply nothing
            "C_GET_ATTENDANCE_REWARD", "C_REQUEST_STACK_ATTENDANCE_EVENT_REWARD", "C_EVENT_MATCHING_DUNGEON_DETAIL_INFO",
            "C_REQUEST_COUPON_DATA", "C_REQUEST_RECV_DAILY_TOKEN", "C_QUERY_COIN", "C_REQUEST_CHANGE_PARTY_NAME",
            "C_PARTY_NOTIFY_MY_POSITION", "C_CANCEL_CHANGE_USER_APPEARANCE", "C_CANCEL_PREPARE_CHANGE_USER_APPEARANCE",
            "C_CUSTOM_USER_CUSTOMIZING", "C_SAVE_CHAT_SETTING", "C_UPDATE_SKILL_SCRIPT",
        })
            Reg(name, 0, (s, b) => ArbiterClientHandlers.OnAcceptSilently(s, b, misc));

        // T134: dungeon cool-time window (all 9 live replies are empty). Clear-count needs the 14-id roster merge - T134b.
        Reg("C_DUNGEON_COOL_TIME_LIST", 0, (s, body) => { s.Send(ArbiterClientHandlers.BuildDungeonCoolTimeList()); return true; });
        // --- T97: party extras, event/VIP windows, reports, profile ---
        Reg("C_REQUEST_PARTY_NAME",                     MiscClientPackets.RequestPartyNameBodySize,      (s,b) => MiscClientPackets.OnRequestPartyName(s,b,misc));
        Reg("C_VIEW_PARTY_INVITE",                      0,                                              (s,b) => MiscClientPackets.OnViewPartyInvite(s,b,misc));
        Reg("C_GET_EVENT_DETAIL",                       0,                                              (s,b) => MiscClientPackets.OnGetEventDetail(s,b,misc));
        Reg("C_REQUEST_VIP_SYSTEM_INFO",                0,                                              (s,b) => MiscClientPackets.OnRequestVipSystemInfo(s,b,misc));
        Reg("C_REQUEST_STACK_ATTENDANCE_EVENT_INFO_UPDATE", 0,                                          (s,b) => MiscClientPackets.OnRequestStackAttendanceEventInfoUpdate(s,b,misc));
        Reg("C_EVENT_MATCHING_BATTLEFIELD_DETAIL_INFO", MiscClientPackets.EventMatchingDetailBodySize,  (s,b) => MiscClientPackets.OnEventMatchingBattlefieldDetailInfo(s,b,misc));
        Reg("C_CHAT_REPORT",                            MiscClientPackets.ChatReportBodySize,           (s,b) => MiscClientPackets.OnChatReport(s,b,misc));
        Reg("C_USER_REPORT",                            MiscClientPackets.UserReportBodySize,           (s,b) => MiscClientPackets.OnUserReport(s,b,misc));
        Reg("C_CHANGE_MY_PROFILE",                      MiscClientPackets.ProfileTextBodySize,          (s,b) => MiscClientPackets.OnChangeMyProfile(s,b,misc));
        Reg("C_UPDATE_MY_DESCRIPTION",                  MiscClientPackets.ProfileTextBodySize,          (s,b) => MiscClientPackets.OnUpdateMyDescription(s,b,misc));
        Reg("C_CHANGE_MY_STATE",                        MiscClientPackets.ChangeMyStateBodySize,        (s,b) => MiscClientPackets.OnChangeMyState(s,b,misc));
        Reg("C_LOGIN_WORLD",                            0,                                              (s,b) => MiscClientPackets.OnLoginWorld(s,b,misc));

        // --- Guild board (T95): list/search/paging, wanted board, invite list, level ranking, flag, bank log ---
        Reg("C_REQUEST_GUILD_LIST",                      GuildBoard.RequestGuildListBodySize, (s, b) => GuildBoard.OnRequestGuildList(s, b, misc));
        Reg("C_REQUEST_GUILD_LIST_PAGE",                 GuildBoard.PageBodySize,       (s, b) => GuildBoard.OnRequestGuildListPage(s, b, misc));
        Reg("C_REQUEST_GUILD_LIST_SORT",                 GuildBoard.SortBodySize,       (s, b) => GuildBoard.OnRequestGuildListSort(s, b, misc));
        Reg("C_REQUEST_GUILD_WANTED_WRITING_LIST",       0,                             (s, b) => GuildBoard.OnRequestGuildWantedWritingList(s, b, misc));
        Reg("C_REQUEST_GUILD_WANTED_WRITING_LIST_PAGE",  GuildBoard.PageBodySize,       (s, b) => GuildBoard.OnRequestGuildWantedWritingListPage(s, b, misc));
        Reg("C_REQUEST_SET_GUILD_WANTED_WRITING",        GuildBoard.SetWantedBodySize,  (s, b) => GuildBoard.OnRequestSetGuildWantedWriting(s, b, misc));
        Reg("C_REQUEST_INVITE_GUILD_LIST",               0,                             (s, b) => GuildBoard.OnRequestInviteGuildList(s, b, misc));
        Reg("C_REQUEST_INVITE_GUILD_LIST_PAGE",          GuildBoard.PageBodySize,       (s, b) => GuildBoard.OnRequestInviteGuildListPage(s, b, misc));
        Reg("C_REQUEST_GUILD_LEVEL_RANKING",             GuildBoard.PageBodySize,       (s, b) => GuildBoard.OnRequestGuildLevelRanking(s, b, misc));
        Reg("C_GET_GUILD_WARE_HISTORY",                  GuildBoard.PageBodySize,       (s, b) => GuildBoard.OnGetGuildWareHistory(s, b, misc));
        Reg("C_RECOMMEND_GUILD",                         GuildBoard.RecommendGuildBodySize, (s, b) => GuildBoard.OnRecommendGuild(s, b, misc));
        Reg("C_RECOMMEND_USER_GUILD",                    GuildBoard.RecommendUserBodySize,  (s, b) => GuildBoard.OnRecommendUserGuild(s, b, misc));
        Reg("C_UPDATE_GUILD_FLAG",                       GuildBoard.UpdateFlagBodySize, (s, b) => GuildBoard.OnUpdateGuildFlag(s, b, misc));
        Reg("C_REQUEST_GUILD_FLAG_IMAGE_DATA",           GuildBoard.FlagImageBodySize,  (s, b) => GuildBoard.OnRequestGuildFlagImageData(s, b, misc));

        // --- Party matching board (T78, status/PARTY-MATCH.md): publish / link / cancel, RAM-only ---
        foreach (var (matchName, matchOp) in PartyMatchManager.ClientOpcodes)
            Reg(matchName, PartyMatchManager.MinBodyLength(matchOp),
                (s, body) => PartyMatchManager.OnClientPacket(s, matchOp, body));
        PartyMatchManager.PartySize = id => PartyWiring.Manager.FindByMember(id)?.Count ?? 1;

        // --- Guild war (T80, status/GUILD-WAR.md): check / declare / withdraw / window, persisted ---
        foreach (var (warName, warOp) in GuildWarManager.ClientOpcodes)
            Reg(warName, GuildWarManager.MinBodyLength(warOp),
                (s, body) => GuildWarManager.OnClientPacket(s, warOp, body));

        // --- Private channels (T96, status/CHAT-DESIGN.md): the eight channel packets. C_WHISPER / C_CHAT stay with SocialHandlers. ---
        foreach (var (chatName, chatOp) in ChatManager.ClientOpcodes)
            Reg(chatName, ChatManager.MinBodyLength(chatOp),
                (s, b) => ChatManager.OnClientPacket(s, chatOp, b));

        // --- Inventory window ---
        Reg("C_SHOW_ITEMLIST", 0, (s, body) =>
        {
            if (s.InWorld) { opcodes.TryGetCode("C_SHOW_ITEMLIST", out ushort op); Forward(s, op, body); return true; }
            var chr = s.SelectedCharacter;
            if (chr != null) InventoryHandlers.SendInventory(s, chr, requested: true);
            return true;
        });

        // --- Servants ---
        RegEmptyReply("C_REQUEST_SERVANT_INFO_LIST", "S_REQUEST_SERVANT_INFO_LIST", new Dictionary<string, object>
        {
            ["hidden"] = true, ["slots"] = 10, ["servants"] = new List<object>(),
        });
        RegEmptyReply("C_REQUEST_SERVANT_ADVENTURE_LIST", "S_RESPONSE_SERVANT_ADVENTURE_LIST", new Dictionary<string, object>
        {
            ["unk1"] = (byte)1, ["unk2"] = -1, ["maxSlots"] = 3, ["unk3"] = 0, ["adventures"] = new List<object>(),
        });

        // --- Server time: T45 ArbiterClientHandlers.OnServerTime (registered above) ---

        // C_CHECK_ALIVE is not in the 376012 opcode map (it logged an error at every startup) - dropped.

        // Logout button -> character select. Mirror the real Arbiter
        // (User::OnRequestReturnToLobby -> OnLeaveWorldTick):
        //   1. immediately AS_USER_REQUEST_EXIT (0x14FF) to World,
        //   2. S_PREPARE_RETURN_TO_LOBBY to the client (shows the countdown),
        //   3. after the countdown, AS_CANCEL_SKILL_STRICTLY + AS_LEAVE_WORLD,
        //   4. World saves and replies SA_LEAVE_WORLD (0x1393); only then is the
        //      client sent S_RETURN_TO_LOBBY (handled in GameSession).
        void StartLeaveCountdown(GameSession s, LeaveMode mode, string preparePacket)
        {
            const int countdown = 5;
            s.BeginLeaveToWorld(mode);
            if (defs.Has(preparePacket))
                s.SendByDef(preparePacket, new Dictionary<string, object> { ["time"] = countdown });
            var cts = new CancellationTokenSource();
            s.PendingLobbyReturn = cts;
            _ = Task.Delay(TimeSpan.FromSeconds(countdown), cts.Token).ContinueWith(t =>
            {
                if (t.IsCanceled) return;
                s.CompleteLeaveToWorld();
            }, TaskScheduler.Default);
        }

        Reg("C_RETURN_TO_LOBBY", 0, (s, body) =>
        {
            log.LogInformation("C_RETURN_TO_LOBBY from {Id} - starting lobby-return", s.Id);
            StartLeaveCountdown(s, LeaveMode.Lobby, "S_PREPARE_RETURN_TO_LOBBY");
            return true;
        });

        Reg("C_CANCEL_RETURN_TO_LOBBY", 0, (s, body) =>
        {
            log.LogInformation("C_CANCEL_RETURN_TO_LOBBY from {Id}", s.Id);
            s.PendingLobbyReturn?.Cancel();
            s.PendingLobbyReturn = null;
            if (s.InWorld) Program.World?.SendUserCancelRequestExit(s.PlayerId);
            s.SendByDef("S_CANCEL_RETURN_TO_LOBBY", new Dictionary<string, object> { ["byInterrupt"] = (byte)0 });
            return true;
        });

        // Exit button -> close client. Same World-side sequence as lobby but with exit
        // values (type=1, reason=8). S_PREPARE_EXIT/S_EXIT are registered as inline defs
        // in Program.cs if the data folder doesn't have them.
        Reg("C_EXIT", 0, (s, body) =>
        {
            log.LogInformation("C_EXIT from {Id}", s.Id);
            StartLeaveCountdown(s, LeaveMode.Exit, "S_PREPARE_EXIT");
            return true;
        });

        // --- Telemetry / world packets: noop standalone, forward in-world ---
        // (T45 moved C_TRADE_BROKER_HIGHEST_ITEM_LEVEL, C_UPDATE_CONTENTS_PLAYTIME, C_EVENT_GUIDE and
        //  C_VISIT_NEW_SECTION to real Arbiter-side handlers above - they must NOT be forwarded.)
        RegNoop("C_SET_VISIBLE_RANGE");
        RegNoop("C_HARDWARE_INFO");
        RegNoop("C_CHANGE_USER_LOBBY_SLOT_ID");
        // C_RQ_SKILL_POLISHING_LIST / _EXP_INFO: real replies since T82 (registered above)
        RegNoop("C_REQUEST_INGAMESTORE_PRODUCT_LIST");
        // C_REQUEST_GUILD_PERK_LIST: real reply since T83 (registered above)
        RegNoop("C_SET_SERVANT_SEQUENCE");
        RegNoop("C_PLAYER_LOCATION");
        RegNoop("C_PLAYER_FLYING_LOCATION");
        RegNoop("C_AVAILABLE_EVENT_MATCHING_LIST");
        RegNoop("C_GUARD_PK_POLICY");
        RegNoop("C_SIMPLE_TIP_REPEAT_CHECK");
    }
}
