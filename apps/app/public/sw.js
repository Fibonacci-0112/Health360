// Service worker for the installable web app. Data lives in PowerSync's IndexedDB, not here; this
// only makes the app open offline, and serves index.html for every in-app route so reloading on a
// deep link works offline too. scripts/write-precache.mjs fills in both constants at export time.
const PRECACHE = [];
const VERSION = 'dev';
const SHELL = `health360-${VERSION}`;

self.addEventListener('install', (event) => {
  // Cache the whole exported app up front: the first visit's bundles loaded before this worker
  // existed, so they never passed through the fetch handler below.
  event.waitUntil(caches.open(SHELL).then((cache) => cache.addAll(['/', ...PRECACHE.filter((u) => u !== '/')])));
  self.skipWaiting();
});

self.addEventListener('activate', (event) => {
  event.waitUntil(
    caches.keys().then((keys) => Promise.all(keys.filter((k) => k !== SHELL).map((k) => caches.delete(k)))),
  );
  self.clients.claim();
});

self.addEventListener('fetch', (event) => {
  const { request } = event;
  const url = new URL(request.url);
  // Only same-origin GETs. Supabase, PowerSync and Edge Function calls always go to the network.
  if (request.method !== 'GET' || url.origin !== self.location.origin) return;

  if (request.mode === 'navigate') {
    // Network first so a new deploy shows up; the precached shell when offline. The shell is never
    // rewritten here: it must stay paired with the bundles this worker version precached, and a
    // new deploy's HTML arrives with its own worker.
    event.respondWith(fetch(request).catch(() => caches.match('/')));
    return;
  }

  // Hashed bundles, wasm and worker files: cache first, then fill the cache.
  event.respondWith(
    caches.match(request).then(
      (hit) =>
        hit ||
        fetch(request).then((res) => {
          if (res.ok) {
            const copy = res.clone();
            caches.open(SHELL).then((cache) => cache.put(request, copy));
          }
          return res;
        }),
    ),
  );
});
