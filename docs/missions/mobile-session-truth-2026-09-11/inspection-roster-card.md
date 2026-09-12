# Independent inspection: mobile roster-card slice

Date: 2026-09-12

## Verdict

**FAIL**

The product wiring is internally consistent, and the focused tests pass, but the slice does not prove
the claim that the Gateway served path enriches and serializes the field. The decisive test invokes an
internal helper and then serializes the already-mutated object. It does not call the `/sessions` route,
pass through its authentication and tenant resolution, or verify the response body produced by that
route. The report calls these "served-path facts", which overstates the evidence.

## Findings by severity

### High — served-path enrichment and serialization are not tested

**Evidence**

- `src/CcDirector.Gateway.UnitTests/AgentToolDisplayFoldTests.cs:53-77` calls
  `StampFleetRolesAndFold` through the test helper at lines 79-83 and then calls
  `JsonSerializer.Serialize` on the same in-memory object.
- `src/CcDirector.Gateway/Api/GatewayEndpoints.cs:1138-1597` contains the actual `/sessions` route and
  its request-specific fleet assembly. No test in the changed slice invokes this route.
- `src/CcDirector.Gateway/Api/GatewayEndpoints.cs:5125-5133` does stamp the field in the shared fold,
  and `src/CcDirector.Gateway/GatewayHost.cs:4520` uses that fold for the display sweep. This supports
  the implementation, but it is not proof that the roster route reaches the fold and emits the field.

**Impact**

A regression in the route's enrichment order, tenant-selected fleet, response projection, or route
serialization could leave the focused tests green while the mobile roster receives no display value.

### Medium — the mobile test exercises the real row component, but not the assembled Home roster

**Evidence**

- `apps/mobile/src/pages/Home.tsx:599-696` is the real `SessionRow` used by the main roster at
  `apps/mobile/src/pages/Home.tsx:438-449`.
- `apps/mobile/src/pages/HomeRosterCard.test.tsx:13` imports that real component, and the four tests
  pass.
- The test renders `SessionRow` directly at `apps/mobile/src/pages/HomeRosterCard.test.tsx:54-60`;
  it does not render the `Home` page's grouping, filtering, or polling path.

**Assessment**

This is not a product defect in the card path. It is a proof boundary: the changed component is the
real main-card component, but page assembly remains untested by this slice.

### Low — no direct regression test covers a retained value being replaced by a newer live value

**Evidence**

- `packages/client-core/src/fleet/rosterRetention.ts:264-304` replaces the retained Director list with
  the current live list when the Director is unreachable and live rows are supplied.
- `apps/mobile/src/pages/HomeRosterCard.test.tsx:63-86` proves one live value survives a later retained
  read, but does not prove that a later live row with a changed `agentToolDisplay` replaces the older
  value.

**Assessment**

The merge implementation preserves object fields by retaining the complete session object and replacing
the list with the supplied live objects. This is a named proof gap, not an observed failure.

## Verified strengths

- `src/CcDirector.Gateway.Contracts/AgentToolDisplayFold.cs:15-28` reads only the Director-reported
  agent token, maps the known display spellings, preserves an unknown trimmed token, and returns the
  explicit `Agent tool not reported` value when the token is absent.
- `src/CcDirector.Gateway/Api/GatewayEndpoints.cs:5129-5133` stamps the display value independently
  from the selected model.
- `src/CcDirector.Gateway.Contracts/SessionDto.cs:18-24` adds an additive, non-null contract property,
  while `packages/client-core/src/api/schema.ts:10878-10884` exposes the compatible optional browser
  field.
- `apps/mobile/src/pages/Home.tsx:603-606` reads only the Gateway display field and uses the explicit
  missing-value label; `apps/mobile/src/pages/Home.tsx:690-696` renders it on every `SessionRow`.
- `apps/mobile/src/styles.css:543-612` keeps the metadata compact, wraps it on narrow screens, and
  gives the new tool chip a distinct but restrained marker.
- The changed Gateway tests cover Claude Code, Pi, Codex, Gemini, OpenCode, Cursor, Grok, GitHub
  Copilot, Custom CLI, an unknown future token, and absent values.
- The mobile tests prove model independence, Gateway spelling, absent-value visibility, retention of
  the existing session name, and retention of the tool field.
- Focused checks run against `e51b9205172f876defdcd4910d20ceac14e0f8e5` passed: sixteen Gateway unit
  tests and four mobile roster-card tests.

## Named proof gaps

1. No authenticated or unauthenticated `/sessions` route test proves that the served response performs
   enrichment and includes the serialized field.
2. No test proves tenant resolution and route-specific fleet filtering preserve the field.
3. No test renders the assembled `Home` page through its actual roster polling and grouping path.
4. No direct retention test proves replacement of an older display value by a newer live value.
5. No narrow-device browser or screenshot proof establishes the final compact layout.

The implementation should not be marked ready for merge until the first gap is closed or the acceptance
claim is narrowed to the internal fold and object serialization that are actually tested.
