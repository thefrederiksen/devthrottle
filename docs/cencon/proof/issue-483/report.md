# Proof Report - Issue #483

## Cron jobs (2/3): firing engine, run history, and guards

**Part 2 of 3 of epic #479.** Makes cron jobs actually fire, on top of the merged #482 store/CRUD.

**Branch/PR:** issue-483-cron-firing-engine (PR #486)
**Build:** `dotnet build cc-director.sln` -> **Build succeeded, 0 Warning(s), 0 Error(s)**.
**Tests:** `dotnet test --filter Cron` -> **48 Passed, 0 Failed, 0 Skipped** (17 new for #483 + 31 from #482).

## How this slice is proven

The firing engine is made deterministically testable with two seams: an injectable `IClock` (a `FakeClock` makes a job "due" without waiting on the wall clock) and an injectable `ICronSessionStarter` (a fake stands in for a live Director). So every guard is proven without a running Director. The run-now + history REST surface is proven over **real HTTP** (`CronRunEndpointsTests` boots the actual `CronRunEndpoints` on a loopback port). No live full Gateway is booted (it would run the machine-global Tailscale Serve provisioner and bind :7878, colliding with the running Gateway - CLAUDE.md rule 0); the engine + real-HTTP tests are the equivalent running-app proof for this headless slice.

## Acceptance criteria - Expected vs Actual

| # | Criterion | Proof (test) | Expected | Actual |
|---|-----------|--------------|----------|--------|
| AC1 | A due job fires: session started on target Director, run recorded | `CronEngineTests.EvaluateDue_RecurringJobDue_Fires_RecordsRun_AdvancesNextRun` | starter invoked with the job, 1 run recorded, NextRunUtc advanced to future | PASS |
| AC2 | `POST /cron/jobs/{id}/run` fires immediately + records | `CronRunEndpointsTests.RunNow_FiresAndRecords_WithFullRecordShape` | 200 + CronRunRecord | PASS |
| AC3 | `GET /cron/jobs/{id}/runs` returns records with scheduledUtc/firedUtc/targetDirectorId/sessionId/infraStatus/taskStatus | `CronRunEndpointsTests.RunNow_FiresAndRecords...`, `.Runs_ReturnsHistory...` | all six fields; infra ("started") distinct from task ("unknown") | PASS |
| AC4 | Overlap: in-flight job's second fire skipped, no 2nd session | `CronEngineTests.RunNow_WhilePriorRunInFlight_SecondIsSkippedAsOverlap` | 2nd run -> SkippedOverlap, starter invoked once | PASS |
| AC5 | Disabled job does not fire; enabling resumes | `CronEngineTests.EvaluateDue_DisabledJob_DoesNotFire_ThenEnablingResumes` | 0 fires while disabled; 1 after enable | PASS |
| AC6 | Missed fire fires AT MOST ONCE on recovery, not per-interval | `CronEngineTests.EvaluateDue_MissedFireWhileDown_FiresAtMostOnce_NotPerInterval` | 3-day downtime -> exactly 1 fire, infra="catch-up", NextRunUtc advanced | PASS |
| AC7 | One-off fires once then auto-disables | `CronEngineTests.EvaluateDue_OneOff_FiresOnce_ThenAutoDisables` | Enabled=false, NextRunUtc=null, no refire | PASS |

Supporting: `Fire_WhenStarterReportsError_RecordsNotStarted` proves a failed session-start still records a run with `infraStatus=not-started` and no sessionId (infra-vs-task honesty). `CronRunHistoryStoreTests` proves newest-first order, the per-job cap, persistence round-trip, and corrupt-file quarantine.

## CenCon impact

Adds the firing engine, run-history store, and run endpoints to the `gateway` container, plus a background sweep timer on `GatewayHost`. No security-posture change: routes inherit the host-wide token middleware; the engine reuses the existing Director session-create path (no new Director surface); no secrets logged. Recommend the Support Agent record the full cron component set in `architecture_manifest.yaml` when part 3 (#484) lands.

## Statement

I believe this slice (#483) is finished: cron jobs fire on a background sweep, run history is recorded and served, and every guard (disabled, overlap, one-off, catch-up) is proven by a passing deterministic test. The solution builds clean with zero warnings. The named-work-list use case is intentionally out of scope and lands in #484.
