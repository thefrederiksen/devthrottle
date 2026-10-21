# Factories need Pro - Gateway proof (issue #4326)

The Factory Agents area is on for an account when ANY of three grants holds: the machine switch, the owner's
per-account switch, or, on the hosted Gateway only, a plan that carries the new `factories` scope (Pro, a team seat,
the retired `hosted` tier and the pre-column legacy row). A plan that cannot be read is Unknown, never off and never
on. One decision, `FactoryAgentsSwitch.Answer`, is read by every factory route, the switch question, the roster chip,
the administrator read and the scheduler.

After the review (`devthrottle_internal/docs/missions/factories-need-pro-2026-10-21/REVIEW-gateway.md`), the factory
gate also covers a factory's memory, briefs, definition (and a new one from a folder), records, a seat's kit and the
factory email, so an account without factories cannot define, publish or fill one. A test reads the real hosted route
table to prove every factory route is behind the gate, except a written list of routes that ask the decision
themselves.

All runs below were on SOREN_NORTH, 21 October 2026, Debug, from the worktree `D:/wt/fnp-gateway`.

## Commands and counts

The review round, on base `bec74ac1c` plus the review fixes (the tree that was then committed as the review commit):

| Command | Result |
|---|---|
| `./scripts/test-local.ps1` | **GREEN**: all 14 suites `outcome=Completed`, 0 failed. `CcDirector.Gateway.UnitTests` (the 288 parked classes this change reaches) 4,756 passed; `CcDirector.Core.Tests` (30 reached classes) 303 passed; `CcDirector.Core.UnitTests` 2,602 passed. |
| `./scripts/test-local.ps1 -Gateway` (the parked suite the COVERAGE GAP line names) | **GREEN**: 1,780 passed, 3 skipped, 0 failed, all four shards Completed. The 3 skips were already there on main. |
| `dotnet test CcDirector.Gateway.UnitTests --filter "FullyQualifiedName~CcDirector.Gateway.Tests.Factory.\|FullyQualifiedName~CcDirector.Gateway.Tests.Tenancy."` | 2,605 passed, 0 failed. |
| `dotnet test CcDirector.Gateway.UnitTests --filter "FullyQualifiedName~FactoriesNeedProTests\|FullyQualifiedName~CronEngineCheckedFireTests"` | 63 passed, 0 failed (`FactoriesNeedProTests` alone: 55). |
| `dotnet test CcDirector.Gateway.Tests --filter "FullyQualifiedName~EveryFactoryRouteIsBehindTheGateTests"` | 1 passed. |

The first round, at `42f6f7dbe` on a clean tree: `test-local.ps1` GREEN (Gateway.UnitTests 4,751), `-Gateway` 1,779
passed and 3 skipped, Factory and Tenancy by name 2,600, the new tests 50; `npm run typecheck` in
`packages/client-core` clean. Before that commit was rebased, one test had failed in `CcDirector.Core.UnitTests`, on a
file main had added (`df943ef86`); main fixed it in `1e3daab40` (#4332).

**The tests can fail.** Three mutations, each restored afterwards:

- Remote given the `factories` scope: 6 of the first 50 went red (the Remote rule row, the Remote 404, the Remote
  switch question, the scope table, the Remote schedule, the administrator read).
- The plan read honoured even when the gate does not gate (`TenantAiGate.SelfHost`): 2 went red (both self-hosted
  tests).
- The memory routes mapped on the bare app again instead of the gate: the route-table test went red, naming
  `GET /factory-memory/notes` as not gated.

## What each test group proves (`Factory/FactoriesNeedProTests.cs`, unless named)

**The scope table.** `factories` is carried by Pro, Team, the retired hosted tier and the null (legacy) tier, and by
nothing else: not Remote, not Free, not the self-host plan, not an unknown tier string.

**The rule, every row.** Real database, real `EntitlementRegistry` with the trial ledger, real team bill, real
`TenantAiGate`, fixed clock. With no owner switch: Pro, Team with a running bill, legacy, hosted and past-due Pro
inside its payment grace are ON by the plan; Free, Remote, a team with no bill, canceled Pro and past-due Pro after its
grace are OFF with `plan_required`; an unreadable entitlement table is UNKNOWN with `plan_unknown`. With the owner
switch on, every row is ON by the owner switch - including the unreadable one, which proves a switched-on account
never depends on the plan read. With the machine switch on, every row is ON by the machine switch. An account switched
off by hand keeps factories by its Pro plan. The decision is logged with its grant, and the quiet variant gives the
identical answer with no line.

**Self-hosted is unchanged.** `TenantAiGate.SelfHost` answers Allowed for `factories` (the trap is real); a switch
handed that gate, or no gate, still answers OFF with `not_switched_on` when the machine switch is off. Machine switch on
is on; an owner switch on the local account still works.

**The routes and the switch question.** The real gate and a real factory route, in process, no port. Pro: 200. Remote:
404 with an empty body. Free with the owner switch: 200. Unreadable plan: 503, `Retry-After: 10`, code
`entitlement_unknown`, the answer `AuthMiddleware` writes for `HostedAccessDecision.RetryUnknown`. The switch question is
always 200, with each reason carrying exactly the fields in API-switch-question.md. The 404 sentence of the routes
mapped outside the gate names the plan only when the plan is why.

**A session's own key, through the gate (review finding 1).** The real hosted tenant boundary and
`GatewayEndpoints.ResolveReadTenant` - the resolver GatewayHost hands both the gate and the route - with the memory
route mapped INTO the gate, and a verified session identity on the request. A switched-on Free account's session
reaches its factory's memory (200, the factory read from the session); a Pro account's does too; a Remote account's
gets 404 with no body. This proves the gate resolves the same account the route does for a session credential.

**Every factory route is behind the gate (`CcDirector.Gateway.Tests/EveryFactoryRouteIsBehindTheGateTests.cs`).**
Reads the finalised route table of a real hosted `GatewayHost` and asserts every route whose path names a factory or a
trigger carries the gate's marker, except nine written out with why: the switch question, the two administrator verbs,
and six owner clicks (talk, the Factory Manager's talk, handling a problem, archive, restore, mark older items
handled) that ask the same decision themselves so a refusal can carry a sentence. It also names nine routes that must
be present and gated (memory, definition draft and create, kit, journal, brief, factory email, the Director's trigger
pull, the activity record), so an empty table cannot pass.

**A Director's own pulls are not logged (review finding 2).** Through the real gate filter: a
`/directors/{id}/triggers` pull adds no `[FactoryAgentsSwitch]` line, a `/triggers` request a person makes adds one.

**The administrator read reports the grant.** `"plan tier pro"` for a Pro account, `"owner switch"` for a Free account
switched on, `reason: "plan_required"` for Remote.

**The scheduler (R2).** The real `CronEngine` asking the real switch. Remote: only the plain schedule starts; the
factory seat's run is did-not-run with `factory-agents-off` and a reason naming Pro and Teams, and its schedule moves
on. A Remote account's CHECKED schedule now says the plan too (review finding 4): its last status is
`check-due: factories come with the Pro and Teams plans ...`. Pro and Team: both start. Free switched on: the factory
schedule starts. Unreadable plan: the factory schedule is skipped this pass and left due, the plain one starts, and a
run-now answers `SkippedFactoriesUnknown`.

The existing `FactoryAgentsSwitchTests`, `FactoryTalkEndpointTests`, `FactoryOwnerActionEndpointTests` and
`CronEngineCheckedFireTests` still pass unchanged in what they assert.
