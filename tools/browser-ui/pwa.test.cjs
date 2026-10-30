const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs/promises');
const path = require('node:path');
const vm = require('node:vm');
const { webcrypto } = require('node:crypto');
const { renderShell } = require('./pwa-shell-fixture.cjs');
const root = path.resolve(__dirname, '../../src/QuestIonAbleFileManager.BrowserHost/wwwroot');

async function worker({ mismatch = false, writes = [] } = {}) {
  const shell = await renderShell(), handlers = {}, entries = new Map(), requests = [];
  const cache = {
    match: async request => entries.get(new URL(typeof request === 'string' ? request : request.url, 'http://127.0.0.1:18129').pathname)?.clone(),
    put: async (key,response) => {writes.push(key);entries.set(key,response.clone());} };
  const context = { URL, Request, Response, Headers, crypto: webcrypto,
    self: { location: new URL('http://127.0.0.1:18129/sw.js'), addEventListener: (name, handler) => handlers[name] = handler },
    caches: { open: async () => cache, keys: async () => [], delete: async () => true },
    fetch: async input => {const pathname=new URL(typeof input === 'string' ? input : input.url,'http://127.0.0.1:18129').pathname;requests.push(pathname);return new Response(mismatch && pathname==='/app.js' ? 'wrong revision' : shell.bytes.get(pathname));} };
  vm.runInNewContext(shell.worker, context);
  let installation; handlers.install({ waitUntil: value => installation = value }); await installation;
  return { handlers, entries, writes, requests };
}

test('worker verifies every exact shell byte before publishing a versioned cache', async () => {
  await worker();
  const writes=[];
  await assert.rejects(worker({mismatch:true,writes}),/hash|integrity|revision/i);
  assert.deepEqual(writes,[], 'An incomplete revision cannot enter the shell cache');
});

test('all shell bytes and worker logic determine revision; unrendered template fails closed', async () => {
  const old=await renderShell(),next=await renderShell({'/app.js':Buffer.from('upgraded shell')}),fixed=await renderShell({},'\n// worker fix');
  assert.notEqual(old.revision,next.revision);assert.notEqual(old.revision,fixed.revision);
  const changedSet=await renderShell({'/apk-set-view.js':Buffer.from('changed APK-set module')});
  assert.notEqual(old.revision,changedSet.revision);
  const changedSingle=await renderShell({'/apk-install-view.js':Buffer.from('changed single APK module')});
  assert.notEqual(old.revision,changedSingle.revision);
  assert.throws(()=>vm.runInNewContext(old.template,{self:{addEventListener(){}}}),/rendered|manifest/i);
});

test('offline worker serves only shipped static assets and never handles private or effect requests', async () => {
  const { handlers, entries, writes, requests } = await worker();
  const count = writes.length, networkCount = requests.length;
  assert.ok(entries.has('/index.html')); assert.ok(entries.has('/agent.html'));
  assert.equal(await entries.get('/apk-set-view.js').clone().text(),await fs.readFile(path.join(root,'apk-set-view.js'),'utf8'));
  assert.match(await entries.get('/index.html').clone().text(),/id="apk-set-view"/);
  assert.equal(await entries.get('/apk-install-view.js').clone().text(),await fs.readFile(path.join(root,'apk-install-view.js'),'utf8'));
  assert.match(await entries.get('/index.html').clone().text(),/id="apk-install-view"/);
  for (const input of [
    ['http://127.0.0.1:18129/browser/v1/session', {}],
    ['http://127.0.0.1:18129/browser/v1/execute', { method: 'POST', body: '{}' }],
    ['http://127.0.0.1:18129/browser/v1/artifacts/receipt-id', {}],
    ['http://127.0.0.1:18129/browser/v1/requests/original-id', {}],
    ['http://127.0.0.1:18129/index.html?token=secret', {}],
    ['http://127.0.0.1:18129/app.js', { headers: { Authorization: 'Bearer private' } }],
    ['http://elsewhere.example/app.js', {}], ['http://127.0.0.1:18129/private.apk', {}],
    ['http://127.0.0.1:18129/manifest.webmanifest', { headers: {'X-Qfm-Connectivity-Probe':'1'} }]
  ]) {
    let intercepted = false;
    handlers.fetch({ request: new Request(...input), respondWith: () => intercepted = true });
    assert.equal(intercepted, false, input[0]);
  }
  let offlinePage;
  handlers.fetch({ request: new Request('http://127.0.0.1:18129/'), respondWith: value => offlinePage = value });
  const page = await offlinePage;
  assert.equal(await page.text(),await entries.get('/index.html').clone().text());
  assert.equal(writes.length, count, 'Runtime responses never enter CacheStorage');
  assert.equal(requests.length, networkCount, 'Runtime static assets cannot mix with a later server revision');
  assert.equal(handlers.sync, undefined); assert.equal(handlers.periodicsync, undefined);
});

test('manifest uses real install icons and a same-origin standalone entry', async () => {
  const manifest = JSON.parse(await fs.readFile(path.join(root, 'manifest.webmanifest'), 'utf8'));
  assert.equal(manifest.start_url, '/'); assert.equal(manifest.display, 'standalone');
  assert.equal(manifest.id, '/'); assert.ok(manifest.name);
  for (const size of [192, 512]) {
    const icon = manifest.icons.find(icon => icon.sizes === `${size}x${size}`);
    assert.ok(icon); const bytes = await fs.readFile(path.join(root, icon.src));
    assert.equal(bytes.readUInt32BE(16), size); assert.equal(bytes.readUInt32BE(20), size);
  }
});
