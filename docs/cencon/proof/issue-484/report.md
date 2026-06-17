# Proof Report - Issue #484

## Cron jobs (3/3): named-work-list use case

**Part 3 of 3 of epic #479** - the headline "midnight, run the loop over a list of work items" use case, on top of the merged #482 (store/CRUD) and #483 (firing engine).

**Branch/PR:** issue-484-cron-worklist-usecase (PR #487)
**Build:** `dotnet build cc-director.sln` -> **0 Warning(s), 0 Error(s)**.
**Tests:** `dotnet test --filter Cron` -> **61 Passed, 0 Failed, 0 Skipped** (13 new for #484 + 48 from #482/#483).

## How this slice is proven

A cron job's action gains an optional `workListName`. When set, the fire triggers the shipped #274 work-list runner to drain that list on the target Director instead of starting a single seeded session. The integration is proven without a live Director via three seams: `ICronWorkListRunner` (engine dispatch), `ICronDirectorResolver` (Director lookup), and `ICronWorkListDrainLauncher` (the actual drain) - the launcher's driver is injectable so a real `WorkListRunner.DrainAsync` is driven with a fake `IImplSessionDriver`, proving the claim + ordered drain through the new path.

## Acceptance criteria - Expected vs Actual

| # | Criterion | Proof (test) | Expected | Actual |
|---|-----------|--------------|----------|--------|
| AC1 | A work-list cron job, when fired, causes the #274 runner to CLAIM the list and start processing | `CronWorkListTriggerTests.DrainLauncher_ClaimsList_AndDrainsItemsInOrder` (list claimed, items started) + `CronEngineWorkListTests.WorkListJob_Dispatches_ToWorkListRunner_RecordsWorklistStarted_NoSession` (engine routes the job to the runner, records `worklist-started`) | claim taken; runner invoked | PASS |
| AC2 | Items processed in list order, advancing per terminal signal | `DrainLauncher_ClaimsList_AndDrainsItemsInOrder` - start order = 101,102,103 through the shipped #274 `WorkListRunner` | in order | PASS |
| AC3 | Empty / already-claimed / no-director / machine-busy handled cleanly, no crash, no duplicate claim | `Trigger_EmptyList...`, `Trigger_AlreadyClaimedList_ReturnsAlreadyClaimed_NoDuplicateClaim` (original claim untouched, launcher never invoked), `Trigger_NoSuchList...`, `Trigger_NoSuchDirector...`, `Trigger_MachineBusy...` | each a clean outcome, no launch | PASS |

Supporting: `WorkListJob_Dispatches...` / `SeedJob_Dispatches_ToSessionStarter_NotWorkListRunner` prove the engine routes by action type (work-list vs seed); `WorkListJob_Recurring_Due_Fires_AndAdvancesSchedule` proves the schedule advances for a work-list job; `Trigger_Started_LaunchesDrain...` proves the background drain is launched with the list + a `cron:<jobId>:` consumer and the machine slot is held during and released after the drain; `CronScheduleTests.Validate_WorkListAction_NoSeed_Ok` / `Validate_NeitherSeedNorWorkList_Fails` cover validation.

Note on AC1/AC2 scope: the per-item ordering and terminal-signal advancement are the shipped #274 `WorkListRunner`'s behavior (already covered by `WorkListRunnerTests`). This slice's new code is the GLUE (cron job -> trigger that runner); the launcher test drives a real `WorkListRunner` through that glue to prove the claim + order end-to-end without a live Director.

## CenCon impact

Adds the work-list trigger components to the `gateway` container and the `workListName` field to `gateway_contracts`. No security-posture change (reuses the shipped #274 drain path and the host-wide token middleware; no new Director surface; no secrets logged). With this slice the cron feature is complete; the Support Agent should now record the full cron component set (store, engine, run history, work-list trigger, Cronos dep) in `architecture_manifest.yaml`.

## Statement

I believe this slice (#484) is finished: a cron job can drain a named work list on a schedule, the engine dispatches correctly by action type, the pre-check guards handle every edge cleanly with no duplicate claim, and the launch path claims + drains in order through the shipped runner. The solution builds clean with zero warnings. With #482 + #483 + #484 merged, the original request - "at midnight, run the implementation loop over a list of work items" - is functional end to end.
