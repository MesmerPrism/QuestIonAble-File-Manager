const key = 'qfm-browser-retained-v1';
const observationKey = 'qfm-browser-observation-v1';
const commands = new Set(['apk.install-inspected', 'app.launch-resolved', 'runtime.observe']);
const terminal = new Set(['completed', 'failed', 'cancelled', 'expired']);
const stages = new Set(['preflighted', 'running', 'cancellationRequested', ...terminal, 'outcomeUnknownRecoveryRequired', 'cleanupDebt', 'clientUncertain']);
const hash = value => typeof value === 'string' && /^[a-f0-9]{64}$/.test(value);
const binding = value => value && typeof value.operationId === 'string' && /^api-[a-f0-9]{32}$/.test(value.operationId) && hash(value.commandDigest);
const textValue = (value, maximum = 256) => typeof value === 'string' && value.length <= maximum ? value : '';
const mutates = command => command !== 'runtime.observe';
const recovery = 'Unknown outcomes require the existing typed owner recovery route; reading status does not reconcile them. Preserve the original ID and digest. Owner records have a bounded 24-hour retention; save the reference promptly.';

// A small allowlist is used both when saving and when restoring. API results,
// retained paths, credentials and captures never enter browser persistence.
function reference(value) {
  if (!binding(value)) return null;
  const version = Number.isSafeInteger(value.review?.versionCode) && value.review.versionCode >= 0 ? String(value.review.versionCode) :
    typeof value.review?.versionCode === 'string' && /^[0-9]{1,19}$/.test(value.review.versionCode) ? value.review.versionCode : '';
  const review = value.review && version && hash(value.review.signerSha256) && textValue(value.review.packageName) &&
    (value.review.versionName == null || (typeof value.review.versionName === 'string' && value.review.versionName.length <= 256)) ? {
    packageName:textValue(value.review.packageName), versionName:textValue(value.review.versionName),
    versionCode:version, signerSha256:value.review.signerSha256
  } : null;
  return {operationId:value.operationId, commandDigest:value.commandDigest,
    command:commands.has(value.command) ? value.command : '', target:textValue(value.target),
    artifactSha256:hash(value.artifactSha256) ? value.artifactSha256 : '',
    artifactSizeBytes:Number.isSafeInteger(value.artifactSizeBytes) && value.artifactSizeBytes >= 0 && value.artifactSizeBytes <= 256 * 1024 * 1024 ? value.artifactSizeBytes : null,
    expiresAt:textValue(value.expiresAt, 64), review,
    installOptions:value.command === 'apk.install-inspected' ? {
      replaceExisting:value.installOptions?.replaceExisting === true,
      grantRuntimePermissions:value.installOptions?.grantRuntimePermissions === true
    } : null,
    attempted:value.attempted !== false, stage:stages.has(value.stage) ? value.stage : 'clientUncertain'};
}

export function createRetainedView({api, getContext, setBusy, showResult}) {
  const element = id => document.getElementById(id);
  const text = (id, value) => { element(id).textContent = value; };
  let original = null, verified = false, preparing = false, executing = false, probing = false;
  let observation = null, observing = false, observationPreflight = false, observationProbing = false, observationMessage = '';
  let authStamp = '', scopeStamp = '', storageAvailable = true, message = '';
  let sourcePresent = false, authEpoch = 0;
  const alternative = error => textValue(error.alternative, 2048) ? ` ${error.alternative}` : '';
  function persist() {
    try { if (original) sessionStorage.setItem(key, JSON.stringify(reference(original))); else sessionStorage.removeItem(key); }
    catch { storageAvailable = false; }
  }
  function persistObservation() {
    try { if (observation) sessionStorage.setItem(observationKey, JSON.stringify(reference(observation))); else sessionStorage.removeItem(observationKey); }
    catch { storageAvailable = false; }
  }
  try {
    const saved = sessionStorage.getItem(key);
    if (saved && saved.length <= 4096) original = reference(JSON.parse(saved));
    persist();
  } catch { original = null; persist(); }
  if (original) message = 'Saved original reference restored. Connect and explicitly read original status before any approval.';
  try {
    const saved = sessionStorage.getItem(observationKey);
    if (saved && saved.length <= 4096) observation = reference(JSON.parse(saved));
    if (observation?.command !== 'runtime.observe') observation = null;
    persistObservation();
  } catch { observation = null; persistObservation(); }
  if (observation) observationMessage = 'Saved separate observation reference restored. Explicitly read its status; nothing is replayed.';
  const options = () => ({replaceExisting:element('native-replace').checked, grantRuntimePermissions:element('native-grant').checked});
  const selectedCommand = () => element('native-command').value;
  const hasUnresolved = () => Boolean(original && !terminal.has(original.stage));
  const observationPending = () => Boolean(observation && !terminal.has(observation.stage));
  const auth = context => JSON.stringify([context.sessionId, Boolean(context.connected)]);
  const available = context => Boolean(context.connected && context.sessionId && context.capability?.supported === true);
  const sameOptions = () => original.command !== 'apk.install-inspected' || JSON.stringify(original.installOptions) === JSON.stringify(options());
  function matches(context) {
    // Core owns an independent retained copy. Missing transient source bytes do
    // not revoke that copy; a present different selection must block approval.
    return Boolean(original?.review && original.command === selectedCommand() &&
      context.selected?.isReady && context.selected.serial === original.target &&
      (!context.artifact || (context.artifact.sha256 === original.artifactSha256 && context.artifact.sizeBytes === original.artifactSizeBytes)) && sameOptions());
  }
  function update() {
    const context = getContext(), currentAuth = auth(context);
    const scope = JSON.stringify([context.selected?.serial, context.selected?.isReady, context.artifact?.id,
      context.artifact?.sha256, context.artifact?.sizeBytes, selectedCommand(), options()]);
    if (currentAuth !== authStamp) { verified = false; element('native-confirm').checked = false; authStamp = currentAuth; authEpoch++; }
    if (sourcePresent && !context.artifact) verified = false;
    sourcePresent = Boolean(context.artifact);
    if (scope !== scopeStamp) { element('native-confirm').checked = false; scopeStamp = scope; }
    const live = available(context), command = selectedCommand();
    const source = context.artifact && /^[a-f0-9]{32}$/.test(context.artifact.id) && hash(context.artifact.sha256) && Number.isSafeInteger(context.artifact.sizeBytes);
    const transientBlock = mutates(command) && context.unresolvedTransient;
    element('native-prepare').disabled = !live || context.busy || preparing || executing || probing || observing || observationProbing || observationPending() || !source || !context.selected?.isReady || hasUnresolved() || transientBlock;
    element('native-install-options').hidden = command !== 'apk.install-inspected';
    for (const id of ['native-command', 'native-replace', 'native-grant']) element(id).disabled = preparing || executing;
    const fresh = Boolean(original && Date.parse(original.expiresAt) > Date.now());
    const executable = Boolean(live && !context.busy && !preparing && !executing && !probing && verified &&
      original?.stage === 'preflighted' && !original.attempted && fresh && matches(context) && !transientBlock);
    element('native-confirm').disabled = !executable;
    element('native-execute').disabled = !executable || !element('native-confirm').checked;
    element('native-status').disabled = !live || !binding(original) || probing;
    element('native-cancel').disabled = !live || !binding(original) || probing || original?.stage === 'preflighted' || terminal.has(original?.stage);
    element('native-save').disabled = !binding(original);
    const entered = {operationId:element('native-original-id').value, commandDigest:element('native-original-digest').value};
    const changesOriginal = hasUnresolved() && (entered.operationId !== original.operationId || entered.commandDigest !== original.commandDigest);
    element('native-read-reference').disabled = !live || probing || !binding(entered) || changesOriginal;
    element('native-review').hidden = !original;
    text('native-capability', !context.connected ? 'Connect to check the configured retained owner. Use the existing QFM desktop/CLI when this host is unavailable.' :
      context.capability?.supported === true ? 'Configured retained owner available. Prepare still checks its current state, exact bytes and selected target.' :
      `${context.capability?.reason || 'This host does not offer retained operations.'} ${context.capability?.alternative || 'Use the configured Windows companion or existing QFM desktop/CLI.'}`);
    if (original) {
      text('native-package', original.review?.packageName || 'Unknown — saved reference has no reviewed package');
      text('native-version', original.review ? `${original.review.versionName || 'Unnamed'} (${original.review.versionCode})` : 'Unknown');
      text('native-signer', original.review?.signerSha256 || 'Unknown');
      text('native-artifact-sha', original.artifactSha256 || 'Unknown');
      text('native-target', original.target || 'Unknown — use the owner record');
      text('native-expiry', Number.isFinite(Date.parse(original.expiresAt)) ? new Date(original.expiresAt).toLocaleString() : 'Unknown');
      text('native-options', original.installOptions ? `Replace existing: ${original.installOptions.replaceExisting ? 'yes' : 'no'}; grant runtime permissions: ${original.installOptions.grantRuntimePermissions ? 'yes' : 'no'}` : 'Not applicable');
      text('native-operation-id', original.operationId); text('native-command-digest', original.commandDigest);
    }
    let detail = message || 'No retained operation prepared.';
    if (original && !verified) detail += ' Read original status after authentication; reconnecting sends nothing.';
    if (original?.stage === 'preflighted' && !fresh) detail += ' Approval expired; explicitly read original status.';
    if (original?.stage === 'preflighted') detail += ' Prepared operations cannot be cancelled. Wait for expiry, then explicitly read original status before preparing another.';
    if (original?.stage === 'preflighted' && !matches(context)) detail += ' Current target, selected bytes, operation or options differ from the reviewed original.';
    if (transientBlock) detail += ' A previous transient device change requires its original readback first.';
    text('native-state', detail);
    text('native-recovery', recovery + (hasUnresolved() ? ' New preparation in the original slot is blocked. Separate runtime investigation can read fresh facts while preserving this original; use typed owner recovery to reconcile it.' : '') +
      (!storageAvailable && original ? ' Session storage is unavailable; save this reference before leaving the page.' : ''));
    element('native-observe').disabled = !live || context.busy || preparing || executing || probing || observing || observationProbing || observationPending() || !source || !context.selected?.isReady;
    text('native-observe-target', context.selected?.isReady ? context.selected.serial : 'No ready target');
    text('native-observe-artifact', source ? context.artifact.sha256 : 'No staged APK');
    element('native-observation-status').disabled = !live || !binding(observation) || observationProbing || observationPreflight;
    element('native-observation-cancel').disabled = !live || !binding(observation) || observationProbing || observationPreflight || observation?.stage === 'preflighted' || terminal.has(observation?.stage);
    element('native-observation-save').disabled = !binding(observation);
    element('native-observation-reference').hidden = !observation;
    if (observation) {
      text('native-observation-id', observation.operationId); text('native-observation-digest', observation.commandDigest);
      text('native-observation-target', observation.target || 'Unknown'); text('native-observation-artifact', observation.artifactSha256 || 'Unknown');
    }
    text('native-observation-state', (observationMessage || 'No separate observation requested.') +
      (observation?.stage === 'preflighted' ? ' Prepared observations cannot be cancelled. Wait for expiry, then explicitly read observation status before requesting another.' :
        observationPending() ? ' Read or cancel this observation before requesting another. No automatic retry.' : '') +
      (!storageAvailable && observation ? ' Session storage is unavailable; save the observation reference before leaving.' : ''));
  }
  const originalBody = () => ({operationId:original.operationId, commandDigest:original.commandDigest});
  function applyStatus(value, label, responseAuth) {
    update();
    if (!binding(value) || value.operationId !== original?.operationId || value.commandDigest !== original.commandDigest ||
      !commands.has(value.command) || (original.command && value.command !== original.command) || !stages.has(value.stage) || value.stage === 'clientUncertain' ||
      !Number.isFinite(Date.parse(value.expiresAt)) || (original.expiresAt && original.expiresAt !== value.expiresAt))
      throw new Error('Original owner response binding is invalid');
    if (!(terminal.has(original.stage) && !terminal.has(value.stage))) original.stage = value.stage;
    original.command ||= value.command;
    if (textValue(value.expiresAt, 64)) original.expiresAt = value.expiresAt;
    verified = responseAuth === authEpoch && available(getContext());
    element('native-confirm').checked = false;
    message = `${label}: owner reports ${original.stage}.`;
    if (original.stage === 'outcomeUnknownRecoveryRequired' || original.stage === 'cleanupDebt') message += ' Preserve this original; use typed owner recovery. Status alone does not reconcile it.';
    if (original.attempted && original.stage === 'preflighted') message += ' This tab already attempted execution; it will not resend the original. Use the owner recovery route.';
    persist(); showResult(value, message);
  }
  async function prepare() {
    update(); if (element('native-prepare').disabled) return;
    const context = getContext(), request = {artifactId:context.artifact.id, sha256:context.artifact.sha256,
      sizeBytes:context.artifact.sizeBytes, command:selectedCommand(), serial:context.selected.serial};
    if (request.command === 'apk.install-inspected') request.installOptions = options();
    const sentAuth = authEpoch;
    preparing = true; setBusy(true); update();
    try {
      const value = await api('/browser/v1/retained/preflight', {method:'POST', body:JSON.stringify(request)});
      if (!binding(value) || value.command !== request.command || value.artifact?.sha256 !== request.sha256 ||
        value.artifact?.sizeBytes !== request.sizeBytes || value.target?.serial !== request.serial ||
        value.artifact?.path !== `retained://${value.operationId}/base.apk` || !Number.isFinite(Date.parse(value.expiresAt)))
        throw new Error('Prepared owner receipt does not match the reviewed request');
      const identity = value.artifact.identity;
      original = reference({...value, target:request.serial, artifactSha256:request.sha256, artifactSizeBytes:request.sizeBytes,
        review:identity, installOptions:request.installOptions, attempted:false, stage:'preflighted'});
      update();
      verified = sentAuth === authEpoch && available(getContext());
      element('native-original-id').value = original.operationId; element('native-original-digest').value = original.commandDigest;
      message = 'Original operation prepared. Review the exact package, version, signer, bytes, target, expiry and options.';
      persist(); showResult(value, message);
    } catch (error) {
      message = `Preparation response unavailable or rejected: ${error.message}. No Execute was sent. An owner preflight may still retain local bytes until its expiry; no automatic retry.${alternative(error)}`;
      showResult({state:'preparationUnavailable', error:error.message}, message);
    } finally { preparing = false; setBusy(false); update(); }
  }
  async function execute() {
    update(); if (element('native-execute').disabled) return;
    const request = originalBody(), sentAuth = authEpoch;
    original.attempted = true; original.stage = 'running'; verified = false; executing = true;
    element('native-confirm').checked = false; persist(); setBusy(true);
    message = 'Original execution sent once. Waiting for owner acknowledgement; page loss does not cancel its effect.'; update();
    try { applyStatus(await api('/browser/v1/retained/execute', {method:'POST', body:JSON.stringify(request)}), 'Original execution', sentAuth); }
    catch (error) {
      if (!terminal.has(original.stage)) original.stage = 'clientUncertain';
      verified = false; persist();
      message = error.dispatched === false ? 'Execute rejected before dispatch. Keep the original reference and read or cancel it before preparing another operation.' :
        'Client outcome unknown after the execute reply was lost or rejected. Preserve the original; do not repeat execution or infer cancellation.';
      message += alternative(error);
      showResult({...request, state:original.stage, error:error.message}, message);
    } finally { executing = false; setBusy(false); update(); }
  }
  async function probe(action, entered = null) {
    update();
    const context = getContext();
    if (!available(context) || probing) return;
    if (entered) {
      if (!binding(entered) || (hasUnresolved() && (entered.operationId !== original.operationId || entered.commandDigest !== original.commandDigest))) return;
      if (!original || entered.operationId !== original.operationId || entered.commandDigest !== original.commandDigest)
        original = reference({...entered, stage:'clientUncertain', attempted:true});
    }
    if (!binding(original) || (action === 'cancel' && (original.stage === 'preflighted' || terminal.has(original.stage)))) return;
    const request = originalBody(), sentAuth = authEpoch;
    probing = true; verified = false; element('native-confirm').checked = false; persist(); update();
    try { applyStatus(await api(`/browser/v1/retained/${action}`, {method:'POST', body:JSON.stringify(request)}), action === 'cancel' ? 'Original cancellation request' : 'Original status', sentAuth); }
    catch (error) {
      verified = false; message = `Original ${action} reply unavailable: ${error.message}. Original metadata remains retained; no automatic retry or reconciliation.${alternative(error)}`;
      showResult({...request, state:original.stage, error:error.message}, message);
    } finally { probing = false; setBusy(Boolean(getContext().busy)); update(); }
  }
  function applyObservationStatus(value) {
    if (!binding(value) || value.operationId !== observation?.operationId || value.commandDigest !== observation.commandDigest ||
      value.command !== 'runtime.observe' || !stages.has(value.stage) || value.stage === 'clientUncertain' || value.expiresAt !== observation.expiresAt)
      throw new Error('Separate observation owner response binding is invalid');
    if (!(terminal.has(observation.stage) && !terminal.has(value.stage))) observation.stage = value.stage;
    observationMessage = `Separate observation: owner reports ${observation.stage}. These runtime facts do not reconcile or confirm the original operation and do not prove app readiness.`;
    persistObservation(); showResult(value, observationMessage);
  }
  async function observeSelected() {
    update(); if (element('native-observe').disabled) return;
    const context = getContext(), request = {artifactId:context.artifact.id, sha256:context.artifact.sha256,
      sizeBytes:context.artifact.sizeBytes, command:'runtime.observe', serial:context.selected.serial};
    const sentAuth = authEpoch;
    let executeSent = false;
    observing = true; observationPreflight = true; setBusy(true); update();
    try {
      const value = await api('/browser/v1/retained/preflight', {method:'POST', body:JSON.stringify(request)});
      if (!binding(value) || value.command !== 'runtime.observe' || value.artifact?.sha256 !== request.sha256 ||
        value.artifact?.sizeBytes !== request.sizeBytes || value.target?.serial !== request.serial ||
        value.artifact?.path !== `retained://${value.operationId}/base.apk` || !Number.isFinite(Date.parse(value.expiresAt)))
        throw new Error('Separate observation receipt does not match selected scope');
      observation = reference({...value, target:request.serial, artifactSha256:request.sha256, artifactSizeBytes:request.sizeBytes,
        review:value.artifact.identity, attempted:false, stage:'preflighted'});
      observationPreflight = false; persistObservation(); update();
      const current = getContext();
      if (authEpoch !== sentAuth || !available(current) || !current.selected?.isReady || current.selected.serial !== request.serial ||
        current.artifact?.id !== request.artifactId || current.artifact?.sha256 !== request.sha256 || current.artifact?.sizeBytes !== request.sizeBytes || Date.parse(observation.expiresAt) <= Date.now()) {
        observationMessage = 'Separate observation prepared, but selected scope, authentication or expiry changed. No execute sent; wait for expiry and explicitly read its retained status before a fresh request.';
        showResult(value, observationMessage); return;
      }
      // This chain has exactly one closed read-only command. Its own reference
      // never replaces, verifies or acknowledges the primary original.
      observation.attempted = true; observation.stage = 'running'; executeSent = true; persistObservation();
      observationMessage = 'Separate read-only observation sent once for the displayed observation target and APK.'; update();
      applyObservationStatus(await api('/browser/v1/retained/execute', {method:'POST',
        body:JSON.stringify({operationId:observation.operationId, commandDigest:observation.commandDigest})}));
    } catch (error) {
      if (executeSent) {
        if (!terminal.has(observation.stage)) observation.stage = 'clientUncertain';
        persistObservation();
        observationMessage = `Separate observation reply unknown. Preserve its own ID and digest; execution will not be replayed. Original operation unchanged.${alternative(error)}`;
      } else observationMessage = `Separate observation preparation unavailable: ${error.message}. No observation execute sent. The owner may retain a bounded local copy until expiry; a fresh read-only request must be explicit. No automatic retry.${alternative(error)}`;
      showResult({operationId:observation?.operationId, command:'runtime.observe', state:observation?.stage || 'preparationUnavailable', error:error.message}, observationMessage);
    } finally { observing = false; observationPreflight = false; setBusy(false); update(); }
  }
  async function probeObservation(action) {
    update();
    if (!available(getContext()) || !binding(observation) || observationProbing || observationPreflight || (action === 'cancel' && (observation.stage === 'preflighted' || terminal.has(observation.stage)))) return;
    const request = {operationId:observation.operationId, commandDigest:observation.commandDigest};
    observationProbing = true; update();
    try { applyObservationStatus(await api(`/browser/v1/retained/${action}`, {method:'POST', body:JSON.stringify(request)})); }
    catch (error) {
      observationMessage = `Separate observation ${action} reply unavailable: ${error.message}. Keep its original reference; no automatic replay or reconciliation.${alternative(error)}`;
      showResult({...request, state:observation.stage, error:error.message}, observationMessage);
    } finally { observationProbing = false; setBusy(Boolean(getContext().busy)); update(); }
  }
  element('native-prepare').addEventListener('click', prepare);
  element('native-execute').addEventListener('click', execute);
  element('native-status').addEventListener('click', () => probe('status'));
  element('native-cancel').addEventListener('click', () => probe('cancel'));
  element('native-read-reference').addEventListener('click', () => probe('status', {operationId:element('native-original-id').value, commandDigest:element('native-original-digest').value}));
  element('native-observe').addEventListener('click', observeSelected);
  element('native-observation-status').addEventListener('click', () => probeObservation('status'));
  element('native-observation-cancel').addEventListener('click', () => probeObservation('cancel'));
  for (const id of ['native-command', 'native-replace', 'native-grant', 'native-confirm']) element(id).addEventListener('change', update);
  for (const id of ['native-original-id', 'native-original-digest']) element(id).addEventListener('input', update);
  function save(value, fileName) {
    if (!binding(value)) return;
    const url = URL.createObjectURL(new Blob([JSON.stringify(reference(value), null, 2)], {type:'application/json'}));
    const link = document.createElement('a'); link.href = url; link.download = fileName; link.click();
    setTimeout(() => URL.revokeObjectURL(url), 1000);
  }
  element('native-save').addEventListener('click', () => save(original, 'qfm-operation-reference.json'));
  element('native-observation-save').addEventListener('click', () => save(observation, 'qfm-observation-reference.json'));
  window.addEventListener('online', update); window.addEventListener('offline', update);
  if (original) { element('native-command').value = original.command || 'apk.install-inspected';
    if (original.installOptions) {element('native-replace').checked = original.installOptions.replaceExisting; element('native-grant').checked = original.installOptions.grantRuntimePermissions;}
    element('native-original-id').value = original.operationId; element('native-original-digest').value = original.commandDigest;
  }
  update();
  return {update, hasUnresolved};
}
