# Phase 4 review: take the wheel, as a whole on main

Mission: API mode for agent wrappers, proven on Pi (issue #4070), phase 4. Reviewer seat for the phase as a whole,
opened by the Delivery Lead (session 130), running a different agent family from every Developer of the phase.
Reviewed on 18 October 2026 against origin/main at commit `20491d6ef`, in a worktree cut from it. Reading only:
nothing was built, fixed or committed as a test by this seat. The ten pull requests the Lead named (#4090, #4091,
#4094, #4102, #4121, #4129, #4186, #4190, #4192, #4211) are all on that commit.

## Verdict

**The phase meets section 5's mode switching and section 3's goal lines on the Director and the Gateway, with one
finding on the surface the user watches.** Exactly one process holds the conversation on every path I could trace,
and the Director enforces it under one ownership lock whose every wait is bounded. Prompts sent while the user holds
the wheel are queued with the reason and written in order on hand back, each with its own receipt. The buttons render
the Gateway's verdict and send the two commands the Director lists. What does not hold is the live terminal stream the
Cockpit and the phone draw the wheel's terminal from: after a hand back and a second take it shows the first
terminal's dead screen and then a partial stream (finding 1). One smaller finding and several notes follow. The
Developer decides what happens to every finding (law 11).

## Scope: what I read, what I ran, what I could not reach

**Read in full on origin/main:** `src/CcDirector.Core/ApiMode/ApiModeSessionBackend.cs`, `ApiModeSessionHost.cs`,
`SessionWheel.cs`; `src/CcDirector.Gateway/Api/GatewayEndpoints.ApiModeVerbs.cs`;
`src/CcDirector.Gateway/ApiModeDisplay/ApiModeSessionFold.cs`, `ApiModeRowStamp.cs`;
`src/CcDirector.Gateway/ApiModeEvents/ApiModeEventIngest.cs`; `src/CcDirector.Gateway/Receipts/ApiModeLostTurnEnd.cs`;
`packages/client-core/src/sessions/wheelAction.ts`, `TerminalWheelButton.tsx`; `apps/cockpit/src/panes/TerminalPane.tsx`.
**Read in part:** `Session.cs` (the mode and wheel surface, the constructor, `InitializeScreen`,
`OnApiModeTerminalChanged`, `OnApiModeEvents`, `TakeWheelAsync`, `HandBackWheelAsync`, `StopApiModeTurnAsync`,
`SendTextAsync` and its guarded sibling, `SendInput`, `WriteRawInput`, `BeginInputAsync`, `OnBackendProcessExited`,
`GetTerminalSnapshot`, `ApplyGatewayDisplayState`); `SessionManager.cs` (the API-mode create, `ToCrashJournalSession`,
the known-id block); `SessionCommandExecutor.cs` (the prompt send's three arms, `TurnStopCoreAsync`, `WheelCoreAsync`);
`ControlApiHost.cs` (the Hello and new-connection callbacks, `PushOnWheelChange`, the stop order);
`DirectorStreamProducers.cs` (`ProduceTerminalAsync`), `DirectorUpStreamHandler.cs` (its one caller);
`CircularTerminalBuffer.GetWrittenSince`; `TerminalStateDetector.Wire` and `FollowApiModeSession`;
`DirectorCrashJournal.cs` (the row), `GatewayEndpoints.ContinuationRequest`, `DirectorRestore.cs` (the restore request);
`DeliveryRecord.cs` (the entry and the owner fields); `ApiModeReceiptStore.cs` (the public surface);
`packages/client-core/src/terminal/interactive.ts` (the keystroke pump, the reset on connect, the frame handler),
`stream.ts` (the mirror's shape), `apps/mobile/src/pages/Terminal.tsx`, `apps/cockpit/src/sessions/SessionDetail.tsx`
(how the Terminal pane is mounted). **The record:** issue #4070 in full, `event-model.md` in full, every
`reviews/phase-4-*.md` with the Developers' answers, and the phase 4 gates.

**Ran on devlinux (Linux, .NET 10.0.301), one project at a time, in the worktree, with
`DOTNET_USE_POLLING_FILE_WATCHER=1`:**

- `dotnet test src/CcDirector.Core.UnitTests --filter "FullyQualifiedName~CcDirector.Core.UnitTests.ApiMode|FullyQualifiedName~ApiModeSessionJournalRowTests"`:
  **212 passed, 0 failed, 0 skipped.**
- `dotnet test src/CcDirector.Gateway.UnitTests -p:RunWorkspaceTypecheck=false --filter "FullyQualifiedName~ApiMode|FullyQualifiedName~Wheel|FullyQualifiedName~ApiModeEventPusher"`:
  **534 passed, 0 failed, 0 skipped**, 1 minute 5 seconds.
- Memory before each run (`free -g`): 22 to 23 gigabytes available (the `free` column read 4 to 5, with 19 in
  cache). Other sessions were running their own Gateway unit suites on the machine at the time; I started no second
  suite of my own.

**Could not reach:** no real Pi, Director or Gateway was started, so every claim about the real terminal hosts, the
real transport and the browser is by reading. No Windows machine; `scripts\test-local.ps1` was not run. I wrote no
throwaway test: finding 1 rests on two short code paths quoted below, not on a measurement. The web tests were not run.

## Section 5's mode switching, checked

**Exactly one process, and the Director enforces it.** `ApiModeSessionBackend` holds `_ownership` across the whole of
a take, a hand back, the shutdown and the terminal's own exit. A take suspends the host first, and `SuspendAsync`
returns only after `Connection.EndHandled`, which is set under the host's lock when Pi's process end was handled; the
terminal factory runs after that. A hand back ends the terminal under the lock and detaches it only once `HasExited`
is true, else refuses with `TerminalStillRunningReason` and changes nothing; only then does `ResumeAsync` start Pi.
`StartConnection` has three callers, and the restart and the overdue timer both read `_suspended` under the host's
lock, which `SuspendAsync` sets in its first step. The stop inside a take is bounded on the injected clock, so no wait
under the ownership lock depends on Pi. I found no path to two processes inside one session. Across sessions,
`ApiModeConversationClaims` refuses a second API-mode session on the same folder and conversation (a terminal-mode Pi
resumed by hand on the same id is not covered, as the wheel review already noted).

**Stop the API process cleanly and start `pi --session-id`, then the reverse.** The take stops a running turn with
Pi's abort, closes its input, kills after the grace; the terminal is started through the terminal path's own factory
with the launch line minus `--mode rpc`, the same folder and environment. The hand back starts `pi --mode rpc
--session-id <id>` in the same folder with `resuming: false` (right: the terminal may have left the conversation
empty), and `WriteQueuedPromptsLocked` writes the held prompts once `ConnectionReady` arrives.

**Section 3's goal lines.** Take the wheel opens the terminal on the same conversation; hand back returns to API mode
with the terminal's messages in Pi's own session file; prompts sent while the user holds the wheel queue
(`PromptQueuedReason.UserHasTheWheel`), go out on hand back in order, each with its receipt. Stop while the user holds
the wheel is refused with the true reason. The one qualification is finding 1: the goal line says the terminal is
opened, and on the second opening the Cockpit and the phone do not show it correctly.

## The seams the Lead named

- **A take racing a turn.** `SuspendAsync` sets `_suspended` under the host's lock before anything else; a
  `SendPromptAsync` arriving after queues with the wheel's reason, one arriving before is written and then stopped by
  the abort, and its turn ends with `TurnStoppedForTheWheelReason` (or the unconfirmed-abort reason after the grace).
  Pinned by two tests with a turn running at the take.
- **A hand back racing a prompt.** `ResumeAsync` holds the host's lock from `StartConnection` to `_suspended = false`;
  a prompt in the window before `ConnectionReady` queues as `agentStarting` and is written on ready.
- **A Gateway restart.** The event pusher resends everything held once per new connection; the rows are re-pushed
  with the holder; the fold states "not known" where the empty feed supports nothing, and the wheel button is ruled
  from the holder alone. Receipts are durable. Nothing of the wheel is lost.
- **A Director restart.** The crash journal row carries `ConnectionMode`, and `ContinuationRequest` passes it, so a
  continued session is in API mode; the planned restore (`DirectorRestore`, `DirectorWayUp`) carries it too. Neither
  passes a resume id: a continuation starts a fresh conversation with a seed prompt, which is the pre-existing shape
  for every agent and not this phase's. The held queue is in memory only (note 1).
- **A hole in the event push.** Every new generation opens with the still-queued prompts re-announced in line order;
  the Gateway folds the line from those events. Reviewed twice already; I re-read `TakeEventsLocked`,
  `WriteQueuedPromptsLocked` and `RefuseQueuedLocked` and found the line and `_queue` agree at every event, except the
  written-but-unacknowledged window the requeue review already names.
- **The buttons and their commands.** `ApiModeSessionFold.WheelAction` is the one ruling; `readWheelAction` reads it
  verbatim and throws on a verb it does not know; the two routes send `wheel-take` and `wheel-hand-back`, which
  `SessionWriteExecutor` lists. A holder of `none` gets "Hand back", which the Director accepts as the verb that
  resumes. Right.
- **The queue's receipts across all of these.** Every path that drops a queued prompt publishes a `PromptRefused`
  with the reason (`RefuseQueuedLocked`), including the session ending while the user holds the wheel. The one path
  with no refusal is the Director process ending (note 1).

## Findings

### 1. The live terminal stream does not follow the wheel's terminal: after a hand back and a second take, the Cockpit and the phone show the first terminal's dead screen, then a partial stream

**Where.** `src/CcDirector.ControlApi/DirectorStreamProducers.cs`, `ProduceTerminalAsync`: one producer per open
stream, whose `cursor` starts at the snapshot's reflected count and is advanced only when `GetWrittenSince` returns
bytes; it reads `session.Buffer` afresh on every pass but never notices that the buffer is a different object.
`src/CcDirector.Core/Memory/CircularTerminalBuffer.cs`, `GetWrittenSince`: `if (position >= _totalWritten) return
(Array.Empty<byte>(), _totalWritten)`, and the producer takes the new position only when `data.Length > 0`.
`apps/cockpit/src/panes/TerminalPane.tsx`: one `InteractiveTerminal` per session, always mounted
(`SessionDetail.tsx`, "the terminal is ALWAYS mounted"), so the stream lives across every switch;
`packages/client-core/src/terminal/interactive.ts` resets the terminal only on a new connection ("each connection
replays full history from byte 0"). The phone's `TerminalMirror` is constructed once per session the same way.

**What happens.** The user takes the wheel; the new buffer's bytes flow from 0 and the pane draws Pi's terminal
(right). They hand back: `Buffer` becomes null, nothing more is streamed, and the pane keeps Pi's last frame with no
word that the terminal is gone. They take the wheel again: a new `CircularTerminalBuffer` starts at 0, the producer's
`cursor` is still the first terminal's byte count N, every read answers empty until the second terminal has written
more than N bytes, and from then on only the bytes past N arrive, mid-frame. The pane shows the first terminal's dead
screen, then a corrupted one, until the user re-selects the session or reloads, which opens a new stream with a fresh
snapshot. A stream opened while the user already holds the wheel is right until the next hand back and take.

**Proof.** By reading the two paths quoted above; I did not run it. The producer's comment says it is "one monotonic
cursor", which is exactly the assumption a second terminal breaks.

**The harm.** Section 3's goal line, "Take the wheel opens Pi's real terminal on the same conversation", is what the
user sees on the Cockpit and the phone, and on the second take they see the wrong terminal. Take, hand back, take
again is ordinary use, and the mission's own check (section 7, steps 4 and 5) takes and hands back once per run, so a
proof run that takes once would pass over it; the committed phase 5 proofs have no Terminal tab dump across a switch.

**Why it must change.** The producer must treat a change of buffer as a new terminal: re-send a Size frame and a
snapshot and start the cursor again at that buffer's reflected count (and say, or show an empty screen, when the buffer
goes away), or the shells must reopen the stream on a wheel change. Which is the Developer's and the Tech Lead's to
weigh. Pin it with a test on `ProduceTerminalAsync` over a session whose backend's buffer is replaced: take, bytes,
hand back, take, bytes, and the frames carry the second terminal's bytes from its first one.

Developer's answer: _(the seat that built the work answers here)_

### 2. Typing into the Terminal pane while the protocol holds the conversation drops every keystroke with no word on screen, and leaves the session's input counter raised

**Where.** `TerminalPane.tsx` mounts the typeable terminal for every session, API mode included, with the wheel
button beside it; `interactive.ts` sends each keystroke as `POST /sessions/{sid}/prompt` with `appendEnter: false`;
the Director's `SendPromptCoreAsync` routes that to `Session.SendInput`, which under `_inputLock` does
`_inputInFlight++` and then `WriteRawInput`, where `ApiModeSessionBackend.Write` throws `NoTerminal` (by design) -
before the `try { AfterRawInput } finally { EndInput() }` that would decrement the counter.

**What happens.** Before any take, and after a hand back (when the pane still shows the dead terminal, finding 1),
the pane looks typeable. Each keystroke fails on the Director; the Cockpit reports it once per run as a background
error and shows nothing; the character is dropped. On the Director, `_inputInFlight` goes up by one per keystroke and
never comes down. What reads it: `SubmitDoorbellLineAsync` refuses to type while it is above zero, and the terminal
guarded send's busy rule; I did not trace whether an API-mode session ever reaches either (its doorbell and guarded
send branch to the host before those checks in the paths I read), so the counter's harm is unproven and I state it as
a leak, not a consequence.

**The harm.** A person who types into the Terminal tab of an API-mode session gets nothing and is told nothing; the
one sentence that would help ("take the wheel to type") is the Director's exception text, which never reaches the
screen. Low: the button beside the pane says what to do, and prompts go through the Chat.

**What would close it.** Either the pane renders no typeable terminal while the verdict's button is "Take the wheel"
(the verdict already says so; rule 7 keeps the decision on the Gateway), or the Gateway's prompt route refuses an
`appendEnter: false` send to an API-mode session whose holder is not `user` with that sentence. And `SendInput` should
raise the counter only once the write has succeeded, or decrement it on the throw.

Developer's answer: _(the seat that built the work answers here)_

## Notes, not findings

1. **A Director process ending with prompts held in the queue (a crash, or a clean exit whose refusals never reach
   the Gateway) leaves their receipts `Queued` on the Gateway for ever**, neither Delivered nor a stated reason. The
   queue is memory only; the crash journal row carries no prompt. A clean shutdown publishes a refusal for each
   (`EndWhileSuspendedLocked`, `RefuseQueuedLocked`) and the pusher writes the batch to the transport before `Publish`
   returns, so it reaches a connected Gateway; a Gateway unreachable at that moment, or a crash, loses it. For a
   Gateway prompt the delivery record is a second record (`OwnerProcessId` on the `delivering` line names a process
   that is gone). Ending such a receipt is the open item the phase 2b review's finding 2 and the requeue review's note
   6 already name; I do not raise it again.
2. **A lost `TurnEnded` with no later proof keeps "Take the wheel" disabled** ("A turn is running. Stop it..."), and
   Stop on an idle Pi answers accepted and changes nothing, so the button stays disabled until the next prompt's
   `TurnStarted` proves the turn over. Needs a hole in the push; bounded by the next prompt.
3. **The keystroke refusal's sentence** says "its protocol holds the conversation" also when the holder is `none`.
   Wording.
4. **`SendPromptAsync`'s queue reason** stays `turnRunning` for a prompt queued behind a turn that a take then cut
   short; the user then holds the wheel. Cosmetic; the re-announcement would carry the same word.
5. **The restore and continuation paths start a fresh Pi conversation** for an API-mode seat, as they do for every
   agent. Not this phase's; recorded so nobody expects the old conversation back after a restart.

## What the next seat need not re-check

- The ownership lock's every wait is bounded, and the two-lock order (ownership outside, host inside) holds in every
  caller I read, including `StopTurnAsync`, which releases the ownership lock before awaiting Pi.
- The Core and Gateway API-mode suites pass on origin/main at `20491d6ef` as run above.
- The buttons read the Gateway's verdict and nothing else; a terminal-mode row carries no verdict and shows no button.
