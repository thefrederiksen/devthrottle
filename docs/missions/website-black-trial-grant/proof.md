# The 7-day Pro trial at an account's first Director - proof

Website Black mission (devthrottle_internal `docs/missions/website-black-2026-11-05`), owner decisions D3
(Pro free for 7 days), D4 (the 7 days start when the Director first connects, not at sign-up) and D20 (no
card). The owner allowed ONE Gateway release for this, with the setting off (5 November 2026: "Yes, you can go
ahead and make a gateway release."). Branch `wb/p1-trial-grant`, cut from origin/main 1948c4a00.

## What changed

| File | Change |
|---|---|
| `src/CcDirector.Gateway/Tenancy/FirstDirectorTrial.cs` | NEW. The setting `CC_GATEWAY_TRIAL_AT_FIRST_DIRECTOR` ("1" on; unset, blank or "0" off; anything else stops the Gateway) and the length, 7 days. |
| `src/CcDirector.Gateway/Tenancy/TrialRegistry.cs` | `GrantIfFirstArrival` takes the length of a NEW trial. The three-argument form keeps 14 days. Never-extend and one-trial-per-account are unchanged. |
| `src/CcDirector.Gateway/Api/HostedEnrollmentEndpoint.cs` | The grant, in `PersonalAccountGate`, where the 14-day grant sat until 11 October: after the paid read, before the tenant is minted. Asked ONLY when `Enroll` is on the Director door with the setting on. A move and a team enrolment pass no ledger; the Cockpit and phone door never qualifies. |
| `src/CcDirector.Gateway/GatewayHostOptions.cs`, `GatewayHost.cs` | The setting is read once on a hosted Gateway (self-host never reads it) and passed to the Director door. |
| `Dockerfile` | A comment saying the setting is deliberately NOT set, and the one line the flip adds. |

Who is granted: a hosted account that is not paying (free plan or no paid row), that this Gateway has never seen
(no tenant), on its first Director enrolment, while the setting is on. Nobody else, ever.

## Tests

`src/CcDirector.Gateway.Tests/FirstDirectorTrialTests.cs` (11, the Gateway's test database, a fixed clock):

| Test | Proves |
|---|---|
| `Enroll_SettingOff_FirstDirectorIsGrantedNoTrial` | **The release changes nothing**: setting off, a first Director enrols on free, no ledger row |
| `Enroll_SettingNotPassed_FirstDirectorIsGrantedNoTrial` | the same for every caller that passes no setting |
| `Enroll_SettingOn_TrialStartsAtTheFirstDirectorNotBefore` | nothing at sign-up; 7 days of Pro starting at the enrolment instant |
| `Enroll_SecondDirectorThreeDaysLater_DoesNotRestartTheTrial` | a second Director keeps the first end date |
| `Enroll_AfterTheSevenDays_GrantsNothingNew_AndEnrolsOnFree` | no second trial |
| `Evaluate_ProUntilTheSevenDaysEnd_FreeFromTheEndInstant` | Pro one tick before the end, free at it |
| `Enroll_CockpitOrPhoneDoor_SettingOn_GrantsNothing` | the Cockpit and phone door grants nothing |
| `Enroll_PayingAccount_SettingOn_GrantsNothing` | a paying account gets nothing |
| `Enroll_AccountTheGatewayAlreadyKnew_SettingOn_GrantsNothing` | an account with a tenant gets nothing |
| `Enroll_TrialLedgerUnreadable_SettingOn_IsARetryThatMintsNothing` | a ledger failure is a 503 and no tenant |
| `Enroll_SettingOnWithNoLedger_IsAWiringDefect` | on with no ledger throws |

`src/CcDirector.Gateway.UnitTests/Tenancy/FirstDirectorTrialSettingTests.cs` (11): the setting's values, off by
default, 7 days.

## Gate

- `.\scripts\test-local.ps1`: GREEN at 1948c4a00 plus this change (every suite outcome=Completed; the parked
  Gateway.UnitTests 16,941 and Core.Tests 4,662 ran).
- `.\scripts\test-local.ps1 -Gateway`: GREEN, four shards, 1,827 executed (the coverage gap the default run named).
- Targeted before the gate: the trial and enrolment classes in Gateway.Tests 84 of 84; the setting and remote
  access classes in Gateway.UnitTests 102 of 102.

## Negative control

The "Director door only" condition in `Enroll` was removed and the new tests run:
`Enroll_CockpitOrPhoneDoor_SettingOn_GrantsNothing` FAILED (1 failed, 10 passed). Restored, 11 of 11 pass.

## The check after the deploy (setting off)

1. The Gateway log at start-up has the line
   `[FirstDirectorTrial] FromEnvironment: CC_GATEWAY_TRIAL_AT_FIRST_DIRECTOR=off`.
2. A test account with no trial (a `.invalid` address) signs in a Director on the hosted Gateway. Afterwards the
   website's `GET /api/trial` for that account answers `state: none` (the ledger has no row for it), and its plan
   is Free.
If either is not so, roll back with `rollback-hosted-gateway.yml`.

## Not done here

No pull request, no merge, no deploy: the Tech Lead does those through the `deploy-hosted-gateway` skill, with the
setting off. Turning the setting on belongs to the Website Black flip.
