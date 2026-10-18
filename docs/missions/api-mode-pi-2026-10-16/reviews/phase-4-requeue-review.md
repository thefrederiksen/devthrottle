# Phase 4 review: every new generation re-announces the queue (pull request #4190)

Mission: API mode for agent wrappers, proven on Pi (issue #4070). Phase 4. Pull request #4190, "API mode phase 4:
every new generation re-announces the queue", branch `apimode/p4-requeue-on-generation`, reviewed at its head commit
`757b4e657` (one commit on `58e563ba0`, which was `origin/main` at the time of the review). Reviewer seat, opened by
the Delivery Lead, running a different agent family from the Developer. Written 17 October 2026 on devlinux, in a
worktree cut from the pull request branch.

**Verdict: approve.** The change does what the ruling asks: at the start of every new generation the Director sends
one `PromptQueued` per prompt still waiting for its receipt, oldest first, numbered 1 to N with its place, its reason
and its text, and every later event of that generation comes after them. The line is folded from the session's own
events and so matches the line the Gateway folds from the same events in every case I could construct, including the
one the pull request names (a prompt written to Pi whose acknowledgement has not come). The Gateway's two receipt
readers accept a re-announcement without moving a delivered or refused receipt back to queued, and the derived places
after a hole are the places the Director would have given with no hole. The tests wire the real stream, forwarding
sink, pusher, ingest, buffer and receipt store, run with no clock, process, sleep or port, and three of them fail
with the re-announcement switched off, which I checked by mutation. Nothing found blocks the merge. Two low findings
are about coverage and a log line, not about what the Gateway shows.

## 1. Scope: what I read, what I ran, what I could not reach

**Read, in full, on the branch:** the whole diff against `origin/main` (seven files, 583 insertions, 38 deletions);
`QueuedPrompts.cs` (new); `SessionEventStream.cs` as it stands after the change; `ApiModeEventPusher.cs` as it
stands after the change, every line, not only the diff (`Publish`, `SegmentForLocked`, `AfterHoleLocked`,
`NewSegmentLocked`, `Hold`, `TakeBatch`, `EnforceBoundLocked`, `Acknowledge`, `Remove`, `SendHeldAsync`,
`PushSessionAsync`, and the `HeldEvent` and `Segment` records with `Through` and `QueuedAfter`); the changed
paragraph of `event-model.md` and the changed summary of `PlaceInLine` in `ApiModeLiveRuling.cs`; both new test
classes. For the judgement on the line I also read, on the same branch and unchanged by it: `ApiModeSessionHost`
(`SendPromptAsync`, `TakeEventsLocked`, `WriteQueuedPromptsLocked`, `WritePromptLocked`, `WriteCommandLocked`,
`OnProcessEnded`, `RefuseQueuedLocked`) and `PiDialect` (`OnProcessExited`, `OnTurnStartOverdue`, `Refuse`) on the
Director side; `ApiModeEventIngest` (`Accept`, `FeedReceipts`, the batch checks), `ApiModeEventBuffer.Apply`,
`ApiModeEventPosition` (`IsLaterGenerationThan`, `IsAfter`, `StartPrecision`), `ApiModeReceiptStore.RecordQueued`
and its sibling writers with `Supersede`, `ApiModeLiveRuling.Rule` for the three prompt events, and
`SessionLiveEndpoint` where it reads the replay's completeness, on the Gateway side; and the ruling itself, the phase
2b review (`phase-2b-queue-position-review.md`, finding 1).

**Ran, one project at a time with no other test host on the machine, `DOTNET_USE_POLLING_FILE_WATCHER=1`, .NET 10:**

1. `CcDirector.Core.UnitTests`, filter `ApiMode`: 271 passed, 0 failed. Matches the pull request.
2. `CcDirector.Gateway.UnitTests`, filter `ApiMode`, `-p:RunWorkspaceTypecheck=false`: 436 passed, 0 failed.
   Matches the pull request.
3. The same project, filter `ApiModeEventPusher`, with one mutation: in `NewSegmentLocked` the line at the hole
   replaced by `QueuedPrompts.Empty`, so a new generation re-announces nothing (the behaviour before this change):
   3 failed, 22 passed, 25 total. That is the pull request's own claim ("3 of 25 in the pusher classes"). The
   mutation was reverted with `git checkout`; the tree is clean.

**Could not reach:** a real Pi; a real hub between a Director and a Gateway (the tests call the ingest through a
delegate, which is what the pusher is built for); Windows; the web clients (unchanged by this pull request, and the
phase 2b review found none that composes queue words of its own); the full default gate (`scripts/test-local.ps1`),
which I did not run - this is a review of two filtered projects, and the merge still needs the Developer's or Tech
Lead's gate run. The branch is one commit on the current `origin/main`, so nothing merged since needs reconciling.

## 2. The ruling, checked one by one

**The re-announcements come first, numbered 1 to N, before any later event.** `NewSegmentLocked` gives the new segment
`Offset = holeThrough - N`, builds the N re-announcements from `Reannounce(holeThrough - N + 1)` so they take the
session's own numbers just below the hole's end and go up as 1 to N, and relabels every held event above the hole so
the first one goes up as N + 1. It inserts the re-announcements at the index of the first relabelled event, or at the
end when none is held yet, so every later event of the generation, held now or published later, comes after them.
Each prompt in the line was added by its own `PromptQueued` at a sequence at or below the hole, so N never exceeds
`holeThrough` and the offset is never negative. The sent numbers cannot collide: re-announcements take 1 to N, and a
relabelled event at sequence `holeThrough + k` takes N + k. `Publish_AHoleWithPromptsQueued_TheReannouncementsComeFirstInQueueOrder`
pins the order, the numbers, the place, the reason and the text on the wire.

**Each re-announcement carries its place, its reason and its text.** `Reannounce` keeps every field of the prompt's own
last `PromptQueued` (id, text, reason, time) and replaces only `Sequence` and `Position`; `Position` is the index in
the line plus one. The Core test `Reannounce_TwoQueued_EachKeepsItsIdTextReasonAndTimeWithItsPlaceAndANewSequence`
pins it field by field.

**Nothing queued, nothing re-announced.** With an empty line the offset is `holeThrough`, as before this change, and
the first event after the hole goes up at sequence 1. `Publish_AHoleWithNothingQueued_TheNewGenerationReannouncesNothing`
pins it on the wire.

**The line is known at a hole whose events never reached the pusher.** `SessionEventStream.Publish` folds `Queued`
over every batch it publishes, whichever sink is current, and stamps each batch with the line as it stood before it.
So when the Gateway is turned off and the forwarding sink sends batches to the log, the stream's line still moves,
and the first batch after the Gateway is turned on carries the line as of after the last logged event, which is the
line at the hole. `SegmentForLocked` detects that hole (the sequence does not follow the last one handed over, or
nothing was ever handed over) and starts the new segment with exactly that line. The Core test
`Publish_EveryBatch_CarriesTheLineAsItStoodBeforeIt` pins the stamping; the first Gateway test pins the whole path.

**The three kinds of hole all re-announce.** A hole in what is handed over (`SegmentForLocked`), the memory bound
(`EnforceBoundLocked`, through `AfterHoleLocked` with the last dropped event's `QueuedAfter`), and an event too large
for any batch (`TakeBatch`, the same way). The first two have a test each; the third has none of its own, and I
traced it instead (section 4, note 4).

## 3. The four questions the brief asked

### 3.1 Is the line kept from events always the true queue?

The line is the fold of three event kinds: a `PromptQueued` joins at the end (a retry under the same id after its
refusal is moved to the end), and a `PromptDelivered` or `PromptRefused` leaves. I checked every path by which a
prompt enters or leaves the Director's own `_queue` against the event the host publishes at that moment:

- **Queued.** `SendPromptAsync` adds to `_queue` and publishes `PromptQueued` in the same locked step. Joins the line
  at the same moment.
- **Written to Pi.** `WriteQueuedPromptsLocked` removes the prompt from `_queue` and writes it; no event says so. The
  prompt stays in the line until its receipt. This is the one place the line and `_queue` disagree, and it is the
  disagreement the pull request names: the Gateway, folding the same events, keeps the receipt `Queued` until
  `PromptDelivered` arrives (the phase 2b review's note 3, the one-event lead). After a hole the re-announcement
  therefore says this prompt is 1st in line and the prompt behind it 2nd, which is exactly what the Gateway shows
  with no hole. So no place a user can see changes because of the hole, which is the ruling's goal. The Director's
  own `Position` would say one less for the prompt behind, but the Gateway never reads `Position` (phase 2b review),
  so that number reaches no screen.
- **Delivered or refused by Pi.** `PiDialect` publishes the receipt event; the host completes the waiter from that
  same event (`TakeEventsLocked`). Leaves the line at the same moment.
- **Pi's process ends and is restarted (the host keeps the session and its queue).** `OnProcessExited` in the dialect
  refuses every prompt written but not yet acknowledged (`ExitedBeforeAcknowledgingReason`), so those leave the
  line; the queued prompts stay in `_queue` and stay in the line, and are written to the new Pi in order. The host's
  own comment says it: "the overdue prompt is not sent again". The line and the queue agree.
- **Pi's process ends for good.** `RefuseQueuedLocked` publishes a `PromptRefused` for every queued prompt and clears
  `_queue`. All leave the line.
- **A prompt sent while nothing can take it.** `SendPromptAsync` publishes a refusal at once; it never entered the
  queue and never enters the line.
- **A prompt written at once (no queue) whose write fails because Pi had just ended.** `WritePromptLocked` puts it at
  the head of `_queue` with no `PromptQueued`. The line does not have it; neither does the Gateway (it was never
  told the prompt was queued), so both readers agree with each other and disagree with `_queue` in the same way they
  did before this change. If Pi is then restarted, the Director writes it first and the Gateway's first word of it is
  its `PromptDelivered`. Pre-existing and narrow (a write that fails in the instant Pi exits, followed by a restart);
  recorded as note 5 below, not a finding against this change.
- **A Director restart.** New process, new stream, empty line, new generation, no hole at sequence 1. The old
  generation's queued receipts on the Gateway keep no place (phase 2b review, finding 2, still open). Unchanged here.

So the line is always what the Gateway would have folded from the same events, and it differs from `_queue` only in
the written-but-unacknowledged window, where the Gateway already differed from `_queue` before this change. I found no
case of a wrong position, a ghost queued prompt or a delivered prompt shown queued that this change introduces. The
one ghost that remains is the one the event model names: a prompt whose own delivery or refusal fell in the hole stays
`Queued` on the Gateway in the older generation with no place. The re-announcement correctly does not include it (its
ending was folded into the line), so it is not counted ahead of anyone. Ending it is the open follow-on from the phase
2b review, not this pull request's scope (note 6).

### 3.2 Can "replaces any it overtakes" drop or duplicate an event, or break a reader's order?

`replaced` is every held re-announcement of the generation whose `Anchor` (the session sequence it stands before) is
above the new hole. I worked out when that is true:

- A hole found by `SegmentForLocked` (the arriving sequence does not follow the last seen) has `holeThrough` at or
  above the last sequence handed over, and every re-announcement's anchor is at or below the last sequence handed over
  (the anchor is the sequence that was arriving when its hole was found, and `LastSeen` is set to it). So this path
  never replaces anything, which is right: those re-announcements stand before an event the new generation does not
  cover again.
- A hole found by `AfterHoleLocked` after the bound or a too-large event dropped a **real** event at sequence S:
  re-announcements are inserted before the first held event above their hole, and the bound drops from the oldest, so
  any re-announcement whose anchor is above S was dropped before S was. Nothing left to replace.
- A hole found by `AfterHoleLocked` after the bound dropped a **re-announcement** (anchor A, so `Through` is A - 1):
  its siblings still held have anchor A, above A - 1, and are replaced; the line used is the dropped one's
  `QueuedAfter`, which for a re-announcement is the line at its hole, so the new segment re-announces the whole line
  again under a fresh label, and relabels every event above A - 1. This is the case the rule exists for: a partial
  set is never sent as if complete.

**Dropped wrongly:** no. The only events removed are re-announcements of the same hole (the Gateway never got them: a
sent and acknowledged one is no longer held). **Duplicated:** no. Relabelling replaces a held event in place
(`state.Held[i] = again`); a re-announcement is either sent under one label or replaced before it is sent. The same
prompt announced in two generations is the design, not a duplicate: the Gateway keeps one receipt per prompt id and
touches it. **Order:** held events stay in session order; the re-announcements go in before the first event above the
hole; `TakeBatch` sends the oldest held first and one generation per batch, so an older label's events always leave
before a newer label's, and the Gateway, which refuses an older generation after a newer one, never sees them out of
order. Sent sequences rise strictly inside every batch (the ingest checks this), which 1 to N then N + 1 onward
satisfies.

One consequence worth stating, pre-existing in shape: while the Gateway is unreachable and the bound is being hit on
every publish, every publish starts another generation (it did before this change too, because the dropped events
were always inside the current segment). This change adds N re-announcements to each of those, which the next
publish drops again. The held set settles at the bound plus N, as the pull request states.

### 3.3 The Gateway after a re-announce: positions right, receipts not regressed

**Positions.** The store orders receipts by first position (generation start, then sequence). A re-announced prompt
whose own `PromptQueued` the Gateway saw keeps its first position in the older generation; one whose `PromptQueued`
fell in the hole gets its first position from the re-announcement in the new generation. Because the lost range is
always after what the Gateway holds (the bound drops the oldest unacknowledged events; the Gateway holds a prefix),
a prompt seen in the old generation was queued before any prompt that only the re-announcement restores, so the
order is right in both mixes. A prompt queued after the hole has a first position above N in the new generation. The
new generation starts one millisecond after the one it continues, which `StartPrecision` keeps (whole milliseconds), so
`IsLaterGenerationThan` and `IsNewer` both say the re-announcement is newer, and `RecordQueued` touches the still
queued receipt's last position to the new generation, which is what `PlaceInLine` counts. The first test pins 1st,
2nd, 3rd across the hole and the move up when the first is delivered; the second pins the same when the bound dropped
the original `PromptQueued` events themselves.

**No regression.** `RecordQueued` on a receipt that is not `Queued` and not `Refused` logs and changes nothing
("a delivered prompt is never sent again"). The live ruling's `PromptQueued` case returns without a change for a
receipt that is not `Queued` unless the event supersedes a refusal. So a re-announcement can never move a delivered
receipt back to queued in either reader. On a `Refused` receipt the re-announcement is read as a retry and replaces
the refusal, which is right by the event model and cannot happen from this code anyway: a refused prompt is not in
the line.

**The live stream.** The buffer writes the receipts inside `Apply` before subscribers are told, and the stream rebuilds
its ruling from the store on a generation change, so the opening it shows after a hole already carries the
re-announced places, and the same events ruled again on the rebuilt ruling are not after its receipts' last position
and change nothing. The two readers derive from the same stored order, as the phase 2b tests pin.

### 3.4 Are the tests real proofs, and rule 5a

`ApiModeEventPusherQueueTests` constructs a real `SessionEventStream` with a `ForwardingSessionEventSink` whose
target the test switches between a `LoggingSessionEventSink` (the Gateway off) and the real `ApiModeEventPusher`,
whose push delegate calls the real `ApiModeEventIngest` over the real `ApiModeEventBuffer` and `ApiModeReceiptStore`
on the Gateway test harness database (the SQLite file every Gateway unit test uses; no server, no Docker). Places are
read as the receipts route reads them (`ReadWithPositions` then `PlaceInLine`). The clock is a `FakeTimeProvider`,
the sweep is never started, nothing sleeps and no port opens. The pusher's fire-and-forget trigger completes inline
because the fake push answers a completed task, so the awaited `PushSessionAsync` calls and the assertions are
deterministic. The four tests cover the Gateway-off hole, the bound dropping the queued events themselves, the empty
line, and the order and numbering on the wire. The mutation run shows the first, second and fourth fail without the
feature; the third is a guard against announcing too much, as the pull request says. `QueuedPromptsTests` covers the
fold's rules, immutability, the re-announcement's fields and the stream's stamping. Every test is named
Method_Scenario_Result and arranged, acted and asserted. Rule 5a is met.

## 4. Findings

### Finding 1 (low, coverage). The "replaces any it overtakes" branch has no test

**Where.** `NewSegmentLocked`, the `replaced` list and its `Remove`. No test reaches it with a non-empty list: in
`Publish_TheMemoryBoundDropsTheQueuedEventsThemselves_TheirPromptsStillReachTheGatewayInLine` the second enforcement
drops both re-announcements along with a text event, so by the time the new segment is made nothing is left to
replace. I traced this by hand (section 3.2); the branch is right, but a mutation that removed the `Remove(state,
replaced)` call would pass every test on the branch.

**The state that reaches it.** The Gateway unreachable, prompts queued, and the bound dropping exactly some of the
re-announcements at the head of the held list (the bound is by count and by characters, so a drop can stop between
two of them).

**The harm if it regressed.** The leftover siblings would still go up under the older label, a partial queue sent as
a generation of its own, one batch before the full one; the Gateway would then take the full one as the later
generation and end up right. So the harm is a wasted batch and a misleading generation in the Gateway's log, not a
wrong place on a screen. Worth a test because the rule is stated in the pull request and the code comment as a
guarantee: a hole with two prompts queued, a bound that drops one re-announcement, and an assertion that the Gateway
receives exactly one generation with both prompts at 1 and 2. Not blocking.

**Developer's answer: fixed, with a test that fails without the branch.** New test
`Publish_TheMemoryBoundDropsPartOfAReannouncement_TheRestIsReplacedAndOneGenerationGoesUp` in
`ApiModeEventPusherQueueTests`, on the same real stream, sink, pusher, ingest and receipt store as the other four. The
Gateway cannot take the push, at most two events are held, p1 and p2 are queued (sequences 1 and 2), the Gateway is
turned off for one text event (sequence 3), and on again for the next (sequence 4). The hole at sequence 4 opens a
generation that re-announces p1 and p2, so five events are held; the bound drops the two original `PromptQueued`
events and p1's re-announcement and stops before p2's, the state the review names. The new segment made after that
drop must replace p2's leftover. The test asserts the Gateway receives exactly one batch, one generation, holding p1
at sequence 1 and place 1, p2 at sequence 2 and place 2, then the text at sequence 3, and that the receipts route
answers p1 1st and p2 2nd.

The mutation the review describes - `Remove(state, replaced);` in `NewSegmentLocked` commented out - was run against
`CcDirector.Gateway.UnitTests` filtered to `ApiModeEventPusher`: 1 failed, 27 passed, 28 total, and the one is the new
test, with `Assert.Single() Failure: The collection contained 2 items`. That is the harm the review traced: p2's
leftover went up alone under the older label, one batch before the full one. The mutation was reverted and the run
below is on the unmutated code.

Unchanged by this answer: the code of the branch. The review found it right; this adds only the proof.

### Finding 2 (low). The bound's drop log names a re-announcement by a sequence that belongs to a real event

**Where.** `EnforceBoundLocked` logs `sequences {first}..{last}` from `Event.Sequence`. A re-announcement's
`Event.Sequence` is the number `Reannounce` gave it, `holeThrough - N + i`, which is the session's own number of a
real event before the hole, one the Director already dropped and already logged. The pusher's stated invariant is that
every drop is logged with the exact sequences dropped; here the log would say, for instance, "sequences 2..5 (4
events)" when 2 and 3 were two re-announcements, not the session's events 2 and 3 being lost now, and the two
re-announcements' loss is not named.

**The harm.** Only to an operator reading the Director log after an outage: the drop line misreads, and the truth is
one line later, in the "A HOLE STARTS A NEW GENERATION" line that lists the re-announced prompt ids. No screen is
affected. A fix is one line: group or name the re-announcements by prompt id in the drop log, as `TakeBatch` already
does for a too-large one. Not blocking.

**Developer's answer: fixed.** `EnforceBoundLocked` now builds the drop's description with a new
`ApiModeEventPusher.DescribeDropped`, which names the session's own dropped events by their sequences, first to last
with their count, and each dropped re-announcement by its prompt id, never by the sequence it was given to go up
under. In the state of finding 1 the line used to read "sequences 1..2 (3 events)", where the 2 was p1's
re-announcement; it now reads "sequences 1..2 (2 events) and the re-announcement of 1 queued prompt(s) [p1], not yet
sent (made at a hole, not events of the session)". A drop of re-announcements alone names no sequence at all. Two
tests pin the words: `DescribeDropped_OwnEventsAndAReannouncement_NamesTheReannouncementByItsPromptNotItsSequence` and
`DescribeDropped_OnlyReannouncements_NamesNoSequence`. The rest of the line, and what the bound does, is unchanged.

**The proof for both answers** (`DOTNET_USE_POLLING_FILE_WATCHER=1`, .NET 10, one project at a time with no other test
run on the machine): `CcDirector.Core.UnitTests` filtered to `ApiMode`, 271 passed, 0 failed;
`CcDirector.Gateway.UnitTests` filtered to `ApiMode` with `-p:RunWorkspaceTypecheck=false`, 439 passed, 0 failed (the
436 of the review plus the three new tests). The default gate was not run for this answer.

## 5. Notes (none blocks the merge)

1. **The Director's `Position` on a re-announcement counts a written prompt.** After a hole, a prompt written to Pi
   but not yet acknowledged is re-announced at position 1, and the event model's words for `position` are "1 is the
   next prompt to be written". The Gateway derives its own place and ignores the number, and its derived place has the
   same one-event lead with or without a hole, so nothing on a screen differs. If `Position` ever becomes something a
   reader uses, the words in the event model should say "waiting for its receipt" rather than "to be written".
2. **The memory bound is exceeded by N until the next publish.** Stated in the pull request. N is the number of
   prompts a user has queued and each carries its text, so the excess is bounded by what the user typed. Fine.
3. **Logging.** The new segment's log line names the hole, the reason, the re-announced ids, the replaced count and
   the relabelled count, which is what an operator needs. `QueuedPrompts` logs nothing, as a pure value; the pusher
   logs around it. Fine.
4. **A re-announcement too large for any batch** (a prompt text over four million characters). `TakeBatch` drops it
   by name without starting another hole, so the Director does not loop. The generation then goes up without its
   sequence 1 (or with a gap), and the buffer answers "not complete" to a reader with a cursor into it, which the live
   endpoint already handles by resetting to the stored receipts; a reader with no cursor is vouched regardless. The
   prompt's own `PromptQueued` was dropped the same way before the hole, so the Gateway never knew it was queued; this
   change does not make that worse. A prompt of that size is not a realistic state.
5. **A prompt whose immediate write failed is re-queued with no `PromptQueued`** (section 3.1). Pre-existing, outside
   this pull request. If the Delivery Lead wants it closed, the host could publish a `PromptQueued` when it puts the
   prompt back at the head; then the line, the Gateway and `_queue` would agree in that case too.
6. **The remaining ghost.** A prompt whose delivery or refusal fell in the hole stays `Queued` on the Gateway in the
   older generation with no place, as the event model now says in so many words. The re-announcement gives the
   Gateway a new fact it did not have before: after a new generation's opening, every still queued receipt it holds
   that was not re-announced is such a ghost or belongs to a dead Director. Ending those is the ruling the phase 2b
   review asked for (its finding 2), and this change makes it possible to do precisely. Follow-on, not this pull
   request.
7. **The host is unchanged, and rightly so.** The pull request's reason holds: `_queue` drops a prompt at the write,
   the Gateway keeps it until the receipt, and the line must match the Gateway. Folding from events is the only source
   that gives that line at the exact point in the event order where the hole falls, including for events the pusher
   never saw.

## 6. Verdict

**Approve.** Merge once the default gate has run green for the Developer or Tech Lead (this review ran the two
projects filtered to ApiMode, not the gate). Findings 1 and 2 are low and may be taken as follow-on; neither changes
what any reader shows. Carry note 6 to the Delivery Lead as the next step on the receipt store, beside the phase 2b
review's finding 2.
