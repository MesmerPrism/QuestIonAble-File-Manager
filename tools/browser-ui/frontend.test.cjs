// Real DOM regression tests. All URLs are route-fulfilled: no server, ADB or USB.
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs/promises');
const path = require('node:path');
const { chromium } = require('playwright');

const root = path.resolve(__dirname, '../../src/QuestIonAbleFileManager.BrowserHost/wwwroot');
const originalKey = 'qfm-browser-original-v1';
let browser;
test.before(async () => { browser = await chromium.launch({ headless: true }); });
test.after(async () => { await browser?.close(); });

async function fixture(t, { origin = 'http://127.0.0.1:18127', mutation, expectedLostResponse = false, blockApp = false } = {}) {
  const context = await browser.newContext({ serviceWorkers: 'block', viewport: { width: 1280, height: 900 } });
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
      const request = route.request(), body = request.method() === 'POST' ? request.postDataJSON() : null;
      calls.push({ method: request.method(), path: url.pathname, body });
      if (url.pathname === '/browser/v1/session') return route.fulfill({ json: { sessionId: 'fixture-session' } });
      if (url.pathname.startsWith('/browser/v1/requests/')) return route.fulfill({ json: reconcile(url.pathname.split('/').at(-1)) || { error: 'request_not_found' } });
      if (url.pathname === '/browser/v1/execute') {
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
    const allowed = new Set(['index.html', 'app.js', 'styles.css', 'usb-proof.js', 'usb-proof-core.js', 'vendor/tango-adb.js']);
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
  const mutations = () => calls.filter(call => call.body && !['devices', 'device.status'].includes(call.body.action));
  t.after(() => assert.deepEqual(errors, [], 'No uncaught frontend runtime errors'));
  t.after(() => assert.deepEqual(consoleProblems.filter(message =>
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
