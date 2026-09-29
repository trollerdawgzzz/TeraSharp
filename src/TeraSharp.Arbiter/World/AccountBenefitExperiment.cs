// SPDX-License-Identifier: MIT
// Copyright (c) 2026 the TeraSharp contributors

using TeraSharp.Arbiter.Persistence;

namespace TeraSharp.Arbiter.World;

/// <summary>
/// The SDB_LOAD_ACCOUNT_BENEFIT reply (0x28BC) and the expiry rule that goes with it.
///
/// <para><b>T219c: the T181 operator seeding is gone.</b> It wrote three cap_final2b packages -
/// 533, 534 and 1000 - onto every listed operator account, and package 1000's captured expiry is
/// 0x6A800E6F = 2026-08-15, already in the past. T182 found the teleport rule elsewhere, so the
/// experiment proved nothing and cost this: World's <c>User::OnTickAccountTrait</c> runs
/// <c>AccountTrait::CheckAccountBenefitInterval</c> every tenth tick, <c>GetExpiredPackges</c>
/// hands the expired ids to <c>AccountTrait::DeletePropertyList</c>, and that resolves each id in
/// the UserTrait datasheet. 1000 is not a user trait, so it logs <c>Unknown user trait 1000</c>,
/// asserts at <c>AccountTrait.cpp(523)</c> and leaves the delete loop - and the package is still
/// there next interval. It is per ACCOUNT, which is why every character on account 1 failed and
/// account 2 was fine, and it is DB state, which is why the ae1b8b5 rollback changed nothing.</para>
/// </summary>
public static class AccountBenefitExperiment
{
    /// <summary>
    /// T219c. An expired package must never reach World - see the class summary. The handler drops
    /// these before building the reply rather than trusting the row to be cleaned up later.
    /// </summary>
    public static bool IsLive(CharacterStore.AccountBenefitRow row, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(row);
        return row.ExpiresAt > now.ToUnixTimeSeconds();
    }

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
