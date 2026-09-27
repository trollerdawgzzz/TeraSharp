// SPDX-License-Identifier: MIT
// Copyright (c) 2026 the TeraSharp contributors

using System.Reflection;
using System.Text.Json;
using TeraSharp.Arbiter.Game;
using TeraSharp.Arbiter.Persistence;
using TeraSharp.Arbiter.Protocol;
using TeraSharp.Arbiter.World;

namespace TeraSharp.Arbiter.Tests;

public static partial class Tests
{
    private static byte[] T199TransferFrame(int record, ushort opcode, int offset = 0, string capture = "cap_bg1")
    {
        var path = FindRepoFile(Path.Combine("data", "t199", "transfer-frames.json"))
            ?? throw new FileNotFoundException("tracked T199 transfer fixtures missing");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var frame = document.RootElement.GetProperty(capture).EnumerateArray().Single(f =>
            (f.TryGetProperty("source_record", out var r) ? r.GetInt32() : f.GetProperty("n").GetInt32()) == record
            && f.GetProperty("op").GetUInt16() == opcode
            && (!f.TryGetProperty("source_offset", out var o) || o.GetInt32() == offset));
        return Convert.FromHexString(frame.GetProperty("hex").GetString()!);
    }

    [Test] public static void T199_both_battleground_transfers_and_return_points_match_retail()
    {
        if (FixtureOrSkip(Path.Combine("data", "t199", "transfer-frames.json"), "T199 transfer-frames.json") is null) return;
        foreach (var (beforeRecord, offerOffset, requestRecord, enterRecord, leaveRecord, returnRecord) in new[] {
            (10258, 228, 12546, 12667, 12548, 16604), (7955, 0, 12594, 12690, 12596, 16385),
        })
        {
            var before = T199TransferFrame(beforeRecord, 0x138E)[6..];
            var offer = BattlefieldHandoff.ParseOffer(10, T199TransferFrame(12535, 0x1513, offerOffset)[6..])!;
            var request = BattlefieldHandoff.ParseEnter(T199TransferFrame(requestRecord, 0x13CB)[6..])!;
            var expected = T199TransferFrame(enterRecord, 0x138E)[6..];
            Hex.True(offer.Battlefield == 37 && offer.Continent == 115 && offer.Channel == 0x0AF03922
                && request.Return.Continent == 7005 && request.Return.Channel == 0,
                "1513 is the destination;13CB holds each player's source return point");
            var move = BattlefieldHandoff.Destination(offer, request.Handle, BitConverter.ToUInt64(before, 84), before);
            // Native normal party formed after login. Production WorldBridge refreshes
            // this state from PartyManager; every other byte comes from these captures.
            BitConverter.GetBytes(offer.PartyId).CopyTo(before, 94);
            Hex.Eq(CrossWorldHandoff.BuildEnter(before, move, BitConverter.ToUInt32(expected, 80)), expected,
                "complete189-byte BG entry matches retail after native current-party refresh");
            Hex.Eq(CrossWorldHandoff.BuildLeave(move, (uint)offer.UserId), T199TransferFrame(leaveRecord, 0x1392)[6..],
                "13CB creates a type2 leave, never a dungeon13BF");
            Hex.Eq(BattlefieldHandoff.BuildSystemReturn((uint)offer.UserId, request.Return),
                T199TransferFrame(requestRecord + 1, 0x27C6)[6..], "persisted system-return push is byte-exact");
            var returned = T199TransferFrame(returnRecord, 0x138E)[6..];
            var back = BattlefieldHandoff.ReturnDestination(request.Return, request.Handle, move.GameId, expected);
            Hex.Eq(CrossWorldHandoff.BuildEnter(expected, back, BitConverter.ToUInt32(returned, 80)), returned,
                "normal exit restores integer town location and preserves party, heading and EtcData");
        }
        var first = BattlefieldHandoff.ParseOffer(10, T199TransferFrame(12390, 0x1513)[6..])!;
        Hex.Eq(BattlefieldHandoff.BuildEntranceInfo(first), T199TransferFrame(2665, 0x942F, capture: "cap_bg1_client2"),
            "forced38 offer entrance bytes");
        Hex.Eq(MatchQueueManager.BuildFinInterPartyMatch(first.Battlefield, 1, 0),
            T199TransferFrame(2667, 0x6470, capture: "cap_bg1_client2"), "forced offer FIN declares matchingType1");
        Hex.True(BattlefieldHandoff.ParseOffer(10, new byte[45]) == null
            && BattlefieldHandoff.ParseEnter(new byte[27]) == null, "short native packets refused");
        var malformed = T199TransferFrame(12535, 0x1513)[6..]; BitConverter.GetBytes(7).CopyTo(malformed, 4);
        Hex.True(BattlefieldHandoff.ParseOffer(10, malformed) == null, "party vector requires whole8-byte entries");
    }

    [Test] public static void T199_offer_timers_enter_both_members_and_restore_each_normal_party()
    {
        if (FixtureOrSkip(Path.Combine("data", "t199", "transfer-frames.json"), "T199 transfer-frames.json") is null) return;
        string map = Path.GetTempFileName();
        using var env = new T185Environment(null);
        using var store = StoreWithTwoAccounts();
        using var events = T161bEvents((2204, "BattleField", 38), (92204, "BattleField", 38));
        var storeProperty = typeof(TeraSharp.Arbiter.Program).GetProperty("Store")!;
        var oldStore = storeProperty.GetValue(null); storeProperty.SetValue(null, store);
        DungeonRouting.ResetForTest(); PartyWiring.ResetForTests(); LeaveGate.Shared.Clear();
        try
        {
            File.WriteAllText(map, "{\"maps\":{\"376012\":{}}}");
            var bridge = new WorldBridge(WorldReplayTable.Load("/nonexistent", QuietLog()), QuietLog())
                { DbProxy = new DbProxyHandlers(store, QuietLog()) };
            using var main = new T192WorldPeer(bridge, 29, 0);
            using var bf = new T192WorldPeer(bridge, 56, 10);
            using var first = new T185Client(new DefinitionRegistry(QuietLog()), OpcodeTable.LoadFromFile(map, "376012"), QuietLog());
            using var second = new T185Client(new DefinitionRegistry(QuietLog()), OpcodeTable.LoadFromFile(map, "376012"), QuietLog());
            T185Environment.SetWorld(bridge);
            DungeonRouting.Channels.CatchAllWorldId = 0;
            var control = (BattlefieldHandoff)typeof(WorldBridge).GetField("_battlefield", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(bridge)!;
            var transfers = (CrossWorldHandoff)typeof(WorldBridge).GetField("_crossWorld", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(bridge)!;
            var now = DateTimeOffset.FromUnixTimeSeconds(1700000000);
            var users = new[] {
                (Client: first, Id: 1u, Before: 10258, Offer38: 12390, Offer37Offset: 228, Request: 12546,
                    Enter: 12667, Leave: 12665, Exit: 16561, FinalLeave: 16602, Return: 16604, Topo: 3123, FinalTopo: 5710,
                    Capture: "cap_bg1_client1"),
                (Client: second, Id: 2u, Before: 7955, Offer38: 12393, Offer37Offset: 0, Request: 12594,
                    Enter: 12690, Leave: 12681, Exit: 16352, FinalLeave: 16383, Return: 16385, Topo: 2895, FinalTopo: 5879,
                    Capture: "cap_bg1_client2"),
            };
            foreach (var u in users)
            {
                var initial = T199TransferFrame(u.Before, 0x138E)[6..];
                var s = u.Client.Session;
                s.PlayerId = u.Id; s.GameId = BitConverter.ToUInt64(initial, 84);
                s.SelectedCharacter = new FakeCharacter { Id = u.Id, Name = "human" + u.Id, Level = 70, Zone = 7005 };
                s.CurrentWorldId = 0; s.TunnelKey = bridge.AllocateTunnelKey(0); s.EnterWorld();
                PartyWiring.Register(s);
                int bot = (int)u.Id + 10;
                PartyWiring.Manager.Register(new((uint)bot, bot, "bot" + bot, 70, 0, 0, 0, (ulong)bot));
                Hex.True(PartyWiring.Manager.OnWorldFrame(PartyPackets.SA_JOIN_PARTY,
                    SaJoinPartyPayload((int)u.Id, bot)).Rejected == null, "normal pre-existing party formed");
                BitConverter.GetBytes(s.GameId).CopyTo(initial, 24); BitConverter.GetBytes(u.Id).CopyTo(initial, 32);
                BitConverter.GetBytes(s.TunnelKey).CopyTo(initial, 80);
                bridge.SendFrame(0, 0x138E, initial); main.Frame();
                var blob = new byte[308]; BitConverter.GetBytes(7005).CopyTo(blob, 236); store.SaveWorldBlob((int)u.Id, blob);
                // A naked13CB cannot enter a destination the Arbiter never offered.
                var noOffer = T199TransferFrame(u.Request, 0x13CB)[6..]; BitConverter.GetBytes(s.GameId).CopyTo(noOffer, 0);
                bridge.HandleFrame(main.Link, 0x13CB, noOffer);
                Hex.True(main.Available == 0 && bf.Available == 0, "missing offer is refused without guessed BF route");
            }
            byte[] OfferFor(uint id, int record, int offset = 0)
            {
                var p = T199TransferFrame(record, 0x1513, offset)[6..]; BitConverter.GetBytes(id).CopyTo(p, 8);
                long party = PartyWiring.Manager.FindByMember((int)id)!.Id;
                BitConverter.GetBytes(party).CopyTo(p, 24);
                for (int at = 46; at + 8 <= p.Length; at += 8) BitConverter.GetBytes(party).CopyTo(p, at);
                return p;
            }
            foreach (var u in users)
            {
                control.TryHandle(bridge, bf.Link, transfers, store, 0x1513, OfferFor(u.Id, u.Offer38), now);
                Hex.Eq(u.Client.Frame(), T199TransferFrame(2665, 0x942F, capture: "cap_bg1_client2"), "38 entrance");
                Hex.Eq(u.Client.Frame(), T199TransferFrame(2666, 0x87AC, capture: "cap_bg1_client2"), "38 sheet event reset");
                Hex.Eq(u.Client.Frame(), T199TransferFrame(2667, 0x6470, capture: "cap_bg1_client2"), "38 FIN");
                Hex.Eq(main.Frame(), T180Frame(0x15CD, PartyPackets.BuildAsChangeEventMatchingState((int)u.Id, false)), "event clear remains main-World");
                control.TryHandle(bridge, bf.Link, transfers, store, 0x1513,
                    OfferFor(u.Id, 12535, u.Offer37Offset), now.AddSeconds(14));
                Hex.Eq(u.Client.Frame(), T199TransferFrame(2746, 0x942F, capture: "cap_bg1_client2"), "37 replaces pending destination");
                Hex.Eq(u.Client.Frame(), T199TransferFrame(2747, 0x6470, capture: "cap_bg1_client2"), "37 has no invented event reset");
            }
            control.Tick(bridge, now.AddSeconds(15));
            foreach (var u in users) Hex.Eq(main.Frame(), T180Frame(0x1597, BitConverter.GetBytes(u.Id)), "old38 timer asks current World to enter latest offer");
            foreach (var u in users)
            {
                var s = u.Client.Session; long party = PartyWiring.Manager.FindByMember((int)u.Id)!.Id;
                var request = T199TransferFrame(u.Request, 0x13CB)[6..]; BitConverter.GetBytes(s.GameId).CopyTo(request, 0);
                bridge.HandleFrame(bf.Link, 0x13CB, request);
                Hex.True(main.Available == 0 && bf.Available == 0, "wrong incoming owner cannot transfer this user");
                bridge.HandleFrame(main.Link, 0x13CB, request);
                var point = BattlefieldHandoff.ParseEnter(request)!.Return;
                Hex.Eq(main.Frame(), T180Frame(0x27C6, BattlefieldHandoff.BuildSystemReturn(u.Id, point)), "source sees stored return before leave");
                var leave = T199TransferFrame(u.Request + 2, 0x1392); BitConverter.GetBytes(u.Id).CopyTo(leave, 22);
                Hex.Eq(main.Frame(), leave, "native type2 leave on source");
                bridge.HandleFrame(main.Link, 0x1393, T199TransferFrame(u.Leave, 0x1393)[6..]); main.Frame(); //1433
                var entered = bf.Frame(); var expected = T199TransferFrame(u.Enter, 0x138E);
                BitConverter.GetBytes(s.GameId).CopyTo(expected, 30); BitConverter.GetBytes(u.Id).CopyTo(expected, 38);
                BitConverter.GetBytes(s.TunnelKey).CopyTo(expected, 86); BitConverter.GetBytes(party).CopyTo(expected, 100);
                Hex.Eq(entered, expected, "owner10 entry matches retail after live identity/party/ticket substitution");
                Hex.True(s.InWorld && s.CurrentWorldId == 10 && !PartyWiring.Manager.FindByMember((int)u.Id)!.IsSys,
                    "forced BG retains the existing normal party");
                bridge.HandleFrame(bf.Link, 0x13F7, TunnelFrames.BuildBypassToClient(T199TransferFrame(u.Topo, 0xE828, capture: u.Capture), TunnelFrames.To(s.TunnelKey)));
                Hex.Eq(u.Client.Frame(), T199TransferFrame(u.Topo, 0xE828, capture: u.Capture), "BF S_LOAD_TOPO reaches original client");
                bridge.HandleFrame(bf.Link, 0x13CB, request);
                Hex.True(bf.Available == 0 && store.GetSystemReturn((int)u.Id) == point, "already-entered request cannot overwrite town return point");
            }
            control.Tick(bridge, now.AddSeconds(29));
            foreach (var u in users) Hex.Eq(bf.Frame(), T180Frame(0x1597, BitConverter.GetBytes(u.Id)), "second offer's timer now follows BF owner");
            foreach (var u in users)
            {
                var s = u.Client.Session; long party = PartyWiring.Manager.FindByMember((int)u.Id)!.Id;
                bridge.HandleFrame(bf.Link, 0x13CD, BitConverter.GetBytes(s.GameId));
                var leave = T199TransferFrame(u.Exit + 1, 0x1392); BitConverter.GetBytes(u.Id).CopyTo(leave, 22);
                Hex.Eq(bf.Frame(), leave, "BG exit begins the same native type2 continuation");
                if (u.Id == 1)
                {
                    bridge.ResetTunnelSequenceForWorld(0); // destination drops before source confirms
                    bridge.HandleFrame(bf.Link, 0x13CD, BitConverter.GetBytes(s.GameId));
                    Hex.Eq(bf.Frame(), leave, "destination reset cancels both pending transfer and Leaving gate; BF can retry");
                }
                bridge.HandleFrame(bf.Link, 0x1393, T199TransferFrame(u.FinalLeave, 0x1393)[6..]); bf.Frame(); //1433
                var returned = main.Frame(); var expected = T199TransferFrame(u.Return, 0x138E);
                BitConverter.GetBytes(s.GameId).CopyTo(expected, 30); BitConverter.GetBytes(u.Id).CopyTo(expected, 38);
                BitConverter.GetBytes(s.TunnelKey).CopyTo(expected, 86); BitConverter.GetBytes(party).CopyTo(expected, 100);
                Hex.Eq(returned, expected, "each user returns to their own captured integer location");
                Hex.True(s.InWorld && s.CurrentWorldId == 0 && PartyWiring.Manager.FindByMember((int)u.Id)!.Id == party,
                    "normal party survives return without system-party teardown");
                bridge.HandleFrame(main.Link, 0x13F7, TunnelFrames.BuildBypassToClient(T199TransferFrame(u.FinalTopo, 0xE828, capture: u.Capture), TunnelFrames.To(s.TunnelKey)));
                Hex.Eq(u.Client.Frame(), T199TransferFrame(u.FinalTopo, 0xE828, capture: u.Capture), "town S_LOAD_TOPO reaches original client");
                s.ForwardToWorld(Convert.FromHexString("04005461"));
                Hex.Eq(main.Frame()[30..], Convert.FromHexString("04005461"), "post-return client packets route to main");
            }
            control.TryHandle(bridge, bf.Link, transfers, store, 0x1513, OfferFor(1, 12535, 228), now.AddSeconds(40));
            first.Frame(); first.Frame();
            bridge.ResetTunnelSequenceForWorld(0);
            control.Tick(bridge, now.AddSeconds(60));
            Hex.True(main.Available == 0 && bf.Available == 0, "source World restart cancels stale pending offer timers");
        }
        finally
        {
            T185Environment.SetWorld(null); storeProperty.SetValue(null, oldStore);
            DungeonRouting.ResetForTest(); PartyWiring.ResetForTests(); LeaveGate.Shared.Clear(); File.Delete(map);
        }
    }
}
