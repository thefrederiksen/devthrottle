# Review: phase 1, the API-mode session (pull request #4088)

Mission: API mode for agent wrappers, proven on Pi (issue #4070). Reviewed on 16 October 2026 against
commit a65420d8e, the head of pull request #4088, in a detached checkout of that commit, diffed against
origin/main at d98620643 (`git diff origin/main...HEAD`, 36 files, 3,372 lines added, 7 removed).

## Scope

What I read:

- The whole diff. In the Director's core: `ApiMode/ApiModeSessionHost.cs` (all 796 lines, twice: once for
  the contract, once for the races between the pump, the queue, the timer and the process end),
  `ApiMode/ApiModeSessionBackend.cs`, `ApiMode/SessionEventStream.cs`, `ApiMode/SessionConnectionMode.cs`,
  `ApiMode/SessionConnectionModeWords.cs`, `ApiMode/ApiModeArguments.cs`, the changes to
  `Sessions/Session.cs`, `Sessions/SessionManager.cs`, `Sessions/SessionStateStore.cs`,
  `Sessions/TextSendOutcome.cs`, `Drivers/IAgentDriver.cs` and `Drivers/PiDriver.cs`. In the control
  layer: `SessionCommandExecutor.cs`, `SessionWriteExecutor.cs`, `ControlEndpoints.cs`,
  `Drain/DirectorRestore.cs`, `SmartRestart/DirectorWayUp.cs`. In the contracts: `NewSessionRequest.cs`,
  `WorkspaceCapture.cs`, `WorkspaceDtos.cs`. The five new Core test files and the one Gateway test file.
  The documents: `event-model.md` (the whole contract, with the three rows of the ruling table and the
  corrected environment paragraph), `preamble-proof.md` and its four files, `spend.md`,
  `docs/BackgroundWork.md` and `.gitattributes`.
- Beyond the diff, on the same commit, every place the pull request's claims depend on: the unchanged
  parts of `Session.SendTextAsync`, `SendTextOnlyWhenWaitingForInputAsync`, `CancelTurnAsync`,
  `DeliverPrePromptAsync`, `DeliverFirstPromptAsync`, `SubmitDoorbellLineAsync`, `KillAsync`,
  `SetActivityState`, `InitializeScreen` and `Dispose`; `SessionManager.CreateSession` from the launch
  spec to `backend.Start` and its failure path; `PiAgent.BuildLaunchSpec`; `PiDialect.OnTurnStartOverdue`,
  `EndNeverStartedTurn`, `CanWritePrompt` and `PromptsAwaitingTurn`; `SystemApiModeProcess`'s environment
  builder; `SessionCommandExecutor.SendPromptCoreAsync`, `FinishesWithinAsync`, `TooOldAnswer` and
  `StartPrePrompt`; `FleetDoorbellRinger`, `SessionDoorbellTarget` and `DoorbellSafety.Check`;
  `FirstPromptGate.CanProve`; `ComposerRetention.MayHoldText`.
- A hunt for every reader of a session's terminal buffer and every branch on the backend type outside
  `Session`, by search over the Director's core and control layer: `TerminalStateDetector`,
  `TerminalSessionRecorder`, `TransientErrorAutoResume`, `SessionStatusWingman`, `TurnReviewLogger`,
  `TurnSummaryCache`, `SessionLogWriter`, `SessionHistoryReader`, `TerminalSubmit`, `SessionAskRunner`,
  `SessionReadExecutor`, `ChatService`, `DirectorStreamProducers` and `FleetDoorbellRinger`. Each one
  either returns before touching a null buffer, or is reached only through the terminal typing path,
  which an API-mode session never enters. The one that misbehaves is the doorbell (finding 1).
- Issue #4070 (the body, including "Lessons from `StudioBackend`", and the Architect's first comment),
  the pull request description, and the six earlier reviews in this folder.
- The preamble proof's four files, swept for the user name, the company, the model provider's host and
  the scratch paths (no hit), and for skill descriptions (every one reads as redacted). The fleet
  preamble's own text is the one on main. The pull request's seven commit messages and its description,
  swept for assistant, model and vendor names and for attribution lines (no hit).

What I ran, on devlinux with the .NET 10 toolkit at `$HOME/.dotnet`, on the pull request head:

- `dotnet test src/CcDirector.Core.UnitTests -p:EnableWindowsTargeting=true --filter "FullyQualifiedName~ApiMode"`:
  210 passed, 0 failed, 0 skipped, 503 milliseconds. The suite's API-mode tests run on fakes only: the
  scripted Pi over `FakeApiModeProcess`, `FakeTimeProvider`, a recording sink; the one test that uses
  `SystemApiModeProcess` hands it a path under the temporary folder that cannot exist and fails before
  anything is spawned. Every wait is on an event the host published or a line it wrote, bounded by a
  30 second hang guard that never decides a pass.
- The whole `CcDirector.Core.UnitTests` suite: 2,087 passed, 5 failed, 23 skipped, 2,115 in all, 13 seconds. The five are the Windows drive-path tests that fail on Linux on main too (`CcWorktreesPoolSlotsTests` three times, `RepositorySourceIndexTests`, `WorktreePoolSettingsTests`); the numbers match the pull request description exactly.
- `dotnet test src/CcDirector.Gateway.UnitTests -p:EnableWindowsTargeting=true --filter "FullyQualifiedName~ApiModeConnectionThroughTheDirectorTests"`
  (the parked suite, one class): 9 passed, 0 failed, 0 skipped, 63 milliseconds.

What I could not reach:

- No real Pi, no real Director and no Windows machine. The preamble proof is read as recorded; I did not
  run it again. The Windows gate is the Tech Lead's.
- The Gateway's side of the events (phase 2's fold and relay) is not in this pull request; I read the
  sink's contract and the generation and sequence rules, not a consumer of them.
- `DeliverPrePromptAsync` (finding 2) waits on the machine clock with real sleeps, so I could not prove
  its behaviour with a test; the finding is from reading the code and the default in the request contract.

What I checked and found correct, so that an absence here means looked at, not skipped:

- Terminal mode is unchanged. `CreateSession` takes `connectionMode` with the default `Terminal`; the
  only new statements on the terminal path are the one `connectionMode == Api` test before the backend
  switch and the `backend is ApiModeSessionBackend` test before `CommandLineLauncher.Build`, both false
  for a terminal session, so the launch line, the environment and the backend are composed by the same
  code as on main. In `Session`, `_apiMode` is null for every terminal session, so the three branches
  (`SendTextAsync`, the guarded send, `CancelTurnAsync`) are one null test each and the constructor
  neither changes the starting state nor subscribes to anything. `IAgentDriver.ConnectionModes` is a
  default member; no driver file but Pi's changed. The restore and reopen pass `ConnectionMode` through
  as null for a terminal seat, which `FromWord` reads as terminal.
- The host against the contract: `get_state` first and no prompt before `ConnectionReady`; the queue
  holds a prompt while one is in flight (from the write to `agent_settled`) and before Pi named its
  conversation, with `PromptQueued` and the right reason and position; a queued prompt is written from
  `OnRecordLocked` after the step that settles the one before it, so `TurnEnded` precedes the next
  `PromptDelivered`; Delivered and Refused come only from the dialect's reading of Pi's response, or from
  the Director at the process end; Stop writes `abort` and completes only on Pi's response, carrying the
  `TurnEnded` that arrived meanwhile, and returns null at once when nothing runs or Pi is gone; an exit
  mid-turn ends the turn failed with the exit code and keeps Delivered; an exit before the acknowledgement
  refuses by the Director; a missing executable throws `ApiModeStartException` naming it, before
  `_connection` is set; a resume that found nothing stops Pi with the reason, refuses the queued prompt
  with that reason, and the session exits through the ordinary `ProcessExited` path; the turn-start bound
  is armed on the injected clock at the `started` acknowledgement, disarmed at `agent_start` and at the
  process end, and when it runs out the dialect's synthetic turn (started with the waiting prompts, ended
  failed with the restart stated) is followed by a restart on the same `--session-id`, folder and
  arguments, with the overdue prompt never written again and the queued one going to the new process.
- The races. Everything that touches a dialect, the queue, the stream or a connection runs under the
  host's one semaphore, including the write of a command and its report to the dialect, so a response
  cannot reach the dialect before the command it answers. A restart replaces `_connection` inside the
  same locked section that observed the end, so no send can see an ended connection with no session end.
  The old process's pump compares its connection to `_connection` under the lock before completing the
  observer channel, so a restart never ends the session. A timer that fires while the turn begins finds
  no waiting prompt under the lock and returns. The observer loop hands batches to `Published` outside
  the lock, so the session's activity handler can never wait on the lock it was called under.
- Generation and sequence: one `SessionEventStream` per `CreateSession`, a new GUID per stream, ordered
  by the injected clock's start; the numbering is shared by every Pi process of the session, and the
  test over two processes proves one generation and a sequence rising by one across both.
- Pi's environment: inherited `CC_` variables removed and the session's stamped set applied after, in
  `SystemApiModeProcess`'s builder; the same executable, folder, `--session-id` and preamble file as the
  terminal path, with `--mode rpc` ahead, proven on a real `SessionManager` create with a scripted Pi and
  recorded once on a real Pi in the preamble proof.
- Every-session machinery with no buffer: the state detector, the recorder and the auto-resume watcher
  return before building a watcher; the Wingman, the turn review log, the session log, the turn summary
  cache, the chat service, the buffer read verb and the live stream producer each test for null and
  answer empty; every screen read on `Session` answers empty. `SessionAskRunner` and `TerminalSubmit`
  are reached only through keystroke paths, which the backend refuses in words.
- The session row: `ConnectionMode` always a word, `WheelHolder` `protocol` while an API-mode session
  runs and `none` once exited or failed, and for a terminal session; the capture keeps the mode only when
  it is `api`, so a terminal seat's record is unchanged.
- `turn-stop`: registered on the write area under the name the Gateway sends, refused with Conflict and
  a sentence pointing at interrupt for a terminal session, Pi's abort confirmed for an API-mode one.
- Tests are named `MethodName_Scenario_ExpectedResult`, use Arrange-Act-Assert, and each claim in the
  pull request description has a test behind it except the two in the findings below, which the tests
  could not have reached.
- The pull request's own gate claim (2,087 passed, 5 failed, 23 skipped, the five being the Windows
  drive-path tests) is checked against my own run above.

## Verdict

Two findings, both on paths that run for every session and that the pull request's own hunt did not
reach. Neither touches a terminal session. Neither is inside the host, whose contract, queue, failure
cases, bound and restart I found correct and proven. The first needs an answer before this is called
done, because it makes fleet messaging to an API-mode session silently dead; the second is a thirty
second delay on every first prompt that the host's own queue makes unnecessary. The Developer decides
what happens to each.

## Findings

### 1. The fleet doorbell never rings an API-mode session, and the test that says the doorbell uses the guarded send is wrong

**Where.** `src/CcDirector.Core/Sessions/FleetDoorbellRinger.cs`, `SessionDoorbellTarget.HasTerminalGrid`
(line 62: `_session.BackendType is SessionBackendType.ConPty`) and `GatherFactsAsync` (lines 152 to 183);
`src/CcDirector.Core/Drivers/DoorbellSafety.cs`, `Check` (lines 195 to 203). The one caller of
`Session.SendTextOnlyWhenWaitingForInputAsync` on this commit is the prompt verb's only-when-waiting
mode (`src/CcDirector.ControlApi/SessionCommandExecutor.cs`, line 484). The doorbell goes through
`FleetDoorbellExecutor` (line 48) to `FleetDoorbellRinger.RingAsync` and never through the guarded send.

**What happens.** An API-mode session is created with `SessionBackendType.ConPty` (the manager refuses any
other backend type for API mode), so `HasTerminalGrid` answers true for it. `GatherFactsAsync` then takes
one frame, which is empty because `SnapshotLiveFrame` has no screen to read, and sets `readable` to
false. `DoorbellSafety.Check` sees no terminal grid and defers every ring with `screen-unreadable` and the
sentence "the session has no rendered terminal to check". Nothing is typed, nothing goes to Pi, and the
API-mode branch the pull request added to the guarded send is never reached by a doorbell.

**The harm.** A fleet message queued for an API-mode session is never announced. The fleet preamble tells
every session that "when the recipient is free, one doorbell line tells it to run `cc-devthrottle
message inbox`", and the preamble proof shows that text reaching Pi byte for byte. In API mode that line
never comes, so the session never reads its inbox and the message is lost in effect. The deferral reason
given to the Gateway, `screen-unreadable`, is a retry-later reason that describes a terminal that could
not be read, not a session that has no terminal, so nothing upstream can tell this case from a stuck
screen. The pull request lists what runs for every session and says the guarded send is "the guarded send
the fleet doorbell and the 'only when waiting' prompts use" (`ApiModeSessionCreationTests.cs`, line 185);
that is not what the code does, and the mistaken claim is why the hunt missed this path.

**Why it must change.** The mission's goal is that a prompt to a Pi session in API mode is "a message the
agent acknowledges"; the doorbell line is exactly such a prompt (one fixed line, agent-origin), and the
host's queue plus Pi's acknowledgement are a better safety check than two screen frames. Either route
the doorbell for an API-mode session through the host (the API-mode branch of the guarded send already
has the only-when-idle semantics the doorbell wants: nothing sent unless Pi can take it now), or refuse
the ring with a reason that says the session is in API mode and let phase 2 announce the inbox over the
protocol. Either way, add a test that rings an API-mode session, and correct the sentence in the creation
test.

Developer's answer: **Accepted, fixed.** The finding is right, and so is its diagnosis: my test comment claimed the
doorbell used the guarded send, and that wrong belief is why my hunt skipped `FleetDoorbellRinger`. The fix takes the
first of the two routes the finding offers. `FleetDoorbellRinger.RingAsync(Session, ...)` now sends an API-mode session to
a new `RingApiModeAsync`, which never reads a screen. The one doorbell line goes through the session's guarded send, whose
API-mode branch sends it to Pi only if Pi can take a prompt now (its conversation named, nothing in flight, nothing
queued). It is an agent-origin line with the same fleet-message provenance the terminal ring records. The answer is `rung`
only when Pi acknowledged the line. Otherwise it is `exited` for an ended session, or `working` for a busy or starting one
or a line Pi refused (Pi's words in the detail), and nothing is sent. The one-ring-at-a-time rule applies as for a
terminal. A terminal session's ring is unchanged. The sentence in `ApiModeSessionCreationTests` now says what the code
does: the guarded send is the prompt verb's "only when waiting" mode, and the API-mode doorbell rides on it.

Tests (`ApiModeSessionTests`, scripted Pi):
- `RingAsync_ApiModeSessionWaiting_SendsTheDoorbellLineToPiAndIsRung`: rung, and Pi was written exactly the doorbell line
  as a prompt and acknowledged it.
- `RingAsync_ApiModeSessionBusy_IsDeferredWorkingAndSendsNothing`: deferred, and nothing more written.
- `RingAsync_ApiModeSessionThatEnded_IsDeferredExited`.

With the new branch removed, the first two fail (the ring defers `screen-unreadable`, as the finding describes).

### 2. A spawned API-mode session's first prompt waits the whole pre-prompt wait, thirty seconds by default, before it is sent

**Where.** `src/CcDirector.Core/Sessions/Session.cs`, `DeliverPrePromptAsync` (the loop after the
`CanGateFirstPrompt` branch): `CanGateFirstPrompt` is `BackendType is ConPty && FirstPromptGate.CanProve(AgentKind)`,
and `CanProve` is true only for Claude Code and Codex, so a Pi session takes the older loop. That loop
reads `Buffer?.TotalBytesWritten ?? 0` every 750 milliseconds of real time and treats the agent as ready
only once more than 1,500 bytes have been written and the count has stopped moving. The default wait is
`NewSessionRequest.PrePromptWaitMs`, 30,000 (`src/CcDirector.Gateway.Contracts/NewSessionRequest.cs`,
line 119); the create verb starts it in `SessionCommandExecutor.StartPrePrompt` (line 1852) for every
request that carries a `PrePrompt`.

**What happens.** An API-mode session has no buffer, so the byte count is 0 on every poll and the "settled"
condition is never met. The loop runs to its deadline, the full wait, and only then calls
`SendTextAsync`, which takes the API-mode branch and hands the prompt to the host. The host would have
queued the prompt at once (`PromptQueued`, `agentStarting`) and written it the moment Pi named its
conversation, which on the preamble proof's wire log was 0.6 seconds after start.

**The harm.** Every `cc-devthrottle session spawn --connection api --prompt ...`, every schedule seed, and
every restore that re-seeds a prompt (the drain restore and the Smart Restart reopen build their request
with a `PrePrompt`) sends the first prompt thirty seconds late, for nothing. The mission's own proof
script (section 7, step 1: spawn Pi in API mode and send a prompt) pays it on every run, and the first
thing a user sees from API mode is a half-minute of "starting" on a prompt the agent could have taken in
under a second. The wait is on the machine clock with real sleeps, so no test in the gate can show it;
the pull request's `ActivityState` test shows the session leaves `Starting` as soon as Pi names its
conversation, which is the signal this loop should have used.

**Why it must change.** The host's queue is the readiness gate for API mode, and it already exists: for an
API-mode session `DeliverPrePromptAsync` can hand the text to `SendTextAsync` straight away (or wait for
`ActivityState` to leave `Starting`, which the session now drives from `ConnectionReady`), and the host
does the rest. One branch, next to the `CanGateFirstPrompt` one, and a test on the scripted Pi that the
first prompt is written right after `get_state`'s answer.

Developer's answer: **Accepted, fixed.** `DeliverPrePromptAsync` now has one branch ahead of the `CanGateFirstPrompt`
one: an API-mode session hands the first prompt to `SendTextAsync` at once. The host's queue holds it (`PromptQueued`,
`agentStarting`) and writes it the moment Pi names its conversation, so there is no wait for terminal output that never
comes. A resume that finds nothing still refuses it with the stated reason, through the same queue.

Test (`ApiModeSessionTests`, scripted Pi that answers only when told):
- `DeliverPrePromptAsync_ApiModeSession_IsQueuedAtOnceAndWrittenRightAfterPiNamesItsConversation`: the prompt is queued
  `agentStarting` before `get_state` is answered, written right after it, and delivered.

The test gives a wait of ten minutes, far beyond the tests' 30 second hang guard. With the branch removed it fails
("Waited 30 seconds for a PromptQueued event"), rather than passing late.

From the weighed list, also done on the Tech Lead's instruction:
- The age rule: a comment at the API-mode branch of `SendTextAsync` says that the first-keystroke age check does not
  apply, and that a prompt waiting in the host's queue is written however long it waited, by design.
- The environment sentence in `event-model.md` section 3 now matches the code. Every inherited `CC_*` variable is
  stripped first; terminal mode strips only those it stamps, so an inherited `CC_DIRECTOR_ROOT` or `CC_WORKTREES_HOME`
  reaches a terminal Pi and not an API-mode one. Then the session's own stamped set is applied, the same set terminal
  mode stamps.
- Weighed item (c), the Gateway delivery id, was already addressed by the Delivery Lead's ruling in commit 782a15d85,
  after the reviewed head. (a), the crash journal, is left for an issue or phase 5's scope, as the review suggests.

## Weighed and not raised

Each of these was looked at and judged not to prove a harm that must change in this pull request. They
are here so the next seat knows they were seen.

- **The three gaps the Developer stated.** (a) The crash journal's roster (`SessionManager.ToCrashJournalSession`)
  carries no `ConnectionMode`, so an "Interrupted sessions" continuation after a Director crash would
  reopen an API-mode session as a terminal Pi on the same conversation. That is one process on the
  conversation, honest in the row (`terminal`), and recoverable; but it is a silent change of mode, so it
  wants an issue or a line in phase 5's scope rather than only a sentence in a pull request. (b) API-mode
  prompts are not counted in the turn ledger: phase 2 reads turns from the events. (c) Prompt ids are
  Director-minted (`prompt-1`, ...) rather than the Gateway's delivery id. Phase 2 will need to attach
  its receipt to its own prompt; letting the caller pass an id into `SendPromptAsync` is a one-parameter
  change, better made when the consumer exists than guessed now.
- **The age rule at the write.** `SendTextAsync` branches to API mode above the first-keystroke age check,
  while the guarded send branches below its receipt-time check, and the prompt verb checks the age at
  receipt for both. So a prompt that waited in the host's queue behind a long turn is written to Pi
  however old it has become. In API mode the queue is the design (prompts wait for the turn, and for the
  wheel in phase 4), so a stale write is the intended behaviour, not an escape from the rule; stating
  that in one comment where the branch is would stop the next reader asking the same question.
- **The environment.** The event model now says the environment is "exactly as terminal mode does". The
  terminal path stamps the session's variables onto the Director's own environment and strips nothing;
  the API-mode path strips every inherited `CC_` variable first (the Tech Lead's ruling). The difference
  is any `CC_` variable the Director inherits but does not stamp, such as `CC_DIRECTOR_ROOT` (which the
  installer's launcher sets on the Director) or `CC_WORKTREES_HOME`. `cc-devthrottle` reads neither, so
  I found no harm; the sentence in the event model is a little stronger than the code.
- **A catch inside a helper.** `ApiModeSessionHost.OnRecordLocked` catches three exception types from the
  dialect to stop Pi with a stated defect. It is not an entry point (rule 4), but the pump loop above it
  would do the same with a less specific reason, and it swallows nothing, so there is no functional harm.
- **`IsRunning` after a failed start.** `ApiModeSessionBackend.Start` sets `_host` before `host.Start`, so a
  backend whose process could not be started answers `IsRunning` true and `HasExited` false. The create's
  failure path disposes the session, so nothing reads it.
- **A refusal after the end.** A prompt sent after Pi ended is refused with a `PromptRefused` published
  after that process's `ConnectionEnded`. Rule 1 holds (one receipt per prompt); `ConnectionEnded` is the
  last event of the process, not of the stream, and the session's own handler is not fed it (the observer
  channel is complete), which is harmless since it is not a state event.
- **Naming.** The verb answers an API-mode refusal through `TooOldAnswer`, because the outcome's
  `NothingTyped` is true. The answer's shape is the generic not-delivered one with the reason verbatim,
  so the Gateway reads Pi's words unchanged; only the method's name is narrower than its use.
- **Abbreviations.** `rpc` is Pi's own flag; `JSON`, `GUID`, `UTC` and `id` are as the codebase writes
  them everywhere. Nothing new was introduced that a reader of this codebase would not already know.
