import { h, api, fmtBytes, fmtDate, fmtDateTime, fmtDur, snippetNodes, copyText, KIND_LABEL, KIND_ICON, verificationBadge } from '/common.js';

const root = document.getElementById('root');
let pollTimer = null;
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

const STATUS_CLASS = { pending: 'warn', processing: 'muted', publishing: 'muted', published: 'ok', rejected: 'bad', failed: 'bad' };
const statusBadge = (s) => h('span', { class: 'badge ' + (STATUS_CLASS[s] || 'muted') }, s);

function stopPolling() { if (pollTimer) { clearTimeout(pollTimer); pollTimer = null; } }

// ------------------------------------------------------------------ login
function loginView(msg) {
  const input = h('input', { type: 'password', id: 'pass', autocomplete: 'current-password', placeholder: 'Passcode', autofocus: true });
  const err = h('p', { class: 'error', role: 'alert' }, msg || '');
  root.replaceChildren(
    h('header', { class: 'site' }, h('h1', {}, 'Atlas admin')),
    h('form', {
      class: 'panel',
      onsubmit: async (e) => {
        e.preventDefault();
        try { await api('/api/login', { json: { passcode: input.value } }); route(); }
        catch (ex) { err.textContent = ex.message; }
      },
    }, h('label', { for: 'pass' }, 'Admin passcode'), input, err, h('div', { class: 'row' }, h('button', { class: 'primary', type: 'submit' }, 'Sign in'))));
}

// ------------------------------------------------------------------ shell
async function shell(active, content) {
  let stats = { counts: {}, openReports: 0 };
  try { stats = await api('/api/admin/stats'); } catch (e) { if (e.status === 401) return loginView(); }
  const c = stats.counts;
  const tab = (key, label, href, n) => h('button', { class: key === active ? 'active' : '', onclick: () => { location.hash = href; } }, label + (n ? ` (${n})` : ''));
  root.replaceChildren(
    h('header', { class: 'site' }, h('h1', {}, h('a', { href: '#/queue' }, 'Atlas admin')), h('div', { class: 'row' },
      h('a', { class: 'btn', href: '/', target: '_blank', rel: 'noopener' }, 'View site'),
      h('button', { onclick: async () => { await api('/api/logout', { method: 'POST' }); loginView(); } }, 'Sign out'))),
    h('div', { class: 'tabs' },
      tab('queue', 'Review queue', '#/queue', c.pending), tab('published', 'Published', '#/published', c.published),
      tab('rejected', 'Rejected', '#/rejected', c.rejected), tab('all', 'All', '#/all'),
      tab('reports', 'Reports', '#/reports', stats.openReports), tab('upload', 'Upload', '#/upload'), tab('settings', 'Settings', '#/settings')),
    content);
  return stats;
}

// ------------------------------------------------------------------ lists
async function listView(key, status) {
  stopPolling();
  const search = h('input', { type: 'search', class: 'grow', placeholder: 'Search everything, including private text and notes', autocomplete: 'off' });
  const list = h('div', {});
  const stats = await shell(key, h('div', {}, h('form', { class: 'row', onsubmit: (e) => { e.preventDefault(); refresh(); } }, search, h('button', { type: 'submit' }, 'Search')), list));
  if (!stats) return;

  async function refresh() {
    const p = new URLSearchParams({ limit: 100 });
    if (status && status !== 'all') p.set('status', status);
    if (search.value.trim()) p.set('q', search.value.trim());
    let r;
    try { r = await api('/api/admin/docs?' + p); } catch (e) { return e.status === 401 ? loginView() : list.replaceChildren(h('p', { class: 'error' }, e.message)); }
    list.replaceChildren(r.items.length ? r.items.map(listItem) : h('div', { class: 'empty' }, status === 'pending' ? 'Nothing is waiting for review.' : 'Nothing here.'));
    stopPolling();
    if (r.items.some((d) => d.status === 'processing' || d.status === 'publishing')) pollTimer = setTimeout(refresh, 3000);
  }
  refresh();
}

function listItem(d) {
  return h('div', { class: 'list-item' },
    h('div', { class: 'thumb' }, d.hasThumbOriginal ? h('img', { src: `/api/admin/docs/${d.id}/thumb`, alt: '', loading: 'lazy' }) : (KIND_ICON[d.kind] || '📄')),
    h('div', { class: 'info' },
      h('a', { href: '#/review/' + d.id }, d.title),
      h('div', { class: 'meta' }, h('span', {}, statusBadge(d.status)), h('span', {}, KIND_LABEL[d.kind] || d.kind), h('span', {}, fmtBytes(d.sizeBytes)),
        h('span', {}, 'uploaded ' + fmtDateTime(d.createdAt)), d.status === 'published' ? verificationBadge(d.verification) : null),
      d.snippet ? h('div', { class: 'snippet' }, snippetNodes(d.snippet)) : null,
      d.error ? h('div', { class: 'small error' }, d.error) : null));
}

// ------------------------------------------------------------------ upload
const CHUNK = 8 * 1024 * 1024;

async function uploadFile(file, onProgress) {
  const init = await api('/api/admin/uploads', { json: { name: file.name, size: file.size } });
  let offset = 0;
  let failures = 0;
  while (offset < file.size) {
    const end = Math.min(offset + CHUNK, file.size);
    try {
      const res = await fetch(`/api/admin/uploads/${init.id}?offset=${offset}`, { method: 'PUT', body: file.slice(offset, end), credentials: 'same-origin' });
      const body = await res.json().catch(() => ({}));
      if (res.ok) { offset = body.received; failures = 0; onProgress(offset / file.size); continue; }
      if (res.status === 409 && body.received != null) { offset = body.received; continue; } // resume where the server is
      if (res.status >= 400 && res.status < 500 && res.status !== 408) { const e = new Error(body.error || `Upload failed (${res.status}).`); e.fatal = true; throw e; }
      throw new Error('Server error');
    } catch (e) {
      if (e.fatal || ++failures > 8) throw e;
      await sleep(Math.min(1000 * failures, 8000));
      try { offset = (await api(`/api/admin/uploads/${init.id}`)).received; } catch { /* network still down: retry the chunk */ }
    }
  }
  return api(`/api/admin/uploads/${init.id}/complete`, { method: 'POST' });
}

async function uploadView() {
  stopPolling();
  const rows = h('div', {});
  const input = h('input', { type: 'file', multiple: true, id: 'file' });
  const start = async (files) => {
    for (const file of files) {
      const bar = h('div', {});
      const status = h('span', { class: 'small' }, 'Uploading…');
      rows.append(h('div', { class: 'panel' }, h('div', {}, file.name, ' · ', fmtBytes(file.size)), h('div', { class: 'progress' }, bar), status));
      try {
        const doc = await uploadFile(file, (f) => { bar.style.width = Math.round(f * 100) + '%'; status.textContent = Math.round(f * 100) + '%'; });
        bar.style.width = '100%';
        status.replaceChildren('Uploaded. Processing, then it will wait in the review queue. ', h('a', { href: '#/review/' + doc.id }, 'Open it'));
      } catch (e) { status.className = 'small error'; status.textContent = e.message; }
    }
  };
  input.addEventListener('change', () => { start([...input.files]); input.value = ''; });
  await shell('upload', h('div', {},
    h('div', { class: 'panel' },
      h('p', {}, 'Uploads go to a private review queue. Nothing is public until you approve it.'),
      h('label', { for: 'file' }, 'Choose files (PDF, images, video, audio, text, Word/Excel/PowerPoint/OpenDocument)'), input,
      h('p', { class: 'small' }, 'Large files upload in 8 MB pieces and resume automatically if the connection drops. Keep this page open until it finishes.')),
    rows));
}

// ------------------------------------------------------------------ review
function blockOf(page, src, redactions, drawState) {
  const overlay = h('div', { class: 'overlay' });
  const block = h('div', { class: 'page-block' }, h('img', { src, alt: 'Page ' + page, draggable: 'false' }), overlay);
  const render = () => {
    overlay.replaceChildren(...redactions.filter((r) => r.page === page).map((r) => {
      const b = h('div', { class: 'rbox' }, h('button', { type: 'button', title: 'Remove this box', 'aria-label': 'Remove redaction box', onclick: (e) => {
        e.stopPropagation(); redactions.splice(redactions.indexOf(r), 1); drawState.changed(); render();
      } }, '×'));
      b.style.cssText = `left:${r.x * 100}%;top:${r.y * 100}%;width:${r.w * 100}%;height:${r.h * 100}%`;
      return b;
    }));
  };
  let start = null, temp = null;
  overlay.addEventListener('pointerdown', (e) => {
    if (!drawState.on || e.target !== overlay) return;
    const rect = overlay.getBoundingClientRect();
    start = { x: (e.clientX - rect.left) / rect.width, y: (e.clientY - rect.top) / rect.height };
    temp = h('div', { class: 'rbox' });
    overlay.append(temp);
    overlay.setPointerCapture(e.pointerId);
    e.preventDefault();
  });
  overlay.addEventListener('pointermove', (e) => {
    if (!start) return;
    const rect = overlay.getBoundingClientRect();
    const x = Math.min(Math.max((e.clientX - rect.left) / rect.width, 0), 1), y = Math.min(Math.max((e.clientY - rect.top) / rect.height, 0), 1);
    temp.style.cssText = `left:${Math.min(x, start.x) * 100}%;top:${Math.min(y, start.y) * 100}%;width:${Math.abs(x - start.x) * 100}%;height:${Math.abs(y - start.y) * 100}%`;
  });
  const finish = (e) => {
    if (!start) return;
    const rect = overlay.getBoundingClientRect();
    const x = Math.min(Math.max((e.clientX - rect.left) / rect.width, 0), 1), y = Math.min(Math.max((e.clientY - rect.top) / rect.height, 0), 1);
    const box = { page, x: Math.min(x, start.x), y: Math.min(y, start.y), w: Math.abs(x - start.x), h: Math.abs(y - start.y) };
    start = null; temp = null;
    if (box.w > 0.005 && box.h > 0.003) { redactions.push(box); drawState.changed(); }
    render();
  };
  overlay.addEventListener('pointerup', finish);
  overlay.addEventListener('pointercancel', () => { start = null; render(); });
  drawState.overlays.push(overlay);
  render();
  return block;
}

function previewFor(d, redactions, drawState) {
  const orig = `/api/admin/docs/${d.id}/original`;
  switch (d.kind) {
    case 'image': return h('div', {}, blockOf(1, orig, redactions, drawState));
    case 'pdf': return pdfPreview(d, redactions, drawState);
    case 'video': return h('video', { src: orig, controls: true, playsinline: true, preload: 'metadata', poster: d.hasThumbOriginal ? `/api/admin/docs/${d.id}/thumb` : null });
    case 'audio': return h('audio', { src: orig, controls: true, preload: 'metadata' });
    case 'text': {
      const pre = h('pre', { class: 'text' }, 'Loading…');
      fetch(orig, { credentials: 'same-origin' }).then((r) => r.text()).then((t) => { pre.textContent = t.slice(0, 100000); });
      return pre;
    }
    default: return h('div', { class: 'panel' }, 'No preview for this file type. ', h('a', { href: orig }, 'Download the original'), '.');
  }
}

function pdfPreview(d, redactions, drawState) {
  const wrap = h('div', {}, h('p', { class: 'small' }, `${d.pageCount ?? '?'} pages. `, h('a', { href: `/api/admin/docs/${d.id}/original`, target: '_blank', rel: 'noopener' }, 'Open the original PDF')));
  return wrap;
}

async function reviewView(id) {
  stopPolling();
  let d;
  try { d = await api('/api/admin/docs/' + id); } catch (e) { return e.status === 401 ? loginView() : listView('queue', 'pending'); }
  const busy = d.status === 'processing' || d.status === 'publishing';
  const redactions = d.redactions.map((r) => ({ ...r }));
  const drawState = { on: false, overlays: [], changed: () => { dirty = true; redactNote.textContent = redactions.length ? `${redactions.length} redaction box(es) will be burned into the published copy.` : ''; } };
  let dirty = false;
  const redactNote = h('p', { class: 'small' }, '');
  const msg = h('p', { class: 'small', role: 'status' });
  const f = {
    title: h('input', { type: 'text', id: 'f-title', maxlength: 300, value: d.title }),
    description: h('textarea', { id: 'f-desc', maxlength: 10000 }, d.description),
    date: h('input', { type: 'date', id: 'f-date', value: d.date || '' }),
    tags: h('input', { type: 'text', id: 'f-tags', value: d.tags.join(', '), placeholder: 'comma, separated, tags', autocapitalize: 'none' }),
    verification: h('select', { id: 'f-ver' }, ['unverified', 'partially verified', 'verified'].map((v) => h('option', { value: v, selected: v === d.verification }, v))),
    notes: h('textarea', { id: 'f-notes', maxlength: 20000, placeholder: 'How it arrived, what you did to check it. Internal only.' }, d.sourceNotes),
  };
  const body = () => ({
    title: f.title.value, description: f.description.value, date: f.date.value, verification: f.verification.value, sourceNotes: f.notes.value,
    tags: f.tags.value.split(',').map((t) => t.trim()).filter(Boolean),
    ...(d.canRedact && d.status !== 'published' && d.status !== 'publishing' && dirty ? { redactions } : {}),
  });
  const run = async (fn, okText) => {
    msg.className = 'small'; msg.textContent = 'Working…';
    try { await fn(); msg.textContent = okText || ''; return true; }
    catch (e) { if (e.status === 401) { loginView(); return false; } msg.className = 'small error'; msg.textContent = e.message; return false; }
  };
  const save = () => run(async () => { d = await api('/api/admin/docs/' + id, { method: 'PATCH', json: body() }); dirty = false; }, 'Saved.');
  const act = (action, confirmText) => async () => {
    if (confirmText && !confirm(confirmText)) return;
    if (action === 'approve' || action === 'reject') { if (!(await save())) return; }
    if (await run(() => api(`/api/admin/docs/${id}/${action}`, { method: 'POST' }))) reviewView(id);
  };
  const del = async () => {
    if (!confirm('Delete this item and all of its files permanently?')) return;
    if (await run(() => api('/api/admin/docs/' + id, { method: 'DELETE' }))) location.hash = '#/queue';
  };

  const drawBtn = h('button', { type: 'button', onclick: () => {
    drawState.on = !drawState.on;
    drawBtn.textContent = drawState.on ? '✏️ Drawing boxes: on (tap to stop)' : '✏️ Draw redaction boxes';
    drawBtn.className = drawState.on ? 'primary' : '';
    for (const o of drawState.overlays) o.classList.toggle('drawing', drawState.on);
  } }, '✏️ Draw redaction boxes');

  const canEditRedactions = d.canRedact && d.status !== 'published' && d.status !== 'publishing';
  const actions = [];
  if (!busy) actions.push(h('button', { class: '', onclick: save }, 'Save'));
  if (d.status === 'pending' || d.status === 'rejected') actions.push(h('button', { class: 'primary', onclick: act('approve', 'Publish this item? It becomes visible to everyone.') }, 'Approve & publish'));
  if (d.status === 'pending') actions.push(h('button', { class: 'danger', onclick: act('reject') }, 'Reject'));
  if (d.status === 'published') actions.push(h('button', { onclick: act('unpublish', 'Take this item offline and move it back to pending?') }, 'Unpublish'), h('button', { class: 'danger', onclick: act('reject', 'Take this item offline and mark it rejected?') }, 'Reject'));
  if (d.status === 'failed') actions.push(h('button', { onclick: act('reprocess') }, 'Reprocess'));
  actions.push(h('button', { class: 'danger', onclick: del }, 'Delete'));

  const info = h('div', { class: 'panel small' },
    h('div', {}, 'Status: ', statusBadge(d.status), ' · ', KIND_LABEL[d.kind] || d.kind, ' · ', fmtBytes(d.sizeBytes)),
    h('div', {}, 'Uploaded as: ', d.originalName),
    h('div', {}, 'Uploaded: ' + fmtDateTime(d.createdAt)),
    d.sha256 ? h('div', {}, 'Original SHA-256 (private):') : null, d.sha256 ? h('div', { class: 'hash' }, d.sha256) : null,
    d.publicSha256 ? h('div', {}, 'Published copy SHA-256 (public):') : null, d.publicSha256 ? h('div', { class: 'hash' }, d.publicSha256) : null,
    d.status === 'published' ? h('div', {}, h('a', { href: '/d/' + d.id, target: '_blank', rel: 'noopener' }, 'Open public page')) : null);

  await shell(d.status === 'published' ? 'published' : d.status === 'rejected' ? 'rejected' : 'queue', h('div', {},
    h('p', {}, h('a', { href: '#/queue' }, '← Back')),
    d.error ? h('div', { class: 'notice' }, d.error) : null,
    busy ? h('div', { class: 'notice' }, d.status === 'processing' ? 'Processing the upload (hashing, extracting text, making a preview)…' : 'Publishing (sanitizing and applying redactions)…') : null,
    h('div', { class: 'review' },
      h('div', {},
        h('h2', {}, 'Preview of the original'),
        canEditRedactions ? h('div', { class: 'row' }, drawBtn) : null,
        canEditRedactions ? redactNote : null,
        previewFor(d, redactions, drawState)),
      h('div', {},
        h('div', { class: 'panel' },
          h('label', { for: 'f-title' }, 'Title (public)'), f.title,
          h('label', { for: 'f-desc' }, 'Description (public)'), f.description,
          h('label', { for: 'f-date' }, 'Date of the material (public)'), f.date,
          h('label', { for: 'f-tags' }, 'Tags (public)'), f.tags,
          h('label', { for: 'f-ver' }, 'Verification status (public)'), f.verification,
          h('label', { for: 'f-notes' }, 'Source notes — INTERNAL ONLY, never shown publicly'), f.notes),
        info,
        h('div', { class: 'sticky-bar' }, actions),
        msg))));
  drawState.changed();
  dirty = false;
  if (busy) pollTimer = setTimeout(() => { if (location.hash === '#/review/' + id) reviewView(id); }, 3000);
}

// ------------------------------------------------------------------ reports
async function reportsView() {
  stopPolling();
  let reports = [];
  const list = h('div', {});
  const draw = () => list.replaceChildren(reports.length ? reports.map((r) => h('div', { class: 'panel' },
    h('div', { class: 'row' }, h('strong', {}, r.reason), h('span', { class: 'badge ' + (r.status === 'open' ? 'warn' : 'muted') }, r.status), h('span', { class: 'small' }, fmtDateTime(r.createdAt))),
    h('p', { class: 'pre' }, r.message),
    r.contact ? h('div', { class: 'small' }, 'Contact: ', r.contact) : null,
    h('div', { class: 'small' }, 'About: ', h('a', { href: '#/review/' + r.docId }, r.docTitle)),
    h('div', { class: 'row mt' },
      h('button', { onclick: async () => { await api(`/api/admin/reports/${r.id}/status`, { json: { status: r.status === 'open' ? 'resolved' : 'open' } }); reportsView(); } }, r.status === 'open' ? 'Mark resolved' : 'Reopen'),
      h('button', { class: 'danger', onclick: async () => { await api('/api/admin/reports/' + r.id, { method: 'DELETE' }); reportsView(); } }, 'Delete')))) : h('div', { class: 'empty' }, 'No takedown or problem reports.'));
  const stats = await shell('reports', list);
  if (!stats) return;
  reports = await api('/api/admin/reports');
  draw();
}

// ------------------------------------------------------------------ settings
async function settingsView() {
  stopPolling();
  const s = await api('/api/admin/settings').catch((e) => e);
  if (s instanceof Error) return s.status === 401 ? loginView() : root.replaceChildren(h('p', { class: 'error' }, s.message));
  const noindex = h('input', { type: 'checkbox', id: 's-noindex', checked: s.noIndex });
  const title = h('input', { type: 'text', id: 's-title', maxlength: 80, value: s.title });
  const intro = h('textarea', { id: 's-intro', maxlength: 1000 }, s.intro);
  const note = h('textarea', { id: 's-note', maxlength: 1000, placeholder: 'Shown in the footer, e.g. how to reach you about takedowns.' }, s.contactNote);
  const msg = h('p', { class: 'small', role: 'status' });
  await shell('settings', h('form', {
    class: 'panel',
    onsubmit: async (e) => {
      e.preventDefault();
      try { await api('/api/admin/settings', { method: 'PUT', json: { noIndex: noindex.checked, title: title.value, intro: intro.value, contactNote: note.value } }); msg.className = 'small'; msg.textContent = 'Saved.'; }
      catch (ex) { msg.className = 'small error'; msg.textContent = ex.message; }
    },
  },
  h('label', { for: 's-title' }, 'Site name'), title,
  h('label', { for: 's-intro' }, 'Intro text on the home page'), intro,
  h('label', { for: 's-note' }, 'Footer note / contact for takedowns'), note,
  h('label', { class: 'row', for: 's-noindex' }, noindex, h('span', {}, 'Ask search engines not to index this site (noindex)')),
  h('p', { class: 'small' }, 'While this is on, every page is sent with an X-Robots-Tag noindex header, robots.txt disallows everything and there is no sitemap. Turn it off only when you want the archive to appear in search engines.'),
  h('div', { class: 'row' }, h('button', { class: 'primary', type: 'submit' }, 'Save settings')), msg));
}

// ------------------------------------------------------------------ router
async function route() {
  const hash = location.hash || '#/queue';
  let session;
  try { session = await api('/api/session'); } catch (e) { return root.replaceChildren(h('p', { class: 'error' }, e.message)); }
  if (!session.authenticated) return loginView();
  const [, page, arg] = hash.split('/');
  switch (page) {
    case 'review': return reviewView(arg);
    case 'published': return listView('published', 'published');
    case 'rejected': return listView('rejected', 'rejected');
    case 'all': return listView('all', 'all');
    case 'reports': return reportsView();
    case 'upload': return uploadView();
    case 'settings': return settingsView();
    default: return listView('queue', 'pending');
  }
}

window.addEventListener('hashchange', route);
route();
