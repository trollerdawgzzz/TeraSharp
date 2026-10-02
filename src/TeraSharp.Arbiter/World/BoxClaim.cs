// SPDX-License-Identifier: MIT
// Copyright (c) 2026 the TeraSharp contributors

using Microsoft.Extensions.Logging;
using TeraSharp.Arbiter.Persistence;

namespace TeraSharp.Arbiter.World;

// =============================================================================================
// BoxClaim - T230. A platform box claimed from the Item Claim page, into the bag.
//
// T207 delivers a box as a system parcel because 100.02 has no box packet at all (T230 section 1:
// zero CLAIM opcodes in all five maps, zero CLAIM/GIFT defs in 917 schemas). The retail surface
// is the Alt+A Awesomium panel, which is HTTP - so the page is served on the api-gateway and this
// is what the page's claim button lands on.
//
// WHERE THE ITEMS GO. The Arbiter cannot insert into a LIVE bag. SDB_USER_LOAD_INVENTORY (0x27A2)
// is the enter-world load and World owns the bag from then on; it originates the insert atoms, we
// only answer them. So there are exactly two honest paths, and which one runs is decided by
// whether the target character is in world:
//
//   offline -> rows straight into `items` at free bag slots. The next SDB_USER_LOAD_INVENTORY
//              serves them, which is the brief's "offline: on next login", and nothing can race
//              us for the bag because nobody holds it.
//   online  -> the T207 system parcel. The player collects it from the mailbox NOW, without a
//              relog, and the item is created by World's own claim transaction - the normal
//              insert path, performed by the side that owns the bag. Writing rows behind a live
//              World would be lost on its next save.
//
// The brief asked for a live insert "via the item create path"; there is no such path we can
// originate (status/T230-ITEM-CLAIM.md section 7.2). The mailbox hop is the closest thing that is
// both immediate and safe, and `Result.Via` says which path ran so the page can tell the player.
// =============================================================================================
public static class BoxClaim
{
    /// <summary>Where a claim put the items.</summary>
    public enum Path
    {
        /// <summary>Nothing was handed over.</summary>
        None = 0,
        /// <summary>Rows in the bag; the next login serves them.</summary>
        Bag = 1,
        /// <summary>A system parcel, collectable immediately.</summary>
        Mail = 2,
    }

    /// <summary>What a claim did. <see cref="Ok"/> is false for every refusal.</summary>
    public readonly record struct Result(bool Ok, string Problem, Path Via, int Items, int CharacterId, int ParcelId)
    {
        public static Result Refused(string why) => new(false, why, Path.None, 0, 0, 0);
    }

    /// <summary>The bag pocket a claimed item lands in (INVEN_TYPE 0, MAIL-WAREHOUSE section 6.4).</summary>
    public const int BagPocket = 0;

    /// <summary>How far up the bag a free slot is looked for before the claim gives up.</summary>
    public const int BagSlots = 72;

    /// <summary>
    /// Claim one box for one account. <paramref name="accountDbId"/> is the ticket's scope and is
    /// checked against the row, so a forged box number cannot reach another account's purchase.
    /// </summary>
    public static Result Claim(CharacterStore store, long accountDbId, long boxSn,
                              Func<int, bool>? isOnline = null, ILogger? log = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        var box = store.GetHubBox(boxSn);
        if (box is null) return Result.Refused("no such box");
        if (box.AccountId != accountDbId) return Result.Refused("no such box");   // never says whose
        if (box.State != CharacterStore.HubBoxPending) return Result.Refused("already claimed");

        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (box.StartAt > 0 && now < box.StartAt) return Result.Refused("not claimable yet");
        if (box.EndAt > 0 && now > box.EndAt) return Result.Refused("expired");

        int characterId = BoxDelivery.TargetCharacter(store, box);
        if (characterId <= 0) return Result.Refused("this account has no character yet");

        var lines = store.GetHubBoxItems(box.BoxSn);
        if (lines.Count == 0)
        {
            store.MarkHubBoxClaimed(box.BoxSn, characterId);
            log?.LogWarning("item-claim: box {Box} has no items - closed with nothing to hand over", box.BoxSn);
            return new(true, string.Empty, Path.None, 0, characterId, 0);
        }

        bool online = isOnline?.Invoke(characterId)
                      ?? global::TeraSharp.Arbiter.Program.World?.SessionForPlayerId(characterId) is not null;
        return online ? ViaMail(store, box, characterId, lines.Count, log)
                      : ViaBag(store, box, characterId, lines, log);
    }

    /// <summary>Offline: the rows go in now and the next inventory load serves them.</summary>
    private static Result ViaBag(CharacterStore store, CharacterStore.HubBoxRow box, int characterId,
                                 IReadOnlyList<CharacterStore.HubBoxItemRow> lines, ILogger? log)
    {
        var free = FreeBagSlots(store, characterId, lines.Count);
        if (free.Count < lines.Count)
            return Result.Refused("not enough room in the bag - make space and claim again");

        // The row has to exist before the box leaves pending, and the box has to leave pending
        // before anything is handed over: claim first, so a crash between the two loses nothing
        // but leaves the box claimable. MarkHubBoxClaimed is the state=0 guard, so this is also
        // what stops a double claim handing the items out twice.
        if (!store.MarkHubBoxClaimed(box.BoxSn, characterId))
            return Result.Refused("already claimed");

        int first = store.ReserveItemIds(lines.Count);
        for (int i = 0; i < lines.Count; i++)
            store.UpsertItem(first + i, characterId, BagPocket, free[i], lines[i].TemplateId,
                             Math.Max(1L, lines[i].Amount));

        log?.LogInformation(
            "item-claim: box {Box} claimed by account {Account} -> {N} item(s) into character {Char}'s bag, slots {Slots} (T230)",
            box.BoxSn, box.AccountId, lines.Count, characterId, string.Join(",", free));
        return new(true, string.Empty, Path.Bag, lines.Count, characterId, 0);
    }

    /// <summary>Online: World owns the bag, so the items go through the mailbox it reads live.</summary>
    private static Result ViaMail(CharacterStore store, CharacterStore.HubBoxRow box, int characterId,
                                  int lineCount, ILogger? log)
    {
        if (!store.MarkHubBoxClaimed(box.BoxSn, characterId))
            return Result.Refused("already claimed");

        // Deliver reads the box row again and would see state 2, so the parcel is written here
        // from the same pieces: the box is already out of pending and must not be swept.
        var result = BoxDelivery.DeliverClaimed(store, box, characterId, log);
        if (result.Parcels == 0)
        {
            log?.LogWarning("item-claim: box {Box} claimed but no parcel was written - still claimed, nothing lost",
                box.BoxSn);
            return new(true, string.Empty, Path.None, 0, characterId, 0);
        }
        store.SetHubBoxParcel(box.BoxSn, result.ParcelId);
        log?.LogInformation(
            "item-claim: box {Box} claimed by account {Account} -> {N} item(s) to character {Char} as parcel {Parcel}, in world (T230)",
            box.BoxSn, box.AccountId, result.Items, characterId, result.ParcelId);
        return new(true, string.Empty, Path.Mail, result.Items, characterId, result.ParcelId);
    }

    /// <summary>
    /// The lowest <paramref name="want"/> free bag slots. Equipped gear is pocket 14 and the
    /// warehouse 9, so only pocket 0 is consulted (INVEN_TYPE, MAIL-WAREHOUSE section 6.4).
    /// </summary>
    public static List<int> FreeBagSlots(CharacterStore store, int characterId, int want)
    {
        ArgumentNullException.ThrowIfNull(store);
        var taken = new HashSet<int>();
        foreach (var row in store.GetInventoryItems(characterId))
            if (row.InvenType == BagPocket) taken.Add(row.Slot);
        var free = new List<int>();
        for (int slot = 0; slot < BagSlots && free.Count < want; slot++)
            if (!taken.Contains(slot)) free.Add(slot);
        return free;
    }
}
