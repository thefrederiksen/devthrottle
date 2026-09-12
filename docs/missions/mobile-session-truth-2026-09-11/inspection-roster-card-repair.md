# Independent inspection: mobile roster-card repair

Date: 2026-09-12

Inspected commit: `7499d8d76ec6624a477c6804dcdc7abbbe4fd68d`

## Verdict

**PASS**

The repair closes the prior served-path proof defect. The new host-bound tests start the Gateway in
hosted mode, enroll authenticated devices in separate tenants, connect real tunnel Directors, await
their pushed snapshots, call the actual authenticated `GET /sessions?envelope=true` route, and inspect
the response body. The assertions prove both route stamping and response serialization, while the
separate control proves missing authentication is rejected and tenant filtering remains effective.

## Findings by severity

No high, medium, or low severity findings were found.

## Evidence reviewed

- `src/CcDirector.Gateway.Tests/AgentToolDisplayRouteTests.cs` uses hosted tenant enrollment, two
  authenticated tunnel Directors, and awaited `PushSnapshotAsync` calls before making route requests.
- `Authenticated_tenant_sessions_route_serializes_tool_from_agent_when_model_disagrees` asserts the
  raw agent token, the conflicting selected model, and the folded display value from the actual response
  body. A missing route stamp or changed response serialization would fail this test.
- `Authenticated_tenant_sessions_route_serializes_loud_tool_label_when_agent_is_absent` proves the
  missing-agent display value is stamped and serialized through the served route.
- `Sessions_route_authentication_and_tenant_selection_control` proves the authentication and tenant
  controls independently: an unauthenticated request receives unauthorized status, and tenant A
  receives its own session without tenant B's session.
- `src/CcDirector.Gateway/Api/GatewayEndpoints.cs` stamps `AgentToolDisplay` from `AgentToolDisplayFold`
  during the shared roster assembly, independently of `CurrentModel`.
- `AgentToolDisplayFold` maps known tool tokens, trims unknown future tokens, and gives missing tokens
  the explicit `Agent tool not reported` value without consulting model data.
- `SessionDto` and the browser schema carry the field additively, with the browser field optional for
  compatibility with older Gateway responses.
- `Home` renders the Gateway-owned field on the shared `SessionRow`, which is used by every roster
  group. It does not derive the tool from the selected model.
- The assembled Home test exercises the polling request, grouping into `Other sessions`, retention
  flow, and the real row component. The retention tests prove a newer live display value replaces an
  older retained value.
- The card styles keep the tool metadata compact, wrapping chips on narrow screens without hiding the
  value.

## Focused checks

- Hosted route tests: three passed.
- Gateway display-fold tests: sixteen passed.
- Client retention tests: twenty-four passed.
- Mobile roster-card tests: five passed.
- Diff whitespace check: passed.

The first hosted route attempt was blocked by a simultaneous build holding a file lock. A foreground
retry after that build completed passed all three route tests; no process was terminated.

## Remaining proof gaps

- No physical-device or screenshot evidence proves final pixel layout on a narrow phone.
- The full repository gate and complete backend and browser suites were not run for this inspection.

These gaps do not undermine the repaired acceptance claim: the Gateway served path and the assembled
mobile roster path are now directly exercised, and the model-substitution and route-stamp mutations
described in the repair report are resisted by the added tests.
