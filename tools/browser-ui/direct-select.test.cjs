// Actual shipped modules/DOM, fully intercepted headless browser; no server or device.
const test=require('node:test'),assert=require('node:assert/strict'),fs=require('node:fs/promises'),path=require('node:path');
const {chromium}=require('playwright');
const root=path.resolve(__dirname,'../../src/QuestIonAbleFileManager.BrowserHost/wwwroot'),origin='http://127.0.0.1:18148',storageKey='qfm-browser-direct-command-v1';
const logical=id=>'kiosk_'+id.replaceAll('-',''),provider=id=>'pc-'+id.replaceAll('-','');
const connection=()=>({sessionId:'session-a',connectionId:'a1234567-1234-4234-8234-123456789abc',connectRequestId:'b1234567-1234-4234-8234-123456789abc',usbOperationId:'usb_b1234567123442348234123456789abc',serial:'USB_TARGET',productChannel:'stable',expiresAtUtc:new Date(Date.now()+300000).toISOString()});
const rows=()=>['alpha','beta'].map(name=>({key:'package:com.example.'+name,name,packageName:'com.example.'+name,installed:true,launchable:true,selectable:true}));
function catalog(body){return {requestId:body.requestId,action:body.action,state:'completed',result:null,mutation:{operationId:provider(body.requestId),commandKind:'openRustyKioskCatalog',target:body.serial,desiredState:`Rusty Kiosk installation inspected; catalog requested if available (${body.productChannel})`,stage:'confirmed',headsetReadback:true,transitions:[]},kiosk:{serial:body.serial,productChannel:body.productChannel,operation:'catalog.open',providerRequestId:provider(body.requestId),providerState:'completed',activityOpenRequested:true,installation:null,result:{providerRequestId:provider(body.requestId),command:'status',accepted:true,completed:true,selectedKey:null,selectedName:null,selectedPackage:null,selectedInstalled:false,selectedLaunchable:false,installedCount:2,notInstalledCount:0,visibleCount:2,visibleEntriesTruncated:false,projectionTruncated:false,entries:rows()},catalogRequestId:body.requestId,reviewedKey:null,reviewedPackage:null,originalFactsOnly:false}};}
function direct(body,scope,records){
  const invoke=body.action==='kiosk.direct.invoke',cancel=body.action==='kiosk.direct.request-cancel',operation=invoke?'invoke':cancel?'requestCancel':body.action.endsWith('request-status')?'requestStatus':'requestResult';
  const original=invoke?{...scope,originalRequestId:body.requestId,logicalRequestId:logical(body.requestId),command:body.command,value:body.value,selectionReview:{catalogRequestId:body.catalogRequestId,serial:scope.serial,productChannel:scope.productChannel,key:body.value,packageName:rows().find(r=>r.key===body.value)?.packageName}}:records.get(body.originalRequestId).kioskDirectCommand;
  const review=original.selectionReview,typed={schema:'rusty.kiosk.cli_result.v1',requestId:original.logicalRequestId,command:original.command,accepted:true,completed:true,controlsOpen:false,selectedKey:review.key,selectedPackage:review.packageName,selectedInstalled:true,selectedLaunchable:true,selectedLaunchRequirement:'any',pendingRequirementLaunch:false,guardArmed:false,setupHelperInstalled:false,setupHelperReady:false,selectedEntry:{key:review.key,launchRequirement:'any',packageName:review.packageName,installed:true,launchable:true,selectable:true}};
  const d={...original,operation,originalEffect:invoke||operation==='requestResult'?'confirmed':'unknown',reconcilesOriginal:invoke||operation==='requestResult',coreReceipt:{actionId:'action_'+body.requestId.replaceAll('-',''),action:cancel?'cancelRequest':operation,requestId:original.logicalRequestId,stage:cancel?'cancelled':'confirmed'},lifecycle:cancel||operation==='requestStatus'?{requestId:original.logicalRequestId,operationState:cancel?'cancelled':'confirmed',accepted:true,completed:true,enqueuedAtMs:1,expiresAtMs:2}:null,typedResult:invoke||operation==='requestResult'?typed:null,cancellationAcknowledged:cancel};
  return {requestId:body.requestId,action:body.action,state:'completed',result:null,mutation:null,kioskDirectCommand:d};
}
let browser;test.before(async()=>{browser=await chromium.launch({headless:true});});test.after(async()=>browser?.close());
async function fixture(t,{respond,status,saved}={}){
  const context=await browser.newContext({serviceWorkers:'block',viewport:{width:1280,height:1100}});t.after(()=>context.close());const page=await context.newPage(),scope=connection(),calls=[],records=new Map(),errors=[];
  page.setDefaultTimeout(5000);page.on('pageerror',e=>errors.push(e.message));
  if(saved)await context.addInitScript(({storageKey,saved})=>sessionStorage.setItem(storageKey,JSON.stringify(saved)),{storageKey,saved});
  await context.route('**/*',async route=>{
    const url=new URL(route.request().url());if(url.origin!==origin)return route.abort('blockedbyclient');
    if(url.pathname.startsWith('/browser/')){
      const body=route.request().method()==='POST'?route.request().postDataJSON():null,call={body,path:url.pathname};calls.push(call);
      if(body){const value=body.action==='kiosk.catalog.open'?catalog(body):direct(body,scope,records);records.set(body.requestId,value);call.saved=await page.evaluate(key=>JSON.parse(sessionStorage.getItem(key)||'null'),storageKey);return respond?respond(route,body,value):route.fulfill({json:value});}
      const value=records.get(url.pathname.split('/').at(-1));return status?status(route,value):route.fulfill({json:value});
    }
    if(url.pathname==='/fixture.js')return route.fulfill({contentType:'text/javascript',body:`import {createKioskView} from './kiosk-view.js'; import {createDirectCommandView} from './direct-command-view.js';
      window.ctx=${JSON.stringify({sessionId:'session-a',connected:true,busy:false,authGeneration:1,selected:{serial:'USB_TARGET',isReady:true},connection:scope,externalUnresolved:false,actions:['kiosk.catalog.open','kiosk.select','kiosk.direct.invoke','kiosk.direct.request-status','kiosk.direct.request-result','kiosk.direct.request-cancel']})}; window.ctx.actions=new Set(window.ctx.actions);window.contextReads=0;window.callbacks=0;
      const api=async(path,options)=>{const r=await fetch(path,options);const v=await r.json();if(!r.ok)throw Object.assign(new Error(v.error),v);return v;};
      window.controls=()=>{window.kiosk?.update();window.view?.update();};const setBusy=value=>{window.ctx.busy=value;window.controls();};
      window.kiosk=createKioskView({api,getContext:()=>{window.contextReads++;return window.ctx;},setBusy,showResult:()=>{},onSelectionSourceChange:()=>{window.callbacks++;window.view?.selectionSourceChanged();}});
      window.view=createDirectCommandView({api,getContext:()=>{window.contextReads++;return {...window.ctx,reviewedSelection:window.overrideReview===undefined?window.kiosk.getReviewedSelection():window.overrideReview};},setBusy,showResult:(value,summary)=>window.latest={value,summary}});window.controls();window.ready=true;`});
    const name=url.pathname==='/'?'index.html':url.pathname.slice(1);if(!['index.html','styles.css','kiosk-view.js','direct-command-view.js'].includes(name))return route.fulfill({status:404,body:''});
    let body=await fs.readFile(path.join(root,name));if(name==='index.html')body=Buffer.from(body.toString().replace(/<script[^>]*src="\.\/pwa.js"[^>]*><\/script>/,'').replace(/<script[^>]*src="\.\/app.js"[^>]*><\/script>/,'<script type="module" src="./fixture.js"></script>'));
    return route.fulfill({contentType:name.endsWith('.js')?'text/javascript':name.endsWith('.css')?'text/css':'text/html',body});
  });
  await page.goto(origin);await page.waitForFunction(()=>window.ready);
  const open=async()=>{await page.locator('#kiosk-open-confirm').check();await page.locator('#kiosk-open').click();await page.waitForFunction(()=>!window.ctx.busy);};
  const highlight=async(index=0)=>{await page.locator('#kiosk-entries button').nth(index).click();};
  const setup=async()=>{await open();await highlight();await page.locator('#direct-command-command').selectOption('select');};
  const invoke=async()=>{await page.locator('#direct-command-invoke-confirm').check();await page.locator('#direct-command-invoke').click();await page.waitForFunction(()=>!window.ctx.busy);};
  const stored=()=>page.evaluate(key=>JSON.parse(sessionStorage.getItem(key)||'null'),storageKey);
  const update=patch=>page.evaluate(patch=>{Object.assign(window.ctx,patch);window.controls();},patch);
  t.after(()=>assert.deepEqual(errors,[],'no page errors or getter recursion'));return {page,scope,calls,records,open,highlight,setup,invoke,stored,update};
}

test('reviewed source getter is pure and defensive and callbacks never run inside update',async t=>{
  const f=await fixture(t);await f.setup();
  const proof=await f.page.evaluate(()=>{const before=window.contextReads,callbacks=window.callbacks,review=window.kiosk.getReviewedSelection();review.key='changed';const second=window.kiosk.getReviewedSelection();const reads=window.contextReads-before;window.controls();return {reads,second,callbacksAfter:window.callbacks,callbacks};});
  assert.equal(proof.reads,0);assert.equal(proof.second.key,rows()[0].key);assert.equal(proof.callbacksAfter,proof.callbacks);assert.equal(f.calls.length,1);
});

test('Select has no key editor and persists immutable complete review before its sole Invoke',async t=>{
  const f=await fixture(t);await f.page.locator('#direct-command-command').selectOption('select');assert.equal(await f.page.locator('#direct-command-invoke-confirm').isDisabled(),true);
  await f.setup();assert.equal(await f.page.locator('#direct-command-text-field').isHidden(),true);assert.match(await f.page.locator('#direct-command-selection-review').textContent(),/com.example.alpha.*catalog source/);
  await f.invoke();const call=f.calls[1],ref=call.saved.original;
  assert.deepEqual(call.body,{requestId:ref.requestId,action:'kiosk.direct.invoke',connectionId:f.scope.connectionId,command:'select',value:rows()[0].key,confirmed:true,catalogRequestId:f.calls[0].body.requestId});
  assert.deepEqual(ref.selectionReview,{catalogRequestId:f.calls[0].body.requestId,serial:'USB_TARGET',productChannel:'stable',key:rows()[0].key,packageName:rows()[0].packageName});
  assert.equal(await f.stored(),null);assert.match(await f.page.locator('#direct-command-state').textContent(),/acknowledged.*selection/i);assert.equal(f.calls.filter(c=>c.body?.action==='kiosk.direct.invoke').length,1);
});

for(const field of ['key','packageName','catalogRequestId'])test('source '+field+' changes revoke consent',async t=>{
  const f=await fixture(t);await f.setup();await f.page.locator('#direct-command-invoke-confirm').check();
  await f.page.evaluate(field=>{window.overrideReview={...window.kiosk.getReviewedSelection(),[field]:field==='catalogRequestId'?'c1234567-1234-4234-8234-123456789abc':field==='packageName'?'com.example.changed':'changed-key'};window.view.update();},field);
  assert.equal(await f.page.locator('#direct-command-invoke-confirm').isChecked(),false);assert.equal(f.calls.length,1);
});

test('explicit row/filter/channel events update Direct and revoke approval without network',async t=>{
  const f=await fixture(t);await f.setup();await f.page.locator('#direct-command-invoke-confirm').check();await f.highlight(1);
  assert.equal(await f.page.locator('#direct-command-invoke-confirm').isChecked(),false);assert.match(await f.page.locator('#direct-command-selection-review').textContent(),/com.example.beta/);
  await f.page.locator('#direct-command-invoke-confirm').check();await f.page.locator('#kiosk-filter').fill('alpha');assert.equal(await f.page.locator('#direct-command-invoke-confirm').isDisabled(),true);
  await f.page.locator('#kiosk-channel').selectOption('labs');assert.equal(await f.page.locator('#direct-command-invoke-confirm').isDisabled(),true);assert.equal(f.calls.length,1);
});

test('same eligible row filter changes still revoke Select approval',async t=>{
  const f=await fixture(t);await f.setup();await f.page.locator('#direct-command-invoke-confirm').check();await f.page.locator('#kiosk-filter').fill('alpha');
  assert.equal(await f.page.locator('#direct-command-invoke-confirm').isChecked(),false);assert.equal(await f.page.locator('#direct-command-invoke-confirm').isDisabled(),false);assert.equal(f.calls.length,1);
});

for(const variant of ['session','serial','channel','expired','auth'])test('current '+variant+' scope cannot borrow review approval',async t=>{
  const f=await fixture(t);await f.setup();await f.page.locator('#direct-command-invoke-confirm').check();
  if(variant==='session')await f.update({sessionId:'session-b'});
  else if(variant==='auth')await f.update({authGeneration:2});
  else await f.update({connection:{...f.scope,...(variant==='serial'?{serial:'OTHER_TARGET'}:variant==='channel'?{productChannel:'labs'}:{expiresAtUtc:new Date(Date.now()-1000).toISOString()})}});
  assert.equal(await f.page.locator('#direct-command-invoke-confirm').isChecked(),false);
  if(variant!=='auth')assert.equal(await f.page.locator('#direct-command-invoke-confirm').isDisabled(),true);assert.equal(f.calls.length,1);
});

for(const variant of ['review-source','review-package','key','package','installed','launchable','row-null','row-key','row-package','row-installed','row-launchable','row-selectable','receipt','value'])test('Select refuses crossed '+variant+' while retaining original',async t=>{
  const f=await fixture(t,{respond:(route,body,value)=>{
    if(body.action==='kiosk.direct.invoke'){
      const d=value.kioskDirectCommand,r=d.typedResult;
      if(variant==='review-source')d.selectionReview.catalogRequestId='c1234567-1234-4234-8234-123456789abc';
      else if(variant==='review-package')d.selectionReview.packageName='com.example.other';
      else if(variant==='key')r.selectedKey='other-key';else if(variant==='package')r.selectedPackage='com.example.other';
      else if(variant==='installed')r.selectedInstalled=false;else if(variant==='launchable')r.selectedLaunchable=false;
      else if(variant==='row-null')r.selectedEntry=null;else if(variant==='row-key')r.selectedEntry.key='other-key';
      else if(variant==='row-package')r.selectedEntry.packageName='com.example.other';
      else if(variant==='row-installed')r.selectedEntry.installed=false;else if(variant==='row-launchable')r.selectedEntry.launchable=false;else if(variant==='row-selectable')r.selectedEntry.selectable=false;
      else if(variant==='receipt')d.coreReceipt.requestId='kiosk_crossed';else d.value='other-key';
    }return route.fulfill({json:value});
  }});
  await f.setup();await f.invoke();assert.equal((await f.stored()).original.command,'select');assert.equal(f.calls.filter(c=>c.body?.action==='kiosk.direct.invoke').length,1);
});

test('stored original recovery ignores changed current catalog and cancellation retains full Select after original resolves',async t=>{
  const f=await fixture(t,{respond:(route,body,value)=>['kiosk.direct.invoke','kiosk.direct.request-cancel'].includes(body.action)?route.abort('failed'):route.fulfill({json:value})});
  await f.setup();await f.invoke();const original=(await f.stored()).original;await f.highlight(1);
  await f.page.locator('#direct-command-cancel-confirm').check();await f.page.locator('#direct-command-cancel').click();await f.page.waitForFunction(()=>!window.ctx.busy);
  const cancellation=(await f.stored()).cancellation;assert.deepEqual(cancellation.originalSelect,original);
  await f.page.locator('#direct-command-request-result').click();await f.page.waitForFunction(()=>!window.ctx.busy);assert.equal((await f.stored()).original,null);assert.deepEqual((await f.stored()).cancellation,cancellation);
  assert.deepEqual(Object.keys(f.calls.at(-1).body).sort(),['requestId','action','connectionId','originalRequestId'].sort());
  await f.page.reload();await f.page.waitForFunction(()=>window.ready);assert.equal(await f.page.evaluate(()=>window.kiosk.getReviewedSelection()),null);
  await f.page.locator('#direct-command-cancellation-status').click();await f.page.waitForFunction(()=>!window.view.hasUnresolved());assert.equal(await f.stored(),null);
  assert.equal(f.calls.filter(c=>c.body?.action==='kiosk.direct.invoke').length,1);
});

test('cancellation cached crossed Select review stays unresolved after original is gone',async t=>{
  const f=await fixture(t,{respond:(route,body,value)=>['kiosk.direct.invoke','kiosk.direct.request-cancel'].includes(body.action)?route.abort('failed'):route.fulfill({json:value}),status:(route,value)=>{value.kioskDirectCommand.selectionReview.packageName='com.example.crossed';return route.fulfill({json:value});}});
  await f.setup();await f.invoke();await f.page.locator('#direct-command-cancel-confirm').check();await f.page.locator('#direct-command-cancel').click();await f.page.waitForFunction(()=>!window.ctx.busy);
  await f.page.locator('#direct-command-request-result').click();await f.page.waitForFunction(()=>!window.ctx.busy);const prior=await f.stored();assert.equal(prior.original,null);
  await f.page.locator('#direct-command-cancellation-status').click();await f.page.waitForTimeout(80);assert.deepEqual(await f.stored(),prior);
});

for(const change of ['source','auth','connection'])test('late Select response after '+change+' change requires explicit cached read',async t=>{
  let release;const wait=new Promise(resolve=>release=resolve);
  const f=await fixture(t,{respond:async(route,body,value)=>{if(body.action==='kiosk.direct.invoke')await wait;return route.fulfill({json:value});}});
  await f.setup();await f.page.locator('#direct-command-invoke-confirm').check();await f.page.locator('#direct-command-invoke').click();await f.page.waitForFunction(()=>window.ctx.busy);
  if(change==='source')await f.highlight(1);else if(change==='auth')await f.update({authGeneration:2});else await f.update({connection:{...f.scope,serial:'OTHER_TARGET'}});
  release();await f.page.waitForFunction(()=>!window.ctx.busy);assert.equal((await f.stored()).original.command,'select');assert.equal(f.calls.length,2);
});
