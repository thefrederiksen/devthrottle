# Phase 4 review: the transport test hang (pull request #4102)

Mission: API mode for agent wrappers, proven on Pi (issue #4070). Pull request #4102, "API mode:
the transport tests wait for the drain-grace timer to be armed instead of counting turns", branch
`apimode/p4-dispose-hang`, reviewed at its head commit `b1b7ec320` (one commit on base
`8f830b589`). Reviewer seat, session 134, running a different agent family from the Developer.
Written 17 October 2026 on devlinux.

**Verdict: accept. The cause stated in the pull request is right, and I reproduced it: the product
is correct and only the tests were wrong. The new tests cannot be raced by the signal they wait on,
and they obey rule 5a. No findings. Four notes, none of which blocks the merge; the Developer
decides what, if anything, to do with them.**

## 1. Scope: what I read, what I ran, what I could not reach

**Read, in full:** the pull request body and its one commit message; the diff
(`git diff 8f830b589..b1b7ec320`, two test files, 22 insertions, 22 deletions); both test
classes `ApiModeTransportEndingTests.cs` and `ApiModeTransportTests.cs` as they stand on the
branch, including the fake-clock tests the change leaves alone; `FakeApiModeProcess.cs`;
`src/TestInfrastructure/VirtualClock.cs` and `VirtualTime.cs`; and in the product,
`src/CcDirector.Core/ApiMode/ApiModeTransport.cs`: the constructor, `Start`, `SendRequestAsync`,
`WriteLineAsync`, `ShutdownAsync`, `DisposeAsync`, `WatchForExitAsync` and `Complete`, plus a search
of the whole file for every use of the time provider. Also the gate script's header on hang
detection, the mission record's phase table, and the state of `CatalogReadExecutor.cs` on
`origin/main` for the pull request's side claim about main being red.

**Ran,** with the .NET 10.0.301 software development kit on devlinux, in a worktree cut from the
branch at `../devthrottle-review-4102`, never the shared checkout, and never two runs at once:

1. The two classes as the branch stands, once:
   `dotnet test src/CcDirector.Core.UnitTests --filter "FullyQualifiedName~ApiModeTransportEndingTests|FullyQualifiedName~ApiModeTransportTests"`
   with hang detection on (`--blame-hang --blame-hang-timeout 2m`). Result: 38 passed, 0 failed,
   0 skipped, 199 milliseconds. No sequence file, so nothing hung.
2. A reproduction of the claimed cause against the OLD test code. I made one temporary edit to the
   fake process in my worktree: `WaitForExitAsync` reports the exit three seconds after it
   happens, which is exactly what a starved pool thread looks like to the transport (the exit is
   real at once, the watcher sees it late). I checked out the two test files from the base commit
   `8f830b589` and ran only the three tests the pull request names, with a 40-second hang timeout.
   Result: the run was aborted by the hang detector. The sequence file names two tests unfinished:
   `DisposeAsync_AfterTheDrainGraceReadsFailOnTheirClosedPipe_NoFaultAndNoKill` and
   `Ended_ProcessExitsButSomethingHoldsItsOutputOpen_EndsAfterTheDrainGrace`. The third,
   `SendRequestAsync_ProcessExitedButTheEndIsNotReportedYet_FailsWithConnectionEndedAndTheExitCode`,
   finished. That is the same pair the pull request says hangs ("both the hung test and its
   sibling").
3. The same late exit against the NEW test code (the branch's test files restored, the temporary
   fake edit still in place), the same three tests. Result: 3 passed, 0 failed, 6 seconds (each
   waits the three real seconds for the exit to be seen, then moves the fake clock in one step).
4. The temporary edit was then reverted; `git status` in the worktree was empty before the review
   branch was cut. Nothing of the reproduction is committed.
5. A scan of the commit message and pull request body for assistant attribution: none.

**Could not reach:** the hang dump itself, which is on SORENLAPTOP and not in the repository, so
the pull request's reading of it (16 threads, none in test or transport code) is taken from the
pull request, not checked; Windows and macOS; the pull request's 200-run loop, which I did not
repeat (this machine's rule is a few runs of the two classes, never the suite); and the rest of
the suite, which I did not run.

## 2. Question 1: is the cause right, and is the product correct

**Yes, and yes.** The transport touches the clock in exactly two places, both `Task.Delay` on the
injected time provider: the shutdown grace in `ShutdownAsync` (line 254) and the drain grace in
`WatchForExitAsync` (line 432). `WatchForExitAsync` is started with `Task.Run` (line 131), awaits
the process's exit with `ConfigureAwait(false)` (line 427), and only then creates the drain-grace
delay. The fake's exit completion source is built with `RunContinuationsAsynchronously`
(`FakeApiModeProcess.cs` line 15), so the watcher's continuation, and with it the arming of the
timer, runs on a pool thread after the test's `process.Exit(...)` returns. Nothing tells the test
when that happens.

The old loop bounded its wait by a count of turns, not by any signal from the code: 100,000 fake
seconds, one per turn, then it fell through to an `await` on a task that only the timer could
complete. Once the pool thread was later than the loop, the timer was armed at fake time plus
100,000 seconds and five more, nobody moved the clock again, and the test waited for ever. Run 2
above is that mechanism made deterministic: a three-second delay hangs the two tests every time.
The pull request's reading of the dump, a test parked on an await with no thread in product code,
is the shape such a wait has; it is consistent with what I reproduced, though I did not read the
dump.

The product is correct on this path. With the real clock the drain-grace delay fires five seconds
after it is armed regardless of when that is, `Complete` runs, the end is reported once, waiting
requests are failed with the end record, and `DisposeAsync` finds the end already reported and does
not kill. No product line is changed, and none needs to be. The `SendRequestAsync` test also holds
up under the late exit: the pending request is registered under the gate before the first await
(lines 150 to 160), so it is in the table whenever the end comes, and whichever branch throws, the
exception is `ApiModeConnectionEndedException` carrying the end record and its exit code.

## 3. Question 2: can the new tests still hang, or pass without proving anything

**Not by a race, and not as the code stands.** The points I checked:

- **The signal is the right one.** `VirtualClock.NextWaitStarted` completes on the next
  `CreateTimer` with a finite due time on that clock (`Arm`, lines 179 to 197). The only timer the
  transport can arm in any of the three tests is the drain grace: `ShutdownAsync` is not called in
  any of them, the `Started` helper and `SendRequestAsync` never touch the clock, and the fake's
  `WaitForExitAsync` is given `CancellationToken.None`, which creates no timer. So the first and only
  wait started after the exit is the one the test wants.
- **There is no subscribe-versus-arm race.** Each test reads `clock.NextWaitStarted` before
  `process.Exit(...)`, and the transport cannot arm the drain grace until it has seen the exit. The
  property returns the completion source that is current at the moment of the read, and `Arm`
  swaps and completes it under the state lock, so a read strictly before the arm always gets the
  source that arm completes. A timer armed before the subscription cannot exist, because nothing
  arms one before the exit.
- **The single step fires the timer.** The clock starts at the Unix epoch and is never moved
  before the arm, so the timer is due at epoch plus five seconds; `Advance(DrainGrace)` runs every
  timer due at or before epoch plus five seconds (`RunNext`, line 111: a timer is skipped only
  when it is due strictly after the target). It fires.
- **Nothing in the chain after the advance waits on the clock or the test thread.** The timer
  callback completes the delay; `WatchForExitAsync` continues, cancels the drained wait, cancels
  the reads and calls `Complete`; `Complete` completes `_ended` and the event channel. The three
  tests then await `Ended`, the send, or the channel, all of which are completed by that chain.
  The assertions are substantive: the Ended test checks the end record is last and carries the
  exit code; the Dispose test checks no fault and no kill after the end was reported by grace; the
  send test checks the exception type and the exit code.
- **The signal is a plain wait on the code's own progress, not on time.** `await graceArmed`
  completes when the transport reaches the clock, however slow the machine. That is why run 3
  passes with a three-second delay the old code could not survive.

## 4. Question 3: rule 5a

**Obeyed.** No sleep, no real clock, no real process, no port. The fake process is scripted; the
clock is `VirtualClock`; the one real wait is for a pool-thread continuation of the fake's own
completion source, which is the code doing its work, not the machine deciding a pass. The
turn-counting loops the change deletes were the machine-dependent part. The four other fake-clock
tests in the two classes (the shutdown-grace tests at `ApiModeTransportEndingTests.cs` lines 147
to 190 and `ApiModeTransportTests.cs` lines 451 to 485) arm their grace synchronously before
`ShutdownAsync` first yields and advance after, so they are not of the hanging shape and rightly
stay as they are.

## 5. Notes, none blocking

These are observations the Developer may act on or decline. None proves a harm in the code as it
stands, which is why they are not findings.

1. **The helper is right only while the drain grace is the first timer the transport arms after
   the exit.** `NextWaitStarted` means "any wait", not "the drain grace". If the transport ever
   arms another timer on the same clock between the exit and the grace, say a read timeout, the
   helper will take that wait, advance five seconds, and the test will hang again rather than
   fail. The helper's comment could say this in one sentence so the next person adding a timer
   knows to look here.
2. **A product regression that never arms the grace would show as a hang, not a named failure.**
   The default gate has no hang naming (the gate script's header: "The default run does not need
   it - its ceiling is two minutes and the kill is already here"); only `-Parked` and `-Gateway`
   runs produce a sequence file. `VirtualTime.RunAsync` guards the same situation with a
   60-second real-time watchdog that "turns a hang into a named failure and never decides a pass"
   (`VirtualTime.cs` line 28). `await graceArmed.WaitAsync(VirtualTime.RealLimit)` would give these
   three tests the same courtesy. The old tests hung the same way, so this is not a regression;
   it is an opportunity.
3. **The helper rebuilds the default grace with `new ApiModeTransportOptions().DrainGrace`.** All
   three transports are constructed without options, so the value matches today. A test that one
   day passes its own options would silently advance by the wrong amount. Handing the helper the
   options, or the transport, would remove the duplicate.
4. **The pull request's side claim is already out of date.** It says main is red on
   `CatchLogLineScanTests` because of two log lines from #4081. That was fixed on main by #4092
   (commit `6c60d3866`), which landed after this branch's base `8f830b589`. A rebase onto
   `origin/main`, or the Delivery Lead's own gate run on main after the merge, will not see that
   failure. Nothing in this pull request touches that file; `origin/main` has not touched either
   test file since the branch's base, so the rebase is trivial.

## 6. What the next seat need not re-check

- Only two test files change. No product code, no shared test infrastructure.
- `VirtualClock` is compiled into the test project as a linked source file
  (`CcDirector.Core.UnitTests.csproj` lines 80 and 81), which is why the `internal`
  `NextWaitStarted` is reachable without an internals-visible attribute.
- `VirtualTime.RunAsync` could not have been used here instead: it fails a run whenever a wait is
  started off the moving thread, and this transport arms its timer from a `Task.Run`. Taking the
  signal by hand is the right tool for a product that legitimately leaves the thread.
