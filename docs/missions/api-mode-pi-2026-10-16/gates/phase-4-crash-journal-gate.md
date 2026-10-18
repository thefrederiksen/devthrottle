# Phase 4 gate: an interrupted API-mode session continues in API mode (pull request #4091)

Run by the phase 4 gate seat on SOREN_NORTH (Windows), 17 October 2026.

- Commit tested: `59e5f59b2`, a clean local merge of origin/main at `8994f5e1a` with the pull request branch
  `apimode/p4-crash-journal` at `981011661`. Nothing was pushed from the gate. Main moved to `1bbd9bd12` while
  the run was in progress; the tested merge is the one recorded here.
- Command: `.\scripts\test-local.ps1 -Parked` (Debug), with TEMP and TMP set for that process to
  `D:\ReposFred\gate-temp\4091`. Every suite ran all of its tests. No rerun was needed, because nothing failed.
- Time: 16 minutes 34 seconds for the run (2 minutes 12 seconds of that the build). The seat waited 27 minutes
  before starting, because this machine had fewer than 12 GB free, which the brief's limits forbid starting
  under; one other gate run was active for the first few minutes of that wait.
- Run folder `D:\ReposFred\gate-temp\4091\cc-test-local-c202655f`, deleted by the script after the green run.
  Gate record written by the script:
  `C:\Users\soren\AppData\Local\cc-director\test-gate\gated\59e5f59b29953e7886d0aac1fdaef3b086830553-20261018T011726Z.json`.

| Suite | Passed | Failed | Skipped |
|---|---|---|---|
| Core.UnitTests | 2,319 | 0 | 0 |
| Avalonia.Tests | 1,089 | 0 | 0 |
| Engine.Tests | 85 | 0 | 0 |
| HostedAgent.Tests | 85 | 0 | 0 |
| Launcher.Tests | 230 | 0 | 0 |
| Terminal.Avalonia.Tests | 71 | 0 | 0 |
| Reclaim.Tests | 305 | 0 | 0 |
| DeliveryQualification.Tests | 17 | 0 | 0 |
| cc-director-setup.Tests | 39 | 0 | 0 |
| cc-director-setup-engine.Tests | 917 | 0 | 0 |
| cc-director-setup-cli.Tests | 74 | 0 | 0 |
| Gateway.Tests shard 1 of 4 | 412 | 0 | 0 |
| Gateway.Tests shard 2 of 4 | 480 | 0 | 0 |
| Gateway.Tests shard 3 of 4 | 418 | 0 | 3 |
| Gateway.Tests shard 4 of 4 | 469 | 0 | 0 |
| Core.Tests | 4,540 | 0 | 0 |
| Gateway.UnitTests | 14,236 | 0 | 0 |

Totals: 25,786 passed, 0 failed, 3 skipped (the three skipped tests are Linux-only). Every suite reported
outcome Completed in the TRX verdict.

No failures. None of the three standing rulings was needed: no test failed with the "cannot create a symbolic
link" permission error on this machine, no test in an untouched suite failed, and no test had to be shown red on
main.

The pull request's two new test files, `ApiModeSessionJournalRowTests.cs` in Core.UnitTests and
`ApiModeSessionSurvivesACrashTests.cs` in Gateway.UnitTests, ran inside their suites and passed.

Nothing hung or crashed; the build had no warnings and no errors. The script's own verdict was GREEN, exit code 0.

**Verdict: passes the gate.**
