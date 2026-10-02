// SPDX-License-Identifier: MIT
// Copyright (c) 2026 the TeraSharp contributors

using TeraSharp.Arbiter.Handlers;
using TeraSharp.Arbiter.World;

namespace TeraSharp.Arbiter.Tests;

// ============ T221: Reaper from level 1 - the start position comes from CreateCharData.xml ============

public static partial class Tests
{
    /// <summary>The shipped sheet keeps the soulless InitPos; the v31-feel sheet (InitPos removed)
    /// starts a Reaper at the InitLoc default like every class; no sheet means the built-in.</summary>
    [Test] public static void T221_start_position_comes_from_InitPos_then_the_InitLoc_default()
    {
        var dir = Path.Combine(Path.GetTempPath(), "t221-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            const string loc = "<InitLoc default=\"true\"><Pos continent=\"5\" pos=\"16260,1253,-4410\" dist=\"100\" dir=\"-18\" /></InitLoc>";
            var sheet = Path.Combine(dir, "CreateCharData.xml");
            File.WriteAllText(sheet, "<CreateCharData><Char class=\"warrior\" createdLevel=\"1\" />"
                + "<Char class=\"soulless\" createdLevel=\"50\"><InitPos continent=\"7087\" pos=\"-48077,-52002,642\" dist=\"0\" dir=\"-111\" /></Char>"
                + loc + "</CreateCharData>");
            var shipped = DatasheetLoader.ReadStartPositions(dir)!;
            Hex.True(shipped[CharacterHandlers.SoullessClassId] == CharacterHandlers.SoullessStart, "shipped: soulless keeps its InitPos");
            Hex.True(shipped[0] == CharacterHandlers.DefaultStart, "shipped: warrior at the InitLoc default");

            File.WriteAllText(sheet, "<CreateCharData><Char class=\"soulless\" createdLevel=\"1\" />" + loc + "</CreateCharData>");
            var edited = DatasheetLoader.ReadStartPositions(dir)!;
            Hex.True(edited.All(p => p == CharacterHandlers.DefaultStart), "v31-feel: every class, soulless included, at the default");

            File.Delete(sheet);
            Hex.True(DatasheetLoader.ReadStartPositions(dir) == null, "no sheet: the built-in stays in use");
            Hex.True(CharacterHandlers.BuiltInStartPositions()[CharacterHandlers.SoullessClassId] == CharacterHandlers.SoullessStart,
                "built-in unchanged");
        }
        finally { Directory.Delete(dir, true); }
    }
}
