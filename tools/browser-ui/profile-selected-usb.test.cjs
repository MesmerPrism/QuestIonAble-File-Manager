// Synthetic intercepted DOM fixtures only. Never opens a real host/store/device.
const test=require('node:test'),assert=require('node:assert/strict'),fs=require('node:fs/promises'),path=require('node:path');
const {chromium}=require('playwright');
const root=path.resolve(__dirname,'../../src/QuestIonAbleFileManager.BrowserHost/wwwroot'),origin='http://127.0.0.1:18143',storageKey='qfm-browser-profile-v1';
const prefix='questionable.file_manager.quest_connectivity_profile_';
const secrets={usb:'PRIVATE-USB-FIXTURE',endpoint:'http://192.168.7.8:39873/',code:'0123456789ABCDEFGHJK'};
function outcome(body){const operation=body.action.slice(8),profile={operation,deviceId:body.deviceId??null,replaceExisting:operation==='import'?body.replaceExisting:null,effect:operation==='list'||operation==='status'?'readOnly':'confirmed',list:null,status:null,receipt:null,failure:null,totalProfiles:null,truncated:false};
  if(operation==='list')Object.assign(profile,{list:{schema:prefix+'list.v1',status:'ok',profiles:[{deviceId:'alpha',state:'enrolled',reasonCode:'profileEnrolled'},{deviceId:'beta',state:'invalid',reasonCode:'profileInvalid'}]},totalProfiles:2});
  if(operation==='status')profile.status={schema:prefix+'status.v1',status:'ok',deviceId:body.deviceId,state:'enrolled',reasonCode:'profileEnrolled'};
  if(operation==='import')profile.receipt={schema:prefix+'mutation.v1',status:'confirmed',action:body.replaceExisting?'replaced':'created',deviceId:body.deviceId,state:'enrolled',reasonCode:body.replaceExisting?'profileReplaced':'profileCreated'};
  if(operation==='revoke')profile.receipt={schema:prefix+'mutation.v1',status:'confirmed',action:'revoked',deviceId:body.deviceId,state:'absent',reasonCode:'profileRevoked'};
  return {requestId:body.requestId,action:body.action,state:'completed',result:null,mutation:null,profile};
}
let browser;test.before(async()=>{browser=await chromium.launch({headless:true});});test.after(async()=>browser?.close());
async function fixture(t,{respond,status,state={},saved,storageFailure=false,width=1280}={}){
  const context=await browser.newContext({serviceWorkers:'block',viewport:{width,height:1000}});t.after(()=>context.close());const page=await context.newPage(),calls=[],errors=[],owner=new Map();page.on('pageerror',e=>errors.push(e.message));
  if(saved!==undefined)await context.addInitScript(({key,saved})=>{if(!sessionStorage.getItem('seeded')){sessionStorage.setItem(key,typeof saved==='string'?saved:JSON.stringify(saved));sessionStorage.setItem('seeded','yes');}},{key:storageKey,saved});
  if(storageFailure)await context.addInitScript(()=>{Storage.prototype.setItem=function(){throw Error('blocked');};});
  await context.route('**/*',async route=>{const url=new URL(route.request().url());if(url.origin!==origin)return route.abort('blockedbyclient');
    if(url.pathname.startsWith('/browser/')){const method=route.request().method(),call={path:url.pathname,method};calls.push(call);
      if(method==='POST'){const headers=route.request().headers(),privateRoute=url.pathname.endsWith('/profiles/import');call.headers=headers;call.bytes=route.request().postDataBuffer();call.body=privateRoute?{requestId:headers['x-qfm-request-id'],action:'profile.import',deviceId:headers['x-qfm-profile-device-id'],replaceExisting:headers['x-qfm-replace-existing']==='true'}:route.request().postDataJSON();call.saved=await page.evaluate(key=>JSON.parse(sessionStorage.getItem(key)||'null'),storageKey);const value=outcome(call.body);owner.set(call.body.requestId,value);return respond?respond(route,call,value):route.fulfill({json:value});}
      const value=owner.get(url.pathname.split('/').at(-1));return status?status(route,value):route.fulfill({json:value});}
    if(url.pathname==='/profile-fixture.js')return route.fulfill({contentType:'text/javascript',body:`import {createProfileView} from './profile-view.js';
      window.ctx=${JSON.stringify({sessionId:'session-a',connected:true,busy:false,actions:['profile.list','profile.status','profile.import','profile.revoke'],capability:{supported:true,code:'platform_supported',maximumPrivateInputBytes:4096,maximumListEntries:128},authGeneration:1,externalUnresolved:false,selected:null,...state})};window.ctx.actions=new Set(window.ctx.actions);window.busyCalls=[];window.latest=[];window.uploadBodies=[];
      window.profiles=createProfileView({getContext:()=>{window.contextReads=(window.contextReads||0)+1;return window.copySelected?{...window.ctx,selected:window.ctx.selected&&{...window.ctx.selected}}:window.ctx;},setBusy:value=>{window.ctx.busy=value;window.busyCalls.push(value);window.siblingBlocked=Boolean(value||window.profiles?.hasUnresolved());window.profiles?.update();},showResult:(value,summary)=>window.latest.push({value,summary}),api:async(path,options={})=>{if(options.body instanceof Uint8Array)window.uploadBodies.push(options.body);const response=await fetch(path,{...options,cache:'no-store'});if(!response.ok){const value=await response.json();throw Object.assign(new Error(value.error),{dispatched:value.dispatched,status:response.status});}return response.json();}});window.profiles.update();window.fixtureReady=true;`});
    const name=url.pathname==='/'?'index.html':url.pathname.slice(1);if(!['index.html','styles.css','profile-view.js'].includes(name))return route.fulfill({status:404,body:''});let bytes=await fs.readFile(path.join(root,name));if(name==='index.html')bytes=Buffer.from(bytes.toString().replace(/<script[^>]*src="\.\/pwa.js"[^>]*><\/script>/,'').replace(/<script[^>]*src="\.\/app.js"[^>]*><\/script>/,'<script type="module" src="./profile-fixture.js"></script>'));return route.fulfill({contentType:name.endsWith('.js')?'text/javascript':name.endsWith('.css')?'text/css':'text/html',body:bytes});});
  await page.goto(origin);await page.waitForFunction(()=>window.fixtureReady);
  const update=patch=>page.evaluate(patch=>{Object.assign(window.ctx,patch);if(Array.isArray(window.ctx.actions))window.ctx.actions=new Set(window.ctx.actions);window.profiles.update();},patch);
  const fill=async(id='alpha')=>{await page.locator('#profile-id').fill(id);await page.locator('#profile-usb').fill(secrets.usb);await page.locator('#profile-endpoint').fill(secrets.endpoint);await page.locator('#profile-code').fill(secrets.code);};
  const create=async()=>{await page.locator('#profile-create-confirm').check();await page.locator('#profile-create').click();await page.waitForFunction(()=>!window.ctx.busy);};
  const replace=async()=>{await page.locator('#profile-replace-confirm').check();await page.locator('#profile-replace').click();await page.waitForFunction(()=>!window.ctx.busy);};
  const revoke=async()=>{await page.locator('#profile-revoke-confirm').check();await page.locator('#profile-revoke').click();await page.waitForFunction(()=>!window.ctx.busy);};
  const read=async id=>{await page.locator('#profile-'+id).click();await page.waitForFunction(()=>!window.ctx.busy);};
  const stored=()=>page.evaluate(key=>JSON.parse(sessionStorage.getItem(key)||'null'),storageKey);
  const cleared=async()=>{for(const id of ['usb','endpoint','code','file'])assert.equal(await page.locator('#profile-'+id).inputValue(),'');assert.equal(await page.locator('#profile-replace-confirm').isChecked(),false);assert.equal(await page.evaluate(()=>window.uploadBodies.every(bytes=>bytes.every(b=>b===0))),true);};
  t.after(()=>assert.deepEqual(errors,[]));return {page,calls,owner,update,fill,create,replace,revoke,read,stored,cleared};
}

const selected=()=>({serial:'SELECTED-USB-FIXTURE',isReady:true,isWifiConnection:false});
async function entered(f,id='alpha'){
  await f.page.locator('#profile-mode').selectOption('selected-usb');await f.page.locator('#profile-id').fill(id);
  await f.page.locator('#profile-endpoint').fill(secrets.endpoint);await f.page.locator('#profile-code').fill(secrets.code);
}

test('shipped profile has explicit selected USB source and local-owner title',async t=>{
  const f=await fixture(t);assert.equal(await f.page.locator('#profile-mode option[value="selected-usb"]').count(),1);assert.equal(await f.page.locator('#profile-selected-usb').count(),1);assert.equal(await f.page.locator('#profile-title').textContent(),'Local connectivity profiles');assert.equal(f.calls.length,0);
});

for(const replace of [false,true])test('selected ready USB + manually entered link uses exact existing '+(replace?'replacement':'create')+' contract',async t=>{
  const f=await fixture(t,{state:{selected:selected()}});await entered(f);assert.equal(await f.page.locator('#profile-manual-usb').isHidden(),true);assert.equal(await f.page.locator('#profile-usb').isDisabled(),true);
  await f.page.locator('#profile-usb').evaluate(e=>e.value='HIDDEN-EDITOR-CANNOT-SELECT-TARGET');assert.match(await f.page.locator('#profile-selected-usb').textContent(),/SELECTED-USB-FIXTURE/);
  assert.equal(f.calls.length,0);if(replace)await f.replace();else await f.create();const call=f.calls[0];
  assert.equal(call.path,'/browser/v1/profiles/import');assert.deepEqual(JSON.parse(call.bytes.toString()),{schema:prefix+'enrollment.v1',target:'QuestIonAbleFileManager/QuestConnectivity/alpha',device_id:'alpha',usb_serial:selected().serial,endpoint:secrets.endpoint,pairing_code:secrets.code});
  assert.deepEqual(Object.keys(call.headers).filter(k=>k.startsWith('x-qfm-')).sort(),['x-qfm-request-id','x-qfm-profile-device-id','x-qfm-confirm-profile-write','x-qfm-replace-existing'].sort());assert.equal(call.headers['x-qfm-replace-existing'],String(replace));
  assert.deepEqual(call.saved,{requestId:call.body.requestId,sessionId:'session-a',action:'profile.import',deviceId:'alpha',replaceExisting:replace});
  const observed=await f.page.evaluate(()=>({storage:{...sessionStorage},latest:window.latest}));for(const value of [...Object.values(secrets),selected().serial])assert.equal(JSON.stringify(observed).includes(value),false);assert.doesNotMatch(JSON.stringify(observed),/usb_serial|endpoint|pairing_code|sha256|document|sourceMode/);
  assert.equal(await f.stored(),null);await f.cleared();assert.equal(f.calls.length,1);
});

for(const row of [null,{serial:'USB-A',isReady:false,isWifiConnection:false},{serial:'USB-A',isReady:true,isWifiConnection:true},{serial:'USB-A',isReady:true},{serial:'USB-A',isReady:1,isWifiConnection:false},{serial:'',isReady:true,isWifiConnection:false},{serial:'bad serial',isReady:true,isWifiConnection:false}])test('selected source rejects absent Wi-Fi unready or malformed row '+JSON.stringify(row),async t=>{
  const f=await fixture(t,{state:{selected:row}});await entered(f);assert.equal(await f.page.locator('#profile-create-confirm').isDisabled(),true);assert.equal(await f.page.locator('#profile-replace-confirm').isDisabled(),true);await f.page.locator('#profile-create').evaluate(e=>{e.disabled=false;e.click();});assert.equal(f.calls.length,0);assert.equal(await f.stored(),null);
});

test('selected mode still requires manually entered endpoint and code',async t=>{
  const f=await fixture(t,{state:{selected:selected()}});await f.page.locator('#profile-mode').selectOption('selected-usb');await f.page.locator('#profile-id').fill('alpha');assert.equal(await f.page.locator('#profile-create-confirm').isDisabled(),true);
  await f.page.locator('#profile-endpoint').fill(secrets.endpoint);assert.equal(await f.page.locator('#profile-create-confirm').isDisabled(),true);await f.page.locator('#profile-code').fill(secrets.code);assert.equal(await f.page.locator('#profile-create-confirm').isDisabled(),false);assert.equal(f.calls.length,0);
});

test('equivalent copied row values preserve approval while changes revoke it',async t=>{
  const f=await fixture(t,{state:{selected:selected()}});await entered(f);await f.page.locator('#profile-create-confirm').check();await f.page.evaluate(()=>{window.copySelected=true;window.profiles.update();window.profiles.update();});assert.equal(await f.page.locator('#profile-create-confirm').isChecked(),true);
  await f.update({selected:{...selected(),serial:'OTHER-USB'}});assert.equal(await f.page.locator('#profile-create-confirm').isChecked(),false);assert.match(await f.page.locator('#profile-selected-usb').textContent(),/OTHER-USB/);assert.equal(f.calls.length,0);
});

for(const patch of [{selected:null},{selected:{...selected(),isReady:false}},{selected:{...selected(),isWifiConnection:true}},{authGeneration:2},{sessionId:'session-b'}])test('selected row/readiness/transport/auth scope revokes consent '+JSON.stringify(patch),async t=>{
  const f=await fixture(t,{state:{selected:selected()}});await entered(f);await f.page.locator('#profile-replace-confirm').check();await f.update(patch);assert.equal(await f.page.locator('#profile-replace-confirm').isChecked(),false);assert.equal(f.calls.length,0);
});

test('manual and private-file sources stay independent of selected row and transport',async t=>{
  const f=await fixture(t,{state:{selected:{...selected(),isWifiConnection:true}}});await f.fill();await f.page.locator('#profile-create-confirm').check();await f.update({selected:null});assert.equal(await f.page.locator('#profile-create-confirm').isChecked(),true);await f.page.locator('#profile-create').click();await f.page.waitForFunction(()=>!window.ctx.busy);assert.equal(JSON.parse(f.calls[0].bytes.toString()).usb_serial,secrets.usb);
  await f.page.locator('#profile-mode').selectOption('file');await f.page.locator('#profile-file').setInputFiles({name:'private.json',mimeType:'application/json',buffer:Buffer.from('{"private":"owned-file-bytes"}')});await f.create();assert.equal(f.calls[1].bytes.toString(),'{"private":"owned-file-bytes"}');await f.cleared();
});

test('selected mode does not add a headset prerequisite to revoke or local reads',async t=>{
  let held,enteredRead;const arrived=new Promise(resolve=>enteredRead=resolve);
  const f=await fixture(t,{state:{selected:selected()},respond:(route,call,value)=>{if(call.body.action==='profile.list'){held=()=>route.fulfill({json:value});enteredRead();return;}return route.fulfill({json:value});}});
  await f.page.locator('#profile-mode').selectOption('selected-usb');await f.page.locator('#profile-id').fill('alpha');await f.page.locator('#profile-revoke-confirm').check();await f.update({selected:null});assert.equal(await f.page.locator('#profile-revoke-confirm').isChecked(),true);await f.page.locator('#profile-revoke').click();await f.page.waitForFunction(()=>!window.ctx.busy);
  await f.page.locator('#profile-list').click();await arrived;await f.update({selected:{...selected(),isWifiConnection:true}});await held();await f.page.waitForFunction(()=>!window.ctx.busy);assert.equal(await f.page.locator('#profile-ids button').count(),2);assert.deepEqual(f.calls.map(c=>c.body.action),['profile.revoke','profile.list']);
});

for(const phase of ['encode','post-persist'])for(const change of ['row','transport','auth','mode'])test(change+' change at '+phase+' invalidates prepared bytes before POST',async t=>{
  const f=await fixture(t,{state:{selected:selected()}});await entered(f);
  await f.page.evaluate(({phase,change})=>{
    const changeScope=()=>{if(change==='row')window.ctx.selected={...window.ctx.selected,serial:'OTHER-USB'};else if(change==='transport')window.ctx.selected={...window.ctx.selected,isWifiConnection:true};else if(change==='auth')window.ctx.authGeneration++;else document.querySelector('#profile-mode').value='manual';};
    const encode=TextEncoder.prototype.encode;TextEncoder.prototype.encode=function(value){const bytes=encode.call(this,value);if(typeof value==='string'&&value.includes('enrollment.v1')){window.preparedPrivate=bytes;if(phase==='encode')changeScope();}return bytes;};
    if(phase==='post-persist'){const setItem=Storage.prototype.setItem;let once=false;Storage.prototype.setItem=function(key,value){setItem.call(this,key,value);if(!once&&key==='qfm-browser-profile-v1'&&value.includes('profile.import')){once=true;changeScope();}};}
  },{phase,change});
  await f.create();assert.equal(f.calls.length,0);assert.equal(await f.stored(),null);assert.equal(await f.page.evaluate(()=>window.preparedPrivate.every(b=>b===0)),true);await f.cleared();
});

test('an admitted original is never retargeted or erased by selection changes; only cached receipt reconciles',async t=>{
  let held,enteredWrite;const arrived=new Promise(resolve=>enteredWrite=resolve);const f=await fixture(t,{state:{selected:selected()},respond:(route,call,value)=>{if(call.body.action==='profile.import'){held=()=>route.fulfill({json:value});enteredWrite();return;}return route.fulfill({json:value});}});
  await entered(f);await f.page.locator('#profile-create-confirm').check();await f.page.locator('#profile-create').click();await arrived;const original=await f.stored();assert.equal(JSON.parse(f.calls[0].bytes.toString()).usb_serial,selected().serial);
  await f.update({selected:{...selected(),serial:'OTHER-USB'}});assert.deepEqual(await f.stored(),original);assert.equal(await f.page.evaluate(()=>window.uploadBodies[0].some(b=>b!==0)),true);
  await held();await f.page.waitForFunction(()=>!window.ctx.busy);assert.deepEqual(await f.stored(),original);await f.cleared();await f.read('status');assert.deepEqual(await f.stored(),original);
  await f.update({selected:null});await f.page.locator('#profile-original-status').click();await f.page.waitForFunction(()=>!window.profiles.hasUnresolved());assert.equal(await f.stored(),null);assert.equal(f.calls.filter(c=>c.path.endsWith('/profiles/import')).length,1);
});

test('mode changes send nothing and clear private fields without deriving Direct credentials',async t=>{
  const f=await fixture(t,{state:{selected:selected(),connection:{pairingCode:'DIRECT-KEY-MUST-NOT-BE-USED',endpoint:'http://192.168.0.1:39873/'}}});await f.fill();const before=f.calls.length;await f.page.locator('#profile-mode').selectOption('selected-usb');await f.cleared();assert.equal(f.calls.length,before);assert.equal(await f.page.locator('#profile-endpoint').inputValue(),'');assert.equal(await f.page.locator('#profile-code').inputValue(),'');
});

test('unsupported store capability remains unavailable even with selected USB',async t=>{
  const f=await fixture(t,{state:{selected:selected(),capability:{supported:false,code:'windows_credential_manager_required',maximumPrivateInputBytes:4096,maximumListEntries:128}}});assert.equal(await f.page.locator('#profile-mode').isDisabled(),true);assert.equal(await f.page.locator('#profile-create-confirm').isDisabled(),true);assert.equal(f.calls.length,0);
});

for(const width of [1280,390])test('selected USB local-profile card fits '+width+' with no private fields in screenshot',async t=>{
  const serial='USB_'+'A'.repeat(252),f=await fixture(t,{width,state:{selected:{serial,isReady:true,isWifiConnection:false}}});await entered(f);await f.create();await f.cleared();assert.equal(await f.page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth),true);
  const dir=path.resolve(__dirname,'../../local/browser-profile-selected-usb-proof');await fs.mkdir(dir,{recursive:true});await f.page.locator('#profile-view').screenshot({path:path.join(dir,width===390?'profile-selected-mobile.png':'profile-selected-desktop.png')});assert.equal(f.calls.length,1);
});

test('actual app passes its selected ready USB row and unchanged private headers without Fleet or Direct requests',async t=>{
  const context=await browser.newContext({serviceWorkers:'block',viewport:{width:1280,height:1000}});t.after(()=>context.close());
  await context.addInitScript(()=>Object.defineProperty(navigator,'usb',{configurable:true,value:undefined}));
  const page=await context.newPage(),calls=[],errors=[],hostOrigin='http://127.0.0.1:18152';page.on('pageerror',error=>errors.push(error.message));page.setDefaultTimeout(6000);
  const devices=[{serial:'APP-USB-A',model:'Fixture USB',state:'device',isReady:true,isWifiConnection:false},{serial:'192.168.7.8:5555',model:'Fixture Wi-Fi',state:'device',isReady:true,isWifiConnection:true}];
  const allowed=new Set(['index.html','app.js','pwa.js','retained-view.js','transfer-view.js','verified-download.js','apk-read-view.js','file-push-view.js','kiosk-view.js','direct-connection-view.js','direct-command-view.js','profile-view.js','fleet-status-view.js','diagnostics-view.js','direct-tags-view.js','direct-staging-view.js','direct-upload-view.js','direct-install-view.js','package-stop-view.js','apk-set-view.js','apk-install-view.js','vendor/noble-sha256.js','styles.css','usb-proof.js','usb-proof-core.js','vendor/tango-adb.js','manifest.webmanifest']);
  await context.route('**/*',async route=>{
    const url=new URL(route.request().url());if(url.origin!==hostOrigin)return route.abort('blockedbyclient');
    if(url.pathname.startsWith('/browser/')){
      const request=route.request(),headers=request.headers(),privateInput=url.pathname==='/browser/v1/profiles/import',body=request.method()==='POST'?request.postDataJSON():null;
      const call={path:url.pathname,method:request.method(),headers,body};calls.push(call);
      if(url.pathname==='/browser/v1/session')return route.fulfill({json:{sessionId:'fixture-session',actions:['devices','device.status','profile.list','profile.status','profile.import','profile.revoke'],connectivityProfiles:{supported:true,code:'platform_supported',maximumPrivateInputBytes:4096,maximumListEntries:128}}});
      if(privateInput){call.saved=await page.evaluate(key=>JSON.parse(sessionStorage.getItem(key)||'null'),storageKey);return route.fulfill({json:outcome({requestId:headers['x-qfm-request-id'],action:'profile.import',deviceId:headers['x-qfm-profile-device-id'],replaceExisting:headers['x-qfm-replace-existing']==='true'})});}
      if(body?.action==='devices')return route.fulfill({json:{requestId:body.requestId,state:'completed',result:{devices}}});
      if(body?.action==='device.status')return route.fulfill({json:{requestId:body.requestId,state:'completed',result:{command:{serial:body.serial},questControlStatus:{wakefulness:'Awake',displayState:'ON',proximityState:'CLOSE',capturedAt:'2026-01-01T00:00:00Z'}}}});
      return route.abort('blockedbyclient');
    }
    const name=url.pathname==='/'?'index.html':url.pathname.slice(1);if(!allowed.has(name))return route.fulfill({status:404,body:''});
    return route.fulfill({contentType:name.endsWith('.js')?'text/javascript':name.endsWith('.css')?'text/css':'text/html',body:await fs.readFile(path.join(root,name))});
  });
  await page.goto(hostOrigin);await page.waitForFunction(()=>!document.querySelector('#usb-capability').textContent.includes('Checking'));assert.equal(calls.length,0);
  await page.locator('#token').fill('fixture-only-not-a-real-credential');await page.locator('#connect').click();await page.waitForFunction(()=>document.querySelector('#host-status').textContent==='Connected');
  await page.locator('#profile-mode').selectOption('selected-usb');assert.equal(calls.length,1,'Mode does not discover devices or probe any store');
  await page.locator('#refresh').click();await page.locator('#devices button').filter({hasText:'APP-USB-A'}).click();await page.waitForFunction(()=>document.querySelector('#power-value').textContent==='Awake');
  assert.match(await page.locator('#profile-selected-usb').textContent(),/APP-USB-A/);const before=calls.length;
  await page.locator('#profile-id').fill('alpha');await page.locator('#profile-endpoint').fill(secrets.endpoint);await page.locator('#profile-code').fill(secrets.code);assert.equal(calls.length,before);
  await page.locator('#profile-create-confirm').check();await page.locator('#profile-create').click();await page.waitForFunction(()=>document.querySelector('#profile-state').textContent.includes('write confirmed'));
  const imported=calls.find(c=>c.path==='/browser/v1/profiles/import');assert.equal(imported.body.usb_serial,'APP-USB-A');assert.equal(imported.body.endpoint,secrets.endpoint);assert.equal(imported.body.pairing_code,secrets.code);assert.equal(imported.headers['x-qfm-session'],'fixture-session');assert.equal(imported.headers['x-qfm-confirm-profile-write'],'true');assert.equal(imported.headers['x-qfm-replace-existing'],'false');
  assert.deepEqual(Object.keys(imported.saved).sort(),['requestId','sessionId','action','deviceId','replaceExisting'].sort());
  const retained=await page.evaluate(()=>JSON.stringify({storage:{...sessionStorage},result:document.querySelector('#result-json').textContent}));for(const value of ['APP-USB-A',secrets.endpoint,secrets.code])assert.equal(retained.includes(value),false);
  await page.locator('#devices button').filter({hasText:'192.168.7.8:5555'}).click();await page.waitForFunction(()=>!document.querySelector('#refresh').disabled);await page.locator('#profile-endpoint').fill(secrets.endpoint);await page.locator('#profile-code').fill(secrets.code);assert.equal(await page.locator('#profile-create-confirm').isDisabled(),true);
  assert.equal(calls.filter(c=>c.path==='/browser/v1/profiles/import').length,1);assert.equal(calls.some(c=>c.path.includes('fleet')||c.body?.action?.startsWith('fleet.')||c.body?.action?.startsWith('kiosk.direct.')),false);assert.deepEqual(errors,[]);
});
