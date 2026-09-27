// SPDX-License-Identifier: MIT
// Copyright (c) 2026 the TeraSharp contributors

// T206 - Dashboard. The retail equivalent is Server/ServerMonitor.aspx plus ChannelMonitor:
// running status, concurrent users, queue depth, and the per-subsystem state. Retail had no
// uptime figure and no list of who is actually on, so both are additions rather than ports.
import { get, h, card, stat, table, fmt, link } from './app.js';

export async function mount(root) {
  const [status, online, db, queue] = await Promise.all([
    get('/api/status'), get('/api/online'), get('/api/db'), get('/api/queue'),
  ]);

  const s = status.data || {};
  const rows = online.data?.online || [];
  const d = db.data || {};
  const q = queue.data || {};

  root.replaceChildren(
    h('h1', { class: 'screen' }, 'Dashboard'),
    h('div', { class: 'cards' },
      stat('World links', s.worldLinks ?? '?', s.worldReady ? 'ready' : 'NOT ready'),
      stat('Online', s.online ?? rows.length, rows.length + ' session(s) listed'),
      stat('Uptime', fmt.duration(s.uptimeSeconds), 'since ' + fmt.time(s.startedAt)),
      stat('Database', fmt.bytes(d.bytes),
        (d.tables?.length ?? 0) + ' tables, schema v' + (d.userVersion ?? 0)
        + (d.walBytes ? ', WAL ' + fmt.bytes(d.walBytes) : '')),
      stat('Matchmaking', (q.queued ?? 0) + ' queued', (q.parties ?? 0) + ' live part(ies)'),
      stat('Memory', fmt.bytes(s.managedBytes), 'working set ' + fmt.bytes(s.workingSetBytes)
        + ', ' + (s.threads ?? 0) + ' threads'),
    ),

    h('h3', {}, 'Online players'),
    table([
      { label: 'Character', cell: r => link(r.name, 'characters', { name: r.name }) },
      { key: 'playerId', label: 'Id', num: true },
      { key: 'account', label: 'Account', cell: r => link(r.account, 'accounts', { q: r.account }) },
      { key: 'level', label: 'Level', num: true },
      { key: 'zone', label: 'Zone', num: true },
    ], rows, 'nobody is in world'),

    h('h3', {}, 'Match queue'),
    table([
      { key: 'leader', label: 'Leader', num: true },
      { key: 'size', label: 'Bodies', num: true },
      { key: 'isParty', label: 'Party' },
      { key: 'state', label: 'State' },
      { label: 'Wants', cell: r => (r.instances || []).join(', ') },
      { label: 'Waiting', cell: r => fmt.duration(r.waitSeconds) },
      { key: 'matchedInstanceId', label: 'Matched', num: true },
    ], q.queues, 'the pool is empty'),

    h('h3', {}, 'Process'),
    table([{ key: 'k', label: 'Field' }, { key: 'v', label: 'Value', wrapped: true }], [
      { k: 'log file', v: s.logFile || '(ring buffer only)' },
      { k: 'console level', v: s.consoleLevel || '' },
      { k: 'gc gen0 / gen2', v: (s.gc0 ?? 0) + ' / ' + (s.gc2 ?? 0) },
      { k: 'database file', v: d.path || '' },
    ]),
  );
}
