// SPDX-License-Identifier: MIT
// Copyright (c) 2026 the TeraSharp contributors

using System.Globalization;
using Microsoft.Extensions.Logging;
using TeraSharp.Arbiter.Network;
using TeraSharp.Arbiter.Persistence;
using TeraSharp.Arbiter.World;

namespace TeraSharp.Arbiter.Handlers;

/// <summary>T201 native QA mailbox/broker paths. Central dispatcher owns authorization.</summary>
public static class QaMailBrokerCommands
{
    public static IReadOnlySet<string> Names { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "clear_parcel", "sub_parcel_days", "set_max_recv_mail_cnt", "set_max_send_mail_cnt",
        "set_trade_broker_remain_hours", "set_tb_avg_price", "clear_tb_avg_price",
        "tb_avg_price", "auto_recalc_tb_avg", "parcelreturn", "set_escrow_return_wait",
        "send_mass_parcel", "send_mass_parcel_n", "reset_event_mail",
    };
    public static bool TryExecute(GameSession session, CharacterStore? store, GmCommandLine line, ILogger log)
    {
        if (!Names.Contains(line.Name)) return false;
        if (store == null || session.SelectedCharacter == null) return true;
        string name = line.Name.ToLowerInvariant(); int character = (int)session.SelectedCharacter.Id;
        try
        {
            switch (name)
            {
                case "clear_parcel":
                    // Arb040:8741: exactly one argument is an optional online target; failed lookup uses self.
                    var target = line.Args.Count == 1 ? Program.World?.InWorldSessions().FirstOrDefault(x =>
                        string.Equals(x.SelectedCharacter?.Name, line.Arg(0), StringComparison.OrdinalIgnoreCase)) : null;
                    store.ClearQaReceivedParcels((int)(target?.SelectedCharacter?.Id ?? (uint)character));
                    break;
                case "sub_parcel_days":
                    int days = QaGeneralCommands.NativeInt(line.Arg(0));
                    if (line.Args.Count < 2 || days <= 0) break;
                    store.SubtractParcelDays(line.Arg(1), days);
                    GmCommandHandlers.SendCustom(session, $"sub_parcel_days with title [{line.Arg(1)}] : [{days}] days");
                    break;
                case "set_max_recv_mail_cnt":
                case "set_max_send_mail_cnt":
                    int count = QaGeneralCommands.NativeInt(line.Arg(0));
                    if (line.Args.Count == 1 && count > 0) store.SetQaParcelLimit(character, count, name == "set_max_recv_mail_cnt");
                    break;
                case "set_trade_broker_remain_hours":
                    int hours = QaGeneralCommands.NativeInt(line.Arg(0));
                    int expiry = QaCommerceSheet.Entry.Value.BrokerExpireDays;
                    if (line.Args.Count == 1 && hours > 0 && expiry > 0)
                        store.SetQaBrokerRemainingHours(character, hours, expiry, DateTime.UtcNow);
                    break;
                case "set_tb_avg_price":
                    if (line.Args.Count != 4) break;
                    int template = QaGeneralCommands.NativeInt(line.Arg(0)), enchant = QaGeneralCommands.NativeInt(line.Arg(1));
                    bool master = QaGeneralCommands.NativeInt(line.Arg(2)) == 1;
                    if (QaItemSheet.Entry.Value.ValidPriceKey(template, enchant, master)
                        && long.TryParse(line.Arg(3), NumberStyles.Integer, CultureInfo.InvariantCulture, out long price))
                        store.SetQaBrokerAverage(template, enchant, master, price);
                    break;
                case "clear_tb_avg_price": store.ClearQaBrokerAverages(); break;
                case "tb_avg_price": store.RecalculateQaBrokerAverages(DateTime.UtcNow); break;
                case "auto_recalc_tb_avg":
                    if (line.Args.Count == 1 && (line.Arg(0).Equals("on", StringComparison.OrdinalIgnoreCase)
                        || line.Arg(0).Equals("off", StringComparison.OrdinalIgnoreCase)))
                        store.SetQaAutoBrokerAverage(line.Arg(0).Equals("on", StringComparison.OrdinalIgnoreCase));
                    break;
                case "parcelreturn":
                    int seconds = QaGeneralCommands.NativeInt(line.Arg(0));
                    store.SetQaEscrowWait(null, seconds);
                    GmCommandHandlers.SendCustom(session, $"ParcelReturn parcel return wait time: {seconds}");
                    break;
                case "set_escrow_return_wait":
                    int minutes = QaGeneralCommands.NativeInt(line.Arg(0));
                    if (line.Args.Count == 1 && minutes > 0) store.SetQaEscrowWait(character, unchecked(minutes * 60));
                    break;
                case "reset_event_mail":
                    int eventId = QaGeneralCommands.NativeInt(line.Arg(0));
                    if (line.Args.Count == 1 && eventId >= 0)
                    {
                        long account = store.GetCharacter(character)?.AccountId ?? 0;
                        if (account > 0) store.UpdateMailEventCheckInfo(account, eventId, DateTimeOffset.UtcNow.ToUnixTimeSeconds(), 0, 0);
                    }
                    break;
                case "send_mass_parcel":
                    if (line.Args.Count == 0) SendMassTest(store, Enumerable.Repeat(session, 500), false);
                    else SendMassTest(store, (IEnumerable<GameSession>?)Program.World?.InWorldSessions() ?? new[] { session }, false);
                    break;
                case "send_mass_parcel_n":
                    int copies = QaGeneralCommands.NativeInt(line.Arg(0));
                    if (line.Args.Count is 1 or 2 && copies > 0)
                        SendMassTest(store, Enumerable.Repeat(session, copies), line.Args.Count == 2);
                    break;
            }
        }
        catch (ArgumentOutOfRangeException) { log.LogWarning("QA {Command}: date exceeds native supported range", name); }
        return true;
    }

    /// <summary>Arb082:12825-12859: ordinary inbox limit gives2; escrow sender limit gives @1219 and error0.</summary>
    public static bool CanMakeParcel(CharacterStore store, int sender, int receiver, int type,
        out uint error, out bool senderFull)
    {
        store.StartQaCommerceMaintenance();
        error = 0; senderFull = false;
        var defaults = QaCommerceSheet.Entry.Value;
        int receiveLimit = store.GetQaParcelLimits(receiver).Receive;
        if (receiveLimit <= 0) receiveLimit = defaults.MaxReceived;
        if (type < 100 && receiveLimit > 0 && store.GetParcelsFor(receiver).Count >= receiveLimit)
        { error = 2; return false; }
        int sendLimit = store.GetQaParcelLimits(sender).Send;
        if (sendLimit <= 0) sendLimit = defaults.MaxSent;
        if (type == 2 && sendLimit > 0 && store.CountUnclaimedEscrowSent(sender) >= sendLimit)
        { senderFull = true; return false; }
        return true;
    }

    /// <summary>Arb057:15688-15737 -> Arb056:31-143: INPUT_PRICE consumes enchant/masterwork from the bag item.</summary>
    public static byte[] InputPrice(CharacterStore store, int character, ReadOnlySpan<byte> body)
    {
        var requested = BrokerPackets.ParseCInputPrice(body.ToArray());
        if (requested == null) return BrokerHandlers.Frame(BrokerPackets.S_TRADE_BROKER_INPUT_PRICE, BrokerPackets.BuildSInputPriceBody());
        var (itemDbId, template) = requested.Value;
        var item = itemDbId is >= 0 and <= int.MaxValue ? store.GetItem((int)itemDbId) : null;
        var key = ItemPriceKey(item?.OwnerDbId == character ? item.Record : null);
        long minimum = 0;
        foreach (var row in store.GetAllBrokerListings().Where(x => x.State == CharacterStore.BrokerListed && x.TemplateId == template && x.Amount > 0 && x.Price > 0))
        {
            if (store.GetBrokerPriceKey(row) != key) continue;
            long unit = row.Price / row.Amount;
            if (minimum == 0 || unit < minimum) minimum = unit;
        }
        long average = store.GetQaBrokerAverage(template, key.Enchant, key.Masterwork);
        return BrokerHandlers.Frame(BrokerPackets.S_TRADE_BROKER_INPUT_PRICE, BrokerPackets.BuildSInputPriceBody(
            minimum, average, registerFeeRate: BitConverter.DoubleToInt64Bits(QaCommerceSheet.Entry.Value.RegisterFeeRate)));
    }
    public static (int Enchant, bool Masterwork, int Grade) ItemPriceKey(byte[]? record)
        => record?.Length >= 0x139 ? (BitConverter.ToInt32(record, ItemEdits.RecEnchantLevel),
            record[ItemEdits.RecMasterwork] != 0, BitConverter.ToInt32(record, ItemEdits.RecGrade)) : (0, false, 0);

    /// <summary>Arb033:5050/5365: actual native test parcels, including literal test payloads.</summary>
    private static void SendMassTest(CharacterStore store, IEnumerable<GameSession> recipients, bool escrow)
    {
        var attachments = new List<byte[]>();
        if (!escrow)
            foreach (var (template, amount) in new[] { (15, 100), (20, 1), (25, 30), (30, 10), (55, 50) })
            {
                var item = new byte[SystemParcelAttachments.ItemRecordSize];
                BitConverter.GetBytes(template).CopyTo(item, 8); BitConverter.GetBytes(amount).CopyTo(item, 12); attachments.Add(item);
            }
        foreach (var recipient in recipients)
        {
            if (recipient.SelectedCharacter == null) continue;
            int receiver = (int)recipient.SelectedCharacter.Id, type = escrow ? 105 : 102;
            long money = escrow ? 123 : 0;
            int parcel = store.CreateParcel(0, "sender", receiver, "title", "body", money, type);
            var record = SystemParcelAttachments.BuildRecord(parcel, receiver, recipient.SelectedCharacter.Name,
                "sender", "title", "body", money, attachments);
            BitConverter.GetBytes(type).CopyTo(record, ParcelDbHandlers.ParcelDataParcelType); store.SetParcelRecord(parcel, record);
            for (int i = 0; i < attachments.Count; i++)
                store.AddParcelItem(parcel, i, 0, BitConverter.ToInt32(attachments[i], 8), BitConverter.ToInt32(attachments[i], 12));
            var (unread, unclaimed) = store.GetParcelCounts(receiver);
            recipient.Send(ParcelHandlers.BuildReadRecvStatus((uint)unread, (uint)unclaimed, flag: false));
        }
    }
}
