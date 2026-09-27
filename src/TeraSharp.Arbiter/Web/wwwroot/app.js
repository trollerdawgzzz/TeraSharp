// SPDX-License-Identifier: MIT
// Copyright (c) 2026 the TeraSharp contributors

// =============================================================================================
// T206 - the admin UI's core: token handling, the fetch wrapper, the DOM helpers every screen
// uses, and the hash router. Plain ES modules; no framework, no build step, no bundler.
//
// The token lives in sessionStorage, so it dies with the browser tab. localStorage (what T106
// used) survives a closed browser, which for a GM token on a shared machine is a real exposure
// for no real gain - signing in again costs one paste.
//
// T206b. Two rules here exist because breaking either one made the tool reject a token the API
// accepts. (1) NOTHING calls the API while nobody is signed in - not even the header poll - so an
// empty token can never produce a 401. (2) A 401 only signs the operator out if it belongs to the
// CURRENT session: every sign-in and sign-out bumps `generation`, and a reply issued under an
// older one is discarded. Without that, a poll already in flight when the form was submitted came
// back 401 a moment later and threw the operator straight back to the login screen, saying the
// token had been refused when it had in fact just been accepted.
// =============================================================================================
import * as dashboard from './view-dashboard.js';
import * as accounts from './view-accounts.js';
import * as characters from './view-characters.js';
import * as mail from './view-mail.js';
import * as guilds from './view-guilds.js';
import * as server from './view-server.js';
import * as settings from './view-settings.js';

const KEY = 'ts_admin_token';
const HEALTH_MS = 5000;
let token = null;
/// Bumped on every sign-in and sign-out. Replies carry the generation they were issued under.
let generation = 0;
let healthTimer = null;

export const SCREENS = [
  ['dashboard', 'Dashboard', dashboard],
  ['accounts', 'Accounts', accounts],
  ['characters', 'Characters', characters],
  ['mail', 'Mail', mail],
  ['guilds', 'Guilds', guilds],
  ['server', 'Server', server],
  ['settings', 'Settings', settings],
];

// ------------------------------------------------------------------------------- the transport

/**
 * One API call. Returns { status, data } and never throws for an HTTP error - a screen decides
 * what a 404 means for it. A 401 signs out, because the only way to get one is a token that has
 * stopped being right.
 */
export async function api(method, path, body) {
  // Nobody is signed in: answer without touching the network. Sending an empty token would earn a
  // real 401, and treating that as "the token was refused" is what used to eat a good sign-in.
  if (!token) return { status: 401, data: { message: 'not signed in' } };

  const issued = generation;
  const init = { method, headers: { 'X-Admin-Token': token } };
  if (body !== undefined) {
    init.headers['Content-Type'] = 'application/json';
    init.body = JSON.stringify(body);
  }
  let res, data = null;
  try {
    res = await fetch(path, init);
  } catch (e) {
    return { status: 0, data: { message: 'the server did not answer: ' + e.message } };
  }
  const text = await res.text();
  try { data = text ? JSON.parse(text) : null; } catch { data = { message: text }; }
  // Only the session that made this call may be ended by its answer.
  if (res.status === 401 && issued === generation && token !== null) {
    signOut('the token was refused');
  }
  return { status: res.status, data };
}

/** GET a path with a query object, dropping empty values. */
export function get(path, query) {
  const q = new URLSearchParams();
  for (const [k, v] of Object.entries(query || {})) {
    if (v !== undefined && v !== null && String(v).length > 0) q.set(k, String(v));
  }
  const s = q.toString();
  return api('GET', s ? path + '?' + s : path);
}

/**
 * A write, with the audit reason the server records. Every mutating endpoint takes `reason`;
 * this asks for it once, so no screen can forget, and a cancelled prompt cancels the write.
 */
export async function write(path, body, promptText) {
  const reason = window.prompt(promptText || 'Reason for the audit log:', '');
  if (reason === null) return null;
  const res = await api('POST', path, Object.assign({ reason }, body));
  if (res.status === 200 && (res.data?.result ?? 0) === 0) {
    toast(res.data.message || 'done', 'ok');
  } else if (res.status !== 401) {
    toast((res.data?.message || 'failed') + ' (' + res.status + ')', 'bad');
  }
  return res;
}

// ------------------------------------------------------------------------------- DOM helpers

/** h('div', {class:'card'}, 'text', child) - attributes, then children. */
export function h(tag, attrs, ...kids) {
  const node = document.createElement(tag);
  for (const [k, v] of Object.entries(attrs || {})) {
    if (v === null || v === undefined || v === false) continue;
    if (k === 'class') node.className = v;
    else if (k === 'html') node.innerHTML = v;
    else if (k.startsWith('on')) node.addEventListener(k.slice(2), v);
    else if (v === true) node.setAttribute(k, '');
    else node.setAttribute(k, String(v));
  }
  add(node, kids);
  return node;
}

function add(node, kids) {
  for (const kid of kids) {
    if (kid === null || kid === undefined || kid === false) continue;
    if (Array.isArray(kid)) add(node, kid);
    else node.append(kid instanceof Node ? kid : document.createTextNode(String(kid)));
  }
}

/** A titled panel. */
export function card(title, ...body) {
  return h('div', { class: 'card' }, title ? h('h2', {}, title) : null, ...body);
}

/** A big number with its caption. */
export function stat(title, value, note) {
  return card(title, h('div', { class: 'stat' }, value), note ? h('div', { class: 'muted' }, note) : null);
}

/**
 * A table. `cols` is [{ key, label, num, cell }]; `cell(row)` may return a node. An empty rows
 * array renders the empty note rather than a headed table with nothing in it.
 */
export function table(cols, rows, emptyNote) {
  if (!rows || rows.length === 0) return h('div', { class: 'empty' }, emptyNote || 'nothing here');
  const head = h('tr', {}, cols.map(c => h('th', { class: c.num ? 'num' : null }, c.label)));
  const body = rows.map(r => h('tr', {}, cols.map(c => h('td',
    { class: [c.num ? 'num' : '', c.wrapped ? 'wrapped' : ''].join(' ').trim() || null },
    c.cell ? c.cell(r) : fmtValue(r[c.key])))));
  return h('div', { class: 'scroll' }, h('table', {}, h('thead', {}, head), h('tbody', {}, body)));
}

function fmtValue(v) {
  if (v === null || v === undefined) return '';
  if (v === true) return 'yes';
  if (v === false) return 'no';
  return String(v);
}

/** A labelled input, returned with the element so a caller can read it back. */
export function field(label, attrs) {
  const input = h(attrs?.tag === 'textarea' ? 'textarea' : attrs?.tag === 'select' ? 'select' : 'input',
    Object.assign({}, attrs, { tag: undefined }));
  return { input, node: h('label', {}, label, input) };
}

/** A clickable that navigates to a screen with a query, e.g. link('name', 'characters', {id:7}). */
export function link(text, screen, query) {
  const q = new URLSearchParams(query || {}).toString();
  return h('a', { href: '#/' + screen + (q ? '?' + q : '') }, text);
}

export function toast(message, kind) {
  const node = h('div', { class: 'toast ' + (kind || '') }, message);
  document.getElementById('toasts').append(node);
  setTimeout(() => node.remove(), 6000);
}

// ------------------------------------------------------------------------------- formatting

export const fmt = {
  num: n => (n === null || n === undefined || n === '') ? '' : Number(n).toLocaleString(),
  bytes(n) {
    n = Number(n) || 0;
    const u = ['B', 'KB', 'MB', 'GB', 'TB'];
    let i = 0;
    while (n >= 1024 && i < u.length - 1) { n /= 1024; i++; }
    return (i === 0 ? n : n.toFixed(1)) + ' ' + u[i];
  },
  /** A unix second, or an ISO string, as local time. Empty stays empty. */
  time(v) {
    if (v === null || v === undefined || v === '' || v === 0) return '';
    const d = typeof v === 'number' ? new Date(v * 1000) : new Date(v);
    return isNaN(d) ? String(v) : d.toLocaleString();
  },
  /** A duration in seconds as 3d 04:05. */
  duration(seconds) {
    seconds = Math.max(0, Math.floor(Number(seconds) || 0));
    const d = Math.floor(seconds / 86400), h2 = Math.floor(seconds % 86400 / 3600);
    const m = Math.floor(seconds % 3600 / 60);
    const pad = x => String(x).padStart(2, '0');
    return (d > 0 ? d + 'd ' : '') + pad(h2) + ':' + pad(m);
  },
  ago(unixSeconds) {
    if (!unixSeconds) return '';
    const s = Math.max(0, Math.floor(Date.now() / 1000 - unixSeconds));
    if (s < 60) return s + 's ago';
    if (s < 3600) return Math.floor(s / 60) + 'm ago';
    if (s < 86400) return Math.floor(s / 3600) + 'h ago';
    return Math.floor(s / 86400) + 'd ago';
  },
};

// ------------------------------------------------------------------------------- the router

/** The query of the current hash, as a plain object. */
export function params() {
  const at = location.hash.indexOf('?');
  return Object.fromEntries(new URLSearchParams(at < 0 ? '' : location.hash.slice(at + 1)));
}

/** Go to a screen. */
export function go(screen, query) {
  const q = new URLSearchParams(query || {}).toString();
  location.hash = '#/' + screen + (q ? '?' + q : '');
}

function currentScreen() {
  const path = location.hash.replace(/^#\/?/, '').split('?')[0];
  return SCREENS.find(s => s[0] === path) || SCREENS[0];
}

let mounted = null;

async function route() {
  const [name, , module] = currentScreen();
  for (const a of document.querySelectorAll('#tabs a')) {
    if (a.dataset.screen === name) a.setAttribute('aria-current', 'page');
    else a.removeAttribute('aria-current');
  }
  const screen = document.getElementById('screen');
  if (mounted && mounted !== name) screen.replaceChildren();
  mounted = name;
  try {
    await module.mount(screen);
  } catch (e) {
    screen.replaceChildren(h('p', { class: 'error' }, 'this screen failed to render: ' + e.message));
  }
}

// ------------------------------------------------------------------------------- session

function signIn(value) {
  token = value;
  generation++;
  try { sessionStorage.setItem(KEY, value); } catch { /* private mode: keep it in memory only */ }
  const error = document.getElementById('login-error');
  error.hidden = true;
  error.textContent = '';
  document.getElementById('login').hidden = true;
  document.getElementById('app').hidden = false;
  // Setting the hash fires hashchange, which routes. Only route here when it did not change.
  if (!location.hash) location.hash = '#/dashboard'; else route();
  startHealth();
}

export function signOut(why) {
  token = null;
  generation++;
  stopHealth();
  try { sessionStorage.removeItem(KEY); } catch { }
  document.getElementById('app').hidden = true;
  const login = document.getElementById('login');
  login.hidden = false;
  const error = document.getElementById('login-error');
  error.hidden = !why;
  error.textContent = why || '';
}

/** Poll the header badge, but only while signed in. */
function startHealth() {
  health();
  if (healthTimer === null) healthTimer = setInterval(health, HEALTH_MS);
}

function stopHealth() {
  if (healthTimer !== null) { clearInterval(healthTimer); healthTimer = null; }
}

/** The header badge: world links and how many people are on. Silent while signed out. */
async function health() {
  if (!token) return;
  const pill = document.getElementById('health');
  const { status, data } = await api('GET', '/api/status');
  if (status !== 200 || !data) { pill.textContent = 'unreachable'; pill.className = 'pill bad'; return; }
  pill.textContent = data.worldLinks + ' world' + (data.worldLinks === 1 ? '' : 's')
    + ' / ' + data.online + ' online / up ' + fmt.duration(data.uptimeSeconds);
  pill.className = 'pill ' + (data.worldReady ? 'ok' : 'bad');
}

function start() {
  const tabs = document.getElementById('tabs');
  tabs.replaceChildren(...SCREENS.map(([name, label]) =>
    h('a', { href: '#/' + name, 'data-screen': name }, label)));
  document.getElementById('sign-out').addEventListener('click', () => signOut(null));
  document.getElementById('login-form').addEventListener('submit', e => {
    e.preventDefault();
    const value = document.getElementById('login-token').value.trim();
    if (value.length === 0) return;
    signIn(value);
  });
  window.addEventListener('hashchange', route);
  // No poll is armed here on purpose: startHealth() runs on sign-in and stopHealth() on sign-out,
  // so the tool makes no API call at all until someone has a token.

  let saved = null;
  try { saved = sessionStorage.getItem(KEY); } catch { }
  if (saved) signIn(saved); else signOut(null);
}

start();
