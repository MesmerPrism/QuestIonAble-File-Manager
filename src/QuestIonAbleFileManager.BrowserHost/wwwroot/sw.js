// BrowserHost renders only its fixed public shell manifest here. A raw file cannot install.
const shell = /*QFM_SHELL_MANIFEST*/ null;
if (!shell || !/^[a-f0-9]{64}$/.test(shell.revision)) throw new Error('Rendered shell manifest required');
const cacheName = `qfm-shell-${shell.revision}`;
const staticPaths = new Set(Object.keys(shell.assets));
const hex = bytes => Array.from(new Uint8Array(bytes), byte => byte.toString(16).padStart(2, '0')).join('');
self.addEventListener('install', event => {
  event.waitUntil((async () => {
    // Verify the entire revision before writing anything. Replaced/missing assets fail installation.
    const verified = await Promise.all(Object.entries(shell.assets).map(async ([path, expected]) => {
      const response = await fetch(new Request(new URL(path, self.location.origin), {
        credentials: 'omit', cache: 'no-store', redirect: 'error'
      }));
      if (!response.ok || hex(await crypto.subtle.digest('SHA-256', await response.clone().arrayBuffer())) !== expected)
        throw new Error('Shell revision hash mismatch');
      return [path, response];
    }));
    const cache = await caches.open(cacheName);
    try { await Promise.all(verified.map(([path, response]) => cache.put(path, response))); }
    catch (error) { await caches.delete(cacheName); throw error; }
  })());
  // Do not skipWaiting: a new UI must not replace an active operation page.
});
self.addEventListener('activate', event => {
  event.waitUntil(caches.keys().then(names => Promise.all(names
    .filter(name => name.startsWith('qfm-shell-') && name !== cacheName)
    .map(name => caches.delete(name)))));
});
self.addEventListener('fetch', event => {
  const request = event.request, url = new URL(request.url);
  const pathname = url.pathname === '/' ? '/index.html' : url.pathname;
  if (request.method !== 'GET' || url.origin !== self.location.origin || url.search ||
      request.headers.has('Authorization') || request.headers.has('X-Qfm-Connectivity-Probe') || !staticPaths.has(pathname)) return;
  // Every controlled document uses one complete revision, even while a new worker waits.
  // Never mix current network HTML/modules with a previous revision's fallback assets.
  event.respondWith((async () => {
    const response = await (await caches.open(cacheName)).match(pathname);
    if (!response) throw new Error('Offline shell asset unavailable');
    const headers = new Headers(response.headers); headers.set('X-Qfm-Shell-Revision', shell.revision);
    return new Response(response.body, {status:response.status, statusText:response.statusText, headers});
  })());
});
