import test from 'node:test';
import assert from 'node:assert/strict';
import { collapsed, createLayout, kindFor, lodHidden, lodOpen, radiusFor, recencyHeat, restorePositions, sunColorFor } from '../../src/Syncly.UI/wwwroot/js/galaxy-layout.mjs';

const fixture = () => ({
  nodes: [{ id: 'parent' }, { id: 'child', parentId: 'parent' }, { id: 'other' }, { id: 'isolated' }],
  edges: [{ from: 'parent', to: 'child' }, { from: 'child', to: 'parent' },
    { from: 'child', to: 'other' }, { from: 'parent', to: 'child' },
    { from: 'parent', to: 'parent' }, { from: 'parent', to: 'absent' }],
});
const settle = layout => { layout.tick(300); return layout; };

test('keeps real parent-child and reciprocal directions, one spring per pair', () => {
  const layout = settle(createLayout(fixture()));
  assert.equal(layout.edges.length, 3);
  assert.equal(layout.links.length, 2);
  assert.equal(layout.byId.get('child').degree, 2);
  const child = layout.byId.get('child');
  const parent = layout.byId.get('parent');
  const isolated = layout.byId.get('isolated');
  assert.ok(Math.hypot(child.x - parent.x, child.y - parent.y) < Math.hypot(child.x - isolated.x, child.y - isolated.y));
  assert.equal(layout.active, false);
});

test('cold layout is deterministic regardless of payload order', () => {
  const input = fixture();
  const first = settle(createLayout(input));
  const second = settle(createLayout({ nodes: [...input.nodes].reverse(), edges: [...input.edges].reverse() }));
  assert.deepEqual(first.snapshot(), second.snapshot());
});

test('metadata-only changes never move settled nodes or restart simulation', () => {
  const input = fixture();
  const layout = settle(createLayout(input));
  const before = layout.snapshot();
  assert.equal(layout.update({ ...input, nodes: input.nodes.map(node => ({ ...node, title: 'Renamed', updatedAt: 123 })) }), false);
  layout.tick(300);
  assert.deepEqual(layout.snapshot(), before);
  assert.equal(layout.active, false);
  assert.equal(layout.byId.get('child').title, 'Renamed');
});

test('empty, singleton and disconnected graphs remain finite and settle', () => {
  for (const count of [0, 1, 100, 1000]) {
    const layout = settle(createLayout({ nodes: Array.from({ length: count }, (_, index) => ({ id: `note-${index}` })) }));
    assert.equal(layout.active, false);
    assert.ok(layout.nodes.every(node => Number.isFinite(node.x) && Number.isFinite(node.y)));
    assert.ok(layout.nodes.every(node => Math.abs(node.x) < 10000 && Math.abs(node.y) < 10000));
  }
});

test('topology updates preserve positions before relaxation and remove stale nodes', () => {
  const input = fixture();
  const layout = settle(createLayout(input));
  const before = { ...layout.byId.get('child') };
  layout.update({ nodes: [...input.nodes.filter(node => node.id !== 'isolated'), { id: 'new' }],
    edges: [...input.edges, { from: 'child', to: 'new' }] });
  assert.equal(layout.byId.get('child').x, before.x);
  assert.equal(layout.byId.has('isolated'), false);
  const added = layout.byId.get('new');
  assert.ok(Math.hypot(added.x - before.x, added.y - before.y) <= 31);
  settle(layout);
});

test('local pins survive refresh and are cleared by reset', () => {
  const layout = settle(createLayout(fixture()));
  layout.pin('child', 123, 456);
  layout.update(fixture());
  assert.equal(layout.byId.get('child').fx, 123);
  layout.reset();
  assert.equal(layout.byId.get('child').fx, null);
  settle(layout);
  assert.notEqual(layout.byId.get('child').x, 123);
});

test('restoration rejects corrupt coordinates and layouts do not share state', () => {
  assert.equal(restorePositions([{ id: 'bad', x: Infinity, y: 0 }, null]).size, 0);
  const first = settle(createLayout(fixture()));
  first.pin('child', 10, 20);
  const restored = createLayout(fixture(), first.snapshot());
  assert.equal(restored.byId.get('child').fx, 10);
  const separate = createLayout(fixture());
  assert.equal(separate.byId.get('child').fx, null);
  restored.destroy();
  separate.destroy();
  assert.equal(separate.active, false);
});

test('node size grows with descendants and missing nodes stay small', () => {
  assert.ok(radiusFor(0, 0, false, 10) > radiusFor(0, 0, false, 0));
  assert.ok(radiusFor(1, 0, false, 6) > radiusFor(1, 0, false, 0));
  assert.ok(radiusFor(2, 0, false, 4) > radiusFor(2, 0, false, 0));
  assert.ok(radiusFor(0, 10, false, 0) > radiusFor(0, 1, false, 0));
  assert.equal(radiusFor(0, 30, true), 6);
  assert.ok(radiusFor(1, 0) < radiusFor(0, 0));
  assert.ok(radiusFor(2, 0) < radiusFor(1, 0));
  assert.ok(radiusFor(10, 0) < radiusFor(2, 0));
});

test('nested notes promote from moon to planet to sun to black hole to galaxy', () => {
  assert.equal(kindFor(0, 0), 'sun');
  assert.equal(kindFor(1, 0), 'planet');
  assert.equal(kindFor(2, 0), 'moon');
  assert.equal(kindFor(3, 0), 'asteroid');
  assert.equal(kindFor(2, 3), 'planet');
  assert.equal(kindFor(3, 3), 'planet');
  assert.equal(kindFor(1, 8), 'sun');
  assert.equal(kindFor(2, 8), 'sun');
  assert.equal(kindFor(0, 31), 'sun');
  assert.equal(kindFor(0, 32), 'blackhole');
  assert.equal(kindFor(1, 32), 'blackhole');
  assert.equal(kindFor(0, 79), 'blackhole');
  assert.equal(kindFor(0, 80), 'galaxy');
  assert.equal(kindFor(1, 0, 'galaxy'), 'blackhole');
  assert.equal(kindFor(2, 0, 'sun'), 'planet');
  assert.equal(kindFor(3, 0, 'planet'), 'moon');
  const moon = createLayout({
    nodes: [{ id: 'sun', depth: 0 }, { id: 'planet', depth: 1, parentId: 'sun' },
      { id: 'moon', depth: 2, parentId: 'planet' },
      ...Array.from({ length: 3 }, (_, index) => ({ id: `nested-${index}`, depth: 3, parentId: 'moon' }))],
  });
  assert.equal(moon.byId.get('moon').kind, 'planet');
  assert.equal(moon.byId.get('moon').descendants, 3);
  assert.equal(moon.byId.get('nested-0').kind, 'moon');
  const heavy = createLayout({
    nodes: [{ id: 'sun', depth: 0 }, { id: 'planet', depth: 1, parentId: 'sun' },
      ...Array.from({ length: 8 }, (_, index) => ({ id: `moon-${index}`, depth: 2, parentId: 'planet' })),
      { id: 'dust', depth: 3, parentId: 'moon-0' }],
  });
  assert.equal(heavy.byId.get('planet').kind, 'sun');
  assert.equal(heavy.byId.get('sun').kind, 'sun');
  assert.equal(heavy.byId.get('moon-0').kind, 'planet');
  assert.equal(heavy.byId.get('moon-7').kind, 'planet');
  assert.equal(heavy.byId.get('dust').kind, 'moon');
  const collapsedHole = createLayout({
    nodes: [{ id: 'core', depth: 0 },
      ...Array.from({ length: 32 }, (_, index) => ({ id: `sat-${index}`, depth: 1, parentId: 'core' }))],
  });
  assert.equal(collapsedHole.byId.get('core').kind, 'blackhole');
  assert.equal(collapsedHole.byId.get('sat-0').kind, 'sun');
  assert.ok(collapsedHole.byId.get('core').radius > radiusFor(0, 0, false, 8));
  const disc = createLayout({
    nodes: [{ id: 'core', depth: 0 },
      ...Array.from({ length: 80 }, (_, index) => ({ id: `sat-${index}`, depth: 1, parentId: 'core' }))],
  });
  assert.equal(disc.byId.get('core').kind, 'galaxy');
  assert.equal(disc.byId.get('sat-0').kind, 'blackhole');
  assert.ok(disc.byId.get('core').radius > collapsedHole.byId.get('core').radius);
});

test('galaxies stay collapsed until zoomed in, except a kept path', () => {
  assert.equal(collapsed({ kind: 'planet', systemRadius: 6, radius: 11 }, 1), true);
  assert.equal(collapsed({ kind: 'planet', systemRadius: 12, radius: 11 }, 1), false);
  const layout = createLayout({
    nodes: [{ id: 'core', depth: 0 },
      ...Array.from({ length: 80 }, (_, index) => ({ id: `sat-${index}`, depth: 1, parentId: 'core' })),
      { id: 'moon', depth: 2, parentId: 'sat-0' }],
  });
  const byId = layout.byId;
  const core = byId.get('core');
  const hole = byId.get('sat-0');
  assert.equal(core.kind, 'galaxy');
  assert.equal(collapsed(core, 0.02), true);
  assert.equal(lodHidden(byId.get('sat-0'), byId, 0.02, new Set()), true);
  assert.equal(lodHidden(byId.get('moon'), byId, 0.02, new Set()), true);
  const galaxyOpen = 18 / core.radius;
  assert.equal(collapsed(core, galaxyOpen), false);
  assert.equal(lodHidden(byId.get('sat-0'), byId, galaxyOpen, new Set()), false);
  assert.equal(lodHidden(byId.get('moon'), byId, galaxyOpen, new Set()), true);
  const holeOpen = 16 / hole.radius;
  assert.equal(collapsed(hole, holeOpen), false);
  assert.equal(lodHidden(byId.get('moon'), byId, holeOpen, new Set()), false);
  const open = lodOpen(byId, ['moon']);
  assert.equal(open.has('core'), true);
  assert.equal(open.has('sat-0'), true);
  assert.equal(lodHidden(byId.get('moon'), byId, 0.02, open), false);
  assert.equal(lodHidden(byId.get('sat-1'), byId, 0.02, open), true);
  const radii = [...byId.values()].filter(node => node.parentId === 'core').map(node => node.orbitRadius);
  assert.equal(new Set(radii).size, radii.length);
  const speeds = [...byId.values()].filter(node => node.parentId === 'core').map(node => node.orbitSpeed);
  assert.ok(new Set(speeds.map(speed => speed.toFixed(8))).size > 1);
});

test('family recency heat uses the newest descendant edit', () => {
  const now = Date.now();
  const layout = createLayout({
    nodes: [{ id: 'core', depth: 0, updatedAt: 1 },
      { id: 'old', depth: 1, parentId: 'core', updatedAt: now - 8 * 86400000 },
      { id: 'fresh', depth: 1, parentId: 'core', updatedAt: now - 3600000 }],
  });
  assert.ok(layout.byId.get('core').heatAt >= layout.byId.get('fresh').updatedAt);
  assert.ok(recencyHeat(layout.byId.get('core'), now) > 0.8);
  assert.equal(recencyHeat({ updatedAt: now - 8 * 86400000 }, now), 0);
  assert.equal(recencyHeat({ updatedAt: now }, now), 1);
});

test('satellites sit on packed rings that do not overlap sibling subsystems', () => {
  const layout = createLayout({
    nodes: [
      { id: 'sun', depth: 0 },
      { id: 'earth', depth: 1, parentId: 'sun' },
      { id: 'mars', depth: 1, parentId: 'sun' },
      { id: 'luna', depth: 2, parentId: 'earth' },
      { id: 'phobos', depth: 2, parentId: 'mars' },
      { id: 'deimos', depth: 2, parentId: 'mars' },
    ],
  });
  layout.orbit(0);
  for (const id of ['earth', 'mars', 'luna', 'phobos', 'deimos']) {
    const node = layout.byId.get(id);
    const parent = layout.byId.get(node.parentId);
    const distance = Math.hypot(node.x - parent.x, node.y - parent.y);
    assert.ok(Math.abs(distance - node.orbitRadius) < 1e-6);
  }
  const earth = layout.byId.get('earth');
  const mars = layout.byId.get('mars');
  assert.ok(Math.abs(earth.orbitRadius - mars.orbitRadius) + 1e-6 >= earth.systemRadius + mars.systemRadius);
  const phobos = layout.byId.get('phobos');
  const deimos = layout.byId.get('deimos');
  assert.ok(Math.abs(phobos.orbitRadius - deimos.orbitRadius) + 1e-6 >= phobos.systemRadius + deimos.systemRadius);
  assert.ok(earth.orbitRadius - earth.systemRadius >= layout.byId.get('sun').radius);
});

test('settled sun systems keep their outer orbit rings from overlapping', () => {
  const moons = (prefix, parent, count, depth) =>
    Array.from({ length: count }, (_, index) => ({ id: `${prefix}-${index}`, depth, parentId: parent }));
  const layout = settle(createLayout({
    nodes: [
      { id: 'sol', depth: 0 }, { id: 'vega', depth: 0 },
      ...moons('sol-p', 'sol', 4, 1),
      ...moons('sol-p0-m', 'sol-p-0', 3, 2),
      ...moons('vega-p', 'vega', 4, 1),
      ...moons('vega-p-0-m', 'vega-p-0', 3, 2),
    ],
  }));
  const sol = layout.byId.get('sol');
  const vega = layout.byId.get('vega');
  const distance = Math.hypot(sol.x - vega.x, sol.y - vega.y);
  assert.ok(distance + 1e-6 >= sol.systemRadius + vega.systemRadius);
  const planet = layout.byId.get('sol-p-0');
  assert.ok(Math.abs(Math.hypot(planet.x - sol.x, planet.y - sol.y) - planet.orbitRadius) < 1e-6);
});

test('larger suns are bluer and smaller suns are redder', () => {
  const rgb = hex => [1, 3, 5].map(index => parseInt(hex.slice(index, index + 2), 16));
  const dwarf = rgb(sunColorFor(18));
  const giant = rgb(sunColorFor(48));
  assert.ok(dwarf[0] > dwarf[2]);
  assert.ok(giant[2] > giant[0]);
  assert.ok(giant[2] > dwarf[2]);
  assert.ok(dwarf[0] > giant[0]);
});

test('returning to an unchanged graph restores its settled layout exactly', () => {
  const first = settle(createLayout(fixture()));
  const restored = createLayout(fixture(), first.snapshot(), first.topology);
  assert.equal(restored.active, false);
  assert.deepEqual(restored.snapshot(), first.snapshot());
  const changed = fixture();
  changed.edges.push({ from: 'isolated', to: 'child' });
  const updated = createLayout(changed, first.snapshot(), first.topology);
  assert.equal(updated.active, true);
  settle(updated);
});