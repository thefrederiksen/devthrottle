# Worker G - a completed stop that nobody recorded (inspection 1, finding I1)

Session `53e4e96f-6c32-43a3-a3ba-e532213f0300`, seat Worker G, running Claude Code.
Branch `mission/stop-a-session`. Commits `75b45c27` and `d79017a4`.

---

## What was wrong, and what it now does

The stop handler handed the request's cancellation token to the tunnel and appended the governance
audit row **only after a successful reply**. Two ordinary things therefore destroyed a session and
left no record of it:

- the caller hung up after the Director had carried the stop out - which is a person closing the
  desktop stop dialog, or that dialog's own ten-second client giving up;
- the tunnel timed out or dropped, and the handler returned through `TunnelFailure` without writing
  anything, even though the Director may well have carried the stop out.

The owner accepted Ruling 4 - any session may stop any other - **on the explicit ground that stops
are audited**. An audit that only lands while the caller is still listening is not an audit.

**The row now belongs to the dispatch, not to the reply.** Everything after the command is sent runs
inside one `try`, and an `OperationCanceledException` raised anywhere inside it writes the row before
the cancellation is allowed to continue. The cancellation stays a cancellation - it is not reported
as anything else.

**Where the Gateway sent a stop and cannot learn what came of it, the row says exactly that**, in
words, carrying the reason and the actor. It is the same event type (`stopped`) as any other stop,
deliberately: a reader looking at the stops for a session has to see it, which a separate event type
nobody queries would prevent. **No fifth verdict word was added** - nothing about the response
changed, and this is an audit detail.

**Where nothing was dispatched, nothing is written.** A Director that was never connected, and a
failure the Director itself reported, both write no row - see the boundary section below.

**The reason now travels down the tunnel** instead of the null payload.

## The files

### `src/CcDirector.Gateway/Api/GatewayEndpoints.cs`

| Change | What it does |
|---|---|
| Two private constants | `StopOutcomeUnknownPrefix` opens the detail of a stop whose outcome the Gateway never learned; `CallerLeftBeforeTheAnswer` is the cause recorded when the caller hung up. |
| `StopSessionAsync`, dispatch region | Wrapped in one `try`, with `catch (OperationCanceledException)` that records the row and rethrows. A `RecordOnce` local function with a `recorded` flag, so one dispatch can only ever produce one row. |
| The tunnel call | Sends `new SessionStopRequest { Reason = reason }` instead of `null` (and still `null` on the reasonless legacy door, so that door sends nothing new). |
| The tunnel-failure branch | Asks `DispatchOutcomeUnknownBecause` whether anything was actually sent, and records an unknown-outcome row when it was, before returning the same `TunnelFailure` answer as before. |
| `DispatchOutcomeUnknownBecause` (new) | The one place that decides "was a stop actually sent, and do we know what came of it". |
| `StopAuditDetail` (new) | Composes the detail. For a known outcome it is the reason, byte for byte as before. For an unknown outcome the unknown is said **first** and the reason follows, because the cap truncates the tail - a five-hundred-character reason loses the reason, never the fact that the outcome is unknown. Capped to `GovernanceAuditLog.MaxDetailChars`, which the trail **rejects** rather than trims; an uncapped detail would have been swallowed by the best-effort catch and lost the very row this fix adds. |
| `RecordStopInTheAuditTrail` | Takes a nullable verdict (log line only) and an optional `outcomeUnknownBecause`. Still best-effort - a database fault still does not fail the response - and still logs loudly. It takes no cancellation token, and the comment says why it never will. |

### `src/CcDirector.Gateway.Tests/SessionStopEndpointTests.cs`

A `sendOverride` on the existing harness, so a test can control **when** the Director answers rather
than only what it says; a `WaitForTheStopToBeRecorded` helper; and eight tests.

## Did the Director need changing? No, and the payload has no consumer yet

Checked before changing the call, as the brief asked. `SessionCommandExecutor.KillAsync` **never
reads `command.PayloadJson`** - `SessionWriteExecutor` dispatches `"kill"` straight to it and the
executor works from `command.SessionId` alone. **So the Director ignores the reason today and no
consumer for it is claimed.** The Gateway sends it so that a Director half can read it without the
Gateway having to change again. The code comment says this in the same words; nobody should read the
send as evidence that the Director records anything.

## The mutation table - red messages exactly as they printed

Every run is the whole `SessionStopEndpointTests` class (26 tests) against the real endpoint over
HTTP with a real `GovernanceAuditLog` on a real database harness. Unmutated: **26 passed, 0 failed**.

| # | Mutation | Result | The failure, verbatim |
|---|---|---|---|
| M1 | Delete `RecordOnce(verdict: null, CallerLeftBeforeTheAnswer);` from the cancellation catch - the append goes back to belonging to the reply. | **Failed: 1, Passed: 25** | `A_stop_the_caller_stopped_waiting_for_is_still_recorded_with_its_reason_and_actor` — `The stop was dispatched to the Director and no audit row was ever written for it. Ruling 4 was accepted on the ground that stops are audited (inspection 1, finding I1).` |
| M2 | The tunnel-failure branch returns without recording, exactly as it did before. | **Failed: 2, Passed: 24** | `A_stop_the_director_never_answered_is_recorded_as_a_stop_whose_outcome_is_unknown` and `A_stop_whose_tunnel_dropped_mid_flight_is_recorded_as_a_stop_whose_outcome_is_unknown` — both `Assert.Single() Failure: The collection was empty` |
| M3 | Hand the request's token back to the append: `if (recorded \|\| ct.IsCancellationRequested) return;`. | **Failed: 1, Passed: 25** | `A_stop_the_caller_stopped_waiting_for_is_still_recorded_with_its_reason_and_actor` — `The stop was dispatched to the Director and no audit row was ever written for it. Ruling 4 was accepted on the ground that stops are audited (inspection 1, finding I1).` |
| M4 | Put the `null` tunnel payload back. | **Failed: 1, Passed: 25** | `The_reason_travels_down_the_tunnel_with_the_stop` — `Assert.Contains() Failure: Sub-string not found` |

In every mutation the **uncancelled control** stayed green, which is the point of it: the cancelled
test and the control set up an identical Director with an identical held-back answer and differ in
one line, whether the caller cancels.

**Two mutation runs were thrown away rather than reported.** The first attempt at M2 left
`DispatchOutcomeUnknownBecause` uncalled, so the build failed and `--no-build` ran the previous
mutant's binary - it printed M1's failure, which would have been a fabricated result. The first
attempt at M4 hit a build error from another seat's in-flight code in the shared tree, with the same
consequence. Both were re-run after fixing the cause, and only the re-runs are in the table above.
**A test result is only real if the build that produced the binary succeeded**; two runs here looked
plausible and were not.

## The suites, run after the code was final

| Suite | Result |
|---|---|
| `CcDirector.Gateway.UnitTests`, whole suite | **Passed: 4233, Failed: 0, Skipped: 8, Total: 4241**, 2 minutes 31 seconds. |
| `SessionStopEndpointTests` alone, in the parked suite, unmutated | **Passed: 26, Failed: 0** (the whole class, run against the real endpoint over HTTP). |
| `CcDirector.Gateway.Tests` (**parked**), whole suite | **I did not run it, and there is no number here.** The Manager stood the run down and is running the parked gate once, on the final code, after every Phase C seat is done - which is the only run that means anything, since my code is not what will ship until the other seats' changes are in beside it. I had started one; it was stopped. |

**My own stopped run blocked another seat for thirty-eight minutes.** The parked suite is serialised by
a per-user lock and my run held it from 14:07 to 14:56 UTC while another run sat queued behind it,
logging "still waiting" every thirty seconds. The queue is working as designed (issue #1156) - the
cost was mine for starting a forty-minute run I had not been asked for.

## The boundary: where a row is deliberately NOT written

- **A Director that was never tunnel-connected.** The command never left the Gateway, so a row would
  attach a stop to something that did not happen. Pinned by
  `A_director_that_was_never_connected_writes_no_row_because_nothing_was_sent`.
- **A failure the Director itself sent** - `BadRequest`, `NotFound`, `Conflict`, `Locked`, `Error`.
  The Director was reached and answered; it is the one party that knows, and it is saying what it
  did. The live example is "the process would not die": the Director deliberately leaves the row in
  place and the session is still running, so recording a stop against it would be false. This is a
  **known** outcome, not an unknown one. Pinned by
  `A_failure_the_director_itself_reported_writes_no_row_because_nothing_was_stopped`.
- **`notOnFleet` and a stop refused for a missing reason** - unchanged, and their existing tests
  still pass.

## What is NOT proven - named honestly

1. **A tunnel drop cannot be told apart from a send that never left, and I recorded it as unknown
   anyway.** `DirectorCommandRouter` returns `TunnelDropped` when the send throws while its own
   deadline has not expired. That covers both "the exception was raised before anything reached the
   Director" and "it was delivered and the connection then died", and nothing in the result
   distinguishes them. So **a tunnel that dropped before the command left now produces a row saying a
   stop was sent and its outcome is unknown, for a stop that may never have happened.** I chose this
   deliberately, on the brief's instruction that silence is the worse error - but the row is not
   evidence that anything was stopped, and nobody should read it as such. Closing this properly needs
   a delivery acknowledgement the tunnel does not have.
2. **The timeout and tunnel-drop tests inject the router's already-synthesized failure result**; they
   do not sleep through a real thirty-second deadline. What the router does with a genuinely expiring
   deadline is covered by `DirectorCommandRouterTimeoutTests`. **The join between a real expiring
   timeout and this handler is not exercised by anything I wrote.**
3. **The claim that a caller cancellation can surface as `TunnelDropped` rather than as an
   `OperationCanceledException` is read off `DirectorCommandRouter`'s own comment about SignalR, not
   observed.** Both roads write a row, so the behaviour is right either way - but which one a real
   cancellation actually takes in production is unverified, and only the exception road has a test
   that drives a genuine cancellation.
4. **The Director is a stub throughout.** Nothing here proves a real agent process was ended, that a
   real SignalR tunnel behaves as modelled, or that the audit row lands in the production database
   rather than the test harness's. No browser, no desktop application, no live fleet.
5. **The cancellation test waits up to twenty seconds for the row.** A cancelled request is not
   synchronous with the client giving up - the server learns of the abort a moment later - so the
   test waits with a deadline rather than reading once. It proves the row **lands**; it does not
   bound how quickly, and it would not catch the row becoming slow.
6. **An attempted destructive act that the Director refused still leaves no governance row at all.**
   "The process would not die" is a stop that was asked for, tried, and failed, and the trail says
   nothing about it. That is out of scope for I1 - the caller is told the truth in the response - but
   it is a real gap in what the trail holds, and it is the Architect's call, not mine.
7. **No web, no command line, no Python** - I touched none of those and ran none of their suites.

## Two things for the Architect, found on the way and not fixed

1. **`AutoDismissSweeper` ends sessions with the same `kill` verb and writes no audit row at all**
   (`src/CcDirector.Gateway/Running/AutoDismissSweeper.cs`, around `:133`). It also sends a null
   payload. If Ruling 4's ground is that stops are audited, this is a second path that stops sessions
   outside the trail. It is not I1 and I left it alone.
2. **The extra unknown-outcome rows do not disturb the one derived number that reads this trail.**
   Checked rather than assumed: `OutcomeLedgerReporter` explicitly excludes
   `GovernanceAuditEventType.Stopped` from its intervention count, and says in its own comment why.
   Anything counting `stopped` rows in future will now be counting stops whose outcome is unknown
   alongside completed ones; the detail text is what tells them apart.

## One incident on the shared branch, recorded because it cost real time

Five seats are working in **one** worktree this phase. Commit `a6c4c28c` carries a documentation
message and also **deleted three lines of the stop handler** - the call that records an unknown
outcome after a tunnel failure. Those lines were a deliberate mutation of mine sitting in the shared
working tree at the moment that commit ran `git add -A`. With them gone the local function they
called is never used, so **the branch did not compile** at that commit. Restored in `d79017a4`,
identical to `75b45c27`. Nothing else of mine was lost, and the Manager was told.

The lesson is not "be careful with `git add -A`", which was already known. It is that **a mutation
left in a shared tree is indistinguishable from work**, so from M2 onwards every mutation here was
applied, built, run and restored inside a single command, with the restore verified before the next
step.
