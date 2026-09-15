import { test, expect } from '@playwright/test';

const state = page => page.evaluate(() => window.graph.inspect('galaxy-canvas'));
const settle = page => expect.poll(async () => (await state(page))?.scheduled, { timeout: 20000 }).toBe(false);
test.beforeEach(async ({ page }) => { await page.goto('/'); await settle(page); });

test('renders actual nodes, collision-free labels and stops drawing at rest', async ({ page }) => {
  const view = await state(page);
  expect(view.nodes.length).toBeGreaterThan(20);
  expect(view.labels.length).toBeGreaterThan(5);
  const pixels = await page.evaluate(() => {
    const canvas = document.querySelector('canvas');
    const context = canvas.getContext('2d');
    const nodes = window.graph.inspect('galaxy-canvas').nodes;
    const ratio = canvas.width / canvas.clientWidth;
    return nodes.filter(node => node.x > 0 && node.y > 0 && node.x < canvas.clientWidth && node.y < canvas.clientHeight)
      .map(node => [...context.getImageData(Math.round(node.x * ratio), Math.round(node.y * ratio), 1, 1).data]);
  });
  expect(pixels.filter(pixel => Math.max(...pixel.slice(0, 3)) > 50).length).toBeGreaterThan(20);
  for (const [index, rectangle] of view.labels.entries()) {
    for (const other of view.labels.slice(index + 1)) {
      expect(rectangle.x < other.x + other.width && rectangle.x + rectangle.width > other.x
        && rectangle.y < other.y + other.height && rectangle.y + rectangle.height > other.y).toBe(false);
    }
  }
  await page.screenshot({ path: 'test-results/galaxy-desktop.png' });
  await page.evaluate(() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve))));
  expect((await state(page)).frames).toBe(view.frames);
});

test('select, open and drag are separate actions', async ({ page }) => {
  const node = (await state(page)).nodes.find(node => node.id === 'note-0');
  await page.mouse.click(node.x, node.y);
  expect((await state(page)).selectedId).toBe('note-0');
  expect(await page.evaluate(() => window.events.some(event => event.method === 'OnOpen'))).toBe(false);
  await page.keyboard.press('Enter');
  expect(await page.evaluate(() => window.events.at(-1))).toEqual({ method: 'OnOpen', args: ['note-0'] });
  await page.evaluate(() => { window.events = []; });
  await page.mouse.move(node.x, node.y);
  await page.mouse.down();
  await page.mouse.move(node.x + 70, node.y + 25, { steps: 6 });
  await page.mouse.up();
  await settle(page);
  expect(await page.evaluate(() => window.events.length)).toBe(0);
  expect((await state(page)).positions.find(node => node.id === 'note-0').fx).not.toBeNull();
});

test('zoom anchors to pointer and title edits preserve positions', async ({ page }) => {
  const before = await state(page);
  await page.mouse.move(300, 250);
  await page.mouse.wheel(0, -80);
  await expect.poll(async () => (await state(page)).camera.scale).toBeGreaterThan(before.camera.scale);
  await settle(page);
  const after = await state(page);
  const anchor = camera => ({ x: (300 - 720) / camera.scale + camera.x, y: (250 - 450) / camera.scale + camera.y });
  expect(anchor(after.camera).x).toBeCloseTo(anchor(before.camera).x, 6);
  expect(anchor(after.camera).y).toBeCloseTo(anchor(before.camera).y, 6);
  await page.evaluate(() => { window.data.nodes[0].title = 'Renamed note'; window.graph.update('galaxy-canvas', window.data); });
  await settle(page);
  expect((await state(page)).positions).toEqual(before.positions);
});

test('neighborhood exit restores camera and space state is isolated', async ({ page }) => {
  const before = (await state(page)).camera;
  await page.evaluate(() => { window.graph.select('galaxy-canvas', 'note-0'); window.graph.configure('galaxy-canvas', { mode: 'connections' }); });
  await settle(page);
  expect((await state(page)).nodes.length).toBeLessThan(20);
  await page.evaluate(() => window.graph.configure('galaxy-canvas', { mode: 'all' }));
  await settle(page);
  expect((await state(page)).camera).toEqual(before);
  await page.evaluate(() => window.loadFixture(1, 'another-space'));
  await settle(page);
  expect((await state(page)).nodes).toHaveLength(1);
  expect((await state(page)).selectedId).toBeNull();
});

test('mobile touch pinch, cancellation and reduced motion', async ({ page, context }) => {
  await page.setViewportSize({ width: 390, height: 844 });
  await page.emulateMedia({ reducedMotion: 'reduce' });
  await page.evaluate(() => window.graph.fit('galaxy-canvas'));
  await settle(page);
  const before = (await state(page)).camera.scale;
  const client = await context.newCDPSession(page);
  await client.send('Input.dispatchTouchEvent', { type: 'touchStart', touchPoints: [{ x: 100, y: 400 }, { x: 280, y: 400 }] });
  await client.send('Input.dispatchTouchEvent', { type: 'touchMove', touchPoints: [{ x: 70, y: 400 }, { x: 310, y: 400 }] });
  await client.send('Input.dispatchTouchEvent', { type: 'touchEnd', touchPoints: [] });
  await settle(page);
  expect((await state(page)).camera.scale).toBeGreaterThan(before);
  expect(await page.evaluate(() => window.events.filter(event => event.method === 'OnSelect').length)).toBe(0);
  const node = (await state(page)).nodes.find(node => node.x > 30 && node.x < 360 && node.y > 30 && node.y < 810);
  await client.send('Input.dispatchTouchEvent', { type: 'touchStart', touchPoints: [{ x: node.x, y: node.y }] });
  await client.send('Input.dispatchTouchEvent', { type: 'touchCancel', touchPoints: [] });
  expect(await page.evaluate(() => window.events.filter(event => event.method === 'OnSelect').length)).toBe(0);
  await page.evaluate(() => window.graph.fit('galaxy-canvas'));
  await settle(page);
  await page.screenshot({ path: 'test-results/galaxy-mobile.png' });
});

test('large fixtures settle and dispose without background work', async ({ page }) => {
  for (const count of [0, 1, 100, 500, 1000]) {
    await page.evaluate(count => window.loadFixture(count, `size-${count}`), count);
    await settle(page);
    const view = await state(page);
    expect(view.positions).toHaveLength(count);
    expect(view.positions.every(node => Number.isFinite(node.x) && Number.isFinite(node.y))).toBe(true);
    console.log(`${count} nodes: largest layout tick ${view.maxTickMs.toFixed(1)}ms`);
  }
  await page.evaluate(() => window.graph.destroy('galaxy-canvas'));
  expect(await state(page)).toBeNull();
});