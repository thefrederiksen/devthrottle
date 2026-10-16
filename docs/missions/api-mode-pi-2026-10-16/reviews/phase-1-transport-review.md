# Phase 1 review: the agent-neutral transport

Mission: API mode for agent wrappers, proven on Pi (issue #4070). Pull request #4075, "API mode
phase 1: the agent-neutral transport", reviewed at its head commit `a0b3eb0c4`. Reviewer seat,
opened by the Tech Lead (session 107), running a different agent family from the Developer.
Written 16 October 2026 on devlinux.

**Verdict: the transport is sound in its main paths and well tested, and it is not ready to be
built on until findings 1 and 2 are answered. Seven findings. The first three are defects in the
contract the brief states (a missing executable must fail with a stated reason; a waiting request
must fail with a stated reason when the process exits; a polite close must be followed by a timed
kill), each proven by a test I ran and then deleted. Findings 4 and 5 are Windows facts read from
the .NET 10 runtime source, not observed on a Windows machine. Findings 6 and 7 are smaller. The
Developer decides what happens to each.**

## 1. Scope: what I read, what I ran, what I could not reach

**Read, in full:** every file in the pull request diff (`git diff origin/main...HEAD`, 13 files,
2,226 insertions, no deletions): `ApiModeTransport.cs`, `SystemApiModeProcess.cs` (with the
executable resolver and the environment builder it holds), `NewlineJsonFramer.cs`,
`IApiModeProcess.cs`, `ApiModeExceptions.cs`, `ApiModeProcessStartInfo.cs`,
`ApiModeTransportEvents.cs`, the five test files including `FakeApiModeProcess.cs`, and the new
row in `docs/BackgroundWork.md`. The contract it serves: `event-model.md` section 1 (framing) and
sections 3.4 and 3.5 (abort, exit). The existing `InheritedSessionEnvironment.cs` the process
builds its environment on. Issue #4070 for the phase table and the layering. The phase 0 review,
for the shape of this file.

**Ran:**

- `dotnet test src/CcDirector.Core.UnitTests -p:EnableWindowsTargeting=true --filter
  "FullyQualifiedName~ApiMode"` with the .NET 10.0.301 software development kit on devlinux:
  73 passed, 0 failed, 0 skipped, 222 milliseconds. The pull request's count of 73 is confirmed by
  a run, not read.
- Three throwaway tests of my own, written into the test project, run, and deleted before anything
  was committed (the tree was clean afterwards; `git status` empty). Each was written to assert
  what the brief says must happen, and each failed, which is the proof behind findings 1, 2 and 3
  below. The exact shape of each is given under its finding.
- A check of every test against the test standard of 11 October: whether any test waits on a real
  process, the real clock, a sleep or a port. Result in section 2.
- A sweep of the pull request's commit message for assistant attribution: none.
- The .NET 10 runtime source on GitHub (`release/10.0` branch, `Process.Windows.cs`, and `main`
  for `SafeProcessHandle.Windows.cs` and `Process.Unix.cs`), for the two Windows facts in
  findings 4 and 5. Those are read, not observed.

**Could not reach:** Windows and macOS. Every Windows claim below says so and names the source
line it rests on. A real Pi run: none was needed, since the fixtures from phase 0 already replay
the real wire logs through the fake, and the transport is not yet wired to anything a person could
run.

## 2. What is right, so the next seat does not re-check it

- **The waiter is registered before the line is written** (`ApiModeTransport.cs` lines 136 to
  155), so a response can never arrive before anyone is waiting for it. Confirmed by the fake,
  which answers from inside its own write.
- **Wire order holds.** One reader loop calls `Deliver`, which takes `_gate`, removes the waiting
  request, numbers the record and writes it to the channel in one critical section; the waiter is
  completed only after that, outside the lock. So a dialect that awaits a response and then reads
  the pump finds the response already in the channel, in its wire position. The abort test (events
  first, the abort's own response last, then the end) proves the case section 3.4 describes.
- **The end is the last event, delivered once.** `Complete` runs only after the exit was seen
  and both read loops ended (or the drain grace elapsed), sets `_endedRecord` under `_gate`, and
  `Deliver` drops and logs any record after it. No record can follow the end.
- **Cancellation of a request is clean:** the registration removes the waiting request under the
  gate and cancels the waiter; a late response then goes to the pump with `AnsweredRequest`
  false. Tested.
- **The framer does what section 1 demands.** Split on line feed only, from bytes; one carriage
  return before the line feed removed and only one; a lone carriage return and U+2028 and U+2029
  inside a string stay in one record; a multi-byte character cut across reads decodes once the
  line is whole; a 3-megabyte line in 64-kilobyte reads is one record; the limit is 64 megabytes.
  All tested, including the real Pi fixtures replayed in reads of 1, 7 and 4,096 bytes.
- **Writes are serialised** under one semaphore and the test with a fake that writes each line
  in two halves with a yield between proves fifty concurrent writers never interleave.
- **Standard error is read continuously** on its own loop and never touches `_gate`, so a full
  standard error pipe cannot stall the process or the output loop. The marker and
  `ErrorLinesSince` arithmetic is right in the three cases tested (marker after the start-up
  warning, marker at the second line, more lines than held).
- **The environment removal is right:** the base is the Director's environment with the
  variables no session may inherit already stripped, then the caller's names and prefixes are
  removed, then the caller's settings are applied, with case-insensitive names on Windows. A set
  variable with a removed prefix still reaches the process. Tested.
- **The executable resolver** finds `pi.cmd` beside the extensionless script on Windows, refuses
  a path that does not exist without searching, and names the executable in every failure.
- **No test depends on the machine.** I checked each: no test spawns a process (the two
  `SystemApiModeProcess` tests fail before `Process.Start` is reached, on a working folder or an
  executable path that cannot exist); the two grace tests use `FakeTimeProvider`; the other
  tests end their fake streams on exit so the default five-second drain grace is never waited;
  there is no sleep and no port. One observation, not a finding: the drain-grace test advances the
  fake clock inside a loop of up to 100,000 `Task.Yield` turns until the watcher has armed its
  timer, so its determinism rests on thread-pool scheduling rather than on a signal from the
  transport. It passed every run I made; it is recorded here only so a future failure is read
  correctly.
- **Nothing is wired into Session or any driver**, and terminal mode is untouched: the diff adds
  files and one documentation row, and deletes nothing.
- **Logging, naming and plain English** meet the rules: every public method logs entry and
  failure, every test is `MethodName_Scenario_ExpectedResult`, and the only short forms are
  Pi's own words (`rpc` in its arguments and fixture names) and the mission's own name.

## 3. Findings

### Finding 1: a transport whose Start failed cannot be disposed, and on the real process DisposeAsync replaces the start failure with a different error

**Where:** `src/CcDirector.Core/ApiMode/ApiModeTransport.cs` line 103 (`_started` is set to 1
before `_process.Start` is attempted), lines 255 to 259 (`DisposeAsync` kills and then awaits
`_ended` whenever `_started` is 1).

**What happens:** when `_process.Start` throws, `_started` stays 1 but no read loop and no exit
watcher were started, so `_ended` can never complete. `DisposeAsync` then calls `_process.Kill()`.
On `SystemApiModeProcess` that throws `InvalidOperationException("The process has not been
started.")` because `_process` is still null. On any process whose Kill does not throw, `await
_ended.Task` waits forever.

**Proof, run and deleted:** with the real `SystemApiModeProcess`, `await using var transport =
new ApiModeTransport(new SystemApiModeProcess()); transport.Start(...)` with a working folder
that does not exist. The exception that reaches the caller is `InvalidOperationException`, not
`ApiModeStartException`: the `await using` disposal throws while the start exception is
propagating, and in C# the disposal's exception replaces it. With the fake process and
`StartFailure` set, `DisposeAsync` had not completed after two seconds.

**Harm:** the brief's "a missing executable fails with a stated reason naming it" does not reach
a caller written in the ordinary shape (`await using`), which is exactly how phase 2 will write
it; the reason that names `pi` and the search path is lost and the user sees "the process has not
been started". And a hung `DisposeAsync` hangs whatever tears the session down. The existing test
`Start_ExecutableMissing_ThrowsNamingTheExecutable` does not dispose the transport, which is why
the suite does not see this.

Developer's answer: **Accepted, fixed.** The transport now has four states: not started, starting, running and
start failed. A failed `_process.Start` sets "start failed" before it rethrows. `DisposeAsync` kills and waits only
for a transport that is running, so after a failed start it just releases the process. `SendRequestAsync`,
`WriteRecordAsync` and `ShutdownAsync` refuse with "the API-mode transport's start failed". A second `Start` says
the same, so a caller never retries on a broken transport.
Tests: `DisposeAsync_AfterAFailedStart_CompletesAndTheCallerSeesTheStartFailure` (the fake, in the `await using`
shape phase 2 will use), `DisposeAsync_AfterTheRealProcessFailedToStart_TheCallerSeesTheStartFailure` (the real
`SystemApiModeProcess`, with a working folder that cannot exist, so nothing is spawned) and
`SendRequestAsync_AfterAFailedStart_ThrowsSayingTheStartFailed`. Against `a0b3eb0c4` the first and third hung
(stopped after 60 seconds) and the second failed with `InvalidOperationException`, which is exactly what you
reported.

### Finding 2: between the process exiting and the end being reported, a request fails with a pipe error instead of the stated connection-ended reason

**Where:** `src/CcDirector.Core/ApiMode/ApiModeTransport.cs` lines 136 to 163
(`SendRequestAsync`) and 175 to 183 (`WriteRecordAsync`).

**What happens:** both methods check `_endedRecord` under the gate and then write. `_endedRecord`
is set only by `Complete`, which runs after the exit watcher has seen the exit and the read loops
have drained, or the drain grace (five seconds by default) has elapsed. In that window the process
is gone, the request is added to `_pending`, and the write to its closed standard input throws an
`IOException` ("Broken pipe" on Linux, "The pipe is being closed" on Windows), which the catch at
line 157 rethrows as it is. No `ApiModeConnectionEndedException` is thrown, and no exit code or
standard error line travels with the failure. No test covers a failed write in either method; the
fake does throw `IOException` once exited (`FakeApiModeProcess.cs` line 62), so the path is
reachable in the suite but unexercised.

**Proof, run and deleted:** fake with `ExitEndsStreams = false` (a grandchild holding the pipes,
the case the drain grace exists for), `FakeTimeProvider`, `process.Exit(0)`, wait 50 milliseconds
so the watcher sees the exit, assert `Ended` is not complete, then `SendRequestAsync`. The
exception is `System.IO.IOException`, not `ApiModeConnectionEndedException`.

**Harm:** section 3.5 of the contract says the turn fails with "Pi exited during the turn (exit
code 137)" plus the standard error lines written since the turn started. The dialect will catch
`ApiModeConnectionEndedException` to build that reason. In this window it gets a pipe error with
no exit code, no error lines and the wrong type, so the user is shown a broken pipe rather than
why Pi went away. The window is not theoretical: it is the whole drain, and with a grandchild
holding the pipes it is five seconds.

Developer's answer: **Accepted, fixed.** Both write paths now go through one helper. If the process has already
exited, the line is not written at all. If the write fails while the process has exited (checked in an exception
filter, so a pipe error on a live process is still rethrown as it is), the pipe error is kept as the inner
exception. In both cases the helper waits for the end record, which comes within the drain grace, and throws
`ApiModeConnectionEndedException` carrying the exit code, the standard error lines and any fault. A write that
succeeded is never reported as unsent, even if the process exits straight after it: a response may already be on
its way.
Tests: `SendRequestAsync_ProcessExitedButTheEndIsNotReportedYet_FailsWithConnectionEndedAndTheExitCode`
(your scenario: a grandchild holds the pipes, `FakeTimeProvider`, and it asserts nothing was written),
`SendRequestAsync_TheWriteRacesTheExit_FailsWithConnectionEndedCarryingThePipeError` and
`WriteRecordAsync_TheWriteRacesTheExit_FailsWithConnectionEnded`. Against `a0b3eb0c4` the first hung and the
other two failed (they got `IOException`).

### Finding 3: the timed kill in ShutdownAsync is only armed after the write lock is taken, so a write stuck on a full standard input pipe prevents the kill from ever starting

**Where:** `src/CcDirector.Core/ApiMode/ApiModeTransport.cs` lines 224 to 235: `await
_writeLock.WaitAsync(cancellationToken)` at line 224, and the grace `Task.Delay` is created at
line 235, after the lock is held and released.

**What happens:** `WriteLineAsync` holds `_writeLock` for the whole write to the pipe. If the
agent has stopped reading its standard input (a hung agent) and a write is pending that filled
the pipe (64 kilobytes on Linux; a first turn carries a system prompt of that order), that write
never returns and the lock is never released. `ShutdownAsync` then waits at line 224
indefinitely; the grace timer does not exist yet, so "if it has not ended within grace, kill it"
never happens. Only the caller's own cancellation token gets it out, and that token does not kill
the process either.

**Harm:** the brief says "polite close of standard input then a timed kill". For the one case a
timed kill exists for, an agent that no longer responds, the kill is not timed: it is never
armed. `DisposeAsync` kills without the lock and so still works, but `ShutdownAsync` is the
method phase 2 will call for a close, and a hung Pi then hangs the close. Reasoned from the code;
not run, because the fake's write never blocks.

Developer's answer: **Accepted, fixed.** `ShutdownAsync` now arms the grace timer first and then waits for
whichever comes first: the write lock, the end, or the grace running out. Input is closed only if the lock comes
free in time. Otherwise the wait for the lock is dropped (a continuation releases the lock if it is granted later)
and the process is killed when the grace runs out. The kill also ends the stuck write, which now fails with
`ApiModeConnectionEndedException` (finding 2).
Test: `ShutdownAsync_AWriteIsStuckOnAFullPipe_KillsWhenTheGraceRunsOut`. A new `BlockWrites` fake holds the write
lock forever. The test advances the fake clock by the grace, then asserts the kill, that input was never closed,
and that the stuck write fails with "killed by the transport". Against `a0b3eb0c4` it hung.

### Finding 4: on Windows a killed process reports exit code -1, which the end record documents as "only when the exit code could not be read"

**Where:** `src/CcDirector.Core/ApiMode/ApiModeTransportEvents.cs` lines 156 to 160 (the
`ExitCode` summary), `src/CcDirector.Core/ApiMode/IApiModeProcess.cs` line 35 (the
`WaitForExitAsync` summary names Linux and macOS only), and the fixture replay test
`ApiModeTransportPiFixtureTests.cs` lines 244 to 245 and 286, which assert 137 for a kill and
"exit code 137" in the failure text.

**What I read:** in the .NET runtime, `SafeProcessHandle.Windows.cs` ends a process with
`Interop.Kernel32.TerminateProcess(this, -1)`, so `Process.ExitCode` after `Kill` on Windows is
-1. `Process.Kill(entireProcessTree: true)` goes through the same call. Read from the source on
GitHub, not observed on a Windows machine.

**Harm:** every `ShutdownAsync` that reaches the kill, and every `DisposeAsync` of a running
process, ends with `ExitCode == -1` and `TransportFault == null` on Windows, a combination the
record's own documentation says cannot occur. A phase 2 dialect written to the documentation,
or to the 137 the fixtures assert, will either report a transport failure that did not happen or
show "-1" where the design says 137. The transport works on Windows, Linux and macOS was a stated
requirement, so the record's contract must say what Windows reports, and the dialect must not
treat -1 as "could not be read".

Developer's answer: **Accepted, fixed.** I could not observe this on Windows from devlinux, but the runtime line
you cite is the kind of evidence the Tech Lead asked for, and the fix is right on every platform. Changes:
- `ApiModeProcessEnded.ExitCode` is now `int?`. It is null only when the exit code could not be read, and then
  `TransportFault` says why. The record documents -1 on Windows as an ordinary exit code, never "unknown".
- A new `KilledByTransport` flag is set by every kill the transport makes (shutdown grace, disposal, a line over
  the limit), so nobody has to read the number to know.
- `Describe()` says "exit code -1, killed by the transport".
- The `IApiModeProcess.WaitForExitAsync` summary names both platforms.
- The fixture tests keep asserting 137 because the fake reports 137, as Linux does; the fake now has a
  `KillExitCode` setting.

Note for phase 1's dialect: build the "Pi exited during the turn" reason from `Describe()`, or from `ExitCode` plus
`KilledByTransport`, and never treat -1 as "could not be read".
Tests: `Ended_KilledOnWindows_MinusOneIsAnOrdinaryExitCodeWithNoFault` and the control
`Ended_ProcessExitsOnItsOwn_IsNotKilledByTheTransport`. Against `a0b3eb0c4` the first failed (the old `Describe()`
has no "killed by the transport"); the control passes on both, as a control should. Still unobserved on a real
Windows machine: the Windows gate on SORENLAPTOP is the first run there.

### Finding 5: on Windows the read cancellation after the drain grace does not interrupt the reads, so every session whose grandchild holds the pipes leaves two thread-pool threads blocked until that grandchild exits

**Where:** `src/CcDirector.Core/ApiMode/ApiModeTransport.cs` line 361 (`_readCancellation.Cancel()`
after the drain grace), lines 294 and 323 (the reads that carry the token);
`src/CcDirector.Core/ApiMode/SystemApiModeProcess.cs` lines 78 to 80 (the streams come from
`Process.StandardOutput.BaseStream` and `Process.StandardError`).

**What I read:** on the `release/10.0` branch of the .NET runtime, `Process.Windows.cs` builds the
redirected streams as `new FileStream(parentOutputPipeHandle, FileAccess.Read, 4096, false)`, with
the comment "Using synchronous Anonymous pipes for process input/output redirection". A
`ReadAsync` on a synchronous `FileStream` is a blocking read queued to the thread pool; the
cancellation token is checked only before the read starts. On Unix the same file builds an
`AnonymousPipeClientStream`, whose asynchronous read is a socket receive that cancellation does
interrupt, so on Linux and macOS the cancel works and this finding does not apply.

**Harm:** on Windows, the case the drain grace was written for (the agent exits, something it
started holds the pipes) ends the transport as designed but leaves the output loop and the error
loop each blocked on a thread-pool thread until the grandchild closes the pipe, which may be
never for a daemon. One such session costs two threads for its lifetime; a Director that runs
many sessions pays it per session. The transport reports the end, so nothing hangs; the cost is a
leak, and the design comment at line 308 ("the exit watcher stopped the read") is not true there.
Read from the runtime source, not observed.

Developer's answer: **Accepted, fixed, with one gap stated.** On Windows only, `SystemApiModeProcess` now wraps
standard output and standard error in a new `DedicatedThreadReadStream`:
- The blocking pipe read runs on a thread of its own and hands chunks over through a bounded channel.
- The transport's read waits on that channel, which cancellation always ends. So the loops stop promptly after
  the drain grace and on disposal, and no pool thread is ever held.
- When the stream is disposed (`SystemApiModeProcess.Dispose`), it calls `CancelSynchronousIo` on the reader
  thread, so the blocked `ReadFile` returns and the thread ends.
- Linux and macOS keep the runtime's own streams, whose reads cancel already. A dedicated thread there could not be
  interrupted and would be a new leak.

**The gap, stated in the class's own documentation:** the reader thread checks for disposal, then calls Read. A
read that starts in the instant between that check and the cancel is not interrupted. That one thread then stays
blocked until the pipe closes, but it is a dedicated thread, not a pool thread.

**What is proven and what is not:** the stream's behaviour is unit tested on Linux with a stream whose Read blocks
the way a synchronous pipe does (`DedicatedThreadReadStreamTests`):
- bytes are passed through in order, and the end of the stream returns 0
- a cancelled read ends at once while the blocking read is still waiting
- a failed read surfaces its own exception
- a buffer smaller than a chunk gets the rest on the next read

The `CancelSynchronousIo` call is Windows-only and has not been run. The Windows gate on SORENLAPTOP compiles it,
but it is first exercised by a real Windows session whose grandchild holds the pipes.

### Finding 6: after the drain grace the watcher reports the end without waiting for the loops it cancelled, and DisposeAsync then disposes what those loops still use; the output loop's catch handler then calls Kill on a disposed process, which throws out of the catch

**Where:** `src/CcDirector.Core/ApiMode/ApiModeTransport.cs` lines 358 to 371 (cancel, then
`Complete` at once), lines 262 to 263 (`DisposeAsync` disposes the process and
`_readCancellation`), lines 311 to 316 (the general catch calls `_process.Kill()`), and
`SystemApiModeProcess.cs` line 123 (`process.HasExited` on a disposed `Process` throws
`InvalidOperationException`).

**What happens:** `Complete` sets `_ended`, `DisposeAsync` wakes and disposes the process and the
cancellation source. The output loop, which was not awaited, may at that moment be past its catch
for cancellation and about to evaluate `_readCancellation.Token` for its next read
(`ObjectDisposedException`), or, on Windows, still inside the blocking read that later fails when
the handle is closed (finding 5). Either way the general catch at line 311 runs, sets
`_outputFault` on a record that has already been published, and calls `_process.Kill()`, whose
`HasExited` on the disposed `Process` throws from inside the catch. The `Task.Run` task faults;
nothing observes it and nothing logs it (the log line at line 314 is written before the throw,
and says "killing process", which does not happen).

**Harm:** a hidden exception in a background task, in exactly the abnormal path where the log is
the only evidence of what went wrong, and a log line that states a kill that did not happen.
Narrow on Linux (a window between two statements), ordinary on Windows (every drain-grace end).
Reasoned from the code; not run.

Developer's answer: **Accepted, fixed.** Three changes:
- `DisposeAsync` now cancels the reads and waits for both read loops to finish before it disposes the process or
  the cancellation source. Neither loop can throw (each catches at its own entry), and with finding 5 both always
  stop on cancellation, on Windows too.
- In both loops, any exception raised after a requested stop (`catch (Exception) when
  (_readCancellation.IsCancellationRequested)`) is recorded as a requested stop. It is not a fault and does not
  trigger a kill, so an `ObjectDisposedException` or a closed-handle `IOException` from an abandoned read no longer
  writes a fault onto a record that has already been published.
- A real output failure kills only if the end has not been reported, and its log line no longer claims a kill
  that did not happen.

Test: `DisposeAsync_AfterTheDrainGraceReadsFailOnTheirClosedPipe_NoFaultAndNoKill`. A new
`CancelledReadFailure` fake setting makes a stopped read throw `ObjectDisposedException`, as a closed Windows handle
does. The test asserts no transport fault and no kill. Against `a0b3eb0c4` it failed (the old general catch called
Kill).

### Finding 7: a failure of the standard error read loop is logged and swallowed, so the end record and ErrorLinesSince are silently empty exactly when a failure reason is being built

**Where:** `src/CcDirector.Core/ApiMode/ApiModeTransport.cs` lines 340 to 343.

**What happens:** the general catch in `ReadErrorLoopAsync` writes a log line and returns. Unlike
the output loop, it does not set `_outputFault` (or any fault), so `ApiModeProcessEnded.TransportFault`
stays null and `ErrorLines` holds whatever was read before the failure.

**Harm:** the owner's rule is no swallowed exception that returns a default. Here the default is
"no standard error lines", returned to the one caller that asks for them, the failure reason of
section 3.5, with nothing on the record to say lines were lost. The user reads "Pi exited during
the turn (exit code 1)" and nothing else, and nobody is told the diagnostics were dropped. Small,
and the fix is one assignment, but it is the one place in the transport where a failure hides.

Developer's answer: **Accepted, fixed.** A failure of the standard error loop now records
"reading standard error failed, so its lines may be incomplete: <reason>". `Complete` joins it with any
output-read fault and any failure to read the exit code into `ApiModeProcessEnded.TransportFault`. So a reason
built from the lines carries, on the same record, the fact that lines were lost.
Test: `Ended_ReadingStandardErrorFails_TheFaultSaysTheLinesMayBeIncomplete` (a new `ErrorReadFailure` fake
setting). Against `a0b3eb0c4` it failed (no fault on the record).

## 4. Observations that are not findings

- `IApiModeProcess.HasExited` is implemented by both processes and used by neither the transport
  nor a test. It is not wrong, only unused.
- A request whose id is a JSON boolean passes `ReadOrAssignId` (it is a `JsonValue`, so the
  "must be a string or a number" check at line 476 is never reached) but `Deliver` maps a boolean
  id to null, so such a request would wait until exit or cancellation. No caller does this.
- Standard error is read with `StreamReader.ReadLineAsync`, which splits on a lone carriage
  return. The contract says standard error is diagnostics, never protocol, so this is fine; it is
  noted so nobody later reaches for it for a protocol stream.
- On Linux the kill exit code of 137 that the fixtures assert comes from the native shim
  (128 plus the signal); I did not re-prove it, since phase 0 stated it and the transport only
  passes `Process.ExitCode` through.

## Developer's answers to the observations in section 4

- **`HasExited` unused:** it is used now. The write path asks it whether a failed write means the process is gone
  (finding 2).
- **A boolean id would wait forever:** fixed. `ReadOrAssignId` now accepts only a string or a number and refuses
  anything else with `ArgumentException` before writing (`SendRequestAsync_BooleanId_ThrowsBecauseNoResponseCouldEverMatchIt`;
  against `a0b3eb0c4` it hung).
- **Standard error is read with `ReadLineAsync`:** agreed, and left as it is. Standard error is diagnostics, never
  protocol.
- **137 on Linux:** confirmed by a run, not only stated. A one-off program (not committed) started a real child
  through `SystemApiModeProcess` on devlinux and had the transport kill it after the shutdown grace. It reported 137.

## Developer's verification after the answers

- The API-mode tests are now 89 (73 + 16 new: 12 in `ApiModeTransportEndingTests`, 4 in
  `DedicatedThreadReadStreamTests`). All passed in eight consecutive runs on devlinux.
- Each new test for a finding was also run against the reviewed head `a0b3eb0c4`, as stated in each answer above.
- Gate counts are in the pull request and in the hand-back to the Tech Lead.
