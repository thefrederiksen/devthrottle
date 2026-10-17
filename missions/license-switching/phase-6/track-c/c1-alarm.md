# Track C, Developer C1, pull request 3 - the approver alarm

17 October 2026, by the Track C Developer for approver registration. Built on 4109 (registration) and 4127
(readiness sentences), on the license alarm of 4110.

## What was built

`Licenses.LicenseApproverAlarm` raises one license alarm, kind `approver-cannot-approve` ("Approver cannot approve
sign-ins"), for each approver whose health says it cannot approve now. The sentences are
`LicenseApproverReadiness.Verdict`'s, so the banner, the email and the approvers row say the same words. It is raised:

- after every check report a Director sends: `LicenseApproverRegistration` calls `LicenseApproverAlarm.RaiseSoon`
  once the report's records are written, and the raise runs on a task of its own, so the hub call never waits for a
  push or an email;
- on the Gateway's once-a-minute license check (`GatewayHost.RunLicenseRenewalEmailCheckAsync`, beside the alarm
  service's own email retry), so an approver nobody checks alarms once its 26 hours pass.

Every raise passes every approver of the tenant that cannot approve, so an approver that recovers, or that the owner
removes, closes its alarm by itself. Every approver alarm is about the account alone (the alarm service's "no one
machine"), and its sentences name the machine, so the alarm keeps its identity when the machine first reports. Raises
for one tenant run one at a time and read the records under that wait. An approver whose sentence is past the alarm's
400 characters is left out and logged with its account, costing only that alarm. A raise with nothing to say writes no
log line, and the minute check reads the approvers through `LicenseApproverStore.ListQuietly`.

Review of this pull request: `review-4140.md`, answered in `review-4140-answers.md`. An account with no approver at all
raises nothing here - a machine with its own browser profile needs none, and readiness is Track A's.

Contract section 10.6 says the same.

## The tests, in plain words (`LicenseApproverAlarmTests`, over the real alarm service)

| Test | What it sets up | What it proves |
|---|---|---|
| `AnApproverThatNeedsAFreshSignIn_OpensOneAlarm_WithTheReadinessSentences_AndPushesAndEmailsOnce` | An approver whose check says it needs a fresh sign-in | One alarm, about account A alone, with the readiness sentences (saying the display name), one push and one email |
| `RaisedAgainWhileStillTrue_StaysOpenQuietly_AndClosesWhenTheApproverCanApproveAgain` | Signed out, raised twice, then healthy | Stays open with no second push; closes once it can approve |
| `AnApproverTheOwnerRemoves_ClosesItsAlarm` | A wrong-account approver, then the owner removes it | The alarm closes |
| `AHealthyApproverThatNobodyChecks_AlarmsOnceItsCheckIsOver26HoursOld` | A healthy approver, then the clock 26 hours and a second later | No alarm while healthy; an alarm saying when it was last checked once the window passes |
| `AnApproverOnAMachineThatHasNeverReported_AlarmsAboutTheAccount_AndStillNamesTheMachine` | An approver on a machine with no reports | The alarm is about the account, its sentence names the machine |
| `AnApproversMachineReportingForTheFirstTime_KeepsTheSameAlarm_NoSecondPushOrEmail` | An approver on a machine with no reports, then that machine reports | The same alarm stays open: still open, nothing closed, one push and one email |
| `AnApproverWhoseSentenceIsTooLongForAnAlarm_CostsOnlyItsOwnAlarm_TheOthersStillOpen` | A 300-character account with a 200-character detail beside a signed-out account A | Account A's alarm opens; the long one is left out with a REFUSED line naming its account |
| `AnApproverWithALongDetailAndALongAccount_WithinTheLimit_StillOpens` | A 100-character account, check failed with a 200-character detail | It opens, within 400 characters |
| `TwoRaisesOfOneTenant_RunOneAtATime_SoTheSecondReadsTheRecordsAsTheyAreWhenItsTurnComes` | First raise held in its push; the record goes healthy, a second raise starts, the record goes signed out | The second waits, then reads signed out: still open, nothing closed |
| `AMinuteRaiseWithNothingToSay_WritesNoLogLine` | A raise with no approver, then one with a healthy approver | The alarm writes no log line |
| `AnAccountWithNoApprover_RaisesNothing` | No approvers | Nothing opens, nothing is pushed |
| `OneTenantsRaise_NeverOpensOrClosesAnotherTenantsAlarm` | An alarm in tenant one, a raise in tenant two | Tenant one's alarm stays; tenant two has none |
| `ACheckReport_RaisesTheAlarm_AfterTheRecordIsWritten_WithoutTheReportWaitingForIt` | The registration wired to `RaiseSoon`; a signed-out check of an owner-set approver | The report answers at once; once the raise finishes the alarm is open with the new state's sentence |
| `AReportRefusedWhole_RaisesNothing_AndADecidedReportRaisesOnce` | A missing report, then an empty one | The refused one calls nothing; the decided one calls once, for its tenant |

## Gate

The gate the Tech Lead set for this phase, TEMP on D:, at commit `33abb15ae` (clean tree, rebased onto `origin/main`
after 4127 merged):

- `.\scripts	est-local.ps1`: GREEN, every suite Completed (Core.UnitTests 2156, Avalonia 1063, Reclaim 305,
  setup-engine 884, and the rest).
- `.\scripts	est-local.ps1 -Parked -Filter "FullyQualifiedName~License"`: GREEN. Gateway.UnitTests 967 of 967,
  Gateway.Tests 22 (the hosted license tests, including every `LicenseSignInHostedTests` case now that the minute check
  raises approver alarms), Core.UnitTests 184, Avalonia 32.

### Gate after the review fixes

TEMP on D:, at commit `64bad72c0` (clean tree; the review fixes with `origin/main` merged in):

- `.\scripts	est-local.ps1`: GREEN, every suite Completed (Core.UnitTests 2160, Avalonia 1063, Reclaim 305,
  setup-engine 884, and the rest).
- `.\scripts	est-local.ps1 -Parked -Filter "FullyQualifiedName~License"`: GREEN. Gateway.UnitTests 997 of 997,
  Gateway.Tests 22.
