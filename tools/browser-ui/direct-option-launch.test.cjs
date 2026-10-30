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
  let original;
  if(!invoke)original=records.get(body.originalRequestId).kioskDirectCommand;
  else if(body.command==='select')original={...scope,originalRequestId:body.requestId,logicalRequestId:logical(body.requestId),command:body.command,value:body.value,selectionReview:{catalogRequestId:body.catalogRequestId,serial:scope.serial,productChannel:scope.productChannel,key:body.value,packageName:rows().find(r=>r.key===body.value)?.packageName}};
  else {const source=records.get(body.optionsRequestId).kioskDirectCommand;original={...scope,originalRequestId:body.requestId,logicalRequestId:logical(body.requestId),command:body.command,value:body.value,launchReview:{optionsRequestId:body.optionsRequestId,selectOriginalRequestId:source.originalRequestId,catalogRequestId:source.selectionReview.catalogRequestId,key:source.selectionReview.key,packageName:source.selectionReview.packageName,bindingSha256:source.typedResult.launchOptions.bindingSha256}};}
  const selected=original.command==='select',review=selected?original.selectionReview:original.launchReview;
  const typed={schema:'rusty.kiosk.cli_result.v1',requestId:original.logicalRequestId,command:original.command,accepted:true,completed:true,controlsOpen:false,selectedKey:selected?review.key:null,selectedPackage:review.packageName,selectedInstalled:selected,selectedLaunchable:selected,selectedLaunchRequirement:'any',pendingRequirementLaunch:false,guardArmed:false,setupHelperInstalled:false,setupHelperReady:false,selectedEntry:selected?{key:review.key,launchRequirement:'any',packageName:review.packageName,installed:true,launchable:true,selectable:true}:null};
  if(selected)typed.launchOptions={state:'ready',packageName:review.packageName,bindingSha256:'a'.repeat(64),options:[{schemaVersion:1,optionId:'  opaque\toption\n  ',displayLabel:'<Example option>',description:'<img src=x> stays literal.'},{schemaVersion:1,optionId:'\ufeff',displayLabel:'FEFF option',description:''}]};
  else {typed.lastDispatchedOptionId=original.value;typed.lastDispatchedOptionPackage=review.packageName;}
  const d={...original,operation,originalEffect:invoke||operation==='requestResult'?'confirmed':'unknown',reconcilesOriginal:invoke||operation==='requestResult',coreReceipt:{actionId:'action_'+body.requestId.replaceAll('-',''),action:cancel?'cancelRequest':operation,requestId:original.logicalRequestId,stage:cancel?'cancelled':'confirmed'},lifecycle:cancel||operation==='requestStatus'?{requestId:original.logicalRequestId,operationState:cancel?'cancelled':'confirmed',accepted:true,completed:true,enqueuedAtMs:1,expiresAtMs:2}:null,typedResult:invoke||operation==='requestResult'?typed:null,cancellationAcknowledged:cancel};
  return {requestId:body.requestId,action:body.action,state:'completed',result:null,mutation:null,kioskDirectCommand:d};
}
let browser;test.before(async()=>{browser=await chromium.launch({headless:true});});test.after(async()=>browser?.close());
async function fixture(t,{respond,status,saved,width=1280}={}){
  const context=await browser.newContext({serviceWorkers:'block',viewport:{width,height:1100}});t.after(()=>context.close());const page=await context.newPage(),scope=connection(),calls=[],records=new Map(),errors=[];
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

const opaque='  opaque\toption\n  ';
async function prepared(f,{recovered=false,choice='0'}={}){
  await f.setup();await f.invoke();
  if(recovered){await f.page.locator('#direct-command-request-result').click();await f.page.waitForFunction(()=>!window.ctx.busy);}
  await f.page.locator('#direct-command-command').selectOption('launch-option');
  if(choice!==null)await f.page.locator('#direct-command-option-choice').selectOption(choice);
}

test('shipped Direct exposes one reviewed command and explicit returned-option chooser',async t=>{
  const f=await fixture(t);assert.equal(await f.page.locator('#direct-command-command option[value="launch-option"]').count(),1);assert.equal(await f.page.locator('#direct-command-option-choice').count(),1);assert.equal(f.calls.length,0);
});

for(const recovered of [false,true])test('one exact opaque current-selection dispatch persists compact review from '+(recovered?'recovered outer source':'original Select source'),async t=>{
  const f=await fixture(t,{respond:(route,body,value)=>recovered&&body.command==='select'?route.abort('failed'):route.fulfill({json:value})});await prepared(f,{recovered,choice:null});
  assert.equal(await f.page.locator('#direct-command-invoke-confirm').isDisabled(),true);assert.equal(await f.page.locator('#direct-command-option-choice').inputValue(),'');
  await f.page.locator('#direct-command-option-choice').selectOption('0');assert.equal(await f.page.locator('#direct-command-text-field').isHidden(),true);assert.equal(await f.page.locator('#direct-command-option-field input,#direct-command-option-field textarea').count(),0);
  assert.match(await f.page.locator('#direct-command-boundary').textContent(),/CURRENT Kiosk selection.*before execution.*not atomically.*after an effect/i);
  const selected=await f.page.evaluate(()=>window.view.getReviewedOption()),calls=f.calls.length;assert.equal(selected.value,opaque);await f.invoke();
  const call=f.calls.at(-1),ref=call.saved.original;assert.equal(f.calls.length,calls+1);assert.equal(await f.stored(),null);
  assert.deepEqual(call.body,{requestId:ref.requestId,action:'kiosk.direct.invoke',connectionId:f.scope.connectionId,command:'launch-option',value:opaque,confirmed:true,optionsRequestId:selected.launchReview.optionsRequestId});
  assert.deepEqual(Object.keys(ref.launchReview),['optionsRequestId','selectOriginalRequestId','catalogRequestId','key','packageName','bindingSha256']);assert.deepEqual(ref.launchReview,selected.launchReview);
  assert.equal(ref.value,opaque);assert.doesNotMatch(JSON.stringify(ref),/displayLabel|description|launchOptions|optionId|originalSelect/);
  const select=f.calls.find(c=>c.body?.command==='select');assert.equal(ref.launchReview.selectOriginalRequestId,select.body.requestId);
  if(recovered)assert.notEqual(ref.launchReview.optionsRequestId,select.body.requestId);else assert.equal(ref.launchReview.optionsRequestId,select.body.requestId);
  assert.equal(f.calls.filter(c=>c.body?.command==='launch-option').length,1);assert.match(await f.page.locator('#direct-command-state').textContent(),/acknowledged.*option ID.*not atomic/i);
});

test('returned-option getter is pure defensive and carries outer action separately',async t=>{
  const f=await fixture(t);await prepared(f);const before=f.calls.length;
  const proof=await f.page.evaluate(()=>{const reads=window.contextReads,a=window.view.getReviewedOption();a.launchReview.packageName='com.changed';a.value='changed';return {reads:window.contextReads-reads,second:window.view.getReviewedOption()};});
  assert.equal(proof.reads,0);assert.equal(proof.second.value,opaque);assert.equal(proof.second.launchReview.packageName,'com.example.alpha');assert.equal(proof.second.optionsAction,'kiosk.direct.invoke');assert.equal(f.calls.length,before);
});

test('explicit picker handles FEFF-only ID without trimming and labels stay literal',async t=>{
  const f=await fixture(t);await prepared(f,{choice:'1'});assert.equal(await f.page.locator('#direct-command-option-choice option').nth(1).textContent(),'<Example option> · '+JSON.stringify(opaque));
  assert.equal(await f.page.locator('#direct-command-option-choice img').count(),0);await f.invoke();assert.equal(f.calls.at(-1).body.value,'\ufeff');assert.equal(await f.stored(),null);
});

for(const change of ['choice','filter','row','channel','auth','connection','implicit-source'])test('new choice consent is revoked by '+change+' without requests',async t=>{
  const f=await fixture(t);await prepared(f);const count=f.calls.length;await f.page.locator('#direct-command-invoke-confirm').check();
  if(change==='choice')await f.page.locator('#direct-command-option-choice').selectOption('1');else if(change==='filter')await f.page.locator('#kiosk-filter').fill('alpha');else if(change==='row')await f.highlight(1);
  else if(change==='channel')await f.page.locator('#kiosk-channel').selectOption('labs');else if(change==='auth')await f.update({authGeneration:2});else if(change==='connection')await f.update({connection:{...f.scope,serial:'OTHER_TARGET'}});else await f.update({selected:{serial:'OTHER_TARGET',isReady:true}});
  assert.equal(await f.page.locator('#direct-command-invoke-confirm').isChecked(),false);if(change!=='choice')assert.equal(await f.page.evaluate(()=>window.view.getReviewedOption()),null);assert.equal(f.calls.length,count);
});

for(const variant of ['id','trimmed-id','package','dispatch-package','missing-id','missing-package','malformed-id','receipt','review-source','review-select','review-catalog','review-key','review-package','review-digest','value'])test('client rejects crossed '+variant+' and retains exact original launch',async t=>{
  const f=await fixture(t,{respond:(route,body,value)=>{
    if(body.command==='launch-option'){
      const d=value.kioskDirectCommand,r=d.typedResult;
      if(variant==='id')r.lastDispatchedOptionId='other';else if(variant==='trimmed-id')r.lastDispatchedOptionId=opaque.trim();else if(variant==='package'){r.selectedPackage='com.example.other';r.lastDispatchedOptionPackage='com.example.other';}
      else if(variant==='dispatch-package')r.lastDispatchedOptionPackage='com.example.other';else if(variant==='missing-id')delete r.lastDispatchedOptionId;else if(variant==='missing-package')delete r.lastDispatchedOptionPackage;else if(variant==='malformed-id')r.lastDispatchedOptionId='\ud800';
      else if(variant==='receipt')d.coreReceipt.requestId='kiosk_crossed';else if(variant==='review-source')d.launchReview.optionsRequestId='c1234567-1234-4234-8234-123456789abc';
      else if(variant==='review-select')d.launchReview.selectOriginalRequestId='c1234567-1234-4234-8234-123456789abc';else if(variant==='review-catalog')d.launchReview.catalogRequestId='c1234567-1234-4234-8234-123456789abc';
      else if(variant==='review-key')d.launchReview.key='other';else if(variant==='review-package')d.launchReview.packageName='com.example.other';else if(variant==='review-digest')d.launchReview.bindingSha256='b'.repeat(64);else d.value='other';
    }return route.fulfill({json:value});
  }});await prepared(f);await f.invoke();assert.equal((await f.stored()).original.value,opaque);assert.equal((await f.stored()).original.command,'launch-option');assert.equal(f.calls.filter(c=>c.body?.command==='launch-option').length,1);
});

test('known predispatch source rejection clears original without consuming another request automatically',async t=>{
  const f=await fixture(t,{respond:(route,body,value)=>body.command==='launch-option'?route.fulfill({status:409,json:{error:'direct_options_unavailable',dispatched:false}}):route.fulfill({json:value})});await prepared(f);await f.invoke();assert.equal(await f.stored(),null);assert.equal(f.calls.length,3);assert.equal(await f.page.evaluate(()=>window.view.hasUnresolved()),false);
});

test('lost launch and cancellation retain nested originalLaunch after typed result clears original and through reload',async t=>{
  const f=await fixture(t,{respond:(route,body,value)=>body.command==='launch-option'||body.action==='kiosk.direct.request-cancel'?route.abort('failed'):route.fulfill({json:value})});await prepared(f);await f.invoke();const original=(await f.stored()).original;
  await f.page.locator('#direct-command-cancel-confirm').check();await f.page.locator('#direct-command-cancel').click();await f.page.waitForFunction(()=>!window.ctx.busy);const both=await f.stored();assert.deepEqual(both.cancellation.originalLaunch,original);assert.equal(both.cancellation.originalSelect,undefined);
  await f.page.locator('#direct-command-request-status').click();await f.page.waitForFunction(()=>!window.ctx.busy);assert.deepEqual(await f.stored(),both);
  await f.page.locator('#direct-command-request-result').click();await f.page.waitForFunction(()=>!window.ctx.busy);const cancelOnly=await f.stored();assert.equal(cancelOnly.original,null);assert.deepEqual(cancelOnly.cancellation,both.cancellation);
  await f.page.reload();await f.page.waitForFunction(()=>window.ready);const calls=f.calls.length;assert.deepEqual(await f.stored(),cancelOnly);assert.equal(await f.page.evaluate(()=>window.view.getReviewedOption()),null);
  await f.page.locator('#direct-command-cancellation-status').click();await f.page.waitForFunction(()=>!window.view.hasUnresolved());assert.equal(await f.stored(),null);assert.equal(f.calls.length,calls+1);assert.equal(f.calls.filter(c=>c.body?.command==='launch-option').length,1);
});

test('changed or missing current options never replace launch recovery review',async t=>{
  const f=await fixture(t,{respond:(route,body,value)=>body.command==='launch-option'?route.abort('failed'):route.fulfill({json:value})});await prepared(f);await f.invoke();const before=(await f.stored()).original;await f.page.locator('#kiosk-filter').fill('no rows');
  await f.page.locator('#direct-command-request-result').click();await f.page.waitForFunction(()=>!window.ctx.busy);assert.equal(await f.stored(),null);assert.deepEqual(await f.page.evaluate(()=>window.latest.value.launchReview),before.launchReview);
  assert.deepEqual(Object.keys(f.calls.at(-1).body).sort(),['requestId','action','connectionId','originalRequestId'].sort());assert.equal(f.calls.filter(c=>c.body?.command==='launch-option').length,1);
});

for(const change of ['choice','auth','connection','source','implicit-source'])test('late launch after '+change+' preserves custody until explicit cached original read',async t=>{
  let release;const pending=new Promise(resolve=>release=resolve);const f=await fixture(t,{respond:async(route,body,value)=>{if(body.command==='launch-option')await pending;return route.fulfill({json:value});}});await prepared(f);
  await f.page.locator('#direct-command-invoke-confirm').check();await f.page.locator('#direct-command-invoke').click();await f.page.waitForFunction(()=>window.ctx.busy);
  if(change==='choice')await f.page.locator('#direct-command-option-choice').selectOption('1');else if(change==='auth')await f.update({authGeneration:2});else if(change==='connection')await f.update({connection:{...f.scope,serial:'OTHER_TARGET'}});else if(change==='source')await f.page.locator('#kiosk-filter').fill('alpha');else await f.update({selected:{serial:'OTHER_TARGET',isReady:true}});
  release();await f.page.waitForFunction(()=>!window.ctx.busy);assert.equal((await f.stored()).original.value,opaque);await f.page.locator('#direct-command-original-status').click();await f.page.waitForFunction(()=>!window.view.hasUnresolved());assert.equal(await f.stored(),null);assert.equal(f.calls.filter(c=>c.body?.command==='launch-option').length,1);
});

function largestFrame(){
  const c={...connection(),sessionId:'s'.repeat(128),serial:'T'.repeat(256)};delete c.expiresAtUtc;
  const original={...c,requestId:'c1234567-1234-4234-8234-123456789abc',action:'kiosk.direct.invoke',logicalRequestId:logical('c1234567-1234-4234-8234-123456789abc'),command:'launch-option',value:'\u0001'.repeat(160),launchReview:{optionsRequestId:'d1234567-1234-4234-8234-123456789abc',selectOriginalRequestId:'e1234567-1234-4234-8234-123456789abc',catalogRequestId:'f1234567-1234-4234-8234-123456789abc',key:'"\\'.repeat(80),packageName:'p.'+'a'.repeat(254),bindingSha256:'a'.repeat(64)}};
  const cancellation={requestId:'01234567-1234-4234-8234-123456789abc',sessionId:original.sessionId,action:'kiosk.direct.request-cancel',originalRequestId:original.requestId,connectionId:original.connectionId,logicalRequestId:original.logicalRequestId,originalLaunch:structuredClone(original)};
  return {original,cancellation};
}
test('worst compact escaped frame above4096 restores unchanged below8192 with nonrecursive nested launch',async t=>{
  const saved=largestFrame(),length=JSON.stringify(saved).length;assert.ok(length>4096&&length<=8192);const f=await fixture(t,{saved});assert.deepEqual(await f.stored(),saved);assert.equal(f.calls.length,0);
  const ref=await f.page.evaluate(()=>window.view.getCleanupReference());assert.equal(ref.sessionId,saved.original.sessionId);assert.equal(await f.page.evaluate(()=>window.view.hasUnresolved()),true);
});
for(const field of ['value','source','scope'])test('simultaneous original and nested launch must agree on '+field,async t=>{
  const saved=largestFrame();if(field==='value')saved.cancellation.originalLaunch.value='other';else if(field==='source')saved.cancellation.originalLaunch.launchReview.optionsRequestId='11234567-1234-4234-8234-123456789abc';else saved.cancellation.originalLaunch.serial='OTHER_TARGET';
  const f=await fixture(t,{saved});assert.equal((await f.stored()).damaged,true);assert.equal(await f.page.evaluate(()=>window.view.getCleanupReference()),null);assert.equal(f.calls.length,0);
});
test('frames beyond8192 are blocked and sanitized without requests',async t=>{
  const saved={...largestFrame(),foreign:'x'.repeat(8192)},f=await fixture(t,{saved});assert.equal((await f.stored()).damaged,true);assert.equal(f.calls.length,0);
});

for(const width of [1280,390])test('reviewed current-selection picker fits viewport '+width+' and captures proof',async t=>{
  const f=await fixture(t,{width});await prepared(f);assert.equal(await f.page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth),true);
  const dir=path.resolve(__dirname,'../../local/browser-direct-option-launch-proof');await fs.mkdir(dir,{recursive:true});await f.page.locator('#direct-command-view').screenshot({path:path.join(dir,width===390?'launch-mobile.png':'launch-desktop.png')});assert.equal(f.calls.length,2);
});
