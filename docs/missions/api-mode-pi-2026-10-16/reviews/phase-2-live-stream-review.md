# Phase 2 review: the live relay of an API-mode session's events

Mission: API mode for agent wrappers, proven on Pi (issue #4070). Pull request #4083, branch
`api-mode-p2-live-stream`, reviewed at its head commit `ee9a565ca`, which is the three commits
`a23130fd5`, `cc7b1e76f` and `ee9a565ca` stacked on `origin/api-mode-p2-display-fold` (commit
`6e427877c`). Reviewer seat, opened by the phase 2 Tech Lead (session 138), running a different
agent family from the Developer. Written 16 October 2026 on devlinux, session 101.

**Verdict: the relay holds. Four findings, none of which stops the merge; the Developer decides
each. Finding 1 is the one with a user-visible wrong word on screen, and it was proven by two tests I
wrote, ran and deleted. Findings 2 and 3 answer the Developer's own open notes (the "second ending"
log line and the missing keep-alive). Finding 4 is documentation.** Everything the Tech Lead asked
me to judge was checked against the code at the pull request's head and against runs I made, not
against the pull request's description.

## 1. Scope: what I read, what I ran, what I could not reach

**Read, in full:** the whole diff of the three commits against `origin/api-mode-p2-display-fold`
(nine files): `SessionLiveEndpoint.cs`, `ApiModeLiveDtos.cs`, the compression and session-key guard
changes, the one-line wiring in `GatewayHost.cs`, and all four test files. The code the relay leans
on, read on the branch: `IApiModeEventFeed` and `ApiModeEventBuffer` (the atomic replay-then-subscribe
under the session lock, `ReplayOf` and its `Complete` rule, `Trim`, eviction over the session cap),
`ApiModeLiveRuling` (what it remembers, `IsFinal`, `ToolFinish`, `TurnEnd`), `ApiModeReceiptStore.Read`
(the order the stored receipts come in), `ConditionalJson` (the 304 path the poll uses),
`GatewayResponseCompression`, the existing `GET /events` stream in `GatewayEndpoints.cs` for
comparison, the `ApiModeLiveItem` contract, and the display rules of `event-model.md` (section 2 and
the `MessageText` row). CLAUDE.md rule 7.

**Ran, with the .NET 10 software development kit on devlinux** (never `scripts/test-local.ps1`):

- `dotnet test` of `CcDirector.Gateway.UnitTests` filtered to `ApiMode`, `Live`, `SessionKeyGuard`,
  `ConditionalJson` and `TeamEndpointRules`: 1,201 tests, 1,200 passed, 1 failed, 0 skipped, 70
  seconds. All 45 `SessionLiveEndpointTests` passed. The one failure is
  `HeldDeliveryDriverTests.ARecordWriteThatThrowsAfterTheClaimMark_IsPressedAgainByTheDriver_ResolvedWithNoRestart`,
  which my filter caught because "Delivery" contains "live". That file and the code it tests are
  byte-for-byte what `origin/main` has, and it fails the same way run alone, so it is not this pull
  request's doing. It is outside my scope; I only report that it is red.
- Three throwaway tests for findings 1 and 2, written in a new file, run (all three passed, which
  is what proves the findings), then deleted. The tree is clean apart from this review.
- A sweep of the three commit messages and the diff for assistant attribution: none. (The only text the sweep
  matched is the name of the repository's `CLAUDE.md` file, cited by rule number in a code comment.)

**Could not reach:** a real Kestrel listener and the Azure front end in front of the hosted Gateway
(finding 3's idle limit is the platform's documented figure, not one I measured); a browser's
`EventSource` against the stream (its reconnect behaviour is the specification's, not observed); the
session-key guard end to end over HTTP (I checked the rule table, as its tests do); the rest of
`Gateway.UnitTests` beyond my filter.

## 2. What was asked, and what I found for each

- **No gap and no duplicate between replay and live at attach.** Holds, by construction: the feed's
  `Subscribe` hands the replay to the handler and adds the subscriber under the same session lock
  that `Apply` holds to append and deliver, so no event can land between the two. The relay's handler
  only queues. The happy path is tested (`Stream_NoCursor_ReplaysWhatIsHeldThenFollowsLive`); no test
  pushes during an attach, and the lock makes that unnecessary.
- **A reset is sent exactly when it must be and never otherwise.** Holds for the cases the contract
  names: a cursor that cannot be continued (evicted, never held, another generation, nothing held),
  and a generation change while following; never to a client with no cursor. Each has a test. The
  one case where a client is continued without a reset but should not simply append is finding 1.
- **Cursor parsing.** `TryParseCursor` uses `NumberStyles.None`, so signs, spaces and thousands
  separators are refused; an overflowing number is refused; the sequence follows the last colon so a
  generation may carry one. Five malformed shapes are tested and answer 400 with the expected shape in
  the message. The header wins over the query, as agreed, and is tested.
- **Tenant isolation.** Every read is keyed by the request's tenant: the roster lookup, the feed's
  `Read` and `Subscribe`, and the receipts read inside `EnterScope`. Another account answers 404 and
  opens no subscription (tested on both routes). The session-key guard lists the two literals, with
  the write methods and a sibling path refused (tested). The team rules class both routes with
  `/history` (tested). A terminal-mode session answers 409 with the plain sentence (tested).
- **The subscription is always released.** `using var subscription` in `StreamAsync` disposes it on
  every exit: a disconnect (cancellation), fell behind, and an exception. Tested for disconnect and
  fell behind. The one case where release is late rather than missing is a client that vanishes
  without closing; see finding 3.
- **The feed is never blocked by a slow client.** `Offer` is one `TryWrite`; nothing in the handler
  waits. The fell-behind test pushes 2,010 events synchronously while the client's writes are gated
  shut and the push returns, which is the proof.
- **Compression.** The stream path bypasses the middleware (`IsSessionLiveStream`, exactly three
  segments) and `text/event-stream` is also excluded by type; the poll is compressed and 304-able.
  Both tested, including the mixed-case path.
- **The route documentation states the load-first-then-attach contract.** It does, in the class
  documentation of `SessionLiveEndpoint` (the paragraph headed "THE CONTRACT FOR A CLIENT (phase
  3)"). There is no other route documentation to update: the receipts route has none either.
- **The tests prove what they claim.** They do. No sleep, no port, no process; the only clock is an
  injected one for the roster's staleness; the 30 second `HangGuard` turns a hang into a failure and
  decides nothing. The fell-behind test's `InRange(1, 2)` is honest: the one interleaving it cannot
  pin is whether the loop had taken event 2 before the channel filled. The database is the
  repository's throwaway SQLite file, as every store test uses.

## 3. Findings

### Finding 1. A "complete" replay is not a complete memory: a reconnecting client can be told to replace a tool title it already had, and can be left with a receipt that hides a failed turn

**Where:** `SessionLiveEndpoint.StartRuling`, the branch for a complete replay with a cursor.

**What happens.** When a client's cursor can be continued (the feed says `Complete`), the relay
starts an EMPTY ruling and feeds it the held events up to the cursor. `Complete` only promises that
every event AFTER the cursor is held. It says nothing about the events BEFORE it, which the bound
(2,000 events or 2,000,000 characters per session, oldest first) may have let go. The ruling's
memory lives in those earlier events: a tool card's title and input come from `ToolCallStarted`;
which prompts a turn carried comes from `TurnStarted`. When the start event is gone, the ruling has
no memory of it, and no reset is sent because the cursor itself is fine.

**The harm, proven by two throwaway tests** (built through the real ingest, buffer and receipt store):

1. A client followed from the start, so it shows the tool card "bash" with its input. It reconnects
   at its newest cursor while the tool is still running, after 2,000 more events have pushed the
   start out. The replay is complete, empty, and no reset is sent. When the tool finishes the client
   receives a `replace` for `tool:tc1` whose title is "A tool that started before this view began",
   status "done", and no input. A dumb client must replace, so the right title it was showing is
   overwritten with a sentence that is false for that client.
2. The same shape with a turn: `TurnStarted` has been let go, the client reconnects, the turn then
   ends `failed`. The ruling sends the turn line only. The prompt's receipt is never rewritten to
   "Delivered - the turn failed: ...", so on that client the prompt reads plain "Delivered", which is
   exactly what event model rule 5 forbids.

**Why it will happen in practice.** The character bound is the one that bites: `ToolCallFinished`
carries the tool's output verbatim, so a turn with a handful of large outputs passes 2,000,000
characters inside one turn, at which point its own `TurnStarted` and the start of any tool still
running are gone while the turn is still live. And reconnects are routine, not rare: on the hosted
Gateway the stream is cut by the front end about every four minutes of quiet (finding 3), and a
browser's `EventSource` reconnects at the newest cursor each time.

**What would make it right** (the Developer chooses): the honest answer is that `Complete` for the
relay has to mean "held back to the start of everything still open", or the reader must be reset. Two
shapes that work: treat a cursor as continuable only when the held events reach back to the earliest
open turn (the feed knows what it evicted; the ruling knows what is open), and send `missed_events`
otherwise; or start every ruling from the stored receipts as well as the held events, which fixes
case 2 durably (the store records each receipt's turn) but not case 1, since nothing durable holds a
tool's title. A third shape, a `replace` that carries no title and lets the client keep its own, is
not open to us: the client is dumb and must not decide to keep anything.

Developer's answer:

### Finding 2. The "second ending" log line fires for every replayed delivery after a reset, and calls correct behaviour a Director defect

**Where:** `ApiModeLiveRuling.IsFinal`, reached from the relay whenever a ruling is started from the
stored receipts (`StartRuling` for an incomplete replay, and the generation change inside the
stream loop) and then handed events the store already recorded.

**What happens.** A ruling built from the stored receipts holds each prompt as `Delivered` or
`Refused`. The events then replayed to it include the very `PromptDelivered` or `PromptRefused` that
produced the receipt (the ingest writes the receipt before the buffer hands the event on, so the
receipt is always ahead of the replay). `IsFinal` cannot tell "the event that made this receipt" from
"a second ending", so for each such event it writes
`a second ending (PromptDelivered) after Delivered; the first stands`. That happens on every reset,
on every no-cursor attach to a generation held from its middle, on every generation change while
following, and on every POLL in those states, once per replayed prompt. Confirmed by a throwaway
test and by reading: the wire output is correct (no item is sent), only the log is wrong.

**The harm.** The line is worded as a Director defect (the ruling's own comment says "a second
ending is a Director defect, logged"). It will be the most common line in the Gateway log for this
feature, written on a path that is routine, so the one time a Director really does end a prompt
twice the line is invisible among copies of itself, and an operator who grepped for it would chase a
defect that did not occur.

**What would make it right** (the Developer chooses): the relay knows it is replaying over stored
state, so it can tell the ruling the cursor the receipts cover and have `IsFinal` stay quiet for an
event at or below it; or the ruling can keep, per receipt, the sequence that ended it (the store
has the position) and log only when a LATER ending arrives. Either keeps the one honest line.

Developer's answer:

### Finding 3. No keep-alive on the stream: it does not break a browser, but it cuts every quiet stream on the hosted Gateway about every four minutes and leaves a vanished client's subscription in place until the session's next event

**Where:** the response loop in `SessionLiveEndpoint.StreamAsync`, which writes only when an event
arrives. (The existing `GET /events` has the same gap; this finding is about the new route.)

**The Developer asked whether this breaks clients behind proxies that close idle connections.** The
answer, in three parts:

1. **A browser `EventSource` is not broken.** The hosted Gateway runs on Azure App Service, whose
   front end closes a response that has sent nothing for 230 seconds (the platform's documented
   figure; I did not measure it). The browser then reconnects on its own, after its retry delay,
   sending `Last-Event-ID`; the relay replays from that cursor, usually nothing, with no reset. The
   user sees nothing. The cost is one reconnect per quiet session view per four minutes, and each of
   those reconnects is an exposure to finding 1.
2. **A client that is not a browser must do that reconnect itself**, and it cannot tell a cut idle
   stream from a dead Gateway, because both look the same: the stream ends with no frame. The route
   documentation should say that the stream may end at any time without a `reset` and that the
   client reconnects with its last cursor. That is not written anywhere yet.
3. **A client that vanishes without closing** (a phone that sleeps, a network that drops behind a
   home router) is not noticed by the Gateway, because nothing is written to it. Its subscription, its
   2,256-slot channel, and the pin its subscriber puts on the session's buffer state (the buffer
   never evicts a session with a subscriber) all stay until the session's next event makes a write
   fail. For a session that has ended, that is until the Gateway restarts. Bounded, and
   self-healing on an active session, but a leak on a quiet one.

**What would make it right** (the Developer chooses): a comment line (`: keep-alive` and a blank
line, which every Server-Sent Events parser ignores) written whenever the loop has waited a period
(twenty to thirty seconds, below the front end's limit) without an event. It keeps the front end from
cutting the stream, and it is the write that detects a vanished client. It can be built without the
real clock deciding a test: the wait is `WaitToReadAsync` raced against a delay from an injected
delay function, which the tests replace.

Developer's answer:

### Finding 4. Two things the contract leaves a phase 3 client to guess

**Where:** the `Sequence` documentation on `ApiModeLiveItem` (`ApiModeDisplayDtos.cs`), and the
contract paragraph on `SessionLiveEndpoint`.

1. **`Sequence` 0 on a stored receipt item.** The Developer's note is right that 0 is a safe value:
   a real sequence starts at 1, so 0 cannot be mistaken for an event. But the type's own words are
   "the sequence of the session event this item was ruled from", which is false for an item that
   came from no event. Say on the type that 0 means the item came from durable state, and say in one
   sentence that a client applies items in FRAME order and never sorts by `Sequence`, because the
   sequence starts again at every generation and a reset puts 0-sequence items after higher ones.
2. **What a no-cursor attach replays over the durable state the client just loaded.** The contract
   says a no-cursor client "holds nothing that could be continued wrongly". It holds the receipts and
   the stored conversation it was told to load first, and a complete replay then sends the WHOLE
   held generation on top of them. That is right only because every item is applied by `ItemId` in
   `replace` mode, except reply text, whose deltas are `append`. The paragraph should say that: the
   replayed items are idempotent over the durable state by `ItemId`, and a reply whose deltas are
   replayed is made whole again by the `final` that follows (event model, the `MessageText` row), so
   a client that keys its loaded conversation by the same message id will not show text twice once
   the final lands. Without that sentence, phase 3 will discover it by seeing doubled text.

Developer's answer:

## 4. Notes that are not findings

- The poll re-rules every held event at or below the cursor on every call (up to 2,000) to bring its
  ruling to the cursor. The ruling is dictionary work and the catch-up serialises nothing, so a
  two-second poll costs a few milliseconds at most. Fine as it is; noted so nobody is surprised by it
  in a profile.
- A malformed cursor is refused (400) before the session is located, so a bad cursor on another
  account's session answers 400 rather than 404. The cursor's shape reveals nothing, so this is not a
  leak; noted only because someone will ask.
- On the poll, after a `missed_events` reset with nothing held, `next` is null and every following
  no-cursor poll answers the stored receipts again, with `next` null, until the session produces an
  event. The ETag makes each repeat a 304, so it is cheap; the stream for the same state sends the
  receipts once. Consistent enough; noted.
