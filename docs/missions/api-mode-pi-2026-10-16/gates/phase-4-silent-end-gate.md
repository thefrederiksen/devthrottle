# Phase 4 gate: an agent that ends with code 0 after a failure reads failed (pull request #4129)

Run by the phase 4 gate seat on SOREN_NORTH (Windows), 17 October 2026.

- Commit tested: `b5c41bf67`, a clean local merge of origin/main at `92fafe201` with the pull request branch at
  `0795fde52`. Nothing was pushed from the gate. Main moved to `0c9e710ac` while the run was in progress; the
  tested merge is the one recorded here.
- Command: `.\scripts\test-local.ps1 -Parked` (Debug), with TEMP and TMP set for that process to
  `D:\ReposFred\gate-temp\4129`. Every suite ran all of its tests. Then `-RerunFailed` once, same TEMP.
- Time: 33 minutes 37 seconds for the run (6 minutes of that the build); the rerun 1 minute 3 seconds. The seat
  waited 2 hours 7 minutes before starting, because this machine had fewer than 12 GB free and two other gate runs
  active, which the brief's limits forbid starting under.
- Run folder `D:\ReposFred\gate-temp\4129\cc-test-local-3c82ecd4`; rerun folder `cc-test-local-b8884b52`. Gate
  record written by the script:
  `C:\Users\soren\AppData\Local\cc-director\test-gate\gated\b5c41bf67d282de395ebbfce2da83670ea2ea446-20261017T230831Z.json`.

| Suite | Passed | Failed | Skipped |
|---|---|---|---|
| Core.UnitTests | 2,231 | 0 | 0 |
| Avalonia.Tests | 1,089 | 0 | 0 |
| Engine.Tests | 85 | 0 | 0 |
| HostedAgent.Tests | 85 | 0 | 0 |
| Launcher.Tests | 214 | 0 | 0 |
| Terminal.Avalonia.Tests | 71 | 0 | 0 |
| Reclaim.Tests | 305 | 0 | 0 |
| DeliveryQualification.Tests | 17 | 0 | 0 |
| cc-director-setup.Tests | 39 | 0 | 0 |
| cc-director-setup-engine.Tests | 914 | 0 | 0 |
| cc-director-setup-cli.Tests | 74 | 0 | 0 |
| Gateway.Tests shard 1 of 4 | 412 | 0 | 0 |
| Gateway.Tests shard 2 of 4 | 480 | 0 | 0 |
| Gateway.Tests shard 3 of 4 | 415 | 0 | 3 |
| Gateway.Tests shard 4 of 4 | 469 | 0 | 0 |
| Gateway.UnitTests | 14,104 | 0 | 0 |
| Core.Tests | 4,538 | 1 | 0 |

Totals: 25,542 passed, 1 failed, 3 skipped (the three skipped tests are Linux-only).

The one failure:

1. `CcDirector.Core.Tests.Drivers.TerminalSubmitTests.ShortPrompt_TuiSwallowsTheEnter_NudgedThroughInsteadOfParking`
   failed with "the fake must have swallowed the submitting Enter" (`TerminalSubmitTests.cs` line 288), after
   0.47 seconds. On the rerun it passed.

The test file and the code it drives (`Drivers/TerminalSubmit.cs`, the recording backend and `ClaudeDriver`) are
not among the twelve files the pull request changes, and no file in the Core.Tests project is changed by it. The
test's own comment says the number of nudges is a race between the verify beat and the fake composer's 30
millisecond recovery window; the run shared the machine with other test runs. It falls under the standing ruling
for a test in an untouched suite failing once and passing on rerun: it does not block, and it is named here.

Two things to note for whoever merges:

- The script's own verdict on the rerun was FLAKY (exit code 10). It asks for the test to be deleted today in a
  test-only pull request on main, or the product fixed, and for `.\scripts\test-local.ps1 -ClearFlaky
  b5c41bf67d282de395ebbfce2da83670ea2ea446` to be run from a clean checkout of main once that is merged;
  `scripts\assert-gated.ps1` accepts this candidate only after that clearance is recorded. The test file was last
  changed in #3946 and the pull request under gate did not touch it, so that is a separate test-only change.
- Neither of the other two standing rulings was needed: no test failed with the "cannot create a symbolic link"
  permission error on this machine, and `SystemdUnitSafetyTests.GatewayUnitContent_CarriesTheStartLimitInTheUnitSection`
  did not fail in this run.

Nothing hung or crashed; the build had no warnings and no errors.

**Verdict: passes the gate under the standing rulings.**