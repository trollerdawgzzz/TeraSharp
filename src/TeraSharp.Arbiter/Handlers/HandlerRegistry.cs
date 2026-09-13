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
                // C_LOAD_TOPO_FIN (first spawn AND after a zone change): [u32 off=18][u32 bytes][u32 pid][entries].
                // Empty list until visited sections are tracked (cap_newchar.log seq 638).
                Program.World?.SendFrame(WorldBridge.OpUpdateVisitedSection, new byte[] { 18,0,0,0, 0,0,0,0, (byte)s.PlayerId, (byte)(s.PlayerId>>8), (byte)(s.PlayerId>>16), (byte)(s.PlayerId>>24) });
                Program.World?.NotifyTopoLoaded(s.PlayerId);
                // Real Arbiter sends these to the client right after C_LOAD_TOPO_FIN (lobby_proxy.log
                // 263-268), in this order. S_LOAD_CLIENT_USER_SETTING here is what makes the chat
                // window process S_CHAT; sent at character select it is discarded on zone load.
                SocialHandlers.SendBlockList(s);
                SocialHandlers.SendFriendGroupList(s);
                SocialHandlers.SendFriendList(s);
                ClientSettingsHandlers.SendUserSetting(s);
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

        // --- Client settings (Arbiter-owned) ---
        Reg("C_REQUEST_CLIENT_CHAT_OPTION_SETTING", 0, ClientSettingsHandlers.OnRequestChatOption);
        Reg("C_REQUEST_CLIENT_UI_SETTING", 0, ClientSettingsHandlers.OnRequestUiSetting);
        Reg("C_SAVE_CLIENT_CHAT_OPTION_SETTING", 0, ClientSettingsHandlers.OnSaveChatOption);
        RegEmptyReply("C_SAVE_CLIENT_UI_SETTING", "S_SAVE_CLIENT_UI_SETTING", new Dictionary<string, object> { ["result"] = (byte)1 });

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

        // --- Server time ---
        Reg("C_SERVER_TIME", 0, (s, body) =>
        {
            if (s.InWorld) { opcodes.TryGetCode("C_SERVER_TIME", out ushort op); Forward(s, op, body); return true; }
            s.SendByDef("S_SERVER_TIME", new Dictionary<string, object>
            {
                ["serverTime"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            });
            return true;
        });

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
        RegNoop("C_SET_VISIBLE_RANGE");
        RegNoop("C_HARDWARE_INFO");
        RegNoop("C_SAVE_CLIENT_ACCOUNT_SETTING");
        RegNoop("C_CHANGE_USER_LOBBY_SLOT_ID");
        RegNoop("C_RQ_SKILL_POLISHING_LIST");
        RegNoop("C_RQ_SKILL_POLISHING_EXP_INFO");
        RegNoop("C_TRADE_BROKER_HIGHEST_ITEM_LEVEL");
        RegNoop("C_REQUEST_INGAMESTORE_PRODUCT_LIST");
        RegNoop("C_REQUEST_GUILD_PERK_LIST");
        RegNoop("C_SET_SERVANT_SEQUENCE");
        RegNoop("C_UPDATE_CONTENTS_PLAYTIME");
        RegNoop("C_PLAYER_LOCATION");
        RegNoop("C_PLAYER_FLYING_LOCATION");
        RegNoop("C_AVAILABLE_EVENT_MATCHING_LIST");
        RegNoop("C_EVENT_GUIDE");
        RegNoop("C_GUARD_PK_POLICY");
        RegNoop("C_VISIT_NEW_SECTION");
        RegNoop("C_SIMPLE_TIP_REPEAT_CHECK");
    }
}
