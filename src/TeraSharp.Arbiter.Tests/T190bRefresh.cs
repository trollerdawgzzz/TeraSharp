// SPDX-License-Identifier: MIT
// Copyright (c) 2026 the TeraSharp contributors

using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;
using TeraSharp.Arbiter.Game;
using TeraSharp.Arbiter.Network;
using TeraSharp.Arbiter.Protocol;
using TeraSharp.Arbiter.World;

namespace TeraSharp.Arbiter.Tests;

public static partial class Tests
{
    [Test] public static void T190b_captured_world_entry_refreshes_retained_party_on_current_world()
    {
        if (FixtureOrSkip(Path.Combine("data", "t190b", "party-frames.json"), "T190b party-frames.json") is null) return;
        string fixture = FindRepoFile(Path.Combine("data", "t190b", "party-frames.json"))
            ?? throw new FileNotFoundException("T190b party capture fixture missing");
        using var doc = JsonDocument.Parse(File.ReadAllText(fixture));
        byte[] Frame(int record) => Convert.FromHexString(doc.RootElement.EnumerateArray()
            .Single(f => f.GetProperty("source_record").GetInt32() == record).GetProperty("hex").GetString()!);
        byte[] request = Frame(13913), expected = Frame(14041);
        Hex.True(BitConverter.ToUInt16(request, 4) == 0x138C && request.Length == 15350,
            "cap_2man_b raw13913+15: complete SA_ENTER_WORLD, not a synthetic header");
        Hex.Eq(expected, "0A 00 00 00 AD 13 EB 03 00 00", "raw14041 captured response");
        ulong handle = LeaveGate.GameIdOf(request.AsSpan(6));
        // Native payload16 is the opaque Arbiter User pointer, not the client GameId
        // at24. TeraSharp intentionally uses its GameId for that handle; register the
        // captured value as the lookup key so this test does not rewrite request bytes.
        ulong? oldGameId = DbProxyHandlers.GameIdByPlayer.TryGetValue(1003, out var old) ? old : null;
        string map = Path.GetTempFileName();
        using var env = new T185Environment(null);
        PartyWiring.ResetForTests(); LeaveGate.Shared.Clear();
        try
        {
            File.WriteAllText(map, "{\"maps\":{\"376012\":{}}}");
            var bridge = new WorldBridge(WorldReplayTable.Load("/nonexistent", QuietLog()), QuietLog());
            T185Environment.SetWorld(bridge);
            using var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            listener.Bind(new IPEndPoint(IPAddress.Loopback, 0)); listener.Listen(1);
            using var worldSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            worldSocket.Connect(listener.LocalEndPoint!);
            using var peer = listener.Accept(); peer.ReceiveTimeout = 2000;
            var link = new WorldLink(26, worldSocket, bridge, QuietLog());
            var links = (List<WorldLink>)typeof(WorldBridge).GetField("_links", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(bridge)!;
            links.Add(link);
            using var clientSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            using var session = new GameSession(clientSocket,
                new PacketDispatcher(Microsoft.Extensions.Logging.Abstractions.NullLogger<PacketDispatcher>.Instance),
                OpcodeTable.LoadFromFile(map, "376012"), new DefinitionRegistry(QuietLog()), 376012, 100, QuietLog());
            session.SelectedCharacter = new FakeCharacter { Id = 1003, Name = "New" };
            session.PlayerId = 1003; session.GameId = handle; session.TunnelKey = 3;
            session.EnterWorld(); PartyWiring.Register(session);
            PartyWiring.Manager.Register(P(2, 1, "dobb"));
            PartyWiring.Manager.FormMatchedParty(T138dMembers((1, MatchRole.Dps), (1003, MatchRole.Dps)), false, dungeonId: 9781);
            PartyWiring.Manager.Unregister(session.TunnelKey);
            PartyWiring.Register(session);
            var retained = PartyWiring.Manager.FindByMember(1003)!;
            var handler = new DbProxyHandlers(null!, QuietLog());
            // Capture uses World0/system party. Repeat with World13/ordinary party to
            // exercise the native current-World route and both MemberEnterWorld branches.
            foreach (var state in new[] { (World: 0, IsSys: true), (World: 13, IsSys: false) })
            {
                session.CurrentWorldId = state.World; link.WorldId = state.World; retained.IsSys = state.IsSys;
                LeaveGate.Shared.Entering(handle);
                Hex.True(handler.TryHandle(bridge, link, 0x138C, request[6..]), "real handler accepts captured entry ack");
                byte[] actual = new byte[expected.Length];
                for (int read = 0; read < actual.Length;)
                {
                    int count = peer.Receive(actual, read, actual.Length - read, SocketFlags.None);
                    Hex.True(count > 0, "World connection remains open"); read += count;
                }
                Hex.Eq(actual, expected, "post-entry refresh preserves the exact captured UserDbId and frame");
                Hex.True(!LeaveGate.Shared.IsEntering(handle), "entry completion still releases the leave gate");
            }
            PartyWiring.ResetForTests();
            Hex.True(handler.TryHandle(bridge, link, 0x138C, request[6..]) && peer.Available == 0,
                "an online character without a party gets no fabricated refresh");
            bridge.UnregisterPlayer(handle, session.CurrentWorldId, session.TunnelKey);
            Hex.True(handler.TryHandle(bridge, link, 0x138C, request[6..]) && peer.Available == 0,
                "an unresolved session gets no refresh");
            T185Environment.SetWorld(null);
        }
        finally
        {
            T185Environment.SetWorld(null); PartyWiring.ResetForTests(); LeaveGate.Shared.Clear(); File.Delete(map);
            DbProxyHandlers.GameIdByPlayer.TryRemove(1003, out _);
            if (oldGameId is { } prior) DbProxyHandlers.GameIdByPlayer[1003] = prior;
        }
    }
}
