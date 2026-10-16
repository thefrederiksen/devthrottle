// Captures the phase 3 proof (mission #4070): the shared live Chat mid-turn and in its failure cases, in the Cockpit's
// frame and the phone's, as screenshots and accessibility dumps (Playwright's ARIA snapshot of the Chat).
//
//   node docs/missions/api-mode-pi-2026-10-16/proofs/phase-3/harness/capture.mjs
//
// Needs playwright-core and its Chromium; set PLAYWRIGHT_CORE to the package folder when it is not resolvable from
// here (on devlinux: /home/soren/ReposFred/rea/node_modules/playwright-core).
import { createRequire } from "node:module";
import { fileURLToPath } from "node:url";
import { writeFileSync } from "node:fs";
import { createServer } from "vite";

const here = fileURLToPath(new URL(".", import.meta.url));
const out = fileURLToPath(new URL("../", import.meta.url));
const require = createRequire(import.meta.url);
const { chromium } = require(process.env.PLAYWRIGHT_CORE ?? "playwright-core");

const server = await createServer({ configFile: `${here}vite.config.ts` });
await server.listen();
const base = "http://localhost:5199/";
// CHROMIUM may name a Chromium binary when the one this playwright-core expects is not downloaded.
const browser = await chromium.launch(process.env.CHROMIUM ? { executablePath: process.env.CHROMIUM } : {});
const shots = [
  { shell: "cockpit", scene: "midturn", viewport: { width: 1100, height: 900 } },
  { shell: "cockpit", scene: "failure", viewport: { width: 1100, height: 760 } },
  { shell: "phone", scene: "midturn", viewport: { width: 412, height: 1005 } },
  { shell: "phone", scene: "failure", viewport: { width: 412, height: 860 } },
];
try {
  for (const s of shots) {
    const page = await browser.newPage({ viewport: s.viewport, colorScheme: "dark" });
    page.on("pageerror", (err) => console.error(`page error: ${err.message}`));
    page.on("console", (msg) => { if (msg.type() === "error") console.error(`console: ${msg.text()}`); });
    await page.goto(`${base}?shell=${s.shell}&scene=${s.scene}`);
    await page.waitForSelector("body[data-ready=true]", { state: "attached", timeout: 20000 });
    await page.waitForTimeout(200);
    const name = `${s.shell}-${s.scene}`;
    await page.screenshot({ path: `${out}${name}.png`, fullPage: false });
    const dump = await page.locator(".live-chat").ariaSnapshot();
    writeFileSync(`${out}${name}.aria.yml`, dump + "\n");
    console.log(`captured ${name}`);
    await page.close();
  }
} finally {
  await browser.close();
  await server.close();
}
