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
  .tiles { display:grid; grid-template-columns:repeat(auto-fit, minmax(150px, 1fr)); gap:10px; }
  .tile { border:1px solid var(--line); border-radius:6px; padding:10px 12px; }
  .tile b { display:block; font-size:19px; font-weight:600; letter-spacing:-0.01em; }
  .tile span { font-size:11px; text-transform:uppercase; letter-spacing:0.06em; color:var(--dim); }
  .dot { display:inline-block; width:8px; height:8px; border-radius:50%; margin-right:6px; }
  #tail { max-height:420px; }
  @media (max-width: 480px) { .tiles { grid-template-columns:repeat(2, 1fr); } }
</style>
</head>
<body>
<main>
  <h1>TeraSharp admin</h1>
  <p class='sub'>Status, lookups, restore and the character edits. 127.0.0.1 only - the token is kept in this browser.</p>

  <section>
    <h2>Token</h2>
    <div class='row'>
      <input id='token' type='password' placeholder='TERASHARP_ADMIN_TOKEN' size='32'>
      <button onclick='save()'>Remember</button>
    </div>
    <div class='note'>Kept in this browser only, and sent as a bearer header.</div>
  </section>

  <section>
    <h2>Status</h2>
    <div class='tiles' id='tiles'>
      <div class='tile'><b id='st_up'>-</b><span>uptime</span></div>
      <div class='tile'><b id='st_world'>-</b><span>world links</span></div>
      <div class='tile'><b id='st_online'>-</b><span>online</span></div>
      <div class='tile'><b id='st_mem'>-</b><span>working set</span></div>
      <div class='tile'><b id='st_gc'>-</b><span>managed / gc</span></div>
      <div class='tile'><b id='st_thr'>-</b><span>threads</span></div>
    </div>
    <div class='row' style='margin-top:12px'>
      <button onclick='toggleLive()' id='livebtn'>Start live tail</button>
      <span class='note' id='st_file' style='margin:0'></span>
    </div>
    <pre id='tail'>-</pre>
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
    <h2>Deleted characters</h2>
    <div class='row'>
      <button onclick='go(5)'>List</button>
      <input id='rid' placeholder='character id' size='12'>
      <input id='reason' placeholder='reason' size='24'>
      <button onclick='go(3)'>Restore</button>
    </div>
    <div class='note'>A delete is a soft delete: the row and its items wait out
    deleteCharacterExpireHour2 (72 h) before the purge takes them. Restore inside the window
    brings the items back too.</div>
    <pre id='out5'>-</pre>
    <pre id='out3'>-</pre>
  </section>

  <section>
    <h2>Edit a character</h2>
    <div class='row'>
      <input id='tid' placeholder='character id' size='12'>
      <input id='treason' placeholder='reason' size='20'>
    </div>
    <div class='row' style='margin-top:8px'>
      <input id='money' placeholder='money' size='12'>
      <button onclick='go(6)'>Set money</button>
      <input id='lvl' placeholder='level 1-70' size='10'>
      <button onclick='go(7)'>Set level</button>
    </div>
    <div class='row' style='margin-top:8px'>
      <input id='tpl' placeholder='item template id' size='16'>
      <input id='amt' placeholder='amount' size='8'>
      <button onclick='go(8)'>Give item</button>
    </div>
    <pre id='out6'>-</pre>
  </section>

  <section>
    <h2>Restrictions and sessions</h2>
    <div class='row'>
      <input id='bid' placeholder='character id' size='12'>
      <input id='bhours' placeholder='hours (0 = forever)' size='18'>
      <input id='breason' placeholder='reason' size='20'>
      <button onclick='go(9)'>Ban</button>
      <button onclick='go(10)'>Unban</button>
      <button onclick='go(11)'>Kick</button>
    </div>
    <pre id='out9'>-</pre>
  </section>

  <section>
    <h2>Announce</h2>
    <div class='row'>
      <input id='atext' placeholder='message to everyone in world' size='40'>
      <button onclick='go(12)'>Send</button>
    </div>
    <pre id='out12'>-</pre>
  </section>

  <section>
    <h2>GM level</h2>
    <div class='row'>
      <input id='gaid' placeholder='account id' size='12'>
      <input id='glvl' placeholder='admin level' size='12'>
      <button onclick='go(13)'>Set</button>
    </div>
    <pre id='out13'>-</pre>
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
  // T106: the page is served without a token, so it carries its own in this header.
  const opt = { method: method, headers: { 'X-Admin-Token': tok() } };
  if (body) { opt.headers['Content-Type'] = 'application/json'; opt.body = JSON.stringify(body); }
  const r = await fetch(path, opt);
  let text;
  try { text = JSON.stringify(await r.json(), null, 2); } catch (e) { text = await r.text(); }
  return r.status + '\n' + text;
}

function num(id) { return parseInt(document.getElementById(id).value, 10) || 0; }

async function go(n) {
  // every control in a section writes into that section's one pre
  const slot = n >= 6 && n <= 8 ? 6 : (n >= 9 && n <= 11 ? 9 : n);
  const out = document.getElementById('out' + slot);
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
    if (n === 5) out.textContent = await call('GET', '/api/deleted');
    if (n >= 6 && n <= 8) {
      const t = { id: num('tid'), reason: document.getElementById('treason').value };
      if (n === 6) { t.money = num('money'); out.textContent = await call('POST', '/api/set-money', t); }
      if (n === 7) { t.level = num('lvl'); out.textContent = await call('POST', '/api/set-level', t); }
      if (n === 8) { t.templateId = num('tpl'); t.amount = num('amt') || 1;
                     out.textContent = await call('POST', '/api/give-item', t); }
    }
    if (n >= 9 && n <= 11) {
      const t = { id: num('bid'), hours: num('bhours'), reason: document.getElementById('breason').value };
      const where = n === 9 ? '/api/ban' : (n === 10 ? '/api/unban' : '/api/kick');
      out.textContent = await call('POST', where, t);
    }
    if (n === 12) out.textContent = await call('POST', '/api/announce', { text: document.getElementById('atext').value });
    if (n === 13) out.textContent = await call('POST', '/api/gm-level',
      { accountId: num('gaid'), level: num('glvl') });
  } catch (e) { out.textContent = 'error: ' + e; }
}

// ---- T106: the Status tab ----

function dur(s) {
  s = Math.max(0, Math.floor(s));
  const d = Math.floor(s / 86400), h = Math.floor(s % 86400 / 3600);
  const m = Math.floor(s % 3600 / 60);
  if (d) return d + 'd ' + h + 'h';
  if (h) return h + 'h ' + m + 'm';
  return m + 'm ' + (s % 60) + 's';
}
function mb(n) { return (n / 1048576).toFixed(0) + ' MB'; }
function set(id, text) { document.getElementById(id).textContent = text; }

async function refreshStatus() {
  if (!tok()) { set('st_up', '-'); return; }
  const r = await fetch('/api/status', { headers: { 'X-Admin-Token': tok() } });
  if (!r.ok) { set('st_up', r.status === 401 ? 'token?' : 'err'); return; }
  const s = await r.json();
  set('st_up', dur(s.uptimeSeconds));
  set('st_world', s.worldLinks + (s.worldReady ? ' ready' : ' loading'));
  set('st_online', s.online);
  set('st_mem', mb(s.workingSetBytes));
  set('st_gc', mb(s.managedBytes) + ' / ' + s.gc0 + '-' + s.gc2);
  set('st_thr', s.threads);
  document.getElementById('st_file').textContent =
    (s.logFile || 'no log file') + '  -  console ' + s.consoleLevel;
}

async function refreshTail() {
  if (!tok()) return;
  const r = await fetch('/api/log?lines=200', { headers: { 'X-Admin-Token': tok() } });
  if (!r.ok) return;
  const s = await r.json();
  const pre = document.getElementById('tail');
  const stick = pre.scrollTop + pre.clientHeight >= pre.scrollHeight - 24;
  pre.textContent = s.lines.join('\n') || '(nothing logged yet)';
  if (stick) pre.scrollTop = pre.scrollHeight;
}

let liveTimer = null;
async function tick() { try { await refreshStatus(); await refreshTail(); } catch (e) {} }
function toggleLive() {
  const btn = document.getElementById('livebtn');
  if (liveTimer) { clearInterval(liveTimer); liveTimer = null; btn.textContent = 'Start live tail'; return; }
  tick();
  liveTimer = setInterval(tick, 2000);
  btn.textContent = 'Stop live tail';
}
tick();
</script>
</body>
</html>
";
}
