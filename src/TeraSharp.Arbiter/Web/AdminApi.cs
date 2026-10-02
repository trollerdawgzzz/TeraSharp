// SPDX-License-Identifier: MIT
// Copyright (c) 2026 the TeraSharp contributors

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

/// <summary>
/// One HTTP answer: a status, a content type and a body. T206 added the two optional headers the
/// embedded UI needs - <paramref name="CacheControl"/> so a browser does not keep yesterday's
/// script, and <paramref name="ETag"/> so an operator can tell which UI build is being served.
/// Both are null on every JSON answer, which is why they are at the end with defaults.
/// </summary>
public sealed record AdminResponse(int Status, string ContentType, string Body,
    string? CacheControl = null, string? ETag = null);

/// <summary>A live session as the online list shows it. Supplied by a delegate so this file
/// never depends on WorldBridge and the tests need no server.</summary>
public sealed record AdminOnlineRow(int PlayerId, string Name, int Level, int Zone, string AccountName);

public sealed class AdminApi
{
    /// <summary>Retail result codes, from WEBADMIN-DESIGN.md section 2.</summary>
    public const int ResultOk = 0, ResultNotFound = 2, ResultInvalid = 3, ResultRefused = 0x16;

    /// <summary>The content type every JSON answer carries. T206 named the literal that was
    /// repeated at forty call sites so a new endpoint cannot get it subtly wrong.</summary>
    public const string JsonType = "application/json; charset=utf-8";

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
        // T206: the same reasoning, now for a multi-file app. index.html, app.css, app.js and one
        // module per screen are embedded resources (AdminAssets), so an ES module can import its
        // siblings by URL. Nothing under /api/ can be reached this way, and a path with a slash or
        // a dot-dot in it is refused outright, so no URL walks out of the resource list.
        if (method == "GET" && !path.StartsWith("/api/", StringComparison.Ordinal))
        {
            var asset = AdminAssets.Find(path);
            if (asset != null)
                return new AdminResponse(200, asset.ContentType, asset.Body, "no-store", asset.ETag);
            if (path == "/" || path == "/index.html")
                return new AdminResponse(200, "text/html; charset=utf-8", AdminAssets.MissingPage);
        }

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
        if (method == "GET" && path == "/api/game-log") return GameLog(query);
        if (method == "POST" && path == "/api/restore-character") return RestoreCharacter(body, sourceIp);
        if (method == "POST" && path == "/api/delete-character") return DeleteCharacterNow(body, sourceIp);
        if (method == "POST" && path == "/api/compact-positions") return CompactPositions(body, sourceIp);

        // ---- phase 2 (T101b) ----
        if (method == "POST" && path == "/api/set-money") return SetMoney(body, sourceIp);
        if (method == "POST" && path == "/api/set-level") return SetLevel(body, sourceIp);
        if (method == "POST" && path == "/api/give-item") return GiveItem(body, sourceIp);
        if (method == "POST" && path == "/api/ban") return Ban(body, sourceIp, true);
        if (method == "POST" && path == "/api/unban") return Ban(body, sourceIp, false);
        if (method == "POST" && path == "/api/kick") return Kick(body, sourceIp);
        if (method == "POST" && path == "/api/reset-character") return ResetCharacter(body, sourceIp);
        if (method == "POST" && path == "/api/repair-inventory") return RepairInventory(body, sourceIp);
        if (method == "POST" && path == "/api/remove-benefit") return RemoveBenefit(body, sourceIp);
        if (method == "GET" && path == "/api/server-achievements") return ServerAchievements();
        if (method == "POST" && path == "/api/clear-server-achievement") return ClearServerAchievement(body, sourceIp);
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

        // ---- T206: the single-page admin UI ----
        if (method == "GET" && path == "/api/db") return Db();
        if (method == "GET" && path == "/api/queue") return Queue();
        if (method == "GET" && path == "/api/settings") return Settings();
        if (method == "GET" && path == "/api/account-logins") return AccountLogins(query);
        if (method == "GET" && path == "/api/item-search") return ItemSearch(query);
        if (method == "GET" && path == "/api/achievements") return Achievements(query);
        if (method == "GET" && path == "/api/parcels") return Parcels(query);
        if (method == "GET" && path == "/api/guilds") return Guilds(query);
        if (method == "GET" && path == "/api/guild") return Guild(query);
        if (method == "POST" && path == "/api/set-position") return SetPosition(body, sourceIp);
        if (method == "POST" && path == "/api/remove-item") return RemoveItem(body, sourceIp);
        if (method == "POST" && path == "/api/reset-skills") return ResetSkills(body, sourceIp);
        if (method == "POST" && path == "/api/reset-client-settings") return ResetClientSettings(body, sourceIp);   // T191c
        if (method == "POST" && path == "/api/set-ep") return SetEp(body, sourceIp);
        if (method == "POST" && path == "/api/send-mail") return SendMail(body, sourceIp);
        if (method == "POST" && path == "/api/delete-parcel") return DeleteParcelNow(body, sourceIp);
        if (method == "POST" && path == "/api/guild-money") return GuildMoney(body, sourceIp);
        if (method == "POST" && path == "/api/guild-level") return GuildLevel(body, sourceIp);
        if (method == "POST" && path == "/api/guild-disband") return GuildDisband(body, sourceIp);
        if (method == "POST" && path == "/api/restart-notice") return RestartNotice(body, sourceIp);
        if (method == "POST" && path == "/api/reload-datasheets") return ReloadDatasheets(body, sourceIp);

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

    /// <summary>
    /// GET /api/game-log?who=&amp;category=&amp;action=&amp;from=&amp;to=&amp;page=&amp;size= - T116.
    ///
    /// <para><c>who</c> is one box on the page and can be an account name, a character name, an
    /// account id or a character id. Digits are tried as a CHARACTER id first and only then as
    /// an account id, because every one of the five decoded log frames names a character and
    /// only some name an account (status/GAME-LOG.md section 3). A term that resolves to
    /// nothing answers 2 rather than silently returning the whole log, which is the failure
    /// that makes an audit tool useless.</para>
    ///
    /// <para>The reply carries <c>total</c> from CountGameLog with the same filters, so the
    /// page can show which page of how many without a second call, and <c>categories</c> so the
    /// dropdown is built from the code rather than a copy of the list in the HTML.</para>
    /// </summary>
    private AdminResponse GameLog(IReadOnlyDictionary<string, string> q)
    {
        long accountId = 0, characterId = 0;
        string who = (Get(q, "who") ?? string.Empty).Trim();
        string resolved = string.Empty;
        if (who.Length > 0)
        {
            if (long.TryParse(who, NumberStyles.Integer, CultureInfo.InvariantCulture, out long id)
                && id > 0)
            {
                var byId = id <= int.MaxValue ? _store.GetCharacter((int)id) : null;
                if (byId != null) { characterId = byId.Id; resolved = "character " + byId.Name; }
                else { accountId = id; resolved = "account " + id; }
            }
            else
            {
                var chr = _store.GetCharacterByName(who);
                if (chr != null) { characterId = chr.Id; resolved = "character " + chr.Name; }
                else
                {
                    var acct = _store.GetAccount(who);
                    if (acct == null)
                        return Json(404, ResultNotFound, "no account or character called " + who);
                    accountId = acct.Id;
                    resolved = "account " + acct.Name;
                }
            }
        }

        string? category = Blank(Get(q, "category"));
        string? action = Blank(Get(q, "action"));
        long from = Num(q, "from"), to = Num(q, "to");
        int page = (int)Math.Min(Num(q, "page"), int.MaxValue);
        int size = (int)Math.Min(Num(q, "size"), CharacterStore.GameLogMaxPageSize);
        if (size == 0) size = 50;

        var rows = _store.QueryGameLog(accountId, characterId, category, action, from, to, page, size);
        long total = _store.CountGameLog(accountId, characterId, category, action, from, to);

        var sb = new StringBuilder();
        sb.Append("{\"result\":").Append(ResultOk)
          .Append(",\"total\":").Append(total)
          .Append(",\"page\":").Append(page)
          .Append(",\"size\":").Append(size)
          .Append(",\"who\":").Append(Str(resolved))
          .Append(",\"categories\":[");
        for (int i = 0; i < World.GameLogPackets.Categories.Length; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append(Str(World.GameLogPackets.Categories[i]));
        }
        sb.Append("],\"log\":[");
        for (int i = 0; i < rows.Count; i++)
        {
            var e = rows[i];
            if (i > 0) sb.Append(',');
            sb.Append("{\"logId\":").Append(e.LogId)
              .Append(",\"at\":").Append(e.LoggedAt)
              .Append(",\"category\":").Append(Str(e.Category))
              .Append(",\"action\":").Append(Str(e.Action))
              .Append(",\"accountId\":").Append(e.AccountId)
              .Append(",\"characterId\":").Append(e.CharacterId)
              .Append(",\"actor\":").Append(Str(NameOfCharacter(e.CharacterId)))
              .Append(",\"targetId\":").Append(e.TargetId)
              .Append(",\"target\":").Append(Str(NameOfCharacter(e.TargetId)))
              .Append(",\"itemDbId\":").Append(e.ItemDbId)
              .Append(",\"item\":{\"templateId\":").Append(e.TemplateId)
              .Append(",\"name\":").Append(Str(e.TemplateId == 0
                  ? string.Empty
                  : Protocol.ItemNames.Lookup(e.TemplateId))).Append('}')
              .Append(",\"amount\":").Append(e.Amount)
              .Append(",\"money\":").Append(e.Money)
              .Append(",\"extra\":").Append(Str(e.Extra)).Append('}');
        }
        sb.Append("]}");
        return new AdminResponse(200, "application/json; charset=utf-8", sb.ToString());
    }

    /// <summary>The character's name for a db id, or the empty string - an id that is not ours
    /// (an account id in the CashItemLog case) has no name and the page shows the number.</summary>
    private string NameOfCharacter(long id)
        => id > 0 && id <= int.MaxValue ? _store.GetCharacter((int)id)?.Name ?? string.Empty : string.Empty;

    /// <summary>Null for a missing or all-space query value, so that filter is simply absent.</summary>
    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    /// <summary>A non-negative long from the query bag; 0 for anything missing or unparsable.</summary>
    private static long Num(IReadOnlyDictionary<string, string> q, string key)
        => long.TryParse(Get(q, key), NumberStyles.Integer, CultureInfo.InvariantCulture,
               out long v) && v > 0 ? v : 0;

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

    /// <summary>
    /// POST /api/delete-character {"id":N,"reason":"...","hard":false} - the same delete the lobby
    /// does.
    ///
    /// <para>T224: this used to be an unconditional hard delete while <c>C_DELETE_USER</c> was an
    /// unconditional 72 h soft delete, so the two disagreed about what "delete" means and neither
    /// read ServerConfig.xml. Both now go through <see cref="CharacterDeletion.Delete"/>, which
    /// takes the window from <c>&lt;DeleteUser&gt;</c>: a zero window is a hard delete, anything
    /// else is the soft delete <c>/api/restore-character</c> undoes. <c>"hard":true</c> keeps the
    /// old behaviour for test characters and GM clean-up, and says so in the audit row.</para>
    ///
    /// <para>Refused while the character is online - a live session keeps its own state in World
    /// until it leaves.</para>
    /// </summary>
    private AdminResponse DeleteCharacterNow(string? body, string? sourceIp)
    {
        int id = (int)(JsonNumber(body, "id") ?? -1);
        string reason = JsonString(body, "reason") ?? string.Empty;
        if (id <= 0) return Json(400, ResultInvalid, "id is required");
        var c = _store.GetCharacter(id);
        if (c == null) { Log(sourceIp, "delete-character", id.ToString(CultureInfo.InvariantCulture), reason, ResultNotFound); return Json(404, ResultNotFound, "no such character"); }

        bool forceHard = JsonBool(body, "hard") ?? false;
        var policy = forceHard
            ? new CharacterDeletion.Policy(0, 0, int.MaxValue)
            : CharacterDeletion.Current;
        var outcome = CharacterDeletion.Delete(
            _store, id, c.AccountId, "admin:" + (sourceIp ?? "?"), c.Level,
            DateTimeOffset.UtcNow, policy, out long deleteAt);
        Log(sourceIp, "delete-character", c.Name, reason,
            outcome == CharacterDeletion.Outcome.Refused ? ResultRefused : ResultOk);
        return outcome switch
        {
            CharacterDeletion.Outcome.Hard => Json(200, ResultOk, $"{c.Name} deleted"),
            CharacterDeletion.Outcome.Scheduled => Json(200, ResultOk,
                $"{c.Name} scheduled for deletion at {deleteAt.ToString(CultureInfo.InvariantCulture)} - "
                + "/api/restore-character undoes it"),
            _ => Json(500, ResultRefused, "the store refused the delete (dependent rows?)"),
        };
    }

    /// <summary>Resolve the target of a write: {"id":N} or {"name":"X"}.</summary>
    private CharacterRecord? Target(string? body)
    {
        int id = (int)(JsonNumber(body, "id") ?? 0);
        if (id > 0) return _store.GetCharacter(id);
        string? name = JsonString(body, "name");
        return string.IsNullOrEmpty(name) ? null : _store.GetCharacterByName(name);
    }

    /// <summary>POST /api/set-money {"id":N,"money":M,"reason":"..."} - WA_CHANGE_MONEY.</summary>
    /// <summary>
    /// T219c. POST /api/remove-benefit {"account":N|"name":"...", "package":P} - drop one account
    /// benefit, or every row T181's retired operator experiment wrote when "package" is left out.
    /// With no target at all it sweeps the experiment rows off every account. An expired package
    /// World cannot resolve as a user trait wedges that account's tick, so this is the repair.
    /// </summary>
    private AdminResponse RemoveBenefit(string? body, string? ip)
    {
        string reason = JsonString(body, "reason") ?? string.Empty;
        double? package = JsonNumber(body, "package");
        double? accountId = JsonNumber(body, "account");
        string? accountName = JsonString(body, "name");

        long? target = null;
        if (accountId != null) target = (long)accountId.Value;
        else if (accountName != null)
        {
            var a = _store.GetAccount(accountName);
            if (a == null) { Log(ip, "remove-benefit", accountName, reason, ResultNotFound); return Json(404, ResultNotFound, "no such account"); }
            target = a.Id;
        }

        if (package != null)
        {
            if (target == null) { Log(ip, "remove-benefit", "?", reason, ResultInvalid); return Json(400, ResultInvalid, "package needs an account"); }
            bool gone = _store.RevokeAccountBenefit(target.Value, (int)package.Value);
            Log(ip, "remove-benefit", accountName ?? target.Value.ToString(System.Globalization.CultureInfo.InvariantCulture), reason, gone ? ResultOk : ResultNotFound);
            return gone ? Json(200, ResultOk, "benefit removed") : Json(404, ResultNotFound, "no such benefit");
        }

        int removed = _store.RemoveBenefitExperimentRows(target);
        Log(ip, "remove-benefit", accountName ?? target?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "*", reason, ResultOk);
        return new AdminResponse(200, "application/json; charset=utf-8",
            "{\"result\":" + ResultOk + ",\"removed\":" + removed + "}");
    }

    /// <summary>
    /// T219b. POST /api/repair-inventory {"id":N} - or no target for every character. Re-stamps
    /// each stored 536-byte item record from its row, so a record can no longer name the pocket
    /// and slot the item sat in before a move. Reports the rows it changed.
    /// </summary>
    private AdminResponse RepairInventory(string? body, string? ip)
    {
        string reason = JsonString(body, "reason") ?? string.Empty;
        bool all = JsonNumber(body, "id") == null && JsonString(body, "name") == null;
        var c = all ? null : Target(body);
        if (!all && c == null) { Log(ip, "repair-inventory", "?", reason, ResultNotFound); return Json(404, ResultNotFound, "no such character"); }

        var r = _store.RepairItemRecords(c?.Id);
        Log(ip, "repair-inventory", c?.Name ?? "*", reason, ResultOk);
        return new AdminResponse(200, "application/json; charset=utf-8",
            "{\"result\":" + ResultOk + ",\"scanned\":" + r.Scanned
            + ",\"restamped\":" + r.Restamped + ",\"noRecord\":" + r.NoRecord + "}");
    }

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
        // T152b: and let World make the same change, so its level commit auto-learns the skills
        // that come with the level. Storing the number alone left `test` at 70 with its
        // creation skills. Sent now if the character is in the world, else after its next spawn.
        if (ok) Handlers.WorldLevelSync.Queue(c.Id, (int)l.Value);
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

    /// <summary>
    /// T203. GET /api/server-achievements - who holds each server first, oldest claim first.
    /// One row per achievement, because that is the whole rule: the planet-wide table is keyed on
    /// the achievement id and only the first claimant is ever in it.
    /// </summary>
    private AdminResponse ServerAchievements()
    {
        var sb = new StringBuilder("{\"result\":" + ResultOk + ",\"claims\":[");
        bool first = true;
        foreach (var claim in _store.GetServerAchievements())
        {
            if (!first) sb.Append(',');
            first = false;
            var chr = _store.GetCharacter(claim.OwnerId);
            sb.Append("{\"achievementId\":").Append(claim.AchievementId.ToString(CultureInfo.InvariantCulture))
              .Append(",\"ownerId\":").Append(claim.OwnerId.ToString(CultureInfo.InvariantCulture))
              .Append(",\"name\":").Append(Str(chr?.Name))
              .Append(",\"partyId\":").Append(claim.PartyId.ToString(CultureInfo.InvariantCulture))
              .Append(",\"claimedAt\":").Append(Str(claim.ClaimedAt)).Append('}');
        }
        sb.Append("]}");
        return new AdminResponse(200, "application/json; charset=utf-8", sb.ToString());
    }

    /// <summary>
    /// T203. POST /api/clear-server-achievement {"id":N} or {"all":true} - release a server first
    /// so it can be won again. Retail has the same pair as operator commands
    /// (ArbiterQACommandHandler::ClearServerAchievement / ClearAllServerAchievement).
    /// </summary>
    private AdminResponse ClearServerAchievement(string? body, string? ip)
    {
        int id = (int)(JsonNumber(body, "id") ?? 0);
        // A flat body, like every other route here: {"all":true} or {"all":1}.
        bool all = (JsonString(body, "all") ?? string.Empty) == "true"
                   || body?.Contains("\"all\":true", StringComparison.OrdinalIgnoreCase) == true
                   || (JsonNumber(body, "all") ?? 0) != 0;
        if (id <= 0 && !all) return Json(400, ResultInvalid, "id or all is required");
        int cleared = _store.ClearServerAchievements(all ? 0 : id);
        Log(ip, "clear-server-achievement", all ? "all" : id.ToString(CultureInfo.InvariantCulture),
            string.Empty, ResultOk);
        return new AdminResponse(200, "application/json; charset=utf-8",
            "{\"result\":" + ResultOk + ",\"cleared\":" + cleared.ToString(CultureInfo.InvariantCulture) + "}");
    }

    /// <summary>
    /// T188. POST /api/reset-character {"id":N,"reason":"..."} - throw away the per-character
    /// state the Arbiter holds between World sessions: the enter-world stamp and game id, the
    /// one-shot Alt+A push and the GM invisibility World last reported, a queued match, a
    /// party-match listing, and a T180 hold or leave-dungeon departure. Nothing durable is
    /// touched - no items, skills, quests or money - so this is the repair for a character that
    /// came up wedged after a World restart, and it is safe on one that did not.
    /// </summary>
    private AdminResponse ResetCharacter(string? body, string? ip)
    {
        int id = (int)(JsonNumber(body, "id") ?? 0);
        string reason = JsonString(body, "reason") ?? string.Empty;
        if (id <= 0) return Json(400, ResultInvalid, "id is required");
        var chr = _store.GetCharacter(id);
        if (chr == null)
        {
            Log(ip, "reset-character", id.ToString(CultureInfo.InvariantCulture), reason, ResultNotFound);
            return Json(404, ResultNotFound, "no such character");
        }
        var cleared = World.CharacterTransientState.Reset(id);
        Log(ip, "reset-character", chr.Name, reason, ResultOk);
        var list = string.Join(",", cleared.Select(c => "\"" + c + "\""));
        return new AdminResponse(200, "application/json; charset=utf-8",
            "{\"result\":" + ResultOk + ",\"cleared\":[" + list + "]}");
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
    /// <summary>
    /// POST /api/compact-positions {"accountId":N} - renumber an account's lobby slots into 1..n.
    ///
    /// <para>T228. Positions were handed out as MAX(position)+1, so an account that had ever deleted
    /// a character carried ordinals past its slot count - account 1's five live characters sat at
    /// 5, 7, 8, 9 and 10 - and World refuses entry on the ordinal, not the count. New characters are
    /// fixed by the allocator and existing ones at their next login; this is the same repair on
    /// demand, for an account whose owner is not about to log in.</para>
    /// </summary>
    private AdminResponse CompactPositions(string? body, string? sourceIp)
    {
        long accountId = (long)(JsonNumber(body, "accountId") ?? -1);
        if (accountId <= 0) return Json(400, ResultInvalid, "accountId is required");
        var a = _store.GetAccountById(accountId);
        if (a == null)
        {
            Log(sourceIp, "compact-positions", accountId.ToString(CultureInfo.InvariantCulture), string.Empty, ResultNotFound);
            return Json(404, ResultNotFound, "no such account");
        }
        var before = _store.OccupiedPositions(accountId);
        int moved = _store.CompactPositions(accountId);
        var after = _store.OccupiedPositions(accountId);
        Log(sourceIp, "compact-positions", accountId.ToString(CultureInfo.InvariantCulture),
            moved.ToString(CultureInfo.InvariantCulture), ResultOk);
        var sb = new StringBuilder();
        sb.Append("{\"result\":").Append(ResultOk)
          .Append(",\"accountId\":").Append(accountId)
          .Append(",\"moved\":").Append(moved)
          .Append(",\"slots\":").Append(Handlers.QaUtilityCommands.CharacterSlots(_store, accountId))
          .Append(",\"before\":[").Append(string.Join(",", before)).Append(']')
          .Append(",\"after\":[").Append(string.Join(",", after)).Append("]}");
        return new AdminResponse(200, "application/json; charset=utf-8", sb.ToString());
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
          // T228: the cap, and every ordinal in use including pending-delete rows the list omits
          .Append(",\"characterSlots\":").Append(Handlers.QaUtilityCommands.CharacterSlots(_store, a.Id))
          .Append(",\"slotsOccupied\":[").Append(string.Join(",", _store.OccupiedPositions(a.Id))).Append(']')
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

    // =========================================================== T206: the single-page admin UI
    //
    // Everything below exists because a screen needs it. The seven screens are Dashboard,
    // Accounts, Characters, Mail, Guilds, Server and Settings (docs/ADMIN.md); T101-T106 already
    // covered most of Accounts and Characters, so what is new here is the state the Dashboard
    // and Settings screens read, the whole of Mail and Guilds, and the handful of per-character
    // writes the retail right-hand action menu offers that this tool did not.

    /// <summary>GET /api/db - the database report the Settings and Dashboard screens show.</summary>
    private AdminResponse Db()
    {
        var s = _store.GetDatabaseStats();
        var sb = new StringBuilder("{\"result\":").Append(ResultOk)
            .Append(",\"path\":").Append(Str(s.Path))
            .Append(",\"bytes\":").Append(s.Bytes)
            .Append(",\"walBytes\":").Append(s.WalBytes)
            .Append(",\"pageCount\":").Append(s.PageCount)
            .Append(",\"pageSize\":").Append(s.PageSize)
            .Append(",\"userVersion\":").Append(s.UserVersion)
            .Append(",\"tables\":[");
        for (int i = 0; i < s.Tables.Count; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append("{\"name\":").Append(Str(s.Tables[i].Name))
              .Append(",\"rows\":").Append(s.Tables[i].Rows).Append('}');
        }
        sb.Append("]}");
        return new AdminResponse(200, JsonType, sb.ToString());
    }

    /// <summary>
    /// GET /api/queue - the matchmaking pool and the party count, which is the one piece of
    /// Dashboard state that lives in memory rather than in the database. Reads
    /// <see cref="World.MatchQueueManager.All"/>, so an empty array means nobody is queued, not
    /// that the read failed.
    /// </summary>
    private AdminResponse Queue()
    {
        var entries = World.MatchQueueManager.All();
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var sb = new StringBuilder("{\"result\":").Append(ResultOk)
            .Append(",\"parties\":").Append(World.PartyWiring.Manager.PartyCount)
            .Append(",\"queued\":").Append(entries.Count)
            .Append(",\"queues\":[");
        for (int i = 0; i < entries.Count; i++)
        {
            var e = entries[i];
            if (i > 0) sb.Append(',');
            sb.Append("{\"leader\":").Append(e.LeaderPlayerId)
              .Append(",\"size\":").Append(e.Size)
              .Append(",\"isParty\":").Append(e.IsParty ? "true" : "false")
              .Append(",\"state\":").Append(Str(e.State.ToString()))
              .Append(",\"matched\":").Append(e.Matched ? "true" : "false")
              .Append(",\"matchedInstanceId\":").Append(e.MatchedInstanceId)
              .Append(",\"queuedAt\":").Append(e.QueuedAt.ToUnixTimeSeconds())
              .Append(",\"waitSeconds\":").Append(Math.Max(0, now - e.QueuedAt.ToUnixTimeSeconds()))
              .Append(",\"instances\":[");
            for (int j = 0; j < e.InstanceIds.Length; j++)
            {
                if (j > 0) sb.Append(',');
                sb.Append(e.InstanceIds[j]);
            }
            sb.Append("]}");
        }
        sb.Append("]}");
        return new AdminResponse(200, JsonType, sb.ToString());
    }

    /// <summary>
    /// Names whose value is never sent to the browser. Substring match, case-insensitive, so a
    /// setting added later that is called anything like a credential is masked by default rather
    /// than by someone remembering to add it here.
    /// </summary>
    private static readonly string[] SecretMarkers = { "TOKEN", "SECRET", "PASSWORD", "PASSWD", "_KEY", "APIKEY" };

    /// <summary>True when this setting's value must not leave the process.</summary>
    internal static bool IsSecret(string name)
    {
        foreach (string marker in SecretMarkers)
            if (name.Contains(marker, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>
    /// GET /api/settings - the effective configuration, teras.json and environment together,
    /// with every secret replaced by its length rather than its value. The masked form still
    /// answers the question the screen is for ("is the token set, and where did it come from?")
    /// without putting the token in a browser tab, a screenshot or a support ticket.
    /// </summary>
    private AdminResponse Settings()
    {
        var sb = new StringBuilder("{\"result\":").Append(ResultOk)
            .Append(",\"configFile\":").Append(Str(TerasConfig.LoadedPath))
            .Append(",\"configProblem\":").Append(Str(TerasConfig.Problem))
            .Append(",\"itemNames\":{\"count\":").Append(Protocol.ItemNames.Count)
            .Append(",\"source\":").Append(Str(Protocol.ItemNames.Source)).Append('}')
            .Append(",\"datasheetDir\":").Append(Str(SafeDatasheetDir()))
            .Append(",\"ui\":{\"files\":").Append(AdminAssets.All.Count)
            .Append(",\"build\":").Append(Str(UiBuild())).Append('}')
            .Append(",\"describe\":[");
        bool firstLine = true;
        foreach (string line in TerasConfig.Describe())
        {
            if (!firstLine) sb.Append(',');
            firstLine = false;
            sb.Append(Str(line));
        }
        sb.Append("],\"values\":[");

        var seen = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in TerasConfig.Map) seen.Add(pair.Value);
        bool first = true;
        foreach (string name in seen)
        {
            string? value = TerasConfig.Get(name);
            bool secret = IsSecret(name);
            if (!first) sb.Append(',');
            first = false;
            sb.Append("{\"name\":").Append(Str(name))
              .Append(",\"source\":").Append(Str(TerasConfig.SourceOf(name)))
              .Append(",\"set\":").Append(string.IsNullOrEmpty(value) ? "false" : "true")
              .Append(",\"secret\":").Append(secret ? "true" : "false")
              .Append(",\"value\":").Append(Str(secret
                  ? (string.IsNullOrEmpty(value) ? string.Empty : "set, " + value.Length + " characters")
                  : value))
              .Append('}');
        }
        sb.Append("]}");
        return new AdminResponse(200, JsonType, sb.ToString());
    }

    /// <summary>The datasheet directory, or a note - resolving it touches the filesystem.</summary>
    private static string SafeDatasheetDir()
    {
        try { return World.DatasheetLoader.Directory(); }
        catch (Exception e) { return "unresolved: " + e.Message; }
    }

    /// <summary>A fingerprint of the embedded UI, so the screen can prove which build it is.</summary>
    private static string UiBuild()
    {
        var index = AdminAssets.Find(AdminAssets.IndexPath);
        return index?.ETag.Trim('"') ?? string.Empty;
    }

    /// <summary>
    /// GET /api/account-logins?id=N|name=X - the Accounts screen's login history.
    ///
    /// There is no login table: the server stamps <c>characters.last_login</c> and (T206) writes
    /// one <c>game_log</c> row per character that comes online, so the history is that log
    /// filtered to this account, with each character's stamped pair alongside. An account that
    /// has not logged in since this build shipped shows the stamps and an empty log, which is the
    /// truth rather than a gap dressed up as zero activity.
    /// </summary>
    private AdminResponse AccountLogins(IReadOnlyDictionary<string, string> q)
    {
        var account = AccountFromQuery(q);
        if (account == null) return Json(404, ResultNotFound, "no such account");
        int limit = (int)Num(q, "limit");
        if (limit <= 0 || limit > 200) limit = 50;

        var rows = _store.QueryGameLog(accountId: account.Id,
            category: World.GameLogPackets.CategoryUser, action: LoginAction, page: 0, pageSize: limit);
        var sb = new StringBuilder("{\"result\":").Append(ResultOk)
            .Append(",\"account\":{\"id\":").Append(account.Id)
            .Append(",\"name\":").Append(Str(account.Name)).Append('}')
            .Append(",\"characters\":[");
        var characters = _store.GetCharacters(account.Id);
        for (int i = 0; i < characters.Count; i++)
        {
            var c = characters[i];
            if (i > 0) sb.Append(',');
            sb.Append("{\"id\":").Append(c.Id)
              .Append(",\"name\":").Append(Str(c.Name))
              .Append(",\"level\":").Append(c.Level)
              .Append(",\"lastLogin\":").Append(Str(Iso(c.LastLogin)))
              .Append(",\"lastLogout\":").Append(Str(Iso(c.LastLogout))).Append('}');
        }
        sb.Append("],\"logins\":[");
        for (int i = 0; i < rows.Count; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append("{\"at\":").Append(rows[i].LoggedAt)
              .Append(",\"characterId\":").Append(rows[i].CharacterId)
              .Append(",\"character\":").Append(Str(_store.GetCharacterName((int)rows[i].CharacterId)))
              .Append(",\"extra\":").Append(Str(rows[i].Extra)).Append('}');
        }
        sb.Append("]}");
        return new AdminResponse(200, JsonType, sb.ToString());
    }

    /// <summary>The <c>game_log</c> action a character coming online writes. Shared with
    /// <c>SocialHandlers</c>, which is the one place that knows a login happened.</summary>
    public const string LoginAction = "login";

    /// <summary>An account from ?id= or ?name=.</summary>
    private AccountRecord? AccountFromQuery(IReadOnlyDictionary<string, string> q)
    {
        long id = Num(q, "id");
        if (id > 0) return _store.GetAccountById(id);
        string? name = Blank(Get(q, "name"));
        return name == null ? null : _store.GetAccount(name);
    }

    /// <summary>A round-trip timestamp, or empty for the default value.</summary>
    private static string Iso(DateTime when)
        => when == default ? string.Empty : when.ToString("o", CultureInfo.InvariantCulture);

    /// <summary>
    /// POST /api/set-position {"id":N,"zone":Z,"x":..,"y":..,"z":..} - the retail right-menu
    /// teleport, which moves a character to a place. <c>/api/teleport</c> is the other retail
    /// verb (move a character to another PLAYER) and needs a live session; this one writes the
    /// spawn point, so it works on an offline character, which is when an operator needs it.
    /// </summary>
    private AdminResponse SetPosition(string? body, string? ip)
    {
        var c = Target(body);
        string reason = JsonString(body, "reason") ?? string.Empty;
        if (c == null) { Log(ip, "set-position", "?", reason, ResultNotFound); return Json(404, ResultNotFound, "no such character"); }
        double? zone = JsonNumber(body, "zone");
        if (zone == null || zone < 0) { Log(ip, "set-position", c.Name, reason, ResultInvalid); return Json(400, ResultInvalid, "zone is required"); }
        float x = (float)(JsonNumber(body, "x") ?? c.X);
        float y = (float)(JsonNumber(body, "y") ?? c.Y);
        float z = (float)(JsonNumber(body, "z") ?? c.Z);

        _store.UpdateLevelAndPosition(c.Id, c.Level, (int)zone.Value, x, y, z);
        Log(ip, "set-position", c.Name, reason, ResultOk, c.Id, c.AccountId,
            "zone " + (int)zone.Value);
        return Json(200, ResultOk, $"{c.Name} moved to zone {(int)zone.Value}");
    }

    /// <summary>POST /api/remove-item {"itemDbId":N,"reason":"..."} - retail WA_DEL_ITEM.</summary>
    private AdminResponse RemoveItem(string? body, string? ip)
    {
        int itemDbId = (int)(JsonNumber(body, "itemDbId") ?? 0);
        string reason = JsonString(body, "reason") ?? string.Empty;
        if (itemDbId <= 0) return Json(400, ResultInvalid, "itemDbId is required");
        var row = _store.GetItem(itemDbId);
        if (row == null) { Log(ip, "remove-item", itemDbId.ToString(CultureInfo.InvariantCulture), reason, ResultNotFound); return Json(404, ResultNotFound, "no such item"); }

        bool ok = _store.DeleteItem(itemDbId);
        string owner = _store.GetCharacterName((int)row.OwnerDbId) ?? row.OwnerDbId.ToString(CultureInfo.InvariantCulture);
        Log(ip, "remove-item", owner, reason, ok ? ResultOk : ResultRefused, row.OwnerDbId, 0,
            "item " + itemDbId + " template " + row.TemplateId + " amount " + row.Amount);
        return ok ? Json(200, ResultOk, $"item {itemDbId} removed from {owner}")
                  : Json(500, ResultRefused, "the store refused the delete");
    }

    /// <summary>
    /// GET /api/item-search?q=name-or-id&amp;limit=N - the item picker behind give-item and mail
    /// attachments. Retail made the operator know the template id; the StrSheet is loaded
    /// anyway for inventory names, so searching it costs nothing.
    /// </summary>
    private AdminResponse ItemSearch(IReadOnlyDictionary<string, string> q)
    {
        int limit = (int)Num(q, "limit");
        if (limit <= 0 || limit > ItemSearchMaxResults) limit = 25;
        var hits = Protocol.ItemNames.Search(Get(q, "q"), limit);
        var sb = new StringBuilder("{\"result\":").Append(ResultOk)
            .Append(",\"loaded\":").Append(Protocol.ItemNames.Count)
            .Append(",\"items\":[");
        for (int i = 0; i < hits.Count; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append("{\"templateId\":").Append(hits[i].TemplateId)
              .Append(",\"name\":").Append(Str(hits[i].Name)).Append('}');
        }
        sb.Append("]}");
        return new AdminResponse(200, JsonType, sb.ToString());
    }

    /// <summary>The most item hits one search will return.</summary>
    public const int ItemSearchMaxResults = 200;

    /// <summary>
    /// POST /api/reset-skills {"id":N} - retail ResetSkill. Zeroes both skill regions of the
    /// world blob; World re-learns the level's skills on the next level commit, which is what
    /// <c>/api/set-level</c> already queues.
    /// </summary>
    private AdminResponse ResetSkills(string? body, string? ip)
    {
        var c = Target(body);
        string reason = JsonString(body, "reason") ?? string.Empty;
        if (c == null) { Log(ip, "reset-skills", "?", reason, ResultNotFound); return Json(404, ResultNotFound, "no such character"); }
        bool ok = _store.ClearAllSkills(c.Id);
        Log(ip, "reset-skills", c.Name, reason, ok ? ResultOk : ResultRefused, c.Id, c.AccountId);
        return ok ? Json(200, ResultOk, $"{c.Name} skills cleared - relog to re-learn")
                  : Json(500, ResultRefused, "the store refused the reset");
    }

    /// <summary>
    /// POST /api/reset-client-settings {"id":N} - T191c. Drops the character's stored client
    /// settings blob. Use it when the blob has run away past 9000 bytes: every C_SAVE_CLIENT_USER_SETTING
    /// is refused from then on (retail refuses the same way) and nothing the client stores persists,
    /// which is what brings the WASD prompt back on every relog. The client rebuilds a small blob on
    /// the next login; hotkeys and UI layout for that character go back to their defaults.
    /// </summary>
    private AdminResponse ResetClientSettings(string? body, string? ip)
    {
        var c = Target(body);
        string reason = JsonString(body, "reason") ?? string.Empty;
        if (c == null) { Log(ip, "reset-client-settings", "?", reason, ResultNotFound); return Json(404, ResultNotFound, "no such character"); }
        bool ok = _store.ClearClientSetting(c.Id);
        Log(ip, "reset-client-settings", c.Name, reason, ok ? ResultOk : ResultRefused, c.Id, c.AccountId);
        return ok ? Json(200, ResultOk, $"{c.Name} client settings cleared - relog to rebuild them")
                  : Json(404, ResultNotFound, "that character had no stored client settings");
    }

    /// <summary>POST /api/set-ep {"id":N,"level":L,"point":P} - the EP tab's two numbers.</summary>
    private AdminResponse SetEp(string? body, string? ip)
    {
        var c = Target(body);
        string reason = JsonString(body, "reason") ?? string.Empty;
        if (c == null) { Log(ip, "set-ep", "?", reason, ResultNotFound); return Json(404, ResultNotFound, "no such character"); }
        double? level = JsonNumber(body, "level");
        double? point = JsonNumber(body, "point");
        if (level == null || level < 0 || point == null || point < 0)
        { Log(ip, "set-ep", c.Name, reason, ResultInvalid); return Json(400, ResultInvalid, "level and point must be >= 0"); }

        bool ok = _store.SetCharacterEpLevel(c.Id, (int)level.Value, (int)point.Value);
        Log(ip, "set-ep", c.Name, reason, ok ? ResultOk : ResultRefused, c.Id, c.AccountId,
            "ep level " + (int)level.Value + " point " + (int)point.Value);
        return ok ? Json(200, ResultOk, $"{c.Name} EP level {(int)level.Value}, {(int)point.Value} points")
                  : Json(500, ResultRefused, "the store refused the update");
    }

    /// <summary>The accomplished-achievement record World stores: id, serverUnique, six date
    /// words, pad. T203 read the same 24 bytes to gate server firsts.</summary>
    private const int AchievementRecordSize = 24;

    /// <summary>
    /// GET /api/achievements?id=N - the Achievement tab. The stored records are World's own 24
    /// bytes, so this decodes the two fields whose meaning T203 established (the id and the
    /// server-unique flag) and the completion date, and leaves the rest alone.
    /// </summary>
    private AdminResponse Achievements(IReadOnlyDictionary<string, string> q)
    {
        var c = CharacterFromQuery(q);
        if (c == null) return Json(404, ResultNotFound, "no such character");
        var records = _store.GetAccomplishedAchievements(c.Id);
        var sb = new StringBuilder("{\"result\":").Append(ResultOk)
            .Append(",\"characterId\":").Append(c.Id)
            .Append(",\"character\":").Append(Str(c.Name))
            .Append(",\"count\":").Append(records.Count)
            .Append(",\"done\":[");
        bool first = true;
        foreach (byte[] record in records)
        {
            if (record.Length < AchievementRecordSize) continue;
            if (!first) sb.Append(',');
            first = false;
            int id = BitConverter.ToInt32(record, 0);
            int unique = BitConverter.ToInt32(record, 4);
            sb.Append("{\"id\":").Append(id)
              .Append(",\"serverUnique\":").Append(unique)
              .Append(",\"date\":").Append(Str(AchievementDate(record))).Append('}');
        }
        sb.Append("],\"serverFirsts\":[");
        first = true;
        foreach (var claim in _store.GetServerAchievements())
        {
            if (claim.OwnerId != c.Id) continue;
            if (!first) sb.Append(',');
            first = false;
            sb.Append("{\"id\":").Append(claim.AchievementId)
              .Append(",\"partyId\":").Append(claim.PartyId)
              .Append(",\"claimedAt\":").Append(Str(claim.ClaimedAt)).Append('}');
        }
        sb.Append("]}");
        return new AdminResponse(200, JsonType, sb.ToString());
    }

    /// <summary>The six little-endian date words at +8, as yyyy-MM-dd HH:mm:ss.</summary>
    private static string AchievementDate(byte[] record)
    {
        int year = BitConverter.ToUInt16(record, 8), month = BitConverter.ToUInt16(record, 10);
        int day = BitConverter.ToUInt16(record, 12), hour = BitConverter.ToUInt16(record, 14);
        int minute = BitConverter.ToUInt16(record, 16), second = BitConverter.ToUInt16(record, 18);
        if (year < 1 || month is < 1 or > 12 || day is < 1 or > 31) return string.Empty;
        return string.Format(CultureInfo.InvariantCulture, "{0:0000}-{1:00}-{2:00} {3:00}:{4:00}:{5:00}",
            year, month, day, hour, minute, second);
    }

    /// <summary>A character from ?id= or ?name=, the read-side twin of <see cref="Target"/>.</summary>
    private CharacterRecord? CharacterFromQuery(IReadOnlyDictionary<string, string> q)
    {
        long id = Num(q, "id");
        if (id > 0) return _store.GetCharacter((int)id);
        string? name = Blank(Get(q, "name"));
        return name == null ? null : _store.GetCharacterByName(name);
    }

    /// <summary>
    /// GET /api/parcels?id=N|name=X - the ParcelInfo tab: the inbox and the sent box, each
    /// parcel with its attachments spelled out. Retail's grid showed one item name per parcel;
    /// <c>GetParcelItems</c> (T206) makes all five slots visible.
    /// </summary>
    private AdminResponse Parcels(IReadOnlyDictionary<string, string> q)
    {
        var c = CharacterFromQuery(q);
        if (c == null) return Json(404, ResultNotFound, "no such character");
        var (unread, readUnclaimed) = _store.GetParcelCounts(c.Id);
        var sb = new StringBuilder("{\"result\":").Append(ResultOk)
            .Append(",\"characterId\":").Append(c.Id)
            .Append(",\"character\":").Append(Str(c.Name))
            .Append(",\"unread\":").Append(unread)
            .Append(",\"readUnclaimed\":").Append(readUnclaimed)
            .Append(",\"inbox\":[");
        AppendParcels(sb, _store.GetParcelsFor(c.Id));
        sb.Append("],\"sent\":[");
        AppendParcels(sb, _store.GetParcelsSentBy(c.Id));
        sb.Append("]}");
        return new AdminResponse(200, JsonType, sb.ToString());
    }

    private void AppendParcels(StringBuilder sb, IReadOnlyList<CharacterStore.ParcelRow> rows)
    {
        for (int i = 0; i < rows.Count; i++)
        {
            var p = rows[i];
            if (i > 0) sb.Append(',');
            sb.Append("{\"parcelId\":").Append(p.ParcelId)
              .Append(",\"type\":").Append(p.ParcelType)
              .Append(",\"status\":").Append(p.Status)
              .Append(",\"isRead\":").Append(p.IsRead ? "true" : "false")
              .Append(",\"isRecved\":").Append(p.IsRecved ? "true" : "false")
              .Append(",\"senderDbId\":").Append(p.SenderDbId)
              .Append(",\"sender\":").Append(Str(p.SenderName))
              .Append(",\"receiverDbId\":").Append(p.ReceiverDbId)
              .Append(",\"receiver\":").Append(Str(_store.GetCharacterName(p.ReceiverDbId)))
              .Append(",\"title\":").Append(Str(p.Title))
              .Append(",\"message\":").Append(Str(p.Message))
              .Append(",\"money\":").Append(p.Money)
              .Append(",\"createdAt\":").Append(Str(Iso(_store.GetParcelCreatedUtc(p.ParcelId))))
              .Append(",\"items\":[");
            var items = _store.GetParcelItems(p.ParcelId);
            for (int j = 0; j < items.Count; j++)
            {
                if (j > 0) sb.Append(',');
                sb.Append("{\"slot\":").Append(items[j].Slot)
                  .Append(",\"itemDbId\":").Append(items[j].ItemDbId)
                  .Append(",\"templateId\":").Append(items[j].TemplateId)
                  .Append(",\"amount\":").Append(items[j].Amount)
                  .Append(",\"name\":").Append(Str(Protocol.ItemNames.Lookup(items[j].TemplateId)))
                  .Append('}');
            }
            sb.Append("]}");
        }
    }

    /// <summary>The system sender name and parcel type, as T202 read them off the capture.</summary>
    public const string SystemSender = "GM";
    /// <summary>Parcel type 102 is the system parcel: no sent-box entry, no reply.</summary>
    public const int SystemParcelType = 102;

    /// <summary>
    /// POST /api/send-mail - retail WA_SEND_NORMAL_PARCEL plus its TeraTime broadcast.
    ///
    /// <code>{"id":N | "name":"X" | "all":"online" | "all":"everyone",
    ///  "sender":"GM","title":"...","message":"...","money":0,
    ///  "items":"88888:1,99999:2","reason":"..."}</code>
    ///
    /// <c>items</c> is a flat string on purpose: the body scanner this file has always used
    /// reads one key at a time and does not understand arrays, and a real parser is a bigger
    /// change than this screen justifies. Up to
    /// <see cref="CharacterStore.MaxParcelAttachments"/> pairs; retail's form stopped at four.
    /// </summary>
    private AdminResponse SendMail(string? body, string? ip)
    {
        string reason = JsonString(body, "reason") ?? string.Empty;
        string title = (JsonString(body, "title") ?? string.Empty).Trim();
        string message = JsonString(body, "message") ?? string.Empty;
        string sender = Blank(JsonString(body, "sender")) ?? SystemSender;
        long money = (long)(JsonNumber(body, "money") ?? 0);
        if (title.Length == 0) return Json(400, ResultInvalid, "title is required");
        if (money < 0) return Json(400, ResultInvalid, "money must be >= 0");

        if (!TryParseAttachments(JsonString(body, "items"), out var attachments, out string problem))
            return Json(400, ResultInvalid, problem);

        var receivers = new List<CharacterRecord>();
        string scope = (JsonString(body, "all") ?? string.Empty).Trim();
        bool allFlag = (JsonNumber(body, "all") ?? 0) != 0
            || scope.Equals("true", StringComparison.OrdinalIgnoreCase);
        if (allFlag && scope.Length == 0) scope = "online";

        if (scope.Length > 0 && !scope.Equals("false", StringComparison.OrdinalIgnoreCase))
        {
            if (scope.Equals("online", StringComparison.OrdinalIgnoreCase)
                || scope.Equals("true", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var row in _online())
                {
                    var c = _store.GetCharacter(row.PlayerId);
                    if (c != null) receivers.Add(c);
                }
            }
            else if (scope.Equals("everyone", StringComparison.OrdinalIgnoreCase))
            {
                receivers.AddRange(_store.GetAllCharacters(BulkMailMaxReceivers));
            }
            else return Json(400, ResultInvalid, "all must be online or everyone");
        }
        else
        {
            var one = Target(body);
            if (one == null) { Log(ip, "send-mail", "?", reason, ResultNotFound); return Json(404, ResultNotFound, "no such character"); }
            receivers.Add(one);
        }

        if (receivers.Count == 0)
        { Log(ip, "send-mail", scope, reason, ResultNotFound); return Json(404, ResultNotFound, "nobody to send to"); }

        int sent = 0;
        foreach (var c in receivers)
        {
            int parcelId = _store.CreateParcel(0, sender, c.Id, title, message, money, SystemParcelType);
            if (parcelId <= 0) continue;
            for (int slot = 0; slot < attachments.Count; slot++)
            {
                // A fresh item id per receiver: two players cannot share one item row.
                _store.AddParcelItem(parcelId, slot, _store.NextItemId(),
                    attachments[slot].Template, attachments[slot].Amount);
            }
            sent++;
        }

        string target = receivers.Count == 1 ? receivers[0].Name : scope + " (" + sent + ")";
        Log(ip, "send-mail", target, reason, sent > 0 ? ResultOk : ResultRefused,
            receivers.Count == 1 ? receivers[0].Id : 0, 0,
            "title " + title + ", " + attachments.Count + " attachment(s), money " + money);
        return new AdminResponse(200, JsonType,
            "{\"result\":" + (sent > 0 ? ResultOk : ResultRefused) + ",\"sent\":" + sent
            + ",\"receivers\":" + receivers.Count + "}");
    }

    /// <summary>The ceiling on an "everyone" mail run, so one click cannot write forever.</summary>
    public const int BulkMailMaxReceivers = 5000;

    /// <summary>Parse <c>"template:amount,template:amount"</c>. An empty string is no attachments,
    /// which is valid; anything malformed is refused with the reason, never silently dropped.</summary>
    internal static bool TryParseAttachments(string? text,
        out List<(int Template, long Amount)> items, out string problem)
    {
        items = new List<(int Template, long Amount)>();
        problem = string.Empty;
        if (string.IsNullOrWhiteSpace(text)) return true;
        foreach (string piece in text.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
        {
            string[] parts = piece.Split(new[] { ':' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length is < 1 or > 2)
            { problem = "attachment '" + piece.Trim() + "' is not template:amount"; return false; }
            if (!int.TryParse(parts[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int template)
                || template <= 0)
            { problem = "attachment '" + piece.Trim() + "' has no template id"; return false; }
            long amount = 1;
            if (parts.Length == 2 && (!long.TryParse(parts[1].Trim(), NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out amount) || amount <= 0))
            { problem = "attachment '" + piece.Trim() + "' has a bad amount"; return false; }
            items.Add((template, amount));
            if (items.Count > CharacterStore.MaxParcelAttachments)
            { problem = "at most " + CharacterStore.MaxParcelAttachments + " attachments"; return false; }
        }
        return true;
    }

    /// <summary>POST /api/delete-parcel {"parcelId":N} - drop a parcel and its attachments.</summary>
    private AdminResponse DeleteParcelNow(string? body, string? ip)
    {
        int parcelId = (int)(JsonNumber(body, "parcelId") ?? 0);
        string reason = JsonString(body, "reason") ?? string.Empty;
        if (parcelId <= 0) return Json(400, ResultInvalid, "parcelId is required");
        var parcel = _store.GetParcel(parcelId);
        if (parcel == null) { Log(ip, "delete-parcel", parcelId.ToString(CultureInfo.InvariantCulture), reason, ResultNotFound); return Json(404, ResultNotFound, "no such parcel"); }

        bool ok = _store.DeleteParcel(parcelId);
        Log(ip, "delete-parcel", _store.GetCharacterName(parcel.ReceiverDbId) ?? parcelId.ToString(CultureInfo.InvariantCulture),
            reason, ok ? ResultOk : ResultRefused, parcel.ReceiverDbId, 0, "parcel " + parcelId);
        return ok ? Json(200, ResultOk, $"parcel {parcelId} deleted")
                  : Json(500, ResultRefused, "the store refused the delete");
    }

    /// <summary>The most guilds one list will return.</summary>
    public const int GuildListLimit = 200;

    /// <summary>
    /// GET /api/guilds?q=&amp;limit=N - the guild list. Retail looked a guild up by exact id or
    /// name; the substring match here is what an operator given half a name actually needs.
    /// </summary>
    private AdminResponse Guilds(IReadOnlyDictionary<string, string> q)
    {
        string? term = Blank(Get(q, "q"));
        int limit = (int)Num(q, "limit");
        if (limit <= 0 || limit > GuildListLimit) limit = 50;

        var all = _store.GetAllGuilds();
        var sb = new StringBuilder("{\"result\":").Append(ResultOk)
            .Append(",\"total\":").Append(all.Count)
            .Append(",\"guilds\":[");
        int shown = 0;
        foreach (var g in all)
        {
            if (shown >= limit) break;
            if (term != null && g.Name.IndexOf(term, StringComparison.OrdinalIgnoreCase) < 0
                && g.GuildId.ToString(CultureInfo.InvariantCulture) != term) continue;
            if (shown > 0) sb.Append(',');
            shown++;
            sb.Append("{\"id\":").Append(g.GuildId)
              .Append(",\"name\":").Append(Str(g.Name))
              .Append(",\"level\":").Append(g.Level)
              .Append(",\"money\":").Append(g.Money)
              .Append(",\"chiefDbId\":").Append(g.ChiefDbId)
              .Append(",\"chief\":").Append(Str(_store.GetCharacterName(g.ChiefDbId)))
              .Append(",\"members\":").Append(_store.CountGuildMembers(g.GuildId))
              .Append(",\"warAcceptable\":").Append(g.WarAcceptable ? "true" : "false")
              .Append('}');
        }
        sb.Append("],\"shown\":").Append(shown).Append('}');
        return new AdminResponse(200, JsonType, sb.ToString());
    }

    /// <summary>GET /api/guild?id=N|name=X - one guild, its roster and its wars.</summary>
    private AdminResponse Guild(IReadOnlyDictionary<string, string> q)
    {
        long id = Num(q, "id");
        var g = id > 0 ? _store.GetGuild((int)id) : null;
        if (g == null)
        {
            string? name = Blank(Get(q, "name"));
            if (name != null) g = _store.GetGuildByName(name);
        }
        if (g == null) return Json(404, ResultNotFound, "no such guild");

        var sb = new StringBuilder("{\"result\":").Append(ResultOk)
            .Append(",\"guild\":{\"id\":").Append(g.GuildId)
            .Append(",\"name\":").Append(Str(g.Name))
            .Append(",\"level\":").Append(g.Level)
            .Append(",\"exp\":").Append(g.Exp)
            .Append(",\"point\":").Append(g.Point)
            .Append(",\"money\":").Append(g.Money)
            .Append(",\"chiefDbId\":").Append(g.ChiefDbId)
            .Append(",\"chief\":").Append(Str(_store.GetCharacterName(g.ChiefDbId)))
            .Append(",\"announce\":").Append(Str(g.Announce))
            .Append(",\"title\":").Append(Str(g.Title))
            .Append(",\"promotion\":").Append(Str(g.Promotion))
            .Append(",\"warAcceptable\":").Append(g.WarAcceptable ? "true" : "false")
            .Append(",\"createDate\":").Append(g.CreateDate)
            .Append(",\"joinMinLevel\":").Append(g.JoinMinLevel)
            .Append(",\"joinMaxLevel\":").Append(g.JoinMaxLevel)
            .Append('}')
            .Append(",\"members\":[");
        var members = _store.GetGuildMembers(g.GuildId);
        for (int i = 0; i < members.Count; i++)
        {
            var m = members[i];
            if (i > 0) sb.Append(',');
            sb.Append("{\"userDbId\":").Append(m.UserDbId)
              .Append(",\"name\":").Append(Str(m.Name))
              .Append(",\"level\":").Append(m.UserLevel)
              .Append(",\"class\":").Append(m.UserClass)
              .Append(",\"race\":").Append(m.Race)
              .Append(",\"groupId\":").Append(m.GuildGroupId)
              .Append(",\"joinDate\":").Append(m.GuildJoinDate)
              .Append(",\"lastLogout\":").Append(m.LastLogoutTime)
              .Append(",\"weekly\":").Append(m.WeeklyContribution)
              .Append(",\"total\":").Append(m.TotalContribution)
              .Append(",\"isChief\":").Append(m.UserDbId == g.ChiefDbId ? "true" : "false")
              .Append('}');
        }
        sb.Append("],\"wars\":[");
        var wars = _store.GetGuildWars(g.GuildId);
        for (int i = 0; i < wars.Count; i++)
        {
            var w = wars[i];
            if (i > 0) sb.Append(',');
            int other = w.AttackGuildId == g.GuildId ? w.DefendGuildId : w.AttackGuildId;
            sb.Append("{\"warId\":").Append(w.WarId)
              .Append(",\"attackGuildId\":").Append(w.AttackGuildId)
              .Append(",\"defendGuildId\":").Append(w.DefendGuildId)
              .Append(",\"opponent\":").Append(Str(_store.GetGuild(other)?.Name))
              .Append(",\"attacking\":").Append(w.AttackGuildId == g.GuildId ? "true" : "false")
              .Append(",\"declaredAt\":").Append(w.DeclaredAt)
              .Append(",\"money\":").Append(w.Money)
              .Append(",\"defendMoney\":").Append(w.DefendMoney)
              .Append(",\"state\":").Append(w.State)
              .Append(",\"defendDeclared\":").Append(w.DefendDeclared ? "true" : "false")
              .Append('}');
        }
        sb.Append("],\"history\":[");
        var history = _store.GetGuildWarHistory(g.GuildId);
        for (int i = 0; i < history.Count; i++)
        {
            if (i > 0) sb.Append(',');
            var h = history[i];
            sb.Append("{\"attackGuildId\":").Append(h.AttackGuildId)
              .Append(",\"defendGuildId\":").Append(h.DefendGuildId)
              .Append(",\"result\":").Append(h.Result)
              .Append(",\"endedAt\":").Append(h.EndedAt).Append('}');
        }
        sb.Append("]}");
        return new AdminResponse(200, JsonType, sb.ToString());
    }

    /// <summary>POST /api/guild-money {"id":N,"money":M} - retail CHANGE_GUILD_MONEY, which
    /// also demanded a reason; this one logs whatever reason it is given.</summary>
    private AdminResponse GuildMoney(string? body, string? ip)
    {
        var g = GuildTarget(body, out string reason);
        if (g == null) { Log(ip, "guild-money", "?", reason, ResultNotFound); return Json(404, ResultNotFound, "no such guild"); }
        double? money = JsonNumber(body, "money");
        if (money == null || money < 0) { Log(ip, "guild-money", g.Name, reason, ResultInvalid); return Json(400, ResultInvalid, "money must be >= 0"); }

        bool ok = _store.SetGuildMoney(g.GuildId, (long)money.Value);
        Log(ip, "guild-money", g.Name, reason, ok ? ResultOk : ResultRefused, 0, 0,
            "was " + g.Money + ", now " + (long)money.Value);
        return ok ? new AdminResponse(200, JsonType,
                        "{\"result\":" + ResultOk + ",\"oldMoney\":" + g.Money
                        + ",\"newMoney\":" + (long)money.Value + "}")
                  : Json(500, ResultRefused, "the store refused the update");
    }

    /// <summary>POST /api/guild-level {"id":N,"level":L} - retail CHANGE_GUILD_LEVEL.</summary>
    private AdminResponse GuildLevel(string? body, string? ip)
    {
        var g = GuildTarget(body, out string reason);
        if (g == null) { Log(ip, "guild-level", "?", reason, ResultNotFound); return Json(404, ResultNotFound, "no such guild"); }
        double? level = JsonNumber(body, "level");
        if (level == null || level < 0 || level > MaxGuildLevel)
        { Log(ip, "guild-level", g.Name, reason, ResultInvalid); return Json(400, ResultInvalid, $"level must be 0..{MaxGuildLevel}"); }

        bool ok = _store.SetGuildLevel(g.GuildId, (int)level.Value);
        Log(ip, "guild-level", g.Name, reason, ok ? ResultOk : ResultRefused, 0, 0,
            "was " + g.Level + ", now " + (int)level.Value);
        return ok ? Json(200, ResultOk, $"{g.Name} is guild level {(int)level.Value}")
                  : Json(500, ResultRefused, "the store refused the update");
    }

    /// <summary>The guild-level ceiling the tool will set.</summary>
    public const int MaxGuildLevel = 20;

    /// <summary>POST /api/guild-disband {"id":N} - retail DELETE_GUILD. No undo.</summary>
    private AdminResponse GuildDisband(string? body, string? ip)
    {
        var g = GuildTarget(body, out string reason);
        if (g == null) { Log(ip, "guild-disband", "?", reason, ResultNotFound); return Json(404, ResultNotFound, "no such guild"); }
        int members = _store.CountGuildMembers(g.GuildId);
        bool ok = _store.DeleteGuild(g.GuildId);
        Log(ip, "guild-disband", g.Name, reason, ok ? ResultOk : ResultRefused, 0, 0,
            members + " member(s), " + g.Money + " money");
        return ok ? Json(200, ResultOk, $"{g.Name} disbanded ({members} members)")
                  : Json(500, ResultRefused, "the store refused the delete");
    }

    /// <summary>A guild from {"id":N} or {"name":"X"}, plus the reason out of the same body.</summary>
    private CharacterStore.GuildRow? GuildTarget(string? body, out string reason)
    {
        reason = JsonString(body, "reason") ?? string.Empty;
        int id = (int)(JsonNumber(body, "id") ?? 0);
        if (id > 0) return _store.GetGuild(id);
        string? name = Blank(JsonString(body, "name"));
        return name == null ? null : _store.GetGuildByName(name);
    }

    /// <summary>How long before a restart the notices go out, longest first.</summary>
    public static readonly int[] RestartNoticeMinutes = { 30, 15, 10, 5, 3, 1 };

    /// <summary>
    /// POST /api/restart-notice {"minutes":M,"text":"..."} - the countdown retail did from a
    /// console. One announcement now and one at each step of
    /// <see cref="RestartNoticeMinutes"/> that still falls inside the window, scheduled through
    /// the same announce table <c>/api/announce-schedule</c> writes. This SCHEDULES NOTICES; it
    /// does not stop the server, because an admin page should not be able to.
    /// </summary>
    private AdminResponse RestartNotice(string? body, string? ip)
    {
        string reason = JsonString(body, "reason") ?? string.Empty;
        int minutes = (int)(JsonNumber(body, "minutes") ?? 0);
        if (minutes <= 0 || minutes > MaxRestartMinutes)
            return Json(400, ResultInvalid, $"minutes must be 1..{MaxRestartMinutes}");
        string text = Blank(JsonString(body, "text")) ?? "The server restarts in {0} minute(s).";
        if (!text.Contains("{0}", StringComparison.Ordinal)) text += " ({0} minute(s))";

        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        int sent = Announce?.Invoke(string.Format(CultureInfo.InvariantCulture, text, minutes)) ?? 0;
        var scheduled = new List<(long Id, long At, int Minutes)>();
        foreach (int step in RestartNoticeMinutes)
        {
            if (step >= minutes) continue;
            long at = now + (minutes - step) * 60L;
            long id = _store.AddAnnounce(string.Format(CultureInfo.InvariantCulture, text, step),
                at, at + RestartNoticeWindowSeconds, 0, ip ?? string.Empty, now);
            scheduled.Add((id, at, step));
        }

        Log(ip, "restart-notice", minutes + " minutes", reason, ResultOk, 0, 0,
            scheduled.Count + " scheduled, " + sent + " reached now");
        var sb = new StringBuilder("{\"result\":").Append(ResultOk)
            .Append(",\"sent\":").Append(sent)
            .Append(",\"scheduled\":[");
        for (int i = 0; i < scheduled.Count; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append("{\"id\":").Append(scheduled[i].Id)
              .Append(",\"at\":").Append(scheduled[i].At)
              .Append(",\"minutes\":").Append(scheduled[i].Minutes).Append('}');
        }
        sb.Append("]}");
        return new AdminResponse(200, JsonType, sb.ToString());
    }

    /// <summary>The longest countdown the tool will schedule.</summary>
    public const int MaxRestartMinutes = 720;
    /// <summary>How long a scheduled notice stays due, so a paused server still sends it once.</summary>
    public const int RestartNoticeWindowSeconds = 120;

    /// <summary>
    /// POST /api/reload-datasheets - re-run <see cref="World.DatasheetLoader.LoadAll"/> and
    /// report what each sheet did. Every reader is a static that reloads in place, so an edited
    /// sheet takes effect without a restart; a sheet that will not parse keeps its built-in and
    /// says <c>fromSheet:false</c> rather than leaving the server with nothing.
    /// </summary>
    private AdminResponse ReloadDatasheets(string? body, string? ip)
    {
        string reason = JsonString(body, "reason") ?? string.Empty;
        IReadOnlyList<World.SheetStatus> sheets;
        try { sheets = World.DatasheetLoader.LoadAll(_log); }
        catch (Exception e)
        {
            Log(ip, "reload-datasheets", "?", reason, ResultRefused);
            return Json(500, ResultRefused, "reload failed: " + e.Message);
        }

        int fromSheet = 0;
        foreach (var s in sheets) if (s.FromSheet) fromSheet++;
        Log(ip, "reload-datasheets", fromSheet + "/" + sheets.Count, reason, ResultOk, 0, 0,
            "directory " + SafeDatasheetDir());

        var sb = new StringBuilder("{\"result\":").Append(ResultOk)
            .Append(",\"directory\":").Append(Str(SafeDatasheetDir()))
            .Append(",\"fromSheet\":").Append(fromSheet)
            .Append(",\"sheets\":[");
        for (int i = 0; i < sheets.Count; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append("{\"sheet\":").Append(Str(sheets[i].Sheet))
              .Append(",\"fromSheet\":").Append(sheets[i].FromSheet ? "true" : "false")
              .Append(",\"entries\":").Append(sheets[i].Entries)
              .Append(",\"consumer\":").Append(Str(sheets[i].Consumer)).Append('}');
        }
        sb.Append("]}");
        return new AdminResponse(200, JsonType, sb.ToString());
    }

    /// <summary>The <c>game_log</c> category every admin write lands under, so T115's log
    /// search can answer "what did an operator do to this account" with the same filters it
    /// uses for everything else.</summary>
    public const string AdminLogCategory = "admin";

    /// <summary>
    /// Audit one write. Two rows, on purpose: <c>admin_log</c> stays the tool's own chronological
    /// feed (what the Server screen tails), and <c>game_log</c> gets the same event keyed on the
    /// account and character it touched, so it shows up when an operator searches that player's
    /// history rather than only in a separate admin list. T206 added the second row; before it,
    /// nothing an operator did was visible from the player's side.
    /// </summary>
    private void Log(string? ip, string action, string target, string reason, int result,
        long characterId = 0, long accountId = 0, string extra = "")
    {
        long at = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        _store.AddAdminLog(at, ip ?? string.Empty, action, target, reason, result);

        var detail = new StringBuilder(target);
        if (reason.Length > 0) detail.Append(" - ").Append(reason);
        if (extra.Length > 0) detail.Append(" [").Append(extra).Append(']');
        detail.Append(" from ").Append(ip ?? "?").Append(" -> ").Append(result);
        _store.AddGameLog(AdminLogCategory, action, accountId, characterId, 0, 0, 0, 0, 0,
            detail.ToString(), at);

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

    /// <summary>
    /// T224. The bool after "key": in a flat JSON body, or null when the key is absent.
    ///
    /// <para>Accepts <c>true</c>/<c>false</c>, <c>"true"</c>/<c>"false"</c> and <c>1</c>/<c>0</c>,
    /// because the admin UI's own fetch calls, a hand-rolled curl and a form post disagree about
    /// which they send.</para>
    ///
    /// <para><b>T224b:</b> this cannot be built on <see cref="RawValue"/>. That scanner only walks
    /// number characters, so it stops dead on the <c>t</c> of <c>true</c> and returns null - which
    /// made <c>{"hard":true}</c> read as "flag absent" and fall through to the ServerConfig policy,
    /// i.e. <c>/api/delete-character</c> scheduled a 72 h soft delete for a caller who asked for an
    /// immediate one. It walks the literal itself instead.</para>
    /// </summary>
    public static bool? JsonBool(string? body, string key)
    {
        if (body == null) return null;
        int i = body.IndexOf("\"" + key + "\"", StringComparison.Ordinal);
        if (i < 0) return null;
        i = body.IndexOf(':', i);
        if (i < 0) return null;
        while (++i < body.Length && char.IsWhiteSpace(body[i])) { }
        if (i >= body.Length) return null;

        int start = i, end = i;
        if (body[end] == '"')                                  // "hard":"true"
        {
            start = ++end;
            while (end < body.Length && body[end] != '"') end++;
        }
        else                                                   // "hard":true / "hard":1
        {
            while (end < body.Length && (char.IsLetterOrDigit(body[end]) || body[end] == '.'
                                         || body[end] == '-' || body[end] == '+')) end++;
        }
        if (end <= start) return null;

        string raw = body[start..end];
        if (raw.Equals("true", StringComparison.OrdinalIgnoreCase)) return true;
        if (raw.Equals("false", StringComparison.OrdinalIgnoreCase)) return false;
        return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double d)
            ? d != 0 : null;
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
