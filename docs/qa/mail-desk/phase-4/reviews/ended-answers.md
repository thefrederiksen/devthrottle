# Answers: a routed session ended on its history record (pull request 4283)

Answers to `ended-and-clock-review.md`, by the Developer of pull request 4283, 20 October 2026. The branch is rebased
onto `mail-desk-phase-4`, which holds pull request 4282 (the test clock) and the corrected contract.

## Finding 1 - a live session counted as ended on a stale final ending: ACCEPTED, FIXED

`MailDeskRouter.IsAlive` now reads the session list first. A row in the list decides on its own: alive unless
`FleetManagerSessions.IsGone`. The history record is read only when the list has no row, which is the case the change
exists for (a Director reaps a session that exits cleanly). This is the order the Tech Lead ruled: a live row always
wins over any history ending.

Test, the reviewer's probe case: `RouteAsync_LiveRowAndAFinalHistoryEnding_IsNotEnded_NotReroutedAndTheSeatStaysBusy`
(closed, finished, director-stopped) - the session's row is live and its history holds a final ending; nothing is
re-routed, no second session is started, and the new email for the seat waits.

Section 3.2 step 3 of the contract now says this ("a row in the session list decides alone"), updated by the Tech
Lead on `mail-desk-phase-4`; this branch is rebased onto it.

## Finding 2 - a Smart Restart brings a routed session back under a new id: ACCEPTED, FIXED

What the code says (read on `origin/mail-desk-phase-4`), which decided the fix:

- **A restore keeps the session's name.** Both restore paths create the new session with `Name = seat.Name`:
  the reopen, `DirectorWayUp.BuildReopen` (`src/CcDirector.ControlApi/SmartRestart/DirectorWayUp.cs`), and the bring
  back, `DirectorRestore.BuildRequest` (`src/CcDirector.ControlApi/Drain/DirectorRestore.cs`). The seat's name is the
  session's own name when it was captured, and the factory and seat are handed back too.
- **The lookup by name already wired could not find it.** `GatewayEndpoints.LastKnownTriggerSessionByName` matches only
  rows whose `OriginSurface` is `Trigger`, and both restore paths create the new session with `OriginSurface = Api`.
  So the first proposal - adopt through that lookup - would never have adopted in production.

What was built, as the Tech Lead approved:

- A separate lookup, `GatewayEndpoints.LastKnownSessionsByName`: every holder's row the account's Directors last
  reported under exactly this name, from any origin surface. The trigger lookup is not widened. It is wired in
  `GatewayHost.NewMailDeskRouter` from the Director registry and the pushed-session store, as the router's new
  `findSessionsByName` delegate.
- In step 3, before a `closed` or `director-stopped` ending counts as ended (and only for a session with no row in the
  list), the router looks for a session with the row's exact `StartName` that is live (in the list, not `IsGone`), has
  a different id, and has the row's factory and seat. Found: `MailDeskRoutedStore.Follow` gives every open row of the
  old session the new id. The row stays open, no count changes, the decision's outcome detail names the new session,
  and it is logged. The run's tally counts it as `Followed`. A `finished` ending is not a restart's ending under the
  contract, so it is re-routed as before.
- One more change the follow needed, in the busy-seat rule. A start whose first email was already done keeps that done
  row on the OLD id, and the newest-session check could read it, find it ended and free the seat while the restored
  session works the rest. So a seat is now also busy while it has an open row with a session after step 3. Every such
  row is with a live session by then, because step 3 has re-routed or followed the rest.

**Known limit, stated plainly: a routed session that renamed itself is not followed after a Smart Restart. It comes
back under its new name, the lookup by the start's name does not find it, and its emails are re-routed once - so for
those emails two sessions can work on them.**

Tests, in `MailDeskRouterEndingsTests` unless named:

- `RouteAsync_EndedByARestartAndALiveSessionWithTheSameNameFactoryAndSeat_IsAdoptedNotRerouted` (closed,
  director-stopped): adopted, still open, nothing re-routed, no second session, the new email waits, and the restored
  session can say done.
- `RouteAsync_LiveSessionWithTheSameNameButAnotherFactoryOrSeat_IsNotAdopted_ReroutedOnce` (another factory, another
  seat).
- `RouteAsync_SessionWithTheSameNameThatIsGone_IsNotAdopted_ReroutedOnce`.
- `RouteAsync_FinishedEndingAndALiveSessionWithTheSameName_IsNotAdopted_ReroutedOnce`.
- `RouteAsync_RestoredSessionHoldingTheRestOfAStartWhoseFirstEmailIsDone_KeepsTheSeatBusy` - shown red with the new
  busy-seat line removed.
- `NewMailDeskRouter_AsGatewayHostWiresIt_FollowsARestoredSessionListedOnTheGateway` - the router built by the host's
  own factory, over a real Director registry and pushed-session store, with the restored session on the API surface.
  Shown red when the host's delegate is replaced by one that finds nothing.
- `MailDeskRoutedStoreTests.Follow_OpenRowsOfTheOldSession_TakeTheNewIdAndStayOpen_ADoneRowIsLeftAsItIs`.

## Runs

```
dotnet test src/CcDirector.Gateway.UnitTests -p:EnableWindowsTargeting=true --filter "FullyQualifiedName~MailDesk|FullyQualifiedName~Trigger|FullyQualifiedName~SessionHistory"
Passed!  - Failed:     0, Passed:  1091, Skipped:     0, Total:  1091

dotnet test src/CcDirector.Core.UnitTests -p:EnableWindowsTargeting=true --filter "FullyQualifiedName~GateTestsDoNotDependOnTheMachine"
Passed!  - Failed:     0, Passed:    11, Skipped:     0, Total:    11
```

## Second review (`ended-answers-review.md`), finding 1 - the router runs before the owner brings the session back: ACCEPTED, FIXED

The Tech Lead ruled that an ending written because a Director stopped or restarted must wait like `interrupted`, and
only an ending a person or the agent itself made ends a routed session at once - first establishing whether the history
record can tell them apart. **It cannot, for a Smart Restart.** What each path records (read on this branch, which is
`mail-desk-phase-4` plus this pull request; none of these files is changed by it):

- **A plain Director shutdown writes `director-stopped`, and that can be told apart.** `App.OnShutdown`
  (`src/CcDirector.Avalonia/App.axaml.cs`) sends the Gateway farewell FIRST, before the sessions are killed; the hub's
  `DirectorStopping` stamps every open row of that Director `director-stopped` (`SessionHistoryStore.MarkDirectorStopped`),
  and a per-session remove arriving during the kill keeps that first ruling.
- **A Smart Restart's drain writes `closed` or `finished` - the same as a person or the agent.** The drain ends each seat
  BEFORE the process stops: a seat that handed over is flagged for deletion and the reaper removes it
  (`DirectorDrain.cs` line 1254, `MarkForDeletion`); a seat still present at the time limit is ended
  (`DirectorDrain.cs` line 1750, `EndAsync`). Either way the Gateway gets a plain per-session remove
  (`DirectorHub.RemoveSession`), which carries no reason. `SessionHistoryRecorder.ObserveRemoval` rules `finished` when
  the last pushed state was Exited or Failed or the dismiss verdict was done, and `closed` otherwise - exactly what a
  person closing the session, or the reaper after the agent's own `session done`, records. The farewell that follows
  finds no open row left, so no `director-stopped` is written for them.
- "Shut down and ignore all sessions" (`DirectorSmartShutdown.cs` line 302) also ends each session `closed`, with
  `keepKitForRestore: false` - nothing is brought back, so ending those at once is right.

**What the Gateway does hold that could tell them apart: the restart record.** The drain saves the record to the
Gateway before it ends each seat (`SaveAsync` at `DirectorDrain.cs` line 1247 before line 1254, and at line 1744 before
line 1750). Each seat (`WorkspaceSeat`, `src/CcDirector.Gateway.Contracts/WorkspaceDtos.cs`) carries the OLD
`SessionId`, the name, the restore decision, and - once brought back - `ReopenedSessionId`
(`WorkspaceStore.RecordRestoredByClaim`). So a routed session whose id is a seat on a restart record still owed could
wait (seat busy, not re-routed), and be followed to `ReopenedSessionId` when the seat is brought back - an exact follow,
not by name. When the owner decides not to bring it back, or after the 24 hours, it ends as before.

The Tech Lead approved all three parts of the recommendation. What was built:

- **`director-stopped` waits like `interrupted`.** Followed by name if the session comes back, otherwise ended by the
  24-hour rule. Only `closed` (a person) and `finished` (the agent) still end a session at once - and only when no
  restart record holds it.
- **The restart record is read**, through a new required delegate `restartSeatsOf`, asked once per run for the same
  sessions as the history endings, in the routed row's own account (`WorkspaceStore.SeatsCapturedFrom(tenant, ids,
  since)`, which opens its context in the account named). `since` is the oldest start among those sessions: a record
  that captured a session is newer than its start, so older records are never parsed. When two records hold one
  session, the newest answers. A record that cannot be parsed is skipped and logged rather than thrown, so one damaged
  restart record cannot stop every email of the account being routed (the store's own `List` keeps the same rule).
  Wired in `GatewayHost.NewMailDeskRouter` from the host's `WorkspaceStore`.
- **For a seat on a restart record** (the record wins over the history ending, which for a Smart Restart is `closed` or
  `finished` and says nothing): brought back - by a Bring back (`RestoredSessionId`) or a reopen of its conversation
  (`Restore.ReopenedSessionId`) - followed to that exact session id; marked not to be brought back (`close`) - ended at
  once; still owed - followed by name if the restored session is live (the mark failed to be written), otherwise not
  ended, and its seat busy, until the 24 hours after its start.
- **The follow by name stays** for `closed` with no record and for `director-stopped`, as before.
- The decision is now made in one place, `MailDeskRouter.FateOf`, which both the orphans step and the busy-seat rule
  call, so the two can never disagree about a session.

The other two record kinds fit the same rule (`DirectorDrain.cs`): "Shut down and ignore all sessions"
(`RecordIgnoreAllAsync`) decides every seat `close`, so its emails are re-routed at once; an operating system shutdown
(`RecordOperatingSystemShutdownAsync`) records every seat `undecided`, so its emails wait for the way up like a Smart
Restart's.

Tests (`MailDeskRouterEndingsTests` unless named):

- `RouteAsync_SmartRestart_CheckRunsBeforeTheBringBack_NotRerouted_ThenFollowedByTheRecord` - the reviewer's order:
  the check runs before the Bring back (not re-routed, no second session, the new email waits), then the record maps
  the seat to a session not yet in the list and under no matching name, and the row follows it.
- `RouteAsync_SmartRestart_SeatReopenedFromItsConversation_IsFollowedToTheReopenedSession` (history `finished`).
- `RouteAsync_SmartRestart_SeatMarkedNotToBeBroughtBack_IsEndedAtOnce`.
- `RouteAsync_SmartRestart_StillOwedAfter24Hours_IsEnded`.
- `RouteAsync_SmartRestart_BroughtBackButTheMarkFailedToBeWritten_IsFollowedByName` - the name fallback.
- `RouteAsync_DirectorStoppedEnding_IsNotEndedBefore24Hours_AndTheSeatIsBusy` (1 minute, 23 hours) and
  `RouteAsync_DirectorStoppedEnding_IsEndedAfter24Hours`.
- `RouteAsync_APersonClosedTheSession_NoRestartRecord_IsReroutedAtOnce`.
- `RouteAsync_ManySessions_AsksForTheRestartRecordsOnceWithTheSameSessionsAndTheOldestStart`.
- `NewMailDeskRouter_AsGatewayHostWiresIt_FollowsTheRestartRecordInTheWorkspaceStore` - over the real workspace store;
  shown red when the host's `restartSeatsOf` is replaced by one that finds nothing.
- `NewMailDeskRouter_AsGatewayHostWiresIt_ARestartRecordOfAnotherAccountIsNotRead`.
- `MailDeskRestartRecordReadTests`: the newest record answers, a record before the window is not read, a damaged record
  is skipped, and how a seat reads as brought back or not to be brought back.
- `director-stopped` was taken out of the two "ended at once" theories, since it no longer ends a session at once.

Runs, on this branch rebased onto `mail-desk-phase-4` at `4dbf953e0`:

```
dotnet test src/CcDirector.Gateway.UnitTests -p:EnableWindowsTargeting=true --filter "FullyQualifiedName~MailDesk|FullyQualifiedName~Trigger|FullyQualifiedName~SessionHistory|FullyQualifiedName~ScheduledRun|FullyQualifiedName~FactorySeat|FullyQualifiedName~Workspace"
Passed!  - Failed:     0, Passed:  1596, Skipped:     0, Total:  1596

dotnet test src/CcDirector.Core.UnitTests -p:EnableWindowsTargeting=true --filter "FullyQualifiedName~GateTestsDoNotDependOnTheMachine"
Passed!  - Failed:     0, Passed:    11, Skipped:     0, Total:    11
```

## Third review (`ended-restart-review.md`): three findings, all ACCEPTED, FIXED as the Tech Lead ruled

### Finding 1 - a seat busy for ever once its brought-back session ended

- **A brought-back seat is judged by the session it came back as, by the ordinary rules.** `MailDeskRouter.FateOf`, on
  a seat whose record names a new session R2, now asks `FateOf(R2)`: R2's own row, R2's own history ending, and the
  24-hour rule from R2's own start (`WorkspaceSeatOnRecord.BroughtBackAtUtc`: the reopen's time, or the Bring back's
  start, or - when the record says neither - the restart). R2 alive: followed. R2 ended: the rows are re-routed (or
  handed on) with the reason saying so. To judge R2 the history is now asked about R2 too: the restart records are
  asked first, then the history once, for the run's sessions and every session a record says one came back as. A
  session restarted again before the router saw it is followed up to three restarts deep.
- **A followed row takes the new session's start.** Found while writing the test for this ruling: after a follow the
  row kept R1's start, so R2 was judged by the 24-hour rule from R1's start - a restart 30 hours into R1's life had R2
  re-routed on the very next run while it worked the email. `MailDeskRoutedStore.Follow` now writes the new session's
  start on the rows (from the record, or the found session's own creation time for the follow by name).
- **A done row never makes a seat busy.** `IsSeatBusy` is now: a start whose outcome is not known, inside its grace;
  or an open row of the seat still with a session after step 3. The "newest session has not ended" check is gone.
  **For the Tech Lead: this changes one case of contract section 1's "A busy seat" row, which still says "while the
  newest session the Mail Desk started ... has not ENDED".** A session that has said done on every email it holds and is
  still running no longer keeps its seat busy; the seat's next email starts a new session at once. Section 1 should
  say what step 3 now says.

### Finding 2 - the end of an owed seat's wait was counted from the session's start

- **Does a restart offer end by itself? No.** The start-up window stops showing a record after
  `DirectorWayUp.OfferedForDays` (7) days from the shutdown (`IsTooOldToOffer`), but File, Restart history keeps its
  Bring back for good: "AGE IS NOT PART OF IT" (`DirectorWayUp.cs`, the history's `Offer`). So the 72 hours are used,
  as ruled.
- A seat still owed a Bring back waits while the record owes it. `MailDeskRouter.RestartOwedFor` (72 hours) is counted
  from the record's creation - the restart, by the Gateway's own clock (`WorkspaceStore.SeatsCapturedFrom` now carries
  the record's `CreatedUtc` as `WorkspaceSeatOnRecord.RecordedUtc`) - never from the session's start. Past it, the
  routed email is handed to the OWNER (`MailDeskRoutedStore.HandToOwner`, reason
  `MailDeskRoutedReasons.NotBroughtBack`: "its session was ended by a Director restart and was not brought back within
  72 hours"), never re-routed; the decision keeps its `seat-started` outcome. The seat is then free.

### Finding 3 - a cancelled restart's record

- `WorkspaceStore.SeatsCapturedFrom` skips a record with `CancelledAtUtc` set: "Cancel and keep working" is not a
  restart, its sessions kept their own ids, and the way up never offers anything in it. A later close by a person is
  then read from the history alone and re-routed at once.

### Tests

New, in `MailDeskRouterEndingsTests` unless named:

- `RouteAsync_DoneRowOnASessionBroughtBackThatThenEnded_NeverKeepsTheSeatBusy` (finished, closed) - the reviewer's
  probe, with the opposite assertion: k-2 starts at the first run once R2 is gone.
- `RouteAsync_OpenRowOnASessionBroughtBackThatThenEnded_IsJudgedByTheNewSession_ReroutedOnce`.
- `RouteAsync_SessionBroughtBackLateInTheFirstSessionsLife_IsJudgedFrom24HoursAfterItsOwnStart` - shown red with the
  start written by `Follow` removed.
- `RouteAsync_SmartRestart_StillOwed71HoursAfterTheRestart_Waits_At72HoursGoesToTheOwnerNeverRerouted` - replaces
  `RouteAsync_SmartRestart_StillOwedAfter24Hours_IsEnded`, whose rule this review withdrew.
- `RouteAsync_SmartRestartLateInTheSessionsLife_WaitsFromTheRestart_NotFromTheSessionsStart` (restart 30 hours into the
  session; still waiting 40 hours later).
- `RouteAsync_SmartRestartOverAWeekend_BroughtBack60HoursAfterTheRestart_IsFollowed_NeverRerouted` (checks at 1 minute,
  24 hours and 59 hours; Bring back at 60 hours).
- `NewMailDeskRouter_AsGatewayHostWiresIt_ACancelledRestartRecordIsIgnored_APersonsCloseIsReroutedAtOnce` - over the
  real workspace store.
- `MailDeskRestartRecordReadTests.SeatsCapturedFrom_ACancelledRecord_IsNotRead_AndTheRestartTimeIsTheRecordsCreation`.
- `MailDeskRoutedStoreTests.Follow_...` now also asserts the new start.

Runs, on this branch rebased onto `mail-desk-phase-4` at `aad3328dd`:

```
dotnet test src/CcDirector.Gateway.UnitTests -p:EnableWindowsTargeting=true --filter "FullyQualifiedName~MailDesk|FullyQualifiedName~Trigger|FullyQualifiedName~SessionHistory|FullyQualifiedName~ScheduledRun|FullyQualifiedName~FactorySeat|FullyQualifiedName~Workspace"
Passed!  - Failed:     0, Passed:  1604, Skipped:     0, Total:  1604

dotnet test src/CcDirector.Core.UnitTests -p:EnableWindowsTargeting=true --filter "FullyQualifiedName~GateTestsDoNotDependOnTheMachine"
Passed!  - Failed:     0, Passed:    11, Skipped:     0, Total:    11
```
