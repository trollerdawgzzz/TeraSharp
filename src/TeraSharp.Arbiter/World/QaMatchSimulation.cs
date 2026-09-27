// SPDX-License-Identifier: MIT
// Copyright (c) 2026 the TeraSharp contributors

namespace TeraSharp.Arbiter.World;

/// <summary>Arb044:6227-6262 -> Arb077:8205. Ten-minute S_MATCH_PROGRESS diagnostic;
/// it changes neither pool membership nor formation rules (Arb077:8930).</summary>
internal static class QaMatchSimulation
{
    private static readonly object Gate = new();
    private static (long Since, int Tank, int Dps, int Healer)? current;
    internal static Func<long> Clock = () => Environment.TickCount64;
    internal static void Set(int tank, int dps, int healer) { lock (Gate) current = (Clock(), tank, dps, healer); }
    internal static void Clear() { lock (Gate) current = null; }
    internal static byte[]? Progress(int instance, bool battlefield)
    {
        lock (Gate)
        {
            if (current is not { } value) return null;
            if (Clock() - value.Since >= 600000) { current = null; return null; }
            return MatchQueueManager.BuildMatchProgress(instance, battlefield ? 1 : 0, 0, value.Tank, value.Dps, value.Healer);
        }
    }
}
