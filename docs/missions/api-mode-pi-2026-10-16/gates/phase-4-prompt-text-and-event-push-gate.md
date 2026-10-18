# Phase 4 gate: the prompt's text and the event push, together (pull requests #4121 and #4090)

Run by the phase 4 gate seat on SOREN_NORTH (Windows), 17 October 2026. The two pull requests were gated as one
because they land on main back to back. This is the second run: the prompt text field became optional on the wire
after the first run, so pull request #4121 moved and the pair was gated again. The first run is kept as a note at
the end.

## Run 2 (the run that stands)

- Commit tested: `eccf38919`, a clean local merge of origin/main at `f08847c53` with `apimode/p4-prompt-text` at
  `1b4df19be` (pull request #4121, a fast-forward because that branch already carried main) and then
  `apimode/p4-event-push` at `dac0ed6e3` (pull request #4090). Both merges applied without conflict. Nothing was
  pushed from the gate worktree.
- Command: `.\scripts\test-local.ps1 -Parked` (Debug), with TEMP and TMP set for that process only to
  `D:\ReposFred\gate-temp\4121-4090-r2`. Every suite ran all of its tests and reported outcome Completed, except the
  two suites below that reported Failed.
- Time: 32 minutes 43 seconds including the build (19:09:10 to 19:41:53); the rerun of the failed tests
  (`-RerunFailed`, once) 1 minute 37 seconds. The run waited 40 minutes to start because three other gate runs were
  active and free memory was under the 12 GB floor.
- Run folder `D:\ReposFred\gate-temp\4121-4090-r2\cc-test-local-0f3a448b`; rerun folder `cc-test-local-e2a9e6b4`.

| Suite | Passed | Failed | Skipped |
|---|---|---|---|
| Core.UnitTests | 2,249 | 1 | 0 |
| Avalonia.Tests | 1,091 | 0 | 0 |
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
| Gateway.Tests shard 3 of 4 | 415 | 0 | 3 |
| Gateway.Tests shard 4 of 4 | 468 | 1 | 0 |
| Core.Tests | 4,540 | 0 | 0 |
| Gateway.UnitTests | 14,175 | 0 | 0 |

Totals: 25,653 passed, 2 failed, 3 skipped (the three skipped tests are Linux-only).

The two failures:

1. `CcDirector.Core.UnitTests.Background.BackgroundWorkRegisterTests.EveryTimerWatcherAndWaitingLoop_HasARowInTheRegister_AndEveryRowStillPointsAtOne`,
   in Core.UnitTests, after 32 seconds, with the message "docs/BackgroundWork.md and the code disagree: COUNT DIFFERS:
   tools/cc-director-setup-engine/LauncherSelfUpdate.cs has 3 site(s) in code, the register says 2". It failed again
   on the rerun with the same message. This is main's own red, not the pull requests': commit `1db17cec5` (#4162,
   on the tested base) added a third wait to the launcher self-update without registering it, and commit `e3ada0536`
   (#4175) registered it on main after this run's base was cut. Neither pull request touches
   `tools/cc-director-setup-engine/LauncherSelfUpdate.cs` or that register row; their only change to
   `docs/BackgroundWork.md` is the new row for `ApiModeEventPusher.cs`. Proof: with the current origin/main
   (`0c9e710ac`) merged into the tested commit on a throwaway branch, the same test passed (one test, Passed, 3
   seconds), and the throwaway branch was then discarded. The tested commit stands as named above.
2. `CcDirector.Gateway.Tests.CockpitParityEndpointsTests.Handovers_lists_seeded_handover_with_parsed_frontmatter`,
   in Gateway.Tests shard 4 of 4, in the test's constructor, with the message "[AtomicFileReplace] could not move
   ...repositories.json...tmp over ...repositories.json: Access is denied. (Win32 error 5), still refused after 10.0 s
   (163 attempts). Something is holding the new file - commonly real-time antivirus or another file-system filter".
   It passed on the rerun of the same commit (one test, Passed, 0.6 seconds). Neither pull request touches
   `src\CcDirector.Gateway.Tests`. It falls under the standing ruling that a test in an untouched suite which fails
   once and passes on rerun does not block but is named. The run shared the machine with other gate runs and the
   real-time scanner; a file held for ten seconds is the scanner, not the code under test.

Nothing hung or crashed. No "cannot create a symbolic link" failure occurred on this machine, which holds the
privilege. `SystemdUnitSafetyTests.GatewayUnitContent_CarriesTheStartLimitInTheUnitSection`, known red on main on
Windows, did not fail in this run.

One thing the Delivery Lead must know before landing: origin/main moved to `0c9e710ac` during this run (phase 3,
#4100, among others), and merging that main into the tested commit conflicts in
`src/CcDirector.Gateway.UnitTests/ApiModeEvents/SessionLiveEndpointTests.cs`. The two branches need that conflict
resolved against current main before they can land; that resolution was not gated here.

**Verdict: passes the gate under the standing rulings.** The one repeat failure is main's own and is already fixed
on main; the one single failure is a file hold in an untouched suite that passed on rerun.

## Run 1 (superseded, kept as a note)

Run on the same machine earlier on 17 October 2026 against `38367b5ee`, a clean merge of origin/main at `92fafe201`
with `apimode/p4-prompt-text` at `b4c0c84cb` and `apimode/p4-event-push` at `dac0ed6e3`. Command and temp folder as
above but under `D:\ReposFred\gate-temp\4121-4090` (run folder `cc-test-local-67ddb61d`, rerun `cc-test-local-a41ce222`).
Time 28 minutes 13 seconds plus a 1 minute 12 second rerun. Totals 25,584 passed, 1 failed, 3 skipped. The one
failure, `CcDirector.Setup.Engine.Tests.LauncherReadinessWaitTests.WhileTheStartedProcessIsAlive_ThePollingContinuesToTheCeiling`
("Only 1 polls before the wait ended - it stopped on something other than the ceiling"), passed on rerun, in a suite
neither pull request touches; it did not recur in run 2. Verdict then: passes under the standing rulings. Superseded
because pull request #4121 changed after it.
