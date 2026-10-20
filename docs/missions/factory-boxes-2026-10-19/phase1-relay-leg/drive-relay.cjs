// Factory Boxes phase 1, the desktop relay leg: the local end-to-end proof.
//
// Drives a real headless Chromium through a local test Gateway (built from the branch) and a scratch test Director,
// to the proof's own webtop desktop, the way a person will: the Cockpit (here: this script, signed in with the test
// Gateway's token) asks the MAIN host for a ticket, a page on the main host navigates to the ticket address on the
// DESKTOP host (a cross-site navigation, which is what the SameSite=Strict box cookie must survive), and the desktop
// loads, streams, takes a click and typing, uploads 64 megabytes and downloads a file - all through
// /boxes/<id>/desktop/ on the desktop host.
//
// Then it measures what ordinary use costs (idle, typing in a terminal, browsing a site, and the worst case: a
// full-screen flood) in bytes toward the browser, and how long a Gateway-to-Director command takes on the same
// tunnel while the desktop floods.
//
// Usage: PW_CORE=<playwright-core> CHROME=<chromium> node drive-relay.cjs <main origin> <desktop origin> <token> <box id> <director id> <out dir>
const { chromium } = require(process.env.PW_CORE);
const fs = require('fs');

const [mainOrigin, desktopOrigin, token, boxId, directorId, out] = process.argv.slice(2);
const sleep = ms => new Promise(r => setTimeout(r, ms));
const log = [];
const say = s => { const line = `${new Date().toISOString()} ${s}`; log.push(line); console.log(line); };

async function command(label) {
  // One Gateway-to-Director round trip on the tunnel: the director-level backfill-numbers verb.
  const t0 = performance.now();
  const r = await fetch(`${mainOrigin}/directors/${directorId}/backfill-numbers`, { method: 'POST', headers: { Authorization: `Bearer ${token}` } });
  await r.text();
  return { label, status: r.status, ms: +(performance.now() - t0).toFixed(1) };
}

(async () => {
  const browser = await chromium.launch({ headless: true, executablePath: process.env.CHROME });
  const ctx = await browser.newContext({ viewport: { width: 1280, height: 800 } });
  const page = await ctx.newPage();
  const requests = [], sockets = [], consoleLines = [];
  page.on('console', m => consoleLines.push(`${m.type()}: ${m.text()}`));
  ctx.on('request', r => requests.push({ method: r.method(), url: r.url().replace(/ticket=[^&]+/, 'ticket=<redacted>'), type: r.resourceType() }));
  ctx.on('requestfinished', async r => { const res = await r.response(); const u = r.url().replace(/ticket=[^&]+/, 'ticket=<redacted>'); const e = requests.find(x => x.url === u && x.status === undefined); if (e && res) e.status = res.status(); });
  page.on('websocket', ws => {
    const s = { url: ws.url(), recv: 0, recvBytes: 0, sent: 0, sentBytes: 0, maxRecv: 0, closed: false };
    sockets.push(s);
    ws.on('framereceived', f => { const n = typeof f.payload === 'string' ? Buffer.byteLength(f.payload) : f.payload.length; s.recv++; s.recvBytes += n; s.maxRecv = Math.max(s.maxRecv, n); });
    ws.on('framesent', f => { const n = typeof f.payload === 'string' ? Buffer.byteLength(f.payload) : f.payload.length; s.sent++; s.sentBytes += n; });
    ws.on('close', () => s.closed = true);
  });
  const received = () => sockets.reduce((a, s) => a + s.recvBytes, 0);

  // 1. The Cockpit asks the main host for a ticket.
  const ticketResponse = await fetch(`${mainOrigin}/boxes/${boxId}/desktop-ticket`, { method: 'POST', headers: { Authorization: `Bearer ${token}` } });
  const ticket = await ticketResponse.json();
  say(`ticket: ${ticketResponse.status}, address on the desktop host: ${ticket.url.replace(/ticket=[^&]+/, 'ticket=<redacted>')}`);

  // 2. A page on the MAIN host opens it - a cross-site navigation to the desktop host.
  await page.goto(`${mainOrigin}/healthz`);
  await Promise.all([page.waitForURL(`${desktopOrigin}/boxes/${boxId}/desktop/`, { timeout: 30000 }), page.evaluate(u => { location.href = u; }, ticket.url)]);
  say(`the browser is on ${page.url()}`);
  await sleep(12000);
  await page.screenshot({ path: `${out}/relay-1-desktop.png` });
  const pageFacts = await page.evaluate(() => ({ href: location.href, documentCookie: document.cookie, title: document.title }));
  const cookies = (await ctx.cookies()).map(c => ({ name: c.name, domain: c.domain, path: c.path, httpOnly: c.httpOnly, secure: c.secure, sameSite: c.sameSite }));
  say(`page facts: ${JSON.stringify(pageFacts)}; cookies: ${JSON.stringify(cookies)}`);

  // 3. A click: the Applications menu.
  await page.mouse.click(30, 12);
  await sleep(2500);
  await page.screenshot({ path: `${out}/relay-2-after-click.png` });
  await page.keyboard.press('Escape');
  await sleep(800);

  // 4. Typing: the run dialog, and text into it.
  await page.mouse.click(640, 400);
  await page.keyboard.press('Alt+F2');
  await sleep(2000);
  await page.keyboard.type('typed-through-the-gateway-relay', { delay: 40 });
  await sleep(2000);
  await page.screenshot({ path: `${out}/relay-3-after-typing.png` });
  await page.keyboard.press('Escape');
  await sleep(1000);

  // 5. Upload 64 megabytes (the most one request may carry) and a smaller file; read the file list; download one back.
  const files = await page.evaluate(async () => {
    const send = async (name, size) => {
      const body = new Uint8Array(size); for (let i = 0; i < size; i++) body[i] = (i * 7 + 3) % 256;
      const t0 = performance.now();
      const r = await fetch(new URL('api/upload', location.href), { method: 'POST', headers: { 'Content-Type': 'application/octet-stream', 'X-Upload-Path': encodeURIComponent(name) }, body });
      return { name, size, status: r.status, ms: Math.round(performance.now() - t0) };
    };
    const big = await send('relay-upload-64mb.bin', 64 * 1024 * 1024);
    const small = await send('relay-upload-8mb.bin', 8 * 1024 * 1024);
    const list = await fetch(new URL('api/files/', location.href)); const listText = await list.text();
    const t0 = performance.now();
    const dl = await fetch(new URL('api/files/relay-upload-8mb.bin', location.href)); const bytes = new Uint8Array(await dl.arrayBuffer());
    let mismatch = -1; for (let i = 0; i < bytes.length; i++) if (bytes[i] !== (i * 7 + 3) % 256) { mismatch = i; break; }
    return { big, small, list: { status: list.status, hasBoth: listText.includes('relay-upload-64mb.bin') && listText.includes('relay-upload-8mb.bin') },
      download: { status: dl.status, bytes: bytes.length, firstMismatch: mismatch, ms: Math.round(performance.now() - t0) } };
  });
  say(`files: ${JSON.stringify(files)}`);

  // 6. What ordinary use costs, toward the browser. Each window is 60 seconds.
  const measure = async (label, act) => {
    const before = received(); const t0 = Date.now();
    await act();
    const bytes = received() - before; const seconds = (Date.now() - t0) / 1000;
    const r = { label, seconds: +seconds.toFixed(1), bytes, megabitsPerSecond: +((bytes * 8) / seconds / 1e6).toFixed(3), gigabytesPerHour: +((bytes / seconds) * 3600 / 1e9).toFixed(3) };
    say(`measure: ${JSON.stringify(r)}`);
    return r;
  };
  const usage = [];
  usage.push(await measure('idle desktop', () => sleep(60000)));

  // A terminal, and typing in it at a person's pace.
  await page.mouse.click(640, 400);
  await page.keyboard.press('Alt+F2'); await sleep(1500);
  await page.keyboard.type('xfce4-terminal --maximize', { delay: 30 }); await page.keyboard.press('Enter');
  await sleep(5000);
  usage.push(await measure('typing in a terminal', async () => {
    const end = Date.now() + 60000;
    while (Date.now() < end) { await page.keyboard.type('echo the quick brown fox jumps over the lazy dog', { delay: 120 }); await page.keyboard.press('Enter'); await sleep(1000); }
  }));
  await page.screenshot({ path: `${out}/relay-4-terminal.png` });

  // Browsing a site in the box's own Chromium, scrolling the page.
  await page.keyboard.press('Alt+F2'); await sleep(1500);
  await page.keyboard.type('chromium --no-first-run --start-maximized https://en.wikipedia.org/wiki/Linux', { delay: 30 }); await page.keyboard.press('Enter');
  await sleep(15000);
  usage.push(await measure('browsing a site (scrolling a Wikipedia article)', async () => {
    const end = Date.now() + 60000;
    while (Date.now() < end) { await page.mouse.move(640, 450); await page.mouse.wheel(0, 400); await sleep(1500); }
  }));
  await page.screenshot({ path: `${out}/relay-5-browsing.png` });
  await page.keyboard.press('Control+w'); await sleep(2000);

  // The worst case: a full-screen terminal flooding text. This is where the 8-megabit ceiling bites.
  await page.keyboard.press('Alt+F2'); await sleep(1500);
  await page.keyboard.type('xfce4-terminal --maximize -x sh -c "while true; do cat /usr/share/dict/words 2>/dev/null || ls -laR /usr; done"', { delay: 20 });
  await page.keyboard.press('Enter');
  await sleep(4000);

  // 7. Commands on the same tunnel: ten while the flood runs, against ten once the desktop is quiet again.
  const during = [];
  const floodMeasure = measure('worst case: a full-screen terminal flood', async () => { await sleep(30000); });
  for (let i = 0; i < 10; i++) { await sleep(2000); during.push(await command('while the desktop floods')); }
  usage.push(await floodMeasure);
  await page.screenshot({ path: `${out}/relay-6-flood.png` });
  await page.keyboard.press('Control+c'); await sleep(500);
  await page.keyboard.press('Alt+F4'); await sleep(3000);
  const quiet = [];
  for (let i = 0; i < 10; i++) { await sleep(1000); quiet.push(await command('desktop quiet')); }
  say(`commands while flooding: ${JSON.stringify(during.map(c => c.ms))}; quiet: ${JSON.stringify(quiet.map(c => c.ms))}`);

  // 8. Close the tab: the socket closes through the relay.
  await page.close();
  await sleep(2000);
  fs.writeFileSync(`${out}/relay-capture.json`, JSON.stringify({ pageFacts, cookies, files, usage, commands: { during, quiet }, sockets, requests, console: consoleLines.slice(0, 200), log }, null, 2));
  await browser.close();
})().catch(e => { console.error(e); process.exit(1); });
