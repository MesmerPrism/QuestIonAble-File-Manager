const storageKey = 'qfm-browser-profile-v1', prefix = 'questionable.file_manager.quest_connectivity_profile_';
const deviceId = value => typeof value === 'string' && value.length > 0 && value.length <= 256 && !/[^a-z0-9._-]/.test(value);
const guid = value => typeof value === 'string' && value.length === 36 && /^[a-f0-9]{8}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{12}$/.test(value) && value !== '00000000-0000-0000-0000-000000000000';
const session = value => typeof value === 'string' && value.length > 0 && value.length <= 128 && !/[^A-Za-z0-9_-]/.test(value);
const predispatch = new Set(['busy','session_capacity_reached','session_changed','unauthorized','origin_or_host_rejected','action_unsupported','confirmation_required','fields_invalid','request_invalid','content_type_invalid','body_size_invalid','request_body_timeout','request_id_invalid','field_invalid','duplicate_field','profile_header_invalid','profile_replace_flag_invalid','profile_device_id_mismatch','content_encoding_invalid','sensitive_endpoint_required','windows_credential_manager_required','profileInputSizeInvalid','profileSchemaInvalid','profileDeviceIdInvalid','profileTargetInvalid','profilePairingCodeInvalid','profileDocumentInvalid']);
function reference(value) {
  if (!value || !guid(value.requestId) || !session(value.sessionId) || !deviceId(value.deviceId)) return null;
  const base = {requestId:value.requestId,sessionId:value.sessionId,action:value.action,deviceId:value.deviceId};
  return value.action === 'profile.revoke' ? base : value.action === 'profile.import' && typeof value.replaceExisting === 'boolean' ? {...base,replaceExisting:value.replaceExisting} : null;
}
const statePair = value => value && deviceId(value.deviceId) && ({enrolled:'profileEnrolled',absent:'profileAbsent',invalid:'profileInvalid'})[value.state] === value.reasonCode;
function bound(value,ref) {
  const p = value?.profile, operation = ref.action.slice(8);
  return value?.requestId === ref.requestId && value.action === ref.action && value.result === null && value.mutation === null && p && p.operation === operation &&
    p.deviceId === (ref.deviceId ?? null) && p.replaceExisting === (operation === 'import' ? ref.replaceExisting : null) && typeof p.truncated === 'boolean' ? p : null;
}
function terminal(value,ref) {
  const p = bound(value,ref); if (!p || p.list !== null || p.status !== null || p.totalProfiles !== null || p.truncated !== false) return null;
  if (value.state === 'completed' && p.effect === 'confirmed' && p.failure === null) {
    const r = p.receipt;
    if (!r || r.schema !== prefix + 'mutation.v1' || r.status !== 'confirmed' || r.deviceId !== ref.deviceId) return null;
    if (ref.action === 'profile.import' && r.state === 'enrolled' && ((r.action === 'created' && r.reasonCode === 'profileCreated') || (ref.replaceExisting && r.action === 'replaced' && r.reasonCode === 'profileReplaced'))) return {effect:'confirmed',receipt:{schema:r.schema,status:r.status,action:r.action,deviceId:r.deviceId,state:r.state,reasonCode:r.reasonCode}};
    if (ref.action === 'profile.revoke' && r.action === 'revoked' && r.state === 'absent' && ['profileRevoked','profileAlreadyAbsent'].includes(r.reasonCode)) return {effect:'confirmed',receipt:{schema:r.schema,status:r.status,action:r.action,deviceId:r.deviceId,state:r.state,reasonCode:r.reasonCode}};
  }
  if (ref.action === 'profile.import' && value.state === 'failed' && p.receipt === null) {
    const f = p.failure;
    if (p.effect === 'notPerformed' && f?.code === 'profileReplaceConfirmationRequired' && f.status === 'rejected' && f.rollbackState === 'not_applicable') return {effect:'notPerformed'};
    if (p.effect === 'rolledBack' && f?.code === 'profileWriteVerificationFailedRolledBack' && f.status === 'failed' && f.rollbackState === 'confirmed') return {effect:'rolledBack'};
  }
  return null;
}

// Only a nonsecret original reference survives reload. Private File/DOM inputs,
// document buffers and returned store facts never enter browser persistence.
export function createProfileView({api,getContext,setBusy,showResult}) {
  const element = id => document.getElementById('profile-' + id), text = (id,value) => { element(id).textContent = value; };
  let original = null, damaged = false, storageAvailable = true, reading = false, writing = false, probing = false;
  let authStamp = '', capabilityStamp = '', authEpoch = 0, revision = 0, rows = [], statusFact = null;
  let message = 'No profile request sent.', listMessage = 'No profile list requested.', statusMessage = 'No stored status requested.';
  const live = context => Boolean(context.connected && session(context.sessionId));
  const supported = context => context.capability?.supported === true && context.capability.maximumPrivateInputBytes === 4096 && context.capability.maximumListEntries === 128;
  const supports = (context,action) => context.actions?.has?.(action) === true;
  const hasUnresolved = () => Boolean(original || damaged || !storageAvailable);
  function persist() {
    try { sessionStorage.setItem(storageKey,JSON.stringify(original ? reference(original) : damaged ? {} : null)); if (!original && !damaged) sessionStorage.removeItem(storageKey); return true; }
    catch { storageAvailable = false; return false; }
  }
  try {
    const saved = sessionStorage.getItem(storageKey);
    if (saved) { try { original = saved.length <= 2048 ? reference(JSON.parse(saved)) : null; } catch { /* sanitized blocked marker below */ } damaged = !original; }
    persist();
  } catch { storageAvailable = false; }
  function clearApprovals() { for (const id of ['create-confirm','replace-confirm','revoke-confirm']) element(id).checked = false; }
  function clearPrivate() {
    for (const id of ['usb','endpoint','code','file']) element(id).value = '';
    clearApprovals(); revision++;
  }
  function renderRows() {
    const list = element('ids'); list.replaceChildren();
    for (const row of rows) {
      const li = document.createElement('li'), button = document.createElement('button'); button.type = 'button'; button.textContent = row.deviceId + ' — ' + row.state;
      button.disabled = !live(getContext()) || getContext().busy;
      button.addEventListener('click',() => { element('id').value = row.deviceId; clearPrivate(); statusFact = null; statusMessage = 'No stored status requested for this ID.'; update(); });
      li.append(button); list.append(li);
    }
  }
  function update() {
    const context = getContext(), nextAuth = JSON.stringify([context.connected,context.sessionId,context.authGeneration]);
    const nextCapability = JSON.stringify([supported(context),[...(context.actions || [])].sort()]);
    if (nextAuth !== authStamp || nextCapability !== capabilityStamp) {
      if (nextAuth !== authStamp) authEpoch++;
      authStamp = nextAuth; capabilityStamp = nextCapability; clearPrivate(); rows = []; statusFact = null;
      listMessage = 'No profile list requested for this authentication scope.'; statusMessage = 'No stored status requested for this authentication scope.';
    }
    const available = live(context) && supported(context), idle = available && !context.busy && !reading && !writing;
    const reviewedId = deviceId(element('id').value), fileMode = element('mode').value === 'file';
    const privateReady = reviewedId && (fileMode ? element('file').files?.length === 1 : Boolean(element('usb').value && element('endpoint').value && element('code').value));
    const mutatable = idle && !hasUnresolved() && !context.externalUnresolved;
    for (const id of ['id','mode']) element(id).disabled = !available;
    for (const id of ['usb','endpoint','code']) element(id).disabled = !available || fileMode || writing;
    element('file').disabled = !available || !fileMode || writing;
    element('manual').hidden = fileMode; element('file-source').hidden = !fileMode;
    element('list').disabled = !idle || !supports(context,'profile.list'); element('status').disabled = !idle || !reviewedId || !supports(context,'profile.status');
    for (const kind of ['create','replace']) {
      const allowed = mutatable && privateReady && supports(context,'profile.import');
      element(kind + '-confirm').disabled = !allowed; element(kind).disabled = !allowed || !element(kind + '-confirm').checked;
    }
    const canRevoke = mutatable && reviewedId && supports(context,'profile.revoke');
    element('revoke-confirm').disabled = !canRevoke; element('revoke').disabled = !canRevoke || !element('revoke-confirm').checked;
    element('clear').disabled = !live(context);
    element('original-status').disabled = !live(context) || !original || original.sessionId !== context.sessionId || probing || damaged;
    element('original-save').disabled = !original;
    text('capability',available ? 'Current-user Windows profile management is available. Store health has not been tested; no headset is required.' : 'Use the Windows QFM companion or its connectivity-profile CLI. This host does not advertise supported profile management.');
    text('list-state',listMessage); text('status-state',statusFact && statusFact.deviceId === element('id').value ? 'Stored ' + statusFact.deviceId + ': ' + statusFact.state + '. This is a local record fact; no connection was tested.' : statusMessage);
    text('state',!storageAvailable ? 'Reference storage unavailable. New profile mutations are blocked.' : damaged ? 'Unknown: saved profile reference is invalid. Preserve the owner reference for recovery.' : original ? 'Unknown original profile mutation. Fresh stored status cannot acknowledge this write; read the original browser result explicitly.' : message);
    element('original').hidden = !original;
    for (const [id,value] of Object.entries({id:original?.requestId,session:original?.sessionId,action:original?.action,device:original?.deviceId,replace:original?.action === 'profile.import' ? String(original.replaceExisting) : 'Not applicable'})) text('original-' + id,value || 'Unknown');
    renderRows();
  }
  function acknowledge(value,ref) {
    const proof = terminal(value,ref); if (!proof || original?.requestId !== ref.requestId) return false;
    original = null; if (!persist()) { original = ref; return false; }
    if (proof.effect === 'notPerformed') message = 'Profile not written: this ID already exists. Re-enter private material and approve a separate replacement explicitly if intended.';
    else if (proof.effect === 'rolledBack') message = 'Write verification failed; rollback confirmed. The new profile was not saved.';
    else message = ref.action === 'profile.revoke' ? 'Revocation confirmed for the exact local profile ID. No remote cleanup performed.' : 'Profile write confirmed for the exact local ID. Connectivity and readiness remain unknown.';
    showResult({requestId:ref.requestId,action:ref.action,deviceId:ref.deviceId,effect:proof.effect,...(proof.receipt ? {receipt:proof.receipt} : {})},message);
    return true;
  }
  async function mutate(action,replaceExisting = false) {
    update(); const button = element(action === 'profile.revoke' ? 'revoke' : replaceExisting ? 'replace' : 'create'); if (button.disabled) return;
    const context = getContext(), ref = reference({requestId:crypto.randomUUID(),sessionId:context.sessionId,action,deviceId:element('id').value,...(action === 'profile.import' ? {replaceExisting} : {})});
    const startedAuth = authEpoch, startedRevision = revision; let bytes = null, dispatched = false;
    writing = true; setBusy(true); update();
    try {
      if (action === 'profile.import') {
        if (element('mode').value === 'file') {
          let file = element('file').files?.[0];
          if (!file || !Number.isSafeInteger(file.size) || file.size < 1 || file.size > 4096) throw new Error('local_private_input_invalid');
          const expectedSize = file.size; bytes = new Uint8Array(await file.arrayBuffer()); file = null;
          if (bytes.byteLength !== expectedSize) throw new Error('local_private_input_invalid');
        } else {
          let document = JSON.stringify({schema:prefix + 'enrollment.v1',target:'QuestIonAbleFileManager/QuestConnectivity/' + ref.deviceId,device_id:ref.deviceId,usb_serial:element('usb').value,endpoint:element('endpoint').value,pairing_code:element('code').value});
          bytes = new TextEncoder().encode(document); document = null;
        }
        if (bytes.byteLength < 1 || bytes.byteLength > 4096) throw new Error('local_private_input_invalid');
      }
      update(); if (authEpoch !== startedAuth || revision !== startedRevision || !live(getContext())) return;
      original = ref; if (!persist()) { original = null; return; } update();
      dispatched = true;
      const value = action === 'profile.import'
        ? await api('/browser/v1/profiles/import',{method:'POST',headers:{'Content-Type':'application/json','X-Qfm-Request-Id':ref.requestId,'X-Qfm-Profile-Device-Id':ref.deviceId,'X-Qfm-Confirm-Profile-Write':'true','X-Qfm-Replace-Existing':String(replaceExisting)},body:bytes})
        : await api('/browser/v1/execute',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({requestId:ref.requestId,action,deviceId:ref.deviceId,confirmed:true})});
      update();
      if (original?.requestId === ref.requestId && authEpoch === startedAuth && revision === startedRevision && live(getContext()) && !acknowledge(value,ref)) message = 'Unknown: profile mutation acknowledgement was incomplete or did not match the original.';
    } catch (error) {
      update();
      if (!dispatched) message = 'Private enrollment input could not be prepared. Inputs cleared; no profile request sent.';
      else if (original?.requestId === ref.requestId && authEpoch === startedAuth && revision === startedRevision && live(getContext()) && error.dispatched === false && predispatch.has(error.message)) {
        original = null; if (!persist()) original = ref;
        message = 'Profile request rejected before dispatch (' + error.message + '). Private inputs cleared.';
      } else if (original?.requestId === ref.requestId) message = 'Unknown: profile reply lost or write may have entered execution. Preserve the nonsecret original reference.';
    } finally {
      if (bytes) bytes.fill(0); bytes = null;
      clearPrivate(); writing = false; update(); setBusy(false);
    }
  }
  async function read(action) {
    update(); if (element(action === 'profile.list' ? 'list' : 'status').disabled) return;
    const ref = {requestId:crypto.randomUUID(),action,...(action === 'profile.status' ? {deviceId:element('id').value} : {})};
    const startedAuth = authEpoch, startedRevision = revision; reading = true; setBusy(true); update();
    try {
      const value = await api('/browser/v1/execute',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify(ref)}); update();
      if (authEpoch !== startedAuth || revision !== startedRevision) return;
      const p = bound(value,ref);
      if (!p || value.state !== 'completed' || p.effect !== 'readOnly' || p.receipt !== null || p.failure !== null) throw new Error('profile_read_invalid');
      if (action === 'profile.list') {
        if (p.status !== null || p.list?.schema !== prefix + 'list.v1' || p.list.status !== 'ok' || !Array.isArray(p.list.profiles) || p.list.profiles.length > 128 || !p.list.profiles.every(statePair) ||
            !Number.isSafeInteger(p.totalProfiles) || p.totalProfiles < p.list.profiles.length || p.truncated !== (p.totalProfiles > p.list.profiles.length) || new Set(p.list.profiles.map(row => row.deviceId)).size !== p.list.profiles.length) throw new Error('profile_read_invalid');
        rows = p.list.profiles.map(row => ({deviceId:row.deviceId,state:row.state,reasonCode:row.reasonCode}));
        listMessage = rows.length + ' returned IDs of ' + p.totalProfiles + (p.truncated ? '; list truncated.' : '.');
        showResult({requestId:ref.requestId,action,effect:'readOnly',profiles:rows,totalProfiles:p.totalProfiles,truncated:p.truncated},listMessage);
      } else {
        if (p.list !== null || p.totalProfiles !== null || p.truncated || p.status?.schema !== prefix + 'status.v1' || p.status.status !== 'ok' || p.status.deviceId !== ref.deviceId || !statePair(p.status)) throw new Error('profile_read_invalid');
        statusFact = {deviceId:p.status.deviceId,state:p.status.state,reasonCode:p.status.reasonCode};
        showResult({requestId:ref.requestId,action,effect:'readOnly',status:statusFact},'Stored local profile status read. Original mutation unchanged.');
      }
    } catch {
      if (action === 'profile.list') { rows = []; listMessage = 'Profile list unavailable. No empty-list or store-health conclusion.'; }
      else { statusFact = null; statusMessage = 'Stored status unavailable. Original mutation unchanged.'; }
    } finally { clearPrivate(); reading = false; update(); setBusy(false); }
  }
  async function readOriginal() {
    update(); if (element('original-status').disabled) return;
    const ref = {...original}, startedAuth = authEpoch; probing = true; update();
    try {
      const value = await api('/browser/v1/requests/' + encodeURIComponent(ref.requestId)); update();
      if (authEpoch === startedAuth && live(getContext()) && getContext().sessionId === ref.sessionId && original?.requestId === ref.requestId && !acknowledge(value,ref)) message = 'Unknown: original browser result missing, pending or unbound. Fresh status cannot reconcile it.';
    } catch { message = 'Unknown: original result unavailable. A missing receipt does not prove no write.'; }
    finally { probing = false; clearPrivate(); update(); setBusy(Boolean(getContext().busy)); }
  }
  for (const id of ['id','mode','usb','endpoint','code','file']) element(id).addEventListener(id === 'mode' || id === 'file' ? 'change' : 'input',() => {
    revision++; clearApprovals();
    if (id === 'mode') clearPrivate();
    if (id === 'id') { statusFact = null; statusMessage = 'No stored status requested for this ID.'; }
    update();
  });
  for (const id of ['create-confirm','replace-confirm','revoke-confirm']) element(id).addEventListener('change',update);
  element('list').addEventListener('click',() => read('profile.list')); element('status').addEventListener('click',() => read('profile.status'));
  element('create').addEventListener('click',() => mutate('profile.import',false)); element('replace').addEventListener('click',() => mutate('profile.import',true)); element('revoke').addEventListener('click',() => mutate('profile.revoke'));
  element('clear').addEventListener('click',() => { clearPrivate(); message = 'Private inputs cleared locally. An admitted or unknown operation is unchanged.'; update(); });
  element('original-status').addEventListener('click',readOriginal);
  element('original-save').addEventListener('click',() => { if (!original) return; const url = URL.createObjectURL(new Blob([JSON.stringify(reference(original),null,2)],{type:'application/json'})), link = document.createElement('a'); link.href = url; link.download = 'qfm-profile-reference.json'; link.click(); setTimeout(() => URL.revokeObjectURL(url),1000); });
  const privateBoundary = () => { clearPrivate(); update(); };
  window.addEventListener('blur',privateBoundary); window.addEventListener('pagehide',privateBoundary);
  document.addEventListener('visibilitychange',privateBoundary);
  return {update,hasUnresolved};
}
