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
    [Test] public static void T201_rookie_reconnect_immediately_before_expiry_removes_replayed_event_before_first_tick()
    {
        using var e = new T201UtilityEnvironment(); var now = DateTimeOffset.FromUnixTimeSeconds(1800000000);
        e.Store.AddDungeonRookieEvent(9781, 1800000000, 1800000060, 100, 10);
        using var reopened = new CharacterStore(Path.Combine(e.DirectoryPath, "store.db"), QuietLog());
        var snapshot = QaDungeonEvents.BuildRookieSnapshot(reopened, now.AddSeconds(59), rememberReplay: true);
        Hex.True(BitConverter.ToInt32(snapshot, 0) == 1, "native14E9 advertises active reward without an expiry timestamp");
        e.Dungeon.Link.SendFrame(0x14E9, snapshot); e.Dungeon.Frame();
        QaDungeonEvents.Tick(reopened, e.Bridge, now.AddSeconds(60));
        var remove = T180Frame(0x14EB, QaDungeonCommands.IntBytes(9781));
        Hex.Eq(e.Dungeon.Frame(), remove, "first tick expires the replayed World reward even though Started was empty");
        Hex.Eq(e.Main.Frame(), remove, "native end notification broadcasts all Worlds");
        Hex.True(reopened.GetDungeonRookieEvents().Count == 0, "expiry also removes persisted event");
        QaDungeonEvents.Tick(reopened, e.Bridge, now.AddSeconds(61));
        Hex.True(e.Main.Available == 0 && e.Dungeon.Available == 0, "no duplicate start or end after deletion");
        QaDungeonEvents.ResetForTests(reopened);
    }

    [Test] public static void T201_continent_abnormality_reservation_uses_native_identity_calendar_and_onoff_order()
    {
        string dir = Path.Combine(Path.GetTempPath(), "t201-abnormality-" + Guid.NewGuid()); Directory.CreateDirectory(dir);
        string? oldDir = Environment.GetEnvironmentVariable("TERASHARP_DATASHEET"); using var env = new T185Environment(null);
        using var store = new CharacterStore(":memory:", QuietLog());
        var prop = typeof(TeraSharp.Arbiter.Program).GetProperty("Store")!; object? oldStore = prop.GetValue(null);
        try
        {
            Environment.SetEnvironmentVariable("TERASHARP_DATASHEET", dir); prop.SetValue(null, store);
            File.WriteAllText(Path.Combine(dir, "ContinentData.xml"), "<ContinentData><Continent id='9781'/></ContinentData>");
            string map = Path.Combine(dir, "map.json"); File.WriteAllText(map, "{\"maps\":{\"376012\":{\"S_SYSTEM_MESSAGE_CUSTOM\":39244}}}");
            var defs = new DefinitionRegistry(QuietLog()); defs.RegisterFromDef("C_ADMIN", "ref command\nstring command\n");
            defs.RegisterFromDef("S_SYSTEM_MESSAGE_CUSTOM", "ref formatted\nstring formatted\n");
            var bridge = new WorldBridge(WorldReplayTable.Load("/nonexistent", QuietLog()), QuietLog());
            using var main = new T192WorldPeer(bridge, 63, 0); using var dungeon = new T192WorldPeer(bridge, 64, 13);
            using var client = new T185Client(defs, OpcodeTable.LoadFromFile(map, "376012"), QuietLog());
            T185Environment.SetWorld(bridge); client.Session.PlayerId = 1; client.Session.GameId = 2014002; client.Session.Account.Name = "event-op";
            client.Session.SelectedCharacter = new FakeCharacter { Id = 1, Name = "Eventer" }; client.Session.EnterWorld();
            var now = new DateTimeOffset(2026, 9, 26, 7, 0, 0, TimeSpan.Zero); QaDungeonEvents.Clock = () => now;
            var gm = new GmCommandHandlers(QuietLog());
            void Run(string text) => gm.OnAdminCommand(client.Session, new byte[] { 6, 0 }.Concat(Encoding.Unicode.GetBytes(text + "\0")).ToArray());
            void Both(ushort op, byte[] payload) { Hex.Eq(main.Frame(), T180Frame(op, payload), "main ordered event frame"); Hex.Eq(dungeon.Frame(), T180Frame(op, payload), "dungeon ordered event frame"); }
            const string command = "add_dungeon_abnormality 9781 999 202609260700 202609260800";
            T181WithOperators(null, () => Run(command)); Hex.True(store.GetContinentAbnormalityEvents().Count == 0 && client.Available == 0, "reservation denied before persistent identity allocation");
            T181WithOperators("event-op", () => Run(command));
            Both(0x1588, Convert.FromHexString("0E0000000100000000")); Both(0x161A, Convert.FromHexString("3526000001000000E7030000"));
            Hex.True(Encoding.Unicode.GetString(client.Frame()[6..]).TrimEnd('\0') == "던전 이상상태 추가 완료", "native success text");
            var first = store.GetContinentAbnormalityEvents().Single();
            Hex.True(first.Id == 1 && first.Start == now.ToUnixTimeSeconds() && first.End == now.AddHours(1).ToUnixTimeSeconds(), "identity and YYYYMMDDHHMM parsing");
            T181WithOperators("event-op", () => Run(command));
            Both(0x1588, Convert.FromHexString("0E0000000200000000")); Both(0x161A, Convert.FromHexString("3526000001000000E7030000")); client.Frame();
            Hex.True(store.GetContinentAbnormalityEvents().Count == 2, "native SQL allows duplicate abnormality under independent reservation identities");
            QaDungeonEvents.Tick(store, bridge, now.AddMinutes(30)); Hex.True(main.Available == 0, "no repeated activation while live");
            QaDungeonEvents.Tick(store, bridge, now.AddHours(1));
            foreach (int id in new[] { 1, 2 }) { Both(0x1588, QaDungeonEvents.BuildContents(id, true)); Both(0x161B, QaDungeonCommands.IntBytes(9781, 1, 999)); }
            Hex.True(store.GetContinentAbnormalityEvents().Count == 0, "expiry removes reservation and dynamic continent record");
            Hex.True(!QaDungeonEvents.TryParseDate("1800000000", out _), "Unix timestamp must not be interpreted as calendar time");
        }
        finally
        {
            prop.SetValue(null, oldStore); T185Environment.SetWorld(null); QaDungeonEvents.ResetForTests(store);
            Environment.SetEnvironmentVariable("TERASHARP_DATASHEET", oldDir); foreach (string file in Directory.GetFiles(dir)) File.Delete(file); Directory.Delete(dir);
        }
    }

    [Test] public static void T201_rookie_event_is_authorized_persistent_replayed_and_expires_on_all_Worlds()
    {
        string dir = Path.Combine(Path.GetTempPath(), "t201-rookie-" + Guid.NewGuid()); Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "store.db"); string? oldDir = Environment.GetEnvironmentVariable("TERASHARP_DATASHEET");
        using var env = new T185Environment(null);
        var prop = typeof(TeraSharp.Arbiter.Program).GetProperty("Store")!; object? oldStore = prop.GetValue(null);
        try
        {
            Environment.SetEnvironmentVariable("TERASHARP_DATASHEET", dir);
            File.WriteAllText(Path.Combine(dir, "ContinentData.xml"), "<ContinentData><Continent id='9781'/></ContinentData>");
            File.WriteAllText(Path.Combine(dir, "DungeonData_9781.xml"), "<Dungeon continentId='9781'/>");
            File.WriteAllText(Path.Combine(dir, "ItemTemplate.xml"), "<Items><Item id='100' maxStack='100'/><Item id='101' maxStack='1'/></Items>");
            File.WriteAllText(Path.Combine(dir, "EnchantData.xml"), "<EnchantData normalMaxCount='12' masterpieceMaxCount='15'/>"); QaItemSheet.Entry.Load(dir);
            string map = Path.Combine(dir, "map.json"); File.WriteAllText(map, "{\"maps\":{\"376012\":{}}}");
            var defs = new DefinitionRegistry(QuietLog()); defs.RegisterFromDef("C_ADMIN", "ref command\nstring command\n");
            var bridge = new WorldBridge(WorldReplayTable.Load("/nonexistent", QuietLog()), QuietLog());
            using var main = new T192WorldPeer(bridge, 61, 0); using var dungeon = new T192WorldPeer(bridge, 62, 13);
            using var client = new T185Client(defs, OpcodeTable.LoadFromFile(map, "376012"), QuietLog());
            T185Environment.SetWorld(bridge); client.Session.PlayerId = 1; client.Session.GameId = 2014001; client.Session.Account.Name = "event-op";
            client.Session.SelectedCharacter = new FakeCharacter { Id = 1, Name = "Eventer" }; client.Session.EnterWorld();
            var now = DateTimeOffset.FromUnixTimeSeconds(1800000000); var gm = new GmCommandHandlers(QuietLog());
            void Run(string text) => gm.OnAdminCommand(client.Session, new byte[] { 6, 0 }.Concat(Encoding.Unicode.GetBytes(text + "\0")).ToArray());
            using (var store = new CharacterStore(path, QuietLog()))
            {
                prop.SetValue(null, store); QaDungeonEvents.Clock = () => now;
                T181WithOperators(null, () => Run("start_rookie_event 9781 1 100 10"));
                Hex.True(store.GetDungeonRookieEvents().Count == 0 && main.Available == 0, "nonoperator cannot schedule an event");
                T181WithOperators("event-op", () =>
                {
                    Run("start_rookie_event 9999 1 100 10"); Run("start_rookie_event 9781 -1 100 10");
                    Run("start_rookie_event 9781 1 999 10"); Run("start_rookie_event 9781 1 101 6");
                    Hex.True(store.GetDungeonRookieEvents().Count == 0, "invalid dungeon, duration, item and unstackable quantity are rejected");
                    Run("start_rookie_event 9781 1 100 10"); Run("start_rookie_event 9781 1 100 10");
                });
                Hex.True(store.GetDungeonRookieEvents().Count == 1 && client.Available == 0, "overlap rejected; native command has no direct client reply");
                QaDungeonEvents.Tick(store, bridge, now);
                var start = T180Frame(0x14EA, Convert.FromHexString("010000001200000035260000120000000000000001000000640000000A000000"));
                Hex.Eq(main.Frame(), start, "native14EA header and target1 reward"); Hex.Eq(dungeon.Frame(), start, "event reaches dungeon World");
                QaDungeonEvents.Tick(store, bridge, now.AddMinutes(1)); Hex.True(main.Available == 0, "active event is not restarted each tick");
                QaDungeonEvents.ResetForTests(store);
            }
            using (var reopened = new CharacterStore(path, QuietLog()))
            {
                prop.SetValue(null, reopened);
                Hex.Eq(QaDungeonEvents.BuildRookieSnapshot(reopened, now.AddMinutes(2)), Convert.FromHexString(
                    "010000000E0000000E00000000000000010000002200000035260000220000000000000001000000640000000A000000"),
                    "native14E9 nested continent/reward list survives restart and is ready for World reconnect");
                QaDungeonEvents.Tick(reopened, bridge, now.AddMinutes(2)); main.Frame(); dungeon.Frame();
                QaDungeonEvents.Tick(reopened, bridge, now.AddHours(1));
                Hex.Eq(main.Frame(), T180Frame(0x14EB, QaDungeonCommands.IntBytes(9781)), "expiry deletes native event by continent");
                Hex.Eq(dungeon.Frame(), T180Frame(0x14EB, QaDungeonCommands.IntBytes(9781)), "expiry reaches all Worlds");
                Hex.True(reopened.GetDungeonRookieEvents().Count == 0, "expired event and reward are removed persistently");
                QaDungeonEvents.ResetForTests(reopened);
                prop.SetValue(null, oldStore); // Client disposal consults Program.Store after this using scope.
            }
        }
        finally
        {
            prop.SetValue(null, oldStore); T185Environment.SetWorld(null); Environment.SetEnvironmentVariable("TERASHARP_DATASHEET", oldDir);
            QaItemSheet.Entry.Load(HandshakeData.DatasheetDirectory()); SqliteConnection.ClearAllPools();
            foreach (string file in Directory.GetFiles(dir)) File.Delete(file); Directory.Delete(dir);
        }
    }
}
