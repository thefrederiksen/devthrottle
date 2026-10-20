// Takes the Boxes screen's QA screenshots (Factory Boxes mission, phase 1) by driving headless Chrome over the DevTools
// protocol. Needs: the fake Gateway (fake-gateway.mjs) on GATEWAY, the Cockpit dev server proxying to it on COCKPIT,
// and Chrome started with --remote-debugging-port=DEVTOOLS. Writes PNG files and results.json beside this script.
import { writeFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

const here = dirname(fileURLToPath(import.meta.url));
const GATEWAY = process.env.GATEWAY ?? "http://127.0.0.1:7979";
const COCKPIT = process.env.COCKPIT ?? "http://127.0.0.1:5173";
const DEVTOOLS = process.env.DEVTOOLS ?? "http://127.0.0.1:9333";
const DESKTOP = { width: 1440, height: 900, deviceScaleFactor: 1, mobile: false };
const PHONE = { width: 390, height: 844, deviceScaleFactor: 2, mobile: true };

const results = [];

async function connect() {
  const page = await (await fetch(`${DEVTOOLS}/json/new?about:blank`, { method: "PUT" })).json();
  const ws = new WebSocket(page.webSocketDebuggerUrl);
  await new Promise((resolve) => ws.addEventListener("open", resolve, { once: true }));
  let id = 0;
  const waiting = new Map();
  ws.addEventListener("message", (event) => {
    const msg = JSON.parse(event.data);
    if (msg.id !== undefined && waiting.has(msg.id)) {
      const { resolve, reject } = waiting.get(msg.id);
      waiting.delete(msg.id);
      if (msg.error) reject(new Error(JSON.stringify(msg.error)));
      else resolve(msg.result);
    }
  });
  const send = (method, params = {}) =>
    new Promise((resolve, reject) => {
      id += 1;
      waiting.set(id, { resolve, reject });
      ws.send(JSON.stringify({ id, method, params }));
    });
  return { send, close: () => ws.close(), targetId: page.id };
}

async function evaluate(cdp, expression) {
  const r = await cdp.send("Runtime.evaluate", { expression, awaitPromise: true, returnByValue: true });
  return r.result.value;
}

// A click as a person makes it: Chrome lets a page open a tab only inside a user's gesture.
async function clickOpen(cdp) {
  await cdp.send("Runtime.evaluate", {
    expression: "document.querySelector('[data-testid=box-open]').click(); true",
    userGesture: true,
  });
}

async function waitFor(cdp, expression, what) {
  for (let i = 0; i < 100; i += 1) {
    if (await evaluate(cdp, expression)) return;
    await new Promise((r) => setTimeout(r, 100));
  }
  const shot = await cdp.send("Page.captureScreenshot", { format: "png" });
  writeFileSync(join(here, "..", "failed-wait.png"), Buffer.from(shot.data, "base64"));
  throw new Error(`timed out waiting for ${what}; the page read: ${(await evaluate(cdp, "document.body.innerText")).slice(0, 600)}`);
}

async function setMode(mode) {
  await fetch(`${GATEWAY}/__qa/mode`, { method: "POST", body: JSON.stringify(mode) });
}

async function load(cdp, viewport, path) {
  await cdp.send("Emulation.setDeviceMetricsOverride", viewport);
  await cdp.send("Page.navigate", { url: `${COCKPIT}/history` });
  await waitFor(cdp, "document.readyState === 'complete'", "the first load");
  // This browser is enrolled: the legacy single key, which the account store takes up as the active account. At phone
  // width the Cockpit's rail is collapsed to its icons (its own remembered choice), as a person on a phone would have it.
  await evaluate(cdp, "localStorage.setItem('cc.deviceKey', 'qa-device-key'); true");
  await evaluate(cdp, `localStorage.setItem('cockpit.railCollapsed', '${viewport.mobile ? "true" : "false"}'); true`);
  await cdp.send("Page.navigate", { url: `${COCKPIT}${path}` });
  await waitFor(cdp, "document.readyState === 'complete'", path);
}

async function shoot(cdp, name, note) {
  await new Promise((r) => setTimeout(r, 400));
  const shot = await cdp.send("Page.captureScreenshot", { format: "png" });
  writeFileSync(join(here, `${name}.png`), Buffer.from(shot.data, "base64"));
  results.push({ name, note });
  console.log(`[shoot] ${name}.png - ${note}`);
}

const rows = "document.querySelectorAll('[data-testid=box-row]').length";
const railHasBoxes = "!!document.querySelector(\".nav a[href='/boxes']\")";

const cdp = await connect();
await cdp.send("Page.enable");
await cdp.send("Runtime.enable");

for (const [label, viewport] of [["desktop", DESKTOP], ["phone", PHONE]]) {
  await setMode({ flag: true, list: "two", ticket: "ok" });
  await load(cdp, viewport, "/boxes");
  await waitFor(cdp, `${rows} === 2`, "two rows");
  await shoot(cdp, `list-${label}`, `flag on, two boxes, ${label} width`);

  await setMode({ flag: true, list: "empty" });
  await load(cdp, viewport, "/boxes");
  await waitFor(cdp, "document.body.innerText.includes('No Director in your account is hosting a desktop')", "the empty state");
  await shoot(cdp, `empty-${label}`, `flag on, count 0, ${label} width`);

  await setMode({ flag: true, list: "error" });
  await load(cdp, viewport, "/boxes");
  await waitFor(cdp, "document.body.innerText.includes('The Gateway is starting up and cannot list boxes yet.')", "the error banner");
  await shoot(cdp, `error-${label}`, `flag on, GET /boxes answers 503 with the Gateway's sentence, ${label} width`);
}

// The rail, flag on and off.
await setMode({ flag: true, list: "two" });
await load(cdp, DESKTOP, "/history");
await waitFor(cdp, railHasBoxes, "the Boxes entry");
const workOn = await evaluate(cdp, "Array.from(document.querySelectorAll('[data-testid=nav-work] .nav-link-label')).map(e => e.textContent)");
await shoot(cdp, "nav-flag-on", `flag on: Work reads ${workOn.join(", ")}`);

await setMode({ flag: false });
await load(cdp, DESKTOP, "/history");
await waitFor(cdp, "document.querySelectorAll('[data-testid=nav-work] .nav-link-label').length > 0", "the rail");
await new Promise((r) => setTimeout(r, 800));
const workOff = await evaluate(cdp, "Array.from(document.querySelectorAll('[data-testid=nav-work] .nav-link-label')).map(e => e.textContent)");
if (await evaluate(cdp, railHasBoxes)) throw new Error("the Boxes entry is drawn with the flag off");
await shoot(cdp, "nav-flag-off", `flag off: Work reads ${workOff.join(", ")}`);

await load(cdp, DESKTOP, "/boxes");
await waitFor(cdp, "document.body.innerText.includes('Page not found')", "Page not found");
await shoot(cdp, "boxes-flag-off", "flag off: /boxes, loaded by its address, is the ordinary Page not found");

// Open: a failed ticket closes the tab and shows the Gateway's sentence on the row.
await setMode({ flag: true, list: "two", ticket: "error" });
await load(cdp, DESKTOP, "/boxes");
await waitFor(cdp, `${rows} === 2`, "two rows");
const tabsBefore = (await (await fetch(`${DEVTOOLS}/json/list`)).json()).filter((t) => t.type === "page").length;
await clickOpen(cdp);
await waitFor(
  cdp,
  "(document.querySelector('[data-testid=box-error]')?.textContent ?? '').includes('No connected Director in your account hosts a desktop for that box.')",
  "the row's error with the Gateway's sentence",
);
await new Promise((r) => setTimeout(r, 300));
const tabsAfterFailure = (await (await fetch(`${DEVTOOLS}/json/list`)).json()).filter((t) => t.type === "page").length;
await shoot(cdp, "open-ticket-error", `ticket refused: the tab was closed (pages before ${tabsBefore}, after ${tabsAfterFailure}) and the row shows the Gateway's sentence`);

// Open: a good ticket sends the new tab to the Gateway's url, verbatim.
await setMode({ ticket: "ok" });
await load(cdp, DESKTOP, "/boxes");
await waitFor(cdp, `${rows} === 2`, "two rows");
await clickOpen(cdp);
let opened = null;
for (let i = 0; i < 50 && opened === null; i += 1) {
  await new Promise((r) => setTimeout(r, 100));
  const pages = (await (await fetch(`${DEVTOOLS}/json/list`)).json()).filter((t) => t.type === "page");
  opened = pages.find((t) => t.url.includes("/__qa/desktop/")) ?? null;
}
if (opened === null) throw new Error("the new tab never reached the ticket's address");
results.push({ name: "open-ok", note: `the new tab is at ${opened.url}` });
console.log(`[shoot] open-ok - the new tab is at ${opened.url}`);

writeFileSync(join(here, "results.json"), `${JSON.stringify({ gateway: GATEWAY, cockpit: COCKPIT, results }, null, 2)}\n`);
cdp.close();
