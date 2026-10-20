# Answers: a routed session ended on its history record (pull request 4283)

Answers to `ended-and-clock-review.md`, by the Developer of pull request 4283, 20 October 2026. The branch was rebased
onto `mail-desk-phase-4` after pull request 4282 (the test clock) was merged into it.

## Finding 1 - a live session counted as ended on a stale final ending: ACCEPTED, FIXED

`MailDeskRouter.IsAlive` now reads the session list first. A row in the list decides on its own: alive unless
`FleetManagerSessions.IsGone`. The history record is read only when the list has no row, which is the case the change
exists for (a Director reaps a session that exits cleanly). This is the order the Tech Lead ruled: a live row always
wins over any history ending.

Test, the reviewer's probe case: `RouteAsync_LiveRowAndAFinalHistoryEnding_IsNotEnded_NotReroutedAndTheSeatStaysBusy`
(closed, finished, director-stopped) - the session's row is live and its history holds a final ending; nothing is
re-routed, no second session is started, and the new email for the seat waits.

The contract still reads "a session row with IsGone, OR a history ending of closed, finished or director-stopped".
Recommendation for the Tech Lead: correct section 3.2 step 3 to say the history ending counts only when the session
list has no row for the session.

## Finding 2 - a Smart Restart brings a routed session back under a new id: NOT BUILT YET, waiting on the Tech Lead

The Tech Lead's proposed fix: before treating a closed or director-stopped ending as ended, look up a live session by
the row's exact `StartName` through `findSessionByName` (already wired) and adopt it.

What the code says (read on `origin/mail-desk-phase-4`):

- **A restore keeps the session's name.** Both restore paths create the new session with `Name = seat.Name`:
  the reopen, `DirectorWayUp.BuildReopen` (`src/CcDirector.ControlApi/SmartRestart/DirectorWayUp.cs`), and the bring
  back, `DirectorRestore.BuildRequest` (`src/CcDirector.ControlApi/Drain/DirectorRestore.cs`). The seat's name is the
  session's own name when it was captured, so one exception: a routed session that renamed itself comes back under
  the new name, and a lookup by `StartName` would not find it.
- **But the lookup that is wired cannot find it.** Production `findSessionByName` is
  `GatewayEndpoints.LastKnownTriggerSessionByName`, which matches only rows whose `OriginSurface` is
  `SessionOriginSurfaces.Trigger`. Both restore paths create the new session with
  `OriginSurface = SessionOriginSurfaces.Api`. So the proposed fix, built on that lookup, would never adopt a restored
  session, and its emails would still be re-routed. A rig test with a fake lookup would pass while production would
  not adopt.

Recommendation: a separate delegate for this check, wired in `GatewayHost.NewMailDeskRouter`. It finds a live session
by the row's exact `StartName` with no surface filter, and also requires the same factory and seat as the row
(`SessionDto.Factory` and `FactorySeat`, which both restore paths hand back). The name holds the factory, the seat and
the minute of the start, and a session that a person started by hand is never named that way. The known gap stays: a
session that renamed itself is not found, and its emails are re-routed after a restart. The alternative is to widen
`LastKnownTriggerSessionByName` to the Api surface, but that also changes how a trigger start whose outcome was not
known is settled, which is outside this task.

Nothing for finding 2 is in the code until the Tech Lead decides.
