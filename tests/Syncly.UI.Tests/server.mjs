import http from 'node:http';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { readFile } from 'node:fs/promises';

const root = fileURLToPath(new URL('../../', import.meta.url));
const types = { '.html': 'text/html', '.mjs': 'text/javascript', '.js': 'text/javascript', '.css': 'text/css', '.svg': 'image/svg+xml' };
const server = http.createServer(async (request, response) => {
  try {
    const pathname = decodeURIComponent(new URL(request.url, 'http://localhost').pathname);
    const relative = pathname === '/' ? 'tests/Syncly.UI.Tests/graph.html' : pathname.slice(1);
    const filename = path.resolve(root, relative);
    if (!filename.startsWith(root)) { response.writeHead(403).end(); return; }
    const content = await readFile(filename);
    response.writeHead(200, { 'Content-Type': types[path.extname(filename)] || 'application/octet-stream', 'Cache-Control': 'no-store' });
    response.end(content);
  } catch { response.writeHead(404).end(); }
});
server.listen(Number(process.env.PORT || 4179), '127.0.0.1', () => console.log(`Graph fixture: http://127.0.0.1:${server.address().port}`));