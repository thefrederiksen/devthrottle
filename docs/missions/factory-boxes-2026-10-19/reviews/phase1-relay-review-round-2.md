# Review, round 2: pull request #4280, the desktop relay leg (Factory Boxes phase 1)

Reviewer seat on the Factory Boxes mission (issue #4258), opened by the Tech Lead. Re-reviewed on 20 October 2026
on devlinux against `origin/boxes/phase1-relay-leg` at `4c80bb012`, rebased onto main at `2934dd163` (the merge of
pull request #4271, the hosted-desktop list). The first round is `phase1-relay-review.md` beside this file. The
reviewer built and ran tests but changed no product code.

## Scope

**Read, in full:**

- The Developer's answers, the section "Developer's answers to the reviews" at the end of
  `docs/missions/factory-boxes-2026-10-19/phase1-relay-leg.md`, and the record's rewritten sections "The local
  end-to-end proof" (both runs) and "The dependency".
- Every commit since the first review, with its diff: `af1f98985` (Clear-Site-Data on the exchange), `8497f4384`
  (the desktop host's refusal names the Gateway and is logged), `d55c3828a` (record), `c66422a3d` (the tenant scope,
  finding 1), `34dcfef01` (the Closed frame's cost, finding 3), `f5d57d63f` (the header comment), `cac42dc50` (the
  hosted end-to-end run and its evidence), `4f91c2831` (the wiring to the list, its store and the Boxes switch), and
  `4c80bb012` (the record after the rebase, the patch deleted).
- The files those commits change, as they now stand on the head: `BoxDesktopEndpoints.cs`, `DesktopRelayMessages.cs`,
  `DesktopRelayPump.cs`, `DirectorDesktopRelayHandler.cs`, `GatewayStreamClient.cs`, `ControlApiHost.cs`,
  `GatewayHost.cs` (the desktop host and the ticket route wiring, the two singletons), `DesktopRelayRig.cs`,
  `DesktopRelayLegTests.cs`, `DesktopRelayPartsTests.cs`.
- The list's code the wiring now calls, on main: `BoxIdRule`, `HostedDesktopRegistry` (its constructor, its empty
  initial state and `Resolve`), `PushedHostedDesktopStore.TryLocate`, `BoxesSwitch.IsOn`, `BoxesGate.Group`.
- The hosted run's evidence: `phase1-relay-leg/hosted/gateway-log.txt`, `director-log.txt`, `refusals.txt`.
- The other reviewer's file `phase1-relay-review-codex.md` (no findings).

**Ran:**

| What | Where | Result |
|---|---|---|
| The relay and list tests in `Gateway.UnitTests` (`FullyQualifiedName~CcDirector.Gateway.UnitTests.Boxes` and `~CcDirector.Gateway.Tests.Boxes`) | the head, `4c80bb012` | 168 passed, 0 failed |
| The scope line `using var scope = tenantBoundary.EnterScope(session.Tenant)` removed from `ServeDesktopHostAsync`, then `DesktopRelayLegTests` | the head, reverted after | 18 of 47 failed, among them `DesktopPage_OnAHostedGateway_SendsTheOpenInsideTheBoxSessionsAccount`; the file was restored and the tree is clean |
| The hosted Gateway log of the new end-to-end run searched for `command DROPPED` | `hosted/gateway-log.txt` | 0 lines; the log's first line is the hosted startup contract satisfied (hosted mode, auth, HTTPS public address, PostgreSQL) |
| The pull request's merge state on GitHub | #4280 | `mergeable: true, mergeable_state: clean` at `4c80bb012` |

**Could not reach:** Azure, a real network, a phone, the Cockpit. The hosted end-to-end run was read, not re-made;
its Gateway was the hosted launcher on this machine with a throwaway PostgreSQL, which is the production shape in the
one respect finding 1 turned on (the tenant-scope deny), and the reviewer checked that respect in its log.

## Finding 1 of round 1: removed

`ServeDesktopHostAsync` (`BoxDesktopEndpoints.cs:214-217`) now enters the box session's account as the tenant scope
the moment the cookie has been checked, so the Boxes switch, the Director lookup and `legs.ServeAsync` all run inside
it; the scope is an `AsyncLocal`, so it reaches `GatewayHost.SendCommandAsync` through `DesktopRelayLegs.OpenAsync`.
`UseDesktopHost` takes the boundary as a required parameter and `GatewayHost.cs:4836` passes `_tenantBoundary`. The
rig's fake send (`DesktopRelayRig.cs:127-141`) now refuses with no scope and finds the Director only in the scope's own
account, which is the hosted rule; the reviewer's experiment confirms the suite depends on the line (18 failures without
it). The hosted run's log has no dropped command across its ticket requests and streams. The harm is gone, and it is
pinned.

## Finding 2 of round 1: removed

The branch is rebased onto main past the list's merge; the stand-ins and the patch file are deleted; the wiring is an
ordinary commit (`4f91c2831`); the record names the two conflicts and how each was resolved (both parameters kept on
each constructor). GitHub reports the pull request mergeable and clean. The reviewer's trial merge of round 1 is no
longer needed, and there is nothing left for a later hand to guess.

## Finding 3 of round 1: removed

`DesktopRelayLimits.CostOf` (`DesktopRelayMessages.cs:208-209`) now charges nothing for a Closed frame, on both ends
(the same constant is the sender's acquire at `DesktopRelayPump.cs` and the receiver's count at line 131), so a close
sent with the window exactly full arrives as itself. The new test
`DesktopRelayPumpWindowEdgeTests.RunAsync_FarSideClosesWithTheWindowExactlyFull_TheEndpointGetsTheFarSidesCloseNotAProtocolFault`
fills the window to the byte with the endpoint stalled, then closes with 4001, and asserts the endpoint gets 4001 and
the far side's reason. The reviewer agrees with the choice of the second remedy: a close must get through to a stalled
far side, so it must not wait for credit.

## The wiring commit (`4f91c2831`), reviewed by the same rules

- **The allow-list at the Director.** `DirectorDesktopRelayHandler` resolves a box id only through
  `HostedDesktopRegistry.Resolve(boxId, requester)`, which checks the id with `BoxIdRule.WhyInvalid`, finds it in the
  loaded list, and logs every refusal with the cleaned id, the requester and the reason; the relay logs its own refusal
  beside it, and the test asserts both lines. The port is still the list's answer and nothing else; `DesktopBoxAddress`
  is unchanged. A `GatewayStreamClient` built without a registry gets `new HostedDesktopRegistry(() => null)`, whose
  initial state is `Loaded.Empty`, so `Resolve` refuses every id with "this Director hosts no desktop with that box id"
  and never throws. `ControlApiHost` passes the one registry it also pushes from, so the Gateway's view and the
  Director's allow-list come from the same file.
- **The Gateway.** The Director for a box is `PushedHostedDesktops.TryLocate(tenant, boxId)?.DirectorId`, tenant first,
  and that store drops a Director's list the moment its current connection ends, so a located Director is a connected
  one of the session's own account; the stream owner recorded in `DesktopRelayRegistry` is that pair, and only that
  Director in that tenant may attach (unchanged). The ticket route is mapped inside `BoxesGate.Group`
  (`GatewayHost.cs:5358-5364`) and still asks the switch and the locator itself; the desktop host asks the same
  `BoxesSwitch.IsOn` of the session's tenant, inside the scope. `BoxIdRule` is at least as strict as the deleted
  stand-in (63 characters, lower-case letters, digits and hyphens, no leading or trailing hyphen), so the prefix, the
  cookie path and the redirect target it is built into cannot carry a slash, a dot or a query character.
- **Tenants stay apart.** Nothing in the wiring resolves a box across tenants: the locator, the switch and the scope all
  take the tenant from the redeemed session, which was minted on the main host from the caller's own account.
- No defect found.

## The two additions since round 1

- `Clear-Site-Data: "storage"` on the ticket exchange (`af1f98985`) wipes the desktop host origin's storage before any
  box page loads, and deliberately not "cookies", which would wipe the box cookie set on the same response. The test
  asserts the header. On the production desktop host this is the Azure name, where a Cockpit opened by that name may
  have left device keys; wiping them there is the point. No harm found.
- The desktop host's 404 for any other route (`8497f4384`) now names the Gateway's public address and logs method,
  path and user agent, with control characters stripped and the length capped, and a path under `/boxes/` logged as
  the cleaned box id only. No harm found.

## Findings

None.

## Verdict

All three findings of round 1 are removed and each is pinned by a test or a run; the wiring commit holds the allow-list
at the Director, the tenant boundary at the Gateway, and the Boxes switch on both hosts. No finding proves harm.
Mergeable from this seat.
