// SPDX-License-Identifier: MIT
// Copyright (c) 2026 the TeraSharp contributors

namespace TeraSharp.Arbiter.Persistence;

public sealed partial class CharacterStore
{
    public sealed record GuildLevelRank(int GuildId, int Rank, int PreviousRank, long Exp, long PreviousExp,
        int Level, int AccountCount, long ResetEpoch);
    private void EnsureGuildLevelRanks() => Exec(@"CREATE TABLE IF NOT EXISTS guild_level_ranking(
guild_id INTEGER PRIMARY KEY, ranking INTEGER NOT NULL, previous_rank INTEGER NOT NULL, exp INTEGER NOT NULL,
previous_exp INTEGER NOT NULL, level INTEGER NOT NULL, accounts INTEGER NOT NULL, reset_epoch INTEGER NOT NULL);");

    public IReadOnlyList<GuildLevelRank> GetGuildLevelRanks(bool publishedOnly = true)
    {
        lock (_lock)
        {
            EnsureGuildLevelRanks(); using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT guild_id,ranking,previous_rank,exp,previous_exp,level,accounts,reset_epoch FROM guild_level_ranking ORDER BY ranking"
                + (publishedOnly ? " LIMIT 100" : "");
            var rows = new List<GuildLevelRank>(); using var reader = cmd.ExecuteReader();
            while (reader.Read()) rows.Add(new(reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2), reader.GetInt64(3),
                reader.GetInt64(4), reader.GetInt32(5), reader.GetInt32(6), reader.GetInt64(7))); return rows;
        }
    }

    /// <summary>Arb069:639 stores every eligible guild and publishes only ranks1..100. SQL17364 preserves prior rank/exp.</summary>
    public void CalculateGuildLevelRanks(int minimumAccounts, long now)
    {
        lock (_lock)
        {
            EnsureGuildLevelRanks(); var old = GetGuildLevelRanks(false).ToDictionary(r => r.GuildId);
            var eligible = GetAllGuilds().Select(g => (Guild: g, Accounts: GetGuildMembers(g.GuildId).Select(m => m.AccountId).Distinct().Count()))
                .Where(r => r.Accounts >= minimumAccounts)
                // Native comparator Arb067:19251: experience, distinct accounts, oldest creation.
                .OrderByDescending(r => r.Guild.Exp).ThenByDescending(r => r.Accounts).ThenBy(r => r.Guild.CreateDate).ToArray();
            using var tx = _db.BeginTransaction();
            using (var clear = _db.CreateCommand()) { clear.Transaction = tx; clear.CommandText = "DELETE FROM guild_level_ranking"; clear.ExecuteNonQuery(); }
            for (int i = 0; i < eligible.Length; i++)
            {
                var row = eligible[i]; old.TryGetValue(row.Guild.GuildId, out var previous);
                using var insert = _db.CreateCommand(); insert.Transaction = tx;
                insert.CommandText = "INSERT INTO guild_level_ranking VALUES($g,$r,$p,$e,$pe,$l,$a,$t)";
                insert.Parameters.AddWithValue("$g", row.Guild.GuildId); insert.Parameters.AddWithValue("$r", i + 1);
                insert.Parameters.AddWithValue("$p", previous?.Rank ?? 0); insert.Parameters.AddWithValue("$e", row.Guild.Exp);
                insert.Parameters.AddWithValue("$pe", previous?.Exp ?? 0); insert.Parameters.AddWithValue("$l", row.Guild.Level);
                insert.Parameters.AddWithValue("$a", row.Accounts); insert.Parameters.AddWithValue("$t", now); insert.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }
}
