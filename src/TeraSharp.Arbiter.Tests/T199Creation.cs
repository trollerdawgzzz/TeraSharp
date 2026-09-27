// SPDX-License-Identifier: MIT
// Copyright (c) 2026 the TeraSharp contributors

using System.Reflection;
using System.Text;
using System.Text.Json;
using TeraSharp.Arbiter.Game;
using TeraSharp.Arbiter.Handlers;
using TeraSharp.Arbiter.Persistence;
using TeraSharp.Arbiter.Protocol;
using TeraSharp.Arbiter.World;

namespace TeraSharp.Arbiter.Tests;

public static partial class Tests
{
    private static byte[] T199CreationFrame(int record, ushort opcode, string capture = "cap_bg1")
    {
        string path = FindRepoFile(Path.Combine("data", "t199", "creation-frames.json"))
            ?? throw new FileNotFoundException("tracked T199 creation fixture missing");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var frame = doc.RootElement.GetProperty(capture).EnumerateArray().Single(f =>
            (f.TryGetProperty("source_record", out var n) ? n.GetInt32() : f.GetProperty("n").GetInt32()) == record
            && f.GetProperty("op").GetInt32() == opcode);
        return Convert.FromHexString(frame.GetProperty("hex").GetString()!);
    }

    [Test] public static void T199_creation_and_open_info_match_every_captured_field()
    {
        if (FixtureOrSkip(Path.Combine("data", "t199", "creation-frames.json"), "T199 creation-frames.json") is null) return;
        foreach (int record in new[] { 7238, 12388, 12533 })
        {
            byte[] frame = T199CreationFrame(record, 0x1518);
            Hex.Eq(T180Frame(0x1518, BattlefieldCreation.BuildCreate(BitConverter.ToInt32(frame, 14),
                new[] { BitConverter.ToInt64(frame, 19), BitConverter.ToInt64(frame, 27) }, frame[18] != 0)), frame,
                "native1518 has byte length16, ordered party IDs and literal GM flag");
        }
        int[] ids = { 5, 10, 11, 26, 27, 28, 29, 30, 37, 38, 39, 40, 46, 47, 70, 71, 110, 118, 119, 156 };
        foreach (var (record, isOpen) in new[] { (2298, false), (2328, true), (8901, false), (8914, true) })
        {
            var rows = BattlefieldCreation.ParseOpen(T199CreationFrame(record, 0x13DF)[6..])!;
            Hex.True(rows.Select(r => r.TemplateId).SequenceEqual(ids)
                && rows.All(r => r.CurrentOpen == isOpen && r.NextOpen == isOpen && r.RemainSec == -1),
                "all20 linked open-info entries, both flags and signed remaining time pinned");
        }
        byte[] broken = T199CreationFrame(8914, 0x13DF)[6..];
        BitConverter.GetBytes(14).CopyTo(broken, 12); // first node next points back to itself
        Hex.True(BattlefieldCreation.ParseOpen(broken) == null && BattlefieldCreation.ParseOpen(new byte[7]) == null,
            "short and cyclic open-info lists do not partially update state");
        var sheet = BattleFieldSheet.Parse("<BattleFieldData><BattleField id='38' type='DeathMatch'><CommonData continentId='115' maxTeamMember='15' minLevel='70' maxLevel='70'/></BattleField></BattleFieldData>");
        Hex.True(sheet.Single().ContinentId == 115, "creation reads the sheet continent, not the template ID as continent");
    }

    [Test] public static void T199_unique_id_and_log_identity_load_issue_reopen_match_native_SQL()
    {
        if (FixtureOrSkip(Path.Combine("data", "t199", "creation-frames.json"), "T199 creation-frames.json") is null) return;
        string path = Path.Combine(Path.GetTempPath(), "t199-creation-" + Guid.NewGuid() + ".db");
        var bridge = new WorldBridge(WorldReplayTable.Load("/nonexistent", QuietLog()), QuietLog());
        using var main = new T192WorldPeer(bridge, 1, 0);
        using var owner = new T192WorldPeer(bridge, 56, 10);
        var creation = new BattlefieldCreation();
        long last = 0;
        try
        {
            using (var store = new CharacterStore(path, QuietLog()))
            {
                Hex.True(store.IssueBattlefieldUniqueId() == 0 && store.IssueBattlefieldUniqueId() == 1,
                    "spIssueBattleFieldUniqueId inserts0 when absent, then increments; not capture-specific seed");
                foreach (var (request, reply) in new[] { (2281, 2282), (8884, 8885) })
                {
                    byte[] expected = T199CreationFrame(reply, 0x1515);
                    last = BitConverter.ToInt32(expected, 6);
                    store.SetCounterValue(CharacterStore.BattlefieldUniqueCounter, last);
                    creation.TryHandle(owner.Link, store, 0x1514, T199CreationFrame(request, 0x1514)[6..]);
                    Hex.Eq(owner.Frame(), expected, "opaque persisted DWORD returned byte-exact on requesting link");
                }
                foreach (var (request, reply, issued, logId) in new[] { (12389, 12403, 12396, 1), (12534, 12536, 12535, 2) })
                {
                    byte[] p = T199CreationFrame(request, 0x13E0)[6..];
                    var native = BattlefieldCreation.ParseLog(p)!;
                    creation.TryHandle(owner.Link, store, 0x13E0, p);
                    Hex.Eq(owner.Frame(), T199CreationFrame(reply, 0x13E1), "SQL identity1/2 and runtime battlefield ID match retail");
                    Hex.True(store.GetBattlefieldLog(logId) == new CharacterStore.BattlefieldLogRow(logId,
                        native.BattlefieldId, native.TemplateId, native.BlueParty, native.RedParty), "both ordered parties retained in durable log");
                    creation.TryHandle(owner.Link, store, 0x1512, T199CreationFrame(issued, 0x1512)[6..]);
                    last++;
                }
                byte[] bad = T199CreationFrame(12389, 0x13E0)[6..]; BitConverter.GetBytes(uint.MaxValue).CopyTo(bad, 0);
                creation.TryHandle(owner.Link, store, 0x13E0, bad);
                Hex.True(owner.Available == 0 && main.Available == 0 && store.GetBattlefieldLog(3) == null,
                    "1512 is one-way; malformed vector neither reserves an identity nor answers");
            }
            using var reopened = new CharacterStore(path, QuietLog());
            Hex.True(reopened.GetBattlefieldLog(1)?.TemplateId == 38 && reopened.GetBattlefieldLog(2)?.TemplateId == 37
                && reopened.GetCounterValue(CharacterStore.BattlefieldUniqueCounter, -1) == last,
                "counter and two creation logs survive process store reopen");
            creation.TryHandle(owner.Link, reopened, 0x1514, Array.Empty<byte>());
            Hex.Eq(owner.Frame(), T180Frame(0x1515, BitConverter.GetBytes(unchecked((int)last))), "reopened startup uses latest issued value");
            Hex.True(reopened.CreateBattlefieldLog(1234, 38, 11, 12) == 3, "SQL identity cannot restart from1 after relaunch");
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Test] public static void T199_GM_creation_uses_template_owner_and_ordered_distinct_parties()
    {
        if (FixtureOrSkip(Path.Combine("data", "t199", "creation-frames.json"), "T199 creation-frames.json") is null) return;
        string map = Path.GetTempFileName();
        using var env = new T185Environment(null);
        var storeProperty = typeof(TeraSharp.Arbiter.Program).GetProperty("Store")!;
        object? previousStore = storeProperty.GetValue(null); storeProperty.SetValue(null, null);
        ulong? oldGame = DbProxyHandlers.GameIdByPlayer.TryGetValue(1, out var previousGame) ? previousGame : null;
        DungeonRouting.ResetForTest(); PartyWiring.ResetForTests();
        BattleFieldSheet.SetForTest(new[] { new BattleFieldEntry(38, "DeathMatch", 15, 70, 70, 115), new BattleFieldEntry(37, "DeathMatch", 3, 70, 70, 115) });
        try
        {
            File.WriteAllText(map, "{\"maps\":{\"376012\":{\"S_SYSTEM_MESSAGE_CUSTOM\":39244}}}");
            var defs = new DefinitionRegistry(QuietLog());
            defs.RegisterFromDef("C_ADMIN", "ref command\nstring command\n");
            defs.RegisterFromDef("S_SYSTEM_MESSAGE_CUSTOM", "ref formatted\nstring formatted\n");
            var opcodes = OpcodeTable.LoadFromFile(map, "376012");
            var bridge = new WorldBridge(WorldReplayTable.Load("/nonexistent", QuietLog()), QuietLog());
            using var main = new T192WorldPeer(bridge, 29, 0);
            using var owner = new T192WorldPeer(bridge, 56, 10);
            using var first = new T185Client(defs, opcodes, QuietLog());
            using var second = new T185Client(defs, opcodes, QuietLog());
            T185Environment.SetWorld(bridge); DungeonRouting.Channels.MapContinent(115, 10);
            foreach (var (client, id, name) in new[] { (first, 1u, "dobb"), (second, 2u, "new") })
            {
                client.Session.PlayerId = id; client.Session.GameId = 19900 + id;
                client.Session.Account.Name = "t199-operator";
                client.Session.SelectedCharacter = new FakeCharacter { Id = id, Name = name, Level = 70 };
                client.Session.EnterWorld(); client.Session.CurrentWorldId = 13;
                PartyWiring.Register(client.Session);
                int bot = (int)id + 10;
                PartyWiring.Manager.Register(new((uint)bot, bot, "bot" + bot, 70, 0, 0, 0, (ulong)bot));
                Hex.True(PartyWiring.Manager.OnWorldFrame(PartyPackets.SA_JOIN_PARTY, SaJoinPartyPayload((int)id, bot)).Rejected == null,
                    "each QA participant has their own existing normal party");
            }
            new BattlefieldCreation().TryHandle(main.Link, null, 0x13DF, T199CreationFrame(8914, 0x13DF)[6..]);
            var handler = new GmCommandHandlers(QuietLog());
            T181WithOperators("t199-operator", () =>
            {
                foreach (var (client, other, clientCapture, command, response, template) in new[] {
                    (first, second, "cap_bg1_client1", 2904, 2905, 38), (second, first, "cap_bg1_client2", 2744, 2745, 37) })
                {
                    handler.OnAdminCommand(client.Session, T199CreationFrame(command, 0xA45C, clientCapture)[4..]);
                    var parties = new[] { PartyWiring.Manager.FindByMember((int)client.Session.PlayerId)!.Id,
                        PartyWiring.Manager.FindByMember((int)other.Session.PlayerId)!.Id };
                    Hex.Eq(owner.Frame(), T180Frame(0x1518, BattlefieldCreation.BuildCreate(template, parties, true)),
                        "both captured commands route to template115 owner10; participant party order follows command");
                    Hex.Eq(client.Frame(), T199CreationFrame(response, 0x994C, clientCapture), "native success text byte-exact");
                }
                byte[] duplicate = new byte[] { 6, 0 }.Concat(Encoding.Unicode.GetBytes("battlefield 38 dobb\0")).ToArray();
                handler.OnAdminCommand(first.Session, duplicate); first.Frame();
                Hex.True(owner.Available == 0 && main.Available == 0, "duplicate party rejected; open-info sender never becomes owner");
                ((List<WorldLink>)typeof(WorldBridge).GetField("_links", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(bridge)!).Remove(owner.Link);
                handler.OnAdminCommand(first.Session, T199CreationFrame(2904, 0xA45C, "cap_bg1_client1")[4..]); first.Frame();
                Hex.True(main.Available == 0 && owner.Available == 0, "missing configured owner refuses creation, no caller/main fallback");
            });
            T181WithOperators(null, () => handler.OnAdminCommand(first.Session,
                T199CreationFrame(2904, 0xA45C, "cap_bg1_client1")[4..]));
            Hex.True(first.Available == 0 && main.Available == 0 && owner.Available == 0,
                "same forced-creation command remains silent for ordinary accounts");
            T185Environment.SetWorld(null);
        }
        finally
        {
            T185Environment.SetWorld(null); storeProperty.SetValue(null, previousStore);
            if (oldGame is ulong old) DbProxyHandlers.GameIdByPlayer[1] = old;
            else DbProxyHandlers.GameIdByPlayer.TryRemove(1, out _);
            DungeonRouting.ResetForTest(); PartyWiring.ResetForTests(); BattleFieldSheet.ResetForTest(); File.Delete(map);
        }
    }
}
