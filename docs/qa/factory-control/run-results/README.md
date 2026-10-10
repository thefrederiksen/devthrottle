# Factory Control, step 1 (and step 6) - every run says how it went

Proof for the change that adds `cc-devthrottle run result | resolve | problems`, the Gateway-recorded
problems, the shifts, and a factory activity row for every result. Run on SOREN_NORTH, 10 October 2026,
on branch `factory-control/runresult` cut from origin/main at `fe2aaccc3`.

## What each test proves, in plain words

Gateway, `src/CcDirector.Gateway.UnitTests/CronRunResultTests.cs`:

| Test | What it shows |
| --- | --- |
| `Report_Ok_IsRecordedOnTheRun_AndTellsTheSessionToCloseItself` | ok is stored on the run and the answer tells the session to close |
| `Report_Problem_IsRecordedWithItsReason_AndTheSessionStaysOpen` | a problem is stored with its reason and the session is told to stay open |
| `Report_AProblemWithoutOneLineWhy_IsRefusedAndNothingIsRecorded` | no reason, a blank one, or two lines: refused, nothing stored |
| `Report_FromASessionNoScheduleStarted_IsRefused_NotASilentSuccess` | a session no schedule started gets a 404 refusal |
| `Sweep_ASessionThatEndedWithoutReporting_IsRecordedAsDidNotReport` | did not report |
| `Sweep_AnOpenRunPastTheEndOfItsShift_IsRecordedAsRanPastItsShift` | ran past its shift across the 16:00 boundary in Toronto |
| `Sweep_OnTheSpringForwardDay_TheNightShiftEndsAtEightLocalTime_NotEightHoursAfterMidnight` | the same on 8 March 2026, a seven-hour night |
| `ShiftOf_TheNightOfTheSpringForwardDay_IsSevenHoursLong`, `ShiftOf_TheNightOfTheFallBackDay_IsNineHoursLong` | the shift helper on both daylight-saving days |
| `Engine_AFireThatStartsNoSession_IsRecordedAsDidNotRun_AndToldToTheFactory` | did not run: a fire that started nothing |
| `Engine_ARunDueWhileTheGatewayWasDown_IsRecordedOnceAsDidNotRun` | did not run: due while the Gateway was down, recorded once |
| `CronEngineTests.EvaluateDue_AWindowRunMissedPastItsDeadline_...` | did not run: reached after its deadline (this test's expectation changed on purpose) |
| `Problems_AProblemFromLastNight_IsStillOpenTheNextMorning` | a problem still open the next morning, with its shift and session |
| `Problems_TheNextRunOfTheSameScheduleReportsOk_ClearsIt` | cleared by the next ok run of the same schedule, not by another schedule's |
| `Resolve_WithAReason_ClosesTheProblem_AndKeepsTheReasonAndWhoDidIt` | cleared by resolve, reason and resolver kept |
| `Resolve_WithNoReason_IsRefusedAndTheProblemStaysOpen` | resolve with no reason refused |
| `AFactoryLinkedRun_LeavesAnActivityRowForEveryResult_...` | step 6: a failed row for the problem, a done row for the next ok, same seat and subject |
| `Resolve_AFactoryProblem_WritesTheRowThatMarksItsFailureHandled` | resolving writes the correcting row for the failed row |
| `Engine_AFactoryRunThatDidNotRun_LeavesAFailedActivityRow` | step 6 for a run that never started |
| `ARunRecordedBeforeResultsExisted_IsUntracked_AndNeverSweptIntoAProblem` | history from before this change is not condemned |

Routes, `src/CcDirector.Gateway.UnitTests/Api/CronRunResultEndpointsTests.cs`: the run is read off the
calling session's key; a person and a session of the factory may resolve, a session outside it is
refused (403); an unknown problem state is refused. `SessionKeyGuardTests` allows exactly the three new
shapes and refuses their neighbours.

Command line, `tools/cc-devthrottle/tests/test_run_ops.py` (16 tests): ok closes the session through
the same request `session done` makes, a problem does not; a problem with no reason is refused before
anything is sent; a non-scheduled session's refusal is shown and the session is not closed; `run resolve`
sends its reason; `run problems` shows every field, prints `count: 0` when empty, passes `--factory` and
`--all` to the Gateway including with `--json`, never reads an answer with no list as none, and fails on an
unknown flag.

## Commands and counts

| Command | Result |
| --- | --- |
| `.\scripts\test-local.ps1` | green: 10 suites, 4,101 tests, every outcome Completed |
| `.\scripts\test-local.ps1 -Parked` | Gateway.UnitTests 10,400 passed (13 skipped); Core.Tests 4,920 passed, 1 failed (see below); Gateway.Tests did not run (see below) |
| `dotnet test src/CcDirector.Core.Tests --filter TerminalThroughputTests` | 4 passed - the one Core.Tests failure is a timing test that passes on its own |
| `pytest tests` in `tools/cc-devthrottle`, with click 8.5 | 3,892 passed, 2 failed before the help fix; after it, the only failure left is `errors website-token`'s over-long summary, which fails identically on origin/main |
| `dotnet ef migrations has-pending-model-changes` (SQLite and Postgres) | no changes |

## What was NOT proved

- **Gateway.Tests (the parked integration suite) did not run.** Another session's run held the
  machine-wide Gateway test lock for the whole 45-minute wait, and the run gave up with no tests. That
  suite holds the Postgres migration-chain tests, which were updated for the new migration but not run.
- The machine's own Python has click 8.1.8 while the tool requires 8.2.1 or later; with it, about 1,660
  command-line tests fail on origin/main too. The counts above come from a scratch environment with the
  required versions.
- Nothing was tried against the live Gateway. No real schedule was used; the owner's schedules were not
  touched.
