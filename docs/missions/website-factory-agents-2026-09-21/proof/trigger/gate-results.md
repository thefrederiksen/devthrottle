# Gate results - the trigger

Run on SOREN_NORTH on 21 September 2026, in the worktree `devthrottle-wbf-trigger`, cut from origin/main.
Every number below was read from the run's own result files, not from a summary.

## Default gate: `.\scripts\test-local.ps1`

| Suite | Outcome | Tests |
|---|---|---|
| CcDirector.Core.UnitTests | Completed | 793 of 793 |
| CcDirector.Avalonia.Tests | Completed | 800 of 800 |
| CcDirector.Engine.Tests | Completed | 68 of 68 (includes the 5 new ProcessJob tests) |
| CcDirector.HostedAgent.Tests | Completed | 88 of 88 |
| CcDirector.Terminal.Avalonia.Tests | Completed | 30 of 30 |
| CcDirector.Reclaim.Tests | Completed | 310 of 310 |
| cc-director-setup.Tests | Completed | 25 of 25 |
| cc-director-setup-engine.Tests | Completed | 619 of 619 |
| CcDirector.Launcher.Tests | **FAILED** | 195 passed, 2 failed of 197 |

**The two Launcher failures are not from this change.** They are the two `LauncherDeclaredCapabilitiesTests`
tests, and they fail the same way on a clean origin/main worktree (commit 0166bde52) with none of this work in
it. Issue #3242 tracks them.

## Parked suites

| Suite | How it ran | Result |
|---|---|---|
| CcDirector.Gateway.UnitTests | In full, plain `dotnet test`, no PostgreSQL | 7059 passed, 0 failed, 8 skipped. The 8 skipped tests are 7 PostgreSQL-backed proofs and 1 architecture test, so they did NOT run in this pass. |
| CcDirector.Gateway.UnitTests | `-Parked` with a filter (Postgres, Migration, Trigger, SessionOrigin), against the run's own PostgreSQL | 139 passed, 1 skipped. The PostgreSQL-backed proofs in this suite ran and passed here. |
| CcDirector.Core.Tests | `-Parked` with the same filter | 41 passed. **The rest of Core.Tests did NOT run.** |
| CcDirector.Gateway.Tests | `FactoryTriggerHostTests`, plain `dotnet test` | 3 passed |
| CcDirector.Gateway.Tests | `-Gateway` with the filter, against the run's own PostgreSQL | **FAILED: 39 passed, 13 failed, 4 skipped of 56.** See below. |

### Why the Gateway.Tests PostgreSQL proofs are not verified

The 13 failures are not a pass, and they are not explained away here. What the result file shows:

- The first failure is a read timeout against the run's own PostgreSQL (`Timeout during reading attempt`).
- Eleven then fail with `55000: cannot connect to invalid database "ccpgproof_runc1ce8998_..."`. PostgreSQL marks
  a database invalid when a drop is cut off partway, so every test after the timeout inherited a broken database.
- One (`HostedStatsServeTests`) fails because the `gateway_stats` schema was never created on that broken
  database.

A second, narrower run of only the three PostgreSQL migration proof classes could not start at all: the run's
own PostgreSQL container did not accept a connection within 90 seconds. At the time the machine was running more
than twenty `dotnet` processes and fifteen containers belonging to other sessions. The record Developer's
parallel run shows PostgreSQL failures too.

That points at the machine rather than at the new migration, but it is **not proven**. The PostgreSQL migration
for the two trigger tables has therefore NOT been verified on a real PostgreSQL. It must run green on a quiet
machine before this merges: `.\scripts\test-local.ps1 -Parked`.

## cc-devthrottle Python tests

The whole suite: 3493 passed, 1 failed, 3 skipped. The failure was this change's own: the new `trigger` group was
not in the help test's list of top-level names (`test_shortHelp_EveryTopLevelNameIsAccountedFor`). It is fixed.
The three affected help files plus the trigger tests then gave 886 passed, 3 skipped. The whole suite was not run
again after that one-line fix.

## Mutation proof

Three pieces of the Gateway's decision were removed one at a time, the tests run, and the source restored:

- the one-at-a-time lock (is the last session still alive)
- the paused check
- the per-trigger lock around a report

Each removal turned its tests red: the skipped-running test, the paused test, and the two-reports-at-once test.

## Not done

- **No end-to-end run on a test Director.** The mandate made it optional (slot 5 or higher, through the
  `cc-director-launch` scheduled task). It was not done. The nearest thing is `FactoryTriggerHostTests`, which boots a
  real Gateway and drives the real Director client (`GatewayClient`) against it over HTTP. No real Director
  process is attached, so the real session starter's attempt to start a session fails, and the test proves that
  failure is recorded as a failed row with a RED "start failed" status. A session actually starting on a real
  Director has therefore not been seen.
