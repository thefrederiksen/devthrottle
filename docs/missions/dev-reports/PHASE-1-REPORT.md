# Dev Reports - phase 1 report: the note-taking script and the shape check

Issue: #2940 (child of #2936). Branch: `mission/dev-reports`. Built, proven, and reviewed twice by an
independent reviewer from a different agent family (`REVIEW-phase-1.md`), then inspected
(`INSPECTION-phase-1.md`). Every finding of all three is fixed, and each fix has a test that was watched
failing without it.

## Ruling 8: report scripts are blocked

The first review found that a `<script>` written into a report shares the frame with the note-taking script,
so it could forge the owner's notes and read restored notes and replies. The Architect upheld the fix as
ruling 8 in `STATE.md`: a dev report runs none of its own scripts or inline event handlers, and the shape
check refuses them. Cost accepted: no script-drawn charts; images are `data:` URLs.

How a host enforces it is `packages/client-core/src/devreports/CONTRACT.md` section 4, sharpened by the
second review:
- the host writes the frame's head itself, first - policy and injected script - and only then the report's
  bytes, untouched (searching the report text for `<head>` is defeated by a `<head>` inside a comment);
- on the window the host takes exactly one message: a `ready` from its frame carrying the current token and
  a port; everything else, both ways, goes over that private port (a page the frame navigates to never has
  the port, so it gets nothing - even before its load event, which a load-event check alone missed);
- a frame load the host did not cause closes the port.

## What was built

**One contract** - `CONTRACT.md`: the report markers, the question markup, what a note's anchor carries,
the host messages and where they travel, when an item counts as sent, how answer revisions and drafts
behave, and section 4, what a host MUST do.

**The note-taking script** - `packages/client-core/src/devreports/dev-report-notes.js`. One plain
JavaScript file, no dependencies, no build step, ASCII only, no network, no storage. Notes on a paragraph,
table cell, SVG part or text selection; a Queue button per question with the recommendation preselected;
QUEUED apart from SENT; one Send. After Send an item stays queued and waiting until the host confirms it; a
refused item stays queued with the host's reason. Re-answering replaces the newest unsent revision, so a
question has at most a pending answer plus one revision. Host pushes, the half-typed note, an unqueued
answer edit (which wins over the queued answer, being newer) and the scroll position all survive a reload.
The tray and Queue buttons live in shadow roots with inline `!important` rules on their hosts, so report
CSS cannot hide them by name, and no DOM attribute a report can copy switches anything off. Credits
lavish-axi by Kun Chen.

**The shape check** - `src/CcDirector.Gateway/DevReports/DevReportShapeCheck.cs`. Pure static class, no
endpoint. Every error is a sentence an agent can act on; the status is returned. One header with an
allowed status, first; one summary right after, with words, not hidden; one questions section right
after, not hidden, with questions or exactly one no-questions element with words; at least one detail
section; evidence optional and last; no section inside another; each question not nested, valid unique id,
two or more options inside it sharing one radio name no other radio uses, exactly one recommended; no loose
options; no scripts, no inline event handlers in the live document. Since the inspection (ruling 9) it
parses the report with AngleSharp, a real HTML5 parser, after the host's head and with scripting on, and
judges the document a browser builds. It is guidance for agents, not the security boundary: it does not
evaluate stylesheets, and the host policy of ruling 8 is what stops a report acting for the owner.

## What is proven, and how

- **Script unit tests** - `devReportNotes.test.ts`, 43 tests, running the same file the apps inject:
  anchoring and selector round-trip, table labels that refuse to guess, the queue with pending, refused,
  revisions and ids, message validation, the page (preselection, notes, no-host Send, nested questions,
  length limits including an overlong question text, `__proto__`, answer drafts, tray out of reach of
  report CSS, markup that copies flags, and the inspection's two-name radio payload).
- **Shape check tests** - `DevReportShapeCheckTests.cs`, 69 tests: a good report and the browser proof's
  sample report pass, and every rule has a report breaking only that rule, including every bypass either
  review named (plaintext, `</scripture`, nested template, the text-only elements, detached options,
  nesting, unclosed elements, `&nbsp;`-only and implicitly closed no-questions text, scripts, handlers),
  and every inspection payload (both commented-template payloads, hidden and empty sections, radio names).
- **Watched failing.** Pre-review guards (rowspan shift, deep validation, evidence last); all 12 first-review
  checker tests against the pre-review checker; option ownership, the refused branch and the comment length
  check each turn only their own test red. In the browser: without saving state after a push, claim J fails;
  without the policy, claim M fails; with the host searching the report for `<head>`, claim M fails and the
  hostile script reads the token; pushing on the window instead of the port, claim N fails with the
  navigated page holding the private reply; without the shadow root, the tray test fails. All restored and
  re-run green.
- **Real browser** - `browser-tests/dev-report-notes-proof/run-proof.mjs`, 14 claims, all PASS, evidence
  JSON and screenshots committed. Beyond the owner flow (sandbox, token, preselection, table-cell note and
  answer in one send, confirm and refuse, junk ignored on the port and the window, reloads keeping pushes,
  drafts, edited answers and scroll, no-host payload): a hostile report with a decoy `<head>` comment, a
  token-snooping observer, a forged send, an `onerror` handler and CSS aimed at the tray gets nowhere and
  the tray stays visible; a link to another page whose load is held back five seconds receives nothing the
  host pushes while the host still believes it is connected, nor after, and its forged ready and send are
  refused.
- **Gates** - `.\scripts\test-local.ps1` green (8 suites, 1,907 tests); client-core typecheck clean, 1,213
  tests pass, eslint clean. The browser proof was re-run after the inspection fixes: 14 of 14 PASS, evidence
  refreshed.

## What is NOT proven

- **No real app hosts the page yet.** The Cockpit, phone and Director must each implement contract
  section 4 in phases 3 and 4; the script cannot protect itself from a host that does not. Statuses and
  replies in the proof are made up; phase 2's Gateway must treat a repeated item id as the same item and a
  later answer to a question as replacing an earlier one.
- **Chromium only.** WebView2, the phone's browser and Safari were not run.
- **CSS can still obstruct.** A report can lay something over the tray or hide the whole page; it cannot
  hide the tray by name or act for the owner. Accepted as visibly broken, not silently hostile.
- **Phone touch behaviour** - text selection and tapping on a real phone were not driven.
- **The shape check does not evaluate stylesheets.** A section hidden by a CSS rule, rather than the
  `hidden` attribute or an inline `display: none`, still passes; a test records that on purpose. No timing
  benchmark was run on a 10 megabyte report. It does not check summary length.
- **Parked Gateway suites.** `CcDirector.Gateway.UnitTests` was run in full after the inspection fixes:
  4,884 passed, 2 skipped, 8 failed, all "Failed to connect to 127.0.0.1:55432" in
  `HostedSchemaRefusesAnUnownedRowTests`, a PostgreSQL only `-Parked` starts. `-Parked` was not run; nothing
  references the new class yet.

## Review

`REVIEW-phase-1.md`. First pass: 11 findings (1 critical, 4 high, 5 medium, 1 low), all fixed. Second pass:
3 held, 8 open (6 carried, 2 new; 4 severe). All 8 fixed: 1 host writes the head first and uses a port;
4 nested templates; 6 shadow-root tray; 8 newer draft wins; 9 decoded text, paragraph closers, one marker;
10 single marker scan; A port transport; B revisions replace the newest unsent answer.

## Inspection

`INSPECTION-phase-1.md`, by an independent reviewer from a different agent family: 2 high, 3 medium, 1 low.
All six fixed on this branch:

1. and 3. **The hand-written scanner is gone** (ruling 9). A comment inside a template no longer hides a live
   script or counts inert markers, and a hidden or empty summary or a hidden questions section is refused.
   Proof: the pre-fix scanner put back under the new tests turns 16 of them red, including every inspection
   payload; restored, 69 of 69 pass.
2. **One radio group per question.** The check refuses options with different names, no name, or a name
   another radio uses; the script keeps at most one option of a question checked, so the answer queued is
   the one the owner clicked. Proof: the old scanner has no name rule (the same 16 red), and without the
   script's one-checked rule the two-name payload queues with both options checked, 2 tests red.
4. **Question text is sent whole or not at all.** No cut before the length check, for the explicit text or
   the heading. Proof: with the old cut back, the overlong and long-heading tests go red.
5. **No private path in the proof.** Playwright comes from `PLAYWRIGHT_PATH` or ordinary resolution; missing,
   the run stops with FAIL and the instruction (watched: exit 1 with the message).
6. **Attribution removed** from this report.
