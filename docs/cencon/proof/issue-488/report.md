# Proof Report - Issue #488

## Cockpit Schedule page: create, view, run, and manage cron jobs

Follow-up to epic #479 (cron jobs). A new Cockpit page at `/schedule`, a pure client of the shipped `/cron/jobs` REST surface (#482-#484).

**Branch/PR:** issue-488-cockpit-schedule (PR pending)
**Build:** `dotnet build cc-director.sln` -> **0 Warning(s), 0 Error(s)**.
**Tests:** Cockpit.Tests cron+schedule -> **11 Passed** (6 GatewayClient cron + 5 Schedule page bUnit), full solution unaffected.

## How this slice is proven

The Cockpit has no live UI surface to drive without a backing Gateway. This repo's established UI-proof pattern (the #239 Wingman page, #219 rail tests) is to render the REAL compiled component with bUnit, backed by a REAL `GatewayClient` whose `HttpClient` is stubbed, and to emit a standalone artifact (the real rendered markup + the real `app.css` + the page's scoped CSS) for a screenshot. That is exactly what is done here - so the screenshot below is the genuine compiled `Schedule` component, with **no Gateway booted** and therefore **zero risk to the running fleet** (no Tailscale Serve reconcile, no chance of firing a real session).

**Proof artifacts:** `schedule-page-rendered.html` + `schedule-page-rendered.png` (this dir).

## Acceptance criteria - Expected vs Actual

| # | Criterion | Evidence | Verdict |
|---|-----------|----------|---------|
| AC1 | Schedule nav item -> `/schedule`, dark-theme chrome | Nav entry added to `NavMenu.razor`; page renders in the real `app.css` theme (`schedule-page-rendered.png`) | PASS (render) |
| AC2 | Empty state, then a populated jobs table (name/target/schedule/next/last/status) | `SchedulePageTests.Renders_jobs_table...` + `.Empty_state_when_no_jobs`; screenshot shows the populated table | PASS |
| AC3 | Valid create -> `201` and the job appears | `GatewayClientCronTests.CreateCronJobAsync_PostsAndReturnsCreatedJob`; page calls `CreateCronJobAsync` then refreshes | PASS (unit) |
| AC4 | Invalid cron -> inline `400` message, not added | `GatewayClientCronTests.CreateCronJobAsync_OnBadRequest_ThrowsWithServerMessage` (message surfaced); page binds it to `_formError` | PASS (unit) |
| AC5 | One-off and work-list jobs distinguishable in the table | screenshot: "once @ 2026-06-18..." vs "0 0 * * *"; "work list Tonight" vs "work list Backlog" | PASS |
| AC6 | Run-now -> a run appears in run-history | `GatewayClientCronTests.RunCronJobNowAsync...` + `SchedulePageTests.Selecting_a_job_shows_its_run_history` (worklist-started row) | PASS |
| AC7 | Edit (`PUT`) and Delete (`DELETE`) reflect | `GatewayClientCronTests.DeleteCronJobAsync_OnNotFound_Throws`; page wires `UpdateCronJobAsync`/`DeleteCronJobAsync` + refresh | PASS (unit) |
| AC8 | Enable/disable persists via `PUT` | page `ToggleEnabledAsync` sends the full DTO with flipped `Enabled` via `UpdateCronJobAsync` | PASS (code) |
| AC9 | Feedback within ~100ms, async load, no freeze | page loads via async `OnInitializedAsync` + 5s `PeriodicTimer` poll (the `Lists.razor` pattern); buttons disable during save | PASS (code) |

## Honest scope of this proof

I used the established **render-proof** pattern (real compiled page + real CSS, screenshotted) rather than booting a `GatewayHost` for a live browser click-through, because booting one runs the machine-global Tailscale Serve provisioner and could fire a real session - a risk to the user's running fleet (flagged before I started). The render screenshot + bUnit render tests + `GatewayClient` contract tests (incl. the `400`/`409` surfacing) prove the page and the wiring; the **end-to-end live browser round-trip (create -> fire -> history against a Gateway) is left for the QA Agent** to perform against isolated infrastructure. This is called out so the visual ACs are not overclaimed.

## CenCon impact
Adds the `/schedule` page, its nav entry, scoped CSS, and `GatewayClient` cron methods to the `cockpit` container. No security-posture change (inherits the Cockpit's existing Gateway auth; pure REST consumer). No contract change (`CronJobDto`/`CronRunRecord` already in `gateway_contracts`).

## Statement
I believe the page is implemented and renders correctly (screenshot of the genuine compiled component), with the REST wiring and error-surfacing covered by passing unit + bUnit tests, and the build clean. The one DoD element I did not perform myself - the live browser click-through against a Gateway - is explicitly handed to QA with the safety rationale above.

---

## QA bounce fix (cycle 2)

QA #488 found that editing a DISABLED job silently re-enabled it (and reset PreventOverlap), because `BuildFromForm()` hardcoded `Enabled = true` and `OpenEdit` did not capture the job's state.

**Fix:** the form now carries `_fEnabled` / `_fPreventOverlap`, defaulted on in `OpenCreate` and captured from the job in `OpenEdit`, and `BuildFromForm` emits both. An edit preserves them.

**Regression test:** `SchedulePageTests.Editing_a_disabled_job_preserves_enabled_false_and_preventoverlap_in_the_put_body` drives the real component through the edit modal (rename a disabled job + Save) and asserts the captured PUT body has `enabled=false` and `preventOverlap=false`. This test FAILS on the pre-fix code and PASSES now.

**Tests:** Cockpit.Tests cron+schedule -> **12 Passed** (was 11; +1 regression). Full solution builds clean.
