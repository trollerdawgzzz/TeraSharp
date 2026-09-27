// SPDX-License-Identifier: MIT
// Copyright (c) 2026 the TeraSharp contributors

using System.Text;
using TeraSharp.Arbiter.Game;
using TeraSharp.Arbiter.Handlers;
using TeraSharp.Arbiter.Protocol;
using TeraSharp.Arbiter.World;

namespace TeraSharp.Arbiter.Tests;

// ============ T208d: a World that links after a party formed must be told about it ============
//
// T208c pinned the failure: cap_bg4 17:07:44, ABS_CREATE went out with two good handles and world
// 10 answered BSA_CREATE_LOG with the party list 0 / 0 - it had never heard of either party,
// because both formed at 10:04:27/34 and world 10's links only came up at 10:07:01. The live
// mirror broadcasts to every World that is connected AT THE TIME (PartyWiring.RouteWorldAction),
// and nothing replayed the table to a World that connected later.
//
// The real Arbiter does. PartyManager::OnConnectWorldServer(int) - Arb_part_079.c:16418,
// FUN_140920480 - takes the party-table lock, walks the whole party list and calls
// Party::UnicastPartyInfoToSpecialWorldServer(int) on each one (Arb_part_067.c:15448,
// FUN_1407c7580), which collects the party's live member slots into a
// vector<PartyMemberBasicInfo> and sends ONE AS_DO_CREATE_PARTY carrying the whole roster
// (FUN_1407ae9d0 = PKT_AS_DO_CREATE_PARTY_WRITE<vector<PartyMemberBasicInfo>>, Arb066:18282)
// followed by AS_DO_SET_LOOTING_METHOD for the same party (FUN_1407af2b0). No per-member
// AS_DO_ADD_PARTY_MEMBER on this path - the roster in the create frame IS the member list.
//
// Because it runs exactly once, when the World connects, it can only ever describe parties that
// already exist, so no World is told about the same party twice.
public static partial class Tests
{
    private static bool T208dSays(byte[] frame, string text)
        => ((ReadOnlySpan<byte>)frame).IndexOf((ReadOnlySpan<byte>)Encoding.Unicode.GetBytes(text)) >= 0;

    /// <summary>
    /// T208d. Two parties form while only world 0 is up; world 10 links afterwards and is handed
    /// both rosters on connect. The BG create that follows then names handles that world knows, so
    /// its BSA_CREATE_LOG comes back with a non-zero party list instead of 0 / 0.
    ///
    /// <para><b>T208d-b - what counts as a member slot.</b>
    /// <c>Party::UnicastPartyInfoToSpecialWorldServer</c> (Arb_part_067.c:15455-15478) walks
    /// <c>party+0x1c8</c> for <c>0x1e</c> = 30 slots at stride 0x30 ints and takes a slot when
    /// <c>slot[0] != -1 &amp;&amp; slot[1] != 0</c> - occupancy, and nothing else. There is no
    /// online, session or dummy test anywhere in that loop, and each accepted slot appends one
    /// 0xA0-byte <c>PartyMemberBasicInfo</c> (= <see cref="PartyPackets.MemberBasicInfoSize"/>) to
    /// the vector handed to the single <c>AS_DO_CREATE_PARTY</c> write. So a QA dummy and an
    /// offline member both travel, which is exactly what <c>Party.Members()</c> yields.
    /// <c>BuildWorldConnectReplay</c> was right; this test's expected roster size was not.</para>
    /// </summary>
    [Test] public static void T208d_party_formed_before_a_world_links_is_replayed_on_connect()
    {
        string map = Path.GetTempFileName();
        using var env = new T185Environment(null);
        var storeProperty = typeof(TeraSharp.Arbiter.Program).GetProperty("Store")!;
        object? previousStore = storeProperty.GetValue(null); storeProperty.SetValue(null, null);
        DungeonRouting.ResetForTest(); PartyWiring.ResetForTests();
        BattlefieldCreation.ResetGmCreateForTests();
        try
        {
            File.WriteAllText(map, "{\"maps\":{\"376012\":{\"S_SYSTEM_MESSAGE_CUSTOM\":39244}}}");
            var defs = new DefinitionRegistry(QuietLog());
            defs.RegisterFromDef("S_SYSTEM_MESSAGE_CUSTOM", "ref formatted\nstring formatted\n");
            var opcodes = OpcodeTable.LoadFromFile(map, "376012");
            var bridge = new WorldBridge(WorldReplayTable.Load("/nonexistent", QuietLog()), QuietLog());
            // WorldBridge's ctor seeds ServerConfig.xml (T111); this test owns the map. T208c-b.
            DungeonRouting.Channels.Clear();
            using var main = new T192WorldPeer(bridge, 29, 0);
            using var first = new T185Client(defs, opcodes, QuietLog());
            using var second = new T185Client(defs, opcodes, QuietLog());
            T185Environment.SetWorld(bridge);
            BattleFieldSheet.SetForTest(new[] { new BattleFieldEntry(38, "DeathMatch", 15, 70, 70, 115) });
            DungeonRouting.Channels.MapContinent(115, 10);

            Hex.True(PartyWiring.ReplayToWorld(10) == 0,
                "with no party in the table there is nothing to replay");

            // Both parties form while world 10 does not exist yet - cap_bg4 10:04:27 and 10:04:34.
            foreach (var (client, id, name) in new[] { (first, 1u, "dobb"), (second, 2u, "mate") })
            {
                client.Session.PlayerId = id; client.Session.GameId = 19900 + id;
                client.Session.SelectedCharacter = new FakeCharacter { Id = id, Name = name, Level = 70 };
                client.Session.EnterWorld(); client.Session.CurrentWorldId = 0;
                PartyWiring.Register(client.Session);
                int bot = (int)id + 10;
                PartyWiring.Manager.Register(new((uint)bot, bot, "bot" + bot, 70, 0, 0, 0, (ulong)bot));
                Hex.True(PartyWiring.Manager.OnWorldFrame(PartyPackets.SA_JOIN_PARTY,
                    SaJoinPartyPayload((int)id, bot)).Rejected == null, "each leader has a party of their own");
            }
            long partyA = PartyWiring.Manager.FindByMember(1)!.Id;
            long partyB = PartyWiring.Manager.FindByMember(2)!.Id;
            Hex.True(partyA != 0 && partyB != 0 && partyA != partyB, "two distinct live parties");
            Hex.True(!bridge.HasLinks(10), "world 10 is not up while they form");

            // World 10 links now, and the replay hands it both rosters.
            using var owner = new T192WorldPeer(bridge, 56, 10);
            while (owner.Available > 0) owner.Frame();          // the link itself says nothing
            Hex.True(PartyWiring.ReplayToWorld(10) == 4,
                "two parties replay as two AS_DO_CREATE_PARTY plus two AS_DO_SET_LOOTING_METHOD");

            foreach (long partyId in new[] { Math.Min(partyA, partyB), Math.Max(partyA, partyB) })
            {
                var create = owner.Frame();
                Hex.True(BitConverter.ToUInt16(create, 4) == PartyPackets.AS_DO_CREATE_PARTY,
                    $"the roster comes first for party 0x{partyId:X}");
                Hex.True(BitConverter.ToInt64(create, 0x0E) == partyId,
                    $"AS_DO_CREATE_PARTY names party 0x{partyId:X}, not 0x{BitConverter.ToInt64(create, 0x0E):X}");
                // T208d-b: the assertion is "the WHOLE roster", not a fixed number - each party
                // here is a leader plus one QA dummy, and the dummy counts (see the slot test
                // quoted above the method). Reading the count off the table makes the frame prove
                // it dropped nobody, whatever the fixture is built from.
                int roster = PartyWiring.Manager.FindById(partyId)!.Count;
                Hex.True(roster == 2, $"party 0x{partyId:X} is a leader and one dummy, not {roster}");
                Hex.True(BitConverter.ToUInt32(create, 0x0A) == roster * (uint)PartyPackets.MemberBasicInfoSize
                    && create.Length == 0x38 + roster * PartyPackets.MemberBasicInfoSize,
                    $"the whole {roster}-member roster travels in the one create frame, as retail's vector does"
                    + $" - got {BitConverter.ToUInt32(create, 0x0A)} B in a {create.Length} B frame");
                var loot = owner.Frame();
                Hex.True(BitConverter.ToUInt16(loot, 4) == PartyPackets.AS_DO_SET_LOOTING_METHOD
                    && BitConverter.ToInt64(loot, 6) == partyId,
                    $"and the looting settings follow for the same party 0x{partyId:X}");
            }
            Hex.True(owner.Available == 0 && main.Available == 0,
                "the replay is a unicast to the connecting World and nothing else goes out");

            // The BG create now names handles world 10 has been told about, so its answer carries
            // them instead of 0 / 0 - which is the whole point.
            BattlefieldCreation.CreateForGm(bridge, first.Session, new[] { "38", "mate" });
            var abs = owner.Frame();
            Hex.True(BitConverter.ToUInt16(abs, 4) == BattlefieldCreation.ABS_CREATE
                && BitConverter.ToInt64(abs, 0x13) == partyA && BitConverter.ToInt64(abs, 0x1B) == partyB,
                "ABS_CREATE carries the two replayed handles, caller's party first");
            first.Frame();   // "Enter battleField[38]"

            var said = new List<string>();
            BattlefieldCreation.ArmGmCreateForTests(38, new[] { partyA, partyB }, 10, said.Add);
            new BattlefieldCreation().TryHandle(owner.Link, null, BattlefieldCreation.BSA_CREATE_LOG,
                T208cCreateLog(0x0AF00002, 38, partyA, partyB));
            Hex.True(said.Count == 0 && BattlefieldCreation.PendingGmTemplate == null,
                "a World that was replayed resolves both handles - no refusal: " + string.Join(" | ", said));
        }
        finally
        {
            BattlefieldCreation.ResetGmCreateForTests();
            T185Environment.SetWorld(null); storeProperty.SetValue(null, previousStore);
            DungeonRouting.ResetForTest(); PartyWiring.ResetForTests();
            if (File.Exists(map)) File.Delete(map);
        }
    }

    /// <summary>
    /// T208d. The builder itself: one create plus one looting frame per party, ordered by party id,
    /// and a party whose last member has gone describes nothing.
    /// </summary>
    [Test] public static void T208d_world_connect_replay_is_one_create_and_one_loot_per_party()
    {
        PartyWiring.ResetForTests();
        try
        {
            var manager = PartyWiring.Manager;
            Hex.True(manager.BuildWorldConnectReplay().Count == 0, "an empty table replays nothing");

            for (int id = 1; id <= 2; id++)
            {
                manager.Register(new((uint)id, id, "lead" + id, 70, 0, 0, 0, (ulong)id));
                int bot = id + 10;
                manager.Register(new((uint)bot, bot, "bot" + bot, 70, 0, 0, 0, (ulong)bot));
                Hex.True(manager.OnWorldFrame(PartyPackets.SA_JOIN_PARTY,
                    SaJoinPartyPayload(id, bot)).Rejected == null, "a two-member party per leader");
            }

            var frames = manager.BuildWorldConnectReplay();
            Hex.True(frames.Count == 4, $"two parties is four frames, not {frames.Count}");
            Hex.True(frames[0].Opcode == PartyPackets.AS_DO_CREATE_PARTY
                && frames[1].Opcode == PartyPackets.AS_DO_SET_LOOTING_METHOD
                && frames[2].Opcode == PartyPackets.AS_DO_CREATE_PARTY
                && frames[3].Opcode == PartyPackets.AS_DO_SET_LOOTING_METHOD,
                "create then looting, per party - the order Party::UnicastPartyInfoToSpecialWorldServer sends");

            long idA = BitConverter.ToInt64(frames[0].Payload, 8);
            long idB = BitConverter.ToInt64(frames[2].Payload, 8);
            Hex.True(idA == PartyWiring.Manager.FindByMember(1)!.Id
                && idB == PartyWiring.Manager.FindByMember(2)!.Id && idA < idB,
                "lowest party id first, and each frame names its own party");
            Hex.True(BitConverter.ToUInt32(frames[0].Payload, 4) == 2 * (uint)PartyPackets.MemberBasicInfoSize,
                "two members, both in the one create frame");
            Hex.True(BitConverter.ToInt64(frames[1].Payload, 0) == idA,
                "the looting frame carries the party id at payload 0");
        }
        finally { PartyWiring.ResetForTests(); }
    }
}
