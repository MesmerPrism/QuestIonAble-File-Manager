const action='kiosk.direct.staging-list', maximumRows=128;
const clean=value=>typeof value==='string'&&!/[\u0000-\u001f\u007f-\u009f]/u.test(value);
const guid=value=>clean(value)&&/^[a-f0-9]{8}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{12}$/.test(value)&&value.replaceAll('-','')!=='0'.repeat(32);
const session=value=>clean(value)&&value.length>0&&value.length<=128&&!/[^A-Za-z0-9_-]/.test(value);
const serial=value=>clean(value)&&value.length>0&&value.length<=256&&!/[^A-Za-z0-9._-]/.test(value);
const count=value=>Number.isSafeInteger(value)&&value>=0;
const name=value=>clean(value)&&value.length>0&&value.length<=160&&/^[A-Za-z0-9][A-Za-z0-9._ ()+@-]*$/.test(value);
function source(value){return value&&session(value.sessionId)&&guid(value.connectionId)&&guid(value.connectRequestId)&&value.usbOperationId==='usb_'+value.connectRequestId.replaceAll('-','')&&serial(value.serial)&&['stable','labs'].includes(value.productChannel)&&clean(value.expiresAtUtc)&&value.expiresAtUtc.length<=64&&Number.isFinite(Date.parse(value.expiresAtUtc))?
 {sessionId:value.sessionId,connectionId:value.connectionId,connectRequestId:value.connectRequestId,usbOperationId:value.usbOperationId,serial:value.serial,productChannel:value.productChannel,expiresAtUtc:value.expiresAtUtc}:null;}
function project(value,reference){const d=value?.kioskDirectStaging,r=d?.coreReceipt,s=reference.source;
 if(value?.requestId!==reference.requestId||value.action!==action||value.state!=='completed'||value.result!==null||value.mutation!==null||!d||
  !['connectionId','connectRequestId','usbOperationId','serial','productChannel'].every(k=>d[k]===s[k])||!count(d.totalCount)||!count(d.returnedCount)||d.returnedCount>maximumRows||d.returnedCount>d.totalCount||!count(d.omittedCount)||d.omittedCount!==d.totalCount-d.returnedCount||typeof d.complete!=='boolean'||d.complete!==(d.omittedCount===0)||!Array.isArray(d.files)||d.files.length!==d.returnedCount||
  !r||!clean(r.actionId)||!/^action_[a-f0-9]{32}$/.test(r.actionId)||r.action!=='listStaging'||r.stage!=='confirmed')throw new Error('staging_inventory_binding_invalid');
 const names=new Set(),files=[];
 for(const file of d.files){if(!file||!name(file.name)||names.has(file.name)||!count(file.bytes)||!count(file.modifiedAtMs))throw new Error('staging_inventory_invalid');names.add(file.name);files.push({name:file.name,bytes:file.bytes,modifiedAtMs:file.modifiedAtMs});}
 return {connectionId:s.connectionId,connectRequestId:s.connectRequestId,usbOperationId:s.usbOperationId,serial:s.serial,productChannel:s.productChannel,totalCount:d.totalCount,returnedCount:d.returnedCount,omittedCount:d.omittedCount,complete:d.complete,files,coreReceipt:{actionId:r.actionId,action:'listStaging',stage:'confirmed'}};
}

// Inventory observations and read references stay in memory. No effect original,
// staged artifact, credential, polling or transfer/install authority is created.
export function createDirectStagingView({api,getContext,setBusy,showResult}){
 const element=id=>document.getElementById('direct-staging-'+id),text=(id,value)=>{element(id).textContent=value;};
 let reference=null,inventory=null,active=false,authStamp='',authEpoch=0,sourceStamp='',sourceEpoch=0,message='No staging inventory requested.';
 const live=context=>context.connected===true&&session(context.sessionId);
 const auth=context=>JSON.stringify([context.connected,context.sessionId,context.authGeneration]);
 function update(){const context=getContext(),nextAuth=auth(context),currentSource=source(context.connection),nextSource=JSON.stringify(currentSource);
  if(nextAuth!==authStamp){authStamp=nextAuth;authEpoch++;if(inventory)message='Authentication changed; earlier inventory metadata was cleared. Read the original cached result explicitly under its host session when eligible.';inventory=null;}
  if(nextSource!==sourceStamp){sourceStamp=nextSource;sourceEpoch++;}
  const busy=Boolean(active||context.busy),current=Boolean(currentSource&&currentSource.sessionId===context.sessionId&&Date.parse(currentSource.expiresAtUtc)>Date.now());
  element('read').disabled=!live(context)||!current||busy||context.actions?.has?.(action)!==true;
  element('original').disabled=!live(context)||!reference||reference.source.sessionId!==context.sessionId||busy;
  text('capability',live(context)&&context.actions?.has?.(action)===true?'Inventory reads are available through the adopted Direct connection. Protected artifact staging is not required.':'Use the existing Direct connection card or typed Kiosk Direct CLI. Authentication, connection and form changes send no inventory query.');
  const shown=inventory?.source||reference?.source||currentSource;
  text('source',shown?`${shown.serial} · ${shown.productChannel} · ${shown.connectionId}${inventory?' — Historical inventory source.':reference?' — Original read scope.':''}`:'No source connection');
  element('facts').hidden=!inventory;element('empty').hidden=!inventory||inventory.value.totalCount!==0;
  const body=element('files');body.replaceChildren();
  if(inventory){const d=inventory.value;text('counts',`${d.returnedCount} returned of ${d.totalCount}; ${d.omittedCount} omitted. ${d.complete?'Complete historical inventory.':'Partial inventory; use the existing typed Kiosk Direct CLI for the complete list.'}`);
   for(const file of d.files){const row=document.createElement('tr');for(const [index,value] of [file.name,file.bytes,file.modifiedAtMs].entries()){const cell=document.createElement('td');if(index===0)cell.className='direct-staging-name';cell.textContent=String(value);row.appendChild(cell);}body.appendChild(row);}
  }else text('counts','No validated inventory result for this read.');
  text('state',message);
 }
 function enter(){active=true;setBusy(true);update();}
 function leave(){active=false;setBusy(false);update();}
 function publish(value,ref){const safe=project(value,ref);inventory={value:safe,source:{...ref.source}};
  message=safe.totalCount===0?'Signed inventory read acknowledged: no files were listed for this historical source.':safe.complete?'Signed historical inventory metadata read acknowledged. Content and transfer/install authority remain unverified.':'Partial signed inventory metadata read acknowledged. Omitted rows are counted; use the typed Kiosk Direct CLI for the complete list.';
  showResult({requestId:ref.requestId,action,state:'completed',result:null,mutation:null,kioskDirectStaging:safe},message);
 }
 async function read(){update();if(element('read').disabled)return;const ref={requestId:crypto.randomUUID(),source:source(getContext().connection)},sentAuth=authEpoch,sentSource=sourceEpoch;
  reference=ref;inventory=null;message='Reading one signed staging inventory snapshot; no transfer, installation or other effect is requested.';enter();
  try{const value=await api('/browser/v1/execute',{method:'POST',body:JSON.stringify({requestId:ref.requestId,action,connectionId:ref.source.connectionId})});project(value,ref);update();
   if(sentAuth!==authEpoch||sentSource!==sourceEpoch||Date.parse(ref.source.expiresAtUtc)<=Date.now()){
    message='Inventory reply arrived after authentication or source changed, or the connection expired. No new inventory is displayed. Read the exact cached result explicitly when eligible.';return;
   }publish(value,ref);
  }catch{message='Inventory read unavailable or unbound. No empty inventory is inferred, no data-changing action was sent, and sibling operation references remain unchanged.';}
  finally{leave();}
 }
 async function original(){update();if(element('original').disabled)return;const ref=reference,sentAuth=authEpoch;inventory=null;message='Reading the exact cached inventory result without querying the provider again.';enter();
  try{const value=await api('/browser/v1/requests/'+ref.requestId);project(value,ref);update();if(sentAuth!==authEpoch||reference!==ref||!live(getContext())||getContext().sessionId!==ref.source.sessionId)return;publish(value,ref);}
  catch{message='Cached inventory result unavailable or unbound. No empty inventory, new provider query or reconciliation is inferred.';}
  finally{leave();}
 }
 element('read').addEventListener('click',read);element('original').addEventListener('click',original);update();return {update};
}
