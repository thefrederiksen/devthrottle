# Proof - Smart Director Restart, phase 1, task 4

Cancel and keep working, Shut down and ignore all, the operating system shutdown record, and the restart
purpose.

Branch `smart-restart/p1-cancel-ignore-restart`, code commit `5000c28ed`, cut from `origin/main` at
`aa8b22912`. Not rebased. No pull request was opened.

## The check

    dotnet test src/CcDirector.Gateway.UnitTests --filter "FullyQualifiedName~Drain|FullyQualifiedName~Restart"

Every run in the foreground, built from source in the same command, never `--no-build`.

| When | Passed | Failed | Total |
|---|---|---|---|
| Baseline, my own run on the untouched worktree at `aa8b22912` | 518 | 0 | 518 |
| After, on `5000c28ed` | 540 | 0 | 540 |
| Revert proof one (the cancelled mark removed), REBUILT | 538 | 2 | 540 |
| Revert proof two (sessions ended before the record), REBUILT | 539 | 1 | 540 |
| After both, restored and REBUILT in the same command | 540 | 0 | 540 |

540 - 518 = 22, and 22 is the number of new test cases counted by hand below.

Also run once, because the session seam, the host and the restore changed:

- The whole `CcDirector.Gateway.UnitTests` project: **6,569 passed, 0 failed, 8 skipped, 6,577 in all**,
  3 minutes 48 seconds, on `5000c28ed`.
- `RetiredMessagingWordsTests` in `CcDirector.Core.UnitTests`: 5 passed, 0 failed.
- `dotnet build src/CcDirector.Avalonia` and `dotnet build tools/harnesses/drain-index-diff`: 0 warnings,
  0 errors each.
- Every changed file counted for bytes above 127: none, except the byte order mark `ControlApiHost.cs`
  and `GatewayClient.cs` already carry on `origin/main`. My first attempt at this check failed to run
  (the search tool refused the locale) and printed nothing; I did not take that as clean and ran it again
  with a count per file.

## What was built

1. **Cancel and keep working.** `SmartShutdownDrainOptions` gained `Cancel` (a token) and `BringBack`.
   The drain leaves the asking loop, the collecting loop and the two thirds stage when it is chosen, and
   `CancelAndKeepWorkingAsync` then does four things in order: takes back the close of any session that
   was flagged and is still open, and gives it its name back; sends `DrainMessages.RestartIsOff` (which
   task 1 had already written) to every session still open; marks the record cancelled, decides every
   closed seat "restore", and SAVES it; then hands the closed seats to the existing restore. Rows end
   `KeptRunning` or `BroughtBack`. `CanCancel` is true from the record until the limit, Shut down now, or
   the cancel itself. The run honours one button once: the two are rivals under one small lock, and no
   handler is called and no token cancelled while it is held.
2. **Shut down and ignore all.** `DirectorDrain.RecordIgnoreAllAsync` writes the record (marked
   `ignore-all`, every seat decided "close"); the engine then ends every live session through the session
   seam it now holds, and saves the close times. When the record cannot be written, for a Gateway that
   does not answer or a Director with no Gateway client at all, the sessions are still ended and
   `RecordRefusal` says why. The comment says this is the owner's stated choice and not a fallback.
3. **The operating system shutdown record.** `DirectorDrain.RecordOperatingSystemShutdownAsync` writes
   the record and returns. It ends nothing.
4. **The restart purpose.** The last two steps of `DirectorRestartCycle` (check the machine again, then
   ask) were moved, unchanged in meaning and in every sentence the cycle reports, into
   `DirectorLauncherRestartStep`. The cycle calls it; so does the run, after the drain answers that
   nothing is left on the Director. `Start` with the restart purpose reads the same one private answer
   `CheckAsync` reads, and throws `InvalidOperationException` with the reason before anything is touched.

One verb was added to the session seam: `IDrainSessionControl.CancelDeletion`, over the session's existing
`CancelDeletion`. Without it a cancel would tell a session "the restart is off" and the reaper would close
it a minute later.

## How a real Director brings sessions back - a finding

The mandate says "through the EXISTING restore, against the SAME Director". On the rig that is the real
`DirectorRestore`. On a real Director, running `DirectorRestore` directly **cannot work**: the Gateway
grants the restore lease at its restore door (`POST /gateway/workspaces/{id}/restore`) and refuses every
mark from a Director that does not hold it (`WorkspaceStore.RecordRestoreMark`). A restore started from
inside the Director holds no lease, so its first mark is refused and nothing comes back.

So the host wires `GatewaySmartShutdownBringBack`: it asks that same door for a restore onto this
Director's own id (new `GatewayClient.RequestWorkspaceRestoreAsync`), the Gateway relays the order back
down, and this Director runs the same `DirectorRestore` it always runs. The door answers "taken", not
"done", so the class reads the record until every seat has an answer, for ten minutes at most. Phase 3's
"Bring back" button will meet the same lease rule and can use the same class.

## Decisions I made that the mandate did not spell out

Raised to the Tech Lead as one hand up while I carried on; no answer could reach me.

- **Every closed session comes back, whatever it answered.** The existing restore starts only seats
  decided "restore", and the mandate says a cancel brings back EVERY closed session. On a cancel the drain
  sets each closed seat to "restore" and keeps the session's own answer in the reason. Tested.
- **`CanCancel` is false on a run that was given no way to bring sessions back.** A run that cannot bring
  a session back must not offer a cancel. This is also why NO assertion in an existing test was changed:
  the first item of my hand up proposed changing `Assert.False(snapshot.CanCancel)` in
  `Start_AcrossOneRealRun_EveryStateAndPhaseIsReached_AndEverySnapshotIsWhole`, and that became
  unnecessary, because that test's engine has no restore wired. Its comment "cancel is never offered
  because it is not built" is now stale; the true reason is "because no restore is wired". I left the
  comment alone rather than edit an existing test.
- **The restore gained one owner rule, only for a cancelled record.** Sessions close leaf first, so the
  usual cancel finds the session under a lead closed and the lead still open. `ResolveOwner` refused that
  (it accepts a still-running owner only if it BLOCKED the drain). On a record marked cancelled, an owner
  that was never closed and is running now is used under its own id. Every other record behaves as before.
- **The operating system record is marked `smart-shutdown` with every seat `ended-at-limit`.** Mission 10.5
  says the way up offers those saved conversations as in 10.3, and that pair is what 10.3 reads. The words
  "ended at the limit" are not literally true of it; the record's reason says the operating system was
  shutting down.
- **Ignore all, or the operating system record, while a smart shutdown is under way.** Ignore all throws
  `InvalidOperationException` and points at Shut down now. The operating system record answers with the
  running shutdown's own record, which already names every session.
- **A cancel that arrives too late** (as the time runs out, or after the last session has gone) is not
  acted on, and the result's `Detail` says so.
- **A closed seat that cannot be brought back keeps the row state `ShutDown`**, with the reason in its
  `Detail`. It is not `BroughtBack` and it is not running.
- **The ignore-all close times are a second save.** If that save fails it is logged and `RecordWritten`
  stays true, because the record of what was running is already on the Gateway.

## One existing test file was touched, and not to make anything pass

`DirectorRestoreTests` gained the `[Collection(DirectorGatesCollection.Name)]` attribute and three lines of
comment. It holds the restore's process-wide gate, and my cancel tests now run a real restore, so side by
side each could refuse the other. No test body, assertion or helper in it changed.
`DrainTestRig.cs` was added to (`CancelDeletion` and its list); nothing in it was changed.

## Each new test, in one sentence

All in `SmartShutdownCancelIgnoreRestartTests`, namespace `CcDirector.Gateway.UnitTests.Drain`. Every one
runs the real engine over the real drain; a cancel runs the real `DirectorRestore` over a fake Gateway fed
from the record the run saved. No snapshot or result is built by hand.

- `CancelAndKeepWorking_WhileCollecting_BringsTheClosedSessionBack_TellsTheOpenOnes_AndMarksTheRecordCancelled`:
  pressed at thirty seconds with one session gone and two open, the closed one is spawned once from its
  handover on the same Director, the two open ones are told once each, nothing is ended or interrupted
  after the press, the cancelled record is saved BEFORE the spawn (order on the journal) and is what the
  restore read, the phases run Collecting, Cancelling, Finished, and `CanCancel` is false before the
  record, true until the press, false after.
- `CancelAndKeepWorking_ASessionFlaggedButStillOpen_HasItsCloseTakenBack_AndIsNotBroughtBackTwice`: a
  session that handed over and is still open has its flag taken back and its name restored, is told, and
  is not spawned.
- `CancelAndKeepWorking_ALeadAndTheSessionUnderItBothClosed_TheLeadComesBackFirst`: the lead is spawned
  first and the session under it second, under the lead's NEW id.
- `CancelAndKeepWorking_ASessionUnderALeadThatIsStillOpen_ComesBackUnderThatLead_WhateverItAnswered`: it
  comes back under the lead's own id although it said "restore: no", and the record keeps that answer.
- `CancelAndKeepWorking_AfterTheLimitOrAfterShutDownNow_IsIgnored_AndTheRunStillEndsEmptied` (2): nobody
  told, nobody spawned, no save ever marked cancelled, every session ended, `Emptied`; the test fails
  itself if the cancel was never pressed.
- `CancelAndKeepWorking_PressedTwice_IsHonouredOnce`: one message, one spawn, one entry into Cancelling.
- `CancelAndKeepWorking_ASessionThatCannotBeBroughtBack_DoesNotStopTheOthers_AndTheRowAndTheResultSayWhy`:
  the refused one stays `ShutDown` with the Gateway's reason on its row and in the result; the other comes back.
- `CancelAndKeepWorking_WithNoWayToBringSessionsBack_IsNeverOffered_AndAPressIsIgnored`: as named.
- `ShutDownIgnoringAllAsync_WritesTheRecordBeforeTheFirstSessionIsEnded_AndEndsEverySession`: the
  journal's first entry is the save, every end comes after it, and that first save carries every
  conversation id, the ignore-all mark and every seat decided "close".
- `ShutDownIgnoringAllAsync_TheGatewayUnreachable_..._AndStillEndsEverySession` (2): a Gateway that does
  not answer, and no Gateway client at all - record not written with the reason, every session ended.
- `RecordAndLetEndAsync_SavesTheRecordWithEveryConversationId_AndNeverInterruptsOrEndsASession`: the
  journal holds one save and nothing else, and every session is still live.
- `RecordAndLetEndAsync_TheGatewayUnreachable_SaysWhy_AndTouchesNoSession`: as named.
- `Start_WithTheRestartPurpose_AsksTheLauncherOnlyAfterEverySessionIsGone_AndEndsRestartAccepted`: nothing
  is live at the moment of the ask, the ask is the journal's last entry, the machine was re-checked once.
- `Start_WithTheRestartPurpose_ALauncherThatRefuses_..._AndTheRecordStands` (2): refused by the launcher,
  and refused at the re-check without the launcher being asked; the record is uncancelled and its seat
  still decided "restore".
- `Start_WithTheRestartPurpose_WhenTheLauncherWouldNotRestartThis_ThrowsWithTheReason_AndTouchesNoSession`
  (2): for "no" and "could not tell"; empty journal, no capture, no run left under way.
- `Start_WithTheClosePurpose_NeverAsksTheLauncher`: no re-check and no ask.
- `BringBackAsync_AsksTheRestoreDoorForThisDirector_AndReadsTheRecordUntilEverySeatHasAnAnswer`: one
  request naming this Director and those seats, three reads, one seat back and one failed.
- `BringBackAsync_ARestoreNotTaken_SaysWhy_AndOneThatNeverAnswers_SaysSoAfterItsPatience`: as named.

## The revert proofs

Both AFTER the commit, one at a time, each REBUILT and the WHOLE check run with no narrowed filter.

| What was undone | Result | Every red test |
|---|---|---|
| `doc.CancelledAtUtc = cancelledAt;` deleted from `DirectorDrain.cs` | 2 failed, 538 passed | The main cancel test, on "value does not have a value" for the saved mark; and the still-open-lead test, on "the collection was empty" - with no mark the restore's new owner rule does not apply and the session under the lead is not spawned |
| In `ShutDownIgnoringAllAsync`, the record written AFTER the sessions are ended | 1 failed, 539 passed | The ignore-all order test, on the journal's first entry |

Restored with `git checkout -- src/CcDirector.ControlApi/Drain/DirectorDrain.cs` and
`git checkout -- src/CcDirector.ControlApi/SmartRestart/DirectorSmartShutdown.cs`. After each: no
difference against `5000c28ed`. After the second, REBUILT, whole check: 540 passed, 0 failed.

I expected one red test in the first proof and got two. The second is a true dependency, not an accident.

## What I could not reach

- **No real Gateway, no real Director, no real launcher.** Nothing here was run against one, as the
  mandate requires. That the restore door accepts a Director's own credential is a reading of
  `WorkspaceEndpoints.cs` (the route has no credential check of its own), not a run.
- **The roster may lag.** The existing restore refuses a seat whose captured session is still on the
  Gateway's roster. A session closed seconds before a cancel may still be listed; its row would then say
  "still running ... not started again". Not tested, not solved.
- **`GatewayClient.RequestWorkspaceRestoreAsync` and the real `CancelDeletion` have no test.** The first
  is one HTTP call; the second is one line over `Session.CancelDeletion`.
- **`ControlApiHost.CreateSmartShutdown`'s new wiring is not watched by a test.** The existing host tests
  still pass over it; none of them presses cancel, ignores all or restarts.
- **Two small windows** where the run says yes to a cancel the drain can no longer act on: between the
  limit being reached and its first snapshot, and between the last session going and the run finishing.
  Both end with `Detail` saying the cancel came too late. Neither has a test; I could not make either
  deterministic on the rig.
- **The parked suites and the Avalonia tests were not run.** Phase 2's screens compile against the
  unchanged interface.
- **The record after a refused restart is not changed** (`directorOutcome` stays `not-restarted`), as in
  the restart cycle. Whether the way up wants `restart-refused` there is phase 3's to say.
