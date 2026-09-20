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

## Open

- Pull requests not yet opened; the Developers are building.
