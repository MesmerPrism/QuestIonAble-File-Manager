import { Adb, AdbDaemonTransport, AdbDaemonWebUsbDevice, AdbDaemonWebUsbDeviceManager, AdbWebCredentialStore } from './vendor/tango-adb.js';
import { createUsbProof } from './usb-proof-core.js';

function capability() {
  const secureContext = globalThis.isSecureContext === true;
  const supported = secureContext && Boolean(AdbDaemonWebUsbDeviceManager.BROWSER && globalThis.crypto?.subtle && globalThis.indexedDB);
  return { supported, secureContext, state: supported ? 'disconnected' : 'unsupported', message: supported
    ? 'Direct USB ADB is available. Choose one authorized headset to read its identity and battery.'
    : 'Direct USB ADB needs a secure context, WebUSB, Web Crypto and IndexedDB. Use a supported desktop Chromium browser over HTTPS or localhost.' };
}

const store = new AdbWebCredentialStore('QuestIonAble File Manager');
async function closeDevice(device) {
  if (device.raw.opened) await device.raw.close();
  if (device.raw.opened) throw new Error('The exact USB handle remained open.');
}

async function readText(stream, signal, limit = 16384) {
  const reader = stream.getReader();
  const decoder = new TextDecoder();
  let length = 0, text = '';
  try {
    while (true) {
      signal.throwIfAborted();
      const { done, value } = await reader.read();
      if (done) return text + decoder.decode();
      length += value.byteLength;
      if (length > limit) throw new Error('Fixed read response exceeded its bound.');
      text += decoder.decode(value, { stream: true });
    }
  } finally { reader.releaseLock(); }
}

async function connectDevice(device, signal) {
  const abort = () => { closeDevice(device).catch(() => {}); };
  signal.addEventListener('abort', abort, { once: true });
  let adb;
  try {
    signal.throwIfAborted();
    const connection = await device.connect();
    signal.throwIfAborted();
    const transport = await AdbDaemonTransport.authenticate({ serial: device.serial, connection, credentialStore: store });
    adb = new Adb(transport);
    signal.throwIfAborted();
    if (!adb.subprocess.shellProtocol) throw new Error('ADB shell v2 is required for checked command exit status.');
    return {
      async read(command, readSignal) {
        readSignal.throwIfAborted();
        const process = await adb.subprocess.shellProtocol.spawn(command, readSignal);
        const stop = () => { Promise.resolve().then(() => process.kill()).catch(() => {}); };
        readSignal.addEventListener('abort', stop, { once: true });
        try {
          const [stdout, stderr, exitCode] = await Promise.all([readText(process.stdout, readSignal), readText(process.stderr, readSignal, 4096), process.exited]);
          if (exitCode !== 0 || stderr.trim()) throw new Error('Fixed read command failed.');
          return stdout;
        } catch (error) { stop(); throw error; }
        finally { readSignal.removeEventListener('abort', stop); }
      },
      disconnected: adb.disconnected,
      close: () => adb.close()
    };
  } catch (error) {
    try { await adb?.close(); } catch { /* transport may already be gone */ }
    await closeDevice(device).catch(() => {});
    throw error;
  } finally { signal.removeEventListener('abort', abort); }
}

const proof = createUsbProof({
  manager: AdbDaemonWebUsbDeviceManager.BROWSER,
  capability,
  connectDevice,
  closeDevice,
  isBusyError: error => error instanceof AdbDaemonWebUsbDevice.DeviceBusyError
});

export { capability };
export const getPermittedDevices = proof.getPermittedDevices;
export const chooseAndRead = proof.chooseAndRead;
export const read = proof.read;
export const reconnect = proof.reconnect;
export const disconnect = proof.disconnect;
