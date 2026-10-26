// Opt-in inert loopback server: exercises real service workers without ADB or USB.
const test = require('node:test');
const assert = require('node:assert/strict');
const http = require('node:http');
const fs = require('node:fs/promises');
const path = require('node:path');
const { chromium } = require('playwright');
const {renderShell}=require('./pwa-shell-fixture.cjs');
const root = path.resolve(__dirname, '../../src/QuestIonAbleFileManager.BrowserHost/wwwroot');
const allowed = new Set(['index.html','agent.html','app.js','pwa.js','retained-view.js','transfer-view.js','verified-download.js','apk-read-view.js','file-push-view.js','kiosk-view.js','vendor/noble-sha256.js','vendor/NOBLE_HASHES_NOTICES.txt','sw.js','styles.css',
  'usb-proof.js','usb-proof-core.js','vendor/tango-adb.js','manifest.webmanifest','icons/qfm-192.png','icons/qfm-512.png']);

test('installed worker opens offline UI/help; secrets and API data never enter caches; reconnect is inert',
  { skip: !process.env.QFM_BROWSER_UI_PORT }, async t => {
  const port = Number(process.env.QFM_BROWSER_UI_PORT);
  assert.ok(Number.isInteger(port) && port > 1024 && port <= 65535, 'Use an explicitly reserved port');
  const origin = `http://127.0.0.1:${port}`, calls = [], errors = [];
  let shell=await renderShell();
  const server = http.createServer(async (request, response) => {
    response.setHeader('Cache-Control','no-store');
    const url = new URL(request.url, origin);
    if (url.pathname.startsWith('/browser/')) {
      calls.push({ method:request.method, path:url.pathname });
      response.setHeader('Content-Type','application/json');
      response.end(JSON.stringify({ sessionId:'synthetic-pwa-session', privateMarker:'fixture-api-data' })); return;
    }
    const relative = url.pathname === '/' ? 'index.html' : url.pathname.slice(1);
    if (!allowed.has(relative) || url.search) {response.writeHead(404).end();return;}
    const type = relative.endsWith('.js') ? 'text/javascript' : relative.endsWith('.css') ? 'text/css' :
      relative.endsWith('.png') ? 'image/png' : relative.endsWith('.webmanifest') ? 'application/manifest+json' : 'text/html';
    try {response.setHeader('Content-Type',type);response.end(relative==='sw.js' ? shell.worker : shell.bytes.get('/'+relative));}
    catch {response.writeHead(404).end();}
  });
  await new Promise((resolve,reject) => {server.once('error',reject);server.listen(port,'127.0.0.1',resolve);});
  t.after(() => new Promise(resolve => {server.closeAllConnections();server.close(resolve);}));
  const browser = await chromium.launch({headless:true}); t.after(() => browser.close());
  const context = await browser.newContext({viewport:{width:1280,height:900}});
  const page = await context.newPage(); page.on('pageerror', error => errors.push(error.message));
  page.on('console', message => {if (['error','warning'].includes(message.type()) && !/net::ERR_(INTERNET_DISCONNECTED|CONNECTION_REFUSED)/.test(message.text())) errors.push(message.text());});
  await page.goto(origin); await page.evaluate(() => navigator.serviceWorker.ready.then(() => true));
  await page.reload(); await page.waitForFunction(() => Boolean(navigator.serviceWorker.controller));
  assert.equal(await page.title(),'QFM Browser'); assert.equal(calls.length,0,'No discovery/session on load');
  await page.locator('#token').fill('synthetic-token-never-cache'); await page.locator('#connect').click();
  await page.waitForFunction(() => document.getElementById('host-status').textContent === 'Connected');
  assert.equal(calls.length,1);
  const cdp = await context.newCDPSession(page);
  const installability = await cdp.send('Page.getInstallabilityErrors');
  assert.deepEqual(installability.installabilityErrors,[], 'Chromium manifest/installability checks');
  // A new server revision must wait, and cannot leak individual new assets into this page.
  const original={requestId:'00000000-0000-4000-8000-000000000002',sessionId:'synthetic-pwa-session',action:'device.normal',target:'FIXTURE-UNRESOLVED'};
  await page.evaluate(value=>sessionStorage.setItem('qfm-browser-original-v1',JSON.stringify(value)),original);
  shell=await renderShell({
    '/index.html':Buffer.from(shell.bytes.get('/index.html').toString().replace('<title>QFM Browser</title>','<title>QFM Browser update</title>')),
    '/app.js':Buffer.concat([shell.bytes.get('/app.js'),Buffer.from('\nglobalThis.qfmFixtureRevision="updated";')])
  });
  const newWorkerEvent=context.waitForEvent('serviceworker');
  await page.evaluate(async()=>{const registration=await navigator.serviceWorker.getRegistration();await registration.update();});
  const newWorker=await newWorkerEvent;
  await page.waitForFunction(async()=>Boolean((await navigator.serviceWorker.getRegistration()).waiting));
  await page.reload();
  assert.equal(await page.title(),'QFM Browser','Waiting worker cannot replace active HTML');
  assert.equal(await page.evaluate(()=>globalThis.qfmFixtureRevision),undefined,'Waiting worker cannot replace active modules');
  assert.deepEqual(await page.evaluate(()=>JSON.parse(sessionStorage.getItem('qfm-browser-original-v1'))),original);
  await context.setOffline(true); await page.waitForFunction(() => document.getElementById('network-status').textContent.startsWith('Offline'));
  assert.equal(await page.locator('#refresh').isDisabled(),true);
  await new Promise(resolve => {server.closeAllConnections();server.close(resolve);});
  await page.reload();
  await page.waitForFunction(() => document.getElementById('network-status').textContent.startsWith('Saved offline shell'));
  assert.equal(await page.title(),'QFM Browser');
  assert.equal(await page.evaluate(()=>globalThis.qfmFixtureRevision),undefined,'Offline reload remains on one old revision');
  assert.deepEqual(await page.evaluate(()=>JSON.parse(sessionStorage.getItem('qfm-browser-original-v1'))),original);
  assert.equal(await page.locator('#token').inputValue(),''); assert.equal(await page.locator('#refresh').isDisabled(),true);
  await page.locator('summary').filter({hasText:'Install and offline help'}).click();
  assert.equal(await page.locator('#install-help').isVisible(),true);
  const cacheEvidence = await page.evaluate(async () => {
    const result=[];
    for (const name of await caches.keys()) {
      const cache=await caches.open(name);
      for (const request of await cache.keys()) {
        const response=await cache.match(request);
        result.push({path:new URL(request.url).pathname, authorization:request.headers.get('Authorization'),
          privateData: /synthetic-token-never-cache|fixture-api-data/.test(await response.clone().text())});
      }
    }
    return result;
  });
  assert.ok(cacheEvidence.length > 5);
  assert.ok(cacheEvidence.every(item => !item.path.startsWith('/browser/') && !item.authorization && !item.privateData));
  if (process.env.QFM_BROWSER_UI_EVIDENCE) {
    await fs.mkdir(process.env.QFM_BROWSER_UI_EVIDENCE,{recursive:true});
    await page.screenshot({path:path.join(process.env.QFM_BROWSER_UI_EVIDENCE,'pwa-offline-desktop.png'),fullPage:true});
    await page.setViewportSize({width:390,height:844});
    assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth),true);
    await page.screenshot({path:path.join(process.env.QFM_BROWSER_UI_EVIDENCE,'pwa-offline-mobile.png'),fullPage:true});
  }
  await page.getByRole('link',{name:'Agent guide',exact:true}).click();
  assert.match(await page.title(),/agent guide/); assert.equal(await page.locator('h1').textContent(),'Agent guide');
  await new Promise(resolve => server.listen(port,'127.0.0.1',resolve));
  await context.setOffline(false); await page.goto(origin);
  assert.equal(calls.length,1,'Reload/reconnect never dispatches/reconciles automatically');
  assert.equal(await page.locator('#refresh').isDisabled(),true);
  // Observe normal activation only after every old document closes; never skipWaiting/claim.
  const activated=newWorker.evaluate(()=>new Promise(resolve=>self.addEventListener('activate',()=>resolve(true),{once:true})));
  await page.close(); await activated;
  const upgraded=await context.newPage();
  await upgraded.goto(origin);
  assert.equal(await upgraded.title(),'QFM Browser update');
  assert.equal(await upgraded.evaluate(()=>globalThis.qfmFixtureRevision),'updated');
  await context.setOffline(true); await upgraded.reload();
  assert.equal(await upgraded.title(),'QFM Browser update');
  assert.equal(await upgraded.evaluate(()=>globalThis.qfmFixtureRevision),'updated','Activated revision is complete offline');
  assert.deepEqual(errors,[]);
});
