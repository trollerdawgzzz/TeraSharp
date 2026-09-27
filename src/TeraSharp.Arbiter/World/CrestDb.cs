// SPDX-License-Identifier: MIT
// Copyright (c) 2026 the TeraSharp contributors

namespace TeraSharp.Arbiter.World;

public sealed partial class DbProxyHandlers
{
    /// <summary>Native Arb028:15512–15545 sends separately loaded extra crest points at full+0xA7;
    /// World:2985690 reads that field. Refresh every entry, including a transfer's cached payload.</summary>
    public void StampEnterWorldCrestPoints(byte[] payload)
    {
        if (_store == null || payload.Length < 165) return;
        int playerId = BitConverter.ToInt32(payload, 32);
        if (playerId <= 0) return;
        var points = _store.GetKnownCrestPoints(playerId);
        if (points is { } known) BitConverter.GetBytes(known.ExPoint).CopyTo(payload, 161);
    }

    private bool OnCrestUse(WorldLink link, byte[] payload)
    {
        var ack = BuildReqIdAck(payload, CrestUseReqDlmId);
        bool ok = payload.Length >= 17;
        if (ok && _store != null)
        {
            int player = PlayerIdForGameId?.Invoke(BitConverter.ToUInt64(payload, 0)) ?? 0;
            ok = player > 0 && _store.SetCrestUse(player, new[] { BitConverter.ToInt32(payload, 12) },
                replace: false, applied: payload[16] != 0);
        }
        ack[4] = (byte)(ok ? 1 : 0);
        link.SendFrame(AS_CREST_USE, ack);
        return true;
    }

    private bool OnCrestUseList(WorldLink link, byte[] payload)
    {
        var ack = BuildCrestUseListReply(payload);
        var ids = ReadCrestUseList(payload);
        bool ok = ids != null;
        if (ok && _store != null)
        {
            int player = PlayerIdForGameId?.Invoke(BitConverter.ToUInt64(payload, 16)) ?? 0;
            ok = player > 0 && _store.SetCrestUse(player, ids!, replace: true);
        }
        ack[12] = (byte)(ok ? 1 : 0);
        link.SendFrame(AS_CREST_USE_LIST, ack);
        return true;
    }

    // Arb028:10500–10640: [count][first][atomsRef][atomsBytes][User handle][DlmId],
    // followed by 12-byte [here][next][crestId] entries. Empty list explicitly clears all.
    public static IReadOnlyList<int>? ReadCrestUseList(byte[] payload)
    {
        if (payload.Length < 28) return null;
        uint count = BitConverter.ToUInt32(payload, 0), offset = BitConverter.ToUInt32(payload, 4);
        if (count > Persistence.CharacterStore.CrestBlobSlots) return null;
        if (count == 0) return offset == 0 ? Array.Empty<int>() : null;
        var ids = new List<int>((int)count);
        var visited = new HashSet<uint>();
        for (uint i = 0; i < count; i++)
        {
            if (offset < 34 || (ulong)offset + 6 > (ulong)payload.Length || !visited.Add(offset)) return null;
            int at = checked((int)offset - 6);
            if (BitConverter.ToUInt32(payload, at) != offset) return null;
            int id = BitConverter.ToInt32(payload, at + 8);
            if (id <= 0) return null;
            ids.Add(id);
            offset = BitConverter.ToUInt32(payload, at + 4);
        }
        return offset == 0 ? ids : null;
    }
}
