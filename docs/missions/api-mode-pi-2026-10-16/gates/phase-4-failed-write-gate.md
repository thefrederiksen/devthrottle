# Phase 4 gate: a prompt whose write failed is refused where it failed (pull request #4192)

Run by the phase 4 gate seat on SOREN_NORTH (Windows), 18 October 2026.

- Commit tested: `1b91e3062`, a clean local merge of origin/main at `c1e0bd3ce` with the pull request branch
  `apimode/p4-requeue-after-failed-write` at `efc17e660`. Nothing was pushed from the gate. Main moved to
  `79674c0a1` while the seat waited; the tested merge is the one recorded here.
- Command: `.\scripts\test-local.ps1 -Parked` (Debug), with TEMP and TMP set for that process to
  `D:\ReposFred\gate-temp\4192`. Every suite ran all of its tests. One suite failed, so the seat ran
  `.\scripts\test-local.ps1 -RerunFailed D:\ReposFred\gate-temp\4192\cc-test-local-9da60309` once, with the same
  TEMP. The failure came back on the rerun.
- Time: 11 minutes 42 seconds for the run (2 minutes 40 seconds of that the build), and 15 seconds for the
  rerun. The seat waited about five hours before starting, from 23:00 on 17 October to 04:11 on 18 October,
  because this machine stayed under the 12 GB free memory floor the brief's limits forbid starting under; other
  gate runs were active for part of that wait.
- Run folders `D:\ReposFred\gate-temp\4192\cc-test-local-9da60309` (the run) and
  `D:\ReposFred\gate-temp\4192\cc-test-local-6adf5c2e` (the rerun), both kept by the script because the run was
  red. The script wrote the red run to its own record at
  `C:\Users\soren\AppData\Local\cc-director\test-gate\runs.jsonl` under the tested commit.

| Suite | Passed | Failed | Skipped |
|---|---|---|---|
| Core.UnitTests | 2,382 | 1 | 0 |
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
| Core.Tests | 4,540 | 0 | 0 |
| Gateway.UnitTests | 14,289 | 0 | 0 |

Totals: 25,916 passed, 1 failed, 3 skipped (the three skipped tests are Linux-only). Every suite except
Core.UnitTests reported outcome Completed in the TRX verdict; Core.UnitTests reported outcome Failed.

## The failure

`CcDirector.Core.UnitTests.ErrorReports.CatchLogLineScanTests.EveryLogLineInACatch_IsReportedOrSaysWhyNot`

Message:

```
1 line(s) written inside a catch would never reach the Gateway. Use the house form "[Class] Method FAILED: ...",
or, for an expected case, put "// not-an-error: <why>" on the line above:
src/CcDirector.Core/ApiMode/ApiModeSessionHost.cs:688: [ApiModeSessionHost] WriteCommand: session=x, x x not written: x
```

Rerun result: failed again, the same message. It is deterministic, not timing.

Cause, read from the diff and the scanner: the pull request changed the catch block in `TryWriteCommandLocked`
(`ApiModeSessionHost.cs`, around line 684). On main the opt-out comment is one line, directly above the log
call:

```
// not-an-error: Pi ended first; the end of the process, which follows, settles everything waiting
FileLog.Write($"[ApiModeSessionHost] WriteCommand: ... not written: {ex.Message}");
```

The pull request wrapped that comment onto two lines, so the line directly above the call now reads
`// settles everything else waiting` and carries no `not-an-error:` marker. The scanner's `OptOutOf` looks only
at the call's own line and the one line above it, and it requires the line above to be the comment alone. The
marker on the line two above is not seen. This is a defect introduced by the pull request, not a test that is
red on main: on main the marker is on the line directly above, which is what the scanner accepts.

The fix is one line in the pull request: keep the `// not-an-error: ...` comment on a single line directly
above the `FileLog.Write` call, or move the marker to the end of the call's own line.

Core.UnitTests is a touched suite: the pull request adds tests to it (`ApiModeSessionHostTests.cs`), so the
"untouched suite" ruling does not apply. The symbolic link ruling was not needed: no test failed with the
"cannot create a symbolic link" permission error. The red-on-main ruling does not apply: the failing line is the
pull request's own change.

The pull request's new tests in `ApiModeSessionHostTests.cs` ran inside Core.UnitTests and passed; the one
failure is the source scan above.

Nothing hung or crashed; the build had no warnings and no errors. The script's own verdict was FAILED, exit
code 1, on both the run and the rerun.

**Verdict: does not pass the gate.** One deterministic failure, introduced by the pull request, with a one-line
fix. The pull request needs a new commit and a new gate run.
