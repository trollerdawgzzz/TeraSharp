// SPDX-License-Identifier: MIT
// Copyright (c) 2026 the TeraSharp contributors

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TeraSharp.Arbiter.Network;

namespace TeraSharp.Arbiter.World;

/// <summary>
/// T172. AS_RESET_PURCHASE_LIMIT (0x1631) - StoreBuyLimitManager::SendResetLimitedStore(int, bool)
/// (Arb_part_085.c:18700): <c>[i32 buyMenuId][u8 1]</c> to World, which clears its per-menu purchase
/// counters. The menus are every <c>&lt;BuyMenu resetType="day"&gt;</c> of BuyMenuData*.xml
/// (<see cref="DatasheetLoader.DailyBuyMenus"/>). cap_final2a/2b 09:00:54: all 24, ascending id, one
/// frame each, the first check after the real Arbiter started (every one was overdue). After that a
/// menu resets when the clock passes its resetTime hour (server local time).
/// </summary>
public static class PurchaseLimitReset
{
    public const ushort AS_RESET_PURCHASE_LIMIT = 0x1631;
    private static readonly object Gate = new();
    private static Timer? _timer;
    private static DateTimeOffset? _lastCheck;
    internal static ILogger Log { get; set; } = NullLogger.Instance;

    public static byte[] Build(int buyMenuId)
    {
        var p = new byte[5];
        BitConverter.GetBytes(buyMenuId).CopyTo(p, 0);
        p[4] = 1;
        return p;
    }

    /// <summary>The menus due between <paramref name="last"/> and <paramref name="now"/>: all of them on
    /// the first check (null), then each whose resetTime hour boundary fell in (last, now].</summary>
    public static List<int> Due(IReadOnlyList<(int Id, int Hour)> menus, DateTimeOffset? last, DateTimeOffset now)
    {
        var due = new List<int>();
        foreach (var (id, hour) in menus)
        {
            if (last == null) { due.Add(id); continue; }
            var boundary = new DateTimeOffset(now.Year, now.Month, now.Day, 0, 0, 0, now.Offset).AddHours(hour);
            if (boundary > now) boundary = boundary.AddDays(-1);
            if (boundary > last.Value) due.Add(id);
        }
        return due;
    }

    /// <summary>One check: the due menus' frames, to every World a player is on (the default World
    /// always). Returns how many frames went out.</summary>
    public static int Check(DateTimeOffset now, WorldBridge? bridge)
    {
        List<int> due;
        lock (Gate) { due = Due(DatasheetLoader.DailyBuyMenus.Value, _lastCheck, now); _lastCheck = now; }
        if (due.Count == 0 || bridge == null || !bridge.IsConnected) return 0;
        foreach (int id in due) Program.Store?.ResetPurchaseLimits(id); // T201: the normal reset and QA reset share the real persisted counters.
        var worlds = bridge.InWorldSessions().Select(s => s.CurrentWorldId).Append(WorldRegistration.DefaultWorldId)
                           .Distinct().Where(bridge.HasLinks).ToList();
        foreach (int w in worlds)
            foreach (int id in due) bridge.SendFrame(w, AS_RESET_PURCHASE_LIMIT, Build(id));
        Log.LogInformation("AS_RESET_PURCHASE_LIMIT: {N} daily menu(s) reset on {W} World(s)", due.Count, worlds.Count);
        return due.Count * worlds.Count;
    }

    /// <summary>Starts the once-a-minute check (idempotent; called on the enter-world edge).</summary>
    public static void EnsureStarted()
    {
        if (global::TeraSharp.Arbiter.Program.World == null) return;
        lock (Gate)
            _timer ??= new Timer(_ =>
            {
                try { Check(DateTimeOffset.Now, global::TeraSharp.Arbiter.Program.World); }
                catch (Exception ex) { Log.LogWarning(ex, "purchase-limit reset check failed"); }
            }, null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    /// <summary>Test seam.</summary>
    public static void Reset() { lock (Gate) { _lastCheck = null; } }
}
