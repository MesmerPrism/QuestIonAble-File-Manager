const action='kiosk.direct.tags-export', maximumBytes=262144;
const schemas=new Set(['rusty.kiosk.app_tags.v1','rusty.kiosk.app_tags.v2']);
const clean=value=>typeof value==='string'&&!/[\u0000-\u001f\u007f-\u009f]/u.test(value);
const guid=value=>clean(value)&&/^[a-f0-9]{8}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{12}$/.test(value)&&value.replaceAll('-','')!=='0'.repeat(32);
const opaque=value=>clean(value)&&/^[a-f0-9]{32}$/.test(value)&&value!=='0'.repeat(32);
const hash=value=>clean(value)&&/^[a-f0-9]{64}$/.test(value);
const session=value=>clean(value)&&value.length>0&&value.length<=128&&!/[^A-Za-z0-9_-]/.test(value);
const serial=value=>clean(value)&&value.length>0&&value.length<=256&&!/[^A-Za-z0-9._-]/.test(value);
const generation=context=>Number.isSafeInteger(context.stagingGeneration)&&context.stagingGeneration>=0?context.stagingGeneration:null;
function source(value){return value&&session(value.sessionId)&&guid(value.connectionId)&&guid(value.connectRequestId)&&value.usbOperationId==='usb_'+value.connectRequestId.replaceAll('-','')&&serial(value.serial)&&['stable','labs'].includes(value.productChannel)&&clean(value.expiresAtUtc)&&value.expiresAtUtc.length<=64&&Number.isFinite(Date.parse(value.expiresAtUtc))?
 {sessionId:value.sessionId,connectionId:value.connectionId,connectRequestId:value.connectRequestId,usbOperationId:value.usbOperationId,serial:value.serial,productChannel:value.productChannel,expiresAtUtc:value.expiresAtUtc}:null;}
function limits(context){const c=context.artifactStaging;return c?.supported===true&&Number.isSafeInteger(c.maximumFileBytes)&&c.maximumFileBytes>=maximumBytes&&Number.isSafeInteger(c.maximumTotalBytes)&&c.maximumTotalBytes>=c.maximumFileBytes&&Number.isSafeInteger(c.maximumFiles)&&c.maximumFiles>0&&Number.isSafeInteger(c.transferTimeoutSeconds)&&c.transferTimeoutSeconds>0&&c.transferTimeoutSeconds<=300?c:null;}
function artifact(value){return value&&opaque(value.id)&&value.fileName==='app-tags.json'&&Number.isSafeInteger(value.sizeBytes)&&value.sizeBytes>0&&value.sizeBytes<=maximumBytes&&hash(value.sha256)?{id:value.id,fileName:'app-tags.json',sizeBytes:value.sizeBytes,sha256:value.sha256}:null;}
function project(value,reference){const d=value?.kioskDirectTags,a=artifact(d?.artifact),r=d?.coreReceipt,s=reference.source;
 if(value?.requestId!==reference.requestId||value.action!==action||value.state!=='completed'||value.result!==null||value.mutation!==null||!d||!a||!schemas.has(d.schema)||d.sizeBytes!==a.sizeBytes||d.sha256!==a.sha256||
   !['connectionId','connectRequestId','usbOperationId','serial','productChannel'].every(k=>d[k]===s[k])||!r||!clean(r.actionId)||!/^action_[a-f0-9]{32}$/.test(r.actionId)||r.action!=='exportTags'||r.stage!=='confirmed')throw new Error('tags_export_binding_invalid');
 return {connectionId:s.connectionId,connectRequestId:s.connectRequestId,usbOperationId:s.usbOperationId,serial:s.serial,productChannel:s.productChannel,schema:d.schema,sizeBytes:a.sizeBytes,sha256:a.sha256,artifact:a,coreReceipt:{actionId:r.actionId,action:'exportTags',stage:'confirmed'}};
}

// Read references and prepared metadata are memory-only. No effect-original,
// credential, tag document or automatic request is created by this card.
export function createDirectTagsView({api,getContext,setBusy,showResult,downloadVerified}){
 const element=id=>document.getElementById('direct-tags-'+id),text=(id,value)=>{element(id).textContent=value;};
 let reference=null,prepared=null,active='',authStamp='',authEpoch=0,sourceStamp='',sourceEpoch=0,lastGeneration=null;
 let message='No tag backup requested.';
 const live=context=>context.connected===true&&session(context.sessionId);
 const auth=context=>JSON.stringify([context.connected,context.sessionId,context.authGeneration]);
 const owned=context=>Boolean(prepared&&live(context)&&limits(context)&&prepared.sessionId===context.sessionId&&prepared.authEpoch===authEpoch&&prepared.generation===generation(context));
 function update(){const context=getContext(),nextAuth=auth(context),currentSource=source(context.connection),nextSource=JSON.stringify(currentSource),currentGeneration=generation(context);
  if(nextAuth!==authStamp){authStamp=nextAuth;authEpoch++;}
  if(nextSource!==sourceStamp){sourceStamp=nextSource;sourceEpoch++;}
  if(lastGeneration!==null&&currentGeneration!==lastGeneration&&prepared){prepared=null;message='Staging was cleared or changed. Earlier prepared-copy metadata is retired; a new explicit read is available.';}
  lastGeneration=currentGeneration;
  const busy=Boolean(context.busy||active),supported=Boolean(live(context)&&limits(context)&&currentGeneration!==null),current=Boolean(currentSource&&currentSource.sessionId===context.sessionId&&Date.parse(currentSource.expiresAtUtc)>Date.now());
  element('stage').disabled=!supported||!current||busy||context.actions?.has?.(action)!==true;
  element('original').disabled=!live(context)||!reference||reference.source.sessionId!==context.sessionId||reference.generation!==currentGeneration||busy;
  const local=owned(context);element('save').disabled=!local||busy||typeof downloadVerified!=='function';element('checksum').disabled=!local||busy;element('remove').disabled=!local||busy;
  text('capability',supported?'Protected staging is available. Each explicit tag backup is at most 256 KiB; the signed provider read still requires the original adopted Direct connection.':'Use a Windows companion with protected staging and its existing Direct connection, or the typed Kiosk tag export CLI. Configuration and connection changes send no tag read.');
  const shown=local?prepared.source:reference?.source||currentSource;
  text('source',shown?`${shown.serial} · ${shown.productChannel} · ${shown.connectionId}${local?' — Historical source of the prepared copy.':reference?' — Original read scope; no current tag data implied.':''}`:'No source connection');
  element('facts').hidden=!local;if(local){text('schema',prepared.value.schema);text('size',String(prepared.value.sizeBytes));text('sha',prepared.value.sha256);}
  text('state',message+(prepared&&!local?' This earlier copy belongs to its original host/authentication and cannot be used as current data.':''));
 }
 function enter(name){active=name;setBusy(true);update();}
 function leave(){active='';setBusy(false);update();}
 function publish(value,ref){const safe=project(value,ref),context=getContext();prepared={value:safe,source:{...ref.source},sessionId:context.sessionId,authEpoch,generation:ref.generation};
  message='Signed tag backup staged with its exact size and SHA-256. The source shown is historical; no tag write or reload request was sent.';
  showResult({requestId:ref.requestId,action,state:'completed',result:null,mutation:null,kioskDirectTags:safe},message);
 }
 async function stage(){update();if(element('stage').disabled)return;const context=getContext(),ref={requestId:crypto.randomUUID(),source:source(context.connection),generation:generation(context)},sentAuth=authEpoch,sentSource=sourceEpoch;
  reference=ref;message='Reading and staging one private tag backup. A lost reply can leave a bounded transient copy; this request is never replayed automatically.';enter('stage');
  try{const value=await api('/browser/v1/execute',{method:'POST',body:JSON.stringify({requestId:ref.requestId,action,connectionId:ref.source.connectionId})});project(value,ref);update();
   if(sentAuth!==authEpoch||sentSource!==sourceEpoch||ref.generation!==generation(getContext())||Date.parse(ref.source.expiresAtUtc)<=Date.now()){
    message='Tag backup reply arrived after authentication, source or staging changed, or the connection expired. No new current copy is offered; a bounded transient copy may remain. Read the exact cached result explicitly when eligible.';return;
   }publish(value,ref);
  }catch{message='Tag backup acknowledgement unavailable or unbound. A bounded transient copy may remain. Read its cached result or start a separate explicit read; neither writes tags nor reconciles another operation.';}
  finally{leave();}
 }
 async function original(){update();if(element('original').disabled)return;const ref=reference,sentAuth=authEpoch;message='Reading the exact cached export result; the provider is not queried again.';enter('original');
  try{const value=await api('/browser/v1/requests/'+ref.requestId);project(value,ref);update();if(sentAuth!==authEpoch||reference!==ref||!live(getContext())||getContext().sessionId!==ref.source.sessionId||generation(getContext())!==ref.generation)return;
   publish(value,ref);
  }catch{message='Cached export result unavailable or unbound. No export, tag write or automatic retry was sent.';}
  finally{leave();}
 }
 async function save(){update();if(element('save').disabled)return;const a={...prepared.value.artifact},sentAuth=authEpoch;message='Saving the prepared private tag copy…';enter('save');
  try{const promise=downloadVerified({...a}),value=await promise;update();const returned=artifact(value?.artifact);
   if(!returned||Object.keys(a).some(k=>a[k]!==returned[k])||!(value.state==='saved'&&value.mode==='streaming'||value.state==='downloadStarted'&&value.mode==='buffered'))throw new Error('save_binding_invalid');
   message=value.state==='saved'?'Save verified and destination close acknowledged. Headset tags are unchanged by this save.':'Verified bytes handed to the browser download flow; final destination completion is not observed.';
   if(sentAuth!==authEpoch)message+=' This save belongs to its original authentication scope.';
  }catch(error){message=error?.saveOutcomeUnknown?'Save close outcome is unknown. Check the selected file before another Save.':'Save was not acknowledged. Check the selected file before another Save. No new tag export was sent.';if(error?.cleanupPending)message+=' Destination cleanup remains pending.';}
  finally{leave();}
 }
 async function remove(){update();if(element('remove').disabled)return;const saved=prepared,a={...saved.value.artifact},sentAuth=authEpoch;message='Removing only the exact prepared local copy…';enter('remove');
  try{const value=await api('/browser/v1/artifacts/'+a.id,{method:'DELETE'});update();if(value?.state!=='removed'||value.scope!=='localOnly'||value.artifactId!==a.id||value.deviceDispatched!==false)throw new Error('removal_binding_invalid');
   if(sentAuth===authEpoch&&prepared===saved){prepared=null;reference=null;message='This prepared local copy was removed. Headset tags and every sibling operation reference are unchanged.';}
  }catch{message='Removal of the prepared local copy was not acknowledged. No tag write, new export or global cleanup was sent.';}
  finally{leave();}
 }
 element('stage').addEventListener('click',stage);element('original').addEventListener('click',original);element('save').addEventListener('click',save);element('remove').addEventListener('click',remove);
 element('checksum').addEventListener('click',()=>{update();if(element('checksum').disabled)return;const url=URL.createObjectURL(new Blob([`${prepared.value.sha256}  app-tags.json\n`],{type:'text/plain'})),link=document.createElement('a');link.href=url;link.download='app-tags.json.sha256';link.click();setTimeout(()=>URL.revokeObjectURL(url),1000);});
 update();return {update};
}
