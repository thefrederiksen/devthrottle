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

## Open

- Packages 3 and 4 still building.
- The measured .NET job time from pull request 3161's first run, to replace the predicted saving.
