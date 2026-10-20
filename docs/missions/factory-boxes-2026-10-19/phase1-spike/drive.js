// Drives the Selkies desktop through the prefix proxy and records every channel.
const { chromium } = require(process.env.PW_CORE);
const fs = require('fs');
const url = process.argv[2], out = process.argv[3], tag = process.argv[4] || 'run';
(async () => {
  const browser = await chromium.launch({ headless: true, executablePath: process.env.CHROME, args: ['--autoplay-policy=no-user-gesture-required', '--use-fake-ui-for-media-stream'] });
  const ctx = await browser.newContext({ viewport: { width: 1280, height: 800 } });
  const page = await ctx.newPage();
  const reqs = [], sockets = [], consoleLines = [];
  page.on('console', m => consoleLines.push(`${m.type()}: ${m.text()}`));
  ctx.on('request', r => reqs.push({ method: r.method(), url: r.url(), type: r.resourceType() }));
  ctx.on('requestfinished', async r => { const res = await r.response(); const e = reqs.find(x => x.url === r.url() && x.status === undefined); if (e && res) e.status = res.status(); });
  ctx.on('requestfailed', r => { const e = reqs.find(x => x.url === r.url() && x.status === undefined); if (e) e.status = 'FAILED ' + (r.failure() || {}).errorText; });
  const hook = ws => { const s = { url: ws.url(), sent: 0, recv: 0, sentBytes: 0, recvBytes: 0, maxRecv: 0, maxSent: 0, closed: false, firstSent: [] };
    sockets.push(s);
    ws.on('framesent', f => { const n = typeof f.payload === 'string' ? Buffer.byteLength(f.payload) : f.payload.length; s.sent++; s.sentBytes += n; s.maxSent = Math.max(s.maxSent, n); if (s.firstSent.length < 6 && typeof f.payload === 'string') s.firstSent.push(f.payload.slice(0, 80)); });
    ws.on('framereceived', f => { const n = typeof f.payload === 'string' ? Buffer.byteLength(f.payload) : f.payload.length; s.recv++; s.recvBytes += n; s.maxRecv = Math.max(s.maxRecv, n); });
    ws.on('close', () => s.closed = true); };
  page.on('websocket', hook);
  // A dedicated worker may own the stream socket; catch those too.
  page.on('worker', w => consoleLines.push('worker started: ' + w.url()));
  await page.goto(url, { waitUntil: 'load' });
  await page.waitForTimeout(12000);
  await page.screenshot({ path: `${out}/${tag}-1-desktop.png` });
  // Click the Xfce "Applications" menu (top-left of the panel) and screenshot the opened menu.
  await page.mouse.click(30, 12);
  await page.waitForTimeout(2500);
  await page.screenshot({ path: `${out}/${tag}-2-after-click.png` });
  await page.keyboard.press('Escape');
  await page.waitForTimeout(800);
  // Open the run dialog with a keystroke chord and type into it.
  await page.mouse.click(640, 400);
  await page.keyboard.press('Alt+F2');
  await page.waitForTimeout(2000);
  await page.keyboard.type('typed-through-the-prefix', { delay: 40 });
  await page.waitForTimeout(2000);
  await page.screenshot({ path: `${out}/${tag}-3-after-typing.png` });
  await page.keyboard.press('Escape');
  await page.waitForTimeout(1000);
  const info = await page.evaluate(() => ({ href: location.href, cookies: document.cookie, title: document.title, sw: !!(navigator.serviceWorker && navigator.serviceWorker.controller) }));
  fs.writeFileSync(`${out}/${tag}-capture.json`, JSON.stringify({ page: info, requests: reqs, websockets: sockets, console: consoleLines }, null, 2));
  await browser.close();
})().catch(e => { console.error(e); process.exit(1); });
