// Factory Boxes phase 1, the desktop relay leg: what the browser sees when the box's Director goes away mid-use.
// Opens the desktop through the local test Gateway, waits until the stream is live, writes "ready" to <out>/director-gone.ready,
// and waits for the test to stop the Director. Records the close code and reason a WebSocket in the page receives.
// Usage: PW_CORE=... CHROME=... node director-gone.cjs <main origin> <desktop origin> <token> <box id> <out dir>
const { chromium } = require(process.env.PW_CORE);
const fs = require('fs');
const [mainOrigin, desktopOrigin, token, boxId, out] = process.argv.slice(2);
const sleep = ms => new Promise(r => setTimeout(r, ms));

(async () => {
  const browser = await chromium.launch({ headless: true, executablePath: process.env.CHROME });
  const page = await (await browser.newContext({ viewport: { width: 1280, height: 800 } })).newPage();
  const ticket = await (await fetch(`${mainOrigin}/boxes/${boxId}/desktop-ticket`, { method: 'POST', headers: { Authorization: `Bearer ${token}` } })).json();
  await page.goto(`${mainOrigin}/healthz`);
  await Promise.all([page.waitForURL(`${desktopOrigin}/boxes/${boxId}/desktop/`), page.evaluate(u => { location.href = u; }, ticket.url)]);
  await sleep(10000);
  // Selkies opens its stream socket inside a worker, out of the page's reach, so the page opens one more of its own
  // through the same relay - a real browser WebSocket, carrying the real box cookie - and records how it ends.
  await page.evaluate(() => new Promise(resolve => {
    window.__closes = [];
    const ws = new WebSocket(new URL('api/websockets', location.href).href.replace(/^http/, 'ws'));
    ws.onopen = () => resolve();
    ws.onclose = e => window.__closes.push({ url: ws.url, code: e.code, reason: e.reason, wasClean: e.wasClean, at: new Date().toISOString() });
  }));
  await page.screenshot({ path: `${out}/director-gone-1-before.png` });
  fs.writeFileSync(`${out}/director-gone.ready`, new Date().toISOString());
  const deadline = Date.now() + 120000;
  let closes = [];
  while (Date.now() < deadline) {
    closes = await page.evaluate(() => window.__closes);
    if (closes.length > 0) break;
    await sleep(500);
  }
  await sleep(2000);
  closes = await page.evaluate(() => window.__closes);
  await page.screenshot({ path: `${out}/director-gone-2-after.png` });
  fs.writeFileSync(`${out}/director-gone.json`, JSON.stringify({ closes }, null, 2));
  console.log(JSON.stringify(closes));
  await browser.close();
})().catch(e => { console.error(e); process.exit(1); });
