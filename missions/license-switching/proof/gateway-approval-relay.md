# Proof - the Gateway relays sign-in approvals and starts sign-ins from the Cockpit

License Switching, phase 2 (Gateway), contract section 8, parts 1 to 3: who approves each account (8.3), the approval
relay (8.3), and Add a spare and Renew now from the Cockpit (8.4). Part 4, the owner's email for a renewal that keeps
failing (8.1), is NOT in this change and no migration is: it follows in its own pull request, on the stored failed
renewal that reached main in pull request 3936.

Branch `license-switching-gw-approval`, rebased onto `origin/main` at `5bd850478`, which has pull request 3936 (Switch
now, the renewal date, the stored failed renewal and its migration). The runs below are on commit `e00bd793d`; the only
commit after it adds this file.

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
- **The four writes are for a signed-in person only**, by the same one rule as the two writes main already has
  (`FleetManagerOwnerDevice.Require`): the owner's own phone or browser. A session key, a Director's own key and the
  shared machine token are refused before the body is read; a body that is not JSON is refused `400`.
- **No migration, no new table, no new column.** The entity file changed by a comment only.
- **The document.** `missions/license-switching/cockpit-api.md`, new section 7: every route and shape, each with a
  real answer produced by running the handlers.

## What was run

Each run in the foreground, one at a time, on a clean tree, on commit `e00bd793d`.

| Run | Result |
|---|---|
| `.\scripts\test-local.ps1` | GREEN. 10 suites, 4,562 tests, every suite Completed. |
| `.\scripts\test-local.ps1 -Parked` | 16 suites, 23,615 tests. 14 Completed, among them `Gateway.UnitTests` 11,378 of 11,378. Three tests red, none of this change (below). 11 minutes. |
| `.\scripts\test-local.ps1 -RerunFailed` | The three again, alone: two red again, one green. |
| `.\scripts\test-database.ps1` | GREEN. 71 executed against the throwaway PostgreSQL, none failed, none skipped. |

**The three reds of the `-Parked` run, and why none is of this change:**

- `ContextLessRouteCensusTests.The_hosted_context_less_route_set_is_exactly_the_ruled_census` and
  `...The_selfhost_context_less_route_set_adds_only_the_hosted_denied_families` (`Gateway.Tests`, red again when run
  alone). The census still lists `GET /cron/jobs/{id}` and `GET /cron/jobs/{id}/runs`, and those two routes are not in
  what it finds any more. `git blame` puts the two route lines at `989d4439c`, pull request 3920 (shifts on the screens),
  which is on main. This change touches no cron file and not the census. It is red on main and needs its own fix there.
- `CircularTerminalBufferTests.ConcurrentAccess_IsThreadSafe` (`Core.Tests`): red once, green when run again alone on
  the same commit. A flaky test of the terminal buffer, which this change does not touch.

**Before the rebase** the same three runs were made on the branch as it was cut from `30e468c18` (commit `5e9f44083`):
default and `-Parked` red only in the two tests the brief named as known reds on main (both since fixed on main by
pull request 3937), and the database tests green, 70 of 70.

**One red of this change was found by the first `-Parked` run and fixed.** `GatewayHttpFailureLineShapeTests` failed:
four failure lines of the new routes said "GET approvers FAILED" where the rule is "VERB /route". The lines were
corrected and the run made again.

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
(the tree was checked clean before and after each). 34 breaks; every one turned at least one test red. "unit" is the
license tests of `Gateway.UnitTests` for this change (545 before the rebase), "hosted" is `LicenseSignInHostedTests`
(12); a break was run against the hosted tests where the behaviour passes through the host.

Breaks 0 to 32 were run before the rebase onto main (0 to 5 before a power cut, on `4ed05750a`; the rest on
`415120e14`); break 33 after it, on `e00bd793d`. The rebase changed none of the product lines the breaks touch, except
that the routes gained the owner-only check break 33 removes.

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

### 33. routes: add a spare is not limited to the owner's own device

`src/CcDirector.Gateway/Api/LicenseSignInEndpoints.cs`

- hosted: 1 red, 11 green
  - LicenseSignInHostedTests.TheApproverRoutes_AreTheOwnersToWrite_AndASessionKeyMayOnlyRead

## The review round (13 October 2026)

The review of this pull request (`../reviews/3943-review.md`) asked for ten things; `../reviews/3943-answers.md`
answers each. This section is the proof for what that round changed. It does not replace anything above, except
where it says so.

**What above no longer holds.** Four kinds of test named in the breaks above were rewritten in this round, because
the behaviour they held is the behaviour the review found wrong. Where a break above lists one of them as red, read
the name that replaced it:

| Named above | Replaced by |
|---|---|
| `Accept_OnePastTheLimitOfApprovalsInFlight_IsRefusedAtOnce_AndAsksNoMachine` | `Accept_EveryRequestPastTheLimit_IsRefusedOnTheCall_AndTheHundredthHoldsNothingMoreThanTheFirst` |
| `Relay_ARequestWithNoAccountOrNoAddress_AnswersRefused_AndAsksNoMachine` | `Accept_ARequestWhoseAccountOrAddressIsNotOne_IsRefusedOnTheCall_AndHoldsAndSendsAndQuotesNothing` |
| every `SettleUnanswered_...` test | the `Settle_...` tests of `LicenseSignInServiceTests` |
| `Start_SettlesARowPastItsTimeFirst_SoItNeverBlocksANewSignIn` | `ARowThatCannotBeSettledYet_StillBlocksASecondSignIn_AndOnceSettledItNoLongerDoes` |

No break above was run again in this round; the breaks below are the ones made against the code as it now is.

### The runs, on the final head

| Run | Result |
|---|---|
| `dotnet test src/CcDirector.Gateway.UnitTests --filter "FullyQualifiedName~Licenses"` | 709 passed, 0 failed |
| `dotnet test src/CcDirector.Gateway.Tests --filter "FullyQualifiedName~License"` | 39 passed, 0 failed |
| `.\scripts\test-database.ps1` | GREEN - 72 executed against the throwaway PostgreSQL, none failed, none skipped |
| `.\scripts\test-local.ps1` | GREEN at `efd7988f3`, tree clean, every suite `outcome=Completed` |
| `dotnet test src/CcDirector.Gateway.UnitTests`, the whole suite | 11,473 passed, 0 failed, 4 minutes 59 seconds |

`.\scripts\test-local.ps1 -Parked` was NOT run in this round. The default run covers no Gateway test at all, which
is why the whole Gateway unit suite was run by hand. Of the hosted suite only the 39 license tests ran.

### The breaks of this round

Each break was made on a committed tree, the tests were built and run, and the file was put back with
`git checkout`. "unit" is the 709 license tests of `Gateway.UnitTests`; "hosted" is the 17 sign-in tests of
`Gateway.Tests`. A number after a test's name is how many of its cases went red.

Breaks 1 to 16, 18 to 24 and 27 to 30 were run on `6805b5cee`. Break 17 was run on `2e916e8aa`, after its test was
given a limit: on `6805b5cee` the break made that test wait for ever and the run was ended by the hang limit, which
is a test that cannot fail, so the test was changed and the break run again. Break 25 was run on `668af1b54`.

#### R1. the limit refuses nothing

`src/CcDirector.Gateway/Licenses/LicenseApprovalRelay.cs`

- unit: 2 red, 707 green
  - LicenseApprovalRelayTests.Accept_EveryRequestPastTheLimit_IsRefusedOnTheCall_AndTheHundredthHoldsNothingMoreThanTheFirst
  - RequestLicenseApprovalHubTests.RequestLicenseApproval_PastTheAccountsLimit_FailsTheCall_AndTheGatewayHoldsNothingForIt

#### R2. a place is not given back when a relay ends

`src/CcDirector.Gateway/Licenses/LicenseApprovalRelay.cs`

- unit: 1 red, 708 green
  - LicenseApprovalRelayTests.Accept_EveryRequestPastTheLimit_IsRefusedOnTheCall_AndTheHundredthHoldsNothingMoreThanTheFirst

#### R3. the limit is counted over every account together

`src/CcDirector.Gateway/Licenses/LicenseApprovalRelay.cs`

- unit: 1 red, 708 green
  - LicenseApprovalRelayTests.Accept_EveryRequestPastTheLimit_IsRefusedOnTheCall_AndTheHundredthHoldsNothingMoreThanTheFirst

#### R4. the length of the address is not looked at on the call

`src/CcDirector.Gateway/Licenses/LicenseApprovalRelay.cs`

- unit: 3 red, 706 green
  - LicenseApprovalRelayTests.Accept_ARequestWhoseAccountOrAddressIsNotOne_IsRefusedOnTheCall_AndHoldsAndSendsAndQuotesNothing (2 cases)
  - RequestLicenseApprovalHubTests.RequestLicenseApproval_WithAnAccountOrAnAddressThatIsNotOne_FailsTheCall_WithASentenceThatQuotesNothing

#### R5. the account is not checked to be an email on the call

`src/CcDirector.Gateway/Licenses/LicenseApprovalRelay.cs`

- unit: 6 red, 703 green
  - LicenseApprovalRelayTests.Accept_ARequestWhoseAccountOrAddressIsNotOne_IsRefusedOnTheCall_AndHoldsAndSendsAndQuotesNothing (5 cases)
  - RequestLicenseApprovalHubTests.RequestLicenseApproval_WithAnAccountOrAnAddressThatIsNotOne_FailsTheCall_WithASentenceThatQuotesNothing

#### R6. the request is logged before it is checked

`src/CcDirector.Gateway/Licenses/LicenseApprovalRelay.cs`

- unit: 12 red, 697 green
  - LicenseApprovalRelayTests.Accept_ARequestWhoseAccountOrAddressIsNotOne_IsRefusedOnTheCall_AndHoldsAndSendsAndQuotesNothing (12 cases)

#### R7. a relay that fails answers nobody

`src/CcDirector.Gateway/Licenses/LicenseApprovalRelay.cs`

- unit: 2 red, 707 green
  - LicenseApprovalRelayTests.Relay_ASendToTheApprovingDirectorThatThrows_StillAnswersTheDirectorThatAsked_AndTheLogHasNoAddress
  - LicenseApprovalRelayTests.Relay_AnApproversSettingThatCannotBeRead_StillAnswersTheDirectorThatAsked

#### R8. a relay that fails logs the message of the failure

`src/CcDirector.Gateway/Licenses/LicenseApprovalRelay.cs`

- unit: 1 red, 708 green
  - LicenseApprovalRelayTests.Relay_ASendToTheApprovingDirectorThatThrows_StillAnswersTheDirectorThatAsked_AndTheLogHasNoAddress

#### R9. a scope that cannot be entered leaves no line

`src/CcDirector.Gateway/Licenses/LicenseApprovalRelay.cs`

- unit: 1 red, 708 green
  - LicenseApprovalRelayTests.Relay_AScopeThatCannotBeEntered_IsLoggedByItsTypeOnly_AndLeavesNothingInFlight

#### R10. the wait for the approving Director is not ended at the deadline

`src/CcDirector.Gateway/Licenses/LicenseApprovalRelay.cs`

- unit: 1 red, 708 green
  - LicenseApprovalRelayTests.Relay_WhenTheSignInsDeadlineIsSoonerThanTheGatewaysWait_WaitsOnlyUntilTheDeadline_AndAnswersExpired

#### R11. a wait that ended at the deadline is not answered expired

`src/CcDirector.Gateway/Licenses/LicenseApprovalRelay.cs`

- unit: 1 red, 708 green
  - LicenseApprovalRelayTests.Relay_WhenTheSignInsDeadlineIsSoonerThanTheGatewaysWait_WaitsOnlyUntilTheDeadline_AndAnswersExpired

#### R12. a request arriving exactly at its deadline is relayed

`src/CcDirector.Gateway/Licenses/LicenseApprovalRelay.cs`

- unit: 1 red, 708 green
  - LicenseApprovalRelayTests.Relay_ARequestArrivingExactlyAtItsDeadline_AnswersExpired_AndAsksNoMachine

#### R13. an unanswered sign-in is never asked again

`src/CcDirector.Gateway/Licenses/LicenseSignInService.cs`

- unit: 8 red, 701 green
  - LicenseSignInServiceTests.ARowThatCannotBeSettledYet_StillBlocksASecondSignIn_AndOnceSettledItNoLongerDoes
  - LicenseSignInServiceTests.SettleEveryTenant_SettlesEachAccountsRowsInItsOwnScope_AndOneAccountsFailureDoesNotStopAnother
  - LicenseSignInServiceTests.Settle_ADirectorThatGivesNoAnswerWhenAskedAgain_LeavesItToTheReport_OpenUntilOneComes (4 cases)
  - LicenseSignInServiceTests.Settle_ADirectorThatNeverSawTheSignIn_SaysItStartedNothing_AndTheRowEndsFailedExpired
  - LicenseSignInServiceTests.Settle_AsksTheDirectorAgainWithTheSameIdentifier_InItsOwnScope_AndEndsTheRowWithItsStoredAnswer

#### R14. an unanswered sign-in is settled from a report of any age

`src/CcDirector.Gateway/Licenses/LicenseSignInService.cs`

- unit: 7 red, 702 green
  - LicenseSignInServiceTests.ARowThatCannotBeSettledYet_StillBlocksASecondSignIn_AndOnceSettledItNoLongerDoes
  - LicenseSignInServiceTests.Settle_ADirectorThatGivesNoAnswerWhenAskedAgain_LeavesItToTheReport_OpenUntilOneComes (4 cases)
  - LicenseSignInServiceTests.Settle_AReportAcceptedWhileTheSignInCouldStillBeRunning_SettlesNothing
  - LicenseSignInServiceTests.Settle_TheTunnelDroppedAndNoReportSince_StaysOpen_HoweverLongItIs

#### R15. a renewal is judged by the machine clock against the Gateway clock

`src/CcDirector.Gateway/Licenses/LicenseSignInService.cs`

- unit: 2 red, 707 green
  - LicenseSignInServiceTests.Settle_ARenewalThatWorked_OnAMachineWhoseClockIsAMinuteBehind_EndsRenewed
  - LicenseSignInServiceTests.Settle_ARenewal_WhoseSignInIsStillTheOneFromBeforeItWasAsked_EndsFailed_WhateverTheClocksSay

#### R16. a sign-in is asked again while a Director could still be running it

`src/CcDirector.Gateway/Licenses/LicenseSignInService.cs`

- unit: 1 red, 708 green
  - LicenseSignInServiceTests.Settle_BeforeNoDirectorCouldStillBeRunningIt_AsksNobody_AndLeavesTheRowOpen

#### R17. a sign-in in flight in this process is settled anyway

`src/CcDirector.Gateway/Licenses/LicenseSignInService.cs`

- unit: 1 red, 708 green
  - LicenseSignInServiceTests.Settle_NeverSettlesASignInWhoseAnswerMayStillCome

#### R18. the sign-in time from before the ask is not kept

`src/CcDirector.Gateway/Licenses/LicenseSignInService.cs`

- unit: 2 red, 707 green
  - LicenseSignInServiceTests.ASignInRow_KeepsTheSignInTimeFromBeforeTheAsk_AndThePageIsNeverToldItAsAStart
  - LicenseSignInServiceTests.Settle_ARenewal_WhoseSignInIsStillTheOneFromBeforeItWasAsked_EndsFailed_WhateverTheClocksSay

#### R19. the start logs the machine identifier before it is checked

`src/CcDirector.Gateway/Licenses/LicenseSignInService.cs`

- unit: 2 red, 707 green
  - LicenseSignInServiceTests.Start_NothingTheCallerSentIsLoggedBeforeItIsChecked (2 cases)

#### R20. one account failing stops the settling of the others

`src/CcDirector.Gateway/Licenses/LicenseSignInService.cs`

- unit: 1 red, 708 green
  - LicenseSignInServiceTests.SettleEveryTenant_SettlesEachAccountsRowsInItsOwnScope_AndOneAccountsFailureDoesNotStopAnother

#### R21. a machine name with a control character is stored

`src/CcDirector.Gateway/Licenses/LicenseApproverStore.cs`

- unit: 10 red, 699 green
  - LicenseApproverStoreTests.Set_ANameOrAccountHoldingACharacterThatIsNotText_IsRefused_NothingIsStored_AndTheLogDoesNotQuoteIt (10 cases)

#### R22. a user with a control character is stored

`src/CcDirector.Gateway/Licenses/LicenseApproverStore.cs`

- unit: 10 red, 699 green
  - LicenseApproverStoreTests.Set_ANameOrAccountHoldingACharacterThatIsNotText_IsRefused_NothingIsStored_AndTheLogDoesNotQuoteIt (10 cases)

#### R23. an account email with a control or format character is taken

`src/CcDirector.Gateway/Licenses/LicenseApproverStore.cs`

- unit: 8 red, 701 green
  - LicenseApprovalRelayTests.Accept_ARequestWhoseAccountOrAddressIsNotOne_IsRefusedOnTheCall_AndHoldsAndSendsAndQuotesNothing (2 cases)
  - LicenseApproverStoreTests.Set_ANameOrAccountHoldingACharacterThatIsNotText_IsRefused_NothingIsStored_AndTheLogDoesNotQuoteIt (5 cases)
  - LicenseSignInServiceTests.Start_NothingTheCallerSentIsLoggedBeforeItIsChecked

#### R24. the approver store logs what it was sent before checking it

`src/CcDirector.Gateway/Licenses/LicenseApproverStore.cs`

- unit: 30 red, 679 green
  - LicenseApproverStoreTests.Set_ANameOrAccountHoldingACharacterThatIsNotText_IsRefused_NothingIsStored_AndTheLogDoesNotQuoteIt (30 cases)

#### R25. the page is told the kept sign-in time as when the sign-in started

`src/CcDirector.Gateway/Licenses/LicenseSignInFold.cs`

- unit: 1 red, 708 green
  - LicenseSignInServiceTests.ASignInRow_KeepsTheSignInTimeFromBeforeTheAsk_AndThePageIsNeverToldItAsAStart

#### R26. the page is given a length of time worked out from the kept sign-in time

`src/CcDirector.Gateway/Licenses/LicenseDisplayFold.cs`

- unit: 0 red, 709 green - NOT CAUGHT. The line broken is one no sign-in row reaches: a sign-in row is folded by
  `LicenseSignInFold`, which answers `null` for these fields itself. The guard was removed from the product, so
  there is nothing left for this break to break.

#### R27. the hub lets the limit refusal through as an unhandled failure

`src/CcDirector.Gateway/Streaming/DirectorHub.cs`

- unit: 1 red, 708 green
  - RequestLicenseApprovalHubTests.RequestLicenseApproval_PastTheAccountsLimit_FailsTheCall_AndTheGatewayHoldsNothingForIt

#### R28. the host does not settle unanswered sign-ins in its minute check

`src/CcDirector.Gateway/GatewayHost.cs`

- hosted: 1 red, 16 green
  - LicenseSignInHostedTests.AnUnansweredSignIn_IsAskedAgainByTheHostsMinuteCheck_OverTheTunnel_AndEndsWithTheDirectorsStoredAnswer

#### R29. the four writes are open to every credential the guard lets through

`src/CcDirector.Gateway/Api/LicenseSignInEndpoints.cs`

- hosted: 2 red, 15 green
  - LicenseSignInHostedTests.TheApproverRoutes_AreTheOwnersToWrite_AndASessionKeyMayOnlyRead
  - LicenseSignInWritesSelfHostedTests.TheSharedMachineToken_ReadsWhoApproves_AndIsRefusedAllFourWrites

#### R30. the relay logs the address it is given

`src/CcDirector.Gateway/Licenses/LicenseApprovalRelay.cs`

- hosted: 1 red, 16 green
  - LicenseSignInHostedTests.NoLineOfTheHostsLog_CarriesTheAddressOrTheApproval_AcrossAnApprovedRelayAndADroppedTunnel

### What these breaks do not show

- No break was made in `SessionKeyGuard` for a session key the owner raised. Break R29 removes the routes' own check
  and the raised session key is still refused, which shows the guard is what stops it; `LicenseGuardTests` (above)
  is what holds the guard's list.
- No break was made in `DirectorCommandRouter`, which is not this pull request's code. Break R30 puts the address
  into a line of the relay and the hosted capture catches it, which shows the capture reads the host's real log.
- The PostgreSQL test was not broken. It ran green against a real PostgreSQL, and that is all that is claimed.

## The second review round (13 October 2026)

The re-review (`../reviews/3943-re-review.md`) approved the pull request with seven findings; round 2 of
`../reviews/3943-answers.md` answers each. This section is the proof for what that round changed.

**What in the review round above no longer holds.** `Settle_ADirectorThatNeverSawTheSignIn_SaysItStartedNothing_AndTheRowEndsFailedExpired`
is gone: for a command that had gone out, that answer no longer ends the row. `Settle_TheTunnelDroppedAndNoReportSince_StaysOpen_HoweverLongItIs`
is now `..._StaysOpen_WhileTheMachineWasSeenWithinADay`: after a day the row ends. The "no answer in time" case of
`Settle_ADirectorThatGivesNoAnswerWhenAskedAgain_LeavesItToTheReport_OpenUntilOneComes` is now its own test, because
a timeout no longer leaves it to the report in the same pass.

### The runs, on `a013c0287`

| Run | Result |
|---|---|
| `dotnet test src/CcDirector.Gateway.UnitTests --filter "FullyQualifiedName~Licenses"` | 720 passed, 0 failed |
| `dotnet test src/CcDirector.Gateway.Tests --filter "FullyQualifiedName~License"` | 39 passed, 0 failed |
| `dotnet test src/CcDirector.Core.UnitTests --filter "FullyQualifiedName~License"` | 186 passed, 0 failed |
| `.\scripts\test-local.ps1`, run after the documents were committed | GREEN at `9c97aab07`, tree clean, every suite `outcome=Completed`; `Core.UnitTests` 1,723 executed |

NOT run in this round: `.\scripts\test-local.ps1 -Parked`, `.\scripts\test-database.ps1` and the whole
`Gateway.UnitTests` suite. The default run covers no Gateway test. The identifier pattern is shared with the
Director's code (`CcDirector.ControlApi`, `CcDirector.Core`), which the default run covers.

### The breaks of this round

Each break was made on the clean tree of `a013c0287`, the tests were built and run, and the file was put back with
`git checkout`; the tree was checked clean after each. The unit run is `LicenseSignInServiceTests` and
`LicenseApprovalRelayTests` (122 tests); break S8 also ran `LicenseMessagesCarryNoKeyTests` of `Core.UnitTests` (19).
All ten went red.

#### S1. an answer that is not a replay ends the row, also for a command that had gone out

`src/CcDirector.Gateway/Licenses/LicenseSignInService.cs`

- src/CcDirector.Gateway.UnitTests: 122 tests, 120 passed, 2 failed
  - Settle_AnAnswerThatIsNotAReplay_ForACommandThatHadGoneOut_DoesNotEndTheRow_AndTheLaterReportDoes `(theReportShowsTheSpare: False, expected: "failed")`
  - Settle_AnAnswerThatIsNotAReplay_ForACommandThatHadGoneOut_DoesNotEndTheRow_AndTheLaterReportDoes `(theReportShowsTheSpare: True, expected: "added")`

#### S2. an answer that is not a replay never ends the row, also for a command that never went out

`src/CcDirector.Gateway/Licenses/LicenseSignInService.cs`

- src/CcDirector.Gateway.UnitTests: 122 tests, 121 passed, 1 failed
  - Settle_ACommandThatNeverWentOut_AndADirectorThatSaysItStartedNothing_EndsTheRowFailedExpired

#### S3. a pass whose ask timed out goes on to settle from the report

`src/CcDirector.Gateway/Licenses/LicenseSignInService.cs`

- src/CcDirector.Gateway.UnitTests: 122 tests, 121 passed, 1 failed
  - Settle_AnAskThatGetsNoAnswerInTime_SettlesNothingInThatPass_NotEvenFromAReportThatShowsTheSpare

#### S4. a machine is asked again whatever its clock difference

`src/CcDirector.Gateway/Licenses/LicenseSignInService.cs`

- src/CcDirector.Gateway.UnitTests: 122 tests, 119 passed, 3 failed
  - Settle_AMachineWhoseClockIsWithinAMinuteOfTheMarginOrBeyondIt_IsNotAskedAgain_AndIsSettledFromItsReport `(secondsApart: -600)`
  - Settle_AMachineWhoseClockIsWithinAMinuteOfTheMarginOrBeyondIt_IsNotAskedAgain_AndIsSettledFromItsReport `(secondsApart: 240)`
  - Settle_AMachineWhoseClockIsWithinAMinuteOfTheMarginOrBeyondIt_IsNotAskedAgain_AndIsSettledFromItsReport `(secondsApart: 300)`

#### S5. the one minute short of the margin is not kept: only a machine at the margin itself is not asked

`src/CcDirector.Gateway/Licenses/LicenseSignInService.cs`

- src/CcDirector.Gateway.UnitTests: 122 tests, 121 passed, 1 failed
  - Settle_AMachineWhoseClockIsWithinAMinuteOfTheMarginOrBeyondIt_IsNotAskedAgain_AndIsSettledFromItsReport `(secondsApart: 240)`

#### S6. a slow clock only: a machine whose clock is ahead by more than the margin is asked

`src/CcDirector.Gateway/Licenses/LicenseSignInService.cs`

- src/CcDirector.Gateway.UnitTests: 122 tests, 121 passed, 1 failed
  - Settle_AMachineWhoseClockIsWithinAMinuteOfTheMarginOrBeyondIt_IsNotAskedAgain_AndIsSettledFromItsReport `(secondsApart: -600)`

#### S7. a machine not seen for a day never ends its row

`src/CcDirector.Gateway/Licenses/LicenseSignInService.cs`

- src/CcDirector.Gateway.UnitTests: 122 tests, 120 passed, 2 failed
  - Settle_AMachineThatHasSentNoReportForADay_EndsTheRowFailed_SaysTheMachineHasNotBeenSeen_AndTheSignInCanBeAskedForAgain `(renewal: False)`
  - Settle_AMachineThatHasSentNoReportForADay_EndsTheRowFailed_SaysTheMachineHasNotBeenSeen_AndTheSignInCanBeAskedForAgain `(renewal: True)`

#### S8. the identifier pattern ends with a dollar sign again

`src/CcDirector.Gateway.Contracts/LicenseMessages.cs`

- src/CcDirector.Gateway.UnitTests: 122 tests, 121 passed, 1 failed
  - Accept_ARequestWhoseIdentifierIsNotASignInIdentifier_IsRefusedOnTheCall_AndNothingIsSent `(signInId: "11111111111111111111111111111111\n")`
- src/CcDirector.Core.UnitTests: 19 tests, 18 passed, 1 failed
  - SwitchIdShape_OnlyGuidN_Matches `(switchId: "6f1c2a9e4b7d4e0f9a3c5b8d2e1f7a40\n", expected: False)`

#### S9. a send that throws is logged with its message

`src/CcDirector.Gateway/Licenses/LicenseSignInService.cs`

- src/CcDirector.Gateway.UnitTests: 122 tests, 121 passed, 1 failed
  - ASendThatThrows_AndAnAnswerThatCannotBeRead_AreLoggedByTheirKindOnly_NeverByTheirMessage

#### S10. an answer that cannot be read is logged with the reader's message

`src/CcDirector.Gateway/Licenses/LicenseSignInService.cs`

- src/CcDirector.Gateway.UnitTests: 122 tests, 121 passed, 1 failed
  - ASendThatThrows_AndAnAnswerThatCannotBeRead_AreLoggedByTheirKindOnly_NeverByTheirMessage

### What these breaks do not show

- The line `Start FAILED` of `LicenseSignInService` now logs the kind of failure only, and no test makes `Start`
  throw, so no test holds that one line. The two other lines of finding 6 are held by break S9 and S10.
- In break S8 the case of an identifier followed by a carriage return and a line break stayed green: the old
  pattern refused that one already. Only the single line break is what the change closes.
- No hosted test was added in this round. The 39 hosted license tests ran green and none of them reaches the three
  new rules; those are held by the unit tests above, over the real store on SQLite with a sender the test controls.
- The clock difference the Gateway keeps is nought for a machine within two minutes of the Gateway and the measured
  difference beyond that (5.2 of the contract). It is as old as the machine's last report. A machine whose clock
  moved since then is judged by the old value; the rule that a timed-out ask settles nothing in its pass is what is
  left for that case, and it does not stop the sign-in from starting.
