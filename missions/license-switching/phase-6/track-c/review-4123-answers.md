# Answers to the review of pull request 4123

The review is `review-4123.md` (head reviewed 698c4e994; the branch was since rebased onto main as 215cd9940, with
no change to what was reviewed). Answered by the Developer who built it. All six are accepted and fixed in
bad46adb6, 20d572b1f, b140fd785 and e38673081.

## 1. HIGH - an approval or a sign-in waits behind a running check with no deadline, and then runs anyway

**Accepted and fixed.** The reviewer's arithmetic is right, and the fix bounds the wait by more than the request's
own deadline, because the deadline alone is not enough: an approval that waited until just before its `NotAfterUtc`
and then ran `license approve` for up to 150 seconds would still press Authorize after the Gateway stopped
listening.

- **The lane's wait has a limit.** `LicenseBrowserLane.EnterWorkAsync(what, longestWait, ct)` now answers null when
  a check is still running at the end of `longestWait`, and the caller then starts nothing. The limit's timer goes
  through a delay seam (the `signInDelay` that `LicenseSwitching` already passes in for the approval wait), so a test
  ends it by hand and no test waits on a clock.
- **An approval may start only while the Gateway is still waiting for it.**
  `LicenseApprover.ToolStartAllowance = ApproveCommandTimeout - ApproveToolTimeout`, 30 seconds: the tool must start
  by the sooner of arrival plus that and the request's `NotAfterUtc`. The lane wait is bounded by it, and the time is
  looked at AGAIN once in the lane. Past it the answer is refused `expired` with its own sentence
  (`LicenseApprover.TooLateMessage`), and `license approve` never runs.
- **A sign-in the Gateway asked for, likewise.**
  `LicenseSignInCoordinator.StartAllowance = SignInCommandTimeout - SignInApprovalWait - SignInFinishAllowance`,
  30 seconds, so a sign-in never starts so late that it could succeed after the Gateway recorded a timeout. The
  daily renewal, which nobody but the renewal waits for, may start until its own deadline. Too late is `expired`,
  before any tool runs (no `HasBrowserProfileAsync`, no `claude auth login`).

I did not take the better fix, where arriving work cancels the running check: as the review says, the Director ends
the tool by killing its process, which skips the Python cleanup that cancels the throwaway login and puts the
profile back. That needs a graceful stop first, and with finding 2 fixed, work waits for at most one check. Thirty
seconds is short against a check of two to seven minutes, so an approval that meets a running check will usually be
refused `expired` and the sign-in tried again later. That is the honest answer; the alternative was pressing
Authorize for nobody. Ending the check early for arriving work is a follow-up for the Tech Lead to decide on.

Tests, all with the fake clock and the hand-driven delay:
- `Approve_DuringACheck_ThatOutlastsTheTimeToApproveInTime_IsRefusedExpired_AndPressesNothing` - the wait asked for
  is exactly 30 seconds; the time passes and the check is still running: refused `expired`, no `license approve`.
- `Approve_DuringACheck_ThatEndsOnlyAfterTheTimeRanOut_IsRefusedExpired_AndPressesNothing` - the check ends before
  the timer fires but after the time ran out: the second look refuses it.
- `Approve_DuringACheck_ThatEndsInTime_Approves` - 29 seconds: approved, the tool ran once.
- `SignIn_TheGatewayAskedFor_DuringACheck_ThatOutlastsItsTime_FailsExpired_AndStartsNoLogin`.
- Lane: `EnterWork_WhileACheckRuns_GivesUpAfterItsLongestWait_AndTakesNothing`,
  `EnterWork_WhileACheckRuns_WithNoTimeLeft_GivesUpAtOnce`, `EnterWork_NoCheckRunning_EntersAtOnce_AndStartsNoTimer`.

## 2. MEDIUM - between rows the check re-takes the lane before waiting work can

**Accepted and fixed.** The lane counts work that is waiting (`WaitingWork`), and `EnterCheckAsync` does not start a
check while it is above zero. So a run of many checks lets an approval in after the one check running now. Work that
gives up or is cancelled stops counting and wakes the waiting check. The design's sentence is now what the code
guarantees, and the class comment says so.

Tests: `TickAsync_AnApprovalArrivesDuringARunOfTwoChecks_GoesInBetweenThem_NotAfterBoth` (the second check is left
ungated, so without the fix it runs to the end before the approval gets in, and the test fails rather than hangs);
lane `EnterCheck_WhileWorkIsWaiting_LetsThatWorkGoFirst` and
`EnterWork_CancelledWhileWaiting_NoLongerHoldsTheNextCheckBack`.

## 3. LOW to MEDIUM - the next run is keyed to when the report was taken

**Accepted and fixed.** `_lastRunUtc` is now the moment that run's checks STARTED, kept with the unsent report, not
the moment the Gateway took it. That covers both cases in the review: a report delayed by the tunnel, and a long run
pushing its own first rows past the window. Test:
`TickAsync_AReportTakenLate_TheNextChecksRunADayAfterTheyStarted_NotADayAfterTheReport`, the reviewer's sequence:
checked at T, report taken at T + 23 h, checks again at T + 24 h.

## 4. LOW - tab characters in the proof file's gate commands

**Accepted and fixed.** Both lines now read `.\scripts\test-local.ps1`; the file has no tab characters left.

## 5. LOW - no test that `Start` schedules the check and that off never does

**Accepted and fixed.** `Start_LicenseSwitchingTurnedOff_NeverSchedulesTheRenewal` asserts
`ApproverCheck.IsScheduled` is false, and the turned-on test, renamed
`Start_LicenseSwitchingTurnedOn_SchedulesTheReporterTheRenewalAndTheApproverCheck`, asserts it is true.

## 6. LOW - the display-name shape is a second copy of the Gateway's

**Accepted and fixed.** The shape is now `LicenseApproverContract.BrowserProfileShape` in the Contracts project. The
Gateway's `LicenseApproverStore.BrowserProfileShape` is that same object, and the Director's check reads it, so the
two cannot drift. This touches C1's contract file by one member; Tech Lead, please say if you want it done
differently.

## Observations

- A profile that is both assigned and offered being checked twice: left as the brief asked, for the Tech Lead.
- A run holding a background slot: noted, unchanged.

## Proof that the new tests catch what they are for

With the fix committed, three changes were put back by hand and the Director's license tests rerun with a hang
guard: the waiting-work rule in `EnterCheckAsync`, `_lastRunUtc` from the report, and the approver's second look at
the time. Exactly four tests went red, all 220 ran and none hung: the two-check run, the late report, the second-look
approval, and the lane's waiting-work test. With the source restored: 220 passed.

An earlier attempt at this hung, because the lane test then awaited the approval alone, and a check that won the
lane made it wait for ever. The test now asks which one got in first, so it fails instead (20d572b1f).

## Gate

At e38673081, tree clean: `.\scripts\test-local.ps1` GREEN, and `.\scripts\test-local.ps1 -Parked` with TEMP and TMP
on D: GREEN, every suite Completed with every test executed except three that `CcDirector.Gateway.Tests` shard 3
reported skipped (I did not look into why). `CcDirector.Gateway.UnitTests` 13,986, `CcDirector.Gateway.Tests` 1,775
executed of 1,778, `CcDirector.Core.Tests`
4,538, `CcDirector.Core.UnitTests` 2,213. The Reclaim short-name test passed this time
(a238f7493 on main builds its tree on the system volume).
