// Factory Boxes phase 1, the test Director on devlinux: open demo-desktop-soren through the local hosted Gateway.
//
// The way the Cockpit does it: ask the MAIN host for a one-time ticket (POST /boxes/<id>/desktop-ticket), then from a
// page on the main host navigate to the address it answers, on the DESKTOP host (a cross-site navigation). The desktop
// loads through the test Director's tunnel. Then a click, Alt+F2 (the Xfce run dialog), typed text, a screenshot, and
// Escape - so nothing is started in the owner's desktop.
//
// Usage: PW_CORE=<playwright-core> CHROME=<chromium> node open-desktop.cjs <main origin> <desktop origin> <key> <box id> <out dir>
const { chromium } = require(process.env.PW_CORE);
const fs = require('fs');

const [mainOrigin, desktopOrigin, key, boxId, out] = process.argv.slice(2);
const sleep = ms => new Promise(r => setTimeout(r, ms));
const redact = u => u.replace(/ticket=[^&]+/, 'ticket=<redacted>');
const log = [];
const say = s => { const line = `${new Date().toISOString()} ${s}`; log.push(line); console.log(line); };

(async () => {
  const browser = await chromium.launch({ headless: true, executablePath: process.env.CHROME });
  const ctx = await browser.newContext({ viewport: { width: 1280, height: 800 } });
  const page = await ctx.newPage();
  const responses = [], sockets = [];
  page.on('response', r => responses.push({ status: r.status(), url: redact(r.url()) }));
  page.on('websocket', ws => {
    const s = { url: ws.url(), received: 0, receivedBytes: 0 };
    sockets.push(s);
    ws.on('framereceived', f => { s.received++; s.receivedBytes += typeof f.payload === 'string' ? Buffer.byteLength(f.payload) : f.payload.length; });
  });

  const ticketResponse = await fetch(`${mainOrigin}/boxes/${boxId}/desktop-ticket`, { method: 'POST', headers: { Authorization: `Bearer ${key}` } });
  const ticket = await ticketResponse.json();
  say(`ticket: HTTP ${ticketResponse.status}, address on the desktop host: ${redact(ticket.url)}`);

  await page.goto(`${mainOrigin}/healthz`);
  await Promise.all([
    page.waitForURL(`${desktopOrigin}/boxes/${boxId}/desktop/`, { timeout: 30000 }),
    page.evaluate(u => { location.href = u; }, ticket.url),
  ]);
  say(`the browser is on ${page.url()}`);
  await sleep(12000);
  await page.screenshot({ path: `${out}/1-desktop.png` });
  const cookies = (await ctx.cookies()).map(c => ({ name: c.name, path: c.path, httpOnly: c.httpOnly, secure: c.secure, sameSite: c.sameSite }));
  say(`cookies: ${JSON.stringify(cookies)}; document.cookie=${JSON.stringify(await page.evaluate(() => document.cookie))}`);

  await page.mouse.click(640, 400);
  await page.keyboard.press('Alt+F2');
  await sleep(2000);
  await page.keyboard.type('typed-through-the-test-director', { delay: 40 });
  await sleep(2500);
  await page.screenshot({ path: `${out}/2-after-typing.png` });
  await page.keyboard.press('Escape');
  await sleep(1000);

  say(`responses: ${responses.length}, all 200: ${responses.every(r => r.status === 200)}; not 200: ${JSON.stringify(responses.filter(r => r.status !== 200))}`);
  say(`websockets: ${JSON.stringify(sockets)}`);
  await browser.close();
  fs.writeFileSync(`${out}/open-desktop.log`, log.join('\n') + '\n');
})().catch(e => { console.error(e); process.exit(1); });
