// SPDX-License-Identifier: MIT
// Copyright (c) 2026 the TeraSharp contributors

using System.Reflection;
using TeraSharp.Arbiter.Game;
using TeraSharp.Arbiter.Handlers;
using TeraSharp.Arbiter.Protocol;
using TeraSharp.Arbiter.World;

namespace TeraSharp.Arbiter.Tests;

public static partial class Tests
{
    [Test] public static void T195_contract_participants_follow_their_own_worlds_and_refuse_missing_owners()
    {
        string map = Path.GetTempFileName();
        using var env = new T185Environment(null);
        ContractBroker.Reset();
        try
        {
            File.WriteAllText(map, "{\"maps\":{\"376012\":{}}}");
            var bridge = new WorldBridge(WorldReplayTable.Load("/nonexistent", QuietLog()), QuietLog());
            using var main = new T192WorldPeer(bridge, 1, 0);
            using var dungeon = new T192WorldPeer(bridge, 13, 13);
            using var initiator = new T185Client(new DefinitionRegistry(QuietLog()), OpcodeTable.LoadFromFile(map, "376012"), QuietLog());
            using var opponent = new T185Client(new DefinitionRegistry(QuietLog()), OpcodeTable.LoadFromFile(map, "376012"), QuietLog());
            T185Environment.SetWorld(bridge);
            foreach (var (client, id, name, world) in new[] { (initiator, 2u, "Test", 13), (opponent, 1002u, "two", 0) })
            {
                client.Session.PlayerId = id; client.Session.GameId = 195000UL + id;
                client.Session.SelectedCharacter = new FakeCharacter { Id = id, Name = name };
                client.Session.CurrentWorldId = world; client.Session.TunnelKey = bridge.AllocateTunnelKey(world);
                client.Session.EnterWorld();
            }
            // Existing T64 goldens: cap_social650/652/653/655/744, unchanged bytes.
            ContractBroker.TryHandleWorldFrame(0x2809, Hex.B(Cap_fetch));
            Hex.Eq(main.Frame(), T180Frame(0x280B, Hex.B(Cap_ask)), "opponent World0 gets the ask");
            Hex.True(dungeon.Available == 0, "initiator waits for opponent's answer");
            ContractBroker.TryHandleWorldFrame(0x280C, Hex.B(Cap_askans));
            Hex.Eq(dungeon.Frame(), T180Frame(0x280A, Hex.B(Cap_verdict)), "initiator World13 gets the verdict");
            var reply = new byte[30]; // native C_REPLY fixed layout, contract index resolves Test
            BitConverter.GetBytes(4).CopyTo(reply, 14); BitConverter.GetBytes(1).CopyTo(reply, 18);
            BitConverter.GetBytes(1).CopyTo(reply, 22); BitConverter.GetBytes(1).CopyTo(reply, 26);
            ContractBroker.OnClientReply(opponent.Session, reply[4..], QuietLog());
            Hex.Eq(main.Frame(), T180Frame(0x280F, Hex.B(Cap_sendend)), "replier World0 gets end");
            Hex.Eq(dungeon.Frame(), T180Frame(0x2810, Hex.B(Cap_reply)), "requestor World13 gets reply");

            // Unsupported type refusal also belongs to the initiator, not a default link.
            var refused = Hex.B(Cap_fetch); BitConverter.GetBytes(35).CopyTo(refused, 20);
            ContractBroker.TryHandleWorldFrame(0x2809, refused);
            Hex.Eq(dungeon.Frame(), T180Frame(0x280A, ContractBroker.BuildDbsFetch(2, 35, 1, 0, 2)),
                "refusal follows initiating character");
            // Synthetic two-person END request, using the native AskUserList layout.
            var end = new byte[36];
            BitConverter.GetBytes(34).CopyTo(end, 0); BitConverter.GetBytes(34).CopyTo(end, 8);
            BitConverter.GetBytes(8).CopyTo(end, 12); BitConverter.GetBytes(2).CopyTo(end, 16);
            BitConverter.GetBytes(4).CopyTo(end, 20); BitConverter.GetBytes(1).CopyTo(end, 24);
            BitConverter.GetBytes(2).CopyTo(end, 28); BitConverter.GetBytes(1002).CopyTo(end, 32);
            ContractBroker.TryHandleWorldFrame(0x280E, end);
            Hex.Eq(dungeon.Frame(), T180Frame(0x280F, ContractBroker.BuildDbsSendEnd(2, 4, 1, 2)), "END follows participant2");
            Hex.Eq(main.Frame(), T180Frame(0x280F, Hex.B(Cap_sendend)), "END follows participant1002");
            initiator.Frame(); opponent.Frame(); // S_END reaches both clients too

            ((List<WorldLink>)typeof(WorldBridge).GetField("_links", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(bridge)!).Remove(dungeon.Link);
            ContractBroker.TryHandleWorldFrame(0x2809, refused);
            Hex.True(main.Available == 0 && dungeon.Available == 0,
                "missing initiator owner never sends a false refusal to main World");
        }
        finally { T185Environment.SetWorld(null); ContractBroker.Reset(); File.Delete(map); }
    }

    [Test] public static void T195_social_trade_guild_and_exit_countdown_use_current_character_world()
    {
        string map = Path.GetTempFileName();
        using var env = new T185Environment(null);
        using var store = StoreWithTwoAccounts();
        var storeProperty = typeof(TeraSharp.Arbiter.Program).GetProperty("Store")!;
        var oldStore = storeProperty.GetValue(null);
        storeProperty.SetValue(null, store);
        try
        {
            File.WriteAllText(map, "{\"maps\":{\"376012\":{}}}");
            var defs = new DefinitionRegistry(QuietLog());
            defs.RegisterFromDef("C_ADD_FRIEND", "string name\nstring message\n");
            defs.RegisterFromDef("C_BLOCK_USER", "string name\n");
            defs.RegisterFromDef("C_REMOVE_BLOCKED_USER", "string name\n");
            var bridge = new WorldBridge(WorldReplayTable.Load("/nonexistent", QuietLog()), QuietLog());
            using var main = new T192WorldPeer(bridge, 1, 0);
            using var dungeon = new T192WorldPeer(bridge, 13, 13);
            using var first = new T185Client(defs, OpcodeTable.LoadFromFile(map, "376012"), QuietLog());
            using var second = new T185Client(defs, OpcodeTable.LoadFromFile(map, "376012"), QuietLog());
            T185Environment.SetWorld(bridge);
            foreach (var (client, id, world) in new[] { (first, 1u, 13), (second, 2u, 0) })
            {
                client.Session.PlayerId = id; client.Session.GameId = 196000UL + id;
                client.Session.SelectedCharacter = FakeCharacter.FromRecord(store.GetCharacter((int)id)!);
                client.Session.CurrentWorldId = world; client.Session.TunnelKey = bridge.AllocateTunnelKey(world);
                client.Session.EnterWorld();
            }
            var social = new SocialHandlers(QuietLog());
            social.OnAddFriend(first.Session, WriteByDef(defs, "C_ADD_FRIEND", new() {
                ["name"] = "t30_2", ["message"] = "",
            }));
            Hex.Eq(dungeon.Frame(), T180Frame(0x2862, SocialHandlers.BuildAsUserPair(1, 0)), "requester's count reaches13");
            Hex.Eq(main.Frame(), T180Frame(0x2862, SocialHandlers.BuildAsUserPair(2, 0)), "target's count reaches0");
            social.OnBlockUser(first.Session, WriteByDef(defs, "C_BLOCK_USER", new() { ["name"] = "t30_2" }));
            Hex.Eq(dungeon.Frame(), T180Frame(0x1475, SocialHandlers.BuildAsUserPair(1, 2)), "block belongs to blocker13");
            social.OnRemoveBlockedUser(first.Session, WriteByDef(defs, "C_REMOVE_BLOCKED_USER", new() { ["name"] = "t30_2" }));
            Hex.Eq(dungeon.Frame(), T180Frame(0x1476, SocialHandlers.BuildAsUserPair(1, 2)), "unblock belongs to blocker13");
            var trade = new byte[ArbiterClientHandlers.AddTradeBagBodySize];
            ArbiterClientHandlers.OnAddTradeBag(first.Session, trade, QuietLog());
            Hex.Eq(dungeon.Frame(), T180Frame(0x1637, ArbiterClientHandlers.BuildAsAddTradeBag(2800, 1, default)),
                "trade bag follows sender's World");
            new BrokerHandlers(QuietLog()).Handle(first.Session, BrokerPackets.C_TRADE_BROKER_CLOSE, Array.Empty<byte>());
            Hex.Eq(dungeon.Frame(), T180Frame(0x1458, BrokerPackets.BuildAsBrokerClose(1)), "existing broker-close payload follows sender");

            GuildWiring.SendWorldAction(bridge, GuildPackets.AS_GUILD_JOINED, GuildPackets.BuildAsGuildJoined(1));
            Hex.Eq(dungeon.Frame(), T180Frame(0x2866, BitConverter.GetBytes(1)), "guild-joined targets that member only");
            var notify = new ArbiterActions(); notify.World(GuildWarManager.AS_NOTIFY_GUILD_WAR_INFO, GuildWarManager.BuildAsNotify(1, true));
            GuildWarManager.Dispatcher(first.Session, QuietLog()).Dispatch(notify, "T195");
            Hex.Eq(dungeon.Frame(), T180Frame(0x14AF, GuildWarManager.BuildAsNotify(1, true)), "guild-war login push follows member");
            Hex.True(main.Available == 0, "per-character pushes do not leak to main");
            var title = GuildPackets.BuildAsGuildString(7, "example");
            GuildWiring.SendWorldAction(bridge, GuildPackets.AS_UPDATE_GUILD_TITLE, title);
            Hex.Eq(main.Frame(), T180Frame(0x1412, title), "guild title is a global mirror");
            Hex.Eq(dungeon.Frame(), T180Frame(0x1412, title), "guild title mirror reaches instance too");

            int guildId = store.CreateGuild("T195", 1, false);
            byte[] load = SaGuildActionPayload(guildId);
            var snapshot = GuildWiring.OnWorldFrame(store, GuildPackets.SA_LOAD_GUILD, load);
            // Exercise the actual WorldBridge callback: 144D/groups/members/perks
            // answer only the requesting link, unlike the title mirror above.
            bridge.HandleFrame(dungeon.Link, GuildPackets.SA_LOAD_GUILD, load);
            foreach (var frame in snapshot.Ordered.OfType<IArbiterWorldAction>())
                Hex.Eq(dungeon.Frame(), T180Frame(frame.Opcode, frame.Payload), "guild snapshot returns to requester13");
            Hex.True(main.Available == 0, "request snapshot is never broadcast to World0");

            foreach (var current in new[] { 13, 0 })
            {
                first.Session.CurrentWorldId = current;
                bridge.SendUserRequestExit(1); bridge.SendUserCancelRequestExit(1);
                var owner = current == 13 ? dungeon : main;
                Hex.Eq(owner.Frame(), T180Frame(0x14FF, BitConverter.GetBytes(1u)), "exit countdown follows current owner");
                Hex.Eq(owner.Frame(), T180Frame(0x1500, BitConverter.GetBytes(1u)), "countdown cancellation follows same owner");
            }
            first.Session.CurrentWorldId = 31;
            bridge.SendUserRequestExit(1); bridge.SendUserCancelRequestExit(1);
            Hex.True(!ArbiterClientHandlers.SendToWorld(1, 0x1637, new byte[48])
                && main.Available == 0 && dungeon.Available == 0, "no fallback for an offline current World");
        }
        finally
        {
            T185Environment.SetWorld(null); storeProperty.SetValue(null, oldStore);
            PartyWiring.ResetForTests(); File.Delete(map);
        }
    }
}
