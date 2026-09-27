// SPDX-License-Identifier: MIT
// Copyright (c) 2026 the TeraSharp contributors

namespace TeraSharp.Arbiter.World;

/// <summary>
/// T192: SA_TELEPORT -> type-2 leave -> AS_ENTER_WORLD. cap_multiworld3 7882..7911;
/// Arb062:15151-15183, Arb030:6937-6986, Arb029:3675-3712, Arb028:15702-15755.
/// Keeps the live session data from AS_ENTER_WORLD; a transfer does not select a character again.
/// </summary>
public sealed class CrossWorldHandoff
{
    public const ushort SA_TELEPORT = 0x1445;
    public sealed record Teleport(ulong UserHandle, ulong GameId, int TargetWorld, int Continent,
        uint Channel, float X, float Y, float Z, short Direction, byte[] EtcData);
    public sealed record Pending(int SourceWorld, int DestinationWorld, Teleport Move, byte[] PreviousEnter);
    private sealed record Enter(int World, byte[] Payload);
    private readonly object gate = new();
    private readonly Dictionary<ulong, Enter> entered = new();
    private readonly Dictionary<ulong, Pending> pending = new();

    /// <summary>TargetWorld=-1 means resolve the destination. Main World's
    /// loadAllContinents owns continents absent from AS_WORLD_CHANNEL_INFO too:
    /// cap_instance1:153328/153336+150, main164D:136913 (no7005/7021 rows).</summary>
    public static int? ResolveDestination(Teleport move, Func<uint, int?> worldForChannel,
        Func<int, int?> worldForContinent, int? catchAllWorld)
        => move.TargetWorld >= 0 ? move.TargetWorld
            : worldForChannel(move.Channel) ?? worldForContinent(move.Continent) ?? catchAllWorld;

    public static Teleport? Parse(byte[] payload)
    {
        // The PDL fixed frame is 56 bytes; offset/length address its variable EtcData.
        if (payload.Length < 50) return null;
        uint offset = BitConverter.ToUInt32(payload, 0), length = BitConverter.ToUInt32(payload, 4);
        if (offset < 56 || offset - 6 > payload.Length || length > payload.Length - (offset - 6)) return null;
        return new(BitConverter.ToUInt64(payload, 8), BitConverter.ToUInt64(payload, 16),
            BitConverter.ToInt32(payload, 24), BitConverter.ToInt32(payload, 28),
            BitConverter.ToUInt32(payload, 32), BitConverter.ToSingle(payload, 36),
            BitConverter.ToSingle(payload, 40), BitConverter.ToSingle(payload, 44),
            BitConverter.ToInt16(payload, 48), payload.AsSpan((int)offset - 6, (int)length).ToArray());
    }

    public void RememberEnter(int world, byte[] payload)
    {
        if (payload.Length < 167) return;
        lock (gate) entered[BitConverter.ToUInt64(payload, 24)] = new(world, (byte[])payload.Clone());
    }

    /// <summary>T199: battleground transfers reuse the live enter fields and EtcData.</summary>
    public byte[]? EnterFor(ulong handle, int world)
    {
        lock (gate) return entered.TryGetValue(handle, out var entry) && entry.World == world
            ? (byte[])entry.Payload.Clone() : null;
    }

    /// <summary>QA StickTogether calls TeleportStart directly (Arb044:6400;030:6925), retaining the target's live context.</summary>
    public static Teleport? QaTeleport(byte[] previous, ulong handle, int continent, uint channel,
        float x, float y, float z, short direction)
    {
        if (previous.Length < 167 || BitConverter.ToUInt64(previous, 24) != handle) return null;
        uint offset = BitConverter.ToUInt32(previous, 8), count = BitConverter.ToUInt32(previous, 12);
        if (offset < 173 || offset - 6 > previous.Length || count > previous.Length - (offset - 6)) return null;
        return new(handle, BitConverter.ToUInt64(previous, 84), -1, continent, channel, x, y, z, direction,
            previous.AsSpan((int)offset - 6, (int)count).ToArray());
    }

    public bool Begin(int sourceWorld, int destinationWorld, Teleport move, uint playerId)
    {
        lock (gate)
        {
            if (!entered.TryGetValue(move.UserHandle, out var entry) || entry.World != sourceWorld
                || BitConverter.ToUInt32(entry.Payload, 32) != playerId
                || BitConverter.ToUInt64(entry.Payload, 84) != move.GameId
                || pending.ContainsKey(move.GameId)) return false;
            pending[move.GameId] = new(sourceWorld, destinationWorld, move, entry.Payload);
            return true;
        }
    }

    public Pending? Take(ulong gameId, int sourceWorld)
    {
        lock (gate)
        {
            if (!pending.TryGetValue(gameId, out var item) || item.SourceWorld != sourceWorld) return null;
            pending.Remove(gameId); return item;
        }
    }

    public void Forget(ulong handle)
    {
        lock (gate)
        {
            entered.Remove(handle);
            foreach (var key in pending.Where(p => p.Value.Move.UserHandle == handle).Select(p => p.Key).ToArray())
                pending.Remove(key);
        }
    }

    public void ForgetWorld(int world)
    {
        lock (gate)
        {
            foreach (var key in entered.Where(e => e.Value.World == world).Select(e => e.Key).ToArray()) entered.Remove(key);
            foreach (var key in pending.Where(p => p.Value.SourceWorld == world || p.Value.DestinationWorld == world)
                .Select(p => p.Key).ToArray()) pending.Remove(key);
        }
    }

    public void Clear() { lock (gate) { entered.Clear(); pending.Clear(); } }

    /// <summary>cap_multiworld3 7884: [gameId][LeaveWorldType=2][reason=0][UserDbId].</summary>
    public static byte[] BuildLeave(Teleport move, uint playerId)
    {
        var payload = new byte[20];
        BitConverter.GetBytes(move.GameId & 0x7FFFFFFFFFFFFFFFUL).CopyTo(payload, 0);
        BitConverter.GetBytes(2u).CopyTo(payload, 8);
        BitConverter.GetBytes(playerId).CopyTo(payload, 16);
        return payload;
    }

    /// <summary>cap_multiworld3 6486 + 7882 -> 7908, retaining all other session/account fields.</summary>
    public static byte[] BuildEnter(byte[] previous, Teleport move, uint ticket)
    {
        if (previous.Length < 167) throw new ArgumentException("AS_ENTER_WORLD fixed payload missing", nameof(previous));
        var payload = new byte[checked(167 + move.EtcData.Length)];
        previous.AsSpan(0, 167).CopyTo(payload);
        BitConverter.GetBytes(173u).CopyTo(payload, 8);
        BitConverter.GetBytes((uint)move.EtcData.Length).CopyTo(payload, 12);
        BitConverter.GetBytes(move.Continent).CopyTo(payload, 48);
        BitConverter.GetBytes(move.Channel).CopyTo(payload, 52);
        BitConverter.GetBytes(move.X).CopyTo(payload, 56);
        BitConverter.GetBytes(move.Y).CopyTo(payload, 60);
        BitConverter.GetBytes(move.Z).CopyTo(payload, 64);
        BitConverter.GetBytes(2u).CopyTo(payload, 68);
        BitConverter.GetBytes((int)move.Direction).CopyTo(payload, 72);
        BitConverter.GetBytes(ticket).CopyTo(payload, 80);
        move.EtcData.CopyTo(payload, 167);
        return payload;
    }

    /// <summary>Native LeaveWorldEnd commits the destination after source saves. 7903 -> 7911.</summary>
    public static byte[] StampLocation(byte[] blob, Teleport move, int destinationWorld)
    {
        var copy = (byte[])blob.Clone();
        if (copy.Length < 308) return copy;
        BitConverter.GetBytes(move.X).CopyTo(copy, 220);
        BitConverter.GetBytes(move.Y).CopyTo(copy, 224);
        BitConverter.GetBytes(move.Z).CopyTo(copy, 228);
        BitConverter.GetBytes(move.Continent).CopyTo(copy, 236);
        BitConverter.GetBytes(move.Channel).CopyTo(copy, 240);
        BitConverter.GetBytes(destinationWorld).CopyTo(copy, 244);
        BitConverter.GetBytes((int)move.Direction).CopyTo(copy, 304);
        return copy;
    }
}
