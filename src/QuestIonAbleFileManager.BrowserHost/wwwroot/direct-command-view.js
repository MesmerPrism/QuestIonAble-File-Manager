const storageKey = 'qfm-browser-direct-command-v1';
const invokeAction = 'kiosk.direct.invoke', cancelAction = 'kiosk.direct.request-cancel';
const maximumSavedFrameCharacters = 8192;
const commands = new Set(['launch-option','select','status','show-controls','show-apps','check-setup-helper','set-launch-requirement','cancel-pending-launch','launch-normal','launch-kiosk','reload','enable-wifi-adb-after-boot','disable-wifi-adb-after-boot','enable-accessibility','disable-accessibility','passthrough-natural','passthrough-contour','request-wifi-adb','disable-wifi-adb','exit-meta-home','set-search','filter-tag','add-tag','remove-tag']);
const wifiAdbCommands = new Set(['request-wifi-adb','disable-wifi-adb']);
const bootPreferences = new Set(['enable-wifi-adb-after-boot','disable-wifi-adb-after-boot']);
const accessibilityCommands = new Set(['enable-accessibility','disable-accessibility']);
const passthroughCommands = new Set(['passthrough-natural','passthrough-contour']);
const passthroughStyles = new Set(['natural','contour-lut']);
const requirements = new Set(['any','wifi-on','wifi-off']);
const stages = new Set(['sent','pending','confirmed','failed','timedOut','pendingWearerAction','rejected','expired','cancelled','cleanupUnknown']);
const states = new Set(['pending','pending_wearer_action','confirmed','rejected','expired','cancelled','unknown']);
const guid = value => typeof value === 'string' && value.length === 36 && /^[a-f0-9]{8}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{12}$/.test(value) && value !== '00000000-0000-0000-0000-000000000000';
const session = value => typeof value === 'string' && value.length > 0 && value.length <= 128 && !/[^A-Za-z0-9_-]/.test(value);
const serial = value => typeof value === 'string' && value.length > 0 && value.length <= 256 && !/[^A-Za-z0-9._-]/.test(value);
const logical = id => 'kiosk_' + id.replaceAll('-','');
const usb = id => 'usb_' + id.replaceAll('-','');
const textCommands = new Set(['set-search','filter-tag','add-tag','remove-tag']);
const tagCommands = new Set(['add-tag','remove-tag']);
// Match Core's .NET Trim set. FEFF is data; U+0085 is whitespace.
const trimText = value => value.replace(/^[\u0009-\u000d\u0020\u0085\u00a0\u1680\u2000-\u200a\u2028\u2029\u202f\u205f\u3000]+|[\u0009-\u000d\u0020\u0085\u00a0\u1680\u2000-\u200a\u2028\u2029\u202f\u205f\u3000]+$/g,'');
const wellFormed = value => !/[\uD800-\uDBFF](?![\uDC00-\uDFFF])|(?<![\uD800-\uDBFF])[\uDC00-\uDFFF]/u.test(value);
const effectiveText = value => typeof value === 'string' && value.length <= 160 && wellFormed(value);
const canonicalText = value => effectiveText(value) && value.length > 0 && trimText(value) === value;
const valueValid = (command,value) => commands.has(command) && (command === 'launch-option' ? optionText(value,160,true) : command === 'select' ? keyValid(value) && wellFormed(value) && trimText(value) === value : command === 'set-launch-requirement' ? requirements.has(value) : textCommands.has(command) ? canonicalText(value) || !tagCommands.has(command) && value === null : value === null);
const boolOrNull = value => value === null || typeof value === 'boolean';
const keyValid = value => typeof value === 'string' && value.length > 0 && value.length <= 160 && value.trim() === value && !/[\x00-\x1f\x7f-\x9f]/.test(value);
const packageValid = value => typeof value === 'string' && value.length <= 256 && /^[A-Za-z0-9_]+(?:\.[A-Za-z0-9_]+)+$/.test(value);
const predispatch = new Set(['direct_options_unavailable','direct_option_not_reviewed','direct_launch_mismatch','kiosk_catalog_unavailable','kiosk_selection_not_reviewed','direct_selection_mismatch','busy','session_capacity_reached','session_changed','unauthorized','origin_or_host_rejected','action_unsupported','confirmation_required','fields_invalid','request_invalid','content_type_invalid','body_size_invalid','request_body_timeout','request_id_invalid','field_invalid','duplicate_field','direct_unavailable','direct_connection_not_found','direct_connection_expired','direct_owner_stopping','direct_command_unsupported','direct_value_invalid','direct_original_unavailable','direct_original_mismatch']);
function scope(value) {
  return value && session(value.sessionId) && guid(value.connectionId) && guid(value.connectRequestId) && value.usbOperationId === usb(value.connectRequestId) && serial(value.serial) && ['stable','labs'].includes(value.productChannel)
    ? {sessionId:value.sessionId,connectionId:value.connectionId,connectRequestId:value.connectRequestId,usbOperationId:value.usbOperationId,serial:value.serial,productChannel:value.productChannel} : null;
}
function selectionReview(value) {
  return value && guid(value.catalogRequestId) && serial(value.serial) && ['stable','labs'].includes(value.productChannel) &&
    keyValid(value.key) && wellFormed(value.key) && trimText(value.key) === value.key && packageValid(value.packageName)
    ? {catalogRequestId:value.catalogRequestId,serial:value.serial,productChannel:value.productChannel,key:value.key,packageName:value.packageName} : null;
}
const sameReview = (left,right) => Boolean(left && right && ['catalogRequestId','serial','productChannel','key','packageName'].every(field => left[field] === right[field]));
function launchReview(value) {
  return value && guid(value.optionsRequestId) && guid(value.selectOriginalRequestId) && guid(value.catalogRequestId) && keyValid(value.key) && wellFormed(value.key) &&
    trimText(value.key) === value.key && packageValid(value.packageName) && typeof value.bindingSha256 === 'string' && /^[a-f0-9]{64}$/.test(value.bindingSha256)
    ? {optionsRequestId:value.optionsRequestId,selectOriginalRequestId:value.selectOriginalRequestId,catalogRequestId:value.catalogRequestId,key:value.key,packageName:value.packageName,bindingSha256:value.bindingSha256} : null;
}
const sameLaunchReview = (left,right) => Boolean(left && right && ['optionsRequestId','selectOriginalRequestId','catalogRequestId','key','packageName','bindingSha256'].every(field => left[field] === right[field]));
function originalReference(value) {
  const base = scope(value);
  const review = selectionReview(value?.selectionReview);
  const launch = launchReview(value?.launchReview);
  return base && guid(value.requestId) && value.action === invokeAction && value.logicalRequestId === logical(value.requestId) && valueValid(value.command,value.value)
    && (value.command === 'select' ? review && review.serial === base.serial && review.productChannel === base.productChannel && review.key === value.value : value.selectionReview == null)
    && (value.command === 'launch-option' ? launch : value.launchReview == null)
    ? {...base,requestId:value.requestId,action:invokeAction,logicalRequestId:value.logicalRequestId,command:value.command,value:value.value,...(review ? {selectionReview:review} : {}),...(launch ? {launchReview:launch} : {})} : null;
}
function cancelReference(value) {
  const selected = value?.originalSelect == null ? null : originalReference(value.originalSelect);
  const launched = value?.originalLaunch == null ? null : originalReference(value.originalLaunch);
  if (value?.originalLaunch != null && (!launched || launched.command !== 'launch-option' || launched.requestId !== value.originalRequestId ||
      launched.connectionId !== value.connectionId || launched.sessionId !== value.sessionId || launched.logicalRequestId !== value.logicalRequestId || selected)) return null;
  if (value?.originalSelect != null && (!selected || selected.command !== 'select' || selected.requestId !== value.originalRequestId ||
      selected.connectionId !== value.connectionId || selected.sessionId !== value.sessionId || selected.logicalRequestId !== value.logicalRequestId)) return null;
  return value && session(value.sessionId) && guid(value.requestId) && guid(value.originalRequestId) && value.requestId !== value.originalRequestId && guid(value.connectionId) && value.action === cancelAction && value.logicalRequestId === logical(value.originalRequestId)
    ? {requestId:value.requestId,sessionId:value.sessionId,action:cancelAction,originalRequestId:value.originalRequestId,connectionId:value.connectionId,logicalRequestId:value.logicalRequestId,...(selected ? {originalSelect:selected} : {}),...(launched ? {originalLaunch:launched} : {})} : null;
}
const exactFields = (value,fields) => value && typeof value === 'object' && !Array.isArray(value) && Object.keys(value).length === fields.length && fields.every(field => Object.hasOwn(value,field));
const optionText = (value,maximum,nonblank = false) => typeof value === 'string' && value.length <= maximum && wellFormed(value) && (!nonblank || trimText(value).length > 0);
function optionObservation(value,review) {
  const empty = state => ({state,packageName:null,bindingSha256:null,options:[]});
  if (value == null) return empty('missing');
  if (!exactFields(value,['state','packageName','bindingSha256','options']) || !['missing','none','rejected','ready','unavailable'].includes(value.state) || !Array.isArray(value.options) || value.options.length > 64) return empty('unavailable');
  if (value.state !== 'ready') return value.packageName === null && value.bindingSha256 === null && value.options.length === 0 ? empty(value.state) : empty('unavailable');
  if (!review || value.packageName !== review.packageName || !packageValid(value.packageName) || typeof value.bindingSha256 !== 'string' || !/^[a-f0-9]{64}$/.test(value.bindingSha256)) return empty('unavailable');
  const ids = new Set(), rows = [];
  for (const row of value.options) {
    if (!exactFields(row,['schemaVersion','optionId','displayLabel','description']) || row.schemaVersion !== 1 || !optionText(row.optionId,160,true) || ids.has(row.optionId) ||
        !optionText(row.displayLabel,96,true) || !optionText(row.description,160)) return empty('unavailable');
    ids.add(row.optionId); rows.push({schemaVersion:1,optionId:row.optionId,displayLabel:row.displayLabel,description:row.description});
  }
  return {state:'ready',packageName:value.packageName,bindingSha256:value.bindingSha256,options:rows};
}
function typedResult(value,id,command,review) {
  if (!value || value.schema !== 'rusty.kiosk.cli_result.v1' || value.requestId !== id || value.command !== command || typeof value.accepted !== 'boolean' || typeof value.completed !== 'boolean' || (bootPreferences.has(command) && typeof value.requestWifiAdbAfterBoot !== 'boolean') ||
      (wifiAdbCommands.has(command) && typeof value.wifiAdbEnabled !== 'boolean') ||
      (accessibilityCommands.has(command) && typeof value.accessibilityEnabled !== 'boolean') ||
      (passthroughCommands.has(command) && (typeof value.systemPassthroughEnabled !== 'boolean' || !passthroughStyles.has(value.passthroughStyle))) ||
      (command === 'passthrough-contour' && typeof value.passthroughLutApplied !== 'boolean') ||
      !boolOrNull(value.controlsOpen) || !boolOrNull(value.pendingRequirementLaunch) || (value.selectedKey !== null && !keyValid(value.selectedKey)) ||
      (value.selectedPackage !== null && !packageValid(value.selectedPackage)) || (value.selectedLaunchRequirement !== null && !requirements.has(value.selectedLaunchRequirement)) ||
      !['selectedInstalled','selectedLaunchable','guardArmed','setupHelperInstalled','setupHelperReady'].every(field => typeof value[field] === 'boolean')) throw new Error('typed_result_invalid');
  if (command === 'set-search' && (value.search != null && !effectiveText(value.search) || value.valueMatches != null && typeof value.valueMatches !== 'boolean') ||
      command === 'filter-tag' && (value.tagFilter != null && !effectiveText(value.tagFilter) || value.valueMatches != null && typeof value.valueMatches !== 'boolean') ||
      tagCommands.has(command) && value.requestedTagPresent != null && typeof value.requestedTagPresent !== 'boolean') throw new Error('typed_result_invalid');
  const row = value.selectedEntry;
  if (row !== null && (!row || !keyValid(row.key) || !requirements.has(row.launchRequirement) || row.key !== value.selectedKey)) throw new Error('typed_result_invalid');
  if (command === 'select' && row !== null && ((row.packageName != null && !packageValid(row.packageName)) ||
      !['installed','launchable','selectable'].every(field => typeof row[field] === 'boolean'))) throw new Error('typed_result_invalid');
  return {schema:value.schema,requestId:id,command,accepted:value.accepted,completed:value.completed,controlsOpen:value.controlsOpen,selectedKey:value.selectedKey,selectedPackage:value.selectedPackage,
    selectedInstalled:value.selectedInstalled,selectedLaunchable:value.selectedLaunchable,selectedLaunchRequirement:value.selectedLaunchRequirement,pendingRequirementLaunch:value.pendingRequirementLaunch,
    guardArmed:value.guardArmed,setupHelperInstalled:value.setupHelperInstalled,setupHelperReady:value.setupHelperReady,...(command === 'launch-option' ? {lastDispatchedOptionId:optionText(value.lastDispatchedOptionId,160) ? value.lastDispatchedOptionId : null,lastDispatchedOptionPackage:packageValid(value.lastDispatchedOptionPackage) ? value.lastDispatchedOptionPackage : null} : {}),...(command === 'select' ? {launchOptions:optionObservation(value.launchOptions,review)} : {}),...(bootPreferences.has(command) ? {requestWifiAdbAfterBoot:value.requestWifiAdbAfterBoot} : {}),...(accessibilityCommands.has(command) ? {accessibilityEnabled:value.accessibilityEnabled} : {}),...(passthroughCommands.has(command) ? {systemPassthroughEnabled:value.systemPassthroughEnabled,passthroughStyle:value.passthroughStyle} : {}),...(command === 'passthrough-contour' ? {passthroughLutApplied:value.passthroughLutApplied} : {}),...(wifiAdbCommands.has(command) ? {wifiAdbEnabled:value.wifiAdbEnabled} : {}),...(command === 'set-search' ? {search:value.search,valueMatches:value.valueMatches} : {}),...(command === 'filter-tag' ? {tagFilter:value.tagFilter,valueMatches:value.valueMatches} : {}),...(tagCommands.has(command) ? {requestedTagPresent:value.requestedTagPresent} : {}),selectedEntry:row === null ? null : {key:row.key,launchRequirement:row.launchRequirement,...(command === 'select' ? {packageName:row.packageName,installed:row.installed,launchable:row.launchable,selectable:row.selectable} : {})}};
}
function project(value,ref,requestId,action) {
  const d = value?.kioskDirectCommand, operation = ({[invokeAction]:'invoke','kiosk.direct.request-status':'requestStatus','kiosk.direct.request-result':'requestResult',[cancelAction]:'requestCancel'})[action];
  const originalId = ref.action === cancelAction ? ref.originalRequestId : ref.requestId;
  const bound = ref.action === cancelAction ? ref.originalSelect || ref.originalLaunch : ref;
  const review = selectionReview(d?.selectionReview);
  const launch = launchReview(d?.launchReview);
  if (value?.requestId !== requestId || value.action !== action || value.result !== null || value.mutation !== null || !d || d.operation !== operation ||
      d.connectionId !== ref.connectionId || d.originalRequestId !== originalId || d.logicalRequestId !== ref.logicalRequestId || !guid(d.connectRequestId) || d.usbOperationId !== usb(d.connectRequestId) ||
      !serial(d.serial) || !['stable','labs'].includes(d.productChannel) || !valueValid(d.command,d.value) ||
      (ref.action === invokeAction && (d.connectRequestId !== ref.connectRequestId || d.usbOperationId !== ref.usbOperationId || d.serial !== ref.serial || d.productChannel !== ref.productChannel || d.command !== ref.command || d.value !== ref.value)) ||
      !['confirmed','pending','pendingWearerAction','unknown'].includes(d.originalEffect) || typeof d.reconcilesOriginal !== 'boolean' || typeof d.cancellationAcknowledged !== 'boolean') throw new Error('command_binding_invalid');
  if (d.command === 'select' ? !bound || bound.command !== 'select' || !sameReview(review,bound.selectionReview) || d.value !== bound.value ||
      d.connectRequestId !== bound.connectRequestId || d.usbOperationId !== bound.usbOperationId || d.serial !== bound.serial || d.productChannel !== bound.productChannel : d.selectionReview != null || ref.originalSelect != null)
    throw new Error('selection_binding_invalid');
  if (d.command === 'launch-option' ? !bound || bound.command !== 'launch-option' || !sameLaunchReview(launch,bound.launchReview) || d.value !== bound.value ||
      d.connectRequestId !== bound.connectRequestId || d.usbOperationId !== bound.usbOperationId || d.serial !== bound.serial || d.productChannel !== bound.productChannel : d.launchReview != null || ref.originalLaunch != null)
    throw new Error('launch_binding_invalid');
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
  if (d.typedResult !== null) typed = typedResult(d.typedResult,ref.logicalRequestId,d.command,review);
  if (typed && lifecycle || typed && !['invoke','requestResult'].includes(operation)) throw new Error('command_binding_invalid');
  return {connectionId:d.connectionId,connectRequestId:d.connectRequestId,usbOperationId:d.usbOperationId,serial:d.serial,productChannel:d.productChannel,operation,originalRequestId:originalId,
    logicalRequestId:ref.logicalRequestId,command:d.command,value:d.value,originalEffect:d.originalEffect,reconcilesOriginal:d.reconcilesOriginal,coreReceipt:receipt,lifecycle,typedResult:typed,cancellationAcknowledged:d.cancellationAcknowledged,...(review ? {selectionReview:review} : {}),...(launch ? {launchReview:launch} : {})};
}
function confirms(d) {
  const t = d.typedResult;
  if (!t?.accepted || !t.completed || d.originalEffect !== 'confirmed' || !d.reconcilesOriginal || d.coreReceipt?.stage !== 'confirmed') return false;
  switch (d.command) {
    case 'launch-option': return t.lastDispatchedOptionId === d.value && t.lastDispatchedOptionPackage === d.launchReview?.packageName && t.selectedPackage === d.launchReview.packageName;
    case 'select': return d.selectionReview?.key === d.value && t.selectedKey === d.value && t.selectedPackage === d.selectionReview.packageName &&
      t.selectedInstalled === true && t.selectedLaunchable === true && t.selectedEntry?.key === d.value && t.selectedEntry.packageName === d.selectionReview.packageName &&
      t.selectedEntry.installed === true && t.selectedEntry.launchable === true && t.selectedEntry.selectable === true;
    case 'set-search': return t.valueMatches === true && t.search === (d.value ?? '');
    // Core owns OrdinalIgnoreCase matching; JavaScript must not approximate it.
    case 'filter-tag': return t.valueMatches === true && effectiveText(t.tagFilter);
    case 'add-tag': return t.requestedTagPresent === true && t.selectedKey !== null && t.selectedEntry?.key === t.selectedKey;
    case 'remove-tag': return t.requestedTagPresent === false && t.selectedKey !== null && t.selectedEntry?.key === t.selectedKey;
    case 'request-wifi-adb': return t.wifiAdbEnabled === true;
    case 'disable-wifi-adb': return t.wifiAdbEnabled === false;
    case 'enable-accessibility': return t.accessibilityEnabled === true;
    case 'disable-accessibility': return t.accessibilityEnabled === false;
    case 'passthrough-natural': return t.systemPassthroughEnabled === true && t.passthroughStyle === 'natural';
    case 'passthrough-contour': return t.systemPassthroughEnabled === true && t.passthroughStyle === 'contour-lut' && t.passthroughLutApplied === true;
    case 'enable-wifi-adb-after-boot': return t.requestWifiAdbAfterBoot === true;
    case 'disable-wifi-adb-after-boot': return t.requestWifiAdbAfterBoot === false;
    case 'show-controls': return t.controlsOpen === true;
    case 'show-apps': return t.controlsOpen === false;
    case 'set-launch-requirement': return t.selectedLaunchRequirement === d.value && t.selectedEntry?.key === t.selectedKey && t.selectedEntry?.launchRequirement === d.value;
    case 'cancel-pending-launch': return t.pendingRequirementLaunch === false;
    case 'launch-normal': return t.guardArmed === false && t.pendingRequirementLaunch === false;
    case 'launch-kiosk': return t.guardArmed === true && t.pendingRequirementLaunch === false;
    default: return d.command === 'status' || d.command === 'check-setup-helper' || d.command === 'reload' || d.command === 'exit-meta-home';
  }
}

// Only whitelisted references survive reload. Predicates used by root composition
// are pure: neither may call getContext, whose connection getter reads this card.
export function createDirectCommandView({api,getContext,setBusy,showResult}) {
  const element = id => document.getElementById('direct-command-' + id), text = (id,value) => { element(id).textContent = value; };
  let original = null, cancellation = null, damaged = false, storageAvailable = true;
  let writing = false, reading = false, probing = false, cancelProbing = false, authStamp = '', authEpoch = 0, stamp = '', epoch = 0, sourceEpoch = 0;
  let message = 'No Direct command sent.', facts = null, lifecycle = null, cancelMessage = 'No cancellation requested.';
  let optionSnapshot = null, chosenOptionId = null;
  const hasUnresolved = () => Boolean(original || cancellation || damaged || !storageAvailable);
  const getCleanupReference = () => {
    if (damaged) return null;
    const ref = originalReference(original) || cancelReference(cancellation);
    return ref ? {sessionId:ref.sessionId,connectionId:ref.connectionId} : null;
  };
  function persist() {
    try {
      if (original || cancellation || damaged) {
        const frame = JSON.stringify({original:originalReference(original),cancellation:cancelReference(cancellation),...(damaged ? {damaged:true} : {})});
        if (frame.length > maximumSavedFrameCharacters) throw new Error('saved_frame_too_large');
        sessionStorage.setItem(storageKey,frame);
      }
      else sessionStorage.removeItem(storageKey);
      return true;
    } catch { storageAvailable = false; return false; }
  }
  try {
    const saved = sessionStorage.getItem(storageKey);
    if (saved) {
      let frame; try { if (saved.length <= maximumSavedFrameCharacters) frame = JSON.parse(saved); } catch { /* blocked below */ }
      original = originalReference(frame?.original); cancellation = cancelReference(frame?.cancellation);
      damaged = !frame || frame.damaged === true || Boolean(frame.original && !original) || Boolean(frame.cancellation && !cancellation) || !original && !cancellation;
      if (original && cancellation && (cancellation.originalRequestId !== original.requestId || cancellation.logicalRequestId !== original.logicalRequestId || cancellation.connectionId !== original.connectionId || cancellation.sessionId !== original.sessionId)) damaged = true;
      if (original && cancellation && (original.command === 'select' ? !sameReview(original.selectionReview,cancellation.originalSelect?.selectionReview) : cancellation.originalSelect != null)) damaged = true;
      if (original && cancellation && (original.command === 'launch-option' ? JSON.stringify(originalReference(original)) !== JSON.stringify(originalReference(cancellation.originalLaunch)) : cancellation.originalLaunch != null)) damaged = true;
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
  const currentSelection = context => {
    const review = selectionReview(context.reviewedSelection), c = connection(context);
    return review && c && context.reviewedSelection.sessionId === context.sessionId && c.serial === review.serial && c.productChannel === review.productChannel ? review : null;
  };
  const observationScope = context => {
    const c = connection(context);
    return JSON.stringify([context.connected,context.sessionId,context.authGeneration,c,Boolean(c && Date.parse(c.expiresAtUtc) > Date.now()),currentSelection(context),sourceEpoch]);
  };
  // This getter reads only this view's immutable observation and explicit local
  // choice. It never obtains context, renders, updates another view or requests.
  const getReviewedOption = () => {
    const snapshot = optionSnapshot, ref = originalReference(snapshot?.reference);
    if (!ref || ref.command !== 'select' || !guid(snapshot.requestId) || !['kiosk.direct.invoke','kiosk.direct.request-result'].includes(snapshot.action) ||
        (snapshot.action === invokeAction ? snapshot.requestId !== ref.requestId : snapshot.requestId === ref.requestId) || snapshot.observation.state !== 'ready' || chosenOptionId === null) return null;
    const matches = snapshot.observation.options.filter(row => row.optionId === chosenOptionId);
    if (matches.length !== 1 || !optionText(matches[0].optionId,160,true)) return null;
    const review = launchReview({optionsRequestId:snapshot.requestId,selectOriginalRequestId:ref.requestId,catalogRequestId:ref.selectionReview.catalogRequestId,
      key:ref.selectionReview.key,packageName:ref.selectionReview.packageName,bindingSha256:snapshot.observation.bindingSha256});
    return review ? {...scope(ref),optionsAction:snapshot.action,launchReview:review,value:matches[0].optionId,displayLabel:matches[0].displayLabel} : null;
  };
  const currentOption = context => {
    const selected = getReviewedOption();
    return selected && optionSnapshot.scope === observationScope(context) && current(context,selected) ? selected : null;
  };
  function renderOptions(context) {
    element('launch-options').hidden = !optionSnapshot;
    const list = element('launch-options-rows'); list.replaceChildren();
    const chooser = element('option-choice'); chooser.replaceChildren();
    const placeholder = document.createElement('option'); placeholder.value = ''; placeholder.textContent = 'Choose a returned option'; chooser.append(placeholder);
    chooser.disabled = !live(context) || optionSnapshot?.observation.state !== 'ready' || optionSnapshot.observation.options.length === 0;
    if (!optionSnapshot) {
      for (const id of ['launch-options-source','launch-options-state','launch-options-binding']) text(id,'');
      return;
    }
    const {reference:ref,observation:o} = optionSnapshot, review = ref.selectionReview;
    text('launch-options-source',`Original request: ${ref.requestId}; logical request: ${ref.logicalRequestId}; catalog source: ${review.catalogRequestId}; selected key: ${review.key}; package: ${review.packageName}; target: ${ref.serial} · ${ref.productChannel}; connection: ${ref.connectionId}; host session: ${ref.sessionId}.`);
    text('launch-options-state',`Returned option status: ${o.state}; ${o.options.length} options.${o.state === 'unavailable' ? ' Option observation unavailable; the selection acknowledgement remains separate.' : ''}`);
    text('launch-options-binding',o.state === 'ready' ? `Returned package: ${o.packageName}; binding SHA-256: ${o.bindingSha256}.` : 'No ready option binding observed.');
    for (const row of o.options) {
      const item = document.createElement('li'), label = document.createElement('strong'), id = document.createElement('code'), description = document.createElement('p');
      label.textContent = row.displayLabel; id.textContent = JSON.stringify(row.optionId); description.textContent = row.description;
      item.append(label,id,description); list.append(item);
      const choice = document.createElement('option'); choice.value = String(chooser.options.length - 1); choice.textContent = row.displayLabel + ' · ' + JSON.stringify(row.optionId); chooser.append(choice);
      if (row.optionId === chosenOptionId) choice.selected = true;
    }
  }
  const editorValue = () => {
    const command = element('command').value;
    if (command === 'launch-option') return currentOption(getContext())?.value;
    if (command === 'select') return currentSelection(getContext())?.key;
    if (!textCommands.has(command)) return command === 'set-launch-requirement' ? element('requirement').value : null;
    const raw = element('text').value;
    return raw.length <= 4096 && wellFormed(raw) ? trimText(raw) || null : undefined;
  };
  function update() {
    const context = getContext(), c = connection(context), nextAuth = JSON.stringify([context.connected,context.sessionId,context.authGeneration]);
    if (optionSnapshot && optionSnapshot.scope !== observationScope(context)) optionSnapshot = null;
    if (nextAuth !== authStamp) { authStamp = nextAuth; authEpoch++; }
    const reviewed = currentSelection(context);
    const option = currentOption(context);
    const next = JSON.stringify([nextAuth,c,element('command').value,element('requirement').value,element('text').value,element('command').value === 'select' ? [reviewed,sourceEpoch] : element('command').value === 'launch-option' ? [option,observationScope(context)] : null,[...(context.actions || [])].sort(),Boolean(context.externalUnresolved)]);
    if (next !== stamp) {
      if (stamp && !hasUnresolved() && !writing && !reading) { message = 'No typed acknowledgement for this current scope. Earlier receipts apply only to their original request.'; cancelMessage = 'No cancellation requested for this current scope.'; }
      stamp = next; epoch++; element('invoke-confirm').checked = false; element('cancel-confirm').checked = false; facts = null; lifecycle = null;
    }
    const idle = live(context) && !context.busy && !writing && !reading;
    const available = c && Date.parse(c.expiresAtUtc) > Date.now();
    const canInvoke = idle && available && !hasUnresolved() && !context.externalUnresolved && supports(context,invokeAction) && valueValid(element('command').value,editorValue());
    element('command').disabled = !live(context); element('requirement').disabled = !live(context); element('requirement-field').hidden = element('command').value !== 'set-launch-requirement';
    const command = element('command').value, value = editorValue(), isText = textCommands.has(command);
    element('selection-review').hidden = command !== 'select';
    text('selection-review',reviewed ? `Reviewed app key: ${reviewed.key}; package: ${reviewed.packageName}; catalog source: ${reviewed.catalogRequestId}.` : 'Highlight an eligible app in a completed Kiosk catalog for this session, target and channel first.');
    element('option-field').hidden = command !== 'launch-option';
    text('option-review',option ? `Returned option: ${option.displayLabel}; exact ID: ${JSON.stringify(option.value)}; historical package: ${option.launchReview.packageName}; options source: ${option.launchReview.optionsRequestId}; original Select: ${option.launchReview.selectOriginalRequestId}; binding SHA-256: ${option.launchReview.bindingSha256}.` : 'Choose one option returned by a verified Select on this connection. No option ID can be typed.');
    element('text-field').hidden = !isText; element('text').disabled = !live(context);
    text('text-label',tagCommands.has(command) ? 'Tag for Kiosk’s selection at execution' : command === 'set-search' ? 'Headset search text' : 'Headset tag filter');
    text('text-review',!isText ? '' : !valueValid(command,value) ? 'Enter up to 160 characters after trimming (some emoji count as two); tags need text.' : value === null ? 'Text to send: null — clear the headset search or tag filter.' : 'Text to send: ' + JSON.stringify(value));
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
    text('boundary',command === 'launch-option' ? 'This dispatches the returned option against the CURRENT Kiosk selection. Another client or wearer may change it before execution. This separate request does not atomically bind the historical app or binding; a package mismatch is detected only after an effect may occur. Foreground and readiness remain unverified.' : command === 'select' ? 'This requests the reviewed catalog app selection only. Later launch and tag commands use Kiosk’s selection at execution; selecting an app does not launch it or establish readiness.' : textCommands.has(command) ? (tagCommands.has(command) ? 'This changes the requested tag on Kiosk’s selection when executed. Another wearer or client may change that selection. Readback identifies the returned selection and requested tag presence or absence only; it does not establish previous presence or readiness.' : 'This changes the HEADSET search or tag filter. The browser’s local catalog filter is separate. Blank text clears this headset value; no request is sent until you approve and send.') : element('command').value === 'exit-meta-home' ? 'Only Kiosk’s acceptance and completion of this exact Meta Home request can be acknowledged. Meta Home foreground, leaving an app, wearer visibility and application/OpenXR readiness remain unverified.' : wifiAdbCommands.has(element('command').value) ? (element('command').value === 'disable-wifi-adb' ? 'Disabling Wi-Fi ADB may immediately close an existing wireless ADB connection. ' : '') + 'Only Kiosk’s reported Wi-Fi ADB flag is observed. Host discovery, connectivity, pairing, address, protected approval, future boot behavior, foreground and readiness remain unverified.' : accessibilityCommands.has(element('command').value) ? 'This observes only Kiosk’s reported accessibility-enabled flag. Protected settings approval and OS permission remain wearer-owned; foreground and app readiness remain unverified.' : passthroughCommands.has(element('command').value) ? 'This observes only Kiosk-reported passthrough mode and LUT state. Visual camera output, protected approval, foreground and app readiness remain unverified.' : bootPreferences.has(element('command').value) ? 'This changes only the request-after-restart preference. Current Wi-Fi ADB, next-boot success, connectivity and protected approval remain unverified.' : c && !available ? 'Connection expired. No new native request is available; cached original GET and explicit connection cleanup remain separate.' : 'Requests affect Kiosk’s current selection, which another wearer or client can change. Guard acknowledgement does not establish a named app launch, foreground state, or application/OpenXR readiness. No atomic catalog-row launch is offered.');
    text('state',!storageAvailable ? 'Reference storage unavailable; new Invoke and cancel are blocked.' : damaged ? 'Unknown: invalid saved command references. Preserve owner evidence; no automatic recovery.' : message + (hasUnresolved() ? ' Preserve unresolved originals; new mutations stay blocked.' : ''));
    text('facts',facts && textCommands.has(facts.command) ? (tagCommands.has(facts.command) ? `Returned selected key: ${facts.selectedKey ?? 'unknown'}; requested tag ${facts.requestedTagPresent === true ? 'present' : facts.requestedTagPresent === false ? 'absent' : 'unknown'}.` : `Headset ${facts.command === 'set-search' ? 'search' : 'tag filter'} (JSON string): ${JSON.stringify(facts.command === 'set-search' ? facts.search : facts.tagFilter) ?? 'unknown'}; Core value match: ${facts.valueMatches}.`) : facts ? `Observed selected key: ${facts.selectedKey ?? 'unknown'}; package: ${facts.selectedPackage ?? 'unknown'}; controls open: ${facts.controlsOpen ?? 'unknown'}; launch requirement: ${facts.selectedLaunchRequirement ?? 'unknown'}; pending requirement launch: ${facts.pendingRequirementLaunch ?? 'unknown'}; guard armed: ${facts.guardArmed}; helper installed: ${facts.setupHelperInstalled}; helper ready: ${facts.setupHelperReady}. These are observations, not an entry-bound launch promise.` : 'No typed original result observed for this scope.');
    if (facts?.command === 'launch-option') text('facts',`Actual dispatched option ID: ${JSON.stringify(facts.lastDispatchedOptionId)}; dispatched package: ${facts.lastDispatchedOptionPackage ?? 'unknown'}; selected package: ${facts.selectedPackage ?? 'unknown'}. These observations do not establish foreground or app readiness.`);
    text('lifecycle',lifecycle ? `Original remote record: ${lifecycle.operationState}; accepted ${lifecycle.accepted}; completed ${lifecycle.completed}. Lifecycle facts do not acknowledge typed effects or this cancellation call.` : 'No separate lifecycle facts read.');
    text('cancel-state',cancelMessage + (cancellation ? ' Its own acknowledgement remains unresolved; status of the original cannot clear it.' : ''));
    element('original').hidden = !original; element('cancellation').hidden = !cancellation;
    for (const [id,value] of Object.entries({id:original?.requestId,logical:original?.logicalRequestId,command:original?.command,value:original && (textCommands.has(original.command) || original.command === 'launch-option') ? JSON.stringify(original.value) : original?.value ?? 'null',target:original?.serial,channel:original?.productChannel,connection:original?.connectionId,session:original?.sessionId})) text('original-' + id,value || 'Unknown');
    for (const [id,value] of Object.entries({id:cancellation?.requestId,original:cancellation?.originalRequestId,logical:cancellation?.logicalRequestId})) text('cancellation-' + id,value || 'Unknown');
    renderOptions(context);
  }
  function show(d,requestId,action,summary = message) {
    // The dedicated, scoped snapshot is the sole option-row output. Generic
    // result JSON can outlive this card's source/auth/connection invalidation.
    if (d.typedResult) { const {launchOptions,...baseFacts} = d.typedResult; facts = baseFacts; }
    else facts = null;
    lifecycle = d.lifecycle;
    showResult({requestId,action,connectionId:d.connectionId,originalRequestId:d.originalRequestId,logicalRequestId:d.logicalRequestId,command:d.command,value:d.value,
      originalEffect:d.originalEffect,reconcilesOriginal:d.reconcilesOriginal,coreReceipt:d.coreReceipt,lifecycle:d.lifecycle,typedResult:facts,cancellationAcknowledged:d.cancellationAcknowledged,...(d.selectionReview ? {selectionReview:d.selectionReview} : {}),...(d.launchReview ? {launchReview:d.launchReview} : {})},summary);
  }
  function acknowledgeOriginal(value,ref,requestId = ref.requestId,action = invokeAction,captureOptions = true) {
    const d = project(value,ref,requestId,action);
    if (ref.command === 'select' && !captureOptions && d.typedResult) d.typedResult = {...d.typedResult,launchOptions:{state:'unavailable',packageName:null,bindingSha256:null,options:[]}};
    if (value.state === 'completed' && confirms(d) && original?.requestId === ref.requestId) {
      if (ref.command === 'select') { chosenOptionId = null; optionSnapshot = captureOptions ? {requestId,action,reference:originalReference(ref),observation:d.typedResult.launchOptions,scope:observationScope(getContext())} : null; }
      original = null; if (!persist()) { original = ref; return; }
      message = ref.command === 'launch-option' ? 'Kiosk acknowledged the exact returned option ID and reviewed package dispatch. Current-selection dispatch is not atomic app binding; foreground and readiness remain unverified.' : ref.command === 'select' ? 'Kiosk acknowledged the reviewed app selection and exact package. App launch, foreground and readiness remain unverified.' : textCommands.has(ref.command) ? (tagCommands.has(ref.command) ? `Kiosk acknowledged the tag request. Returned selection: ${d.typedResult.selectedKey}; requested tag ${d.typedResult.requestedTagPresent ? 'present' : 'absent'}.` : 'Kiosk acknowledged the exact headset search or tag-filter request. The browser local catalog filter is separate.') : ref.command === 'exit-meta-home' ? 'Kiosk acknowledged the Meta Home request as accepted and completed. Meta Home foreground, leaving an app, wearer visibility and application/OpenXR readiness remain unverified.' : wifiAdbCommands.has(ref.command) ? `Kiosk-reported Wi-Fi ADB flag observed: ${d.typedResult.wifiAdbEnabled ? 'enabled' : 'disabled'}. Host discovery, connectivity, pairing, address, protected approval, future boot behavior, foreground and readiness remain unverified.` : accessibilityCommands.has(ref.command) ? `Kiosk-reported accessibility-enabled flag observed: ${d.typedResult.accessibilityEnabled ? 'on' : 'off'}. Protected approval, OS permission, foreground and app readiness remain unverified.` : passthroughCommands.has(ref.command) ? `Kiosk-reported passthrough state observed: ${d.typedResult.passthroughStyle}${ref.command === 'passthrough-contour' ? ', LUT reported applied' : ''}. Visual camera output, protected approval, foreground and app readiness remain unverified.` : bootPreferences.has(ref.command) ? `Request-after-restart preference observed: ${d.typedResult.requestWifiAdbAfterBoot ? 'on' : 'off'}. Current Wi-Fi ADB, next-boot success, connectivity and protected approval remain unverified.` : ref.command === 'reload' ? 'Kiosk provider reload acknowledged. App foreground and application/OpenXR readiness remain unknown.' : ['launch-normal','launch-kiosk'].includes(ref.command) ? 'Typed current-selection request acknowledged; guard mode observed. Named app launch, foreground and readiness remain unknown.' : 'Exact typed original request acknowledged. No application readiness inferred.';
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
    const ref = cancel ? cancelReference({requestId,sessionId:context.sessionId,action:cancelAction,originalRequestId:original.requestId,connectionId:original.connectionId,logicalRequestId:original.logicalRequestId,...(original.command === 'select' ? {originalSelect:originalReference(original)} : {}),...(original.command === 'launch-option' ? {originalLaunch:originalReference(original)} : {})})
      : originalReference({...c,requestId,action:invokeAction,logicalRequestId:logical(requestId),command:element('command').value,value:editorValue(),...(element('command').value === 'select' ? {selectionReview:currentSelection(context)} : {}),...(element('command').value === 'launch-option' ? {launchReview:currentOption(context)?.launchReview} : {})});
    if (!ref) return;
    if (!cancel && ref.command !== 'launch-option') optionSnapshot = null;
    if (cancel) cancellation = ref; else original = ref;
    if (!persist()) { if (cancel) cancellation = null; else original = null; update(); setBusy(Boolean(getContext().busy)); return; }
    writing = true; element('invoke-confirm').checked = false; element('cancel-confirm').checked = false; setBusy(true); update();
    const sentAuth = authEpoch, sentEpoch = epoch, sentObservation = observationScope(getContext());
    const body = cancel ? {requestId,action:cancelAction,connectionId:ref.connectionId,originalRequestId:ref.originalRequestId,confirmed:true}
      : {requestId,action:invokeAction,connectionId:ref.connectionId,command:ref.command,value:ref.value,confirmed:true,...(ref.command === 'select' ? {catalogRequestId:ref.selectionReview.catalogRequestId} : {}),...(ref.command === 'launch-option' ? {optionsRequestId:ref.launchReview.optionsRequestId} : {})};
    try {
      const value = await api('/browser/v1/execute',{method:'POST',body:JSON.stringify(body)}); update();
      if ((cancel ? cancellation : original)?.requestId !== ref.requestId) return;
      if (sentAuth !== authEpoch || sentEpoch !== epoch || !live(getContext())) { optionSnapshot = null; message = 'Reply arrived after authentication, form or connection changed. Read the exact cached original explicitly; no replay.'; return; }
      if (cancel) acknowledgeCancel(value,ref); else acknowledgeOriginal(value,ref,ref.requestId,invokeAction,sentObservation === observationScope(getContext()));
    } catch (error) {
      update(); if ((cancel ? cancellation : original)?.requestId !== ref.requestId) return;
      if (sentAuth === authEpoch && sentEpoch === epoch && live(getContext()) && error.dispatched === false && predispatch.has(error.message)) {
        if (cancel) cancellation = null; else original = null;
        if (!persist()) { if (cancel) cancellation = ref; else original = ref; }
        message = 'Request rejected before dispatch. Review a separate request explicitly.';
      } else message = 'Unknown: the request may have entered execution. Keep its original ID; never repeat Invoke or cancellation automatically.';
      optionSnapshot = null;
    } finally { writing = false; update(); setBusy(false); }
  }
  async function recover(result = false) {
    update(); if (element(result ? 'request-result' : 'request-status').disabled) return;
    const ref = {...original}, requestId = crypto.randomUUID(), action = result ? 'kiosk.direct.request-result' : 'kiosk.direct.request-status', sentAuth = authEpoch, sentEpoch = epoch, sentObservation = observationScope(getContext());
    reading = true; setBusy(true); update();
    try {
      const value = await api('/browser/v1/execute',{method:'POST',body:JSON.stringify({requestId,action,connectionId:ref.connectionId,originalRequestId:ref.requestId})}); update();
      if (original?.requestId !== ref.requestId || sentAuth !== authEpoch || sentEpoch !== epoch || !live(getContext())) { optionSnapshot = null; return; }
      if (result) acknowledgeOriginal(value,ref,requestId,action,sentObservation === observationScope(getContext()));
      else { const d = project(value,ref,requestId,action); message = 'Separate lifecycle facts read. They never acknowledge the original or a cancellation call.'; show(d,requestId,action); }
    } catch { optionSnapshot = null; message = 'Original native read unavailable or unbound. Original references unchanged; no retry or replacement connection.'; }
    finally { reading = false; update(); setBusy(false); }
  }
  async function cached(cancel = false) {
    update(); if (element(cancel ? 'cancellation-status' : 'original-status').disabled) return;
    const ref = {...(cancel ? cancellation : original)}, sentAuth = authEpoch, sentObservation = observationScope(getContext());
    if (cancel) cancelProbing = true; else probing = true; update();
    try {
      const value = await api('/browser/v1/requests/' + encodeURIComponent(ref.requestId)); update();
      if ((cancel ? cancellation : original)?.requestId !== ref.requestId || sentAuth !== authEpoch || !live(getContext()) || getContext().sessionId !== ref.sessionId) { optionSnapshot = null; return; }
      if (cancel) acknowledgeCancel(value,ref); else acknowledgeOriginal(value,ref,ref.requestId,invokeAction,sentObservation === observationScope(getContext()));
    } catch { optionSnapshot = null; message = 'Cached original result unavailable or crossed. Missing records prove no absence of effects.'; }
    finally { if (cancel) cancelProbing = false; else probing = false; update(); setBusy(Boolean(getContext().busy)); }
  }
  for (const id of ['command','requirement']) element(id).addEventListener('change',update);
  element('text').addEventListener('input',update);
  element('option-choice').addEventListener('change',() => {
    const index = element('option-choice').value;
    chosenOptionId = /^(0|[1-9][0-9]*)$/.test(index) ? optionSnapshot?.observation.options[Number(index)]?.optionId ?? null : null;
    update();
  });
  for (const id of ['invoke-confirm','cancel-confirm']) element(id).addEventListener('change',update);
  element('invoke').addEventListener('click',()=>mutate(false)); element('cancel').addEventListener('click',()=>mutate(true));
  element('request-status').addEventListener('click',()=>recover(false)); element('request-result').addEventListener('click',()=>recover(true));
  element('original-status').addEventListener('click',()=>cached(false)); element('cancellation-status').addEventListener('click',()=>cached(true));
  element('save-reference').addEventListener('click',()=>{const blob = new Blob([JSON.stringify({original:originalReference(original),cancellation:cancelReference(cancellation)},null,2)],{type:'application/json'}), url = URL.createObjectURL(blob), link = document.createElement('a'); link.href = url; link.download = 'qfm-direct-command-reference.json'; link.click(); setTimeout(()=>URL.revokeObjectURL(url),1000);});
  setInterval(update,1000);
  const selectionSourceChanged = () => { sourceEpoch++; optionSnapshot = null; update(); };
  return {update,hasUnresolved,getCleanupReference,selectionSourceChanged,getReviewedOption};
}
