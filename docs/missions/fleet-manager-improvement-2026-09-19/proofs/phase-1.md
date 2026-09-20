# Fleet Manager Improvement - phase 1 - the Tech Lead's own check

20 September 2026. Run by the Tech Lead (session 0cc96f78), not by the Developer, in a worktree no
Developer is editing: `D:\ReposFred\devthrottle-fmi-p1-gate`.

## What was tested, exactly

Pull request 3198, head `bc47d00e4`, merged with `origin/main` at `07b6e32fe`. The merge commit in the
gate worktree is `24ef5efd6`. This is what will land, not what the Developer built on: the branch was
cut twelve commits behind the main branch, so testing the branch head alone would have said nothing
about the code after a merge.

## Run 1 - the default gate

    .\scripts\test-local.ps1

Nine suites, one build, four minutes forty-five seconds to build.

| Suite | Result | Passed | Failed | Total |
|---|---|---|---|---|
| CcDirector.Core.UnitTests | pass | 764 | 0 | 764 |
| CcDirector.Avalonia.Tests | pass | 646 | 0 | 646 |
| CcDirector.Engine.Tests | pass | 63 | 0 | 63 |
| CcDirector.HostedAgent.Tests | pass | 88 | 0 | 88 |
| CcDirector.Launcher.Tests | FAIL | 209 | 2 | 211 |
| CcDirector.Terminal.Avalonia.Tests | pass | 30 | 0 | 30 |
| CcDirector.Reclaim.Tests | pass | 323 | 0 | 323 |
| cc-director-setup.Tests | pass | 25 | 0 | 25 |
| cc-director-setup-engine.Tests | pass | 619 | 0 | 619 |

Every suite reported `outcome=Completed` and executed every test it held, so nothing was silently
skipped at the suite level. Results kept at `C:\Users\soren\AppData\Local\Temp\cc-test-local-d8f74fd2`.

### The two Launcher failures are this machine, not this change - observed, not assumed

    CcDirector.Launcher.Tests.LauncherDeclaredCapabilitiesTests
      .An_unarmed_launcher_declares_no_restart_signal_and_that_is_a_NO_not_an_unknown
      .Describing_the_launcher_asks_the_signal_and_never_raises_it

Both fail the same way: `Assert.False()` on `LauncherDeclaredCapabilities.Describe().RestartSignalArmed`,
expected false, actually true.

I read the code rather than accepting the Developer's word for it.
`LauncherDeclaredCapabilities.RestartSignalArmedNow()` asks `LifecycleSignal.HasListener(name)` first,
and that name - `LifecycleSignalNames.LauncherRestartDirector()` - is a machine-wide named signal, not
a process-local one. Each test's own comment states its premise as "nothing in this test process has
armed the signal", and both assert `ArmedLifecycleSignals` is empty, which holds. The kernel still
answers yes, because a real launcher is running on SOREN_NORTH with that signal armed. The test asks a
machine-wide question and reads the answer as a statement about its own process.

`git diff --name-only origin/main...bc47d00e4` names no file under the Launcher at all, so this change
cannot have caused it. It is a pre-existing defect in those two tests - they fail on any developer
machine that has a launcher running - and it belongs to whoever owns the restart signal work, not to
this phase. It is written down here so it is not rediscovered a third time.

## Run 2 - the Gateway unit suite, which the default run parks

    dotnet test src\CcDirector.Gateway.UnitTests\CcDirector.Gateway.UnitTests.csproj -c Debug

**6,753 passed, 0 failed, 8 skipped, 6,761 total, six minutes twenty-two seconds.**

The eight skipped tests are the PostgreSQL-backed proofs: the tenant-gate sweep check, the host boot
migration check, and six `HostedSchemaRefusesAnUnownedRowTests`. I ran this suite on its own rather
than through `-Parked`, so no throwaway database existed and those eight never ran. **A skipped test
reports as a pass in every report we produce, so say it plainly: those eight are not evidence of
anything in this run.**

## Run 3 - the raised session host tests, through the real request pipeline

    dotnet test src\CcDirector.Gateway.Tests\CcDirector.Gateway.Tests.csproj -c Debug --filter "FullyQualifiedName~RaisedSession"

**19 passed, 0 failed, nine minutes twenty-four seconds.** This is the suite that matters: this
repository's known trap is that adding a route does not add it to `SessionKeyGuard`, so verbs answer
403 while every unit test stays green. These drive the real middleware with a real session key.

The nineteen, by what each one shows:

*The grants*
- `AgentInput_WithARaisedKey_ReachesTheRouteAsTheOwnersDeviceDoes_AndAnUnraisedKeyIsRefusedAsToday`
- `OwnerOnlyFleetManagerRoutes_WithARaisedKey_ReachTheRouteAsTheOwnersDeviceDoes_AndAnUnraisedKeyIsRefusedAsToday`

*The refusals - as much the feature as the grants*
- `WhatRaisedDoesNotBuy_IsRefusedToARaisedKey_ByteForByteAsToAnUnraisedOne`
- `RaiseAndLower_FromAnySessionKey_RaisedOrNot_AreRefusedByTheGuard_AndChangeNothing`
- `Raise_FromADirectorsKey_IsRefusedByTheRoute_AsTheOwnersAlone`
- `AnotherAccountsSession_IsAnsweredToARaisedKeyExactlyAsAnUnknownSessionIs`
- `Raise_AnotherAccountsSession_AnswersAsAnUnknownOne_AndRaisesNothing`
- `ARaisedKeyOfOneAccount_IsNotRaisedInAnother`

*The list and its lifetime*
- `Raise_FromTheOwnersDevice_RaisesTheSession_StampsTheRoster_AndIsRecorded`
- `Lower_FromTheOwnersDevice_LowersTheSession_TakesTheGrantAway_AndIsRecorded`
- `SettingUpTheFleetManager_FromTheOwnersDevice_RaisesIt_AndRaisedFollowsTheMarkWhenItMovesAndClears`
- `MarkingItselfTheFleetManager_WithASessionKey_DoesNotRaiseTheSession`
- `ASessionKeyMovingTheMarkAway_LowersTheFleetManagerTheOwnerSetUp_AndRaisesNobody`
- `WhenTheSessionEnds_ItsEntryIsGone_AndItsKeyNoLongerPasses`
- `TheList_SurvivesAGatewayRestart`

*Messages*
- `Messages_FromARaisedSender_PassTheRelationshipRuleAndBothRates_AndAnIdenticalUnreadOneIsStillDropped`
- `Messages_FromAnUnraisedSender_AreLimitedAsToday`

*The record*
- `EveryRaisedAction_LeavesARecordNamingTheSessionThatTookIt_AndAnUnraisedAttemptLeavesNone`
- `ARouteEverySessionKeyReaches_LeavesNoRaisedRecord`

## What these runs do NOT cover - read this before calling the phase proven

1. **The rest of the Gateway host suite did not run.** I ran nineteen of it by name. The Developer
   measured the whole suite at one hour sixteen minutes; a foreground turn is capped at ten, and the
   Delivery Lead has been asked to decide how that run is to be made rather than having it hidden in
   the background. Until it runs, nothing is known about the host tests this change did not add.
2. **`Core.Tests` did not run at all**, in any of the three runs. The default gate's own coverage
   warning named it, along with the two Gateway suites, as parked code this change touches.
3. **Eight PostgreSQL proofs were skipped**, as above. The production database is PostgreSQL, so the
   migration this change adds has not been proven against the engine that will actually run it in a
   run of mine. The Developer reports having fixed two PostgreSQL migration proofs that were already
   behind the main branch; that is its evidence, not mine.
4. **No web or Python tests ran**, and none were expected to - this task changes neither.
5. **Nothing here says the widening is correctly bounded.** Nineteen green tests show the cases the
   Developer thought of. Whether a raised key can reach a route nobody listed is a question for the
   Reviewer reading `AuthMiddleware` and `SessionKeyGuard`, not for a test run, and it is the first
   thing the Reviewer was asked to check.
6. **The revert proof is the Developer's, not mine.** It reports that removing the raised check from
   the middleware turns six of the nineteen red, and that the restore was rebuilt rather than run with
   `--no-build`. I did not repeat it.
