// SPDX-License-Identifier: MIT
// Copyright (c) 2026 the TeraSharp contributors

using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using TeraSharp.Arbiter.Game;
using TeraSharp.Arbiter.Handlers;
using TeraSharp.Arbiter.Network;
using TeraSharp.Arbiter.Protocol;
using TeraSharp.Arbiter.World;

namespace TeraSharp.Arbiter.Tests;

public static partial class Tests
{
    private sealed class T185Log : ILogger
    {
        public readonly List<string> Warnings = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? ex, Func<TState, Exception?, string> format)
        { if (level == LogLevel.Warning) Warnings.Add(format(state, ex)); }
    }

    private sealed class T185Environment : IDisposable
    {
        private readonly string? _standalone = Environment.GetEnvironmentVariable(WorldAvailability.StandaloneVariable);
        private readonly bool _replay = LoginHandlers.PureReplay;
        private readonly WorldBridge? _world = TeraSharp.Arbiter.Program.World;
        public T185Environment(string? standalone)
        {
            Environment.SetEnvironmentVariable(WorldAvailability.StandaloneVariable, standalone);
            SetWorld(null); LoginHandlers.PureReplay = false;
        }
        public static void SetWorld(WorldBridge? world) => typeof(TeraSharp.Arbiter.Program)
            .GetProperty("World")!.SetValue(null, world);
        public void Dispose()
        {
            SetWorld(_world); LoginHandlers.PureReplay = _replay;
            Environment.SetEnvironmentVariable(WorldAvailability.StandaloneVariable, _standalone);
        }
    }

    private sealed class T185Client : IDisposable
    {
        private readonly Socket _peer;
        private readonly ulong? _previousGameId = DbProxyHandlers.GameIdByPlayer.TryGetValue(2, out var old) ? old : null;
        public readonly GameSession Session;
        public T185Client(DefinitionRegistry definitions, OpcodeTable opcodes, ILogger log)
        {
            using var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            listener.Bind(new IPEndPoint(IPAddress.Loopback, 0)); listener.Listen(1);
            var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            client.Connect(listener.LocalEndPoint!);
            _peer = listener.Accept(); _peer.ReceiveTimeout = 2000;
            Session = new GameSession(client,
                new PacketDispatcher(Microsoft.Extensions.Logging.Abstractions.NullLogger<PacketDispatcher>.Instance),
                opcodes, definitions, 376012, 100, log);
            Session.Account.Characters.Add(new FakeCharacter { Id = 2 });
        }
        public int Available => _peer.Available;
        private void Read(byte[] buffer, int start, int length)
        {
            while (length > 0)
            {
                int n = _peer.Receive(buffer, start, length, SocketFlags.None);
                Hex.True(n > 0, "client connection remains open"); start += n; length -= n;
            }
        }
        public byte[] Frame()
        {
            var header = new byte[4]; Read(header, 0, 4);
            var frame = new byte[BitConverter.ToUInt16(header, 0)];
            Hex.True(frame.Length >= 4, "complete client header"); header.CopyTo(frame, 0);
            Read(frame, 4, frame.Length - 4); return frame;
        }
        public void Dispose()
        {
            if (Session.PlayerId != 0) DbProxyHandlers.GameIdByPlayer.TryRemove((int)Session.PlayerId, out _);
            Session.Dispose(); _peer.Dispose();
            if (_previousGameId is ulong old) DbProxyHandlers.GameIdByPlayer[2] = old;
        }
    }

    [Test] public static void T185_no_world_refuses_selection_and_never_spawns()
    {
        var defs = LoadDefinitionsOrSkip(); if (defs == null) return;
        var opcodes = LoadOpcodesOrSkip(); if (opcodes == null) return;
        using var env = new T185Environment(null);
        var log = new T185Log();
        var handler = new LoginHandlers(log);
        var staleReady = new WorldBridge(WorldReplayTable.Load("/nonexistent", QuietLog()), QuietLog());
        var worlds = (PerWorld<WorldRuntime>)typeof(WorldBridge)
            .GetField("_worlds", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(staleReady)!;
        worlds.For(WorldRegistration.DefaultWorldId).MarkReady();
        // Both absence of a bridge and a listening bridge without World links used to fall back.
        foreach (var world in new WorldBridge?[] { null, new WorldBridge(WorldReplayTable.Load("/nonexistent", QuietLog()), QuietLog()), staleReady })
        {
            T185Environment.SetWorld(world);
            // PureReplay must not be an independent way around the default entry gate.
            LoginHandlers.PureReplay = world != null;
            using var client = new T185Client(defs, opcodes, log);
            handler.OnSelectUser(client.Session, Convert.FromHexString("0200000000"));
            Hex.Eq(client.Frame(), Convert.FromHexString("10000EF3060040003700360039000000"),
                "cap_social3_client2 record 33: @769");
            Hex.Eq(client.Frame(), Convert.FromHexString("0F00FB8A0000000000000000000000"),
                "cap_social3_client2 record 34: accepted=false, admin=0, error=0, first-login flags=false");
            Hex.True(client.Available == 0 && !client.Session.InWorld && client.Session.SelectedCharacter == null
                && client.Session.PlayerId == 0 && client.Session.GameId == 0, "no spawn burst, identity allocation, or world registration");
            client.Session.SelectedCharacter = client.Session.Account.Characters[0];
            int warnings = log.Warnings.Count;
            handler.OnLoadTopoFin(client.Session, ReadOnlyMemory<byte>.Empty);
            Hex.True(client.Available == 0 && log.Warnings.Count == warnings + 1,
                "late LOAD_TOPO_FIN with stale character cannot synthesize S_SPAWN_ME");
        }
        Hex.True(log.Warnings.Count == 6, "each selection refusal and late load packet logs Warning");
        Environment.SetEnvironmentVariable(WorldAvailability.StandaloneVariable, "true");
        Hex.True(!WorldAvailability.StandaloneEnabled, "only literal TERASHARP_STANDALONE=1 opts in");
    }

    [Test] public static void T185_explicit_standalone_retains_select_and_spawn()
    {
        var defs = LoadDefinitionsOrSkip(); if (defs == null) return;
        var opcodes = LoadOpcodesOrSkip(); if (opcodes == null) return;
        // Standalone harnesses opt in locally; the suite must not enable it globally.
        using var env = new T185Environment("1");
        using var client = new T185Client(defs, opcodes, QuietLog());
        var handler = new LoginHandlers(QuietLog());
        handler.OnSelectUser(client.Session, Convert.FromHexString("0200000000"));
        var selected = client.Frame();
        Hex.True(BitConverter.ToUInt16(selected, 2) == 0x8AFB && selected[4] == 1,
            "opt-in preserves accepted selection");
        bool topo = false, phaseOneComplete = false;
        for (int i = 0; i < 200 && !phaseOneComplete; i++)
        {
            ushort opcode = BitConverter.ToUInt16(client.Frame(), 2);
            topo |= opcode == opcodes["S_LOAD_TOPO"];
            phaseOneComplete = opcode == opcodes["S_ENABLE_DISABLE_SELLABLE_ITEM_LIST"];
        }
        Hex.True(topo && phaseOneComplete && client.Session.SelectedCharacter?.Id == 2 && client.Session.GameId != 0
            && !client.Session.InWorld, "old standalone phase one reaches topology load without a World");
        handler.OnLoadTopoFin(client.Session, ReadOnlyMemory<byte>.Empty);
        Hex.True(BitConverter.ToUInt16(client.Frame(), 2) == opcodes["S_SPAWN_ME"], "old standalone phase two spawns");
    }
}
