# Proof - the Gateway's once-a-minute check and the sending of switches (phase 2)

Branch `license-switching-gw-tick`, with origin/main merged in at `faffafcdc`. The gate below ran on
commit `773bef6ed`. This is the piece that makes the Gateway actually switch: it reads the picture, asks
the decision engine, and carries out what the engine decided.

No migration was added. The no-room event lives in the existing `license_switch_log` table, so
`.\scripts\test-database.ps1` was not needed and was not run.

## What is here

- `src/CcDirector.Gateway/Licenses/LicenseSwitchService.cs` - the check for one tenant (`TickAsync`), the
  check for every tenant inside the tenant pass (`TickEveryTenantAsync`), and Switch now (`SwitchNowAsync`),
  all through one send.
- `src/CcDirector.Gateway/Licenses/LicenseOwnerMailer.cs` - the owner email, sent by tenant through the
  account service; a self-hosted Gateway sends nothing and says so.
- `src/CcDirector.Gateway/Licenses/LicenseGatewayClock.cs` - the one clock a usage report is taken at and
  the check decides by (`GatewayHost.LicenseClock`), so a hosted test can run at a fixed time.
- `src/CcDirector.Gateway/Licenses/LicenseStore.cs` - the no-room event rows, the refusal of a second open
  switch for a machine, and a quiet `HasMachines`.
- `src/CcDirector.Gateway/Licenses/LicenseDecisionEngine.cs` - `NoRoomEmail`, which rebuilds the email for
  a retry and writes none when it would no longer be true.
- `src/CcDirector.Gateway/GatewayHost.cs` - the one-minute timer, `RunLicenseSwitchCheckAsync`, and the
  wiring. `src/CcDirector.Gateway/Streaming/DirectorHub.cs` - a report is taken at the license clock.
- `LicenseSnapshotSource.cs`, `LicenseUsageHistory.cs`, `LicenseUsageIngest.cs` and their two test files
  are the shared commit `9596645a5`, cherry-picked unchanged. The picture and the two-hour history have
  one home each, shared with the Licenses API pull request; this branch does not write a second.
- `missions/license-switching/switch-now-method.md` - the exact signature of the one method the Cockpit
  API calls, written in the first commit.

## How a no-room event is stored without a migration

One row in `license_switch_log` with `Kind = "no-room"`. `FromAccountEmail` is the account that ran out,
`State` is `open` or `ended`, `NotAfterUtc` is when the event is expected to end, `ReasonCode` is
`owner-emailed`, `owner-email-failed` or `no-owner-account`, `LastSentAtUtc` is when the email went, and
`Message` is the sentence to show. `MachineId` is empty because the event belongs to an account.

## The brief, case by case

Unit tests are in `src/CcDirector.Gateway.UnitTests/Licenses/`, with a fake Director sender, a fake
mailer and a test clock. No test sleeps, reads the real clock, or opens a port.

| Case | Test |
|---|---|
| Under the threshold nothing is sent and nothing is written | `Tick_UnderTheThreshold_SendsNothingAndWritesNothing` |
| The pending row is written BEFORE the send; every machine on the account is sent | `Tick_AtTheThreshold_WritesTheRowBeforeTheSend_AndSendsToEveryMachineOnTheAccount` |
| Sends go together, not one after another | `Tick_SeveralMachines_AreSentTogether_NotOneAfterAnother` |
| Bars are recorded | `Tick_AfterAThresholdSwitch_RecordsTheBar` |
| Contract 3.5: no connection stays pending, director-offline | `Result_NotConnected_StaysPendingDirectorOffline` |
| Contract 3.5: timeout or dropped tunnel is unknown | `Result_TimeoutOrTunnelDropped_IsUnknown` (2 rows) |
| Contract 3.5: bad request or error is failed, director-error | `Result_BadRequestOrError_IsFailedDirectorError_WithTheErrorAsTheMessage` (2 rows) |
| Contract 3.5: an answer records the body's outcome and takes the read-back | `Result_Ok_RecordsTheBodysOutcomeAndReasonCode_AndTakesTheReadBack` (4 rows) |
| An answer that is not this switch's is not believed | `Result_OkWithABodyThatIsNotThisSwitchsResult_IsFailedDirectorError_AndTakesNoReadBack` |
| Offline: the same identifier is sent again | `Offline_TheNextCheck_SendsTheSameSwitchIdentifierAgain` |
| Offline past NotAfterUtc: the row ends, the next decision has a new identifier | `Offline_PastNotAfter_TheRowEndsFailed_AndTheCheckDecidesAfreshWithANewIdentifier` |
| Offline: not sent again once the decision no longer stands, or changes | `Offline_WhenTheDecisionNoLongerStands_TheSwitchIsNotSentAgain`, `Offline_WhenTheDecisionChanges_TheWaitingRowEnds_AndTheNewDecisionGetsANewIdentifier` |
| Restart (contract 5.5) before NotAfterUtc | `Restart_BeforeNotAfter_ResendsTheSameIdentifier_OnlyOnceTheMachineHasReported`, `Restart_AReplayedAnswer_SettlesTheRow_AndNeverOverwritesTheMachinesLaterReport` |
| Restart after NotAfterUtc: one more send, then settled from the machine's report | `Restart_AfterNotAfter_ResendsOnceAnyway_AndTheDirectorsExpiredAnswerSettlesTheRow`, `Restart_AfterNotAfter_NoAnswerToTheOneResend_IsSettledFromTheMachinesReport` (2 rows) |
| No second identifier while a row is open (contract 5.4) | `OpenRow_WithNoKnownOutcome_TheMachineIsSentNoSecondIdentifier`, `OpenRow_WithNoKnownOutcome_ItsMachineTakesNoPartInChoosingTheTarget_AndTheOthersStillSwitch`, `OpenRow_OnOneMachine_DoesNotHoldBackTheOtherMachinesOnTheAccount`, `CreatePendingSwitch_WhileTheMachineHasAnOpenSwitch_IsRefused_AndAllowedAgainOnceItEnds` |
| No room: one email, nothing switched, the log says so | `NoTarget_OneEmailAcrossManyChecks_NothingSwitched_AndTheLogSaysTheOwnerWasEmailed`, `NoRoom_IsOneOpenRowPerAccount_ThatCanBeMovedAndEnded` |
| A failed email is not marked emailed, is retried, and never goes twice | `NoTarget_AFailedSendIsNotMarkedEmailed_IsRetriedOnALaterCheck_AndNeverSentAgainOnceItWent`, `NoTarget_WhenTheMailServiceAsksForAWait_TheRetryWaitsThatLong`, `RecordNoRoomEmail_AnEventRecordedAsEmailed_IsNeverChangedAgain`, `NoRoomEmail_*` (2 tests) |
| Self-hosted with no account: logged, nothing sent | `NoTarget_OnAGatewayWithNoAccountToEmail_IsRecordedOnce_AndNeverTriedAgain`, `SendAsync_OnASelfHostedGateway_SendsNothing_AndSaysThereIsNoAccount` |
| The event closes; a new event emails once; a longer outage moves the end | `NoTarget_TheEventCloses_AndANewEventEmailsOnce`, `NoTarget_WhenTheOutageLastsLonger_TheEventsEndMovesWithIt` |
| The mailer itself | `LicenseOwnerMailerTests` (6 tests) |
| Switch now, through the same send | `SwitchNow_*` (6 tests) |
| Tenants are kept apart | `Tick_ForOneTenant_NeverSendsToAnotherTenantsDirector_OrTouchesItsLog`, `TickEveryTenant_ChecksEachTenantInItsOwnScope_AndOneTenantsFailureDoesNotStopTheNext`, `TickEveryTenant_WithNoTenantInScope_ChecksNothing` |
| Inert for a tenant with no machines | `Tick_ForATenantWithNoMachines_DoesNothing_NotEvenReadTheClock`, `HasMachines_IsTrueOnlyForATenantWhoseMachineHasReported` |
| The real host, two accounts, a real tunnel | `src/CcDirector.Gateway.Tests/Licenses/LicenseSwitchCheckHostedTests.cs` (2 tests) |

## Each test proved by breaking the code

Each line is one change to the product code, then the license tests, then the change undone. The count is
how many license tests went red. Run on the 190 tests of that moment; the last one was re-run after its
test was written.

| Change to the code | Tests red |
|---|---|
| A Director that is not connected no longer marks the row director-offline | 6 |
| A waiting row is never sent again with its own identifier | 1 |
| A row never sent does not end at NotAfterUtc | 1 |
| A timeout or dropped tunnel is recorded failed, not unknown | 6 |
| The answer's read-back is not taken | 5 |
| An answer for another switch identifier is accepted | 1 |
| A row with no known outcome is sent again without waiting for the machine to report | 4 |
| After NotAfterUtc a row is sent again on every check, not once | 2 |
| A row settled from the machine's report always ends failed | 1 |
| The service no longer holds back a machine whose row has no known outcome (the store still refuses) | 1 (was 0 until `OpenRow_WithNoKnownOutcome_ItsMachineTakesNoPartInChoosingTheTarget_AndTheOthersStillSwitch` was added) |
| The store no longer refuses a second open switch (the service still holds it back) | 1 |
| Neither holds the machine back | 4 |
| A failed owner email is recorded as emailed | 2 |
| An event already emailed is emailed again on the next check | 4 |
| The mail service's asked-for wait is ignored | 1 |
| The engine's new bars are not recorded | 1 |
| A tenant with no machines is checked anyway | 1 |
| The sends of one check run one after another | 1 |
| One tenant's failed check stops the tenants after it | 1 |
| A closed no-room event is not ended in the log | 1 |
| A no-room event's later end is not recorded | 1 |
| A waiting row is not ended when the decision for its machine changes | 1 |
| A waiting Switch now is never sent again by the minute check | 1 |
| Switch now does not end the switch still waiting for an offline Director | 1 |
| An event recorded as emailed can be changed again | 1 |
| An account can have two open no-room events | 1 |
| An open no-room event older than the asked-for time is left out of the log | 1 |
| The rebuilt no-room email is written even when a target exists | 1 |
| The host runs its check outside the tenant pass (`GatewayHost.RunLicenseSwitchCheckAsync`) | 2 of 2 hosted tests |

Not broken on purpose, and why: "the row is written before the send" is asserted directly - the fake
Director reads the row at the moment the command arrives and finds it pending. The move of the usage
report's time onto the license clock was not broken on purpose either; the hosted tests would see it only
by how far the real clock is from the test's fixed time.

## Commands run, and their results

```
dotnet test src\CcDirector.Gateway.UnitTests\CcDirector.Gateway.UnitTests.csproj --filter "FullyQualifiedName~Tests.Licenses"
    Passed: 191, Failed: 0
dotnet test src\CcDirector.Gateway.Tests\CcDirector.Gateway.Tests.csproj --filter "FullyQualifiedName~LicenseSwitchCheckHostedTests"
    Passed: 2, Failed: 0
.\scripts\test-local.ps1 -Parked            (commit 773bef6ed, clean tree, 22,800 tests in 16 suites)
    RESULT: FAILED in 2 projects - 4 tests, none of them in this change (below)
.\scripts\test-local.ps1 -RerunFailed <that run's folder>
    3 passed alone, 1 failed again
```

The `-Parked` run is every suite of the default run plus the three parked ones:

| Suite | Passed | Failed |
|---|---|---|
| CcDirector.Core.UnitTests | 1,624 | 1 |
| CcDirector.Avalonia.Tests | 1,043 | 0 |
| CcDirector.Engine.Tests | 86 | 0 |
| CcDirector.HostedAgent.Tests | 89 | 0 |
| CcDirector.Launcher.Tests | 218 | 0 |
| CcDirector.Terminal.Avalonia.Tests | 82 | 0 |
| CcDirector.Reclaim.Tests | 305 | 0 |
| cc-director-setup.Tests | 39 | 0 |
| cc-director-setup-engine.Tests | 870 | 0 |
| cc-director-setup-cli.Tests | 74 | 0 |
| CcDirector.Gateway.Tests (four parts) | 3,052 | 3 |
| CcDirector.Core.Tests | 4,585 | 0 |
| CcDirector.Gateway.UnitTests | 10,729 | 0 |

### The gate is not green, and the four red tests are not from this change

1. `GateTestsDoNotDependOnTheMachineTests.No_gate_test_adds_a_sleep_a_real_clock_a_real_process_docker_or_a_fixed_port`
   fails on `src/CcDirector.Gateway.UnitTests/CronRunResultTests.cs`, four new reads of the real clock.
   That file arrived from main (`2a6df8062`) and is byte for byte the file on origin/main; this branch
   does not touch it. Main is red on it by itself, and it failed again on the re-run as it must. Two open
   pull requests (3917 and 3918) say main's gate is red on reads of the real clock; whether either
   covers this file was not checked.
   The first default run of this branch caught the same guard on this branch's own hosted test (six reads
   of the real clock). That one was a true finding and is fixed: the host now has a license clock and the
   test runs at a fixed time.
2. `MissionRouteTenantScopingTests.A_missions_list_serves_only_the_callers_own_missions` and two tests in
   `MissionAttachRouteTenantScopingTests` failed once with a 500 from creating a mission. The Gateway's own
   line for it: `IOException: The process cannot access the file '...\missions.json' because it is being
   used by another process`. No license code is on that path. All three passed alone on the re-run of the
   same commit. They are flaky on a shared file, and belong to whoever owns the mission store.

The default run alone (`.\scripts\test-local.ps1`, no `-Parked`) was run once before the clock fix, on a
machine at 100% processor with another session's release gate under way. It ended OVER BUDGET: two suites
passed the 120-second ceiling and were stopped. Every suite of the default run then ran to the end inside
the `-Parked` run above, which is the same suites and three more.

## The review round (pull request 3923)

The review and the answers to every finding are `missions/license-switching/reviews/3923-minute-check-review.md` and
`3923-answers.md`. What changed in the code, the test that holds each change down, and the break that turns it red:

| Change | Test | Break, and tests red |
|---|---|---|
| A switch is marked in flight only once the whole plan stands, and the mark is cleared on every path out | `ACheckThatFailsAfterPlanningASend_DoesNotStrandThatSwitch` | Marked when its send is planned, as before: 1 |
| A waiting row is written back to pending with no reason before it is sent again | `AResendCutOffPartWay_ReadsAsOutcomeNotKnown_AndTheMachineIsGivenNoSecondIdentifier` | Not written back: 1 |
| A replayed answer's read-back is never taken once the machine has reported after the row was written | `ApplySwitchReadBack_ReplayedAnswerFromADirectorWhoseClockIsAhead_IsNotTakenOnceTheMachineHasReportedSinceTheRowWasWritten`, `..._WhenTheMachineHasNotReportedSinceTheRowWasWritten_IsTaken` | The new condition removed: 1 |
| Each tenant's check starts in its own scope and all are awaited together; a skipped minute logs a line | `TickEveryTenant_ADirectorThatDoesNotAnswer_HoldsUpNoOtherTenant_AndEachSendStartsInItsOwnTenantsScope` | Each tenant awaited before the next: 1 |
| A resend goes to the Director of the machine's last report | `AResend_GoesToTheDirectorOfTheMachinesLastReport_NotTheOneTheRowWasWrittenFor` | Goes to the Director the row was written for: 1 |
| The in-flight mark | `TheMinuteCheck_WhileSwitchNowsSendIsInFlight_DoesNotSendThatSwitchAgain`, `SwitchNow_WhileTheMinuteChecksSendIsInFlight_LeavesThatMachineAlone` | Nothing is ever marked: 1 |
| Not settled from a report that says the switch is still running | `Restart_AfterNotAfter_WhileTheMachineSaysThatSwitchIsStillRunning_IsNotSettledFromItsReport` | The condition made always true: 1 |
| A refused owner email is tried further and further apart, up to an hour | `NoTarget_AnEmailThatKeepsBeingRefused_IsTriedFurtherAndFurtherApart` | The wait fixed at one minute: 1 |
| The store's statements on PostgreSQL | `src/CcDirector.Gateway.DatabaseTests/Data/LicenseStorePostgresTests.cs` (2 tests) | - |
| A check that finds nothing to do writes no line of its own | read from the code; no test | - |

One of these tests first failed by hanging rather than by going red (the in-flight one, when nothing was marked): it
awaited a check that was waiting on an answer that never came. It now judges the check before awaiting it. The aborted
run reported "Passed: 179" of 201, which is why the count of a run is always read beside its total here.


## What a reviewer should know

- Tenants are decided one after another and their sends go together: a Director that does not answer holds up
  nobody else (changed in the review round; it used to hold the tenants after it).
- The Gateway marks an event emailed after the mail service confirms. A Gateway that stops between the
  confirmation and the mark sends that one email once more after it starts again. Sending nothing would
  be the worse mistake, so the mark comes second.
- A row that is pending with reason director-offline was never sent, so it does not hold its machine back
  from a new decision. A row sent with no known outcome does.
