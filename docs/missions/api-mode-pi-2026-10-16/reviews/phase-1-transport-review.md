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

Developer's answer:

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

Developer's answer:

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

Developer's answer:

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

Developer's answer:

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

Developer's answer:

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

Developer's answer:

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

Developer's answer:

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
