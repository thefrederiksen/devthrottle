# Proof - Gateway decision engine and forecast (phase 2, first part)

Branch `license-switching-gw-engine`, cut from origin/main at 54fd5da0d. Pure code only: nothing is wired
to the store, the hub, the command router or a mailer yet - that is the second pull request, after the
storage work merges.

## What is here

- `src/CcDirector.Gateway/Licenses/LicenseDecisionModel.cs` - the engine's own input (a snapshot of one
  tenant) and output (switches, bars, emails, exhaustion events opened and closed, skipped machines).
- `src/CcDirector.Gateway/Licenses/LicenseDecisionEngine.cs` - the tick (`Decide`), Switch now
  (`DecideManual`), and the target choice they and the forecast share.
- `src/CcDirector.Gateway/Licenses/LicenseForecaster.cs` - rate, expected time to 97%, next switch.
- Tests: `src/CcDirector.Gateway.UnitTests/Licenses/` - 44 tests.

## The check (MISSION.md section 7), test by test

| Case | Test |
|---|---|
| Under 97% does nothing | `Decide_UnderThreshold_DoesNothing` |
| 97% on the 5-hour window switches | `Decide_FiveHourWindowAtThreshold_Switches` |
| 97% on the week switches | `Decide_WeekAtThreshold_Switches` |
| Target choice order | `Decide_TargetChoiceOrder_LowestWeekThenFiveHourThenEmail` (3 cases) |
| No target: one email, no switch | `Decide_NoTarget_SendsOneEmailAndSwitchesNothing` |
| A second tick sends no second email | `Decide_NoTargetOnTheSecondTick_SendsNoSecondEmail` |
| Triggered account not chosen before its reset, and is after | `Decide_AccountThatTriggered_IsNotChosenBeforeItsResetAndIsAfter` |
| Switch now | `DecideManual_*` (7 tests) |
| Every machine on the tired account switched | `Decide_EveryMachineOnTheTiredAccount_IsSwitched` |
| A machine missing the spare blocks that target | `Decide_MachineMissingTheSpare_BlocksThatTarget`, `Decide_NoTargetHeldByEveryMachine_EmailsAndSwitchesNothing` |
| Stale cases (contract 5.2) | `Decide_StaleReadingOfTheAccountInUse_DoesNotTrigger`, `Decide_StaleCandidate_IsNeverChosen`, `Decide_StaleMachine_IsSkippedAndTheOthersStillGo`, `IsReadingStale_ActiveAndSpareLimits_AndNeverRead` |
| Forecast | `LicenseForecasterTests` (9 tests) |

## Commands run, and their results

```
dotnet test src/CcDirector.Gateway.UnitTests/CcDirector.Gateway.UnitTests.csproj --filter "FullyQualifiedName~CcDirector.Gateway.Tests.Licenses"
Passed!  - Failed: 0, Passed: 44, Skipped: 0, Total: 44
```

Revert proof (each mutation applied to the committed engine, the 44 tests run, then `git checkout` restored it):

| Mutation | Result |
|---|---|
| Threshold `>=` changed to `>` on the 5-hour window | 17 failed, 27 passed |
| Bars ignored | 1 failed: `Decide_AccountThatTriggered_IsNotChosenBeforeItsResetAndIsAfter` |
| Open exhaustion event ignored | 1 failed: `Decide_NoTargetOnTheSecondTick_SendsNoSecondEmail` |
| Restored | 44 passed |

```
.\scripts\test-local.ps1
```
Eight suites passed (Engine 103, HostedAgent 89, Launcher 218, Terminal.Avalonia 82, Reclaim 310, setup 39,
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
PARKED_RESULT
