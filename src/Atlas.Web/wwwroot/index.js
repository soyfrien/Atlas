import { h, api, fmtBytes, fmtDate, fmtDur, snippetNodes, KIND_LABEL, KIND_ICON, verificationBadge } from '/common.js';

const $ = (id) => document.getElementById(id);
const state = { offset: 0, total: 0, loading: false };
const PAGE = 24;

function readUrl() {
  const p = new URLSearchParams(location.search);
  $('q').value = p.get('q') || '';
  $('f-kind').value = p.get('kind') || '';
  $('f-ver').value = p.get('verification') || '';
  $('f-tag').value = p.get('tag') || '';
  $('f-from').value = p.get('from') || '';
  $('f-to').value = p.get('to') || '';
}

function queryParams() {
  const p = new URLSearchParams();
  const set = (k, v) => { if (v) p.set(k, v); };
  set('q', $('q').value.trim()); set('kind', $('f-kind').value); set('verification', $('f-ver').value);
  set('tag', $('f-tag').value); set('from', $('f-from').value); set('to', $('f-to').value);
  return p;
}

function card(d) {
  const meta = [KIND_LABEL[d.kind] || d.kind, d.date ? fmtDate(d.date) : null, d.durationSec ? fmtDur(d.durationSec) : null,
    d.pageCount > 1 ? d.pageCount + ' pages' : null, fmtBytes(d.sizeBytes)].filter(Boolean);
  return h('a', { class: 'card', href: '/d/' + d.id },
    h('div', { class: 'thumb' }, d.hasThumb ? h('img', { src: `/thumbs/${d.id}.jpg`, alt: '', loading: 'lazy' }) : (KIND_ICON[d.kind] || '📄')),
    h('div', { class: 'body' },
      h('h3', {}, d.title),
      h('div', {}, verificationBadge(d.verification)),
      d.snippet ? h('div', { class: 'snippet' }, snippetNodes(d.snippet)) : (d.description ? h('div', { class: 'snippet' }, d.description.slice(0, 140)) : null),
      h('div', { class: 'meta' }, meta.map((m) => h('span', {}, m))),
      d.tags.length ? h('div', { class: 'tags' }, d.tags.slice(0, 4).map((t) => h('span', { class: 'tag' }, t))) : null));
}

async function load(reset) {
  if (state.loading) return;
  state.loading = true;
  if (reset) { state.offset = 0; $('results').replaceChildren(); }
  const p = queryParams();
  p.set('limit', PAGE); p.set('offset', state.offset);
  try {
    const r = await api('/api/docs?' + p);
    state.total = r.total;
    for (const d of r.items) $('results').append(card(d));
    state.offset += r.items.length;
    const hasFilter = [...queryParams().keys()].length > 0;
    $('status').textContent = r.total === 0 ? '' : `${r.total} ${r.total === 1 ? 'item' : 'items'}${hasFilter ? ' match' : ''}`;
    if (r.total === 0) $('results').replaceChildren(h('div', { class: 'empty' }, hasFilter ? 'Nothing matches that search.' : 'The archive is empty so far. Items appear here once they have been reviewed and published.'));
    $('more').hidden = state.offset >= r.total;
  } catch (e) {
    $('status').replaceChildren(h('span', { class: 'error' }, e.message));
  } finally { state.loading = false; }
}

function apply() {
  const p = queryParams();
  history.replaceState(null, '', p.toString() ? '/?' + p : '/');
  load(true);
}

async function init() {
  const [cfg, tags] = await Promise.all([api('/api/config'), api('/api/tags')]);
  document.title = cfg.title;
  $('site-title').textContent = cfg.title;
  $('intro').textContent = cfg.intro;
  $('footer').textContent = cfg.contactNote || '';
  for (const [k, l] of Object.entries(KIND_LABEL)) $('f-kind').append(h('option', { value: k }, l));
  for (const v of cfg.verification) $('f-ver').append(h('option', { value: v }, v));
  for (const t of tags) $('f-tag').append(h('option', { value: t.tag }, `${t.tag} (${t.count})`));
  readUrl();
  $('search').addEventListener('submit', (e) => { e.preventDefault(); apply(); });
  for (const id of ['f-kind', 'f-ver', 'f-tag', 'f-from', 'f-to']) $(id).addEventListener('change', apply);
  $('more').addEventListener('click', () => load(false));
  load(true);
}

init().catch((e) => { $('status').textContent = e.message; });
