// SPDX-License-Identifier: MIT
// Copyright (c) 2026 the TeraSharp contributors

namespace TeraSharp.Arbiter.Persistence;

public sealed partial class CharacterStore
{
    public sealed record AwakenEvent(int Id, bool Enchant, int[] Values, long Start, long End);
    private void EnsureAwakenEvents() => Exec(@"CREATE TABLE IF NOT EXISTS qa_awaken_change(id INTEGER PRIMARY KEY AUTOINCREMENT,data BLOB NOT NULL,start INTEGER NOT NULL,end INTEGER NOT NULL);
CREATE TABLE IF NOT EXISTS qa_awaken_enchant(id INTEGER PRIMARY KEY AUTOINCREMENT,data BLOB NOT NULL,start INTEGER NOT NULL,end INTEGER NOT NULL);");
    private static string AwakenTable(bool enchant) => enchant ? "qa_awaken_enchant" : "qa_awaken_change";
    public IReadOnlyList<AwakenEvent> GetAwakenEvents(bool enchant)
    {
        lock (_lock)
        {
            EnsureAwakenEvents(); using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT id,data,start,end FROM " + AwakenTable(enchant) + " ORDER BY id";
            var rows = new List<AwakenEvent>(); using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var data = (byte[])r[1]; if (data.Length != (enchant ? 24 : 12)) continue;
                var values = Enumerable.Range(0, data.Length / 4).Select(i => BitConverter.ToInt32(data, i * 4)).ToArray();
                rows.Add(new(r.GetInt32(0), enchant, values, r.GetInt64(2), r.GetInt64(3)));
            }
            return rows;
        }
    }
    public AwakenEvent? AddAwakenEvent(bool enchant, int[] values, long start, long end)
    {
        if (values.Length != (enchant ? 6 : 3) || end <= start) return null;
        lock (_lock)
        {
            EnsureAwakenEvents(); int keyLength = enchant ? 4 : 1;
            if (GetAwakenEvents(enchant).Any(r => r.Values.Take(keyLength).SequenceEqual(values.Take(keyLength)) && start <= r.End && r.Start <= end)) return null;
            byte[] data = new byte[values.Length * 4]; for (int i = 0; i < values.Length; i++) BitConverter.GetBytes(values[i]).CopyTo(data, i * 4);
            using var cmd = _db.CreateCommand(); cmd.CommandText = "INSERT INTO " + AwakenTable(enchant) + "(data,start,end) VALUES($d,$s,$e); SELECT last_insert_rowid()";
            cmd.Parameters.AddWithValue("$d", data); cmd.Parameters.AddWithValue("$s", start); cmd.Parameters.AddWithValue("$e", end);
            return new(Convert.ToInt32(cmd.ExecuteScalar()), enchant, (int[])values.Clone(), start, end);
        }
    }
    public bool DeleteAwakenEvent(bool enchant, int id)
    {
        lock (_lock)
        {
            EnsureAwakenEvents(); using var cmd = _db.CreateCommand(); cmd.CommandText = "DELETE FROM " + AwakenTable(enchant) + " WHERE id=$id";
            cmd.Parameters.AddWithValue("$id", id); return cmd.ExecuteNonQuery() == 1;
        }
    }
}
