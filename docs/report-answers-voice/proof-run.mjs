// Report Answers by Voice - end-to-end proof driver.
//
// Drives the BUILT Cockpit and phone app, served by a Gateway built from this tree and stood up by
// docs/missions/dev-reports/proof/rig.ps1 (build, up, session, report), with a real session on a real Director.
// The browser's microphone is Chromium's fake capture device fed from a recorded clip, so everything after the
// microphone is the real path: the app's recorder, the Gateway's transcription endpoint, the speech model, and the
// words posted back into the report's box.
//
// Run:  PLAYWRIGHT_PATH=<npm global root>/playwright node proof-run.mjs --stage comment|own --clip <file.wav>
//   comment  dictates into the question's comment box, in the Cockpit and on the phone
//   own      picks "Something else", dictates the own answer, queues it and sends it, in the Cockpit
// Screenshots go to docs/report-answers-voice/screens/. Prints PASS/FAIL lines, exits non-zero on any FAIL.
// ASCII only. The rig token is never printed.

import { readFileSync, mkdirSync, existsSync } from "node:fs";
import { createRequire } from "node:module";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

const requireCjs = createRequire(import.meta.url);
const { chromium } = requireCjs(process.env.PLAYWRIGHT_PATH || "playwright");

const here = dirname(fileURLToPath(import.meta.url));
const shots = join(here, "screens");
mkdirSync(shots, { recursive: true });
const rigRoot = join(process.env.LOCALAPPDATA, "dev-report-proof-rig-p3b");
const gateway = "http://127.0.0.1:7951";
const args = process.argv.slice(2);
const arg = (n) => { const i = args.indexOf(n); return i >= 0 ? args[i + 1] : undefined; };
const stage = arg("--stage");
const clip = arg("--clip");
if (!["comment", "own"].includes(stage) || !clip || !existsSync(clip)) {
  console.error("[voice-proof] FAIL: usage --stage comment|own --clip <existing wav>");
  process.exit(2);
}

const token = readFileSync(join(rigRoot, "config", "director", "gateway-token.txt"), "utf8").trim();
const session = JSON.parse(readFileSync(join(rigRoot, "session.json"), "utf8"));
const report = JSON.parse(readFileSync(join(rigRoot, "report.json"), "utf8"));
const sessionId = session.CC_SESSION_ID;
const reportId = report.report.id;

const T = {
  reportsTab: '[data-testid="session-tab-reports"]',
  listRow: (id) => `[data-testid="dev-report-row"][data-report-id="${id}"]`,
  frame: '[data-testid="dev-report-frame"]',
  viewer: '[data-testid="dev-report-viewer"]',
  send: '[data-testid="dev-report-send"]',
  conversation: '[data-testid="dev-report-conversation"]',
  sheetOpen: '[data-testid="report-conversation-open"]',
};
const PHONE = { width: 390, height: 844 };
const DESKTOP = { width: 1440, height: 900 };

let failed = 0;
const say = (m) => console.log(`[voice-proof] ${m}`.split(token).join("<rig token>"));
const check = (name, ok, detail = "") => { if (!ok) failed++; say(`${ok ? "PASS" : "FAIL"} ${name}${detail ? " - " + detail : ""}`); };

async function waitFor(what, fn, ms) {
  const end = Date.now() + ms;
  while (Date.now() < end) {
    if (await fn()) return true;
    await new Promise((r) => setTimeout(r, 250));
  }
  throw new Error(`timed out after ${ms} ms waiting for ${what}`);
}

const browser = await chromium.launch({
  headless: true,
  args: [
    "--use-fake-ui-for-media-stream",
    "--use-fake-device-for-media-stream",
    `--use-file-for-fake-audio-capture=${clip}`,
  ],
});

async function openApp(app) {
  const ctx = await browser.newContext({ viewport: app === "phone" ? PHONE : DESKTOP, serviceWorkers: "block" });
  await ctx.grantPermissions(["microphone"], { origin: gateway });
  await ctx.addInitScript((t) => {
    try {
      if (!localStorage.getItem("cc.accounts")) {
        localStorage.setItem("cc.accounts", JSON.stringify([{ id: "proof", label: "Proof", email: null, deviceKey: t, installId: "proof-install" }]));
        localStorage.setItem("cc.activeAccount", "proof");
      }
    } catch { /* the report frame has no storage */ }
  }, token);
  await ctx.addCookies([{ name: "cc-gateway-token", value: token, url: gateway }]);
  const page = await ctx.newPage();
  const transcriptionCalls = [];
  page.on("response", (r) => {
    const p = new URL(r.url()).pathname;
    if (/\/wingman\/utterance\/[^/]+\/complete$/.test(p) && r.request().method() === "POST") transcriptionCalls.push(`${r.status()} ${p}`);
  });
  await page.goto(app === "phone"
    ? `${gateway}/mobile/session/${encodeURIComponent(sessionId)}`
    : `${gateway}/session/${encodeURIComponent(sessionId)}`);
  await page.locator(T.reportsTab).click({ timeout: 30000 });
  await page.locator(T.listRow(reportId)).click({ timeout: 30000 });
  await page.locator(T.frame).waitFor({ timeout: 30000 });
  await waitFor("the host to connect to the page",
    async () => (await page.locator(T.viewer).getAttribute("data-connected")) === "true", 30000);
  return { ctx, page, transcriptionCalls, frame: page.frameLocator(T.frame) };
}

// Tap the microphone under one box, let the clip play, tap stop, and wait for the words to land.
async function dictate(app, view, fieldSuffix, box, label) {
  const mic = view.frame.locator(`[data-drn="mic"][data-field-id$=":${fieldSuffix}"]`);
  check(`${app}: a microphone is drawn under the ${label}`, await mic.isVisible());
  const before = await box.inputValue();
  await mic.locator('[data-drn="mic-button"]').click();
  await waitFor("the box to say it is recording",
    async () => /record/i.test(await mic.locator('[data-drn="mic-state"]').innerText()), 15000);
  await view.page.waitForTimeout(6500);
  const recordingState = await mic.locator('[data-drn="mic-state"]').innerText();
  await mic.scrollIntoViewIfNeeded();
  await view.page.screenshot({ path: join(shots, `${app}-${fieldSuffix}-recording.png`) });
  check(`${app}: the ${label} shows recording with a timer`, /record/i.test(recordingState) && /\d/.test(recordingState), recordingState);
  await mic.locator('[data-drn="mic-button"]').click();
  await waitFor("the words to land in the box", async () => (await box.inputValue()) !== before, 60000);
  const after = await box.inputValue();
  await view.page.waitForTimeout(500);
  await view.page.screenshot({ path: join(shots, `${app}-${fieldSuffix}-landed.png`) });
  check(`${app}: the transcript landed in the ${label}`, after.length > before.length, JSON.stringify(after));
  check(`${app}: the dictation went through the Gateway`, view.transcriptionCalls.some((c) => c.startsWith("200 ")),
    view.transcriptionCalls.join(", "));
  return after;
}

try {
  if (stage === "comment") {
    for (const app of ["cockpit", "phone"]) {
      const view = await openApp(app);
      const box = view.frame.locator("textarea[data-dev-report-comment]").first();
      await dictate(app, view, "comment", box, "comment box");
      await view.ctx.close();
    }
  } else {
    const app = "cockpit";
    const view = await openApp(app);
    await view.frame.locator('input[type="radio"][value="own-answer"]').check();
    const box = view.frame.locator('textarea[data-drn="own-answer"]').first();
    await box.waitFor({ state: "visible", timeout: 10000 });
    const queue = view.frame.locator('[data-drn="queue-answer"]').first();
    check(`${app}: Queue answer waits for words in the Something else box`, await queue.isDisabled());
    const words = await dictate(app, view, "own", box, "Something else box");
    check(`${app}: Queue answer is enabled once the words land`, await queue.isEnabled());
    await queue.click();
    await view.page.locator(T.send).click({ timeout: 15000 });
    await view.page.waitForTimeout(3000);
    await view.page.screenshot({ path: join(shots, `${app}-own-sent.png`) });
    const panel = await view.page.locator(T.conversation).innerText();
    check(`${app}: the Conversation panel shows Something else with the owner's words`,
      // innerText collapses runs of whitespace the way the page renders them, so compare with whitespace collapsed.
      panel.includes("Something else") && panel.replace(/\s+/g, " ").includes(words.replace(/\s+/g, " ").trim()),
      JSON.stringify(panel.slice(0, 400)));
    await view.ctx.close();
  }
} catch (err) {
  check("the run finished", false, err.message);
} finally {
  await browser.close();
}
say(failed === 0 ? "RESULT: PASS" : `RESULT: FAIL (${failed})`);
process.exit(failed === 0 ? 0 : 1);
