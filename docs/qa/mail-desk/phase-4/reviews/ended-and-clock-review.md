# Review: a routed session ended on its history record (pull request 4283) and the test clock (pull request 4282)

Reviewer seat: "Mail Desk - Reviewer - session ended and test clock", opened by the phase 4 Tech Lead (session 128).
Date: 20 October 2026.

## Scope

What I read, at these exact commits (fetched from origin on 20 October 2026):

- Pull request 4283, `origin/md4-ended` at `5e186237b`, against `origin/mail-desk-phase-4` at `cc97a11e8`: every line of
  the diff - `MailDeskRouter.cs`, `GatewayHost.cs`, `MailDeskRoutingRig.cs`, `MailDeskRouterTests.cs` and the new
  `MailDeskRouterEndingsTests.cs`.
- Pull request 4282, `origin/md4-clock` at `e8cb07c4b`: the one-line diff.
- Around the diff, to judge it: `SessionHistoryStore` (`UpsertLive`, `RecordEnding`, `MarkDirectorStopped`,
  `CloseAbsentSessions`, `ConcludeInterrupted`, both `EndingsOf` overloads), `SessionHistoryRecorder`
  (`ObserveRemoval`, `ObserveDirectorStopping`), `DirectorHub.DirectorStopping`, `FleetManagerSessions.IsGone`,
  every use of `_sessionHistory` in `GatewayHost.cs`, the Smart Restart drain (`DrainSessionControl.EndAsync`) and
  the restore (`DirectorWayUp.BuildReopen`), and `MailDeskRoutedStore.Done`.

What I ran (Linux, devlinux):

- On `md4-ended`: `dotnet test src/CcDirector.Gateway.UnitTests --filter "MailDesk|Trigger|SessionHistory"` -
  1,079 passed, 0 failed.
- On `md4-ended`: `dotnet test src/CcDirector.Core.UnitTests --filter GateTestsDoNotDependOnTheMachine` - 1 failed,
  exactly the expected one: `real-clock | src/CcDirector.Gateway.UnitTests/MailDesk/MailDeskRouterTests.cs | 1 (new)`.
- On `md4-ended` merged with `md4-clock` (in a scratch worktree, since removed): the same guard - 11 of 11 passed; and
  `MailDeskRouterTests.Context_*` - passed.
- A throwaway probe test, never committed, added to `MailDeskRouterEndingsTests` in that scratch worktree, for finding
  1: it failed as finding 1 predicts (details there).

What I could not reach: no real Director, no real Smart Restart, no Gateway restart. Finding 2 is argued from the code
paths named, not reproduced end to end.

## Verdicts

- **Pull request 4282 (test clock): approve.** The received time now comes from `_rig.Now`. The guard goes from 1
  failure to none, and the test it touches still passes. The test only measures how long the context text is, so the
  received time's value never mattered to what it proves. No findings.
- **Pull request 4283 (session ended): changes needed - 2 findings.** The account is right (the router calls the
  `EndingsOf(tenant, ids)` overload, which opens its context in that account, not the ambient one). It asks once per
  run, with the session of every open row and the newest session of each seat with a queued email; a test proves
  one call. `interrupted` and no record keep the 24-hour rule, and tests prove both. The `GatewayHost` move is safe: the
  field is `readonly` and assigned exactly once, `_gatewayDb` is made at line 2177, long before the new place at line
  2873, and the only earlier reader (the cron run results, line 2778) is a lambda that runs after construction - one
  store instance is shared by everything. The tests use the fake clock and the rig's database: no sleep, no port, no
  process. The two findings below are about WHEN a final history ending is believed.

## Findings

### 1. A session still live in the session list is counted as ended when its history record holds a stale final ending

- **Where:** `src/CcDirector.Gateway/MailDesk/MailDeskRouter.cs`, lines 373-381 (`IsAlive`). The history check at line
  376 now runs BEFORE `if (row is not null) return true;` at line 381. Before this change a row present and not gone
  was always alive.
- **Why a live session can have a final ending:** a history record that is `closed`, `finished` or `director-stopped`
  is never reopened. `SessionHistoryStore.UpsertLive` reopens only an `interrupted` row (lines 56-62); a session that
  shows up again on the stream after any other ending keeps that ending for good. Two real ways to get there:
  - A late farewell. `DirectorHub.DirectorStopping` (line 1318) stamps every open row of that Director
    `director-stopped`, and the comment at line 1326 says plainly that this ruling is "deliberately NOT gated" by
    whether the connection is still the active one. So a delayed farewell from a connection a reconnect already
    replaced marks every session of a Director that is still running as `director-stopped`.
  - `CloseAbsentSessions` rules `closed` any open row missing from a snapshot; if that session is pushed again later,
    `UpsertLive` leaves it `closed`.
- **The harm:** the router re-routes the emails of a session that is running and working on them, and starts a second
  session on the same emails at the next run - the exact "two sessions on one email" the 24-hour rule exists to
  prevent, and now reachable even with the session in plain sight.
- **Failure scenario, run:** in the probe, a session took email k-1, then its row was put back in the session list
  as live (`ActivityState = "Working"`, not crashed) and its history ending set to `director-stopped`, then to
  `closed`. Five minutes later `RouteAsync` re-routed it: `Expected: 0, Actual: 1` re-routed, for both endings.
- **Not covered by the tests:** every new test removes the row from the session list first
  (`Assert.False(_rig.Sessions.ContainsKey(session))`), so no test covers a row present together with a final ending.
- **Note on the contract:** section 3.2 step 3 reads "a session row with IsGone, OR a history ending of closed,
  finished or director-stopped". The code follows that text literally. The harm is still real; whether the contract
  wording or the code changes is the builder's call.

### 2. A Smart Restart ends a routed session `closed` and brings it back under a new id - the emails are now re-routed within minutes, so two sessions work them

- **Where:** `src/CcDirector.Gateway/MailDesk/MailDeskRouter.cs`, line 376, and `EndedForGood` (the `closed` and
  `director-stopped` cases).
- **The path, as read:** the Smart Shutdown drain ends each seat with a kill (`DrainSessionControl.EndAsync`, keeping
  the kit for the restore). The removal reaches `SessionHistoryRecorder.ObserveRemoval`, which records `closed` (or
  `finished`). On the way up, `DirectorWayUp.BuildReopen` creates the seat again as a NEW session: a new session id,
  `ResumeSessionId` set to the same agent conversation, the same factory, seat and kit folder (with its `work/`), and
  the same origin. Its comment says it: "a reopen RESUMES the same conversation, so the agent comes back believing it
  is the same session mid-task." The routed rows still name the OLD session id.
- **The harm:** before this change the old session was simply absent, so the 24-hour rule held its emails while the
  restored seat finished them. Now the old id's `closed` ending ends it at the very next Mail Desk run: its emails are
  re-routed and a fresh session is started on them, while the restored seat is still working the same emails from its
  resumed conversation. A Director auto-update performs this restart, so it is a routine event, not a rare one.
- **Failure scenario:** a routed session holds email k-1 and is mid-work. The owner's Director updates itself; the
  drain ends the session (history: `closed`) and the restore reopens it as a new session that resumes the conversation
  holding k-1. Five minutes later the Mail Desk check runs: the old id has no row and a `closed` ending, so k-1 is
  re-routed and a second session starts on it. Both act on the same email (both may file it, answer it, or pay it).
- **Pre-existing, made acute:** the restored seat cannot answer for the email even before this change -
  `MailDeskRoutedStore.Done` refuses a caller whose session id is not the row's (line 394) - but the 24-hour rule hid
  that for a day; this change exposes it at once.
- **Not reproduced:** I did not run a real Director restart; this rests on the code paths named above.

## Things checked and found sound (no finding)

- The account: `GatewayHost.NewMailDeskRouter` passes `sessionHistory.EndingsOf(tenant, ids)`, the explicit-account
  overload, not the ambient one.
- Once per run: `EndingsOfThisRun` is called once in `RouteAsync`, after the unknown starts are settled; the test
  `RouteAsync_ManySessionsAndRows_AsksForTheHistoryEndingsOnceForEverySessionItReads` proves a single call holding
  both sessions.
- `interrupted` and no record: both still wait 24 hours, proven through the fake and through the real store
  (`NewMailDeskRouter_AsGatewayHostWiresIt_AnInterruptedRecordDoesNotEndTheSession`).
- A cleanly ended session no longer holds its seat:
  `RouteAsync_SeatsLastSessionSaidDoneThenWasReaped_WithAFinalHistoryEnding_StartsTheNewEmailAtOnce` covers it.
- The host wiring test builds the router through the same static method the host calls, over the real store, so it
  would fail if the host stopped passing the endings.
