// SPDX-License-Identifier: MIT
// Copyright (c) 2026 the TeraSharp contributors

using System.Reflection;
using TeraSharp.Arbiter.Game;
using TeraSharp.Arbiter.Handlers;
using TeraSharp.Arbiter.Protocol;
using TeraSharp.Arbiter.World;

namespace TeraSharp.Arbiter.Tests;

public static partial class Tests
{
    [Test] public static void T195_admin_frames_follow_current_world_and_target_without_main_fallback()
    {
        string map = Path.GetTempFileName();
        using var env = new T185Environment(null);
        ulong? previousGameId = DbProxyHandlers.GameIdByPlayer.TryGetValue(1, out var oldGameId) ? oldGameId : null;
        var storeProperty = typeof(TeraSharp.Arbiter.Program).GetProperty("Store")!;
        var previousStore = storeProperty.GetValue(null);
        storeProperty.SetValue(null, null);
        try
        {
            File.WriteAllText(map, "{\"maps\":{\"376012\":{}}}");
            var defs = new DefinitionRegistry(QuietLog());
            defs.RegisterFromDef("C_ADMIN", "ref command\nstring command\n");
            var opcodes = OpcodeTable.LoadFromFile(map, "376012");
            var bridge = new WorldBridge(WorldReplayTable.Load("/nonexistent", QuietLog()), QuietLog());
            using var main = new T192WorldPeer(bridge, 1, 0);
            using var dungeon = new T192WorldPeer(bridge, 13, 13);
            T185Environment.SetWorld(bridge);
            using var gm = new T185Client(defs, opcodes, QuietLog());
            using var target = new T185Client(defs, opcodes, QuietLog());
            gm.Session.Account.Name = "t195-operator";
            gm.Session.PlayerId = 1; gm.Session.GameId = 19501;
            gm.Session.SelectedCharacter = new FakeCharacter { Id = 1, Name = "T195gm" };
            gm.Session.EnterWorld();
            target.Session.PlayerId = 2; target.Session.GameId = 19502;
            target.Session.SelectedCharacter = new FakeCharacter { Id = 2, Name = "T195target" };
            target.Session.EnterWorld();
            var handler = new GmCommandHandlers(QuietLog());
            // cap_final2b raw30646, A->W #51 (World0): literal nodie, user1, mode1.
            // cap_2man_b raw16350, A->W #54 (World13): kill 1000000 inside9781.
            var nodie = Convert.FromHexString("1E00000029281200000001000000010000006E006F006400690065000000");
            var kill = Convert.FromHexString("2C00000029281200000001000000010000006B0069006C006C00200031003000300030003000300030000000");
            var command = Convert.FromHexString("06006E006F006400690065000000");
            T181WithOperators("t195-operator", () =>
            {
                handler.OnAdminCommand(gm.Session, command);
                Hex.Eq(main.Frame(), nodie, "cap_final2b30646 byte-exact AS_ADMIN_COMMAND");
                Hex.True(dungeon.Available == 0, "before transfer the GM is on World0");
                // T192's completed type2 entry updates this session field.
                gm.Session.CurrentWorldId = 13;
                handler.OnAdminCommand(gm.Session, command);
                Hex.Eq(dungeon.Frame(), nodie, "same command after transfer reaches World13");
                handler.OnAdminCommand(gm.Session, new byte[] { 6, 0 }.Concat(kill[18..]).ToArray());
                Hex.Eq(dungeon.Frame(), kill, "cap_2man_b16350 byte-exact kill to World13");

                var skill = new byte[12]; BitConverter.GetBytes(1).CopyTo(skill, 8);
                GmAdminTool.OnGmSkill(gm.Session, skill, QuietLog());
                Hex.Eq(dungeon.Frame(), T180Frame(0x2827,
                    ArbiterClientHandlers.BuildGmSkillRequest(1, "T195gm", 1)), "T148 invincibility panel follows GM");
                var teleport = new byte[GmAdminTool.GmTeleportBodySize];
                BitConverter.GetBytes(9781).CopyTo(teleport, 2);
                GmAdminTool.OnGmTeleport(gm.Session, teleport, QuietLog());
                Hex.Eq(dungeon.Frame(), T180Frame(0x2827,
                    ArbiterClientHandlers.BuildGmTeleportRequest(1, "T195gm", teleport)), "T155 teleport panel follows GM");
                Hex.True(main.Available == 0, "no per-GM frame leaks to the main link after transfer");

                // The ask belongs to the TARGET's current World, not the GM's.
                var askBody = new byte[18];
                BitConverter.GetBytes(2).CopyTo(askBody, 2);
                BitConverter.GetBytes(ArbiterClientHandlers.UserActionGoTo).CopyTo(askBody, GmAdminTool.UserActionOffset);
                GmAdminTool.OnRequestUserAction(gm.Session, askBody, QuietLog());
                byte[] ask = T180Frame(0x2825, ArbiterClientHandlers.BuildAskUserAction(
                    1, "T195gm", 2, "T195target", ArbiterClientHandlers.UserActionGoTo));
                Hex.Eq(main.Frame(), ask, "target still on World0 receives2825");
                target.Session.CurrentWorldId = 13;
                GmAdminTool.OnRequestUserAction(gm.Session, askBody, QuietLog());
                Hex.Eq(dungeon.Frame(), ask, "target now on World13 receives2825");
                Hex.True(main.Available == 0, "target's old World receives nothing");
                // Filled2826 comes back to the REQUESTER's World, irrespective of the responding link.
                bridge.HandleFrame(main.Link, 0x2826, ask[6..]);
                Hex.Eq(dungeon.Frame(), T180Frame(0x2827, ask[6..]), "2826 forwards verbatim to current requester World");
            });

            T181WithOperators(null, () =>
            {
                handler.OnAdminCommand(gm.Session, command);
                GmAdminTool.OnGmTeleport(gm.Session, new byte[GmAdminTool.GmTeleportBodySize], QuietLog());
                Hex.True(main.Available == 0 && dungeon.Available == 0 && gm.Available == 0,
                    "unprivileged commands and panel requests remain silent");
            });
            ((List<WorldLink>)typeof(WorldBridge).GetField("_links", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(bridge)!).Remove(dungeon.Link);
            T181WithOperators("t195-operator", () => handler.OnAdminCommand(gm.Session, command));
            Hex.True(!ArbiterClientHandlers.SendToWorld(gm.Session, 0x2827, new byte[216])
                && !ArbiterClientHandlers.SendToWorld(target.Session, 0x2825, new byte[216])
                && main.Available == 0 && dungeon.Available == 0,
                "missing current-World link refuses all forwards even while World0 remains connected");
        }
        finally
        {
            T185Environment.SetWorld(null);
            storeProperty.SetValue(null, previousStore);
            if (previousGameId is ulong old) DbProxyHandlers.GameIdByPlayer[1] = old;
            File.Delete(map);
        }
    }
}
