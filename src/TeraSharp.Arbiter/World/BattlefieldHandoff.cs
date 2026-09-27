// SPDX-License-Identifier: MIT
// Copyright (c) 2026 the TeraSharp contributors

using TeraSharp.Arbiter.Network;
using TeraSharp.Arbiter.Persistence;
using TeraSharp.Arbiter.Protocol;

namespace TeraSharp.Arbiter.World;

/// <summary>T199: cap_bg1's forced offers and two-way World0/BF10 hand-off.
/// Unlike dungeon entry, 13CB carries the RETURN location; 1513 supplies the
/// destination. Arb061:19162-19210; Arb028:14874-14925; Arb029:3495-3515.</summary>
public sealed class BattlefieldHandoff
{
    public const ushort BSA_NOTIFY_USER_ENTERABLE = 0x1513, SA_ENTER_BATTLE_FIELD = 0x13CB,
        BSA_EXIT_BATTLE_FIELD = 0x13CD, AS_REQUEST_ENTER_BATTLEFIELD = 0x1597;
    public sealed record Offer(int UserId, int OwnerWorld, int Battlefield, int Continent, uint Channel,
        long PartyId, float X, float Y, float Z, short Direction, long[] Parties);
    public sealed record EnterRequest(ulong Handle, CharacterStore.SystemReturnPoint Return);
    private sealed record PendingOffer(Offer Offer, GameSession Session, ulong GameId, int SourceWorld);
    private sealed record Entered(int OwnerWorld, GameSession Session, ulong GameId, int? ReturnWorld);
    private readonly object gate = new();
    private readonly Dictionary<int, PendingOffer> offers = new();
    private readonly Dictionary<int, Entered> entered = new();
    private readonly List<(DateTimeOffset Due, int UserId, GameSession Session, ulong GameId, int OwnerWorld, int SourceWorld)> timers = new();

    public static Offer? ParseOffer(int ownerWorld, byte[] p)
    {
        // Fixed52-byte frame; vector's count is BYTES, eight per party ID.
        if (p.Length < 46) return null;
        uint off = BitConverter.ToUInt32(p, 0), bytes = BitConverter.ToUInt32(p, 4);
        if (off < 52 || off - 6 > p.Length || bytes % 8 != 0 || bytes > p.Length - (off - 6)) return null;
        var parties = new long[bytes / 8];
        for (int i = 0; i < parties.Length; i++) parties[i] = BitConverter.ToInt64(p, (int)off - 6 + i * 8);
        return new(BitConverter.ToInt32(p, 8), ownerWorld, BitConverter.ToInt32(p, 12),
            BitConverter.ToInt32(p, 16), BitConverter.ToUInt32(p, 20), BitConverter.ToInt64(p, 24),
            BitConverter.ToSingle(p, 32), BitConverter.ToSingle(p, 36), BitConverter.ToSingle(p, 40),
            BitConverter.ToInt16(p, 44), parties);
    }

    public static EnterRequest? ParseEnter(byte[] p) => p.Length < 28 ? null
        : new(BitConverter.ToUInt64(p, 0), new(BitConverter.ToInt32(p, 8), BitConverter.ToUInt32(p, 12),
            BitConverter.ToInt32(p, 16), BitConverter.ToInt32(p, 20), BitConverter.ToInt32(p, 24)));

    public static byte[] BuildEntranceInfo(Offer offer)
    {
        var p = new byte[12]; BitConverter.GetBytes((ushort)12).CopyTo(p, 0);
        BitConverter.GetBytes((ushort)0x942F).CopyTo(p, 2);
        BitConverter.GetBytes(offer.Battlefield).CopyTo(p, 4); BitConverter.GetBytes(offer.Continent).CopyTo(p, 8);
        return p;
    }

    public static byte[] BuildSystemReturn(uint userId, CharacterStore.SystemReturnPoint point)
    {
        var p = new byte[24]; BitConverter.GetBytes(userId).CopyTo(p, 0);
        BitConverter.GetBytes(point.Continent).CopyTo(p, 4); BitConverter.GetBytes(point.Channel).CopyTo(p, 8);
        BitConverter.GetBytes(point.X).CopyTo(p, 12); BitConverter.GetBytes(point.Y).CopyTo(p, 16);
        BitConverter.GetBytes(point.Z).CopyTo(p, 20); return p;
    }

    public static CrossWorldHandoff.Teleport Destination(Offer offer, ulong handle, ulong gameId, byte[] previous)
        => new(handle, gameId, offer.OwnerWorld, offer.Continent, offer.Channel,
            offer.X, offer.Y, offer.Z, offer.Direction, previous[167..]);

    public static CrossWorldHandoff.Teleport ReturnDestination(CharacterStore.SystemReturnPoint point,
        ulong handle, ulong gameId, byte[] previous)
        => new(handle, gameId, -1, point.Continent, point.Channel, point.X, point.Y, point.Z,
            unchecked((short)BitConverter.ToInt32(previous, 72)), previous[167..]);

    public bool TryHandle(WorldBridge bridge, WorldLink link, CrossWorldHandoff transfers,
        CharacterStore? store, ushort opcode, byte[] payload, DateTimeOffset now)
    {
        if (opcode == BSA_NOTIFY_USER_ENTERABLE)
        {
            var offer = ParseOffer(link.WorldId, payload);
            var user = offer == null ? null : bridge.SessionForPlayerId(offer.UserId);
            if (offer == null || user == null || !user.InWorld) return true;
            bool accept;
            lock (gate)
            {
                accept = !entered.TryGetValue(offer.UserId, out var active)
                    || !ReferenceEquals(active.Session, user) || active.GameId != user.GameId;
                if (accept && offer.Battlefield > 0 && offer.Continent > 0 && offer.Channel != uint.MaxValue)
                {
                    offers[offer.UserId] = new(offer, user, user.GameId, user.CurrentWorldId);
                    // Every offer schedules its own callback. In cap_bg1, the old
                    //38 timer enters the newer37; the37 timer fires while already inside.
                    timers.Add((now.AddSeconds(15), offer.UserId, user, user.GameId, offer.OwnerWorld, user.CurrentWorldId));
                }
                else accept = false;
            }
            if (accept)
            {
                user.Send(BuildEntranceInfo(offer));
                var events = DatasheetLoader.EventMatchingTargets.Value;
                if (events.TryGetValue(offer.Battlefield, out var target) && target.BattleField.Length > 0)
                {
                    user.Send(MatchQueueManager.BuildChangeEventMatchingState(target.BattleField, false, 0));
                    if (bridge.HasLinks(WorldRegistration.DefaultWorldId))
                        bridge.SendFrame(WorldRegistration.DefaultWorldId, PartyPackets.AS_CHANGE_EVENT_MATCHING_STATE,
                            PartyPackets.BuildAsChangeEventMatchingState(offer.UserId, false));
                }
            }
            // SetEnterableBattleFieldInfoCache emits FIN after its cache branch.
            user.Send(MatchQueueManager.BuildFinInterPartyMatch(offer.Battlefield, 1, 0));
            return true;
        }
        if (opcode != SA_ENTER_BATTLE_FIELD && opcode != BSA_EXIT_BATTLE_FIELD) return false;
        if (payload.Length < 8 || store == null) return true;
        ulong handle = BitConverter.ToUInt64(payload, 0);
        var session = bridge.PlayerForGameId(handle);
        if (session == null || !session.InWorld || session.CurrentWorldId != link.WorldId) return true;
        int userId = (int)session.PlayerId;
        var previous = transfers.EnterFor(handle, link.WorldId);
        if (previous == null) return true;
        lock (gate)
        {
            if (opcode == SA_ENTER_BATTLE_FIELD)
            {
                var request = ParseEnter(payload);
                if (request == null || request.Return.Continent <= 0
                    || entered.TryGetValue(userId, out var active) && ReferenceEquals(active.Session, session) && active.GameId == session.GameId
                    || !offers.TryGetValue(userId, out var pending) || !ReferenceEquals(pending.Session, session)
                    || pending.GameId != session.GameId) return true;
                var offer = pending.Offer;
                var party = PartyWiring.Manager.FindByMember(userId);
                if (party == null || !offer.Parties.Contains(party.Id) || !bridge.HasLinks(offer.OwnerWorld)) return true;
                var move = Destination(offer, handle, session.GameId, previous);
                if (!transfers.Begin(link.WorldId, offer.OwnerWorld, move, session.PlayerId)) return true;
                if (!store.SaveSystemReturn(userId, request.Return))
                { transfers.Take(session.GameId, link.WorldId); return true; }
                // Native UpdateSysReturnLoc sends27C6 to the source before LeaveWorldStart.
                link.SendFrame(0x27C6, BuildSystemReturn(session.PlayerId, request.Return));
                entered[userId] = new(offer.OwnerWorld, session, session.GameId, null);
                offers.Remove(userId);
                link.SendFrame(WorldBridge.OpLeaveWorld, CrossWorldHandoff.BuildLeave(move, session.PlayerId));
            }
            else
            {
                if (!entered.TryGetValue(userId, out var active) || !ReferenceEquals(active.Session, session)
                    || active.GameId != session.GameId || active.OwnerWorld != link.WorldId || active.ReturnWorld != null) return true;
                var point = store.GetSystemReturn(userId);
                if (point == null) return true;
                var move = ReturnDestination(point, handle, session.GameId, previous);
                int? owner = CrossWorldHandoff.ResolveDestination(move,
                    channel => DungeonRouting.Channels.WorldForChannel(move.Continent, unchecked((int)channel)),
                    DungeonRouting.Channels.WorldForContinent, DungeonRouting.Channels.CatchAllWorldId);
                if (owner is not int destination || !bridge.HasLinks(destination)
                    || !transfers.Begin(link.WorldId, destination, move, session.PlayerId)) return true;
                // Native LeaveSysParty removes only a system party (Arb079:13428).
                // This forced capture has normal parties, retained through both transfers.
                entered[userId] = active with { ReturnWorld = destination };
                link.SendFrame(WorldBridge.OpLeaveWorld, CrossWorldHandoff.BuildLeave(move, session.PlayerId));
            }
        }
        return true;
    }

    public void Tick(WorldBridge bridge, DateTimeOffset now)
    {
        List<(DateTimeOffset Due, int UserId, GameSession Session, ulong GameId, int OwnerWorld, int SourceWorld)> due;
        lock (gate) { due = timers.Where(t => t.Due <= now).ToList(); timers.RemoveAll(t => t.Due <= now); }
        foreach (var timer in due)
        {
            var user = bridge.SessionForPlayerId(timer.UserId);
            if (user != null && user.InWorld && ReferenceEquals(user, timer.Session)
                && user.GameId == timer.GameId && bridge.HasLinks(user.CurrentWorldId))
                bridge.SendFrame(user.CurrentWorldId, AS_REQUEST_ENTER_BATTLEFIELD, BitConverter.GetBytes(timer.UserId));
        }
    }

    public void CompleteTransfer(uint userId, int destination)
    {
        lock (gate) if (entered.TryGetValue((int)userId, out var active) && active.ReturnWorld == destination)
            entered.Remove((int)userId);
    }
    public void ForgetUser(ulong gameId)
    {
        lock (gate)
        {
            foreach (int id in offers.Where(x => x.Value.GameId == gameId).Select(x => x.Key).ToArray()) offers.Remove(id);
            foreach (int id in entered.Where(x => x.Value.GameId == gameId).Select(x => x.Key).ToArray()) entered.Remove(id);
            timers.RemoveAll(t => t.GameId == gameId);
        }
    }
    public void Clear() { lock (gate) { offers.Clear(); entered.Clear(); timers.Clear(); } }
    public void ForgetWorld(int world)
    {
        lock (gate)
        {
            foreach (int id in offers.Where(x => x.Value.Offer.OwnerWorld == world || x.Value.SourceWorld == world)
                .Select(x => x.Key).ToArray()) offers.Remove(id);
            foreach (int id in entered.Where(x => x.Value.OwnerWorld == world).Select(x => x.Key).ToArray()) entered.Remove(id);
            // CrossWorldHandoff cancels transfers to a disconnected destination.
            // Permit the still-live source BF to request return again after it recovers.
            foreach (int id in entered.Where(x => x.Value.ReturnWorld == world).Select(x => x.Key).ToArray())
                entered[id] = entered[id] with { ReturnWorld = null };
            timers.RemoveAll(t => t.OwnerWorld == world || t.SourceWorld == world);
        }
    }
}
