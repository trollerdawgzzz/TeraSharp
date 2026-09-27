// SPDX-License-Identifier: MIT
// Copyright (c) 2026 the TeraSharp contributors

// T206 - Accounts. Replaces the Account/Default.aspx search plus Users/GMInfo.aspx and
// Log/LogAccount.aspx: retail split the lookup, the GM flag and the login log over three pages,
// so one account's characters, restrictions and logins are gathered on one screen here. The
// search keeps retail's "id or name, several at a time" box but drops its two mutually exclusive
// fields - the server decides which kind of term it was given.
import { get, write, h, card, stat, table, field, link, fmt, params, go } from './app.js';

export async function mount(root) {
  const p = params();
  const reload = () => mount(root);
  const q = field('AccountDBID or AccountName - comma or space separated',
    { value: p.q || '', placeholder: '1001, 1002   or   gm_kim' });

  const parts = [
    h('h1', { class: 'screen' }, 'Accounts'),
    card('Find an account', h('form', {
      class: 'row',
      onsubmit: e => { e.preventDefault(); go('accounts', { q: q.input.value.trim() }); },
    }, h('div', { class: 'grow' }, q.node), h('button', { class: 'primary' }, 'Search'))),
  ];

  if (p.q) parts.push(await results(p));
  if (p.id || p.name) parts.push(...await detail(p, reload));
  root.replaceChildren(...parts);
}

/** The hit list. `Open` carries the search along so the list survives the drill-in. */
async function results(p) {
  const { status, data } = await get('/api/accounts', { q: p.q });
  if (status !== 200) return h('p', { class: 'error' }, data?.message || 'the search failed');
  const rows = data.accounts || [];
  return card(rows.length + ' account(s) matched' + (rows.length === 50 ? ' (capped at 50)' : ''),
    table([
      { key: 'id', label: 'Id', num: true },
      { key: 'name', label: 'Name' },
      { key: 'adminLevel', label: 'GM level', num: true },
      { label: 'Characters', num: true, cell: a => String((a.characters || []).length) },
      { label: '', cell: a => link('Open', 'accounts', { q: p.q, id: a.id }) },
    ], rows, 'no account matched that term'));
}

/** Everything about one account: the card, its characters, its restrictions, its logins. */
async function detail(p, reload) {
  const key = { id: p.id, name: p.name };
  const [acc, hist] = await Promise.all([
    get('/api/account', key), get('/api/account-logins', key),
  ]);
  if (acc.status !== 200) {
    return [h('p', { class: 'error' }, acc.data?.message || 'no such account')];
  }
  const a = acc.data.account || {};
  const chars = acc.data.characters || [];

  // One hours box for the whole table: per-row inputs would mean twelve of them saying the same
  // thing, and a GM bans one character at a time anyway.
  const hours = field('Ban / mute hours - 0 means permanent', { type: 'number', min: '0', value: '0' });
  const gm = field('GM level', { type: 'number', min: '0', value: a.adminLevel ?? 0 });

  const run = async (path, body, ask) => {
    if (ask && !window.confirm(ask)) return;
    if (await write(path, body)) reload();
  };
  const btn = (text, cls, fn) => h('button', { class: cls, onclick: fn }, text);
  const hrs = () => Number(hours.input.value) || 0;

  return [
    h('h3', {}, 'Account ' + (a.name || p.name || p.id)),
    h('div', { class: 'cards' },
      stat('Account', a.name || '', 'id ' + (a.id ?? '')),
      stat('GM level', a.adminLevel ?? 0, a.adminLevel > 0 ? 'has GM powers' : 'ordinary player'),
      stat('Play time', fmt.duration(a.playTimeSec),
        (a.characterCount ?? chars.length) + ' character(s)'),
      stat('Last login', fmt.time(a.lastLogin) || 'never'),
      card('Set GM level', h('div', { class: 'row' },
        h('div', { class: 'grow' }, gm.node),
        btn('Apply', 'primary', () => run('/api/gm-level',
          { accountId: a.id ?? p.id, level: Number(gm.input.value) || 0 })))),
    ),

    h('h3', {}, 'Characters'),
    h('div', { class: 'row' }, h('div', { class: 'grow' }, hours.node),
      h('span', { class: 'muted' }, 'the Ban and Mute buttons below use this')),
    table([
      { label: 'Name', cell: c => link(c.name, 'characters', { id: c.id }) },
      { key: 'level', label: 'Level', num: true },
      { key: 'race', label: 'Race' },
      { key: 'class', label: 'Class' },
      { key: 'zone', label: 'Zone', num: true },
      { label: 'Last login', cell: c => fmt.time(c.lastLogin) },
      { label: 'Actions', cell: c => h('div', { class: 'row' },
        btn('Kick', null, () => run('/api/kick', { id: c.id })),
        btn('Ban', 'danger', () => run('/api/ban', { id: c.id, hours: hrs() })),
        btn('Unban', null, () => run('/api/unban', { id: c.id, hours: 0 },
          'Lift the ban on ' + c.name + ' (id ' + c.id + ')?')),
        btn('Mute', 'danger', () => run('/api/mute', { id: c.id, hours: hrs() })),
        btn('Unmute', null, () => run('/api/unmute', { id: c.id, hours: 0 },
          'Lift the mute on ' + c.name + ' (id ' + c.id + ')?'))) },
    ], chars, 'this account has no characters'),

    h('h3', {}, 'Restrictions'),
    table([
      { label: 'Character', cell: b => b.character
        ? link(b.character, 'characters', { id: b.characterId }) : String(b.characterId ?? '') },
      { key: 'typeName', label: 'Type' },
      { key: 'level', label: 'Level', num: true },
      { label: 'Until', cell: b => b.until ? fmt.time(b.until) : 'permanent' },
      { label: 'Active', cell: b => h('span', { class: 'pill ' + (b.active ? 'bad' : 'ok') },
        b.active ? 'active' : 'expired') },
      { key: 'reason', label: 'Reason', wrapped: true },
      { label: 'Set at', cell: b => fmt.time(b.setAt) },
    ], acc.data.bans, 'no ban or mute on record'),

    h('h3', {}, 'Sessions by character'),
    table([
      { label: 'Character', cell: c => link(c.name, 'characters', { id: c.id }) },
      { key: 'level', label: 'Level', num: true },
      { label: 'Last login', cell: c => fmt.time(c.lastLogin) },
      { label: 'Last logout', cell: c => fmt.time(c.lastLogout) },
    ], hist.data?.characters,
      hist.status === 200 ? 'no session on record' : 'the login history failed to load'),

    h('h3', {}, 'Login log'),
    table([
      { label: 'At', cell: r => fmt.time(r.at) },
      { label: 'Character', cell: r => r.character
        ? link(r.character, 'characters', { id: r.characterId }) : '' },
      { key: 'characterId', label: 'Id', num: true },
      { key: 'extra', label: 'Detail', wrapped: true },
    ], hist.data?.logins, 'nothing logged for this account'),
  ];
}
