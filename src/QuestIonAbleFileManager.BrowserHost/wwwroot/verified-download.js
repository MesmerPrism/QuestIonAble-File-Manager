import {sha256} from './vendor/noble-sha256.js';

export const MAXIMUM_BUFFERED_DOWNLOAD_BYTES = 256 * 1024 * 1024;
export const MAXIMUM_ARTIFACT_BYTES = 1024 * 1024 * 1024;
const hashHeader = 'X-Qfm-Artifact-Sha256', sizeHeader = 'X-Qfm-Artifact-Size-Bytes';
const alternatives = 'Use a supporting Chrome or Edge browser with streaming Save, or the existing QFM CLI. The staged artifact remains on the host.';
const messages = {
  artifact_receipt_invalid:'The artifact receipt is invalid.', artifact_url_invalid:'Download requires the exact artifact route on this local host origin.',
  artifact_headers_invalid:'Download headers do not match the artifact receipt.', buffered_limit_invalid:'The buffered download limit must be between zero and 256 MiB.',
  artifact_response_invalid:'The download response does not match its bound artifact receipt.', artifact_size_mismatch:'Downloaded bytes do not match the receipt size.',
  artifact_digest_mismatch:'Downloaded bytes do not match the receipt SHA-256.', download_cancelled:'Download cancelled; no successful save is reported.',
  download_rejected:'The host rejected this artifact download. Keep the receipt and check the current host/session availability.',
  download_failed:'The verified download failed. No successful save is reported.', download_save_failed:'The destination did not acknowledge a successful close. Check the selected file before another save.',
  download_picker_unavailable:'The browser could not open the save picker. Choose Save directly and approve the browser prompt.',
  download_timeout:'The verified download exceeded its bounded lifetime. No successful save is reported; the staged artifact remains on the host.',
  download_timeout_invalid:'Download lifetime must be 1..300000 milliseconds and cleanup lifetime 1..2000 milliseconds.',
  streaming_save_unavailable:'This artifact exceeds the bounded buffered download limit and requires streaming Save.'
};
export class ArtifactDownloadError extends Error {
  constructor(code) { super(messages[code] || messages.download_failed); this.name = 'ArtifactDownloadError'; this.code = code;
    if (code === 'streaming_save_unavailable' || code === 'download_picker_unavailable') this.alternative = alternatives;
  }
}
const fail = code => { throw new ArtifactDownloadError(code); };
const isHash = value => typeof value === 'string' && /^[0-9a-f]{64}$/.test(value);
const hex = bytes => Array.from(bytes, byte => byte.toString(16).padStart(2, '0')).join('');

function boundReceipt(value) {
  if (!value || typeof value.id !== 'string' || !/^[0-9a-f]{32}$/.test(value.id) || /^0{32}$/.test(value.id) ||
    !isHash(value.sha256) || !Number.isSafeInteger(value.sizeBytes) || value.sizeBytes < 0 || value.sizeBytes > MAXIMUM_ARTIFACT_BYTES ||
    typeof value.fileName !== 'string' || value.fileName.length > 128 || !/^[a-zA-Z0-9][a-zA-Z0-9._ -]*$/.test(value.fileName) ||
    /[. ]$/.test(value.fileName) || value.fileName.includes('..')) fail('artifact_receipt_invalid');
  const stem = value.fileName.split('.')[0].trimEnd().toUpperCase();
  if (/^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])$/.test(stem)) fail('artifact_receipt_invalid');
  return Object.freeze({id:value.id, fileName:value.fileName, sizeBytes:value.sizeBytes, sha256:value.sha256});
}
function boundUrl(artifact, input) {
  if (location.protocol !== 'http:' || !(location.hostname === '[::1]' || /^127(?:\.\d{1,3}){3}$/.test(location.hostname))) fail('artifact_url_invalid');
  const expected = `${location.origin}/browser/v1/artifacts/${artifact.id}`;
  let actual;
  try { if (input !== undefined && typeof input !== 'string') fail('artifact_url_invalid'); actual = new URL(input ?? expected, location.origin).href; }
  catch { fail('artifact_url_invalid'); }
  if (actual !== expected) fail('artifact_url_invalid');
  return expected;
}
function boundHeaders(input, artifact) {
  let headers;
  try { headers = new Headers(input); } catch { fail('artifact_headers_invalid'); }
  if ((headers.has(hashHeader) && headers.get(hashHeader) !== artifact.sha256) ||
      (headers.has(sizeHeader) && headers.get(sizeHeader) !== String(artifact.sizeBytes))) fail('artifact_headers_invalid');
  headers.set(hashHeader, artifact.sha256); headers.set(sizeHeader, String(artifact.sizeBytes));
  return headers;
}
function checkResponse(response, artifact, url) {
  if (!response.ok) { const error = new ArtifactDownloadError('download_rejected'); error.status = response.status; throw error; }
  if (response.redirected || (response.url && response.url !== url) ||
    response.headers.get(hashHeader) !== artifact.sha256 || response.headers.get(sizeHeader) !== String(artifact.sizeBytes) ||
    response.headers.get('Content-Length') !== String(artifact.sizeBytes) ||
    response.headers.get('Content-Type')?.split(';')[0].trim().toLowerCase() !== 'application/octet-stream' ||
    (!response.body && artifact.sizeBytes > 0)) fail('artifact_response_invalid');
}

/** Call directly from Save's click handler. The picker is invoked before the
 * first await or fetch. Neither credentials nor downloaded bytes are persisted.
 * Streaming returns saved only after verified close. The buffered fallback can
 * acknowledge browser download initiation, not the browser's final disk save.
 */
export async function saveArtifact(receipt, {url, headers, signal, maxBufferedBytes = MAXIMUM_BUFFERED_DOWNLOAD_BYTES,
  timeoutMilliseconds = 300000, cleanupTimeoutMilliseconds = 2000, onProgress} = {}) {
  let artifact, reader, writable, response, hasher, closeStarted = false, committed = false, phase = 'validate';
  let deadlineTimer, deadlineAt = Infinity, timedOut = false, pendingNative = 0, readerCancellation, outputAbort;
  const requestCancellation = new AbortController();
  const externalAbort = () => requestCancellation.abort();
  const cancellationError = () => new ArtifactDownloadError(timedOut ? 'download_timeout' : 'download_cancelled');
  const checkCancelled = () => {
    if (performance.now() >= deadlineAt) { timedOut = true; requestCancellation.abort(); }
    if (requestCancellation.signal.aborted || signal?.aborted) throw cancellationError();
  };
  const beginLifetime = () => {
    deadlineAt = performance.now() + timeoutMilliseconds;
    deadlineTimer = setTimeout(() => { timedOut = true; requestCancellation.abort(); }, timeoutMilliseconds);
  };
  // Native picker/filesystem promises do not necessarily observe AbortSignal.
  // Stop awaiting at the deadline without starting another write or close.
  const interruptible = promise => new Promise((resolve, reject) => {
    const underlying = Promise.resolve(promise);
    if (requestCancellation.signal.aborted) { void underlying.catch(() => {}); reject(cancellationError()); return; }
    const aborted = () => { requestCancellation.signal.removeEventListener('abort', aborted); reject(cancellationError()); };
    requestCancellation.signal.addEventListener('abort', aborted, {once:true});
    underlying.then(value => { requestCancellation.signal.removeEventListener('abort', aborted); resolve(value); },
      error => { requestCancellation.signal.removeEventListener('abort', aborted); reject(error); });
  });
  const nativeWait = promise => {
    pendingNative++;
    const native = Promise.resolve(promise);
    native.then(() => pendingNative--, () => pendingNative--);
    return interruptible(native);
  };
  const cancelReader = () => reader ? (readerCancellation ||= Promise.resolve().then(() => reader.cancel())) : Promise.resolve();
  const abortOutput = () => writable && !committed ? (outputAbort ||= Promise.resolve().then(() => writable.abort())) : Promise.resolve();
  const abortResources = () => { void cancelReader().catch(() => {}); void abortOutput().catch(() => {}); };
  const cleanup = async () => {
    const attempts = [cancelReader(), abortOutput()];
    if (!reader && response?.body) attempts.push(Promise.resolve().then(() => response.body.cancel()));
    let timer;
    try {
      return await Promise.race([Promise.allSettled(attempts).then(results => results.every(result => result.status === 'fulfilled')),
        new Promise(resolve => { timer = setTimeout(() => resolve(false), cleanupTimeoutMilliseconds); })]);
    } finally { clearTimeout(timer); }
  };
  const notify = (event, received = 0) => {
    try { onProgress?.({phase:event, bytesReceived:received, totalBytes:artifact.sizeBytes}); }
    catch { /* Progress observes the operation; it does not control commit. */ }
  };
  let received = 0;
  try {
    artifact = boundReceipt(receipt);
    const target = boundUrl(artifact, url), requestHeaders = boundHeaders(headers, artifact);
    if (!Number.isSafeInteger(maxBufferedBytes) || maxBufferedBytes < 0 || maxBufferedBytes > MAXIMUM_BUFFERED_DOWNLOAD_BYTES) fail('buffered_limit_invalid');
    if (!Number.isSafeInteger(timeoutMilliseconds) || timeoutMilliseconds < 1 || timeoutMilliseconds > 300000 ||
      !Number.isSafeInteger(cleanupTimeoutMilliseconds) || cleanupTimeoutMilliseconds < 1 || cleanupTimeoutMilliseconds > 2000) fail('download_timeout_invalid');
    if (onProgress !== undefined && typeof onProgress !== 'function') fail('download_failed');
    checkCancelled(); signal?.addEventListener('abort', externalAbort, {once:true});
    requestCancellation.signal.addEventListener('abort', abortResources);
    const streaming = typeof globalThis.showSaveFilePicker === 'function';
    let destination;
    if (streaming) {
      phase = 'picker';
      // Do not insert asynchronous work ahead of this user-activation boundary.
      const selection = globalThis.showSaveFilePicker({suggestedName:artifact.fileName});
      notify('choosingDestination'); destination = await interruptible(selection); checkCancelled();
    } else if (artifact.sizeBytes > maxBufferedBytes) fail('streaming_save_unavailable');
    // Human picker time is excluded; all subsequent receive/write/verify/close
    // waits have one finite lifetime. Cleanup has its own smaller bound.
    beginLifetime();
    phase = 'receive'; checkCancelled();
    response = await interruptible(fetch(target, {method:'GET', headers:requestHeaders, credentials:'omit', cache:'no-store', redirect:'error', signal:requestCancellation.signal}));
    checkCancelled(); checkResponse(response, artifact, target);
    if (streaming) {
      const creation = Promise.resolve(destination.createWritable());
      // A timed-out create may return later. Retain exactly that handle for
      // abort only; no later write/close or new download follows it.
      creation.then(created => { writable = created; if (requestCancellation.signal.aborted) void abortOutput().catch(() => {}); }, () => {});
      await nativeWait(creation); checkCancelled();
      hasher = sha256.create();
    }
    reader = response.body?.getReader();
    const chunks = []; notify('receiving');
    while (reader) {
      checkCancelled(); const next = await interruptible(reader.read()); checkCancelled();
      if (next.done) break;
      if (!(next.value instanceof Uint8Array)) fail('download_failed');
      if (next.value.byteLength > artifact.sizeBytes - received) fail('artifact_size_mismatch');
      received += next.value.byteLength;
      if (streaming) { hasher.update(next.value); checkCancelled(); await nativeWait(writable.write(next.value)); checkCancelled(); }
      else chunks.push(next.value.slice());
      notify('receiving', received);
    }
    if (received !== artifact.sizeBytes) fail('artifact_size_mismatch');
    checkCancelled(); notify('verifying', received);
    let blob;
    const digest = streaming ? hex(hasher.digest()) :
      hex(new Uint8Array(await interruptible(crypto.subtle.digest('SHA-256', await interruptible((blob = new Blob(chunks, {type:'application/octet-stream'})).arrayBuffer())))));
    checkCancelled();
    if (digest !== artifact.sha256) fail('artifact_digest_mismatch');
    if (streaming) {
      phase = 'close'; notify('saving', received); checkCancelled(); closeStarted = true;
      await nativeWait(writable.close()); checkCancelled(); committed = true;
      notify('complete', received);
      return {state:'saved', mode:'streaming', artifact};
    }
    const blobUrl = URL.createObjectURL(blob), link = document.createElement('a');
    try {
      link.href = blobUrl; link.download = artifact.fileName; link.hidden = true; document.body.append(link);
      checkCancelled(); link.click();
    } catch (error) { URL.revokeObjectURL(blobUrl); throw error; }
    finally { link.remove(); }
    setTimeout(() => URL.revokeObjectURL(blobUrl), 1000);
    notify('downloadStarted', received);
    return {state:'downloadStarted', mode:'buffered', artifact};
  } catch (error) {
    const safeError = error instanceof ArtifactDownloadError ? error : new ArtifactDownloadError(
      timedOut ? 'download_timeout' : signal?.aborted || error?.name === 'AbortError' ? 'download_cancelled' : phase === 'close' ? 'download_save_failed' : phase === 'picker' ? 'download_picker_unavailable' : 'download_failed');
    if (closeStarted && !committed) safeError.saveOutcomeUnknown = true;
    requestCancellation.abort();
    if (!await cleanup() || pendingNative > 0) safeError.cleanupPending = true;
    throw safeError;
  } finally {
    signal?.removeEventListener('abort', externalAbort);
    clearTimeout(deadlineTimer);
    requestCancellation.signal.removeEventListener('abort', abortResources);
    if (reader) { try { reader.releaseLock(); } catch { /* Already released/cancelled. */ } }
    hasher?.destroy();
  }
}
