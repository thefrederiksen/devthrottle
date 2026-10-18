# Phase 4 gate: take the wheel and hand back, the Director side (pull request #4094)

Run by the phase 4 gate seat on SOREN_NORTH (Windows), 17 October 2026 (evening, local time).

- Commit tested: `1ac2199a3`, a clean local merge of origin/main at `142bcbff1` with the pull request branch
  `apimode/p4-wheel` at `6e52d03c3`. Nothing was pushed from the gate.
- Which branches were merged: the brief said to merge `apimode/p4-prompt-text` (pull request #4121) first if it
  was not yet on main. It was: #4121 merged at 01:52 UTC on 18 October as `142bcbff1`, which is the origin/main the
  gate started from. So only `apimode/p4-wheel` was merged, onto a main that already held #4121.
- Command: `.\scripts\test-local.ps1 -Parked` (Debug), with TEMP and TMP set for that process to
  `D:\ReposFred\gate-temp\4094`. No rerun was possible: nothing ran.
- Time: 3 minutes 20 seconds, all of it the build. The seat checked the brief's limits before starting: 13.8 GB
  of memory free, 398 GB free on D:, one other gate run active (worktree `devthrottle-gate-4179`), Docker up.
- Run folder `D:\ReposFred\gate-temp\4094\cc-test-local-1208e680`; its `run.json` records verdict `BUILD FAILED`,
  exit code 1, an empty suite list.

| Suite | Passed | Failed | Skipped |
|---|---|---|---|
| (none) | 0 | 0 | 0 |

Totals: 0 passed, 0 failed, 0 skipped. **The merged tree does not compile, so no suite ran.**

The build errors (two, both `CS7036`, no warnings):

1. `src\CcDirector.Core.UnitTests\Sessions\ApiModeSessionJournalRowTests.cs(37,27)`: "There is no argument given
   that corresponds to the required parameter 'clock' of
   `ApiModeSessionBackend.ApiModeSessionBackend(string, string, bool, SessionEventStream, Func<IApiModeProcess>,
   Func<ISessionBackend>, TimeProvider)`".
2. `src\CcDirector.Gateway.UnitTests\ApiMode\ApiModeSessionSurvivesACrashTests.cs(46,27)`: the same error, same
   constructor.

Why the merge breaks when neither side does, read from the three branches rather than guessed:

- The wheel branch was cut from `1bbd9bd12`. Since then main gained three commits: #4091 (`91f1b38ca`, the crash
  journal, merged 01:35 UTC on 18 October), its gate record #4188, and #4121.
- #4091 added the two test files above. Both construct `ApiModeSessionBackend` with SIX arguments, which is the
  constructor main has (`sessionId, agentSessionId, resuming, stream, processFactory, clock`). Every caller on
  main passes six; main is consistent with itself.
- The wheel branch widens the constructor to SEVEN arguments, inserting `Func<ISessionBackend> terminalFactory`
  before `clock`, and updates every caller it knows about (`ApiModeSessionTests`, `ApiModeWheelTests`,
  `SessionManager`, `ApiModeConnectionThroughTheDirectorTests`). The wheel branch is consistent with itself. It does
  not contain #4091 (`git merge-base --is-ancestor` says no), so it has never seen the two new callers.
- Git merges the two cleanly because no file is touched by both. The result has a seven-argument constructor and
  two six-argument callers. That is the whole failure.

What the pull request needs before it can land: merge origin/main (or rebase onto it) and pass a terminal factory
in the two callers that #4091 added, the same way `ApiModeConnectionThroughTheDirectorTests` does on the wheel
branch. Then gate again. The fix is two lines of test code, but it has to be made on the pull request branch, not
in the gate: the gate pushes nothing.

None of the three standing rulings applies: there was no test failure to rerun, no "cannot create a symbolic
link" error, and no test red on main, because no test ran.

**Verdict: FAILS the gate. The pull request as it stands would break the build on main.**
