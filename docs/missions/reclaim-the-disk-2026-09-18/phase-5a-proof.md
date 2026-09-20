# Phase 5 part one: proof

From the Developer, 20 September 2026. Part one of `mandate-phase-5.md`: the Launcher hosts the
background scan. Code commit `c7a053d04` on `reclaim/phase5-launcher-background-scan`. Every number
below was read off a run made by me on that commit, on the machine SOREN_NORTH.

No real disk was scanned, the Launcher and the Director were never started, no process was stopped,
and nothing on the machine was removed. Every scan in every test is of a tree the test built in the
temporary folder and deleted.

## The tests, by what the mandate asked for

Engine tests are in `src/CcDirector.Reclaim.Tests`, Launcher tests in `src/CcDirector.Launcher.Tests`.

**The guard against a second scan.**

- `BackgroundScanJobTests.Run_WhileAnotherScanIsRunning_IsRefusedAndTouchesNothing` - a real scan
  is held open inside the guard (its rule waits), and a second real scan is started. The second
  returns `AnotherScanIsRunning`, writes no saved scan, leaves its own folder at never run and the
  first scan's record at running. When the first ends, the second folder scans: the guard lets go.
- `BackgroundDiskScanTests.RunDueScansAsync_WhileAnotherScanHoldsTheGuard_ScansNothing` - the same
  through the Launcher's class.

**The three states told apart.**

- never run: `Read_WhenNoScanWasEverStarted_SaysNeverRunAndSaysNothingWasLost`
- running: `Read_WhileAScanIsRunning_SaysRunningAndSinceWhen` - read from outside while a real scan
  is really inside its rule.
- failed: `Read_AfterAScanThrew_SaysFailedAndSaysWhy`,
  `Read_AfterAScanOfAFolderThatIsNotThere_SaysFailed`
- failed, the case the mandate names, a crash that would otherwise read as running:
  `Read_WhenTheRecordSaysRunningAndItsProcessIsGone_SaysFailedNotRunning`
- failed and never "never run": `Read_WhenTheRecordCannotBeRead_SaysFailedNotNeverRun`
- a failure after a success still names the saved result:
  `Read_AfterAFailureThatFollowsASuccess_StillNamesTheLastWholeResult`

**The interrupted scan.**

- `ScanIndexStoreTests.Save_CutShortHalfWayThrough_LeavesTheEarlierScanWholeAndNothingALaterReadBelieves`
  - a save is stopped after half its bytes are on the disk, by an exception thrown from inside the
  write. `Load` then returns the EARLIER scan with its earlier numbers and date, and the listing
  shows exactly one saved scan and no unreadable file.
- `ScanIndexStoreTests.Save_CutShortWithNoEarlierScan_LeavesNoSavedScanAtAll` - the same with
  nothing saved before: no scan, not half of one, and nothing in the unreadable list.
- `Run_AskedToStopDuringTheWalk_SavesNothingAndKeepsTheEarlierResult` and
  `Scan_AskedToStop_ThrowsRatherThanReturningPartOfADisk` - a walk asked to stop returns nothing.

**It never removes.** `Run_NeverRemovesAnything_TheTreeIsByteForByteWhatItWas` and
`BackgroundDiskScanTests.RunDueScansAsync_NeverRemovesAnything`.

**When a folder is due**, seven `IsDue_*` tests and
`RunDueScansAsync_ScansWhatIsDueAndThenLeavesItAlone`.

## Revert proofs

Method, the same for all five: the code was committed first (`c7a053d04`); the check was DELETED,
never switched off; the WHOLE test project was built and run with no filter; the file was restored
with `git checkout`; and the restore run at the end was a full build and run, never `--no-build`,
with `git status` empty and no diff against the commit.

| | What was deleted | Whole Reclaim project, 255 tests | Tests that went red |
|---|---|---|---|
| A | the `if (guard is null)` return in `BackgroundScanJob.Run` | 253 passed, 2 failed | the guard test. Whole Launcher project too: 207 passed, 1 failed, the Launcher guard test |
| B | the dead-holder branch in `BackgroundScanStatusStore.Read` | 254 passed, 1 failed | `Read_WhenTheRecordSaysRunningAndItsProcessIsGone_SaysFailedNotRunning` |
| C | the `WriteFailed` call where a thrown scan is recorded | 252 passed, 3 failed | `Read_AfterAScanThrew...`, `Read_AfterAScanOfAFolderThatIsNotThere...`, `Read_AfterAFailureThatFollowsASuccess...` |
| D | the temporary name and the move in `WholeFileWriter` (written in place) | 253 passed, 2 failed | both `Save_CutShort...` tests |
| E | the stop check in `DirectoryScanner.Scan` | 253 passed, 2 failed | `Scan_AskedToStop...`, `Run_AskedToStopDuringTheWalk...` |

Restore run: Reclaim 255 of 255; Launcher 208 of 208 on both targets.

One thing in run A is not the mutation. Its second red test was
`MachineRulesDeclineTests.MachineRules_DeclineTheCategoriesTheMissionOnlyReports...`. I had pointed
the storage root at a scratch folder under Temp for that run, and that phase 2 test correctly
objects that a storage root under Temp is inside the test scratch rule's folder. It was red with
the guard restored under the same setting, and green in every run made under the seat's own
environment, which is every run from B onward. I found it only because the run was not filtered.

## The local gate

`.\scripts\test-local.ps1`, the default run, on `c7a053d04`. I checked for another test run on the
machine first and waited for one to end.

**Eight of nine suites green, and the gate itself RED.**

| Suite | Result |
|---|---|
| CcDirector.Core.UnitTests | 764 of 764 |
| CcDirector.Avalonia.Tests | 554 of 554 |
| CcDirector.Engine.Tests | 63 of 63 |
| CcDirector.HostedAgent.Tests | 88 of 88 |
| CcDirector.Terminal.Avalonia.Tests | 30 of 30 |
| CcDirector.Reclaim.Tests | 255 of 255 |
| cc-director-setup.Tests | 25 of 25 |
| cc-director-setup-engine.Tests | 619 of 619 |
| CcDirector.Launcher.Tests | **206 of 208, FAILED** |

The two failures, read from the gate's own result file, are both in
`LauncherDeclaredCapabilitiesTests`: `Describing_the_launcher_asks_the_signal_and_never_raises_it`
and `An_unarmed_launcher_declares_no_restart_signal_and_that_is_a_NO_not_an_unknown`. They ask
Windows whether a Launcher restart signal is armed for the current storage root and expect no.

What I observed about them, and no more than that:

- On an untouched worktree of origin/main (`ff6933f55`), from this same session, the whole Launcher
  project gives 195 of 197 with the SAME two tests failing. My branch adds 11 tests and changes
  neither of those two.
- With the storage root pointed at an empty folder, the whole Launcher project on my branch gives
  208 of 208 on both targets.
- This session's storage root is `...\cc-director\instances\default`, the live installation's.

So they fail in this session with or without my change, and pass when the storage root is not the
live one. I have NOT looked at which process holds that signal, so I state no cause beyond that.
**The Delivery Lead should rerun the gate from outside a session of the live installation before
accepting this**; I could not make it green from where I sit and did not bend anything to try.

**The COVERAGE GAP line named all three parked suites. It is partly right.**
`CcDirector.Gateway.UnitTests` references the Launcher project, so that one is a real edge.
`CcDirector.Core.Tests` and `CcDirector.Gateway.Tests` are named only because a project file
changed and because the tool's path under `tools/` is not recognised, which makes the selector
choose everything; nothing in Core or the Gateway was touched. See the section on that suite below: I did not run it.

## What this proof does not cover

Stated plainly, because each of these is a place a defect could be and no test here would see it.

1. **A real interruption.** No process was killed. The half-written save is an exception thrown
   from inside the write, and the dead Launcher is a running record whose holder start time was
   changed to one that matches no process. Power loss, and whether the move is a single step on
   every file system a user has, are not proved.
2. **The Launcher actually running.** `RunLoopAsync`, the ten minute wait, the hourly look, the
   `--managed` gate and the two lines that start the loop in the tray mode and the headless mode
   are not exercised by any test. I was told not to start the Launcher, and did not. Deleting
   either start line would leave every test green.
3. **A real disk.** No whole-disk scan was run. The 1,406 seconds is phase 2's measurement, used as
   a design input. That the record really says running for that long, that a stop really lands
   within a moment on 3.5 million files, and what the scan costs a machine in use, are unmeasured.
4. **Two processes.** The guard is proved within one process. It rests on the operating system
   refusing a second opening of a file shared with nobody, which is per machine on Windows, but no
   test starts a second process.
5. **macOS and Linux.** Everything ran on Windows. The guard's refusal, the whole-file move and the
   process start time check are all platform behaviour, and none was run anywhere else. There are
   also no rules off Windows, so the saved recommendations there will say broken, by design.
6. **The real Windows rules inside the job.** The job tests use a rule the tests own. The ten real
   rules are proved by their own phase 2 and 4 tests, and the path from the job to them is
   `RecommendationRun`, which the command line tool's existing `recommend` tests also go through.
   No test runs the job with `WindowsRuleSet`, because those rules read the live machine.
7. **`RootsToScan`.** One test says it names folders that exist. Which drive types it leaves out is
   not proved; there was no removable or network drive to prove it on.
8. **Removal.** There is none on this branch, so "never removes" is proved by a tree being
   unchanged and by there being no removal code to call. Once phase 3 merges, removal code will be
   reachable from the Launcher's references, and nothing here stops a later change from calling it.
   That guard belongs to whoever joins the two and should be a test that fails when the Launcher
   names a removal type.
9. **The parked suites and the web and Python tests**, except as the next section says.
10. **The Director.** Nothing reads these files yet. That the saved recommendations hold what the
    phase 6 screen needs is a judgement, not a proof.

## The parked suite that references the Launcher

**I did not run it, and nothing here covers it.** `CcDirector.Gateway.UnitTests` is the one parked
suite with a real edge to this change. Each time I went to run the whole project, other sessions
had test runs going on this machine, the last two being parked gates from
`devthrottle-reclaim-gate1` and `devthrottle-wingman-error-dev`, one of them inside the Gateway
suite that holds the machine-wide lock. My instruction was not to start a run beside a gate, and a
4,259 test project started beside two of them would have disturbed theirs as well as mine. So:

- `.\scripts\test-local.ps1 -Parked` has NOT been run on this branch.
- What I changed in the Launcher is one new class, one new static method on `LauncherCore`, two
  call sites that start it, and two project references. I changed no existing Launcher type. That
  is a reason to expect the Gateway unit tests to be unaffected. It is not a run.

**The Delivery Lead should run `-Parked` on this commit before it merges**, from a worktree of its
own and outside a session of the live installation, which settles this and the two red Launcher
tests above in one run.

## The branch is two commits behind origin/main

origin/main moved to `ff6933f55` while I worked: two Gateway commits, `#3154` and `#3155`. Neither
touches the Launcher, the engine or the tool. I did not rebase, because every number above is a
run on `c7a053d04` and a rebase would have made them numbers about a tree I did not run.
