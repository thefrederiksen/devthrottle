// The mid-turn screen of the API mode proof must show the reply streaming in, not only the status line
// (mission #4070, section 3: "replies streaming"). The run of 20 October 2026 (linux-20261020-003750) photographed the
// Cockpit the moment its status line said "Working - turn 3", a second or more before Pi's first word, so the
// Cockpit's picture had no reply while the phone's, taken after it, did.
//
// Each page below is the live conversation's real markup at one moment of that turn (ApiModeLiveChat in client-core).
// The first is the moment the Cockpit was photographed: the status line is up and the thread still ends at the
// prompt's receipt. Waiting for the status line alone accepts it - that is the defect - so the wait must refuse it.
//
//   PLAYWRIGHT_DIR=<folder with playwright-core> CHROME=<chrome> node --test scripts/prove-api-mode-pi-screens.test.mjs
import { test, before, after } from "node:test";
import assert from "node:assert/strict";
import { loadChromium, waitForScreen } from "./prove-api-mode-pi-screens.mjs";

if (!process.env.PLAYWRIGHT_DIR || !process.env.CHROME)
  throw new Error("PLAYWRIGHT_DIR and CHROME are required: this test drives a real browser, as the proof does");

const EXPECT = "Working - turn";
// Long enough that a wait which is going to accept the page has accepted it.
const REFUSE_MS = 1500;

function screen(threadEnd) {
  return `<!doctype html><html><body>
    <div class="live-chat-status" role="status" aria-label="Session status">
      <span class="live-chat-status-part">Working - turn 3</span><span class="live-chat-status-part">API mode</span>
    </div>
    <div class="live-chat-scroll">
      <div class="live-chat-thread" role="log" aria-label="Live conversation">
        <article class="live-chat-reply"><div class="live-chat-body md"><p>DONE</p></div></article>
        <div class="live-chat-turn">Turn finished</div>
        <article class="live-chat-prompt">Write the numbers from 1 to 600, one per line, with no other text.
          <span class="live-chat-receipt">Delivered</span></article>
        ${threadEnd}
      </div>
    </div>
  </body></html>`;
}

let browser, page;
before(async () => {
  browser = await loadChromium().launch({ executablePath: process.env.CHROME, headless: true });
  page = await browser.newPage();
});
after(async () => {
  await browser.close();
});

test("waitForScreen_StatusLineUpButNoReplyYet_RefusesTheScreen", async () => {
  await page.setContent(screen(""));

  await assert.rejects(waitForScreen(page, { expect: EXPECT, reply: true, timeout: REFUSE_MS }));
});

test("waitForScreen_TheReplyArticleIsThereButHasNoTextYet_RefusesTheScreen", async () => {
  await page.setContent(screen(`<article class="live-chat-reply"><div class="live-chat-body md"></div></article>`));

  await assert.rejects(waitForScreen(page, { expect: EXPECT, reply: true, timeout: REFUSE_MS }));
});

test("waitForScreen_TheReplyIsStreamingIn_AcceptsTheScreen", async () => {
  await page.setContent(screen(`<article class="live-chat-reply"><div class="live-chat-body md"><p>1<br>2<br>3</p></div></article>`));

  await waitForScreen(page, { expect: EXPECT, reply: true, timeout: REFUSE_MS });
});

test("waitForScreen_ReplyStreamingButTheStatusLineIsGone_RefusesTheScreen", async () => {
  await page.setContent(screen(`<article class="live-chat-reply"><div class="live-chat-body md"><p>1<br>2</p></div></article>`)
    .replace("Working - turn 3", "Idle - turn 3"));

  await assert.rejects(waitForScreen(page, { expect: EXPECT, reply: true, timeout: REFUSE_MS }));
});
