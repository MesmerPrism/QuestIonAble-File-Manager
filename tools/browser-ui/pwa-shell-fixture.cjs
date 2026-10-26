// Mirrors BrowserShellAssets.RenderWorker exactly; backend tests bind its public manifest.
const fs=require('node:fs/promises');
const path=require('node:path');
const {createHash}=require('node:crypto');
const root=path.resolve(__dirname,'../../src/QuestIonAbleFileManager.BrowserHost/wwwroot');
const paths=['/index.html','/agent.html','/app.js','/pwa.js','/retained-view.js','/styles.css','/usb-proof.js','/usb-proof-core.js','/vendor/tango-adb.js','/manifest.webmanifest','/icons/qfm-192.png','/icons/qfm-512.png'];
const hash=bytes=>createHash('sha256').update(bytes).digest('hex');
async function renderShell(overrides={},workerSuffix='') {
  const template=await fs.readFile(path.join(root,'sw.js'),'utf8')+workerSuffix;
  const marker='/*QFM_SHELL_MANIFEST*/ null';
  if(template.split(marker).length!==2)throw new Error('Worker must have exactly one rendered manifest placeholder');
  const bytes=new Map(),assets={};
  for(const asset of paths) {const content=overrides[asset]??await fs.readFile(path.join(root,asset.slice(1)));bytes.set(asset,content);assets[asset]=hash(content);}
  const revision=hash(template+'\n'+paths.map(asset=>asset+'\t'+assets[asset]+'\n').join(''));
  return {template,revision,bytes,worker:template.replace(marker,JSON.stringify({revision,assets}))};
}
module.exports={root,paths,hash,renderShell};
