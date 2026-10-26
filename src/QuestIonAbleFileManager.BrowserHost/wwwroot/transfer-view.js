const hash = value => typeof value === 'string' && /^[a-f0-9]{64}$/.test(value);
const serial = value => typeof value === 'string' && value.length <= 256 && /^[A-Za-z0-9._:-]+$/.test(value);
const packageName = value => typeof value === 'string' && value.length <= 256 && /^[A-Za-z0-9_]+(?:\.[A-Za-z0-9_]+)+$/.test(value);
const remoteFile = (value, maximum = 256) => typeof value === 'string' && value.length > 1 && value.length <= maximum && value.startsWith('/') && !value.endsWith('/') && !/[\0\r\n]/.test(value);
function fileName(value) {
  return typeof value === 'string' && /^[A-Za-z0-9][A-Za-z0-9._ -]{0,127}$/.test(value) && !/[. ]$/.test(value) && !value.includes('..') &&
    !/^(?:CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])$/i.test(value.split('.')[0].trimEnd());
}
function limits(context) {
  const value = context.artifactStaging;
  return value?.supported === true && Number.isSafeInteger(value.maximumFileBytes) && value.maximumFileBytes > 0 &&
    Number.isSafeInteger(value.maximumTotalBytes) && value.maximumTotalBytes >= value.maximumFileBytes &&
    Number.isSafeInteger(value.maximumFiles) && value.maximumFiles > 0 &&
    Number.isSafeInteger(value.transferTimeoutSeconds) && value.transferTimeoutSeconds > 0 ? value : null;
}

// Read captures have their own in-memory output. They never select a local APK,
// store credentials/results, or replace an unresolved retained operation.
export function createTransferView({api, getContext, setBusy, showResult, saveArtifact}) {
  const element = id => document.getElementById(id);
  const text = (id, value) => { element(id).textContent = value; };
  let output = null, capturing = false, saving = false, removing = false;
  let authStamp = '', targetStamp = '', packageStamp, authEpoch = 0, targetEpoch = 0;
  let message = 'No read capture requested. Select one ready headset and an exact file or package.';
  const connected = context => Boolean(context.connected && context.sessionId);
  const ready = context => Boolean(context.selected?.isReady && serial(context.selected.serial));
  const supports = (context, action) => context.actions?.has?.(action) === true;
  function update() {
    const context = getContext(), capability = limits(context);
    const auth = JSON.stringify([Boolean(context.connected), context.sessionId]);
    const target = JSON.stringify([context.selected?.serial, context.selected?.isReady]);
    if (auth !== authStamp) { authStamp = auth; authEpoch++; }
    if (target !== targetStamp) { targetStamp = target; targetEpoch++; }
    const selectedPackage = typeof context.selectedPackage === 'string' ? context.selectedPackage : '';
    if (selectedPackage !== packageStamp) { packageStamp = selectedPackage; element('transfer-package').value = selectedPackage; }
    const busy = Boolean(context.busy || capturing || saving || removing);
    const live = Boolean(connected(context) && capability), targetReady = ready(context);
    for (const id of ['transfer-file-path', 'transfer-file-name', 'transfer-package', 'transfer-export-name']) element(id).disabled = !live || busy;
    element('transfer-pull').disabled = !live || !targetReady || busy || !supports(context, 'files.pull') ||
      !remoteFile(element('transfer-file-path').value) || !fileName(element('transfer-file-name').value);
    element('transfer-export').disabled = !live || !targetReady || busy || !supports(context, 'apk.export') ||
      !packageName(element('transfer-package').value) || !fileName(element('transfer-export-name').value) || !/\.apk$/i.test(element('transfer-export-name').value);
    const owned = Boolean(output && live && output.sessionId === context.sessionId);
    const saveable = Boolean(owned && targetReady && output.serial === context.selected.serial && output.artifact.sizeBytes <= capability.maximumFileBytes);
    element('transfer-save').disabled = !saveable || busy;
    element('transfer-remove').disabled = !owned || busy;
    element('transfer-save-checksum').disabled = !saveable || busy || !output?.checksumText;
    element('transfer-save-checksum').hidden = !output?.checksumText;
    text('transfer-target', targetReady ? context.selected.serial : 'No ready target');
    text('transfer-capability', !connected(context) ? 'Connect to read protected staging limits and available read actions.' : capability
      ? `Host limits: ${capability.maximumFileBytes.toLocaleString()} bytes per file, ${capability.maximumTotalBytes.toLocaleString()} bytes across ${capability.maximumFiles} files; ${capability.transferTimeoutSeconds} seconds per transfer.`
      : 'This host does not supply protected staging limits. Use the qualified companion or typed CLI.');
    element('transfer-output').hidden = !output;
    element('transfer-checksum').hidden = !output?.checksumText;
    if (output) {
      text('transfer-output-name', output.artifact.fileName); text('transfer-output-target', output.serial);
      text('transfer-output-source', output.packageName ? `${output.packageName} — ${output.sourcePath}` : output.sourcePath);
      text('transfer-output-size', `${output.artifact.sizeBytes.toLocaleString()} bytes`); text('transfer-output-sha', output.artifact.sha256);
      text('transfer-checksum', output.checksumText || '');
    }
    text('transfer-state', message + (output && !owned ? ' This output belongs to its original host session; it cannot be reused through another session.' :
      output && !saveable ? ' Reselect the captured target before saving these bytes.' : ''));
  }
  function validate(value, request, maximum) {
    const transfer = value?.artifactTransfer, artifact = transfer?.artifact;
    if (value?.requestId !== request.requestId || value.action !== request.action || value.state !== 'completed' ||
        transfer?.serial !== request.serial || !artifact || typeof artifact.id !== 'string' || !/^[a-f0-9]{32}$/.test(artifact.id) || artifact.id === '0'.repeat(32) ||
        artifact.fileName !== request.fileName || !fileName(artifact.fileName) || !hash(artifact.sha256) ||
        !Number.isSafeInteger(artifact.sizeBytes) || artifact.sizeBytes < 0 || artifact.sizeBytes > maximum)
      throw new Error(value?.state === 'failed' ? value.error || 'capture_failed' : 'capture_receipt_invalid');
    if (request.action === 'files.pull' ? transfer.sourcePath !== request.remotePath || transfer.packageName != null || transfer.checksumText != null :
        transfer.packageName !== request.package || !remoteFile(transfer.sourcePath, 4096) || !/\.apk$/i.test(transfer.sourcePath) || transfer.checksumText !== `${artifact.sha256}  ${artifact.fileName}\n`)
      throw new Error('capture_source_invalid');
    return {artifact:{id:artifact.id, fileName:artifact.fileName, sizeBytes:artifact.sizeBytes, sha256:artifact.sha256},
      serial:transfer.serial, sourcePath:transfer.sourcePath, packageName:transfer.packageName || null, checksumText:transfer.checksumText || null};
  }
  async function capture(action) {
    update(); if (element(action === 'files.pull' ? 'transfer-pull' : 'transfer-export').disabled) return;
    const context = getContext(), capability = limits(context), sentAuth = authEpoch, sentTarget = targetEpoch;
    const request = {requestId:crypto.randomUUID(), action, serial:context.selected.serial,
      ...(action === 'files.pull' ? {remotePath:element('transfer-file-path').value, fileName:element('transfer-file-name').value}
        : {package:element('transfer-package').value, fileName:element('transfer-export-name').value})};
    const sessionId = context.sessionId;
    output = null; capturing = true; setBusy(true);
    message = 'Read request sent once. Waiting for captured size and SHA-256; no save has started.'; update();
    try {
      const value = await api('/browser/v1/execute', {method:'POST', body:JSON.stringify(request)});
      const captured = validate(value, request, capability.maximumFileBytes);
      update();
      if (sentAuth !== authEpoch || sentTarget !== targetEpoch || !connected(getContext())) {
        message = 'Read reply arrived after authentication or target changed. No output selected. Bounded local bytes may remain; clear staged files only through the separate explicit control.';
        showResult({requestId:request.requestId, action, state:'captureScopeChanged'}, message); return;
      }
      output = {...captured, sessionId};
      message = 'Captured bytes are staged separately. Review the source, target, size and SHA-256, then explicitly Save or Remove.';
      showResult(value, message);
    } catch (error) {
      message = `Read capture unavailable: ${error.message}. No save started and no automatic retry. Bounded local bytes may remain; use the separate Clear staged files control if needed.`;
      if (error.message === 'split_export_unsupported') message += ' A split APK set cannot be exported as a single APK; use the supported complete-set workflow.';
      showResult({requestId:request.requestId, action, state:'captureUnavailable', error:error.message}, message);
    } finally { capturing = false; setBusy(false); update(); }
  }
  function save() {
    update(); if (element('transfer-save').disabled) return;
    const captured = output; saving = true; setBusy(true); update();
    let operation;
    try { operation = saveArtifact({...captured.artifact}); } // Direct click call preserves the browser picker gesture.
    catch (error) { operation = Promise.reject(error); }
    Promise.resolve(operation).then(value => {
      const matches = value?.artifact && ['id', 'fileName', 'sizeBytes', 'sha256'].every(name => value.artifact[name] === captured.artifact[name]);
      if (!matches || !((value.state === 'saved' && value.mode === 'streaming') || (value.state === 'downloadStarted' && value.mode === 'buffered')))
        throw Object.assign(new Error('The save helper did not return a matching verified outcome.'), {saveOutcomeUnknown:true});
      message = value.state === 'saved'
        ? 'Streaming save verified and destination close acknowledged. Captured bytes remain staged until explicitly removed.'
        : 'Browser download started after byte verification. Final disk save is not confirmed; check browser downloads. Captured bytes remain staged.';
    })
      .catch(error => {
        const detail = String(error?.message || 'No verified save result was returned').replace(/[.!?]$/, '');
        message = `${error?.saveOutcomeUnknown === true ? 'Save outcome unknown' : 'Save not completed'}: ${detail}. Captured bytes remain staged.`;
        if (error?.cleanupPending === true) message += ' Browser cleanup is still pending; wait for cleanup and check the selected file before another Save.';
        else if (error?.saveOutcomeUnknown === true) message += ' Check the selected file and browser downloads before another Save.';
        if (typeof error?.alternative === 'string' && error.alternative.length <= 2048) message += ` ${error.alternative}`;
      })
      .finally(() => { saving = false; setBusy(false); update(); });
  }
  async function remove() {
    update(); if (element('transfer-remove').disabled) return;
    const captured = output; removing = true; setBusy(true); update();
    try {
      const value = await api(`/browser/v1/artifacts/${captured.artifact.id}`, {method:'DELETE'});
      if (value?.state !== 'removed' || value.artifactId !== captured.artifact.id || value.scope !== 'localOnly' || value.deviceDispatched !== false)
        throw new Error('removal_receipt_invalid');
      if (output === captured) output = null;
      message = 'Only this captured staging artifact was removed. Headset bytes and original files are unchanged.';
    } catch (error) { message = `Captured staging removal unavailable: ${error.message}. No automatic retry or global cleanup.`; }
    finally { removing = false; setBusy(false); update(); }
  }
  element('transfer-pull-form').addEventListener('submit', event => { event.preventDefault(); capture('files.pull'); });
  element('transfer-export-form').addEventListener('submit', event => { event.preventDefault(); capture('apk.export'); });
  for (const id of ['transfer-file-path', 'transfer-file-name', 'transfer-package', 'transfer-export-name']) element(id).addEventListener('input', update);
  element('transfer-save').addEventListener('click', save); element('transfer-remove').addEventListener('click', remove);
  element('transfer-save-checksum').addEventListener('click', () => {
    update(); if (element('transfer-save-checksum').disabled) return;
    const url = URL.createObjectURL(new Blob([output.checksumText], {type:'text/plain'}));
    const link = document.createElement('a'); link.href = url; link.download = output.artifact.fileName + '.sha256'; link.click();
    setTimeout(() => URL.revokeObjectURL(url), 1000);
  });
  function selectRemoteFile(path) {
    const context = getContext();
    if (context.busy || capturing || saving || removing || !remoteFile(path)) return false;
    element('transfer-file-path').value = path;
    const name = path.slice(path.lastIndexOf('/') + 1); element('transfer-file-name').value = fileName(name) ? name : '';
    update(); return true;
  }
  update();
  return {update, selectRemoteFile};
}
