# Track C, Developer C1, pull request 1 - the approver contract and the registration

17 October 2026, by the Track C Developer for approver registration. Built to the Track C design. This pull request
is the contract Developer C2 calls and the Gateway rule that decides what a Director's approver checks may store.
Pull request 2 (the plain-English readiness sentence, the approvers route's new fields, Track A's function) follows.

## What was built

- **The contract** (`src/CcDirector.Gateway.Contracts/LicenseApproverMessages.cs`): the six state words
  (`LicenseApproverStates`, with the five a Director may report in `Reportable`), the two source words, the hub method
  names, the 32-row limit, the 200-character detail limit, the 26-hour not-checked window
  (`LicenseApproverContract.NotCheckedAfter`, the one named constant), `LicenseApproverAssignments` and its row,
  `LicenseApproverCheckReport` and its row, and the answer to a report (`LicenseApproverCheckAnswer`: an outcome word and,
  for a refusal, a reason word, per row). The answer is an addition to the design's shape: the design names no return
  type, and a per-row answer lets the Director log why a registration was refused. A Director may ignore it.
- **Two hub methods** on `DirectorHub`: `GetLicenseApproverAssignments()` and `ReportLicenseApproverChecks(report)`.
  Both are refused before Hello, take the tenant and the Director from the connection's binding, run in the tenant's
  scope, and are offered in the Hello answer only by a Gateway that keeps registrations.
- **`LicenseApproverRegistration`** (`src/CcDirector.Gateway/Licenses/`): the rule. The machine and operating system
  user are the Licenses read model's record of which machine the calling Director last reported for, as the approval
  relay looks them up - never anything in the report. Each report is decided under the approver store's lock, so an
  owner's change can never land between a decision and its write.
- **`LicenseApproverStore`** extended: each record gains its source and its latest check (state, when, who the page
  showed, the Director's sentence). An old record with no source reads as owner-set. The owner removing a registered
  record keeps a removal record (account, machine, user, profile) in its own tenant setting
  (`license_approver_removals`, at most 64, oldest dropped first); the owner setting that account's approver again clears
  it. `LicenseApproverHealth.StateOf` is the not-checked rule in one place.
- **`missions/license-switching/contract.md` section 9** documents all of it.

## Why there is no separate tenant guard

The design names "only where license switching automation is allowed" as a refusal. There is no Gateway-side record
of which DevThrottle accounts allow automation: the only switch is each Director's own `licenseSwitching.enabled`
setting, off by default, and a Director with it off sends no usage reports at all. So the rule that only the Director
reporting licenses for a machine may register, together with the rule that the account must be one the tenant's
machines hold, already binds every registration to a machine where the owner switched automation on - and the
machine's offer setting is off by default on top of that. A Gateway check wired to "this account has machines
reporting" would be true whenever those two rules pass, so it would test nothing. The Track C Tech Lead ruled to leave
it out.

## The tests, in plain words

All are in `src/CcDirector.Gateway.UnitTests/Licenses/`, over the real per-tenant settings and the real Licenses store
on a throwaway SQLite file. Machine A (Director `dir-a`) holds accounts A and B; machine B (Director `dir-b`) holds C.

### `LicenseApproverRegistrationTests` - the rule

| Test | What it sets up | What it proves | What would break without the rule |
|---|---|---|---|
| `Report_CanApproveForAnAccountTheTenantHolds_RegistersTheProfile_OnTheMachineAndUserTheGatewayHasForTheDirector` | `dir-a` reports profile "work" can approve account A | The record is stored as registered, naming machine A and its user as the Gateway knows them, with the check's state, time, shown account and sentence | Nothing would register at all, or the record would carry the wrong machine |
| `Report_FromADirectorThatReportsForNoMachineOfTheTenant_RefusesEveryRow_AndStoresNothing` | A Director that never reported licenses sends two good rows | Both are refused `not-the-reporting-director`; nothing is stored | Any connected Director could make itself the approver of any account |
| `Report_FromADirectorThatNoLongerReportsForItsMachine_IsRefused_TheDirectorThatReportsNowIsTheOne` | A new Director takes over machine A's reports; the old one reports a check | The old one is refused; the new one registers | A Director that lost its machine could still register for it |
| `Report_FromTheSameDirectorIdInAnotherTenant_ReachesNothingOfThisTenant` | `dir-a`'s identifier reports into tenant two | Refused there, and nothing is stored in either tenant | One DevThrottle account could write another's approvers |
| `Report_ForAnAccountNoMachineOfTheTenantHolds_IsRefused_AndStoresNothing` | A can-approve row for an account no machine holds | Refused `account-not-held` | Approver records could be made for arbitrary accounts |
| `Report_ForAnAccountHeldOnlyByAnotherMachineOfTheTenant_Registers` | Machine A proves it can approve account C, held only by machine B | Registered: the rule is "the tenant holds it", not "this machine holds it" | The approver machine could only approve its own sign-ins, which is the opposite of the point |
| `Report_FromAMachineThatDoesNotOfferItsProfiles_RegistersNothing` | A report with `offersApprovers` false | Refused `not-offered` | A machine never opted in would become an approver |
| `Report_FromAMachineThatDoesNotOffer_StillUpdatesTheHealthOfARecordTheOwnerSetOnIt` | The owner set machine A's "work" for A; machine A, not offering, reports it needs a fresh sign-in | Health updated, record stays owner-set | An owner-set approver on a machine that does not offer would never be checked |
| `Report_ForAnAccountWhoseApproverTheOwnerSet_NeverOverwritesIt` | The owner set machine B for A; machine A proves it can approve A | Refused `owner-set`; the owner's record is unchanged | A registration would overwrite the owner's choice |
| `Report_ForAnOwnerSetRecordThatHasNotBeenCheckedFor26Hours_StillNeverOverwritesIt` | As above, three days later | Still refused `owner-set` | The not-checked rule would quietly hand the owner's record to a registration |
| `Report_ForARegistrationTheOwnerRemoved_IsRefused_UntilTheOwnerSetsAnApproverForThatAccountAgain` | Register, owner removes, report again; then the owner sets and removes the account, report again | Refused `owner-removed` while the removal stands; registered again once the owner has set that account | The owner's removal would be undone by the next daily check, or would last for ever |
| `Report_TheOwnersRemovalBindsOnlyThatProfile_AnotherProfileOfTheMachineMayRegister` | Owner removes the "work" registration; machine A proves "personal" can approve | Registered: the removal names one account, machine, user and profile | A removal would block a whole machine |
| `Report_AgainstARegisteredIncumbentThatCanApprove_KeepsTheIncumbent` | Machine A registered; machine B proves it can approve too | Refused `healthy-incumbent`; machine A stays | Two machines would swap the record back and forth every day |
| `Report_AgainstARegisteredIncumbentWhoseLastCheckSaysItCannotApprove_ReplacesIt` | Machine A registered, then reports itself signed out; machine B proves it can approve | Machine B replaces it | An approver that cannot approve would block a working one |
| `Report_AgainstARegisteredIncumbentCheckedExactly26HoursAgo_KeepsIt_AndOneMinuteLaterReplacesIt` | Machine A registered at T; machine B reports at T+26h and at T+26h+1m | Kept at the boundary, replaced after it | An absent incumbent would block for ever, or a healthy one would be replaced early |
| `Report_ANewRegistrationWhenTheTenantKeepsTheMostApprovers_IsRefused` | 64 owner records for other accounts; a registration for A | Refused `too-many-approvers` | The setting could grow without bound |
| `Report_AHealthRowForARecordNamingTheCallersMachineAndProfile_UpdatesItsHealth_WhoeverMadeIt` | Owner set machine A "work"; machine A reports wrong account | All four health fields updated, source stays owner | Owner-set approvers would never get a health state |
| `Report_AHealthRowForARecordNamingAnotherMachine_IsRefused_AndTheRecordIsUnchanged` | Owner set machine B; machine A reports signed out for A | Refused `record-names-another-approver`; no health written | Any machine could mark another machine's approver unhealthy |
| `Report_AHealthRowForAnotherProfileOfTheSameMachine_IsRefused` | Owner set machine A "work"; machine A reports on "personal" | Refused, no health written | A check of one profile would be read as the health of another |
| `Report_ARowThatProvesNothingForAnAccountWithNoRecord_StoresNothing` | Signed-out rows, with and without an account, for an account with no record | Nothing stored | Unhealthy profiles would become approver records |
| `Report_WithMoreRowsThanAllowed_IsRefusedWhole_AndStoresNothing` | 33 good rows | The call is refused with the limit sentence; nothing stored | A Director could flood the store |
| `Report_WithExactlyTheMostRowsAllowed_IsTaken` | 32 rows | Taken: the first registers, the rest update its health | The limit would be off by one |
| `Report_MissingOrWithNoRows_IsRefused` | A null report, a report with no row list | Refused; nothing stored | A malformed call would crash or be read as "no checks" |
| `Report_ARowOfTheWrongShape_IsRefusedWhole_AndStoresNothing` (15 cases) | No profile; a profile with characters a profile cannot have; a profile over 64 characters; an account or shown account that is not an email; an account too long; an unknown state; `not-checked` reported; a check dated more than 5 minutes ahead; a check older than 26 hours; a detail over 200 characters; a detail with a line break; can-approve with no account; can-approve showing another account; a null row | Each is refused `invalid-row`, its account is not echoed, nothing is stored | Bad input would be stored, said back in a sentence, or handed to a command on the approving machine |
| `Report_ARefusedRow_DoesNotStopTheOtherRowsOfTheSameReport` | A bad row and a good row | The bad one refused, the good one registered | One bad profile would stop every check of a machine |
| `Report_AccountsAreComparedLowerCased_AndTheProfileTrimmed` | Mixed-case accounts, a padded profile | Stored lower-cased and trimmed, matching the owner's form | The same account would be two records |
| `Report_LogsEveryAcceptedAndRefusedRow_WithTheAccountAndTheMachine_AndNeverTextThatWasNotChecked` | A registration, an invalid row carrying a line break and a marker word, an account not held | Each row has a log line with the machine, the account (or `-` when unchecked), the outcome and the reason; the marker never appears | Refusals would be silent, or a payload could write fake log lines |
| `Assignments_AreTheRecordsThatNameTheCallersMachineAndUser_OwnerSetAndRegistered_AndNoOthers` | An owner record and a registered record on machine A, an owner record on machine B | Machine A's Director gets exactly its two, with their sources | A Director would check profiles it does not hold, or miss one |
| `Assignments_ForADirectorThatReportsForNoMachineOfTheTenant_AreEmpty` | A stranger Director; `dir-a` in another tenant | Both get nothing | Records would leak to Directors that do not hold them |

### `LicenseApproverStoreTests` - what is kept (new and changed tests)

| Test | What it proves |
|---|---|
| `WhatIsStored_IsExactlyTheFieldsOfTheContract_WhoMadeTheRecordAndItsLastCheck` | The stored record has exactly the nine fields, and an owner's record says `owner`. Changed from the four-field test. |
| `ARecordStoredBeforeSourcesExisted_ReadsAsOwnerSet_AndNeverChecked` | A record written before this change reads as owner-set and `not-checked` - so no registration can overwrite it, and it is never read as healthy. |
| `ARecordWhoseSourceIsNotASourceWord_IsAnError` | A stored source the code does not know is an error, never guessed. |
| `Remove_OfARegisteredRecord_LeavesARemovalRecordForThatAccountMachineUserAndProfile` | The owner's removal of a registration is remembered, with when. |
| `Remove_OfAnOwnerSetRecord_LeavesNoRemovalRecord` | Removing the owner's own record leaves nothing behind. |
| `Set_ClearsTheRemovalRecordsOfThatAccount_AndOnlyThatAccount` | The owner setting one account clears only that account's removals. |
| `Removals_KeepTheNewestUpToTheMost` | 65 removals keep the newest 64. |
| `Set_TheVeryProfileThatWasChecked_KeepsItsLastCheck_AndMakesItOwnerSet` | The owner adopting a registered record keeps its health (machine compared without case) and takes it over. |
| `Set_AnotherProfile_StartsNeverChecked` | The owner naming a different profile does not inherit another profile's health. |
| `StateOf_ARecordCheckedExactly26HoursAgo_IsItsState_AndOneSecondLaterIsNotChecked` | The not-checked boundary, and that the constant is 26 hours. |
| `Change_ThatLeavesMoreThanTheMostApprovers_IsAnError_AndWritesNothing` | The store's own limit holds even if a rule above it were wrong. |

### `LicenseApproverHubTests` - the hub

| Test | What it proves |
|---|---|
| `BothMethods_BeforeHello_AreRefused_AndNothingIsStored` | No call is taken from a connection that has not said who it is. |
| `ReportLicenseApproverChecks_FromTheDirectorThatReportsForTheMachine_Registers_AndItsAssignmentsListIt` | End to end through the real hub: the bound Director registers, and its assignments then list the record. |
| `ReportLicenseApproverChecks_OnAConnectionBoundAsAnotherDirector_RegistersNothing` | The connection's binding decides who is asking. |
| `ReportLicenseApproverChecks_WithTooManyRows_FailsTheCall_WithTheRegistrationsSentence` | A report past the limit fails the call with a sentence that quotes nothing of it. |
| `Hello_OffersBothMethods_WhenTheGatewayKeepsRegistrations` | A Director is told it may call them. |
| `AGatewayThatKeepsNoRegistrations_OffersNeitherMethod_AndRefusesBoth` | A Director is never told to report into a Gateway that drops reports. |
| `TheHubMethodNames_AreTheContractsNames` | The contract constants and the hub's method names cannot drift. |

### Do the rules carry weight

With the `owner-set` and `healthy-incumbent` checks disabled in the source, four registration tests went red
(the two owner-set tests and the two incumbent tests); the source was restored before the gate.

## Gate

`.\scripts\test-local.ps1 -Parked` at commit `068c566b8` (the tree held only the uncommitted section 9 of
`contract.md`, a document):

```
CcDirector.Core.UnitTests                outcome=Completed    total=2127   executed=2127
CcDirector.Avalonia.Tests                outcome=Completed    total=1063   executed=1063
CcDirector.Engine.Tests                  outcome=Completed    total=85     executed=85
CcDirector.HostedAgent.Tests             outcome=Completed    total=85     executed=85
CcDirector.Launcher.Tests                outcome=Completed    total=214    executed=214
CcDirector.Terminal.Avalonia.Tests       outcome=Completed    total=71     executed=71
CcDirector.Reclaim.Tests                 outcome=Completed    total=305    executed=305
CcDirector.DeliveryQualification.Tests   outcome=Completed    total=17     executed=17
cc-director-setup.Tests                  outcome=Completed    total=39     executed=39
cc-director-setup-engine.Tests           outcome=Completed    total=884    executed=884
cc-director-setup-cli.Tests              outcome=Completed    total=74     executed=74
CcDirector.Gateway.Tests.shard1of4       outcome=Completed    total=412    executed=412
CcDirector.Gateway.Tests.shard2of4       outcome=Completed    total=480    executed=480
CcDirector.Gateway.Tests.shard3of4       outcome=Completed    total=403    executed=403
CcDirector.Gateway.Tests.shard4of4       outcome=Completed    total=469    executed=469
CcDirector.Core.Tests                    outcome=Completed    total=4538   executed=4538
CcDirector.Gateway.UnitTests             outcome=Failed       total=13891  executed=13891   (7 failed)
```

The seven Gateway unit test failures are not this change's. Six are in `PromptOnlyWhenWaitingForInputTests` and
`FleetManagerEventServiceTests`; the re-run of only the failed tests (`-RerunFailed`, clean tree, same commit) failed
the same six again, and the same six fail on an untouched checkout of `origin/main` at `08dec1140` (134 run, 6
failed). They come from the "look before typing" change already on main, and a fix branch for them exists
(`fix/4072-six-gateway-tests`). The seventh, `FleetManagerEventServiceTests.ADirectorOlderThanTheIdleCheck_IsSentNothing_AndTheEventsWait`,
failed once and passed on the re-run: it failed on correct code and belongs to the same area. Every license test
passed: 888 in the license namespace, including the 111 of the three classes above.
