// Shipped DOM + retained module; every request intercepted, no host/device/listener.
const test=require('node:test'),assert=require('node:assert/strict'),fs=require('node:fs/promises'),path=require('node:path');
const {chromium}=require('playwright');
const root=path.resolve(__dirname,'../../src/QuestIonAbleFileManager.BrowserHost/wwwroot');
const v1='questionable.file_manager.local_api.v1',v2='questionable.file_manager.local_api.v2';
const key='qfm-browser-retained-v1',obsKey='qfm-browser-observation-v1';
const id='api-'+'1'.repeat(32),digest='2'.repeat(64),sha='3'.repeat(64),obsId='api-'+'9'.repeat(32),obsDigest='a'.repeat(64);
const artifact={id:'4'.repeat(32),fileName:'fixture.apk',sizeBytes:3,sha256:sha};
let browser;test.before(async()=>{browser=await chromium.launch({headless:true});});test.after(async()=>browser?.close());
async function fixture(t,{capability={supported:true,contractVersions:[v1,v2]},respond,width=1280}={}){
  const context=await browser.newContext({serviceWorkers:'block',viewport:{width,height:1000}});t.after(()=>context.close());
  const page=await context.newPage(),calls=[],errors=[];page.on('pageerror',error=>errors.push(error.message));
  let original=null,observation=null,stage='preflighted',observationStage='preflighted';
  const expiresAt=new Date(Date.now()+120000).toISOString();
  await context.route('**/*',async route=>{
    const url=new URL(route.request().url());if(url.origin!=='http://127.0.0.1:18127')return route.abort('blockedbyclient');
    if(url.pathname.startsWith('/browser/')){
      const body=route.request().postDataJSON(),action=url.pathname.split('/').at(-1);calls.push({path:url.pathname,body});
      const version=url.pathname.includes('/v2/')?v2:v1,isObservation=body.command==='runtime.observe'||body.operationId===obsId;
      if(action==='preflight'){
        const value={contractVersion:version,operationId:isObservation?obsId:id,commandDigest:isObservation?obsDigest:digest,command:body.command,expiresAt,
          artifact:{path:`retained://${isObservation?obsId:id}/base.apk`,sizeBytes:body.sizeBytes,sha256:body.sha256,identity:{packageName:'com.example.app',versionName:'1.7',versionCode:7,signerSha256:'5'.repeat(64)}},target:{serial:body.serial,isReady:true},
          ...(version===v2?{installOptions:body.installOptions||{replaceExisting:true,grantRuntimePermissions:false}}:{})};
        if(isObservation){observation=value;observationStage='preflighted';}else{original=value;stage='preflighted';}
        if(respond)return respond(route,body,value,{action,version,isObservation,setStage:value=>{stage=value;}});
        return route.fulfill({json:value});
      }
      const prepared=isObservation?observation:original;
      const value={contractVersion:version,operationId:body.operationId,commandDigest:body.commandDigest,command:prepared?.command||(version===v2?'apk.deploy':'apk.install-inspected'),expiresAt:prepared?.expiresAt||expiresAt,
        stage:action==='execute'?'completed':isObservation?observationStage:stage,result:null};
      if(action==='execute'){if(isObservation)observationStage='completed';else stage='completed';}
      if(respond)return respond(route,body,value,{action,version,isObservation,setStage:value=>{stage=value;}});
      return route.fulfill({json:value});
    }
    if(url.pathname==='/deploy-fixture.js')return route.fulfill({contentType:'text/javascript',body:`import {createRetainedView} from './retained-view.js';
      window.ctx={sessionId:'session-a',connected:true,busy:false,selected:{serial:'QUEST-A',isReady:true},artifact:${JSON.stringify(artifact)},capability:${JSON.stringify(capability)},unresolvedTransient:false};
      window.retained=createRetainedView({getContext:()=>window.ctx,setBusy:value=>{window.ctx.busy=value;window.retained?.update();},showResult:(value,summary)=>window.latest={value,summary},
      api:async(path,options)=>{const response=await fetch(path,{...options,cache:'no-store'});if(!response.ok){const body=await response.json();throw Object.assign(new Error(body.error),body);}return response.json();}});window.retained.update();window.fixtureReady=true;`});
    const name=url.pathname==='/'?'index.html':url.pathname.slice(1);if(!['index.html','retained-view.js','styles.css'].includes(name))return route.fulfill({status:404,body:''});
    let bytes=await fs.readFile(path.join(root,name));if(name==='index.html')bytes=Buffer.from(bytes.toString().replace(/<script[^>]*src="\.\/pwa.js"[^>]*><\/script>/,'').replace(/<script[^>]*src="\.\/app.js"[^>]*><\/script>/,'<script type="module" src="./deploy-fixture.js"></script>'));
    return route.fulfill({contentType:name.endsWith('.js')?'text/javascript':name.endsWith('.css')?'text/css':'text/html',body:bytes});
  });
  await page.goto('http://127.0.0.1:18127/');await page.waitForFunction(()=>window.fixtureReady);
  const update=patch=>page.evaluate(patch=>{Object.assign(window.ctx,patch);window.retained.update();},patch);
  const select=()=>page.locator('#native-command').selectOption('apk.deploy');
  const prepare=async()=>{await select();await page.locator('#native-prepare').click();await page.waitForFunction(()=>!window.ctx.busy);};
  const execute=async()=>{await page.locator('#native-confirm').check();await page.locator('#native-execute').click();await page.waitForFunction(()=>!window.ctx.busy);};
  const stored=()=>page.evaluate(key=>JSON.parse(sessionStorage.getItem(key)||'null'),key);
  t.after(()=>assert.deepEqual(errors,[],'No uncaught UI errors'));
  return {page,calls,update,select,prepare,execute,stored,setStage:value=>{stage=value;}};
}

test('older capability leaves deploy unavailable and ordinary preparation stays v1',async t=>{
  const f=await fixture(t,{capability:{supported:true}});
  assert.equal(await f.page.locator('#native-command option[value="apk.deploy"]').evaluate(option=>option.disabled),true);
  await f.page.evaluate(()=>{document.querySelector('#native-command').value='apk.deploy';window.retained.update();});
  assert.equal(await f.page.locator('#native-prepare').isDisabled(),true);assert.equal(f.calls.length,0);
  await f.page.locator('#native-command').selectOption('apk.install-inspected');await f.page.locator('#native-prepare').click();await f.page.waitForFunction(()=>!window.ctx.busy);
  assert.equal(f.calls[0].path,'/browser/v1/retained/preflight');assert.equal('contractVersion' in f.calls[0].body,false);
});

test('only an exact advertised v2 array enables deploy and capability changes send nothing',async t=>{
  const f=await fixture(t);
  for(const contractVersions of [[v1],[v2.toUpperCase()],v2]){
    await f.update({capability:{supported:true,contractVersions}});
    assert.equal(await f.page.locator('#native-command option[value="apk.deploy"]').evaluate(option=>option.disabled),true);
  }
  await f.update({capability:{supported:true,contractVersions:[v1,v2]}});
  assert.equal(await f.page.locator('#native-command option[value="apk.deploy"]').evaluate(option=>option.disabled),false);assert.equal(f.calls.length,0);
});

test('v2 deploy reviews exact version policy effects and normalized install options before one execute',async t=>{
  const f=await fixture(t);await f.prepare();
  assert.deepEqual(f.calls[0],{path:'/browser/v2/retained/preflight',body:{contractVersion:v2,artifactId:artifact.id,sha256:sha,sizeBytes:3,command:'apk.deploy',serial:'QUEST-A',installOptions:{replaceExisting:true,grantRuntimePermissions:false}}});
  assert.equal(await f.page.locator('#native-execute').isDisabled(),true);
  await f.page.locator('#native-execute').dispatchEvent('click');assert.equal(f.calls.length,1);
  assert.equal(await f.page.locator('#native-contract').textContent(),v2);assert.match(await f.page.locator('#native-policy').textContent(),/exact-installed-install-launch.v1/);
  assert.match(await f.page.locator('#native-effects').textContent(),/install.*launch/i);assert.match(await f.page.locator('#native-effects').textContent(),/OpenXR.*unknown/i);
  assert.equal((await f.stored()).contractVersion,v2);assert.equal((await f.stored()).command,'apk.deploy');
  await f.execute();assert.equal(f.calls[1].path,'/browser/v2/retained/execute');assert.deepEqual(f.calls[1].body,{contractVersion:v2,operationId:id,commandDigest:digest});
  assert.equal(await f.page.locator('#native-execute').isDisabled(),true);assert.match(await f.page.locator('#native-state').textContent(),/OpenXR.*unknown/i);
});

for(const version of [v1,null,'questionable.file_manager.local_api.v3'])test(`crossed v2 preflight version ${version} cannot grant approval`,async t=>{
  const f=await fixture(t,{respond:(route,body,value,meta)=>{if(meta.action==='preflight'){if(version===null)delete value.contractVersion;else value.contractVersion=version;}return route.fulfill({json:value});}});
  await f.prepare();assert.equal(await f.page.locator('#native-confirm').isDisabled(),true);assert.equal(await f.stored(),null);assert.equal(f.calls.length,1);
  assert.equal(f.calls[0].path,'/browser/v2/retained/preflight');
});

for(const field of ['options','source','target','command','digest'])test(`crossed prepared deploy ${field} cannot grant approval`,async t=>{
  const f=await fixture(t,{respond:(route,body,value,meta)=>{if(meta.action==='preflight'){
    if(field==='options')value.installOptions.grantRuntimePermissions=true;if(field==='source')value.artifact.sha256='f'.repeat(64);if(field==='target')value.target.serial='QUEST-B';if(field==='command')value.command='apk.install-inspected';if(field==='digest')value.commandDigest='bad';
  }return route.fulfill({json:value});}});
  await f.prepare();assert.equal(await f.page.locator('#native-confirm').isDisabled(),true);assert.equal(await f.stored(),null);assert.equal(f.calls.length,1);
});

test('deploy options target and selector changes reset approval without changing original routing',async t=>{
  const f=await fixture(t);await f.prepare();await f.page.locator('#native-confirm').check();await f.page.locator('#native-grant').check();
  assert.equal(await f.page.locator('#native-confirm').isChecked(),false);assert.equal(await f.page.locator('#native-execute').isDisabled(),true);
  await f.page.locator('#native-grant').uncheck();await f.page.locator('#native-confirm').check();await f.update({selected:{serial:'QUEST-B',isReady:true}});
  assert.equal(await f.page.locator('#native-confirm').isChecked(),false);await f.page.locator('#native-command').selectOption('apk.install-inspected');
  await f.page.locator('#native-status').click();await f.page.waitForFunction(()=>window.latest?.value.stage==='preflighted');
  assert.equal(f.calls.at(-1).path,'/browser/v2/retained/status');assert.equal(f.calls.at(-1).body.contractVersion,v2);assert.equal(await f.page.locator('#native-execute').isDisabled(),true);
});

test('lost v2 execute survives reload and status without replay or version fallback',async t=>{
  const f=await fixture(t,{respond:(route,body,value,meta)=>meta.action==='execute'?route.abort('failed'):route.fulfill({json:value})});
  await f.prepare();await f.execute();assert.equal((await f.stored()).stage,'clientUncertain');const before=f.calls.length;
  await f.page.reload();await f.page.waitForFunction(()=>window.fixtureReady);await f.update({sessionId:'session-b'});assert.equal(f.calls.length,before);
  f.setStage('outcomeUnknownRecoveryRequired');await f.page.locator('#native-status').click();await f.page.waitForFunction(()=>window.latest?.value.stage==='outcomeUnknownRecoveryRequired');
  assert.equal(f.calls.at(-1).path,'/browser/v2/retained/status');assert.equal((await f.stored()).contractVersion,v2);assert.equal(await f.page.evaluate(()=>window.retained.hasUnresolved()),true);
  assert.equal(f.calls.filter(call=>call.path.endsWith('/execute')).length,1);
  assert.equal(await f.page.locator('#native-prepare').isDisabled(),true);
});

test('wrong version execute reply cannot promote an uncertain deploy to completed',async t=>{
  const f=await fixture(t,{respond:(route,body,value,meta)=>route.fulfill({json:meta.action==='execute'?{...value,contractVersion:v1,stage:'completed'}:value})});
  await f.prepare();await f.execute();assert.equal((await f.stored()).stage,'clientUncertain');assert.equal((await f.stored()).contractVersion,v2);assert.equal(await f.page.evaluate(()=>window.retained.hasUnresolved()),true);
  assert.equal(f.calls.length,2);
});

test('wrong version execute reply preserves a concurrently confirmed owner unknown stage',async t=>{
  let finish;const f=await fixture(t,{respond:async(route,body,value,meta)=>{
    if(meta.action==='execute'){await new Promise(resolve=>{finish=resolve;});return route.fulfill({json:{...value,contractVersion:v1,stage:'completed'}});}
    if(meta.action==='status')return route.fulfill({json:{...value,stage:'outcomeUnknownRecoveryRequired'}});
    return route.fulfill({json:value});}});
  await f.prepare();await f.page.locator('#native-confirm').check();await f.page.locator('#native-execute').click();await f.page.waitForFunction(()=>window.ctx.busy);
  await f.page.locator('#native-status').click();await f.page.waitForFunction(()=>window.latest?.value.stage==='outcomeUnknownRecoveryRequired');finish();await f.page.waitForFunction(()=>!window.ctx.busy);
  assert.equal((await f.stored()).stage,'outcomeUnknownRecoveryRequired');assert.equal((await f.stored()).contractVersion,v2);
});

test('v2 status and cancellation retain original version while execution owns busy state',async t=>{
  let finish;const f=await fixture(t,{respond:async(route,body,value,meta)=>{
    if(meta.action==='execute'){meta.setStage('running');await new Promise(resolve=>{finish=resolve;});return route.fulfill({json:{...value,stage:'cancelled'}});}
    if(meta.action==='cancel'){meta.setStage('cancellationRequested');return route.fulfill({json:{...value,stage:'cancellationRequested'}});}
    return route.fulfill({json:value});}});
  await f.prepare();await f.page.locator('#native-confirm').check();await f.page.locator('#native-execute').click();await f.page.waitForFunction(()=>window.ctx.busy);
  await f.page.locator('#native-status').click();await f.page.waitForFunction(()=>window.latest?.value.stage==='running');
  await f.page.locator('#native-cancel').click();await f.page.waitForFunction(()=>window.latest?.value.stage==='cancellationRequested');
  assert.equal(await f.page.evaluate(()=>window.ctx.busy),true);
  assert.deepEqual(f.calls.slice(-2),['status','cancel'].map(action=>({path:`/browser/v2/retained/${action}`,body:{contractVersion:v2,operationId:id,commandDigest:digest}})));
  finish();await f.page.waitForFunction(()=>!window.ctx.busy);assert.equal((await f.stored()).stage,'cancelled');
});

test('wrong version status cannot overwrite primary unknown and separate observation stays v1',async t=>{
  let crossed=false;const f=await fixture(t,{respond:(route,body,value,meta)=>{
    if(meta.action==='execute'&&!meta.isObservation){meta.setStage('outcomeUnknownRecoveryRequired');return route.fulfill({json:{...value,stage:'outcomeUnknownRecoveryRequired'}});}
    return route.fulfill({json:crossed&&meta.action==='status'&&!meta.isObservation?{...value,contractVersion:v1,stage:'completed'}:value});}});
  await f.prepare();await f.execute();const original=await f.stored();crossed=true;await f.page.locator('#native-status').click();await f.page.waitForFunction(()=>document.querySelector('#native-state').textContent.includes('unavailable'));
  assert.deepEqual(await f.stored(),original);await f.page.locator('#native-observe').click();await f.page.waitForFunction(()=>!window.ctx.busy);
  assert.deepEqual(await f.stored(),original);assert.deepEqual(f.calls.slice(-2).map(call=>call.path),['/browser/v1/retained/preflight','/browser/v1/retained/execute']);
  assert.equal(f.calls.slice(-2).some(call=>'contractVersion' in call.body),false);
});

test('manual recovery defaults v1 and explicit v2 selection controls routing without inferring ID',async t=>{
  const f=await fixture(t);assert.equal(await f.page.locator('#native-original-contract').inputValue(),v1);
  await f.page.locator('#retained-view details summary').click();await f.page.locator('#native-original-id').fill(id);await f.page.locator('#native-original-digest').fill(digest);
  await f.page.locator('#native-original-contract').selectOption(v2);assert.equal(f.calls.length,0);await f.page.locator('#native-read-reference').click();await f.page.waitForFunction(()=>window.latest?.value.stage==='preflighted');
  assert.deepEqual(f.calls[0],{path:'/browser/v2/retained/status',body:{contractVersion:v2,operationId:id,commandDigest:digest}});
  assert.equal((await f.stored()).contractVersion,v2);assert.equal(await f.page.locator('#native-execute').isDisabled(),true);
  await f.page.locator('#native-original-contract').selectOption(v1);assert.equal(await f.page.locator('#native-read-reference').isDisabled(),true);assert.equal(f.calls.length,1);
});

test('legacy unknown reference without version remains v1 after reload on v2-capable host',async t=>{
  const f=await fixture(t);await f.page.evaluate(({key,id,digest,sha})=>sessionStorage.setItem(key,JSON.stringify({operationId:id,commandDigest:digest,command:'apk.install-inspected',target:'QUEST-A',artifactSha256:sha,attempted:true,stage:'outcomeUnknownRecoveryRequired'})),{key,id,digest,sha});
  await f.page.reload();await f.page.waitForFunction(()=>window.fixtureReady);assert.equal(f.calls.length,0);assert.equal('contractVersion' in await f.stored(),false);
  await f.page.locator('#native-status').click();await f.page.waitForFunction(()=>window.latest?.value.stage==='preflighted');assert.equal(f.calls[0].path,'/browser/v1/retained/status');assert.equal('contractVersion' in f.calls[0].body,false);
  assert.equal(await f.page.locator('#native-execute').isDisabled(),true);
});

test('restored v2 reference is not downgraded when host does not advertise v2',async t=>{
  const f=await fixture(t);await f.prepare();const before=f.calls.length;await f.update({capability:{supported:true}});
  assert.equal(await f.page.locator('#native-status').isDisabled(),true);assert.equal(await f.page.locator('#native-confirm').isDisabled(),true);
  assert.equal((await f.stored()).contractVersion,v2);assert.equal(f.calls.length,before);assert.match(await f.page.locator('#native-state').textContent(),/version|contract/i);
});

test('retained deploy survives missing transient source and reauthentication after explicit original status',async t=>{
  const f=await fixture(t);await f.prepare();await f.page.reload();await f.page.waitForFunction(()=>window.fixtureReady);await f.update({artifact:null,sessionId:'session-b'});
  assert.equal(await f.page.locator('#native-confirm').isDisabled(),true);await f.page.locator('#native-status').click();await f.page.waitForFunction(()=>window.latest?.value.stage==='preflighted');
  await f.execute();assert.deepEqual(f.calls.map(call=>call.path),['preflight','status','execute'].map(action=>`/browser/v2/retained/${action}`));
  assert.equal(f.calls.filter(call=>call.path.endsWith('/preflight')).length,1);
});

test('saved deploy reference allowlists version review and options without paths or result data',async t=>{
  const f=await fixture(t);await f.prepare();await f.page.evaluate(()=>{const original=URL.createObjectURL;URL.createObjectURL=blob=>{blob.text().then(value=>window.savedReference=JSON.parse(value));return original(blob);};});
  await f.page.locator('#native-save').click();await f.page.waitForFunction(()=>window.savedReference);
  const saved=await f.page.evaluate(()=>window.savedReference);assert.equal(saved.contractVersion,v2);assert.deepEqual(saved.installOptions,{replaceExisting:true,grantRuntimePermissions:false});
  assert.equal(JSON.stringify(saved).includes('retained://'),false);assert.equal('result' in saved,false);assert.equal('secret' in saved,false);
});

test('deploy review is literal and fits mobile without claiming application readiness',async t=>{
  const label='<img src=x onerror="window.injected=true">';const f=await fixture(t,{width:390,respond:(route,body,value,meta)=>{if(meta.action==='preflight')value.artifact.identity.packageName=label;return route.fulfill({json:value});}});
  await f.prepare();assert.equal(await f.page.locator('#native-package').textContent(),label);assert.equal(await f.page.locator('#native-package img').count(),0);
  assert.equal(await f.page.evaluate(()=>Boolean(window.injected)),false);assert.equal(await f.page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth),true);
  assert.match(await f.page.locator('#native-effects').textContent(),/readiness.*unknown/i);
});
