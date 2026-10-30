const storageKey = 'qfm-browser-package-stop-original-v1', action = 'apk.stop';
const contractVersion = 'questionable.file_manager.browser_package_stop.v1';
const guid = value => typeof value === 'string' && /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/.test(value) && value !== '00000000-0000-0000-0000-000000000000';
const serial = value => typeof value === 'string' && value.length > 0 && value.length <= 256 && /^[A-Za-z0-9._:-]+$/.test(value);
const packageName = value => typeof value === 'string' && value.length <= 256 && /^[A-Za-z0-9_]+(?:\.[A-Za-z0-9_]+)+$/.test(value);
const closed = (value, required, optional = []) => value && typeof value === 'object' && !Array.isArray(value) && required.every(key => Object.hasOwn(value,key)) && Object.keys(value).every(key => required.includes(key) || optional.includes(key));
const count = value => Number.isInteger(value) && value >= 0 && value <= 2147483647;
const stamp = value => typeof value === 'string' && /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?(?:Z|[+-]\d{2}:\d{2})$/.test(value) && Number.isFinite(Date.parse(value)) && Date.parse(value) > 0;
function reference(value) {
  if (!closed(value,['requestId','action','sessionId','serial','packageName']) || !guid(value.requestId) || value.action !== action ||
      typeof value.sessionId !== 'string' || !/^[A-Za-z0-9_-]{1,128}$/.test(value.sessionId) || !serial(value.serial) || !packageName(value.packageName)) return null;
  return Object.freeze({requestId:value.requestId,action,sessionId:value.sessionId,serial:value.serial,packageName:value.packageName});
}
function proof(value, original) {
  const keys = ['contractVersion','serial','packageName','packagePresentBeforeDispatch','packagePresentAfterDispatch','processAbsenceVerified','processEvidenceDisposition','processCount','foregroundComponentCount','topResumedComponentCount','isQuiescent'];
  if (!closed(value,keys) || value.contractVersion !== contractVersion || value.serial !== original.serial || value.packageName !== original.packageName ||
      !['packagePresentBeforeDispatch','packagePresentAfterDispatch','isQuiescent'].every(key => typeof value[key] === 'boolean') ||
      ![true,false,null].includes(value.processAbsenceVerified) || !['verified_absent','unverified'].includes(value.processEvidenceDisposition) ||
      !['processCount','foregroundComponentCount','topResumedComponentCount'].every(key => count(value[key])) ||
      value.isQuiescent !== (value.processAbsenceVerified === true && value.processEvidenceDisposition === 'verified_absent' &&
        value.processCount === 0 && value.foregroundComponentCount === 0 && value.topResumedComponentCount === 0)) throw Error('invalid proof');
  return Object.fromEntries(keys.map(key => [key,value[key]]));
}
function receipt(value, original) {
  if (!closed(value,['operationId','commandKind','target','desiredState','stage','observedState','headsetReadback','transitions'],['isTerminal']) ||
      typeof value.operationId !== 'string' || !/^pc-[0-9a-f]{32}$/.test(value.operationId) || value.operationId === 'pc-'+'0'.repeat(32) ||
      value.commandKind !== 'stopPackage' || value.target !== original.serial || value.desiredState !== 'package-stop' ||
      !['pending','confirmed'].includes(value.stage) || value.observedState !== (value.stage === 'confirmed' ? 'package-quiescent' : 'package-stop-pending') ||
      typeof value.headsetReadback !== 'boolean' || (Object.hasOwn(value,'isTerminal') && value.isTerminal !== (value.stage === 'confirmed')) ||
      !Array.isArray(value.transitions) || value.transitions.length < 2 || value.transitions.length > 3) throw Error('invalid receipt');
  const transitions = value.transitions, stages = transitions.map(t => t?.stage);
  if (stages[0] !== 'sent' || stages[1] !== 'pending' || stages.at(-1) !== value.stage ||
      (stages.length === 3 && !['pending','confirmed'].includes(stages[2])) ||
      transitions.some((t,i) => !closed(t,['stage','at','message']) || !stamp(t.at) || t.message !== 'package-stop-'+t.stage || (i && Date.parse(t.at) < Date.parse(transitions[i-1].at)))) throw Error('invalid transitions');
  return {...value,transitions:transitions.map(t => ({stage:t.stage,at:t.at,message:t.message}))};
}
function checked(value, original) {
  const empty = ['result','artifactInspection','artifactTransfer','artifactRead','artifactPush','kiosk','kioskDirectConnection','profile','kioskDirectCommand','fleet','kioskDirectTags','kioskDirectStaging','kioskDirectUpload','kioskDirectInstall'];
  if (!closed(value,['requestId','action','state'],[...empty,'error','mutation','packageStop']) || value.requestId !== original.requestId || value.action !== action ||
      !empty.every(key => value[key] == null) || !['running','pending','outcomeUnknown','failed','completed'].includes(value.state) ||
      (value.error != null && (value.state === 'completed' || !['package_stop_binding_mismatch','package_stop_outcome_unknown'].includes(value.error)))) throw Error('invalid binding');
  const packageStop = value.packageStop == null ? null : proof(value.packageStop,original);
  const mutation = value.mutation == null ? null : receipt(value.mutation,original);
  if (value.state === 'completed' && (!packageStop || !mutation || !packageStop.packagePresentBeforeDispatch || !packageStop.packagePresentAfterDispatch ||
      !packageStop.isQuiescent || packageStop.processEvidenceDisposition !== 'verified_absent' || mutation.stage !== 'confirmed' || !mutation.headsetReadback)) throw Error('unconfirmed stop');
  return {requestId:original.requestId,action,state:value.state,...(packageStop ? {packageStop} : {}),...(mutation ? {mutation} : {})};
}

// This card retains one immutable original, separate from the current form.
// Custody getters never perform storage writes, acknowledgement or rendering.
export function createPackageStopView({api,getContext,setBusy,showResult}) {
  const element = id => document.getElementById('package-stop-'+id), text = (id,value) => {element(id).textContent = value;};
  let original = null, damaged = false, storageAvailable = true, inFlight = false, authStamp = '', scopeStamp = '', authEpoch = 0, scopeEpoch = 0;
  let message = 'No package stop requested.';
  try {
    const saved = sessionStorage.getItem(storageKey);
    if (saved !== null) {
      try {original = saved.length <= 2048 ? reference(JSON.parse(saved)) : null;} catch { /* preserve the exact invalid stored bytes */ }
      damaged = !original;
      message = damaged ? 'Saved package-stop reference is invalid. Preserve its custody for the owner; changes stay blocked.' : 'Saved original restored. Explicitly read its result; no stop was replayed.';
    }
  } catch {storageAvailable = false; message = 'Package-stop reference storage is unavailable; changes stay blocked.';}
  const hasUnresolved = () => Boolean(original || damaged || !storageAvailable);
  const getOriginalReference = () => original ? {...original} : null;
  const connected = context => Boolean(context.connected && context.sessionId);
  function update() {
    const context = getContext(), auth = JSON.stringify([context.sessionId,Boolean(context.connected),context.authGeneration]);
    if (auth !== authStamp) {authStamp = auth; authEpoch++;}
    const scope = JSON.stringify([auth,context.selected?.serial,context.selected?.isReady,context.packageName,Boolean(context.externalUnresolved),context.actions?.has?.(action)]);
    if (scope !== scopeStamp) {scopeStamp = scope; scopeEpoch++; element('confirm').checked = false;}
    const live = connected(context), ready = context.selected?.isReady === true && serial(context.selected.serial);
    const canMutate = live && ready && packageName(context.packageName) && context.actions?.has?.(action) && !context.busy && !inFlight && !context.externalUnresolved && !hasUnresolved();
    element('confirm').disabled = !canMutate;
    element('send').disabled = !canMutate || !element('confirm').checked;
    element('original').disabled = !original || !live || context.sessionId !== original.sessionId || Boolean(context.busy || inFlight);
    element('save').disabled = !original;
    text('target',ready ? context.selected.serial : 'No ready target');
    text('package',packageName(context.packageName) ? context.packageName : 'Enter an exact package name');
    text('capability',!live ? 'Connect to check package-stop support.' : context.actions?.has?.(action) ? 'Stops the reviewed package for the current Android user, then checks process and activity quiescence.' : 'This host does not advertise package stop. Use the typed QFM owner route.');
    element('reference').hidden = !original;
    text('request',original?.requestId || ''); text('original-target',original?.serial || ''); text('original-package',original?.packageName || '');
    text('state',message + (original && live && original.sessionId !== context.sessionId ? ' Host session changed. The new host cannot recover this original; preserve it for the owner.' : '') +
      (hasUnresolved() ? ' New mutations stay blocked. No automatic retry.' : '') + (context.externalUnresolved ? ' Another original is unresolved; package stop is blocked.' : ''));
  }
  function persistOriginal(saved) {
    try {sessionStorage.setItem(storageKey,JSON.stringify(saved)); return true;}
    catch {storageAvailable = false; return false;}
  }
  function acknowledge(value, saved) {
    const safe = checked(value,saved);
    if (safe.state !== 'completed') {message = 'Original package stop remains pending or unknown. Read its original result explicitly.'; showResult(safe,message); return;}
    // Remove durable custody before releasing the in-memory/global blocker.
    try {sessionStorage.removeItem(storageKey); original = null;}
    catch {storageAvailable = false; message = 'Package quiescence reported, but original custody could not be cleared. Preserve it for the owner.'; showResult(safe,message); return;}
    message = `Owner confirmed package quiescence for ${saved.serial} / ${saved.packageName}.`;
    showResult(safe,message);
  }
  async function send() {
    update(); if (element('send').disabled) return;
    const context = getContext(), saved = reference({requestId:crypto.randomUUID(),action,sessionId:context.sessionId,serial:context.selected.serial,packageName:context.packageName});
    if (!saved) return;
    if (!persistOriginal(saved)) {message = 'Package stop was not sent: original reference could not be saved.'; setBusy(false); update(); return;}
    original = saved; element('confirm').checked = false;
    const sentAuth = authEpoch, sentScope = scopeEpoch;
    inFlight = true; message = 'Package stop sent. Waiting for the original owner result; elapsed time does not cancel it.'; setBusy(true); update();
    try {
      const value = await api('/browser/v1/execute',{method:'POST',body:JSON.stringify({requestId:saved.requestId,action,serial:saved.serial,package:saved.packageName,confirmed:true})});
      update();
      if (sentAuth !== authEpoch || sentScope !== scopeEpoch || !connected(getContext())) {message = 'Reply arrived after authentication or review changed. Preserve the original and read its result explicitly.'; return;}
      acknowledge(value,saved);
    } catch {message = 'Package-stop outcome is unknown or its reply is invalid. Preserve the original and read its result explicitly.';}
    finally {inFlight = false; setBusy(false); update();}
  }
  async function readOriginal() {
    update(); if (element('original').disabled) return;
    const saved = original, sentAuth = authEpoch;
    inFlight = true; setBusy(true); update();
    try {
      const value = await api('/browser/v1/requests/'+saved.requestId); update();
      if (sentAuth !== authEpoch || !connected(getContext()) || getContext().sessionId !== saved.sessionId) {message = 'Original reply discarded after authentication changed. Preserve the original reference.'; return;}
      acknowledge(value,saved);
    } catch {message = 'Original result is unavailable or invalid. Its outcome stays unknown; do not repeat the stop.';}
    finally {inFlight = false; setBusy(false); update();}
  }
  element('confirm').addEventListener('change',update); element('send').addEventListener('click',send); element('original').addEventListener('click',readOriginal);
  element('save').addEventListener('click',() => {
    if (!original) return;
    const url = URL.createObjectURL(new Blob([JSON.stringify(getOriginalReference(),null,2)],{type:'application/json'})), link = document.createElement('a');
    link.href = url; link.download = 'qfm-package-stop-original-reference.json'; link.click(); setTimeout(() => URL.revokeObjectURL(url),1000);
  });
  update(); return {update,hasUnresolved,getOriginalReference};
}
