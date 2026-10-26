const storageKey = 'qfm-browser-direct-connection-v1';
const connectAction = 'kiosk.direct.connect-usb', closeAction = 'kiosk.direct.close', statusAction = 'kiosk.direct.status';
const bootstrapSchema = 'questionable.file_manager.kiosk_direct_usb.v1', cleanupSchema = 'questionable.file_manager.kiosk_direct_usb_cleanup.v1';
const guid = value => typeof value === 'string' && value.length === 36 && /^[a-f0-9]{8}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{12}$/.test(value) && value !== '00000000-0000-0000-0000-000000000000';
const usbId = value => 'usb_' + value.replaceAll('-','');
const session = value => typeof value === 'string' && value.length > 0 && value.length <= 128 && /^[A-Za-z0-9_-]+$/.test(value);
const serial = value => typeof value === 'string' && value.length > 0 && value.length <= 256 && /^[A-Za-z0-9._-]+$/.test(value);
const channel = value => value === 'stable' || value === 'labs';
const utc = value => typeof value === 'string' && value.length <= 40 && /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?(?:Z|\+00:00)$/.test(value) && Number.isFinite(Date.parse(value));
const predispatch = new Set(['busy','session_capacity_reached','target_not_ready','session_changed','unauthorized','origin_or_host_rejected','action_unsupported',
  'confirmation_required','fields_invalid','request_invalid','content_type_invalid','body_size_invalid','request_body_timeout','request_id_invalid','field_invalid','duplicate_field',
  'product_channel_invalid','direct_unavailable','direct_connection_exists','direct_connection_not_found','direct_connection_expired','direct_owner_stopping']);
function scope(value) {
  if (!value || !session(value.sessionId) || !guid(value.connectRequestId) || value.usbOperationId !== usbId(value.connectRequestId) || !serial(value.serial) || !channel(value.productChannel)) return null;
  return {sessionId:value.sessionId,connectRequestId:value.connectRequestId,usbOperationId:value.usbOperationId,serial:value.serial,productChannel:value.productChannel};
}
function connectionReference(value) {
  const base = scope(value);
  return base && guid(value.connectionId) && utc(value.expiresAtUtc) ? {...base,connectionId:value.connectionId,expiresAtUtc:value.expiresAtUtc} : null;
}
function originalReference(value) {
  const base = scope(value);
  if (!base || !guid(value.requestId)) return null;
  if (value.action === connectAction && value.connectRequestId === value.requestId) return {...base,requestId:value.requestId,action:connectAction};
  const connection = connectionReference(value);
  return value.action === closeAction && connection ? {...connection,requestId:value.requestId,action:closeAction} : null;
}

// Persist nonsecret ownership references only. Native credentials and all
// observations remain host-owned or ephemeral; reload performs no operation.
export function createDirectConnectionView({api,getContext,setBusy,showResult}) {
  const element = id => document.getElementById('direct-' + id), text = (id,value) => { element(id).textContent = value; };
  let original = null, connection = null, damaged = false, storageAvailable = true;
  let writing = false, reading = false, probing = false, stamp = '', epoch = 0, authStamp = '', authEpoch = 0;
  let connectionMessage = 'No Direct connection acknowledged.', statusMessage = 'No signed status read.', cleanupMessage = 'No cleanup acknowledged.';
  let message = 'No Direct request sent.', disposition = 'Unknown';
  const live = context => Boolean(context.connected && session(context.sessionId));
  const supports = (context,action) => context.actions?.has?.(action) === true;
  const ready = context => context.selected?.isReady === true && context.selected?.isWifiConnection === false && serial(context.selected.serial);
  const lostCustody = context => Boolean((original && original.sessionId !== context.sessionId) || (connection && connection.sessionId !== context.sessionId));
  // Root calls this before update on host changes. Do not cache this decision.
  const hasUnresolved = () => Boolean(original || damaged || !storageAvailable || lostCustody(getContext()));
  const getConnectionReference = () => {
    const context = getContext();
    return live(context) && connection && !original && !damaged && storageAvailable && !lostCustody(context)
      ? {...connectionReference(connection)} : null;
  };
  function persist() {
    try {
      const frame = {original:originalReference(original),connection:connectionReference(connection),...(damaged ? {damaged:true} : {})};
      sessionStorage.setItem(storageKey,JSON.stringify(frame));
      if (!original && !connection && !damaged) sessionStorage.removeItem(storageKey);
      return true;
    } catch { storageAvailable = false; return false; }
  }
  try {
    const saved = sessionStorage.getItem(storageKey);
    if (saved) {
      let frame;
      try { if (saved.length <= 4096) frame = JSON.parse(saved); } catch { /* fail closed below */ }
      original = originalReference(frame?.original); connection = connectionReference(frame?.connection);
      damaged = !frame || frame.damaged === true || Boolean(frame.original && !original) || Boolean(frame.connection && !connection) || (!original && !connection);
      if (original?.action === closeAction && (!connection || original.connectionId !== connection.connectionId || original.connectRequestId !== connection.connectRequestId || original.sessionId !== connection.sessionId || original.serial !== connection.serial || original.productChannel !== connection.productChannel || original.expiresAtUtc !== connection.expiresAtUtc)) damaged = true;
    }
    persist();
  } catch { storageAvailable = false; }
  function contextStamp() {
    const context = getContext();
    return JSON.stringify([context.connected,context.sessionId,context.selected?.serial,context.selected?.isReady,context.selected?.isWifiConnection,element('channel').value,
      [...(context.actions || [])].sort(),Boolean(context.externalUnresolved),context.cleanupConnectionId,connection?.connectionId,connection?.expiresAtUtc]);
  }
  function update() {
    const authContext = getContext(), nextAuth = JSON.stringify([authContext.connected,authContext.sessionId]);
    if (nextAuth !== authStamp) { authStamp = nextAuth; authEpoch++; }
    const next = contextStamp();
    if (next !== stamp) {
      stamp = next; epoch++; element('connect-confirm').checked = false; element('close-confirm').checked = false;
      statusMessage = 'No signed status read for the current scope.'; cleanupMessage = 'No cleanup acknowledged for the current scope.'; disposition = 'Unknown';
      connectionMessage = connection ? 'Saved connection handle; read status explicitly. Saved metadata does not restore credentials.' : 'No Direct connection acknowledged.';
    }
    const context = getContext(), authenticated = live(context), lost = lostCustody(context), blocked = hasUnresolved();
    const idle = authenticated && !context.busy && !writing && !reading && !probing;
    const canConnect = idle && !blocked && !context.externalUnresolved && !connection && ready(context) && supports(context,connectAction);
    const canClose = idle && !blocked && connection && !lost && (!context.externalUnresolved || context.cleanupConnectionId === connection.connectionId) && supports(context,closeAction);
    element('channel').disabled = !authenticated || writing || reading;
    element('connect-confirm').disabled = !canConnect; element('connect').disabled = !canConnect || !element('connect-confirm').checked;
    element('close-confirm').disabled = !canClose; element('close').disabled = !canClose || !element('close-confirm').checked;
    element('status').disabled = !idle || !connection || lost || supports(context,statusAction) === false || Date.parse(connection.expiresAtUtc) <= Date.now();
    element('original-status').disabled = !authenticated || !original || original.sessionId !== context.sessionId || probing || writing || damaged;
    element('original-save').disabled = !original && !connection;
    text('target',context.selected?.serial || 'No ready USB target');
    text('product',element('channel').value === 'labs' ? 'io.github.mesmerprism.rustykiosk.labs' : 'io.github.mesmerprism.rustykiosk');
    text('capability',!authenticated ? 'Connect to check this host’s Direct actions.' : supports(context,connectAction) ? 'Direct uses an exact ready classic USB target. Signed status and close use the saved connection target.' : 'This host does not offer Direct USB connections.');
    const displayed = connection || original;
    text('connection-target',displayed?.serial || 'Unknown'); text('connection-channel',displayed?.productChannel || 'Unknown');
    text('connection-id',connection?.connectionId || 'Unknown'); text('connect-id',displayed?.connectRequestId || 'Unknown'); text('usb-id',displayed?.usbOperationId || 'Unknown');
    text('expires',connection?.expiresAtUtc || 'Unknown'); text('disposition',disposition);
    text('connection-state',lost ? 'Unknown: host session changed; native custody and credentials cannot be restored from this reference.' : connection && Date.parse(connection.expiresAtUtc) <= Date.now() ? 'Connection expired by local clock. Explicit close remains available for cleanup; no request was sent by this timer.' : connectionMessage);
    text('status-state',statusMessage); text('cleanup-state',cleanupMessage);
    text('state',!storageAvailable ? 'Reference storage unavailable. New connection actions are blocked.' : damaged ? 'Unknown: saved reference is invalid. Preserve the owner reference; automatic recovery is unavailable.' : original ? 'Unknown original connection action. Read its original browser result explicitly; no retry is sent.' : lost ? 'Unknown: saved connection belongs to a different host session. Preserve the reference for the owner route.' : message);
    element('original').hidden = !original;
    for (const [id,value] of Object.entries({id:original?.requestId,action:original?.action,session:original?.sessionId,target:original?.serial,channel:original?.productChannel,usb:original?.usbOperationId})) text('original-' + id,value || 'Unknown');
  }
  function bound(value,ref,requestId,action) {
    const dto = value?.kioskDirectConnection;
    return value?.requestId === requestId && value?.action === action && value.result === null && value.mutation === null && dto &&
      dto.connectRequestId === ref.connectRequestId && dto.usbOperationId === ref.usbOperationId && dto.serial === ref.serial && dto.productChannel === ref.productChannel &&
      (!ref.connectionId || dto.connectionId === ref.connectionId) && (!ref.expiresAtUtc || dto.expiresAtUtc === ref.expiresAtUtc) ? dto : null;
  }
  function acknowledge(value,ref) {
    const dto = bound(value,ref,ref.requestId,ref.action);
    if (!dto || value.state !== 'completed') return false;
    if (ref.action === connectAction) {
      if (dto.state !== 'connected' || !guid(dto.connectionId) || !utc(dto.expiresAtUtc) || typeof dto.enabledByRequest !== 'boolean' ||
          dto.bootstrap?.schema !== bootstrapSchema || dto.bootstrap.operationId !== ref.usbOperationId || dto.bootstrap.stage !== 'confirmed' || dto.adoption?.stage !== 'confirmed' ||
          !['signedStatusRead','typedKioskStatusRead','stagingInventoryRead'].every(key => dto.adoption[key] === true)) return false;
      const previous = connection; connection = connectionReference({...ref,...dto,sessionId:ref.sessionId}); original = null;
      if (!persist()) { original = ref; connection = previous; return false; }
      update(); connectionMessage = 'Authenticated operator transport adopted. App foreground and application readiness remain unknown.';
      message = 'Original connection acknowledged. Signed status and close remain separate explicit actions.';
    } else {
      const cleanup = dto.cleanup;
      if (dto.state !== 'closed' || typeof dto.enabledByRequest !== 'boolean' || cleanup?.schema !== cleanupSchema || cleanup.operationId !== ref.usbOperationId || cleanup.stage !== 'confirmed' ||
          cleanup.listenerDisposition !== (dto.enabledByRequest ? 'owned-stopped' : 'preexisting-preserved')) return false;
      const previous = connection; connection = null; original = null;
      if (!persist()) { connection = previous; original = ref; return false; }
      update(); disposition = cleanup.listenerDisposition;
      cleanupMessage = dto.enabledByRequest ? 'Close confirmed: this request’s owned listener stopped and its connection key was erased.' : 'Close confirmed: pre-existing listener preserved; this connection key was erased.';
      connectionMessage = 'Saved connection closed by its actual cleanup receipt.'; message = 'Original close acknowledged.';
    }
    showResult({requestId:ref.requestId,action:ref.action,state:'completed',connectionId:dto.connectionId,serial:ref.serial,productChannel:ref.productChannel},message);
    return true;
  }
  async function mutate(action) {
    update(); const context = getContext();
    if ((action === connectAction ? element('connect') : element('close')).disabled) return;
    const requestId = crypto.randomUUID();
    const ref = action === connectAction ? originalReference({requestId,action,sessionId:context.sessionId,connectRequestId:requestId,usbOperationId:usbId(requestId),serial:context.selected.serial,productChannel:element('channel').value}) : originalReference({...connection,requestId,action});
    if (!ref) return;
    original = ref;
    if (!persist()) { original = null; update(); setBusy(Boolean(getContext().busy)); return; }
    writing = true; const started = epoch; setBusy(true); update();
    const body = action === connectAction ? {requestId,action,serial:ref.serial,productChannel:ref.productChannel,confirmed:true} : {requestId,action,connectionId:ref.connectionId,confirmed:true};
    try {
      const value = await api('/browser/v1/execute',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify(body)});
      update();
      if (epoch === started && getContext().sessionId === ref.sessionId && getContext().connected && !acknowledge(value,ref)) message = 'Unknown: connection acknowledgement was incomplete or did not match the saved reference.';
    } catch (error) {
      update();
      if (epoch === started && getContext().sessionId === ref.sessionId && getContext().connected && error.dispatched === false && predispatch.has(error.message)) {
        original = null; if (!persist()) original = ref;
        message = 'Request rejected before dispatch (' + error.message + '). No connection action acknowledged.';
      } else message = 'Unknown: the reply was lost or the operation may have entered execution. Preserve the original reference.';
    } finally { writing = false; update(); setBusy(false); }
  }
  async function readStatus() {
    update(); if (element('status').disabled) return;
    const ref = {...connection}, requestId = crypto.randomUUID(), started = epoch;
    reading = true; setBusy(true); update();
    try {
      const value = await api('/browser/v1/execute',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({requestId,action:statusAction,connectionId:ref.connectionId})});
      update(); const dto = bound(value,ref,requestId,statusAction);
      if (epoch !== started) return;
      if (value.state !== 'completed' || !dto || dto.state !== 'connected' || typeof dto.status?.installerAllowed !== 'boolean' || !utc(dto.status.observedAtUtc)) { statusMessage = 'Signed status was not acknowledged for this saved connection. Original outcome unchanged.'; return; }
      statusMessage = 'Signed status read at ' + dto.status.observedAtUtc + '. Installer allowed: ' + (dto.status.installerAllowed ? 'yes' : 'no') + '. Original outcome unchanged; no bootstrap or readiness claim.';
      showResult({requestId,action:statusAction,connectionId:ref.connectionId,serial:ref.serial,productChannel:ref.productChannel,installerAllowed:dto.status.installerAllowed},statusMessage);
    } catch { if (epoch === started) statusMessage = 'Signed status unavailable. No bootstrap or retry sent; original outcome unchanged.'; }
    finally { reading = false; update(); setBusy(false); }
  }
  async function readOriginal() {
    update(); if (element('original-status').disabled) return;
    const ref = {...original}, startedAuth = authEpoch; probing = true; update();
    try {
      const value = await api('/browser/v1/requests/' + encodeURIComponent(ref.requestId));
      update();
      // Metadata reads intentionally ignore current selection/channel. Only the
      // original host-session and still-current reference can acknowledge it.
      if (authEpoch === startedAuth && getContext().connected && getContext().sessionId === ref.sessionId && original?.requestId === ref.requestId && !acknowledge(value,ref)) message = 'Unknown: original result is pending, missing or does not match the saved reference.';
    } catch { message = 'Unknown: original browser result unavailable. A missing result does not prove no effect.'; }
    finally { probing = false; update(); setBusy(Boolean(getContext().busy)); }
  }
  element('channel').addEventListener('change',update);
  element('connect-confirm').addEventListener('change',update); element('close-confirm').addEventListener('change',update);
  element('connect').addEventListener('click',() => mutate(connectAction)); element('close').addEventListener('click',() => mutate(closeAction));
  element('status').addEventListener('click',readStatus); element('original-status').addEventListener('click',readOriginal);
  element('original-save').addEventListener('click',() => {
    const blob = new Blob([JSON.stringify({original:originalReference(original),connection:connectionReference(connection)},null,2)],{type:'application/json'});
    const url = URL.createObjectURL(blob), link = document.createElement('a'); link.href = url; link.download = 'qfm-direct-reference.json'; link.click(); setTimeout(() => URL.revokeObjectURL(url),1000);
  });
  // Display-only clock: expiry never dispatches status, close or a replacement.
  setInterval(update,1000);
  return {update,hasUnresolved,getConnectionReference};
}
