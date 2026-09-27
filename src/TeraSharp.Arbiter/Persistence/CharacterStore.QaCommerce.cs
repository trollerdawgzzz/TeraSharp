// SPDX-License-Identifier: MIT
// Copyright (c) 2026 the TeraSharp contributors

using System.Globalization;
using Microsoft.Extensions.Logging;
using TeraSharp.Arbiter.Handlers;
using TeraSharp.Arbiter.World;

namespace TeraSharp.Arbiter.Persistence;

public sealed partial class CharacterStore
{
    private readonly Dictionary<(int Template, int Enchant, bool Masterwork), long> _qaBrokerPrices = new();
    private readonly Dictionary<int, (int Receive, int Send)> _qaParcelLimits = new();
    private readonly Dictionary<int, int> _qaEscrowWait = new();
    private int? _qaGlobalEscrowWait;
    private Timer? _qaCommerceTimer;
    private bool _qaCommerceDisposed, _qaCommerceSchema, _qaAutoBrokerAverage = true;
    private DateTime _qaLastBrokerCalculation;

    private void EnsureQaCommerceSchema()
    {
        if (_qaCommerceSchema) return;
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "CREATE TABLE IF NOT EXISTS broker_price_keys(trade_id INTEGER PRIMARY KEY,enchant INTEGER NOT NULL,masterwork INTEGER NOT NULL,grade INTEGER NOT NULL);"
            + "CREATE TABLE IF NOT EXISTS mail_event_check_info(account_id INTEGER NOT NULL,event_id INTEGER NOT NULL,last_updated INTEGER NOT NULL,received_count INTEGER NOT NULL,play_seconds INTEGER NOT NULL,PRIMARY KEY(account_id,event_id));";
        cmd.ExecuteNonQuery(); _qaCommerceSchema = true;
    }

    public void StartQaCommerceMaintenance()
    {
        lock (_lock)
        {
            if (_qaCommerceDisposed || _qaCommerceTimer != null) return;
            // Native ParcelTimer retries every300000ms (Arb057:9754); broker date/hour gate is independent.
            _qaCommerceTimer = new Timer(_ =>
            {
                try { QaCommerceTick(DateTime.UtcNow); }
                catch (Exception ex) { _log.LogWarning(ex, "QA commerce maintenance failed"); }
            }, null, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));
        }
    }
    private void DisposeQaCommerce()
    {
        lock (_lock) { _qaCommerceDisposed = true; _qaCommerceTimer?.Dispose(); _qaCommerceTimer = null; }
    }

    public void SetQaEscrowWait(int? sender, int seconds)
    {
        lock (_lock)
        {
            if (sender.HasValue) _qaEscrowWait[sender.Value] = seconds;
            else _qaGlobalEscrowWait = seconds;
        }
        StartQaCommerceMaintenance();
    }

    public void SetQaAutoBrokerAverage(bool enabled)
    { lock (_lock) _qaAutoBrokerAverage = enabled; StartQaCommerceMaintenance(); }

    public sealed record MailEventCheckInfo(int EventId, long LastUpdated, int ReceivedCount, int PlaySeconds);
    public void UpdateMailEventCheckInfo(long account, int eventId, long now, int received, int playSeconds)
    {
        lock (_lock)
        {
            EnsureQaCommerceSchema(); using var cmd = _db.CreateCommand();
            cmd.CommandText = "INSERT INTO mail_event_check_info VALUES($a,$e,$n,$r,$p) ON CONFLICT(account_id,event_id) DO UPDATE SET last_updated=$n,received_count=$r,play_seconds=$p";
            cmd.Parameters.AddWithValue("$a", account); cmd.Parameters.AddWithValue("$e", eventId);
            cmd.Parameters.AddWithValue("$n", now); cmd.Parameters.AddWithValue("$r", received); cmd.Parameters.AddWithValue("$p", playSeconds); cmd.ExecuteNonQuery();
        }
    }
    public IReadOnlyList<MailEventCheckInfo> GetMailEventCheckInfo(long account)
    {
        lock (_lock)
        {
            EnsureQaCommerceSchema(); using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT event_id,last_updated,received_count,play_seconds FROM mail_event_check_info WHERE account_id=$a ORDER BY event_id";
            cmd.Parameters.AddWithValue("$a", account); using var r = cmd.ExecuteReader(); var rows = new List<MailEventCheckInfo>();
            while (r.Read()) rows.Add(new(r.GetInt32(0), r.GetInt64(1), r.GetInt32(2), r.GetInt32(3))); return rows;
        }
    }

    public (int Enchant, bool Masterwork, int Grade) GetBrokerPriceKey(BrokerListingRow row)
    {
        lock (_lock)
        {
            EnsureQaCommerceSchema(); using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT enchant,masterwork,grade FROM broker_price_keys WHERE trade_id=$id";
            cmd.Parameters.AddWithValue("$id", row.TradeId); using var r = cmd.ExecuteReader();
            if (r.Read()) return (r.GetInt32(0), r.GetBoolean(1), r.GetInt32(2));
            // Legacy listings predate this snapshot; use their surviving item record when available.
            return QaMailBrokerCommands.ItemPriceKey(row.ItemDbId is >= 0 and <= int.MaxValue ? GetItem((int)row.ItemDbId)?.Record : null);
        }
    }
    private void SnapshotBrokerPriceKey(int tradeId, long itemDbId)
    {
        EnsureQaCommerceSchema();
        var key = QaMailBrokerCommands.ItemPriceKey(itemDbId is >= 0 and <= int.MaxValue ? GetItem((int)itemDbId)?.Record : null);
        using var cmd = _db.CreateCommand(); cmd.CommandText = "INSERT OR REPLACE INTO broker_price_keys VALUES($id,$e,$m,$g)";
        cmd.Parameters.AddWithValue("$id", tradeId); cmd.Parameters.AddWithValue("$e", key.Enchant);
        cmd.Parameters.AddWithValue("$m", key.Masterwork); cmd.Parameters.AddWithValue("$g", key.Grade); cmd.ExecuteNonQuery();
    }

    /// <summary>Arb054:3188-4041/Arb057:16398: mean of sold unit prices, one vote per transaction.</summary>
    public void RecalculateQaBrokerAverages(DateTime nowUtc)
    {
        lock (_lock)
        {
            var sheet = QaCommerceSheet.Entry.Value;
            var sums = new Dictionary<(int Template, int Enchant, bool Masterwork), (decimal Sum, int Count)>();
            foreach (var row in GetAllBrokerListings())
            {
                if (row.Amount <= 0 || row.Price <= 0 || row.State is < BrokerSold or > BrokerBuyerCollected
                    || !DateTime.TryParse(row.SoldAt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var sold)
                    || sold < nowUtc.AddDays(-sheet.AverageDays)) continue;
                var attributes = GetBrokerPriceKey(row);
                if (!QaItemSheet.Entry.Value.ValidPriceKey(row.TemplateId, attributes.Enchant, attributes.Masterwork)) continue;
                var key = (row.TemplateId, attributes.Enchant, attributes.Masterwork);
                var old = sums.GetValueOrDefault(key); sums[key] = (old.Sum + row.Price / row.Amount, old.Count + 1);
            }
            // Native SetAvgPrice updates represented enchant buckets (both normal and masterpiece), not all IDs.
            foreach (var group in sums.Keys.Select(x => (x.Template, x.Enchant)).Distinct())
                foreach (bool master in new[] { false, true })
                {
                    var key = (group.Template, group.Enchant, master); var value = sums.GetValueOrDefault(key);
                    _qaBrokerPrices[key] = value.Count == 0 ? 0 : (long)(value.Sum / value.Count);
                }
        }
    }

    public void QaCommerceTick(DateTime nowUtc)
    {
        var returned = new List<int>();
        lock (_lock)
        {
            if (_qaCommerceDisposed) return;
            var local = nowUtc.ToLocalTime(); var sheet = QaCommerceSheet.Entry.Value;
            if (_qaAutoBrokerAverage && local.Hour >= sheet.AverageHour && local.Date != _qaLastBrokerCalculation.Date)
            { RecalculateQaBrokerAverages(nowUtc); _qaLastBrokerCalculation = local; }
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT parcel_id,sender_db_id,created_at FROM parcels WHERE parcel_type=2 AND status<2 AND is_recved=0";
            var due = new List<int>();
            using (var r = cmd.ExecuteReader()) while (r.Read())
            {
                int sender = r.GetInt32(1);
                int seconds = _qaEscrowWait.GetValueOrDefault(sender, _qaGlobalEscrowWait ?? sheet.EscrowSeconds);
                if (DateTime.TryParse(r.GetString(2), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var created)
                    && (nowUtc - created).TotalSeconds >= seconds) due.Add(r.GetInt32(0));
            }
            foreach (int parcel in due)
            {
                var original = GetParcel(parcel); if (original == null || original.SenderDbId <= 0) continue;
                byte[] record = ParcelDbHandlers.ServedParcelRecord(this, original, full: true);
                using var update = _db.CreateCommand(); update.CommandText = "UPDATE parcels SET status=3 WHERE parcel_id=$id AND status<2";
                update.Parameters.AddWithValue("$id", parcel); if (update.ExecuteNonQuery() != 1) continue;
                int receiver = original.SenderDbId;
                int newId = CreateParcel(0, "@477", receiver, original.Title, original.Message, original.Money, 103);
                BitConverter.GetBytes(0).CopyTo(record, ParcelDbHandlers.ParcelDataSenderDbId);
                QaParcelText(record, ParcelDbHandlers.ParcelDataSenderName, "@477");
                BitConverter.GetBytes(receiver).CopyTo(record, ParcelDbHandlers.ParcelDataReceiverDbId);
                QaParcelText(record, ParcelDbHandlers.ParcelDataReceiverName, GetCharacter(receiver)?.Name);
                BitConverter.GetBytes(newId).CopyTo(record, ParcelDbHandlers.ParcelDataParcelId);
                BitConverter.GetBytes(103).CopyTo(record, ParcelDbHandlers.ParcelDataParcelType);
                SetParcelRecord(newId, record);
                using var copy = _db.CreateCommand();
                copy.CommandText = "INSERT INTO parcel_items(parcel_id,slot,item_db_id,template_id,amount) SELECT $new,slot,item_db_id,template_id,amount FROM parcel_items WHERE parcel_id=$old";
                copy.Parameters.AddWithValue("$new", newId); copy.Parameters.AddWithValue("$old", parcel); copy.ExecuteNonQuery();
                returned.Add(receiver);
            }
        }
        foreach (int receiver in returned)
        {
            var (unread, unclaimed) = GetParcelCounts(receiver);
            Program.World?.SessionForPlayerId(receiver)?.Send(ParcelHandlers.BuildReadRecvStatus((uint)unread, (uint)unclaimed, flag: false));
        }
    }
    private static void QaParcelText(byte[] record, int offset, string? text)
    {
        Array.Clear(record, offset, ParcelDbHandlers.ParcelNameMaxChars * 2);
        if (text != null) System.Text.Encoding.Unicode.GetBytes(text[..Math.Min(text.Length, ParcelDbHandlers.ParcelNameMaxChars - 1)]).CopyTo(record, offset);
    }

    public void SetQaParcelLimit(int character, int amount, bool receive)
    {
        lock (_lock)
        {
            _qaParcelLimits.TryGetValue(character, out var old);
            _qaParcelLimits[character] = receive ? (amount, old.Send) : (old.Receive, amount);
        }
    }
    public (int Receive, int Send) GetQaParcelLimits(int character)
    { lock (_lock) return _qaParcelLimits.GetValueOrDefault(character); }

    /// <summary>Arb082:1108-1182 -> Arb083:2463-2517: receiving-list deletion marks status4.</summary>
    public int ClearQaReceivedParcels(int receiver)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "UPDATE parcels SET status=4 WHERE receiver_db_id=$r AND status<4";
            cmd.Parameters.AddWithValue("$r", receiver); return cmd.ExecuteNonQuery();
        }
    }
    public int CountUnclaimedEscrowSent(int sender)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM parcels WHERE sender_db_id=$s AND parcel_type=2 AND status<4 AND is_recved=0";
            cmd.Parameters.AddWithValue("$s", sender); return Convert.ToInt32(cmd.ExecuteScalar());
        }
    }
    /// <summary>spQASubParcelDays changes all matching titles, irrespective of sender/recipient.</summary>
    public int SubtractParcelDays(string title, int days)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "UPDATE parcels SET created_at=datetime(created_at,$days) WHERE title=$title";
            cmd.Parameters.AddWithValue("$days", "-" + days.ToString(CultureInfo.InvariantCulture) + " days");
            cmd.Parameters.AddWithValue("$title", title); return cmd.ExecuteNonQuery();
        }
    }
    public void SetQaBrokerAverage(int template, int enchant, bool masterwork, long price)
    { lock (_lock) _qaBrokerPrices[(template, enchant, masterwork)] = price; }
    public long GetQaBrokerAverage(int template, int enchant, bool masterwork)
    { lock (_lock) return _qaBrokerPrices.GetValueOrDefault((template, enchant, masterwork)); }
    public void ClearQaBrokerAverages() { lock (_lock) _qaBrokerPrices.Clear(); }

    public IReadOnlyList<BrokerListingRow> GetAllBrokerListings()
        => QueryBroker($"SELECT {BrokerColumns} FROM broker_listings ORDER BY trade_id", 0, 0);

    /// <summary>Arb056:16544-16699 stores a new registration time, not an unrelated UI override.</summary>
    public void SetQaBrokerRemainingHours(int seller, int hours, int expireDays, DateTime nowUtc)
    {
        DateTime registered = nowUtc.AddDays(-expireDays).AddHours(hours);
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "UPDATE broker_listings SET registered_at=$r WHERE seller_db_id=$s AND state=$active";
            cmd.Parameters.AddWithValue("$r", registered.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
            cmd.Parameters.AddWithValue("$s", seller); cmd.Parameters.AddWithValue("$active", BrokerListed); cmd.ExecuteNonQuery();
        }
    }
}
