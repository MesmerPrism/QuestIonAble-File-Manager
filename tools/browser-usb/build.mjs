import { build } from 'esbuild';
import { readFile, mkdir, writeFile } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));
const output = path.resolve(here, '../../src/QuestIonAbleFileManager.BrowserHost/wwwroot/vendor');
await mkdir(output, { recursive: true });
const result = await build({
  absWorkingDir: here,
  entryPoints: ['vendor-entry.js'],
  outfile: path.join(output, 'tango-adb.js'),
  bundle: true,
  format: 'esm',
  platform: 'browser',
  target: ['chrome120', 'edge120'],
  minify: true,
  legalComments: 'none',
  sourcemap: false,
  metafile: true,
  banner: { js: '/*! Tango ADB browser dependencies. MIT and component notices: ./TANGO_NOTICES.txt */' }
});

const names = [...new Set(Object.keys(result.metafile.inputs).map(input => /node_modules\/(\@[^/]+\/[^/]+|[^/]+)/.exec(input.replaceAll('\\', '/'))?.[1]).filter(Boolean))].sort();
let notices = 'QuestIonAble File Manager: self-hosted Tango ADB dependencies\n\n';
notices += 'Built from the exact npm package-lock.json. No runtime CDN or network import.\n';
notices += 'Tango upstream: https://github.com/yume-chan/ya-webadb\n\n';
const inventory = [];
for (const name of names) {
  const root = path.join(here, 'node_modules', name);
  const manifest = JSON.parse(await readFile(path.join(root, 'package.json'), 'utf8'));
  const license = await readFile(path.join(root, 'LICENSE'), 'utf8');
  inventory.push({ name, version: manifest.version, license: manifest.license });
  notices += `===== ${name}@${manifest.version} (${manifest.license}) =====\n${license.trim()}\n\n`;
}
// Preserve AOSP attribution included by upstream for adapted ADB protocol code.
// This notice is source-controlled, not fetched during a build.
notices += '===== Upstream adb.NOTICE (AOSP attribution) =====\n';
notices += (await readFile(path.join(here, 'upstream-adb.NOTICE'), 'utf8')).trim() + '\n';
await writeFile(path.join(output, 'TANGO_NOTICES.txt'), notices);
await writeFile(path.join(output, 'tango-packages.json'), JSON.stringify(inventory, null, 2) + '\n');
console.log(`Bundled ${inventory.length} dependency packages; notices and exact versions are in wwwroot/vendor.`);
