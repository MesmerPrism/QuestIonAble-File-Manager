const action='kiosk.direct.staging-upload',storageKey='qfm-browser-direct-upload-v1',maximumBytes=1073741824;
const clean=v=>typeof v==='string'&&!/[\u0000-\u001f\u007f-\u009f]/u.test(v);
const guid=v=>clean(v)&&v.length===36&&/^[a-f0-9]{8}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{12}$/.test(v)&&v.replaceAll('-','')!=='0'.repeat(32);
const opaque=v=>clean(v)&&v.length===32&&/^[a-f0-9]{32}$/.test(v)&&v!=='0'.repeat(32);
const hash=v=>clean(v)&&v.length===64&&/^[a-f0-9]{64}$/.test(v);
const session=v=>clean(v)&&v.length>0&&v.length<=128&&!/[^A-Za-z0-9_-]/.test(v);
const serial=v=>clean(v)&&v.length>0&&v.length<=256&&!/[^A-Za-z0-9._-]/.test(v);
const name=v=>clean(v)&&v.length>0&&v.length<=160&&v.trim()===v&&/^[A-Za-z0-9][A-Za-z0-9._ ()+@-]*$/.test(v);
const size=v=>Number.isSafeInteger(v)&&v>=1&&v<=maximumBytes;
function source(v){return v&&session(v.sessionId)&&guid(v.connectionId)&&guid(v.connectRequestId)&&v.usbOperationId==='usb_'+v.connectRequestId.replaceAll('-','')&&serial(v.serial)&&['stable','labs'].includes(v.productChannel)&&clean(v.expiresAtUtc)&&v.expiresAtUtc.length<=64&&Number.isFinite(Date.parse(v.expiresAtUtc))?{sessionId:v.sessionId,connectionId:v.connectionId,connectRequestId:v.connectRequestId,usbOperationId:v.usbOperationId,serial:v.serial,productChannel:v.productChannel,expiresAtUtc:v.expiresAtUtc}:null;}
function artifact(v){return v&&opaque(v.id)&&hash(v.sha256)&&size(v.sizeBytes)&&clean(v.fileName)&&v.fileName.length>0&&v.fileName.length<=160&&!/[\\/:]/.test(v.fileName)?{id:v.id,fileName:v.fileName,sizeBytes:v.sizeBytes,sha256:v.sha256}:null;}
function limits(v){return v?.supported===true&&Number.isSafeInteger(v.maximumFileBytes)&&v.maximumFileBytes>0&&v.maximumFileBytes<=maximumBytes&&Number.isSafeInteger(v.maximumTotalBytes)&&v.maximumTotalBytes>=v.maximumFileBytes&&Number.isSafeInteger(v.maximumFiles)&&v.maximumFiles>0&&Number.isSafeInteger(v.transferTimeoutSeconds)&&v.transferTimeoutSeconds>0&&v.transferTimeoutSeconds<=300?{maximumFileBytes:v.maximumFileBytes,maximumTotalBytes:v.maximumTotalBytes,maximumFiles:v.maximumFiles,transferTimeoutSeconds:v.transferTimeoutSeconds}:null;}
function reference(v){return v&&guid(v.requestId)&&v.action===action&&session(v.sessionId)&&guid(v.sourceConnectionId)&&guid(v.connectRequestId)&&v.usbOperationId==='usb_'+v.connectRequestId.replaceAll('-','')&&serial(v.serial)&&['stable','labs'].includes(v.channel)&&opaque(v.artifactId)&&hash(v.sha256)&&size(v.sizeBytes)&&name(v.stagedName)?{requestId:v.requestId,action,sessionId:v.sessionId,sourceConnectionId:v.sourceConnectionId,connectRequestId:v.connectRequestId,usbOperationId:v.usbOperationId,serial:v.serial,channel:v.channel,artifactId:v.artifactId,sha256:v.sha256,sizeBytes:v.sizeBytes,stagedName:v.stagedName}:null;}
const predispatch=new Set(['busy','session_capacity_reached','session_changed','unauthorized','origin_or_host_rejected','action_unsupported','confirmation_required','fields_invalid','request_invalid','content_type_invalid','body_size_invalid','request_body_timeout','request_id_invalid','field_invalid','duplicate_field','direct_unavailable','direct_connection_not_found','direct_connection_expired','direct_owner_stopping','artifact_staging_unavailable','artifact_id_invalid','artifact_hash_invalid','artifact_size_invalid','direct_upload_name_invalid']);
function project(v,ref){
 if(v?.requestId!==ref.requestId||v.action!==action||v.result!==null||v.mutation!==null)throw new Error('upload_binding_invalid');
 if(v.state==='running'&&v.kioskDirectUpload==null)return null;
 const d=v.kioskDirectUpload,r=d?.coreReceipt;
 if(!d||d.connectionId!==ref.sourceConnectionId||d.connectRequestId!==ref.connectRequestId||d.usbOperationId!==ref.usbOperationId||d.serial!==ref.serial||d.productChannel!==ref.channel||d.artifactId!==ref.artifactId||d.sha256!==ref.sha256||d.sizeBytes!==ref.sizeBytes||d.stagedName!==ref.stagedName)throw new Error('upload_binding_invalid');
 const confirmed=v.state==='completed'&&d.disposition==='confirmed'&&d.verification==='signed-upload-size-sha256'&&r&&clean(r.actionId)&&/^action_[a-f0-9]{32}$/.test(r.actionId)&&r.action==='uploadStaging'&&r.stage==='confirmed';
 const notDispatched=v.state==='failed'&&d.disposition==='notDispatched'&&d.verification==='none'&&r===null;
 const unknown=v.state==='outcomeUnknown'&&d.disposition==='outcomeUnknown'&&d.verification==='none'&&r===null;
 if(!confirmed&&!notDispatched&&!unknown)throw new Error('upload_proof_invalid');
 return {connectionId:ref.sourceConnectionId,connectRequestId:ref.connectRequestId,usbOperationId:ref.usbOperationId,serial:ref.serial,productChannel:ref.channel,artifactId:ref.artifactId,sha256:ref.sha256,sizeBytes:ref.sizeBytes,stagedName:ref.stagedName,disposition:d.disposition,verification:d.verification,coreReceipt:confirmed?{actionId:r.actionId,action:'uploadStaging',stage:'confirmed'}:null};
}

// Only this allowlisted original binding survives reload. Root composition calls
// the two memory-only predicates without recursively reading this card's context.
export function createDirectUploadView({api,getContext,getArtifact,setBusy,showResult}){
 const element=id=>document.getElementById('direct-upload-'+id),text=(id,v)=>{element(id).textContent=v;};
 let original=null,damaged=false,storageAvailable=true,writing=false,probing=false,facts=null;
 let authStamp='',authEpoch=0,scopeStamp='',scopeEpoch=0,artifactStamp='',message='No Direct staging upload requested.';
 const live=c=>c.connected===true&&session(c.sessionId);
 const hasUnresolved=()=>Boolean(original||damaged||!storageAvailable);
 const getCleanupReference=()=>{const ref=!damaged&&reference(original);return ref?{sessionId:ref.sessionId,connectionId:ref.sourceConnectionId}:null;};
 function persist(){try{if(original)sessionStorage.setItem(storageKey,JSON.stringify(reference(original)));else sessionStorage.removeItem(storageKey);return true;}catch{storageAvailable=false;return false;}}
 try{const saved=sessionStorage.getItem(storageKey);if(saved!==null){try{original=saved.length<=4096?reference(JSON.parse(saved)):null;}catch{/* keep a blocked reference */}damaged=!original;if(damaged)sessionStorage.setItem(storageKey,'{}');else persist();message=original?'Original upload reference restored. Read its original result explicitly; no upload was replayed.':'Saved upload reference is damaged. New uploads remain blocked; use the typed owner route to inspect the original scope.';}}catch{storageAvailable=false;}
 function update(){const c=getContext(),s=source(c.connection),a=artifact(getArtifact()),cap=limits(c.artifactStaging),nextAuth=JSON.stringify([c.connected,c.sessionId,c.authGeneration]);
  if(nextAuth!==authStamp){authStamp=nextAuth;authEpoch++;facts=null;}
  const nextArtifact=JSON.stringify([a,c.stagingGeneration]);if(nextArtifact!==artifactStamp){artifactStamp=nextArtifact;element('name').value=a&&name(a.fileName)?a.fileName:'';}
  const current=Boolean(s&&s.sessionId===c.sessionId&&Date.parse(s.expiresAtUtc)>Date.now());
  const nextScope=JSON.stringify([nextAuth,s,current,a,c.stagingGeneration,cap,c.actions?.has?.(action)===true,Boolean(c.externalUnresolved),element('name').value]);
  if(nextScope!==scopeStamp){scopeStamp=nextScope;scopeEpoch++;element('confirm').checked=false;}
  if(writing||probing||c.busy)element('confirm').checked=false;
  const busy=Boolean(writing||probing||c.busy),ready=Boolean(live(c)&&current&&cap&&a&&a.sizeBytes<=cap.maximumFileBytes&&c.actions?.has?.(action)===true&&!c.externalUnresolved&&!hasUnresolved()&&!busy);
  element('name').disabled=busy||hasUnresolved()||!live(c);element('confirm').disabled=!ready||!name(element('name').value);element('send').disabled=element('confirm').disabled||!element('confirm').checked;
  element('original').disabled=!original||!live(c)||original.sessionId!==c.sessionId||probing;
  text('capability',!live(c)?'Connect to review Direct upload capabilities.':!cap?'Protected artifact staging is unavailable; use the supported Windows companion or typed CLI.':c.actions?.has?.(action)!==true?'Direct staging upload is unavailable on this host. Use the typed Kiosk Direct CLI.':`Staging limit: ${cap.maximumFileBytes.toLocaleString()} bytes per file. Choose the source in Local APK.`);
  text('source',a?a.fileName:'No staged file. Select a file in Local APK.');text('artifact',a?.id||'');text('size',a?`${a.sizeBytes.toLocaleString()} bytes`:'');text('sha',a?.sha256||'');text('target',s?`${s.serial} · ${s.productChannel} · ${s.connectionId}${current?'':' — expired or unavailable'}`:'No adopted connection');
  const shown=facts?.ref||original;element('facts').hidden=!shown;
  if(shown){text('request',shown.requestId);text('original-source',`${shown.serial} · ${shown.channel} · ${shown.sourceConnectionId} · artifact ${shown.artifactId} · ${shown.sizeBytes} bytes · ${shown.sha256}`);text('staged-name',shown.stagedName);text('verification',facts?.value.disposition==='confirmed'?'Historical signed size and SHA-256 acknowledgement':facts?.value.disposition==='notDispatched'?'Not dispatched':'Unresolved original upload');}
  text('state',message+(original?' Original upload remains unresolved. Do not replay it; read its exact original result.':'')+(original&&original.sessionId!==c.sessionId?' The host session changed; the cached original is unavailable here. A missing record cannot prove no effect.':'')+(c.externalUnresolved?' Another operation is unresolved; new uploads are blocked.':'')+(!storageAvailable?' Session storage is unavailable; new uploads are blocked.':'')+(damaged?' Saved reference is damaged; use the typed owner route.':''));
 }
 function acknowledge(v,ref){const d=project(v,ref);if(!d||d.disposition==='outcomeUnknown'){message='Upload outcome remains unknown. No matching terminal transfer acknowledgement is available.';return;}
  if(original!==ref)return;original=null;if(!persist()){original=ref;message='A terminal acknowledgement arrived, but the original reference could not be cleared safely.';return;}
  facts={ref,value:d};message=d.disposition==='confirmed'?'Provider upload acknowledged the exact name, size and SHA-256. This is a historical transfer agreement, not an installation or ongoing file check.':'Upload was not dispatched. Review the source and destination before approving a separate new request.';
  showResult({requestId:ref.requestId,action,state:v.state,result:null,mutation:null,kioskDirectUpload:d},message);
 }
 async function send(){update();if(element('send').disabled)return;const c=getContext(),s=source(c.connection),a=artifact(getArtifact()),ref=reference({requestId:crypto.randomUUID(),action,sessionId:c.sessionId,sourceConnectionId:s.connectionId,connectRequestId:s.connectRequestId,usbOperationId:s.usbOperationId,serial:s.serial,channel:s.productChannel,artifactId:a.id,sha256:a.sha256,sizeBytes:a.sizeBytes,stagedName:element('name').value});
  if(!ref)return;original=ref;if(!persist()){original=null;message='Upload not sent: its original reference could not be saved.';update();return;}
  const sentAuth=authEpoch,sentScope=scopeEpoch;writing=true;facts=null;element('confirm').checked=false;message='Sending this upload once; waiting for the signed transfer acknowledgement.';setBusy(true);update();
  try{const v=await api('/browser/v1/execute',{method:'POST',body:JSON.stringify({requestId:ref.requestId,action,connectionId:ref.sourceConnectionId,artifactId:ref.artifactId,sha256:ref.sha256,sizeBytes:ref.sizeBytes,name:ref.stagedName,confirmed:true})});update();if(original!==ref)return;
   if(sentAuth!==authEpoch||sentScope!==scopeEpoch||!live(getContext())){message='Upload reply arrived after authentication or review scope changed. Preserve the original and read its result explicitly.';return;}acknowledge(v,ref);
  }catch(error){update();if(original!==ref)return;if(sentAuth===authEpoch&&sentScope===scopeEpoch&&live(getContext())&&error?.dispatched===false&&predispatch.has(error.message)){original=null;if(!persist())original=ref;message=original?'Predispatch rejection arrived, but the original reference could not be cleared safely.':'Upload rejected before dispatch. Review the scope and approve a separate new request explicitly.';}else message='Upload reply unavailable or unbound. The original outcome remains unknown; no automatic retry.';}
  finally{writing=false;setBusy(false);update();}
 }
 async function originalResult(){update();if(element('original').disabled)return;const ref=original,sentAuth=authEpoch;probing=true;update();try{const v=await api('/browser/v1/requests/'+ref.requestId);update();if(original!==ref)return;if(sentAuth!==authEpoch||!live(getContext())||getContext().sessionId!==ref.sessionId){message='Original-result reply arrived after authentication changed; the unresolved reference is preserved.';return;}acknowledge(v,ref);}catch{if(original===ref)message='Original result unavailable or unbound. Missing records and connection cleanup do not prove no upload effect.';}finally{probing=false;setBusy(Boolean(getContext().busy));update();}}
 element('name').addEventListener('input',update);element('confirm').addEventListener('change',update);element('send').addEventListener('click',send);element('original').addEventListener('click',originalResult);update();return {update,hasUnresolved,getCleanupReference};
}
