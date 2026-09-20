// The phase 5 screenshot driver: opens the real phone application in a real browser, walks the New
// Session flow to the repository step, and writes a PNG of it.
//
// It drives Chrome over the DevTools protocol with Node's own WebSocket - no browser automation
// package is installed on this machine, and installing one to take a picture would be a change to the
// machine this proof has no business making.
//
// Run it with:
//   node docs/missions/one-repository-list/proofs/phase-5/shoot.mjs <url> <output.png>
// It expects the stub Gateway and the phone's development server to be up already; see README.md in
// this folder for the whole sequence.
import { spawn } from "node:child_process";
import { mkdtempSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";

const url = process.argv[2];
const output = process.argv[3];
if (!url || !output) {
  console.error("usage: node shoot.mjs <url> <output.png>");
  process.exit(2);
}

const CHROME = "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome";
const DEBUG_PORT = 9333;
// A phone, not a desktop window squeezed narrow: this is the iPhone viewport the shell is tuned for.
const WIDTH = 390;
const HEIGHT = 844;

const profile = mkdtempSync(join(tmpdir(), "phase5-chrome-"));
const chrome = spawn(CHROME, [
  "--headless=new",
  `--remote-debugging-port=${DEBUG_PORT}`,
  `--user-data-dir=${profile}`,
  `--window-size=${WIDTH},${HEIGHT}`,
  "--hide-scrollbars",
  "--no-first-run",
  "--no-default-browser-check",
  "about:blank",
], { stdio: "ignore" });

const sleep = (ms) => new Promise((done) => setTimeout(done, ms));

async function webSocketUrl() {
  for (let attempt = 0; attempt < 50; attempt++) {
    try {
      const response = await fetch(`http://127.0.0.1:${DEBUG_PORT}/json/list`);
      const targets = await response.json();
      const page = targets.find((target) => target.type === "page");
      if (page?.webSocketDebuggerUrl) return page.webSocketDebuggerUrl;
    } catch {
      // The browser is still starting; the loop below is the wait.
    }
    await sleep(200);
  }
  throw new Error("Chrome never opened a debugging page");
}

class Devtools {
  constructor(socket) {
    this.socket = socket;
    this.nextId = 1;
    this.pending = new Map();
    socket.addEventListener("message", (event) => {
      const message = JSON.parse(String(event.data));
      const waiting = this.pending.get(message.id);
      if (!waiting) return;
      this.pending.delete(message.id);
      if (message.error) waiting.fail(new Error(JSON.stringify(message.error)));
      else waiting.done(message.result);
    });
  }

  send(method, params = {}) {
    const id = this.nextId++;
    this.socket.send(JSON.stringify({ id, method, params }));
    return new Promise((done, fail) => this.pending.set(id, { done, fail }));
  }

  /** Evaluate an expression in the page and hand back its value, failing loudly on a thrown one. */
  async evaluate(expression) {
    const result = await this.send("Runtime.evaluate", {
      expression,
      awaitPromise: true,
      returnByValue: true,
    });
    if (result.exceptionDetails) {
      throw new Error("page threw: " + JSON.stringify(result.exceptionDetails));
    }
    return result.result.value;
  }

  /** Click the first element whose accessible label matches, failing loudly when there is none. */
  async click(label) {
    const clicked = await this.evaluate(`(() => {
      const button = [...document.querySelectorAll('button')]
        .find((candidate) => (candidate.getAttribute('aria-label') || candidate.textContent || '').trim().startsWith(${JSON.stringify(label)}));
      if (!button) return false;
      button.click();
      return true;
    })()`);
    if (!clicked) throw new Error(`nothing on the page is labelled ${label}`);
    await sleep(300);
  }
}

const socket = new WebSocket(await webSocketUrl());
await new Promise((open, fail) => {
  socket.addEventListener("open", open, { once: true });
  socket.addEventListener("error", fail, { once: true });
});
const page = new Devtools(socket);

await page.send("Page.enable");
await page.send("Runtime.enable");
await page.send("Emulation.setDeviceMetricsOverride", {
  width: WIDTH,
  height: HEIGHT,
  deviceScaleFactor: 2,
  mobile: true,
});

// Land on the shell once so localStorage belongs to the right origin, enrol a device so the phone's
// auth gate lets the New Session route render, then open that route for real.
await page.send("Page.navigate", { url: new URL("/mobile/", url).toString() });
await sleep(1500);
await page.evaluate(`(() => {
  localStorage.setItem('cc.accounts', JSON.stringify([
    { id: 'proof', label: 'soren@centerconsulting.com', deviceKey: 'proof-device-key', installId: 'proof-install' },
  ]));
  localStorage.setItem('cc.activeAccount', 'proof');
  return true;
})()`);
await page.send("Page.navigate", { url: new URL("/mobile/new", url).toString() });
await sleep(2500);

await page.click("Select Director Sorens Mac mini");
await page.click("Select agent Claude Code");
await sleep(1200);

// An optional third argument types a search, so the searched view can be shot as well as the default.
const search = process.argv[4];
if (search) {
  await page.evaluate(`(() => {
    const input = document.getElementById('newsession-repository-search');
    const setter = Object.getOwnPropertyDescriptor(window.HTMLInputElement.prototype, 'value').set;
    setter.call(input, ${JSON.stringify(search)});
    input.dispatchEvent(new Event('input', { bubbles: true }));
    return true;
  })()`);
  await sleep(800);
}

// Say what is on screen, in order, so the picture is not the only evidence.
const onScreen = await page.evaluate(`JSON.stringify([...document.querySelectorAll('button[aria-label^="Select repository "]')]
  .map((button) => button.getAttribute('aria-label').replace('Select repository ', '')))`);
console.log("repositories on screen, top to bottom: " + onScreen);

const shot = await page.send("Page.captureScreenshot", { format: "png", captureBeyondViewport: false });
writeFileSync(output, Buffer.from(shot.data, "base64"));
console.log("wrote " + output);

socket.close();
chrome.kill();
