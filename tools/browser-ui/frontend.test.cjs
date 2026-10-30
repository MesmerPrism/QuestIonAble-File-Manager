// Real DOM regression tests. All URLs are route-fulfilled: no server, ADB or USB.
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs/promises');
const path = require('node:path');
const {createHash}=require('node:crypto');
const { chromium } = require('playwright');

const root = path.resolve(__dirname, '../../src/QuestIonAbleFileManager.BrowserHost/wwwroot');
const originalKey = 'qfm-browser-original-v1';
const diagnosticsKey = 'qfm-browser-diagnostics-original-v1';
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

async function fixture(t, { origin = 'http://127.0.0.1:18127', mutation, session, files, diagnostics, expectedLostResponse = false, blockApp = false } = {}) {
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
      if (url.pathname.startsWith('/browser/v3/retained/') && diagnostics) return diagnostics({route,body,path:url.pathname});
      if (url.pathname === '/browser/v1/session') return session ? session(route) : route.fulfill({ json: { sessionId: 'fixture-session', artifactStaging:{supported:true}, actions:['devices','device.status','wifi.enable','wifi.connect','device.keep-awake','device.normal','files.list','apk.list','apk.permissions','device.performance','device.performance-clear','wifi.disconnect','device.actor-observe','device.actor-identity','adb.forwards','apk.inspect'] } });
      const artifact={id:'00000000000040008000000000000003',fileName:'fixture.apk',sizeBytes:3,sha256:createHash('sha256').update(Buffer.from([1,2,3])).digest('hex')};
      if(url.pathname==='/browser/v1/artifacts'&&request.method()==='POST')return route.fulfill({status:201,json:{state:'staged',scope:'localOnly',deviceDispatched:false,artifact}});
      if(url.pathname.startsWith('/browser/v1/artifacts/')&&request.method()==='DELETE')return route.fulfill({json:{state:'removed',scope:'localOnly',artifactId:url.pathname.split('/').at(-1),deviceDispatched:false}});
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
    const allowed = new Set(['index.html', 'app.js', 'pwa.js', 'retained-view.js', 'transfer-view.js', 'verified-download.js', 'apk-read-view.js','file-push-view.js','kiosk-view.js','direct-connection-view.js','direct-command-view.js','profile-view.js','fleet-status-view.js','diagnostics-view.js','direct-tags-view.js','direct-staging-view.js','direct-upload-view.js','direct-install-view.js','package-stop-view.js', 'vendor/noble-sha256.js', 'styles.css', 'usb-proof.js', 'usb-proof-core.js', 'vendor/tango-adb.js', 'manifest.webmanifest']);
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

for (const kind of ['command-only','other-original','changed-host-command','crossed-connection']) test(`real app limits Direct command cleanup exemption: ${kind}`,async t=>{
  const directActions=['kiosk.direct.connect-usb','kiosk.direct.status','kiosk.direct.close','kiosk.direct.invoke','kiosk.direct.request-status','kiosk.direct.request-result','kiosk.direct.request-cancel'];
  const f=await fixture(t,{session:route=>route.fulfill({json:{sessionId:'fixture-session',actions:['devices','device.status','device.keep-awake',...directActions]}})});
  const connectRequestId='11111111-1111-4111-8111-111111111111',connectionId='22222222-2222-4222-8222-222222222222',requestId='33333333-3333-4333-8333-333333333333';
  const connection={sessionId:'fixture-session',connectRequestId,usbOperationId:'usb_'+connectRequestId.replaceAll('-',''),serial:'FIXTURE-A',productChannel:'stable',connectionId,expiresAtUtc:new Date(Date.now()+300000).toISOString()};
  const original={...connection,requestId,action:'kiosk.direct.invoke',logicalRequestId:'kiosk_'+requestId.replaceAll('-',''),command:'status',value:null};
  delete original.expiresAtUtc;
  if(kind==='changed-host-command')original.sessionId='old-host-session';
  if(kind==='crossed-connection')original.connectionId='44444444-4444-4444-8444-444444444444';
  const commandReference={original,cancellation:null};
  await f.page.evaluate(({connection,commandReference,other})=>{
    sessionStorage.setItem('qfm-browser-direct-connection-v1',JSON.stringify({original:null,connection}));
    sessionStorage.setItem('qfm-browser-direct-command-v1',JSON.stringify(commandReference));
    if(other)sessionStorage.setItem('qfm-browser-original-v1',JSON.stringify({requestId:'55555555-5555-4555-8555-555555555555',sessionId:'fixture-session',action:'device.keep-awake',target:'FIXTURE-A'}));
  },{connection,commandReference,other:kind==='other-original'});
  await f.page.reload();assert.equal(f.calls.length,0,'Reload sends no command or recovery');
  await f.connect();await f.selectA();await f.page.locator('#power-confirm').check();
  assert.equal(await f.page.locator('#apply-hold').isDisabled(),true);
  assert.equal(await f.page.locator('#direct-connect').isDisabled(),true);
  assert.equal(await f.page.locator('#direct-command-invoke').isDisabled(),true);
  const permitsCleanup=kind==='command-only';
  assert.equal(await f.page.locator('#direct-close-confirm').isDisabled(),!permitsCleanup);
  assert.equal(f.calls.filter(call=>call.body?.action?.startsWith('kiosk.direct.')).length,0,'Authentication and global controls stay inert');
  if(permitsCleanup){
    await f.page.locator('#direct-close-confirm').check();await f.page.locator('#direct-close').click();await f.settled();
    const closing=f.calls.filter(call=>call.body?.action==='kiosk.direct.close');assert.equal(closing.length,1);assert.equal(closing[0].body.connectionId,connectionId);
  }else{
    await f.page.locator('#direct-close').evaluate(button=>{button.disabled=false;});await f.page.locator('#direct-close').dispatchEvent('click');
    assert.equal(f.calls.filter(call=>call.body?.action==='kiosk.direct.close').length,0,'Execute-time guard refuses the crossed or additional-owner exemption');
  }
  assert.deepEqual(await f.page.evaluate(()=>JSON.parse(sessionStorage.getItem('qfm-browser-direct-command-v1'))),commandReference,'Cleanup never acknowledges a command');
});

for(const [command,desired] of [['enable-wifi-adb-after-boot',true],['disable-wifi-adb-after-boot',false]])test('real app boot preference recovers only its cached original: '+command,async t=>{
  const commandKey='qfm-browser-direct-command-v1';let connection,savedBeforeDispatch;
  const f=await fixture(t,{expectedLostResponse:true,session:route=>route.fulfill({json:{...directTagsSession(),actions:[...directTagsSession().actions,'kiosk.direct.invoke','kiosk.direct.request-result']}}),mutation:async({route,body,retained})=>{
    if(body.action==='kiosk.direct.connect-usb'){const value=directTagsConnectionReply(body);connection=value.kioskDirectConnection;return route.fulfill({json:value});}
    assert.equal(body.action,'kiosk.direct.invoke');assert.equal(body.command,command);assert.equal(body.value,null);assert.equal(body.confirmed,true);
    savedBeforeDispatch=await f.page.evaluate(key=>JSON.parse(sessionStorage.getItem(key)),commandKey);
    const logicalRequestId='kiosk_'+body.requestId.replaceAll('-','');
    retained.set(body.requestId,{requestId:body.requestId,action:body.action,state:'completed',result:null,mutation:null,kioskDirectCommand:{
      connectionId:connection.connectionId,connectRequestId:connection.connectRequestId,usbOperationId:connection.usbOperationId,serial:connection.serial,productChannel:connection.productChannel,
      operation:'invoke',originalRequestId:body.requestId,logicalRequestId,command,value:null,originalEffect:'confirmed',reconcilesOriginal:true,cancellationAcknowledged:false,lifecycle:null,
      coreReceipt:{actionId:'action_'+body.requestId.replaceAll('-',''),action:'invoke',requestId:logicalRequestId,stage:'confirmed'},
      typedResult:{schema:'rusty.kiosk.cli_result.v1',requestId:logicalRequestId,command,accepted:true,completed:true,controlsOpen:null,selectedKey:null,selectedPackage:null,selectedInstalled:false,selectedLaunchable:false,selectedLaunchRequirement:null,pendingRequirementLaunch:null,guardArmed:false,setupHelperInstalled:false,setupHelperReady:false,selectedEntry:null,requestWifiAdbAfterBoot:desired}}});
    return route.abort('failed');
  }});
  assert.deepEqual(await f.page.locator('#direct-command-command option').evaluateAll(options=>options.slice(9,11).map(option=>option.value)),['enable-wifi-adb-after-boot','disable-wifi-adb-after-boot']);
  await adoptDirectForTags(f);await f.page.locator('#direct-command-command').selectOption(command);
  assert.equal(await f.page.locator('#direct-command-invoke').isDisabled(),true);
  await f.page.locator('#direct-command-invoke-confirm').check();await f.page.locator('#direct-command-invoke').click();await f.settled();
  const original=await f.page.evaluate(key=>JSON.parse(sessionStorage.getItem(key)),commandKey);
  assert.deepEqual(original,savedBeforeDispatch);assert.equal(original.original.command,command);
  assert.equal(await f.page.locator('#apply-hold').isDisabled(),true);
  const other={requestId:'f1234567-1234-4234-8234-123456789abc',sessionId:'fixture-session',action:'device.keep-awake',serial:'FIXTURE-A'};
  await f.page.evaluate(({key,value})=>sessionStorage.setItem(key,JSON.stringify(value)),{key:originalKey,value:other});
  const callsBeforeReload=f.calls.length;await f.page.reload();assert.equal(f.calls.length,callsBeforeReload);await f.connect();
  await f.page.locator('#direct-command-original-status').click();
  await f.page.waitForFunction(key=>sessionStorage.getItem(key)===null,commandKey);
  assert.equal(await f.page.evaluate(key=>sessionStorage.getItem(key),commandKey),null);
  assert.deepEqual(await f.outstanding(),other);assert.equal(await f.page.locator('#apply-hold').isDisabled(),true);
  assert.match(await f.page.locator('#direct-command-state').textContent(),/request-after-restart preference observed/i);
  assert.match(await f.page.locator('#result-summary').textContent(),new RegExp('preference observed.*'+(desired?'on':'off'),'i'));
  assert.equal(f.calls.filter(call=>call.body?.action==='kiosk.direct.invoke').length,1);
  assert.equal(f.calls.filter(call=>call.method==='GET'&&call.path.startsWith('/browser/v1/requests/')).length,1);
  assert.equal(f.calls.some(call=>call.body?.action==='kiosk.direct.request-result'),false);
});

const directControlCases=[
  ['enable-accessibility',{accessibilityEnabled:true},{accessibilityEnabled:false}],
  ['disable-accessibility',{accessibilityEnabled:false},{accessibilityEnabled:true}],
  ['passthrough-natural',{systemPassthroughEnabled:true,passthroughStyle:'natural'},{systemPassthroughEnabled:false,passthroughStyle:'natural'}],
  ['passthrough-contour',{systemPassthroughEnabled:true,passthroughStyle:'contour-lut',passthroughLutApplied:true},{systemPassthroughEnabled:true,passthroughStyle:'contour-lut',passthroughLutApplied:false}],
  ['request-wifi-adb',{wifiAdbEnabled:true},{wifiAdbEnabled:false}],
  ['disable-wifi-adb',{wifiAdbEnabled:false},{wifiAdbEnabled:true}]
];
for(const [command,desired,contrary] of directControlCases)test('real app Direct control preserves pending then reads its original typed result: '+command,async t=>{
  let connection,original;
  const f=await fixture(t,{session:route=>route.fulfill({json:{...directTagsSession(),actions:[...directTagsSession().actions,'kiosk.direct.invoke','kiosk.direct.request-result']}}),mutation:({route,body,retained})=>{
    if(body.action==='kiosk.direct.connect-usb'){const value=directTagsConnectionReply(body);connection=value.kioskDirectConnection;return route.fulfill({json:value});}
    const recovery=body.action==='kiosk.direct.request-result';
    assert.equal(body.action,recovery?'kiosk.direct.request-result':'kiosk.direct.invoke');
    if(!recovery){original={...body};assert.equal(body.command,command);assert.equal(body.value,null);assert.equal(body.confirmed,true);}
    else {assert.equal(body.originalRequestId,original.requestId);assert.equal(body.connectionId,original.connectionId);}
    const logicalRequestId='kiosk_'+original.requestId.replaceAll('-','');
    const value={requestId:body.requestId,action:body.action,state:recovery?'completed':'pending',result:null,mutation:null,kioskDirectCommand:{
      connectionId:connection.connectionId,connectRequestId:connection.connectRequestId,usbOperationId:connection.usbOperationId,serial:connection.serial,productChannel:connection.productChannel,
      operation:recovery?'requestResult':'invoke',originalRequestId:original.requestId,logicalRequestId,command,value:null,originalEffect:recovery?'confirmed':'pendingWearerAction',reconcilesOriginal:recovery,cancellationAcknowledged:false,lifecycle:null,
      coreReceipt:{actionId:'action_'+body.requestId.replaceAll('-',''),action:recovery?'requestResult':'invoke',requestId:logicalRequestId,stage:recovery?'confirmed':'pendingWearerAction'},
      typedResult:{schema:'rusty.kiosk.cli_result.v1',requestId:logicalRequestId,command,accepted:true,completed:true,controlsOpen:null,selectedKey:null,selectedPackage:null,selectedInstalled:false,selectedLaunchable:false,selectedLaunchRequirement:null,pendingRequirementLaunch:null,guardArmed:false,setupHelperInstalled:false,setupHelperReady:false,selectedEntry:null,...(recovery?desired:contrary)}}};
    retained.set(body.requestId,value);return route.fulfill({json:value});
  }});
  assert.deepEqual(await f.page.locator('#direct-command-command option').evaluateAll(options=>options.slice(11).map(option=>option.value)),directControlCases.map(([command])=>command));
  await adoptDirectForTags(f);await f.page.locator('#direct-command-command').selectOption(command);
  if(command==='disable-wifi-adb')assert.match(await f.page.locator('#direct-command-boundary').textContent(),/may immediately close an existing wireless ADB connection/);
  await f.page.locator('#direct-command-invoke-confirm').check();await f.page.locator('#direct-command-invoke').click();await f.settled();
  const pending=await f.page.evaluate(()=>JSON.parse(sessionStorage.getItem('qfm-browser-direct-command-v1')));
  assert.equal(pending.original.requestId,original.requestId);assert.equal(pending.original.command,command);
  assert.equal(await f.page.locator('#direct-command-invoke').isDisabled(),true);assert.equal(await f.page.locator('#apply-hold').isDisabled(),true);
  await f.page.locator('#direct-command-request-result').click();await f.settled();
  assert.equal(await f.page.evaluate(()=>sessionStorage.getItem('qfm-browser-direct-command-v1')),null);
  const shown=JSON.parse(await f.page.locator('#result-json').textContent());
  assert.equal(shown.originalRequestId,original.requestId);assert.equal(shown.command,command);
  for(const [field,value] of Object.entries(desired))assert.equal(shown.typedResult[field],value);
  assert.equal(f.calls.filter(call=>call.body?.action==='kiosk.direct.invoke').length,1);
  assert.equal(f.calls.filter(call=>call.body?.action==='kiosk.direct.request-result').length,1);
});

const fleetSession = route => route.fulfill({json:{sessionId:'fixture-session',actions:['devices','device.status','device.keep-awake','fleet.status'],fleetInstallerStatus:{supported:true}}});
const fleetReply = body => ({requestId:body.requestId,action:'fleet.status',state:'completed',result:null,mutation:null,fleet:{operation:'status',status:{
  schema:'questionable.file_manager.fleet_installer_status.v1',status:'ready',configured:true,sourceKind:'embedded_pages_metadata',product:'rusty-fleet-labs',version:'1.2.3',channel:'labs',descriptorId:'fixture-release',descriptorSha256:'a'.repeat(64),highestHandoffVersion:null,lastOutcome:null,observedAtMs:1770000000000}}});
test('real app Fleet status is explicit, host scoped and independent of a ready device',async t=>{
  const f=await fixture(t,{session:fleetSession,mutation:({route,body})=>route.fulfill({json:fleetReply(body)})});
  assert.equal(await f.page.locator('#fleet-status-read').isDisabled(),true);assert.equal(f.calls.length,0);
  await f.connect();assert.equal(f.calls.length,1);assert.equal(await f.page.locator('#fleet-status-read').isDisabled(),false);
  await f.page.locator('#fleet-status-read').click();await f.settled();
  assert.equal(f.calls.length,2);assert.deepEqual(Object.keys(f.calls[1].body).sort(),['action','requestId']);
  assert.equal(await f.page.locator('#fleet-status-product').textContent(),'rusty-fleet-labs');
  assert.equal(await f.page.locator('#fleet-status-version').textContent(),'1.2.3');
  assert.equal(await f.page.evaluate(()=>Object.keys(sessionStorage).some(key=>key.includes('fleet'))),false);
});
for(const kind of ['generic','profile'])test(`real app Fleet status never acknowledges an unresolved ${kind} write`,async t=>{
  const f=await fixture(t,{session:fleetSession,mutation:({route,body})=>route.fulfill({json:fleetReply(body)})});
  const key=kind==='generic'?originalKey:'qfm-browser-profile-v1';
  const original=kind==='generic'?{requestId:'55555555-5555-4555-8555-555555555555',sessionId:'fixture-session',action:'device.keep-awake',target:'FIXTURE-A'}:
    {requestId:'33333333-3333-4333-8333-333333333333',sessionId:'fixture-session',action:'profile.revoke',deviceId:'fixture-profile'};
  await f.page.evaluate(({key,original})=>sessionStorage.setItem(key,JSON.stringify(original)),{key,original});await f.page.reload();
  assert.equal(f.calls.length,0);await f.connect();await f.selectA();
  assert.equal(await f.page.locator('#fleet-status-read').isDisabled(),false);
  await f.page.locator('#fleet-status-read').click();await f.settled();await f.page.locator('#power-confirm').check();
  assert.equal(await f.page.locator('#apply-hold').isDisabled(),true);
  assert.deepEqual(await f.page.evaluate(key=>JSON.parse(sessionStorage.getItem(key)),key),original);
  assert.equal(f.calls.filter(call=>call.body?.action==='fleet.status').length,1);
  assert.equal(f.calls.filter(call=>call.method==='GET'&&call.path.includes('/requests/')).length,0);
});
test('real app Fleet read reply is discarded after authentication is cleared',async t=>{
  let release,entered;const started=new Promise(resolve=>entered=resolve),held=new Promise(resolve=>release=resolve);
  const f=await fixture(t,{session:fleetSession,mutation:async({route,body})=>{entered();await held;return route.fulfill({json:fleetReply(body)});}});
  await f.connect();await f.page.locator('#fleet-status-read').click();await started;
  await f.page.locator('#disconnect-host').click();release();
  await f.page.waitForFunction(()=>document.querySelector('#connect').disabled===false);
  assert.equal(await f.page.locator('#fleet-status-facts').isHidden(),true);
  assert.equal(await f.page.locator('#fleet-status-read').isDisabled(),true);
  assert.equal((await f.page.locator('#result-json').textContent()).includes('fixture-release'),false);
});

const diagnosticsVersion='questionable.file_manager.local_api.v3';
const diagnosticsLimits={maximumProcessLogs:8,recentLogLinesPerCapture:400,maximumPayloadFiles:14,maximumPayloadFileBytes:262144,maximumPayloadBytes:3670016,maximumArchiveBytes:4194304,reservedOutputBytes:8388608,reservedOutputFiles:15,maximumRequiredStandardOutputBytes:262144,maximumRequiredStandardErrorBytes:262144,maximumOptionalStandardOutputBytes:262144,maximumOptionalStandardErrorBytes:262144,maximumInputArtifactBytes:1073741824,maximumBinaryFramingBytes:256,maximumProcessTimeoutSeconds:300};
function diagnosticsSession(sessionId='fixture-session') {return {sessionId,actions:['devices','device.status','device.keep-awake','device.normal'],artifactStaging:{supported:true,maximumFileBytes:1073741824},retainedOperations:{supported:true,contractVersions:['questionable.file_manager.local_api.v1','questionable.file_manager.local_api.v2',diagnosticsVersion]}};}
function diagnosticsPrepared() {return {contractVersion:diagnosticsVersion,operationId:'api-'+'a'.repeat(32),command:'apk.diagnose',commandDigest:'b'.repeat(64),expiresAt:new Date(Date.now()+120000).toISOString(),serial:'FIXTURE-A',artifact:{sizeBytes:3,sha256:createHash('sha256').update(Buffer.from([1,2,3])).digest('hex'),identity:{packageName:'com.example.fixture',versionCode:1,versionName:'1.0',signerSha256:'a'.repeat(64),splitName:null}},outputIntent:{outputId:'diag-'+'c'.repeat(32),policyVersion:'exact-installed-diagnostic-output.v1',archiveFormat:'zip-store.v1',plannedLeafDigest:'d'.repeat(64),limits:{...diagnosticsLimits}}};}
function diagnosticsCompleted(prepared) {return {contractVersion:diagnosticsVersion,operationId:prepared.operationId,command:'apk.diagnose',commandDigest:prepared.commandDigest,expiresAt:prepared.expiresAt,stage:'completed',consumed:true,serial:prepared.serial,output:{intent:prepared.outputIntent,phase:'available',available:true,resourceCount:8,debtCount:0,errorCode:null,archive:{outputId:prepared.outputIntent.outputId,fileName:'apk-diagnostics.zip',sizeBytes:3,sha256:'e'.repeat(64),publishedAt:new Date().toISOString()}},diagnosticSummary:{diagnosticContract:'questionable.file_manager.apk_diagnostic_bundle.v3',capturedAt:new Date().toISOString(),captureState:'complete',payloadCount:6,failedCount:0,truncatedCount:0,applicationReadiness:'unknown',applicationReadinessAuthority:false,openXrReadiness:'unknown',openXrReadinessAuthority:false,wearerVisibility:'unknown',wearerVisibilityAuthority:false},errorCode:null};}
async function stageDiagnosticsSource(f) {await f.selectA();await f.page.locator('#apk-upload').setInputFiles({name:'fixture.apk',mimeType:'application/octet-stream',buffer:Buffer.from([1,2,3])});await f.page.locator('#stage-apk').click();await f.settled();}

test('real app diagnostic original survives lost capture and recovers without target, upload or replay',async t=>{
  let sessionNumber=0,prepared,executeCount=0,statusCount=0;
  const f=await fixture(t,{expectedLostResponse:true,session:route=>route.fulfill({json:diagnosticsSession(++sessionNumber===1?'fixture-session':'replacement-session')}),diagnostics:async({route,body,path})=>{
    if(path.endsWith('/preflight')) {prepared=diagnosticsPrepared();return route.fulfill({json:prepared});}
    if(path.endsWith('/execute')) {executeCount++;const saved=await f.page.evaluate(key=>JSON.parse(sessionStorage.getItem(key)),diagnosticsKey);assert.equal(saved.executionSent,true);assert.equal(saved.operationId,body.operationId);assert.equal(saved.commandDigest,body.commandDigest);return route.abort('failed');}
    if(path.endsWith('/status')) {statusCount++;assert.deepEqual(body,{contractVersion:diagnosticsVersion,operationId:prepared.operationId,commandDigest:prepared.commandDigest});return route.fulfill({json:diagnosticsCompleted(prepared)});}
    return route.abort('blockedbyclient');
  }});
  assert.equal(f.calls.length,0);await f.connect();assert.equal(f.calls.length,1);await stageDiagnosticsSource(f);
  await f.page.locator('#diagnostics-prepare').click();await f.settled();await f.page.locator('#diagnostics-consent').check();await f.page.locator('#diagnostics-capture').click();await f.settled();
  assert.equal(executeCount,1);assert.equal(statusCount,0);assert.equal(await f.page.locator('#diagnostics-capture').isDisabled(),true);
  await f.page.locator('#power-confirm').check();assert.equal(await f.page.locator('#apply-hold').isDisabled(),true);
  const saved=await f.page.evaluate(key=>JSON.parse(sessionStorage.getItem(key)),diagnosticsKey);
  await f.page.locator('#disconnect-host').click();await f.page.reload();await f.connect();
  assert.equal(executeCount,1);assert.equal(statusCount,0);assert.equal(await f.page.locator('#selection').textContent(),'Choose a headset to read its current state.');
  await f.page.locator('#diagnostics-status').click();await f.settled();assert.equal(statusCount,1);assert.equal(executeCount,1);
  assert.equal(await f.page.locator('#diagnostics-copy').isEnabled(),true);
  const recovered=await f.page.evaluate(key=>JSON.parse(sessionStorage.getItem(key)),diagnosticsKey);assert.equal(recovered.operationId,saved.operationId);assert.equal(recovered.stage,'completed');
  assert.equal(f.calls.filter(call=>call.path.endsWith('/output')).length,0);
  await f.selectA();await f.page.locator('#power-confirm').check();assert.equal(await f.page.locator('#apply-hold').isEnabled(),true);
});

test('real app sibling unknown blocks diagnostic preparation through pure uncertainty getters',async t=>{
  const f=await fixture(t,{session:route=>route.fulfill({json:diagnosticsSession()})});
  const original={requestId:'00000000-0000-4000-8000-000000000077',sessionId:'fixture-session',action:'device.normal',target:'FIXTURE-A'};
  await f.page.evaluate(({key,value})=>sessionStorage.setItem(key,JSON.stringify(value)),{key:originalKey,value:original});await f.page.reload();await f.connect();await stageDiagnosticsSource(f);
  assert.equal(await f.page.locator('#diagnostics-prepare').isDisabled(),true);assert.equal(f.calls.filter(call=>call.path.startsWith('/browser/v3/retained/')).length,0);
  assert.deepEqual(await f.outstanding(),original);
});

test('real app uncertain diagnostic preparation blocks mutations but sends no automatic recovery calls',async t=>{
  const f=await fixture(t,{session:route=>route.fulfill({json:diagnosticsSession()})});
  const original={contractVersion:diagnosticsVersion,command:'apk.diagnose',phase:'preflight-unknown',attemptId:'00000000-0000-4000-8000-000000000078',sessionId:'old-session',artifactId:'00000000000040008000000000000003',artifactSha256:'a'.repeat(64),artifactSizeBytes:3,serial:'FIXTURE-A',operationId:null,commandDigest:null,outputId:null,plannedLeafDigest:null,expiresAt:null,executionSent:false,cancelPending:false,stage:'clientUncertain'};
  await f.page.evaluate(({key,value})=>sessionStorage.setItem(key,JSON.stringify(value)),{key:diagnosticsKey,value:original});await f.page.reload();await f.connect();await f.selectA();await f.page.locator('#power-confirm').check();
  assert.equal(await f.page.locator('#apply-hold').isDisabled(),true);assert.equal(await f.page.locator('#diagnostics-capture').isDisabled(),true);assert.equal(await f.page.locator('#diagnostics-status').isDisabled(),true);assert.equal(f.calls.filter(call=>call.path.startsWith('/browser/v3/retained/')).length,0);
  assert.deepEqual(await f.page.evaluate(key=>JSON.parse(sessionStorage.getItem(key)),diagnosticsKey),original);
});

const directTagsAction='kiosk.direct.tags-export';
function directTagsSession(){return {sessionId:'fixture-session',actions:['devices','device.status','device.keep-awake','device.normal','kiosk.direct.connect-usb','kiosk.direct.status','kiosk.direct.close',directTagsAction],artifactStaging:{supported:true,maximumFileBytes:1073741824,maximumTotalBytes:2147483648,maximumFiles:8,transferTimeoutSeconds:300}};}
function directTagsConnectionReply(body,known){
  const connection=body.action==='kiosk.direct.connect-usb'?{connectionId:'d1234567-1234-4234-8234-123456789abc',connectRequestId:body.requestId,usbOperationId:'usb_'+body.requestId.replaceAll('-',''),serial:body.serial,productChannel:body.productChannel,expiresAtUtc:new Date(Date.now()+300000).toISOString(),enabledByRequest:true,bootstrap:{schema:'questionable.file_manager.kiosk_direct_usb.v1',operationId:'usb_'+body.requestId.replaceAll('-',''),stage:'confirmed'},adoption:{stage:'confirmed',signedStatusRead:true,typedKioskStatusRead:true,stagingInventoryRead:true}}:{...known};
  connection.state=body.action==='kiosk.direct.close'?'closed':'connected';
  if(connection.state==='closed'){delete connection.bootstrap;delete connection.adoption;connection.cleanup={schema:'questionable.file_manager.kiosk_direct_usb_cleanup.v1',operationId:connection.usbOperationId,stage:'confirmed',listenerDisposition:'owned-stopped'};}
  return {requestId:body.requestId,action:body.action,state:'completed',result:null,mutation:null,kioskDirectConnection:connection};
}
function directTagsExportReply(body,connection){
  const bytes=Buffer.from('{"schema":"rusty.kiosk.app_tags.v1","apps":[{"name":"private-tag-document","tags":["fixture"]}]}');
  const artifact={id:'e'.repeat(32),fileName:'app-tags.json',sizeBytes:bytes.length,sha256:createHash('sha256').update(bytes).digest('hex')};
  return {requestId:body.requestId,action:directTagsAction,state:'completed',result:null,mutation:null,rawDocument:'private-tag-document',kioskDirectTags:{connectionId:connection.connectionId,connectRequestId:connection.connectRequestId,usbOperationId:connection.usbOperationId,serial:connection.serial,productChannel:connection.productChannel,schema:'rusty.kiosk.app_tags.v1',sizeBytes:artifact.sizeBytes,sha256:artifact.sha256,artifact,coreReceipt:{actionId:'action_'+'c'.repeat(32),action:'exportTags',stage:'confirmed'}}};
}
async function adoptDirectForTags(f){await f.connect();await f.selectA();await f.page.locator('#direct-connect-confirm').check();await f.page.locator('#direct-connect').click();await f.settled();await f.page.waitForFunction(()=>!document.querySelector('#direct-tags-stage').disabled);}

test('real app tag backup recovers its cached copy while preserving an unrelated unknown effect',async t=>{
  let connection;
  const f=await fixture(t,{expectedLostResponse:true,session:route=>route.fulfill({json:directTagsSession()}),mutation:({route,body,retained})=>{
    if(body.action==='kiosk.direct.connect-usb'){const value=directTagsConnectionReply(body);connection=value.kioskDirectConnection;retained.set(body.requestId,value);return route.fulfill({json:value});}
    if(body.action==='device.keep-awake')return route.abort('failed');
    if(body.action===directTagsAction){retained.set(body.requestId,directTagsExportReply(body,connection));return route.abort('failed');}
    return route.abort('blockedbyclient');
  }});
  await adoptDirectForTags(f);await f.page.locator('#power-confirm').check();await f.page.locator('#apply-hold').click();await f.settled();
  const original=await f.outstanding();assert.equal(original.action,'device.keep-awake');
  const before=await f.page.evaluate(()=>Object.fromEntries(Object.entries(sessionStorage)));
  await f.page.locator('#direct-tags-stage').click();await f.settled();assert.equal(await f.page.locator('#direct-tags-facts').isHidden(),true);
  await f.page.locator('#direct-tags-original').click();await f.settled();assert.equal(await f.page.locator('#direct-tags-facts').isVisible(),true);
  assert.equal(await f.page.locator('#direct-tags-save').isEnabled(),true);assert.deepEqual(await f.outstanding(),original);
  assert.deepEqual(await f.page.evaluate(()=>Object.fromEntries(Object.entries(sessionStorage))),before);
  assert.equal(f.calls.filter(c=>c.body?.action===directTagsAction).length,1);
  assert.equal(f.calls.filter(c=>c.method==='GET'&&c.path.startsWith('/browser/v1/requests/')).length,1);
  assert.equal(f.calls.some(c=>c.path.endsWith('/'+original.requestId)),false);
  assert.equal((await f.page.locator('#result-json').textContent()).includes('private-tag-document'),false);
  assert.equal(await f.page.locator('#apply-hold').isDisabled(),true);
  await f.page.locator('#direct-tags-remove').click();await f.settled();assert.equal(await f.page.locator('#direct-tags-facts').isHidden(),true);assert.deepEqual(await f.outstanding(),original);
});

test('real app tag copy survives Direct close and retires on the shared staging Clear',async t=>{
  let connection;
  const f=await fixture(t,{session:route=>route.fulfill({json:directTagsSession()}),mutation:({route,body,retained})=>{
    const value=body.action===directTagsAction?directTagsExportReply(body,connection):directTagsConnectionReply(body,connection);
    if(body.action==='kiosk.direct.connect-usb')connection=value.kioskDirectConnection;
    retained.set(body.requestId,value);return route.fulfill({json:value});
  }});
  await adoptDirectForTags(f);await f.page.locator('#direct-tags-stage').click();await f.settled();
  await f.page.locator('#direct-close-confirm').check();await f.page.locator('#direct-close').click();await f.settled();
  assert.equal(await f.page.locator('#direct-tags-stage').isDisabled(),true);assert.equal(await f.page.locator('#direct-tags-save').isEnabled(),true);
  await f.page.locator('#direct-tags-original').click();await f.settled();assert.equal(await f.page.locator('#direct-tags-save').isEnabled(),true);
  assert.equal(f.calls.filter(c=>c.body?.action===directTagsAction).length,1);
  assert.equal(f.calls.filter(c=>c.body?.action==='kiosk.direct.close').length,1);
  await f.page.locator('#clear-artifacts').click();await f.settled();assert.equal(await f.page.locator('#direct-tags-save').isDisabled(),true);assert.equal(await f.page.locator('#direct-tags-original').isDisabled(),true);
  assert.equal(await f.outstanding(),null);
});

const directStagingAction='kiosk.direct.staging-list';
function directStagingSession(){return {...directTagsSession(),artifactStaging:{supported:false},actions:[...directTagsSession().actions,directStagingAction]};}
function directStagingReply(body,connection){return {requestId:body.requestId,action:directStagingAction,state:'completed',result:null,mutation:null,privateEndpoint:'private-sentinel',kioskDirectStaging:{connectionId:connection.connectionId,connectRequestId:connection.connectRequestId,usbOperationId:connection.usbOperationId,serial:connection.serial,productChannel:connection.productChannel,totalCount:2,returnedCount:2,omittedCount:0,complete:true,files:[{name:'my app (1)+@.apk',bytes:123,modifiedAtMs:1700000000000},{name:'a'+'+'.repeat(159),bytes:Number.MAX_SAFE_INTEGER,modifiedAtMs:Number.MAX_SAFE_INTEGER}],coreReceipt:{actionId:'action_'+'a'.repeat(32),action:'listStaging',stage:'confirmed'}}};}
async function adoptDirectForStaging(f){
  assert.equal(await f.page.locator('#direct-staging-read').count(),1);
  await f.connect();await f.selectA();await f.page.locator('#direct-connect-confirm').check();await f.page.locator('#direct-connect').click();await f.settled();
  await f.page.waitForFunction(()=>!document.querySelector('#direct-staging-read').disabled);
}
test('real app Direct inventory needs no artifact staging and preserves an unrelated unknown effect',async t=>{
  let connection;
  const f=await fixture(t,{expectedLostResponse:true,session:route=>route.fulfill({json:directStagingSession()}),mutation:({route,body,retained})=>{
    if(body.action==='kiosk.direct.connect-usb'){const value=directTagsConnectionReply(body);connection=value.kioskDirectConnection;retained.set(body.requestId,value);return route.fulfill({json:value});}
    if(body.action==='device.keep-awake')return route.abort('failed');
    if(body.action===directStagingAction){retained.set(body.requestId,directStagingReply(body,connection));return route.abort('failed');}
    return route.abort('blockedbyclient');
  }});
  await adoptDirectForStaging(f);await f.page.locator('#power-confirm').check();await f.page.locator('#apply-hold').click();await f.settled();
  const original=await f.outstanding(),before=await f.page.evaluate(()=>Object.fromEntries(Object.entries(sessionStorage)));
  await f.page.locator('#direct-staging-read').click();await f.settled();
  await f.page.locator('#direct-staging-original').click();await f.settled();
  assert.match(await f.page.locator('#direct-staging-files').textContent(),/my app \(1\)\+@\.apk/);
  assert.ok((await f.page.locator('#direct-staging-files').textContent()).includes('a'+'+'.repeat(159)));
  for(const width of [390,1280]){
    await f.page.setViewportSize({width,height:900});
    assert.equal(await f.page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth),true,'Inventory fits the page');
    assert.equal(await f.page.locator('#direct-staging-view').evaluate(e=>e.scrollWidth<=e.clientWidth),true,'Inventory fits its panel');
    if(process.env.QFM_BROWSER_UI_EVIDENCE){await fs.mkdir(process.env.QFM_BROWSER_UI_EVIDENCE,{recursive:true});await f.page.locator('#direct-staging-view').screenshot({path:path.join(process.env.QFM_BROWSER_UI_EVIDENCE,`direct-staging-${width}.png`)});}
  }
  assert.equal(f.calls.filter(c=>c.body?.action===directStagingAction).length,1);
  assert.equal(f.calls.filter(c=>c.method==='GET'&&c.path.startsWith('/browser/v1/requests/')).length,1);
  assert.equal(f.calls.some(c=>c.path.endsWith('/'+original.requestId)),false);
  assert.deepEqual(await f.outstanding(),original);assert.deepEqual(await f.page.evaluate(()=>Object.fromEntries(Object.entries(sessionStorage))),before);
  assert.equal(await f.page.locator('#apply-hold').isDisabled(),true);
  assert.equal((await f.page.locator('#result-json').textContent()).includes('private-sentinel'),false);
  assert.equal(f.calls.some(c=>c.path.startsWith('/browser/v1/artifacts')),false);
});
test('real app cached Direct inventory remains historical after close and host reconnect sends no read',async t=>{
  let connection;
  const f=await fixture(t,{session:route=>route.fulfill({json:directStagingSession()}),mutation:({route,body,retained})=>{
    const value=body.action===directStagingAction?directStagingReply(body,connection):directTagsConnectionReply(body,connection);
    if(body.action==='kiosk.direct.connect-usb')connection=value.kioskDirectConnection;
    retained.set(body.requestId,value);return route.fulfill({json:value});
  }});
  await adoptDirectForStaging(f);await f.page.locator('#direct-staging-read').click();await f.settled();
  await f.page.locator('#direct-close-confirm').check();await f.page.locator('#direct-close').click();await f.settled();
  assert.equal(await f.page.locator('#direct-staging-read').isDisabled(),true);
  assert.equal(await f.page.locator('#direct-staging-original').isEnabled(),true);
  await f.page.locator('#direct-staging-original').click();await f.settled();
  assert.match(await f.page.locator('#direct-staging-files').textContent(),/my app/);
  assert.match(await f.page.locator('#direct-staging-state').textContent(),/historical/i);
  await f.page.locator('#disconnect-host').click();await f.connect();
  assert.equal(f.calls.filter(c=>c.body?.action===directStagingAction).length,1);
  assert.equal(await f.outstanding(),null);
});

const directUploadAction='kiosk.direct.staging-upload',directUploadKey='qfm-browser-direct-upload-v1';
function directUploadSession(){return {...directTagsSession(),actions:[...directTagsSession().actions,directUploadAction]};}
function directUploadReply(body,connection){return {requestId:body.requestId,action:directUploadAction,state:'completed',result:null,mutation:null,privateSourcePath:'private-source-sentinel',kioskDirectUpload:{connectionId:connection.connectionId,connectRequestId:connection.connectRequestId,usbOperationId:connection.usbOperationId,serial:connection.serial,productChannel:connection.productChannel,artifactId:body.artifactId,sha256:body.sha256,sizeBytes:body.sizeBytes,stagedName:body.name,disposition:'confirmed',verification:'signed-upload-size-sha256',coreReceipt:{actionId:'action_'+'b'.repeat(32),action:'uploadStaging',stage:'confirmed'}}};}
async function prepareDirectUpload(f){
  assert.equal(await f.page.locator('#direct-upload-send').count(),1);
  await f.connect();await f.selectA();
  await f.page.locator('#apk-upload').setInputFiles({name:'fixture.apk',mimeType:'application/octet-stream',buffer:Buffer.from([1,2,3])});
  await f.page.locator('#stage-apk').click();await f.settled();
  await f.page.locator('#direct-connect-confirm').check();await f.page.locator('#direct-connect').click();await f.settled();
  await f.page.locator('#direct-upload-name').fill('reviewed fixture (1)+@.apk');await f.page.locator('#direct-upload-confirm').check();
}
test('real app lost Direct upload blocks mutations, permits exact-source close and recovers cached acknowledgement',async t=>{
  let connection;
  const f=await fixture(t,{expectedLostResponse:true,session:route=>route.fulfill({json:directUploadSession()}),mutation:({route,body,retained})=>{
    const value=body.action===directUploadAction?directUploadReply(body,connection):directTagsConnectionReply(body,connection);
    if(body.action==='kiosk.direct.connect-usb')connection=value.kioskDirectConnection;
    retained.set(body.requestId,value);return body.action===directUploadAction?route.abort('failed'):route.fulfill({json:value});
  }});
  await prepareDirectUpload(f);await f.page.locator('#direct-upload-send').click();await f.settled();
  const original=await f.page.evaluate(key=>sessionStorage.getItem(key),directUploadKey);assert.ok(original);
  await f.page.locator('#power-confirm').check();assert.equal(await f.page.locator('#apply-hold').isDisabled(),true);
  await f.page.locator('#direct-close-confirm').check();assert.equal(await f.page.locator('#direct-close').isEnabled(),true);
  await f.page.locator('#direct-close').click();await f.settled();
  assert.equal(await f.page.evaluate(key=>sessionStorage.getItem(key),directUploadKey),original);
  await f.page.locator('#direct-upload-original').click();await f.page.waitForFunction(key=>sessionStorage.getItem(key)===null,directUploadKey);await f.settled();
  assert.equal(await f.page.evaluate(key=>sessionStorage.getItem(key),directUploadKey),null);
  assert.equal(f.calls.filter(c=>c.body?.action===directUploadAction).length,1);
  assert.equal(f.calls.filter(c=>c.body?.action==='kiosk.direct.close').length,1);
  assert.equal(f.calls.filter(c=>c.method==='GET'&&c.path.startsWith('/browser/v1/requests/')).length,1);
  assert.equal((await f.page.locator('#result-json').textContent()).includes('private-source-sentinel'),false);
  assert.equal(await f.outstanding(),null);
});
test('real app restored Direct upload never bypasses an unrelated original for source cleanup',async t=>{
  let connection;
  const f=await fixture(t,{expectedLostResponse:true,session:route=>route.fulfill({json:directUploadSession()}),mutation:({route,body,retained})=>{
    const value=body.action===directUploadAction?directUploadReply(body,connection):directTagsConnectionReply(body,connection);
    if(body.action==='kiosk.direct.connect-usb')connection=value.kioskDirectConnection;
    retained.set(body.requestId,value);return body.action===directUploadAction?route.abort('failed'):route.fulfill({json:value});
  }});
  await prepareDirectUpload(f);await f.page.locator('#direct-upload-send').click();await f.settled();
  const upload=await f.page.evaluate(key=>sessionStorage.getItem(key),directUploadKey),other={requestId:'f1234567-1234-4234-8234-123456789abc',sessionId:'fixture-session',action:'device.keep-awake',serial:'FIXTURE-A'};
  await f.page.evaluate(({key,value})=>sessionStorage.setItem(key,JSON.stringify(value)),{key:originalKey,value:other});await f.page.reload();await f.connect();
  assert.equal(await f.page.locator('#direct-close').isDisabled(),true);
  assert.equal(await f.page.locator('#direct-upload-send').isDisabled(),true);
  assert.deepEqual(await f.outstanding(),other);assert.equal(await f.page.evaluate(key=>sessionStorage.getItem(key),directUploadKey),upload);
  assert.equal(f.calls.filter(c=>c.body?.action===directUploadAction).length,1);
  assert.equal(f.calls.some(c=>c.body?.action==='kiosk.direct.close'),false);
});
test('real app cleanup joins every unresolved Direct source before permitting Close',async t=>{
  for(const sameConnection of [true,false])await t.test(sameConnection?'same source':'different source',async t=>{
    let connection;
    const f=await fixture(t,{expectedLostResponse:true,session:route=>route.fulfill({json:directUploadSession()}),mutation:({route,body,retained})=>{
      const value=body.action===directUploadAction?directUploadReply(body,connection):directTagsConnectionReply(body,connection);
      if(body.action==='kiosk.direct.connect-usb')connection=value.kioskDirectConnection;
      retained.set(body.requestId,value);return body.action===directUploadAction?route.abort('failed'):route.fulfill({json:value});
    }});
    await prepareDirectUpload(f);await f.page.locator('#direct-upload-send').click();await f.settled();
    const requestId='e1234567-1234-4234-8234-123456789abc',source={sessionId:'fixture-session',connectionId:sameConnection?connection.connectionId:'f1234567-1234-4234-8234-123456789abc',connectRequestId:connection.connectRequestId,usbOperationId:connection.usbOperationId,serial:connection.serial,productChannel:connection.productChannel,requestId,action:'kiosk.direct.invoke',logicalRequestId:'kiosk_'+requestId.replaceAll('-',''),command:'status',value:null};
    await f.page.evaluate(value=>sessionStorage.setItem('qfm-browser-direct-command-v1',JSON.stringify({original:value,cancellation:null,damaged:false})),source);
    await f.page.reload();await f.connect();
    if(sameConnection)await f.page.locator('#direct-close-confirm').check();
    else assert.equal(await f.page.locator('#direct-close-confirm').isDisabled(),true);
    assert.equal(await f.page.locator('#direct-close').isEnabled(),sameConnection);
    assert.equal(f.calls.some(c=>c.body?.action==='kiosk.direct.close'),false);
    assert.ok(await f.page.evaluate(key=>sessionStorage.getItem(key),directUploadKey));
  });
});

const directInstallAction='kiosk.direct.install',directInstallStatus='kiosk.direct.install-status',directInstallKey='qfm-browser-direct-install-v1';
function directInstallSession(){return {...directUploadSession(),actions:[...directUploadSession().actions,'apk.inspect',directInstallAction,directInstallStatus]};}
function directInstallReply(body,connection,original,installed=false){
  const first=body.action===directInstallAction,prior=first?null:original.kioskDirectInstall,originalRequestId=first?body.requestId:prior.originalRequestId,installRequestId='install_'+originalRequestId.replaceAll('-','');
  return {requestId:body.requestId,action:body.action,state:installed?'completed':'pending',result:null,mutation:null,privatePath:'private-install-sentinel',kioskDirectInstall:{connectionId:connection.connectionId,connectRequestId:connection.connectRequestId,usbOperationId:connection.usbOperationId,serial:connection.serial,productChannel:connection.productChannel,operation:first?'install':'installStatus',originalRequestId,installRequestId,inspectionRequestId:first?body.inspectionRequestId:prior.inspectionRequestId,artifactId:first?body.artifactId:prior.artifactId,sha256:first?body.sha256:prior.sha256,sizeBytes:first?body.sizeBytes:prior.sizeBytes,packageName:'example.fixture.app',versionCode:1,signerSha256:'a'.repeat(64),originalEffect:installed?'installed':'pendingWearerAction',reconcilesOriginal:installed,coreReceipt:{actionId:'action_'+'a'.repeat(32),action:first?'install':'installStatus',requestId:installRequestId,stage:installed?'confirmed':'pendingWearerAction'},receipt:{requestId:installRequestId,state:installed?'installed':'pending-wearer-confirmation',completed:installed,sessionId:7,packageName:installed?'example.fixture.app':null}}};
}
async function prepareDirectInstall(f){
  assert.equal(await f.page.locator('#direct-install-send').count(),1);
  await f.connect();await f.selectA();
  await f.page.locator('#apk-upload').setInputFiles({name:'fixture.apk',mimeType:'application/octet-stream',buffer:Buffer.from([1,2,3])});await f.page.locator('#stage-apk').click();await f.settled();
  assert.equal(await f.page.locator('#direct-install-send').isDisabled(),true);
  await f.page.locator('#inspect-apk').click();await f.settled();
  await f.page.locator('#direct-connect-confirm').check();await f.page.locator('#direct-connect').click();await f.settled();
  await f.page.locator('#direct-install-confirm').check();
}
test('real app attended Direct install remains pending then status resolves without source bytes or another install',async t=>{
  let connection;
  const f=await fixture(t,{session:route=>route.fulfill({json:directInstallSession()}),mutation:({route,body,retained})=>{
    const value=body.action===directInstallAction||body.action===directInstallStatus?directInstallReply(body,connection,retained.get(body.originalRequestId),body.action===directInstallStatus):directTagsConnectionReply(body,connection);
    if(body.action==='kiosk.direct.connect-usb')connection=value.kioskDirectConnection;
    retained.set(body.requestId,value);return route.fulfill({json:value});
  }});
  await prepareDirectInstall(f);await f.page.locator('#direct-install-send').click();await f.settled();
  assert.ok(await f.page.evaluate(key=>sessionStorage.getItem(key),directInstallKey));
  await f.page.locator('#power-confirm').check();assert.equal(await f.page.locator('#apply-hold').isDisabled(),true);
  await f.page.locator('#clear-artifacts').click();await f.settled();
  assert.equal(await f.page.locator('#direct-install-status').isEnabled(),true);
  await f.page.locator('#direct-install-status').click();await f.page.waitForFunction(key=>sessionStorage.getItem(key)===null,directInstallKey);await f.settled();
  assert.equal(f.calls.filter(c=>c.body?.action===directInstallAction).length,1);assert.equal(f.calls.filter(c=>c.body?.action===directInstallStatus).length,1);assert.equal(f.calls.filter(c=>c.body?.action===directUploadAction).length,0);
  assert.equal((await f.page.locator('#result-json').textContent()).includes('private-install-sentinel'),false);assert.equal(await f.outstanding(),null);
});
test('real app lost Direct install can close exact source then recover its terminal cached receipt',async t=>{
  let connection;
  const f=await fixture(t,{expectedLostResponse:true,session:route=>route.fulfill({json:directInstallSession()}),mutation:({route,body,retained})=>{
    const value=body.action===directInstallAction?directInstallReply(body,connection,null,true):directTagsConnectionReply(body,connection);
    if(body.action==='kiosk.direct.connect-usb')connection=value.kioskDirectConnection;
    retained.set(body.requestId,value);return body.action===directInstallAction?route.abort('failed'):route.fulfill({json:value});
  }});
  await prepareDirectInstall(f);await f.page.locator('#direct-install-send').click();await f.settled();
  const original=await f.page.evaluate(key=>sessionStorage.getItem(key),directInstallKey);assert.ok(original);
  await f.page.locator('#direct-close-confirm').check();await f.page.locator('#direct-close').click();await f.settled();assert.equal(await f.page.evaluate(key=>sessionStorage.getItem(key),directInstallKey),original);
  assert.equal(await f.page.locator('#direct-install-status').isDisabled(),true);await f.page.locator('#direct-install-original').click();await f.page.waitForFunction(key=>sessionStorage.getItem(key)===null,directInstallKey);
  assert.equal(f.calls.filter(c=>c.body?.action===directInstallAction).length,1);assert.equal(f.calls.some(c=>c.body?.action===directInstallStatus),false);assert.equal(await f.outstanding(),null);
});
test('real app restored install and upload from different sources cannot use the Close exception',async t=>{
  let connection;
  const f=await fixture(t,{expectedLostResponse:true,session:route=>route.fulfill({json:directInstallSession()}),mutation:({route,body,retained})=>{
    const value=body.action===directInstallAction?directInstallReply(body,connection,null,true):directTagsConnectionReply(body,connection);if(body.action==='kiosk.direct.connect-usb')connection=value.kioskDirectConnection;retained.set(body.requestId,value);return body.action===directInstallAction?route.abort('failed'):route.fulfill({json:value});
  }});
  await prepareDirectInstall(f);await f.page.locator('#direct-install-send').click();await f.settled();
  const source=JSON.parse(await f.page.evaluate(key=>sessionStorage.getItem(key),directInstallKey)),upload={requestId:'c1234567-1234-4234-8234-123456789abc',action:directUploadAction,sessionId:'fixture-session',sourceConnectionId:'f1234567-1234-4234-8234-123456789abc',connectRequestId:connection.connectRequestId,usbOperationId:connection.usbOperationId,serial:connection.serial,channel:'stable',artifactId:source.artifactId,sha256:source.sha256,sizeBytes:source.sizeBytes,stagedName:'old.apk'};
  await f.page.evaluate(({key,value})=>sessionStorage.setItem(key,JSON.stringify(value)),{key:directUploadKey,value:upload});await f.page.reload();await f.connect();
  assert.equal(await f.page.locator('#direct-close-confirm').isDisabled(),true);assert.equal(await f.page.locator('#direct-close').isDisabled(),true);
  assert.equal(f.calls.filter(c=>c.body?.action===directInstallAction).length,1);assert.equal(f.calls.some(c=>c.body?.action==='kiosk.direct.close'),false);
});
