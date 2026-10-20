# Review: the answers to the restart record review (pull request 4283, commit 2155bb9a9)

Reviewer seat: "Mail Desk - Reviewer - restart wait answers", opened by the phase 4 Tech Lead (session 128).
Date: 20 October 2026.

## Scope

What I read, at `2155bb9a9` (checked out detached; `origin/mail-desk-phase-4` fetched on 20 October 2026):

- `reviews/ended-restart-review.md` (the three findings this commit answers) and the new section of
  `reviews/ended-answers.md`.
- `CONTRACT.md` on `origin/mail-desk-phase-4`: section 1, "A busy seat", and section 3.2 step 3.
- Every line of the commit: `MailDeskRouter` (`FactsOfThisRun`, `RerouteOrphans`, `FateOf`, `ByName`, `IsSeatBusy`,
  `RestartOwedFor`, `MaxRestartsFollowed`), `MailDeskRoutedStore.Follow` and `MailDeskRoutedReasons.NotBroughtBack`,
  `WorkspaceStore.SeatsCapturedFrom` and `WorkspaceSeatOnRecord`, the rig, and the new and changed tests in
  `MailDeskRouterEndingsTests`, `MailDeskRestartRecordReadTests` and `MailDeskRoutedStoreTests`.
- Around the diff, to judge it: `MailDeskRoutedStore.HandToOwner` and `ToOwner`; `MailDeskRoutedFold.OwnerProblems`
  (the problem and the 08:00 line); `WorkspaceStore`'s restore marks (who writes `StartedAtUtc` and `ReopenedAtUtc`, and
  on which clock); `DirectorDrain.CancelAndKeepWorkingAsync` (`src/CcDirector.ControlApi/Drain/DirectorDrain.cs`
  lines 1819-1960); `DirectorWayUp.HasSomethingToActOn` and `BuildHistoryEntry`.

Not in scope, not reopened: anything earlier reviews settled (reading the record at all, the account boundary, the
read window, the order of row, record and history).

What I ran (Linux, devlinux), on `2155bb9a9`:

- `dotnet test src/CcDirector.Gateway.UnitTests -p:EnableWindowsTargeting=true --filter
  "FullyQualifiedName~MailDesk|FullyQualifiedName~Workspace|FullyQualifiedName~Restart"` - 1,318 passed, 1 failed
  (`HeldDeliveryDriverTests.ARecordWriteThatThrowsAfterTheClaimMark_IsPressedAgainByTheDriver_ResolvedWithNoRestart`,
  which fails on this machine on main too, as the brief says), 0 skipped.
- Two probe tests of my own, added to `MailDeskRouterEndingsTests` for the run and then removed (not committed; the
  file was restored with `git checkout` before the suite run above). Both are quoted below so the builder can turn them
  into regression tests. Each passed on `2155bb9a9` - that is, each proved its defect - and each has a control case
  that passed with the opposite result.

## Verdict

**Two findings. Not ready to merge as it stands.** Finding 1 puts two sessions on one email in the weekend scenario
the brief names, through a path the commit says it covers ("followed up to three restarts deep") but does not.
Finding 2 puts two sessions on one email for a short window after "Cancel and keep working". Both are the harm the
contract rules out; both have a narrow fix.

The three findings of the previous review are otherwise met - see "Checked and found sound" at the end.

## Findings

### 1. A seat restarted a second time before the router runs is judged without its second restart record - the email is re-routed while the second record still owes the Bring back

- **Where:** `src/CcDirector.Gateway/MailDesk/MailDeskRouter.cs`, `FactsOfThisRun` line 212 (the restart records are
  asked about `ids`, the open rows' own sessions, only) and line 216 (the session a record says one came back as is
  added to the HISTORY question only); `FateOf` line 309 (`facts.RestartSeats.TryGetValue` for that session at depth 1,
  which can never hit) and lines 330-337 (so it falls through to its history ending alone).
- **What the code does:** for R1 the first record says "brought back as R2", and `FateOf` judges R2 with the ordinary
  rules - but R2 was never asked of the restart records. If a second restart captured R2 and still owes it, `FateOf`
  does not know: R2 has no row, its history ending is `closed` (a Smart Restart's drain) or `director-stopped` /
  `interrupted` (an operating system shutdown), the follow by name finds nothing because R3 does not exist yet, so R2 is
  ended - at once for `closed`, or by the 24-hour rule counted from R2's start for the others - and the email is
  re-routed. `MaxRestartsFollowed` (line 343) and the depth parameter therefore never reach a second level, and the
  answers' sentence "A session restarted again before the router saw it is followed up to three restarts deep" is not
  true. No test exercises it.
- **The harm:** two sessions on one email, the very thing the 72-hour rule exists to prevent. The owner later presses
  Bring back on the second record; R3 resumes the old conversation holding the email, while the re-routed session S2
  works it too, and R3 cannot say done (`not_your_email`).
- **Failure scenario (a weekend):** Friday 16:30 a routed session R1 holds k-1. Friday 16:40 the owner does a Smart
  Restart to take an update and presses Bring back at once: R1 returns as R2. Friday 17:00 the laptop is shut down
  before the next Mail Desk check; the operating system shutdown's record captures R2, undecided. Monday 09:00 the first
  check runs within seconds of the Director coming back: R1 is on record 1, back as R2; R2 has no row, and record 2 is
  never read; R2's ending is `closed` (or, for an operating system shutdown, `director-stopped` or none, and R2 started
  64 hours ago, past its 24 hours). R2 is ended, k-1 is re-routed, S2 is started. 09:02 the owner presses Bring back on
  Friday's shutdown: R3 resumes k-1.
- **Proof, run on `2155bb9a9`** (not committed). It passes for `secondRestart` true and false alike - the second record
  changes nothing - and it asserts the re-route and the second session:

  ```csharp
  [Theory]
  [InlineData(true)]
  [InlineData(false)]
  public async Task Probe_TwoRestartsBeforeTheRouterRuns_TheSecondStillOwed(bool secondRestart)
  {
      var (held, session) = await SessionReapedWithAnotherEmailWaitingAsync();
      var name = _rig.Starts[0].Request.Name!;
      var r2 = _rig.NextSessionId();
      var first = CapturedSeat(session, name);
      first.RestoredSessionId = r2;
      first.Restore!.StartedAtUtc = _rig.Now;
      _rig.Endings[session] = SessionHistoryEndings.Closed;
      _rig.Now = _rig.Now.AddMinutes(10);
      if (secondRestart) CapturedSeat(r2, name);          // a second record, still owing R2 a Bring back
      _rig.Endings[r2] = SessionHistoryEndings.Closed;
      _rig.Now = _rig.Now.AddMinutes(1);
      var run = await _rig.RouteAsync();
      Assert.Equal((1, 2), (run.Rerouted, _rig.Starts.Count));   // PASSES with the second record: re-routed, two sessions
  }
  ```

  The rig's fake answers exactly what it is asked, as the production store does (`SeatsCapturedFrom` returns only the
  ids passed in), so the probe stands for production.
- **What would close it** (the builder's call): ask the records again for the sessions the first answer names (a loop
  to the depth limit, or one more question), so `FateOf` sees each level's record. Once that is done, note that at
  depth 1 or more `FateOf` returns `then with { Why = "... the restart record <id> brought it back as <session id> ..." }`
  and `RerouteOrphans` writes `fate.Why` as the owner reason (line 243): the owner's problem and the 08:00 line would
  then show a record id and a session id. At depth 0 the reason is the clean `NotBroughtBack` sentence. Today that
  path is unreachable, which is this finding.

### 2. "Cancel and keep working" brings the already shut-down seats back under NEW ids, but the record that says so is now ignored - a router run inside the Bring back re-routes the email

- **Where:** `src/CcDirector.Gateway/Workspaces/WorkspaceStore.cs` line 767 (`if (doc.CancelledAtUtc is not null)
  continue;` - the whole record is skipped); the writer, `src/CcDirector.ControlApi/Drain/DirectorDrain.cs`,
  `CancelAndKeepWorkingAsync`, lines 1835-1850 (seats already closed go in `closed`), 1896-1914 (each is decided
  "restore"), 1919-1932 (the record is saved CANCELLED, before the restore, "which reads it from the Gateway") and
  1934-1940 (`smart.BringBack` starts each closed seat again, under a new session id).
- **What the code does:** the answer to the previous finding 3 says "Cancel and keep working is not a restart, its
  sessions kept their own ids, and the way up never offers anything in it." That is true only of the seats still OPEN
  at the cancel. A seat the drain had ALREADY shut down (history `closed`) is on the same record, decided "restore", and
  is brought back by the cancel itself, on the same Director, under a new id, with `RestoredSessionId` recorded on that
  cancelled record. Skipping the record drops exactly the fact that tells the router to wait. Until the restored session
  is in the Gateway's session list (so the follow by name can find it), the router sees: R1 `closed`, no record, no
  session with its name - ended at once.
- **The harm:** two sessions on one email. The window is from the cancelled save until the restored session is listed:
  the Bring back starts seats one after another, leads first, each through a Gateway spawn and a session snapshot, so
  with several closed seats it is many seconds. It is longer when a spawn times out (`TimedOut` in
  `DirectorRestore`: "the Gateway may still start the seat") - then the router has already re-routed when the restored
  session appears. Before this commit the record was read and the seat waited; the change made this path worse.
- **Failure scenario:** R1 holds k-1. The owner starts a Smart Restart; the drain shuts R1 down (history `closed`) and
  is still waiting on other seats when the owner presses "Cancel and keep working". The record is saved cancelled; the
  Bring back of the closed seats begins. A Mail Desk check is screened on that Director in the meantime: R1 is closed,
  the record is skipped, nothing has R1's name yet, so k-1 is re-routed and S2 started. Seconds later R1 returns as R2,
  resuming k-1.
- **Proof, run on `2155bb9a9`** (not committed), over the real `WorkspaceStore` as the host wires it. Cancelled: one
  re-route, a second session. The same record not cancelled: nothing re-routed, one session:

  ```csharp
  [Theory]
  [InlineData(true)]
  [InlineData(false)]
  public async Task Probe_CancelledRecord_SeatShutDownBeforeTheCancel_BringBackUnderWay(bool cancelled)
  {
      var (held, session) = await SessionReapedWithAnotherEmailWaitingAsync();
      _rig.UseGatewayHostWiring(new SessionHistoryStore(_rig.Db));
      WriteHistoryRecord(session, SessionHistoryEndings.Closed);
      _rig.SaveRestartRecord("restart-cancelled", cancelled ? _rig.Now : null, new WorkspaceSeat
      {
          SessionId = session,
          Name = _rig.Starts[0].Request.Name!,
          ClosedAtUtc = _rig.Now.AddMinutes(-1),
          Restore = new WorkspaceSeatRestore { Decision = WorkspaceRestoreDecisions.Restore, Command = "bring back" },
      });
      _rig.Now = _rig.Now.AddMinutes(1);
      var run = await _rig.RouteAsync();
      Assert.Equal(cancelled ? (1, 2) : (0, 1), (run.Rerouted, _rig.Starts.Count));   // PASSES both ways
  }
  ```
- **What would close it** (the builder's call): on a cancelled record, skip only the seats the cancel kept running
  (no `ClosedAtUtc`), and keep reading the seats it shut down - those are brought back by the cancel, and the record
  carries their new ids. The new test
  `NewMailDeskRouter_AsGatewayHostWiresIt_ACancelledRestartRecordIsIgnored_APersonsCloseIsReroutedAtOnce` uses a seat
  with no `ClosedAtUtc` (one kept running), so it would still hold.

## Checked and found sound (no finding)

- **Finding 1 of the previous review: nothing waits for ever.** `IsSeatBusy` (line 521) is now a start inside its
  grace, or an open row with a session after step 3; a `done` row never counts. Every open row with a session reaches
  one of: alive (a live row), followed, re-routed, handed to the owner, or waiting under a rule with an end - the 24
  hours from the session's own start, or the 72 hours from the record. The reviewer's probe is now
  `RouteAsync_DoneRowOnASessionBroughtBackThatThenEnded_NeverKeepsTheSeatBusy`, with the opposite assertion. The busy
  rule matches section 1 of the contract as it stands on `origin/mail-desk-phase-4` ("A session that has said done or
  returned every email it was given no longer keeps the seat busy ... ruled 20 October 2026"), so the note to the Tech
  Lead in the answers is already met.
- **The 72 hours are on the Gateway's own record time.** `RestartOwedFor` is compared with `onRecord.RecordedUtc`
  (line 324), which is `WorkspaceEntity.CreatedUtc`, set by the Gateway when the record is first saved. The session's
  start plays no part (`RouteAsync_SmartRestartLateInTheSessionsLife_WaitsFromTheRestart_NotFromTheSessionsStart`).
  The start a followed row takes, `BroughtBackAtUtc`, is the Started or Reopened mark's time, which `WorkspaceStore`
  writes from the Gateway's own `nowUtc` and keeps against a Director's later saves (lines 391-401) - not the
  Director's clock either.
- **The weekend with one restart.** Checks at 1 minute, 24 hours and 59 hours wait; the Bring back at 60 hours is
  followed, never re-routed, one session (`RouteAsync_SmartRestartOverAWeekend_...`). I read the path and agree. The
  weekend with TWO restarts is finding 1.
- **The hand to the owner loses nothing.** `HandToOwner` goes through the same `ToOwner` as every other hand: state
  `returned`, `OwnerReason`, `OwnerUtc`; the decision keeps `seat-started` (`failDecision: false`). The problem and the
  08:00 line are folded from exactly those columns (`MailDeskRoutedFold.OwnerProblems`, lines 75-123), so the email
  appears as a "Mail handed to you" problem with the reason "its session was ended by a Director restart and was not
  brought back within 72 hours", and in the 08:00 section, like any other. Each open row of the session is handed, and
  the seat is then free (asserted in the 71/72-hour test).
- **The `WorkspaceStore` change.** It is there to carry the record's creation time into `WorkspaceSeatOnRecord` (for
  the 72 hours) and to skip cancelled records. `SeatsCapturedFrom` and `WorkspaceSeatOnRecord` have exactly one
  production caller, `GatewayHost.NewMailDeskRouter` (line 491); a search of `src` outside the test projects finds no
  other. Nothing outside the Mail Desk changes. Whether skipping the WHOLE cancelled record is right is finding 2.
- **A followed row takes the new session's start**, so R2 is not judged by R1's 24 hours
  (`RouteAsync_SessionBroughtBackLateInTheFirstSessionsLife_IsJudgedFrom24HoursAfterItsOwnStart`, and
  `Follow_...` asserts the written start).
- **No real clock, sleep, process or port in the added test lines.** A search of every added line in the test projects
  for `DateTime.Now`, `DateTime.UtcNow`, `Task.Delay`, `Thread.Sleep`, `Process`, `Stopwatch`, `localhost`, `Port` and
  `TcpListener` finds none; the tests use the rig's injected clock, its in-memory database and fakes, and the real
  `WorkspaceStore` and `SessionHistoryStore` over that database.
