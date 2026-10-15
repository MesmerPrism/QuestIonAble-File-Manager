import test from 'node:test';
import assert from 'node:assert/strict';
import { createUsbProof, parseBattery, parseBootId, parseSerial } from '../../../src/QuestIonAbleFileManager.BrowserHost/wwwroot/usb-proof-core.js';

const BOOT = '12345678-1234-1234-1234-123456789abc';
const NEXT_BOOT = '12345678-1234-1234-1234-123456789abd';
const BATTERY = 'Current Battery Service state:\n  USB powered: true\n  status: 2\n  level: 73\n  scale: 100\n';
const tick = () => new Promise(resolve => setImmediate(resolve));
function fixture(options = {}) {
  const devices = ['fixture-a', 'fixture-b'].map(serial => ({ raw: {}, serial, name: 'Test USB device' }));
  const calls = [], closed = [];
  let chosen = devices[0], chooserCalls = 0;
  const d = {
    capability: () => ({ supported: true, secureContext: true, message: 'Test only' }),
    manager: { getDevices: async () => devices, requestDevice: () => { chooserCalls++; return Promise.resolve(chosen); } },
    isBusyError: error => error?.name === 'DeviceBusyError',
    closeDevice: async device => { closed.push(device.serial); },
    connectDevice: async (device, signal) => ({
      read: async command => {
        calls.push({ target: device.serial, command: [...command] });
        signal.throwIfAborted();
        if (command[0] === 'getprop') return device.serial;
        if (command[0] === 'cat') return BOOT;
        if (command[0] === 'dumpsys') return BATTERY;
        throw new Error('Unexpected command');
      },
      close: async () => {}
    }),
    ...options
  };
  return { proof: createUsbProof(d), dependencies: d, devices, calls, closed, choose: value => { chosen = value; }, chooserCalls: () => chooserCalls };
}

test('permission enumeration is inert and selected read uses only fixed commands on its exact handle', async () => {
  const f = fixture();
  const permitted = await f.proof.getPermittedDevices();
  assert.equal(f.calls.length, 0);
  assert.notEqual(permitted[0].selectionId, permitted[1].selectionId);
  const states = [];
  const pending = f.proof.chooseAndRead({ onState: state => states.push(state.state) });
  assert.equal(f.chooserCalls(), 1, 'chooser must be called before any await loses user activation');
  const result = await pending;
  assert.equal(result.state, 'connected');
  assert.equal(result.reading.serial, 'fixture-a');
  assert.equal(result.reading.bootId, BOOT);
  assert.deepEqual(result.reading.battery, { level: 73, scale: 100, status: 2, charging: true });
  assert.deepEqual(states, ['auth-prompt', 'connected']);
  assert.deepEqual(f.calls.map(call => call.command), [['getprop', 'ro.serialno'], ['cat', '/proc/sys/kernel/random/boot_id'], ['dumpsys', 'battery'], ['getprop', 'ro.serialno'], ['cat', '/proc/sys/kernel/random/boot_id']]);
  assert.ok(f.calls.every(call => call.target === 'fixture-a'));
  assert.equal((await f.proof.read(permitted[1].selectionId)).errorCode, 'NOT_CONNECTED');
  assert.ok(f.calls.every(call => call.target === 'fixture-a'), 'a stale selection cannot fall back to another ready headset');
  await f.proof.disconnect(result.selectionId);
  assert.equal((await f.proof.read(result.selectionId)).errorCode, 'NOT_CONNECTED');
});

test('two devices with the same USB serial retain distinct exact-handle selections', async () => {
  const f = fixture(); f.devices[1].serial = f.devices[0].serial;
  const first = await f.proof.getPermittedDevices();
  const second = await f.proof.getPermittedDevices();
  assert.notEqual(first[0].selectionId, first[1].selectionId);
  assert.equal(first[0].selectionId, second[0].selectionId);
});

test('canonical identity change during a read rejects battery and closes connection', async () => {
  let reads = 0;
  const f = fixture({ connectDevice: async () => ({ read: async command => command[0] === 'getprop' ? (++reads === 1 ? 'fixture-a' : 'fixture-b') : command[0] === 'cat' ? BOOT : BATTERY, close: async () => {} }) });
  const result = await f.proof.chooseAndRead();
  assert.equal(result.errorCode, 'TARGET_CHANGED');
  assert.equal(result.reading, undefined);
  assert.ok(f.closed.length > 0);
});

test('boot changes within a read reject the result', async () => {
  let reads = 0;
  const f = fixture({ connectDevice: async () => ({ read: async command => command[0] === 'getprop' ? 'fixture-a' : command[0] === 'cat' ? (++reads === 1 ? BOOT : NEXT_BOOT) : BATTERY, close: async () => {} }) });
  assert.equal((await f.proof.chooseAndRead()).errorCode, 'TARGET_CHANGED');
});

test('reconnect cannot substitute a different canonical headset behind a known USB handle', async () => {
  let serial = 'fixture-a';
  const f = fixture({ connectDevice: async () => ({ read: async command => command[0] === 'getprop' ? serial : command[0] === 'cat' ? BOOT : BATTERY, close: async () => {} }) });
  const first = await f.proof.chooseAndRead();
  await f.proof.disconnect(first.selectionId);
  serial = 'fixture-b';
  assert.equal((await f.proof.reconnect(first.selectionId)).errorCode, 'TARGET_CHANGED');
});

test('busy, cancelled chooser and unsupported browser are distinct honest states', async () => {
  const busy = fixture({ connectDevice: async () => { const error = new Error(); error.name = 'DeviceBusyError'; throw error; } });
  assert.equal((await busy.proof.chooseAndRead()).state, 'interface-busy');
  const cancelled = fixture(); cancelled.choose(undefined);
  assert.equal((await cancelled.proof.chooseAndRead()).state, 'not-permitted');
  const unsupported = fixture({ capability: () => ({ supported: false, message: 'No WebUSB' }) });
  assert.equal((await unsupported.proof.chooseAndRead()).state, 'unsupported');
  assert.deepEqual(await unsupported.proof.getPermittedDevices(), []);
  assert.equal(unsupported.chooserCalls(), 0);
});

test('disconnect cancels pending authentication; late success is closed and never published', async () => {
  let resolveConnect, sessionClosed = 0;
  const f = fixture({ connectDevice: () => new Promise(resolve => { resolveConnect = resolve; }) });
  const states = [];
  const pending = f.proof.chooseAndRead({ onState: state => states.push(state.state) });
  await tick();
  const [selected] = await f.proof.getPermittedDevices();
  const closing = await f.proof.disconnect(selected.selectionId);
  assert.equal(closing.errorCode, 'OPERATION_BUSY');
  assert.equal(closing.cleanupPending, true);
  assert.equal(closing.closureConfirmed, false);
  assert.equal((await pending).state, 'disconnected');
  assert.equal((await f.proof.reconnect(selected.selectionId)).errorCode, 'OPERATION_BUSY', 'late native work retains the lock');
  resolveConnect({ read: async () => { throw new Error('Must not read a cancelled session'); }, close: async () => { sessionClosed++; } });
  await tick();
  assert.equal(sessionClosed, 1);
  assert.ok(!states.includes('connected'));
});

test('timed-out authentication retains its handle lock until underlying work settles', async () => {
  let resolveConnect;
  const f = fixture({ authTimeoutMs: 5, connectDevice: () => new Promise(resolve => { resolveConnect = resolve; }) });
  const result = await f.proof.chooseAndRead();
  assert.equal(result.errorCode, 'AUTH_TIMEOUT');
  assert.equal((await f.proof.reconnect(result.selectionId)).errorCode, 'OPERATION_BUSY');
  resolveConnect({ read: async () => '', close: async () => {} });
  await tick();
});

test('reconnect fails closed while a prior session close outlives the UI close deadline', async () => {
  let finishClose, slowClose = false, connections = 0;
  const f = fixture({ closeTimeoutMs: 5 });
  const connect = f.dependencies.connectDevice;
  f.dependencies.connectDevice = async (...args) => {
    connections++;
    const session = await connect(...args);
    return { ...session, close: () => slowClose ? new Promise(resolve => { finishClose = resolve; }) : Promise.resolve() };
  };
  const first = await f.proof.chooseAndRead();
  assert.equal(first.state, 'connected');
  slowClose = true;
  const blocked = await f.proof.reconnect(first.selectionId);
  assert.equal(blocked.errorCode, 'OPERATION_BUSY');
  assert.equal(connections, 1, 'a late old close must never overlap a new session');
  assert.equal((await f.proof.reconnect(first.selectionId)).errorCode, 'OPERATION_BUSY');
  slowClose = false; finishClose(); await tick();
  assert.equal((await f.proof.reconnect(first.selectionId)).state, 'connected');
  assert.equal(connections, 2);
});

test('disconnect reports pending cleanup and retains the lock while exact raw USB close remains pending', async () => {
  let finishClose, slowClose = false, connections = 0;
  const f = fixture({ closeTimeoutMs: 5, closeDevice: () => slowClose ? new Promise(resolve => { finishClose = resolve; }) : Promise.resolve() });
  const connect = f.dependencies.connectDevice;
  f.dependencies.connectDevice = async (...args) => { connections++; return connect(...args); };
  const first = await f.proof.chooseAndRead();
  slowClose = true;
  const result = await f.proof.disconnect(first.selectionId);
  assert.equal(result.errorCode, 'OPERATION_BUSY');
  assert.equal(result.cleanupPending, true);
  assert.equal(result.closureConfirmed, false);
  assert.equal((await f.proof.reconnect(first.selectionId)).errorCode, 'OPERATION_BUSY');
  assert.equal(connections, 1);
  slowClose = false; finishClose(); await tick();
  assert.equal((await f.proof.reconnect(first.selectionId)).state, 'connected');
  assert.equal(connections, 2);
});

test('a failed raw USB close cannot claim disconnected or admit another connection', async () => {
  let rejectClose = false, connections = 0;
  const f = fixture({ closeDevice: async () => { if (rejectClose) throw new Error('Fixture close refusal'); } });
  const connect = f.dependencies.connectDevice;
  f.dependencies.connectDevice = async (...args) => { connections++; return connect(...args); };
  const first = await f.proof.chooseAndRead();
  rejectClose = true;
  assert.equal((await f.proof.disconnect(first.selectionId)).errorCode, 'CLOSE_FAILED');
  assert.equal((await f.proof.reconnect(first.selectionId)).errorCode, 'CLOSE_FAILED');
  assert.equal(connections, 1);
  rejectClose = false;
  assert.equal((await f.proof.reconnect(first.selectionId)).state, 'connected');
});

test('an unplug event marks only its current exact session disconnected without polling', async () => {
  let unplug;
  const disconnected = new Promise(resolve => { unplug = resolve; });
  const f = fixture();
  const connect = f.dependencies.connectDevice;
  f.dependencies.connectDevice = async (...args) => ({ ...await connect(...args), disconnected });
  const states = [];
  const result = await f.proof.chooseAndRead({ onState: state => states.push(state.state) });
  assert.equal(result.state, 'connected');
  assert.equal((await f.proof.read(result.selectionId, { onState: state => states.push(state.state) })).state, 'connected');
  unplug(); await tick();
  assert.equal(states.at(-1), 'disconnected');
  assert.equal((await f.proof.read(result.selectionId)).errorCode, 'NOT_CONNECTED');
});

test('malformed identity and battery values remain failures rather than fabricated defaults', () => {
  for (const text of ['', 'unknown', 'two\nserials', 'A; reboot']) assert.throws(() => parseSerial(text));
  assert.throws(() => parseBootId('not-a-boot-id'));
  for (const text of ['', 'level: 101\nscale: 100\nstatus: 2', 'level: 50\nscale: 100\nstatus: 2\nlevel: 51']) assert.throws(() => parseBattery(text));
  assert.equal(parseBattery('level: 50\nscale: 100\nstatus: 1').charging, null);
});
