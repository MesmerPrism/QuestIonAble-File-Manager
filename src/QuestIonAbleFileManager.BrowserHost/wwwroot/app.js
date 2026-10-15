import * as usb from './usb-proof.js';

const byId = id => document.getElementById(id);
const host = { token: '', session: '', selected: '', devices: [], busy: false, original: null };
const mutations = new Set(['wifi.enable', 'wifi.connect', 'device.keep-awake', 'device.normal']);
const originalKey = 'qfm-browser-original-v1';
const localHostMode = location.protocol === 'http:' && (location.hostname === '[::1]' || /^127(?:\.\d{1,3}){3}$/.test(location.hostname));
try { host.original = JSON.parse(sessionStorage.getItem(originalKey) || 'null'); } catch { /* storage unavailable */ }
const text = (id, value) => { byId(id).textContent = value; };
const recordOriginal = value => { host.original = value; try { if (value) sessionStorage.setItem(originalKey, JSON.stringify(value)); else sessionStorage.removeItem(originalKey); } catch { /* tab memory remains authoritative */ } };
const showResult = (value, summary) => { text('result-summary', summary); text('result-json', JSON.stringify(value, null, 2)); };
const readySelected = () => host.devices.find(d => d.serial === host.selected && d.isReady);
function controls() {
  const connected = localHostMode && Boolean(host.token && host.session), ready = Boolean(readySelected());
  const mutatable = connected && !host.busy && !host.original;
  byId('refresh').disabled = !connected || host.busy;
  byId('read-status').disabled = !connected || !ready || host.busy;
  byId('apply-hold').disabled = !mutatable || !ready || !byId('power-confirm').checked;
  byId('restore-normal').disabled = byId('apply-hold').disabled;
  byId('wifi-connect').disabled = !mutatable || !byId('wifi-confirm').checked || !byId('wifi-host').value.trim();
  byId('wifi-enable').disabled = !mutatable || !ready || Boolean(readySelected()?.isWifiConnection) || !byId('wifi-confirm').checked;
  byId('connect').disabled = !localHostMode || host.busy;
  byId('token').disabled = !localHostMode;
  byId('local-host-link').hidden = localHostMode;
  byId('disconnect-host').hidden = !connected;
  byId('reconcile').hidden = !host.original;
  byId('reconcile').disabled = !connected || host.busy || host.original?.sessionId !== host.session;
}
function clearStatus() { for (const id of ['power-value','display-value','proximity-value','hold-value']) text(id, 'Unknown'); text('status-age',''); }
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
  if (!response.ok) { const error = new Error(value.error || `Request rejected (${response.status})`); error.status = response.status; error.dispatched = value.dispatched; throw error; }
  return value;
}
function consume(value, action) {
  const receipt = value.result?.mutationReceipt || value.mutation;
  const state = receipt?.stage || value.state;
  showResult(value, `${action}: ${state}${receipt?.observedState ? ` — ${receipt.observedState}` : ''}`);
  if (action === 'devices' && value.result?.devices) renderDevices(value.result.devices);
  if (value.result?.command?.serial === host.selected) applyStatus(value);
  const receiptConfirmed = receipt?.stage === 'confirmed';
  const connection = value.result?.wifiAdbConnectionResult;
  const endpointReady = action === 'wifi.connect' && connection?.device?.isReady === true && connection.endpoint === host.original?.target && connection.device.serial === host.original?.target;
  if (host.original && value.requestId === host.original.requestId && ((value.state === 'completed' && (!mutations.has(action) || receiptConfirmed || endpointReady)) || value.state === 'failed')) recordOriginal(null);
}
async function execute(action, fields = {}) {
  if (!host.session || host.busy || (mutations.has(action) && host.original)) return;
  const requestId = crypto.randomUUID(); host.busy = true;
  if (mutations.has(action)) recordOriginal({ requestId, sessionId:host.session, action, target:fields.serial || fields.host });
  controls(); showResult({requestId,action,state:'sent'}, `${action}: sent. Waiting for owner readback.`);
  try { consume(await api('/browser/v1/execute', {method:'POST',body:JSON.stringify({requestId,action,...fields})}), action); }
  catch (error) {
    if (error.dispatched === false && host.original?.requestId === requestId) recordOriginal(null);
    // A lost response may follow a real effect. Preserve the original ID; never resubmit.
    const unknown = mutations.has(action) && error.dispatched !== false;
    showResult({requestId,action,state:unknown?'outcomeUnknown':'rejected',error:error.message}, unknown ? 'Outcome unknown. Read the original result; do not repeat this change.' : `Request not dispatched: ${error.message}`);
  } finally { host.busy = false; controls(); }
}
byId('connect-form').addEventListener('submit', async event => {
  event.preventDefault(); if (!localHostMode) {byId('token').value='';return;} host.token = byId('token').value; byId('token').value=''; host.busy=true; controls();
  try { const session=await api('/browser/v1/session'); host.session=session.sessionId; text('host-status','Connected');
    if (host.original) showResult(host.original, host.original.sessionId === host.session ? 'An original change needs readback. Use Read original result.' : 'Host session changed after an unresolved change. Inspect current state using the owner route; changes stay blocked in this tab.');
  } catch(error) { host.token='';host.session='';text('host-status',error.message); } finally {host.busy=false;controls();}
});
byId('disconnect-host').addEventListener('click',()=>{host.token='';host.session='';host.selected='';host.devices=[];clearStatus();byId('devices').replaceChildren();byId('device-empty').hidden=false;text('device-empty','Connect to list authorized USB and WiFi devices.');text('host-status','Not connected');text('selection','Choose a headset to read its current state.');text('inventory-age','');controls();});
byId('refresh').addEventListener('click',()=>execute('devices'));
byId('read-status').addEventListener('click',()=>execute('device.status',{serial:host.selected}));
byId('power-confirm').addEventListener('change',controls);byId('wifi-confirm').addEventListener('change',controls);
for(const id of ['wifi-host','wifi-port'])byId(id).addEventListener('input',()=>{byId('wifi-confirm').checked=false;controls();});
byId('duration').addEventListener('change',()=>{byId('power-confirm').checked=false;controls();});
byId('apply-hold').addEventListener('click',()=>{if(!byId('apply-hold').disabled)execute('device.keep-awake',{serial:host.selected,durationMilliseconds:Number(byId('duration').value),confirmed:true});byId('power-confirm').checked=false;controls();});
byId('restore-normal').addEventListener('click',()=>{if(!byId('restore-normal').disabled)execute('device.normal',{serial:host.selected,confirmed:true});byId('power-confirm').checked=false;controls();});
byId('wifi-form').addEventListener('submit',event=>{event.preventDefault();if(!byId('wifi-connect').disabled){const address=byId('wifi-host').value.trim(),port=Number(byId('wifi-port').value);execute('wifi.connect',{host:address,port,serial:`${address}:${port}`,confirmed:true});byId('wifi-confirm').checked=false;controls();}});
byId('wifi-enable').addEventListener('click',()=>{if(!byId('wifi-enable').disabled)execute('wifi.enable',{serial:host.selected,port:Number(byId('wifi-port').value),confirmed:true});byId('wifi-confirm').checked=false;controls();});
byId('reconcile').addEventListener('click',async()=>{if(!host.original||host.busy)return;host.busy=true;controls();try{consume(await api(`/browser/v1/requests/${host.original.requestId}`),host.original.action);}catch(error){showResult(host.original,`Original result unavailable: ${error.message}. Do not replay.`);}finally{host.busy=false;controls();}});
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
if (!localHostMode) text('host-status','Use the local QFM host page. Never enter a host token on a public page.');
controls();
