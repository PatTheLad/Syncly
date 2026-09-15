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
// Page icons (Syncly.Model/PageIcons.cs) plus the shell chrome (Syncly.Model/MaterialIcons.cs).
// IconCatalogTests fails when a name listed there has no file here.
for (const name of ['add', 'analytics', 'apartment', 'archive', 'arrow_back', 'arrow_downward', 'arrow_upward', 'article', 'attach_file', 'biotech', 'bolt', 'bookmark', 'build', 'business_center', 'calendar_month', 'call', 'campaign', 'check', 'check_box', 'checklist', 'chevron_right', 'close', 'cloud', 'code', 'content_copy', 'cottage', 'dark_mode', 'delete', 'description', 'directions_car', 'download', 'drag_indicator', 'drive_file_move', 'eco', 'edit', 'edit_note', 'event', 'explore', 'favorite', 'filter_alt', 'fit_screen', 'fitness_center', 'flag', 'flight', 'folder', 'folder_open', 'folder_zip', 'forest', 'format_h1', 'format_h2', 'format_h3', 'format_indent_decrease', 'format_indent_increase', 'format_list_bulleted', 'format_list_numbered', 'format_paragraph', 'format_quote', 'groups', 'help', 'home', 'horizontal_rule', 'hub', 'inventory_2', 'key', 'keyboard_arrow_down', 'keyboard_command_key', 'label', 'link', 'local_cafe', 'local_fire_department', 'lock', 'luggage', 'mail', 'map', 'medical_services', 'memory', 'menu', 'more_horiz', 'more_vert', 'movie', 'music_note', 'north_east', 'open_in_new', 'palette', 'park', 'payments', 'person', 'pets', 'photo_camera', 'picture_as_pdf', 'print', 'psychology', 'public', 'receipt_long', 'restart_alt', 'restaurant', 'rocket_launch', 'savings', 'schedule', 'school', 'science', 'search', 'settings', 'shopping_cart', 'star', 'sticky_note_2', 'storefront', 'sunny', 'sync', 'tag', 'task_alt', 'today', 'train', 'trending_up', 'upgrade', 'visibility', 'water_drop', 'waves', 'work', 'workspaces']) {
  output.set(`icons/material/${name}.svg`, await readFile(path.join(here, 'node_modules/@material-symbols/svg-400/outlined', `${name}.svg`), 'utf8'));
}
output.set('icons/material/LICENSE.txt', await readFile(path.join(here, 'node_modules/@material-symbols/svg-400/LICENSE'), 'utf8'));
for (const [name, content] of output) {
  const destination = path.join(assets, name);
  if (check) {
    if (await readFile(destination, 'utf8') !== content) throw new Error(`Stale vendor asset: ${name}`);
  } else {
    await mkdir(path.dirname(destination), { recursive: true });
    await writeFile(destination, content);
  }
}
console.log(`${check ? 'Verified' : 'Built'} ${output.size} offline assets.`);