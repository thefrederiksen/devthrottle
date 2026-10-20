// The phase 7 screenshots of the Mail Desk page, for ONE scenario of seed.py, against a THROWAWAY Gateway
// (docs/qa/mail-desk/phase-7/CONTRACT.md, section 4). capture.py runs it; it is not run by hand.
//
//   node shots.cjs <gateway> <out folder> <scenario: empty | unpublished | on | off>      with QA_PERSON_KEY set
//
// Playwright 1.60.0 from the web-site skill's tools folder (NODE_PATH). Headless Chromium (the full build), a fresh
// context per picture, signed in as the throwaway Gateway's enrolled browser device the way every Cockpit and phone
// browser is: the cc-gateway-token cookie and the stored account (client-core/auth/accountStore.ts). Never a shared
// browser profile.
//
// Desktop pictures are the Cockpit at 1440 wide; phone pictures are the phone app at 390 wide. Each picture is taken
// only once the page shows what the scenario must show, and what was SEEN is written beside the pictures in
// seen-<scenario>.json - the heading, the cards, the Work rail and the drawer - so a picture is never the only record.
"use strict";

const fs = require("node:fs");
const path = require("node:path");
const { chromium } = require("playwright");

const [gateway, outDir, scenario] = process.argv.slice(2);
const key = process.env.QA_PERSON_KEY;
if (!gateway || !outDir || !["empty", "unpublished", "on", "off"].includes(scenario) || !key) {
  console.error("usage: QA_PERSON_KEY=... node shots.cjs <gateway> <out folder> <empty|unpublished|on|off>");
  process.exit(2);
}
if (!/^http:\/\/(127\.0\.0\.1|localhost):\d+$/.test(gateway)) {
  console.error(`[shots] ${gateway} is not this machine; the screenshots are taken of a throwaway Gateway only`);
  process.exit(2);
}

const DESKTOP = { width: 1440, height: 900 };
const PHONE = { width: 390, height: 844 };
const ON = scenario !== "off";

async function signedIn(browser, viewport, phone) {
  const context = await browser.newContext({
    viewport,
    deviceScaleFactor: phone ? 2 : 1,
    isMobile: phone,
    hasTouch: phone,
  });
  await context.addCookies([{ name: "cc-gateway-token", value: key, url: gateway }]);
  await context.addInitScript((deviceKey) => {
    localStorage.setItem("cc.accounts", JSON.stringify([
      { id: "qa", label: "email@devthrottle.com", email: "email@devthrottle.com", deviceKey, installId: "qa-phase-7" }]));
    localStorage.setItem("cc.activeAccount", "qa");
    localStorage.setItem("cc.deviceKey", deviceKey);
  }, key);
  return context;
}

/** What the page shows that the scenario is about: the heading, the cards' text, or "Page not found". */
async function readPage(page) {
  return page.evaluate(() => ({
    address: location.pathname,
    headings: Array.from(document.querySelectorAll("h1")).map((h) => h.textContent.trim()),
    cards: Array.from(document.querySelectorAll("[data-testid=mail-desk-channel]")).map((c) => c.innerText.trim()),
    empty: document.querySelector(".mail-desk-empty")?.textContent.trim() ?? null,
    controls: document.querySelectorAll(".mail-desk button, .mail-desk input, .mail-desk select, .mail-desk textarea").length,
    mailDeskLinks: Array.from(document.querySelectorAll("a")).filter((a) => a.getAttribute("href")?.endsWith("/mail-desk")).length,
  }));
}

/** The page has had its answer: the cards, the empty sentence, or "Page not found" - then a moment for the fonts. */
async function settled(page) {
  if (scenario === "on" || scenario === "unpublished") await page.waitForSelector("[data-testid=mail-desk-channel] >> nth=2", { timeout: 30000 });
  else if (scenario === "empty") await page.waitForSelector(".mail-desk-empty", { timeout: 30000 });
  else await page.waitForSelector("text=Page not found", { timeout: 30000 });
  await page.waitForLoadState("networkidle");
  await page.waitForTimeout(1000);
}

async function shoot(page, name) {
  // The Cockpit scrolls inside its own pane, so a full-page picture would stop at the window's height (dry run 3 cut
  // the third card off). The window is made as tall as the tallest scrolled content first.
  const hidden = await page.evaluate(() => Math.max(0, ...Array.from(document.querySelectorAll("*"))
    .filter((el) => /(auto|scroll)/.test(getComputedStyle(el).overflowY))
    .map((el) => el.scrollHeight - el.clientHeight)));
  if (hidden > 0) {
    const size = page.viewportSize();
    await page.setViewportSize({ width: size.width, height: size.height + hidden });
    await page.waitForTimeout(500);
  }
  const file = path.join(outDir, `${name}.png`);
  await page.screenshot({ path: file, fullPage: true });
  console.log(`[shots] ${name}.png`);
}

async function cockpit(browser, seen) {
  const context = await signedIn(browser, DESKTOP, false);
  const page = await context.newPage();
  await page.goto(`${gateway}/mail-desk`, { waitUntil: "domcontentloaded" });
  await settled(page);
  // The rail's Work section: with the switch on the entry must be drawn, with it off it must be absent once the read
  // has been answered - the page's own read has already been answered here, and the rail asks the same route.
  if (ON) await page.waitForSelector('nav a[href="/mail-desk"]', { timeout: 30000 });
  seen.desktop = {
    ...(await readPage(page)),
    work: await page.$$eval('[data-testid=nav-work] .nav-link-label', (els) => els.map((e) => e.textContent.trim())),
  };
  await shoot(page, `desktop-${scenario}`);
  await context.close();
}

async function phone(browser, seen) {
  const context = await signedIn(browser, PHONE, true);
  const page = await context.newPage();
  await page.goto(`${gateway}/mobile/mail-desk`, { waitUntil: "domcontentloaded" });
  await settled(page);
  seen.phone = await readPage(page);
  await shoot(page, `phone-${scenario}`);

  // The drawer, opened from the session list (the only screen whose app bar has the menu).
  await page.goto(`${gateway}/mobile/`, { waitUntil: "domcontentloaded" });
  await page.getByRole("button", { name: "Open menu" }).click();
  const drawer = page.getByRole("navigation", { name: "Navigation" });
  await drawer.waitFor({ timeout: 30000 });
  if (ON) await drawer.locator('a[href="/mobile/mail-desk"]').waitFor({ timeout: 30000 });
  else await page.waitForLoadState("networkidle");
  await page.waitForTimeout(1000);
  seen.drawer = await drawer.locator("a").evaluateAll((links) => links.map((a) => [a.textContent.trim(), a.getAttribute("href")]));
  await page.screenshot({ path: path.join(outDir, `phone-drawer-${scenario}.png`) });
  console.log(`[shots] phone-drawer-${scenario}.png`);
  await context.close();
}

(async () => {
  fs.mkdirSync(outDir, { recursive: true });
  // The full Chromium build, headless - not Playwright's slimmed headless shell, which crashed on the phone's session
  // list at every width on devlinux (19 Oct 2026; the session list takes a screen wake lock) while this build did not.
  const browser = await chromium.launch({ headless: true, channel: "chromium" });
  const seen = { scenario, gateway, takenUtc: new Date().toISOString() };
  try {
    await cockpit(browser, seen);
    await phone(browser, seen);
  } finally {
    fs.writeFileSync(path.join(outDir, `seen-${scenario}.json`), JSON.stringify(seen, null, 2) + "\n");
    await browser.close();
  }
})().catch((err) => {
  console.error(`[shots] FAILED: ${err && err.stack ? err.stack : err}`);
  process.exit(1);
});
