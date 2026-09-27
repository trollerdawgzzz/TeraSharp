// SPDX-License-Identifier: MIT
// Copyright (c) 2026 the TeraSharp contributors

using TeraSharp.Arbiter.Persistence;

namespace TeraSharp.Arbiter.World;

/// <summary>T181 operator-only experiment. These are literal cap_final2b 171 values, not player
/// defaults or evidence that packages cause @3301. Do not refresh the captured expiry dates.</summary>
public static class AccountBenefitExperiment
{
    public static readonly (int PackageId, long ExpiresAt)[] Seeds =
    {
        (533, 0x6ACF286F), (534, 0x7D31B36F), (1000, 0x6A800E6F)
    };

    /// <summary>Arb_part_063.c:12662 load writer, Arb_part_032.c:163 GetPackageInfoList.
    /// Each 16-byte struct is i32 package, 4 padding bytes, i64 expiry. The padding local
    /// uStack_54 is never assigned. 0x55 reproduces frame 171; it is not a benefit value.</summary>
    public static byte[] BuildReply(uint dlmId, bool found, IReadOnlyList<CharacterStore.AccountBenefitRow> rows)
    {
        int count = found ? rows.Count : 0;
        var payload = new byte[13 + count * 16];
        BitConverter.TryWriteBytes(payload.AsSpan(0, 4), 19u);
        BitConverter.TryWriteBytes(payload.AsSpan(4, 4), count * 16);
        BitConverter.TryWriteBytes(payload.AsSpan(8, 4), dlmId);
        payload[12] = found ? (byte)1 : (byte)0;
        for (int i = 0; i < count; i++)
        {
            int offset = 13 + i * 16;
            BitConverter.TryWriteBytes(payload.AsSpan(offset, 4), rows[i].PackageId);
            BitConverter.TryWriteBytes(payload.AsSpan(offset + 4, 4), 0x55);
            BitConverter.TryWriteBytes(payload.AsSpan(offset + 8, 8), rows[i].ExpiresAt);
        }
        return payload;
    }
}
