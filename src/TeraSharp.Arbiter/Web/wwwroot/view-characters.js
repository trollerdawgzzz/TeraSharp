// SPDX-License-Identifier: MIT
// Copyright (c) 2026 the TeraSharp contributors

// T206 - Characters. Replaces the retail Users/Default.aspx hub, the subset of its 34 detail tabs
// this tool covers (ItemInfo, WareHouse, SkillInfo, Achievement, UserEpSystem, UserRestrict), and
// the right-hand operator command menu. ParcelInfo is on the Mail screen instead, and the item add
// is template id + amount only - retail also asked for enchant and passive options, which nothing
// here can apply, so offering the boxes would be a lie.
import { get, write, h, stat, table, field, link, toast, fmt, params, go } from './app.js';

const KV = [{ key: 'k', label: 'Field' },
            { label: 'Value', wrapped: true, cell: r => r.node || String(r.v ?? '') }];
const num = f => Number(f.input.value);

export async function mount(root) {
  const p = params();
  if (!p.id && !p.name) return searchScreen(root, p);
  const { status, data } = await get('/api/character', { id: p.id, name: p.name });
  if (status !== 200 || !data?.character) {
    return root.replaceChildren(h('h1', { class: 'screen' }, 'Characters'),
      h('p', { class: 'error' }, (data?.message || 'no such character') + ' (' + status + ')'),
      link('back to the search', 'characters', {}));
  }
  return detail(root, data);
}

// ------------------------------------------------------------------------------- the search hub

async function searchScreen(root, p) {
  const q = field('Character name', { value: p.q || '', placeholder: 'part of a name' });
  const hits = h('div', {});
  const run = async () => {
    const res = await get('/api/search', { q: q.input.value.trim(), kind: 'name' });
    const rows = (res.data?.hits || []).map(x => Object.assign({ online: x.online }, x.character));
    hits.replaceChildren(res.data?.note ? h('p', { class: 'muted' }, res.data.note) : null, table([
      { label: 'Name', cell: r => link(r.name, 'characters', { id: r.id }) },
      { key: 'id', label: 'Id', num: true }, { key: 'level', label: 'Level', num: true },
      { key: 'zone', label: 'Zone', num: true },
      { label: 'Online', cell: r => h('span', { class: 'pill ' + (r.online ? 'ok' : '') }, r.online ? 'yes' : 'no') },
    ], rows, 'nothing matched'));
  };
  const restore = async c => {
    const res = await write('/api/restore-character', { id: c.id }, 'Reason for restoring ' + c.name + ':');
    if (res && res.status === 200) mount(root);
  };
  const deleted = await get('/api/deleted', { limit: 25 });

  root.replaceChildren(
    h('h1', { class: 'screen' }, 'Characters'),
    h('form', { class: 'row', onsubmit: e => { e.preventDefault(); run(); } },
      h('div', { class: 'grow' }, q.node), h('button', { class: 'primary' }, 'Search')),
    hits,
    h('h3', {}, 'Recently deleted'),
    table([
      { key: 'name', label: 'Name' }, { key: 'id', label: 'Id', num: true },
      { key: 'level', label: 'Level', num: true }, { label: 'Last login', cell: r => fmt.time(r.lastLogin) },
      { label: '', cell: r => h('button', { onclick: () => restore(r) }, 'Restore') },
    ], deleted.data?.deleted || [], 'nothing is waiting on a delete'));
  if (p.q) run();
}

// --------------------------------------------------------------------- the per-character shell
// The tab row is the retail detail page's left pane and Actions its right one. One /api/character
// read feeds every tab, so a tab click re-renders the section holder and nothing else.

async function detail(root, d) {
  const c = d.character;
  const reload = () => mount(root);
  const tabs = ['Overview', 'Inventory', 'Skills', 'Achievements', 'EP', 'Restrictions'];
  const body = h('div', {});
  let achievements = null, at = 'Overview';
  const bar = h('div', { class: 'row' }, tabs.map(t => h('button', { onclick: () => paint(t) }, t)));

  async function paint(next) {
    at = next;
    Array.from(bar.children).forEach((b, i) => { b.className = tabs[i] === at ? 'primary' : ''; });
    if (at === 'Achievements' && achievements === null) {
      achievements = (await get('/api/achievements', { id: c.id })).data || {};
    }
    const section = { Overview: () => overview(d), Inventory: () => inventory(d, reload),
      Skills: () => skills(c, reload), Achievements: () => achieved(achievements), EP: () => epTab(d, reload),
      Restrictions: () => table(RESTRICT, d.restrictions, 'no ban or mute on this character') };
    body.replaceChildren(...[].concat(section[at]()));
  }

  root.replaceChildren(
    h('h1', { class: 'screen' }, 'Characters'),
    h('div', { class: 'row' }, h('strong', {}, c.name),
      h('span', { class: 'muted' }, '#' + c.id + ' - level ' + c.level + ' - account ' + c.accountId),
      h('span', { class: 'grow' }), link('search again', 'characters', {})),
    bar, body, actions(d, reload));
  await paint(at);
}

const RESTRICT = [
  { key: 'typeName', label: 'Kind' }, { key: 'type', label: 'Type', num: true },
  { key: 'level', label: 'Level', num: true },
  { label: 'Until', cell: r => r.until ? fmt.time(r.until) : 'permanent' },
  { label: 'Active', cell: r => h('span', { class: 'pill ' + (r.active ? 'bad' : '') }, r.active ? 'yes' : 'no') },
  { key: 'reason', label: 'Reason', wrapped: true }, { label: 'Set at', cell: r => fmt.time(r.setAt) },
];

function overview(d) {
  const c = d.character, pos = d.position || {}, pr = d.progress || {};
  const round = n => Math.round(Number(n) || 0);
  return [
    h('div', { class: 'cards' },
      stat('Level', c.level ?? '?', 'race ' + (c.race ?? '?') + ' / class ' + (c.class ?? '?')),
      stat('Money', fmt.num(d.money), 'carried gold'), stat('Zone', pos.zone ?? '?', 'world ' + (pos.world ?? '?')),
      stat('Exp', fmt.num(pr.exp), fmt.duration(pr.playSeconds) + ' played')),
    h('h3', {}, 'Character'),
    table(KV, [
      { k: 'zone / x / y / z', v: [pos.zone, round(pos.x), round(pos.y), round(pos.z)].join(' / ') },
      { k: 'world / guard / section', v: [pos.world, pos.guard, pos.section].join(' / ') },
      { k: 'exp / rest bonus', v: fmt.num(pr.exp) + ' / ' + fmt.num(pr.restBonus) },
      { k: 'quests active / done', v: fmt.num(pr.questsActive) + ' / ' + fmt.num(pr.questsCompleted) },
      { k: 'achievements / sections seen', v: fmt.num(pr.achievements) + ' / ' + fmt.num(pr.sectionsVisited) },
      { k: 'guild', v: 'none', node: d.guild ? link(d.guild.name, 'guilds', { id: d.guild.id }) : null },
      { k: 'last login', v: fmt.time(c.lastLogin) }, { k: 'delete at', v: d.deleteAt ? fmt.time(d.deleteAt) : 'no' },
    ]),
  ];
}

function inventory(d, reload) {
  const remove = async it => {
    const what = it.name || 'template ' + it.templateId;
    if (!window.confirm('Remove ' + what + ' from ' + d.character.name + '?')) return;
    const res = await write('/api/remove-item', { itemDbId: it.itemDbId }, 'Reason for removing ' + what + ':');
    if (res && res.status === 200) reload();
  };
  const cols = [
    { key: 'slot', label: 'Slot', num: true }, { key: 'templateId', label: 'Template', num: true },
    { key: 'name', label: 'Name' }, { key: 'count', label: 'Count', num: true },
    { key: 'itemDbId', label: 'Item id', num: true },
    { label: '', cell: it => h('button', { class: 'danger', onclick: () => remove(it) }, 'Remove') },
  ];
  return [
    h('h3', {}, 'Inventory (' + (d.items?.length || 0) + ' stacks)'),
    table(cols, d.items, 'the bags are empty'),
    (d.warehouse || []).map(w => [
      h('h3', {}, 'Warehouse tab ' + w.tab + ' - ' + fmt.num(w.money) + ' gold, ' + (w.slots ?? '?') + ' slots'),
      table(cols, w.items, 'this tab is empty'),
    ]),
  ];
}

function skills(c, reload) {
  return [
    h('p', { class: 'muted' }, 'Per-skill rows sit inside the world server blob, which this tool does not '
      + 'decode - retail SkillInfo listed them one by one. The reset clears the learned set, so the '
      + 'world re-grants the defaults at the next login.'),
    h('button', { class: 'danger', onclick: async () => {
      if (!window.confirm('Reset every learned skill on ' + c.name + ' (#' + c.id + ')?')) return;
      const res = await write('/api/reset-skills', { id: c.id }, 'Reason for the skill reset:');
      if (res && res.status === 200) reload();
    } }, 'Reset skills'),
  ];
}

function achieved(a) {
  return [
    h('h3', {}, (a?.count ?? 0) + ' achievement(s) earned'),
    table([{ key: 'id', label: 'Achievement', num: true }, { key: 'serverUnique', label: 'Server first' },
      { label: 'Earned', cell: r => fmt.time(r.date) }], a?.done, 'none earned yet'),
    h('h3', {}, 'Server firsts claimed'),
    table([{ key: 'id', label: 'Achievement', num: true }, { key: 'partyId', label: 'Party', num: true },
      { label: 'Claimed', cell: r => fmt.time(r.claimedAt) }], a?.serverFirsts, 'no server first here'),
  ];
}

function epTab(d, reload) {
  const e = d.ep, id = d.character.id;
  const level = field('EP level', { type: 'number', value: e?.level ?? 0, min: 0 });
  const point = field('EP points', { type: 'number', value: e?.point ?? 0, min: 0 });
  return [
    table(KV, e ? [
      { k: 'level', v: e.level }, { k: 'point', v: e.point }, { k: 'exp', v: fmt.num(e.exp) },
      { k: 'daily exp / limit', v: fmt.num(e.dailyExp) + ' / ' + fmt.num(e.dailyLimit) },
      { k: 'daily reset', v: fmt.time(e.resetTime) },
    ] : [], 'this character has no EP row yet'),
    h('div', { class: 'row' }, level.node, point.node,
      h('button', { class: 'primary', onclick: async () => {
        const res = await write('/api/set-ep', { id, level: num(level), point: num(point) }, 'Reason for the EP set:');
        if (res && res.status === 200) reload();
      } }, 'Set EP')),
  ];
}

// ------------------------------------------------------------------------------ the action pane
// The retail command menu, less teleport-to-player (that delegate is unwired) and the account-wide
// commands, which belong to Accounts.

function actions(d, reload) {
  const c = d.character, pos = d.position || {}, id = c.id;
  const run = async (path, body, why) => {
    const res = await write(path, body, why);
    if (res && res.status === 200) reload();
  };
  const btn = (label, path, body, why) => h('button', { onclick: () => run(path, body(), why) }, label);
  const level = field('Level', { type: 'number', value: c.level, min: 1, max: 70 });
  const money = field('Money', { type: 'number', value: d.money ?? 0, min: 0 });
  const [zone, x, y, z] = [['Zone', pos.zone], ['X', pos.x], ['Y', pos.y], ['Z', pos.z]]
    .map(([label, v]) => field(label, { type: 'number', value: v ?? '' }));
  const rename = field('New name', { value: c.name });
  const query = field('Item name', { placeholder: 'part of an item name' });
  const found = field('Match', { tag: 'select' });
  const amount = field('Amount', { type: 'number', value: 1, min: 1 });
  const lookUp = async () => {
    const res = await get('/api/item-search', { q: query.input.value.trim(), limit: 50 });
    const items = res.data?.items || [];
    found.input.replaceChildren(...items.map(i => h('option', { value: i.templateId }, i.templateId + ' - ' + i.name)));
    if (items.length === 0) toast('no item matched that name', 'bad');
  };
  const giveItem = () => {
    const templateId = Number(found.input.value);
    if (!templateId) { toast('search for an item first', 'bad'); return; }
    run('/api/give-item', { id, templateId, amount: num(amount) }, 'Reason for the item grant:');
  };
  const resetChar = () => {
    if (!window.confirm('Reset ' + c.name + ' (#' + id + ')? Transient state is cleared.')) return;
    run('/api/reset-character', { id }, 'Reason for the character reset:');
  };
  const deleteChar = async () => {
    if (!window.confirm('Delete ' + c.name + ' (#' + id + ') for good? There is no undo.')) return;
    const res = await write('/api/delete-character', { id }, 'Reason for deleting ' + c.name + ':');
    if (res && res.status === 200) go('characters', {});
  };

  return h('fieldset', {}, h('legend', {}, 'Actions on ' + c.name),
    h('div', { class: 'row' }, level.node,
      btn('Set level', '/api/set-level', () => ({ id, level: num(level) }), 'Reason for the level change:'),
      money.node,
      btn('Set money', '/api/set-money', () => ({ id, money: num(money) }), 'Reason for the money change:')),
    h('div', { class: 'row' }, zone.node, x.node, y.node, z.node, btn('Set position', '/api/set-position',
      () => ({ id, zone: num(zone), x: num(x), y: num(y), z: num(z) }), 'Reason for the move:')),
    h('div', { class: 'row' }, rename.node,
      btn('Rename', '/api/rename', () => ({ id, name: rename.input.value.trim() }), 'Reason for the rename:')),
    h('div', { class: 'row' }, h('div', { class: 'grow' }, query.node),
      h('button', { onclick: lookUp }, 'Search'), h('div', { class: 'grow' }, found.node), amount.node,
      h('button', { class: 'primary', onclick: giveItem }, 'Add item')),
    h('div', { class: 'row' }, h('button', { class: 'danger', onclick: resetChar }, 'Reset character'),
      h('button', { class: 'danger', onclick: deleteChar }, 'Delete character')));
}
