// SPDX-License-Identifier: MIT
// Copyright (c) 2026 the TeraSharp contributors

using System.Reflection;
using System.Text.Json;
using TeraSharp.Arbiter.Game;
using TeraSharp.Arbiter.Handlers;
using TeraSharp.Arbiter.Protocol;
using TeraSharp.Arbiter.World;

namespace TeraSharp.Arbiter.Tests;

public static partial class Tests
{
    private static byte[] T195InstanceFrame(string capture, int record, ushort opcode, int offset = 0)
    {
        var path = FindRepoFile(Path.Combine("data", "t195", "instance-frames.json"))
            ?? throw new FileNotFoundException("tracked T195 instance fixtures missing");
        using var data = JsonDocument.Parse(File.ReadAllText(path));
        return Convert.FromHexString(data.RootElement.GetProperty(capture).EnumerateArray().Single(f =>
            (f.TryGetProperty("source_record", out var r) ? r.GetInt32() : f.GetProperty("n").GetInt32()) == record
            && f.GetProperty("op").GetUInt16() == opcode
            && (!f.TryGetProperty("source_offset", out var o) || o.GetInt32() == offset))
            .GetProperty("hex").GetString()!);
    }

    [Test] public static void T195_instance_say_party_chat_and_party_menu_reach_their_native_destinations()
    {
        if (FixtureOrSkip(Path.Combine("data", "t195", "instance-frames.json"), "T195 instance-frames.json") is null) return;
        string map = Path.GetTempFileName();
        using var env = new T185Environment(null);
        PartyWiring.ResetForTests();
        try
        {
            File.WriteAllText(map, "{\"maps\":{\"376012\":{\"S_CHAT\":32107}}}");
            var defs = new DefinitionRegistry(QuietLog());
            defs.RegisterFromDef("C_CHAT", "ref message\nuint32 channel\nstring message\n");
            defs.RegisterFromDef("S_CHAT", "uint32 channel\nuint64 gameId\nbool isWorldEventTarget\nbool gm\nbool founder\nstring name\nstring message\n");
            var bridge = new WorldBridge(WorldReplayTable.Load("/nonexistent", QuietLog()), QuietLog());
            using var main = new T192WorldPeer(bridge, 81, 0);
            using var instance = new T192WorldPeer(bridge, 106, 13);
            using var first = new T185Client(defs, OpcodeTable.LoadFromFile(map, "376012"), QuietLog());
            using var second = new T185Client(defs, OpcodeTable.LoadFromFile(map, "376012"), QuietLog());
            T185Environment.SetWorld(bridge);
            foreach (var (client, id, name) in new[] { (first, 10u, "leader"), (second, 9u, "member") })
            {
                client.Session.PlayerId = id;
                client.Session.SelectedCharacter = new FakeCharacter { Id = id, Name = name, Level = 70 };
                client.Session.GameId = 0x80000AF00001UL + (id == 10 ? 0UL : 1UL);
                client.Session.CurrentWorldId = 13;
                client.Session.TunnelKey = bridge.AllocateTunnelKey(13);
                client.Session.EnterWorld();
                PartyWiring.Register(client.Session);
            }
            using var sheet = new T184hDungeonSheet(9781, total: 2);
            PartyWiring.Manager.FormMatchedParty(new[] {
                new PartyManager.MatchedMember(10, MatchRole.Dps), new PartyManager.MatchedMember(9, MatchRole.Dps),
            }, false, 9781);
            var chat = new ChatHandlers(QuietLog());
            chat.OnChat(first.Session, T195InstanceFrame("cap_instance1_client2", 12289, 0xEB77)[4..]);
            Hex.Eq(instance.Frame(), T195InstanceFrame("cap_instance1", 152756, 0x1449),
                "say payload stays byte-exact; corrected destination is World13");
            Hex.True(main.Available == 0, "say never leaks to World0");
            // During the captured transfer the main and instance allocators both
            // issue ticket5; afterwards users9/10 have exchanged their old slots.
            first.Session.CurrentWorldId = 0; first.Session.TunnelKey = 5;
            second.Session.TunnelKey = 5;
            chat.OnChat(second.Session, T195InstanceFrame("cap_instance1_client1", 5035, 0xEB77)[4..]);
            var partyChat = first.Frame();
            Hex.Eq(second.Frame(), partyChat, "party sender and peer receive the same S_CHAT directly");
            Hex.True(BitConverter.ToUInt16(partyChat, 2) == 0x7D6B
                && BitConverter.ToUInt32(partyChat, 8) == 1
                && BitConverter.ToUInt64(partyChat, 12) == second.Session.GameId
                && main.Available == 0 && instance.Available == 0,
                "party recipients remain distinct with equal wire tickets on different Worlds");
            first.Session.CurrentWorldId = 13; first.Session.TunnelKey = 6;
            chat.OnChat(second.Session, T195InstanceFrame("cap_instance1_client1", 5035, 0xEB77)[4..]);
            Hex.Eq(first.Frame(), partyChat, "party route remains stable after captured ticket5/6 swap");
            Hex.Eq(second.Frame(), partyChat, "sender still gets one echo after swap");

            foreach (var (clientRecord, clientOp, worldRecord, worldOp) in new[] {
                (12624, (ushort)0xC8B9, 153239, (ushort)0x13BA),
                (12740, (ushort)0x5D24, 153535, (ushort)0x13BB),
                (12767, (ushort)0x5D24, 153619, (ushort)0x13BB),
                (12649, (ushort)0x5867, 153291, (ushort)0x13B9),
            })
            {
                PartyWiring.OnClientPacket(first.Session, clientOp,
                    T195InstanceFrame("cap_instance1_client2", clientRecord, clientOp)[4..]);
                var expected = T195InstanceFrame("cap_instance1", worldRecord, worldOp);
                if (worldOp == 0x13B9) BitConverter.GetBytes(PartyWiring.Manager.FindByMember(10)!.Id).CopyTo(expected, 14);
                Hex.Eq(main.Frame(), expected, "native vote broadcast keeps main World copy");
                Hex.Eq(instance.Frame(), expected, "native vote broadcast also reaches the dungeon World");
            }
            // Vote reply, /leave and Unstuck's menu/contract16 are per-character
            // tunnels. World582147-582178; ContractNearTown constructor1211152.
            foreach (var packet in new[] {
                T195InstanceFrame("cap_instance1_client1", 5382, 0xF783),
                T195InstanceFrame("cap_instance1_client2", 12937, 0xFFB6),
                T195InstanceFrame("cap_instance1_client2", 13333, 0x944C),
                T195InstanceFrame("cap_instance1_client2", 13338, 0x8853),
                T195InstanceFrame("cap_instance1_client2", 13344, 0x944C),
            })
            {
                first.Session.ForwardToWorld(packet);
                var tunnel = instance.Frame();
                Hex.True(BitConverter.ToUInt16(tunnel, 4) == 0x13F6, "vote/leave/Unstuck uses existing World tunnel");
                Hex.Eq(tunnel[30..], packet, "vote/leave/Unstuck inner bytes unchanged");
                Hex.True(main.Available == 0, "per-character tunnel stays on World13");
            }
        }
        finally { T185Environment.SetWorld(null); PartyWiring.ResetForTests(); File.Delete(map); }
    }

    [Test] public static void T195_party_mirrors_broadcast_and_party_chat_excludes_outsiders()
    {
        if (FixtureOrSkip(Path.Combine("data", "t195", "instance-frames.json"), "T195 instance-frames.json") is null) return;
        var sent = new List<(int world, ushort op)>();
        foreach (var op in new ushort[] { 0x139E, 0x139F, 0x13A0, 0x13A1, 0x13A2, 0x13A3,
            0x13A4, 0x13A5, 0x13A6, 0x13A7, 0x13B9, 0x13BA, 0x13BB, 0x13BC })
        {
            sent.Clear();
            PartyWiring.RouteWorldAction(op, Array.Empty<byte>(), id => id is 0 or 13, _ => 13,
                (world, code, _) => sent.Add((world, code)));
            Hex.True(sent.SequenceEqual(new[] { (0, op), (13, op) }), "party mirror/vote reaches each registered World once");
        }
        sent.Clear();
        PartyWiring.RouteWorldAction(0x13F5, T195InstanceFrame("cap_2man_b", 19061, 0x13F5)[6..],
            id => id is 0 or 13, _ => 13, (world, code, _) => sent.Add((world, code)));
        Hex.True(sent.SequenceEqual(new[] { (13, (ushort)0x13F5) }), "dropout notification stays current-World only");
        sent.Clear();
        PartyWiring.RouteWorldAction(0x15CD, new byte[5], id => id is 0 or 13, _ => 13,
            (world, code, _) => sent.Add((world, code)));
        Hex.True(sent.SequenceEqual(new[] { (0, (ushort)0x15CD) }), "retail matching clear deliberately remains main-World only");

        using var sheet = new T184hDungeonSheet(9781, total: 2);
        var manager = new PartyManager(QuietLog());
        manager.Register(P(11, 1, "sender")); manager.Register(P(12, 2, "peer")); manager.Register(P(13, 3, "outsider"));
        manager.FormMatchedParty(new[] { new PartyManager.MatchedMember(1, MatchRole.Dps),
            new PartyManager.MatchedMember(2, MatchRole.Dps) }, false, 9781);
        var actions = manager.Chat(11, 1, "<FONT>hey</FONT>");
        Hex.True(actions.ToWorld.Count == 0 && actions.ToClients.Select(c => c.Ticket).SequenceEqual(new uint[] { 11, 12 }),
            "party chat reaches only online party members, including sender");
        Hex.True(manager.Chat(13, 1, "outsider").IsEmpty
            && manager.Chat(11, 32, "raid only").IsEmpty
            && manager.Chat(11, 1, "blocked", (recipient, _) => recipient == 2).ToClients.Count == 1,
            "nonmembers, nonraid raid channel and receiver block lists retain native exclusions");
    }

    [Test] public static void T195_return_from_instance_resolves_catchall_and_preserves_retail_destination()
    {
        if (FixtureOrSkip(Path.Combine("data", "t195", "instance-frames.json"), "T195 instance-frames.json") is null) return;
        var before = T195InstanceFrame("cap_multiworld3", 7908, 0x138E);
        var request = T195InstanceFrame("cap_multiworld3", 8168, 0x1445);
        var move = CrossWorldHandoff.Parse(request[6..])!;
        var expectedEnter = T195InstanceFrame("cap_multiworld3", 8193, 0x138E);
        Hex.Eq(CrossWorldHandoff.BuildEnter(before[6..], move, BitConverter.ToUInt32(expectedEnter, 86)),
            expectedEnter[6..], "retail reverse7908 +8168 ->8193, all saved-return fields exact");
        Hex.Eq(CrossWorldHandoff.BuildLeave(move, 1), T195InstanceFrame("cap_multiworld3", 8169, 0x1392)[6..],
            "retail reverse type2 leave");
        foreach (var record in new[] { 153328, 153336, 153967, 153977, 154360, 154433 })
        {
            var live = CrossWorldHandoff.Parse(T195InstanceFrame("cap_instance1", record, 0x1445,
                record == 153336 ? 150 : 0)[6..])!;
            Hex.True(live.TargetWorld == -1 && live.Continent == 7005
                && live.Channel == (record >= 154360 ? 0x0AF00001u : uint.MaxValue)
                && CrossWorldHandoff.ResolveDestination(live, _ => null, _ => null, 0) == 0,
                "each member's reset/leave/Unstuck resolves main catch-all despite missing destination channel/continent rows");
        }
        Hex.True(CrossWorldHandoff.ResolveDestination(move with { TargetWorld = 13 }, _ => 0, _ => 0, 0) == 13
            && CrossWorldHandoff.ResolveDestination(move with { TargetWorld = -1 }, _ => 13, _ => 0, 0) == 13
            && CrossWorldHandoff.ResolveDestination(move with { TargetWorld = -1 }, _ => null, _ => null, null) == null,
            "explicit owner and channel owner outrank catch-all; unknown configuration has no fabricated fallback");

        string map = Path.GetTempFileName();
        using var env = new T185Environment(null);
        using var store = StoreWithTwoAccounts();
        DungeonRouting.ResetForTest(); PartyWiring.ResetForTests(); LeaveGate.Shared.Clear();
        try
        {
            File.WriteAllText(map, "{\"maps\":{\"376012\":{}}}");
            var bridge = new WorldBridge(WorldReplayTable.Load("/nonexistent", QuietLog()), QuietLog())
                { DbProxy = new DbProxyHandlers(store, QuietLog()) };
            using var main = new T192WorldPeer(bridge, 11, 0);
            using var instance = new T192WorldPeer(bridge, 14, 13);
            using var client = new T185Client(new DefinitionRegistry(QuietLog()), OpcodeTable.LoadFromFile(map, "376012"), QuietLog());
            T185Environment.SetWorld(bridge);
            client.Session.PlayerId = 1; client.Session.GameId = move.GameId;
            client.Session.SelectedCharacter = new FakeCharacter { Id = 1, Name = "dob", Level = 70, Zone = 9781 };
            client.Session.CurrentWorldId = 13; client.Session.TunnelKey = bridge.AllocateTunnelKey(13);
            client.Session.EnterWorld(); LeaveGate.Shared.Entered(move.GameId);
            BitConverter.GetBytes(move.GameId).CopyTo(before, 30);
            BitConverter.GetBytes(client.Session.TunnelKey).CopyTo(before, 86);
            BitConverter.GetBytes(move.GameId).CopyTo(request, 14);
            bridge.SendFrame(13, WorldBridge.OpPlayerEnter, before[6..]); instance.Frame();
            DungeonRouting.Channels.MapContinent(9781, 13);
            DungeonRouting.Channels.CatchAllWorldId = 0;
            var departure = T195InstanceFrame("cap_multiworld3", 8164, 0x13C2);
            BitConverter.GetBytes(move.GameId).CopyTo(departure, 6);
            bridge.HandleFrame(instance.Link, 0x13C2, departure[6..]);
            Hex.Eq(instance.Frame(), T195InstanceFrame("cap_multiworld3", 8165, 0x13C3), "departure continues to owning dungeon World");
            var offlineOwner = (byte[])request.Clone(); BitConverter.GetBytes(7).CopyTo(offlineOwner, 30);
            bridge.HandleFrame(instance.Link, 0x1445, offlineOwner[6..]);
            Hex.True(main.Available == 0 && instance.Available == 0,
                "an explicit offline owner is refused, never redirected to the live catch-all World");
            bridge.HandleFrame(instance.Link, 0x1445, request[6..]);
            Hex.Eq(instance.Frame(), T195InstanceFrame("cap_multiworld3", 8169, 0x1392), "catch-all permits actual source leave");
            // Synthetic minimal blob: no ignored binary capture dependency.
            var blob = new byte[308]; BitConverter.GetBytes(9781).CopyTo(blob, 236);
            store.SaveWorldBlob(1, blob);
            bridge.HandleFrame(instance.Link, 0x1393, T195InstanceFrame("cap_multiworld3", 8191, 0x1393)[6..]);
            Hex.Eq(instance.Frame(), T195InstanceFrame("cap_multiworld3", 8192, 0x1433), "delete source duplicates");
            BitConverter.GetBytes(move.GameId).CopyTo(expectedEnter, 30);
            BitConverter.GetBytes(client.Session.TunnelKey).CopyTo(expectedEnter, 86);
            Hex.Eq(main.Frame(), expectedEnter, "return AS_ENTER_WORLD arrives on World0 with current ticket");
            Hex.True(client.Session.InWorld && client.Session.CurrentWorldId == 0 && client.Available == 0,
                "type2 return keeps client in-world without lobby packets");
            var saved = store.GetCharacter(1)!.WorldBlob!;
            Hex.True(BitConverter.ToInt32(saved, 236) == move.Continent && BitConverter.ToInt32(saved, 244) == 0,
                "saved destination blob now belongs to main World");
            Hex.Eq(saved[220..232], request[42..54], "World-supplied saved return XYZ retained");
            var topo = Convert.FromHexString("150028E85D1B000000E09EC40010EA4500D0074500"); // multiworld3 raw8279+702
            bridge.HandleFrame(main.Link, 0x13F7, TunnelFrames.BuildBypassToClient(topo, TunnelFrames.To(client.Session.TunnelKey)));
            Hex.Eq(client.Frame(), topo, "World0 S_LOAD_TOPO reaches the same client after return");
            client.Session.ForwardToWorld(Convert.FromHexString("050083F701"));
            Hex.Eq(main.Frame()[30..], Convert.FromHexString("050083F701"), "subsequent per-character traffic follows World0");
            Hex.True(instance.Available == 0, "old instance receives no post-return client traffic");
        }
        finally
        {
            T185Environment.SetWorld(null); DungeonRouting.ResetForTest(); PartyWiring.ResetForTests();
            LeaveGate.Shared.Clear(); File.Delete(map);
        }
    }
}
