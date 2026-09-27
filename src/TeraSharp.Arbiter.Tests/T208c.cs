// SPDX-License-Identifier: MIT
// Copyright (c) 2026 the TeraSharp contributors

using System.Text;
using TeraSharp.Arbiter.Game;
using TeraSharp.Arbiter.Handlers;
using TeraSharp.Arbiter.Protocol;
using TeraSharp.Arbiter.World;

namespace TeraSharp.Arbiter.Tests;

// ========================= T208c: /@battlefield must never fail silently =========================
//
// cap_bg4 (tap), 17:07:44 - the reported failure. /@battlefield 38 <leader>, both leaders in 3-man
// parties, world 10 linked with 18 continents:
//   194977  A->W  0x1518 ABS_CREATE        template 38, isGm 1, parties 0xAF0000100000002 / ...001
//   194978  W->A  0x13E0 BSA_CREATE_LOG    battlefieldId 0x0AF00002, template 38, party list 0 / 0
//   194979  W->A  0x1512 BSA_CREATE_RESULT (empty)
//   194980  A->W  0x13E1 ABS_CREATE_LOG    log identity 3
//   ... and no 0x1513 offer, ever.
// The same command at 17:09:46 after both parties were re-formed (196198-196201) carried the real
// ids in BSA_CREATE_LOG and produced the 462-byte offer. The two that failed were formed at
// 10:04:27 and 10:04:34; world 10's links came up at 10:07:01, and nothing replays
// AS_DO_CREATE_PARTY to a World that links after a party forms - so world 10 had never heard of
// either handle. Our side sent a well-formed frame and said "Enter battleField[38]" regardless,
// logged nothing, and wrote a battlefield with no parties into the log table.
public static partial class Tests
{
    /// <summary>
    /// BSA_CREATE_LOG as World sends it: [u32 listOffset=26][u32 listBytes=16][i64 battlefieldId]
    /// [i32 templateId] then the two ordered party handles at frame offset 26. cap_bg4 194978/196199
    /// are exactly this, 36 bytes, with the party pair zeroed and real respectively.
    /// </summary>
    private static byte[] T208cCreateLog(long battlefieldId, int templateId, long blue, long red)
    {
        var p = new byte[36];
        BitConverter.GetBytes(26).CopyTo(p, 0);
        BitConverter.GetBytes(16).CopyTo(p, 4);
        BitConverter.GetBytes(battlefieldId).CopyTo(p, 8);
        BitConverter.GetBytes(templateId).CopyTo(p, 16);
        BitConverter.GetBytes(blue).CopyTo(p, 20);
        BitConverter.GetBytes(red).CopyTo(p, 28);
        return p;
    }

    private static bool T208cSays(byte[] frame, string text)
        => ((ReadOnlySpan<byte>)frame).IndexOf((ReadOnlySpan<byte>)Encoding.Unicode.GetBytes(text)) >= 0;

    /// <summary>
    /// T208c. World's zero party list is a refusal and is now reported as one - to the log at
    /// Information and to the GM who asked - instead of being stored as a battlefield with no
    /// parties. The half-resolved case names the handle that was missing, and a create World DID
    /// build says nothing to the caller.
    /// </summary>
    [Test] public static void T208c_world_that_resolves_no_party_is_an_explicit_refusal()
    {
        var bridge = new WorldBridge(WorldReplayTable.Load("/nonexistent", QuietLog()), QuietLog());
        using var owner = new T192WorldPeer(bridge, 56, 10);
        var creation = new BattlefieldCreation();
        long blue = 0x0AF0000100000002, red = 0x0AF0000100000001;

        try
        {
            var said = new List<string>();
            BattlefieldCreation.ArmGmCreateForTests(38, new[] { blue, red }, 10, said.Add);
            Hex.True(BattlefieldCreation.PendingGmTemplate == 38, "the create is armed until World answers");

            creation.TryHandle(owner.Link, null, BattlefieldCreation.BSA_CREATE_LOG,
                T208cCreateLog(0x0AF00002, 38, 0, 0));
            Hex.True(said.Count == 1 && said[0].Contains("battleField[38] was not created")
                && said[0].Contains("world 10") && said[0].Contains("Re-form"),
                "the GM is told which world refused and what to do: " + string.Join(" | ", said));
            Hex.True(BattlefieldCreation.PendingGmTemplate == null && owner.Available == 0,
                "the wait is over and a store-less refusal answers no frame");

            // One handle resolved, one not - name the one that was missing.
            said.Clear();
            BattlefieldCreation.ArmGmCreateForTests(38, new[] { blue, red }, 10, said.Add);
            creation.TryHandle(owner.Link, null, BattlefieldCreation.BSA_CREATE_LOG,
                T208cCreateLog(0x0AF00002, 38, 0, red));
            Hex.True(said.Count == 1 && said[0].Contains($"party 0x{blue:X}"),
                "the half-resolved case names the handle World did not know: " + string.Join(" | ", said));

            // A create World built is not a refusal.
            said.Clear();
            BattlefieldCreation.ArmGmCreateForTests(38, new[] { blue, red }, 10, said.Add);
            creation.TryHandle(owner.Link, null, BattlefieldCreation.BSA_CREATE_LOG,
                T208cCreateLog(0x0AF00003, 38, blue, red));
            Hex.True(said.Count == 0 && BattlefieldCreation.PendingGmTemplate == null,
                "a resolved pair says nothing to the caller and closes the wait");

            // Another template's log request must not consume this command's wait.
            BattlefieldCreation.ArmGmCreateForTests(38, new[] { blue, red }, 10, said.Add);
            creation.TryHandle(owner.Link, null, BattlefieldCreation.BSA_CREATE_LOG,
                T208cCreateLog(0x0AF00004, 37, 0, 0));
            Hex.True(said.Count == 0 && BattlefieldCreation.PendingGmTemplate == 38,
                "a log request for another template is not this command's answer");
        }
        finally { BattlefieldCreation.ResetGmCreateForTests(); }
    }

    /// <summary>
    /// T208c. The three exits that used to send nothing and still say "Enter battleField[id]" -
    /// no continent on the template, no World owning it, owner World not running - now each answer
    /// the GM with the reason, and the success text is only said when the frame really left.
    /// </summary>
    [Test] public static void T208c_every_gm_refusal_answers_the_caller()
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
            // T208c-b: WorldBridge's constructor seeds ServerConfig.xml's WorldServerList into
            // DungeonRouting.Channels (T111, WorldBridge.cs:271 -> WorldServerList.SeedDefault), so
            // on a real deployment the continent map is NOT empty by the time we get here - it maps
            // 115 to world 10 already, which is exactly what step 2 wants to be missing. Clearing
            // AFTER construction is what makes "unclaimed" mean unclaimed instead of "whatever
            // ServerConfig.xml happens to say", and the assertion below keeps it honest.
            DungeonRouting.Channels.Clear();
            using var main = new T192WorldPeer(bridge, 29, 0);
            using var first = new T185Client(defs, opcodes, QuietLog());
            using var second = new T185Client(defs, opcodes, QuietLog());
            T185Environment.SetWorld(bridge);

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
            var args = new[] { "38", "mate" };

            // 1. The template has no continent at all.
            BattleFieldSheet.SetForTest(new[] { new BattleFieldEntry(38, "DeathMatch", 15, 70, 70, 0) });
            BattlefieldCreation.CreateForGm(bridge, first.Session, args);
            Hex.True(T208cSays(first.Frame(), "no continent configured") && main.Available == 0,
                "a template with no continent is refused, not silently dropped");

            // 2. A continent nobody claims.
            BattleFieldSheet.SetForTest(new[] { new BattleFieldEntry(38, "DeathMatch", 15, 70, 70, 115) });
            Hex.True(DungeonRouting.Channels.WorldForContinent(115) == null,
                "the fixture starts with continent 115 unowned - if this fails, something seeded the map");
            BattlefieldCreation.CreateForGm(bridge, first.Session, args);
            Hex.True(T208cSays(first.Frame(), "which no World has claimed") && main.Available == 0,
                "an unclaimed continent is refused by name");

            // 3. The owner World is configured but not running - cap_bg4 10:04:40, before world 10 linked.
            DungeonRouting.Channels.MapContinent(115, 10);
            BattlefieldCreation.CreateForGm(bridge, first.Session, args);
            Hex.True(T208cSays(first.Frame(), "world 10, which is not running") && main.Available == 0,
                "the configured owner being down is refused, with no fallback to the caller's world");
            Hex.True(BattlefieldCreation.PendingGmTemplate == null, "a refused command never arms a wait");

            // 4. Owner up: the frame goes out, the native text is said, and the wait is armed.
            using var owner = new T192WorldPeer(bridge, 56, 10);
            BattlefieldCreation.CreateForGm(bridge, first.Session, args);
            var parties = new[] { PartyWiring.Manager.FindByMember(1)!.Id, PartyWiring.Manager.FindByMember(2)!.Id };
            Hex.Eq(owner.Frame(), T180Frame(BattlefieldCreation.ABS_CREATE,
                BattlefieldCreation.BuildCreate(38, parties, true)), "the create still goes to the template's owner");
            Hex.True(T208cSays(first.Frame(), "Enter battleField[38]")
                && BattlefieldCreation.PendingGmTemplate == 38,
                "only a create that left says the native text, and it stays answerable");
        }
        finally
        {
            BattlefieldCreation.ResetGmCreateForTests();
            T185Environment.SetWorld(null); storeProperty.SetValue(null, previousStore);
            DungeonRouting.ResetForTest(); PartyWiring.ResetForTests();
            if (File.Exists(map)) File.Delete(map);
        }
    }
}
