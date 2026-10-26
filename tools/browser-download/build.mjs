import {build} from 'esbuild';
import {readFile, mkdir, writeFile} from 'node:fs/promises';
import path from 'node:path';
import {fileURLToPath} from 'node:url';
import {createHash} from 'node:crypto';

const here = path.dirname(fileURLToPath(import.meta.url));
const output = path.resolve(here, '../../src/QuestIonAbleFileManager.BrowserHost/wwwroot/vendor');
const manifest = JSON.parse(await readFile(path.join(here, 'node_modules/@noble/hashes/package.json'), 'utf8'));
if (manifest.version !== '2.4.0' || manifest.license !== 'MIT') throw new Error('Unexpected SHA-256 package identity');
const result = await build({absWorkingDir:here, entryPoints:['vendor-entry.js'], outfile:path.join(output, 'noble-sha256.js'),
  bundle:true, format:'esm', platform:'browser', target:['chrome120', 'edge120'], minify:true,
  legalComments:'none', sourcemap:false, metafile:true, write:false,
  banner:{js:'/*! @noble/hashes 2.4.0 SHA-256. MIT; see ./NOBLE_HASHES_NOTICES.txt */'}});
for (const input of Object.keys(result.metafile.inputs))
  if (input !== 'vendor-entry.js' && !input.replaceAll('\\', '/').startsWith('node_modules/@noble/hashes/'))
    throw new Error('Unexpected runtime dependency in SHA-256 bundle');
const license = await readFile(path.join(here, 'node_modules/@noble/hashes/LICENSE'), 'utf8');
const lock = JSON.parse(await readFile(path.join(here, 'package-lock.json'), 'utf8'));
const pinned = lock.packages['node_modules/@noble/hashes'];
if (pinned.version !== manifest.version || !pinned.integrity?.startsWith('sha512-')) throw new Error('Package lock identity is unavailable');
const sha = bytes => createHash('sha256').update(bytes).digest('hex');
const notices = 'QuestIonAble File Manager: self-hosted incremental SHA-256\n\n' +
  'Built from the exact tools/browser-download/package-lock.json. No runtime CDN or network import.\n' +
  'Upstream: https://github.com/paulmillr/noble-hashes/tree/2.4.0\n' +
  'Release source commit: 663c2aeeffc308ac0cded59bd32f7c212adacfc2\n\n' +
  `===== @noble/hashes@${manifest.version} (${manifest.license}) =====\n${license.trim()}\n`;
const files = new Map([
  ['noble-sha256.js', result.outputFiles[0].contents],
  ['NOBLE_HASHES_NOTICES.txt', Buffer.from(notices)],
  ['noble-hashes-packages.json', Buffer.from(JSON.stringify([{name:manifest.name, version:manifest.version, license:manifest.license,
    sourceCommit:'663c2aeeffc308ac0cded59bd32f7c212adacfc2', packageIntegrity:pinned.integrity,
    licenseSha256:sha(license), bundleSha256:sha(result.outputFiles[0].contents)}], null, 2) + '\n')]
]);
if (process.argv.includes('--check')) {
  for (const [name, bytes] of files)
    if (!Buffer.from(bytes).equals(await readFile(path.join(output, name)))) throw new Error(`Vendored output differs: ${name}`);
  console.log('Incremental SHA-256 bundle, notices and inventory match the pinned source.');
} else {
  await mkdir(output, {recursive:true});
  for (const [name, bytes] of files) await writeFile(path.join(output, name), bytes);
  console.log('Bundled pinned @noble/hashes SHA-256 with MIT notice and package inventory.');
}
