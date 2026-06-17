# QA Proof Report - Issue #488 (re-verification after fix)

## Cockpit Schedule page: create, view, run, and manage cron jobs

**Verifier:** QA Agent (independent - own fresh build + own test run + own screenshot).
**PR:** #491 (branch `issue-488-cockpit-schedule`, HEAD 88bbdd3).
**Mode:** standalone QA -> NOT merged; merge left to the human.
**History:** cycle 1 FAILED (editing a disabled job silently re-enabled it). This is the re-verification of the fix.

## How I verified independently
1. Checked out the PR branch fresh and built it myself: `dotnet build` -> **0 Warning(s), 0 Error(s)**.
2. Ran the **full** Cockpit.Tests suite myself -> **58 Passed, 0 Failed, 0 Skipped** (incl. the 12 cron+schedule tests; no regression elsewhere).
3. Confirmed the fix in code and re-emitted + screenshotted the genuine compiled page myself (`qa/schedule-page-qa.png`).

## The cycle-1 defect is fixed
- `Schedule.razor` now carries `_fEnabled` / `_fPreventOverlap`: defaulted on in `OpenCreate`, **captured from the job in `OpenEdit`** (lines 308-309), and emitted by `BuildFromForm` (lines 327, 339). An edit preserves both.
- Regression test `SchedulePageTests.Editing_a_disabled_job_preserves_enabled_false_and_preventoverlap_in_the_put_body` drives the REAL component through the edit modal (rename a disabled job + Save) and asserts the captured PUT body has `enabled=false` and `preventOverlap=false`. It passes now and (by construction) fails on the pre-fix code. **Re-verified: PASS.**

## Acceptance criteria - Expected vs Actual

| # | Criterion | Evidence | Verdict |
|---|-----------|----------|---------|
| AC1 | Schedule nav -> `/schedule`, dark-theme chrome | nav entry + my screenshot (`qa/schedule-page-qa.png`) | PASS |
| AC2 | Empty state, then populated jobs table | `Renders_jobs_table...` + `Empty_state_when_no_jobs`; screenshot shows the table | PASS |
| AC3 | Valid create -> 201 + appears | `GatewayClientCronTests.CreateCronJobAsync_PostsAndReturnsCreatedJob`; page wires create+refresh | PASS |
| AC4 | Invalid cron -> inline 400, not added | `CreateCronJobAsync_OnBadRequest_ThrowsWithServerMessage`; bound to `_formError` | PASS |
| AC5 | One-off vs work-list distinguishable | screenshot: "once @ ..." vs "0 0 * * *"; "work list Tonight/Backlog" | PASS |
| AC6 | Run-now -> run appears in history | `RunCronJobNowAsync...` + `Selecting_a_job_shows_its_run_history` | PASS |
| AC7 | Edit (PUT) + Delete (DELETE) reflect; **edit preserves enabled/overlap** | `DeleteCronJobAsync_OnNotFound_Throws` + the new regression test (the cycle-1 defect) | PASS |
| AC8 | Enable/disable persists via PUT | `ToggleEnabledAsync` sends full DTO with flipped Enabled; **edit no longer clobbers it** (regression test) | PASS |
| AC9 | Feedback <~100ms, async load, no freeze | async `OnInitializedAsync` + 5s `PeriodicTimer`; buttons disable while saving | PASS |

## Method / regression lens
- Full Cockpit suite green (58) - no adjacent breakage.
- `review-code` lens: no forbidden patterns introduced by the fix; the change is a state-preservation correctness fix with a covering test.
- Security (DT): unchanged - pure REST consumer, inherits the Cockpit's Gateway auth, no secrets logged.

### Residual note (unchanged from cycle 1, not a blocker)
Proof is via the repo's render-proof pattern (real compiled page + real CSS, screenshotted) + bUnit interaction tests (incl. the edit-PUT-body assertion) + `GatewayClient` contract tests. A full live browser create->fire->history round-trip against a Gateway was not run (booting a `GatewayHost` risks the live fleet). Given the edit-interaction is now test-proven and the page renders, I am satisfied for this UI slice - consistent with how #239 was proven. Flagged so it is not overclaimed.

## Verdict
**VERIFIED - all 9 acceptance criteria met; the cycle-1 defect is fixed and covered by a regression test.** Build clean, 58/58 Cockpit tests pass on an independent build. Standalone QA does not merge - **PR #491 is ready for the human to merge.**
