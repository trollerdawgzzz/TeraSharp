// SPDX-License-Identifier: MIT
// Copyright (c) 2026 the TeraSharp contributors

namespace TeraSharp.Arbiter.Persistence;

public sealed partial class CharacterStore
{
    public sealed record NonPkSection(int Continent, string Area, int Section);
    private void EnsureNonPkSections() => Exec(@"CREATE TABLE IF NOT EXISTS admin_non_pk_sections(
continent INTEGER NOT NULL, area TEXT NOT NULL, section INTEGER NOT NULL,
PRIMARY KEY(continent,area,section));");

    public void SetNonPkSection(NonPkSection row, bool nonPk)
    {
        lock (_lock)
        {
            EnsureNonPkSections(); using var cmd = _db.CreateCommand();
            cmd.CommandText = nonPk ? "INSERT OR IGNORE INTO admin_non_pk_sections VALUES($c,$a,$s)"
                : "DELETE FROM admin_non_pk_sections WHERE continent=$c AND area=$a AND section=$s";
            cmd.Parameters.AddWithValue("$c", row.Continent); cmd.Parameters.AddWithValue("$a", row.Area);
            cmd.Parameters.AddWithValue("$s", row.Section); cmd.ExecuteNonQuery();
        }
    }
    public IReadOnlyList<NonPkSection> GetNonPkSections()
    {
        lock (_lock)
        {
            EnsureNonPkSections(); using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT continent,area,section FROM admin_non_pk_sections ORDER BY continent,area,section";
            using var reader = cmd.ExecuteReader(); var rows = new List<NonPkSection>();
            while (reader.Read()) rows.Add(new(reader.GetInt32(0), reader.GetString(1), reader.GetInt32(2))); return rows;
        }
    }

    /// <summary>Native SetPosNoLock persists location without a teleport or a logout (Arb030:5288).</summary>
    public bool SetQaLocation(int user, int continent, uint channel, float x, float y, float z)
    {
        lock (_lock)
        {
            var record = GetCharacter(user); if (record == null) return false;
            byte[]? blob = record.WorldBlob == null ? null : (byte[])record.WorldBlob.Clone();
            if (blob is { Length: >= 244 })
            {
                BitConverter.GetBytes(x).CopyTo(blob, 220); BitConverter.GetBytes(y).CopyTo(blob, 224);
                BitConverter.GetBytes(z).CopyTo(blob, 228); BitConverter.GetBytes(continent).CopyTo(blob, 236);
                BitConverter.GetBytes(channel).CopyTo(blob, 240);
            }
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "UPDATE characters SET zone=$c,x=$x,y=$y,z=$z,world_blob=$b WHERE id=$u";
            cmd.Parameters.AddWithValue("$c", continent); cmd.Parameters.AddWithValue("$x", x);
            cmd.Parameters.AddWithValue("$y", y); cmd.Parameters.AddWithValue("$z", z);
            cmd.Parameters.AddWithValue("$b", (object?)blob ?? DBNull.Value); cmd.Parameters.AddWithValue("$u", user);
            return cmd.ExecuteNonQuery() == 1;
        }
    }
}
