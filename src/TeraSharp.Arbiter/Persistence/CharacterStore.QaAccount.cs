// SPDX-License-Identifier: MIT
// Copyright (c) 2026 the TeraSharp contributors

namespace TeraSharp.Arbiter.Persistence;

public sealed partial class CharacterStore
{
    // Native account objects hold QA packages and publisher XP in RAM. SQL writes are only
    // game XP, tokens, last daily-token receipt and received level rewards (Arb060/061/065).
    private readonly Dictionary<long, Dictionary<int, AccountBenefitRow>> _qaPackages = new();
    private readonly Dictionary<long, int> _qaVipPubExp = new();

    public bool AddQaPackage(long account, int package, long expires)
    {
        if (NoSuchAccount(nameof(AddQaPackage), account)) return false;
        lock (_lock)
        {
            var current = QaPackages(account);
            if (current.TryGetValue(package, out var old) && old.ExpiresAt >= expires) return false;
            current[package] = new(account, package, expires, old?.Value ?? 0);
            return true;
        }
    }
    public void RemoveQaPackage(long account, int package) { lock (_lock) QaPackages(account).Remove(package); }
    public IReadOnlyList<int> ClearQaPackages(long account)
    {
        lock (_lock) { var rows = QaPackages(account); var ids = rows.Keys.OrderBy(x => x).ToArray(); rows.Clear(); return ids; }
    }
    private Dictionary<int, AccountBenefitRow> QaPackages(long account)
    {
        if (!_qaPackages.TryGetValue(account, out var rows))
        {
            rows = GetAccountBenefits(account).ToDictionary(x => x.PackageId);
            _qaPackages[account] = rows;
        }
        return rows;
    }

    public void AddQaVipPublisherExp(long account, int delta)
    {
        if (NoSuchAccount(nameof(AddQaVipPublisherExp), account)) return;
        lock (_lock) _qaVipPubExp[account] = checked(GetVipInfo(account).PubExp + delta);
    }
    public void SetQaVipGameExp(long account, int value)
    {
        if (NoSuchAccount(nameof(SetQaVipGameExp), account)) return;
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "INSERT INTO vip_info(account_id,game_exp) VALUES($a,$v) ON CONFLICT(account_id) DO UPDATE SET game_exp=$v";
            cmd.Parameters.AddWithValue("$a", account); cmd.Parameters.AddWithValue("$v", value); cmd.ExecuteNonQuery();
        }
    }
    public bool AddQaVipTokens(long account, int delta)
    {
        if (NoSuchAccount(nameof(AddQaVipTokens), account)) return false;
        lock (_lock)
        {
            long total = checked(GetVipInfo(account).TokenAmount + delta);
            if (total < 0) return false;
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "INSERT INTO vip_info(account_id,token_amount) VALUES($a,$v) ON CONFLICT(account_id) DO UPDATE SET token_amount=$v";
            cmd.Parameters.AddWithValue("$a", account); cmd.Parameters.AddWithValue("$v", total); cmd.ExecuteNonQuery();
            return true;
        }
    }
    private void EnsureQaVipTables() => Exec(@"CREATE TABLE IF NOT EXISTS vip_daily_token(account_id INTEGER PRIMARY KEY, received_at INTEGER NOT NULL);
CREATE TABLE IF NOT EXISTS vip_level_reward(account_id INTEGER NOT NULL, level INTEGER NOT NULL, PRIMARY KEY(account_id,level));");
    public long GetVipDailyTokenReceived(long account)
    {
        lock (_lock)
        {
            EnsureQaVipTables(); using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT received_at FROM vip_daily_token WHERE account_id=$a";
            cmd.Parameters.AddWithValue("$a", account); return Convert.ToInt64(cmd.ExecuteScalar() ?? 0);
        }
    }
    public void SetVipDailyTokenReceived(long account, long time)
    {
        if (NoSuchAccount(nameof(SetVipDailyTokenReceived), account)) return;
        lock (_lock)
        {
            EnsureQaVipTables(); using var cmd = _db.CreateCommand();
            cmd.CommandText = "INSERT INTO vip_daily_token VALUES($a,$v) ON CONFLICT(account_id) DO UPDATE SET received_at=$v";
            cmd.Parameters.AddWithValue("$a", account); cmd.Parameters.AddWithValue("$v", time); cmd.ExecuteNonQuery();
        }
    }
    public bool MarkVipLevelReward(long account, int level)
    {
        if (NoSuchAccount(nameof(MarkVipLevelReward), account)) return false;
        lock (_lock)
        {
            EnsureQaVipTables(); using var cmd = _db.CreateCommand();
            cmd.CommandText = "INSERT OR IGNORE INTO vip_level_reward VALUES($a,$l)";
            cmd.Parameters.AddWithValue("$a", account); cmd.Parameters.AddWithValue("$l", level); return cmd.ExecuteNonQuery() == 1;
        }
    }
    public void ClearVipLevelRewards(long account)
    {
        lock (_lock)
        {
            EnsureQaVipTables(); using var cmd = _db.CreateCommand();
            cmd.CommandText = "DELETE FROM vip_level_reward WHERE account_id=$a";
            cmd.Parameters.AddWithValue("$a", account); cmd.ExecuteNonQuery();
        }
    }
}
