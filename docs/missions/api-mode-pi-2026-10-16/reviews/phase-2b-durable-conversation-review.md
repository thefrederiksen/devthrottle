# Phase 2 follow-on review: the durable conversation opens the live stream (pull request #4200)

Mission: API mode for agent wrappers, proven on Pi (issue #4070). Phase 2 follow-on, item 3: the last Gateway piece
the clients (phase 3) build on. Pull request #4200, "API mode phase 2 follow-on: the durable conversation opens the
live stream", branch `api-mode-durable-conversation`, reviewed at its head commit `96bbe560e` (one commit on base
`432e54a28`, which was `origin/main` at the time of the review). Reviewer seat, session 109, opened by the Delivery
Lead, running a different agent family from the Developer. Written 18 October 2026 on devlinux.

**Verdict: approve, on condition that finding 1 is fixed before the merge.** The change implements the Tech Lead's
design as written: every ruled final item is stored per session under the buffer's session lock, worded by the
ruling alone; a later final with the same id replaces its row in place and keeps the Gateway's number; writes are
idempotent by position; a reader with no cursor is handed the opening (stored items with the receipts merged in) read
under the same lock the ingest writes under, and the stream continues from exactly there with no gap and no double; a
cursor reader is unchanged except that a reset now names this route and is followed by the opening; the bound, the
marker and the retention are as specified; connection lines are named by their generation. I traced the join by hand
for every way it can be reached (a batch arriving between the subscribe and the snapshot, a reset mid-stream, a
generation change, the poll form) and found no gap and no double. The one thing I would not merge as it stands is the
cost of the ingest path: the conversation store opens a database transaction and runs a query for EVERY reply delta
and every thought delta, under the session's lock and under a lock shared by every session on the Gateway (finding
1). On the in-process SQLite harness that is cheap; on the hosted PostgreSQL Gateway each of those is a network round
trip, and deltas are most of what an API-mode session sends. The fix is small and local to the store. Six further
findings, all low, and four notes; none blocks the merge.

Judged by reading and by three permitted test runs, each filtered to named classes, each started only after checking
no other test host was running on the machine. Run 1: the five touched classes (`ApiModeConversationStoreTests`,
`SessionLiveEndpointTests`, `ApiModeLiveRulingTests`, `ApiModeEventIngestTests`,
`FleetOutcomeStopIdentityMigrationTests`): 190 passed, 0 failed, 61 seconds. Run 2: one probe test of my own that
times the ingest (deleted after the run, never committed; its numbers are under finding 1). The third run was not
used. The client-core vitest file was not run here; the Developer reports 43 passed, and the client change is two
comments, two type comments and one test-kit string.

## 1. Scope: what I read, what I ran, what I could not reach

**Read, in full, on the branch:** the diff against `origin/main` (36 files; the two Designer files and the two
snapshots only for what they add); `ApiModeConversationStore.cs`, `ApiModeConversationOpening.cs`,
`ApiModeConversationItemEntity.cs` (new); `ApiModeLiveRuling.cs`, `ApiModeEventIngest.cs`, `ApiModeEventBuffer.cs`,
`IApiModeEventFeed.cs`, `SessionLiveEndpoint.cs` as they stand after the change, the whole stream loop included;
`ApiModeReceiptStore.cs` (the positioned read, where a receipt's first position is set and reset, the per-row
purge); `ApiModeEventPosition.cs` (the millisecond precision and the ordering rule); `SessionHistorySweep.cs`;
`GatewayDbContext.cs` (the entity's mapping, the tenant scope, the collations); both migrations; the hub's
`PushApiModeEvents` and the Director's `ApiModeEventPusher` (what a refused batch becomes); the Pi dialect's
`EmitText` and `Turn.OpenMessage` (where a message id is made) and `SessionEventNumbering` (the turn id's form); the
client's `liveConversation.ts`, `ApiModeLiveChat.tsx`, `liveTypes.ts`, `liveStream.ts`, `useApiModeLiveChat.ts`; the
three changed test files and the new one, with the shared helpers in `ApiModeEventTestData`; the ruling and ingest
diff of open pull request #4179 (for the clash only; not reviewed); the pull request body and its known difference.

**Ran:** the two runs above. **Did not run:** the gate, the parked suites, the client-core tests, PostgreSQL.

**Could not reach:** a real Pi; the hosted Gateway's PostgreSQL round-trip times (finding 1 reasons from the SQLite
measurement and the statement count); Windows.

## 2. The design, point by point

The Tech Lead's design is checked against the code here; each point names where it holds.

- **Each ruled final item is stored per session; never a delta; receipts stay in the receipt store.**
  `ApiModeLiveRuling.ConversationItems` hands the store exactly what the ruling makes of one event, less the receipt
  items; the store keeps a `replace` item and only marks the place of an `append` one (`RecordOnce`, the
  `isFinal` branch). A thought delta is hidden by the ruling, so nothing is stored for it. Holds.
- **Written by the ingest under the buffer's session lock, words from the ruling only.** `ApiModeEventIngest.Accept`
  calls `_conversation.Record` inside the `persist` callback the buffer runs under `state.Gate`, after the receipts.
  The store words nothing; the one line it does not take from the ruling, "Earlier conversation in this session is not
  kept.", is worded in `ApiModeConversationOpening`, which is Gateway code (rule 7 holds). Holds.
- **A later final with the same id replaces in place and keeps its number.** `Fill` changes every field but
  `Ordinal`, `CreatedAtUtc` and the first position; the primary key is (tenant, session, item id).
  `Record_ALaterFinalWithTheSameItemId_ReplacesItInPlaceAndKeepsItsPlace` pins it. Holds.
- **Idempotent by position.** A replace is applied only when its position is after the row's last position
  (`IsAfter`, generation start then sequence, both at millisecond precision because `ApiModeEventPosition`'s
  constructor trims the start on both sides). A row is created without a position check, which is safe because the
  buffer refuses an older generation outright and inside a generation only sequences above the highest held reach
  the store; after a Gateway restart a re-sent batch finds its rows already there.
  `Record_TheSameBatchSentAgain_ChangesNothing` restarts the Gateway between the two sends. Holds.
- **A reader with no cursor gets the opening, read under the same lock, then live with no gap and no double.** See
  section 3, question 1. Holds.
- **A cursor reader is unchanged except that a reset says rebuild from this route and the opening follows.** The
  complete-replay path still calls `StartRuling` from an empty ruling caught up to the cursor and sends nothing first
  (`Stream_ACursorReaderWithAStoredConversation_GetsOnlyTheEventsAfterItsCursor`). `ResetFor` names
  `/sessions/{sid}/live` alone; `RebuildAsync` writes the reset and then the opening. Holds.
- **Bound: 2,000 items and 2,000,000 characters per session, oldest dropped, the opening then starts with the
  marker line.** `EnforceBound` runs inside the write's transaction when a write added a row or made one longer; it
  never drops the newest row; it marks the new oldest. Four tests cover the item bound, the character bound, one item
  larger than the whole bound, and the first item larger than the bound (no marker). The opening adds the notice when
  the first stored row is marked and leaves out receipts placed before it. Holds.
- **Retention 90 days like the receipts; no purge on RemoveSession.** `Retention` is the receipts' constant; the
  sweep calls `PurgeOlderThan` per tenant beside the receipts' purge; the conversation purge is per session (a session
  goes whole once its newest item is older than the cutoff). Nothing was added to the roster's remove path. Holds,
  with the asymmetry recorded as finding 5.
- **Connection items renamed `connection:<generation>:<sequence>`.** `ConnectionItemId` takes the generation from the
  held event's position and refuses an unpositioned connection event; the test kit's `connectionItem` and the two
  contract comments follow. Holds.

## 3. The nine questions

**(1) Gap and double at the join.** Holds, in every case I could construct.

The mechanism is two counters. `LiveConnection.Offer` counts every event the feed hands the stream (`_offered`),
whether or not the channel took it, and the feed hands events only under the session's lock. `Open` reads the
stored receipts, the stored conversation, the held events and `connection.Offered` inside ONE `Snapshot` callback
under that same lock. So the count the opening carries (`Opening.Queued`) is exactly the number of queued events whose
effect the stores already reflect, because the ingest writes both stores before it offers an event, and both the
write and the offer happen under the lock the snapshot holds. The loop then skips the first `Queued` events it takes
off the channel (`++taken <= inOpeningThrough`) and rules everything after.

- *A batch arriving between the subscribe and the snapshot.* `Subscribe` replays the held events into the channel
  under the lock and registers the handler before releasing it. A batch ingested between that and the snapshot is
  written to the stores, appended, and offered (counted) before the snapshot can take the lock; the opening reflects
  it and `Queued` includes it. No gap, no double. The channel is sized at the buffer's bound plus the backlog, so the
  replay of a full buffer cannot overflow it.
- *A reset mid-stream.* At a generation change or a `Remembers` failure the loop has already taken the event that
  triggered it (`taken` counts it); `RebuildAsync` sends the reset and a fresh opening whose `Queued` is at least
  `taken`, and the loop `continue`s past the trigger event because the opening holds it. Every event offered after
  the snapshot has an index above `Queued` and is ruled by the opening's ruling. No gap, no double.
  `Stream_TheDirectorRestartsWhileFollowing_SendsAResetThenTheNewGeneration` and the two "no longer reach back"
  tests read the reset, the opening, and then a live frame that is the next event and only the next event.
- *The poll form.* A poll with no cursor is served the opening with `next` set to the cursor of the last held event
  the opening reflects (none when nothing is held, so the next poll asks for the opening again rather than for a
  cursor the Gateway never handed out). A poll whose cursor the buffer cannot continue is served a reset and the
  opening with the same `next`. `Serve` clears anything ruled before the reset. The next poll continues from `next`
  through the unchanged cursor path. No gap, no double.
- *Fell behind.* `Offer` counts an event it could not queue, so `Queued` can exceed the channel's contents, but the
  loop never reads past a fallen-behind flag: it ends the stream with the fell-behind reset and the client reconnects
  with no cursor. No wrong picture.

**(2) The opening after a Gateway restart equals what a live reader saw.** Holds for every rendered field but the
one the Developer states. `Stream_NoCursorAfterAGatewayRestart_OpensWithWhatAReaderSawLive` applies a whole turn the
way a client applies it, restarts the Gateway (new buffer, new stores over the same database), and compares the
opening and the poll to the live view field by field. The order agrees because both sides order by the position of
the event that created each item: the live reader saw a reply where its first delta arrived, and the stored reply's
row was created at that delta; a receipt is placed by the position of the event that created its row, which is the
event the live reader first saw it at.

The known difference (a stored receipt carries `turnId`, a live receipt of a turn that ended `done` never got it):
**not a defect for a client applying by item id today.** `liveConversation.ts` keeps `turnId` on the entry and nothing
reads it; `ApiModeLiveChat.tsx` switches on `kind` alone. It becomes one the day a client groups or styles by turn,
and the right fix is then on the live side (send the receipt again when `TurnStarted` gives it a turn), which is
what #4179 already does for a relinked lost receipt. Recorded as note A.

Two differences the Developer did not state, both consequences of "deltas are never stored": a reply that streamed
but never got its final (the agent died mid-reply) is absent from the opening once the buffer has let its deltas go,
where a live reader saw its partial text; and a reader opening mid-reply after a Gateway restart sees the reply with
its beginning missing until the final arrives, because only the deltas the new process holds can be folded in.
Finding 6.

**(3) The tool finish after a restart keeps the stored title.** Holds on both sides. The store reads the stored card
before ruling a `ToolCallFinished` and hands it to `ConversationItems`, whose ruling loads a card still `running`
into its open tools, so the finish is ruled with the start's title and input
(`Record_AToolFinishAfterAGatewayRestart_KeepsTheTitleItsStartGave`). A reader's opening ruling is built from the
stored finals the same way, so a live finish after the restart keeps the title too
(`Stream_AToolRunningAcrossAGatewayRestart_FinishesLiveWithItsTitle`). A finished card is deliberately not loaded,
and `Remembers` answers false for it (`Rule_StartedFromAStoredFinishedToolCard_DoesNotTreatItAsRunning`), which is
the one path behind finding 4.

**(4) The bound.** Counts and the marker hold (section 2). Receipts before the oldest kept row are left out of the
opening: `ApiModeConversationOpening.Build` cuts at the first stored row's first position when that row is marked, and
the stream test over the bound asserts no receipt kind appears. The retention rule differs from the receipts': finding
5.

**(5) The ingest refuses a message, tool call or request id over 128 characters: what happens to the Director's
batch.** It is refused whole and dropped for good; nothing stalls. `Parse` throws `ArgumentException` before anything is
written; the hub catches exactly that type, logs "REFUSED a malformed batch", and answers null; the pusher reads null as
"refused for good", logs it with the sequence range, removes those events from what it holds, and continues with the
next batch. So the session's push does not stall and nothing is re-sent for ever. What is lost is the whole batch: up to
500 events, which can include the receipts, the turn's end and the tool cards that shared it with the long id; the
buffer then accepts the next batch above the hole, and no reader is told. That is the shape every malformed-batch
refusal already had before this change; what is new is that the trigger is an id the MODEL provider mints (Pi's tool
call id passes through the dialect unchanged), not one the Director controls. The Director's own ids are safe: a
message id is `<turnId>-message-<n>` and a turn id is `turn-<n>`, so a message id is well under 128. Finding 2, low:
real tool call ids are a few dozen characters, and the answer to the Delivery Lead's question is "no stall".

**(6) The migration.** Newest on both providers (`20261018041929` SQLite, `20261018042014` PostgreSQL, both after the
prompt-text migration), one table, primary key led by `tenant_id`, a unique index on (tenant, session, ordinal), a
retention index on (tenant, updated-at), collation `C` on the three Director-supplied strings on PostgreSQL. The entity
is in `ApplyTenantScope`, so the global filter applies to every read and the `Find` queries do not name the tenant
themselves. `FleetOutcomeStopIdentityMigrationTests` passed in run 1, which checks that the newest Designer on each
provider carries the current model and neither snapshot is behind it. The two-accounts tests (`Read_AnotherAccounts...`
and `Stream_NoCursorInAnotherAccount...`) prove the scoping through the real boundary. Holds.

**(7) Cost on the ingest path under the lock.** Finding 1.

**(8) The client-core change and the contract comment.** The contract paragraph in `SessionLiveEndpoint.cs` now says
"attach with no cursor, and the stream opens with the conversation", what the opening holds, that its id is the
cursor of the last event it reflects, that it is read under the lock, and what a client does on a reset (drop
everything, apply what follows; after fell-behind, reconnect with no cursor). `liveStream.ts`, `liveTypes.ts` and
`useApiModeLiveChat.ts` say the same thing in the client's words; `liveTypes.ts` adds the `notice` kind and the
connection id form; the test kit's connection id follows. The client already drops everything on a reset
(`applyReset` returns empty entries) and renders an unknown kind as a line, so the notice renders without a client
change. Consistent.

**(9) Tests, names, logging.** Every new test name is `Method_Scenario_Result`. The new tests cannot pass without the
change: the store tests name types the base does not have, and the moved expectations (one opening frame under the
cursor of the last held event, `connection:gen-a:1`, a reset followed by the opening with an id) describe behaviour
the base does not produce; I traced each moved assertion against the old code and each would fail there. The tests
run on the in-memory host and the harness database with an injected clock; nothing sleeps or binds a real port. The
one test that takes long is the bound test over 2,001 items; it ran inside the 61 seconds. Logging: every store write
logs the item and its number; the opening logs once in `Open` and once when sent; `Record` logs a lost race and the
bound. `ApiModeConversationStore.Read` logs nothing, which is the one public method short of the project's rule; the
receipt store's reads are the same, so I record it as note D and not a finding.

## 4. Findings

**Finding 1 (medium, fix before the merge). Every delta costs a database transaction and a query under two locks.**
*Where:* `ApiModeConversationStore.Record` and `RecordOnce`. *The state that reaches it:* every `MessageText` event
that is not the first delta of its reply, and every thought delta, which the ruling hides. `Record` returns early
only for the four receipt-and-turn-start kinds; for everything else `RecordOnce` opens a context and begins a
transaction BEFORE it asks the ruling what the event keeps. A later reply delta then runs one `Find` query, sees the
row, and returns 0, rolling the transaction back; a thought delta opens the transaction, gets no items from the
ruling, and rolls back. All of it runs inside the buffer's session lock (so a reader's opening for that session
waits) and inside the store's `_gate`, which is ONE lock for every session of every tenant on the Gateway (so every
other session's conversation write waits too). *Measured* (run 2, in-process SQLite, one batch of 500): reply deltas
0.31 milliseconds each (155 milliseconds for the batch), thought deltas 0.07 milliseconds each, stored finals 7.5
milliseconds each (3.8 seconds for a batch of 500 finals). On the hosted Gateway the database is PostgreSQL over the
network: a delta is then three round trips (begin, select, rollback), which at the half-millisecond to millisecond
each that an in-region database costs is two to three milliseconds per delta, serialised fleet-wide by `_gate`.
Deltas are most of what a streaming session sends. *The harm:* a ceiling of a few hundred deltas a second for the
whole Gateway before the hub's push calls queue behind the lock, and a longer wait for every opening read while a
batch is being written; the receipt store has the same lock pattern but is only touched by the five receipt kinds,
which are rare. *The fix:* ask the ruling first and return without a context when it keeps nothing (that removes the
thought-delta cost entirely); and remember, in memory per (tenant, session), the reply ids whose place row is known
to exist, cleared when the final is stored, so a reply's second and later deltas touch no database at all. A reply's
first delta still writes its place row under the lock, as the design requires. One transaction per batch rather than
one per event would cut the stored-item cost further, but that is a larger change and not a condition.

**Developer's answer (fixed, as the Tech Lead ruled).** `ApiModeConversationStore.Record` now asks the ruling first,
with no database: an event it keeps nothing of (thought text) opens no context. The store remembers in memory, per
(tenant, session), the reply ids whose place row is known to exist, so a reply's second and later deltas touch nothing;
an id goes when the reply's final is seen, and the whole set goes when a turn ends, when the bound or the retention lets
rows go, and past 256 open replies. The first delta still writes the place row; after a Gateway restart an open reply's
next delta costs one query to learn the row is there, then nothing. A tool's finish is still ruled after its stored card
is read, so its title survives a restart. The memory only ever promises that a row exists; if another Gateway's bound
let the row go during a deploy, the reply's final becomes a new row at the end, where it would have gone had its deltas
arrived after the drop.

*The lock.* The store's one lock shared by every session is gone. Every write of a session is made under that session's
own lock (and already under the buffer's session lock), so sessions never wait for each other in this store. Nothing
spans sessions but the retention sweep, which takes no lock: its delete re-checks the cutoff, so a session written
between its two statements keeps its new rows. `Read` takes no store lock either: it is one query, and what makes the
opening exact is the buffer's session lock its caller holds. A same-session race, were one ever possible, is still safe:
the order is unique per session, and the loser re-reads and decides again.

*The test.* `Record_DeltasAfterTheReplysPlaceRow_DoNoDatabaseWork` pushes 200 deltas (100 reply, 100 thought) after the
place row and counts, from Entity Framework's own diagnostic events filtered to the test's database file, connections
opened, transactions begun and statements run: 0, 0, 0. Against the old store the same test reads 0, 200, 100 and
fails. The test also checks the instrument sees a stored final, so a silent instrument cannot pass it.
`Record_AnOpenReplyAfterAGatewayRestart_CostsOneQueryThenNothing` covers the restart.

*Measured* on the in-process SQLite harness, a probe test of the same shape as the review's (three rounds of 500-event
batches through the real ingest, after a warm-up; deleted after the run, never committed). Each figure is the batch's
whole ingest time divided by 500, so it includes the buffer and the receipt store's pass:

| | Before | After |
|---|---|---|
| Reply delta | 0.18 to 0.20 milliseconds | 0.02 to 0.04 milliseconds |
| Thought delta | 0.04 milliseconds | 0.007 to 0.02 milliseconds |
| Stored final | 7.4 to 8.3 milliseconds | 7.1 to 8.7 milliseconds (unchanged: one transaction per stored item) |

On PostgreSQL the difference is larger than on SQLite: a delta was three round trips (begin, select, rollback) and is now
none. One transaction per batch for stored finals was not taken; the review did not make it a condition.

**Finding 2 (low). The new 128-character refusal drops a whole batch for an id the Director does not mint.**
*Where:* `ApiModeEventIngest.CheckFields`, the `SourceId` cases; `PiDialect` lines 658 to 762 pass Pi's tool call id
through unchanged. *The state that reaches it:* a `ToolCallStarted` or `ToolCallFinished` whose tool call id, minted
by the model provider behind Pi, is longer than 128 characters. *The harm:* that batch, up to 500 events, is refused
whole, logged on both sides, dropped by the Director for good and never re-sent; its receipts, turn end and other tool
cards are lost with it, and the stream carries a silent hole. The push does not stall (question 5). *Why low:* the
ids providers mint today are a few dozen characters, and the shape is the one every malformed-batch refusal already
has. *The fix, when it is taken:* bound the id at the Director, where both the start and the finish of a tool map
through the same place, since the id is only a correlation key; then a refusal at the Gateway is a true Director
defect, as the other refusals are.

**Developer's answer (fixed, as the Tech Lead ruled: the refusal is removed).** The ingest no longer checks a message,
tool call or permission request id for length. An item whose id is longer than the column (160 characters) is not kept
in the conversation: one log line names the session, the item kind and the id's length, and the rest of the batch, the
receipts and the live stream go on as before. A reply's over-long id is remembered, so its later deltas log nothing more.
`Accept_AToolCallIdLongerThanTheConversationKeeps_KeepsTheRestOfTheBatch` replaces the refusal test: a batch with a
receipt, a tool whose id does not fit, a reply and a turn end is accepted whole, the buffer holds every event, the
receipt is kept, and the conversation keeps the reply and the turn line.

**Finding 3 (low). A poll reader with a cursor starts its ruling from empty, not from the stored conversation.**
*Where:* `SessionLiveEndpoint.ReadItems`, the cursor path, calls `StartRuling`, which builds a new ruling and
catches it up on the held events only. *The state that reaches it:* a Gateway restarted in the middle of a
generation; a tool started before the restart; a client polls once with no cursor (its opening shows the card
running, from the store) and then polls with the cursor it was given after the tool finishes. *The harm:* the second
poll's ruling has not seen the start, `vouched` is false because the buffer holds the generation from its middle,
`Remembers` answers no for the finish, and the page becomes a reset and the opening again: the client drops and
re-applies everything, once, though the opening it holds already had the card. The stream reader in the same state
continues smoothly, because its ruling came from the opening. Not wrong, only a flicker; the fix is to build the
cursor poll's ruling from the stored conversation as the opening does.

**Developer's answer (stays, as the Tech Lead ruled).** The cost is one reset and one re-applied opening, once, on a
poll reader, only after a Gateway restart in the middle of a tool; the picture is correct before and after. Building
the cursor poll's ruling from the store would add a database read to every cursor poll for every reader, to save a
flicker that needs a restart to happen. The stream reader, which is what the clients use, is not affected.

**Finding 4 (low). A re-sent tool finish can replace a stored title with "A tool that started before this view
began" on a reader that opened from the store.** *Where:* `ApiModeLiveRuling.ToolFinish` and the stored-conversation
constructor, which loads only cards still `running`. *The state that reaches it:* the ingest stored a batch's receipts
and conversation items and then threw before the buffer appended them (the one half-stored case the ingest's own
comment allows for); a reader attached with no cursor in the window before the Director's re-send, so its opening
shows the finished card from the store with its title; the re-send arrives, the buffer now appends and hands the
finish on, and the reader's ruling, which did not load the finished card, rules it as a tool it never saw start.
*The harm:* a correct card is replaced by one with no title and no input, on that reader only, until it reconnects.
The receipt items are guarded against exactly this replay by their stored positions; the tool card is the one item
kind without such a guard in the reader's ruling. Narrow (a database failure between the two writes, then a reader
in a window of seconds), so low. The fix is to give the reader's ruling the stored finished cards too, so a finish for
a card it knows is finished is ignored or re-ruled with the stored title.

**Developer's answer (stays, as the Tech Lead ruled).** It needs a database failure between the two writes of one
batch and a reader opening in the seconds before the Director's re-send; the wrong card lives only on that reader and is
gone on its next connection, and the stored row is right. Loading finished cards into every reader's ruling changes what
every opening carries to cover that window; not worth it now.

**Finding 5 (low). Retention is per row for receipts and per session for the conversation.** *Where:*
`ApiModeReceiptStore.PurgeOlderThan` (every row last changed before the cutoff) against
`ApiModeConversationStore.PurgeOlderThan` (a session whole, when its newest item is older than the cutoff). *The
state that reaches it:* a session in use for more than 90 days. *The harm:* its receipts from the first weeks are
purged while the replies and turn lines they led to stay, so the opening shows replies without the prompts that
asked for them and no notice says so; the "never a silent gap at the start of a session still in use" promise the
conversation's rule makes is kept by one store and broken by the other. The design says "retention 90 days like
receipts" and does not settle which rule; this is a question for the Tech Lead, not a defect in the code against its
record. The cheap alignment is to make the receipts' purge per session too.

**Developer's answer (stays per session, as the Tech Lead ruled).** Per session avoids a silent gap at the start of a
session still in use: the conversation is read as one continuous run from its first kept item, and a per-row purge would
cut its top with no line saying so. The class comment of `ApiModeConversationStore` now says why it differs from the
receipts' per-row rule (a receipt is an answer about one prompt that stands on its own) and states the consequence: a
session in use for more than 90 days can show replies whose receipts have gone.

**Finding 6 (low, record). What "deltas are never stored" costs the opening.** *Where:* `RecordOnce` (a place row
with no text) and `ApiModeConversationOpening.Build` (the fold of held deltas). *The state that reaches it:* (a) a
reply that streamed deltas and never got its final because the agent died; (b) a Gateway restart while a reply is
streaming. *The harm:* in (a) the reply is in the opening only while the buffer still holds its deltas; after that,
or after a restart, it is absent, where a live reader saw its partial text and the turn's failure line beside it. In
(b) the opening shows the reply with its beginning missing until the final arrives, because the new process holds
only the deltas sent after it started. Both follow from the design and neither is a wrong picture that lasts beyond
the final; recorded so the phase 3 client's authors know what an opening can and cannot show.

**Developer's answer (stays, as the Tech Lead ruled).** It follows from "deltas are never stored", and storing deltas
would bring back exactly the per-delta database cost finding 1 removes. The final replaces the partial picture when it
arrives; a reply that never gets a final still has its turn's failure line. Recorded for the phase 3 client's authors.

**Finding 7 (low, record). A no-cursor reader is still reset on every Director restart.** *Where:* the stream loop's
generation check. *The state that reaches it:* a reader that opened from the stored conversation, following live,
when the Director starts a new generation. *The harm:* the client drops up to 2,000 items and re-applies the whole
opening (up to two million characters in one frame), though the durable conversation now spans generations and the
connection ids no longer collide across them. The design kept the reset for cursor readers and said nothing about
this case; the behaviour is correct, and removing the reset later needs the ruling's per-generation sequence guard
(`_receiptSequences`) to be made generation-aware first. Not for this change.

**Developer's answer (stays, as the Tech Lead ruled).** The behaviour is correct, and removing the reset first needs the
ruling's per-generation sequence guard made generation-aware; that is separate work, not a fix to this change.

## 5. Notes (none blocks the merge)

**Note A. The known difference is correctly stated and is not a client defect today** (question 2). The record
should say that it becomes one when a client groups by turn, and that the fix then belongs on the live side.

**Note B. The clash with #4179 (lost turn end).** Both pull requests edit the same region of
`ApiModeLiveRuling.Rule`: #4179 moves the event switch into a new `RuleEvent` and rules lost endings before it; #4200
changes the two connection cases inside that switch and adds the stored-conversation constructor, `ConversationItems`,
`ToolItemId` and `OpenTool`. Both add cases to `ApiModeEventIngest.CheckFields`. Whichever lands second will hit
textual conflicts in both files; they are mechanical. Semantically the two agree: #4179's re-send of a relinked
receipt is a receipt item, which `ConversationItems` filters out, so nothing new reaches the conversation store; and
when the opening's ruling catches up on held events and #4179's lost-end rule fires inside it, the receipt row the
#4179 store wrote already carries the same outcome, so the opening and the live view agree. The ruling
`ConversationItems` builds per event has no receipts, so #4179's loop over `_order` is a no-op there. Not reviewed
beyond this.

**Note C. The buffer's `Snapshot` creates a session state for any admitted session that has none.** A poll or stream
for a session with no events yet now adds an entry to the buffer's map (as `Subscribe` already did); the cap's
eviction handles it. Not a finding.

**Note D. Attribution sweep of the commit (message and every changed file): clean. Plain English: clean**, apart from
the product names and the acronyms the code already uses. `ApiModeConversationStore.Read` has no log line; the receipt
store's reads have none either.

## 6. Verdict

**Approve, on condition that finding 1 is fixed before the merge** (the delta path must not open a transaction or
run a query; the fix is described under the finding and is local to `ApiModeConversationStore`). Findings 2 to 7 are
recorded for the Delivery Lead to schedule or decline; finding 5 is a question for the Tech Lead. The design is
implemented as approved, the join is proven by reading and by the tests, the migration is sound on both providers,
the tenant scoping holds, and the contract the phase 3 clients will read is now stated in one place and is true.
