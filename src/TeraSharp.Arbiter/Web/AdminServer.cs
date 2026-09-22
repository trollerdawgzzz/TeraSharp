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
  :root { color-scheme: light dark; --bg:#fbfbfa; --panel:#fff; --fg:#1a1a18; --line:#dedddb;
          --dim:#6b6a67; --accent:#2a6b4f; --warn:#9a3b2c; --chip:rgba(42,107,79,.10); }
  @media (prefers-color-scheme: dark) {
    :root { --bg:#191918; --panel:#1f1f1e; --fg:#eeeeec; --line:#333230; --dim:#a3a29e;
            --accent:#7fc0a0; --warn:#e08a78; --chip:rgba(127,192,160,.12); }
  }
  * { box-sizing: border-box; }
  body { margin:0; background:var(--bg); color:var(--fg);
         font:14px/1.55 ui-sans-serif, system-ui, -apple-system, Segoe UI, sans-serif; }
  .app { display:grid; grid-template-columns:200px 1fr; min-height:100vh; }
  nav { border-right:1px solid var(--line); padding:18px 12px; position:sticky; top:0; height:100vh; overflow:auto; }
  nav h1 { font-size:14px; margin:0 0 2px; letter-spacing:-0.01em; }
  nav p { margin:0 0 16px; font-size:11px; color:var(--dim); }
  nav a { display:block; padding:7px 10px; border-radius:6px; color:var(--fg); text-decoration:none;
          font-size:13px; cursor:pointer; }
  nav a:hover { background:var(--chip); }
  nav a.on { background:var(--chip); color:var(--accent); font-weight:600; }
  main { padding:22px 24px 64px; max-width:1100px; }
  section { display:none; }
  section.on { display:block; }
  h2 { font-size:16px; margin:0 0 4px; letter-spacing:-0.01em; }
  h3 { font-size:11px; text-transform:uppercase; letter-spacing:0.07em; color:var(--dim);
       margin:22px 0 8px; }
  p.sub { margin:0 0 18px; color:var(--dim); font-size:12px; }
  .card { border:1px solid var(--line); border-radius:8px; padding:14px 16px; margin-bottom:14px;
          background:var(--panel); }
  input, button, select, textarea { font:inherit; padding:6px 9px; border:1px solid var(--line);
          border-radius:6px; background:var(--bg); color:var(--fg); }
  textarea { width:100%; min-height:60px; resize:vertical; }
  button { cursor:pointer; border-color:var(--accent); color:var(--accent); background:transparent; }
  button:hover { background:var(--accent); color:var(--bg); }
  button.danger { border-color:var(--warn); color:var(--warn); }
  button.on { background:var(--accent); color:var(--bg); }
  button.danger:hover { background:var(--warn); color:var(--bg); }
  .row { display:flex; gap:8px; flex-wrap:wrap; align-items:center; }
  .row + .row { margin-top:8px; }
  table { width:100%; border-collapse:collapse; font-size:13px; }
  th, td { text-align:left; padding:6px 8px; border-bottom:1px solid var(--line); vertical-align:top; }
  th { font-size:11px; text-transform:uppercase; letter-spacing:0.05em; color:var(--dim);
       font-weight:600; border-bottom-width:2px; }
  tbody tr:hover { background:var(--chip); }
  td.num { text-align:right; font-variant-numeric:tabular-nums; }
  .tiles { display:grid; grid-template-columns:repeat(auto-fit, minmax(140px, 1fr)); gap:10px; }
  .tile { border:1px solid var(--line); border-radius:6px; padding:10px 12px; background:var(--panel); }
  .tile b { display:block; font-size:18px; font-weight:600; letter-spacing:-0.01em; }
  .tile span { font-size:10px; text-transform:uppercase; letter-spacing:0.06em; color:var(--dim); }
  .chip { display:inline-block; padding:1px 7px; border-radius:99px; background:var(--chip);
          font-size:11px; color:var(--accent); }
  .chip.off { background:rgba(127,127,127,.12); color:var(--dim); }
  pre { margin:10px 0 0; padding:10px; background:rgba(127,127,127,.09); border-radius:6px;
        overflow:auto; font:11.5px/1.5 ui-monospace, SFMono-Regular, Menlo, monospace; max-height:380px; }
  .note { font-size:11.5px; color:var(--dim); margin-top:8px; }
  .empty { color:var(--dim); font-size:12.5px; padding:10px 0; }
  dialog { border:1px solid var(--line); border-radius:8px; background:var(--panel); color:var(--fg);
           padding:18px 20px; max-width:420px; }
  dialog::backdrop { background:rgba(0,0,0,.45); }
  @media (max-width: 720px) {
    .app { grid-template-columns:1fr; }
    nav { position:static; height:auto; border-right:0; border-bottom:1px solid var(--line); }
    nav a { display:inline-block; }
    main { padding:16px; }
  }
</style>
</head>
<body>
<div class='app'>
<nav>
  <h1>TeraSharp admin</h1>
  <p>127.0.0.1 only</p>
  <div class='row' style='margin-bottom:14px'>
    <input id='token' type='password' placeholder='token' size='12'>
    <button onclick='saveToken()'>Save</button>
  </div>
  <a data-tab='status' class='on'>Status</a>
  <a data-tab='search'>Search</a>
  <a data-tab='account'>Account</a>
  <a data-tab='character'>Character</a>
  <a data-tab='online'>Online</a>
  <a data-tab='restrict'>Restrictions</a>
  <a data-tab='announce'>Announces</a>
  <a data-tab='grant'>Grants</a>
  <a data-tab='deleted'>Deleted</a>
  <a data-tab='logs'>Logs</a>
</nav>
<main>

<section id='t-status' class='on'>
  <h2>Status</h2>
  <p class='sub'>The process, the World link and the tail of the log.</p>
  <div class='tiles'>
    <div class='tile'><b id='st_up'>-</b><span>uptime</span></div>
    <div class='tile'><b id='st_world'>-</b><span>world links</span></div>
    <div class='tile'><b id='st_online'>-</b><span>online</span></div>
    <div class='tile'><b id='st_mem'>-</b><span>working set</span></div>
    <div class='tile'><b id='st_gc'>-</b><span>managed / gc</span></div>
    <div class='tile'><b id='st_thr'>-</b><span>threads</span></div>
  </div>
  <div class='row' style='margin-top:12px'>
    <button id='livebtn' onclick='toggleLive()'>Start live tail</button>
    <span class='note' id='st_file' style='margin:0'></span>
  </div>
  <pre id='tail'>-</pre>
</section>

<section id='t-search'>
  <h2>Search</h2>
  <p class='sub'>Characters by name prefix or id, a guild roster, or who is in world now.</p>
  <div class='card'>
    <div class='row'>
      <select id='skind'>
        <option value='name'>name (prefix)</option>
        <option value='id'>id (character or account)</option>
        <option value='guild'>guild name</option>
        <option value='online'>online</option>
      </select>
      <input id='sq' placeholder='term' size='22'>
      <button onclick='doSearch()'>Search</button>
    </div>
    <div class='note' id='snote'></div>
  </div>
  <div id='sresult'></div>
</section>

<section id='t-account'>
  <h2>Account</h2>
  <p class='sub'>Its characters, benefits, restrictions and play time.</p>
  <div class='card'>
    <div class='row'>
      <input id='aid' placeholder='account id' size='10'>
      <input id='aname' placeholder='or name' size='16'>
      <button onclick='loadAccount()'>Open</button>
    </div>
  </div>
  <div id='aresult'></div>
</section>

<section id='t-character'>
  <h2>Character</h2>
  <p class='sub'>Inventory, warehouse, money, progress, guild, EP, cards and position.</p>
  <div class='card'>
    <div class='row'>
      <input id='cid' placeholder='character id' size='10'>
      <input id='cname' placeholder='or name' size='16'>
      <button onclick='loadCharacter()'>Open</button>
    </div>
    <div class='row'>
      <input id='rename' placeholder='new name' size='16'>
      <input id='renreason' placeholder='reason' size='18'>
      <button class='danger' onclick='askRename()'>Rename</button>
    </div>
  </div>
  <div id='cresult'></div>
</section>

<section id='t-online'>
  <h2>Online</h2>
  <p class='sub'>Live sessions. Kick, warn, or pull one player to another.</p>
  <div class='card'>
    <div class='row'>
      <button onclick='loadOnline()'>Refresh</button>
      <input id='oreason' placeholder='reason (recorded)' size='24'>
    </div>
    <div class='row'>
      <input id='warntext' placeholder='warning text' size='30'>
      <input id='tpto' placeholder='teleport target id' size='16'>
    </div>
  </div>
  <div id='oresult'></div>
</section>

<section id='t-restrict'>
  <h2>Restrictions</h2>
  <p class='sub'>Bans and mutes: reason and expiry, list and lift. Hours 0 is permanent.</p>
  <div class='card'>
    <div class='row'>
      <input id='bid' placeholder='character id' size='10'>
      <input id='bname' placeholder='or name' size='16'>
      <button onclick='loadRestrictions()'>List</button>
    </div>
    <div class='row'>
      <input id='bhours' placeholder='hours (0 = forever)' size='16'>
      <input id='breason' placeholder='reason' size='22'>
      <button class='danger' onclick='askRestrict(1)'>Ban</button>
      <button onclick='askRestrict(2)'>Unban</button>
      <button class='danger' onclick='askRestrict(3)'>Mute</button>
      <button onclick='askRestrict(4)'>Unmute</button>
    </div>
  </div>
  <div id='bresult'></div>
</section>

<section id='t-announce'>
  <h2>Announces</h2>
  <p class='sub'>Send one now, or schedule one. A scheduled announce survives a restart.</p>
  <div class='card'>
    <h3>Now</h3>
    <textarea id='annow' placeholder='message to everyone in world'></textarea>
    <div class='row'><button onclick='askAnnounceNow()'>Send now</button></div>
  </div>
  <div class='card'>
    <h3>Scheduled</h3>
    <textarea id='anlater' placeholder='message'></textarea>
    <div class='row'>
      <input id='anstart' type='datetime-local'>
      <input id='anend' type='datetime-local'>
      <input id='aninterval' placeholder='repeat every N seconds (0 = once)' size='26'>
      <button onclick='scheduleAnnounce()'>Schedule</button>
    </div>
    <div class='note'>Start blank means now. Repeat 0 sends it once and disables the row.</div>
  </div>
  <div id='anresult'></div>
</section>

<section id='t-grant'>
  <h2>Grants</h2>
  <p class='sub'>Money, level, items and GM level. Every one records its reason.</p>
  <div class='card'>
    <div class='row'>
      <input id='gid' placeholder='character id' size='10'>
      <input id='gname' placeholder='or name' size='16'>
      <input id='greason' placeholder='reason' size='22'>
    </div>
    <div class='row'>
      <input id='gmoney' placeholder='set money to' size='14'>
      <button onclick='askGrant(1)'>Set money</button>
      <input id='glevel' placeholder='level 1-70' size='11'>
      <button onclick='askGrant(2)'>Set level</button>
    </div>
    <div class='row'>
      <input id='gtpl' placeholder='item template id' size='16'>
      <input id='gamt' placeholder='amount' size='9'>
      <button onclick='askGrant(3)'>Give item</button>
    </div>
    <div class='row'>
      <input id='gacct' placeholder='account id' size='12'>
      <input id='glvl' placeholder='admin level' size='12'>
      <button class='danger' onclick='askGrant(4)'>Set GM level</button>
    </div>
  </div>
  <div id='gresult'></div>
</section>

<section id='t-deleted'>
  <h2>Deleted characters</h2>
  <p class='sub'>Waiting out the 72 h window. Restore brings the items back too.</p>
  <div class='card'>
    <div class='row'>
      <button onclick='loadDeleted()'>Refresh</button>
      <input id='dreason' placeholder='reason' size='22'>
    </div>
  </div>
  <div id='dresult'></div>
</section>

<section id='t-logs'>
  <h2>Logs</h2>
  <p class='sub'>What the players did, and what this tool did.</p>
  <div class='row' style='margin-bottom:12px'>
    <button id='lt-game' class='on' onclick='logTab(0)'>Game log</button>
    <button id='lt-admin' onclick='logTab(1)'>Admin log</button>
  </div>

  <div id='lp-game'>
    <div class='card'>
      <div class='row'>
        <input id='gl_who' placeholder='account or character (name or id)' size='30'>
        <select id='gl_cat'><option value=''>any category</option></select>
        <input id='gl_act' placeholder='action prefix' size='14'>
      </div>
      <div class='row' style='margin-top:8px'>
        <label class='note'>from <input id='gl_from' type='datetime-local'></label>
        <label class='note'>to <input id='gl_to' type='datetime-local'></label>
        <select id='gl_size'>
          <option value='25'>25 / page</option>
          <option value='50' selected>50 / page</option>
          <option value='100'>100 / page</option>
          <option value='200'>200 / page</option>
        </select>
        <button onclick='loadGameLog(0)'>Search</button>
        <button onclick='glPage(-1)'>Prev</button>
        <button onclick='glPage(1)'>Next</button>
      </div>
      <div class='note' id='gl_note'></div>
    </div>
    <div id='glresult'></div>
  </div>

  <div id='lp-admin' style='display:none'>
    <div class='card'><div class='row'><button onclick='loadAudit()'>Refresh</button></div></div>
    <div id='auresult'></div>
  </div>
</section>

</main>
</div>

<dialog id='confirm'>
  <h2 id='cf_title'>Confirm</h2>
  <p id='cf_body' class='sub'></p>
  <div class='row'>
    <button class='danger' onclick='confirmYes()'>Do it</button>
    <button onclick='document.getElementById(&#39;confirm&#39;).close()'>Cancel</button>
  </div>
</dialog>

<script>
// ---- plumbing -------------------------------------------------------------
function tok() { return document.getElementById('token').value; }
function saveToken() { try { localStorage.setItem('ts_admin_token', tok()); } catch (e) {} tick(); }
try { document.getElementById('token').value = localStorage.getItem('ts_admin_token') || ''; } catch (e) {}

function el(id) { return document.getElementById(id); }
function val(id) { return el(id).value.trim(); }
function num(id) { return parseInt(val(id), 10) || 0; }
function esc(s) {
  return String(s === null || s === undefined ? '' : s)
    .split('&').join('&amp;').split('<').join('&lt;').split('>').join('&gt;');
}

async function api(method, path, body) {
  const opt = { method: method, headers: { 'X-Admin-Token': tok() } };
  if (body) { opt.headers['Content-Type'] = 'application/json'; opt.body = JSON.stringify(body); }
  const r = await fetch(path, opt);
  let data = null;
  try { data = await r.json(); } catch (e) { data = { message: 'no JSON in the reply' }; }
  return { status: r.status, data: data };
}

function say(id, r) {
  const ok = r.status === 200;
  el(id).innerHTML = `<div class='card'><b>${ok ? 'Done' : 'Refused'}</b> `
    + `<span class='chip${ok ? '' : ' off'}'>${r.status} / result ${esc(r.data.result)}</span>`
    + `<div class='note'>${esc(r.data.message || JSON.stringify(r.data))}</div></div>`;
}

function table(cols, rows) {
  if (!rows || !rows.length) return `<div class='empty'>Nothing to show.</div>`;
  const head = cols.map(c => `<th>${esc(c.label)}</th>`).join('');
  const body = rows.map(row =>
    '<tr>' + cols.map(c => `<td class='${c.num ? 'num' : ''}'>${c.cell(row)}</td>`).join('') + '</tr>'
  ).join('');
  return `<div class='card'><table><thead><tr>${head}</tr></thead><tbody>${body}</tbody></table></div>`;
}

function when(unix) {
  if (!unix) return '-';
  return new Date(unix * 1000).toISOString().replace('T', ' ').slice(0, 16);
}
function localUnix(id) {
  const v = val(id);
  if (!v) return 0;
  const t = Date.parse(v);
  return isNaN(t) ? 0 : Math.floor(t / 1000);
}
function itemLabel(it) {
  return it.name ? `${esc(it.name)} <span class='note'>${it.templateId}</span>` : esc(it.templateId);
}

// ---- tabs -----------------------------------------------------------------
const tabs = document.querySelectorAll('nav a[data-tab]');
tabs.forEach(a => a.onclick = () => {
  tabs.forEach(x => x.classList.remove('on'));
  a.classList.add('on');
  document.querySelectorAll('main section').forEach(s => s.classList.remove('on'));
  el('t-' + a.dataset.tab).classList.add('on');
  // T116: arriving at Logs fills the category dropdown and shows the newest rows,
  // so the tab is never a blank form with an empty select.
  if (a.dataset.tab === 'logs' && !el('gl_cat').value && !glTotal) loadGameLog(0);
});

// ---- confirmation ---------------------------------------------------------
let pending = null;
function ask(title, body, run) {
  pending = run;
  el('cf_title').textContent = title;
  el('cf_body').textContent = body;
  el('confirm').showModal();
}
function confirmYes() {
  el('confirm').close();
  const run = pending; pending = null;
  if (run) run();
}

// ---- status ---------------------------------------------------------------
function dur(s) {
  s = Math.max(0, Math.floor(s));
  const d = Math.floor(s / 86400), h = Math.floor(s % 86400 / 3600), m = Math.floor(s % 3600 / 60);
  if (d) return d + 'd ' + h + 'h';
  if (h) return h + 'h ' + m + 'm';
  return m + 'm ' + (s % 60) + 's';
}
function mb(n) { return (n / 1048576).toFixed(0) + ' MB'; }

async function refreshStatus() {
  if (!tok()) { el('st_up').textContent = 'token?'; return; }
  const r = await api('GET', '/api/status');
  if (r.status !== 200) { el('st_up').textContent = r.status === 401 ? 'token?' : 'err'; return; }
  const s = r.data;
  el('st_up').textContent = dur(s.uptimeSeconds);
  el('st_world').textContent = s.worldLinks + (s.worldReady ? ' ready' : ' loading');
  el('st_online').textContent = s.online;
  el('st_mem').textContent = mb(s.workingSetBytes);
  el('st_gc').textContent = mb(s.managedBytes) + ' / ' + s.gc0 + '-' + s.gc2;
  el('st_thr').textContent = s.threads;
  el('st_file').textContent = (s.logFile || 'no log file') + '  -  console ' + s.consoleLevel;
}
async function refreshTail() {
  if (!tok()) return;
  const r = await api('GET', '/api/log?lines=200');
  if (r.status !== 200) return;
  const pre = el('tail');
  const stick = pre.scrollTop + pre.clientHeight >= pre.scrollHeight - 24;
  pre.textContent = r.data.lines.join('\n') || '(nothing logged yet)';
  if (stick) pre.scrollTop = pre.scrollHeight;
}
let liveTimer = null;
async function tick() { try { await refreshStatus(); await refreshTail(); } catch (e) {} }
function toggleLive() {
  const b = el('livebtn');
  if (liveTimer) { clearInterval(liveTimer); liveTimer = null; b.textContent = 'Start live tail'; return; }
  tick(); liveTimer = setInterval(tick, 2000); b.textContent = 'Stop live tail';
}

// ---- search ---------------------------------------------------------------
async function doSearch() {
  const r = await api('GET', '/api/search?kind=' + encodeURIComponent(val('skind'))
    + '&q=' + encodeURIComponent(val('sq')));
  if (r.status !== 200) { say('sresult', r); return; }
  el('snote').textContent = r.data.note || '';
  el('sresult').innerHTML = table([
    { label: 'id', num: true, cell: h => esc(h.character.id) },
    { label: 'name', cell: h => `<a onclick='openCharacter(${h.character.id})'>${esc(h.character.name)}</a>` },
    { label: 'lvl', num: true, cell: h => esc(h.character.level) },
    { label: 'account', num: true, cell: h => `<a onclick='openAccount(${h.character.accountId})'>${esc(h.character.accountId)}</a>` },
    { label: 'zone', num: true, cell: h => esc(h.character.zone) },
    { label: '', cell: h => h.online ? `<span class='chip'>online</span>` : '' },
  ], r.data.hits);
}
function openCharacter(id) {
  el('cid').value = id; el('cname').value = '';
  document.querySelector(`nav a[data-tab=character]`).click();
  loadCharacter();
}
function openAccount(id) {
  el('aid').value = id; el('aname').value = '';
  document.querySelector(`nav a[data-tab=account]`).click();
  loadAccount();
}

// ---- account --------------------------------------------------------------
async function loadAccount() {
  const q = val('aid') ? 'id=' + encodeURIComponent(val('aid')) : 'name=' + encodeURIComponent(val('aname'));
  const r = await api('GET', '/api/account?' + q);
  if (r.status !== 200) { say('aresult', r); return; }
  const a = r.data.account;
  let h = `<div class='tiles'>
    <div class='tile'><b>${esc(a.id)}</b><span>account id</span></div>
    <div class='tile'><b>${esc(a.name)}</b><span>name</span></div>
    <div class='tile'><b>${esc(a.adminLevel)}</b><span>admin level</span></div>
    <div class='tile'><b>${esc(a.characterCount)}</b><span>characters</span></div>
    <div class='tile'><b>${dur(a.playTimeSec)}</b><span>play time</span></div>
    <div class='tile'><b>${esc((a.lastLogin || '-').slice(0, 16).replace('T', ' '))}</b><span>last login</span></div>
  </div>`;
  h += `<h3>Characters</h3>` + table([
    { label: 'id', num: true, cell: c => esc(c.id) },
    { label: 'name', cell: c => `<a onclick='openCharacter(${c.id})'>${esc(c.name)}</a>` },
    { label: 'lvl', num: true, cell: c => esc(c.level) },
    { label: 'class', num: true, cell: c => esc(c.class) },
    { label: 'race', num: true, cell: c => esc(c.race) },
    { label: 'zone', num: true, cell: c => esc(c.zone) },
  ], r.data.characters);
  h += `<h3>Benefits</h3>` + table([
    { label: 'package', num: true, cell: x => esc(x.packageId) },
    { label: 'expires', cell: x => when(x.expiresAt) },
    { label: 'value', num: true, cell: x => esc(x.value) },
  ], r.data.benefits);
  h += `<h3>Restrictions</h3>` + table([
    { label: 'character', cell: x => esc(x.character) },
    { label: 'type', cell: x => esc(x.typeName) },
    { label: 'until', cell: x => x.until ? when(x.until) : 'permanent' },
    { label: 'active', cell: x => x.active ? `<span class='chip'>active</span>` : `<span class='chip off'>lapsed</span>` },
    { label: 'reason', cell: x => esc(x.reason) },
  ], r.data.bans);
  el('aresult').innerHTML = h;
}

// ---- character ------------------------------------------------------------
async function loadCharacter() {
  const q = val('cid') ? 'id=' + encodeURIComponent(val('cid')) : 'name=' + encodeURIComponent(val('cname'));
  const r = await api('GET', '/api/character?' + q);
  if (r.status !== 200) { say('cresult', r); return; }
  const d = r.data, c = d.character, p = d.position, g = d.guild, pr = d.progress;
  let h = `<div class='tiles'>
    <div class='tile'><b>${esc(c.name)}</b><span>id ${esc(c.id)}</span></div>
    <div class='tile'><b>${esc(c.level)}</b><span>level</span></div>
    <div class='tile'><b>${esc(d.money)}</b><span>money</span></div>
    <div class='tile'><b>${g ? esc(g.name) : '-'}</b><span>guild</span></div>
    <div class='tile'><b>${d.ep ? esc(d.ep.level) : '-'}</b><span>ep level</span></div>
    <div class='tile'><b>${esc(p.zone)}</b><span>zone</span></div>
    <div class='tile'><b>${esc(pr.questsActive)} / ${esc(pr.questsCompleted)}</b><span>quests live / done</span></div>
    <div class='tile'><b>${esc(pr.achievements)}</b><span>achievements</span></div>
    <div class='tile'><b>${dur(pr.playSeconds || 0)}</b><span>play time</span></div>
  </div>`;
  if (d.deleteAt) h += `<div class='card'><b class='chip off'>delete pending</b> `
    + `<span class='note'>due ${when(d.deleteAt)} - restore it from the Deleted tab</span></div>`;
  h += `<div class='card note'>position ${esc(p.x)}, ${esc(p.y)}, ${esc(p.z)} `
    + `- section ${esc(p.world)} / ${esc(p.guard)} / ${esc(p.section)} `
    + `- ${esc(pr.sectionsVisited)} sections visited - exp ${esc(pr.exp)}, rested ${esc(pr.restBonus)}</div>`;

  const itemCols = [
    { label: 'slot', num: true, cell: i => esc(i.slot) },
    { label: 'item', cell: itemLabel },
    { label: 'count', num: true, cell: i => esc(i.count) },
    { label: 'db id', num: true, cell: i => esc(i.itemDbId) },
  ];
  h += `<h3>Inventory (${d.items.length})</h3>` + table(itemCols, d.items);
  (d.warehouse || []).forEach(w => {
    h += `<h3>${esc(w.tab)} warehouse - ${esc(w.money)} gold, ${esc(w.slots)} slots</h3>`
       + table(itemCols, w.items);
  });
  h += `<h3>Restrictions</h3>` + table([
    { label: 'type', cell: x => esc(x.typeName) },
    { label: 'until', cell: x => x.until ? when(x.until) : 'permanent' },
    { label: 'active', cell: x => x.active ? `<span class='chip'>active</span>` : `<span class='chip off'>lapsed</span>` },
    { label: 'reason', cell: x => esc(x.reason) },
  ], d.restrictions);
  h += `<h3>Cards</h3>` + table([
    { label: 'card', cell: itemLabel },
    { label: 'amount', num: true, cell: x => esc(x.amount) },
  ], d.cards);
  if (!d.itemNames) h += `<div class='note'>No item-name sheet loaded, so items show their template id. `
    + `See status/WEBADMIN-DESIGN.md section 11.</div>`;
  el('cresult').innerHTML = h;
}

function askRename() {
  const name = val('rename');
  if (!name) return;
  ask('Rename character', `Rename ${val('cid') || val('cname')} to ${name}?`, async () => {
    say('cresult', await api('POST', '/api/rename',
      { id: num('cid'), name: name, reason: val('renreason') }));
  });
}

// ---- online ---------------------------------------------------------------
async function loadOnline() {
  const r = await api('GET', '/api/online');
  if (r.status !== 200) { say('oresult', r); return; }
  el('oresult').innerHTML = table([
    { label: 'id', num: true, cell: o => esc(o.playerId) },
    { label: 'name', cell: o => `<a onclick='openCharacter(${o.playerId})'>${esc(o.name)}</a>` },
    { label: 'lvl', num: true, cell: o => esc(o.level) },
    { label: 'zone', num: true, cell: o => esc(o.zone) },
    { label: 'account', cell: o => esc(o.account) },
    { label: '', cell: o => `<button class='danger' onclick='askKick(${o.playerId})'>Kick</button> `
        + `<button onclick='askWarn(${o.playerId})'>Warn</button> `
        + `<button onclick='askTp(${o.playerId})'>Teleport</button>` },
  ], r.data.online);
}
function askKick(id) {
  ask('Kick player', `Disconnect ${id}?`, async () => {
    say('oresult', await api('POST', '/api/kick', { id: id, reason: val('oreason') }));
  });
}
function askWarn(id) {
  const text = val('warntext');
  if (!text) { alert('Type the warning first.'); return; }
  ask('Warn player', `Send to ${id}: ${text}`, async () => {
    say('oresult', await api('POST', '/api/warn', { id: id, text: text, reason: val('oreason') }));
  });
}
function askTp(id) {
  const to = num('tpto');
  if (!to) { alert('Type the target id first.'); return; }
  ask('Teleport', `Move ${id} to ${to}?`, async () => {
    say('oresult', await api('POST', '/api/teleport', { id: id, targetId: to, reason: val('oreason') }));
  });
}

// ---- restrictions ---------------------------------------------------------
async function loadRestrictions() {
  const q = val('bid') ? 'id=' + encodeURIComponent(val('bid')) : 'name=' + encodeURIComponent(val('bname'));
  const r = await api('GET', '/api/restrictions?' + q);
  if (r.status !== 200) { say('bresult', r); return; }
  el('bresult').innerHTML = table([
    { label: 'type', cell: x => esc(x.typeName) },
    { label: 'set', cell: x => when(x.setAt) },
    { label: 'until', cell: x => x.until ? when(x.until) : 'permanent' },
    { label: 'active', cell: x => x.active ? `<span class='chip'>active</span>` : `<span class='chip off'>lapsed</span>` },
    { label: 'reason', cell: x => esc(x.reason) },
  ], r.data.restrictions);
}
function askRestrict(which) {
  const paths = { 1: '/api/ban', 2: '/api/unban', 3: '/api/mute', 4: '/api/unmute' };
  const names = { 1: 'Ban', 2: 'Unban', 3: 'Mute', 4: 'Unmute' };
  const who = val('bid') || val('bname');
  const hours = num('bhours');
  const forever = (which === 1 || which === 3) && !hours;
  ask(names[which], `${names[which]} ${who}${forever ? ' permanently' : (hours ? ' for ' + hours + ' h' : '')}?`,
    async () => {
      const body = { reason: val('breason'), hours: hours };
      if (val('bid')) body.id = num('bid'); else body.name = val('bname');
      const r = await api('POST', paths[which], body);
      say('bresult', r);
      if (r.status === 200) loadRestrictions();
    });
}

// ---- announces ------------------------------------------------------------
function askAnnounceNow() {
  const text = val('annow');
  if (!text) return;
  ask('Announce now', `Send to everyone in world: ${text}`, async () => {
    say('anresult', await api('POST', '/api/announce', { text: text }));
  });
}
async function scheduleAnnounce() {
  const text = val('anlater');
  if (!text) return;
  const r = await api('POST', '/api/announce-schedule', {
    text: text, startAt: localUnix('anstart'), endAt: localUnix('anend'), intervalSec: num('aninterval')
  });
  if (r.status !== 200) { say('anresult', r); return; }
  loadAnnounces();
}
async function loadAnnounces() {
  const r = await api('GET', '/api/announces');
  if (r.status !== 200) { say('anresult', r); return; }
  el('anresult').innerHTML = `<h3>Scheduled</h3>` + table([
    { label: 'id', num: true, cell: a => esc(a.id) },
    { label: 'text', cell: a => esc(a.text) },
    { label: 'start', cell: a => when(a.startAt) },
    { label: 'end', cell: a => a.endAt ? when(a.endAt) : '-' },
    { label: 'every', num: true, cell: a => a.intervalSec ? a.intervalSec + 's' : 'once' },
    { label: 'last', cell: a => when(a.lastSent) },
    { label: '', cell: a => a.enabled ? `<span class='chip'>on</span>` : `<span class='chip off'>done</span>` },
    { label: '', cell: a => `<button class='danger' onclick='askDropAnnounce(${a.id})'>Remove</button>` },
  ], r.data.announces);
}
function askDropAnnounce(id) {
  ask('Remove announce', `Delete scheduled announce ${id}?`, async () => {
    await api('POST', '/api/announce-delete', { id: id });
    loadAnnounces();
  });
}

// ---- grants ---------------------------------------------------------------
function grantTarget(body) {
  if (val('gid')) body.id = num('gid'); else body.name = val('gname');
  body.reason = val('greason');
  return body;
}
function askGrant(which) {
  const who = val('gid') || val('gname');
  if (which === 1) {
    ask('Set money', `Set ${who} money to ${val('gmoney')}?`, async () =>
      say('gresult', await api('POST', '/api/set-money', grantTarget({ money: num('gmoney') }))));
  } else if (which === 2) {
    ask('Set level', `Set ${who} to level ${val('glevel')}?`, async () =>
      say('gresult', await api('POST', '/api/set-level', grantTarget({ level: num('glevel') }))));
  } else if (which === 3) {
    ask('Give item', `Give ${who} ${val('gamt') || 1} x ${val('gtpl')}?`, async () =>
      say('gresult', await api('POST', '/api/give-item',
        grantTarget({ templateId: num('gtpl'), amount: num('gamt') || 1 }))));
  } else {
    ask('Set GM level', `Set account ${val('gacct')} to admin level ${val('glvl')}?`, async () =>
      say('gresult', await api('POST', '/api/gm-level',
        { accountId: num('gacct'), level: num('glvl'), reason: val('greason') })));
  }
}

// ---- deleted --------------------------------------------------------------
async function loadDeleted() {
  const r = await api('GET', '/api/deleted');
  if (r.status !== 200) { say('dresult', r); return; }
  el('dresult').innerHTML = table([
    { label: 'id', num: true, cell: c => esc(c.id) },
    { label: 'name', cell: c => esc(c.name) },
    { label: 'lvl', num: true, cell: c => esc(c.level) },
    { label: 'account', num: true, cell: c => esc(c.accountId) },
    { label: '', cell: c => `<button onclick='askRestore(${c.id})'>Restore</button>` },
  ], r.data.deleted);
}
function askRestore(id) {
  ask('Restore character', `Undo the delete on ${id}? Its items come back too.`, async () => {
    await api('POST', '/api/restore-character', { id: id, reason: val('dreason') });
    loadDeleted();
  });
}

// ---- audit ----------------------------------------------------------------
// ---- logs: two panels in one tab (T116) -----------------------------------
function logTab(i) {
  el('lt-game').classList.toggle('on', i === 0);
  el('lt-admin').classList.toggle('on', i === 1);
  el('lp-game').style.display = i === 0 ? '' : 'none';
  el('lp-admin').style.display = i === 1 ? '' : 'none';
  if (i === 1) loadAudit();
}

let glPageNo = 0, glTotal = 0;
function glPage(d) {
  const size = num('gl_size') || 50;
  const next = glPageNo + d;
  if (next < 0 || next * size >= glTotal) return;
  loadGameLog(next);
}

async function loadGameLog(page) {
  glPageNo = page || 0;
  const size = num('gl_size') || 50;
  const p = new URLSearchParams();
  if (val('gl_who')) p.set('who', val('gl_who'));
  if (val('gl_cat')) p.set('category', val('gl_cat'));
  if (val('gl_act')) p.set('action', val('gl_act'));
  if (localUnix('gl_from')) p.set('from', localUnix('gl_from'));
  if (localUnix('gl_to')) p.set('to', localUnix('gl_to'));
  p.set('page', glPageNo);
  p.set('size', size);

  const r = await api('GET', '/api/game-log?' + p.toString());
  if (r.status !== 200) { glTotal = 0; say('glresult', r); return; }
  glTotal = r.data.total;

  const sel = el('gl_cat');
  if (sel.options.length <= 1 && r.data.categories) {
    r.data.categories.forEach(c => {
      const o = document.createElement('option');
      o.value = c; o.textContent = c; sel.appendChild(o);
    });
  }

  const first = glTotal ? glPageNo * size + 1 : 0;
  const last = Math.min(glTotal, (glPageNo + 1) * size);
  el('gl_note').textContent = glTotal
    ? `${first}-${last} of ${glTotal}` + (r.data.who ? ` for ${r.data.who}` : '')
    : 'no rows match';

  el('glresult').innerHTML = table([
    { label: 'when', cell: e => when(e.at) },
    { label: 'category', cell: e => `<span class='chip'>${esc(e.category)}</span>` },
    { label: 'action', cell: e => esc(e.action) },
    { label: 'actor', cell: e => e.actor ? `${esc(e.actor)} <span class='note'>${e.characterId}</span>`
        : (e.characterId ? String(e.characterId) : '-') },
    { label: 'target', cell: e => e.target ? `${esc(e.target)} <span class='note'>${e.targetId}</span>`
        : (e.targetId ? String(e.targetId) : '-') },
    { label: 'item', cell: e => e.item.templateId ? itemLabel(e.item) : '-' },
    { label: 'amount', num: true, cell: e => e.amount || '-' },
    { label: 'money', num: true, cell: e => e.money || '-' },
    { label: 'extra', cell: e => e.extra ? `<span class='note'>${esc(e.extra)}</span>` : '-' },
  ], r.data.log);
}

async function loadAudit() {
  const r = await api('GET', '/api/admin-log?limit=200');
  if (r.status !== 200) { say('auresult', r); return; }
  el('auresult').innerHTML = table([
    { label: 'when', cell: e => when(e.at) },
    { label: 'action', cell: e => esc(e.action) },
    { label: 'target', cell: e => esc(e.target) },
    { label: 'reason', cell: e => esc(e.reason) },
    { label: 'from', cell: e => esc(e.sourceIp) },
    { label: 'result', num: true, cell: e => e.result === 0
        ? `<span class='chip'>ok</span>` : `<span class='chip off'>${esc(e.result)}</span>` },
  ], r.data.log);
}

tick();
</script>
</body>
</html>
";
}
