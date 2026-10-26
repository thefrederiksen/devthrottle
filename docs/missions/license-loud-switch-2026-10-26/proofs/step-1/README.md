# Step 1 proof: a machine stranded on a full account is one alarm

Branch `loud-switch-1-stranded-event`, cut from origin/main at aa1c515af. The change itself is commit 8ef7c9b5f.
Every output here was run on that commit. Local temporary paths are written as `<temp>` and the worktree as `<worktree>`.

## What changed, in plain words

On 26 October an account ran out of its weekly limit. The Gateway switched the one machine that held a sign-in for an
account with room, and left the other two where they were. Each got a log row and nothing else, because some account
still had room and so it was not the "no account has room" case.

Now the decision engine names those machines (`LicenseDecisionEngine.StrandedMachines`). It uses the same rule `Decide`
uses to leave them behind. The readiness check raises one license alarm per machine through the existing alarm service
(`LicenseStrandedAlarm`, kind `stranded-on-full-account`). When the alarm opens it sends one email, one push and one
banner line. While it stays open it sends nothing more. It closes by itself when the machine moves off the account, when
the account resets, or when the machine becomes ready and is switched. The alarm row in the database holds its state, so
a Gateway that restarts finds it open and sends nothing again.

The kind is not named "cannot-switch", because `LicenseLogKinds.CannotSwitch` already means a Director that reports it
cannot run a switch at all.

## The outputs

| File | What it is | Result |
|---|---|---|
| `licenses-filter.txt` | `dotnet test src/CcDirector.Gateway.UnitTests --filter "FullyQualifiedName~CcDirector.Gateway.Tests.Licenses"` | 1290 passed, 0 failed |
| `test-local.txt` | `.\scripts\test-local.ps1`, the default gate, including the 30 parked Gateway.UnitTests classes this change can reach | red only in `cc-director-setup-engine.Tests`, see below |
| `test-local-gateway.txt` | `.\scripts\test-local.ps1 -Gateway`, the parked Gateway.Tests suite, which the default gate named as a coverage gap | 1809 passed, 1 failed, 3 skipped, see below |
| `test-local-gateway-rerun.txt` | `.\scripts\test-local.ps1 -RerunFailed` on that run | the one failed test passed alone |
| `replay-step-3-on-step-1.txt` | the step 3 replay test (`LicenseStrandedReplayTests`, commit 3a67367bc), cherry-picked locally without committing it here | 11 passed, 2 failed, see below |

### The two red results that are not this change

- **`cc-director-setup-engine.Tests`: 10 failures, all in `MacAppNameAndPlaceTests`.** These tests build Mac paths and
  fail when run on Windows ("Invalid URI", "NotApplicable" where "Move" was expected). The project references only
  `CcDirector.Core` and `CcDirector.Gateway.Contracts`, and this change touches neither, so main at aa1c515af fails the
  same way. Reported to the Tech Lead.
- **`Gateway.Tests`: `RecordingCompletenessGateHttpTests.Complete_EmptyCapture_Returns400_NamedError`** failed once and
  passed on its own re-run of the same commit, so it failed on correct code. It is a recordings test, not a license one.
  Reported to the Tech Lead.

### The replay

The two `OutOfWeek_26October_AMachineThatCannotSwitch_IsToldByEmailPushAndBanner` cases (SOREN_NORTH and Mac-mini) were
red on aa1c515af and are green with this step. The two `EightyOfWeek_25October_..._RaisesTheWarning` cases are still red,
as expected: the 80% warning is step 2. Every other replay case passes, including the negative controls: with the sign-in
held nothing is loud, usage alone raises nothing, and the stale machine is never named.

## What each new test proves

`LicenseStrandedMachineTests` (the rule, as pure code):

- `StrandedMachines_ATiredAccountWithAMachineReadyForNoAccountWithRoom_NamesThatMachine` - on the 26 October picture in
  small, the machine with no sign-in for the account with room is named, against that account, with its real reason.
- `StrandedMachines_OnThePictureDecideActsOn_AreTheMachinesItLeavesBehind` - one rule, two readers: the machines named
  stranded are exactly the ones the automatic switch does not move, and no "no account has room" event opens.
- `StrandedMachines_NoMachineOnTheAccountIsReady_NamesEachOne_AndOpensNoNoRoomEvent` - it is per machine, and still not
  the no-room event while some account has room.
- `StrandedMachines_ReadyForAnAccountWithRoomThatIsNotTheBest_IsNotStranded` - a machine that can move to any account
  with room is not stranded.
- `StrandedMachines_NoAccountHasRoom_IsTheNoRoomEventAndNothingIsStranded` - the no-room event keeps its meaning,
  unchanged: it opens, with its own "no account had room" words, and nothing is called stranded.
- `StrandedMachines_EveryMachineReady_AtAnyUse_NamesNothing` (80%, 96.9%, 97% and over, both windows) and
  `StrandedMachines_UnderTheThreshold_AMachineNotReady_IsNotStranded` - the #4249 boundary: usage alone raises nothing.
- `StrandedMachines_TheAccountsReadingIsStale_NamesNothing` - a stale reading never opens it.
- `StrandedMachines_AMachineThatCannotBeSentASwitchNow_IsNotStranded` (stale, open switch, switch in progress, reports
  refused, reports it cannot run a switch, held) - the existing meanings are kept.
- `StrandedMachines_ASignInThatCannotBeUsed_StrandsTheMachineWithItsReason` - a refused sign-in, or one that expires
  before a switch could finish, strands the machine and says so.

`LicenseStrandedAlarmTests` (the readiness check over the real license store and the real alarm service, on a throwaway
SQLite file, with the test's own clock and recorders for the email and the push):

- `Tick_AMachineOnATiredAccountReadyForNoAccountWithRoom_OpensOneAlarmForIt` - one alarm, for that machine only, with
  the exact words: the machine, the account it is stuck on, the account it needs, why, and the one thing to do.
- `Tick_TheAlarmOpens_SendsOneEmailOnePushAndOneBannerLine` - exactly one email (naming all of that), one push (machine
  name and kind only, no email address), one banner line.
- `Tick_TheNextChecks_KeepItOpen_AndSendNothingMore` - no repeat on the next ticks.
- `Tick_AfterAGatewayRestart_TheOpenAlarmIsFound_AndNothingIsSentAgain` - a new alarm service over the same database
  finds the open alarm and sends no second email or push.
- `Tick_TheMachineMovesOffTheAccount_TheAlarmCloses` - first close: the machine moves.
- `Tick_TheAccountResets_TheAlarmCloses` and `Tick_TheTiredWindowsResetTimePasses_OnAStaleReading_TheAlarmCloses` -
  second close: the account resets, seen by a fresh reading or by its reset time passing with nothing reporting.
- `Tick_TheMachineBecomesReady_TheAlarmCloses` - third close: the machine becomes ready, so the switch takes it.
- `Tick_WhileTheReadingAndTheMachineAreStale_TheAlarmStaysOpen` - not known is not fixed: a minute without facts does
  not close it, and sends nothing.
- `Tick_NoAccountHasRoom_RaisesNoStrandedAlarm_AndTheNoRoomStatusIsUnchanged` - the no-room case stays status on the
  Licenses page, with no alarm, email or push.
- `Tick_AtTheLimit_EveryMachineReady_RaisesNothing` and `Tick_NearTheLimit_AMachineNotReady_RaisesNoStrandedAlarm` - the
  #4249 boundary, over the real services.

None of these tests opens a port, starts Docker, sleeps or reads the real clock.
