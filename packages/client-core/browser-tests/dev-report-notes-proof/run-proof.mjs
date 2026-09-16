// Dev report note-taking script proof driver (issue #2940, phase 1 of #2936).
//
// It serves this directory plus the ONE shipping script (src/devreports/dev-report-notes.js, read from
// source so the proof cannot drift from it), and drives a real Chromium:
//   claim A - the report runs in <iframe sandbox="allow-scripts"> with no allow-same-origin, and really is
//             cut off: the host cannot read the frame's document, and the frame cannot read its parent's
//             or use storage.
//   claim B - the page announces ready with its question ids, and the host's restore connects it.
//   claim C - the recommended option is preselected.
//   claim D - a note on a table cell carries the cell's selector, text, row label and column label.
//   claim E - an answer carries the question, the chosen option and the comment.
//   claim F - queued items show as QUEUED, apart from SENT, until Send.
//   claim G - Send posts ONE send message with the whole queue, and the host gets exactly those items.
//   claim H - the host's status words and the agent's reply are shown verbatim.
//   claim I - malformed or foreign messages are ignored whole.
//   claim J - a reload loses nothing: the host's restore brings back sent items, the half-typed note and
//             the scroll position.
//   claim K - opened as a plain page with no host, Send shows the exact payload and sends nothing.
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
const PLAYWRIGHT_PATH =
  process.env.PLAYWRIGHT_PATH ||
  "C:/Users/soren/AppData/Roaming/npm/node_modules/@playwright/cli/node_modules/playwright";
const { chromium } = requireCjs(PLAYWRIGHT_PATH);

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
const reportFrame = (page) => page.frames().find((f) => f.parentFrame() === page.mainFrame());

async function main() {
  const server = await serve();
  const base = `http://127.0.0.1:${port}`;
  log(`serving ${base}/`);

  const browser = await chromium.launch();
  const page = await browser.newPage({ viewport: { width: 1600, height: 900 } });
  const evidence = { scenario: "dev report notes in a sandboxed frame: note a table cell, answer a question, send", steps: {} };

  try {
    await page.goto(`${base}/`);
    await waitFor("the page to announce ready", async () => (await received(page)).some((m) => m.type === "ready"));
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
    const ready = (await received(page)).find((m) => m.type === "ready");
    evidence.steps.ready = ready;
    check("B: the page announces ready with its question ids", JSON.stringify(ready.payload.questionIds) === '["rerun"]', JSON.stringify(ready));

    // ---- claim C: recommendation preselected.
    const preselected = await frame.locator("input[name=rerun][value=tonight]").isChecked();
    check("C: the recommended option is preselected", preselected === true, `tonight checked=${preselected}`);

    // ---- claim D: note a table cell with real mouse clicks.
    await frame.locator("[data-drn=pick]").click();
    await frame.locator("#results tbody tr:nth-child(2) td:nth-child(3)").click({ position: { x: 8, y: 8 } });
    await frame.locator("[data-drn=composer-text]").fill("Forty-two failures cannot be right - the suite has twelve tests.");
    await frame.locator("[data-drn=composer-queue]").click();

    // ---- claim E: answer the question (not the recommendation), with a comment.
    await frame.locator("input[name=rerun][value=tomorrow]").check();
    await frame.locator("textarea[data-dev-report-comment]").fill("The database is back tomorrow morning.");
    await frame.locator("[data-drn=queue-answer]").click();

    // ---- claim F: queued, not sent.
    const queuedText = await frame.locator("[data-drn=queued]").innerText();
    const sentTextBefore = await frame.locator("[data-drn=sent]").innerText();
    await page.screenshot({ path: join(here, "evidence-queued.png") });
    check(
      "F: queued items show as queued and not as sent",
      queuedText.includes("Forty-two failures") && queuedText.includes("Wait for tomorrow") && sentTextBefore.includes("Nothing here"),
      `queued=${JSON.stringify(queuedText.slice(0, 160))} sent=${JSON.stringify(sentTextBefore)}`,
    );

    // ---- claim G: Send.
    const sendsBefore = (await received(page)).filter((m) => m.type === "send").length;
    await frame.locator("[data-drn=send]").click();
    await waitFor("the host to get send", async () => (await received(page)).filter((m) => m.type === "send").length === sendsBefore + 1);
    const send = (await received(page)).filter((m) => m.type === "send").at(-1);
    evidence.steps.sendPayloadTheHostGot = send;
    const [note, answer] = send.payload.items;
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
    check("G: one send message carries exactly the two queued items", send.payload.items.length === 2 && sendsBefore === 0, `${send.payload.items.length} items, ${sendsBefore} earlier sends`);

    // ---- claim H: status and reply shown verbatim.
    await page.evaluate(() => {
      window.__hostSend("status", { updates: [{ id: "n1", status: "held", statusLabel: "Held - delivered when the agent finishes its turn" }] });
      window.__hostSend("reply", { reply: { id: "r1", text: "You are right - the fixture double-counted. Fixed in section 1.", at: "2026-09-16T12:00:00Z" } });
    });
    await waitFor("the reply to show", async () => (await frame.locator("[data-drn=replies]").innerText()).includes("double-counted"));
    const sentAfter = await frame.locator("[data-drn=sent]").innerText();
    const queuedAfter = await frame.locator("[data-drn=queued]").innerText();
    await page.screenshot({ path: join(here, "evidence-sent-status-reply.png") });
    check(
      "H: the host's status words and the reply are shown verbatim, and the queue is empty",
      sentAfter.includes("Held - delivered when the agent finishes its turn") && sentAfter.includes("Sent to the app") && queuedAfter.includes("Nothing here"),
      JSON.stringify(sentAfter.slice(0, 300)),
    );

    // ---- claim I: malformed and foreign messages change nothing.
    await page.evaluate(() => {
      const f = document.getElementById("report").contentWindow;
      f.postMessage({ channel: "devthrottle.dev-report", version: 1, type: "status", payload: { updates: [{ id: "n1", status: "delivered" }] } }, "*");
      f.postMessage({ channel: "someone-else", version: 1, type: "status", payload: { updates: [{ id: "n1", status: "x", statusLabel: "FOREIGN" }] } }, "*");
      f.postMessage({ channel: "devthrottle.dev-report", version: 2, type: "reply", payload: { reply: { id: "r9", text: "WRONG VERSION", at: "" } } }, "*");
      f.postMessage({ channel: "devthrottle.dev-report", version: 1, type: "wipe", payload: {} }, "*");
    });
    await page.waitForTimeout(500);
    const afterJunk = (await frame.locator("[data-drn=sent]").innerText()) + (await frame.locator("[data-drn=replies]").innerText());
    check(
      "I: malformed, foreign and unknown messages are ignored",
      afterJunk.includes("Held - delivered when the agent finishes its turn") && !afterJunk.includes("FOREIGN") && !afterJunk.includes("WRONG VERSION"),
      JSON.stringify(afterJunk.slice(0, 200)),
    );

    // ---- claim J: reload keeps sent, the half-typed note and the scroll position.
    await frame.locator("[data-drn=pick]").click();
    await frame.locator("#explain").click();
    await frame.locator("[data-drn=composer-text]").pressSequentially("half typed");
    await reportFrame(page).evaluate(() => window.scrollTo(0, 300));
    await page.waitForTimeout(700);
    const readiesBefore = (await received(page)).filter((m) => m.type === "ready").length;
    await page.evaluate(() => window.__reload());
    await waitFor("the reloaded page to announce ready", async () => (await received(page)).filter((m) => m.type === "ready").length === readiesBefore + 1);
    await waitFor("the draft to come back", async () => (await frame.locator("[data-drn=composer-text]").inputValue()) === "half typed");
    const restored = await reportFrame(page).evaluate(() => ({
      scrollY: window.scrollY,
      draft: document.querySelector("[data-drn=composer-text]").value,
      where: document.querySelector("[data-drn=composer-where]").textContent,
      sent: document.querySelector("[data-drn=sent]").textContent,
      replies: document.querySelector("[data-drn=replies]").textContent,
    }));
    evidence.steps.afterReload = restored;
    check(
      "J: after a reload the host's restore brings back sent items, the reply, the half-typed note and the scroll position",
      restored.scrollY === 300 && restored.draft === "half typed" && restored.where.includes("Gateway failures") &&
        restored.sent.includes("Held - delivered when the agent finishes its turn") && restored.replies.includes("double-counted"),
      JSON.stringify(restored).slice(0, 300),
    );

    // ---- claim K: no host.
    const plain = await browser.newPage({ viewport: { width: 1600, height: 900 } });
    await plain.goto(`${base}/plain.html`);
    await plain.click("[data-drn=queue-answer]");
    await plain.click("[data-drn=toggle]");
    await plain.click("[data-drn=send]");
    const plainResult = await plain.evaluate(() => ({
      payloadVisible: !document.querySelector("[data-drn=payload-box]").hasAttribute("hidden"),
      payload: document.querySelector("[data-drn=payload]").textContent,
      queued: document.querySelector("[data-drn=queued]").textContent,
    }));
    await plain.screenshot({ path: join(here, "evidence-no-host-payload.png") });
    const shown = JSON.parse(plainResult.payload || "null");
    evidence.steps.noHost = { payloadShown: shown, queued: plainResult.queued };
    check(
      "K: with no host, Send shows the exact send message and keeps the queue",
      plainResult.payloadVisible && shown?.channel === "devthrottle.dev-report" && shown?.type === "send" &&
        shown?.payload?.items?.[0]?.optionValue === "tonight" && plainResult.queued.includes("Rerun tonight"),
      JSON.stringify(shown),
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
