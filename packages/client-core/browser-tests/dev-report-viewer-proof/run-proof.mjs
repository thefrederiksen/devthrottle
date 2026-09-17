// Dev report viewer proof driver (issue #3010, phase 3 of #2936).
//
// Drives the BUILT Cockpit and phone app, served by a Gateway built from this tree and started by rig.ps1, with a
// real session on a real Director. Three stages, run in order unless --stage names one:
//
//   rig    the rig is what it claims to be, and the Gateway half of the round trip works without any app:
//          R1 - the Gateway answering is the one built from this tree (its version against this commit)
//          R2 - every rig process runs from the rig root, and the rig Gateway sees exactly the rig Director and
//               its fixture session
//          R3 - cc-dev-reports publishes the fixtures AS that session; the hostile reports that pass the shape
//               check publish, and the one with scripts is refused (so the direct test must bypass the check)
//          R4 - an owner send is delivered, and the prompt the Gateway composed - read back from its database -
//               names the row, the column and the chosen option; a cc-dev-reports reply comes back on the
//               owner route verbatim
//          R5 - both built apps load signed in against the rig, and the recording server is on another origin
//   frame  the frame cannot reach the app, in the Cockpit and on the phone (claims F1-F8, see README.md)
//   e2e    end to end at phone width and desktop width (claims E1-E9, see README.md)
//
// --mutation <name> applies one named guard removal (mutations.mjs) to the app's JavaScript IN THE BROWSER ONLY,
// by answering the bundle request with an edited copy. Nothing on disk changes. A mutation whose text is not
// found fails the run, so a guard removal that did not happen can never read as a red.
//
// Run:  node run-proof.mjs [--stage rig|frame|e2e] [--mutation <name>]
//       writes evidence/<stage>-<date>[-<mutation>].json plus screenshots, prints PASS/FAIL, exits non-zero on
//       any FAIL. ASCII only.

import { createServer } from "node:http";
import { readFileSync, writeFileSync, mkdirSync, existsSync, copyFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { dirname, join } from "node:path";
import { createRequire } from "node:module";
import { spawnSync } from "node:child_process";

const requireCjs = createRequire(import.meta.url);
const playwrightTarget = process.env.PLAYWRIGHT_PATH || "playwright";
let chromium;
try {
  ({ chromium } = requireCjs(playwrightTarget));
} catch (err) {
  console.error(
    `[viewer-proof] FAIL: cannot load Playwright from "${playwrightTarget}" (${err.code || err.message}).\n` +
      "[viewer-proof] Set PLAYWRIGHT_PATH to the playwright package directory of an install, for example\n" +
      "[viewer-proof]   PLAYWRIGHT_PATH=<npm global root>/@playwright/cli/node_modules/playwright node run-proof.mjs",
  );
  process.exit(1);
}

// ---------------------------------------------------------------------------------------------------- config

const here = dirname(fileURLToPath(import.meta.url));
const repo = join(here, "../../../..");
const args = process.argv.slice(2);
const argValue = (name) => {
  const i = args.indexOf(name);
  return i >= 0 ? args[i + 1] : undefined;
};
for (const a of args) {
  if (a.startsWith("--") && !["--stage", "--mutation"].includes(a)) {
    console.error(`[viewer-proof] FAIL: unknown flag ${a}`);
    process.exit(2);
  }
}
const stageArg = argValue("--stage");
const stages = stageArg ? [stageArg] : ["rig", "frame", "e2e"];
for (const s of stages) {
  if (!["rig", "frame", "e2e"].includes(s)) {
    console.error(`[viewer-proof] FAIL: --stage must be rig, frame or e2e, not ${s}`);
    process.exit(2);
  }
}
const mutationName = argValue("--mutation");

const rigRoot = process.env.RIG_ROOT || join(process.env.LOCALAPPDATA || "", "dev-report-proof-rig");
const gateway = process.env.RIG_GATEWAY || "http://127.0.0.1:7931";
const evilPort = Number(process.env.EVIL_PORT || 7941);
const evil = `http://127.0.0.1:${evilPort}`;
const PHONE = { width: 390, height: 844 };
const DESKTOP = { width: 1400, height: 900 };
const workDir = join(rigRoot, "work");
const date = new Date().toISOString().slice(0, 10);
const evidenceDir = join(here, "evidence");
mkdirSync(evidenceDir, { recursive: true });
mkdirSync(workDir, { recursive: true });

// The viewer's test ids (mission/dev-reports-p3-viewer).
const T = {
  reportsTab: '[data-testid="session-tab-reports"]',
  listRow: (id) => `[data-testid="dev-report-row"][data-report-id="${id}"]`,
  frame: '[data-testid="dev-report-frame"]',
  viewer: '[data-testid="dev-report-viewer"]',
  conversation: '[data-testid="dev-report-conversation"]',
  queuedItem: '[data-testid="dev-report-queued-item"]',
  sentItem: '[data-testid="dev-report-sent-item"]',
  reply: '[data-testid="dev-report-reply"]',
  send: '[data-testid="dev-report-send"]',
  sheetOpen: '[data-testid="report-conversation-open"]',
  sheetClose: '[data-testid="report-conversation-close"]',
};

const log = (m) => console.log(`[viewer-proof] ${m}`);
const results = [];
// The rig's machine token stands in for the owner's device key, so a red run that reads the app's storage prints it.
// It is a throwaway rig credential, and it is still never written out: every output passes through redact().
function redact(text) {
  let out = String(text);
  for (const file of [join(rigRoot, "config", "director", "gateway-token.txt")]) {
    if (existsSync(file)) {
      const secret = readFileSync(file, "utf8").trim();
      if (secret) out = out.split(secret).join("<rig token>");
    }
  }
  if (existsSync(join(rigRoot, "session.json"))) {
    const key = JSON.parse(readFileSync(join(rigRoot, "session.json"), "utf8")).CC_GATEWAY_SESSION_KEY;
    if (key) out = out.split(key).join("<session key>");
  }
  return out;
}
function check(claim, ok, detail) {
  detail = redact(detail);
  log(`${ok ? "PASS" : "FAIL"} - ${claim}: ${detail}`);
  results.push({ claim, ok: !!ok, detail });
}
const evidence = { mutation: mutationName || null, gateway, rigRoot, evil, steps: {}, mutationApplied: {} };

async function waitFor(desc, fn, timeoutMs = 20000, everyMs = 250) {
  const start = Date.now();
  let last;
  for (;;) {
    try {
      last = await fn();
      if (last) return last;
    } catch (err) {
      last = err;
    }
    if (Date.now() - start > timeoutMs) throw new Error(`timed out after ${timeoutMs} ms waiting for: ${desc}`);
    await new Promise((r) => setTimeout(r, everyMs));
  }
}

// ---------------------------------------------------------------------------------------------------- rig access

function readRigToken() {
  const file = join(rigRoot, "config", "director", "gateway-token.txt");
  if (!existsSync(file)) throw new Error(`no rig token at ${file} - run rig.ps1 up`);
  return readFileSync(file, "utf8").trim();
}

function readSession() {
  const file = join(rigRoot, "session.json");
  if (!existsSync(file)) throw new Error(`no fixture session at ${file} - run rig.ps1 session`);
  return JSON.parse(readFileSync(file, "utf8"));
}

async function owner(method, path, body) {
  const res = await fetch(gateway + path, {
    method,
    headers: { Authorization: `Bearer ${readRigToken()}`, ...(body ? { "content-type": "application/json" } : {}) },
    body: body ? JSON.stringify(body) : undefined,
  });
  const text = await res.text();
  let json = null;
  try { json = JSON.parse(text); } catch { json = null; }
  return { status: res.status, text, json, headers: res.headers };
}

// cc-dev-reports run AS the fixture session: its Gateway URL, its session key, its session id.
function devReports(...toolArgs) {
  const s = readSession();
  const r = spawnSync("python", [join(repo, "tools", "cc-dev-reports", "main.py"), ...toolArgs, "--json"], {
    env: { ...process.env, CC_GATEWAY_URL: s.CC_GATEWAY_URL, CC_GATEWAY_SESSION_KEY: s.CC_GATEWAY_SESSION_KEY, CC_SESSION_ID: s.CC_SESSION_ID },
    encoding: "utf8",
    timeout: 60000,
  });
  let json = null;
  try { json = JSON.parse(r.stdout); } catch { json = null; }
  return { exit: r.status, json, stdout: r.stdout, stderr: r.stderr };
}

// Put a fixture into the rig's work folder (the report key is the file path, so the same name republishes),
// with __EVIL__ pointing at the recording server.
function stageFixture(fixture, asName) {
  const text = readFileSync(join(here, "fixtures", fixture), "utf8").replaceAll("__EVIL__", evil);
  const path = join(workDir, asName || fixture);
  writeFileSync(path, text);
  return path;
}

function publish(fixture, asName, { mayRefuse = false } = {}) {
  const path = stageFixture(fixture, asName);
  const r = devReports("open", path);
  if (!mayRefuse && r.exit !== 0) {
    throw new Error(`cc-dev-reports open ${fixture} failed (exit ${r.exit}): ${r.stdout} ${r.stderr} - if the session has ended, run rig.ps1 session`);
  }
  return { path, ...r };
}

// The Gateway's own database: the rows of the session's transcript it has stored. The delivered prompt is the
// user turn that follows the send.
function sessionUserTurns(sessionId) {
  const py = [
    "import sqlite3, sys, json",
    "c = sqlite3.connect(sys.argv[1])",
    "rows = c.execute(\"select Ordinal, PartsJson, ReceivedAtUtc from session_turns where SessionId = ? and Role = 'User' order by Ordinal\", (sys.argv[2],)).fetchall()",
    "out = []",
    "for o, parts, at in rows:",
    "    texts = [p.get('text') or '' for p in json.loads(parts) if p.get('kind') == 'Text']",
    "    if texts: out.append({'ordinal': o, 'text': '\\n'.join(texts), 'receivedAtUtc': at})",
    "sys.stdout.write(json.dumps(out, ensure_ascii=True))",
  ].join("\n");
  const r = spawnSync("python", ["-c", py, join(rigRoot, "gateway.db"), sessionId], { encoding: "utf8", timeout: 30000 });
  if (r.status !== 0) throw new Error(`reading session_turns failed: ${r.stderr}`);
  return JSON.parse(r.stdout);
}

// ---------------------------------------------------------------------------------------------------- recording server

const evilHits = [];
function serveEvil() {
  const server = createServer((req, res) => {
    const url = req.url || "/";
    evilHits.push({ at: new Date().toISOString(), method: req.method, url, referer: req.headers.referer || "" });
    if (url.startsWith("/evil.html")) {
      res.writeHead(200, { "content-type": "text/html; charset=utf-8" });
      res.end(readFileSync(join(here, "fixtures", "evil.html")));
      return;
    }
    res.writeHead(204, { "access-control-allow-origin": "*" });
    res.end();
  });
  return new Promise((resolve) => server.listen(evilPort, "127.0.0.1", () => resolve(server)));
}
const hitsSince = (mark) => evilHits.slice(mark);

// ---------------------------------------------------------------------------------------------------- browser helpers

let mutation = null;
if (mutationName) {
  const { MUTATIONS } = await import("./mutations.mjs");
  mutation = MUTATIONS[mutationName];
  if (!mutation) {
    console.error(`[viewer-proof] FAIL: no mutation named ${mutationName} in mutations.mjs`);
    process.exit(2);
  }
}

async function newAppContext(browser, viewport) {
  const token = readRigToken();
  // Service workers are blocked so every bundle request reaches the network, where a mutation can answer it; a
  // worker serving a cached bundle would bypass the mutation and report a guard as present when it was removed.
  const ctx = await browser.newContext({ viewport, serviceWorkers: "block" });
  // Signed in the way an enrolled browser is: the account store holds the key, and the Cockpit's server-side gate
  // reads the Gateway cookie. The rig Gateway's machine token stands in for a device key (the owner routes take
  // either).
  await ctx.addInitScript((t) => {
    try {
      if (!localStorage.getItem("cc.accounts")) {
        localStorage.setItem("cc.accounts", JSON.stringify([{ id: "proof", label: "Proof", email: null, deviceKey: t, installId: "proof-install" }]));
        localStorage.setItem("cc.activeAccount", "proof");
      }
    } catch { /* the frame has no storage; only the app's own origin is seeded */ }
  }, token);
  await ctx.addCookies([{ name: "cc-gateway-token", value: token, url: gateway }]);
  if (mutation) {
    const applied = new Set();
    await ctx.route(mutation.urlPattern, async (route) => {
      const response = await route.fetch();
      let body = await response.text();
      const url = new URL(route.request().url()).pathname;
      const bundle = (evidence.mutationApplied[url] = evidence.mutationApplied[url] || []);
      mutation.replace.forEach(([from, to], i) => {
        const hit = typeof from === "string" ? body.includes(from) : from.test(body);
        if (typeof from !== "string") from.lastIndex = 0;
        if (hit) body = typeof from === "string" ? body.split(from).join(to) : body.replace(from, to);
        if (hit && !bundle.includes(i)) bundle.push(i);
      });
      await route.fulfill({ response, body });
    });
  }
  const page = await ctx.newPage();
  page.on("crash", () => check("the app page did not crash", false, `the renderer crashed at ${page.url()}`));
  const sends = [];
  page.on("request", (r) => {
    if (r.method() === "POST" && /\/dev-reports\/[^/]+\/send$/.test(new URL(r.url()).pathname)) {
      sends.push({ at: new Date().toISOString(), url: r.url(), body: r.postData() });
    }
  });
  const popups = [];
  ctx.on("page", (p) => { if (p !== page) popups.push(p.url()); });
  return { ctx, page, sends, popups };
}

// Every replacement of the mutation must have matched in every app bundle that carries the frame host.
function assertMutationApplied(app) {
  if (!mutation) return;
  const bundles = Object.entries(evidence.mutationApplied).filter(([url]) => (app === "phone") === url.startsWith("/mobile/"));
  const ok = bundles.length > 0 && bundles.some(([, hits]) => hits.length === mutation.replace.length);
  check(`mutation ${mutationName} was applied to the ${app} bundle`, ok, JSON.stringify(bundles));
}

async function screenshot(page, name) {
  const file = join(evidenceDir, `${name}${mutationName ? "-" + mutationName : ""}.png`);
  await page.screenshot({ path: file });
  return file;
}

// Open one report in an app. The routes are the ones the viewer brief names.
async function openReport(page, app, sessionId, reportId) {
  if (app === "phone") {
    await page.goto(`${gateway}/mobile/session/${encodeURIComponent(sessionId)}`);
    await page.locator(T.reportsTab).click({ timeout: 20000 });
    await page.locator(T.listRow(reportId)).click({ timeout: 20000 });
  } else {
    await page.goto(`${gateway}/session/${encodeURIComponent(sessionId)}`); // the Cockpit is served at the site root
    await page.locator(T.reportsTab).click({ timeout: 20000 });
    await page.locator(T.listRow(reportId)).click({ timeout: 20000 });
  }
  await page.locator(T.frame).waitFor({ timeout: 20000 });
  await waitFor("the host to be connected to the page", async () => (await page.locator(T.viewer).getAttribute("data-connected")) === "true", 20000);
}

// The conversation panel: beside the report in the Cockpit, in a bottom sheet on the phone.
async function withConversation(page, app, fn) {
  if (app === "phone") await page.locator(T.sheetOpen).click();
  try {
    return await fn(page.locator(T.conversation));
  } finally {
    if (app === "phone") await page.locator(T.sheetClose).click();
  }
}
const panelText = (page, app) => withConversation(page, app, (c) => c.innerText());

// The report frame as a Playwright Frame (for reading inside it), found by its element.
async function reportFrame(page) {
  const handle = await page.locator(T.frame).elementHandle();
  return handle.contentFrame();
}

// Inside the notes tray (its shadow root): Playwright's CSS locators pierce open shadow roots.
const tray = (page) => page.frameLocator(T.frame);

// ---------------------------------------------------------------------------------------------------- stage: rig

async function stageRig(browser) {
  // R1 - the Gateway answering is the one built from this tree.
  const health = await (await fetch(`${gateway}/healthz`)).json();
  const builtSha = String(health.version || "").split("+")[1] || "";
  const head = spawnSync("git", ["-C", repo, "rev-parse", "HEAD"], { encoding: "utf8" }).stdout.trim();
  const ancestor = builtSha ? spawnSync("git", ["-C", repo, "merge-base", "--is-ancestor", builtSha, "HEAD"]).status === 0 : false;
  const productDiff = builtSha
    ? spawnSync("git", ["-C", repo, "diff", "--name-only", builtSha, "HEAD", "--", "src", "apps", "packages/client-core/src", "tools/cc-dev-reports"], { encoding: "utf8" }).stdout.trim()
    : "(no built commit)";
  const shells = {};
  for (const shell of ["c", "mobile"]) {
    const f = join(rigRoot, "gateway", "wwwroot", shell, "build.json");
    shells[shell] = existsSync(f) ? JSON.parse(readFileSync(f, "utf8")) : null;
  }
  evidence.steps.R1 = { health, builtSha, head, ancestor, productChangedSinceBuild: productDiff, shells };
  check(
    "R1: the rig Gateway is the one built from this tree, and no product code changed since it was built",
    builtSha.length === 40 && ancestor && productDiff === "",
    `gateway version ${health.version}; this worktree HEAD ${head}; built commit is an ancestor: ${ancestor}; ` +
      `product files changed since: ${productDiff === "" ? "none" : JSON.stringify(productDiff)}; shells ${JSON.stringify(shells)}`,
  );

  // R2 - processes and what the Gateway sees.
  const ps = spawnSync("powershell", ["-NoProfile", "-Command",
    "Get-Process | Where-Object { $_.Path -like '*dev-report-proof-rig*' } | Select-Object Id, Path | ConvertTo-Json"], { encoding: "utf8" });
  const procs = JSON.parse(ps.stdout || "[]");
  const procList = Array.isArray(procs) ? procs : [procs];
  const directors = (await owner("GET", "/directors")).json;
  const directorItems = Array.isArray(directors) ? directors : directors.directors || [];
  const sessionsResp = (await owner("GET", "/sessions")).json;
  const sessionItems = Array.isArray(sessionsResp) ? sessionsResp : sessionsResp.sessions || [];
  const session = readSession();
  const regDir = join(rigRoot, "instances", "default", "config", "director", "instances");
  const reg = spawnSync("powershell", ["-NoProfile", "-Command", `Get-ChildItem '${regDir}' -Filter *.json | Get-Content -Raw`], { encoding: "utf8" }).stdout;
  const rigDirectorId = (JSON.parse(reg).DirectorId || "").toLowerCase();
  const exes = procList.map((p) => String(p.Path).toLowerCase());
  evidence.steps.R2 = { processes: procList, directors: directorItems.map((d) => d.directorId), sessions: sessionItems.map((s) => ({ id: s.sessionId, name: s.name, directorId: s.directorId })), rigDirectorId };
  check(
    "R2: the rig's Gateway, launcher and Director run from the rig root, and the rig Gateway sees only the rig Director and its session",
    ["gateway\\devthrottle-gateway.exe", "launcher\\cc-launcher.exe", "app\\cc-director.exe"].every((e) => exes.some((x) => x.endsWith(e))) &&
      directorItems.length === 1 && String(directorItems[0].directorId).toLowerCase() === rigDirectorId &&
      sessionItems.some((s) => s.sessionId === session.CC_SESSION_ID && String(s.directorId).toLowerCase() === rigDirectorId) &&
      session.CC_GATEWAY_URL === gateway,
    `processes ${JSON.stringify(procList.map((p) => p.Path))}; directors ${JSON.stringify(evidence.steps.R2.directors)}; fixture session ${session.CC_SESSION_ID} on ${session.CC_GATEWAY_URL}`,
  );

  // R3 - publish as the session.
  const pub = {
    report: publish("report-v1.html", "rig-probe-report.html"),
    hostilePublished: publish("hostile-published.html"),
    hostileRefresh: publish("hostile-refresh.html"),
    hostileDirect: publish("hostile-direct.html", undefined, { mayRefuse: true }),
  };
  evidence.steps.R3 = Object.fromEntries(Object.entries(pub).map(([k, v]) => [k, { exit: v.exit, json: v.json }]));
  check(
    "R3: cc-dev-reports publishes as the session; the hostile reports the shape check lets through publish, and the scripted one is refused",
    pub.report.exit === 0 && pub.report.json.report.sessionId === session.CC_SESSION_ID &&
      pub.hostilePublished.exit === 0 && pub.hostileRefresh.exit === 0 &&
      pub.hostileDirect.exit === 1 && pub.hostileDirect.json.code === "shape_check_failed",
    `report ${pub.report.json?.report?.id} v${pub.report.json?.report?.version}; hostile-published ${pub.hostilePublished.json?.report?.id}; ` +
      `hostile-refresh ${pub.hostileRefresh.json?.report?.id}; hostile-direct refused: ${JSON.stringify(pub.hostileDirect.json?.errors)}`,
  );

  // R4 - owner send, delivery, the composed prompt from the database, and a reply.
  const reportId = pub.report.json.report.id;
  const before = sessionUserTurns(session.CC_SESSION_ID).length;
  const probe = `probe-${Date.now()}`;
  const sent = await owner("POST", `/dev-reports/${reportId}/send`, {
    items: [
      { id: `${probe}-n`, kind: "note", text: "Rig probe: forty-two cannot be right",
        anchor: { type: "table-cell", selector: "#results > tbody > tr:nth-of-type(2) > td:nth-of-type(2)", quote: "42", rowLabel: "Gateway", columnLabel: "Failures" } },
      { id: `${probe}-a`, kind: "answer", questionId: "deploy-window", question: "When should we deploy the fix?",
        optionValue: "monday", optionLabel: "Monday - the team is around", comment: "Rig probe comment" },
    ],
  });
  const delivered = await waitFor("both probe items delivered", async () => {
    const d = (await owner("GET", `/dev-reports/${reportId}`)).json;
    const items = d.items.filter((i) => i.id.startsWith(probe));
    return items.length === 2 && items.every((i) => i.status === "delivered") ? d : null;
  }, 120000, 1000);
  const prompt = await waitFor("the composed prompt in session_turns", () => {
    const turns = sessionUserTurns(session.CC_SESSION_ID).slice(before);
    return turns.find((t) => t.text.includes("Rig probe: forty-two cannot be right"));
  }, 60000, 1000);
  const replyText = `Rig probe reply ${probe} - verbatim, with "quotes" and a dash.`;
  const reply = devReports("reply", replyText, "--report", reportId);
  const detail = (await owner("GET", `/dev-reports/${reportId}`)).json;
  evidence.steps.R4 = { sendResponse: sent.json, itemsAfter: delivered.items.filter((i) => i.id.startsWith(probe)), composedPrompt: prompt, reply: reply.json, repliesAfter: detail.replies };
  check(
    "R4: an owner send is delivered, the prompt the Gateway composed names the row, the column and the option, and a reply comes back verbatim",
    sent.status === 200 &&
      prompt.text.includes('row "Gateway"') && prompt.text.includes('column "Failures"') &&
      prompt.text.includes('"Monday - the team is around" (value "monday")') &&
      reply.exit === 0 && detail.replies.some((r) => r.text === replyText),
    `send ${sent.status} ${sent.text}; prompt ordinal ${prompt.ordinal}: ${JSON.stringify(prompt.text.slice(0, 400))}...; reply exit ${reply.exit}`,
  );

  // R5 - the apps load signed in, and the recording server is another origin.
  const shots = {};
  for (const [app, vp, path] of [["phone", PHONE, "/mobile/"], ["cockpit", DESKTOP, "/sessions"]]) {
    const { ctx, page } = await newAppContext(browser, vp);
    await page.goto(gateway + path);
    try {
      await waitFor(`${app} to show the fixture session`, async () => (await page.locator("body").innerText()).includes("dev report proof fixture"), 30000);
    } catch (err) {
      await screenshot(page, `rig-${app}-signed-in-TIMEOUT`);
      throw new Error(`${err.message}; the page at ${page.url()} showed: ${JSON.stringify((await page.locator("body").innerText()).slice(0, 400))}`);
    }
    shots[app] = { url: page.url(), screenshot: await screenshot(page, `rig-${app}-signed-in`) };
    await ctx.close();
  }
  const mark = evilHits.length;
  const evilProbe = await fetch(`${evil}/beacon/rig-probe`);
  evidence.steps.R5 = { shots, evilOrigin: new URL(evil).origin, appOrigin: new URL(gateway).origin, evilProbeStatus: evilProbe.status };
  check(
    "R5: the built phone app and Cockpit load signed in against the rig, and the recording server is a different origin",
    shots.phone.url.startsWith(`${gateway}/mobile/`) && shots.cockpit.url.startsWith(`${gateway}/sessions`) &&
      new URL(evil).origin !== new URL(gateway).origin && hitsSince(mark).length === 1,
    JSON.stringify(evidence.steps.R5),
  );
}

// ---------------------------------------------------------------------------------------------------- stage: frame

async function stageFrame(browser) {
  const session = readSession();
  const published = publish("hostile-published.html").json.report;
  const refresh = publish("hostile-refresh.html").json.report;
  // The carrier is a well-formed report; the app's own request for its bytes is answered with the scripted
  // hostile report, which the Gateway would refuse. So the host is handed what the shape check never saw.
  const carrier = publish("report-v1.html", "direct-carrier.html").json.report;
  const directHtml = readFileSync(join(here, "fixtures", "hostile-direct.html"), "utf8").replaceAll("__EVIL__", evil);

  for (const [app, vp] of [["phone", PHONE], ["cockpit", DESKTOP]]) {
    const out = (evidence.steps[`frame-${app}`] = {});

    // ---- F1-F3: the scripted report handed straight to the host.
    {
      const { ctx, page, sends } = await newAppContext(browser, vp);
      await ctx.route(new RegExp(`/dev-reports/${carrier.id}/html`), async (route) => {
        const response = await route.fetch();
        await route.fulfill({ response, body: directHtml });
      });
      const mark = evilHits.length;
      await openReport(page, app, session.CC_SESSION_ID, carrier.id);
      await page.waitForTimeout(4000);
      const frameAttr = await page.locator(T.frame).evaluate((el) => ({ sandbox: el.getAttribute("sandbox"), hostCanReadFrame: el.contentDocument !== null }));
      const f = await reportFrame(page);
      const inside = await f.evaluate(() => {
        const o = { origin: window.origin };
        const head = document.head;
        const first = head && head.firstElementChild;
        o.firstHeadElement = first ? first.outerHTML.slice(0, 300) : null;
        o.scriptRan = document.documentElement.getAttribute("data-hostile-script-ran");
        o.handlerRan = document.documentElement.getAttribute("data-hostile-handler-ran");
        o.tokenSeen = document.documentElement.getAttribute("data-hostile-token");
        // As though a report script DID run: what could it reach?
        try { o.parentStorage = JSON.stringify(window.parent.localStorage); } catch (e) { o.parentStorage = "blocked: " + e.name; }
        try { o.parentCookie = window.parent.document.cookie; } catch (e) { o.parentCookie = "blocked: " + e.name; }
        try { o.parentDom = window.parent.document.title; } catch (e) { o.parentDom = "blocked: " + e.name; }
        try { o.ownStorage = String(window.localStorage.length); } catch (e) { o.ownStorage = "blocked: " + e.name; }
        try { o.ownCookie = document.cookie; } catch (e) { o.ownCookie = "blocked: " + e.name; }
        return o;
      });
      await screenshot(page, `frame-${app}-direct`);
      const hits = hitsSince(mark);
      out.direct = { frameAttr, inside, evilHits: hits, sends };
      check(`F1 (${app}): the report is in <iframe sandbox="allow-scripts">, opaque origin, host cannot read it`,
        frameAttr.sandbox === "allow-scripts" && !frameAttr.hostCanReadFrame && inside.origin === "null",
        JSON.stringify({ frameAttr, origin: inside.origin }));
      check(`F2 (${app}): the host's policy is the first thing in the head, and the report's scripts, handler, guessed nonce, external script and token snooping do not run`,
        /http-equiv="Content-Security-Policy"/i.test(inside.firstHeadElement || "") && !inside.scriptRan && !inside.handlerRan && !inside.tokenSeen &&
          hits.filter((h) => h.url.startsWith("/beacon/direct-")).length === 0,
        JSON.stringify({ firstHeadElement: inside.firstHeadElement, scriptRan: inside.scriptRan, handlerRan: inside.handlerRan, tokenSeen: inside.tokenSeen, beacons: hits.map((h) => h.url) }));
      check(`F3 (${app}): code in the frame cannot read the app's storage, cookies or page, and has none of its own`,
        inside.parentStorage.startsWith("blocked") && inside.parentCookie.startsWith("blocked") && inside.parentDom.startsWith("blocked") &&
          inside.ownStorage.startsWith("blocked") && inside.ownCookie.startsWith("blocked"),
        JSON.stringify({ parentStorage: inside.parentStorage, parentCookie: inside.parentCookie, parentDom: inside.parentDom, ownStorage: inside.ownStorage, ownCookie: inside.ownCookie }));
      check(`F4a (${app}): the direct report's forged send and forged ready (no token) make the app send nothing`,
        sends.length === 0, JSON.stringify(sends));
      assertMutationApplied(app);
      await ctx.close();
    }

    // ---- F4-F8: the published hostile report, then a click away to another origin.
    {
      const { ctx, page, sends, popups } = await newAppContext(browser, vp);
      const mark = evilHits.length;
      await openReport(page, app, session.CC_SESSION_ID, published.id);
      await page.waitForTimeout(3000);
      const appUrl = page.url();
      const t = tray(page);
      const loadHits = hitsSince(mark);
      await screenshot(page, `frame-${app}-published`);
      check(`F5 (${app}): nothing the published hostile report loads by itself reaches another origin (base, report policy, stylesheets, fonts, backgrounds, images, media, objects, nested frames)`,
        loadHits.length === 0, JSON.stringify(loadHits.map((h) => h.url)));
      // Everything the check lets through that needs a click, clicked the way the owner might. None of these may
      // navigate the frame or reach another origin, so the frame must still be showing the report afterwards.
      const markClicks = evilHits.length;
      for (const id of ["#fake-composer", "#fake-send", "#link-js", "#form-submit", "#pick-monday"]) {
        await t.locator(id).click({ timeout: 5000, force: true }).catch((e) => { out[`click ${id}`] = String(e.message).slice(0, 200); });
        await page.waitForTimeout(700);
      }
      const clickHits = hitsSince(markClicks);
      const stillReport = await t.locator("#attacks").count().catch(() => 0);
      out.clicks = { clickHits, stillReport, sends };
      check(`F4b (${app}): the fake Send, the javascript: link, the form, and a focus and a pick that a stylesheet watches reach nothing and make the app send nothing`,
        sends.length === 0 && clickHits.length === 0 && stillReport === 1 && !out["click #fake-send"] && !out["click #form-submit"],
        JSON.stringify(out.clicks));

      // A data: link is a navigation too: the frame leaves the report for a page the host did not load.
      const markData = evilHits.length;
      const connectedBeforeData = await page.locator(T.viewer).getAttribute("data-connected");
      await t.locator("#link-data").click({ force: true });
      await page.waitForTimeout(8000); // longer than the poll interval, so a host push would have happened
      const dataFrame = await reportFrame(page);
      const dataPage = await dataFrame.evaluate(() => ({
        url: location.href.slice(0, 40),
        scriptRan: document.documentElement.getAttribute("data-data-url-script-ran"),
      })).catch((e) => ({ error: String(e.message).slice(0, 200) }));
      const connectedAfterData = await page.locator(T.viewer).getAttribute("data-connected");
      out.dataLink = { connectedBeforeData, connectedAfterData, dataPage, hits: hitsSince(markData), sends };
      await screenshot(page, `frame-${app}-data-link`);
      check(`F7a (${app}): a data: link takes the frame away; the host drops the page (connected false), nothing reaches another origin, and the app sends nothing`,
        connectedBeforeData === "true" && connectedAfterData === "false" && hitsSince(markData).length === 0 && sends.length === 0,
        JSON.stringify(out.dataLink));
      // Back to the report for the remaining attacks.
      await ctx.close();
    }
    {
      const { ctx, page, sends, popups } = await newAppContext(browser, vp);
      await openReport(page, app, session.CC_SESSION_ID, published.id);
      await page.waitForTimeout(2000);
      const appUrl = page.url();
      const t = tray(page);

      // The popup and the top window: blocked by the sandbox.
      await t.locator("#link-blank").click({ force: true }).catch(() => {});
      await t.locator("#link-top").click({ force: true }).catch(() => {});
      await page.waitForTimeout(1500);
      check(`F6 (${app}): the report cannot open a popup or take over the app window`,
        popups.length === 0 && page.url() === appUrl, JSON.stringify({ popups, url: page.url(), appUrl }));

      // A plain link to a page whose scripts run.
      const markNav = evilHits.length;
      await t.locator("#link-away").click({ force: true });
      await waitFor("the other-origin page to run", () => hitsSince(markNav).some((h) => h.url.startsWith("/beacon/evil-link-ran")), 15000);
      await page.waitForTimeout(8000); // longer than the poll interval, so a host push would have happened
      const navHits = hitsSince(markNav);
      const connectedAfterNav = await page.locator(T.viewer).getAttribute("data-connected");
      await screenshot(page, `frame-${app}-navigated`);
      out.navigated = { navHits, sends, connectedAfterNav };
      check(`F7 (${app}): after a link takes the frame to another origin, that page runs but the host drops it, it gets nothing, and its forged ready and sends are refused`,
        connectedAfterNav === "false" && navHits.some((h) => h.url.startsWith("/beacon/evil-link-ran")) &&
          navHits.filter((h) => /port-message|window-message/.test(h.url)).length === 0 &&
          navHits.filter((h) => /top-navigation-worked/.test(h.url)).length === 0 && sends.length === 0,
        JSON.stringify(navHits.map((h) => decodeURIComponent(h.url).slice(0, 200))));
      await ctx.close();
    }

    // ---- F8: the report navigates itself away with a meta refresh.
    {
      const { ctx, page, sends } = await newAppContext(browser, vp);
      const mark = evilHits.length;
      await openReport(page, app, session.CC_SESSION_ID, refresh.id);
      await waitFor("the meta refresh to land on the other origin", () => hitsSince(mark).some((h) => h.url.startsWith("/beacon/evil-meta-refresh-ran")), 20000);
      await page.waitForTimeout(8000);
      const hits = hitsSince(mark);
      const connectedAfterRefresh = await page.locator(T.viewer).getAttribute("data-connected");
      await screenshot(page, `frame-${app}-meta-refresh`);
      out.metaRefresh = { hits, sends, connectedAfterRefresh };
      check(`F8 (${app}): after a meta refresh takes the frame away, the host drops the page, it gets nothing and can send nothing`,
        connectedAfterRefresh === "false" && hits.filter((h) => /port-message|window-message/.test(h.url)).length === 0 && sends.length === 0,
        JSON.stringify(hits.map((h) => decodeURIComponent(h.url).slice(0, 200))));
      await ctx.close();
    }
  }
}

// ---------------------------------------------------------------------------------------------------- stage: e2e

async function stageE2e(browser) {
  const session = readSession();
  const key = `e2e-report-${Date.now()}.html`;
  const v1 = publish("report-v1.html", key);
  const reportId = v1.json && v1.json.report && v1.json.report.id;
  evidence.steps.E1 = { exit: v1.exit, json: v1.json };
  check("E1: cc-dev-reports open publishes the report", v1.exit === 0 && v1.json.report.version === 1, JSON.stringify(v1.json && v1.json.report));

  // ---- phone at 390 by 844
  const phone = await newAppContext(browser, PHONE);
  const p = phone.page;
  await openReport(p, "phone", session.CC_SESSION_ID, reportId);
  const t = tray(p);
  await t.locator("#version-marker").waitFor({ timeout: 20000 });
  await screenshot(p, "e2e-phone-1-open");
  check("E2: the report opens full screen in the phone app at 390 by 844",
    (await t.locator("#version-marker").innerText()) === "Report version 1", p.url());

  // Note a table cell and answer the question in the page; Send from the app's conversation sheet.
  await t.locator("[data-drn=pick]").click();
  await t.locator("#results tbody tr:nth-child(2) td:nth-child(3)").click({ position: { x: 4, y: 4 } });
  await t.locator("[data-drn=composer-text]").fill("Forty-two failures from the phone cannot be right.");
  await t.locator("[data-drn=composer-queue]").click();
  await t.locator("input[name=deploy-window][value=monday]").check();
  await t.locator("textarea[data-dev-report-comment]").fill("Monday, from the phone.");
  await t.locator("[data-drn=queue-answer]").click();
  const turnsBefore = sessionUserTurns(session.CC_SESSION_ID).length;
  await p.locator(T.sheetOpen).click();
  await waitFor("both items queued in the app", async () => (await p.locator(T.queuedItem).count()) === 2, 15000);
  await screenshot(p, "e2e-phone-2-queued");
  await p.locator(T.send).click();
  const detail = await waitFor("both items to reach the Gateway and be delivered", async () => {
    const d = (await owner("GET", `/dev-reports/${reportId}`)).json;
    return d.items.length === 2 && d.items.every((i) => i.status === "delivered") ? d : null;
  }, 120000, 1000);
  const labels = detail.items.map((i) => i.statusLabel);
  await waitFor("the app to show the Gateway's words for both items", async () => {
    const shown = await p.locator(`${T.sentItem} [data-testid="dev-report-item-status"]`).allInnerTexts();
    return shown.length === 2 && labels.every((l) => shown.includes(l));
  }, 30000);
  await screenshot(p, "e2e-phone-3-sent");
  evidence.steps.E3 = { sendRequests: phone.sends, items: detail.items };
  check("E3: Send posts the note and the answer, and the app shows the Gateway's status words verbatim",
    phone.sends.length >= 1 &&
      detail.items.some((i) => i.kind === "note" && i.anchor.type === "table-cell" && i.anchor.rowLabel === "Gateway" && i.anchor.columnLabel === "Failures") &&
      detail.items.some((i) => i.kind === "answer" && i.optionValue === "monday" && i.comment === "Monday, from the phone."),
    JSON.stringify(detail.items.map((i) => ({ id: i.id, kind: i.kind, status: i.status, statusLabel: i.statusLabel }))));

  const prompt = await waitFor("the composed prompt in the Gateway database", () =>
    sessionUserTurns(session.CC_SESSION_ID).slice(turnsBefore).find((x) => x.text.includes("Forty-two failures from the phone")), 60000, 1000);
  evidence.steps.E4 = { composedPrompt: prompt };
  check("E4: the prompt the Gateway composed, read from its database, names the row, the column and the chosen option",
    prompt.text.includes('row "Gateway"') && prompt.text.includes('column "Failures"') && prompt.text.includes('"Monday - the team is around" (value "monday")'),
    JSON.stringify(prompt.text));

  const replyText = `Checked from the proof at ${new Date().toISOString()} - the fixture double-counted.`;
  const reply = devReports("reply", replyText, "--report", reportId);
  await waitFor("the reply to appear in the phone app", async () => (await p.locator(T.reply).allInnerTexts()).some((x) => x.includes(replyText)), 30000);
  await screenshot(p, "e2e-phone-4-reply");
  check("E5: a cc-dev-reports reply appears in the phone app verbatim", reply.exit === 0, replyText);
  await p.locator(T.sheetClose).click();

  // Half-type a note and scroll, then republish the same file.
  await t.locator("[data-drn=pick]").click();
  await t.locator("#explain").click();
  await t.locator("[data-drn=composer-text]").pressSequentially("half typed on the phone");
  await (await reportFrame(p)).evaluate(() => window.scrollTo(0, 900));
  await p.waitForTimeout(1500);
  copyFileSync(join(here, "fixtures", "report-v2.html"), join(workDir, key));
  const v2 = devReports("open", join(workDir, key));
  await waitFor("the frame to reload in place with version 2", async () => (await tray(p).locator("#version-marker").innerText()) === "Report version 2", 30000);
  await waitFor("the host to be connected to the new page", async () => (await p.locator(T.viewer).getAttribute("data-connected")) === "true", 20000);
  await p.waitForTimeout(1500);
  const after = await (await reportFrame(p)).evaluate(() => {
    let draft = null;
    for (const h of document.querySelectorAll("[data-dev-report-ui]")) {
      const el = h.shadowRoot && h.shadowRoot.querySelector("[data-drn=composer-text]");
      if (el) draft = el.value;
    }
    return { scrollY: window.scrollY, draft };
  });
  await screenshot(p, "e2e-phone-5-republished");
  evidence.steps.E6 = { republish: v2.json, after, url: p.url() };
  check("E6: republishing reloads the page in place, keeping the scroll position and the half-typed note",
    v2.exit === 0 && v2.json.report.version === 2 && Math.abs(after.scrollY - 900) <= 2 && after.draft === "half typed on the phone" &&
      p.url().includes(reportId),
    JSON.stringify(evidence.steps.E6));
  await phone.ctx.close();

  // ---- the Cockpit at desktop width, the same report
  const desk = await newAppContext(browser, DESKTOP);
  const d = desk.page;
  await openReport(d, "cockpit", session.CC_SESSION_ID, reportId);
  await tray(d).locator("#version-marker").waitFor({ timeout: 20000 });
  await waitFor("the Cockpit to show the reply", async () => (await d.locator(T.reply).allInnerTexts()).some((x) => x.includes(replyText)), 30000);
  const deskShown = await d.locator(`${T.sentItem} [data-testid="dev-report-item-status"]`).allInnerTexts();
  await screenshot(d, "e2e-cockpit-1-open");
  check("E7: the same report in the Cockpit at desktop width shows version 2, the Gateway's words and the reply",
    (await tray(d).locator("#version-marker").innerText()) === "Report version 2" && labels.every((l) => deskShown.includes(l)),
    JSON.stringify({ deskShown }));

  // ---- end the session; Send shows the Gateway's refusal sentence
  // The Cockpit is a second browser with its own state for this report. Two notes are queued. When ids were a
  // per-page counter the first took the id the phone already used (n1) and was lost while reading as delivered;
  // ids are now random, and E9 also checks that no id posted here names a different item the Gateway holds.
  const ended = await owner("DELETE", `/sessions/${session.CC_SESSION_ID}`);
  await waitFor("the Gateway to rule the session ended", async () => (await owner("GET", `/dev-reports/${reportId}`)).json.report.sessionEnded === true, 90000, 1000);
  const td = tray(d);
  for (const [target, words] of [["#summary", "First note from the Cockpit after the session ended."], ["#explain", "Second note from the Cockpit after the session ended."]]) {
    await td.locator("[data-drn=pick]").click();
    await td.locator(target).click();
    await td.locator("[data-drn=composer-text]").fill(words);
    await td.locator("[data-drn=composer-queue]").click();
  }
  await waitFor("both notes queued in the app", async () => (await d.locator(T.queuedItem).count()) === 2, 15000);
  await d.locator(T.send).click();
  await d.waitForTimeout(4000);
  const queuedTexts = await d.locator(T.queuedItem).allInnerTexts();
  const sentTexts = await d.locator(T.sentItem).allInnerTexts();
  const detailAfter = (await owner("GET", `/dev-reports/${reportId}`)).json;
  const sentBody = desk.sends.length ? JSON.parse(desk.sends[desk.sends.length - 1].body) : null;
  await screenshot(d, "e2e-cockpit-2-ended-refused");
  evidence.steps.E8 = { deleteSession: ended.status, sentBody, queuedTexts, sentTexts,
    gatewayItems: detailAfter.items.map((i) => ({ id: i.id, kind: i.kind, text: i.text, status: i.status, statusLabel: i.statusLabel })) };
  const secondQueued = queuedTexts.find((x) => x.includes("Second note from the Cockpit"));
  check("E8: after the session ends, Send shows the Gateway's refusal sentence and the note stays queued",
    ended.status < 300 && !!secondQueued && /ended/i.test(secondQueued),
    JSON.stringify({ secondQueued }));
  // A note the owner wrote either reaches the Gateway or stays in front of the owner, queued. It must never leave the
  // queue while the Gateway does not hold it - and never be shown as delivered.
  const first = "First note from the Cockpit after the session ended.";
  const trayQueued = await td.locator("[data-drn=queued]").innerText();
  const traySent = await td.locator("[data-drn=sent]").innerText();
  const gatewayHasFirst = detailAfter.items.some((i) => i.text === first);
  const stillQueuedInApp = queuedTexts.some((x) => x.includes(first));
  const trayShowsFirstAsSent = traySent.includes(first);
  const idsNamingAnotherItem = sentBody
    ? sentBody.items.filter((i) => detailAfter.items.some((g) => g.id === i.id && g.text !== i.text)).map((i) => i.id)
    : null;
  evidence.steps.E9 = { idsPosted: sentBody && sentBody.items.map((i) => i.id), idsNamingAnotherItem, gatewayHoldsTheText: gatewayHasFirst,
    stillQueuedInApp, trayShowsFirstAsSent, traySent, trayQueued, appQueued: queuedTexts, appSent: sentTexts };
  check("E9: a note written on a second device reaches the Gateway or stays queued - it never disappears or reads as delivered",
    idsNamingAnotherItem !== null && idsNamingAnotherItem.length === 0 &&
      (gatewayHasFirst || (stillQueuedInApp && !trayShowsFirstAsSent)),
    JSON.stringify(evidence.steps.E9));
  await desk.ctx.close();
}

// ---------------------------------------------------------------------------------------------------- main

const server = await serveEvil();
log(`recording server on ${evil}; rig Gateway ${gateway}; stages ${stages.join(", ")}${mutationName ? "; mutation " + mutationName : ""}`);
// The full Chromium in its new headless mode, not the headless shell: the headless shell's renderer crashes on
// the built phone app whenever a session on the rig is working (reproduced three times in three; the same page in
// a headed browser and in this mode does not crash). See README.md.
const browser = await chromium.launch({ channel: "chromium" });
let crashed = null;
try {
  for (const s of stages) {
    log(`---- stage ${s}`);
    if (s === "rig") await stageRig(browser);
    if (s === "frame") await stageFrame(browser);
    if (s === "e2e") await stageE2e(browser);
  }
} catch (err) {
  crashed = err;
  check("the run completed", false, String(err && err.stack ? err.stack : err).slice(0, 1500));
} finally {
  await browser.close();
  server.close();
}

evidence.evilHits = evilHits;
evidence.results = results;
const failed = results.filter((r) => !r.ok);
// A mutation run is a red run: it succeeds when every claim the removed guard protects went red in both apps, and
// the removal was really applied to both bundles.
let redVerdict = null;
if (mutation) {
  const redFor = (prefix, app) => results.some((r) => !r.ok && r.claim.startsWith(`${prefix} (${app})`));
  const appliedEverywhere = ["phone", "cockpit"].every((app) => results.some((r) => r.ok && r.claim === `mutation ${mutationName} was applied to the ${app} bundle`));
  const perClaim = mutation.expectRed.flatMap((prefix) => ["phone", "cockpit"].map((app) => ({ claim: `${prefix} (${app})`, red: redFor(prefix, app) })));
  redVerdict = { appliedEverywhere, perClaim, confirmed: appliedEverywhere && perClaim.every((c) => c.red) };
  evidence.redVerdict = redVerdict;
  for (const c of perClaim) log(`${c.red ? "RED AS EXPECTED" : "NOT RED"} - ${c.claim} with ${mutationName}`);
  log(`mutation ${mutationName}: ${redVerdict.confirmed ? "RED CONFIRMED" : "RED NOT CONFIRMED"} (applied to both bundles: ${appliedEverywhere})`);
}
const file = join(evidenceDir, `${stages.join("-")}-${date}${mutationName ? "-" + mutationName : ""}.json`);
writeFileSync(file, redact(JSON.stringify(evidence, null, 2)));
log(`${results.length - failed.length} passed, ${failed.length} failed${crashed ? " (the run stopped early)" : ""}; evidence ${file}`);
process.exit(redVerdict ? (redVerdict.confirmed ? 0 : 1) : failed.length ? 1 : 0);
