// SPDX-License-Identifier: MIT
// Copyright (c) 2026 the TeraSharp contributors

using System.Text;
using System.Text.Json;
using TeraSharp.Arbiter.Game;
using TeraSharp.Arbiter.Handlers;
using TeraSharp.Arbiter.Protocol;
using TeraSharp.Arbiter.World;

namespace TeraSharp.Arbiter.Tests;

public static partial class Tests
{
    private static byte[] T201PartyFrame(int record)
    {
        string path = FindRepoFile(Path.Combine("data", "t201", "party-command-frames.json"))
            ?? throw new FileNotFoundException("tracked T201 party frames missing");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        return Convert.FromHexString(doc.RootElement.GetProperty("cap_bg1").EnumerateArray()
            .Single(x => x.GetProperty("source_record").GetInt32() == record).GetProperty("hex").GetString()!);
    }

    [Test] public static void T201_QA_party_create_and_add_match_both_retail_parties()
    {
        if (FixtureOrSkip(Path.Combine("data", "t201", "party-command-frames.json"), "T201 party-command-frames.json") is null) return;
        foreach (var (createRecord, addRecord) in new[] { (11887, 11971), (12077, 12167) })
        {
            byte[] create = T201PartyFrame(createRecord), add = T201PartyFrame(addRecord);
            var manager = new PartyManager(QuietLog());
            PartyManager.PartyPlayer Player(int at)
            {
                int id = BitConverter.ToInt32(create, at + 4);
                return new((uint)id, id, Encoding.Unicode.GetString(create, at + 36, 74).Split('\0')[0],
                    BitConverter.ToInt32(create, at + 16), BitConverter.ToInt32(create, at + 20),
                    BitConverter.ToInt32(create, at + 24), BitConverter.ToInt32(create, at + 28),
                    BitConverter.ToUInt64(create, at + 8), Laurel: BitConverter.ToInt32(create, at + 0x74),
                    AwakenGrade: BitConverter.ToInt32(create, at + 0x78),
                    Online: create[at + 112] != 0, QaDummy: create[at + 112] == 0);
            }
            var leader = Player(56); var dummy = Player(216);
            manager.Register(leader); manager.Register(dummy);
            var made = manager.JoinForQa(leader, dummy, false);
            long partyId = manager.FindByMember(leader.UserDbId)!.Id;
            BitConverter.GetBytes(partyId).CopyTo(create, 14);
            foreach (int at in new[] { 56, 216 })
            {
                // Arb068:14433-14464: wcsncpy_s initializes only through the terminator;
                // the constructor leaves alignment bytes71..73 and92..97 untouched.
                int nameEnd = at + 0x24;
                while (nameEnd < at + 0x6E && BitConverter.ToUInt16(create, nameEnd) != 0) nameEnd += 2;
                nameEnd += 2;
                if (nameEnd < at + 0x6E) Array.Clear(create, nameEnd, at + 0x6E - nameEnd);
                Array.Clear(create, at + 0x71, 3); Array.Clear(create, at + 0x92, 6);
            }
            Hex.Eq(T180Frame(0x139E, made.ToWorld.Single(x => x.Opcode == 0x139E).Payload), create,
                "native QA creation: all semantic bytes; normalized allocated party ID, unused name tail and alignment padding");
            int thirdId = BitConverter.ToInt32(add, 22);
            var third = new PartyManager.PartyPlayer((uint)thirdId, thirdId,
                Encoding.Unicode.GetString(add[67..]).TrimEnd('\0'), 1, 0, 0, 1, 0, Online: false, QaDummy: true);
            manager.Register(third);
            var added = manager.JoinForQa(leader, third, false);
            BitConverter.GetBytes(partyId).CopyTo(add, 10);
            Hex.Eq(T180Frame(0x139F, added.ToWorld.Single(x => x.Opcode == 0x139F).Payload), add,
                "complete71B dummy-add matches, including offline bit and zero World object");
            var dismiss = new byte[12]; BitConverter.GetBytes(leader.UserDbId).CopyTo(dismiss, 8);
            manager.OnWorldFrame(PartyPackets.SA_DISMISS_PARTY, dismiss);
            Hex.True(manager.PartyCount == 0 && !manager.TryGetTicket(dummy.UserDbId, out _)
                && !manager.TryGetTicket(thirdId, out _), "dismiss removes ephemeral QA users from registry");
        }
    }

    [Test] public static void T201_party_count_raid_and_board_commands_are_authorized_and_mutate_real_state()
    {
        string map = Path.GetTempFileName();
        using var env = new T185Environment(null);
        var storeProperty = typeof(TeraSharp.Arbiter.Program).GetProperty("Store")!;
        object? savedStore = storeProperty.GetValue(null); storeProperty.SetValue(null, null);
        PartyWiring.ResetForTests(); PartyMatchManager.Reset();
        try
        {
            File.WriteAllText(map, "{\"maps\":{\"376012\":{\"S_SYSTEM_MESSAGE\":62222,\"S_SYSTEM_MESSAGE_CUSTOM\":39244,\"S_SHOW_PARTY_MATCH_INFO\":57189,\"S_OTHER_USER_APPLY_PARTY\":64886}}}");
            var defs = new DefinitionRegistry(QuietLog());
            defs.RegisterFromDef("C_ADMIN", "ref command\nstring command\n");
            defs.RegisterFromDef("S_SYSTEM_MESSAGE", "ref message\nstring message\n");
            defs.RegisterFromDef("S_SYSTEM_MESSAGE_CUSTOM", "ref formatted\nstring formatted\n");
            defs.RegisterFromDef("S_SHOW_PARTY_MATCH_INFO", "int16 pageCurrent\nint16 pageCount\narray listings\n- int32 leaderId\n- byte isRaid\n- int16 playerCount\n- string message\n- string leader\n");
            defs.RegisterFromDef("S_OTHER_USER_APPLY_PARTY", "byte unk1\nint32 pid\nint16 class\nint16 race\nint16 gender\nint16 level\nbyte unk2\nstring name\n");
            var bridge = new WorldBridge(WorldReplayTable.Load("/nonexistent", QuietLog()), QuietLog());
            T185Environment.SetWorld(bridge);
            using var first = new T185Client(defs, OpcodeTable.LoadFromFile(map, "376012"), QuietLog());
            using var second = new T185Client(defs, OpcodeTable.LoadFromFile(map, "376012"), QuietLog());
            foreach (var (client, id, name) in new[] { (first, 41u, "qaone"), (second, 42u, "qatwo") })
            {
                client.Session.PlayerId = id; client.Session.GameId = 201000 + id;
                client.Session.Account.Name = "t201-op";
                client.Session.SelectedCharacter = new FakeCharacter { Id = id, Name = name, Level = 70 };
                client.Session.EnterWorld(); PartyWiring.Register(client.Session);
            }
            var handler = new GmCommandHandlers(QuietLog());
            void Run(T185Client client, string text) => handler.OnAdminCommand(client.Session,
                new byte[] { 6, 0 }.Concat(Encoding.Unicode.GetBytes(text + "\0")).ToArray());
            void Drain(T185Client client) { while (client.Available > 0) client.Frame(); }
            T181WithOperators(null, () =>
            {
                foreach (string name in QaPartyCommands.Names) Run(first, name + " 2");
                Hex.True(first.Available == 0 && PartyWiring.Manager.PartyCount == 0 && PartyMatchManager.Count == 0,
                    "every local party command is refused by central authorization for a normal account");
            });
            T181WithOperators("t201-op", () =>
            {
                Run(first, "party 2");
                var party = PartyWiring.Manager.FindByMember(41)!;
                Hex.True(party.Count == 3 && !party.Raid && party.Members().Count(x => !x.Online && x.GameId == 0) == 2,
                    "authorized numeric extension adds two ephemeral dummies");
                Hex.True(second.Available == 0, "no roster/custom message is delivered to an outsider"); Drain(first);
                Run(first, "party 3"); Drain(first);
                Hex.True(party.Count == 3, "capacity refusal is atomic");
                var dismiss = new byte[12]; BitConverter.GetBytes(41).CopyTo(dismiss, 8);
                PartyWiring.Manager.OnWorldFrame(PartyPackets.SA_DISMISS_PARTY, dismiss);
                Run(first, "raid 2"); Drain(first);
                Hex.True(PartyWiring.Manager.FindByMember(41) is { Raid: true, Count: 3 }, "native raid count creates raid");
                PartyWiring.Manager.OnWorldFrame(PartyPackets.SA_DISMISS_PARTY, dismiss);
                Run(first, "party qaone namedbot"); Drain(first);
                Hex.True(PartyWiring.Manager.FindByMember(41)!.Members().Any(x => x.Name == "namedbot"), "two-name native syntax retained");
                PartyWiring.Manager.OnWorldFrame(PartyPackets.SA_DISMISS_PARTY, dismiss);
                Run(first, "reg_party \"old text\"");
                Hex.True(BitConverter.ToUInt16(first.Frame(), 2) == 0xF30E && PartyMatchManager.Find(41)?.Message == "old text", "register persists PR and emits SMT997");
                Run(first, "change_pr \"new text\"");
                Hex.True(first.Available == 0 && PartyMatchManager.Find(41)?.Message == "new text", "native change_pr updates silently");
                Run(second, "apply_party 41");
                Hex.True(BitConverter.ToUInt16(first.Frame(), 2) == 0xFD76 && PartyWiring.Manager.HasApplication(42, 41), "apply records candidate and notifies listed target");
                Run(first, "show_cand");
                var candidates = first.Frame(); int candidateAt = BitConverter.ToUInt16(candidates, 6);
                Hex.True(BitConverter.ToUInt16(candidates, 2) == 0xF12D && BitConverter.ToUInt16(candidates, 4) == 1
                    && BitConverter.ToInt32(candidates, candidateAt + 6) == 42, "show_cand returns the live application");
                Hex.True(Encoding.Unicode.GetString(candidates, 10, candidateAt - 10).TrimEnd('\0') == "new text",
                    "candidate header carries the listing's current PR text");
                Run(first, "show_party 0 1 70 \"\"");
                Hex.True(BitConverter.ToUInt16(first.Frame(), 2) == 0xDF65, "browse emits live board");
                Run(first, "unreg_party");
                Hex.True(BitConverter.ToUInt16(first.Frame(), 2) == 0xF30E && first.Available == 0 && PartyMatchManager.Find(41) == null,
                    "native QA unregister emits only SMT994, no extra browse page");
                Run(first, "world_of_party_match");
                Hex.True(first.Available == 0 && PartyMatchManager.Count == 0, "native diagnostic has no observable effect");
                Run(first, "sim_match_progress 1 2 3");
                Hex.Eq(QaMatchSimulation.Progress(9781, false)!, MatchQueueManager.BuildMatchProgress(9781, 0, 0, 1, 2, 3), "authorized simulation command reaches the actual progress consumer");
                Run(first, "sim_match_progress");
                Hex.True(QaMatchSimulation.Progress(9781, false) == null, "authorized no-args simulation command clears the override");
            });
        }
        finally
        {
            T185Environment.SetWorld(null); storeProperty.SetValue(null, savedStore);
            PartyWiring.ResetForTests(); PartyMatchManager.Reset(); File.Delete(map);
        }
    }

    [Test] public static void T201_simulated_match_progress_expires_without_changing_the_pool()
    {
        var oldClock = QaMatchSimulation.Clock; long now = 1000;
        try
        {
            QaMatchSimulation.Clock = () => now; MatchQueueManager.Reset();
            var entry = MatchQueueManager.Add(71, new[] { 9781 }, new[] { new MatchQueueManager.Queuer(71, 71, 12, 70, 1) }, DateTimeOffset.UtcNow);
            QaMatchSimulation.Set(7, 8, 9);
            Hex.Eq(MatchQueueManager.ProgressFrame(entry, 9781)!, MatchQueueManager.BuildMatchProgress(9781, 0, 0, 7, 8, 9),
                "native temporary counts override only progress display");
            Hex.True(entry.Size == 1 && entry.State == MatchQueueManager.MatchState.Waiting, "simulation cannot create party members or finish a match");
            now += 600000;
            Hex.True(QaMatchSimulation.Progress(9781, false) == null, "native600000ms window expires");
            QaMatchSimulation.Set(1, 2, 3); QaMatchSimulation.Clear();
            Hex.True(QaMatchSimulation.Progress(9781, false) == null, "no-args command clears simulation");
        }
        finally { QaMatchSimulation.Clear(); QaMatchSimulation.Clock = oldClock; MatchQueueManager.Reset(); }
    }

    [Test] public static void T201_APM_and_match_battle_field_forward_each_command_to_the_current_World()
    {
        string map = Path.GetTempFileName(); using var env = new T185Environment(null);
        var prop = typeof(TeraSharp.Arbiter.Program).GetProperty("Store")!;
        object? old = prop.GetValue(null); prop.SetValue(null, null);
        try
        {
            File.WriteAllText(map, "{\"maps\":{\"376012\":{}}}");
            var defs = new DefinitionRegistry(QuietLog()); defs.RegisterFromDef("C_ADMIN", "ref command\nstring command\n");
            var bridge = new WorldBridge(WorldReplayTable.Load("/nonexistent", QuietLog()), QuietLog());
            using var main = new T192WorldPeer(bridge, 29, 0); using var current = new T192WorldPeer(bridge, 56, 13);
            using var client = new T185Client(defs, OpcodeTable.LoadFromFile(map, "376012"), QuietLog());
            T185Environment.SetWorld(bridge); client.Session.Account.Name = "t201-op";
            client.Session.PlayerId = 43; client.Session.GameId = 201043;
            client.Session.SelectedCharacter = new FakeCharacter { Id = 43, Name = "qa", Level = 70 };
            client.Session.EnterWorld(); client.Session.CurrentWorldId = 13;
            var gm = new GmCommandHandlers(QuietLog());
            foreach (string text in new[] { "apm_use on", "apm_use off", "apm_ask", "apm_add 17", "apm_reset", "match_battle_field 38 other",
                "enchantitemsuccess 1", "allow_teleport", "allow_teleport on", "allow_teleport off" })
            {
                byte[] body = new byte[] { 6, 0 }.Concat(Encoding.Unicode.GetBytes(text + "\0")).ToArray();
                T181WithOperators("t201-op", () => gm.OnAdminCommand(client.Session, body));
                Hex.Eq(current.Frame(), T180Frame(0x2829, GmCommandHandlers.BuildWorldForward(43, 1, text)), "native World command preserves args and current owner");
                T181WithOperators(null, () => gm.OnAdminCommand(client.Session, body));
                Hex.True(main.Available == 0 && current.Available == 0 && client.Available == 0, "normal account refusal sends no World frame");
            }
        }
        finally { T185Environment.SetWorld(null); prop.SetValue(null, old); File.Delete(map); }
    }
}
