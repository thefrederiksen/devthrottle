# Phase 2 gate: the display fold and the live stream (pull requests #4086 and #4083)

Run by the phase 2 Tech Lead's gate seats on SORENLAPTOP (Windows), 16 October 2026. Pull request #4083 is
stacked on #4086, so one run on #4083's head covers both.

## First run: no verdict

- Commit tested: `e9e6f3a3d`. Command: `.\scripts\test-local.ps1 -Parked` (Debug).
- Verdict line: `NEVER RAN` (exit 9). `Core.UnitTests` passed 2,006 of 2,006 tests and then its test host hung;
  the blame collector stopped it after 10 minutes. The hanging test was
  `ApiModeTransportEndingTests.DisposeAsync_AfterTheDrainGraceReadsFailOnTheirClosedPipe_NoFaultAndNoKill`
  (phase 1, Director side, not touched by these pull requests); its class alone passed 12 of 12. Reported to
  the Delivery Lead for the Director side. Every Gateway suite passed in that run (Gateway.UnitTests 13,807 of 13,807).
- Run folder `C:\Users\soren\AppData\Local\Temp\cc-test-local-8ce88fe9` (hang dump inside).

Main then gained #4088, so both branches took main and the gate ran again.

## Second run: the verdict

- Commit tested: `5384b2180` (head of #4083, holding #4086 and main at `85263c060`).
- Command: `.\scripts\test-local.ps1 -Parked` (Debug). Every suite ran all of its tests.
- Verdict line: `FAILED in 6 projects, 12 failed tests`. Run folder
  `C:\Users\soren\AppData\Local\Temp\cc-test-local-7c347102`; rerun of the failed tests (`-RerunFailed`, once) in
  `C:\Users\soren\AppData\Local\Temp\cc-test-local-a0ef970f`.

| Suite | Passed | Failed | Skipped |
|---|---|---|---|
| Core.UnitTests | 2,126 | 1 | 0 |
| Avalonia.Tests | 1,063 | 0 | 0 |
| Engine.Tests | 85 | 0 | 0 |
| HostedAgent.Tests | 85 | 0 | 0 |
| Launcher.Tests | 214 | 0 | 0 |
| Terminal.Avalonia.Tests | 71 | 0 | 0 |
| Reclaim.Tests | 305 | 0 | 0 |
| DeliveryQualification.Tests | 17 | 0 | 0 |
| cc-director-setup.Tests | 39 | 0 | 0 |
| cc-director-setup-engine.Tests | 883 | 1 | 0 |
| cc-director-setup-cli.Tests | 74 | 0 | 0 |
| Gateway.Tests shard 1 of 4 | 412 | 0 | 0 |
| Gateway.Tests shard 2 of 4 | 478 | 2 | 0 |
| Gateway.Tests shard 3 of 4 | 395 | 6 | 0 |
| Gateway.Tests shard 4 of 4 | 468 | 1 | 0 |
| Gateway.UnitTests | 13,819 | 0 | 0 |
| Core.Tests | 4,526 | 1 | 0 |

The twelve failures. None is in code these pull requests change; their diff is the Gateway, its contracts,
its tests and the client schema.

1. `CatchLogLineScanTests.EveryLogLineInACatch_IsReportedOrSaysWhyNot`: two log lines added by #4081
   (`CatalogReadExecutor.cs`), already on main. Failed again on rerun. Under the Delivery Lead's ruling of
   16 October, a known failure on main until its fix merges.
2. The three symbolic-link tests under the Delivery Lead's standing ruling (`phase-2-session-fields-gate.md`),
   failing again on rerun with the same error.
3. Six `SessionFileAccessTests` tests that create symbolic links (`Decide_symbolicLinkToADotfileUnderHome_isRefused`,
   `Decide_symbolicLinkFromANamedPathToAnOrdinaryFile_isAllowed`, `Decide_symbolicLinkFromANamedPathToASecret_isRefused`,
   `Decide_symbolicLinkInsideANamedFolderToAnUnnamedFile_isRefused`,
   `Decide_symbolicLinkInsideTheWorkingFolderToAProtectedRoot_isRefused`, `Decide_servesTheRealPathNotTheLink`),
   added by #4081 on main, failing with the same cause (this host cannot create a symbolic link), again on rerun.
4. `LauncherReadinessWaitTests.WhileTheStartedProcessIsAlive_ThePollingContinuesToTheCeiling` ("Only 18 polls
   before the wait ended"): passed on rerun.
5. `WingmanTtsStatusPassthroughTests.PostTts_Upstream429WithDateRetryAfter_NormalizesTheHintToSeconds`
   ("The given header was not found"): passed on rerun.

Items 3 to 5 are outside the standing ruling and were put to the Delivery Lead before merging; none is
touched here. The Gateway unit tests, which hold these pull requests' tests, passed in full.
