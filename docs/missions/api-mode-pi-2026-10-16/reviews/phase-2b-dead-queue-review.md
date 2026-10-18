# Phase 2 follow-on review: a receipt whose queue died gets an explicit ending (pull request #4209)

Mission: API mode for agent wrappers, proven on Pi (issue #4070). Phase 2 follow-on, item 9. Pull request
#4209, "API mode phase 2 follow-on: a receipt whose queue died gets an explicit ending", branch
`api-mode-dead-queue`, reviewed at its head commit `d26ef2500`. The branch sits on pull request #4179 (head
`b72faa0d7`, reviewed separately), so only the two item 9 commits were reviewed: `664fbc86f` (the first proof, a
later generation that did not re-announce the prompt) and `d26ef2500` (the second proof, the owning Director
came back as a new process). Reviewer seat, session bc725552, opened by the Tech Lead, running a different
agent family from the Developer. Written 18 October 2026 on devlinux.

**Verdict: hold.** The rule is sound in its design and the Developer has thought the hard cases through: I
could not make either proof fire on a prompt that is still queued or in flight through any path the pusher on
`origin/main` can take, the replacement rules are right in both readers, the new enum value breaks nothing
that reads it, and the tests do depend on the change (19 of the new tests fail with both proofs switched off).
The hold is for three things, in order of weight. First, the branch cannot merge as it stands and its first
proof rests on code the branch does not contain: GitHub reports it CONFLICTING with main, and pull request
#4190 (the re-announcement every proof-1 argument stands on) merged to main after this branch was cut, so no
test here runs the proof against the real pusher; the tests hand-write the re-announcement the proof assumes.
Second, a `TurnStarted` that names the prompt rules that prompt "no record of sending it", which is false on
its face: the event that proves is itself the record. Third, the first proof costs a store transaction, under
the store's one global lock, for every tool event of every API-mode turn. Each is small to fix. Two more
findings of low severity, and five notes.

## 1. Scope: what I read, what I ran, what I could not reach

**Read, in full, on the branch:** the diff of the two item 9 commits (fourteen files: five production, one
contract, eight test); `ApiModeLostQueuedPrompt.cs` (new) and `ApiModeReceiptStore.cs`, `ApiModeLiveRuling.cs`,
`ApiModeEventIngest.cs`, `DirectorHub.cs` as they stand after the change; `ApiModeEventPosition.cs`
(`IsLaterGenerationThan`, `IsAfter`, `StartPrecision`); `SessionLiveEndpoint.cs` (the poll route, the stream
loop's generation change and `RebuildAsync`, `StartRuling`); `ApiModeSessionFold.cs` (the queued count);
`ApiModeVerdictReceipts.cs` (the other reader of the stored state); the pull request body; all eight changed
test files.

**Read on `origin/main`, which the branch lacks:** pull request #4190's `ApiModeEventPusher.cs` (`Publish`,
`SegmentForLocked`, `AfterHoleLocked`, `NewSegmentLocked`, `TakeBatch`, `Hold`), `QueuedPrompts.cs` (`After`,
`Reannounce`), and the holes paragraph of `event-model.md`; `GatewayStreamClient.cs` (`_startedAt`, read once
from the process start time; `ReseedAsync`, which sends the Hello on the first connect, on every reconnect
and on the ten-second re-push); the receipt table's indexes in `GatewayDbContext.cs`; observation C of
`phase-2-phase-review.md`.

**Read for the clients:** `liveTypes.ts`, `liveConversation.ts` and `ApiModeLiveChat.tsx` in `client-core`;
a search of `packages`, `apps` and `tools` for any reader of the receipt state. None exists: the clients
render the live items' text and tone and the receipt's `words`, as rule 7 asks. `schema.ts` does not carry
the enum. No C# project outside the Gateway and its contracts reads `ApiModeReceiptDto`, so no older
desktop or command-line build deserialises the new string value.

**Ran, on devlinux, Debug, `CcDirector.Gateway.UnitTests`, the seven changed test classes in one filter, two
runs, each after checking no other test host was up, with `DOTNET_USE_POLLING_FILE_WATCHER=1`:**

- Run 1, the branch as it stands: **343 passed, 0 failed, 0 skipped**, 35 seconds. The sum of the Developer's
  per-class counts is also 343.
- Run 2, a deliberate break: in `ApiModeLostQueuedPrompt.cs`, `Proves` made to answer false for every event
  and `AppliesToEndedProcess` made to answer false for every set start time, everything else as on the
  branch: **19 failed, 324 passed**. The nineteen: the six proving-event cases of
  `Rule_AProofInALaterGenerationThatDidNotReannounceIt_RulesTheStoredQueuedReceiptUnknown`,
  `Rule_AProofWithoutItsPositionWhileAStoredReceiptIsQueued_Throws`,
  `Rule_ARulingThenTheRealDeliveryInTheSameReader_ReplacesIt`,
  `Rule_ThreeQueuedAndTheLaterGenerationReannouncesTwo_TheFirstIsRuledUnknownAndThePlacesMoveUp`,
  `Accept_AHoleThatSwallowedTheDelivery_TheLaterGenerationsFirstOtherEventRulesItUnknown`,
  `Hello_FromANewProcessOfTheDirector_RulesItsEarlierProcesssQueuedReceiptsUnknown`,
  `Stream_AQueuedPromptTheLaterGenerationDidNotReannounce_IsSentUnknownAsTheReceiptsEndpointSaysIt`,
  two in `ApiModeReceiptPromptTextTests`, three in `ApiModeReceiptStoreTests` (the new-process ruling, the
  new process's own generation, and the `sequence: 3` case of the late delivery), and both new tests in
  `SessionReceiptsEndpointTests`. The file was restored from git afterwards and the tree is clean. (A first
  attempt at the break did not compile, because an unreachable-code warning is an error in this build; no
  test host ran for it.) So the hub's `Hello` is proven to call the new path, which the brief asked about.

**Could not reach:** the real pusher (not on the branch, see finding 1); a real Director process, so the
process start time was not observed on a wire; a real PostgreSQL; Windows.

## 2. Findings

### Finding 1 - medium - the branch is not mergeable, and its first proof rests on code it does not contain

**Where.** The whole branch. `git merge-base --is-ancestor` of pull request #4190's merge commit
(`3298c4184`) into `d26ef2500` answers no; `origin/main` has it. GitHub reports pull request #4209
`CONFLICTING`; a `git merge-tree` of `origin/main` and `d26ef2500` conflicts in one file,
`PushApiModeEventsHubTests.cs`, where both sides edited the `NewHub` helper's signature (main added a
`fleetDisplayState` parameter; the branch changed the `Hello()` helper on the next line). The production
files auto-merge.

**The state that reaches it.** Every argument for the first proof - "the first event of a later generation
that is not a `PromptQueued` comes after every re-announcement that generation will make" - is an argument
about `ApiModeEventPusher.NewSegmentLocked` on `origin/main`, which inserts the re-announcements at the
index of the first held event after the hole with sequences 1..N and gives the events after the hole N+1
onward. I read that code and the argument holds against it, including the cases the brief named: a
generation with nothing queued re-announces nothing and its first event rules the older prompts, which is
right because nothing was queued at the hole; a batch split across pushes carries only `PromptQueued` in its
first part, which proves nothing, so the proof waits for the next part; skipping `MessageText` only delays
the proof to the next other event, because the re-announcements are contiguous at 1..N and no text can sit
among them; a second hole replaces the re-announcements not yet sent and re-announces the line as it stands
at the second hole. But none of that code is on this branch, and none of the tests here exercise it: the
ingest, store, ruling and endpoint tests all hand-write generation B's `PromptQueued` and then its next event.
`ApiModeEventPusherQueueTests` on main wires the real stream, pusher, ingest and store together and already
proves the re-announcement order; it is the one place where a hole that swallows a delivery can be driven
through the real pusher and the Unknown ruling asserted at the far end.

**The harm.** Two kinds. The mechanical one: the pull request cannot be merged until it is rebased and the
one test-file conflict resolved. The evidential one: the premise of the first proof is tested nowhere on
this branch; if #4190's insertion order ever changed, or if the rebase put the branch's `Proves` list out of
step with a new event kind the pusher sends before the re-announcements, nothing here would fail.

**What closes it.** Rebase onto `origin/main` with `--onto` as the pull request body already plans, resolve
the `NewHub` conflict (keep both: the `fleetDisplayState` parameter and the `Hello(DateTime)` helper), then
add one end-to-end test to `ApiModeEventPusherQueueTests`: three prompts queued, the first's delivery and the
turn's end fall in a hole made by the memory bound, the generation after the hole re-announces the other two,
and at the first event after them the first prompt's receipt reads the Unknown words while the other two
read 1st and 2nd. Re-run the seven classes and that one on the rebased tree.

**Developer's answer.** Accepted and done. The branch is rebased onto pull request #4179's new head (`cdd81b9a6`,
which sits on current main with #4190), with `git rebase --onto origin/api-mode-lost-turn-end b72faa0d7`. The one
conflict, in `PushApiModeEventsHubTests.cs`, is resolved keeping both sides whole: main's `NewHub` with its
`fleetDisplayState` parameter and the two display-push tests, and this branch's `Hello(DateTime startedAt)`. Three
tests are added to `ApiModeEventPusherQueueTests`, driving the real `SessionEventStream`, `ApiModeEventPusher`,
ingest, buffer and receipt store:
`Publish_AHoleSwallowsADelivery_ThePromptIsRuledUnknownAtTheFirstEventAfterTheReannouncements` (p1, p2, p3 queued;
p1's delivery published while the Gateway is off; the next generation re-announces p2 and p3 only; a reply text
proves nothing, so p1 still reads Queued; at the next tool event p1 reads the Unknown words and p2, p3 read 1st and
2nd), `Publish_AHoleWhileThePromptStaysQueued_ItIsReannouncedAndStaysQueued`, and
`Publish_AHoleSwallowsADeliveryAndTheNextEventIsItsTurnsStart_ThePromptIsNotRuledUnknown` (finding 2 through the real
pusher). The hole is made by turning the Gateway off rather than by the memory bound: it is the same hole to the
pusher (a new segment that re-announces from the stream's own queue line), and it keeps the test free of the bound's
arithmetic. With the first proof switched off, the first of the three fails.

### Finding 2 - medium - a `TurnStarted` that names the prompt rules it "no record of sending it"

**Where.** `ApiModeLostQueuedPrompt.Proves`, which answers true for `TurnStarted` with `ownPromptId` null;
`ApiModeEventIngest.FeedReceipts`, which calls `RecordQueueLeftProven` before `RecordTurnStarted`; and
`ApiModeLiveRuling.QueuesLeft`, the same order in the live reader. The pull request body records the case
under "Also not covered": "A `TurnStarted` that names an Unknown prompt shows that the prompt was delivered,
but it does not carry the acknowledgement, so it does not link the prompt".

**The state that reaches it.** The memory bound drops the OLDEST held events (`EnforceBoundLocked` removes
from index 0), so the hole is a prefix of what was held, and its last event can be anything. Take: `p1`
queued in generation A; the Gateway unreachable for long enough that the bound drops a prefix ending exactly
at `p1`'s `PromptDelivered`; the next held event is the `TurnStarted` whose `promptIds` is `[p1]`. On main the
pusher starts generation B at the hole, re-announces the line as it stood just after the hole - which does
not contain `p1`, since its delivery is in the prefix - and sends the `TurnStarted` as B's first event.
`Proves` answers true, `Applies` answers true for `p1` (Queued, older generation), and `p1` is ruled Unknown
with the words "Not known - the Director reconnected without this prompt in its queue or any record of
sending it". The same event's own `promptIds` are the record of sending it. Before this change the receipt
read `Queued: a turn is running` while the turn ran on it, which was stale but not false.

**The harm.** The user reads, on the prompt the agent is at that moment working on, that there is no record it
was sent. The words are the ruled ones for the case where nothing is known; here something is.

**What closes it.** The smallest change: let `Proves` hand back the prompt ids a `TurnStarted` names as prompts
the proof does not rule (as it already hands back the one prompt a `PromptDelivered` or `PromptRefused` ends),
so `Applies` leaves them to the turn linkage and they keep reading Queued as today. `ownPromptId` becomes a
small set rather than one string, in both readers. The better change - ruling such a prompt Delivered with
no acknowledgement, since the Director said the turn carries it - is a product ruling for the Tech Lead,
and item 7's review already noted the same gap from the other side (a prompt whose `TurnStarted` fell in the
hole). Either way, one test: a `TurnStarted` naming the only queued prompt as generation B's first event
does not rule it Unknown.

**Developer's answer.** Accepted and done, the smallest change the review names. `ApiModeLostQueuedPrompt.Proves` now
hands back the prompts the event names (`ownPromptIds`, a set): the one a `PromptDelivered` or `PromptRefused` ends,
or the ones a `TurnStarted` carries. Leaving them out of the proof is not enough on its own: a `TurnStarted` does not
link a Queued receipt, so the row would still sit in the older generation and the next proof of the same generation
would rule it Unknown anyway. So a still-queued prompt that a `TurnStarted` carries keeps reading Queued, as before
this change, and its position moves to the `TurnStarted`, as a re-announcement would move it
(`ApiModeLostQueuedPrompt.IsCarriedBy`, applied the same way in `ApiModeReceiptStore.RecordQueueLeftProven` and
`ApiModeLiveRuling.QueuesLeft`). The store method now takes the proving event itself, and refuses one that proves
nothing. Ruling such a prompt Delivered is left as the review frames it, a product ruling. Tests: in the store (the
turn's start then a later proof, never Unknown, position moved; a turn that carries another prompt still rules the
queued one; an event that proves nothing is refused), the live ruling (the same two), the ingest, and the real pusher
(above). With the change switched off, the five that pin the record fail.

### Finding 3 - medium - the first proof reads the store, under its global lock, once per tool event

**Where.** `ApiModeEventIngest.FeedReceipts`: `RecordQueueLeftProven` runs for every event `Proves` accepts,
which is every event but `PromptQueued` and `MessageText` - so every `ToolCallStarted`, `ToolCallFinished`,
`PermissionRequested`, `ConnectionReady`, `TurnStarted`, `TurnEnded`. `ApiModeReceiptStore.RecordQueueLeftProven`
takes `_gate` (one lock for every session of every tenant), opens a context, begins a transaction, reads the
session's Queued rows (served by the `(TenantId, SessionId, TurnId)` index, so the read itself is cheap) and
commits. Before this change the store was touched only by receipt events and by the three events
`ApiModeLostTurnEnd` accepts.

**The state that reaches it.** Any API-mode turn. A turn with a hundred tool calls is two hundred tool events,
each now a database round trip inside the buffer's callback, which runs under the session's lock, and inside
the store's global lock, which serialises it against every other session's receipt writes on the Gateway.
Against the hosted PostgreSQL that is a few milliseconds each; a five-hundred-event batch spends a second or
more holding the global lock for reads that can find nothing, because after the first proving event of a
generation no Queued row of an older generation can remain (they were all ruled, and an older generation is
refused by the buffer thereafter) and no new one can appear (a `PromptQueued` of this generation moves its
row to this generation).

**The harm.** Ingest latency and lock contention that grow with tool activity, for no information after the
first proof per generation. Not a correctness fault.

**What closes it.** Remember, per session, the generation the proof has already been made in (a
`ConcurrentDictionary<string, (string Generation, DateTime Started)>` in the ingest, or in the store), and
skip the read when the event's generation is that one. A Gateway restart empties it and the next event proves
once more, which is idempotent. One test: two proving events in one generation make one store read (count
the rows read with the existing test fixture, or assert on the store's own counter if one is added).

**Developer's answer.** Accepted and done. The ingest remembers, per tenant and session, the generation the proof was
last made in (in memory, a `ConcurrentDictionary`), and skips the store for every later proving event of that
generation. It is written only after the store call returns, so a failed proof is made again. A Gateway restart
forgets it and the next event proves once more, which changes nothing already ruled. The live ruling is not changed:
it reads its own memory, not the store. `ApiModeEventIngestTests.Accept_ManyProofsInOneGeneration_ReadTheStoreForTheProofOnce`
counts the database work with a diagnostic listener on the test's own database file (the instrument from pull
request #4200, kept as a private class in the test until one of the two is on main): the first proof is one
transaction, and three more tool events of the same generation do no database work at all (no connection, no
transaction, no statement). `Accept_AProofInEachOfTwoLaterGenerations_ProvesInEach` shows a newer generation proves
again. With the memory switched off, the count test fails. The dictionary holds one short entry per API-mode session
the Gateway has seen since it started and is never pruned; that is small, and named here so it is a choice.

### Finding 4 - low - the second proof's clock caveat names the safe direction and not the unsafe one

**Where.** The class summary of `ApiModeLostQueuedPrompt`, second-proof paragraph: "It could fire falsely only
if two live processes shared one Director id, or the machine's clock stepped forward between them".

**The state that reaches it.** `_startedAt` is `Process.GetCurrentProcess().StartTime.ToUniversalTime()`, read
once when `GatewayStreamClient` is built, early in the process; a generation's start is `clock.GetUtcNow()`
when its `SessionEventStream` is made, and a hole's segment is the previous start plus one millisecond. A
forward step of the Director machine's clock after the process started makes every later generation later
still, which is safe. A BACKWARD step after `_startedAt` was read, larger than the process's age at the step,
stamps a generation made after it EARLIER than the process start; the next new connection's Hello (a Gateway
deploy, a tunnel drop) then rules every prompt queued in that generation Unknown while the process that holds
them is alive. On Linux `Process.StartTime` is computed from the boot time and the process's start tick, and
its error is toward earlier, which is also safe. A later real event replaces the ruling either way.

**The harm.** A wrong sentence in the one document a future reader will trust about when the proof is wrong.
The exposure itself is small and already bounded by the replacement rule.

**What closes it.** Reword the caveat: a backward step of the Director's clock after the process started, by
more than the process's age, followed by a reconnect.

**Developer's answer.** Accepted and done. The class summary now names the unsafe direction: a BACKWARD step of the
Director machine's clock after the process started, by more than the process's age at the step, followed by a
reconnect; and says that a forward step is safe.

### Finding 5 - low - the second proof's read has no index to run on

**Where.** `ApiModeReceiptStore.ProveProcessEndedOnce`: `Where(r => r.DirectorId == directorId && r.State ==
queued)`. The receipt table's indexes are `(TenantId, SessionId, TurnId)` and `(TenantId, UpdatedAtUtc)`;
nothing leads with `DirectorId`.

**The state that reaches it.** Every new hub connection of every Director, once (the `Context.Items` guard is
right: the ten-second re-push reuses the connection, a reconnect is a new one, and a reconnect of the same
process reads and rules nothing). Each is a scan of the tenant's receipts, under the store's global lock,
inside the synchronous `Hello`, contained by the try/catch so the Hello never fails for it.

**The harm.** With the ninety-day purge the table stays bounded, so today this is milliseconds. It grows with
the tenant's receipt count, on the path every Director takes to come back after a deploy, when every Director
reconnects at once.

**What closes it.** An index on `(TenantId, DirectorId, State)`, or `(TenantId, DirectorId)`, with its
migration. Not a merge blocker on its own.

**Developer's answer.** Accepted and done. The receipt table has a new index on `(TenantId, DirectorId, State)`,
tenant-leading as the table's other indexes are for the global filter. This pull request had no migration of its
own, so it adds one on both providers, generated with the Entity Framework tool at version 9.0.2 (the project's):
`20261018124011_AddApiModeReceiptDirectorIndex` (SQLite) and `20261018124044_AddApiModeReceiptDirectorIndex`
(PostgreSQL, `gateway` schema), each newer than `AddApiModeReceiptPromptText`, with both model snapshots updated. Each
migration only creates the one index. `FleetOutcomeStopIdentityMigrationTests` passes (3 of 3). Not exercised on a
real PostgreSQL.

## 3. What I checked and found right (so the next seat need not)

- **Either proof on a prompt still queued or in flight.** First proof: see finding 1 for the pusher's order;
  in addition, the buffer refuses an older generation after a newer one, so a late batch of generation A
  cannot re-prove against B; a batch of B whose first part (re-announcements) was stored before a Gateway
  restart is continued by the Director from its first unacknowledged event, and the rows re-announced already
  carry B. The one gap I found is a batch REFUSED FOR GOOD (the hub answers null for a malformed batch): the
  pusher drops it without starting a new generation, so if it held re-announcements the events after it would
  rule those prompts Unknown. A re-announcement is built by the Director from its own events, so a malformed
  one is a Director defect; recorded, not a finding. Second proof: a reconnect of one process sends the same
  start time and every generation of that process starts at or after it; a default start time rules nothing;
  a Director whose session moved gets a new session id (the move mints one), and the old receipts are that
  dead session's; two live processes under one Director id are refused by the registry's credential binding
  except for one credential running two processes under one instance home, which is a misconfiguration.
- **The replacement rules.** A real event replaces Unknown whatever its sequence, in the store (`ReplacesUnknown`
  then `TouchForward`, the row never moving backwards) and in the ruling (`replacesUnknown` skips both guards
  and `_receiptSequences` keeps the maximum); an older-generation event is set aside as a replay in both;
  the same-generation-lower-sequence case is reachable only after a Gateway restart replay and is safe.
- **Every switch on the state.** `ReceiptWords` and `ReceiptTone` have the new arm; `PlaceInLine` and
  `WaitingIfMoved` count `Queued` only, so an Unknown receipt has no place and the places behind it move up
  (tested); `ApiModeSessionFold` counts `State == Queued`, so the roster's queued count drops it;
  `ApiModeVerdictReceipts` and the store's `Read` parse the stored string with `Enum.Parse`, which accepts
  `Unknown`; the entity's column is a plain string. The enum is serialised as a string on the wire, and no
  client or other C# project reads it, so nothing older breaks.
- **The three surfaces agree for the first proof.** The store rules at the proving event inside the buffer's
  callback, before the buffer publishes it, so a stream that rebuilds from the store at the generation change
  opens with Unknown, a stream that rebuilt earlier rules it at the same event, the poll's ruling likewise,
  and the receipts endpoint words it through the same static function. `Stream_AQueuedPromptTheLaterGenerationDidNotReannounce_IsSentUnknownAsTheReceiptsEndpointSaysIt`
  compares the two.
- **The tests.** Names follow `Method_Scenario_Result`; no clock, process, sleep or port; nineteen fail without
  the change and the rest are guards that nothing happens where nothing should. Logging: every new write logs
  the decision with session, prompt, positions and the proof, in the file's existing style.

## 4. Notes (not findings)

- **N1. A live stream already open on a dead session never learns the second proof.** The pull request body
  states this as an accepted limit: the receipts endpoint and the stream's opening read the store; an open
  stream sees it at its next reset. The brief's ruling asks that "the endpoint, the live stream and the
  opening agree"; for the second proof they agree only after a reload. If the Delivery Lead wants the open
  stream to follow, the mechanism exists: the Hello path knows which sessions it changed, and a reset frame
  (`RebuildAsync`'s path) already makes a stream rebuild from the store. That is new plumbing from the hub to
  the feed, so a follow-on item, not this pull request.
- **N2. The event model does not yet state either Gateway ruling.** The holes paragraph of `event-model.md`
  on main still ends "A delivery or refusal whose own event fell in the hole is not corrected by the
  Director", which is true of the Director; the Gateway's `lost` turn ending (item 7) and `Unknown` receipt
  ending (item 9) belong in that paragraph in one sentence each. Neither item touched the document.
- **N3. The pull request body says the Developer ran `PushApiModeEventsHubTests` (8 passed).** The brief said
  the hub classes had not been run; my run 1 ran the class (8 of the 343) and run 2 shows its Hello test fails
  without the change, so the hub really does call the new path.
- **N4. A re-announcement too large for any batch** is dropped by the pusher on the ground that its own
  `PromptQueued` was as large and the Gateway never held the receipt. The re-announcement carries one more
  field (`position`), so a prompt text within a few characters of the four-million limit could fit as a
  `PromptQueued` and not as a re-announcement, and would then be ruled Unknown while queued. Negligible, and a
  real event replaces it.
- **N5. The word "lost" in the pull request title and the entity comment.** The pull request title says
  "whose queue died"; the words shown to the user claim neither a death nor a hole, as the class summary
  explains at length. Good: the summary is the clearest statement in the mission of why a Gateway inference
  must not claim more than both proofs show.

## 5. Verdict

**Hold.** Rebase onto `origin/main` (finding 1), add the one end-to-end test through the real pusher
(finding 1), keep a `TurnStarted`'s own prompts out of the proof or rule them deliberately (finding 2), and
prove once per generation (finding 3). Findings 4 and 5 can ride along or follow. With those four done and
the seven classes plus `ApiModeEventPusherQueueTests` green on the rebased tree, I would approve without a
second full read: the design, the words, the replacement rules and the client safety are right as they stand.

## Check of the review fixes

Pull request #4209 checked at its head `457d447c6`, now rebased onto pull request #4179's head `cdd81b9a6`, which sits
on current main with pull request #4190; nothing on main is ahead of the branch. GitHub reports it MERGEABLE and CLEAN.
Reviewer seat, session bc725552, 18 October 2026 on devlinux.

**Verdict: hold, on one thing only - the addition beyond the ruling.** Findings 1, 3, 4 and 5 are fixed as asked, and
the index migration is right on both providers. Finding 2 is fixed in the sense that a carried prompt no longer reads
"no record of sending it"; but the state it reads instead - Queued, counted in the new generation's line - is one the
Director does not hold, it miscounts every prompt behind it for the rest of the generation, and it turns into the
Unknown words at the next hole anyway. A prompt a `TurnStarted` carries should be ruled Delivered, because in the
Director's own events a prompt enters a turn's `promptIds` only after its `PromptDelivered` was published. That is a
product ruling for the Tech Lead; the reasoning and the evidence are below. With it ruled either way and one more
small commit, I would approve.

**Read, in full:** the diff from `cdd81b9a6` to `457d447c6` (the three production files, the contract, the context, both
migrations with their designers and snapshots, nine test files, the Developer's five answers in this file); `PiDialect.cs`
on `origin/main` (`_deliveredAwaitingTurn`, filled at line 460 in the `started` disposition of a `PromptDelivered` and
read by `OnAgentStart` into `TurnStarted.PromptIds`); pull request #4200's file list.

**Ran, Debug, `CcDirector.Gateway.UnitTests`, two runs, each after checking no other test host was up:**

- Run 1, the nine affected classes in one filter (the seven of the first review, `ApiModeEventPusherQueueTests` and
  `FleetOutcomeStopIdentityMigrationTests`): **366 passed, 0 failed, 0 skipped**, 41 seconds.
- Run 2, one throwaway test appended to `ApiModeEventIngestTests` and removed afterwards (the tree is clean), driving
  the real ingest and store exactly as the Developer's tests do: generation A queues `p1`; its delivery falls in the
  hole; generation B opens with the `TurnStarted` that carries `p1`, that turn ends `done`, and the user queues `p2`;
  then generation C re-announces `p2` only and a tool runs. The test asserts the words as the receipts endpoint derives
  them (`ReadWithPositions`, `PlaceInLine`, `ReceiptWords`) after B, and `p1`'s state after C. **It passed**, which
  means the branch answers: after B, `p1` reads "Queued, 1st in line: a turn is running" and `p2` reads "Queued, 2nd
  in line: a turn is running"; after C, `p1` is Unknown.

### Finding 1 - fixed

Rebased with `--onto`; the one conflict resolved keeping both sides. Three end-to-end tests through the real
`SessionEventStream`, `ApiModeEventPusher`, ingest, buffer and store. The first is the one I asked for and it pins
every step: the re-announcement holds `p2` and `p3` only, `p1` still reads Queued after the reply text (which proves
nothing), and at the tool event `p1` reads the Unknown words with `p2` and `p3` at 1st and 2nd. The hole is made by
turning the Gateway off rather than by the memory bound; to the pusher it is the same hole (a new segment that
re-announces from the stream's own line), so that is a fair simplification. The third test asserts only that `p1` does
not read the Unknown words, not what it does read - see finding 2.

### Finding 2 - fixed in its letter, and the replacement state is wrong

What the branch does: `Proves` hands back every prompt the event names; `IsCarriedBy` holds for a `TurnStarted` that
names a Queued receipt of an older generation, and both readers then keep the receipt Queued and move its position to
the `TurnStarted`, "as a re-announcement would". The Developer's reason is right as far as it goes: with the proof made
once per generation in the ingest and not at all in the ruling's memory, leaving the prompt in the older generation
would let the next proof of the same generation rule it Unknown in the live ruling, so its position has to move.

Why the moved Queued state is the wrong one:

- **The Director does not hold it.** The Director's own line (`QueuedPrompts`) dropped `p1` at its `PromptDelivered`,
  which is why generation B did not re-announce it. Moving `p1` into B's generation makes the Gateway count it in B's
  line: for the rest of generation B, after its turn has ended, `p1` reads "Queued, 1st in line: a turn is running",
  every prompt queued behind it reads one place too far ("2nd in line" for the only prompt the Director holds), and
  `ApiModeSessionFold`'s queued count is one too high. Before item 9 the same receipt read "Queued: a turn is running"
  with no place and was not counted, because `PlaceInLine` counts only the newest generation. So on this one path the
  fix regresses the place in line, which is the thing pull request #4178 and #4190 were built to get right.
- **It is not stable.** At the next hole, generation C re-announces the Director's line, which does not have `p1`, and
  C's first proving event rules `p1` Unknown - the very words finding 2 objected to, arriving one hole later.
- **The record it rests on says more than "Queued".** In `PiDialect`, a prompt id enters `TurnStarted.PromptIds` from
  `_deliveredAwaitingTurn`, which is filled only when the `PromptDelivered` is published (line 460, the `started`
  disposition) and read into the turn by `OnAgentStart`; `EndNeverStartedTurn` reads the same set. So a `TurnStarted`
  that carries a prompt the Gateway still holds as Queued at a later generation is proof that the prompt's
  `PromptDelivered` was published before it and fell in the hole. That is the same strength of proof as the one the
  whole item rests on, and it proves delivery, not continued queueing.

What I recommend, for the Tech Lead to rule: a Queued receipt of an older generation that a later generation's
`TurnStarted` carries is ruled **Delivered** by the Gateway - its disposition and acknowledgement unknown (both fields
are already nullable, and `ReceiptWords` for Delivered reads neither), the turn linked by the same event, since the proof
runs before `RecordTurnStarted` and the linkage takes Delivered rows - so the turn's outcome then lands on it by the
existing rules, and a later turn ending reads on it as on any delivered prompt. This is the Gateway's inference, as
`lost` and `Unknown` are, so it should be marked as one on the row (a disposition word for "the acknowledgement was
lost", parallel to the `lost` turn outcome) and replaced by the real `PromptDelivered` should one ever arrive, which it
cannot except as a replay. With that, `IsCarriedBy` becomes the branch that rules Delivered instead of the branch that
moves a Queued row, in the same two places, and the third pusher test asserts "Delivered" rather than "not Unknown".
If the Tech Lead rules instead to keep the Queued state, the two consequences above (the place in line for the rest of
the generation; Unknown at the next hole) belong in the class summary, and the third pusher test should assert the
words it does produce, so the choice is on record.

### Finding 3 - fixed

The ingest remembers, per tenant and session, the generation the proof was last made in, written only after the store
call returns, so a failed proof is made again; a newer generation proves once more; a Gateway restart proves once more
and changes nothing. `Accept_ManyProofsInOneGeneration_ReadTheStoreForTheProofOnce` counts the database work with a
diagnostic listener and shows three later tool events do no database work at all;
`Accept_AProofInEachOfTwoLaterGenerations_ProvesInEach` shows the next generation proves again. The dictionary is never
pruned; one short entry per API-mode session since the Gateway started is small, and the Developer names it as a choice.
Right as done.

### Finding 4 - fixed

The class summary now names the unsafe direction (a backward step of the Director machine's clock after the process
started, by more than the process's age, followed by a reconnect) and says a forward step is safe.

### Finding 5 - fixed, and the migration is right

`GatewayDbContext` adds `HasIndex(TenantId, DirectorId, State)` with a comment saying why. Two migrations,
`20261018124011_AddApiModeReceiptDirectorIndex` (SQLite) and `20261018124044_AddApiModeReceiptDirectorIndex` (PostgreSQL,
schema `gateway`), each the newest in its folder, each creating only `IX_api_mode_receipts_tenant_id_DirectorId_State`
on `api_mode_receipts` over `tenant_id`, `DirectorId`, `State` - the same column spelling as the table's existing
`IX_api_mode_receipts_tenant_id_SessionId_TurnId` - with a matching `Down`. Each designer's target model is identical to
its provider's updated snapshot (I diffed the model bodies; no difference on either provider), and each designer is two
lines longer than the previous migration's, which is the index. Pull request #4200 (open) adds
`20261018041929_AddApiModeConversationItems` and `20261018042014_AddApiModeConversationItems`, timestamped earlier than
these, and edits both snapshots: whichever of the two merges second must regenerate its migration against the merged
snapshot so the chain and the snapshot agree. Not exercised on a real PostgreSQL, as the Developer says.

### The rest

The hub test conflict is resolved keeping main's `fleetDisplayState` parameter and its two display-push tests beside
this branch's `Hello(DateTime)` helper. Nothing else in the two item 9 commits changed beyond what the five answers
describe. With the carried prompt ruled - Delivered as recommended, or Queued with its consequences recorded - and the
nine classes green on that commit, I would approve.
