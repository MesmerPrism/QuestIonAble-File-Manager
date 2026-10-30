// Shipped module with intercepted synthetic acknowledgements only; no host/device.
const test=require('node:test'),assert=require('node:assert/strict'),fs=require('node:fs/promises'),path=require('node:path');
const {chromium}=require('playwright');
const root=path.resolve(__dirname,'../../src/QuestIonAbleFileManager.BrowserHost/wwwroot'),origin='http://127.0.0.1:18141',key='qfm-browser-direct-command-v1';
const commands=['status','show-controls','show-apps','check-setup-helper','set-launch-requirement','cancel-pending-launch','launch-normal','launch-kiosk','reload'];
const logical=id=>'kiosk_'+id.replaceAll('-','');
const connection=()=>({sessionId:'session-a',connectionId:'a1234567-1234-4234-8234-123456789abc',connectRequestId:'b1234567-1234-4234-8234-123456789abc',usbOperationId:'usb_b1234567123442348234123456789abc',serial:'USB_TARGET',productChannel:'stable',expiresAtUtc:new Date(Date.now()+300000).toISOString()});
function typed(command,id,value){const textValue=value; if(['set-search','filter-tag','add-tag','remove-tag'].includes(command))value='any'; return {...(['set-search','filter-tag'].includes(command)?{valueMatches:true,...(command==='set-search'?{search:textValue??''}:{tagFilter:textValue??''})}:{}),...(['add-tag','remove-tag'].includes(command)?{requestedTagPresent:command==='add-tag'}:{}),schema:'rusty.kiosk.cli_result.v1',requestId:id,command,accepted:true,completed:true,controlsOpen:command==='show-controls',selectedKey:'selected-key',selectedPackage:'example.selected.app',selectedInstalled:true,selectedLaunchable:true,selectedLaunchRequirement:value||'any',pendingRequirementLaunch:false,guardArmed:command==='launch-kiosk',setupHelperInstalled:true,setupHelperReady:true,...(['enable-wifi-adb-after-boot','disable-wifi-adb-after-boot'].includes(command)?{requestWifiAdbAfterBoot:command==='enable-wifi-adb-after-boot'}:{}),...(['enable-accessibility','disable-accessibility'].includes(command)?{accessibilityEnabled:command==='enable-accessibility'}:{}),...(['passthrough-natural','passthrough-contour'].includes(command)?{systemPassthroughEnabled:true,passthroughStyle:command==='passthrough-natural'?'natural':'contour-lut'}:{}),...(command==='passthrough-contour'?{passthroughLutApplied:true}:{}),...(['request-wifi-adb','disable-wifi-adb'].includes(command)?{wifiAdbEnabled:command==='request-wifi-adb'}:{}),selectedEntry:{key:'selected-key',launchRequirement:value||'any'}};}
function outcome(body,scope,records){
  const invoke=body.action==='kiosk.direct.invoke',cancel=body.action==='kiosk.direct.request-cancel';
  const original=invoke?{...scope,originalRequestId:body.requestId,logicalRequestId:logical(body.requestId),command:body.command,value:body.value}:records.get(body.originalRequestId).kioskDirectCommand;
  const operation=invoke?'invoke':cancel?'requestCancel':body.action.endsWith('request-status')?'requestStatus':'requestResult';
  const d={connectionId:original.connectionId,connectRequestId:original.connectRequestId,usbOperationId:original.usbOperationId,serial:original.serial,productChannel:original.productChannel,operation,originalRequestId:original.originalRequestId,logicalRequestId:original.logicalRequestId,command:original.command,value:original.value,originalEffect:operation==='invoke'||operation==='requestResult'?'confirmed':'unknown',reconcilesOriginal:operation==='invoke'||operation==='requestResult',coreReceipt:{actionId:'action_'+body.requestId.replaceAll('-',''),action:cancel?'cancelRequest':operation,requestId:original.logicalRequestId,stage:cancel?'cancelled':'confirmed'},lifecycle:operation==='requestStatus'||cancel?{requestId:original.logicalRequestId,operationState:cancel?'cancelled':'confirmed',accepted:true,completed:true,enqueuedAtMs:1000,expiresAtMs:2000}:null,typedResult:operation==='invoke'||operation==='requestResult'?typed(original.command,original.logicalRequestId,original.value):null,cancellationAcknowledged:cancel};
  return {requestId:body.requestId,action:body.action,state:'completed',result:null,mutation:null,kioskDirectCommand:d};
}
let browser;test.before(async()=>{browser=await chromium.launch({headless:true});});test.after(async()=>browser?.close());
async function fixture(t,{respond,status,state={},saved,width=1280,storageFailure=false}={}){
  const context=await browser.newContext({serviceWorkers:'block',viewport:{width,height:1100}});t.after(()=>context.close());const page=await context.newPage(),calls=[],records=new Map(),errors=[],scope=connection();
  page.setDefaultTimeout(6000);page.setDefaultNavigationTimeout(6000);
  page.on('pageerror',error=>errors.push(error.message));
  if(saved!==undefined)await context.addInitScript(({key,saved})=>{if(!sessionStorage.getItem('fixture-seeded')){sessionStorage.setItem(key,typeof saved==='string'?saved:JSON.stringify(saved));sessionStorage.setItem('fixture-seeded','yes');}},{key,saved});
  if(storageFailure)await context.addInitScript(()=>{Storage.prototype.setItem=function(){throw new Error('private-storage-error');};});
  await context.route('**/*',async route=>{
    const url=new URL(route.request().url());if(url.origin!==origin)return route.abort('blockedbyclient');
    if(url.pathname.startsWith('/browser/')){
      const method=route.request().method(),body=method==='POST'?route.request().postDataJSON():null,call={method,path:url.pathname,body};calls.push(call);
      if(body){const value=outcome(body,scope,records);records.set(body.requestId,value);call.saved=await page.evaluate(key=>JSON.parse(sessionStorage.getItem(key)||'null'),key);return respond?respond(route,body,value):route.fulfill({json:value});}
      const value=records.get(url.pathname.split('/').at(-1));return status?status(route,value):route.fulfill({json:value});
    }
    if(url.pathname==='/command-fixture.js')return route.fulfill({contentType:'text/javascript',body:`import {createDirectCommandView} from './direct-command-view.js';
      window.ctx=${JSON.stringify({sessionId:'session-a',connected:true,busy:false,authGeneration:1,actions:['kiosk.direct.invoke','kiosk.direct.request-status','kiosk.direct.request-result','kiosk.direct.request-cancel'],connection:scope,externalUnresolved:false,...state})};window.ctx.actions=new Set(window.ctx.actions);window.busyCalls=[];window.contextReads=0;
      window.view=createDirectCommandView({getContext:()=>{window.contextReads++;return window.ctx;},setBusy:value=>{window.busyCalls.push(value);window.ctx.busy=value;window.siblingBlocked=Boolean(value||window.view?.hasUnresolved());window.view?.update();},showResult:(value,summary)=>window.latest={value,summary},api:async(path,options)=>{const r=await fetch(path,{...options,cache:'no-store'});const value=await r.json();if(!r.ok)throw Object.assign(new Error(value.error),{dispatched:value.dispatched,status:r.status});return value;}});window.view.update();window.fixtureReady=true;`});
    const name=url.pathname==='/'?'index.html':url.pathname.slice(1);if(!['index.html','styles.css','direct-command-view.js'].includes(name))return route.fulfill({status:404,body:''});
    // Exercise the shipped closed menu without injecting command options.
    let bytes=await fs.readFile(path.join(root,name));if(name==='index.html')bytes=Buffer.from(bytes.toString().replace(/<script[^>]*src="\.\/pwa.js"[^>]*><\/script>/,'').replace(/<script[^>]*src="\.\/app.js"[^>]*><\/script>/,'<script type="module" src="./command-fixture.js"></script>'));
    return route.fulfill({contentType:name.endsWith('.js')?'text/javascript':name.endsWith('.css')?'text/css':'text/html',body:bytes});
  });
  await page.goto(origin);await page.waitForFunction(()=>window.fixtureReady);
  const update=patch=>page.evaluate(patch=>{Object.assign(window.ctx,patch);if(Array.isArray(window.ctx.actions))window.ctx.actions=new Set(window.ctx.actions);window.view.update();},patch);
  const invoke=async(command='status',value='any')=>{await page.locator('#direct-command-command').selectOption(command);if(command==='set-launch-requirement')await page.locator('#direct-command-requirement').selectOption(value);if(['set-search','filter-tag','add-tag','remove-tag'].includes(command))await page.locator('#direct-command-text').fill(value??'');await page.locator('#direct-command-invoke-confirm').check();await page.locator('#direct-command-invoke').click();await page.waitForFunction(()=>!window.ctx.busy);};
  const recovery=async kind=>{await page.locator('#direct-command-'+kind).click();await page.waitForFunction(()=>!window.ctx.busy);};
  const cancel=async()=>{await page.locator('#direct-command-cancel-confirm').check();await page.locator('#direct-command-cancel').click();await page.waitForFunction(()=>!window.ctx.busy);};
  const stored=()=>page.evaluate(key=>JSON.parse(sessionStorage.getItem(key)||'null'),key);
  t.after(()=>assert.deepEqual(errors,[],'No uncaught command UI errors'));return {page,calls,records,scope,update,invoke,recovery,cancel,stored};
}

const textCommands=['set-search','filter-tag','add-tag','remove-tag'];
test('shipped Direct menu exposes all four existing text commands',async t=>{const f=await fixture(t);assert.equal(await f.page.locator('#direct-command-command option').evaluateAll(options=>options.filter(option=>['set-search','filter-tag','add-tag','remove-tag'].includes(option.value)).length),4);assert.equal(f.calls.length,0);});

for(const [command,raw,canonical] of [
  ['set-search','  Headset Search  ','Headset Search'],['filter-tag',' \u0085\u2000 ΚΌΣΜΟΣ \u3000','ΚΌΣΜΟΣ'],
  ['add-tag','  wearable TAG ','wearable TAG'],['remove-tag','\u0085a\u0085','a'],
  ['set-search','\uFEFFdata\uFEFF','\uFEFFdata\uFEFF'],['add-tag','<img src=x onerror=window.injected=true>','<img src=x onerror=window.injected=true>'],
  ['set-search','a\tb\nc\u0001d','a\tb\nc\u0001d'],['filter-tag','😀'.repeat(80),'😀'.repeat(80)],
  ['set-search','',null],['set-search','\u0085 \u2029',null],['filter-tag','',null],['filter-tag','\u3000',null]
])test(command+' canonical wire '+JSON.stringify(raw),async t=>{
  const f=await fixture(t);await f.page.locator('#direct-command-command').selectOption(command);await f.page.locator('#direct-command-text').fill(raw);
  const review=await f.page.locator('#direct-command-text-review').textContent();assert.ok(review.includes(canonical===null?'null':JSON.stringify(canonical)));assert.equal(f.calls.length,0);
  await f.page.locator('#direct-command-invoke-confirm').check();await f.page.locator('#direct-command-invoke').click();await f.page.waitForFunction(()=>!window.ctx.busy);
  assert.equal(f.calls[0].body.value,canonical);assert.equal(f.calls[0].saved.original.value,canonical);assert.equal(await f.stored(),null);assert.equal(f.calls.length,1);
  assert.equal(await f.page.evaluate(()=>window.injected),undefined);assert.equal(await f.page.locator('#direct-command-text-review img').count(),0);
});
for(const [command,raw] of [['add-tag',''],['remove-tag',' \u0085'],['set-search','a'.repeat(161)],['filter-tag','😀'.repeat(81)],['add-tag','\ud800'],['set-search','\udc00'],['filter-tag','x'.repeat(4097)]])test(command+' invalid raw input retains disabled admission '+JSON.stringify(raw).slice(0,80),async t=>{
  const f=await fixture(t);await f.page.locator('#direct-command-command').selectOption(command);await f.page.locator('#direct-command-text').evaluate((element,raw)=>{element.value=raw;element.dispatchEvent(new Event('input',{bubbles:true}));},raw);
  assert.equal(await f.page.locator('#direct-command-invoke-confirm').isDisabled(),true);await f.page.locator('#direct-command-invoke').evaluate(button=>{button.disabled=false;button.click();});assert.equal(f.calls.length,0);assert.equal(await f.stored(),null);
});
test('the complete .NET Trim whitespace set clears, while FEFF stays data and internal whitespace stays literal',async t=>{
  const f=await fixture(t),space='\t\n\v\f\r \u0085\u00a0\u1680\u2000\u2001\u2002\u2003\u2004\u2005\u2006\u2007\u2008\u2009\u200a\u2028\u2029\u202f\u205f\u3000';
  await f.invoke('set-search',space);assert.equal(f.calls[0].body.value,null);
  await f.invoke('set-search','\uFEFF');assert.equal(f.calls[1].body.value,'\uFEFF');
  await f.invoke('add-tag','a\u0085b\u0000c');assert.equal(f.calls[2].body.value,'a\u0085b\u0000c');
});
for(const command of textCommands)test(command+' approval is bound to raw text even when canonical value stays equal',async t=>{
  const f=await fixture(t);await f.page.locator('#direct-command-command').selectOption(command);await f.page.locator('#direct-command-text').fill('a');await f.page.locator('#direct-command-invoke-confirm').check();await f.page.locator('#direct-command-text').fill(' a ');assert.equal(await f.page.locator('#direct-command-invoke-confirm').isChecked(),false);assert.equal(f.calls.length,0);
  await f.page.locator('#direct-command-invoke-confirm').check();await f.page.locator('#direct-command-command').selectOption('status');assert.equal(await f.page.locator('#direct-command-invoke-confirm').isChecked(),false);
});
test('headset controls and execution-time selection disclose their separate scope',async t=>{
  const f=await fixture(t);for(const command of ['set-search','filter-tag']){await f.page.locator('#direct-command-command').selectOption(command);assert.match(await f.page.locator('#direct-command-boundary').textContent(),/HEADSET/);assert.match(await f.page.locator('#direct-command-boundary').textContent(),/browser.*local/i);}
  for(const command of ['add-tag','remove-tag']){await f.page.locator('#direct-command-command').selectOption(command);assert.match(await f.page.locator('#direct-command-boundary').textContent(),/when executed/);assert.match(await f.page.locator('#direct-command-boundary').textContent(),/wearer or client/);}
  assert.equal(f.calls.length,0);
});
const crossedText={
  request:v=>v.requestId='d1234567-1234-4234-8234-123456789abc',action:v=>v.action='kiosk.direct.request-result',
  connection:v=>v.kioskDirectCommand.connectionId='d1234567-1234-4234-8234-123456789abc',connect:v=>v.kioskDirectCommand.connectRequestId='d1234567-1234-4234-8234-123456789abc',
  usb:v=>v.kioskDirectCommand.usbOperationId='usb_'+'a'.repeat(32),serial:v=>v.kioskDirectCommand.serial='OTHER',channel:v=>v.kioskDirectCommand.productChannel='labs',
  original:v=>v.kioskDirectCommand.originalRequestId='d1234567-1234-4234-8234-123456789abc',logical:v=>v.kioskDirectCommand.logicalRequestId='kiosk_'+'a'.repeat(32),
  command:v=>v.kioskDirectCommand.command='status',value:v=>v.kioskDirectCommand.value='other',operation:v=>v.kioskDirectCommand.operation='requestResult',
  receiptMissing:v=>v.kioskDirectCommand.coreReceipt=null,receiptAction:v=>v.kioskDirectCommand.coreReceipt.action='requestResult',receiptId:v=>v.kioskDirectCommand.coreReceipt.requestId='kiosk_'+'a'.repeat(32),
  receiptStage:v=>v.kioskDirectCommand.coreReceipt.stage='pending',receiptActionId:v=>v.kioskDirectCommand.coreReceipt.actionId+='\n',
  schema:v=>v.kioskDirectCommand.typedResult.schema='other',typedId:v=>v.kioskDirectCommand.typedResult.requestId='other',typedCommand:v=>v.kioskDirectCommand.typedResult.command='status',
  accepted:v=>v.kioskDirectCommand.typedResult.accepted=false,completed:v=>v.kioskDirectCommand.typedResult.completed=false,
  unconfirmed:v=>v.kioskDirectCommand.originalEffect='pending',unreconciled:v=>v.kioskDirectCommand.reconcilesOriginal=false,
  resultMissing:v=>v.kioskDirectCommand.typedResult=null,envelopePending:v=>v.state='pending'
};
for(const command of textCommands)for(const [field,change] of Object.entries(crossedText))test(command+' crossed '+field+' cannot clear custody',async t=>{
  const f=await fixture(t,{respond:(route,body,value)=>{change(value);return route.fulfill({json:value});}});await f.invoke(command,'value');assert.ok((await f.stored()).original);assert.equal(await f.page.evaluate(()=>window.view.hasUnresolved()),true);assert.equal(f.calls.length,1);
});
for(const [command,field,value] of [
  ['set-search','search','VALUE'],['set-search','search','value\u0000'],['set-search','search','x'.repeat(161)],['set-search','search','\ud800'],['set-search','search',null],['set-search','valueMatches',false],['set-search','valueMatches',null],
  ['filter-tag','tagFilter','x'.repeat(161)],['filter-tag','tagFilter','\udc00'],['filter-tag','valueMatches',false],['filter-tag','valueMatches',null],
  ['add-tag','requestedTagPresent',false],['add-tag','requestedTagPresent',null],['remove-tag','requestedTagPresent',true],['remove-tag','requestedTagPresent',null],
  ['add-tag','selectedEntry',null],['remove-tag','selectedEntry',null],['add-tag','selectedEntry',[]],['remove-tag','selectedEntry',[{key:'selected-key',launchRequirement:'any'},{key:'selected-key',launchRequirement:'any'}]],['add-tag','selectedEntry',{key:'wrong-key',launchRequirement:'any'}],['remove-tag','selectedKey','wrong-key']
])test(command+' missing contrary malformed proof '+field+' '+JSON.stringify(value).slice(0,60),async t=>{
  const f=await fixture(t,{respond:(route,body,result)=>{result.kioskDirectCommand.typedResult[field]=value;return route.fulfill({json:result});}});await f.invoke(command,'value');assert.ok((await f.stored()).original);assert.equal(f.calls.length,1);
});
for(const command of ['set-search','filter-tag'])test(command+' missing typed matching field retains original',async t=>{
  const f=await fixture(t,{respond:(route,body,result)=>{delete result.kioskDirectCommand.typedResult.valueMatches;return route.fulfill({json:result});}});await f.invoke(command,'value');assert.ok((await f.stored()).original);
});
test('filter accepts Core non-ASCII matching proof without a JavaScript folding rule',async t=>{
  const f=await fixture(t,{respond:(route,body,value)=>{value.kioskDirectCommand.typedResult.tagFilter='Σ';return route.fulfill({json:value});}});await f.invoke('filter-tag','ς');assert.equal(await f.stored(),null);assert.equal((await f.page.evaluate(()=>window.latest)).value.typedResult.tagFilter,'Σ');
});
for(const command of ['add-tag','remove-tag'])test(command+' reports returned execution-time selection and server membership only',async t=>{
  const f=await fixture(t,{respond:(route,body,value)=>{const result=value.kioskDirectCommand.typedResult;result.selectedKey='selection-changed';result.selectedEntry={key:'selection-changed',launchRequirement:'wifi-off'};return route.fulfill({json:value});}});await f.invoke(command,'Σ');assert.equal(await f.stored(),null);assert.match(await f.page.locator('#direct-command-facts').textContent(),/selection-changed/);assert.match(await f.page.locator('#direct-command-facts').textContent(),command==='add-tag'?/tag present/:/tag absent/);assert.doesNotMatch(await f.page.locator('#direct-command-facts').textContent(),/ready|previous|launchable|installed/i);
});
for(const command of textCommands)for(const [name,patch] of Object.entries({auth:{authGeneration:2},connection:{connection:{...connection(),connectionId:'d1234567-1234-4234-8234-123456789abc'}},disconnect:{connected:false},value:null}))test(command+' late '+name+' reply retains original and only explicit cached recovery clears it',async t=>{
  let held,release;const received=new Promise(resolve=>release=resolve);const f=await fixture(t,{respond:(route,body,value)=>{held=()=>route.fulfill({json:value});release();}});const running=f.invoke(command,'value');await received;const before=await f.stored();
  if(name==='value')await f.page.locator('#direct-command-text').fill('changed');else await f.update(patch);await held();await running;assert.deepEqual(await f.stored(),before);
  await f.update({connected:true});await f.page.locator('#direct-command-original-status').click();await f.page.waitForFunction(()=>!window.view.hasUnresolved());assert.equal(f.calls.filter(call=>call.body?.action==='kiosk.direct.invoke').length,1);
});
for(const command of textCommands)test(command+' persists original across reload and lifecycle/cancellation cannot reconcile effects',async t=>{
  const f=await fixture(t,{respond:(route,body,value)=>body.action==='kiosk.direct.invoke'?route.abort('failed'):route.fulfill({json:value})});await f.invoke(command,'\uFEFFa\tb\u0085c');const before=await f.stored();await f.page.reload();await f.page.waitForFunction(()=>window.fixtureReady);assert.deepEqual(await f.stored(),before);assert.equal(f.calls.length,1);
  await f.recovery('request-status');assert.deepEqual(await f.stored(),before);await f.cancel();assert.ok((await f.stored()).original);assert.equal((await f.stored()).cancellation,null);
  const cached=f.records.get(before.original.requestId).kioskDirectCommand;cached.originalEffect='unknown';cached.reconcilesOriginal=false;cached.typedResult=null;await f.update({connection:{...f.scope,expiresAtUtc:new Date(Date.now()-1000).toISOString()}});assert.equal(await f.page.locator('#direct-command-request-result').isDisabled(),true);await f.page.locator('#direct-command-original-status').click();await f.page.waitForFunction(()=>!document.getElementById('direct-command-original-status').disabled);assert.ok((await f.stored()).original);
  await f.update({connection:f.scope});await f.recovery('request-result');assert.equal(await f.stored(),null);assert.equal(f.calls.filter(call=>call.body?.action==='kiosk.direct.invoke').length,1);
});
test('old eighteen typed result snapshots omit all four new metadata fields',async t=>{
  const old=['status','show-controls','show-apps','check-setup-helper','set-launch-requirement','cancel-pending-launch','launch-normal','launch-kiosk','reload','exit-meta-home','enable-wifi-adb-after-boot','disable-wifi-adb-after-boot','enable-accessibility','disable-accessibility','passthrough-natural','passthrough-contour','request-wifi-adb','disable-wifi-adb'];const f=await fixture(t);
  for(const command of old){await f.invoke(command,'any');assert.equal(await f.stored(),null);const typed=(await f.page.evaluate(()=>window.latest)).value.typedResult;for(const field of ['search','tagFilter','valueMatches','requestedTagPresent'])assert.equal(Object.hasOwn(typed,field),false);}
});

for(const [command,value] of [['set-search',''],['filter-tag',' value '],['add-tag',null],['remove-tag',42],['set-search','x'.repeat(161)],['filter-tag','\ud800'],['add-tag','\u0085'],['set-search',undefined]])test('noncanonical saved '+command+' value '+JSON.stringify(value)+' cannot acquire recovery authority',async t=>{
  const reference={...connection(),requestId:'c1234567-1234-4234-8234-123456789abc',action:'kiosk.direct.invoke',logicalRequestId:'kiosk_c1234567123442348234123456789abc',command,value};delete reference.expiresAtUtc;
  const f=await fixture(t,{saved:{original:reference,cancellation:null}});assert.deepEqual(await f.stored(),{original:null,cancellation:null,damaged:true});assert.equal(f.calls.length,0);assert.equal(await f.page.evaluate(()=>window.view.hasUnresolved()),true);
});
test('desktop and mobile text review stays literal and contained',async t=>{
  const output=path.resolve(__dirname,'../../local/browser-direct-text-proof');await fs.mkdir(output,{recursive:true});
  for(const width of [1280,390]){const f=await fixture(t,{width});await f.page.locator('#direct-command-command').selectOption('add-tag');await f.page.locator('#direct-command-text').fill('<img src=x onerror=window.injected=true>\t'+'長'.repeat(100));
    assert.equal(await f.page.locator('#direct-command-text').getAttribute('maxlength'),'4096');assert.equal(await f.page.locator('#direct-command-text-review img').count(),0);assert.match(await f.page.locator('#direct-command-text-review').textContent(),/\\t/);assert.equal(await f.page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth),true);await f.page.locator('#direct-command-view').screenshot({path:path.join(output,'text-'+width+'.png')});}
});

for(const command of textCommands)test(command+' incomplete optional proof renders actual receipt without clearing custody',async t=>{
  const f=await fixture(t,{respond:(route,body,value)=>{const proof=value.kioskDirectCommand.typedResult;if(command==='set-search')proof.search=null;if(command==='filter-tag')delete proof.tagFilter;if(['add-tag','remove-tag'].includes(command)){delete proof.requestedTagPresent;proof.selectedEntry=null;}return route.fulfill({json:value});}});await f.invoke(command,'value');assert.ok((await f.stored()).original);assert.equal((await f.page.evaluate(()=>window.latest)).value.coreReceipt.stage,'confirmed');assert.match(await f.page.locator('#direct-command-state').textContent(),/actual Core stage confirmed/);if(['add-tag','remove-tag'].includes(command))assert.match(await f.page.locator('#direct-command-facts').textContent(),/tag unknown/);
});
test('missing or null reported search cannot confirm an explicitly requested clear',async t=>{
  for(const reported of [undefined,null]){const f=await fixture(t,{respond:(route,body,value)=>{const proof=value.kioskDirectCommand.typedResult;if(reported===undefined)delete proof.search;else proof.search=null;return route.fulfill({json:value});}});await f.invoke('set-search','');assert.ok((await f.stored()).original);assert.equal((await f.page.evaluate(()=>window.latest)).value.coreReceipt.stage,'confirmed');}
});
test('explicit null filter proof stays unknown; server must project empty-filter equivalence',async t=>{
  const f=await fixture(t,{respond:(route,body,value)=>{value.kioskDirectCommand.typedResult.tagFilter=null;return route.fulfill({json:value});}});await f.invoke('filter-tag','');assert.ok((await f.stored()).original);assert.equal((await f.page.evaluate(()=>window.latest)).value.coreReceipt.stage,'confirmed');
});
