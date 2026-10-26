const isHash = value => typeof value === 'string' && /^[a-f0-9]{64}$/.test(value);
const isSerial = value => typeof value === 'string' && value.length <= 256 && /^[A-Za-z0-9._:-]+$/.test(value);
const policies = {
  'apk-read-preflight': {action:'apk.preflight', label:'APK preflight'},
  'apk-read-exact': {action:'runtime.observe', verification:'exact', label:'Exact installed APK'},
  'apk-read-development': {action:'runtime.observe', verification:'development-metadata', label:'Development metadata — weaker evidence'}
};
const unknownFields = ['request','policy','package','installed','deploy','launch','diagnose','process','foreground','top-resumed','process-source','application','openxr'];
const readBoundary = 'Android process and activity facts leave application and OpenXR readiness unknown. These advisory reads do not approve installation or reconcile an original operation.';
const developmentBoundary = 'Development metadata does not prove installed bytes, signer, a unique build or transaction provenance. Application and OpenXR readiness remain unknown.';

// Advisory reads own no retained/native/transfer state and are never persisted or replayed.
export function createApkReadView({api, getContext, setBusy, showResult}) {
  const element = id => document.getElementById(id);
  const text = (name, value) => { element('apk-read-' + name).textContent = value; };
  let stamp = '', epoch = 0, reading = false, result = null;
  let message = 'No APK readiness or runtime read requested.';
  const connected = context => Boolean(context.connected && context.sessionId);
  const supports = (context, action) => context.actions?.has?.(action) === true;
  function source(context) {
    const value = context.artifact, limit = context.artifactStaging?.maximumFileBytes;
    return value && /^[a-f0-9]{32}$/.test(value.id) && value.id !== '0'.repeat(32) && isHash(value.sha256) &&
      typeof value.fileName === 'string' && value.fileName.length > 0 && value.fileName.length <= 128 &&
      Number.isSafeInteger(value.sizeBytes) && value.sizeBytes >= 0 && value.sizeBytes <= 1073741824 &&
      Number.isSafeInteger(limit) && value.sizeBytes <= limit ? value : null;
  }
  function update() {
    const context = getContext(), artifact = source(context), ready = context.selected?.isReady === true && isSerial(context.selected.serial);
    const current = JSON.stringify([Boolean(context.connected), context.sessionId, context.selected?.serial, context.selected?.isReady,
      context.artifact?.id, context.artifact?.sha256, context.artifact?.sizeBytes, context.artifact?.fileName,
      context.artifactStaging?.supported, supports(context, 'apk.preflight'), supports(context, 'runtime.observe')]);
    if (current !== stamp) {
      if (stamp) message = 'Connection, selected APK, target or available action changed. Read again explicitly for this scope.';
      stamp = current; epoch++; result = null;
    }
    const live = connected(context) && context.artifactStaging?.supported === true && artifact && ready;
    for (const [id, policy] of Object.entries(policies)) element(id).disabled = !live || Boolean(context.busy || reading) || !supports(context, policy.action);
    text('capability', !connected(context) ? 'Connect to use the available APK read actions.' : context.artifactStaging?.supported !== true
      ? 'Protected artifact reads are unavailable on this host. Use the supported Windows companion or typed CLI.'
      : !supports(context, 'apk.preflight') && !supports(context, 'runtime.observe') ? 'This host does not advertise APK read actions. Use the typed CLI.'
        : 'Available reads use staged bytes and the selected ready target. Retained operation configuration is separate.');
    text('target', ready ? context.selected.serial : 'No ready target');
    text('artifact', artifact ? `${artifact.fileName} · ${artifact.sizeBytes.toLocaleString()} bytes · SHA-256 ${artifact.sha256}` : 'No current staged APK');
    for (const field of unknownFields) text(field, 'Unknown');
    element('apk-read-checks').replaceChildren();
    text('boundary', readBoundary);
    if (result) render(result);
    text('state', message);
  }
  function inspectionMatches(value, artifact) {
    return value?.path === artifact.fileName && value.sizeBytes === artifact.sizeBytes && value.sha256 === artifact.sha256 &&
      typeof value.identity?.packageName === 'string';
  }
  function installedMatches(value, request) {
    return value === null || (value?.serial === request.serial && value.baseApkSha256 === request.sha256 && value.baseApkSizeBytes === request.sizeBytes);
  }
  function runtimeValid(value, artifact, request, exact) {
    const contract = exact ? 'questionable.file_manager.app_runtime_observation.v6' : 'questionable.file_manager.development_runtime_facts.v1';
    return value?.observationContract === contract && inspectionMatches(value.artifact, artifact) &&
      typeof value.isForeground === 'boolean' && typeof value.isTopResumed === 'boolean' &&
      Array.isArray(value.processIds) && value.processIds.every(pid => Number.isInteger(pid) && pid > 0 && pid <= 2147483647) &&
      (value.processAlive === true || value.processAlive === null) &&
      value.applicationReadiness === 'unknown' && value.applicationReadinessAuthority === false && value.openXrReadiness === 'unknown' && value.openXrReadinessAuthority === false &&
      (exact ? installedMatches(value.installed, request) : value.installed === null);
  }
  function validate(value, request, artifact) {
    if (value?.requestId !== request.requestId || value.action !== request.action) throw new Error('read_binding_invalid');
    if (value.state !== 'completed') throw new Error(typeof value.error === 'string' ? value.error : 'read_not_completed');
    const read = value.artifactRead;
    if (value.result != null || !read || read.serial !== request.serial ||
        !['id','fileName','sha256','sizeBytes'].every(key => read.artifact?.[key] === artifact[key])) throw new Error('read_source_invalid');
    if (request.action === 'apk.preflight') {
      const preflight = read.preflight;
      if (read.kind !== 'preflight' || read.verification != null || read.runtime != null || read.development != null ||
          preflight?.preflightContract !== 'questionable.file_manager.apk_preflight.v1' || preflight.serial !== request.serial ||
          !inspectionMatches(preflight.artifact, artifact) || !['absent','exact','different','unverified'].includes(preflight.installedMatch) ||
          !['readyForDeploy','readyForLaunch','readyForDiagnose'].every(key => typeof preflight[key] === 'boolean') ||
          !Array.isArray(preflight.checks) || preflight.checks.length > 64 ||
          !preflight.checks.every(check => typeof check.id === 'string' && typeof check.passed === 'boolean' && typeof check.detail === 'string')) throw new Error('preflight_receipt_invalid');
    } else {
      if (read.kind !== 'runtime' || read.verification !== request.verification || read.preflight != null) throw new Error('runtime_policy_invalid');
      if (request.verification === 'exact') {
        if (read.development != null || !runtimeValid(read.runtime, artifact, request, true)) throw new Error('runtime_receipt_invalid');
      } else {
        const development = read.development;
        if (read.runtime != null || development?.observationContract !== 'questionable.file_manager.development_app_runtime_observation.v1' ||
            development.verificationPolicy !== 'development-metadata' || !inspectionMatches(development.expectedArtifact, artifact) ||
            development.installedBytesVerified !== false || development.installedSignerVerified !== false || development.reportedInstallProvenanceVerified !== false ||
            typeof development.installedMetadata?.packageName !== 'string' || !runtimeValid(development.runtime, artifact, request, false)) throw new Error('development_receipt_invalid');
      }
    }
    return read;
  }
  function render({request, read, label}) {
    text('request', request.requestId); text('policy', label);
    text('application', 'Unknown — no application readiness authority'); text('openxr', 'Unknown — no OpenXR readiness authority');
    if (read.preflight) {
      const preflight = read.preflight;
      text('package', preflight.artifact.identity.packageName);
      text('installed', {absent:'Absent (structured preflight)',exact:'Exact installed APK',different:'Different installed APK',unverified:'Unverified installed identity'}[preflight.installedMatch]);
      for (const [name, key] of [['deploy','readyForDeploy'],['launch','readyForLaunch'],['diagnose','readyForDiagnose']])
        text(name, preflight[key] ? 'Ready by APK preflight' : 'Not ready by APK preflight');
      for (const check of preflight.checks) {
        const row = document.createElement('li'); row.textContent = `${check.passed ? 'Passed' : 'Not passed'} · ${check.id}: ${check.detail}`;
        element('apk-read-checks').append(row);
      }
    } else {
      const development = read.development, runtime = development?.runtime || read.runtime;
      text('package', runtime.artifact.identity.packageName);
      text('installed', development ? 'Matching development metadata only' : runtime.installed === null ? 'Absent (structured exact observation)' : 'Exact installed APK');
      text('process', runtime.processAlive === true ? 'Present (Android observation)' : 'Unknown — no process absence proof');
      text('foreground', runtime.isForeground ? 'Observed' : 'Not observed'); text('top-resumed', runtime.isTopResumed ? 'Observed' : 'Not observed');
      text('process-source', `${runtime.processObservationQuality || 'Unknown quality'} · ${runtime.processObservationSource || 'Unknown source'}`);
      if (development) text('boundary', developmentBoundary);
    }
  }
  async function read(id) {
    update(); if (element(id).disabled) return;
    const context = getContext(), artifact = {...source(context)}, policy = policies[id], sentEpoch = epoch;
    const request = {requestId:crypto.randomUUID(),action:policy.action,serial:context.selected.serial,artifactId:artifact.id,sha256:artifact.sha256,sizeBytes:artifact.sizeBytes,
      ...(policy.verification ? {verification:policy.verification} : {})};
    result = null; reading = true; message = `${policy.label} read sent once. Waiting for source-bound facts.`; setBusy(true); update();
    try {
      const value = await api('/browser/v1/execute', {method:'POST',body:JSON.stringify(request)});
      update();
      if (epoch !== sentEpoch || !connected(getContext())) {
        message = 'Read reply arrived after the connection, APK, target or available action changed. No facts adopted; read again explicitly.';
        showResult({requestId:request.requestId,action:request.action,state:'readScopeChanged'}, message); return;
      }
      const projection = validate(value, request, artifact);
      result = {request,read:projection,label:policy.label};
      message = `${policy.label} read completed for this APK and target. ${policy.verification === 'development-metadata' ? developmentBoundary : readBoundary}`;
      showResult(value, message);
    } catch (error) {
      update(); result = null;
      message = epoch !== sentEpoch ? 'Read ended after the selected scope changed. No facts adopted; read again explicitly.'
        : `APK read unavailable: ${String(error?.message || 'No verified read result').slice(0,256)}. Installed identity and runtime facts remain unknown. No automatic retry or verification fallback.`;
      showResult({requestId:request.requestId,action:request.action,state:'readUnavailable'}, message);
    } finally { reading = false; setBusy(false); update(); }
  }
  for (const id of Object.keys(policies)) element(id).addEventListener('click', () => read(id));
  update();
  return {update};
}
