// Shipped DOM/module with synthetic intercepted requests; no server or device.
const test=require('node:test'),assert=require('node:assert/strict'),fs=require('node:fs/promises'),path=require('node:path');
const {chromium}=require('playwright');
const root=path.resolve(__dirname,'../../src/QuestIonAbleFileManager.BrowserHost/wwwroot');
const storageKey='qfm-browser-direct-connection-v1',origin='http://127.0.0.1:18139';
const usb=id=>'usb_'+id.replaceAll('-','');
const handle='a1234567-1234-1234-1234-123456789abc';
const schema='questionable.file_manager.kiosk_direct_usb.v1',cleanupSchema='questionable.file_manager.kiosk_direct_usb_cleanup.v1';
function outcome(body,connection){
  const connect=body.action==='kiosk.direct.connect-usb';
  const value=connect?{connectionId:handle,connectRequestId:body.requestId,usbOperationId:usb(body.requestId),serial:body.serial,productChannel:body.productChannel,expiresAtUtc:new Date(Date.now()+300000).toISOString(),enabledByRequest:true}:structuredClone(connection);
  value.state=body.action==='kiosk.direct.close'?'closed':'connected';
  if(connect){value.bootstrap={schema,operationId:value.usbOperationId,stage:'confirmed'};value.adoption={stage:'confirmed',signedStatusRead:true,typedKioskStatusRead:true,stagingInventoryRead:true};}
  else {delete value.bootstrap;delete value.adoption;}
  if(body.action==='kiosk.direct.status')value.status={installerAllowed:false,observedAtUtc:new Date().toISOString()};
  if(body.action==='kiosk.direct.close')value.cleanup={schema:cleanupSchema,operationId:value.usbOperationId,stage:'confirmed',listenerDisposition:value.enabledByRequest?'owned-stopped':'preexisting-preserved'};
  return {requestId:body.requestId,action:body.action,state:'completed',result:null,mutation:null,kioskDirectConnection:value};
}
let browser;test.before(async()=>{browser=await chromium.launch({headless:true});});test.after(async()=>browser?.close());
async function fixture(t,{respond,status,width=1280,state={},saved,storageFailure=false}={}){
  const context=await browser.newContext({serviceWorkers:'block',viewport:{width,height:1000}});t.after(()=>context.close());const page=await context.newPage(),calls=[],errors=[],owner=new Map();let connection=saved?.connection?{...saved.connection,enabledByRequest:true}:undefined;
  page.on('pageerror',error=>errors.push(error.message));
  if(saved!==undefined)await context.addInitScript(({key,saved})=>{if(!sessionStorage.getItem('fixture-seeded')){sessionStorage.setItem(key,typeof saved==='string'?saved:JSON.stringify(saved));sessionStorage.setItem('fixture-seeded','yes');}},{key:storageKey,saved});
  if(storageFailure)await context.addInitScript(()=>{Storage.prototype.setItem=function(){throw new Error('unavailable');};});
  await context.route('**/*',async route=>{
    const url=new URL(route.request().url());if(url.origin!==origin)return route.abort('blockedbyclient');
    if(url.pathname.startsWith('/browser/')){
      const method=route.request().method(),body=method==='POST'?route.request().postDataJSON():undefined,call={method,path:url.pathname,body};calls.push(call);
      if(body){const value=outcome(body,connection);if(body.action==='kiosk.direct.connect-usb')connection=structuredClone(value.kioskDirectConnection);owner.set(body.requestId,value);call.saved=await page.evaluate(key=>JSON.parse(sessionStorage.getItem(key)||'null'),storageKey);return respond?respond(route,body,value):route.fulfill({json:value});}
      const value=owner.get(url.pathname.split('/').at(-1));return status?status(route,value):route.fulfill({json:value});
    }
    if(url.pathname==='/direct-fixture.js')return route.fulfill({contentType:'text/javascript',body:`import {createDirectConnectionView} from './direct-connection-view.js';
      window.ctx=${JSON.stringify({sessionId:'session-a',connected:true,busy:false,selected:{serial:'QUEST-A',isReady:true,isWifiConnection:false},actions:['kiosk.direct.connect-usb','kiosk.direct.status','kiosk.direct.close'],externalUnresolved:false,...state})};window.ctx.actions=new Set(window.ctx.actions);window.busyCalls=[];
      window.direct=createDirectConnectionView({getContext:()=>window.ctx,setBusy:value=>{window.busyCalls.push(value);window.ctx.busy=value;window.siblingBlocked=Boolean(value||window.direct?.hasUnresolved());window.direct?.update();},showResult:(value,summary)=>window.latest={value,summary},
      api:async(path,options)=>{const response=await fetch(path,{...options,cache:'no-store'});if(!response.ok){const value=await response.json();throw Object.assign(new Error(value.error),{dispatched:value.dispatched,status:response.status});}return response.json();}});window.direct.update();window.fixtureReady=true;`});
    const name=url.pathname==='/'?'index.html':url.pathname.slice(1);if(!['index.html','styles.css','direct-connection-view.js'].includes(name))return route.fulfill({status:404,body:''});
    let bytes=await fs.readFile(path.join(root,name));if(name==='index.html')bytes=Buffer.from(bytes.toString().replace(/<script[^>]*src="\.\/pwa.js"[^>]*><\/script>/,'').replace(/<script[^>]*src="\.\/app.js"[^>]*><\/script>/,'<script type="module" src="./direct-fixture.js"></script>'));
    return route.fulfill({contentType:name.endsWith('.js')?'text/javascript':name.endsWith('.css')?'text/css':'text/html',body:bytes});
  });
  await page.goto(origin);await page.waitForFunction(()=>window.fixtureReady);
  const update=patch=>page.evaluate(patch=>{Object.assign(window.ctx,patch);if(Array.isArray(window.ctx.actions))window.ctx.actions=new Set(window.ctx.actions);window.direct.update();},patch);
  const connect=async()=>{await page.locator('#direct-connect-confirm').check();await page.locator('#direct-connect').click();await page.waitForFunction(()=>!window.ctx.busy);};
  const close=async()=>{await page.locator('#direct-close-confirm').check();await page.locator('#direct-close').click();await page.waitForFunction(()=>!window.ctx.busy);};
  const read=async()=>{await page.locator('#direct-status').click();await page.waitForFunction(()=>!window.ctx.busy);};
  const stored=()=>page.evaluate(key=>JSON.parse(sessionStorage.getItem(key)||'null'),storageKey);
  t.after(()=>assert.deepEqual(errors,[],'No uncaught Direct UI errors'));return {page,calls,owner,update,connect,close,read,stored};
}
test('Direct controls are inert without JavaScript',async t=>{
  const context=await browser.newContext({javaScriptEnabled:false,serviceWorkers:'block'});t.after(()=>context.close());await context.route('**/*',async route=>new URL(route.request().url()).pathname==='/'?route.fulfill({contentType:'text/html',body:await fs.readFile(path.join(root,'index.html'))}):route.abort('blockedbyclient'));
  const page=await context.newPage();page.setDefaultTimeout(1000);await page.goto(origin);assert.equal(await page.locator('#direct-connection-view').count(),1);
  for(const id of ['channel','connect-confirm','connect','status','close-confirm','close','original-status','original-save'])assert.equal(await page.locator('#direct-'+id).isDisabled(),true);
});
test('one explicit USB connect persists original identity before POST and stores only handle metadata after acknowledgement',async t=>{
  const f=await fixture(t);assert.equal(f.calls.length,0);assert.equal(await f.page.locator('#direct-connect').isDisabled(),true);await f.connect();
  const call=f.calls[0],id=call.body.requestId;assert.deepEqual(call.body,{requestId:id,action:'kiosk.direct.connect-usb',serial:'QUEST-A',productChannel:'stable',confirmed:true});
  assert.equal(call.saved.original.requestId,id);assert.equal(call.saved.original.usbOperationId,usb(id));assert.equal(call.saved.original.sessionId,'session-a');assert.equal(call.saved.connection,null);
  const saved=await f.stored();assert.equal(saved.original,null);assert.equal(saved.connection.connectionId,handle);assert.equal(saved.connection.connectRequestId,id);assert.equal(saved.connection.usbOperationId,usb(id));
  assert.deepEqual(Object.keys(saved.connection).sort(),['sessionId','connectionId','connectRequestId','usbOperationId','serial','productChannel','expiresAtUtc'].sort());
  assert.equal(await f.page.evaluate(()=>window.direct.hasUnresolved()),false);assert.match(await f.page.locator('#direct-connection-state').textContent(),/adopted|authenticated/i);
  assert.match(await f.page.locator('#direct-boundary').textContent(),/readiness/i);assert.equal(await f.page.locator('#direct-connect').isDisabled(),true);
});
test('signed status and close bind saved stable connection independently of current target and Labs channel',async t=>{
  const f=await fixture(t);await f.connect();const before=await f.stored();await f.page.locator('#direct-channel').selectOption('labs');await f.update({selected:{serial:'QUEST-B',isReady:true,isWifiConnection:false}});await f.read();
  assert.deepEqual(f.calls[1].body,{requestId:f.calls[1].body.requestId,action:'kiosk.direct.status',connectionId:handle});assert.deepEqual(await f.stored(),before);
  assert.match(await f.page.locator('#direct-status-state').textContent(),/Installer allowed: no/);assert.equal(await f.page.locator('#direct-connection-target').textContent(),'QUEST-A');assert.equal(await f.page.locator('#direct-connection-channel').textContent(),'stable');
  await f.close();const call=f.calls[2];assert.deepEqual(call.body,{requestId:call.body.requestId,action:'kiosk.direct.close',connectionId:handle,confirmed:true});assert.equal(call.saved.original.action,'kiosk.direct.close');assert.equal(call.saved.original.connectionId,handle);assert.equal(await f.stored(),null);assert.match(await f.page.locator('#direct-cleanup-state').textContent(),/owned listener stopped/);assert.equal(await f.page.locator('#direct-disposition').textContent(),'owned-stopped');
  assert.equal(f.calls.filter(c=>c.body?.action==='kiosk.direct.connect-usb').length,1);
});
test('pre-existing listener is preserved and never described as stopped',async t=>{
  const f=await fixture(t,{respond:(route,body,value)=>{value.kioskDirectConnection.enabledByRequest=false;if(value.kioskDirectConnection.cleanup)value.kioskDirectConnection.cleanup.listenerDisposition='preexisting-preserved';return route.fulfill({json:value});}});
  await f.connect();await f.close();assert.equal(await f.stored(),null);assert.match(await f.page.locator('#direct-cleanup-state').textContent(),/pre-existing listener preserved/);assert.doesNotMatch(await f.page.locator('#direct-cleanup-state').textContent(),/listener stopped/);assert.equal(await f.page.locator('#direct-disposition').textContent(),'preexisting-preserved');
});
for(const state of [{isReady:false,isWifiConnection:false},{isReady:true,isWifiConnection:true},{isReady:true},{isReady:true,isWifiConnection:false,serial:'192.0.2.1:5555'}])test('connect requires an explicitly ready classic USB target '+JSON.stringify(state),async t=>{
  const f=await fixture(t,{state:{selected:{serial:'QUEST-A',...state}}});assert.equal(await f.page.locator('#direct-connect-confirm').isDisabled(),true);assert.equal(f.calls.length,0);
});
test('Labs is explicit and approval resets on target channel auth capability and handle changes',async t=>{
  const f=await fixture(t);for(const change of [()=>f.page.locator('#direct-channel').selectOption('labs'),()=>f.update({selected:{serial:'QUEST-B',isReady:true,isWifiConnection:false}}),()=>f.update({connected:false})]){
    await f.page.locator('#direct-connect-confirm').check();await change();assert.equal(await f.page.locator('#direct-connect-confirm').isChecked(),false);
  }
  await f.update({connected:true});await f.page.locator('#direct-connect-confirm').check();await f.update({actions:[]});assert.equal(await f.page.locator('#direct-connect-confirm').isChecked(),false);await f.update({actions:['kiosk.direct.connect-usb','kiosk.direct.status','kiosk.direct.close']});await f.connect();assert.equal(f.calls[0].body.productChannel,'labs');assert.equal(f.calls[0].body.serial,'QUEST-B');assert.equal(await f.page.locator('#direct-connect-confirm').isChecked(),false);
});
for(const field of ['request','action','target','channel','connect ID','USB ID','connection ID','expiry','bootstrap schema','bootstrap ID','bootstrap stage','adoption stage','signed read','typed read','staging read','raw result','mutation'])test('crossed/incomplete connect '+field+' cannot acknowledge original',async t=>{
  const f=await fixture(t,{respond:(route,body,value)=>{const d=value.kioskDirectConnection;if(field==='request')value.requestId=crypto.randomUUID();if(field==='action')value.action='kiosk.direct.close';if(field==='target')d.serial='QUEST-B';if(field==='channel')d.productChannel='labs';if(field==='connect ID')d.connectRequestId=crypto.randomUUID();if(field==='USB ID')d.usbOperationId='usb_'+'f'.repeat(32);if(field==='connection ID')d.connectionId='00000000-0000-0000-0000-000000000000';if(field==='expiry')d.expiresAtUtc='tomorrow';if(field==='bootstrap schema')d.bootstrap.schema='unbound';if(field==='bootstrap ID')d.bootstrap.operationId='usb_'+'f'.repeat(32);if(field==='bootstrap stage')d.bootstrap.stage='pending';if(field==='adoption stage')d.adoption.stage='pending';if(field==='signed read')d.adoption.signedStatusRead=false;if(field==='typed read')d.adoption.typedKioskStatusRead=false;if(field==='staging read')d.adoption.stagingInventoryRead=false;if(field==='raw result')value.result={private:'capture'};if(field==='mutation')value.mutation={stage:'confirmed'};return route.fulfill({json:value});}});
  await f.connect();assert.ok((await f.stored()).original);assert.equal((await f.stored()).connection,null);assert.equal(await f.page.evaluate(()=>window.direct.hasUnresolved()),true);assert.equal(await f.page.locator('#direct-close').isDisabled(),true);
});
test('lost connect reply reload sends nothing; manual original GET recovers handle independently of selection/channel',async t=>{
  const f=await fixture(t,{respond:(route,body,value)=>body.action==='kiosk.direct.connect-usb'?route.abort('failed'):route.fulfill({json:value})});await f.connect();const before=await f.stored();assert.equal(await f.page.evaluate(()=>window.direct.hasUnresolved()),true);
  await f.page.reload();await f.page.waitForFunction(()=>window.fixtureReady);assert.equal(f.calls.length,1);assert.deepEqual(await f.stored(),before);await f.page.locator('#direct-channel').selectOption('labs');await f.update({selected:{serial:'QUEST-B',isReady:true,isWifiConnection:false}});
  await f.page.locator('#direct-original-status').click();await f.page.waitForFunction(()=>!window.direct.hasUnresolved());assert.equal(f.calls[1].method,'GET');assert.equal(f.calls[1].path,'/browser/v1/requests/'+before.original.requestId);assert.equal((await f.stored()).connection.serial,'QUEST-A');assert.equal((await f.stored()).connection.productChannel,'stable');assert.equal(await f.page.evaluate(()=>window.siblingBlocked),false);assert.equal(f.calls.length,2);
});
test('completed handle survives reload without status and can close explicitly',async t=>{
  const f=await fixture(t);await f.connect();const before=await f.stored();await f.page.reload();await f.page.waitForFunction(()=>window.fixtureReady);assert.equal(f.calls.length,1);assert.deepEqual(await f.stored(),before);assert.match(await f.page.locator('#direct-connection-state').textContent(),/Saved/);await f.close();assert.equal(await f.stored(),null);assert.equal(f.calls[1].body.action,'kiosk.direct.close');
});
test('changed host dynamically blocks global mutations before update, preserves refs, and sends nothing',async t=>{
  const f=await fixture(t);await f.connect();const before=await f.stored();const blocked=await f.page.evaluate(()=>{window.ctx.sessionId='session-b';return window.direct.hasUnresolved();});assert.equal(blocked,true);await f.update({});assert.equal(await f.page.locator('#direct-close-confirm').isDisabled(),true);assert.equal(await f.page.locator('#direct-status').isDisabled(),true);assert.equal(await f.page.locator('#direct-connect-confirm').isDisabled(),true);assert.deepEqual(await f.stored(),before);assert.equal(f.calls.length,1);assert.match(await f.page.locator('#direct-state').textContent(),/different host session/);
});
test('lost close reply preserves original after reload/new host; separate status never acknowledges cleanup',async t=>{
  const f=await fixture(t,{respond:(route,body,value)=>body.action==='kiosk.direct.close'?route.abort('failed'):route.fulfill({json:value})});await f.connect();await f.close();const before=await f.stored();assert.equal(before.original.action,'kiosk.direct.close');await f.read();assert.deepEqual(await f.stored(),before);assert.match(await f.page.locator('#direct-status-state').textContent(),/Original outcome unchanged/);
  await f.page.reload();await f.page.waitForFunction(()=>window.fixtureReady);assert.equal(f.calls.length,3);await f.update({sessionId:'session-b'});assert.equal(await f.page.locator('#direct-original-status').isDisabled(),true);assert.deepEqual(await f.stored(),before);await f.update({sessionId:'session-a'});await f.page.locator('#direct-original-status').click();await f.page.waitForFunction(()=>!window.direct.hasUnresolved());assert.equal(await f.stored(),null);assert.equal(f.calls.filter(c=>c.body?.action==='kiosk.direct.close').length,1);
});
for(const field of ['cleanup schema','cleanup ID','cleanup stage','disposition','enabled','connection','expiry'])test('close '+field+' mismatch remains unknown',async t=>{
  const f=await fixture(t,{respond:(route,body,value)=>{if(body.action==='kiosk.direct.close'){const d=value.kioskDirectConnection;if(field==='cleanup schema')d.cleanup.schema='unbound';if(field==='cleanup ID')d.cleanup.operationId='usb_'+'f'.repeat(32);if(field==='cleanup stage')d.cleanup.stage='cleanupUnknown';if(field==='disposition')d.cleanup.listenerDisposition='preexisting-preserved';if(field==='enabled')d.enabledByRequest=null;if(field==='connection')d.connectionId=crypto.randomUUID();if(field==='expiry')d.expiresAtUtc=new Date(Date.now()+60000).toISOString();}return route.fulfill({json:value});}});await f.connect();await f.close();assert.equal((await f.stored()).original.action,'kiosk.direct.close');assert.equal(await f.page.evaluate(()=>window.direct.hasUnresolved()),true);assert.doesNotMatch(await f.page.locator('#direct-cleanup-state').textContent(),/Close confirmed/);
});
test('locally expired handle sends nothing automatically and still permits explicit close',async t=>{
  const id='b1234567-1234-1234-1234-123456789abc',ref={sessionId:'session-a',connectionId:handle,connectRequestId:id,usbOperationId:usb(id),serial:'QUEST-A',productChannel:'stable',expiresAtUtc:new Date(Date.now()-10000).toISOString()};
  const f=await fixture(t,{saved:{original:null,connection:ref}});assert.equal(f.calls.length,0);assert.equal(await f.page.locator('#direct-status').isDisabled(),true);assert.match(await f.page.locator('#direct-connection-state').textContent(),/expired/);await f.close();assert.equal(f.calls.length,1);assert.equal(f.calls[0].body.action,'kiosk.direct.close');assert.equal(await f.stored(),null);
});
test('readonly status loss does not save original or bootstrap again',async t=>{
  const f=await fixture(t,{respond:(route,body,value)=>body.action==='kiosk.direct.status'?route.abort('failed'):route.fulfill({json:value})});await f.connect();const before=await f.stored();await f.read();assert.deepEqual(await f.stored(),before);assert.equal(await f.page.evaluate(()=>window.direct.hasUnresolved()),false);assert.match(await f.page.locator('#direct-status-state').textContent(),/unavailable/);assert.equal(f.calls.filter(c=>c.body?.action==='kiosk.direct.connect-usb').length,1);
});
for(const code of ['busy','direct_unavailable','direct_connection_exists'])test('genuine nondispatch '+code+' clears only fresh original',async t=>{
  const f=await fixture(t,{respond:route=>route.fulfill({status:409,json:{error:code,dispatched:false}})});await f.connect();assert.equal(await f.stored(),null);assert.equal(await f.page.evaluate(()=>window.direct.hasUnresolved()),false);assert.match(await f.page.locator('#direct-state').textContent(),/rejected before dispatch/);
});
for(const error of [{error:'request_already_consumed'},{error:'request_already_consumed',dispatched:false},{error:'direct_unavailable'},{error:'new_unknown_code',dispatched:false}])test('unproven or consumed rejection retains original '+JSON.stringify(error),async t=>{
  const f=await fixture(t,{respond:route=>route.fulfill({status:409,json:error})});await f.connect();assert.ok((await f.stored()).original);assert.equal(await f.page.evaluate(()=>window.direct.hasUnresolved()),true);
});
test('storage unavailable blocks connection mutations before HTTP',async t=>{
  const f=await fixture(t,{storageFailure:true});assert.equal(await f.page.locator('#direct-connect-confirm').isDisabled(),true);assert.equal(await f.page.evaluate(()=>window.direct.hasUnresolved()),true);assert.match(await f.page.locator('#direct-state').textContent(),/storage unavailable/i);assert.equal(f.calls.length,0);
});
test('private fields never enter sanitized storage or latest result',async t=>{
  const f=await fixture(t,{respond:(route,body,value)=>{Object.assign(value.kioskDirectConnection,{endpoint:'https://private.invalid/',sessionId:'remote-secret',generation:'secret-generation',key:'secret-key',message:'private-message',staging:['private-name']});return route.fulfill({json:value});}});await f.connect();const data=await f.page.evaluate(key=>({stored:sessionStorage.getItem(key),latest:window.latest}),storageKey);assert.doesNotMatch(JSON.stringify(data),/private|remote-secret|secret-key|secret-generation|staging/);
});
test('malformed saved frame is sanitized and blocks instead of erasing uncertainty',async t=>{
  const f=await fixture(t,{saved:'{"original":{"key":"private-credential"}}'});assert.equal(await f.page.evaluate(()=>window.direct.hasUnresolved()),true);assert.deepEqual(await f.stored(),{original:null,connection:null,damaged:true});assert.equal(await f.page.locator('#direct-connect-confirm').isDisabled(),true);assert.equal(f.calls.length,0);
});
test('mobile card wraps full identities, renders target literally, and contains no additional Direct commands',async t=>{
  const f=await fixture(t,{width:390,state:{selected:{serial:'<img src=x onerror=alert(1)>',isReady:false,isWifiConnection:false}}});assert.equal(await f.page.locator('#direct-target img').count(),0);assert.equal(await f.page.locator('#direct-target').textContent(),'<img src=x onerror=alert(1)>');await f.update({selected:{serial:'QUEST-A',isReady:true,isWifiConnection:false}});await f.connect();
  assert.equal(await f.page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth),true);const labels=await f.page.locator('#direct-connection-view button').allTextContents();assert.doesNotMatch(labels.join(' '),/Invoke|install|pair|launch|file transfer/i);
});
test('real DateTimeOffset UTC offset timestamps bind connect status and close without changing stored bytes',async t=>{
  const f=await fixture(t,{respond:(route,body,value)=>{value.kioskDirectConnection.expiresAtUtc=value.kioskDirectConnection.expiresAtUtc.replace('Z','+00:00');if(value.kioskDirectConnection.status)value.kioskDirectConnection.status.observedAtUtc=value.kioskDirectConnection.status.observedAtUtc.replace('Z','+00:00');return route.fulfill({json:value});}});await f.connect();assert.equal((await f.stored()).original,null);assert.match((await f.stored()).connection.expiresAtUtc,/\+00:00$/);await f.read();assert.match(await f.page.locator('#direct-status-state').textContent(),/Signed status read/);await f.close();assert.equal(await f.stored(),null);
});
for(const change of ['target','channel','auth','host'])test('late connect '+change+' reply cannot project until explicit original GET',async t=>{
  let release;const received=new Promise(resolve=>release=resolve);let held;
  const f=await fixture(t,{respond:async(route,body,value)=>{held=()=>route.fulfill({json:value});release();}});
  await f.page.locator('#direct-connect-confirm').check();await f.page.locator('#direct-connect').click();await received;
  if(change==='target')await f.update({selected:{serial:'QUEST-B',isReady:true,isWifiConnection:false}});
  if(change==='channel')await f.page.evaluate(()=>{document.getElementById('direct-channel').value='labs';document.getElementById('direct-channel').dispatchEvent(new Event('change'));});
  if(change==='auth'){await f.update({connected:false});await f.update({connected:true});}
  if(change==='host')await f.update({sessionId:'session-b'});
  await held();await f.page.waitForFunction(()=>!window.ctx.busy);assert.ok((await f.stored()).original);assert.equal((await f.stored()).connection,null);assert.equal(await f.page.evaluate(()=>window.direct.hasUnresolved()),true);
  if(change==='host')await f.update({sessionId:'session-a'});await f.page.locator('#direct-original-status').click();await f.page.waitForFunction(()=>!window.direct.hasUnresolved());assert.equal((await f.stored()).connection.connectionId,handle);assert.equal(f.calls.filter(c=>c.method==='POST').length,1);
});
test('late definitive rejection after target change cannot clear original',async t=>{
  let held,release;const received=new Promise(resolve=>release=resolve);const f=await fixture(t,{respond:async route=>{held=()=>route.fulfill({status:409,json:{error:'busy',dispatched:false}});release();}});
  await f.page.locator('#direct-connect-confirm').check();await f.page.locator('#direct-connect').click();await received;await f.update({selected:{serial:'QUEST-B',isReady:true,isWifiConnection:false}});await held();await f.page.waitForFunction(()=>!window.ctx.busy);assert.ok((await f.stored()).original);
});
for(const state of ['outcomeUnknown','running','failed'])test('noncompleted original '+state+' cannot be reconciled by cleanup facts',async t=>{
  const f=await fixture(t,{respond:(route,body,value)=>{value.state=state;Object.assign(value.kioskDirectConnection,{state:'cleanupUnknown',cleanup:{schema:cleanupSchema,operationId:usb(body.requestId),stage:'confirmed',listenerDisposition:'owned-stopped'}});return route.fulfill({json:value});}});await f.connect();const before=await f.stored();await f.page.locator('#direct-original-status').click();await f.page.waitForFunction(()=>window.busyCalls.length>=3);assert.deepEqual(await f.stored(),before);assert.equal(await f.page.evaluate(()=>window.direct.hasUnresolved()),true);
});
test('original metadata GET preserves an already held sibling busy flag',async t=>{
  const f=await fixture(t,{respond:route=>route.abort('failed')});await f.connect();await f.update({busy:true});assert.equal(await f.page.locator('#direct-original-status').isDisabled(),false);await f.page.locator('#direct-original-status').click();await f.page.waitForFunction(()=>!window.direct.hasUnresolved());assert.equal(await f.page.evaluate(()=>window.ctx.busy),true);assert.equal(await f.page.evaluate(()=>window.busyCalls.at(-1)),true);
});
test('external unresolved blocks connect and close while signed status remains a separate explicit read',async t=>{
  const f=await fixture(t,{state:{externalUnresolved:true}});assert.equal(await f.page.locator('#direct-connect-confirm').isDisabled(),true);await f.update({externalUnresolved:false});await f.connect();await f.update({externalUnresolved:true});assert.equal(await f.page.locator('#direct-close-confirm').isDisabled(),true);await f.read();assert.equal(f.calls[1].body.action,'kiosk.direct.status');
});
for(const field of ['target','connection','connect','usb','expiry','status'])test('crossed signed status '+field+' cannot report success',async t=>{
  const f=await fixture(t,{respond:(route,body,value)=>{if(body.action==='kiosk.direct.status'){const d=value.kioskDirectConnection;if(field==='target')d.serial='QUEST-B';if(field==='connection')d.connectionId=crypto.randomUUID();if(field==='connect')d.connectRequestId=crypto.randomUUID();if(field==='usb')d.usbOperationId='usb_'+'a'.repeat(32);if(field==='expiry')d.expiresAtUtc=new Date(Date.now()+1000).toISOString();if(field==='status')d.status=null;}return route.fulfill({json:value});}});await f.connect();const before=await f.stored();await f.read();assert.deepEqual(await f.stored(),before);assert.doesNotMatch(await f.page.locator('#direct-status-state').textContent(),/Signed status read at/);
});
test('connection card desktop/mobile screenshots retain ordinary form layout',async t=>{
  const output=path.resolve(__dirname,'../../local/direct-connection-ui-review');await fs.mkdir(output,{recursive:true});
  for(const width of [1280,390]){const f=await fixture(t,{width});await f.connect();await f.page.locator('#direct-connection-view').screenshot({path:path.join(output,'connection-'+width+'.png')});assert.equal(await f.page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth),true);}
});
for(const field of ['serial','sessionId'])test('saved '+field+' with trailing newline fails closed as malformed custody',async t=>{
  const id='b1234567-1234-1234-1234-123456789abc',ref={sessionId:'session-a',connectionId:handle,connectRequestId:id,usbOperationId:usb(id),serial:'QUEST-A',productChannel:'stable',expiresAtUtc:new Date(Date.now()+300000).toISOString()};ref[field]+='\n';
  const f=await fixture(t,{saved:{original:null,connection:ref}});assert.deepEqual(await f.stored(),{original:null,connection:null,damaged:true});assert.equal(await f.page.evaluate(()=>window.direct.hasUnresolved()),true);assert.equal(await f.page.locator('#direct-close-confirm').isDisabled(),true);assert.equal(f.calls.length,0);
});
test('local display clock crossing expiry does not invalidate an actual bound close acknowledgement',async t=>{
  const expiry=new Date(Date.now()+1800).toISOString();let held,release;const received=new Promise(resolve=>release=resolve);
  const f=await fixture(t,{respond:(route,body,value)=>{value.kioskDirectConnection.expiresAtUtc=expiry;if(body.action==='kiosk.direct.close'){held=()=>route.fulfill({json:value});release();return;}return route.fulfill({json:value});}});await f.connect();await f.page.locator('#direct-close-confirm').check();await f.page.locator('#direct-close').click();await received;
  await f.page.waitForFunction(()=>document.getElementById('direct-connection-state').textContent.includes('expired by local clock'));await held();await f.page.waitForFunction(()=>!window.ctx.busy);assert.equal(await f.stored(),null);assert.match(await f.page.locator('#direct-cleanup-state').textContent(),/Close confirmed/);
});
test('original GET crossing a same-session authentication cycle preserves uncertainty until a fresh explicit GET',async t=>{
  let held,release,first=true;const received=new Promise(resolve=>release=resolve);const f=await fixture(t,{respond:route=>route.abort('failed'),status:(route,value)=>{if(first){first=false;held=()=>route.fulfill({json:value});release();return;}return route.fulfill({json:value});}});await f.connect();const before=await f.stored();await f.page.locator('#direct-original-status').click();await received;await f.update({connected:false});await f.update({connected:true});await held();await f.page.waitForFunction(()=>window.busyCalls.length>=3);assert.deepEqual(await f.stored(),before);assert.equal(await f.page.evaluate(()=>window.direct.hasUnresolved()),true);
  await f.page.locator('#direct-original-status').click();await f.page.waitForFunction(()=>!window.direct.hasUnresolved());assert.equal((await f.stored()).connection.connectionId,handle);assert.equal(f.calls.filter(c=>c.method==='POST').length,1);
});

test('sanitized metadata getter returns an independent same-host copy and no lost custody',async t=>{
  const f=await fixture(t);await f.connect();
  const ref=await f.page.evaluate(()=>{const ref=window.direct.getConnectionReference();ref.serial='CHANGED';return window.direct.getConnectionReference();});
  assert.equal(ref.serial,'QUEST-A');assert.equal(ref.connectionId,handle);assert.equal(ref.sessionId,'session-a');
  assert.deepEqual(Object.keys(ref).sort(),['sessionId','connectRequestId','usbOperationId','serial','productChannel','connectionId','expiresAtUtc'].sort());
  await f.update({sessionId:'session-b'});assert.equal(await f.page.evaluate(()=>window.direct.getConnectionReference()),null);
});

test('cleanup exception permits only exact saved close and never new connect or lost custody',async t=>{
  const f=await fixture(t);await f.connect();await f.update({externalUnresolved:true,cleanupConnectionId:handle});
  assert.equal(await f.page.locator('#direct-close-confirm').isDisabled(),false);assert.equal(await f.page.locator('#direct-connect-confirm').isDisabled(),true);
  await f.update({cleanupConnectionId:'b1234567-1234-1234-1234-123456789abc'});assert.equal(await f.page.locator('#direct-close-confirm').isDisabled(),true);
  await f.update({cleanupConnectionId:handle,sessionId:'session-b'});assert.equal(await f.page.locator('#direct-close-confirm').isDisabled(),true);
  assert.equal(f.calls.length,1);
});

test('sanitized connection metadata remains available after expiry for cached command recovery and explicit cleanup',async t=>{
  const f=await fixture(t);await f.connect();const before=await f.page.evaluate(()=>window.direct.getConnectionReference());await f.page.evaluate(()=>{const now=Date.now();Date.now=()=>now+600000;window.direct.update();});assert.deepEqual(await f.page.evaluate(()=>window.direct.getConnectionReference()),before);assert.equal(await f.page.locator('#direct-close-confirm').isDisabled(),false);assert.equal(await f.page.locator('#direct-status').isDisabled(),true);assert.equal(f.calls.length,1);
});

test('uncertain own close hides adopted metadata and cannot receive the command cleanup exception',async t=>{
  const f=await fixture(t,{respond:(route,body,value)=>body.action==='kiosk.direct.close'?route.abort('failed'):route.fulfill({json:value})});await f.connect();await f.update({externalUnresolved:true,cleanupConnectionId:handle});await f.close();assert.equal(await f.page.evaluate(()=>window.direct.getConnectionReference()),null);assert.equal(await f.page.locator('#direct-close-confirm').isDisabled(),true);assert.ok((await f.stored()).original);assert.equal(f.calls.length,2);
});
