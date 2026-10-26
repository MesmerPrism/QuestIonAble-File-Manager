// Shipped DOM/module, synthetic intercepted API only. No host, listener or device.
const test=require('node:test'),assert=require('node:assert/strict'),fs=require('node:fs/promises'),path=require('node:path');
const {chromium}=require('playwright');
const root=path.resolve(__dirname,'../../src/QuestIonAbleFileManager.BrowserHost/wwwroot');
const key='qfm-browser-file-push-v1',sha='a'.repeat(64),id='b'.repeat(32);
const caps={supported:true,maximumFileBytes:1073741824,maximumTotalBytes:2147483648,maximumFiles:8,transferTimeoutSeconds:300};
const artifact={id,fileName:'notes with spaces.bin',sizeBytes:3,sha256:sha};
let browser;test.before(async()=>{browser=await chromium.launch({headless:true});});test.after(async()=>browser?.close());
const complete=body=>({requestId:body.requestId,action:'files.push',state:'completed',result:null,
  artifactPush:{artifact:{...artifact},serial:body.serial,remotePath:body.remotePath,verification:'remote-size',remoteSizeVerified:true},
  mutation:{operationId:'pc-'+'c'.repeat(32),commandKind:'pushFile',target:body.serial,desiredState:'file present at '+body.remotePath,stage:'confirmed',headsetReadback:true,observedState:'Remote size matched',transitions:[]}});
async function fixture(t,{upload,execute,status,remove,width=1280,state={}}={}){
  const context=await browser.newContext({serviceWorkers:'block',viewport:{width,height:1000}});t.after(()=>context.close());
  const page=await context.newPage(),calls=[],errors=[];page.on('pageerror',error=>errors.push(error.message));let ownerResult=null;
  await context.route('**/*',async route=>{
    const url=new URL(route.request().url());if(url.origin!=='http://127.0.0.1:18127')return route.abort('blockedbyclient');
    if(url.pathname.startsWith('/browser/')){
      const method=route.request().method(),body=url.pathname==='/browser/v1/execute'?route.request().postDataJSON():undefined;
      const call={path:url.pathname,method,body};calls.push(call);
      if(url.pathname==='/browser/v1/artifacts'){
        call.fileName=route.request().headers()['x-qfm-file-name'];call.byteLength=route.request().postDataBuffer().length;
        const value={state:'staged',scope:'localOnly',deviceDispatched:false,artifact:{...artifact,fileName:call.fileName}};
        return upload?upload(route,value):route.fulfill({status:201,json:value});
      }
      if(url.pathname==='/browser/v1/execute'){
        call.storedAtExecute=await page.evaluate(key=>JSON.parse(sessionStorage.getItem(key)||'null'),key);
        ownerResult=complete(body);return execute?execute(route,body,ownerResult):route.fulfill({json:ownerResult});
      }
      if(url.pathname.startsWith('/browser/v1/requests/'))return status?status(route,ownerResult):route.fulfill({json:ownerResult});
      if(method==='DELETE')return remove?remove(route):route.fulfill({json:{state:'removed',scope:'localOnly',artifactId:url.pathname.split('/').at(-1),deviceDispatched:false}});
      return route.abort('blockedbyclient');
    }
    if(url.pathname==='/push-fixture.js')return route.fulfill({contentType:'text/javascript',body:`import {createFilePushView} from './file-push-view.js';
      window.ctx=${JSON.stringify({sessionId:'session-a',connected:true,busy:false,selected:{serial:'QUEST-A',isReady:true},actions:['files.push'],artifactStaging:caps,externalUnresolved:false,stagingGeneration:0,artifact:{id:'apk-unchanged'},...state})};window.ctx.actions=new Set(window.ctx.actions);
      sessionStorage.setItem('native-sentinel','native-original-unknown');sessionStorage.setItem('transfer-sentinel','transfer-output-unchanged');
      window.pushView=createFilePushView({getContext:()=>window.ctx,setBusy:value=>{window.ctx.busy=value;window.siblingBlocked=Boolean(value||window.pushView?.hasUnresolved());window.pushView?.update();},showResult:(value,summary)=>window.latest={value,summary},
        api:async(path,options)=>{const response=await fetch(path,{...options,cache:'no-store'});if(!response.ok){const value=await response.json();throw Object.assign(new Error(value.error),value,{status:response.status});}return response.json();}});window.pushView.update();window.fixtureReady=true;`});
    const name=url.pathname==='/'?'index.html':url.pathname.slice(1);if(!['index.html','styles.css','file-push-view.js'].includes(name))return route.fulfill({status:404,body:''});
    let bytes=await fs.readFile(path.join(root,name));if(name==='index.html')bytes=Buffer.from(bytes.toString().replace(/<script[^>]*src="\.\/pwa.js"[^>]*><\/script>/,'').replace(/<script[^>]*src="\.\/app.js"[^>]*><\/script>/,'<script type="module" src="./push-fixture.js"></script>'));
    return route.fulfill({contentType:name.endsWith('.js')?'text/javascript':name.endsWith('.css')?'text/css':'text/html',body:bytes});
  });
  await page.goto('http://127.0.0.1:18127/');await page.waitForFunction(()=>window.fixtureReady);
  const update=patch=>page.evaluate(patch=>{Object.assign(window.ctx,patch);if(Array.isArray(window.ctx.actions))window.ctx.actions=new Set(window.ctx.actions);window.pushView.update();},patch);
  const stage=async()=>{await page.locator('#file-push-upload').setInputFiles({name:artifact.fileName,mimeType:'application/octet-stream',buffer:Buffer.from([1,2,3])});await page.locator('#file-push-stage').click();await page.waitForFunction(()=>!window.ctx.busy);};
  const approve=async(remote='/sdcard/Download/report.bin')=>{await page.locator('#file-push-remote').fill(remote);await page.locator('#file-push-confirm').check();};
  const push=async()=>{await approve();await page.locator('#file-push-send').click();await page.waitForFunction(()=>!window.ctx.busy);};
  const stored=()=>page.evaluate(key=>JSON.parse(sessionStorage.getItem(key)||'null'),key);
  t.after(()=>assert.deepEqual(errors,[],'No uncaught push UI errors'));
  return {page,calls,update,stage,approve,push,stored,setOwner:value=>{ownerResult=value;}};
}

test('file push is inert without JavaScript',async t=>{
  const context=await browser.newContext({javaScriptEnabled:false,serviceWorkers:'block'});t.after(()=>context.close());
  await context.route('**/*',async route=>new URL(route.request().url()).pathname==='/'?route.fulfill({contentType:'text/html',body:await fs.readFile(path.join(root,'index.html'))}):route.abort('blockedbyclient'));
  const page=await context.newPage();await page.goto('http://127.0.0.1:18127/');
  for(const id of ['upload','name','stage','remote','confirm','send','status','remove'])assert.equal(await page.locator('#file-push-'+id).isDisabled(),true);
});

test('generic binary upload remains separate and approved push persists exact reference before dispatch',async t=>{
  const f=await fixture(t);assert.equal(f.calls.length,0);await f.stage();assert.equal(await f.page.locator('#file-push-name-value').textContent(),artifact.fileName);
  assert.equal(await f.stored(),null);assert.deepEqual(await f.page.evaluate(()=>window.ctx.artifact),{id:'apk-unchanged'});
  await f.page.locator('#file-push-remote').fill('/sdcard/Download/report.bin');assert.equal(await f.page.locator('#file-push-send').isDisabled(),true);
  await f.page.locator('#file-push-send').dispatchEvent('click');assert.equal(f.calls.length,1);
  await f.push();const call=f.calls.find(call=>call.path==='/browser/v1/execute'),{requestId,...body}=call.body;
  assert.deepEqual(body,{action:'files.push',artifactId:id,sha256:sha,sizeBytes:3,serial:'QUEST-A',remotePath:'/sdcard/Download/report.bin',confirmOverwrite:true});
  assert.deepEqual(call.storedAtExecute,{requestId,sessionId:'session-a',action:'files.push',serial:'QUEST-A',remotePath:body.remotePath,artifactId:id,sha256:sha,sizeBytes:3});
  assert.equal(await f.stored(),null);assert.equal(await f.page.evaluate(()=>window.pushView.hasUnresolved()),false);
  assert.match(await f.page.locator('#file-push-state').textContent(),/remote size/i);assert.match(await f.page.locator('#file-push-boundary').textContent(),/SHA-256.*atomic.*previous/i);
  assert.equal(f.calls[0].fileName,artifact.fileName);assert.equal(f.calls[0].byteLength,3);
  assert.equal(await f.page.evaluate(()=>sessionStorage.getItem('native-sentinel')),'native-original-unknown');
});

for(const remote of ['/','/sdcard/Download/','/sdcard/./report.bin','/sdcard/../report.bin','/sdcard//report.bin','relative.bin','/sdcard/\tbad.bin','/sdcard/\u0085bad.bin','/sdcard/\u009fbad.bin'])test(`invalid destination ${JSON.stringify(remote)} never dispatches`,async t=>{
  const f=await fixture(t);await f.stage();await f.page.locator('#file-push-remote').fill(remote);assert.equal(await f.page.locator('#file-push-send').isDisabled(),true);
  assert.equal(await f.page.locator('#file-push-confirm').isDisabled(),true);
  await f.page.locator('#file-push-send').dispatchEvent('click');assert.equal(f.calls.length,1);
});

test('path target file-selection and staging changes reset overwrite approval',async t=>{
  const f=await fixture(t);await f.stage();await f.approve();await f.page.locator('#file-push-remote').fill('/sdcard/other.bin');assert.equal(await f.page.locator('#file-push-confirm').isChecked(),false);
  await f.page.locator('#file-push-confirm').check();await f.update({selected:{serial:'QUEST-B',isReady:true}});assert.equal(await f.page.locator('#file-push-confirm').isChecked(),false);
  await f.page.locator('#file-push-confirm').check();await f.page.locator('#file-push-upload').setInputFiles({name:'another.bin',buffer:Buffer.from([4])});assert.equal(await f.page.locator('#file-push-confirm').isChecked(),false);
  await f.update({stagingGeneration:1});assert.equal(await f.page.locator('#file-push-source').isHidden(),true);assert.equal(await f.page.locator('#file-push-send').isDisabled(),true);assert.equal(f.calls.length,1);
});

for(const field of ['request','action','source ID','source hash','source size','target','remote','verification','size proof','marker target','marker kind','marker readback','raw result'])test(`crossed ${field} completion retains unknown instead of allowing another push`,async t=>{
  const f=await fixture(t,{execute:(route,body,value)=>{
    if(field==='request')value.requestId=crypto.randomUUID();if(field==='action')value.action='files.pull';if(field==='source ID')value.artifactPush.artifact.id='d'.repeat(32);
    if(field==='source hash')value.artifactPush.artifact.sha256='d'.repeat(64);if(field==='source size')value.artifactPush.artifact.sizeBytes=4;
    if(field==='target')value.artifactPush.serial='QUEST-B';if(field==='remote')value.artifactPush.remotePath='/sdcard/elsewhere.bin';
    if(field==='verification')value.artifactPush.verification='sha256';if(field==='size proof')value.artifactPush.remoteSizeVerified=false;
    if(field==='marker target')value.mutation.target='QUEST-B';if(field==='marker kind')value.mutation.commandKind='installApk';if(field==='marker readback')value.mutation.headsetReadback=false;
    if(field==='raw result')value.result={command:{localPath:'C:/private/source'}};
    return route.fulfill({json:value});}});
  await f.stage();await f.push();assert.equal(await f.page.evaluate(()=>window.pushView.hasUnresolved()),true);assert.ok(await f.stored());assert.equal(await f.page.locator('#file-push-send').isDisabled(),true);assert.equal(f.calls.length,2);
});

test('lost reply reload reconnect and changed session never replay or discard original',async t=>{
  const f=await fixture(t,{execute:route=>route.abort('failed')});await f.stage();await f.push();const original=await f.stored();assert.equal(await f.page.evaluate(()=>window.pushView.hasUnresolved()),true);
  const count=f.calls.length;await f.page.reload();await f.page.waitForFunction(()=>window.fixtureReady);assert.equal(f.calls.length,count);assert.deepEqual(await f.stored(),original);assert.equal(await f.page.locator('#file-push-source').isHidden(),true);
  await f.update({connected:false});await f.update({connected:true,sessionId:'session-b'});assert.equal(f.calls.length,count);assert.deepEqual(await f.stored(),original);
  assert.equal(await f.page.locator('#file-push-status').isDisabled(),true);assert.match(await f.page.locator('#file-push-state').textContent(),/typed.*owner|typed.*CLI/i);
});

test('same-session explicit original GET can confirm after target change and exact source removal',async t=>{
  const f=await fixture(t,{execute:route=>route.abort('failed')});await f.stage();await f.push();const original=await f.stored();
  await f.page.locator('#file-push-remove').click();await f.page.waitForFunction(()=>!window.ctx.busy);assert.deepEqual(await f.stored(),original);
  await f.update({selected:{serial:'QUEST-B',isReady:true}});await f.page.locator('#file-push-status').click();await f.page.waitForFunction(()=>!window.pushView.hasUnresolved());
  assert.deepEqual(f.calls.at(-1),{path:'/browser/v1/requests/'+original.requestId,method:'GET',body:undefined});
  assert.equal(await f.stored(),null);assert.match(await f.page.locator('#file-push-state').textContent(),/QUEST-A/);assert.equal(f.calls.filter(call=>call.path==='/browser/v1/execute').length,1);
});

test('completed original GET refreshes the parent mutation gate after a lost POST',async t=>{
  const f=await fixture(t,{execute:route=>route.abort('failed')});await f.stage();await f.push();assert.equal(await f.page.evaluate(()=>window.siblingBlocked),true);
  await f.page.locator('#file-push-status').click();await f.page.waitForFunction(()=>!window.pushView.hasUnresolved());assert.equal(await f.page.evaluate(()=>window.siblingBlocked),false);
});

for(const state of ['pending','outcomeUnknown','failed'])test(`${state} result and missing status preserve original`,async t=>{
  const f=await fixture(t,{execute:(route,body,value)=>route.fulfill({json:{...value,state,artifactPush:{...value.artifactPush,remoteSizeVerified:false},mutation:null}}),status:route=>route.fulfill({status:404,json:{error:'request_not_found',dispatched:false}})});
  await f.stage();await f.push();const original=await f.stored();await f.page.locator('#file-push-status').click();await f.page.waitForFunction(()=>document.querySelector('#file-push-state').textContent.includes('unavailable'));
  assert.deepEqual(await f.stored(),original);assert.equal(await f.page.evaluate(()=>window.pushView.hasUnresolved()),true);assert.equal(f.calls.filter(call=>call.path==='/browser/v1/execute').length,1);
});

for(const [error,clears] of [['busy',true],['remote_file_path_invalid',true],['request_already_consumed',false],['duplicate_request_id',false]])test(`dispatched:false ${error} ${clears?'clears a genuine preadmission rejection':'cannot clear original'}`,async t=>{
  const f=await fixture(t,{execute:route=>route.fulfill({status:409,json:{error,dispatched:false}})});await f.stage();await f.push();assert.equal(await f.page.evaluate(()=>window.pushView.hasUnresolved()),!clears);
  assert.equal(Boolean(await f.stored()),!clears);assert.equal(await f.page.locator('#file-push-confirm').isChecked(),false);assert.equal(f.calls.length,2);
});

test('late dispatched:false reply from an earlier authentication cannot clear original',async t=>{
  let finish;const f=await fixture(t,{execute:async route=>{await new Promise(resolve=>{finish=resolve;});return route.fulfill({status:409,json:{error:'busy',dispatched:false}});}});
  await f.stage();await f.approve();await f.page.locator('#file-push-send').click();await f.page.waitForFunction(()=>window.ctx.busy);
  await f.update({connected:false});await f.update({connected:true});finish();await f.page.waitForFunction(()=>!window.ctx.busy);
  assert.equal(await f.page.evaluate(()=>window.pushView.hasUnresolved()),true);assert.ok(await f.stored());assert.equal(f.calls.length,2);
});

test('acknowledged original GET stays confirmed when the concurrent POST reply is subsequently lost',async t=>{
  let finish;const f=await fixture(t,{execute:async route=>{await new Promise(resolve=>{finish=resolve;});return route.abort('failed');}});
  await f.stage();await f.approve();await f.page.locator('#file-push-send').click();await f.page.waitForFunction(()=>window.ctx.busy);
  await f.page.locator('#file-push-status').click();await f.page.waitForFunction(()=>!window.pushView.hasUnresolved());assert.equal(await f.page.evaluate(()=>window.ctx.busy),true);assert.equal(await f.page.evaluate(()=>window.siblingBlocked),true);
  finish();await f.page.waitForFunction(()=>!window.ctx.busy);assert.equal(await f.stored(),null);assert.match(await f.page.locator('#file-push-state').textContent(),/Confirmed remote size/);
  assert.equal(f.calls.filter(call=>call.path==='/browser/v1/execute').length,1);
});

test('storage failure prevents dispatch instead of losing the original reference on reload',async t=>{
  const f=await fixture(t);await f.stage();await f.page.evaluate(()=>{Storage.prototype.setItem=()=>{throw new Error('storage blocked');};});
  await f.push();assert.equal(f.calls.length,1);assert.equal(await f.page.evaluate(()=>window.pushView.hasUnresolved()),false);assert.match(await f.page.locator('#file-push-state').textContent(),/not sent|storage.*unavailable/i);
});

test('restoring original strips unrelated private fields without discarding uncertainty',async t=>{
  const f=await fixture(t,{execute:route=>route.abort('failed')});await f.stage();await f.push();const original=await f.stored();
  await f.page.evaluate(key=>{const value=JSON.parse(sessionStorage.getItem(key));value.secret='never-persist-secret';value.result={path:'C:/private/local.bin'};sessionStorage.setItem(key,JSON.stringify(value));},key);
  const count=f.calls.length;await f.page.reload();await f.page.waitForFunction(()=>window.fixtureReady);assert.deepEqual(await f.stored(),original);assert.equal(f.calls.length,count);
});

for(const change of ['authentication','target','staging'])test(`late completed push after ${change} change requires explicit original read`,async t=>{
  let finish;const f=await fixture(t,{execute:async(route,body,value)=>{await new Promise(resolve=>{finish=resolve;});return route.fulfill({json:value});}});
  await f.stage();await f.approve();await f.page.locator('#file-push-send').click();await f.page.waitForFunction(()=>window.ctx.busy);
  if(change==='authentication'){await f.update({connected:false});await f.update({connected:true});}
  if(change==='target'){await f.update({selected:{serial:'QUEST-B',isReady:true}});await f.update({selected:{serial:'QUEST-A',isReady:true}});}
  if(change==='staging')await f.update({stagingGeneration:1});finish();await f.page.waitForFunction(()=>!window.ctx.busy);
  assert.equal(await f.page.evaluate(()=>window.pushView.hasUnresolved()),true);assert.ok(await f.stored());assert.equal(f.calls.length,2);
  await f.page.locator('#file-push-status').click();await f.page.waitForFunction(()=>!window.pushView.hasUnresolved());assert.equal(f.calls.at(-1).method,'GET');
});

test('late upload response cannot reattach staging after authentication changes',async t=>{
  let finish;const f=await fixture(t,{upload:async(route,value)=>{await new Promise(resolve=>{finish=resolve;});return route.fulfill({status:201,json:value});}});
  await f.page.locator('#file-push-upload').setInputFiles({name:artifact.fileName,buffer:Buffer.from([1,2,3])});await f.page.locator('#file-push-stage').click();await f.page.waitForFunction(()=>window.ctx.busy);
  await f.update({connected:false});await f.update({connected:true});finish();await f.page.waitForFunction(()=>!window.ctx.busy);assert.equal(await f.page.locator('#file-push-source').isHidden(),true);assert.equal(await f.stored(),null);assert.equal(f.calls.length,1);
});

test('capabilities quota and external uncertainty gate push independently of native configuration',async t=>{
  const f=await fixture(t,{state:{actions:[],artifactStaging:{supported:false},capability:{supported:false}}});assert.equal(await f.page.locator('#file-push-stage').isDisabled(),true);
  await f.update({actions:['files.push'],artifactStaging:{...caps,maximumFileBytes:2}});await f.page.locator('#file-push-upload').setInputFiles({name:artifact.fileName,buffer:Buffer.from([1,2,3])});assert.equal(await f.page.locator('#file-push-stage').isDisabled(),true);
  await f.update({artifactStaging:caps});await f.stage();await f.approve();await f.update({externalUnresolved:true});assert.equal(await f.page.locator('#file-push-send').isDisabled(),true);assert.equal(await f.page.locator('#file-push-confirm').isChecked(),false);
  await f.update({externalUnresolved:false});await f.push();assert.equal(f.calls.length,2);
});

test('saved original is allowlisted and literal destination fits mobile',async t=>{
  const remote='/sdcard/Download/<img src=x onerror=window.injected=true>.bin';const f=await fixture(t,{width:390,execute:route=>route.abort('failed')});await f.stage();await f.approve(remote);await f.page.locator('#file-push-send').click();await f.page.waitForFunction(()=>!window.ctx.busy);
  assert.equal(await f.page.locator('#file-push-original img').count(),0);assert.equal(await f.page.evaluate(()=>Boolean(window.injected)),false);assert.equal(await f.page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth),true);
  const original=await f.stored();assert.deepEqual(Object.keys(original).sort(),['action','artifactId','remotePath','requestId','serial','sessionId','sha256','sizeBytes']);
  await f.page.evaluate(()=>{const original=URL.createObjectURL;URL.createObjectURL=blob=>{blob.text().then(text=>window.savedReference=JSON.parse(text));return original(blob);};});await f.page.locator('#file-push-save').click();await f.page.waitForFunction(()=>window.savedReference);
  assert.deepEqual(await f.page.evaluate(()=>window.savedReference),original);assert.equal(await f.page.evaluate(()=>sessionStorage.getItem('transfer-sentinel')),'transfer-output-unchanged');
});
