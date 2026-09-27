// SPDX-License-Identifier: MIT
// Copyright (c) 2026 the TeraSharp contributors

namespace TeraSharp.Arbiter.Persistence;

public sealed partial class CharacterStore
{
    public sealed record PurchaseLimit(int BuyMenu, int Menu, int Item, int Count);
    private void EnsurePurchaseLimits()
    {
        using var command = _db.CreateCommand();
        command.CommandText = "CREATE TABLE IF NOT EXISTS purchase_limits(account_id INTEGER NOT NULL,buy_menu INTEGER NOT NULL,menu_id INTEGER NOT NULL,item_id INTEGER NOT NULL,buy_count INTEGER NOT NULL,PRIMARY KEY(account_id,buy_menu,menu_id,item_id))";
        command.ExecuteNonQuery();
    }
    public int AddPurchaseLimit(long account, int buyMenu, int menu, int item, int count)
    {
        if (buyMenu == 0 || menu == 0 || item == -1 || count <= 0) return 0;
        lock (_lock)
        {
            EnsurePurchaseLimits(); using var command = _db.CreateCommand();
            command.CommandText = "INSERT INTO purchase_limits(account_id,buy_menu,menu_id,item_id,buy_count) VALUES($a,$b,$m,$i,$n) ON CONFLICT(account_id,buy_menu,menu_id,item_id) DO UPDATE SET buy_count=buy_count+excluded.buy_count RETURNING buy_count";
            command.Parameters.AddWithValue("$a", account); command.Parameters.AddWithValue("$b", buyMenu); command.Parameters.AddWithValue("$m", menu);
            command.Parameters.AddWithValue("$i", item); command.Parameters.AddWithValue("$n", count);
            return checked((int)(long)command.ExecuteScalar()!);
        }
    }
    public IReadOnlyList<PurchaseLimit> GetPurchaseLimits(long account)
    {
        lock (_lock)
        {
            EnsurePurchaseLimits(); using var command = _db.CreateCommand();
            command.CommandText = "SELECT buy_menu,menu_id,item_id,buy_count FROM purchase_limits WHERE account_id=$a ORDER BY buy_menu,menu_id,item_id";
            command.Parameters.AddWithValue("$a", account); using var reader = command.ExecuteReader(); var rows = new List<PurchaseLimit>();
            while (reader.Read()) rows.Add(new(reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2), reader.GetInt32(3)));
            return rows;
        }
    }
    public void ResetPurchaseLimits(int buyMenu)
    {
        lock (_lock)
        {
            EnsurePurchaseLimits(); using var command = _db.CreateCommand();
            // Both native SQL deletes are scoped to this buyMenu and cover all accounts and realm counts.
            command.CommandText = "DELETE FROM purchase_limits WHERE buy_menu=$b"; command.Parameters.AddWithValue("$b", buyMenu); command.ExecuteNonQuery();
        }
    }
}
