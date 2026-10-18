# Phase 4 review: take the wheel and hand back, the Director side (pull request #4094)

Mission: API mode for agent wrappers, proven on Pi (issue #4070), phase 4, the Director's half of the
wheel. Reviewed on 17 October 2026 against commit `b4fd8d012`, the head of pull request #4094 (branch
`apimode/p4-wheel`), in a worktree cut from that commit at `../devthrottle-review-4094`, diffed against
its base on main, `e58f498f3` (21 files, 1,589 lines added, 55 removed). Reviewer seat, session
`3fb754d9`, running a different agent family from the Developer. Reading only; nothing here was built,
fixed or written as a test by the reviewer.

**This is the second round on this pull request.** A first round was written on 16 October against
commit `2ad9a2858` and committed to the branch `apimode/p4-wheel-review` on origin, based on the pull
request head rather than on main, and no pull request was opened for it, so it never reached the
mission's record. The pull request's own code is byte for byte the same in `b4fd8d012` as in
`2ad9a2858` (`git diff 2ad9a2858 b4fd8d012` over `src/CcDirector.Core`, `src/CcDirector.ControlApi` and the
three test projects is empty; the only difference is the rebase onto main). None of the first round's
five findings has an answer in the code. I re-checked each of them against the code myself, below, and
add two findings of my own. This file is on `apimode/p4-wheel-review-round-2`, cut from origin/main,
because the branch name the brief gave is taken by the first round and rewriting that branch is not a
reviewer's to do.

## Verdict

**Not ready to merge as written.** The seam is the one the mission describes, the common paths hold,
and the fifty-five Core tests, the Gateway tests and the Avalonia tests the pull request names pass
here. But one path breaks the mission's one hard rule, exactly one process on a conversation, and it is
reachable with a terminal Pi that does not exit within five seconds (finding 1, confirmed from the first
round). A second path reports a Stop that did not happen (finding 2, new). Both must change. The other
five findings are smaller, and the Developer may decline any of them with a reason. The Developer decides
what happens to every finding (law 11).

## The Developer's answer, in short (17 October 2026)

Answered on branch `apimode/p4-wheel` (pull request #4094) by a new Developer seat, the original having gone. Findings 1
to 6 are fixed, each with a test; finding 7 is declined with the reason and a test that the three components cope across
a switch; the test that leaned on the real clock now runs the detector on a fake one. The answers are under each finding.

**Every fix's test fails without it.** For each finding I put the old behaviour back in the production code alone, built,
ran only that finding's tests, and restored: finding 1 failed (the old `InvalidOperationException`), 2 failed (three
tests: Stop answered as done), 3 failed (the take hung until the thirty-second hang guard), 4 failed (the session ended
Starting), 5 failed (no row with nobody holding the wheel was ever pushed), 6 failed (the turn ended "stopped by the
user").

**Ran on devlinux, one project at a time, filtered, with no other test run on the machine:**

- `dotnet test src/CcDirector.Core.UnitTests --filter "FullyQualifiedName~CcDirector.Core.UnitTests.ApiMode"` (the whole
  API-mode namespace, the wheel and creation tests among it): **157 passed, 0 failed, 0 skipped.**
- `dotnet test src/CcDirector.Gateway.UnitTests --filter "FullyQualifiedName~ApiModeConnectionThroughTheDirectorTests"`:
  **16 passed, 0 failed, 0 skipped.**
- Not run here: the Windows gate (`scripts\test-local.ps1`), which is the Tech Lead's, and the rest of the parked
  Gateway unit suite.

## Scope: what I read, what I ran, what I could not reach

**Read, in full, on the pull request head:**

- `src/CcDirector.Core/ApiMode/ApiModeSessionBackend.cs` (whole file), `ApiModeSessionHost.cs` (the diff,
  then `SendPromptAsync`, `TrySendPromptNowAsync`, `StopAsync`, `ShutdownAsync`, `SuspendAsync`,
  `ResumeAsync`, `EndWhileSuspendedLocked`, `DisposeAsync`, `StartConnection`, `ConnectAsync`, `PumpAsync`,
  `ObserveAsync`, `OnRecordLocked`, `OnProcessEndedLocked`, `RefuseQueuedLocked`, `TakeStepLocked`,
  `WriteQueuedPromptsLocked`, `ReconcileTurnStartTimerLocked`, `OnTurnStartOverdueAsync`,
  `StopProcessAsync` and the `Connection` class), `SessionWheel.cs` (whole file),
  `SessionConnectionModeWords.cs`, and `ApiModeTransport.ShutdownAsync` with `ThrowIfNotRunning` and the
  transport's state constants.
- `src/CcDirector.Core/Sessions/Session.cs`: the diff, and around it `OnApiModeEvents`,
  `InitializeScreen`, `SendTextAsync`'s and `SendTextOnlyWhenWaitingForInputAsync`'s API-mode branches,
  `SendApiModePromptOnlyWhenWaitingAsync`, `CancelTurnAsync`'s API-mode branch and
  `StopApiModeTurnAsync`, `KillAsync`, `ApplyTerminalActivityState`, every place `IsBrandNew` is cleared,
  and `Dispose`. `SessionManager.cs`: the diff, the launch path from the executable's resolution to
  `backend.Start` (lines 1236 to 1300), `KillSessionAsync`, `KillAndRemoveForDeletionAsync` and the
  kill-all at line 2124.
- `src/CcDirector.Core/Wingman/TerminalStateDetector.cs`: the diff, the constructors, `Watcher.Start`,
  `OnActivityStateChanged`, `OnBytesCore` (the brand-new suppression) and the quiet threshold.
- `src/CcDirector.ControlApi/SessionCommandExecutor.cs` (`WheelAsync`, `WheelCoreAsync`, `PromptAsync`,
  `SendPromptAsync` including the twenty-second answer budget, `TurnStopAsync`, `TurnStopCoreAsync`,
  `KillAsync`), `SessionWriteExecutor.cs`, `ControlApiHost.cs` (the new `PushSessionRowAsync`, the block
  that turns session events into deltas at lines 1818 to 1870), `GatewayStreamClient.cs`
  (`PushDeltaAndWaitAsync`, `NotifyDelta`), `ControlEndpoints.cs`.
- On origin/main: `src/CcDirector.Gateway/Api/GatewayEndpoints.ApiModeVerbs.cs` in full (the three
  commands, the contract on the Director in its summary, and the rule that Stop needs only the mode),
  `DirectorCommandRouter.cs` (the thirty-second default command timeout),
  `src/CcDirector.Core/UnixPty/UnixProcessHost.cs` and `ConPty/ProcessHost.cs` (`GracefulShutdownAsync`),
  `Backends/UnixPtyBackend.cs` (`HasExited`), `ApiMode/Pi/PiDialect.cs` (the abort's reason), and the
  buffer readers in `TerminalSessionRecorder.cs`, `Storage/SessionLogWriter.cs` and
  `SessionStatusWingman.cs`.
- The tests: `ApiModeWheelTests.cs` (all sixteen), `FakeTerminalBackend.cs`, the `FakeApiModeProcess.cs`
  change, the new test in `ApiModeSessionCreationTests.cs` with that class's rig,
  `ApiModeSessionTests.cs`, `SessionConnectionModeWordsTests.cs`,
  `ApiModeConnectionThroughTheDirectorTests.cs`, `MinimalPiProcess.cs`, `DirectorKeysBeforeRosterTests.cs`,
  and the helpers `ScriptedPi.WithinHangGuard` and `RecordingSink.WaitForAsync`.
- The documents: the pull request's description, issue #4070 (sections 3, 5, 7 and 8),
  `event-model.md` in full, the mission README, `reviews/phase-2-wheel-verb-review.md` (both rounds, which
  hold the Delivery Lead's rulings this pull request is bound by), `gates/phase-2-wheel-verb-gate.md`,
  `reviews/phase-4-dispose-hang-review.md`, and the first round of this review on
  `origin/apimode/p4-wheel-review`.

**Ran, on devlinux (Linux, .NET 10.0.301 at `$HOME/.dotnet`), one project at a time, never two at once,
with no other test run on the machine (checked with `pgrep` first):**

- `dotnet test src/CcDirector.Core.UnitTests --filter "FullyQualifiedName~ApiModeWheelTests|FullyQualifiedName~ApiModeSessionCreationTests|FullyQualifiedName~SessionConnectionModeWordsTests|FullyQualifiedName~ApiModeSessionTests"`
  with hang detection on: **55 passed, 0 failed, 0 skipped**, 831 milliseconds. The sixteen wheel tests
  are among them.
- `dotnet test src/CcDirector.Avalonia.Tests --filter "FullyQualifiedName~DirectorKeysBeforeRosterTests"`:
  **17 passed, 0 failed**, including the two new awaited-push tests.
- `dotnet test src/CcDirector.Gateway.UnitTests --filter "FullyQualifiedName~ApiModeConnectionThroughTheDirectorTests"`:
  **14 passed, 0 failed, 0 skipped**, 255 milliseconds, including the four new wheel verb tests and the awaited-push test.
- `git merge-tree --write-tree` of the pull request head against origin/main, against
  `origin/apimode/p4-prompt-text` (pull request #4121) and against `origin/apimode/p4-silent-end` (pull
  request #4129). The results are under "Conflicts" below.
- A first attempt at the test runs failed for reasons of this machine, not of the branch: the `dotnet` on
  the agent shell's path is version 8 and refuses the repository's `global.json`, and the Gateway test
  project's build runs `npm ci`, which needs node on the path. Both runs were repeated with the right
  tools and are the numbers above.

**Could not reach:**

- No real Pi, Director or Gateway was started. Everything about the real terminal hosts (the kill on a
  timed-out shutdown, the exit monitor) is read, not seen; finding 1 rests on that reading and on the
  first round's throwaway test, which I did not repeat.
- No Windows machine. `scripts\test-local.ps1` and the `-Parked` gate were not run; the pull request says
  the Windows gate is the Tech Lead's. `CommandLineLauncher.Build` wrapping a `pi.cmd` shim is read only.
- The Terminal tab buttons and the clients: not in this pull request, by its own statement.
- I wrote no test, throwaway or otherwise. Where the first round proved a finding with one, I say so and
  I do not claim the measurement as mine.

## The brief's five questions

### (1) Is every ruling met?

- **The commands.** `wheel-take` and `wheel-hand-back` are listed in `SessionWriteExecutor.Verbs` and
  dispatched to `SessionCommandExecutor.WheelAsync`; a test reads the names from
  `GatewayEndpoints.WheelTakeCommand` and `WheelHandBackCommand` so the two sides cannot drift.
  `turn-stop` is present: it was merged with phase 2 and is unchanged by this pull request, and its
  presence is pinned by the existing test. **But Stop and the wheel do not know about each other:
  finding 2.**
- **The reply waits for the push.** `WheelCoreAsync` awaits `pushSessionRow`, which `ControlApiHost` binds
  to `GatewayStreamClient.PushDeltaAndWaitAsync`, which awaits `IDirectorHubCalls.PushDeltaAsync` (the
  invoke, never `NotifyDelta`, never a bare `SendAsync`) and refuses before this connection's keys are
  sent. The Avalonia test holds the hub's delta and proves the client does not return; the Gateway-side
  test holds the push and proves no reply goes out before it. Met.
- **Only on a session the agent controls.** That rule is the Gateway's (`RunApiModeVerbAsync`, phase 2),
  and the Director receives these commands only from the Gateway, so the Director side has nothing to
  add and adds nothing. Met where it lives.
- **One holder.** A second take is refused by the Director (`AlreadyHeldReason`) and by the Gateway; a
  hand back mid-turn is refused with `TerminalMidTurnReason`, read under the ownership lock from the
  session's activity, which the terminal state detector drives while the user holds the wheel. Both
  tested. **One path breaks it: finding 1.**
- **Held prompts.** A send while the host is suspended queues with `PromptQueuedReason.UserHasTheWheel`
  and is never typed into the terminal. On hand back `ResumeAsync` starts Pi, and once Pi names its
  conversation (`ConnectionReady`) `WriteQueuedPromptsLocked` writes the queue in order, each prompt
  getting its own `PromptDelivered` or `PromptRefused` from the dialect. The test
  `SendTextAsync_WhileTheUserHoldsTheWheel_IsQueuedThenDeliveredOnHandBackWithItsReceipt` proves it for
  one prompt; the order for several rests on phase 1's queue tests, which this pull request does not
  repeat with the wheel held. A prompt that waits longer than the Gateway command's twenty-second answer
  budget is answered "Delivering" by `SendPromptAsync` and its late outcome recorded when the receipt
  arrives, the same as a prompt queued behind a running turn. Met.
- **What a guarded send does while the user holds the wheel.** `SendTextOnlyWhenWaitingForInputAsync`
  (the Fleet Manager's events, doorbells) goes to `TrySendPromptNowAsync`, which returns null while
  suspended, so the prompt is refused busy and not held. That is phase 1's rule for a guarded send
  (never wait) and is consistent, but the refusal's sentence, "the agent is not waiting for a prompt (one
  is running or waiting to be sent, or it is starting)", does not say the true reason. A note, not a
  finding.

### (2) Can two holders exist, can the lock deadlock, can Pi start while the user holds the wheel?

- **Two holders inside one session.** The ownership lock is held across the whole of a take, a hand back,
  the terminal's own exit and the start of a kill; the take suspends the host before the terminal factory
  is called, and `SuspendAsync` returns only after the process's end was handled under the host's lock
  (`Connection.EndHandled`). `TakeWheelAsync_PiSlowToExit_TheTerminalStartsOnlyAfterPiHasExited` proves no
  terminal is made while Pi lives. The hand back ends the terminal before `ResumeAsync`. **The one path
  that breaks this is finding 1: the hand back's wait for the terminal's exit is not a wait on the
  timeout branch of the real terminal hosts.**
- **Two holders across sessions.** `ApiModeConversationClaims` refuses a second API-mode session on the
  same folder and conversation, and releases on the session's end and on dispose; tested. It does not
  cover a terminal-mode Pi resumed on the same conversation id, by the pull request's own design
  ("terminal-mode Pi sessions do not take or check it"). The mission sentence is broader ("exactly one
  process holds a conversation at any moment, and the Director enforces it"); a note below.
- **Pi starting while the user holds the wheel.** `StartConnection` has three callers: `Start`, the
  restart in `OnProcessEndedLocked`, and `ResumeAsync`. The restart is skipped when
  `connection.Suspending || _suspended`; `ResumeAsync` requires `_suspended`; `OnTurnStartOverdueAsync`
  returns when either flag is set; `SendPromptAsync`, `TrySendPromptNowAsync` and
  `WriteQueuedPromptsLocked` all read `_suspended`. Both orders of the turn-start bound against a take are
  safe: the flag is set under the host's lock in the first step of `SuspendAsync`, and the timer's callback
  reads it under the same lock. The pull request's stated known limit (the timer firing exactly inside
  the take's wait cannot be made deterministic on one fake clock) is accurate; the two tests that touch
  it (`...AfterTheTurnStartBoundRestartedPi...` and `...WhilePiOwesATurn...`) pin the two orders that can
  be pinned.
- **Pi exiting on its own during a take.** If Pi ends after `_suspended` is set, `OnProcessEndedLocked`
  treats the end as the take's own (no restart, queue kept, `EndHandled` set and return), and
  `SuspendAsync`'s later `Transport.ShutdownAsync` returns at once because the transport's state stays
  `Running` after an exit and `_ended` is already complete. The terminal then opens as asked. Correct.
- **Director shutdown mid-switch.** `KillAsync` takes the ownership lock behind a take in progress, so a
  shutdown that arrives during a take ends the terminal the take opened, then calls `ShutdownAsync`,
  which, finding the host suspended, ends the session on the last process's end
  (`EndWhileSuspendedLocked`) and refuses every queued prompt with the reason. Serialised and correct,
  as long as the take itself ends. **It does not always end: finding 3.** The first round also proved by
  a throwaway test that a kill while the user holds the wheel (take complete) works and gives every
  queued prompt its refusal; I confirm the path by reading.
- **Director restart with the wheel held.** The user's terminal is a child of the Director's own
  pseudo-terminal and does not outlive it; after the restart the session comes back over the protocol
  with nothing beside it. Read, not run.
- **Deadlock.** The two locks are never nested the wrong way: the ownership lock is never taken while the
  host's lock is held, and the host's lock is never held across an await on the ownership lock. The
  terminal's `ProcessExited` handler takes the ownership lock on a fire-and-forget task, so a hand back
  that ends the terminal under the lock is not waited on by it. The one unbounded wait under the ownership
  lock is the take's stop of a running turn (finding 3): not a deadlock in the strict sense, but a lock
  held for as long as Pi takes to confirm its abort, with the kill queued behind it.

### (3) Is the terminal started exactly as the terminal path starts it?

Yes. `SessionManager` hands the API-mode backend the resolved executable and the launch line *before*
`CommandLineLauncher.Build` (line 1253: `backend is ApiModeSessionBackend ? (resolvedExe, args) :
CommandLineLauncher.Build(resolvedExe, args)`), and the same `envVars` the terminal path passes. The take
applies `CommandLineLauncher.Build(_executable, _launchLine)`, passes the same folder and the same
environment, and starts the terminal through the terminal path's own factory
(`PlatformSessionBackend.CreateDefault(_options.DefaultBufferSizeBytes)`, the very expression the
terminal arm uses). The launch line keeps `--session-id` and the preamble's `--append-system-prompt`,
without `--mode rpc` (`TakeWheelAsync_StartsPisTerminalWithTheTerminalPathsLaunch`). The one difference
is the size: the terminal path starts at 120 by 30 and is resized by the view, while the take starts at
the session's current size (`Resize` keeps `_cols` and `_rows` up to date). That is the better of the
two. `session.LaunchExecutable` is not updated on a take; cosmetic.

### (4) Do terminal-mode sessions stay untouched?

Yes, read line by line. `TerminalStateDetector.Wire` gains one test on `ConnectionMode == Api` that is
false for every terminal session, which then falls through to the unchanged code; `OnSessionRemoved` and
`Dispose` gain a lookup in a dictionary that is empty for a Director with no API-mode session. In
`Session`, every new line is behind `_apiMode is not null` or returns `NoWheelReason` when it is null, and
`OnTerminalChanged` is raised only from the API-mode backend's event. In `SessionManager` the only change
is inside the API-mode arm of the backend ternary plus a property with a default. `ControlEndpoints.Map`
answers `none` for a terminal row exactly as before (pinned by `SessionConnectionModeWordsTests`).
`SessionWriteExecutor` lists two more names. `ControlApiHost` builds one more delegate per command. Tested:
`TakeWheelAsync_TerminalSession_IsRefused` and `WheelCoreAsync_TerminalSession_IsRefusedForBothCommands`.

### (5) Are the tests real proofs, and do any depend on the real clock, a sleep or a real process?

- **Fakes only.** `ScriptedPi` replays the real recordings; `FakeTerminalBackend` runs nothing and exits
  when told; `MinimalPiProcess` answers `get_state` and exits when its input closes; `FakeTimeProvider`
  drives the turn-start bound; the Avalonia tests use a recording hub. No `Task.Delay`, `Thread.Sleep`,
  `Stopwatch` or real process in any changed test file (searched).
- **The hang guards.** `ScriptedPi.WithinHangGuard` and the Gateway tests' `WaitAsync(30 seconds)` are
  real-clock waits that can only *fail* a test that hangs; they never decide a pass. That is the shape
  the dispose-hang review accepted, and I accept it too.
- **One test leans on the real clock for its pass**, lightly:
  `HandBackWheelAsync_PiWorkingInTheTerminalAsTheDetectorReadsIt_IsRefused` runs a real
  `TerminalStateDetector` on `TimeProvider.System` with the ten-second quiet threshold. The hand back must
  run before that threshold flips the state back to waiting; it runs on the next statement, so the margin
  is ten seconds of machine stall. The detector's internal constructor takes a `TimeProvider`, so the
  margin could be made infinite at no cost. A note, not a finding.
- **What the tests prove.** The timeline tests assert the order of process starts and exits from the
  factories themselves, which is the right place; the slow-Pi test proves the take waits; the stream test
  proves one generation and a sequence rising by one across the switch; the Director-side test proves the
  reply waits for the push with a held delta. Two gaps against the brief's rulings: no test has a turn
  *running* at the take (the two "owes a turn" tests are acknowledged prompts with no `agent_start`),
  so the order and the words of that turn's end are pinned by nothing (first round, finding 2); and no
  test covers a hand back whose terminal will not exit (finding 1).

## Findings

Numbered by severity. The first round's numbering is given in brackets so the Developer can answer each
once.

### 1. [First round, finding 1, confirmed] A hand back whose terminal does not exit within the grace detaches it anyway, and the next hand back starts Pi beside the still-running terminal

**Where.** `src/CcDirector.Core/ApiMode/ApiModeSessionBackend.cs`, `EndTerminalLocked` and its caller
`HandBackWheelAsync`.

**What the code does.** `EndTerminalLocked` sets `_terminal = null` and `_wheelHolder = None` *before* it
awaits `terminal.GracefulShutdownAsync(5000)`, and throws afterwards if `terminal.HasExited` is false. The
throw is reachable on both real hosts: `UnixProcessHost.GracefulShutdownAsync` (lines 248 to 265) writes
Control+C, sends the terminate signal, waits up to the timeout, and when the timeout wins sends the kill
signal and **returns without waiting for the exit**; `UnixPtyBackend.HasExited` (line 38) is true only
once the exit monitor has set the status to "Exited". `ConPty/ProcessHost.GracefulShutdownAsync` (lines
331 to 347) does the same with `KillProcessTree`. So on the timeout branch the backend throws with the
terminal already detached: `_terminal` is null, the holder is `None`, the terminal object is never disposed
(its own exit handler finds `ReferenceEquals(_terminal, terminal)` false and returns), no `TerminalChanged`
is raised (the `detached` flag is set only after the await), the session's screen stays fed from a buffer
nothing owns, and `Session.HandBackWheelAsync` and `WheelCoreAsync` end in an exception instead of a
refusal. The next hand back finds `_terminal` null and calls `host.ResumeAsync`: Pi starts over the
protocol while the terminal may still be alive.

**Proof.** By reading, above. The first round also proved it with a throwaway test on the pull request's
own rig (a terminal stand-in whose shutdown returns without exiting; take, hand back, hand back): the
first hand back threw, the second returned Done with two Pi processes started and the terminal still
running. I did not repeat that test.

**The harm.** The mission's one hard rule (issue #4070, section 5), broken by the Director itself on the
path whose own exception message says it guards it. A terminal Pi that does not die within five seconds
of Control+C and the terminate signal is not exotic: one with a tool's child process running is enough.

**Why it must change.** Detach only once the exit is confirmed: either wait for the exit after the kill
(the take already waits for a slow Pi in `SuspendAsync`), or, when the terminal is still alive after the
grace, keep `_terminal` and the holder `User` and return a refusal that says so, so the next hand back
tries again against the same terminal. Dispose the terminal object on every exit path. Pin it with a
test: a terminal that will not exit, a hand back, and an assertion that one Pi was started and the holder
is still `User`.

Developer's answer: **Fixed.** `EndTerminalLocked` no longer detaches anything before the exit is confirmed. It asks
the terminal to shut down (the hosts kill it after the five seconds) and returns true only once `HasExited` is true;
only then are `_terminal` and the holder cleared and the terminal disposed. When the terminal is still running after its
shutdown returns, it returns false and nothing changes: the terminal stays attached, the holder stays `User`, and
`HandBackWheelAsync` returns the refusal `TerminalStillRunningReason` ("... the wheel was not handed back: two processes
may never hold one conversation. The terminal is still yours; hand back again once it has gone."), so the next hand back
tries the same terminal again. When the kill does land, the terminal's own exit handler finds it still attached and
handles it as an exit on its own (holder `None`, terminal disposed, `TerminalChanged` raised), after which a hand back
starts Pi with nothing beside it. On the kill path (`GracefulShutdownAsync`), where the session is ending and no Pi will
be started, a terminal that has not exited is let go and disposed, which kills it. I chose the refusal over waiting
longer after the kill: the wait would need a second clock bound for the same hosts that already gave up waiting, and the
refusal is honest and safe.

Test: `ApiModeWheelTests.HandBackWheelAsync_TheTerminalHasNotExitedAfterItsShutdown_IsRefusedAndNoPiStartsBesideIt` -
a terminal stand-in that keeps running after its shutdown (`FakeTerminalBackend.ExitsOnShutdown = false`), two hand
backs (both refused with the reason, holder `User`, screen still attached, one Pi started), then the exit, then a third
hand back that starts Pi; the timeline proves the terminal had exited before the second Pi started. **Without the fix**
(the old order put back: detach, shut down, throw) it fails: `System.InvalidOperationException` out of the first hand
back.

### 2. [New] A Stop while the user holds the wheel, or while no process holds the conversation, answers "the agent confirmed the stop" and stops nothing

**Where.** `src/CcDirector.ControlApi/SessionCommandExecutor.cs`, `TurnStopCoreAsync`;
`src/CcDirector.Core/Sessions/Session.cs`, `CancelTurnAsync`'s API-mode branch and `StopApiModeTurnAsync`;
`src/CcDirector.Core/ApiMode/ApiModeSessionHost.cs`, `StopAsync`.

**What the code does.** The Gateway sends `turn-stop` for an API-mode session whatever the holder, by
the phase 2 ruling that "Stop needs only the mode: a holder the Gateway cannot read must never keep a
user from stopping a turn". On the Director, `TurnStopCoreAsync` checks only the connection mode, calls
`CancelTurnAsync`, which calls `StopApiModeTurnAsync`, which calls `host.StopAsync`. While the user holds
the wheel the host's connection has ended, so `StopAsync` logs "Pi is not running; nothing to stop" and
returns null; `TurnStopCoreAsync` then logs "the agent confirmed the stop" and returns `Success`. The
Gateway answers the caller `accepted`. The turn running in the user's terminal is untouched. The same
happens after the terminal exited on its own (holder `None`), where there is no turn at all.

**The harm.** A person on the phone or in the Cockpit who sees the Chat working and presses Stop is told
it stopped, and it did not. Before this pull request the host's connection could not be ended while the
session lived, so this answer was unreachable; this pull request makes it reachable on every take. It is
also the ruling in reverse: the Gateway deliberately does not refuse Stop on a holder it cannot read
because it trusts the Director to rule, and the Director rules nothing.

**Why it must change.** The Director must answer the truth: refuse Stop while the holder is `User` with
a sentence that says the turn is in the terminal (the user can stop it there, or hand back), and refuse
it while the holder is `None` with the sentence that nothing runs. Whether a Stop while the user holds
the wheel should instead interrupt the terminal is the Developer's to propose and the Tech Lead's to
rule; refusing with the reason is the smallest honest answer. One test each.

Developer's answer: **Fixed: Stop is refused with the true reason.** The ruling is the backend's, made from the holder
read under the ownership lock (so a switch under way is seen finished, never half done; the lock is not held across Pi's
answer): `ApiModeSessionBackend.StopTurnAsync` returns null when Pi confirmed the stop, or the reason nothing was stopped -
`StopWhileUserHoldsReason` ("You have the wheel of this session, so its turn runs in Pi's terminal, where Stop cannot
reach it: nothing was stopped. Stop it in the terminal, then hand back ..."), `StopWithNoHolderReason` ("No process
holds this session's conversation (its terminal exited), so no turn is running and nothing was stopped ..."), or that
the session has ended. If a take begins after that read, while the abort is on its way, and Pi was therefore not running
to answer, the stop is refused too. `Session.StopApiModeTurnAsync` passes it on, and `TurnStopCoreAsync` answers a
refusal as Conflict in those words, which the Gateway passes through. I did not make Stop interrupt the terminal: that
would be a new behaviour for the Tech Lead to rule on, and refusing with the reason is the smallest honest answer.

Tests: `ApiModeWheelTests.StopApiModeTurnAsync_WhileTheUserHoldsTheWheel_IsRefusedWithTheReasonAndStopsNothing` (nothing
typed into the terminal, no abort written), `..._AfterTheTerminalExitedOnItsOwn_IsRefusedBecauseNothingRuns`,
`..._WhileTheProtocolHoldsIt_IsPisAbortConfirmed` (the ordinary path still answers null), and on the Gateway side
`ApiModeConnectionThroughTheDirectorTests.TurnStopCoreAsync_WhileTheUserHoldsTheWheel_IsRefusedInTheDirectorsWords`.
**Without the fix** (the ruling taken out) the two Core refusal tests fail with "Strings differ" and the Gateway-side
test fails at `Assert.False(result.Ok)`.

### 3. [New] The take's stop of a running turn has no bound, and it runs under the ownership lock, so a Pi that does not confirm its abort blocks every later take, hand back and kill, and the "last resort" kill is never reached

**Where.** `src/CcDirector.Core/ApiMode/ApiModeSessionHost.cs`, `SuspendAsync` (the `StopAsync` before the
transport's `ShutdownAsync`) and `StopAsync`; `ApiModeSessionBackend.cs`, `TakeWheelAsync` (which holds
`_ownership` across `SuspendAsync`) and `GracefulShutdownAsync` (which awaits `_ownership.WaitAsync()` with
no timeout); `Session.KillAsync`; `SessionManager` lines 1999 and 2124.

**What the code does.** When a turn is running, `SuspendAsync` first calls `StopAsync`, which writes Pi's
`abort` and awaits `waiter.Completion` until Pi answers the abort or the process ends. The cancellation
token is `CancellationToken.None` all the way from `Session.TakeWheelAsync`, and nothing else bounds the
wait. Pi answers an abort only once it is idle (event model, section 3.4). Only after the stop does
`SuspendAsync` close Pi's input and let the transport kill it after `ShutdownGrace` (five seconds), which
the pull request calls "the transport's timed kill is the last resort". So for a Pi that is working and
does not become idle on abort (a tool whose child process does not end, a hung model stream, a Pi that
has stopped reading its input), the take never reaches that last resort; it waits in `StopAsync` for as
long as Pi takes, holding the ownership lock the whole time.

**The harm, in order.** The Gateway answers the caller a timeout after thirty seconds
(`DirectorCommandRouter.DefaultCommandTimeout`) while the Director's take carries on. Every later take and
hand back waits behind the lock. The kill verb (`SessionCommandExecutor.KillAsync` to
`SessionManager.KillSessionAsync` to `Session.KillAsync` to `GracefulShutdownAsync`) waits behind the same
lock before its own timeout even starts, so the one verb that would end the stuck Pi cannot reach it. The
Director's own shutdown kills every session through the same `KillAsync` (`SessionManager` line 2124) and
waits there too. Only `Dispose`, which takes no lock, kills the process and unblocks the chain, and in the
kill path `Dispose` runs only after `KillAsync` returns.

**Why it must change.** Bound the stop inside `SuspendAsync` by the same grace the shutdown uses, and go
on to close the input when the bound runs out; the transport already kills after the grace, so the take
then ends within about ten seconds in the worst case, inside the Gateway's wait, and the kill never queues
behind an unbounded wait. Pin it with a test on the fake clock: a Pi that never answers its abort, a take,
the clock advanced past the grace, and an assertion that the take completed, the terminal opened, and the
turn ended failed or cancelled with a reason that says the abort was not confirmed. The first round's
check "the kill while the user holds the wheel works" covers a take that completed; this is the take that
does not.

Developer's answer: **Fixed.** `SuspendAsync` bounds the stop of a running turn by the same grace the shutdown uses, on
the injected clock (`new CancellationTokenSource(grace, _clock)` linked to the caller's token). When the bound runs out
it logs that Pi did not confirm its abort and goes on exactly as after a confirmed stop: Pi's input is closed and the
transport kills it after the grace. So the take ends within about two graces in the worst case (ten seconds), inside the
Gateway's thirty-second wait, and nothing queued behind the ownership lock - a take, a hand back, a kill, the Director's
shutdown - waits on Pi's goodwill. The turn then ends failed with `TurnAbortUnconfirmedReason` ("ended because the user
took the wheel: Pi did not confirm its abort in time, so its process was closed"), with Pi's own end reason after it in
brackets (see finding 6 for how).

Test: `ApiModeWheelTests.TakeWheelAsync_PiNeverConfirmsItsAbort_TheTakeEndsAfterTheGraceAndTheTerminalOpens` - the abort
recording, answered only when the test says, with a turn running; the take writes the abort, which is never answered;
the fake clock moves by the grace; the take completes, Pi had exited before the terminal was made, and the events end
`TurnEnded` (failed, the unconfirmed reason) then `ConnectionEnded`. **Without the fix** (the caller's token passed
instead of the bounded one) it fails: "Waited 30 seconds for the take, past a Pi that never confirms its abort; it never
happened, so the code under test hangs here."

### 4. [First round, finding 3, confirmed] After a hand back the session can be left "Starting": the terminal-changed handler and Pi's ConnectionReady race for the activity state

**Where.** `ApiModeSessionBackend.HandBackWheelAsync` raises `TerminalChanged` in its `finally`, after
`host.ResumeAsync` has returned and the lock is released; `ApiModeSessionHost.ResumeAsync` returns as soon
as the process is started, and the pump and observer loops publish `ConnectionReady` on their own threads;
`Session.OnApiModeTerminalChanged` sets `Starting` when the holder is `Protocol`; `Session.OnApiModeEvents`
sets `WaitingForInput` on `ConnectionReady`. Nothing orders the two.

**Proof.** By reading, the order is not fixed: `ConnectAsync` writes `get_state` as soon as it gets the
host's lock, which `ResumeAsync` releases before `HandBackWheelAsync` releases the ownership lock and
raises the event. The first round measured it with a throwaway test on the fake Pi: forty hand backs,
`WaitingForInput` thirty-five times and `Starting` five times, and in the five nothing set the state again
until the next turn. With a real Pi, which takes hundreds of milliseconds to answer `get_state`, the
handler almost always wins, which is why this is fourth and not first.

**The harm.** The row the awaited push carries, and every delta until the next `TurnStarted`, says the
session is starting when it is idle; the Gateway folds the roster colour from it. The guarded send is not
affected (it consults the host's gate, not the state). The same stomp can land on `Working` when queued
prompts start a turn before the handler runs.

**Why it should change.** Set the session's state for the hand back before `ResumeAsync` is called, under
the ownership lock, or leave the state alone on a hand back and let `ConnectionReady` set it. Either makes
the first round's measurement deterministic.

Developer's answer: **Fixed, by ordering, not by narrowing the race.** The hand back now takes a second callback,
`beforeResume`, which runs under the ownership lock just before `host.ResumeAsync` starts Pi; `Session` passes "set
Starting". Nothing the new Pi reports can come before it, because Pi does not exist yet. The terminal-changed handler no
longer touches the activity when the protocol holds the conversation, so `ConnectionReady` (waiting for a prompt) and a
queued prompt's `TurnStarted` (working) can no longer be overwritten by a late Starting. With no holder (the terminal
exited on its own, or Pi could not be started again) the handler still sets waiting for a prompt; the failed-restart path
now raises `TerminalChanged` too, so a session that was set Starting does not stay there.

Test: `ApiModeWheelTests.HandBackWheelAsync_PiNamesItsConversationBeforeTheHandBackFinishes_TheSessionEndsWaitingForInput`
makes the losing order certain rather than likely: once Pi is started, the hand back is held (through the backend's
status change to "Running") until the session itself has read the new Pi's `ConnectionReady`; the session must then end
waiting for a prompt, having been Starting then waiting and nothing after. **Without the fix** (the callback emptied and
the old handler put back) it fails with "Values differ": the session ends Starting.

### 5. [First round, finding 4, confirmed] The holder becoming `none` when the user's terminal exits on its own is pushed to the Gateway by nothing

**Where.** `ApiModeSessionBackend.HandleTerminalExitAsync` sets the holder to `None` and raises
`TerminalChanged`; `Session.OnApiModeTerminalChanged` sets `WaitingForInput` and raises `OnTerminalChanged`.
`ControlApiHost` lines 1818 to 1870 push a delta on the activity change, the hold, the transcribing,
background-running and explaining flags and the rest; `OnTerminalChanged` is not among them, and
`WheelHolder` has no change event. The same gap is on a hand back whose Pi could not be started (holder
`None`, a refusal returned, and `WheelCoreAsync` pushes nothing on a refusal).

**What happens.** The usual own exit is the user quitting Pi at an idle prompt, when the detector has
already set `WaitingForInput`, so the activity does not change and no delta goes up. The Gateway keeps
`user` until the next full re-push, up to ten seconds.

**The harm.** For that window the Gateway refuses a take with "You already have the wheel of this session:
its terminal is open on the conversation" when the terminal is gone, and a client shows "Hand back" for a
session with no terminal. Bounded and self-healing; the smallest of the seven. Raised because the rule
written in that very block of `ControlApiHost` is "if the fold reads it, it pushes", and the Gateway's
wheel verb reads the holder to refuse.

**What would close it.** One more subscription of the same shape: `session.OnTerminalChanged` pushing
`ControlEndpoints.Map(session, DirectorId)`. The awaited push stays the contract for the two verbs.

Developer's answer: **Fixed.** A change of holder pushes like any other fold input: `ControlApiHost.PushOnWheelChange`
subscribes `session.OnTerminalChanged` to a delta of `ControlEndpoints.Map(session, DirectorId)`, attached in the same
place and the same one-line shape as the hold, colour and owner pushes. It covers the user's terminal exiting on its own
and a hand back whose Pi could not be started (which now raises `TerminalChanged`, see finding 4). The two wheel commands
keep their awaited push; this one is the ordinary fire-and-forget delta, which is right for a change no command made.

Test: `ApiModeConnectionThroughTheDirectorTests.PushOnWheelChange_TheUsersTerminalExitsOnItsOwn_PushesTheRowWithNobodyHoldingTheWheel`
- the take pushes `user`, the terminal's own exit pushes `none`. **Without the fix** (the subscription made to do nothing)
it fails with a timeout: no row with nobody holding the wheel is ever pushed. What this test does NOT cover: the one line
in `WireDoorbellPush` that calls the helper for every session. No existing test drives that method (none of the pushes
beside it is tested either); the line is read, not run.

### 6. [First round, finding 2, confirmed] A take during a running turn ends the turn with the reason "stopped by the user", and no test has a turn running at the take

**Where.** `ApiModeSessionHost.SuspendAsync` stops the turn through `StopAsync`, whose `TurnEnded` carries
`PiDialect.StoppedByUserReason` ("stopped by the user", `PiDialect.cs` lines 46 and 685).
`OnProcessEndedLocked` rewrites only the `ConnectionEnded` reason to `UserTookTheWheelReason`.

**The harm.** Event model rule 5: a prompt's receipt shows its turn's end beside Delivered. A prompt whose
turn was cut because the user took the wheel is shown in the Chat and in the stored receipt exactly as a
prompt the user stopped with the Stop button; the record cannot tell the two apart. And none of the
sixteen wheel tests has a turn running at the take, so the order `TurnEnded` then `ConnectionEnded` under a
take is pinned by nothing in the gate. The first round proved the order and the words with a throwaway
test on the `abort` recording; I confirm the mechanism by reading.

**Why it should change.** Carry the take's reason onto the `TurnEnded` the suspend produced (a reason the
host passes to the dialect's abort, or the same rewrite the `ConnectionEnded` gets), and add the one test
the `abort` recording already allows.

Developer's answer: **Fixed.** While a take is under way (from the first step of `SuspendAsync`, when `_suspended` is
set), the host gives a turn the take cut short the take's reason before the event is published: a turn cancelled by
Pi's abort ends with `TurnStoppedForTheWheelReason` ("stopped because the user took the wheel: the conversation was
opened in Pi's terminal"), never the Stop button's "stopped by the user"; a turn that failed because Pi never confirmed
the abort and its process was closed (finding 3) ends with `TurnAbortUnconfirmedReason` followed by Pi's own reason. A
turn that finished on its own during the take is left as it was. The receipt and the record can now tell a take from a
Stop.

Test: `ApiModeWheelTests.TakeWheelAsync_DuringARunningTurn_EndsThatTurnWithTheWheelsReasonBeforePisProcessEnds` - the
abort recording with a turn running at the take (the gap the review named): the turn ends cancelled, in the wheel's
words, and the order is `TurnEnded` then `ConnectionEnded` with `UserTookTheWheelReason`. The finding 3 test is the
second test with a turn running at the take. **Without the fix** (the rewrite switched off) it fails with "Strings
differ" at the reason: "stopped by the user".

### 7. [First round, finding 5, confirmed] The recorder, the session log writer and the Wingman's watcher read a session's buffer once, at creation, and never see the wheel's terminal

**Where.** `TerminalSessionRecorder.cs` line 184, `SessionLogWriter.cs` line 90,
`SessionStatusWingman.cs` line 179: each reads `session.Buffer` once and does nothing when it is null,
which an API-mode session's buffer is at creation. None re-reads when the terminal appears.

**The harm.** The minutes the user spends typing into Pi's terminal under the wheel reach neither the
session log nor the recording corpus, where a terminal-mode Pi's screen would. Low. The Developer may rule
the wheel period out of those records' scope; if so, say so in the design note, and still run those
subscribers across a switch in a test to prove they cope.

Developer's answer: **Declined for this pull request, with a test that they cope.** The wheel period stays out of the
session log and the recording corpus. Why that does no harm: (a) nothing is lost from the conversation - the terminal
runs the same Pi on the same `--session-id` in the same folder, and Pi writes every message of that conversation to its
own session file, which is where the conversation's record lives, for terminal and API mode alike; (b) the session log
and the recording corpus record a terminal-mode session's screen; an API-mode session's record is its event stream, which
carries the take and the hand back as `ConnectionEnded` and `ConnectionReady` with their reasons, so the record shows
exactly when the user held the wheel; (c) the Wingman's watcher has nothing to read while the user types in a terminal
they are looking at, and its stop reading comes back with the protocol. Re-wiring the three components to follow
`OnTerminalChanged` is a change to three components shared by every terminal session, which I would rather not make
inside this pull request; if the Tech Lead wants the wheel period recorded, it is a follow-up of its own.

Test: `ApiModeSessionCreationTests.TakeWheelAsync_ThenHandBack_EverySessionMachineryCopesAcrossTheSwitch` - a real
create with the recorder, the session log, the Wingman's watchers and every other per-session component attached; take,
Pi draws in the terminal, hand back, then a prompt over the protocol is delivered, and the session carries on with no
buffer. This test proves they cope; it is not a fail-without test, because nothing was fixed.

## Notes, not findings

- **The pull request says "when no push path exists, no switch is made".** That is true of the test
  (`WheelCoreAsync_NoWayToReportTheSwitch_IsRefusedAndNothingIsSwitched` passes a null delegate), but
  `ControlApiHost` always supplies a delegate, which throws inside when `_streamClient` is null, which is
  after the switch. In production the refusal-before-switch cannot happen, and the case cannot arise
  either, because the command itself arrives over the stream. The sentence in the description is wider
  than the code; the code is fine.
- **A take that succeeds and whose push then fails** (the stream dropped, or the keys of a fresh
  connection not yet sent) is answered Conflict with "The wheel was switched (now held by: user), but
  the Gateway could not be told". The words are honest and the next re-push carries the holder. The
  caller sees an error for a switch that happened; nothing better is available once the switch is made.
- **A take in the instant between Pi's last batch and the session learning of its exit** (`_sessionEnded`
  set in the host, `_hasExited` not yet set on the backend) throws out of `SuspendAsync` instead of
  returning a refusal; the lock is released, nothing is stranded, the Gateway gets an error instead of
  "has ended". The first round noted the same.
- **The claim covers API-mode sessions only.** A terminal-mode Pi resumed on the same conversation id in
  the same folder is not refused. That is the pull request's stated design and the terminal path is
  unchanged by it, but the mission's sentence is "exactly one process holds a conversation at any moment,
  and the Director enforces it". If the Tech Lead wants the sentence to hold for terminal sessions too,
  it is a phase 5 or 6 question, not this pull request's.
- **The guarded send's refusal while the user holds the wheel** names a running or starting agent, not the
  wheel (question 1 above). Wording.
- **`HandBackWheelAsync_PiWorkingInTheTerminalAsTheDetectorReadsIt_IsRefused`** runs the detector on the
  real clock with a ten-second margin (question 5 above). The internal constructor takes a `TimeProvider`.

  Developer's answer: **Replaced (rule 5a).** The creation tests' terminal state detector now runs on a fake clock of
  its own (the internal constructor, `TerminalStateDetector.QuietThreshold`, a `FakeTimeProvider`). The test moves only
  that clock, in steps of the settle check (400 milliseconds) and never past the settle deferral (three seconds), which
  is far short of the ten-second quiet threshold, so the reading can no longer turn back to waiting before the hand back
  reads it, however long the machine stalls. Passes; all nine creation tests pass on the fake clock.
- **`TakeWheelAsync_WhilePiOwesATurn_TheTurnStartRestartNeverRacesIt`** proves that the timer was disarmed
  when Pi's process ended during the take; the `_suspended` checks in the timer's callback are covered by
  reading, not by this test. The pull request's known limit says as much.
- **Disposing the backend while the user holds the wheel without a kill first** leaves the fire-and-forget
  `DisposeHostAsync` waiting for ever on the observer loop, which only `ShutdownAsync` completes while
  suspended. Every product path kills first; the Director's own exit does not care. The first round noted
  the same.
- **Brand-new suppression.** The detector drops a brand-new session's bytes, so the mid-turn guard would
  be inert for a user who takes the wheel of a fresh session; but a typed Enter through `SendInput` clears
  `IsBrandNew` (`Session.cs` line 3288), exactly as in terminal mode, before Pi can draw a response. Not a
  gap. The new `IsBrandNew = false` on a delivered API-mode prompt is right and closes a phase 1 gap.
- **The twenty-second answer budget** in `SendPromptAsync` means a prompt held by the wheel is answered
  "Delivering" to the Gateway and its receipt arrives later through the event stream and the late-outcome
  record. The same as a prompt held behind a turn; not new here, and right.

## Conflicts I can see

Measured with `git merge-tree --write-tree` from the pull request head, 17 October 2026:

- **origin/main** (seventeen commits past the branch's base at the time of writing): merges clean. The only commit on main since the branch's base that touches a file this
  pull request touches is #4072 (`Session.cs`, the doorbell line), and it merges without conflict.
- **Pull request #4121** (`apimode/p4-prompt-text`): merges clean, although both touch
  `ApiModeSessionHost.cs` and `ApiModeSessionTests.cs`.
- **Pull request #4129** (`apimode/p4-silent-end`): **conflicts** in `ApiModeSessionBackend.cs` and
  `ApiModeSessionHost.cs`. Whichever of the two lands second must be rebased by hand on those two files;
  `Session.cs`, `ApiModeSessionCreationTests.cs` and `ApiModeSessionTests.cs` auto-merge.

## What the next seat need not re-check

- The push contract is met by construction and by two tests; the hub method is an invoke, not a send.
- Terminal-mode sessions run no new statement that changes their behaviour (question 4).
- The terminal is started through the terminal path's own factory, launcher and environment (question 3).
- The pull request's code is unchanged since the first round, so every one of that round's findings
  stands as written there; this file is the place to answer them.

## Round 2

Reviewed on 17 October 2026 against commit `697af4171`, the head of pull request #4094 after the Developer's answers
(commit `3af9df1f4`) and the merge of origin/main (commit `1bbd9bd12`, which brought the silent end of pull request
#4129 and the event push of pull request #4090) into the branch. Read in a worktree cut from that commit at
`../devthrottle-review-4094b`, diffed against origin/main (22 files, 2,743 lines added, 71 removed). Reviewer seat,
session `c010dd7e`, running a different agent family from the Developer. Reading only: nothing here was built, fixed or
committed as a test by the reviewer. Where I ran a throwaway test to measure a claim, I say so, and the file was restored
to the commit's bytes before anything was committed (the working tree was clean at the commit of this section).

### Verdict

**Not ready to merge as written, for one defect in the merge resolution; everything else holds.** Six of the seven
first-round answers are right: each fix closes the harm it was raised for, and each fix's test fails without it as the
Developer says (I did not repeat the fail-without runs; I read each test against its fix and the fixes are the ones the
tests exercise). The declined seventh is declined with a reason that holds. The merge resolution is right in four of the
five cases the brief names, and wrong in one: **a take that had to kill Pi, followed by the user ending the session while
they hold the wheel, reads as a crashed session** (round 2, finding 1, measured with a throwaway test). The sentence the
Developer wrote into the merge, that the end while suspended records a clean end, is true of the stream's verdict and
false of the session's, because the session's crash rule reads the exit code too, and the exit code it is handed is the
one the take's kill produced. Two processes never hold the conversation, and the locks cannot deadlock; the tests use no
real clock, sleep or process. The Developer decides what happens to the finding (law 11).

### Scope: what I read, what I ran, what I could not reach

**Read, in full, on the pull request head:** `ApiModeSessionBackend.cs`, `ApiModeSessionHost.cs`, `SessionWheel.cs`,
`SessionConnectionModeWords.cs`; the diff of `Session.cs` and `SessionManager.cs` against origin/main, and in `Session.cs`
the unchanged `OnBackendProcessExited`, `IsUnexpectedExit`, `KillAsync`, `OnApiModeEvents` and `OnBackendStatusChanged`;
the diffs of `ControlApiHost.cs`, `ControlEndpoints.cs`, `GatewayStreamClient.cs`, `SessionCommandExecutor.cs`,
`SessionWriteExecutor.cs` and `TerminalStateDetector.cs`; `ApiModeTransport.ShutdownAsync` and `KillByTransport`;
`UnixPtyBackend.Dispose` and `UnixProcessHost.Dispose` (the claim in the finding 1 answer that disposing a terminal
kills it). **The merge itself:** I rebuilt the automatic merge of the two parents with `git merge-tree --write-tree`,
which conflicts in five files, and diffed the committed merge against it, so every line the Developer chose by hand is
read: `ApiModeSessionBackend.cs` (the `Buffer` property), `ApiModeSessionHost.cs` (the failure line and the clean end in
`EndWhileSuspendedLocked`), `SessionManager.cs` (the forwarding sink with the terminal factory), the two test files, and
the two tests the merge added to `ApiModeWheelTests.cs`. **The tests:** `ApiModeWheelTests.cs` (all twenty-five),
`FakeTerminalBackend.cs`, `FakeApiModeProcess.cs` (the diff), `ApiModeSessionCreationTests.cs` (the rig and the two
wheel tests), `ApiModeSessionTests.cs` and `SessionConnectionModeWordsTests.cs` (the diffs),
`ApiModeConnectionThroughTheDirectorTests.cs` (the diff), `MinimalPiProcess.cs`, `DirectorKeysBeforeRosterTests.cs` (the
diff), and `ScriptedPi.WithinHangGuard`. **The documents:** the first round above with every answer, and the pull
request's description.

**Ran, on devlinux (Linux, .NET 10.0.301), one project at a time, with no other test run on the machine (checked with
`pgrep` before each):**

- `dotnet test src/CcDirector.Core.UnitTests --filter "FullyQualifiedName~CcDirector.Core.UnitTests.ApiMode"`:
  **172 passed, 0 failed, 0 skipped**, 521 milliseconds. The twenty-five wheel tests and the nine creation tests are
  among them.
- `dotnet test src/CcDirector.Gateway.UnitTests --filter "FullyQualifiedName~ApiModeConnectionThroughTheDirectorTests"`
  (built with `-p:RunWorkspaceTypecheck=false`): **16 passed, 0 failed, 0 skipped**, 275 milliseconds.
- `dotnet test src/CcDirector.Avalonia.Tests --filter "FullyQualifiedName~DirectorKeysBeforeRosterTests"` (a file the
  merge resolved by hand): **19 passed, 0 failed, 0 skipped**, 132 milliseconds.
- Two throwaway tests on the pull request's own rig, appended to `ApiModeWheelTests.cs`, run alone, and the file restored
  from the commit's bytes afterwards. Their results are under finding 1 and under question (2) below.

**Could not reach:** no real Pi, Director or Gateway was started, so the real transport's kill and the real terminal
hosts' kill are read, not seen. No Windows machine: `scripts\test-local.ps1` was not run; the pull request says the
Windows gate is the Tech Lead's. The desktop and the clients are not in this pull request.

### (1) The seven answers

1. **Finding 1 (the terminal that does not exit): right.** `EndTerminalLocked` now changes nothing until `HasExited` is
   true, and returns false otherwise; `HandBackWheelAsync` turns false into `TerminalStillRunningReason` with the holder
   still `User` and the terminal still attached. The exit, when the kill lands, reaches `HandleTerminalExitAsync`, which
   finds the terminal still attached and detaches it as an exit on its own, so the next hand back starts Pi with nothing
   beside it. On the kill path (`GracefulShutdownAsync`) a terminal that has not exited is detached and disposed, and I
   confirmed that `UnixProcessHost.Dispose` sends the kill signal to a child that has not exited, so "disposed, which kills
   it" is true on the Unix host. The test (`...TheTerminalHasNotExitedAfterItsShutdown...`) drives exactly the false
   branch twice, then the exit, then the hand back, and reads the timeline from the factories. Closed.
2. **Finding 2 (Stop while the user holds the wheel): right.** `ApiModeSessionBackend.StopTurnAsync` reads the refusal
   under the ownership lock, so a take under way is seen finished (bounded, finding 3), and answers `StopWhileUserHoldsReason`,
   `StopWithNoHolderReason`, or the ended sentence. The lock is released before Pi's answer is awaited. The race the answer
   names, a take beginning after the read, is caught by `host.Suspended` after a null answer. `TurnStopCoreAsync` answers
   Conflict in those words. Three Core tests and one Gateway-side test. One wording note below: the no-holder sentence says
   "its terminal exited" also when the holder is `None` because a hand back could not start Pi (measured, question 2).
   Closed.
3. **Finding 3 (the unbounded stop under the lock): right.** `SuspendAsync` bounds `StopAsync` with
   `new CancellationTokenSource(grace, _clock)` linked to the caller's token, catches the bound's cancellation alone, and
   goes on to close Pi's input; the transport kills after the same grace. The take therefore ends within two graces, and
   nothing queued behind the ownership lock waits on Pi. The abandoned stop waiter is completed when the process ends
   (`OnProcessEndedLocked` completes every waiter in `_stops`), so nothing leaks. The test advances the fake clock by the
   grace and reads the take's completion, the timeline and the turn's reason. Closed. **But see finding 1 of this round:**
   the kill this path ends in leaves an exit code that a later end of the session reads as a crash.
4. **Finding 4 (the Starting stomp): right.** `beforeResume` runs under the ownership lock before `StartConnection`, so
   Starting cannot land after anything the new Pi reports; `OnApiModeTerminalChanged` sets no activity while the holder is
   `Protocol`, and sets waiting for a prompt only with no holder. The test makes the losing order certain by holding the
   hand back's status change until the session has read `ConnectionReady`, and reads the two states in order. Closed.
5. **Finding 5 (the holder pushed by nothing): right.** `ControlApiHost.PushOnWheelChange` subscribes `OnTerminalChanged`
   to a fire-and-forget delta of the mapped row, attached beside the other pushes in `WireDoorbellPush`; the failed
   restart now raises `TerminalChanged` too (measured, question 2). The Gateway-side test drives the terminal's own exit and
   reads the pushed holder. The Developer's own stated gap stands: the line that attaches the helper for every session is
   read, not run. Closed.
6. **Finding 6 (the take's turn ends "stopped by the user"): right.** `TakeEventsLocked` rewrites, while `_suspended`,
   a cancelled turn's reason to `TurnStoppedForTheWheelReason` and an unconfirmed abort's failed turn to
   `TurnAbortUnconfirmedReason` with Pi's reason in brackets; a turn that ended done is untouched. Two tests now have a turn
   running at the take, which was the gap. The rewrite also catches a turn the user's own Stop cancelled in the same instant
   a take began, which then reads as the wheel's; the two cannot be told apart from inside the host, and the wheel's reason
   is the truer of the two for a turn that ends under a take. Closed.
7. **Finding 7 (the recorder, the log writer and the Wingman never see the wheel's terminal): the decline holds.** The
   reason proves no harm to the conversation's record: the terminal runs the same Pi on the same conversation id in the
   same folder, so every message of the wheel period is in Pi's own session file, which is the record a resume and the
   Chat read; the session log and the recording corpus are terminal-screen records of terminal sessions, and an API-mode
   session's record is its event stream, which carries the take and the hand back with their reasons. The Wingman's reading
   is for a person who is not looking; the user holding the wheel is looking. What is lost is the screen of the wheel period
   in the session log, which the Developer names and rules out of scope for this pull request. The test
   (`...EverySessionMachineryCopesAcrossTheSwitch`) attaches every per-session component and drives a take, a draw, a hand
   back and a prompt; it proves they cope, and says so. Accepted as a scope decision with its reason written down.

The note about the detector on the real clock: **right.** The creation tests' detector now runs on its own
`FakeTimeProvider`, and the mid-turn test moves only that clock, in settle-check steps, never as far as the quiet
threshold. The pass no longer depends on the machine.

### (2) The merge resolution

The automatic merge conflicts in five files; the Developer's resolutions are all the right ones: `Buffer` is the
terminal's (the branch's), the forwarding sink is kept with the terminal factory added (both sides), the two test files
keep both sides' tests, and in the host the `events[^1]` rewritten with the wheel's reason is what `_lastConnectionEnded`
carries. The five cases the brief names:

- **A take never reads as a failed or crashed end: true of the take itself, false of the session's end after a take
  that killed Pi.** During a take `OnProcessEndedLocked` returns before `_sessionEnded` or `_sessionEndedInFailure` is
  set, so the take raises no exit and the session's status is untouched (the merge's test reads `Running` while held).
  The line `failure = !suspended && connection.Dialect.EndedInFailure` is therefore not what keeps the take clean; the
  early return is, and `failure` is never written while suspended (`loopFailed` forcing it to true is dead on that path
  too). What keeps the *end while suspended* clean is `EndWhileSuspendedLocked` writing `_sessionEndedInFailure = false`,
  and that covers only the stream's verdict. The session also reads the exit code. **Round 2, finding 1.**
- **A Pi failure while the protocol holds the wheel still reads failed: true.** Not suspended, so the dialect's verdict
  stands as on main; the merge's second test drives it through a hand back and reads `Failed`, `Crashed` and the reason.
- **A terminal exit on its own: true.** Not a session end: the holder becomes `None`, the status text says so, the
  activity is set to waiting for a prompt, the row is pushed, and Stop is refused with the no-holder sentence. Tested
  (`...ThenTheTerminalExitsOnItsOwn...`, `...AfterTheTerminalExitedOnItsOwn...`, the Gateway-side push test).
- **A hand back that cannot start Pi: true, by reading and by a throwaway measurement; no committed test pins it.**
  `ResumeAsync` sets `_suspended = false` only after `StartConnection` returned, so a start that throws leaves the host
  suspended with the old ended connection; the backend turns the exception into a refusal in words, sets the holder to
  `None`, and raises `TerminalChanged`, which sets waiting for a prompt and pushes the row. I measured it on the rig with a
  process factory that throws on the hand back's start: the hand back was refused with "Pi could not be started again over
  the protocol (...)", the holder was `None`, the screen was empty, the activity went Starting then waiting for a prompt,
  Stop was refused with the no-holder sentence, a following take opened the terminal with no Pi started (one Pi process
  in all), and a kill ended the session `Exited`, not crashed, with exit code 0. The path reads truly. It has no test in
  the gate; a note below.
- **A Director shutdown mid-switch: true.** `GracefulShutdownAsync` waits for the ownership lock, which a take now holds
  for at most about two graces (finding 3) and a hand back for at most the terminal's shutdown plus Pi's start; it ends the
  terminal, releases the lock, and `ShutdownAsync` ends the session on the last process's end when suspended, or on the
  running Pi's end when not. The one window I can see is a hand back that takes the ownership lock in the instant between
  the shutdown's release of it and `ShutdownAsync` taking the host's lock: `ResumeAsync` starts a Pi, and `ShutdownAsync`
  then shuts that Pi down and ends the session on its end. Never two processes, never a stuck state, one wasted process
  start; a note, not a finding.

### (3) Two processes, and the lock

- **Inside one session.** Every path that sets the holder to `None` leaves the host suspended or exited, so a take with
  no holder opens the terminal with no Pi to stop and none that can start (`StartConnection` has three callers; the restart
  and the overdue timer both read `_suspended`, and `ResumeAsync` requires it). The hand back that finds its terminal still
  running changes nothing. The take waits for `EndHandled`. The timeline tests read the order from the factories. I found no
  path to two processes.
- **Across sessions.** The claim is unchanged from round 1 and covers API-mode sessions only, as the pull request states.
- **Deadlock.** The ownership lock is taken in `TakeWheelAsync`, `HandBackWheelAsync`, `StopTurnAsync` (released before
  Pi's answer), `GracefulShutdownAsync` (released before the host's shutdown) and `HandleTerminalExitAsync` (a
  fire-and-forget task, so the terminal's exit thread never blocks on it, and the fake terminal's synchronous exit inside
  `GracefulShutdownAsync` cannot either). The host's lock is never held while the ownership lock is awaited, and the
  ownership lock is held across host calls that take the host's lock, which is the one safe order. Every wait under the
  ownership lock is now bounded on the injected clock (the stop and the transport's grace) or by the terminal host's own
  timeout. The `beforeResume` callback runs the session's activity handlers under the ownership lock; the production
  handlers push a fire-and-forget delta and read the holder without a lock, so none can wait on it. The one sync wait I
  found is in a test's own handler (`...PiNamesItsConversationBeforeTheHandBackFinishes...`), under a hang guard, by design.

### (4) Rule 5a

No `Task.Delay`, `Thread.Sleep`, `Stopwatch`, `DateTime.Now`, real process or `TimeProvider.System` in any changed test
file (searched). The only waits on the real clock are hang guards (`ScriptedPi.WithinHangGuard`, the Gateway tests'
thirty-second `WaitAsync`, the Avalonia test's `HoldDelta`), each of which can only fail a test that hangs and never
decides a pass. `MinimalPiProcess` and `FakeTerminalBackend` spawn nothing; the creation tests write a file named `pi`
that is never run. The one `DateTime.UtcNow` in `DirectorKeysBeforeRosterTests.cs` is a key expiry in a pre-existing
helper, not this pull request's. Met.

### Findings

#### Round 2, finding 1. A take that had to kill Pi, then the user ending the session while they hold the wheel, reads as a crashed session with the take's words as the failure

**Where.** `src/CcDirector.Core/ApiMode/ApiModeSessionHost.cs`, `EndWhileSuspendedLocked` (the session's end is
`_lastConnectionEnded`, whose `ExitCode` is the killed process's); `src/CcDirector.Core/Sessions/Session.cs`,
`OnBackendProcessExited` (`crashed = IsUnexpectedExit(exitCode, wasWorking) || streamSaysFailed`, and
`IsUnexpectedExit` is `exitCode != 0 || wasWorking`).

**What the code does.** When a take's abort is not confirmed within the grace (finding 3), or Pi does not exit when its
input is closed, the transport kills it: exit code 137 on Linux and macOS, -1 on Windows (`ApiModeProcessEnded`'s own
documentation). That `ConnectionEnded`, with its exit code, becomes `_lastConnectionEnded`. The user then holds the wheel
for as long as they like. When the session is ended while they hold it, by the kill verb, the desktop or the Director's own
shutdown, `EndWhileSuspendedLocked` makes that record the session's end: the stream's verdict is written clean
(`_sessionEndedInFailure = false`), but the exit code travels with it to `Exited` and to `Session.OnBackendProcessExited`,
whose rule calls any non-zero exit code a crash. The session ends `Failed`, `Crashed` true, with the status reason
`failed: The user took the wheel: Pi's conversation is open in its terminal`.

**Proof.** A throwaway test on the pull request's own rig, the merge's test
`TakeWheelAsync_PiStoppedOnPurposeUnderAnUnconfirmedAbort_ThenTheSessionEnds_ReadsCleanNeverFailed` with two changes:
the fake Pi does not exit when its input closes (`exitsWhenInputCloses: false`, so the transport's kill is reached, as the
Developer's own description of finding 3 names it), and the session is ended with `Session.KillAsync` as the kill verb and
the Director's shutdown end it. The fake clock was advanced once for the abort's bound and once for the shutdown grace.
Result: `killsByTheTake=1 statusWhileHeld=Running exitCode=137 status=Failed crashed=True endedInFailure=False
endReason=The user took the wheel: Pi's conversation is open in its terminal lastStatusReason=failed: The user took the
wheel: Pi's conversation is open in its terminal`. The merge's own test passes only because its fake Pi exits with code 0
on input close, so the kill is never reached. The file was restored afterwards.

**The harm.** The brief's rule for the merge is that a take never reads as a failed or crashed end, and the Developer
wrote that the end while suspended records a clean end. On this path the session's final row is red, `Crashed` is true,
and the reason shown is the take's sentence presented as a failure, for a session the user ended on purpose, possibly an
hour after the take. The kill the exit code records belongs to the take, which the stream already reports as the user's
doing; the end belongs to the user. Everything that reads `Crashed` or `Failed` (the roster colour, the record of how the
session ended) is told something false.

**Why it must change.** The session's end while suspended is the Director's own, on purpose, so its exit code must not be
the take's. Either `EndWhileSuspendedLocked` records the end with exit code 0 (the killed process's own code is already
published in its `ConnectionEnded` and in the log, so nothing is lost), or `Session.OnBackendProcessExited` rules an
API-mode end by the stream's verdict alone, which is the design the silent end (pull request #4129) wrote into that method's
own comment ("Pi exits 0 whenever its input closes ... the stream says how its agent ended"); the second is wider and is
the Developer's and the Tech Lead's to weigh. Pin it with the merge's test run with a Pi that does not exit on input close,
so the kill is reached, and the session ended through `KillAsync`: `Exited`, not `Failed`, `Crashed` false. The same test
would also do for the reason: while the merge's test asserts the reason does not contain "failed", a clean end's reason is
not shown at all, so that assertion passes on the wording, not the verdict.

### Notes, not findings

- **The end reason while suspended says the terminal is open.** `EndWhileSuspendedLocked` keeps the take's reason, "The
  user took the wheel: Pi's conversation is open in its terminal", as the session's `EndReason`, at the moment the terminal
  has just been closed by the same shutdown. The queue refusals on the same path append "; the session ended before the
  wheel was handed back"; the end reason could carry the same suffix. Wording; today a clean end's reason is not displayed.
- **No test pins a hand back whose Pi cannot be started.** The path reads truly (question 2) and I measured it with a
  throwaway, which the gate does not have. The rig makes it a one-line arrangement: a `StartSession` with one Pi makes the
  hand back's start throw. Law 17.
- **The no-holder Stop sentence** ("its terminal exited") is also answered when the holder is `None` because a hand back
  could not start Pi (measured). True in spirit, wrong in the detail. Wording.
- **A hand back right after a take under an unconfirmed abort is refused as mid-turn for up to the quiet threshold.**
  On that path `TurnEnded` and `ConnectionEnded` arrive in one batch, so `OnApiModeEvents` leaves the activity at
  `Working`; `OnApiModeTerminalChanged` sets nothing on a take; the detector moves it to waiting only after the terminal
  has been quiet for ten seconds. Until then `terminalMidTurn()` is true and the hand back is refused with
  `TerminalMidTurnReason`. The refusal is safe and self-healing, and the kill path is not affected (the terminal's end sets
  waiting for a prompt before the host's end is read). Bounded by the threshold; not a finding.
- **A take whose terminal cannot start and whose Pi then cannot be started back** throws out of `TakeWheelAsync` instead
  of returning a refusal, leaving the holder `None` with the host suspended: a coherent state the next take or hand back
  recovers from, and the Gateway sees an error rather than a sentence. The first round's similar note stands.
- **The loop failure's `failure = true` is dead while suspended.** `OnProcessEndedLocked` returns before
  `_sessionEndedInFailure` is written on a suspended end, and `EndWhileSuspendedLocked` later writes it false, so the
  Developer's sentence "loop failure still forces it" is true only while the protocol holds the wheel, which is where it
  was already true on main. The published `ConnectionEnded` on that double fault says only that the user took the wheel;
  the loop failure is in the log and the defects. A Director defect coinciding with a take; the label is defensible either
  way.
- **A hand back landing in the instant between a shutdown's release of the ownership lock and the host's shutdown**
  starts a Pi that the shutdown then ends (question 2). One wasted process start; never two at once.
- **The transient `None` pushed during a hand back.** `EndTerminalLocked` sets the holder to `None` and `beforeResume`
  then sets Starting, whose activity push carries a row with no holder for the milliseconds until `ResumeAsync` sets
  `Protocol` and the awaited push carries it. Both go up the same connection in order. Harmless.

### What the next seat need not re-check

- The seven answers' code is the code their tests exercise; six close their harm and one is declined with its reason.
- The merge chose the right side in all five conflicted files; the one defect is in what the resolution left unsaid (the
  exit code), not in a chosen line.
- The ownership lock's every wait is bounded, and the two-lock order is the safe one.
