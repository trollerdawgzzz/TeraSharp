// SPDX-License-Identifier: MIT
// Copyright (c) 2026 the TeraSharp contributors

using System.Text;
using System.Xml.Linq;
using Microsoft.Data.Sqlite;
using TeraSharp.Arbiter.Game;
using TeraSharp.Arbiter.Handlers;
using TeraSharp.Arbiter.Persistence;
using TeraSharp.Arbiter.Protocol;
using TeraSharp.Arbiter.World;

namespace TeraSharp.Arbiter.Tests;

public static partial class Tests
{
    [Test] public static void T201_phase_and_native_BG_result_survive_restart_and_serve_native_loads()
    {
        string path = Path.Combine(Path.GetTempPath(), "t201-dungeon-" + Guid.NewGuid() + ".db");
        try
        {
            using (var store = new CharacterStore(path, QuietLog()))
            {
                var acct = store.GetOrCreateAccount("t201");
                int owner = store.CreateCharacter(new CharacterRecord { AccountId = acct.Id, Name = "T201" });
                Hex.True(owner == 1, "controlled owner");
                store.InitializeDungeonPhaseReset(1, 1700000000);
                Hex.True(store.SetDungeonPhase(1, 9781, 3, 1800000000), "phase writes to an existing owner");
                Hex.True(!store.SetDungeonPhase(999, 9781, 3, 1800000000), "unknown character cannot create phase state");
                store.SetNativeBattlefieldResult(1, 3, 2, 4, 6, 8, 10, 12, 14, 16, 1234);
                store.SetDungeonEnabled(9781, false);
            }
            using (var store = new CharacterStore(path, QuietLog()))
            {
                byte[] request = Convert.FromHexString("7B00000001000000");
                var (phaseOp, phase) = RunHandler1(0x2869, request, store);
                Hex.True(phaseOp == 0x286A, "known reset epoch loads without another destructive15E0");
                // Decompile pin: Arb063:13833 header; Arb065:9819 24B element.
                Hex.Eq(phase, Convert.FromHexString("1B00000018000000017B00000000F15365000000003526000001000000030000000000000000F1536500000000"),
                    "phase id, character, level, padding and both persistent reset epochs");
                var (bgOp, bg) = RunHandler1(0x2895, request, store);
                Hex.True(bgOp == 0x2896, "native BG list opcode");
                Hex.Eq(bg[..13], Convert.FromHexString("1300000040000000017B000000"), "raw list length is64 bytes, not element count");
                int[] expected = { 1, 3, 2, 4, 6, 8, 10, 12, 14, 16, 0, 0, 1234, 0, 0, 0 };
                for (int i = 0; i < expected.Length; i++) Hex.True(BitConverter.ToInt32(bg, 13 + i * 4) == expected[i], $"native BattleFieldData field{i}");
                Hex.Eq(QaDungeonCommands.BuildDisabledDungeons(store.GetDisabledDungeons()),
                    Convert.FromHexString("0E0000000400000035260000"), "persisted157E disabled list");
                store.SetDungeonEnabled(9781, true);
                Hex.True(store.GetDisabledDungeons().Count == 0, "on removes persisted disabled row");
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            foreach (string suffix in new[] { "", "-wal", "-shm" }) File.Delete(path + suffix);
        }
    }

    [Test] public static void T201_dungeon_QA_commands_authorization_routing_and_native_state()
    {
        string dir = Path.Combine(Path.GetTempPath(), "t201-commands-" + Guid.NewGuid()); Directory.CreateDirectory(dir);
        string map = Path.Combine(dir, "map.json"); string? oldSheet = Environment.GetEnvironmentVariable("TERASHARP_DATASHEET");
        using var env = new T185Environment(null); using var store = new CharacterStore(":memory:", QuietLog());
        var prop = typeof(TeraSharp.Arbiter.Program).GetProperty("Store")!; object? oldStore = prop.GetValue(null);
        try
        {
            Environment.SetEnvironmentVariable("TERASHARP_DATASHEET", dir); prop.SetValue(null, store);
            File.WriteAllText(Path.Combine(dir, "ContinentData.xml"), "<ContinentData><Continent id='9781'/></ContinentData>");
            File.WriteAllText(map, "{\"maps\":{\"376012\":{\"S_SYSTEM_MESSAGE\":62222,\"S_SYSTEM_MESSAGE_CUSTOM\":39244}}}");
            var acct = store.GetOrCreateAccount("t201-op"); int user = store.CreateCharacter(new CharacterRecord { AccountId = acct.Id, Name = "T201" });
            var defs = new DefinitionRegistry(QuietLog()); defs.RegisterFromDef("C_ADMIN", "ref command\nstring command\n");
            defs.RegisterFromDef("S_SYSTEM_MESSAGE_CUSTOM", "ref formatted\nstring formatted\n");
            var bridge = new WorldBridge(WorldReplayTable.Load("/nonexistent", QuietLog()), QuietLog());
            using var main = new T192WorldPeer(bridge, 41, 0); using var dungeon = new T192WorldPeer(bridge, 42, 13);
            using var client = new T185Client(defs, OpcodeTable.LoadFromFile(map, "376012"), QuietLog());
            T185Environment.SetWorld(bridge); DungeonRouting.Channels.MapContinent(9781, 13);
            client.Session.PlayerId = (uint)user; client.Session.GameId = 2011001; client.Session.Account.Name = "t201-op";
            client.Session.SelectedCharacter = new FakeCharacter { Id = (uint)user, Name = "T201", Level = 70 };
            client.Session.EnterWorld(); client.Session.CurrentWorldId = 13;
            BattleFieldSheet.SetForTest(new[] { new BattleFieldEntry(37, "Round_PvP", 3, 65, 70) });
            var gm = new GmCommandHandlers(QuietLog());
            void Run(string text) => gm.OnAdminCommand(client.Session, new byte[] { 6, 0 }.Concat(Encoding.Unicode.GetBytes(text + "\0")).ToArray());
            void Current(ushort op, byte[] payload)
            {
                Hex.Eq(dungeon.Frame(), T180Frame(op, payload), "per-user command goes to World13"); Hex.True(main.Available == 0, "not sent to mainWorld");
            }
            void Broadcast(ushort op, byte[] payload)
            {
                Hex.Eq(main.Frame(), T180Frame(op, payload), "broadcast mainWorld"); Hex.Eq(dungeon.Frame(), T180Frame(op, payload), "broadcast dungeonWorld");
            }
            string[] commands = { "reset_dungeon", "set_dungeoncool 9781 9", "phaselevel 9781 3", "clear_bf_cool",
                "set_bf_result 37 1 2 3 400 5 6 7 8 9", "update_bf_score 37 500", "get_bf_result_resettime",
                "set_bf_result_resettime 2026 9 25 7", "dungeon_log on", "dungeon_onoff 9781 off", "info_dungeon" };
            T181WithOperators(null, () =>
            {
                foreach (string text in commands) Run(text);
                Hex.True(main.Available == 0 && dungeon.Available == 0 && client.Available == 0
                    && store.GetDungeonPhases(user) == null && store.GetDisabledDungeons().Count == 0, "all commands denied before writes/sends for nonoperator");
            });
            T181WithOperators("t201-op", () =>
            {
                Run("reset_dungeon"); Broadcast(0x13B9, PartyPackets.BuildAsResetAllDungeon(2800, user, 0, false, 0));
                Run("set_dungeoncool 9781 9"); Current(0x13B4, QaDungeonCommands.IntBytes(2800, user, 9781, 9));
                Run("set_dungeoncool 999999 9"); Hex.True(dungeon.Available == 0 && main.Available == 0, "unknown continent silently refused");
                Run("phaselevel 9781 3"); Current(0x15E1, QaDungeonCommands.IntBytes(user, 9781, 3));
                Hex.True(BitConverter.ToInt32(store.GetDungeonPhases(user)!.Rows.Single(), 8) == 3, "phase persisted before push");
                Run("clear_bf_cool"); Broadcast(0x1525, QaDungeonCommands.IntBytes(user)); Current(0x1561, QaDungeonCommands.IntBytes(user));
                Run("set_bf_result 37 1 2 3 400 5 6 7 8 9");
                Current(0x13C9, QaDungeonCommands.IntBytes(user, 37, 3, 1, 3, 2, 400, 7, 8, 9, 5, 6));
                Run("set_bf_result 37 1 2 3 400 5"); Hex.True(dungeon.Available == 0, "native six-argument OOB case safely rejected");
                Run("update_bf_score 37 500"); Current(0x13C9, QaDungeonCommands.IntBytes(user, 37, 3, 5, 5, 5, 500, 5, 5, 5, 5, 5));
                store.SetCounterValue("native_bg_season_last_time_2", new DateTimeOffset(2026, 9, 25, 7, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds());
                Run("get_bf_result_resettime");
                string message = Encoding.Unicode.GetString(client.Frame()[6..]).TrimEnd('\0');
                Hex.True(message == "[UTC] YYYY_M_D H:M:S : [2026]_[9]_[25] [7]:[0]:[0]", "native UTC reset-time formatting");
                Run("dungeon_log on"); Broadcast(0x14FE, new byte[] { 1 }); client.Frame();
                Run("dungeon_log off"); Broadcast(0x14FE, new byte[] { 0 }); client.Frame();
                Run("dungeon_onoff 9781 off"); Broadcast(0x1580, Convert.FromHexString("3526000000"));
                Hex.True(!MatchWiring.Offered(new[] { 9781 }).Contains(9781), "disabled dungeon cannot accept another application");
                Run("dungeon_onoff 9781 on"); Broadcast(0x1580, Convert.FromHexString("3526000001"));
                Hex.True(MatchWiring.Offered(new[] { 9781 }).Contains(9781), "on reopens matching");
                Run("info_dungeon"); Hex.True(client.Available == 0, "no dungeon info rows produces no messages");
                File.WriteAllText(Path.Combine(dir, "DungeonConstraint.xml"), "<DungeonConstraint><ConstraintList><Constraint continentId='9781'/></ConstraintList></DungeonConstraint>");
                File.WriteAllText(Path.Combine(dir, "DungeonData_9781.xml"), "<Dungeon continentId='9781' name='@dungeon:9781'/>");
                var cooldown = new byte[52]; BitConverter.GetBytes(9781).CopyTo(cooldown, 0);
                DbProxyHandlers.DungeonCoolTimeNever.CopyTo(cooldown, 8); DbProxyHandlers.DungeonCoolTimeNever.CopyTo(cooldown, 24);
                store.UpsertDungeonCoolTime(user, 9781, cooldown);
                Run("info_dungeon");
                Hex.True(Encoding.Unicode.GetString(client.Frame()[6..]).TrimEnd('\0') == "@3567\vdungeonName\v@dungeon:9781",
                    "native diagnostic3567 uses persisted record and sheet display name");
            });
        }
        finally
        {
            T185Environment.SetWorld(null); prop.SetValue(null, oldStore); BattleFieldSheet.ResetForTest();
            QaDungeonCommands.ResetForTests();
            Environment.SetEnvironmentVariable("TERASHARP_DATASHEET", oldSheet); DungeonRouting.Channels.Clear();
            foreach (string file in Directory.GetFiles(dir)) File.Delete(file); Directory.Delete(dir);
        }
    }

    [Test] public static void T201_info_dungeon_native_seconds_counts_and_matching_duration()
    {
        var record = new byte[52]; BitConverter.GetBytes(9781).CopyTo(record, 0);
        ushort[] date = { 2026, 9, 25, 7, 0, 0 };
        for (int i = 0; i < date.Length; i++) BitConverter.GetBytes(date[i]).CopyTo(record, 8 + i * 2);
        var now = new DateTimeOffset(2026, 9, 25, 7, 1, 0, TimeSpan.Zero);
        var constraint = XElement.Parse("<Constraint coolTime='5' coolTimeForPartyMatching='2'/>");
        var normal = QaDungeonInfo.Summarize(record, constraint, "@dungeon:9781", now);
        Hex.True(normal.Seconds == 240 && normal.Remaining == -1 && QaDungeonInfo.Message(normal) == "@3566\vdungeonName\v@dungeon:9781\vsecond\v240",
            "Arb001:5090 adds minutes; Arb029:15810 formats positive remaining seconds");
        record[48] = 1;
        Hex.True(QaDungeonInfo.Summarize(record, constraint, "x", now).Seconds == 60, "matching type1 uses matching cooldown");
        constraint.SetAttributeValue("enterLimitCount", "3"); BitConverter.GetBytes(1).CopyTo(record, 40);
        var counted = QaDungeonInfo.Summarize(record, constraint, "x", now);
        Hex.True(QaDungeonInfo.Message(counted) == "@3568\vdungeonName\vx\vcount\v2", "remaining entries override cooldown display");
        constraint.SetAttributeValue("phaseSave", "true");
        Hex.True(QaDungeonInfo.Summarize(record, constraint, "x", now).Remaining == -1, "phaseSave disables entry count as nativeParseConstraint");
    }
}
