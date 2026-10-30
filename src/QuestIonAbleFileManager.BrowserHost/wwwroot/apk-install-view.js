const storageKey = 'qfm-browser-apk-install-original-v1', action = 'apk.install';
const contractVersion = 'questionable.file_manager.browser_apk_install.v1';
const optionNames = ['replaceExisting','allowDowngrade','grantRuntimePermissions','allowTestPackages'];
const optionIds = ['replace','downgrade','grant','test'];
const closed = (value,required,optional=[]) => value && typeof value === 'object' && !Array.isArray(value) && required.every(key => Object.hasOwn(value,key)) && Object.keys(value).every(key => required.includes(key) || optional.includes(key));
const copy = value => value == null ? null : JSON.parse(JSON.stringify(value));
const bytes = value => new TextEncoder().encode(value).byteLength;
const guid = value => typeof value === 'string' && /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/.test(value) && value !== '00000000-0000-0000-0000-000000000000';
const serial = value => typeof value === 'string' && /^[A-Za-z0-9._:-]{1,256}$/.test(value);
const digest = value => typeof value === 'string' && /^[0-9a-f]{64}$/.test(value);
function source(value) {
  if (!closed(value,['id','fileName','sizeBytes','sha256']) || typeof value.id !== 'string' || !/^[0-9a-f]{32}$/.test(value.id) || value.id === '0'.repeat(32) || typeof value.fileName !== 'string' || !/^[A-Za-z0-9][A-Za-z0-9._ -]{0,127}$/.test(value.fileName) || /[. ]$/.test(value.fileName) || value.fileName.includes('..') || !/\.apk$/i.test(value.fileName) || /^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])$/.test(value.fileName.split('.')[0].trimEnd().toUpperCase()) || !Number.isSafeInteger(value.sizeBytes) || value.sizeBytes <= 0 || value.sizeBytes > 1073741824 || !digest(value.sha256)) return null;
  return {id:value.id,fileName:value.fileName,sizeBytes:value.sizeBytes,sha256:value.sha256};
}
const options = value => closed(value,optionNames) && optionNames.every(key => typeof value[key] === 'boolean') ? Object.fromEntries(optionNames.map(key => [key,value[key]])) : null;
const sameSource = (left,right) => Boolean(source(left) && ['id','fileName','sizeBytes','sha256'].every(key => left[key] === right[key]));
function reference(value) {
  if (!closed(value,['requestId','action','sessionId','serial','source','options']) || !guid(value.requestId) || value.action !== action || typeof value.sessionId !== 'string' || !/^[A-Za-z0-9_-]{1,128}$/.test(value.sessionId) || !serial(value.serial)) return null;
  const artifact = source(value.source), flags = options(value.options);
  const result = artifact && flags ? {requestId:value.requestId,action,sessionId:value.sessionId,serial:value.serial,source:artifact,options:flags} : null;
  return result && bytes(JSON.stringify(result)) <= 4096 ? result : null;
}
function savedReference(raw) {
  if(bytes(raw)>4096)return null;
  const value=JSON.parse(raw),tokens=raw.match(/"(?:\\.|[^"\\])*"|[{}\[\]:,]/g)||[],keys=new Set();
  // Every key in this closed reference shape is unique, including nested keys.
  // Read JSON string tokens so escaped duplicate names cannot bypass custody.
  for(let index=0;index<tokens.length-1;index++)if(tokens[index][0]==='"'&&tokens[index+1]===':'){const name=JSON.parse(tokens[index]);if(keys.has(name))return null;keys.add(name);}
  return reference(value);
}
function identity(value) {
  if (!closed(value,['packageName','versionCode','versionName','signerSha256','splitName']) || typeof value.packageName !== 'string' || !/^[A-Za-z0-9_]+(?:\.[A-Za-z0-9_]+)+$/.test(value.packageName) || typeof value.versionCode !== 'string' || !/^[1-9][0-9]{0,18}$/.test(value.versionCode) || BigInt(value.versionCode) > 9223372036854775807n || !(value.versionName === null || typeof value.versionName === 'string' && value.versionName.length <= 512) || !digest(value.signerSha256) || value.splitName !== null) throw Error('invalid identity');
  return copy(value);
}
const sameIdentity = (left,right) => ['packageName','versionCode','versionName','signerSha256','splitName'].every(key => left[key] === right[key]);
function timestamp(value) {
  const fields = typeof value === 'string' && value.match(/^(\d{4})-(\d{2})-(\d{2})T(\d{2}):(\d{2}):(\d{2})(?:\.(\d{1,7}))?(Z|[+-](\d{2}):(\d{2}))$/);
  if (!fields) return null;
  const [,year,month,day,hour,minute,second,fraction='',offset,offsetHour='0',offsetMinute='0'] = fields;
  const date = new Date(0); date.setUTCFullYear(Number(year),Number(month)-1,Number(day));
  if (Number(year) < 1 || date.getUTCFullYear() !== Number(year) || date.getUTCMonth()+1 !== Number(month) || date.getUTCDate() !== Number(day) || Number(hour)>23 || Number(minute)>59 || Number(second)>59 || Number(offsetHour)>14 || Number(offsetMinute)>59 || Number(offsetHour)===14 && Number(offsetMinute)!==0) return null;
  const wholeSecond = Date.parse(`${year}-${month}-${day}T${hour}:${minute}:${second}${offset}`);
  if (!Number.isFinite(wholeSecond)) return null;
  const ticks = BigInt(wholeSecond)*10000n+BigInt(fraction.padEnd(7,'0'));
  return ticks <= -621355968000000000n || ticks > 2534023007999999999n ? null : ticks;
}
function receipt(value,original) {
  if (!closed(value,['operationId','commandKind','target','desiredState','stage','observedState','headsetReadback','transitions'],['isTerminal']) || typeof value.operationId !== 'string' || !/^pc-[0-9a-f]{32}$/.test(value.operationId) || value.operationId === 'pc-'+'0'.repeat(32) || value.commandKind !== 'installApk' || value.target !== original.serial || value.desiredState !== 'apk-install-exact-base' || value.stage !== 'confirmed' || value.observedState !== 'exact-installed-base-bytes-and-identity' || value.headsetReadback !== true || Object.hasOwn(value,'isTerminal') && value.isTerminal !== true || !Array.isArray(value.transitions) || value.transitions.length !== 3) throw Error('invalid receipt');
  const times = value.transitions.map(transition => timestamp(transition?.at));
  if (value.transitions.some((transition,index) => !closed(transition,['stage','at','message']) || transition.stage !== ['sent','pending','confirmed'][index] || transition.message !== 'apk-install-'+transition.stage || times[index] === null || index && times[index] < times[index-1])) throw Error('invalid transitions');
  return copy(value);
}
function checked(value,original) {
  const empty = ['result','artifactInspection','artifactTransfer','artifactRead','artifactPush','kiosk','kioskDirectConnection','profile','kioskDirectCommand','fleet','kioskDirectTags','kioskDirectStaging','kioskDirectUpload','kioskDirectInstall','packageStop','apkSet'];
  if (!closed(value,['requestId','action','state','apkInstall'],[...empty,'error','mutation']) || value.requestId !== original.requestId || value.action !== action || !empty.every(key => value[key] == null)) throw Error('invalid outcome');
  const proof = value.apkInstall;
  if (!closed(proof,['contractVersion','serial','source','options','executionStarted','installerCommandSucceeded','exactInstalledBaseBytes','artifactIdentity','installed']) || proof.contractVersion !== contractVersion || proof.serial !== original.serial || !sameSource(proof.source,original.source) || !options(proof.options) || !optionNames.every(key => proof.options[key] === original.options[key]) || typeof proof.executionStarted !== 'boolean') throw Error('invalid proof');
  let mutation = null;
  if (value.state === 'completed') {
    if (value.error != null || proof.executionStarted !== true || proof.installerCommandSucceeded !== true || proof.exactInstalledBaseBytes !== true) throw Error('unconfirmed completion');
    const expected = identity(proof.artifactIdentity), installed = proof.installed;
    if (!closed(installed,['serial','identity','baseApkSha256','baseApkSizeBytes','verificationMethod','verificationFallbackReason']) || installed.serial !== original.serial || !sameIdentity(identity(installed.identity),expected) || installed.baseApkSha256 !== original.source.sha256 || installed.baseApkSizeBytes !== original.source.sizeBytes || !(installed.verificationMethod === 'same-opened-handle-device-sha256' && installed.verificationFallbackReason === null || installed.verificationMethod === 'host-streamed-sha256' && installed.verificationFallbackReason === 'remote-handle-digest-capability-unsupported')) throw Error('invalid installed identity');
    mutation = receipt(value.mutation,original);
  } else {
    if (proof.installerCommandSucceeded !== null || proof.exactInstalledBaseBytes !== null || proof.artifactIdentity !== null || proof.installed !== null || value.mutation != null) throw Error('invalid unsettled proof');
    if (value.state === 'running') {if (value.error != null) throw Error('invalid running');}
    else if (value.state === 'outcomeUnknown') {if (!proof.executionStarted || !['apk_install_outcome_unknown','apk_install_binding_mismatch'].includes(value.error)) throw Error('invalid unknown');}
    else throw Error('invalid state');
  }
  return {requestId:original.requestId,action,state:value.state,apkInstall:copy(proof),...(mutation ? {mutation} : {}),...(value.error ? {error:value.error} : {})};
}

// Original custody is independent of the current form. Getters are pure.
export function createApkInstallView({api,getContext,getArtifact,setBusy,showResult}) {
  const element = id => document.getElementById('apk-install-'+id), text = (id,value) => {element(id).textContent=value;};
  let original=null,damagedOriginalCustody=false,inFlight=false,authStamp='',scopeStamp='',authEpoch=0,scopeEpoch=0,message='No single APK install requested.';
  try {
    const saved=sessionStorage.getItem(storageKey);
    if(saved!==null){try{original=savedReference(saved);}catch{/* retain saved bytes */}damagedOriginalCustody=!original;message=original?'Saved original restored. Read its result explicitly; no install was replayed.':'Saved original is invalid. Preserve its custody for the owner; mutations stay blocked.';}
  }catch{damagedOriginalCustody=true;message='Original storage is unavailable. Preserve custody; mutations stay blocked.';}
  const hasUnresolved=()=>Boolean(original || damagedOriginalCustody),getOriginalReference=()=>copy(original);
  const live=context=>Boolean(context.connected && context.sessionId);
  const flags=()=>Object.fromEntries(optionNames.map((key,index)=>[key,element(optionIds[index]).checked]));
  function update(){
    const context=getContext(),artifact=source(getArtifact()),auth=JSON.stringify([context.sessionId,Boolean(context.connected),context.authGeneration]);
    if(auth!==authStamp){authStamp=auth;authEpoch++;}
    const scope=JSON.stringify([auth,context.selected?.serial,context.selected?.isReady,artifact,flags(),context.stagingGeneration,Boolean(context.externalUnresolved),context.actions?.has?.(action),context.artifactStaging]);
    if(scope!==scopeStamp){scopeStamp=scope;scopeEpoch++;element('confirm').checked=false;}
    const idle=live(context)&&!context.busy&&!inFlight,supported=context.artifactStaging?.supported===true&&context.actions?.has?.(action);
    const available=idle&&supported&&context.selected?.isReady===true&&serial(context.selected.serial)&&Boolean(artifact)&&!hasUnresolved()&&!context.externalUnresolved;
    for(const id of optionIds)element(id).disabled=!idle;
    element('confirm').disabled=!available;element('send').disabled=!available||!element('confirm').checked;
    element('original').disabled=!idle||!original||context.sessionId!==original.sessionId;element('save').disabled=!original;
    text('source',artifact?.fileName||'No current staged APK');text('size',artifact?artifact.sizeBytes+' bytes':'Unknown');text('sha',artifact?.sha256||'Unknown');text('target',context.selected?.isReady===true?context.selected.serial:'No ready target');
    text('capability',!live(context)?'Connect to check single APK installation support.':supported?'Review the current staged APK, exact target and all four options before approval.':'Single APK installation is unavailable on this host. Use the configured Windows companion or typed QFM CLI.');
    text('state',message+(original&&live(context)&&original.sessionId!==context.sessionId?' Host session changed; this host cannot recover the saved original.':'')+(hasUnresolved()?' Original custody blocks mutations. No automatic retry.':'')+(context.externalUnresolved?' Another device original is unresolved.':''));
    element('reference').hidden=!original;text('request',original?.requestId||'');text('original-target',original?.serial||'');text('original-source',original?`${original.source.fileName} — ${original.source.sizeBytes} bytes — ${original.source.sha256}`:'');text('original-options',original?optionNames.map(name=>`${name}: ${original.options[name]}`).join(', '):'');
  }
  function acknowledge(value,saved){
    const safe=checked(value,saved);
    if(safe.state!=='completed'){message='Original install remains running or unknown. Read only its original result explicitly.';showResult(safe,message);return;}
    try{sessionStorage.removeItem(storageKey);if(sessionStorage.getItem(storageKey)!==null)throw Error('remove not acknowledged');original=null;}
    catch{damagedOriginalCustody=true;message='Bound result received, but original custody could not be cleared. Preserve it for the owner.';showResult(safe,message);return;}
    message=`Installer succeeded and exact installed BASE bytes and identity were verified for ${saved.serial}. Launch, app readiness, effective permissions and rollback are not established.`;showResult(safe,message);
  }
  async function send(){
    update();if(element('send').disabled)return;
    const context=getContext(),saved=reference({requestId:crypto.randomUUID(),action,sessionId:context.sessionId,serial:context.selected.serial,source:source(getArtifact()),options:flags()});if(!saved)return;
    original=saved;
    try{const serialized=JSON.stringify(saved);sessionStorage.setItem(storageKey,serialized);if(sessionStorage.getItem(storageKey)!==serialized)throw Error('write not acknowledged');}
    catch{damagedOriginalCustody=true;message='Install was not sent: original reference storage could not be acknowledged.';update();return;}
    element('confirm').checked=false;const sentAuth=authEpoch,sentScope=scopeEpoch;
    inFlight=true;message='Install requested. Waiting for actual settlement; elapsed time does not cancel or authorize replay.';setBusy(true);update();
    try{const value=await api('/browser/v1/execute',{method:'POST',body:JSON.stringify({requestId:saved.requestId,action,serial:saved.serial,source:copy(saved.source),options:{...saved.options},confirmed:true})});update();if(sentAuth!==authEpoch||sentScope!==scopeEpoch||!live(getContext())){message='Reply arrived after authentication or review changed. Preserve the original and explicitly read its result.';return;}acknowledge(value,saved);}
    catch{message='Install result is unavailable or invalid. Preserve the original; no automatic replay.';}
    finally{inFlight=false;setBusy(false);update();}
  }
  async function readOriginal(){
    update();if(element('original').disabled)return;
    const saved=copy(original),sentAuth=authEpoch,sentScope=scopeEpoch;inFlight=true;setBusy(true);update();
    try{const value=await api('/browser/v1/requests/'+saved.requestId);update();if(sentAuth!==authEpoch||sentScope!==scopeEpoch||!live(getContext())||getContext().sessionId!==saved.sessionId){message='Original reply arrived after authentication or review changed. Preserve custody and explicitly read it again.';return;}acknowledge(value,saved);}
    catch{message='Original result is unavailable or invalid. Its outcome stays unknown; do not repeat the install.';}
    finally{inFlight=false;setBusy(false);update();}
  }
  function stagingCleared(){element('confirm').checked=false;update();}
  for(const id of optionIds)element(id).addEventListener('change',update);
  element('confirm').addEventListener('change',update);element('send').addEventListener('click',send);element('original').addEventListener('click',readOriginal);
  element('save').addEventListener('click',()=>{if(!original)return;const url=URL.createObjectURL(new Blob([JSON.stringify(getOriginalReference(),null,2)],{type:'application/json'})),link=document.createElement('a');link.href=url;link.download='qfm-apk-install-original-reference.json';link.click();setTimeout(()=>URL.revokeObjectURL(url),1000);});
  update();return{update,hasUnresolved,getOriginalReference,stagingCleared};
}
