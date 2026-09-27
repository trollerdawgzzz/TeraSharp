// SPDX-License-Identifier: MIT
// Copyright (c) 2026 the TeraSharp contributors

using System.Net;
using System.Text;
using Microsoft.Extensions.Logging;
using TeraSharp.Arbiter.Persistence;

namespace TeraSharp.Arbiter.Web;

// =============================================================================================
// AdminServer - the HttpListener shell around AdminApi. T101 phase 1, T101b phase 2.
//
// Everything that decides anything lives in AdminApi; this file only moves bytes. It binds
// 127.0.0.1 ONLY - never + or *, which would make the tool reachable from the network - and it
// refuses to start at all when TERASHARP_ADMIN_TOKEN is unset.
//
//   TERASHARP_ADMIN_PORT    default 8050
//   TERASHARP_ADMIN_TOKEN   required; sent as  Authorization: Bearer <token>  or ?token=
//
// T206. The one-page form that used to live at the bottom of this file as a const string is gone.
// The UI is now the embedded files in AdminAssets (Web/wwwroot), which AdminApi serves for any GET
// outside /api/ - so this file is back to moving bytes and nothing else.
// =============================================================================================
public sealed class AdminServer : IDisposable
{
    public const int DefaultPort = 8050;
    public const string PortVariable = "TERASHARP_ADMIN_PORT";
    public const string TokenVariable = "TERASHARP_ADMIN_TOKEN";
    /// <summary>T106: the header the page's own fetch() puts the token in.</summary>
    public const string TokenHeader = "X-Admin-Token";

    private readonly HttpListener _listener = new();
    private readonly AdminApi _api;
    private readonly ILogger _log;
    private readonly CancellationTokenSource _stop = new();

    public int Port { get; }

    /// <summary>
    /// T106. The JSON surface, so the wiring in <c>Program.cs</c> can hand it the two delegates
    /// T101b left settable - <c>KickPlayer</c> and <c>Announce</c> - after the server is up. It
    /// was unreachable before: <c>TryStart</c> built the <c>AdminApi</c> inside the constructor
    /// call and nothing ever got a reference back.
    /// </summary>
    public AdminApi Api => _api;

    private AdminServer(AdminApi api, int port, ILogger log)
    {
        _api = api; _log = log; Port = port;
        // 127.0.0.1, not localhost and not +: the loopback literal is the whole security model.
        _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
    }

    /// <summary>
    /// Build and start the server, or return null when it is not configured. A missing token is
    /// the normal case on a machine that does not want the tool, so it logs at Information and
    /// carries on rather than throwing into Program's startup path.
    /// </summary>
    public static AdminServer? TryStart(CharacterStore store,
        Func<IReadOnlyList<AdminOnlineRow>>? online, ILogger log)
    {
        string? token = TerasConfig.Get(TokenVariable);
        if (string.IsNullOrWhiteSpace(token))
        {
            log.LogInformation("admin web: {Var} is not set - not starting", TokenVariable);
            return null;
        }

        int port = DefaultPort;
        string? p = TerasConfig.Get(PortVariable);
        if (!string.IsNullOrWhiteSpace(p) && int.TryParse(p, out int parsed) && parsed is > 0 and < 65536)
            port = parsed;

        var server = new AdminServer(new AdminApi(store, token, online, log), port, log);
        try
        {
            server._listener.Start();
        }
        catch (Exception ex)
        {
            log.LogWarning("admin web: could not bind 127.0.0.1:{Port} - {Msg}", port, ex.Message);
            server.Dispose();
            return null;
        }

        _ = Task.Run(() => server.LoopAsync());
        log.LogInformation("admin web listening on http://127.0.0.1:{Port}/", port);
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
            catch (Exception ex) { _log.LogWarning("admin web: {Msg}", ex.Message); }
        }
    }

    private void Serve(HttpListenerContext ctx)
    {
        var req = ctx.Request;
        string ip = req.RemoteEndPoint?.Address.ToString() ?? "?";

        // Belt and braces: the prefix already binds loopback, but a proxy in front would not show.
        if (req.RemoteEndPoint != null && !IPAddress.IsLoopback(req.RemoteEndPoint.Address))
        {
            Write(ctx, new AdminResponse(403, "text/plain; charset=utf-8", "loopback only"));
            return;
        }

        var query = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string? key in req.QueryString.AllKeys)
            if (key != null) query[key] = req.QueryString[key] ?? string.Empty;

        // T106: three ways in, in order of preference. X-Admin-Token is what the page sends -
        // it prompts for the token itself now, because the page is served WITHOUT one, so the
        // browser has nowhere to put an Authorization header on the first request.
        string? token = req.Headers[TokenHeader];
        if (string.IsNullOrEmpty(token))
        {
            string? auth = req.Headers["Authorization"];
            if (auth != null && auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                token = auth["Bearer ".Length..].Trim();
            else if (query.TryGetValue("token", out var qt)) token = qt;
        }

        string? body = null;
        if (req.HasEntityBody)
        {
            using var reader = new StreamReader(req.InputStream, req.ContentEncoding ?? Encoding.UTF8);
            body = reader.ReadToEnd();
        }

        Write(ctx, _api.Handle(req.HttpMethod ?? "GET", req.Url?.AbsolutePath ?? "/", query, body, token, ip));
    }

    private static void Write(HttpListenerContext ctx, AdminResponse r)
    {
        var bytes = Encoding.UTF8.GetBytes(r.Body);
        ctx.Response.StatusCode = r.Status;
        ctx.Response.ContentType = r.ContentType;
        // T206: the embedded UI asks for no-store and carries an ETag. no-store rather than a
        // long max-age because the assets ship inside the assembly: after an upgrade the operator
        // reloads the page, and a cached module from the previous build would call endpoints that
        // moved. The ETag is there so the Settings screen can show which build is live.
        if (r.CacheControl != null) ctx.Response.Headers["Cache-Control"] = r.CacheControl;
        if (r.ETag != null) ctx.Response.Headers["ETag"] = r.ETag;
        ctx.Response.ContentLength64 = bytes.Length;
        ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
        ctx.Response.OutputStream.Close();
    }

    public void Dispose()
    {
        try { _stop.Cancel(); } catch { }
        try { if (_listener.IsListening) _listener.Stop(); } catch { }
        try { _listener.Close(); } catch { }
        _stop.Dispose();
    }
}
