// SPDX-License-Identifier: MIT
// Copyright (c) 2026 the TeraSharp contributors

namespace TeraSharp.Arbiter.Persistence;

public sealed partial class CharacterStore
{
    public sealed record FestivalEvent(int Id, int Event, long Start, long End);
    private void EnsureFestivals() => Exec("CREATE TABLE IF NOT EXISTS festival_events(id INTEGER PRIMARY KEY AUTOINCREMENT,event INTEGER NOT NULL,start INTEGER NOT NULL,end INTEGER NOT NULL)");
    public List<FestivalEvent> GetFestivalEvents()
    {
        lock (_lock)
        {
            EnsureFestivals(); using var cmd = _db.CreateCommand(); cmd.CommandText = "SELECT id,event,start,end FROM festival_events ORDER BY id";
            using var r = cmd.ExecuteReader(); var rows = new List<FestivalEvent>();
            while (r.Read()) rows.Add(new(r.GetInt32(0), r.GetInt32(1), r.GetInt64(2), r.GetInt64(3))); return rows;
        }
    }
    public FestivalEvent? AddFestivalEvent(int eventId, long start, long end, bool scheduled)
    {
        lock (_lock)
        {
            var rows = GetFestivalEvents();
            if (scheduled && rows.Any(r => r.Event == eventId && r.End != 0 && start <= r.End && r.Start <= end)) return null;
            if (!scheduled && rows.Any(r => r.Event == eventId && r.Start <= start && (r.End == 0 || start < r.End))) return null;
            using var cmd = _db.CreateCommand(); cmd.CommandText = "INSERT INTO festival_events(event,start,end) VALUES($e,$s,$f) RETURNING id";
            cmd.Parameters.AddWithValue("$e", eventId); cmd.Parameters.AddWithValue("$s", start); cmd.Parameters.AddWithValue("$f", end);
            return new(Convert.ToInt32(cmd.ExecuteScalar()), eventId, start, end);
        }
    }
    public void DeleteFestivalEvent(int id)
    {
        lock (_lock) { EnsureFestivals(); using var cmd = _db.CreateCommand(); cmd.CommandText = "DELETE FROM festival_events WHERE id=$id"; cmd.Parameters.AddWithValue("$id", id); cmd.ExecuteNonQuery(); }
    }
}
