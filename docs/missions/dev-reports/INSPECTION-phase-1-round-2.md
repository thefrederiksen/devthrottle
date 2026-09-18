# Phase 1 independent re-inspection of pull request 2948

Scope: `origin/main` at `0649a2825` through `mission/dev-reports` at `0a959659d`. I read the prior inspection's six findings, the changed implementation and tests, the contract, and the phase report as an unverified claim. I did not run `run-proof.mjs`. Temporary local probes and mutations were restored; this review is the only file added.

**Verdict: changes needed before merge.** Four new medium findings and one low finding. No high findings in this pass.

## New findings

### 1. Medium - a template element can stand in for a visible header or detail section

**File and line:** `src/CcDirector.Gateway/DevReports/DevReportShapeCheck.cs:77,100-151,226-239`.

The parser correctly excludes markers *inside* template content, but `QuerySelectorAll("[data-dev-report]")` still returns the `<template>` element itself. The count and order checks accept `<template data-dev-report="header" data-dev-report-status="done">Header</template>` as the sole header, or `<template data-dev-report="detail">Detail</template>` as the sole detail. Neither renders. The summary path happens to reject a template summary because `HasWords` sees no content on the template element. The contract's first header and required detail can therefore disappear while the verdict passes.

**Verified:** A temporary test passed a valid control and each of the template-header and template-detail variants with zero errors. The same test rejected a template-summary variant. jsdom gave the template element `display: none` with its text in `template.content`. The temporary test was removed. This does not show that markers nested inside templates are counted; the existing tests correctly reject those.

### 2. Medium - the inline visibility check disagrees with CSS parsing

**File and line:** `src/CcDirector.Gateway/DevReports/DevReportShapeCheck.cs:411-445`.

`HasInlineDisplayNone` splits the raw `style` attribute and compares the unparsed value to `none`. With `style="display:/**/none"` on the summary, the browser's CSS parser treats the comment as ignorable and hides the section, but the shape check passes it. The reverse also occurs: `style="display:none;display:block"` is displayed by the browser's final declaration, but the shape check rejects it because it returns on the first `display:none`. This breaks the specific contract promise to reject an inline `display:none`; the separately documented stylesheet limitation is broader.

**Verified:** A temporary shape test returned `Passed = true` for the comment variant and `Passed = false` for the later-show variant. jsdom computed `display: none` and `display: block`, respectively. The [CSS syntax specification](https://www.w3.org/TR/css-syntax-3/) says comments do not affect parsing. The temporary test was removed.

### 3. Medium - non-rendered SVG description can satisfy the summary's word rule

**File and line:** `src/CcDirector.Gateway/DevReports/DevReportShapeCheck.cs:59,451-467`.

`HasWords` skips SVG `title` but walks SVG `desc` as ordinary visible text. Replacing the sole summary text with `<svg><desc>invisible words</desc></svg>` returns `Passed = true`. The owner sees no summary words. The [SVG 2 specification](https://www.w3.org/TR/SVG/struct.html#DescriptionAndTitleElements) defines `desc` as non-rendered descriptive content. The existing empty-summary tests cover `style`, `hidden`, and whitespace, but not this foreign-content case.

**Verified:** A temporary shape test passed the valid control and the `desc`-only summary variant with zero errors. The temporary test was removed. The SVG specification establishes the visual result; I did not capture a browser screenshot for this payload.

### 4. Medium - the new radio-name check makes valid large reports quadratic

**File and line:** `src/CcDirector.Gateway/DevReports/DevReportShapeCheck.cs:365-383`.

For each question, `CheckRadioGroup` scans `allRadios` with `Any` to see whether another radio shares its name. A valid report with unique names makes that scan reach the end every time. This is a new per-question full scan after the earlier per-question scan was removed. A publish check on an oversized report can occupy a Gateway request for a long time; phase 1 has no publish endpoint yet.

**Verified:** One temporary test generated valid reports with two radio options per question. The checker passed all three and took 158 ms for 1,000 questions (150 KB), 2,407 ms for 5,000 (766 KB), and 37,538 ms for 10,000 (1.54 MB) in the local Debug test process. The 10,000-question case is a benchmark, not a normal report-size claim. The temporary test was removed. No timeout, input-size limit, or large-report regression test appears in this phase.

### 5. Low - the phase report overstates revert coverage for the hygiene fixes

**File and line:** `docs/missions/dev-reports/PHASE-1-REPORT.md:5,123-125`.

The report says every inspection fix has a test watched failing without it. The personal-path removal and attribution removal have no committed regression tests. The path paragraph describes a manual missing-package run; that tests failure guidance, not whether restoring the personal fallback is caught. The attribution paragraph only says the text was removed. Both fixes are present, but a green suite would not catch either reversion.

**Verified:** I inspected the changed test files, the proof driver, the two edited documents, and the added-line hygiene scan. I did not run the proof driver. The report should state the narrower evidence, or add guards that fail when those lines return.

## The six earlier findings

| Prior finding | Current code and behavior | Revert detection |
|---|---|---|
| 1. Commented template end/start bypass | Fixed for the two reported payloads. AngleSharp excludes inert template content and sees the live script after a commented start tag. | Restoring the old checker made `Check_MarkersInsideATemplateWithACommentedEndTag_DoNotCount` and `Check_ScriptAfterATemplateWithACommentedStartTag_Fails` fail. |
| 2. Different radio names queue the wrong answer | Fixed for the reported payload. The shape check rejects split or shared names, and the script clears other checked options on change. | The old checker failed the new two-name, unnamed, and shared-name tests. Restoring the old script's radio initialization made two page tests fail, including the clicked-option case. |
| 3. Hidden or empty required sections | Fixed for `hidden`, ordinary inline `display:none`, and empty summaries. See new findings 1-3 for remaining gaps. | The old checker failed the hidden-summary, hidden-questions, and empty-summary tests. |
| 4. Overlong question is truncated and queued | Fixed for explicit and heading text: `questionText` returns the whole text and Queue checks its length. | Restoring the old `questionText` made the overlong-explicit and long-heading tests fail; 41 other page tests passed. |
| 5. Private absolute Playwright fallback | Fixed in `run-proof.mjs:41-54`: resolution uses `PLAYWRIGHT_PATH` or `playwright`. | No committed test fails on restoring the private fallback. I did not run the proof driver. |
| 6. Tool attribution in phase documents | Fixed in the two cited documents; they describe an independent reviewer without naming an assistant family. | No committed test fails on restoring attribution. |

With the entire old shape checker restored temporarily, the current focused suite failed 16 of 69 cases, including the cases for findings 1-3. The restored current checker passed 69 of 69 with zero skipped. The restored current script passed 43 of 43 page tests; each of the two script mutations above made two tests fail. Source files and the temporary probes were restored before this review was written.

## Scope and hygiene

The current checker agrees with the tested parser outcomes for commented templates, `noscript`, implied nesting, and live scripts inside SVG and `foreignObject`. Its stylesheet limitation is explicit: a class rule or `<style>` rule can hide a section while the shape check passes, and the existing test records that. The host's content policy remains the security boundary. No production app host or Gateway publish endpoint is covered by this phase, and I did not run a real-browser proof in this pass.

Before adding this review, I scanned all 20 changed text files (4 additional changed files are PNGs) and 4,391 added text lines for private absolute paths, machine names, non-ASCII characters, and assistant or tool attribution. The scan found zero hits. `git diff --check origin/main...HEAD` was clean. These scans describe the implementation diff, not a regression guard.
