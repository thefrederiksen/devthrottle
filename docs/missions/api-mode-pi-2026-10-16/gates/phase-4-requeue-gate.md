# Phase 4 gate: every new generation re-announces the queue (pull request #4190)

Run by the phase 4 gate seat on SOREN_NORTH (Windows), 17 October 2026.

- Commit tested: `d026bb1bf`, a clean local merge of origin/main at `58e563ba0` with the pull request branch at
  `c48d0b9b7` (the head of `apimode/p4-requeue-on-generation`, which answers the two low findings of its review).
  Nothing was pushed from the gate. Main moved to `794874c7c` (eight commits, among them the take-the-wheel pull
  request #4094 and the prompt-text pull request #4146) while the run was in progress; the tested merge is the one
  recorded here.
- Command: `.\scripts\test-local.ps1 -Parked` (Debug), with TEMP and TMP set for that process to
  `D:\ReposFred\gate-temp\4190`. Every suite ran all of its tests. No rerun was needed: nothing failed.
- Time: 22 minutes 18 seconds for the run, 2 minutes 30 seconds of that the build. The seat waited 1 hour 9
  minutes before starting, because this machine had fewer than 12 GB free the whole time (two other gate runs,
  for #4179 and #4094, and a build agent's test run were sharing it), which the brief's limits forbid starting
  under. The run started at 23:49 with 14.3 GB free and one other gate run (#4146) active.
- Run folder `D:\ReposFred\gate-temp\4190\cc-test-local-1e5f0252`, deleted by the script after the green run.
  Gate record written by the script:
  `C:\Users\soren\AppData\Local\cc-director\test-gate\gated\d026bb1bfae14c850686803681508e163c082ce6-20261018T034938Z.json`.

| Suite | Passed | Failed | Skipped |
|---|---|---|---|
| Core.UnitTests | 2,388 | 0 | 0 |
| Avalonia.Tests | 1,091 | 0 | 0 |
| Engine.Tests | 85 | 0 | 0 |
| HostedAgent.Tests | 85 | 0 | 0 |
| Launcher.Tests | 230 | 0 | 0 |
| Terminal.Avalonia.Tests | 71 | 0 | 0 |
| Reclaim.Tests | 305 | 0 | 0 |
| DeliveryQualification.Tests | 22 | 0 | 0 |
| cc-director-setup.Tests | 39 | 0 | 0 |
| cc-director-setup-engine.Tests | 924 | 0 | 0 |
| cc-director-setup-cli.Tests | 74 | 0 | 0 |
| Gateway.Tests shard 1 of 4 | 412 | 0 | 0 |
| Gateway.Tests shard 2 of 4 | 480 | 0 | 0 |
| Gateway.Tests shard 3 of 4 | 418 | 0 | 3 |
| Gateway.Tests shard 4 of 4 | 469 | 0 | 0 |
| Gateway.UnitTests | 14,277 | 0 | 0 |
| Core.Tests | 4,540 | 0 | 0 |

Totals: 25,910 passed, 0 failed, 3 skipped (the three skipped tests are Linux-only).

No test failed, so none of the three standing rulings was needed: no test failed with the "cannot create a
symbolic link" permission error on this machine, no test in an untouched suite failed once, and no test red on
main was met. The two new test files the pull request adds (`QueuedPromptsTests` in Core.UnitTests and
`ApiModeEventPusherQueueTests` in Gateway.UnitTests) ran inside their suites and passed. The script's
own verdict was GREEN (exit code 0), and every suite reported outcome Completed with executed equal to total
except shard 3 of Gateway.Tests, whose three unexecuted tests are the Linux-only skips.

Nothing hung or crashed; the build had no warnings and no errors.

**Verdict: passes the gate.**
