using System.Net;
using System.Text;
using Microsoft.Extensions.Logging;
using TeraSharp.Arbiter.Persistence;

namespace TeraSharp.Arbiter.Web;

// =============================================================================================
// AdminServer - the HttpListener shell around AdminApi. T101 phase 1.
//
// Everything that decides anything lives in AdminApi; this file only moves bytes. It binds
// 127.0.0.1 ONLY - never + or *, which would make the tool reachable from the network - and it
// refuses to start at all when TERASHARP_ADMIN_TOKEN is unset.
//
//   TERASHARP_ADMIN_PORT    default 8050
//   TERASHARP_ADMIN_TOKEN   required; sent as  Authorization: Bearer <token>  or ?token=
// =============================================================================================
public sealed class AdminServer : IDisposable
{
    public const int DefaultPort = 8050;
    public const string PortVariable = "TERASHARP_ADMIN_PORT";
    public const string TokenVariable = "TERASHARP_ADMIN_TOKEN";

    private readonly HttpListener _listener = new();
    private readonly AdminApi _api;
    private readonly ILogger _log;
    private readonly CancellationTokenSource _stop = new();

    public int Port { get; }

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
        string? token = Environment.GetEnvironmentVariable(TokenVariable);
        if (string.IsNullOrWhiteSpace(token))
        {
            log.LogInformation("admin web: {Var} is not set - not starting", TokenVariable);
            return null;
        }

        int port = DefaultPort;
        string? p = Environment.GetEnvironmentVariable(PortVariable);
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

        string? token = null;
        string? auth = req.Headers["Authorization"];
        if (auth != null && auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            token = auth["Bearer ".Length..].Trim();
        else if (query.TryGetValue("token", out var qt)) token = qt;

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

/// <summary>
/// The one page the tool serves. Single-quoted HTML attributes throughout, so the whole thing
/// fits in a verbatim string without a double quote in it - CLAUDE.md's rule.
/// </summary>
public static class AdminPage
{
    public const string Html = @"<!doctype html>
<html lang='en'>
<head>
<meta charset='utf-8'>
<meta name='viewport' content='width=device-width, initial-scale=1'>
<title>TeraSharp admin</title>
<style>
  :root { color-scheme: light dark; --bg:#fbfbfa; --fg:#1a1a18; --line:#dedddb; --dim:#6b6a67; --accent:#2a6b4f; }
  @media (prefers-color-scheme: dark) {
    :root { --bg:#191918; --fg:#eeeeec; --line:#333230; --dim:#a3a29e; --accent:#7fc0a0; }
  }
  * { box-sizing: border-box; }
  body { margin:0; padding:24px 16px 64px; background:var(--bg); color:var(--fg);
         font:15px/1.5 ui-sans-serif, system-ui, -apple-system, Segoe UI, sans-serif; }
  main { max-width: 900px; margin: 0 auto; }
  h1 { font-size:19px; margin:0 0 4px; letter-spacing:-0.01em; }
  p.sub { margin:0 0 24px; color:var(--dim); font-size:13px; }
  section { border:1px solid var(--line); border-radius:8px; padding:16px; margin-bottom:16px; background:transparent; }
  h2 { font-size:13px; text-transform:uppercase; letter-spacing:0.06em; color:var(--dim); margin:0 0 12px; }
  input, button { font:inherit; padding:7px 10px; border:1px solid var(--line); border-radius:6px;
                  background:var(--bg); color:var(--fg); }
  button { cursor:pointer; border-color:var(--accent); color:var(--accent); }
  button:hover { background:var(--accent); color:var(--bg); }
  .row { display:flex; gap:8px; flex-wrap:wrap; align-items:center; }
  pre { margin:12px 0 0; padding:12px; background:rgba(127,127,127,.09); border-radius:6px;
        overflow:auto; font:12px/1.5 ui-monospace, SFMono-Regular, Menlo, monospace; max-height:340px; }
  .note { font-size:12px; color:var(--dim); margin-top:10px; }
</style>
</head>
<body>
<main>
  <h1>TeraSharp admin</h1>
  <p class='sub'>Phase 1 - read-only lookups and restore. 127.0.0.1 only.</p>

  <section>
    <h2>Token</h2>
    <div class='row'>
      <input id='token' type='password' placeholder='TERASHARP_ADMIN_TOKEN' size='32'>
      <button onclick='save()'>Remember</button>
    </div>
    <div class='note'>Kept in this browser only, and sent as a bearer header.</div>
  </section>

  <section>
    <h2>Accounts</h2>
    <div class='row'>
      <input id='q' placeholder='name or id' size='24'>
      <button onclick='go(0)'>Search</button>
    </div>
    <pre id='out0'>-</pre>
  </section>

  <section>
    <h2>Character</h2>
    <div class='row'>
      <input id='cid' placeholder='id' size='8'>
      <input id='cname' placeholder='or name' size='18'>
      <button onclick='go(1)'>Look up</button>
    </div>
    <pre id='out1'>-</pre>
  </section>

  <section>
    <h2>Online</h2>
    <div class='row'><button onclick='go(2)'>Refresh</button></div>
    <pre id='out2'>-</pre>
  </section>

  <section>
    <h2>Restore a scheduled delete</h2>
    <div class='row'>
      <input id='rid' placeholder='character id' size='12'>
      <input id='reason' placeholder='reason' size='24'>
      <button onclick='go(3)'>Restore</button>
    </div>
    <div class='note'>Clears a pending delete inside its grace window. A character whose row is
    already gone needs the phase-2 soft-delete tables.</div>
    <pre id='out3'>-</pre>
  </section>

  <section>
    <h2>Admin log</h2>
    <div class='row'><button onclick='go(4)'>Show</button></div>
    <pre id='out4'>-</pre>
  </section>
</main>
<script>
function tok() { return document.getElementById('token').value; }
function save() { try { localStorage.setItem('ts_admin_token', tok()); } catch (e) {} }
try { document.getElementById('token').value = localStorage.getItem('ts_admin_token') || ''; } catch (e) {}

async function call(method, path, body) {
  const opt = { method: method, headers: { 'Authorization': 'Bearer ' + tok() } };
  if (body) { opt.headers['Content-Type'] = 'application/json'; opt.body = JSON.stringify(body); }
  const r = await fetch(path, opt);
  let text;
  try { text = JSON.stringify(await r.json(), null, 2); } catch (e) { text = await r.text(); }
  return r.status + '\n' + text;
}

async function go(n) {
  const out = document.getElementById('out' + n);
  out.textContent = 'working...';
  try {
    if (n === 0) out.textContent = await call('GET', '/api/accounts?q=' + encodeURIComponent(document.getElementById('q').value));
    if (n === 1) {
      const id = document.getElementById('cid').value.trim();
      const nm = document.getElementById('cname').value.trim();
      const qs = id ? 'id=' + encodeURIComponent(id) : 'name=' + encodeURIComponent(nm);
      out.textContent = await call('GET', '/api/character?' + qs);
    }
    if (n === 2) out.textContent = await call('GET', '/api/online');
    if (n === 3) out.textContent = await call('POST', '/api/restore-character', {
      id: parseInt(document.getElementById('rid').value, 10) || 0,
      reason: document.getElementById('reason').value
    });
    if (n === 4) out.textContent = await call('GET', '/api/admin-log?limit=100');
  } catch (e) { out.textContent = 'error: ' + e; }
}
</script>
</body>
</html>
";
}
