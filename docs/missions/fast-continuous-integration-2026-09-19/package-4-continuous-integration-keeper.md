# Package 4: the continuous integration keeper

A daily scheduled job that reads a repository's own run records, checks the budget from one file,
and raises a report. It proposes and changes nothing.

## What was built

| Where | What |
|---|---|
| `tools/cc-continuous-integration-keeper/` | The keeper: a command line tool with `collect` and `check`, following the AXI standard. |
| `.github/continuous-integration-budget.json` | The one file a person edits. Every threshold is read from it. |
| `.github/workflows/continuous-integration-keeper.yml` | The daily job, at 12:17 Coordinated Universal Time, plus a by-hand run that takes a different repository. |
| `.github/workflows/ci.yml` | One added step, so the keeper's own tests run on every pull request that touches the tools folder. |
| `tools/cc-continuous-integration-keeper/README.md` | How it works, and what each rule does not see. |

The repository it reads is an input. `--repository <owner/name>` on either command, and the daily
job takes one as an input when a person starts it by hand.

**The keeper proposes.** The job has `actions: read` and `contents: read` and no write permission of
any kind. It edits no workflow, reruns nothing, and opens, closes and merges nothing. When something
is raised, the job goes red with the whole report in its summary and keeps the report and the run
records it rests on as an artifact for ninety days.

**Every condition answers one of three states**, never two: raised, clear, or **not-measured**. A
condition that could not be measured ends the command with an error, never with a pass. A window
with no pull request runs says nothing about how long a pull request waits, and a failed run whose
log could not be read says nothing about which test was red.

## What is proved, and how

91 tests, all passing:
`python3 -m pytest tools/cc-continuous-integration-keeper/tests -q`.

**The proof the brief asks for** is `test_01_the_real_week_raises_the_budget_and_the_red_test.py`. It
loads `tests/fixtures/run-records-2026-09-16-to-19.json` - this repository's own CI runs for 16 to
19 September 2026, 485 runs, 231 of them finished, 184 job logs read, collected with the keeper's own
`collect` command and not edited afterwards - and the budget file this repository ships. It asserts
that the keeper raises:

- **the red test, by its full name**,
  `CcDirector.Gateway.Tests.FleetManagerRoutesHostTests.The_session_list_pins_the_Fleet_Manager_and_offers_each_row_its_change_of_owner`,
  red on **9 finished runs in a row** and on **18 of 231** finished runs, first named in run
  35401849157 started 18 September 22:30 and last in run 35459682083 started 19 September 17:57
  Coordinated Universal Time, **19 hours** apart, while 5 runs in the same span were green;
- **the budget**, with **180** finished pull request runs, **28 of them (16 per cent) inside 20
  minutes**, a median of **83.8 minutes**, a ninetieth point of **99.7 minutes**, and a **median of
  76.5 minutes for a green result over 99 runs** - the number the brief states as 76 minutes. The
  test computes that median itself, straight from the records, rather than reading it back out of
  the keeper's own sentence.

The fixture depends on no network and on no date: every time in it is a time in September 2026. The
test also writes the records back out and asserts the file is byte for byte the same, so the
evidence cannot rot into something that no longer reads back.

**Each of the four conditions is shown firing, and shown staying quiet one step below its
threshold** (`test_02`): nine runs in ten inside the budget against eight in ten; a fifth of growth
against a fifth and one test more; two red runs in a row against three; a job that ran against the
same job skipped.

**A quiet week raises nothing** (`test_03`), and every condition comes back *clear* rather than
*not-measured*, with the size of what it looked at beside it.

**Not-measured is never a pass** (`test_04`): an empty window, too few pull request runs, a failed
run whose log could not be read, and a test runner the keeper does not recognise each come back
not-measured, and the command ends with exit code 1.

**The log reader is proved against real logs** (`test_05`): excerpts taken verbatim out of this
repository's own job logs, timestamps and colour codes and all. Including the case that a looser
pattern got wrong: this repository's own test output prints the sentence
`FAIL (1) - the C# fold and the shared answers differ.`, and an earlier pattern read that sentence
as the name of a failing test.

**The collector is proved against the payloads GitHub really answers with** (`test_07`), captured
from the live interface and kept field for field apart from the blocks the keeper never reads.

**A record file is read as strictly as the budget file** (`test_09`): a run that concluded with no
end time, a job record that does not say whether its log was read, a time written some other way -
each is refused rather than half-loaded, because a record file that half-loaded would be judged as
though the parts it dropped had never happened.

**The command is held to the AXI standard** (`test_08`): every finding reads back exactly out of the
default output with the shared parser, no identifier is ever cut short, `--json` carries every field
and every filter applies to it too, an unknown flag or field fails with the valid values, the output
is ASCII only.

**Run for real, not only in tests.** Against the live repository over the seven days to 20 September
2026: 628 runs, 307 finished, 1,711 job records, 217 job logs read, six minutes. It raised the same
red test, the budget at 13 per cent inside 20 minutes, and two growing suites - the .NET job up
**23.2 per cent** and the web job up **40.8 per cent** in six days.

## What is NOT proved

- **Condition 4's rule is a derivation, and here is exactly what it is.** "What should have run" is
  taken from what DID run: every job name seen running in a finished successful run on the branch is
  expected in all of them. Where it would be wrong: (a) a job skipped in *every* run in the window is
  invisible, because nothing establishes that it should have run; (b) a job that is **absent** from a
  run - added to or removed from the workflow inside the window - is treated as absent rather than
  skipped, on purpose, or every run before a job existed would raise a finding; (c) it looks only at
  runs that concluded SUCCESS, so a job GitHub skipped because the job it needed failed is left
  alone, which also means a skip inside a failed run is never reported; (d) a repository that
  deliberately skips jobs by path on its default branch would raise a finding on every run, and would
  need `skipped_job.branch` pointed somewhere else. **It did not fire on the real week** - this
  repository skipped no job it should have run in those four days - so condition 4 is proved firing
  only on built records, not on real ones.
- **"Any repository" is proved by construction, not by a second repository.** The repository is a
  parameter everywhere and nothing about this one is hard-coded, and the three test runners
  recognised (the .NET runner, pytest, vitest) are common rather than particular. But the keeper has
  been run against one repository. A repository whose tests are run by something else gets no test
  counts and no failing test names, and the keeper reports that as no reading rather than as a clean
  week - which is the right answer, but it is a much weaker keeper there.
- **The daily job has never fired.** A scheduled workflow only runs from the default branch, so the
  schedule itself is unproven until this is merged. The live run above used a personal token; the
  job uses `GITHUB_TOKEN` with `actions: read`, which should be enough to list runs and jobs and
  download job logs, and that has not been demonstrated.
- **Growth is read at the two ends of the window only.** A suite that doubled on Tuesday and was cut
  back on Thursday reads as no growth. A job **renamed** inside the window has one reading at each
  name and is not compared at all; the report names those jobs rather than passing over them
  silently, which is how the Python job appears in the real week - it became a matrix and changed
  its name.
- **The budget is measured from when a run started to when it ended**, not from when it was created,
  so the queue wait is outside the number. On this repository the two differ by well under a minute
  at the median. Runs cancelled by a newer push are left out and counted separately, because a
  cancelled run's length measures the cancelling.
- **One budget file names one workflow.** A repository with two workflows worth watching needs two
  budget files and two runs of the keeper.
- **Job logs age out.** GitHub keeps them ninety days by default, so a window older than that reads
  as unavailable, and the red-test rule comes back not-measured rather than clear.
- **A rerun is not distinguished.** The attempt number is recorded but not judged; the run list
  answers with the latest attempt, and the keeper judges that.
- **The keeper does not open an issue.** "Raises a report" is a red scheduled job with the report in
  its summary and in an artifact. Whether it should also open an issue is an owner decision that has
  not been taken; it would need a write permission this job deliberately does not have.

## For the Delivery Lead to decide

1. **Condition 3's scope** - raised separately on 20 September. The brief says "three finished runs
   in a row on the main branch". On the real week that rule NEVER FIRES: of the 18 finished runs that
   named the red test, exactly one was a run on main, and the longest row on main is 1, because a
   newer push cancels the older run and only 51 of 135 runs on main finished at all. Over every
   finished run the longest row is 9. So the scope is a setting in the budget file
   (`repeated_red_test.branches` and `.events`, where `"*"` means every one), this repository's file
   uses `"*"` with the reasoning written into the file's own notes, and both settings are proved:
   the wide scope raises the real test, the main-only scope comes back not-measured on the same
   records rather than clear. **Confirm or overrule.**
2. **The keeper is not in `tools/registry.json`.** It is a repository tool, not a shipped one, and it
   needs the GitHub command line tool and a sign-in. `cc-history` and `cc-status` are also folders
   under `tools/` with no registry row, so there is precedent, and leaving it out keeps it off the
   installed product's Tools page and out of the shipped-tool contract guard. Say if it should be
   registered with `"ship": false` instead.
3. **Two findings the live run made that belong to other packages.** The .NET job's test count grew
   23.2 per cent and the web job's 40.8 per cent in the six days to 20 September - that is package 7's
   territory, and the keeper will raise it again tomorrow. And the budget is broken by a wide margin
   today: 13 per cent of finished pull request runs inside 20 minutes, against the nine in ten the
   owner ruled. The keeper will raise that every day until packages 1, 2 and 3 land, which is what it
   is for.
