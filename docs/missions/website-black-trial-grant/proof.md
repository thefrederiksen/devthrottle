# The 7-day Pro trial at an account's first Director - proof

Website Black mission (devthrottle_internal `docs/missions/website-black-2026-11-05`), owner decisions D3
(Pro free for 7 days), D4 (the 7 days start when the Director first connects, not at sign-up) and D20 (no
card). The owner allowed ONE Gateway release for this, with the setting off (5 November 2026: "Yes, you can go
ahead and make a gateway release."). Branch `wb/p1-trial-grant`, cut from origin/main 1948c4a00, origin/main
6b605a2e5 merged in for the review fixes. The review is `review.md`, the answers `answers.md`.

## What changed

| File | Change |
|---|---|
| `src/CcDirector.Gateway/Tenancy/FirstDirectorTrial.cs` | NEW. The setting `CC_GATEWAY_TRIAL_AT_FIRST_DIRECTOR` ("1" on; unset, blank or "0" off; anything else stops the Gateway) and the length, 7 days. |
| `src/CcDirector.Gateway/Tenancy/TrialRegistry.cs` | `GrantIfFirstArrival` takes the length of a NEW trial. The three-argument form keeps 14 days. Never-extend and one-trial-per-account are unchanged. A refused insert that is not a lost race answers `Unknown` (the retry), never a throw (review finding 1). |
| `src/CcDirector.Gateway/Api/HostedEnrollmentEndpoint.cs` | The grant, in `PersonalAccountGate`, where the 14-day grant sat until 11 October: after the paid read, before the tenant is minted. Asked ONLY when `Enroll` is on the Director door with the setting on. A move and a team enrolment pass no ledger; the Cockpit and phone door never qualifies. |
| `src/CcDirector.Gateway/GatewayHostOptions.cs`, `GatewayHost.cs` | The setting is read once on a hosted Gateway (self-host never reads it) and passed to the Director door. |
| `.github/workflows/deploy-hosted-gateway.yml` | The input `trial_at_first_director` (unchanged by default, on writes 1, off writes 0), handled like the two Teams switches: checked before the login, written only when asked, reported before and after, and a run that did not ask fails if the setting changed (decision 11). |
| `Dockerfile` | Nothing about this setting at all (decision 11.2): the workflow input is the one way it is written. |

Who is granted: a hosted account that is not paying (free plan or no paid row), that this Gateway has never seen
(no tenant), on its first Director enrolment, while the setting is on. Nobody else, ever.

## Tests

`src/CcDirector.Gateway.Tests/FirstDirectorTrialTests.cs` (12, the Gateway's test database, a fixed clock):

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

| `Enroll_TrialLedgerWriteFails_SettingOn_IsARetryThatMintsNothing` | the read succeeds and the INSERT is refused: 503, no tenant, no row (review finding 1) |

`src/CcDirector.Gateway.Tests/FirstDirectorTrialRouteTests.cs` (2, a REAL hosted `GatewayHost` over HTTP, review
finding 2):

| Test | Proves |
|---|---|
| `FirstDirector_OverTheWire_WithTheSettingOn_StartsTheTrial_AndTheNextReadIsPro` | the host carries the setting and the ledger to the route: a Director's POST answers 200, a 7-day trial is running, the next read is Pro |
| `MovingADirector_ToTheOwnAccount_OverTheWire_GrantsNoTrial` | a team Director moved to the person's own account mints the tenant and writes no trial |

`src/CcDirector.Gateway.UnitTests/Tenancy/FirstDirectorTrialSettingTests.cs` (11): the setting's values, off by
default, 7 days.

`src/CcDirector.Core.UnitTests/Release/FirstDirectorTrialSwitchIsADeployInputTests.cs` (4): the workflow input is a
choice defaulting to unchanged; on -> 1, off -> 0, unchanged -> nothing, anything else refused, before the login; the
setting is written only when asked and nowhere else; the Dockerfile has no line for it.

`workflow-input-run.txt`: the workflow's own refusal step, cut out unchanged and run with Git Bash for each value.
Not given and `unchanged` hand on nothing to write; `on` gives 1, `off` gives 0; `1`, `true`, `yes` and `ON` are
refused with exit 1.

## Gate

- `.\scripts\test-local.ps1`: GREEN at 1948c4a00 plus this change (every suite outcome=Completed; the parked
  Gateway.UnitTests 16,941 and Core.Tests 4,662 ran).
- `.\scripts\test-local.ps1 -Gateway`: GREEN, four shards, 1,827 executed (the coverage gap the default run named).
- Targeted before the gate: the trial and enrolment classes in Gateway.Tests 84 of 84; the setting and remote
  access classes in Gateway.UnitTests 102 of 102.

After the review fixes, with origin/main 6b605a2e5 merged in:
- `.\scripts\test-local.ps1`: every suite outcome=Completed EXCEPT `CcDirector.Launcher.Tests`, which the script
  stopped at its 120-second ceiling (RESULT: OVER BUDGET, "NOT a test failure"). This change touches nothing the
  Launcher tests reach. Run alone straight after, it passed 234 of 234 (1 minute 23 seconds of tests) with the
  machine at 80% CPU from other work. The ceiling was not raised. The first run of this gate also caught a real
  rule break, a real-clock read in the new route test, and that was fixed (the test now judges at instants read
  from the trial row).
- `.\scripts\test-local.ps1 -Gateway`: GREEN, four shards, 1,830 executed.
- Targeted: the trial classes in Gateway.Tests 30 of 30; the Release and Architecture guards in Core.UnitTests
  62 of 62.

## Negative controls

- The "Director door only" condition in `Enroll` was removed and the new tests run:
  `Enroll_CockpitOrPhoneDoor_SettingOn_GrantsNothing` FAILED (1 failed, 10 passed). Restored, 11 of 11 pass.
- The review fixes, three faults planted at once and the targeted tests run (restored afterwards):
  - the old `throw;` put back in the ledger: `Enroll_TrialLedgerWriteFails_SettingOn_IsARetryThatMintsNothing`
    FAILED with the `DbUpdateException`;
  - `trials:` and `firstDirectorTrial:` taken out of the host's `Map` call:
    `FirstDirector_OverTheWire_WithTheSettingOn_StartsTheTrial_AndTheNextReadIsPro` FAILED;
  - the workflow made to write the setting with no input: `TheSetting_IsWrittenOnlyWhenARunAsksForIt_AndNowhereElse`
    FAILED.
  Gateway.Tests 2 failed of 30, Core.UnitTests Release 1 failed of 41; each failure was the test aimed at that fault.
  Restored: 30 of 30 and 41 of 41.

## Known, and accepted (review finding 3, Tech Lead decision 10.3)

Where Teams is released, a team member's Director that still has sessions registered in the team, set up again for
the person's own account, is refused with 409 AFTER the trial row is written and the tenant minted. Their 7 days run
from that first attempt, and the retry gets the same row. The same holds for a database failure at the
registration step. Accepted because the population is narrow and the placement keeps the stronger property: no
tenant is ever minted without the grant having been asked first, so no account can end up "already known" with no
trial.

## The check after the deploy (setting off)

1. The Gateway log at start-up has the line
   `[FirstDirectorTrial] FromEnvironment: CC_GATEWAY_TRIAL_AT_FIRST_DIRECTOR=off`. An Azure application setting
   overrides the image, so this line, not the image, is the proof of what the process read. It stays a step after
   every hosted deploy while the setting exists. The run summary's row for the setting should read `unset`
   before and after, requested `unchanged`.
2. A test account with no trial (a `.invalid` address) signs in a Director on the hosted Gateway. Afterwards the
   website's `GET /api/trial` for that account answers `state: none` (the ledger has no row for it), and its plan
   is Free.
If either is not so, roll back with `rollback-hosted-gateway.yml`.

## Not done here

No pull request, no merge, no deploy: the Tech Lead does those through the `deploy-hosted-gateway` skill, with the
setting off (the `trial_at_first_director` input left at unchanged). Turning it on is one more run of the same
workflow with that input set to on, with no new code, at the Website Black flip, and it is the owner's decision
(D27). Nobody has run the workflow with that input.
