// CC Browser - Local Test Server for CAPTCHA fixture pages
// Serves static HTML from the fixtures directory on port 9999

import { createServer } from 'http';
import { readFileSync, existsSync } from 'fs';
import { join, extname, dirname } from 'path';
import { fileURLToPath } from 'url';

const __dirname = dirname(fileURLToPath(import.meta.url));

const MIME_TYPES = {
  '.html': 'text/html',
  '.js': 'application/javascript',
  '.mjs': 'application/javascript',
  '.css': 'text/css',
  '.json': 'application/json',
  '.png': 'image/png',
  '.jpg': 'image/jpeg',
  '.svg': 'image/svg+xml',
};

let server = null;

export function startServer(port = 9999) {
  return new Promise((resolve, reject) => {
    server = createServer((req, res) => {
      let pathname = req.url.split('?')[0];
      if (pathname === '/') pathname = '/no-captcha.html';

      const filePath = join(__dirname, pathname);

      if (!existsSync(filePath)) {
        res.writeHead(404, { 'Content-Type': 'text/plain' });
        res.end('Not found');
        return;
      }

      const ext = extname(filePath);
      const contentType = MIME_TYPES[ext] || 'application/octet-stream';

      try {
        const content = readFileSync(filePath);
        res.writeHead(200, { 'Content-Type': contentType });
        res.end(content);
      } catch (err) {
        res.writeHead(500, { 'Content-Type': 'text/plain' });
        res.end('Server error: ' + err.message);
      }
    });

    server.on('error', (err) => {
      if (err.code === 'EADDRINUSE') {
        // Port already in use - try to use it anyway (another test may have started it)
        resolve({ port, reused: true });
      } else {
        reject(err);
      }
    });

    server.listen(port, '127.0.0.1', () => {
      resolve({ port, reused: false });
    });
  });
}

export function stopServer() {
  return new Promise((resolve) => {
    if (!server) {
      resolve();
      return;
    }
    server.close(() => {
      server = null;
      resolve();
    });
  });
}

// Allow running standalone: node test-server.mjs
if (process.argv[1] && process.argv[1].includes('test-server')) {
  const port = parseInt(process.argv[2]) || 9999;
  startServer(port).then(({ port: p }) => {
    console.log(`Test server listening on http://127.0.0.1:${p}`);
    console.log('Serving fixture pages from:', __dirname);
    console.log('Press Ctrl+C to stop');
  });
}
