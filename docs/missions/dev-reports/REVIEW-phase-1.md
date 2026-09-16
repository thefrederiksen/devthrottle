# Phase 1 independent review

Scope: `git diff da3f45c3e..80c5cb5cf` on `mission/dev-reports`. I treated the contract, handoff, and phase report as claims to test, not as evidence. The findings below are ranked by impact. **Five are severe** (one critical and four high). The phase is not ready to integrate into a real host until the trust boundary and delivery behavior are settled.

## Findings

### 1. Critical - report-owned script can impersonate the note script and read restored private state

**Files/lines:** `packages/client-core/src/devreports/CONTRACT.md:99-104`, `packages/client-core/browser-tests/dev-report-notes-proof/index.html:22,37-45`, `packages/client-core/src/devreports/dev-report-notes.js:618-625,934-959`; the shape checker explicitly allows `<script>` at `src/CcDirector.Gateway/DevReports/DevReportShapeCheck.cs:341-345`.

The report is agent-written HTML in an `allow-scripts` iframe. Any `<script>` in that HTML executes with the same frame identity as `dev-report-notes.js`. It can call `parent.postMessage` with a forged `ready`, `send`, or `state-changed` envelope; the host's required `event.source === frame.contentWindow` check cannot distinguish it. It can also listen for the host's `restore`, read queued/sent notes and replies, and send them to an outside endpoint. The note script making no network calls does not protect state that another script in the same page can read. The current shape check accepts such a report, and the contract does not require a policy that prevents report-owned script execution.

**Verified:** In real Chromium, a report-owned script inside `sandbox="allow-scripts"` sent a forged `send` that passed the host's frame-source check, then received a `restore` containing a private reply. This did not exercise a real Gateway, which is not built in this phase. The proof's claim A only establishes that the frame cannot read its parent's DOM; it does not establish that the frame's messages or its own state are trustworthy. The host integration needs an enforceable separation (for example, a restrictive script policy plus HTML sanitization) before it treats frame messages as owner actions.

### 2. High - Send consumes the queue before the host accepts anything

**Files/lines:** `packages/client-core/src/devreports/dev-report-notes.js:466-475,846-859`; `packages/client-core/src/devreports/CONTRACT.md:122-132`.

After any valid `restore`, clicking Send posts a message and immediately moves every queued item into SENT with the script-invented words "Sent to the app". `postMessage` only enqueues a browser event; it does not confirm that the host or Gateway accepted the items. If the host is closing, rejects a request, loses its Gateway connection, or never answers, the items disappear from QUEUED and the UI offers no retry. This also contradicts the ruling that delivery status and its words come from the Gateway/host. The browser proof's test host always receives the event, so it cannot detect this loss case.

**Verified:** Source trace; no failing-host browser run. The protocol has no acceptance/acknowledgement message to wait for. Keep items recoverable until an authoritative receipt or refusal is received.

### 3. High - pushed status and replies are lost on the next reload unless another action happens

**Files/lines:** `packages/client-core/src/devreports/dev-report-notes.js:948-954`; `packages/client-core/browser-tests/dev-report-notes-proof/index.html:37-45`; `packages/client-core/browser-tests/dev-report-notes-proof/run-proof.mjs:180-231`.

The contract says the host saves the latest `state-changed` state for `restore`. The page changes its sent items on `status` and its replies on `reply`, but these branches only call `render()`, never `emitState()`. In the supplied test host, send an updated status and a reply, then reload immediately: its saved state still has the old status and no reply, so the visible updates vanish. The browser proof stays green because it scrolls after pushing those messages; the scroll handler emits a fresh state and incidentally saves both updates before reload. Removing that scroll step exposes the gap.

**Verified:** Source trace against the exact test-host save rule and the browser proof's event sequence. I did not obtain a separate successful reload reproduction. The real host could separately reconstruct its own pushes from Gateway data, but that is not the host behavior specified or proved here.

### 4. High - the shape check can pass a page with no rendered report sections

**Files/lines:** `src/CcDirector.Gateway/DevReports/DevReportShapeCheck.cs:263-346`, especially `341-345`; `packages/client-core/src/devreports/CONTRACT.md:22-38`.

`ReadStartTags` scans inside HTML raw-text elements it does not skip. For example, place a complete set of valid header, summary, questions/no-questions, and detail markers after `<plaintext>` and omit its end tag. The checker sees the apparent start tags in order and would return `Passed`, while the browser renders all those characters as plaintext and `document.querySelectorAll('[data-dev-report]')` returns zero. The same scanner can resume prematurely at `</scripture>` inside a script because it searches for the substring `</script` without checking the next character; a browser has not closed the script there.

**Verified:** The `<plaintext>` payload produced zero report-marker elements in an HTML DOM parser. The checker verdict follows directly from its scanner; I did not execute `Check` on that payload. The existing tests cover only the five named skipped tags and ordinary closing tags.

### 5. High - the checker and page disagree about which radio options belong to a question

**Files/lines:** `src/CcDirector.Gateway/DevReports/DevReportShapeCheck.cs:227-240`; `packages/client-core/src/devreports/dev-report-notes.js:585-586,865-899`; `packages/client-core/src/devreports/CONTRACT.md:60-74`.

The checker assigns radio options by document order until the next question marker; the page uses `q.querySelectorAll`, which assigns only DOM descendants and also includes nested questions. A report can pass the checker with two radios placed after a question's closing `</div>` but before the next marker; its Queue button then has no options to choose. Conversely, two nested questions can each have two valid options and one recommendation, yet Queue on the outer question captures the inner question's checked option as the answer to the outer question. That is a silent, confidently wrong decision payload.

**Verified:** In jsdom, Queue on a nested outer question emitted `{ questionId: "outer", optionValue: "i1", optionLabel: "Inner 1" }` from the inner question. The checker behavior follows from its explicit position bounds; I did not execute `Check` on this payload. Enforce a DOM ownership rule in the checker and make the page use the same rule, or reject nesting and options outside their question element.

### 6. Medium - valid report markup can disable the note interface before it starts

**Files/lines:** `packages/client-core/src/devreports/dev-report-notes.js:604-607,292-303`; `src/CcDirector.Gateway/DevReports/DevReportShapeCheck.cs:59-70`.

An agent-written report may put `data-dev-report-notes-started="1"` on `<html>`. That attribute is not forbidden by the shape check, but `start()` interprets it as its own initialization flag and throws before adding the tray or announcing `ready`. Marking `<body data-dev-report-ui>` likewise causes `isUi()` to treat every report element as part of the tray, so notes cannot be anchored. These public DOM markers let ordinary report markup interfere with the injected UI even without a report-owned script.

**Verified:** In jsdom, pre-seeding the root attribute made `start()` throw `dev-report-notes: already started on this page`. The body-marker effect follows directly from `closest()`.

### 7. Medium - a long question comment throws on Queue and a long note draft cannot be restored

**Files/lines:** `packages/client-core/src/devreports/dev-report-notes.js:325-350,367-370,430-451,815-819,882-898`.

The question comment field has no length limit. Paste 20,001 characters and press Queue: `queueAnswer()` rejects the item and throws from the click listener, with no visible validation message and no queued answer. The note composer likewise emits every keystroke into `draft.text`, but `validState` refuses a draft longer than 20,000 characters; the host can save that emitted state and the next `restore` is silently ignored. A shape-valid question ID or option value over the limit has the same Queue failure.

**Verified:** In jsdom, a 20,001-character question comment caused `queueAnswer: the answer is not valid` and left the queue empty. The draft restore failure follows from the emitted value and `validState` predicate.

### 8. Medium - unqueued question input is omitted from the promised half-typed state

**Files/lines:** `packages/client-core/src/devreports/dev-report-notes.js:815-819,889-920`; `packages/client-core/src/devreports/CONTRACT.md:132,142-151`; `docs/missions/dev-reports/HANDOFF-phase-1.md:17-21`.

Only the note composer has an input handler that updates `state.draft`. Changing a question radio or typing in its optional comment does not emit `state-changed` until Queue is pressed, and `restore` reapplies queued answers only. If the agent republishes the report while the owner is halfway through answering, the comment disappears and the recommendation is selected again. The handoff promises restoration of "half-typed text" without limiting it to note drafts; the browser proof tests only a half-typed note. If question drafts are intentionally excluded, the contract and handoff should say so explicitly.

**Verified:** Source trace; no separate reload reproduction.

### 9. Medium - an empty no-questions marker is accepted as if it said so in words

**Files/lines:** `src/CcDirector.Gateway/DevReports/DevReportShapeCheck.cs:201-211`; `packages/client-core/src/devreports/CONTRACT.md:42-46`; `docs/design/dev-reports/cc-dev-reports.html:240-243`.

`<section data-dev-report="questions"><p data-dev-report-no-questions></p></section>` satisfies the checker because it only counts the marker. It shows no words to the owner, despite the contract and design requiring the section to say there are no questions. The existing positive test uses nonempty text and never checks the empty case.

**Verified:** Source trace; no direct `Check` invocation on the payload.

### 10. Medium - question checking is quadratic although the code and report claim a linear scan

**Files/lines:** `src/CcDirector.Gateway/DevReports/DevReportShapeCheck.cs:26-31,214-242`, especially `227-229`; `docs/missions/dev-reports/PHASE-1-REPORT.md` under "The shape check".

For each of N questions, `tags.Where(...).ToList()` walks the full tag list again to find that question's options. A report with thousands of otherwise valid questions inside the planned 10 MB publish limit can consume work proportional to N times the number of tags on a Gateway request. The start-tag tokenizer is one pass, but the overall shape check is not. No test or benchmark guards the stated linear-time claim.

**Verified:** Loop/collection inspection; no timing benchmark. Build an index of radio positions once or scan options alongside questions.

### 11. Low - a legal question ID suppresses its queued/sent indicator

**Files/lines:** `src/CcDirector.Gateway/DevReports/DevReportShapeCheck.cs:46,214-225`; `packages/client-core/src/devreports/dev-report-notes.js:862-880,901-911`.

`__proto__` matches the allowed ID pattern. The page stores question state elements in a plain `{}` and assigns `questionStates[id] = stateText`. For `__proto__`, that changes the object's prototype instead of adding an own property, so `renderQuestionStates()` never updates the indicator. The answer still queues, but the question itself gives no "Queued" or "Sent" feedback.

**Verified:** In jsdom, Queue produced one answer while the question-state text stayed empty. Use a `Map` or a null-prototype dictionary, or reject reserved IDs.

## Evidence and limits

- `npx vitest run src/devreports` passed: 27 tests.
- `dotnet test src/CcDirector.Gateway.UnitTests --filter FullyQualifiedName~DevReportShapeCheckTests --no-restore` passed: 32 tests.
- The browser security reproduction used Chromium with an `allow-scripts` sandbox and a host that checked `event.source`. It demonstrated message forgery and exposure of restored state, not actual Gateway delivery or outside-network exfiltration.
- I did not edit implementation or tests and did not rerun the full local gate. Static findings above name their verification limit individually. The existing green suites cover the supplied benign sample and do not settle the adversarial cases.
