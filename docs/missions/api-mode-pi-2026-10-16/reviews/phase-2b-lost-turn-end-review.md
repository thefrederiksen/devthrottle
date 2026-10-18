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

## Re-review

Pull request #4179 re-reviewed at its head `7f6ecf8b6` (three commits on `origin/main` at `8994f5e1a`, which holds
pull request #4178, a queued receipt's place in line). Reviewer seat, session 868ed433, opened by the Tech Lead,
running a different agent family from the Developer. Written 17 October 2026 on devlinux.

**Verdict: hold, one finding (low in reach, but it is the store and the live ruling disagreeing, and the fix is
small).** The rebase is sound: both behaviours are whole in the store and in the live ruling, the three readers agree,
and replacing `_linkedHere` with `_lastPositions` kept the forward-only positions. The new path for a receipt with no
turn id is right in the case it was asked for (the turn's start and end both in the hole). It goes wrong in one
sequence: when the real `TurnStarted` arrives late, older than the lost ruling, the receipt is re-opened under its turn,
and the real `TurnEnded` that follows it is then dropped by the store's forward-only rule while the live ruling applies
it. I reproduced it with one test on each side (section 3 below): the store's fails, the ruling's passes.

### 1. Scope: what I read, what I ran

**Read, in full, on the branch head:** `git diff origin/main...HEAD` (twelve files); `git range-diff` from the approved
head `f5a95005c` to this head, for the four production files; `ApiModeLostTurnEnd.cs`, `ApiModeLiveRuling.cs`,
`ApiModeReceiptStore.cs` and `ApiModeEventIngest.cs` as they stand; the six changed test files' diffs with their
helpers. **Read on `origin/main`:** `ApiModeEventPosition.cs` (`IsLaterGenerationThan`, `IsAfter`),
`Api/SessionLiveEndpoint.cs` (`StartRuling`, the poll route's reset, the stream's `RebuildAsync`, `StoredReceipts`,
`ReceiptItems`), `Api/SessionReceiptsEndpoint.cs` (`Read`), `ApiModeSessionFold.cs` (the turn count and the outcome
switch), and the #4178 commit `7792ff019`.

**Ran, on devlinux, Debug, one run, after checking no other test host was up:** `CcDirector.Gateway.UnitTests` filtered
to the six classes the change touches (`ApiModeLiveRulingTests`, `ApiModeReceiptStoreTests`, `ApiModeEventIngestTests`,
`SessionReceiptsEndpointTests`, `SessionLiveEndpointTests`, `ApiModeSessionFoldTests`), with the two reproduction tests
of section 3 appended for the run only. **275 tests, 274 passed, 1 failed**: the failure is the store reproduction, as
predicted; every test the Developer wrote passed. The two test files were restored from git afterwards; the tree is clean.

**Could not reach:** the same as the first review (a Director really pushing events, PostgreSQL, Windows).

### 2. The rebase, checked

**Place in line (#4178) is whole in the live ruling.** `ReceiptChange` still ends in `WaitingIfMoved(e.Sequence, receipt)`;
`TurnStart` and `TurnEnd` both still end in `WaitingIfMoved(sequence, null)`; the new `LostTurnEnds` ends in it too
whenever it changed a receipt, because the ruled rows moved on to the proof's generation, which can take an older
generation's queue out of the count, exactly as the store's rows then read. The proof event is handed to `LostTurnEnds`
first and to `RuleEvent` second, so the recount runs twice on one event; the changed-words check makes the second pass
send nothing new. The store side needs nothing: the place is derived at read time, never stored.

**The lost ending is whole in both.** Store: `RecordTurnOverProven` runs from the ingest before the event's own change,
selects every Delivered row with no outcome (with or without a turn id) and asks `ApiModeLostTurnEnd.Applies`;
`EndTurnOnce` ends the open rows under the forward-only rule and, only when none is open, replaces the lost ones
whatever the position without moving the position back. Ruling: `LostTurnEnds` asks the same `Applies` against
`_lastPositions`; `TurnEnd` targets the open receipts of the turn, else the lost ones, and touches forward. The
real-event overrides are pinned on both sides, including an older-generation `TurnEnded` after the ruling.

**`_lastPositions` for `_linkedHere` is the right replacement, and forward-only positions survived it.** I traced
every place the ruling's `_lastPositions` moves against the store's row position: set from the stored position at
construction; `Touch` in `ReceiptChange` (behind the stored-position and sequence guards, as before); `Touch` in
`TurnStart`'s normal path (unguarded, as on main; the store's `IsNewer` guard means the two can differ only within one
generation, and both the lost rule and the place in line compare generations only, so it is harmless); `Touch` to the
proof's position in `LostTurnEnds` (the store does the same); `TouchForward` in `TurnEnd` and in the new replace path
(the store's `IsNewer`-guarded `Touch`). `Rule_AStoredReceiptTouchedInThisGeneration_IsNotRuledLostByALaterProof` pins
the one case `_linkedHere` used to carry. `RecordQueued`, `RecordDelivered`, `RecordRefused`, `RecordTurnStarted`'s normal
path and the ruling's `ReceiptChange` are untouched.

**The endpoint, the live stream and the stream's opening from the store agree.** All three read `ReadWithPositions`,
call `PlaceInLine` over the same list, and word each receipt through `ReceiptWords` and `ReceiptTone`. The Developer's
`Read_ATurnWhoseEndWasLost_GivesTheWordsTheLiveStreamGives` checks the served receipt, the live item and a reader
rebuilt from the store against each other for a lost ending; `SessionLiveEndpointTests`' changed expectation (p1 lost in
the stored receipts after the reset, then only the connection line) is right for its events.

**The new no-turn-id path, in the case it was asked for.** A Delivered receipt with no turn id and no outcome is ruled
lost by the same three proofs in a later generation, except by a `TurnStarted` that names the prompt, which is its own
turn starting; the ingest and the ruling both ask the proof first and link second. The store's query no longer requires
a turn id. Pinned in the store, the ruling and the ingest (`Accept_AHoleThatSwallowedTheTurnsStartAndEnd_...`,
`Accept_ANewGenerationWhoseFirstTurnNamesTheDeliveredPrompt_...`). Correct.

### 3. Finding

#### Finding 1 (low). A late real `TurnStarted` re-opens a lost turn with no turn id, and the store then drops the real `TurnEnded` the live ruling applies

**Where:** `ApiModeReceiptStore.RecordTurnStarted`, the new block `if (row.TurnId is null && row.TurnOutcome == lost)`,
which sets the turn id and calls `EndTurn(row, null, null, null)` whatever the event's position; its twin in
`ApiModeLiveRuling.TurnStart`. Then `EndTurnOnce`, whose open-rows branch keeps `if (!IsNewer(position, row)) continue;`.

**The sequence.** p1 is Delivered at A#1 with no turn id. Generation B's `ConnectionReady` at B#1 rules it lost; the row's
position is now B#1. The `TurnStarted` of t1 naming p1 arrives late, at A#2 (older than B#1). The replace path links p1
to t1, clears the lost ending and, rightly, leaves the position at B#1. The real `TurnEnded` of t1 then arrives, at A#3.

- Store: `EndTurnOnce` finds p1 open under t1; A#3 is not newer than B#1; `continue`. Changed: 0. p1 reads a bare
  "Delivered" under t1, open, for the rest of generation B; no later proof in generation B can rule it again, because
  its position is already B's.
- Live ruling: `TurnEnd` finds p1 open under t1 and applies the outcome with no position guard. p1 reads "Delivered - the
  turn failed: the model errored".

So the two readers disagree, and the store has dropped a real ending in the one path the Tech Lead's "the real ending
replaces lost at any position" rule exists for. Had the lost ending been left in place, the same `TurnEnded` would have
taken the else branch (no open rows, replace the lost ones) and both readers would show the real outcome.

**Reproduced.** Two tests, run once each against the branch head and then removed (they are not committed; the
Developer should add them under their own names):

    // ApiModeReceiptStoreTests - FAILS on the branch: Expected 1, Actual 0
    var store = StoreWithADeliveredPromptAndNoTurn();
    store.RecordTurnOverProven(Director, Sid, ApiModeLostTurnEnd.Continuing.None, "ConnectionReady", AfterRestart(1));
    store.RecordTurnStarted(Director, Sid, "t1", new[] { "p1" }, At(2));
    var changed = store.RecordTurnEnded(Director, Sid, "t1", "failed", "the model errored", At(3), T0.AddSeconds(4));
    Assert.Equal(1, changed);
    Assert.Equal("Delivered - the turn failed: the model errored", ApiModeLiveRuling.ReceiptWords(Only(store)));

    // ApiModeLiveRulingTests - PASSES on the branch, which is the disagreement
    var ruling = RulingWithADeliveredPromptAndNoTurn();
    ruling.Rule(HeldB(Ready(1)));
    ruling.Rule(HeldA(Started(4, "t1", "p1")));
    var items = ruling.Rule(HeldA(Ended(5, "t1", TurnOutcome.Failed, "the model errored", null)));
    Assert.Equal(new[] { "Delivered - the turn failed: the model errored" }, ReceiptWordsOf(items, "p1"));

**Why the re-open is wrong in principle, not only in this sequence.** By the dialect, a `TurnStarted` naming p1 that
arrives after a proof which did not name p1 is always an earlier turn arriving out of order: an acknowledged prompt moves
into the very next turn at `OnAgentStart`, and a process exit sends a `TurnStarted` plus a failed `TurnEnded` for prompts
it acknowledged and never ran before `ConnectionEnded`, so no later turn of the session can be p1's. The proof already
showed every earlier turn over. Clearing the lost ending therefore says "running" of a turn that is proven over; the
Developer's test `RecordTurnStarted_ALostReceiptWithNoTurn_TheRealStartReplacesTheLostEnding` pins that wrong reading
("Delivered", open). Low, because it needs an event to arrive after a proof that is newer than it, which the first review
traced to two Gateway processes ingesting at once during a deploy and nothing else; the same reach as the rule it breaks.

**What I recommend.** In the store's replace path and the ruling's twin: link the receipt to its turn always (so the real
`TurnEnded` can find it), but clear the lost ending only when the `TurnStarted` is newer than the receipt's last
position (`IsNewer` in the store, `IsAfter` against `_lastPositions` in the ruling: the turn is genuinely starting now);
when it is older, keep `lost`. Then the late real `TurnEnded` takes the existing no-open-rows branch in both readers and
replaces the lost ending with the real outcome, position unmoved. Turn the two reproductions above into tests under the
Developer's names (the store one goes green with the fix; the ruling one stays green), and change the two existing
"replaces the lost ending" tests on the older position to expect `lost` kept with the turn id set. One small side effect
to accept or address: in the ruling, `ReceiptIfChanged` compares words only, so the relinked receipt's item (now with a
turn id) is not re-sent to the client; harmless, or compare the turn id too. A relinked receipt also gains a turn id
the fold then counts, which narrows the count gap the Developer named under note 1.

#### Developer's answer

**Accepted, with the Reviewer's fix, in the store and the live ruling alike.** A real `TurnStarted` that names a
receipt ruled lost with no turn id now always links the receipt to its turn, so the real `TurnEnded` can find it. It
clears the lost ending only when it is newer than the receipt's last position: `IsNewer` against the row in
`ApiModeReceiptStore.RecordTurnStarted`, and a new `IsNewerThanLast` against `_lastPositions` in
`ApiModeLiveRuling.TurnStart` (which `TouchForward` now uses too, so the two ask the same question one way). When the
start is older, the receipt keeps `lost` with the turn id set and its position unmoved; the late real `TurnEnded` then
finds no open receipt under the turn and takes the existing replace branch in both readers. The note in
`ApiModeLostTurnEnd` says the same.

**The side effect in the ruling, addressed rather than accepted.** The store counts the relink as a change, so the
ruling now re-sends the relinked receipt's item (same words, turn id now set) instead of leaving the client without the
turn id. `ReceiptIfChanged` is unchanged; the item is added for the relinked receipt only.

**Tests.** The two reproductions are in under my names, the same event sequence on each side:
`ApiModeReceiptStoreTests.RecordTurnEnded_AfterAnOlderRealStartLinkedALostReceiptWithNoTurn_TheRealEndReplacesTheLostEnding`
(failed before the fix, passes now) and
`ApiModeLiveRulingTests.Rule_NoTurnRuledLostThenItsOlderRealTurnStartedAndEnded_ShowsTheRealOutcome` (passed before and
after); both end with "Delivered - the turn failed: the model errored". The two older-position tests are flipped and
renamed to `RecordTurnStarted_ALostReceiptWithNoTurnAndAnOlderRealStart_LinksItAndKeepsTheLostEnding` and
`Rule_NoTurnRuledLostThenItsOlderRealTurnStarted_LinksItAndKeepsTheLostEnding`: lost kept, turn id `t1`, the store's
position still B#4. A new store test, `RecordTurnStarted_ALostReceiptWithNoTurnAndANewerRealStart_ReplacesTheLostEnding`,
pins the newer case on the store side; the ruling's `Rule_AStoredLostEndingWithNoTurnThenItsRealTurnStarted_ReplacesIt`
already pinned it there and still passes.

**Ran, on devlinux, Debug, one class at a time, no other test host up:** `ApiModeLiveRulingTests` 96 passed,
`ApiModeReceiptStoreTests` 60 passed, `SessionReceiptsEndpointTests` 13 passed, `SessionLiveEndpointTests` 54 passed,
and as a cross-check `ApiModeEventIngestTests` 28 passed. None failed.

### 4. Notes (none blocks the merge once finding 1 is fixed)

1. **A lost `PromptDelivered` is a different hole, outside item 7.** In the ingest test
   `Accept_AHoleThatSwallowedTheTurnsEnd_TheNextTurnsStartRulesItLost`, p2's delivery is in generation B so all is well;
   but when the hole swallows a prompt's `PromptDelivered` and the next generation's `TurnStarted` names it, the store
   logs "is Queued, not Delivered; it is not linked" and the receipt reads Queued for ever while its turn runs. Not this
   change's concern; a line for the mission's list of what remains.
2. **The fold.** Both new fold tests are right, and I agree with the Developer that the turn count for a turnless lost
   receipt is the Tech Lead's call, not a change to make here.
3. **The first review's four notes** are all answered: note 1 pinned, note 2 done (and checked above), note 3 and note 4
   stand as recorded.

### 5. Verdict

**Hold** for finding 1: keep the lost ending when the real `TurnStarted` that relinks a turnless receipt is older than
the lost ruling, in the store and in the ruling, with the two reproductions as tests. Everything else in the rebase and
the new path is approved as it stands; after that one change the pull request is complete for item 7 as the Tech Lead
ruled it, including the first review's finding 1.

## Check of the re-review fix

Pull request #4179 checked at its head `44ddcf7a7` (one commit on the re-review head `6cafc2c7f`). Reviewer seat,
session 868ed433, 17 October 2026 on devlinux.

**Verdict: approve.** Finding 1 is fixed as recommended, in the store and the live ruling alike, and the one side effect
I named is addressed rather than accepted.

**Read, in full:** the diff from `6cafc2c7f` to `44ddcf7a7` (three production files, two test files, the Developer's
answer in this file). **Ran, Debug, one run, after checking no other test host was up:** `ApiModeLiveRulingTests` and
`ApiModeReceiptStoreTests` in one filter: **156 tests, 156 passed, none failed.**

**The store** (`RecordTurnStarted`): a lost row with no turn id is always linked to the turn; the lost ending is cleared
and the row touched only when the event is newer than the row's last position; an older one keeps `lost`, moves only
`UpdatedAtUtc`, and still counts as a change. `EndTurnOnce` is untouched, so the late real `TurnEnded` now finds no open
row under the turn and takes the existing replace branch. `RecordTurnEnded_AfterAnOlderRealStartLinkedALostReceiptWithNoTurn_TheRealEndReplacesTheLostEnding`
is my store reproduction under the Developer's name, and it passes. The flipped test keeps `lost` with turn id `t1` and
the position still at B#4; the new newer-case test pins the clear.

**The live ruling** (`TurnStart`): the same question is asked of `_lastPositions` through the new `IsNewerThanLast`,
which `TouchForward` now also uses (I checked it is the same predicate the old inline condition expressed: without a
position or without a last position it counts as newer, as the #4178 convention has it). A newer start clears and
touches; an older start links, keeps `lost`, does not touch, and its receipt item is re-sent with the turn id set, so a
client is not left holding an item without it. `TurnEnd` is untouched and takes the lost-receipts branch. My ruling
reproduction is in under the Developer's name and passes, as does the flipped older-position test.

**Both readers now agree on the sequence of finding 1**: after the late start and the late end, the store's row and the
ruling's receipt both read "Delivered - the turn failed: the model errored", with the position unmoved. The note in
`ApiModeLostTurnEnd` states the rule as now implemented. Nothing else in the pull request changed.

**Approve.** The pull request is complete for item 7 as the Tech Lead ruled it, including the first review's finding 1
and the re-review's finding 1.
