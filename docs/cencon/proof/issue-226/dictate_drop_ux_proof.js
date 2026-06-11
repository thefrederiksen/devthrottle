// Proof harness for issue #226 - Cockpit dictation failure UX + retry with preserved audio.
//
// Drives the REAL shipped src/CcDirector.Cockpit/wwwroot/js/cockpit-dictate.js (no
// reimplementation) inside headless Chromium. The mic graph is stubbed deterministically
// (AudioContext/AudioWorklet/getUserMedia) so a fixed number of PCM16 frames are produced,
// and window.WebSocket is replaced by a controllable fake whose open/started/error/close
// the test drives. This isolates the four acceptance criteria so each can be screenshotted:
//
//   1. No bare message      - a mid-stream drop shows the WS close code/reason.
//   2. Typed Director cause  - a typed {type:error} before close is surfaced as the cause.
//   3. Retry preserves audio - >= 24000 captured bytes -> Retry replays the buffered frames,
//                              the new socket RECEIVES them, and a transcript with the
//                              pre-drop words is produced.
//   4. Tiny clip cancels     - < 24000 captured bytes -> no Retry, clean cancel (no red ERROR).
//
// ASCII only. Run: node docs/cencon/proof/issue-226/dictate_drop_ux_proof.js
'use strict';

const http = require('http');
const fs = require('fs');
const path = require('path');

const PW = require(path.join(process.env.APPDATA, 'npm', 'node_modules', '@playwright', 'cli', 'node_modules', 'playwright'));
const { chromium } = PW;

const REPO = path.resolve(__dirname, '..', '..', '..', '..');
const JS_DIR = path.join(REPO, 'src', 'CcDirector.Cockpit', 'wwwroot', 'js');
const OUT = __dirname;
const PORT = 7473;

function readJs(name) { return fs.readFileSync(path.join(JS_DIR, name), 'utf8'); }

// Host page: loads the REAL cockpit-dictate.js, installs deterministic stubs, and exposes
// window.__h hooks the test drives. The fake mic posts frames of FRAME_SAMPLES int16 each
// every tick; the test decides how many frames to emit before forcing the drop.
const HOST_PAGE = `<!doctype html><html><head><meta charset="utf-8"><title>dictate proof</title></head>
<body><div id="root"></div>
<script>
  // ---- deterministic fake WebSocket (the page's 'new WebSocket(url)' uses this) ----
  window.__sockets = [];
  class FakeWebSocket {
    constructor(url) {
      this.url = url; this.readyState = 0; this.binaryType = 'blob';
      this.OPEN = 1; this.sent = []; this.binaryFramesReceived = 0; this.binaryBytesReceived = 0;
      this.onopen = this.onmessage = this.onerror = this.onclose = null;
      window.__sockets.push(this);
    }
    send(data) {
      this.sent.push(data);
      if (data instanceof ArrayBuffer) { this.binaryFramesReceived++; this.binaryBytesReceived += data.byteLength; }
    }
    close() { /* client-initiated close is a no-op for the test */ }
    // test drivers:
    _open() { this.readyState = 1; if (this.onopen) this.onopen({}); this._emit({ type: 'ready' }); }
    _emit(obj) { if (this.onmessage) this.onmessage({ data: JSON.stringify(obj) }); }
    _drop(code, reason) { this.readyState = 3; if (this.onclose) this.onclose({ code: code, reason: reason || '', wasClean: false }); }
  }
  FakeWebSocket.OPEN = 1;
  window.WebSocket = FakeWebSocket;

  // ---- deterministic fake mic graph (no real getUserMedia / AudioWorklet) ----
  const FRAME_SAMPLES = 2400; // 2400 int16 = 4800 bytes per frame at 24kHz ~= 100ms
  navigator.mediaDevices = navigator.mediaDevices || {};
  navigator.mediaDevices.getUserMedia = async () => ({ getTracks: () => [{ stop() {} }] });
  navigator.mediaDevices.enumerateDevices = async () => ([{ kind: 'audioinput', deviceId: 'fake', label: 'Fake Mic' }]);
  window.AudioWorkletNode = function (ctx, name) {
    this.port = { onmessage: null };
    this.connect = function () {};
    this.disconnect = function () {};
    // register so the test can pump frames into this node
    window.__pumpFrame = () => {
      const buf = new Int16Array(FRAME_SAMPLES).buffer;
      if (this.port.onmessage) this.port.onmessage({ data: buf });
    };
  };
  class FakeAudioContext {
    constructor() { this.audioWorklet = { addModule: async () => {} }; }
    createMediaStreamSource() { return { connect() {}, disconnect() {} }; }
    createAnalyser() { return { fftSize: 0, smoothingTimeConstant: 0, frequencyBinCount: 16, connect() {}, getByteFrequencyData() {} }; }
    close() {}
  }
  window.AudioContext = FakeAudioContext; window.webkitAudioContext = FakeAudioContext;

  // Fake DotNet ref: record which callback fired (Insert / Send / Cancel).
  window.__callbacks = [];
  window.__dotnetRef = { invokeMethodAsync: (m, a) => { window.__callbacks.push({ m: m, a: a }); return Promise.resolve(); } };
</script>
<script>__DICTATE_JS__</script>
<script>
  // Pump N frames into the live capture buffer, then return the latest socket.
  window.__pumpFrames = (n) => { for (let i = 0; i < n; i++) window.__pumpFrame(); };
  window.__lastSocket = () => window.__sockets[window.__sockets.length - 1];
  window.__startDialog = () => window.cockpitDictate.start(window.__dotnetRef, 'wss://example/dictate', '/js/dictate-worklet.js', 'default');
</script>
</body></html>`;

function serve() {
  const dictateJs = readJs('cockpit-dictate.js');
  const page = HOST_PAGE.replace('__DICTATE_JS__', dictateJs);
  return http.createServer((req, res) => {
    if (req.url === '/' || req.url.startsWith('/index')) {
      res.writeHead(200, { 'Content-Type': 'text/html; charset=utf-8' }); res.end(page); return;
    }
    res.writeHead(404); res.end('nope');
  }).listen(PORT);
}

const sleep = (ms) => new Promise(r => setTimeout(r, ms));

async function dialogText(page) {
  return await page.evaluate(() => {
    const t = document.querySelector('.cd-transcript');
    const s = document.querySelector('.cd-status');
    const retry = document.querySelector('.cd-retry');
    return {
      transcript: t ? t.value : null,
      status: s ? s.textContent : null,
      statusColor: s ? getComputedStyle(s).color : null,
      retryVisible: retry ? getComputedStyle(retry).display !== 'none' : false,
    };
  });
}

async function main() {
  const server = serve();
  const browser = await chromium.launch({ headless: true });
  const results = [];
  try {
    // ============ Scenario 1: mid-stream drop shows close code/reason (no bare message) ============
    {
      const page = await browser.newPage({ viewport: { width: 720, height: 560 } });
      await page.goto('http://127.0.0.1:' + PORT + '/');
      await page.evaluate(() => window.__startDialog());
      await sleep(150);
      // bring the socket to ready->started, pump enough audio to be recoverable (>=24000 bytes)
      await page.evaluate(() => { const w = window.__lastSocket(); w._open(); w._emit({ type: 'started' }); });
      await page.evaluate(() => window.__pumpFrames(8)); // 8 * 4800 = 38400 bytes
      await sleep(120);
      // force a mid-stream drop with a close code + reason, NO typed error
      await page.evaluate(() => window.__lastSocket()._drop(1011, 'provider stream closed'));
      await sleep(150);
      const d = await dialogText(page);
      await page.screenshot({ path: path.join(OUT, 'ac1-drop-code-reason.png') });
      const pass = d.status === 'ERROR' && /code 1011/.test(d.transcript) && /provider stream closed/.test(d.transcript) && d.transcript !== 'Connection closed.';
      results.push({ ac: 1, name: 'No bare message (close code/reason surfaced)', pass: pass, observed: d.transcript, retry: d.retryVisible });
      await page.close();
    }

    // ============ Scenario 2: typed Director cause surfaced ============
    {
      const page = await browser.newPage({ viewport: { width: 720, height: 560 } });
      await page.goto('http://127.0.0.1:' + PORT + '/');
      await page.evaluate(() => window.__startDialog());
      await sleep(150);
      await page.evaluate(() => { const w = window.__lastSocket(); w._open(); w._emit({ type: 'started' }); });
      await page.evaluate(() => window.__pumpFrames(8));
      await sleep(120);
      // Director sends a typed error (no API key) THEN closes - the dialog must show the cause.
      await page.evaluate(() => {
        const w = window.__lastSocket();
        w._emit({ type: 'error', message: 'Dictation is unavailable: no OpenAI key on the owning machine. Set one in Settings > Voice.' });
        w._drop(1011, '');
      });
      await sleep(150);
      const d = await dialogText(page);
      await page.screenshot({ path: path.join(OUT, 'ac2-typed-cause.png') });
      const pass = d.status === 'ERROR' && /no OpenAI key/.test(d.transcript) && d.transcript !== 'Connection closed.';
      results.push({ ac: 2, name: 'Typed Director cause surfaced', pass: pass, observed: d.transcript, retry: d.retryVisible });
      await page.close();
    }

    // ============ Scenario 3: Retry preserves audio ============
    {
      const page = await browser.newPage({ viewport: { width: 720, height: 560 } });
      await page.goto('http://127.0.0.1:' + PORT + '/');
      await page.evaluate(() => window.__startDialog());
      await sleep(150);
      await page.evaluate(() => { const w = window.__lastSocket(); w._open(); w._emit({ type: 'started' }); });
      await page.evaluate(() => window.__pumpFrames(8)); // 38400 bytes captured before drop
      await sleep(120);
      const preDropBytes = await page.evaluate(() => window.__lastSocket().binaryBytesReceived);
      await page.evaluate(() => window.__lastSocket()._drop(1011, 'provider stream closed'));
      await sleep(150);
      const dAfterDrop = await dialogText(page);
      await page.screenshot({ path: path.join(OUT, 'ac3a-drop-with-retry.png') });

      // Click Retry: a NEW socket opens; drive it ready->started and confirm the buffered audio
      // is REPLAYED (the new socket receives the pre-drop frames), then deliver a final transcript
      // containing the pre-drop words.
      await page.evaluate(() => document.querySelector('.cd-retry').click());
      await sleep(120);
      await page.evaluate(() => window.__pumpFrames(0)); // no new live frames needed
      await page.evaluate(() => { const w = window.__lastSocket(); w._open(); w._emit({ type: 'started' }); });
      await sleep(150);
      const replayBytes = await page.evaluate(() => window.__lastSocket().binaryBytesReceived);
      // simulate the user finishing: server returns a final transcript including the pre-drop words
      await page.evaluate(() => window.__lastSocket()._emit({ type: 'final', cleaned: 'the words spoken before the drop', raw: 'the words spoken before the drop' }));
      await sleep(150);
      const dFinal = await dialogText(page);
      await page.screenshot({ path: path.join(OUT, 'ac3b-retry-transcript.png') });
      const newSocketOpened = await page.evaluate(() => window.__sockets.length >= 2);
      const pass = dAfterDrop.retryVisible && newSocketOpened && replayBytes >= 24000 && /before the drop/.test(dFinal.transcript);
      results.push({ ac: 3, name: 'Retry replays buffered audio -> transcript with pre-drop words',
        pass: pass, preDropBytes: preDropBytes, replayBytesOnNewSocket: replayBytes,
        observedTranscript: dFinal.transcript });
      await page.close();
    }

    // ============ Scenario 4: sub-floor clip cancels cleanly (no Retry, no red ERROR) ============
    {
      const page = await browser.newPage({ viewport: { width: 720, height: 560 } });
      await page.goto('http://127.0.0.1:' + PORT + '/');
      await page.evaluate(() => window.__startDialog());
      await sleep(150);
      await page.evaluate(() => { const w = window.__lastSocket(); w._open(); w._emit({ type: 'started' }); });
      await page.evaluate(() => window.__pumpFrames(2)); // 2 * 4800 = 9600 bytes (< 24000 floor)
      await sleep(120);
      await page.evaluate(() => window.__lastSocket()._drop(1011, 'provider stream closed'));
      await sleep(150);
      const overlayGone = await page.evaluate(() => document.querySelector('.cd-overlay') === null);
      const cb = await page.evaluate(() => window.__callbacks.map(c => c.m));
      await page.screenshot({ path: path.join(OUT, 'ac4-subfloor-clean-cancel.png') });
      const pass = overlayGone && cb.includes('OnDictateCancel');
      results.push({ ac: 4, name: 'Sub-floor clip cancels cleanly (no Retry, no error)', pass: pass, overlayGone: overlayGone, callbacks: cb });
      await page.close();
    }
  } finally {
    await browser.close();
    server.close();
  }

  const allPass = results.every(r => r.pass);
  fs.writeFileSync(path.join(OUT, 'results.json'), JSON.stringify({ allPass: allPass, results: results }, null, 2));
  console.log(JSON.stringify({ allPass: allPass, results: results }, null, 2));
  process.exit(allPass ? 0 : 1);
}

main().catch(e => { console.error(e); process.exit(2); });
