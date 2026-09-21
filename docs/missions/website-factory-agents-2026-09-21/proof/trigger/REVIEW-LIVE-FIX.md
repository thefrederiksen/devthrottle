# Review - the trigger's slow-start fix (devthrottle pull request 3281)

Reviewer: a separate session on the mission workflow, seated as Reviewer. It did not write any of the code
under review and changed nothing.

Commit read: `fed7ae306` (the head of pull request 3281 at review time), which contains both fix commits
`19ed0ba84` (the report answers at once; the lock is taken before the start) and `d5f81154f` (an outcome-unknown
start keeps the lock and its session is adopted by name), plus the proof commits.

## Scope

What I read, in full, from the pull request branch (never the shared working tree):

- `src/CcDirector.Gateway/Factory/Triggers/TriggerService.cs` (whole file)
- `src/CcDirector.Gateway/Factory/Triggers/TriggerStore.cs` (whole file)
- `src/CcDirector.Gateway/Factory/Triggers/TriggerStatusFold.cs` (the diff and its callers)
- `src/CcDirector.Gateway/Running/MachineSessionSpawner.cs` (the diff and surrounding file)
- `src/CcDirector.Gateway/Api/TriggerEndpoints.cs`, `GatewayEndpoints.cs` (`LastKnownTriggerSessionByName`),
  `SessionVerbClient.cs`, `DirectorCommandRouter.cs` (the status synthesis), `GatewayHost.cs` (the diff)
- `src/CcDirector.Gateway.Contracts/TriggerDtos.cs`, `DirectorCommandMessages.cs` (`DirectorCommandStatus`)
- The Director's side: `src/CcDirector.ControlApi/GatewayClient.cs` (`ReportTriggerCheckAsync` - any success
  status, 202 included, counts as recorded and the body is ignored), `DirectorTriggerRunner.cs`,
  `src/CcDirector.Core/Sessions/SessionName.cs` (an explicit name passes through verbatim - the adoption match)
- The tests: `TriggerServiceTests.cs`, `TriggerStatusFoldTests.cs`, `MachineSessionSpawnerTests.cs`,
  `FactoryTriggerHostTests.cs`, and the proof `docs/missions/website-factory-agents-2026-09-21/proof/trigger/LIVE-CHECK.md`

What I ran: I cut a throwaway worktree at `fed7ae306` and ran the trigger and spawner unit suites:
`dotnet test src/CcDirector.Gateway.UnitTests --filter "FullyQualifiedName~Factory.Triggers|FullyQualifiedName~MachineSessionSpawner"`
- 110 passed, 0 failed, 0 skipped. The worktree and the local branch were then deleted.

What I could not reach:

- The live rig. I did not reproduce the live defect or the live fix; I read the proof's evidence instead, which
  itself honestly names what it did not prove live: the 5-minute lapse and the adoption after a Gateway restart
  mid-start are covered by unit tests only.
- `CcDirector.Gateway.Tests` (the host suite) and the parked suites - I did not run them; the Developer's proof
  records their results.

## Findings

### 1. The adoption lookup re-derives the session name from facts that can change, so an owner edit inside the window can still start a second session while the first lives

`TriggerService.ResolveUnknownStart` looks for the unknown start's session by recomputing the name:

```csharp
var name = SessionName(trigger, _timeZone(tenant), startedUtc);
var found = _findSessionByName(tenant, name);
```

`SessionName` is built from the trigger's **current** `FactoryAgent` and `Name` and the account's **current**
time zone. The session the Director actually created carries the name computed at `Decide` time
(`p.Request.Name`), which is also quoted in the failed row's reason. Those two agree today - and nothing stops
them disagreeing:

- `PATCH /triggers/{id}` (`TriggerStore.Update`) will change a trigger's `Name` or `FactoryAgent` at any moment,
  with no regard for a pending unknown-start lock.
- The account's time zone setting (`_tenantSettingsResolver.TimeZone(tenant)`, resolved fresh on every call)
  can be changed in Settings at any moment.

If either changes between the unknown start and a settling check inside the 5-minute grace, the recomputed name
no longer equals the name on the session, so:

- the session is never found and never adopted;
- after the grace the lock lapses with a row claiming "no session named '<the NEW name>' showed up" - a false
  statement about a name no start ever used;
- the next check starts a SECOND session while the first still lives on the machine - precisely the double start
  this change exists to make impossible.

The harm is real but the window is narrow: it needs an unknown outcome (itself the rare case) AND an owner edit
to the trigger's name, its factory agent, or the account's time zone inside the following five minutes. That is
why this is a finding and not a verdict against the change: the mainline is sound. But the fix is cheap and
closes the last path I found to two live sessions: persist the name the start used together with the pending
lock (`BeginStart` already stores the start time; the name is one more column), or read it back from the failed
row's reason, which already carries the exact name - rather than re-deriving it from mutable facts. The seat
that built this decides whether to take it.

## Verification against the mandate's own questions

I hunted each named risk; this is what I found, so the findings above stand alone.

- **One trigger, two live sessions** - only the path in finding 1. The gate serialises every decision and every
  row write; the lock is taken by `BeginStart` before the start begins, so a start in flight blocks new starts;
  a check that lands while the start runs records `skipped-running` with no session and starts nothing. A
  definite failure releases the lock only by `RecordStartResult(..., holdLock: false)`, and the release is
  guarded by `LastSessionId` being empty, so it cannot undo an adoption that happened meanwhile. Cross-tenant
  adoption is impossible: `LastKnownTriggerSessionByName` iterates only the tenant's own Directors, and the
  match requires the exact name (Ordinal) and origin surface `trigger`. Two different starts of the same
  trigger cannot share a minute-resolution name: while the lock holds nothing new starts, and after the lapse
  the next start is more than an interval later. A lapse while the Director's roster push is more than five
  minutes late can still leave an unknown-created session running unmanaged - that is the Tech Lead's accepted
  ruling, not a defect.
- **The lock held forever with no red** - no path found. In flight: the create is bounded by the router's
  30-second wait. Unknown outcome: RED through `TriggerStatusFold` (and silence, also red, outranks it - the
  ordering is deliberate and tested), and it lapses at the first check after the grace; a Gateway that died
  mid-start leaves the same pending lock, which the next Gateway adopts or lapses. Definite failure: releases
  at once. A resumed Director always eventually delivers a check, and any check settles it.
- **An exception in the background start** - cannot vanish. `FinishStartAsync` is a try/catch at an entry
  point: the starter's exceptions become failed or unknown rows; a failure to record leaves the pending lock in
  the database, which the next check then treats as an unknown start and settles by name - and the name still
  matches, because the session was created with it. Everything is logged.
- **The background start racing the next check** - the start writes its row under the same per-trigger
  semaphore, and `_inFlight` is removed only inside that critical section, so a concurrent check either sees
  the finished state or skips on the pending lock; it can never see "no session, nothing running" while the
  start is still writing. `IsLastSessionAlive` reads a pending lock as alive inside the grace.
- **Adoption matching the wrong session** - the name carries the trigger and the minute of the start; the
  origin-surface filter excludes hand-made and schedule sessions; the tenant is scoped by the caller. The one
  wrong-match path I found is finding 1's mirror: not matching the wrong session but failing to match the right
  one.
- **A Gateway shutdown mid-spawn** - `_triggerStartLifetime` is cancelled at stop and never by a request; the
  router rethrows the caller's cancellation, `FinishStartAsync` classifies it as an unknown outcome from the
  token state, the lock is held, and the next Gateway adopts or lapses it. If the process dies before the row
  is written, the pending lock from `BeginStart` is the record, and it lapses rather than holding forever
  (tested).
- **The flag from the router's status, not the text** - confirmed. `SessionVerbClient.CreateSessionWithOutcomeAsync`
  sets it from `result.Status is DirectorCommandStatus.Timeout or DirectorCommandStatus.TunnelDropped`, both
  synthesised by the router itself from its own linked cancellation token and from the send throwing - never
  parsed from a sentence. Failures before the create is sent (machine off, no Director on the tunnel, a refused
  Director) and a Director that answered with an error are definite, and the tests prove each.

Two small observations, not findings - no harm that must change:

- Between `BeginStart`'s database write and the `_inFlight` mark there is a millisecond window in which a
  concurrent status read can label the trigger RED "start outcome unknown" for one render; it self-corrects on
  the next read and nothing groups on it.
- Pause-then-resume, the owner's documented way out of a stuck lock, does not release a pending unknown-start
  lock (resume releases only when a session id is held). That is the safe side: releasing early is exactly the
  double-start risk, and the red is bounded by the 5-minute grace.

## Verdict

Within the scope above: one finding, a narrow edge of the adoption design. The two defects the change set out
to kill are killed - the report answers at once (the Director's client accepts any success status, and the
202 body is additive), the lock is taken before the start, a definite failure releases it and turns red, an
unknown outcome holds it, says so in a row, adopts its session by name, and lapses with a row after the grace.
The proof's live evidence covers the mainline and the unknown-outcome adoption; the Developer has already
named the two cases it could not produce live.

## The Developer's answer

**Finding 1 - accepted and fixed** (the Tech Lead ruled to accept it), commit `f0ccfeb82`, pins moved in `0e1bdd611`.

- `BeginStart` now stores the exact name the start gives its session with the lock, in a new column
  `triggers.LastStartName` (nullable, 320 characters: two 128-character fields and the separators and minute).
  Migrations `AddTriggerStartName` for SQLite (`20260921203243`) and PostgreSQL (`20260921203258`); each adds
  that one column and nothing else, and the two model snapshots differ by that column only.
- `ResolveUnknownStart` looks the session up by `LastStartName` and never works the name out again. A lock with
  no stored name has no name to look for, and lapses after the grace (every lock `BeginStart` takes carries one).
- A released lock forgets the name with the start time: a definite failure, a lapse, and the owner's pause and
  resume.

Tests: `AnUnknownOutcome_IsStillAdopted_WhenTheOwnerRenamesTheTriggerMeanwhile` renames the trigger AND its factory
agent during a pending unknown start and the session is still adopted, the next check is `skipped-running` on it,
and nothing else starts. Revert proof: with the name worked out again (the old line restored), that test FAILS;
restored, it passes - both runs rebuilt. `ADefiniteFailure_ForgetsTheStartName_WithTheLock` covers the release.
The account time zone path is the same mechanism (the stored name no longer reads the zone) and has no separate
test.

Gate after the fix: `dotnet test src\CcDirector.Gateway.UnitTests` 7168 passed, 0 failed, 8 skipped (seven
migration pins moved by one, as their comments describe). `.\scripts\test-local.ps1 -Gateway` filtered to
Postgres, Migration, Trigger, Factory and the three host tests that build the spawner, on its own throwaway
PostgreSQL: 114 passed, 0 failed; the 4 not run are the hosted-database proofs that need the real hosted
connection string, which the rig never sets. `.\scripts\test-local.ps1`: green except the two Launcher tests
known red (issue 3242).

The two observations, not taken: the one-render red window between `BeginStart` and the in-flight mark is
harmless as you say; and pause-and-resume not releasing a pending unknown lock is the safe side, bounded by the
5-minute grace.
