// SPDX-License-Identifier: MIT
// Copyright (c) 2026 the TeraSharp contributors

using Microsoft.Extensions.Logging;
using TeraSharp.Arbiter.Persistence;

namespace TeraSharp.Arbiter.World;

// =============================================================================================
// BoxDelivery - T207. A platform box becomes a system parcel.
//
// 100.02 has NO box packet: there is no C_/S_ITEM_CLAIM and no box list in the client's opcode
// map or in the Arbiter's tables, so the retail "Item Claim" button is the Awesomium web view and
// the items themselves arrive from the platform side. The in-game surface we do have for "here
// are items you have not collected yet" is the system parcel that SA_MAKE_SYS_PARCEL writes, so a
// box is delivered as one: same parcels table, same claim transaction, survives a relog, and the
// player collects it from the mailbox (status/T207-HUB.md section 3).
//
// A box with more than MaxParcelAttachments lines becomes several parcels, because the record has
// five attachment slots (SystemParcelAttachments.ParcelItemsOffset) and dropping the sixth item
// would lose a purchase.
// =============================================================================================
public static class BoxDelivery
{
    /// <summary>
    /// The mail sender shown for a shop delivery. Plain text on purpose: the localisation keys
    /// (<c>@Achievement:6903</c> and friends) are the client's own, and inventing one shows blank.
    /// </summary>
    public const string Sender = "Shop";

    /// <summary>What a delivery attempt did, for the log and for the tests.</summary>
    public readonly record struct Result(int Parcels, int Items, int CharacterId)
    {
        /// <summary>True when the box left the pending state.</summary>
        public bool Delivered => Parcels > 0;
    }

    /// <summary>
    /// Deliver one box. Returns an undelivered result when the account has no character yet - the
    /// box stays pending and the next sweep (or the next BoxNotiUser) tries again, which is what
    /// happens when a purchase is made before the buyer ever logged in.
    /// </summary>
    public static Result Deliver(CharacterStore store, CharacterStore.HubBoxRow box, ILogger? log = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(box);

        int characterId = TargetCharacter(store, box);
        if (characterId <= 0)
        {
            log?.LogInformation(
                "hub: box {Box} for account {Account} has no character to deliver to yet - left pending",
                box.BoxSn, box.AccountId);
            return new(0, 0, 0);
        }

        var target = store.GetCharacter(characterId);
        if (target is null) return new(0, 0, 0);

        var lines = store.GetHubBoxItems(box.BoxSn);
        if (lines.Count == 0)
        {
            // Nothing to hand over: close the box rather than sweeping it forever.
            store.MarkHubBoxDelivered(box.BoxSn, 0);
            log?.LogWarning("hub: box {Box} has no items - marked delivered with no parcel", box.BoxSn);
            return new(0, 0, characterId);
        }

        string title = box.Title.Length > 0 ? box.Title : Sender;
        int parcels = 0, delivered = 0, lastParcel = 0;
        for (int start = 0; start < lines.Count; start += CharacterStore.MaxParcelAttachments)
        {
            var slice = new List<CharacterStore.HubBoxItemRow>();
            for (int i = start; i < lines.Count && slice.Count < CharacterStore.MaxParcelAttachments; i++)
                slice.Add(lines[i]);

            int parcelId = store.CreateParcel(0, Sender, characterId, title, box.Content, 0,
                ParcelDbHandlers.ParcelTypeSystem);
            if (parcelId <= 0) continue;

            var records = new List<byte[]>();
            for (int slot = 0; slot < slice.Count; slot++)
            {
                records.Add(ItemRecord(slice[slot].TemplateId, slice[slot].Amount));
                store.AddParcelItem(parcelId, slot, 0, slice[slot].TemplateId, slice[slot].Amount);
            }
            store.SetParcelRecord(parcelId,
                SystemParcelAttachments.BuildRecord(parcelId, characterId, target.Name, Sender,
                    title, box.Content, 0, records));

            parcels++;
            delivered += slice.Count;
            lastParcel = parcelId;
        }

        if (parcels > 0) store.MarkHubBoxDelivered(box.BoxSn, lastParcel);
        log?.LogInformation(
            "hub: box {Box} delivered to character {Char} as {Parcels} parcel(s), {Items} item(s)",
            box.BoxSn, characterId, parcels, delivered);
        return new(parcels, delivered, characterId);
    }

    /// <summary>Every pending box, oldest first. Returns how many boxes left the queue.</summary>
    public static int DeliverPending(CharacterStore store, long accountId = 0, ILogger? log = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        int done = 0;
        foreach (var box in store.PendingHubBoxes(accountId))
            if (Deliver(store, box, log).Delivered) done++;
        return done;
    }

    /// <summary>
    /// Who gets the parcel: the character tera-api named, when it belongs to the paying account,
    /// else the account's most recently played one. A box is bought per account, and retail lets
    /// any of the account's characters claim it, so "the one the buyer plays" is the closest a
    /// per-character mailbox can get.
    /// </summary>
    public static int TargetCharacter(CharacterStore store, CharacterStore.HubBoxRow box)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(box);
        var characters = store.GetCharacters(box.AccountId);
        if (characters.Count == 0) return 0;
        if (box.CharacterId > 0)
            foreach (var c in characters) if (c.Id == box.CharacterId) return c.Id;
        var best = characters[0];
        foreach (var c in characters) if (c.LastLogin > best.LastLogin) best = c;
        return best.Id;
    }

    /// <summary>
    /// One SendItemInfo record: template and amount only, the shape SA_MAKE_SYS_PARCEL's own
    /// attachments have (SystemParcelAttachments.TryRead - template at +8, amount at +12, and the
    /// item db id stays zero until World's claim transaction allocates it).
    /// </summary>
    public static byte[] ItemRecord(int templateId, long amount)
    {
        var item = new byte[SystemParcelAttachments.ItemRecordSize];
        BitConverter.GetBytes(templateId).CopyTo(item, 8);
        BitConverter.GetBytes((int)Math.Clamp(amount, 1, int.MaxValue)).CopyTo(item, 12);
        return item;
    }
}
