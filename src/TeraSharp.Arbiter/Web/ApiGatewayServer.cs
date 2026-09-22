// SPDX-License-Identifier: MIT
// Copyright (c) 2026 the TeraSharp contributors

using System.Net;
using System.Text;
using Microsoft.Extensions.Logging;
using TeraSharp.Arbiter.Auth;

namespace TeraSharp.Arbiter.Web;

// =============================================================================================
// ApiGatewayServer - T132. The listener that sits at apiServerAddress.
//
// WHAT THIS IS, HONESTLY: an instrument, not the finished admin tool. T131 established that the
// client builds the Alt+A panel's URL itself, from the apiServerAddress + apiServerAuthToken in
// S_LOGIN_ACCOUNT_INFO. The PATH it requests is decided in the client, and T132's research could
// not read it:
//
//   * ArbiterServer.exe.c has no admin-tool path at all. Its only URL is a hardcoded retail
//     phone-home, FUN_14016dad0(.., L"http://%s/Default.aspx?v=%s", a hardcoded retail address,
//     "Live-100.02 TW #9 (Gold)"), fired through InternetOpenUrlW at startup. Unrelated.
//   * WebApp\ContentsControl\Awesomium\* is a DIFFERENT feature - the GM pages that manage the
//     per-server (Title, Url) list behind C_/S_REQUEST_SERVER_ADMINTOOL_AWESOMIUM_URL. In the
//     working capture both of those strings are EMPTY and the panel opened anyway.
//   * tera-api's gateway API (API_GATEWAY_LISTEN_PORT=8040) is, per its own .env.example, "for
//     receiving connections from the external website (like billing)" - not an admin tool. 8040
//     was never the right service, which is the other half of why the panel stays blank.
//   * The client install (D:\Tera 100, S1Game\Config, the exe) is not reachable from Cowork.
//
// So this serves EVERY path, logs the request in full, and returns a minimal page. One Alt+A
// press against it turns "we do not know the path" into a measurement: the log line names the
// method, path, query, headers and cookies, and says whether a token arrived and whether it
// verified. T133 can then serve the real thing.
//
//   TERASHARP_API_GATEWAY_SERVE   required; 1/true, or this does not start at all
//   TERASHARP_API_GATEWAY         host:port - the SAME value the client is told (default 8800)
//   TERASHARP_API_GATEWAY_BIND    optional; 0.0.0.0 / + / an IP, when the client is not local
//   TERASHARP_API_JWT_SECRET      the HS256 key the token is checked against
//
// Unlike AdminServer this can bind beyond loopback, because the client that must reach it is
// usually on another machine. That is why it is opt-in: it is off unless SERVE is set. On
// Windows a prefix of + or 0.0.0.0 needs an urlacl or an elevated process -
//   netsh http add urlacl url=http://+:8800/ user=%USERNAME%
// and the bind failure is logged rather than thrown.
// =============================================================================================
public sealed class ApiGatewayServer : IDisposable
{
    /// <summary>Opt-in switch. Absent or not 1/true means the listener never starts.</summary>
    public const string EnableVariable = "TERASHARP_API_GATEWAY_SERVE";

    /// <summary>Overrides the host half of the prefix when the client is not on this box.</summary>
    public const string BindVariable = "TERASHARP_API_GATEWAY_BIND";

    /// <summary>Query and cookie names worth trying, in the order a token is most likely to use.</summary>
    public static readonly string[] TokenKeys =
    {
        "token", "authtoken", "auth_token", "authkey", "auth", "accesstoken",
        "access_token", "apiserverauthtoken", "jwt", "t",
    };

    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly ILogger _log;

    private ApiGatewayServer(string prefix, ILogger log)
    {
        _log = log;
        _listener.Prefixes.Add(prefix);
    }

    /// <summary>
    /// The HttpListener prefix for a configured address. The port always comes from the address
    /// the client is told, because a listener on a different port is not the thing the client is
    /// pointed at. The host comes from <see cref="BindVariable"/> when set, so the common remote
    /// case is one env var; 0.0.0.0 and * are both spelled + , which is what HttpListener wants.
    /// </summary>
    public static string PrefixFor(string? configured = null, string? bind = null)
    {
        string address = ApiGatewayToken.Address(configured);
        int colon = address.LastIndexOf(':');
        string host = colon > 0 ? address[..colon] : address;
        string port = colon >= 0 ? address[(colon + 1)..] : string.Empty;
        if (port.Length == 0) port = ApiGatewayToken.DefaultAddress[(ApiGatewayToken.DefaultAddress.IndexOf(':') + 1)..];

        string b = bind ?? Environment.GetEnvironmentVariable(BindVariable) ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(b)) host = b.Trim();
        if (host is "0.0.0.0" or "*" or "+") host = "+";
        return "http://" + host + ":" + port + "/";
    }

    /// <summary>
    /// The token, from wherever the client chose to put it: Authorization: Bearer first, then the
    /// query string, then Cookie. Returns null when none of them carried one. We do not yet know
    /// which the client uses - that is the point of the probe - so all three are read.
    /// </summary>
    public static string? TokenFrom(string? rawQuery, string? authorization, string? cookieHeader)
    {
        if (!string.IsNullOrWhiteSpace(authorization))
        {
            string a = authorization.Trim();
            if (a.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                string v = a[7..].Trim();
                if (v.Length > 0) return v;
            }
        }
        return PairValue(rawQuery, '&') ?? PairValue(cookieHeader, ';');
    }

    private static string? PairValue(string? raw, char separator)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        foreach (string part in raw.TrimStart('?').Split(separator))
        {
            int eq = part.IndexOf('=');
            if (eq <= 0) continue;
            string key = part[..eq].Trim().ToLowerInvariant();
            bool wanted = false;
            foreach (string k in TokenKeys) if (key == k) { wanted = true; break; }
            if (!wanted) continue;
            string value = part[(eq + 1)..].Trim();
            if (value.Length == 0) continue;
            try { return Uri.UnescapeDataString(value); } catch (Exception) { return value; }
        }
        return null;
    }

    /// <summary>
    /// One line describing a token: whether it is there, whether it verifies with the configured
    /// key, and who it claims to be. Never logs the signature.
    /// </summary>
    public static string DescribeToken(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return "no token";
        string[] seg = token.Split('.');
        if (seg.Length != 3) return $"malformed token ({seg.Length} segments, {token.Length} chars)";
        string claims;
        try { claims = ApiGatewayToken.Decode(seg[1]); } catch (Exception) { claims = "(undecodable)"; }
        bool ok;
        try { ok = ApiGatewayToken.Verify(token, ApiGatewayToken.Secret()); } catch (Exception) { ok = false; }
        return (ok ? "token VERIFIED " : "token PRESENT but NOT verified ") + claims;
    }

    /// <summary>Starts the probe, or returns null and says why. Never throws.</summary>
    public static ApiGatewayServer? TryStart(ILogger log)
    {
        string serve = Environment.GetEnvironmentVariable(EnableVariable) ?? string.Empty;
        if (!(serve.Trim() == "1" || serve.Trim().Equals("true", StringComparison.OrdinalIgnoreCase)))
        {
            log.LogInformation(
                "api-gateway probe: {Var} is not set - nothing is serving {Address}, so the Alt+A panel will stay blank",
                EnableVariable, ApiGatewayToken.Address());
            return null;
        }

        string prefix = PrefixFor();
        var server = new ApiGatewayServer(prefix, log);
        try
        {
            server._listener.Start();
        }
        catch (Exception ex)
        {
            log.LogWarning(
                "api-gateway probe: could not bind {Prefix} - {Msg}. A + or 0.0.0.0 prefix needs an urlacl: netsh http add urlacl url={Prefix} user=%USERNAME%",
                prefix, ex.Message, prefix);
            server.Dispose();
            return null;
        }

        _ = Task.Run(() => server.LoopAsync());
        log.LogInformation("api-gateway probe listening on {Prefix} - the client is told {Address}",
            prefix, ApiGatewayToken.Address());
        ApiGatewayToken.LogMode(log);
        return server;
    }

    private async Task LoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync().ConfigureAwait(false); }
            catch (Exception) { return; }        // stopped, or the listener died
            try { Serve(ctx); }
            catch (Exception ex) { _log.LogWarning("api-gateway probe: {Msg}", ex.Message); }
        }
    }

    private void Serve(HttpListenerContext ctx)
    {
        var req = ctx.Request;
        string ip = req.RemoteEndPoint?.Address.ToString() ?? "?";
        string method = req.HttpMethod ?? "GET";
        string path = req.Url?.AbsolutePath ?? "/";
        string query = req.Url?.Query ?? string.Empty;

        var headers = new StringBuilder();
        foreach (string? name in req.Headers.AllKeys)
        {
            if (name == null) continue;
            headers.Append(name).Append(": ").Append(req.Headers[name]).Append(" | ");
        }

        string token = TokenFrom(query, req.Headers["Authorization"], req.Headers["Cookie"]) ?? string.Empty;

        // THIS is the deliverable: the line that says what the client actually asked for.
        _log.LogInformation(
            "api-gateway probe: {Ip} {Method} {Path}{Query} ua={Agent} :: {Token} :: headers {Headers}",
            ip, method, path, query, req.UserAgent ?? "?", DescribeToken(token), headers.ToString());

        var bytes = Encoding.UTF8.GetBytes(Page(path, query, DescribeToken(token)));
        ctx.Response.StatusCode = 200;
        ctx.Response.ContentType = "text/html; charset=utf-8";
        ctx.Response.ContentLength64 = bytes.Length;
        ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
        ctx.Response.OutputStream.Close();
    }

    /// <summary>
    /// The minimal page. Awesomium is an old Chromium embed, so this is deliberately plain HTML
    /// with no script, no fonts and no external anything - if the panel is a shell that only
    /// needs a document to load, this is enough for it to appear. Single-quoted attributes so the
    /// verbatim string holds no double quote (CLAUDE.md).
    /// </summary>
    public static string Page(string path, string query, string token) => @"<!doctype html>
<html><head><meta charset='utf-8'><title>TeraSharp</title></head>
<body style='background:#101014;color:#d8d8e0;font:13px/1.5 sans-serif;margin:16px'>
<h1 style='font-size:16px;margin:0 0 12px'>TeraSharp api-gateway probe</h1>
<p>This page exists so the panel has something to load. What the client asked for:</p>
<pre style='background:#1a1a22;padding:10px;white-space:pre-wrap'>" + Escape(path + query) + @"
" + Escape(token) + @"</pre>
<p>Serve the real page from this path once the log confirms it.</p>
</body></html>";

    private static string Escape(string s) => s
        .Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    public void Dispose()
    {
        try { _stop.Cancel(); } catch (Exception) { }
        try { if (_listener.IsListening) _listener.Stop(); } catch (Exception) { }
        try { _listener.Close(); } catch (Exception) { }
        _stop.Dispose();
    }
}
