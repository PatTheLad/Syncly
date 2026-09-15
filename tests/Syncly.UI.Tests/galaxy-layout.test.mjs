import test from 'node:test';
import assert from 'node:assert/strict';
import { createLayout, radiusFor, restorePositions } from '../../src/Syncly.UI/wwwroot/js/galaxy-layout.mjs';

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

test('node size is bounded and missing nodes are distinct', () => {
  assert.equal(radiusFor(0), 9);
  assert.ok(radiusFor(10) > radiusFor(1));
  assert.equal(radiusFor(100000), 19);
  assert.equal(radiusFor(30, true), 7);
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