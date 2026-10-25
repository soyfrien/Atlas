// Tiny DOM helpers shared by all pages. Nothing here uses innerHTML: all text goes through textContent, so archive
// content can never inject markup.
export function h(tag, attrs, ...children) {
  const el = document.createElement(tag);
  for (const [k, v] of Object.entries(attrs || {})) {
    if (v == null || v === false) continue;
    if (k === 'class') el.className = v;
    else if (k === 'style') el.style.cssText = v; // CSSOM is allowed by our CSP; a style attribute in markup is not
    else if (k.startsWith('on') && typeof v === 'function') el.addEventListener(k.slice(2), v);
    else if (v === true) el.setAttribute(k, '');
    else el.setAttribute(k, v);
  }
  for (const c of children.flat(Infinity)) {
    if (c == null || c === false) continue;
    el.append(c.nodeType ? c : document.createTextNode(String(c)));
  }
  return el;
}

export async function api(path, opts = {}) {
  const init = { credentials: 'same-origin', ...opts };
  if (opts.json !== undefined) {
    init.method = opts.method || 'POST';
    init.headers = { 'Content-Type': 'application/json', ...(opts.headers || {}) };
    init.body = JSON.stringify(opts.json);
    delete init.json;
  }
  const res = await fetch(path, init);
  let body = null;
  const ct = res.headers.get('content-type') || '';
  if (ct.includes('json')) body = await res.json().catch(() => null);
  if (!res.ok) {
    const err = new Error((body && body.error) || (res.status === 429 ? 'Too many requests. Wait a minute and try again.' : `Request failed (${res.status}).`));
    err.status = res.status;
    err.body = body;
    throw err;
  }
  return body;
}

export function fmtBytes(n) {
  if (n == null) return '';
  const u = ['B', 'KB', 'MB', 'GB', 'TB'];
  let i = 0;
  while (n >= 1024 && i < u.length - 1) { n /= 1024; i++; }
  return (i === 0 ? n : n.toFixed(n < 10 ? 1 : 0)) + ' ' + u[i];
}

export function fmtDate(s) {
  if (!s) return '';
  const d = /^\d{4}-\d{2}-\d{2}$/.test(s) ? new Date(s + 'T12:00:00') : new Date(s);
  return isNaN(d) ? s : d.toLocaleDateString(undefined, { year: 'numeric', month: 'short', day: 'numeric' });
}

export function fmtDateTime(s) {
  const d = new Date(s);
  return isNaN(d) ? s : d.toLocaleString();
}

export function fmtDur(sec) {
  if (sec == null) return '';
  sec = Math.round(sec);
  const hh = Math.floor(sec / 3600), mm = Math.floor((sec % 3600) / 60), ss = sec % 60;
  return (hh ? hh + ':' + String(mm).padStart(2, '0') : mm) + ':' + String(ss).padStart(2, '0');
}

// Search snippets mark hits with \u0001 ... \u0002; turn them into <mark> elements safely.
export function snippetNodes(s) {
  if (!s) return [];
  const out = [];
  let open = false;
  for (const part of s.split(/([\u0001\u0002])/)) {
    if (part === '\u0001') open = true;
    else if (part === '\u0002') open = false;
    else if (part) out.push(open ? h('mark', {}, part) : part);
  }
  return out;
}

export const KIND_LABEL = { pdf: 'PDF', image: 'Image', video: 'Video', audio: 'Audio', text: 'Text', office: 'Document' };
export const KIND_ICON = { pdf: '📄', image: '🖼️', video: '🎬', audio: '🎧', text: '📝', office: '📑' };

export function verificationBadge(v) {
  const cls = v === 'verified' ? 'ok' : v === 'partially verified' ? 'warn' : 'muted';
  return h('span', { class: 'badge ' + cls }, v);
}

export async function copyText(text) {
  try { await navigator.clipboard.writeText(text); return true; } catch { return false; }
}

if ('serviceWorker' in navigator) navigator.serviceWorker.register('/sw.js').catch(() => {});
