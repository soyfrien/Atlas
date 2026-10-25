// Minimal service worker: makes Atlas installable and lets the app shell open offline.
// It never touches /api, /files, /thumbs, /pages or /admin, so archive content and logins always hit the server.
const CACHE = 'atlas-shell-v2';
const SHELL = ['/', '/app.css', '/common.js', '/index.js', '/icons/icon-192.png', '/manifest.webmanifest'];

self.addEventListener('install', (event) => {
  event.waitUntil(caches.open(CACHE).then((c) => c.addAll(SHELL)).then(() => self.skipWaiting()));
});

self.addEventListener('activate', (event) => {
  event.waitUntil(
    caches.keys()
      .then((keys) => Promise.all(keys.filter((k) => k !== CACHE).map((k) => caches.delete(k))))
      .then(() => self.clients.claim()));
});

self.addEventListener('fetch', (event) => {
  const req = event.request;
  if (req.method !== 'GET') return;
  const url = new URL(req.url);
  if (url.origin !== location.origin) return;
  const p = url.pathname;
  if (p.startsWith('/api/') || p.startsWith('/files/') || p.startsWith('/thumbs/') || p.startsWith('/pages/') ||
      p.startsWith('/admin') || p.startsWith('/d/') || p === '/sw.js' || p === '/robots.txt' || p === '/sitemap.xml') return;
  if (req.mode === 'navigate') {
    event.respondWith(fetch(req).catch(() => caches.match('/')));
    return;
  }
  event.respondWith(
    fetch(req).then((res) => {
      if (res.ok && !res.redirected) { const copy = res.clone(); caches.open(CACHE).then((c) => c.put(req, copy)); }
      return res;
    }).catch(() => caches.match(req)));
});
