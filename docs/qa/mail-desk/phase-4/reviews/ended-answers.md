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
