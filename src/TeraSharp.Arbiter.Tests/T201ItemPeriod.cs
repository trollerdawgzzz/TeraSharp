// SPDX-License-Identifier: MIT
// Copyright (c) 2026 the TeraSharp contributors

using Microsoft.Data.Sqlite;
using TeraSharp.Arbiter.Handlers;
using TeraSharp.Arbiter.Persistence;
using TeraSharp.Arbiter.World;

namespace TeraSharp.Arbiter.Tests;

public static partial class Tests
{
    [Test] public static void T201_item_period_reconnect_before_first_tick_preserves_creation_expiry_and_removal()
    {
        using var e = new T201UtilityEnvironment();
        File.WriteAllText(Path.Combine(e.DirectoryPath, "ItemTemplate.xml"), "<ItemData><Item id='7' periodInMinute='60' periodByWebAdmin='True'/></ItemData>");
        File.WriteAllText(Path.Combine(e.DirectoryPath, "EnchantData.xml"), "<EnchantData normalMaxCount='0' masterpieceMaxCount='0'/>");
        QaItemSheet.Entry.Load(e.DirectoryPath);
        var begin = DateTimeOffset.FromUnixTimeSeconds(1800000000); var oldClock = QaItemPeriodCommands.Clock;
        QaItemPeriodCommands.Clock = () => begin.AddSeconds(1);
        try
        {
            e.Store.AddQaItemPeriod(7, 1800000000, 1800000060, 1800000120);
            using var reopened = new CharacterStore(Path.Combine(e.DirectoryPath, "store.db"), QuietLog());
            QaItemPeriodCommands.Replay(reopened, e.Dungeon.Link, begin.AddSeconds(1));
            Hex.Eq(e.Dungeon.Frame(), Convert.FromHexString("16000000C314010000000700000078D2496B00000000"), "reconnect serves active SQL event before manager's first tick");
            Hex.True(e.Main.Available == 0, "reconnect does not broadcast to other Worlds");
            var atom = new byte[ItemCreate.AtomSize]; BitConverter.GetBytes(8).CopyTo(atom, 4); BitConverter.GetBytes(7).CopyTo(atom, 0x18);
            DbProxyHandlers.WriteArbTimestamp(atom, 0x1F0, new DateTime(1970, 1, 1));
            byte[] reply = BitConverter.GetBytes(14).Concat(BitConverter.GetBytes(atom.Length)).Concat(atom).ToArray();
            QaItemPeriodCommands.ApplyCreatedAtoms(reopened, reply, 0);
            var timestamp = new byte[16]; DbProxyHandlers.WriteArbTimestamp(timestamp, 0, begin.AddSeconds(120).UtcDateTime);
            Hex.Eq(reply.AsSpan(8 + 0x1F0, 16).ToArray(), timestamp, "immediate creation reads persisted active expiry without waiting for tick");
            QaItemPeriodCommands.TryExecute(e.Caller.Session, reopened, GmCommandParser.Parse("delete_item_period 7")!, QuietLog()); e.Caller.Frame();
            var remove = Convert.FromHexString("0A000000C41401000000");
            Hex.Eq(e.Main.Frame(), remove, "immediate deletion broadcasts native removal"); Hex.Eq(e.Dungeon.Frame(), remove, "restored World overlay is cleared");
            QaItemPeriodCommands.Tick(reopened, e.Bridge, begin.AddSeconds(2));
            Hex.True(reopened.GetQaItemPeriods().Count == 0 && e.Main.Available == 0 && e.Dungeon.Available == 0, "later tick cannot resurrect deleted overlay");
        }
        finally { QaItemPeriodCommands.Clock = oldClock; }
    }

    [Test] public static void T201_item_period_native_lifecycle_reopens_and_controls_created_expiry()
    {
        using var environment = new T185Environment(null);
        string directory = Path.Combine(Path.GetTempPath(), "t201-period-" + Guid.NewGuid()); Directory.CreateDirectory(directory);
        string database = Path.Combine(directory, "period.db");
        var oldClock = QaItemPeriodCommands.Clock;
        try
        {
            File.WriteAllText(Path.Combine(directory, "ItemTemplate.xml"), "<ItemData><Item id='7' periodInMinute='60' periodByWebAdmin='True'/><Item id='8' periodInMinute='30' periodByWebAdmin='False'/></ItemData>");
            File.WriteAllText(Path.Combine(directory, "EnchantData.xml"), "<EnchantData normalMaxCount='0' masterpieceMaxCount='0'/>");
            Hex.True(QaItemSheet.Entry.Load(directory).FromSheet, "period rules come from template attributes");
            var begin = DateTimeOffset.FromUnixTimeSeconds(1800000000); QaItemPeriodCommands.Clock = () => begin;
            using (var first = new CharacterStore(database, QuietLog()))
            {
                Hex.True(first.AddQaItemPeriod(7, 1800000000, 1800000060, 1800000120) == 1, "native identity allocation");
                Hex.True(first.AddQaItemPeriod(7, 1800000060, 1800000090, 1800000150) == 0, "touching ranges rejected, Arb082:13462");
            }
            using (var reopened = new CharacterStore(database, QuietLog()))
            {
                var bridge = new WorldBridge(WorldReplayTable.Load("/nonexistent", QuietLog()), QuietLog());
                using var main = new T192WorldPeer(bridge, 1, 0); using var dungeon = new T192WorldPeer(bridge, 13, 13);
                T185Environment.SetWorld(bridge);
                QaItemPeriodCommands.Tick(reopened, bridge, begin.AddSeconds(-1));
                Hex.True(main.Available == 0 && dungeon.Available == 0, "pending event emits no update before start");
                QaItemPeriodCommands.Tick(reopened, bridge, begin);
                // Native layout, synthetic identity1/template7/expiry1800000120; not presented as a retail capture.
                var expected = Convert.FromHexString("16000000C314010000000700000078D2496B00000000");
                Hex.Eq(main.Frame(), expected, "Arb083:5557 AS_UPDATE_ITEM_PERIOD i32/i32/i64");
                Hex.Eq(dungeon.Frame(), expected, "native update broadcasts every linked World");
                // Use the reopened event store for the item records too; item ownership is an opaque DB identifier.
                for (int op = 7; op <= 8; op++)
                {
                    reopened.UpsertItem(100 + op, 1, 0, op, 7, 1, null);
                    var atom = new byte[ItemCreate.AtomSize]; BitConverter.GetBytes(op).CopyTo(atom, 4);
                    BitConverter.GetBytes(100 + op).CopyTo(atom, 0x10); BitConverter.GetBytes(7).CopyTo(atom, 0x18);
                    BitConverter.GetBytes(1L).CopyTo(atom, 0x38); BitConverter.GetBytes(1L).CopyTo(atom, 0x50);
                    DbProxyHandlers.WriteArbTimestamp(atom, 0x1F0, new DateTime(1970, 1, 1));
                    byte[] reply = BitConverter.GetBytes(14).Concat(BitConverter.GetBytes(atom.Length)).Concat(atom).ToArray();
                    QaItemPeriodCommands.ApplyCreatedAtoms(reopened, reply, 0); ItemCreate.StoreRecords(reopened, reply, 0);
                    var timestamp = new byte[16]; DbProxyHandlers.WriteArbTimestamp(timestamp, 0, begin.AddSeconds(120).UtcDateTime);
                    Hex.Eq(reply.AsSpan(8 + 0x1F0, 16).ToArray(), timestamp, "native active event expiry overrides unset op7/op8 timestamp");
                    Hex.Eq(reopened.GetItem(100 + op)!.Record!.AsSpan(0x1D0, 16).ToArray(), timestamp, "same expiry persists in ItemData");
                    Hex.True(reply[8 + 0x200] == 1 && reopened.GetItem(100 + op)!.Record![0x1E0] == 1, "periodByWebAdmin flag matches both forms");
                    var unchanged = (byte[])reply.Clone(); QaItemPeriodCommands.Clock = () => begin.AddDays(1);
                    QaItemPeriodCommands.ApplyCreatedAtoms(reopened, reply, 0); Hex.Eq(reply, unchanged, "a supplied timestamp is never recomputed");
                    QaItemPeriodCommands.Clock = () => begin;
                }
                QaItemPeriodCommands.Tick(reopened, bridge, begin.AddSeconds(60));
                Hex.Eq(main.Frame(), Convert.FromHexString("0A000000C41401000000"), "native expiry of application window removes by event ID"); dungeon.Frame();
                Hex.True(reopened.GetQaItemPeriods().Count == 1, "ended event persists until its item-expiry time");
                QaItemPeriodCommands.Tick(reopened, bridge, begin.AddSeconds(120));
                Hex.True(reopened.GetQaItemPeriods().Count == 0 && main.Available == 0 && dungeon.Available == 0,
                    "final native SQL cleanup sends no additional remove");
            }
            using (var h = new T201AccountHarness())
            {
                QaItemPeriodCommands.TryExecute(h.Client.Session, h.Store,
                    GmCommandParser.Parse("add_item_period 7 202701010000 202701020000 202701030000")!, QuietLog());
                h.Client.Frame(); Hex.True(h.Store.GetQaItemPeriods().Single().Template == 7, "native QA parser writes validated dates and template");
                QaItemPeriodCommands.TryExecute(h.Client.Session, h.Store, GmCommandParser.Parse("delete_item_period 7")!, QuietLog());
                h.Client.Frame(); Hex.True(h.Store.GetQaItemPeriods().Count == 0 && h.Main.Available == 0 && h.Instance.Available == 0,
                    "native pending-event deletion removes SQL and sends only the custom confirmation");
            }
            foreach (string name in QaItemPeriodCommands.Names)
                Hex.True(GmCommandHandlers.Classify(true, 0, GmCommandParser.Parse(name)) == GmDispatch.NotAuthorised, "operator-only period command: " + name);
        }
        finally
        {
            T185Environment.SetWorld(null); QaItemPeriodCommands.Clock = oldClock; QaItemSheet.Entry.UseBuiltIn();
            SqliteConnection.ClearAllPools(); Directory.Delete(directory, true);
        }
    }
}
