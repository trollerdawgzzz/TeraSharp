// SPDX-License-Identifier: MIT
// Copyright (c) 2026 the TeraSharp contributors

using TeraSharp.Arbiter.Game;
using TeraSharp.Arbiter.Handlers;
using TeraSharp.Arbiter.Protocol;

namespace TeraSharp.Arbiter.Tests;

// =============================================================================================
// T191d - the WASD movement guide on every login, and the byte behind it.
//
// The prompt is not tutorial state and not a setting blob. Everything the brief listed was
// checked against the captures first and every one of them matches retail exactly:
//
//   S_SIMPLE_TIP_REPEAT_CHECK   identical. The client asks for tips 1, 2, 0x23, 0x27, 0x29 once
//                               per client process and the answer is [tipId][01] in all three
//                               captures - ours (cap_wasd_client 1260..1299), a played character
//                               (cap_2man_b_client1 525..572) and the real server
//                               (classic_live 5669..5699). Same ids, same order, same bytes.
//   S_LOAD_CLIENT_USER_SETTING  round-trips. cap_wasd_client saves 1133 B at 1423 and is served
//                               the same sha at 1812; saves again at 2774 and is served that at
//                               3271. T191c persistence is correct.
//   S_LOGIN                     carries no tutorial or first-login field in v100 (S_LOGIN.14.def).
//                               visible = 1 and isSecondCharacter = 0 in ours and in retail.
//   S_USER_STATUS               17 B, status 0, byte-identical in all three.
//   AS_ENTER_WORLD              TutorialUser (payload 115) is 0 - Handlers/WorldEntry.cs:236.
//
// The difference is one frame that never arrives. cap_final_gm_client2 - the real Arbiter with a
// GM - sends S_ADMIN_GM_SKILL (0x64BE) [i32 skill=0 Invisible][u8 enabled] once per world entry,
// between S_FESTIVAL_LIST and S_LOAD_TOPO:
//
//     98  S_FESTIVAL_LIST     08 00 4E 8F 00 00 00 00
//     99  S_ADMIN_GM_SKILL    09 00 BE 64 00 00 00 00 01      <- the switch
//     100 S_LOAD_TOPO         15 00 28 E8 05 00 00 00 ...
//   2444/2445/2446 the same three frames on the second world entry.
//
// cap_wasd_client is an operator (S_LOGIN_ARBITER.status 0x21) with three world entries and has
// no S_ADMIN_GM_SKILL at all, so the client's GM-skill switch is never initialised and the guide
// stays up. The panel's Invisible toggle (cap_final_gm_client2 542 -> 546) is the first one the
// client ever sees, which is why pressing it clears the prompt immediately. The ordinary
// characters in cap_2man_b_client1/2 are status 0x1F, never get the switch in retail either, and
// show no prompt - they are the control, not the counter-example.
//
// T152 removed the push because OUR value was a guess ("invisible" while World had the GM
// visible, so the panel's OFF vaporized them). The frame is back with World's own value.
// =============================================================================================
public static partial class Tests
{
    /// <summary>cap_final_gm_client2 frame 99 / 2445 - the enter-world switch, invisibility ON.</summary>
    static readonly byte[] T191dRetailPush = Hex.B("09 00 BE 64 00 00 00 00 01");

    /// <summary>cap_final_gm_client2 frame 546 - the panel toggle's answer, invisibility OFF.</summary>
    static readonly byte[] T191dRetailToggleOff = Hex.B("09 00 BE 64 00 00 00 00 00");

    /// <summary>cap_final_gm_client2 frame 100 - the frame the push has to get in front of.</summary>
    static readonly byte[] T191dRetailTopo =
        Hex.B("15 00 28 E8 05 00 00 00 D3 50 83 46 A3 0A 9D 44 00 00 00 00 00");

    /// <summary>
    /// The builder reproduces both captured frames byte for byte, and the constants it is driven
    /// by are the ones the def names (skill 0 = Invisible).
    /// </summary>
    [Test] public static void T191d_the_gm_skill_switch_matches_the_captured_frames()
    {
        Hex.Eq(ArbiterClientHandlers.BuildAdminGmSkill(ArbiterClientHandlers.GmSkillInvisible, on: true),
            T191dRetailPush, "cap_final_gm_client2 99: enter-world push, invisibility ON");
        Hex.Eq(ArbiterClientHandlers.BuildAdminGmSkill(ArbiterClientHandlers.GmSkillInvisible, on: false),
            T191dRetailToggleOff, "cap_final_gm_client2 546: the panel toggle's answer, OFF");
        Hex.True(ArbiterClientHandlers.IsLoadTopo(T191dRetailTopo)
                 && ArbiterClientHandlers.AdminGmSkillPrecedes == "S_LOAD_TOPO"
                 && ArbiterClientHandlers.AdminGmSkillFollows == "S_FESTIVAL_LIST",
            "the anchor is retail's own slot: after S_FESTIVAL_LIST, before S_LOAD_TOPO");
    }

    /// <summary>
    /// The regression itself: an operator's tunnelled S_LOAD_TOPO now carries the switch in front
    /// of it, once per world entry, with World's value - and an ordinary character gets nothing.
    /// </summary>
    [Test] public static void T191d_an_operator_gets_the_switch_before_the_tunnelled_topo()
    {
        var definitions = new DefinitionRegistry(QuietLog());
        var opcodes = OpcodeTable.LoadFromFile(T191dOpcodeMap(), "376012");
        string? previousGm = Environment.GetEnvironmentVariable(GmAccounts.EnvVariable);
        try
        {
            using var client = new T185Client(definitions, opcodes, QuietLog());
            client.Session.PlayerId = 2;
            client.Session.SelectedCharacter = new FakeCharacter { Id = 2, Name = "testt" };
            client.Session.Account.Name = "t191d-operator";
            Environment.SetEnvironmentVariable(GmAccounts.EnvVariable, client.Session.Account.Name);
            TerasConfig.ResetForTests();
            Hex.True(ArbiterClientHandlers.OperatorGetsGmSkillPush(
                GmCommandHandlers.LevelOf(client.Session, null)), "the session is an operator");

            // World has not vaporized: the switch says visible, which is the truth T152 needs.
            ArbiterClientHandlers.ResetGmSkillPush(2);
            ArbiterClientHandlers.DeliverTunnelled(client.Session, (byte[])T191dRetailTopo.Clone());
            Hex.Eq(client.Frame(), T191dRetailToggleOff,
                "the switch goes out FIRST, carrying World's own visibility - never a guess");
            Hex.Eq(client.Frame(), T191dRetailTopo, "then S_LOAD_TOPO, unchanged");

            // A zone change inside the same world entry must not re-arm GM skill (T121).
            ArbiterClientHandlers.DeliverTunnelled(client.Session, (byte[])T191dRetailTopo.Clone());
            Hex.Eq(client.Frame(), T191dRetailTopo, "second topo load in the same entry: topo only");
            Hex.True(client.Available == 0, "and nothing else - the push is once per world entry");

            // A relog: SDB_USER_ENTERWORLD re-arms it, and this time World HAS vaporized, so the
            // frame is retail's 99 byte for byte.
            ArbiterClientHandlers.ResetGmSkillPush(2);
            ArbiterClientHandlers.SetGmInvisible(2, true);
            ArbiterClientHandlers.DeliverTunnelled(client.Session, (byte[])T191dRetailTopo.Clone());
            Hex.Eq(client.Frame(), T191dRetailPush,
                "vaporized on World -> the captured enter-world frame, 01");
            Hex.Eq(client.Frame(), T191dRetailTopo, "still followed by the topo");

            // An ordinary character - cap_2man_b_client1/2, S_LOGIN_ARBITER.status 0x1F.
            Environment.SetEnvironmentVariable(GmAccounts.EnvVariable, "");
            TerasConfig.ResetForTests();
            ArbiterClientHandlers.ResetGmSkillPush(2);
            ArbiterClientHandlers.DeliverTunnelled(client.Session, (byte[])T191dRetailTopo.Clone());
            Hex.Eq(client.Frame(), T191dRetailTopo, "a non-operator gets the topo");
            Hex.True(client.Available == 0, "and no GM-skill switch, exactly as retail");
        }
        finally
        {
            Environment.SetEnvironmentVariable(GmAccounts.EnvVariable, previousGm);
            TerasConfig.ResetForTests();
            ArbiterClientHandlers.ResetGmSkillPush(2);
        }
    }

    /// <summary>A one-opcode map: DeliverTunnelled reads the wire bytes, not the name table.</summary>
    static string T191dOpcodeMap()
    {
        string path = Path.Combine(Path.GetTempPath(), "t191d-opcodes-" + Guid.NewGuid() + ".json");
        File.WriteAllText(path, "{\"maps\":{\"376012\":{}}}");
        return path;
    }
}
