# Phase 4 review: a prompt whose write to Pi failed is refused where it failed, never queued again

Mission: API mode for agent wrappers, proven on Pi (issue #4070), phase 4. Pull request #4192, branch
`apimode/p4-requeue-after-failed-write`, reviewed at its head commit `e803edb70`, whose merge base is
`origin/main` at `58e563ba0`. Main has since moved by one commit (`c1e0bd3ce`, the licenses page), which
touches nothing under API mode. Reviewer seat (session 127), running a different agent family from the
Developer. Written 17 October 2026 on devlinux, in the worktree `../devthrottle-review-4192`.

**Verdict: the change holds. No finding proves harm.** The premise the pull request rests on is true on
main: a prompt write fails only once Pi's process has ended, and no restart can follow such a failure, so
a prompt put back in the queue could never have been written. The refusal therefore loses nothing a
resend would not also lose, and it gives the prompt its one receipt at once instead of a stale "queued"
story. Every prompt still gets exactly one receipt, no prompt is reported queued and then refused by the
same call, the other waiting prompts keep their places, and the tests are real. Once pull request #4094
(take the wheel) lands the premise still holds in substance; one narrow window, described in section 5,
is worth a sentence in that pull request's review, not a change here.

What follows is the scope, the premise checked line by line, the change itself, the view once #4094
lands, the tests, and two notes. The Developer owes no answer to the notes. Everything was checked against
the code on the branch and against runs I made, not against the pull request's description.

## 1. Scope: what I read, what I ran, what I could not reach

**Read, in full, on the branch:** the whole diff against `origin/main` (four files); in
`ApiModeSessionHost`: `TrySendPromptNowAsync`, `SendPromptAsync`, `StopAsync`, `ConnectAsync`,
`PumpAsync`, `OnRecordLocked`, `OnProcessEndedLocked` and its restart, `RefuseQueuedLocked`,
`TakeStepLocked`, `TakeEventsLocked`, `WriteQueuedPromptsLocked`, `WritePromptLocked`,
`WriteCommandLocked`, the new `TryWriteCommandLocked`, `ReconcileTurnStartTimerLocked`,
`OnTurnStartOverdueAsync`, `FeedErrorLinesLocked`, `RefusedByDirector` and the `Connection` class; in
`ApiModeTransport`: `SendRequestAsync`, `WriteRecordAsync`, `WriteLineAsync`, `Complete` and
`ShutdownAsync`; `ApiModeConnectionEndedException`; in `PiDialect`: `CanWritePrompt`,
`PromptsAwaitingTurn`, `StopReason`, `BuildPrompt`, `OnCommandWritten`, `OnProcessExited`,
`OnTurnStartOverdue`, `EndNeverStartedTurn`, `OnPromptResponse`, `OnAgentStart`, the turn end, the
parse-error path, `Refuse` and `RequireStop`, and every site that adds to or removes from `_inFlight`
and `_deliveredAwaitingTurn`; the two callers in `Session` (`SendApiModePromptAsync` and
`SendApiModePromptOnlyWhenWaitingAsync`); and the test rig (`FakeApiModeProcess.WriteLineAsync`,
`RecordingSink.Caller` and `PublishedFrom`).

**Read on the open branch of pull request #4094** (`apimode/p4-wheel`, head `5122ea878`): its whole diff
of `ApiModeSessionHost` - `SuspendAsync`, `ResumeAsync`, `EndWhileSuspendedLocked`, the `_suspended`
checks it adds to the three prompt write sites and to the turn-start restart, and its changes to
`OnProcessEndedLocked`.

**Ran, on devlinux with the .NET 10 software development kit at `~/.dotnet`, polling file watcher on,
one project at a time, after checking that no other test run was going:**

- `CcDirector.Core.UnitTests`, filtered to `ApiMode`, on the branch: 266 passed, 0 failed.
- The two new tests, three times each, with `ApiModeSessionHost.cs` swapped for the copy on
  `origin/main` and everything else from the branch: the first test
  (`...TheSendRefusesItAtOnceAndNeverQueuesIt`) failed every time; the second
  (`...ThatPromptIsRefusedAndTheOthersKeepTheirPlaceUntilTheEnd`) passed every time. The branch's host
  was then restored; the worktree is clean apart from this file.
- `CcDirector.Gateway.UnitTests`, filtered to `ApiMode`, built with `-p:RunWorkspaceTypecheck=false`:
  432 passed, 0 failed.

**Not run, not reached:** the full local gate (`scripts/test-local.ps1`); any real Pi process; the
Gateway's handling of the receipt beyond its existing unit tests (the pull request changes nothing
there, and I did not re-read the ingest). I did not run pull request #4094's tests or merge the two
branches together.

## 2. The premise, checked: a prompt write fails only after Pi's process has ended

`TryWriteCommandLocked` catches one exception type, `ApiModeConnectionEndedException`, and lets every
other one propagate exactly as `WriteCommandLocked` did before. The transport throws that type from
`WriteRecordAsync` in two places only:

- Before writing, under the transport's gate, when `_endedRecord` is already set. `_endedRecord` is set
  in `Complete`, in the same locked step that posts the `ApiModeProcessEnded` record to the events
  channel and completes it.
- From `WriteLineAsync`, when the process `HasExited` before the write, or when the write throws and the
  process `HasExited` by then. In both cases the method does not throw at once: it awaits `_ended.Task`,
  which `Complete` sets as its last act, after the end record is in the channel. Only then does it throw,
  carrying the same `ApiModeProcessEnded` record.

So when the host catches the exception, Pi's process has ended, the transport has already queued the end
for the host's event loop, and `ex.Ended` is the very record that loop will hand to `OnProcessEndedLocked`.
The pull request's reason text (`Pi exited (<ended.Describe()>)`) and the end's own text for the prompts
still in the queue (`Pi exited (<end.Describe()>)`) are built from one record, so they agree, which the
second test's two equal refusal reasons confirm.

A write that throws while the process has not exited (an `IOException` on a live pipe, an
`ObjectDisposedException`, the transport not running) is not caught here, on main or on the branch. That
is unchanged behaviour and outside this change.

## 3. The premise, checked: no restart can follow a failed prompt write

A restart happens in `OnProcessEndedLocked` only when `connection.RestartAfterEnd` is true. The only
writer of that flag is `OnTurnStartOverdueAsync`, which returns first unless
`connection.Dialect.PromptsAwaitingTurn.Count > 0`. `PromptsAwaitingTurn` is `_deliveredAwaitingTurn`,
which gains a prompt in exactly one place: `OnPromptResponse`, when Pi acknowledges a written prompt with
the disposition `started`. That prompt is already in `_inFlight`, put there by `OnCommandWritten` when
its line was written, and `OnCommandWritten` runs only after a write succeeds.

`CanWritePrompt` is `_connectionReadySent && !_exited && _stopReason is null && _inFlight.Count == 0`,
and all three prompt write sites (`TrySendPromptNowAsync`, `SendPromptAsync`,
`WriteQueuedPromptsLocked`) check it under the host's lock before writing. So while any prompt could be
or become "awaiting its turn", no prompt is written at all, and a write cannot fail.

I checked every site that removes from `_inFlight` for one that would empty it while a prompt still waits
in `_deliveredAwaitingTurn`, which would reopen `CanWritePrompt` while a restart could still be armed.
There is none:

- `EndNeverStartedTurn` (called by `OnTurnStartOverdue` and by `OnProcessExited`) clears
  `_deliveredAwaitingTurn` and removes the same prompts from `_inFlight` in one step, and in the overdue
  case `RequireStop` sets `_stopReason` in the same step under the same lock, which closes
  `CanWritePrompt` for the rest of this process. Nothing ever clears `_stopReason` (one assignment in the
  file, in `RequireStop`).
- `OnPromptResponse` removes a prompt on refusal, on `handled` and on an unknown disposition; none of
  those prompts was added to `_deliveredAwaitingTurn`.
- `OnAgentStart` moves the waiting prompts into the turn and clears `_deliveredAwaitingTurn`; the prompts
  stay in `_inFlight` until the turn ends.
- The turn end removes the turn's own prompts, which left `_deliveredAwaitingTurn` at `OnAgentStart`.
- `Refuse` removes the prompt it refuses; its callers are the exit, the parse-error path (a prompt still
  awaiting Pi's response, so not yet acknowledged) and `OnPromptResponse` as above.
- `OnProcessExited` clears everything, after which `_exited` closes `CanWritePrompt` anyway.

The failed-write prompt itself never reaches `OnCommandWritten`, so it is never in `_inFlight`, never in
`_awaitingResponse`, and never acknowledged: `OnProcessExited` cannot refuse it a second time, and the
end's sweep of `_awaitingReceipt` does not find it (its receipt was completed by the refusal). The first
test's `Assert.Empty(rig.Host.Defects)` and its single `PromptRefused` confirm both.

The restart itself starts a fresh `Connection` with a fresh dialect and a false `RestartAfterEnd`. A queued
prompt whose write to the new process fails is refused by this change, and the argument above applies to
the new process unchanged: nothing can arm a second restart without a successfully written prompt, and
none is written while any is in flight.

Conclusion: on main, a prompt put back at the head of the queue after a failed write could only ever be
refused at the end of the process. The pull request's statement of the premise is correct.

## 4. The change itself

- **One receipt per prompt.** In the two send paths the prompt is never added to `_queue`, so the end's
  `RefuseQueuedLocked` never sees it. In the drain, `WriteQueuedPromptsLocked` removes it from `_queue`
  before `WritePromptLocked`, and the branch no longer puts it back. The refusal completes its
  `_awaitingReceipt` entry through `TakeEventsLocked`; the end's sweep therefore does not touch it and
  records no defect.
- **No queued-then-refused flicker.** `SendPromptAsync` and `TrySendPromptNowAsync` emit `PromptQueued`
  only on the queueing branch, which is not the branch that writes. A prompt that was legitimately queued
  earlier and is refused when its write fails (the second test's second prompt) is a true queued-to-refused
  transition, not a flicker.
- **Positions.** Positions are stamped once, at queueing, from `_queue.Count`. Nothing in the change
  re-stamps them, and the remaining prompts are refused at the end in queue order as before. The second
  test asserts positions 1 and 2 and that no further `PromptQueued` appears.
- **The reason.** `FeedErrorLinesLocked` runs before the reason is chosen, as it does at the process end,
  so a stop reason carried by a standard error line that has already arrived wins over the bare exit
  description. The error cursor advances, so the end does not feed those lines twice. A stop reason found
  there calls `StopProcessAsync` on a process that has already ended, which the transport's
  `ShutdownAsync` answers at once with the recorded end: harmless.
- **`WriteCommandLocked` for everything else** is now a one-line wrapper with the same boolean contract;
  `StopAsync`, `ConnectAsync` and `TakeStepLocked` behave as before.
- **The callers.** `Session.SendApiModePromptAsync` turns the refused receipt into
  `TextSendOutcome.Refused(reason)`, and `Session.SendApiModePromptOnlyWhenWaitingAsync` turns it into a
  busy result carrying the reason. Both now get that answer at once instead of at the process end.

## 5. Pull request #4094 (take the wheel): does the premise still hold once it lands?

In substance, yes. #4094 adds a second way for a Pi process to end without the session ending: the take
closes Pi's input, and the hand back (`ResumeAsync`) starts a new process on the same conversation and
writes the prompts still queued. That is exactly the kind of path the brief asked me to look for: a
process end that is followed by a resume, where a prompt put back in the queue would later have been
written, and a prompt refused instead would not.

The take cannot itself make a prompt write fail. `SuspendAsync` takes the host's lock and sets
`_suspended = true` before it does anything to Pi. Every prompt write runs under that same lock, and on
the #4094 branch all three write sites check `!_suspended` before writing. A write that is in progress
when the take arrives holds the lock, so the take waits for it to finish before it can set the flag, and
only after the flag is set does the take stop the turn and close the input. The turn-start restart also
checks `_suspended` and `connection.Suspending`. So from the first step of a take no prompt line is
written, and a write cannot fail because of the take.

The one window left is not the take's doing: Pi exits of its own accord exactly as a prompt is being
written (the write fails, this change refuses the prompt), and the user's take reaches the host's lock
after that write releases it but before the host's event loop, which is already waiting for the same lock
with the end record in hand, gets to handle the end. In that ordering #4094's `OnProcessEndedLocked` sees
`_suspended`, keeps the rest of the queue, reports the process end as "the user took the wheel", and the
hand back later writes the kept prompts to a new Pi. On main's code the failed-write prompt would have
been among them; on this branch it has already been refused with "Pi exited before the prompt could be
sent", with its text, and the user can send it again. The receipt is honest and nothing is silent, the
window needs a crash and a take in the same instant, and a re-queue would reintroduce the stale "queued"
receipt this change removes for every ordinary case. I do not count it as harm this pull request must
answer. It is worth a sentence to the seat reviewing #4094, because in that same ordering the take also
reports a Pi that crashed as one the user stopped, which is #4094's story to get right, not this one's.

## 6. The tests

Both tests use the fake process, the injected clock and the scripted Pi: no real process, sleep, port or
clock. `ExitDuringWriteOf` exits the fake and fails the write with a broken pipe for one chosen line,
which is the shape the real race takes (the transport's `WriteLineAsync` then sees `HasExited` and waits
for the end). `RecordingSink.Caller` is an `AsyncLocal`, so a name set by the test flows into the send
and into every event the send publishes under the lock, and not into events the event loop publishes,
whose execution context was captured when the loop started. That is a sound way to say "this call
published that event", and the first test uses it to pin the refusal to the send.

The first test is a real regression test: with the branch's host swapped for main's it failed three times
out of three, at `PublishedFrom(refused)` as the pull request says. The second test is not a regression
test for the drained-prompt case: it passed three times out of three against main's host too, because on
main the re-queued prompt is refused at the end with the same reason, the same text and in the same order,
and the re-queue emits no second `PromptQueued`. It still earns its place as the guard for "the others
keep their places and are refused at the end", which is what its name claims. See note 1 for the one-line
change that would make it tell the two behaviours apart.

## 7. Notes, no answer owed

1. **Making the second test discriminate.** On the branch the drained prompt's refusal is published in its
   own batch, from the drain, before the end's batch that refuses the third prompt; on main both refusals
   arrive in one batch from `RefuseQueuedLocked`. `RecordingSink.Batches` already exists, so asserting
   that the two refusals are in different batches (or that the second prompt's refusal batch precedes the
   `ApiModeProcessEnded` handling's events) would make the test fail on main's host without weakening
   anything it asserts now.
2. **A sentence for #4094's reviewer.** The ordering in section 5 - a crash during a prompt write followed
   at once by a take - makes #4094 report a crashed Pi as "the user took the wheel". Whether that matters
   is that pull request's call.
