# Phase 4 review: take the wheel and hand back, the Director side (pull request #4094)

Mission: API mode for agent wrappers, proven on Pi (issue #4070), phase 4, the Director's half of the
wheel. Reviewed on 16 October 2026 against commit 2ad9a2858, the head of pull request #4094 (branch
`apimode/p4-wheel`), in a detached checkout of that commit, diffed against origin/main at 15ca77338
(`git diff origin/main...HEAD`: 21 files, 1,589 lines added, 55 removed). Reviewer seat, opened by the
Tech Lead (session 107), running a different agent family from the Developer. Reading only; nothing
here was built or fixed by the reviewer. This review branch is based on the pull request head.

## Verdict

**The seam is the approved one and the common paths hold: never two processes on the take, the hand
back and the turn-start restart; the prompts that waited go out in order with their receipts; the
reply waits for the Gateway's push. Five findings, three of them proven by a test written, run and
deleted. Finding 1 is the one that breaks the mission's first rule (two processes on one
conversation) on a reachable path, and it must change. Findings 2 and 3 are the Tech Lead's own
conditions 1 and 2 not fully met. Findings 4 and 5 are smaller and the Developer may decline them
with a reason.** The Developer decides what happens to each.

## Scope

**What I read, in full, on the pull request head:**

- `src/CcDirector.Core/ApiMode/ApiModeSessionBackend.cs` (the whole file, twice: once for the
  contract, once for every interleaving of take, hand back, the terminal exiting on its own, shutdown,
  dispose and an exception in the middle of a switch), `ApiMode/ApiModeSessionHost.cs` (the diff, then
  `SuspendAsync`, `ResumeAsync`, `StopAsync`, `ShutdownAsync`, `DisposeAsync`, `EndWhileSuspendedLocked`,
  `OnProcessEndedLocked`, `TakeEventsLocked`, `WriteQueuedPromptsLocked`, `ReconcileTurnStartTimerLocked`,
  `OnTurnStartOverdueAsync`, `StartConnection`, `PumpAsync`, `ObserveAsync` and the `Connection` class),
  `ApiMode/SessionWheel.cs` (the holder, the result and `ApiModeConversationClaims`),
  `ApiMode/SessionConnectionModeWords.cs`.
- The changes to `Sessions/Session.cs` and the code they lean on: the constructor, `InitializeScreen`,
  `SnapshotScreenRowsWithCursor`, `OnApiModeEvents`, `OnApiModeTerminalChanged`, `TakeWheelAsync`,
  `HandBackWheelAsync`, `SetActivityState`, `OnBackendProcessExited`, `KillAsync`, `Dispose`, and the
  places that clear `IsBrandNew`. The change to `Sessions/SessionManager.cs` and the `CreateSession`
  backend choice around it; `KillSessionAsync`, `RemoveSession`, `KillAllSessionsAsync`, `Dispose`.
- `Wingman/TerminalStateDetector.cs`: the diff and the `Watcher` constructor; and every other reader of
  a session's buffer outside tests (`TerminalSessionRecorder`, `TransientErrorAutoResume`,
  `SessionLogWriter`, `SessionStatusWingman`, `TurnSummaryCache`, `TurnReviewLogger`,
  `ActivityEventProducer`, `SessionHistoryReader`, `TerminalSubmit`, `DirectorStreamProducers`,
  `SessionReadExecutor`, `WingmanContextBuilder`, `ChatService`, `ControlEndpoints`, the two terminal
  controls and the input guards), to answer condition 2.
- `ControlApi/SessionCommandExecutor.cs` (`WheelAsync`, `WheelCoreAsync`, the dispatch),
  `SessionWriteExecutor.cs`, `ControlApiHost.cs` (the new `PushSessionRowAsync` and the whole block
  that subscribes a session's events to `NotifyDelta`), `GatewayStreamClient.cs`
  (`PushDeltaAndWaitAsync`, `NotifyDelta`, the re-push interval), `ControlEndpoints.cs`.
- On origin/main: `src/CcDirector.Gateway/Api/GatewayEndpoints.ApiModeVerbs.cs` in full (the
  contract on the Director in its doc comment), `Streaming/DirectorHub.cs` `PushDelta`,
  `Core/UnixPty/UnixProcessHost.cs` and `Core/ConPty/ProcessHost.cs` (`GracefulShutdownAsync`, the
  exit monitor, `Dispose`), `Backends/UnixPtyBackend.cs` and `ConPtyBackend.cs`, `Pi/PiDialect.cs`
  (`OnProcessExited`, the abort's reason), `Utilities/CommandLineLauncher.cs`.
- The tests: `ApiModeWheelTests.cs` (all sixteen), `FakeTerminalBackend.cs`, the changes to
  `FakeApiModeProcess.cs`, `ApiModeSessionCreationTests.cs`, `ApiModeSessionTests.cs`,
  `SessionConnectionModeWordsTests.cs`, `ApiModeConnectionThroughTheDirectorTests.cs`,
  `MinimalPiProcess.cs`, `DirectorKeysBeforeRosterTests.cs`; and `ScriptedPi`, `RecordingSink`, the
  recordings' names.
- The documents: issue #4070 (sections 2, 3, 7), `event-model.md` in full, the mission README,
  `phase-1-integration-review.md`, `phase-1-phase-review.md`, `phase-2-wheel-verb-review.md`, and the
  pull request's description.

**What I ran, on devlinux (Linux, .NET 10 at `$HOME/.dotnet`):**

- `dotnet test src/CcDirector.Core.UnitTests -p:EnableWindowsTargeting=true --filter FullyQualifiedName~ApiMode`:
  238 passed, 0 failed, 0 skipped, 1 second. This is 16 more than the phase review's count on main, the
  sixteen `ApiModeWheelTests`, plus one in `ApiModeSessionCreationTests` already counted under `ApiMode`.
- `dotnet test src/CcDirector.Gateway.UnitTests --filter FullyQualifiedName~ApiModeConnectionThroughTheDirectorTests`
  14 passed, 0 failed; `dotnet test src/CcDirector.Avalonia.Tests --filter FullyQualifiedName~DirectorKeysBeforeRosterTests`:
  17 passed, 0 failed. Both include the new awaited-push tests.
- Four throwaway tests, written in this checkout against the fakes the pull request itself uses
  (`ScriptedPi` on the real recordings, `FakeTerminalBackend`, and one stand-in terminal that does not
  exit when shut down), run, and deleted before this file was committed. No real Pi, process, port,
  sleep or clock. Each is described under the finding it proves. One of them (the kill while the user
  holds the wheel) found no defect and is reported under "Weighed and not raised".

**What I could not reach:**

- No real Pi, no real Director, no Gateway: the recordings and the fakes stand in for Pi and the
  terminal. The end-to-end proof script is phase 6.
- No Windows machine. `scripts\test-local.ps1` and the `-Parked` gate were not run here; the pull
  request says the Windows gate is the Tech Lead's. Two things are therefore read, not seen: the ConPty
  process host's `KillProcessTree` on a shutdown that times out (finding 1 applies to it by reading),
  and `CommandLineLauncher.Build` wrapping `pi.cmd` for the user's terminal.
- The Terminal tab buttons and anything on a client: not in this pull request, by its own statement.

## The brief's questions, and what the code does

An absence below means looked at and found as the design and the brief say, not skipped.

**(a) Never two processes, never a stranded one.** One `SemaphoreSlim` per backend (`_ownership`) is
held across the whole of `TakeWheelAsync`, `HandBackWheelAsync`, `GracefulShutdownAsync` and the handling
of the terminal exiting on its own, so two takes, a take and a hand back, or a kill during a switch are
serialised, and the second take reads `User` and is refused (`TakeWheelAsync_Twice_...`, and the Director
side verb test). The take suspends the host before the terminal factory is called, and `SuspendAsync`
returns only after the process's end was handled under the host's lock (`Connection.EndHandled`); the
`TakeWheelAsync_PiSlowToExit_...` test holds Pi alive after its input closes and proves no terminal is
made until it exits. The hand back ends the terminal and waits for `HasExited` before `ResumeAsync`
(finding 1 is the one branch where that wait is not a wait). The turn-start restart cannot start Pi
beside a terminal: `_suspended` is set in the first locked step of `SuspendAsync`, before the stop, and
`OnTurnStartOverdueAsync`, `OnProcessEndedLocked` (`restart` is false when suspended),
`WriteQueuedPromptsLocked`, `TrySendPromptNowAsync` and `SendPromptAsync` all read it; the
`TakeWheelAsync_WhilePiOwesATurn_...` test advances the fake clock past twice the bound and sees no
restart. Pi exiting on its own while the protocol holds the conversation ends the session, and a later
take is refused ("has ended"). The terminal exiting on its own leaves holder `None` and the queue intact
(two tests). The process-wide claim refuses a second API-mode session on the same folder and
conversation and is released at the session's end and on dispose (`Start_ASecondApiModeSession...`).
An exception out of `SuspendAsync` or the terminal factory leaves the lock released (the `finally`), the
holder at `Protocol` or `None`, and no second process. **The one path that breaks this is finding 1.**

**(b) Terminal mode unchanged.** Read line by line in the three files the brief names:

- `TerminalStateDetector.cs`: `Wire` gains one test, `session.ConnectionMode == Api` (line 303), which is
  `_apiMode is null ? Terminal : Api` on the session, so false for every terminal session, which then
  falls through to the unchanged code. `OnSessionRemoved` gains a `TryRemove` on `_apiModeSessions`,
  empty for a Director with no API-mode session. `Dispose` gains a `foreach` over that same dictionary.
  Three new statements run for a terminal session; none changes its behaviour. `FollowApiModeSession` and
  its `Rewire` run only for an API-mode session.
- `Session.cs`: the new subscription in the constructor is inside the existing `if (_apiMode is not null)`;
  `WheelHolder` reads `_apiMode?.WheelHolder ?? None`; `OnTerminalChanged` is raised only from
  `OnApiModeTerminalChanged`, which only the API-mode backend's event reaches; `TakeWheelAsync` and
  `HandBackWheelAsync` return at `_apiMode is null`; the two `IsBrandNew = false` lines are in the two
  API-mode send methods; the new lines in `Dispose` are inside `if (_apiMode is not null)`. A terminal
  session runs no new statement in this file.
- `SessionManager.cs`: a new property with a default, and the factory argument inside the API-mode arm of
  the backend ternary. A terminal session runs no new statement.
- Outside the three: `ControlEndpoints.Map` now calls `WheelHolderWord(s.WheelHolder)` for every row,
  terminal rows included, and answers `none` for them exactly as `WheelHolderOf(Terminal, …)` did
  (pinned by `SessionConnectionModeWordsTests`). `SessionWriteExecutor` lists two more verb names.
  `ControlApiHost` builds one more delegate per dispatched command.

**(c) The queue.** A send while suspended queues with `PromptQueuedReason.UserHasTheWheel`, is never typed
into the terminal, and goes out after the new process's `ConnectionReady` (the host writes queued prompts
only when `connection.CanWritePrompt`, which needs `Ready`), each with its own `PromptDelivered`
(`SendTextAsync_WhileTheUserHoldsTheWheel_...`). The terminal exiting on its own keeps the queue
(`...ThenTheTerminalExitsOnItsOwn_...`). The session ending while the user holds the wheel refuses every
queued prompt with the reason and the sender gets that receipt: proven by my throwaway test, below. The
test for order with more than one queued prompt is the existing host test from phase 1; this pull
request queues one. **Not pinned by a test in this pull request:** the kill while held (works), and a
hand back whose Pi cannot be started again (the code keeps the queue and refuses with the reason).

**(d) The push contract.** `WheelCoreAsync` awaits `pushSessionRow`, which is
`GatewayStreamClient.PushDeltaAndWaitAsync`, which awaits `hub.PushDeltaAsync` (the invoke, not
`SendAsync`), and only then returns `Success`. The hub test holds the delta and proves the client does not
return before it; the Director side test holds the push and proves no reply goes out before it. With no
push path (`PushSessionRowAsync` null) the verb refuses before switching. With the push throwing, the
switch stands and the reply is a Conflict that says so in plain words; the Gateway's next re-push (every
10 seconds, half the 20-second stale window) carries the holder. Honest, and the only sound answer
once the switch is made. Deltas from other triggers before the awaited push carry lower sequences and
the old holder; they travel the same ordered connection ahead of it. **A gap of the same shape as the
push contract exists on one path nobody pushes: finding 4.**

**(e) Events and the holder at each step.** Take: `ConnectionEnded` with
`UserTookTheWheelReason`, nothing from Pi while held, holder `User`. Hand back: `ConnectionReady`, then the
receipts, holder `Protocol`. One generation and a sequence rising by one across the switch
(`...TheEventStreamKeepsItsGenerationAndKeepsCounting`). Own exit: holder `None`, prompts stay queued, the
next take opens the terminal without starting Pi, the next hand back starts Pi with nothing to stop
(both tested). The one thing the stream shows that the brief did not ask for is finding 2.

**(f) Tests.** Fakes only: `ScriptedPi` on the recordings, `FakeTerminalBackend`, `FakeTimeProvider`,
`MinimalPiProcess`; the 30-second hang guards only fail a test. Names follow
`Method_Scenario_Result`. The temporary folder each test creates is a path, not a machine condition.

**(g) The rules.** Logging on entry, exit and failure throughout. The three `catch` blocks that are not
at entry points (`TakeWheelAsync` around the terminal's start, `HandBackWheelAsync` around Pi's restart,
`WheelCoreAsync` around the push) each report the failure in the result rather than hide it, which is
the opposite of a fallback. Plain English in the code and the comments; the one short form is the
owner's own name for the mode.

## Findings

### 1. A hand back whose terminal does not exit in time detaches it anyway, and the next hand back starts Pi beside the still-running terminal

**Where.** `src/CcDirector.Core/ApiMode/ApiModeSessionBackend.cs`, `EndTerminalLocked` (lines 378 to 389)
and its caller `HandBackWheelAsync` (lines 275 to 281).

**What happens.** `EndTerminalLocked` sets `_terminal = null` and `_wheelHolder = None` **before** it
calls `terminal.GracefulShutdownAsync`, then throws if `terminal.HasExited` is false. The throw is
reachable: `UnixProcessHost.GracefulShutdownAsync` (`src/CcDirector.Core/UnixPty/UnixProcessHost.cs`,
lines 248 to 265) writes Ctrl+C, sends SIGTERM, waits up to the timeout, and when the timeout wins sends
SIGKILL and **returns without waiting for the exit**; `UnixPtyBackend.HasExited` (line 38) becomes true
only when the exit monitor thread has reaped the process. `ConPty/ProcessHost.GracefulShutdownAsync`
(lines 331 to 347) does the same with `KillProcessTree`. So on the timeout branch the backend throws with
the terminal already detached: `_terminal` is null, the holder is `None`, the terminal object is never
disposed (its `ProcessExited` handler finds `ReferenceEquals(_terminal, terminal)` false and returns), no
`TerminalChanged` is raised (the `detached` flag is set only after the await), so the session's screen
stays fed from a buffer nothing owns, and the exception leaves `Session.HandBackWheelAsync` and
`WheelCoreAsync` as an exception rather than a refusal. The next hand back finds `_terminal` null and
calls `host.ResumeAsync`: Pi starts over the protocol while the terminal may still be alive.

**Proof.** A throwaway test on the pull request's own rig with a terminal stand-in whose
`GracefulShutdownAsync` returns without exiting (what the real host does on its timeout branch, seen from
the backend): take, then hand back, then hand back again. The first hand back threw
`InvalidOperationException: The terminal of session … did not exit, so Pi was not started over the
protocol: two processes may never hold one conversation.`; afterwards the holder was `None`,
`Backend.Terminal` was null, the terminal had not exited and was not disposed. The second hand back
returned **Done**, a second Pi process had been started (`PisStarted = 2`), the holder was `Protocol`,
and the terminal was still running.

**The harm.** This is the mission's one hard rule (issue #4070, section 5: "Exactly one process holds a
conversation at any moment, and the Director enforces it"), broken by the Director itself on the path
its own exception message says it guards. A Pi that does not die within five seconds of Ctrl+C and
SIGTERM is not exotic: a terminal Pi that has a tool's child process running, or one that installed its
own signal handler, is enough. It also contradicts the Tech Lead's condition 3 in substance: a hand back
that did not complete leaves the terminal detached from the lock's view of the world.

**Why it must change.** Detach only once the exit is confirmed. Either wait for the exit after the kill
(the backend already waits for a slow Pi's exit on the take, in `SuspendAsync`), or, when the terminal
is still alive after the grace, keep `_terminal` and the holder `User` and return a refusal that says the
terminal would not exit, so the next hand back tries again against the same terminal. Dispose the
terminal object on every exit path. Pin it with a test: a terminal that will not exit, a hand back, and
an assertion that `PisStarted` stays at one and the holder stays `User`.

Developer's answer:

### 2. A take during a running turn ends the turn with the reason "stopped by the user", not that the user took the wheel, and no test pins the take-during-a-turn case at all

**Where.** `src/CcDirector.Core/ApiMode/ApiModeSessionHost.cs`, `SuspendAsync` (the stop at lines 360
to 365 is `StopAsync`, whose `TurnEnded` carries the dialect's abort reason, `PiDialect.StoppedByUserReason`
= "stopped by the user", `Pi/PiDialect.cs` lines 46 and 685), and `OnProcessEndedLocked` (lines 645 to
647), which rewrites only the `ConnectionEnded` reason to `UserTookTheWheelReason`.

**Proof.** A throwaway test on the `abort` recording: a prompt, wait for the first thought delta, then
`TakeWheelAsync`. The stream was `ConnectionReady, PromptDelivered, TurnStarted, MessageText, MessageText,
TurnEnded, ConnectionEnded`; the `TurnEnded` was `Cancelled` with reason "stopped by the user"; the
`ConnectionEnded` reason was "The user took the wheel: Pi's conversation is open in its terminal". The
order is right. The reason is not the one the Tech Lead's condition 1 asked for.

**The harm.** The event model's rule 5 says a prompt's receipt shows its turn's end beside Delivered. So
a prompt whose turn was cut because the user took the wheel is shown, in the Chat and in the stored
receipt, exactly as a prompt the user stopped with the Stop button. The record cannot tell a Stop from a
take, which is what the condition exists to prevent. And none of the sixteen wheel tests has a turn
running at the take: the two "owes a turn" tests are acknowledged prompts with no `agent_start`, so the
order `TurnEnded` before `ConnectionEnded` under a take is pinned by nothing in the gate.

**Why it must change.** The Tech Lead made it a condition of the seam. The `abort` recording already
provokes the case, as my test shows, so the pin costs one test. Carrying the take's reason onto that
`TurnEnded` needs either a reason the host passes to the dialect's abort, or the same rewrite the
`ConnectionEnded` gets, applied to the `TurnEnded` the stop produced during a suspend.

Developer's answer:

### 3. After a hand back the session can be left "Starting" for good: the terminal-changed handler and Pi's ConnectionReady race for the activity state

**Where.** `src/CcDirector.Core/ApiMode/ApiModeSessionBackend.cs`, `HandBackWheelAsync`: `TerminalChanged`
is raised in the `finally` (lines 298 to 303), after `host.ResumeAsync` has returned and the lock is
released. `ApiModeSessionHost.ResumeAsync` (lines 390 to 410) returns as soon as the process is started;
the pump, the dialect and the observer loop then publish `ConnectionReady` on their own threads.
`Session.OnApiModeTerminalChanged` (line 4822) sets `Starting` when the holder is `Protocol`;
`Session.OnApiModeEvents` (lines 4846 to 4849) sets `WaitingForInput` on `ConnectionReady`. Nothing orders
the two.

**Proof.** A throwaway test ran take then hand back forty times on the pull request's rig, waited for
the second `ConnectionReady` and one yield, and read `ActivityState`: `WaitingForInput` 35 times,
`Starting` 5 times. In the five, `ConnectionReady` had already set `WaitingForInput` and the
terminal-changed handler then set `Starting` on top. Nothing sets the state again until the next turn.

**The harm.** The row the awaited push carries (`WheelCoreAsync` maps the session at line 935, after the
switch) and every delta until the next `TurnStarted` says the session is starting when it is idle and
waiting for a prompt. The Gateway folds the roster from that state, and the Director's own
`OnActivityChanged` edge work (`ControlApiHost`, lines 1822 onwards: the doorbell state, the turn push
trigger, the delta) all read it. The guarded doorbell send still works, because it consults the host's
gate rather than the state, which is why the symptom is a wrong colour and not a lost prompt. Condition 2
asked that every-session machinery cope with the switch; a state machine driven from two unordered
sources does not.

**Why it must change.** The order is the backend's to decide. Setting the session's state for the hand
back under the ownership lock before `ResumeAsync` is called (raise `TerminalChanged` first, then resume),
or leaving the state alone on the hand back and letting `ConnectionReady` set it, removes the race; with
a fake Pi the test above becomes deterministic.

Developer's answer:

### 4. The holder becoming "none" when the user's terminal exits on its own is pushed to the Gateway by nothing

**Where.** `src/CcDirector.Core/ApiMode/ApiModeSessionBackend.cs`, `HandleTerminalExitAsync` (lines 403 to
432): sets `_wheelHolder = None`, sets a status, raises `TerminalChanged`. `Session.OnApiModeTerminalChanged`
then sets `WaitingForInput` and raises `OnTerminalChanged`. `src/CcDirector.ControlApi/ControlApiHost.cs`,
lines 1822 to 1905: the session events that push a delta are the activity change, the hold, the three
colour inputs, the Wingman gate, the uncommitted count and the controller. `OnTerminalChanged` is not
among them, and `WheelHolder` has no change event of its own. The same gap is on the hand back whose
Pi could not be started (`HandBackWheelAsync`, lines 287 to 293: holder `None`, a refusal returned,
`WheelCoreAsync` pushes nothing on a refusal).

**What happens.** The usual way a terminal exits on its own is the user quitting Pi at an idle prompt.
By then the detector has set `WaitingForInput`, so `SetActivityState(WaitingForInput)` is not a change,
no activity delta goes up, and the Gateway keeps `user` until the next full re-push: up to 10 seconds
(`GatewayStreamClient`, half of `GatewayConfig.DefaultStreamStaleAfterSeconds` = 20).

**The harm.** For that window the Gateway rules on a holder that is false: a take is refused with "You
already have the wheel of this session: its terminal is open on the conversation" when the terminal is
gone, and a client shows "Hand back" for a session with no terminal. Bounded and self-healing, so this
is the smallest of the five. It is raised because the repository's own rule, written in the very block
of `ControlApiHost` that would hold the fix, is "if the fold READS it, it pushes", and the Gateway's wheel
verb reads `WheelHolder` to refuse.

**What would close it.** One more line of the same shape as the eight above it: `session.OnTerminalChanged`
pushing `ControlEndpoints.Map(session, DirectorId)`. The awaited push stays the contract for the two
verbs; this is only for the change the user's terminal makes by itself.

Developer's answer:

### 5. Condition 2 is tested for the screen and the detector only; the recorder, the session log and the Wingman's prompt watcher never see the wheel's terminal

**Where.** `src/CcDirector.Core/Wingman/TerminalSessionRecorder.cs` line 184, `Storage/SessionLogWriter.cs`
line 90, `Wingman/SessionStatusWingman.cs` line 179: each reads `session.Buffer` once, when the session is
created, and does nothing when it is null. An API-mode session's buffer is null at creation, so none of
the three attaches, and none re-reads when the terminal appears. (`TransientErrorAutoResume` watches the `AgentKind.ClaudeCode`
agent only, line 97, so nothing changes for Pi.)

**What happens.** None of them crashes: they skip. The pull request's tests across a take and a hand
back cover the session's screen (`...TheSessionsScreenIsTheTerminalsOnlyWhileTheUserHoldsIt`) and the
detector (`HandBackWheelAsync_PiWorkingInTheTerminalAsTheDetectorReadsIt_IsRefused`). No test runs the
recorder, the session log writer or the Wingman's watcher across a take, which is what the Tech Lead's
condition named.

**The harm.** A terminal-mode Pi session's screen is written to the session log (the Director's own
record of what the user saw) and, when capture is on, to the recording corpus the Wingman is tuned
from. The same Pi screen, under the wheel, is written to neither: the minutes the user spent typing into
Pi's terminal leave no record on the Director. Low severity, and the Developer may rule that the wheel
period is out of those two records' scope; if so, the condition should say so, and a test should still
run those subscribers across a switch to prove they cope.

Developer's answer:

## Weighed and not raised

Each of these was looked at and judged not to prove a harm on a path the code can reach today, or to be
outside this pull request. They are here so the next seat knows they were seen.

- **The kill while the user holds the wheel works.** My throwaway test: take, a prompt (queued with
  `UserHasTheWheel`), `KillAsync`. The prompt's send returned not confirmed with the reason "Pi exited
  before the prompt could be sent: The user took the wheel …; the session ended before the wheel was
  handed back"; the stream showed `PromptRefused` with the same words; the terminal had exited; the
  session ended `Exited`, exit code 0, holder `None`. Right, and not pinned by a test in the pull
  request; the Developer may want it in the gate since it is the one path through
  `EndWhileSuspendedLocked`.
- **A take in the instant between Pi's last batch and the session learning of the exit** (`_sessionEnded`
  set in the host, `_hasExited` not yet set on the backend): `SuspendAsync` throws
  `InvalidOperationException` rather than the backend refusing, and the dispatch has no catch, so the
  Gateway gets an error rather than "has ended". The window is the observer loop's hand-off; nothing is
  stranded and the lock is released.
- **Disposing the backend while the user holds the wheel, without a shutdown first:** `host.DisposeAsync`
  waits on the observer loop, which ends only when `_observed` is completed, which only `ShutdownAsync`
  does while suspended; so the fire-and-forget `DisposeHostAsync` never completes. The only path that
  disposes without a prior kill is the Director's own exit (`SessionManager.Dispose`), where it does not
  matter; the stop verb kills first.
- **Bytes Pi's terminal writes between `terminal.Start` and the session's parser attaching** are in the
  terminal's buffer but never parsed (`InitializeScreen` makes a fresh parser and does not replay the
  buffer; the desktop control replays, the session does not). The window is the microseconds between
  line 231 and line 252 of the backend, before a child process can have written anything; noted because
  a later change that does work between the two would open it.
- **The `ConnectionEnded` reason when Pi crashes during a take:** `OnProcessEndedLocked` rewrites it to
  `UserTookTheWheelReason` because `_suspended` is already set; the exit code is kept, and the terminal
  opens as the user asked.
- **`PromptQueuedReason.UserHasTheWheel` on a prompt queued after the terminal exited on its own,** when
  nobody holds the wheel: wording only; the queue's behaviour is the one the brief's own-exit rule asks.
- **Delta ordering around the awaited push:** deltas from other triggers before the push carry lower
  sequence numbers and travel the same ordered connection; the hub applies them in order and the awaited
  push is the last and newest. The holder is set before each `SetStatus`, so no delta carries a holder
  older than its own moment.
- **The brand-new suppression of the detector while the user holds a fresh session's wheel:** a typed
  Enter clears `IsBrandNew` (`Session.cs` line 3288), the same way as in terminal mode, so the detector
  starts reading at the first thing the user submits.
- **`ApiModeConversationClaims` is process-wide and keyed by folder and conversation,** so the sixteen
  tests that all start the `plain` recording's conversation release it by disposing each session; my
  sampling test tripped over this until it disposed between runs. A reminder for whoever writes the next
  test, not a defect.
- **The known limit the pull request states** (the bound's timer firing exactly inside the take's wait for
  Pi's exit cannot be made deterministic on one fake clock): read and agreed; the three guards are each
  covered on their own.
