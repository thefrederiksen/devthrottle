# Mail Desk phase 4, task B (live routing to seats): review

Reviewer: session "Mail Desk - Reviewer - live routing", opened by the phase 4 Tech Lead (session 357bed9d), 19 October
2026. A different agent from the Developer. I read and ran; I changed no code.

## Scope

**What I reviewed:** pull request 4275, branch `md4-routing` at `d5cc5d9ee`, against `origin/mail-desk-phase-4` at
`6e3726f28` (the branch is cut from the phase branch's tip; checked with `git merge-base --is-ancestor` after a fetch).

**What I read, line by line:**

- `src/CcDirector.Gateway/MailDesk/MailDeskRouter.cs` and `MailDeskRoutedStore.cs` (all of both).
- The `TriggerService` change (`FinishScreeningAsync` split into the record and the router) and where the `_screening`
  entry and the per-trigger lock are released; `HandToMailDesk`; `DefinedStart` and `IsLastSessionAlive` to compare.
- The `GatewayHost` wiring: the shared starter, `LastKnownSession`, `LastKnownTriggerSessionByName`.
- `PushedSessionStore` (in memory, how a Director's sessions are kept and forgotten) and the Director registry's
  eviction horizon.
- The Director's create checks in `src/CcDirector.ControlApi/SessionCommandExecutor.cs` (`SettleSeatStart`, the origin
  and origin surface checks) and `FactoryRunKit.WithCheck` (what `check.json` needs: a check path, a count above 0).
- `FactorySeatStart.Build`; `MailDeskDecisionStore.Record` (a kept decision changes only when it is `failed`, so a
  routed decision is never rewritten under its routed row).
- Both migrations (SQLite `20261020020440`, PostgreSQL `20261020020600`), both snapshot changes, and the model change.
- The tests: `MailDeskRouterTests`, `MailDeskRoutedStoreTests`, `MailDeskRoutingRig`,
  `AddMailDeskRoutedLiveColumnsMigrationTests`; the PostgreSQL test file I read but could not run.
- The contracts: phase 4 sections 1, 3 and 7.

**What I ran:** `dotnet test src/CcDirector.Gateway.UnitTests -p:EnableWindowsTargeting=true --filter
"FullyQualifiedName~MailDesk|FullyQualifiedName~Trigger|FullyQualifiedName~Origin|FullyQualifiedName~Migration"` on
devlinux, with the polling file watcher and a scratch `LOCALAPPDATA`. Result: 1,024 passed, 0 failed, 0 skipped (1 minute 45 seconds), exit 0.

**What I could not reach:** the PostgreSQL migration and `MailDeskRoutedPostgresTests` (they need
`scripts/test-database.ps1` and Docker; not in what I was allowed to run). Nothing was run against a real Director.

## What I checked and found sound

- **An observe decision is never acted on.** `CreateMissing` takes only decisions recorded with `mode` = `live`; the
  start step also skips a row whose channel is now `observe`. Tested both ways.
- **No body text.** The router reads only the decision's sender, subject, layer and reason and the row's return note;
  the preview is not on any entity it touches. No router or store log line carries a sender, subject or note. The
  marker test is real (the marker is proved to be in the report first).
- **The create is one a released Director takes.** Origin `schedule`, surface `trigger`, a full seat (factory, seat,
  version, kit) so `SettleSeatStart` accepts the `FactoryCheck`, and `Count` is always at least 1 (the first email is
  always given, cut to fit), which `FactoryRunKit.WithCheck` requires. `Start` = 0 is not checked by the Director.
- **Where.** The machine is the reporting Director's, resolved by the report route from the registry, and the create
  names that Director. The backup test proves it on machine B.
- **No email started twice by the router itself.** The start name and time are written before the start is sent;
  a start whose answer is lost is found by that name; a start whose session id came back but whose `Take` write failed
  is adopted by name on the next run. The per-account guard keeps two router runs of one account apart on this
  Gateway, and the unique index on (account, decision) keeps two routed rows of one decision apart.
- **The router never changes the run row.** It runs after the record, outside the per-trigger lock and after the
  `_screening` entry is removed, so a slow router cannot make the next report `skipped-running`. Its failure is caught
  and logged. Tested.
- **Migration order.** Both new migrations are later than the newest on `origin/main` (`AddSessionRuleChanges`,
  20261020002835 and 20261020002913) and on the phase branch. The SQLite migration test proves the channel is filled
  from the decision and a second row of one decision is refused.

## Verdict

**Changes needed, but small.** The router meets contract sections 1, 3 and 7 as written, and I found no way for it to act on an observe decision, put body text anywhere, start on the wrong machine, make a create the Director would refuse, change the run row, or lose an email outright. Two findings: one real duplicate-seat risk, which comes from the contract's own rule that a session the Gateway cannot see has ended (finding 1), and one contract rule no test proves (finding 2).

## Findings

### 1. A session the Gateway cannot see is taken for an ended one, so a live seat can be given the same emails again

**Where:** `src/CcDirector.Gateway/MailDesk/MailDeskRouter.cs:326-331` (`IsAlive`), used by `RerouteOrphans` (line 159,
then the re-route at line 162) and by `IsSeatBusy` (line 321).

**What it does:** when `findSession` returns nothing, the session counts as alive only inside `StartGrace` (5 minutes)
of its start. `findSession` is `GatewayEndpoints.LastKnownSession`, which reads `PushedSessionStore` - an in-memory
cache filled by each Director's stream. A Director that is not connected to this Gateway process has no entry in it,
so every session it runs is "unknown", and a seat session started more than 5 minutes ago is then taken as ended.

**Failure scenario:** machine A's Director runs session S1 for `qa-books/clerk` holding emails E1 and E2. The Gateway
is redeployed (an in-place restart, the normal deploy), and machine A's Director has not reconnected yet - or has lost
its connection to the Gateway, which is exactly when a backup machine takes the Mail Desk claim (phase 5). Machine B
reports a screened run. The router finds S1 in no cache, S1 started an hour ago, so `RerouteOrphans` re-routes E1 and
E2 (`RerouteCount` 1) and `StartSeatsAsync` starts S2 on machine B with both. S1 is still working: two agents now act
on the same two emails (two replies to a customer, an invoice handled twice). When S1 then runs
`maildesk done <E1>`, the row is with S2 and S1 is refused `not_your_email`. If it happens once more (another restart
during S2), the email goes to the owner as "its session ended twice without saying done or returning it", which is
false. The same reading also clears the busy rule (line 321), so a new email for that seat starts a second session
beside a live one.

**Why it must change:** this follows the letter of contract section 3.2, step 3 ("a session the Gateway no longer knows
after `StartGrace`"), and `TriggerService.IsLastSessionAlive` reads it the same way, but the cost is different. A
trigger misreading this starts one extra session that re-checks the work; here the cost is the same email acted on by
two seats, which contract section 1 ("One session per seat per run", "A busy seat") exists to prevent. Before the
Gateway restarts the cache keeps a disconnected Director's last rows for the eviction horizon (24 hours, `DirectorRegistry.SweepStale`), but the cache and the registry are
in memory and start empty, so the risk is
concentrated right after a deploy. "Not seen" should only mean "ended" when the session's Director is connected and
has sent its snapshot since connecting without it; otherwise it is unknown and the row should wait. Which way to fix it
- here, or in the contract - is the Developer's and the Tech Lead's decision.

### 2. No test proves a seat stays busy for its new emails while a start with an unknown outcome is inside the grace

**Where:** `src/CcDirector.Gateway/MailDesk/MailDeskRouter.cs:317-319` (the first clause of `IsSeatBusy`); tests in
`src/CcDirector.Gateway.UnitTests/MailDesk/MailDeskRouterTests.cs` (the two `StartWithAnUnknownOutcome` tests).

**What the tests claim:** contract section 3.4 says that for a start whose outcome is unknown "no second session is
started for it meanwhile (the seat counts as busy while that start is inside `StartGrace`)", and section 3.5 item 8
asks for "no second start is made inside the grace".

**Why they do not prove it:** both tests have exactly one email. Its row keeps the start's name, and `StartSeatsAsync`
(line 177) only ever queues rows whose start name is null, so that row could never be started again whether or not
the seat is busy. No test adds a second email for the same seat during the grace. Reading every test that reaches
`IsSeatBusy`, deleting its first clause would leave them all green (I did not change the code to confirm it).

**Failure scenario if that clause regresses:** the start of S1 for `qa-books/clerk` times out (outcome unknown) but the
Director did create S1. One minute later a new email for `qa-books/clerk` arrives; with the clause gone the seat looks
free (no row of the seat has a session yet), and S2 starts beside S1. Nothing would catch it. A test with a second
email for the seat inside the grace (and one after it) closes this.

## Noted, not findings

- **A start counted failed after the grace whose session appears later** gives the same emails to two sessions. The
  contract accepts this (section 3.2, step 2) and the trigger has the same rule; it is part of the same reading as
  finding 1.
- **A seat session that finished its emails but waits on a person** is not dismissed, so the busy rule holds every
  new email of that seat queued for as long as it waits, with no problem raised. The contract's busy rule says exactly
  this; whether the owner should be told is a product question, not a defect of this change.
- **`BeginStart` naming fewer rows than asked** throws after naming some of them; those rows would wait out the grace
  and be counted as one failed start that was never made. No writer can change a queued row between the read and that
  write today (done and return need the holding session; a queued row has none), so I could not build a scenario.
- The test rig's lookup by name ignores the origin surface, while production (`LastKnownTriggerSessionByName`) also
  requires `trigger`. The router sets `trigger`, so this does not change what the tests prove.
