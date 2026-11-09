// Watch player proof driver (Watch your factory, piece 5).
//
// It (1) esbuild-bundles the REAL shared player with its own stylesheet, drawing the committed contract fixture
// (harness-entry.tsx), (2) serves this directory over http, and (3) screenshots it in a real Chromium at a desktop
// width and at 390 pixels: during a step animation, and with zoom and follow on (an instant replay). It also checks,
// in the real browser, that every station and character of the fixture is drawn and that each control changes the
// playback state the player reports.
//
// Run:  node run-proof.mjs [output folder]   ->  screenshots + evidence.json, prints PASS/FAIL. ASCII only.

import { build } from "esbuild";
import { createServer } from "node:http";
import { mkdir, readFile, writeFile } from "node:fs/promises";
import { fileURLToPath } from "node:url";
import { dirname, join, extname } from "node:path";
import { createRequire } from "node:module";

const requireCjs = createRequire(import.meta.url);
const PLAYWRIGHT_PATH =
  process.env.PLAYWRIGHT_PATH || "C:/Users/soren/AppData/Roaming/npm/node_modules/@playwright/cli/node_modules/playwright";
const { chromium } = requireCjs(PLAYWRIGHT_PATH);

const here = dirname(fileURLToPath(import.meta.url));
const out = process.argv[2] || join(here, "evidence");
const port = Number(process.env.PORT || 8817);
const contentTypes = { ".html": "text/html; charset=utf-8", ".js": "text/javascript; charset=utf-8", ".css": "text/css; charset=utf-8" };
const log = (m) => console.log(`[watch-player-proof] ${m}`);

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
    loader: { ".ts": "ts", ".tsx": "tsx", ".css": "css", ".json": "json" },
    define: { "process.env.NODE_ENV": '"production"' },
    logLevel: "warning",
  });
  return {
    js: result.outputFiles.find((f) => f.path.endsWith(".js")).text,
    css: result.outputFiles.find((f) => f.path.endsWith(".css")).text,
  };
}

function serve({ js, css }) {
  const server = createServer(async (req, res) => {
    const path = (req.url || "/").split("?")[0];
    try {
      if (path === "/harness.iife.js") return res.writeHead(200, { "content-type": contentTypes[".js"] }), res.end(js);
      if (path === "/harness.css") return res.writeHead(200, { "content-type": contentTypes[".css"] }), res.end(css);
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

const state = (page) =>
  page.evaluate(() => {
    const el = document.querySelector(".fwp");
    return { ...el.dataset };
  });
const wait = (ms) => new Promise((r) => setTimeout(r, ms));

async function capture(browser, url, width, height, tag, evidence) {
  const page = await browser.newPage({ viewport: { width, height }, deviceScaleFactor: 2, hasTouch: width < 500 });
  const shots = {};
  const shot = async (name) => {
    const path = join(out, `${tag}-${name}.png`);
    await page.screenshot({ path, fullPage: true });
    shots[name] = path;
    log(`screenshot ${path}`);
  };

  // ---- the hub: every station and character drawn where the view says.
  await page.goto(`${url}#hub`);
  await page.locator(".fwp-svg").waitFor();
  const fixture = JSON.parse(await readFile(join(here, "../../src/factory/watch/factoryWatchView.fixture.json"), "utf8")).hub;
  const drawn = await page.evaluate(() => ({
    stations: [...document.querySelectorAll("[data-station]")].map((g) => g.getAttribute("data-station")),
    characters: [...document.querySelectorAll("[data-seat].fwp-character")].map((g) => g.getAttribute("data-seat")),
  }));
  check(`${tag}: every station drawn`, fixture.stations.every((s) => drawn.stations.includes(s.id)), drawn.stations.join(","));
  check(`${tag}: every character drawn`, fixture.characters.every((c) => drawn.characters.includes(c.seat)), drawn.characters.join(","));

  // ---- during a step animation: jump to the talked step, let it play a moment, pause.
  await page.locator(".fwp-step[data-kind='talked'] .fwp-step-main").click();
  await wait(1200);
  await page.getByRole("button", { name: "Pause", exact: true }).click();
  let s = await state(page);
  check(`${tag}: pause stops playback`, s.playing === "false", `playing=${s.playing}`);
  const talking = await page.locator(".fwp-character[data-animation='talked']").count();
  const bubble = await page.locator("[data-effect='bubble']").count();
  check(`${tag}: talked step animates with its bubble`, talking === 1 && bubble === 1, `talking=${talking}, bubble=${bubble}`);
  await shot("hub-step-talked");

  // ---- a delivery, mid-walk, at normal size.
  await page.locator(".fwp-step[data-kind='delivered'] .fwp-step-main").first().click();
  await page.getByRole("button", { name: "Play", exact: true }).click();
  await wait(1500);
  await page.getByRole("button", { name: "Pause", exact: true }).click();
  await shot("hub-step-delivered");

  // ---- the hiccup.
  await page.locator(".fwp-step[data-kind='hiccup'] .fwp-step-main").click();
  await page.getByRole("button", { name: "Play", exact: true }).click();
  await wait(1000);
  await page.getByRole("button", { name: "Pause", exact: true }).click();
  await shot("hub-step-hiccup");

  // ---- zoom and follow: an instant replay of the delivery (slow motion, follows the seat, zooms in).
  await page.locator(".fwp-step[data-kind='delivered'] .fwp-step-replay").first().click();
  await wait(3600);
  s = await state(page);
  check(`${tag}: instant replay is slow motion, zoomed, following`, s.speed === "0.25" && Number(s.zoom) > 1 && s.follow === "website-desk", JSON.stringify(s));
  await page.getByRole("button", { name: "Pause", exact: true }).click();
  await wait(900);
  await shot("hub-zoom-follow");

  // ---- the controls, in the real browser.
  await page.getByRole("button", { name: "Whole floor", exact: true }).click();
  s = await state(page);
  check(`${tag}: Fit returns to the whole floor`, s.zoom === "1.00" && s.follow === "", JSON.stringify(s));
  await page.getByRole("button", { name: "Zoom in", exact: true }).click();
  s = await state(page);
  check(`${tag}: zoom in zooms`, Number(s.zoom) > 1, `zoom=${s.zoom}`);
  await page.getByRole("button", { name: "Fast", exact: true }).click();
  s = await state(page);
  check(`${tag}: Fast sets the speed`, s.speed === "3", `speed=${s.speed}`);
  await page.locator(".fwp-follow select").selectOption("checker");
  s = await state(page);
  check(`${tag}: picking a character follows it`, s.follow === "checker", `follow=${s.follow}`);
  await page.getByRole("button", { name: "From the start", exact: true }).click();
  s = await state(page);
  check(`${tag}: From the start replays from zero`, Number(s.t) < 0.5 && s.playing === "true", JSON.stringify(s));

  // ---- the line halls and the empty view.
  await page.goto(`${url}?lines#lines`);
  await page.locator(".fwp-svg").waitFor();
  await page.locator(".fwp-step[data-kind='woke'] .fwp-step-main").click();
  await wait(1500);
  await page.getByRole("button", { name: "Pause", exact: true }).click();
  await shot("lines-step-woke");

  await page.goto(`${url}?empty#empty`);
  await page.locator("[data-testid='fwp-empty']").waitFor();
  await wait(400);
  await shot("empty");

  evidence[tag] = shots;
  await page.close();
}

async function main() {
  await mkdir(out, { recursive: true });
  const server = await serve(await bundle());
  const url = `http://127.0.0.1:${port}/`;
  log(`serving ${url}, writing to ${out}`);
  const browser = await chromium.launch(process.env.PLAYWRIGHT_CHROMIUM ? { executablePath: process.env.PLAYWRIGHT_CHROMIUM } : {});
  const evidence = { scenario: "the shared watch player drawing the contract fixture", screenshots: {} };
  try {
    await capture(browser, url, 1366, 900, "desktop", evidence.screenshots);
    await capture(browser, url, 390, 844, "phone-390", evidence.screenshots);
  } finally {
    await browser.close();
    server.close();
  }
  evidence.failed = fail;
  await writeFile(join(out, "evidence.json"), JSON.stringify(evidence, null, 2));
  log(fail.length === 0 ? "ALL PASS" : `FAILED: ${fail.join("; ")}`);
  process.exit(fail.length === 0 ? 0 : 1);
}

main().catch((e) => {
  console.error(e);
  process.exit(2);
});
