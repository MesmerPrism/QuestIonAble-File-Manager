import * as usb from './usb-proof.js';
import {createRetainedView} from './retained-view.js';
import {createTransferView} from './transfer-view.js';
import {saveArtifact} from './verified-download.js';
import {createApkReadView} from './apk-read-view.js';
import {createFilePushView} from './file-push-view.js';
import {createKioskView} from './kiosk-view.js';
import {createDirectConnectionView} from './direct-connection-view.js';
import {createDirectCommandView} from './direct-command-view.js';
import {createProfileView} from './profile-view.js';
import {createFleetStatusView} from './fleet-status-view.js';
import {createDiagnosticsView} from './diagnostics-view.js';
import {createDirectTagsView} from './direct-tags-view.js';
import {createDirectStagingView} from './direct-staging-view.js';
import {createDirectUploadView} from './direct-upload-view.js';
import {createDirectInstallView} from './direct-install-view.js';
import {createPackageStopView} from './package-stop-view.js';
import {createApkSetView} from './apk-set-view.js';

const byId = id => document.getElementById(id);
const host = { token: '', session: '', selected: '', devices: [], actions: new Set(), busy: false, original: null,
  artifact:null, artifactSession:'', inspection:null, artifactStaging:{supported:false}, retainedCapability:{supported:false}, profileCapability:{supported:false}, apkSetInstall:{supported:false}, fleetStatusCapability:{supported:false}, stagingGeneration:0 };
let retainedView, transferView, apkReadView, filePushView, kioskView, directConnectionView, directCommandView, profileView, fleetStatusView, diagnosticsView, directTagsView, directStagingView, directUploadView, directInstallView, packageStopView, apkSetView;
const mutations = new Set(['wifi.enable', 'wifi.connect', 'wifi.disconnect', 'device.keep-awake', 'device.normal', 'device.performance', 'device.performance-clear']);
const originalKey = 'qfm-browser-original-v1';
const localHostMode = location.protocol === 'http:' && (location.hostname === '[::1]' || /^127(?:\.\d{1,3}){3}$/.test(location.hostname));
let connectionGeneration = 0;
const downloads = new Set();
const abortDownloads = () => { for(const download of downloads) download.abort(); };
try { host.original = JSON.parse(sessionStorage.getItem(originalKey) || 'null'); } catch { /* storage unavailable */ }
const text = (id, value) => { byId(id).textContent = value; };
const recordOriginal = value => { host.original = value; try { if (value) sessionStorage.setItem(originalKey, JSON.stringify(value)); else sessionStorage.removeItem(originalKey); } catch { /* tab memory remains authoritative */ } };
const showResult = (value, summary) => { text('result-summary', summary); text('result-json', JSON.stringify(value, null, 2)); };
const readySelected = () => host.devices.find(d => d.serial === host.selected && d.isReady);
const artifactMaximum = () => Number.isSafeInteger(host.artifactStaging.maximumFileBytes) && host.artifactStaging.maximumFileBytes > 0 &&
  host.artifactStaging.maximumFileBytes <= 1024*1024*1024 ? host.artifactStaging.maximumFileBytes : 256*1024*1024;
const saveOwnedArtifact = receipt => {
  const download = new AbortController(); downloads.add(download);
  return saveArtifact(receipt,{headers:{Authorization:`Bearer ${host.token}`,'X-Qfm-Session':host.session},signal:download.signal})
    .finally(()=>downloads.delete(download));
};
const artifactBytes = value => value >= 1024*1024*1024 ? `${value/(1024*1024*1024)} GiB` : `${value/(1024*1024)} MiB`;
function controls() {
  const connected = localHostMode && Boolean(host.token && host.session), ready = Boolean(readySelected());
  const mutatable = connected && !host.busy && !host.original && !apkSetView?.hasUnresolved() && !packageStopView?.hasUnresolved() && !retainedView?.hasUnresolved() && !filePushView?.hasUnresolved() && !kioskView?.hasUnresolved() && !directConnectionView?.hasUnresolved() && !directCommandView?.hasUnresolved() && !directUploadView?.hasUnresolved() && !directInstallView?.hasUnresolved() && !profileView?.hasUnresolved() && !diagnosticsView?.hasUnresolved();
  byId('refresh').disabled = !connected || host.busy;
  byId('read-status').disabled = !connected || !ready || host.busy;
  byId('apply-hold').disabled = !mutatable || !ready || !byId('power-confirm').checked;
  byId('restore-normal').disabled = byId('apply-hold').disabled;
  byId('wifi-connect').disabled = !mutatable || !byId('wifi-confirm').checked || !byId('wifi-host').value.trim();
  byId('wifi-enable').disabled = !mutatable || !ready || Boolean(readySelected()?.isWifiConnection) || !byId('wifi-confirm').checked;
  const supports = action => host.actions.has(action);
  byId('wifi-disconnect').disabled = !mutatable || !supports('wifi.disconnect') || !byId('wifi-confirm').checked || !byId('wifi-host').value.trim();
  byId('apply-performance').disabled = !mutatable || !ready || !supports('device.performance') || !byId('power-confirm').checked || (!byId('cpu-level').value && !byId('gpu-level').value);
  byId('clear-performance').disabled = !mutatable || !ready || !supports('device.performance-clear') || !byId('power-confirm').checked;
  for(const [id,action] of [['browse-files','files.list'],['parent-folder','files.list'],['list-packages','apk.list'],['read-permissions','apk.permissions'],['read-actor','device.actor-observe'],['read-actor-identity','device.actor-identity'],['read-forwards','adb.forwards']]) byId(id).disabled=!connected || !ready || host.busy || !supports(action);
  byId('connect').disabled = !localHostMode || host.busy;
  byId('token').disabled = !localHostMode;
  byId('local-host-link').hidden = localHostMode;
  byId('disconnect-host').hidden = !connected;
  byId('reconcile').hidden = !host.original;
  byId('reconcile').disabled = !connected || host.busy || host.original?.sessionId !== host.session;
  const staging = connected && host.artifactStaging.supported && !host.busy;
  const ownedArtifact = staging && host.artifact && host.artifactSession===host.session;
  byId('apk-upload').disabled = !staging;byId('artifact-name').disabled = !staging;
  byId('stage-apk').disabled = !staging || !byId('apk-upload').files.length || !byId('artifact-name').value;
  byId('inspect-apk').disabled = !ownedArtifact || !host.actions.has('apk.inspect');
  for(const id of ['download-artifact','remove-artifact'])byId(id).disabled=!ownedArtifact;
  byId('clear-artifacts').disabled=!staging;
  retainedView?.update();
  transferView?.update();
  apkReadView?.update();
  filePushView?.update();
  kioskView?.update();
  directConnectionView?.update();
  directCommandView?.update();
  profileView?.update();
  fleetStatusView?.update();
  diagnosticsView?.update();
  directTagsView?.update();
  directStagingView?.update();
  directUploadView?.update();
  directInstallView?.update();
  packageStopView?.update();
  apkSetView?.update();
}
function clearStatus() { for (const id of ['power-value','display-value','proximity-value','hold-value']) text(id, 'Unknown'); text('status-age',''); byId('file-entries').replaceChildren(); byId('packages').replaceChildren();byId('package-name').value='';byId('actor-pid').value='';text('files-state','Select a ready headset to browse its shell-accessible storage.');text('permissions-state','Permission facts are read-only; they do not grant access or decide app readiness.'); }
function applyStatus(value) {
  const result = value.result || {};
  const status = result.questControlStatus || result.questKeepAwakeResult?.effectiveStatus;
  if (!status) return;
  text('power-value', status.wakefulness || 'Unknown'); text('display-value', status.displayState || 'Unknown'); text('proximity-value', status.proximityState || 'Unknown');
  text('hold-value', Number.isFinite(status.proximityHoldRemainingMilliseconds) ? `${Math.round(status.proximityHoldRemainingMilliseconds / 60000)} minutes` : 'Unknown');
  text('status-age', `Observed ${new Date(status.capturedAt || Date.now()).toLocaleTimeString()}. Read again for current state.`);
}
function renderDevices(devices) {
  host.devices = devices; const list = byId('devices'); list.replaceChildren();
  if (!devices.some(d => d.serial === host.selected && d.isReady)) { host.selected = ''; clearStatus(); text('selection','Choose a headset to read its current state.'); }
  byId('device-empty').hidden = devices.length > 0;
  text('device-empty', 'No authorized devices found. Check the cable, debugging authorization or known WiFi route.');
  for (const device of devices) {
    const item = document.createElement('li'), button = document.createElement('button'); button.className = 'device-button'; button.type = 'button'; button.disabled = !device.isReady;
    button.setAttribute('aria-pressed', String(device.serial === host.selected));
    const name = document.createElement('span'), label = document.createElement('strong'), serial = document.createElement('small'), state = document.createElement('span');
    label.textContent = device.model || 'Quest headset'; serial.textContent = device.serial; state.textContent = `${device.isWifiConnection ? 'WiFi' : 'USB'} · ${device.state}`; name.append(label, serial); button.append(name, state);
    button.addEventListener('click', async () => { host.selected = device.serial; clearStatus(); text('selection', `${device.model || 'Quest'} · ${device.serial}`); byId('power-confirm').checked = false; byId('wifi-confirm').checked = false; renderDevices(host.devices); await execute('device.status', { serial: device.serial }); });
    item.append(button); list.append(item);
  }
  text('inventory-age', `Inventory ${new Date().toLocaleTimeString()}. USB and WiFi rows may refer to the same headset.`); controls();
}
async function api(path, options = {}) {
  if (!localHostMode) throw new Error('Host credentials are accepted only on the local QFM host page.');
  const response = await fetch(path, { ...options, credentials:'omit', cache:'no-store', redirect:'error', headers:{ 'Authorization':`Bearer ${host.token}`, 'X-Qfm-Session':host.session, ...(options.body ? {'Content-Type':'application/json'} : {}), ...options.headers } });
  const body = await response.text();
  const value = body ? JSON.parse(body) : {};
  if (!response.ok) { const error = new Error(value.error || `Request rejected (${response.status})`); error.status = response.status; error.dispatched = value.dispatched; error.alternative=value.alternative; throw error; }
  return value;
}
function consume(value, action, projectLive = true) {
  const receipt = value.result?.mutationReceipt || value.mutation;
  const state = receipt?.stage || value.state;
  showResult(value, `${action}: ${state}${receipt?.observedState ? ` — ${receipt.observedState}` : ''}`);
  const projectTarget = projectLive && Boolean(host.selected) && value.result?.command?.serial === host.selected;
  if (projectLive && action === 'devices' && value.result?.devices) renderDevices(value.result.devices);
  if (projectLive && value.result?.command?.serial === host.selected) applyStatus(value);
  if(projectTarget && action==='files.list' && value.result?.remoteEntries){const list=byId('file-entries');list.replaceChildren();for(const entry of value.result.remoteEntries){const item=document.createElement('li'),label=document.createElement('button');label.type='button';label.textContent=entry.name;if(entry.isDirectory){label.setAttribute('aria-label',`Open ${entry.name}`);label.addEventListener('click',()=>{byId('remote-path').value=entry.fullPath;execute('files.list',{serial:host.selected,path:entry.fullPath});});}else{label.setAttribute('aria-label',`Select file ${entry.name}`);label.addEventListener('click',()=>transferView?.selectRemoteFile?.(entry.fullPath,entry.name));}item.append(label);list.append(item);}text('files-state',`${value.result.remoteEntries.length} entries observed for ${value.result.command?.serial || host.selected}. Refresh for current state.`);}
  if(projectTarget && action==='apk.list' && value.result?.packages){const list=byId('packages');list.replaceChildren();for(const name of value.result.packages){const item=document.createElement('li'),button=document.createElement('button');button.type='button';button.textContent=name;button.addEventListener('click',()=>{byId('package-name').value=name;controls();});item.append(button);list.append(item);}}
  if(projectTarget && action==='apk.permissions' && value.result?.apkPermissionObservation)text('permissions-state','Permission facts observed. Open Latest result for the reported grants, modes and unavailable/unknown fields; this does not prove app readiness.');
  if(projectLive && action==='apk.inspect' && value.state==='completed' && value.artifactInspection?.artifact.id===host.artifact?.id && value.artifactInspection.artifact.sha256===host.artifact.sha256 && value.artifactInspection.artifact.sizeBytes===host.artifact.sizeBytes){const facts=value.artifactInspection.inspection,identity=facts.identity;if(facts.sha256===host.artifact.sha256 && facts.sizeBytes===host.artifact.sizeBytes)host.inspection={requestId:value.requestId,sessionId:host.session,artifactId:host.artifact.id,sha256:facts.sha256,sizeBytes:facts.sizeBytes,packageName:identity.packageName,versionCode:identity.versionCode,signerSha256:identity.signerSha256,splitName:identity.splitName};text('apk-package',identity.packageName);text('apk-version',`${identity.versionName || ''} (${identity.versionCode})`);text('apk-signer',identity.signerSha256);text('apk-digest',facts.sha256);text('artifact-state','Local APK inspection completed against the staged size and SHA-256.');}
  if(projectLive && action==='apk.inspect' && value.state==='failed')text('artifact-state',`Local APK inspection failed (${value.error || 'inspection_failed'}). Verify the selected APK and Android Build Tools configuration on this host.`);
  const receiptConfirmed = receipt?.stage === 'confirmed';
  const connection = value.result?.wifiAdbConnectionResult;
  const endpointReady = action === 'wifi.connect' && connection?.device?.isReady === true && connection.endpoint === host.original?.target && connection.device.serial === host.original?.target;
  if (host.original && value.requestId === host.original.requestId && ((value.state === 'completed' && (!mutations.has(action) || receiptConfirmed || endpointReady)) || value.state === 'failed')) recordOriginal(null);
}
async function execute(action, fields = {}) {
  if (!host.session || host.busy || !host.actions.has(action) || (mutations.has(action) && (host.original || apkSetView?.hasUnresolved() || packageStopView?.hasUnresolved() || retainedView?.hasUnresolved() || filePushView?.hasUnresolved() || kioskView?.hasUnresolved() || directConnectionView?.hasUnresolved() || directCommandView?.hasUnresolved() || directUploadView?.hasUnresolved() || directInstallView?.hasUnresolved() || profileView?.hasUnresolved() || diagnosticsView?.hasUnresolved()))) return;
  const requestId = crypto.randomUUID(); host.busy = true;
  const generation = connectionGeneration;
  if (mutations.has(action)) recordOriginal({ requestId, sessionId:host.session, action, target:fields.serial || fields.host });
  controls(); showResult({requestId,action,state:'sent'}, `${action}: sent. Waiting for owner readback.`);
  try { const result = await api('/browser/v1/execute', {method:'POST',body:JSON.stringify({requestId,action,...fields})}); consume(result, action, generation === connectionGeneration && Boolean(host.token && host.session)); }
  catch (error) {
    if (error.dispatched === false && host.original?.requestId === requestId) recordOriginal(null);
    // A lost response may follow a real effect. Preserve the original ID; never resubmit.
    const unknown = mutations.has(action) && error.dispatched !== false;
    showResult({requestId,action,state:unknown?'outcomeUnknown':'rejected',error:error.message}, unknown ? 'Outcome unknown. Read the original result; do not repeat this change.' : `Request not dispatched: ${error.message}`);
  } finally { host.busy = false; controls(); }
}
byId('connect-form').addEventListener('submit', async event => {
  event.preventDefault(); if (!localHostMode || host.busy) {byId('token').value='';return;} host.token = byId('token').value; byId('token').value=''; host.busy=true; const generation=++connectionGeneration;abortDownloads(); host.session='';host.selected='';host.devices=[];host.actions=new Set();clearStatus();byId('devices').replaceChildren();byId('power-confirm').checked=false;byId('wifi-confirm').checked=false;controls();
  try { const session=await api('/browser/v1/session'); if(generation!==connectionGeneration)return; host.session=session.sessionId;host.actions=new Set(session.actions || ['devices','device.status','wifi.enable','wifi.connect','device.keep-awake','device.normal']); text('host-status','Connected');
    host.artifactStaging=session.artifactStaging || {supported:false};
    host.apkSetInstall=session.apkSetInstall || {supported:false};
    if(byId('artifact-limits'))text('artifact-limits',host.artifactStaging.supported ? `Protected staging: up to ${artifactBytes(artifactMaximum())} per file, ${host.artifactStaging.maximumFiles || 8} files and ${artifactBytes(host.artifactStaging.maximumTotalBytes || 512*1024*1024)} total in this host process. Captures and retained owner copies have separate custody. Streaming save is offered where the browser supports it; buffered fallback is limited to 256 MiB.` : 'Protected staging is unavailable on this host. Use the configured Windows companion or current QFM CLI.');
    host.retainedCapability=session.retainedOperations || {supported:false,reason:'This host has no configured retained operation owner.',alternative:'Use the current typed CLI or configure the Windows companion using the guide.'};
    host.fleetStatusCapability=session.fleetInstallerStatus || {supported:false};
    host.profileCapability=session.connectivityProfiles || {supported:false,reason:'This host does not offer local connectivity profile management.',alternative:'Use the Windows companion or typed connectivity-profile CLI.'};
    if(host.artifactSession && host.artifactSession!==host.session){host.artifact=null;clearApkInspection();text('artifact-state','Host session changed. Select and stage bytes again; old handles cannot be reused.');}
    text('artifact-capability',host.artifactStaging.supported ? 'Protected transient staging is supported on this Windows host when its temporary volume and permissions are valid.' : (host.artifactStaging.reason || 'This host does not offer protected browser staging.')+' '+(host.artifactStaging.alternative || 'Use the Windows companion or current QFM desktop/CLI.'));
    const caps=session.platformCapabilities;
    if(caps) text('host-capabilities',`Host: ${caps.platform}. ${caps.hostAdbSupported ? 'Device controls require a configured ADB and authorized transport.' : 'Host ADB is unavailable on this platform.'} ${!caps.connectivityProfilesSupported || !caps.fleetInstallerSupported ? 'Private connectivity profiles and Fleet installer handoff require the Windows host; use that host for those features.' : 'Native profile and installer services are available through their supported owner routes.'}`);
    if (host.original) showResult(host.original, host.original.sessionId === host.session ? 'An original change needs readback. Use Read original result.' : 'Host session changed after an unresolved change. Inspect current state using the owner route; changes stay blocked in this tab.');
  } catch(error) { host.token='';host.session='';text('host-status',error.message); } finally {host.busy=false;controls();}
});
byId('disconnect-host').addEventListener('click',()=>{connectionGeneration++;abortDownloads();byId('power-confirm').checked=false;byId('wifi-confirm').checked=false;host.token='';host.session='';host.selected='';host.devices=[];clearStatus();byId('devices').replaceChildren();byId('device-empty').hidden=false;text('device-empty','Connect to list authorized USB and WiFi devices.');text('host-status','Not connected');text('host-capabilities',"Connect to check this host's available capabilities.");text('selection','Choose a headset to read its current state.');text('inventory-age','');controls();});
byId('refresh').addEventListener('click',()=>execute('devices'));
byId('read-status').addEventListener('click',()=>execute('device.status',{serial:host.selected}));
byId('power-confirm').addEventListener('change',controls);byId('wifi-confirm').addEventListener('change',controls);
for(const id of ['wifi-host','wifi-port'])byId(id).addEventListener('input',()=>{byId('wifi-confirm').checked=false;controls();});
byId('duration').addEventListener('change',()=>{byId('power-confirm').checked=false;controls();});
byId('apply-hold').addEventListener('click',()=>{if(!byId('apply-hold').disabled)execute('device.keep-awake',{serial:host.selected,durationMilliseconds:Number(byId('duration').value),confirmed:true});byId('power-confirm').checked=false;controls();});
byId('restore-normal').addEventListener('click',()=>{if(!byId('restore-normal').disabled)execute('device.normal',{serial:host.selected,confirmed:true});byId('power-confirm').checked=false;controls();});
byId('wifi-form').addEventListener('submit',event=>{event.preventDefault();if(!byId('wifi-connect').disabled){const address=byId('wifi-host').value.trim(),port=Number(byId('wifi-port').value);execute('wifi.connect',{host:address,port,serial:`${address}:${port}`,confirmed:true});byId('wifi-confirm').checked=false;controls();}});
byId('wifi-enable').addEventListener('click',()=>{if(!byId('wifi-enable').disabled)execute('wifi.enable',{serial:host.selected,port:Number(byId('wifi-port').value),confirmed:true});byId('wifi-confirm').checked=false;controls();});
byId('wifi-disconnect').addEventListener('click',()=>{if(!byId('wifi-disconnect').disabled){const address=byId('wifi-host').value.trim(),port=Number(byId('wifi-port').value);execute('wifi.disconnect',{host:address,port,serial:`${address}:${port}`,confirmed:true});byId('wifi-confirm').checked=false;controls();}});
for(const id of ['cpu-level','gpu-level'])byId(id).addEventListener('change',()=>{byId('power-confirm').checked=false;controls();});
byId('apply-performance').addEventListener('click',()=>{if(!byId('apply-performance').disabled)execute('device.performance',{serial:host.selected,cpu:byId('cpu-level').value===''?null:Number(byId('cpu-level').value),gpu:byId('gpu-level').value===''?null:Number(byId('gpu-level').value),confirmed:true});byId('power-confirm').checked=false;controls();});
byId('clear-performance').addEventListener('click',()=>{if(!byId('clear-performance').disabled)execute('device.performance-clear',{serial:host.selected,confirmed:true});byId('power-confirm').checked=false;controls();});
byId('file-form').addEventListener('submit',event=>{event.preventDefault();if(!byId('browse-files').disabled)execute('files.list',{serial:host.selected,path:byId('remote-path').value});});
byId('parent-folder').addEventListener('click',()=>{if(byId('parent-folder').disabled)return;const path=byId('remote-path').value.replace(/\/+$/,'');byId('remote-path').value=path.slice(0,path.lastIndexOf('/')) || '/';execute('files.list',{serial:host.selected,path:byId('remote-path').value});});
byId('list-packages').addEventListener('click',()=>{if(!byId('list-packages').disabled)execute('apk.list',{serial:host.selected});});
byId('package-name').addEventListener('input',controls);
byId('permissions-form').addEventListener('submit',event=>{event.preventDefault();if(!byId('read-permissions').disabled)execute('apk.permissions',{serial:host.selected,package:byId('package-name').value});});
for(const [id,action] of [['read-actor','device.actor-observe'],['read-actor-identity','device.actor-identity'],['read-forwards','adb.forwards']])byId(id).addEventListener('click',()=>{if(!byId(id).disabled)execute(action,{serial:host.selected,...(action==='adb.forwards'?{}:{pid:Number(byId('actor-pid').value)})});});
function clearApkInspection(){host.inspection=null;for(const id of ['apk-package','apk-version','apk-signer','apk-digest'])text(id,'Unknown');}
byId('apk-upload').addEventListener('change',()=>{const file=byId('apk-upload').files[0];byId('artifact-name').value=file?.name || '';host.artifact=null;clearApkInspection();text('artifact-state','Selected bytes have not been staged. Previously staged local files remain until explicitly cleared.');controls();});
byId('artifact-name').addEventListener('input',controls);
async function localArtifactOperation(operation){
  if(host.busy || !host.session || !host.artifactStaging.supported)return;
  host.busy=true;const generation=connectionGeneration;controls();
  try{await operation(generation);}
  catch(error){
    const uncertainty=error.saveOutcomeUnknown ? ' The selected file close outcome is unknown. Check the selected file before another Save.' : '';
    const cleanup=error.cleanupPending ? ' Cleanup is pending; wait and inspect the selected file before another Save.' : '';
    const recovery=uncertainty || cleanup ? ' Captured bytes remain staged. No automatic retry.' :
      ' No automatic retry. Reconnect and use Clear staged files if an upload response was lost.';
    text('artifact-state',`Local artifact operation failed: ${error.message} ${error.alternative || ''}${uncertainty}${cleanup}${recovery}`);
  }finally{host.busy=false;controls();}
}
byId('artifact-form').addEventListener('submit',event=>{event.preventDefault();if(byId('stage-apk').disabled)return;const file=byId('apk-upload').files[0],name=byId('artifact-name').value;if(!file || file.size>artifactMaximum()){text('artifact-state',`Choose one APK no larger than ${artifactMaximum()} bytes.`);return;}localArtifactOperation(async generation=>{const session=host.session;const value=await api('/browser/v1/artifacts',{method:'POST',body:file,headers:{'Content-Type':'application/octet-stream','X-Qfm-File-Name':name}});host.artifact=value.artifact;host.artifactSession=session;if(generation===connectionGeneration)text('artifact-state',`${value.artifact.fileName} staged locally: ${value.artifact.sizeBytes} bytes, SHA-256 ${value.artifact.sha256}.`);});});
byId('inspect-apk').addEventListener('click',()=>{if(byId('inspect-apk').disabled)return;const artifact=host.artifact;clearApkInspection();text('artifact-state','Reading the staged APK through shared Core inspection…');execute('apk.inspect',{artifactId:artifact.id,sha256:artifact.sha256,sizeBytes:artifact.sizeBytes});});
byId('remove-artifact').addEventListener('click',()=>{if(byId('remove-artifact').disabled)return;const artifact=host.artifact;localArtifactOperation(async generation=>{await api(`/browser/v1/artifacts/${artifact.id}`,{method:'DELETE'});if(host.artifact?.id===artifact.id)host.artifact=null;host.stagingGeneration++;apkSetView?.stagingCleared();if(generation===connectionGeneration){clearApkInspection();text('artifact-state','Staged file removed. Original file unchanged.');}});});
byId('clear-artifacts').addEventListener('click',()=>{if(byId('clear-artifacts').disabled)return;localArtifactOperation(async generation=>{await api('/browser/v1/artifacts/cleanup',{method:'POST'});host.artifact=null;host.stagingGeneration++;apkSetView?.stagingCleared();if(generation===connectionGeneration){clearApkInspection();text('artifact-state','Unleased staged files cleared. Original files unchanged.');}});});
byId('download-artifact').addEventListener('click',()=>{if(byId('download-artifact').disabled)return;const artifact=host.artifact;localArtifactOperation(async generation=>{const result=await saveOwnedArtifact(artifact);if(generation===connectionGeneration)text('artifact-state',result.state==='saved'?'Staged bytes saved after exact size and SHA-256 verification.':'Staged bytes verified; download handed to the browser.');});});
byId('reconcile').addEventListener('click',async()=>{if(!host.original||host.busy)return;const original=host.original,generation=connectionGeneration;host.busy=true;controls();try{consume(await api(`/browser/v1/requests/${original.requestId}`),original.action,generation===connectionGeneration && Boolean(host.token && host.session));}catch(error){showResult(original,`Original result unavailable: ${error.message}. Do not replay.`);}finally{host.busy=false;controls();}});
for(const mode of ['host','usb'])byId(`${mode}-tab`).addEventListener('click',()=>{for(const name of ['host','usb']){byId(`${name}-view`).hidden=name!==mode;if(name===mode)byId(`${name}-tab`).setAttribute('aria-current','page');else byId(`${name}-tab`).removeAttribute('aria-current');}});
const cap=usb.capability();text('usb-capability',cap.message);byId('usb-choose').disabled=!cap.supported;byId('usb-permitted').disabled=!cap.supported;
function usbResult(value){text('usb-status',`${value.state}: ${value.message}`);if(value.reading){byId('usb-reading').hidden=false;text('usb-reading',JSON.stringify(value.reading,null,2));}else{byId('usb-reading').hidden=true;}showResult(value,`USB proof: ${value.state}`);}
async function permitted(){const devices=await usb.getPermittedDevices(),select=byId('usb-selection'),selected=select.value;select.replaceChildren();const empty=document.createElement('option');empty.value='';empty.textContent='Choose a permitted device';select.append(empty);for(const device of devices){const option=document.createElement('option');option.value=device.selectionId;option.textContent=`${device.name} · ${device.usbSerial || 'USB serial unavailable'}`;select.append(option);}select.value=devices.some(d=>d.selectionId===selected)?selected:'';usbControls();return devices;}
function usbControls(){for(const id of ['usb-read','usb-reconnect','usb-disconnect'])byId(id).disabled=!byId('usb-selection').value;}
async function usbAction(action){byId('usb-choose').disabled=true;try{const result=await action();usbResult(result);await permitted();if(result.selectionId){byId('usb-selection').value=result.selectionId;usbControls();}}catch(error){usbResult({state:'failed',message:error.message});}finally{byId('usb-choose').disabled=!cap.supported;}}
byId('usb-choose').addEventListener('click',()=>usbAction(()=>usb.chooseAndRead({onState:usbResult})));
byId('usb-permitted').addEventListener('click',()=>permitted().catch(error=>usbResult({state:'failed',message:error.message})));
byId('usb-selection').addEventListener('change',()=>{byId('usb-reading').hidden=true;usbControls();});
byId('usb-read').addEventListener('click',()=>usbAction(()=>usb.read(byId('usb-selection').value,{onState:usbResult})));
byId('usb-reconnect').addEventListener('click',()=>usbAction(()=>usb.reconnect(byId('usb-selection').value,{onState:usbResult})));
byId('usb-disconnect').addEventListener('click',()=>usbAction(()=>usb.disconnect(byId('usb-selection').value)));
retainedView=createRetainedView({api,getContext:()=>({sessionId:host.session,connected:localHostMode && Boolean(host.token && host.session),busy:host.busy,selected:readySelected() || null,artifact:host.artifactSession===host.session?host.artifact:null,capability:host.retainedCapability,unresolvedTransient:Boolean(host.original || apkSetView?.hasUnresolved() || packageStopView?.hasUnresolved() || filePushView?.hasUnresolved() || kioskView?.hasUnresolved() || directConnectionView?.hasUnresolved() || directCommandView?.hasUnresolved() || directUploadView?.hasUnresolved() || directInstallView?.hasUnresolved() || profileView?.hasUnresolved() || diagnosticsView?.hasUnresolved())}),setBusy:value=>{host.busy=value;controls();},showResult});
transferView=createTransferView({api,getContext:()=>({sessionId:host.session,connected:localHostMode && Boolean(host.token && host.session),busy:host.busy,selected:readySelected() || null,selectedPackage:byId('package-name').value,actions:host.actions,artifactStaging:host.artifactStaging}),setBusy:value=>{host.busy=value;controls();},showResult,saveArtifact:saveOwnedArtifact});
apkReadView=createApkReadView({api,getContext:()=>({sessionId:host.session,connected:localHostMode && Boolean(host.token && host.session),busy:host.busy,selected:readySelected() || null,artifact:host.artifactSession===host.session?host.artifact:null,actions:host.actions,artifactStaging:host.artifactStaging}),setBusy:value=>{host.busy=value;controls();},showResult});
filePushView=createFilePushView({api,getContext:()=>({sessionId:host.session,connected:localHostMode && Boolean(host.token && host.session),busy:host.busy,selected:readySelected() || null,actions:host.actions,artifactStaging:host.artifactStaging,stagingGeneration:host.stagingGeneration,externalUnresolved:Boolean(host.original || apkSetView?.hasUnresolved() || packageStopView?.hasUnresolved() || retainedView?.hasUnresolved() || kioskView?.hasUnresolved() || directConnectionView?.hasUnresolved() || directCommandView?.hasUnresolved() || directUploadView?.hasUnresolved() || directInstallView?.hasUnresolved() || profileView?.hasUnresolved() || diagnosticsView?.hasUnresolved())}),setBusy:value=>{host.busy=value;controls();},showResult});
kioskView=createKioskView({api,getContext:()=>({sessionId:host.session,connected:localHostMode && Boolean(host.token && host.session),busy:host.busy,selected:readySelected() || null,actions:host.actions,externalUnresolved:Boolean(host.original || apkSetView?.hasUnresolved() || packageStopView?.hasUnresolved() || retainedView?.hasUnresolved() || filePushView?.hasUnresolved() || directConnectionView?.hasUnresolved() || directCommandView?.hasUnresolved() || directUploadView?.hasUnresolved() || directInstallView?.hasUnresolved() || profileView?.hasUnresolved() || diagnosticsView?.hasUnresolved())}),setBusy:value=>{host.busy=value;controls();},showResult,onSelectionSourceChange:()=>directCommandView?.selectionSourceChanged()});
const nonDirectBlockers = () => Boolean(host.original || apkSetView?.hasUnresolved() || packageStopView?.hasUnresolved() || retainedView?.hasUnresolved() || filePushView?.hasUnresolved() || kioskView?.hasUnresolved() || profileView?.hasUnresolved() || diagnosticsView?.hasUnresolved());
const otherDirectBlockers = () => nonDirectBlockers() || Boolean(directUploadView?.hasUnresolved() || directInstallView?.hasUnresolved());
const directCleanupConnectionId = () => {
  if (nonDirectBlockers()) return null;
  const references = [];
  for (const view of [directCommandView, directUploadView, directInstallView]) {
    if (!view?.hasUnresolved()) continue;
    const reference = view.getCleanupReference();
    if (!reference || reference.sessionId !== host.session) return null;
    references.push(reference);
  }
  return references.length && references.every(reference => reference.connectionId === references[0].connectionId) ? references[0].connectionId : null;
};
directConnectionView=createDirectConnectionView({api,getContext:()=>({sessionId:host.session,connected:localHostMode && Boolean(host.token && host.session),busy:host.busy,selected:readySelected() || null,actions:host.actions,externalUnresolved:nonDirectBlockers() || Boolean(directCommandView?.hasUnresolved() || directUploadView?.hasUnresolved() || directInstallView?.hasUnresolved()),cleanupConnectionId:directCleanupConnectionId()}),setBusy:value=>{host.busy=value;controls();},showResult});
directCommandView=createDirectCommandView({api,getContext:()=>({sessionId:host.session,connected:localHostMode && Boolean(host.token && host.session),busy:host.busy,actions:host.actions,authGeneration:connectionGeneration,connection:directConnectionView?.getConnectionReference(),reviewedSelection:kioskView?.getReviewedSelection(),externalUnresolved:otherDirectBlockers() || Boolean(directConnectionView?.hasUnresolved())}),setBusy:value=>{host.busy=value;controls();},showResult});
profileView=createProfileView({api,getContext:()=>({sessionId:host.session,connected:localHostMode && Boolean(host.token && host.session),busy:host.busy,actions:host.actions,capability:host.profileCapability,authGeneration:connectionGeneration,selected:readySelected() || null,externalUnresolved:Boolean(host.original || apkSetView?.hasUnresolved() || packageStopView?.hasUnresolved() || retainedView?.hasUnresolved() || filePushView?.hasUnresolved() || kioskView?.hasUnresolved() || directConnectionView?.hasUnresolved() || directCommandView?.hasUnresolved() || directUploadView?.hasUnresolved() || directInstallView?.hasUnresolved() || diagnosticsView?.hasUnresolved())}),setBusy:value=>{host.busy=value;controls();},showResult});
fleetStatusView=createFleetStatusView({api,getContext:()=>({sessionId:host.session,connected:localHostMode && Boolean(host.token && host.session),busy:host.busy,actions:host.actions,capability:host.fleetStatusCapability,authGeneration:connectionGeneration}),setBusy:value=>{host.busy=value;controls();},showResult});
diagnosticsView=createDiagnosticsView({api,getContext:()=>({sessionId:host.session,connected:localHostMode && Boolean(host.token && host.session),authGeneration:connectionGeneration,busy:host.busy,selected:readySelected() || null,capability:host.retainedCapability,artifactStaging:host.artifactStaging,stagingGeneration:host.stagingGeneration,externalUnresolved:Boolean(host.original || apkSetView?.hasUnresolved() || packageStopView?.hasUnresolved() || retainedView?.hasUnresolved() || filePushView?.hasUnresolved() || kioskView?.hasUnresolved() || directConnectionView?.hasUnresolved() || directCommandView?.hasUnresolved() || directUploadView?.hasUnresolved() || directInstallView?.hasUnresolved() || profileView?.hasUnresolved())}),getArtifact:()=>host.artifactSession===host.session && host.artifact ? {...host.artifact} : null,setBusy:value=>{host.busy=value;controls();},showResult,downloadVerified:saveOwnedArtifact});
directUploadView=createDirectUploadView({api,getContext:()=>({sessionId:host.session,connected:localHostMode && Boolean(host.token && host.session),authGeneration:connectionGeneration,busy:host.busy,actions:host.actions,connection:directConnectionView?.getConnectionReference(),artifactStaging:host.artifactStaging,stagingGeneration:host.stagingGeneration,externalUnresolved:nonDirectBlockers() || Boolean(directConnectionView?.hasUnresolved() || directCommandView?.hasUnresolved() || directInstallView?.hasUnresolved())}),getArtifact:()=>host.artifactSession===host.session && host.artifact ? {...host.artifact} : null,setBusy:value=>{host.busy=value;controls();},showResult});
directInstallView=createDirectInstallView({api,getContext:()=>({sessionId:host.session,connected:localHostMode && Boolean(host.token && host.session),authGeneration:connectionGeneration,busy:host.busy,actions:host.actions,connection:directConnectionView?.getConnectionReference(),artifactStaging:host.artifactStaging,stagingGeneration:host.stagingGeneration,externalUnresolved:nonDirectBlockers() || Boolean(directConnectionView?.hasUnresolved() || directCommandView?.hasUnresolved() || directUploadView?.hasUnresolved())}),getArtifact:()=>host.artifactSession===host.session && host.artifact ? {...host.artifact} : null,getInspection:()=>host.artifactSession===host.session && host.inspection?.sessionId===host.session && host.inspection.artifactId===host.artifact?.id && host.inspection.sha256===host.artifact.sha256 && host.inspection.sizeBytes===host.artifact.sizeBytes ? {...host.inspection} : null,setBusy:value=>{host.busy=value;controls();},showResult});
directStagingView=createDirectStagingView({api,getContext:()=>({sessionId:host.session,connected:localHostMode && Boolean(host.token && host.session),authGeneration:connectionGeneration,busy:host.busy,actions:host.actions,connection:directConnectionView?.getConnectionReference()}),setBusy:value=>{host.busy=value;controls();},showResult});
directTagsView=createDirectTagsView({api,getContext:()=>({sessionId:host.session,connected:localHostMode && Boolean(host.token && host.session),authGeneration:connectionGeneration,busy:host.busy,actions:host.actions,connection:directConnectionView?.getConnectionReference(),artifactStaging:host.artifactStaging,stagingGeneration:host.stagingGeneration}),setBusy:value=>{host.busy=value;controls();},showResult,downloadVerified:saveOwnedArtifact});
packageStopView=createPackageStopView({api,getContext:()=>({sessionId:host.session,connected:localHostMode && Boolean(host.token && host.session),authGeneration:connectionGeneration,busy:host.busy,selected:readySelected() || null,packageName:byId('package-name').value,actions:host.actions,externalUnresolved:Boolean(host.original || apkSetView?.hasUnresolved() || retainedView?.hasUnresolved() || filePushView?.hasUnresolved() || kioskView?.hasUnresolved() || directConnectionView?.hasUnresolved() || directCommandView?.hasUnresolved() || directUploadView?.hasUnresolved() || directInstallView?.hasUnresolved() || profileView?.hasUnresolved() || diagnosticsView?.hasUnresolved())}),setBusy:value=>{host.busy=value;controls();},showResult});
apkSetView=createApkSetView({api,getContext:()=>({sessionId:host.session,connected:localHostMode && Boolean(host.token && host.session),authGeneration:connectionGeneration,busy:host.busy,selected:readySelected() || null,actions:host.actions,artifactStaging:host.artifactStaging,apkSetInstall:host.apkSetInstall,stagingGeneration:host.stagingGeneration,externalUnresolved:Boolean(host.original || packageStopView?.hasUnresolved() || retainedView?.hasUnresolved() || filePushView?.hasUnresolved() || kioskView?.hasUnresolved() || directConnectionView?.hasUnresolved() || directCommandView?.hasUnresolved() || directUploadView?.hasUnresolved() || directInstallView?.hasUnresolved() || profileView?.hasUnresolved() || diagnosticsView?.hasUnresolved())}),getArtifact:()=>host.artifactSession===host.session && host.artifact ? {...host.artifact} : null,setBusy:value=>{host.busy=value;controls();},showResult});
if (!localHostMode) text('host-status','Use the local QFM host page. Never enter a host token on a public page.');
controls();
window.addEventListener('offline', () => {
  connectionGeneration++;abortDownloads();host.token='';host.session='';host.selected='';host.devices=[];
  byId('token').value='';byId('power-confirm').checked=false;byId('wifi-confirm').checked=false;
  clearStatus();byId('devices').replaceChildren();byId('device-empty').hidden=false;
  text('device-empty','Offline. Connect explicitly and refresh devices when the host is available.');
  text('host-status','Offline — disconnected');text('host-capabilities','Host capabilities unavailable offline.');text('selection','Live device state unavailable.');text('inventory-age','');controls();
});
window.addEventListener('online', controls);
