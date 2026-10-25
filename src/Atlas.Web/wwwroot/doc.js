import { h, api, fmtBytes, fmtDate, fmtDateTime, fmtDur, copyText, KIND_LABEL, verificationBadge } from '/common.js';

const app = document.getElementById('app');
const id = location.pathname.split('/')[2];

function viewer(d) {
  const url = `/files/${d.id}`;
  switch (d.kind) {
    case 'image': return h('div', { class: 'viewer' }, h('img', { src: url, alt: d.title }));
    case 'video': return h('div', { class: 'viewer' }, h('video', { src: url, controls: true, playsinline: true, preload: 'metadata', poster: d.hasThumb ? `/thumbs/${d.id}.jpg` : null }));
    case 'audio': return h('div', { class: 'viewer' }, h('audio', { src: url, controls: true, preload: 'metadata' }));
    case 'text': {
      const pre = h('pre', { class: 'text' }, 'Loading…');
      fetch(url).then((r) => r.text()).then((t) => { pre.textContent = t.length > 200000 ? t.slice(0, 200000) + '\n\n… (truncated, download for the full text)' : t; });
      return h('div', { class: 'viewer' }, pre);
    }
    case 'pdf': return pdfViewer(d);
    default: return h('div', { class: 'panel' }, h('p', {}, 'This file type can\'t be shown in the browser. Use the download button.'));
  }
}

// PDFs are shown as page images (rendered by the server and loaded lazily) so they read well on any phone.
function pdfViewer(d) {
  const n = d.pageCount || 0;
  if (!n) return h('div', { class: 'panel' }, h('p', {}, 'No preview is available. Use the download button.'));
  const box = h('div', { class: 'viewer pages' });
  const SHOW = 10;
  let shown = 0;
  const more = h('button', { onclick: () => add() }, 'Show more pages');
  function add() {
    const end = Math.min(n, shown + SHOW);
    for (let i = shown + 1; i <= end; i++) box.append(h('img', { src: `/pages/${d.id}/${i}.jpg`, alt: `Page ${i} of ${n}`, loading: 'lazy' }));
    shown = end;
    more.hidden = shown >= n;
  }
  add();
  return h('div', {}, box, h('div', { class: 'row center' }, more));
}

function reportForm(d, cfg) {
  const msg = h('p', { class: 'small', role: 'status' });
  const reason = h('select', { id: 'r-reason', required: true }, cfg.reportReasons.map((r) => h('option', { value: r }, ({
    copyright: 'Copyright', privacy: 'Privacy / personal information', inaccurate: 'Inaccurate or misleading', safety: 'Safety concern', other: 'Something else' })[r] || r)));
  const message = h('textarea', { id: 'r-message', maxlength: 2000, required: true, placeholder: 'What is wrong with this item, and what should happen?' });
  const contact = h('input', { type: 'text', id: 'r-contact', maxlength: 200, placeholder: 'Email or other way to reach you (optional)', autocomplete: 'off' });
  const hp = h('div', { class: 'hp', 'aria-hidden': 'true' }, h('input', { type: 'text', id: 'r-website', tabindex: '-1', autocomplete: 'off' }));
  const form = h('form', {
    onsubmit: async (e) => {
      e.preventDefault();
      msg.className = 'small'; msg.textContent = 'Sending…';
      try {
        await api('/api/reports', { json: { docId: d.id, reason: reason.value, message: message.value, contact: contact.value, website: hp.firstChild.value } });
        form.replaceChildren(h('p', {}, 'Thank you. Your report was recorded and will be reviewed.'));
      } catch (err) { msg.className = 'small error'; msg.textContent = err.message; }
    },
  },
  h('label', { for: 'r-reason' }, 'Reason'), reason,
  h('label', { for: 'r-message' }, 'Details'), message,
  h('label', { for: 'r-contact' }, 'Contact (optional)'), contact, hp,
  h('div', { class: 'row', style: 'margin-top:12px' }, h('button', { class: 'primary', type: 'submit' }, 'Send report')), msg);
  return h('details', { class: 'report panel' }, h('summary', {}, 'Report this item or request a takedown'), form);
}

async function main() {
  const [cfg, d] = await Promise.all([api('/api/config'), api('/api/docs/' + id)]);
  document.getElementById('site-title').textContent = cfg.title;
  const meta = [KIND_LABEL[d.kind] || d.kind, d.date ? 'Dated ' + fmtDate(d.date) : null, d.durationSec ? fmtDur(d.durationSec) : null,
    d.pageCount > 1 ? d.pageCount + ' pages' : null, fmtBytes(d.sizeBytes), d.publishedAt ? 'Published ' + fmtDate(d.publishedAt) : null].filter(Boolean);
  app.replaceChildren(
    h('h2', {}, d.title),
    h('div', { class: 'row' }, verificationBadge(d.verification), h('span', { class: 'meta' }, meta.map((m) => h('span', {}, m)))),
    d.tags.length ? h('div', { class: 'tags', style: 'margin-top:10px' }, d.tags.map((t) => h('a', { class: 'tag', href: '/?tag=' + encodeURIComponent(t) }, t))) : null,
    d.description ? h('p', { style: 'white-space:pre-wrap;overflow-wrap:anywhere' }, d.description) : null,
    viewer(d),
    h('div', { class: 'row' },
      h('a', { class: 'btn primary', href: `/files/${d.id}/download` }, 'Download'),
      d.kind === 'pdf' ? h('a', { class: 'btn', href: `/files/${d.id}`, target: '_blank', rel: 'noopener' }, 'Open original PDF') : null),
    h('div', { class: 'panel' },
      h('div', { class: 'small' }, 'SHA-256 of the file offered for download. Compare it with the output of ', h('code', {}, 'sha256sum'), ' to check your copy is intact.'),
      h('div', { class: 'hash', style: 'margin-top:8px' }, d.sha256 || ''),
      h('div', { class: 'row', style: 'margin-top:8px' }, h('button', { onclick: async (e) => { if (await copyText(d.sha256)) e.target.textContent = 'Copied'; } }, 'Copy hash')),
      h('div', { class: 'small', style: 'margin-top:8px' }, 'Embedded metadata (such as camera, location or author fields) is removed from published copies.')),
    reportForm(d, cfg));
}

main().catch((e) => {
  app.replaceChildren(h('div', { class: 'empty' }, e.status === 404 ? 'This item doesn\'t exist or isn\'t published.' : e.message));
});
