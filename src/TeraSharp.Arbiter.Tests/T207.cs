// SPDX-License-Identifier: MIT
// Copyright (c) 2026 the TeraSharp contributors

using TeraSharp.Arbiter.Handlers;
using TeraSharp.Arbiter.Persistence;
using TeraSharp.Arbiter.Web;
using TeraSharp.Arbiter.World;

namespace TeraSharp.Arbiter.Tests;

/// <summary>
/// T207 - TERA Shop and item delivery. TeraSharp answers tera-api's hub socket itself, so a
/// purchase becomes a system parcel and the retail arb_gw gateway is not needed.
///
/// <para>Every request below is built the way tera-api builds it: the framing of its
/// hubConnection.js, the arguments of its hubFunctions.js and the protobuf field numbers of its
/// generated modules. That is the contract we have to satisfy, so these tests break if either side
/// of it moves. What is pinned and what is convention: status/T207-HUB.md section 6.</para>
/// </summary>
public static partial class Tests
{
    // =======================================================================================
    // T207 - the platform hub: a tera-api purchase becomes a system parcel.
    //
    // The requests below are built the way tera-api builds them (lib/hubConnection.js framing,
    // hubFunctions.js arguments, the protobuf field numbers of its generated modules), so these
    // tests fail if either side of that contract is edited. status/T207-HUB.md has the format.
    // =======================================================================================

    /// <summary>A hub with no socket and no live server behind it.</summary>
    static HubServer HubFor(TeraSharp.Arbiter.Persistence.CharacterStore store, HubServer.Hooks? hooks = null)
        => HubServer.ForTests(store, hooks ?? new HubServer.Hooks(), QuietLog());

    /// <summary>A SendMessageReq frame carrying one inner call, as tera-api's sendMessage writes it.</summary>
    static byte[] HubCall(ulong jobId, uint target, ushort innerId, byte[] inner)
    {
        var body = new HubProtocol.Writer().Fixed64(1, jobId).Fixed32(2, target)
            .Bytes(3, HubProtocol.Inner(innerId, inner));
        return HubProtocol.Frame(HubProtocol.SendMessageReq, body.ToArray());
    }

    /// <summary>An opmsg for one box-API function with tera-api's name/value arguments.</summary>
    static byte[] HubOpMsg(int function, params (string Name, string Value)[] arguments)
    {
        var w = new HubProtocol.Writer()
            .Fixed32(1, HubProtocol.Gusid(HubProtocol.CategoryWebCsTool, 0))
            .Fixed32(2, HubProtocol.Gusid(HubProtocol.CategoryBoxApi, 0))
            .Varint(3, 1)
            .Fixed32(5, HubProtocol.Gusid(HubProtocol.CategoryBoxApi, function))
            .Varint(6, 1);
        foreach (var (name, value) in arguments)
            w.Bytes(9, new HubProtocol.Writer().Text(1, name).Text(2, value).ToArray());
        return w.ToArray();
    }

    /// <summary>The answer a call produced: its inner id and protobuf, or null when refused.</summary>
    static (ushort AnsId, List<HubProtocol.Field> Fields)? HubAnswer(HubServer hub, byte[] frame)
    {
        var replies = hub.Handle(frame);
        Hex.True(replies.Count >= 1, "every call gets at least a SendMessageAns");
        var ack = HubProtocol.Parse(HubProtocol.FrameBody(replies[0]));
        Hex.True(HubProtocol.FrameId(replies[0]) == HubProtocol.SendMessageAns, "first reply is SendMessageAns");
        bool ok = HubProtocol.Scalar(ack, 4) != 0;
        if (!ok) { Hex.True(replies.Count == 1, "a refused call sends no answer"); return null; }
        Hex.True(replies.Count == 2 && HubProtocol.FrameId(replies[1]) == HubProtocol.RecvMessageReq,
            "an accepted call answers with RecvMessageReq");
        var recv = HubProtocol.Parse(HubProtocol.FrameBody(replies[1]));
        byte[] msgBuf = HubProtocol.Bytes(recv, 3);
        return (HubProtocol.InnerId(msgBuf), HubProtocol.Parse(HubProtocol.InnerBody(msgBuf)));
    }

    /// <summary>The opmsg answer's resultScalar - the new box or service-item id, as text.</summary>
    static string HubScalar((ushort AnsId, List<HubProtocol.Field> Fields)? answer)
    {
        Hex.True(answer is not null, "the call was refused");
        Hex.True(HubProtocol.GusidNumber((uint)HubProtocol.Scalar(answer!.Value.Fields, 10)) == 0,
            "resultCode number 0 is what tera-api reads as success");
        return System.Text.Encoding.UTF8.GetString(HubProtocol.Bytes(answer!.Value.Fields, 11));
    }

    /// <summary>tera-api's convertBoxTagValue output for one item and the three box tags.</summary>
    static (string Items, string Tags) HubBoxStrings(long serviceItemSn, long count, string title, string content)
    {
        static string Hex8(string text) => Convert.ToHexString(System.Text.Encoding.UTF8.GetBytes(text)).ToLowerInvariant();
        return ($"1,{serviceItemSn},0,1,1,{Hex8(count.ToString())}",
                $"3,1,{Hex8(content)},2,{Hex8(title)},3,{Hex8("icon")}");
    }

    [Test]
    public static void T207_register_is_answered_with_this_server_and_the_box_api()
    {
        using var store = GuildStore(1);
        var hub = HubFor(store);
        // RegisterReq: serverId (fixed32 1) = category webcstool, number 0. tera-api sends no more.
        var request = HubProtocol.Frame(HubProtocol.RegisterReq,
            new HubProtocol.Writer().Fixed32(1, HubProtocol.Gusid(HubProtocol.CategoryWebCsTool, 0)).ToArray());
        var replies = hub.Handle(request);
        Hex.True(replies.Count == 1 && HubProtocol.FrameId(replies[0]) == HubProtocol.RegisterAns,
            "one RegisterAns");
        // The frame's own size prefix counts itself: 2 + 2 + body.
        Hex.True(replies[0][0] + (replies[0][1] << 8) == replies[0].Length, "size prefix counts itself");
        var fields = HubProtocol.Parse(HubProtocol.FrameBody(replies[0]));
        Hex.True(HubProtocol.Scalar(fields, 1) == 1, "result true, or tera-api retries forever");
        var announced = new List<uint>();
        foreach (var f in fields) if (f.Number == 2) announced.Add((uint)f.Varint);
        Hex.True(announced.Contains(hub.Gusid) && announced.Contains(HubProtocol.BoxApiGusid),
            "the server list names this arbiter and the box API");
    }

    [Test]
    public static void T207_a_tera_api_purchase_arrives_as_a_system_parcel()
    {
        using var store = GuildStore(1);
        var online = new List<string>();
        var hub = HubFor(store, new HubServer.Hooks
        {
            IsOnline = _ => true,
            Notify = (_, text) => online.Add(text),
        });
        long account = store.GetCharacter(1)!.AccountId;

        // 1. CreateServiceItem (gufid 117): the catalogue entry for item template 88888.
        string serviceItemSn = HubScalar(HubAnswer(hub, HubCall(1, HubProtocol.BoxApiGusid, HubProtocol.OpMsg,
            HubOpMsg(HubProtocol.FnCreateServiceItem,
                ("serviceItemServiceSN", "1"), ("serviceItemMappingItemSN", "88888"),
                ("serviceItemStartActivationDateTime", "2026-09-27 01:00:00"),
                ("serviceItemEnableFlag", "1"), ("serviceItemName", "Test Item"),
                ("serviceItemDescription", "a test"), ("serviceItemRegisterUserSN", "0"),
                ("serviceItemTagInfo", "1,1,1")))));
        Hex.True(serviceItemSn == "1", $"first service item is 1, got {serviceItemSn}");

        // 2. getByTemplateId asks GetPageServiceItem before creating another one.
        var page = HubAnswer(hub, HubCall(2, HubProtocol.BoxApiGusid, HubProtocol.OpMsg,
            HubOpMsg(HubProtocol.FnGetPageServiceItem, ("offset", "0"), ("count", "1"),
                ("serviceItemMappingItemSN", "88888"), ("serviceItemEnableFlag", "1"),
                ("sort", "serviceItemSN"), ("dir", "desc"))));
        var set = HubProtocol.Parse(HubProtocol.Bytes(page!.Value.Fields, 12));
        var row = HubProtocol.Parse(HubProtocol.Bytes(set, 2));
        Hex.True(System.Text.Encoding.UTF8.GetString(HubProtocol.Bytes(row, 1)) == "1",
            "the first column of the first row is the service item id tera-api reuses");

        // 3. CreateBox (gufid 107) for the account, three of that item.
        var (items, tags) = HubBoxStrings(1, 3, "Thank you", "Shop purchase");
        string boxSn = HubScalar(HubAnswer(hub, HubCall(3, HubProtocol.BoxApiGusid, HubProtocol.OpMsg,
            HubOpMsg(HubProtocol.FnCreateBox,
                ("receiverServiceSN", "1"), ("receiverUserSN", account.ToString()),
                ("receiverGUSID", ""), ("receiverCharacterSN", ""), ("receiverCharacterName", ""),
                ("startActivationDateTime", "2026-09-27 01:00:00"),
                ("endActivationDateTime", "2026-10-27 01:00:00"),
                ("visableFlagBeforeActivation", "1"), ("boxTagInfo", tags),
                ("boxServiceItemInfo", items), ("externalTransactionKey", "shop-42")))));
        Hex.True(boxSn == "1", $"first box is 1, got {boxSn}");

        var box = store.GetHubBox(1)!;
        Hex.True(box.State == TeraSharp.Arbiter.Persistence.CharacterStore.HubBoxDelivered
            && box.ParcelId > 0, "the box is delivered and names its parcel");
        Hex.True(box.Title == "Thank you" && box.Content == "Shop purchase" && box.ExternalKey == "shop-42",
            "box tag 2 is the title, tag 1 the content, and the transaction key is kept for support");

        var parcel = store.GetParcel(box.ParcelId)!;
        Hex.True(parcel.ReceiverDbId == 1 && parcel.SenderName == BoxDelivery.Sender,
            "the parcel goes to the account's character, from the shop");
        var record = store.GetParcelRecord(box.ParcelId)!;
        int at = SystemParcelAttachments.ParcelItemsOffset;
        Hex.True(BitConverter.ToInt32(record, at + 8) == 88888, "template at +8 of the attachment record");
        Hex.True(BitConverter.ToInt32(record, at + 12) == 3, "amount at +12 - three of them, from item tag 1");
        Hex.True(store.CountParcelItems(box.ParcelId) == 1, "one attachment row");
        Hex.True(online.Count == 1 && online[0] == HubServer.NoticeText, "an online buyer is told once");
    }

    [Test]
    public static void T207_a_box_for_an_account_with_no_character_waits_for_one()
    {
        using var store = new TeraSharp.Arbiter.Persistence.CharacterStore(":memory:", QuietLog());
        var account = store.GetOrCreateAccount("t207");
        var hub = HubFor(store);
        HubScalar(HubAnswer(hub, HubCall(1, HubProtocol.BoxApiGusid, HubProtocol.OpMsg,
            HubOpMsg(HubProtocol.FnCreateServiceItem, ("serviceItemMappingItemSN", "77777"),
                ("serviceItemEnableFlag", "1"), ("serviceItemName", "Waiting"))))); 
        var (items, tags) = HubBoxStrings(1, 1, "Later", "Later");
        HubScalar(HubAnswer(hub, HubCall(2, HubProtocol.BoxApiGusid, HubProtocol.OpMsg,
            HubOpMsg(HubProtocol.FnCreateBox, ("receiverUserSN", account.Id.ToString()),
                ("boxTagInfo", tags), ("boxServiceItemInfo", items)))));

        Hex.True(store.PendingHubBoxes().Count == 1, "no character yet, so the box waits");
        Hex.True(BoxDelivery.DeliverPending(store) == 0, "and a sweep cannot place it");

        int id = store.CreateCharacter(new TeraSharp.Arbiter.Persistence.CharacterRecord
        {
            AccountId = account.Id, Name = "t207a", Race = 1, Class = 2, Level = 1, TemplateId = 10101,
            Zone = 5, Appearance = new byte[8], Details = new byte[32], Shape = new byte[64], Position = 1,
        });
        Hex.True(BoxDelivery.DeliverPending(store) == 1, "the first character collects it");
        Hex.True(store.PendingHubBoxes().Count == 0, "nothing is left pending");
        var box = store.GetHubBox(1)!;
        Hex.True(store.GetParcel(box.ParcelId)!.ReceiverDbId == id, "and it went to that character");
    }

    [Test]
    public static void T207_add_benefit_grants_elite_and_an_unknown_account_is_refused()
    {
        using var store = GuildStore(1);
        long account = store.GetCharacter(1)!.AccountId;
        var pushed = new List<long>();
        var hub = HubFor(store, new HubServer.Hooks { BenefitsChanged = pushed.Add });

        // AddBenefitReq: userSrl fixed64 1, benefitId fixed32 2, remainSec fixed32 3.
        var grant = new HubProtocol.Writer().Fixed64(1, (ulong)account).Fixed32(2, 533).Fixed32(3, 30 * 86400);
        Hex.True(HubAnswer(hub, HubCall(1, hub.Gusid, HubProtocol.AddBenefitReq, grant.ToArray())) is not null,
            "a benefit for a real account is accepted");
        var rows = store.GetAccountBenefits(account);
        Hex.True(rows.Count == 1 && rows[0].PackageId == 533, "benefit 533 - tera-api's elite id - is stored");
        Hex.True(rows[0].ExpiresAt > DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 29 * 86400,
            "remainSec seconds from now, so elite runs out when tera-api says it does");
        Hex.True(pushed.Count == 1 && pushed[0] == account, "the online list is refreshed once");

        // account_benefits REFERENCES accounts(id): an unknown account must be refused, not inserted.
        var stranger = new HubProtocol.Writer().Fixed64(1, 987654).Fixed32(2, 533).Fixed32(3, 60);
        Hex.True(HubAnswer(hub, HubCall(2, hub.Gusid, HubProtocol.AddBenefitReq, stranger.ToArray())) is null,
            "an unknown account is refused rather than breaking the connection");

        var remove = new HubProtocol.Writer().Fixed64(1, (ulong)account).Fixed32(2, 533);
        Hex.True(HubAnswer(hub, HubCall(3, hub.Gusid, HubProtocol.RemoveBenefitReq, remove.ToArray())) is not null,
            "RemoveBenefit is answered");
        Hex.True(store.GetAccountBenefits(account).Count == 0, "and the row is gone");
    }

    [Test]
    public static void T207_box_strings_parse_the_way_tera_api_encodes_them()
    {
        // convertBoxTagValue: count, then per item serviceItemSN, externalItemKey, tagCount and
        // tagSN/hex(value) pairs; item tag 1 is the stack count. Tag values are hex UTF-8.
        var items = HubProtocol.ParseBoxItems("2,7,0,1,1,3132,9,0,1,1,31");
        Hex.True(items.Count == 2, "two items");
        Hex.True(items[0].ServiceItemSn == 7 && items[0].Count == 12, "hex 3132 is the text 12");
        Hex.True(items[1].ServiceItemSn == 9 && items[1].Count == 1, "hex 31 is the text 1");
        var tags = HubProtocol.ParseBoxTags("3,1,636f6e74656e74,2,7469746c65,3,69636f6e");
        Hex.True(tags[1] == "content" && tags[2] == "title" && tags[3] == "icon",
            "1 content, 2 title, 3 icon - tera-api's boxHelper order");
        Hex.True(HubProtocol.ParseBoxItems("1,7,0").Count == 0, "a truncated list yields nothing, never a guess");
        Hex.True(HubProtocol.Timestamp(new Dictionary<string, string> { ["d"] = "2026-09-27 01:00:00" }, "d") > 0
            && HubProtocol.Timestamp(new Dictionary<string, string> { ["d"] = "" }, "d") == 0,
            "an empty hub date means no limit");
    }

    [Test]
    public static void T207_the_shop_button_gets_one_string_field()
    {
        // S_SHOW_AWESOMIUMWEB_SHOP's shipped def is a single field, `string link`: the u16 offset
        // is frame-relative (4 bytes of header plus the field itself), then UTF-16LE.
        var body = ShopUrl.BuildShowShop("http://127.0.0.1:81/tera/ShopMain");
        Hex.True(BitConverter.ToUInt16(body, 0) == 6, "the string starts at frame offset 6");
        Hex.True(System.Text.Encoding.Unicode.GetString(body, 2, body.Length - 4)
            == "http://127.0.0.1:81/tera/ShopMain", "the link, NUL-terminated");
        Hex.True(body[^1] == 0 && body[^2] == 0, "and it is terminated");
    }
}
