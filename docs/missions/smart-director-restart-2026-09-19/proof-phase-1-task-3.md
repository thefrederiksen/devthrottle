# Proof - Smart Director Restart, phase 1, task 3

The smart shutdown run: the time allowed, two stages, the limit, Shut down now, progress per session.

Branch `smart-restart/p1-engine`, code commit `344bffd77`. The work was done and both revert proofs were
run on `c8b85a059`, cut from `origin/main` at `9f79e92dc`. `origin/main` then moved four commits and one
of them touched `ControlApiHost.cs`, so before the first push the branch was rebased onto `origin/main`
at `912340ed8` (no conflict) and the code commit became `344bffd77`. The check was REBUILT and run again
on the rebased branch: the last row of the table. The revert proofs were not repeated after the rebase.

## The check

    dotnet test src/CcDirector.Gateway.UnitTests --filter "FullyQualifiedName~Drain|FullyQualifiedName~Restart"

| When | Passed | Failed | Total |
|---|---|---|---|
| Baseline, my own run on the untouched worktree at `9f79e92dc` | 487 | 0 | 487 |
| After the engine change, BEFORE any new test existed (the older tests, untouched) | 487 | 0 | 487 |
| After, on `c8b85a059`, built from source in the same command | 517 | 0 | 517 |
| After both revert proofs, restored and REBUILT in the same command | 517 | 0 | 517 |
| After the rebase onto `origin/main` at `912340ed8`, on `344bffd77`, REBUILT in the same command | 517 | 0 | 517 |

517 - 487 = 30, and 30 is the number of new test cases counted by hand below. The baseline is 487 and
not the 477 of `proof-phase-1-task-1.md` because the restart cycle work (issue 3169) merged in between.

**No existing test was edited or removed.** `DrainTestRig.cs` was added to (a journal of calls, a
mid-turn set, a handover written only on the second message, a capture that can fail); nothing in it
was changed, and the second row of the table is the older tests passing on the new engine before any
of my tests existed.

The whole `CcDirector.Gateway.UnitTests` project, run once because the session control seam and the
host changed: **6,519 passed, 0 failed, 8 skipped, 6,527 in all**, 4 minutes 50 seconds. That run was
on `c8b85a059`, BEFORE the rebase, and was not repeated after it. The harness
`tools/harnesses/drain-index-diff`, which implements the seam, builds with no warnings.

Everything ran in the foreground. My first baseline run was piped through a tail, which hides output
while it runs; every run after it was not.

## What was built

1. **The time allowed as an option.** `DrainOptions.SmartShutdown` (`SmartShutdownDrainOptions`: the
   time allowed, the "Shut down now" token, the wait after the ending, the snapshot handler). Null is
   the older drain. Every new branch in `DirectorDrain` reads that one field.
2. **Stage one at two thirds** (`InterruptAndAskAgainAsync`). Every session still present with no
   drain state is sent `DrainMessages.HandOverNow`; one that is mid-turn is interrupted first. "Mid-turn"
   is a new question on the seam, `IDrainSessionControl.IsMidTurn`, answered by the real seam from
   `ActivityState.Working` - the same fact the deletion reaper uses, so the word means one thing.
3. **The limit** (`EndEverySessionStillPresentAsync`). One last collect, then every session still present
   is marked, the record is SAVED, each is ended leaf first, the record is saved again, and the run
   waits (one minute at most) for each to be verifiably absent.
4. **Shut down now.** A cancellation token, so the ten second wait between polls ends at once. Honoured
   from the asking phase, the collecting phase and stage one; once.
5. **Progress.** The drain builds a complete `SmartShutdownSnapshot` on every change and once per poll.
   Rows are leads first, each followed by the sessions under it, at every depth. The words are in one
   place, `SmartShutdownWords`. `DrainProgress` still works for the older path and is untouched.
6. **`DirectorSmartShutdown`** (`ISmartShutdown`) with `CheckAsync` and `Start`, and
   **`ControlApiHost.CreateSmartShutdown()`**, never null. The run asks `CheckAsync` itself as its first
   step, so the dialog and the run cannot disagree about the Gateway.
7. **The mark.** `ShutdownKind = smart-shutdown` goes on the document straight after the capture, and the
   record is saved with it BEFORE anything is asked (the older path's first save is after the asking).

Not built, as the mandate says, and each says so in plain words instead of pretending:
`CancelAndKeepWorking` (ignored and logged; `CanCancel` is false in every snapshot),
`ShutDownIgnoringAllAsync`, `RecordAndLetEndAsync` and `Start` with `Purpose = Restart` (each throws
`NotSupportedException` and touches nothing).

## Decisions I made that the mandate did not spell out - say if any should change

- **A session that HAD handed over and is still present at the limit keeps "drained".** The mandate
  says every session still present is recorded `ended-at-limit`. I did that for every session that had
  NOT handed over. A session already "drained" or "covered" (waiting for the reaper, or held by the leaf
  first gate behind a slow session under it) is ended too, so the Director is empty, but keeps its
  state, its restore answer and its handover. Recording it `ended-at-limit` would throw away a clean
  handover and, by mission 10.3, offer it back as "ended without a handover", unticked. Tested.
- **A session that said "blocked" or "declined" IS recorded `ended-at-limit`**, with its blocked reason
  and its document kept, and a line in the record saying what it had declared.
- **A refused request does not make a session final.** The older path records such a seat unreachable at
  once, because it has nothing else to try. The smart path shows `NotDelivered` with the reason at once,
  tries again at two thirds, and ends it at the limit.
- **A smart shutdown waits for the limit whenever anything is still present**, even if everything left is
  blocked or wedged. Sessions were told "you have N minutes"; the owner has Shut down now.
- **Sessions that appeared after the capture are ended too**, with a line in the record saying they are
  described nowhere. The application is about to close over them either way.
- **`Emptied` is decided by one presence question**: is the live session list empty at the very end. A
  session that will not die gives `Failed` with the stop path's reason, never `Emptied`.
- **A session that went away by itself** during the run is recorded gone on the next poll (row `ShutDown`
  with a detail saying it wrote nothing), instead of showing "asked" until the limit.
- **The reap timeout does not apply to a smart shutdown**: the limit is its answer to a flagged session
  that will not go, so it never writes "it was NOT forced" about a session it is about to end.
- **The clock starts when the run starts**, not when the last request lands. With many leads the last one
  asked has a few seconds less than the message says.
- **Row priority**: gone, then handed over, then interrupted, then writing, then not delivered, then
  asked, then pending. A session interrupted while a half-written file exists shows `Interrupted`.

## Each new test, in one sentence

All in `SmartShutdownRunTests`, namespace `CcDirector.Gateway.UnitTests.Drain`. Every one runs the real
`DirectorSmartShutdown` over the real `DirectorDrain`; only the sessions and the Gateway store are fakes.
No test builds a snapshot by hand. The run is held at its first step until the handler is attached, so
every snapshot the engine raises is seen.

- `Start_ASessionThatHandsOverInTime_IsClosedAndNeverInterruptedOrEnded`: a session that writes its
  handover is flagged and verified gone, never interrupted, never ended; every save carries the smart
  shutdown mark; the request said ten minutes and did not promise it would not be killed.
- `Start_ASessionMidTurnAtTwoThirds_IsInterruptedAndAskedAgain_ThenHandsOverAndIsNotEnded`: a session
  mid-turn is interrupted BEFORE the short message, at exactly 400 seconds of ten minutes, its row says
  interrupted, it then hands over, is closed the ordinary way, and is not ended.
- `Start_ASessionThatCannotBeInterrupted_IsStillAskedAgain_IsNeverShownInterrupted_AndIsEndedAtTheLimit`:
  the Pi case - the refusal is on the row in the driver's words, the row is never `Interrupted`, the
  short message is still sent, and the limit ends it.
- `Start_ASessionThatNeverAnswers_IsEndedAtTheLimit_AndTheRecordNamingItWasSavedBeforeItWasEnded`: asked
  again without an interrupt, ended at exactly ten minutes, saved as `ended-at-limit` with its
  conversation id on the SAVED record, and the journal of calls shows that save before the end and
  another after.
- `Start_ASessionWithAHalfWrittenHandoverAtTheLimit_IsEndedAndWhatItWroteIsKept`: a file too short to be
  a handover shows the row as writing, and its path is on the seat when it is ended.
- `Start_ASessionThatHandedOverButIsStillPresentAtTheLimit_IsEndedAndKeepsItsHandover`: the first
  decision above.
- `Start_ASessionThatWillNotDie_EndsFailedNamingIt_NotEmptied`: a stop that is refused gives `Failed`
  with the reason, no close time, and a row that is not called ended.
- `Start_ASessionThatAppearedAfterTheCapture_IsEndedTooAndTheRecordSaysSo`: as named.
- `Start_ALeadWithASessionUnderIt_ClosesTheLeafFirst_AndListsTheLeadFirst`: the session under the lead
  is flagged first; every snapshot lists the lead first with its session under it and names its owner;
  only the heads were asked; the session under the lead is shown as pending.
- `Start_ALeadAndTheSessionUnderItThatNeverAnswer_AreEndedLeafFirst`: at the limit the order is leaf
  first too, and the session under the lead did get the short message at two thirds.
- `Start_AWedgedSession_ShowsNotDeliveredWithTheReasonAtOnce_AndIsEndedAtTheLimit`: `NotDelivered` with
  the delivery path's reason in a snapshot raised with no time passed, and ended at the limit.
- `ShutDownNow_FromTheCollectingPhase_EndsEverySessionStillPresentAtOnce_AndOnlyOnce`: pressed at thirty
  seconds, everything is ended at thirty seconds, nobody interrupted or asked again, the second press
  does nothing, and every later snapshot says the button is spent.
- `ShutDownNow_BeforeAnythingIsAsked_AsksNobodyAndEndsEverySession`: pressed before the first step, no
  request is ever sent and every session is recorded with its conversation id and ended.
- `Start_AcrossOneRealRun_EveryStateAndPhaseIsReached_AndEverySnapshotIsWhole`: six kinds of session in
  one run reach all eight states reachable without a cancel and the phases in order; every snapshot's
  total, gone count, count label, phase label, state labels, two times and `CanCancel` are checked.
- `Start_WhileNothingChanges_StillRaisesASnapshotEveryPoll`: thirty polls, a new snapshot between each.
- `Start_AChangedHandlerThatThrows_DoesNotStopTheRunOrTheNextHandler`: as named.
- `Start_EachAllowedTime_InterruptsAtTwoThirdsAndEndsAtTheLimit` (5): for 5, 10, 15, 30 and 60 minutes
  the interrupt is at 200, 400, 600, 1200 and 2400 seconds and the end at the limit, by the rig's clock
  at the moment of each call, and the snapshots name the same two moments.
- `Drain_WithNoSmartShutdownOptions_IsTheOlderDrain_NinetyMinutesAndNothingForced`: default options, a
  session mid-turn throughout - the mid-turn question is never asked, nothing is interrupted or ended,
  the default ninety minutes ends it unreachable and running, no mark, the older words.
- `CheckAsync_TheGatewayCannotBeReached_RefusesWithTheReason`: as named; the restart answer still comes
  from the eligibility answer.
- `CheckAsync_NoGatewayClient_AndALauncherThatWouldNotRestartThis_SaysBothAndWhy` (2): for "no" and for
  "could not tell", the restart refusal is the eligibility reason word for word.
- `CheckAsync_TheGatewayAnswers_AllowsIt_AndTouchesNothing`: as named.
- `Start_TheGatewayCannotBeReached_IsRefusedAndTouchesNoSession`: `Refused`, nothing sent, renamed,
  flagged, interrupted, ended or asked, nothing captured, nothing saved.
- `Start_WithARunAlreadyUnderWay_ThrowsSayingSo_AndTheFirstRunIsUnharmed`: as named.
- `Start_WithADrainAlreadyRunning_ThrowsNamingIt_AndTouchesNothing`: as named.
- `CreateSmartShutdown_OnAHostWithNoGatewayClient_IsTheRealEngine_AndItsCheckSaysWhy`: watches the HOST.

## The revert proofs

Both done AFTER the code was committed (`c8b85a059`), one at a time, each with a full build and the
WHOLE check (no narrowed filter, never `--no-build`). Restored each time with
`git checkout -- src/CcDirector.ControlApi/Drain/DirectorDrain.cs`.

| What was undone | Result | What went red |
|---|---|---|
| Two thirds: the stage's condition changed so it never fires | **10 failed, 507 passed, 517** | The two thirds test, the Pi test, all five allowed-time cases, the all-states run, the never-answers test and the ended-leaf-first test (the last two assert the short message was sent) |
| The `SaveAsync` before the first `EndAsync` deleted | **1 failed, 516 passed, 517** | `Start_ASessionThatNeverAnswers_IsEndedAtTheLimit_AndTheRecordNamingItWasSavedBeforeItWasEnded`, and nothing else |

After the second restore: no difference against `c8b85a059`, both lines confirmed back in the file by
search, REBUILT, whole check: 517 passed, 0 failed.

I ran the second proof at quiet verbosity, which does not print the assertion message, so I know WHICH
test went red and not which of its assertions. Both candidates (the order, and the close time being
absent in that save) are the rule under test.

## What I could not reach

- **No real agent and no real Director.** The rig shows the interrupt is asked for at the right moment,
  not that an agent mid-turn stops and writes. Mission section "Not verified" lists the same.
- **`IsMidTurn` on the real seam has no test.** It is one expression over `ActivityState.Working`; the
  tests here watch the fake. A test over a real `SessionManager`, like task 1's verb tests, would close it.
- **Nothing in the product calls `CreateSmartShutdown` yet.** Phase 2's screens are the caller.
- **The real Gateway question** (`ListWorkspacesAsync` as the reachability check) is only exercised as
  "no client at all". A Gateway that is configured and does not answer is tested through the seam.
- **A real Gateway refuses `ended-at-limit` until it is deployed** (interface document section 5). On an
  older Gateway the save before the ending would throw and the run would end `Failed` having ended
  nobody - safe, but it would not shut anything down.
- **Snapshots raised between `Start` returning and a screen attaching its handler are missed** by that
  handler. `Current` is always whole, so a screen should read `Current` after attaching.
- **`Start` returns at once** is held only by construction (`Task.Run`), not by a timing test.
- **Cancellation of the drain's own token** is never used by the run; nothing here cancels a run except
  the two buttons, one of which is not built.
