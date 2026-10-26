// Real Chromium DOM, with every URL intercepted; no listener, ADB or USB.
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs/promises');
const path = require('node:path');
const {chromium} = require('playwright');
const root = path.resolve(__dirname, '../../src/QuestIonAbleFileManager.BrowserHost/wwwroot');
const storageKey = 'qfm-browser-retained-v1';
const observationKey = 'qfm-browser-observation-v1';
const id = 'api-' + '1'.repeat(32), digest = '2'.repeat(64), sha = '3'.repeat(64);
const observationId = 'api-' + '9'.repeat(32), observationDigest = 'a'.repeat(64);
let browser;
test.before(async () => {browser = await chromium.launch({headless:true});});
test.after(async () => {await browser?.close();});

async function fixture(t, {capability={supported:true}, preflight, execute, status, observationPreflight, observationExecute, width=1280}={}) {
  const context=await browser.newContext({serviceWorkers:'block',viewport:{width,height:900}});
  t.after(()=>context.close());
  const page=await context.newPage(), calls=[], errors=[];
  page.on('pageerror',error=>errors.push(error.message));
  const state={sessionId:'session-a',connected:true,busy:false,selected:{serial:'QUEST-A',isReady:true},
    artifact:{id:'4'.repeat(32),fileName:'fixture.apk',sha256:sha,sizeBytes:3}, capability, unresolvedTransient:false};
  let ownerStage='preflighted';
  let observedStage='preflighted', observedReceipt=null;
  const expiresAt=new Date(Date.now()+120000).toISOString();
  const receipt=()=>({operationId:id,commandDigest:digest,command:'apk.install-inspected',expiresAt,
    artifact:{path:`retained://${id}/base.apk`,sizeBytes:3,sha256:sha,identity:{packageName:'com.example.app',versionCode:7,versionName:'1.7',signerSha256:'5'.repeat(64)}},target:{serial:'QUEST-A',isReady:true}});
  await context.route('**/*',async route=>{
    const url=new URL(route.request().url());
    if(url.origin!=='http://127.0.0.1:18127')return route.abort('blockedbyclient');
    if(url.pathname.startsWith('/browser/')){
      const body=route.request().postDataJSON(); calls.push({path:url.pathname,body});
      if(url.pathname.endsWith('/preflight')){
        if(body.command==='runtime.observe'){
          observedReceipt={...receipt(),operationId:observationId,commandDigest:observationDigest,command:'runtime.observe',target:{serial:body.serial,isReady:true},
            artifact:{...receipt().artifact,path:`retained://${observationId}/base.apk`,sha256:body.sha256,sizeBytes:body.sizeBytes}};
          observedStage='preflighted';
          return observationPreflight?observationPreflight(route,body,observedReceipt):route.fulfill({json:observedReceipt});
        }
        return preflight?preflight(route,body,receipt()):route.fulfill({json:{...receipt(),command:body.command}});
      }
      const isObservation=body.operationId===observationId;
      const value={operationId:isObservation?observationId:id,commandDigest:isObservation?observationDigest:digest,command:isObservation?'runtime.observe':'apk.install-inspected',expiresAt:receipt().expiresAt,stage:isObservation?observedStage:ownerStage};
      if(isObservation){
        if(url.pathname.endsWith('/execute')){
          observedStage='running';
          if(observationExecute)return observationExecute(route,body,{...value,stage:'running'},stage=>{observedStage=stage;});
          observedStage='completed';return route.fulfill({json:{...value,stage:'completed',result:{processAlive:true,isTopResumed:true,privatePath:'C:/private/observation',capture:'private-observation-capture',secret:'private-observation-secret'}}});
        }
        if(url.pathname.endsWith('/status'))return route.fulfill({json:{...value,stage:observedStage}});
        if(url.pathname.endsWith('/cancel')){
          if(observedStage==='preflighted')return route.fulfill({status:409,json:{error:'operation_not_running',dispatched:false}});
          if(observedStage==='running')observedStage='cancellationRequested';
          return route.fulfill({json:{...value,stage:observedStage}});
        }
      }
      if(url.pathname.endsWith('/execute')){
        ownerStage='running';
        if(execute)return execute(route,body,{...value,stage:'running'},stage=>{ownerStage=stage;});
        ownerStage='completed';return route.fulfill({json:{...value,stage:'completed',result:{privatePath:'C:/private/never-persist',capture:'never-persist'}}});
      }
      if(url.pathname.endsWith('/status'))return status?status(route,body,value):route.fulfill({json:value});
      if(url.pathname.endsWith('/cancel')){
        if(ownerStage==='preflighted')return route.fulfill({status:409,json:{error:'operation_not_running',dispatched:false}});
        if(ownerStage==='running')ownerStage='cancellationRequested';
        return route.fulfill({json:{...value,stage:ownerStage}});
      }
      return route.abort('blockedbyclient');
    }
    if(url.pathname==='/retained-fixture.js')return route.fulfill({contentType:'text/javascript',body:`import {createRetainedView} from './retained-view.js';
      window.ctx=${JSON.stringify(state)};
      window.retained=createRetainedView({getContext:()=>window.ctx,setBusy:value=>{window.ctx.busy=value;window.retained?.update();},showResult:(value,summary)=>{window.latest={value,summary};},api:async(path,options)=>{const response=await fetch(path,{...options,cache:'no-store'});if(!response.ok){const value=await response.json();const error=new Error(value.error);error.dispatched=value.dispatched;error.alternative=value.alternative;throw error;}return response.json();}});window.retained.update();window.fixtureReady=true;`});
    const name=url.pathname==='/'?'index.html':url.pathname.slice(1);
    if(!new Set(['index.html','styles.css','retained-view.js']).has(name))return route.fulfill({status:404,body:''});
    let bytes=await fs.readFile(path.join(root,name));
    if(name==='index.html')bytes=Buffer.from(bytes.toString().replace(/<script[^>]*src="\.\/pwa.js"[^>]*><\/script>/,'').replace(/<script[^>]*src="\.\/app.js"[^>]*><\/script>/,'<script type="module" src="./retained-fixture.js"></script>'));
    return route.fulfill({contentType:name.endsWith('.js')?'text/javascript':name.endsWith('.css')?'text/css':'text/html',body:bytes});
  });
  await page.goto('http://127.0.0.1:18127/');await page.waitForFunction(()=>window.fixtureReady);
  const update=async patch=>page.evaluate(patch=>{Object.assign(window.ctx,patch);window.retained.update();},patch);
  const prepare=async()=>{await page.locator('#native-prepare').click();await page.waitForFunction(()=>document.querySelector('#native-review').hidden===false);};
  const stored=()=>page.evaluate(key=>JSON.parse(sessionStorage.getItem(key)||'null'),storageKey);
  t.after(()=>assert.deepEqual(errors,[],'No uncaught UI errors'));
  return {page,calls,update,prepare,stored,setOwnerStage:value=>{ownerStage=value;},setObservationStage:value=>{observedStage=value;}};
}

test('prepare exposes exact review and executes original binding once',async t=>{
  const f=await fixture(t);await f.prepare();
  assert.equal(await f.page.locator('#native-package').textContent(),'com.example.app');
  assert.match(await f.page.locator('#native-version').textContent(),/1.7.*7/);
  assert.equal(await f.page.locator('#native-artifact-sha').textContent(),sha);
  assert.equal(await f.page.locator('#native-target').textContent(),'QUEST-A');
  assert.equal(await f.page.locator('#native-execute').isDisabled(),true);
  await fs.mkdir(path.resolve(__dirname,'../../local'),{recursive:true});
  await f.page.locator('#retained-view').screenshot({path:path.resolve(__dirname,'../../local/retained-view-desktop.png')});
  assert.deepEqual(f.calls[0].body,{artifactId:'4'.repeat(32),sha256:sha,sizeBytes:3,command:'apk.install-inspected',serial:'QUEST-A',installOptions:{replaceExisting:true,grantRuntimePermissions:false}});
  await f.page.locator('#native-confirm').check();await f.page.locator('#native-execute').click();
  await f.page.waitForFunction(()=>window.latest?.value.stage==='completed');
  assert.deepEqual(f.calls.filter(c=>c.path.endsWith('/execute')).map(c=>c.body),[{operationId:id,commandDigest:digest}]);
  assert.equal(await f.page.locator('#native-execute').isDisabled(),true);
  assert.equal(JSON.stringify(await f.stored()).includes('never-persist'),false);
});

test('scope changes reset approval and block mismatched execution',async t=>{
  const f=await fixture(t);await f.prepare();await f.page.locator('#native-confirm').check();
  await f.update({selected:{serial:'QUEST-B',isReady:true}});
  assert.equal(await f.page.locator('#native-confirm').isChecked(),false);assert.equal(await f.page.locator('#native-execute').isDisabled(),true);
  await f.update({selected:{serial:'QUEST-A',isReady:true}});await f.page.locator('#native-confirm').check();
  await f.page.locator('#native-grant').check();
  assert.equal(await f.page.locator('#native-confirm').isChecked(),false);assert.equal(await f.page.locator('#native-confirm').isDisabled(),true);
  await f.page.locator('#native-grant').uncheck();await f.page.locator('#native-confirm').check();
  await f.update({artifact:{id:'6'.repeat(32),sha256:'7'.repeat(64),sizeBytes:3}});
  assert.equal(await f.page.locator('#native-execute').isDisabled(),true);
  assert.equal(f.calls.length,1);
});

test('late prepare reply cannot restore verification after reconnect to the same owner session',async t=>{
  let finish;
  const f=await fixture(t,{preflight:async(route,body,value)=>{await new Promise(resolve=>{finish=resolve;});await route.fulfill({json:value});}});
  await f.page.locator('#native-prepare').click();
  await f.page.waitForFunction(()=>window.ctx.busy);
  await f.update({connected:false});await f.update({connected:true});
  finish();await f.page.waitForFunction(()=>!document.querySelector('#native-review').hidden);
  assert.equal(await f.page.locator('#native-confirm').isDisabled(),true);
  await f.page.locator('#native-status').click();await f.page.waitForFunction(()=>window.latest?.value.stage==='preflighted');
  assert.equal(await f.page.locator('#native-confirm').isDisabled(),false);
});

test('lost execute reply survives reload and new authentication without replay',async t=>{
  const f=await fixture(t,{execute:route=>route.abort('failed')});await f.prepare();await f.page.locator('#native-confirm').check();await f.page.locator('#native-execute').click();
  await f.page.waitForFunction(()=>window.latest?.summary.includes('unknown'));
  assert.equal(await f.page.evaluate(()=>window.retained.hasUnresolved()),true);
  assert.equal((await f.stored()).operationId,id);
  const before=f.calls.length;
  await f.page.reload();await f.page.waitForFunction(()=>window.fixtureReady);
  await f.update({connected:false});await f.update({connected:true,sessionId:'session-b'});
  assert.equal(f.calls.length,before);assert.equal(await f.page.locator('#native-execute').isDisabled(),true);
  f.setOwnerStage('outcomeUnknownRecoveryRequired');await f.page.locator('#native-status').click();
  await f.page.waitForFunction(()=>window.latest?.value.stage==='outcomeUnknownRecoveryRequired');
  assert.equal(await f.page.locator('#native-prepare').isDisabled(),true);
  assert.equal(f.calls.filter(c=>c.path.endsWith('/execute')).length,1);
  assert.match(await f.page.locator('#native-recovery').textContent(),/typed owner recovery/);
  f.setOwnerStage('preflighted');await f.page.locator('#native-status').click();
  await f.page.waitForFunction(()=>window.latest?.value.stage==='preflighted');
  assert.equal(await f.page.locator('#native-execute').isDisabled(),true);
  assert.equal(f.calls.filter(c=>c.path.endsWith('/execute')).length,1);
});

test('missing transient source uses retained copy after explicit original status without reupload',async t=>{
  const f=await fixture(t);await f.prepare();await f.page.locator('#native-confirm').check();
  await f.update({artifact:null});
  assert.equal(await f.page.locator('#native-confirm').isChecked(),false);
  assert.equal(await f.page.locator('#native-confirm').isDisabled(),true);
  await f.page.reload();await f.page.waitForFunction(()=>window.fixtureReady);
  await f.update({artifact:null,sessionId:'new-auth'});
  assert.equal(await f.page.locator('#native-execute').isDisabled(),true);
  await f.page.locator('#native-status').click();await f.page.waitForFunction(()=>window.latest?.value.stage==='preflighted');
  await f.page.locator('#native-confirm').check();await f.page.locator('#native-execute').click();
  await f.page.waitForFunction(()=>window.latest?.value.stage==='completed');
  assert.deepEqual(f.calls.map(call=>call.path.split('/').at(-1)),['preflight','status','execute']);
});

test('restored metadata is allowlisted and never stores API captures, paths or credentials',async t=>{
  const f=await fixture(t);await f.prepare();
  await f.page.evaluate(key=>{const value=JSON.parse(sessionStorage.getItem(key));value.secret='fixture-bearer-secret';value.path='C:/private/staged.apk';value.capture='sensitive-capture';value.review.secret='nested-secret';sessionStorage.setItem(key,JSON.stringify(value));},storageKey);
  await f.page.reload();await f.page.waitForFunction(()=>window.fixtureReady);
  const json=JSON.stringify(await f.stored());
  for(const forbidden of ['fixture-bearer-secret','C:/private','sensitive-capture','nested-secret','retained://'])assert.equal(json.includes(forbidden),false);
  assert.equal(f.calls.length,1);assert.equal(await f.page.locator('#native-execute').isDisabled(),true);
});

test('expired approval cannot dispatch execution and nothing refreshes automatically',async t=>{
  const f=await fixture(t,{preflight:(route,body,value)=>route.fulfill({json:{...value,expiresAt:'2000-01-01T00:00:00Z'}})});
  await f.prepare();assert.equal(await f.page.locator('#native-confirm').isDisabled(),true);
  assert.equal(await f.page.locator('#native-execute').isDisabled(),true);
  assert.match(await f.page.locator('#native-state').textContent(),/expired/);
  assert.equal(f.calls.length,1);
});

test('missing capability and transient uncertainty prevent mutating dispatch',async t=>{
  const f=await fixture(t,{capability:null});
  assert.equal(await f.page.locator('#native-prepare').isDisabled(),true);assert.match(await f.page.locator('#native-capability').textContent(),/desktop|CLI|Windows/);
  await f.update({capability:{supported:true},unresolvedTransient:true});
  assert.equal(await f.page.locator('#native-prepare').isDisabled(),true);
  await f.page.locator('#native-command').selectOption('runtime.observe');
  assert.equal(await f.page.locator('#native-prepare').isDisabled(),false);
  assert.equal(f.calls.length,0);
});

test('explicit local connection works without internet while disconnected controls stay inert',async t=>{
  const f=await fixture(t);
  await f.page.evaluate(()=>{Object.defineProperty(navigator,'onLine',{value:false,configurable:true});window.retained.update();});
  assert.equal(await f.page.locator('#native-prepare').isDisabled(),false);
  await f.update({connected:false});
  assert.equal(await f.page.locator('#native-prepare').isDisabled(),true);
  assert.match(await f.page.locator('#native-capability').textContent(),/Connect/);
  assert.equal(f.calls.length,0);
});

test('lazy owner admission rejection shows the supported owner procedure',async t=>{
  const f=await fixture(t,{preflight:route=>route.fulfill({status:503,json:{error:'state_in_use',dispatched:false,alternative:'Use the existing retained owner or stop its process through the supported owner procedure.'}})});
  await f.page.locator('#native-prepare').click();await f.page.waitForFunction(()=>window.latest?.value.state==='preparationUnavailable');
  assert.match(await f.page.locator('#native-state').textContent(),/supported owner procedure/);
  assert.equal(f.calls.length,1);
});

test('original status and cancel stay available during executing request',async t=>{
  let finish;
  const f=await fixture(t,{execute:async(route,body,value,setStage)=>{await new Promise(resolve=>{finish=resolve;});setStage('cancelled');await route.fulfill({json:{...value,stage:'cancelled'}});}});
  await f.prepare();await f.page.locator('#native-confirm').check();await f.page.locator('#native-execute').click();
  await f.page.waitForFunction(()=>window.ctx.busy);
  assert.equal(await f.page.locator('#native-status').isDisabled(),false);assert.equal(await f.page.locator('#native-cancel').isDisabled(),false);
  await f.page.locator('#native-cancel').click();await f.page.waitForFunction(()=>window.latest?.value.stage==='cancellationRequested');
  assert.equal(await f.page.evaluate(()=>window.ctx.busy),true);
  assert.deepEqual(f.calls.find(c=>c.path.endsWith('/cancel')).body,{operationId:id,commandDigest:digest});
  finish();await f.page.waitForFunction(()=>!window.ctx.busy&&window.latest?.value.stage==='cancelled');
});

test('review data stays literal and mobile view has no horizontal overflow',async t=>{
  const f=await fixture(t,{width:375,preflight:(route,body,value)=>route.fulfill({json:{...value,artifact:{...value.artifact,identity:{...value.artifact.identity,packageName:'<img src=x onerror="window.injected=true">',versionName:'<script>bad</script>'}}}})});
  await f.prepare();assert.equal(await f.page.locator('#native-package img').count(),0);assert.match(await f.page.locator('#native-package').textContent(),/<img/);
  assert.equal(await f.page.evaluate(()=>Boolean(window.injected)),false);
  assert.equal(await f.page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth),true);
  await fs.mkdir(path.resolve(__dirname,'../../local'),{recursive:true});
  await f.page.locator('#retained-view').screenshot({path:path.resolve(__dirname,'../../local/retained-view-mobile.png')});
});

test('manual reference requires explicit original status and never authorizes execution',async t=>{
  const f=await fixture(t);await f.update({artifact:null,selected:null});
  await f.page.locator('#retained-view details summary').click();
  await f.page.locator('#native-original-id').fill(id);await f.page.locator('#native-original-digest').fill(digest);
  assert.equal(f.calls.length,0);await f.page.locator('#native-read-reference').click();
  await f.page.waitForFunction(()=>window.latest?.value.stage==='preflighted');
  assert.deepEqual(f.calls[0].body,{operationId:id,commandDigest:digest});
  assert.equal(await f.page.locator('#native-execute').isDisabled(),true);
});

async function primaryUnknown(f){
  await f.prepare();await f.page.locator('#native-confirm').check();await f.page.locator('#native-execute').click();
  await f.page.waitForFunction(()=>window.latest?.value.stage==='outcomeUnknownRecoveryRequired');
  return f.stored();
}
const unknownExecute=(route,body,value,setStage)=>{setStage('outcomeUnknownRecoveryRequired');return route.fulfill({json:{...value,stage:'outcomeUnknownRecoveryRequired'}});};

test('separate runtime observation keeps primary unknown and exposes its own selected scope',async t=>{
  const f=await fixture(t,{execute:unknownExecute});const original=await primaryUnknown(f);
  await f.update({selected:{serial:'QUEST-B',isReady:true},artifact:{id:'6'.repeat(32),sha256:'7'.repeat(64),sizeBytes:4}});
  assert.equal(await f.page.locator('#native-observe-target').textContent(),'QUEST-B');
  await f.page.locator('#native-observe').click();await f.page.waitForFunction(()=>window.latest?.value.operationId==='api-'+'9'.repeat(32)&&window.latest.value.stage==='completed');
  assert.deepEqual(await f.stored(),original);
  assert.deepEqual(f.calls.slice(-2).map(call=>call.body),[
    {artifactId:'6'.repeat(32),sha256:'7'.repeat(64),sizeBytes:4,command:'runtime.observe',serial:'QUEST-B'},
    {operationId:observationId,commandDigest:observationDigest}
  ]);
  assert.equal(await f.page.locator('#native-prepare').isDisabled(),true);
  assert.equal(await f.page.evaluate(()=>window.retained.hasUnresolved()),true);
  assert.match(await f.page.locator('#native-observation-state').textContent(),/separate|Separate/);
  const observed=await f.page.evaluate(key=>JSON.parse(sessionStorage.getItem(key)),observationKey);
  assert.equal(observed.target,'QUEST-B');assert.equal(observed.command,'runtime.observe');
  for(const forbidden of ['C:/private','private-observation-capture','private-observation-secret','retained://'])assert.equal(JSON.stringify(observed).includes(forbidden),false);
  const count=f.calls.length;
  await f.page.reload();await f.page.waitForFunction(()=>window.fixtureReady);
  assert.equal(f.calls.length,count);assert.deepEqual(await f.stored(),original);
});

test('lost observation reply retains separate binding and never replays on reload or status',async t=>{
  const f=await fixture(t,{execute:unknownExecute,observationExecute:route=>route.abort('failed')});const original=await primaryUnknown(f);
  await f.page.locator('#native-observe').click();await f.page.waitForFunction(()=>document.querySelector('#native-observation-state').textContent.includes('unknown'));
  assert.equal(await f.page.locator('#native-observe').isDisabled(),true);
  const observed=await f.page.evaluate(key=>JSON.parse(sessionStorage.getItem(key)),observationKey);
  assert.equal(observed.operationId,observationId);assert.equal(observed.attempted,true);
  const count=f.calls.length;
  await f.page.reload();await f.page.waitForFunction(()=>window.fixtureReady);
  assert.equal(f.calls.length,count);
  f.setObservationStage('outcomeUnknownRecoveryRequired');await f.page.locator('#native-observation-status').click();
  await f.page.waitForFunction(()=>document.querySelector('#native-observation-state').textContent.includes('outcomeUnknownRecoveryRequired'));
  assert.deepEqual(await f.stored(),original);assert.equal(await f.page.locator('#native-observe').isDisabled(),true);
  assert.equal(f.calls.filter(call=>call.path.endsWith('/execute')&&call.body.operationId===observationId).length,1);
  await f.page.locator('#native-observation-cancel').click();
  await f.page.waitForFunction(()=>document.querySelector('#native-observation-state').textContent.includes('outcomeUnknownRecoveryRequired'));
  assert.equal(await f.page.locator('#native-observe').isDisabled(),true);assert.deepEqual(await f.stored(),original);
});

test('observation cancellation stays requested until owner execution acknowledgement',async t=>{
  let finish;
  const f=await fixture(t,{execute:unknownExecute,observationExecute:async(route,body,value,setStage)=>{await new Promise(resolve=>{finish=resolve;});setStage('cancelled');await route.fulfill({json:{...value,stage:'cancelled'}});}});
  const original=await primaryUnknown(f);
  await f.page.locator('#native-observe').click();await f.page.waitForFunction(()=>document.querySelector('#native-observation-state').textContent.includes('sent once'));
  await f.page.locator('#native-observation-cancel').click();await f.page.waitForFunction(()=>document.querySelector('#native-observation-state').textContent.includes('cancellationRequested'));
  assert.equal(await f.page.evaluate(()=>window.ctx.busy),true);assert.equal(await f.page.locator('#native-observe').isDisabled(),true);
  finish();await f.page.waitForFunction(()=>!window.ctx.busy&&document.querySelector('#native-observation-state').textContent.includes('cancelled'));
  assert.equal(await f.page.locator('#native-observe').isDisabled(),false);assert.deepEqual(await f.stored(),original);
});

test('scope change during separate preflight retains reference without executing or replacing primary',async t=>{
  let finish;
  const f=await fixture(t,{execute:unknownExecute,observationPreflight:async(route,body,value)=>{await new Promise(resolve=>{finish=resolve;});await route.fulfill({json:value});}});
  const original=await primaryUnknown(f);
  await f.page.locator('#native-observe').click();await f.page.waitForFunction(()=>window.ctx.busy);
  await f.update({selected:{serial:'QUEST-B',isReady:true}});finish();
  await f.page.waitForFunction(()=>document.querySelector('#native-observation-state').textContent.includes('scope'));
  assert.equal(f.calls.filter(call=>call.path.endsWith('/execute')&&call.body.operationId===observationId).length,0);
  assert.deepEqual(await f.stored(),original);assert.equal(await f.page.locator('#native-observe').isDisabled(),true);
  assert.equal(await f.page.locator('#native-observation-cancel').isDisabled(),true);
  assert.match(await f.page.locator('#native-observation-state').textContent(),/expiry/);
  f.setObservationStage('expired');await f.page.locator('#native-observation-status').click();await f.page.waitForFunction(()=>document.querySelector('#native-observation-state').textContent.includes('expired'));
  assert.equal(await f.page.locator('#native-observe').isDisabled(),false);
});

test('known preflighted original cannot be cancelled and needs explicit status after expiry',async t=>{
  const f=await fixture(t);await f.prepare();
  assert.equal(await f.page.locator('#native-cancel').isDisabled(),true);
  assert.match(await f.page.locator('#native-state').textContent(),/expiry/);
  await f.page.locator('#native-cancel').dispatchEvent('click');
  assert.equal(f.calls.filter(call=>call.path.endsWith('/cancel')).length,0);
  f.setOwnerStage('expired');await f.page.locator('#native-status').click();await f.page.waitForFunction(()=>window.latest?.value.stage==='expired');
  assert.equal(await f.page.locator('#native-prepare').isDisabled(),false);
});

test('reconnect during separate preflight preserves binding without executing even for same scope',async t=>{
  let finish;
  const f=await fixture(t,{execute:unknownExecute,observationPreflight:async(route,body,value)=>{await new Promise(resolve=>{finish=resolve;});await route.fulfill({json:value});}});
  const original=await primaryUnknown(f);
  await f.page.locator('#native-observe').click();await f.page.waitForFunction(()=>window.ctx.busy);
  await f.update({connected:false});await f.update({connected:true});finish();
  await f.page.waitForFunction(()=>document.querySelector('#native-observation-state').textContent.includes('authentication'));
  assert.equal(f.calls.filter(call=>call.path.endsWith('/execute')&&call.body.operationId===observationId).length,0);
  assert.equal(await f.page.locator('#native-observation-id').textContent(),observationId);
  assert.equal(await f.page.locator('#native-observation-cancel').isDisabled(),true);
  assert.deepEqual(await f.stored(),original);
});

test('separate observation needs available owner, staged source and one ready selected target',async t=>{
  const f=await fixture(t,{capability:null});assert.equal(await f.page.locator('#native-observe').isDisabled(),true);
  await f.update({capability:{supported:true},artifact:null});assert.equal(await f.page.locator('#native-observe').isDisabled(),true);
  await f.update({artifact:{id:'4'.repeat(32),sha256:sha,sizeBytes:3},selected:{serial:'QUEST-A',isReady:false}});
  assert.equal(await f.page.locator('#native-observe').isDisabled(),true);assert.equal(f.calls.length,0);
});
