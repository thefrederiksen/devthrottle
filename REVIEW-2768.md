# Review 2768

Verdict: REQUEST CHANGES

Reviewed commit: `89dcb3ad96a6b9c9db673df6ffae438f2849c719`

Finding count: 2 HIGH, 6 MEDIUM, 2 LOW.

## Findings

### HIGH 1 - The third capability check does not certify the launcher that receives the restart

`src/CcDirector.ControlApi/Restart/DirectorRestartCycle.cs:193-204` checks capability, then performs another awaited Gateway report, then makes a separate HTTP request to ask for the restart. The check and the ask are not bound to one launcher connection or declaration. `src/CcDirector.Gateway/Streaming/LauncherConnectionRegistry.cs:52-70` explicitly permits a new connection for the machine to supersede the checked connection. The later restart route resolves and dispatches to the then-current connection through `src/CcDirector.Gateway/Api/LauncherLifecycleRelay.cs:105-124` and `:303-308`; it does not repeat the guarded-restart declaration check at dispatch.

A capable launcher can therefore pass the third check, be superseded while the progress report is awaited, and have the restart delivered to an older launcher that does not understand `onlyIfEmpty`. `src/CcDirector.Gateway/Api/LauncherLifecycleRelay.cs:127-174` confirms that such a launcher may already have restarted a busy Director before its bare success is converted to a 502. That is detection after the work-loss event, not prevention. This violates the required immediate-before-ask property and can restart over live, unaccounted sessions.

### HIGH 2 - `Drained` is accepted without proof that a workspace exists

`RestartDrainOutcome` permits a nullable workspace identifier (`src/CcDirector.ControlApi/Restart/DirectorRestartCycle.cs:20-21`). In `DirectorRestartCycle.RunStepsAsync`, the `Drained` arm at `:177-180` proceeds without requiring a non-empty `WorkspaceId`, and the guarded restart is sent at `:193-204` using that unchecked value. The result is then described as recorded in a workspace at `:206-215`, even when the identifier is null or blank.

The drain seam can therefore answer `Drained` without the record needed to restore the closed sessions, and the cycle will restart anyway. This is a permissive reading of a partial answer and can lose the fleet. The cycle test fake always returns `"ws-1"` (`src/CcDirector.Gateway.UnitTests/Restart/DirectorRestartCycleTests.cs:14-23`), so the invalid but representable answer is not exercised.

### MEDIUM 1 - This build creates approvals even though its cycle is known to have no drain

The create path checks launcher capability, roster knowledge, and launcher ownership, then creates the pending record (`src/CcDirector.Gateway/Api/DirectorRestartRequestService.cs:152-252`). It has no check for whether the Director can perform the drain. The production cycle is nevertheless always constructed with `NoDrainOnThisBuild` (`src/CcDirector.ControlApi/ControlApiHost.cs:1008-1014`), whose only result is `Unavailable` (`src/CcDirector.ControlApi/Restart/DirectorRestartCycle.cs:36-50`). Thus every otherwise-valid request can be shown to the owner and accepted, but this build will abandon every one at the first drain step for a fact known before the approval was created.

This directly contradicts Issue #2725's requirement that the owner is never shown an approval for a restart that cannot work. The confirmation sentence also promises that the Director drains, restarts, and reports (`packages/client-core/src/restart/RestartRequestsPanel.tsx:154-160`), although this production construction can do none of those after the accept.

### MEDIUM 2 - Two accepted requests can both be reported as taken before either cycle owns the single-run gate

The store blocks only another `Pending` request (`src/CcDirector.Gateway/Api/DirectorRestartRequestStore.cs:63-80`). As soon as request A is accepted, request B can be created and accepted for the same machine. On the Director, `StartRestartCycle` checks the static `Running` value, but it does not claim that gate; it schedules `RunAsync` with `Task.Run` and immediately returns success (`src/CcDirector.ControlApi/ControlApiHost.cs:1000-1024`). The gate is claimed later, inside `RunAsync` (`src/CcDirector.ControlApi/Restart/DirectorRestartCycle.cs:131-140`).

If both commands cross that scheduling window, both callers receive `taken = true`. Only one task claims the gate. The other throws before entering the method's `try` block, so the outer task merely logs it and never reports the corresponding request abandoned. Accepted requests do not expire (`src/CcDirector.Gateway/Api/DirectorRestartRequestStore.cs:265-280`), leaving that approval stuck as running. The concurrency test starts the second cycle only after the first is already inside the drain (`src/CcDirector.Gateway.UnitTests/Restart/DirectorRestartCycleTests.cs:206-225`); it does not exercise the host's check-to-schedule window.

### MEDIUM 3 - A real successful restart cannot execute the old Director's completion report

After the restart HTTP request returns, the old Director reports `Completed` (`src/CcDirector.ControlApi/Restart/DirectorRestartCycle.cs:202-217`). But the launcher does not answer that command until it has awaited `DirectorSupervisor.RestartAsync` (`src/CcDirector.Launcher/LauncherStreamClient.cs:179-209`). The supervisor signals the old Director and waits for that process to exit (`src/CcDirector.Launcher/DirectorSupervisor.cs:376-385`), then starts the replacement and only then returns success (`:517-546`). The process that is supposed to resume the await and send `Completed` is therefore already gone before the success response exists.

The durable record remains `Accepted`, and the store expires only pending records and sweeps only records with `ClosedAtUtc` (`src/CcDirector.Gateway/Api/DirectorRestartRequestStore.cs:265-280`). The green cycle test does not certify this production handoff: its fake restart returns an immediate in-process 200 without stopping the caller (`src/CcDirector.Gateway.UnitTests/Restart/DirectorRestartCycleTests.cs:26-50`). The owner receives a permanently misleading running state instead of the required final report.

### MEDIUM 4 - Restart-request commands bypass the command timeout that `SendCommandAsync` requires its caller to supply

The service invokes its raw `_sendCommand` delegate directly for both the pre-approval eligibility query (`src/CcDirector.Gateway/Api/DirectorRestartRequestService.cs:434-445`) and the accepted cycle dispatch (`:341-345`). Production wires that delegate straight to `GatewayHost.SendCommandAsync` (`src/CcDirector.Gateway/GatewayHost.cs:3900-3912`). `SendCommandAsync` explicitly has no internal bound and says the bound must come from `DirectorCommandRouter.TrySendAsync` (`:4805-4841`), whose normal timeout is 30 seconds (`src/CcDirector.Gateway/Api/DirectorCommandRouter.cs:30-35,76-103`). This service never enters that router.

A connected Director that leaves the invocation unanswered can therefore hold a create indefinitely. More seriously, accept has already changed the record to `Accepted` before the unbounded call, so the owner can be left waiting indefinitely with a record that never expires. A caller disconnect may cancel one instance, but that is not a server-side command bound and does not cover a caller that keeps waiting.

### MEDIUM 5 - A malformed successful list response is treated as proof that there are no requests

`listRestartRequests` returns an empty array whenever a successful JSON body lacks an array-valued `requests` member (`packages/client-core/src/restart/restartRequests.ts:54-64`). `useRestartRequests` then clears the last-known list and its error (`packages/client-core/src/restart/RestartRequestsPanel.tsx:36-46`), and the panel renders nothing (`:65-70`). A valid 200 JSON object with a missing, renamed, or wrongly typed member is therefore indistinguishable from a verified empty list. Pending approvals disappear without any error and may expire while the owner is being told nothing is needed.

### MEDIUM 6 - The machine path is checked after mutation on accept and not checked at all on decline or report

`AcceptAsync` calls the store first, changing the request from `Pending` to `Accepted`, and only then checks whether the request belongs to the machine in the URL (`src/CcDirector.Gateway/Api/DirectorRestartRequestService.cs:255-297`; the state mutation is at `src/CcDirector.Gateway/Api/DirectorRestartRequestStore.cs:132-166`). A mismatched URL abandons the real request while the response says `Nothing was done`. `Decline` and `Report` never compare `request.Machine` with the path at all (`src/CcDirector.Gateway/Api/DirectorRestartRequestService.cs:373-410`). This differs from both GET and accept's stated route identity and lets a wrong machine/request pairing close or update a request for another machine in the same account.

### LOW 1 - The shared card's safety comment is false about the source of its sentences

The file says every sentence is written by the Gateway and the card decides nothing (`packages/client-core/src/restart/RestartRequestsPanel.tsx:16-21,91`), but the critical confirmation promise is authored locally at `:154-160`. That false maintenance claim makes the promise easy to drift from server behavior; in this commit it already contradicts the production `NoDrainOnThisBuild` behavior described above.

### LOW 2 - A present but schema-invalid decline body is silently treated as no reason

`OptionalReasonAsync` rejects malformed JSON, but a valid JSON scalar or array returns null (`src/CcDirector.Gateway/Api/DirectorRestartRequestEndpoints.cs:141-150`). The decline then succeeds with `declined by the owner`, silently dropping the present body. This collapses "no body" and "body present but not the required object" into the permissive action despite the nearby comment's refusal guarantee.

## Admission-surface result

I found no widening in the session-key guard shapes. The create route is matched only as `POST /machines/{machine}/director/restart-requests`; accept, decline, report, and direct restart remain refused. The unit suite includes positive create/read cases and negative action/direct-restart cases. This statement certifies the guard implementation and its unit calls at this commit; it does not certify the full hosted authentication route because the parked `Gateway.Tests` suite was not run.

## Verification and boundary

I reviewed the real files at exact commit `89dcb3ad96a6b9c9db673df6ffae438f2849c719`, covering the post-merge Phase 6 changes and the `LauncherStreamClient.cs` conflict resolution in merge `f2e50bb4`. I traced create, accept, decline, report, store transitions and expiry, session-key route matching, capability folds, Director eligibility and cycle ordering, Gateway-to-Director and Gateway-to-launcher routing, launcher declaration/dispatch, supervisor restart sequencing, the shared client card, and the changed tests and CLI surface.

Executed after restoring this fresh worktree's missing project assets:

- `dotnet test src\CcDirector.Gateway.UnitTests\CcDirector.Gateway.UnitTests.csproj --configuration Debug --verbosity minimal`: 3,862 passed, 2 skipped, 0 failed, 3,864 total.
- `dotnet test src\CcDirector.Launcher.Tests\CcDirector.Launcher.Tests.csproj --configuration Debug --verbosity minimal`: 180 passed on `net10.0` and 180 passed on `net10.0-windows`, 0 skipped, 0 failed.

The test results certify only the cases those two projects executed. They do not certify a real drain/workspace/restore implementation, the SignalR scheduling window, launcher replacement between capability and dispatch, or the self-terminating Director-to-Gateway-to-launcher handoff. Per mandate, I did not run `Gateway.Tests` or `Core.Tests`. No live Director was drained or restarted, and no end-to-end restore was exercised. The unmerged drain and restore phases were not taken on trust as proof that this branch can complete the cycle.
