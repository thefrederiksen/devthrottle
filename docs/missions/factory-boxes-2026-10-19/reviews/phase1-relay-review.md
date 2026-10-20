# Review: pull request #4280, the desktop relay leg (Factory Boxes phase 1)

Reviewer seat on the Factory Boxes mission (issue #4258), opened by the Tech Lead. Reviewed on 20 October 2026 on
devlinux, in a worktree cut from `origin/boxes/phase1-relay-leg` at `ae77ed191`. The reviewer built and ran tests but
changed no product code.

## Scope

**Read, in full:**

- The mission document on `origin/main` (`docs/missions/factory-boxes-2026-10-19/mission.md`): section 5 item 1, and
  section 7 (the relay check and the allow-list check).
- The spike on `origin/main` (`phase1-spike-path-prefix.md`), its list "What the relay leg must do as a result".
- The pull request text, and the whole diff `origin/main...origin/boxes/phase1-relay-leg`: 49 files, 6,746 lines added.
  Every new production file was read line by line: `Gateway.Contracts/DesktopRelayMessages.cs`; `Core/Boxes/` (the
  pump, the window, the rate limiter, the usage tally, the header rules, the WebSocket end, the endpoint interface);
  `ControlApi/Boxes/` (the Director handler, the loopback connector and address builder, the box HTTP end, the lookup
  stand-in) and the `GatewayStreamClient` change; `Gateway/Boxes/` (the endpoints, the origin, the tickets, the
  seams, the header rules, the browser HTTP end, the legs); `Gateway/Streaming/DesktopRelayRegistry.cs` and the
  `DirectorHub` method; the `GatewayHost` wiring and the compression exclusion.
- Every new test file: `DesktopRelayLegTests`, `DirectorDesktopRelayHandlerTests`, `DesktopRelayPartsTests`,
  `DesktopRelayFairnessTests`, `DesktopRelayRig`, `FakeDesktop`.
- The record `phase1-relay-leg.md`, the evidence folder (`refusals.sh`, `refusals.txt`, the Director and Gateway
  logs), and the wiring patch `phase1-relay-leg/wiring-after-list-merges.patch`.
- The list branch `origin/boxes/phase1-hosted-desktop-list` (pull request #4271, still open, not on main): the
  registry, the pushed store, the Boxes gate and switch, so the patch's targets could be checked.
- The existing code the leg depends on: `GatewayHost.SendCommandAsync` and its tenant-scope rule, the hosted
  tenant-scope middleware and its place in the pipeline, `HostedTenantBoundary`, `AsyncLocalTenantContext`,
  `ITenantPass`, `DirectorCommandRouter.TrySendAsync`, `DirectorStreamLimits`, the forwarded-headers set-up.
- The repository's project instructions file and `docs/CodingStyle.md`.

**Ran:**

| What | Where | Result |
|---|---|---|
| The relay tests, `FullyQualifiedName~CcDirector.Gateway.UnitTests.Boxes` | this branch, `ae77ed191` | 113 passed, 0 failed, 36 seconds |
| A trial merge of this branch with `origin/boxes/phase1-hosted-desktop-list` (`c4325802f`) | a scratch worktree | two conflicts (finding 2) |
| `git apply --check` of the wiring patch on that plain merge | the scratch worktree | refused: `patch failed: src/CcDirector.ControlApi/GatewayStreamClient.cs:255` |
| The wiring patch after resolving the two conflicts the way the patch presupposes | the scratch worktree | applied cleanly |
| The relay tests on the merged tree with the patch applied | the scratch worktree | 113 passed, 0 failed |
| The list's own tests on the merged tree with the patch applied (`Gateway.Tests.Boxes` classes, `Core.UnitTests` Boxes, `Avalonia.Tests` Boxes) | the scratch worktree | 39 passed (Gateway), 48 passed (Core), 8 passed (Avalonia), 0 failed |
| An experiment for finding 1: the rig's fake command send made to refuse when no hosted tenant scope is in effect, exactly as `GatewayHost.SendCommandAsync` does, then three relay tests | this branch, reverted after | the ticket test passed; the desktop page test and the WebSocket test failed with `Expected: OK, Actual: ServiceUnavailable` |

**Could not reach:** a hosted Gateway (the production shape), Azure, a real network, a phone, the Cockpit. The
reviewer did not repeat the local end-to-end run; its evidence was read, not re-made.

## Findings

### 1. On the hosted Gateway every desktop request and WebSocket answers 503: the desktop host never enters a tenant scope, and the command send denies without one

**Where:** `src/CcDirector.Gateway/Boxes/BoxDesktopEndpoints.cs:131-135` and `:151-212`;
`src/CcDirector.Gateway/Boxes/DesktopRelayLegs.cs:127`; `src/CcDirector.Gateway/GatewayHost.cs:4820`, `:4856-4870`
and `:7660-7680`.

**The harm.** In production the Gateway is hosted. There, a browser that holds a valid box cookie and asks the desktop
host for the page, an asset, `api/status` or the `api/websockets` upgrade gets **503 "The Director hosting this box is
not connected right now."** every time, with the Director connected and the box listed. The ticket route still works,
so the Cockpit's Open button hands out a ticket, the desktop host redeems it and sets the cookie, and then nothing
loads. The feature does not work on the one Gateway it is for. The Gateway's log says, for each request,
`SendCommandAsync: GATEWAY BUG - command DROPPED, no tenant scope in effect`.

**Why.** `GatewayHost.SendCommandAsync` (`GatewayHost.cs:7660-7680`) refuses to send any command when
`_tenantPass.Current` is null, and on hosted that is `AsyncLocalTenantContext.CurrentOrNull`: null unless a scope has
been entered on the current async flow. For an ordinary request the scope is entered by the middleware at
`GatewayHost.cs:4856-4870`, which runs AFTER the sign-in middleware (`:4850`) and resolves the tenant from the
authenticated device key. The desktop host is deliberately placed BEFORE sign-in (`:4820`), and its branch
(`BoxDesktopEndpoints.cs:131-135`, `UseWhen` ... `desktop.Run(...)`) terminates the pipeline, so neither the sign-in nor
the scope middleware ever runs for it; and even if the scope middleware did run, `ResolveRequestTenant` would answer
null there, because the desktop host carries no device key by design. `ServeDesktopHostAsync` knows the tenant (it is on
the redeemed session, `session.Tenant`, line 189) but enters no scope with it; `DesktopRelayLegs.OpenAsync` then calls
`DirectorCommandRouter.TrySendAsync(_sendCommand, ...)` at `DesktopRelayLegs.cs:127`, which lands in the deny. The
pattern the Gateway uses for exactly this case is `RingDirectorAsync` at `GatewayHost.cs:987`: `using
(_tenantBoundary.EnterScope(tenant))` around the send.

**Why no test or run caught it.** The end-to-end run was on a self-host Gateway (the record: "A local test Gateway
built from this work (self-host ...)"); on self-host `_tenantPass.Current` is always `TenantId.Local`, so the deny never
fires. The rig (`DesktopRelayRig.cs:322-329`) builds a HOSTED boundary over a real `AsyncLocalTenantContext` for the
sign-in gate, but its fake `SendCommandAsync` calls the Director handler directly and never looks at the scope, so the
one rule that breaks the hosted path is the one rule the rig does not model. The experiment in the scope table
reproduces the production behaviour with a two-line change to that fake: the page and the socket become 503 while the
ticket route keeps working.

**What must change.** The desktop host must enter the session's tenant scope before it relays (the boundary is not
among `UseDesktopHost`'s parameters today, nor in the wiring patch's version of it), and the rig's fake send must
refuse without a scope, so the test pins the hosted shape rather than the self-host one. The record's claim that the
leg is proven end to end should say it was proven on self-host only, until a hosted run exists.

### 2. The wiring patch does not apply to a merge of the two branches: it presupposes a conflict resolution that is recorded nowhere

**Where:** `docs/missions/factory-boxes-2026-10-19/phase1-relay-leg/wiring-after-list-merges.patch`, hunk
`src/CcDirector.ControlApi/GatewayStreamClient.cs @@ -255,7 +256,7 @@`; and the record's section "The dependency".

**The harm.** The record says: "When the list merges, this branch is rebased onto main and the patch applied", and the
pull request says the patch "was tested on the merged tree (159 of 159 relay and list tests)". Whoever does that
lands in two merge conflicts first, and the patch then refuses to apply until the conflicts are resolved in one
particular way that only the author's private tree knew. A different, equally reasonable resolution leaves the patch
unapplicable, and the "159 of 159" cannot be reproduced from what is committed.

**The evidence.** Both branches change the constructor of `GatewayStreamClient` and of `DirectorHub`:

- `GatewayStreamClient(...)`: this branch adds `Boxes.IHostedDesktopLookup? hostedDesktops = null`; the list branch
  adds `Func<IReadOnlyList<HostedDesktopDto>>? hostedDesktops = null`. Same name, different type, same position:
  a content conflict.
- `DirectorHub(...)`: this branch adds `DesktopRelayRegistry? desktopRelay = null`; the list branch adds
  `PushedHostedDesktopStore? hostedDesktops = null`, at the same position: a content conflict.

The patch's context for the first hunk reads
`Func<IReadOnlyList<HostedDesktopDto>>? hostedDesktops = null,` followed by
`Boxes.IHostedDesktopLookup? hostedDesktopLookup = null)`: a parameter named `hostedDesktopLookup`, which exists on
neither branch. It exists only after someone keeps both parameters and renames this branch's to that exact name. The
reviewer did that (and kept both hub parameters), after which the patch applied cleanly and the relay tests passed on
the merged tree; the list's tests are in the scope table. The patch should either carry that resolution (the two
conflicted files in full, or a note naming the resolution), or the branch should be rebased onto the list branch now so
that no conflict is left for later.

### 3. A Closed frame is sent without taking window, so at a nearly full window the receiver reports a protocol fault and the browser gets 1011 instead of the real close code

**Where:** `src/CcDirector.Core/Boxes/DesktopRelayPump.cs:365` (every Data and Head frame acquires its cost before it is
sent), `:454` (the Closed frame is written to the far side with no acquire), `:321-327` (the receiver adds the cost of
every non-Credit frame, Closed included, and ends the stream when the total exceeds the window);
`src/CcDirector.Gateway.Contracts/DesktopRelayMessages.cs:203-204` (a Closed frame costs 64 bytes).

**The harm.** The sender can have exactly `WindowBytes` (1,048,576) outstanding. If it then closes, the receiver sees
up to 1,048,640 and takes the branch at line 324: it drops the real Closed frame (`continue` at line 326 skips the
inbox), logs `the far side sent 1048640 bytes against a 1048576-byte window`, and closes its own end with 1011
"internal error" and that sentence instead of the code and reason the other side sent. The condition - the window
within 64 bytes of full - only arises when the receiving end is stalled (no credit is coming back), which is also the
moment a box is most likely to close on its own (Selkies drops a viewer that stops acknowledging). So the one case where
the close code matters most is the case where it is wrong, and the log blames a protocol fault that did not happen.
Rare (it needs the last frame before saturation to leave fewer than 64 bytes free), but it is a defect in the flow
control's own accounting, not in a peer. The fix is one of: charge the Closed frame to the window like every other
frame (and let the receiver's check allow one frame of overhead), or exempt Closed from the receiver's count the way
Credit is exempt.

## Observations that are not findings

- The WebSocket open sends the browser's request headers down in `OpenDesktopStreamRequest.Headers`, but
  `LoopbackDesktopConnector.ConnectWebSocketAsync` (`DesktopBoxConnector.cs:317-336`) uses only the address and the
  subprotocols; no header (no Cookie, no Origin, no User-Agent) reaches the box's upgrade. The spike proved Selkies
  needs none, so nothing breaks today; but `BoxDesktopHeaders.cs:184-185` says a Selkies token cookie "still reaches
  the box", which is true for HTTP only.
- The desktop host is excluded from the hosted traffic meter's tenant attribution (the meter runs before the desktop
  branch and resolves no tenant there), so `/diag/traffic` will not show desktop bytes under the account; the relay's
  own per-box usage lines are the record of that spend.
- On the main host the ticket route on this branch applies the Boxes switch itself rather than through
  `BoxesGate.Group`; the patch moves it inside the group, which is the list's contract. The desktop host cannot be in
  the group (no Gateway sign-in there), and the record says so; the switch is asked of the session's tenant instead,
  which is the right tenant.

## Verdict

Not mergeable as it stands: finding 1 means the leg does not work on the hosted Gateway, and the pull request's proof
covers self-host only; finding 2 means the committed wiring cannot be applied as described; finding 3 is small.
