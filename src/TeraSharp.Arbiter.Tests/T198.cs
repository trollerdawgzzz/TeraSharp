// SPDX-License-Identifier: MIT
// Copyright (c) 2026 the TeraSharp contributors

using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;
using TeraSharp.Arbiter.World;

namespace TeraSharp.Arbiter.Tests;

public static partial class Tests
{
    private static JsonDocument? T198Frames(bool requirePatch = false)
    {
        string? path = FindRepoFile(Path.Combine("data", "t198", "frames.json"));
        if (path == null) { Skip.Because("data/t198/frames.json absent"); return null; }
        if (requirePatch)
            Hex.True(typeof(WorldBridge).GetMethod("FlushStalledTunnels", BindingFlags.Instance | BindingFlags.NonPublic) != null,
                "apply status/T198-PATCH.diff to human-owned WorldBridge.cs before running this regression");
        return JsonDocument.Parse(File.ReadAllText(path));
    }

    private static byte[] T198Frame(JsonDocument frames, string capture, int record)
        => Convert.FromHexString(frames.RootElement.GetProperty(capture).EnumerateArray().Single(f =>
            (f.TryGetProperty("source_record", out var source) ? source.GetInt32() : f.GetProperty("n").GetInt32()) == record)
            .GetProperty("hex").GetString()!);

    private static byte[][] T198TunnelPayloads(JsonDocument frames)
        => frames.RootElement.GetProperty("cap_instance1_tunnel").EnumerateArray()
            .OrderBy(f => f.GetProperty("sequence").GetInt32())
            .Select(f => Convert.FromHexString(f.GetProperty("hex").GetString()!)[6..]).ToArray();

    private static Task T198Worker(Action action)
        => Task.Factory.StartNew(action, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    private static void T198ConcurrentLogin(bool watchdog)
    {
        using var frames = T198Frames(requirePatch: true); if (frames == null) return;
        byte[][] payloads = T198TunnelPayloads(frames);
        Hex.True(payloads.Length == 18, "complete captured login prefix, sequence 0 through 17");
        for (int i = 0; i < payloads.Length; i++)
        {
            var recipient = TunnelFrames.ParseBypassToClient(payloads[i])!.Recipients.Single();
            Hex.True(recipient.Ticket == 5 && recipient.Sequence == i, "captured per-ticket sequence " + i);
        }
        var bridge = new WorldBridge(WorldReplayTable.Load("/nonexistent", QuietLog()), QuietLog());
        using var socket0 = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        using var socket1 = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        var firstLink = new WorldLink(82, socket0, bridge, QuietLog());
        var otherLink = new WorldLink(98, socket1, bridge, QuietLog());
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var received = new ConcurrentQueue<byte[]>();
        int calls = 0;
        bridge.RegisterTunnelRoute(5, packet =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                entered.Set();
                if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("test login callback release");
            }
            received.Enqueue(packet);
        });
        Task first = T198Worker(() => bridge.HandleFrame(firstLink, WorldBridge.OpTunnelToClient, payloads[0]));
        Task? second = null;
        try
        {
            Hex.True(entered.Wait(TimeSpan.FromSeconds(3)), "seq0 callback entered before later link frames");
            second = T198Worker(() =>
            {
                foreach (int i in watchdog ? new[] { 16, 17 } : Enumerable.Range(1, 17))
                    bridge.HandleFrame(otherLink, WorldBridge.OpTunnelToClient, payloads[i]);
                if (watchdog)
                    typeof(WorldBridge).GetMethod("FlushStalledTunnels", BindingFlags.Instance | BindingFlags.NonPublic)!
                        .Invoke(bridge, new object[] { DateTime.UtcNow.AddSeconds(1) });
            });
            Hex.True(second.Wait(TimeSpan.FromSeconds(3)), "other link/watchdog can enqueue while the callback is blocked");
            Hex.True(received.IsEmpty, "artisan packets cannot overtake the in-flight S_LOGIN callback");
        }
        finally
        {
            release.Set();
            Hex.True(first.Wait(TimeSpan.FromSeconds(3)), "first delivery finishes");
            if (second != null) Hex.True(second.Wait(TimeSpan.FromSeconds(3)), "second worker finishes");
        }
        int[] expected = watchdog ? new[] { 0, 16, 17 } : Enumerable.Range(0, 18).ToArray();
        byte[][] actual = received.ToArray();
        Hex.True(actual.Length == expected.Length, "each ready frame delivered exactly once");
        for (int i = 0; i < actual.Length; i++)
            Hex.Eq(actual[i], TunnelFrames.ParseBypassToClient(payloads[expected[i]])!.ClientPacket,
                "cap_instance1 World sequence " + expected[i] + " retains delivery order");
    }

    [Test] public static void T198_parallel_links_cannot_deliver_artisan_before_login()
        => T198ConcurrentLogin(watchdog: false);

    [Test] public static void T198_stall_watchdog_shares_the_ordered_delivery_queue()
        => T198ConcurrentLogin(watchdog: true);

    [Test] public static void T198_ticket_reuse_and_sequence_reset_discard_queued_old_packets()
    {
        using var frames = T198Frames(requirePatch: true); if (frames == null) return;
        byte[][] payloads = T198TunnelPayloads(frames);
        foreach (bool replaceOwner in new[] { false, true })
        {
            var bridge = new WorldBridge(WorldReplayTable.Load("/nonexistent", QuietLog()), QuietLog());
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            var link = new WorldLink(82, socket, bridge, QuietLog());
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            var previous = new ConcurrentQueue<byte[]>();
            var replacement = new ConcurrentQueue<byte[]>();
            int calls = 0;
            bridge.RegisterTunnelRoute(5, packet =>
            {
                if (Interlocked.Increment(ref calls) == 1)
                {
                    entered.Set();
                    if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("test old-owner callback release");
                }
                previous.Enqueue(packet);
            });
            Task first = T198Worker(() => bridge.HandleFrame(link, WorldBridge.OpTunnelToClient, payloads[0]));
            try
            {
                Hex.True(entered.Wait(TimeSpan.FromSeconds(3)), "old seq0 callback entered");
                bridge.HandleFrame(link, WorldBridge.OpTunnelToClient, payloads[1]);
                Hex.True(previous.IsEmpty, "seq1 queued behind old callback");
                if (replaceOwner)
                {
                    bridge.UnregisterTunnelRoute(5);
                    bridge.RegisterTunnelRoute(5, packet => replacement.Enqueue(packet));
                }
                else bridge.ResetTunnelSequence(5);
                bridge.HandleFrame(link, WorldBridge.OpTunnelToClient, payloads[0]);
            }
            finally
            {
                release.Set();
                Hex.True(first.Wait(TimeSpan.FromSeconds(3)), "old callback finishes for its original owner");
            }
            Hex.True(previous.Count == (replaceOwner ? 1 : 2), "queued old seq1 cancelled; reset still delivers its new seq0");
            Hex.True(replacement.Count == (replaceOwner ? 1 : 0), "replacement owner receives only its new seq0");
            foreach (byte[] packet in previous.Concat(replacement))
                Hex.Eq(packet, TunnelFrames.ParseBypassToClient(payloads[0])!.ClientPacket, "no old seq1 reaches either owner");
        }
    }

    [Test] public static void T198_operator_and_player_crafting_loads_match_retail_GM()
    {
        using var frames = T198Frames(); if (frames == null) return;
        using var store = T181Store();
        T181WithOperators("acct2", () =>
        {
            foreach (var (record, owner, retail) in new[] { (137003, 1003, 49997), (137005, 1003, 49999), (137343, 1, 49997), (137345, 1, 49999) })
            {
                byte[] request = T198Frame(frames, "cap_instance1_loads", record);
                BitConverter.GetBytes(owner).CopyTo(request, 10); // only database identity differs in this store
                var (opcode, reply) = RunHandler1(BitConverter.ToUInt16(request, 4), request[6..], store);
                Hex.Eq(T180Frame(opcode, reply), T198Frame(frames, "cap_instance1_loads", record + 1), "operator/player live load " + record);
                byte[] control = T198Frame(frames, "cap_final2b_GM_loads", retail);
                request.AsSpan(6, 4).CopyTo(control.AsSpan(14, 4)); // live DlmId, all other bytes exact
                Hex.Eq(T180Frame(opcode, reply), control, "native status33 GM load " + retail);
            }
        });
        string gm = "cap_final2_clients/capture_2026-09-22T08-06-24-583Z";
        foreach (var (player, ours, retail) in new[] { (66, 52, 64), (67, 67, 65), (181, 181, 158) })
        {
            byte[] control = T198Frame(frames, gm, retail);
            Hex.Eq(T198Frame(frames, "cap_instance1_client1", player), control, "working player's artisan/recipe/fatigue equals real GM");
            Hex.Eq(T198Frame(frames, "cap_instance1_client2", ours), control, "failing operator's artisan/recipe/fatigue equals real GM");
        }
        Hex.Eq(T198Frame(frames, "cap_instance1_client2", 7), T198Frame(frames, gm, 7), "same operator status33 login flags");
    }
}
