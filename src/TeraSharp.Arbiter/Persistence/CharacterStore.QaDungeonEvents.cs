// SPDX-License-Identifier: MIT
// Copyright (c) 2026 the TeraSharp contributors

namespace TeraSharp.Arbiter.Persistence;

public sealed partial class CharacterStore
{
    public sealed record ContinentAbnormalityEvent(int Id, int Continent, int Abnormality, long Start, long End);
    private void EnsureAbnormalityEvents() => Exec(@"CREATE TABLE IF NOT EXISTS continent_abnormality_events(
 id INTEGER PRIMARY KEY AUTOINCREMENT, continent INTEGER NOT NULL, abnormality INTEGER NOT NULL,
 start INTEGER NOT NULL, end INTEGER NOT NULL);");
    public ContinentAbnormalityEvent AddContinentAbnormalityEvent(int continent, int abnormality, long start, long end)
    {
        lock (_lock)
        {
            EnsureAbnormalityEvents(); using var cmd = _db.CreateCommand();
            // Native spInsertContinentAbnormality always allocates a fresh identity; repeated
            // continent/abnormality pairs are allowed. The reservation is scoped to that identity.
            cmd.CommandText = "INSERT INTO continent_abnormality_events(continent,abnormality,start,end) VALUES($c,$a,$s,$e) RETURNING id";
            cmd.Parameters.AddWithValue("$c", continent); cmd.Parameters.AddWithValue("$a", abnormality);
            cmd.Parameters.AddWithValue("$s", start); cmd.Parameters.AddWithValue("$e", end);
            return new(Convert.ToInt32(cmd.ExecuteScalar()), continent, abnormality, start, end);
        }
    }
    public List<ContinentAbnormalityEvent> GetContinentAbnormalityEvents()
    {
        lock (_lock)
        {
            EnsureAbnormalityEvents(); using var cmd = _db.CreateCommand(); cmd.CommandText = "SELECT id,continent,abnormality,start,end FROM continent_abnormality_events ORDER BY id";
            using var r = cmd.ExecuteReader(); var rows = new List<ContinentAbnormalityEvent>();
            while (r.Read()) rows.Add(new(r.GetInt32(0), r.GetInt32(1), r.GetInt32(2), r.GetInt64(3), r.GetInt64(4)));
            return rows;
        }
    }
    public void DeleteContinentAbnormalityEvent(int id)
    {
        lock (_lock) { EnsureAbnormalityEvents(); using var cmd = _db.CreateCommand(); cmd.CommandText = "DELETE FROM continent_abnormality_events WHERE id=$id"; cmd.Parameters.AddWithValue("$id", id); cmd.ExecuteNonQuery(); }
    }

    public sealed record DungeonRookieEvent(int Id, int Continent, long Start, long End, int Item, int Amount);
    private void EnsureRookieEvents() => Exec(@"CREATE TABLE IF NOT EXISTS dungeon_rookie_events(
 id INTEGER PRIMARY KEY AUTOINCREMENT, continent INTEGER NOT NULL, start INTEGER NOT NULL,
 end INTEGER NOT NULL, item INTEGER NOT NULL, amount INTEGER NOT NULL);");

    // Native SQL5268-5320 stores event times and reward target1; QA's other reward list is empty.
    public DungeonRookieEvent? AddDungeonRookieEvent(int continent, long start, long end, int item, int amount)
    {
        lock (_lock)
        {
            EnsureRookieEvents(); using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT count(*) FROM dungeon_rookie_events WHERE continent=$c AND start<=$end AND end>=$start";
            cmd.Parameters.AddWithValue("$c", continent); cmd.Parameters.AddWithValue("$start", start); cmd.Parameters.AddWithValue("$end", end);
            if ((long)cmd.ExecuteScalar()! != 0 || end < start) return null;
            cmd.CommandText = "INSERT INTO dungeon_rookie_events(continent,start,end,item,amount) VALUES($c,$start,$end,$i,$a) RETURNING id";
            cmd.Parameters.AddWithValue("$i", item); cmd.Parameters.AddWithValue("$a", amount);
            return new(Convert.ToInt32(cmd.ExecuteScalar()), continent, start, end, item, amount);
        }
    }
    public List<DungeonRookieEvent> GetDungeonRookieEvents()
    {
        lock (_lock)
        {
            EnsureRookieEvents(); using var cmd = _db.CreateCommand(); cmd.CommandText = "SELECT id,continent,start,end,item,amount FROM dungeon_rookie_events ORDER BY id";
            using var r = cmd.ExecuteReader(); var rows = new List<DungeonRookieEvent>();
            while (r.Read()) rows.Add(new(r.GetInt32(0), r.GetInt32(1), r.GetInt64(2), r.GetInt64(3), r.GetInt32(4), r.GetInt32(5)));
            return rows;
        }
    }
    public void DeleteDungeonRookieEvent(int id)
    {
        lock (_lock) { EnsureRookieEvents(); using var cmd = _db.CreateCommand(); cmd.CommandText = "DELETE FROM dungeon_rookie_events WHERE id=$id"; cmd.Parameters.AddWithValue("$id", id); cmd.ExecuteNonQuery(); }
    }
}
