// Shipped DOM and module; all requests intercepted, no listener/device/Kiosk.
const test=require('node:test'),assert=require('node:assert/strict'),fs=require('node:fs/promises'),path=require('node:path');
const {chromium}=require('playwright');
const root=path.resolve(__dirname,'../../src/QuestIonAbleFileManager.BrowserHost/wwwroot'),storageKey='qfm-browser-kiosk-v1';
const provider=id=>'pc-'+id.replaceAll('-','');
const installation=()=>({mainInstalled:true,mainVersion:'fixture-version',setupHelperInstalled:true,setupHelperVersion:'helper-version',setupHelperReady:true,sameSignerControlGranted:true,hostOperatorAvailable:true});
const entries=()=>[{key:'package:com.example.alpha',name:'Alpha App',packageName:'com.example.alpha',installed:true,launchable:true,selectable:true},
  {key:'name:Missing App',name:'Missing App',packageName:null,installed:false,launchable:false,selectable:false},
  {key:'package:com.example.service',name:'Service App',packageName:'com.example.service',installed:true,launchable:false,selectable:false}];
function outcome(body){
  const operation=body.action.replace('kiosk.',''),readonly=['installation','original-result'].includes(operation),providerRequestId=operation==='installation'?null:body.providerRequestId||provider(body.requestId);
  const command=operation==='select'?'select':body.expectedCommand||'status',selected=body.key||'package:com.example.alpha';
  return {requestId:body.requestId,action:body.action,state:'completed',result:null,mutation:readonly?null:{operationId:'pc-'+'9'.repeat(32),commandKind:operation==='select'?'selectRustyKioskApp':'openRustyKioskCatalog',target:body.serial,
      desiredState:operation==='select'?`Rusty Kiosk installation inspected; selection ${body.key} requested if available (${body.productChannel})`:`Rusty Kiosk installation inspected; catalog requested if available (${body.productChannel})`,stage:'confirmed',headsetReadback:true,transitions:[]},
    kiosk:{serial:body.serial,productChannel:body.productChannel,operation,providerRequestId,providerState:operation==='installation'?'installationOnly':'completed',activityOpenRequested:!readonly,
      installation:installation(),result:operation==='installation'?null:{providerRequestId,command,accepted:true,completed:true,selectedKey:selected,selectedName:'Alpha App',selectedPackage:'com.example.alpha',selectedInstalled:true,selectedLaunchable:true,
        installedCount:2,notInstalledCount:1,visibleCount:3,visibleEntriesTruncated:false,projectionTruncated:false,entries:entries()},
      catalogRequestId:operation==='catalog.open'?body.requestId:operation==='select'?body.catalogRequestId:null,reviewedKey:body.key||null,reviewedPackage:operation==='select'?'com.example.alpha':null,originalFactsOnly:operation==='original-result'}};
}
let browser;test.before(async()=>{browser=await chromium.launch({headless:true});});test.after(async()=>browser?.close());
async function fixture(t,{respond,status,width=1280,state={}}={}){
  const context=await browser.newContext({serviceWorkers:'block',viewport:{width,height:1000}});t.after(()=>context.close());const page=await context.newPage(),calls=[],errors=[],owner=new Map();page.on('pageerror',error=>errors.push(error.message));
  await context.route('**/*',async route=>{
    const url=new URL(route.request().url());if(url.origin!=='http://127.0.0.1:18127')return route.abort('blockedbyclient');
    if(url.pathname.startsWith('/browser/')){
      const method=route.request().method(),body=method==='POST'?route.request().postDataJSON():undefined,call={path:url.pathname,method,body};calls.push(call);
      if(url.pathname==='/browser/v1/execute'){
        const value=outcome(body);owner.set(body.requestId,value);call.saved=await page.evaluate(key=>JSON.parse(sessionStorage.getItem(key)||'null'),storageKey);
        return respond?respond(route,body,value):route.fulfill({json:value});
      }
      const value=owner.get(url.pathname.split('/').at(-1));return status?status(route,value):route.fulfill({json:value});
    }
    if(url.pathname==='/kiosk-fixture.js')return route.fulfill({contentType:'text/javascript',body:`import {createKioskView} from './kiosk-view.js';
      window.ctx=${JSON.stringify({sessionId:'session-a',connected:true,busy:false,selected:{serial:'QUEST-A',isReady:true},actions:['kiosk.installation','kiosk.catalog.open','kiosk.select','kiosk.original-result'],externalUnresolved:false,...state})};window.ctx.actions=new Set(window.ctx.actions);
      sessionStorage.setItem('native-original','unknown-native');sessionStorage.setItem('push-original','unknown-push');
      window.kiosk=createKioskView({getContext:()=>window.ctx,setBusy:value=>{window.ctx.busy=value;window.siblingBlocked=Boolean(value||window.kiosk?.hasUnresolved());window.kiosk?.update();},showResult:(value,summary)=>window.latest={value,summary},
        api:async(path,options)=>{const response=await fetch(path,{...options,cache:'no-store'});if(!response.ok){const value=await response.json();throw Object.assign(new Error(value.error),value,{status:response.status});}return response.json();}});window.kiosk.update();window.fixtureReady=true;`});
    const name=url.pathname==='/'?'index.html':url.pathname.slice(1);if(!['index.html','styles.css','kiosk-view.js'].includes(name))return route.fulfill({status:404,body:''});
    let bytes=await fs.readFile(path.join(root,name));if(name==='index.html')bytes=Buffer.from(bytes.toString().replace(/<script[^>]*src="\.\/pwa.js"[^>]*><\/script>/,'').replace(/<script[^>]*src="\.\/app.js"[^>]*><\/script>/,'<script type="module" src="./kiosk-fixture.js"></script>'));
    return route.fulfill({contentType:name.endsWith('.js')?'text/javascript':name.endsWith('.css')?'text/css':'text/html',body:bytes});
  });
  await page.goto('http://127.0.0.1:18127/');await page.waitForFunction(()=>window.fixtureReady);
  const update=patch=>page.evaluate(patch=>{Object.assign(window.ctx,patch);if(Array.isArray(window.ctx.actions))window.ctx.actions=new Set(window.ctx.actions);window.kiosk.update();},patch);
  const open=async()=>{await page.locator('#kiosk-open-confirm').check();await page.locator('#kiosk-open').click();await page.waitForFunction(()=>!window.ctx.busy);};
  const select=async()=>{await page.locator('#kiosk-entries button').first().click();await page.locator('#kiosk-select-confirm').check();await page.locator('#kiosk-select').click();await page.waitForFunction(()=>!window.ctx.busy);};
  const stored=()=>page.evaluate(key=>JSON.parse(sessionStorage.getItem(key)||'null'),storageKey);
  t.after(()=>assert.deepEqual(errors,[],'No uncaught Kiosk UI errors'));return {page,calls,update,open,select,stored,owner};
}

test('Kiosk controls are inert without JavaScript',async t=>{
  const context=await browser.newContext({javaScriptEnabled:false,serviceWorkers:'block'});t.after(()=>context.close());await context.route('**/*',async route=>new URL(route.request().url()).pathname==='/'?route.fulfill({contentType:'text/html',body:await fs.readFile(path.join(root,'index.html'))}):route.abort('blockedbyclient'));
  const page=await context.newPage();page.setDefaultTimeout(1000);await page.goto('http://127.0.0.1:18127/');for(const id of ['channel','installation','open-confirm','open','filter','select-confirm','select','original-status','original-facts'])assert.equal(await page.locator('#kiosk-'+id).isDisabled(),true);
});

test('passive installation sends exact stable channel and exposes flags without an open/catalog claim',async t=>{
  const f=await fixture(t,{state:{externalUnresolved:true}});assert.equal(f.calls.length,0);await f.page.locator('#kiosk-installation').click();await f.page.waitForFunction(()=>!window.ctx.busy);
  const {requestId,...body}=f.calls[0].body;assert.deepEqual(body,{action:'kiosk.installation',serial:'QUEST-A',productChannel:'stable'});assert.equal(await f.stored(),null);
  assert.match(await f.page.locator('#kiosk-installation-state').textContent(),/Installed/);assert.match(await f.page.locator('#kiosk-installation-boundary').textContent(),/signer.*readiness/i);
  assert.equal(await f.page.locator('#kiosk-entries button').count(),0);assert.equal(await f.page.locator('#kiosk-open').isDisabled(),true);
});

test('explicit open persists derived provider ID then local filter/highlight sends nothing until separate selection approval',async t=>{
  const f=await fixture(t);assert.equal(await f.page.locator('#kiosk-channel').inputValue(),'stable');assert.equal(await f.page.locator('#kiosk-open').isDisabled(),true);
  await f.open();const open=f.calls[0],providerRequestId=provider(open.body.requestId);assert.deepEqual(open.saved,{requestId:open.body.requestId,sessionId:'session-a',serial:'QUEST-A',productChannel:'stable',action:'kiosk.catalog.open',providerRequestId,expectedCommand:'status'});
  assert.deepEqual(Object.keys(open.body).sort(),['action','confirmed','productChannel','requestId','serial']);assert.equal(open.body.confirmed,true);assert.equal(await f.stored(),null);
  await f.page.locator('#kiosk-filter').fill('Alpha');assert.equal(await f.page.locator('#kiosk-entries button').count(),1);assert.equal(f.calls.length,1);
  await f.page.locator('#kiosk-entries button').click();assert.equal(await f.page.locator('#kiosk-select').isDisabled(),true);assert.equal(f.calls.length,1);
  await f.page.locator('#kiosk-select-confirm').check();await f.page.locator('#kiosk-select').click();await f.page.waitForFunction(()=>!window.ctx.busy);
  assert.deepEqual(f.calls[1].body,{requestId:f.calls[1].body.requestId,action:'kiosk.select',serial:'QUEST-A',productChannel:'stable',catalogRequestId:open.body.requestId,key:'package:com.example.alpha',confirmed:true});
  assert.equal(f.calls[1].saved.providerRequestId,provider(f.calls[1].body.requestId));assert.equal(await f.page.locator('#kiosk-provider-selected').textContent(),'package:com.example.alpha');
  assert.match(await f.page.locator('#kiosk-boundary').textContent(),/selection.*launch/i);assert.equal(await f.stored(),null);
});

test('unavailable product uses confirmed no-open branch without inventing an empty catalog',async t=>{
  const f=await fixture(t,{respond:(route,body,value)=>{value.kiosk.providerState='installationOnly';value.kiosk.activityOpenRequested=false;value.kiosk.installation.hostOperatorAvailable=false;value.kiosk.result=null;value.kiosk.catalogRequestId=null;return route.fulfill({json:value});}});
  await f.page.locator('#kiosk-channel').selectOption('labs');await f.open();assert.equal(f.calls[0].body.productChannel,'labs');assert.equal(await f.stored(),null);assert.equal(await f.page.locator('#kiosk-entries button').count(),0);
  assert.match(await f.page.locator('#kiosk-catalog-state').textContent(),/unavailable.*no catalog/i);assert.equal(await f.page.locator('#kiosk-select').isDisabled(),true);
});

test('channel change clears an earlier unavailable-catalog claim for the new product',async t=>{
  const f=await fixture(t,{respond:(route,body,value)=>{Object.assign(value.kiosk,{providerState:'installationOnly',activityOpenRequested:false,result:null,catalogRequestId:null});value.kiosk.installation.hostOperatorAvailable=false;return route.fulfill({json:value});}});
  await f.open();await f.page.locator('#kiosk-channel').selectOption('labs');assert.doesNotMatch(await f.page.locator('#kiosk-catalog-state').textContent(),/operator unavailable/i);assert.equal(f.calls.length,1);
});

test('selection no-op retires stale catalog and never displays reviewed key as a new selection',async t=>{
  const f=await fixture(t,{respond:(route,body,value)=>{if(body.action==='kiosk.select'){Object.assign(value.kiosk,{providerState:'installationOnly',activityOpenRequested:false,result:null});value.kiosk.installation.hostOperatorAvailable=false;}return route.fulfill({json:value});}});
  await f.open();await f.select();assert.equal(await f.stored(),null);assert.equal(await f.page.locator('#kiosk-entries button').count(),0);assert.equal(await f.page.locator('#kiosk-provider-selected').textContent(),'Unknown');
  assert.match(await f.page.locator('#kiosk-catalog-state').textContent(),/Selection not performed.*fresh catalog/i);assert.equal(await f.page.locator('#kiosk-select').isDisabled(),true);
});

for(const field of ['operator available','no readback','pending marker','unknown activity','missing provider ID'])test(`incomplete no-open proof (${field}) cannot acknowledge original`,async t=>{
  const f=await fixture(t,{respond:(route,body,value)=>{Object.assign(value.kiosk,{providerState:'installationOnly',activityOpenRequested:false,result:null,catalogRequestId:null});value.kiosk.installation.hostOperatorAvailable=false;
    if(field==='operator available')value.kiosk.installation.hostOperatorAvailable=true;if(field==='no readback')value.mutation.headsetReadback=false;if(field==='pending marker')value.mutation.stage='pending';if(field==='unknown activity')value.kiosk.activityOpenRequested=null;
    if(field==='missing provider ID')value.kiosk.providerRequestId=null;
    return route.fulfill({json:value});}});await f.open();assert.ok(await f.stored());assert.equal(await f.page.evaluate(()=>window.kiosk.hasUnresolved()),true);
});

test('missing and nonlaunchable catalog rows can be highlighted but never selected on headset',async t=>{
  const f=await fixture(t);await f.open();for(const index of [1,2]){await f.page.locator('#kiosk-entries button').nth(index).click();assert.equal(await f.page.locator('#kiosk-select-confirm').isDisabled(),true);assert.equal(await f.page.locator('#kiosk-select').isDisabled(),true);}assert.equal(f.calls.length,1);
});

test('catalog counts and both truncation signals describe returned rows honestly',async t=>{
  const f=await fixture(t,{respond:(route,body,value)=>{Object.assign(value.kiosk.result,{installedCount:500,visibleCount:140,visibleEntriesTruncated:true,projectionTruncated:true});return route.fulfill({json:value});}});await f.open();await f.page.locator('#kiosk-filter').fill('no match');
  assert.match(await f.page.locator('#kiosk-catalog-state').textContent(),/500/);assert.match(await f.page.locator('#kiosk-catalog-state').textContent(),/truncat/i);assert.match(await f.page.locator('#kiosk-catalog-state').textContent(),/0.*returned|0.*matching/i);assert.equal(f.calls.length,1);
});

test('row filter channel target and authentication changes clear separate approvals without automatic requests',async t=>{
  const f=await fixture(t);await f.open();await f.page.locator('#kiosk-entries button').first().click();await f.page.locator('#kiosk-select-confirm').check();await f.page.locator('#kiosk-entries button').nth(1).click();assert.equal(await f.page.locator('#kiosk-select-confirm').isChecked(),false);
  await f.page.locator('#kiosk-open-confirm').check();await f.page.locator('#kiosk-channel').selectOption('labs');assert.equal(await f.page.locator('#kiosk-open-confirm').isChecked(),false);assert.equal(await f.page.locator('#kiosk-entries button').count(),0);
  await f.page.locator('#kiosk-open-confirm').check();await f.update({selected:{serial:'QUEST-B',isReady:true}});assert.equal(await f.page.locator('#kiosk-open-confirm').isChecked(),false);
  await f.page.locator('#kiosk-open-confirm').check();await f.update({connected:false});await f.update({connected:true});assert.equal(await f.page.locator('#kiosk-open-confirm').isChecked(),false);assert.equal(f.calls.length,1);
});

for(const field of ['request','action','target','channel','provider ID','provider command','catalog ID','marker ID','marker kind','marker target','marker readback','raw result'])test(`crossed ${field} open reply preserves unknown without a catalog`,async t=>{
  const f=await fixture(t,{respond:(route,body,value)=>{if(field==='request')value.requestId=crypto.randomUUID();if(field==='action')value.action='kiosk.select';if(field==='target')value.kiosk.serial='QUEST-B';if(field==='channel')value.kiosk.productChannel='labs';
    if(field==='provider ID')value.kiosk.providerRequestId='pc-'+'f'.repeat(32);if(field==='provider command')value.kiosk.result.command='select';if(field==='catalog ID')value.kiosk.catalogRequestId=crypto.randomUUID();if(field==='marker ID')value.mutation.operationId='pc-'+'0'.repeat(32);if(field==='marker kind')value.mutation.commandKind='selectRustyKioskApp';if(field==='marker target')value.mutation.target='QUEST-B';if(field==='marker readback')value.mutation.headsetReadback=false;if(field==='raw result')value.result={rawJson:'private-native-capture'};return route.fulfill({json:value});}});
  await f.open();assert.ok(await f.stored());assert.equal(await f.page.evaluate(()=>window.kiosk.hasUnresolved()),true);assert.equal(await f.page.locator('#kiosk-entries button').count(),0);assert.equal(f.calls.length,1);
});

for(const state of ['pending','outcomeUnknown'])test(`${state} open keeps original through passive installation and separate completed provider facts`,async t=>{
  const f=await fixture(t,{respond:(route,body,value)=>route.fulfill({json:body.action==='kiosk.catalog.open'?{...value,state,kiosk:{...value.kiosk,providerState:'pending',result:null,catalogRequestId:null},mutation:null}:value})});
  await f.open();const original=await f.stored();await f.page.locator('#kiosk-installation').click();await f.page.waitForFunction(()=>!window.ctx.busy);assert.deepEqual(await f.stored(),original);
  await f.page.locator('#kiosk-original-facts').click();await f.page.waitForFunction(()=>!window.ctx.busy);assert.deepEqual(await f.stored(),original);assert.equal(await f.page.evaluate(()=>window.kiosk.hasUnresolved()),true);
  assert.equal(f.calls.at(-1).body.action,'kiosk.original-result');assert.equal(f.calls.at(-1).body.providerRequestId,original.providerRequestId);assert.equal(f.calls.at(-1).body.expectedCommand,'status');assert.match(await f.page.locator('#kiosk-provider-facts').textContent(),/separate.*original.*unchanged/i);
});

test('lost open reload and changed host never replay; explicit original GET confirms and refreshes sibling controls',async t=>{
  const f=await fixture(t,{respond:(route,body,value)=>body.action==='kiosk.catalog.open'?route.abort('failed'):route.fulfill({json:value})});await f.open();const original=await f.stored(),count=f.calls.length;
  await f.page.reload();await f.page.waitForFunction(()=>window.fixtureReady);assert.equal(f.calls.length,count);assert.deepEqual(await f.stored(),original);
  await f.update({sessionId:'session-b'});assert.equal(await f.page.locator('#kiosk-original-status').isDisabled(),true);assert.equal(await f.page.locator('#kiosk-open').isDisabled(),true);assert.deepEqual(await f.stored(),original);
  await f.page.locator('#kiosk-original-facts').click();await f.page.waitForFunction(()=>!window.ctx.busy);assert.deepEqual(await f.stored(),original);
  await f.update({sessionId:'session-a',selected:{serial:'QUEST-B',isReady:true}});await f.page.locator('#kiosk-original-status').click();await f.page.waitForFunction(()=>!window.kiosk.hasUnresolved());
  assert.equal(f.calls.at(-1).path,'/browser/v1/requests/'+original.requestId);assert.equal(f.calls.at(-1).method,'GET');assert.equal(await f.page.evaluate(()=>window.siblingBlocked),false);assert.equal(await f.page.locator('#kiosk-entries button').count(),0);
  assert.equal(f.calls.filter(call=>call.body?.action==='kiosk.catalog.open').length,1);
});

for(const change of ['auth','target','channel'])test(`late catalog after ${change} round trip stays unknown until explicit original GET`,async t=>{
  let finish;const f=await fixture(t,{respond:async(route,body,value)=>{await new Promise(resolve=>{finish=resolve;});return route.fulfill({json:value});}});
  await f.page.locator('#kiosk-open-confirm').check();await f.page.locator('#kiosk-open').click();await f.page.waitForFunction(()=>window.ctx.busy);
  if(change==='auth'){await f.update({connected:false});await f.update({connected:true});}if(change==='target'){await f.update({selected:{serial:'QUEST-B',isReady:true}});await f.update({selected:{serial:'QUEST-A',isReady:true}});}if(change==='channel'){await f.page.locator('#kiosk-channel').selectOption('labs');await f.page.locator('#kiosk-channel').selectOption('stable');}
  finish();await f.page.waitForFunction(()=>!window.ctx.busy);assert.ok(await f.stored());assert.equal(await f.page.locator('#kiosk-entries button').count(),0);
  await f.page.locator('#kiosk-original-status').click();await f.page.waitForFunction(()=>!window.kiosk.hasUnresolved());assert.equal(f.calls.length,2);
});

test('lost selection preserves key/command and separate facts never acknowledge it',async t=>{
  const f=await fixture(t,{respond:(route,body,value)=>body.action==='kiosk.select'?route.abort('failed'):route.fulfill({json:value})});await f.open();await f.select();const original=await f.stored();assert.equal(original.expectedCommand,'select');assert.equal(original.key,'package:com.example.alpha');
  await f.page.locator('#kiosk-original-facts').click();await f.page.waitForFunction(()=>!window.ctx.busy);assert.equal(f.calls.at(-1).body.key,original.key);assert.deepEqual(await f.stored(),original);assert.equal(f.calls.filter(call=>call.body?.action==='kiosk.select').length,1);
});

for(const field of ['reviewed package','selected key','catalog source'])test(`crossed ${field} selection reply stays unresolved`,async t=>{
  const f=await fixture(t,{respond:(route,body,value)=>{if(body.action==='kiosk.select'){if(field==='reviewed package')value.kiosk.reviewedPackage='com.example.other';if(field==='selected key')value.kiosk.result.selectedKey='package:com.example.other';if(field==='catalog source')value.kiosk.catalogRequestId=crypto.randomUUID();}return route.fulfill({json:value});}});
  await f.open();await f.select();assert.ok(await f.stored());assert.equal(await f.page.locator('#kiosk-select').isDisabled(),true);assert.equal(f.calls.length,2);
});

test('separate facts reject crossed provider identity without changing original',async t=>{
  const f=await fixture(t,{respond:(route,body,value)=>{if(body.action==='kiosk.catalog.open')return route.abort('failed');value.kiosk.providerRequestId='pc-'+'f'.repeat(32);return route.fulfill({json:value});}});await f.open();const original=await f.stored();await f.page.locator('#kiosk-original-facts').click();await f.page.waitForFunction(()=>!window.ctx.busy);
  assert.deepEqual(await f.stored(),original);assert.match(await f.page.locator('#kiosk-provider-facts').textContent(),/unavailable/i);assert.equal(await f.page.locator('#kiosk-facts-selected').textContent(),'Unknown');
});

test('cached original acknowledgement preserves concurrent POST busy and cannot be undone by its lost reply',async t=>{
  let finish;const f=await fixture(t,{respond:async route=>{await new Promise(resolve=>{finish=resolve;});return route.abort('failed');}});
  await f.page.locator('#kiosk-open-confirm').check();await f.page.locator('#kiosk-open').click();await f.page.waitForFunction(()=>window.ctx.busy);
  await f.page.locator('#kiosk-original-status').click();await f.page.waitForFunction(()=>!window.kiosk.hasUnresolved());assert.equal(await f.page.evaluate(()=>window.ctx.busy),true);
  finish();await f.page.waitForFunction(()=>!window.ctx.busy);assert.equal(await f.stored(),null);assert.equal(await f.page.locator('#kiosk-entries button').count(),3);
});

test('saved/reloaded original allowlists metadata and never stores catalogs, captures or private fields',async t=>{
  const f=await fixture(t,{respond:route=>route.abort('failed')});await f.open();const original=await f.stored();
  await f.page.evaluate(key=>{const value=JSON.parse(sessionStorage.getItem(key));value.result={rawJson:'private-capture',secret:'private-secret',path:'C:/private/state'};sessionStorage.setItem(key,JSON.stringify(value));},storageKey);
  const count=f.calls.length;await f.page.reload();await f.page.waitForFunction(()=>window.fixtureReady);assert.deepEqual(await f.stored(),original);assert.equal(f.calls.length,count);
  await f.page.evaluate(()=>{const make=URL.createObjectURL;URL.createObjectURL=blob=>{blob.text().then(value=>window.savedReference=JSON.parse(value));return make(blob);};});await f.page.locator('#kiosk-original-save').click();await f.page.waitForFunction(()=>window.savedReference);assert.deepEqual(await f.page.evaluate(()=>window.savedReference),original);
});

test('only genuine current-session preadmission failure can clear original; duplicate:false cannot',async t=>{
  let error='busy';const f=await fixture(t,{respond:route=>route.fulfill({status:409,json:{error,dispatched:false}})});await f.open();assert.equal(await f.stored(),null);error='request_already_consumed';await f.open();assert.ok(await f.stored());assert.equal(await f.page.evaluate(()=>window.kiosk.hasUnresolved()),true);
});

test('installation failure does not report absent and unsupported actions/native config remain separate',async t=>{
  const f=await fixture(t,{state:{actions:[],capability:{supported:false}},respond:route=>route.fulfill({status:503,json:{error:'operation_failed'}})});assert.equal(await f.page.locator('#kiosk-installation').isDisabled(),true);await f.update({actions:['kiosk.installation']});await f.page.locator('#kiosk-installation').click();await f.page.waitForFunction(()=>!window.ctx.busy);
  assert.match(await f.page.locator('#kiosk-installation-state').textContent(),/unavailable/i);assert.doesNotMatch(await f.page.locator('#kiosk-installation-state').textContent(),/not installed/i);assert.equal(await f.stored(),null);
});

test('catalog data stays literal and mobile fits without persisting catalog or changing other originals',async t=>{
  const label='<img src=x onerror="window.injected=true">',f=await fixture(t,{width:390,respond:(route,body,value)=>{value.kiosk.result.entries[0].name=label;return route.fulfill({json:value});}});await f.open();
  assert.equal(await f.page.locator('#kiosk-entries img').count(),0);assert.match(await f.page.locator('#kiosk-entries').textContent(),/<img/);assert.equal(await f.page.evaluate(()=>Boolean(window.injected)),false);assert.equal(await f.page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth),true);
  assert.equal(await f.stored(),null);assert.equal(await f.page.evaluate(()=>sessionStorage.getItem('native-original')),'unknown-native');assert.equal(await f.page.evaluate(()=>sessionStorage.getItem('push-original')),'unknown-push');
});
