# The continuous integration keeper

Reads a repository's own continuous integration run records once a day, judges them against
thresholds that live in **one file**, and raises a report. It **proposes**: it never edits a
workflow, never reruns anything, and never opens, closes or merges anything.

The repository it reads is an **input, not a constant**. Point it at any repository you can read
with the GitHub command line tool.

## The four conditions

| Condition | What it is |
|---|---|
| `budget` | A pull request waits longer for a result than the budget allows. |
| `suite-growth` | A job runs more tests than it did at the start of the window, by more than the share the budget allows. |
| `repeated-red-test` | The same test is named red on several finished runs in a row. |
| `skipped-job` | A run concluded successfully while a job that runs in other runs on the same branch was skipped. |

Each answers one of **three** states, never two:

- **raised** - it is true, and the finding carries the numbers and a proposal;
- **clear** - it was measured and is not true, and the report says what it was measured over;
- **not-measured** - it could not be measured. **This is never a pass.** A window with no pull
  request runs says nothing about how long a pull request waits; a failed run whose log could not
  be read says nothing about which test was red. The command ends with an error, not with success.

## Running it

```
cc-continuous-integration-keeper collect --repository <owner/name> --budget <file> --out records.json
cc-continuous-integration-keeper check   --budget <file> --records records.json --full
```

`check --repository <owner/name>` collects the window and judges it in one step. From this
repository, without installing anything:

```
python3 tools/cc-continuous-integration-keeper/main.py check \
  --budget .github/continuous-integration-budget.json \
  --records tools/cc-continuous-integration-keeper/tests/fixtures/run-records-2026-09-16-to-19.json
```

Exit codes: `0` everything measured and nothing raised, `1` error (including a condition that could
not be measured), `2` usage error, `3` something was raised.

It follows the AXI standard (`docs/axi-standard.md`): compact output, `--json` with every filter
applied to it too, `--fields`, definitive empty states, `help[]` lines, and never a prompt.

## The budget file

One file, no code: [`.github/continuous-integration-budget.json`](../../.github/continuous-integration-budget.json).
Every threshold is read from it and it is validated strictly - a missing key, a key nobody knows, a
value of the wrong kind or out of range is an error naming the key. A budget file that half-loaded
would measure against a default nobody chose.

`repeated_red_test.branches` and `.events` take `"*"`, meaning every one. In this repository they
are set to `"*"` rather than to `main`: main rarely finishes a run, because a newer push cancels the
older one, so a rule written as "three finished runs in a row on main" stays silent through exactly
the kind of event it exists to catch. The file carries that reasoning in its own `notes`.

## Where the numbers come from

- **Run records** come from `gh api repos/<owner>/<name>/actions/runs`, filtered to the workflow the
  budget names and to runs that started inside the window.
- **Job records** are fetched for every run that FINISHED (concluded success or failure). A run
  cancelled by a newer push was cut short, so nothing in it is judged.
- **Failing test names** and **test counts** are not in the run record at all - they are read out of
  the job's own log, recognising the .NET test runner, pytest and vitest. Logs are fetched for every
  failed job of a finished run, and for every job of the earliest and the latest finished successful
  run on the default branch, which is what a growth reading needs at each end.

Anything not fetched is recorded as `not-fetched`, and a log that was asked for and refused as
`unavailable`. Neither is ever recorded as an empty reading.

## What each rule does NOT see

Stated here because a rule whose blind spot is unwritten gets trusted past it.

- **`budget`** measures finished pull request runs only, from the moment a run started to the moment
  it ended. A run cancelled by a newer push is left out and counted separately; if every run in a
  window were cancelled the condition would be not-measured, not clear.
- **`suite-growth`** compares a job's test count at the two ends of the window, so it is blind to a
  job that was **renamed** inside the window (its old and new names each have one reading, and the
  report names them as not compared) and to a runner it does not recognise. A job whose runner it
  cannot read has no reading at all, and no reading is reported as no reading.
- **`repeated-red-test`** counts a run as "not red for this test" when the test is not named in it -
  including a run that never ran the job holding that test. So a workflow that skips the test job on
  some runs breaks a row the keeper would otherwise see, and the keeper under-reports rather than
  over-reports.
- **`skipped-job`** derives what SHOULD have run from what DID run: every job name seen running in a
  finished successful run on the branch is expected in all of them. That means it cannot see a job
  that was skipped in *every* run in the window, and it treats a job that is **absent** from a run
  (added or removed from the workflow inside the window) as absent rather than skipped. It looks
  only at runs that concluded SUCCESS, because a job GitHub skipped after the job it needed failed
  is the failure's shadow rather than news of its own - and a green verdict resting on a job nobody
  ran is the case that matters, since GitHub counts a skipped job as successful when it judges a
  required check.

## The tests

`python3 -m pytest tools/cc-continuous-integration-keeper/tests -q`

They run on every pull request that touches `tools/`, in the `Tool contracts (Python)` job of
`.github/workflows/ci.yml`, and again inside the daily job before it reads anything.

The frozen fixture `tests/fixtures/run-records-2026-09-16-to-19.json` is this repository's own CI
runs for 16 to 19 September 2026 - 485 runs, 231 of them finished, 184 job logs read - collected
with the `collect` command above and not edited afterwards. It depends on no network and on no
date, and it is what proves the keeper raises the test that was red for 19 hours and the median
wait of 76.5 minutes for a green result.
