# Phase 2 review: the Gateway ruling, read as a whole

Mission: API mode for agent wrappers, proven on Pi (issue #4070). Phase 2 is merged on main as five pull
requests: #4077 (the session fields), #4079 (take the wheel, hand back, stop the turn, and the owned-session
key rule), #4082 (receipts, the event ingest, `DirectorHub.PushApiModeEvents` and its watermark), #4086 (the
display ruling) and #4083 (the per-session live stream with replay). Each was reviewed alone; this review reads
the merged code together, at origin/main commit `e58f498f3` (the merge of #4083), against section 5 of the
mission document, phase 2's row in section 6, and rule 7 of the project instructions (the Gateway rules, a
client only renders). Phase Reviewer seat, opened by the Delivery Lead (session 102), running a different
agent family from the Developers. Written 16 October 2026 on devlinux. Nothing here was built or fixed by the
reviewer; the only files I wrote were throwaway tests, run and deleted.

## Verdict

**The phase holds as a whole, with one finding that must be answered before phase 3 builds on the receipts,
and one low finding for the seat that integrates phase 4.** Every seam the Delivery Lead named was traced:
ingest to store to fold to live stream, the prompt ids the Director sends, replay after a sequence number and
after a generation change, and a Gateway restart mid-turn. The five pull requests fit together where they
touch, with one exception: the receipt store and the live ruling keep the FIRST ending of a prompt, while the
event model as amended in phase 1, and the Director built to it, say that a retried delivery reuses its
Gateway id and the LATER receipt stands. On the merged code a prompt that the Director refused once and Pi
then acknowledged on the Gateway's own retry is shown as refused for ninety days, and the turn it started is
invisible to the roster's verdict. That is finding 1, proven by two tests I wrote, ran and deleted.

## Scope: what I read, what I ran, what I could not reach

**Read in full, on origin/main.** The Gateway: `IApiModeEventFeed.cs`, `ApiModeEventBuffer.cs`,
`ApiModeEventIngest.cs`, `ApiModeReceiptStore.cs`, `ApiModeEventPosition.cs`, `ApiModeSessionFold.cs`,
`ApiModeRowStamp.cs`, `ApiModeVerdictReceipts.cs`, `ApiModeLiveRuling.cs`, `SessionLiveEndpoint.cs`,
`SessionReceiptsEndpoint.cs`, `GatewayEndpoints.ApiModeVerbs.cs`, the `PushApiModeEvents` method and its
wiring in `DirectorHub.cs`, the API-mode entries in `SessionKeyGuard.cs`, `GatewayResponseCompression.cs` and
`SessionHistorySweep.cs`, the stamp call sites in `GatewayEndpoints.cs`, and the host wiring in
`GatewayHost.cs`. The contracts: `DirectorUpStreamMessages.cs` (the batch and the watermark), `SessionEvents.cs`
and `SessionEventValues.cs`, `ApiModeReceiptDtos.cs`, `ApiModeDisplayDtos.cs`, `ApiModeLiveDtos.cs`, and the two
fields with their value classes on `SessionDto.cs`. The Director side, read to check the seams the Gateway
leans on: `SessionEventNumbering.cs`, `SessionEventStream.cs` (the sink), `ApiModeSessionHost.cs` (prompt id
resolution, the queue, the process end), `PiDialect.cs` (turn start and turn end), `SessionConnectionModeWords.cs`,
the row mapping in `ControlEndpoints.cs`, the API-mode send in `Session.cs`, the `turn-stop` verb in
`SessionCommandExecutor.cs`, and `OwnedSessionInput.OwnsDirectly`. The record: the mission document (issue
#4070), `event-model.md` whole, the mission README, and all five phase 2 pull request reviews with their
Developer answers. The three open phase 4 pull requests (#4090 the Director push, #4091 the crash journal,
#4094 the wheel) were read as their descriptions and the review text in their diffs, to know what is in
flight; their code was not reviewed.

**Ran, on devlinux with the .NET 10 software development kit** (never `scripts/test-local.ps1`, which is
Windows-bound; never a real Pi):

- Three throwaway tests in a new file under `Gateway.UnitTests/Receipts`, written to assert what the event
  model requires. All three FAILED, which is the proof of finding 1 and of observation C. The file is deleted;
  the branch carries only this review.
- `dotnet test src/CcDirector.Gateway.UnitTests` filtered to `ApiMode`, `SessionLive`, `SessionReceipts`,
  `SessionConnectionFields`, `SessionKeyGuard`, `PiFixtureIngest` and `PushApiModeEvents`, after deleting the
  throwaway file: 770 tests, 770 passed, 0 failed, 0 skipped, 72 seconds. So the merged phase passes its own
  tests on this machine; the finding below is a case those tests do not ask about.

**Could not reach.** A Director push does not exist on main (`SessionManager.SessionEventSink` is the
`LoggingSessionEventSink`, which "sends nothing anywhere"; the push is pull request #4090, open), so nothing
in this review rests on events really travelling from a Director to this Gateway: every ingest statement is
from the Gateway's tests, which drive the ingest directly, and from reading. A team key on the hub connection
(no test in the repository sets one; the per-pull-request review of #4082 recorded the same gap). A real
PostgreSQL. The hosted front end. A browser's `EventSource`. Phase 4's code.

## What section 5 and phase 2's row ask for, and what I found

- **Connection mode and the wheel.** `ConnectionMode` and `WheelHolder` are facts the Director reports on the
  row (`ControlEndpoints.Map`); the Gateway reads them in exactly two places, `SessionWheelState.Read` for the
  verbs and `ApiModeSessionFold` for the verdict, and nothing else branches on them. A row from a Director too
  old to send them reads `terminal` and `none`. Holds.
- **A receipt per prompt.** The ingest writes `PromptQueued`, `PromptDelivered`, `PromptRefused`, `TurnStarted`
  and `TurnEnded` to the store before the buffer hands the event on, keyed by tenant, session and prompt id,
  with the acknowledgement and the reasons verbatim. Holds, except for the retry case in finding 1.
- **Working or idle with the turn number; cancelled and failed endings.** The fold counts turns from the stored
  receipts in the order their first prompt arrived, calls the latest turn open while no receipt it carried has
  an outcome, and says "Working - turn N" only when the Director also reports the protocol process holds the
  conversation; otherwise it says "Not known" rather than guess. The last turn's `failed` and `cancelled`
  endings are worded on the row and, by rule 5, beside Delivered on the receipt ("Delivered - the turn failed:
  reason", "Delivered - stopped"). Holds, except that a turn whose only prompt is in the state of finding 1 is
  never counted.
- **The queue.** The count and the Director's reason, grouped when the reasons differ. Holds.
- **Live relay.** The stream replays what is held after the client's cursor and then follows, under the one
  session lock, so there is no gap and no duplicate at attach; the poll gives the same items with a `next`
  cursor. Holds.
- **The wheel verb and its refusals.** Terminal mode, take while the user holds it, and hand back while the
  protocol holds it are refused on the Gateway with one sentence each and nothing sent; `none` goes to the
  Director for both verbs; the Director's own refusals pass through verbatim; a Director without phase 4
  answers "unknown verb", which no surface offers a button for yet. Holds.
- **The owner's ruling: an agent may use the wheel and Stop only on a session it controls.** A session key
  reaches the three routes through the guard and is refused `not_your_session` (403, nothing sent, nothing
  about the session's state said) unless `OwnedSessionInput.OwnsDirectly` holds: the target's controller is the
  caller and the target is not the caller. A raised session is the owner's grant and is not held to it, as on
  the prompt route. The same rule, read from the same class, for all three verbs. Holds.
- **Rule 7, the client is dumb.** The row verdict (`ApiModeSessionDisplay`), every receipt (`Words`, `Tone`) and
  every live item carry finished sentences, status words and colour hints; `Kind` and `Tone` are documented as
  style keys. The receipts route and the live stream word a receipt through one static function, so the two
  readers cannot disagree. The verbs' refusals are one sentence each from the Gateway, and the Gateway chooses
  the Stop sentence by caller rather than leaving a client to. I found no state a client would have to
  interpret. Holds.

## The seams, traced

- **Ingest to store to buffer to readers.** One lock per session orders the generation decision, the durable
  write and the in-memory append and delivery; a thrown write leaves memory untouched so the same batch can be
  sent again; the store repeats the forward-only rule per row. The store is therefore always at or ahead of the
  buffer. Both readers are written for that: the fold reads the receipts for everything durable and the buffer
  only for the connection event; a live reader started from the receipts stays quiet on the replayed ending
  each receipt was built from (`_endingsFromStore`) and skips a turn outcome a receipt already carries. Holds.
- **Replay after a sequence number.** A same-generation cursor is continued only when it is at or above both
  the last trimmed sequence and one below the first sequence this process held; otherwise a reset. A continued
  reader whose held window does not reach the generation's first event asks `Remembers` before each event and
  is reset at the first event that builds on something let go. Holds.
- **Replay after a generation change.** A later generation replaces the session's held events and counts from
  one; a reader following the stream is reset with `new_generation` and rebuilt from the stored receipts; the
  store takes any event of a later generation as newer whatever its sequence; an older generation is refused
  whole. Holds. (The Director push in #4090 also starts a new generation after a hole it could not send, inside
  one process, counting the sequence again from one; the Gateway's rule covers that the same way, and turn ids
  keep rising in that process, so the store's turn linkage is unaffected.)
- **A Gateway restart mid-turn.** The buffer starts empty and the first batch it holds starts above one, so
  every cursor from before the restart is reset and no-cursor readers start from the receipts; the receipts
  carry the open turn, so the row says "Working - turn N" while the Director reports the protocol holds the
  conversation. That verdict is right as long as the Director re-sends what the Gateway never acknowledged
  (observation B of the display-fold review). #4090's description says it holds every event until a watermark
  covers it and re-sends everything held once per new connection; I read that, I did not run it. See
  observation B below for what the restart costs the row even then.
- **The prompt ids the Director sends.** A prompt from the Gateway carries its delivery id; any other prompt
  gets a new GUID; a Gateway id still waiting for its receipt is refused in words; a Gateway id whose earlier
  attempt was refused is accepted again and logged as a retry. That last rule is where phase 1 and phase 2
  disagree: finding 1.

## Findings

### Finding 1 (must be answered before phase 3 reads receipts). A retried delivery reuses its Gateway id, the Director says the later receipt stands, and the Gateway keeps the first: the retry's "Delivered" is refused as a Director defect and its turn is invisible to the verdict

**The two contracts.** `event-model.md`, section 2, "What a `promptId` is" (the Delivery Lead's ruling and the
phase 1 integration review, finding 3): "a retry of a delivery that was not delivered reuses its Gateway id
exactly, so the same `promptId` can carry a `PromptRefused` and then a later receipt, and **the later receipt
stands** (read the latest receipt per `promptId`, in `sequence` order)". The Director is built to it:
`ApiModeSessionHost.ResolvePromptIdLocked` accepts a Gateway id that was used before once its receipt has
settled, logs "sent again (a retry of a delivery that was not delivered)", and its own summary says "the
retry's receipt is the later one under the same id, and the later receipt stands".

The Gateway is built to the opposite. `ApiModeReceiptStore.RecordDelivered`, when the row's state is not
`Queued`: "REFUSED, the receipt is already Refused (one ending per prompt, event model rule 1); the first
ending stands", and nothing is written. `RecordRefused` does the same after a Refused. `ApiModeLiveRuling.IsFinal`
keeps the first ending for the live stream, counts the second as a Director defect and logs it. The per-pull-request
review of #4082 checked rule 1 ("one `PromptDelivered` or one `PromptRefused` per prompt written to the
agent") and did not read the retry paragraph; rule 1 is per prompt written, and a retry is a second write.

**The path that produces it, on main.** The Gateway's prompt route sends with a delivery id; the Director's
`Session.SendApiModePromptAsync` turns the host's refusal into `TextSendOutcome.Refused`, and
`SessionCommandExecutor.SendRecordedDeliveryAsync` marks the delivery not delivered. The Gateway's own
re-press (`ClaimedSendCore`, Gateway-driven: "the words are not in" under the limit is held as retrying and
sent again on the next attempt) presses the same delivery id again. The Director writes the prompt to Pi
under that id; Pi acknowledges; `PromptDelivered` goes up with the same `promptId` as the earlier
`PromptRefused`. One concrete trigger: the first press lands while the host is restarting Pi after the
turn-start bound (`PromptRefused`, `refusedBy: director`, "Pi is not running: ..."); the re-press a few
seconds later is acknowledged.

**The harm, proven.** Two throwaway tests, written to assert the event model's rule, run against the merged
code, both FAILED:

1. Store: `RecordRefused("gw-1", "Pi is not running: the process exited", director, generation a, sequence 1)`
   then `RecordDelivered("gw-1", started, ack, generation a, sequence 5)`. The second call answered false and
   the one stored receipt is `Refused` (the assertion message was "the retry's delivery was refused by the
   store").
2. Live ruling: `Rule(PromptRefused gw-1, sequence 1)` then `Rule(PromptDelivered gw-1, sequence 5)`: the
   second answered no item at all (`Assert.Single` on an empty collection), so a client watching the stream
   keeps "Refused: Pi is not running".

What a user then sees, for the ninety-day retention: the receipts route and the Chat's receipt item say
"Refused: Pi is not running: ..." for a prompt Pi acknowledged and acted on, which is the one thing the
mission's goal forbids ("Delivered ... or a stated reason why not. Never a silent loss" - here the stated
reason is false). It spreads to the row: the retry's `TurnStarted` names a prompt whose receipt is Refused,
so `RecordTurnStarted` does not link it ("is Refused, not Delivered; it is not linked"); `RecordTurnEnded` then
finds "no receipt is carried by this turn". The fold counts turns from linked receipts only, so while that
turn runs the row says "Idle" with the previous turn number, the turn number stays one short for the rest of
the session, and if that turn fails or is stopped its ending is shown on no receipt and on no row. The
Gateway log, meanwhile, records each such retry as "a second ending ... the first stands", the line the
#4083 review asked to keep meaningful for real Director defects.

**What makes it right (the Developer decides; the Delivery Lead decides which contract is the contract).**
Either the Gateway takes the event model as written: `Delivered` is final, `Refused` is not, so a later
`PromptDelivered` or `PromptRefused` at a higher position than a `Refused` row replaces it (the position rule
already refuses a re-sent duplicate, so idempotency is kept), with `ApiModeLiveRuling.IsFinal` matching and the
"second ending" line kept for an ending after `Delivered`; and `RecordTurnStarted` is unchanged because the
retry's delivery now precedes its turn. Or the Delivery Lead reverses the phase 1 ruling so a retry carries a
new id, and the Director changes. The first keeps the receipt joined to the prompt the user sent, which was
the reason for the ruling. Phase 3 should not read receipts until one of the two is done, because its tests
will otherwise pin the wrong words.

### Finding 2 (low; becomes real when phase 4 merges). The fold prefers a held "Agent disconnected" reason to a wheel holder of `none`, so a terminal the user closed is reported as the user holding the wheel

**Where.** `ApiModeSessionFold.Agent`: the latest held `ConnectionReady` or `ConnectionEnded` is read first, and
a `ConnectionEnded` answers "Agent disconnected: " plus its reason for any holder but `user`; the check for
`WheelHolders.None` ("No agent process is running") comes after it.

**The state that reaches it.** #4094's description: when the user takes the wheel the stream shows
`ConnectionEnded` with the reason "the user took the wheel", then nothing from Pi; when the user's terminal
exits on its own, `WheelHolder` becomes `none`, Pi is not restarted, and no further event is written. The
fold is pure, so its answer for that input is certain by reading: kind `stopped`, agent label
"Agent disconnected: the user took the wheel".

**The harm.** The row tells the user a terminal is open on the conversation when none is, and nothing holds it.
The user who reads it waits for a terminal that is not there; the one verb that would reopen it (take) is sent
to the Director, which is right, but the sentence that led there is false. Phase 4 is not merged, so this is
not on a user's screen today; it will be the day #4094 merges unless the fold checks `none` before the held
event, or phase 4 writes a `ConnectionEnded` when the user's terminal exits (the event model's `ConnectionEnded`
is for the agent process, and the terminal is one). The seat integrating phase 4 decides which.

Developer's answer: **Accepted, fixed in the Gateway** (pull request #4174, revised after its review,
`phase-2b-holder-none-label-review.md`). The Tech Lead ruled: a wheel holder of `none` is the Director's current
report and wins, so it comes first; a held `ConnectionEnded` is still true history and follows it. A holder of `none`
covers two states, and the rule is the same for both:

- **The user's terminal exiting.** The user took the wheel (the disconnect "the user took the wheel" is held), then
  their terminal exited and the Director reports `none`. The row reads "No agent process is running. The last
  connection ended: the user took the wheel" - no longer telling the user a terminal is open.
- **Pi's own process exiting**, with or without a turn open. The Director writes `ConnectionEnded` with the exit
  reason and reports `none`. The row reads "No agent process is running. The last connection ended: Pi exited (exit
  code 137)". The held ending is the only place the exit reason shows on the row when no turn was open, so it is
  never dropped.

With no `ConnectionEnded` as the latest held connection event, the label is "No agent process is running" alone.
Kind stays `stopped`. The `user` and `protocol` paths are unchanged. Tests in `ApiModeSessionFoldTests.cs`:
`Fold_AHeldDisconnectWhenNoProcessHoldsTheWheel_SaysNoAgentProcessIsRunningThenTheLastEnding` (the terminal state),
`Fold_AHeldReadyWhenNoProcessHoldsTheWheel_SaysNoAgentProcessIsRunningWithNoEnding` (a held `ConnectionReady` adds no
ending), `Fold_AHeldDisconnectWhileTheProtocolHoldsTheWheel_SaysTheAgentDisconnected` (holder `protocol` unchanged),
and, for Pi's own exit, `Stamp_RefusedBeforeAcknowledgement_NoTurnAndTheAgentIsGone` and
`Stamp_FailedMidTurn_SaysTheLastTurnFailedWithItsReason` in `ApiModeSessionVerdictTests`, each asserting the exit
reason with its exit code still appears.

## Observations (not findings: no harm proven on main, recorded so the next seat need not rediscover them)

- **A. No producer on main.** Nothing on main pushes an API-mode event to the Gateway; the Director's sink is the
  log. The push is pull request #4090 (open, phase 4). Until it merges, every Gateway surface of phase 2 answers
  from an empty buffer and an empty store for a real session, and phase 3 cannot see a live item from a real
  Pi. Phase 6's check item 1 ("Assert a Delivered receipt") depends on it.
- **B. After every Gateway restart the agent label on every API-mode row reads "Not known" for the rest of the
  generation.** The connection event lives only in the buffer; #4090 re-sends unacknowledged events, and the
  `ConnectionReady` at the start of a long generation was acknowledged long before. Honest, and the tone stays
  neutral, but "Agent connected (model)" will not come back on the roster until the next connection event,
  which for a quiet session is the next Director restart. If the roster should recover it, the fix is to keep
  the latest connection event durably or to have the Director re-send it on a new connection.
- **C. Turn ids are unique per Director process; the store's turn linkage assumes unique per session.** The
  store links and closes turns by (session, turn id) with no generation; `SessionEventNumbering` starts turn ids
  again at `turn-1` in every new `SessionEventStream`, which is every Director process. A throwaway test proved
  what would follow if one session id ever carried two generations from two processes: generation a delivers
  `p1` and starts `turn-1`; generation b (later) delivers `p2`, starts `turn-1` and ends it `done`; `p1`'s
  receipt took `done` from the wrong turn and the fold counted one turn, not two (the assertion `Expected: 2,
  Actual: 1` is the one that failed). It is not reachable today: no path keeps a DevThrottle session id across a
  Director process (`NewSessionRequest` has no field to preassign one; a Smart Restart and a crash continuation
  each mint a new id and resume Pi's conversation under it), and #4090's hole generations stay inside one
  process. Recorded because the store's rule rests on that fact, which nothing in the contracts states. A turn
  id that carries the generation, or a store keyed by both, would close it for good.
- **D. The display-fold review's observation B is answered by #4090's design**, as far as its description and
  review text say: every event is held until a watermark covers it, and everything held is re-sent once per new
  connection. I did not run it.
