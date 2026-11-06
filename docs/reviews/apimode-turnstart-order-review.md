# Review: the scripted Pi keeps the recording's order between standard error and standard output (pull request 4667)

Written 6 November 2026 by the reviewer seat for pull request 4667, at commit `5fb3a92af`, which is
one commit on top of `origin/main` (`ff55d00e4`). This file is the whole output of the review. Nothing
in the pull request was changed.

## Verdict

**APPROVE.**

The cause the author names is the real cause, and this review reproduced it on `origin/main`. The
fix is a test-side change only, it waits on nothing but calls the code under test makes, and it hides
nothing the other tests relied on. The same race also broke a second test on the same recording, which
the fix covers as well.

## Scope

Read, in full, from this worktree (the pull request's commit, verified against `origin/main` with
`git diff origin/main...HEAD`):

- The whole diff: `FakeApiModeProcess.cs`, `ScriptedPi.cs` and the new `FakeApiModeProcessTests.cs`,
  all under `src/CcDirector.Core.UnitTests/ApiMode/`.
- The product code the fake stands in for and the code that consumes it, all unchanged by the pull
  request: `IApiModeProcess.cs`, `SystemApiModeProcess.cs`, `ApiModeTransport.cs` (the two read loops,
  the standard error store and the end of the process), `ApiModeSessionHost.cs` (where standard error
  lines are fed to the dialect) and `PiDialect.cs` (where the standard error mark is cleared and where
  a reason is built from it).
- The recording the failing test replays, `TestData/pi-rpc/plain.transcript.jsonl`, record by record
  with its timestamps.
- The failing test, `TurnStartBound_TurnBeginsInTime_NeverRestarts`, and the rig it uses.

Ran, from this worktree, with `DOTNET_USE_POLLING_FILE_WATCHER=1` and `-p:EnableWindowsTargeting=true`:

- Every API-mode test in `CcDirector.Core.UnitTests`: 308 passed, 0 failed, including the two new
  tests.
- The named test, 40 runs in a row on the fixed tree under the machine's own load (load average about
  20 on 32 cores): 40 passed.
- A reproduction, described below, on a scratch worktree of `origin/main` and on this tree under the
  same conditions. The scratch worktree was removed afterwards.

Did not run: the full local gate (`scripts/test-local.ps1`), which is the author's to run before
merging. The pull request touches only test code in one project, and every test of that project's
API-mode folder was run here.

## Is the cause right?

Yes. Three facts, each checked in the code or the recording:

1. **The recording has the gap the author describes.** In `plain.transcript.jsonl` the standard error
   warning (`Warning: No project session found ...`) is at 534 milliseconds and the `response` and
   `agent_start` records that follow it on standard output are both at 632 milliseconds. The real Pi
   wrote them about a tenth of a second apart. `ScriptedPi.Emit` pushes all three in one synchronous
   loop, so the fake offers them at the same instant.

2. **The two streams are read by independent loops, and the mark is cleared at `agent_start`.** The
   transport starts `ReadOutputLoopAsync` and `ReadErrorLoopAsync` as two tasks. The host feeds the
   stored standard error lines to the dialect just before each standard output record
   (`FeedErrorLinesLocked` in `OnRecordLocked`). The dialect clears `_stderrSinceMark` when the prompt is
   written and again in `OnAgentStart`, and at exit it appends whatever is still in the list to the
   reason, as `. Standard error: ...`. So if the error loop has not yet stored the warning when the host
   handles `agent_start`, the warning is stored after the clear and survives to the exit, and the
   reason reads `Pi exited (exit code 0). Standard error: Warning: ...`. That is exactly the failure
   the author saw.

3. **It reproduces, and the fix stops it.** Under the machine's natural load neither tree failed in 40
   runs, so the window is small. With the thread pool starved (`DOTNET_ThreadPool_MinThreads=2`,
   `DOTNET_ThreadPool_MaxThreads=4`) and 24 busy loops running, the `ApiModeSessionHostTests` class was
   run 30 times on each tree:

   | Tree | Runs | Failed runs | Failing tests |
   |---|---|---|---|
   | `origin/main` (unfixed) | 30 | 9 | `TurnStartBound_TurnBeginsInTime_NeverRestarts` once; `SendPromptAsync_PlainPrompt_IsDeliveredAndTheTurnEndsDone` eight times |
   | pull request 4667 (fixed) | 30 | 0 | none |

   Every one of the nine failures carries the same symptom: a `ConnectionEnded` whose reason is
   `Pi exited (exit code 0). Standa...` where `Pi exited (exit code 0)` was expected. The sibling test
   replays the same `plain` recording through the same scripted Pi, so it had the same defect; the
   author did not name it, and the fix covers it because the order is kept in the scripted Pi, not in
   one test.

**Could a real Pi do this, making it a product defect?** In principle the real transport can see the
same reorder: two pipes carry no order between them, and a standard error line written shortly before
a standard output record can be stored after that record has been handled if the error loop's thread
is starved. The fake's own documentation says so honestly. But the consequence in the product is only
which window a diagnostic line is attributed to: the `Reason` and `StderrTail` of `ConnectionEnded`
would carry a warning that was written before the turn rather than during it. The turn outcome, the
restart decision, the prompt receipts and the stop on a resume warning (`FeedErrorLinesLocked` runs
before the exit is handed to the dialect, so no line is ever lost) are all unchanged. The product has
no way to know the order across two pipes, so the reason text is the best it can do with what it has.
This review finds no product defect, and no finding that the product should change.

## Does the fix depend on time or the machine?

No. The ordering is a hand-over of control, never a wait on a clock:

- `EmitError` counts the lines pushed; `EmitOutput` stamps each chunk with how many standard error
  lines had been pushed before it.
- `ReadOutputAsync` hands a chunk out only once `_errorLinesHandedOver` has reached that stamp, waiting
  on a `TaskCompletionSource` otherwise.
- `ReadErrorLineAsync` raises `_errorLinesHandedOver` to the number of lines taken **at the start of the
  next call**, that is, when the transport's error loop comes back for another line. The transport
  stores a line under `_errorGate` before it comes back, so when the output is released the line is
  already in the store that `FeedErrorLinesLocked` reads. That is the exact property the host needs, and
  it is reached by a call the code under test makes, not by a delay.
- The end of the process is safe: `Exit` completes both channels, the error loop drains every queued
  line (a completed channel still yields what is in it), hands each one over on its next call, and only
  then reads the end and stops. So every chunk's stamp is met before the error loop ends, and no output
  can be left waiting on a reader that is gone.
- The wait honours the read cancellation token, so a transport that stops its loops (disposal, or the
  drain grace running out on the fake clock) does not hang on this wait.

The two new tests are deterministic in the same way: the `Assert.False(output.IsCompleted)` checks hold
because nothing but the test's own next call can release the task, and the one `WithinHangGuard` is the
existing 30-second hang guard that never decides a pass, as the file's own rule says. The second test's
`Assert.True(output.IsCompleted)` holds because with no standard error line before the chunk every step
of the read completes synchronously.

Two small observations, neither a finding:

- A waiter cancelled by the token stays in `_handOverWaiters`. In a fake that lives for one test this is
  harmless.
- `CancelledReadFailure` is not applied to a cancelled ordering wait. Only `ScriptedPi` turns
  `KeepsOrderAcrossStreams` on, and it never sets `CancelledReadFailure`, so no test can reach that
  combination.

## Does it hide anything the other tests relied on?

No.

- `KeepsOrderAcrossStreams` is off by default. The seven test files that construct
  `FakeApiModeProcess` directly (transport, wheel, session, creation and ending tests) get the old
  behaviour unchanged; the channel's element type changed but it is private.
- The only caller that turns it on is `ScriptedPi`, which replays real recordings. For those tests the
  only change is that output pushed after a standard error line now arrives after that line is stored,
  which is the order the real Pi produced. A test could only have depended on the old behaviour by
  asserting the race, and all 308 API-mode tests pass, 30 times over under the starved pool.
- The named test's assertion is unchanged. The fix makes the expectation it already had hold
  deterministically; it does not loosen it.
- Nothing in the product changed, so no product behaviour is masked.

## Summary of the evidence this review does not cover

- The full local gate was not run here.
- The stress reproduction used a starved thread pool, which is harsher than the gate's conditions. It
  shows the race exists and that the fix removes it; it does not measure how often the gate would have
  hit it.
