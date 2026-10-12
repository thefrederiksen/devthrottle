# Proof - Gateway decision engine and forecast (phase 2, first part)

Branch `license-switching-gw-engine`, cut from origin/main at 54fd5da0d and rebased on 1d8a4c2e2 for the review round. Pure code only: nothing is wired
to the store, the hub, the command router or a mailer yet - that is the second pull request, after the
storage work merges.

## What is here

- `src/CcDirector.Gateway/Licenses/LicenseDecisionModel.cs` - the engine's own input (a snapshot of one
  tenant) and output (switches, bars, emails, exhaustion events opened and closed, skipped machines).
- `src/CcDirector.Gateway/Licenses/LicenseDecisionEngine.cs` - the tick (`Decide`), Switch now
  (`DecideManual`), and the target choice they and the forecast share.
- `src/CcDirector.Gateway/Licenses/LicenseForecaster.cs` - rate, expected time to 97%, next switch.
- Tests: `src/CcDirector.Gateway.UnitTests/Licenses/` - 70 engine and forecast tests (115 license tests in all, with the store work now on main).

## The check (MISSION.md section 7), test by test

| Case | Test |
|---|---|
| Under 97% does nothing; 96.99 / 97.0 on each window separately | `Decide_UnderThreshold_DoesNothing`, `Decide_ThresholdBoundary_OnEachWindowSeparately` (4 rows) |
| 97% on the 5-hour window switches | `Decide_FiveHourWindowAtThreshold_Switches` |
| 97% on the week switches | `Decide_WeekAtThreshold_Switches` |
| Target choice order | `Decide_TargetChoiceOrder_LowestWeekThenFiveHourThenEmail` (3 cases) |
| No target: one email, no switch | `Decide_NoTarget_SendsOneEmailAndSwitchesNothing` |
| A second tick sends no second email | `Decide_NoTargetOnTheSecondTick_SendsNoSecondEmail` |
| A reading taken before a passed reset never triggers, opens an event or emails | `Decide_PreResetReadingOverSeveralTicksAfterTheReset_SendsNoEmail`, `Decide_PreResetReadingAfterTheReset_DoesNotSwitchOrBar`, `Forecast_ReadingTakenBeforeAPassedReset_IsNotTiredNow` |
| One email per continuous outage: no close by time while a fresh reading is tired, end moved to the later reset (round 2 ruling) | `Decide_WeekTiresDuringTheEvent_OneEmailForTheWholeOutage`, `Decide_ResetReportedLaterThanTheEventsEnd_KeepsTheEventOpenAndMovesItsEnd` |
| A reading whose reset is exactly now is not tired | `Decide_ReadingWhoseResetIsExactlyNow_IsNotTired` |
| A new event after the reset emails once | `Decide_ReadingTakenAfterTheResetAgainAtThreshold_IsANewEventWithOneEmail` |
| An event closes on a fresh reading under 97%, not on a stale one | `Decide_ExhaustionWithAFreshReadingUnderThreshold_ClosesBeforeItsEnd`, `Decide_ExhaustionWithAStaleReadingUnderThreshold_StaysOpen` |
| A candidate at 97.0 has no room, at 96.99 it does, on each window | `Decide_CandidateRoomBoundary_OnEachWindowSeparately` (4 rows) |
| Triggered account not chosen before its reset, and is after | `Decide_AccountThatTriggered_IsNotChosenBeforeItsResetAndIsAfter` |
| Switch now | `DecideManual_*` (8 tests) |
| Every machine on the tired account switched | `Decide_EveryMachineOnTheTiredAccount_IsSwitched` |
| A machine missing the spare blocks that target | `Decide_MachineMissingTheSpare_BlocksThatTarget`, `Decide_NoTargetHeldByEveryMachine_EmailsAndSwitchesNothing` |
| Stale cases (contract 5.2) | `Decide_StaleReadingOfTheAccountInUse_DoesNotTrigger`, `Decide_StaleCandidate_IsNeverChosen`, `Decide_StaleMachine_IsSkippedAndTheOthersStillGo`, `IsReadingStale_ActiveAndSpareLimits_AndNeverRead` |
| A spare whose sign-in expires at or before now + 10 minutes is never a target (Tech Lead's contract addition) | `Decide_SpareSignInExpiringWithinTheCommandLifetime_IsNotChosen`, `DecideManual_NamedTargetWhoseSpareSignInIsExpiring_IsRefused` |
| Forecast, with each holding's sign-in expiry verdict (expired / expires soon within 3 days / fine / not known); next-switch target matches the engine | `LicenseForecasterTests` (17 tests) |

## Commands run, and their results

```
dotnet test src/CcDirector.Gateway.UnitTests/CcDirector.Gateway.UnitTests.csproj --filter "FullyQualifiedName~CcDirector.Gateway.Tests.Licenses"
Passed!  - Failed: 0, Passed: 115, Skipped: 0, Total: 115   (round 2, after rebasing on main with the store work: 70 of them engine and forecast)
```

Revert proof (each mutation applied to the committed engine, the license tests run, then `git checkout` restored it):

| Mutation | Result |
|---|---|
| Threshold `>=` changed to `>` on the 5-hour window | 17 failed, 27 passed |
| Bars ignored | 1 failed: `Decide_AccountThatTriggered_IsNotChosenBeforeItsResetAndIsAfter` |
| Open exhaustion event ignored | 1 failed: `Decide_NoTargetOnTheSecondTick_SendsNoSecondEmail` |
| Restored | 44 passed |
| (after the expiry addition) spare expiry check disabled | 2 failed: both expiry tests |
| Restored | 52 passed |
| (review round) tired check `>=` changed to `>` | 23 failed, 44 passed |
| (review round) 5-hour room check `<` changed to `<=` | 1 failed: `Decide_CandidateRoomBoundary_OnEachWindowSeparately(97, 10, False)` |
| (review round) event not closed by a fresh reading under 97% | 1 failed: `Decide_ExhaustionWithAFreshReadingUnderThreshold_ClosesBeforeItsEnd` |
| (review round) a passed reset still counts as tired | 2 failed: both pre-reset tests |
| (review round) forecast passes every machine, not the switchable ones | 1 failed: `Forecast_NextSwitchTarget_IgnoresAStaleMachineTheWayTheEngineDoes` |
| Restored | 67 passed |
| (round 2) a fresh tired reading no longer keeps the event open | 3 failed |
| (round 2) the event's end never moved later | 2 failed |
| (round 2) `IsTired` reset `>` changed to `>=` | 1 failed |
| Restored | 115 passed |

```
.\scripts\test-local.ps1
```
First round, before the review. Eight suites passed (Engine 103, HostedAgent 89, Launcher 218, Terminal.Avalonia 82, Reclaim 310, setup 39,
setup-engine 875, setup-cli 74). `CcDirector.Core.UnitTests` and `CcDirector.Avalonia.Tests` were STOPPED
by the 120-second budget - not failed - while the machine was at 100% processor with 93 dotnet and test
host processes from other sessions. Neither suite is touched by this change. Run in full directly:

```
dotnet test src/CcDirector.Core.UnitTests/CcDirector.Core.UnitTests.csproj --no-build
Passed!  - Failed: 0, Passed: 1456, Skipped: 0, Total: 1456
dotnet test src/CcDirector.Avalonia.Tests/CcDirector.Avalonia.Tests.csproj --no-build
Passed!  - Failed: 0, Passed: 1022, Skipped: 0, Total: 1022
```

```
.\scripts\test-local.ps1 -Parked
```
First run, on the engine before the expiry addition: the ten default suites all passed again (Core.UnitTests
1456, Avalonia.Tests 1022 and the eight above). It was STOPPED by me before the three parked suites
(Gateway.Tests, Core.Tests, Gateway.UnitTests) finished, because the Tech Lead asked every Developer to hold
gates while the machine was short of memory.

Second run, after the review fixes, at commit f2bb52c8c (rebased on origin/main 1d8a4c2e2), tree clean -
`RESULT: GREEN`, exit 0, every suite `outcome=Completed` with executed equal to total:

| Suite | Passed |
|---|---|
| CcDirector.Core.UnitTests | 1602 |
| CcDirector.Avalonia.Tests | 1044 |
| CcDirector.Engine.Tests | 103 |
| CcDirector.HostedAgent.Tests | 89 |
| CcDirector.Launcher.Tests | 218 |
| CcDirector.Terminal.Avalonia.Tests | 82 |
| CcDirector.Reclaim.Tests | 310 |
| cc-director-setup.Tests | 39 |
| cc-director-setup-engine.Tests | 875 |
| cc-director-setup-cli.Tests | 74 |
| CcDirector.Gateway.Tests (parked) | 3077 |
| CcDirector.Core.Tests (parked) | 4841 |
| CcDirector.Gateway.UnitTests (parked) | 10519 |

This run includes every default suite, so it is also the default gate for this commit. The only change
after it is this proof file.
