# Phase 2 review: the take the wheel and hand back verb (pull request #4079)

Mission: API mode for agent wrappers, proven on Pi (issue #4070), phase 2, the Gateway ruling.
Reviewed: pull request #4079, branch `api-mode-p2-wheel-verb`, at commit `88992db37cf06c4a9eb1cf8eb9e6472edac7d543`.
Reviewer seat: a different agent family from the Developer, reading only; nothing here was built or fixed by the reviewer.

## Verdict

Approve with three findings for the Developer to answer. None of the three is a bypass of a refusal or a tenant leak. Finding 1 is a conflict between this pull request's session key ruling and the mission's own proof; it needs the Tech Lead's decision before phase 6, not a code change in this pull request. Findings 2 and 3 are rulings the Gateway makes on a pushed state that it cannot fully justify from what it sees, each with a concrete way a correct request is refused.

## Scope: what was read, what was run, what was not reached

**Read, in full, on the pull request head:**
- `src/CcDirector.Gateway/Api/GatewayEndpoints.SessionWheel.cs` (new: `SessionWheelState.Read`, `MoveSessionWheelAsync`, the refusal codes and sentences).
- `src/CcDirector.Gateway/Api/GatewayEndpoints.cs`, the two new route lines and, for comparison, the interrupt and escape routes, `LocateSessionForRequestAsync`, `LocateSessionAsync`, `SessionUnavailable`, `TunnelFailure` and `MapDirectorFailure`.
- `src/CcDirector.Gateway/Util/SessionKeyGuard.cs`, the whole `Check` method and the new `IsWheelMove`, plus `AuthMiddleware` where the guard is applied (lines 436 to 460 and 640 to 690), to confirm the guard runs on every request path and that the raised answer is only taken when the session is raised.
- `src/CcDirector.Gateway/Api/DirectorCommandRouter.cs` (`TrySendAsync`, the default 30 second timeout).
- `src/CcDirector.Gateway.UnitTests/Api/SessionWheelEndpointTests.cs` and the `SessionKeyGuardRaisedTests.cs` diff.
- `src/CcDirector.ControlApi/SessionWriteExecutor.cs` and `SessionCommandExecutor.cs` on the Director side, to learn what a Director does with a verb it does not know (a `BadRequest` naming the verb).
- `src/CcDirector.ControlApi/GatewayStreamClient.cs`, to learn how the Director pushes session state (a delta per changed session through `NotifyDelta`, and a full re-push every ten seconds).
- The mission issue #4070, section 7 "Proven means", and the mission record under `docs/missions/api-mode-pi-2026-10-16/`.
- The two `SessionDto` fields and their constants were read only as far as needed to understand the reader; they are reviewed in their own pull request and nothing below is about them.

**Run, on this machine (Linux, .NET 10.0.301):**
- `dotnet test src/CcDirector.Gateway.UnitTests --filter "FullyQualifiedName~Wheel|FullyQualifiedName~SessionKeyGuard"`: 502 passed, 0 failed, 0 skipped. This matches the Developer's count.
- The three tests the Developer reported failing on Linux, run alone: all three fail here too.
- A search of every remote branch for the string `wheel-take`: it exists only on this branch. The Director side has no handler yet, so the command names are pinned by this branch alone.

**Not reached:**
- `scripts/test-local.ps1` and the `-Parked` gate: not runnable on this machine. The Tech Lead runs them on Windows.
- The hosted tenant boundary. Every test in the new file uses the self-host boundary, so the caller's tenant is always the local one. The hosted path through `ResolveReadTenant` is the very same code interrupt uses and was not changed, but this review did not exercise it.
- No live Gateway, Director or Pi was started. Nothing below rests on a running system.

## On the three Linux failures the Developer reported

They are unrelated to this change. The branch touches six files; none of the three test files, and none of the product code they drive (`HeldDeliveryDriver`, the typed prompt store, the voice settings store), is among them. Each forces a save failure through a Windows-only file mechanism: the two voice tests hold the file open with `FileShare.None`, which Linux does not enforce, and the held delivery test sets the `ReadOnly` file attribute, which does not stop the store's write on Linux either (the Developer's description says `FileShare.None` for all three; for `HeldDeliveryDriverTests` it is the read-only attribute, the effect is the same). They need the Windows gate, as the Developer said. They are not evidence against this pull request, and they are not evidence for it.

## What was checked and found correct

- **Every Gateway refusal sends nothing.** Each refusal returns before `TrySendAsync`, and every refusal test asserts the fake sender recorded nothing. The order of checks is right: locate (404 or 503), unreadable state (502), terminal mode (409), then the two holder rules (409).
- **No bypass through the key guard.** `IsAgentInput` is evaluated before the allow list, so no later widening of the allow list can open the two routes. The guard lower-cases the structural segments and trims a trailing slash, so `/Sessions/x/WHEEL/Hand-Back/` is refused the same way; the tests prove it. Only the two literal four-segment shapes match; `GET`, `/wheel`, and `/wheel/steal` fall to the allow list and are refused even to a raised key.
- **Tenant isolation.** The routes locate the session through `LocateSessionForRequestAsync`, exactly as interrupt does: the tenant comes from the credential, the pushed store is looked up under that tenant, and the owning Director is resolved under the same tenant (the issue #1847 guard). The test with a second account's Director proves the caller cannot locate, learn of, or reach that Director's session.
- **One reader.** Mode and holder are read in `SessionWheelState.Read` and nowhere else in the handler; the handler compares only the normalised `Holder` the reader returns.
- **Terminal mode untouched.** A terminal session, and a row from a Director that pushes neither field, are refused as `not_api_mode` before anything is sent. No existing route, command or guard entry was changed in behaviour; interrupt and escape are byte for byte as before.
- **Director refusals pass through verbatim.** `MapDirectorFailure` keeps the Director's sentence and maps its status; the tests cover Conflict, Locked, BadRequest, NotFound and Error. A disconnected Director is answered with the same body as interrupt, proven byte for byte on the real route map.
- **A Director that does not yet have phase 4** answers `wheel-take` with `BadRequest` "unknown verb 'wheel-take'" from `SessionCommandExecutor.DispatchAsync`, which the Gateway returns as a 400 with those words. It cannot hang on the 30 second timeout. The words are not a user sentence, but no surface offers the button until phase 4 lands (mission section 8), so this is a window, not a defect.
- **The tests prove what they claim.** The handler tests drive the real handler over a real registry and a real pushed store on an injected clock, with the rows built through the same serialised form a push carries, so the "older Director" case is the real deserialisation path and not a hand-set default. The route map tests boot the real `GatewayEndpoints.Map` on an in-memory test server. No sleep, port, process, database or real clock.

## Findings

### Finding 1. The session key ruling makes the mission's own proof unrunnable "by an agent with no human" through the Gateway

**What the pull request decides.** An ordinary session key is refused on both wheel routes with its own sentence (`AgentInputRefusal.Wheel`), with no "session it owns" exception; a raised session passes under `RaisedGrant.AgentInput`. The reasoning given (taking the wheel opens the agent's real terminal for a person to type into, and a hand back closes the owner's terminal under him, so both decide who types into the session, which is the owner's) is consistent with how interrupt and escape are ruled, and I find the ruling itself defensible and the implementation correct.

**The harm.** The mission's check (issue #4070, section 7, "Proven means", item 2) says the end-to-end proof script runs the flow "through the Gateway with `cc-devthrottle`", "run by an agent with no human", and its steps 4 and 5 and two of the failure cases ("hand back while the terminal is mid-turn", "take the wheel twice: the second is refused") are take the wheel and hand back. `cc-devthrottle` authenticates with the session's own key (`Authorization: Bearer <CC_GATEWAY_SESSION_KEY>`). Under this pull request that request is answered 403 `session_key_out_of_scope` by the middleware before any wheel logic runs, so the proof can never observe `wheel_already_held`, the Director's mid-turn refusal, or a hand back. The pull request's own note says the script "needs the owner's own key or a raised session", but raising a session is the owner's device's action alone (`POST /sessions/{sid}/raise` is refused to every session key, raised or not, by design), so both escapes require a human. "Run by an agent with no human" and this ruling cannot both be true for steps 4 and 5.

**Why it is a finding and not a note.** Phase 6 will be built against the check as written; if this is discovered then, the proof either silently shrinks (the wheel steps are dropped and the check reads as passed) or phase 6 reopens phase 2. It should be settled now, by the Tech Lead, as a recorded decision. The options I can see, in no order: (a) the owner raises the proof session once before the run and the check is amended to say so; (b) a session key may take and hand back the wheel of a session it owns, the way the prompt route already lets an owned session be typed into (`OwnedSessionInput`), with the route checking ownership; (c) the proof script drives the Director's command path directly for the two wheel steps and the Gateway refusal is proven separately by the unit tests that already exist. Option (b) weakens the stated reasoning (an agent would then be able to open a terminal on a session it spawned), so it is a product decision, not a reviewer's.

Developer's answer: **Ruled: option (b)**, by the owner through the Delivery Lead and the Tech Lead, 16 October 2026. Done in this pull request:
- A session key may take the wheel, hand back, and stop the turn of an API-mode session **only on a session it controls**. That is the prompt route's owned-session rule, `OwnedSessionInput.OwnsDirectly`: the target's `ControllerSessionId` names the caller, and the target is not the caller itself.
- Every other session refuses an agent key: 403, code `not_your_session`, with the sentence `AgentInputRefusal.ApiModeNotYourSession`. The route checks this before it says anything about the session's state, so a caller learns nothing about a session it does not control.
- The guard (`SessionKeyGuard.IsApiModeVerb`) lets the three shapes through to the route, the same split as the prompt. A raised session is not held to ownership, as on the prompt route. Interrupt is unchanged.
- **Stop** is added as the ruling asks: `POST /sessions/{sid}/turn/stop`, for API-mode sessions only. A terminal session is refused with a sentence pointing at interrupt. The command `turn-stop` goes to the owning Director, its refusals pass through verbatim, and the same key rule applies. Stop needs only the mode, so a holder the Gateway cannot read never blocks it.
- Tested both ways: a key on a session it controls is allowed for all three verbs. A key on a session another session controls, on one the owner runs, or on itself is refused with nothing sent. A raised key is allowed.

The phase 6 proof can now run with no human, provided its script spawns the Pi session with `--controlled-by self`.

### Finding 2. A hand back while the holder is `none` is refused by the Gateway with a sentence that may be false, and the Gateway cannot know

**What the code does.** `MoveSessionWheelAsync` refuses a hand back whenever the holder is anything but `user`, so a holder of `none` is answered 409 `wheel_not_held`: "You do not have the wheel of this session, so there is nothing to hand back: it is still running in API mode." For a take, the same `none` is deliberately sent on to the Director, because "the Gateway cannot see a reason to refuse, so the Director, which holds the ownership lock, decides".

**The harm.** `none` is defined (in the contract's own words) as "no agent process holds the conversation". Phase 4 has not been built, and nothing in the mission says what the Director reports after the user took the wheel and the terminal process then ended on its own (Pi exited, the user typed its exit command, the process crashed). If phase 4 reports `none` in that state, the user who then presses hand back to get the session running over the protocol again is told, falsely, that the session "is still running in API mode", and the one verb that would resume it is blocked at the Gateway. The Director, which knows the truth, is never asked. The asymmetry with take is the point: for take the pull request trusts the Director to rule on `none`; for hand back it rules itself on a state whose meaning phase 4 has not yet fixed.

**Why it is a finding.** The brief for this review asks that every refusal be correct. This one is correct only under an assumption about phase 4 that is not written anywhere. Either the pull request records that assumption as a contract phase 4 must meet (`none` never follows `user` without the Director itself resuming the protocol), or the hand back on `none` goes to the Director as the take does. The Developer decides which.

Developer's answer: **Accepted, fixed.** A hand back is now refused at the Gateway only while the holder is `protocol`, the one holder for which the sentence "it is still running in API mode" is true. A hand back while the holder is `none` goes to the Director, the same as a take on `none`. After the user held the wheel, what `none` means is the Director's to rule, and hand back may be the very verb that resumes the session. This needs no promise from phase 4. The test `MoveSessionWheelAsync_HandBackWhileTheUserDoesNotHoldTheWheel_IsRefused(none)` is replaced by `MoveSessionWheelAsync_HandBackWhileNobodyHoldsTheWheel_IsSentToTheDirectorToRule`, which asserts 200 and one `wheel-hand-back` sent. The `protocol` case keeps its own refusal test.

### Finding 3. The Gateway's ruling on a pushed state can refuse what the Director would allow, and the pull request describes the risk in one direction only

**What the pull request says.** "The Gateway's ruling reads a pushed state that can be one push behind. That is safe: the Director is the only enforcer of one holder and refuses what its own state does not allow." That covers the case where the stale state lets a request through that the Director then refuses. It does not cover the inverse.

**The harm.** The Director pushes a changed session as a delta (`GatewayStreamClient.NotifyDelta`), sent without waiting, separately from the command reply. So after a successful take, the reply reaches the caller while the delta that moves the holder to `user` is still in flight. A hand back issued in that window reads the old holder (`protocol`) and is refused 409 `wheel_not_held`, "You do not have the wheel of this session", when the Director would have accepted it. The same holds for a take right after a hand back, refused `wheel_already_held`. For a person the window is small and a second press clears it. For the proof script and any client that acts on the previous reply at once, it is a false refusal of a correct request, with a sentence that tells the caller something untrue. A client that follows rule 7 shows the sentence verbatim and cannot soften it.

**Why it is a finding.** The two 409 rulings exist "so the common refusals are answered at once and in one set of words"; that is a fair reason, but a ruling on a state the Gateway knows can be stale is a ruling that is sometimes wrong, and the pull request should say so and say what the client or the proof does about it. Possible answers, the Developer's to choose: phase 4 pushes the delta before it replies to the wheel command, so the reply never outruns the state (this makes the window zero by construction and is the strongest answer); or the Gateway rules only when the pushed row is newer than the last wheel command it forwarded for that session; or the window is accepted and recorded in the phase 6 proof as "retry once on 409 within two seconds of a wheel reply". What should not happen is the proof script hitting this on its first run with nothing written down to explain it.

Developer's answer: **Accepted, answered with the first option: a contract on the Director (phase 4), carried by the Tech Lead.** The contract: *before it returns its reply to `wheel-take` or `wheel-hand-back`, the Director has finished sending `PushDelta` with the session's new `WheelHolder` on the same hub connection (awaited, not the fire-and-forget `NotifyDelta`).*

Why this closes the window by construction, checked on origin/main: the Gateway sends the command as a SignalR client result on the Director's hub connection (`hub.Clients.Client(connectionId).InvokeAsync<DirectorCommandResult>("Command", ...)` in `GatewayHost`). The delta arrives on that same connection as the hub method `DirectorHub.PushDelta`, which applies it to the pushed store inline. The hub runs one call per connection at a time (`MaximumParallelInvocationsPerClient` is not raised anywhere, so it is the default of 1). A delta the Director finished sending before its reply is therefore applied before the Gateway completes the command and answers the caller. Any later request then reads the new holder. The stale-state paragraph in `MoveSessionWheelAsync`'s summary now states both directions and this contract.

The two other options were weighed and not chosen. A Gateway-side "rule only when the row is newer than the last forwarded command" adds state to the Gateway and still answers wrongly if the delta is lost. A retry in the proof would hide a false sentence that a person also sees. If phase 4 cannot meet the contract, the fallback is to drop the two holder rulings and let the Director rule them. That is a one-line change here, and I would want the Tech Lead's word before making it.

## Notes, not findings

- `SessionKeyGuardRaisedTests.cs` gains a byte order mark on its first line (the diff shows `-using` then `+﻿using`). Harmless, but it is a change the pull request does not intend.

  Developer's answer: Accepted, fixed. The mark is removed; the file's first bytes match origin/main again.
- The pull request table says a Director status other than Conflict, Locked, BadRequest or NotFound maps to 502. `MapDirectorFailure` maps `Timeout` to 504. The code is right; the table is slightly off.

  Developer's answer: Accepted, fixed in the pull request description: `Timeout` is 504, and anything else is 502.
- The pull request lists a reviewed count of "502 passed" for new and touched tests; confirmed on this machine.

## Second round

Reviewed: pull request #4079 at commit `d0db3c7b2`, the two commits since the first round (`244b4d37a` and `d0db3c7b2`). Same reviewer seat, a different agent family from the Developer, reading only.

### Verdict

Approve. No finding proves harm. Three notes follow, none of which blocks the merge; the Developer decides whether to act on any of them.

### Scope: what was read, what was run, what was not reached

**Read, in full, on the pull request head:**
- `src/CcDirector.Gateway/Api/GatewayEndpoints.ApiModeVerbs.cs` (the renamed handler, now `RunApiModeVerbAsync`, with the `StopTurn` verb, the ownership check, and the `not_your_session` refusal).
- `src/CcDirector.Gateway/Util/SessionKeyGuard.cs`, the diff since the first round: `IsWheelMove` becomes `IsApiModeVerb`, the unraised answer changes from a refusal to `Allow`, and `AgentInputRefusal.Wheel` becomes `ApiModeNotYourSession`.
- `src/CcDirector.Gateway/Api/GatewayEndpoints.cs`, the three route lines.
- `src/CcDirector.Gateway/Util/OwnedSessionInput.cs`, the whole class, to confirm what `OwnsDirectly` requires: the target's controller names the caller, and the target is not the caller itself.
- `src/CcDirector.Gateway/Util/AuthMiddleware.cs`: `AuthenticateSession`, `AcceptSession`, `RaisedGrantOf` and `CallingSession`, to confirm that every verified session key reaches the route with its identity recorded and with a raised grant recorded only when the session is raised and raising changes the answer. This is the same path the prompt route has relied on since Parent Control, fix 1.
- The prompt route's own ownership branch in `GatewayEndpoints.cs` (around `DeliverOwnedPromptAsync`), to compare the two rules side by side.
- `FleetManagerHandOverEndpoints.cs` and `SpawnOrigin.cs`, to answer whether any route open to a session key can make it the controller of a session it did not start.
- `src/CcDirector.Gateway.UnitTests/Api/ApiModeVerbEndpointTests.cs` (the renamed test file, all of it) and the `SessionKeyGuardRaisedTests.cs` diff.
- On origin/main, for the finding 3 contract: `DirectorHub.PushDelta`, `DirectorHubCalls.PushDeltaAsync`, `GatewayStreamClient` line 1177, and `GatewayHost` line 7410.

**Run, on this machine (Linux, .NET 10):**
- `dotnet test src/CcDirector.Gateway.UnitTests --filter "FullyQualifiedName~ApiModeVerb|FullyQualifiedName~Wheel|FullyQualifiedName~TurnStop|FullyQualifiedName~SessionKeyGuard"`: 529 passed, 0 failed, 0 skipped.
- A search of the pull request head for the retired names (`AgentInputRefusal.Wheel`, `IsWheelMove`, `MoveSessionWheelAsync`): they survive only in the first-round text of this review file, which is history and is right to keep them.

**Not reached:**
- `scripts/test-local.ps1`: not run, as the brief asks.
- The hosted tenant boundary: every handler test uses the self-host boundary. The locate step is unchanged from the first round and from interrupt.
- No live Gateway, Director or Pi. The two `SessionDto` fields were not reviewed here; they belong to pull request #4077.

### What was checked and found correct

**The ownership check cannot be bypassed.** I traced every way a request reaches `RunApiModeVerbAsync`:
- The owner's device (a device key or the shared machine token): `CallingSession` is null, so the check is skipped. That is the owner, and the owner's grant is not held to ownership.
- An unraised session key: the guard now answers `Allow` with `RaisedGrant.None`, `AcceptSession` records the identity and no grant, and the route refuses unless `OwnsDirectly` holds. `AcceptSession` is the one place a session key is admitted, so there is no session-key request without a recorded identity.
- A raised session key: `AuthenticateSession` asks the guard a second time as raised; the raised answer carries `AgentInput`, which differs from `None`, so raising changes the answer and the grant is recorded. The route then skips the ownership check, exactly as the prompt route does. The raised action is recorded by the middleware before the route runs.
- Becoming the controller of a session one did not start: the hand-over route lets an ordinary session key hand a session it controls *to the owner* only; `me` is accepted only from the account's Fleet Manager, and no session can name a third session. `PATCH /sessions/{sid}` accepts a name and nothing else. So an ordinary session key cannot promote itself into the rule. The Fleet Manager can take an owner-run session and then use these verbs on it; that is the ruling's own "or was handed" and the owner's standing grant to that seat.
- The three shapes the guard lets through are literal: `GET`, `/wheel/steal`, `/turn/start`, `/turn` and `/wheel` fall to the allow list and are refused even to a raised key, and the tests prove it.

**Nothing about another session is revealed.** The ownership check runs before `SessionWheelState.Read`, so a refused caller is answered `not_your_session` whether the session is terminal, API mode, or unreadable, and the test for the terminal case proves the order. The refusal body carries the code and the sentence and nothing else; the prompt route's refusal, by comparison, includes the target's `ActivityState`, so this route is the stricter of the two. What a refused caller does learn is that the session exists in its account and whether its Director is stale (the 404 and 503 from the locate step come first). That is nothing new: `GET /sessions/{sid}` and `GET /sessions` are open to every session key in the same account.

**Stop's refusals.** A terminal session, including a row from a Director that pushes no mode, is refused 409 `not_api_mode` with a sentence that names interrupt and sends nothing. An unknown mode is refused 502 and sends nothing. An API-mode session with a holder the Gateway cannot read is *sent*, by design, because Stop needs only the mode; the test proves it. Every Director refusal passes through with its words and status. The route is on the real route map and reaches the Director with `turn-stop`.

**Both sides of the key rule are tested.** Allowed: a key on a session it controls, for all three verbs, with the right command sent once. Refused with nothing sent: a key on a session another session controls (three verbs), on a session the owner runs (three verbs), and on itself. Allowed: a raised key on a session it does not control. The guard tests prove the unraised shape reaches the route and the raised shape carries the grant.

**The finding 3 contract holds by construction, with one wording note below.** On origin/main, `DirectorHub.PushDelta` is a synchronous `void` hub method that applies the delta to the pushed store before it returns, and `DirectorHubCalls.PushDeltaAsync` sends it with `InvokeAsync`, which does not complete until the Gateway has returned from the method. So a Director that awaits `PushDeltaAsync` before replying cannot reply until the store already holds the new holder, whatever the hub's dispatch order. `MaximumParallelInvocationsPerClient` is not set anywhere, so the default of one applies, as the Developer said. The fire-and-forget form is `GatewayStreamClient.NotifyDelta`, which wraps the same call in an unawaited `SendAsync(...)` at line 1177.

**The answer to finding 2 is as described.** A hand back is refused at the Gateway only while the holder is `protocol`; `none` goes to the Director, and the test asserts 200 and one `wheel-hand-back` sent.

### Findings

None. Numbering would have continued from 4.

### Notes, not findings

- **The Stop refusal on a terminal session points every caller at interrupt, including a session key that cannot use it.** `StopNotApiModeReason` says "interrupt it instead (cc-devthrottle session interrupt, or Ctrl+C in its terminal)". Interrupt is refused to every session key, even on a session it controls (the brief and `OwnedSessionInput` both keep it the owner's). An agent that controls a terminal-mode child and calls Stop is therefore sent to a verb that answers 403 with the Typing sentence, which then tells it to message the session instead. The end state is correct and it costs one extra request, which is why this is a note. The Gateway may choose the sentence by caller without breaking rule 7, since the ruling stays on the Gateway.

  Developer's answer: Accepted, fixed. A session key (not raised) that asks to stop a terminal-mode session now reads `StopNotApiModeAgentReason`: Stop does not apply, an agent may not interrupt a session, and it should send a queued message instead (`AgentInputRefusal.Instead`). The owner's device and a raised session still read the sentence that names interrupt, which they can use. The Gateway chooses the sentence, so rule 7 holds. New test: `RunApiModeVerbAsync_SessionKeyStopsATerminalSessionItControls_IsPointedAtAQueuedMessageNotInterrupt`.

- **The ordering test asserts the code only.** `RunApiModeVerbAsync_NotControlled_IsRefusedBeforeTheSessionsStateIsSaid` checks that the body's code is `not_your_session`, but not the 403 status and not that nothing was sent. The other refusal tests assert all three through `AssertRefused`; this one could too, at no cost.

  Developer's answer: Accepted, fixed. The test now uses `AssertRefused`, so it checks 403, the code, the sentence, and that nothing was sent.

- **The contract's words say "finished sending"; the guarantee comes from awaiting the completion.** The phase 4 contract reads "finished sending `PushDelta` ... (awaited, not the fire-and-forget `NotifyDelta`)". What makes the window zero by construction is that `DirectorHubCalls.PushDeltaAsync` uses `InvokeAsync`, whose task completes only after the Gateway's hub method has returned, and that method applies the store synchronously. "Finished sending" could be read as satisfied by an awaited `SendAsync`, which completes when the bytes leave and would make the ordering rest on the hub's dispatch internals instead. Naming the call (await `PushDeltaAsync`, the existing method) where the contract is carried to phase 4 removes the ambiguity.

  Developer's answer: Accepted, fixed. The contract in the handler's summary and in the pull request now names the call: the Director awaits `DirectorHubCalls.PushDeltaAsync` (which uses `InvokeAsync` and completes only after the Gateway's `DirectorHub.PushDelta` has applied the delta), never `GatewayStreamClient.NotifyDelta` and never a bare awaited `SendAsync`. The Tech Lead carries that wording to phase 4.
