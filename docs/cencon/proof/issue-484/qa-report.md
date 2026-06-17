# QA Proof Report - Issue #484

## Cron jobs (3/3): named-work-list use case

**Verifier:** QA Agent (independent - own fresh build + own test run; did not trust the dev report).
**PR:** #487 (branch `issue-484-cron-worklist-usecase`, HEAD cc32a67).
**Mode:** standalone QA -> NOT merged; merge left to the human.

## How I verified independently

1. Checked out the PR branch fresh and **built the whole solution myself**: `dotnet build cc-director.sln` -> **0 Warning(s), 0 Error(s)**.
2. **Ran the cron suite myself**: `dotnet test --filter Cron` -> **61 Passed, 0 Failed, 0 Skipped** (13 new for #484: 7 trigger + 4 engine-dispatch + 2 validation; plus 48 from #482/#483).
3. **Adversarially audited** the new code: dispatch-by-action-type, the pre-check ordering (no duplicate claim), the background drain + machine-slot release, and the validation change for regressions.

No live full Gateway booted (Tailscale provisioner + :7878 bind would collide with the running Gateway). The integration is proven without a Director via the three seams; the launcher test drives a REAL `WorkListRunner` through the new path with a fake `IImplSessionDriver`.

## Acceptance criteria - Expected vs Actual (independently judged)

| # | Criterion | Evidence (reproduced) | Verdict |
|---|-----------|-----------------------|---------|
| AC1 | A work-list cron job, when fired, causes the #274 runner to CLAIM the list + start processing | `DrainLauncher_ClaimsList_AndDrainsItemsInOrder` (claim taken via the real runner; items started) + `WorkListJob_Dispatches_ToWorkListRunner_RecordsWorklistStarted_NoSession` (engine routes a work-list job to the runner, records `worklist-started`, null sessionId) | PASS |
| AC2 | Items processed in list order | `DrainLauncher_ClaimsList_AndDrainsItemsInOrder` - start order 101,102,103 through the shipped #274 `WorkListRunner` | PASS |
| AC3 | Empty / already-claimed / no-director / machine-busy handled cleanly, no crash, no duplicate claim | `Trigger_EmptyList...`, `Trigger_AlreadyClaimedList_ReturnsAlreadyClaimed_NoDuplicateClaim` (launcher never invoked; original claim untouched), `Trigger_NoSuchList...`, `Trigger_NoSuchDirector...`, `Trigger_MachineBusy...` | PASS |

Audited supporting behavior: the engine dispatches by action type (`SeedJob_Dispatches_ToSessionStarter_NotWorkListRunner` proves seed jobs are unaffected); a work-list job advances its schedule (`WorkListJob_Recurring_Due_Fires_AndAdvancesSchedule`); the Started path launches the drain in the BACKGROUND with a `cron:<jobId>:` consumer and releases the machine slot in a `finally` (`Trigger_Started_LaunchesDrain...`); validation accepts a work-list action without a seed and rejects neither-present (`Validate_WorkListAction_NoSeed_Ok` / `Validate_NeitherSeedNorWorkList_Fails`).

## Method / regression lens
- **Forbidden patterns:** swept the 4 new source files for null-forgiving `!`, `.Result`, `.Wait()` -> **none**.
- **Background task:** the detached drain owns its try/catch and releases the machine slot in `finally` - no unobserved exception, no leaked slot.
- **Regression:** the `Validate` change (seed OR workListName) did not break prior behavior - all 48 #482/#483 tests still pass; seed-only jobs remain valid; neither-present still fails.
- **Security (DT rules):** no new auth surface (inherits host-wide token middleware); reuses the shipped #274 drain path (no new Director surface); no secrets logged. No blocking rule violated.

### Non-blocking observations (not defects)
1. `architecture_manifest.yaml` does not yet list the cron components. The feature is COMPLETE after this slice (#482+#483+#484); recommend the Support Agent record the full set (store, engine, run history, work-list trigger, Cronos dep) now. No security-posture change.
2. (Carried from #483) run-now fires even a disabled job - not constrained by any AC; reasonable manual override.

## Verdict

**VERIFIED - all 3 acceptance criteria met.** Build clean, 61/61 cron tests pass on an independent build, no forbidden patterns, no regression from the validation change, no security drift; the launch path claims + drains in order through the shipped runner and every edge is handled cleanly with no duplicate claim. Standalone QA does not merge; **PR #487 is ready for the human to merge** - the final slice of epic #479.
