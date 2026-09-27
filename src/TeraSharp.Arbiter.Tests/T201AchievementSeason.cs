// SPDX-License-Identifier: MIT
// Copyright (c) 2026 the TeraSharp contributors

using System.Text;
using Microsoft.Data.Sqlite;
using TeraSharp.Arbiter.Game;
using TeraSharp.Arbiter.Handlers;
using TeraSharp.Arbiter.Persistence;
using TeraSharp.Arbiter.Protocol;
using TeraSharp.Arbiter.World;

namespace TeraSharp.Arbiter.Tests;

public static partial class Tests
{
    [Test] public static void T201_achievement_season_broadcasts_native_schedule_delayed_refresh_and_optional_persistence()
    {
        string dir = Path.Combine(Path.GetTempPath(), "t201-achseason-" + Guid.NewGuid()); Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "store.db"); string? oldDir = Environment.GetEnvironmentVariable("TERASHARP_DATASHEET");
        using var env = new T185Environment(null); var prop = typeof(TeraSharp.Arbiter.Program).GetProperty("Store")!; object? oldStore = prop.GetValue(null);
        try
        {
            Environment.SetEnvironmentVariable("TERASHARP_DATASHEET", dir);
            File.WriteAllText(Path.Combine(dir, "AchievementGradeInfo.xml"), "<AchievementGradeInfo><Season id='1'/><Season id='2'/></AchievementGradeInfo>"); QaAchievementCommands.Sheet.Load(dir);
            string map = Path.Combine(dir, "map.json"); File.WriteAllText(map, "{\"maps\":{\"376012\":{\"S_SYSTEM_MESSAGE_CUSTOM\":39244}}}");
            var defs = new DefinitionRegistry(QuietLog()); defs.RegisterFromDef("C_ADMIN", "ref command\nstring command\n"); defs.RegisterFromDef("S_SYSTEM_MESSAGE_CUSTOM", "ref formatted\nstring formatted\n");
            var bridge = new WorldBridge(WorldReplayTable.Load("/nonexistent", QuietLog()), QuietLog());
            using var main = new T192WorldPeer(bridge, 71, 0); using var dungeon = new T192WorldPeer(bridge, 72, 13);
            using var client = new T185Client(defs, OpcodeTable.LoadFromFile(map, "376012"), QuietLog());
            T185Environment.SetWorld(bridge); client.Session.PlayerId = 12; client.Session.GameId = 2015001; client.Session.Account.Name = "season-op";
            client.Session.SelectedCharacter = new FakeCharacter { Id = 12, Name = "Season" }; client.Session.EnterWorld();
            var now = DateTimeOffset.FromUnixTimeSeconds(1800000000); QaAchievementCommands.Clock = () => now;
            var gm = new GmCommandHandlers(QuietLog());
            void Run(string text) => gm.OnAdminCommand(client.Session, new byte[] { 6, 0 }.Concat(Encoding.Unicode.GetBytes(text + "\0")).ToArray());
            void Both(ushort op, byte[] payload) { Hex.Eq(main.Frame(), T180Frame(op, payload), "native main broadcast"); Hex.Eq(dungeon.Frame(), T180Frame(op, payload), "native dungeon broadcast"); }
            using (var store = new CharacterStore(path, QuietLog()))
            {
                prop.SetValue(null, store);
                T181WithOperators(null, () => Run("change_achievement_season 1 true"));
                Hex.True(store.GetAchievementSeasons().Count == 0 && main.Available == 0 && client.Available == 0, "nonoperator refused before season change");
                T181WithOperators("season-op", () =>
                {
                    Run("change_achievement_season 3 true"); client.Frame(); Hex.True(main.Available == 0, "sheet maximum enforced");
                    Run("change_achievement_season 1 true"); Both(0x1510, QaDungeonCommands.IntBytes(1));
                    Both(0x1511, Convert.FromHexString("010000000E0000000E000000000000000100000000D2496B00000000")); client.Frame();
                    QaAchievementCommands.Tick(store, bridge, now.AddSeconds(2)); Hex.True(main.Available == 0, "native refresh delay");
                    QaAchievementCommands.Tick(store, bridge, now.AddSeconds(3)); Both(0x150F, QaDungeonCommands.IntBytes(12));
                    Run("change_achievement_season 1"); client.Frame(); Both(0x150F, QaDungeonCommands.IntBytes(12));
                    Run("change_achievement_season 2"); Both(0x1510, QaDungeonCommands.IntBytes(2)); main.Frame(); dungeon.Frame(); client.Frame();
                    Hex.True(store.GetAchievementSeasons().Count == 1 && QaAchievementCommands.Snapshot(store).Count == 2, "omitted true changes runtime only");
                });
                QaAchievementCommands.ResetForTests(store); prop.SetValue(null, oldStore);
            }
            using (var reopened = new CharacterStore(path, QuietLog()))
            {
                Hex.True(reopened.GetAchievementSeasons().Keys.Single() == 1, "only explicitly persistent season survives restart");
                Hex.Eq(QaAchievementCommands.BootFrame(reopened, 0x1510, now)!, QaDungeonCommands.IntBytes(1), "reconnected World receives saved current season");
                QaAchievementCommands.ResetForTests(reopened);
            }
        }
        finally
        {
            prop.SetValue(null, oldStore); T185Environment.SetWorld(null); Environment.SetEnvironmentVariable("TERASHARP_DATASHEET", oldDir);
            QaAchievementCommands.Sheet.Load(HandshakeData.DatasheetDirectory()); SqliteConnection.ClearAllPools(); foreach (string file in Directory.GetFiles(dir)) File.Delete(file); Directory.Delete(dir);
        }
    }
}
