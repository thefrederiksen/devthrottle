# Proof - the Licenses API: Switch now, the renewal date, a failed renewal

The follow-up to the pull request that added the three read routes. What it adds:

- `PUT /gateway/licenses/accounts/renewal` and the stored renewal date of a subscription.
- What a usage report says of a holding's last sign-in renewal, stored and folded into `renewalFailed*`.
- `POST /gateway/licenses/switch-now`, calling `LicenseSwitchService.SwitchNowAsync`.
- The Licenses page is offered Switch now on the same picture the route decides on.
- The decision engine names a machine by its label in every sentence that names one.
- One migration, `AddLicenseRenewal`, for both database providers: four columns, and the "C" collation on
  `license_switch_log.FromAccountEmail`.
- The picture the once-a-minute check reads is no longer logged on every read.

Shapes and real answers are in `missions/license-switching/cockpit-api.md`, section 6.

## What was run

| Run | Result |
|---|---|
| `dotnet test src/CcDirector.Gateway.UnitTests --filter "FullyQualifiedName~Licenses"` | 456 passed, 0 failed |
| `dotnet test src/CcDirector.Gateway.Tests --filter "FullyQualifiedName~LicenseHostTests"` (real HTTP against a started Gateway) | all passed, among them the 5 new ones |
| `.\\scripts\\test-database.ps1` (a throwaway PostgreSQL in Docker) | see "The database run" below |
| `.\\scripts\\test-local.ps1 -Parked` | see "The gate" below |

## A defect the real-HTTP test found

`RenewalDate_SetByTheOwnersBrowser_IsStored_AndTheOverviewSaysIt` failed the first time it ran: the route answered
`200` with an EMPTY body, for a good request and for a bad one alike. The handler was mapped as a lambda taking only
the `HttpContext`, which is mapped as a plain request delegate, and the result it returned was dropped. The unit
tests call the handler directly and could not see it. Both writes now execute their result by hand, and the three
host tests of each hold that down: reverting the fix turns them red with "The input does not contain any JSON tokens".

## Mutations - each breaks one behaviour, runs all 456 license unit tests, and is restored

Run with the scratch runner used for the first pull request: one change at a time, the original bytes written back
in a `finally`, and the restored tree rebuilt and rerun at the end (456 of 456).

### The renewal date and a failed renewal

| | The behaviour broken | Went red |
|---|---|---|
| R01 | A renewal date that has passed is still shown as ahead | 3: both "has passed" rows of `RenewsText_SaysTheDateAsAPersonWould_...`, and `RenewsText_TodayIsTheGatewaysDate...` |
| R02 | Today is not said as today | 2 |
| R03 | Another year is not said | 2 |
| R04 | The overview does not carry the stored date | 5 (fold, route and store tests) |
| R05 | A failed renewal is always a warning, even on an expired sign-in | 4 |
| R06 | A failed renewal on a sign-in that only expires soon is a warning | 3 (the 1 second and exactly 3 days rows, and the route test) |
| R07 | One reason word loses its own sentence | 1: `Holding_FailedRenewal_EveryReasonTheContractNames_HasItsOwnWords` |
| R08 | Two reason words share one sentence | 1: the same |
| R09 | An age is said for a failure stamped ahead of the Gateway's clock | 1 |
| R10 | The Director's sentence is dropped when the reason is not known | 1 |
| R11 | The failed renewal text is given when no renewal failed | 2 (one is the overview's exact-shape test) |
| S01 | A report that says the renewal worked does not clear the stored failure | 1 |
| S02 | A reason word outside the contract is stored | 1 |
| S03 | A reason is stored with no failure time | 1 |
| S04 | The Director's sentence is not capped | 1 |
| S05 | The renewal date is not saved | 5 |
| S06 | The date of an unknown account is reported as stored | 2 |
| S07 | The account is looked up by the email as typed, not normalised | 2 |
| E01 | Any date text the machine can read is accepted | 2: the "10/17/2026" and "17 Oct 2026" rows |
| E02 | A caller with no DevThrottle account is not refused | 1 |
| E03 | An unknown account is answered as if stored | 1 |
| E04 | A blank account email is not refused | 1 |
| U01 | The renewal write is allowed to a session key | 2: the PUT row of the guard table, and the raised-session test |
| U02 | The renewal route is added to the reads a session key may make | 1: the GET row |

### Switch now and the machine label

| | The behaviour broken | Went red |
|---|---|---|
| W01 | Switch now runs although the scope in effect is not the caller's own account | 1: `SwitchNow_OutsideTheScopeOfTheCallersOwnAccount_IsADefect_...` |
| W02 | A refusal is answered 400, not 409 | 7 |
| W03 | A refusal does not say why each machine was left out | 2 |
| W04 | The page asks the engine with the picture in which every open switch holds its machine back (the code before this change) | 1: `AMachineWhoseSwitchIsOnlyWaitingForItsDirector_CanBeSwitchedNow_OnThePage_AndByTheRoute` |
| W05 | The page treats no machine as mid-switch | 3 |
| W06 | The engine never adds the user to a machine name | 5 |
| W07 | Two users are told apart only when the machine name is spelled the same | 1 (the "windows" / "WINDOWS" test) |
| W08 | The engine always adds the user | 4 |
| W09 | A failed switch is said to be on the target | 1 |
| W10 | A switch that has not finished is not mentioned | 2 |
| W11 | The machines left out are not in the accepted sentence | 1 |
| W12 | A blank account is passed to the service | did not compile: the compiler refuses a possibly-null account there |
| W13 | The answer gives the log row's identifier as the switch identifier (a mistake this change made and its own test caught) | 1 |
| W14 | The page's sentence for an account no machine can leave does not say why | 2 |
| W15 | A caller with no DevThrottle account reaches the switch service | 1 |
| W16 | A skipped machine's reason does not name the machine | 1 |
| W17 | The target named in the body is ignored | 5 |

No mutation survived.

## What these tests do not show

- No test here drives a real Director. The Director on the other end of the send is made by the test (the unit
  tests) or absent (the host tests, where the switch therefore waits for the machine). The Director's half is proved
  in its own pull request.
- The sixty-second wait of the route is not run for sixty seconds anywhere; the test of a send that is not answered
  uses the answer the router itself gives when that wait ends.
- The host test of an accepted Switch now first ends the open switch its fixture wrote, through the store.

## The database run

`.\scripts\test-database.ps1`, on the tree rebased onto main `30e468c18` with Switch now in it:
`RESULT: GREEN - 71 test(s) executed against the throwaway PostgreSQL, none failed, none skipped.`

`AddLicenseRenewalPostgresTests` is the new one: it migrates to `AddFactoryEmailReports`, stores an account, applies
`AddLicenseRenewal`, checks the four columns and the collation, writes and reads all four columns through the real
`LicenseStore` on PostgreSQL, and migrates back down. `PostgresProviderProofTests.Collation_ExplicitC_...` failed the
first time (68 of 69): its declared list of collated columns did not have `license_switch_log.FromAccountEmail`.
The column was added to that list and the run was green.

## The gate

`.\scripts\test-local.ps1 -Parked` on commit `0480f33c4` (this branch, rebased onto main `30e468c18`). It is RED,
in four tests, none of which this change touches - no file under `src/CcDirector.Core`, its two test projects, or the
window placement code and test is in this pull request's diff:

| Suite | Result |
|---|---|
| CcDirector.Gateway.UnitTests | 11,099 passed, 1 failed |
| CcDirector.Gateway.Tests (four shards) | 829 + 757 + 674 + 811 passed, 0 failed |
| CcDirector.Core.UnitTests | 1,718 passed, 2 failed |
| CcDirector.Core.Tests | 4,584 passed, 1 failed |
| The ten other suites (Avalonia, Engine, HostedAgent, Launcher, Terminal, Reclaim, the three installer suites) | all passed |

The four, and what the re-run of only the failed tests on the same commit (`-RerunFailed`) said:

| Test | First run | Alone | What it is |
|---|---|---|---|
| `GateTestsDoNotDependOnTheMachineTests.No_gate_test_adds_a_sleep_...` | failed | failed again | Main's baseline file was not lowered when other work removed sleeps from three test files it names. Reported with the first pull request; still red on main. |
| `BackgroundWorkRegisterTests.EveryTimerWatcherAndWaitingLoop_...` | failed | failed again | `docs/BackgroundWork.md` says two sites in `GitHubActionsBackend.cs`, the code has one. Reported with the first pull request; still red on main. |
| `WindowRestorePlacementTests.PlaceSwitchedOn_AFollowerNamedFirst_...` | failed | failed again | Failed on every run today, also three times alone before the rebase. The test passes `DateTime.UtcNow` into the placement; this pull request changes neither it nor the code it tests. |
| `UpdaterStateConcurrencyTests.ReadsAndEditsAtOnce_AllSucceed` | failed ("Access to the path is denied") | passed | Flipped once under the load of the gate. In the run before the rebase it passed, and a different test of that suite flipped instead (`RepositoryWatcherIntegrationTests.PeriodicReconciliation_...`, which then passed three times alone). |

The license tests inside that run: every one passed.
