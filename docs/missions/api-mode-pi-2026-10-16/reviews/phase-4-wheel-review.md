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
