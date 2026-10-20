# Review: a routed session and the restart record (pull request 4283, commits cb5f599b6 and fb7271c6f)

Reviewer seat: "Mail Desk - Reviewer - routed session and restart record", opened by the phase 4 Tech Lead (session 128).
Date: 20 October 2026.

## Scope

What I read, at `origin/md4-ended` = `fb7271c6f` (fetched from origin on 20 October 2026, checked out detached):

- `reviews/ended-answers-review.md` (the previous review, one finding) and `reviews/ended-answers.md` (the answers,
  including the new section answering that finding).
- `CONTRACT.md` section 3.2 step 3 on this branch.
- Every line of `cb5f599b6` (documents only) and `fb7271c6f`: `MailDeskRouter.FactsOfThisRun`, `RerouteOrphans`,
  `FateOf`, `ByName`, `Unseen`, `IsSeatBusy`; `WorkspaceStore.SeatsCapturedFrom` and `WorkspaceSeatOnRecord`;
  `GatewayHost.NewMailDeskRouter` and its one call; the rig; `MailDeskRouterEndingsTests`,
  `MailDeskRestartRecordReadTests`, the change to `MailDeskRouterTests`.
- Around the diff, to judge it: `MailDeskRoutedStore.StartsOf` (its order) and `Follow`; `WorkspaceStore.Save` (who
  sets `CreatedUtc`); in `src/CcDirector.ControlApi/Drain/DirectorDrain.cs`, `CancelAndKeepWorkingAsync` (what the record
  holds after "Cancel and keep working") and `RecordOperatingSystemShutdownAsync`.

What I ran (Linux, devlinux), on `fb7271c6f`:

- `dotnet test src/CcDirector.Gateway.UnitTests -p:EnableWindowsTargeting=true --filter
  "FullyQualifiedName~MailDesk|FullyQualifiedName~Trigger|FullyQualifiedName~SessionHistory|FullyQualifiedName~Restart"`
  - 1,600 passed, 1 failed (`HeldDeliveryDriverTests.ARecordWriteThatThrowsAfterTheClaimMark_...`, which fails on this
  machine on main too, as the brief says), 0 skipped.
- A probe test of my own for finding 1, in the rig, NOT committed (deleted after the run). It is quoted in finding 1 so
  the builder can turn it into a regression test. It passed on `fb7271c6f` - that is, it proved the defect - and its
  control (the same steps with no restart record) failed, proving the record is the cause.

What I could not reach: no real Director, no real Smart Restart, no operating system shutdown, no real Gateway. Findings
2 and 3 are argued from the code paths named, not reproduced end to end.

## Verdict

**Changes needed - 3 findings.** The previous review's finding (the check runs before the Bring back) is answered for
the case it described: a seat still owed is not re-routed and its seat counts busy, and the record's own mapping is
followed (`RouteAsync_SmartRestart_CheckRunsBeforeTheBringBack_NotRerouted_ThenFollowedByTheRecord` proves exactly that
order). But the record now makes a seat busy for ever in an ordinary sequence (finding 1), its end to the wait is
measured from the wrong moment and so can still fire before the Bring back (finding 2), and a record left by a cancelled
restart makes a person's close look like a restart (finding 3).

## Findings

### 1. A seat is busy for ever once its newest session was brought back by a restart and that restored session then ended

- **Where:** `src/CcDirector.Gateway/MailDesk/MailDeskRouter.cs`, `IsSeatBusy` lines 494-497, and `FateOf` line 291.
- **What the code does:** `IsSeatBusy` takes the newest start of the seat (`StartsOf`, newest start first, then by row
  id) and asks `FateOf` about that row's session id. When the row is a DONE row still on the old id R1 (a done row is
  never followed - `MailDeskRoutedStore.Follow` moves only open rows), `FateOf(R1)` finds R1 on the restart record with
  `BroughtBackAs` R2 and returns `BackAs(R2)` - not ended - WITHOUT asking whether R2 is still alive. `IsSeatBusy` reads
  "not ended" as busy. Nothing in that branch has an end: `Unseen` and its 24 hours are never reached, and the record
  stays in the read window for ever, because the window starts at the oldest start of the sessions read, which is R1's
  own start, and the record was created after it.
- **The harm:** every new email for that seat waits for ever - `run.Waiting` grows, nothing is started, and nothing
  tells the owner. The brief asked for exactly this: "a seat busy for ever ... is there an end to the wait?" There is
  none on this path.
- **Failure scenario (the ordinary path):** a routed session R1 takes email k-1 and says done on it (or takes two and
  says done on the first, the case the previous answers already met). The owner does a Smart Restart; the drain
  captures R1 and ends it; after the restart the owner presses Bring back and R1 returns as R2 (the record's
  `RestoredSessionId`). Later R2 ends - its agent finishes, or a person closes it - and is reaped. Email k-2 arrives
  for the same seat. Every run from then on: the newest start's row names R1, the record says "back as R2", the seat
  is busy, k-2 waits. Thirty days later it is still waiting.
- **Proof, run on `fb7271c6f`** (in `MailDeskRouterEndingsTests` style, not committed), passed for both
  `finished` and `closed` as R2's ending:

  ```csharp
  var first = _rig.Decide(channel, "k-1", MailDeskWords.Decisions.Route, Books, Clerk, minutesAgo: 30);
  await _rig.RouteAsync();
  var r1 = _rig.Row(first.Id).SessionId!;
  Assert.True(_rig.Routed.Done(MailDeskRoutingRig.Account, _rig.Row(first.Id).Id, r1, "paid", _rig.Now).Succeeded);
  var r2 = _rig.NextSessionId();
  _rig.RestartSeats[r1] = new WorkspaceSeat { SessionId = r1, Name = _rig.Starts[0].Request.Name!,
      Restore = new WorkspaceSeatRestore { Decision = WorkspaceRestoreDecisions.Restore }, RestoredSessionId = r2 };
  _rig.Endings[r1] = SessionHistoryEndings.Closed;
  _rig.Endings[r2] = laterEnding;               // finished, or closed
  _rig.Decide(channel, "k-2", MailDeskWords.Decisions.Route, Books, Clerk, minutesAgo: 1);
  _rig.Now = _rig.Now.AddHours(1);  var soon = await _rig.RouteAsync();
  _rig.Now = _rig.Now.AddDays(30);  var later = await _rig.RouteAsync();
  Assert.Equal((1, 1, 1), (_rig.Starts.Count, soon.Waiting, later.Waiting));   // PASSES: k-2 never starts
  ```

  The control - the same steps without the `RestartSeats` line - starts k-2 at the first run (`soon.Waiting` 0), so the
  restart record is the cause.
- **The tests do not cover it:** every new test of `BackAs` has an OPEN row, which is followed and then judged on the
  new id. None asks `IsSeatBusy` about a done row on an old id whose restored session has since ended.

### 2. The end of the wait for a seat still owed is counted from the session's start, not from the restart - so after a long gap the first check after the way up still re-routes before the Bring back

- **Where:** `src/CcDirector.Gateway/MailDesk/MailDeskRouter.cs`, `FateOf` line 295 (`?? Unseen(sessionId, startedUtc)`),
  `Unseen` line 335, and `RerouteOrphans` line 234 (the start time passed in is the rows' start).
- **What the code does:** a seat on a restart record that is still owed waits only until 24 hours after the ROUTED
  SESSION STARTED. The restart itself, and how long the owner has had to press Bring back, play no part. The record's
  creation time is not even carried into `WorkspaceSeatOnRecord`.
- **The harm:** the same harm as the previous review's finding, reachable whenever the restart comes more than
  24 hours after the session's start, or the way up comes more than 24 hours after the start: the first Mail Desk check
  after the Director comes back re-routes the email and starts a second session, while the offer window still waits
  for the owner; the Bring back then resumes the old conversation holding the same email. Two sessions work one email,
  and the restored one cannot say done (`not_your_email`). The answers state that an operating system shutdown's
  records "wait for the way up like a Smart Restart's"; past 24 hours from the start, they do not.
- **Failure scenario:** Friday 17:00 a routed session R1 starts on k-1 and stops to ask the owner something. Friday
  18:00 the laptop is shut down; the operating system shutdown record captures R1, undecided. Monday 09:00 the owner
  starts the machine; the Director reaches the Gateway and its Mail Desk check is screened within seconds. R1 has no
  row, the record still owes it, no session has its name, and 64 hours have passed since its start: `Unseen` ends it,
  k-1 is re-routed and S2 started. At 09:02 the owner presses Bring back; R1 returns as R2 resuming k-1. The same
  happens on a weekday if the owner presses "Not now" and brings the seats back the next day from File, Restart
  history ("Not now is not final", the `director-restart` skill) - any session started more than 24 hours before that
  press.
- **Not decided here** (the builder's call): the contract's step 3 says a seat still owed "is not ended and its seat
  counts busy" and names no limit; the brief rightly asks for an end. Where that end is counted from - the record's
  creation, the owner's decision, or something else - is a design choice. Today's choice, the session's start, is the
  one that brings back the two-sessions harm.
- **The tests do not cover it:** `RouteAsync_SmartRestart_StillOwedAfter24Hours_IsEnded` proves the end fires; no test
  puts the restart late in the session's life, and none follows the end with the Bring back it pre-empts.

### 3. A restart that was cancelled leaves a record that makes a person's close look like a restart still owed - the email waits up to 24 hours instead of being re-routed at once

- **Where:** `src/CcDirector.Gateway/MailDesk/MailDeskRouter.cs`, `FateOf` lines 289-296; `WorkspaceSeatOnRecord`
  (`src/CcDirector.Gateway/Workspaces/WorkspaceStore.cs` lines 872-878); the record's writer,
  `src/CcDirector.ControlApi/Drain/DirectorDrain.cs`, `CancelAndKeepWorkingAsync` (line 1879, a seat kept running;
  line 1920, `doc.CancelledAtUtc` set) - not changed by this pull request.
- **What the code does:** "Cancel and keep working" during a Smart Shutdown keeps the sessions still open running UNDER
  THEIR OWN IDS, and saves the record with `CancelledAtUtc` set and those seats left with no `ClosedAtUtc`, no
  `RestoredSessionId` and their own restore decision (usually undecided). `FateOf` reads neither `CancelledAtUtc` nor
  `ClosedAtUtc`: any seat on any captured record that is not marked `close` and has no new id is "still owed a Bring
  back".
- **The harm:** a person closing that session later - or its agent finishing without saying done - is read as a restart
  still owed. The contract says a session a person closed is re-routed at once; instead its emails, and every new email
  of its seat, wait until 24 hours after its start, and nothing shows the owner why. This is the "person's close
  mistaken for a restart" the brief asked about. It errs to waiting, not to two sessions, so it is the least of the
  three.
- **Failure scenario:** R1 is working on k-1. The owner starts File, Smart Restart, then presses "Cancel and keep
  working"; R1 had not been closed yet, so it keeps running as R1, and the record holds R1 as an undecided seat. An hour
  later the owner closes R1 by hand. History `closed`, row reaped; the router finds R1 on the record, still owed, no
  session with its name: not ended. k-1 and the seat's new emails wait until 24 hours after R1's start.

## Things checked and found sound (no finding)

- **The reviewer's order, for a seat still owed and within the 24 hours.** Not re-routed, no second session, the new
  email waits, and the record's mapping is followed even when the restored session is not yet in the list and has
  another name. Proven by `RouteAsync_SmartRestart_CheckRunsBeforeTheBringBack_NotRerouted_ThenFollowedByTheRecord`.
- **No record of another account is read.** `SeatsCapturedFrom` opens its context with `_db.CreateContext(tenant)` in
  the routed row's own account; `NewMailDeskRouter_AsGatewayHostWiresIt_ARestartRecordOfAnotherAccountIsNotRead` puts
  the same seat in another account, created inside the read window, and proves it is not read.
- **Another Director's record.** A record is matched by the session id, which names one session; a record of another
  Director of the same account can only match a session that Director captured, which is the right answer.
- **The record is asked once per run**, with the same session ids as the history endings and the oldest start
  (`RouteAsync_ManySessions_AsksForTheRestartRecordsOnceWithTheSameSessionsAndTheOldestStart`); `IsSeatBusy` reuses the
  run's facts. The lookup by name inside `FateOf` is still per session, as before this change.
- **The read window.** `CreatedUtc` is set by the Gateway when the record is first saved (`WorkspaceStore.Save`) and
  kept on later writes, so a mark of a reopen does not move it out of the window, and the Director's clock does not
  enter.
- **`director-stopped` waits like `interrupted`**, and **`closed` with no record is re-routed at once** - both proven by
  tests at 1 minute and 23 hours, and after 24 hours.
- **No real clock, sleep, process or port** in the new tests: the rig's injected clock and database, a fake record
  reader, and a real `WorkspaceStore` over the rig's database. A search of the added test lines for the real clock,
  sleeps, delays, processes and ports finds none.
- **A damaged record is skipped and logged.** That session then reads as having no record, so a `closed` ending
  re-routes it at once - the two-sessions side. I take it as a declared choice, since a damaged record is rare and the
  skip is logged; I can show no ordinary path to it.
