// SPDX-License-Identifier: MIT
// Copyright (c) 2026 the TeraSharp contributors

using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;
using TeraSharp.Arbiter.Game;
using TeraSharp.Arbiter.Handlers;
using TeraSharp.Arbiter.Protocol;
using TeraSharp.Arbiter.World;

namespace TeraSharp.Arbiter.Tests;

public static partial class Tests
{
    private static JsonDocument? T194FramesOrSkip()
    {
        string? path = FindRepoFile(Path.Combine("data", "t194", "polishing-frames.json"));
        if (path == null)
        {
            Skip.Because("T194 tracked polishing capture fixture is absent");
            return null;
        }
        return JsonDocument.Parse(File.ReadAllText(path));
    }

    private static byte[] T194Bytes(JsonElement frame)
        => Convert.FromHexString(frame.GetProperty("hex").GetString()!);

    [Test] public static void T194_polishing_queries_forward_to_current_world_without_zero_replies()
    {
        using var data = T194FramesOrSkip(); if (data == null) return;
        var tunnels = data.RootElement.GetProperty("query_tunnels").EnumerateArray().ToArray();
        var requests = tunnels.Where(f => f.GetProperty("op").GetInt32() == 0x13F6).ToArray();
        var replies = tunnels.Where(f => f.GetProperty("op").GetInt32() == 0x13F7).ToArray();
        var retail = data.RootElement.GetProperty("retail_client").EnumerateArray().ToArray();
        Hex.Eq(T194Bytes(replies[0])[38..], T194Bytes(retail.Single(f => f.GetProperty("n").GetInt32() == 189)),
            "retail LIST is already inside World's bypass response");
        Hex.Eq(T194Bytes(replies[1])[38..], T194Bytes(retail.Single(f => f.GetProperty("n").GetInt32() == 190)),
            "retail EXP_INFO includes sheet maxExp=1 even before any polishing");

        string map = Path.GetTempFileName();
        using var env = new T185Environment(null);
        try
        {
            File.WriteAllText(map, "{\"maps\":{\"376012\":{}}}");
            var bridge = new WorldBridge(WorldReplayTable.Load("/nonexistent", QuietLog()), QuietLog());
            using var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            listener.Bind(new IPEndPoint(IPAddress.Loopback, 0)); listener.Listen(1);
            using var local = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            local.Connect(listener.LocalEndPoint!);
            using var peer = listener.Accept(); peer.ReceiveTimeout = 2000;
            var link = new WorldLink(1, local, bridge, QuietLog());
            ((List<WorldLink>)typeof(WorldBridge).GetField("_links", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(bridge)!).Add(link);
            T185Environment.SetWorld(bridge);
            using var client = new T185Client(new DefinitionRegistry(QuietLog()),
                OpcodeTable.LoadFromFile(map, "376012"), QuietLog());
            client.Session.GameId = BitConverter.ToUInt64(T194Bytes(requests[0]), 14);
            client.Session.SelectedCharacter = new FakeCharacter { Id = 1003, Name = "polishing", Level = 70 };
            client.Session.EnterWorld();
            foreach (int world in new[] { 0, 13 })
            {
                link.WorldId = world; client.Session.CurrentWorldId = world;
                foreach (var captured in requests)
                {
                    byte[] expected = T194Bytes(captured);
                    byte[] request = expected[30..];
                    bool handled = BitConverter.ToUInt16(request, 2) == ArbiterClientHandlers.C_RQ_SKILL_POLISHING_LIST
                        ? ArbiterClientHandlers.OnRqSkillPolishingList(client.Session, request.AsMemory(4), QuietLog())
                        : ArbiterClientHandlers.OnRqSkillPolishingExpInfo(client.Session, request.AsMemory(4), QuietLog());
                    Hex.True(handled, "registered handler forwards the complete retail request");
                    var actual = new byte[expected.Length];
                    for (int n = 0; n < actual.Length;)
                    {
                        int read = peer.Receive(actual, n, actual.Length - n, SocketFlags.None);
                        Hex.True(read > 0, "World socket stays open"); n += read;
                    }
                    // The only runtime value is the AS_BYPASS_FROM_CLIENT monotonic tick.
                    expected.AsSpan(22, 8).CopyTo(actual.AsSpan(22, 8));
                    Hex.Eq(actual, expected, $"cap_final2b raw371+{captured.GetProperty("source_offset").GetInt32()}");
                    Hex.True(client.Available == 0, "no synthetic zero polishing state masks World's state");
                }
            }
            T185Environment.SetWorld(null);
        }
        finally { T185Environment.SetWorld(null); File.Delete(map); }
    }

    [Test] public static void T194_full_retail_polishing_writes_reload_byte_exact_without_seeded_end_state()
    {
        using var data = T194FramesOrSkip(); if (data == null) return;
        // Existing fixture helper creates the captured owner 1003 before any child rows.
        // Keep every request and its owner-transaction atoms unmodified.
        using var store = T181Store();
        var frames = data.RootElement.GetProperty("db_pairs").EnumerateArray().ToArray();
        int writes = 0;
        for (int i = 0; i < frames.Length; i += 2)
        {
            byte[] request = T194Bytes(frames[i]);
            byte[] reply = T194Bytes(frames[i + 1]);
            ushort requestOp = BitConverter.ToUInt16(request, 4);
            var (op, body) = RunHandler1(requestOp, request[6..], store);
            Hex.True(op == BitConverter.ToUInt16(reply, 4), "retail reply opcode");
            Hex.Eq(body, reply[6..], $"cap_final2b {frames[i].GetProperty("source_record").GetInt32()} -> "
                + frames[i + 1].GetProperty("source_record").GetInt32());
            if (requestOp != 0x2975) writes++;
        }
        Hex.True(writes == 199, "11 EXP writes + 180 upgrades + 6 unlocks + 2 option changes");
        var state = store.GetPolishing(1003);
        Hex.True(state.Level == 180 && state.Point == 0 && state.TotalPoint == 180 && state.Exp == 24362201,
            "cap_final2b50068 restores the actual maximum-level state after spending all points");
        Hex.True(store.GetPolishingOptions(1003).Count == 6 && store.GetPolishingLevels(1003).Count == 3,
            "all unlocked options, active selections and final skill levels survive the load");
    }
}
