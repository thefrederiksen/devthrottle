# Factories need Pro - Gateway proof (issue #4326)

The Factory Agents area is on for an account when ANY of three grants holds: the machine switch, the owner's
per-account switch, or, on the hosted Gateway only, a plan that carries the new `factories` scope (Pro, a team seat,
the retired `hosted` tier and the pre-column legacy row). A plan that cannot be read is Unknown, never off and never
on. One decision, `FactoryAgentsSwitch.Answer`, is read by every factory route, the switch question, the roster chip,
the administrator read and the scheduler.

All runs below were on SOREN_NORTH, 21 October 2026, Debug, from the worktree `D:\wtnp-gateway`.

## Commands and counts

| Command | Result |
|---|---|
| `.\scripts	est-local.ps1` at `42f6f7dbe` (this change rebased on `1e3daab40`), tree clean | **GREEN**: all 14 suites `outcome=Completed`, 0 failed. Among them `CcDirector.Gateway.UnitTests` (the 288 parked classes this change reaches) 4,751 passed, `CcDirector.Core.Tests` (30 reached classes) 303 passed, `CcDirector.Core.UnitTests` 2,602 passed. |
| `.\scripts	est-local.ps1 -Gateway` (the parked suite the COVERAGE GAP line names) | 1,779 passed, 3 skipped, 0 failed, all four shards Completed. Run on the change before the rebase, on base `8f3cd136c`; the rebase brought only a release commit and a test-only fix (#4332). |
| `dotnet test CcDirector.Gateway.UnitTests --filter "FullyQualifiedName~CcDirector.Gateway.Tests.Factory.\|FullyQualifiedName~CcDirector.Gateway.Tests.Tenancy."` | 2,600 passed, 0 failed. |
| `dotnet test CcDirector.Gateway.UnitTests --filter "FullyQualifiedName~CronEngine\|FullyQualifiedName~FactoriesNeedProTests"` | 75 passed, 0 failed. |
| `dotnet test ... --filter "FullyQualifiedName~FactoriesNeedProTests"` (the new tests) | 50 passed, 0 failed. |
| `npm run typecheck` in `packages/client-core` | clean. |

The first default run, before the rebase, had one red test in `CcDirector.Core.UnitTests`:
`GateTestsDoNotDependOnTheMachineTests` named `SessionCredentialAfterLateEnrollTests.cs`, a file added on main by
`df943ef86` and not touched here. Main fixed it in `1e3daab40` (#4332); the run above is after that.

**The new tests can fail.** Two mutations, each restored afterwards:

- Remote given the `factories` scope: 6 of the 50 went red (the Remote rule row, the Remote 404, the Remote switch
  question, the scope table, the Remote schedule, the administrator read).
- The plan read honoured even when the gate does not gate (`TenantAiGate.SelfHost`): 2 went red (both self-hosted
  tests).

## What each test group proves (`Factory/FactoriesNeedProTests.cs`)

**The scope table.** `factories` is carried by Pro, Team, the retired hosted tier and the null (legacy) tier, and by
nothing else: not Remote, not Free, not the self-host plan, not an unknown tier string. This is the table every plan
question reads, so a wrong line here would be wrong everywhere.

**The rule, every row.** Real database, real `EntitlementRegistry` with the trial ledger, real team bill, real
`TenantAiGate`, fixed clock. With no owner switch: Pro, Team with a running bill, legacy, hosted and past-due Pro
inside its payment grace are ON by the plan; Free, Remote, a team with no bill, canceled Pro and past-due Pro after its
grace are OFF with `plan_required`; an unreadable entitlement table is UNKNOWN with `plan_unknown`. With the owner
switch on, every row is ON by the owner switch - including the unreadable one, which proves a switched-on account
never depends on the plan read (the owner switch is asked first). With the machine switch on, every row is ON by the
machine switch. An account switched off by hand keeps factories by its Pro plan (off removes only the owner's grant).
No account bound on the hosted Gateway is off, never on. The decision is logged with its grant
(`grant=plan tier pro`, `grant=owner switch`), and the quiet variant used by the roster gives the identical answer
with no line.

**Self-hosted is unchanged.** The test first shows the trap is real: `TenantAiGate.SelfHost` answers Allowed for
`factories`. A switch handed that gate, or no gate, still answers OFF with `not_switched_on` when the machine switch is
off, and `ChecksPlans` is false. Machine switch on is on; an owner switch on the local account still works.

**The routes and the switch question.** The real gate and a real factory route (`GET /gateway/factory/activity`),
invoked in process with no port. Pro: 200. Remote: 404 with an empty body, as if unmapped (R3). Free with the owner
switch: 200. Unreadable plan: 503, `Retry-After: 10`, code `entitlement_unknown` - the same answer the access lease
writes in `AuthMiddleware` for `HostedAccessDecision.RetryUnknown` and `HostedAiHttp.TenantAiRefusal` gives an Unknown
model call. The switch question is always 200: Pro gives `enabled: true` and every other field null; Remote and Free
give `plan_required` with the title "Factories come with Pro", the pricing URL and the upgrade body (which mentions no
trial); unreadable gives `plan_unknown` with a sentence and no upgrade fields; self-hosted with the machine switch off
gives `not_switched_on` with today's sentence. The 404 sentence of the routes mapped outside the gate (talk, the
owner's actions, the Factory Manager's talk) names the plan only when the plan is why, and is otherwise unchanged.

**The administrator read reports the grant.** `GET /gateway/admin/factory-agents` gives `grant: "plan tier pro"` for
a Pro account, `"owner switch"` for a Free account switched on, and `reason: "plan_required"` for Remote.

**The scheduler (R2).** The real `CronEngine` asking the real switch, with one factory seat's schedule and one plain
schedule both due. Remote: only the plain schedule starts a session; the factory seat's run is recorded as did-not-run
with status `factory-agents-off` and a reason naming Pro and Teams, and its schedule moves on to its next hour. Pro and
Team: both start. Free switched on by the owner: the factory schedule starts. Unreadable plan: the factory schedule
is skipped this pass - nothing recorded, still due, so the next sweep asks again - while the plain schedule starts, and
a run-now of it answers `SkippedFactoriesUnknown` (503 at the route).

The existing `FactoryAgentsSwitchTests`, `FactoryTalkEndpointTests`, `FactoryOwnerActionEndpointTests` and
`CronEngineCheckedFireTests` still pass unchanged in what they assert; the last was only moved from a yes/no delegate
to the decision type.
