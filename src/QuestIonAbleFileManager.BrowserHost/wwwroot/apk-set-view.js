const storageKey = 'qfm-browser-apk-set-original-v1', action = 'apk.install-set';
const contractVersion = 'questionable.file_manager.browser_apk_set.v1';
const optionNames = ['replaceExisting','allowDowngrade','grantRuntimePermissions','allowTestPackages'];
const optionIds = ['replace','downgrade','grant','test'];
const closed = (value, required, optional = []) => value && typeof value === 'object' && !Array.isArray(value) && required.every(key => Object.hasOwn(value,key)) && Object.keys(value).every(key => required.includes(key) || optional.includes(key));
const guid = value => typeof value === 'string' && /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/.test(value) && value !== '00000000-0000-0000-0000-000000000000';
const serial = value => typeof value === 'string' && /^[A-Za-z0-9._:-]{1,256}$/.test(value);
const byteLength = value => new TextEncoder().encode(value).byteLength;
const copy = value => value == null ? null : JSON.parse(JSON.stringify(value));
function fileName(value) {
  if (typeof value !== 'string' || !/^[A-Za-z0-9][A-Za-z0-9._ -]{0,127}$/.test(value) || /[. ]$/.test(value) || value.includes('..') || !/\.apk$/i.test(value)) return false;
  const stem = value.split('.')[0].trimEnd().toUpperCase();
  return !/^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])$/.test(stem);
}
function source(value) {
  if (!closed(value,['id','fileName','sizeBytes','sha256']) || typeof value.id !== 'string' || !/^[0-9a-f]{32}$/.test(value.id) || value.id === '0'.repeat(32) || !fileName(value.fileName) || !Number.isSafeInteger(value.sizeBytes) || value.sizeBytes <= 0 || value.sizeBytes > 1073741824 || typeof value.sha256 !== 'string' || !/^[0-9a-f]{64}$/.test(value.sha256)) return null;
  return {id:value.id,fileName:value.fileName,sizeBytes:value.sizeBytes,sha256:value.sha256};
}
function sources(values,minimum = 2) {
  if (!Array.isArray(values) || values.length < minimum || values.length > 8) return null;
  const checked = values.map(source);
  if (checked.some(value => !value) || new Set(checked.map(value => value.id)).size !== checked.length || new Set(checked.map(value => value.fileName.toLowerCase())).size !== checked.length || checked.reduce((sum,value) => sum + value.sizeBytes,0) > 2147483648) return null;
  return checked;
}
function options(value) { return closed(value,optionNames) && optionNames.every(key => typeof value[key] === 'boolean') ? Object.fromEntries(optionNames.map(key => [key,value[key]])) : null; }
function reference(value) {
  if (!closed(value,['requestId','action','sessionId','serial','sources','options']) || !guid(value.requestId) || value.action !== action || typeof value.sessionId !== 'string' || !/^[A-Za-z0-9_-]{1,128}$/.test(value.sessionId) || !serial(value.serial)) return null;
  const selected = sources(value.sources), flags = options(value.options);
  const result = selected && flags ? {requestId:value.requestId,action,sessionId:value.sessionId,serial:value.serial,sources:selected,options:flags} : null;
  return result && byteLength(JSON.stringify(result)) <= 4096 ? result : null;
}
function sameSources(left,right) {
  const checked = sources(left);
  if (!checked || checked.length !== right.length || !checked.every(value => right.some(expected => value.id === expected.id && value.fileName === expected.fileName && value.sizeBytes === expected.sizeBytes && value.sha256 === expected.sha256))) return false;
  // The host preserves admission order in its proof. Core's command snapshot may
  // use base-first OrdinalIgnoreCase order; permitted names are strictly ASCII.
  const base = value => /^(base|base-master)\.apk$/i.test(value.fileName) ? 0 : 1;
  const ordered = [...right].sort((a,b) => base(a)-base(b) || (a.fileName.toUpperCase() < b.fileName.toUpperCase() ? -1 : a.fileName.toUpperCase() > b.fileName.toUpperCase() ? 1 : 0));
  return [right,ordered].some(expected => checked.every((value,index) => value.id === expected[index].id));
}
function receipt(value,original) {
  const stamp = value => {
    const fields = typeof value === 'string' && value.match(/^(\d{4})-(\d{2})-(\d{2})T(\d{2}):(\d{2}):(\d{2})(?:\.(\d{1,7}))?(Z|[+-](\d{2}):(\d{2}))$/);
    if (!fields || !Number.isFinite(Date.parse(value)) || Date.parse(value) <= 0) return null;
    const [,year,month,day,hour,minute,second,fraction = '',offset,offsetHour = '0',offsetMinute = '0'] = fields;
    const date = new Date(Date.UTC(Number(year),Number(month)-1,Number(day)));
    if (date.getUTCFullYear() !== Number(year) || date.getUTCMonth()+1 !== Number(month) || date.getUTCDate() !== Number(day) || Number(hour) > 23 || Number(minute) > 59 || Number(second) > 59 || Number(offsetHour) > 14 || Number(offsetMinute) > 59 || (Number(offsetHour) === 14 && Number(offsetMinute) !== 0)) return null;
    // Date.parse normalizes the offset, but truncates sub-millisecond digits.
    // Parse the whole second and add all seven .NET 100 ns digits exactly.
    const wholeSecond = Date.parse(`${year}-${month}-${day}T${hour}:${minute}:${second}${offset}`);
    return BigInt(wholeSecond)*10000n + BigInt(fraction.padEnd(7,'0'));
  };
  if (!closed(value,['operationId','commandKind','target','desiredState','stage','observedState','headsetReadback','transitions'],['isTerminal']) || typeof value.operationId !== 'string' || !/^pc-[0-9a-f]{32}$/.test(value.operationId) || value.operationId === 'pc-'+'0'.repeat(32) || value.commandKind !== 'installApkBundle' || value.target !== original.serial || value.desiredState !== 'apk-set-install' || value.stage !== 'confirmed' || value.observedState !== 'package-manager-completed-inventory-refreshed' || value.headsetReadback !== true || (Object.hasOwn(value,'isTerminal') && value.isTerminal !== true) || !Array.isArray(value.transitions) || value.transitions.length !== 3) throw Error('invalid receipt');
  const timestamps = value.transitions.map(transition => stamp(transition?.at));
  if (value.transitions.some((transition,index) => !closed(transition,['stage','at','message']) || transition.stage !== ['sent','pending','confirmed'][index] || transition.message !== 'apk-set-'+transition.stage || timestamps[index] === null || (index && timestamps[index] < timestamps[index-1]))) throw Error('invalid transitions');
  return copy(value);
}
function checked(value,original) {
  const empty = ['result','artifactInspection','artifactTransfer','artifactRead','artifactPush','kiosk','kioskDirectConnection','profile','kioskDirectCommand','fleet','kioskDirectTags','kioskDirectStaging','kioskDirectUpload','kioskDirectInstall','packageStop'];
  if (!closed(value,['requestId','action','state','apkSet'],[...empty,'error','mutation']) || value.requestId !== original.requestId || value.action !== action || !empty.every(key => value[key] == null)) throw Error('invalid outcome');
  const proof = value.apkSet;
  if (!closed(proof,['contractVersion','serial','sources','partCount','options','executionStarted','packageManagerCompleted','inventoryRefreshed','sourceCleanup']) || proof.contractVersion !== contractVersion || proof.serial !== original.serial || !sameSources(proof.sources,original.sources) || proof.partCount !== original.sources.length || !options(proof.options) || !optionNames.every(key => proof.options[key] === original.options[key]) || typeof proof.executionStarted !== 'boolean' || ![null,true].includes(proof.packageManagerCompleted) || ![null,true].includes(proof.inventoryRefreshed) || !['notStarted','held','completed','pending'].includes(proof.sourceCleanup)) throw Error('invalid proof');
  const complete = proof.packageManagerCompleted === true && proof.inventoryRefreshed === true;
  const settled = ['completed','pending'].includes(proof.sourceCleanup);
  let mutation = null;
  if (value.state === 'completed') {
    if (value.error != null || !proof.executionStarted || !complete || !settled) throw Error('unconfirmed completion');
    mutation = receipt(value.mutation,original);
  } else if (value.state === 'preparationFailed') {
    if (proof.executionStarted || proof.packageManagerCompleted !== null || proof.inventoryRefreshed !== null || value.mutation != null || !settled || !['apk_set_preparation_failed','apk_set_work_cleanup_pending'].includes(value.error)) throw Error('invalid preparation failure');
  } else if (value.state === 'outcomeUnknown') {
    if (!proof.executionStarted || proof.packageManagerCompleted !== null || proof.inventoryRefreshed !== null || value.mutation != null || !settled || !['apk_set_outcome_unknown','apk_set_binding_mismatch'].includes(value.error)) throw Error('invalid unknown');
  } else if (value.state === 'running') {
    if (proof.packageManagerCompleted !== null || proof.inventoryRefreshed !== null || value.error != null || value.mutation != null || !['notStarted','held'].includes(proof.sourceCleanup) || (proof.executionStarted && proof.sourceCleanup !== 'held')) throw Error('invalid running');
  } else throw Error('invalid state');
  return {requestId:original.requestId,action,state:value.state,apkSet:copy(proof),...(mutation ? {mutation} : {}),...(value.error ? {error:value.error} : {})};
}

// Immutable originals and cleanup debt have separate custody. Getters never update
// the DOM, invoke app callbacks, perform requests or touch browser storage.
export function createApkSetView({api,getContext,getArtifact,setBusy,showResult}) {
  const element = id => document.getElementById('apk-set-'+id), text = (id,value) => {element(id).textContent = value;};
  let original = null, damagedOriginalCustody = false, receipts = [], selected = new Set(), cleanupPending = false;
  let inFlight = false, authStamp = '', scopeStamp = '', sourceStamp = '', capabilityStamp = '', authEpoch = 0, scopeEpoch = 0;
  let message = 'No APK-set install requested.';
  try {
    const saved = sessionStorage.getItem(storageKey);
    if (saved !== null) {
      try { original = byteLength(saved) <= 4096 ? reference(JSON.parse(saved)) : null; } catch { /* preserve invalid stored bytes */ }
      damagedOriginalCustody = !original;
      message = original ? 'Saved original restored. Read its result explicitly; no install was replayed.' : 'Saved original is invalid. Preserve its custody for the owner; mutations stay blocked.';
    }
  } catch { damagedOriginalCustody = true; message = 'Original storage is unavailable. Preserve custody; mutations stay blocked.'; }
  const hasUnresolved = () => Boolean(original || damagedOriginalCustody);
  const getOriginalReference = () => copy(original);
  const live = context => Boolean(context.connected && context.sessionId);
  const selectedSources = () => receipts.filter(value => selected.has(value.id)).map(value => ({...value}));
  const flags = () => Object.fromEntries(optionNames.map((key,index) => [key,element(optionIds[index]).checked]));
  const supported = context => context.apkSetInstall?.supported === true && context.apkSetInstall.maximumParts === 8 && context.apkSetInstall.maximumFileBytes === 1073741824 && context.apkSetInstall.maximumSourceBytes === 2147483648 && context.apkSetInstall.maximumWorkingCopyBytes === 2147483648 && context.apkSetInstall.maximumSourceAndWorkingBytes === 4294967296 && context.apkSetInstall.maximumConcurrentOperations === 1 && typeof context.apkSetInstall.workingCopyCleanupPending === 'boolean';
  function renderSources() {
    const list = element('sources'), focus = list.contains(document.activeElement) ? document.activeElement.id : null; list.replaceChildren();
    for (const value of receipts) {
      const row = document.createElement('li'), label = document.createElement('label'), check = document.createElement('input'), review = document.createElement('span'), name = document.createElement('strong'), size = document.createElement('span'), digest = document.createElement('code');
      label.className = 'check'; check.id = 'apk-set-source-'+value.id; check.type = 'checkbox'; check.checked = selected.has(value.id); check.disabled = Boolean(getContext().busy || inFlight);
      check.setAttribute('aria-label','Select '+value.fileName); name.textContent = value.fileName; size.textContent = value.sizeBytes+' bytes'; digest.textContent = value.sha256;
      review.append(name,size,digest); label.append(check,review); row.append(label);
      const remove = document.createElement('button'); remove.id = 'apk-set-remove-'+value.id; remove.type = 'button'; remove.textContent = 'Remove from selection'; remove.disabled = check.disabled; remove.addEventListener('click',() => {selected.delete(value.id); update();}); row.append(remove);
      check.addEventListener('change',() => {if (check.checked) selected.add(value.id); else selected.delete(value.id); update();}); list.append(row);
    }
    if (focus) document.getElementById(focus)?.focus({preventScroll:true});
  }
  function update() {
    const context = getContext(), auth = JSON.stringify([context.sessionId,Boolean(context.connected),context.authGeneration]);
    if (auth !== authStamp) {authStamp = auth; authEpoch++;}
    const staging = JSON.stringify([context.sessionId,context.stagingGeneration]);
    if (staging !== sourceStamp) {sourceStamp = staging; receipts = []; selected.clear(); element('files').value = '';}
    const capability = JSON.stringify([context.sessionId,context.authGeneration,context.apkSetInstall]);
    if (capability !== capabilityStamp) {capabilityStamp = capability; if (live(context) && typeof context.apkSetInstall?.workingCopyCleanupPending === 'boolean') cleanupPending = context.apkSetInstall.workingCopyCleanupPending;}
    const selectedParts = selectedSources(), scope = JSON.stringify([auth,context.selected?.serial,context.selected?.isReady,selectedParts,flags(),context.stagingGeneration,Boolean(context.externalUnresolved),context.actions?.has?.(action),supported(context)]);
    if (scope !== scopeStamp) {scopeStamp = scope; scopeEpoch++; element('confirm').checked = false;}
    const idle = live(context) && !context.busy && !inFlight, stagingReady = idle && context.artifactStaging?.supported === true;
    const available = idle && supported(context) && context.actions?.has?.(action) && context.selected?.isReady === true && serial(context.selected.serial) && !hasUnresolved() && !context.externalUnresolved && !cleanupPending && Boolean(sources(selectedParts));
    element('files').disabled = !stagingReady;
    element('stage').disabled = !stagingReady || !element('files').files.length;
    element('add-current').disabled = !stagingReady || !source(getArtifact());
    for (const id of optionIds) element(id).disabled = !idle;
    element('confirm').disabled = !available; element('send').disabled = !available || !element('confirm').checked;
    element('original').disabled = !idle || !original || context.sessionId !== original.sessionId;
    element('save').disabled = !original;
    text('target',context.selected?.isReady === true ? context.selected.serial : 'No ready target');
    text('capacity','Current host process: 8 parts maximum; 1 GiB per file; 2 GiB staged sources plus 2 GiB working copies = 4 GiB APK payload. These are process limits, not a crash-wide disk bound.');
    text('capability',!live(context) ? 'Connect to check ordinary APK-set support.' : supported(context) && context.actions?.has?.(action) ? 'Installs the selected parts together with Android Package Manager, then refreshes package inventory. Android validates package, signer and required split compatibility.' : 'Ordinary APK-set installation is unavailable on this host. Use the supported QFM owner route.');
    text('cleanup',cleanupPending ? 'Working-copy cleanup is pending. New APK-set installs stay blocked; this local warning does not acknowledge or undo device effects.' : 'No working-copy cleanup debt reported.');
    text('state',message + (original && live(context) && original.sessionId !== context.sessionId ? ' Host session changed; this host cannot recover the saved original.' : '') + (hasUnresolved() ? ' Device original custody blocks mutations. No automatic retry.' : '') + (context.externalUnresolved ? ' Another device original is unresolved.' : ''));
    element('reference').hidden = !original;
    text('request',original?.requestId || ''); text('original-target',original?.serial || '');
    text('original-sources',original ? original.sources.map(value => `${value.fileName} — ${value.sizeBytes} bytes — ${value.sha256}`).join('\n') : '');
    renderSources();
  }
  function addReceipt(value) {
    const checked = source(value);
    if (!checked) throw Error('invalid staged receipt');
    const existing = receipts.find(value => value.id === checked.id);
    if (existing) {if (JSON.stringify(existing) !== JSON.stringify(checked)) throw Error('changed staged receipt'); selected.add(existing.id); return;}
    const all = [...receipts,checked];
    if (!sources(all,1)) throw Error('invalid source selection');
    receipts = all; selected.add(checked.id);
  }
  async function stage() {
    update(); if (element('stage').disabled) return;
    const files = [...element('files').files];
    if (files.length > 8 || files.some(file => !fileName(file.name) || file.size <= 0 || file.size > 1073741824) || new Set(files.map(file => file.name.toLowerCase())).size !== files.length || files.reduce((sum,file) => sum+file.size,0) > 2147483648) {message = 'Choose 1–8 distinct APK filenames within the advertised byte limits. Names must follow the host ASCII filename policy; names are never changed.'; update(); return;}
    const sentAuth = authEpoch, sentStaging = sourceStamp;
    inFlight = true; setBusy(true); update();
    try {
      for (const file of files) {
        const value = await api('/browser/v1/artifacts',{method:'POST',body:file,headers:{'Content-Type':'application/octet-stream','X-Qfm-File-Name':file.name}});
        update();
        if (sentAuth !== authEpoch || sentStaging !== sourceStamp) {message = 'Staging scope changed. Reconnect and explicitly select current receipts again.'; return;}
        if (!closed(value,['state','scope','artifact','deviceDispatched']) || value.state !== 'staged' || value.scope !== 'localOnly' || value.deviceDispatched !== false || !source(value.artifact) || value.artifact.fileName !== file.name || value.artifact.sizeBytes !== file.size) throw Error('invalid staged reply');
        addReceipt(value.artifact); message = `Staged ${file.name} locally. Review all selected receipts before approval.`;
      }
      element('files').value = '';
    } catch {message = 'Local staging failed or returned an invalid receipt. Successful earlier receipts remain selected. No install was sent; inspect local staging before retrying.';}
    finally {inFlight = false; setBusy(false); update();}
  }
  function acknowledge(value,saved) {
    const safe = checked(value,saved);
    if (safe.apkSet.sourceCleanup === 'pending') cleanupPending = true;
    if (!['completed','preparationFailed'].includes(safe.state)) {message = 'Original install remains running or unknown. Read only its original result explicitly.'; showResult(safe,message); return;}
    try {sessionStorage.removeItem(storageKey); original = null;}
    catch {damagedOriginalCustody = true; message = 'Bound result received, but original custody could not be cleared. Preserve it for the owner.'; showResult(safe,message); return;}
    message = safe.state === 'completed' ? `Package Manager completed and inventory refreshed for ${saved.serial}. This does not prove installed bytes, signer, version, required-split completeness, app readiness or rollback.` : 'Local preparation failed before Core execution could start. No device install was entered.';
    showResult(safe,message);
  }
  async function send() {
    update(); if (element('send').disabled) return;
    const context = getContext(), saved = reference({requestId:crypto.randomUUID(),action,sessionId:context.sessionId,serial:context.selected.serial,sources:selectedSources(),options:flags()});
    if (!saved) return;
    try {sessionStorage.setItem(storageKey,JSON.stringify(saved));}
    catch {damagedOriginalCustody = true; message = 'Install was not sent: original reference storage failed.'; setBusy(false); update(); return;}
    original = saved; element('confirm').checked = false;
    const sentAuth = authEpoch, sentScope = scopeEpoch;
    inFlight = true; message = 'Install requested. Waiting for actual process settlement; elapsed time does not cancel or authorize replay.'; setBusy(true); update();
    try {
      const value = await api('/browser/v1/execute',{method:'POST',body:JSON.stringify({requestId:saved.requestId,action,serial:saved.serial,sources:copy(saved.sources),options:{...saved.options},confirmed:true})});
      update();
      if (sentAuth !== authEpoch || sentScope !== scopeEpoch || !live(getContext())) {message = 'Reply arrived after authentication or review changed. Preserve the original and explicitly read its result.'; return;}
      acknowledge(value,saved);
    } catch {message = 'Install result is unavailable or invalid. Preserve the original; no automatic replay.';}
    finally {inFlight = false; setBusy(false); update();}
  }
  async function readOriginal() {
    update(); if (element('original').disabled) return;
    const saved = copy(original), sentAuth = authEpoch, sentScope = scopeEpoch;
    inFlight = true; setBusy(true); update();
    try {
      const value = await api('/browser/v1/requests/'+saved.requestId); update();
      if (sentAuth !== authEpoch || sentScope !== scopeEpoch || !live(getContext()) || getContext().sessionId !== saved.sessionId) {message = 'Original reply arrived after authentication or review changed. Preserve its custody and explicitly read it again.'; return;}
      acknowledge(value,saved);
    } catch {message = 'Original result is unavailable or invalid. Its outcome stays unknown; do not repeat the install.';}
    finally {inFlight = false; setBusy(false); update();}
  }
  function stagingCleared() {receipts = []; selected.clear(); element('files').value = ''; element('confirm').checked = false; update();}
  element('files').addEventListener('change',update); element('stage').addEventListener('click',stage);
  element('add-current').addEventListener('click',() => {update(); if (element('add-current').disabled) return; try {addReceipt(getArtifact()); message = 'Current staged APK added to this separate selection.';} catch {message = 'Current receipt conflicts with the selected set or its bounds.';} update();});
  for (const id of optionIds) element(id).addEventListener('change',update);
  element('confirm').addEventListener('change',update); element('send').addEventListener('click',send); element('original').addEventListener('click',readOriginal);
  element('save').addEventListener('click',() => {
    if (!original) return;
    const url = URL.createObjectURL(new Blob([JSON.stringify(getOriginalReference(),null,2)],{type:'application/json'})), link = document.createElement('a');
    link.href = url; link.download = 'qfm-apk-set-original-reference.json'; link.click(); setTimeout(() => URL.revokeObjectURL(url),1000);
  });
  update(); return {update,hasUnresolved,getOriginalReference,stagingCleared};
}
