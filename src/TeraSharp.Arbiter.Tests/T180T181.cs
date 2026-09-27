// SPDX-License-Identifier: MIT
// Copyright (c) 2026 the TeraSharp contributors

using Microsoft.Data.Sqlite;
using System.Reflection;
using System.Net.Sockets;
using TeraSharp.Arbiter.Handlers;
using TeraSharp.Arbiter.Network;
using TeraSharp.Arbiter.Persistence;
using TeraSharp.Arbiter.Protocol;
using TeraSharp.Arbiter.World;

namespace TeraSharp.Arbiter.Tests;

public static partial class Tests
{
    private static byte[] T180Frame(ushort op, byte[] body)
    {
        var frame = new byte[6 + body.Length];
        BitConverter.GetBytes(frame.Length).CopyTo(frame, 0);
        BitConverter.GetBytes(op).CopyTo(frame, 4);
        body.CopyTo(frame, 6);
        return frame;
    }

    private static void T180LeavePair(string request, string reply, string reference)
    {
        var frame = Convert.FromHexString(request);
        ulong handle = BitConverter.ToUInt64(frame, 6);
        int continent = BitConverter.ToInt32(frame, 14);
        var channels = new DungeonChannels();
        channels.MapContinent(continent, 13);
        var control = new WorldUserControls();
        var sent = new List<WorldSend>();
        int cancelled = 0;
        var now = DateTimeOffset.FromUnixTimeSeconds(1000);
        Hex.True(control.TryHandle(0x13C2, frame[6..],
            h => h == handle ? new(2800, 1, 0, () => cancelled++) : null,
            channels, w => w == 13, sent.Add, now), reference + " handled");
        Hex.True(sent.Count == 1 && sent[0].WorldId == 13, "owner 13 even though user is on World 0");
        Hex.Eq(T180Frame(sent[0].Opcode, sent[0].Payload), Convert.FromHexString(reply), reference);
        Hex.True(cancelled == 1 && control.DepartureFor(handle) == new WorldUserControls.Departure(1, continent, now),
            "cancel countdown and record departure");
    }

    [Test] public static void T180_multiworld_12832_ordinal2_to_12834() => T180LeavePair(
        "12000000C2132000ACCC7A02000063260000", "12000000C313F00A00000100000063260000", "cap_multiworld 12832#2 -> 12834");

    [Test] public static void T180_multiworld3_8164_to_8165() => T180LeavePair(
        "12000000C213200092CA6A02000035260000", "12000000C313F00A00000100000035260000", "cap_multiworld3 8164 -> 8165");

    [Test] public static void T180_multiworld3_8320_to_8321() => T180LeavePair(
        "12000000C213200092CA6A02000035260000", "12000000C313F00A00000100000035260000", "cap_multiworld3 8320 -> 8321");

    [Test] public static void T180_leave_does_not_echo_a_pointer_or_fall_back_to_the_arriving_world()
    {
        var control = new WorldUserControls();
        var channels = new DungeonChannels();
        var sent = new List<WorldSend>();
        var request = Convert.FromHexString("200092CA6A02000035260000");
        var now = DateTimeOffset.UtcNow;
        control.TryHandle(0x13C2, request, _ => null, channels, _ => true, sent.Add, now);
        Hex.True(sent.Count == 0 && control.DepartureFor(BitConverter.ToUInt64(request)) == null, "unresolved user dropped");
        control.TryHandle(0x13C2, request, _ => new(99, 42, 0), channels, _ => true, sent.Add, now);
        Hex.True(sent.Count == 0, "unknown continent has no same-link fallback");
        channels.MapContinent(9781, 13);
        control.TryHandle(0x13C2, request, _ => new(99, 42, 0), channels, _ => false, sent.Add, now);
        Hex.True(sent.Count == 0, "offline owner has no same-link fallback");
        control.TryHandle(0x13C2, request, _ => new(99, 42, 0), channels, _ => true, sent.Add, now);
        Hex.Eq(sent.Single().Payload, Convert.FromHexString("630000002A00000035260000"), "live PDId instead of captured user 1");
        control.Forget(BitConverter.ToUInt64(request));
        Hex.True(control.DepartureFor(BitConverter.ToUInt64(request)) == null, "session cleanup");
    }

    [Test] public static void T180_final2b_51509_to_51510_and_client_51511()
    {
        var control = new WorldUserControls();
        var sent = new List<WorldSend>();
        var now = DateTimeOffset.FromUnixTimeSeconds(1000);
        control.TryHandle(0x158A, Convert.FromHexString("2000C2C91802000001"),
            h => h == 0x218C9C20020UL ? new(2800, 1, 13) : null, new(), _ => true, sent.Add, now);
        Hex.True(sent.Count == 1 && sent[0].WorldId == 13 && control.IsHeld(1, now), "resolved target held on its own World");
        Hex.Eq(T180Frame(sent[0].Opcode, sent[0].Payload), Convert.FromHexString("0B0000008C150100000001"), "cap_final2b 51510");
        // World generates this client packet after applying 158C; no duplicate Arbiter send.
        Hex.Eq(ArbiterClientHandlers.BuildAdminHoldCharacter(true), Convert.FromHexString("05000EA301"), "inner packet, cap_final2b 51511");
    }

    [Test] public static void T180_hold_refresh_expiry_release_and_load_state()
    {
        using var store = GuildStore(1);
        var db = new DbProxyHandlers(store, QuietLog());
        var control = db.UserControls;
        var sent = new List<WorldSend>();
        var now = DateTimeOffset.UtcNow;
        var request = Convert.FromHexString("2000C2C91802000001");
        int currentWorld = 13;
        WorldControlUser? Resolve(ulong handle) => new(2800, 1, currentWorld);
        void Set(DateTimeOffset at) => control.TryHandle(0x158A, request, Resolve, new(), _ => true, sent.Add, at);
        Set(now);
        var load = RunHandler(0x2930, Convert.FromHexString("1A0100002000C2C91802000001000000"), 1, store, db);
        Hex.Eq(load.Single().body, Convert.FromHexString("1A0100000101"), "hold-status DLM reflects active state (decompile-derived held variant)");
        Set(now.AddSeconds(30)); // Old expiry must not cancel the refreshed hold.
        control.Tick(now.AddSeconds(60), Resolve, _ => true, sent.Add);
        Hex.True(sent.Count == 2 && control.IsHeld(1, now.AddSeconds(60)), "renewed hold survives old expiry");
        currentWorld = 0;
        control.Tick(now.AddSeconds(90), Resolve, _ => true, sent.Add);
        Hex.True(sent.Count == 3 && sent[2].WorldId == 0 && !control.IsHeld(1, now.AddSeconds(90)), "expiry follows target after handoff");
        Hex.Eq(sent[2].Payload, Convert.FromHexString("0100000000"), "decompile-derived release; no real false pair claimed");
        control.Tick(now.AddSeconds(91), Resolve, _ => true, sent.Add);
        Hex.True(sent.Count == 3, "expiry sends once");
        Set(now.AddSeconds(100));
        request[8] = 0;
        Set(now.AddSeconds(101));
        Hex.True(!control.IsHeld(1, now.AddSeconds(102)) && sent.Last().Payload[4] == 0, "explicit release clears expiry");
        request[8] = 1;
        Set(now);
        control.Forget(BitConverter.ToUInt64(request));
        Hex.True(!control.IsHeld(1, now), "unregister clears User-owned state");
    }

    [Test] public static void T180_short_and_unresolved_control_requests_do_not_mutate_state()
    {
        var control = new WorldUserControls();
        var sent = new List<WorldSend>();
        foreach (ushort op in new ushort[] { 0x13C2, 0x158A })
        {
            control.TryHandle(op, new byte[7], _ => throw new Exception("short frame resolved"), new(), _ => true, sent.Add, DateTimeOffset.UtcNow);
            control.TryHandle(op, new byte[12], _ => null, new(), _ => true, sent.Add, DateTimeOffset.UtcNow);
        }
        Hex.True(sent.Count == 0, "no stale-pointer reply");
    }

    private static CharacterStore T181Store()
    {
        var store = StoreWithTwoAccounts();
        // Preserve the captured UserDbId 1003 without creating 1002 irrelevant fixture users.
        var sql = (SqliteConnection)typeof(CharacterStore).GetField("_db", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(store)!;
        using var cmd = sql.CreateCommand();
        cmd.CommandText = "UPDATE characters SET id=1003 WHERE id=2";
        cmd.ExecuteNonQuery();
        return store;
    }

    [Test] public static void T180_human_patch_routes_live_handles_and_cleans_up_state()
    {
        if (typeof(WorldBridge).GetMethod("ResolveControlUser", BindingFlags.NonPublic | BindingFlags.Instance) == null)
        {
            Skip.Because("T180-PATCH.diff has not been applied to human-owned WorldBridge.cs");
            return;
        }
        using var store = GuildStore(1);
        var sockets = new List<Socket>();
        string map = Path.GetTempFileName();
        DungeonRouting.ResetForTest();
        try
        {
            var bridge = new WorldBridge(WorldReplayTable.Load("/nonexistent", QuietLog()), QuietLog())
                { DbProxy = new DbProxyHandlers(store, QuietLog()) };
            (WorldLink link, Socket peer) Link(int world)
            {
                using var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                listener.Bind(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 0));
                listener.Listen(1);
                var local = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                sockets.Add(local);
                local.Connect(listener.LocalEndPoint!);
                var peer = listener.Accept();
                peer.ReceiveTimeout = 2000;
                sockets.Add(peer);
                var link = new WorldLink(world + 1, local, bridge, QuietLog()) { WorldId = world, PlanetId = 2800 };
                ((List<WorldLink>)typeof(WorldBridge).GetField("_links", BindingFlags.NonPublic | BindingFlags.Instance)!
                    .GetValue(bridge)!).Add(link);
                return (link, peer);
            }
            byte[] Read(Socket socket, int size)
            {
                var frame = new byte[size];
                int count = 0;
                while (count < size)
                {
                    int read = socket.Receive(frame, count, size - count, SocketFlags.None);
                    Hex.True(read > 0, "test socket closed");
                    count += read;
                }
                return frame;
            }
            var main = Link(0);
            var owner = Link(13);
            File.WriteAllText(map, "{\"maps\":{\"376012\":{}}}");
            using var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            var session = new GameSession(client,
                new PacketDispatcher(Microsoft.Extensions.Logging.Abstractions.NullLogger<PacketDispatcher>.Instance),
                OpcodeTable.LoadFromFile(map, "376012"), new DefinitionRegistry(QuietLog()), 376012, 100, QuietLog())
                { GameId = 0x26ACA920020UL, PlayerId = 1, CurrentWorldId = 0, TunnelKey = 1 };
            bridge.RegisterPlayer(session);
            DungeonRouting.Channels.MapContinent(9781, 13);
            bridge.HandleFrame(main.link, 0x13C2, Convert.FromHexString("200092CA6A02000035260000"));
            Hex.Eq(Read(owner.peer, 18), Convert.FromHexString("12000000C313F00A00000100000035260000"), "live bridge owner route");
            Hex.True(main.peer.Available == 0, "no unconditional arriving-link ACK");
            session.CurrentWorldId = 13;
            var hold = new byte[9];
            BitConverter.GetBytes(session.GameId).CopyTo(hold, 0);
            hold[8] = 1;
            bridge.HandleFrame(main.link, 0x158A, hold);
            Hex.Eq(Read(owner.peer, 11), Convert.FromHexString("0B0000008C150100000001"), "panel reply sent to target World");
            Hex.True(main.peer.Available == 0 && bridge.DbProxy.UserControls.IsHeld(1, DateTimeOffset.UtcNow), "hold kept on resolved user");
            using var countdown = new CancellationTokenSource();
            session.PendingLobbyReturn = countdown;
            bridge.HandleFrame(main.link, 0x13C2, Convert.FromHexString("200092CA6A02000035260000"));
            Hex.Eq(Read(owner.peer, 10), Convert.FromHexString("0A000000001501000000"), "cancel return sent to current World");
            Read(owner.peer, 18);
            Hex.True(countdown.IsCancellationRequested && session.PendingLobbyReturn == null, "pending return cancelled");
            bridge.UnregisterPlayer(session.GameId, 13, 1);
            Hex.True(!bridge.DbProxy.UserControls.IsHeld(1, DateTimeOffset.UtcNow)
                && bridge.DbProxy.UserControls.DepartureFor(session.GameId) == null, "bridge unregister clears User state");
        }
        finally
        {
            foreach (var socket in sockets) socket.Dispose();
            File.Delete(map);
            DungeonRouting.ResetForTest();
        }
    }

    private static void T181WithOperators(string? value, Action action)
    {
        string? saved = Environment.GetEnvironmentVariable(GmAccounts.EnvVariable);
        try { Environment.SetEnvironmentVariable(GmAccounts.EnvVariable, value); action(); }
        finally { Environment.SetEnvironmentVariable(GmAccounts.EnvVariable, saved); }
    }

    [Test] public static void T181_final2b_170_to_171_operator_benefits_byte_exact()
    {
        using var store = T181Store();
        T181WithOperators("acct2", () =>
        {
            var (op, body) = RunHandler1(0x28BB, Convert.FromHexString("1B000000EB030000"), store);
            Hex.Eq(T180Frame(op, body), Convert.FromHexString("43000000BC2813000000300000001B0000000115020000550000006F28CF6A0000000016020000550000006FB3317D00000000E8030000550000006F0E806A00000000"), "cap_final2b 171 (67 B)");
            Hex.True(store.GetAccountBenefits(store.GetAccount("acct2")!.Id).Count == 3, "three persisted rows");
            RunHandler1(0x28BB, Convert.FromHexString("1B000000EB030000"), store);
            Hex.True(store.GetAccountBenefits(store.GetAccount("acct2")!.Id).Count == 3, "idempotent relog");
        });
    }

    [Test] public static void T181_final2b_496_to_497_normal_account_empty()
    {
        using var store = T181Store();
        T181WithOperators("acct2", () =>
        {
            var (op, body) = RunHandler1(0x28BB, Convert.FromHexString("6200000001000000"), store);
            Hex.Eq(T180Frame(op, body), Convert.FromHexString("13000000BC2813000000000000006200000001"), "cap_final2b 497 (19 B)");
            Hex.True(store.GetAccountBenefits(store.GetAccount("acct1")!.Id).Count == 0, "player table remains empty");
        });
    }

    [Test] public static void T181_allow_list_removal_and_unlisted_admin_do_not_grant_players_benefits()
    {
        using var store = T181Store();
        long account = store.GetAccount("acct2")!.Id;
        void Load() => RunHandler1(0x28BB, Convert.FromHexString("1B000000EB030000"), store);
        T181WithOperators("acct2", Load);
        store.GrantAccountBenefit(account, 777, 12345, 99); // A separate grant is not ours to revoke.
        store.SetAdminLevel(account, 5);
        T181WithOperators("t30_2", Load); // Character-name aliases must not seed this experiment.
        var rows = store.GetAccountBenefits(account);
        Hex.True(rows.Count == 1 && rows[0].PackageId == 777 && rows[0].Value == 99, "only experiment grants revoked");
        T181WithOperators(null, Load);
        Hex.True(store.GetAccountBenefits(account).Count == 1, "stored admin level alone cannot seed");
    }

    [Test] public static void T181_missing_user_and_short_request_do_not_seed_or_replay()
    {
        using var store = T181Store();
        T181WithOperators("acct2", () =>
        {
            var (op, body) = RunHandler1(0x28BB, Convert.FromHexString("EFBE0000FFFFFF7F"), store);
            Hex.True(op == 0x28BC, "real loader opcode");
            Hex.Eq(body, Convert.FromHexString("1300000000000000EFBE000000"), "decompile-derived unknown User: live DLM, ok0");
            Hex.True(RunHandler(0x28BB, new byte[7], 0, store).Count == 0, "malformed request dropped");
            Hex.True(store.GetAccountBenefits(store.GetAccount("acct2")!.Id).Count == 0, "no grant from unknown user");
        });
    }

    [Test] public static void T181_allow_teleport_is_an_authorized_World_pass_through()
    {
        var line = GmCommandParser.Parse("/@allow_teleport on");
        Hex.True(GmCommandHandlers.Classify(true, 5, line) == GmDispatch.ForwardToWorld, "operator forwards");
        Hex.True(GmCommandHandlers.Classify(true, 0, line) == GmDispatch.NotAuthorised, "player denied");
        Hex.True(GmCommandHandlers.Classify(false, 5, line) == GmDispatch.NoUser, "requires in-world user");
        var frame = GmCommandHandlers.BuildWorldForward(1003, GmCommandHandlers.BypassModeWorld, line!.Rebuilt());
        Hex.True(BitConverter.ToInt32(frame, 4) == 1003 && BitConverter.ToInt32(frame, 8) == 1, "live UserDbId and World bypass mode");
        Hex.True(System.Text.Encoding.Unicode.GetString(frame, 12, frame.Length - 12).TrimEnd('\0') == "allow_teleport on", "on/off arguments preserved");
    }
}
