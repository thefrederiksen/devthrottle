# Step 2 proof: the 80% warning covers both windows again, for faults only

Mission "Licenses - Loud When Stranded", step 2. Branch `loud-switch-2-cannot-switch-at-80`. It was built on
5ba96eb0a (step 1). Step 1 was then reverted on main (7c173f301, pull request 4512) and relanded with the same product
code plus a host-start test (2349a1ea0, pull request 4525). This branch is rebased onto 2349a1ea0. The Licenses code at
5ba96eb0a and at 2349a1ea0 is identical, and the rebase was clean.

## What changed, in plain words

- **A new warning, `cannot-switch-at-80`.** It opens when an account in use is at or above 80% of EITHER window, the
  weekly one included, and a machine on it is ready for no account that has room, while some account does have room.
  It sends one email, one push and one banner line, with the six-hour hold-down. The email names the machine, the
  account it is on, the account it needs, and the one thing to do.
- **It is step 1's rule at 80%, not a second copy.** The engine has one function, `MachinesThatCannotSwitch(snapshot,
  percent)`. The stranded alarm asks it at 97%, the warning at 80%. Both alarms are built by the same
  `LicenseStrandedAlarm.ConditionsAt`. So the warning inherits step 1's rules: a stale, mid-switch, refused or held
  machine is never named, and nothing opens while the Gateway's own sign-in is repairing the machine. The repair is
  bounded at 15 minutes, so a Director that never answers is still loud. The Tech Lead confirmed this ruling.
- **One machine, one alert.** The warning stands down for any machine whose stranded alarm is open. So a fresh picture
  at 100% sends one push per stranded machine, not two. A rise from 80% to 97% sends the warning and then the stranded
  alarm, in that order.
- **Covered by any account with room.** The readiness check now calls a machine ready when it is ready for at least one
  account with room, named against the best of those it is ready for. That is where the switch itself would move it.
  Only a machine ready for none of them is "not ready".
- **`not-ready-five-hour` is retired.** The new warning covers the five-hour window too, so keeping the old kind would
  be two alarms for one fault. Its stored rows close on the first check, and it is never raised again.
- **The #4249 boundary holds.** Usage alone, at any level, with every machine ready for an account with room, raises
  nothing.

## The runs in this folder

Run after the rebase onto 2349a1ea0:

| File | What it is | Result |
|---|---|---|
| `licenses-suite.txt` | `dotnet test src/CcDirector.Gateway.UnitTests --filter "FullyQualifiedName~CcDirector.Gateway.Tests.Licenses"` | 1331 passed, 0 failed |
| `replay-step-3-on-step-2.txt` | The step 3 replay (`LicenseStrandedReplayTests`, commits 3a67367bc and 1ad23b948) cherry-picked onto this branch WITHOUT committing, run, then removed | 13 passed, 0 failed |
| `host-start-postgres.txt` | `.\scripts\test-database.ps1 -Filter FullyQualifiedName~LicenseHostStartPostgresTests`: step 1's reland test, a hosted host on a throwaway PostgreSQL serving its health check while its Licenses minute check runs | 1 passed, 0 failed, 0 skipped |

Run before the rebase, on 5ba96eb0a, whose Licenses code is identical to main's:

| File | What it is | Result |
|---|---|---|
| `test-local.txt` | `.\scripts\test-local.ps1`, the default gate | Red in two places, both outside this change (below) |
| `avalonia-tests-alone.txt` | `dotnet test src/CcDirector.Avalonia.Tests`, run on its own | 1209 passed, 0 failed |
| `test-local-gateway.txt` | `.\scripts\test-local.ps1 -Gateway`, the parked host-bound suite the gate named as a coverage gap | Green: 1810 run, 3 skipped, 0 failed |

The default gate had two red suites:
- `cc-director-setup-engine.Tests`: the 10 known `MacAppNameAndPlaceTests` failures (#4484). They are red on main too.
- `CcDirector.Avalonia.Tests`: the suite started and wrote no result, and the run was marked over budget. Run on its own
  it passes 1209 of 1209. This change touches no Avalonia code. The gate's `-RerunFailed` refuses a run marked over
  budget, which is why the suite was run directly.

Every other suite passed, Core and the Gateway unit tests included.

## What the new tests pin (`LicenseCannotSwitchAlarmTests`, 23 tests, over the real store, sign-in service, approver check and alarm service)

- Weekly at 80% raises the warning: one email, one push, one banner line, with the problem and action word for word.
- The five-hour window at 80% raises it too. Both windows high give one warning naming both. Under 80% raises nothing.
- A machine ready only for the SECOND account with room is "Ready for account-c", no sign-in starts, and nothing is
  raised.
- A stale machine is never warned about.
- No account with room raises no warning: that is status, as before.
- The #4249 boundary: 80%, 97% and 100% of either window, with every machine ready (for B, or only for C), raise
  nothing.
- The warning stays open on the next check and sends nothing more. It closes when the machine becomes ready, when the
  account falls under 80%, and when the machine leaves the account. It stays open while the reading is stale.
- The hold-down: a warning that comes back within six hours tells nobody, and after six hours it tells the owner again.
- A fresh picture at 100% with two stranded machines gives two pushes, one per machine, both the stranded alarm, and no
  warning.
- 80% then 97% of the week gives two pushes in order (warning, then stranded) and one banner line throughout.
- An open alarm of the retired five-hour kind closes, and only the warning is raised.

The step 1 tests that pinned the five-hour alarm during a repair were changed to the confirmed ruling.
`Tick_At97OfTheFiveHourWindow_WhileTheGatewayRepairs_NeitherAlarmOpens` now holds that neither alarm opens while a
young repair runs. The 80-to-97 deaf-Director test now opens the warning at 16 minutes, saying the sign-in got no
answer, and the stranded alarm takes over at 97%.

## What this does not prove

- The default gate and the `-Gateway` run were not repeated after the rebase. Besides step 1's revert and reland, the
  rebase brought in eight other commits from main (deploy workflows, factory pages and email, microphone meters, setup
  tests, a license renewal test). Those are main's own, and this change does not touch their code.
- Nothing here was deployed.
