# Review 2768 - round two

Verdict: REQUEST CHANGES

Reviewed commit: `cf983fadfd1ac00bff1c3fee3279ca3222a10690`

Finding count: 0 HIGH, 5 MEDIUM, 2 LOW.

NO HIGH FINDINGS.

## Findings

### MEDIUM 1 - Caller cancellation is turned into a definitive dispatch failure and closes a request whose cycle may already be running

`DirectorRestartRequestService.SendBoundedAsync` correctly distinguishes its own timeout from the caller's cancellation: the filter at `src/CcDirector.Gateway/Api/DirectorRestartRequestService.cs:505-508` does not translate the exception when `ct` was cancelled. The caller does not actually let that cancellation propagate, though. `AcceptAsync`'s following `catch (Exception)` at `:356-360` catches the same `OperationCanceledException`, changes the request to `Abandoned`, and says the Director could not be told to begin.

SignalR cancellation means this caller stopped waiting; it does not establish that the command was never delivered. The Director may already have claimed and scheduled the cycle before the acknowledgement is lost. Closing the record then makes its later reports fail because `DirectorRestartRequestStore.Report` accepts reports only while the record is `Accepted` (`src/CcDirector.Gateway/Api/DirectorRestartRequestStore.cs:212-218`). The request can therefore say the Director was not told while the Director is draining or restarting. The helper's comment at `DirectorRestartRequestService.cs:495-496` claims the caller cancellation propagates as itself, but the code one frame above does not honor that claim. `AskEligibilityAsync` similarly catches caller cancellation as an ordinary inability to ask at `:459-465`, although that path does not mutate a record.

The timeout is no longer unbounded, and caller cancellation is not mislabeled as the 30-second timeout. This remaining arm is why round-one MEDIUM 4 is only partially closed.

### MEDIUM 2 - An `Ok` command result is accepted as `taken = true` without reading the acknowledgement

After the cycle command returns, `DirectorRestartRequestService.AcceptAsync` checks only `result.Status != Ok` at `src/CcDirector.Gateway/Api/DirectorRestartRequestService.cs:363-371`; it never reads `result.BodyJson`. Any `Ok` body, including no body, `{ "taken": false }`, or a different request id, reaches `:374-380` and is reported as "the Director has taken the cycle".

The current producer does the right thing: `ControlApiHost.StartRestartCycle` claims the gate before scheduling and writes `{ taken = true, requestId = ... }` at `src/CcDirector.ControlApi/ControlApiHost.cs:1020-1040`. But the receiver does not verify that answer. A half-updated or faulty Director can therefore leave the owner with a running request and block another request for three hours even though no cycle was taken. The tests supply only `taken = true`; there is no negative acknowledgement case.

The synchronous claim and the store's accepted-state exclusion close the concrete two-command race from round one, but the cross-process acknowledgement is still a binary `Ok/not Ok` reading of a richer answer. Round-one MEDIUM 2 is partially closed.

### MEDIUM 3 - Fire-and-forget drain progress can overwrite the report that is meant to be the old Director's last word

The new pre-ask report at `src/CcDirector.ControlApi/Restart/DirectorRestartCycle.cs:235-245` is awaited before the launcher is asked, which fixes the local ordering. Earlier drain progress is not awaited: the callback at `:199-201` starts `TryReportAsync` and discards its task. A slow earlier HTTP report can consequently arrive after the awaited pre-ask report. The store unconditionally replaces `Progress` for every accepted report at `src/CcDirector.Gateway/Api/DirectorRestartRequestStore.cs:220-225` and carries no source sequence with which to reject an older logical step.

If the launcher then stops the old Director, the durable last line can regress from "asking the launcher ... the next Director reports" to an earlier line such as "collecting". Three hours later the expiry text quotes that stale arrival as the Director's last word (`DirectorRestartRequestStore.cs:287-293`). The unit fake completes every report synchronously, so the ordering assertion at `src/CcDirector.Gateway.UnitTests/Restart/DirectorRestartCycleTests.cs:149-158` cannot expose this production race.

Round-one MEDIUM 3 is partially closed: the report was moved before the ask and the record now expires, but the report is not guaranteed to remain the last progress value.

### MEDIUM 4 - Accepting a running-expired request reports the old pending deadline as the expiry time

An accepted request now expires at `AcceptedAtUtc + RunningExpiry` in `src/CcDirector.Gateway/Api/DirectorRestartRequestStore.cs:287-293`, but its `ExpiresAtUtc` property remains the original 30-minute pending deadline. When a stale client attempts another accept after the three-hour running expiry, `Accept` returns the same `Expired` outcome and `DirectorRestartRequestService.AcceptAsync` says it "expired at" `request.ExpiresAtUtc` at `src/CcDirector.Gateway/Api/DirectorRestartRequestService.cs:279-287`.

The embedded request's `StateReason` has the accurate three-hour uncertainty text, but the top-level error the action displays names an earlier, unrelated time and describes an approval that outlived its request. This is a second partially closed part of round-one MEDIUM 3: list readers get the new uncertainty reason, while the accept reader gets the pending expiry answer.

### MEDIUM 5 - A running cycle is refused with a sentence saying it is still waiting for the owner's answer

`DirectorRestartRequestStore.TryCreate` now correctly treats both `Pending` and `Accepted` as open at `src/CcDirector.Gateway/Api/DirectorRestartRequestStore.cs:81-91`. `DirectorRestartRequestService.CreateAsync` still renders every refusal through the old pending-only response at `src/CcDirector.Gateway/Api/DirectorRestartRequestService.cs:249-259`: code `request_already_pending`, "is already pending", and "Wait for the owner's answer".

For an accepted request the owner has already answered and the cycle is running. The safety exclusion works, but the caller is told to wait for an event that already happened rather than being told that the Director is in a cycle. The new store test asserts the accepted record blocks creation, but no service test reads this response for the accepted case.

### LOW 1 - The malformed-list error reaches the card as the contradictory text `error 200`

`listRestartRequests` now throws when a successful body lacks a `requests` array, which preserves the last-known list as intended. It constructs `new GatewayError(res.status, ...)` with status 200 at `packages/client-core/src/restart/restartRequests.ts:64-71`. `useRestartRequests` sends that through `gatewayErrorMessage` (`packages/client-core/src/restart/RestartRequestsPanel.tsx:38-46`), and that formatter ignores a `GatewayError`'s own message except for status 401. With no server reason, status 200 falls through to `DevThrottle could not complete that (error 200)` at `packages/client-core/src/api/client.ts:584-595,615-638`.

The owner does see an error and the last-known list remains, so round-one MEDIUM 5 is closed. The displayed explanation is nevertheless internally contradictory and discards the precise malformed-answer sentence the fix wrote. The new client test checks the direct rejection message only; it does not pass the error through the hook's formatter.

### LOW 2 - The decline reader still collapses a present non-string `reason` to no reason, and the claimed regression test does not exist

`OptionalReasonAsync` now rejects a non-object top-level body at `src/CcDirector.Gateway/Api/DirectorRestartRequestEndpoints.cs:149-154`, closing the exact scalar/array path from round one. Within an object, however, `:155-157` returns null both when `reason` is absent and when it is present as a number, array, object, boolean, or null. A body such as `{ "reason": { "text": "not during the demo" } }` therefore succeeds as an unreasoned decline and silently drops the supplied value instead of refusing the schema-invalid field.

The fix commit says all ten findings are closed "each with a test that can fail", but the restart-request route tests contain only the successful decline at `src/CcDirector.Gateway.Tests/DirectorRestartRequestRouteTests.cs:277-290`; there is no non-object or wrong-typed-reason decline test in the changed test inventory. Round-one LOW 2 is partially closed: top-level non-objects are refused, while a schema-invalid `reason` inside an object is still read as absence.

## Round-one closure ledger

- HIGH 1 - CLOSED. `LauncherConnectionRegistry.GetActiveConnection` returns one `LauncherStreamConnection` containing both `ConnectionId` and `Declaration` (`src/CcDirector.Gateway/Streaming/LauncherConnectionRegistry.cs:112-122`). `GatewayHost.SendLauncherCommandAsync` reads it once, gates that value, and invokes the captured `connection.ConnectionId` without another registry lookup (`src/CcDirector.Gateway/GatewayHost.cs:4852-4873`). A superseding launcher is not substituted between check and dispatch.
- HIGH 2 - CLOSED. `Drained` with null, empty, or whitespace workspace is abandoned before the capability check; `Blocked`, `Unavailable`, and the default unknown verdict all abandon and never ask the launcher (`src/CcDirector.ControlApi/Restart/DirectorRestartCycle.cs:202-224`).
- MEDIUM 1 - CLOSED. `DrainAvailable` is nullable on the wire, the Director fills it from `IRestartCycleDrain.Availability`, and the Gateway permits only explicit `Eligible == true` and `DrainAvailable == true`. False, null/missing, malformed JSON, and a failed command all refuse.
- MEDIUM 2 - PARTIALLY CLOSED. The current Director claims synchronously before it schedules, and `TryCreate` blocks accepted records. The Gateway still accepts any `Ok` command body as the positive `taken` acknowledgement (MEDIUM 2 above).
- MEDIUM 3 - PARTIALLY CLOSED. The pre-ask report and three-hour expiry exist. An older fire-and-forget report can overwrite the intended last line, and the accept response for the running-expiry state names the wrong deadline (MEDIUM 3 and MEDIUM 4 above).
- MEDIUM 4 - PARTIALLY CLOSED. Both Director commands have a 30-second server bound and the timeout filter does not call caller cancellation a timeout. The outer catch converts caller cancellation into a dispatch failure and mutates the request anyway (MEDIUM 1 above).
- MEDIUM 5 - CLOSED. A successful envelope without an array throws and `useRestartRequests` leaves its last-known list standing. The error copy has the LOW issue above.
- MEDIUM 6 - CLOSED. Accept, decline, and report check the request's machine before their action mutation and return the same 404 as an unknown id.
- LOW 1 - CLOSED. `AcceptSentence` is written by the Gateway, copied by the store, and rendered by the shared card.
- LOW 2 - PARTIALLY CLOSED. A non-object top-level body now throws. A present non-string `reason` is still silently treated as absent, and the commit's claimed regression test is absent (LOW 2 above).

Totals: 6 CLOSED, 4 PARTIALLY CLOSED, 0 NOT CLOSED.

## Guard and detection-test assessment

The HIGH 1 dispatch guard is prevention, not after-the-fact detection. It reads the declaration and the destination identifier from one registry value, refuses an undeclared connection before `InvokeAsync`, and sends an ordinary restart through as a control. `GuardedRestartDispatchRouteTests` would detect removal of that pre-dispatch gate because its stub records every received command and the guarded case requires the list to remain empty while the ordinary case requires one command.

`RestartOnlyIfEmptyRouteTests.ALauncherThatIgnoresTheFlag_IsReportedAsAFailure_NotAsAGuardedRestart` still proves its stated, separate property. Its Hello declares both restart tokens, so the new pre-dispatch gate permits the command; setting `_launcherHonoursTheFlag = false` then produces a bare success, and the route must answer 502 with `launcher-did-not-honour-only-if-empty`. It proves detection of a launcher that declared the guard and then failed to acknowledge it. It does not prove prevention for an undeclared launcher; the new guarded-dispatch class owns that proof.

## Verification and boundary

I reviewed the complete `89dcb3ad9..cf983fadf` diff (19 files, 659 insertions and 69 deletions), the real checked-out production files, the round-one report, the pull request body, and the full fix-round commit message. I traced the launcher connection/declaration registry through dispatch, every drain verdict, eligibility serialization and interpretation, cycle claiming and release, request creation/accept/report/expiry transitions, cancellation and timeout catches, the list reader and card error path, decline-body parsing, and every changed test file.

Executed:

- `dotnet test src\CcDirector.Gateway.UnitTests\CcDirector.Gateway.UnitTests.csproj --no-restore --configuration Debug --verbosity minimal --filter "FullyQualifiedName~DirectorRestartRequestServiceTests|FullyQualifiedName~DirectorRestartRequestStoreTests|FullyQualifiedName~DirectorRestartCycleTests|FullyQualifiedName~GuardedRestartDispatchGateTests"`: 73 passed, 0 failed, 0 skipped. The build also completed the repository's strict workspace typechecks.
- `npm exec --workspace @devthrottle/client-core -- vitest run src/restart/RestartRequestsPanel.test.tsx`: 10 passed, 0 failed.

This verdict does NOT certify `Gateway.Tests` or `Core.Tests`; the mandate parks both, so I did not run them. In particular, the two host-bound guarded-restart classes and the full restart-request routes were read but not executed in this round. It does not certify a real drain, a real workspace/restore, a live Director stopping itself through its launcher, a launcher supersession race under SignalR, or report ordering over a real delayed network. The 73 unit tests use synchronous report fakes and therefore do not cover the progress-overtake finding. No live Director was drained or restarted.
