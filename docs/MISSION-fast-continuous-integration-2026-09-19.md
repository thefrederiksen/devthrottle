# Mission: fast continuous integration, and a release gate with no database

**Status: ACTIVE.** Chartered by the owner on 19 September 2026. Branch:
`mission/fast-continuous-integration`. How a mission conducts itself is in
[.claude/skills/mission/SKILL.md](../.claude/skills/mission/SKILL.md); this document describes the work
and nothing else.

---

## Why

A release could not be gated on a Mac. The release gate
(`scripts/test-local.ps1 -Parked -Configuration Release`) stopped with "Docker is not available, and
these suites need a PostgreSQL server", and the release shipped without it. The owner's words: why are
we using PostgreSQL to run this; if it is database tests that need a database that is really stupid; the
whole thing takes too long and is too complicated; continuous integration takes an awful long time.

What was found, measured on 19 September 2026 from 300 runs of the continuous integration workflow:

- A pull request waits a median of **76 minutes** for a green result. Comparable open source projects,
  measured the same day from their own run records, sit between 5 and 18 minutes.
- The build is not the cost. Across 119 runs the solution builds in a median of 3.4 minutes. The test
  step takes a median of **82 minutes**, and in the run examined 97 of 103 minutes was one suite,
  `CcDirector.Gateway.Tests`: 2,678 tests run strictly one at a time, one of which
  (`HostedImagePublishedArtifactTests`) takes 16.5 minutes on its own.
- About 56 of roughly 16,000 tests need PostgreSQL. They prove our own migrations and our restricted
  database role on the hosted Gateway. Continuous integration starts no database, so they are skipped
  there every time; the local release gate was the only place they ever ran. One class of six tests
  (`HostedSchemaRefusesAnUnownedRowTests`) is the whole reason the unit suite demands a database.
- Apart from those tests, the local release gate repeats what continuous integration already runs.
- Nobody waits for a result that takes 76 minutes, so a test stayed red on main for 18 hours unnoticed.

**True when this mission is finished:** a pull request gets its result inside 20 minutes; a release is
gated by its own workflow, from any machine, with no database and no Docker; the PostgreSQL proofs run
when database-facing code changes and before every hosted Gateway deploy, and are never silently
skipped where they are meant to run; and two standing agents keep it that way. This is a problem every
team with a growing test suite has, so the two agents are built to work on any repository DevThrottle
is pointed at, not only this one.

## The baseline, frozen

Measured 16 to 19 September 2026. Every later claim of improvement is against these numbers.

| What | Value |
|---|---|
| Pull request, green result, median | 76 minutes |
| Push to main, green result, median | 88 minutes |
| Test step of the .NET job, median over 56 successful steps | 81.9 minutes |
| Build step, median over 114 | 3.4 minutes |
| `CcDirector.Gateway.Tests`, one green run | 97 minutes, 2,678 tests, 56 skipped |
| `CcDirector.Core.Tests`, same run | 10.7 minutes, 4,464 tests |
| `CcDirector.Gateway.UnitTests`, same run | 8.7 minutes, 6,387 tests |
| Runs cancelled by a newer push | 150 of 300 |
| Runs on main that finished | 21 of 97 |

Which of the two smaller suites owns which total was worked out from test namespaces in an interleaved
log and could be swapped. The 97 minutes is certain.

## Rulings

Stated by the owner:

1. The release workflow runs the full tests itself and only then tags and publishes. The local
   `-Parked` gate and its Docker rig are retired.
2. PostgreSQL proofs run only when database-facing files change - "if we're not making any SQL
   changes, why run SQL tests" - in their own project.
3. A newer push cancels the older run, on pull requests and on main. This stays.
4. The two cheap steps go ahead: move the 16.5-minute test, and split the slow suite across six
   machines.
5. Grouping several pull requests into one run is not pursued now. (GitHub's merge queue is not offered
   on a repository owned by a personal account.)
6. The budget is 20 minutes for nine pull request runs in ten.
7. Once the run has held that budget for a week, a green result becomes a required check on main.
8. Two standing agents are wanted: one that watches continuous integration and keeps it fast and
   correct, one that optimises the tests so there is the right amount of coverage without the cost.

Inferred, not stated - challenge these if they look wrong:

- The PostgreSQL proofs also run before every hosted Gateway deploy, because that is the only thing
  they protect.
- `-Parked` is retired last, after one real release has gone through the new workflow.
- The standing agents propose changes as pull requests and never change a workflow on their own.
- The test optimiser's first output is a report that changes nothing.

## The work, in the order it lands

One pull request per package, each carrying its measured before and after.

1. **Move `HostedImagePublishedArtifactTests` to the hosted deploy workflow.** It proves a property of
   the published hosted image. It must still fail closed and block the deploy. While there, establish
   how the deploy workflow's existing check of the shipping commit's check runs treats a commit whose
   run was cancelled - most runs on main are.
   *Proof owed:* the test seen running and passing in a deploy rehearsal; the suite about 16 minutes
   shorter.
2. **Split `CcDirector.Gateway.Tests` across six machines.** Build once, share the output, each machine
   runs its share of the test classes. The shares are computed from the list of tests the suite itself
   reports. A final step adds up the tests run on all machines and fails unless the total equals that
   list - a check for a presence, never for the absence of failures.
   *Proof owed:* that final step seen failing when a class is deliberately left out of every share;
   the measured run time including any wait for machines. GitHub's published limit for a free account
   is twenty machines at once and one run would use about fifteen; if runs queue, fall back to four
   machines and fewer Python machines on pull requests.
   Predicted from one run's class times: about 17 minutes with six machines, 23 with four. Tests that
   only pass because of what ran before them will surface here; they are fixed, not hidden.
3. **PostgreSQL proofs into their own project.** A Linux job that starts and discards its own database,
   started by these paths: `src/CcDirector.Gateway.Migrations.Postgres/`, the `Data`, `Stats` and
   `Tenancy` folders of `src/CcDirector.Gateway/`, the proofs themselves, `Directory.Packages.props`,
   and the workflow file. The hosted deploy requires it. `CcDirector.Gateway.UnitTests` loses its one
   database class and needs no database.
   *Proof owed:* the job seen starting on a migration change and not starting on an unrelated one; zero
   skipped tests inside it.
4. **The continuous integration keeper.** A daily scheduled job that reads the run records, checks the
   budget from one file, and raises a report when the budget is broken, a suite has grown by more than
   a fifth in a week, the same test is red on three finished runs in a row on main, or a job that
   should have run was skipped.
   *Proof owed:* fed the week of 16 to 19 September, it raises the 18-hour red test and the 76-minute
   median.
5. **The release workflow.** Started by hand with a version number; runs the full tests, reusing the
   deploy workflow's green-check pattern; tags and publishes only when green. The release run-book is
   rewritten to match, including its stale instruction to run the installer tests by hand. Not started
   until v2.8.0 is out.
   *Proof owed:* a rehearsal on a throwaway version that goes red and leaves no tag behind; then one
   real release.
6. **Retire `-Parked`,** the Docker rig and the parked lists from `scripts/test-local.ps1`. Only after
   package 5's real release. The two-minute default run is not touched.
7. **The test optimiser, as a report first:** slowest classes, tests whose time is sleeping, tests that
   start a whole Gateway host to check one value, duplicates, and a proposed sorting of tests into
   tiers (every change, every pull request, timed or before release, started by paths). It will not
   propose removing or demoting a test without naming the test that still covers the same code.

Packages 1, 3 and 4 do not depend on each other. Package 2 follows 1. Package 5 waits for v2.8.0.

Before package 2: main must be seen green on a finished run. The test
`FleetManagerRoutesHostTests.The_session_list_pins_the_Fleet_Manager_and_offers_each_row_its_change_of_owner`
was red in 14 runs between 18 September 23:25 and 19 September 17:57 (times in Coordinated Universal
Time) while five runs in the same window were green. A change merged at 18:25 and the next finished
run was green; no run on main has finished since. If it is red again it is diagnosed first - as a
defect in the test or in the product, named as one or the other.

## Out of scope

- Sharing one Gateway host per test class instead of one per test (759 places across 206 files start
  one). Decided only after packages 1 and 2 are measured.
- Moving the repository to an organization to obtain the merge queue.
- Running main on a timer instead of on every merge.
- Any change to the two-minute default local run.
- Any change to what the tests assert.

## Not proven at the start

- Nothing was measured locally; all timings are from GitHub's machines.
- The six-machine prediction is from one run and excludes the cost of handing built output between
  machines.
- "About 56" PostgreSQL tests is a count of test attributes cross-checked against one log's skips.
- The path list in package 3 covers every product file that mentions the PostgreSQL driver today; it
  was not checked against code that reaches the database without naming the driver.
