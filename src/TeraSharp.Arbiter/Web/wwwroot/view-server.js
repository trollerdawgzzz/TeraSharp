// SPDX-License-Identifier: MIT
// Copyright (c) 2026 the TeraSharp contributors

// T206 - Server. One screen for what retail spread over Announce/IngameAnnounce.aspx (manual and
// scheduled broadcasts), Log/default.aspx and Log/SearchFromAuditLog.aspx (the game log search and
// who-did-what) and Server/AchievementSeason.aspx (server-first claims). The restart notice, the
// datasheet reload and the live log tail have no retail counterpart - they are additions.
import { get, write, h, table, field, link, toast, fmt, params } from './app.js';

// The log search keeps its filter and page here, so Next/Prev survive a re-render, and the tail's
// timer lives here so a second mount can kill the first one's interval instead of racing it.
let filter = { who: '', category: '', action: '', from: '', to: '', page: 0, size: 50 };
let tailTimer = null;

const unix = v => v ? Math.floor(new Date(v).getTime() / 1000) : 0;
const ok = res => !!res && res.status === 200 && (res.data?.result ?? 0) === 0;
const box = (legend, ...body) => h('fieldset', {}, h('legend', {}, legend), ...body);

export async function mount(root) {
  if (tailTimer) { clearInterval(tailTimer); tailTimer = null; }
  const who = params().who;
  if (who) filter = Object.assign({}, filter, { who, page: 0 });

  root.replaceChildren(
    h('h1', { class: 'screen' }, 'Server'),
    announceNow(), scheduled(), restartNotice(), datasheets(), serverFirsts(),
    gameLog(), adminLog(), logTail(),
  );
}

// ------------------------------------------------------------------------------- announcements

function announceNow() {
  const text = field('Message', { placeholder: 'goes to every character in world, once' });
  const send = async () => {
    const value = text.input.value.trim();
    if (!value) return toast('nothing to announce', 'bad');
    const res = await write('/api/announce', { text: value }, 'Reason for this announcement:');
    if (!ok(res)) return;
    toast('sent to ' + fmt.num(res.data.sent ?? 0) + ' player(s)', 'ok');
    text.input.value = '';
  };
  return box('Announce now', h('div', { class: 'row' },
    h('div', { class: 'grow' }, text.node),
    h('button', { class: 'primary', onclick: send }, 'Send')));
}

function scheduled() {
  const text = field('Message', { placeholder: 'repeated until the end time' });
  const start = field('Start at', { type: 'datetime-local' });
  const end = field('End at', { type: 'datetime-local' });
  const every = field('Interval (s)', { type: 'number', min: 1, value: 300 });
  const list = h('div', {});

  const load = async () => {
    const { data } = await get('/api/announces');
    list.replaceChildren(table([
      { key: 'id', label: 'Id', num: true },
      { key: 'text', label: 'Text', wrapped: true },
      { label: 'Starts', cell: r => fmt.time(r.startAt) },
      { label: 'Ends', cell: r => fmt.time(r.endAt) },
      { label: 'Last sent', cell: r => fmt.time(r.lastSent) },
      { key: 'intervalSec', label: 'Every (s)', num: true },
      { key: 'enabled', label: 'Enabled' },
      { key: 'createdBy', label: 'Added by' },
      { label: '', cell: r => h('button', { class: 'danger', onclick: async () => {
        if (!window.confirm('Delete scheduled announcement ' + r.id + ' ("' + r.text + '")?')) return;
        if (ok(await write('/api/announce-delete', { id: r.id }, 'Reason for deleting announcement ' + r.id + ':'))) load();
      } }, 'Delete') },
    ], data?.announces, 'nothing is scheduled'));
  };
  load();

  const add = async () => {
    const value = text.input.value.trim();
    if (!value) return toast('nothing to announce', 'bad');
    const body = { text: value, startAt: unix(start.input.value), endAt: unix(end.input.value), intervalSec: Number(every.input.value) || 0 };
    if (ok(await write('/api/announce-schedule', body, 'Reason for this scheduled announcement:'))) {
      text.input.value = '';
      load();
    }
  };

  return box('Scheduled announcements',
    h('p', { class: 'muted' }, 'A blank start time means now; a blank end time means it runs until it is deleted.'),
    h('div', { class: 'row' },
      h('div', { class: 'grow' }, text.node), start.node, end.node, every.node,
      h('button', { onclick: add }, 'Add')),
    list);
}

function restartNotice() {
  const minutes = field('Minutes from now', { type: 'number', min: 1, max: 720, value: 10 });
  const text = field('Message (optional)', { placeholder: '{0} is replaced by the minutes left, e.g. "Restarting in {0} minute(s) - please find a safe spot."' });
  const out = h('div', {});
  const send = async () => {
    const body = { minutes: Number(minutes.input.value) || 0, text: text.input.value.trim() };
    const res = await write('/api/restart-notice', body, 'Reason for the restart notice:');
    if (!ok(res)) return;
    out.replaceChildren(table([
      { key: 'minutes', label: 'At minutes left', num: true },
      { label: 'Goes out', cell: r => fmt.time(r.at) },
    ], res.data.scheduled, 'nothing was scheduled'));
  };
  return box('Restart notice',
    h('p', { class: 'muted' }, 'This only schedules the warning broadcasts - it does not stop, restart or lock the server. Shut the process down yourself once the last notice has gone out.'),
    h('div', { class: 'row' },
      minutes.node, h('div', { class: 'grow' }, text.node),
      h('button', { onclick: send }, 'Send')),
    out);
}

function datasheets() {
  const out = h('div', {});
  const reload = async () => {
    if (!window.confirm('Re-read every datasheet from disk now? A sheet a consumer has already cached only changes where that consumer re-reads it.')) return;
    const res = await write('/api/reload-datasheets', {}, 'Reason for reloading the datasheets:');
    if (!ok(res)) return;
    const d = res.data, sheets = d.sheets || [];
    out.replaceChildren(
      h('p', { class: 'muted' }, (d.directory || '(no directory)') + ' - '
        + fmt.num(d.fromSheet ?? 0) + ' of ' + fmt.num(sheets.length) + ' from sheet'),
      table([
        { key: 'sheet', label: 'Sheet' },
        { key: 'fromSheet', label: 'From sheet' },
        { key: 'entries', label: 'Entries', num: true },
        { key: 'consumer', label: 'Consumer', wrapped: true },
      ], sheets, 'no sheet was read'));
  };
  return box('Datasheets',
    h('div', { class: 'row' }, h('button', { onclick: reload }, 'Reload'),
      h('span', { class: 'muted' }, 'item names, drop tables and the rest of the static data')),
    out);
}

// ------------------------------------------------------------------------------- server firsts

function serverFirsts() {
  const out = h('div', {});
  const load = async () => {
    const { data } = await get('/api/server-achievements');
    const claims = data?.claims || [];
    const clear = (body, question) => async () => {
      if (!window.confirm(question)) return;
      if (ok(await write('/api/clear-server-achievement', body, 'Reason for clearing the server-first claim:'))) load();
    };
    out.replaceChildren(
      h('div', { class: 'row' },
        h('button', { class: 'danger', disabled: claims.length === 0,
          onclick: clear({ all: true }, 'Clear ALL ' + claims.length + ' server-first claim(s)? Every one of them becomes claimable again.') }, 'Clear all'),
        h('span', { class: 'muted' }, fmt.num(claims.length) + ' claim(s) on record')),
      table([
        { key: 'achievementId', label: 'Achievement', num: true },
        { key: 'ownerId', label: 'Owner id', num: true },
        { label: 'Owner', cell: r => r.ownerId ? link(r.name || String(r.ownerId), 'characters', { id: r.ownerId }) : (r.name || '') },
        { key: 'partyId', label: 'Party', num: true },
        { label: 'Claimed', cell: r => fmt.time(r.claimedAt) },
        { label: '', cell: r => h('button', { class: 'danger',
          onclick: clear({ id: r.achievementId }, 'Clear the server-first claim on achievement '
            + r.achievementId + ' (held by ' + (r.name || r.ownerId) + ')?') }, 'Clear') },
      ], claims, 'no server-first has been claimed'));
  };
  load();
  return box('Server-first achievements', out);
}

// ------------------------------------------------------------------------------- game log

function gameLog() {
  const who = field('Who (account or character, name or id)', { value: filter.who });
  const category = field('Category', { tag: 'select' });
  const action = field('Action', { value: filter.action, placeholder: 'exact action name' });
  const from = field('From', { type: 'datetime-local' });
  const to = field('To', { type: 'datetime-local' });
  const size = field('Page size', { type: 'number', min: 1, max: 500, value: filter.size });
  const note = h('span', { class: 'muted' });
  const out = h('div', {});

  const load = async () => {
    const { data } = await get('/api/game-log', filter);
    const cats = data?.categories || [];
    if (category.input.options.length !== cats.length + 1) {
      category.input.replaceChildren(h('option', { value: '' }, '(any category)'),
        ...cats.map(c => h('option', { value: c }, c)));
    }
    category.input.value = filter.category || '';
    note.textContent = fmt.num(data?.total ?? 0) + ' row(s), page ' + ((filter.page || 0) + 1);
    out.replaceChildren(table([
      { label: 'At', cell: r => fmt.time(r.at) },
      { key: 'category', label: 'Category' },
      { key: 'action', label: 'Action' },
      { label: 'Actor', cell: r => r.characterId ? link(r.actor || String(r.characterId), 'characters', { id: r.characterId }) : (r.actor || '') },
      { key: 'target', label: 'Target' },
      { label: 'Item', cell: r => r.item ? (r.item.name || String(r.item.templateId ?? '')) : '' },
      { key: 'amount', label: 'Amount', num: true },
      { label: 'Money', cell: r => fmt.num(r.money), num: true },
      { key: 'extra', label: 'Extra', wrapped: true },
    ], data?.log, 'nothing matched that filter'));
  };
  load();

  const search = () => {
    filter = {
      who: who.input.value.trim(), category: category.input.value, action: action.input.value.trim(),
      from: unix(from.input.value) || '', to: unix(to.input.value) || '',
      page: 0, size: Number(size.input.value) || 50,
    };
    load();
  };
  const step = by => () => { filter.page = Math.max(0, (filter.page || 0) + by); load(); };

  return box('Game log search',
    h('div', { class: 'row' },
      h('div', { class: 'grow' }, who.node), category.node, action.node, from.node, to.node, size.node,
      h('button', { class: 'primary', onclick: search }, 'Search')),
    h('div', { class: 'row' },
      h('button', { onclick: step(-1) }, 'Prev'),
      h('button', { onclick: step(1) }, 'Next'), note),
    out);
}

// ------------------------------------------------------------------------------- live log tail

// The tool's own audit trail. It sits beside the game log rather than on a tab of its own,
// because the question an operator asks is "what happened to this player", and the answer is in
// both: T206 also writes every admin write into game_log under the category admin.
function adminLog() {
  const body = h('div', {});
  const lines = field('Entries', { type: 'number', value: 100, min: 1, max: 500 });
  const load = async () => {
    const { status, data } = await get('/api/admin-log', { limit: lines.input.value });
    body.replaceChildren(status === 200
      ? table([
          { label: 'When', cell: r => fmt.time(r.at) },
          { key: 'action', label: 'Action' },
          { key: 'target', label: 'Target' },
          { key: 'reason', label: 'Reason', wrapped: true },
          { key: 'sourceIp', label: 'From' },
          { key: 'result', label: 'Result', num: true },
        ], data.log, 'no admin action has been recorded yet')
      : h('p', { class: 'error' }, data?.message || 'the audit log failed to load'));
  };
  load();
  return box('Admin audit log',
    h('div', { class: 'row' }, h('div', { class: 'grow' }, lines.node),
      h('button', { onclick: load }, 'Refresh')),
    body);
}

function logTail() {
  const lines = field('Lines', { type: 'number', min: 1, max: 2000, value: 200 });
  const auto = field('Auto (every 3s)', { type: 'checkbox' });
  const pre = h('pre', { class: 'tail' }, 'loading...');

  const load = async () => {
    const { data } = await get('/api/log', { lines: Number(lines.input.value) || 200 });
    pre.textContent = (data?.lines || []).join('\n') || '(the ring buffer is empty)';
    pre.scrollTop = pre.scrollHeight;
  };
  load();

  auto.input.addEventListener('change', () => {
    if (tailTimer) { clearInterval(tailTimer); tailTimer = null; }
    if (auto.input.checked) { tailTimer = setInterval(load, 3000); load(); }
  });

  return box('Live log tail',
    h('div', { class: 'row' }, lines.node, h('button', { onclick: load }, 'Refresh'), auto.node,
      h('span', { class: 'muted' }, 'oldest line first; the ring buffer holds what the process has said since it started')),
    pre);
}
