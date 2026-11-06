# The 7-day Pro trial at an account's first Director - ANSWERS to the review

Developer, Website Black start flow and trial track, 6 November 2026. Answers to `review.md` (committed unchanged
beside this file), following the Tech Lead's decisions 10 and 11 (devthrottle_internal
`docs/missions/website-black-2026-11-05/tasks/phase-1-developer-flow-decisions.md`). The owner granted ONE Gateway
release, so this release carries everything the flip needs: at the flip only the setting is turned on.

## Finding 1. A failed ledger WRITE is an unhandled exception, not the retry - ACCEPTED, fixed

The reviewer is right: Entity Framework wraps a refused INSERT in the same `DbUpdateException` a lost grant race
raises, and the race handler re-threw when the re-read found no row. `TrialRegistry.GrantIfFirstArrival` now
answers `Unknown` in that case (a running trial written by the winner is still adopted), so the enrolment gate's
existing `Unknown` branch answers the 503 retry its comment describes, mints no tenant and grants nothing. The
registry keeps its own promise that it does not throw.

Test: `FirstDirectorTrialTests.Enroll_TrialLedgerWriteFails_SettingOn_IsARetryThatMintsNothing`. The read succeeds
and a SQLite `BEFORE INSERT` trigger refuses the insert (the reviewer's trigger). It asserts 503, no tenant and no
row. The drop-the-table test stays and still proves the read failure.

Negative control: with the old `throw;` put back, this test FAILED with the `DbUpdateException` the reviewer
reproduced.

## Finding 2. No test proves the route is wired - ACCEPTED, fixed, with the move case

New `FirstDirectorTrialRouteTests`: a REAL hosted `GatewayHost` with `FirstDirectorTrial` on, over REAL HTTP.
- `FirstDirector_OverTheWire_WithTheSettingOn_StartsTheTrial_AndTheNextReadIsPro`: a Director's POST to
  `/devices/enroll-hosted` with a valid account token answers 200, the ledger then holds a running trial ending 7
  days later, and the next entitlement read is Pro. This also proves `GatewayHostOptions` carries the setting to
  the route.
- `MovingADirector_ToTheOwnAccount_OverTheWire_GrantsNoTrial`: a Director set up for a team, moved to the person's
  own account through `/devices/enroll-hosted/move`, answers 200, mints the tenant and writes no trial.

How the token works: the host's account-token validator reads its public key set, audience and issuer from
environment variables a test may set (`GatewayAccountFactory.BuildAuthorizationValidator`). The test sets them for
the life of its own host and restores them; test parallelization is off in this assembly. A test host (not a real
hosted image) also seeds a PAID "hosted" row whenever a device is bound (`OnAccountBoundForTest`). A paid row wins
over a trial, so the test switches that hook off; production never wires it.

Negative control: with `trials:` and `firstDirectorTrial:` taken out of the host's `Map` call, the route test FAILED.

## Finding 3. The 7 days can start on an enrolment that is then refused - NO CHANGE (decision 10.3)

Recorded as known in `proof.md`: where Teams is released, a team member's Director with live team sessions, set
up again for the person's own account, is refused with 409 after the trial row is written. The 7 days then run
from that first attempt, and the retry gets the same row. The population is narrow, and the placement keeps the
stronger property: no tenant can ever be minted without the grant having been asked first.

## Finding 4. One sentence of the setting's documentation is false - ACCEPTED, corrected (with decision 11)

`FirstDirectorTrial.cs` now says what is true. The hosted Gateway reads the setting as an Azure application
setting, and the one way to write it is the deploy workflow's `trial_at_first_director` input. The image carries no
line for it. An application setting overrides anything in the image, so the start-up log line
`[FirstDirectorTrial] FromEnvironment: CC_GATEWAY_TRIAL_AT_FIRST_DIRECTOR=on|off` is the proof of what the process
read. That log check stays a standing step after every hosted deploy while the setting exists (`proof.md`).

Decision 11 in the same change:
- `.github/workflows/deploy-hosted-gateway.yml` takes the input `trial_at_first_director`, a choice of unchanged
  (the default), on and off, the same way it handles `CC_GATEWAY_TEAMS` and `CC_GATEWAY_TEAM_MENTOR`. On writes 1,
  off writes 0, and unchanged writes nothing. The value is checked before the Azure login, so a refused value
  builds and changes nothing. The run summary shows the setting before, as requested and after. A run that did
  not ask for it fails if the setting changed anyway.
- The `Dockerfile` carries no line for it, not even a comment.
- Proof: `src/CcDirector.Core.UnitTests/Release/FirstDirectorTrialSwitchIsADeployInputTests.cs` (4 tests). It
  checks the input's shape, that the mapping runs before the login, that the setting is written only when a run
  asks for it and nowhere else, and that the image has no line for it. `workflow-input-run.txt` is the
  workflow's own refusal step, cut out unchanged and run with Git Bash for each value:
  - not given or `unchanged` hands on nothing to write;
  - `on` gives 1 and `off` gives 0;
  - `1`, `true`, `yes` and `ON` are REFUSED, exit 1.
- Negative control: making the workflow write the setting with no input FAILED
  `TheSetting_IsWrittenOnlyWhenARunAsksForIt_AndNowhereElse`.
- Nobody has run the workflow with the input (decision 11.4).

## Finding 5. A stale "how this fails on purpose" sentence - ACCEPTED, reworded

`HostedProTrialTests`: the sentence now says the grant is back behind the first-Director setting. The test passes
no setting, so it pins "off by default". Making the gate grant without the setting, or defaulting the setting to
on, turns it red.

## Notes that are not findings

- `tenants.LookupBySubject` with no catch: no change. It is the same class of read as the mint that every
  enrolment already makes.
- The website's 14-day wording for a running trial: carried by the Website Black flip (the website's 7-day wording
  lands with it), as the reviewer says. No row exists to show while the setting is off.
- The move path's "grants nothing": now tested (finding 2).
