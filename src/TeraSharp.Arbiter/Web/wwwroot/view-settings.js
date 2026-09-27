// SPDX-License-Identifier: MIT
// Copyright (c) 2026 the TeraSharp contributors

// T206 - Settings. Retail had no Settings group at all; the nearest things were the server
// registry on Server/Default.aspx and the log-preset editor. This is an addition: it answers the
// question every support conversation opens with - what configuration did the running server
// actually resolve? - by showing the resolved value and the source of every setting, not the
// file on disk. Read-only: nothing here writes, because changing a setting means a restart.
import { get, h, card, stat, table, fmt } from './app.js';

export async function mount(root) {
  const [settings, db, status] = await Promise.all([
    get('/api/settings'), get('/api/db'), get('/api/status'),
  ]);

  const s = settings.data || {};
  const d = db.data || {};
  const st = status.data || {};
  const values = s.values || [];
  const names = s.itemNames || {};
  const ui = s.ui || {};
  const setCount = values.filter(v => v.set).length;

  root.replaceChildren(
    h('h1', { class: 'screen' }, 'Settings'),
    h('div', { class: 'cards' },
      stat('Config file', s.configFile || '(none - environment only)',
        values.length + ' setting(s) known'),
      stat('Settings set', fmt.num(setCount), 'of ' + values.length + ', the rest are defaults'),
      stat('Database', fmt.bytes(d.bytes),
        (d.tables?.length ?? 0) + ' tables, schema v' + (d.userVersion ?? 0)),
      stat('Item names', fmt.num(names.count ?? 0) + ' names',
        names.source || '(no sheet found)'),
    ),

    s.configProblem ? h('p', { class: 'error' }, 'teras.json problem: ' + s.configProblem) : null,

    h('h3', {}, 'Effective settings'),
    h('p', { class: 'muted' }, 'Secret values are masked by the server and never leave the '
      + 'process - what you see below is a length, not the value.'),
    table([
      {
        label: 'Name', cell: r => [r.name, r.secret ? ' ' : null,
          r.secret ? h('span', { class: 'pill' }, 'secret') : null],
      },
      { key: 'source', label: 'Source' },
      { key: 'set', label: 'Set' },
      { key: 'value', label: 'Value', wrapped: true },
    ], values, 'the server reported no settings'),

    h('h3', {}, 'Startup report'),
    card(null, h('pre', { class: 'tail' }, (s.describe || []).join('\n')
      || 'the server logged no startup lines')),

    h('h3', {}, 'Runtime'),
    table([{ key: 'k', label: 'Field' }, { key: 'v', label: 'Value', wrapped: true }], [
      { k: 'datasheet directory', v: s.datasheetDir || '(none)' },
      { k: 'embedded UI', v: (ui.files ?? 0) + ' file(s), build ' + (ui.build || '?') },
      { k: 'log file', v: st.logFile || '(ring buffer only)' },
      { k: 'console level', v: st.consoleLevel || '' },
      { k: 'started at', v: fmt.time(st.startedAt) },
      { k: 'uptime', v: fmt.duration(st.uptimeSeconds) },
    ]),

    h('h3', {}, 'Database tables'),
    table([
      { key: 'name', label: 'Table' },
      { key: 'rows', label: 'Rows', num: true, cell: r => fmt.num(r.rows) },
    ], [...(d.tables || [])].sort((a, b) => (b.rows ?? 0) - (a.rows ?? 0)),
      'the database reported no tables'),
  );
}
