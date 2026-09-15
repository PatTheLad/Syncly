import { defineConfig } from '@playwright/test';

export default defineConfig({
  testDir: '.', testMatch: 'graph.spec.mjs', timeout: 30000, workers: 1,
  use: { baseURL: 'http://127.0.0.1:4179', viewport: { width: 1440, height: 900 }, screenshot: 'only-on-failure' },
  webServer: { command: 'node server.mjs', url: 'http://127.0.0.1:4179', reuseExistingServer: !process.env.CI },
});