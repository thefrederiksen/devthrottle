# Track C, Developer C1 - summary for the track report

17 October 2026, by the Track C Developer for approver registration. Built to the Track C design. Five pull requests,
all merged to main:

| Pull request | What it did | Proof and review |
|---|---|---|
| 4109 | Registration: the contract types, the two hub methods, the record's source and health, removal records, the registration rule | `c1-registration.md`, `review-4109.md`, `review-4109-answers.md` |
| 4127 | Readiness: the health sentences, the new approver-row fields, an owner's removal that sticks, Track A's `WhyNotReady` | `c1-readiness.md`, `review-4127.md`, `review-4127-answers.md` |
| 4140 | The approver alarm through the one license alarm mechanism | `c1-alarm.md`, `review-4140.md`, `review-4140-answers.md` |
| 4150 | Main fixed forward: the Cockpit fixtures regenerated, the client approver type given the new fields | (fixture regeneration, no review) |
| this one | The hosted proof of the alarm, and this summary | `review-4148.md`, `review-4148-answers.md` |

## What was built

**A Director registers its own approvers.** A Director with license switching on checks its browser profiles once a
day and reports each one over its hub connection (`ReportLicenseApproverChecks`). It asks which records name its machine
with `GetLicenseApproverAssignments`. The Gateway takes the tenant and the Director from the hub's Hello, and the
machine and user from the license read model (the Director that last reported for that machine), never from the
report. A profile that can approve for an account the tenant holds is registered as that account's approver. A report
on a record that names the caller's machine and profile updates that record's health, whoever made it.

**The owner stays in charge.** A record the owner set is never overwritten by a registration. A registration the
owner removed is never brought back by the next check, until the owner sets an approver for that account again.

**Health is read, never assumed.** Each record carries its last check: can-approve, signed-out, needs-fresh-sign-in,
wrong-account or check-failed, and when. A record never checked, or not checked in 26 hours, is not-checked, and only
can-approve counts as able to approve. The Gateway folds the finished sentences (`healthText`, `whatToDo`) that the
Cockpit shows as they are, and the approver's status is ready only when its Director is connected AND it can approve.
Track A asks `LicenseApproverReadiness.WhyNotReady` whether an account can be signed in.

**An approver that cannot approve is said loudly.** One license alarm per such approver, about its account, raised
after every check report and on the Gateway's once-a-minute check, so an approver nobody checks alarms once its 26 hours
pass. It closes by itself when the approver recovers or is removed. It stays quiet when there is nothing to say.

## Each refusal and its test, in plain words (`LicenseApproverRegistrationTests` unless named)

| Refusal | When | Test |
|---|---|---|
| not-the-reporting-director | The caller is not the Director the Gateway has as reporting for a machine of this tenant, or another Director has reported for that machine since | `Report_FromADirectorThatReportsForNoMachineOfTheTenant_RefusesEveryRow_AndStoresNothing`, `Report_FromADirectorThatNoLongerReportsForItsMachine_IsRefused_TheDirectorThatReportsNowIsTheOne`, `Report_FromTheSameDirectorIdInAnotherTenant_ReachesNothingOfThisTenant` |
| invalid-row | A row of the wrong shape: no profile, a profile of the wrong shape, too long or with a space around it; an account that is not an email or is too long; a shown account that is not an email; an unknown state; not-checked reported by a Director; a check time in the future or over 26 hours old; a detail too long or with a line break; can-approve with no account or showing another; wrong-account showing the account itself; a missing row; a display name of the wrong shape. The whole report is refused and nothing is stored | `Report_ARowOfTheWrongShape_IsRefusedWhole_AndStoresNothing` (one case each), `Report_ADisplayNameOfTheWrongShape_IsRefusedWhole` |
| (the whole report) | More rows than allowed (32), or no report at all | `Report_WithMoreRowsThanAllowed_IsRefusedWhole_AndStoresNothing`, `Report_WithExactlyTheMostRowsAllowed_IsTaken`, `Report_MissingOrWithNoRows_IsRefused` |
| account-not-held | No machine of the tenant holds the account | `Report_ForAnAccountNoMachineOfTheTenantHolds_IsRefused_AndStoresNothing` (and `Report_ForAnAccountHeldOnlyByAnotherMachineOfTheTenant_Registers` for the case that is allowed) |
| not-offered | The machine does not offer its profiles as approvers | `Report_FromAMachineThatDoesNotOfferItsProfiles_RegistersNothing` (its health report on an owner-set record still counts: `Report_FromAMachineThatDoesNotOffer_StillUpdatesTheHealthOfARecordTheOwnerSetOnIt`) |
| owner-set | The account's approver was set by the owner, healthy or not, checked or not | `Report_ForAnAccountWhoseApproverTheOwnerSet_NeverOverwritesIt`, `Report_ForAnOwnerSetRecordThatHasNotBeenCheckedFor26Hours_StillNeverOverwritesIt` |
| owner-removed | The owner removed this registration | `Report_ForARegistrationTheOwnerRemoved_IsRefused_UntilTheOwnerSetsAnApproverForThatAccountAgain`, `Report_TheOwnersRemovalBindsOnlyThatProfile_AnotherProfileOfTheMachineMayRegister`, and on the hosted Gateway `ARegisteredApprover_ComesInOverTheDirectorsHub_ASessionKeyStillCannotRemoveIt_AndTheOwnersRemovalSticks` |
| healthy-incumbent | A registered approver already can approve and was checked within 26 hours | `Report_AgainstARegisteredIncumbentThatCanApprove_KeepsTheIncumbent`, `Report_AgainstARegisteredIncumbentCheckedExactly26HoursAgo_KeepsIt_AndOneMinuteLaterReplacesIt` (and `Report_AgainstARegisteredIncumbentWhoseLastCheckSaysItCannotApprove_ReplacesIt`) |
| record-names-another-approver | A health row for a record on another machine, or another profile of the same machine | `Report_AHealthRowForARecordNamingAnotherMachine_IsRefused_AndTheRecordIsUnchanged`, `Report_AHealthRowForAnotherProfileOfTheSameMachine_IsRefused` |
| too-many-approvers | The tenant already keeps the most approvers | `Report_ANewRegistrationWhenTheTenantKeepsTheMostApprovers_IsRefused` |

One refused row never stops the others (`Report_ARefusedRow_DoesNotStopTheOtherRowsOfTheSameReport`), every accepted
and refused row is logged with its account and machine and never with unchecked text
(`Report_LogsEveryAcceptedAndRefusedRow_WithTheAccountAndTheMachine_AndNeverTextThatWasNotChecked`), accounts are
compared in lower case, and the profile identifier is compared exactly.

Readiness (`LicenseApproverReadinessTests`): a sentence for every state; no approver says so;
`WhyNotReady` refuses an account that is not an email, and never reads another tenant's approver.

The alarm (`LicenseApproverAlarmTests`, 14 tests, listed in `c1-alarm.md`), and on the hosted Gateway
`ASignedOutCheck_OverTheDirectorsHub_OpensTheApproverAlarm_AndACanApproveCheckClosesIt`: a real signed-out check over
the tunnel opens Alice's alarm with the readiness sentence and one owner email, while Bob, with a healthy approver for
the same account, gets no alarm and his approver stays ready; a can-approve check closes it. It was shown red with the
host's link from the report to the alarm taken out.

## Gate for this pull request

TEMP on D:. `.\scripts\test-local.ps1 -Parked -Filter "FullyQualifiedName~License"`: see the result recorded below.
