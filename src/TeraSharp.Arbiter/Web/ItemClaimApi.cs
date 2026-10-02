// SPDX-License-Identifier: MIT
// Copyright (c) 2026 the TeraSharp contributors

using System.Text;
using Microsoft.Extensions.Logging;
using TeraSharp.Arbiter.Auth;
using TeraSharp.Arbiter.Persistence;
using TeraSharp.Arbiter.World;

namespace TeraSharp.Arbiter.Web;

// =============================================================================================
// ItemClaimApi - T230. The Item Claim panel, served on the api-gateway.
//
// 100.02 has no box packet (T230 section 1), so retail's "Item Claim" button is the Alt+A
// Awesomium web view and the items reach the character server-side. That is the surface this
// serves: the client already opens http://<TERASHARP_API_GATEWAY>/ when Alt+A is pressed, the
// gateway answers every path, and these three are ours.
//
//   GET  /itemclaim            the page
//   GET  /itemclaim/list       {"accountDbId":N,"boxes":[...]}
//   POST /itemclaim/claim      ?box=N  ->  {"ok":true,"via":"bag"|"mail","items":N,...}
//
// THE TICKET. The page is scoped by the `accountDbId` of the HS256 ticket the client presents -
// the one S_LOGIN_ACCOUNT_INFO already mints (ApiGatewayToken). Three deliberate choices, because
// nobody has yet measured where the client puts it (section 6.1):
//
//   * the signature is VERIFIED and an unset TERASHARP_API_JWT_SECRET refuses every request. It
//     is the only thing standing between one account's purchases and another's, so it fails
//     closed, the way AuthProvider does.
//   * `exp` is advisory. ApiGatewayToken.LifetimeSeconds is 120 and the ticket is minted at
//     login, so by the time a player opens the panel it is always expired; the age that is
//     enforced instead is iat + MaxAge (TERASHARP_ITEM_CLAIM_MAX_AGE, default one hour).
//   * the token is read from Authorization, then the query string, then Cookie - whichever the
//     client used (ApiGatewayServer.TokenFrom).
//
// Serving the page does not require knowing the client's own initial path: the API paths are
// ours, and ApiGatewayServer answers the root with the page too.
// =============================================================================================
public static class ItemClaimApi
{
    /// <summary>Everything under this prefix is ours.</summary>
    public const string Prefix = "/itemclaim";

    /// <summary>How old a ticket may be, in seconds, overriding the unusable 120 s <c>exp</c>.</summary>
    public const string MaxAgeVariable = "TERASHARP_ITEM_CLAIM_MAX_AGE";

    /// <summary>One hour: long enough to open a panel and read it, short enough to matter.</summary>
    public const int DefaultMaxAgeSeconds = 3600;

    /// <summary>One answer: a status, a content type and a body.</summary>
    public readonly record struct Reply(int Status, string ContentType, string Body);

    public static Reply Json(int status, string body) => new(status, "application/json; charset=utf-8", body);

    /// <summary><see cref="MaxAgeVariable"/>, or <see cref="DefaultMaxAgeSeconds"/>.</summary>
    public static int MaxAgeSeconds()
    {
        string? raw = TerasConfig.Get(MaxAgeVariable);
        return !string.IsNullOrWhiteSpace(raw) && int.TryParse(raw.Trim(), out int v) && v > 0
            ? v : DefaultMaxAgeSeconds;
    }

    /// <summary>Does this path belong to us?</summary>
    public static bool Handles(string? path)
        => path is not null
        && (path.Equals(Prefix, StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(Prefix + "/", StringComparison.OrdinalIgnoreCase));

    // ------------------------------------------------------------------------------ the ticket

    /// <summary>
    /// The account a ticket is for, or 0 with the reason. Verifies the signature against the
    /// configured key, refuses outright when there is no configured key, and enforces
    /// <see cref="MaxAgeSeconds"/> against <c>iat</c>.
    /// </summary>
    public static long AccountFor(string? token, long nowUnix, out string problem)
    {
        problem = string.Empty;
        if (!ApiGatewayToken.HasConfiguredSecret)
        {
            problem = "item claim is unavailable: " + ApiGatewayToken.SecretVariable + " is not set";
            return 0;
        }
        if (string.IsNullOrWhiteSpace(token)) { problem = "no session ticket"; return 0; }
        var seg = token.Split('.');
        if (seg.Length != 3) { problem = "malformed session ticket"; return 0; }
        bool ok;
        try { ok = ApiGatewayToken.Verify(token, ApiGatewayToken.Secret()); }
        catch (Exception) { ok = false; }
        if (!ok) { problem = "session ticket does not verify"; return 0; }

        string claims;
        try { claims = ApiGatewayToken.Decode(seg[1]); } catch (Exception) { problem = "undecodable session ticket"; return 0; }
        long account = Number(claims, "accountDbId");
        if (account <= 0) { problem = "session ticket carries no accountDbId"; return 0; }
        long iat = Number(claims, "iat");
        int maxAge = MaxAgeSeconds();
        if (iat > 0 && nowUnix - iat > maxAge)
        {
            problem = "session ticket is older than " + maxAge.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + "s - press Alt+A again after relogging";
            return 0;
        }
        if (iat > 0 && iat - nowUnix > 300) { problem = "session ticket is from the future"; return 0; }
        return account;
    }

    /// <summary>A bare integer claim out of a flat JSON object, or 0. No parser needed for seven keys.</summary>
    public static long Number(string json, string key)
    {
        if (string.IsNullOrEmpty(json)) return 0;
        int at = json.IndexOf("\"" + key + "\":", StringComparison.Ordinal);
        if (at < 0) return 0;
        at += key.Length + 3;
        while (at < json.Length && (json[at] == ' ' || json[at] == '"')) at++;
        int end = at;
        if (end < json.Length && (json[end] == '-' || json[end] == '+')) end++;
        while (end < json.Length && char.IsAsciiDigit(json[end])) end++;
        return long.TryParse(json.AsSpan(at, end - at), System.Globalization.NumberStyles.Integer,
                             System.Globalization.CultureInfo.InvariantCulture, out long v) ? v : 0;
    }

    /// <summary>A query-string value, unescaped, or null.</summary>
    public static string? Query(string? rawQuery, string key)
    {
        if (string.IsNullOrWhiteSpace(rawQuery)) return null;
        foreach (string part in rawQuery.TrimStart('?').Split('&'))
        {
            int eq = part.IndexOf('=');
            if (eq <= 0) continue;
            if (!part[..eq].Trim().Equals(key, StringComparison.OrdinalIgnoreCase)) continue;
            string v = part[(eq + 1)..];
            try { return Uri.UnescapeDataString(v); } catch (Exception) { return v; }
        }
        return null;
    }

    // ------------------------------------------------------------------------------ the routes

    /// <summary>
    /// Answer one request. <paramref name="store"/> null means the Arbiter has no DB yet, which is
    /// a 503 rather than an empty list - an empty list would read as "your purchases are gone".
    /// </summary>
    public static Reply Serve(CharacterStore? store, string method, string path, string? rawQuery,
                              string? token, long nowUnix, ILogger? log = null)
    {
        string tail = path.Length > Prefix.Length ? path[Prefix.Length..].TrimEnd('/') : string.Empty;
        if (tail.Length == 0)
            return new(200, "text/html; charset=utf-8", Page());

        if (store is null) return Json(503, Error("the server is still starting - try again"));

        long account = AccountFor(token, nowUnix, out string problem);
        if (account <= 0)
        {
            log?.LogWarning("item-claim: refused {Method} {Path} - {Why}", method, path, problem);
            return Json(401, Error(problem));
        }

        if (tail.Equals("/list", StringComparison.OrdinalIgnoreCase))
            return Json(200, Listing(store, account));

        if (tail.Equals("/claim", StringComparison.OrdinalIgnoreCase))
        {
            if (!string.Equals(method, "POST", StringComparison.OrdinalIgnoreCase))
                return Json(405, Error("claim is a POST"));
            long box = Number("\"box\":" + (Query(rawQuery, "box") ?? "0"), "box");
            if (box <= 0) return Json(400, Error("claim needs ?box=<number>"));
            var result = BoxClaim.Claim(store, account, box, isOnline: null, log);
            if (!result.Ok) return Json(409, Error(result.Problem));
            return Json(200,
                "{\"ok\":true,\"box\":" + box.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + ",\"via\":\"" + (result.Via == BoxClaim.Path.Mail ? "mail" : result.Via == BoxClaim.Path.Bag ? "bag" : "none")
                + "\",\"items\":" + result.Items.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + ",\"characterId\":" + result.CharacterId.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + ",\"parcelId\":" + result.ParcelId.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + ",\"message\":\"" + Escape(Told(result)) + "\"}");
        }

        return Json(404, Error("no such endpoint"));
    }

    /// <summary>What the player is told after a claim - the two paths read differently.</summary>
    public static string Told(BoxClaim.Result r) => r.Via switch
    {
        BoxClaim.Path.Bag => "Added to your inventory. It will be there next time you log in.",
        BoxClaim.Path.Mail => "Sent to your in-game mailbox - open the mail window to collect it.",
        _ => "Nothing to collect in this box.",
    };

    /// <summary>The account's boxes, newest first, with their lines and claim state.</summary>
    public static string Listing(CharacterStore store, long accountDbId)
    {
        ArgumentNullException.ThrowIfNull(store);
        var sb = new StringBuilder();
        sb.Append("{\"accountDbId\":").Append(accountDbId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        sb.Append(",\"boxes\":[");
        bool first = true;
        foreach (var box in store.HubBoxesForAccount(accountDbId))
        {
            if (!first) sb.Append(',');
            first = false;
            sb.Append("{\"box\":").Append(box.BoxSn.ToString(System.Globalization.CultureInfo.InvariantCulture));
            sb.Append(",\"title\":\"").Append(Escape(box.Title.Length > 0 ? box.Title : BoxDelivery.Sender)).Append('"');
            sb.Append(",\"content\":\"").Append(Escape(box.Content)).Append('"');
            sb.Append(",\"state\":").Append(box.State.ToString(System.Globalization.CultureInfo.InvariantCulture));
            sb.Append(",\"claimable\":").Append(box.State == CharacterStore.HubBoxPending ? "true" : "false");
            sb.Append(",\"claimedAt\":").Append(box.ClaimedAt.ToString(System.Globalization.CultureInfo.InvariantCulture));
            sb.Append(",\"parcelId\":").Append(box.ParcelId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            sb.Append(",\"items\":[");
            bool firstItem = true;
            foreach (var line in store.GetHubBoxItems(box.BoxSn))
            {
                if (!firstItem) sb.Append(',');
                firstItem = false;
                sb.Append("{\"templateId\":").Append(line.TemplateId.ToString(System.Globalization.CultureInfo.InvariantCulture));
                sb.Append(",\"amount\":").Append(line.Amount.ToString(System.Globalization.CultureInfo.InvariantCulture));
                sb.Append('}');
            }
            sb.Append("]}");
        }
        sb.Append("]}");
        return sb.ToString();
    }

    public static string Error(string why) => "{\"ok\":false,\"error\":\"" + Escape(why) + "\"}";

    /// <summary>JSON string escaping, and enough of it: these strings come from tera-api.</summary>
    public static string Escape(string? s)
    {
        if (string.IsNullOrEmpty(s)) return string.Empty;
        var sb = new StringBuilder(s.Length + 8);
        foreach (char c in s)
        {
            if (c == '"' || c == '\\') sb.Append('\\').Append(c);
            else if (c == '\n') sb.Append("\\n");
            else if (c == '\r') sb.Append("\\r");
            else if (c == '\t') sb.Append("\\t");
            else if (c < ' ') sb.Append("\\u").Append(((int)c).ToString("x4", System.Globalization.CultureInfo.InvariantCulture));
            else sb.Append(c);
        }
        return sb.ToString();
    }

    // ------------------------------------------------------------------------------ the page

    /// <summary>
    /// The panel. Loosely retail: dark plate, a gold-ish header rule, one row per box with a
    /// Collect button on the right.
    ///
    /// <para><b>ES5 only, and nothing external.</b> Awesomium 1.6/1.7 is an old Chromium embed -
    /// no fetch, no let/const, no arrow functions, no template literals - and the panel has no
    /// route to a CDN, so there is no font, no stylesheet and no image here. XMLHttpRequest and
    /// string concatenation are what it has. Single-quoted HTML attributes so the verbatim string
    /// holds no double quote (CLAUDE.md).</para>
    /// </summary>
    public static string Page() => @"<!doctype html>
<html><head><meta charset='utf-8'><title>Item Claim</title>
<style>
body{background:#0d0d11;color:#cfcfd6;font:13px/1.55 'Segoe UI',sans-serif;margin:0;padding:18px}
h1{font-size:15px;font-weight:600;letter-spacing:.06em;text-transform:uppercase;color:#d8c08a;
   margin:0 0 4px;padding-bottom:9px;border-bottom:1px solid #2b2b35}
p.sub{margin:0 0 16px;color:#7b7b88;font-size:12px}
ul{list-style:none;margin:0;padding:0}
li{background:#16161d;border:1px solid #262630;border-radius:2px;padding:10px 12px;margin-bottom:8px;
   display:flex;align-items:center}
li.done{opacity:.55}
.body{flex:1;min-width:0}
.t{color:#e4e4ea;font-weight:600}
.c{color:#80808d;font-size:12px}
.i{color:#9aa9c4;font-size:12px;margin-top:2px}
button{background:#2f2a1d;color:#e6d4a4;border:1px solid #5b4e30;border-radius:2px;
       padding:6px 14px;font:600 12px/1 sans-serif;cursor:pointer;margin-left:12px}
button:hover{background:#3d3524}
button[disabled]{background:#1b1b22;color:#5d5d68;border-color:#2b2b35;cursor:default}
.note{margin-top:14px;color:#7b7b88;font-size:12px;min-height:16px}
.err{color:#c98a8a}
</style></head>
<body>
<h1>Item Claim</h1>
<p class='sub'>Purchases waiting for this account.</p>
<ul id='list'></ul>
<div class='note' id='note'>Loading...</div>
<script>
function x(){return window.XMLHttpRequest?new XMLHttpRequest():new ActiveXObject('Microsoft.XMLHTTP');}
function note(t,bad){var n=document.getElementById('note');n.innerHTML='';
  n.className=bad?'note err':'note';n.appendChild(document.createTextNode(t));}
function esc(s){return String(s==null?'':s);}
function row(b){
  var li=document.createElement('li');
  if(!b.claimable){li.className='done';}
  var d=document.createElement('div');d.className='body';
  var t=document.createElement('div');t.className='t';t.appendChild(document.createTextNode(esc(b.title)));
  d.appendChild(t);
  if(b.content){var c=document.createElement('div');c.className='c';
    c.appendChild(document.createTextNode(esc(b.content)));d.appendChild(c);}
  var names=[],j;
  for(j=0;j<b.items.length;j++){names.push(b.items[j].templateId+' x'+b.items[j].amount);}
  var i=document.createElement('div');i.className='i';
  i.appendChild(document.createTextNode(names.length?names.join(', '):'(empty)'));
  d.appendChild(i);
  li.appendChild(d);
  var btn=document.createElement('button');
  btn.appendChild(document.createTextNode(b.claimable?'Collect':(b.state==2?'Collected':'Delivered')));
  if(!b.claimable){btn.disabled=true;}
  else{btn.onclick=function(){claim(b.box,btn);};}
  li.appendChild(btn);
  return li;
}
function load(){
  var r=x();r.open('GET','/itemclaim/list'+location.search,true);
  r.onreadystatechange=function(){
    if(r.readyState!=4){return;}
    var o;try{o=JSON.parse(r.responseText);}catch(e){o=null;}
    if(!o||o.ok===false){note(o&&o.error?o.error:'Could not read your purchases.',true);return;}
    var ul=document.getElementById('list');ul.innerHTML='';
    var k;for(k=0;k<o.boxes.length;k++){ul.appendChild(row(o.boxes[k]));}
    note(o.boxes.length?'':'Nothing to collect.');
  };
  r.send(null);
}
function claim(box,btn){
  btn.disabled=true;note('Collecting...');
  var q=location.search?location.search+'&box='+box:'?box='+box;
  var r=x();r.open('POST','/itemclaim/claim'+q,true);
  r.onreadystatechange=function(){
    if(r.readyState!=4){return;}
    var o;try{o=JSON.parse(r.responseText);}catch(e){o=null;}
    if(!o||o.ok===false){note(o&&o.error?o.error:'Could not collect that.',true);btn.disabled=false;return;}
    note(o.message);load();
  };
  r.send(null);
}
load();
</script>
</body></html>";
}
