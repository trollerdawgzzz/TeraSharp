// SPDX-License-Identifier: MIT
// Copyright (c) 2026 the TeraSharp contributors

namespace TeraSharp.Arbiter.Persistence;

public sealed partial class CharacterStore
{
    // Native ProductMarkInfo's twelve SQL columns (GameDatabaseDefinition.xml:3373/6242).
    public sealed record StyleProduct(int Id, int Item, long SaleStart, long SaleEnd, int MarkType,
        long MarkStart, long MarkEnd, int Discount, long PreviewStart, string Label, int Price, int SaleType);
    private void EnsureStyleProducts() => Exec(@"CREATE TABLE IF NOT EXISTS qa_style_products(
id INTEGER PRIMARY KEY,item INTEGER NOT NULL UNIQUE,sale_start INTEGER NOT NULL,sale_end INTEGER NOT NULL,
mark_type INTEGER NOT NULL,mark_start INTEGER NOT NULL,mark_end INTEGER NOT NULL,discount INTEGER NOT NULL,
preview_start INTEGER NOT NULL,label TEXT NOT NULL,price INTEGER NOT NULL,sale_type INTEGER NOT NULL);");
    public IReadOnlyList<StyleProduct> GetStyleProducts()
    {
        lock (_lock)
        {
            EnsureStyleProducts(); using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT id,item,sale_start,sale_end,mark_type,mark_start,mark_end,discount,preview_start,label,price,sale_type FROM qa_style_products ORDER BY id";
            using var r = cmd.ExecuteReader(); var rows = new List<StyleProduct>();
            while (r.Read()) rows.Add(new(r.GetInt32(0), r.GetInt32(1), r.GetInt64(2), r.GetInt64(3), r.GetInt32(4),
                r.GetInt64(5), r.GetInt64(6), r.GetInt32(7), r.GetInt64(8), r.GetString(9), r.GetInt32(10), r.GetInt32(11)));
            return rows;
        }
    }
    public bool AddStyleProduct(StyleProduct row)
    {
        lock (_lock)
        {
            EnsureStyleProducts(); using var cmd = _db.CreateCommand();
            cmd.CommandText = "INSERT OR IGNORE INTO qa_style_products VALUES($id,$item,$start,$end,$mark,$ms,$me,$discount,$preview,$label,$price,$type)";
            cmd.Parameters.AddWithValue("$id", row.Id); cmd.Parameters.AddWithValue("$item", row.Item);
            cmd.Parameters.AddWithValue("$start", row.SaleStart); cmd.Parameters.AddWithValue("$end", row.SaleEnd);
            cmd.Parameters.AddWithValue("$mark", row.MarkType); cmd.Parameters.AddWithValue("$ms", row.MarkStart);
            cmd.Parameters.AddWithValue("$me", row.MarkEnd); cmd.Parameters.AddWithValue("$discount", row.Discount);
            cmd.Parameters.AddWithValue("$preview", row.PreviewStart); cmd.Parameters.AddWithValue("$label", row.Label);
            cmd.Parameters.AddWithValue("$price", row.Price); cmd.Parameters.AddWithValue("$type", row.SaleType);
            return cmd.ExecuteNonQuery() == 1;
        }
    }
    public bool DeleteStyleProduct(int id)
    {
        lock (_lock)
        {
            EnsureStyleProducts(); using var cmd = _db.CreateCommand(); cmd.CommandText = "DELETE FROM qa_style_products WHERE id=$id";
            cmd.Parameters.AddWithValue("$id", id); return cmd.ExecuteNonQuery() == 1;
        }
    }
}
