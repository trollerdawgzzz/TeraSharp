// SPDX-License-Identifier: MIT
// Copyright (c) 2026 the TeraSharp contributors

namespace TeraSharp.Arbiter.Persistence;

public sealed partial class CharacterStore
{
    /// <summary>
    /// Native warehouse transactions validate before executing any atom (Arb048:13470–13537).
    /// DO_TS_WARE_CHANGE_MONEY rejects negative balances and reports29 only for the upper cap
    /// (Arb038:4403–4478). Hold the same store lock through validation and application so another
    /// World link cannot change the balance between those steps.
    /// </summary>
    public bool TryApplyWarehouseTransfer(IEnumerable<(long Owner, int Inven, long Delta)> changes,
        long maximum, Action apply, out uint error)
    {
        lock (_lock)
        {
            error = 0;
            long cap = maximum > 0 ? maximum : 100_000_000_000_000L;
            var projected = new Dictionary<(long Owner, int Inven), decimal>();
            foreach (var change in changes)
            {
                var key = (change.Owner, change.Inven);
                decimal current = projected.TryGetValue(key, out var value) ? value : GetWarehouse(key.Owner, key.Inven).Money;
                decimal next = current + change.Delta;
                if (next < 0) return false;
                if (next > cap) { error = 29; return false; }
                projected[key] = next;
            }
            apply();
            return true;
        }
    }
}
