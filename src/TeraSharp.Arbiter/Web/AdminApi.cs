using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;
using TeraSharp.Arbiter.Persistence;

namespace TeraSharp.Arbiter.Web;

// =============================================================================================
// AdminApi - the admin web tool's JSON surface, T101 phase 1.
// Feature list and the retail tool it replaces: status/WEBADMIN-DESIGN.md.
//
// PURE. Nothing here touches a socket: Handle() takes a method, a path, a query bag and a body,
// and returns a status and a string. AdminServer is the twenty lines of HttpListener around it,
// and every test drives this class directly.
//
// SCOPE. WEBADMIN-DESIGN.md section 6 calls phase 1 "read-only lookups", and its section 4 table
// is what TeraSharp can already answer without a new table. That is what is implemented:
// accounts, character, online, plus the restore the brief adds. The money / item / ban / announce
// / kick / gm-level endpoints are phase 2 there, and they answer 501 with a result code rather
// than 404, so the page can show "not in this phase" instead of looking broken.
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

    /// <summary>True when the tool is usable at all. False means TERASHARP_ADMIN_TOKEN is unset.</summary>
    public bool Enabled => _token != null;

    /// <summary>The phase-2 endpoints, answered with a clean refusal so the page can say so.</summary>
    public static readonly string[] PhaseTwoPaths =
    {
        "/api/set-money", "/api/set-level", "/api/give-item",
        "/api/ban", "/api/unban", "/api/kick", "/api/announce", "/api/gm-level",
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
        if (!TokenOk(token))
        {
            _log.LogWarning("admin: bad token from {Ip} for {Path}", sourceIp ?? "?", path);
            return Json(401, ResultRefused, "bad or missing token");
        }

        if (method == "GET" && (path == "/" || path == "/index.html"))
            return new AdminResponse(200, "text/html; charset=utf-8", AdminPage.Html);

        if (method == "GET" && path == "/api/accounts") return Accounts(query);
        if (method == "GET" && path == "/api/character") return Character(query);
        if (method == "GET" && path == "/api/online") return Online();
        if (method == "GET" && path == "/api/admin-log") return AdminLog(query);
        if (method == "POST" && path == "/api/restore-character") return RestoreCharacter(body, sourceIp);

        foreach (var p in PhaseTwoPaths)
            if (path == p)
                return Json(501, ResultRefused,
                    $"{path} is phase 2 - see status/WEBADMIN-DESIGN.md section 6");

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

        sb.Append(",\"items\":[");
        var items = _store.GetItems(c.Id, 0);
        for (int i = 0; i < items.Count; i++)
        {
            var it = items[i];
            if (i > 0) sb.Append(',');
            sb.Append("{\"itemDbId\":").Append(it.ItemDbId)
              .Append(",\"templateId\":").Append(it.TemplateId)
              .Append(",\"slot\":").Append(it.Slot)
              .Append(",\"count\":").Append(it.Amount).Append('}');
        }
        sb.Append("]}");
        return new AdminResponse(200, "application/json; charset=utf-8", sb.ToString());
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
    /// POST /api/restore-character {"id":N,"reason":"..."} - clears a SCHEDULED delete.
    ///
    /// <para><b>This is not the retail WA_UNDELETE_USER.</b> That one brings back a character whose
    /// row is gone, which needs the soft-delete columns and the <c>deleted_items</c> table
    /// WEBADMIN-DESIGN.md section 4 lists as new. TeraSharp's <c>characters.delete_at</c> (T88) is
    /// a SCHEDULED delete, and T88 left <c>OnDeleteUser</c> hard-deleting the row - so this can
    /// only rescue a character inside its grace window, and answers 2 (not found) for anything
    /// else. Restoring a truly deleted character is phase 2 work with the new tables.</para>
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
            return Json(404, ResultNotFound, "no such character - a hard-deleted row cannot be restored in phase 1");
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
