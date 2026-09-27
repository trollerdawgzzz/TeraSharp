// SPDX-License-Identifier: MIT
// Copyright (c) 2026 the TeraSharp contributors

namespace TeraSharp.Arbiter.Persistence;

public sealed partial class CharacterStore
{
    public int GetFriendshipGage(int owner, int friend)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand(); cmd.CommandText = "SELECT friendship_gage FROM friends WHERE character_id=$o AND friend_id=$f";
            cmd.Parameters.AddWithValue("$o", owner); cmd.Parameters.AddWithValue("$f", friend);
            return cmd.ExecuteScalar() is long value ? (int)value : 0;
        }
    }

    public int MaxFriendshipGage(int owner)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand(); cmd.CommandText = "SELECT COALESCE(MAX(friendship_gage),0) FROM friends WHERE character_id=$o";
            cmd.Parameters.AddWithValue("$o", owner); return Math.Max(0, Convert.ToInt32(cmd.ExecuteScalar()));
        }
    }

    public bool SetFriendshipGage(int owner, int friend, int requested, out int value)
    {
        lock (_lock)
        {
            value = Math.Min(requested, 9999999); // Arb030:10915: condition6 assigns, preserves native signed values.
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "UPDATE friends SET friendship_gage=$v WHERE character_id=$o AND friend_id=$f AND friendship_gage<=9999999 AND friendship_gage<>$v";
            cmd.Parameters.AddWithValue("$o", owner); cmd.Parameters.AddWithValue("$f", friend); cmd.Parameters.AddWithValue("$v", value);
            return cmd.ExecuteNonQuery() != 0;
        }
    }

    public void SetQaChatBan(long accountId, long untilUnix)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand(); cmd.CommandText = "UPDATE accounts SET chat_ban_until=$u WHERE id=$a";
            cmd.Parameters.AddWithValue("$a", accountId); cmd.Parameters.AddWithValue("$u", untilUnix); cmd.ExecuteNonQuery();
        }
    }

    public long GetQaChatBan(long accountId)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand(); cmd.CommandText = "SELECT chat_ban_until FROM accounts WHERE id=$a";
            cmd.Parameters.AddWithValue("$a", accountId); return cmd.ExecuteScalar() is long value ? value : 0;
        }
    }

    public bool ClearExpiredQaChatBan(long accountId, long nowUnix)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "UPDATE accounts SET chat_ban_until=0 WHERE id=$a AND chat_ban_until<>0 AND chat_ban_until<=$n";
            cmd.Parameters.AddWithValue("$a", accountId); cmd.Parameters.AddWithValue("$n", nowUnix);
            return cmd.ExecuteNonQuery() != 0;
        }
    }
}
