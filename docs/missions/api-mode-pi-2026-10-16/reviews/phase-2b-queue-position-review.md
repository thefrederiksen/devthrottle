# Phase 2 follow-on review: a queued receipt says its place in line (pull request #4178)

Mission: API mode for agent wrappers, proven on Pi (issue #4070). Phase 2 follow-on, item 5. Pull request
#4178, "API mode phase 2 follow-on: a queued receipt says its place in line", branch
`api-mode-queue-position`, reviewed at its head commit `180a8fc03` (one commit on base `3260adb7d`, which was
`origin/main` at the time of the review). Reviewer seat, opened by the Tech Lead, running a different agent
family from the Developer. Written 17 October 2026 on devlinux.

**Verdict: approve.** The change does what the Tech Lead's brief rules: the words are
`Queued, <ordinal> in line: <why>`; the place is derived at read time from the session's still-queued receipts in
queue order and never stored; the live stream re-sends the words of every still-queued receipt that moved up,
in the frame of the event that moved it; when no place can be known the words stay `Queued: <why>`; and the
receipts route, the live ruling and the stream's opening from the store give the same words for the same
receipts after every event, which a test now pins. The Developer's one judgement call, that a receipt still
queued from an older Director generation gets no place and is not counted ahead of anyone, is true to the
Director as it stands on main today. It is not true to the Director that pull request #4090 (open, not merged)
would make, and that collision is finding 1. Nothing found blocks this merge; findings 1 and 2 are for the
Delivery Lead to carry to #4090 and to the receipt store's backlog.

## 1. Scope: what I read, what I ran, what I could not reach

**Read, in full, on the branch:** the diff against `origin/main` (ten files, 536 insertions, 29 deletions);
`ApiModeLiveRuling.cs` as it stands after the change (the constructor from stored receipts, `Rule`,
`ReceiptChange`, `WaitingIfMoved`, `InQueueOrder`, `Touch`, `Supersedes`, `TurnStart`, `TurnEnd`, `Ordinal`,
both `PlaceInLine` overloads, `ReceiptWords`, `ReceiptItem`, `Copy`); `SessionReceiptsEndpoint.cs`;
`SessionLiveEndpoint.cs` (`StartRuling`, `StoredReceipts`, `ReceiptItems`, the poll route's reset path, the
stream loop's generation change and `RebuildAsync`); `ApiModeReceiptStore.cs` (`ReadWithPositions` and its
order, `Supersede`, `PurgeOlderThan`); `ApiModeEventPosition.cs`; `ApiModeEventBuffer.Apply` (what the Gateway
does when a generation moves); `ApiModeReceiptDtos.cs`; the Director's `ApiModeSessionHost` (`SendPromptAsync`
and its `Position = _queue.Count`, `WriteQueuedPromptsLocked`, `WritePromptLocked`, `OnProcessEndedLocked`,
`RefuseQueuedLocked`, `DisposeAsync`) and `SessionEventStream` (where a generation is born); the contract's
`PromptQueued.Position` and event-model.md line 59; issue #4090's text; and all five changed test files with
the shared helpers in `ApiModeEventTestData`.

**Ran, twice, as the brief allowed (one class each, with no other test host on the machine at the time):**

1. `ApiModeLiveRulingTests` on the branch as pushed: 77 passed, 0 failed.
2. `ApiModeLiveRulingTests` with one mutation, the loop in `WaitingIfMoved` that re-sends the still-queued
   receipts removed (the behaviour before this change): 4 failed, 73 passed. The mutation was then reverted
   with `git checkout`; the tree is clean. So the new tests do fail without the re-send.

The other four classes (`SessionReceiptsEndpointTests`, `ApiModeReceiptStoreTests`,
`SessionLiveEndpointTests`, `ApiModeVerdictOnTheRosterTests`) were not run here; the Developer reports them at
11, 42, 54 and 8 passed, and my reading gives me no reason to doubt it.

**Could not reach:** a real Pi; a real Director restart against a real Gateway (the Director's event push, #4090,
is not merged, so no Director event reaches a Gateway today); Windows; the web clients' rendering of a
receipt (I searched `packages/client-core`, `apps/cockpit` and `apps/mobile` for any code that composes
"Queued" words or reads `queuePosition`, and found none, so a client can only render `words` verbatim).

## 2. The brief's rulings, checked one by one

**The words.** `ReceiptWords` gives `Queued, {Ordinal(place)} in line: {why}` when `QueuePosition` is set and
`Queued: {why}` when it is null (`ApiModeLiveRuling.cs` lines 219 to 221). Nothing else composes a queued
receipt's words: `ReceiptItem` calls `ReceiptWords`, the receipts route calls `ReceiptWords`, and the only
other reader of the store outside tests, `ApiModeVerdictReceipts`, does not word receipts at all (the roster
test that changed its expected words reads the receipts route).

**The ordinal.** `Ordinal` (line 270) takes `place % 100` of 11, 12 or 13 as `th` first, then `place % 10` for
`st`, `nd`, `rd`, else `th`. Traced by hand: 1st, 2nd, 3rd, 4th, 10th, 11th, 12th, 13th, 14th, 21st, 22nd, 23rd,
101st, 111th, 112th, 113th, 121st, 1011th. Invariant culture, so no locale can change the digits. A place below
one throws, which is right: a place is produced only by `PlaceInLine`, which starts at 1, so a lower value is a
defect and never words. The theory covers 1 to 4, 10 to 13, 21 to 23, 101, 111 to 113 and 1000.

**True now, derived at read time, from still-queued receipts in queue order.** `PlaceInLine` (lines 295 to 318)
walks the receipts in the order given and numbers only those whose state is `Queued`, one plus the count before
them. The receipts route hands it `ReadWithPositions`, whose order is `(FirstGenerationStartedUtc, FirstSequence,
PromptId)`, the order the prompts arrived. The live ruling hands it `_order`, which is the order its receipts
were first seen, and for a ruling started from the store, the store's order. The Director's queue is a list
drained from index 0 (`WriteQueuedPromptsLocked`) and appended at the end (`SendPromptAsync`), so arrival order
is its queue order. The number is never written: the entity has no column, the store's `ToDto` does not set it,
and `Read_QueuedPrompts_AreReadInTheOrderTheyWereQueuedAndCarryNoStoredPlaceInLine` pins that the store
answers null for every receipt.

**The live re-send on every leaving event.** `WaitingIfMoved` (line 371) recounts every place and then runs
`ReceiptIfChanged` over every still-queued receipt in order, so only one whose words changed is sent again. It
is reached from every path that can take a prompt out of the queue:

- delivered and refused: `ReceiptChange` ends in `WaitingIfMoved(e.Sequence, receipt)`;
- a retry superseding a refusal: the same `ReceiptChange`, after `Supersedes` has moved the prompt to the end
  of `_order`;
- stopped: a queued prompt leaves the queue on a stop only as a `PromptRefused` from the Director
  (`RefuseQueuedLocked`), which is the refused path above; a delivered prompt's "Delivered - stopped" comes from
  `TurnEnd`, which also ends in `WaitingIfMoved` although a turn ending never moves a queued prompt;
- a newer generation's first receipt event: `ReceiptChange` touches the receipt's last position, and the recount
  takes the older generation's queued receipts out of the line (test
  `Rule_TheFirstPromptQueuedByANewerGeneration_TheOlderQueuedReceiptLosesItsPlace`).

The changed receipt's item comes first in the frame, then the moved receipts in queue order, each under the
sequence of the event that moved it, which `Rule_TheOneAheadIsDelivered_EveryWaitingPromptMovesUp` pins
including the sequence. `Rule_APromptBehindTheWaitingOnesIsDelivered_NoWaitingPromptIsSentAgain` pins that
nothing is re-sent when no place moved. The mutation run above shows the re-send is load-bearing.

**The retry joins the end of the line, and it matches the Director.** `Supersedes` removes and re-adds the prompt
id in `_order` (lines 431 to 432), the store moves `FirstGenerationStartedUtc` and `FirstSequence` to the
retry's event (#4142), and the Director's retry is a new `SendPromptAsync` call that does `_queue.Add`, so all
three put it last. `Rule_ARefusedPromptRetried_JoinsTheEndOfTheLine` and the store test pin the two Gateway
readers; the endpoint's agreement test runs a refusal and a retry through both and compares after every event.

**The opening of the live stream from the store.** `StoredReceipts` in `SessionLiveEndpoint` now calls
`PlaceInLine` over what it read, so the opening items (`ReceiptItems`, sequence 0) carry the place, and the
ruling built from the same list copies `QueuePosition`, recounts over its own copies, and records those words
as already shown, so the first live event after the opening sends only what changed. The same function runs
on the poll route's reset path and on the stream's `RebuildAsync` (a new generation, or missed events), since
both go through `StoredReceipts`. `Rule_SeededFromStoredReceipts_TheWaitingPromptMovesUpWhenTheOneAheadIsDelivered`
pins the opening words and the first move after them.

**The Director's `Position` counts from 1, and the Gateway's place agrees on a fresh queue.** The contract
(`SessionEvents.cs` line 69) and event-model.md line 59 both say position 1 is the next prompt to be written;
`ApiModeSessionHost.SendPromptAsync` sends `_queue.Count` after the add, so the first queued prompt is 1.
The Gateway does not read `Position` at all; it derives. `Rule_AFreshQueue_EachPlaceIsTheDirectorsOwnPosition`
proves the derived place equals the Director's number for three prompts queued on an empty queue. See note 2
for what is not checked.

**The two readers agree.** `Read_AndTheLiveRuling_GiveTheSameWordsForEveryReceiptAfterEveryEvent` drives seven
events (three queued, a refusal, a retry, two deliveries) through the store as the ingest writes it and through a
live ruling, and after each event compares the receipts route's words with the ruling's shown words and with
the stream's opening from the store. `Stream_TheOneAheadIsDelivered_TheWaitingPromptMovesUpAndAgreesWithTheReceiptsRoute`
does the same on the real stream through the ingest. That is the project's rule 7 proven as the brief asked,
not merely by sharing a function.

**Additive, not stored, no migration.** `QueuePosition` is a nullable integer on the receipt contract class only. There is no
entity change, no migration, and `git show --stat` lists no file under the persistence folders, so it cannot
collide with the migration in #4146.

## 3. The Developer's judgement call: an older generation's queued receipt gets no place

**Is it true to the Director?** On main as it stands, yes. A generation is born with a `SessionEventStream`
(`SessionEventStream.cs` line 55), of which there is one per session for the life of the Director process, and
the Director's queue is a private list in `ApiModeSessionHost`, which lives and dies with that process. A Pi
process restart inside one Director keeps the generation and keeps the queue (`OnProcessEndedLocked`: when
`restart` is true, the queue is not refused and the log line says "queued=N; the overdue prompt is not sent
again"), so a queued prompt in the same generation is still in line, and the Gateway counts it, which is right.
A Director restart is a new process, a new stream, a new generation, and a new empty queue, so a receipt still
queued from the old generation names a prompt no Director holds. Giving it no place, and not counting it ahead
of the new generation's prompts, is the only honest answer the Gateway can give from what it has. The
recount happens the moment a receipt event of the newer generation lands, for both readers alike (the store's
rows carry each receipt's last generation; the live stream rebuilds from the store on a generation change, so a
ruling never mixes two generations except through a stored receipt's position, which is exactly what
`PlaceInLine` reads).

**But the premise, "a new generation means the old queue is gone", is about to stop being true.** That is
finding 1.

**Is an orphan ever resolved?** No. That is finding 2, and it is pre-existing.

## 4. Findings

### Finding 1 (medium, against pull request #4090 rather than this one). A hole in the Director's push starts a new generation inside one Director process, whose queue survives, so under #4090 the Gateway's "older generation has no queue" rule would understate the newer prompts' places and strip the older ones of a place they still hold

**Where.** The rule is `PlaceInLine` (`ApiModeLiveRuling.cs` lines 304 to 318) and its summary, which says
"Only the newest Director generation among the receipts holds a queue: a Director restart starts a new one".
The collision is in issue #4090, "The memory bound" section: "A hole starts a new generation: after any event
the Gateway cannot be sent (dropped by the bound, too large, or never handed to the pusher because the Gateway
was turned off in the settings), what follows goes up as a new generation from sequence 1, started a millisecond
after the one it continues." The pusher that does this is not on main (no file in `src` names it), and #4090 is
open.

**The state that reaches it.** The Director holds p1 and p2 queued (positions 1 and 2, generation A). The
Gateway is turned off in the Director's settings for a minute and turned on again, or the pusher's bound drops
an event. The Director keeps its process, its queue and its two prompts, but what it sends next is generation
B from sequence 1. The user sends p3; the Director queues it at its true position 3 and sends `PromptQueued`
for p3 in generation B.

**The harm.** The Gateway's `ApiModeEventBuffer` takes B as a later generation. The receipts route and the live
stream (after its `NewGeneration` reset) then read p1 and p2 as "Queued: a turn is running" with no place, and
p3 as "Queued, 1st in line", while the Director will write p1, then p2, then p3. The number shown for p3 is a
guess, and a wrong one, which the brief forbids ("never a guessed number"); p1 and p2 lose a place they truly
hold. It self-heals when p1 and p2 are delivered (their deliveries arrive in B and end their receipts), so it is
not for ever, but every place shown in between is wrong. The Gateway cannot tell this case from a restart: the
only signal it has is the generation, and #4090 gives both cases the same signal.

**What to do.** Nothing in this pull request; its rule is right for the Director on main. Carry this to #4090
before it merges, with two ways out for the Delivery Lead to choose between: (a) after a hole, the Director
re-announces every prompt still in its queue with a fresh `PromptQueued` in the new generation (the position
true at that moment), so the Gateway's rule becomes true again by the Director's own word; or (b) a hole does not
start a new generation. (a) also fixes the #4090 text's own gap ("The receipt of a prompt whose own event fell in
the hole is not corrected"). Whichever is chosen, the `PlaceInLine` summary's sentence about what a generation
means should then be checked against it.

### Finding 2 (low, pre-existing). A receipt still queued when its Director generation dies is never resolved on the Gateway: it reads "Queued: a turn is running" with no place until retention deletes it, ninety days later

**Where.** No code on the Gateway ends a queued receipt of a superseded generation. `ApiModeEventBuffer.Apply`
(line 131) logs "moved from generation A to generation B" and lets the old generation's held events go, and
nothing follows that with a write to `ApiModeReceiptStore`. The store's only end is `PurgeOlderThan`, run by
`SessionHistorySweep` with `Retention` of 90 days. This pull request does not introduce it: the store (#4082)
and the live ruling (#4083) have no rule for it either. What this pull request changes is the words, from
"Queued: a turn is running", which read as if the prompt were about to be written, to the same words with no
place, which the pull request text documents as "no place can be known". That is better than "1st in line",
and it is the right call within this change, but it is not resolution.

**The state that reaches it.** The Director holds p1 queued. It crashes, or its machine loses power. Nothing
refuses p1: `RefuseQueuedLocked` runs only on a clean end of Pi's process (`OnProcessEndedLocked` with
`restart` false), and only a clean `DisposeAsync` reaches that. Even on a clean shutdown the refusal is an event
of the old generation that must still be pushed before the process exits, and whether #4090's pusher flushes at
dispose is not promised in its text. Today, with no pusher merged, nothing reaches the Gateway in either case.

**The harm.** The user sees a prompt that will never be written listed as queued for up to ninety days. Any
later Gateway feature that counts queued receipts (the roster's verdict, a "waiting prompts" badge) would count
a ghost. The Gateway does know the moment the generation moved, so it could rule the receipt ended by the
Director's restart with words of its own; but the event model says a refusal's reason is the Director's or the
agent's words, so that needs a ruling from the Architect or the Delivery Lead, not a Developer's choice. Record
it as follow-on work on the receipt store, beside #4090: either the Director refuses its queue on every end it
can see and the pusher flushes before exit, and the Gateway ends what the Director could not.

### Finding 3 (low). A dead generation's queued receipt keeps "1st in line" until a receipt event of the newer generation lands, although the Gateway already knows the generation moved

**Where.** `PlaceInLine` (line 304) finds the newest generation among the receipts it is given, not the
session's current generation. `ApiModeEventBuffer` knows the current generation; the live stream knows it
(`held.Generation`, line 321); neither hands it to the recount.

**The state that reaches it.** p1 queued in generation A; the Director restarts; the new Director connects and
sends `ConnectionReady` and a turn's events in generation B, but no receipt event yet (no prompt has been sent
since the restart).

**The harm.** Both readers say "Queued, 1st in line: a turn is running" for p1 until some prompt is queued,
delivered or refused in B. The two readers still agree with each other, so rule 7 holds; what is wrong is the
place itself, for a window that is usually short. It is the same gap as finding 2 in a smaller form, and the
honest fix is the same ruling. If the Delivery Lead wants it closed on its own, the recount can take the
session's current generation as a parameter (the buffer's, for the receipts route; `held.Generation`, for the
stream), and the first `TurnStart` or `ConnectionReady` of a new generation would then strip the place at
once. Not for this pull request.

## 5. Notes (none blocks the merge)

1. **Cost per event.** `WaitingIfMoved` runs on every receipt event, every `TurnStarted` and every `TurnEnded`,
   and each run builds a list of every receipt the ruling holds (`InQueueOrder`), recounts them, and builds the
   words of every still-queued receipt. That is linear in the ruling's receipts, not in the buffer's 2,000
   events, and only text, so for a session with hundreds of prompts it is microseconds per event. `_order.Remove`
   on a retry is also linear. Message text and tool events, the bulk of the wire, never reach it. No concern.
2. **The Director's `Position` is carried but never checked.** The Gateway derives the place and ignores the
   number the Director sent with `PromptQueued`. On a fresh queue the test proves they agree; after a hole
   (finding 1) they would not, and nothing would say so. One log line in `ReceiptChange` when a `PromptQueued`'s
   `Position` differs from the place just derived would be a cheap detector of exactly the states findings 1 and
   2 describe. Optional.
3. **A one-event lead over the Director.** The Director removes a prompt from its queue when it writes it to Pi,
   and the Gateway moves the receipt out of `Queued` when Pi's acknowledgement arrives as `PromptDelivered`. In
   between, the Gateway's count of the line is one more than the Director's. Harmless: on the Gateway's own
   terms the prompt is queued until it has its receipt.
4. **`TurnStart` now returns items it never did before.** It used to return empty always; it now returns the
   result of a recount, which is empty unless a place moved. The summary on `Rule` still says a turn starting
   "changes no receipt's words", which stays true in every reachable state because the live stream rebuilds on a
   generation change before a `TurnStarted` of the new generation could touch a receipt. Fine as written.
5. **Tests.** Every new test is named `Method_Scenario_Result`, arranged, acted and asserted. The ones that pin
   the change fail without it (the mutation run). Two are pins rather than proofs:
   `Rule_APromptBehindTheWaitingOnesIsDelivered_NoWaitingPromptIsSentAgain` passes before the change too (it
   guards against over-sending, which is its point), and the store test's "carries no place" clause is true of
   any receipt object the store never sets; both are worth keeping.
6. **The opening is counted twice.** `StoredReceipts` runs `PlaceInLine` over the read, then the ruling's
   constructor runs it again over its copies. Same input, same answer; the second run is what lets the
   ruling's shown words match the opening. Clear enough with the comment; no change asked.

## 6. Verdict

**Approve.** Merge after the Tech Lead's own check of the four classes I did not run. Carry finding 1 to pull
request #4090 before it merges, and findings 2 and 3 to the receipt store's follow-on list with a request for
the Architect's or Delivery Lead's ruling on who ends a queued receipt whose Director is gone.
