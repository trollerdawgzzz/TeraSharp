// SPDX-License-Identifier: MIT
// Copyright (c) 2026 the TeraSharp contributors

// T206 - Guilds. One screen in place of retail's three: Guild/Default.aspx looked a guild up by
// GuildDBID or GuildName, Users/GuildInfo.aspx showed the guild and its roster, and
// Guild/GuildWarManagement.aspx held the war grid - here the search, the guild, the members and
// both war tables are the same page, because an operator who found a guild always wanted them.
// Retail's CHANGE_GUILD_ANNOUNCE and banish_guild_member have no endpoint on the arbiter yet, so
// the actions are money, level and disband. War state and result print as the raw integers the
// server sends: retail enumerated 19 result names and none have been transcribed here, so any
// label this screen showed would be invented rather than ported.
import { get, write, h, card, stat, table, field, link, toast, fmt, params, go } from './app.js';

export async function mount(root) {
  const p = params();
  const q = field('Guild id or name', { value: p.q || '', placeholder: 'GuildDBID or GuildName' });
  const search = h('form', {
    class: 'row',
    onsubmit: e => { e.preventDefault(); go('guilds', { q: q.input.value.trim() }); },
  }, h('div', { class: 'grow' }, q.node), h('button', { class: 'primary', type: 'submit' }, 'Search'));

  root.replaceChildren(
    h('h1', { class: 'screen' }, 'Guilds'),
    card('Find a guild', search),
    ...(p.id || p.name ? await one(p, root) : await found(p)),
  );
}

// -------------------------------------------------------------------------- the search results

async function found(p) {
  const { status, data } = await get('/api/guilds', { q: p.q, limit: 50 });
  if (status !== 200 || !data) {
    return [h('p', { class: 'error' }, (data?.message || 'the guild list failed') + ' (' + status + ')')];
  }
  const guilds = data.guilds || [];
  return [
    h('h3', {}, 'Guilds'),
    table([
      { key: 'id', label: 'Id', num: true },
      { key: 'name', label: 'Name' },
      { key: 'level', label: 'Level', num: true },
      { label: 'Money', num: true, cell: g => fmt.num(g.money) },
      { label: 'Chief', cell: g => chiefLink(g) },
      { key: 'members', label: 'Members', num: true },
      { key: 'warAcceptable', label: 'War ok' },
      { label: '', cell: g => link('open', 'guilds', { id: g.id }) },
    ], guilds, p.q ? 'no guild matches ' + p.q : 'there are no guilds'),
    h('p', { class: 'muted' },
      (data.shown ?? guilds.length) + ' shown of ' + (data.total ?? guilds.length) + ' total'),
  ];
}

/** The chief as a link into Characters, or whatever of it the row has. */
function chiefLink(g) {
  if (g.chiefDbId) return link(g.chief || g.chiefDbId, 'characters', { id: g.chiefDbId });
  return g.chief || '';
}

// ------------------------------------------------------------------------------- one guild

async function one(p, root) {
  const { status, data } = await get('/api/guild', { id: p.id, name: p.name });
  if (status !== 200 || !data?.guild) {
    return [h('p', { class: 'error' }, (data?.message || 'no such guild') + ' (' + status + ')')];
  }
  const g = data.guild;
  const members = data.members || [], wars = data.wars || [], history = data.history || [];
  return [
    h('div', { class: 'cards' },
      stat('Level', g.level ?? 0, 'exp ' + fmt.num(g.exp)),
      stat('Money', fmt.num(g.money), 'guild funds'),
      stat('Members', fmt.num(members.length), 'chief ' + (g.chief || g.chiefDbId || '?')),
      stat('War acceptable', g.warAcceptable ? 'yes' : 'no', wars.length + ' war(s) on record'),
    ),

    h('h3', {}, 'Guild'),
    table([{ key: 'k', label: 'Field' }, { label: 'Value', wrapped: true, cell: r => r.v }], [
      { k: 'id', v: String(g.id ?? '') },
      { k: 'name', v: g.name || '' },
      { k: 'chief', v: chiefLink(g) },
      { k: 'exp', v: fmt.num(g.exp) },
      { k: 'point', v: fmt.num(g.point) },
      { k: 'announce', v: g.announce || '' },
      { k: 'title', v: g.title || '' },
      { k: 'promotion', v: g.promotion || '' },
      { k: 'created', v: fmt.time(g.createDate) },
      { k: 'join levels', v: (g.joinMinLevel ?? 0) + ' - ' + (g.joinMaxLevel ?? 0) },
    ]),

    h('h3', {}, 'Members'),
    table([
      { label: 'Name', cell: m => m.userDbId ? link(m.name, 'characters', { id: m.userDbId }) : (m.name || '') },
      { key: 'level', label: 'Level', num: true },
      { key: 'class', label: 'Class' },
      { key: 'race', label: 'Race' },
      { key: 'groupId', label: 'Group', num: true },
      { label: 'Joined', cell: m => fmt.time(m.joinDate) },
      { label: 'Last logout', cell: m => fmt.time(m.lastLogout) },
      { label: 'Weekly', num: true, cell: m => fmt.num(m.weekly) },
      { label: 'Total', num: true, cell: m => fmt.num(m.total) },
      { label: '', cell: m => m.isChief ? h('span', { class: 'pill ok' }, 'chief') : '' },
    ], members, 'this guild has no members'),

    h('h3', {}, 'Wars'),
    table([
      { key: 'warId', label: 'War id', num: true },
      { label: 'Opponent', cell: w => opponentLink(w) },
      { label: 'Side', cell: w => w.attacking ? 'attacker' : 'defender' },
      { label: 'Declared', cell: w => fmt.time(w.declaredAt) },
      { label: 'Money', num: true, cell: w => fmt.num(w.money) },
      { label: 'Defend money', num: true, cell: w => fmt.num(w.defendMoney) },
      { key: 'state', label: 'State', num: true },
      { key: 'defendDeclared', label: 'Defend declared' },
    ], wars, 'no war is running'),

    h('h3', {}, 'War history'),
    table([
      { key: 'attackGuildId', label: 'Attacker', num: true },
      { key: 'defendGuildId', label: 'Defender', num: true },
      { key: 'result', label: 'Result', num: true },
      { label: 'Ended', cell: w => fmt.time(w.endedAt) },
    ], history, 'this guild has never finished a war'),

    actions(g, members.length, root),
  ];
}

/** The other side of a war, linked back into this screen. */
function opponentLink(w) {
  const id = w.attacking ? w.defendGuildId : w.attackGuildId;
  if (!id) return w.opponent || '';
  return link(w.opponent || id, 'guilds', { id });
}

// ------------------------------------------------------------------------------- the actions

function actions(g, memberCount, root) {
  const money = field('Money', { type: 'number', value: g.money ?? 0, min: 0, step: 1 });
  const level = field('Level (0-20)', { type: 'number', value: g.level ?? 0, min: 0, max: 20, step: 1 });
  const done = res => { if (res && res.status === 200) mount(root); };

  const setMoney = async () => done(await write('/api/guild-money',
    { id: g.id, money: Number(money.input.value) },
    'Why is the guild fund changing? (retail made this reason mandatory)'));

  const setLevel = async () => {
    const value = Number(level.input.value);
    if (!Number.isInteger(value) || value < 0 || value > 20) return toast('the level must be 0..20', 'bad');
    done(await write('/api/guild-level', { id: g.id, level: value }, 'Why is the guild level changing?'));
  };

  const disband = async () => {
    if (!window.confirm('Disband guild "' + (g.name || g.id) + '" (id ' + g.id + ') with its '
      + memberCount + ' member(s)? The guild and every membership go away and there is no undo.')) return;
    const res = await write('/api/guild-disband', { id: g.id }, 'Why is this guild being disbanded?');
    if (res && res.status === 200) go('guilds');  // it is gone; fall back to the list
  };

  return h('fieldset', {},
    h('legend', {}, 'Actions'),
    h('div', { class: 'row' },
      h('div', { class: 'grow' }, money.node),
      h('button', { onclick: setMoney }, 'Set money'),
      h('div', { class: 'grow' }, level.node),
      h('button', { onclick: setLevel }, 'Set level'),
    ),
    h('div', { class: 'row' },
      h('button', { class: 'danger', onclick: disband }, 'Disband guild'),
      h('span', { class: 'muted' }, 'Announce and member banishment have no endpoint yet; '
        + 'retail did both from GuildInfo.aspx.'),
    ),
  );
}
