# Proof - the team's shared skills and workflows, Gateway part (devthrottle_internal#2304)

Branch `teams/2304-shared-skills`, 4 October 2026, cut from origin/main at cdaab4f2b (after #2300 and #2302 merged).
Everything below ran locally on SOREN_NORTH. No production database and no deploy was touched. **No schema changed.**

This is the Gateway part. The Skills and workflows page (screen S5) is built on the Cockpit team switcher that #2312's
Developer is putting into `packages/client-core`; it comes in a second pull request once that switcher is on main, with
its own screenshot added here.

## What already existed, and what was missing

A team is a tenant, and the skill and workflow libraries were already tenant-scoped. So most of #2304 was already
there:

| Already on main | What it gives a team |
|---|---|
| `SkillStore`, `WorkflowStore` answer for the AMBIENT tenant | Inside a team's tenant they read and write the team's own skills and workflows, beside DevThrottle's built-ins. Nothing to add. |
| Built-ins held read-only in a shared library partition | A team cannot edit or delete a built-in, exactly as a person cannot. |
| `SkillTenantOverrideEntity`, `WorkflowTenantOverrideEntity` | A team's own on/off choice for a built-in. |
| `TeamEndpointRules` already declares `/gateway/skills` and `/gateway/workflows` (read = use, write = change), and `TeamEndpointGate` decides them | A session whose key is bound to the team's tenant is allowed to read the team's library (Owner, Manager, Developer), refused a change unless Owner or Manager, and a Collaborator is refused both. |

What was missing:

1. **A way for the Owner or a Manager to manage the team's library from their own account.** Their device key is
   bound to their personal tenant, so `/gateway/skills` answers with their personal library. Nothing reached a
   team's.
2. **What the page needs to show what the Gateway decided** - whether this person may change the library, and the
   sentence when not (rule 7).
3. **Proof** that a skill added by a Manager reaches a Developer on that team and no other team.

What is still missing and is NOT this issue: a session's own pull through a key bound to the team's tenant is not yet
accepted. Today's hosted device registry refuses such a key, and the gate cannot name the person behind it. Both are
#2311 (a Director set up for a team). See "The session side" below.

## What was built

| Piece | File | What it is |
|---|---|---|
| The team's library routes | `src/CcDirector.Gateway/Api/TeamLibraryEndpoints.cs` | The EXISTING skill and workflow routes mounted a second time under `/teams/{teamId}/skills/...` and `/teams/{teamId}/workflows/...`, same shapes, one set of handlers. Each request enters the team's tenant only after the gate allowed it in that team. Plus `GET /teams/{teamId}/library`, the page's one read. |
| One set of handlers | `Api/SkillEndpoints.cs`, `Api/WorkflowEndpoints.cs` | `Map(app, store, root)`: the route root is a parameter (default `/gateway/skills`, `/gateway/workflows`, unchanged). No handler was copied. |
| The rules | `Teams/TeamEndpointRules.cs` | `/teams/{teamId}/skills` and `/teams/{teamId}/workflows`: read = use the team's shared skills and workflows, anything else = change them; `/teams/{teamId}/library` (exact) = use. The team comes from the route. |
| The gate's record | `Teams/TeamEndpointGate.cs` | When the gate ALLOWS a request in a team it records that team on the request (`AllowedTeam`). The library routes enter a team only when that record names the very team in the route - so a route that somehow meets no gate refuses (403) instead of serving a team's rows unchecked. |
| Dark | `GatewayHost.cs` | Mapped only while `CC_GATEWAY_TEAMS=1`, beside the other team routes. |

The permission is decided in exactly one place: `TeamEndpointGate` -> `TeamAccess.Decide` -> the role table. The
library read asks `TeamAccess.Decide(team, caller, ChangeSharedSkillsAndWorkflows)` for the `canChange` answer the
page shows; it does not write a second rule.

### `GET /teams/{teamId}/library`

```json
{
  "team": { "id": "...", "name": "Library", "role": "Developer" },
  "canChange": false,
  "changeRefusal": "In this team you are a Developer, and a Developer may not change the team's shared skills and workflows.",
  "builtInNote": "DevThrottle's own built-in skills and workflows are available to every session as well. They are not listed here and cannot be changed.",
  "count": 2,
  "items": [
    { "id": "listed-flow", "name": "Team review", "summary": "...", "kind": "Workflow", "enabled": true, "version": 1,
      "changedAtUtc": "...", "changedBy": "manager@example.com", "canChange": false },
    { "id": "listed-skill", "name": "Release checklist", "summary": "...", "kind": "Skill", "enabled": true, "version": 1,
      "changedAtUtc": "...", "changedBy": "manager@example.com", "canChange": false }
  ]
}
```

`items` is the team's OWN skills and workflows. Each item's `canChange` is the caller's permission AND the store's
own verdict on that item. A Collaborator gets 403 with the gate's sentence; someone who is not a member gets the one
"no such team" 404.

## The session side, and what waits on #2311

The issue's proof asks for "a session started by a test Developer showing the team skill loaded". That needs a Director
set up for a team, which is #2311 and is not on main. Per the brief (Delivery Lead decision D3) it is **not faked**:

- **Proven now, at the Gateway** (`Issue2304Test1_TheSessionSide_...`): the gate the hosted Gateway installs ALLOWS
  `GET /gateway/skills`, `GET /gateway/skills/{id}/body` and `GET /gateway/workflows` in the team's tenant with the
  Developer as the caller, and the skill store, inside the team's tenant, lists the Manager's skill and serves its body;
  inside the other team's tenant it does neither. A Collaborator's pull and a Developer's change are refused.
- **Waits on #2311**: the real request with a key bound to the team's tenant. Today that key is refused at
  authentication (pinned by #2302's `OverTheWire_AKeyBoundToATeamsTenant_IsNotAcceptedToday...`), and the gate cannot
  yet name the person behind it. **The session-level proof - a session started by a test Developer showing the team
  skill loaded - waits on #2311.**
- **Seam raised to the Tech Lead (4 Oct) and relayed for #2311**: a session fetches a skill body with its own SESSION
  key, whose identity is (session, tenant, Director) - no person. #2311's brief teaches the gate the caller for a team
  DEVICE key only. Unless #2311 also answers the caller for a team session key (from its Director's credential), a team
  session's own `cc-devthrottle skill get` stays refused as unidentified.

## The tests

The three from #2304, and the rest the brief asks for. Gateway suite: `src/CcDirector.Gateway.Tests/Teams/`;
unit suite: `src/CcDirector.Gateway.UnitTests/Teams/`.

| Test | Where | Result |
|---|---|---|
| #2304-1: a skill added by a Manager is available to a Developer on that team, and to no other team (and not to anyone's personal library) | `HostedTeamLibraryTests.Issue2304Test1_ASkillAddedByAManager_...` | pass |
| #2304-1, workflows | `HostedTeamLibraryTests.Issue2304Test1_AWorkflowAddedByAManager_...` | pass |
| #2304-1, the session side at the gate and the store | `HostedTeamLibraryTests.Issue2304Test1_TheSessionSide_...` | pass (the over-the-wire part waits on #2311) |
| #2304-2: a Developer's create, change, publish, switch-off, clone and remove of a team skill is refused by the server (403, `team_action_refused`), and nothing changed | `HostedTeamLibraryTests.Issue2304Test2_...TeamSkill...` | pass |
| #2304-2, workflows | `HostedTeamLibraryTests.Issue2304Test2_...TeamWorkflow...` | pass |
| #2304-3: a Collaborator cannot list the skills, the workflows, or the library | `HostedTeamLibraryTests.Issue2304Test3_ACollaborator_CannotListThem` (3 cases) | pass |
| Owner and Manager change and remove a team skill | `HostedTeamLibraryTests.OwnerAndManager_ChangeAndRemoveATeamSkill` (2 cases) | pass |
| Someone who is not a member is told there is no such team | `HostedTeamLibraryTests.SomeoneWhoIsNotAMember_...` (4 cases) | pass |
| Built-ins stay read-only inside a team, even for its Owner | `HostedTeamLibraryTests.BuiltInSkillsAndWorkflows_StayReadOnlyInsideATeam_...` | pass |
| A personal account's own skills behave exactly as before, and never reach the team | `HostedTeamLibraryTests.APersonalAccountsOwnSkills_...` | pass |
| The page's read says what each role may do (Owner, Manager, Developer) | `HostedTeamLibraryTests.Library_SaysWhatTheCallerMayDo_...` (3 cases) | pass |
| Routes absent while `CC_GATEWAY_TEAMS` is off (read from the real route table), personal library still answers | `HostedTeamsDarkTests.SwitchUnset_TheTeamLibraryRoutesAreAbsent_...` | pass |
| The route-table walk: every new endpoint states its action, no rule is stale | `TeamEndpointWalkTests` (unchanged, now covers the new routes) | pass |
| The rules match the new routes | `TeamEndpointRulesTests` (8 new cases, 2 new tests) | pass |
| The gate records the team it allowed, and only then | `TeamEndpointGateTests.Check_AnAllowedRequest_...`, `RunAsync_AllowedInATeam_...`, `RunAsync_RefusedOrNotATeamRequest_...`, `AllowedTeam_NoContext_Throws` | pass |
| A library route enters a team only on the gate's record for that team; self-hosted refused; session keys refused | `TeamLibraryEndpointsTests` (12 cases) | pass |
| The existing skill and workflow routes are unchanged by the refactor | `SkillEndpointsTests`, `WorkflowEndpointsTests`, `SkillLibraryReachesASessionTests` | pass |

## Every test can fail - three revert checks

Each broke one rule, ran the tests that guard it, and restored the source (committed first; restored in `finally`).
Output in `test-runs.txt`.

| Broken | Tests run | Result |
|---|---|---|
| A. The role table: a Developer may change the shared skills and workflows, a Collaborator may use them | `Issue2304Test2*`, `Issue2304Test3*` | 5 of 5 FAILED |
| B. The library routes no longer enter the team's tenant | `Issue2304Test1_ASkill...`, `Issue2304Test1_AWorkflow...` | 2 of 2 FAILED |
| C. The library routes enter the team without the gate's record | `TeamLibraryEndpointsTests` | `TeamToEnter_TheGateNeverRan_ReturnsNull` FAILED |

## The checks run before reporting

See `test-runs.txt`. In short: the Gateway suite filtered to Teams, Skill and Workflow, 100 of 100; the whole Gateway
unit suite, 8,355 passed, 8 skipped, 0 failed (one earlier run of the same code had 1 failure whose name the summary cut
off; it did not recur); the default local gate, nine suites green and `CcDirector.Core.UnitTests` stopped for running
past the 120-second budget, then green on its own (1,148). No web or Python code changed in this part.

## Schema

None. No table, column, index or migration was added or altered.
