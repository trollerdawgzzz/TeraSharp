// T206b - drive the admin UI's real sign-in path from node, against a running Arbiter.
//
// WHY THIS EXISTS. T206b was a bug the C# tests could not see: the API accepted the token
// (Invoke-RestMethod proved it) and every endpoint answered, but the sign-in form said "the token
// was refused". The fault was app.js's lifecycle - it armed the header poll at page load, so while
// nobody was signed in it called /api/status with an empty token, earned a real 401, and read that
// as a refusal. Nothing on the server was wrong, so nothing on the server could fail a test.
//
// This loads the SHIPPED wwwroot into node behind the smallest DOM it needs and drives the actual
// sign-in path against a real listener. Everything under test is still app.js's own code: the
// fetch wrapper, the 401 handling, the timer lifecycle and the router.
//
//   node tools/admin-ui-probe.mjs --token <TERASHARP_ADMIN_TOKEN> [--url http://127.0.0.1:8051]
//                                [--root src/TeraSharp.Arbiter/Web/wwwroot]
//
// Requires node 18 or newer (for global fetch) and the Arbiter running. Exit 0 means the sign-in
// path is sound; anything else prints which step failed. This is a manual probe, not part of
// `dotnet run --project src/TeraSharp.Arbiter.Tests` - the suite must not need node installed.

import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';

// ---------------------------------------------------------------------------- arguments

const args = new Map();
for (let i = 2; i < process.argv.length; i += 2) args.set(process.argv[i], process.argv[i + 1]);
const BASE = (args.get('--url') || 'http://127.0.0.1:8051').replace(/\/$/, '');
const ROOT = resolve(args.get('--root') || 'src/TeraSharp.Arbiter/Web/wwwroot');
const TOKEN = args.get('--token');
if (!TOKEN) {
  console.error('usage: node tools/admin-ui-probe.mjs --token <token> [--url ...] [--root ...]');
  process.exit(2);
}

// ---------------------------------------------------------------------------- the DOM shim
//
// Only what app.js and the view modules actually touch. Nothing here decides anything app.js
// would otherwise decide - setInterval is the one exception, and it is captured rather than
// stubbed so the probe can fire a poll on demand instead of waiting five seconds for one.

class N {
  constructor(tag) {
    this.tagName = (tag || '').toUpperCase(); this.children = []; this.attrs = {};
    this.listeners = {}; this.text = ''; this.className = ''; this.dataset = {};
    this.hidden = false; this.value = ''; this.checked = false;
  }
  append(...kids) { for (const k of kids) this.children.push(k); }
  replaceChildren(...kids) { this.children = kids.filter(Boolean); }
  remove() { }
  addEventListener(name, fn) { (this.listeners[name] ||= []).push(fn); }
  setAttribute(k, v) {
    this.attrs[k] = String(v);
    if (k.startsWith('data-')) this.dataset[k.slice(5)] = String(v);
  }
  removeAttribute(k) { delete this.attrs[k]; }
  getAttribute(k) { return this.attrs[k] ?? null; }
  set innerHTML(v) { this.text = v; }
  set textContent(v) { this.text = v; this.children = []; }
  get textContent() { return this.text; }
  fire(name, event) { for (const fn of this.listeners[name] || []) fn(event || { preventDefault() { } }); }
}

const byId = new Map();
for (const m of readFileSync(ROOT + '/index.html', 'utf8').matchAll(/id="([a-z-]+)"/g))
  byId.set(m[1], new N('div'));

const timers = [];
const sessionValues = new Map();
const win = {
  listeners: {},
  addEventListener(n, f) { (this.listeners[n] ||= []).push(f); },
  fire(n) { for (const f of this.listeners[n] || []) f(); },
  prompt: () => 'admin-ui-probe', confirm: () => false,
};
const loc = {
  _hash: '',
  get hash() { return this._hash; },
  set hash(v) { if (v === this._hash) return; this._hash = v; win.fire('hashchange'); },
};

globalThis.Node = N;
globalThis.document = {
  createElement: tag => new N(tag),
  createTextNode: t => { const n = new N('#text'); n.text = String(t); return n; },
  getElementById: id => byId.get(id) ?? null,
  querySelectorAll: () => (byId.get('tabs')?.children ?? []),
};
// setTimeout is left alone: node's fetch is built on it, and app.js only uses it to retire a toast.
globalThis.setInterval = (fn) => { timers.push({ fn }); return timers.length; };
globalThis.clearInterval = id => { if (timers[id - 1]) timers[id - 1].fn = null; };
globalThis.sessionStorage = {
  getItem: k => (sessionValues.has(k) ? sessionValues.get(k) : null),
  setItem: (k, v) => sessionValues.set(k, String(v)),
  removeItem: k => sessionValues.delete(k),
};
globalThis.localStorage = {
  getItem() { throw new Error('app.js must not touch localStorage - the token belongs to the tab'); },
  setItem() { throw new Error('app.js must not touch localStorage - the token belongs to the tab'); },
};
globalThis.location = loc;
globalThis.window = win;

// app.js uses relative URLs, which node's fetch cannot resolve on its own.
const wire = globalThis.fetch;
let calls = 0;
globalThis.fetch = (url, init) => { calls++; return wire(BASE + url, init); };

// ---------------------------------------------------------------------------- the probe

const app = await import(ROOT + '/app.js');

const armed = () => timers.filter(t => t.fn).length;
const signedIn = () => byId.get('app').hidden === false;
const error = () => byId.get('login-error').text || '';
const settle = async () => { for (let i = 0; i < 4; i++) await new Promise(r => setImmediate(r)); };
const poll = async () => { for (const t of timers) if (t.fn) await t.fn(); };
function signIn(value) {
  byId.get('login-token').value = value;
  byId.get('login-form').fire('submit');
}

let failed = 0;
function check(what, ok, detail) {
  if (!ok) failed++;
  console.log('%s  %s%s', ok ? ' ok ' : 'FAIL', what, detail ? '   (' + detail + ')' : '');
}

// 1. A page nobody has signed in on must be completely silent. This is the defect: a poll armed
//    at load called /api/status with an empty token and read the 401 as a refusal.
check('nothing is polling before sign-in', armed() === 0, armed() + ' timer(s) armed');
check('no API call on load', calls === 0, calls + ' call(s) made');
await poll(); await settle();
check('an idle login page shows no error', error() === '', error());

// 2. The token the API accepts signs in.
signIn(TOKEN);
await settle();
check('the API token signs in', signedIn(), 'error: ' + error());
check('sign-in left no stale error', error() === '');
check('the header poll is now armed', armed() === 1, armed() + ' timer(s)');

// 3. The race that made this reproducible: a poll in flight when the form is submitted. Its 401
//    used to land after the sign-in had succeeded and throw the operator back out.
app.signOut(null);
sessionValues.clear();
const inFlight = poll();
signIn(TOKEN);
await inFlight;
await settle();
check('a sign-in survives a reply from the previous session', signedIn(), 'error: ' + error());

// 4. Real authenticated reads, through the UI's own wrapper.
for (const path of ['/api/status', '/api/online', '/api/db', '/api/queue']) {
  const r = await app.api('GET', path);
  check('wrapper GET ' + path, r.status === 200, 'status ' + r.status
    + (r.data && r.data.message ? ': ' + r.data.message : ''));
}

// 5. A token the server really does refuse must still sign the operator out, or the fix would have
//    turned a refusal into a hang.
app.signOut(null);
sessionValues.clear();
signIn(TOKEN + '-wrong');
await settle();
check('a bad token is still refused', !signedIn() && error() === 'the token was refused', error());

console.log('');
console.log(failed === 0 ? 'admin-ui-probe: PASS' : 'admin-ui-probe: ' + failed + ' check(s) FAILED');
process.exit(failed === 0 ? 0 : 1);
