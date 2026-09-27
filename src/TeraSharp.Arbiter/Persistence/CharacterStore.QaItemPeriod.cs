// SPDX-License-Identifier: MIT
// Copyright (c) 2026 the TeraSharp contributors

namespace TeraSharp.Arbiter.Persistence;

public sealed partial class CharacterStore
{
    public sealed record QaItemPeriod(int Id, int Template, long Start, long End, long Expiry);
    private void EnsureQaItemPeriods()
    {
        using var command = _db.CreateCommand();
        command.CommandText = "CREATE TABLE IF NOT EXISTS qa_item_period(event_id INTEGER PRIMARY KEY AUTOINCREMENT,template_id INTEGER NOT NULL,start_time INTEGER NOT NULL,end_time INTEGER NOT NULL,expiry_time INTEGER NOT NULL)";
        command.ExecuteNonQuery();
    }
    public IReadOnlyList<QaItemPeriod> GetQaItemPeriods()
    {
        lock (_lock)
        {
            EnsureQaItemPeriods(); using var command = _db.CreateCommand();
            command.CommandText = "SELECT event_id,template_id,start_time,end_time,expiry_time FROM qa_item_period ORDER BY event_id";
            using var reader = command.ExecuteReader(); var rows = new List<QaItemPeriod>();
            while (reader.Read()) rows.Add(new(reader.GetInt32(0), reader.GetInt32(1), reader.GetInt64(2), reader.GetInt64(3), reader.GetInt64(4)));
            return rows;
        }
    }
    public int AddQaItemPeriod(int template, long start, long end, long expiry)
    {
        lock (_lock)
        {
            EnsureQaItemPeriods(); using var command = _db.CreateCommand();
            // Native NewInfoValidCheck rejects touching as well as overlapping intervals for the same template.
            command.CommandText = "SELECT COUNT(*) FROM qa_item_period WHERE template_id=$t AND start_time<=$end AND end_time>=$start";
            command.Parameters.AddWithValue("$t", template); command.Parameters.AddWithValue("$start", start); command.Parameters.AddWithValue("$end", end);
            if (Convert.ToInt64(command.ExecuteScalar()) != 0) return 0;
            command.Parameters.AddWithValue("$expiry", expiry);
            command.CommandText = "INSERT INTO qa_item_period(template_id,start_time,end_time,expiry_time) VALUES($t,$start,$end,$expiry);SELECT last_insert_rowid()";
            return checked((int)(long)command.ExecuteScalar()!);
        }
    }
    public void DeleteQaItemPeriod(int id)
    {
        lock (_lock)
        {
            EnsureQaItemPeriods(); using var command = _db.CreateCommand();
            command.CommandText = "DELETE FROM qa_item_period WHERE event_id=$id"; command.Parameters.AddWithValue("$id", id); command.ExecuteNonQuery();
        }
    }
}
