# Phase 1 record - fast continuous integration

Kept by the Delivery Lead. Phase 1 is packages 1, 3 and 4 of the brief, which do not depend on one
another. One pull request per package, opened by the Delivery Lead against main and not merged by it.

## Seats

| Seat | What it does | Worktree | Branch |
|---|---|---|---|
| Delivery Lead | Drives phase 1, opens the pull requests, lands this record | `devthrottle-fast-ci` | `mission/fast-continuous-integration` |
| Developer, package 1 | Moves the sixteen-minute hosted image test to the deploy workflow, and settles how that workflow treats a cancelled run | `devthrottle-ci-p1` | `ci/package-1-hosted-image-test-to-deploy` |
| Developer, package 3 | Puts the PostgreSQL proofs into their own project with a path-started Linux job | `devthrottle-ci-p3` | `ci/package-3-postgres-proofs-own-project` |
| Developer, package 4 | Builds the continuous integration keeper as a daily scheduled job | `devthrottle-ci-p4` | `ci/package-4-continuous-integration-keeper` |

Each worktree was cut from `origin/main` at commit `736d9afe2` on 19 September 2026. No two
workstreams share a tree.

## What each Developer was told the proof is

Taken from the brief's own "proof owed" lines, and sharpened into a presence rather than an absence
in each case, because a check whose pass condition is an absence certifies a run that never happened.

- **Package 1.** The test seen running and passing in a deploy rehearsal, and the continuous
  integration suite about sixteen minutes shorter. The moved test must fail closed: the deploy does
  not happen unless the test actually ran and passed, asserted as a counted number of tests executed.
  Separately, in writing: how the deploy workflow's check of the shipping commit's check runs treats
  a commit whose continuous integration run was cancelled. Most runs on main are cancelled - one
  hundred and fifty of three hundred in the frozen baseline, and only twenty-one of ninety-seven runs
  on main finished at all - so if that check accepts a cancelled run, an untested commit can deploy.
- **Package 3.** The job seen starting on a migration change and not starting on an unrelated one,
  shown with two real runs; and zero skipped tests inside it, asserted as a counted number of tests
  executed rather than as the absence of failures. The Developer counts the PostgreSQL tests itself:
  the brief's "about fifty-six" is a count of test attributes cross-checked against one log's skips
  and is not proven. The path list is checked against code that reaches the database without naming
  the PostgreSQL driver, which the brief records as unchecked.
- **Package 4.** Fed the week of 16 to 19 September 2026 as a committed fixture, the keeper raises
  both the eighteen-hour red test and the seventy-six-minute median, by name and by number. Each of
  the four conditions is proved to fire, and a quiet week is proved to raise nothing.

## Standing limits given to every Developer

- No grant to deploy to production, tear anything down, or send anything outward. Package 1 was told
  explicitly never to run the real hosted deploy and to stop and report if a rehearsal cannot be had
  without one.
- Nothing runs hidden: no sub-agents, no backgrounded work, no detached processes.
- No test's assertions are changed. Moving a test is in scope; rewriting what it asserts is not.
- Each says plainly, in its pull request body, what is not proven.

## Package 1 - open as pull request 3161

Branch `ci/package-1-hosted-image-test-to-deploy`, opened 20 September 2026, not merged.
`HostedImagePublishedArtifactTests` moved into its own project that is deliberately absent from
`cc-director.sln`, so the continuous integration test step loses it structurally rather than by a
filter argument anyone could drop, and a new reusable workflow runs it as a job the deploy declares
`needs:` on.

**Checked by the Delivery Lead, not taken on the Developer's word.** All three cited runs were read
from the run records:

| Run | What it shows | Conclusion |
|---|---|---|
| 35483939371 | The test runs and passes in the new workflow | success |
| 35483844691 | The test deliberately marked skipped; `dotnet test` still exited zero, and the verdict step refused it | failure |
| 35483737565 | The exact call the deploy makes, from a branch, refused at the first step | failure |

The second of those is the one that matters: it shows the pass condition is a presence. A counted
number of tests actually executed, read from what the built assembly declares, rather than the
absence of a failure.

**Not proven, and said plainly in the pull request body:** the sixteen-minute saving is predicted,
not measured - it is quoted from the log of continuous integration run 35461379953, where the test
took 16 minutes 32 seconds of a 103 minute 9 second job. Opening the pull request produces the
measurement, and it goes here when the run finishes. No real deploy was run; the Developer had no
grant and took none. The merged workflow file has not itself been run, because a dispatch cannot
start a workflow that is not yet on the default branch; one dispatch after merging confirms it.

## The live fail-open on the production deploy path

Package 1's second half was to establish how the deploy workflow treats a commit whose run was
cancelled. The answer is that a cancelled run IS refused once it exists - but the gate is a
fail-open anyway, and it is operating today.

Its pass condition is an absence: nothing has failed. A commit whose checks have not finished yet
satisfies it. **Deploy run 35406585169 on 18 September started eleven seconds after the push,
printed that the commit had no check runs of its own yet, passed its own gate, and shipped. That
commit's `Build & Test (.NET)` later concluded cancelled and its `CI result` concluded failure.**
Fifteen of the last fifteen hosted deploys passed the gate with no finished check.

The Delivery Lead verified this directly from the deploy run's log and the commit's check runs,
rather than relaying the Developer's report, because it is the finding going in front of the owner.

It was deliberately not fixed. Requiring a named successful check is a few lines, but today it
would refuse nearly every deploy, because the .NET job takes about a hundred minutes and most runs
on main are cancelled by the next push. The owner has ruled twice against waiting on continuous
integration, and his ruling 7 already sets the right order: make the run fast, hold the budget for
a week, then require a green result. The question inside it that only he can answer - what "tested"
should mean for a commit that will never get a verdict of its own - was raised to him on
20 September with that recommendation. The full write-up is in
`how-the-deploy-gate-treats-a-cancelled-run.md` beside this record.

## Package 4 - open as pull request 3163

Branch `ci/package-4-continuous-integration-keeper`, opened 20 September 2026, not merged.
`tools/cc-continuous-integration-keeper` with `collect` and `check`, every threshold in
`.github/continuous-integration-budget.json`, a daily job at 12:17 Coordinated Universal Time that
takes a repository as an input, and its tests added to the pull request run. The job holds
`actions:read` and `contents:read` and no write permission at all: it proposes and changes nothing,
which is the owner's ruling.

**Checked by the Delivery Lead.** The ninety-one tests were run here, not taken on report: all
green. The named assertions were read as well as counted, because a suite that passes proves
nothing until you know what it asserts. The two that matter:

- `test_the_eighteen_hour_red_test_is_raised_by_name_and_by_number` and
  `test_the_budget_is_raised_with_the_76_minute_median` - the proof the brief asked for, against a
  frozen fixture of this repository's own records for 16 to 19 September, which
  `test_the_fixture_still_reads_back_exactly` pins as genuine.
- `test_04_an_unmeasured_condition_is_never_a_pass`, eight tests ending in
  `test_the_command_ends_with_an_error_when_it_measured_nothing`. A keeper that reports "clear"
  because it could read nothing would be the same fail-open this mission exists to end, and it
  refuses to.

**Two decisions taken here rather than sent to the owner**, both written into the pull request body:

1. **Condition 3 is scoped to every branch, not to main.** The brief words it as three finished runs
   in a row *on main*. Measured against the very week it exists to catch, that never fires: one of
   the eighteen runs naming the red test was on main, and the longest row on main is one, because
   only fifty-one of a hundred and thirty-five runs on main finished that week. The brief's wording
   and the brief's proof owed cannot both be satisfied, and the proof owed is the outcome the
   package exists for. The cause is the owner's own ruling 3 - cancellation applies on main too -
   so this is that ruling's consequence surfacing, not a disagreement with it. It is a setting,
   both settings are proved, and one edit moves it back.
2. **The keeper is not registered in `tools/registry.json`**, matching the two sibling repository
   tools `cc-history` and `cc-status`, which are unregistered for the same reason.

**Not proven, and said plainly in the body:** condition 4 fires on built records only - this
repository skipped no job it should have run in those four days. The daily schedule itself cannot
run until this is on main, and with it the assumption that the workflow token can download job
logs; the live run used a personal token. "Any repository" is proved by construction and against
three common test runners, not against a second repository. Job logs age out at ninety days, and an
older window reads as not measured rather than as clear.

**Two live findings routed elsewhere:** the keeper's real run over the seven days to 20 September
reports the .NET suite up 23.2 per cent and the web suite up 40.8 per cent in six days - package 7's
territory - and reports the budget broken by a wide margin, which it will raise daily until
packages 1, 2 and 3 land. Both are the keeper working.

## The measurement - 41 minutes 44 seconds, and only 16 of them explained

Two runs at the **same base commit**, `736d9afe2`, so this is like-for-like rather than a comparison
against the frozen baseline.

| | `Build & Test (.NET)` | `CcDirector.Gateway.Tests` | Tests in that suite |
|---|---|---|---|
| main, run 35478491468 | 100 minutes 30 seconds | 95 minutes 13 seconds | 2,678 |
| pull request 3161, run 35484228243 | 58 minutes 46 seconds | 55 minutes 8 seconds | 2,677 |

The predicted saving was the removed test's own time, 16 minutes 32 seconds. The measured difference
is 41 minutes 44 seconds. **Only the test's own time is accounted for; the remaining twenty-five
minutes are not explained and are not claimed.** One run each is not a distribution and variation
between GitHub's machines has not been measured. The saving is at least the test's own time; the
rest needs more runs before anyone relies on it. This is recorded here rather than quietly rounded
up, because a number nobody can explain is not a result.

## Main is red, and the redness moves - a defect in the tests

Run 35484228243 on pull request 3161 is red, and so is run 35478491468 on main at that pull
request's own base commit. Seven failures each, all inside `CcDirector.Gateway.Tests`, none in
anything package 1 built. Four are the same on both sides:

- `HostedDirectorTunnelGovernanceTests.A_tunnel_push_reaches_the_ledger_and_the_morning_report`
- `VoiceServingLoopIsolationTests.Voice_sweep_reaches_only_the_owning_tenants_director`
- `VoiceSweepBudgetTests.Cached_audio_cannot_hide_a_terminal_failure_after_a_later_user_message`
- `WingmanMenuGuardProofTests.Prompt_WithoutMenuGuard_MenuOnScreen_ForwardsAndNeverReadsTheScreen`

Three fail only on main - `SessionSupervisorLiveWiringTests.AParkedSessionThatDiedOnAConnectionFault_IsSentContinueOverTheTunnel`,
`TunnelExplicitRouteProofTests.RecapGenerate_ridesTheTunnel_andPreservesThe201AndModel`,
`TunnelExplicitRouteProofTests.RequestDeletion_ridesTheTunnel_andSynthesizesPendingDeletionTrue` -
and three only on the pull request -
`StreamCommandTests.GatewayPromptEndpoint_RoutesDownTheStream_NotHttp`,
`TunnelExplicitRouteProofTests.RecapRead_ridesTheTunnel`,
`TunnelExplicitRouteProofTests.Summary_ridesTheTunnel`. `TunnelExplicitRouteProofTests` contributes
two different tests to each side.

**That shift is the finding, and it is a defect in the tests rather than in the product.** The only
difference between the two runs is which tests ran and in what order: package 1 removes one class
from a suite that runs strictly one test at a time. Tests whose result depends on what ran before
them are what the brief predicted would surface when this suite is disturbed, and the brief's
instruction is that they are fixed, not hidden. Nothing here is unexplained in its cause - it is
shared state or ordering inside `CcDirector.Gateway.Tests` - but which tests it takes down has to be
named one by one.

Main has been red since at least 00:20 on 20 September. That is the blind spot this mission exists
to close, and it is the condition package 4's keeper raises by design.

## The deadlock this creates, raised to the owner

The brief holds package 2 until main is seen green on a finished run. Main cannot go green until
these order-dependent tests are fixed, and the brief assigns that fixing to package 2 itself
("Tests that only pass because of what ran before them will surface here; they are fixed, not
hidden"). The two conditions cannot both be satisfied in the order the brief sets.

Raised to the owner on 20 September with this recommendation: make the order-dependent tests a
package of their own, taken before package 2, because package 2's six-way split disturbs ordering
far more violently than removing one class did, and going into it on a suite that already fails
differently depending on what ran before would make its own presence check unreadable.

## Open

- Package 3 still building.
- All three branches are based on `736d9afe2`; main has since moved on. Each is merged by the
  Architect, which rebases or lets the merge carry it.
- The unexplained twenty-five minutes of the measured saving.
