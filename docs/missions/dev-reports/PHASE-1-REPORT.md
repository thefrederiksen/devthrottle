# Dev Reports - phase 1 report: the note-taking script and the shape check

Issue: #2940 (child of #2936). Branch: `mission/dev-reports`. Built, proven, reviewed twice by a Codex
session; every finding of the first review is fixed. The second pass is recorded in
`REVIEW-phase-1.md` beside this file.

## One thing the Architect must look at: report scripts are now blocked

The first review found a real hole: the report runs in the same frame as the note-taking script, so a
`<script>` written into the report could post a `send` that looks exactly like the owner's, and read the
notes and replies the host restores. The sandbox cannot tell them apart. I reproduced it: with no policy,
the hostile test report's script and `onerror` handler ran and read the restored state.

**What I did (inferred - reverse it if it is wrong):** the contract now requires every host to put a
Content-Security-Policy on the report that lets only the host's own injected script run (a fresh nonce per
load), to stamp that script with a fresh token that every message must carry, and to treat any frame load
it did not cause (a link or a meta refresh to another page) as the end of that token. The shape check now
refuses `<script>` and inline event handlers, so an agent learns this at publish time.

**The cost:** a dev report cannot run its own JavaScript (no script-drawn charts) and images must be
`data:` URLs. The design already says "hand-drawn SVG with a label on every part", so I judged that
acceptable, but it is a product rule the owner has not stated. The alternative - letting report scripts
run - leaves the owner's notes forgeable by the report and has no fix I know of inside one frame.

## What was built

**One contract** - `packages/client-core/src/devreports/CONTRACT.md`: the report markers, the question
markup, what a note's anchor carries, the versioned host messages, when an item counts as sent, and
section 4, what a host MUST do so a message really came from the owner.

**The note-taking script** - `packages/client-core/src/devreports/dev-report-notes.js`. One plain
JavaScript file, no dependencies, no build step, ASCII only, no network, no storage. Notes on a paragraph,
table cell, SVG part or text selection; a Queue button per question with the recommendation preselected;
QUEUED apart from SENT; one Send. After Send an item stays queued, marked as waiting, until the host
answers with a status for it; a refused item stays queued with the host's reason and can be sent again.
Every status and reply the host pushes is handed back in the state, so a reload keeps it. The half-typed
note, an unqueued choice and comment, and the scroll position come back after a reload. Every message to
the host carries the token. Report markup cannot switch the script off or hide elements from notes (the
flags it relies on are no longer DOM attributes). Credits lavish-axi by Kun Chen.

**The shape check** - `src/CcDirector.Gateway/DevReports/DevReportShapeCheck.cs`. A pure static class, no
endpoint. Returns every error, each a sentence an agent can act on, plus the status. Rules: one header with
an allowed status, first; one summary right after; one questions section right after that, closed, never
empty, with questions or a no-questions element that has words; at least one detail section; evidence
optional and last; unknown section values refused; each question closed, not nested, with a valid unique
id, at least two options inside it and exactly one recommended; no radio option loose in the section; no
scripts and no inline event handlers. It pairs start and end tags, skips what a browser treats as text
(script, style, textarea, title, xmp, iframe, noembed, noframes, noscript, template) and stops at
plaintext. Options are merged into questions in one pass rather than a rescan per question.

## What is proven, and how

- **Script unit tests** - `devReportNotes.test.ts`, 36 tests, running the SAME file the apps inject.
  Adds, since the review: items stay queued until confirmed; refused stays queued with the reason; a
  pending item cannot be removed; a re-answer to a pending question gets a new id; nested questions answer
  with their own options; a too-long comment is reported, not thrown; `__proto__` as a question id works;
  an unqueued choice and comment are in the state; report markup copying the old flags changes nothing.
- **Shape check tests** - `DevReportShapeCheckTests.cs`, 46 tests: a good report passes, every rule has a
  report that breaks only that rule, the browser proof's sample report passes. Adds: plaintext,
  `</scripture` inside a script, the five other text-only elements, options after a question closes,
  nested questions, unclosed question and section, empty no-questions element, scripts, event handlers.
- **Watched failing.** Before the review: the rowspan-shift guard, deep state validation, and the
  evidence-last rule were each reverted and their test went red. After the review: all 12 new shape check
  tests fail against the pre-review checker and pass against the new one; reverting option ownership, the
  refused branch, and the comment length check each turn exactly their own test red; in the browser,
  reverting "save state after a push" failed claim J (status and reply gone after a reload), and removing
  the host's policy failed claim M (the hostile report's script and handler ran and read the restored
  state). Everything was restored and re-run green.
- **Real browser** - `browser-tests/dev-report-notes-proof/run-proof.mjs`, 14 claims, all PASS, with
  `evidence-2026-09-16.json` and screenshots: the sandbox; ready carries the token; preselection; a note on
  the "Gateway" / "Failures" cell and an answer with a comment arrive as one `send`; nothing leaves the
  queue until the host confirms; a refused answer stays queued with the reason and is the only thing sent
  again; junk messages change nothing; a reload straight after a status and reply keeps both; a reload
  keeps the half-typed note, the unqueued choice and comment and the scroll; a plain page with no host shows
  the payload; a hostile report's script and handler do not run and tokenless or wrong-token messages are
  refused; a link to another page ends the token, that page's forged ready and send are refused, and the
  host pushes nothing to it.
- **Gates** - `.\scripts\test-local.ps1` green (8 suites, 1,907 tests). client-core typecheck clean, all
  1,206 client-core tests pass, eslint clean.

## What is NOT proven

- **No real app hosts the page yet.** The host is a test page; the Cockpit, phone and Director hosts are
  phases 3 and 4, and each must implement contract section 4. The statuses and reply in the proof are made
  up; the Gateway's come in phase 2, which must also accept a repeated item id as the same item.
- **The policy was proven in Chromium only.** WebView2 (Chromium) and the phone's browser were not run.
  Safari on an iPhone was not tried.
- **A host that forgets section 4 is unsafe.** The script cannot protect itself from a report script that
  runs; only the host's policy stops that.
- **Phone touch behaviour** - text selection and tapping on a real phone were not driven.
- **The shape check is not a full HTML parser.** It pairs tags by name, so an element a browser closes
  implicitly (a `p` left open) has no end, and the check reports it as unclosed rather than guessing. It
  does not check the summary's length or that nothing untagged sits above it. No timing benchmark was run;
  the per-question rescan the review found is gone, but the "proportional to size" claim is by reading.
- **Parked Gateway suites.** The full `CcDirector.Gateway.UnitTests` project was run directly before the
  review: 4,847 passed, 8 failed, all in `HostedSchemaRefusesAnUnownedRowTests` with "Failed to connect to
  127.0.0.1:55432" - a PostgreSQL only the `-Parked` run starts. `-Parked` was not run; nothing in the parked
  suites references the new class, which nothing calls yet.

## Review

`REVIEW-phase-1.md`: first pass 11 findings (1 critical, 4 high, 5 medium, 1 low). All 11 fixed:
1 trust (contract section 4, policy, token, navigation), 2 send kept until confirmed, 3 pushes saved to
state, 4 text-only elements and end-tag boundary, 5 option ownership and no nesting (checker and page
agree), 6 no DOM flags, 7 length limits reported, 8 answer drafts in state, 9 empty no-questions refused,
10 one-pass option merge, 11 `Map` for question states. Second pass: see the review file.
