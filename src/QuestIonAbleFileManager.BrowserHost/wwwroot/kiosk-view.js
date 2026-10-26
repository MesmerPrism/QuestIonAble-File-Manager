const storageKey = 'qfm-browser-kiosk-v1';
const actions = new Set(['kiosk.catalog.open','kiosk.select']);
const guid = value => typeof value === 'string' && value.length === 36 && /^[a-f0-9]{8}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{12}$/.test(value) && value !== '00000000-0000-0000-0000-000000000000';
const providerId = value => typeof value === 'string' && value.length === 35 && /^pc-[a-f0-9]{32}$/.test(value) && value !== 'pc-' + '0'.repeat(32);
const deriveProviderId = value => 'pc-' + value.replaceAll('-','');
const channel = value => value === 'stable' || value === 'labs';
const serial = value => typeof value === 'string' && value.length > 0 && value.length <= 256 && /^[A-Za-z0-9._:-]+$/.test(value);
const bounded = (value, maximum = 256) => typeof value === 'string' && value.length <= maximum;
const nullable = value => value === null || bounded(value);
const rowKey = value => bounded(value,160) && value.length > 0 && value.trim() === value && !/[\x00-\x1f\x7f-\x9f]/.test(value);
const packageName = value => bounded(value) && /^[A-Za-z0-9_]+(?:\.[A-Za-z0-9_]+)+$/.test(value);
const count = value => Number.isInteger(value) && value >= 0 && value <= 2147483647;
const recovery = 'Preserve the original request and provider IDs. A changed companion session cannot recover its in-memory browser ledger. Use the typed QFM owner route for recovery; separate provider facts do not acknowledge or reconcile this original. No automatic retry.';
const predispatch = new Set(['busy','session_capacity_reached','target_not_ready','session_changed','unauthorized','origin_or_host_rejected','action_unsupported',
  'confirmation_required','fields_invalid','request_invalid','content_type_invalid','body_size_invalid','request_body_timeout','kiosk_catalog_unavailable','kiosk_selection_not_reviewed',
  'kiosk_key_invalid','product_channel_invalid','provider_request_id_invalid','kiosk_command_invalid','request_id_invalid','field_invalid','duplicate_field']);
function reference(value) {
  if (!value || !guid(value.requestId) || !bounded(value.sessionId,128) || !/^[A-Za-z0-9_-]+$/.test(value.sessionId) || !serial(value.serial) || !channel(value.productChannel) ||
      !actions.has(value.action) || value.providerRequestId !== deriveProviderId(value.requestId) || value.expectedCommand !== (value.action === 'kiosk.select' ? 'select' : 'status') ||
      (value.action === 'kiosk.select' && !rowKey(value.key))) return null;
  return {requestId:value.requestId,sessionId:value.sessionId,serial:value.serial,productChannel:value.productChannel,action:value.action,
    providerRequestId:value.providerRequestId,expectedCommand:value.expectedCommand,...(value.action === 'kiosk.select' ? {key:value.key} : {})};
}
function installationValid(value) {
  return value && ['mainInstalled','setupHelperInstalled','setupHelperReady','sameSignerControlGranted','hostOperatorAvailable'].every(key => typeof value[key] === 'boolean') &&
    nullable(value.mainVersion) && nullable(value.setupHelperVersion);
}
function resultValid(value, expectedId, command) {
  return value && value.providerRequestId === expectedId && value.command === command && typeof value.accepted === 'boolean' && typeof value.completed === 'boolean' &&
    ['selectedKey','selectedName','selectedPackage'].every(key => nullable(value[key])) && typeof value.selectedInstalled === 'boolean' && typeof value.selectedLaunchable === 'boolean' &&
    ['installedCount','notInstalledCount','visibleCount'].every(key => count(value[key])) && typeof value.visibleEntriesTruncated === 'boolean' && typeof value.projectionTruncated === 'boolean' &&
    Array.isArray(value.entries) && value.entries.length <= 128 && value.entries.every(entry => rowKey(entry.key) && bounded(entry.name) && nullable(entry.packageName) &&
      ['installed','launchable','selectable'].every(key => typeof entry[key] === 'boolean') && (!entry.selectable || entry.installed && entry.launchable && packageName(entry.packageName)));
}

// Catalogs and provider facts are ephemeral. Only one small original mutation
// reference survives reload; separate facts never clear that reference.
export function createKioskView({api,getContext,setBusy,showResult}) {
  const element = id => document.getElementById('kiosk-' + id), text = (id,value) => { element(id).textContent = value; };
  let original = null, damaged = false, storageAvailable = true, catalog = null, highlighted = null, selectedFact = null, installed = null, factsView = null;
  let reading = false, writing = false, probing = false, factsReading = false;
  let authStamp = '', targetStamp = '', scopeStamp = '', authEpoch = 0, scopeEpoch = 0, catalogEpoch = 0, rowEpoch = 0;
  let message = 'No Kiosk request sent.', installationMessage = 'Installation facts unknown.', catalogMessage = 'No catalog requested.', factsMessage = 'No separate provider facts read.';
  const connected = context => Boolean(context.connected && context.sessionId);
  const ready = context => context.selected?.isReady === true && serial(context.selected.serial);
  const supports = (context,action) => context.actions?.has?.(action) === true;
  const selectedChannel = () => element('channel').value;
  const hasUnresolved = () => Boolean(original || damaged);
  function persist() {
    try {
      if (original) sessionStorage.setItem(storageKey,JSON.stringify(reference(original)));
      else if (damaged) sessionStorage.setItem(storageKey,'{}');
      else sessionStorage.removeItem(storageKey);
      return true;
    } catch { storageAvailable = false; return false; }
  }
  try {
    const saved = sessionStorage.getItem(storageKey);
    if (saved) {
      try { original = saved.length <= 4096 ? reference(JSON.parse(saved)) : null; } catch { /* retain a sanitized blocked recovery marker */ }
      damaged = !original; persist(); message = original ? 'Original Kiosk reference restored. Explicitly read its original result; nothing was replayed.' : 'Saved Kiosk reference is invalid. ' + recovery;
    }
  } catch { storageAvailable = false; }
  function clearCatalog() { catalog = null; highlighted = null; selectedFact = null; catalogEpoch++; }
  const eligible = entry => Boolean(catalog && entry && entry.selectable && entry.installed && entry.launchable && packageName(entry.packageName) &&
    catalog.result.entries.filter(candidate => candidate.key === entry.key).length === 1);
  function filtered() {
    const query = element('filter').value.trim().toLowerCase();
    return catalog ? catalog.result.entries.filter(entry => [entry.name,entry.packageName,entry.key].some(value => value?.toLowerCase().includes(query))) : [];
  }
  function update() {
    const context = getContext(), currentAuth = JSON.stringify([context.sessionId,Boolean(context.connected)]);
    const target = JSON.stringify([context.selected?.serial,context.selected?.isReady,selectedChannel()]);
    if (currentAuth !== authStamp) { authStamp = currentAuth; authEpoch++; clearCatalog(); installed = null; factsView = null; installationMessage = 'Installation facts unknown for the current connection.'; catalogMessage = 'No catalog read for the current connection. Open one explicitly.'; factsMessage = 'Separate provider facts cleared after authentication changed.'; }
    if (target !== targetStamp) { targetStamp = target; clearCatalog(); installed = null; installationMessage = 'Installation facts unknown for the current target/channel.'; catalogMessage = 'No catalog read for the current target/channel. Open one explicitly.'; }
    const visible = filtered();
    if (highlighted && !visible.includes(highlighted)) { highlighted = null; rowEpoch++; }
    const scope = JSON.stringify([currentAuth,target,catalogEpoch,rowEpoch,element('filter').value,Boolean(context.externalUnresolved),
      ...['kiosk.installation','kiosk.catalog.open','kiosk.select','kiosk.original-result'].map(action => supports(context,action))]);
    if (scope !== scopeStamp) { scopeStamp = scope; scopeEpoch++; element('open-confirm').checked = false; element('select-confirm').checked = false; }
    const live = connected(context), busy = Boolean(context.busy || reading || writing || factsReading), targetReady = live && ready(context) && channel(selectedChannel());
    const canMutate = targetReady && !busy && !probing && !hasUnresolved() && !context.externalUnresolved && storageAvailable;
    element('channel').disabled = !live;
    element('installation').disabled = !targetReady || busy || !supports(context,'kiosk.installation');
    element('open-confirm').disabled = !canMutate || !supports(context,'kiosk.catalog.open');
    element('open').disabled = element('open-confirm').disabled || !element('open-confirm').checked;
    element('filter').disabled = !live || !catalog;
    element('select-confirm').disabled = !canMutate || !supports(context,'kiosk.select') || !eligible(highlighted);
    element('select').disabled = element('select-confirm').disabled || !element('select-confirm').checked;
    element('original-status').disabled = !original || !live || context.sessionId !== original.sessionId || probing;
    element('original-save').disabled = !original;
    element('original-facts').disabled = !original || !live || busy || probing || !supports(context,'kiosk.original-result');
    text('target',ready(context) ? context.selected.serial : 'No ready target');
    text('product',selectedChannel() === 'labs' ? 'io.github.mesmerprism.rustykiosk.labs' : 'io.github.mesmerprism.rustykiosk');
    text('capability', !live ? 'Connect to check this host’s Kiosk actions.' : supports(context,'kiosk.installation') || supports(context,'kiosk.catalog.open')
      ? 'Optional Kiosk actions use the exact selected target and explicit Stable/Labs channel. Native retained configuration and protected staging are not required.' : 'This host does not advertise Kiosk actions. Use the typed QFM CLI.');
    text('installation-state',installed ? installed.mainInstalled ? 'Installed (reported installation facts).' : 'Not installed (reported installation facts).' : installationMessage);
    text('version',installed?.mainVersion || 'Unknown'); text('helper-version',installed?.setupHelperVersion || 'Unknown');
    text('helper',installed ? `${installed.setupHelperInstalled ? 'Installed' : 'Not installed'}; ready: ${installed.setupHelperReady ? 'yes' : 'no'}` : 'Unknown');
    text('grant',installed ? installed.sameSignerControlGranted ? 'Reported granted' : 'Reported not granted' : 'Unknown');
    text('provider',installed ? installed.hostOperatorAvailable ? 'Reported available' : 'Reported unavailable' : 'Unknown');
    element('entries').replaceChildren();
    for (const entry of visible) {
      const item = document.createElement('li'), button = document.createElement('button'), label = document.createElement('span'), name = document.createElement('strong'), detail = document.createElement('small');
      button.type = 'button'; button.className = 'device-button'; button.setAttribute('aria-pressed',String(highlighted === entry));
      name.textContent = entry.name; detail.textContent = `${entry.packageName || 'No installed package'} · ${entry.installed ? 'installed' : 'not installed'} · ${entry.launchable ? 'public launcher reported' : 'no public launcher reported'}`;
      label.append(name,detail); button.append(label); item.append(button); element('entries').append(item);
      button.addEventListener('click',() => { if (catalog?.result.entries.includes(entry)) { highlighted = entry; rowEpoch++; update(); } });
    }
    text('catalog-state',catalog ? `Reported installed: ${catalog.result.installedCount}; not installed: ${catalog.result.notInstalledCount}; visible: ${catalog.result.visibleCount}. ${visible.length} matching rows from ${catalog.result.entries.length} returned rows.${catalog.result.visibleEntriesTruncated || catalog.result.projectionTruncated ? ' Catalog/projection is truncated; displayed rows are incomplete.' : ''} This is the returned snapshot, not a live refresh.` : catalogMessage);
    text('row-key',highlighted?.key || 'No local row highlighted'); text('row-package',highlighted?.packageName || 'Unknown');
    text('provider-selected',selectedFact?.selectedKey || 'Unknown'); text('provider-selected-package',selectedFact?.selectedPackage || 'Unknown');
    element('original').hidden = !original;
    if (original) { text('original-id',original.requestId); text('original-provider',original.providerRequestId); text('original-target',original.serial); text('original-channel',original.productChannel); text('original-command',original.expectedCommand); text('original-key',original.key || 'Not applicable'); }
    text('provider-facts',factsMessage);
    text('facts-target',factsView ? `${factsView.serial} / ${factsView.productChannel}` : 'Unknown'); text('facts-provider',factsView?.providerRequestId || 'Unknown');
    text('facts-selected',factsView?.result?.selectedKey || 'Unknown'); text('facts-command',factsView?.result?.command || 'Unknown');
    text('state',message + (hasUnresolved() ? ' Original outcome remains unresolved; new mutations are blocked. ' + recovery : '') +
      (context.externalUnresolved ? ' Another original mutation is unresolved; Kiosk changes are blocked.' : '') + (!storageAvailable ? ' Session storage is unavailable; no new Kiosk mutation can be dispatched.' : ''));
  }
  function common(value, request) {
    if (value?.requestId !== request.requestId || value.action !== request.action || value.result != null) throw new Error('Kiosk response binding is invalid');
    if (value.state === 'running' && value.kiosk == null) return null;
    const facts = value.kiosk;
    if (!facts || facts.serial !== request.serial || facts.productChannel !== request.productChannel || facts.operation !== request.action.slice(6) ||
        !['installationOnly','pending','missing','completed','rejected','unknown'].includes(facts.providerState) ||
        ![true,false,null].includes(facts.activityOpenRequested) || (facts.installation !== null && !installationValid(facts.installation)) ||
        facts.originalFactsOnly !== (request.action === 'kiosk.original-result')) throw new Error('Kiosk target/channel facts are invalid');
    return facts;
  }
  function markerMatches(marker,saved) {
    return marker && providerId(marker.operationId) && marker.target === saved.serial &&
      marker.commandKind === (saved.expectedCommand === 'select' ? 'selectRustyKioskApp' : 'openRustyKioskCatalog') &&
      marker.desiredState === (saved.expectedCommand === 'select' ? `Rusty Kiosk installation inspected; selection ${saved.key} requested if available (${saved.productChannel})` : `Rusty Kiosk installation inspected; catalog requested if available (${saved.productChannel})`);
  }
  function validateMutation(value,saved,sourceBinding) {
    const facts = common(value,saved); if (!facts) return {kind:'pending',facts:null};
    const noOpen = facts.providerState === 'installationOnly' && facts.activityOpenRequested === false && facts.result === null && facts.installation?.hostOperatorAvailable === false;
    if (facts.providerRequestId !== saved.providerRequestId ||
        (facts.result !== null && !resultValid(facts.result,saved.providerRequestId,saved.expectedCommand)) ||
        (value.mutation != null && !markerMatches(value.mutation,saved))) throw new Error('Kiosk provider/marker binding is invalid');
    if (saved.expectedCommand === 'select' && (facts.reviewedKey !== saved.key || !guid(facts.catalogRequestId) || !packageName(facts.reviewedPackage) ||
        (sourceBinding && (facts.catalogRequestId !== sourceBinding.catalogRequestId || facts.reviewedPackage !== sourceBinding.packageName)))) throw new Error('Kiosk selection source is invalid');
    if (value.state !== 'completed') return {kind:'pending',facts};
    if (!markerMatches(value.mutation,saved) || value.mutation.stage !== 'confirmed' || value.mutation.headsetReadback !== true) throw new Error('Kiosk change is not confirmed');
    if (noOpen) return {kind:'unavailable',facts};
    if (facts.providerState !== 'completed' || facts.activityOpenRequested !== true || !facts.result?.accepted || !facts.result.completed ||
        (saved.expectedCommand === 'status' && facts.catalogRequestId !== saved.requestId) ||
        (saved.expectedCommand === 'select' && (facts.result.selectedKey !== saved.key || facts.result.selectedPackage !== facts.reviewedPackage || !facts.result.selectedInstalled || !facts.result.selectedLaunchable))) throw new Error('Kiosk completed result is unconfirmed');
    return {kind:'completed',facts};
  }
  const currentTarget = saved => { const context = getContext(); return connected(context) && ready(context) && context.selected.serial === saved.serial && selectedChannel() === saved.productChannel; };
  function acknowledge(value,saved,sourceBinding) {
    const checked = validateMutation(value,saved,sourceBinding);
    if (checked.kind === 'pending') { message = 'Owner reports a pending or uncertain Kiosk result. Preserve the original reference.'; showResult(value,message); return; }
    original = null; persist();
    if (currentTarget(saved)) {
      if (checked.facts.installation) installed = checked.facts.installation;
      if (checked.kind === 'unavailable') { clearCatalog(); catalogMessage = saved.expectedCommand === 'select' ? 'Selection not performed: operator unavailable; no catalog returned. Open a fresh catalog before another selection.' : 'Kiosk operator unavailable; no catalog returned. No activity was opened by this conditional operation.'; }
      else if (saved.expectedCommand === 'status') { catalog = {requestId:saved.requestId,result:checked.facts.result}; selectedFact = checked.facts.result; highlighted = null; catalogEpoch++; }
      else { selectedFact = checked.facts.result; rowEpoch++; }
    }
    message = checked.kind === 'unavailable' ? 'Owner confirmed the conditional installation check without opening the activity. No selection was performed.' :
      saved.expectedCommand === 'select' ? 'Owner confirmed the exact Kiosk selection. This does not establish an app launch or readiness.' : 'Owner returned the requested Kiosk catalog. Review a row separately before selecting it on the headset.';
    showResult(value,message);
  }
  async function mutate(action) {
    update(); if (element(action === 'kiosk.select' ? 'select' : 'open').disabled) return;
    const context = getContext(), selection = highlighted, sourceBinding = action === 'kiosk.select' ? {catalogRequestId:catalog.requestId,packageName:selection.packageName} : null;
    const requestId = crypto.randomUUID(), saved = reference({requestId,sessionId:context.sessionId,serial:context.selected.serial,productChannel:selectedChannel(),action,
      expectedCommand:action === 'kiosk.select' ? 'select' : 'status',key:selection?.key,providerRequestId:deriveProviderId(requestId)});
    if (!saved) return; original = saved;
    if (!persist()) { original = null; message = 'Kiosk mutation not sent: original reference could not be persisted.'; update(); return; }
    if (action === 'kiosk.catalog.open') { clearCatalog(); catalogMessage = 'Waiting for an explicitly requested catalog.'; }
    const request = {requestId:saved.requestId,action,serial:saved.serial,productChannel:saved.productChannel,...(sourceBinding ? {catalogRequestId:sourceBinding.catalogRequestId,key:saved.key} : {}),confirmed:true};
    writing = true; element('open-confirm').checked = false; element('select-confirm').checked = false; setBusy(true); message = 'Original Kiosk change sent once. Waiting for matching provider readback.'; update();
    const sentAuth = authEpoch, sentScope = scopeEpoch;
    try {
      const value = await api('/browser/v1/execute',{method:'POST',body:JSON.stringify(request)}); update(); if (original !== saved) return;
      if (sentAuth !== authEpoch || sentScope !== scopeEpoch || !connected(getContext())) { message = 'Reply arrived after authentication, target, channel, catalog or local row changed. Preserve the original and explicitly read its result.'; return; }
      acknowledge(value,saved,sourceBinding);
    } catch (error) {
      update(); if (original !== saved) return;
      if (sentAuth === authEpoch && connected(getContext()) && getContext().sessionId === saved.sessionId && error.dispatched === false && predispatch.has(error.message)) {
        original = null; persist(); message = 'Kiosk request rejected before dispatch. Review and approve a separate new request explicitly.';
      } else { message = 'Kiosk reply unavailable or untrusted. Preserve the original; its outcome remains unknown.'; showResult({...saved,state:'outcomeUnknown'},message); }
    } finally { writing = false; setBusy(false); update(); }
  }
  async function readInstallation() {
    update(); if (element('installation').disabled) return;
    const context = getContext(), request = {requestId:crypto.randomUUID(),action:'kiosk.installation',serial:context.selected.serial,productChannel:selectedChannel()}, sentAuth = authEpoch, sentScope = scopeEpoch;
    installed = null; reading = true; setBusy(true); installationMessage = 'Reading passive installation facts.'; update();
    try {
      const value = await api('/browser/v1/execute',{method:'POST',body:JSON.stringify(request)}); update();
      if (sentAuth !== authEpoch || sentScope !== scopeEpoch) { installationMessage = 'Installation reply discarded after scope changed.'; return; }
      const facts = common(value,request);
      if (value.state !== 'completed' || value.mutation != null || !installationValid(facts?.installation) || facts.providerState !== 'installationOnly' || facts.providerRequestId !== null || facts.activityOpenRequested !== false || facts.result !== null) throw new Error('Installation facts unavailable');
      installed = facts.installation; showResult(value,'Passive Kiosk installation flags read. They do not verify signer bytes, connection health or app readiness.');
    } catch { installed = null; installationMessage = 'Installation facts unavailable. Absence and readiness are not inferred from failure.'; }
    finally { reading = false; setBusy(false); update(); }
  }
  async function originalStatus() {
    update(); if (element('original-status').disabled) return;
    const saved = original, sentAuth = authEpoch; probing = true; update();
    try {
      const value = await api('/browser/v1/requests/' + saved.requestId,{method:'GET'}); update(); if (original !== saved) return;
      if (sentAuth !== authEpoch || !connected(getContext()) || getContext().sessionId !== saved.sessionId) { message = 'Original result arrived after authentication changed; original remains unresolved.'; return; }
      acknowledge(value,saved);
    } catch { if (original === saved) message = 'Original browser result unavailable or crossed. A missing result does not prove no effect.'; }
    finally { probing = false; setBusy(Boolean(getContext().busy)); update(); }
  }
  async function readOriginalFacts() {
    update(); if (element('original-facts').disabled) return;
    const saved = original, sentAuth = authEpoch, request = {requestId:crypto.randomUUID(),action:'kiosk.original-result',serial:saved.serial,productChannel:saved.productChannel,
      providerRequestId:saved.providerRequestId,expectedCommand:saved.expectedCommand,...(saved.key ? {key:saved.key} : {})};
    factsView = null; factsReading = true; setBusy(true); factsMessage = 'Reading separate original provider facts; original reference unchanged.'; update();
    try {
      const value = await api('/browser/v1/execute',{method:'POST',body:JSON.stringify(request)}); update(); if (original !== saved) return;
      if (sentAuth !== authEpoch || !connected(getContext())) { factsMessage = 'Separate provider reply discarded after authentication changed. Original unchanged.'; return; }
      const facts = common(value,request);
      if (value.state !== 'completed' || value.mutation != null || facts?.providerRequestId !== saved.providerRequestId || facts.activityOpenRequested !== false ||
          (facts.result !== null && !resultValid(facts.result,saved.providerRequestId,saved.expectedCommand))) throw new Error('Separate provider facts binding invalid');
      factsMessage = `Separate original provider facts: ${facts.providerState}. Original reference unchanged; these facts do not reconcile its browser outcome or prove launch/readiness.`;
      factsView = facts;
      showResult(value,factsMessage);
    } catch { factsMessage = 'Separate original provider facts unavailable. Original reference unchanged; no automatic retry.'; }
    finally { factsReading = false; setBusy(false); update(); }
  }
  element('channel').addEventListener('change',update); element('filter').addEventListener('input',update);
  for (const id of ['open-confirm','select-confirm']) element(id).addEventListener('change',update);
  element('installation').addEventListener('click',readInstallation); element('open').addEventListener('click',() => mutate('kiosk.catalog.open')); element('select').addEventListener('click',() => mutate('kiosk.select'));
  element('original-status').addEventListener('click',originalStatus); element('original-facts').addEventListener('click',readOriginalFacts);
  element('original-save').addEventListener('click',() => {
    if (!original) return;
    const url = URL.createObjectURL(new Blob([JSON.stringify(reference(original),null,2)],{type:'application/json'})), link = document.createElement('a');
    link.href = url; link.download = 'qfm-kiosk-original-reference.json'; link.click(); setTimeout(() => URL.revokeObjectURL(url),1000);
  });
  update(); return {update,hasUnresolved};
}
