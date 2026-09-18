// Dev report note-taking script proof driver (issue #2940, phase 1 of #2936).
//
// It serves this directory plus the ONE shipping script (src/devreports/dev-report-notes.js, read from
// source so the proof cannot drift from it), and drives a real Chromium against a test host that follows
// CONTRACT.md section 4:
//   claim A - the report runs in <iframe sandbox="allow-scripts"> with no allow-same-origin: the host cannot
//             read the frame's document, and the frame cannot read its parent's or use storage.
//   claim B - the page announces ready, carrying the host's token, with its question ids.
//   claim C - the recommended option is preselected.
//   claim D - a note on a table cell carries the cell's selector, text, row label and column label.
//   claim E - an answer carries the question, the chosen option and the comment.
//   claim F - queued items show as queued, apart from sent, until Send - in the APP's panel, the only
//             conversation on the screen.
//   claim G - the app's Send carries the whole queue, and nothing leaves the queue until the host confirms it.
//   claim H - the host's status words and the agent's reply are shown verbatim; a refused item stays
//             queued with the host's reason, and sending again carries only it.
//   claim I - malformed or foreign messages are ignored whole.
//   claim J - a reload straight after the host pushes a status and a reply loses neither.
//   claim K - a reload brings back the half-typed note, a choice and comment edited AFTER an answer was
//             queued (the newer edit wins), and the scroll position.
//   claim L - with no host, Send shows the exact payload and sends nothing.
//   claim O - HOSTED, the page draws only the note-taking parts: no queued, sent or replies list, no Send
//             and no payload box inside the frame, so the screen has ONE conversation and ONE Send.
//   claim P - the note box opens beside what the note is about and covers neither it nor the question that
//             contains it, and the hosted tray is not a scrolling panel.
//   claim M - a report's own scripts, event handlers and token-snooping observer do not run even behind a
//             decoy "<head>" in a comment; its CSS cannot hide the notes interface; and anything but a
//             token-bearing ready on the window is refused.
//   claim N - when a link takes the frame to another page, that page gets nothing the host pushes - even in
//             the window before its load event, while the host still thinks it is connected - and its
//             forged ready and send are refused.
//
// This proves the SCRIPT against a TEST HOST (index.html). It is not the Cockpit, the phone or the
// Director - those hosts are phases 3 and 4 - and nothing here reaches a Gateway.
//
// Run:  node run-proof.mjs   ->  writes evidence-<date>.json + screenshots, prints PASS/FAIL. ASCII only.

import { createServer } from "node:http";
import { readFile, writeFile } from "node:fs/promises";
import { fileURLToPath } from "node:url";
import { dirname, join, extname } from "node:path";
import { createRequire } from "node:module";

const requireCjs = createRequire(import.meta.url);
// Playwright is not a dependency of this repository. It is loaded from PLAYWRIGHT_PATH when that is set, and
// otherwise by ordinary module resolution from this directory; when neither finds it, the run stops and says
// how to point it at an install (see README.md).
const playwrightTarget = process.env.PLAYWRIGHT_PATH || "playwright";
let chromium;
try {
  ({ chromium } = requireCjs(playwrightTarget));
} catch (err) {
  console.error(
    `[dev-report-proof] FAIL: cannot load Playwright from "${playwrightTarget}" (${err.code || err.message}).\n` +
      "[dev-report-proof] Set PLAYWRIGHT_PATH to the playwright package directory of an install, for example\n" +
      "[dev-report-proof]   PLAYWRIGHT_PATH=<npm global root>/@playwright/cli/node_modules/playwright node run-proof.mjs\n" +
      "[dev-report-proof] (npm root -g prints the global root), or install playwright where Node resolves it from here."
  );
  process.exit(1);
}

const here = dirname(fileURLToPath(import.meta.url));
const scriptPath = join(here, "../../src/devreports/dev-report-notes.js");
const port = Number(process.env.PORT || 8799);
const contentTypes = { ".html": "text/html; charset=utf-8", ".js": "text/javascript; charset=utf-8" };

const log = (m) => console.log(`[dev-report-proof] ${m}`);

function serve() {
  const server = createServer(async (req, res) => {
    const path = (req.url || "/").split("?")[0];
    try {
      if (path === "/dev-report-notes.js") {
        res.writeHead(200, { "content-type": contentTypes[".js"] });
        res.end(await readFile(scriptPath));
        return;
      }
      // An image that takes five seconds, so a page holding it has not fired load for five seconds.
      if (path === "/slow.png") {
        await new Promise((r) => setTimeout(r, 5000));
        res.writeHead(404, { "content-type": "text/plain" });
        res.end("slow");
        return;
      }
      // The report opened as a plain page, with the script but no host around it.
      if (path === "/plain.html") {
        const report = await readFile(join(here, "sample-report.html"), "utf8");
        const script = await readFile(scriptPath, "utf8");
        res.writeHead(200, { "content-type": contentTypes[".html"] });
        res.end(report.replace("</body>", () => `<script>${script}</script></body>`));
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

async function waitFor(desc, fn, timeoutMs = 15000) {
  const start = Date.now();
  for (;;) {
    let ok = false;
    try { ok = await fn(); } catch { ok = false; }
    if (ok) return;
    if (Date.now() - start > timeoutMs) throw new Error(`timed out waiting for: ${desc}`);
    await new Promise((r) => setTimeout(r, 150));
  }
}

const fail = [];
const passed = [];
function check(claim, ok, detail) {
  log(`${ok ? "PASS" : "FAIL"} - ${claim}: ${detail}`);
  (ok ? passed : fail).push(claim);
}

const received = (page) => page.evaluate(() => window.__received);
const ofType = async (page, type) => (await received(page)).filter((m) => m.type === type);
const reportFrame = (page) => page.frames().find((f) => f.parentFrame() === page.mainFrame());
const text = (frame, drn) => frame.locator(`[data-drn=${drn}]`).innerText();

// The APP's panel - the one conversation on the screen. Hosted, the page draws none of this.
const panel = (page, which) => page.locator(`#panel-${which}`).innerText();
const hostState = (page) => page.evaluate(() => window.__state());
const appSends = (page) => page.evaluate(() => window.__appSends);
const appSend = async (page) => {
  const before = (await appSends(page)).length;
  await page.locator("#panel-send").click();
  await waitFor("the app's Send to carry the queue", async () => (await appSends(page)).length === before + 1);
  return (await appSends(page))[before];
};

// How many of a thing the PAGE draws, counted inside its shadow roots - where a page query cannot see.
const drawnInPage = (page, selector) =>
  reportFrame(page).evaluate((s) => {
    let n = 0;
    for (const host of document.querySelectorAll("[data-dev-report-ui]")) {
      if (host.shadowRoot) n += host.shadowRoot.querySelectorAll(s).length;
    }
    return n;
  }, selector);

// Every button on the whole screen whose words start with Send: the app's panel and the page's frame.
async function sendButtons(page) {
  const inApp = await page.evaluate(() => Array.from(document.querySelectorAll("button"))
    .filter((b) => /^Send/.test((b.textContent || "").trim())).length);
  const inPage = await reportFrame(page).evaluate(() => {
    let n = 0;
    for (const host of document.querySelectorAll("[data-dev-report-ui]")) {
      if (host.shadowRoot) n += Array.from(host.shadowRoot.querySelectorAll("button"))
        .filter((b) => /^Send/.test((b.textContent || "").trim())).length;
    }
    return n;
  });
  return { inApp, inPage, total: inApp + inPage };
}

async function reloadAndWait(page) {
  const readies = (await ofType(page, "ready")).length;
  await page.evaluate(() => window.__reload());
  await waitFor("the reloaded page to announce ready", async () => (await ofType(page, "ready")).length === readies + 1);
  await waitFor("the host to be connected again", () => page.evaluate(() => window.__isConnected()));
  await page.waitForTimeout(300);
}

async function main() {
  const server = await serve();
  const base = `http://127.0.0.1:${port}`;
  log(`serving ${base}/`);

  const browser = await chromium.launch();
  const page = await browser.newPage({ viewport: { width: 1600, height: 900 } });
  const evidence = { scenario: "dev report notes in a sandboxed frame: note a table cell, answer a question, send", steps: {} };

  try {
    await page.goto(`${base}/`);
    await waitFor("the page to announce ready", async () => (await ofType(page, "ready")).length === 1);
    await waitFor("the host to be connected", () => page.evaluate(() => window.__isConnected()));
    const frame = page.frameLocator("#report");

    // ---- claim A: the sandbox is real.
    const sandbox = await page.evaluate(() => ({
      attribute: document.getElementById("report").getAttribute("sandbox"),
      hostCanReadFrameDocument: document.getElementById("report").contentDocument !== null,
    }));
    const inside = await reportFrame(page).evaluate(() => {
      const out = { origin: window.origin };
      try { out.parentTitle = window.parent.document.title; out.canReadParent = true; } catch { out.canReadParent = false; }
      try { window.localStorage.getItem("x"); out.storage = true; } catch { out.storage = false; }
      return out;
    });
    evidence.steps.sandbox = { ...sandbox, ...inside };
    check(
      "A: the report is in a sandboxed frame that cannot reach its host",
      sandbox.attribute === "allow-scripts" && !sandbox.hostCanReadFrameDocument &&
        inside.origin === "null" && !inside.canReadParent && !inside.storage,
      JSON.stringify(evidence.steps.sandbox),
    );

    // ---- claim B: ready then restore.
    const ready = (await ofType(page, "ready"))[0];
    evidence.steps.ready = ready;
    check(
      "B: the page announces ready with the host's token and its question ids",
      JSON.stringify(ready.payload.questionIds) === '["rerun"]' && typeof ready.token === "string" && ready.token.length === 32,
      JSON.stringify({ ...ready, token: ready.token ? `(${ready.token.length} characters)` : ready.token }),
    );

    // ---- claim C: recommendation preselected.
    const preselected = await frame.locator("input[name=rerun][value=tonight]").isChecked();
    check("C: the recommended option is preselected", preselected === true, `tonight checked=${preselected}`);

    // ---- claim O: hosted, the page draws only the note-taking parts. The owner saw two conversations and
    // two Send buttons, one of them dead; this is the claim that says there is now one of each.
    const pageDraws = {
      queued: await drawnInPage(page, "[data-drn=queued]"),
      sent: await drawnInPage(page, "[data-drn=sent]"),
      replies: await drawnInPage(page, "[data-drn=replies]"),
      send: await drawnInPage(page, "[data-drn=send]"),
      payloadBox: await drawnInPage(page, "[data-drn=payload-box]"),
      conversation: await drawnInPage(page, "[data-drn=conversation]"),
      headings: await drawnInPage(page, ".drn-h"),
      addNote: await drawnInPage(page, "[data-drn=pick]"),
      noteBox: await drawnInPage(page, "[data-drn=composer]"),
      queueAnswer: await drawnInPage(page, "[data-drn=queue-answer]"),
    };
    const buttons = await sendButtons(page);
    evidence.steps.hostedPageDraws = { pageDraws, sendButtons: buttons };
    check(
      "O: hosted, the page draws no conversation and no Send - one conversation and one Send on the screen",
      pageDraws.queued === 0 && pageDraws.sent === 0 && pageDraws.replies === 0 && pageDraws.send === 0 &&
        pageDraws.payloadBox === 0 && pageDraws.conversation === 0 && pageDraws.headings === 0 &&
        pageDraws.addNote === 1 && pageDraws.noteBox === 1 && pageDraws.queueAnswer === 1 &&
        buttons.inPage === 0 && buttons.inApp === 1,
      JSON.stringify(evidence.steps.hostedPageDraws),
    );

    // ---- claim D: note a table cell with real mouse clicks.
    await frame.locator("[data-drn=pick]").click();
    await frame.locator("#results tbody tr:nth-child(2) td:nth-child(3)").click({ position: { x: 8, y: 8 } });

    // ---- claim P (first half): the note box is beside the cell, not over it.
    const overCell = await reportFrame(page).evaluate(() => {
      const inUi = (sel) => {
        for (const h of document.querySelectorAll("[data-dev-report-ui]")) {
          const found = h.shadowRoot && h.shadowRoot.querySelector(sel);
          if (found) return found;
        }
        return null;
      };
      const r = (el) => { const b = el.getBoundingClientRect(); return { top: b.top, left: b.left, bottom: b.bottom, right: b.right, width: b.width, height: b.height }; };
      const tray = inUi(".drn-tray");
      return {
        box: r(inUi("[data-drn=composer]")),
        cell: r(document.querySelector("#results tbody tr:nth-child(2) td:nth-child(3)")),
        trayScrolls: tray.scrollHeight > tray.clientHeight,
        pageScrollsSideways: document.documentElement.scrollWidth > document.documentElement.clientWidth,
      };
    });
    await frame.locator("[data-drn=composer-text]").fill("Forty-two failures cannot be right - the suite has twelve tests.");
    await page.screenshot({ path: join(here, "evidence-note-box-beside-cell.png") });
    await frame.locator("[data-drn=composer-queue]").click();

    // ---- claim E: answer the question (not the recommendation), with a comment.
    await frame.locator("input[name=rerun][value=tomorrow]").check();
    await frame.locator("textarea[data-dev-report-comment]").fill("The database is back tomorrow morning.");
    await frame.locator("[data-drn=queue-answer]").click();

    // ---- claim F: queued, not sent - in the app's panel, the only conversation there is.
    const queuedText = await panel(page, "queued");
    const sentTextBefore = await panel(page, "sent");
    await page.screenshot({ path: join(here, "evidence-queued.png") });
    check(
      "F: queued items show as queued and not as sent, in the app's panel",
      queuedText.includes("Forty-two failures") && queuedText.includes("Wait for tomorrow") && sentTextBefore.includes("Nothing sent yet"),
      `queued=${JSON.stringify(queuedText.slice(0, 160))} sent=${JSON.stringify(sentTextBefore)}`,
    );

    // ---- claim G: the app's Send - the whole queue, and nothing leaves the queue yet.
    const send = await appSend(page);
    evidence.steps.sendPayloadTheAppCarried = send;
    const [note, answer] = send.items;
    check(
      "D: the note names the table cell by selector, text, row and column",
      note && note.kind === "note" && note.anchor.type === "table-cell" && note.anchor.quote === "42" &&
        note.anchor.rowLabel === "Gateway" && note.anchor.columnLabel === "Failures" &&
        (await reportFrame(page).evaluate((s) => document.querySelector(s)?.textContent, note.anchor.selector)) === "42",
      JSON.stringify(note),
    );
    check(
      "E: the answer carries the question, the chosen option and the comment",
      answer && answer.kind === "answer" && answer.questionId === "rerun" &&
        answer.question === "Should the failed suite be rerun tonight?" && answer.optionValue === "tomorrow" &&
        answer.optionLabel === "Wait for tomorrow - nothing is blocked" && answer.comment === "The database is back tomorrow morning.",
      JSON.stringify(answer),
    );
    const stateWhileWaiting = await hostState(page);
    check(
      "G: one Send carries both items, and they stay queued until the host confirms",
      send.items.length === 2 && stateWhileWaiting.queued.length === 2 && stateWhileWaiting.sent.length === 0 &&
        (await ofType(page, "send")).length === 0,
      `${send.items.length} items; queued=${stateWhileWaiting.queued.length} sent=${stateWhileWaiting.sent.length}`,
    );

    // ---- claim H: the host confirms one and refuses the other; then a reply.
    const sentIds = send.items.map((i) => i.id);
    await page.evaluate((ids) => {
      window.__hostSend("status", { updates: [
        { id: ids[0], status: "held", statusLabel: "Held - delivered when the agent finishes its turn" },
        { id: ids[1], status: "refused", statusLabel: "Not taken - try again" },
      ] });
      window.__hostSend("reply", { reply: { id: "r1", text: "You are right - the fixture double-counted. Fixed in section 1.", at: "2026-09-16T10:00:00Z" } });
    }, sentIds);
    await waitFor("the reply to show", async () => (await panel(page, "replies")).includes("double-counted"));
    const sentAfter = await panel(page, "sent");
    const queuedAfter = await panel(page, "queued");
    await page.screenshot({ path: join(here, "evidence-sent-status-reply.png") });
    const hOk1 = sentAfter.includes("Held - delivered when the agent finishes its turn") && !sentAfter.includes("Wait for tomorrow") &&
      queuedAfter.includes("Wait for tomorrow") && queuedAfter.includes("Not taken - try again") && !queuedAfter.includes("Forty-two");

    // ---- claim P (second half): a note on a question's own heading never lands on the question.
    await frame.locator("[data-drn=pick]").click();
    await frame.locator("#questions h3").click();
    const overQuestion = await reportFrame(page).evaluate(() => {
      const inUi = (sel) => {
        for (const h of document.querySelectorAll("[data-dev-report-ui]")) {
          const found = h.shadowRoot && h.shadowRoot.querySelector(sel);
          if (found) return found;
        }
        return null;
      };
      const r = (el) => { const b = el.getBoundingClientRect(); return { top: b.top, left: b.left, bottom: b.bottom, right: b.right, width: b.width, height: b.height }; };
      return {
        box: r(inUi("[data-drn=composer]")),
        heading: r(document.querySelector("#questions h3")),
        question: r(document.querySelector("[data-dev-report-question]")),
      };
    });
    await page.screenshot({ path: join(here, "evidence-note-box-beside-question.png") });
    await frame.locator("[data-drn=composer-cancel]").click();
    const overlaps = (a, b) => a.left < b.right && b.left < a.right && a.top < b.bottom && b.top < a.bottom;
    evidence.steps.noteBoxPlacement = { overCell, overQuestion };
    check(
      "P: the note box is drawn beside what the note is about, over neither it nor its question, and the hosted tray does not scroll",
      overCell.box.width > 0 && overCell.box.height > 0 && !overlaps(overCell.box, overCell.cell) &&
        overQuestion.box.width > 0 && !overlaps(overQuestion.box, overQuestion.heading) &&
        !overlaps(overQuestion.box, overQuestion.question) &&
        overCell.trayScrolls === false && overCell.pageScrollsSideways === false,
      JSON.stringify(evidence.steps.noteBoxPlacement),
    );

    // ---- claim J: reload straight away - nothing else happens between the pushes and the reload.
    await reloadAndWait(page);
    const afterPushReload = {
      sent: await panel(page, "sent"),
      queued: await panel(page, "queued"),
      replies: await panel(page, "replies"),
    };
    evidence.steps.reloadRightAfterPushes = afterPushReload;
    check(
      "J: a reload straight after the host's status and reply keeps both",
      afterPushReload.sent.includes("Held - delivered when the agent finishes its turn") &&
        afterPushReload.queued.includes("Not taken - try again") && afterPushReload.replies.includes("double-counted"),
      JSON.stringify(afterPushReload).slice(0, 400),
    );

    // Send again: only the refused item goes, and the host takes it this time.
    const resend = await appSend(page);
    await page.evaluate((id) => window.__hostSend("status", { updates: [{ id: id, status: "delivered", statusLabel: "Delivered to the session" }] }), sentIds[1]);
    await waitFor("the answer to reach sent", async () => (await panel(page, "sent")).includes("Delivered to the session"));
    const queuedEnd = await panel(page, "queued");
    evidence.steps.resend = resend;
    check(
      "H: statuses and the reply show verbatim; a refused item stays queued with the reason and is the only thing sent again",
      hOk1 && resend.items.length === 1 && resend.items[0].id === sentIds[1] && queuedEnd.includes("Nothing queued"),
      `resend=${JSON.stringify(resend.items.map((i) => i.id))} queuedAfterConfirm=${JSON.stringify(queuedEnd)}`,
    );

    // ---- claim I: malformed and foreign messages change nothing - sent down the real port, and on the window.
    await page.evaluate((id) => {
      const f = { postMessage: (d) => window.__hostSendRaw(d) };
      document.getElementById("report").contentWindow.postMessage({ channel: "devthrottle.dev-report", version: 1, type: "reply", payload: { reply: { id: "w1", text: "ON THE WINDOW", at: "" } } }, "*");
      f.postMessage({ channel: "devthrottle.dev-report", version: 1, type: "status", payload: { updates: [{ id: id, status: "delivered" }] } });
      f.postMessage({ channel: "someone-else", version: 1, type: "status", payload: { updates: [{ id: id, status: "x", statusLabel: "FOREIGN" }] } });
      f.postMessage({ channel: "devthrottle.dev-report", version: 2, type: "reply", payload: { reply: { id: "r9", text: "WRONG VERSION", at: "" } } });
      f.postMessage({ channel: "devthrottle.dev-report", version: 1, type: "wipe", payload: {} });
    }, sentIds[0]);
    await page.waitForTimeout(500);
    const afterJunk = (await panel(page, "sent")) + (await panel(page, "replies"));
    check(
      "I: malformed, foreign and unknown messages are ignored",
      afterJunk.includes("Held - delivered when the agent finishes its turn") && !afterJunk.includes("FOREIGN") &&
        !afterJunk.includes("WRONG VERSION") && !afterJunk.includes("ON THE WINDOW"),
      JSON.stringify(afterJunk.slice(0, 200)),
    );

    // ---- claim K: reload keeps the half-typed note, an answer edited after queueing, and the scroll position.
    await frame.locator("[data-drn=pick]").click();
    await frame.locator("#explain").click();
    await frame.locator("[data-drn=composer-text]").pressSequentially("half typed");
    await frame.locator("input[name=rerun][value=tonight]").check();
    await frame.locator("[data-drn=queue-answer]").click();
    await frame.locator("input[name=rerun][value=tomorrow]").check();
    await frame.locator("textarea[data-dev-report-comment]").fill("half an answer");
    await reportFrame(page).evaluate(() => window.scrollTo(0, 300));
    await page.waitForTimeout(700);
    await reloadAndWait(page);
    await waitFor("the draft to come back", async () => (await frame.locator("[data-drn=composer-text]").inputValue()) === "half typed");
    const restored = await reportFrame(page).evaluate(() => {
      const inUi = (sel) => {
        for (const h of document.querySelectorAll("[data-dev-report-ui]")) {
          const found = h.shadowRoot && h.shadowRoot.querySelector(sel);
          if (found) return found;
        }
        return null;
      };
      return {
      scrollY: window.scrollY,
      draft: inUi("[data-drn=composer-text]").value,
      where: inUi("[data-drn=composer-where]").textContent,
      tomorrowChecked: document.querySelector("input[name=rerun][value=tomorrow]").checked,
      comment: document.querySelector("textarea[data-dev-report-comment]").value,
      };
    });
    evidence.steps.afterReload = restored;
    check(
      "K: after a reload the half-typed note, the answer edited after queueing (not the queued one), and the scroll position come back",
      restored.scrollY === 300 && restored.draft === "half typed" && restored.where.includes("Gateway failures") &&
        restored.tomorrowChecked && restored.comment === "half an answer",
      JSON.stringify(restored).slice(0, 300),
    );

    // ---- claim L: no host.
    const plain = await browser.newPage({ viewport: { width: 1600, height: 900 } });
    await plain.goto(`${base}/plain.html`);
    await plain.click("[data-drn=queue-answer]");
    await plain.click("[data-drn=toggle]");
    await plain.click("[data-drn=send]");
    // Playwright locators reach into the tray's shadow root; page queries would not.
    const plainResult = {
      payloadVisible: await plain.locator("[data-drn=payload]").isVisible(),
      payload: await plain.locator("[data-drn=payload]").textContent(),
      queued: await plain.locator("[data-drn=queued]").textContent(),
    };
    await plain.screenshot({ path: join(here, "evidence-no-host-payload.png") });
    const shown = JSON.parse(plainResult.payload || "null");
    evidence.steps.noHost = { payloadShown: shown, queued: plainResult.queued };
    check(
      "L: with no host, Send shows the exact send message and keeps the queue",
      plainResult.payloadVisible && shown?.channel === "devthrottle.dev-report" && shown?.type === "send" &&
        shown?.payload?.items?.[0]?.optionValue === "tonight" && plainResult.queued.includes("Rerun tonight") && !("token" in shown),
      JSON.stringify(shown),
    );

    // ---- claim M: a hostile report's scripts, handler and CSS get nowhere; only a token-bearing ready counts.
    const hostile = await browser.newPage({ viewport: { width: 1600, height: 900 } });
    await hostile.goto(`${base}/?report=hostile-report.html`);
    await waitFor("the hostile report's page to connect", () => hostile.evaluate(() => window.__isConnected()));
    await hostile.waitForTimeout(800);
    const hostileFrame = reportFrame(hostile);
    const ran = await hostileFrame.evaluate(() => ({
      script: document.documentElement.getAttribute("data-hostile-script-ran"),
      handler: document.documentElement.getAttribute("data-hostile-handler-ran"),
      snoopedToken: document.documentElement.getAttribute("data-hostile-token"),
      readMessage: document.documentElement.getAttribute("data-hostile-read-message"),
      tokenAttributeLeft: document.querySelector("[data-dev-report-token]") !== null,
    }));
    const hf = hostile.frameLocator("#report");
    const visible = {
      trayToggle: await hf.locator("[data-drn=toggle]").isVisible(),
      queueAnswer: await hf.locator("[data-drn=queue-answer]").isVisible(),
    };
    // Something that does get to run in the frame - here the proof driver, standing in for a script the
    // policy failed to stop - posts on the window: a send, and a ready with no token but with a port.
    await hostileFrame.evaluate(() => {
      parent.postMessage({ channel: "devthrottle.dev-report", version: 1, type: "send", payload: { items: [] } }, "*");
      const ch = new MessageChannel();
      parent.postMessage({ channel: "devthrottle.dev-report", version: 1, type: "ready", payload: { questionIds: [] } }, "*", [ch.port2]);
    });
    await hostile.waitForTimeout(500);
    const hostileHost = await hostile.evaluate(() => ({
      accepted: window.__received.map((m) => m.type),
      refused: window.__refused.map((r) => `${r.type}:${r.why}`),
    }));
    await hostile.screenshot({ path: join(here, "evidence-hostile-report.png") });
    evidence.steps.hostileReport = { ran, visible, host: hostileHost };
    check(
      "M: a report's script, handler and token snooping do not run behind a decoy head, its CSS cannot hide the tray, and only a token-bearing ready is taken",
      ran.script === null && ran.handler === null && ran.snoopedToken === null && ran.readMessage === null && !ran.tokenAttributeLeft &&
        visible.trayToggle && visible.queueAnswer &&
        JSON.stringify(hostileHost.accepted) === '["ready"]' &&
        JSON.stringify(hostileHost.refused) === JSON.stringify(["send:only ready comes on the window", "ready:no token"]),
      JSON.stringify(evidence.steps.hostileReport),
    );

    // ---- claim N: a link takes the frame elsewhere. Push BEFORE that page's load event, while the host still
    // believes it is connected - the window the second review found - and check the page got nothing.
    await hf.locator("#away").click();
    await waitFor("the other page's script to run", () => reportFrame(hostile).evaluate(() => document.body.getAttribute("data-evil-ran") === "yes"));
    const beforeLoad = await hostile.evaluate(() => ({ unexpectedLoads: window.__unexpectedLoads, connected: window.__isConnected() }));
    const pushedBeforeLoad = await hostile.evaluate(() =>
      window.__hostSend("reply", { reply: { id: "r1", text: "PRIVATE REPLY", at: "" } }));
    await hostile.waitForTimeout(800);
    const gotBeforeLoad = await reportFrame(hostile).evaluate(() => ({
      window: document.body.getAttribute("data-evil-got-message"),
      port: document.body.getAttribute("data-evil-got-port-message"),
    }));
    await waitFor("the other page's load event", async () => (await hostile.evaluate(() => window.__unexpectedLoads)) === 1, 20000);
    const pushedAfterLoad = await hostile.evaluate(() =>
      window.__hostSend("reply", { reply: { id: "r2", text: "PRIVATE REPLY 2", at: "" } }));
    await hostile.waitForTimeout(500);
    const afterNavigation = await hostile.evaluate(() => ({
      connected: window.__isConnected(),
      accepted: window.__received.map((m) => m.type),
      refused: window.__refused.map((r) => `${r.type}:${r.why}`),
    }));
    const gotAfterLoad = await reportFrame(hostile).evaluate(() => ({
      window: document.body.getAttribute("data-evil-got-message"),
      port: document.body.getAttribute("data-evil-got-port-message"),
    }));
    evidence.steps.navigatedAway = { beforeLoad, pushedBeforeLoad, gotBeforeLoad, pushedAfterLoad, afterNavigation, gotAfterLoad };
    check(
      "N: the page a link navigates to receives nothing pushed before or after its load event, and its forged ready and send are refused",
      beforeLoad.unexpectedLoads === 0 && beforeLoad.connected === true &&
        gotBeforeLoad.window === null && gotBeforeLoad.port === null &&
        !afterNavigation.connected && pushedAfterLoad === false && gotAfterLoad.window === null && gotAfterLoad.port === null &&
        JSON.stringify(afterNavigation.accepted) === '["ready"]' &&
        afterNavigation.refused.includes("ready:no token") && afterNavigation.refused.includes("send:only ready comes on the window"),
      JSON.stringify(evidence.steps.navigatedAway),
    );
  } catch (err) {
    check("the run completed", false, err.stack || String(err));
  } finally {
    await browser.close();
    server.close();
  }

  evidence.passed = passed;
  evidence.failed = fail;
  const out = join(here, `evidence-${new Date().toISOString().slice(0, 10)}.json`);
  await writeFile(out, JSON.stringify(evidence, null, 2) + "\n");
  log(`evidence written to ${out}`);
  if (fail.length) {
    log(`FAIL - ${fail.length} claim(s) failed: ${fail.join("; ")}`);
    process.exit(1);
  }
  log(`PASS - all ${passed.length} claims`);
}

main();
