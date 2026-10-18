# Phase 2 follow-on review: the live session push carries the wheel action (pull request #4184)

Mission: API mode for agent wrappers, proven on Pi (issue #4070), phase 2 follow-on. Reviewer seat, opened
by the Delivery Lead, a different agent family from the Developer. Reviewed at the pull request head
`822097e8b` on the branch `api-mode-wheel-action-live`, one commit ahead of `origin/main` (`30556737f`),
in its own worktree on the Linux machine. Date: 17 October 2026. The reviewer read and ran; it built
nothing of its own and fixed nothing.

## Verdict

**Approve.** The push stamps the API-mode verdict through the same `ApiModeRowStamp` call, from the same
`ApiModeDisplayFacts` object and the same row facts the roster route uses, so there is one fold and no
second one. The payload field is additive and nullable, and the proof that an older Director still reads
the request goes through the Director's real deserialiser. The change signature carries the whole verdict
and the verdict holds nothing that moves between events, so it cannot flood a Director. The per-session
re-push after an accepted event batch runs after the receipts are written, pushes only that session, and
pushes nothing on either kind of refusal. Four findings, none of them a defect that should hold the merge:
one about cost, one about what two of the new tests prove, one about logging, one about how the diff reads
on GitHub.

## Scope: what was read

- The whole diff of the pull request against `origin/main` (seven files), including the text diff of
  `FleetDisplayStateObserver.cs`, which git shows as binary (finding 4).
- Gateway: `Fleet/DisplayFold.cs` in full; `Fleet/FleetDisplayStateObserver.cs` in full (`Sweep`,
  `PushSessionAsync`, `BuildRequest`, `Signature`, `SendDisplayStateAsync`, the gate); `GatewayHost.cs` where
  `_apiModeDisplayFacts` is built, handed to the endpoint options and to the fold, and where the sweep timer
  and the hub are wired; `Api/GatewayEndpoints.cs`, `StampFleetRolesAndFold` in full and both roster call
  sites; `Api/DirectorCommandRouter.cs`, `TrySendAsync`; `GatewayHost.SendCommandAsync`; `Streaming/DirectorHub.cs`,
  `PushApiModeEvents` in full and `EnterBoundTenantScope`; `Tenancy/ITenantPass.cs`, both implementations of
  `Current`; `ApiModeDisplay/ApiModeRowStamp.cs`, `ApiModeVerdictReceipts.cs` and the head of
  `ApiModeSessionFold.cs`; `ApiModeEvents/ApiModeEventIngest.cs`, `Accept` and `Parse`; the contract classes
  `SetDisplayStateRequest`, `ApiModeSessionDisplay`, `ApiModeWheelAction` and the batch contract in
  `DirectorUpStreamMessages.cs`.
- Director, for the body's pointers: `FleetDisplayStateExecutor.cs`, `SetDisplayState`;
  `SessionCommandExecutor.Deserialize` and its options; `Session.ApplyGatewayDisplayState`;
  `ControlEndpoints.Map` at the inbox line.
- Web, for the body's channel table: `rosterStore.ts`, `fleetClient.ts`, the `GET /events` writer in
  `GatewayEndpoints.cs`, the two `MapHub` lines, and a search for every reader of `wheelAction` under
  `packages/` and `apps/`.
- The tests: `ApiModeWheelOnTheDisplayPushTests.cs` in full, the diff of `PushApiModeEventsHubTests.cs`, and
  `DisplayFoldTraceMatchesPushTests.cs` in full.

## Scope: what was run

One filtered run of the parked `Gateway.UnitTests` project on this machine, after confirming no other test
host was running, with `DOTNET_USE_POLLING_FILE_WATCHER=1`:

| Class | Cases in the file | Result |
|---|---|---|
| `ApiModeWheelOnTheDisplayPushTests` (new) | 6 | passed |
| `PushApiModeEventsHubTests` (2 added) | 8 | passed |
| `FleetDisplayStateObserverTests` | 11 | passed |
| `DisplayFoldTraceMatchesPushTests` | 2 | passed |
| `DisplayPushVoiceEnrichmentTests` | 6 | passed |
| `SessionCommandExecutorTests` | 69 | passed |
| **Total** | **102** | **102 passed, 0 failed, 13.9 seconds** |

The total reconciles with the `[Fact]` count of the six files, so every class the filter named ran, the
new one included.

## Scope: what was not reached

- The Director's side is not in this pull request, and the Director's event pusher (the caller of
  `PushApiModeEvents`) is not on `origin/main`: only Gateway code names that hub method there. So the rate at
  which batches arrive, and therefore the real cost of finding 1, could not be measured or read.
- No running Gateway or Director. The wire was exercised only through the recording sender in the tests,
  which deserialises the payload with the web defaults exactly as the Director does.
- Whether the tests fail without the change was judged by reading, not by a mutation run (the brief allows
  two runs; the second was kept for a re-run that turned out not to be needed). The reading is in finding 2.
- The Windows gate (`scripts\test-local.ps1`) was not run here.

## Finding 1: every accepted event batch re-folds the whole fleet and runs one receipt query, even a batch that stored nothing

**Severity: low. Cost, not correctness.**

**Where.** `src/CcDirector.Gateway/Streaming/DirectorHub.cs`, `PushApiModeEvents`, the call to
`PushApiModeSessionDisplayAsync`, and `src/CcDirector.Gateway/Fleet/FleetDisplayStateObserver.cs`,
`PushSessionAsync` lines 243 to 246, which take the whole fleet snapshot and stamp every row before looking
for the one session.

**The state that reaches it.** A Director re-sends a batch the Gateway already holds (same generation,
sequences at or below the highest held). `ApiModeEventIngest.Accept` stores nothing and still answers a
watermark with the generation filled in, so the hub's `mark is { Generation.Length: > 0 }` test is true and
the re-push runs: one `PushedSessionStore` snapshot, one fold of every session in the tenant, and inside it one
`ApiModeVerdictReceipts.Read` database query (and its log line). The gate then finds the signature unchanged
and sends nothing.

**The harm.** Work with no visible effect, once per batch, at whatever rate the Director's pusher sends
batches. It is the same order of cost as the existing path: the hub's row push already re-folds the whole
fleet on every delta (`Observe` calls `Sweep`), and `PushSessionAsync` has always folded the whole fleet
because a role resolved across the fleet can change any row. Tenants with no API-mode rows pay nothing
extra, because `ApiModeRowStamp.Stamp` returns before the read when it finds no API-mode row. The pull
request's design decision (one fold, the real one, not a cheaper second one) is the right one under rule 7,
and the brief's question about cost is answered: the cost is one more full fold per accepted batch. If the
Director's pusher turns out to send a batch per event rather than per flush, the number to watch is the
`[ApiModeVerdictReceipts] Read` line rate in the Gateway log. Nothing to change in this pull request; the
Director's Developer should know the Gateway folds once per accepted batch when choosing the batch cadence.

## Finding 2: two of the six new tests pass with or without the change

**Severity: low. A test that proves less than its place in the list suggests.**

**Where.** `src/CcDirector.Gateway.UnitTests/ApiModeDisplay/ApiModeWheelOnTheDisplayPushTests.cs`,
`Record_AnApiModeSession_StampsNoApiModeVerdict` and `Push_WithNoAccountInScope_StampsNoApiModeVerdict`.

**The state that reaches it.** Both assert that `ApiMode` is null. Before this pull request the push stamped no
verdict on any row, so both assertions were true on `origin/main` as well. They guard the two rulings (a trace
stamps none, no account in scope stamps none) against a later change, which is worth having, but they do not
show this change did anything.

**The harm.** None to the product. The other four tests in the class, and the two added hub tests, do fail
without the change, by reading: `Push_AnApiModeSession_CarriesTheWheelActionTheRosterServes` asserts the pushed
verdict is not null; `Sweep_TheWheelChangesHands_PushesTheNewWheelAction` asserts two sends, where without the
signature change the third sweep would send nothing (colour, label and bucket do not move when the wheel
changes hands) and without the `BuildRequest` copy the first send would carry a null verb;
`Sweep_ATerminalModeSession_SendsNoApiModeVerdict` passes either way but is a terminal-mode guard, which is its
stated purpose; `Deserialize_ARequestCarryingApiMode_IsReadByADirectorBuiltBeforeIt` needs the new field to
exist to compile. `PushApiModeEvents_AfterIngest_PushesThatSessionsDisplayStateAndNoOther` asserts one send to
the right session, where the old hub sends nothing. So the change is proven; two of the eight are guards and
the body's sentence "6 new" reads as if all six prove it. Accepted as written.

## Finding 3: a successful re-push leaves its trace only in the router's line

**Severity: low. Logging shape against the project rule.**

**Where.** `src/CcDirector.Gateway/Streaming/DirectorHub.cs`, `PushApiModeSessionDisplayAsync`, which logs only
its failure; `src/CcDirector.Gateway/Fleet/FleetDisplayStateObserver.cs`, `PushSessionAsync`, which writes no
entry line and no line of its own on a send (pre-existing).

**The state that reaches it.** A turn ends, the batch is accepted, the re-push sends the new verdict. The Gateway
log shows the ingest's "stored N of N" line and then `[DirectorCommandRouter] set-display-state sid=...` with the
outcome, and nothing that says the send was the event-driven re-push rather than a sweep. When the desktop's
wheel button is late, the reader of the log cannot tell from the log which path sent the last fold, or that the
re-push ran and the gate stopped it.

**The harm.** Slower diagnosis of exactly the symptom this pull request exists to remove. A one-line entry
log in `PushApiModeSessionDisplayAsync` naming the session and that the gate was consulted would close it.
Not a merge blocker; the failure path is logged, and the error handling is right: the whole of
`PushSessionAsync`, including its synchronous fold, runs inside the private method's try, so a receipt read
that throws is written down and never reaches the Director's hub call.

## Finding 4: the observer's diff reads as "Binary files differ" on GitHub

**Severity: low. Not this pull request's doing, but it hides two of the three production changes from a reader of the pull request page.**

**Where.** `src/CcDirector.Gateway/Fleet/FleetDisplayStateObserver.cs`, line 68: the constant `DefaultScope`
carries a real NUL byte inside its string literal (the comment above it says "The NUL prefix keeps it distinct
from any real TenantId.Value"). That byte is on `origin/main` already; this pull request did not add it.

**The state that reaches it.** Git classifies the file as binary, so `git show`, `git diff` and the GitHub
pull request page print "Binary files differ" for the file that holds the `BuildRequest` copy and the signature
change. A reviewer reading the pull request on GitHub does not see those two edits unless they fetch the branch
and diff with `--text`.

**The harm.** A review done from the web page alone would have passed those two lines unread. Not for this
pull request to fix; worth a follow-up that writes the prefix as an escape (`"\0single"`) so the file diffs as
text again, with the behaviour unchanged.

## The brief's questions, answered one by one

**Are the facts truly the same object and the same values as the roster's?** Yes. `GatewayHost` builds
`_apiModeDisplayFacts` once (line 2193) and hands that one field both to the endpoint options the roster and
`GET /sessions/{sid}` read from (line 4962) and to the fold the push runs (line 2442, through the lambda). Both
routes reach the one stamp, `ApiModeRowStamp.Stamp`, inside `StampFleetRolesAndFold` (line 6586). The row
facts the fold reads (`ConnectionMode`, `WheelHolder`, and `IsExited` off the row) come from the same
`PushedSessionStore` on both paths: the roster reads the store's folded clones, the push reads `SnapshotFresh`
deep copies. The tenant is the request's on the roster and the ambient pass's on the push; both are the account
the rows belong to. The first test in the new class compares the two serialised verdicts for the same row and
they are equal.

**Tenant scoping: a push with no account in scope stamps none. Is that right, and can it null a field the
Director already shows?** It is right, and it cannot. On self-host `ITenantPass.Current` is always Local, so a
scope-less push does not exist there. On hosted, every production caller of `DisplayFold.Push` has a scope: the
timer sweep runs inside `ForEachTenant`, the hub's `Observe`, `ObserveSnapshot`, `ObserveRemoval` and the new
re-push all run inside `EnterBoundTenantScope`, and the hold endpoint's `PushSessionAsync` runs in the request
scope. If a scope-less fold ever did run, `GatewayHost.SendCommandAsync` reads `_tenantPass.Current` before its
first await and drops the command with a "GATEWAY BUG" line, so a null stamped by a scope-less fold cannot reach
a Director. `Record` (the trace) stamps none and sends nothing. The only null that reaches a Director is a real
one: the session is not in API mode.

**Signature churn.** `ApiModeSessionDisplay` holds no time, no counter and nothing read from a clock: its
fields are the mode, the wheel holder, the turn number and outcome from the receipts, the queue count and the
connection line from the held events, and the wheel button ruled from those. `ApiModeSessionFold` is documented
as pure and its source names no clock. `JsonSerializer.Serialize` with default options writes properties in
declaration order, so the same verdict signs the same every pass. A change to any field, the wheel button's
verb, label, enabled flag or disabled reason included, re-pushes once. No flood.

**The per-session re-push after ingest.** Ordering: `Accept` writes the receipts inside `_buffer.Apply` before
it returns, and the re-push runs after it returns, so the fold reads this batch's receipts and held events. The
fold and the gate read run synchronously before the first await, inside the hub's tenant scope, as the comment
says. Against the 5-second sweep: not serialised, and a descheduled re-push can land an older fold after a newer
one; the shared gate makes the next sweep or push repair it within the sweep interval, which is the documented
behaviour of `PushSessionAsync` already. A refused batch (`null`) pushes nothing, tested; a refused-for-now
batch (generation empty) pushes nothing, by the `Generation.Length: > 0` guard, not tested but read. Errors: a
private async method with a try at the entry point, the result discarded, as the other fire-and-forget pushes
on the hub do.

**Does the older-Director proof use the Director's real deserialiser path?** Yes. `FleetDisplayStateExecutor.SetDisplayState`
reads the payload with `SessionCommandExecutor.Deserialize<SetDisplayStateRequest>`, which is
`JsonSerializer.Deserialize` with `JsonSerializerDefaults.Web` and no unmapped-member rule, so an unknown property
is skipped. The test serialises a request carrying `apiMode` with the same web options the observer uses, then
reads it through that same static method into a class with exactly the eight fields an older Director's request
has, and checks each of them. The one thing it does not prove is that the older Director's executor then does the
right thing with the rest of the request, which it does today without reading `ApiMode` at all.

**Does the trace still match the push for colour and label?** Yes. `DisplayFoldTraceMatchesPushTests` passed
(both cases), and the new `Record` test shows the trace stamps no verdict. The verdict reads nothing the colour,
label or bucket read and they do not read it, so the trace's colour and label are unchanged by its absence.

**Test names.** All eight new tests are `Method_Scenario_Result`.

**Logging.** Finding 3.

**The Director-side pointers in the body.** All three are accurate on this head: `FleetDisplayStateExecutor.cs`
line 63 is the `ApplyGatewayDisplayState` call, `Session.cs` line 636 is `ApplyGatewayDisplayState`, and
`ControlEndpoints.cs` line 409 is `InboxLine = s.GatewayInboxLine`. One thing the Director's Developer should
not miss, which the body implies but does not say: `Session.ApplyGatewayDisplayState` fires its changed event
only when one of the fields it compares differs, so the new field must join that comparison or the rail will not
redraw when only the wheel button changed.

**The channel table in the body.** Checked and accurate: the roster poll is 2000 milliseconds
(`ROSTER_POLL_MS`), the path constant is at `fleetClient.ts` line 202, `GET /events` writes only
`director.added` and `director.removed`, the two hubs are `/director-stream` and `/launcher-stream`, and the
only file under `packages/` or `apps/` that names `wheelAction` is the generated `schema.ts`.

## Judged against, and what held

- **Rule 7, one fold.** No second fold was added. The push gained one more input to the one shared method,
  and `DisplayFoldTraceMatchesPushTests` still shows `EnrichVoiceThenFoldForPush` has exactly one caller.
- **The Tech Lead's rulings.** Same fold, same facts: held. Additive and nullable, an older Director accepts it:
  held, through the real deserialiser. The signature includes the whole wheel action: held. Only that session is
  re-pushed after ingest: held, tested. Push only, not the trace: held, tested.
- **No fallback programming.** With no facts or no account the stamp assigns null and claims nothing; nothing
  substitutes Local or a guessed verdict.
- **Try-catch at entry points only.** The one new catch is at the fire-and-forget boundary.
- **Plain English, no attribution.** The commit message and the pull request body carry none.

## Verdict

**Approve.** Merge as it stands. The four findings are for the record and for the Director's Developer, not
conditions on the merge.
