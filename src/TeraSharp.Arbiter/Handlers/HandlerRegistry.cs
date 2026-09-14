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
        foreach (var name in new[]
        {
            "C_REQUEST_PARTY_MATCH_INFO", "C_REQUEST_MY_PARTY_MATCH_INFO", "C_PARTY_MATCH_WINDOW_CLOSED",
            "C_REQUEST_GUILD_LIST", "C_DUNGEON_COOL_TIME_LIST", "C_VIEW_BATTLE_FIELD_RESULT",
            "C_REQUEST_CANDIDATE_LIST", "C_SHOW_AWESOMIUMWEB_SHOP", "C_RESET_ALL_DUNGEON",
            "C_UPDATE_CONTENTS_PLAYTIME", "C_EVENT_GUIDE",
        })
            Reg(name, 0, (s, b) => ArbiterClientHandlers.OnAcceptSilently(s, b, misc));

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

        Reg("C_CHECK_ALIVE", 0, (s, body) => true);

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
        RegNoop("C_RQ_SKILL_POLISHING_LIST");
        RegNoop("C_RQ_SKILL_POLISHING_EXP_INFO");
        RegNoop("C_REQUEST_INGAMESTORE_PRODUCT_LIST");
        RegNoop("C_REQUEST_GUILD_PERK_LIST");
        RegNoop("C_SET_SERVANT_SEQUENCE");
        RegNoop("C_PLAYER_LOCATION");
        RegNoop("C_PLAYER_FLYING_LOCATION");
        RegNoop("C_AVAILABLE_EVENT_MATCHING_LIST");
        RegNoop("C_GUARD_PK_POLICY");
        RegNoop("C_SIMPLE_TIP_REPEAT_CHECK");
    }
}
