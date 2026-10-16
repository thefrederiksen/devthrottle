# Phase 2 review: the receipt store and the event ingest

Mission: API mode for agent wrappers, proven on Pi (issue #4070). Pull request #4082, branch
`api-mode-p2-receipts`, reviewed at its head commit `2d5d3e926`. Reviewer seat, opened by the phase 2
Tech Lead (session 138), running a different agent family from the Developer. Written 16 October 2026
on devlinux, session 146.

**Verdict: the receipts and the ingest hold. Three findings, one of which (finding 1) should be
answered before the live relay is built on the feed, because the relay's correctness rests on the
flag it concerns. Findings 2 and 3 are small and the Developer decides.** Everything the Tech Lead
asked me to judge was checked against the code at the pull request's head and against runs I made,
not against the pull request's description. Finding 1 was proven by a test I wrote, ran and deleted.

## 1. Scope: what I read, what I ran, what I could not reach

**Read, in full:** `event-model.md` (the contract, including section 2's five rules and the Delivery
Lead's ruling on Pi exiting); every file the branch changes against `origin/main` except the two
generated migration designer files and the two model snapshots, which I read only as diffs; the two
migrations themselves (SQLite and PostgreSQL); the existing code the new code leans on, read on the
branch: `TeamCallerOwnership.ClaimOf`, `SessionKeyRegistry.DirectorOfSession`, the hub's
`PushTurns` and `TeamTurnPushRefusalFor`, `GatewayDbContext.ApplyTenantScope` (the tenant filter),
`GatewayEndpoints.ResolveReadTenant`, `SessionHistorySweep`, `TeamEndpointRules`, the session event
records and their serializer options in `CcDirector.Gateway.Contracts.ApiMode`, and the Director's
`SessionEventNumbering` (sequences start at 1). All seven new test files and the test data file.

**Ran, with the .NET 10 software development kit on devlinux:**

- `dotnet build` of `CcDirector.Gateway.UnitTests`, `CcDirector.Gateway.Tests` and
  `CcDirector.Gateway.DatabaseTests`: all three succeed with no warnings, so the changed
  constructors (`SessionHistorySweep`, `DirectorHub`) break no other suite.
- `dotnet test` of `CcDirector.Gateway.UnitTests` filtered to the new suites plus the boot smoke
  tests and the team route rules: 166 tests, 166 passed, 0 failed, 0 skipped, 23 seconds. That run
  includes `GatewayHostBootSmokeTests`, which asserts `HasPendingModelChanges()` is false for BOTH the
  SQLite and the PostgreSQL model snapshots, so "no pending model changes" is a run, not a reading.
- A throwaway test of the event buffer for finding 1 (written, run, failed as predicted, deleted;
  the tree is clean apart from this file).
- A sweep of the four branch commits and the changed files for assistant attribution: none.

**Could not reach:** a real PostgreSQL (the migration was read, and the snapshot check run, but no
database server applied it, by the test standard); the ownership rule under a real team key (no test
in the repository drives either `PushTurns` or `PushApiModeEvents` with a team key on the connection,
see note A); the Director side of phase 2, which does not exist yet, so every statement about what a
Director "will" do is read from the contracts this pull request writes.

## 2. What is right, so the next seat does not re-check it

- **Tenant isolation is by construction at every layer.** The receipt table's primary key leads with
  the tenant; every read and write goes through the context's global filter on `ActiveTenant`; the
  store never takes a tenant from a caller. The hub binds the tenant from the connection
  (`EnterBoundTenantScope`, `RequireBoundTenant`), never from the batch. The ingest enters the bound
  tenant's scope around the store writes. The buffer keys every session by (tenant, session) and a
  subscription names its tenant. The endpoint resolves the tenant from the authenticated credential,
  reads the store inside that scope, asks the roster in that tenant only, and answers another
  account's session identifier with 404 exactly like an unknown one. The tests prove each of these
  with two accounts over one database (`Accept_TheSameSessionIdInTwoAccounts_NeverMeets`,
  `Read_AnotherAccountsSession_IsNotFoundAndServesNothing`, the three store tests under "tenant
  isolation", and the purge test).
- **The ownership rule on the hub method is the same code path as the turn push**, not a copy:
  `TeamTurnPushRefusalFor` with an operation name for the log. Refused for good (another person's
  rows, another Director's key) answers null; refused for now (not the only roster holder) answers an
  empty generation and sequence -1, which the ingest never sees. The method refuses before Hello
  (`RequireBoundDirector`), and the hub omits it from the advertised methods when the Gateway has no
  ingest. Both are tested.
- **Idempotency and ordering.** The buffer decides under the session's lock: same generation, only
  sequences above the highest held are new; a later generation (by start time, ties by identifier,
  at whole milliseconds on both sides) replaces the session's and counts afresh; an older generation
  is refused whole with the reason logged and null answered. The store repeats the same rule per row
  (`ApiModeEventPosition.IsAfter`) so a second Gateway process, or a batch half-stored before an
  exception, cannot move a receipt backwards. The persist callback runs BEFORE the append and the
  delivery, so a thrown database error leaves memory untouched and the same batch can be sent again.
  Every one of these is tested, including the same fixture sent twice through subscribers (each event
  delivered once) and the late earlier event after the turn ended.
- **Nothing is dropped silently.** A refused generation, a malformed batch, a second ending, a turn
  start naming a prompt that is not delivered or is already carried, and a turn end that carries no
  receipt are each logged with the session, the prompt or turn, and the position. The ingest logs
  "stored N of M" for every accepted batch, so a duplicate batch leaves a line saying it stored none.
- **One ending per prompt (rule 1)** is enforced in the store: Delivered and Refused are final, a
  second ending is logged and refused, and the first stands. Tested both ways.
- **The turn's outcome beside Delivered (rule 5)** is a turn start linking delivered prompts to the
  turn, and a turn end writing its outcome and reason onto those rows once. The exit, abort, plain,
  tool, resume, refusal and exit-before-acknowledgement recordings all end with the receipt the
  contract says, and the ruling's three cases are covered (exit after acknowledging: Delivered with
  `failed` beside it; exit before: Refused by the Director, no turn; the Director-restart case by a
  later generation closing the turn `failed`).
- **The migrations are additive on both providers**: one `CreateTable` and three indexes, `Down`
  drops only the new table. The PostgreSQL column collations are `C` on the three Director-supplied
  identifiers compared in SQL (session, prompt, turn), as the context pins them. The snapshots match
  the model on both providers, by the run above.
- **Retention** is 90 days from the Gateway's own last change, swept per tenant by the existing
  `SessionHistorySweep`, and the purge is proven to leave another tenant's rows alone.
- **The buffer's bounds and handler rules** hold as documented: 2,000 events and 2,000,000 characters
  per session with the oldest let go and `EvictedThrough` kept; 1,000 sessions with the least recently
  written unwatched session let go, and a stale reference re-fetched (`Evicted`); a handler is called
  under the session lock so a subscriber's replay and its live events cannot interleave; a throwing
  handler is logged and stops neither the other handlers nor the store. Each is tested.
- **Terminal mode is untouched.** The diff is additive everywhere it touches existing code: a new
  optional constructor argument on the hub, a new store argument on the sweep, a new endpoint, a new
  table. `PushTurns` and its log lines are unchanged.
- **The tests prove what they claim, and meet the test standard.** `PiFixtureIngestTests` feeds every
  recorded wire log record by record through the real `PiDialect` with a `FakeTimeProvider` and no
  process, pushes the events in batches of five through the real ingest, and checks the stored
  acknowledgement against the line read back from the fixture's own standard output file. The other
  suites use the per-test SQLite file harness the existing Gateway store tests use, a fixed clock
  injected into the store, and no sleep, port, process or real clock. I found no assertion that passes
  for a reason other than the one its name gives.

## 3. Findings

### Finding 1. The feed tells a reader its replay is complete across events this process never held

**Where:** `src/CcDirector.Gateway/ApiModeEvents/ApiModeEventBuffer.cs`, `Apply` (the `floor` is -1
and `EvictedThrough` stays -1 when the session has no state in this process) and `ReplayOf` (a
same-generation cursor is answered `Complete: cursor.Sequence >= state.EvictedThrough`).

**What happens:** the buffer is held in this process only and a Gateway restart starts it empty, which
the interface says. A Director does not restart when the Gateway does, so after a deploy it carries on
in the SAME generation with the NEXT sequence. The first batch this process sees for the session then
starts at, say, sequence 50. The buffer stores 50 onwards with `EvictedThrough` at -1, and a reader that
comes back with the cursor it held before the restart, (generation, 30), is answered events 50, 51, 52
with `Complete: true`. Events 31 to 49 were never held here and the reader is not told.

**Proof, run and deleted:** a fresh `ApiModeEventBuffer`; `Apply` with three `MessageText` events at
sequences 50 to 52 in generation `gen-a`; `Read` with cursor (`gen-a`, 30) answers exactly those three
events and `Complete` is true (the assertion that it is false is the one that failed). The same read
with no cursor answers `Complete: true` as well, by the code: `after is null && EvictedThrough < 0`. The
Director's `SessionEventNumbering` starts every generation at 1, so a first held sequence above 1 is
always a gap, never the start of the stream.

**Harm:** `IApiModeEventFeed` documents `Complete` as the one signal by which "the reader has missed
events and must rebuild from durable state rather than append", and the live relay (another
Developer's work on this branch's feed) will be written to that sentence. Across every deploy, a phone
that reconnects with its cursor will append past missing events: a `ToolCallStarted` whose card never
appears, `MessageText` deltas missing from the middle of a reply, a `TurnStarted` it never saw before
the `TurnEnded` it does see. The existing bound test (`Apply_PastTheEventBound_...`) proves the flag
for events this process let go; nothing proves it for events this process never had, which is the
common case after a restart. The fix is small and the Developer decides it: the buffer can record the
first sequence it held in the current generation and answer `Complete` only when the cursor is at or
above both `EvictedThrough` and that first sequence minus one (and, with no cursor, only when the first
held sequence is 1).

Developer's answer: **Accepted, fixed in commit `ddb83d2f0`.** The reviewer is right, and the restart case is
the common one, not an edge. `ApiModeEventBuffer` now records, per session, the first sequence THIS process held in
the current generation (`FirstHeldSequence`, reset on a generation switch and on a session that was let go and came
back). A replay is `Complete` only when:

- with a cursor of the current generation: the cursor is at or above both the last evicted sequence and
  `FirstHeldSequence - 1`;
- with no cursor: this process holds the generation from its first sequence and has let nothing go;
- never when the session has nothing held here (`Read` and `Subscribe` alike), because after a restart "nothing yet"
  and "missed everything" look the same.

The fix leans on the start of a generation being known, so the contract now says it: every generation starts at
sequence 1 (`ApiModeEventBatch.FirstSequence`; the Director's `SessionEventNumbering` already starts there), and the
ingest refuses a sequence below it. The tests use the reviewer's exact case (held 50 to 52, cursor 30: not complete),
plus no cursor after a mid-generation start, a cursor exactly one below the first held (complete), a subscription's
replay, an empty session, and a later generation starting from 1 (complete again). `PiFixtureIngestTests` also asserts
that the real dialect's first event is sequence 1 and that a whole recording reads back complete. The interface's
`Complete` sentence was rewritten to list every case in which it is false.

### Finding 2. `ApiModeReceiptStore.DirectorsOf` is never called, and its comment says it is

**Where:** `src/CcDirector.Gateway/Receipts/ApiModeReceiptStore.cs`, `DirectorsOf`: "The ingest asks it
before accepting a team's events into a session (the `PushTurns` ownership rule)." No code outside the
tests calls it (`git grep DirectorsOf` on the branch: the hub and the ownership answer call the turn
store's two methods only).

**What happens:** in a team, `TeamCallerOwnership.ClaimOf` decides whose a session is in three steps:
the Directors that wrote its stored turns; else the session key row; else the roster. For an API-mode
session the first step is empty, because its conversation is not pushed as turns, so the receipts are
the only durable record of which Director wrote the session, and the claim never reads them. The
method that would answer that question exists and is dead.

**Harm, and its condition:** the harm is reachable only for an API-mode session with no session key row
(the key row is permanent once written and names one Director for good, which closes the gap for any
session that gets one). For a session without one, ownership falls to the roster, so a colleague's
Director that later lists the identifier as the only holder is accepted, and its events are written
beside the first Director's receipts under `GET /sessions/{sid}/receipts`. I cannot say from this pull
request whether API-mode sessions get a key row, because the Director side is not built. Either the
claim should read `DirectorsOf` as the comment promises, or the method and the sentence should go; a
comment that names a caller that does not exist will mislead the next seat.

Developer's answer: **Accepted in part: the dead method and its false sentence are removed; wiring the receipts into
the team claim is NOT done here, and is raised with the Tech Lead.** `DirectorsOf` and its test are gone, so no comment
names a caller that does not exist. I did not add the receipts to `TeamCallerOwnership.ClaimOf`, because that method
also decides who may READ a session in a team (two call sites), the change would alter team access for every
reader, and no test in the repository drives either path with a team key (the reviewer's note A), so the change could
not be proven here. Whether it is needed depends on a fact this pull request cannot know: whether an API-mode session
registers a session key row (Pi runs with no `CC_*` variables, so it may not). If it does, the key row decides and
there is no gap; if it does not, the claim should read the receipts' Directors. That question went to the Tech Lead
with this answer, with the recommendation that the Director register the session key for an API-mode session exactly
as for a terminal one (which closes the gap with no Gateway change). **No answer came by the deadline, so this stays
OPEN:** the phase that builds the Director side of API mode must settle it, and if API-mode sessions get no key row,
the team claim must read the receipts' Directors, with a team-key test.

### Finding 3. The batch contract omits the "refused for now" answer the hub gives

**Where:** `src/CcDirector.Gateway.Contracts/DirectorUpStreamMessages.cs`, the summary of
`ApiModeEventBatch` ("The answer is an `ApiModeEventWatermark` ... `null` means REFUSED for good ... A
hub error ... is an exception") against `DirectorHub.PushApiModeEvents`, which also answers a watermark
with an EMPTY generation and sequence -1 for `NotItsOnlyHolder` ("send it again at the next trigger").

**Harm:** the hub's own comment names the batch summary as "the contract", and the Director-side
Developer will read that summary, not the hub. Read to the letter, an empty-generation watermark is a
success that says the Gateway is on generation "" at sequence -1. A Director that adopts the answered
generation and sequence as the Gateway's watermark would send its next batch from the start and could
log that the Gateway refused nothing, when in fact nothing was stored and the whole batch must be sent
again. Two sentences in the batch summary, matching the hub's, close it.

Developer's answer: **Accepted, fixed in commit `ddb83d2f0`.** The batch summary now lists the three answers a
Director must tell apart: a watermark with a generation (stored); a watermark with an EMPTY generation and sequence -1
(refused for now, nothing stored, not a watermark to adopt, send the same batch again); and `null` (refused for good).
`ApiModeEventWatermark` carries the same warning on its fields.

## 4. Notes, not findings

These are things I checked and want on the record. None proves a harm in this pull request.

- **A. The ownership rule is unproven by any test, for both hub methods.** `PushApiModeEventsHubTests`
  run on the self-host boundary, where `TeamTurnPushRefusalFor` returns at its first line because no
  team key is on the connection. No test in the repository sets `TeamKeyHolderItemKey` on a hub
  context, so "another Director's or another person's session is refused" is true for this method by
  sharing the turn push's code, and for neither method by a run. That gap predates this branch.
- **B. No byte bound on a batch or an event.** The contract bounds a batch at 500 events and the
  verbatim receipt fields at 64 kilobytes, but `MessageText.text`, `ToolCallStarted.input` and
  `ToolCallFinished.output` are unbounded, and nothing in `Parse` sizes them. The only guard is the
  tunnel's receive limit (32 megabytes), which closes the connection rather than refusing the batch, and
  by the contract's own words the Director then sends the same batch again. A Director must size its
  batches by bytes as well as by count; the contract does not say so.
- **C. The receipt store's one lock is taken inside each session's buffer lock while a database
  transaction runs**, on the hub's invocation thread. Every session's ingest serialises on that lock, so
  a slow database stalls every API-mode session, not one. Correct, and worth knowing before the fleet
  is large.
- **D. `SubscribeAll` has no replay**, so after a Gateway restart the display fold sees `TurnEnded` for
  turns whose `TurnStarted` it never saw, and `PromptDelivered` for prompts it never saw queued. The
  fold's Developer must start from the durable receipts, not from an empty state; the interface says so
  in one clause and could say it louder.
- **E. The hub tests write their registry and statistics files under the system temporary folder**
  (`Path.GetTempPath()`) and delete them on dispose. The local gate script gives every test process its
  own temporary folder inside the run folder (`scripts/test-local.ps1`, the block at line 580), so under
  the gate this lands in the run folder, not the machine's; it is the existing pattern, not a new leak.

**Developer's answers to the notes:**

- **A.** Agreed that it predates this branch and that the method shares the turn push's code rather than proving it.
  Not changed here; it is the gap that made me decline the team-claim change in finding 2.
- **B. Accepted, fixed in commit `ddb83d2f0`.** `ApiModeEventBatch.MaxCharacters` (4,000,000 characters of event JSON
  per batch) is refused whole by the ingest with the reason, and the summary tells a Director to size its batches by
  characters as well as by count, because a message past the tunnel's limit closes the connection instead of being
  answered. Tested (`too-many-characters`).
- **C.** Agreed, and left as it is: the store's one lock keeps its writes simple and correct, and the stall needs a
  slow database plus many API-mode sessions. Worth revisiting when the fleet is large.
- **D. Accepted, fixed in commit `ddb83d2f0`.** `SubscribeAll`'s summary now says, in its own sentences, that a reader
  with no replay must start from the durable receipts, never from empty, and names the two things it will see after a
  restart.
- **E.** Agreed: the existing pattern, and contained by the gate script's own temporary folder.
