const storageKey = 'qfm-browser-direct-command-v1';
const invokeAction = 'kiosk.direct.invoke', cancelAction = 'kiosk.direct.request-cancel';
const commands = new Set(['status','show-controls','show-apps','check-setup-helper','set-launch-requirement','cancel-pending-launch','launch-normal','launch-kiosk','reload','enable-wifi-adb-after-boot','disable-wifi-adb-after-boot']);
const bootPreferences = new Set(['enable-wifi-adb-after-boot','disable-wifi-adb-after-boot']);
const requirements = new Set(['any','wifi-on','wifi-off']);
const stages = new Set(['sent','pending','confirmed','failed','timedOut','pendingWearerAction','rejected','expired','cancelled','cleanupUnknown']);
const states = new Set(['pending','pending_wearer_action','confirmed','rejected','expired','cancelled','unknown']);
const guid = value => typeof value === 'string' && value.length === 36 && /^[a-f0-9]{8}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{12}$/.test(value) && value !== '00000000-0000-0000-0000-000000000000';
const session = value => typeof value === 'string' && value.length > 0 && value.length <= 128 && !/[^A-Za-z0-9_-]/.test(value);
const serial = value => typeof value === 'string' && value.length > 0 && value.length <= 256 && !/[^A-Za-z0-9._-]/.test(value);
const logical = id => 'kiosk_' + id.replaceAll('-','');
const usb = id => 'usb_' + id.replaceAll('-','');
const valueValid = (command,value) => commands.has(command) && (command === 'set-launch-requirement' ? requirements.has(value) : value === null);
const boolOrNull = value => value === null || typeof value === 'boolean';
const keyValid = value => typeof value === 'string' && value.length > 0 && value.length <= 160 && value.trim() === value && !/[\x00-\x1f\x7f-\x9f]/.test(value);
const packageValid = value => typeof value === 'string' && value.length <= 256 && /^[A-Za-z0-9_]+(?:\.[A-Za-z0-9_]+)+$/.test(value);
const predispatch = new Set(['busy','session_capacity_reached','session_changed','unauthorized','origin_or_host_rejected','action_unsupported','confirmation_required','fields_invalid','request_invalid','content_type_invalid','body_size_invalid','request_body_timeout','request_id_invalid','field_invalid','duplicate_field','direct_unavailable','direct_connection_not_found','direct_connection_expired','direct_owner_stopping','direct_command_unsupported','direct_value_invalid','direct_original_unavailable','direct_original_mismatch']);
function scope(value) {
  return value && session(value.sessionId) && guid(value.connectionId) && guid(value.connectRequestId) && value.usbOperationId === usb(value.connectRequestId) && serial(value.serial) && ['stable','labs'].includes(value.productChannel)
    ? {sessionId:value.sessionId,connectionId:value.connectionId,connectRequestId:value.connectRequestId,usbOperationId:value.usbOperationId,serial:value.serial,productChannel:value.productChannel} : null;
}
function originalReference(value) {
  const base = scope(value);
  return base && guid(value.requestId) && value.action === invokeAction && value.logicalRequestId === logical(value.requestId) && valueValid(value.command,value.value)
    ? {...base,requestId:value.requestId,action:invokeAction,logicalRequestId:value.logicalRequestId,command:value.command,value:value.value} : null;
}
function cancelReference(value) {
  return value && session(value.sessionId) && guid(value.requestId) && guid(value.originalRequestId) && value.requestId !== value.originalRequestId && guid(value.connectionId) && value.action === cancelAction && value.logicalRequestId === logical(value.originalRequestId)
    ? {requestId:value.requestId,sessionId:value.sessionId,action:cancelAction,originalRequestId:value.originalRequestId,connectionId:value.connectionId,logicalRequestId:value.logicalRequestId} : null;
}
function typedResult(value,id,command) {
  if (!value || value.schema !== 'rusty.kiosk.cli_result.v1' || value.requestId !== id || value.command !== command || typeof value.accepted !== 'boolean' || typeof value.completed !== 'boolean' || (bootPreferences.has(command) && typeof value.requestWifiAdbAfterBoot !== 'boolean') ||
      !boolOrNull(value.controlsOpen) || !boolOrNull(value.pendingRequirementLaunch) || (value.selectedKey !== null && !keyValid(value.selectedKey)) ||
      (value.selectedPackage !== null && !packageValid(value.selectedPackage)) || (value.selectedLaunchRequirement !== null && !requirements.has(value.selectedLaunchRequirement)) ||
      !['selectedInstalled','selectedLaunchable','guardArmed','setupHelperInstalled','setupHelperReady'].every(field => typeof value[field] === 'boolean')) throw new Error('typed_result_invalid');
  const row = value.selectedEntry;
  if (row !== null && (!row || !keyValid(row.key) || !requirements.has(row.launchRequirement) || row.key !== value.selectedKey)) throw new Error('typed_result_invalid');
  return {schema:value.schema,requestId:id,command,accepted:value.accepted,completed:value.completed,controlsOpen:value.controlsOpen,selectedKey:value.selectedKey,selectedPackage:value.selectedPackage,
    selectedInstalled:value.selectedInstalled,selectedLaunchable:value.selectedLaunchable,selectedLaunchRequirement:value.selectedLaunchRequirement,pendingRequirementLaunch:value.pendingRequirementLaunch,
    guardArmed:value.guardArmed,setupHelperInstalled:value.setupHelperInstalled,setupHelperReady:value.setupHelperReady,...(bootPreferences.has(command) ? {requestWifiAdbAfterBoot:value.requestWifiAdbAfterBoot} : {}),selectedEntry:row === null ? null : {key:row.key,launchRequirement:row.launchRequirement}};
}
function project(value,ref,requestId,action) {
  const d = value?.kioskDirectCommand, operation = ({[invokeAction]:'invoke','kiosk.direct.request-status':'requestStatus','kiosk.direct.request-result':'requestResult',[cancelAction]:'requestCancel'})[action];
  const originalId = ref.action === cancelAction ? ref.originalRequestId : ref.requestId;
  if (value?.requestId !== requestId || value.action !== action || value.result !== null || value.mutation !== null || !d || d.operation !== operation ||
      d.connectionId !== ref.connectionId || d.originalRequestId !== originalId || d.logicalRequestId !== ref.logicalRequestId || !guid(d.connectRequestId) || d.usbOperationId !== usb(d.connectRequestId) ||
      !serial(d.serial) || !['stable','labs'].includes(d.productChannel) || !valueValid(d.command,d.value) ||
      (ref.action === invokeAction && (d.connectRequestId !== ref.connectRequestId || d.usbOperationId !== ref.usbOperationId || d.serial !== ref.serial || d.productChannel !== ref.productChannel || d.command !== ref.command || d.value !== ref.value)) ||
      !['confirmed','pending','pendingWearerAction','unknown'].includes(d.originalEffect) || typeof d.reconcilesOriginal !== 'boolean' || typeof d.cancellationAcknowledged !== 'boolean') throw new Error('command_binding_invalid');
  if ((operation === 'requestStatus' || operation === 'requestCancel') && d.reconcilesOriginal) throw new Error('command_binding_invalid');
  let receipt = null, lifecycle = null, typed = null;
  if (d.coreReceipt !== null) {
    const r = d.coreReceipt;
    if (!r || typeof r.actionId !== 'string' || !/^action_[a-f0-9]{32}$/.test(r.actionId) || r.action !== (operation === 'requestCancel' ? 'cancelRequest' : operation) || r.requestId !== ref.logicalRequestId || !stages.has(r.stage)) throw new Error('core_receipt_invalid');
    receipt = {actionId:r.actionId,action:r.action,requestId:r.requestId,stage:r.stage};
  }
  if (d.lifecycle !== null) {
    const l = d.lifecycle, time = value => value === null || Number.isSafeInteger(value) && value >= 0;
    if (!l || l.requestId !== ref.logicalRequestId || !states.has(l.operationState) || typeof l.accepted !== 'boolean' || typeof l.completed !== 'boolean' || !time(l.enqueuedAtMs) || !time(l.expiresAtMs)) throw new Error('lifecycle_invalid');
    lifecycle = {requestId:l.requestId,operationState:l.operationState,accepted:l.accepted,completed:l.completed,enqueuedAtMs:l.enqueuedAtMs,expiresAtMs:l.expiresAtMs};
  }
  if (d.typedResult !== null) typed = typedResult(d.typedResult,ref.logicalRequestId,d.command);
  if (typed && lifecycle || typed && !['invoke','requestResult'].includes(operation)) throw new Error('command_binding_invalid');
  return {connectionId:d.connectionId,connectRequestId:d.connectRequestId,usbOperationId:d.usbOperationId,serial:d.serial,productChannel:d.productChannel,operation,originalRequestId:originalId,
    logicalRequestId:ref.logicalRequestId,command:d.command,value:d.value,originalEffect:d.originalEffect,reconcilesOriginal:d.reconcilesOriginal,coreReceipt:receipt,lifecycle,typedResult:typed,cancellationAcknowledged:d.cancellationAcknowledged};
}
function confirms(d) {
  const t = d.typedResult;
  if (!t?.accepted || !t.completed || d.originalEffect !== 'confirmed' || !d.reconcilesOriginal || d.coreReceipt?.stage !== 'confirmed') return false;
  switch (d.command) {
    case 'enable-wifi-adb-after-boot': return t.requestWifiAdbAfterBoot === true;
    case 'disable-wifi-adb-after-boot': return t.requestWifiAdbAfterBoot === false;
    case 'show-controls': return t.controlsOpen === true;
    case 'show-apps': return t.controlsOpen === false;
    case 'set-launch-requirement': return t.selectedLaunchRequirement === d.value && t.selectedEntry?.key === t.selectedKey && t.selectedEntry?.launchRequirement === d.value;
    case 'cancel-pending-launch': return t.pendingRequirementLaunch === false;
    case 'launch-normal': return t.guardArmed === false && t.pendingRequirementLaunch === false;
    case 'launch-kiosk': return t.guardArmed === true && t.pendingRequirementLaunch === false;
    default: return d.command === 'status' || d.command === 'check-setup-helper' || d.command === 'reload';
  }
}

// Only whitelisted references survive reload. Predicates used by root composition
// are pure: neither may call getContext, whose connection getter reads this card.
export function createDirectCommandView({api,getContext,setBusy,showResult}) {
  const element = id => document.getElementById('direct-command-' + id), text = (id,value) => { element(id).textContent = value; };
  let original = null, cancellation = null, damaged = false, storageAvailable = true;
  let writing = false, reading = false, probing = false, cancelProbing = false, authStamp = '', authEpoch = 0, stamp = '', epoch = 0;
  let message = 'No Direct command sent.', facts = null, lifecycle = null, cancelMessage = 'No cancellation requested.';
  const hasUnresolved = () => Boolean(original || cancellation || damaged || !storageAvailable);
  const getCleanupReference = () => {
    if (damaged) return null;
    const ref = originalReference(original) || cancelReference(cancellation);
    return ref ? {sessionId:ref.sessionId,connectionId:ref.connectionId} : null;
  };
  function persist() {
    try {
      if (original || cancellation || damaged) sessionStorage.setItem(storageKey,JSON.stringify({original:originalReference(original),cancellation:cancelReference(cancellation),...(damaged ? {damaged:true} : {})}));
      else sessionStorage.removeItem(storageKey);
      return true;
    } catch { storageAvailable = false; return false; }
  }
  try {
    const saved = sessionStorage.getItem(storageKey);
    if (saved) {
      let frame; try { if (saved.length <= 4096) frame = JSON.parse(saved); } catch { /* blocked below */ }
      original = originalReference(frame?.original); cancellation = cancelReference(frame?.cancellation);
      damaged = !frame || frame.damaged === true || Boolean(frame.original && !original) || Boolean(frame.cancellation && !cancellation) || !original && !cancellation;
      if (original && cancellation && (cancellation.originalRequestId !== original.requestId || cancellation.logicalRequestId !== original.logicalRequestId || cancellation.connectionId !== original.connectionId || cancellation.sessionId !== original.sessionId)) damaged = true;
    }
    persist();
  } catch { storageAvailable = false; }
  const live = context => Boolean(context.connected && session(context.sessionId));
  const supports = (context,action) => context.actions?.has?.(action) === true;
  const connection = context => {
    const ref = scope(context.connection);
    return live(context) && ref?.sessionId === context.sessionId && typeof context.connection?.expiresAtUtc === 'string' && Number.isFinite(Date.parse(context.connection.expiresAtUtc))
      ? {...ref,expiresAtUtc:context.connection.expiresAtUtc} : null;
  };
  const current = (context,ref) => {
    const c = connection(context);
    return c && ref.sessionId === context.sessionId && c.connectionId === ref.connectionId && c.connectRequestId === ref.connectRequestId && c.usbOperationId === ref.usbOperationId && c.serial === ref.serial && c.productChannel === ref.productChannel && Date.parse(c.expiresAtUtc) > Date.now();
  };
  function update() {
    const context = getContext(), c = connection(context), nextAuth = JSON.stringify([context.connected,context.sessionId,context.authGeneration]);
    if (nextAuth !== authStamp) { authStamp = nextAuth; authEpoch++; }
    const next = JSON.stringify([nextAuth,c,element('command').value,element('requirement').value,[...(context.actions || [])].sort(),Boolean(context.externalUnresolved)]);
    if (next !== stamp) {
      if (stamp && !hasUnresolved() && !writing && !reading) { message = 'No typed acknowledgement for this current scope. Earlier receipts apply only to their original request.'; cancelMessage = 'No cancellation requested for this current scope.'; }
      stamp = next; epoch++; element('invoke-confirm').checked = false; element('cancel-confirm').checked = false; facts = null; lifecycle = null;
    }
    const idle = live(context) && !context.busy && !writing && !reading;
    const available = c && Date.parse(c.expiresAtUtc) > Date.now();
    const canInvoke = idle && available && !hasUnresolved() && !context.externalUnresolved && supports(context,invokeAction) && valueValid(element('command').value,element('command').value === 'set-launch-requirement' ? element('requirement').value : null);
    element('command').disabled = !live(context); element('requirement').disabled = !live(context); element('requirement-field').hidden = element('command').value !== 'set-launch-requirement';
    element('invoke-confirm').disabled = !canInvoke; element('invoke').disabled = !canInvoke || !element('invoke-confirm').checked;
    const canRead = idle && original && current(context,original);
    element('request-status').disabled = !canRead || !supports(context,'kiosk.direct.request-status'); element('request-result').disabled = !canRead || !supports(context,'kiosk.direct.request-result');
    const canCancel = canRead && !cancellation && storageAvailable && !damaged && !context.externalUnresolved && supports(context,cancelAction);
    element('cancel-confirm').disabled = !canCancel; element('cancel').disabled = !canCancel || !element('cancel-confirm').checked;
    element('original-status').disabled = !original || !live(context) || original.sessionId !== context.sessionId || probing || damaged;
    element('cancellation-status').disabled = !cancellation || !live(context) || cancellation.sessionId !== context.sessionId || cancelProbing || damaged;
    element('save-reference').disabled = !original && !cancellation;
    const displayed = c || original;
    text('scope',displayed ? `${displayed.serial} · ${displayed.productChannel} · ${displayed.connectionId}` : 'No adopted connection. Use the connection card first.');
    text('boundary',bootPreferences.has(element('command').value) ? 'This changes only the request-after-restart preference. Current Wi-Fi ADB, next-boot success, connectivity and protected approval remain unverified.' : c && !available ? 'Connection expired. No new native request is available; cached original GET and explicit connection cleanup remain separate.' : 'Requests affect Kiosk’s current selection, which another wearer or client can change. Guard acknowledgement does not establish a named app launch, foreground state, or application/OpenXR readiness. No atomic catalog-row launch is offered.');
    text('state',!storageAvailable ? 'Reference storage unavailable; new Invoke and cancel are blocked.' : damaged ? 'Unknown: invalid saved command references. Preserve owner evidence; no automatic recovery.' : message + (hasUnresolved() ? ' Preserve unresolved originals; new mutations stay blocked.' : ''));
    text('facts',facts ? `Observed selected key: ${facts.selectedKey ?? 'unknown'}; package: ${facts.selectedPackage ?? 'unknown'}; controls open: ${facts.controlsOpen ?? 'unknown'}; launch requirement: ${facts.selectedLaunchRequirement ?? 'unknown'}; pending requirement launch: ${facts.pendingRequirementLaunch ?? 'unknown'}; guard armed: ${facts.guardArmed}; helper installed: ${facts.setupHelperInstalled}; helper ready: ${facts.setupHelperReady}. These are observations, not an entry-bound launch promise.` : 'No typed original result observed for this scope.');
    text('lifecycle',lifecycle ? `Original remote record: ${lifecycle.operationState}; accepted ${lifecycle.accepted}; completed ${lifecycle.completed}. Lifecycle facts do not acknowledge typed effects or this cancellation call.` : 'No separate lifecycle facts read.');
    text('cancel-state',cancelMessage + (cancellation ? ' Its own acknowledgement remains unresolved; status of the original cannot clear it.' : ''));
    element('original').hidden = !original; element('cancellation').hidden = !cancellation;
    for (const [id,value] of Object.entries({id:original?.requestId,logical:original?.logicalRequestId,command:original?.command,value:original?.value ?? 'null',target:original?.serial,channel:original?.productChannel,connection:original?.connectionId,session:original?.sessionId})) text('original-' + id,value || 'Unknown');
    for (const [id,value] of Object.entries({id:cancellation?.requestId,original:cancellation?.originalRequestId,logical:cancellation?.logicalRequestId})) text('cancellation-' + id,value || 'Unknown');
  }
  function show(d,requestId,action,summary = message) {
    facts = d.typedResult; lifecycle = d.lifecycle;
    showResult({requestId,action,connectionId:d.connectionId,originalRequestId:d.originalRequestId,logicalRequestId:d.logicalRequestId,command:d.command,value:d.value,
      originalEffect:d.originalEffect,reconcilesOriginal:d.reconcilesOriginal,coreReceipt:d.coreReceipt,lifecycle:d.lifecycle,typedResult:d.typedResult,cancellationAcknowledged:d.cancellationAcknowledged},summary);
  }
  function acknowledgeOriginal(value,ref,requestId = ref.requestId,action = invokeAction) {
    const d = project(value,ref,requestId,action);
    if (value.state === 'completed' && confirms(d) && original?.requestId === ref.requestId) {
      original = null; if (!persist()) { original = ref; return; }
      message = bootPreferences.has(ref.command) ? `Request-after-restart preference observed: ${d.typedResult.requestWifiAdbAfterBoot ? 'on' : 'off'}. Current Wi-Fi ADB, next-boot success, connectivity and protected approval remain unverified.` : ref.command === 'reload' ? 'Kiosk provider reload acknowledged. App foreground and application/OpenXR readiness remain unknown.' : ['launch-normal','launch-kiosk'].includes(ref.command) ? 'Typed current-selection request acknowledged; guard mode observed. Named app launch, foreground and readiness remain unknown.' : 'Exact typed original request acknowledged. No application readiness inferred.';
    } else message = `Original proof remains ${d.originalEffect}; actual Core stage ${d.coreReceipt?.stage ?? 'unavailable'}. Lifecycle, pending requirements and missing evidence do not confirm a typed effect.`;
    show(d,requestId,action);
  }
  function acknowledgeCancel(value,ref) {
    const d = project(value,ref,ref.requestId,cancelAction);
    if (value.state === 'completed' && d.cancellationAcknowledged && !d.reconcilesOriginal && d.coreReceipt?.stage === 'cancelled' && d.lifecycle?.operationState === 'cancelled' && d.lifecycle.completed && cancellation?.requestId === ref.requestId) {
      cancellation = null; if (!persist()) { cancellation = ref; return; }
      cancelMessage = 'Actual cancellation acknowledgement read. The original Invoke remains separate; earlier effects were not rolled back.';
    } else cancelMessage = 'Cancellation acknowledgement unknown or too late. Preserve its separate reference and the original Invoke.';
    show(d,ref.requestId,cancelAction,cancelMessage);
  }
  async function mutate(cancel = false) {
    update(); if (element(cancel ? 'cancel' : 'invoke').disabled) return;
    const context = getContext(), c = connection(context), requestId = crypto.randomUUID();
    const ref = cancel ? cancelReference({requestId,sessionId:context.sessionId,action:cancelAction,originalRequestId:original.requestId,connectionId:original.connectionId,logicalRequestId:original.logicalRequestId})
      : originalReference({...c,requestId,action:invokeAction,logicalRequestId:logical(requestId),command:element('command').value,value:element('command').value === 'set-launch-requirement' ? element('requirement').value : null});
    if (!ref) return;
    if (cancel) cancellation = ref; else original = ref;
    if (!persist()) { if (cancel) cancellation = null; else original = null; update(); setBusy(Boolean(getContext().busy)); return; }
    writing = true; element('invoke-confirm').checked = false; element('cancel-confirm').checked = false; setBusy(true); update();
    const sentAuth = authEpoch, sentEpoch = epoch;
    const body = cancel ? {requestId,action:cancelAction,connectionId:ref.connectionId,originalRequestId:ref.originalRequestId,confirmed:true}
      : {requestId,action:invokeAction,connectionId:ref.connectionId,command:ref.command,value:ref.value,confirmed:true};
    try {
      const value = await api('/browser/v1/execute',{method:'POST',body:JSON.stringify(body)}); update();
      if ((cancel ? cancellation : original)?.requestId !== ref.requestId) return;
      if (sentAuth !== authEpoch || sentEpoch !== epoch || !live(getContext())) { message = 'Reply arrived after authentication, form or connection changed. Read the exact cached original explicitly; no replay.'; return; }
      if (cancel) acknowledgeCancel(value,ref); else acknowledgeOriginal(value,ref);
    } catch (error) {
      update(); if ((cancel ? cancellation : original)?.requestId !== ref.requestId) return;
      if (sentAuth === authEpoch && sentEpoch === epoch && live(getContext()) && error.dispatched === false && predispatch.has(error.message)) {
        if (cancel) cancellation = null; else original = null;
        if (!persist()) { if (cancel) cancellation = ref; else original = ref; }
        message = 'Request rejected before dispatch. Review a separate request explicitly.';
      } else message = 'Unknown: the request may have entered execution. Keep its original ID; never repeat Invoke or cancellation automatically.';
    } finally { writing = false; update(); setBusy(false); }
  }
  async function recover(result = false) {
    update(); if (element(result ? 'request-result' : 'request-status').disabled) return;
    const ref = {...original}, requestId = crypto.randomUUID(), action = result ? 'kiosk.direct.request-result' : 'kiosk.direct.request-status', sentAuth = authEpoch, sentEpoch = epoch;
    reading = true; setBusy(true); update();
    try {
      const value = await api('/browser/v1/execute',{method:'POST',body:JSON.stringify({requestId,action,connectionId:ref.connectionId,originalRequestId:ref.requestId})}); update();
      if (original?.requestId !== ref.requestId || sentAuth !== authEpoch || sentEpoch !== epoch || !live(getContext())) return;
      if (result) acknowledgeOriginal(value,ref,requestId,action);
      else { const d = project(value,ref,requestId,action); message = 'Separate lifecycle facts read. They never acknowledge the original or a cancellation call.'; show(d,requestId,action); }
    } catch { message = 'Original native read unavailable or unbound. Original references unchanged; no retry or replacement connection.'; }
    finally { reading = false; update(); setBusy(false); }
  }
  async function cached(cancel = false) {
    update(); if (element(cancel ? 'cancellation-status' : 'original-status').disabled) return;
    const ref = {...(cancel ? cancellation : original)}, sentAuth = authEpoch;
    if (cancel) cancelProbing = true; else probing = true; update();
    try {
      const value = await api('/browser/v1/requests/' + encodeURIComponent(ref.requestId)); update();
      if ((cancel ? cancellation : original)?.requestId !== ref.requestId || sentAuth !== authEpoch || !live(getContext()) || getContext().sessionId !== ref.sessionId) return;
      if (cancel) acknowledgeCancel(value,ref); else acknowledgeOriginal(value,ref);
    } catch { message = 'Cached original result unavailable or crossed. Missing records prove no absence of effects.'; }
    finally { if (cancel) cancelProbing = false; else probing = false; update(); setBusy(Boolean(getContext().busy)); }
  }
  for (const id of ['command','requirement']) element(id).addEventListener('change',update);
  for (const id of ['invoke-confirm','cancel-confirm']) element(id).addEventListener('change',update);
  element('invoke').addEventListener('click',()=>mutate(false)); element('cancel').addEventListener('click',()=>mutate(true));
  element('request-status').addEventListener('click',()=>recover(false)); element('request-result').addEventListener('click',()=>recover(true));
  element('original-status').addEventListener('click',()=>cached(false)); element('cancellation-status').addEventListener('click',()=>cached(true));
  element('save-reference').addEventListener('click',()=>{const blob = new Blob([JSON.stringify({original:originalReference(original),cancellation:cancelReference(cancellation)},null,2)],{type:'application/json'}), url = URL.createObjectURL(blob), link = document.createElement('a'); link.href = url; link.download = 'qfm-direct-command-reference.json'; link.click(); setTimeout(()=>URL.revokeObjectURL(url),1000);});
  setInterval(update,1000);
  return {update,hasUnresolved,getCleanupReference};
}
