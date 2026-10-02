// SPDX-License-Identifier: MIT
// Copyright (c) 2026 the TeraSharp contributors

using TeraSharp.Arbiter.Auth;
using TeraSharp.Arbiter.Persistence;
using TeraSharp.Arbiter.Web;
using TeraSharp.Arbiter.World;

namespace TeraSharp.Arbiter.Tests;

// =============================================================================================
// T230 - the Item Claim panel on the api-gateway.
//
// 100.02 has no box packet: zero CLAIM opcodes across all five maps in data.json and zero
// CLAIM/GIFT defs among 917 schemas, so retail's Item Claim button is the Alt+A Awesomium web
// view (status/T230-ITEM-CLAIM.md sections 1-3). The page is therefore HTTP on the gateway, and
// these tests cover the three things that page does: list, claim, and the record it leaves.
//
// The ticket is the whole security boundary - it is the only thing separating one account's
// purchases from another's - so the refusals are tested first and hardest.
// =============================================================================================
public static partial class Tests
{
    /// <summary>Sets one <c>TERASHARP_*</c> variable for a test and puts it back.</summary>
    private sealed class T230Env : IDisposable
    {
        private readonly (string Name, string? Old)[] _old;
        internal T230Env(params (string Name, string? Value)[] set)
        {
            _old = set.Select(s => (s.Name, Environment.GetEnvironmentVariable(s.Name))).ToArray();
            foreach (var (name, value) in set) Environment.SetEnvironmentVariable(name, value);
        }
        public void Dispose()
        {
            foreach (var (name, old) in _old) Environment.SetEnvironmentVariable(name, old);
        }
    }

    const string T230Secret = "t230-portal-secret";

    /// <summary>A store with one account, one character, and a two-line box waiting for it.</summary>
    static CharacterStore T230Store(out long account, out int character, out long box,
                                    string name = "t230", int items = 2)
    {
        var store = new CharacterStore(":memory:", QuietLog());
        store.EnsureHubSchema();
        account = store.GetOrCreateAccount(name).Id;
        character = store.CreateCharacter(new CharacterRecord
        {
            AccountId = account, Name = name, Race = 1, Class = 2, Level = 60, TemplateId = 10101,
            Zone = 7005, Appearance = new byte[8], Details = new byte[32], Shape = new byte[64], Position = 1,
        });
        box = T230Box(store, account, items);
        return store;
    }

    static long T230Box(CharacterStore store, long account, int items, int characterId = 0)
    {
        var lines = new List<CharacterStore.HubBoxItemRow>();
        for (int i = 0; i < items; i++)
        {
            long sn = store.CreateHubServiceItem(88800 + i, "Item " + i, string.Empty);
            lines.Add(new CharacterStore.HubBoxItemRow(0, i, sn, 88800 + i, 2 + i));
        }
        return store.CreateHubBox(account, characterId, "Shop purchase", "Thank you", string.Empty,
                                  0, 0, "ext-" + account, lines);
    }

    static string T230Ticket(long account, long nowUnix)
        => ApiGatewayToken.Mint(account, nowUnix, System.Text.Encoding.UTF8.GetBytes(T230Secret));

    [Test] public static void T230_the_panel_is_scoped_by_a_verified_ticket_and_fails_closed()
    {
        using var store = T230Store(out long account, out int character, out long box);
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        string good = T230Ticket(account, now);
        Hex.True(character > 0 && box > 0, "one character and one box to start");

        // Fail closed: with no signing key configured the page serves nobody, however good the
        // ticket looks. This is the AuthProvider rule - a server anyone can read as anyone is
        // worse than a server with no panel.
        using (new T230Env((ApiGatewayToken.SecretVariable, null)))
        {
            long who = ItemClaimApi.AccountFor(good, now, out string why);
            Hex.True(who == 0 && why.Contains(ApiGatewayToken.SecretVariable, StringComparison.Ordinal),
                     "no configured key means no claim page, and the reason names the variable: " + why);
            var r = ItemClaimApi.Serve(store, "GET", "/itemclaim/list", null, good, now);
            Hex.True(r.Status == 401, $"and the endpoint is 401, got {r.Status}");
        }

        using var env = new T230Env((ApiGatewayToken.SecretVariable, T230Secret),
                                    (ItemClaimApi.MaxAgeVariable, "3600"));

        Hex.True(ItemClaimApi.AccountFor(good, now, out _) == account, "a freshly minted ticket names its account");
        Hex.True(ItemClaimApi.AccountFor(null, now, out string noTok) == 0 && noTok.Length > 0, "no ticket, no account");
        Hex.True(ItemClaimApi.AccountFor("not.a.jwt", now, out _) == 0, "a malformed ticket is refused");

        // A tampered payload: same header and signature, accountDbId re-pointed. This is the
        // attack the signature exists to stop.
        //
        // T230b: this read `Payload(1, ...)`, and `accounts` is last_insert_rowid on a fresh
        // :memory: store, so THIS account is 1 - the "forgery" was byte-identical to the real
        // ticket and verified, correctly. The harness that checked it used a hard-coded account 7
        // and so never saw it. Re-point to an id this store cannot have issued, and assert the
        // premise as well, so the case can never quietly stop being a forgery again.
        var seg = good.Split('.');
        long notThem = account + 1_000_000;
        string forged = seg[0] + "." + ApiGatewayToken.Base64Url(
            System.Text.Encoding.UTF8.GetBytes(ApiGatewayToken.Payload(notThem, now))) + "." + seg[2];
        Hex.True(notThem != account && forged.Split('.')[1] != seg[1] && forged.Split('.')[2] == seg[2],
                 "the re-pointed payload really is a different segment under the original signature");
        Hex.True(ItemClaimApi.AccountFor(forged, now, out string bad) == 0 && bad.Contains("verify", StringComparison.Ordinal),
                 "a re-pointed ticket does not verify: " + bad);

        // And one that needs no id at all: the real payload with a mangled signature. HMACSHA256
        // is 32 bytes, so both segments are 43 base64url chars and FixedTimeEquals compares them
        // rather than bailing on a length mismatch.
        string flipped = seg[0] + "." + seg[1] + "." + (seg[2][0] == 'A' ? 'B' : 'A') + seg[2][1..];
        Hex.True(flipped.Length == good.Length && flipped != good
                 && ItemClaimApi.AccountFor(flipped, now, out _) == 0,
                 "and neither does a ticket whose signature was touched");

        // exp is 120 s and unusable, so iat + MaxAge is the age that is enforced.
        Hex.True(ApiGatewayToken.LifetimeSeconds == 120, "the minted lifetime is still the unusable 120 s");
        Hex.True(ItemClaimApi.AccountFor(T230Ticket(account, now - 119), now, out _) == account,
                 "a ticket past its own exp still works - that is the point");
        Hex.True(ItemClaimApi.AccountFor(T230Ticket(account, now - 3601), now, out string stale) == 0
                 && stale.Contains("3600", StringComparison.Ordinal),
                 "but not one older than the configured max age: " + stale);
        using (new T230Env((ItemClaimApi.MaxAgeVariable, "60")))
            Hex.True(ItemClaimApi.AccountFor(T230Ticket(account, now - 61), now, out _) == 0
                     && ItemClaimApi.MaxAgeSeconds() == 60, "and the max age is configurable");
        Hex.True(ItemClaimApi.AccountFor(T230Ticket(account, now + 600), now, out _) == 0,
                 "a ticket from the future is refused");

        // The listing is this account's boxes and nobody else's.
        long other = store.GetOrCreateAccount("t230-other").Id;
        long otherBox = T230Box(store, other, 1);
        string list = ItemClaimApi.Listing(store, account);
        Hex.True(list.Contains("\"box\":" + box, StringComparison.Ordinal)
                 && !list.Contains("\"box\":" + otherBox, StringComparison.Ordinal),
                 "the listing holds this account's box and not the other account's: " + list);
        Hex.True(ItemClaimApi.Number(list, "accountDbId") == account, "and says whose it is");
        Hex.True(list.Contains("\"claimable\":true", StringComparison.Ordinal)
                 && list.Contains("\"templateId\":88800", StringComparison.Ordinal)
                 && list.Contains("\"amount\":3", StringComparison.Ordinal),
                 "with the lines the player is being offered");

        // Routing: the page needs no ticket (it fetches the list itself), the endpoints do.
        var page = ItemClaimApi.Serve(store, "GET", "/itemclaim", null, null, now);
        Hex.True(page.Status == 200 && page.ContentType.StartsWith("text/html", StringComparison.Ordinal)
                 && page.Body.Contains("Item Claim", StringComparison.Ordinal), "the page itself serves without a ticket");
        Hex.True(ItemClaimApi.Handles("/itemclaim") && ItemClaimApi.Handles("/itemclaim/list")
                 && !ItemClaimApi.Handles("/probe") && !ItemClaimApi.Handles("/"),
                 "and only our prefix is ours");
        Hex.True(ItemClaimApi.Serve(store, "GET", "/itemclaim/claim", "?box=" + box, good, now).Status == 405,
                 "claiming with GET is 405");
        Hex.True(ItemClaimApi.Serve(store, "POST", "/itemclaim/claim", null, good, now).Status == 400,
                 "and claiming without a box number is 400");
        Hex.True(ItemClaimApi.Serve(null, "GET", "/itemclaim/list", null, good, now).Status == 503,
                 "no store is 503, never an empty list - an empty list reads as 'my purchases are gone'");
    }

    [Test] public static void T230_an_offline_claim_fills_the_bag_once_and_only_once()
    {
        using var store = T230Store(out long account, out int character, out long box);
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        using var env = new T230Env((ApiGatewayToken.SecretVariable, T230Secret));
        string ticket = T230Ticket(account, now);
        Hex.True(store.CountInventoryItems(character) == 0, "an empty bag to start");

        var reply = ItemClaimApi.Serve(store, "POST", "/itemclaim/claim", "?box=" + box, ticket, now);
        Hex.True(reply.Status == 200 && reply.Body.Contains("\"via\":\"bag\"", StringComparison.Ordinal),
                 "an offline character is served through the bag: " + reply.Body);
        Hex.True(ItemClaimApi.Number(reply.Body, "items") == 2
                 && ItemClaimApi.Number(reply.Body, "characterId") == character,
                 "two items, to that character");

        // The rows are real and in the bag pocket, at the lowest free slots.
        var rows = store.GetInventoryItems(character).OrderBy(r => r.Slot).ToList();
        Hex.True(rows.Count == 2 && rows.All(r => r.InvenType == BoxClaim.BagPocket && r.ItemDbId > 0),
                 "two bag rows with allocated ids: " + string.Join(", ", rows.Select(r => $"{r.TemplateId}@{r.InvenType}:{r.Slot}")));
        Hex.True(rows[0].Slot == 0 && rows[1].Slot == 1, "in slots 0 and 1");
        Hex.True(rows.Any(r => r.TemplateId == 88800 && r.Amount == 2)
                 && rows.Any(r => r.TemplateId == 88801 && r.Amount == 3), "with the box's own templates and counts");
        Hex.True(rows[0].ItemDbId != rows[1].ItemDbId, "and distinct ids - the T105 collision, not repeated");

        // The consumption record: state 2, who took it, when. This IS the record of truth.
        var after = store.GetHubBox(box)!;
        Hex.True(after.State == CharacterStore.HubBoxClaimed && after.ClaimedBy == character
                 && after.ClaimedAt > 0 && after.ParcelId == 0,
                 $"state {after.State}, claimed by {after.ClaimedBy}, no parcel - it went straight to the bag");
        Hex.True(store.PendingHubBoxes().Count == 0, "and the parcel sweep will not touch it again");
        Hex.True(BoxDelivery.DeliverPending(store) == 0, "so no second copy arrives as mail");

        // Twice is the thing that must not work.
        var again = ItemClaimApi.Serve(store, "POST", "/itemclaim/claim", "?box=" + box, ticket, now);
        Hex.True(again.Status == 409 && again.Body.Contains("already claimed", StringComparison.Ordinal),
                 "a second claim is refused: " + again.Body);
        Hex.True(store.CountInventoryItems(character) == 2, "and hands out nothing");

        // Another account's box is "no such box" - the same answer as a box that does not exist,
        // so the page cannot be used to enumerate other people's purchases.
        long other = store.GetOrCreateAccount("t230-other").Id;
        long otherBox = T230Box(store, other, 1);
        var theirs = ItemClaimApi.Serve(store, "POST", "/itemclaim/claim", "?box=" + otherBox, ticket, now);
        var nothing = ItemClaimApi.Serve(store, "POST", "/itemclaim/claim", "?box=999999", ticket, now);
        Hex.True(theirs.Status == 409 && nothing.Status == 409 && theirs.Body == nothing.Body,
                 "another account's box and a box that does not exist answer identically: " + theirs.Body);
        Hex.True(store.GetHubBox(otherBox)!.State == CharacterStore.HubBoxPending, "and it is still theirs to claim");

        // A full bag refuses rather than dropping the purchase.
        long full = T230Box(store, account, 2);
        for (int slot = 2; slot < BoxClaim.BagSlots; slot++)
            store.UpsertItem(store.NextItemId(), character, BoxClaim.BagPocket, slot, 1000, 1);
        var cramped = ItemClaimApi.Serve(store, "POST", "/itemclaim/claim", "?box=" + full, ticket, now);
        Hex.True(cramped.Status == 409 && cramped.Body.Contains("room", StringComparison.Ordinal),
                 "a full bag is told so: " + cramped.Body);
        Hex.True(store.GetHubBox(full)!.State == CharacterStore.HubBoxPending,
                 "and the box stays claimable - a refused claim must never consume one");
    }

    [Test] public static void T230_a_claim_in_world_goes_through_the_mailbox()
    {
        using var store = T230Store(out long account, out int character, out long box);
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        // In world, World owns the bag: rows written behind it are lost on its next save, and it
        // originates the insert atoms rather than answering ours. The mailbox is the path that is
        // both immediate and safe - World's own claim transaction creates the item.
        var result = BoxClaim.Claim(store, account, box, isOnline: _ => true, QuietLog());
        Hex.True(result.Ok && result.Via == BoxClaim.Path.Mail && result.Items == 2,
                 $"claimed as mail, got {result.Via} / {result.Items} / {result.Problem}");
        Hex.True(store.CountInventoryItems(character) == 0, "nothing was written into the live bag");

        var parcel = store.GetParcel(result.ParcelId);
        Hex.True(parcel is not null && parcel.ReceiverDbId == character
                 && parcel.ParcelType == ParcelDbHandlers.ParcelTypeSystem && parcel.SenderDbId == 0,
                 "a system parcel for that character");
        Hex.True(store.GetParcelItems(result.ParcelId).Count == 2, "with both attachments");
        var record = store.GetParcelRecord(result.ParcelId)!;
        int at = SystemParcelAttachments.ParcelItemsOffset;
        Hex.True(BitConverter.ToInt32(record, at + 8) == 88800
                 && BitConverter.ToInt32(record, at + SystemParcelAttachments.ItemRecordSize + 8) == 88801,
                 "and the attachment slots World reads carry both templates");

        var after = store.GetHubBox(box)!;
        Hex.True(after.State == CharacterStore.HubBoxClaimed && after.ClaimedBy == character
                 && after.ParcelId == result.ParcelId,
                 $"state {after.State}, parcel {after.ParcelId} recorded against the claim");
        Hex.True(store.PendingHubBoxes().Count == 0 && BoxDelivery.DeliverPending(store) == 0,
                 "and the sweep cannot deliver it a second time");
        Hex.True(!BoxClaim.Claim(store, account, box, _ => true).Ok, "nor can a second claim");
    }

    [Test] public static void T230_the_parcel_sweep_is_still_the_fallback_with_the_gateway_off()
    {
        using var store = T230Store(out long account, out int character, out long box);
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        // Nothing about T230 changes the T207 path: with no page served, a box is swept into a
        // parcel and lands in state 1, which is a different state from a page claim on purpose -
        // the two surfaces give support different answers.
        Hex.True(BoxDelivery.DeliverPending(store) == 1, "the sweep delivers it");
        var after = store.GetHubBox(box)!;
        Hex.True(after.State == CharacterStore.HubBoxDelivered && after.ParcelId > 0
                 && after.ClaimedBy == 0 && after.ClaimedAt == 0,
                 $"state {after.State} with a parcel and no claimer - delivered, not claimed");
        Hex.True(store.GetParcel(after.ParcelId)!.ReceiverDbId == character, "to the account's character");

        // And a box the sweep already delivered is not offered by the page.
        using var env = new T230Env((ApiGatewayToken.SecretVariable, T230Secret));
        string list = ItemClaimApi.Listing(store, account);
        Hex.True(list.Contains("\"claimable\":false", StringComparison.Ordinal)
                 && list.Contains("\"state\":" + CharacterStore.HubBoxDelivered, StringComparison.Ordinal),
                 "the page shows it as already delivered rather than hiding it: " + list);
        Hex.True(ItemClaimApi.Serve(store, "POST", "/itemclaim/claim", "?box=" + box,
                                    T230Ticket(account, now), now).Status == 409,
                 "and refuses to claim it again");

        // A box with no lines closes rather than being swept forever.
        long empty = store.CreateHubBox(account, 0, "Empty", string.Empty, string.Empty, 0, 0, "e",
                                        Array.Empty<CharacterStore.HubBoxItemRow>());
        var closed = BoxClaim.Claim(store, account, empty, _ => false, QuietLog());
        Hex.True(closed.Ok && closed.Via == BoxClaim.Path.None && closed.Items == 0,
                 "an empty box is closed with nothing handed over");
        Hex.True(store.GetHubBox(empty)!.State == CharacterStore.HubBoxClaimed, "and does not come back");
    }
}
