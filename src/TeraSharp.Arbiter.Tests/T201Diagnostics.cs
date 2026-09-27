// SPDX-License-Identifier: MIT
// Copyright (c) 2026 the TeraSharp contributors

using TeraSharp.Arbiter.Game;
using TeraSharp.Arbiter.Handlers;
using TeraSharp.Arbiter.Protocol;
using TeraSharp.Arbiter.World;

namespace TeraSharp.Arbiter.Tests;

public static partial class Tests
{
    [Test] public static void T201_runtime_bypass_gate_and_leave_countdown_consume_QA_settings()
    {
        using var environment = new T185Environment(null);
        string map = Path.GetTempFileName(); QaDiagnosticCommands.ResetRuntimeForTests();
        try
        {
            File.WriteAllText(map, "{\"maps\":{\"376012\":{\"C_RETURN_TO_LOBBY\":100,\"S_PREPARE_RETURN_TO_LOBBY\":101}}}");
            var defs = new DefinitionRegistry(QuietLog()); defs.RegisterFromDef("S_PREPARE_RETURN_TO_LOBBY", "int32 time\n");
            var opcodes = OpcodeTable.LoadFromFile(map, "376012");
            using var client = new T185Client(defs, opcodes, QuietLog()); var session = client.Session;
            var bridge = new WorldBridge(WorldReplayTable.Load("/nonexistent", QuietLog()), QuietLog());
            using var main = new T192WorldPeer(bridge, 1, 0); T185Environment.SetWorld(bridge);
            byte[]? delivered = null; bridge.RegisterTunnelRoute(99, bytes => delivered = bytes);
            void Run(string command) => QaDiagnosticCommands.TryExecute(session, null, GmCommandParser.Parse(command)!, QuietLog());
            byte[] packet = Convert.FromHexString("04003412");
            byte[] tunnel = TunnelFrames.BuildBypassToClient(packet, new TunnelRecipient(0, 0, 99, 0));
            Run("set_bypass off"); bridge.HandleFrame(main.Link, WorldBridge.OpTunnelToClient, tunnel);
            Hex.True(delivered == null, "native disabled bypass drops before entering reorder buffers");
            Run("set_bypass ON"); Hex.True(!QaDiagnosticCommands.BypassEnabled, "native requires literal lowercase on/off");
            Run("set_bypass on"); bridge.HandleFrame(main.Link, WorldBridge.OpTunnelToClient, tunnel);
            Hex.Eq(delivered!, packet, "reenabled native bypass delivers unchanged packet");
            Run("set_arbiter_worldparam anotherKey 100"); Hex.True(QaDiagnosticCommands.LeaveCountdownSeconds == 5, "only native key changes leave wait");
            Run("set_arbiter_worldparam CLIENTLEAVEWORLDWAITSEC 2");
            var dispatcher = new TeraSharp.Arbiter.Network.PacketDispatcher(Microsoft.Extensions.Logging.Abstractions.NullLogger<TeraSharp.Arbiter.Network.PacketDispatcher>.Instance);
            HandlerRegistry.RegisterAll(dispatcher, opcodes, defs, new CapturingLoggerFactory());
            session.PlayerId = 1; session.SelectedCharacter = new FakeCharacter { Id = 1 }; session.EnterWorld();
            dispatcher.Dispatch(session, Convert.FromHexString("04006400"));
            Hex.Eq(client.Frame(), Convert.FromHexString("0800650002000000"), "real logout countdown builder consumes override, not a detached QA value");
            session.PendingLobbyReturn?.Cancel(); session.PendingLobbyReturn = null;
            Run("set_arbiter_worldparam clientLeaveWorldWaitSec -1");
            Hex.True(QaDiagnosticCommands.LeaveCountdownSeconds == -1 && QaDiagnosticCommands.WaitForLeaveCountdown(-1, CancellationToken.None).IsCompletedSuccessfully,
                "native signed negative expires immediately instead of Task.Delay infinite wait");
        }
        finally { QaDiagnosticCommands.ResetRuntimeForTests(); T185Environment.SetWorld(null); File.Delete(map); }
    }

    [Test] public static void T201_native_diagnostics_broadcast_and_QA_logout_has_no_countdown_request()
    {
        using var env = new T185Environment(null);
        string map = Path.GetTempFileName();
        try
        {
            File.WriteAllText(map, "{\"maps\":{\"376012\":{\"S_SYSTEM_MESSAGE_CUSTOM\":39244}}}");
            var defs = new DefinitionRegistry(QuietLog());
            defs.RegisterFromDef("S_SYSTEM_MESSAGE_CUSTOM", "ref formatted\nstring formatted\n");
            var bridge = new WorldBridge(WorldReplayTable.Load("/nonexistent", QuietLog()), QuietLog());
            using var main = new T192WorldPeer(bridge, 1, 0); using var owner = new T192WorldPeer(bridge, 13, 13);
            using var client = new T185Client(defs, OpcodeTable.LoadFromFile(map, "376012"), QuietLog());
            T185Environment.SetWorld(bridge);
            var s = client.Session; s.PlayerId = 1; s.SelectedCharacter = new FakeCharacter { Id = 1 };
            s.CurrentWorldId = 13; s.EnterWorld();
            void Execute(string text) => QaDiagnosticCommands.TryExecute(s, null, GmCommandParser.Parse(text)!, QuietLog());
            Execute("profile");
            Hex.Eq(main.Frame(), Convert.FromHexString("060000004714"), "Arb043:19902 native main World profile request");
            Hex.Eq(owner.Frame(), Convert.FromHexString("060000004714"), "native dungeon World profile request");
            Execute("reset_profile");
            Hex.Eq(main.Frame(), Convert.FromHexString("060000004814"), "Arb044:1510 native main World profiler reset");
            Hex.Eq(owner.Frame(), Convert.FromHexString("060000004814"), "native dungeon World profiler reset");
            Execute("huntingevent_add"); Execute("consoleprint_commandlist"); Execute("add_event_system 1 2 3 4 5 6");
            Hex.True(client.Available == 0 && main.Available == 0 && owner.Available == 0, "native empty handler and console diagnostic send no client traffic");
            Execute("reload_datasheet");
            Hex.Eq(main.Frame(), Convert.FromHexString("0A000000B31301000000"), "native datasheet reload notifies every World with caller ID");
            Hex.Eq(owner.Frame(), Convert.FromHexString("0A000000B31301000000"), "dungeon World receives same reload request");
            using (var store = StoreWithTwoAccounts())
            {
                QaDiagnosticCommands.TryExecute(s, store, GmCommandParser.Parse("refresh_quest_cache")!, QuietLog());
                byte[] message = client.Frame();
                Hex.True(BitConverter.ToUInt16(message, 2) == 0x994C
                    && System.Text.Encoding.Unicode.GetString(message).Contains("QuestCache refresh completed."),
                    "native SQL quest-cache refresh completion; no fabricated quest mutation");
            }
            Execute("logout");
            Hex.True(BitConverter.ToUInt16(owner.Frame(), 4) == WorldBridge.OpCancelSkillStrictly, "immediate native QA leave starts with cancel-skill, not AS_USER_REQUEST_EXIT");
            byte[] leave = owner.Frame();
            Hex.True(BitConverter.ToUInt16(leave, 4) == WorldBridge.OpLeaveWorld
                && BitConverter.ToInt32(leave, 14) == 3 && BitConverter.ToInt32(leave, 18) == 0,
                "Arb043:17034 native lobby leave type3/reason0, user's current World");
            s.OnWorldLeaveConfirmed(); // Cancel the test's five-second fallback.
            Hex.True(main.Available == 0, "no caller-specific leave leaks to World0");
            Execute("dis"); Hex.True(!s.InWorld, "native QA disconnect closes and unregisters the session");
            foreach (string name in QaDiagnosticCommands.Names)
                Hex.True(GmCommandHandlers.Classify(true, 0, GmCommandParser.Parse(name)) == GmDispatch.NotAuthorised, "same operator gate: " + name);
        }
        finally { T185Environment.SetWorld(null); File.Delete(map); }
    }
}
