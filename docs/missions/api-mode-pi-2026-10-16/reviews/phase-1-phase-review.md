# Phase review: phase 1 as a whole, the API-mode transport and Pi's dialect

Mission: API mode for agent wrappers, proven on Pi (issue #4070). Phase 1 was merged as four pull
requests, each reviewed alone: #4074 (the event types), #4075 (the transport), #4076 (Pi's dialect) and
#4088 (the API-mode session). This review reads the merged result together, against section 5 of the
mission document and phase 1's row in section 6. Reviewer seat, opened by the Delivery Lead (session
102), running a different agent family from every Developer of the phase. Written 16 October 2026 on
devlinux, in a worktree cut from origin/main at 85263c060 (the merge of #4088).

**Verdict: the phase holds. One finding, proven by a test I wrote, ran and deleted, on the seam between
the dialect's exit handling and the session's own exit verdict. Everything else the brief named is in the
code and covered by tests that replay the real recordings.** The Developer who owns the session wiring
decides what happens to the finding.

## Scope

What I read, all on origin/main at 85263c060:

- The Director's API mode in full: `ApiModeSessionHost.cs` (850 lines), `Pi/PiDialect.cs` (841),
  `ApiModeTransport.cs` (602), `ApiModeSessionBackend.cs`, `SystemApiModeProcess.cs` (the process, the
  executable resolver and the environment builder), `DedicatedThreadReadStream.cs`, `NewlineJsonFramer.cs`,
  `ApiModeTransportEvents.cs`, `IApiModeProcess.cs`, `ApiModeProcessStartInfo.cs`, `ApiModeArguments.cs`,
  `ApiModeExceptions.cs`, `SessionEventStream.cs`, `SessionEventNumbering.cs`, `Pi/PiCommand.cs`.
- Every seam into the rest of the Director: the API-mode branches of `Session.cs` (the constructor,
  `ConnectionMode`, `SendTextAsync`, `SendTextOnlyWhenWaitingForInputAsync`, `CancelTurnAsync`,
  `DeliverPrePromptAsync`, `OnApiModeEvents`, `OnBackendProcessExited`, `IsUnexpectedExit`, `Dispose`);
  `SessionManager.CreateSession` from `RequireApiModeSupported` through the backend choice, the launch line
  and the recorded conversation id; `PiDriver.ConnectionModes` and `PiAgent.BuildLaunchSpec`; `FleetDoorbellRinger`;
  `SessionCommandExecutor` (the prompt verb's two sends, the not-delivered answer, `turn-stop`);
  `SessionWriteExecutor`; `ControlEndpoints` (the session row); `DirectorRestore.BuildRequest` and
  `DirectorWayUp.BuildReopen`; `WayUpWords.ReopenOffer`; `CommandLineLauncher`; the crash journal's row
  and its consumers.
- The tests: `PiWireLogReplay`, `PiDialectReplayTests`, `ApiModeTransportPiFixtureTests`, `ScriptedPi`,
  `FakeApiModeProcess`, `RecordingSink`, and the names and the exit-related bodies of
  `ApiModeSessionHostTests`, `ApiModeSessionTests` and `ApiModeSessionCreationTests`.
- The seven recordings in `src/CcDirector.Core.Tests/TestData/pi-rpc/` and their README: the order of
  records in `plain`, the shapes of `text_end`, `agent_settled`, `get_state`, `tool_execution_end`, the
  abort's order, and every `stopReason` value on the wire.
- The mission document (issue #4070), `event-model.md` in full, the mission README, `spend.md`, and the
  four per-pull-request reviews with the Developers' answers.

What I ran, on devlinux with the .NET 10 toolkit at `$HOME/.dotnet`:

- `dotnet test src/CcDirector.Core.UnitTests --filter "FullyQualifiedName~ApiMode"`: 222 passed, 0 failed,
  0 skipped, 658 milliseconds.
- The whole `CcDirector.Core.UnitTests` suite: 2,098 passed, 6 failed, 23 skipped, 12 seconds. Five are the
  Windows drive-path tests that fail on Linux on main (`CcWorktreesPoolSlotsTests` three times,
  `RepositorySourceIndexTests`, `WorktreePoolSettingsTests`). The sixth is new since the pull requests'
  own runs and is not phase 1's: `CatchLogLineScanTests.EveryLogLineInACatch_IsReportedOrSaysWhyNot`
  names two log lines in `src/CcDirector.ControlApi/CatalogReadExecutor.cs` (lines 498 and 503, the
  `fs-list` verb for a session, issue #4078) that are written inside a catch in neither the house form nor
  with the opt-out comment. It is red on origin/main today and whoever owns #4078 should chase it.
- Two throwaway tests, written in the review worktree, run and deleted, for the finding below. Both passed,
  which is what proves it.

What I could not reach:

- No real Pi and no real Director; the recordings stand in for Pi. The spend record was not touched.
- No Windows machine. Three things are therefore read, not seen: a bare `pi` resolving to `pi.cmd` and
  being started by `Process.Start` without `cmd /s /c` (the terminal path uses the shim wrapper, the API
  path does not); `DedicatedThreadReadStream` and its `CancelSynchronousIo`; the exit code -1 of a killed
  process. The pull request says the Windows gate is the Tech Lead's.
- Stop in the window between Pi's `started` acknowledgement and `agent_start` (0.3 to 0.4 seconds on the
  recordings): no recording provokes it, and what Pi's `abort` does there is not documented. The host would
  answer "nothing was running" and the turn would then begin.
- The Gateway's side (the receipt store, the ingest, the verbs of phase 2, also on main) is outside phase
  1 and was not reviewed here. The Director does not yet send its event batches to the Gateway: the only
  sink on main is `LoggingSessionEventSink`.

## What the brief asked, and what the merged code does

An absence below means looked at and found as the design says, not skipped.

**A long-lived process per session.** One `ApiModeSessionHost` per session, one `Connection` (a transport
and a dialect) at a time, started once in `Start` and pumped until the process ends. The only second process
is the restart after the turn-start bound: it is started inside the same locked section that observed the
first process's end (`OnProcessEndedLocked`), after the transport reported the exit and drained the pipes,
and never on a shutdown, a disposal or a record the dialect could not read (`ShuttingDown`). So section 5's
"exactly one process holds a conversation" holds for both paths phase 1 has that change the process.

**Line-feed-only framing.** `NewlineJsonFramer` works on bytes, splits on 0x0A only, strips one carriage
return before it, holds a line across reads (so a UTF-8 character split by a read boundary is decoded whole)
and refuses a line over 64 megabytes. The writer serialises with System.Text.Json, which never writes a raw
line feed inside a string, so one record is one line. Tested against all seven recordings' standard output
at read sizes 1, 7 and 4,096 bytes.

**Delivery from Pi's acknowledgement in place of the echo checks.** `PromptDelivered` comes only from a
`response` whose id the dialect wrote, with Pi's line kept verbatim as the acknowledgement (asserted byte
for byte against the `plain` recording). `Session.SendApiModePromptAsync` answers Delivered only on that
receipt and Refused with the reason otherwise; the typing, echo, records and first-keystroke age checks are
not run for an API-mode session (the age rule's absence at the write is stated in a comment, by design: the
host's queue is where a prompt waits). The prompt verb turns a refusal into the same not-delivered answer
every other refusal uses, with Pi's words in `DeliveryStateReason`. The guarded "only when waiting" send
consults the host (`TrySendPromptNowAsync`: conversation named, nothing in flight, nothing queued), never a
screen, and the fleet doorbell rides on it for an API-mode session.

**Stop as abort confirmed by Pi.** `StopAsync` writes `abort`, registers a waiter on that command id, and
completes only when Pi answers it, carrying the `TurnEnded` published meanwhile; an abort while idle answers
null and emits nothing; a process end settles every waiter. The `abort` recording's order (the aborted
`message_end`, `agent_settled` with `aborted: true`, and the abort's own response last) is what the replay
asserts, and the `turn-stop` verb refuses a terminal session with a sentence pointing at interrupt.

**Resume by session id.** `PiAgent.BuildLaunchSpec` puts `--session-id` on every launch; `CreateSession`
starts the backend `resuming` when a resume id was given; the dialect condemns a resume on Pi's "No project
session found" warning or on `messageCount` 0, before `ConnectionReady`, and no prompt is written before
`ConnectionReady`, so a prompt can never run in the empty conversation Pi made instead; the queued prompt is
refused with that reason. The Smart Restart reopen passes the seat's recorded conversation id as `ResumeSessionId` and
`ConnectionMode`; the conversation id is recorded for Pi because `PiAgent.SupportsPreassignedSessionId` is true,
and Pi's plugin declares it can resume a saved conversation. The drain restore passes `ConnectionMode` and,
as for every agent, opens a fresh conversation with the handover prompt.

**Terminal mode's code path unchanged.** The whole of API mode enters the terminal path at these points and
nowhere else: in `Session`, one null test of `_apiMode` in each of the constructor, `SendTextAsync`, the
guarded send, `CancelTurnAsync`, `DeliverPrePromptAsync` and `Dispose`; in `SessionManager.CreateSession`,
the `connectionMode == Api` test before the backend switch and the `backend is ApiModeSessionBackend` test
before `CommandLineLauncher.Build`; in `FleetDoorbellRinger.RingAsync`, one `ConnectionMode == Api` test; in
`SessionCommandExecutor`, a new verb that refuses terminal sessions, and an optional `deliveryId` the
terminal path never reads. `PiDriver` gains only `ConnectionModes`. Every one is false or null for a terminal
session, so a terminal session's launch line, environment, backend, sends and Stop are composed by the same
statements as before the phase.

**Section 7's failure cases that belong to the Director.** Pi exits mid-turn: the turn ends `failed` with
the exit code and the standard error written since the turn started, the prompt stays Delivered (the
Delivery Lead's ruling), `ConnectionEnded` follows, and the session's exit code is 137 (host test and
session test, both on the `exit` recording; but see the finding on what the session itself concludes). Pi
refuses: `PromptRefused` by the agent with Pi's words verbatim, through the host, the session and the verb
(the `refusal` recording). Pi missing: `SessionManager` refuses before anything is started when the command
is not a file and not on the search path, and `ApiModeExecutableResolver` refuses the same way if it is ever
reached, each naming the executable. Acknowledged but never started: the turn-start bound on the injected
clock, armed at the `started` acknowledgement and disarmed at `agent_start`; when it runs out the dialect
emits the ruling's third row and condemns the process, the host restarts Pi on the same conversation in the
same folder with the same arguments, the overdue prompt is never sent again, and a queued prompt goes to the
new process (host test, with the restart proven not to fire when the turn begins in time).

**The seams between the four pull requests.** Everything that touches a dialect, the queue, the stream or
a connection runs under the host's one lock; a command is written and reported to the dialect inside it, so
Pi's response can never reach the dialect first. Standard error is fed to the dialect before every record
that is not a reply delta and at the process end, through a cursor, so a failure reason holds its own turn's
lines. `OnProcessEndedLocked` refuses the still-queued prompts first (they were never written, so the
dialect knows nothing of them), then takes the dialect's end events, then settles every stop waiter and every
receipt still owed, and only then restarts or records the session's end; `Published` runs on its own loop
outside the lock and `Exited` fires after the last batch. The transport's request matching
(`SendRequestAsync`) is not used by the host, which writes with `WriteRecordAsync` and lets the dialect match
ids; that is one mechanism, not two. The exit code translation (the recorder's -9, .NET's 137 on Linux) is
applied the same way in both replays and stated in the fixture README and the dialect's contract.

**Whether the tests replay the real fixtures.** The dialect tests replay the seven transcripts record by
record, with the fake clock set to each record's recorded time, and assert the exact event sequence. The
transport tests replay the seven standard output files byte for byte at three read sizes, and the seven
transcripts as request and response. The host, session and creation tests use `ScriptedPi`, which answers
each command the host writes with the records Pi wrote after the same command in a transcript: blocks are
matched by command type, so the host's `get_state`-first order is answered by the recorded `get_state`
block although the recorder wrote it after the prompt, and a recording with no `get_state` (`tool`, `exit`,
`exit-before-ack`) borrows `plain`'s with the conversation id swapped. Every departure from the recording is
named where it is used: `AcknowledgeOnly` cuts a prompt's block after the acknowledgement (the unrecorded
"acknowledged but never started" case), `EditStdout` changes `messageCount` 5 to 0 (the unrecorded empty
resume), `PromptAnswers` and `RepeatFirstPromptAnswer` pick which recorded prompt answers which written
one. Nothing is hand-built, and the two unrecorded cases are the two the event model says no recording
provokes. The 30 second hang guard only fails a test; no wait decides a pass.

## Finding

### 1. After an API-mode Pi ends, the session's own verdict reads a clean exit whenever the exit code is 0, however the stream says the turn ended

**Where.** `src/CcDirector.Core/Sessions/Session.cs`: `OnApiModeEvents` sets `WaitingForInput` on every
`TurnEnded`, whatever its outcome, and `OnBackendProcessExited` decides `Crashed` from
`IsUnexpectedExit(exitCode, wasWorking)` where `wasWorking` is `ActivityState == Working` at the moment the
exit arrives. `ApiModeSessionBackend.OnHostExited` hands the session the exit code alone; the
`ConnectionEnded` reason never reaches the session.

**What happens.** The host publishes every event batch before it raises `Exited` (the observer loop reports
the end after the last batch, by design). So when Pi ends mid-turn, the dialect's synthesised `TurnEnded`
(`failed`, "Pi exited during the turn") moves the session to `WaitingForInput`, and the exit then arrives
with `wasWorking` false, always. The rule written at that line for issue #959, "a session that dropped out
while it was actively working is a crash even if its exit code is 0", can therefore never fire for an
API-mode session; the exit code alone decides. The reason the dialect wrote (the resume that failed, the
record it could not read, the exit mid-turn) is in the stream and in the Director log, and nowhere on the
session: no `Crashed`, no `Failed`, no status colour.

**Proof.** Two tests, written in the review worktree, run and deleted:

- The real `exit` recording's standard output (Pi killed while the reply streamed) replayed through the fake
  process, which reported exit code 0 instead of the recording's kill. The stream held one `TurnEnded`,
  `Failed`; the send was answered Delivered; and the session ended with `ExitCode` 0, `Status` `Exited`,
  `Crashed` false.
- The `resume` recording with `messageCount` changed to 0, started `resuming`. `ConnectionEnded` carried
  "Resume failed: Pi's conversation ... has no messages (messageCount 0)"; the session ended with `ExitCode`
  0, `Status` `Exited`, `Crashed` false.

**Why exit code 0 is reachable.** Pi exits 0 when its standard input closes (event model, section 1; every
recorded run). The Director closes it in two places that are not a user's Stop or kill: on a resume that
found nothing (`StopReason` from the dialect), and on a record the dialect could not read mid-turn
(`OnRecordLocked`'s catch, which stops Pi and lets the end end the turn `failed`). In both the session reads
exactly as a Pi that finished its work and left. The 137 case the recording shows is marked `Failed`, but
only because the code is not 0, with the generic "crashed: the agent process exited with code 137", not the
dialect's reason.

**The harm.** Section 3.7 of the event model says a resume that silently made a new session "must fail with
that reason", and section 7 of the mission document says of Pi exiting mid-turn that "the session shows
failed with the reason". For the paths above the session shows `Exited`. `Status` and `Crashed` are what the
session row carries to the Gateway (`ControlEndpoints`, the session row's `Status` and `Crashed` fields), so
the roster, the fleet and phase 2's fold read a clean end; a user who asked for a resume sees a session that
exited a second after it was created, with the pre-prompt refused and no reason on the row. Terminal mode
would have marked the same event (a Pi gone while working, exit code 0) as a crash. The one test that covers
the failed resume at the session level, `CreateSession_ApiModePiResumeFindsNothing_SessionEndsWithTheReason`,
asserts `Status is Exited or Failed`, so it passes on either answer and cannot catch this.

**Why it must change.** The event model gives the session the signal it lacks: a `TurnEnded` whose outcome
is `failed` because the process ended, and a `ConnectionEnded` with a reason, both published before the
exit. What the right shape is belongs to the Developer who wired the session: leave the state `Working`
when the `TurnEnded` is the dialect's exit synthesis so the existing rule fires, or carry the
`ConnectionEnded` reason from the backend to the session and mark it failed with that reason when the
stream says so, or both. Whichever it is, the creation test should pin `Failed`, and a test should pin a
Pi that ends with exit code 0 mid-turn.

**Answer (accepted; phase 4, branch `apimode/p4-silent-end`).** Both shapes, and the end now says what went
wrong:

- **The stream decides, not the exit code.** `PiDialect.EndedInFailure` is set when Pi's process ends: true when
  the Director had required a stop (a resume that found nothing, a turn that never began), or a turn was
  running, or a prompt was delivered with no turn, or a prompt was never acknowledged. An idle Pi whose input
  was closed is not a failure. `ApiModeSessionHost.EndedInFailure` adds the host's own failures (a restart that
  could not start Pi, the Director's event loop failing). `ApiModeSessionBackend` reads it and the
  `ConnectionEnded` reason before it raises `ProcessExited`, and `Session.OnBackendProcessExited` marks the
  session `Failed` and `Crashed` when the stream says so, whatever the exit code.
- **The issue #959 rule now fires too.** A `TurnEnded` in the same batch as the `ConnectionEnded` (the dialect's
  exit synthesis) no longer moves the session to `WaitingForInput`, so the exit is read as one while working.
- **The end says what went wrong, the exit code and the standard error tail.** The turn and never-started
  paths already did; the resume that found nothing and the prompt never acknowledged did not. Their
  `ConnectionEnded` reason now ends with `Pi exited (exit code N)` and, when Pi wrote any, `Standard error: ...`.
  The session carries the reason as `Session.EndReason`, and its status reason (on the session row) reads
  `failed: <that reason>` instead of the generic "crashed" words.
- **The failure colour is final.** A Pi whose resume found nothing ends within milliseconds of creation, before
  the Wingman first sees it; the Wingman's plain "exited" no longer paints over the failure.

- **A record the Director could not read ends failed too, even with nothing in hand** (finding 1 of the pull
  request's own review, `phase-4-silent-end-review.md`): the host tells the dialect, so the stop reason is the
  end's reason.

Tests, in `CcDirector.Core.UnitTests` (145 of 145 API mode tests pass):
`CreateSession_ApiModePiResumeFindsNothing_SessionEndsWithTheReason` (now pins `Failed`, exit code 0 and the
reason), `CreateSession_ApiModePiEndsWithExitCodeZeroMidTurn_SessionFailsWithTheReason`,
`CreateSession_ApiModePiIdleEndsWithExitCodeZero_SessionExitsCleanly` (the clean exit still reads clean),
`SetStatusColor_AfterTheSessionFailed_IsDroppedAndTheFailureAndItsReasonStay`,
`OnProcessExited_BeforeAcknowledgement_EndReasonCarriesTheExitCodeAndStandardError`,
`OnProcessExited_AfterAResumeThatFoundNothing_EndReasonCarriesTheStopReasonExitCodeAndStandardError` and
`OnProcessExited_IdleWithNothingInHand_IsNotAFailure`, and after the pull request's review
`OnRecord_UnreadableGetStateAnswerWhileIdle_EndsInFailureWithTheReason`,
`CreateSession_ApiModePiRecordUnreadableWhileIdle_SessionFailsWithTheReason`,
`RequireStopForUnreadableRecord_ThenPiExitedIdle_EndsInFailureWithTheReason`,
`OnProcessExited_IdleWithNonZeroCodeAndStandardError_ReasonCarriesTheStandardError`,
`CreateSession_ApiModePiEndsMidTurn_ActivityGoesFromWorkingToExitedWithNoWaitBetween` and
`TurnStartBound_RestartCannotStartPi_EndsInFailureWithBothReasons`. Five existing tests that pinned the old end wording were
updated to the new wording. Also run: the API mode tests in `CcDirector.Gateway.UnitTests` (272 of 272 pass;
the Gateway shows the `ConnectionEnded` reason verbatim), and the session colour and crash tests in
`CcDirector.Core.Tests` (`SessionStatusWingmanTests`, `SessionReapOnExitTests`, `SessionDeletionReaperTests`,
57 of 57 pass), because the final colour applies to every session.

## Weighed and not raised

Each of these was looked at and judged not to prove a harm on a path the merged code can reach today. They
are here so the next seat knows they were seen.

- **The crash journal's row carries no `ConnectionMode`** (`SessionManager.ToCrashJournalSession`), which
  the integration review named and left for "an issue or a line in phase 5's scope". Neither exists: no
  GitHub issue mentions it, and the mission README's phase rows do not. Today nothing reopens a session from
  the journal: the desktop detects and preserves the journals, and the control layer lists, dismisses and
  removes them (`interrupted-list`, `interrupted-dismiss`, `interrupted-remove`). So a mode change on
  recovery cannot happen yet; the knowledge that it would sits only in a review file. The Delivery Lead may
  want it written where the restore work will find it.
- **`LoggingSessionEventSink` is the only sink on main.** It writes every event as JSON to the Director's
  log, including every reply delta, every tool call's verbatim `input` and `output`, and every
  acknowledgement line. That is phase 1's stated interim until phase 2's relay exists on the Director side
  (nothing outside `CcDirector.Core` references `SessionEventBatch` today). When the relay lands, this sink
  should not stay as a second writer: the Director log would otherwise double as a full transcript of every
  API-mode conversation, tool output included.
- **The mission README still says phase 1 "Not started"** and phase 0 "In review". It is the record the
  Delivery Lead lands; it is out of date, not wrong code.
- **`ActivityState` is `WaitingForInput` while a queued prompt waits to be written** (after a `TurnEnded`
  and before the next `PromptDelivered`, inside one locked section). Cosmetic: the guarded send consults the
  host's gate, not the state.
- **`ErrorLinesSince.LinesNoLongerHeld` is not read by the host.** It is non-zero only when more than 2,000
  standard error lines arrive between two records that are not reply deltas. Pi writes almost nothing to
  standard error.
- **`turn-stop` has no budget of its own.** `StopAsync` waits for Pi's response with no cancellation; the
  waiter is settled at the process end, so it waits only while Pi is alive and silent, which is a hung Pi
  the user can kill.
- **The sixth red test** in `Core.UnitTests` (`CatchLogLineScanTests`, two lines in
  `CatalogReadExecutor.cs`) is issue #4078's, not this phase's. Named in the scope above so it is chased.
