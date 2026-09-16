# Phase 1 independent inspection of pull request 2948

Scope: `origin/main` at `0649a2825` through `mission/dev-reports` at `c228f4379`. I read the 20 changed paths in the diff and inspected the implementation, contract, test host, tests, and proof driver. I treated the earlier reviews and phase report as claims. I did not run `run-proof.mjs` or edit implementation or tests.

**Verdict: changes needed before merge.** Six findings: two high, three medium, one low.

## Findings

### 1. High - a comment in a template defeats both the marker and script checks

**File and line:** `src/CcDirector.Gateway/DevReports/DevReportShapeCheck.cs:555-575` (`FindTemplateEnd`), affecting the checks at lines 69-81 and 329-345.

`FindTemplateEnd` counts every textual `<template>` or `</template>` without recognizing comments. In `<template><template><!-- </template> --></template>`, the closing tag inside the comment reduces the scanner's depth, so it resumes at the inner template's real end tag. A complete set of report markers placed there is still inert content of the outer template in the browser, yet the checker counts it. Conversely, after an otherwise valid report, `<template><!-- <template> --></template><script>...</script>` makes the scanner skip to end of file, missing a live script. The regular nested-template test at `DevReportShapeCheckTests.cs:294-299` does not contain a comment.

**Verified:** I invoked the compiled `DevReportShapeCheck.Check` on a valid control and both payloads; all three returned `Passed = true`. jsdom found four live markers in the control and zero in the inert-marker payload. In the second payload it found one live script and executed it. A host that applies the contract's nonce policy still blocks that script, but the Gateway's claimed script refusal and rendered-section check do not hold.

### 2. High - valid radio markup can send a different answer from the one the owner clicked

**Files and lines:** `src/CcDirector.Gateway/DevReports/DevReportShapeCheck.cs:284-322`; `packages/client-core/src/devreports/dev-report-notes.js:1006-1013,681-684,1038-1060`; `packages/client-core/src/devreports/CONTRACT.md:70-80`.

The shape check counts radio inputs and a recommendation but never checks their `name` groups. With two options in one question named `first` and `second`, and the second marked recommended, the browser permits both to be checked. The script selects the **last** checked input when Queue is pressed. If the owner clicks the first option, the second stays checked and the queued answer is still the recommendation. The contract allows this markup as written; it does not require a shared radio name.

**Verified:** The compiled shape check returned `Passed = true` for this report. Running the shipping script in jsdom gave checked states `false,true` at start, `true,true` after clicking the first option, and a queued `optionValue` of `recommended`. The current tests use one shared radio name throughout and stay green.

### 3. Medium - the checker accepts a report with no visible summary or questions

**Files and lines:** `src/CcDirector.Gateway/DevReports/DevReportShapeCheck.cs:69-80,193-245`; `src/CcDirector.Gateway.UnitTests/DevReports/DevReportShapeCheckTests.cs:24-46,126-153`.

The checker looks for markers and question words but ignores visibility. Adding `hidden` to the summary and questions sections of a valid report still returns `Passed = true`; the browser displays neither section. An empty summary also passes. This lets the verdict certify the required shape while the owner sees no summary or questions. The tests reject an absent marker or empty questions body, but do not check a hidden section or empty summary.

**Verified:** The compiled checker passed the control and the variant with both sections hidden. jsdom reported `hidden = true` and computed `display = none` for both sections.

### 4. Medium - an overlong question is silently truncated and queued

**Files and lines:** `packages/client-core/src/devreports/dev-report-notes.js:645-651,1044-1059`; `packages/client-core/src/devreports/CONTRACT.md:83-84`.

The contract says the page will explain the limit and decline to queue when a question's own markup exceeds 20,000 characters. `questionText()` truncates the explicit question text to 20,000 before the Queue handler checks its length, making that check unable to catch this case. A heading fallback is truncated to 240 by `textOf()`. The queued answer can therefore carry different question text from what the owner saw.

**Verified:** With a 20,001-character `data-dev-report-question-text`, the shipping script queued one answer whose question was 20,000 characters and displayed its normal queued status, with no length warning. Existing length tests cover comments, not question text.

### 5. Medium - the browser proof has a personal absolute dependency path

**File and line:** `packages/client-core/browser-tests/dev-report-notes-proof/run-proof.mjs:41-44`.

The Playwright fallback is an absolute path inside one person's Windows profile. This discloses a private workstation path in the public pull request and makes the documented `node run-proof.mjs` command depend on that profile unless `PLAYWRIGHT_PATH` is set. The README at lines 8-13 describes a global installation, but the fallback is personal.

**Verified:** I inspected the exact fallback and the README. This is the only private-path hit in the 16 changed text files at the reviewed head; four other changed files are PNGs.

### 6. Low - phase documents include tool attribution

**Files and lines:** `docs/missions/dev-reports/PHASE-1-REPORT.md:3`; `docs/missions/dev-reports/HANDOFF-phase-1.md:45`.

Both new documents identify the inspection tool's family. The repository's owner-only attribution rule applies to documents as well as code and commits. The review record can describe independence without naming the tool.

**Verified:** A scan of the 16 changed text files at `c228f4379` found these two attribution lines. It found no non-ASCII characters. I did not include uncommitted phase 2 files in this scan.

## Earlier review findings and test reach

The core fixes for earlier findings 2, 3, 5, 6, 7, 8, 9, 11, A, and B are present in the code. Their targeted assertions exercise receipt before removing queue items, state emission after pushes, option ownership, shadow roots, length feedback for comments, restored answer drafts, decoded no-questions text, the reserved question ID, port-only navigation behavior, and replacement of a newer answer revision. These assertions would fail for the specific earlier implementations described in the review. Finding 10's per-question rescan is gone by source inspection; no timing benchmark establishes the broader proportional-time claim. Finding 4 is **not fully fixed**, as finding 1 above shows. Finding 1's host policy and private port are implemented in the test host, but the Gateway's publish check still admits a live script in the payload above.

I ran the focused suites against this head: 39 script tests passed; 52 Gateway shape tests passed, with zero skipped. I inspected the browser proof's assertions but did not rerun it because it overwrites committed evidence. I did not revert fixes, so this inspection does not independently certify the phase report's claim that every guard was watched failing. The new payloads above identify concrete cases the passing suites miss.

## Trust boundary and limits

Under the test host's nonce policy and private-port protocol, I found no route for report markup alone to forge an owner note or read restored state. The template payload shows why the shape check cannot be relied on to enforce script absence; the host policy remains necessary. No production app hosts a report in this phase, so this inspection does not establish how those future hosts will enforce the contract.

A report can hide the whole page with CSS or cover the tray with an overlay. `CONTRACT.md:248-253` already discloses this limit. The current shadow-root and inline rules protect the tray against the tested selectors aimed at it, not against page-wide obstruction.
