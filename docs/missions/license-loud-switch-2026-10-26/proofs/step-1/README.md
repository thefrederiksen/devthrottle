# Step 1 proof: a machine stranded on a full account is one alarm

Branch `loud-switch-1-stranded-event`, cut from origin/main at aa1c515af. The change is commit 8ef7c9b5f. The answer to
the review is commit 87a47eae2. The outputs below were run on 87a47eae2 unless the table says otherwise. Local temporary
paths are written as `<temp>`, and the worktree as `<worktree>`.

## What changed, in plain words

On 26 October an account ran out of its weekly limit. The Gateway switched the one machine that held a sign-in for an
account with room, and left the other two where they were. Each got a log row and nothing else, because some account
still had room, so it was not the "no account has room" case.

Now the decision engine names those machines (`LicenseDecisionEngine.StrandedMachines`), using the same rule `Decide`
uses to leave them behind. The readiness check raises one license alarm per machine through the existing alarm service
(`LicenseStrandedAlarm`, kind `stranded-on-full-account`):

- **When it opens:** one email, one push and one banner line.
- **While it stays open:** nothing more is sent. The alarm row is its state, so a Gateway that restarts finds it open
  and sends nothing again.
- **When it closes:** by itself, when the machine moves off the account, when the account resets, or when the machine
  becomes ready and is switched.

The review asked for two more rules, and both are in:

- **It is loud only about what the Gateway cannot repair.** While the Gateway's own sign-in for that machine is starting,
  running, or has just ended well and waits for the next report, the alarm does not open. It opens when no automatic fix
  is possible (no approver can approve, or the Director is offline), or when the fix failed. Its "one thing to do" is the
  readiness check's own sentence for that machine.
- **One machine, one fault, one alarm.** From 97% up the stranded alarm owns the machine, and the five-hour alarm stands
  down for it. At 80% of the five-hour window the owner hears once. At 97% that line closes and the stranded alarm is the
  "again" of decision D1.

The kind is not called "cannot-switch", because `LicenseLogKinds.CannotSwitch` already means a Director that reports it
cannot run a switch at all.

## The outputs

| File | What it is | Result |
|---|---|---|
| `licenses-filter.txt` | `dotnet test src/CcDirector.Gateway.UnitTests --filter "FullyQualifiedName~CcDirector.Gateway.Tests.Licenses"` | 1301 passed, 0 failed |
| `review-tests-on-the-reviewed-commit.txt` | the new alarm tests run against the reviewed code (8ef7c9b5f's `LicenseStrandedAlarm.cs` and `LicenseReadinessService.cs`) | 9 failed, 14 passed: the tests see both findings |
| `test-local.txt` | `.\scripts\test-local.ps1`, the default gate, including the parked Gateway.UnitTests classes this change can reach | red in two suites this change does not touch, see below |
| `test-local-rerun.txt` | `.\scripts\test-local.ps1 -RerunFailed` on that run | the Core test passed alone; the 10 Mac tests failed again |
| `test-local-gateway.txt`, `test-local-gateway-rerun.txt` | `-Gateway` and its re-run, on 8ef7c9b5f (before the review answer) | 1809 passed, 3 skipped, 1 failed, and that one passed alone |
| `replay-step-3-on-step-1.txt` | the step 3 replay (`LicenseStrandedReplayTests`, commit 1ad23b948), cherry-picked locally and not committed here | 11 passed, 2 failed, see below |

### Red results that are not this change

- **`cc-director-setup-engine.Tests`: 10 failures in `MacAppNameAndPlaceTests`.** These are known (#4484). They fail on
  Windows on main too, as the reviewer observed on aa1c515af.
- **`CcDirector.Core.UnitTests`: `ApiModeSessionHostTests.TurnStartBound_TurnBeginsInTime_NeverRestarts`** failed once and
  passed on its own re-run. Core is not touched by this change.
- **`Gateway.Tests`: `RecordingCompletenessGateHttpTests.Complete_EmptyCapture_Returns400_NamedError`** failed once and
  passed on its own re-run. It is a recordings test.

### The replay

Both `OutOfWeek_26October_AMachineThatCannotSwitch_IsToldByEmailPushAndBanner` cases (SOREN_NORTH and Mac-mini) were red
on aa1c515af and are green now. The two `EightyOfWeek_25October_..._RaisesTheWarning` cases are still red, because the
80% warning is step 2. Every other case passes, including the negative controls: with the sign-in held nothing is loud,
usage alone raises nothing, and the stale machine is never named.

## What each new test proves

`LicenseStrandedMachineTests` tests the rule as pure code:

- `StrandedMachines_ATiredAccountWithAMachineReadyForNoAccountWithRoom_NamesThatMachine`: the machine with no sign-in for
  the account with room is named, against that account, with its real reason.
- `StrandedMachines_OnThePictureDecideActsOn_AreTheMachinesItLeavesBehind`: the machines named stranded are exactly the
  ones the automatic switch does not move, and no "no account has room" event opens.
- `StrandedMachines_NoMachineOnTheAccountIsReady_NamesEachOne_AndOpensNoNoRoomEvent`: the rule is per machine, and it is
  still not the no-room event while some account has room.
- `StrandedMachines_ReadyForAnAccountWithRoomThatIsNotTheBest_IsNotStranded`: a machine that can move to any account with
  room is not stranded.
- `StrandedMachines_NoAccountHasRoom_IsTheNoRoomEventAndNothingIsStranded`: the no-room event keeps its meaning.
- `StrandedMachines_EveryMachineReady_AtAnyUse_NamesNothing` and `StrandedMachines_UnderTheThreshold_AMachineNotReady_IsNotStranded`:
  the #4249 boundary.
- `StrandedMachines_TheAccountsReadingIsStale_NamesNothing`: a stale reading never opens it.
- `StrandedMachines_AMachineThatCannotBeSentASwitchNow_IsNotStranded`: stale, open switch, switch in progress, refused,
  cannot run a switch, and held each keep their existing meaning.
- `StrandedMachines_ASignInThatCannotBeUsed_StrandsTheMachineWithItsReason`: a refused or soon-expiring sign-in strands
  the machine, and the alarm says which.

`LicenseStrandedAlarmTests` runs the readiness check over the real store, the real sign-in service, the real approver
check and the real alarm service, on a throwaway SQLite file with the test's own clock. The default picture is the
26 October one: no approver can approve, so the Gateway cannot fix the machine by itself.

- Opening, once: `..._OpensOneAlarmForIt` (the exact words, and the action is the readiness check's approver sentence),
  `..._SendsOneEmailOnePushAndOneBannerLine`, `..._TheDirectorIsOffline_NoAutomaticFix_OpensTheAlarm_SayingToStartIt`,
  `..._TheNextChecks_KeepItOpen_AndSendNothingMore`, and `..._AfterAGatewayRestart_TheOpenAlarmIsFound_AndNothingIsSentAgain`.
- The three closes: `..._TheMachineMovesOffTheAccount_TheAlarmCloses`, `..._TheAccountResets_TheAlarmCloses`,
  `..._TheTiredWindowsResetTimePasses_OnAStaleReading_TheAlarmCloses`, and `..._TheMachineBecomesReady_TheAlarmCloses`.
  Also `..._WhileTheReadingAndTheMachineAreStale_TheAlarmStaysOpen`, because an unknown is not a fix.
- Review finding 2, quiet while the Gateway repairs:
  - `..._TheGatewayStartsItsOwnSignIn_OpensNoAlarm`, `..._WhileTheGatewaysSignInRuns_OpensNoAlarm` and
    `..._TheGatewaysSignInEndedWell_AndWaitsForTheReport_OpensNoAlarm`: no alarm, email or push while the Gateway's own
    sign-in starts, runs, or has ended well.
  - `..._TheGatewaysSignInFailed_OpensTheAlarm_SayingItFailed`: once the fix fails, the alarm opens with the failure
    and the retry time.
  - `..._AnOpenAlarm_StaysOpenWhileTheGatewayTriesAgain`: a retry is not a fix, and sends no second email.
- Review finding 1, one alarm per machine:
  - `..._At97OfTheFiveHourWindow_AStrandedMachine_GetsOneAlarm`: one alarm, one email, one push, one banner line.
  - `..._From80To97OfTheFiveHourWindow_TheFiveHourAlarmHandsOverToTheStrandedAlarm`: the five-hour alarm at 80%, then
    at 97% its line closes and the stranded one opens. Two pushes in that order, two emails, never two lines at once.
  - `..._At97OfTheFiveHourWindow_WhileTheGatewayRepairs_NeitherAlarmOpens`: neither alarm opens while the Gateway
    repairs the machine.
- What it is not: `..._NoAccountHasRoom_RaisesNoStrandedAlarm_AndTheNoRoomStatusIsUnchanged`, plus
  `..._AtTheLimit_EveryMachineReady_RaisesNothing` (weekly 100%, five-hour 97%, both 100%) and
  `..._NearTheLimit_AMachineNotReady_RaisesNoStrandedAlarm`, which pin the #4249 boundary.

None of these tests opens a port, starts Docker, sleeps or reads the real clock.
