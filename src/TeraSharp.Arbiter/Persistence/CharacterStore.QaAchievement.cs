// SPDX-License-Identifier: MIT
// Copyright (c) 2026 the TeraSharp contributors

namespace TeraSharp.Arbiter.Persistence;

public sealed partial class CharacterStore
{
    private void EnsureAchievementSeasons() => Exec("CREATE TABLE IF NOT EXISTS achievement_seasons(id INTEGER PRIMARY KEY,start INTEGER NOT NULL)");
    public Dictionary<int, long> GetAchievementSeasons()
    {
        lock (_lock)
        {
            EnsureAchievementSeasons(); using var cmd = _db.CreateCommand(); cmd.CommandText = "SELECT id,start FROM achievement_seasons ORDER BY id";
            using var r = cmd.ExecuteReader(); var rows = new Dictionary<int, long>();
            while (r.Read()) rows[r.GetInt32(0)] = r.GetInt64(1); return rows;
        }
    }
    public void SetAchievementSeason(int id, long start)
    {
        lock (_lock)
        {
            EnsureAchievementSeasons(); using var cmd = _db.CreateCommand();
            cmd.CommandText = "INSERT INTO achievement_seasons VALUES($id,$start) ON CONFLICT(id) DO UPDATE SET start=excluded.start";
            cmd.Parameters.AddWithValue("$id", id); cmd.Parameters.AddWithValue("$start", start); cmd.ExecuteNonQuery();
        }
    }
}
