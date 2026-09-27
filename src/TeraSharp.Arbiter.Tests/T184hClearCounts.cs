// SPDX-License-Identifier: MIT
// Copyright (c) 2026 the TeraSharp contributors

using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;
using TeraSharp.Arbiter.Game;
using TeraSharp.Arbiter.Handlers;
using TeraSharp.Arbiter.Protocol;
using TeraSharp.Arbiter.World;

namespace TeraSharp.Arbiter.Tests;

public static partial class Tests
{
    private static byte[] T184hClearFrame(string capture, int record)
    {
        string path = FindRepoFile(Path.Combine("data", "t184h", "clear-count-frames.json"))
            ?? throw new FileNotFoundException("T184h clear-count frames missing");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        return Convert.FromHexString(doc.RootElement.EnumerateArray().Single(f =>
            f.GetProperty("capture").GetString() == capture &&
            f.GetProperty(capture == "cap_2man" ? "source_record" : "n").GetInt32() == record)
            .GetProperty("hex").GetString()!);
    }

    [Test] public static void T184h_clear_count_request_is_forwarded_to_current_world_byte_exact()
    {
        if (FixtureOrSkip(Path.Combine("data", "t184h", "clear-count-frames.json"), "T184h clear-count-frames.json") is null) return;
        byte[] request = T184hClearFrame("cap_2man_client2", 822);
        byte[] expected = T184hClearFrame("cap_2man", 1464);
        byte[] reply = T184hClearFrame("cap_2man_client2", 824);
        byte[] tunnel = T184hClearFrame("cap_2man", 1466);
        Hex.Eq(expected[30..], request, "retail request is tunneled unchanged");
        Hex.Eq(tunnel[38..], reply, "337-byte retail response is produced by World");
        string map = Path.GetTempFileName();
        using var env = new T185Environment(null);
        try
        {
            File.WriteAllText(map, "{\"maps\":{\"376012\":{}}}");
            var bridge = new WorldBridge(WorldReplayTable.Load("/nonexistent", QuietLog()), QuietLog());
            using var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            listener.Bind(new IPEndPoint(IPAddress.Loopback, 0)); listener.Listen(1);
            using var local = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            local.Connect(listener.LocalEndPoint!);
            using var peer = listener.Accept(); peer.ReceiveTimeout = 2000;
            var link = new WorldLink(1, local, bridge, QuietLog());
            ((List<WorldLink>)typeof(WorldBridge).GetField("_links", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(bridge)!).Add(link);
            T185Environment.SetWorld(bridge);
            using var client = new T185Client(new DefinitionRegistry(QuietLog()),
                OpcodeTable.LoadFromFile(map, "376012"), QuietLog());
            client.Session.GameId = BitConverter.ToUInt64(expected, 14);
            client.Session.SelectedCharacter = new FakeCharacter { Id = 1, Name = "dobb", Level = 70 };
            client.Session.EnterWorld();
            foreach (int world in new[] { 0, 13 })
            {
                link.WorldId = world; client.Session.CurrentWorldId = world;
                Hex.True(ArbiterClientHandlers.OnDungeonClearCountList(client.Session, request.AsMemory(4)),
                    "existing registered handler accepts the captured request");
                var actual = new byte[expected.Length];
                for (int n = 0; n < actual.Length;)
                {
                    int read = peer.Receive(actual, n, actual.Length - n, SocketFlags.None);
                    Hex.True(read > 0, "World socket stays open"); n += read;
                }
                // Only the Arbiter's monotonic tick is runtime state, full-frame22..29.
                expected.AsSpan(22, 8).CopyTo(actual.AsSpan(22, 8));
                Hex.Eq(actual, expected, "cap_2man raw1464+0; current World owns the roster");
                Hex.True(client.Available == 0, "no synthetic fourteen-row reply accompanies the forward");
            }
            T185Environment.SetWorld(null);
        }
        finally { T185Environment.SetWorld(null); File.Delete(map); }
    }

    [Test] public static void T184h_clear_count_standalone_sheet_roster_matches_cap_2man()
    {
        if (FixtureOrSkip(Path.Combine("data", "t184h", "clear-count-frames.json"), "T184h clear-count-frames.json") is null) return;
        if (FixtureOrSkip(Path.Combine("data", "t184h", "dungeon-clear-sheets", "DungeonMatching.xml"), "T184h projected clear-count sheets") is null) return;
        string sheets = Path.GetDirectoryName(FindRepoFile(Path.Combine("data", "t184h",
            "dungeon-clear-sheets", "DungeonMatching.xml"))
            ?? throw new FileNotFoundException("T184h projected sheet fixtures missing"))!;
        string map = Path.GetTempFileName();
        string changed = Path.Combine(Path.GetTempPath(), "T184h-clear-" + Guid.NewGuid().ToString("N"));
        var oldStore = TeraSharp.Arbiter.Program.Store;
        using var env = new T185Environment("1");
        using var store = StoreWithTwoAccounts();
        try
        {
            File.WriteAllText(map, "{\"maps\":{\"376012\":{}}}");
            Hex.True(DungeonClearCountSheet.Entry.Load(sheets).FromSheet, "native list inputs loaded from sheets");
            Hex.True(DatasheetLoader.All.Contains(DungeonClearCountSheet.Entry), "startup/check-config reports the reader");
            typeof(TeraSharp.Arbiter.Program).GetProperty("Store")!.SetValue(null, store);
            store.SetDungeonClearCount(1, 9781, 1);
            using var client = new T185Client(new DefinitionRegistry(QuietLog()),
                OpcodeTable.LoadFromFile(map, "376012"), QuietLog());
            client.Session.PlayerId = 1;
            client.Session.SelectedCharacter = new FakeCharacter { Id = 1, Name = "dobb", Level = 70 };
            var request = T184hClearFrame("cap_2man_client2", 822);
            Hex.True(ArbiterClientHandlers.OnDungeonClearCountList(client.Session, request.AsMemory(4)), "standalone handler answered");
            Hex.Eq(client.Frame(), T184hClearFrame("cap_2man_client2", 824),
                "cap_2man client2:824, all 25 rows and clear1/newbie1 for 9781");
            Hex.True(DungeonClearCountSheet.IsNewbie(9) && !DungeonClearCountSheet.IsNewbie(10),
                "native IsNewbie compares count < sheet threshold, not count == 0");

            Directory.CreateDirectory(changed);
            File.WriteAllText(Path.Combine(changed, "DungeonMatching.xml"),
                "<DungeonMatching><Dungeon id='9781' dungeonMinLevel='68' dungeonMaxLevel='70' minItemLevel='453'/>"
                + "<Dungeon id='9991' dungeonMinLevel='68' dungeonMaxLevel='70' minItemLevel='500'/>"
                + "<Dungeon id='9992' dungeonMinLevel='10' dungeonMaxLevel='69' minItemLevel='501'/>"
                + "<Dungeon id='9993' dungeonMinLevel='68' dungeonMaxLevel='70' minItemLevel='502'/></DungeonMatching>");
            File.WriteAllText(Path.Combine(changed, "DungeonNewbieBonus.xml"),
                "<DungeonNewbieBonus><DungeonNewbieBonusCheck clearCount='2'/></DungeonNewbieBonus>");
            File.WriteAllText(Path.Combine(changed, "DungeonData_9781.xml"), "<Dungeon continentId='9781'/>");
            File.WriteAllText(Path.Combine(changed, "DungeonData_9991.xml"),
                "<Dungeon continentId='9991'><Condition type='completeQuest' value='123'/></Dungeon>");
            File.WriteAllText(Path.Combine(changed, "DungeonData_9992.xml"), "<Dungeon continentId='9992'/>");
            DungeonClearCountSheet.Entry.Load(changed);
            Hex.True(DungeonClearCountSheet.ForLevel(70).Select(r => r.Id).SequenceEqual(new[] { 9781 }),
                "exclude missing DungeonData, wrong level and unfinished prerequisite quests");
            Hex.True(DungeonClearCountSheet.ForLevel(70, new[] { 123 }).Select(r => r.Id).SequenceEqual(new[] { 9991, 9781 })
                && DungeonClearCountSheet.IsNewbie(1) && !DungeonClearCountSheet.IsNewbie(2),
                "sheet edits change eligibility, item-level order and newbie threshold without a hardcoded ID set");
        }
        finally
        {
            typeof(TeraSharp.Arbiter.Program).GetProperty("Store")!.SetValue(null, oldStore);
            DungeonClearCountSheet.Entry.UseBuiltIn(); File.Delete(map);
            if (Directory.Exists(changed)) Directory.Delete(changed, true);
        }
    }
}
