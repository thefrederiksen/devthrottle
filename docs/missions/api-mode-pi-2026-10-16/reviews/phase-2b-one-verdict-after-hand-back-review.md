# Phase 2 follow-on review: after a hand back the API-mode verdict says starting, as the row does (pull request #4246)

Mission: API mode for agent wrappers, proven on Pi (issue #4070), phase 2 follow-on. Rule 7 of the project
instructions: two Gateway verdicts on one session must not contradict each other. Reviewer seat, a different session
from the Developer's, reading only. Reviewed at the pull request head `877c0421c`, on the branch
`api-mode-one-verdict-after-hand-back`, one commit on `cdb6bd994`. `origin/main` is `0e01cef94`, three commits
past that base; the branch merges onto it clean, and on the merged tree the fold still has exactly one caller
(`ApiModeRowStamp`), which passes the new argument. Date: 18 October 2026.

## Verdict

**Approve.** The fold now reads the one fact the row reads, the Director's `ActivityState` `Starting`, and in the hand
back window the two verdicts agree. The Director facts the design rests on hold on `origin/main`. The only other path
that reaches the new branch, a brand-new session's first start (and a crash continuation, which is one), gets a verdict
that is true there as well, and better than before, where it read red. No client reads `Kind`, and every reader of the
words renders them verbatim. Take the wheel is safe to press while the agent is starting. The new tests fail without
the change and pass with it, and every name is `Method_Scenario_Result`. The Developer's one deviation, a failed last
turn keeping the red tone in that window, is right. Two low findings, neither blocking: the comments and the record
call the new state "after a hand back" when it also covers every first start, and no test pins that first-start
state.

## Scope: what I read, what I ran, what I could not reach

**Read, in full, at the pull request head:** the whole diff (four files): `ApiModeSessionFold.cs`,
`ApiModeRowStamp.cs`, `ApiModeDisplayDtos.cs` and both classes of `ApiModeSessionFoldTests.cs`; the pull request
description.

**Read on `origin/main`, for the Director facts:** in `Session.cs`, the API-mode constructor (which sets `Starting`),
`HandBackWheelAsync`, `OnApiModeTerminalChanged`, `OnApiModeEvents`, `TakeWheelAsync` and the guarded send; every
writer of `ActivityState.Starting` in `src` (there are two: that constructor and the hand back's `beforeResume`); in
`ApiModeSessionBackend.cs`, `Start`, `TakeWheelAsync`, `HandBackWheelAsync`, `EndTerminalLocked` and every write of
`_wheelHolder`; in `ApiModeSessionHost.cs`, `Start`, `SuspendAsync`, `ResumeAsync` and `TakeEventsLocked`;
`SessionCommandExecutor.WheelCoreAsync`, which pushes the row after a switch; `ControlApiHost.PushOnWheelChange`
and the activity-change subscription beside it; `SessionManager.CreateSession` where an API-mode backend is built,
and commit `91f1b38ca` (#4091, the crash continuation).

**Read for the readers of the verdict, on the tree merged with `origin/main`:** every file in `packages`, `apps`,
`src` and `tools` that names `activityLabel`, `agentLabel`, `ActivityLabel`, `AgentLabel` or the kind words;
`ApiModeLiveChat.tsx` (the one place the shared Chat draws the verdict), the phone's `Chat.tsx`, the Cockpit's
`SessionActionBar.tsx`, and the desktop's `TerminalWheelPresenter.cs`.

**Ran, twice, as the brief allows, each after `ps -C dotnet -o pid=,args= | grep -i testhost` showed no other test
host:**

1. `dotnet test` on `CcDirector.Gateway.UnitTests`, filtered to `ApiModeSessionFoldTests` and
   `ApiModeSessionVerdictTests`, with the new rule switched off in the fold (`var starting = false && ...`) and the rest
   of the change in place. Result: 47 tests, 43 passed, **4 failed**:
   `Fold_TheAgentIsStartingAfterAHandBack_IsStartingAndNeutralNotIdleAndRed`,
   `Fold_TheAgentIsStartingWithNoTurnYet_SaysItIsStarting`,
   `Fold_TheAgentIsStartingAfterAFailedTurn_KeepsTheFailedTurnRed` and
   `Stamp_HandBackWindow_TheRowAndTheApiModeVerdictAgreeTheAgentIsStarting`. The file was restored afterwards.
2. The same project at the unchanged head, filtered to those two classes and `ApiModeVerdictOnTheRosterTests`:
   55 tests, **55 passed**, which matches the pull request's counts (32, 15 and 8).

The five new tests that pass both ways are guards against over-reach (an open turn still working, the user's wheel,
a terminal row, the ordinary verdict once not starting, the window's end); passing both ways is what they are for.

**Could not reach:** a running Gateway, Director or Pi. Every statement about the Director below is from reading
`origin/main`, not from a live hand back.

## The brief's checks

**1. The Director facts the design rests on.** All three hold on `origin/main`.

- *Starting is set before the new Pi starts, and cannot land after it is ready.* `Session.HandBackWheelAsync` passes
  `() => SetActivityState(ActivityState.Starting)` as `beforeResume`; `ApiModeSessionBackend.HandBackWheelAsync`
  calls it under the ownership lock, after the user's terminal has ended and just before `host.ResumeAsync`.
  `ResumeAsync` starts the process (`StartConnection`) and returns without waiting for it to name its conversation,
  so `ConnectionReady` can only be read after `Starting` was set.
- *It leaves Starting on `ConnectionReady`.* `OnApiModeEvents` sets `WaitingForInput` on `ConnectionReady`, and on
  `TurnStarted` sets `Working`. Nothing else in the API-mode path writes the activity while the protocol holds the
  conversation (`OnApiModeTerminalChanged` writes `WaitingForInput` only when no terminal is attached and the holder
  is not the protocol).
- *The Gateway sees the window with the protocol holding the wheel.* `WheelCoreAsync` pushes the session's row after
  the switch, once `_wheelHolder` is `Protocol`, and waits for the Gateway to apply it. Unless Pi has already named
  its conversation by then, that row carries `WheelHolder` `protocol` and `ActivityState` `Starting`, while the newest
  held connection event is the take's `ConnectionEnded` (the event stream is the same `SessionEventStream` across the
  switch). This is the exact state the change folds, and the one the stamp test builds.
- *A failed hand back leaves no stale Starting.* When `ResumeAsync` throws, the backend sets the holder to `none` and
  raises the terminal change, and `OnApiModeTerminalChanged` moves the activity to `WaitingForInput`; the fold then
  reads `stopped`, which it never confused with starting.

**2. Every other path that leaves `Starting` with the protocol holding the wheel.** There is one, and the verdict is
true there.

- *A brand-new session's first start.* The API-mode constructor sets `ActivityState.Starting`; `Start` sets the holder
  to `Protocol` once the process is started; `ConnectionReady` moves it on. With nothing held yet, the fold used to
  say `idle`, "Idle - no turn yet", agent label "Agent starting" and tone **red** (agent not connected while the
  protocol holds the wheel). It now says `starting`, "Starting - the agent is starting", "Agent starting", neutral.
  True, and the old red was itself a small instance of the same contradiction, beside a row that read blue.
- *A crash continuation (#4091).* `CreateSession` builds a new `ApiModeSessionBackend` and a new
  `SessionEventStream` for the session id it creates, with `resuming` set; the constructor sets `Starting` as for any
  new session. It is the first-start case: nothing held, no receipts, "Starting - the agent is starting". True.
- *After a Gateway restart.* The feed is empty and not complete, so `Agent` alone would say
  "Not known: the Gateway holds no connection event for this session". In the starting window the fold now says
  "Agent starting" instead. That is still a fact the Gateway holds: the Director's row says the agent is starting.
  Right.
- *No other writer.* `Starting` is written in two places in `src` (the constructor and the hand back). The terminal
  state detector drives the activity only through `ApplyTerminalActivityState`, and only with `Working` or
  `WaitingForInput`; and while the detector runs the user holds the wheel, which the fold reads before starting.

Two short windows where the row and the verdict still differ, both outside this change and both corrected without
anyone acting, so I weighed them and did not raise them:

- Between `beforeResume` and the post-switch push, the activity notification can reach the Gateway while the row it
  holds still says `user` (the last full row pushed). The verdict then reads the user's wheel beside a starting row,
  for as long as `ResumeAsync` takes to start a process; `WheelCoreAsync`'s push ends it.
- A take pressed while the agent is starting (check 4) leaves the activity at `Starting` until the terminal state
  detector reads the terminal's screen, because `OnApiModeEvents` does not act on `ConnectionEnded` and a terminal
  attaching leaves the activity alone. The fold reads `userHasTheWheel` there, which is the true verdict; it is the
  row that lags. The test `Fold_StartingWhileTheUserHasTheWheel_IsTheUsersWheel` pins the fold's half.

**3. Every reader of `Kind`.** Nothing branches on it. On the tree merged with `origin/main`, no `.ts`, `.tsx`, `.cs` or
`.axaml` outside the fold and its tests reads `ApiModeSessionDisplay.Kind` or `Working`. The shared Chat
(`ApiModeLiveChat.tsx`, which both the Cockpit and the phone mount) draws `activityLabel`, `lastTurnEnding` and
`agentLabel` verbatim, and turns `tone` into a class with a switch whose default is neutral. The desktop
(`TerminalWheelPresenter`) reads only `WheelAction`. So the new word cannot fall through to a wrong rendering
anywhere; there is nothing for it to fall through. This is rule 7 working as intended.

**4. The wheel button while starting.** Take the wheel, enabled, is safe to press then. The take waits on the ownership
lock, which the hand back holds until `ResumeAsync` has returned, so it cannot interleave with the switch.
`SuspendAsync` sets `_suspended` first, so from that moment a send queues with the user's-wheel reason and no prompt
is written. It decides whether to stop a turn with `TurnRunning || !CanWritePrompt && Ready`: a Pi that has not named
its conversation is neither, so no abort is sent to a process that cannot answer one. Then Pi's input is closed and it
is killed after the grace if it has not exited, and the take returns once its `ConnectionEnded` is published. The
prompts that were queued waiting for the agent to start stay queued and go out at the next hand back. Nothing is lost
and nothing is refused that should not be. The one cost is the lagging row described under check 2.

**5. The tests.** Four of the nine new tests fail without the rule (run 1) and all pass with it (run 2). Every name
is `Method_Scenario_Result` and every test is Arrange-Act-Assert. The stamp test goes through the real ingest, store
and `ApiModeRowStamp`, and also asserts the row's own colour and bucket, so it proves the two verdicts agree rather
than that one of them changed.

**6. The deviation: a failed last turn keeps the red tone in the window.** Right. The tone is one hint for the whole
verdict, and the Chat applies it to the activity pill and to `LastTurnEnding` alike, so "Starting - the agent is
starting again after turn 3" is drawn red beside "The last turn failed: ...". That is the same treatment
"Idle after turn 3" already gets after a failed turn, and the failure is still true. The one place a take makes a
turn fail is a take whose abort Pi never confirmed (`TurnAbortUnconfirmedReason`, outcome `Failed`); there, too, the
red tells the user something real. A turn the take stopped cleanly ends `Cancelled`, which is not red.

**7. An open turn still reads working.** I weighed whether `Starting` should beat an open turn and concluded it
should not. On the Director the two cannot both be true (`TurnStarted` sets `Working`), so they meet at the Gateway
only when one fact has arrived before the other: the new Pi's `TurnStarted` arriving before the activity change that
followed `ConnectionReady`. Working is the true word in that race. The other way they meet, a take's `TurnEnded`
lost on its way to the Gateway, would read "Working" beside a starting row, but that is the lost-turn-end case this
mission already reviewed, it is rare, and it was "Working" before this change too.

## Findings

### Finding 1 (low): the comments and the record call the new state "after a hand back", and it also covers every first start

**Where.** `ApiModeDisplayDtos.cs`, the summary of `ApiModeSessionDisplay.Kind` ("after a hand back, until it is
ready, the same window the row reads as starting") and of `ActivityLabel`; the class summary of
`ApiModeSessionFold`, the paragraph headed "Starting after a hand back"; the comment on the new `else if (starting)`
branch ("a hand back, before the new process names its conversation"); the pull request description.

**The state that reaches it.** A brand-new API-mode session, and every crash continuation, from the moment its
process is started until it names its conversation (check 2).

**The harm.** No wrong behaviour; the verdict there is true. But the change silently alters the first-start verdict
(from red "Idle - no turn yet" to neutral "Starting - the agent is starting"), and the next reader of the fold, or of
the contract, learns that `starting` happens only after a hand back. Someone debugging a new session that reads
"Starting" would look for a hand back that never happened. One clause in each of those places: "while the agent
process is starting: a new session's first start, or a hand back".

**Developer's answer.** Accepted and fixed. `ApiModeDisplayDtos.cs`: the summary of `Kind` now says the starting
state covers "while the agent process is starting - a new session's first start, or a hand back", and the summary
of `ActivityLabel` names both words ("Starting - the agent is starting" for a first start, "... again after turn 3"
for a hand back). `ApiModeSessionFold.cs`: the class paragraph is now headed "Starting: a new session's first start,
or a hand back", says a session is set starting when it is created (every crash continuation is one) and before a
hand back, and says what a first start read before this rule (red "Idle - no turn yet"); the comment on the
`else if (starting)` branch names both. The pull request description says the same.

### Finding 2 (low): no test pins the first-start state

**Where.** `ApiModeSessionFoldTests.cs`. `Fold_TheAgentIsStartingWithNoTurnYet_SaysItIsStarting` has no turn but
uses `HeldAfterTheTake()`, a held `Ready` and a held take ending, which is not what a new session holds.

**The state that reaches it.** As in finding 1: protocol holds the wheel, `ActivityState` `Starting`, no receipts,
nothing held (`HeldWhole()`), and after a Gateway restart nothing held and the feed not complete.

**The harm.** That state's verdict changed with this pull request, from red to neutral, and nothing would notice if a
later edit to the `starting` condition (for example, to make it require a held `ConnectionEnded`, which would be a
natural "only after a hand back" tightening) sent new sessions back to red. One test through the stamp, with an
empty feed and a starting row, asserting `starting`, neutral and "Agent starting", closes it.

**Developer's answer.** Accepted and fixed, with two tests. `Fold_ANewSessionsFirstStart_IsStartingAndNeutralNotIdleAndRed`
is a theory over both first-start feeds - the whole generation with nothing held (`HeldWhole()`) and, after a Gateway
restart, an empty feed that is not complete - with the protocol holding the wheel, no receipts and the agent
starting; it asserts `starting`, neutral, not working, no turn number, "Starting - the agent is starting" and "Agent
starting". `Stamp_FirstStart_TheRowAndTheApiModeVerdictAgreeTheAgentIsStarting` goes through the real ingest, store
and `ApiModeRowStamp` with nothing pushed and a row whose `ActivityState` is `Starting`, and asserts the row is blue
and the verdict is `starting`, neutral, "Starting - the agent is starting" and "Agent starting". With the rule switched
off in the fold (`var starting = false && ...`) all three cases fail; with it restored all pass.

## Summary

| Finding | Severity | Blocks the merge |
|---|---|---|
| 1. Comments and record say "after a hand back"; the state also covers every first start | low | no |
| 2. No test pins the first-start verdict, which this change moved from red to neutral | low | no |

The verdict is **approve**. Both findings can be answered in this pull request before it merges or in the next
follow-on; neither changes what the user sees.
