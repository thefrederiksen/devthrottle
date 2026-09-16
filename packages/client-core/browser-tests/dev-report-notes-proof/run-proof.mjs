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
//   claim F - queued items show as queued, apart from sent, until Send.
//   claim G - Send posts ONE send message with the whole queue - and nothing leaves the queue until the
//             host confirms it.
//   claim H - the host's status words and the agent's reply are shown verbatim; a refused item stays
//             queued with the host's reason, and sending again carries only it.
//   claim I - malformed or foreign messages are ignored whole.
//   claim J - a reload straight after the host pushes a status and a reply loses neither.
//   claim K - a reload brings back the half-typed note, a choice and comment edited AFTER an answer was
//             queued (the newer edit wins), and the scroll position.
//   claim L - with no host, Send shows the exact payload and sends nothing.
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
    const queuedText = await text(frame, "queued");
    const sentTextBefore = await text(frame, "sent");
    await page.screenshot({ path: join(here, "evidence-queued.png") });
    check(
      "F: queued items show as queued and not as sent",
      queuedText.includes("Forty-two failures") && queuedText.includes("Wait for tomorrow") && sentTextBefore.includes("Nothing here"),
      `queued=${JSON.stringify(queuedText.slice(0, 160))} sent=${JSON.stringify(sentTextBefore)}`,
    );

    // ---- claim G: Send - one message, and nothing leaves the queue yet.
    await frame.locator("[data-drn=send]").click();
    await waitFor("the host to get send", async () => (await ofType(page, "send")).length === 1);
    const send = (await ofType(page, "send"))[0];
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
    const queuedWhilePending = await text(frame, "queued");
    const sentWhilePending = await text(frame, "sent");
    check(
      "G: one send carries both items, and they stay queued, waiting, until the host confirms",
      send.payload.items.length === 2 && queuedWhilePending.includes("Waiting for the app to confirm") &&
        queuedWhilePending.includes("Forty-two failures") && sentWhilePending.includes("Nothing here"),
      `${send.payload.items.length} items; queued=${JSON.stringify(queuedWhilePending.slice(0, 200))}`,
    );

    // ---- claim H: the host confirms one and refuses the other; then a reply.
    await page.evaluate(() => {
      window.__hostSend("status", { updates: [
        { id: "n1", status: "held", statusLabel: "Held - delivered when the agent finishes its turn" },
        { id: "a2", status: "refused", statusLabel: "Not taken - try again" },
      ] });
      window.__hostSend("reply", { reply: { id: "r1", text: "You are right - the fixture double-counted. Fixed in section 1.", at: "2026-09-16T12:00:00Z" } });
    });
    await waitFor("the reply to show", async () => (await text(frame, "replies")).includes("double-counted"));
    const sentAfter = await text(frame, "sent");
    const queuedAfter = await text(frame, "queued");
    await page.screenshot({ path: join(here, "evidence-sent-status-reply.png") });
    const hOk1 = sentAfter.includes("Held - delivered when the agent finishes its turn") && !sentAfter.includes("Wait for tomorrow") &&
      queuedAfter.includes("Wait for tomorrow") && queuedAfter.includes("Not taken - try again") && !queuedAfter.includes("Forty-two");

    // ---- claim J: reload straight away - nothing else happens between the pushes and the reload.
    await reloadAndWait(page);
    const afterPushReload = {
      sent: await text(frame, "sent"),
      queued: await text(frame, "queued"),
      replies: await text(frame, "replies"),
    };
    evidence.steps.reloadRightAfterPushes = afterPushReload;
    check(
      "J: a reload straight after the host's status and reply keeps both",
      afterPushReload.sent.includes("Held - delivered when the agent finishes its turn") &&
        afterPushReload.queued.includes("Not taken - try again") && afterPushReload.replies.includes("double-counted"),
      JSON.stringify(afterPushReload).slice(0, 400),
    );

    // Send again: only the refused item goes, and the host takes it this time.
    await frame.locator("[data-drn=toggle]").click();
    await frame.locator("[data-drn=send]").click();
    await waitFor("the second send", async () => (await ofType(page, "send")).length === 2);
    const resend = (await ofType(page, "send"))[1];
    await page.evaluate(() => window.__hostSend("status", { updates: [{ id: "a2", status: "delivered", statusLabel: "Delivered to the session" }] }));
    await waitFor("the answer to reach sent", async () => (await text(frame, "sent")).includes("Delivered to the session"));
    const queuedEnd = await text(frame, "queued");
    evidence.steps.resend = resend;
    check(
      "H: statuses and the reply show verbatim; a refused item stays queued with the reason and is the only thing sent again",
      hOk1 && resend.payload.items.length === 1 && resend.payload.items[0].id === "a2" && queuedEnd.includes("Nothing here"),
      `resend=${JSON.stringify(resend.payload.items.map((i) => i.id))} queuedAfterConfirm=${JSON.stringify(queuedEnd)}`,
    );

    // ---- claim I: malformed and foreign messages change nothing - sent down the real port, and on the window.
    await page.evaluate(() => {
      const f = { postMessage: (d) => window.__hostSendRaw(d) };
      document.getElementById("report").contentWindow.postMessage({ channel: "devthrottle.dev-report", version: 1, type: "reply", payload: { reply: { id: "w1", text: "ON THE WINDOW", at: "" } } }, "*");
      f.postMessage({ channel: "devthrottle.dev-report", version: 1, type: "status", payload: { updates: [{ id: "n1", status: "delivered" }] } });
      f.postMessage({ channel: "someone-else", version: 1, type: "status", payload: { updates: [{ id: "n1", status: "x", statusLabel: "FOREIGN" }] } });
      f.postMessage({ channel: "devthrottle.dev-report", version: 2, type: "reply", payload: { reply: { id: "r9", text: "WRONG VERSION", at: "" } } });
      f.postMessage({ channel: "devthrottle.dev-report", version: 1, type: "wipe", payload: {} });
    });
    await page.waitForTimeout(500);
    const afterJunk = (await text(frame, "sent")) + (await text(frame, "replies"));
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
