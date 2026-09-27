// SPDX-License-Identifier: MIT
// Copyright (c) 2026 the TeraSharp contributors

using System.Text;

namespace TeraSharp.Arbiter.World;

/// <summary>
/// SA_MAKE_SYS_PARCEL's linked items become ParcelData.SendItemInfo[5], not inventory
/// transaction atoms. Arb_part_062.c:10195-10311; Arb_part_083.c:353-535, 733-781.
/// cap_final2b 6189 -> 28054/28068: achievement 1903 sends template 201100 x3.
/// </summary>
public static class SystemParcelAttachments
{
    public const int RequestSize = 37;
    public const int ItemWireSize = 43;
    public const int ItemRecordSize = 0x1B0;
    public const int ParcelItemsOffset = 0xD8;
    public const int ParcelMoneyOffset = 0x950;
    public const int ParcelMessageOffset = 0x9E8;
    public const int ParcelRecordSize = 0xDD8;

    /// <summary>
    /// Convert the request's items to the native, zero-initialized SendItemInfo records.
    /// The item DB id stays zero for these newly granted items; World's claim transaction
    /// allocates it (cap_final2b 28069 -> 28070). Never file an incomplete attachment list.
    /// </summary>
    public static bool TryRead(byte[] payload, out List<byte[]> items)
    {
        items = new();
        if (payload.Length < RequestSize) return false;
        uint count = ParcelDbHandlers.U32(payload, 0), next = ParcelDbHandlers.U32(payload, 4);
        if (count > (uint)(payload.Length / ItemWireSize)) return false;
        var seen = new HashSet<uint>();
        for (uint i = 0; i < count; i++)
        {
            if (!Element(payload, next, ItemWireSize, seen, out int at)) return false;
            int template = BitConverter.ToInt32(payload, at + 16);
            int amount = BitConverter.ToInt32(payload, at + 20);
            if (template <= 0 || amount <= 0) return false;
            // Native resolves this through ItemTemplateManager, not by copying the input.
            // The captured achievement/EP rewards have zero. Do not invent unidentified gear.
            if (ParcelDbHandlers.U32(payload, at + 29) != 0) return false;
            var item = new byte[ItemRecordSize];
            Buffer.BlockCopy(payload, at + 16, item, 8, 8); // template, amount
            item[16] = payload[at + 24];                   // Masterpiece
            Buffer.BlockCopy(payload, at + 25, item, 32, 4); // EnchantCount
            Buffer.BlockCopy(payload, at + 33, item, 40, 8); // CouponId
            item[52] = payload[at + 41];                   // BoundOnLoot

            uint options = ParcelDbHandlers.U32(payload, at + 8);
            uint optionNext = ParcelDbHandlers.U32(payload, at + 12);
            if (options > (uint)(payload.Length / 20)) return false;
            var optionSeen = new HashSet<uint>();
            for (uint j = 0; j < options; j++)
            {
                if (!Element(payload, optionNext, 20, optionSeen, out int o)) return false;
                uint page = ParcelDbHandlers.U32(payload, o + 8);
                uint index = ParcelDbHandlers.U32(payload, o + 12);
                if (page >= 2 || index >= 15) return false;
                item[56] = 1;
                Buffer.BlockCopy(payload, o + 16, item, 60 + (int)(page * 15 + index) * 4, 4);
                optionNext = ParcelDbHandlers.U32(payload, o + 4);
            }
            if (optionNext != 0) return false;
            items.Add(item);
            next = ParcelDbHandlers.U32(payload, at + 4);
        }
        return next == 0;
    }

    private static bool Element(byte[] payload, uint frameOffset, int size, HashSet<uint> seen, out int at)
    {
        at = 0;
        if (frameOffset < 6u + RequestSize || !seen.Add(frameOffset)) return false;
        uint offset = frameOffset - 6;
        if (offset > (uint)payload.Length || (uint)size > (uint)payload.Length - offset) return false;
        at = (int)offset;
        return ParcelDbHandlers.U32(payload, at) == frameOffset;
    }

    /// <summary>
    /// The full record is required on claim; list replies use its 0x9E8 prefix. The five
    /// attachment slots are in BOTH forms. Native unused/padding bytes remain zero rather
    /// than replaying another character's stack contents from the captured native records.
    /// </summary>
    public static byte[] BuildRecord(int parcelId, int receiverId, string receiverName,
        string writer, string title, string message, long money, IReadOnlyList<byte[]> items)
    {
        if (items.Count > Persistence.CharacterStore.MaxParcelAttachments)
            throw new ArgumentOutOfRangeException(nameof(items));
        var record = new byte[ParcelRecordSize];
        ParcelDbHandlers.BuildParcelDataNoMsg(parcelId, receiverId, 0, writer, receiverName, 0, title)
            .CopyTo(record, 0);
        BitConverter.GetBytes(ParcelDbHandlers.ParcelTypeSystem).CopyTo(record, ParcelDbHandlers.ParcelDataParcelType);
        BitConverter.GetBytes(money).CopyTo(record, ParcelMoneyOffset);
        for (int i = 0; i < items.Count; i++)
        {
            if (items[i].Length != ItemRecordSize) throw new ArgumentException("SendItemInfo size", nameof(items));
            items[i].CopyTo(record, ParcelItemsOffset + i * ItemRecordSize);
        }
        // wcsncpy_s(..., 0x1F5, ...): at most 500 UTF-16 code units plus NUL.
        Encoding.Unicode.GetBytes(message[..Math.Min(message.Length, 500)]).CopyTo(record, ParcelMessageOffset);
        return record;
    }
}
