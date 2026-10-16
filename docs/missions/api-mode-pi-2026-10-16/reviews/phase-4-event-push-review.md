# Phase 4 review: the Director pushes its API-mode events to the Gateway

Pull request #4090, head commit `c4a8c3892`, reviewed on 16 October 2026 by the Reviewer seat the Tech Lead
opened. I read; I changed nothing.

## Scope

**What I read, at the pull request's head and on origin/main:**

- The diff of the pull request against origin/main, whole: `ApiModeEventPusher.cs` (new), the wiring in
  `ControlApiHost.cs` (start, the settings change, the stop), `GatewayStreamClient.PushApiModeEventsAsync`
  (both overloads and the key gate), `DirectorHubCalls.cs`, `ForwardingSessionEventSink` and the sink
  comments in `SessionEventStream.cs`, the one line in `SessionManager.cs`, the row in
  `docs/BackgroundWork.md`, and the three test files.
- The contract: the summaries of `ApiModeEventBatch` and `ApiModeEventWatermark` in
  `DirectorUpStreamMessages.cs`, and `DirectorStreamLimits` for the tunnel's inbound message size.
- The Gateway side the pusher talks to: `DirectorHub.PushApiModeEvents` and the capability list its Hello
  answers, `ApiModeEventIngest.Accept` and `Parse`, `ApiModeEventBuffer.Apply`, `Trim` and `ReplayOf`, and
  `IApiModeEventFeed`.
- What calls the sink: `ApiModeSessionHost.TakeEventsLocked` (the publish runs under the session host's lock)
  and `SessionEventStream.Publish`.
- What the pusher is modelled on: `TurnPusher` (its run loop, its catch blocks, its dispose) and the stream
  client's reseed, Hello callback, re-push cadence and `SessionIdsMayGoUp` gate.
- `BackgroundJobs` and `BackgroundJob`, to confirm the sweep awaits the task it is given and never overlaps
  itself.
- The mission's `event-model.md` and the phase 2 receipts-and-ingest review (its findings on the Gateway
  restart and on reading the empty-generation answer are what this pull request builds on).

**What I ran, on devlinux (Linux):**

| Suite and filter | Result |
|---|---|
| `Gateway.UnitTests`, `ApiModeEventPusherTests` and `DirectorKeysBeforeRosterTests` | 13 passed, 0 failed |
| `Avalonia.Tests`, `DirectorKeysBeforeRosterTests` and `SecretTransferMachineKeyTests` | 30 passed, 0 failed |
| `Core.UnitTests`, whole | 2,099 passed, 6 failed, 23 skipped |

The six Core failures are the ones the brief and the pull request name: five Windows drive-path tests and
`CatchLogLineScanTests.EveryLogLineInACatch_IsReportedOrSaysWhyNot`. I checked that the scan failure names
only two lines in `CatalogReadExecutor.cs`, which this branch does not touch; it names nothing in the
pusher, the stream client or the host.

**What I could not reach:** no real Gateway, tunnel or Pi process was run, so the hub call over a real
connection (serialisation of a 4,000,000-character batch, the real `HubException` path) is read, not
observed. I did not run the full `Gateway.UnitTests` suite, only the two classes above.

## Verdict

The pull request does what it says on the paths the brief listed. Every event is held until a watermark
covers it; batches are one generation each and bounded by count and by characters; the four answers are told
apart exactly as the contract reads, including an empty generation from null; a hub exception keeps the
events; a new Director generation goes in its own batch from sequence 1; nothing is pushed while the Gateway
does not advertise `PushApiModeEvents`; terminal sessions never reach the pusher, because only the API-mode
backend is given an event stream; and the per-session run cannot overlap itself, loses no wakeup and cannot
spin on an answer that moves it nowhere. I found no path on which an event is lost or reordered that the
pull request does not already name (the memory bound, an event too large for any batch, and a Director
restart, which loses what was held in memory and is a new generation by contract).

Two findings need the Developer's answer before this is called done; neither is a lost event. The first is
a push storm the pull request's own comment says cannot happen. The second is that the memory-bound drop the
pull request promises "the Gateway will see" is invisible to the Gateway, so the honest log line on the
Director is the only record. A third, smaller, is a gap in the proof.

## Findings

### 1. Every Hello re-sends everything held, and a Hello happens every ten seconds, so a Gateway that keeps refusing receives the same batch six times a minute

**Where:** `ControlApiHost.cs`, the `onHello` callback: `_ = _apiModeEventPusher?.ResendAllAsync();` with the
comment "storing is idempotent, so the regular re-push's Hello costs nothing"; `ApiModeEventPusher.cs`, the
summary's TRIGGERS paragraph.

**What happens:** the stream client sends a Hello on every re-push, and the re-push cadence is
`DefaultStreamStaleAfterSeconds / 2`, ten seconds (`GatewayStreamClient` line 242, `GatewayConfig` line 118).
The reseed hands every Hello's answer to `_onHello` (`GatewayStreamClient` line 1069), not only the first
on a connection. So `ResendAllAsync` runs every ten seconds for the life of the connection.

That costs nothing only while nothing is held. Whenever the Gateway stops a run and the events stay held,
the same oldest batch, up to 500 events or 4,000,000 characters, goes up again every ten seconds, per
session, until the Gateway stores it. The cases in which the Gateway is connected and still stops the run:

- refused for now (in a team, this Director is not yet the session's only holder): `DirectorHub` line 659,
  and the hub logs the refusal every time (line 757);
- the ingest's store throws (the receipt database unreachable while the tunnel is up): `Accept` throws past
  the hub's `catch (ArgumentException)`, the Director sees a `HubException`, and the pusher holds the batch;
- the two defensive branches (a watermark of another generation, a watermark that covers none).

**The harm:** a Gateway whose database is down for ten minutes receives 60 copies of a batch that can be
4,000,000 characters, per API-mode session, with three log lines per copy (the stream client's entry line,
the pusher's "held" line, the Gateway's refusal line): about 240 megabytes of upload per session and 180
lines of log that say the same thing, in a case the one-minute sweep already covers at a sixth of the cost.
The turn pusher, which the pull request says it is modelled on, avoids exactly this: its Hello callback
re-seeds only when `TurnWatermarksKnown`, which the hub answers true once per connection (`DirectorHub`
lines 536 to 547), so the ten-second Hello does not re-push turns. The event pusher has no such gate and
cannot tell a connection's first Hello from its repeat.

**Why it must change:** the comment in the host states a cost of nothing, and the design rests on it; the
cost is bounded only by how long the Gateway keeps refusing. The brief's third question asked for no push
storm, and this is one.

Developer's answer:

### 2. A memory-bound drop in the middle of a generation is invisible to the Gateway, so the log line's promise that "the Gateway will see a gap" does not hold, and readers are told the stream is complete

**Where:** `ApiModeEventPusher.EnforceBoundLocked` (the log line ends "the Gateway will see a gap") and
`TakeBatch` (the too-large drop, "the Gateway will see a gap at this sequence"); the Gateway's
`ApiModeEventBuffer.Apply` and `ReplayOf`.

**What the Gateway does with the batch after a drop.** `Apply` takes as new every event whose sequence is
above `HighestSequence` (`floor`) and stores them; it never checks that the first new sequence is
`HighestSequence + 1`. The only two marks of a hole it keeps are `FirstHeldSequence` (set once, when the
generation is first held in this process) and `EvictedThrough` (moved only by the Gateway's own `Trim`).
So:

- If the Gateway has not yet held the generation (a long outage from the generation's start), the first
  batch after the drop begins above 1, `FirstHeldSequence` records it, and `ReplayOf` answers
  `Complete: false`. Honest.
- If the Gateway had acknowledged up to sequence `h`, and the Director then dropped `h+1 .. k` (20,000
  events or 50,000,000 characters published while the Gateway was unreachable, or a single event over
  4,000,000 characters), the next batch from `k+1` is stored as if it followed `h`. `HighestSequence` jumps
  from `h` to the batch's last sequence; neither mark moves. A reader whose cursor is `(generation, h)` is
  answered the events from `k+1` with `Complete: true`.

**What the fold shows.** The receipts are fed from the same `fresh` list (`ApiModeEventIngest.Accept`), so an
event in the hole never reaches them. If a `TurnEnded` fell in the hole, the turn stays "Working - turn N"
on every screen with nothing to end it; if a `TurnStarted` fell in it, the turn is never shown started and
its prompts keep their queued receipts. Nothing on the Gateway side can tell this from a turn that is simply
still running. The Director's log is the only record, which is what the pull request's wording "nothing is
dropped without saying so" delivers, but the next sentence, that the Gateway will see a gap, is true in the
first case and false in the second.

**Is dropping the oldest safe under the Gateway's rules?** Yes, in the sense the brief asked: it never
makes the Gateway refuse or move backwards, since the Gateway only refuses an older generation and only
ignores sequences at or below its highest, and keeping the newest is the right choice for a fold that wants
the latest turn. What is not safe is the silence afterwards. The same silent hole arises on the settings
path with nothing dropped at all: when the Gateway is turned off, `UseApiModeEventSink` points running
sessions at the logging sink while the pusher keeps what it held; when it is turned on again, the held
older events go up and the later ones continue in the same generation, with the events published in
between in the Director's log only.

**Why it must change:** the pull request's own claim is that a gap the Gateway will see is always
explained; the Gateway sees no gap, and its readers are told the opposite. Where the record of the hole
belongs, the Gateway (marking a stored batch whose first new sequence is above `HighestSequence + 1`, the
way `FirstHeldSequence` already marks a late start) or the Director, is the Developer's decision; the log
line should say what is true either way.

Developer's answer:

### 3. The concurrency claims are proven by reading only: no test holds a push open and publishes during it

**Where:** `ApiModeEventPusherTests.cs`.

**What is missing:** the pull request claims "runs for one session never overlap" and the pusher's summary
says a trigger during a run is marked pending and consumed by the runner before it leaves. Every test's fake
Gateway answers synchronously, so no run is ever in flight when a second trigger arrives, and
`ResendAllAsync` is never exercised over more than one session. The hand-off (`Pending` set under the lock
by `PushSessionAsync` while `Running`, read under the lock by the runner at the top of its loop) reads
correct to me, and the no-progress and short-watermark stops are tested. But the path the brief's third
question is about is not under test, and it can be, with the fakes the file already uses: a fake whose
`Push` returns a `TaskCompletionSource` task, a publish while it is open, then completion, asserting a
second batch goes up in the same run and nothing waits for the sweep.

**The harm:** a regression in the hand-off (say, clearing `Pending` after the send rather than before)
would pass this suite, and in production would show only as events that wait up to ten seconds for the
next Hello, which nobody would trace to the pusher. The harm is bounded by finding 1's cadence, which is why
this is the smallest finding; it stays a finding because the pull request puts the claim in writing.

Developer's answer:

## Notes that need no change, recorded so the next seat need not re-derive them

- **The answer handling matches the contract exactly.** `answer is null` is tested before
  `string.IsNullOrEmpty(answer.Generation)`, so refused-for-good and refused-for-now cannot be confused;
  `PushSessionAsync_RefusedForNow_KeepsTheEventsAndSendsTheSameBatchAgain` proves the batch is identical
  on the second send. The pull request's note that treating refused-for-now as stored passed every test
  before the dedicated test existed is a fair account of why that test is there.
- **The capability gate is the Gateway's own list.** `canPush` reads `GatewaySupports("PushApiModeEvents")`
  and the hub removes that method from its Hello answer when it has no ingest (`DirectorHub` line 532), so a
  Gateway without a store is never pushed at. With this Gateway, the watermark's generation always equals
  the batch's when it is not null, and `HighestSequence` is always the batch's last stored sequence, so the
  "another generation" and "covers none" branches are defensive; they stop the run rather than guess, which
  is the right shape for a branch that should never fire.
- **The hub call starts on the session's event loop, under its lock.** `TakeEventsLocked` calls the sink
  while the host holds its lock; `Publish` serialises every event to JSON there, and `Trigger` runs
  `PushSessionAsync` synchronously up to the first real await, which is inside `HubConnection.InvokeAsync`
  after the batch is serialised and written to the transport. For a 4,000,000-character batch that is
  measurable work on the loop that reads Pi's next event. I have no measurement, so this is not a finding;
  the turn pusher hands its follow-up rounds to `Task.Run`, and the Developer may want the same here.
- **The stop path.** The host swaps the sink to the logging one before disposing the pusher, so a publish
  after `_stopping.Dispose()` would need to have read the old sink first; the window is a few microseconds
  and the turn pusher has the same shape. Not a finding.
- **A session that ends with events held.** Nothing removes a session's state at its end; the Hello and the
  sweep keep sending until the Gateway stores or refuses for good, and the state retires itself once empty.
  In a team, a session another Director keyed ends in null and is dropped with a log line. Within the bound,
  and correct.
- **The two Avalonia test files changed** because `IDirectorHubCalls` gained `PushApiModeEventsAsync` and
  both files hold a fake that implements it; `DirectorKeysBeforeRosterTests` also gained the two tests that
  prove the push waits for the connection's keys, in the same way as the turn push.
- **The rules.** Try-catch: the catch around `_push` in `SendHeldAsync` is in a helper, but the exception is
  one of the contract's four answers and the turn pusher's round catches in the same place; the catch in
  `PushSessionAsync` is the entry point of the fire-and-forget. No fallback: every unexpected answer is
  logged and stops the run; nothing is substituted. Logging: every outcome of a run is logged with the
  session, generation and sequences; `Publish`, `Trigger` and `HeldCount` do not log entry, which on the
  hot path is the right call. Plain English: the code and comments read as sentences; the abbreviations
  present are the contract's own field names.
- **Tests are fakes only:** one in-memory fake Gateway, no hub, port, process, clock or sleep; every test is
  deterministic because the capability is off while events are published, so the fire-and-forget trigger
  never pushes. Names follow `MethodName_Scenario_ExpectedResult`.
