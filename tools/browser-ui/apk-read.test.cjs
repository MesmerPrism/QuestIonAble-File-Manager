// Actual shipped DOM/module with intercepted synthetic API; no listener or device.
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs/promises');
const path = require('node:path');
const {chromium} = require('playwright');
const root = path.resolve(__dirname, '../../src/QuestIonAbleFileManager.BrowserHost/wwwroot');
const sha = '3'.repeat(64), artifact = {id:'4'.repeat(32),fileName:'fixture.apk',sha256:sha,sizeBytes:3};
const identity = {packageName:'com.example.app',versionCode:7,versionName:'1.7',signerSha256:'5'.repeat(64)};
const inspected = () => ({path:artifact.fileName,sha256:sha,sizeBytes:3,identity:{...identity}});
const installed = () => ({serial:'QUEST-A',identity:{...identity},apkPaths:['/data/app/example/base.apk'],baseApkSha256:sha,baseApkSizeBytes:3});
const runtime = () => ({artifact:inspected(),installed:installed(),observationContract:'questionable.file_manager.app_runtime_observation.v6',
  isForeground:true,isTopResumed:true,processIds:[123],processAlive:true,processObservationQuality:'pidofReportedProcesses',
  processObservationSource:'fixed serial-scoped pidof derived from inspected package',processCorroboration:{state:'notCollected',absenceAuthority:false},
  foregroundComponents:['com.example.app/.Main'],topResumedComponents:['com.example.app/.Main'],blockingSystemComponents:[],
  applicationReadiness:'unknown',applicationReadinessAuthority:false,openXrReadiness:'unknown',openXrReadinessAuthority:false});
function reply(body) {
  const read={artifact:{...artifact},serial:body.serial,kind:body.action==='apk.preflight'?'preflight':'runtime',verification:body.verification||null,
    preflight:null,runtime:null,development:null};
  if(body.action==='apk.preflight')read.preflight={artifact:inspected(),manifest:{minimumSdkVersion:29,targetSdkVersion:34,launcherActivities:['com.example.app.Main']},
    serial:body.serial,device:{serial:body.serial,isReady:true},deviceApiLevel:32,installedMatch:'exact',installed:installed(),launcherComponent:'com.example.app/.Main',
    readyForDeploy:true,readyForLaunch:true,readyForDiagnose:true,checks:[{id:'device-api',passed:true,detail:'Minimum SDK fits target.'}],nextCommands:[{purpose:'launch',ready:true}],
    preflightContract:'questionable.file_manager.apk_preflight.v1'};
  else if(body.verification==='exact')read.runtime=runtime();
  else read.development={expectedArtifact:inspected(),installedMetadata:{packageName:identity.packageName,versionCode:7,versionName:'1.7',apkPaths:['/data/app/example/base.apk'],lastUpdateTime:'fixture'},
    runtime:{...runtime(),installed:null,observationContract:'questionable.file_manager.development_runtime_facts.v1'},reportedInstallReference:null,observationContract:'questionable.file_manager.development_app_runtime_observation.v1',verificationPolicy:'development-metadata',
    installedBytesVerified:false,installedSignerVerified:false,reportedInstallProvenanceVerified:false,identityLimitation:'Matching metadata does not identify a unique build or prove installed bytes/signer.'};
  return {requestId:body.requestId,action:body.action,state:'completed',result:null,artifactRead:read};
}
let browser;
test.before(async()=>{browser=await chromium.launch({headless:true});});
test.after(async()=>{await browser?.close();});

async function fixture(t,{respond,width=1280,state={}}={}) {
  const context=await browser.newContext({serviceWorkers:'block',viewport:{width,height:900}});t.after(()=>context.close());
  const page=await context.newPage(),calls=[],errors=[];
  page.on('pageerror',error=>errors.push(error.message));
  const initial={sessionId:'session-a',connected:true,busy:false,selected:{serial:'QUEST-A',isReady:true},artifact:{...artifact},actions:['apk.preflight','runtime.observe'],
    artifactStaging:{supported:true,maximumFileBytes:1073741824},...state};
  await context.route('**/*',async route=>{
    const url=new URL(route.request().url());if(url.origin!=='http://127.0.0.1:18127')return route.abort('blockedbyclient');
    if(url.pathname==='/browser/v1/execute'){
      const body=route.request().postDataJSON();calls.push(body);
      return respond?respond(route,body,reply(body)):route.fulfill({json:reply(body)});
    }
    if(url.pathname==='/apk-read-fixture.js')return route.fulfill({contentType:'text/javascript',body:`import {createApkReadView} from './apk-read-view.js';
      window.ctx=${JSON.stringify(initial)};window.ctx.actions=new Set(window.ctx.actions);
      sessionStorage.setItem('original-native','original-unknown');sessionStorage.setItem('original-transfer','transfer-unknown');
      window.readView=createApkReadView({getContext:()=>window.ctx,setBusy:value=>{window.ctx.busy=value;window.readView?.update();},
        showResult:(value,summary)=>{window.latest={value,summary};},api:async(path,options)=>{const response=await fetch(path,{...options,cache:'no-store'});if(!response.ok){const body=await response.json();throw new Error(body.error);}return response.json();}});
      window.readView.update();window.fixtureReady=true;`});
    const name=url.pathname==='/'?'index.html':url.pathname.slice(1);
    if(!['index.html','styles.css','apk-read-view.js'].includes(name))return route.fulfill({status:404,body:''});
    let bytes=await fs.readFile(path.join(root,name));
    if(name==='index.html')bytes=Buffer.from(bytes.toString().replace(/<script[^>]*src="\.\/pwa.js"[^>]*><\/script>/,'').replace(/<script[^>]*src="\.\/app.js"[^>]*><\/script>/,'<script type="module" src="./apk-read-fixture.js"></script>'));
    return route.fulfill({contentType:name.endsWith('.js')?'text/javascript':name.endsWith('.css')?'text/css':'text/html',body:bytes});
  });
  await page.goto('http://127.0.0.1:18127/');await page.waitForFunction(()=>window.fixtureReady);
  const update=patch=>page.evaluate(patch=>{Object.assign(window.ctx,patch);if(Array.isArray(window.ctx.actions))window.ctx.actions=new Set(window.ctx.actions);window.readView.update();},patch);
  const read=async id=>{await page.locator('#'+id).click();await page.waitForFunction(()=>!window.ctx.busy);};
  t.after(()=>assert.deepEqual(errors,[],'No uncaught UI errors'));
  return {page,calls,update,read};
}

test('APK read controls are disabled before JavaScript admission',async t=>{
  const context=await browser.newContext({javaScriptEnabled:false,serviceWorkers:'block'});t.after(()=>context.close());
  await context.route('**/*',async route=>new URL(route.request().url()).pathname==='/'
    ?route.fulfill({contentType:'text/html',body:await fs.readFile(path.join(root,'index.html'))}):route.abort('blockedbyclient'));
  const page=await context.newPage();await page.goto('http://127.0.0.1:18127/');
  for(const id of ['apk-read-preflight','apk-read-exact','apk-read-development'])assert.equal(await page.locator('#'+id).isDisabled(),true);
});

test('readiness renders independent typed readiness facts and sends exact artifact scope once',async t=>{
  const f=await fixture(t);assert.equal(f.calls.length,0);await f.read('apk-read-preflight');
  assert.deepEqual(f.calls.map(({requestId,...body})=>body),[{action:'apk.preflight',serial:'QUEST-A',artifactId:artifact.id,sha256:sha,sizeBytes:3}]);
  assert.match(f.calls[0].requestId,/^[a-f0-9-]{36}$/);assert.equal(await f.page.locator('#apk-read-installed').textContent(),'Exact installed APK');
  for(const id of ['deploy','launch','diagnose'])assert.equal(await f.page.locator('#apk-read-'+id).textContent(),'Ready by APK preflight');
  assert.match(await f.page.locator('#apk-read-checks').textContent(),/Minimum SDK fits target/);
  assert.match(await f.page.locator('#apk-read-application').textContent(),/Unknown/);
});

for(const match of ['absent','different','unverified'])test(`preflight preserves ${match} independently of blocked readiness`,async t=>{
  const f=await fixture(t,{respond:(route,body,value)=>{Object.assign(value.artifactRead.preflight,{installedMatch:match,installed:null,readyForDeploy:false,readyForLaunch:false,readyForDiagnose:false});return route.fulfill({json:value});}});
  await f.read('apk-read-preflight');assert.match(await f.page.locator('#apk-read-installed').textContent(),new RegExp(match==='absent'?'Absent':match==='different'?'Different':'Unverified'));
  assert.equal(await f.page.locator('#apk-read-launch').textContent(),'Not ready by APK preflight');
  assert.equal(f.calls.length,1);
});

test('exact runtime structured absence is distinct from failure and process absence stays unknown',async t=>{
  const f=await fixture(t,{respond:(route,body,value)=>{Object.assign(value.artifactRead.runtime,{installed:null,processIds:[],processAlive:null,isForeground:false,isTopResumed:false,processObservationQuality:'pidofReportedNoProcesses'});return route.fulfill({json:value});}});
  await f.read('apk-read-exact');assert.match(await f.page.locator('#apk-read-installed').textContent(),/Absent/);
  assert.match(await f.page.locator('#apk-read-process').textContent(),/Unknown/);assert.match(await f.page.locator('#apk-read-state').textContent(),/completed/i);
  assert.equal(f.calls[0].verification,'exact');
});

test('failed exact read does not assert absence or fall back to development metadata',async t=>{
  const f=await fixture(t,{respond:(route,body)=>route.fulfill({json:{requestId:body.requestId,action:body.action,state:'failed',error:'installed_identity_mismatch'}})});
  await f.read('apk-read-exact');assert.match(await f.page.locator('#apk-read-state').textContent(),/unavailable/i);
  assert.equal(await f.page.locator('#apk-read-installed').textContent(),'Unknown');assert.equal(f.calls.length,1);assert.equal(f.calls[0].verification,'exact');
});

test('development observation labels weaker evidence and never claims build or readiness proof',async t=>{
  const f=await fixture(t);await f.read('apk-read-development');
  assert.equal(f.calls[0].verification,'development-metadata');assert.match(await f.page.locator('#apk-read-policy').textContent(),/Development metadata/);
  assert.match(await f.page.locator('#apk-read-installed').textContent(),/metadata only/i);
  for(const id of ['apk-read-application','apk-read-openxr'])assert.match(await f.page.locator('#'+id).textContent(),/Unknown/);
  assert.match(await f.page.locator('#apk-read-boundary').textContent(),/installed bytes.*signer.*unique build.*provenance/i);
  assert.equal(f.calls.length,1);
});

for(const flag of ['installedBytesVerified','installedSignerVerified','reportedInstallProvenanceVerified'])test(`development receipt cannot upgrade ${flag}`,async t=>{
  const f=await fixture(t,{respond:(route,body,value)=>{value.artifactRead.development[flag]=true;return route.fulfill({json:value});}});
  await f.read('apk-read-development');assert.equal(await f.page.locator('#apk-read-installed').textContent(),'Unknown');assert.match(await f.page.locator('#apk-read-state').textContent(),/unavailable/i);
  assert.equal(f.calls.length,1);
});

test('exact observation rejects the development facts contract without fallback',async t=>{
  const f=await fixture(t,{respond:(route,body,value)=>{value.artifactRead.runtime.observationContract='questionable.file_manager.development_runtime_facts.v1';return route.fulfill({json:value});}});
  await f.read('apk-read-exact');assert.equal(await f.page.locator('#apk-read-installed').textContent(),'Unknown');assert.equal(f.calls.length,1);
});

for(const field of ['nested artifact digest','installed target','readiness authority'])test(`exact runtime rejects crossed ${field}`,async t=>{
  const f=await fixture(t,{respond:(route,body,value)=>{
    if(field==='nested artifact digest')value.artifactRead.runtime.artifact.sha256='f'.repeat(64);
    if(field==='installed target')value.artifactRead.runtime.installed.serial='QUEST-B';
    if(field==='readiness authority')value.artifactRead.runtime.applicationReadinessAuthority=true;
    return route.fulfill({json:value});}});
  await f.read('apk-read-exact');assert.equal(await f.page.locator('#apk-read-installed').textContent(),'Unknown');assert.equal(f.calls.length,1);
});

test('lost reply clears old facts and reload never retries or stores a read',async t=>{
  let lose=false;const f=await fixture(t,{respond:(route,body,value)=>lose?route.abort('failed'):route.fulfill({json:value})});
  await f.read('apk-read-exact');lose=true;await f.read('apk-read-exact');assert.equal(await f.page.locator('#apk-read-installed').textContent(),'Unknown');
  assert.match(await f.page.locator('#apk-read-state').textContent(),/no automatic retry/i);
  const before=f.calls.length;await f.page.reload();await f.page.waitForFunction(()=>window.fixtureReady);assert.equal(f.calls.length,before);
  assert.equal(await f.page.evaluate(()=>sessionStorage.length),2);
});

for(const change of ['target','artifact','authentication'])test(`late read after ${change} round trip cannot restore source facts`,async t=>{
  let finish;const f=await fixture(t,{respond:async(route,body,value)=>{await new Promise(resolve=>{finish=resolve;});return route.fulfill({json:value});}});
  await f.page.locator('#apk-read-preflight').click();await f.page.waitForFunction(()=>window.ctx.busy);
  if(change==='target'){await f.update({selected:{serial:'QUEST-B',isReady:true}});await f.update({selected:{serial:'QUEST-A',isReady:true}});}
  if(change==='artifact'){await f.update({artifact:{...artifact,id:'6'.repeat(32)}});await f.update({artifact:{...artifact}});}
  if(change==='authentication'){await f.update({connected:false});await f.update({connected:true});}
  finish();await f.page.waitForFunction(()=>!window.ctx.busy);
  assert.equal(await f.page.locator('#apk-read-installed').textContent(),'Unknown');assert.match(await f.page.locator('#apk-read-state').textContent(),/changed/i);
  assert.equal(f.calls.length,1);assert.notEqual(await f.page.evaluate(()=>window.latest?.value.state),'completed');
});

for(const field of ['requestId','action','serial','sha256','policy'])test(`crossed ${field} response is rejected without useful facts`,async t=>{
  const f=await fixture(t,{respond:(route,body,value)=>{
    if(field==='requestId')value.requestId='crossed';if(field==='action')value.action='apk.preflight';if(field==='serial')value.artifactRead.serial='QUEST-B';
    if(field==='sha256')value.artifactRead.artifact.sha256='f'.repeat(64);if(field==='policy')value.artifactRead.verification='development-metadata';
    return route.fulfill({json:value});}});
  await f.read('apk-read-exact');assert.equal(await f.page.locator('#apk-read-installed').textContent(),'Unknown');assert.match(await f.page.locator('#apk-read-state').textContent(),/unavailable/i);assert.equal(f.calls.length,1);
});

test('changing current scope clears completed facts without dispatch or storage changes',async t=>{
  const f=await fixture(t);await f.read('apk-read-exact');await f.update({artifact:null});
  assert.equal(await f.page.locator('#apk-read-installed').textContent(),'Unknown');assert.equal(await f.page.locator('#apk-read-exact').isDisabled(),true);assert.equal(f.calls.length,1);
  assert.deepEqual(await f.page.evaluate(()=>Object.fromEntries(Object.entries(sessionStorage))),{'original-native':'original-unknown','original-transfer':'transfer-unknown'});
});

test('missing capability/actions block dispatch while retained configuration and unknown operations are independent',async t=>{
  const f=await fixture(t,{state:{artifactStaging:{supported:false},capability:{supported:false},unresolvedTransient:true}});
  for(const id of ['preflight','exact','development'])assert.equal(await f.page.locator('#apk-read-'+id).isDisabled(),true);
  await f.update({artifactStaging:{supported:true,maximumFileBytes:1073741824},actions:[]});assert.equal(await f.page.locator('#apk-read-preflight').isDisabled(),true);
  await f.update({actions:['apk.preflight','runtime.observe']});await f.read('apk-read-preflight');assert.equal(f.calls.length,1);
  assert.equal(await f.page.evaluate(()=>sessionStorage.getItem('original-native')),'original-unknown');
});

test('network offline hint does not override an explicitly authenticated local connector',async t=>{
  const f=await fixture(t);
  await f.page.evaluate(()=>{Object.defineProperty(navigator,'onLine',{get:()=>false,configurable:true});window.readView.update();});
  assert.equal(await f.page.locator('#apk-read-preflight').isDisabled(),false);
  await f.read('apk-read-preflight');assert.equal(f.calls.length,1);
  await f.update({connected:false});assert.equal(await f.page.locator('#apk-read-preflight').isDisabled(),true);
});

test('all actions serialize through busy and values remain literal with a narrow mobile layout',async t=>{
  const malicious='<img src=x onerror="window.injected=true">';
  const f=await fixture(t,{width:390,respond:(route,body,value)=>{value.artifactRead.preflight.artifact.identity.packageName=malicious;value.artifactRead.preflight.checks[0].detail=malicious;return route.fulfill({json:value});}});
  await f.update({busy:true});for(const id of ['preflight','exact','development'])assert.equal(await f.page.locator('#apk-read-'+id).isDisabled(),true);
  await f.update({busy:false});await f.read('apk-read-preflight');assert.equal(await f.page.locator('#apk-read-package').textContent(),malicious);
  assert.equal(await f.page.locator('#apk-read-view img').count(),0);assert.equal(await f.page.evaluate(()=>Boolean(window.injected)),false);
  assert.equal(await f.page.evaluate(()=>document.documentElement.scrollWidth<=window.innerWidth),true);
  await f.page.locator('#apk-read-preflight').focus();assert.equal(await f.page.evaluate(()=>document.activeElement.id),'apk-read-preflight');
  if(process.env.QFM_BROWSER_UI_EVIDENCE){await fs.mkdir(process.env.QFM_BROWSER_UI_EVIDENCE,{recursive:true});await f.page.locator('#apk-read-view').screenshot({path:path.join(process.env.QFM_BROWSER_UI_EVIDENCE,'apk-read-mobile.png')});}
});
