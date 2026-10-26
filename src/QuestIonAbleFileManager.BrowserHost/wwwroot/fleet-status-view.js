const action = 'fleet.status', schema = 'questionable.file_manager.fleet_installer_status.v1';
const statuses = new Set(['not_configured','ready','already_handed_off','not_newer_than_last_handoff']);
const sources = new Set(['none','embedded_pages_metadata','environment_pages_metadata','environment_local_fixture']);
const outcomes = new Set(['guided_installer_failed','guided_installer_completed','unknown']);
const failures = new Set(['fleet_configuration_invalid','fleet_release_unavailable','fleet_release_rejected','fleet_replay_unavailable','fleet_status_timeout','fleet_status_cancelled','fleet_status_binding_invalid','fleet_status_unavailable']);
const version = value => typeof value === 'string' && /^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$/.test(value) && value.split('.').every(part => Number(part) <= 2147483647);
const identifier = value => typeof value === 'string' && /^[A-Za-z0-9._-]{1,128}$/.test(value);
const safeFailure = code => failures.has(code) ? code : 'fleet_status_unavailable';
function statusFacts(value) {
  if (!value || value.schema !== schema || !statuses.has(value.status) || typeof value.configured !== 'boolean' || !sources.has(value.sourceKind) ||
      !Number.isSafeInteger(value.observedAtMs) || value.observedAtMs < 0) return null;
  const optional = ['product','version','channel','descriptorId','descriptorSha256','highestHandoffVersion','lastOutcome'];
  if (!value.configured) {
    if (value.status !== 'not_configured' || value.sourceKind !== 'none' || optional.some(key => value[key] !== null)) return null;
  } else if (value.status === 'not_configured' || value.sourceKind === 'none' ||
      !((value.channel === 'stable' && value.product === 'rusty-fleet') || (value.channel === 'labs' && value.product === 'rusty-fleet-labs')) ||
      !version(value.version) || !identifier(value.descriptorId) || typeof value.descriptorSha256 !== 'string' || !/^[a-f0-9]{64}$/.test(value.descriptorSha256) ||
      !(value.highestHandoffVersion === null || version(value.highestHandoffVersion)) || !(value.lastOutcome === null || outcomes.has(value.lastOutcome))) return null;
  // Copy only the typed public contract. Never expose raw Core output or extra fields.
  return {schema:value.schema,status:value.status,configured:value.configured,sourceKind:value.sourceKind,product:value.product,version:value.version,
    channel:value.channel,descriptorId:value.descriptorId,descriptorSha256:value.descriptorSha256,highestHandoffVersion:value.highestHandoffVersion,
    lastOutcome:value.lastOutcome,observedAtMs:value.observedAtMs};
}

// Host-wide read observation only: no saved reference or mutation custody.
export function createFleetStatusView({api,getContext,setBusy,showResult}) {
  const element = id => document.getElementById('fleet-status-' + id), text = (id,value) => { element(id).textContent = value; };
  const stamp = context => JSON.stringify([context.connected,context.sessionId,context.authGeneration]);
  const live = context => context.connected === true && typeof context.sessionId === 'string' && context.sessionId.length > 0;
  const supported = context => context.capability?.supported === true && context.actions?.has?.(action) === true;
  let authStamp = '', authEpoch = 0, reading = false, facts = null;
  let message = 'No trusted release status read for this authentication scope.';
  function update() {
    const context = getContext(), nextStamp = stamp(context);
    if (nextStamp !== authStamp) {
      authStamp = nextStamp; authEpoch++; facts = null;
      message = 'No trusted release status read for this authentication scope.';
    }
    element('read').disabled = !live(context) || !supported(context) || Boolean(context.busy) || reading;
    text('capability', !live(context) ? 'Connect to a Windows QFM companion to check Fleet status support. Release configuration is unknown until an explicit read.' :
      !supported(context) ? 'Fleet status requires a supported Windows QFM companion. Release configuration is unknown; use the typed Windows QFM CLI owner route when needed.' :
      facts ? 'Windows Fleet status is available. Release configuration is observed below; read again for current facts. The configured source and channel belong to this host.' :
      'Windows Fleet status is available. Release configuration remains unknown until an explicit read; the configured source and channel belong to this host.');
    text('state',message);
    element('facts').hidden = !facts;
    for (const [id,key] of [['status','status'],['source','sourceKind'],['product','product'],['version','version'],['channel','channel'],['descriptor','descriptorId'],['sha','descriptorSha256'],['highest','highestHandoffVersion'],['outcome','lastOutcome'],['observed','observedAtMs']])
      text(id,facts ? String(facts[key] ?? 'Not reported') : 'Unknown');
    text('configured',facts ? facts.configured ? 'Yes' : 'No' : 'Unknown');
  }
  function failed(code,requestId) {
    facts = null; const error = safeFailure(code);
    message = `Fleet release status unavailable (${error}). No installed-service health, enrollment or prior operation acknowledgement is established.`;
    showResult({requestId,action,state:'failed',result:null,mutation:null,error},message);
  }
  async function read() {
    update(); const context = getContext();
    if (!live(context) || !supported(context) || context.busy || reading) return;
    const requestId = crypto.randomUUID(), epoch = authEpoch, scope = authStamp;
    const current = () => epoch === authEpoch && scope === stamp(getContext());
    reading = true; facts = null; message = 'Reading trusted release metadata and replay observations…'; setBusy(true); update();
    try {
      const value = await api('/browser/v1/execute',{method:'POST',body:JSON.stringify({requestId,action})});
      if (!current()) return;
      if (!value || value.requestId !== requestId || value.action !== action || value.result !== null || value.mutation !== null) {
        failed('fleet_status_binding_invalid',requestId); return;
      }
      if (value.state === 'failed' && value.fleet == null) { failed(value.error,requestId); return; }
      const observed = value.state === 'completed' && value.fleet?.operation === 'status' ? statusFacts(value.fleet.status) : null;
      if (!observed) { failed('fleet_status_binding_invalid',requestId); return; }
      facts = observed; message = 'Trusted release and replay facts observed. These facts do not acknowledge a prior operation.';
      showResult({requestId,action,state:'completed',result:null,mutation:null,fleet:{operation:'status',status:observed}},message);
    } catch (error) {
      if (current()) failed(error?.message,requestId);
    } finally {
      // Authentication changes may discard facts, but keep the shared gate until this call actually ends.
      reading = false; setBusy(false); update();
    }
  }
  element('read').addEventListener('click',read);
  return {update};
}
