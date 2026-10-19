// Exercises the shipped/minified Tango bundle against an in-memory USB peer.
// It never uses native WebUSB, a headset, a driver or the host ADB daemon.
import test from 'node:test';
import assert from 'node:assert/strict';

const encoder = new TextEncoder();
const decoder = new TextDecoder();
const commands = [];
const raw = {
  serialNumber: 'fixture-usb', productName: 'In-memory test peer', vendorId: 1, productId: 1,
  opened: false, busy: false,
  async open() { this.opened = true; },
  async close() { this.opened = false; while (readers.length) readers.shift().reject(new DOMException('Closed', 'NetworkError')); },
  async claimInterface() { if (this.busy) throw new DOMException('Owned by fixture', 'NetworkError'); },
  async transferIn() {
    const bytes = received.length ? received.shift() : await new Promise((resolve, reject) => readers.push({ resolve, reject }));
    return { status: 'ok', data: new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength) };
  },
  async transferOut(_endpoint, bytes) {
    const data = new Uint8Array(bytes.buffer, bytes.byteOffset, bytes.byteLength);
    if (data.byteLength === 0) return { status: 'ok', bytesWritten: 0 };
    if (!outgoing) {
      const header = new DataView(data.buffer, data.byteOffset, data.byteLength);
      outgoing = { command: header.getUint32(0, true), arg0: header.getUint32(4, true), arg1: header.getUint32(8, true), length: header.getUint32(12, true) };
      if (outgoing.length === 0) { const packet = outgoing; outgoing = null; handle(packet, new Uint8Array()); }
    } else {
      assert.equal(data.byteLength, outgoing.length);
      const packet = outgoing; outgoing = null; handle(packet, data);
    }
    return { status: 'ok', bytesWritten: data.byteLength };
  }
};
const alternate = { alternateSetting: 0, interfaceClass: 0xff, interfaceSubclass: 0x42, interfaceProtocol: 1,
  endpoints: [{ direction: 'in', endpointNumber: 1, packetSize: 512 }, { direction: 'out', endpointNumber: 2, packetSize: 512 }] };
raw.configuration = { configurationValue: 1, interfaces: [{ interfaceNumber: 1, claimed: false, alternate, alternates: [alternate] }] };
raw.configurations = [raw.configuration];
const received = [], readers = [];
let outgoing = null, remoteId = 1;
const awaitingAck = new Map();
const CNXN = 0x4e584e43, OPEN = 0x4e45504f, OKAY = 0x59414b4f, WRTE = 0x45545257, CLSE = 0x45534c43;
function enqueue(bytes) {
  if (readers.length) readers.shift().resolve(bytes); else received.push(bytes);
}
function packet(command, arg0, arg1, payload = new Uint8Array()) {
  const bytes = new Uint8Array(24), view = new DataView(bytes.buffer);
  [command, arg0, arg1, payload.byteLength, payload.reduce((sum, byte) => sum + byte, 0), (command ^ 0xffffffff) >>> 0].forEach((value, index) => view.setUint32(index * 4, value, true));
  enqueue(bytes); if (payload.byteLength) enqueue(payload);
}
function shellPacket(id, data) {
  const result = new Uint8Array(5 + data.byteLength);
  result[0] = id; new DataView(result.buffer).setUint32(1, data.byteLength, true); result.set(data, 5); return result;
}
function handle(header, payload) {
  if (header.command === CNXN) {
    packet(CNXN, 0x01000001, 1024 * 1024, encoder.encode('device::ro.product.model=fixture;features=shell_v2;\0'));
  } else if (header.command === OPEN) {
    const service = decoder.decode(payload).replace(/\0$/, '');
    commands.push(service);
    const outputs = new Map([
      ['shell,v2,raw:getprop ro.serialno', 'fixture-canonical\n'],
      ['shell,v2,raw:cat /proc/sys/kernel/random/boot_id', '12345678-1234-1234-1234-123456789abc\n'],
      ['shell,v2,raw:dumpsys battery', 'level: 70\nscale: 100\nstatus: 3\n']
    ]);
    assert.ok(outputs.has(service), `Unexpected protocol service: ${service}`);
    const remote = remoteId++;
    packet(OKAY, remote, header.arg0);
    const stdout = shellPacket(1, encoder.encode(outputs.get(service)));
    const exit = shellPacket(3, new Uint8Array([0]));
    const content = new Uint8Array(stdout.byteLength + exit.byteLength); content.set(stdout); content.set(exit, stdout.byteLength);
    // Separate USB turns let the client finish the OPEN/OKAY socket handshake.
    setImmediate(() => { packet(WRTE, remote, header.arg0, content); awaitingAck.set(remote, header.arg0); });
  } else if (header.command === OKAY && awaitingAck.has(header.arg1)) {
    packet(CLSE, header.arg1, awaitingAck.get(header.arg1));
    awaitingAck.delete(header.arg1);
  }
}
const usb = new EventTarget();
usb.getDevices = async () => [raw];
usb.requestDevice = async () => raw;
Object.defineProperty(globalThis, 'navigator', { value: { usb }, configurable: true });
globalThis.isSecureContext = true;
globalThis.indexedDB = {}; // The no-auth fixture handshake never accesses key storage.
globalThis.USBConnectionEvent = class extends Event { constructor(type, options) { super(type); this.device = options.device; } };
const proof = await import('../../../src/QuestIonAbleFileManager.BrowserHost/wwwroot/usb-proof.js');

test('shipped Tango bundle performs the fixed read over fake USB ADB packets', async () => {
  assert.equal(proof.capability().supported, true);
  assert.equal((await proof.getPermittedDevices()).length, 1);
  assert.equal(commands.length, 0);
  const result = await proof.chooseAndRead();
  assert.equal(result.state, 'connected', JSON.stringify({ result, commands }));
  assert.equal(result.reading.serial, 'fixture-canonical');
  assert.equal(result.reading.battery.level, 70);
  assert.equal(commands.length, 5);
  assert.equal((await proof.disconnect(result.selectionId)).state, 'disconnected');
  assert.equal(raw.opened, false);
});

test('minified vendor busy exception remains interface-busy, without authenticating', async () => {
  raw.busy = true;
  const result = await proof.chooseAndRead();
  assert.equal(result.state, 'interface-busy');
  assert.equal(commands.length, 5);
  assert.equal(raw.opened, false);
});
