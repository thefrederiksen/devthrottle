# Phase 4 review: the silent end (pull request #4129)

Mission: API mode for agent wrappers, proven on Pi (issue #4070). Pull request #4129, "API mode phase 4:
an agent that ends with code 0 after a failure reads failed, with the reason", branch
`apimode/p4-silent-end`, reviewed at its head commit `9955e6f74` (two commits on base `ba4a44fa4`). It
answers finding 1 of `phase-1-phase-review.md`. Reviewer seat, session 145, opened by the Delivery Lead and
running a different agent family from the Developer. Written 17 October 2026 on devlinux, in a worktree cut
from the pull request branch at `../devthrottle-review-4129`, never the shared checkout.

**Verdict: accept with two findings for the Developer, neither of which undoes the change. The change does
what it says on the paths it names: the stream decides the end, a Pi that ends with code 0 in the middle
of a turn, before acknowledging a prompt, or after a resume that found nothing now reads `Failed` with the
reason, a clean idle end still reads `Exited`, and the failure colour cannot be painted over. Finding 1 is a
silent end that survives the change: a record the Director could not read while Pi had nothing in hand
still ends as a clean exit with code 0, and the pull request's own code comment says that path is covered.
Finding 2 is smaller: an idle Pi that dies with a non-zero code drops its standard error from the reason.
Both are proven by throwaway tests, written, run and deleted. The Developer who built the work decides what
to do with each; a finding here advises and does not command.**

## 1. Scope: what I read, what I ran, what I could not reach

**Read, in full, in the review worktree at the pull request's head:**

- The whole product diff (`git diff origin/main...HEAD -- src/CcDirector.Core/`, 332 lines):
  `ApiModeSessionBackend.cs`, `ApiModeSessionHost.cs`, `Pi/PiDialect.cs` and `Sessions/Session.cs`.
- The whole test diff (289 lines): `ApiModeSessionCreationTests.cs`, `ApiModeSessionHostTests.cs`,
  `ApiModeSessionTests.cs`, `PiDialectReplayTests.cs`, `PiDialectTests.cs`, `ScriptedPi.cs`, and the answer
  added to `phase-1-phase-review.md`.
- Around the diff, as the branch stands: `PiDialect.OnProcessExited`, `OnCommandWritten`, `OnStderrLine`,
  `OnStdoutRecord`, `OnResponse`, `OnState`, `RequireStop` and the state fields they write;
  `ApiModeSessionHost.PumpAsync`, `ObserveAsync`, `ReportEndAfterLoopFailureAsync`, `OnRecordLocked`,
  `OnProcessEndedLocked`, `ShutdownAsync`, `DisposeAsync`, `StartConnection`, `StopProcessAsync` and the
  `Connection` flags; `ApiModeSessionBackend` whole; in `Session.cs` the exit handler
  `OnBackendProcessExited`, `IsUnexpectedExit`, `KillAsync`, `SetActivityState`, the new
  `SetStatusColorCore` and every holder of `_wingmanEventsLock`; `SessionStatusWingman` (its mapping and
  the paint it writes when it first wires a session); `TransientErrorAutoResume`'s one colour write;
  `SessionManager.KillSessionAsync`, `KillAllSessionsAsync`, `OnSessionProcessExited`, `ShouldReapOnExit`
  and the backend choice in `CreateSession`; `ControlEndpoints` for which session fields reach the Gateway;
  `SessionOrdering`'s crash arm in the contracts; the test fakes `ScriptedPi` and `FakeApiModeProcess`.
- The phase 1 phase review in full (finding 1 and what it weighed), the phase 4 dispose-hang review, and
  the mission document's merge plan.
- Every search for other writers of the colour, other readers of `Crashed`, `EndReason` and the host's
  `Defects`, and every consumer of `SessionStatus.Failed` and `SessionStatus.Exiting`.

**Ran,** with the .NET 10.0.301 software development kit on devlinux, after checking that no other test run
was going (`pgrep -af 'dotnet.*test'` was empty each time), one project at a time, filtered, never a whole
suite:

1. The pull request's own set, once: `dotnet test src/CcDirector.Core.UnitTests --filter
   "FullyQualifiedName~CcDirector.Core.UnitTests.ApiMode"`. Result: 139 passed, 0 failed, 0 skipped,
   434 milliseconds. The same count the pull request reports.
2. Seven throwaway tests in one temporary file in the review worktree, run once, then deleted (`git status`
   empty afterwards; nothing of them is committed). Each is named below where it is used. Three asserted what
   the design says should happen and failed, which is what proves findings 1 and 2 and the weighed case in
   section 7; two asserted the pull request's claims and passed; two were probes that print the end state
   of a user stop and fail on purpose so the state is in the output.
3. A scan of both commit messages and the pull request body for assistant attribution: none.

**Could not reach:**

- No real Pi and no real Director; the recordings and the fake process stand in, as in every review of this
  mission. In particular I did not see a real `pi --mode rpc` answer `get_state` with a changed shape, so
  finding 1 is proven on the fake with one field removed, not on a Pi that does it.
- The Gateway's side. `Status`, `Crashed` and `LastStatusReason` ride the session row (`ControlEndpoints`),
  and the contracts' fold reads `Crashed` for its crash arm, so the verdict reaches the fleet by
  construction; I did not run the Gateway unit tests the pull request ran (272, API mode classes), nor the
  three `CcDirector.Core.Tests` classes it ran (57).
- Hand back to the terminal ("the user takes the wheel"). It is not on this branch:
  `SessionConnectionModeWords` says the user's wheel is phase 4 and the code has no such path yet. What a
  hand back will read is reasoned from the rule, in section 3, not observed.
- Windows and macOS.
- The branch is one commit behind `origin/main` at review time (`a2994b3f8`, two test files in other
  projects; no file overlaps the pull request's).

## 2. Question 1: does a genuinely clean end still read clean

**Yes for the idle ends; a stop with a prompt in hand reads Failed, which is the terminal rule's reading of
the same event.** The dialect's rule is `EndedInFailure = _stopReason is not null || _turn is not null ||
_deliveredAwaitingTurn.Count > 0 || unacknowledged.Count > 0`, and the host adds a failed restart and a
failed event loop. So:

- **Normal exit, nothing in hand.** Pi waiting for a prompt, its input closed: nothing is set, the reason is
  "Pi exited (exit code 0)", the session reads `Exited`, `Crashed` false. Pinned by the pull request's
  `CreateSession_ApiModePiIdleEndsWithExitCodeZero_SessionExitsCleanly` and
  `OnProcessExited_IdleWithNothingInHand_IsNotAFailure`, both read.
- **User stop while idle.** `Session.KillAsync` sets `Status` to `Exiting`, the backend's
  `GracefulShutdownAsync` reaches `ApiModeSessionHost.ShutdownAsync`, which marks the connection
  `ShuttingDown` and closes Pi's input; Pi exits 0 with nothing in hand. My probe
  `Session_UserStopsIdle_ReadsExited` (a `plain` Pi, `KillAsync(1000)`) printed: status `Exited`, crashed
  false, exit 0, end reason "Pi exited (exit code 0)". Clean.
- **User stop with a prompt written and not yet acknowledged.** My probe
  `Session_UserStopsWithAPromptUnacknowledged_ReadsFailed` (a `plain` Pi with answers held back, the prompt
  written, then `KillAsync`) printed: status `Failed`, crashed true, exit 0, colour `error`, reason
  "failed: Pi exited before acknowledging (exit code 0)", the send answered not delivered. A stop while a
  turn runs reads the same way through `_turn`. **Before this change that stop read clean**, but only by the
  accident finding 1 described: the exit-synthesised `TurnEnded` moved the session to waiting and the exit
  code was 0. A terminal session stopped while working reads `Crashed` too: `IsUnexpectedExit(exitCode,
  wasWorking)` consults neither `Status == Exiting` nor who asked, and nothing on the exit path does. So this
  is not a regression against the product's rule; it is the rule now applied to API mode. Whether a stop the
  user asked for while work was in hand should read `Failed` at all is a design question for the Delivery
  Lead, and it is the same question for both modes; I do not raise it as a finding. One consequence for the
  roster: a `Crashed` session is never auto-reaped (`SessionManager.OnSessionProcessExited`), so a session
  the user stopped mid-turn now stays as a red row until closed by hand, as a terminal one does.
- **Hand back to the terminal.** Not built yet. By the rule above, a hand back that closes Pi's input while
  Pi is idle reads clean; one that does it with a prompt in hand reads `Failed`. The seat that builds the
  hand back should either stop Pi only while nothing is in hand, or carry the intent to the dialect. Noted
  for that seat, not a finding here.

## 3. Question 2: can a failure still end silently on any path

**One can, and I proved it. Finding 1 below.** The other paths named in the brief:

- **Pi killed.** Exit code 137 (or any non-zero code): `IsUnexpectedExit` marks the crash as before, and the
  reason now reads "failed: Pi exited (exit code 137)" from the stream, or the turn's reason if one ran.
  Pinned by the updated `PiDialectReplayTests` lines and the `exit` recording tests.
- **Resume found nothing.** `_stopReason` is set by `RequireStop` from `OnState` (message count 0) or
  `OnStderrLine` (the "No project session found" warning), so the end is a failure whatever the code, and the
  reason now ends ". Pi exited (exit code 0)" with the standard error tail when there is one. Pinned by
  `CreateSession_ApiModePiResumeFindsNothing_SessionEndsWithTheReason`, which now asserts `Failed`, `Crashed`,
  `ExitCode` 0, `EndReason` and `LastStatusReason`, and by the two new dialect tests.
- **Never acknowledged.** `unacknowledged.Count > 0` makes it a failure, and the reason carries the code and
  standard error. Pinned by `OnProcessExited_BeforeAcknowledgement_EndReasonCarriesTheExitCodeAndStandardError`
  and by my probe above through the whole session.
- **Standard error only.** Pi writing to standard error and running on is not an end, so there is nothing
  to show. Pi writing to standard error and dying idle with a non-zero code is a failure (the code), but the
  reason drops the standard error: finding 2. Pi writing to standard error and ending idle with code 0 reads
  clean with the tail on the `ConnectionEnded` event only; see section 7 for why I weighed and did not raise
  that.
- **The Director's own failures.** A restart that could not start Pi and an event loop that failed both set
  `failure = true` in `OnProcessEndedLocked`. Read, not tested by the pull request (section 6), and I did not
  write a throwaway for either because the code path is three lines and unconditional.
- **The backend's guard.** `ApiModeSessionBackend.OnHostExited` throws if `Ended` has not completed. That
  throw lands in `ObserveAsync`'s catch, is logged, and `ProcessExited` is never raised, so if the guard ever
  fired it would produce exactly the silent end this change removes. It cannot fire: `ObserveAsync` sets
  `_ended` on the line before it raises `Exited`, and `DisposeAsync` never touches `_ended`. Holds by
  construction; noted so the next reader does not have to re-derive it.

### Finding 1 (proven): a record the Director could not read while Pi had nothing in hand ends clean

**Where.** `ApiModeSessionHost.OnRecordLocked`, the catch for `KeyNotFoundException`,
`InvalidOperationException` and `FormatException` out of `PiDialect.OnStdoutRecord`. It logs, adds a defect,
sets `connection.ShuttingDown = true` and calls `StopProcessAsync(connection, "the Director could not read
Pi's record: ...")`. The reason goes to the log only. The dialect is told nothing: `_stopReason` stays null,
because `RequireStop` is only called from `OnState` and `OnStderrLine`.

**What happens.** Pi's input is closed and it exits 0. `PiDialect.OnProcessExited` finds `_stopReason` null,
no turn, no delivered prompt, no unacknowledged prompt, so `EndedInFailure` is false and the reason is
"Pi exited (exit code 0)". `OnProcessEndedLocked` takes `failure` from the dialect, and neither `loopFailed`
nor the restart arm applies, so the host's `EndedInFailure` is false too. The session reads `Exited`,
`Crashed` false, with no reason that says anything went wrong.

**Proof.** Throwaway `Host_UnreadableGetStateAnswerWhileIdle_EndsInFailure`: a `plain` Pi whose `get_state`
answer had `"success":true,` removed (the shape `OnResponse` reads with `GetProperty("success")`), started in
a host exactly as `ApiModeSessionHostTests.StartHost` does. The host ended; the assertion that
`host.EndedInFailure` is true failed with: `EndedInFailure=False; reason='Pi exited (exit code 0)'; exit=0;
defects='the dialect could not read record 1: The given key was not present in the dictionary.';
ready=False`.

**Why it is reachable.** The `get_state` answer is read before any prompt can be written (`CanWritePrompt`
waits for `ConnectionReady`), so Pi has nothing in hand by design at the one record every session reads
first. A Pi whose `get_state` shape changed (a field renamed or moved, which is what the dialect's contract
says the throw means) ends every session this way, on start and on every restart. The same holds for any
record the dialect throws on between turns.

**The harm.** The session row shows a plain grey "exited" with no reason; `Status` and `Crashed` carry a
clean end to the Gateway's fold; and because an API-mode session is created with `SessionBackendType.ConPty`
(`SessionManager.CreateSession`, the backend choice) and the exit code is 0, `ShouldReapOnExit` is true and
`OnSessionProcessExited` removes the row three seconds later (`CleanExitReapDelayMs`). The session is gone
from the roster and the user who created it sees nothing. The one record of what happened is the host's
`Defects` list, which nothing outside the API mode folder reads, and the Director log. This is the shape
finding 1 of phase 1 described, on a path that finding named ("a record the dialect could not read"), and
the pull request's own comment on `PiDialect.EndedInFailure` lists "a record it could not read" among the
covered cases. It is covered only when a turn happens to be running at that moment.

**Why it must change.** The event model's rule for this path (the dialect's class comment: the transport
"stops this Pi process and calls OnProcessExited, which still refuses every prompt ... so rules 1 to 3 hold")
is kept, but the mission's section 7 promise, "the session shows failed with the reason", is not. The host
already has the reason in hand when it stops Pi. Two shapes, for the Developer to choose: tell the dialect
(a `RequireStop` equivalent the host may call before `StopProcessAsync`, so `_stopReason` carries the reason
and the end is a failure in the stream's own words), or fold it in the host the way `loopFailed` is folded
(a flag on the connection set in that catch, read in `OnProcessEndedLocked`, appended to the reason and
setting `failure = true`). Either way the throwaway above is the test to keep, with the assertion the other
way round, plus one through `CreateApiModeSession` pinning `Failed` and the reason on the session.

### Finding 2 (proven, small): an idle Pi that dies with a non-zero code drops its standard error from the reason

**Where.** `PiDialect.OnProcessExited`, the last arm of `reason`: `$"Pi exited (exit code {exitCode})"`, with
no `stderrDetail`, while the three failure arms above it append it.

**Proof.** Throwaway `Dialect_IdleNonZeroExitWithStandardError_ReasonCarriesIt`: a ready dialect, one
standard error line "Error: ENOSPC no space left on device", then `OnProcessExited(1)`. The assertion that
the reason contains "Standard error" failed; the reason is "Pi exited (exit code 1)". The line is on the
event's `StderrTail`, so the stream has it; the reason does not.

**The harm.** The session row reads "failed: Pi exited (exit code 1)" on the one path where the exit code is
the only evidence the dialect has, and the standard error is the only other evidence Pi left. The pull
request body says "every failed ConnectionEnded reason carries exit code and standard error tail"; this one
does not. One line fixes it (append `stderrDetail` to the last arm), and
`OnProcessExited_IdleWithNothingInHand_IsNotAFailure` keeps its exact-string assertion because that test has
no standard error line.

## 4. Question 3: does the final crash colour break any rule that clears it

**No, and nothing clears it, by design and by construction.**

- `_statusColorFinal` is set once, in `SetStatusColorCore` with `final: true`, which only the crash arm of
  `OnBackendProcessExited` passes. No code resets it. A `Session` is never restarted or resumed in place:
  `Session.cs` has no restart, resume or reattach method, and every reopen, restore and restart path goes
  through `SessionManager.CreateSession` and makes a new `Session` (five `new Session(` sites, all creations).
  So "a restart or resume of the same session" is a new object with a fresh flag.
- The writers that could paint over it are the Wingman's mapping (`SessionStatusWingman.WireSession`'s first
  paint and its activity handler) and `TransientErrorAutoResume`'s give-up red. Both are now dropped after a
  crash. Proven by the pull request's `SetStatusColor_AfterTheSessionFailed_IsDroppedAndTheFailureAndItsReasonStay`
  and by my throwaway `Session_AfterFailure_WingmanInitPaintIsDropped`, which after a mid-turn exit 0 wrote
  the Wingman's `isNew` paint and then a `PositiveEvidence` red and found the colour still `error` with the
  reason still "failed: ...". Passed.
- **Terminal-mode sessions are touched, deliberately and harmlessly.** The flag applies to every crash, not
  only API mode. For a terminal crash the Wingman's grey paint already happened before the red (it runs
  synchronously inside `SetActivityState(Exited)`, which the exit handler calls before the colour), so the
  red already won on main; the new flag only adds that nothing later can repaint it. I found no legitimate
  colour write after a terminal crash: the terminal detector writes activity, not colour; the auto-resume's
  give-up is a red that the final red already says. `MarkForDeletion`'s grey write was deleted long ago
  (defect 23, as the Wingman's class comment records).
- **Locking.** `SetStatusColorCore` takes `_statusColorLock` and, inside it, `_wingmanEventsLock`. The two
  other holders of `_wingmanEventsLock` (`RecentWingmanEvents` and `ClearWingmanContext`) take no other lock,
  so the order cannot invert. `OnStatusColorChanged` is raised outside the lock, so a handler that calls back
  into the session cannot deadlock. The `old == color && LastStatusReason == newReason` early return still
  skips the event, as before; the final flag is set before that return, so a repeated crash write is still
  final.
- The Gateway's crash arm reads `SessionDto.Crashed`, not the colour (`SessionOrdering`, as its comment
  says), so the fold is unaffected by colour either way; the colour matters to the standalone desktop and
  the few readers the Wingman's class comment lists.

## 5. Question 4: the issue 959 rule

**It fires now, and it is not what decides the verdict.** `OnApiModeEvents` skips the move to
`WaitingForInput` for a `TurnEnded` in a batch that also holds a `ConnectionEnded`. The dialect emits the
exit-synthesised `TurnEnded` and the `ConnectionEnded` in one step, and `OnProcessEndedLocked` publishes
that step as one batch, so the pair always travel together; a turn that ended on `agent_settled` is its own
record and its own batch, so it is never caught by the rule. The host raises `Exited` after the last batch
has reached `Published`'s handlers, so by the time `OnBackendProcessExited` reads `ActivityState` the batch
has been applied.

Proof: throwaway `Session_PiEndsMidTurnWithZero_ActivityGoesWorkingToExitedWithNoWaitBetween`, the `exit`
recording with exit code 0, recording every activity transition. The last transition was `Working->Exited`
and no `Working->WaitingForInput` occurred. Passed. So `wasWorking` is true and `IsUnexpectedExit(0, true)`
would mark the crash even without `streamSaysFailed`. Belt and braces, both fastened.

Two interactions I checked and found sound:

- The restart path. When the turn-start bound runs out, `OnTurnStartOverdue` emits `TurnStarted` and
  `TurnEnded` in their own batch (no `ConnectionEnded`), so the session goes to waiting, then the stuck Pi's
  end is a batch with only refusals and the `ConnectionEnded`, then the new Pi's `ConnectionReady` sets
  waiting again. Nothing in the rule misreads a restart as a crash.
- A never-started turn at exit. The dialect emits `TurnStarted`, `TurnEnded`, `ConnectionEnded` in one
  batch; the `TurnStarted` still sets `Working`, the `TurnEnded` is skipped, so the exit is read while
  working, consistently with the stream's own verdict.

One coverage note, in section 6: the pull request has no test that pins the 959 path on its own, because
every new test passes through `streamSaysFailed` as well.

## 6. Question 5: are the tests real proofs

**Yes, for what they claim.** Each new test drives the real code (the recordings through the fake process,
or the dialect directly) and asserts the specific verdict, the reason string, `Crashed`, `ExitCode`,
`EndReason` and `LastStatusReason`, not a disjunction. The weak assertion phase 1 called out (`Status is
Exited or Failed`) is gone. The `ScriptedPi.ExitCode` override is the honest way to make the recorded kill
read as code 0, and it is used only where the test says so. The five rewordings of existing assertions match
the new reason strings exactly, and I read each.

What is not covered, for the Developer to weigh:

- The host's own two failure arms (`loopFailed`, a restart that could not start Pi) set
  `EndedInFailure`, and no test reads `ApiModeSessionHost.EndedInFailure` on either path. A search of the
  host, session and creation test files for "Restarting Pi failed", "event loop failed" and
  `EndedInFailure` finds nothing: neither arm is exercised by any test on the branch.
- No test pins that the exit-synthesised `TurnEnded` keeps the session `Working` (section 5). Every new
  session-level test would still pass if that change were reverted, because the stream's verdict decides.
  My throwaway above is the shape of the missing test.
- Finding 1's path has no test, and finding 2's path has one that happens to have no standard error line.

## 7. Weighed and not raised

- **An end with code 0 before Pi named its conversation, with only `get_state` pending, reads clean.**
  Throwaway `Dialect_ExitZeroBeforeReady_IsReported`: `get_state` written, `OnProcessExited(0)`;
  `EndedInFailure` false, reason "Pi exited (exit code 0)". The general shape of finding 1: the dialect
  cannot tell a stop the user asked for from a stop the Director made for a reason of its own, or from a Pi
  that ended by itself, because it is never told who closed the input. Standing alone it is reachable only
  when the Director closes the input (the user's stop, which should read clean, or the catch of finding 1)
  or when Pi ends by itself with code 0, which Pi does only on input close. So the reachable instance is
  finding 1 and this is not a second finding.
- **Standard error with an idle exit 0.** Covered by the same reasoning: without a failure signal from the
  Director, an idle exit 0 is a clean end, and the tail is on the event for anyone who reads the stream.
- **Two reasons for one end.** In `OnProcessEndedLocked` the `ConnectionEnded` is published (and reaches
  the stream and the Gateway) before the `loopFailed` and restart-failed arms append their text, so the
  published event's reason lacks ". The Director stopped Pi because its event loop failed" or ". Restarting
  Pi failed: ..." while `Session.EndReason` has it. The restart arm did this on main before this change; the
  loop arm is new and follows it. No harm proven beyond the stream and the row disagreeing on two rare paths;
  noted for whoever next touches that method.
- **`ExitCode` is now assigned last.** Any reader that saw `ExitCode` set and then read `Status` or
  `Crashed` would have raced before; now it sees the whole verdict. I found no such reader, so this is
  tidiness, correctly done.
- **The pull request edits the phase 1 review to record its answer.** That is where law 11 says the answer
  goes.
- **Attribution.** None in either commit message or the body.

## 8. For the record

Throwaway tests written, run and deleted in `../devthrottle-review-4129` (none committed, worktree clean
after): `Host_UnreadableGetStateAnswerWhileIdle_EndsInFailure` (failed: finding 1),
`Dialect_IdleNonZeroExitWithStandardError_ReasonCarriesIt` (failed: finding 2),
`Dialect_ExitZeroBeforeReady_IsReported` (failed: section 7, weighed),
`Session_PiEndsMidTurnWithZero_ActivityGoesWorkingToExitedWithNoWaitBetween` (passed: section 5),
`Session_AfterFailure_WingmanInitPaintIsDropped` (passed: section 4),
`Session_UserStopsWithAPromptUnacknowledged_ReadsFailed` and `Session_UserStopsIdle_ReadsExited` (probes
that print the end state: section 2).
