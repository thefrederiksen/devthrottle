# Proof - the Gateway relays sign-in approvals and starts sign-ins from the Cockpit

License Switching, phase 2 (Gateway), contract section 8, parts 1 to 3: who approves each account (8.3), the approval
relay (8.3), and Add a spare and Renew now from the Cockpit (8.4). Part 4, the owner's email for a renewal that keeps
failing (8.1), is NOT in this change and no migration is: it waits for the stored failed renewal to reach main.

Branch `license-switching-gw-approval`, cut from `origin/main` at `30e468c18`. The runs below are on commit
`5e9f44083`; the only commit after it adds this file.

## What was built

- **Approvers.** `LicenseApprover { AccountEmail, MachineName, OsUser, BrowserProfile }`, one per Claude account, kept
  per DevThrottle account in the existing per-account settings (`tenant_settings`, key `license_approvers`). Routes:
  `GET`, `PUT` and `DELETE /gateway/licenses/approvers`. The overview says per account who approves it.
- **The relay.** `DirectorHub.RequestLicenseApproval` returns at once. `LicenseApprovalRelay` finds the approver in the
  caller's own account, sends `license-approve` to the Director that last reported for that machine with the caller's
  own key from its Hello as `SealToRecipient`, and sends the answer on as `license-approval-result`; or answers at once
  `no-approver` or `approver-offline`. The address and the sealed approval are held in memory for the one relay.
- **Sign-ins from the Cockpit.** `POST /gateway/licenses/sign-ins/add-spare` and `.../renew` answer `202` as soon as
  the sign-in's row is written and `license-sign-in` is on its way. The row is a row of the Licenses log of its own
  kind (`sign-in`), which the log route answers in words.
- **No migration, no new table, no new column.** The entity file changed by a comment only.
- **The document.** `missions/license-switching/cockpit-api.md`, new section 7: every route and shape, each with a
  real answer produced by running the handlers.

## What was run

Each run in the foreground, one at a time, on a clean tree.

| Run | Result |
|---|---|
| `.\scripts\test-local.ps1` | 10 suites, 4,562 tests. 9 suites Completed. `Core.UnitTests`: 1,718 passed, 2 failed - the two known reds below. |
| `.\scripts\test-local.ps1 -Parked` | 16 suites, 23,414 tests. 15 Completed, among them `Gateway.UnitTests` 11,189 of 11,189, `Gateway.Tests` 3,078 of 3,078 in four shards, `Core.Tests` 4,585 of 4,585. `Core.UnitTests`: the same two reds. 12 minutes. |
| `.\scripts\test-database.ps1` | GREEN. 70 executed against the throwaway PostgreSQL, none failed, none skipped. |

The two reds are on main and not of this change, as the brief named them:

- `GateTestsDoNotDependOnTheMachineTests.No_gate_test_adds_a_sleep_a_real_clock_a_real_process_docker_or_a_fixed_port`
  - it asks for five entries to be REMOVED from its baseline (files of other work); it names no file of this change.
- `BackgroundWorkRegisterTests.EveryTimerWatcherAndWaitingLoop_HasARowInTheRegister_AndEveryRowStillPointsAtOne` - the
  count for `GitHubActionsBackend.cs`.

**One red of this change was found by the first `-Parked` run and fixed.** On commit `415120e14`,
`GatewayHttpFailureLineShapeTests` failed: four failure lines of the new routes said "GET approvers FAILED" where the
rule is "VERB /route". The lines were corrected (`5e9f44083`) and the whole `-Parked` run was made again on that commit;
the table above is that second run.

## The tests of this change

| File | Tests | What it holds down |
|---|---|---|
| `Gateway.UnitTests/Licenses/LicenseApproverStoreTests.cs` | 20 | one approver per account, per DevThrottle account; refusals store nothing; exactly the four fields are stored |
| `Gateway.UnitTests/Licenses/LicenseApprovalRelayTests.cs` | 40 | returns at once; the command, the key, the waits, the Director that last reported; every refusal; tenant isolation |
| `Gateway.UnitTests/Licenses/RequestLicenseApprovalHubTests.cs` | 8 | the hub method: refused before Hello, returns at once, the key and the tenant are the connection's |
| `Gateway.UnitTests/Licenses/LicenseSignInServiceTests.cs` | 33 | accepted at once with the row written; every refusal; every result of the send; rows with no answer |
| `Gateway.UnitTests/Licenses/LicenseSignInEndpointsTests.cs` | 23 | the exact shape of every answer; finished sentences; the page and the route say the same |
| `Gateway.UnitTests/Licenses/LicenseSignInKeepsNothingTests.cs` | 3 | no column, no stored byte and no log line carries the address or the sealed approval |
| `Gateway.UnitTests/Licenses/LicenseGuardTests.cs` | +17 | what a session key may call |
| `Gateway.Tests/Licenses/LicenseSignInHostedTests.cs` | 12 | a real hosted Gateway, three test Directors on the real tunnel, the routes over real HTTP |

`LicenseEndpointsTests` (already on main) lists the exact names of the overview's shapes; the eight new names were
appended to it.

## What each break turned red

Each line below is one behaviour broken in the product code, the code rebuilt, the tests run, and the source put back
(the tree was checked clean before and after each). 33 breaks; every one turned at least one test red. "unit" is the
license tests of `Gateway.UnitTests` (545), "hosted" is `LicenseSignInHostedTests` (12); a break was run against the
hosted tests where the behaviour passes through the host.

Breaks 0 to 5 were run on commit `4ed05750a`, before a power cut; the rest on `415120e14`. The product code of the two
commits is the same; the second only gives three waits in the relay tests a limit.

**Three things the breaks showed, said plainly:**

1. **A defect of this change was found by the hosted tests, not by the unit tests.** The three routes that read a body
   were first mapped with a lambda that the framework bound as a plain request delegate: the handler ran and its
   answer was thrown away, `200` with an empty body. Every unit test passed, because they call the handlers. Ten of
   the twelve hosted tests went red. It is fixed, and break 32 puts it back and shows the red again.
2. **Two tests hung instead of failing.** With the relay sending nothing (break 6), a relay test waited for a command
   with no limit and the run never ended. Those waits now have a limit that only ends a broken run, and break 6 is a
   red test.
3. **The hosted tests do NOT prove the relay and the sign-in enter the tenant's scope themselves** (breaks 6 and 21:
   unit red, hosted green). On the host the background work inherits the scope of the hub call or the request that
   started it, so the commands are delivered either way. The unit tests hold the explicit scope; nothing end to end
   does. The same for break 3: with the relay's own check removed, the host's send to a Director that is not connected
   returns nothing and the answer is still `approver-offline`.

### 0. relay: the hub call waits for the approving Director

`src/CcDirector.Gateway/Licenses/LicenseApprovalRelay.cs`

- unit: 2 red, 0 green
  - LicenseApprovalRelayTests.Accept_ReturnsBeforeTheApprovingDirectorAnswers
  - RequestLicenseApprovalHubTests.RequestLicenseApproval_ReturnsWhileTheApprovingMachineStillHoldsTheCommand

### 1. relay: the approval is sealed to no key of the asking Director

`src/CcDirector.Gateway/Licenses/LicenseApprovalRelay.cs`

- unit: 3 red, 542 green
  - LicenseApprovalRelayTests.Relay_WithTheApproverConnected_SendsLicenseApproveToItsDirector_WithTheAskingDirectorsOwnKey
  - RequestLicenseApprovalHubTests.RequestLicenseApproval_FromTwoDirectors_SealsEachToItsOwnKey
  - RequestLicenseApprovalHubTests.RequestLicenseApproval_SealsToTheKeyOfTheCallersOwnHello_AndAnswersTheCallersOwnDirectorInItsOwnTenan
- hosted: 1 red, 11 green
  - LicenseSignInHostedTests.ARequest_IsApprovedOnTheApprovingMachine_SealedToTheAskingDirectorsOwnKey_AndAnsweredToI

### 2. relay: no approver set is answered approver-offline

`src/CcDirector.Gateway/Licenses/LicenseApprovalRelay.cs`

- unit: 3 red, 542 green
  - LicenseApprovalRelayTests.Relay_AnApproverSetInAnotherAccount_IsNeverUsed_AndThatAccountsDirectorIsSentNothing
  - LicenseApprovalRelayTests.Relay_NoApproverSetForTheAccount_AnswersRefusedNoApprover_AndAsksNoMachine
  - LicenseSignInKeepsNothingTests.NoLogLine_CarriesTheAddressOrTheApproval_WhileEachRelayAndSignInIsLoggedByItsIdentifier
- hosted: 2 red, 10 green
  - LicenseSignInHostedTests.ARequest_ForAnAccountWithNoApproverSet_IsAnsweredAtOnce_RefusedNoApprover
  - LicenseSignInHostedTests.AnAccountWithNoApprover_IsNotServedByTheApproverOfAnother

### 3. relay: an approving Director that is not connected is asked anyway

`src/CcDirector.Gateway/Licenses/LicenseApprovalRelay.cs`

- unit: 1 red, 544 green
  - LicenseApprovalRelayTests.Relay_TheApprovingMachinesDirectorIsNotConnected_AnswersApproverOffline_AndAsksNoMachine
- hosted: 0 red, 12 green (this break is not seen here)

### 4. relay: the approve command is given the wrong wait

`src/CcDirector.Gateway/Licenses/LicenseApprovalRelay.cs`

- unit: 1 red, 544 green
  - LicenseApprovalRelayTests.Relay_UsesTheWaitsOfTheContrac

### 5. relay: the answer is given the wrong wait

`src/CcDirector.Gateway/Licenses/LicenseApprovalRelay.cs`

- unit: 1 red, 544 green
  - LicenseApprovalRelayTests.Relay_UsesTheWaitsOfTheContrac

### 6. relay: the commands are sent outside the tenant scope

`src/CcDirector.Gateway/Licenses/LicenseApprovalRelay.cs`

- unit: 21 red, 524 green
  - LicenseApprovalRelayTests.Accept_OnePastTheLimitOfApprovalsInFlight_IsRefusedAtOnce_AndAsksNoMachine
  - LicenseApprovalRelayTests.Accept_ReturnsBeforeTheApprovingDirectorAnswers
  - LicenseApprovalRelayTests.Accept_TheSameSignInAgainWhileItsRelayRuns_IsDropped
  - LicenseApprovalRelayTests.Relay_ARefusalWithAReasonTheContractDoesNotHave_IsPassedOnAsApprovalFailed
  - LicenseApprovalRelayTests.Relay_AnAnswerThatIsNotTheResultOfThisApproval_AnswersRefused_AndPassesOnNoSealedValue
  - LicenseApprovalRelayTests.Relay_AnApproverSetInAnotherAccount_IsNeverUsed_AndThatAccountsDirectorIsSentNothing
  - LicenseApprovalRelayTests.Relay_Approved_SendsTheSealedApprovalToTheDirectorThatAsked_AndToNoOther
  - LicenseApprovalRelayTests.Relay_EveryCommandIsSentInsideTheAskingDirectorsOwnTenantScope
  - LicenseApprovalRelayTests.Relay_FindsTheApprovingMachineWithoutRegardToCase
  - LicenseApprovalRelayTests.Relay_TheApproveCommandGetsNoAnswerInTime_AnswersRefusedApprovalFailed
  - LicenseApprovalRelayTests.Relay_TheApprovingDirectorDoesNotKnowTheCommand_AnswersRefusedApprovalFailed
  - LicenseApprovalRelayTests.Relay_TheApprovingDirectorRefuses_PassesOnItsReasonAndItsSentence_AndNoSealedValue
  - LicenseApprovalRelayTests.Relay_TwoAccountsAskingForTheSameAccount_EachReachesOnlyItsOwnApprover
  - LicenseSignInKeepsNothingTests.AfterRelaysAndASignIn_TheBytesOfTheDatabaseContainNeitherTheAddressNorTheApproval
  - LicenseSignInKeepsNothingTests.NoLogLine_CarriesTheAddressOrTheApproval_WhileEachRelayAndSignInIsLoggedByItsIdentifier
  - RequestLicenseApprovalHubTests.RequestLicenseApproval_ReturnsWhileTheApprovingMachineStillHoldsTheCommand
  - RequestLicenseApprovalHubTests.RequestLicenseApproval_SealsToTheKeyOfTheCallersOwnHello_AndAnswersTheCallersOwnDirectorInItsOwnTenan
- hosted: 0 red, 12 green (this break is not seen here)

### 7. relay: a refusal passes the sealed value on

`src/CcDirector.Gateway/Licenses/LicenseApprovalRelay.cs`

- unit: 3 red, 542 green
  - LicenseApprovalRelayTests.Relay_TheApprovingDirectorRefuses_PassesOnItsReasonAndItsSentence_AndNoSealedValue
  - LicenseSignInKeepsNothingTests.AfterRelaysAndASignIn_TheBytesOfTheDatabaseContainNeitherTheAddressNorTheApproval
  - LicenseSignInKeepsNothingTests.NoLogLine_CarriesTheAddressOrTheApproval_WhileEachRelayAndSignInIsLoggedByItsIdentifier

### 8. relay: a reason word the contract does not have is passed on

`src/CcDirector.Gateway/Licenses/LicenseApprovalRelay.cs`

- unit: 1 red, 544 green
  - LicenseApprovalRelayTests.Relay_ARefusalWithAReasonTheContractDoesNotHave_IsPassedOnAsApprovalFailed

### 9. relay: a request past its deadline is relayed

`src/CcDirector.Gateway/Licenses/LicenseApprovalRelay.cs`

- unit: 1 red, 544 green
  - LicenseApprovalRelayTests.Relay_TheRequestArrivedAfterItsOwnDeadline_AnswersExpired_AndAsksNoMachine

### 10. relay: a log line carries the address

`src/CcDirector.Gateway/Licenses/LicenseApprovalRelay.cs`

- unit: 1 red, 544 green
  - LicenseSignInKeepsNothingTests.NoLogLine_CarriesTheAddressOrTheApproval_WhileEachRelayAndSignInIsLoggedByItsIdentifier

### 11. relay: a log line carries the sealed approval

`src/CcDirector.Gateway/Licenses/LicenseApprovalRelay.cs`

- unit: 1 red, 544 green
  - LicenseSignInKeepsNothingTests.NoLogLine_CarriesTheAddressOrTheApproval_WhileEachRelayAndSignInIsLoggedByItsIdentifier

### 12. relay: a repeat of a request in flight is relayed again

`src/CcDirector.Gateway/Licenses/LicenseApprovalRelay.cs`

- unit: 1 red, 544 green
  - LicenseApprovalRelayTests.Accept_TheSameSignInAgainWhileItsRelayRuns_IsDropped

### 13. approvers: every account shares one list

`src/CcDirector.Gateway/Licenses/LicenseApproverStore.cs`

- unit: 39 red, 506 green
  - LicenseApprovalRelayTests.Accept_OnePastTheLimitOfApprovalsInFlight_IsRefusedAtOnce_AndAsksNoMachine
  - LicenseApprovalRelayTests.Accept_ReturnsBeforeTheApprovingDirectorAnswers
  - LicenseApprovalRelayTests.Accept_TheSameSignInAgainWhileItsRelayRuns_IsDropped
  - LicenseApprovalRelayTests.Relay_ARefusalWithAReasonTheContractDoesNotHave_IsPassedOnAsApprovalFailed
  - LicenseApprovalRelayTests.Relay_AnAnswerThatIsNotTheResultOfThisApproval_AnswersRefused_AndPassesOnNoSealedValue
  - LicenseApprovalRelayTests.Relay_AnApproverNamingAMachineOnlyAnotherAccountHas_AnswersApproverOffline_AndThatMachineIsSentNothing
  - LicenseApprovalRelayTests.Relay_Approved_SendsTheSealedApprovalToTheDirectorThatAsked_AndToNoOther
  - LicenseApprovalRelayTests.Relay_EveryCommandIsSentInsideTheAskingDirectorsOwnTenantScope
  - LicenseApprovalRelayTests.Relay_FindsTheApprovingMachineWithoutRegardToCase
  - LicenseApprovalRelayTests.Relay_SendsToTheDirectorThatSentTheApprovingMachinesLastRepor
  - LicenseApprovalRelayTests.Relay_TheApproveCommandGetsNoAnswerInTime_AnswersRefusedApprovalFailed
  - LicenseApprovalRelayTests.Relay_TheApprovingDirectorDisconnectsBetweenTheCheckAndTheSend_AnswersApproverOffline
  - LicenseApprovalRelayTests.Relay_TheApprovingDirectorDoesNotKnowTheCommand_AnswersRefusedApprovalFailed
  - LicenseApprovalRelayTests.Relay_TheApprovingDirectorRefuses_PassesOnItsReasonAndItsSentence_AndNoSealedValue
  - LicenseApprovalRelayTests.Relay_TheApprovingMachineHasNeverReported_AnswersApproverOffline_AndAsksNoMachine
  - LicenseApprovalRelayTests.Relay_TheApprovingMachineIsAnotherUserOfThatComputer_AnswersApproverOffline
  - LicenseApprovalRelayTests.Relay_TheApprovingMachinesDirectorIsNotConnected_AnswersApproverOffline_AndAsksNoMachine
  - LicenseApprovalRelayTests.Relay_TheTunnelToTheApprovingDirectorDrops_AnswersApproverOffline
  - LicenseApprovalRelayTests.Relay_TwoAccountsAskingForTheSameAccount_EachReachesOnlyItsOwnApprover
  - LicenseApprovalRelayTests.Relay_UsesTheWaitsOfTheContrac
  - LicenseApprovalRelayTests.Relay_WithTheApproverConnected_SendsLicenseApproveToItsDirector_WithTheAskingDirectorsOwnKey
  - LicenseApproverStoreTests.AStoredValueThatCannotBeRead_IsAnError_NeverAnEmptyLis
  - LicenseApproverStoreTests.AnApprover_SetInOneAccount_IsNeverSeenByAnother
  - LicenseApproverStoreTests.List_IsInAccountEmailOrder
  - LicenseApproverStoreTests.Remove_OfTheLastApprover_LeavesNoStoredValueAtAll
  - LicenseApproverStoreTests.Remove_RemovesOnlyThatAccountsApprover_AndSaysWhetherThereWasOne
  - LicenseApproverStoreTests.Set_ForAnAccountThatHasAnApprover_ReplacesI
  - LicenseApproverStoreTests.Set_OnePastTheMostApproversAnAccountKeeps_IsRefused_AndReplacingOneIsStillAllowed
  - LicenseApproverStoreTests.Set_ThenFindAndList_ReturnTheApprover_WithTheAccountLowerCasedAndTheNamesTrimmed
  - LicenseSignInEndpointsTests.Approvers_OfOneAccount_AreNeverListedSetOverOrRemovedByAnother
  - LicenseSignInEndpointsTests.Overview_SaysPerAccountWhoApprovesIt_AsFinishedWords
  - LicenseSignInEndpointsTests.RemoveApprover_SaysWhetherThereWasOne_InASentence
  - LicenseSignInEndpointsTests.SetApprover_AnswersTheApproverInTheExactShapeThePageIsBuiltFrom_AndTheListShowsI
  - LicenseSignInKeepsNothingTests.AfterRelaysAndASignIn_TheBytesOfTheDatabaseContainNeitherTheAddressNorTheApproval
  - LicenseSignInKeepsNothingTests.NoLogLine_CarriesTheAddressOrTheApproval_WhileEachRelayAndSignInIsLoggedByItsIdentifier
- hosted: 5 red, 7 green
  - LicenseSignInHostedTests.ARequest_IsApprovedOnTheApprovingMachine_SealedToTheAskingDirectorsOwnKey_AndAnsweredToI
  - LicenseSignInHostedTests.ARequest_WhoseApprovingMachineHasNoDirectorConnected_IsAnsweredAtOnce_RefusedApproverOffline
  - LicenseSignInHostedTests.OneAccount_NeverReachesAnApproverOrADirectorOfAnother
  - LicenseSignInHostedTests.TheApproverRoutes_AreTheOwnersToWrite_AndASessionKeyMayOnlyRead
  - LicenseSignInHostedTests.TheOverview_SaysWhoApprovesEachAccount_AndOffersTheSignInsItsRoutesWouldStar

### 14. stored: a sign-in row keeps the address mark

`src/CcDirector.Gateway/Licenses/LicenseSignInLog.cs`

- unit: 1 red, 2 green
  - LicenseSignInKeepsNothingTests.AfterRelaysAndASignIn_TheBytesOfTheDatabaseContainNeitherTheAddressNorTheApproval

### 15. stored: the log table gains a column for the address

`src/CcDirector.Gateway/Data/Entities/LicenseEntities.cs`

- unit: 3 red, 0 green
  - LicenseSignInKeepsNothingTests.AfterRelaysAndASignIn_TheBytesOfTheDatabaseContainNeitherTheAddressNorTheApproval
  - LicenseSignInKeepsNothingTests.NoLogLine_CarriesTheAddressOrTheApproval_WhileEachRelayAndSignInIsLoggedByItsIdentifier
  - LicenseSignInKeepsNothingTests.NoStoredTable_HasAColumnForTheAddressOrTheApproval

### 16. sign-in: the route waits for the Director

`src/CcDirector.Gateway/Licenses/LicenseSignInService.cs`

- unit: 2 red, 0 green
  - LicenseSignInEndpointsTests.AddSpare_AnswersAcceptedAtOnce_InTheExactShapeThePageIsBuiltFrom
  - LicenseSignInServiceTests.Start_AnswersBeforeTheDirectorDoes_WithItsRowAlreadyWrittenPending

### 17. sign-in: a machine with no Director connected is sent one

`src/CcDirector.Gateway/Licenses/LicenseSignInService.cs`

- unit: 3 red, 542 green
  - LicenseSignInEndpointsTests.Overview_OffersNeither_OnAMachineWhoseDirectorIsNotConnected_WithTheSentenceTheRouteRefusesWi
  - LicenseSignInEndpointsTests.StartingASignIn_ThatCannotBeStarted_IsRefusedWithAStatusAWordAndAFinishedSentence
  - LicenseSignInServiceTests.Start_OnAMachineWhoseDirectorIsNotConnected_IsRefusedWithASentence_AndNothingIsWrittenOrSen
- hosted: 1 red, 11 green
  - LicenseSignInHostedTests.ASignIn_OnAMachineWithNoDirectorConnected_IsRefusedWithAFinishedSentence_AndNothingIsWritten

### 18. sign-in: a second sign-in for the same machine and account is started

`src/CcDirector.Gateway/Licenses/LicenseSignInService.cs`

- unit: 5 red, 540 green
  - LicenseSignInEndpointsTests.Overview_WhileASignInIsRunning_ShowsItOnItsMachine_AndDoesNotOfferToRenewThatAccoun
  - LicenseSignInEndpointsTests.StartingASignIn_ThatCannotBeStarted_IsRefusedWithAStatusAWordAndAFinishedSentence
  - LicenseSignInServiceTests.NoAnswerCameBack_TheRowIsUnknownAndStillOpen_AndItBlocksASecondSignIn(status
  - LicenseSignInServiceTests.Start_WhileASignInForThatMachineAndAccountIsRunning_IsRefusedWithASentence
- hosted: 1 red, 11 green
  - LicenseSignInHostedTests.AddASpare_IsAcceptedWhileTheDirectorStillHoldsTheCommand_AndItsOutcomeLandsInTheLog

### 19. sign-in: the command is given the wrong wait

`src/CcDirector.Gateway/Licenses/LicenseSignInService.cs`

- unit: 1 red, 544 green
  - LicenseSignInServiceTests.Start_AddASpare_SendsLicenseSignInToTheMachinesDirector_AsTheContractSays

### 20. sign-in: no answer is recorded as failed

`src/CcDirector.Gateway/Licenses/LicenseSignInService.cs`

- unit: 9 red, 536 green
  - LicenseSignInEndpointsTests.Overview_OfOneAccount_SaysNothingOfTheApproversOrSignInsOfAnother
  - LicenseSignInServiceTests.NoAnswerCameBack_TheRowIsUnknownAndStillOpen_AndItBlocksASecondSignIn(status
  - LicenseSignInServiceTests.SettleUnanswered_ANewSpareTheMachinesReportDoesNotShow_EndsFailed
  - LicenseSignInServiceTests.SettleUnanswered_ANewSpareTheMachinesReportShows_EndsAdded_AndSaysItWasSettledFromTheRepor
  - LicenseSignInServiceTests.SettleUnanswered_ARenewal_EndsRenewedOnlyWhenTheReportShowsASignInMadeAfterItWasAsked(signedInAfter
  - LicenseSignInServiceTests.SettleUnanswered_BeforeNoDirectorCouldStillBeRunningIt_LeavesTheRowOpen
  - LicenseSignInServiceTests.SettleUnanswered_SettlesOnlyTheAccountItIsAskedFor

### 21. sign-in: the command is sent outside the tenant scope

`src/CcDirector.Gateway/Licenses/LicenseSignInService.cs`

- unit: 30 red, 515 green
  - LicenseSignInEndpointsTests.Overview_OfOneAccount_SaysNothingOfTheApproversOrSignInsOfAnother
  - LicenseSignInEndpointsTests.Overview_WhileASignInIsRunning_ShowsItOnItsMachine_AndDoesNotOfferToRenewThatAccoun
  - LicenseSignInEndpointsTests.StartingASignIn_ThatCannotBeStarted_IsRefusedWithAStatusAWordAndAFinishedSentence
  - LicenseSignInEndpointsTests.TheOutcomeOfASignIn_LandsInTheLog_AsItsOwnKindOfRow_InWords
  - LicenseSignInKeepsNothingTests.AfterRelaysAndASignIn_TheBytesOfTheDatabaseContainNeitherTheAddressNorTheApproval
  - LicenseSignInKeepsNothingTests.NoLogLine_CarriesTheAddressOrTheApproval_WhileEachRelayAndSignInIsLoggedByItsIdentifier
  - LicenseSignInServiceTests.ARowThatHasEnded_IsNeverChangedAgain
  - LicenseSignInServiceTests.AnAnswerThatIsNotTheResultOfThisSignIn_TheRowEndsFailedDirectorError
  - LicenseSignInServiceTests.NoAnswerCameBack_TheRowIsUnknownAndStillOpen_AndItBlocksASecondSignIn(status
  - LicenseSignInServiceTests.SettleUnanswered_ANewSpareTheMachinesReportDoesNotShow_EndsFailed
  - LicenseSignInServiceTests.SettleUnanswered_ANewSpareTheMachinesReportShows_EndsAdded_AndSaysItWasSettledFromTheRepor
  - LicenseSignInServiceTests.SettleUnanswered_ARenewal_EndsRenewedOnlyWhenTheReportShowsASignInMadeAfterItWasAsked(signedInAfter
  - LicenseSignInServiceTests.SettleUnanswered_BeforeNoDirectorCouldStillBeRunningIt_LeavesTheRowOpen
  - LicenseSignInServiceTests.SettleUnanswered_NeverSettlesASignInWhoseAnswerMayStillCome
  - LicenseSignInServiceTests.SettleUnanswered_SettlesOnlyTheAccountItIsAskedFor
  - LicenseSignInServiceTests.Start_AddASpare_SendsLicenseSignInToTheMachinesDirector_AsTheContractSays
  - LicenseSignInServiceTests.Start_AnswersBeforeTheDirectorDoes_WithItsRowAlreadyWrittenPending
  - LicenseSignInServiceTests.Start_SettlesARowPastItsTimeFirst_SoItNeverBlocksANewSignIn
  - LicenseSignInServiceTests.Start_TwoAccountsWithAMachineOfTheSameName_EachSignsInItsOwnMachineInItsOwnScope
  - LicenseSignInServiceTests.Start_WhileASignInForThatMachineAndAccountIsRunning_IsRefusedWithASentence
  - LicenseSignInServiceTests.TheDirectorRefused_TheRowEndsFailedWithItsReasonAndItsSentence
  - LicenseSignInServiceTests.TheDirectorSignedIn_TheRowEndsWithItsOutcomeItsSentenceAndItsTimes(outcome
- hosted: 0 red, 12 green (this break is not seen here)

### 22. sign-in: an unanswered row is settled at once

`src/CcDirector.Gateway/Licenses/LicenseSignInService.cs`

- unit: 3 red, 542 green
  - LicenseSignInServiceTests.NoAnswerCameBack_TheRowIsUnknownAndStillOpen_AndItBlocksASecondSignIn(status
  - LicenseSignInServiceTests.SettleUnanswered_BeforeNoDirectorCouldStillBeRunningIt_LeavesTheRowOpen

### 23. sign-in: a row still in flight is settled

`src/CcDirector.Gateway/Licenses/LicenseSignInService.cs`

- unit: 1 red, 544 green
  - LicenseSignInServiceTests.SettleUnanswered_NeverSettlesASignInWhoseAnswerMayStillCome

### 24. sign-in: add a spare is accepted for an account the machine holds

`src/CcDirector.Gateway/Licenses/LicenseSignInService.cs`

- unit: 2 red, 543 green
  - LicenseSignInEndpointsTests.StartingASignIn_ThatCannotBeStarted_IsRefusedWithAStatusAWordAndAFinishedSentence
  - LicenseSignInServiceTests.Start_AddASpareForAnAccountTheMachineHolds_AndRenewForOneItDoesNot_AreRefusedWithASentence

### 25. sign-in: an answer for another sign-in is recorded as this one

`src/CcDirector.Gateway/Licenses/LicenseSignInService.cs`

- unit: 1 red, 544 green
  - LicenseSignInServiceTests.AnAnswerThatIsNotTheResultOfThisSignIn_TheRowEndsFailedDirectorError

### 26. hub: the key of the Hello is not kept

`src/CcDirector.Gateway/Streaming/DirectorHub.cs`

- unit: 3 red, 542 green
  - RequestLicenseApprovalHubTests.RequestLicenseApproval_FromTwoDirectors_SealsEachToItsOwnKey
  - RequestLicenseApprovalHubTests.RequestLicenseApproval_ReturnsWhileTheApprovingMachineStillHoldsTheCommand
  - RequestLicenseApprovalHubTests.RequestLicenseApproval_SealsToTheKeyOfTheCallersOwnHello_AndAnswersTheCallersOwnDirectorInItsOwnTenan
- hosted: 5 red, 7 green
  - LicenseSignInHostedTests.ARequest_ForAnAccountWithNoApproverSet_IsAnsweredAtOnce_RefusedNoApprover
  - LicenseSignInHostedTests.ARequest_IsApprovedOnTheApprovingMachine_SealedToTheAskingDirectorsOwnKey_AndAnsweredToI
  - LicenseSignInHostedTests.ARequest_WhoseApprovingMachineHasNoDirectorConnected_IsAnsweredAtOnce_RefusedApproverOffline
  - LicenseSignInHostedTests.AnAccountWithNoApprover_IsNotServedByTheApproverOfAnother
  - LicenseSignInHostedTests.OneAccount_NeverReachesAnApproverOrADirectorOfAnother

### 27. hub: a Gateway with a relay does not offer the method

`src/CcDirector.Gateway/Streaming/DirectorHub.cs`

- unit: 2 red, 543 green
  - RequestLicenseApprovalHubTests.AGatewayThatRelaysNoApprovals_DoesNotOfferTheMethod_AndRefusesACallToI
  - RequestLicenseApprovalHubTests.GatewayCapabilities_ListRequestLicenseApproval
- hosted: 1 red, 11 green
  - LicenseSignInHostedTests.Hello_OffersRequestLicenseApproval

### 28. guard: a session key may not read the approvers

`src/CcDirector.Gateway/Util/SessionKeyGuard.cs`

- unit: 4 red, 541 green
  - LicenseGuardTests.TheGuard_AllowsASessionKey_ExactlyTheLicenseReads(method
  - LicenseGuardTests.TheGuard_AllowsTheApproversRead_ByTheSameConstantTheEndpointMapsItWi
- hosted: 1 red, 11 green
  - LicenseSignInHostedTests.TheApproverRoutes_AreTheOwnersToWrite_AndASessionKeyMayOnlyRead

### 29. fold: add a spare is offered on a machine with no Director connected

`src/CcDirector.Gateway/Licenses/LicenseSignInFold.cs`

- unit: 1 red, 544 green
  - LicenseSignInEndpointsTests.Overview_OffersNeither_OnAMachineWhoseDirectorIsNotConnected_WithTheSentenceTheRouteRefusesWi

### 30. fold: renew now is offered while a sign-in for that account runs

`src/CcDirector.Gateway/Licenses/LicenseSignInFold.cs`

- unit: 2 red, 543 green
  - LicenseSignInEndpointsTests.Overview_WhileASignInIsRunning_ShowsItOnItsMachine_AndDoesNotOfferToRenewThatAccoun
  - PushLicenseUsageHubTests.GatewayCapabilities_WithNoLicenseIngest_DoNotOfferPushLicenseUsage

### 31. fold: an approver whose Director is not connected reads as ready

`src/CcDirector.Gateway/Licenses/LicenseSignInFold.cs`

- unit: 1 red, 544 green
  - LicenseSignInEndpointsTests.AnApprover_WhoseDirectorIsNotConnected_SaysSo_AndCannotApproveNo
- hosted: 1 red, 11 green
  - LicenseSignInHostedTests.ARequest_WhoseApprovingMachineHasNoDirectorConnected_IsAnsweredAtOnce_RefusedApproverOffline

### 32. routes: add a spare is mapped as a plain request delegate

`src/CcDirector.Gateway/Api/LicenseSignInEndpoints.cs`

- hosted: 3 red, 9 green
  - LicenseSignInHostedTests.ASignIn_OnAMachineOfAnotherAccount_IsRefusedAsUnknown_AndThatMachinesDirectorIsSentNothing
  - LicenseSignInHostedTests.ASignIn_OnAMachineWithNoDirectorConnected_IsRefusedWithAFinishedSentence_AndNothingIsWritten
  - LicenseSignInHostedTests.AddASpare_IsAcceptedWhileTheDirectorStillHoldsTheCommand_AndItsOutcomeLandsInTheLog
