# Phase 1 independent re-inspection of pull request 2948, round 3

Scope: only `638d0265d..a11400191` on `mission/dev-reports`, against the five findings in `INSPECTION-phase-1-round-2.md` and the ruling in `HANDOFF-phase-1-fix-2.md`. I treated the phase report as an unverified claim. I did not run `run-proof.mjs`.

**Verdict: no defects found in this diff. 0 high, 0 medium, 0 low.**

| Round 2 finding | Inspection result and revert check |
|---|---|
| 1. Unrendered section markers | The checker excludes a marker on, or inside, an unrendered element before counting and ordering sections. It also excludes an unrendered no-questions marker. Removing the section filter made all seven new header and detail cases fail; removing the no-questions filter made its case fail. The restored code passed them. |
| 2. Inline CSS parsing | The Architect ruled that the shape check judges structure and the `hidden` attribute, not CSS. The inline `display:none` check and its contract promise are gone. The class comment and contract both state that inline styles and stylesheet rules can hide a section despite a passing verdict. Reintroducing an inline `display:none` check made the new style case fail; the restored code passed it. This is the documented limit, not an open visibility finding. |
| 3. SVG description text | `HasWords` skips SVG `desc`, `title` and `metadata` while accepting drawn SVG `text`. Restoring the previous text filter made the new `desc` and `metadata` cases fail; `title` was already excluded. The restored code passed all three rejection cases and the drawn-text control. |
| 4. Quadratic radio-name check | The checker builds a name-to-owner dictionary once, then looks up each question's name. The committed timing test builds an approximately 10 MB ASCII report with 49,202 questions, two named radios per question, unique names, and a passing verdict; it therefore exercises the radio-name path. With the old per-question radio scan restored and the same test input reduced to approximately 3 MB, the test failed its 30-second bound: 14,903 questions took 52.3 seconds. The restored code passed the original 10 MB test. I did not wait for the old quadratic scan to complete on the full 10 MB input. |
| 5. Phase report proof claim | The report now identifies the private-path and attribution fixes as inspection-only, with no regression tests, and names the narrower missing-package proof. Its description of the structure-only CSS rule matches the contract and checker. |

The focused Release suite passed after restoration: 82 passed, 0 failed, 0 skipped. All temporary source and test mutations were restored; the tracked worktree was clean before this file was added. The baseline Release run also built the mobile and Cockpit assets. The targeted mutation runs skipped those repeated asset builds.

For this diff's 223 added lines across five text files, a scan found no private absolute paths, machine names, non-ASCII characters, or tool attribution. `git diff --check 638d0265d..a11400191` passed. This inspection covers the changed shape checker, its tests and documentation; it does not establish host behavior or a publish endpoint, which are outside this phase.
