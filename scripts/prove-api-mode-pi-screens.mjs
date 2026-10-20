// The screens of the API mode proof (mission #4070, section 7, item 4), taken from the REAL Cockpit and phone app that
// the proof's test Gateway serves (built with -p:BuildCockpit=true -p:BuildMobile=true), showing the real API-mode
// session the proof is driving. Called by scripts/prove-api-mode-pi.ps1 at the moments each screen exists; it can
// also be run by hand against a running rig.
//
//   node prove-api-mode-pi-screens.mjs shot --name <name> --session <id> --tab chat|terminal --out <folder>
//        [--expect <text>] [--expect-reply] [--ready <file>] [--timeout <seconds>]
//   node prove-api-mode-pi-screens.mjs newsession --out <folder>
//
// Environment: GATEWAY_URL (the test Gateway), GATEWAY_TOKEN (its machine token, which the browser presents as the
// Gateway's own cookie and as the client's stored key), PLAYWRIGHT_DIR (a folder whose node_modules holds
// playwright-core), CHROME (a Chrome executable; playwright-core downloads no browser).
//
// For each surface (the Cockpit at 1440 by 900, the phone at 390 by 844) it writes <name>-<surface>.png and
// <name>-<surface>.aria.txt, the page's accessibility tree as Playwright reads it. `--expect` is text the screen must
// show before it is photographed; `--expect-reply` adds that, at the same moment, the live conversation must end in a
// reply with text - the reply streaming in, which is what the mid-turn screen exists to show. `--ready` is a file
// written once both pages are open, so the caller can start the turn the shot is waiting for. The last line printed
// is one JSON object per surface: {name, surface, ok, detail}.
//
// Why `--expect-reply`: the session's status line says "Working - turn" the moment the turn starts, but Pi's first
// words arrive a second or more after it. Waiting for the status line alone photographed the Cockpit before the
// reply existed, and the phone - photographed after the Cockpit - only by luck after it (the run of 20 October 2026,
// linux-20261020-003750). Measured in the real Cockpit on 20 October: status line at 00:58:33.273, first reply word
// stored by the Director at 00:58:34.373, drawn by the Cockpit at 00:58:34.456 and by the phone at 00:58:34.438.
import { createRequire } from "node:module";
import { writeFileSync } from "node:fs";
import { join } from "node:path";
import { pathToFileURL } from "node:url";

const args = process.argv.slice(2);
const command = args[0];
const opt = (name, fallback) => {
  const i = args.indexOf(`--${name}`);
  return i >= 0 && i + 1 < args.length ? args[i + 1] : fallback;
};
const GATEWAY = (process.env.GATEWAY_URL || "").replace(/\/$/, "");
const TOKEN = process.env.GATEWAY_TOKEN || "";

/** playwright-core from PLAYWRIGHT_DIR (it downloads no browser; it drives CHROME). */
export function loadChromium() {
  const require = createRequire(join(process.env.PLAYWRIGHT_DIR, "node_modules") + "/");
  return require("playwright-core").chromium;
}

/**
 * Wait until the page shows what the shot is for, then return; throws when it does not within `timeout`
 * milliseconds. `expect` is text the page must show. With `reply`, at the same moment the live conversation must end
 * in a reply that has text: a status line alone is not a reply streaming in.
 */
export async function waitForScreen(page, { expect, reply, timeout }) {
  if (!reply) {
    await page.getByText(expect, { exact: false }).first().waitFor({ timeout });
    return;
  }
  await page.waitForFunction((want) => {
    if (want && !document.body.innerText.includes(want)) return false;
    const log = document.querySelector('[role="log"][aria-label="Live conversation"]');
    const last = log?.lastElementChild;
    return !!last && last.matches("article.live-chat-reply") && (last.textContent ?? "").trim() !== "";
  }, expect ?? "", { timeout });
}

const SURFACES = {
  cockpit: { viewport: { width: 1440, height: 900 } },
  phone: { viewport: { width: 390, height: 844 }, deviceScaleFactor: 2, isMobile: true, hasTouch: true },
};

async function open(browser, surface) {
  const ctx = await browser.newContext(SURFACES[surface]);
  const host = new URL(GATEWAY).hostname;
  await ctx.addCookies([{ name: "cc-gateway-token", value: TOKEN, domain: host, path: "/" }]);
  await ctx.addInitScript((t) => { localStorage.setItem("cc.deviceKey", t); }, TOKEN);
  return ctx.newPage();
}

async function save(page, out, name, surface, locator) {
  const base = join(out, `${name}-${surface}`);
  await page.screenshot({ path: `${base}.png`, fullPage: surface === "phone" });
  writeFileSync(`${base}.aria.txt`, await (locator ?? page.locator("body")).ariaSnapshot());
}

// The session's Chat or Terminal tab on each surface.
async function goToTab(page, surface, sid, tab) {
  if (surface === "phone") {
    await page.goto(`${GATEWAY}/mobile/session/${sid}/${tab}`);
  } else {
    await page.goto(`${GATEWAY}/session/${sid}`);
    await page.getByRole("tab", { name: tab === "chat" ? "Chat" : "Terminal" }).click();
  }
  await page.getByRole("tablist", { name: "Session view" }).waitFor({ timeout: 30000 });
}

async function shot() {
  const name = opt("name"), sid = opt("session"), tab = opt("tab", "chat"), out = opt("out");
  const expect = opt("expect"), ready = opt("ready"), timeout = Number(opt("timeout", "60")) * 1000;
  const reply = args.includes("--expect-reply");
  const want = reply ? `"${expect}" with a reply streaming in` : `"${expect}"`;
  const browser = await loadChromium().launch({ executablePath: process.env.CHROME, headless: true });
  const results = [];
  try {
    const pages = {};
    for (const surface of Object.keys(SURFACES)) {
      pages[surface] = await open(browser, surface);
      await goToTab(pages[surface], surface, sid, tab);
    }
    if (ready) writeFileSync(ready, new Date().toISOString());
    for (const [surface, page] of Object.entries(pages)) {
      let ok = true, detail = "photographed";
      if (expect) {
        try {
          await waitForScreen(page, { expect, reply, timeout });
          detail = `showed ${want}`;
        } catch {
          ok = false;
          detail = `did not show ${want} within ${timeout / 1000} seconds`;
        }
      }
      await save(page, out, name, surface);
      results.push({ name, surface, ok, detail });
    }
  } finally {
    await browser.close();
  }
  console.log(JSON.stringify(results));
}

// New Session with Pi and API mode chosen, on both surfaces. Nothing is submitted: the dialog is photographed and
// closed, so no session is started.
async function newsession() {
  const out = opt("out");
  const browser = await loadChromium().launch({ executablePath: process.env.CHROME, headless: true });
  const results = [];
  try {
    const cockpit = await open(browser, "cockpit");
    await cockpit.goto(`${GATEWAY}/sessions`);
    await cockpit.getByRole("button", { name: /new session/i }).first().click();
    const dialog = cockpit.locator(".newsess-modal");
    await dialog.waitFor({ timeout: 30000 });
    // The Director's known repositories come first; any one will do, the Connection row is about the agent.
    const repo = dialog.locator("[data-repo-path], .newsess-repo, li button").first();
    if (await repo.count()) await repo.click();
    await clickIfPresent(dialog.getByRole("button", { name: /^Pi$/ }));
    results.push(await chooseApiMode(cockpit, dialog, out, "new-session-api-mode", "cockpit"));

    const phone = await open(browser, "phone");
    await phone.goto(`${GATEWAY}/mobile/new`);
    await clickIfPresent(phone.getByRole("button", { name: /^Select Director / }).first());
    await clickIfPresent(phone.getByRole("button", { name: "Select agent Pi" }));
    await clickIfPresent(phone.getByRole("button", { name: /^Select repository / }).first());
    results.push(await chooseApiMode(phone, phone.locator("body"), out, "new-session-api-mode", "phone"));
  } finally {
    await browser.close();
  }
  console.log(JSON.stringify(results));
}

async function clickIfPresent(locator) {
  try { await locator.click({ timeout: 10000 }); } catch { /* the step is not shown on this surface */ }
}

async function chooseApiMode(page, scope, out, name, surface) {
  try {
    await scope.getByRole("group", { name: "Connection" }).waitFor({ timeout: 30000 });
    const apiMode = scope.getByRole("button", { name: "API mode" });
    await apiMode.click();
    const pressed = (await apiMode.getAttribute("aria-pressed")) === "true";
    await save(page, out, name, surface, scope);
    return { name, surface, ok: pressed, detail: pressed ? "Connection row shown; API mode pressed" : "API mode was not pressed after the click" };
  } catch (e) {
    await save(page, out, name, surface);
    return { name, surface, ok: false, detail: `no Connection row with API mode: ${String(e.message).split("\n")[0]}` };
  }
}

// Run as a command; imported (by its test), it only exports.
if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  if (!GATEWAY || !TOKEN || !process.env.PLAYWRIGHT_DIR || !process.env.CHROME) {
    console.error("GATEWAY_URL, GATEWAY_TOKEN, PLAYWRIGHT_DIR and CHROME are all required");
    process.exit(2);
  }
  if (command === "shot") await shot();
  else if (command === "newsession") await newsession();
  else { console.error(`unknown command "${command}": shot or newsession`); process.exit(2); }
}
