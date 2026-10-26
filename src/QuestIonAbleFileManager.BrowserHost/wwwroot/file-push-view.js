const storageKey = 'qfm-browser-file-push-v1';
const hash = value => typeof value === 'string' && /^[a-f0-9]{64}$/.test(value);
const artifactId = value => typeof value === 'string' && /^[a-f0-9]{32}$/.test(value) && value !== '0'.repeat(32);
const serial = value => typeof value === 'string' && value.length <= 256 && /^[A-Za-z0-9._:-]+$/.test(value);
const requestId = value => typeof value === 'string' && /^[a-f0-9]{8}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{12}$/.test(value) && value !== '00000000-0000-0000-0000-000000000000';
function fileName(value) {
  return typeof value === 'string' && /^[A-Za-z0-9][A-Za-z0-9._ -]{0,127}$/.test(value) && !/[. ]$/.test(value) && !value.includes('..') &&
    !/^(?:CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])$/i.test(value.split('.')[0].trimEnd());
}
function remoteFile(value) {
  return typeof value === 'string' && value.length > 1 && value.length <= 256 && value.startsWith('/') && !/[\x00-\x1f\x7f-\x9f]/.test(value) &&
    value.slice(1).split('/').every(part => part !== '' && part !== '.' && part !== '..');
}
function limits(context) {
  const value = context.artifactStaging;
  return value?.supported === true && Number.isSafeInteger(value.maximumFileBytes) && value.maximumFileBytes > 0 &&
    Number.isSafeInteger(value.maximumTotalBytes) && value.maximumTotalBytes >= value.maximumFileBytes &&
    Number.isSafeInteger(value.maximumFiles) && value.maximumFiles > 0 && Number.isSafeInteger(value.transferTimeoutSeconds) && value.transferTimeoutSeconds > 0 ? value : null;
}
function reference(value) {
  if (!value || !requestId(value.requestId) || typeof value.sessionId !== 'string' || !/^[A-Za-z0-9_-]{1,128}$/.test(value.sessionId) || value.action !== 'files.push' ||
      !serial(value.serial) || !remoteFile(value.remotePath) || !artifactId(value.artifactId) || !hash(value.sha256) ||
      !Number.isSafeInteger(value.sizeBytes) || value.sizeBytes < 0 || value.sizeBytes > 1073741824) return null;
  return {requestId:value.requestId,sessionId:value.sessionId,action:'files.push',serial:value.serial,remotePath:value.remotePath,
    artifactId:value.artifactId,sha256:value.sha256,sizeBytes:value.sizeBytes};
}
const recovery = 'Preserve the original reference. Use the typed QFM CLI/owner route to inspect the exact target and destination. A missing browser request after a companion restart does not prove no effect. No automatic retry.';
const predispatchCodes = new Set(['busy','session_capacity_reached','target_not_ready','session_changed','unauthorized','origin_or_host_rejected',
  'artifact_not_found','artifact_integrity_mismatch','artifact_identity_changed','artifact_staging_unavailable','artifact_access_failed','artifact_operation_timeout',
  'artifact_binding_mismatch','artifact_size_invalid','artifact_hash_invalid','artifact_id_invalid','body_size_invalid','fields_invalid','request_invalid',
  'action_unsupported','confirmation_required','remote_path_invalid','remote_file_path_invalid','content_type_invalid','request_body_timeout']);

// The generic upload is memory-only. Only the minimal original push binding is
// persisted before dispatch; it never becomes an APK or retained-operation input.
export function createFilePushView({api,getContext,setBusy,showResult}) {
  const element = id => document.getElementById('file-push-' + id);
  const text = (id,value) => { element(id).textContent = value; };
  let source = null, original = null, damagedReference = false, storageAvailable = true;
  let uploading = false, pushing = false, removing = false, probing = false;
  let authStamp = '', scopeStamp = '', authEpoch = 0, scopeEpoch = 0, selectionEpoch = 0;
  let uploadMessage = 'No generic file staged.', message = 'No file push requested.';
  const connected = context => Boolean(context.connected && context.sessionId);
  const supports = context => context.actions?.has?.('files.push') === true;
  const hasUnresolved = () => Boolean(original || damagedReference);
  function persist() {
    try {
      if (original) sessionStorage.setItem(storageKey,JSON.stringify(reference(original)));
      else if (damagedReference) sessionStorage.setItem(storageKey,'{}');
      else sessionStorage.removeItem(storageKey);
      return true;
    } catch { storageAvailable = false; return false; }
  }
  try {
    const saved = sessionStorage.getItem(storageKey);
    if (saved) {
      try { original = saved.length <= 4096 ? reference(JSON.parse(saved)) : null; } catch { /* preserve a blocked, sanitized recovery marker */ }
      damagedReference = !original; persist();
      message = original ? 'Original push reference restored. Explicitly read its original result; nothing was replayed.' : 'Saved push reference could not be validated. New pushes stay blocked. ' + recovery;
    }
  } catch { storageAvailable = false; }
  function update() {
    const context = getContext(), capability = limits(context);
    const auth = JSON.stringify([context.sessionId,Boolean(context.connected),context.stagingGeneration ?? 0]);
    if (auth !== authStamp) {
      if (authStamp && source) uploadMessage = 'Connection or staging inventory changed. Generic source reference cleared; the original push reference is preserved.';
      authStamp = auth; authEpoch++; source = null; element('upload').value = '';
    }
    const scope = JSON.stringify([auth,context.selected?.serial,context.selected?.isReady,source?.artifact.id,source?.artifact.sha256,source?.artifact.sizeBytes,
      element('remote').value,element('name').value,selectionEpoch,Boolean(context.externalUnresolved),supports(context)]);
    if (scope !== scopeStamp) { scopeStamp = scope; scopeEpoch++; element('confirm').checked = false; }
    const local = Boolean(connected(context) && capability), live = local && supports(context);
    const busy = Boolean(context.busy || uploading || pushing || removing), owned = Boolean(source && local && source.sessionId === context.sessionId);
    const ready = context.selected?.isReady === true && serial(context.selected.serial);
    for (const id of ['upload','name']) element(id).disabled = !live || busy;
    const file = element('upload').files[0];
    element('stage').disabled = !live || busy || owned || !file || file.size > capability.maximumFileBytes || !fileName(element('name').value);
    element('remove').disabled = !owned || busy;
    element('remote').disabled = !live || busy;
    const admissible = live && owned && ready && !busy && !hasUnresolved() && !context.externalUnresolved && storageAvailable && source.artifact.sizeBytes <= capability.maximumFileBytes;
    element('confirm').disabled = !admissible || !remoteFile(element('remote').value);
    element('send').disabled = !admissible || !remoteFile(element('remote').value) || !element('confirm').checked;
    element('status').disabled = !original || !connected(context) || original.sessionId !== context.sessionId || probing;
    element('save').disabled = !original;
    text('capability', !connected(context) ? 'Connect to read protected staging and file push capabilities.' : capability
      ? supports(context) ? `Host limits: ${capability.maximumFileBytes.toLocaleString()} bytes per file, ${capability.maximumTotalBytes.toLocaleString()} bytes across ${capability.maximumFiles} files; ${capability.transferTimeoutSeconds} seconds per upload/push.`
        : 'This host does not advertise file push. Use the typed QFM CLI.'
      : 'Protected file staging is unavailable on this host. Use the supported Windows companion or typed QFM CLI.');
    text('target',ready ? context.selected.serial : 'No ready target');
    element('source').hidden = !source;
    if (source) {
      text('name-value',source.artifact.fileName); text('size',`${source.artifact.sizeBytes.toLocaleString()} bytes`);
      text('sha',source.artifact.sha256); text('artifact-id',source.artifact.id);
    }
    text('upload-state',uploadMessage + (owned ? ' Remove this exact staged file before staging another.' : ''));
    element('original').hidden = !original;
    if (original) {
      text('original-id',original.requestId); text('original-session',original.sessionId); text('original-target',original.serial); text('original-remote',original.remotePath);
      text('original-artifact',original.artifactId); text('original-sha',original.sha256); text('original-size',`${original.sizeBytes.toLocaleString()} bytes`);
    }
    text('state',message + (original ? ' This original remains unresolved; new pushes are blocked. ' + recovery : '') +
      (original && context.sessionId !== original.sessionId ? ' The current host session differs; original lookup is unavailable here. ' : '') +
      (context.externalUnresolved ? ' Another device mutation is unresolved; file push is blocked.' : '') +
      (!storageAvailable ? ' Session storage is unavailable; no new push can be dispatched safely. Use the typed CLI.' : ''));
  }
  function receipt(value, name, size, maximum) {
    const artifact = value?.artifact;
    if (value?.state !== 'staged' || value.scope !== 'localOnly' || value.deviceDispatched !== false || !artifactId(artifact?.id) ||
        !hash(artifact.sha256) || artifact.fileName !== name || !fileName(artifact.fileName) || artifact.sizeBytes !== size ||
        !Number.isSafeInteger(size) || size < 0 || size > 1073741824 || size > maximum)
      throw new Error('upload_receipt_invalid');
    return {id:artifact.id,fileName:artifact.fileName,sizeBytes:artifact.sizeBytes,sha256:artifact.sha256};
  }
  async function stage(event) {
    event.preventDefault(); update(); if (element('stage').disabled) return;
    const context = getContext(), file = element('upload').files[0], name = element('name').value, sentAuth = authEpoch;
    const sessionId = context.sessionId, maximum = limits(context).maximumFileBytes;
    uploading = true; setBusy(true); uploadMessage = 'Uploading one local file. Waiting for the protected size and SHA-256 receipt.'; update();
    try {
      const value = await api('/browser/v1/artifacts',{method:'POST',body:file,headers:{'Content-Type':'application/octet-stream','X-Qfm-File-Name':name}});
      const artifact = receipt(value,name,file.size,maximum); update();
      if (sentAuth !== authEpoch || !connected(getContext())) {
        uploadMessage = 'Upload reply arrived after connection or staging changed. No source adopted. Bounded local bytes may remain; no automatic retry.'; return;
      }
      source = {artifact,sessionId}; uploadMessage = 'Generic file staged locally. Review its source size and SHA-256 separately from the destination.';
      showResult(value,uploadMessage);
    } catch (error) { uploadMessage = `Upload unavailable: ${String(error?.message || 'No valid receipt').slice(0,256)}. No source adopted and no automatic retry.`; }
    finally { uploading = false; setBusy(false); update(); }
  }
  function markerMatches(marker, saved) {
    return marker && /^pc-[a-f0-9]{32}$/.test(marker.operationId) && marker.commandKind === 'pushFile' && marker.target === saved.serial && marker.desiredState === 'file present at ' + saved.remotePath;
  }
  function validate(value, saved, expectedName) {
    if (value?.requestId !== saved.requestId || value.action !== 'files.push' || value.result != null) throw new Error('push_response_binding_invalid');
    if (value.state === 'running' && value.artifactPush == null) return false;
    const push = value.artifactPush, artifact = push?.artifact;
    if (!push || push.serial !== saved.serial || push.remotePath !== saved.remotePath || push.verification !== 'remote-size' ||
        artifact?.id !== saved.artifactId || artifact.sha256 !== saved.sha256 || artifact.sizeBytes !== saved.sizeBytes || !fileName(artifact.fileName) ||
        (expectedName && artifact.fileName !== expectedName) || (value.mutation != null && !markerMatches(value.mutation,saved))) throw new Error('push_source_or_readback_invalid');
    if (value.state !== 'completed') return false;
    if (push.remoteSizeVerified !== true || !markerMatches(value.mutation,saved) || value.mutation.stage !== 'confirmed' || value.mutation.headsetReadback !== true)
      throw new Error('push_size_readback_unconfirmed');
    return true;
  }
  function acknowledge(value, saved, expectedName) {
    if (!validate(value,saved,expectedName)) {
      message = `Push outcome remains unresolved; owner reports ${String(value.state).slice(0,64)}. No matching completed remote-size proof.`;
      showResult(value,message); return;
    }
    original = null; persist();
    message = `Confirmed remote size: ${saved.sizeBytes.toLocaleString()} bytes at ${saved.serial}: ${saved.remotePath}. Remote SHA-256, atomic replacement and previous contents are not proven.`;
    showResult(value,message);
  }
  async function push() {
    update(); if (element('send').disabled) return;
    const context = getContext(), artifact = {...source.artifact};
    const saved = reference({requestId:crypto.randomUUID(),sessionId:context.sessionId,action:'files.push',serial:context.selected.serial,remotePath:element('remote').value,
      artifactId:artifact.id,sha256:artifact.sha256,sizeBytes:artifact.sizeBytes});
    if (!saved) { message = 'Push reference could not be validated. No Execute sent.'; update(); return; }
    original = saved;
    if (!persist()) { original = null; message = 'Push not sent: its original reference could not be persisted.'; update(); return; }
    const {sessionId,...request} = saved, sentAuth = authEpoch, sentScope = scopeEpoch;
    pushing = true; element('confirm').checked = false; setBusy(true); message = 'Original file push sent once. Waiting for remote-size readback; page loss does not cancel its effect.'; update();
    try {
      const value = await api('/browser/v1/execute',{method:'POST',body:JSON.stringify({...request,confirmOverwrite:true})}); update();
      if (original !== saved) return;
      if (sentAuth !== authEpoch || sentScope !== scopeEpoch || !connected(getContext())) {
        message = 'Push reply arrived after connection, source, target or destination changed. Original outcome remains unknown; explicitly read its original result.'; return;
      }
      acknowledge(value,saved,artifact.fileName);
    } catch (error) {
      update();
      if (original !== saved) return;
      if (sentAuth === authEpoch && connected(getContext()) && getContext().sessionId === saved.sessionId &&
          error.dispatched === false && predispatchCodes.has(error.message) && !/(?:consum|duplicat|already)/i.test(error.message)) {
        original = null; persist(); message = `Push rejected before dispatch: ${error.message}. Review the scope and approve a separate new request explicitly.`;
      } else {
        message = `Push reply unavailable or untrusted: ${String(error?.message || 'No acknowledgement').slice(0,256)}. Original outcome is unknown.`;
        showResult({...saved,state:'outcomeUnknown'},message);
      }
    } finally { pushing = false; setBusy(false); update(); }
  }
  async function status() {
    update(); if (element('status').disabled) return;
    const saved = original, sentAuth = authEpoch; probing = true; update();
    try {
      const value = await api('/browser/v1/requests/' + saved.requestId,{method:'GET'}); update();
      if (original !== saved) return;
      if (sentAuth !== authEpoch || !connected(getContext()) || getContext().sessionId !== saved.sessionId) {
        message = 'Original lookup reply arrived after authentication or host session changed. Original outcome remains unknown.'; return;
      }
      acknowledge(value,saved);
    } catch (error) {
      if (original === saved) message = `Original result unavailable: ${String(error?.message || 'No matching acknowledgement').slice(0,256)}. Missing or crossed status does not prove no effect.`;
    } finally { probing = false; setBusy(Boolean(getContext().busy)); update(); }
  }
  async function remove() {
    update(); if (element('remove').disabled) return;
    const selected = source, sentAuth = authEpoch; removing = true; setBusy(true); update();
    try {
      const value = await api('/browser/v1/artifacts/' + selected.artifact.id,{method:'DELETE'}); update();
      if (value?.state !== 'removed' || value.scope !== 'localOnly' || value.deviceDispatched !== false || value.artifactId !== selected.artifact.id) throw new Error('removal_receipt_invalid');
      if (source === selected && sentAuth === authEpoch) source = null;
      uploadMessage = 'Only this generic staged file was removed. Any original push reference is preserved.';
    } catch (error) { uploadMessage = `Staging removal unavailable: ${String(error?.message || 'No matching receipt').slice(0,256)}. Original push reference is preserved.`; }
    finally { removing = false; setBusy(false); update(); }
  }
  element('upload-form').addEventListener('submit',stage);
  element('upload').addEventListener('change',() => { selectionEpoch++; const file = element('upload').files[0]; element('name').value = file && fileName(file.name) ? file.name : ''; update(); });
  for (const id of ['name','remote']) element(id).addEventListener('input',update);
  element('confirm').addEventListener('change',update); element('send').addEventListener('click',push); element('remove').addEventListener('click',remove); element('status').addEventListener('click',status);
  element('save').addEventListener('click',() => {
    if (!original) return;
    const url = URL.createObjectURL(new Blob([JSON.stringify(reference(original),null,2)],{type:'application/json'}));
    const link = document.createElement('a'); link.href = url; link.download = 'qfm-file-push-reference.json'; link.click(); setTimeout(() => URL.revokeObjectURL(url),1000);
  });
  update();
  return {update,hasUnresolved};
}
