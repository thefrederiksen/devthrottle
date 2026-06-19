# QA Proof Report - Issue #483

## Cron jobs (2/3): firing engine, run history, and guards

**Verifier:** QA Agent (independent - own fresh build + own test run, did not trust the dev report).
**PR:** #486 (branch `issue-483-cron-firing-engine`, HEAD 03b807c).
**Mode:** standalone QA (outside the implementation-loop) -> NOT merged; merge left to the human.

## How I verified independently

1. Checked out the PR branch fresh and **built the whole solution myself**: `dotnet build cc-director.sln` -> **0 Warning(s), 0 Error(s)**.
2. **Ran the cron suite myself**: `dotnet test --filter Cron` -> **48 Passed, 0 Failed, 0 Skipped** (17 new for #483: 7 engine + 6 history + 4 real-HTTP endpoint; plus #482's 31).
3. **Adversarially audited the engine** (review-code lens) against the issue - focusing on the two subtle correctness points (catch-up "fire once" and overlap release) - and swept the new source for forbidden patterns.

No live full Gateway booted: it would run the machine-global Tailscale Serve provisioner and bind :7878, colliding with the running Gateway (CLAUDE.md rule 0). The firing engine is proven deterministically via an injectable `IClock` (FakeClock) + fake `ICronSessionStarter`, and run-now/history over real HTTP - the equivalent running-app proof for a headless slice.

## Acceptance criteria - Expected vs Actual (independently judged)

| # | Criterion | Evidence (reproduced) | Verdict |
|---|-----------|-----------------------|---------|
| AC1 | Due job fires: session started on target, run recorded | `EvaluateDue_RecurringJobDue_Fires_RecordsRun_AdvancesNextRun` - starter invoked, 1 run, NextRunUtc advanced to future | PASS |
| AC2 | `POST /cron/jobs/{id}/run` fires immediately + records | `RunNow_FiresAndRecords_WithFullRecordShape` (real HTTP 200 + record) | PASS |
| AC3 | `GET /cron/jobs/{id}/runs` records carry scheduled/fired/target/session/infra/task | `Runs_ReturnsHistory_AfterRunNow` + record-shape assertions; infra ("started") distinct from task ("unknown") | PASS |
| AC4 | Overlap: in-flight job's 2nd fire skipped, no 2nd session | `RunNow_WhilePriorRunInFlight_SecondIsSkippedAsOverlap` - 2nd -> SkippedOverlap, starter invoked once | PASS |
| AC5 | Disabled job does not fire; enabling resumes | `EvaluateDue_DisabledJob_DoesNotFire_ThenEnablingResumes` - 0 while disabled, 1 after enable | PASS |
| AC6 | Missed fire fires AT MOST ONCE, not per-interval | `EvaluateDue_MissedFireWhileDown_FiresAtMostOnce_NotPerInterval` - 3-day downtime -> exactly 1 fire, infra="catch-up", advanced | PASS |
| AC7 | One-off fires once then auto-disables | `EvaluateDue_OneOff_FiresOnce_ThenAutoDisables` - Enabled=false, NextRunUtc=null, no refire | PASS |

Audited the code paths behind the subtle ACs: catch-up recomputes `ComputeNextRunUtc(job, firedUtc)` to the next FUTURE occurrence (no backlog replay); the overlap claim is released in a `finally`; one-off disables with a null next-run; the sweep timer is disposed in `StopAsync` and its callback owns a boundary try/catch. `Fire_WhenStarterReportsError_RecordsNotStarted` confirms a failed start still records `infraStatus=not-started` with no sessionId.

## Method / regression lens
- **Forbidden patterns:** swept the 6 new source files for null-forgiving `!`, `.Result`, `.Wait()` -> **none** (`!started`/`!isManual` are logical-not, allowed).
- **Catch blocks:** boundary-only (sweep timer callback; engine per-job isolation at the timer boundary; history quarantine/save log-and-rethrow). No silent fallbacks.
- **Security (DT rules):** no new auth surface (inherits host-wide token middleware); engine reuses the existing Director session-create path (no new Director surface); no secrets logged. No blocking rule violated.
- **Tests:** present for all new behavior; warnings-as-errors clean.

### Non-blocking observations (not defects)
1. `RunNowAsync` fires even a disabled job (no enabled-check on the manual path). Not constrained by any AC; a reasonable manual-override semantic. Flagged for awareness.
2. `architecture_manifest.yaml` does not yet list the cron firing components - same deferral as #482; recommend Support records the full set when #484 lands. No security-posture change.

## Verdict

**VERIFIED - all 7 acceptance criteria met.** Build clean, 48/48 cron tests pass on an independent build, no forbidden patterns, no security drift, catch-up/overlap/one-off logic audited correct. Standalone QA does not merge; **PR #486 is ready for the human to merge.**
