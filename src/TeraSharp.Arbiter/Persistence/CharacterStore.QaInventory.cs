// SPDX-License-Identifier: MIT
// Copyright (c) 2026 the TeraSharp contributors

namespace TeraSharp.Arbiter.Persistence;

public sealed partial class CharacterStore
{
    /// <summary>Native SharedDBCache task5/6 resets purchased page count; clear variants also empty the container.</summary>
    public void ResetQaWarehouse(long accountId, int invenType, bool clearItems)
    {
        if (invenType is not (1 or 12)) throw new ArgumentOutOfRangeException(nameof(invenType));
        lock (_lock)
        {
            using var transaction = _db.BeginTransaction();
            using var command = _db.CreateCommand(); command.Transaction = transaction;
            command.Parameters.AddWithValue("$a", accountId); command.Parameters.AddWithValue("$t", invenType);
            if (clearItems)
            {
                command.CommandText = "DELETE FROM items WHERE owner_db_id=$a AND inven_type=$t"; command.ExecuteNonQuery();
            }
            command.CommandText = "INSERT INTO warehouses(owner_db_id,inven_type,money,slot_count,updated_at) VALUES($a,$t,0,0,datetime('now')) "
                + "ON CONFLICT(owner_db_id,inven_type) DO UPDATE SET slot_count=0,updated_at=datetime('now')" + (clearItems ? ",money=0" : "");
            command.ExecuteNonQuery(); transaction.Commit();
        }
    }
}
