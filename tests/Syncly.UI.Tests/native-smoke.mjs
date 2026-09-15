import { chromium, expect } from '@playwright/test';
import { mkdir } from 'node:fs/promises';

if (process.env.SYNCLY_GRAPH_ALLOW_FIXTURE !== '1') throw new Error('Use a disposable desktop --data directory and set SYNCLY_GRAPH_ALLOW_FIXTURE=1. This test creates and edits notes.');
const browser = await chromium.connectOverCDP(process.env.SYNCLY_GRAPH_CDP || 'http://127.0.0.1:9227');
const page = browser.contexts()[0].pages()[0];
const errors = [];
page.on('pageerror', error => errors.push(error.message));
await mkdir('test-results', { recursive: true });
const graphState = () => page.evaluate(async () => (await import('./js/galaxy.mjs')).inspect('galaxy-canvas'));
const settle = () => expect.poll(async () => (await graphState())?.settled, { timeout: 20000 }).toBe(true);
const enterGraph = async () => { await page.getByTitle('Graph', { exact: true }).click(); await page.locator('.graph-page').waitFor(); await settle(); };
const selectNote = async title => {
  await page.getByRole('searchbox', { name: 'Find a note' }).fill(title);
  await page.locator('.graph-note-list[aria-label=Notes] button').filter({ hasText: title }).first().click();
  await expect(page.locator('.graph-inspector h2')).toHaveText(title);
  await settle();
};

try {
  await page.setViewportSize({ width: 1440, height: 900 });
  if (process.argv.includes('--seed')) {
    const notes = [
      ['Knowledge garden', 'A place for connected ideas. [[Design systems]] [[Field notes]] [[Reading list]] [[Unwritten idea]]'],
      ['Design systems', 'Quiet interfaces, clear states, and predictable navigation. [[Knowledge garden]] [[Small experiments]]'],
      ['Field notes', 'Observations from the week. [[Knowledge garden]] [[Research journal]]'],
      ['Reading list', 'Books and papers worth returning to. [[Research journal]]'],
      ['Small experiments', 'Try the smallest useful version first. [[Design systems]]'],
      ['Research journal', 'Questions, evidence, and next steps. [[Open questions]]'],
      ['Open questions', 'What changes when ideas are connected? [[Knowledge garden]]'],
      ['Weekly review', 'A quiet place to reflect.'],
      ['An unusually long note title with enough words to test wrapping on a narrow mobile screen', 'Long titles should remain readable. [[Field notes]]'],
    ];
    for (const [title, text] of notes) {
      await page.getByTitle('New page (Ctrl+N)', { exact: true }).click();
      await page.locator('.page-title').fill(title);
      await page.locator('.page-title').press('Tab');
      await page.locator('.block-text').first().click();
      await page.locator('[contenteditable=true]').first().fill(text);
      await page.locator('.page-title').click();
      await expect(page.locator('.page-title')).toHaveValue(title);
    }
  }

  await enterGraph();
  await selectNote('Knowledge garden');
  await expect(page.locator('.graph-excerpt')).toContainText('A place for connected ideas.');
  await expect(page.getByRole('list', { name: 'Outgoing links' })).toContainText('Design systems');
  await expect(page.getByRole('list', { name: 'Incoming links' })).toContainText('Open questions');
  const before = (await graphState()).positions;
  await page.getByRole('button', { name: 'Connections', exact: true }).click();
  await settle();
  expect((await graphState()).mode).toBe('connections');
  await page.getByRole('button', { name: 'All', exact: true }).click();
  await settle();
  expect((await graphState()).positions).toEqual(before);

  await selectNote('Weekly review');
  const beforeRename = (await graphState()).positions;
  await page.locator('.graph-inspector').getByTitle('Page actions', { exact: true }).click();
  await page.getByRole('button', { name: 'Rename', exact: true }).click();
  await page.getByRole('textbox', { name: 'Page title' }).fill('Weekly review revised');
  await page.getByRole('button', { name: 'Save', exact: true }).click();
  await expect(page.locator('.graph-inspector h2')).toHaveText('Weekly review revised');
  await settle();
  expect((await graphState()).positions).toEqual(beforeRename);
  await page.locator('.graph-inspector').getByTitle('Page actions', { exact: true }).click();
  await page.getByRole('button', { name: 'Rename', exact: true }).click();
  await page.getByRole('textbox', { name: 'Page title' }).fill('Weekly review');
  await page.getByRole('button', { name: 'Save', exact: true }).click();
  await expect(page.locator('.graph-inspector h2')).toHaveText('Weekly review');
  await selectNote('Knowledge garden');
  await page.getByRole('list', { name: 'Outgoing links' }).getByRole('button', { name: 'Design systems', exact: true }).click();
  await expect(page.locator('.graph-inspector h2')).toHaveText('Design systems');
  await expect(page.locator('.graph-excerpt')).toContainText('Quiet interfaces');

  await page.getByRole('button', { name: 'Graph settings', exact: true }).click();
  await page.getByRole('combobox', { name: 'Filter notes' }).selectOption('unresolved');
  await page.getByRole('button', { name: 'Close settings', exact: true }).click();
  await selectNote('unwritten idea');
  await expect(page.getByRole('button', { name: 'Create page', exact: true })).toBeVisible();
  await page.getByRole('button', { name: 'Graph settings', exact: true }).click();
  await page.getByRole('combobox', { name: 'Filter notes' }).selectOption('all');
  await page.getByRole('button', { name: 'Close settings', exact: true }).click();
  await selectNote('Knowledge garden');
  await page.getByRole('searchbox', { name: 'Find a note' }).fill('');
  await page.locator('.graph-note-list[aria-label=Notes] button').filter({ hasText: 'Knowledge garden' }).first().click();

  for (const [width, height] of [[1440, 900], [1024, 768], [390, 844], [360, 640], [844, 390]]) {
    await page.setViewportSize({ width, height });
    if (width < 720) {
      await page.getByTitle('Graph', { exact: true }).click();
      await expect(page.locator('.shell')).toHaveClass(/no-sidebar/);
    }
    await settle();
    const expand = page.getByRole('button', { name: 'Expand details', exact: true });
    if (await expand.isVisible()) await expand.click();
    await settle();
    const dimensions = await page.evaluate(() => {
      const canvas = document.querySelector('.graph-canvas').getBoundingClientRect();
      const inspector = document.querySelector('.graph-inspector').getBoundingClientRect();
      const graph = document.querySelector('.graph-page');
      return { canvas: { x: canvas.x, y: canvas.y, right: canvas.right, bottom: canvas.bottom, width: canvas.width, height: canvas.height },
        inspector: { x: inspector.x, y: inspector.y, right: inspector.right, bottom: inspector.bottom },
        overflow: graph.scrollWidth > graph.clientWidth + 1 };
    });
    await page.screenshot({ path: `test-results/native-${width}x${height}.png` });
    expect(dimensions.canvas.width).toBeGreaterThan(100);
    expect(dimensions.canvas.height).toBeGreaterThan(90);
    expect(dimensions.overflow).toBe(false);
    expect(dimensions.canvas.right <= dimensions.inspector.x + 1 || dimensions.canvas.bottom <= dimensions.inspector.y + 1).toBe(true);
    const cameraBounds = await page.locator('.graph-camera').boundingBox();
    expect(dimensions.canvas.bottom).toBeLessThanOrEqual(cameraBounds.y + 1);
  }
  await page.setViewportSize({ width: 1440, height: 900 });
  await selectNote('Knowledge garden');
  await page.locator('.graph-inspector').getByTitle('Page actions', { exact: true }).click();
  await page.getByRole('button', { name: 'Duplicate', exact: true }).click();
  await expect(page.locator('.graph-inspector h2')).toHaveText('Knowledge garden copy');
  await page.locator('.graph-inspector').getByTitle('Page actions', { exact: true }).click();
  await page.getByRole('button', { name: 'Move to trash', exact: true }).click();
  await page.locator('.graph-dialog').getByRole('button', { name: 'Move to trash', exact: true }).click();
  await expect(page.locator('.graph-inspector')).toHaveCount(0);
  await selectNote('unwritten idea');
  await page.getByRole('button', { name: 'Create page', exact: true }).click();
  await expect(page.locator('.graph-page')).toHaveCount(0);
  await enterGraph();
  expect((await graphState()).positions.some(node => node.id === 'missing:unwritten idea')).toBe(false);
  await selectNote('unwritten idea');
  await page.locator('.graph-inspector').getByTitle('Page actions', { exact: true }).click();
  await page.getByRole('button', { name: 'Move to trash', exact: true }).click();
  await page.locator('.graph-dialog').getByRole('button', { name: 'Move to trash', exact: true }).click();
  await expect(page.locator('.graph-inspector')).toHaveCount(0);
  await selectNote('Knowledge garden');
  await page.getByRole('button', { name: 'Open page', exact: true }).click();
  await expect(page.locator('.graph-page')).toHaveCount(0);
  await enterGraph();
  expect(errors).toEqual([]);
  console.log('Native Blazor smoke passed: previews, directions, scope, rename stability, duplicate/trash, unresolved creation, responsive inspector and open/back.');
} finally {
  await browser.close();
}