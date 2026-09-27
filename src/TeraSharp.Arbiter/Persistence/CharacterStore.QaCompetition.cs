// SPDX-License-Identifier: MIT
// Copyright (c) 2026 the TeraSharp contributors

namespace TeraSharp.Arbiter.Persistence;

public sealed partial class CharacterStore
{
    private void EnsureCompetitionTable() => Exec(@"CREATE TABLE IF NOT EXISTS dungeon_competition_result(
 season INTEGER NOT NULL, continent INTEGER NOT NULL, owner INTEGER NOT NULL, class INTEGER NOT NULL,
 stage INTEGER NOT NULL, time INTEGER NOT NULL, PRIMARY KEY(season,continent,owner));");
    public sealed record CompetitionUpdate(bool Improved, int OldStage, long OldTime);

    /// <summary>Native spUpdateDungeonCompetitionResult, SQL20324; higher stage, then lower time wins.</summary>
    public CompetitionUpdate? UpdateCompetitionResult(int owner, int continent, int season, int characterClass, int stage, long time)
    {
        if (NoSuchOwner(nameof(UpdateCompetitionResult), owner)) return null;
        lock (_lock)
        {
            EnsureCompetitionTable(); using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT stage,time FROM dungeon_competition_result WHERE season=$s AND continent=$c AND owner=$o";
            cmd.Parameters.AddWithValue("$s", season); cmd.Parameters.AddWithValue("$c", continent); cmd.Parameters.AddWithValue("$o", owner);
            int oldStage = 0; long oldTime = 0; bool found;
            using (var r = cmd.ExecuteReader()) { found = r.Read(); if (found) { oldStage = r.GetInt32(0); oldTime = r.GetInt64(1); } }
            bool better = !found || stage > oldStage || stage == oldStage && time < oldTime;
            if (better)
            {
                cmd.CommandText = "INSERT INTO dungeon_competition_result VALUES($s,$c,$o,$class,$stage,$time) ON CONFLICT(season,continent,owner) DO UPDATE SET class=excluded.class,stage=excluded.stage,time=excluded.time";
                cmd.Parameters.AddWithValue("$class", characterClass); cmd.Parameters.AddWithValue("$stage", stage); cmd.Parameters.AddWithValue("$time", time); cmd.ExecuteNonQuery();
            }
            return new(better, oldStage, oldTime);
        }
    }

    public bool DeleteCompetitionResult(int owner, int continent, int season)
    {
        lock (_lock)
        {
            EnsureCompetitionTable(); using var cmd = _db.CreateCommand();
            cmd.CommandText = "DELETE FROM dungeon_competition_result WHERE season=$s AND continent=$c AND owner=$o";
            cmd.Parameters.AddWithValue("$s", season); cmd.Parameters.AddWithValue("$c", continent); cmd.Parameters.AddWithValue("$o", owner);
            return cmd.ExecuteNonQuery() != 0;
        }
    }

    public List<RankingScore> GetCompetitionScores(int continent, int season)
    {
        lock (_lock)
        {
            EnsureCompetitionTable(); using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT r.owner,c.name,r.class,r.stage,r.time FROM dungeon_competition_result r JOIN characters c ON c.id=r.owner WHERE r.season=$s AND r.continent=$d AND c.deleted_at=0 ORDER BY r.stage DESC,r.time,r.owner";
            cmd.Parameters.AddWithValue("$s", season); cmd.Parameters.AddWithValue("$d", continent);
            var rows = new List<RankingScore>(); using var r = cmd.ExecuteReader();
            while (r.Read()) rows.Add(new(r.GetInt32(0), r.GetString(1), r.GetInt32(2), r.GetInt32(3), r.GetInt64(4)));
            return rows;
        }
    }
}
