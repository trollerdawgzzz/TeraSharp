// SPDX-License-Identifier: MIT
// Copyright (c) 2026 the TeraSharp contributors

namespace TeraSharp.Arbiter.World;

/// <summary>T180: resolved Arbiter User handle, never a captured pointer used as a UserDbId.</summary>
public sealed record WorldControlUser(uint PlanetId, uint UserDbId, int WorldId,
    Action? CancelPendingReturn = null);

/// <summary>
/// T180. Per-Arbiter state for SA_REQUEST_LEAVE_DUNGEON and SA_ADMIN_HOLD_CHARACTER.
/// Arb_part_062.c:13016-13099 routes leave by continent; :2303-2313 resolves the hold target.
/// Arb_part_030.c:5047-5118 stores hold + expiry; Arb_part_029.c:11908-11984 releases on tick.
/// Callbacks deliberately route to the resolved World; there is no arriving-link fallback.
/// </summary>
public sealed class WorldUserControls
{
    public const ushort SA_ADMIN_HOLD_CHARACTER = 0x158A;
    public const ushort AS_ADMIN_HOLD_CHARACTER = 0x158C;
    // Restriction/holdCharacter default 60 (Arb_part_058.c:13653); DateTime adds seconds
    // (Arb_part_001.c:5099). No evidence for an indefinite hold.
    public static readonly TimeSpan DefaultHoldDuration = TimeSpan.FromSeconds(60);
    public sealed record Departure(uint UserDbId, int Continent, DateTimeOffset RequestedAt);
    private sealed record Hold(uint UserDbId, DateTimeOffset ExpiresAt);
    private readonly Dictionary<ulong, Departure> _departures = new();
    private readonly Dictionary<ulong, Hold> _holds = new();
    private readonly object _gate = new();

    public Departure? DepartureFor(ulong handle)
    {
        lock (_gate) return _departures.GetValueOrDefault(handle);
    }

    public bool IsHeld(uint userDbId, DateTimeOffset now)
    {
        lock (_gate) return _holds.Values.Any(h => h.UserDbId == userDbId && h.ExpiresAt > now);
    }

    public void Forget(ulong handle)
    {
        lock (_gate) { _departures.Remove(handle); _holds.Remove(handle); }
    }

    /// <summary>
    /// T188. Forget every hold and departure for one character, by user db id and (when it is
    /// known) by the handle it was registered under. Returns true when there was one: a hold
    /// that outlived its World session is exactly what the admin reset is for.
    /// </summary>
    public bool ForgetUser(uint userDbId, ulong handle = 0)
    {
        lock (_gate)
        {
            bool had = handle != 0 && (_departures.Remove(handle) | _holds.Remove(handle));
            foreach (var k in _holds.Where(kv => kv.Value.UserDbId == userDbId).Select(kv => kv.Key).ToList())
                had |= _holds.Remove(k);
            foreach (var k in _departures.Where(kv => kv.Value.UserDbId == userDbId).Select(kv => kv.Key).ToList())
                had |= _departures.Remove(k);
            return had;
        }
    }

    public bool TryHandle(ushort op, byte[] payload, Func<ulong, WorldControlUser?> resolve,
        DungeonChannels channels, Func<int, bool> isLive, Action<WorldSend> send, DateTimeOffset now)
    {
        if (op != ContinentHandoff.SA_REQUEST_LEAVE_DUNGEON && op != SA_ADMIN_HOLD_CHARACTER)
            return false;
        int min = op == SA_ADMIN_HOLD_CHARACTER ? 9 : 12;
        if (payload.Length < min) return true;
        ulong handle = BitConverter.ToUInt64(payload, 0);
        lock (_gate)
        {
            var user = resolve(handle);
            if (user == null) return true;
            if (op == ContinentHandoff.SA_REQUEST_LEAVE_DUNGEON)
            {
                int continent = BitConverter.ToInt32(payload, 8);
                // The real handler queues cancellation of lobby return and prepare-appearance,
                // before attempting the owner lookup. TeraSharp has no prepare-appearance state.
                user.CancelPendingReturn?.Invoke();
                _departures[handle] = new(user.UserDbId, continent, now);
                if (channels.WorldForContinent(continent) is int owner && isLive(owner))
                    send(new(owner, ContinentHandoff.AS_REQUEST_LEAVE_DUNGEON,
                        ContinentHandoff.LeaveReply(user.PlanetId, user.UserDbId, continent)));
            }
            else
            {
                bool held = payload[8] != 0;
                if (held) _holds[handle] = new(user.UserDbId, now + DefaultHoldDuration);
                else _holds.Remove(handle);
                if (isLive(user.WorldId))
                    send(new(user.WorldId, AS_ADMIN_HOLD_CHARACTER, HoldReply(user.UserDbId, held)));
                // World applies the flag, then tunnels S_ADMIN_HOLD_CHARACTER (final2b 51511).
                // Sending a second client notification here would duplicate that packet.
            }
        }
        return true;
    }

    /// <summary>Serialize tick with hold/release so an old expiry cannot release a renewed hold.</summary>
    public void Tick(DateTimeOffset now, Func<ulong, WorldControlUser?> resolve,
        Func<int, bool> isLive, Action<WorldSend> send)
    {
        lock (_gate)
        {
            foreach (var (handle, hold) in _holds.ToArray())
            {
                if (hold.ExpiresAt > now) continue;
                var user = resolve(handle);
                if (user != null && user.UserDbId == hold.UserDbId && isLive(user.WorldId))
                    send(new(user.WorldId, AS_ADMIN_HOLD_CHARACTER, HoldReply(user.UserDbId, false)));
                _holds.Remove(handle);
            }
        }
    }

    public static byte[] HoldReply(uint userDbId, bool held)
    {
        var payload = new byte[5];
        BitConverter.TryWriteBytes(payload.AsSpan(0, 4), userDbId);
        payload[4] = held ? (byte)1 : (byte)0;
        return payload;
    }
}
