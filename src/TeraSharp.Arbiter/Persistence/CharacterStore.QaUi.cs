// SPDX-License-Identifier: MIT
// Copyright (c) 2026 the TeraSharp contributors

namespace TeraSharp.Arbiter.Persistence;

public sealed partial class CharacterStore
{
    public void SetQaVoice(int owner, byte voice)
    {
        lock (_lock)
        {
            using var get = _db.CreateCommand(); get.CommandText = "SELECT appearance FROM characters WHERE id=$o";
            get.Parameters.AddWithValue("$o", owner);
            if (get.ExecuteScalar() is not byte[] appearance || appearance.Length < 2) return;
            appearance[1] = voice;
            using var put = _db.CreateCommand();
            put.CommandText = "UPDATE characters SET appearance=$a, qa_voice=$v WHERE id=$o";
            put.Parameters.AddWithValue("$a", appearance); put.Parameters.AddWithValue("$v", voice); put.Parameters.AddWithValue("$o", owner);
            put.ExecuteNonQuery();
        }
    }

    // Native spUpdateObserverType and spLoadAccountUsers2 preserve3/-1. No native immediate wire reply.
    public void SetQaObserverType(int owner, int value)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand(); cmd.CommandText = "UPDATE characters SET observer_type=$v WHERE id=$o";
            cmd.Parameters.AddWithValue("$v", value); cmd.Parameters.AddWithValue("$o", owner); cmd.ExecuteNonQuery();
        }
    }

    public int GetQaObserverType(int owner)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand(); cmd.CommandText = "SELECT observer_type FROM characters WHERE id=$o";
            cmd.Parameters.AddWithValue("$o", owner); return cmd.ExecuteScalar() is long value ? (int)value : -1;
        }
    }

    // Arb070 UpdateContentsOnOff + Arb069 DisableDecoUI remove older type33 ids; exactly one stays selected.
    public void SetQaDecoUi(int value)
    {
        lock (_lock)
        {
            Exec("CREATE TABLE IF NOT EXISTS qa_ui_state(name TEXT PRIMARY KEY, value INTEGER NOT NULL)");
            using var cmd = _db.CreateCommand(); cmd.CommandText = "INSERT OR REPLACE INTO qa_ui_state VALUES('deco',$v)";
            cmd.Parameters.AddWithValue("$v", value); cmd.ExecuteNonQuery();
        }
    }

    public int? GetQaDecoUi()
    {
        lock (_lock)
        {
            Exec("CREATE TABLE IF NOT EXISTS qa_ui_state(name TEXT PRIMARY KEY, value INTEGER NOT NULL)");
            using var cmd = _db.CreateCommand(); cmd.CommandText = "SELECT value FROM qa_ui_state WHERE name='deco'";
            return cmd.ExecuteScalar() is long value ? (int)value : null;
        }
    }
}
