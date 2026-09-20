using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;
using TeraSharp.Arbiter.Persistence;

namespace TeraSharp.Arbiter.Web;

// =============================================================================================
// AdminApi - the admin web tool's JSON surface, T101 phase 1 + T101b phase 2.
// Feature list and the retail tool it replaces: status/WEBADMIN-DESIGN.md.
//
// PURE. Nothing here touches a socket: Handle() takes a method, a path, a query bag and a body,
// and returns a status and a string. AdminServer is the twenty lines of HttpListener around it,
// and every test drives this class directly.
//
// SCOPE. Phase 1 (T101) was WEBADMIN-DESIGN.md section 6's "read-only lookups": accounts,
// character, online, admin-log. Phase 2 (T101b) is the rest of section 6's second row - money,
// give-item, ban/unban, kick, announce, gm-level - plus the soft delete that makes restore mean
// what WA_UNDELETE_USER means. Phase 3 (bulk mail, events, festivals) still answers 501 with a
// result code rather than 404, so the page shows "not in this phase" instead of looking broken.
//
// RESULT CODES. The doc's section 2 catalogue reads them off the real writers: 0 ok, 2 not found,
// 3 invalid argument, 0x16 refused. The JSON mirrors those rather than inventing a scheme.
// =============================================================================================

/// <summary>One HTTP answer: a status, a content type and a body.</summary>
public sealed record AdminResponse(int Status, string ContentType, string Body);

/// <summary>A live session as the online list shows it. Supplied by a delegate so this file
/// never depends on WorldBridge and the tests need no server.</summary>
public sealed record AdminOnlineRow(int PlayerId, string Name, int Level, int Zone, string AccountName);

public sealed class AdminApi
{
    /// <summary>Retail result codes, from WEBADMIN-DESIGN.md section 2.</summary>
    public const int ResultOk = 0, ResultNotFound = 2, ResultInvalid = 3, ResultRefused = 0x16;

    private readonly CharacterStore _store;
    private readonly Func<IReadOnlyList<AdminOnlineRow>> _online;
    private readonly ILogger _log;
    private readonly string? _token;

    /// <summary>
    /// <paramref name="token"/> is the shared secret from TERASHARP_ADMIN_TOKEN. A null or empty
    /// token FAILS CLOSED - every request is refused - because an admin surface that defaults to
    /// open is the one mistake that cannot be walked back.
    /// </summary>
    public AdminApi(CharacterStore store, string? token, Func<IReadOnlyList<AdminOnlineRow>>? online, ILogger log)
    {
        _store = store;
        _token = string.IsNullOrWhiteSpace(token) ? null : token;
        _online = online ?? (() => Array.Empty<AdminOnlineRow>());
        _log = log;
    }

    /// <summary>Disconnect one player. Returns false when they are not online. Set by the
    /// wiring; unset means kick answers 2 (not found) rather than pretending.</summary>
    public Func<int, bool>? KickPlayer { get; set; }

    /// <summary>Send a system message to everyone in world. Returns how many got it.</summary>
    public Func<string, int>? Announce { get; set; }

    /// <summary>T106: what the Status tab reports about the World link - (links, ready, online).
    /// A delegate again, so this file still knows nothing about WorldBridge.</summary>
    public Func<(int Links, bool Ready, int Online)>? WorldStatus { get; set; }

    /// <summary>T106: when the process started, for uptime. Set once by the wiring.</summary>
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>T101c: one system message to one player. False when they are not in world.</summary>
    public Func<int, string, bool>? WarnPlayer { get; set; }

    /// <summary>T101c: move <c>(playerId, targetPlayerId)</c> to the target. False when either
    /// is not in world. Unset means teleport answers 0x16 rather than pretending.</summary>
    public Func<int, int, bool>? TeleportTo { get; set; }

    /// <summary>True when the tool is usable at all. False means TERASHARP_ADMIN_TOKEN is unset.</summary>
    public bool Enabled => _token != null;

    /// <summary>Phase 3 - bulk operations and events. Still answered as refusals.</summary>
    public static readonly string[] PhaseThreePaths =
    {
        "/api/bulk-mail", "/api/event", "/api/festival",
    };

    // -------------------------------------------------------------------------- entry point

    /// <summary>
    /// Route one request. <paramref name="token"/> is whatever the caller presented; it is
    /// compared in constant time against the configured one.
    /// </summary>
    public AdminResponse Handle(string method, string path,
        IReadOnlyDictionary<string, string>? query = null, string? body = null,
        string? token = null, string? sourceIp = null)
    {
        query ??= new Dictionary<string, string>();
        path = (path ?? "/").TrimEnd('/');
        if (path.Length == 0) path = "/";

        if (!Enabled)
            return Json(503, ResultRefused, "TERASHARP_ADMIN_TOKEN is not set - the admin tool is disabled");

        // T106: the PAGE is served without a token. It has to be - a browser typing
        // http://127.0.0.1:8050/ has nowhere to put a header, and T101's 401 meant the tool was
        // only usable through curl. The page is a static shell that carries no data: it prompts
        // for the token, keeps it in this browser, and sends it as X-Admin-Token on every API
        // call below, each of which is still gated. The listener is bound to 127.0.0.1 and
        // Serve() re-checks the peer is loopback, so serving it costs nothing.
        if (method == "GET" && (path == "/" || path == "/index.html"))
            return new AdminResponse(200, "text/html; charset=utf-8", AdminPage.Html);

        if (!TokenOk(token))
        {
            _log.LogWarning("admin: bad token from {Ip} for {Path}", sourceIp ?? "?", path);
            return Json(401, ResultRefused, "bad or missing token");
        }

        if (method == "GET" && path == "/api/accounts") return Accounts(query);
        if (method == "GET" && path == "/api/character") return Character(query);
        if (method == "GET" && path == "/api/online") return Online();
        if (method == "GET" && path == "/api/admin-log") return AdminLog(query);
        if (method == "GET" && path == "/api/deleted") return Deleted(query);
        if (method == "GET" && path == "/api/account") return Account(query);
        if (method == "GET" && path == "/api/search") return Search(query);
        if (method == "GET" && path == "/api/restrictions") return Restrictions(query);
        if (method == "GET" && path == "/api/announces") return Announces(query);
        if (method == "GET" && path == "/api/status") return Status();
        if (method == "GET" && path == "/api/log") return LogTail(query);
        if (method == "POST" && path == "/api/restore-character") return RestoreCharacter(body, sourceIp);

        // ---- phase 2 (T101b) ----
        if (method == "POST" && path == "/api/set-money") return SetMoney(body, sourceIp);
        if (method == "POST" && path == "/api/set-level") return SetLevel(body, sourceIp);
        if (method == "POST" && path == "/api/give-item") return GiveItem(body, sourceIp);
        if (method == "POST" && path == "/api/ban") return Ban(body, sourceIp, true);
        if (method == "POST" && path == "/api/unban") return Ban(body, sourceIp, false);
        if (method == "POST" && path == "/api/kick") return Kick(body, sourceIp);
        if (method == "POST" && path == "/api/announce") return Announcement(body, sourceIp);
        if (method == "POST" && path == "/api/gm-level") return GmLevel(body, sourceIp);
        // ---- phase 2b (T101c) ----
        if (method == "POST" && path == "/api/mute") return Mute(body, sourceIp, true);
        if (method == "POST" && path == "/api/unmute") return Mute(body, sourceIp, false);
        if (method == "POST" && path == "/api/warn") return Warn(body, sourceIp);
        if (method == "POST" && path == "/api/teleport") return Teleport(body, sourceIp);
        if (method == "POST" && path == "/api/rename") return Rename(body, sourceIp);
        if (method == "POST" && path == "/api/announce-schedule") return ScheduleAnnounce(body, sourceIp);
        if (method == "POST" && path == "/api/announce-delete") return DeleteAnnounce(body, sourceIp);

        foreach (var p in PhaseThreePaths)
            if (path == p)
                return Json(501, ResultRefused,
                    $"{path} is phase 3 - see status/WEBADMIN-DESIGN.md section 6");

        return Json(404, ResultNotFound, "no such endpoint");
    }

    private bool TokenOk(string? presented)
    {
        if (_token == null || presented == null) return false;
        if (presented.Length != _token.Length) return false;
        int diff = 0;
        for (int i = 0; i < presented.Length; i++) diff |= presented[i] ^ _token[i];
        return diff == 0;
    }

    // ------------------------------------------------------------------------- the endpoints

    /// <summary>GET /api/accounts?q=name-or-id - the account picker.</summary>
    private AdminResponse Accounts(IReadOnlyDictionary<string, string> q)
    {
        string term = Get(q, "q") ?? string.Empty;
        var rows = _store.SearchAccounts(term, 50);
        var sb = new StringBuilder();
        sb.Append("{\"result\":").Append(ResultOk).Append(",\"accounts\":[");
        for (int i = 0; i < rows.Count; i++)
        {
            var a = rows[i];
            if (i > 0) sb.Append(',');
            sb.Append("{\"id\":").Append(a.Id)
              .Append(",\"name\":").Append(Str(a.Name))
              .Append(",\"adminLevel\":").Append(a.AdminLevel)
              .Append(",\"characters\":[");
            var chars = _store.GetCharacters(a.Id);
            for (int c = 0; c < chars.Count; c++)
            {
                if (c > 0) sb.Append(',');
                sb.Append(CharacterBrief(chars[c]));
            }
            sb.Append("]}");
        }
        sb.Append("]}");
        return new AdminResponse(200, "application/json; charset=utf-8", sb.ToString());
    }

    /// <summary>GET /api/character?id=N or ?name=X - the row, its money, guild and items.</summary>
    private AdminResponse Character(IReadOnlyDictionary<string, string> q)
    {
        CharacterRecord? c = null;
        string? idText = Get(q, "id");
        if (idText != null && int.TryParse(idText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int id))
            c = _store.GetCharacter(id);
        else
        {
            string? name = Get(q, "name");
            if (!string.IsNullOrEmpty(name)) c = _store.GetCharacterByName(name);
        }
        if (c == null) return Json(404, ResultNotFound, "no such character");

        long money = _store.GetCharacterMoney(c.Id);
        long deleteAt = _store.GetCharacterDeleteAt(c.Id);
        int guildId = _store.GetGuildIdOf(c.Id);
        var guild = guildId != 0 ? _store.GetGuild(guildId) : null;

        var sb = new StringBuilder();
        sb.Append("{\"result\":").Append(ResultOk).Append(",\"character\":")
          .Append(CharacterBrief(c))
          .Append(",\"money\":").Append(money)
          .Append(",\"deleteAt\":").Append(deleteAt)
          .Append(",\"guild\":");
        if (guild == null) sb.Append("null");
        else sb.Append("{\"id\":").Append(guild.GuildId)
               .Append(",\"name\":").Append(Str(guild.Name)).Append('}');

        // T101c: the rest of the character page. Every figure here is a read the store already
        // had; the only new thing is the item NAME, which comes from the optional sheet
        // (Protocol/ItemNames) and falls back to the empty string so the page shows the id.
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        sb.Append(",\"position\":{\"zone\":").Append(c.Zone)
          .Append(",\"x\":").Append(c.X.ToString("0.##", CultureInfo.InvariantCulture))
          .Append(",\"y\":").Append(c.Y.ToString("0.##", CultureInfo.InvariantCulture))
          .Append(",\"z\":").Append(c.Z.ToString("0.##", CultureInfo.InvariantCulture))
          .Append(",\"world\":").Append(c.LastWorld)
          .Append(",\"guard\":").Append(c.LastGuard)
          .Append(",\"section\":").Append(c.LastSection).Append('}');

        sb.Append(",\"progress\":{\"exp\":").Append(c.Exp)
          .Append(",\"restBonus\":").Append(c.RestBonus)
          .Append(",\"questsActive\":").Append(_store.CountQuests(c.Id))
          .Append(",\"questsCompleted\":").Append(_store.GetCompletedQuestIds(c.Id).Count)
          .Append(",\"achievements\":").Append(_store.GetAccomplishedAchievements(c.Id).Count)
          .Append(",\"sectionsVisited\":").Append(_store.GetVisitedSections(c.Id).Count)
          // T113: play time has a real figure now - stamped at leave-world. S_GET_USER_LIST has
          // no field for it (checked the def: lastLogoutTime, deleteTime and banEndTime are the
          // only times in it), so the admin page is where it can actually be shown.
          .Append(",\"playSeconds\":").Append(_store.GetCharacterPlaySeconds(c.Id)).Append('}');

        // T101d: null until the ep_* columns have actually been written.
        //
        // CharacterStore.GetCharacterEp returns a row for ANY character that exists - all seven
        // ep_* columns are NOT NULL DEFAULT 0, and answering AS_LOAD_EXTRAPOINT_DATA with zeros
        // is the correct packet behaviour (it is what both captured characters send). For a
        // PAGE it is not: a fabricated zero row reads as "this character has an EP panel at
        // level 0" when the truth is that nothing has ever touched it. All-zero is the only
        // signal the schema offers, and it costs nothing to be wrong about - a character with
        // 0 exp, 0 points and 0 level has no panel worth drawing either way.
        var ep = _store.GetCharacterEp(c.Id);
        bool epWritten = ep != null && (ep.EpExp != 0 || ep.EpLevel != 0 || ep.EpPoint != 0
                                        || ep.DailyEpExp != 0 || ep.ReserveBonus != 0
                                        || ep.DailyLimit != 0 || ep.ResetTime != 0);
        sb.Append(",\"ep\":");
        if (!epWritten) sb.Append("null");
        else sb.Append("{\"level\":").Append(ep!.EpLevel)
               .Append(",\"point\":").Append(ep.EpPoint)
               .Append(",\"exp\":").Append(ep.EpExp)
               .Append(",\"dailyExp\":").Append(ep.DailyEpExp)
               .Append(",\"dailyLimit\":").Append(ep.DailyLimit)
               .Append(",\"resetTime\":").Append(ep.ResetTime).Append('}');

        sb.Append(",\"cards\":[");
        var cards = _store.GetAccountCards(c.AccountId);
        for (int i = 0; i < cards.Count; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append("{\"templateId\":").Append(cards[i].CardTemplateId)
              .Append(",\"amount\":").Append(cards[i].Amount)
              .Append(",\"name\":").Append(Str(Protocol.ItemNames.Lookup(cards[i].CardTemplateId))).Append('}');
        }
        sb.Append(']');

        sb.Append(",\"restrictions\":[");
        var restrictions = _store.GetRestrictions(c.Id);
        for (int i = 0; i < restrictions.Count; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append(RestrictionJson(c, restrictions[i], now));
        }
        sb.Append(']');

        sb.Append(",\"items\":[");
        var items = _store.GetInventoryItems(c.Id);
        for (int i = 0; i < items.Count; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append(ItemJson(items[i]));
        }
        sb.Append(']');

        // Two warehouses: the ACCOUNT one (inven 1, shared) and this character's own (inven 9).
        sb.Append(",\"warehouse\":[");
        AppendWarehouse(sb, c, World.WarehouseHandlers.InvenAccountWarehouse, "account", first: true);
        AppendWarehouse(sb, c, World.WarehouseHandlers.InvenCharacterWarehouse, "character", first: false);
        sb.Append(']');

        sb.Append(",\"itemNames\":").Append(Protocol.ItemNames.Count);
        sb.Append('}');
        return new AdminResponse(200, "application/json; charset=utf-8", sb.ToString());
    }

    /// <summary>One inventory row. <c>count</c> keeps T101's key; <c>name</c> is new and may be
    /// empty, which is what the page renders as the bare template id.</summary>
    private static string ItemJson(CharacterStore.ItemRow it)
        => "{\"itemDbId\":" + it.ItemDbId
         + ",\"templateId\":" + it.TemplateId
         + ",\"slot\":" + it.Slot
         + ",\"inven\":" + it.InvenType
         + ",\"count\":" + it.Amount
         + ",\"name\":" + Str(Protocol.ItemNames.Lookup(it.TemplateId)) + "}";

    /// <summary>One warehouse tab: its money, its slot count and its rows.</summary>
    private void AppendWarehouse(StringBuilder sb, CharacterRecord c, int invenType, string label, bool first)
    {
        // Inven 1 is keyed on the ACCOUNT, inven 9 on the character - TransSQLExec's mask 0x1132.
        long owner = invenType == World.WarehouseHandlers.InvenAccountWarehouse ? c.AccountId : c.Id;
        var (money, slots) = _store.GetWarehouse(owner, invenType);
        var rows = _store.GetItems(owner, invenType);

        if (!first) sb.Append(',');
        sb.Append("{\"tab\":").Append(Str(label))
          .Append(",\"inven\":").Append(invenType)
          .Append(",\"money\":").Append(money)
          .Append(",\"slots\":").Append(slots)
          .Append(",\"items\":[");
        for (int i = 0; i < rows.Count; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append(ItemJson(rows[i]));
        }
        sb.Append("]}");
    }

    /// <summary>GET /api/online - WorldBridge.InWorldSessions(), through the delegate.</summary>
    private AdminResponse Online()
    {
        var rows = _online();
        var sb = new StringBuilder();
        sb.Append("{\"result\":").Append(ResultOk).Append(",\"online\":[");
        for (int i = 0; i < rows.Count; i++)
        {
            var o = rows[i];
            if (i > 0) sb.Append(',');
            sb.Append("{\"playerId\":").Append(o.PlayerId)
              .Append(",\"name\":").Append(Str(o.Name))
              .Append(",\"level\":").Append(o.Level)
              .Append(",\"zone\":").Append(o.Zone)
              .Append(",\"account\":").Append(Str(o.AccountName)).Append('}');
        }
        sb.Append("]}");
        return new AdminResponse(200, "application/json; charset=utf-8", sb.ToString());
    }

    /// <summary>GET /api/admin-log?limit=N - what this tool has done.</summary>
    private AdminResponse AdminLog(IReadOnlyDictionary<string, string> q)
    {
        int limit = 100;
        string? l = Get(q, "limit");
        if (l != null && int.TryParse(l, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n))
            limit = n < 1 ? 1 : (n > 500 ? 500 : n);

        var rows = _store.GetAdminLog(limit);
        var sb = new StringBuilder();
        sb.Append("{\"result\":").Append(ResultOk).Append(",\"log\":[");
        for (int i = 0; i < rows.Count; i++)
        {
            var e = rows[i];
            if (i > 0) sb.Append(',');
            sb.Append("{\"at\":").Append(e.At)
              .Append(",\"sourceIp\":").Append(Str(e.SourceIp))
              .Append(",\"action\":").Append(Str(e.Action))
              .Append(",\"target\":").Append(Str(e.Target))
              .Append(",\"reason\":").Append(Str(e.Reason))
              .Append(",\"result\":").Append(e.Result).Append('}');
        }
        sb.Append("]}");
        return new AdminResponse(200, "application/json; charset=utf-8", sb.ToString());
    }

    /// <summary>
    /// POST /api/restore-character {"id":N,"reason":"..."} - the retail WA_UNDELETE_USER.
    ///
    /// <para>T101b made this real. T88's <c>OnDeleteUser</c> hard-deleted the row, so phase 1
    /// could only rescue a character the client had scheduled and not yet confirmed. The delete
    /// is a SOFT delete now: the row stays, its items wait in <c>deleted_items</c>, and
    /// <c>CharacterStore.PurgeExpiredDeletes</c> is what finally removes it once
    /// <c>deleteCharacterExpireHour2</c> (72 h) has run out. So this undoes a real delete for the
    /// whole window, items included, and answers 2 only once the purge has been through.</para>
    /// </summary>
    private AdminResponse RestoreCharacter(string? body, string? sourceIp)
    {
        int id = (int)(JsonNumber(body, "id") ?? -1);
        string reason = JsonString(body, "reason") ?? string.Empty;
        if (id <= 0) return Json(400, ResultInvalid, "id is required");

        var c = _store.GetCharacter(id);
        if (c == null)
        {
            Log(sourceIp, "restore-character", id.ToString(CultureInfo.InvariantCulture), reason, ResultNotFound);
            return Json(404, ResultNotFound, "no such character - the purge has already been through");
        }
        if (_store.GetCharacterDeleteAt(id) == 0)
        {
            Log(sourceIp, "restore-character", c.Name, reason, ResultInvalid);
            return Json(409, ResultInvalid, "that character has no delete pending");
        }

        bool ok = _store.CancelCharacterDelete(id, c.AccountId);
        Log(sourceIp, "restore-character", c.Name, reason, ok ? ResultOk : ResultRefused);
        return ok
            ? Json(200, ResultOk, $"delete cancelled for {c.Name}")
            : Json(500, ResultRefused, "the store refused the cancel");
    }

    // ------------------------------------------------------------------------- phase 2 (T101b)

    /// <summary>Resolve the target of a write: {"id":N} or {"name":"X"}.</summary>
    private CharacterRecord? Target(string? body)
    {
        int id = (int)(JsonNumber(body, "id") ?? 0);
        if (id > 0) return _store.GetCharacter(id);
        string? name = JsonString(body, "name");
        return string.IsNullOrEmpty(name) ? null : _store.GetCharacterByName(name);
    }

    /// <summary>POST /api/set-money {"id":N,"money":M,"reason":"..."} - WA_CHANGE_MONEY.</summary>
    private AdminResponse SetMoney(string? body, string? ip)
    {
        var c = Target(body);
        string reason = JsonString(body, "reason") ?? string.Empty;
        if (c == null) { Log(ip, "set-money", "?", reason, ResultNotFound); return Json(404, ResultNotFound, "no such character"); }
        double? m = JsonNumber(body, "money");
        if (m == null || m < 0) { Log(ip, "set-money", c.Name, reason, ResultInvalid); return Json(400, ResultInvalid, "money must be >= 0"); }

        long old = _store.GetCharacterMoney(c.Id);
        long now = _store.SetCharacterMoney(c.Id, (long)m.Value);
        Log(ip, "set-money", c.Name, reason, ResultOk);
        // The retail reply carries both figures (WEBADMIN-DESIGN.md section 2), so this does too.
        return new AdminResponse(200, "application/json; charset=utf-8",
            "{\"result\":" + ResultOk + ",\"oldMoney\":" + old + ",\"newMoney\":" + now + "}");
    }

    /// <summary>The level ceiling the tool will set. Retail caps at 70 in this build.</summary>
    public const int MaxLevel = 70;

    /// <summary>POST /api/set-level {"id":N,"level":L,"reason":"..."}.</summary>
    private AdminResponse SetLevel(string? body, string? ip)
    {
        var c = Target(body);
        string reason = JsonString(body, "reason") ?? string.Empty;
        if (c == null) { Log(ip, "set-level", "?", reason, ResultNotFound); return Json(404, ResultNotFound, "no such character"); }
        double? l = JsonNumber(body, "level");
        if (l == null || l < 1 || l > MaxLevel) { Log(ip, "set-level", c.Name, reason, ResultInvalid); return Json(400, ResultInvalid, $"level must be 1..{MaxLevel}"); }

        bool ok = _store.UpdateLevelAndExp(c.Id, (int)l.Value, c.Exp);
        Log(ip, "set-level", c.Name, reason, ok ? ResultOk : ResultRefused);
        return ok ? Json(200, ResultOk, $"{c.Name} is level {(int)l.Value}")
                  : Json(500, ResultRefused, "the store refused the update");
    }

    /// <summary>
    /// POST /api/give-item {"id":N,"templateId":T,"amount":A,"reason":"..."} - WA_ADD_ITEM,
    /// minus the 262-byte option block. WEBADMIN-DESIGN.md section 6 budgets that block its own
    /// task; a plain template and amount is what the tool needs for a make-good.
    /// </summary>
    private AdminResponse GiveItem(string? body, string? ip)
    {
        var c = Target(body);
        string reason = JsonString(body, "reason") ?? string.Empty;
        if (c == null) { Log(ip, "give-item", "?", reason, ResultNotFound); return Json(404, ResultNotFound, "no such character"); }
        int template = (int)(JsonNumber(body, "templateId") ?? 0);
        long amount = (long)(JsonNumber(body, "amount") ?? 1);
        if (template <= 0 || amount <= 0)
        { Log(ip, "give-item", c.Name, reason, ResultInvalid); return Json(400, ResultInvalid, "templateId and amount must be positive"); }

        int itemId = _store.NextItemId();
        int slot = NextFreeSlot(c.Id);
        _store.UpsertItem(itemId, c.Id, 0, slot, template, amount);
        Log(ip, "give-item", c.Name, reason, ResultOk);
        return new AdminResponse(200, "application/json; charset=utf-8",
            "{\"result\":" + ResultOk + ",\"itemDbId\":" + itemId + ",\"slot\":" + slot + "}");
    }

    private int NextFreeSlot(long ownerDbId)
    {
        int slot = 0;
        foreach (var it in _store.GetItems(ownerDbId, 0)) if (it.Slot >= slot) slot = it.Slot + 1;
        return slot;
    }

    /// <summary>
    /// POST /api/ban and /api/unban {"id":N,"hours":H,"reason":"..."} - the retail
    /// WA_ADD/DEL_CHARACTER_RESTRICTION pair. <c>hours</c> 0 is permanent.
    /// </summary>
    private AdminResponse Ban(string? body, string? ip, bool add)
    {
        string what = add ? "ban" : "unban";
        var c = Target(body);
        string reason = JsonString(body, "reason") ?? string.Empty;
        if (c == null) { Log(ip, what, "?", reason, ResultNotFound); return Json(404, ResultNotFound, "no such character"); }

        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (!add)
        {
            bool lifted = _store.RemoveRestriction(c.Id, CharacterStore.RestrictionBan);
            Log(ip, what, c.Name, reason, lifted ? ResultOk : ResultNotFound);
            return lifted ? Json(200, ResultOk, $"{c.Name} is unbanned")
                          : Json(404, ResultNotFound, "that character is not banned");
        }
        double hours = JsonNumber(body, "hours") ?? 0;
        if (hours < 0) { Log(ip, what, c.Name, reason, ResultInvalid); return Json(400, ResultInvalid, "hours must be >= 0"); }
        long until = hours <= 0 ? 0 : now + (long)(hours * 3600);
        bool ok = _store.AddRestriction(c.Id, CharacterStore.RestrictionBan, 1, until, reason, now);
        Log(ip, what, c.Name, reason, ok ? ResultOk : ResultRefused);
        return new AdminResponse(200, "application/json; charset=utf-8",
            "{\"result\":" + (ok ? ResultOk : ResultRefused) + ",\"until\":" + until + "}");
    }

    /// <summary>POST /api/kick {"id":N,"reason":"..."} - WA_FORCE_KICK, live sessions only.</summary>
    private AdminResponse Kick(string? body, string? ip)
    {
        int id = (int)(JsonNumber(body, "id") ?? 0);
        string reason = JsonString(body, "reason") ?? string.Empty;
        if (id <= 0) return Json(400, ResultInvalid, "id is required");
        bool ok = KickPlayer?.Invoke(id) ?? false;
        Log(ip, "kick", id.ToString(CultureInfo.InvariantCulture), reason, ok ? ResultOk : ResultNotFound);
        return ok ? Json(200, ResultOk, "disconnected")
                  : Json(404, ResultNotFound, "that player is not online");
    }

    /// <summary>POST /api/announce {"text":"...","reason":"..."} - WA_INSTANT_INGAME_ANNOUNCE.</summary>
    private AdminResponse Announcement(string? body, string? ip)
    {
        string text = JsonString(body, "text") ?? string.Empty;
        string reason = JsonString(body, "reason") ?? string.Empty;
        if (text.Length == 0) return Json(400, ResultInvalid, "text is required");
        int sent = Announce?.Invoke(text) ?? 0;
        Log(ip, "announce", text.Length > 40 ? text[..40] : text, reason, ResultOk);
        return new AdminResponse(200, "application/json; charset=utf-8",
            "{\"result\":" + ResultOk + ",\"sent\":" + sent + "}");
    }

    /// <summary>POST /api/gm-level {"accountId":N,"level":L,"reason":"..."}.</summary>
    private AdminResponse GmLevel(string? body, string? ip)
    {
        long accountId = (long)(JsonNumber(body, "accountId") ?? 0);
        string reason = JsonString(body, "reason") ?? string.Empty;
        double? level = JsonNumber(body, "level");
        if (accountId <= 0 || level == null || level < 0)
            return Json(400, ResultInvalid, "accountId and a level >= 0 are required");
        var acct = _store.GetAccountById(accountId);
        if (acct == null) { Log(ip, "gm-level", accountId.ToString(CultureInfo.InvariantCulture), reason, ResultNotFound); return Json(404, ResultNotFound, "no such account"); }

        bool ok = _store.SetAdminLevel(accountId, (int)level.Value);
        Log(ip, "gm-level", acct.Name, reason, ok ? ResultOk : ResultRefused);
        return ok ? Json(200, ResultOk, $"{acct.Name} is admin level {(int)level.Value}")
                  : Json(500, ResultRefused, "the store refused the update");
    }

    /// <summary>GET /api/deleted - characters waiting out the 72 h window.</summary>
    private AdminResponse Deleted(IReadOnlyDictionary<string, string> q)
    {
        int limit = 50;
        string? l = Get(q, "limit");
        if (l != null && int.TryParse(l, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n))
            limit = n < 1 ? 1 : (n > 200 ? 200 : n);
        var rows = _store.GetDeletedCharacters(limit);
        var sb = new StringBuilder();
        sb.Append("{\"result\":").Append(ResultOk).Append(",\"deleted\":[");
        for (int i = 0; i < rows.Count; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append(CharacterBrief(rows[i]));
        }
        sb.Append("]}");
        return new AdminResponse(200, "application/json; charset=utf-8", sb.ToString());
    }

    // ------------------------------------------------------------------------- T106: status

    /// <summary>
    /// GET /api/status - what the Status tab shows: uptime, the World link, who is online and
    /// what the process is holding. Everything that is not the store comes through
    /// <see cref="WorldStatus"/>, so this file still has no idea what a WorldBridge is.
    /// </summary>
    private AdminResponse Status()
    {
        var up = DateTimeOffset.UtcNow - StartedAt;
        var w = WorldStatus?.Invoke() ?? (0, false, 0);
        long managed = GC.GetTotalMemory(false);
        long working = 0;
        try { working = Environment.WorkingSet; } catch (Exception) { }

        var sb = new StringBuilder();
        sb.Append("{\"result\":").Append(ResultOk)
          .Append(",\"uptimeSeconds\":").Append((long)up.TotalSeconds)
          .Append(",\"startedAt\":").Append(Str(StartedAt.ToString("o", CultureInfo.InvariantCulture)))
          .Append(",\"worldLinks\":").Append(w.Item1)
          .Append(",\"worldReady\":").Append(w.Item2 ? "true" : "false")
          .Append(",\"online\":").Append(w.Item3)
          .Append(",\"managedBytes\":").Append(managed)
          .Append(",\"workingSetBytes\":").Append(working)
          .Append(",\"gc0\":").Append(GC.CollectionCount(0))
          .Append(",\"gc2\":").Append(GC.CollectionCount(2))
          .Append(",\"threads\":").Append(System.Diagnostics.Process.GetCurrentProcess().Threads.Count)
          .Append(",\"logFile\":").Append(Str(ArbiterLogProvider.CurrentPath))
          .Append(",\"consoleLevel\":").Append(Str(ArbiterLogProvider.ConsoleLevel().ToString()))
          .Append('}');
        return new AdminResponse(200, "application/json; charset=utf-8", sb.ToString());
    }

    /// <summary>The Status tab's tail size, and the ceiling on ?lines=.</summary>
    public const int DefaultTailLines = 200, MaxTailLines = 2000;

    /// <summary>
    /// GET /api/log?lines=N - the newest N log lines, oldest first, straight out of
    /// <see cref="ArbiterLogProvider"/>'s ring. It never touches the file, so a 2-second poll
    /// costs nothing and cannot collide with the writer.
    /// </summary>
    private AdminResponse LogTail(IReadOnlyDictionary<string, string> q)
    {
        int lines = DefaultTailLines;
        string? l = Get(q, "lines");
        if (l != null && int.TryParse(l, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n))
            lines = n < 1 ? 1 : (n > MaxTailLines ? MaxTailLines : n);

        var rows = ArbiterLogProvider.Tail(lines);
        var sb = new StringBuilder();
        sb.Append("{\"result\":").Append(ResultOk).Append(",\"lines\":[");
        for (int i = 0; i < rows.Count; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append(Str(rows[i]));
        }
        sb.Append("]}");
        return new AdminResponse(200, "application/json; charset=utf-8", sb.ToString());
    }

    // ======================================================================= T101c: the pages

    /// <summary>Resolve a character from ?id= or ?name=.</summary>
    private CharacterRecord? FromQuery(IReadOnlyDictionary<string, string> q)
    {
        string? idText = Get(q, "id");
        if (idText != null && int.TryParse(idText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int id))
            return _store.GetCharacter(id);
        string? name = Get(q, "name");
        return string.IsNullOrEmpty(name) ? null : _store.GetCharacterByName(name);
    }

    /// <summary>
    /// GET /api/account?id=N or ?name=X - the account page. Everything the account HAS, rather
    /// than everything the account IS: TeraSharp's <c>accounts</c> row is only (id, name,
    /// admin_level, created_at, play_time_sec), so the rest is derived from its characters.
    /// </summary>
    private AdminResponse Account(IReadOnlyDictionary<string, string> q)
    {
        AccountRecord? a = null;
        string? idText = Get(q, "id");
        if (idText != null && long.TryParse(idText, NumberStyles.Integer, CultureInfo.InvariantCulture, out long id))
            a = _store.GetAccountById(id);
        else
        {
            string? name = Get(q, "name");
            if (!string.IsNullOrEmpty(name)) a = _store.GetAccount(name);
        }
        if (a == null) return Json(404, ResultNotFound, "no such account");

        var chars = _store.GetCharacters(a.Id);
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        DateTime lastLogin = default;
        foreach (var c in chars) if (c.LastLogin > lastLogin) lastLogin = c.LastLogin;

        var sb = new StringBuilder();
        sb.Append("{\"result\":").Append(ResultOk)
          .Append(",\"account\":{\"id\":").Append(a.Id)
          .Append(",\"name\":").Append(Str(a.Name))
          .Append(",\"adminLevel\":").Append(a.AdminLevel)
          .Append(",\"playTimeSec\":").Append(_store.GetAccountPlayTime(a.Id))
          .Append(",\"characterCount\":").Append(chars.Count)
          .Append(",\"lastLogin\":").Append(Str(lastLogin == default
              ? string.Empty : lastLogin.ToString("o", CultureInfo.InvariantCulture)))
          .Append('}');

        sb.Append(",\"characters\":[");
        for (int i = 0; i < chars.Count; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append(CharacterBrief(chars[i]));
        }
        sb.Append(']');

        sb.Append(",\"benefits\":[");
        var benefits = _store.GetAccountBenefits(a.Id);
        for (int i = 0; i < benefits.Count; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append("{\"packageId\":").Append(benefits[i].PackageId)
              .Append(",\"expiresAt\":").Append(benefits[i].ExpiresAt)
              .Append(",\"value\":").Append(benefits[i].Value).Append('}');
        }
        sb.Append(']');

        // Bans are per CHARACTER in this schema, so the account view is the union over its own.
        sb.Append(",\"bans\":[");
        bool first = true;
        foreach (var c in chars)
            foreach (var r in _store.GetRestrictions(c.Id))
            {
                if (!first) sb.Append(',');
                first = false;
                sb.Append(RestrictionJson(c, r, now));
            }
        sb.Append("]}");
        return new AdminResponse(200, "application/json; charset=utf-8", sb.ToString());
    }

    /// <summary>One restriction, with the character it is on and whether it is still biting.</summary>
    private static string RestrictionJson(CharacterRecord c, CharacterStore.RestrictionRow r, long now)
        => "{\"characterId\":" + c.Id
         + ",\"character\":" + Str(c.Name)
         + ",\"type\":" + r.Type
         + ",\"typeName\":" + Str(r.Type == CharacterStore.RestrictionBan ? "ban"
                                : r.Type == CharacterStore.RestrictionMute ? "mute" : "other")
         + ",\"level\":" + r.Level
         + ",\"until\":" + r.Until
         + ",\"active\":" + ((r.Until == 0 || r.Until > now) ? "true" : "false")
         + ",\"reason\":" + Str(r.Reason)
         + ",\"setAt\":" + r.SetAt + "}";

    /// <summary>GET /api/restrictions?id=N - the ban/mute list for one character.</summary>
    private AdminResponse Restrictions(IReadOnlyDictionary<string, string> q)
    {
        var c = FromQuery(q);
        if (c == null) return Json(404, ResultNotFound, "no such character");
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var sb = new StringBuilder();
        sb.Append("{\"result\":").Append(ResultOk).Append(",\"restrictions\":[");
        var rows = _store.GetRestrictions(c.Id);
        for (int i = 0; i < rows.Count; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append(RestrictionJson(c, rows[i], now));
        }
        sb.Append("]}");
        return new AdminResponse(200, "application/json; charset=utf-8", sb.ToString());
    }

    /// <summary>
    /// GET /api/search?q=&amp;kind=name|id|guild|online - the one search box.
    /// <c>name</c> is a prefix match on characters, <c>id</c> takes a character or account id,
    /// <c>guild</c> lists a guild's roster and <c>online</c> filters the live list.
    /// </summary>
    private AdminResponse Search(IReadOnlyDictionary<string, string> q)
    {
        string term = (Get(q, "q") ?? string.Empty).Trim();
        string kind = (Get(q, "kind") ?? "name").Trim().ToLowerInvariant();
        var hits = new List<CharacterRecord>();
        string note = string.Empty;

        switch (kind)
        {
            case "id":
            {
                if (int.TryParse(term, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n))
                {
                    var c = _store.GetCharacter(n);
                    if (c != null) hits.Add(c);
                    else hits.AddRange(_store.GetCharacters(n));   // then try it as an account id
                }
                break;
            }
            case "guild":
            {
                var g = _store.GetGuildByName(term);
                if (g == null) { note = "no such guild"; break; }
                note = g.Name;
                foreach (var m in _store.GetGuildMembers(g.GuildId))
                {
                    var c = _store.GetCharacter(m.UserDbId);
                    if (c != null) hits.Add(c);
                }
                break;
            }
            case "online":
            {
                foreach (var o in _online())
                {
                    if (term.Length != 0 && o.Name.IndexOf(term, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    var c = _store.GetCharacter(o.PlayerId);
                    if (c != null) hits.Add(c);
                }
                break;
            }
            default:
            {
                foreach (var name in _store.FindCharacterNamesByPrefix(term, SearchLimit))
                {
                    var c = _store.GetCharacterByName(name);
                    if (c != null) hits.Add(c);
                }
                break;
            }
        }

        var live = new HashSet<int>();
        foreach (var o in _online()) live.Add(o.PlayerId);

        var sb = new StringBuilder();
        sb.Append("{\"result\":").Append(ResultOk)
          .Append(",\"kind\":").Append(Str(kind))
          .Append(",\"note\":").Append(Str(note))
          .Append(",\"hits\":[");
        for (int i = 0; i < hits.Count && i < SearchLimit; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append("{\"character\":").Append(CharacterBrief(hits[i]))
              .Append(",\"online\":").Append(live.Contains(hits[i].Id) ? "true" : "false")
              .Append('}');
        }
        sb.Append("]}");
        return new AdminResponse(200, "application/json; charset=utf-8", sb.ToString());
    }

    /// <summary>How many hits one search returns.</summary>
    public const int SearchLimit = 50;

    // ======================================================================= T101c: the writes

    /// <summary>POST /api/mute and /api/unmute - the same shape as ban, other restriction type.</summary>
    private AdminResponse Mute(string? body, string? ip, bool add)
    {
        string what = add ? "mute" : "unmute";
        var c = Target(body);
        string reason = JsonString(body, "reason") ?? string.Empty;
        if (c == null) { Log(ip, what, "?", reason, ResultNotFound); return Json(404, ResultNotFound, "no such character"); }

        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (!add)
        {
            bool lifted = _store.RemoveRestriction(c.Id, CharacterStore.RestrictionMute);
            Log(ip, what, c.Name, reason, lifted ? ResultOk : ResultNotFound);
            return lifted ? Json(200, ResultOk, c.Name + " is unmuted")
                          : Json(404, ResultNotFound, "that character is not muted");
        }
        double hours = JsonNumber(body, "hours") ?? 0;
        if (hours < 0) { Log(ip, what, c.Name, reason, ResultInvalid); return Json(400, ResultInvalid, "hours must be >= 0"); }
        long until = hours <= 0 ? 0 : now + (long)(hours * 3600);
        bool ok = _store.AddRestriction(c.Id, CharacterStore.RestrictionMute, 1, until, reason, now);
        Log(ip, what, c.Name, reason, ok ? ResultOk : ResultRefused);
        return new AdminResponse(200, "application/json; charset=utf-8",
            "{\"result\":" + (ok ? ResultOk : ResultRefused) + ",\"until\":" + until + "}");
    }

    /// <summary>POST /api/warn {"id":N,"text":"...","reason":"..."} - one system message, one player.</summary>
    private AdminResponse Warn(string? body, string? ip)
    {
        int id = (int)(JsonNumber(body, "id") ?? 0);
        string text = JsonString(body, "text") ?? string.Empty;
        string reason = JsonString(body, "reason") ?? string.Empty;
        if (id <= 0 || text.Length == 0) return Json(400, ResultInvalid, "id and text are required");
        bool ok = WarnPlayer?.Invoke(id, text) ?? false;
        Log(ip, "warn", id.ToString(CultureInfo.InvariantCulture), reason, ok ? ResultOk : ResultNotFound);
        return ok ? Json(200, ResultOk, "warned")
                  : Json(404, ResultNotFound, "that player is not online");
    }

    /// <summary>POST /api/teleport {"id":N,"targetId":M} - move id to target. Both must be in world.</summary>
    private AdminResponse Teleport(string? body, string? ip)
    {
        int id = (int)(JsonNumber(body, "id") ?? 0);
        int target = (int)(JsonNumber(body, "targetId") ?? 0);
        string reason = JsonString(body, "reason") ?? string.Empty;
        if (id <= 0 || target <= 0) return Json(400, ResultInvalid, "id and targetId are required");
        if (TeleportTo == null) return Json(501, ResultRefused, "teleport is not wired on this server");
        bool ok = TeleportTo.Invoke(id, target);
        Log(ip, "teleport", id + " -> " + target, reason, ok ? ResultOk : ResultNotFound);
        return ok ? Json(200, ResultOk, "moved")
                  : Json(404, ResultNotFound, "one of them is not online");
    }

    /// <summary>
    /// POST /api/rename {"id":N,"name":"X","reason":"..."} - the admin half of T88's rename.
    /// The name goes through the same <c>CharacterHandlers.ValidateName</c> rules the client
    /// path uses, so the tool cannot create a row the game would reject.
    /// </summary>
    private AdminResponse Rename(string? body, string? ip)
    {
        int id = (int)(JsonNumber(body, "id") ?? 0);
        string name = (JsonString(body, "name") ?? string.Empty).Trim();
        string reason = JsonString(body, "reason") ?? string.Empty;
        var c = id > 0 ? _store.GetCharacter(id) : null;
        if (c == null) { Log(ip, "rename", id.ToString(CultureInfo.InvariantCulture), reason, ResultNotFound); return Json(404, ResultNotFound, "no such character"); }

        var verdict = Handlers.CharacterHandlers.ValidateName(name);
        if (verdict != Handlers.NameCheck.Ok)
        { Log(ip, "rename", c.Name, reason, ResultInvalid); return Json(400, ResultInvalid, "the name is rejected: " + verdict); }
        if (_store.GetCharacterByName(name) != null)
        { Log(ip, "rename", c.Name, reason, ResultInvalid); return Json(409, ResultInvalid, "that name is taken"); }

        bool ok = _store.RenameCharacter(c.Id, name);
        Log(ip, "rename", c.Name + " -> " + name, reason, ok ? ResultOk : ResultRefused);
        return ok ? Json(200, ResultOk, c.Name + " is now " + name)
                  : Json(500, ResultRefused, "the store refused the rename");
    }

    /// <summary>GET /api/announces - the scheduled ones. An instant announce is never a row.</summary>
    private AdminResponse Announces(IReadOnlyDictionary<string, string> q)
    {
        var rows = _store.GetAnnounces(100);
        var sb = new StringBuilder();
        sb.Append("{\"result\":").Append(ResultOk).Append(",\"announces\":[");
        for (int i = 0; i < rows.Count; i++)
        {
            var a = rows[i];
            if (i > 0) sb.Append(',');
            sb.Append("{\"id\":").Append(a.Id)
              .Append(",\"text\":").Append(Str(a.Text))
              .Append(",\"startAt\":").Append(a.StartAt)
              .Append(",\"endAt\":").Append(a.EndAt)
              .Append(",\"intervalSec\":").Append(a.IntervalSec)
              .Append(",\"lastSent\":").Append(a.LastSent)
              .Append(",\"enabled\":").Append(a.Enabled ? "true" : "false")
              .Append(",\"createdBy\":").Append(Str(a.CreatedBy)).Append('}');
        }
        sb.Append("]}");
        return new AdminResponse(200, "application/json; charset=utf-8", sb.ToString());
    }

    /// <summary>
    /// POST /api/announce-schedule {"text":"...","startAt":U,"endAt":U,"intervalSec":S}.
    /// <c>startAt</c> 0 means now. A row with <c>intervalSec</c> 0 goes out once and disables
    /// itself; the timer that drains them is <c>CharacterStore.TakeDueAnnounces</c>.
    /// </summary>
    private AdminResponse ScheduleAnnounce(string? body, string? ip)
    {
        string text = JsonString(body, "text") ?? string.Empty;
        string reason = JsonString(body, "reason") ?? string.Empty;
        if (text.Length == 0) return Json(400, ResultInvalid, "text is required");

        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        long startAt = (long)(JsonNumber(body, "startAt") ?? 0);
        if (startAt <= 0) startAt = now;
        long endAt = (long)(JsonNumber(body, "endAt") ?? 0);
        long interval = (long)(JsonNumber(body, "intervalSec") ?? 0);
        if (endAt != 0 && endAt < startAt) return Json(400, ResultInvalid, "endAt is before startAt");
        if (interval < 0) return Json(400, ResultInvalid, "intervalSec must be >= 0");

        long id = _store.AddAnnounce(text, startAt, endAt, interval, ip ?? string.Empty, now);
        Log(ip, "announce-schedule", text.Length > 40 ? text[..40] : text, reason, ResultOk);
        return new AdminResponse(200, "application/json; charset=utf-8",
            "{\"result\":" + ResultOk + ",\"id\":" + id + ",\"startAt\":" + startAt + "}");
    }

    /// <summary>POST /api/announce-delete {"id":N}.</summary>
    private AdminResponse DeleteAnnounce(string? body, string? ip)
    {
        long id = (long)(JsonNumber(body, "id") ?? 0);
        string reason = JsonString(body, "reason") ?? string.Empty;
        if (id <= 0) return Json(400, ResultInvalid, "id is required");
        bool ok = _store.DeleteAnnounce(id);
        Log(ip, "announce-delete", id.ToString(CultureInfo.InvariantCulture), reason, ok ? ResultOk : ResultNotFound);
        return ok ? Json(200, ResultOk, "removed")
                  : Json(404, ResultNotFound, "no such announce");
    }

    private void Log(string? ip, string action, string target, string reason, int result)
    {
        _store.AddAdminLog(DateTimeOffset.UtcNow.ToUnixTimeSeconds(), ip ?? string.Empty,
            action, target, reason, result);
        _log.LogInformation("admin {Action} {Target} from {Ip} -> {Result} ({Reason})",
            action, target, ip ?? "?", result, reason);
    }

    // ------------------------------------------------------------------------------ helpers

    private static string? Get(IReadOnlyDictionary<string, string> q, string key)
        => q.TryGetValue(key, out var v) ? v : null;

    private static AdminResponse Json(int status, int result, string message)
        => new(status, "application/json; charset=utf-8",
            "{\"result\":" + result.ToString(CultureInfo.InvariantCulture) + ",\"message\":" + Str(message) + "}");

    private static string CharacterBrief(CharacterRecord c)
        => "{\"id\":" + c.Id
         + ",\"accountId\":" + c.AccountId
         + ",\"name\":" + Str(c.Name)
         + ",\"level\":" + c.Level
         + ",\"race\":" + c.Race
         + ",\"class\":" + c.Class
         + ",\"gender\":" + c.Gender
         + ",\"zone\":" + c.Zone
         + ",\"lastLogin\":" + Str(c.LastLogin == default
             ? string.Empty
             : c.LastLogin.ToString("o", CultureInfo.InvariantCulture))
         + "}";

    /// <summary>A JSON string literal. Escapes what RFC 8259 requires and nothing else.</summary>
    public static string Str(string? s)
    {
        var sb = new StringBuilder("\"");
        foreach (char ch in s ?? string.Empty)
        {
            switch (ch)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (ch < ' ') sb.Append("\\u").Append(((int)ch).ToString("x4", CultureInfo.InvariantCulture));
                    else sb.Append(ch);
                    break;
            }
        }
        return sb.Append('"').ToString();
    }

    /// <summary>The number after "key": in a flat JSON body, or null. Deliberately tiny - the
    /// bodies this tool posts are three fields long and pulling in a parser is not worth it.</summary>
    public static double? JsonNumber(string? body, string key)
    {
        string? raw = RawValue(body, key);
        if (raw == null) return null;
        return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) ? d : null;
    }

    /// <summary>The string after "key": in a flat JSON body, with the standard escapes undone.</summary>
    public static string? JsonString(string? body, string key)
    {
        if (body == null) return null;
        int i = body.IndexOf("\"" + key + "\"", StringComparison.Ordinal);
        if (i < 0) return null;
        i = body.IndexOf(':', i);
        if (i < 0) return null;
        while (++i < body.Length && char.IsWhiteSpace(body[i])) { }
        if (i >= body.Length || body[i] != '"') return null;
        var sb = new StringBuilder();
        for (int j = i + 1; j < body.Length; j++)
        {
            char ch = body[j];
            if (ch == '\\' && j + 1 < body.Length)
            {
                char n = body[++j];
                sb.Append(n switch
                {
                    'n' => '\n', 'r' => '\r', 't' => '\t', 'b' => '\b', 'f' => '\f',
                    'u' when j + 4 < body.Length => Unicode(body, ref j),
                    _ => n,
                });
                continue;
            }
            if (ch == '"') break;
            sb.Append(ch);
        }
        return sb.ToString();
    }

    private static char Unicode(string body, ref int j)
    {
        char c = (char)int.Parse(body.Substring(j + 1, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        j += 4;
        return c;
    }

    private static string? RawValue(string? body, string key)
    {
        if (body == null) return null;
        int i = body.IndexOf("\"" + key + "\"", StringComparison.Ordinal);
        if (i < 0) return null;
        i = body.IndexOf(':', i);
        if (i < 0) return null;
        int start = ++i;
        while (start < body.Length && char.IsWhiteSpace(body[start])) start++;
        int end = start;
        while (end < body.Length && (char.IsDigit(body[end]) || body[end] == '-' || body[end] == '+'
                                     || body[end] == '.' || body[end] == 'e' || body[end] == 'E')) end++;
        return end > start ? body[start..end] : null;
    }
}
