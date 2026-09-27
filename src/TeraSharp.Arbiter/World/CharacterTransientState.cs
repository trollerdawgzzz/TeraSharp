// SPDX-License-Identifier: MIT
// Copyright (c) 2026 the TeraSharp contributors

namespace TeraSharp.Arbiter.World;

/// <summary>
/// T188 - the per-character state the Arbiter keeps in memory for the length of a session, and
/// the one button that throws it away.
///
/// <para>Everything here is state a character CARRIES between one World session and the next
/// without anything in `characters` recording it: the enter-world stamp and game id, the one-shot
/// Alt+A push, the GM invisibility World reported, a queued or re-offered match, a party-match
/// listing, and T180's hold / leave-dungeon entries. A session that ended without a clean
/// SA_LEAVE_WORLD (a World restart, a crash, a dropped link) leaves some of it behind, and the
/// next login inherits it.</para>
///
/// <para><b>What this is not.</b> It touches nothing durable: no items, no skills, no quests, no
/// money, no character row. A reset is always safe to run on a character that is behaving, and it
/// is the repair for one that is not - the same thing a relog does, without the relog.</para>
/// </summary>
public static class CharacterTransientState
{
    // A client may disconnect before the final World socket does. Remember that character's
    // owner until the World reset; retaining just ids avoids keeping closed GameSessions alive.
    private sealed class DepartedPlayers
    {
        public readonly Dictionary<int, int> Owners = new();
    }
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<WorldBridge, DepartedPlayers>
        Departed = new();

    public static void PlayerRegistered(WorldBridge world, uint playerId)
    {
        var departed = Departed.GetValue(world, static _ => new DepartedPlayers());
        lock (departed) departed.Owners.Remove(unchecked((int)playerId));
    }

    public static void PlayerUnregistered(WorldBridge world, int worldId, uint playerId)
    {
        if (playerId == 0 || playerId > int.MaxValue) return;
        var departed = Departed.GetValue(world, static _ => new DepartedPlayers());
        lock (departed)
        {
            departed.Owners[(int)playerId] = worldId;
        }
    }

    /// <summary>
    /// T192. The last link of a World has gone: its registered characters cannot retain the
    /// enter stamp / GM one-shot from the vanished World process. Use the same reset as the
    /// admin repair, scoped by the session's current owner so another live World is untouched.
    /// Null is the existing all-World ResetTunnelSequence path. This sends no packets.
    /// </summary>
    public static int ResetWorldSessions(WorldBridge world, int? worldId = null)
    {
        var current = world.InWorldSessions().Where(s => s.PlayerId > 0 && s.PlayerId <= int.MaxValue)
            .GroupBy(s => (int)s.PlayerId).ToDictionary(g => g.Key, g => g.Last().CurrentWorldId);
        var players = current.Where(p => worldId == null || p.Value == worldId.Value)
            .Select(p => p.Key).ToHashSet();
        var departed = Departed.GetValue(world, static _ => new DepartedPlayers());
        lock (departed)
        {
            foreach (var p in departed.Owners.Where(p => worldId == null || p.Value == worldId.Value).ToArray())
            {
                // A relog or a hand-off already registered on another World wins over the old owner.
                if (!current.TryGetValue(p.Key, out int owner) || worldId == null || owner == worldId.Value)
                    players.Add(p.Key);
                departed.Owners.Remove(p.Key);
            }
        }
        int changed = 0;
        foreach (int playerId in players)
        {
            var live = world.SessionForPlayerId(playerId);
            if (live != null && worldId != null && live.CurrentWorldId != worldId.Value) continue;
            if (Reset(playerId, world).Count != 0) changed++;
        }
        return changed;
    }

    /// <summary>
    /// Drop every in-memory trace of <paramref name="playerId"/>. Returns the names of the things
    /// that actually held something, so the caller (the admin API) can report what it cleared;
    /// an empty list means the character had nothing pending.
    /// </summary>
    public static IReadOnlyList<string> Reset(int playerId, WorldBridge? world = null)
    {
        var cleared = new List<string>();
        if (playerId <= 0) return cleared;

        // T180 holds and leave-dungeon departures are keyed by the World-side user handle, so
        // the game id has to be read before it is forgotten.
        ulong gameId = DbProxyHandlers.GameIdByPlayer.TryGetValue(playerId, out var g) ? g : 0;
        var bridge = world ?? TeraSharp.Arbiter.Program.World;
        var controls = bridge?.DbProxy?.UserControls;
        if (controls != null && controls.ForgetUser((uint)playerId, gameId)) cleared.Add("hold");

        if (DbProxyHandlers.ForgetPlayer(playerId)) cleared.Add("enter-world");
        if (Handlers.ArbiterClientHandlers.ForgetPlayer(playerId)) cleared.Add("gm-push");
        if (MatchQueueManager.RemoveByPlayer((uint)playerId)) cleared.Add("match-queue");
        if (PartyMatchManager.OnLeaveWorld(playerId)) cleared.Add("party-listing");
        if (bridge != null) PlayerRegistered(bridge, (uint)playerId);
        return cleared;
    }
}
