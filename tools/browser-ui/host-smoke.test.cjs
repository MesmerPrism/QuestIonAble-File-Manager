// Opt-in actual middleware/CSP/PWA smoke. No discovery or device command is sent.
const test=require('node:test'), assert=require('node:assert/strict');
const {spawn}=require('node:child_process'), {randomBytes}=require('node:crypto');
const {chromium}=require('playwright');
test('actual host serves authenticated session and installable offline shell under its CSP',
  {skip:!process.env.QFM_BROWSER_HOST_DLL},async t=>{
    const port=Number(process.env.QFM_BROWSER_UI_PORT);
    assert.ok(Number.isInteger(port)&&port>1024&&port<=65535,'Reserve an explicit port');
    assert.ok(process.env.QFM_BROWSER_DOTNET,'Explicit SDK/runtime executable required');
    const origin=`http://127.0.0.1:${port}`, token=randomBytes(32).toString('hex');
    const child=spawn(process.env.QFM_BROWSER_DOTNET,[process.env.QFM_BROWSER_HOST_DLL,'--listen',origin+'/'],{
      windowsHide:true,stdio:'ignore',env:{...process.env,QUESTIONABLE_FILE_MANAGER_BROWSER_BEARER:token,
        QUESTIONABLE_FILE_MANAGER_ADB:'__qfm_smoke_no_adb_available__'}});
    let exited=false;child.once('exit',()=>exited=true);
    const stop=async()=>{if(!exited){child.kill();await new Promise((resolve,reject)=>{const timeout=setTimeout(()=>reject(new Error('Owned host did not stop')),10000);child.once('exit',()=>{clearTimeout(timeout);resolve();});});}};
    t.after(stop);
    let started=false;
    for(let i=0;i<100;i++){if(exited)break;try{const response=await fetch(origin);if(response.ok){started=true;break;}}catch{}await new Promise(resolve=>setTimeout(resolve,100));}
    assert.equal(started,true,'Actual host starts without initializing ADB');
    assert.equal((await fetch(origin+'/browser/v1/session')).status,401);
    assert.equal((await fetch(origin+'/browser/v1/session',{headers:{Authorization:`Bearer ${token}`,Origin:'https://untrusted.example'}})).status,403);
    const session=await (await fetch(origin+'/browser/v1/session',{headers:{Authorization:`Bearer ${token}`}})).json();
    assert.ok(session.sessionId);assert.ok(session.platformCapabilities.platform);assert.ok(session.actions.includes('files.list'));
    assert.ok(session.apkSetInstall);assert.equal(typeof session.apkSetInstall.workingCopyCleanupPending,'boolean');
    assert.equal((await fetch(origin+'/apk-set-view.js')).status,200);
    const manifest=await fetch(origin+'/manifest.webmanifest');assert.match(manifest.headers.get('content-type'),/application\/manifest\+json/);
    const worker=await fetch(origin+'/sw.js');assert.match(worker.headers.get('content-type'),/javascript/);
    assert.doesNotMatch(await worker.text(),/QFM_SHELL_MANIFEST/);
    const browser=await chromium.launch({headless:true});t.after(()=>browser.close());
    const context=await browser.newContext(),page=await context.newPage(),errors=[],apiRequests=[];
    page.on('request',request=>{const pathname=new URL(request.url()).pathname;if(pathname.startsWith('/browser/'))apiRequests.push({method:request.method(),path:pathname});});
    page.on('pageerror',error=>errors.push(error.message));
    page.on('console',message=>{if(['error','warning'].includes(message.type())&&!/net::ERR_CONNECTION_REFUSED/.test(message.text()))errors.push(message.text());});
    const response=await page.goto(origin);assert.match(response.headers()['content-security-policy'],/worker-src 'self'/);
    await page.evaluate(()=>navigator.serviceWorker.ready.then(()=>true));await page.reload();
    await page.waitForFunction(()=>Boolean(navigator.serviceWorker.controller));
    const cdp=await context.newCDPSession(page);assert.deepEqual((await cdp.send('Page.getInstallabilityErrors')).installabilityErrors,[]);
    await page.locator('#token').fill(token);await page.locator('#connect').click();
    await page.waitForFunction(()=>document.getElementById('host-status').textContent==='Connected');
    assert.match(await page.locator('#host-capabilities').textContent(),/Host:/);
    assert.equal(await page.locator('#apk-set-view').count(),1);
    assert.equal(await page.locator('#apk-set-send').isDisabled(),true);
    await stop();await page.reload();await page.waitForFunction(()=>document.getElementById('network-status').textContent.startsWith('Saved offline shell'));
    assert.equal(await page.locator('#token').inputValue(),'');assert.equal(await page.locator('#refresh').isDisabled(),true);
    assert.equal(await page.locator('#apk-set-view').count(),1);assert.equal(await page.locator('#apk-set-send').isDisabled(),true);
    await page.getByRole('link',{name:'Agent guide',exact:true}).click();assert.match(await page.title(),/agent guide/);
    assert.deepEqual(errors,[],'Actual CSP permits only shipped shell modules/worker');
    assert.ok(apiRequests.every(request=>request.method==='GET'&&request.path==='/browser/v1/session'),'Loading, authentication and offline reload send no device or recovery request');
  });
