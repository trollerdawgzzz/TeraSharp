// SPDX-License-Identifier: MIT
// Copyright (c) 2026 the TeraSharp contributors

// T206 - Mail. Retail spread this over four pages: Users/ATO_SendNormalParcel.aspx to send,
// Users/ParcelInfo.aspx to read a mailbox, and WorldFestival/TeraTimeRegister plus
// MailEventRegister to blast everyone online. One screen does all four, because the only thing
// that differed between them was the receiver list. Retail's form stopped at four attachments;
// this protocol has five parcel slots, so the cap here is five.
import { get, write, h, stat, table, field, toast, fmt, params } from './app.js';

/** As many slots as CharacterStore.MaxParcelAttachments; the server refuses a sixth pair. */
const MAX_ATTACHMENTS = 5;

/** The stand-in before a mailbox is named, shown by both the first mount and a cleared box. */
const EMPTY = () => h('div', { class: 'empty' }, 'name a character to read their mailbox');

/** The select value -> the send body's receiver key, and the wording a confirm needs. */
const SCOPES = [['one', 'one character'], ['online', 'everyone online'],
  ['everyone', 'every character']];

export async function mount(root) {
  const q = params();
  const parcels = h('div', {});
  const who = field('Character',
    { value: q.name || q.id || '', placeholder: 'exact name or character id' });
  const load = () => loadParcels(parcels, who.input.value.trim());

  root.replaceChildren(
    h('h1', { class: 'screen' }, 'Mail'),
    sendBox(),
    h('fieldset', {}, h('legend', {}, 'View parcels'),
      h('div', { class: 'row' },
        h('div', { class: 'grow' }, who.node),
        h('button', { class: 'primary', onclick: load }, 'Load'))),
    parcels,
  );

  // The Characters screen links in as #/mail?name=X, so a named target loads unprompted.
  if (who.input.value.trim().length > 0) await load();
  else parcels.replaceChildren(EMPTY());
}

// --------------------------------------------------------------------------------- the send form

function sendBox() {
  const name = field('Character', { placeholder: 'exact name' });
  const scope = field('Recipients', { tag: 'select',
    onchange: () => { name.input.disabled = scope.input.value !== 'one'; } });
  scope.input.append(...SCOPES.map(([value, label]) => h('option', { value }, label)));

  const sender = field('Sender', { value: 'GM' });
  const title = field('Title', { placeholder: 'required' });
  const message = field('Message', { tag: 'textarea' });
  const money = field('Money', { type: 'number', value: '0', min: '0' });

  const picked = [];
  const list = h('div', {});
  const search = field('Item', { placeholder: 'name or template id' });
  const found = field('Match', { tag: 'select' });
  const amount = field('Amount', { type: 'number', value: '1', min: '1' });

  async function doSearch() {
    const { data } = await get('/api/item-search', { q: search.input.value.trim(), limit: 25 });
    const items = data?.items || [];
    found.input.replaceChildren(...items.map(i =>
      h('option', { value: i.templateId }, i.templateId + ' - ' + i.name)));
    if (items.length === 0)
      toast(data?.loaded ? 'no item matched' : 'no item names are loaded - type a template id', 'bad');
  }

  function drawPicked() {
    list.replaceChildren(picked.length === 0
      ? h('div', { class: 'empty' }, 'no attachments')
      : h('div', {}, picked.map((p, i) => h('div', { class: 'row' },
        h('span', { class: 'grow mono' }, p.label + ' x ' + fmt.num(p.amount)),
        h('button', { onclick: () => { picked.splice(i, 1); drawPicked(); } }, 'Remove')))));
  }

  function addAttachment() {
    if (picked.length >= MAX_ATTACHMENTS)
      return toast('a parcel carries at most ' + MAX_ATTACHMENTS + ' attachments', 'bad');
    const templateId = Number(found.input.value || search.input.value.trim());
    if (!Number.isFinite(templateId) || templateId <= 0)
      return toast('search for an item first', 'bad');
    const amt = Math.max(1, Number(amount.input.value) || 1);
    const hit = found.input.selectedOptions[0];
    picked.push({ templateId, amount: amt, label: hit ? hit.textContent : String(templateId) });
    drawPicked();
  }

  async function send() {
    const body = {
      sender: sender.input.value.trim() || 'GM',
      title: title.input.value.trim(),
      message: message.input.value,
      money: Number(money.input.value) || 0,
      items: picked.map(p => p.templateId + ':' + p.amount).join(','),
    };
    if (body.title.length === 0) { toast('a title is required', 'bad'); return; }

    const mode = scope.input.value;
    if (mode === 'one') {
      const target = name.input.value.trim();
      if (target.length === 0) { toast('name the character to mail', 'bad'); return; }
      body.name = target;
    } else {
      body.all = mode;
      const label = mode === 'online' ? 'everyone online right now'
        : 'every character on this server (up to 5000)';
      if (!window.confirm('Mail "' + body.title + '" to ' + label + '? One parcel each, and'
        + ' there is no way to take them back.')) return;
    }

    const res = await write('/api/send-mail', body, 'Reason for the audit log:');
    if (res && res.status === 200 && (res.data?.result ?? 0) === 0) {
      toast('sent ' + fmt.num(res.data.sent ?? 0) + ' of ' + fmt.num(res.data.receivers ?? 0), 'ok');
    }
  }

  drawPicked();
  return h('fieldset', {}, h('legend', {}, 'Send system mail'),
    h('div', { class: 'row' },
      h('div', { class: 'grow' }, scope.node),
      h('div', { class: 'grow' }, name.node),
      h('div', { class: 'grow' }, sender.node)),
    title.node,
    message.node,
    money.node,
    h('h3', {}, 'Attachments'),
    h('div', { class: 'row' },
      h('div', { class: 'grow' }, search.node),
      h('button', { onclick: doSearch }, 'Search'),
      h('div', { class: 'grow' }, found.node),
      h('div', { class: 'grow' }, amount.node),
      h('button', { onclick: addAttachment }, 'Add attachment')),
    list,
    h('div', { class: 'row' }, h('button', { class: 'primary', onclick: send }, 'Send')));
}

// ------------------------------------------------------------------------------ the parcel boxes

async function loadParcels(into, who) {
  if (who.length === 0) return into.replaceChildren(EMPTY());
  const key = /^\d+$/.test(who) ? 'id' : 'name';
  const { status, data } = await get('/api/parcels', { [key]: who });
  if (status !== 200 || !data)
    return into.replaceChildren(h('p', { class: 'error' },
      (data?.message || 'no such character') + ' (' + status + ')'));
  const reload = () => loadParcels(into, who);
  into.replaceChildren(
    h('div', { class: 'cards' },
      stat('Unread', fmt.num(data.unread ?? 0), 'never opened'),
      stat('Read, unclaimed', fmt.num(data.readUnclaimed ?? 0), 'attachments still sitting there')),
    h('h3', {}, 'Inbox - ' + (data.character || who)),
    table(parcelCols(reload), data.inbox, 'the inbox is empty'),
    h('h3', {}, 'Sent'),
    table(parcelCols(null), data.sent, 'this character has sent nothing'),
  );
}

/** Retail's grid: ParcelDBID, Type, Status, Sender, Recver, SendTime, RecvTime, ItemName, Msg. */
function parcelCols(reload) {
  const cols = [
    { key: 'parcelId', label: 'Parcel', num: true },
    { key: 'type', label: 'Type', num: true },
    { key: 'status', label: 'Status', num: true },
    { key: 'sender', label: 'Sender' },
    { key: 'receiver', label: 'Receiver' },
    { key: 'title', label: 'Title', wrapped: true },
    { label: 'Money', num: true, cell: r => fmt.num(r.money) },
    { label: 'Sent', cell: r => fmt.time(r.createdAt) },
    { label: 'Attachments', wrapped: true,
      cell: r => (r.items || []).map(i => (i.name || i.templateId) + ' x ' + i.amount).join(', ') },
  ];
  if (reload) cols.push({ label: '',
    cell: r => h('button', { class: 'danger', onclick: () => remove(r, reload) }, 'Delete') });
  return cols;
}

async function remove(parcel, reload) {
  if (!window.confirm('Delete parcel ' + parcel.parcelId + ' ("' + (parcel.title || '')
    + '")? Its attachments go with it, and there is no undo.')) return;
  const res = await write('/api/delete-parcel', { parcelId: parcel.parcelId });
  if (res) await reload();
}
