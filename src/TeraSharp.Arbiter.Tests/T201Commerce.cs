// SPDX-License-Identifier: MIT
// Copyright (c) 2026 the TeraSharp contributors

using System.Globalization;
using Microsoft.Data.Sqlite;
using TeraSharp.Arbiter.Handlers;
using TeraSharp.Arbiter.Persistence;
using TeraSharp.Arbiter.World;

namespace TeraSharp.Arbiter.Tests;

public static partial class Tests
{
    private static string T201CommerceSheets()
    {
        string directory = Path.Combine(Path.GetTempPath(), "t201-commerce-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "WorldData.xml"), "<WorldData><TradeBroker expireDays='9' transactionFeeRate='0.125'/><Parcel maxRecvListCnt='100' maxSendListCnt='100' escrowReturnWait='600'/></WorldData>");
        File.WriteAllText(Path.Combine(directory, "ItemTemplate.xml"), "<ItemData><Item id='7' enchantEnable='False' masterpieceRate='0'/><Item id='8' enchantEnable='True' masterpieceRate='0.5'/></ItemData>");
        File.WriteAllText(Path.Combine(directory, "EnchantData.xml"), "<EnchantData normalMaxCount='2' masterpieceMaxCount='3'/>");
        Hex.True(QaCommerceSheet.Entry.Load(directory).FromSheet && QaItemSheet.Entry.Load(directory).FromSheet,
            "commerce and item/enchant values come from supplied sheets");
        return directory;
    }
    private static void T201ReleaseCommerceSheets(string directory)
    {
        QaCommerceSheet.Entry.UseBuiltIn(); QaItemSheet.Entry.UseBuiltIn(); Directory.Delete(directory, true);
    }

    [Test] public static void T201_parcel_QA_deletes_received_rows_and_ages_matching_titles_globally()
    {
        using var h = new T201AccountHarness();
        int own = h.Store.CreateParcel(0, "system", 1, "same title", "", 0, 103);
        int other = h.Store.CreateParcel(0, "system", 2, "same title", "", 0, 103);
        int untouched = h.Store.CreateParcel(0, "system", 2, "different", "", 0, 103);
        h.Store.AddParcelItem(own, 0, 12345, 7, 1);
        DateTime ownBefore = h.Store.GetParcelCreatedUtc(own), otherBefore = h.Store.GetParcelCreatedUtc(other);
        DateTime untouchedBefore = h.Store.GetParcelCreatedUtc(untouched);
        T181WithOperators("t39", () =>
        {
            h.Run("sub_parcel_days 2 \"same title\""); h.Client.Frame();
            Hex.True(h.Store.GetParcelCreatedUtc(own) == ownBefore.AddDays(-2)
                && h.Store.GetParcelCreatedUtc(other) == otherBefore.AddDays(-2)
                && h.Store.GetParcelCreatedUtc(untouched) == untouchedBefore,
                "spQASubParcelDays changes exact titles globally, including another receiver");
            h.Run("clear_parcel unknown-online-name");
            Hex.True(h.Store.GetParcel(own) == null && h.Store.GetParcelsFor(1).Count == 0
                && h.Store.GetParcel(other) != null && h.Store.CountParcelItems(own) == 1,
                "Arb082 ClearRecvedParcel marks status4; missing online target falls back to caller, attachments retained");
            Hex.True(h.Client.Available == 0 && h.Main.Available == 0 && h.Instance.Available == 0,
                "native clear has no invented client or World acknowledgement");
        });
    }

    [Test] public static void T201_parcel_QA_limits_are_consumed_by_make_parcel_and_keep_native_error_fields()
    {
        string directory = T201CommerceSheets();
        try
        {
            using var h = new T201AccountHarness();
            h.Store.CreateParcel(0, "system", 1, "existing", "", 0, 103);
            byte[] record = ParcelDbHandlers.BuildParcelDataNoMsg(0, 1, 2, "sender", "g1", title: "new");
            BitConverter.GetBytes(1).CopyTo(record, ParcelDbHandlers.ParcelDataParcelType);
            byte[] request = new byte[ParcelDbHandlers.MakeRequestSize + record.Length];
            BitConverter.GetBytes(26).CopyTo(request, 0); BitConverter.GetBytes(record.Length).CopyTo(request, 4);
            BitConverter.GetBytes(17).CopyTo(request, 16); record.CopyTo(request, 20);
            T181WithOperators("t39", () =>
            {
                h.Run("set_max_recv_mail_cnt 1");
                var refused = RunHandler1(0x2779, request, h.Store);
                Hex.True(refused.op == 0x277A, "native make-parcel reply opcode");
                Hex.Eq(refused.body, Convert.FromHexString("1B0000000000000011000000000200000001000000"),
                    "Arb082 inbox limit: empty atom ref, live DLM17, false, error2, receiver1");
                h.Run("set_max_recv_mail_cnt 2");
                var accepted = RunHandler1(0x2779, request, h.Store);
                Hex.True(accepted.body[ParcelDbHandlers.MakeRspSuccess] == 1
                    && h.Store.GetParcelsFor(1).Count == 2 && h.Store.GetParcelsFor(1).Any(x => x.ParcelType == 1),
                    "changed runtime limit governs the actual World DB handler and type remains preserved");
                // T234b: the handler now tells the receiver their badge went up, which is what the
                // real Arbiter does from Handler_SDB_MAKE_PARCEL (Arb_part_071.c:15656) and what
                // this harness makes reachable - the receiver resolves to "g1", character 1, whose
                // session is h.Client. Character 1 holds the type-103 parcel created above and this
                // new one, neither opened, so the badge is two unread and nothing read-unclaimed.
                // Draining it here is also what keeps the operator-gate block below counting zero.
                Hex.Eq(h.Client.Frame(), ParcelHandlers.BuildReadRecvStatus(2, 0),
                    "T234b: an accepted make-parcel pushes the receiver's mail badge");
                Hex.True(h.Main.Available == 0 && h.Instance.Available == 0,
                    "and nothing goes to World - the badge is the Arbiter's own packet");
                h.Run("set_max_send_mail_cnt 1"); h.Store.CreateParcel(1, "g1", 2, "escrow", "", 0, 2);
                Hex.True(!QaMailBrokerCommands.CanMakeParcel(h.Store, 1, 2, 2, out uint error, out bool senderFull)
                    && error == 0 && senderFull, "native escrow sender limit uses @1219 with zero DB error");
                Hex.True(QaMailBrokerCommands.CanMakeParcel(h.Store, 1, 2, 1, out _, out _),
                    "ordinary mail does not consume the escrow sender limit");
            });
            T181WithOperators(null, () =>
            {
                foreach (string name in QaMailBrokerCommands.Names) h.Run(name + " 1");
                Hex.True(h.Client.Available == 0 && h.Instance.Available == 0 && h.Main.Available == 0,
                    "all commerce QA mutations retain central operator authorization");
            });
        }
        finally { T201ReleaseCommerceSheets(directory); }
    }

    [Test] public static void T201_broker_QA_average_and_expiry_have_real_price_and_listing_consumers()
    {
        string directory = T201CommerceSheets();
        try
        {
            using var h = new T201AccountHarness();
            byte[] item = new byte[536]; BitConverter.GetBytes(2).CopyTo(item, ItemEdits.RecEnchantLevel);
            h.Store.UpsertItem(101, 1, 0, 1, 8, 1, item); h.Store.UpsertItem(102, 2, 0, 1, 8, 2, item);
            int own = h.Store.CreateBrokerListing(1, "g1", 101, 8, 2, 100);
            int other = h.Store.CreateBrokerListing(2, "g2", 102, 8, 2, 80);
            int sold = h.Store.CreateBrokerListing(1, "g1", 101, 8, 2, 100); h.Store.SellBrokerListing(sold, 2);
            string otherBefore = h.Store.GetBrokerListing(other)!.RegisteredAt, soldBefore = h.Store.GetBrokerListing(sold)!.RegisteredAt;
            T181WithOperators("t39", () =>
            {
                h.Run("set_tb_avg_price 8 2 0 75"); h.Run("set_tb_avg_price 8 3 0 999"); h.Run("set_tb_avg_price 7 0 1 999");
                Hex.True(h.Store.GetQaBrokerAverage(8, 3, false) == 0 && h.Store.GetQaBrokerAverage(7, 0, true) == 0,
                    "normal enchant and masterwork eligibility use loaded ItemTemplate/EnchantData");
                byte[] request = new byte[12]; BitConverter.GetBytes(101L).CopyTo(request, 0); BitConverter.GetBytes(8).CopyTo(request, 8);
                new BrokerHandlers(QuietLog()).Handle(h.Client.Session, BrokerPackets.C_TRADE_BROKER_INPUT_PRICE, request);
                Hex.Eq(h.Client.Frame(), Convert.FromHexString("2C00E64F28000000000000004B0000000000000000000000000000000000000000000000000000000000C03F"),
                    "Arb05631-143: minimum per item40, runtime average75, zero TCat, sheet transaction fee DOUBLE0.125");
                h.Run("clear_tb_avg_price");
                byte[] cleared = QaMailBrokerCommands.InputPrice(h.Store, 1, request);
                Hex.True(BitConverter.ToInt64(cleared, 4) == 40 && BitConverter.ToInt64(cleared, 12) == 0,
                    "clear affects average cache, not actual listings/minimum");
                DateTime before = DateTime.UtcNow;
                h.Run("set_trade_broker_remain_hours 2");
                DateTime changed = DateTime.Parse(h.Store.GetBrokerListing(own)!.RegisteredAt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
                Hex.True(Math.Abs((changed - before.AddDays(-9).AddHours(2)).TotalSeconds) < 2
                    && h.Store.GetBrokerListing(other)!.RegisteredAt == otherBefore && h.Store.GetBrokerListing(sold)!.RegisteredAt == soldBefore,
                    "native remaining-hours updates only caller's active registration times using loaded expireDays");
            });
        }
        finally { T201ReleaseCommerceSheets(directory); }
    }

    [Test] public static void T201_broker_recalculation_uses_sale_snapshots_and_auto_toggle_controls_real_tick()
    {
        string directory = T201CommerceSheets();
        try
        {
            using var h = new T201AccountHarness();
            byte[] record = new byte[536]; BitConverter.GetBytes(2).CopyTo(record, ItemEdits.RecEnchantLevel);
            h.Store.UpsertItem(123, 1, 0, 1, 8, 1, record);
            int first = h.Store.CreateBrokerListing(1, "g1", 123, 8, 2, 200); h.Store.SellBrokerListing(first, 2);
            int second = h.Store.CreateBrokerListing(1, "g1", 123, 8, 100, 30000); h.Store.SellBrokerListing(second, 2);
            // The buyer's later enchant must not rewrite historical sold-item attributes.
            BitConverter.GetBytes(1).CopyTo(record, ItemEdits.RecEnchantLevel); h.Store.UpsertItem(123, 2, 0, 1, 8, 1, record);
            T181WithOperators("t39", () =>
            {
                h.Run("tb_avg_price");
                Hex.True(h.Store.GetQaBrokerAverage(8, 2, false) == 200 && h.Store.GetQaBrokerAverage(8, 1, false) == 0,
                    "native (200/2 +30000/100)/2 =200, not a quantity-weighted average or changed bag attributes");
                h.Run("set_tb_avg_price 8 2 0 999"); h.Run("auto_recalc_tb_avg OFF");
                DateTime tick = DateTime.UtcNow.AddDays(1);
                h.Store.QaCommerceTick(tick); Hex.True(h.Store.GetQaBrokerAverage(8, 2, false) == 999, "off gates scheduled calculation");
                h.Run("auto_recalc_tb_avg ON");
                h.Store.QaCommerceTick(tick.ToLocalTime().Date.AddHours(12).ToUniversalTime());
                Hex.True(h.Store.GetQaBrokerAverage(8, 2, false) == 200, "on permits daily sheet-hour calculation");
                h.Run("set_tb_avg_price 8 2 0 777");
                h.Store.QaCommerceTick(tick.ToLocalTime().Date.AddHours(13).ToUniversalTime());
                Hex.True(h.Store.GetQaBrokerAverage(8, 2, false) == 777, "same-date tick does not calculate twice");
            });
        }
        finally { T201ReleaseCommerceSheets(directory); }
    }

    [Test] public static void T201_escrow_QA_timeout_returns_new_system_parcel_once_and_notifies_current_session()
    {
        string directory = T201CommerceSheets();
        try
        {
            using var h = new T201AccountHarness();
            TeraSharp.Arbiter.Program.World!.RegisterPlayer(h.Client.Session);
            int original = h.Store.CreateParcel(1, "g1", 2, "escrow", "body", 0, 2);
            byte[] item = new byte[SystemParcelAttachments.ItemRecordSize]; BitConverter.GetBytes(8).CopyTo(item, 8); BitConverter.GetBytes(3).CopyTo(item, 12);
            byte[] record = SystemParcelAttachments.BuildRecord(original, 2, "g2", "g1", "escrow", "body", 0, new[] { item });
            BitConverter.GetBytes(1).CopyTo(record, 0); BitConverter.GetBytes(2).CopyTo(record, ParcelDbHandlers.ParcelDataParcelType);
            h.Store.SetParcelRecord(original, record); h.Store.AddParcelItem(original, 0, 999, 8, 3);
            DateTime created = h.Store.GetParcelCreatedUtc(original);
            T181WithOperators("t39", () =>
            {
                h.Run("parcelreturn 0"); h.Client.Frame(); h.Run("set_escrow_return_wait 2");
                h.Store.QaCommerceTick(created.AddSeconds(119));
                Hex.True(h.Store.GetParcel(original) != null && h.Store.GetParcelsFor(1).Count == 0, "sender override minutes wins over global seconds");
                h.Store.QaCommerceTick(created.AddSeconds(120));
                var returned = h.Store.GetParcelsFor(1).Single();
                Hex.True(h.Store.GetParcel(original) == null && h.Store.CountParcelItems(original) == 1
                    && returned.ParcelId != original && returned.ParcelType == 103 && returned.SenderName == "@477"
                    && h.Store.CountParcelItems(returned.ParcelId) == 1,
                    "Arb08217714-17753 marks original3 and creates a new system return with preserved attachments");
                byte[] newRecord = h.Store.GetParcelRecord(returned.ParcelId)!;
                Hex.True(BitConverter.ToInt32(newRecord, 0) == 0 && BitConverter.ToInt32(newRecord, 0x50) == 1
                    && BitConverter.ToInt32(newRecord, 0xA4) == 103 && BitConverter.ToInt32(newRecord, 0xD8 + 12) == 3,
                    "returned full record carries system sender, original sender as receiver, type103 and original item quantity");
                Hex.Eq(h.Client.Frame(), Convert.FromHexString("0D006EF2010000000000000000"), "ParcelTimer native F26E false notification, current World13 session");
                h.Store.QaCommerceTick(created.AddHours(1));
                Hex.True(h.Store.GetParcelsFor(1).Count == 1 && h.Client.Available == 0, "status3 prevents duplicate returns");
            });
        }
        finally { T201ReleaseCommerceSheets(directory); }
    }

    [Test] public static void T201_mass_mail_test_payloads_and_mail_event_reset_are_native_and_persistent()
    {
        string database = Path.Combine(Path.GetTempPath(), "t201-mailevent-" + Guid.NewGuid() + ".db");
        try
        {
            using (var h = new T201AccountHarness())
                T181WithOperators("t39", () =>
                {
                    h.Run("send_mass_parcel_n 1");
                    Hex.Eq(h.Client.Frame(), Convert.FromHexString("0D006EF2010000000000000000"), "normal native mass-test notification flagfalse");
                    var parcel = h.Store.GetParcelsFor(1).Single(); byte[] record = h.Store.GetParcelRecord(parcel.ParcelId)!;
                    Hex.True(parcel.ParcelType == 102 && parcel.SenderName == "sender" && parcel.Title == "title" && parcel.Message == "body"
                        && h.Store.CountParcelItems(parcel.ParcelId) == 5, "Arb033 native literal test strings and all five attachment slots");
                    var expected = new[] { (15, 100), (20, 1), (25, 30), (30, 10), (55, 50) };
                    for (int i = 0; i < expected.Length; i++)
                        Hex.True(BitConverter.ToInt32(record, 0xD8 + i * 0x1B0 + 8) == expected[i].Item1
                            && BitConverter.ToInt32(record, 0xD8 + i * 0x1B0 + 12) == expected[i].Item2, "native mass attachment " + i);
                    h.Run("send_mass_parcel_n 1 ignored-token"); h.Client.Frame();
                    var escrow = h.Store.GetParcelsFor(1).Single(x => x.ParcelType == 105);
                    Hex.True(escrow.Money == 123 && h.Store.CountParcelItems(escrow.ParcelId) == 0
                        && BitConverter.ToInt64(h.Store.GetParcelRecord(escrow.ParcelId)!, 0x950) == 123,
                        "second token selects native system escrow type105, money123, no attachments");
                    h.Run("send_mass_parcel_n -1"); h.Run("send_mass_parcel_n 1 a b");
                    Hex.True(h.Store.GetParcelsFor(1).Count == 2 && h.Client.Available == 0, "native invalid counts/arity do not generate parcels");
                    h.Store.UpdateMailEventCheckInfo(1, 7, 123, 9, 3600); h.Run("reset_event_mail 7");
                    var state = h.Store.GetMailEventCheckInfo(1).Single();
                    Hex.True(state.ReceivedCount == 0 && state.PlaySeconds == 0 && state.LastUpdated > 123,
                        "native account/event upsert clears counts and refreshes date without granting a reward");
                });
            using (var store = new CharacterStore(database, QuietLog())) store.UpdateMailEventCheckInfo(42, 7, 999, 0, 0);
            using (var reopened = new CharacterStore(database, QuietLog()))
                Hex.True(reopened.GetMailEventCheckInfo(42).Single() == new CharacterStore.MailEventCheckInfo(7, 999, 0, 0), "spUpdateMailEventCheckInfo state survives restart");
        }
        finally { SqliteConnection.ClearAllPools(); File.Delete(database); }
    }
}
