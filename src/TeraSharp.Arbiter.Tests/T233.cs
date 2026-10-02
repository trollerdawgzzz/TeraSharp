// SPDX-License-Identifier: MIT
// Copyright (c) 2026 the TeraSharp contributors

using System.Text;
using TeraSharp.Arbiter.Handlers;

namespace TeraSharp.Arbiter.Tests;

// =============================================================================================
// T233 - WorldServer's QA command table, and /@help serving it without the dead _helpworld
// forward. The table itself is WorldQaCommandData (545 rows extracted from the binary; see
// status/WORLD-QA-COMMANDS.md for provenance). What is testable here is that the extraction is
// internally consistent, that the search the help command uses behaves as documented, and that
// /@help now answers entirely from local data - no packet leaves for World.
// =============================================================================================
public static partial class Tests
{
    /// <summary>S_SYSTEM_MESSAGE_CUSTOM as T201UtilityEnvironment maps it: len, opcode, ref, UTF-16 + NUL.</summary>
    private static byte[] T233CustomFrame(string text)
    {
        var body = Encoding.Unicode.GetBytes(text + "\0");
        var frame = new byte[6 + body.Length];
        BitConverter.GetBytes((ushort)frame.Length).CopyTo(frame, 0);
        BitConverter.GetBytes((ushort)39244).CopyTo(frame, 2);
        BitConverter.GetBytes((ushort)6).CopyTo(frame, 4);
        body.CopyTo(frame, 6);
        return frame;
    }

    private static string T233WorldLine(WorldQaCommandData.Entry row) =>
        $"{row.Name} {row.Arguments} // {row.Description} [world] &#xa;";

    [Test] public static void T233_world_qa_table_is_the_545_registrations_the_binary_makes()
    {
        var rows = WorldQaCommandData.Rows;
        Hex.True(rows.Length == 545, "every FUN_140114090 registration is present");
        Hex.True(rows.Count(r => r.Type == 4) == 530 && rows.Count(r => r.Type == 1) == 15,
            "the type split is 530 QA plus 15 battlefield/cheat - type 0 (OP) is never registered");
        Hex.True(rows.Count(r => r.Arguments.Length == 0) == 231,
            "231 rows passed the &PTR_1419c2970 no-argument sentinel");
        var keys = rows.Select(r => (Name: r.Name.ToLowerInvariant(), r.Type)).ToList();
        Hex.True(keys.SequenceEqual(keys.OrderBy(k => k.Name, StringComparer.Ordinal).ThenBy(k => k.Type)),
            "rows are ordered by lowercased name (ordinal) then type");
        Hex.True(rows.GroupBy(r => r.Name).Where(g => g.Count() > 1).Select(g => g.Key).SequenceEqual(new[] { "bookmark" }),
            "bookmark is the only name registered twice");
        Hex.True(rows.All(r => r.Name.Length > 0 && !r.Name.Contains('"') && !r.Description.Contains('"') && !r.Arguments.Contains('"')),
            "no row carries a quote, so the verbatim string literals are faithful");

        // The forward T201 used never existed. Neither do the two names the glyph/skill work guessed at.
        foreach (string absent in new[] { "_helpworld", "helpworld", "help", "_help", "perfect_crest", "perfect_skill", "glyph" })
            Hex.True(!rows.Any(r => r.Name.Equals(absent, StringComparison.OrdinalIgnoreCase)),
                "WorldServer does not register " + absent);

        var crestAll = rows.Single(r => r.Name == "crest_all");
        Hex.True(crestAll.Type == 4 && crestAll.Arguments.Length == 0 && crestAll.Description == "문장 모두 습득",
            "crest_all is the learn-every-glyph command");
        var perfectLevel = rows.Single(r => r.Name == "perfect_level");
        Hex.True(perfectLevel.Type == 4 && perfectLevel.Arguments == "새로운레벨"
            && perfectLevel.Description == "레벨 변경+스킬습득",
            "perfect_level is the level-change-plus-learn-skills command");
    }

    [Test] public static void T233_world_catalogue_search_matches_names_loosely_and_korean_text_exactly()
    {
        Hex.True(WorldQaCommandData.Search("").Count() == 545 && WorldQaCommandData.Search(null!).Count() == 545,
            "an empty or absent keyword matches the whole table");
        Hex.True(WorldQaCommandData.Search("crest").Select(r => r.Name)
            .SequenceEqual(new[] { "clear_all_crest", "crest", "crest_all", "crest_window" }),
            "crest is the glyph family and there are exactly four of them");
        Hex.True(WorldQaCommandData.Search("CREST_ALL").Single().Name == "crest_all", "name matching ignores case");
        Hex.True(WorldQaCommandData.Search("문장 모두 습득").Single().Name == "crest_all",
            "the retail usage text is searchable");
        Hex.True(WorldQaCommandData.Search("새로운레벨").Select(r => r.Name)
            .SequenceEqual(new[] { "level", "perfect_level" }), "argument hints are searchable too");
        Hex.True(WorldQaCommandData.Search("perfect_skill").Single().Name == "perfect_skillPolishing",
            "perfect_skill only surfaces as a prefix of perfect_skillPolishing - it is not itself a command");
        Hex.True(!WorldQaCommandData.Search("nosuchcommand").Any(), "an unknown term matches nothing rather than throwing");
    }

    [Test] public static void T233_help_answers_from_both_catalogues_and_forwards_nothing_to_world()
    {
        using var e = new T201UtilityEnvironment();
        T181WithOperators(null, () =>
        {
            e.Run("help crest");
            Hex.True(e.Caller.Available == 0 && e.Main.Available == 0 && e.Dungeon.Available == 0,
                "help is still behind the operator gate");
        });
        T181WithOperators("utility-op", () =>
        {
            // "crest" matches no row in the Arbiter's own catalogue, so the answer is the heading
            // plus World's four crest rows - byte for byte, in table order, tagged [world].
            e.Run("help crest");
            Hex.Eq(e.Caller.Frame(), T233CustomFrame("*****Help search Result***** &#xa;"), "help heading");
            foreach (var row in WorldQaCommandData.Search("crest"))
                Hex.Eq(e.Caller.Frame(), T233CustomFrame(T233WorldLine(row)), "world row " + row.Name);
            Hex.True(e.Caller.Available == 0, "no further rows for an Arbiter-less keyword");
            Hex.True(e.Main.Available == 0 && e.Dungeon.Available == 0,
                "T233: _helpworld is not a WorldServer command, so help no longer forwards anything");

            // The empty keyword matches all 545 World rows; the answer is capped and summarised.
            Hex.True(WorldQaCommandData.Rows.Length > QaUtilityCommands.WorldHelpRowLimit,
                "the cap is below the table size, so the empty keyword exercises it");
            e.Run("help");
            Hex.Eq(e.Caller.Frame(), T233CustomFrame("*****Help search Result***** &#xa;"), "help heading, empty keyword");
            int native = QaCommandHelpData.Rows.Count(r => !r.Name.StartsWith('_'));
            for (int i = 0; i < native; i++) e.Caller.Frame();
            foreach (var row in WorldQaCommandData.Rows.Take(QaUtilityCommands.WorldHelpRowLimit))
                Hex.Eq(e.Caller.Frame(), T233CustomFrame(T233WorldLine(row)), "capped world row " + row.Name);
            Hex.Eq(e.Caller.Frame(), T233CustomFrame(
                $"... and {WorldQaCommandData.Rows.Length - QaUtilityCommands.WorldHelpRowLimit} more WorldServer command(s) - narrow the search &#xa;"),
                "the remaining World rows are summarised, not sent");
            Hex.True(e.Caller.Available == 0 && e.Main.Available == 0 && e.Dungeon.Available == 0,
                "the whole answer came from local catalogues");
        });
    }
}
