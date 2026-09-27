// SPDX-License-Identifier: MIT
// Copyright (c) 2026 the TeraSharp contributors

using System.Text.Json;
using TeraSharp.Arbiter.Persistence;

namespace TeraSharp.Arbiter.World;

/// <summary>
/// T211. The profile's <b>PvP Record</b> tab, aggregated out of what T199 already files.
///
/// <para>Every finished battleground writes one <c>game_log</c> row per participant -
/// <see cref="BattlegroundResults.FileResults"/>, category <c>pvp</c>, action
/// <c>battleground.result</c> - whose <c>extra</c> JSON carries the whole native
/// <see cref="BattlegroundResults.Result"/> record. That is the only place a match outcome is
/// kept, so the tab is a fold over those rows rather than a table of its own.</para>
///
/// <para><b>What Outcome means</b> (decompile): <c>User::ProcessBattleFieldResult(int,int)</c>,
/// Arb_part_029.c:15355-15360 and :15395. It returns immediately when the outcome is <c>2</c>,
/// then computes <c>isWin = (outcome == 1)</c> and takes <c>User::GetBFWinCount</c> for a win and
/// <c>User::GetBFLoseCount</c> otherwise. So <b>1 is a win, 2 is a draw the native record ignores,
/// anything else is a loss</b>.</para>
/// </summary>
public static class BattlefieldRecords
{
    /// <summary>Arb_part_029.c:15359 - <c>isWin = (outcome == 1)</c>.</summary>
    public const int OutcomeWin = 1;

    /// <summary>Arb_part_029.c:15355 - the native record writer returns on this one.</summary>
    public const int OutcomeDraw = 2;

    /// <summary>The <c>game_log</c> action <see cref="BattlegroundResults.FileResults"/> writes.</summary>
    public const string ResultAction = "battleground.result";

    /// <summary>
    /// OURS: how far back the fold reads. 20 pages of 200 is 4000 matches per character, which is
    /// more than any real profile, and it bounds the query on a log that only ever grows.
    /// </summary>
    public const int MaxPages = 20;

    /// <summary>One battleground's totals for one character - a row of the tab.</summary>
    public readonly record struct Record(int BfId, int BfType, int Matches, int Wins, int Losses,
        int Draws, int Kills, int Deaths, int Assists, int Captures, int Destroys, int GradeScore);

    /// <summary>The <c>extra</c> JSON <see cref="BattlegroundResults.FileResults"/> writes.</summary>
    public sealed record ResultEnvelope(BattlegroundResults.Result? Result);

    /// <summary>
    /// Every battleground this character has a filed result for, oldest battleground id first.
    /// An unparseable <c>extra</c> still contributes the columns the row itself carries
    /// (<c>template_id</c> is the battlefield id and <c>amount</c> the kills), so a log written by
    /// an older build is counted rather than dropped.
    /// </summary>
    public static List<Record> For(CharacterStore? store, int characterId)
    {
        var totals = new Dictionary<(int BfId, int BfType), Record>();
        if (store == null || characterId <= 0) return new List<Record>();

        for (int page = 0; page < MaxPages; page++)
        {
            var rows = store.QueryGameLog(characterId: characterId, category: GameLogPackets.CategoryPvp,
                action: ResultAction, page: page, pageSize: CharacterStore.GameLogMaxPageSize);
            foreach (var row in rows)
            {
                if (!string.Equals(row.Action, ResultAction, StringComparison.Ordinal)) continue;
                BattlegroundResults.Result? result = null;
                if (row.Extra.Length > 0)
                {
                    try { result = JsonSerializer.Deserialize<ResultEnvelope>(row.Extra)?.Result; }
                    catch (JsonException) { result = null; }
                }

                int bfId = result?.BattleFieldId ?? row.TemplateId;
                int bfType = result?.BattleFieldType ?? 0;
                int outcome = result?.Outcome ?? -1;
                var key = (bfId, bfType);
                totals.TryGetValue(key, out var t);
                totals[key] = new Record(bfId, bfType,
                    t.Matches + 1,
                    t.Wins + (outcome == OutcomeWin ? 1 : 0),
                    t.Losses + (outcome != OutcomeWin && outcome != OutcomeDraw ? 1 : 0),
                    t.Draws + (outcome == OutcomeDraw ? 1 : 0),
                    t.Kills + (result?.Kills ?? (int)row.Amount),
                    t.Deaths + (result?.Deaths ?? 0),
                    t.Assists + (result?.Assists ?? 0),
                    t.Captures + (result?.Captures ?? 0),
                    t.Destroys + (result?.Destroys ?? 0),
                    Math.Max(t.GradeScore, result?.NativeGradePoint ?? 0));
            }
            if (rows.Count < CharacterStore.GameLogMaxPageSize) break;
        }

        var list = new List<Record>(totals.Values);
        list.Sort((a, b) => a.BfId != b.BfId ? a.BfId.CompareTo(b.BfId) : a.BfType.CompareTo(b.BfType));
        return list;
    }
}
