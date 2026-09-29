// SPDX-License-Identifier: MIT
// Copyright (c) 2026 the TeraSharp contributors

using TeraSharp.Arbiter.Game;
using TeraSharp.Arbiter.Handlers;

namespace TeraSharp.Arbiter.Tests;

// =============================================================================================
// T218 - the GM's admin level in AS_ENTER_WORLD.
//
// World's console prints SpawnComplete caludesucks(9) AdminLevel[0], but the Arbiter is not the
// one getting it wrong. cap_makeitem3's AS_ENTER_WORLD for that very session carries
//
//     payload+111 = 05 00 00 00
//
// and the Arbiter's C_ADMIN line for the same session resolves 5 as well, so both sides of the
// Arbiter agree. Retail puts its own GM's level at the same offset: cap_final has 0x138E frames
// for player 1 and player 2 with 01 at payload+111, on a stack whose GM was adminLevel 1 (T46).
//
// The offset is right, the value is right, and WorldEntry.EnterWorld already passes
// GmCommandHandlers.LevelOf(s, Program.Store) into it (WorldEntry.cs:51). What prints 0 is on
// World's side of the wire - its binding, or something it overwrites after reading. T46 already
// recorded that World only ever LOGS this field, so a 0 in that line does not by itself explain a
// refused makeitem. status/T218-ADMINLEVEL.md.
// =============================================================================================
public static partial class Tests
{
    /// <summary>
    /// T218. The builder puts the level where retail puts it, and a level-5 session's payload
    /// reads back 5 at 111 - the check the brief asked for.
    /// </summary>
    [Test] public static void T218_admin_level_five_lands_at_payload_111()
    {
        var chr = new FakeCharacter { Id = 9, Name = "caludesucks" };
        var p = WorldEntry.BuildEnterWorldPayload(0x80000AF00001UL, chr, tunnelKey: 0, adminLevel: 5);
        Hex.True(p.Length == 183, "still the 183-byte payload");
        Hex.True(BitConverter.ToUInt32(p, 111) == 5,
            $"payload+111 is the AdminLevel (T46), got {BitConverter.ToUInt32(p, 111)}");

        // The two fields either side, so a shifted write is caught rather than passing by luck.
        Hex.True(BitConverter.ToUInt32(p, 103) == 6, "payload+103 SubscriptionFeeType is still 6");
        Hex.True(BitConverter.ToUInt32(p, 107) == 0, "payload+107 AccountRestrictionLevel is still 0");
        Hex.True(p[115] == 0 && p[116] == 0, "payload+115/116 TutorialUser / LeaveParty still 0");

        Hex.True(BitConverter.ToUInt32(
            WorldEntry.BuildEnterWorldPayload(0x80000AF00001UL, chr, tunnelKey: 0, adminLevel: 0), 111) == 0,
            "and a non-GM session writes 0 there, so the field is the level and not a constant");
    }

    /// <summary>
    /// T218. The captured bytes, ours and retail's, at the same offset. cap_makeitem3 is the
    /// session whose console said AdminLevel[0].
    /// </summary>
    [Test] public static void T218_the_captured_enter_world_frames_agree_on_the_offset()
    {
        // cap_makeitem3, AS_ENTER_WORLD for player 9, payload 103..120.
        byte[] ours = Hex.B("06 00 00 00 00 00 00 00 05 00 00 00 00 00 00 00 00 00");
        // cap_final, AS_ENTER_WORLD for player 1 on the retail stack, payload 103..120.
        byte[] retail = Hex.B("06 00 00 00 00 00 00 00 01 00 00 00 00 00 00 00 00 00");

        Hex.True(BitConverter.ToUInt32(ours, 111 - 103) == 5,
            "cap_makeitem3: the Arbiter DID send AdminLevel 5 for the session World logged as 0");
        Hex.True(BitConverter.ToUInt32(retail, 111 - 103) == 1,
            "cap_final: retail puts its GM's level 1 at the same offset");
        for (int i = 0; i < ours.Length; i++)
            if (i != 111 - 103)
                Hex.True(ours[i] == retail[i],
                    "and every other byte of that window matches retail, so the field is not shifted");
    }

    /// <summary>
    /// T218. The wiring itself: EnterWorld must pass the resolved level, not a literal. This is the
    /// line that would silently become 0 again if somebody dropped the argument.
    /// </summary>
    [Test] public static void T218_enter_world_passes_the_resolved_level()
    {
        string? path = FindRepoFile(Path.Combine("src", "TeraSharp.Arbiter", "Handlers", "WorldEntry.cs"));
        if (path == null) { Skip.Because("WorldEntry.cs not in this tree"); return; }
        string source = File.ReadAllText(path);
        Hex.True(source.Contains("GmCommandHandlers.LevelOf(s, Program.Store))"),
            "EnterWorld builds the payload with the resolved admin level");

        // And the resolver itself: an account listed by name gets the GM level, which is what the
        // launcher's accountDBID-as-name login produces (TERASHARP_GM_ACCOUNTS=\"1\", account '1').
        Hex.True(GmCommandHandlers.LevelOf("1", "caludesucks", 0, "1") == GmAccounts.GmAdminLevel,
            "the listed accountDBID gets the GM level even with nothing stored on the row");
        Hex.True(GmCommandHandlers.LevelOf("1", "caludesucks", 0, "2,3") == 0,
            "and an unlisted one does not");
    }
}
