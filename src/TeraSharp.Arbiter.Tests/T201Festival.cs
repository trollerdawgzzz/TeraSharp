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
    [Test] public static void T201_festival_scope_scheduling_restart_and_daily_override_match_native_managers()
    {
        string dir = Path.Combine(Path.GetTempPath(), "t201-festival-" + Guid.NewGuid()); Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "store.db"); string? oldDir = Environment.GetEnvironmentVariable("TERASHARP_DATASHEET");
        using var env = new T185Environment(null); var prop = typeof(TeraSharp.Arbiter.Program).GetProperty("Store")!; object? oldStore = prop.GetValue(null);
        try
        {
            Environment.SetEnvironmentVariable("TERASHARP_DATASHEET", dir);
            File.WriteAllText(Path.Combine(dir, "WorldFestival.xml"), "<WorldFestival><EventObject id='10'/><EventObject id='20' broadcastToWorldServer='true'><DailyEvent/></EventObject></WorldFestival>");
            File.WriteAllText(Path.Combine(dir, "DailyEvent.xml"), "<DailyEvent enable='false'><Time resetHour='6'/></DailyEvent>"); QaFestivalCommands.Sheet.Load(dir);
            string map = Path.Combine(dir, "map.json"); File.WriteAllText(map, "{\"maps\":{\"376012\":{}}}");
            var defs = new DefinitionRegistry(QuietLog()); defs.RegisterFromDef("C_ADMIN", "ref command\nstring command\n");
            var bridge = new WorldBridge(WorldReplayTable.Load("/nonexistent", QuietLog()), QuietLog());
            using var main = new T192WorldPeer(bridge, 73, 0); using var dungeon = new T192WorldPeer(bridge, 74, 13);
            using var client = new T185Client(defs, OpcodeTable.LoadFromFile(map, "376012"), QuietLog());
            T185Environment.SetWorld(bridge); client.Session.PlayerId = 1; client.Session.GameId = 2015002; client.Session.Account.Name = "festival-op";
            client.Session.SelectedCharacter = new FakeCharacter { Id = 1, Name = "Festival" }; client.Session.EnterWorld();
            var now = new DateTimeOffset(2026, 9, 26, 7, 0, 0, TimeSpan.Zero); QaFestivalCommands.Clock = () => now;
            var gm = new GmCommandHandlers(QuietLog());
            void Run(string text) => gm.OnAdminCommand(client.Session, new byte[] { 6, 0 }.Concat(Encoding.Unicode.GetBytes(text + "\0")).ToArray());
            void Both(ushort op, byte[] p) { Hex.Eq(main.Frame(), T180Frame(op, p), "main event frame"); Hex.Eq(dungeon.Frame(), T180Frame(op, p), "dungeon event frame"); }
            using (var store = new CharacterStore(path, QuietLog()))
            {
                prop.SetValue(null, store);
                T181WithOperators(null, () => { foreach (string name in QaFestivalCommands.Names) Run(name + " 20 202609260701 202609260702"); });
                Hex.True(main.Available == 0 && client.Available == 0 && store.GetFestivalEvents().Count == 0, "all four commands reject nonoperators");
                T181WithOperators("festival-op", () =>
                {
                    Run("start_festival 999"); Hex.True(store.GetFestivalEvents().Count == 0, "undefined event does not start");
                    Run("start_festival 10"); var row = store.GetFestivalEvents().Single();
                    Hex.Eq(main.Frame(), T180Frame(0x149A, QaFestivalCommands.StartPayload(row)), "native default scope is mainWorld only");
                    Hex.True(dungeon.Available == 0, "nonbroadcast event absent from instanceWorld");
                    Run("start_festival 10"); Hex.True(main.Available == 0 && store.GetFestivalEvents().Count == 1, "already running start is idempotent");
                    Run("stop_festival 10"); Hex.Eq(main.Frame(), T180Frame(0x149B, QaDungeonCommands.IntBytes(10)), "native manual stop");
                    Run("dailyevent 4"); QaFestivalCommands.Tick(store, bridge, now); Both(0x156B, QaDungeonCommands.IntBytes(4));
                    Run("dailyevent 8"); QaFestivalCommands.Tick(store, bridge, now.AddSeconds(1)); Hex.True(main.Available == 0, "invalid override leaves day unchanged");
                    Run("reserve_festival 20 202609260701 202609260702"); Run("reserve_festival 20 202609260702 202609260703");
                    Hex.True(store.GetFestivalEvents().Count == 1 && main.Available == 0, "inclusive overlap refused; future event remains silent");
                    QaFestivalCommands.Tick(store, bridge, now.AddMinutes(1));
                    Both(0x149A, QaFestivalCommands.StartPayload(store.GetFestivalEvents().Single()));
                    Hex.True(client.Available == 0, "native festival commands have no invented direct client reply");
                });
                QaFestivalCommands.ResetForTests(store); prop.SetValue(null, oldStore);
            }
            using (var reopened = new CharacterStore(path, QuietLog()))
            {
                var row = reopened.GetFestivalEvents().Single(); Hex.True(row.Event == 20, "reserved festival survives restart");
                QaFestivalCommands.Tick(reopened, bridge, now.AddMinutes(1)); Both(0x149A, QaFestivalCommands.StartPayload(row));
                var day = main.Frame(); Hex.Eq(dungeon.Frame(), day, "restarted daily event current day broadcasts");
                Hex.True(BitConverter.ToUInt16(day, 4) == 0x156B && BitConverter.ToInt32(day, 6) is >= 1 and <= 7, "festival activates sheet/local-calendar daily event");
                QaFestivalCommands.Tick(reopened, bridge, now.AddMinutes(2).AddSeconds(1));
                Both(0x149B, QaDungeonCommands.IntBytes(20)); Both(0x156B, QaDungeonCommands.IntBytes(0));
                Hex.True(reopened.GetFestivalEvents().Count == 0, "expiration removes persisted event and turns its daily event off");
                QaFestivalCommands.ResetForTests(reopened);
            }
        }
        finally
        {
            prop.SetValue(null, oldStore); T185Environment.SetWorld(null); Environment.SetEnvironmentVariable("TERASHARP_DATASHEET", oldDir);
            QaFestivalCommands.Sheet.Load(HandshakeData.DatasheetDirectory()); SqliteConnection.ClearAllPools(); foreach (string file in Directory.GetFiles(dir)) File.Delete(file); Directory.Delete(dir);
        }
    }
}
