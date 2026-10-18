# Phase 4 gate: take the wheel and hand back, the Director side (pull request #4094)

Run by the phase 4 gate seat on SOREN_NORTH (Windows), 17 October 2026 (late evening, local time; 03:17 to 03:42 UTC
on 18 October). This is the second run. The first run is kept as a short note at the end.

- Commit tested: `76cc43a65`, a clean local merge of origin/main at `58e563ba0` (#4180) with the pull request branch
  `apimode/p4-wheel` at `5122ea878`. The merge touched no file on both sides and left a clean tree. Nothing was
  pushed from the gate.
- Command: `.\scripts\test-local.ps1 -Parked` (Debug, suite filter `Gate!=Quarantine`), with TEMP and TMP set for
  that process only to `D:\ReposFred\gate-temp\4094-r2`. Rerun: `.\scripts\test-local.ps1 -RerunFailed
  D:\ReposFred\gate-temp\4094-r2\cc-test-local-8d484712`, same TEMP, once.
- Limits checked before starting: free memory stayed between 4.9 GB and 11.2 GB for 27 minutes while two other gate
  runs (worktrees `devthrottle-gate-4179` and `devthrottle-gate-4191`) were active, so the seat waited. It started
  at 12.1 GB free with one other run active (`devthrottle-gate-4179`), 392 GB free on D:, Docker up (29.6.1).
- Time: 21 minutes 59 seconds for the full run (4 minutes 5 seconds of it the build), plus 37 seconds for the rerun.
- Run folders: `D:\ReposFred\gate-temp\4094-r2\cc-test-local-8d484712` (verdict `FAILED`, exit code 1) and
  `D:\ReposFred\gate-temp\4094-r2\cc-test-local-7a67f2f8` (the rerun, verdict `FAILED`, exit code 1). Both kept.

| Suite | Passed | Failed | Skipped |
|---|---|---|---|
| CcDirector.Core.UnitTests | 2408 | 1 | 0 |
| CcDirector.Avalonia.Tests | 1093 | 0 | 0 |
| CcDirector.Engine.Tests | 85 | 0 | 0 |
| CcDirector.HostedAgent.Tests | 85 | 0 | 0 |
| CcDirector.Launcher.Tests | 230 | 0 | 0 |
| CcDirector.Terminal.Avalonia.Tests | 71 | 0 | 0 |
| CcDirector.Reclaim.Tests | 305 | 0 | 0 |
| CcDirector.DeliveryQualification.Tests | 22 | 0 | 0 |
| cc-director-setup.Tests | 39 | 0 | 0 |
| cc-director-setup-engine.Tests | 924 | 0 | 0 |
| cc-director-setup-cli.Tests | 74 | 0 | 0 |
| CcDirector.Gateway.Tests shard 1 of 4 | 412 | 0 | 0 |
| CcDirector.Gateway.Tests shard 2 of 4 | 480 | 0 | 0 |
| CcDirector.Gateway.Tests shard 3 of 4 | 418 | 0 | 3 |
| CcDirector.Gateway.Tests shard 4 of 4 | 469 | 0 | 0 |
| CcDirector.Core.Tests | 4540 | 0 | 0 |
| CcDirector.Gateway.UnitTests | 14277 | 0 | 0 |

Totals: 25,432 passed, 1 failed, 3 skipped, across 17 suite runs. The build compiled with no warnings and no errors,
so the first run's defect (two six-argument callers against a seven-argument constructor) is fixed on the branch.

The three skipped tests are the standing Linux-only cases in `SessionFileAccessTests` (shard 3 of 4), skipped on
Windows by design: `PasswordFileHome_onLinux_isTheHomeInThePasswordFileForThisUser`,
`Decide_plainPosixPathNamedInProse_isAllowed_andItsNeighbourIsNot`, `Decide_plainPosixPathToASecret_isStillRefused`.

## The one failure

`CcDirector.Core.UnitTests.ErrorReports.CatchLogLineScanTests.EveryLogLineInACatch_IsReportedOrSaysWhyNot`, 2.2 seconds.

Message:

    1 line(s) written inside a catch would never reach the Gateway. Use the house form "[Class] Method FAILED: ...",
    or, for an expected case, put "// not-an-error: <why>" on the line above:
    src/CcDirector.Core/ApiMode/ApiModeSessionHost.cs:398: [ApiModeSessionHost] SuspendAsync: session=x, Pi did not
    confirm its abort within xms; its process is closed under the turn

Rerun result: **failed again**, same test, same message. The run script's own words: "FAILED AGAIN - these tests
failed on the release gate AND on the re-run of the same commit. Treat them as code defects."

Where the line comes from, read from the branches rather than guessed:

- Line 398 of `ApiModeSessionHost.cs` is a `FileLog.Write` inside the `catch (OperationCanceledException)` that the
  pull request adds to `SuspendAsync` (the bounded wait for Pi to confirm its abort). The text "Pi did not confirm
  its abort" does not exist anywhere on origin/main; it exists only on `apimode/p4-wheel`. The pull request changes
  `ApiModeSessionHost.cs` (208 lines) and does not touch the scanner test.
- The scanner is a source scan with the C# parser, so it is deterministic: it reports every offending line in one
  message, and it listed exactly one, the pull request's. Every other catch-block log line in the merged tree,
  including all of main's own, is either recognised as an error or carries a `// not-an-error:` reason. So this
  test is not red on main; it is red because of this pull request.
- The scanner landed on main on 10 October (#3763) and the pull request branch carries it (it merged origin/main
  twice, last at `2efbca486`). So the pull request's own tree is red on this test as well, not only the merge.

What the pull request needs before it can land, one of two one-line edits on the pull request branch (the gate
pushes nothing):

- If a Pi that never confirms its abort is a defect worth reporting, write the line in the house form, for example
  `[ApiModeSessionHost] SuspendAsync FAILED: Pi did not confirm its abort within {grace.TotalMilliseconds}ms ...`.
- If it is an expected case (the grace period is a designed bound and the process is closed on purpose), put
  `// not-an-error: <why>` on the line above, with a real reason of at least ten characters, the way the same
  file already does at its line 673 ("Pi ended first; the end of the process, which follows, settles everything
  waiting").

Then gate again.

Rulings: the "cannot create a symbolic link" ruling does not apply (no such error). The "untouched suite, passes on
rerun" ruling does not apply: the suite is touched by the pull request and the test failed on the rerun. The "red on
main" ruling does not apply: the flagged line is not on main.

**Verdict: FAILS the gate. One deterministic test failure, caused by a log line the pull request adds.**

## Note: the first run (17 October 2026, evening)

The first run tested `1ac2199a3`, a merge of origin/main at `142bcbff1` with `apimode/p4-wheel` at `6e52d03c3`. It
did not compile: two `CS7036` errors, because #4091 had added two test callers of `ApiModeSessionBackend` with six
arguments (`ApiModeSessionJournalRowTests.cs:37`, `ApiModeSessionSurvivesACrashTests.cs:46`) while the wheel branch
widened the constructor to seven. No suite ran; 3 minutes 20 seconds, all build. Verdict then: FAILS, the merge does
not build. The branch's commit `5122ea878` ("the tests from main pass a terminal factory") fixed that, which is why
this second run was asked for.