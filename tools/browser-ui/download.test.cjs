// Tests the shipped module and pinned SHA-256 bundle. All network routes are
// intercepted; filesystem picker/writer are mocked, with no host or device.
const test=require('node:test');
const assert=require('node:assert/strict');
const fs=require('node:fs/promises');
const path=require('node:path');
const {createHash}=require('node:crypto');
const {chromium}=require('playwright');
const root=path.resolve(__dirname,'../../src/QuestIonAbleFileManager.BrowserHost/wwwroot');
const artifactId='1'.repeat(32), abcSha='ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad';
const receipt={id:artifactId,fileName:'fixture.apk',sizeBytes:3,sha256:abcSha};
let browser;
test.before(async()=>{browser=await chromium.launch({headless:true});});
test.after(async()=>{await browser?.close();});

async function scenario(t,configuration={}){
  const context=await browser.newContext({serviceWorkers:'block'});t.after(()=>context.close());
  const page=await context.newPage(),errors=[];page.on('pageerror',error=>errors.push(error.message));
  const origin=configuration.origin||'http://127.0.0.1:18127';
  await context.route('**/*',async route=>{
    const url=new URL(route.request().url());
    if(url.origin!==origin)return route.abort('blockedbyclient');
    if(url.pathname==='/')return route.fulfill({contentType:'text/html',body:'<!doctype html><title>Verified download fixture</title><button id="save">Save</button>'});
    const name=url.pathname.slice(1);
    if(!['verified-download.js','vendor/noble-sha256.js'].includes(name))return route.fulfill({status:404,body:''});
    return route.fulfill({contentType:'text/javascript',body:await fs.readFile(path.join(root,name))});
  });
  await page.goto(origin+'/');
  await page.evaluate(async cfg=>{
    const {saveArtifact}=await import('./verified-download.js');
    const artifact=cfg.receipt||{id:'1'.repeat(32),fileName:'fixture.apk',sizeBytes:3,sha256:'ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad'};
    window.metrics={events:[],requests:[],writes:0,closed:0,aborted:0,writerCreated:0,cancelledReader:0,maxChunk:0,progress:[],downloads:[]};
    window.cancellation=new AbortController();
    const writer={write:async bytes=>{window.metrics.writes+=bytes.byteLength;window.metrics.maxChunk=Math.max(window.metrics.maxChunk,bytes.byteLength);if(cfg.abortOnWrite)window.cancellation.abort();if(cfg.writeFail)throw new Error('private native file path');if(cfg.blockWrite)await new Promise(()=>{});},
      close:async()=>{window.metrics.closed++;if(cfg.closeFail)throw new Error('private native close path');if(cfg.blockClose)await new Promise(()=>{});},
      abort:async()=>{window.metrics.aborted++;if(cfg.abortFail)throw new Error('private native abort path');if(cfg.blockAbort)await new Promise(()=>{});}};
    if(cfg.streaming!==false)window.showSaveFilePicker=()=>{
      window.metrics.events.push({step:'picker',active:navigator.userActivation.isActive});
      if(cfg.pickerReject)return Promise.reject(new DOMException('private picker detail','AbortError'));
      const handle={createWritable:async()=>{window.metrics.writerCreated++;if(cfg.blockCreate)await new Promise(()=>{});if(cfg.createDelayMilliseconds)await new Promise(resolve=>setTimeout(resolve,cfg.createDelayMilliseconds));return writer;}};
      return cfg.pickerDelayMilliseconds?new Promise(resolve=>setTimeout(()=>resolve(handle),cfg.pickerDelayMilliseconds)):Promise.resolve(handle);
    };else window.showSaveFilePicker=undefined;
    HTMLAnchorElement.prototype.click=function(){window.metrics.downloads.push({fileName:this.download,url:this.href});};
    if(cfg.noBlob)window.Blob=class{constructor(){throw new Error('Streaming must not construct Blob');}};
    window.fetch=async(url,options)=>{
      window.metrics.events.push({step:'fetch'});const headers=new Headers(options.headers);
      window.metrics.requests.push({url:String(url),method:options.method,cache:options.cache,credentials:options.credentials,redirect:options.redirect,
        sha:headers.get('X-Qfm-Artifact-Sha256'),size:headers.get('X-Qfm-Artifact-Size-Bytes')});
      if(cfg.fetchFail)throw new Error('private network diagnostic');
      const raw=cfg.text===undefined?new TextEncoder().encode('abc'):new TextEncoder().encode(cfg.text);
      const total=cfg.actualBytes===undefined?raw.length:cfg.actualBytes;
      const chunkBytes=cfg.chunkBytes||1;
      const zero=new Uint8Array(chunkBytes);let offset=0;
      const body=new ReadableStream({pull(controller){
        if(cfg.blockRead)return;
        if(offset>=total){controller.close();return;}
        if(cfg.readFail&&offset>0){controller.error(new Error('private stream diagnostic'));return;}
        const count=Math.min(chunkBytes,total-offset);
        controller.enqueue(cfg.zeros?zero.subarray(0,count):raw.slice(offset,offset+count));offset+=count;
      },cancel(){window.metrics.cancelledReader++;}});
      if(cfg.abortDuringRead)setTimeout(()=>window.cancellation.abort(),10);
      const response=new Response(body,{status:cfg.status||200,headers:{'Content-Type':'application/octet-stream',
        'Content-Length':String(artifact.sizeBytes),'X-Qfm-Artifact-Size-Bytes':String(artifact.sizeBytes),'X-Qfm-Artifact-Sha256':artifact.sha256,...cfg.responseHeaders}});
      if(cfg.responseUrl)Object.defineProperty(response,'url',{value:cfg.responseUrl});
      response.blob=()=>{throw new Error('Response.blob would bypass bounded reads');};
      response.arrayBuffer=()=>{throw new Error('Response.arrayBuffer would bypass bounded reads');};
      return response;
    };
    if(cfg.abortBeforeCall)window.cancellation.abort();
    document.querySelector('#save').addEventListener('click',()=>{
      window.outcome=null;
      saveArtifact(artifact,{url:cfg.url,headers:{Authorization:'Bearer fixture-only-download-token','X-Qfm-Session':'fixture-session',...cfg.requestHeaders},signal:window.cancellation.signal,
        maxBufferedBytes:cfg.maxBufferedBytes,timeoutMilliseconds:cfg.timeoutMilliseconds,cleanupTimeoutMilliseconds:cfg.cleanupTimeoutMilliseconds,
        onProgress:event=>{window.metrics.progress.push(event);if(cfg.abortOnSaving&&event.phase==='saving')window.cancellation.abort();if(cfg.progressThrows)throw new Error('private progress detail');}})
        .then(result=>{window.outcome={result};}).catch(error=>{window.outcome={error:{name:error.name,code:error.code,status:error.status,message:error.message,alternative:error.alternative,cleanupPending:error.cleanupPending,saveOutcomeUnknown:error.saveOutcomeUnknown}};});
    });
  },configuration);
  await page.locator('#save').click();await page.waitForFunction(()=>window.outcome,{timeout:90000});
  const result=await page.evaluate(()=>({outcome:window.outcome,...window.metrics,storageKeys:[...Object.keys(sessionStorage),...Object.keys(localStorage)]}));
  assert.deepEqual(errors,[]);assert.deepEqual(result.storageKeys,[]);
  return {page,...result};
}

test('streaming validates receipt before close and opens picker with active user gesture before fetch',async t=>{
  const value=await scenario(t);
  assert.equal(value.outcome.result.state,'saved');assert.equal(value.outcome.result.mode,'streaming');
  assert.deepEqual(value.events.map(event=>event.step),['picker','fetch']);assert.equal(value.events[0].active,true);
  assert.equal(value.closed,1);assert.equal(value.aborted,0);assert.equal(value.writes,3);
  assert.deepEqual(value.requests,[{url:`http://127.0.0.1:18127/browser/v1/artifacts/${artifactId}`,method:'GET',cache:'no-store',credentials:'omit',redirect:'error',sha:abcSha,size:'3'}]);
  assert.equal(value.progress.at(-1).phase,'complete');
});

test('pinned shipped SHA-256 supports independent known vectors and split updates',async t=>{
  const value=await scenario(t);
  const vectors=await value.page.evaluate(async()=>{const {sha256}=await import('./vendor/noble-sha256.js');const hex=bytes=>Array.from(bytes,byte=>byte.toString(16).padStart(2,'0')).join('');
    return [hex(sha256(new Uint8Array())),hex(sha256.create().update(new TextEncoder().encode('a')).update(new TextEncoder().encode('bc')).digest()),hex(sha256(new Uint8Array(1000000).fill(97)))];});
  assert.deepEqual(vectors,['e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855','ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad','cdc76e5c9914fb9281a1c7e284d73e67f1809a48a497200e046d39ccc7112cd0']);
});

test('streaming above256MiB uses bounded chunks and no Blob or arrayBuffer',async t=>{
  const size=257*1024*1024,chunk=Buffer.alloc(1024*1024),digest=createHash('sha256');for(let i=0;i<257;i++)digest.update(chunk);
  const value=await scenario(t,{receipt:{...receipt,sizeBytes:size,sha256:digest.digest('hex')},zeros:true,actualBytes:size,chunkBytes:chunk.length,noBlob:true});
  assert.equal(value.outcome.result.state,'saved');assert.equal(value.writes,size);assert.ok(value.maxChunk<=1024*1024);
  assert.equal(value.closed,1);assert.equal(value.progress.at(-1).bytesReceived,size);assert.equal(value.downloads.length,0);
});

for(const [name,cfg,code] of [
  ['short body',{text:'ab'},'artifact_size_mismatch'],['long body',{text:'abcd'},'artifact_size_mismatch'],
  ['wrong same-size bytes',{text:'abd'},'artifact_digest_mismatch'],['failed read',{readFail:true},'download_failed'],
  ['cancelled mid-write',{abortOnWrite:true},'download_cancelled'],['cancelled pending read',{blockRead:true,abortDuringRead:true},'download_cancelled'],
  ['failed write',{writeFail:true},'download_failed'],['failed close',{closeFail:true},'download_save_failed']
])test(name+' never reports completion and aborts its owned writable',async t=>{
  const value=await scenario(t,cfg);assert.equal(value.outcome.error.code,code);assert.equal(value.aborted,1);
  assert.equal(value.progress.some(event=>event.phase==='complete'),false);
  if(!cfg.closeFail)assert.equal(value.closed,0);
  else assert.equal(value.outcome.error.saveOutcomeUnknown,true);
  assert.equal(JSON.stringify(value.outcome).includes('private'),false);
});

test('abort refusal retains explicit cleanup uncertainty without masking digest failure',async t=>{
  const value=await scenario(t,{text:'abd',abortFail:true});assert.equal(value.outcome.error.code,'artifact_digest_mismatch');
  assert.equal(value.outcome.error.cleanupPending,true);assert.equal(value.closed,0);
});

test('picker rejection and pre-cancelled request never fetch or create writable',async t=>{
  for(const cfg of [{pickerReject:true},{abortBeforeCall:true}]){const value=await scenario(t,cfg);assert.equal(value.outcome.error.code,'download_cancelled');assert.equal(value.requests.length,0);assert.equal(value.writerCreated,0);}
});

for(const url of ['https://attacker.example/browser/v1/artifacts/'+artifactId,'/browser/v1/artifacts/'+'2'.repeat(32),'/browser/v1/artifacts/'+artifactId+'?token=secret','/browser/v1/artifacts/'+artifactId+'#fragment','http://user@127.0.0.1:18127/browser/v1/artifacts/'+artifactId])
test('rejects URL outside exact artifact scope: '+url,async t=>{const value=await scenario(t,{url});assert.equal(value.outcome.error.code,'artifact_url_invalid');assert.equal(value.requests.length,0);assert.equal(value.events.length,0);});

test('public page origin cannot send supplied credential headers',async t=>{
  const value=await scenario(t,{origin:'https://public.example'});assert.equal(value.outcome.error.code,'artifact_url_invalid');assert.equal(value.requests.length,0);
});

for(const cfg of [{responseHeaders:{'X-Qfm-Artifact-Sha256':'0'.repeat(64)}},{responseHeaders:{'X-Qfm-Artifact-Size-Bytes':'4'}},{responseHeaders:{'Content-Length':'4'}},{responseUrl:'http://127.0.0.1:18127/other'}])
test('rejects mismatched response binding before creating output '+JSON.stringify(cfg),async t=>{const value=await scenario(t,cfg);assert.equal(value.outcome.error.code,'artifact_response_invalid');assert.equal(value.writerCreated,0);});

test('buffered fallback verifies small bytes and reports download initiation, not saved file',async t=>{
  const value=await scenario(t,{streaming:false});assert.equal(value.outcome.result.state,'downloadStarted');assert.equal(value.outcome.result.mode,'buffered');
  assert.equal(value.downloads[0].fileName,'fixture.apk');assert.equal(value.writerCreated,0);assert.equal(value.progress.at(-1).phase,'downloadStarted');
  const wrong=await scenario(t,{streaming:false,text:'abd'});assert.equal(wrong.outcome.error.code,'artifact_digest_mismatch');assert.equal(wrong.downloads.length,0);
});

test('empty artifact is verified in streaming and buffered modes',async t=>{
  for(const streaming of [true,false]){
    const value=await scenario(t,{streaming,text:'',receipt:{...receipt,sizeBytes:0,sha256:'e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855'}});
    assert.ok(value.outcome.result);assert.equal(value.writes,0);
    assert.equal(value.outcome.result.state,streaming?'saved':'downloadStarted');
  }
});

test('host rejection reports safe status without creating output or reading diagnostic body',async t=>{
  const value=await scenario(t,{status:401});assert.equal(value.outcome.error.code,'download_rejected');assert.equal(value.outcome.error.status,401);
  assert.equal(value.writerCreated,0);assert.equal(value.closed,0);
});

test('unsupported large download explains streaming alternative without issuing request',async t=>{
  const value=await scenario(t,{streaming:false,receipt:{...receipt,sizeBytes:257*1024*1024}});
  assert.equal(value.outcome.error.code,'streaming_save_unavailable');assert.match(value.outcome.error.alternative,/Chrome|Edge/);assert.match(value.outcome.error.alternative,/CLI/);
  assert.equal(value.requests.length,0);
});

test('receipt path, oversize and mismatched requested hash reject before picker or network',async t=>{
  for(const cfg of [{receipt:{...receipt,fileName:'../private.apk'}},{receipt:{...receipt,sizeBytes:1024*1024*1024+1}},{requestHeaders:{'X-Qfm-Artifact-Sha256':'0'.repeat(64)}}]){
    const value=await scenario(t,cfg);assert.ok(value.outcome.error);assert.equal(value.events.length,0);assert.equal(value.requests.length,0);
  }
});

test('progress callback failure cannot interrupt verified save or leak private diagnostics',async t=>{
  const value=await scenario(t,{progressThrows:true});assert.equal(value.outcome.result.state,'saved');assert.equal(value.aborted,0);
});

test('finite lifetime cancels stalled receive after picker while leaving human chooser untimed',async t=>{
  const stalled=await scenario(t,{blockRead:true,timeoutMilliseconds:25,cleanupTimeoutMilliseconds:25});
  assert.equal(stalled.outcome.error.code,'download_timeout');assert.equal(stalled.aborted,1);assert.equal(stalled.closed,0);
  const chooser=await scenario(t,{pickerDelayMilliseconds:80,timeoutMilliseconds:25});
  assert.equal(chooser.outcome.result.state,'saved');
});

for(const cfg of [{blockCreate:true},{blockWrite:true},{blockClose:true,blockAbort:true}])test('deadline bounds pending native promises '+JSON.stringify(cfg),async t=>{
  const start=Date.now();const value=await scenario(t,{...cfg,timeoutMilliseconds:25,cleanupTimeoutMilliseconds:25});
  assert.equal(value.outcome.error.code,'download_timeout');assert.equal(value.outcome.error.cleanupPending,true);
  assert.ok(Date.now()-start<5000);assert.equal(value.progress.some(event=>event.phase==='complete'),false);
  if(cfg.blockClose)assert.equal(value.outcome.error.saveOutcomeUnknown,true);
});

test('cancellation from saving callback is observed before attempting close',async t=>{
  const value=await scenario(t,{abortOnSaving:true});assert.equal(value.outcome.error.code,'download_cancelled');
  assert.equal(value.closed,0);assert.equal(value.aborted,1);assert.equal(value.outcome.error.saveOutcomeUnknown,undefined);
});

test('late writable creation after timeout is aborted without later write or close',async t=>{
  const value=await scenario(t,{createDelayMilliseconds:100,timeoutMilliseconds:25,cleanupTimeoutMilliseconds:25});
  assert.equal(value.outcome.error.code,'download_timeout');assert.equal(value.outcome.error.cleanupPending,true);
  await value.page.waitForFunction(()=>window.metrics.aborted===1);
  const late=await value.page.evaluate(()=>({writes:window.metrics.writes,closed:window.metrics.closed,aborted:window.metrics.aborted}));
  assert.deepEqual(late,{writes:0,closed:0,aborted:1});
});
