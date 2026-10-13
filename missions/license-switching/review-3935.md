# Review of pull request 3935 - the fix for the red continuous integration run on main

Reviewed commit: `a469699fc` (the pull request head), against its merge base `30e468c18` and against
origin/main as it stood at the end of the review, `e043550fe`.

## Verdict

**One finding, and it blocks the merge as the branch stands: the pull request now conflicts with main.**
The four fixes of our own failures are sound. Each test still proves what it claimed, and I measured
that rather than read it. Once the branch is rebased and the conflict is resolved by taking main's
line, I have nothing against it.

## Finding 1 - the branch conflicts with main, and two of its six parts are already on main

While this review ran, pull request 3937 ("Bank the machine-dependence baseline and correct the
background-work register after the flaky test fixes") merged to main. It made the same two edits this
pull request makes for the two failures that were not ours.

Proof:

- GitHub reports the pull request as `mergeable: CONFLICTING`, `mergeStateStatus: DIRTY`.
- `git merge-tree --write-tree HEAD origin/main` reports
  `CONFLICT (content): Merge conflict in docs/BackgroundWork.md`. Nothing else conflicts;
  `tools/cc-devthrottle/src/cli.py` merges by itself.
- The baseline file `GateTestsDoNotDependOnTheMachine.baseline.txt` is byte for byte the same on main
  and on this branch (`git diff origin/main HEAD` on it is empty). That part of the pull request is
  now a change that changes nothing.
- The register row for `GitHubActionsBackend.cs` gives one site on both. Main's row also carries a
  sentence this branch does not: "Both waits go through the backend's one `WaitAsync` seam, which is
  `Task.Delay` in the product, so the code has one site". That sentence is the reason the count is
  one, so main's row is the better one.

What to do: rebase onto origin/main and take main's version of the register row. After that the pull
request holds only the four fixes of our own failures, and the last paragraph of the commit message
(the register and the five baseline lines) should be removed, because it will describe edits the
commit no longer makes.

This is the only thing I am asking to be changed.

## What I checked and found sound

### The click import (right for both dependency sets)

- I built two throwaway environments matching the two legs of the continuous integration job: the
  floor (Typer 0.16.1, Click 8.2.1, Rich 13.0.0, Requests 2.31.0) and the latest (Typer 0.27.3,
  Rich 15.0.0, and no Click package installed at all).
- On an out-of-tree copy of main, the latest leg fails to load 52 test modules, and the floor leg
  fails exactly one test: the summary of `license get-browser` is 83 characters. Both are the
  failures the commit message names.
- On the pull request head, the whole `tools/cc-devthrottle/tests` suite passes on both:
  **4,298 passed, 3 skipped on the floor, and 4,298 passed, 3 skipped on the latest.**
- `typer.Abort` is `click.exceptions.Abort` on the floor and `typer.exceptions.Abort` on the latest.
  In both the class is named `Abort`, which is what the shared failure reporter matches on
  (`tools/cc_shared/tool_errors.py` line 420 looks for the name, not the class), so a sign-in the
  caller called off is still read as the caller's stop on both. The existing test
  `test_cli_signIn_CallerClosesStandardInput_IsCalledOff_NotAToolFailure` passes on both legs.
- The new test `test_no_source_file_ImportsTheClickPackage` can fail: I added an indented
  `import click` to a source file in an out-of-tree copy and it failed on both legs, naming the file
  and the line.

### The command summary

The new text is 77 characters. The old text appears nowhere else in the repository, so no document
is left quoting it.

### The license test that depended on the machine's search path

The test still proves the claim in its name, and no longer needs a tool installed on the machine. It
puts a copy on a search path of its own, proves the resolver finds that copy by bare name, and then
proves the Director's own lookup returns nothing for an empty tool folder with that same search path
handed in. If the lookup ever searched by bare name, it would return the planted copy and the test
would fail. The product change is a pass-through: the one-argument form hands in the process's own
`PATH` and `PATHEXT`, which is exactly what `ExecutableResolver.Resolve` read before.
I did not run a mutation of the product for this one; the conclusion is from reading
`ExecutableResolver.cs`, where a path that contains a folder separator is never looked up on the
search path.

### The several-threads test (proves no less than before, and I measured it)

This was the one most at risk of being made steady by proving less, so I did not rely on reading it.
Out of tree, I copied `LicenseUsageHistory.cs` into a scratch project named for this review, gave the
copy a switch that turns its lock off, and ran the old test body and the new test body against it,
ten times each, on all 24 cores, pinned to two cores, and pinned to one core.

| Cores | Test body | Lock on | Lock off |
|---|---|---|---|
| 24 | new | 0 of 10 failed, 1.3 seconds each | 10 of 10 failed |
| 24 | old | 0 of 10 failed, 6.4 seconds each | 10 of 10 failed |
| 2 | new | 0 of 10 failed, 1.9 seconds each | 10 of 10 failed |
| 2 | old | 0 of 10 failed, 6.5 seconds each | 10 of 10 failed |
| 1 | new | 0 of 10 failed, 2.1 seconds each | 10 of 10 failed |
| 1 | old | 0 of 10 failed, 5.9 seconds each | 10 of 10 failed |

- With the lock removed the new test fails every time, on every core count, with "a series out of
  order or with a point twice". It still catches what it exists to catch.
- The overlap is real and not just arranged on paper. With the lock on, of the 8,000 reads in one
  run, the readers saw a newer newest point than their previous read 2,393 times on 24 cores, 412
  times on two cores and 157 times on one core, and at most one read saw an empty series.
- The assertion that was dropped, "a reader never read while the writers wrote", is the one that
  failed under load. It is replaced by a fixed 2,000 reads for each reader that the writers are held
  open for, which is stronger, and the order check is now strict, so it also catches a point held
  twice, which the old check let through.
- It is about a third of the old test's running time.

### The register and the baseline

Both tests pass on the pull request head, and the baseline test is exact in both directions (a count
that is too high fails as well as one that is too low), so a pass means the five lines are exactly
what it asked for and nothing more. See Finding 1 for why this part is now redundant.

## Tests run on the pull request head

- `GateTestsDoNotDependOnTheMachineTests`, `BackgroundWorkRegisterTests` and `LicenseCommandLineTests`
  in `CcDirector.Core.UnitTests`: 35 passed, 0 failed.
- `LicenseUsageHistoryTests` in `CcDirector.Gateway.UnitTests`: 14 passed, 0 failed, on each of five
  runs in a row.
- `tools/cc-devthrottle/tests`, whole suite, on the floor and on the latest dependency sets: 4,298
  passed and 3 skipped on each.

## Scope, and what I did not do

- I reviewed the eight files in `git diff origin/main...HEAD` and the code directly beneath them
  (`ExecutableResolver.cs`, `LicenseUsageHistory.cs`, `tool_errors.py`, the continuous integration
  job that defines the two dependency sets).
- I ran the affected test classes, not the whole local gate, and not the parked suites.
- All tests ran on the pull request head, which is four commits behind main. I did not run them on
  the rebased result, because the rebase has not been made. Main's own copy of the baseline is
  identical, so the baseline result carries over only if main itself is green there.
- Everything ran on Windows. The two license tests have a branch for other systems (the file mode on
  the planted tool) that I read but did not run.
- One thing outside this pull request that the Delivery Lead should know: release 2.19.0 was cut on
  main (pull request 3938) while the click import is still there. The installed tool is not affected,
  because the package declares Click as a dependency and so always installs it; only the latest leg
  of the continuous integration job, which installs Typer alone, fails to load.
- I changed no existing file. The scratch project and the two environments are outside the worktree.
