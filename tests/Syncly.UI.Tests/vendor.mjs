import { build } from 'esbuild';
import { readFile, writeFile, mkdir } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import path from 'node:path';

const here = path.dirname(fileURLToPath(import.meta.url));
const assets = path.resolve(here, '../../src/Syncly.UI/wwwroot');
const check = process.argv.includes('--check');
const result = await build({
  stdin: { contents: "export * from 'd3-force';", resolveDir: here },
  bundle: true, format: 'esm', minify: true, write: false, target: 'es2020',
});
const output = new Map([['js/vendor/d3-force.mjs', result.outputFiles[0].text]]);
const licenses = [];
for (const name of ['d3-force', 'd3-quadtree', 'd3-dispatch', 'd3-timer']) {
  const metadata = JSON.parse(await readFile(path.join(here, 'node_modules', name, 'package.json'), 'utf8'));
  licenses.push(`${name} ${metadata.version}\n${await readFile(path.join(here, 'node_modules', name, 'LICENSE'), 'utf8')}`);
}
output.set('js/vendor/LICENSE.txt', licenses.join('\n\n'));
for (const name of ['search', 'x', 'plus', 'minus', 'maximize', 'locate-fixed', 'rotate-ccw', 'sliders-horizontal', 'list', 'arrow-up-right', 'chevron-up', 'chevron-down', 'ellipsis', 'arrow-left', 'copy', 'file-plus', 'pencil', 'trash-2']) {
  output.set(`icons/graph/${name}.svg`, await readFile(path.join(here, 'node_modules/lucide-static/icons', `${name}.svg`), 'utf8'));
}
output.set('icons/graph/LICENSE.txt', await readFile(path.join(here, 'node_modules/lucide-static/LICENSE'), 'utf8'));
for (const [name, content] of output) {
  const destination = path.join(assets, name);
  if (check) {
    if (await readFile(destination, 'utf8') !== content) throw new Error(`Stale vendor asset: ${name}`);
  } else {
    await mkdir(path.dirname(destination), { recursive: true });
    await writeFile(destination, content);
  }
}
console.log(`${check ? 'Verified' : 'Built'} ${output.size} offline graph assets.`);