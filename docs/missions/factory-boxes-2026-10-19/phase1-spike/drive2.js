// Second pass: a large screen in heavy motion (frame sizes), plus the upload and file-browser channels.
const { chromium } = require(process.env.PW_CORE);
const fs = require('fs');
const url = process.argv[2], out = process.argv[3], tag = process.argv[4] || 'motion';
(async () => {
  const browser = await chromium.launch({ headless: true, executablePath: process.env.CHROME, args: ['--autoplay-policy=no-user-gesture-required'] });
  const ctx = await browser.newContext({ viewport: { width: 1920, height: 1080 } });
  const page = await ctx.newPage();
  const sizes = [], http = [];
  ctx.on('requestfinished', async r => { const res = await r.response(); if (!r.url().startsWith('blob:')) http.push(`${r.method()} ${r.url()} -> ${res ? res.status() : '?'}`); });
  page.on('websocket', ws => ws.on('framereceived', f => sizes.push(typeof f.payload === 'string' ? Buffer.byteLength(f.payload) : f.payload.length)));
  await page.goto(url, { waitUntil: 'load' });
  await page.waitForTimeout(10000);
  // A terminal flooding random text repaints most of the screen every frame.
  await page.mouse.click(960, 540);
  await page.keyboard.press('Alt+F2');
  await page.waitForTimeout(1500);
  await page.keyboard.type(`xfce4-terminal --maximize -x sh -c "while true; do head -c 20000 /dev/urandom | base64; done"`, { delay: 5 });
  await page.keyboard.press('Enter');
  await page.waitForTimeout(4000);
  const before = sizes.length;
  await page.waitForTimeout(10000);
  await page.screenshot({ path: `${out}/${tag}-terminal-flood.png` });
  const motion = sizes.slice(before).sort((a, b) => a - b);
  // Upload lands in the box's Desktop folder, which is the file browser's root.
  // Upload: the page's own upload call is a POST of the raw file to ./api/upload.
  const upload = await page.evaluate(async () => {
    const body = new Uint8Array(1024 * 1024); crypto.getRandomValues(body.subarray(0, 65536));
    const r = await fetch(new URL('api/upload', location.href), { method: 'POST', headers: { 'Content-Type': 'application/octet-stream', 'X-Upload-Path': encodeURIComponent('spike-upload.bin') }, body });
    return `${r.status} ${(await r.text()).slice(0, 120)}`;
  });
  const files = await page.evaluate(async () => { const r = await fetch(new URL('api/files/', location.href)); const t = await r.text(); return { status: r.status, hasUpload: t.includes('spike-upload.bin'), head: t.slice(0, 300) }; });
  const download = await page.evaluate(async () => { const r = await fetch(new URL('api/files/spike-upload.bin', location.href)); const b = await r.arrayBuffer(); return `${r.status} ${b.byteLength} bytes`; });
  const pct = p => motion[Math.min(motion.length - 1, Math.floor(motion.length * p))];
  fs.writeFileSync(`${out}/${tag}-capture.json`, JSON.stringify({
    viewport: '1920x1080',
    motionWindowSeconds: 10, motionFrames: motion.length, motionBytes: motion.reduce((a, b) => a + b, 0),
    frameBytes: { p50: pct(0.5), p90: pct(0.9), p99: pct(0.99), max: motion[motion.length - 1] },
    framesOver48KiB: motion.filter(n => n > 49152).length,
    allFramesMax: Math.max(...sizes),
    upload, files, download, http }, null, 2));
  await page.keyboard.press('Control+c');
  await browser.close();
})().catch(e => { console.error(e); process.exit(1); });
