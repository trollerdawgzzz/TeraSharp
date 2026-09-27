// SPDX-License-Identifier: MIT
// Copyright (c) 2026 the TeraSharp contributors

namespace TeraSharp.Arbiter.Persistence;

public sealed partial class CharacterStore
{
    // T201: native spUpdateDungeonPhase / spUpdateDungeonOff and BattleFieldResultManager.
    // These tables are separate from T138's custom bg_rating and raw T199 battle logs.
    private void EnsureQaDungeonTables()
    {
        Exec(@"CREATE TABLE IF NOT EXISTS dungeon_phase_reset(owner INTEGER PRIMARY KEY, epoch INTEGER NOT NULL);
CREATE TABLE IF NOT EXISTS dungeon_phase(owner INTEGER NOT NULL, continent INTEGER NOT NULL, level INTEGER NOT NULL,
 epoch INTEGER NOT NULL, PRIMARY KEY(owner,continent));
CREATE TABLE IF NOT EXISTS native_battlefield_result(owner INTEGER NOT NULL, type INTEGER NOT NULL,
 data BLOB NOT NULL, PRIMARY KEY(owner,type));
CREATE TABLE IF NOT EXISTS disabled_dungeons(id INTEGER PRIMARY KEY);");
    }

    public sealed record DungeonPhaseState(long ResetEpoch, IReadOnlyList<byte[]> Rows);

    public DungeonPhaseState? GetDungeonPhases(int owner)
    {
        lock (_lock)
        {
            EnsureQaDungeonTables();
            using var stamp = _db.CreateCommand();
            stamp.CommandText = "SELECT epoch FROM dungeon_phase_reset WHERE owner=$o";
            stamp.Parameters.AddWithValue("$o", owner);
            if (stamp.ExecuteScalar() is not long epoch) return null;
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT continent,level,epoch FROM dungeon_phase WHERE owner=$o ORDER BY continent";
            cmd.Parameters.AddWithValue("$o", owner);
            var rows = new List<byte[]>(); using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                // Arb065:9819-9957; native DungeonPhaseInfo is 24 bytes, including alignment.
                var row = new byte[24]; BitConverter.GetBytes(r.GetInt32(0)).CopyTo(row, 0);
                BitConverter.GetBytes(owner).CopyTo(row, 4); BitConverter.GetBytes(r.GetInt32(1)).CopyTo(row, 8);
                BitConverter.GetBytes(r.GetInt64(2)).CopyTo(row, 16); rows.Add(row);
            }
            return new(epoch, rows);
        }
    }

    public long InitializeDungeonPhaseReset(int owner, long epoch)
    {
        if (NoSuchOwner(nameof(InitializeDungeonPhaseReset), owner)) return epoch;
        lock (_lock)
        {
            EnsureQaDungeonTables(); using var cmd = _db.CreateCommand();
            cmd.CommandText = "INSERT OR IGNORE INTO dungeon_phase_reset VALUES($o,$e); SELECT epoch FROM dungeon_phase_reset WHERE owner=$o";
            cmd.Parameters.AddWithValue("$o", owner); cmd.Parameters.AddWithValue("$e", epoch);
            return Convert.ToInt64(cmd.ExecuteScalar());
        }
    }

    public bool SetDungeonPhase(int owner, int continent, int level, long defaultResetEpoch)
    {
        if (NoSuchOwner(nameof(SetDungeonPhase), owner)) return false;
        lock (_lock)
        {
            long epoch = InitializeDungeonPhaseReset(owner, defaultResetEpoch);
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "INSERT INTO dungeon_phase VALUES($o,$c,$l,$e) ON CONFLICT(owner,continent) DO UPDATE SET level=excluded.level,epoch=excluded.epoch";
            cmd.Parameters.AddWithValue("$o", owner); cmd.Parameters.AddWithValue("$c", continent);
            cmd.Parameters.AddWithValue("$l", level); cmd.Parameters.AddWithValue("$e", epoch);
            return cmd.ExecuteNonQuery() == 1;
        }
    }

    public IReadOnlyList<byte[]> GetNativeBattlefieldResults(int owner)
    {
        lock (_lock)
        {
            EnsureQaDungeonTables(); using var cmd = _db.CreateCommand();
            // Arb071:13822-13914's seven native ranking categories (not the event types 200+).
            cmd.CommandText = "SELECT data FROM native_battlefield_result WHERE owner=$o AND type BETWEEN 0 AND 6 ORDER BY type";
            cmd.Parameters.AddWithValue("$o", owner); var rows = new List<byte[]>(); using var r = cmd.ExecuteReader();
            while (r.Read()) if ((byte[])r[0] is { Length: 64 } row) rows.Add(row);
            return rows;
        }
    }

    public bool SetNativeBattlefieldResult(int owner, int type, int wins, int losses, int draws,
        int kills, int deaths, int assists, int field8, int field10, int grade)
    {
        if (NoSuchOwner(nameof(SetNativeBattlefieldResult), owner)) return false;
        lock (_lock)
        {
            EnsureQaDungeonTables(); using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT data FROM native_battlefield_result WHERE owner=$o AND type=$t";
            cmd.Parameters.AddWithValue("$o", owner); cmd.Parameters.AddWithValue("$t", type);
            var row = cmd.ExecuteScalar() as byte[];
            if (row?.Length != 64) row = new byte[64];
            // 64-byte BattleFieldData projection (Arb071:13822). Preserve the other counters.
            int[] values = { owner, type, wins, losses, draws, kills, deaths, assists, field8, field10 };
            for (int i = 0; i < values.Length; i++) BitConverter.GetBytes(values[i]).CopyTo(row, i * 4);
            BitConverter.GetBytes(grade).CopyTo(row, 48);
            cmd.CommandText = "INSERT INTO native_battlefield_result VALUES($o,$t,$d) ON CONFLICT(owner,type) DO UPDATE SET data=excluded.data";
            cmd.Parameters.AddWithValue("$d", row); return cmd.ExecuteNonQuery() == 1;
        }
    }

    public IReadOnlyList<int> GetDisabledDungeons()
    {
        lock (_lock)
        {
            EnsureQaDungeonTables(); using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT id FROM disabled_dungeons ORDER BY id";
            var ids = new List<int>(); using var r = cmd.ExecuteReader(); while (r.Read()) ids.Add(r.GetInt32(0)); return ids;
        }
    }

    public void SetDungeonEnabled(int id, bool enabled)
    {
        lock (_lock)
        {
            EnsureQaDungeonTables(); using var cmd = _db.CreateCommand();
            cmd.CommandText = enabled ? "DELETE FROM disabled_dungeons WHERE id=$id" : "INSERT OR IGNORE INTO disabled_dungeons VALUES($id)";
            cmd.Parameters.AddWithValue("$id", id); cmd.ExecuteNonQuery();
        }
    }
}
