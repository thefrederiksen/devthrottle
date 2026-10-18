# Phase 2 follow-on review: a retried prompt's later receipt stands (pull request #4142)

Mission: API mode for agent wrappers, proven on Pi (issue #4070). Phase 2 follow-on, item 6: finding 1 of
`phase-2-phase-review.md`. Pull request #4142, "API mode phase 2 follow-on: a retried prompt's later receipt
stands", branch `api-mode-later-receipt-stands`, reviewed at its head commit `998401695` (one commit on base
`617912aab`, which was `origin/main` at the time of the review). Reviewer seat, session 126, opened by the
Gateway Tech Lead, running a different agent family from the Developer. Written 17 October 2026 on devlinux.

**Verdict: holds, on condition that finding 1 is fixed.** The store, the fold's turn numbering and the ordering
guards implement the event model's rule as written: only a `Refused` receipt is superseded, `Delivered` stays
final, and no older event - a re-sent one, a lower sequence, an older generation - can move a receipt backwards.
The one place the change does not hold is the live ruling when it is started from the stored receipts: it takes
a retry's refusal for a replay of the refusal it already holds, so a stream can show the wrong refusal reason
for as long as it stays open (finding 1). That is the same gap the Developer recorded as a "known limit", but
the limit as written understates it: the record says the last item a reader is handed is always the stored one,
and in finding 1 it is not. The fix is small and local to `ApiModeLiveRuling`, and it removes the known limit at
the same time. Three further notes; none blocks the merge.

Judged by reading only. The Tech Lead's brief said not to run tests on this machine (the machine crashed from
parallel test runs; the full gate runs on Windows), so nothing below is a run. Where I say a test passes, I
traced the code by hand; the Developer reports the touched classes at 100 of 100 and the caller classes at
100 of 100, and I have no reason from the reading to doubt that.

## 1. Scope: what I read, what I did not run, what I could not reach

**Read, in full, on the branch:** the diff against `origin/main` (seven files, 532 insertions, 38 deletions);
`ApiModeReceiptStore.cs`, `ApiModeLiveRuling.cs` and `ApiModeReceiptEntity.cs` as they stand after the change;
`ApiModeSessionFold.cs` and `ApiModeVerdictReceipts.cs` (unchanged, the readers whose correctness rests on the
store's read order); `ApiModeEventPosition.cs` (the position rule); `ApiModeEventIngest.cs` (the receipts are
written before the buffer hands an event on); `ApiModeEventBuffer.cs` (`Apply`, `ReplayOf`, when a replay is
complete); `SessionLiveEndpoint.cs` (`StartRuling`, the stream loop's generation change and `RebuildAsync`, the
poll route); the Director's `ApiModeSessionHost.ResolvePromptIdLocked` (a Gateway id is reused only after its
refusal settled, and a delivered id is never sent again); `event-model.md` section 2 ("What a `promptId` is"
and the five rules); finding 1 of the phase 2 phase review and the Developer's answer appended to it; finding 2
of the #4083 live-stream review, which produced the test this change alters; and all three changed test files
with the shared event helpers in `ApiModeEventTestData`.

**Did not run:** anything. No build, no test, by instruction.

**Could not reach:** the Director's re-send after a Gateway restart (decision 13) other than as the phase review
describes it; a real Pi; Windows.

## 2. The six questions

**(1) The ordering guard.** Holds in the store and holds in the ruling, for every way an older event can arrive.

- Store: `IsNewer` is asked before any state is looked at, in all three record methods, and `Supersede` is only
  reachable behind it. `ApiModeEventPosition.IsAfter` compares sequences inside one generation and generation
  start times across generations, so a re-sent batch of the same generation (same or lower sequence) and a late
  event of an older generation both answer "not newer" and return null before `Supersede` runs. A retry in a
  NEW generation (a Director restart between the refusal and the retry, sequence starting again at 1) is newer
  whatever its sequence, which is right. The two new tests pin both cases
  (`RecordRefused_AnOlderRefusalReplayedAfterTheRetrysDelivery_DoesNotOverwrite`,
  `RecordRefused_FromAnOlderGenerationAfterTheRetrysDelivery_DoesNotOverwrite`).
- Ruling: `_receiptSequences` refuses any receipt event whose sequence is at or below the last one ruled for
  that prompt, before the receipt is created or changed. It compares sequence only, with no generation, and
  that is safe because the callers never feed one ruling two generations: the stream rebuilds the ruling from
  the store the moment a held event's generation differs (`SessionLiveEndpoint` line 321), and a poll builds a
  fresh ruling per request from one generation's held events. The guard is defence in depth: the buffer already
  dedupes at ingest, so a subscriber is never handed a duplicate in one Gateway lifetime. See note 3.

**(2) Moving `FirstGenerationStartedUtc` and `FirstSequence` to the retry.** Right, and necessary. The fold
numbers turns in the order receipts are read, which is `(FirstGenerationStartedUtc, FirstSequence, PromptId)`
in both `ApiModeReceiptStore.Read` and `ApiModeVerdictReceipts.Read`. Without the move, "gw-1 refused at 1, p2
delivered at 2 and its turn t1 done, gw-1 retried and delivered at 6 and its turn t2 started" reads gw-1 first,
so the fold's turn order is `[t2, t1]`, the latest turn is t1 (done), and the row says "Idle after turn 2"
while t2 runs. With the move it reads `[p2, gw-1]`, order `[t1, t2]`, "Working - turn 2". The new
`Read_ARetryDeliveredAfterAnotherPrompt_IsReadAfterIt` and `Stamp_ARetryDeliveredAfterAnotherPromptsTurn_IsTheLatestTurn`
pin exactly this. It cannot double-count: the fold dedupes turns by id (`seenTurns`), and one receipt carries
one turn id. It cannot misorder a turn's own receipts: a retry's delivery always precedes the turn start that
names it, and a prompt Pi queued into a running turn joins that turn after its own delivery, so the first
receipt read for any turn is still one delivered before or during that turn. When the retry's first event is a
`PromptQueued`, the first position moves to that, and the later `PromptDelivered` only touches the last
position - the same shape a fresh prompt has.

**(3) The changed #4083 test.** The new expectation is right under the event model, and it hides no regression
of what #4083 fixed. #4083's finding 2 was that a ruling started from the store logged the very ending the
stored receipt was built from as a "second ending"; its fix was to consume the first same-kind ending silently
and log any other. The old test `Rule_AStoredDeliveryThenAReplayedRefusal_IsLoggedAsASecondEnding` asserted
that a refusal handed to a store-started ruling holding `Delivered` is logged as a second ending. Under the
rule this change adopts, that refusal has two possible readings and the ruling cannot tell them apart: the
refusal the stored delivery superseded (a replay, older than the delivery, the common case after a reset) or a
genuine second ending after the delivery (a Director defect). The change reads it as the former until the
delivery's own event comes back, then as the latter. The store sees every event with its position and logs the
genuine defect either way ("REFUSED, the receipt is already Delivered ... the delivery stands"), so the one
honest line that #4083 wanted kept still exists; it has moved from the ruling to the store for this one case.
What the ruling's counter no longer covers, and the Tech Lead should know: a defective refusal arriving at a
store-started reader before the stored delivery's replay is not counted by `SecondEndingsLogged`. The wire
output is right in both readings (nothing is sent, `Delivered` stands). Acceptable.

**(4) The "known limit".** Not acceptable as recorded, because the record is wrong about its own bound. The
Developer's answer says: "an earlier attempt's event may show for a moment before the stored refusal replaces
it again, and the last item that reader is handed is always the stored one." The first half is true (note 2
traces it). The second half is false in the case of finding 1, where the reader never shows the true state
again for the life of the stream. Finding 1 has the concrete case. The fix for finding 1 removes the flicker
too, so there is no limit left to record once it is in.

**(5) Thread safety and the transaction.** Holds. `Supersede` is a private step inside the `apply` closure that
`Write` runs under `_gate`, inside the one transaction per change, and on a lost race the closure re-reads the
row and decides again from what it finds. The ruling is one instance per reader on one thread, as before; the
new dictionary adds one entry per prompt per reader. `ApiModeVerdictReceipts` reads outside the lock, but a
supersede is one row in one transaction, so it sees the row before or after, never between. See note 4 for a
pre-existing race this change neither adds to nor fixes.

**(6) Logging, and the entity.** Logging meets the project rule: every supersede is one `SUPERSEDE` line naming
the refusal it replaced plus the `Write` line saying what was done; every ending refused after `Delivered` is a
line; the ruling's guard logs each older event it refuses, and that path only fires on a real anomaly (the
buffer never hands a duplicate). The entity change is comment-only: three lines, all `///`, no property, no
migration, no snapshot. Confirmed from the diff.

## 3. Findings

### Finding 1 (medium). A stream started from the stored receipts swallows a retry that arrives live, so it can show a wrong refusal reason, or "Refused" for a prompt that is queued, for the life of the stream

**Where.** `ApiModeLiveRuling.Supersedes`, the `_endingsFromStore` branch, reached by the stream after any
reset (`StartRuling` with an incomplete replay, or `RebuildAsync`).

**What happens.** A ruling built from the store holds `gw-1` as `Refused` (reason A) and puts `gw-1` in
`_endingsFromStore`. It then treats the next `PromptQueued` for `gw-1` as a replay of the first attempt's
queueing (it returns false and changes nothing), and the next `PromptRefused` for `gw-1` as the stored ending
coming back (same ending: it removes `gw-1` from the set, returns false, and the lambda returns before writing
the new reason). That is correct when those events ARE replays, which is the case the Developer reasoned from:
the store is at or ahead of every REPLAYED event. It is wrong when they are the retry's events arriving LIVE
after the store was read, which the ruling cannot distinguish without a position. The retry's `Queued` is
dropped, and the retry's refusal is consumed as if it were the old one, leaving reason A on screen while the
store holds reason B. Nothing later corrects it: the set entry is gone, the words did not change, and no item
is sent. A poll is unaffected (it rebuilds from the store every request); only a long-lived stream is wrong,
and a reload fixes it.

**Concrete failure case.** A long session has pushed the buffer past its bound (2,000 events or 2,000,000
characters of tool output), so any attach has an incomplete replay and starts from the store. The user presses
a prompt while the Director is restarting Pi: `PromptRefused gw-1` "Pi is not running: the process exited"
(sequence 40). The user reloads the Chat: the stream opens with the stored receipt "Refused: Pi is not
running...". The user presses again (the Gateway's re-press, same id): `PromptQueued gw-1 turnRunning` (41)
- the ruling swallows it, the screen still says "Refused" while the row's queue label says "1 prompt waiting".
Pi then refuses the retry itself: `PromptRefused gw-1` "Agent is already processing. Specify
streamingBehavior..." (42), `refusedBy: agent` - the ruling consumes it as the stored ending, writes nothing,
sends nothing. The stream shows "Refused: Pi is not running" indefinitely; the receipts route and the next
reload show "Refused: Agent is already processing". Two readers of one Gateway disagree on why a prompt was
refused, which is the display defect finding 1 of the phase review was raised to remove. The same shape
happens after a Gateway restart (empty buffer, every attach starts from the store) whenever the retry's events
were acknowledged to the Director separately from the first attempt's and so are not re-sent together with
them. Trace with the test helpers: start a ruling from `{ gw-1, Refused, RefusalReason = PiNotRunning }`, then
`Rule(Queued(41, "gw-1"))` and `Rule(Refused(42, "gw-1", RefusalBusy, Agent))`: both answer no item, and the
receipt's `RefusalReason` is still `PiNotRunning`. The branch has no test for this sequence; the one
store-started retry test (`Rule_AStoredRefusalThenTheRetrysDelivery_TheReceiptReadsDelivered`) covers only the
`Delivered` retry, which the code special-cases.

**What makes it right (the Developer decides).** Give the ruling the position the stored ending was built from,
and decide by position instead of by kind. The store has it on every row (`LastGeneration`, `LastSequence`),
the #4083 review's finding 2 already proposed it ("the ruling can keep, per receipt, the sequence that ended
it - the store has the position"), and the stream knows the held generation of every event it rules. With it:
an event at or below the stored position is a replay and changes nothing, unlogged; an event above it is new
and is ruled by the ordinary rule (a `Refused` receipt is superseded, a `Delivered` one is final and logs the
second ending). `_endingsFromStore` and its same-kind guess then go away, and the "known limit" with them.
Carry the position on an internal read path rather than on the public `ApiModeReceiptDto`, which clients
receive. A weaker alternative is the event time: the store keeps `RefusedAtUtc` and `QueuedAtUtc` from the
event's own `At`, so an event with a later `At` than the stored ending's is newer; note that the test helpers
stamp every event with one `T0`, so those tests would need distinct times. Either way the new test to add is
the one in the failure case above, asserting the retry's reason is shown.

**Developer's answer (accepted, fixed with the proposed fix).** The live ruling now decides by position, never by
"an ending is already held". The store hands a store-started ruling each receipt with the position of the last event
it applied to the row (`ApiModeReceiptStore.ReadWithPositions`, an internal read path answering `ApiModeStoredReceipt`;
the public `ApiModeReceiptDto` is unchanged). A held event now carries its generation's start
(`ApiModeHeldEvent.GenerationStartedUtc`, set by the buffer from the batch), so the ruling compares an event to the
stored position with the store's own rule, `ApiModeEventPosition.IsAfter`: at or before the stored position is a
replay and changes nothing, unlogged; after it is ruled by the ordinary rule (a `Refused` receipt is superseded and
logged, `Delivered` is final and a second ending is logged). The stream and the poll hand every event as a held event
(`Rule(ApiModeHeldEvent)`); a receipt event for a stored prompt handed without a position throws rather than guess.
`_endingsFromStore`, its same-kind guess and the recorded known limit are removed together. New tests: the concrete
case above, as a ruling test (`Rule_AStoredRefusalThenTheRetryQueuedAndRefusedLive_ShowsTheRetrysReason`) and end to
end through the stream against the store the receipts route reads
(`Stream_StartedFromAStoredRefusal_ARetryRefusedLiveShowsTheReasonTheReceiptsRouteAnswers`); a replay of the stored
refusal's own events, up to and including its position, is quiet and counts no supersede
(`Rule_TheEventsAStoredRefusalWasBuiltFromReplayed_AreQuietAndLogNoSupersede`); a retry in a later generation is the
retry whatever its sequence; a missing position throws; and the store's positioned read carries the last applied
position, unmoved by an ending it refused.

## 4. Notes (none blocks the merge)

**Note 2 (low). The flicker the Developer recorded writes a false `SUPERSEDE` line.** Stored `Refused` (reason
B, the second refusal), replay `Queued(1)`, `Refused(2, A)`, `Queued(4)`, `Refused(6, B)`: the first refusal is
consumed as the stored ending, so `Queued(4)` finds the prompt no longer in `_endingsFromStore`, logs
`SUPERSEDE, the prompt was refused and PromptQueued is its retry`, counts it in `SupersedesLogged`, and sends
"Queued: a turn is running"; `Refused(6)` then restores reason B. The screen is right within two frames, but
the log claims a retry that did not happen at that moment, on a routine reset path - the same shape of log
noise #4083's finding 2 removed. The fix for finding 1 removes this too.

**Developer's answer.** Removed with finding 1: every event of that replay is at or before the stored position, so
none is ruled and none is logged. `Rule_TheEventsAStoredRefusalWasBuiltFromReplayed_AreQuietAndLogNoSupersede` is
this exact sequence.

**Note 3 (low). The ruling's guard is per generation by the callers' discipline, not by its own.**
`_receiptSequences` compares raw sequences. It is correct today because `SessionLiveEndpoint` rebuilds the
ruling on every generation change and a poll's ruling never spans two. If a future caller fed one ruling across
a Director restart, every receipt event of the new generation (sequence 1 upward) would be refused as "not
after" the old one. Worth one sentence in the class summary ("one ruling rules one generation"), or storing the
held generation beside the sequence; not a defect now.

**Developer's answer.** Documented, per the owner's ruling: the class summary of `ApiModeLiveRuling` now says one
ruling rules one generation, that the sequence guard compares sequences only, and that it relies on its callers
handing it one generation's events in order (the stream rebuilds on a generation change; a poll builds a ruling per
request). Not enforced: the plain `Rule(SessionEvent)` path carries no generation, so enforcing it would mean every
caller hands a position, which is a larger change than this note asks for.

**Note 4 (pre-existing, outside this change). Two Gateways updating one existing row have no concurrency
token.** The store's atomicity story covers two processes CREATING the same row (the key collides, the loser
re-reads). Two processes UPDATING one row - say Gateway A applying `Delivered(5)` and Gateway B applying a
later `Refused(6)` during a deploy's overlap - both read `Refused` at 2, both decide, and the later commit
wins regardless of which event is newer, so A committing last leaves `Delivered` at 5 under a newer refusal.
`Supersede` runs inside the same update, so it neither widens nor narrows this; the overlap is the one-minute
deploy window and the receipt is a single row. Recorded so it is not mistaken for a defect of this change if
it is ever seen.

**Developer's answer.** Agreed it is pre-existing and outside this pull request; no change, by the owner's ruling.
Recorded as a follow-up: give `ApiModeReceiptEntity` a concurrency token (or make the update conditional on the row's
last position), so two Gateways updating one row during a deploy's overlap cannot let an older event's commit land
last.

**Note 5.** Attribution sweep of the commit message, the pull request body and the changed files: clean.
Plain English: clean. `RecordTurnStarted` is unchanged, as the phase review required; the Director is
unchanged.

## 5. Re-review of the fix for finding 1 (head commit `7116f56fe`)

Reviewer seat again, a different session from the Developer, 17 October 2026 on devlinux, reading only (no build, no
test, by instruction). Read: the fix commit whole (eleven files), `ApiModeLiveRuling.cs`, `ApiModeReceiptStore.cs`,
`ApiModeStoredReceipt.cs`, `ApiModeEventPosition.cs`, `IApiModeEventFeed.cs`, `ApiModeEventBuffer.cs` (`Apply`,
`Read`, `Subscribe`, `ReplayOf`), `ApiModeEventIngest.cs` (where a batch's generation start comes from),
`SessionLiveEndpoint.cs` (the poll, the stream loop, `RebuildAsync`, `StartRuling`, `StoredReceipts`), the entity and
its one migration, and every changed test with its helpers.

**Verdict: the review now holds. Finding 1 is fixed, and the fix introduces no defect I can find.**

**Finding 1, the concrete case.** A ruling built from the store holds `gw-1` Refused with the position of the last
event the store applied to that row, (generation A, 40). `PromptQueued` at 41 arrives as a held event with its
position: 41 is after 40 in the same generation, the receipt is Refused, so `Supersedes` logs one supersede, clears the
refusal, the state becomes Queued and the item "Queued: a turn is running" is sent. `PromptRefused` at 42 is after 40
and after 41, the receipt is Queued, so the retry's reason is written and "Refused: Agent is already processing..." is
sent. The screen and the receipts route now agree, which is what finding 1 asked for.
`Rule_AStoredRefusalThenTheRetryQueuedAndRefusedLive_ShowsTheRetrysReason` is this exact sequence, asserting both items
and the single supersede.

**Every replay path hands a position.** There are three production callers of the ruling and all three hand a held
event: the poll (`ReadItems`, both the ruling it starts with and the one it rebuilds mid-page on a missed-events
reset), the stream loop (after `StartRuling`, after a `RebuildAsync` for a new generation, and after a `RebuildAsync`
for missed events), and `StartRuling`'s catch-up loop, whose events are the feed's held events and whose ruling is a
fresh one with no stored positions. The held event's position is built from the same batch fields
(`Generation`, `GenerationStartedUtc`, the event's `Sequence`) the ingest hands the store, so the two sides compare like
with like, and inside one generation the comparison is by sequence alone, so the start time cannot disagree there.

- Reset with the buffer past its bound: the attach's replay is incomplete, `StartRuling` reads the positioned receipts,
  and every later event is a held event. Replays at or before the row's position are quiet; the retry's events are
  after it and are ruled. The ruling test above is this path's decision.
- Gateway restart: the buffer holds nothing, `ReplayOf` answers not complete, and the stream opens from the store.
  `Stream_StartedFromAStoredRefusal_ARetryRefusedLiveShowsTheReasonTheReceiptsRouteAnswers` is this path end to end: it
  writes the refusal at 40 into the same store the endpoint is mapped with, opens the stream, pushes the retry through
  the Gateway's own ingest (store first, then buffer), and asserts the opening frame, the two live frames with their
  cursors, and that the last frame's words equal the stored receipt worded by `ReceiptWords`. The Director's re-send
  of the old events after a Gateway restart (same generation, same sequences) is at or before the stored position and
  so is quiet; a re-send is not simulated in the test, but the quiet-replay ruling test covers the decision.
- A new generation mid-stream: `RebuildAsync` reads the positioned receipts and the loop continues with held events of
  the new generation, whose later start makes every one of them "after" a position from the old generation, whatever
  its sequence. `Rule_AStoredRefusalThenTheRetryInALaterGeneration_IsTheRetryWhateverItsSequence` pins that decision
  with the retry at sequence 3 of generation B over a refusal at 40 of generation A.

**Note 2's false supersede line is gone too.** `Rule_TheEventsAStoredRefusalWasBuiltFromReplayed_AreQuietAndLogNoSupersede`
replays the exact sequence of note 2 (Queued 1, Refused 2, Queued 4, Refused 6 against a store position of 6) and
asserts no item, no supersede and no second ending. I traced it: every event is at or before 6, so none reaches the
rule.

**The throw cannot take down a stream.** The new exception fires only when a receipt event for a prompt whose receipt
came from the store is handed without a position, which is the plain `Rule(SessionEvent)` overload. No production
code calls that overload: the three callers above all pass `ApiModeHeldEvent`. Two other ways a position could be
missing are closed before the ruling sees anything: the ingest refuses a batch whose generation start is unset before
the buffer holds it, so a held event's `Position` property always constructs; and the three position columns on the
receipt row are non-nullable in the one migration that created the table (`20261016183035_AddApiModeReceipts`), so the
positioned read cannot meet a row without them. `Rule_AReceiptEventForAStoredPromptWithoutItsPosition_Throws` pins the
contract for any future caller; it is a guard test, not a path a user can reach.

**The tests are real proofs.** Each new test arranges the stored state the finding describes, acts through the public
surface a reader uses (`Rule(ApiModeHeldEvent)`, or the HTTP stream and the ingest), and asserts the words a client
would see plus the counters that would betray a wrong path. The store test
`ReadWithPositions_EachReceipt_CarriesThePositionOfTheLastEventTheStoreApplied` asserts the positioned read answers the
same receipts in the same order as `Read`, that a retry's two events move the position to the last one applied, and
that an ending the store refused (a second ending after Delivered) leaves the position where it was. Every earlier
store-started ruling test was moved onto the positioned constructor with an explicit position, and no two-argument held
event or unpositioned stored ruling remains anywhere under `src` (searched). The end-to-end test checks the store read
the receipts route serves, worded by the one `ReceiptWords` function, rather than the route over HTTP; the route is a
thin read of that store, so this is adequate.

### New notes (none blocks the merge)

**Note 6 (low, record only). A replay of an event the store refused is ruled again by each store-started reader.**
The store does not move a row's position when it refuses an event (a second ending after Delivered, a `PromptQueued`
after Delivered), so such an event sits after the stored position and a reader started from the store rules it: for a
Delivered receipt the `PromptQueued` changes nothing silently, and a second `PromptDelivered` or `PromptRefused` is
logged as a second ending and counted in `SecondEndingsLogged`. The wire output is right in every case (the delivery
stands, nothing is sent). The only cost is that one Director defect, already logged once by the store, is logged again
by every reader that resets while the event is still held. That is a true line about a real defect, not a false one,
and the existing `Rule_AnEndingAfterTheOneAStoredReceiptWasBuiltFrom_IsLoggedAsASecondEnding` is this behaviour. Recorded
so nobody reads the repeated line as a new defect.

**Note 7.** Attribution sweep of the fix commit (message and every changed file): clean. Plain English: clean. The
public `ApiModeReceiptDto` is unchanged, the entity is unchanged, and there is no new migration, as the finding asked.

**Note 8 (low, a one-paragraph correction the Developer should make before the merge). The phase 2 phase review still
records the removed behaviour.** The Developer's answer under finding 1 of `phase-2-phase-review.md` (the
`ApiModeLiveRuling` bullet) still describes the same-kind guess ("until the event a stored ending was built from comes
back, an earlier attempt's `PromptQueued` and the refusal a stored delivery superseded are taken as that replay") and
still ends with the known limit this fix removed ("the stored receipts carry no positions ... the last item that reader
is handed is always the stored one"). Both sentences are now false about the code on this branch: the stored receipts
do carry positions, and the ruling decides by them. The code and this review are right; that record is stale, and a
reader of the mission's record would be told a limit exists that does not. It does not change the verdict on the code.
