# Review: the answers to the review of pull request 4283 (a routed session ended on its history record)

Reviewer seat: "Mail Desk - Reviewer - session ended answers", opened by the phase 4 Tech Lead (session 128).
Date: 20 October 2026.

## Scope

What I read, at `origin/md4-ended` = `b6ab186ae` (fetched from origin on 20 October 2026):

- `reviews/ended-and-clock-review.md` (the earlier review, two findings) and `reviews/ended-answers.md` (the answers).
- Every line of the two answering commits: `d210b05d5` (finding 1) and `b6ab186ae` (finding 2) - `MailDeskRouter.cs`,
  `MailDeskRoutedStore.Follow`, `GatewayEndpoints.LastKnownSessionsByName`, `GatewayHost.NewMailDeskRouter` and its one
  call, the rig and every new test.
- `CONTRACT.md` section 3.2 step 3 on this branch.
- Around the diff, to judge it: `MailDeskRouter.SessionName`, `MailDeskRoutedStore.Open` and `StartsOf`,
  `GatewayEndpoints.LastKnownTriggerSessionByName` (unchanged), `PushedSessionStore.GetLastKnown` and `IsHoldersRow`,
  `TriggerService.HandToMailDesk` and `FinishScreeningAsync`, `DirectorTriggerRunner.RunAsync`, where `ControlApiHost`
  starts and stops the trigger runner, `DirectorWayUp.BuildReopen` and `ReopenAsync`, `DirectorRestore.BuildRequest`,
  `WayUpStartUpAsk`, `WayUpOfferWindow`, and the `director-restart` skill (what the owner sees after a Smart Restart).

What I ran (Linux, devlinux), on `b6ab186ae`:

- `dotnet test src/CcDirector.Gateway.UnitTests -p:EnableWindowsTargeting=true --filter
  "FullyQualifiedName~MailDesk|FullyQualifiedName~Trigger|FullyQualifiedName~SessionHistory"` - 1,095 passed, 0 failed, 0 skipped.

What I could not reach: no real Director, no real Smart Restart, no Gateway restart. Finding 1 is argued from the code
paths named, not reproduced end to end.

## Verdict

- **Finding 1 of the earlier review (a live row and a stale final ending): answered.** `IsAlive` now returns
  `!FleetManagerSessions.IsGone(row)` whenever the list has a row (`MailDeskRouter.cs` line 421), and reads the history
  only when it has none. The new test puts a live row together with each of `closed`, `finished` and `director-stopped`
  and proves nothing is re-routed, no second session starts and the new email waits. The contract says the same. No
  finding.
- **Finding 2 of the earlier review (a Smart Restart brings the session back under a new id): changes needed - 1
  finding.** The follow is correct when it fires, but in the ordinary Smart Restart sequence it does not get the
  chance to fire: the router runs, and re-routes the email, before the owner brings the session back.

## Findings

### 1. The follow only works if the session is already back when the router runs, and after a Smart Restart the router runs first - so the email is still re-routed and still worked by two sessions

- **Where:** `src/CcDirector.Gateway/MailDesk/MailDeskRouter.cs`, `RerouteOrphans` line 210 and `FollowRestored` from
  line 228. A `closed` or `director-stopped` session with no row is followed only if a live session with the start's
  name, factory and seat is in the list AT THAT RUN; otherwise its rows are re-routed at once and a new session is
  started. Nothing re-examines the email later when the restored session does appear.
- **Why the restored session is not there yet:**
  - The restore waits for the owner. After a Smart Restart the Director asks the way up engine once and shows the
    window "A restart is available"; the seats come back only when the owner presses **Bring back**
    (`WayUpOfferWindow.axaml.cs` line 111, `BringBackFromThePressAsync`), or later from File, Restart history - "Not
    now is not final" (the `director-restart` skill). `DirectorWayUp.ReopenAsync` and `DirectorRestore` both run from
    that press.
  - The Mail Desk check does not wait. `ControlApiHost` starts `DirectorTriggerRunner` when the Director starts; its
    first poll is immediate (`DirectorTriggerRunner.cs` line 84) and a fresh process has no due time, so the check
    runs within seconds of the Director reaching its Gateway. Every check that does not fail and is not paused is
    screened (`TriggerService.HandToMailDesk`, line 519), whatever its count, and every screened run runs the router
    (`FinishScreeningAsync`). So the router runs every check interval from start-up, offer window open or not.
  - The same is true during the drain itself: the trigger runner is cancelled only when the host is disposed
    (`ControlApiHost.cs` line 2067), so a check during the up to ten minutes of drain sees a routed session already
    ended `closed` with nothing restored.
- **The harm:** exactly the harm of the earlier finding 2, still reachable on the ordinary path. At the first run after
  the session's ending, the email is re-routed (`RerouteCount` 1) and a new session S2 is started on it. When the owner
  then presses Bring back, the seat reopens resuming the old conversation, which holds the email in its first prompt
  (and, after a Smart Restart, a handover written mid-task); it carries on with it. Now two sessions work one email (both may answer it, file it or pay it). The
  restored session cannot even say done - the row is with S2, so `MailDeskRoutedStore.Done` refuses it
  (`not_your_email`) - and nothing tells it the email moved.
- **Failure scenario:** a routed session R1 holds email k-1. The owner picks File, Smart Restart (or the Director
  updates itself). The drain asks R1 for a handover and ends it; history records `closed`. The Director restarts,
  reaches the Gateway, and within 30 seconds its Mail Desk check is screened and the router runs: R1 has no row and a
  `closed` ending; `FollowRestored` asks for a live session named like R1's start and finds none, because the offer
  window is still waiting for the owner. k-1 is re-routed and session S2 starts on it. A minute later the owner presses
  Bring back: R1 returns as R2 with R1's name, factory and seat, resuming the conversation that holds k-1. R2 and S2
  both work k-1. On the next run R2 is not followed either: k-1's row now names S2, which is live.
- **The tests do not cover this order.** Every new test of the follow (`MailDeskRouterEndingsTests`,
  `RouteAsync_EndedByARestartAndALiveSessionWithTheSameNameFactoryAndSeat_IsAdoptedNotRerouted` at line 264 and the
  four after it, and the host wiring test) puts the restored session in the list BEFORE the router runs. None runs the
  router between the ending and the restore, which is the order a Smart Restart actually produces.
- **Not decided here** (the builder's call): the contract's step 3 says what to do when the restored session is found,
  but not what to do while it is still owed. The Gateway does hold that fact - the restart record lists the seats it
  still owes, with their session names, and the Director marks each reopened seat on it (`MarkReopenedAsync`) - but
  whether to read it, and what to do with an email whose session is on a record still owed, is a design decision.

## Things checked and found sound (no finding)

- **No wrong session is followed, within what I could see.** The candidate must have the row's exact `StartName`, the
  same factory and seat, a different id, and be in the list and not gone. The start name carries the factory, the seat,
  the email count and the minute (`SessionName`, line 522), and a seat is started at most once a run and not again
  while busy, so two starts of one seat do not share a name in practice. A gone session and a session of another
  factory or seat are each proven not followed by a test.
- **No account leak.** `LastKnownSessionsByName` walks `registry.ListDirectors(tenant)` and
  `pushedSessions.GetLastKnown(tenant, ...)`, both scoped to the account, and keeps only the holder's row
  (`IsHoldersRow`). It is the same store `findSession` reads (`LastKnownSession`), so the follow cannot see a session
  the rest of step 3 cannot.
- **The trigger's own lookup is untouched.** `LastKnownTriggerSessionByName` is unchanged and still feeds step 2; the
  new lookup is a separate delegate.
- **No seat busy for ever.** The new busy line (line 404) counts an open row with a session. After step 3 every such
  row is live by `IsAlive` or was just followed to a live session; a later run that finds that session ended re-routes
  the row as before, and a session with no row and no final ending still ends 24 hours after the row's start (the
  followed row keeps its first start time, so not later than before). The test with a done first email proves the
  done row on the old id no longer frees the seat.
- **One session per email when the follow fires.** `Follow` moves only open rows of the old id, leaves done rows as
  they are, counts nothing, and the restored session can then say done (proven by test).
- **No real clock, sleep, process or port in the new tests.** They use the rig's clock and database; the host wiring
  test uses a real `DirectorRegistry` over the rig's own temporary folder and an in-memory `PushedSessionStore`. The
  store's own clock touches only the copied rows' clock fields, which the router does not read.
- **The renamed-session limit** is stated in the answers and in the contract; I take it as a declared limit, not a
  finding.
