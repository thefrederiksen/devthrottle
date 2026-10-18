# Phase 2 follow-on review: a turn whose end was lost gets an explicit ending (pull request #4179)

Mission: API mode for agent wrappers, proven on Pi (issue #4070). Phase 2 follow-on, item 7. Pull request
#4179, "API mode phase 2 follow-on: a turn whose end was lost gets an explicit ending", branch
`api-mode-lost-turn-end`, reviewed at its head commit `28e2a2656` (one commit on base `3260adb7d`; `origin/main`
had moved on to `c7ec59930` by the time of the review, and nothing in those two later commits touches API
mode). Reviewer seat, session a1a4f53c, opened by the Tech Lead, running a different agent family from the
Developer. Written 17 October 2026 on devlinux.

**Verdict: approve.** The change does what the Tech Lead ruled, and no more: the outcome word is `lost` on the
existing `TurnOutcome` field, the words and the neutral tone are the ruled ones, nothing new goes on the wire,
and the ending is ruled only when a later-generation event proves the turn over, never because a generation
began. I checked each of the three proof events against the Director's own rules in `PiDialect` and
`ApiModeSessionHost` on `origin/main` and against the pusher's hole rule on pull request #4090's branch, and
each one holds: in the Director's own stream every one of them is preceded by the earlier turn's `TurnEnded`,
so a `TurnEnded` the Gateway does not have by then was in the hole. The real ending replaces `lost` at any
position in both the store and the live ruling, the forward-only rule is untouched for every other event, the
receipts read and the live stream word the receipt through the one static function, and the changed stream
expectation is right. One finding, low, which does not hold the merge: the gap the Developer names (a prompt
whose `TurnStarted` also fell in the hole) is a turn whose end was lost in item 7's own words, the same three
events prove it over, and it should be a follow-on item rather than left as "out of scope". Four notes.

## 1. Scope: what I read, what I ran, what I could not reach

**Read, in full, on the branch:** the diff against the base (ten files: four production, one contract, five
test); `ApiModeLostTurnEnd.cs` (new), `ApiModeLiveRuling.cs`, `ApiModeReceiptStore.cs` and
`ApiModeEventIngest.cs` as they stand after the change; `ApiModeEventPosition.cs` (`IsLaterGenerationThan`,
`IsAfter`); `SessionLiveEndpoint.cs` (the poll route, the stream loop's generation change and `RebuildAsync`,
`StartRuling`); `ApiModeSessionFold.cs` (the other reader of `TurnOutcome`); all five changed test files with
their helpers in `ApiModeEventTestData`; observation C of `phase-2-phase-review.md`.

**Read on `origin/main`, the Director side:** `PiDialect.cs` (`OnState`, which sends `ConnectionReady` once per
dialect; `OnAgentStart`, which starts nothing while `_turn` is set; `OnProcessExited`, which ends a running
turn `failed` and then sends `ConnectionEnded`; `EndTurn`, the only place `_turn` is cleared, which sends the
`TurnEnded`); `ApiModeSessionHost.cs` (`StartConnection` makes a new dialect per Pi process with the one
shared `SessionEventStream.Numbering`; `OnProcessEndedLocked` calls `OnProcessExited` and only then
`StartConnection` for a restart); `SessionEventStream.cs` (one generation per session per Director process;
turn ids from the shared numbering, so they continue across Pi restarts and start again only with a Director
process).

**Read on pull request #4090's branch `apimode/p4-event-push`, which is still OPEN and not on main:**
`ApiModeEventPusher.cs`, the class summary and `Publish`: a hole re-labels what follows as a new generation
"started one millisecond after the one it continues"; the session's own stream keeps its generation and its
Pi process keeps running; a reconnect of the hub tunnel is NOT a new generation (`NewConnection` and the first
answered Hello resend what is held under the same label).

**Ran, on devlinux, Debug, one class per run, two runs, each after checking no other test host was up:**

- Run 1, a deliberate break: `ApiModeLiveRuling.cs` reverted to its base version (everything else on the
  branch), `ApiModeLiveRulingTests`: **7 failed, 49 passed, 56 total.** The file was restored from git
  afterwards. By reading, the seven are the three cases of `Rule_AProofInALaterGeneration_RulesTheStoredOpenTurnLost`,
  `Rule_AProofWithoutItsPositionWhileAStoredTurnIsOpen_Throws`, `Rule_AStoredLostEndingThenTheRealTurnEnded_ReplacesIt`,
  `Rule_ADifferentTurnUnderTheSameId_EndsItsOwnReceiptAndLeavesTheLostEnding` and the new `lost` row of
  `ReceiptWords_EachState_IsTheEventModelsWords`; the other three new cases assert that nothing happens, or
  that the real ending applies, which the base also does. The summary line is what I captured; the per-test
  names are inferred from reading, not from the output.
- Run 2, the branch as it stands, `ApiModeReceiptStoreTests`: 50 tests ran and no failed-test line was
  printed at normal verbosity. I did not capture the exit code, so take the Developer's "50 passed" as the
  stronger statement and mine as consistent with it.

**Could not reach:** a Director really pushing events (the push is #4090, open; on main
`SessionManager.SessionEventSink` is still the logging sink), so every statement about generations and holes
is from reading #4090's branch, not from a run; a real PostgreSQL; Windows; the three other test classes the
Developer ran (the two-run allowance was spent on the two above).

## 2. The Tech Lead's rulings, one by one

**`lost`, the words, the tone, no new wire field.** `ApiModeLostTurnEnd.Outcome` is `"lost"`;
`ApiModeLiveRuling.LostWords` is "Delivered - the turn's end was lost when the Director reconnected";
`ReceiptTone` answers `neutral` for it. `ApiModeReceiptDto` gains no member; its `TurnOutcome` and `Words`
summaries list the new value. Nothing on either side parses the word back into the `TurnOutcome` enumeration
(no `Enum.Parse<TurnOutcome>` anywhere in Gateway or Core), and no TypeScript client branches on
`turnOutcome` (none in `packages` or `apps`). Holds.

**Only on proof, never because a generation began.** `ApiModeLostTurnEnd.Applies` requires all of: Delivered,
a turn id, a turn id other than the proof's own, no outcome, and `proof.IsLaterGenerationThan(lastGeneration,
lastGenerationStartedUtc)`. `Proves` answers true only for `TurnStarted`, `ConnectionReady` and
`ConnectionEnded`. A reply's text, a tool card and the same turn's own events rule nothing; the two "same turn
continues in generation B" tests (store, live ruling, ingest, endpoint) pin that. Holds.

**A real `TurnEnded` always replaces `lost`, whatever its position.** Store, `EndTurnOnce`: when no row of the
turn is open, every row ruled `lost` takes the real outcome, reason and end time; `Touch` only if the position
is newer, else only `UpdatedAtUtc` moves. Live ruling, `TurnEnd`: the targets are the open receipts of the
turn, or, when there are none, the `lost` ones; no position guard stands in the way. Pinned on both sides,
including an older-generation `TurnEnded` after the ruling (`RecordTurnEnded_TheRealEndingOlderThanTheLostRuling_ReplacesIt`,
`Rule_TheRealTurnEndedAfterTheLostEnding_ReplacesIt`). Holds.

**The store and the live ruling apply it under the same condition; the endpoint and the stream give the same
words.** Both call `ApiModeLostTurnEnd.Applies`. The store compares against the row's last applied position;
the ruling compares against the position it took from the store, with `_linkedHere` standing in for the
`Touch` the store would have done at a `TurnStarted` this ruling itself applied (see note 2 for how #4178
changes that). The words come from the one static `ReceiptWords`. `SessionReceiptsEndpointTests.Read_ATurnWhoseEndWasLost_GivesTheWordsTheLiveStreamGives`
checks the served receipt, the live item and a reader rebuilt from the store against each other. Holds.

## 3. The Developer's proof rule, judged against the Director's real behaviour

**Can a `ConnectionReady` or a different `TurnStarted` arrive in a new generation while the old turn still
runs?** No, on what is on `origin/main` and on #4090's branch.

- A reconnect of the hub tunnel is not a new generation. The pusher holds every event until a watermark
  covers it and resends what is held under the same label on the first answered Hello of a new connection.
  Only a hole (the memory bound, an oversized event, the Gateway turned off in the settings) re-labels, and
  only a Director process restart makes a new `SessionEventStream`.
- `ConnectionReady` is sent once per Pi process (`OnState` sets `_connectionReadySent`, and a dialect lives
  for one process). A new process is started only from `OnProcessEndedLocked`, after `OnProcessExited` has
  ended any running turn `failed` and sent `ConnectionEnded`. So in the Director's own stream a
  `ConnectionReady` is always preceded by the earlier turn's `TurnEnded`.
- `TurnStarted` is sent only from `OnAgentStart` when `_turn` is null, and `_turn` is set to null only in
  `EndTurn`, which sends the `TurnEnded`. So a `TurnStarted` of a different turn is always preceded by the
  earlier turn's `TurnEnded`.
- `ConnectionEnded` is sent from `OnProcessExited` after `EndTurn`, as above.

So when one of the three is the first event of a later generation and the Gateway has no `TurnEnded` for an
open turn of an earlier generation, that `TurnEnded` is in the hole. The one way a `TurnEnded` can reach the
Gateway AFTER such a proof is two Gateway processes ingesting out of order during a deploy, and that is the
case "the real ending replaces lost" answers.

**Turn ids restart per Director process: does the "receipt under the same turn id still open" exception hold
up?** Yes, and it is the right exception for the reachable world, where it is also necessary: inside one
Director process the numbering is per session stream, shared by every Pi process of the session, so a turn
id is never reused, and a `TurnStarted` of turn `t1` in a later generation can only be a replay or the very
turn that is continuing. The guard in `EndTurnOnce` (`open` first, `lost` only when nothing is open) and its
twin in `TurnEnd` therefore never fire today; they are correct if observation C's collision ever arises. One
honest addition: they would not close observation C. In that hypothetical, if the colliding generation's
first event is a `TurnStarted` of the SAME id as the open one, the same-id exception keeps the old receipt
open, the new prompt is linked beside it, and the new turn's `TurnEnded` ends both: the exact defect
observation C recorded. Observation C stays open and unaffected by this change, which is the right outcome
for item 7.

**The gap the Developer names.** See finding 1.

## 4. The rest of the brief

- **Forward-only positions for other events.** `RecordQueued`, `RecordDelivered`, `RecordRefused` and
  `RecordTurnStarted` are untouched. `RecordTurnEnded` keeps `IsNewer` for every open row; only the `lost`
  rows are reachable without it, and the position never moves backwards (`Touch` is behind `IsNewer`;
  pinned by the "position never moves back" assertion). `ReceiptChange` in the ruling is untouched.
- **The live stream's opening from the store.** `StartRuling` (incomplete replay), the poll route's
  `MissedEvents` reset and the stream's `RebuildAsync` all build from `ReadWithPositions` and hand held events
  with their positions, so a store-built ruling always has a position for the proof. The one bare `Rule(e)`
  in production (`StartRuling`, catching a fresh ruling up on a cursor) runs on a ruling with no stored
  receipts, where `LostTurnEnds` loops over nothing and cannot throw. The store feeds receipts inside the
  buffer's `fresh` callback, before the buffer hands the event on, so a ruling rebuilt at a generation change
  reads a store that has already ruled.
- **The changed expectation in `SessionLiveEndpointTests`.** The test's own `Started(2)` carries `p1` under
  `t1`; generation A ends with that turn open; generation B's first event is `ConnectionReady`. That is the
  proof, so `p1` reads lost in the stored receipts the reset is followed by, and the `ConnectionReady` then
  adds only its connection line. Correct.
- **Tests would fail without the change.** Run 1 above: seven fail with the base ruling. The ingest and
  store tests cannot be run against the base (the base store has no `RecordTurnOverProven`), so for those I
  traced by hand: `Accept_AHoleThatSwallowedTheTurnsEnd_TheNextTurnsStartRulesItLost` asserts `lost`, which
  the base never writes.
- **Naming.** Every new test is Method_Scenario_Result.
- **Clash with pull request #4178 (queue position).** Certain, textual, in `ApiModeLiveRuling.cs`: both add
  fields after `_receiptSequences`, both change the constructor, both edit the `ReceiptWords` summary, both
  change `TurnStart` (#4178 adds a `position` parameter, `Touch` and `WaitingIfMoved`; #4179 adds
  `_linkedHere`) and both rewrite `TurnEnd` (#4178 adds `position`, `Touch` and `WaitingIfMoved`; #4179
  replaces the loop with the open-then-lost targets). Whichever merges second needs a hand merge. Not a
  finding against either; see note 2 for the chance it opens.

## 5. Findings

### Finding 1 (low). A prompt whose `TurnStarted` also fell in the hole is a turn whose end was lost, and it is left with no ending

**Where:** `ApiModeLostTurnEnd.Applies` requires `turnId is not null`; `ProveTurnsOverOnce` selects only rows
with a turn id; the pull request's "Not covered" paragraph.

**The state that reaches it:** `p1` is delivered in generation A (its `PromptDelivered` arrived). Its turn's
`TurnStarted` and `TurnEnded` both fall in the hole. Generation B then carries a `TurnStarted` of another
turn, or a connection event.

**Why the same proof covers it:** by the dialect, a delivered prompt is in flight until the turn that carries
it ENDS (`_inFlight` is cleared in the turn's end path, and `_deliveredAwaitingTurn` moves into the next
turn at `OnAgentStart`; a process exit sends a `TurnStarted` plus a failed `TurnEnded` for prompts it
acknowledged and never ran). So the same three events, in a later generation, prove this prompt's turn over
too, and a `TurnStarted` that does not name the prompt proves it as well as one of a different turn id does.
The ruled words are also true of it: the delivery is certain, and the turn's end was lost.

**The harm:** such a receipt reads a bare green "Delivered" for ever, and the fold counts one turn fewer than
ran. Even the real `TurnEnded`, should it arrive, cannot attach: `RecordTurnEnded` finds no row under the turn
id. Low, because the words shown are not false, only incomplete, and because every hole case is behind #4090,
which is not merged.

**What I recommend:** a follow-on item, not a change to #4179: extend `Applies` to a Delivered receipt with no
turn id when the proof is a connection event or a `TurnStarted` that does not name the prompt, in the store
and in the ruling alike, with the same words. It is the Developer's rule applied one step further, and it is
small.

## 6. Notes (none blocks the merge)

1. **The session fold renders `lost` as a plain ending.** `ApiModeSessionFold` treats any non-null outcome as
   the turn being over, so a session whose latest turn is `lost` reads "Idle after turn N", with no
   "last turn" line and a neutral tone. That is a fair reading, and it is better than the "Working" the fold
   showed before. There is no fold test with a `lost` receipt; one row in the fold's tests would pin it.
2. **`_linkedHere` is a stand-in that #4178 makes unnecessary.** #4178 adds `_lastPositions`, the position of
   the last event that changed each receipt, kept by `Touch` in `ReceiptChange`, `TurnStart` and `TurnEnd`.
   That is exactly the store's row position, which is what `Applies` compares against. After both merge,
   `LostTurnEnds` can compare against `_lastPositions` and drop `_linkedHere` and the loop over
   `_storedPositions`, and the ruling and the store then decide from the same number rather than from two
   bookkeepings that agree by argument.
3. **A Director that never comes back is the common way a turn's end is lost, and it is not this case.** A
   Director that exits or crashes mid-turn mints a new session id on restart (observation C), so the old
   session's receipts never see a later generation and never see a proof: its open turn stays open, and the
   fold's "Not known: turn N started and its end never reached the Gateway" is what the user sees. That is
   outside item 7 as ruled (its words say "when the Director reconnected", and nothing reconnected), so it is
   not a finding here, but it is worth a line in the mission's list of what remains.
4. **One extra read per proof event.** `RecordTurnOverProven` runs a query under the store's lock for every
   `TurnStarted`, `ConnectionReady` and `ConnectionEnded`, almost always finding nothing to change, and logs
   only when it changes something. That is the right trade for a rule that must not miss, and the query is
   indexed by session; recorded so nobody is surprised by it in a profile.

## 7. Verdict

**Approve.** Finding 1 goes to a follow-on item; the pull request is complete for item 7 as the Tech Lead
ruled it.

## Developer's answer

Rebased onto main after pull request #4178 (a queued receipt's place in line). The clash in
`ApiModeLiveRuling.cs` was resolved keeping both behaviours whole: the place in line, counted again and
re-sent after every receipt event, and the lost ending with the real `TurnEnded` replacing it.

**Note 2, `_linkedHere` against `_lastPositions`: replaced.** It is plainly the same information.
`_lastPositions` is the position of the last event that changed each receipt, moved by `Touch` exactly
where the store's row moves (`ReceiptChange`, `TurnStart`, `TurnEnd`); `Applies` in the store compares
against the row's `LastGeneration`. `_linkedHere` only ever recorded one case of that - a stored receipt
linked in this ruling's generation - so it was a stand-in for the number `_lastPositions` now holds.
`LostTurnEnds` loops over the receipts in order and compares against `_lastPositions`; `_linkedHere` is
gone. Two things follow so the ruling keeps the store's bookkeeping exactly: a lost ending `Touch`es the
receipt to the proof's position (the store does), and the real ending that replaces a lost one moves the
position only forward (`TouchForward`, the store's `IsNewer` guard on `Touch`). A receipt whose last event
was handed without a position counts as of this ruling's generation, as `PlaceInLine` already counts it,
and is never ruled lost. After a lost ending the places in line are counted again (`WaitingIfMoved`),
because the rows moved to the proof's generation, as the store reads them.

**Finding 1 (low), a prompt whose `TurnStarted` also fell in the hole: done.** `ApiModeLostTurnEnd.Proves`
now hands back the proving turn whole (`Continuing`: its id and the prompts it names). `Applies` rules a
Delivered receipt with no outcome lost on the same three proofs in a later generation, with one difference
for a receipt with no turn id: a `TurnStarted` that names the prompt is its own turn starting, not a proof.
Store (`RecordTurnOverProven`, which no longer selects only rows with a turn id) and live ruling
(`LostTurnEnds`) decide through the same `Applies`. The real event that replaces it is the turn's
`TurnStarted`: in `RecordTurnStarted` and in the ruling's `TurnStart`, a receipt ruled lost with no turn is
linked whatever the event's position, the lost ending is cleared, and the position never moves back. Its
`TurnEnded` then ends it as any turn. Tests: store
`RecordTurnOverProven_NoTurnAndAConnectionEventInALaterGeneration_RulesTheTurnLost` (two cases),
`..._NoTurnAndAnotherTurnStartsInALaterGeneration_RulesTheTurnLost`,
`..._NoTurnAndTheTurnThatNamesThePromptStarts_RulesNothingAndLinksIt`,
`..._NoTurnInTheSameGeneration_RulesNothing`,
`RecordTurnStarted_ALostReceiptWithNoTurn_TheRealStartReplacesTheLostEnding`,
`RecordTurnEnded_AfterTheRealStartReplacedALostEndingWithNoTurn_EndsTheTurn`; live ruling
`Rule_NoTurnAndAProofInALaterGeneration_RulesTheTurnLost` (three proofs),
`Rule_NoTurnAndTheTurnThatNamesThePromptStarts_RulesNothingAndLinksIt`,
`Rule_NoTurnAndAProofInTheSameGeneration_RulesNothing`,
`Rule_NoTurnRuledLostThenItsRealTurnStarted_ReplacesTheLostEnding`,
`Rule_AStoredLostEndingWithNoTurnThenItsRealTurnStarted_ReplacesIt`,
`Rule_AStoredReceiptTouchedInThisGeneration_IsNotRuledLostByALaterProof` (pins note 2); ingest
`Accept_AHoleThatSwallowedTheTurnsStartAndEnd_TheNextTurnsStartRulesItLost`,
`Accept_ANewGenerationWhoseFirstTurnNamesTheDeliveredPrompt_LinksItAndRulesNothingLost`.

**Note 1, the fold when the last turn's outcome is lost: pinned, behaviour kept.**
`Fold_TheLastTurnsEndWasLost_IsIdleAfterThatTurnWithNoLastTurnLine`: kind `idle`, tone neutral,
"Idle after turn 2", no last-turn line. That is true: the turn is proven over, and how it ended is not
known, so saying nothing about its ending is the honest reading. One part is less clean, and I say so
rather than change it: `Fold_ALostEndingWhoseTurnStartWasLostToo_CountsOnlyTheTurnsWhoseStartArrived`
pins that a lost receipt with no turn id adds no turn to the count, so a session whose third turn's start
fell in the hole reads "Idle after turn 2". That number counts the turns whose start reached the Gateway.
It is not changed here because the honest alternative is not available: one turn can carry several
prompts, so counting each turnless lost receipt as a turn would over-count, and either way the number would
be a guess. It was already so before this change (the receipt read a bare Delivered). If the Tech Lead
wants it worded ("at least turn 3", or a turn count that says some were lost), that is a follow-on item.
