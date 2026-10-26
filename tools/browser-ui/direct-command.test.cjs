// Shipped module with intercepted synthetic acknowledgements only; no host/device.
const test=require('node:test'),assert=require('node:assert/strict'),fs=require('node:fs/promises'),path=require('node:path');
const {chromium}=require('playwright');
const root=path.resolve(__dirname,'../../src/QuestIonAbleFileManager.BrowserHost/wwwroot'),origin='http://127.0.0.1:18141',key='qfm-browser-direct-command-v1';
const commands=['status','show-controls','show-apps','check-setup-helper','set-launch-requirement','cancel-pending-launch','launch-normal','launch-kiosk'];
const logical=id=>'kiosk_'+id.replaceAll('-','');
const connection=()=>({sessionId:'session-a',connectionId:'a1234567-1234-4234-8234-123456789abc',connectRequestId:'b1234567-1234-4234-8234-123456789abc',usbOperationId:'usb_b1234567123442348234123456789abc',serial:'USB_TARGET',productChannel:'stable',expiresAtUtc:new Date(Date.now()+300000).toISOString()});
function typed(command,id,value){return {schema:'rusty.kiosk.cli_result.v1',requestId:id,command,accepted:true,completed:true,controlsOpen:command==='show-controls',selectedKey:'selected-key',selectedPackage:'example.selected.app',selectedInstalled:true,selectedLaunchable:true,selectedLaunchRequirement:value||'any',pendingRequirementLaunch:false,guardArmed:command==='launch-kiosk',setupHelperInstalled:true,setupHelperReady:true,selectedEntry:{key:'selected-key',launchRequirement:value||'any'}};}
function outcome(body,scope,records){
  const invoke=body.action==='kiosk.direct.invoke',cancel=body.action==='kiosk.direct.request-cancel';
  const original=invoke?{...scope,originalRequestId:body.requestId,logicalRequestId:logical(body.requestId),command:body.command,value:body.value}:records.get(body.originalRequestId).kioskDirectCommand;
  const operation=invoke?'invoke':cancel?'requestCancel':body.action.endsWith('request-status')?'requestStatus':'requestResult';
  const d={connectionId:original.connectionId,connectRequestId:original.connectRequestId,usbOperationId:original.usbOperationId,serial:original.serial,productChannel:original.productChannel,operation,originalRequestId:original.originalRequestId,logicalRequestId:original.logicalRequestId,command:original.command,value:original.value,originalEffect:operation==='invoke'||operation==='requestResult'?'confirmed':'unknown',reconcilesOriginal:operation==='invoke'||operation==='requestResult',coreReceipt:{actionId:'action_'+body.requestId.replaceAll('-',''),action:cancel?'cancelRequest':operation,requestId:original.logicalRequestId,stage:cancel?'cancelled':'confirmed'},lifecycle:operation==='requestStatus'||cancel?{requestId:original.logicalRequestId,operationState:cancel?'cancelled':'confirmed',accepted:true,completed:true,enqueuedAtMs:1000,expiresAtMs:2000}:null,typedResult:operation==='invoke'||operation==='requestResult'?typed(original.command,original.logicalRequestId,original.value):null,cancellationAcknowledged:cancel};
  return {requestId:body.requestId,action:body.action,state:'completed',result:null,mutation:null,kioskDirectCommand:d};
}
let browser;test.before(async()=>{browser=await chromium.launch({headless:true});});test.after(async()=>browser?.close());
async function fixture(t,{respond,status,state={},saved,width=1280,storageFailure=false}={}){
  const context=await browser.newContext({serviceWorkers:'block',viewport:{width,height:1100}});t.after(()=>context.close());const page=await context.newPage(),calls=[],records=new Map(),errors=[],scope=connection();
  page.setDefaultTimeout(6000);page.setDefaultNavigationTimeout(6000);
  page.on('pageerror',error=>errors.push(error.message));
  if(saved!==undefined)await context.addInitScript(({key,saved})=>{if(!sessionStorage.getItem('fixture-seeded')){sessionStorage.setItem(key,typeof saved==='string'?saved:JSON.stringify(saved));sessionStorage.setItem('fixture-seeded','yes');}},{key,saved});
  if(storageFailure)await context.addInitScript(()=>{Storage.prototype.setItem=function(){throw new Error('private-storage-error');};});
  await context.route('**/*',async route=>{
    const url=new URL(route.request().url());if(url.origin!==origin)return route.abort('blockedbyclient');
    if(url.pathname.startsWith('/browser/')){
      const method=route.request().method(),body=method==='POST'?route.request().postDataJSON():null,call={method,path:url.pathname,body};calls.push(call);
      if(body){const value=outcome(body,scope,records);records.set(body.requestId,value);call.saved=await page.evaluate(key=>JSON.parse(sessionStorage.getItem(key)||'null'),key);return respond?respond(route,body,value):route.fulfill({json:value});}
      const value=records.get(url.pathname.split('/').at(-1));return status?status(route,value):route.fulfill({json:value});
    }
    if(url.pathname==='/command-fixture.js')return route.fulfill({contentType:'text/javascript',body:`import {createDirectCommandView} from './direct-command-view.js';
      window.ctx=${JSON.stringify({sessionId:'session-a',connected:true,busy:false,authGeneration:1,actions:['kiosk.direct.invoke','kiosk.direct.request-status','kiosk.direct.request-result','kiosk.direct.request-cancel'],connection:scope,externalUnresolved:false,...state})};window.ctx.actions=new Set(window.ctx.actions);window.busyCalls=[];window.contextReads=0;
      window.view=createDirectCommandView({getContext:()=>{window.contextReads++;return window.ctx;},setBusy:value=>{window.busyCalls.push(value);window.ctx.busy=value;window.siblingBlocked=Boolean(value||window.view?.hasUnresolved());window.view?.update();},showResult:(value,summary)=>window.latest={value,summary},api:async(path,options)=>{const r=await fetch(path,{...options,cache:'no-store'});const value=await r.json();if(!r.ok)throw Object.assign(new Error(value.error),{dispatched:value.dispatched,status:r.status});return value;}});window.view.update();window.fixtureReady=true;`});
    const name=url.pathname==='/'?'index.html':url.pathname.slice(1);if(!['index.html','styles.css','direct-command-view.js'].includes(name))return route.fulfill({status:404,body:''});
    let bytes=await fs.readFile(path.join(root,name));if(name==='index.html')bytes=Buffer.from(bytes.toString().replace(/<script[^>]*src="\.\/pwa.js"[^>]*><\/script>/,'').replace(/<script[^>]*src="\.\/app.js"[^>]*><\/script>/,'<script type="module" src="./command-fixture.js"></script>'));
    return route.fulfill({contentType:name.endsWith('.js')?'text/javascript':name.endsWith('.css')?'text/css':'text/html',body:bytes});
  });
  await page.goto(origin);await page.waitForFunction(()=>window.fixtureReady);
  const update=patch=>page.evaluate(patch=>{Object.assign(window.ctx,patch);if(Array.isArray(window.ctx.actions))window.ctx.actions=new Set(window.ctx.actions);window.view.update();},patch);
  const invoke=async(command='status',value='any')=>{await page.locator('#direct-command-command').selectOption(command);if(command==='set-launch-requirement')await page.locator('#direct-command-requirement').selectOption(value);await page.locator('#direct-command-invoke-confirm').check();await page.locator('#direct-command-invoke').click();await page.waitForFunction(()=>!window.ctx.busy);};
  const recovery=async kind=>{await page.locator('#direct-command-'+kind).click();await page.waitForFunction(()=>!window.ctx.busy);};
  const cancel=async()=>{await page.locator('#direct-command-cancel-confirm').check();await page.locator('#direct-command-cancel').click();await page.waitForFunction(()=>!window.ctx.busy);};
  const stored=()=>page.evaluate(key=>JSON.parse(sessionStorage.getItem(key)||'null'),key);
  t.after(()=>assert.deepEqual(errors,[],'No uncaught command UI errors'));return {page,calls,records,scope,update,invoke,recovery,cancel,stored};
}

for(const command of commands)test('explicit closed command '+command+' saves logical identity before its single POST',async t=>{
  const f=await fixture(t);assert.equal(f.calls.length,0);await f.invoke(command,'wifi-off');const call=f.calls[0],ref=call.saved.original;
  assert.equal(call.body.action,'kiosk.direct.invoke');assert.equal(call.body.confirmed,true);assert.equal(call.body.value,command==='set-launch-requirement'?'wifi-off':null);
  assert.equal(ref.logicalRequestId,logical(call.body.requestId));assert.equal(ref.command,command);assert.equal(ref.connectionId,f.scope.connectionId);assert.equal(await f.stored(),null);
  assert.match(await f.page.locator('#direct-command-state').textContent(),/acknowledged/i);assert.equal(f.calls.length,1);
});

test('pure recovery predicates return independent copies without context recursion',async t=>{
  const ref={...connection(),requestId:'c1234567-1234-4234-8234-123456789abc',action:'kiosk.direct.invoke',logicalRequestId:'kiosk_c1234567123442348234123456789abc',command:'status',value:null};delete ref.expiresAtUtc;
  const f=await fixture(t,{saved:{original:ref,cancellation:null}});
  const value=await f.page.evaluate(()=>{const before=window.contextReads,ref=window.view.getCleanupReference();ref.connectionId='changed';return {unchanged:window.view.getCleanupReference(),blocked:window.view.hasUnresolved(),reads:window.contextReads-before};});
  assert.equal(value.reads,0);assert.equal(value.blocked,true);assert.deepEqual(value.unchanged,{sessionId:'session-a',connectionId:ref.connectionId});assert.equal(f.calls.length,0);
});

test('completed acknowledgement updates the shared summary after the exact proof',async t=>{
  const f=await fixture(t);await f.invoke();assert.match(await f.page.evaluate(()=>window.latest.summary),/acknowledged/);
});

test('lost Invoke survives reload without replay and explicit typed result reads only its original',async t=>{
  const f=await fixture(t,{respond:(route,body,value)=>body.action==='kiosk.direct.invoke'?route.abort('failed'):route.fulfill({json:value})});
  await f.invoke('show-controls');const before=await f.stored(),id=before.original.requestId;assert.equal(f.calls.length,1);assert.equal(await f.page.evaluate(()=>window.siblingBlocked),true);
  await f.page.reload();await f.page.waitForFunction(()=>window.fixtureReady);assert.equal(f.calls.length,1);assert.deepEqual(await f.stored(),before);
  await f.recovery('request-status');assert.deepEqual(await f.stored(),before);assert.match(await f.page.locator('#direct-command-lifecycle').textContent(),/never|do not acknowledge/);
  assert.deepEqual(Object.keys(f.calls[1].body).sort(),['requestId','action','connectionId','originalRequestId'].sort());assert.equal(f.calls[1].body.originalRequestId,id);
  await f.recovery('request-result');assert.equal(await f.stored(),null);assert.equal(f.calls.length,3);assert.equal(await f.page.evaluate(()=>window.siblingBlocked),false);
});

test('uncertain cancellation has its own stored ID and lifecycle facts cannot acknowledge it',async t=>{
  const f=await fixture(t,{respond:(route,body,value)=>['kiosk.direct.invoke','kiosk.direct.request-cancel'].includes(body.action)?route.abort('failed'):route.fulfill({json:value})});
  await f.invoke();const original=(await f.stored()).original;await f.cancel();const both=await f.stored(),cancel=f.calls[1];assert.deepEqual(both.original,original);assert.equal(cancel.saved.cancellation.requestId,cancel.body.requestId);assert.notEqual(cancel.body.requestId,original.requestId);
  f.records.get(original.requestId).kioskDirectCommand.originalEffect='unknown';
  await f.recovery('request-status');assert.deepEqual(await f.stored(),both);assert.equal(f.calls[2].body.originalRequestId,original.requestId);
  await f.page.reload();await f.page.waitForFunction(()=>window.fixtureReady);assert.deepEqual(await f.stored(),both);assert.equal(f.calls.length,3);
  await f.page.locator('#direct-command-cancellation-status').click();await f.page.waitForFunction(()=>!JSON.parse(sessionStorage.getItem('qfm-browser-direct-command-v1')).cancellation);
  assert.deepEqual((await f.stored()).original,original);assert.match(await f.page.locator('#direct-command-cancel-state').textContent(),/Actual cancellation acknowledgement/);assert.equal(await f.page.evaluate(()=>window.siblingBlocked),true);assert.equal(f.calls.at(-1).path,'/browser/v1/requests/'+cancel.body.requestId);
});

test('typed result can acknowledge Invoke while an uncertain cancel independently retains cleanup custody',async t=>{
  const f=await fixture(t,{respond:(route,body,value)=>['kiosk.direct.invoke','kiosk.direct.request-cancel'].includes(body.action)?route.abort('failed'):route.fulfill({json:value})});
  await f.invoke();await f.cancel();const cancel=(await f.stored()).cancellation;await f.recovery('request-result');const after=await f.stored();assert.equal(after.original,null);assert.deepEqual(after.cancellation,cancel);assert.equal(await f.page.locator('#direct-command-invoke-confirm').isDisabled(),true);
  assert.deepEqual(await f.page.evaluate(()=>window.view.getCleanupReference()),{sessionId:cancel.sessionId,connectionId:cancel.connectionId});
  await f.page.locator('#direct-command-cancellation-status').click();await f.page.waitForFunction(()=>!window.view.hasUnresolved());assert.equal(await f.stored(),null);
});

for(const pending of [true,null])test('launch with pending requirement '+pending+' preserves unknown despite a confirmed Core guard receipt',async t=>{
  const f=await fixture(t,{respond:(route,body,value)=>{value.kioskDirectCommand.typedResult.pendingRequirementLaunch=pending;value.kioskDirectCommand.originalEffect=pending?'pendingWearerAction':'unknown';value.kioskDirectCommand.reconcilesOriginal=false;value.state=pending?'pending':'outcomeUnknown';return route.fulfill({json:value});}});
  await f.invoke('launch-kiosk');assert.ok((await f.stored()).original);assert.equal(await f.page.evaluate(()=>window.view.hasUnresolved()),true);assert.match(await f.page.locator('#direct-command-state').textContent(),/Core stage confirmed/);assert.equal(f.calls.length,1);
});

for(const [command,change] of [
  ['show-controls',d=>d.typedResult.controlsOpen=false],['show-apps',d=>d.typedResult.controlsOpen=true],
  ['set-launch-requirement',d=>d.typedResult.selectedEntry=null],['set-launch-requirement',d=>d.typedResult.selectedEntry.launchRequirement='any'],
  ['cancel-pending-launch',d=>d.typedResult.pendingRequirementLaunch=null],['launch-normal',d=>d.typedResult.guardArmed=true],['launch-kiosk',d=>d.typedResult.guardArmed=false]
])test('independent operation-specific readback rejects mismatched '+command+' evidence '+change.toString(),async t=>{
  const f=await fixture(t,{respond:(route,body,value)=>{change(value.kioskDirectCommand);return route.fulfill({json:value});}});await f.invoke(command,'wifi-off');assert.ok((await f.stored()).original);assert.equal(await f.page.evaluate(()=>window.siblingBlocked),true);
});

const crossed={
  request:value=>value.requestId='d1234567-1234-4234-8234-123456789abc',action:value=>value.action='kiosk.direct.request-result',
  connection:value=>value.kioskDirectCommand.connectionId='d1234567-1234-4234-8234-123456789abc',
  connect:value=>value.kioskDirectCommand.connectRequestId='d1234567-1234-4234-8234-123456789abc',usb:value=>value.kioskDirectCommand.usbOperationId='usb_'+'a'.repeat(32),
  serial:value=>value.kioskDirectCommand.serial='OTHER_USB',channel:value=>value.kioskDirectCommand.productChannel='labs',
  original:value=>value.kioskDirectCommand.originalRequestId='d1234567-1234-4234-8234-123456789abc',logical:value=>value.kioskDirectCommand.logicalRequestId='kiosk_'+'a'.repeat(32),
  command:value=>value.kioskDirectCommand.command='show-controls',value:value=>value.kioskDirectCommand.value='wifi-on',operation:value=>value.kioskDirectCommand.operation='requestResult',
  receiptAction:value=>value.kioskDirectCommand.coreReceipt.action='requestResult',receiptId:value=>value.kioskDirectCommand.coreReceipt.requestId='kiosk_'+'a'.repeat(32),
  receiptSuffix:value=>value.kioskDirectCommand.coreReceipt.actionId+='\n',schema:value=>value.kioskDirectCommand.typedResult.schema='other',
  typedId:value=>value.kioskDirectCommand.typedResult.requestId='kiosk_'+'a'.repeat(32),typedCommand:value=>value.kioskDirectCommand.typedResult.command='show-controls',
  packageSuffix:value=>value.kioskDirectCommand.typedResult.selectedPackage+='\n',rawResult:value=>value.result={privatePath:'PRIVATE_SENTINEL'},mutation:value=>value.mutation={stage:'confirmed'}
};
for(const [field,change] of Object.entries(crossed))test('crossed '+field+' reply cannot acknowledge original',async t=>{
  const f=await fixture(t,{respond:(route,body,value)=>{change(value);return route.fulfill({json:value});}});await f.invoke();assert.ok((await f.stored()).original);assert.equal(await f.page.evaluate(()=>window.view.hasUnresolved()),true);assert.equal(await f.page.evaluate(()=>window.latest),undefined);
});

for(const state of ['pending','pending_wearer_action','confirmed','rejected','expired','cancelled','unknown'])test('lifecycle '+state+' never replaces typed original acknowledgement',async t=>{
  const f=await fixture(t,{respond:(route,body,value)=>{const d=value.kioskDirectCommand;d.typedResult=null;d.lifecycle={requestId:d.logicalRequestId,operationState:state,accepted:state!=='rejected',completed:state!=='pending',enqueuedAtMs:null,expiresAtMs:null};d.originalEffect='unknown';d.reconcilesOriginal=false;d.coreReceipt.stage=state==='unknown'?'failed':'confirmed';return route.fulfill({json:value});}});await f.invoke();assert.ok((await f.stored()).original);assert.match(await f.page.locator('#direct-command-lifecycle').textContent(),new RegExp(state));assert.match(await f.page.locator('#direct-command-state').textContent(),/remains unknown/);
});

for(const [name,state] of Object.entries({disconnected:{connected:false},noConnection:{connection:null},expired:{connection:{...connection(),expiresAtUtc:new Date(Date.now()-1000).toISOString()}},unsupported:{actions:[]},busy:{busy:true},otherUnknown:{externalUnresolved:true}}))test(name+' admission stays disabled even for read/helper Invoke',async t=>{
  const f=await fixture(t,{state});assert.equal(await f.page.locator('#direct-command-invoke-confirm').isDisabled(),true);await f.page.locator('#direct-command-invoke').evaluate(button=>{button.disabled=false;button.click();});assert.equal(f.calls.length,0);assert.equal(await f.stored(),null);
});

test('only eight commands and three canonical requirements exist and each approval is scope-bound',async t=>{
  const f=await fixture(t);assert.deepEqual(await f.page.locator('#direct-command-command option').evaluateAll(options=>options.map(o=>o.value)),commands);assert.deepEqual(await f.page.locator('#direct-command-requirement option').evaluateAll(options=>options.map(o=>o.value)),['any','wifi-on','wifi-off']);
  assert.equal(await f.page.locator('#direct-command-invoke').isDisabled(),true);await f.page.locator('#direct-command-invoke-confirm').check();await f.page.locator('#direct-command-command').selectOption('check-setup-helper');assert.equal(await f.page.locator('#direct-command-invoke-confirm').isChecked(),false);
  await f.page.locator('#direct-command-invoke-confirm').check();await f.update({authGeneration:2});assert.equal(await f.page.locator('#direct-command-invoke-confirm').isChecked(),false);assert.equal(f.calls.length,0);
});

for(const value of ['any','wifi-on','wifi-off'])test('launch preference '+value+' is sent literally without any Wi-Fi control',async t=>{
  const f=await fixture(t);await f.invoke('set-launch-requirement',value);assert.equal(f.calls[0].body.value,value);assert.equal(f.calls.length,1);assert.equal(await f.stored(),null);
});

for(const [code,dispatched,clears] of [['busy',false,true],['direct_connection_expired',false,true],['request_already_consumed',false,false],['busy',undefined,false],['unexpected_private_message',false,false]])test('error '+code+' dispatched='+dispatched+' preserves only definitive predispatch semantics',async t=>{
  const f=await fixture(t,{respond:route=>route.fulfill({status:409,json:{error:code,dispatched}})});await f.invoke();assert.equal(Boolean((await f.stored())?.original),!clears);assert.equal(await f.page.evaluate(()=>window.view.hasUnresolved()),!clears);assert.doesNotMatch(await f.page.locator('#direct-command-view').textContent(),/unexpected_private_message/);
});

for(const [name,patch] of Object.entries({auth:{authGeneration:2},disconnect:{connected:false},connection:{connection:{...connection(),connectionId:'d1234567-1234-4234-8234-123456789abc'}},otherUnknown:{externalUnresolved:true}}))test('late Invoke reply after '+name+' preserves exact original until explicit cached GET',async t=>{
  let release,held;const received=new Promise(resolve=>release=resolve);const f=await fixture(t,{respond:(route,body,value)=>{held=()=>route.fulfill({json:value});release();}});
  const running=f.invoke();await received;const before=await f.stored();await f.update(patch);await held();await running;assert.deepEqual(await f.stored(),before);
  await f.update({connected:true,externalUnresolved:false});await f.page.locator('#direct-command-original-status').click();await f.page.waitForFunction(()=>!window.view.hasUnresolved());assert.equal(f.calls.filter(c=>c.method==='POST').length,1);
});

test('original GET ignores current form and connection expiry but refuses a crossed authentication generation',async t=>{
  let release,held,first=true;const received=new Promise(resolve=>release=resolve);const f=await fixture(t,{respond:route=>route.abort('failed'),status:(route,value)=>{if(first){first=false;held=()=>route.fulfill({json:value});release();return;}return route.fulfill({json:value});}});
  await f.invoke();const before=await f.stored();await f.page.locator('#direct-command-original-status').click();await received;await f.update({authGeneration:2});await held();await f.page.waitForFunction(()=>window.busyCalls.length>=3);assert.deepEqual(await f.stored(),before);
  await f.page.locator('#direct-command-command').selectOption('launch-kiosk');await f.update({connection:{...f.scope,expiresAtUtc:new Date(Date.now()-1000).toISOString()}});assert.equal(await f.page.locator('#direct-command-request-result').isDisabled(),true);await f.page.locator('#direct-command-original-status').click();await f.page.waitForFunction(()=>!window.view.hasUnresolved());assert.equal(f.calls.filter(c=>c.method==='POST').length,1);
});

test('cached exact acknowledgement during pending POST preserves busy and late lost reply never recreates original',async t=>{
  let release,held;const received=new Promise(resolve=>release=resolve);const f=await fixture(t,{respond:route=>{held=()=>route.abort('failed');release();}});
  const running=f.invoke();await received;assert.equal(await f.page.evaluate(()=>window.ctx.busy),true);await f.page.locator('#direct-command-original-status').click();await f.page.waitForFunction(()=>!window.view.hasUnresolved());assert.equal(await f.page.evaluate(()=>window.ctx.busy),true);assert.equal(await f.page.evaluate(()=>window.busyCalls.at(-1)),true);
  await held();await running;assert.equal(await f.stored(),null);assert.equal(await f.page.evaluate(()=>window.siblingBlocked),false);assert.equal(f.calls.filter(c=>c.method==='POST').length,1);
});

test('missing cached record and lost native read preserve the original without new logical requests',async t=>{
  const f=await fixture(t,{respond:route=>route.abort('failed'),status:route=>route.fulfill({status:404,json:{error:'request_not_found',dispatched:false}})});await f.invoke();const before=await f.stored();await f.page.locator('#direct-command-original-status').click();await f.page.waitForFunction(()=>window.busyCalls.length>=3);await f.recovery('request-result');assert.deepEqual(await f.stored(),before);assert.equal(f.calls.filter(c=>c.body?.action==='kiosk.direct.invoke').length,1);assert.match(await f.page.locator('#direct-command-state').textContent(),/unchanged/);
});

for(const field of ['state','stage','lifecycle','flag','logical'])test('uncertain cancel '+field+' never clears either saved operation',async t=>{
  const f=await fixture(t,{respond:(route,body,value)=>{if(body.action==='kiosk.direct.invoke')return route.abort('failed');const d=value.kioskDirectCommand;if(field==='state')value.state='outcomeUnknown';if(field==='stage')d.coreReceipt.stage='confirmed';if(field==='lifecycle')d.lifecycle.operationState='confirmed';if(field==='flag')d.cancellationAcknowledged=false;if(field==='logical')d.logicalRequestId='kiosk_'+'a'.repeat(32);return route.fulfill({json:value});}});await f.invoke();await f.cancel();const both=await f.stored();assert.ok(both.original);assert.ok(both.cancellation);assert.equal(await f.page.locator('#direct-command-cancel-confirm').isDisabled(),true);assert.equal(f.calls.length,2);
});

test('storage failure blocks Invoke and cancellation before any POST',async t=>{
  const f=await fixture(t,{storageFailure:true});await f.page.locator('#direct-command-invoke-confirm').check();await f.page.locator('#direct-command-invoke').click();assert.equal(f.calls.length,0);assert.equal(await f.page.evaluate(()=>window.view.hasUnresolved()),true);assert.match(await f.page.locator('#direct-command-state').textContent(),/storage unavailable/);
});

test('private fields are stripped from persisted references and displayed typed projections',async t=>{
  const sentinel='PRIVATE_SENTINEL';const f=await fixture(t,{respond:(route,body,value)=>{const d=value.kioskDirectCommand;d.endpoint=sentinel;d.RawJson=sentinel;d.coreReceipt.message=sentinel;d.typedResult.rawJson=sentinel;d.typedResult.selectedEntry.path=sentinel;return route.fulfill({json:value});}});await f.invoke();assert.doesNotMatch(await f.page.locator('body').textContent(),new RegExp(sentinel));assert.doesNotMatch(await f.page.evaluate(()=>JSON.stringify(window.latest)),new RegExp(sentinel));assert.equal(await f.stored(),null);
  const reference={...f.scope,requestId:'c1234567-1234-4234-8234-123456789abc',action:'kiosk.direct.invoke',logicalRequestId:'kiosk_c1234567123442348234123456789abc',command:'status',value:null,endpoint:sentinel,key:sentinel};
  const restored=await fixture(t,{saved:{original:reference,cancellation:null,raw:sentinel}});assert.doesNotMatch(JSON.stringify(await restored.stored()),new RegExp(sentinel));assert.doesNotMatch(JSON.stringify(await restored.stored()),/expiresAtUtc/);assert.equal(restored.calls.length,0);
});

for(const invalid of ['not-json',{original:{requestId:'PRIVATE_SENTINEL'},cancellation:null},{original:null,cancellation:null}])test('damaged reference fails closed with only sanitized custody marker '+JSON.stringify(invalid),async t=>{
  const f=await fixture(t,{saved:invalid});assert.deepEqual(await f.stored(),{original:null,cancellation:null,damaged:true});assert.equal(await f.page.evaluate(()=>window.view.hasUnresolved()),true);assert.equal(await f.page.evaluate(()=>window.view.getCleanupReference()),null);assert.equal(f.calls.length,0);
});

test('desktop and mobile keep literal selected keys and long connection identity inside the card',async t=>{
  const output=path.resolve(__dirname,'../../local/direct-command-ui-review');await fs.mkdir(output,{recursive:true});
  for(const width of [1280,390]){
    const literal='<img src=x onerror=window.injected=true>'+'X'.repeat(100),f=await fixture(t,{width,respond:(route,body,value)=>{const d=value.kioskDirectCommand.typedResult;d.selectedKey=literal;d.selectedEntry.key=literal;return route.fulfill({json:value});}});
    await f.invoke();assert.equal(await f.page.evaluate(()=>window.injected),undefined);assert.match(await f.page.locator('#direct-command-facts').textContent(),/onerror=window.injected/);assert.equal(await f.page.locator('#direct-command-facts img').count(),0);
    assert.equal(await f.page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth),true);
    await f.update({connection:{...f.scope,serial:'USB_'+'A'.repeat(240)}});assert.equal(await f.page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth),true);
    await f.page.locator('#direct-command-view').screenshot({path:path.join(output,'command-'+width+'.png')});
  }
});

test('keyboard approval permits one explicit helper request and no implicit lifecycle calls',async t=>{
  const f=await fixture(t);await f.page.locator('#direct-command-command').selectOption('check-setup-helper');await f.page.locator('#direct-command-invoke-confirm').focus();await f.page.keyboard.press('Space');await f.page.keyboard.press('Tab');assert.equal(await f.page.locator('#direct-command-invoke').evaluate(button=>document.activeElement===button),true);await f.page.keyboard.press('Enter');await f.page.waitForFunction(()=>window.busyCalls.length>=2);assert.equal(f.calls.length,1);assert.equal(f.calls[0].body.command,'check-setup-helper');
});

test('OS offline remains advisory for an explicitly connected local Direct scope',async t=>{
  const f=await fixture(t);await f.page.evaluate(()=>Object.defineProperty(navigator,'onLine',{value:false,configurable:true}));await f.invoke('status');assert.equal(f.calls.length,1);assert.equal(await f.stored(),null);
});

test('saved reference download contains only original metadata and never typed results',async t=>{
  const f=await fixture(t,{respond:route=>route.abort('failed')});await f.invoke();const download=f.page.waitForEvent('download');await f.page.locator('#direct-command-save-reference').click();const file=await download;const content=JSON.parse(await fs.readFile(await file.path(),'utf8'));assert.deepEqual(content,await f.stored());assert.equal(file.suggestedFilename(),'qfm-direct-command-reference.json');assert.doesNotMatch(JSON.stringify(content),/typedResult|coreReceipt|lifecycle|endpoint|RawJson/);assert.equal(f.calls.length,1);
});

test('a completed request is not relabelled as a typed acknowledgement for a different connection or command',async t=>{
  const f=await fixture(t);await f.invoke();await f.update({connection:{...f.scope,connectionId:'d1234567-1234-4234-8234-123456789abc'}});assert.doesNotMatch(await f.page.locator('#direct-command-state').textContent(),/Exact typed original request acknowledged/);assert.match(await f.page.locator('#direct-command-facts').textContent(),/No typed original result/);assert.equal(f.calls.length,1);
});

test('initial HTML disables every effect and recovery control without JavaScript',async t=>{
  const context=await browser.newContext({javaScriptEnabled:false});t.after(()=>context.close());const page=await context.newPage();await context.route('**/*',async route=>{const name=new URL(route.request().url()).pathname==='/'?'index.html':'styles.css';return route.fulfill({contentType:name.endsWith('.css')?'text/css':'text/html',body:await fs.readFile(path.join(root,name))});});await page.goto(origin);
  assert.equal(await page.locator('#direct-command-view button:not([disabled]),#direct-command-view input:not([disabled]),#direct-command-view select:not([disabled])').count(),0);
});

test('local expiry during an already sent Invoke does not reject its bound actual acknowledgement',async t=>{
  let held,release;const received=new Promise(resolve=>release=resolve);const f=await fixture(t,{respond:(route,body,value)=>{held=()=>route.fulfill({json:value});release();}});const running=f.invoke();await received;
  await f.page.evaluate(()=>{const now=Date.now();Date.now=()=>now+600000;window.view.update();});await held();await running;assert.equal(await f.stored(),null);assert.match(await f.page.locator('#direct-command-state').textContent(),/acknowledged/);assert.match(await f.page.locator('#direct-command-boundary').textContent(),/expired/);assert.equal(f.calls.length,1);
});

test('changed host preserves both references and offers no native substitute or cached lookup',async t=>{
  const f=await fixture(t,{respond:route=>route.abort('failed')});await f.invoke();await f.cancel();const before=await f.stored();await f.update({sessionId:'other-host',connection:{...f.scope,sessionId:'other-host'}});
  for(const id of ['invoke-confirm','cancel-confirm','request-status','request-result','original-status','cancellation-status'])assert.equal(await f.page.locator('#direct-command-'+id).isDisabled(),true,id);
  assert.deepEqual(await f.stored(),before);assert.equal(f.calls.length,2);assert.deepEqual(await f.page.evaluate(()=>window.view.getCleanupReference()),{sessionId:'session-a',connectionId:f.scope.connectionId});
});

test('native read reply crossing connection or form change cannot clear the original',async t=>{
  let held,release;const received=new Promise(resolve=>release=resolve);const f=await fixture(t,{respond:(route,body,value)=>{if(body.action==='kiosk.direct.invoke')return route.abort('failed');held=()=>route.fulfill({json:value});release();}});await f.invoke();const before=await f.stored(),running=f.recovery('request-result');await received;await f.page.locator('#direct-command-command').selectOption('launch-normal');await held();await running;assert.deepEqual(await f.stored(),before);assert.equal(await f.page.evaluate(()=>window.view.hasUnresolved()),true);
});

test('cancel definitive predispatch rejection clears only its newly saved cancel reference',async t=>{
  const f=await fixture(t,{respond:(route,body)=>body.action==='kiosk.direct.invoke'?route.abort('failed'):route.fulfill({status:409,json:{error:'busy',dispatched:false}})});await f.invoke();const before=(await f.stored()).original;await f.cancel();assert.deepEqual((await f.stored()).original,before);assert.equal((await f.stored()).cancellation,null);assert.equal(await f.page.evaluate(()=>window.view.hasUnresolved()),true);assert.equal(f.calls.length,2);
});

test('owner-valid numeric package segment acknowledges both the original and explicit typed recovery literally',async t=>{
  const packageName='com.example.123';let lose=true;
  const f=await fixture(t,{respond:(route,body,value)=>{if(value.kioskDirectCommand.typedResult)value.kioskDirectCommand.typedResult.selectedPackage=packageName;if(lose&&body.action==='kiosk.direct.invoke'){lose=false;return route.abort('failed');}return route.fulfill({json:value});}});
  await f.invoke();assert.ok((await f.stored()).original);await f.recovery('request-result');assert.equal(await f.stored(),null);assert.equal(await f.page.evaluate(()=>window.latest.value.typedResult.selectedPackage),packageName);
  await f.invoke();assert.equal(await f.stored(),null);assert.match(await f.page.locator('#direct-command-facts').textContent(),/com\.example\.123/);assert.equal(f.calls.filter(call=>call.body?.action==='kiosk.direct.invoke').length,2);
});

for(const packageName of ['com..123','.com.example','com.example.','com.example.12-3',' com.example.123','com.example.123\n','x'.repeat(255)+'.y'])test('malformed or oversized package token remains unknown '+JSON.stringify(packageName),async t=>{
  const f=await fixture(t,{respond:(route,body,value)=>{value.kioskDirectCommand.typedResult.selectedPackage=packageName;return route.fulfill({json:value});}});await f.invoke();assert.ok((await f.stored()).original);assert.equal(await f.page.evaluate(()=>window.latest),undefined);assert.equal(f.calls.length,1);
});
