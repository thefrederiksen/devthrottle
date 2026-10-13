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
| `Load_EveryRunDueWhileTheGatewayWasDown_IsRecorded_InTheSameWriteThatMovesTheScheduleOn` | did not run: every time a schedule was due while the Gateway was down, one row each, in the write that moves the schedule on (replaced in round 2, see below) |
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
| After rebasing onto origin/main `51627d041`: `dotnet test src/CcDirector.Gateway.UnitTests` | 10,387 passed, 0 failed |
| After the rebase: `.\scripts\test-database.ps1` (throwaway PostgreSQL) | green: 68 executed, 68 passed, none skipped - the new Postgres migration applies |

## Revert proof

With the fix committed, two rules were broken on purpose and the tests run against a fresh build:
removing the "missed while the Gateway was down" record, and removing the clearing rule (a later ok run of
the same schedule). Exactly `Engine_ARunDueWhileTheGatewayWasDown_IsRecordedOnceAsDidNotRun` and
`Problems_TheNextRunOfTheSameScheduleReportsOk_ClearsIt` went red (2 failed, 36 passed); the source was then
restored and the 46 run-result tests passed again on a rebuild.

## What was NOT proved

- **Gateway.Tests (the parked integration suite) did not run.** Another session's run held the
  machine-wide Gateway test lock for the whole 45-minute wait (and still held it two hours later), and the
  run gave up with no tests. The PostgreSQL migration tests that used to live there now run through
  `scripts\test-database.ps1`, which was green.
- The machine's own Python has click 8.1.8 while the tool requires 8.2.1 or later; with it, about 1,660
  command-line tests fail on origin/main too. The counts above come from a scratch environment with the
  required versions.
- Nothing was tried against the live Gateway. No real schedule was used; the owner's schedules were not
  touched.

## Round 2: the review of pull request 3799 (11 October 2026)

The review found nine defects. Each fix has a test that fails without it; the table names the test and
the revert proof that showed it red. Rebased onto origin/main `4a80d7dff` before the gates below.

| Finding | Fix | Test that fails without it |
| --- | --- | --- |
| 1. A result was not one-way: a problem then ok erased the problem | A result is recorded once. The same result again answers with the stored one; a different one is refused (409). The store write is conditional on the state that was read, so a sweep and a report cannot overwrite each other | `Report_OkAfterTheRunReportedAProblem_IsRefused_AndTheProblemStands`, `Report_AProblemAfterTheRunReportedOk_IsRefused`, `Report_TheSameResultTwice_AnswersWithTheStoredOne_AndWritesNoSecondActivityRow`, `RecordResult_IsConditional_SoTheSweepCannotOverwriteAReportThatLandedFirst` |
| 2. The close followed the word asked for, not the stored outcome | `CloseSession` is read off the stored result: only an ok run closes. A late report on a run past its shift is added to the problem once and the session stays open | `Report_AfterTheRunWentPastItsShift_IsKeptButTheRunStaysAProblem` |
| 3. Missed-while-down only saw the System tenant | The record is no longer made by the engine from a list the System-tenant load built. A schedule reached late now records every time it skipped, inside the account's own sweep, before it advances; a due time already on record is not recorded twice | `EvaluateDue_MissedFireWhileDown_FiresAtMostOnce_NotPerInterval`, `Engine_AScheduleReachedLate_FiresOnce_AndRecordsEveryOtherTimeItWasDue`, `Engine_ADueTimeAlreadyOnRecord_IsNotRecordedTwice` |
| 4. An outage of several due times collapsed to one row | One did-not-run row per due time, bounded at 500 per schedule per pass; past that the last row says how many more were not listed | `Load_EveryRunDueWhileTheGatewayWasDown_IsRecorded_InTheSameWriteThatMovesTheScheduleOn` |
| 5. The activity write swallowed exceptions, so step 6 could silently miss a row | The run is marked as owing its row in the same write that records its result or resolve. A write that fails is logged, stays owed, and the next sweep writes it; each landed row is settled at once, so none is written twice | `AnActivityWriteThatFails_LeavesTheRowOwed_AndTheNextSweepWritesIt`, `AResolveWhoseCorrectingRowFails_IsWrittenByTheNextSweep`, `AFactoryLinkedRun_LeavesAnActivityRowForEveryResult_...` |
| 6. Missed runs taken at load were lost if the later write failed | No hand-off list any more: the did-not-run rows are added in the same database write that moves the schedule on, so both land or neither does | `Load_EveryRunDueWhileTheGatewayWasDown_IsRecorded_InTheSameWriteThatMovesTheScheduleOn` |
| 7. The 50-row history cap pruned open problems | The cap skips a row that still owes its activity row, and an unresolved problem with no later ok run | `AnOpenProblem_IsNeverPrunedByTheHistoryCap` (and `AProblemClearedByALaterOk_IsPrunedLikeAnyOtherOldRun` shows the cap still works) |
| 8. Byte-order marks in the four migration files | Stripped; the migrations were regenerated on top of main's AddSecretTransfers | checked by a byte scan of the changed files |
| 9. A trailing blank line in `cli.py` | Removed | `git diff --check origin/main` is clean |

### Revert proof, round 2

Each fix was broken on its own, with the fix committed, and the run-result and engine tests (73) were run
on a fresh build; the file was restored from git after each, and the tree was clean at the end.

| Fix broken | Went red |
| --- | --- |
| one-way: any stored result may be overwritten | `Report_AProblemAfterTheRunReportedOk_IsRefused`, `Report_OkAfterTheRunReportedAProblem_IsRefused_AndTheProblemStands`, `Report_TheSameResultTwice_...`, `Report_AfterTheRunWentPastItsShift_...` (4) |
| the conditional store write ignores what was read | `RecordResult_IsConditional_...` (1) |
| the close follows the word asked for | `Report_AfterTheRunWentPastItsShift_IsKeptButTheRunStaysAProblem` (1) |
| a late schedule does not record the times it skipped | `EvaluateDue_MissedFireWhileDown_FiresAtMostOnce_NotPerInterval`, `Engine_AScheduleReachedLate_...` (2) |
| the load records no missed runs | `Load_EveryRunDueWhileTheGatewayWasDown_...` (1) |
| the sweep writes no owed activity row | `AFactoryLinkedRun_LeavesAnActivityRowForEveryResult_...`, `AResolveWhoseCorrectingRowFails_...`, `AnActivityWriteThatFails_...` (3) |
| the cap keeps nothing | `AnOpenProblem_IsNeverPrunedByTheHistoryCap` (1) |
| a due time already on record is recorded again | `Engine_ADueTimeAlreadyOnRecord_IsNotRecordedTwice` (1) |

### Gates, round 2

| Command | Result |
| --- | --- |
| `dotnet ef migrations has-pending-model-changes` (SQLite and Postgres) | no changes |
| `git diff --check origin/main` | clean |
| `.\scripts\test-local.ps1` | 8 suites green; 2 red, each with one failure that main introduced and this branch does not touch (below) |
| `dotnet test src/CcDirector.Gateway.UnitTests` | 10,485 passed, 2 failed; the 2 (`HeldDeliveryReachabilityTests`) pass on their own, 19 of 19 - a load-sensitive pair, not cron code |
| `.\scripts\test-database.ps1` (throwaway PostgreSQL) | 67 passed, 1 failed - main's (below); the new migration applies |
| `pytest` in `tools/cc-devthrottle`, scratch environment with click 8.2.1 or later | 3,907 passed, 3 skipped, 0 failed |

Failures on main, not from this change (none of these files is in this branch's diff):

- `BackgroundWorkRegisterTests.EveryTimerWatcherAndWaitingLoop_...` (Core.UnitTests):
  `src/CcDirector.ControlApi/LinkRequestsCache.cs` has a recurring mechanism with no row in
  `docs/BackgroundWork.md`. Added by `4a80d7dff` (#3853).
- `ShownErrorSourceScanTests.EveryErrorShownOnTheDesktop_...` (Avalonia.Tests):
  `src/CcDirector.Avalonia/LinkAskFlyout.cs:74` shows `ex.Message` without reporting it. Same commit.
- `PostgresProviderProofTests.Collation_ExplicitC_OnExactlyTheDeclaredNaturalKeys_OnRealPostgres`:
  `secret_transfers.State` and `secret_transfers.TransferId` carry the C collation but are not on the
  test's list. Added by main's AddSecretTransfers migration.

## For the factory email: as-of, raised time and finished sentences (11 October 2026)

Asked by the email design (draft pull request 3783, `docs/qa/factory-control/morning-email/DESIGN.md`). The
problems answer now carries `asOfUtc`, and `GET /cron/problems?asOf=<UTC ISO 8601>` asks for an earlier instant:
a problem raised after it is left out, and a clear or resolve after it has not happened yet, so the 16:00 and
midnight checks read the state as it stood when their shift ended. An instant it cannot read is refused (400).
Each problem carries `raisedAtUtc` (renamed from `recordedUtc`, which nothing outside the fold read), and three
finished pieces of text the email and the factory page show as they are: `sentence` ("Did not run: its machine
was offline when it was due."), `whenText` ("Started Sat 10 Oct 02:00" or "Due Sun 11 Oct 02:00", in the
account's time zone) and `openForText` ("open 29 hours", null once closed). The per-factory 24-hour counts are
NOT here; the email work adds them.

| Test | What it shows |
| --- | --- |
| `Problems_CarryTheFinishedWords_AndWhenEachProblemWasRaised` | as-of, raised time, sentence, when and how long open |
| `Problems_AsOfAnEarlierInstant_ListThemAsTheyStoodThen` | open at midnight before the ok run that cleared it; nothing before it was raised |
| `Problems_AsOfAnInstantBeforeTheResolve_ListItAsOpen` | a resolve after the instant has not happened yet |
| `Problems_ARunThatNeverStarted_SaysWhenItWasDue` | "Due ..." and the did-not-run sentence |
| `SentenceOf_...`, `OpenFor_...`, `Problems_AnAsOfThatIsNotUtc_IsRefused` | the words, and a non-UTC instant refused |
| `CronRunResultEndpointsTests.Problems_AsOf_...` | the route's `asOf`, and its 400 |

Revert proof: dropping the raised-after filter turned 2 tests red, counting a later resolve turned 1 red,
and leaving the reason bare as the sentence turned 2 red; restored, the 88 run-result and engine tests pass.

## Round 3: the second review (11 October 2026)

Rebased onto origin/main `0e1f32430` (#3788). The branch was squashed to one commit before the rebase so the
conflict with main's `license` command group was resolved once (both groups kept).

| Finding | Fix | Test that fails without it |
| --- | --- | --- |
| 1. An owed row was dropped when its schedule was deleted before the sweep | The run keeps its factory, seat and schedule name, fixed on the run in the same write that records its first result (or by the engine, for a run recorded with its result). The row is built from the run, never from the schedule | `AnOwedRow_IsStillWrittenToItsFactory_AfterItsScheduleIsDeleted` |
| 2. A failure after the row committed but before it was noted wrote it twice | The row and the run's note of it are now ONE database write (`CronRunHistoryStore.WriteActivityRow`): they land together or not at all, and a run whose row is noted answers with it. The activity record's keys stay Gateway-minted (`GatewayMintedKeyEntity`), so a caller-fixed id was not an option | `AWriteThatStopsAfterItsRowCommitted_IsNotWrittenTwice` |
| 3. The overflow past the 500-row limit was only words, and the limit did not count the first missed time | The last row stores the overflow as a number (`MissedNotListed`); each problem has `occurrences` and the list has the total. The limit now counts the first missed time, so an outage writes at most 500 rows | `Load_AnOutageLongerThanTheLimit_RecordsTheLimit_AndCountsTheRestAsANumber`, `Engine_AScheduleReachedFarTooLate_RecordsTheLimit_AndCountsTheRestAsANumber` |

Revert proof, each fix broken alone on a fresh build (92 run-result and engine tests): building the row from
the schedule turned finding 1's test red; leaving the row un-noted in its write turned 5 red, finding 2's test
among them; not storing the count turned both overflow tests red; letting the limit exclude the first time
turned the load test red. The tree was clean after each restore.

Gates: `dotnet test src/CcDirector.Gateway.UnitTests` 10,536 passed, 0 failed; `pytest` in
`tools/cc-devthrottle` 4,171 passed, 3 skipped; `has-pending-model-changes` clean for SQLite and Postgres
(the AddCronRunResults migration was regenerated, 14 columns and 3 indexes); `git diff --check` clean;
`.\scripts\test-database.ps1` 67 of 68, the one failure being main's `secret_transfers` collation, unchanged.

## Round 4: the third review (11 October 2026)

| Finding | Fix | Test that fails without it |
| --- | --- | --- |
| 1. A late ok, sent before the sweep, closed a run that ran past its shift | A report on a pending run whose shift has ended records "ran past its shift", with the report added - the same answer as when the sweep got there first. The session stays open | `Report_OkAfterTheShiftEnded_BeforeTheSweep_IsRecordedAsRanPastItsShift` (and `Report_OkBeforeTheShiftEnds_IsOk`) |
| 2. The factory link was taken at the first result, so a schedule deleted mid-run lost the row | The engine fixes the schedule's factory, seat and name on every run it creates; the result write no longer takes a link at all | `ARunStartedByTheEngine_StillOwesItsFactoryRow_WhenItsScheduleIsDeletedBeforeItReports` |
| 3. Problem filtering and resolve rights read the schedule's current factory | Both read the run's own factory. The resolve route no longer reads the schedule at all | `AProblem_StaysWithItsRunsFactory_WhenTheScheduleIsMovedOrDeleted`, `Resolve_StillNeedsTheRunsFactory_AfterItsScheduleIsDeletedOrMoved` |
| 4. A late report reset the raised time | Only the first result sets `ResultUtc`; a later write that adds to a problem keeps it | `Problems_AsOfBeforeALateReport_StillListTheProblem_RaisedWhenTheSweepFoundIt` |

Revert proof, each fix broken alone on a fresh build (98 run-result and engine tests): 1 red, 2 red, 2 red,
2 red and 1 red respectively, each including that finding's test; the tree was clean after each restore.
Gates: `dotnet test src/CcDirector.Gateway.UnitTests` 10,542 passed, 0 failed. No schema or command-line
change in this round, so the database and Python suites were not re-run.

## After the review passed: rebased onto main's new migration (12 October 2026)

The fourth review passed on head `4cf928063`. Main then added the AddLicenseSwitching migration, so the branch
was rebased onto origin/main `8610c8fb2` and the AddCronRunResults migration regenerated on top of it for both
providers (still 14 columns and 3 indexes on `cron_runs`, nothing else). No product code changed. The migration
chain test now lists main's migration before this one, and its count is 163: main's 128, the license migration's
18, and this one's 17.

Gates on the rebased head: `has-pending-model-changes` clean for SQLite and Postgres;
`dotnet test src/CcDirector.Gateway.UnitTests` 10,657 passed, 0 failed; `pytest` in `tools/cc-devthrottle`
4,171 passed, 3 skipped; `.\scripts\test-database.ps1` GREEN, 68 executed, none failed, none skipped (main's
`secret_transfers` collation failure is fixed).
