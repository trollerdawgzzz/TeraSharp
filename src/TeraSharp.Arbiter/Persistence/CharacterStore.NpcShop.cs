// SPDX-License-Identifier: MIT
// Copyright (c) 2026 the TeraSharp contributors

namespace TeraSharp.Arbiter.Persistence;

public sealed partial class CharacterStore
{
    public sealed record NpcShopChange(long Id, int BuyList, int Item, long Price);
    private void EnsureNpcShopChanges()
    {
        using var command = _db.CreateCommand();
        command.CommandText = "CREATE TABLE IF NOT EXISTS npc_shop_changes(id INTEGER PRIMARY KEY AUTOINCREMENT,buy_list INTEGER NOT NULL,item_id INTEGER NOT NULL,price INTEGER NOT NULL)";
        command.ExecuteNonQuery();
    }
    public NpcShopChange AddNpcShopChange(int buyList, int item, long price)
    {
        lock (_lock)
        {
            EnsureNpcShopChanges(); using var command = _db.CreateCommand();
            command.CommandText = "INSERT INTO npc_shop_changes(buy_list,item_id,price) VALUES($b,$i,$p);SELECT last_insert_rowid()";
            command.Parameters.AddWithValue("$b", buyList); command.Parameters.AddWithValue("$i", item); command.Parameters.AddWithValue("$p", price);
            return new((long)command.ExecuteScalar()!, buyList, item, price);
        }
    }
    public IReadOnlyList<NpcShopChange> GetNpcShopChanges()
    {
        lock (_lock)
        {
            EnsureNpcShopChanges(); using var command = _db.CreateCommand();
            command.CommandText = "SELECT id,buy_list,item_id,price FROM npc_shop_changes ORDER BY id";
            using var reader = command.ExecuteReader(); var rows = new List<NpcShopChange>();
            while (reader.Read()) rows.Add(new(reader.GetInt64(0), reader.GetInt32(1), reader.GetInt32(2), reader.GetInt64(3)));
            return rows;
        }
    }
}
