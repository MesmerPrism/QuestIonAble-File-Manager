// Real DOM fixtures only: every request is fulfilled or blocked; no host or device.
const test=require('node:test');
const assert=require('node:assert/strict');
const fs=require('node:fs/promises');
const path=require('node:path');
const {chromium}=require('playwright');
const root=path.resolve(__dirname,'../../src/QuestIonAbleFileManager.BrowserHost/wwwroot');

test('read capture affordances exist disabled before application code loads',async()=>{
  const html=await fs.readFile(path.join(root,'index.html'),'utf8');
  assert.ok(html.includes('id="transfer-view"'));
  for(const id of ['transfer-pull','transfer-export','transfer-save','transfer-remove'])assert.ok(new RegExp(`id="${id}"[^>]*disabled`).test(html),`${id} is disabled without JavaScript`);
});

const sha='a'.repeat(64), artifactId='b'.repeat(32);
const caps={supported:true,maximumFileBytes:1073741824,maximumTotalBytes:2147483648,maximumFiles:8,transferTimeoutSeconds:300};
let browser;
test.before(async()=>{browser=await chromium.launch({headless:true});});
test.after(async()=>{await browser?.close();});
async function fixture(t,{capture,remove,saveReject=false,width=1280}={}){
  const context=await browser.newContext({serviceWorkers:'block',viewport:{width,height:950}});t.after(()=>context.close());
  const page=await context.newPage(),calls=[],errors=[];page.on('pageerror',error=>errors.push(error.message));
  const state={sessionId:'session-a',connected:true,busy:false,selected:{serial:'QUEST-A',isReady:true},selectedPackage:'com.example.fixture',
    actions:['files.pull','apk.export'],artifactStaging:caps,artifact:{id:'local-apk-unchanged'},original:{operationId:'original-unknown',stage:'outcomeUnknownRecoveryRequired'}};
  await context.route('**/*',async route=>{
    const url=new URL(route.request().url());if(url.origin!=='http://127.0.0.1:18127')return route.abort('blockedbyclient');
    if(url.pathname.startsWith('/browser/')){
      const method=route.request().method(),body=method==='POST'?route.request().postDataJSON():undefined;calls.push({path:url.pathname,method,body});
      if(url.pathname==='/browser/v1/execute'){
        const value={requestId:body.requestId,action:body.action,state:'completed',artifactTransfer:{artifact:{id:artifactId,fileName:body.fileName,sizeBytes:3,sha256:sha},
          serial:body.serial,sourcePath:body.remotePath||'/data/app/fixture/base.apk',packageName:body.package||null,checksumText:body.package?`${sha}  ${body.fileName}\n`:null}};
        return capture?capture(route,body,value):route.fulfill({json:value});
      }
      if(method==='DELETE')return remove?remove(route):route.fulfill({json:{state:'removed',scope:'localOnly',artifactId:url.pathname.split('/').at(-1),deviceDispatched:false}});
      return route.abort('blockedbyclient');
    }
    if(url.pathname==='/transfer-fixture.js')return route.fulfill({contentType:'text/javascript',body:`import {createTransferView} from './transfer-view.js';
      window.ctx=${JSON.stringify(state)};window.ctx.actions=new Set(window.ctx.actions);window.saved=[];
      if(!sessionStorage.getItem('qfm-browser-retained-v1'))sessionStorage.setItem('qfm-browser-retained-v1','original-unknown-sentinel');
      window.transfers=createTransferView({getContext:()=>window.ctx,setBusy:value=>{window.ctx.busy=value;window.transfers?.update();},
        showResult:(value,summary)=>window.latest={value,summary},saveArtifact:receipt=>{window.saveBeforeOtherListener=!window.otherSaveListenerRan;window.saved.push(receipt);
          if(window.saveErrorOverride)return Promise.reject(Object.assign(new Error(window.saveErrorOverride.message),window.saveErrorOverride));
          return ${saveReject?'Promise.reject(new Error("fixture save cancelled"))':"Promise.resolve(Object.hasOwn(window,'saveResultOverride')?window.saveResultOverride:{state:'saved',mode:'streaming',artifact:receipt})"};},
        api:async(path,options)=>{const response=await fetch(path,{...options,cache:'no-store'});if(!response.ok)throw new Error((await response.json()).error);return response.json();}});
      window.fixtureReady=true;`});
    const name=url.pathname==='/'?'index.html':url.pathname.slice(1);
    if(!new Set(['index.html','styles.css','transfer-view.js']).has(name))return route.fulfill({status:404,body:''});
    let bytes=await fs.readFile(path.join(root,name));
    if(name==='index.html')bytes=Buffer.from(bytes.toString().replace(/<script[^>]*src="\.\/pwa.js"[^>]*><\/script>/,'').replace(/<script[^>]*src="\.\/app.js"[^>]*><\/script>/,'<script type="module" src="./transfer-fixture.js"></script>'));
    return route.fulfill({contentType:name.endsWith('.js')?'text/javascript':name.endsWith('.css')?'text/css':'text/html',body:bytes});
  });
  await page.goto('http://127.0.0.1:18127/');await page.waitForFunction(()=>window.fixtureReady);
  const update=patch=>page.evaluate(patch=>{Object.assign(window.ctx,patch);if(Array.isArray(patch.actions))window.ctx.actions=new Set(patch.actions);window.transfers.update();},patch);
  const fillPull=async(remote='/sdcard/Download/report.txt',name='report.txt')=>{await page.locator('#transfer-file-path').fill(remote);await page.locator('#transfer-file-name').fill(name);};
  const settled=()=>page.waitForFunction(()=>!window.ctx.busy&&Boolean(window.latest));
  const pull=async()=>{await fillPull();await page.locator('#transfer-pull').click();await settled();};
  t.after(()=>assert.deepEqual(errors,[],'No uncaught transfer UI errors'));
  return {page,calls,update,fillPull,pull,settled};
}

test('pull has one strict read request and a separate verified output before an explicit save',async t=>{
  const f=await fixture(t);await f.pull();
  assert.equal(f.calls.length,1);const body=f.calls[0].body;
  assert.deepEqual(Object.keys(body).sort(),['action','fileName','remotePath','requestId','serial']);
  assert.equal(body.action,'files.pull');assert.equal(body.serial,'QUEST-A');assert.equal(body.remotePath,'/sdcard/Download/report.txt');
  assert.match(body.requestId,/^[a-f0-9-]{36}$/);assert.equal(await f.page.locator('#transfer-output-target').textContent(),'QUEST-A');
  assert.equal(await f.page.locator('#transfer-output-source').textContent(),body.remotePath);assert.equal(await f.page.locator('#transfer-output-sha').textContent(),sha);
  assert.equal(await f.page.locator('#transfer-save').isDisabled(),false);assert.equal(await f.page.evaluate(()=>window.saved.length),0);
  await f.page.evaluate(()=>document.getElementById('transfer-save').addEventListener('click',()=>window.otherSaveListenerRan=true));
  await f.page.locator('#transfer-save').click();await f.page.waitForFunction(()=>window.saved.length===1&&!window.ctx.busy);
  assert.equal(await f.page.evaluate(()=>window.saveBeforeOtherListener),true,'Save helper is invoked in the original click stack');
  assert.deepEqual(await f.page.evaluate(()=>window.saved[0]),{id:artifactId,fileName:'report.txt',sizeBytes:3,sha256:sha});
  assert.equal(f.calls.length,1,'Save cannot issue another capture');
  assert.deepEqual(await f.page.evaluate(()=>window.ctx.artifact),{id:'local-apk-unchanged'});
  assert.equal(await f.page.evaluate(()=>sessionStorage.getItem('qfm-browser-retained-v1')),'original-unknown-sentinel');
});

test('export binds package,APK filename,remote source and exact checksum sidecar',async t=>{
  const f=await fixture(t);await f.page.locator('#transfer-export-name').fill('fixture.apk');await f.page.locator('#transfer-export').click();await f.settled();
  assert.deepEqual(Object.keys(f.calls[0].body).sort(),['action','fileName','package','requestId','serial']);
  assert.equal(f.calls[0].body.package,'com.example.fixture');assert.equal(f.calls[0].body.action,'apk.export');
  assert.equal(await f.page.locator('#transfer-checksum').textContent(),`${sha}  fixture.apk\n`);
  assert.match(await f.page.locator('#transfer-output-source').textContent(),/com.example.fixture.*\/data\/app\/fixture\/base.apk/);
  assert.match(await f.page.locator('#transfer-view').textContent(),/App data, split APK sets, OBBs, assets and Store entitlements are not included/);
});

test('remove deletes only the exact captured artifact and leaves other staging and originals alone',async t=>{
  const f=await fixture(t);await f.pull();await f.page.locator('#transfer-remove').click();await f.page.waitForFunction(()=>!window.ctx.busy&&document.getElementById('transfer-output').hidden);
  assert.deepEqual(f.calls.map(call=>[call.method,call.path]),[['POST','/browser/v1/execute'],['DELETE',`/browser/v1/artifacts/${artifactId}`]]);
  assert.deepEqual(await f.page.evaluate(()=>window.ctx.artifact),{id:'local-apk-unchanged'});
  assert.equal(await f.page.evaluate(()=>sessionStorage.length),1);assert.match(await f.page.locator('#transfer-state').textContent(),/Only this captured staging artifact/);
});

for(const [name,change] of [
  ['request ID',value=>{value.requestId='wrong';}],['action',value=>{value.action='apk.export';}],['target',value=>{value.artifactTransfer.serial='QUEST-B';}],
  ['filename',value=>{value.artifactTransfer.artifact.fileName='other.txt';}],['source',value=>{value.artifactTransfer.sourcePath='/sdcard/other.txt';}],
  ['opaque ID',value=>{value.artifactTransfer.artifact.id='../private';}],['SHA',value=>{value.artifactTransfer.artifact.sha256='A'.repeat(64);}],
  ['size',value=>{value.artifactTransfer.artifact.sizeBytes=1073741825;}],['unexpected package',value=>{value.artifactTransfer.packageName='com.other.app';}],
  ['unexpected checksum',value=>{value.artifactTransfer.checksumText='wrong';}]
])test(`mismatched ${name} cannot become a saveable capture`,async t=>{
  const f=await fixture(t,{capture:(route,body,value)=>{change(value);return route.fulfill({json:value});}});await f.pull();
  assert.equal(await f.page.locator('#transfer-save').isDisabled(),true);assert.equal(await f.page.locator('#transfer-output').isHidden(),true);
  assert.equal(f.calls.length,1);assert.match(await f.page.locator('#transfer-state').textContent(),/unavailable/);
});

for(const part of ['checksum','package'])test(`export ${part} mismatch cannot enable Save`,async t=>{
  const f=await fixture(t,{capture:(route,body,value)=>route.fulfill({json:{...value,artifactTransfer:{...value.artifactTransfer,...(part==='checksum'?{checksumText:`${sha} fixture.apk\n`}:{packageName:'com.other.app'})}}})});
  await f.page.locator('#transfer-export').click();await f.settled();assert.equal(await f.page.locator('#transfer-save').isDisabled(),true);
});

test('split export rejection is explicit and cannot publish a partial saveable APK',async t=>{
  const f=await fixture(t,{capture:(route,body,value)=>route.fulfill({json:{requestId:body.requestId,action:body.action,state:'failed',error:'split_export_unsupported'}})});
  await f.page.locator('#transfer-export').click();await f.settled();
  assert.equal(await f.page.locator('#transfer-save').isDisabled(),true);assert.match(await f.page.locator('#transfer-state').textContent(),/complete-set workflow/);
  assert.equal(f.calls.length,1);
});

test('mismatched removal acknowledgement cannot clear a known captured output',async t=>{
  const f=await fixture(t,{remove:route=>route.fulfill({json:{state:'removed',scope:'localOnly',artifactId:'c'.repeat(32),deviceDispatched:false}})});
  await f.pull();await f.page.locator('#transfer-remove').click();await f.page.waitForFunction(()=>!window.ctx.busy);
  assert.equal(await f.page.locator('#transfer-output').isVisible(),true);assert.match(await f.page.locator('#transfer-state').textContent(),/removal_receipt_invalid/);
  assert.equal(f.calls.filter(call=>call.method==='DELETE').length,1);
});

test('lost read reply,reload and connectivity events never replay or persist capture data',async t=>{
  const f=await fixture(t,{capture:route=>route.abort('failed')});await f.pull();const count=f.calls.length;
  await f.page.evaluate(()=>{dispatchEvent(new Event('offline'));dispatchEvent(new Event('online'));});
  await f.page.reload();await f.page.waitForFunction(()=>window.fixtureReady);
  assert.equal(f.calls.length,count);assert.equal(await f.page.locator('#transfer-output').isHidden(),true);
  assert.equal(await f.page.locator('#transfer-save').isDisabled(),true);assert.equal(await f.page.evaluate(()=>sessionStorage.length),1);
});

for(const kind of ['target','authentication'])test(`late capture after ${kind} change cannot select old bytes for current scope`,async t=>{
  let finish,entered;const started=new Promise(resolve=>{entered=resolve;});
  const f=await fixture(t,{capture:async(route,body,value)=>{entered();await new Promise(resolve=>{finish=resolve;});await route.fulfill({json:value});}});
  await f.fillPull();await f.page.locator('#transfer-pull').click();await started;
  if(kind==='target')await f.update({selected:{serial:'QUEST-B',isReady:true}});
  else{await f.update({connected:false});await f.update({connected:true});}
  finish();await f.settled();assert.equal(await f.page.locator('#transfer-output').isHidden(),true);
  assert.equal(await f.page.locator('#transfer-save').isDisabled(),true);assert.match(await f.page.locator('#transfer-state').textContent(),/authentication or target changed/);
  assert.equal(f.calls.length,1);
});

test('capabilities and exact file/package inputs fail closed without requests',async t=>{
  const f=await fixture(t);await f.fillPull('/', '../bad.txt');
  assert.equal(await f.page.locator('#transfer-pull').isDisabled(),true);
  await f.fillPull();await f.update({actions:['apk.export']});assert.equal(await f.page.locator('#transfer-pull').isDisabled(),true);
  await f.update({artifactStaging:{supported:true}});assert.equal(await f.page.locator('#transfer-export').isDisabled(),true);
  await f.update({artifactStaging:caps,connected:false});assert.equal(await f.page.locator('#transfer-export').isDisabled(),true);
  await f.update({connected:true,selected:{serial:'QUEST-A',isReady:false}});assert.equal(await f.page.locator('#transfer-export').isDisabled(),true);
  assert.equal(f.calls.length,0);
});

test('capture size uses supplied numerical host limit without hardcoded local fallback',async t=>{
  const f=await fixture(t);await f.update({artifactStaging:{...caps,maximumFileBytes:2,maximumTotalBytes:4,maximumFiles:1,transferTimeoutSeconds:7}});
  await f.pull();assert.equal(await f.page.locator('#transfer-save').isDisabled(),true);
  assert.match(await f.page.locator('#transfer-capability').textContent(),/2 bytes per file.*4 bytes across 1 files; 7 seconds/);
});

test('exact1GiB captured metadata remains reviewable without buffering any bytes',async t=>{
  const f=await fixture(t,{capture:(route,body,value)=>{value.artifactTransfer.artifact.sizeBytes=caps.maximumFileBytes;return route.fulfill({json:value});}});
  await f.pull();assert.equal(await f.page.locator('#transfer-save').isDisabled(),false);
  assert.match(await f.page.locator('#transfer-output-size').textContent(),/1,073,741,824/);
  assert.equal(await f.page.evaluate(()=>window.saved.length),0);
});

test('selectRemoteFile only fills literal source and Save failure never recaptures or removes it',async t=>{
  const f=await fixture(t,{saveReject:true});
  assert.equal(await f.page.evaluate(()=>window.transfers.selectRemoteFile('/sdcard/Download/report.txt')),true);
  assert.equal(await f.page.locator('#transfer-file-path').inputValue(),'/sdcard/Download/report.txt');assert.equal(f.calls.length,0);
  await f.page.locator('#transfer-pull').click();await f.settled();
  await f.page.locator('#transfer-save').click();await f.page.waitForFunction(()=>!window.ctx.busy&&document.getElementById('transfer-state').textContent.includes('Save not completed'));
  assert.equal(f.calls.length,1);assert.equal(await f.page.locator('#transfer-output').isVisible(),true);
  await f.update({sessionId:'other-session'});assert.equal(await f.page.locator('#transfer-save').isDisabled(),true);assert.equal(await f.page.locator('#transfer-remove').isDisabled(),true);
});

test('mobile output and source labels stay literal without horizontal overflow',async t=>{
  const f=await fixture(t,{width:375});const remote='/sdcard/Download/<img src=x onerror=alert(1)>.txt';
  await f.fillPull(remote,'literal.txt');await f.page.locator('#transfer-pull').click();await f.settled();
  assert.equal(await f.page.locator('#transfer-output-source').textContent(),remote);assert.equal(await f.page.locator('#transfer-output-source img').count(),0);
  assert.equal(await f.page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth),true);
  if(process.env.QFM_BROWSER_UI_EVIDENCE){await fs.mkdir(process.env.QFM_BROWSER_UI_EVIDENCE,{recursive:true});await f.page.locator('#transfer-view').screenshot({path:path.join(process.env.QFM_BROWSER_UI_EVIDENCE,'transfer-mobile.png')});}
});

test('Save result distinguishes verified streaming close from buffered browser handoff',async t=>{
  const f=await fixture(t);await f.pull();await f.page.locator('#transfer-save').click();await f.page.waitForFunction(()=>!window.ctx.busy);
  assert.match(await f.page.locator('#transfer-state').textContent(),/Streaming save verified.*close acknowledged/);
  await f.page.evaluate(({artifactId,sha})=>{window.saveResultOverride={state:'downloadStarted',mode:'buffered',artifact:{id:artifactId,fileName:'report.txt',sizeBytes:3,sha256:sha}};},{artifactId,sha});
  await f.page.locator('#transfer-save').click();await f.page.waitForFunction(()=>!window.ctx.busy);
  const message=await f.page.locator('#transfer-state').textContent();assert.match(message,/Browser download started/);assert.match(message,/Final disk save is not confirmed/);
  assert.doesNotMatch(message,/close acknowledged/);assert.equal(f.calls.length,1);
});

for(const kind of ['undefined','mismatched receipt','wrong state/mode'])test(`Save result ${kind} cannot claim a successful save`,async t=>{
  const f=await fixture(t);await f.pull();
  await f.page.evaluate(({kind,artifactId,sha})=>{window.saveResultOverride=kind==='undefined'?undefined:{state:'saved',mode:kind==='wrong state/mode'?'buffered':'streaming',
    artifact:{id:kind==='mismatched receipt'?'c'.repeat(32):artifactId,fileName:'report.txt',sizeBytes:3,sha256:sha}};},{kind,artifactId,sha});
  await f.page.locator('#transfer-save').click();await f.page.waitForFunction(()=>!window.ctx.busy);
  const message=await f.page.locator('#transfer-state').textContent();assert.match(message,/outcome unknown/i);assert.match(message,/Check the selected file.*before another Save/);
  assert.doesNotMatch(message,/Save finished|close acknowledged|Browser download started/);assert.equal(f.calls.length,1);
});

test('Save failure surfaces the supported alternative without automatic recapture',async t=>{
  const f=await fixture(t);await f.pull();await f.page.evaluate(()=>{window.saveErrorOverride={message:'Streaming Save is required.',alternative:'Use a supporting browser or the qualified CLI.'};});
  await f.page.locator('#transfer-save').click();await f.page.waitForFunction(()=>!window.ctx.busy);
  assert.match(await f.page.locator('#transfer-state').textContent(),/Use a supporting browser or the qualified CLI/);assert.equal(f.calls.length,1);
});

for(const unknown of [false,true])test(`Save cleanup pending${unknown?' with uncertain close':''} requires inspection before another save`,async t=>{
  const f=await fixture(t);await f.pull();await f.page.evaluate(unknown=>{window.saveErrorOverride={message:'The native save did not settle.',saveOutcomeUnknown:unknown,cleanupPending:true};},unknown);
  await f.page.locator('#transfer-save').click();await f.page.waitForFunction(()=>!window.ctx.busy);
  const message=await f.page.locator('#transfer-state').textContent();assert.match(message,/cleanup is still pending/);assert.match(message,/selected file before another Save/);
  if(unknown)assert.match(message,/Save outcome unknown/);
  assert.doesNotMatch(message,/retry/i);assert.equal(f.calls.length,1);assert.equal(await f.page.evaluate(()=>window.saved.length),1);
});
