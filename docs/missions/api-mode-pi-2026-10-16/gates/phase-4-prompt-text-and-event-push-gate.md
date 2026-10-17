# Phase 4 gate: the prompt's text and the event push, together (pull requests #4121 and #4090)

Run by the phase 4 gate seat on SOREN_NORTH (Windows), 17 October 2026. The two pull requests were gated as one
because they land on main back to back.

- Commit tested: `38367b5ee`, a clean local merge of origin/main at `92fafe201` with `apimode/p4-prompt-text` at
  `b4c0c84cb` (pull request #4121) and then `apimode/p4-event-push` at `dac0ed6e3` (pull request #4090). Both merges
  applied without conflict. Nothing was pushed from the gate worktree.
- Command: `.\scripts\test-local.ps1 -Parked` (Debug), with TEMP and TMP set for that process only to
  `D:\ReposFred\gate-temp\4121-4090`. Every suite ran all of its tests and reported outcome Completed, except the one
  suite below that reported Failed.
- Time: 28 minutes 13 seconds including the build; the rerun of the failed test (`-RerunFailed`, once) 1 minute 12 seconds.
- Run folder `D:\ReposFred\gate-temp\4121-4090\cc-test-local-67ddb61d`; rerun folder `cc-test-local-a41ce222`.

| Suite | Passed | Failed | Skipped |
|---|---|---|---|
| Core.UnitTests | 2,250 | 0 | 0 |
| Avalonia.Tests | 1,091 | 0 | 0 |
| Engine.Tests | 85 | 0 | 0 |
| HostedAgent.Tests | 85 | 0 | 0 |
| Launcher.Tests | 214 | 0 | 0 |
| Terminal.Avalonia.Tests | 71 | 0 | 0 |
| Reclaim.Tests | 305 | 0 | 0 |
| DeliveryQualification.Tests | 17 | 0 | 0 |
| cc-director-setup.Tests | 39 | 0 | 0 |
| cc-director-setup-engine.Tests | 913 | 1 | 0 |
| cc-director-setup-cli.Tests | 74 | 0 | 0 |
| Gateway.Tests shard 1 of 4 | 412 | 0 | 0 |
| Gateway.Tests shard 2 of 4 | 480 | 0 | 0 |
| Gateway.Tests shard 3 of 4 | 415 | 0 | 3 |
| Gateway.Tests shard 4 of 4 | 469 | 0 | 0 |
| Core.Tests | 4,539 | 0 | 0 |
| Gateway.UnitTests | 14,125 | 0 | 0 |

Totals: 25,584 passed, 1 failed, 3 skipped (the three skipped tests are Linux-only).

The one failure:

1. `CcDirector.Setup.Engine.Tests.LauncherReadinessWaitTests.WhileTheStartedProcessIsAlive_ThePollingContinuesToTheCeiling`,
   in cc-director-setup-engine.Tests, after 4.2 seconds, with the message "Only 1 polls before the wait ended - it
   stopped on something other than the ceiling." It passed on the rerun of the same commit (one pass, outcome
   Completed). Neither pull request touches `tools\cc-director-setup-engine` or its tests. It falls under the
   standing ruling that a test in an untouched suite which fails once and passes on rerun does not block but is
   named. The run was on a machine under heavy load from other work (free memory had to be waited on for 35
   minutes before the run could start), which is the likeliest reason a timing test polled only once. The test
   script's own verdict calls the test defective and asks for it to be deleted or fixed today in a test-only pull
   request on main; that is owed by whoever owns the setup engine suite, not by this mission.

Nothing hung or crashed. The nine "cannot create a symbolic link" tests named in `phase-4-event-push-gate.md` all
passed on this machine, which holds the privilege. `SystemdUnitSafetyTests.GatewayUnitContent_CarriesTheStartLimitInTheUnitSection`,
known red on main on Windows, did not fail in this run.

**Verdict: passes the gate under the standing rulings.**
