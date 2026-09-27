// SPDX-License-Identifier: MIT
// Copyright (c) 2026 the TeraSharp contributors

using TeraSharp.Arbiter.Handlers;
using TeraSharp.Arbiter.Persistence;
using TeraSharp.Arbiter.World;

namespace TeraSharp.Arbiter.Tests;

// ================== T211: the profile's PvP Record and Dungeon History tabs ==================
//
// Both were empty because nothing answered them with anything.
//
//   PvP Record     C_VIEW_BATTLE_FIELD_RESULT (0xEC3D) was registered to OnAcceptSilently, so the
//                  tab opened onto silence. The request shape comes from
//                  Handler_C_VIEW_BATTLE_FIELD_RESULT (Arb_part_041.c:14181-14232): min length 6,
//                  param_2[2] is a string ref holding a CHARACTER NAME. The reply layout comes
//                  from the dumper FUN_140307ba0 (Arb_part_024.c:10014-10479), which names all
//                  seventeen fields and reads each from a fixed offset.
//   Dungeon        C_DUNGEON_RANK_RECORD_LIST was answered, but with a hardcoded empty board even
//   History        though T167 files dungeon_rank_records on every SDB_UPDATE_DUNGEON_RANK_RECORD.
//
// Nothing here is byte-pinned to a capture because no capture in the tree opens either tab:
// cap_bg1/cap_bg4 contain no 0xEC3D, 0xE459, 0xF679 or 0xC9E3 at all. The layouts are
// decompile-marked, and the only captured member of the family, C_DUNGEON_CLEAR_COUNT_LIST
// (cap_bg1_client1 586/588), already worked and is unchanged.
public static partial class Tests
{
    /// <summary>
    /// One BSA_END_BATTLE_FIELD_RESULT_LIST payload as World sends it:
    /// <c>[u32 count][u32 firstOffset][i32 battleFieldType]</c> then 44-byte elements of
    /// <c>[u32 self][u32 next][i32 outcome][i32 userDbId][i32 kills][i32 deaths][i32 assists]
    /// [i32 captures][i32 gradePoint][i32 destroys][i32 battleFieldId]</c>.
    /// Arb_part_061.c:18169-18404; field names Arb_part_013.c:5001-5253.
    /// </summary>
    private static byte[] T211ResultList(int battleFieldType,
        params (int Outcome, int UserDbId, int Kills, int Deaths, int Assists, int Captures,
                int GradePoint, int Destroys, int BattleFieldId)[] rows)
    {
        const int Stride = 44, First = 12;
        var p = new byte[First + rows.Length * Stride];
        BitConverter.GetBytes(rows.Length).CopyTo(p, 0);
        BitConverter.GetBytes(rows.Length == 0 ? 0 : First + 6).CopyTo(p, 4);
        BitConverter.GetBytes(battleFieldType).CopyTo(p, 8);
        for (int i = 0; i < rows.Length; i++)
        {
            int at = First + i * Stride;
            BitConverter.GetBytes(at + 6).CopyTo(p, at);
            BitConverter.GetBytes(i + 1 < rows.Length ? at + Stride + 6 : 0).CopyTo(p, at + 4);
            BitConverter.GetBytes(rows[i].Outcome).CopyTo(p, at + 8);
            BitConverter.GetBytes(rows[i].UserDbId).CopyTo(p, at + 12);
            BitConverter.GetBytes(rows[i].Kills).CopyTo(p, at + 16);
            BitConverter.GetBytes(rows[i].Deaths).CopyTo(p, at + 20);
            BitConverter.GetBytes(rows[i].Assists).CopyTo(p, at + 24);
            BitConverter.GetBytes(rows[i].Captures).CopyTo(p, at + 28);
            BitConverter.GetBytes(rows[i].GradePoint).CopyTo(p, at + 32);
            BitConverter.GetBytes(rows[i].Destroys).CopyTo(p, at + 36);
            BitConverter.GetBytes(rows[i].BattleFieldId).CopyTo(p, at + 40);
        }
        return p;
    }

    /// <summary>
    /// T211. S_VIEW_BATTLE_FIELD_RESULT sits at exactly the offsets the dumper reads: the array
    /// head and the two scalars in the 16-byte head, then 64-byte elements whose fifteen i32 are
    /// in the dumper's order.
    /// </summary>
    [Test] public static void T211_view_battle_field_result_is_the_dumpers_layout()
    {
        var empty = ArbiterClientHandlers.BuildViewBattleFieldResult(7, 0);
        Hex.True(empty.Length == 16 && BitConverter.ToUInt16(empty, 0) == 16
            && BitConverter.ToUInt16(empty, 2) == ArbiterClientHandlers.S_VIEW_BATTLE_FIELD_RESULT
            && BitConverter.ToUInt16(empty, 4) == 0 && BitConverter.ToUInt16(empty, 6) == 0
            && BitConverter.ToInt32(empty, 8) == 7 && BitConverter.ToInt32(empty, 12) == 0,
            "the empty form is the 16-byte head the dumper's 0xf < len guard accepts");

        var rows = new[]
        {
            new ArbiterClientHandlers.BattleFieldResultRow(38, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15),
            new ArbiterClientHandlers.BattleFieldResultRow(37, 1, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31, 32, 33),
        };
        var p = ArbiterClientHandlers.BuildViewBattleFieldResult(1234, 5, rows);

        Hex.True(p.Length == 16 + 2 * 64 && BitConverter.ToUInt16(p, 0) == p.Length
            && BitConverter.ToUInt16(p, 4) == 2 && BitConverter.ToUInt16(p, 6) == 16
            && BitConverter.ToInt32(p, 8) == 1234 && BitConverter.ToInt32(p, 12) == 5,
            $"two rows is 16 + 2 x 64 with the head filled, not {p.Length}");
        Hex.True(BitConverter.ToUInt16(p, 16) == 16 && BitConverter.ToUInt16(p, 18) == 80
            && BitConverter.ToUInt16(p, 80) == 80 && BitConverter.ToUInt16(p, 82) == 0,
            "the element chain is self/next with a zero terminator");

        // Every field at the offset FUN_140307ba0 reads it from.
        int[] first = { 38, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15 };
        for (int f = 0; f < first.Length; f++)
            Hex.True(BitConverter.ToInt32(p, 16 + 4 + f * 4) == first[f],
                $"element field {f} belongs at +{4 + f * 4}");
        Hex.True(BitConverter.ToInt32(p, 80 + 4) == 37 && BitConverter.ToInt32(p, 80 + 60) == 33,
            "the second element starts a fresh 64-byte stride");
    }

    /// <summary>
    /// T211. The tab's numbers are a fold over the game_log rows T199 already files: one row per
    /// participant per finished match. Outcome 1 is a win and 2 a draw
    /// (User::ProcessBattleFieldResult, Arb_part_029.c:15355/15359), so two matches on one
    /// battleground come out 1-1-0 with the per-match counters summed and the grade kept at its
    /// best.
    /// </summary>
    [Test] public static void T211_pvp_record_folds_the_filed_match_results()
    {
        using var store = StoreWithTwoAccounts();

        Hex.True(BattlefieldRecords.For(store, 1).Count == 0, "a character with no match has no row");

        // Match one on battleground 38: character 1 wins, character 2 loses.
        Hex.True(BattlegroundResults.FileResults(store, T211ResultList(2,
            (BattlefieldRecords.OutcomeWin, 1, 7, 2, 3, 1, 900, 0, 38),
            (0, 2, 1, 9, 0, 0, 100, 0, 38))), "the end-result list is one-way and always accepted");
        // Match two, same battleground: character 1 loses this one, and a draw on battleground 37.
        BattlegroundResults.FileResults(store, T211ResultList(2, (0, 1, 2, 5, 1, 0, 400, 3, 38)));
        BattlegroundResults.FileResults(store, T211ResultList(1,
            (BattlefieldRecords.OutcomeDraw, 1, 4, 4, 0, 2, 700, 1, 37)));

        var mine = BattlefieldRecords.For(store, 1);
        Hex.True(mine.Count == 2 && mine[0].BfId == 37 && mine[1].BfId == 38,
            "one row per battleground, lowest id first");

        var bg38 = mine[1];
        Hex.True(bg38.BfType == 2 && bg38.Matches == 2 && bg38.Wins == 1 && bg38.Losses == 1
            && bg38.Draws == 0 && bg38.Kills == 9 && bg38.Deaths == 7 && bg38.Assists == 4
            && bg38.Captures == 1 && bg38.Destroys == 3 && bg38.GradeScore == 900,
            $"battleground 38 folds to 1-1-0 with summed counters, got {bg38}");
        Hex.True(mine[0].Draws == 1 && mine[0].Wins == 0 && mine[0].Losses == 0,
            "outcome 2 counts as a draw and as neither a win nor a loss");

        var theirs = BattlefieldRecords.For(store, 2);
        Hex.True(theirs.Count == 1 && theirs[0].Losses == 1 && theirs[0].Wins == 0
            && theirs[0].Deaths == 9, "the loser's own row is separate and is a loss");

        // The rating columns are T138c's, and the rank is a position on that same board.
        store.SetBgRating(1, 40); store.SetBgRating(2, 90);
        Hex.True(store.GetBgRatingRank(2) == 1 && store.GetBgRatingRank(1) == 2,
            "the higher rating is rank 1");
        Hex.True(store.GetBgRatingRank(99) == 0, "a character with no rating has no rank");
    }

    /// <summary>
    /// T211. The dungeon board is T167's dungeon_rank_records: highest point first, then fastest
    /// time, ranks 1-based, and the caller's own row is the one the header renders.
    /// </summary>
    [Test] public static void T211_dungeon_record_board_is_the_stored_rank_rows()
    {
        using var store = StoreWithTwoAccounts();
        Hex.True(store.GetDungeonRankBoard(9781, 1).Count == 0, "no runs filed, no board");

        store.RecordDungeonRank(new CharacterStore.DungeonRankRow(1, 9781, 1, 1200, 300, 111, true, 10, 20, 30, "t30_1"));
        store.RecordDungeonRank(new CharacterStore.DungeonRankRow(2, 9781, 1, 1500, 280, 222, true, 11, 21, 31, "t30_2"));
        store.RecordDungeonRank(new CharacterStore.DungeonRankRow(1, 9781, 2, 9999, 100, 333, true, 0, 0, 0, ""));

        var board = store.GetDungeonRankBoard(9781, 1);
        Hex.True(board.Count == 2 && board[0].CharacterId == 2 && board[1].CharacterId == 1
            && board[0].TopPoint == 1500 && board[1].TopPoint == 1200,
            "one season only, highest point first");
        Hex.True(board[0].Name == "t30_2" && board[0].GuildName == "" && board[0].Class == 12
            && board[0].Race == 4 && board[0].Gender == 1 && board[0].TopTime == 280
            && board[0].PlayDate == 222, "the identity columns come from the characters row");

        var rows = new[]
        {
            new ArbiterClientHandlers.DungeonRankRow(1, board[0].Name, board[0].GuildName,
                board[0].Class, board[0].Race, board[0].Gender, board[0].TopPoint, board[0].PlayDate),
            new ArbiterClientHandlers.DungeonRankRow(2, board[1].Name, board[1].GuildName,
                board[1].Class, board[1].Race, board[1].Gender, board[1].TopPoint, board[1].PlayDate),
        };
        var framed = ArbiterClientHandlers.BuildDungeonRankRecordList(9781, 1, 0,
            dctType: 0, myRecord: rows[1], lastSortTime: 222, rows: rows);
        Hex.True(BitConverter.ToUInt16(framed, 2) == ArbiterClientHandlers.S_DUNGEON_RANK_RECORD_LIST
            && BitConverter.ToUInt16(framed, 4) == 2 && framed[0x1C] == 1
            && BitConverter.ToInt32(framed, 0x1D) == 2 && BitConverter.ToInt32(framed, 0x2D) == 1200
            && BitConverter.ToInt64(framed, 0x39) == 222,
            "HasMyRecord is set, and the head carries my rank, my record and the sort time");
    }
}
