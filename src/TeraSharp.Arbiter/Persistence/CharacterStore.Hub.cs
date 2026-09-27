// SPDX-License-Identifier: MIT
// Copyright (c) 2026 the TeraSharp contributors

namespace TeraSharp.Arbiter.Persistence;

// =============================================================================================
// T207 - the platform "hub" tables: service items, boxes and their contents.
//
// A tera-api purchase becomes one CreateServiceItem per item template (a catalogue entry, reused
// by template id the way tera-api's ServiceItem.getByTemplateId expects) and then one CreateBox
// naming those service items with a count each. The box is what the player receives; TeraSharp
// turns it into a system parcel (World/BoxDelivery.cs) because 100.02 has no box packet of its
// own - see status/T207-HUB.md.
//
// The rows are kept after delivery on purpose: tera-api logs a purchase against its own box id,
// and a support question ("where did my item go") is answered by joining hub_boxes.parcel_id.
// There is deliberately NO foreign key to accounts: tera-api can pay for an account that has
// never logged in here, and a FOREIGN KEY failure on this path would close the hub socket
// (CLAUDE.md: SQLite foreign keys are enforced).
// =============================================================================================
public sealed partial class CharacterStore
{
    /// <summary>A catalogue entry: one item template, as tera-api's ServiceItem sees it.</summary>
    public sealed record HubServiceItemRow(long ServiceItemSn, int TemplateId, string Name,
        string Description, bool Enabled, long RegisterUserSn);

    /// <summary>A box waiting for, or already turned into, a parcel. State 0 = pending, 1 = delivered.</summary>
    public sealed record HubBoxRow(long BoxSn, long AccountId, int CharacterId, string Title,
        string Content, string Icon, long StartAt, long EndAt, string ExternalKey, int State, int ParcelId);

    /// <summary>One line of a box: the service item, the template it resolves to, and the count.</summary>
    public sealed record HubBoxItemRow(long BoxSn, int Slot, long ServiceItemSn, int TemplateId, long Amount);

    public const int HubBoxPending = 0, HubBoxDelivered = 1;

    private void EnsureHubTables()
    {
        using var command = _db.CreateCommand();
        command.CommandText =
            "CREATE TABLE IF NOT EXISTS hub_service_items(" +
            "service_item_sn INTEGER PRIMARY KEY AUTOINCREMENT, template_id INTEGER NOT NULL," +
            "name TEXT NOT NULL DEFAULT '', description TEXT NOT NULL DEFAULT ''," +
            "enabled INTEGER NOT NULL DEFAULT 1, register_user_sn INTEGER NOT NULL DEFAULT 0," +
            "created_at INTEGER NOT NULL DEFAULT 0);" +
            "CREATE TABLE IF NOT EXISTS hub_boxes(" +
            "box_sn INTEGER PRIMARY KEY AUTOINCREMENT, account_id INTEGER NOT NULL," +
            "character_id INTEGER NOT NULL DEFAULT 0, title TEXT NOT NULL DEFAULT ''," +
            "content TEXT NOT NULL DEFAULT '', icon TEXT NOT NULL DEFAULT ''," +
            "start_at INTEGER NOT NULL DEFAULT 0, end_at INTEGER NOT NULL DEFAULT 0," +
            "external_key TEXT NOT NULL DEFAULT '', state INTEGER NOT NULL DEFAULT 0," +
            "parcel_id INTEGER NOT NULL DEFAULT 0, created_at INTEGER NOT NULL DEFAULT 0);" +
            "CREATE TABLE IF NOT EXISTS hub_box_items(" +
            "box_sn INTEGER NOT NULL, slot INTEGER NOT NULL, service_item_sn INTEGER NOT NULL," +
            "template_id INTEGER NOT NULL, amount INTEGER NOT NULL DEFAULT 1," +
            "PRIMARY KEY(box_sn, slot));" +
            "CREATE INDEX IF NOT EXISTS hub_boxes_pending ON hub_boxes(state, account_id);";
        command.ExecuteNonQuery();
    }

    /// <summary>Tests and --selftest: create the tables without a hub call having arrived.</summary>
    public void EnsureHubSchema()
    {
        lock (_lock) EnsureHubTables();
    }

    private static long HubNow() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    // ---------------------------------------------------------------------------- service items

    public long CreateHubServiceItem(int templateId, string name, string description,
        bool enabled = true, long registerUserSn = 0)
    {
        lock (_lock)
        {
            EnsureHubTables();
            using var command = _db.CreateCommand();
            command.CommandText =
                "INSERT INTO hub_service_items(template_id,name,description,enabled,register_user_sn,created_at) " +
                "VALUES($t,$n,$d,$e,$u,$c);SELECT last_insert_rowid()";
            command.Parameters.AddWithValue("$t", templateId);
            command.Parameters.AddWithValue("$n", name ?? string.Empty);
            command.Parameters.AddWithValue("$d", description ?? string.Empty);
            command.Parameters.AddWithValue("$e", enabled ? 1 : 0);
            command.Parameters.AddWithValue("$u", registerUserSn);
            command.Parameters.AddWithValue("$c", HubNow());
            return (long)command.ExecuteScalar()!;
        }
    }

    public HubServiceItemRow? GetHubServiceItem(long serviceItemSn)
    {
        lock (_lock)
        {
            EnsureHubTables();
            using var command = _db.CreateCommand();
            command.CommandText =
                "SELECT service_item_sn,template_id,name,description,enabled,register_user_sn " +
                "FROM hub_service_items WHERE service_item_sn=$s";
            command.Parameters.AddWithValue("$s", serviceItemSn);
            using var reader = command.ExecuteReader();
            return reader.Read() ? ReadHubServiceItem(reader) : null;
        }
    }

    /// <summary>
    /// The newest enabled catalogue entry for a template, or null. tera-api asks for this before
    /// creating one (getByTemplateId), so answering it keeps its catalogue from growing a row per
    /// purchase.
    /// </summary>
    public IReadOnlyList<HubServiceItemRow> PageHubServiceItems(int templateId, int offset, int count)
    {
        lock (_lock)
        {
            EnsureHubTables();
            using var command = _db.CreateCommand();
            command.CommandText =
                "SELECT service_item_sn,template_id,name,description,enabled,register_user_sn " +
                "FROM hub_service_items WHERE enabled=1 AND ($t=0 OR template_id=$t) " +
                "ORDER BY service_item_sn DESC LIMIT $c OFFSET $o";
            command.Parameters.AddWithValue("$t", templateId);
            command.Parameters.AddWithValue("$c", count <= 0 ? 10 : count);
            command.Parameters.AddWithValue("$o", offset < 0 ? 0 : offset);
            using var reader = command.ExecuteReader();
            var rows = new List<HubServiceItemRow>();
            while (reader.Read()) rows.Add(ReadHubServiceItem(reader));
            return rows;
        }
    }

    public bool DisableHubServiceItem(long serviceItemSn)
    {
        lock (_lock)
        {
            EnsureHubTables();
            using var command = _db.CreateCommand();
            command.CommandText = "UPDATE hub_service_items SET enabled=0 WHERE service_item_sn=$s";
            command.Parameters.AddWithValue("$s", serviceItemSn);
            return command.ExecuteNonQuery() > 0;
        }
    }

    private static HubServiceItemRow ReadHubServiceItem(Microsoft.Data.Sqlite.SqliteDataReader reader)
        => new(reader.GetInt64(0), reader.GetInt32(1), reader.GetString(2), reader.GetString(3),
            reader.GetInt32(4) != 0, reader.GetInt64(5));

    // ---------------------------------------------------------------------------- boxes

    /// <summary>
    /// A box and its lines, in one transaction. Items whose service item is unknown or disabled
    /// are dropped with the box still created, because a box with one bad line must still deliver
    /// the rest; the caller logs the difference.
    /// </summary>
    public long CreateHubBox(long accountId, int characterId, string title, string content, string icon,
        long startAt, long endAt, string externalKey, IEnumerable<HubBoxItemRow> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        lock (_lock)
        {
            EnsureHubTables();
            using var tx = _db.BeginTransaction();
            long boxSn;
            using (var command = _db.CreateCommand())
            {
                command.Transaction = tx;
                command.CommandText =
                    "INSERT INTO hub_boxes(account_id,character_id,title,content,icon,start_at,end_at," +
                    "external_key,state,parcel_id,created_at) " +
                    "VALUES($a,$ch,$t,$co,$i,$s,$e,$k,0,0,$now);SELECT last_insert_rowid()";
                command.Parameters.AddWithValue("$a", accountId);
                command.Parameters.AddWithValue("$ch", characterId);
                command.Parameters.AddWithValue("$t", title ?? string.Empty);
                command.Parameters.AddWithValue("$co", content ?? string.Empty);
                command.Parameters.AddWithValue("$i", icon ?? string.Empty);
                command.Parameters.AddWithValue("$s", startAt);
                command.Parameters.AddWithValue("$e", endAt);
                command.Parameters.AddWithValue("$k", externalKey ?? string.Empty);
                command.Parameters.AddWithValue("$now", HubNow());
                boxSn = (long)command.ExecuteScalar()!;
            }
            int slot = 0;
            foreach (var item in items)
            {
                if (item.TemplateId <= 0 || item.Amount <= 0) continue;
                using var command = _db.CreateCommand();
                command.Transaction = tx;
                command.CommandText =
                    "INSERT INTO hub_box_items(box_sn,slot,service_item_sn,template_id,amount) " +
                    "VALUES($b,$s,$si,$t,$a)";
                command.Parameters.AddWithValue("$b", boxSn);
                command.Parameters.AddWithValue("$s", slot++);
                command.Parameters.AddWithValue("$si", item.ServiceItemSn);
                command.Parameters.AddWithValue("$t", item.TemplateId);
                command.Parameters.AddWithValue("$a", item.Amount);
                command.ExecuteNonQuery();
            }
            tx.Commit();
            return boxSn;
        }
    }

    public HubBoxRow? GetHubBox(long boxSn)
    {
        lock (_lock)
        {
            EnsureHubTables();
            using var command = _db.CreateCommand();
            command.CommandText = HubBoxSelect + " WHERE box_sn=$b";
            command.Parameters.AddWithValue("$b", boxSn);
            using var reader = command.ExecuteReader();
            return reader.Read() ? ReadHubBox(reader) : null;
        }
    }

    /// <summary>
    /// Boxes still waiting for a character to deliver them to. <paramref name="accountId"/> 0 is
    /// every account, which is what the delivery timer walks.
    /// </summary>
    public IReadOnlyList<HubBoxRow> PendingHubBoxes(long accountId = 0, int limit = 100)
    {
        lock (_lock)
        {
            EnsureHubTables();
            using var command = _db.CreateCommand();
            command.CommandText = HubBoxSelect +
                " WHERE state=0 AND ($a=0 OR account_id=$a) ORDER BY box_sn LIMIT $l";
            command.Parameters.AddWithValue("$a", accountId);
            command.Parameters.AddWithValue("$l", limit <= 0 ? 100 : limit);
            using var reader = command.ExecuteReader();
            var rows = new List<HubBoxRow>();
            while (reader.Read()) rows.Add(ReadHubBox(reader));
            return rows;
        }
    }

    public IReadOnlyList<HubBoxItemRow> GetHubBoxItems(long boxSn)
    {
        lock (_lock)
        {
            EnsureHubTables();
            using var command = _db.CreateCommand();
            command.CommandText =
                "SELECT box_sn,slot,service_item_sn,template_id,amount FROM hub_box_items " +
                "WHERE box_sn=$b ORDER BY slot";
            command.Parameters.AddWithValue("$b", boxSn);
            using var reader = command.ExecuteReader();
            var rows = new List<HubBoxItemRow>();
            while (reader.Read())
                rows.Add(new(reader.GetInt64(0), reader.GetInt32(1), reader.GetInt64(2),
                    reader.GetInt32(3), reader.GetInt64(4)));
            return rows;
        }
    }

    /// <summary>Records which parcel a box became. Only ever moves a box out of pending.</summary>
    public bool MarkHubBoxDelivered(long boxSn, int parcelId)
    {
        lock (_lock)
        {
            EnsureHubTables();
            using var command = _db.CreateCommand();
            command.CommandText =
                "UPDATE hub_boxes SET state=1, parcel_id=$p WHERE box_sn=$b AND state=0";
            command.Parameters.AddWithValue("$p", parcelId);
            command.Parameters.AddWithValue("$b", boxSn);
            return command.ExecuteNonQuery() > 0;
        }
    }

    private const string HubBoxSelect =
        "SELECT box_sn,account_id,character_id,title,content,icon,start_at,end_at,external_key," +
        "state,parcel_id FROM hub_boxes";

    private static HubBoxRow ReadHubBox(Microsoft.Data.Sqlite.SqliteDataReader reader)
        => new(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt32(2), reader.GetString(3),
            reader.GetString(4), reader.GetString(5), reader.GetInt64(6), reader.GetInt64(7),
            reader.GetString(8), reader.GetInt32(9), reader.GetInt32(10));
}
