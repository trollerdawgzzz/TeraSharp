// SPDX-License-Identifier: MIT
// Copyright (c) 2026 the TeraSharp contributors

using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;
using TeraSharp.Arbiter.Game;
using TeraSharp.Arbiter.Protocol;
using TeraSharp.Arbiter.World;

namespace TeraSharp.Arbiter.Tests;

public static partial class Tests
{
    private static byte[] T192HandoffFrame(string capture, int record, ushort opcode)
    {
        string path = FindRepoFile(Path.Combine("data", "t192", "control-frames.json"))
            ?? throw new FileNotFoundException("T192 captured control frames missing");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        return Convert.FromHexString(doc.RootElement.GetProperty(capture).EnumerateArray().Single(f =>
            f.GetProperty("source_record").GetInt32() == record && f.GetProperty("op").GetUInt16() == opcode)
            .GetProperty("hex").GetString()!);
    }

    private sealed class T192WorldPeer : IDisposable
    {
        private readonly Socket local;
        private readonly Socket peer;
        public readonly WorldLink Link;
        public T192WorldPeer(WorldBridge bridge, int id, int world)
        {
            using var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            listener.Bind(new IPEndPoint(IPAddress.Loopback, 0)); listener.Listen(1);
            local = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            local.Connect(listener.LocalEndPoint!);
            peer = listener.Accept(); peer.ReceiveTimeout = 2000;
            Link = new WorldLink(id, local, bridge, QuietLog()) { WorldId = world };
            ((List<WorldLink>)typeof(WorldBridge).GetField("_links", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(bridge)!).Add(Link);
        }
        public int Available => peer.Available;
        public byte[] Frame()
        {
            byte[] Read(int size)
            {
                var bytes = new byte[size];
                for (int n = 0; n < size;)
                {
                    int read = peer.Receive(bytes, n, size - n, SocketFlags.None);
                    Hex.True(read > 0, "World socket remains open"); n += read;
                }
                return bytes;
            }
            var head = Read(6);
            return head.Concat(Read(checked((int)BitConverter.ToUInt32(head, 0)) - 6)).ToArray();
        }
        public void Dispose() { local.Dispose(); peer.Dispose(); }
    }

    [Test] public static void T192_handoff_layouts_match_all_three_multiworld3_pairs()
    {
        if (FixtureOrSkip(Path.Combine("data", "t192", "control-frames.json"), "T192 control-frames.json") is null) return;
        foreach (var (request, enter, ready, response) in new[]
        {
            (7827, 7828, 7830, 7831), (8550, 8552, 8553, 8554), (14530, 14531, 14533, 14534),
        })
        {
            var req = T192HandoffFrame("cap_multiworld3", request, 0x13BE);
            var expected = T192HandoffFrame("cap_multiworld3", enter, 0x13BF);
            Hex.Eq(ContinentHandoff.EnterReply(req[6..], BitConverter.ToUInt32(expected, 6),
                BitConverter.ToUInt32(expected, 10))!, expected[6..], $"cap_multiworld3 {request}->{enter}, no normalization");
            Hex.Eq(ContinentHandoff.ReadyReply(T192HandoffFrame("cap_multiworld3", ready, 0x13C0)[6..])!,
                T192HandoffFrame("cap_multiworld3", response, 0x13C1)[6..], $"cap_multiworld3 {ready}->{response}");
        }
    }

    [Test] public static void T192_handoff_routes_resolved_user_and_records_owner_instance()
    {
        if (FixtureOrSkip(Path.Combine("data", "t192", "control-frames.json"), "T192 control-frames.json") is null) return;
        string map = Path.GetTempFileName();
        using var env = new T185Environment(null);
        using var store = StoreWithTwoAccounts();
        DungeonRouting.ResetForTest();
        try
        {
            File.WriteAllText(map, "{\"maps\":{\"376012\":{}}}");
            var bridge = new WorldBridge(WorldReplayTable.Load("/nonexistent", QuietLog()), QuietLog())
                { DbProxy = new DbProxyHandlers(store, QuietLog()) };
            using var source = new T192WorldPeer(bridge, 11, 7);
            using var owner = new T192WorldPeer(bridge, 14, 13);
            using var main = new T192WorldPeer(bridge, 1, 0);
            using var client = new T185Client(new DefinitionRegistry(QuietLog()),
                OpcodeTable.LoadFromFile(map, "376012"), QuietLog());
            var request = T192HandoffFrame("cap_multiworld3", 7827, 0x13BE);
            client.Session.PlayerId = 1;
            client.Session.GameId = BitConverter.ToUInt64(request, 6);
            client.Session.CurrentWorldId = 7;
            bridge.RegisterPlayer(client.Session);
            DungeonRouting.Channels.MapContinent(9781, 13);
            bridge.HandleFrame(source.Link, 0x13BE, request[6..]);
            Hex.Eq(owner.Frame(), T192HandoffFrame("cap_multiworld3", 7828, 0x13BF), "resolved user1, exact owner frame");
            Hex.True(source.Available == 0 && main.Available == 0, "no same-link ack or premature enter-world push");

            var ready = T192HandoffFrame("cap_multiworld3", 7830, 0x13C0);
            bridge.HandleFrame(owner.Link, 0x13C0, ready[6..]);
            Hex.Eq(source.Frame(), T192HandoffFrame("cap_multiworld3", 7831, 0x13C1), "ready goes to current World7, not constant World0");
            Hex.True(main.Available == 0 && owner.Available == 0, "ready is sent only to the resolved user's current World");
            Hex.True(store.GetCharacter(1)!.InstancePdId == BitConverter.ToInt32(ready, 6 + DbProxyHandlers.DungeonCtxInstancePdId),
                "cross-World early path retains the same-World instance state update (native User+0x4024)");
            bridge.UnregisterPlayer(client.Session.GameId, 7, client.Session.TunnelKey);

            store.SaveInstancePdId(1, 0);
            client.Session.PlayerId = 2;
            bridge.RegisterPlayer(client.Session);
            var memberReady = (byte[])ready.Clone();
            BitConverter.GetBytes(2u).CopyTo(memberReady, 10);
            bridge.HandleFrame(owner.Link, 0x13C0, memberReady[6..]);
            var memberResponse = T192HandoffFrame("cap_multiworld3", 7831, 0x13C1);
            BitConverter.GetBytes(2u).CopyTo(memberResponse, 10);
            Hex.Eq(source.Frame(), memberResponse, "the ready PDId identifies the entrant even when context names another party owner");
            Hex.True(store.GetCharacter(2)!.InstancePdId == BitConverter.ToInt32(ready, 154)
                && store.GetCharacter(1)!.InstancePdId == 0, "instance state belongs to resolved entrant2, not context owner1");
            bridge.UnregisterPlayer(client.Session.GameId, 7, client.Session.TunnelKey);

            // handoff1 C_ENTER_DUNGEON2799 leads to request25381 for user10. The broken full+10 is 1;
            // the correction changes just that PDId field, preserving the entire live context.
            var live = T192HandoffFrame("cap_handoff1", 25381, 0x13BE);
            var expected = T192HandoffFrame("cap_handoff1", 25382, 0x13BF);
            BitConverter.GetBytes(10u).CopyTo(expected, 10);
            client.Session.PlayerId = 10;
            client.Session.GameId = BitConverter.ToUInt64(live, 6);
            bridge.RegisterPlayer(client.Session);
            bridge.HandleFrame(source.Link, 0x13BE, live[6..]);
            Hex.Eq(owner.Frame(), expected, "handoff1 correction: 13BF addresses actual user10, not captured user1");
            bridge.UnregisterPlayer(client.Session.GameId, 7, client.Session.TunnelKey);

            // The other applicant's context names party owner10, but its opaque handle is
            // a different User. Never use DungeonEnterContext's owner as the entering user.
            var other = T192HandoffFrame("cap_handoff1", 25358, 0x13BE);
            var otherExpected = T192HandoffFrame("cap_handoff1", 25359, 0x13BF);
            Hex.True(BitConverter.ToUInt32(other, 38) == 10, "captured context contains party owner10");
            BitConverter.GetBytes(9u).CopyTo(otherExpected, 10);
            client.Session.PlayerId = 9;
            client.Session.GameId = BitConverter.ToUInt64(other, 6);
            bridge.RegisterPlayer(client.Session);
            bridge.HandleFrame(source.Link, 0x13BE, other[6..]);
            Hex.Eq(owner.Frame(), otherExpected, "request handle resolves user9 even though nested context still names10");
            bridge.UnregisterPlayer(client.Session.GameId, 7, client.Session.TunnelKey);

            bridge.HandleFrame(source.Link, 0x13BE, live[6..]);
            bridge.HandleFrame(owner.Link, 0x13C0, ready[6..]);
            Hex.True(owner.Available == 0 && source.Available == 0 && main.Available == 0,
                "unknown request handle and stale ready PDId produce no fabricated user1 or fallback reply");
        }
        finally { DungeonRouting.ResetForTest(); File.Delete(map); }
    }

    [Test] public static void T192_teleport_type2_continues_to_owner_without_lobby_and_loads_destination_blob()
    {
        if (FixtureOrSkip(Path.Combine("data", "t192", "control-frames.json"), "T192 control-frames.json") is null) return;
        string sourceFixture = Path.Combine("data", "t192", "source-world-blob.bin");
        string destinationFixture = Path.Combine("data", "t192", "destination-world-blob.bin");
        string? sourcePath = FindRepoFile(sourceFixture), destinationPath = FindRepoFile(destinationFixture);
        if (sourcePath == null || destinationPath == null)
        {
            Skip.Because("optional capture fixture(s) missing: " + string.Join(", ",
                new[] { sourcePath == null ? sourceFixture : null, destinationPath == null ? destinationFixture : null }
                    .Where(path => path != null)));
            return;
        }
        var sourceBlob = File.ReadAllBytes(sourcePath);
        var destinationBlob = File.ReadAllBytes(destinationPath);
        var initial = T192HandoffFrame("cap_multiworld3", 6486, 0x138E);
        var teleport = T192HandoffFrame("cap_multiworld3", 7882, 0x1445);
        var nativeMove = CrossWorldHandoff.Parse(teleport[6..])!;
        var expectedEnter = T192HandoffFrame("cap_multiworld3", 7908, 0x138E);
        Hex.Eq(CrossWorldHandoff.BuildEnter(initial[6..], nativeMove, 1), expectedEnter[6..],
            "native initial6486 + teleport7882 -> owner7908: every byte, no normalization");
        Hex.Eq(CrossWorldHandoff.BuildLeave(nativeMove, 1), T192HandoffFrame("cap_multiworld3", 7884, 0x1392)[6..],
            "TeleportStart leaves type2/reason0 with the existing gameId");
        var invalid = teleport[6..]; BitConverter.GetBytes(uint.MaxValue).CopyTo(invalid, 0);
        Hex.True(CrossWorldHandoff.Parse(invalid) == null && CrossWorldHandoff.Parse(new byte[49]) == null,
            "short or out-of-range variable data cannot start a transfer");

        string map = Path.GetTempFileName();
        using var env = new T185Environment(null);
        using var store = StoreWithTwoAccounts();
        DungeonRouting.ResetForTest(); PartyWiring.ResetForTests(); LeaveGate.Shared.Clear();
        try
        {
            File.WriteAllText(map, "{\"maps\":{\"376012\":{}}}");
            var bridge = new WorldBridge(WorldReplayTable.Load("/nonexistent", QuietLog()), QuietLog())
                { DbProxy = new DbProxyHandlers(store, QuietLog()) };
            T185Environment.SetWorld(bridge);
            using var source = new T192WorldPeer(bridge, 11, 0);
            using var owner = new T192WorldPeer(bridge, 14, 13);
            using var client = new T185Client(new DefinitionRegistry(QuietLog()),
                OpcodeTable.LoadFromFile(map, "376012"), QuietLog());
            client.Session.PlayerId = 1;
            client.Session.SelectedCharacter = new FakeCharacter { Id = 1, Name = "dob", Level = 70, Zone = 7005 };
            client.Session.GameId = nativeMove.GameId;
            client.Session.CurrentWorldId = 0;
            client.Session.TunnelKey = bridge.AllocateTunnelKey(0);
            client.Session.EnterWorld();
            LeaveGate.Shared.Entered(client.Session.GameId);
            // Retail has native User pointers; TeraSharp uses its GameId as that opaque
            // handle. Normalize exactly this field for the live wiring test, never PDIds.
            BitConverter.GetBytes(client.Session.GameId).CopyTo(initial, 30);
            BitConverter.GetBytes(client.Session.GameId).CopyTo(teleport, 14);
            BitConverter.GetBytes(client.Session.GameId).CopyTo(expectedEnter, 30);
            BitConverter.GetBytes(client.Session.TunnelKey).CopyTo(initial, 86);
            bridge.SendFrame(0, WorldBridge.OpPlayerEnter, initial[6..]);
            Hex.Eq(source.Frame(), initial, "cache the exact live initial AS_ENTER_WORLD payload");
            Hex.True(BitConverter.ToUInt64(initial, 100) == 0 && initial[108] == 0,
                "captured initial login predates the test's matched party");
            using var sheet = new T184hDungeonSheet(9781, total: 2);
            PartyWiring.Register(client.Session);
            PartyWiring.Manager.Register(P(100, 2, "member"));
            var formed = PartyWiring.Manager.FormMatchedParty(new[]
            {
                new PartyManager.MatchedMember(1, MatchRole.Dps), new PartyManager.MatchedMember(2, MatchRole.Dps),
            }, false, 9781);
            var party = PartyWiring.Manager.FindByMember(1)!;
            Hex.True(formed.Rejected == null && party.IsSys, "a match forms after the cached login");
            BitConverter.GetBytes((ulong)party.Id).CopyTo(expectedEnter, 100);
            expectedEnter[108] = 1;
            bridge.AllocateTunnelKey(13); // slot5 occupied: live allocator starts at5, native capture started at0
            BitConverter.GetBytes(6u).CopyTo(expectedEnter, 86); // only runtime ticket differs from captured1
            DungeonRouting.Channels.MapContinent(9781, 13);
            bridge.HandleFrame(source.Link, CrossWorldHandoff.SA_TELEPORT, teleport[6..]);
            Hex.Eq(source.Frame(), T192HandoffFrame("cap_multiworld3", 7884, 0x1392), "source type2 leave is byte-exact");
            Hex.True(owner.Available == 0 && client.Available == 0, "wait for source saves; no premature owner entry or lobby packet");
            var expectedBlob = (byte[])sourceBlob.Clone();
            foreach (var (offset, count) in new[] { (220, 12), (236, 12), (304, 4) })
                destinationBlob.AsSpan(offset, count).CopyTo(expectedBlob.AsSpan(offset, count));
            Hex.Eq(CrossWorldHandoff.StampLocation(sourceBlob, nativeMove, 13), expectedBlob,
                "patch only capture-proven location fields; retain every other source-blob byte");
            store.SaveWorldBlob(1, sourceBlob); // captured source save7903 completes before1393:7906
            bridge.HandleFrame(source.Link, WorldBridge.OpSaLeaveWorld,
                T192HandoffFrame("cap_multiworld3", 7906, 0x1393)[6..]);
            Hex.Eq(source.Frame(), T192HandoffFrame("cap_multiworld3", 7907, 0x1433), "delete duplicates1393 gameId, not its native User pointer");
            Hex.Eq(owner.Frame(), expectedEnter, "owner type2 entry matches7908 with live handle, ticket and newly formed party");
            Hex.True(client.Session.InWorld && client.Session.CurrentWorldId == 13 && client.Session.TunnelKey == 6
                && client.Available == 0 && LeaveGate.Shared.IsEntering(client.Session.GameId),
                "same client remains in World, destination owns fresh ticket6, and its enter completion gates a later leave");
            var saved = store.GetCharacter(1)!.WorldBlob!;
            foreach (var (offset, count) in new[] { (220, 12), (236, 12), (304, 4) })
                Hex.Eq(saved.AsSpan(offset, count).ToArray(), destinationBlob.AsSpan(offset, count).ToArray(),
                    $"destination load7911 location bytes at blob+{offset}");
            Hex.True(store.GetCharacter(1)!.Zone == 9781 && store.GetCharacter(1)!.InstancePdId == 0x0AF0000D,
                "character row and instance follow the destination blob");
            bridge.NotifyTopoLoaded(client.Session.CurrentWorldId, client.Session.PlayerId);
            Hex.True(BitConverter.ToUInt16(owner.Frame(), 4) == 0x1390, "existing topo-finish prelude reaches owner");
            Hex.Eq(owner.Frame(), T192HandoffFrame("cap_multiworld3", 8030, 0x138F), "C_LOAD_TOPO_FIN continuation reaches owner13");
            Hex.True(source.Available == 0, "source receives no destination topo-finish traffic");
            bridge.UnregisterPlayer(client.Session.GameId, 13, client.Session.TunnelKey);
            T185Environment.SetWorld(null); // dispose client without creating an unrelated leave request
        }
        finally
        {
            T185Environment.SetWorld(null); DungeonRouting.ResetForTest(); PartyWiring.ResetForTests();
            LeaveGate.Shared.Clear(); File.Delete(map);
        }
    }
}
