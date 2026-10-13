# Answers to the pull request 3923 re-review

The re-review is `3923-re-review.md`: approved, with four new findings. The Tech Lead ruled on all four: fix 1, add
the three tests of 2, fix 3, fix 4. origin/main was merged in first (pull request 3925, the Licenses page reads); the
shared files - `LicenseSnapshotSource.cs`, `LicenseUsageHistory.cs`, `LicenseUsageIngest.cs` and their tests - are
main's, with no difference.

The breaks below were made in the worktree one at a time and restored; the counts are of the 396 license unit tests
(the Licenses page's tests came in with main).

**1. Should fix - one Director that does not answer still cost every tenant its next minute. Fixed, by the ruling.**
The host no longer guards the whole pass. `GatewayHost.CheckLicenseSwitches` starts a pass every minute whether or not
the last one has finished, and the overlap is left to the per-tenant guard in `LicenseSwitchService.TickAsync`, which
skips, and logs, only the tenant whose own check is still waiting. A pass ends when its sends do, and a send ends after the
60-second command wait at the latest, so passes do not pile up.
Test: `TickEveryTenant_WhileOneTenantsDirectorHasNotAnswered_ASecondPassStillChecksAndSwitchesTheOthers` - tenant one's
Director has not answered, tenant two reaches the threshold a minute later, a second pass is started, and it finishes
without waiting: tenant one skipped, tenant two switched.
What this test does and does not prove: it holds down the service behaviour the host now relies on. The change itself
is the removal of the host's guard, which is a timer callback and is read, not tested - the test Director in the
hosted tests answers at once and cannot be made to hold an answer. No single break in the service turns this test
red: with each tenant awaited before the next (the round 1 break) it still passes, because tenant one is skipped at
once; that break is caught by the round 1 test, 1 red.
The proof's sentence is corrected: nobody else is held up, inside a check or across checks.

**2. Should fix, small - three things no committed test held. Three tests added.**
- A tenant cannot overlap itself: `NoTarget_WhileTheMailServiceHasNotAnswered_ASecondCheckOfTheSameTenantSendsNoSecondEmail`.
  Break: the guard never skips - 1 red, that test.
- The email wait is forgotten when the email goes and when the event ends:
  `NoTarget_TheWaitIsForgotten_SoANewEventStartsAgainAtOneMinute`, two cases (the event ends while its email waits; the
  email went before the event ended). Break: nothing is forgotten - 2 red, both cases.
- The wait stops growing at an hour: `NoTarget_TheWaitBetweenTriesStopsGrowingAtAnHour`, 200 checks of an account
  whose week is used up, tries at minutes 0, 1, 3, 7, 15, 31, 63, 123 and 183. Break: the limit raised to ten hours -
  1 red, that test.

**3. Note - a fresh answer from a Director whose clock is behind was not taken. Fixed, by the ruling.**
`LicenseStore.ApplySwitchReadBack` takes the Gateway's own send time and no longer reads the Director's clock at all.
A fresh answer is taken unless the machine has reported since this Gateway sent the command; a replayed answer, as
before, unless the machine has reported since the row was written. Both rules are the same conditional update with a
different Gateway time. The measured clock skew is no longer part of this decision.
Tests: `ApplySwitchReadBack_AFreshAnswer_IsJudgedByTheGatewaysSendTime_NotTheDirectorsClock` (the Director 90 seconds
behind, and 90 ahead), `ApplySwitchReadBack_NoReportSinceTheCommandWasSent_TakesTheAccountOnMachineAndHoldings`,
`ApplySwitchReadBack_TheMachineReportedSinceTheCommandWasSent_IsRefused`, and through the whole check
`AFreshAnswer_FromADirectorWhoseClockIsBehind_StillSetsTheMachinesAccount` (the reviewer's numbers: 90 seconds behind,
the report 20 seconds before the check). Two store tests that held the old rule are replaced by these. The PostgreSQL
test calls the new signature.
Break: a fresh answer judged by the Director's finish time again - 3 red: the 90-seconds-behind case, the
reported-since case, and the whole-check test.

**4. Note - the one send after expiry was counted when planned. Fixed, by the ruling.**
The plan only asks whether the one send has been used. It is counted in `RecordResult`, when the command has gone out
and brought no answer (a timeout or a dropped tunnel). A plan that fails, or a Director that is not connected, uses
nothing. Every result that ends the row removes the entry, so none is left behind after a Director error.
Test: `Restart_AfterNotAfter_ASendThatNeverLeftIsNotTheOneSend_TheDirectorIsStillAskedOnce` - past the expiry the
Director is not connected, then it is back and is asked once, and only after that unanswered ask is the row settled
from the machine's report.
Break: counted when planned - 1 red, that test.

## What else changed

- Two of the Licenses page's tests on main (`LicenseEndpointsTests`, the log paging) wrote five open switches for one
  machine to fill the log. This branch's store refuses a second open switch for a machine (contract section 5.4), so
  those tests now end each switch before writing the next. Nothing they assert changed.
- The Gateway host has one `LicenseClock`. Both pull requests had added a property of that name; it is one property
  now, read by the hub, the minute check and the Licenses routes.

## The gate

- License unit tests: 396 passed. Hosted license tests: 10 passed.
- `.\scripts\test-database.ps1`: GREEN, 70 executed on the throwaway PostgreSQL, none failed, none skipped.
- `.\scripts\test-local.ps1` on `6488a3105`: FAILED in 1 project, the two tests below.
- `.\scripts\test-local.ps1 -Parked` on `6488a3105`: 23,251 of 23,253. Every Gateway suite is green: Gateway.Tests
  3,066 of 3,066, Gateway.UnitTests 11,040 of 11,040.

The two red tests are the same two as last round, guards that main fails by itself and that name no file this branch
touches: `GateTestsDoNotDependOnTheMachineTests` (tests were fixed on main and the baseline file was not lowered) and
`BackgroundWorkRegisterTests` (`docs/BackgroundWork.md` is one count out for `GitHubActionsBackend.cs`).
