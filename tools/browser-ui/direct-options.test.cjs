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
  typed.launchOptions={state:"ready",packageName:review.packageName,bindingSha256:"a".repeat(64),options:[{schemaVersion:1,optionId:"  opaque\\toption  ",displayLabel:"Example option",description:""}]};
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

test('shipped Direct has a historical returned-options observation container',async t=>{
  const f=await fixture(t);const count=await f.page.locator('#direct-command-launch-options').count();t.diagnostic(JSON.stringify({returnedOptionsContainers:count}));assert.equal(count,1);assert.equal(f.calls.length,0);
});

test('same original Select displays only bounded historical options with immutable original header',async t=>{
  const f=await fixture(t);await f.setup();await f.invoke();const original=f.calls[1].body;
  assert.equal(await f.stored(),null);assert.equal(await f.page.locator('#direct-command-launch-options').isVisible(),true);
  const source=await f.page.locator('#direct-command-launch-options-source').textContent();assert.ok(source.includes(original.requestId));assert.ok(source.includes(logical(original.requestId)));assert.ok(source.includes(f.calls[0].body.requestId));assert.ok(source.includes(rows()[0].packageName));
  assert.match(await f.page.locator('#direct-command-launch-options-state').textContent(),/ready; 1 options/);assert.equal(await f.page.locator('#direct-command-launch-options-rows li').count(),1);
  assert.equal(await f.page.locator('#direct-command-launch-options button, #direct-command-launch-options input').count(),0);
  assert.equal(await f.page.locator('#direct-command-command option[value="launch-option"], #direct-command-command option[value="focus-search"], #direct-command-command option[value="focus-tag-editor"]').count(),0);
  assert.equal(f.calls.length,2);assert.equal(f.calls.filter(c=>c.body?.action==='kiosk.direct.invoke').length,1);
  assert.doesNotMatch(JSON.stringify(f.calls[1].saved),/launchOptions|bindingSha256|optionId|displayLabel/);
});

test('generic result output excludes option rows and digest after valid Select and source clear',async t=>{
  const f=await fixture(t);await f.setup();await f.invoke();assert.equal(await f.stored(),null);assert.equal(await f.page.locator('#direct-command-launch-options').isVisible(),true);
  const before=await f.page.evaluate(()=>JSON.stringify(window.latest.value));await f.page.locator('#kiosk-filter').fill('alpha');
  assert.equal(await f.page.locator('#direct-command-launch-options').isHidden(),true);const after=await f.page.evaluate(()=>JSON.stringify(window.latest.value));
  assert.doesNotMatch(before,/launchOptions|optionId|bindingSha256/);assert.doesNotMatch(after,/launchOptions|optionId|bindingSha256/);
});

test('generic result output excludes option rows and digest on unconfirmed crossed base selection',async t=>{
  const f=await fixture(t,{respond:(route,body,value)=>{if(body.action==='kiosk.direct.invoke')value.kioskDirectCommand.typedResult.selectedPackage='com.example.other';return route.fulfill({json:value});}});
  await f.setup();await f.invoke();assert.equal((await f.stored()).original.command,'select');assert.equal(await f.page.locator('#direct-command-launch-options').isHidden(),true);
  assert.doesNotMatch(await f.page.evaluate(()=>JSON.stringify(window.latest.value)),/launchOptions|optionId|bindingSha256/);
});

for(const state of ['missing','none','rejected','unavailable','ready'])test('distinct option state '+state+' never changes exact Select acknowledgement',async t=>{
  const f=await fixture(t,{respond:(route,body,value)=>{if(body.action==='kiosk.direct.invoke')value.kioskDirectCommand.typedResult.launchOptions={state,packageName:state==='ready'?rows()[0].packageName:null,bindingSha256:state==='ready'?'a'.repeat(64):null,options:[]};return route.fulfill({json:value});}});
  await f.setup();await f.invoke();assert.equal(await f.stored(),null);assert.match(await f.page.locator('#direct-command-launch-options-state').textContent(),new RegExp(state+'; 0 options'));assert.match(await f.page.locator('#direct-command-state').textContent(),/acknowledged.*selection/i);
});

const invalidOptions={
  'missing-property':o=>undefined,'null-property':o=>null,'array':o=>[], 'unknown-field':o=>({...o,privateField:'PRIVATE'}),'state':o=>({...o,state:'invented'}),
  'crossed-package':o=>({...o,packageName:'com.example.other'}),'missing-package':o=>({...o,packageName:null}),
  'bad-digest':o=>({...o,bindingSha256:'g'.repeat(64)}),'uppercase-digest':o=>({...o,bindingSha256:'A'.repeat(64)}),'long-digest':o=>({...o,bindingSha256:'a'.repeat(65)}),
  'none-with-rows':o=>({...o,state:'none'}),'rejected-with-binding':o=>({...o,state:'rejected',options:[]}), 'missing-with-binding':o=>({...o,state:'missing',options:[]}),
  'rows-null':o=>({...o,options:null}),'too-many':o=>({...o,options:Array.from({length:65},(_,i)=>({...o.options[0],optionId:'id'+i}))}),
  'duplicate':o=>({...o,options:[o.options[0],o.options[0]]}),'row-array':o=>({...o,options:[[]]}),'row-null':o=>({...o,options:[null]}),
  'row-extra-field':o=>({...o,options:[{...o.options[0],nativeProvider:'PRIVATE'}]}),'schema':o=>({...o,options:[{...o.options[0],schemaVersion:2}]}),
  'blank-id':o=>({...o,options:[{...o.options[0],optionId:'\u0085'}]}),'long-id':o=>({...o,options:[{...o.options[0],optionId:'a'.repeat(161)}]}),
  'bad-id-utf16':o=>({...o,options:[{...o.options[0],optionId:'\ud800'}]}),'blank-label':o=>({...o,options:[{...o.options[0],displayLabel:'\u0085'}]}),
  'long-label':o=>({...o,options:[{...o.options[0],displayLabel:'a'.repeat(97)}]}),'bad-label-utf16':o=>({...o,options:[{...o.options[0],displayLabel:'\udc00'}]}),
  'missing-description':o=>({...o,options:[{schemaVersion:1,optionId:'id',displayLabel:'label'}]}),'null-description':o=>({...o,options:[{...o.options[0],description:null}]}),
  'long-description':o=>({...o,options:[{...o.options[0],description:'a'.repeat(161)}]}),'bad-description-utf16':o=>({...o,options:[{...o.options[0],description:'\ud800'}]})
};
for(const [name,change] of Object.entries(invalidOptions))test('optional '+name+' cannot erase valid selection acknowledgement',async t=>{
  const f=await fixture(t,{respond:(route,body,value)=>{if(body.action==='kiosk.direct.invoke'){const typed=value.kioskDirectCommand.typedResult;typed.launchOptions=change(typed.launchOptions);}return route.fulfill({json:value});}});
  await f.setup();await f.invoke();assert.equal(await f.stored(),null);assert.match(await f.page.locator('#direct-command-state').textContent(),/acknowledged.*selection/i);
  assert.match(await f.page.locator('#direct-command-launch-options-state').textContent(),new RegExp((name==='missing-property'||name==='null-property'?'missing':'unavailable')+'; 0 options'));
  assert.equal(await f.page.locator('#direct-command-launch-options-rows li').count(),0);assert.equal(f.calls.length,2);
});

test('opaque whitespace controls FEFF and Ordinal IDs survive while markup stays literal',async t=>{
  const ids=['\ufeff','A','a',' \tcontrol\n ','x'.repeat(160)],label='<img src=x onerror=window.injected=true>',description='<script>window.injected=true</script>';
  const f=await fixture(t,{respond:(route,body,value)=>{if(body.action==='kiosk.direct.invoke')value.kioskDirectCommand.typedResult.launchOptions.options=ids.map(optionId=>({schemaVersion:1,optionId,displayLabel:label,description}));return route.fulfill({json:value});}});
  await f.setup();await f.invoke();assert.equal(await f.stored(),null);assert.deepEqual(await f.page.locator('#direct-command-launch-options-rows code').allTextContents(),ids.map(id=>JSON.stringify(id)));
  assert.deepEqual(await f.page.locator('#direct-command-launch-options-rows strong').allTextContents(),ids.map(()=>label));assert.equal(await f.page.locator('#direct-command-launch-options-rows img, #direct-command-launch-options-rows script').count(),0);assert.equal(await f.page.evaluate(()=>window.injected),undefined);
});

test('maximum64 rows and96/160 labels descriptions remain bounded',async t=>{
  const f=await fixture(t,{respond:(route,body,value)=>{if(body.action==='kiosk.direct.invoke')value.kioskDirectCommand.typedResult.launchOptions.options=Array.from({length:64},(_,i)=>({schemaVersion:1,optionId:'id'+i,displayLabel:'L'.repeat(96),description:'D'.repeat(160)}));return route.fulfill({json:value});}});
  await f.setup();await f.invoke();assert.equal(await f.page.locator('#direct-command-launch-options-rows li').count(),64);assert.equal(await f.stored(),null);
  assert.equal(await f.page.locator('#direct-command-launch-options-rows').evaluate(e=>e.clientHeight<=360),true);assert.equal(f.calls.length,2);
});

test('unconfirmed returned selection never exposes ready options',async t=>{
  const f=await fixture(t,{respond:(route,body,value)=>{if(body.action==='kiosk.direct.invoke')value.kioskDirectCommand.typedResult.selectedPackage='com.example.other';return route.fulfill({json:value});}});
  await f.setup();await f.invoke();assert.equal((await f.stored()).original.command,'select');assert.equal(await f.page.locator('#direct-command-launch-options').isHidden(),true);assert.equal(await f.page.locator('#direct-command-launch-options-rows li').count(),0);
});

test('snapshot survives menu mode changes and source callback clears it even outside Select',async t=>{
  const f=await fixture(t);await f.setup();await f.invoke();await f.page.locator('#direct-command-command').selectOption('status');
  assert.equal(await f.page.locator('#direct-command-launch-options').isVisible(),true);await f.page.locator('#kiosk-filter').fill('alpha');
  assert.equal(await f.page.locator('#direct-command-launch-options').isHidden(),true);assert.equal(await f.page.locator('#direct-command-launch-options-rows li').count(),0);assert.equal(f.calls.length,2);
});

for(const change of ['auth','session','connection','expiry','row','channel','source'])test('observations clear on '+change+' without requests',async t=>{
  const f=await fixture(t);await f.setup();await f.invoke();
  if(change==='auth')await f.update({authGeneration:2});else if(change==='session')await f.update({sessionId:'session-b'});
  else if(change==='connection')await f.update({connection:{...f.scope,connectionId:'c1234567-1234-4234-8234-123456789abc'}});
  else if(change==='expiry')await f.update({connection:{...f.scope,expiresAtUtc:new Date(Date.now()-1000).toISOString()}});
  else if(change==='row')await f.highlight(1);else if(change==='channel')await f.page.locator('#kiosk-channel').selectOption('labs');
  else await f.page.evaluate(()=>{window.overrideReview={...window.kiosk.getReviewedSelection(),catalogRequestId:'c1234567-1234-4234-8234-123456789abc'};window.view.update();});
  assert.equal(await f.page.locator('#direct-command-launch-options').isHidden(),true);assert.equal(await f.page.locator('#direct-command-launch-options-rows li').count(),0);assert.equal(f.calls.length,2);
});

test('reload sends nothing and exact cached original reconstructs observation without a current catalog',async t=>{
  const f=await fixture(t,{respond:(route,body,value)=>body.action==='kiosk.direct.invoke'?route.abort('failed'):route.fulfill({json:value})});
  await f.setup();await f.invoke();const original=(await f.stored()).original;assert.doesNotMatch(JSON.stringify(await f.stored()),/launchOptions|optionId|bindingSha256/);
  await f.page.reload();await f.page.waitForFunction(()=>window.ready);assert.equal(f.calls.length,2);assert.equal(await f.page.locator('#direct-command-launch-options').isHidden(),true);assert.equal(await f.page.evaluate(()=>window.kiosk.getReviewedSelection()),null);
  await f.page.locator('#direct-command-original-status').click();await f.page.waitForFunction(()=>!window.view.hasUnresolved());assert.equal(await f.stored(),null);
  assert.ok((await f.page.locator('#direct-command-launch-options-source').textContent()).includes(original.requestId));assert.equal(await f.page.locator('#direct-command-launch-options-rows li').count(),1);assert.equal(f.calls.length,3);
});

test('original result reconstructs source after row change and lifecycle/cancellation cannot substitute option facts',async t=>{
  const f=await fixture(t,{respond:(route,body,value)=>['kiosk.direct.invoke','kiosk.direct.request-cancel'].includes(body.action)?route.abort('failed'):route.fulfill({json:value})});
  await f.setup();await f.invoke();const original=(await f.stored()).original;await f.highlight(1);
  await f.page.locator('#direct-command-cancel-confirm').check();await f.page.locator('#direct-command-cancel').click();await f.page.waitForFunction(()=>!window.ctx.busy);assert.doesNotMatch(JSON.stringify(await f.stored()),/launchOptions|optionId|bindingSha256/);
  await f.page.locator('#direct-command-request-status').click();await f.page.waitForFunction(()=>!window.ctx.busy);assert.equal(await f.page.locator('#direct-command-launch-options').isHidden(),true);
  await f.page.locator('#direct-command-request-result').click();await f.page.waitForFunction(()=>!window.ctx.busy);assert.equal((await f.stored()).original,null);assert.equal(await f.page.locator('#direct-command-launch-options-rows li').count(),1);
  const source=await f.page.locator('#direct-command-launch-options-source').textContent();assert.ok(source.includes(original.requestId));assert.ok(source.includes('com.example.alpha'));assert.ok(!source.includes('com.example.beta'));
  await f.page.locator('#direct-command-cancellation-status').click();await f.page.waitForFunction(()=>!window.view.hasUnresolved());assert.equal(await f.page.locator('#direct-command-launch-options-source').textContent(),source);assert.equal(f.calls.filter(c=>c.body?.action==='kiosk.direct.invoke').length,1);
});

test('exported recovery references never contain optional observation rows',async t=>{
  const f=await fixture(t,{respond:(route,body,value)=>body.action==='kiosk.direct.invoke'?route.abort('failed'):route.fulfill({json:value})});await f.setup();await f.invoke();
  await f.page.evaluate(()=>{const original=URL.createObjectURL;URL.createObjectURL=blob=>{window.exportedText=blob.text();return original(blob);};});
  await f.page.locator('#direct-command-save-reference').click();const exported=await f.page.evaluate(()=>window.exportedText);assert.doesNotMatch(exported,/launchOptions|optionId|displayLabel|bindingSha256/);assert.equal(JSON.parse(exported).original.command,'select');
});

for(const change of ['auth','connection','source'])test('late Select after '+change+' cannot introduce observation rows',async t=>{
  let release;const pending=new Promise(resolve=>release=resolve);const f=await fixture(t,{respond:async(route,body,value)=>{if(body.action==='kiosk.direct.invoke')await pending;return route.fulfill({json:value});}});
  await f.setup();await f.page.locator('#direct-command-invoke-confirm').check();await f.page.locator('#direct-command-invoke').click();await f.page.waitForFunction(()=>window.ctx.busy);
  if(change==='auth')await f.update({authGeneration:2});else if(change==='connection')await f.update({connection:{...f.scope,serial:'OTHER_TARGET'}});else await f.highlight(1);
  release();await f.page.waitForFunction(()=>!window.ctx.busy);assert.equal(await f.page.locator('#direct-command-launch-options').isHidden(),true);assert.equal((await f.stored()).original.command,'select');assert.equal(f.calls.length,2);
});

for(const kind of ['cached','typed-result'])test('late '+kind+' recovery outside Select mode cannot restore rows after a source callback',async t=>{
  let release,entered;const pending=new Promise(resolve=>release=resolve),started=new Promise(resolve=>entered=resolve);
  const f=await fixture(t,{respond:async(route,body,value)=>{if(body.action==='kiosk.direct.invoke')return route.abort('failed');if(body.action==='kiosk.direct.request-result'){entered();await pending;}return route.fulfill({json:value});},status:async(route,value)=>{entered();await pending;return route.fulfill({json:value});}});
  await f.setup();await f.invoke();await f.page.locator('#direct-command-command').selectOption('status');
  await f.page.locator(kind==='cached'?'#direct-command-original-status':'#direct-command-request-result').click();await started;
  await f.page.locator('#kiosk-filter').fill('alpha');release();await f.page.waitForFunction(()=>!window.view.hasUnresolved());
  assert.equal(await f.stored(),null);assert.equal(await f.page.locator('#direct-command-launch-options').isHidden(),true);assert.equal(await f.page.locator('#direct-command-launch-options-rows li').count(),0);assert.equal(f.calls.length,3);
});

for(const kind of ['cached','typed-result'])test('late '+kind+' recovery outside Select mode cannot restore rows after implicit catalog loss',async t=>{
  let release,entered;const pending=new Promise(resolve=>release=resolve),started=new Promise(resolve=>entered=resolve);
  const f=await fixture(t,{respond:async(route,body,value)=>{if(body.action==='kiosk.direct.invoke')return route.abort('failed');if(body.action==='kiosk.direct.request-result'){entered();await pending;}return route.fulfill({json:value});},status:async(route,value)=>{entered();await pending;return route.fulfill({json:value});}});
  await f.setup();await f.invoke();await f.page.locator('#direct-command-command').selectOption('status');
  await f.page.locator(kind==='cached'?'#direct-command-original-status':'#direct-command-request-result').click();await started;
  await f.update({selected:{serial:'OTHER_TARGET',isReady:true}});assert.equal(await f.page.evaluate(()=>window.kiosk.getReviewedSelection()),null);
  release();await f.page.waitForFunction(()=>!window.view.hasUnresolved());assert.equal(await f.stored(),null);
  assert.equal(await f.page.locator('#direct-command-launch-options').isHidden(),true);assert.equal(await f.page.locator('#direct-command-launch-options-rows li').count(),0);assert.equal(f.calls.length,3);
});

for(const width of [1280,390])test('literal returned-options view fits viewport '+width+' and captures proof',async t=>{
  const f=await fixture(t,{width,respond:(route,body,value)=>{if(body.action==='kiosk.direct.invoke')value.kioskDirectCommand.typedResult.launchOptions.options=[
    {schemaVersion:1,optionId:'  opaque\toption\n  ',displayLabel:'<Example option>',description:'<img src=x onerror=window.injected=true> is literal text.'},
    {schemaVersion:1,optionId:'\ufeff',displayLabel:'\ufeff',description:'An exact opaque ID; no launch action is offered.'}];return route.fulfill({json:value});}});
  await f.setup();await f.invoke();assert.equal(await f.page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth),true);
  const directory=path.resolve(__dirname,'../../local/browser-direct-options-proof');await fs.mkdir(directory,{recursive:true});
  await f.page.locator('#direct-command-launch-options').screenshot({path:path.join(directory,width===390?'options-mobile.png':'options-desktop.png')});assert.equal(f.calls.length,2);
});
