// SPDX-License-Identifier: MIT
// Copyright (c) 2026 the TeraSharp contributors

using System.Buffers.Binary;

namespace TeraSharp.Arbiter.World;

/// <summary>
/// MatchServer statistics forwarded by Arbiter to World for the dungeon/battleground browser.
/// Arb039:5230-5344 writes AS1644: 21-byte matching rows with nested 10-byte role rows.
/// World:694867-694959 copies their status bytes into F732; these are not queue member counts.
/// </summary>
public static class MatchBrowsePackets
{
    public readonly record struct RoleStatus(byte RoleMask, byte Status);
    public sealed record MatchingStatus(int MatchingId, byte MatchTimeStatus, IReadOnlyList<RoleStatus> Roles);

    /// <summary>
    /// Payload (six-byte control header excluded). Every linked offset is full-frame-relative.
    /// The caller supplies measured statistics; this writer invents neither activity nor time.
    /// Empty input preserves cap_social's 18-byte request, including a zero list offset.
    /// </summary>
    public static byte[] BuildExtendedList(int userDbId, IReadOnlyList<MatchingStatus> rows)
    {
        int frameSize = 18;
        foreach (var row in rows) frameSize = checked(frameSize + 21 + checked(row.Roles.Count * 10));
        if (frameSize > ushort.MaxValue) throw new ArgumentOutOfRangeException(nameof(rows));
        var payload = new byte[frameSize - 6];
        static void Put(byte[] p, int frameOffset, int value)
            => BinaryPrimitives.WriteInt32LittleEndian(p.AsSpan(frameOffset - 6, 4), value);
        Put(payload, 6, rows.Count);
        Put(payload, 10, rows.Count == 0 ? 0 : 18);
        Put(payload, 14, userDbId);
        int offset = 18;
        for (int i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            int next = offset + 21 + row.Roles.Count * 10;
            Put(payload, offset, offset);
            Put(payload, offset + 4, i + 1 < rows.Count ? next : 0);
            Put(payload, offset + 8, row.Roles.Count);
            Put(payload, offset + 12, row.Roles.Count == 0 ? 0 : offset + 21);
            Put(payload, offset + 16, row.MatchingId);
            payload[offset + 20 - 6] = row.MatchTimeStatus;
            for (int j = 0, roleOffset = offset + 21; j < row.Roles.Count; j++, roleOffset += 10)
            {
                Put(payload, roleOffset, roleOffset);
                Put(payload, roleOffset + 4, j + 1 < row.Roles.Count ? roleOffset + 10 : 0);
                payload[roleOffset + 8 - 6] = row.Roles[j].RoleMask;
                payload[roleOffset + 9 - 6] = row.Roles[j].Status;
            }
            offset = next;
        }
        return payload;
    }
}
