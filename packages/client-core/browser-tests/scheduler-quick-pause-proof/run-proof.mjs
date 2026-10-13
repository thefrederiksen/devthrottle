// Scheduler quick pause proof driver (issue #3910).
//
// It (1) esbuild-bundles the REAL Cockpit Schedule page, Factories screen and factory Seats tab with their own
// stylesheets (harness-entry.tsx), (2) serves this directory over http, and (3) drives them in a real Chromium
// against a fake Gateway, proving the four changes the issue asked for with hard evidence:
//   claim A - one word: the Schedule page says Active and Paused, never Enabled or Disabled.
//   claim B - pause everything: one tap on the Schedule page sends PUT /cron/pause {paused:true}, a banner says so in
//             the Gateway's words, and no schedule was written.
//   claim C - the Factories screen shows the same banner and the same control, and one tap there resumes; every
//             schedule's own enabled flag is exactly what it was before the pause.
//   claim D - a factory's Seats tab has Pause beside Edit; one tap pauses that schedule, the line then reads
//             (paused) and offers Resume, and one tap resumes it.
//   claim E - the schedule editor has an Active / Paused control; it opens a paused schedule as Paused, an edit that
//             leaves the control alone saves it still paused, and picking Active is what resumes it.
//
// This proves the CLIENT flow against shipping code. It is NOT the real Gateway - that nothing fires while paused,
// that no schedule is written and that the switch survives a restart are proven by the Gateway C# suite
// (CronPauseEverythingTests, CronPauseEverythingHostTests).
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
const port = Number(process.env.PORT || 8799);
const contentTypes = {
  ".html": "text/html; charset=utf-8",
  ".js": "text/javascript; charset=utf-8",
  ".css": "text/css; charset=utf-8",
};

const log = (m) => console.log(`[quick-pause-proof] ${m}`);

async function bundle() {
  const result = await build({
    entryPoints: [join(here, "harness-entry.tsx")],
    bundle: true,
    format: "iife",
    write: false,
    outdir: here,
    entryNames: "harness.iife",
    target: "es2021",
    jsx: "automatic",
    loader: { ".ts": "ts", ".tsx": "tsx", ".css": "css" },
    define: { "process.env.NODE_ENV": '"production"' },
    logLevel: "warning",
  });
  const js = result.outputFiles.find((f) => f.path.endsWith(".js")).text;
  const css = result.outputFiles.find((f) => f.path.endsWith(".css")).text;
  log(`bundled the real Cockpit pages (${js.length} bytes of script, ${css.length} bytes of the Cockpit's own styles)`);
  return { js, css };
}

function serve({ js, css }) {
  const server = createServer(async (req, res) => {
    const path = (req.url || "/").split("?")[0];
    try {
      if (path === "/harness.iife.js") {
        res.writeHead(200, { "content-type": contentTypes[".js"] });
        res.end(js);
        return;
      }
      if (path === "/harness.css") {
        res.writeHead(200, { "content-type": contentTypes[".css"] });
        res.end(css);
        return;
      }
      const file = path === "/" ? "index.html" : path.replace(/^\/+/, "");
      const body = await readFile(join(here, file));
      res.writeHead(200, { "content-type": contentTypes[extname(file)] || "application/octet-stream" });
      res.end(body);
    } catch {
      res.writeHead(404, { "content-type": "text/plain" });
      res.end("not found");
    }
  });
  return new Promise((resolve) => server.listen(port, "127.0.0.1", () => resolve(server)));
}

const fail = [];
function check(claim, ok, detail) {
  log(`${ok ? "PASS" : "FAIL"} - ${claim}: ${detail}`);
  if (!ok) fail.push(claim);
}

const flags = (page) => page.evaluate(() => Object.fromEntries(window.__gateway.jobs.map((j) => [j.id, j.enabled])));
const requests = (page, method, prefix) =>
  page.evaluate(([m, p]) => window.__requests.filter((r) => r.method === m && r.path.startsWith(p)), [method, prefix]);
const shot = (page, name) => page.screenshot({ path: join(here, name), fullPage: true });
// In-app navigation: the page is not reloaded, so the fake Gateway keeps its state between screens.
const go = (page, route) => page.evaluate((r) => { window.location.hash = r; }, route);

async function main() {
  const server = await serve(await bundle());
  const url = `http://127.0.0.1:${port}/`;
  log(`serving ${url}`);

  // PLAYWRIGHT_CHROMIUM names a Chromium to drive when this Playwright's own download is not on the machine.
  const browser = await chromium.launch(process.env.PLAYWRIGHT_CHROMIUM ? { executablePath: process.env.PLAYWRIGHT_CHROMIUM } : {});
  const page = await browser.newPage({ viewport: { width: 1500, height: 900 } });
  const evidence = { scenario: "scheduler quick pause: pause everything, pause one where it is read, one word", steps: {} };

  try {
    // ---- claim A: one word.
    await page.goto(`${url}#/schedule`);
    await page.getByText("Morning issue sweep").waitFor();
    await page.getByRole("button", { name: "Pause everything" }).waitFor();
    const before = await flags(page);
    evidence.steps.enabledFlagsBefore = before;
    const activeChip = page.locator(".sched-chip.active").first();
    const chipWord = (await activeChip.textContent()).trim();
    const chipTitle = await activeChip.getAttribute("title");
    await page.getByRole("tab", { name: /Paused/ }).click();
    await page.getByText("Evening tidy").waitFor();
    const pausedWord = (await page.locator(".sched-chip.paused").first().textContent()).trim();
    const pausedHtml = await page.content();
    await shot(page, "evidence-1-paused-tab.png");
    await page.getByRole("tab", { name: /Active/ }).click();
    await page.getByText("Morning issue sweep").waitFor();
    const activeHtml = await page.content();
    await shot(page, "evidence-2-schedule-page.png");
    const oldWords = /Enabled|Disabled|Toggle enabled/;
    evidence.steps.chipWords = { active: chipWord, activeTitle: chipTitle, paused: pausedWord };
    check("A: the status chip says Active and Paused", chipWord === "Active" && pausedWord === "Paused", `${chipWord} / ${pausedWord}, title "${chipTitle}"`);
    check("A: nothing on the page says Enabled or Disabled", !oldWords.test(activeHtml) && !oldWords.test(pausedHtml), "searched the rendered page, titles included, on both tabs");

    // ---- claim B: pause everything from the Schedule page.
    await page.getByRole("button", { name: "Pause everything" }).click();
    const banner = page.getByTestId("schedules-paused-banner");
    await banner.waitFor();
    const bannerText = (await banner.locator(".sched-pause-banner-text").textContent()).trim();
    const pausePuts = await requests(page, "PUT", "/cron/pause");
    const jobPutsAfterPause = await requests(page, "PUT", "/cron/jobs");
    evidence.steps.pauseEverything = { requests: pausePuts, bannerText, scheduleWrites: jobPutsAfterPause.length };
    await shot(page, "evidence-3-everything-paused.png");
    check("B: one tap sends PUT /cron/pause {paused:true}", pausePuts.length === 1 && pausePuts[0].body.paused === true, JSON.stringify(pausePuts));
    check("B: the banner says so in the Gateway's words", bannerText.includes("Everything is paused.") && bannerText.includes("(fake)"), bannerText);
    check("B: pausing everything wrote no schedule", jobPutsAfterPause.length === 0, `${jobPutsAfterPause.length} schedule write(s)`);
    check("B: every schedule's own enabled flag is untouched", JSON.stringify(await flags(page)) === JSON.stringify(before), JSON.stringify(await flags(page)));

    // ---- claim C: the Factories screen shows it, and resumes it.
    await go(page, "/factories");
    await page.getByTestId("factories-page").waitFor();
    const factoriesBanner = page.getByTestId("schedules-paused-banner");
    await factoriesBanner.waitFor();
    await shot(page, "evidence-4-factories-paused.png");
    check("C: the Factories screen shows the same banner", (await factoriesBanner.textContent()).includes("Everything is paused."), "banner present on /factories");
    await page.getByTestId("schedules-pause-toggle").click();
    await factoriesBanner.waitFor({ state: "detached" });
    await page.getByRole("button", { name: "Pause everything" }).waitFor();
    const allPausePuts = await requests(page, "PUT", "/cron/pause");
    const after = await flags(page);
    evidence.steps.resumeEverything = { requests: allPausePuts, enabledFlagsAfter: after };
    await shot(page, "evidence-5-factories-resumed.png");
    check("C: one tap on the Factories screen sends PUT /cron/pause {paused:false}", allPausePuts.length === 2 && allPausePuts[1].body.paused === false, JSON.stringify(allPausePuts));
    check("C: after resuming, every schedule is exactly as it was before the pause", JSON.stringify(after) === JSON.stringify(before), `before ${JSON.stringify(before)} after ${JSON.stringify(after)}`);
    check("C: neither direction wrote a schedule", (await requests(page, "PUT", "/cron/jobs")).length === 0, "0 schedule writes");

    // ---- claim D: Pause and Resume beside Edit on the Seats tab.
    await go(page, "/factories/warmforward/seats");
    const seat = page.getByTestId("fa-seat-nora-hale");
    await seat.waitFor();
    const line = seat.locator(".fa-seat-schedule");
    const buttonsBefore = (await line.getByRole("button").allTextContents()).map((t) => t.trim());
    await shot(page, "evidence-6-seats-active.png");
    check("D: the seat schedule has Pause beside Edit", JSON.stringify(buttonsBefore) === JSON.stringify(["Edit schedule", "Pause"]), buttonsBefore.join(" | "));
    await line.getByRole("button", { name: "Pause" }).click();
    await line.getByRole("button", { name: "Resume" }).waitFor();
    const pausedLine = (await line.locator("span").first().textContent()).trim();
    const seatPut = (await requests(page, "PUT", "/cron/jobs/cj_boss")).at(-1);
    evidence.steps.seatPause = { line: pausedLine, write: seatPut };
    await shot(page, "evidence-7-seats-paused.png");
    check("D: one tap pauses that schedule", seatPut.body.enabled === false && (await flags(page)).cj_boss === false, JSON.stringify(seatPut.body.enabled));
    check("D: the line then reads (paused) and offers Resume", pausedLine === "Daily 09:00 (America/New_York) (paused)", pausedLine);
    check("D: the write echoes none of the Gateway's own state", !("nextRunUtc" in seatPut.body) && !("stateLabel" in seatPut.body), Object.keys(seatPut.body).join(","));
    check("D: pausing one schedule changed no other", (await flags(page)).cj_own === before.cj_own && (await flags(page)).cj_tidy === before.cj_tidy, JSON.stringify(await flags(page)));
    await line.getByRole("button", { name: "Resume" }).click();
    await line.getByRole("button", { name: "Pause" }).waitFor();
    check("D: one tap resumes it", (await flags(page)).cj_boss === true, JSON.stringify(await flags(page)));

    // ---- claim E: the editor's Active / Paused control.
    await go(page, "/schedule");
    await page.getByRole("tab", { name: /Paused/ }).click();
    await page.getByText("Evening tidy").waitFor();
    await page.getByRole("button", { name: "Edit", exact: true }).click();
    const dialog = page.getByRole("dialog", { name: "Edit cron job" });
    await dialog.waitFor();
    const opensPaused = await dialog.getByRole("radio", { name: "Paused" }).getAttribute("aria-checked");
    await shot(page, "evidence-8-editor-paused.png");
    check("E: the editor opens a paused schedule as Paused", opensPaused === "true", `Paused aria-checked=${opensPaused}`);
    await dialog.locator("input").first().fill("Evening tidy, renamed");
    await dialog.getByRole("button", { name: "Save" }).click();
    await dialog.waitFor({ state: "detached" });
    const renamePut = (await requests(page, "PUT", "/cron/jobs/cj_tidy")).at(-1);
    evidence.steps.editorRenameWhilePaused = renamePut.body;
    check("E: an edit that leaves the control alone saves it still paused", renamePut.body.enabled === false && renamePut.body.name === "Evening tidy, renamed", JSON.stringify({ name: renamePut.body.name, enabled: renamePut.body.enabled }));
    await page.getByText("Evening tidy, renamed").waitFor();
    await page.getByRole("button", { name: "Edit", exact: true }).click();
    await dialog.waitFor();
    await dialog.getByRole("radio", { name: "Active" }).click();
    await shot(page, "evidence-9-editor-active.png");
    await dialog.getByRole("button", { name: "Save" }).click();
    await dialog.waitFor({ state: "detached" });
    const resumePut = (await requests(page, "PUT", "/cron/jobs/cj_tidy")).at(-1);
    evidence.steps.editorPickActive = { enabled: resumePut.body.enabled };
    check("E: picking Active is what resumes it", resumePut.body.enabled === true && (await flags(page)).cj_tidy === true, JSON.stringify(resumePut.body.enabled));

    evidence.generatedAtUtc = new Date().toISOString();
    evidence.result = fail.length === 0 ? "PASS" : `FAIL (${fail.join("; ")})`;
    await writeFile(join(here, `evidence-${evidence.generatedAtUtc.slice(0, 10)}.json`), JSON.stringify(evidence, null, 2));
    log("wrote evidence json");
  } finally {
    await browser.close();
    server.close();
  }

  log(fail.length === 0 ? "ALL CLAIMS PASS" : `FAILED: ${fail.join("; ")}`);
  process.exit(fail.length === 0 ? 0 : 1);
}

main().catch((e) => {
  console.error(`[quick-pause-proof] ERROR: ${e.stack || e.message}`);
  process.exit(1);
});
