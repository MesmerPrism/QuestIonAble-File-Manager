const contractVersion='questionable.file_manager.local_api.v3', command='apk.diagnose', storageKey='qfm-browser-diagnostics-original-v1';
const terminal=new Set(['completed','failed','cancelled','expired']);
const stages=new Set(['preparing','preflighted','running','cancellationRequested',...terminal,'outcomeUnknownRecoveryRequired','cleanupDebt']);
const errors=new Set(['diagnostic_preparation_failed','diagnostic_execution_failed','diagnostic_output_unavailable','diagnostic_cleanup_pending','diagnostic_cleanup_debt','diagnostic_recovery_required','journal_persist_failed','operation_expired','operation_cancelled']);
const predispatch=new Set(['body_size_invalid','content_type_invalid','content_encoding_invalid','request_body_timeout','request_invalid','fields_invalid','duplicate_field','field_invalid','contract_unsupported','command_not_allowed','artifact_id_invalid','artifact_hash_invalid','artifact_size_invalid','serial_invalid','expiry_invalid','operation_binding_invalid','diagnostic_output_binding_invalid','artifact_filename_invalid']);
const caps={maximumProcessLogs:8,recentLogLinesPerCapture:400,maximumPayloadFiles:14,maximumPayloadFileBytes:262144,maximumPayloadBytes:3670016,maximumArchiveBytes:4194304,reservedOutputBytes:8388608,reservedOutputFiles:15,maximumRequiredStandardOutputBytes:262144,maximumRequiredStandardErrorBytes:262144,maximumOptionalStandardOutputBytes:262144,maximumOptionalStandardErrorBytes:262144,maximumInputArtifactBytes:1073741824,maximumBinaryFramingBytes:256,maximumProcessTimeoutSeconds:300};
const clean=value=>typeof value==='string'&&!/[\u0000-\u001f\u007f-\u009f]/u.test(value);
const hash=value=>clean(value)&&/^[a-f0-9]{64}$/.test(value);
const guid=value=>clean(value)&&/^[a-f0-9]{8}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{12}$/.test(value)&&value.replaceAll('-','')!=='0'.repeat(32);
const opaque=value=>clean(value)&&/^[a-f0-9]{32}$/.test(value)&&value!=='0'.repeat(32);
const ownerId=value=>clean(value)&&value.startsWith('api-')&&opaque(value.slice(4));
const outputId=value=>clean(value)&&value.startsWith('diag-')&&opaque(value.slice(5));
const serial=value=>clean(value)&&value.length<=256&&/^[A-Za-z0-9._:-]+$/.test(value);
const bytes=(value,max=1073741824)=>Number.isSafeInteger(value)&&value>0&&value<=max;
const date=value=>clean(value)&&value.length<=64&&/^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d(?:\.\d{1,7})?(?:Z|\+00:00)$/.test(value)&&Number.isFinite(Date.parse(value));
const copy=value=>value==null?null:JSON.parse(JSON.stringify(value));
function receipt(value,zip=false){return value&&opaque(value.id)&&bytes(value.sizeBytes,zip?4194304:1073741824)&&hash(value.sha256)&&
 typeof value.fileName==='string'&&value.fileName.length<=128&&(!zip||value.fileName==='apk-diagnostics.zip')?
 {id:value.id,fileName:value.fileName,sizeBytes:value.sizeBytes,sha256:value.sha256}:null;}
function intent(value){if(!value||!outputId(value.outputId)||!hash(value.plannedLeafDigest)||value.policyVersion!=='exact-installed-diagnostic-output.v1'||value.archiveFormat!=='zip-store.v1'||!value.limits||Object.entries(caps).some(([k,v])=>value.limits[k]!==v))return null;
 return {outputId:value.outputId,policyVersion:value.policyVersion,archiveFormat:value.archiveFormat,plannedLeafDigest:value.plannedLeafDigest,limits:{...caps}};}
function facts(value){const id=value?.identity;if(!value||!bytes(value.sizeBytes)||!hash(value.sha256)||!id||!clean(id.packageName)||id.packageName.length>256||!/^[A-Za-z0-9_]+(?:\.[A-Za-z0-9_]+)+$/.test(id.packageName)||!Number.isSafeInteger(id.versionCode)||id.versionCode<=0||!hash(id.signerSha256)||id.splitName!==null||!(id.versionName===null||typeof id.versionName==='string'))return null;
 return {sizeBytes:value.sizeBytes,sha256:value.sha256,identity:{packageName:id.packageName,versionCode:id.versionCode,versionName:id.versionName,signerSha256:id.signerSha256,splitName:null}};}
function reference(value){if(!value||value.contractVersion!==contractVersion||value.command!==command||!guid(value.attemptId)||!clean(value.sessionId)||value.sessionId.length>256||!opaque(value.artifactId)||!hash(value.artifactSha256)||!bytes(value.artifactSizeBytes)||!serial(value.serial)||typeof value.executionSent!=='boolean'||typeof value.cancelPending!=='boolean')return null;
 const base={contractVersion,command,phase:value.phase,attemptId:value.attemptId,sessionId:value.sessionId,artifactId:value.artifactId,artifactSha256:value.artifactSha256,artifactSizeBytes:value.artifactSizeBytes,serial:value.serial,
 operationId:null,commandDigest:null,outputId:null,plannedLeafDigest:null,expiresAt:null,executionSent:value.executionSent,cancelPending:value.cancelPending,stage:value.stage};
 if(value.phase==='preflight-unknown'&&value.operationId===null&&value.commandDigest===null&&value.outputId===null&&value.plannedLeafDigest===null&&value.expiresAt===null&&!value.executionSent&&!value.cancelPending&&value.stage==='clientUncertain')return base;
 if(value.phase!=='operation'||!ownerId(value.operationId)||!hash(value.commandDigest)||!outputId(value.outputId)||!hash(value.plannedLeafDigest)||!date(value.expiresAt)||!stages.has(value.stage)&&value.stage!=='clientUncertain')return null;
 return {...base,operationId:value.operationId,commandDigest:value.commandDigest,outputId:value.outputId,plannedLeafDigest:value.plannedLeafDigest,expiresAt:value.expiresAt};}
function summary(value){if(!value||value.diagnosticContract!=='questionable.file_manager.apk_diagnostic_bundle.v3'||!date(value.capturedAt)||!['complete','partial'].includes(value.captureState)||!Number.isInteger(value.payloadCount)||value.payloadCount<1||value.payloadCount>14||!Number.isInteger(value.failedCount)||value.failedCount<0||value.failedCount>value.payloadCount||!Number.isInteger(value.truncatedCount)||value.truncatedCount<0||value.truncatedCount>value.payloadCount||value.captureState==='complete'&&(value.failedCount||value.truncatedCount)||value.applicationReadiness!=='unknown'||value.applicationReadinessAuthority!==false||value.openXrReadiness!=='unknown'||value.openXrReadinessAuthority!==false||value.wearerVisibility!=='unknown'||value.wearerVisibilityAuthority!==false)return null;
 return {diagnosticContract:value.diagnosticContract,capturedAt:value.capturedAt,captureState:value.captureState,payloadCount:value.payloadCount,failedCount:value.failedCount,truncatedCount:value.truncatedCount,applicationReadiness:'unknown',applicationReadinessAuthority:false,openXrReadiness:'unknown',openXrReadinessAuthority:false,wearerVisibility:'unknown',wearerVisibilityAuthority:false};}
function observed(value,original){const output=value?.output,i=intent(output?.intent);if(!value||value.contractVersion!==contractVersion||value.command!==command||value.operationId!==original.operationId||value.commandDigest!==original.commandDigest||value.serial!==original.serial||!date(value.expiresAt)||Date.parse(value.expiresAt)!==Date.parse(original.expiresAt)||!stages.has(value.stage)||typeof value.consumed!=='boolean'||!i||i.outputId!==original.outputId||i.plannedLeafDigest!==original.plannedLeafDigest||!['preparing','preflighted','capturing','sealing','sealed','available','unavailable','cleanupPending','removed'].includes(output.phase)||typeof output.available!=='boolean'||output.available!==(output.phase==='available')||!Number.isInteger(output.resourceCount)||output.resourceCount<0||output.resourceCount>16||!Number.isInteger(output.debtCount)||output.debtCount<0||output.debtCount>output.resourceCount||!errors.has(value.errorCode)&&value.errorCode!==null||!errors.has(output.errorCode)&&output.errorCode!==null)return null;
 let archive=null;if(output.available){const a=output.archive;if(value.stage!=='completed'||!value.consumed||!a||a.outputId!==i.outputId||a.fileName!=='apk-diagnostics.zip'||!bytes(a.sizeBytes,4194304)||!hash(a.sha256)||!date(a.publishedAt))return null;archive={outputId:a.outputId,fileName:'apk-diagnostics.zip',sizeBytes:a.sizeBytes,sha256:a.sha256,publishedAt:a.publishedAt};}else if(output.archive!==null)return null;
 const safeSummary=value.diagnosticSummary===null?null:summary(value.diagnosticSummary);if(value.diagnosticSummary!==null&&!safeSummary||value.stage==='completed'&&!safeSummary||output.available&&!safeSummary||['running','cancellationRequested'].includes(value.stage)&&!value.consumed||['preparing','preflighted'].includes(value.stage)&&value.consumed)return null;
 return {contractVersion,operationId:value.operationId,command,commandDigest:value.commandDigest,expiresAt:value.expiresAt,stage:value.stage,consumed:value.consumed,serial:value.serial,
 output:{intent:i,phase:output.phase,available:output.available,resourceCount:output.resourceCount,debtCount:output.debtCount,errorCode:output.errorCode,archive},diagnosticSummary:safeSummary,errorCode:value.errorCode};}

export function createDiagnosticsView({api,getContext,getArtifact,setBusy,showResult,downloadVerified}){
 const element=id=>document.getElementById('diagnostics-'+id),text=(id,value)=>{element(id).textContent=value;};
 let original=null,damaged=false,review=null,reviewVerified=false,latest=null,transient=null,authStamp='',scopeStamp='',authEpoch=0,scopeEpoch=0,observationEpoch=0;
 let message='No diagnostics prepared.',copyMessage='No transient ZIP copy prepared. Copying never captures again.';
 const calls=new Set();
 try{const stored=sessionStorage.getItem(storageKey);if(stored){if(stored.length>4096)damaged=true;else{original=reference(JSON.parse(stored));damaged=!original;}message=damaged?'Saved diagnostics reference is invalid. Preserve it for the owner; new work stays blocked.':'Saved original restored. Explicitly read its status; no capture or copy is replayed.';}}catch{damaged=true;message='Diagnostics reference storage is unavailable. No native request can be admitted from this card.';}
 const hasUnresolved=()=>Boolean(damaged||original&&(original.phase==='preflight-unknown'||original.cancelPending||!terminal.has(original.stage)));
 const hasUncertainPreflight=()=>Boolean(damaged||original?.phase==='preflight-unknown');
 const getOriginalReference=()=>damaged?{damaged:true}:copy(original);
 const auth=context=>JSON.stringify([context.connected,context.sessionId,context.authGeneration]);
 const sourceScope=context=>JSON.stringify([auth(context),context.selected?.serial,context.selected?.isReady,getArtifact()]);
 const live=context=>context.connected===true&&clean(context.sessionId)&&context.sessionId.length>0;
 const available=context=>live(context)&&context.capability?.supported===true&&context.capability?.contractVersions?.includes(contractVersion);
 const binding=()=>({contractVersion,operationId:original.operationId,commandDigest:original.commandDigest});
 function persist(){try{if(original)sessionStorage.setItem(storageKey,JSON.stringify(reference(original)));else sessionStorage.removeItem(storageKey);return true;}catch{return false;}}
 function enter(name){calls.add(name);setBusy(true);update();}
 function leave(name){calls.delete(name);setBusy(calls.size>0);update();}
 function update(){const context=getContext(),a=auth(context),s=sourceScope(context);if(a!==authStamp){authStamp=a;authEpoch++;reviewVerified=false;review=null;latest=null;element('consent').checked=false;}
  if(s!==scopeStamp){scopeStamp=s;scopeEpoch++;reviewVerified=false;element('consent').checked=false;}
  if(original?.phase==='operation'&&!original.executionSent&&Date.parse(original.expiresAt)<=Date.now()){reviewVerified=false;element('consent').checked=false;}
  const source=receipt(getArtifact()),ready=context.selected?.isReady&&serial(context.selected?.serial),ownBusy=calls.size>0,busy=context.busy||ownBusy;
  const approval=Boolean(available(context)&&original?.phase==='operation'&&!original.executionSent&&original.stage==='preflighted'&&reviewVerified&&Date.parse(original.expiresAt)>Date.now()&&!context.externalUnresolved&&!busy);
  const recovery=Boolean(available(context)&&original?.phase==='operation'&&!damaged&&(!context.busy||calls.has('capture')));
  element('prepare').disabled=!available(context)||!source||!clean(source.fileName)||!source.fileName.toLowerCase().endsWith('.apk')||!ready||busy||hasUnresolved()||Boolean(context.externalUnresolved);
  element('consent').disabled=!approval;element('capture').disabled=!approval||!element('consent').checked;
  element('status').disabled=!recovery||calls.has('status');element('cancel').disabled=!recovery||calls.has('cancel')||!original?.executionSent||original.cancelPending||terminal.has(original?.stage);
  element('reference').disabled=!original;
  const archive=latest?.output.archive,nativeArchive=Boolean(available(context)&&archive&&!busy);
  element('copy').disabled=!nativeArchive||context.artifactStaging?.supported!==true;
  const copied=Boolean(live(context)&&transient&&transient.sessionId===context.sessionId&&transient.authStamp===authStamp);
  element('save').disabled=!copied||busy||typeof downloadVerified!=='function';element('checksum').disabled=!copied||busy;element('remove').disabled=!copied||busy;
  text('source',source?`${source.fileName} — ${source.sizeBytes} bytes — ${source.sha256}${source.fileName.toLowerCase().endsWith('.apk')?'':' — Select a staged APK (.apk) for diagnostics.'}`:'No uploaded APK');text('target',ready?context.selected.serial:'No ready target');
  text('capability',available(context)?'Retained diagnostics is configured. Preparation checks protected storage and the exact APK and target; capture still requires your explicit review and consent.':'Use a configured Windows companion with protected staging and retained diagnostics, or the existing typed apk diagnose CLI. No request is sent automatically.');
  element('review').hidden=!review;if(review){text('reviewed-target',review.serial);text('reviewed-size',String(review.artifact.sizeBytes));text('reviewed-sha',review.artifact.sha256);text('reviewed-package',review.artifact.identity.packageName);text('reviewed-version',`${review.artifact.identity.versionCode} / ${review.artifact.identity.versionName??'Not reported'}`);text('reviewed-signer',review.artifact.identity.signerSha256);}
  element('summary').hidden=!latest?.diagnosticSummary;if(latest?.diagnosticSummary){const v=latest.diagnosticSummary;text('summary',`Capture ${v.captureState}: ${v.payloadCount} payloads, ${v.failedCount} failed, ${v.truncatedCount} truncated.\nApplication readiness, OpenXR readiness and wearer visibility remain unknown.`);}
  element('archive').hidden=!archive;if(archive){text('archive-size',String(archive.sizeBytes));text('archive-sha',archive.sha256);}
  text('original-scope',original?`${original.operationId??'Unacknowledged preparation'} — target ${original.serial} — APK ${original.artifactSha256} — ${original.stage}`:'No saved original');
  text('state',message+(review&&!reviewVerified?' Review is invalid for the current source, target or authentication; it cannot be captured.':''));text('copy-state',copyMessage+(transient&&!copied?' The copy belongs to its original authentication scope.':''));
 }
 function uncertain(textMessage){if(original?.phase==='operation'&&terminal.has(original.stage)){message='The original terminal acknowledgement remains authoritative; a later unavailable reply did not replace it.';return;}if(original?.phase==='operation'&&!terminal.has(original.stage)&&!['outcomeUnknownRecoveryRequired','cleanupDebt'].includes(original.stage))original.stage='clientUncertain';persist();message=textMessage;}
 async function prepare(){update();if(element('prepare').disabled)return;const context=getContext(),source=receipt(getArtifact()),sentAuth=authEpoch,sentScope=scopeEpoch,previous=copy(original);
  original={contractVersion,command,phase:'preflight-unknown',attemptId:crypto.randomUUID(),sessionId:context.sessionId,artifactId:source.id,artifactSha256:source.sha256,artifactSizeBytes:source.sizeBytes,serial:context.selected.serial,operationId:null,commandDigest:null,outputId:null,plannedLeafDigest:null,expiresAt:null,executionSent:false,cancelPending:false,stage:'clientUncertain'};
  review=null;latest=null;reviewVerified=false;element('consent').checked=false;if(!persist()){original=previous;message='Cannot save the preflight intent; no request was sent. Restore reference storage before another preparation.';update();return;}
  message='Preparation intent saved. Asking the retained owner once; a lost reply can leave an anonymous retained input and output reservation.';enter('prepare');
  try{const value=await api('/browser/v3/retained/preflight',{method:'POST',body:JSON.stringify({contractVersion,artifactId:source.id,sha256:source.sha256,sizeBytes:source.sizeBytes,command,serial:original.serial})});
   const f=facts(value?.artifact),i=intent(value?.outputIntent);if(!value||value.contractVersion!==contractVersion||value.command!==command||!ownerId(value.operationId)||!hash(value.commandDigest)||!date(value.expiresAt)||value.serial!==original.serial||!f||f.sha256!==source.sha256||f.sizeBytes!==source.sizeBytes||!i)throw new Error('reply_binding_invalid');
   // Keep validated original custody even across auth changes; it never restores approval.
   update();
   original={...original,phase:'operation',operationId:value.operationId,commandDigest:value.commandDigest,outputId:i.outputId,plannedLeafDigest:i.plannedLeafDigest,expiresAt:value.expiresAt,stage:'preflighted'};persist();update();
   if(sentAuth!==authEpoch||sentScope!==scopeEpoch){message='Preparation acknowledged for its original source and target. Authentication or review changed; capture remains disabled. Read original status explicitly.';return;}
   review={contractVersion,operationId:value.operationId,command,commandDigest:value.commandDigest,expiresAt:value.expiresAt,artifact:f,serial:value.serial,outputIntent:i};reviewVerified=true;message='Exact APK and target prepared. Review private-data scope and consent before the sole capture request.';showResult(copy(review),message);
  }catch(error){if(error?.dispatched===false&&predispatch.has(error.message)){original=null;persist();message='Preparation was explicitly rejected before owner admission. No capture was sent.';}else uncertain('Preparation acknowledgement is unknown. Its saved intent has no invented operation ID. Keep the native owner and reference for qualified recovery; do not prepare or capture again.');}
  finally{leave('prepare');}
 }
 function apply(value,{startEpoch=observationEpoch}={}){const safe=observed(value,original);if(!safe)throw new Error('reply_binding_invalid');
  if(startEpoch!==observationEpoch&&(terminal.has(original.stage)||['outcomeUnknownRecoveryRequired','cleanupDebt'].includes(original.stage)))return;
  observationEpoch++;latest=safe;if(!(original.executionSent&&safe.stage==='preflighted'&&!safe.consumed))original.stage=safe.stage;else original.stage='clientUncertain';
  if(terminal.has(safe.stage))original.cancelPending=false;persist();message=original.stage==='clientUncertain'?'The owner still reports an unconsumed approval after a sent execution. Execution acknowledgement remains unknown; never send it again.':safe.stage==='cancellationRequested'?'Cancellation requested. Actual capture termination and cleanup are not acknowledged yet.':safe.stage==='completed'?'Capture acknowledged. Only completeness facts are shown; app and wearer readiness remain unknown.':`Original ${safe.stage}; output availability and cleanup facts are separate.`;
  showResult(copy(safe),message);
 }
 async function capture(){update();if(element('capture').disabled)return;const sentAuth=authEpoch,startEpoch=observationEpoch;original.executionSent=true;element('consent').checked=false;reviewVerified=false;
  if(!persist()){original.executionSent=false;reviewVerified=Boolean(review);message='Cannot save the execution reference; no capture was sent. Fresh consent is required before another explicit attempt.';update();return;}
  message='Capture sent once. Page loss and cancellation requests do not acknowledge completion.';enter('capture');
  try{const value=await api('/browser/v3/retained/execute',{method:'POST',body:JSON.stringify(binding())});update();if(sentAuth!==authEpoch){uncertain('Capture reply crossed authentication. Preserve the original and explicitly read its native status.');return;}apply(value,{startEpoch});}
  catch{uncertain('Capture acknowledgement is unknown. Read the original status explicitly; never replay the capture.');}
  finally{leave('capture');}
 }
 async function recover(cancel=false){update();if(element(cancel?'cancel':'status').disabled)return;const sentAuth=authEpoch,startEpoch=observationEpoch,scope=copy(binding()),name=cancel?'cancel':'status';
  if(cancel){const previousCancel=original.cancelPending;original.cancelPending=true;if(!persist()){original.cancelPending=previousCancel;message='Cannot save the cancellation intent; no cancellation was sent.';update();return;}}
  message=cancel?'Requesting cancellation; this is not completion or rollback.':'Reading original native status without preparing or capturing again.';enter(name);
  try{const value=await api('/browser/v3/retained/'+name,{method:'POST',body:JSON.stringify(scope)});update();if(sentAuth!==authEpoch)return;apply(value,{startEpoch});}
  catch{message=cancel?'Cancellation acknowledgement is unknown. Keep the original; explicit status must report actual terminal acknowledgement.':'Original status unavailable. Its reference and any uncertainty remain unchanged; use the same configured native owner.';}
  finally{leave(name);}
 }
 async function stageCopy(){update();if(element('copy').disabled)return;const sentAuth=authEpoch,source={...binding(),outputId:latest.output.archive.outputId,sizeBytes:latest.output.archive.sizeBytes,sha256:latest.output.archive.sha256};copyMessage='Copying the published archive into separate protected transient staging; this never captures again.';enter('copy');
  try{const value=await api('/browser/v3/retained/output',{method:'POST',body:JSON.stringify(source)}),a=receipt(value?.artifact,true);
   if(value?.state!=='staged'||value.scope!=='localOnly'||value.deviceDispatched!==false||!a||a.sizeBytes!==source.sizeBytes||a.sha256!==source.sha256||!value.source||Object.keys(source).some(k=>source[k]!==value.source[k]))throw new Error('copy_binding_invalid');
   update();if(sentAuth!==authEpoch){copyMessage='Copy acknowledgement crossed authentication; bounded transient bytes may remain on the original host. No save or recapture was started.';return;}
   transient={artifact:a,sessionId:getContext().sessionId,authStamp};copyMessage='Separate ZIP copy verified by receipt size and SHA-256. Save or remove this exact copy explicitly.';showResult({state:'staged',scope:'localOnly',artifact:{...a},source:{...source},deviceDispatched:false},copyMessage);
  }catch{copyMessage='ZIP copy acknowledgement unavailable. A bounded transient copy may remain; another explicit archive copy never captures again. Native archive custody is unchanged.';}
  finally{leave('copy');}
 }
 async function save(){update();if(element('save').disabled)return;const a={...transient.artifact},sentAuth=authEpoch;enter('save');copyMessage='Saving the prepared copy…';
  try{const promise=downloadVerified({...a}),value=await promise;update();if(!value||!receipt(value.artifact,true)||Object.keys(a).some(k=>a[k]!==value.artifact[k])||!(value.state==='saved'&&value.mode==='streaming'||value.state==='downloadStarted'&&value.mode==='buffered'))throw new Error('save_binding_invalid');
   copyMessage=value.state==='saved'?'Save verified and destination close acknowledged.':'Verified bytes handed to the browser download flow; final destination completion is not observed.';if(sentAuth!==authEpoch)copyMessage+=' This save belongs to its original authentication scope.';
  }catch(error){copyMessage=error?.saveOutcomeUnknown?'Save close outcome is unknown. Check the selected file before another Save.':'Save was not acknowledged. Check the selected file before another Save. No capture or new copy was sent.';if(error?.cleanupPending)copyMessage+=' Destination cleanup is pending; check the selected file before another Save.';}
  finally{leave('save');}
 }
 async function remove(){update();if(element('remove').disabled)return;const a={...transient.artifact},sentAuth=authEpoch;enter('remove');
  try{const value=await api('/browser/v1/artifacts/'+a.id,{method:'DELETE'});update();if(value?.state!=='removed'||value.scope!=='localOnly'||value.deviceDispatched!==false)throw new Error('remove_unacknowledged');if(sentAuth===authEpoch){transient=null;copyMessage='This exact transient ZIP copy was removed. The native archive and original outcome remain unchanged.';}}
  catch{copyMessage='Removal of the transient copy was not acknowledged. Native archive custody and the original outcome are unchanged.';}
  finally{leave('remove');}
 }
 function downloadText(contents,name){const url=URL.createObjectURL(new Blob([contents],{type:'text/plain'})),link=document.createElement('a');link.href=url;link.download=name;link.click();setTimeout(()=>URL.revokeObjectURL(url),0);}
 element('prepare').addEventListener('click',prepare);element('capture').addEventListener('click',capture);element('status').addEventListener('click',()=>recover());element('cancel').addEventListener('click',()=>recover(true));element('copy').addEventListener('click',stageCopy);element('save').addEventListener('click',save);element('remove').addEventListener('click',remove);element('consent').addEventListener('change',update);
 element('checksum').addEventListener('click',()=>{update();if(!element('checksum').disabled)downloadText(`${transient.artifact.sha256}  apk-diagnostics.zip\n`,'apk-diagnostics.zip.sha256');});element('reference').addEventListener('click',()=>{if(original)downloadText(JSON.stringify(reference(original),null,2),'qfm-diagnostics-original.json');});
 return {update,hasUnresolved,getOriginalReference,hasUncertainPreflight};
}
