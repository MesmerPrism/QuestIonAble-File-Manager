// Pure session logic. The browser adapter is the public entry point.
const COMMANDS = Object.freeze({
  serial: Object.freeze(['getprop', 'ro.serialno']),
  bootId: Object.freeze(['cat', '/proc/sys/kernel/random/boot_id']),
  battery: Object.freeze(['dumpsys', 'battery'])
});

class ProofError extends Error {
  constructor(code, message) { super(message); this.code = code; }
}

export function parseSerial(text) {
  const value = String(text).trim();
  if (!/^[A-Za-z0-9][A-Za-z0-9._:-]{0,127}$/.test(value) || /^(unknown|null|undefined)$/i.test(value)) {
    throw new ProofError('INVALID_IDENTITY', 'The headset did not return one valid canonical serial.');
  }
  return value;
}

export function parseBootId(text) {
  const value = String(text).trim().toLowerCase();
  if (!/^[a-f0-9]{8}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{12}$/.test(value) || /^0{8}-0{4}-0{4}-0{4}-0{12}$/.test(value)) {
    throw new ProofError('INVALID_IDENTITY', 'The headset did not return a valid boot identifier.');
  }
  return value;
}

export function parseBattery(text) {
  const fields = new Map();
  for (const line of String(text).split(/\r?\n/)) {
    const match = /^\s*(level|scale|status|AC powered|USB powered|Wireless powered):\s*(\S+)\s*$/.exec(line);
    if (!match) continue;
    if (fields.has(match[1])) throw new ProofError('INVALID_BATTERY', 'The battery response contained duplicate fields.');
    fields.set(match[1], match[2]);
  }
  const number = name => /^\d+$/.test(fields.get(name) ?? '') ? Number(fields.get(name)) : NaN;
  const level = number('level'), scale = number('scale'), status = number('status');
  if (!Number.isSafeInteger(level) || !Number.isSafeInteger(scale) || scale < 1 || scale > 10000 || level > scale || ![1, 2, 3, 4, 5].includes(status)) {
    throw new ProofError('INVALID_BATTERY', 'The battery response was incomplete or outside its expected range.');
  }
  // Android BatteryManager: 1 unknown, 2 charging, 3 discharging, 4 not charging, 5 full.
  return Object.freeze({ level, scale, status, charging: status === 1 ? null : status === 2 });
}

export function createUsbProof(dependencies) {
  const records = new Map();
  const rawIds = new WeakMap();
  let nextId = 1;
  const capability = () => dependencies.capability();
  const unsupported = () => ({ state: 'unsupported', message: capability().message });
  const descriptor = record => ({ selectionId: record.id, name: record.device.name || 'ADB USB device', usbSerial: record.device.serial || '' });
  const envelope = (record, state, message, extra = {}) => ({ state, message, selectionId: record?.id, ...(record ? { device: descriptor(record) } : {}), ...extra });
  const notify = (callback, value) => { try { callback?.(value); } catch { /* UI callbacks cannot change transport ownership. */ } return value; };
  function remember(device) {
    const raw = device.raw;
    if (!raw || (typeof raw !== 'object' && typeof raw !== 'function')) throw new ProofError('INVALID_DEVICE', 'No exact USB handle was returned.');
    const existing = rawIds.get(raw);
    if (existing) return records.get(existing);
    const id = `usb-${nextId++}`;
    const record = { id, device, generation: 0, session: null, active: null, closing: null, closeFailed: false, pending: new Set(), serial: null, bootId: null };
    rawIds.set(raw, id); records.set(id, record); return record;
  }
  async function close(record) {
    const session = record.session;
    record.session = null;
    // Closing this exact USB handle never resets the device or touches a host ADB server.
    let timer;
    let pending = record.closing;
    if (!pending) {
      pending = Promise.allSettled([
        Promise.resolve().then(() => session?.close()),
        Promise.resolve().then(() => dependencies.closeDevice(record.device))
      ]);
      record.closing = pending;
      record.pending.add(pending);
      pending.then(results => { record.closeFailed = results[1].status === 'rejected'; record.pending.delete(pending); if (record.closing === pending) record.closing = null; });
    }
    try { await Promise.race([pending, new Promise(resolve => { timer = setTimeout(resolve, dependencies.closeTimeoutMs ?? 2000); })]); }
    finally { clearTimeout(timer); }
  }
  function failure(record, error) {
    if (dependencies.isBusyError(error)) return envelope(record, 'interface-busy', 'This USB interface is already in use. Release it in the owning application before retrying; this page will not stop ADB.', { errorCode: 'INTERFACE_BUSY' });
    if (error?.name === 'NotAllowedError' || error?.name === 'SecurityError') return envelope(record, 'not-permitted', 'USB permission was not granted to this page.', { errorCode: 'USB_PERMISSION' });
    if (error?.name === 'AbortError') return envelope(record, 'disconnected', 'The selected USB operation was cancelled.', { errorCode: 'CANCELLED' });
    if (error instanceof ProofError) return envelope(record, 'failed', error.message, { errorCode: error.code });
    return envelope(record, 'failed', 'The selected headset could not be read. Check its cable, debugging authorization and connection, then reconnect explicitly.', { errorCode: 'USB_READ_FAILED' });
  }
  async function getPermittedDevices() {
    if (!capability().supported) return [];
    return (await dependencies.manager.getDevices()).map(device => descriptor(remember(device)));
  }
  async function bounded(record, operation, milliseconds, code, message) {
    const controller = record.active;
    let timer, rejectAbort;
    const abort = () => rejectAbort(controller.signal.reason);
    const aborted = new Promise((_, reject) => { rejectAbort = reject; });
    controller.signal.addEventListener('abort', abort, { once: true });
    if (controller.signal.aborted) abort();
    // Retain the exact-handle lock until even a late underlying promise settles.
    // A timeout/cancel may return promptly, but must not overlap a new connection.
    const pending = Promise.resolve().then(() => { controller.signal.throwIfAborted(); return operation(controller.signal); });
    record.pending.add(pending);
    pending.then(() => record.pending.delete(pending), () => record.pending.delete(pending));
    const timeout = new Promise((_, reject) => {
      timer = setTimeout(() => { const error = new ProofError(code, message); controller.abort(error); reject(error); }, milliseconds);
    });
    try { return await Promise.race([pending, timeout, aborted]); }
    finally { clearTimeout(timer); controller.signal.removeEventListener('abort', abort); }
  }
  async function readIdentity(session, signal) {
    return { serial: parseSerial(await session.read(COMMANDS.serial, signal)), bootId: parseBootId(await session.read(COMMANDS.bootId, signal)) };
  }
  async function perform(record, connect, onState) {
    if (record.active || record.pending.size) return notify(onState, envelope(record, 'failed', 'An earlier operation is still settling for this exact USB selection. Wait before reconnecting.', { errorCode: 'OPERATION_BUSY' }));
    const generation = ++record.generation;
    record.active = new AbortController();
    const controller = record.active;
    const signal = controller.signal;
    const publish = value => generation === record.generation ? notify(onState, value) : envelope(record, 'disconnected', 'This USB operation was cancelled.');
    try {
      if (connect) {
        await close(record);
        signal.throwIfAborted();
        if (record.pending.size) throw new ProofError('OPERATION_BUSY', 'The previous USB connection is still closing. Wait before reconnecting this exact selection.');
        if (record.closeFailed) throw new ProofError('CLOSE_FAILED', 'The previous USB handle could not be confirmed closed. No new connection was opened.');
        publish(envelope(record, 'auth-prompt', 'Connecting and authorizing ADB. Check the headset if it asks to trust this browser.'));
        record.session = await bounded(record, async activeSignal => {
          const session = await dependencies.connectDevice(record.device, activeSignal);
          if (activeSignal.aborted || generation !== record.generation) { await session.close(); activeSignal.throwIfAborted(); throw new DOMException('Cancelled', 'AbortError'); }
          return session;
        }, dependencies.authTimeoutMs ?? 60000, 'AUTH_TIMEOUT', 'ADB authorization did not finish within one minute. Check the headset and reconnect explicitly.');
      }
      if (!record.session) throw new ProofError('NOT_CONNECTED', 'Reconnect this selected USB device before reading it.');
      const session = record.session;
      if (session.disconnected) {
          const ended = () => {
            if (generation !== record.generation || record.session !== session) return;
            record.session = null;
            record.active?.abort(new DOMException('USB transport disconnected', 'AbortError'));
            publish(envelope(record, 'disconnected', 'The selected USB transport disconnected. Reconnect explicitly to read it again.'));
          };
          Promise.resolve(session.disconnected).then(ended, ended);
      }
      const reading = await bounded(record, async activeSignal => {
        const before = await readIdentity(session, activeSignal);
        if (record.serial !== null && before.serial !== record.serial) throw new ProofError('TARGET_CHANGED', 'The canonical serial changed. This result is rejected; choose the intended headset again.');
        if (!connect && record.bootId !== null && before.bootId !== record.bootId) throw new ProofError('BOOT_CHANGED', 'The selected headset restarted. Reconnect explicitly before accepting a new reading.');
        const battery = parseBattery(await session.read(COMMANDS.battery, activeSignal));
        const after = await readIdentity(session, activeSignal);
        if (before.serial !== after.serial || before.bootId !== after.bootId) throw new ProofError('TARGET_CHANGED', 'Headset identity or boot changed during this read. No battery result was accepted.');
        activeSignal.throwIfAborted();
        return Object.freeze({ ...before, battery });
      }, dependencies.readTimeoutMs ?? 15000, 'READ_TIMEOUT', 'The selected headset did not complete its read within 15 seconds. Reconnect explicitly.');
      signal.throwIfAborted();
      record.serial = reading.serial; record.bootId = reading.bootId;
      return publish(envelope(record, 'connected', 'Read-only USB proof completed for this exact headset and boot.', { reading }));
    } catch (error) {
      await close(record);
      return publish(failure(record, signal.aborted ? signal.reason : error));
    } finally { if (record.active === controller) record.active = null; }
  }
  async function chooseAndRead({ onState } = {}) {
    if (!capability().supported) return notify(onState, unsupported());
    try {
      // No await before requestDevice: this call must run directly in a user click.
      const pending = dependencies.manager.requestDevice();
      const device = await pending;
      if (!device) return notify(onState, { state: 'not-permitted', message: 'No USB device was selected. Nothing was connected.', errorCode: 'NO_SELECTION' });
      return await perform(remember(device), true, onState);
    } catch (error) { return notify(onState, failure(null, error)); }
  }
  function selected(selectionId) {
    const record = records.get(selectionId);
    if (!record) throw new ProofError('INVALID_SELECTION', 'Choose one permitted USB device before continuing.');
    return record;
  }
  async function read(selectionId, { onState } = {}) {
    if (!capability().supported) return notify(onState, unsupported());
    try { return await perform(selected(selectionId), false, onState); } catch (error) { return notify(onState, failure(null, error)); }
  }
  async function reconnect(selectionId, { onState } = {}) {
    if (!capability().supported) return notify(onState, unsupported());
    try { return await perform(selected(selectionId), true, onState); } catch (error) { return notify(onState, failure(null, error)); }
  }
  async function disconnect(selectionId) {
    try {
      const record = selected(selectionId);
      record.generation++;
      record.active?.abort(new DOMException('Disconnected by operator', 'AbortError'));
      await close(record);
      if (record.pending.size) return envelope(record, 'failed', 'USB cleanup is still pending; closure is not confirmed. This exact handle remains locked until cleanup settles.', { errorCode: 'OPERATION_BUSY', cleanupPending: true, closureConfirmed: false });
      if (record.closeFailed) return envelope(record, 'failed', 'The USB handle could not be confirmed closed. Retry disconnect explicitly.', { errorCode: 'CLOSE_FAILED', cleanupPending: false, closureConfirmed: false });
      return envelope(record, 'disconnected', 'The selected USB connection is closed. Browser permission and its stored ADB key are retained.', { cleanupPending: false, closureConfirmed: true });
    } catch (error) { return failure(null, error); }
  }
  return Object.freeze({ capability, getPermittedDevices, chooseAndRead, read, reconnect, disconnect });
}
