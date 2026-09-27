// SPDX-License-Identifier: MIT
// Copyright (c) 2026 the TeraSharp contributors

using System.Net.Sockets;
using System.Reflection;
using TeraSharp.Arbiter.Handlers;
using TeraSharp.Arbiter.Protocol;
using TeraSharp.Arbiter.World;

namespace TeraSharp.Arbiter.Tests;

public static partial class Tests
{
    [Test] public static void T192_last_world_link_resets_registered_and_departed_characters_only()
    {
        // User's live restart reproduction: /api/reset-character reported enter-world/gm-push
        // and repaired items/skills. This tests that exact repair through the actual link-close
        // callback, not a synthetic protocol reply. Requires status/T192-PATCH.diff.
        const int here = 19201, there = 19202, left = 19203, moved = 19204;
        var ids = new[] { here, there, left, moved };
        string map = Path.GetTempFileName();
        using var env = new T185Environment(null);
        var bridge = new WorldBridge(WorldReplayTable.Load("/nonexistent", QuietLog()), QuietLog());
        const BindingFlags fields = BindingFlags.Instance | BindingFlags.NonPublic;
        T Field<T>(string name) => (T)typeof(WorldBridge).GetField(name, fields)!.GetValue(bridge)!;
        var links = Field<List<WorldLink>>("_links");
        var worlds = Field<PerWorld<WorldRuntime>>("_worlds");
        var tunnels = Field<Dictionary<(int World, uint Ticket), TunnelReorderBuffer>>("_tunnels");
        var departed = Field<DepartedTickets>("_departed");
        using var w0a = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        using var w0b = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        using var w13 = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        using var w0next = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        var first = new WorldLink(1, w0a, bridge, QuietLog()) { WorldId = 0 };
        var last = new WorldLink(2, w0b, bridge, QuietLog()) { WorldId = 0 };
        var other = new WorldLink(3, w13, bridge, QuietLog()) { WorldId = 13 };
        var restarted = new WorldLink(4, w0next, bridge, QuietLog()) { WorldId = 0 };
        long entered = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 60;
        try
        {
            File.WriteAllText(map, "{\"maps\":{\"376012\":{}}}");
            var opcodes = OpcodeTable.LoadFromFile(map, "376012");
            var definitions = new DefinitionRegistry(QuietLog());
            using var a = new T185Client(definitions, opcodes, QuietLog());
            using var b = new T185Client(definitions, opcodes, QuietLog());
            using var c = new T185Client(definitions, opcodes, QuietLog());
            using var d = new T185Client(definitions, opcodes, QuietLog());
            var clients = new[] { a, b, c, d };
            links.AddRange(new[] { first, last, other });
            worlds.For(0).MarkReady(); worlds.For(13).MarkReady();
            for (int n = 0; n < ids.Length; n++)
            {
                int id = ids[n];
                CharacterTransientState.Reset(id, bridge);
                var session = clients[n].Session;
                session.PlayerId = (uint)id; session.GameId = WorldRuntime.GameIdBase | (uint)id;
                session.TunnelKey = (uint)(100 + n); session.CurrentWorldId = n == 1 ? 13 : 0;
                bridge.RegisterPlayer(session);
                DbProxyHandlers.MarkEnteredWorld(id, entered);
                DbProxyHandlers.GameIdByPlayer[id] = session.GameId;
                Hex.True(ArbiterClientHandlers.TryTakeGmSkillPush(id), "live entry consumed its push");
            }
            // Client closes first; it is absent from InWorldSessions by the time World dies.
            bridge.UnregisterPlayer(c.Session.GameId, 0, c.Session.TunnelKey);
            // Another departed character has already relogged on the still-live World.
            bridge.UnregisterPlayer(d.Session.GameId, 0, d.Session.TunnelKey);
            d.Session.CurrentWorldId = 13; bridge.RegisterPlayer(d.Session);
            var ours = tunnels[(0, a.Session.TunnelKey)];
            var theirs = tunnels[(13, b.Session.TunnelKey)];
            ours.NextSeq = 7; ours.Pending[9] = new byte[] { 1 };
            theirs.NextSeq = 11; theirs.Pending[13] = new byte[] { 2 };
            departed.Add(0, 7001); departed.Add(13, 7001);

            bridge.OnWorldLinkClosed(first);
            Hex.True(worlds.For(0).IsReady && ours.NextSeq == 7 && ids.All(id =>
                DbProxyHandlers.GameIdByPlayer.ContainsKey(id) && !ArbiterClientHandlers.TryTakeGmSkillPush(id)),
                "one worker closing does not reset any character while another link is live");

            bridge.OnWorldLinkClosed(last);
            foreach (int id in new[] { here, left })
            {
                Hex.True(!DbProxyHandlers.GameIdByPlayer.ContainsKey(id)
                    && DbProxyHandlers.SecondsInWorld(id, entered + 120) == 0,
                    "both registered and client-first departed characters lose stale entry state");
                Hex.True(ArbiterClientHandlers.TryTakeGmSkillPush(id), "next login has a fresh GM push");
            }
            foreach (int id in new[] { there, moved })
                Hex.True(DbProxyHandlers.GameIdByPlayer.ContainsKey(id)
                    && DbProxyHandlers.SecondsInWorld(id, entered + 120) == 120
                    && !ArbiterClientHandlers.TryTakeGmSkillPush(id), "other World ownership survives");
            Hex.True(!worlds.For(0).IsReady && worlds.For(13).IsReady
                && ours.NextSeq == 0 && ours.Pending.Count == 0
                && theirs.NextSeq == 11 && theirs.Pending.Count == 1
                && !departed.Contains(0, 7001) && departed.Contains(13, 7001),
                "readiness, tunnel queues and departed tickets reset only on the failed World");

            bridge.OnWorldLinkClosed(last);
            Hex.True(!ArbiterClientHandlers.TryTakeGmSkillPush(here), "duplicate close is inert");
            links.Add(restarted); worlds.For(0).MarkReady();
            DbProxyHandlers.MarkEnteredWorld(here, entered);
            DbProxyHandlers.GameIdByPlayer[here] = a.Session.GameId;
            bridge.OnWorldLinkClosed(restarted);
            Hex.True(!DbProxyHandlers.GameIdByPlayer.ContainsKey(here)
                && ArbiterClientHandlers.TryTakeGmSkillPush(here), "the next World restart resets again");
            Hex.True(clients.All(client => client.Available == 0), "cleanup emits no guessed protocol frames");

            bridge.ResetTunnelSequence();
            Hex.True(!DbProxyHandlers.GameIdByPlayer.ContainsKey(there)
                && !DbProxyHandlers.GameIdByPlayer.ContainsKey(moved)
                && theirs.NextSeq == 0 && theirs.Pending.Count == 0 && departed.Count == 0,
                "explicit all-World reset uses the same transient cleanup");
        }
        finally
        {
            foreach (int id in ids) CharacterTransientState.Reset(id, bridge);
            File.Delete(map);
        }
    }
}
