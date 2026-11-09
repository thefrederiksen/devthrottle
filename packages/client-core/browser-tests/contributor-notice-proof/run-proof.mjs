// Contributor email choice proof driver (Team implementation piece 4.4).
//
// It (1) esbuild-bundles the REAL ContributorNoticeCard (harness-entry.tsx), (2) serves this directory over http with
// the shared settings stylesheet - and, for the phone, the phone's own stylesheet with the card inside a .screen frame,
// exactly where the phone re-tunes the shared cards for touch - and (3) drives the card in a real Chromium:
//   claim A - the card shows the Gateway's words, right away chosen, no hour.
//   claim B - choosing once a day sends ONE PUT { mode: "daily", dailyHour: 8 }; the hour and the zone line then show,
//             with the Gateway's saved line.
//   claim C - choosing 17:00 sends ONE more PUT { mode: "daily", dailyHour: 17 }.
// Screenshots at desktop width (760) and phone width (390).
//
// Run:  node run-proof.mjs   ->  writes evidence-<date>.json + screenshots, prints PASS/FAIL. ASCII only.
import { build } from "esbuild";
import { createServer } from "node:http";
import { readFile, writeFile } from "node:fs/promises";
import { fileURLToPath } from "node:url";
import { dirname, join, extname } from "node:path";
import { createRequire } from "node:module";

const requireCjs = createRequire(import.meta.url);
const PLAYWRIGHT_PATH =
  process.env.PLAYWRIGHT_PATH ||
  "C:/Users/soren/AppData/Roaming/npm/node_modules/@playwright/cli/node_modules/playwright";
const { chromium } = requireCjs(PLAYWRIGHT_PATH);

const here = dirname(fileURLToPath(import.meta.url));
const settingsCss = join(here, "../../src/settings/settings.css");
const phoneCss = join(here, "../../../../apps/mobile/src/styles.css");
const port = Number(process.env.PORT || 8799);
const types = { ".html": "text/html; charset=utf-8", ".js": "text/javascript; charset=utf-8", ".css": "text/css; charset=utf-8" };
const log = (m) => console.log("[contributor-notice-proof] " + m);

async function bundle() {
  const result = await build({
    entryPoints: [join(here, "harness-entry.tsx")],
    bundle: true,
    format: "iife",
    write: false,
    target: "es2021",
    jsx: "automatic",
    loader: { ".ts": "ts", ".tsx": "tsx", ".css": "empty" },
    logLevel: "warning",
  });
  return result.outputFiles[0].text;
}

function serve(js, phone) {
  return createServer(async (req, res) => {
    const path = (req.url || "/").split("?")[0];
    try {
      if (path === "/harness.iife.js") return res.writeHead(200, { "content-type": types[".js"] }).end(js);
      if (path === "/settings.css") return res.writeHead(200, { "content-type": types[".css"] }).end(await readFile(settingsCss));
      if (path === "/shell.css")
        return res.writeHead(200, { "content-type": types[".css"] }).end(phone.value ? await readFile(phoneCss) : "");
      const file = path === "/" ? "index.html" : path.replace(/^\/+/, "");
      const body = await readFile(join(here, file));
      res.writeHead(200, { "content-type": types[extname(file)] || "application/octet-stream" }).end(body);
    } catch (e) {
      res.writeHead(404).end(String(e));
    }
  });
}

const results = [];
const claim = (id, ok, detail) => {
  results.push({ id, ok, detail });
  log((ok ? "PASS" : "FAIL") + " claim " + id + ": " + detail);
};

const js = await bundle();
const phone = { value: false };
const server = serve(js, phone);
await new Promise((r) => server.listen(port, r));
// PW_CHANNEL=msedge (or chrome) uses an installed browser when Playwright's own Chromium is not downloaded.
const browser = await chromium.launch(process.env.PW_CHANNEL ? { channel: process.env.PW_CHANNEL } : {});
const base = "http://localhost:" + port + "/";
try {
  // Desktop.
  let page = await browser.newPage({ viewport: { width: 760, height: 720 } });
  await page.goto(base);
  await page.getByText("Email when something is sent to you").waitFor();
  claim(
    "A",
    (await page.getByLabel("Email me right away").isChecked()) && (await page.locator("[data-testid=contributor-notice-hour]").count()) === 0,
    "right away chosen, no hour shown",
  );
  await page.screenshot({ path: join(here, "evidence-desktop-right-away.png") });
  await page.getByLabel("Once a day, in one email").check();
  await page.getByText("Saved. You get one email a day at 08:00, only when something is waiting.").waitFor();
  let puts = await page.evaluate(() => window.__puts);
  claim(
    "B",
    puts.length === 1 && puts[0].mode === "daily" && puts[0].dailyHour === 8 &&
      (await page.getByText("Times are in your own time zone, America/Toronto.").count()) === 1,
    JSON.stringify(puts),
  );
  await page.getByLabel("Send the daily email at").selectOption("17");
  await page.getByText("Saved. You get one email a day at 17:00, only when something is waiting.").waitFor();
  puts = await page.evaluate(() => window.__puts);
  claim("C", puts.length === 2 && puts[1].mode === "daily" && puts[1].dailyHour === 17, JSON.stringify(puts));
  await page.screenshot({ path: join(here, "evidence-desktop-daily.png") });
  await page.close();

  // Phone: the phone's stylesheet, the card inside its .screen frame.
  phone.value = true;
  page = await browser.newPage({ viewport: { width: 390, height: 844 }, deviceScaleFactor: 2 });
  await page.goto(base);
  await page.evaluate(() => {
    document.getElementById("frame").className = "screen";
    document.body.style.padding = "16px";
  });
  await page.getByText("Email when something is sent to you").waitFor();
  await page.getByLabel("Once a day, in one email").check();
  await page.getByText("Saved. You get one email a day at 08:00, only when something is waiting.").waitFor();
  await page.screenshot({ path: join(here, "evidence-phone-daily.png"), fullPage: true });
  await page.close();
} finally {
  await browser.close();
  server.close();
}
const date = new Date().toISOString().slice(0, 10);
await writeFile(join(here, "evidence-" + date + ".json"), JSON.stringify({ date, results }, null, 2) + "\n");
const failed = results.filter((r) => !r.ok).length;
log(failed === 0 ? "ALL " + results.length + " CLAIMS PASS" : failed + " CLAIM(S) FAILED");
process.exit(failed === 0 ? 0 : 1);
