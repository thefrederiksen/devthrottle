// "Watch your factory" piece 6 proof driver: the explainer card and the way in, in both shells, in a real Chromium.
//
// It (1) reads the card's words, the button's words and the Factories page's words OUT OF THE GATEWAY'S C# SOURCE
// (ContributorWatchExplainer.cs, ContributorEndpoints.cs, TeamApp.cs) - so nothing approved is typed here, (2) esbuild-
// bundles the REAL Cockpit contributor Factories page and the REAL phone one, each with its own stylesheets, over a fake
// Gateway that answers in the Gateway's shapes (fake-gateway.ts), (3) serves them over http, and (4) for each shell at a
// desktop width and at 390 pixels: screenshots the Factories page with its "Watch your factory" button, presses it,
// screenshots the explainer card, presses the card's button, and screenshots the player playing after the card.
//
// In the browser it checks: the button's words are the Gateway's; the card shows exactly the Gateway's paragraphs and
// button, in order, and nothing else; pressing it sends POST .../explainer-seen and starts the player; and opening the
// watch view again after that goes straight to the player (the Gateway's record, not the browser's).
//
// Run:  PLAYWRIGHT_PATH=<folder of the playwright package> node run-proof.mjs [output folder]   ->  screenshots + evidence.json, prints PASS/FAIL. ASCII only.

import { build } from "esbuild";
import { createServer } from "node:http";
import { mkdir, readFile, writeFile } from "node:fs/promises";
import { fileURLToPath } from "node:url";
import { dirname, join } from "node:path";
import { createRequire } from "node:module";

const requireCjs = createRequire(import.meta.url);
function loadPlaywright() {
  const target = process.env.PLAYWRIGHT_PATH || "playwright";
  try {
    return requireCjs(target);
  } catch (e) {
    console.error(`[contributor-watch-proof] FAIL - cannot load Playwright from '${target}': ${e.message}\nSet PLAYWRIGHT_PATH to the folder of an installed playwright package.`);
    process.exit(2);
  }
}
const { chromium } = loadPlaywright();

const here = dirname(fileURLToPath(import.meta.url));
const repo = join(here, "../../../..");
const out = process.argv[2] || join(here, "evidence");
const port = Number(process.env.PORT || 8818);
const log = (m) => console.log(`[contributor-watch-proof] ${m}`);

// ---- the words, from the Gateway's source.
async function gatewayWords() {
  const explainer = await readFile(join(repo, "src/CcDirector.Gateway/Teams/ContributorWatchExplainer.cs"), "utf8");
  const pages = await readFile(join(repo, "src/CcDirector.Gateway/Api/ContributorEndpoints.cs"), "utf8");
  const app = await readFile(join(repo, "src/CcDirector.Gateway/Teams/TeamApp.cs"), "utf8");
  const block = /Paragraphs = new\[\]\s*\{([\s\S]*?)\};/.exec(explainer)[1];
  const paragraphs = [...block.matchAll(/"((?:[^"\\]|\\.)*)"/g)].map((m) => JSON.parse(`"${m[1]}"`));
  const constant = (src, name) => JSON.parse(new RegExp(`${name} = ("(?:[^"\\\\]|\\\\.)*");`).exec(src)[1]);
  const pageLabel = (id) => new RegExp(`new TeamAppPage\\("${id}", "([^"]+)"`).exec(app)[1];
  return {
    paragraphs,
    watchLabel: constant(explainer, "WatchLabel"),
    factoriesIntro: constant(pages, "FactoriesIntro"),
    factoriesEmpty: constant(pages, "FactoriesEmpty"),
    factoriesLabel: pageLabel("factories"),
    inboxLabel: pageLabel("inbox"),
    // TeamApp.NotAvailableTo(role), for a Collaborator: the sentence a limited app shows at any other address.
    elsewhere: /NotAvailableTo\(TeamRole role\) => \$"([^"]*)";/.exec(app)[1].replace(/\{[^}]*\}/, "collaborators"),
  };
}

async function bundle(entry) {
  const result = await build({
    entryPoints: [join(here, entry)],
    bundle: true,
    format: "iife",
    write: false,
    outdir: here,
    entryNames: "bundle",
    target: "es2021",
    jsx: "automatic",
    loader: { ".ts": "ts", ".tsx": "tsx", ".css": "css", ".json": "json", ".svg": "dataurl", ".png": "dataurl", ".woff2": "dataurl", ".woff": "dataurl" },
    define: { "process.env.NODE_ENV": '"production"', "import.meta.env": "{}" },
    logLevel: "warning",
  });
  return {
    js: result.outputFiles.find((f) => f.path.endsWith(".js")).text,
    css: result.outputFiles.find((f) => f.path.endsWith(".css"))?.text ?? "",
  };
}

function page(words, shell) {
  return `<!doctype html><html><head><meta charset="utf-8" /><meta name="viewport" content="width=device-width, initial-scale=1" />
<title>Watch your factory - ${shell}</title><link rel="stylesheet" href="/${shell}.css" />
<style>html,body{margin:0;background:var(--bg)} .proof-cockpit-main{padding:24px 28px}</style>
<script>window.__PROOF__ = ${JSON.stringify(words)};</script></head>
<body><div id="root"></div><script src="/${shell}.js"></script></body></html>`;
}

function serve(files) {
  const server = createServer((req, res) => {
    const path = (req.url || "/").split("?")[0];
    const f = files[path];
    if (!f) return res.writeHead(404), res.end("not found");
    res.writeHead(200, { "content-type": f.type });
    res.end(f.body);
  });
  return new Promise((resolve) => server.listen(port, "127.0.0.1", () => resolve(server)));
}

const fail = [];
function check(claim, ok, detail) {
  log(`${ok ? "PASS" : "FAIL"} - ${claim}: ${detail}`);
  if (!ok) fail.push(claim);
}
const wait = (ms) => new Promise((r) => setTimeout(r, ms));

async function run(browser, words, shell, width, height, tag) {
  const pageObj = await browser.newPage({ viewport: { width, height }, deviceScaleFactor: 2, hasTouch: width < 500 });
  const errors = [];
  pageObj.on("pageerror", (e) => errors.push(e.message));
  const shots = {};
  const shot = async (name) => {
    const path = join(out, `${tag}-${name}.png`);
    await pageObj.screenshot({ path, fullPage: true });
    shots[name] = path;
    log(`screenshot ${path}`);
  };
  const base = `http://127.0.0.1:${port}/${shell}.html`;

  // 1. The Factories page with the button.
  await pageObj.goto(`${base}#/contributor/factories`);
  const button = pageObj.getByTestId("contributor-watch-button");
  await button.waitFor();
  await wait(300);
  check(`${tag}: the button's words are the Gateway's`, (await button.textContent()) === words.watchLabel, await button.textContent());
  await shot("factories-page-with-button");

  // 2. The explainer card.
  await button.click();
  await pageObj.getByTestId("watch-explainer").waitFor();
  await wait(300);
  const shown = await pageObj.getByTestId("watch-explainer-paragraph").allTextContents();
  check(`${tag}: the card's paragraphs are exactly the Gateway's, in order`, JSON.stringify(shown) === JSON.stringify(words.paragraphs), JSON.stringify(shown));
  const go = await pageObj.getByTestId("watch-explainer-go").textContent();
  check(`${tag}: the card's button is the Gateway's`, go === words.watchLabel, go);
  const playerBehind = await pageObj.locator(".fwp").count();
  check(`${tag}: nothing plays before the card is pressed`, playerBehind === 0, `players drawn: ${playerBehind}`);
  await shot("explainer-card");

  // 3. The player after the card.
  await pageObj.getByTestId("watch-explainer-go").click();
  await pageObj.locator(".fwp").waitFor();
  await wait(2600);
  const posted = await pageObj.evaluate(() => window.__PROOF_LOG__.filter((l) => l.startsWith("POST ") && l.endsWith("/explainer-seen")));
  check(`${tag}: pressing it tells the Gateway`, posted.length === 1, JSON.stringify(posted));
  const playback = await pageObj.evaluate(() => ({ ...document.querySelector(".fwp").dataset }));
  check(`${tag}: the player is playing after the card`, playback.playing === "true", JSON.stringify(playback));
  await shot("player-after-card");

  // 4. Back to the Factories page by the shell's own way back, and opened again: the Gateway now says the card is seen,
  // so it goes straight to the player. (In the page, not a reload: the fake Gateway's record lives in the page.)
  const back = shell === "cockpit" ? pageObj.getByRole("link", { name: words.factoriesLabel, exact: true }) : pageObj.getByRole("link", { name: "Back" });
  await back.click();
  await pageObj.getByTestId("contributor-watch-button").click();
  await pageObj.locator(".fwp").waitFor();
  const cardAgain = await pageObj.getByTestId("watch-explainer").count();
  check(`${tag}: opened again after the card, it goes straight to the player`, cardAgain === 0, `cards: ${cardAgain}`);

  check(`${tag}: no script error`, errors.length === 0, errors.join(" | ") || "none");
  await pageObj.close();
  return shots;
}

const words = await gatewayWords();
log(`words from the Gateway source: ${words.paragraphs.length} paragraphs, button '${words.watchLabel}', page '${words.factoriesLabel}'`);
check("the Gateway source holds three paragraphs", words.paragraphs.length === 3, String(words.paragraphs.length));
await mkdir(out, { recursive: true });

const cockpit = await bundle("cockpit-entry.tsx");
const phone = await bundle("phone-entry.tsx");
const server = await serve({
  "/cockpit.html": { type: "text/html; charset=utf-8", body: page(words, "cockpit") },
  "/cockpit.js": { type: "text/javascript; charset=utf-8", body: cockpit.js },
  "/cockpit.css": { type: "text/css; charset=utf-8", body: cockpit.css },
  "/phone.html": { type: "text/html; charset=utf-8", body: page(words, "phone") },
  "/phone.js": { type: "text/javascript; charset=utf-8", body: phone.js },
  "/phone.css": { type: "text/css; charset=utf-8", body: phone.css },
});
// PROOF_SERVE_ONLY=1 serves both pages and waits, for looking at them by hand at http://127.0.0.1:8818/cockpit.html and phone.html.
if (process.env.PROOF_SERVE_ONLY) { log("serving"); await new Promise(() => {}); }
const browser = await chromium.launch();
const screenshots = {};
try {
  screenshots["cockpit-desktop"] = await run(browser, words, "cockpit", 1280, 860, "cockpit-desktop");
  screenshots["cockpit-390"] = await run(browser, words, "cockpit", 390, 844, "cockpit-390");
  screenshots["phone-390"] = await run(browser, words, "phone", 390, 844, "phone-390");
  screenshots["phone-desktop"] = await run(browser, words, "phone", 1280, 860, "phone-desktop");
} finally {
  await browser.close();
  server.close();
}

await writeFile(
  join(out, "evidence.json"),
  JSON.stringify({ scenario: "Watch your factory piece 6: the way in, the explainer card, and the player after it, in both shells", words, screenshots, failed: fail }, null, 2),
);
log(fail.length === 0 ? "PASS - every check passed" : `FAIL - ${fail.length} check(s) failed: ${fail.join("; ")}`);
process.exit(fail.length === 0 ? 0 : 1);
