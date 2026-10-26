// Real DOM regression tests. All URLs are route-fulfilled: no server, ADB or USB.
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs/promises');
const path = require('node:path');
const {createHash}=require('node:crypto');
const { chromium } = require('playwright');

const root = path.resolve(__dirname, '../../src/QuestIonAbleFileManager.BrowserHost/wwwroot');
const originalKey = 'qfm-browser-original-v1';
let browser;
test.before(async () => { browser = await chromium.launch({ headless: true }); });
test.after(async () => { await browser?.close(); });

test('Local APK Save preserves uncertain close and pending cleanup after disconnect',async t=>{
  const f=await fixture(t);await f.connect();
  await f.page.locator('#apk-upload').setInputFiles({name:'fixture.apk',mimeType:'application/octet-stream',buffer:Buffer.from([1,2,3])});
  await f.page.locator('#stage-apk').click();await f.settled();
  await f.page.evaluate(()=>Object.defineProperty(window,'showSaveFilePicker',{configurable:true,value:()=>Promise.resolve({createWritable:()=>Promise.resolve({
    write:async()=>{},close:()=>{window.closeEntered=true;return new Promise(()=>{});},abort:()=>new Promise(()=>{})
  })})}));
  await f.page.locator('#download-artifact').click();await f.page.waitForFunction(()=>window.closeEntered===true);
  await f.page.evaluate(()=>window.dispatchEvent(new Event('offline')));
  await f.page.waitForFunction(()=>document.querySelector('#artifact-state').textContent.includes('failed'));
  const message=await f.page.locator('#artifact-state').textContent();
  assert.match(message,/close outcome is unknown/i);assert.match(message,/cleanup.*pending/i);assert.match(message,/check the selected file before another save/i);
  assert.equal(f.calls.filter(call=>call.method==='GET'&&call.path.startsWith('/browser/v1/artifacts/')).length,1);
  assert.equal(f.calls.filter(call=>call.method==='DELETE'||call.path.endsWith('/cleanup')).length,0);
  assert.equal(await f.page.locator('#token').inputValue(),'');
});

async function fixture(t, { origin = 'http://127.0.0.1:18127', mutation, session, files, expectedLostResponse = false, blockApp = false } = {}) {
  const context = await browser.newContext({ serviceWorkers: 'block', viewport: { width: 1280, height: 900 } });
  // Exercise the bounded fallback here. Picker/streaming behavior has its own
  // real-DOM suite with explicit destination handles and synthetic byte streams.
  await context.addInitScript(() => Object.defineProperty(window,'showSaveFilePicker',{value:undefined,configurable:true}));
  t.after(() => context.close());
  const page = await context.newPage(), calls = [], errors = [], consoleProblems = [], blocked = [], retained = new Map();
  page.on('pageerror', error => errors.push(error.message));
  page.on('console', message => {
    if (['error', 'warning'].includes(message.type())) consoleProblems.push(message.text());
  });
  const devices = [
    { serial: 'FIXTURE-A', model: 'Fixture Quest A', state: 'device', isReady: true, isWifiConnection: false },
    { serial: 'FIXTURE-B', model: 'Fixture Quest B', state: 'device', isReady: true, isWifiConnection: false }
  ];
  const completed = request => ({ requestId: request.requestId, action: request.action, state: 'completed', result: {
    command: { serial: request.serial }, mutationReceipt: { stage: 'confirmed', observedState: 'fixture readback' }
  } });
  let reconcile = requestId => retained.get(requestId);
  await context.route('**/*', async route => {
    const url = new URL(route.request().url());
    // Interception applies to every origin, including accidental external URLs.
    if (url.origin !== origin) return route.abort('blockedbyclient');
    if (blockApp && url.pathname === '/app.js') { blocked.push(url.pathname); return route.abort('blockedbyclient'); }
    if (url.pathname.startsWith('/browser/')) {
      const request = route.request(), body = request.method() === 'POST' && request.headers()['content-type']?.startsWith('application/json') ? request.postDataJSON() : null;
      calls.push({ method: request.method(), path: url.pathname, body });
      if (url.pathname === '/browser/v1/session') return session ? session(route) : route.fulfill({ json: { sessionId: 'fixture-session', artifactStaging:{supported:true}, actions:['devices','device.status','wifi.enable','wifi.connect','device.keep-awake','device.normal','files.list','apk.list','apk.permissions','device.performance','device.performance-clear','wifi.disconnect','device.actor-observe','device.actor-identity','adb.forwards','apk.inspect'] } });
      const artifact={id:'00000000000040008000000000000003',fileName:'fixture.apk',sizeBytes:3,sha256:createHash('sha256').update(Buffer.from([1,2,3])).digest('hex')};
      if(url.pathname==='/browser/v1/artifacts'&&request.method()==='POST')return route.fulfill({status:201,json:{state:'staged',scope:'localOnly',deviceDispatched:false,artifact}});
      if(url.pathname.startsWith('/browser/v1/artifacts/')&&request.method()==='DELETE')return route.fulfill({json:{state:'removed',scope:'localOnly',deviceDispatched:false}});
      if(url.pathname.startsWith('/browser/v1/artifacts/')&&request.method()==='GET')return route.fulfill({contentType:'application/octet-stream',headers:{'Content-Length':'3','X-Qfm-Artifact-Sha256':artifact.sha256,'X-Qfm-Artifact-Size-Bytes':'3'},body:Buffer.from([1,2,3])});
      if(url.pathname==='/browser/v1/artifacts/cleanup')return route.fulfill({json:{state:'cleaned',count:1,scope:'localOnly',deviceDispatched:false}});
      if (url.pathname.startsWith('/browser/v1/requests/')) return route.fulfill({ json: reconcile(url.pathname.split('/').at(-1)) || { error: 'request_not_found' } });
      if (url.pathname === '/browser/v1/execute') {
        if(body.action==='apk.inspect')return route.fulfill({json:{requestId:body.requestId,action:body.action,state:'completed',artifactInspection:{artifact,inspection:{path:artifact.fileName,sizeBytes:artifact.sizeBytes,sha256:artifact.sha256,identity:{packageName:'example.fixture.app',versionCode:1,versionName:'1.0',signerSha256:'a'.repeat(64),splitName:null}}}}});
        if(body.action==='files.list'){const result={requestId:body.requestId,state:'completed',result:{command:{serial:body.serial},remoteEntries:[{name:'Download',fullPath:'/sdcard/Download',isDirectory:true},{name:'<script>fixture</script>.txt',fullPath:'/sdcard/fixture.txt',isDirectory:false}]}};return files ? files(route,result) : route.fulfill({json:result});}
        if(body.action==='apk.list')return route.fulfill({json:{requestId:body.requestId,state:'completed',result:{command:{serial:body.serial},packages:['example.fixture.app']}}});
        if(body.action==='apk.permissions')return route.fulfill({json:{requestId:body.requestId,state:'completed',result:{command:{serial:body.serial},apkPermissionObservation:{packageName:body.package}}}});
        if (body.action === 'devices') return route.fulfill({ json: { requestId: body.requestId, state: 'completed', result: { devices } } });
        if (body.action === 'device.status') return route.fulfill({ json: { requestId: body.requestId, state: 'completed', result: {
          command: { serial: body.serial }, questControlStatus: { wakefulness: 'Awake', displayState: 'ON', proximityState: 'CLOSE', capturedAt: '2026-01-01T00:00:00Z' }
        } } });
        const outcome = completed(body); retained.set(body.requestId, outcome);
        if (mutation) return mutation({ route, body, outcome, retained });
        return route.fulfill({ json: outcome });
      }
      return route.abort('blockedbyclient');
    }
    const allowed = new Set(['index.html', 'app.js', 'pwa.js', 'retained-view.js', 'transfer-view.js', 'verified-download.js', 'apk-read-view.js','file-push-view.js','kiosk-view.js','direct-connection-view.js','profile-view.js', 'vendor/noble-sha256.js', 'styles.css', 'usb-proof.js', 'usb-proof-core.js', 'vendor/tango-adb.js', 'manifest.webmanifest']);
    const relative = url.pathname === '/' ? 'index.html' : url.pathname.slice(1);
    if (!allowed.has(relative)) return route.fulfill({ status: 404, body: '' });
    const contentType = relative.endsWith('.js') ? 'text/javascript' : relative.endsWith('.css') ? 'text/css' : 'text/html';
    return route.fulfill({ contentType, body: await fs.readFile(path.join(root, relative)) });
  });
  await page.goto(origin + '/');
  if (!blockApp) await page.waitForFunction(() => !document.querySelector('#usb-capability').textContent.includes('Checking'));
  assert.equal(await page.title(), 'QFM Browser');
  assert.equal(await page.locator('h1').textContent(), 'Manage your Quest');
  assert.equal(await page.locator('nextjs-portal, vite-error-overlay').count(), 0);
  async function connect() {
    await page.locator('#token').fill('fixture-only-not-a-real-credential');
    await page.locator('#connect').click();
    await page.waitForFunction(() => document.querySelector('#host-status').textContent === 'Connected');
  }
  async function selectA() {
    await page.locator('#refresh').click();
    await page.locator('.device-button').filter({ hasText: 'FIXTURE-A' }).click();
    await page.waitForFunction(() => document.querySelector('#power-value').textContent === 'Awake');
  }
  async function hold() { await page.locator('#power-confirm').check(); await page.locator('#apply-hold').click(); }
  async function settled() { await page.waitForFunction(() => !document.querySelector('#refresh').disabled); }
  const outstanding = () => page.evaluate(key => JSON.parse(sessionStorage.getItem(key) || 'null'), originalKey);
  const mutations = () => calls.filter(call => call.body && !['devices', 'device.status','files.list','apk.list','apk.permissions','device.actor-observe','device.actor-identity','adb.forwards','apk.inspect'].includes(call.body.action));
  t.after(() => assert.deepEqual(errors, [], 'No uncaught frontend runtime errors'));
  t.after(() => assert.deepEqual(consoleProblems.filter(message =>
    !message.includes('Service Worker registration blocked by Playwright') &&
    !(expectedLostResponse && message.includes('net::ERR_FAILED')) &&
    !(blockApp && message.includes('net::ERR_BLOCKED_BY_CLIENT')) &&
    !message.includes('status of 409')), [], 'No unexpected console errors or warnings'));
  return { page, calls, blocked, connect, selectA, hold, settled, outstanding, mutations, completed,
    setReconcile: callback => { reconcile = callback; } };
}

test('definite preadmission rejection clears original and permits a separately approved new change', async t => {
  let first = true;
  const f = await fixture(t, { mutation: ({ route, outcome }) => {
    if (first) { first = false; return route.fulfill({ status: 409, json: { error: 'busy', dispatched: false } }); }
    return route.fulfill({ json: outcome });
  } });
  await f.connect(); await f.selectA(); await f.hold(); await f.settled();
  assert.match(await f.page.locator('#result-summary').textContent(), /not dispatched.*busy/);
  assert.equal(await f.outstanding(), null);
  await f.hold(); await f.settled();
  assert.equal(await f.outstanding(), null);
  assert.equal(f.mutations().length, 2);
  assert.notEqual(f.mutations()[0].body.requestId, f.mutations()[1].body.requestId);
});

test('offline clears credentials and live state, preserves uncertain original and reconnect never replays', async t => {
  const f = await fixture(t, { mutation: ({ route }) => route.abort('failed'), expectedLostResponse: true });
  await f.connect(); await f.selectA(); await f.hold(); await f.settled();
  const original = await f.outstanding(), count = f.calls.length;
  await f.page.evaluate(() => window.dispatchEvent(new Event('offline')));
  assert.match(await f.page.locator('#network-status').textContent(), /Offline/);
  assert.equal(await f.page.locator('#refresh').isDisabled(), true);
  assert.equal(await f.page.locator('#connect').isDisabled(), false, 'An explicit loopback connection remains possible');
  assert.equal(await f.page.locator('#token').inputValue(), '');
  assert.equal(await f.page.locator('#power-value').textContent(), 'Unknown');
  assert.equal((await f.outstanding()).requestId, original.requestId);
  await f.page.evaluate(() => window.dispatchEvent(new Event('online')));
  assert.equal(await f.page.locator('#refresh').isDisabled(), true);
  assert.equal(f.calls.length, count, 'Connectivity events cannot execute or reconcile commands');
  assert.equal((await f.outstanding()).requestId, original.requestId);
});

test('late session response cannot reconnect a tab after offline', async t => {
  let release;
  const f=await fixture(t,{session:route => new Promise(resolve => {release=async()=>{await route.fulfill({json:{sessionId:'fixture-session'}});resolve();};})});
  await f.page.locator('#token').fill('fixture-only-not-a-real-credential'); await f.page.locator('#connect').click();
  await f.page.waitForFunction(() => document.querySelector('#connect').disabled);
  await f.page.evaluate(() => window.dispatchEvent(new Event('offline')));
  assert.ok(release); await release();
  await f.page.evaluate(() => window.dispatchEvent(new Event('online')));
  await f.page.waitForFunction(() => !document.querySelector('#connect').disabled);
  assert.equal(await f.page.locator('#host-status').textContent(),'Offline — disconnected');
  assert.equal(await f.page.locator('#refresh').isDisabled(),true);
  assert.equal(f.calls.length,1);
});

test('network-offline signal still allows explicit local USB connector use without automatic requests', async t => {
  const f=await fixture(t);
  await f.page.evaluate(() => window.dispatchEvent(new Event('offline')));
  assert.equal(f.calls.length,0);
  await f.connect(); await f.selectA();
  assert.equal(await f.page.locator('#power-value').textContent(),'Awake');
  assert.equal(f.mutations().length,0);
});

test('file and package views use typed read routes, render literal names and clear on target change', async t => {
  const f=await fixture(t); await f.connect(); await f.selectA();
  await f.page.locator('#browse-files').click(); await f.settled();
  assert.ok(f.calls.some(call=>call.body?.action==='files.list' && call.body.path==='/sdcard'));
  assert.match(await f.page.locator('#file-entries').textContent(),/<script>fixture<\/script>.txt/);
  assert.equal(await f.page.locator('#file-entries script').count(),0);
  await f.page.getByRole('button',{name:'Open Download',exact:true}).click(); await f.settled();
  assert.equal(f.calls.at(-1).body.path,'/sdcard/Download');
  await f.page.locator('#list-packages').click(); await f.settled();
  await f.page.getByRole('button',{name:'example.fixture.app',exact:true}).click();
  await f.page.locator('#read-permissions').click(); await f.settled();
  assert.equal(f.calls.at(-1).body.package,'example.fixture.app');
  assert.equal(f.mutations().length,0);
  await f.page.locator('.device-button').filter({hasText:'FIXTURE-B'}).click(); await f.settled();
  assert.equal(await f.page.locator('#file-entries').textContent(),'');
  assert.equal(await f.page.locator('#package-name').inputValue(),'');
});

test('performance and disconnect require fresh scoped approvals and preserve typed fields', async t => {
  const f=await fixture(t); await f.connect(); await f.selectA();
  assert.equal(await f.page.locator('#apply-performance').isDisabled(),true);
  await f.page.locator('#power-confirm').check(); await f.page.locator('#cpu-level').selectOption('3');
  assert.equal(await f.page.locator('#power-confirm').isChecked(),false);
  await f.page.locator('#gpu-level').selectOption('2'); await f.page.locator('#power-confirm').check();
  await f.page.locator('#apply-performance').click(); await f.settled();
  assert.equal(f.calls.at(-1).body.action,'device.performance'); assert.equal(f.calls.at(-1).body.cpu,3); assert.equal(f.calls.at(-1).body.gpu,2);
  assert.equal(await f.page.locator('#power-confirm').isChecked(),false);
  await f.page.locator('#wifi-host').fill('192.0.2.20');await f.page.locator('#wifi-confirm').check();
  await f.page.locator('#wifi-disconnect').click();await f.settled();
  assert.equal(f.calls.at(-1).body.action,'wifi.disconnect');assert.equal(f.calls.at(-1).body.serial,'192.0.2.20:5555');
});

test('late folder result stays bound to its original target after selection changes', async t => {
  let release;
  const f=await fixture(t,{files:(route,result)=>new Promise(resolve=>{release=async()=>{await route.fulfill({json:result});resolve();};})});
  await f.connect();await f.selectA();await f.page.locator('#browse-files').click();
  await f.page.locator('.device-button').filter({hasText:'FIXTURE-B'}).click();
  assert.match(await f.page.locator('#selection').textContent(),/FIXTURE-B/);
  assert.ok(release);await release();await f.settled();
  assert.equal(await f.page.locator('#file-entries').textContent(),'');
});

test('disconnect and host-session replacement clear approvals and old target views', async t => {
  const f=await fixture(t);await f.connect();await f.selectA();await f.page.locator('#browse-files').click();await f.settled();
  await f.page.locator('#wifi-confirm').check();await f.page.locator('#power-confirm').check();
  await f.page.locator('#disconnect-host').click();
  assert.equal(await f.page.locator('#wifi-confirm').isChecked(),false);assert.equal(await f.page.locator('#power-confirm').isChecked(),false);
  assert.equal(await f.page.locator('#file-entries').textContent(),'');
  await f.connect();assert.equal(await f.page.locator('#refresh').isDisabled(),false);assert.equal(await f.page.locator('#file-entries').textContent(),'');
});

test('local APK upload and inspection use an opaque binding without selecting or mutating a headset', async t => {
  const f=await fixture(t);await f.connect();
  assert.equal(await f.page.locator('#apk-upload').count(),1);
  await f.page.locator('#apk-upload').setInputFiles({name:'fixture.apk',mimeType:'application/vnd.android.package-archive',buffer:Buffer.from([1,2,3])});
  await f.page.locator('#stage-apk').click();await f.settled();
  assert.match(await f.page.locator('#artifact-state').textContent(),/staged/i);
  await f.page.locator('#inspect-apk').click();await f.settled();
  assert.equal(await f.page.locator('#apk-package').textContent(),'example.fixture.app');
  const inspection=f.calls.find(call=>call.body?.action==='apk.inspect').body;
  assert.deepEqual(Object.keys(inspection).sort(),['requestId','action','artifactId','sha256','sizeBytes'].sort());
  assert.equal(inspection.artifactId,'00000000000040008000000000000003');assert.equal(f.mutations().length,0);
  assert.equal(f.calls.some(call=>call.body?.action==='devices'),false);
  await f.page.setViewportSize({width:390,height:844});
  assert.equal(await f.page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth),true,'Staged identities fit mobile width');
  if(process.env.QFM_BROWSER_UI_EVIDENCE)await f.page.screenshot({path:path.join(process.env.QFM_BROWSER_UI_EVIDENCE,'artifact-inspection-mobile.png'),fullPage:true});
  await f.page.locator('#remove-artifact').click();await f.settled();
  assert.equal(await f.page.locator('#inspect-apk').isDisabled(),true);
  assert.equal(await f.page.locator('#apk-package').textContent(),'Unknown');
});

test('staged download checks bytes and unsupported hosts give an explicit alternative', async t => {
  const f=await fixture(t);await f.connect();
  await f.page.locator('#apk-upload').setInputFiles({name:'fixture.apk',mimeType:'application/octet-stream',buffer:Buffer.from([1,2,3])});
  await f.page.locator('#stage-apk').click();await f.settled();
  const downloadEvent=f.page.waitForEvent('download');await f.page.locator('#download-artifact').click();
  const download=await downloadEvent;assert.equal(download.suggestedFilename(),'fixture.apk');
  assert.deepEqual(await fs.readFile(await download.path()),Buffer.from([1,2,3]));await f.settled();
  const limited=await fixture(t,{session:route=>route.fulfill({json:{sessionId:'fixture-session',artifactStaging:{supported:false,reason:'Protected path staging unavailable.',alternative:'Use the Windows companion.'}}})});
  await limited.connect();assert.equal(await limited.page.locator('#apk-upload').isDisabled(),true);
  assert.match(await limited.page.locator('#artifact-capability').textContent(),/Windows companion/);
  await limited.page.locator('#artifact-form').dispatchEvent('submit');assert.equal(limited.calls.length,1);
});

test('lost response retains original across reload, blocks replay and reconciles by GET only', async t => {
  const f = await fixture(t, { mutation: ({ route }) => route.abort('failed'), expectedLostResponse: true });
  await f.connect(); await f.selectA(); await f.hold(); await f.settled();
  const original = await f.outstanding(); assert.ok(original?.requestId);
  assert.match(await f.page.locator('#result-summary').textContent(), /Outcome unknown/);
  await f.page.locator('#power-confirm').check();
  assert.equal(await f.page.locator('#apply-hold').isDisabled(), true);
  await f.page.locator('#apply-hold').dispatchEvent('click');
  assert.equal(f.mutations().length, 1);
  await f.page.reload(); await f.page.locator('#token').waitFor(); await f.connect();
  assert.equal((await f.outstanding()).requestId, original.requestId);
  f.setReconcile(() => ({ requestId: original.requestId, state: 'pending' }));
  await f.page.locator('#reconcile').click(); await f.settled();
  assert.equal((await f.outstanding()).requestId, original.requestId);
  f.setReconcile(() => f.completed(f.mutations()[0].body));
  await f.page.locator('#reconcile').click(); await f.settled();
  assert.equal(await f.outstanding(), null);
  assert.equal(f.mutations().length, 1, 'Reconciliation never repeats POST');
});

for (const matches of [true, false]) test(`Wi-Fi completion ${matches ? 'clears an exact ready endpoint' : 'retains a mismatched endpoint'}`, async t => {
  const f = await fixture(t, { mutation: ({ route, body }) => route.fulfill({ json: {
    requestId: body.requestId, state: 'completed', result: { wifiAdbConnectionResult: {
      endpoint: matches ? body.serial : '192.0.2.99:5555', device: { serial: body.serial, isReady: true }
    } }
  } }) });
  await f.connect();
  await f.page.locator('#wifi-host').fill('192.0.2.20'); await f.page.locator('#wifi-confirm').check(); await f.page.locator('#wifi-connect').click(); await f.settled();
  assert.equal(Boolean(await f.outstanding()), !matches);
  assert.equal(f.mutations().length, 1);
});

test('changed target, proximity duration and Wi-Fi endpoint reset their matching confirmation', async t => {
  const f = await fixture(t); await f.connect(); await f.selectA();
  await f.page.locator('#power-confirm').check(); await f.page.locator('#duration').selectOption('60000');
  assert.equal(await f.page.locator('#power-confirm').isChecked(), false);
  await f.page.locator('#power-confirm').check(); await f.page.locator('#wifi-confirm').check();
  await f.page.locator('.device-button').filter({ hasText: 'FIXTURE-B' }).click(); await f.settled();
  assert.equal(await f.page.locator('#power-confirm').isChecked(), false);
  assert.equal(await f.page.locator('#wifi-confirm').isChecked(), false);
  await f.page.locator('#wifi-host').fill('192.0.2.20'); await f.page.locator('#wifi-confirm').check(); await f.page.locator('#wifi-port').fill('5556');
  assert.equal(await f.page.locator('#wifi-confirm').isChecked(), false);
  assert.equal(f.mutations().length, 0);
  if (process.env.QFM_BROWSER_UI_EVIDENCE) {
    await fs.mkdir(process.env.QFM_BROWSER_UI_EVIDENCE, { recursive: true });
    await f.page.screenshot({ path: path.join(process.env.QFM_BROWSER_UI_EVIDENCE, 'fixture-host-desktop.png'), fullPage: true });
  }
});

test('public origin disables credentials and forced form submission cannot reach any host API', async t => {
  const f = await fixture(t, { origin: 'https://qfm-browser.example' });
  assert.equal(await f.page.locator('#token').isDisabled(), true);
  assert.equal(await f.page.locator('#connect').isDisabled(), true);
  assert.equal(await f.page.locator('#local-host-link').isVisible(), true);
  await f.page.locator('#token').evaluate(element => { element.value = 'fixture-secret-never-send'; });
  await f.page.locator('#connect-form').dispatchEvent('submit');
  assert.equal(await f.page.locator('#token').inputValue(), '');
  await f.page.locator('#refresh').dispatchEvent('click');
  await f.page.locator('#wifi-form').dispatchEvent('submit');
  assert.deepEqual(f.calls, [], 'No session, execution or readback API on public origin');
  assert.equal(await f.page.evaluate(() => JSON.stringify(sessionStorage).includes('fixture-secret-never-send')), false);
  if (process.env.QFM_BROWSER_UI_EVIDENCE) {
    await f.page.setViewportSize({ width: 390, height: 844 });
    await f.page.screenshot({ path: path.join(process.env.QFM_BROWSER_UI_EVIDENCE, 'fixture-public-mobile.png'), fullPage: true });
  }
});

test('public credential form stays disabled when the application module fails to load', async t => {
  const f = await fixture(t, { origin: 'https://qfm-browser.example', blockApp: true });
  assert.deepEqual(f.blocked, ['/app.js'], 'The real application module was prevented from loading');
  assert.equal(await f.page.locator('#token').isDisabled(), true);
  assert.equal(await f.page.locator('#connect').isDisabled(), true);
  assert.equal(await f.page.locator('#token').inputValue(), '');
  assert.deepEqual(f.calls, [], 'Static markup accepts no credentials or API request without JavaScript');
});

for (const kind of ['original','changed-host-connection','same-host-connection']) test(`real app shares Direct custody gating: ${kind}`,async t=>{
  const f=await fixture(t),connectRequestId='11111111-1111-4111-8111-111111111111';
  const scope={sessionId:kind==='changed-host-connection'?'old-host-session':'fixture-session',connectRequestId,
    usbOperationId:'usb_'+connectRequestId.replaceAll('-',''),serial:'FIXTURE-A',productChannel:'stable'};
  const reference=kind==='original'?{original:{...scope,requestId:connectRequestId,action:'kiosk.direct.connect-usb'},connection:null}:
    {original:null,connection:{...scope,connectionId:'22222222-2222-4222-8222-222222222222',expiresAtUtc:new Date(Date.now()+300000).toISOString()}};
  await f.page.evaluate(value=>sessionStorage.setItem('qfm-browser-direct-connection-v1',JSON.stringify(value)),reference);
  await f.page.reload();assert.equal(f.calls.length,0,'Reload never reconnects or reads an original');
  await f.connect();await f.selectA();await f.page.locator('#power-confirm').check();
  const blocked=kind!=='same-host-connection';assert.equal(await f.page.locator('#apply-hold').isDisabled(),blocked);
  if(blocked){
    assert.match(await f.page.locator('#direct-state').textContent(),/unknown/i);
    // Exercise the execute-time guard as well as disabled markup.
    await f.page.locator('#apply-hold').evaluate(button=>{button.disabled=false;});
    await f.page.locator('#apply-hold').dispatchEvent('click');assert.equal(f.mutations().length,0);
  }else{await f.page.locator('#apply-hold').click();await f.settled();assert.equal(f.mutations().length,1);}
  assert.deepEqual(await f.page.evaluate(()=>JSON.parse(sessionStorage.getItem('qfm-browser-direct-connection-v1'))),reference);
});

for (const kind of ['import','revoke','changed-host-import']) test(`real app preserves unresolved local profile scope: ${kind}`,async t=>{
  const f=await fixture(t),reference={requestId:'33333333-3333-4333-8333-333333333333',
    sessionId:kind==='changed-host-import'?'old-host-session':'fixture-session',
    action:kind==='revoke'?'profile.revoke':'profile.import',deviceId:'fixture-profile',
    ...(kind==='revoke'?{}:{replaceExisting:false})};
  await f.page.evaluate(value=>sessionStorage.setItem('qfm-browser-profile-v1',JSON.stringify(value)),reference);
  await f.page.reload();assert.equal(f.calls.length,0,'No automatic profile/owner request after reload');
  await f.connect();await f.selectA();await f.page.locator('#power-confirm').check();
  assert.equal(await f.page.locator('#apply-hold').isDisabled(),true);
  await f.page.locator('#apply-hold').evaluate(button=>{button.disabled=false;});
  await f.page.locator('#apply-hold').dispatchEvent('click');assert.equal(f.mutations().length,0,'Execute-time guard preserves local profile uncertainty');
  assert.deepEqual(await f.page.evaluate(()=>JSON.parse(sessionStorage.getItem('qfm-browser-profile-v1'))),reference);
  assert.equal(f.calls.some(call=>call.path.includes('/profiles/')||call.body?.action?.startsWith('profile.')),false);
});
