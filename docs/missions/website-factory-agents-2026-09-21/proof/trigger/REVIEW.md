# Review - pull request 3273 - the trigger (Website Business Factory, product track)

Reviewer: the Reviewer seat on the mission workflow. Read-only: I built nothing, changed nothing, merged nothing.

Reviewed at the pull request head, fetched read-only as `review-3273` in the shared checkout and read with
`git show`. Base compared against `origin/main`.

## Scope

**What I read** (all at the pull request head):

- The Gateway's decision half: `TriggerService.cs`, `TriggerStore.cs`, `TriggerCheckContract.cs`,
  `TriggerStatusFold.cs`, `TriggerDefinition.cs`, `TriggerEndpoints.cs`, the two entities, the
  `GatewayDbContext` additions, both migrations (SQLite and PostgreSQL), the `GatewayHost` wiring of the
  service, the routes and the `factoryAgents.enabled` switch, and `FactoryAgentsConfig.cs`.
- The Director's half: `DirectorTriggerRunner.cs`, `ITriggerGateway.cs`, the `GatewayClient` and
  `ControlApiHost` additions, and the `ProcessJob` / `IJob` / `SessionOrigin` changes.
- The guard: `SessionKeyGuard.IsTriggerRoute` and its tests.
- The command line: `trigger_ops.py`, the `cli.py` wiring and action list, `docs/cli-reference.md`,
  and the Python tests.
- The contracts: `TriggerDtos.cs` (the outcome and status vocabulary).
- The tests: `TriggerServiceTests`, `TriggerCheckContractTests`, `TriggerStatusFoldTests`,
  `TriggerDefinitionTests`, `DirectorTriggerRunnerTests`, `FactoryTriggerHostTests`, `ProcessJobTests`,
  `SessionKeyGuardTests`, `test_trigger_ops.py`.
- The proof: `docs/missions/website-factory-agents-2026-09-21/proof/trigger/README.md` and `gate-results.md`.
- For the lock's meaning I also read the roster primitives it rests on:
  `GatewayEndpoints.LastKnownSession`, `PushedSessionStore.GetLastKnown` / `Forget`,
  `FleetManagerSessions.IsGone`, and the `DirectorRegistry` removal wiring in `GatewayHost`.
- For the two later mandate changes I read the state of pull request 3272 (the activity record, still open)
  and its `FactoryActivityOutcome` vocabulary.

**What I ran**: nothing. This seat reads; the developer's own gate results are quoted below where relevant,
taken as claims from his proof, not as my own evidence.

**What I could not reach**:

- A live end-to-end run on a real Director (the developer did not do one either; his proof says so).
- The PostgreSQL migration proof: the developer's own gate report shows 13 Gateway.Tests PostgreSQL failures
  on a machine-overloaded run and states plainly that the PostgreSQL migration is unverified. I did not
  re-run it. This must be run green on a quiet machine (`.\\scripts\\test-local.ps1 -Parked`) before merge;
  that condition is correctly stated in the pull request and I repeat it as a merge condition, not a finding.
- The `factory_activity` integration: pull request 3272 has not merged, so per the mandate it is correctly
  absent and correctly declared.

## Verdict

The change does what the mandate asked, and the proof is unusually honest about what it does not cover.
Every case the mandate named is implemented and has a test that proves the right thing, including the
negative cases (broken checks, no report, switch off, pause). Three findings follow; none of them blocks
the design, and the seat that built the work decides what happens to each.

## Findings

### Finding 1 (medium): the one-at-a-time lock can hold forever, silently, after a Director dies uncleanly

`TriggerService.IsLastSessionAlive` reads the session's roster row through
`GatewayEndpoints.LastKnownSession`, which returns the last row any Director pushed, **however stale**
(`PushedSessionStore.GetLastKnown` has no freshness cut, and the store's own comment says entries
"deliberately survive a disconnect"). A session counts as ended only when that row says `Exited` or
`Crashed`. The row is forgotten only when the Director is removed from the registry
(`Registry.OnDirectorRemoved`), which happens on an unregister or a force-kill route - not when a Director
process simply dies.

The harm, concretely: Director A on a machine holds a trigger and starts session S. Director A is then
killed without unregistering (a force-kill, a crash, a power cut - this fleet's own conventions run several
Directors on one machine, and its documentation records force-kills as a real occurrence). S dies with it,
but A's last pushed row for S still says `Working`. Another Director on the same machine takes over the
check once the claim lapses (two intervals plus a minute - that part works). From then on every check that
counts work writes `skipped-running`, because the stale row never says the session ended. The status stays
**OK**. The trigger never starts another session again, for as long as that registration sits in the
registry, and the work pile grows with nothing red anywhere - which is exactly the silence the mandate's
"Red, never silence" rule exists to prevent.

There is also no remedy short of destruction: pause and resume do not clear `LastSessionId`, and update
does not either. Deleting and recreating the trigger works, but the run history is then unreachable (the
runs route answers 404 for a deleted trigger) and the definition's identity is lost.

Why it must change: the failure direction chosen (never double-start) is the right one to lean to, and the
five-minute start grace correctly covers the brief incompleteness the mandate named - but a *permanently*
stale row converts a safety lean into a permanent work stoppage with an OK face. Either the lock should read
the freshness the rest of the product reads (for example the `streamStaleAfter` cut `TryLocate` uses, with a
deliberately generous horizon so a briefly offline Director does not release the lock), or a long-lived
`skipped-running` over a stale row should surface as a status the owner can see. As written, the one
failure mode of the lock that is unbounded is also the one nobody can see.

### Finding 2 (low): the lock does not survive one specific Gateway restart window - between the start and the record

The lock state (`LastSessionId`, `LastStartedUtc`) is in the `triggers` table, so it survives a Gateway
restart in general. But `DecideAsync` starts the session first and `RecordCheck` writes `LastSessionId`
after the start returns. If the Gateway process dies inside that window, a live session exists on the
machine and the trigger's row says it never started one; the next check that counts work starts a second
session doing the same work. This is a sub-second window and the schedule engine has the same shape, so it
is a pre-existing pattern rather than a new sin - but the mandate explicitly asked about the lock across a
Gateway restart, and the honest answer is "yes, except this window". Recording the *intent* to start
(persisted before the spawn, resolved or cleared when the start returns) would close it; whether that is
worth doing here is the Tech Lead's call, and it should probably be decided for schedules and triggers
together rather than for triggers alone.

### Finding 3 (low): the new command's limit flag breaks the fleet's own flag convention

`cc-devthrottle trigger runs` takes `--limit` (with `-n` as an alias), while every other result-limiting
command in the same file uses `--count` / `-n`, and the repository's own instructions warn about exactly
this: "use `--count` / `-n` for result limits, NOT `--limit`". Agents that call the tool by convention
will type `--count`, get a refusal, and pay a round trip; the warning exists in the instructions because
that confusion has already cost time. `docs/cli-reference.md` documents `--limit N`, so the two must change
together. Small, cheap to fix before merge, and it only gets more expensive to rename after agents have
learned the command.

## The mandate's focus list, answered

Each item the review mandate named, and where it stands:

- **The lock holds until the STARTED session has ended, not just while it is being started.** Yes. The lock
  is `LastSessionId` plus the roster row, not a start-in-progress flag; `ASecondCheckWhileThatSessionLives_...`
  proves a second counting check writes `skipped-running` and starts nothing, and that a new start happens
  only after the session exits (a crash also releases it). The five-minute start grace covers a session not
  yet in the roster. Across a Gateway restart the lock state is in the database - except the window of
  Finding 2. The unbounded stale case is Finding 1.
- **An empty check starts nothing.** Yes: count 0 writes `nothing-to-do`; the starter fake recording zero
  calls is the proof (`EmptyCheck_WritesNothingToDo_AndStartsNoSession`).
- **Every broken-check shape is a recorded failure and a RED status.** Yes: could not start, timeout
  (checked before the exit code, so a timeout with plausible output still fails), no exit code, exit
  non-zero, printed nothing, not JSON, JSON that is not an object, no `count`, a count that is not an
  integer, a negative count - each with its own recorded reason, each turning the trigger RED
  "check failed: reason" (`TriggerCheckContractTests`, `ABrokenCheck_...`). A failed *start* is recorded as
  a failed row and RED "start failed: reason", so it is never silent either.
- **"No checks ran" goes RED when a Director stops reporting.** Yes: no recorded check within two intervals
  (of the last check, or of creation for a never-checked trigger) is RED "no checks ran", and a newer
  silence outranks an older failure (`TriggerStatusFoldTests`).
- **Paused starts nothing.** Yes: a paused trigger is still checked and recorded (`paused` row, count kept),
  and starts nothing until resumed.
- **The switch off unmaps the endpoints and the Director runs nothing.** Yes: the routes are mapped only
  inside `if (FactoryAgentsEnabled)`, and `FactoryTriggerHostTests.SwitchOff_...` proves 404s on the write
  routes, a non-JSON answer on the reads, the real client reading that as "off", and a runner that then
  makes zero checks - counted by a fake that records its calls. The switch is default-off and only a JSON
  boolean `true` turns it on.
- **The Director runs only triggers for its own machine and only commands the definition names.** Yes:
  the machine is read from the Director's own registration in the caller's account - never from the
  request - and `ClaimForDirector` hands out only that machine's triggers. The command the Director runs is
  the definition's `CheckCommand`, and the Director stores nothing locally (in-memory scheduling only).
  One gap I checked and judge **not** a finding: the report route does not verify that the reporting
  Director's machine matches the trigger's machine (only the fetch does). A Director naming another
  Director could in principle report on a trigger it never ran. Within one account that is not an
  escalation - the same credential can already start sessions on any machine in the account, and a session
  key cannot reach the route at all (the guard refuses both Director trigger routes to a session, with
  tests). Defense in depth would be cheap; it is not required by the mandate.
- **Who may create a trigger, and can one tenant make another tenant's Director run a command?** Any
  session key in the account (the guard allows the `/triggers` surface, so `cc-devthrottle trigger add`
  works from any agent), any device key, and the machine token; the creator is recorded in `CreatedBy`.
  That is the same class of power a session key already has (it can start sessions with prompts), so it is
  not an escalation. Cross-tenant: no. Every route resolves the caller's tenant and refuses an unbound
  request; the store is tenant-partitioned by construction; `ATriggerIsTheAccountsOwn` proves another
  account can neither see nor report on the trigger. One tenant cannot make another tenant's Director run
  anything.
- **The product knows nothing about any particular business tool.** Yes. I searched the product code for
  business-tool names: the only occurrences of mail or website vocabulary are in tests, proof documents and
  the mission name. The contract asks exactly one thing of a tool: exit 0 and print JSON with an integer
  `count`.

## The two later mandate changes

- **Every check written into `factory_activity` once the record merges.** Not in this pull request, which
  is correct: pull request 3272 is still open, the mandate says not to wait for it, and the pull request
  and the proof both say so plainly. This is a follow-up the Tech Lead must hold open against 3272 merging,
  and note that its scope grew: every check, not only started / failed / paused.
- **The outcome list gained `skipped`.** Present, as `skipped-running`: the run vocabulary is
  `nothing-to-do`, `started`, `paused`, `skipped-running`, `failed`, and a counting check that finds its
  last session alive records `skipped-running` and starts nothing. If a *separate*, sixth outcome called
  `skipped` was intended, nothing in the mandate's own test list or in pull request 3272's activity
  vocabulary (ten words, none of them `skipped`) asks for it, and I found no decision it would carry that
  `skipped-running` does not. If the Tech Lead means something else by it, that is worth one sentence back
  to me.

## Notes (no change asked)

- `Agent = "ClaudeCode"` is hard-coded on the started session. The mandate's definition has no agent field,
  and the schedule starter hard-codes the same value, so this is consistent with the product rather than a
  new decision - but it is a product decision made silently, and the first tenant whose default agent is
  not Claude Code will hit it.
- The claim design (first Director to fetch holds a trigger; the claim lapses after two intervals plus a
  minute) is sound, including the arithmetic: a check's timeout is at most half its interval, so a check
  can never still be running when its claim lapses and a second Director takes over.
- The proof's mutation section (lock, pause and per-trigger lock removed one at a time, each turning its
  test red) is the right kind of evidence, and the gate report's refusal to dress up the PostgreSQL failures
  as anything other than unverified is exactly what the method asks of a proof.

## What this review does not cover

No live Director, no PostgreSQL, no `factory_activity` - the three things the developer himself marked
unverified. The PostgreSQL migration proof on a quiet machine remains a merge condition stated in the pull
request, and the person merging should treat it as blocking for the hosted Gateway, where that migration
is the one that runs.

## Developer's answer

Answered by the follow-up Developer on pull request 3273, commit f0c488c8f on branch `wbf-trigger`.

### Finding 1 - ACCEPTED, by surfacing it red (the lock itself is unchanged)

I chose the red status over a freshness release. A freshness cut releases the lock on a guess: a Director
that is offline for longer than the horizon but whose session is still running would get a second session
doing the same work, which is the failure the lock exists to prevent. Surfacing it keeps the safe lean and
removes the silence, which is what the finding and the Tech Lead asked for.

What changed: `TriggerStatusFold` now answers RED
"session <id> has not ended after N hours; no new session starts until it does" when the last check counted
work, was recorded `skipped-running`, and the session it is waiting on was started more than
`TriggerStatusFold.LongRunningAfter` (six hours) ago. It is keyed to `skipped-running`, so an old session
with nothing waiting behind it does not turn the trigger red. Silence ("no checks ran") and a failed check
still outrank it.

Proof:
- `TriggerServiceTests.ADirectorThatDiedLeavesItsSessionWorking_TheLockHolds_ButTheOwnerSeesItRed` - the
  reviewer's scenario through the real service and database: the session row stays `Working` for good, an
  hour in the trigger is OK and skipping, past the horizon it is RED naming the session, and exactly one
  session was ever started.
- `TriggerStatusFoldTests`: red past the horizon, OK exactly at it, OK for an old start whose last check
  found nothing to do.
- Mutation: disabling the new red branch turns exactly those two tests red (72 of 74 pass); restored, 74 of 74.

Not done, and said plainly: the owner still has no remedy short of deleting and recreating the trigger once
the stale row is seen. A release verb (clear `LastSessionId` on the owner's explicit word) would be the
remedy; I did not add it because it widens the command surface beyond what the finding asked. It is a
Tech Lead call whether that belongs here or in a follow-up.

### Finding 2 - DECLINED for this pull request

The window is between `SpawnOnMachineAsync` returning and `RecordCheck` writing `LastSessionId` - a
database write that follows immediately, so sub-second. Closing it needs an intent row written before the
start and reconciled afterwards, and reconciling an intent whose outcome is unknown after a restart needs
the same "is that session alive" question the lock already asks, so it is a real piece of design, not a
line. The schedule engine has the identical shape, so fixing it for triggers alone would leave the product
with two answers to one problem. It should be decided once for schedules and triggers together. Worst case
if it happens: one duplicate session doing the same work, which the pile-count check makes visible and
harmless for the work these triggers start (reading mail, handling a queue).

### Finding 3 - ACCEPTED

`cc-devthrottle trigger runs` takes `--count` / `-n`; `--limit` is gone and is refused (a test proves the
refusal and that no request is made). `docs/cli-reference.md` updated. Tests: `--count 3` and `-n 7` both
reach the client with that number.

### The check

- `.\scripts\test-local.ps1`: every suite green except the 2 Launcher restart-signal tests known red under
  issue 3242 (`An_unarmed_launcher_declares_no_restart_signal...`, `Describing_the_launcher_asks_the_signal...`).
- `dotnet test src\CcDirector.Gateway.UnitTests` in full: 7063 passed, 8 skipped, 0 failed.
- Trigger Gateway.Tests by filter `Trigger`: 5 of 5.
- `tools/cc-devthrottle` in a virtual environment with the pinned packages from `ci.yml`: 3496 passed,
  3 skipped.
- NOT run: the PostgreSQL proofs (Docker is wedged). Not a pass. `-Parked` is still a merge condition.
