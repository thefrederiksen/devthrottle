# Proof - who can do what, enforced everywhere (devthrottle_internal#2302)

Branch `teams/2302-permissions`, 3 October 2026, cut from origin/main at bdf7b5411 (after #2300 merged). Everything
below ran locally on SOREN_NORTH. No production database and no deploy was touched. No schema changed.

## What was built

| Piece | File | What it is |
|---|---|---|
| The role table | `src/CcDirector.Gateway/Teams/TeamPermissions.cs` | The 11 rows of #2098 plus three named actions, as data. One line per row, one cell per role. Every answer reads this table; nothing compares role ranks. |
| The one question | `src/CcDirector.Gateway/Teams/TeamAccess.cs` | `Decide(team, person, action)`: role from `gateway.team_members`, answer from the table. Not a member - no, for everything. |
| What each endpoint states | `src/CcDirector.Gateway/Teams/TeamEndpointRules.cs` | One list: which action each endpoint states inside a team. |
| The gate | `src/CcDirector.Gateway/Teams/TeamEndpointGate.cs` | Middleware after routing on the hosted Gateway. A request acting in a team goes on only if the endpoint states an action, the person is known, is a member, and the table gives their role the action - and, for private things, touches only their own. Anything else is refused by the server (403, code `team_action_refused`; 404 for a team route naming a team the caller is not in). |

The three actions beside the table, from #2098's own text:

- **See the members and roles** - "Shared with the team: ... the member list and roles". Every role.
- **Read another person's prompts** - prompts are "private to the person". No role.
- **Read the prompts the Mentor quotes on its page about a person** - the one exception, named so #2305 can use it.
  Owner and Manager, exactly where "read the Mentor's page about each person" is granted.

**Default deny.** An endpoint not in the list states no action and is refused inside a team. The walk test reads the
real hosted route table and proves every undeclared endpoint is refused, every endpoint of the named families states
one, and no rule in the list is stale.

**Who invites whom** (#2301). `TeamPermissions.ActionToAddOrRemove(role)` and `ActionToChangeRole` put the rule in the
table: adding or removing a Developer or Collaborator is the "invite or remove" row; adding or removing a Manager, and
any role change, is the Owner-only row. #2301 was not on main when this branch was cut, so nothing was folded; the
Tech Lead routes that fold.

## How the caller is identified, and what that means today

The person behind a request is found from the request's own device or session key: the key's tenant, and that
tenant's account subject - the same way #2300's team routes do it. Nothing a client sends names the caller.

A key bound to a **team's** tenant names no person this way (the team's tenant is not one person's), so the gate
refuses it as unidentified rather than guessing. Nothing yet records which member a session, Director or prompt inside
a team belongs to, so "does this request touch only the caller's own" cannot be shown, and is refused. Both are
supplied by **#2311** (a Director set up for a team, its own key, one person). So **today every request inside a team's
tenant is refused**, which is the safe direction. Today's hosted device registry also rejects a key bound to a tenant
its account does not own, so no such request even authenticates - `OverTheWire_AKeyBoundToATeamsTenant_...` pins that.

The one team endpoint a person reaches today is `GET /teams/{teamId}/members`, from their own account; it runs through
the gate over real HTTP for all four roles and a stranger.

For the per-cell tests, `TeamEndpointGate.Check` - the server's decision for one request - is called with the real
route pattern and a caller and ownership supplied by the test, standing in for what #2311 will supply. The middleware
tests call `RunAsync` with exactly what production supplies and show it refuses.

## The role table, cell by cell

Every cell has a policy test (`TeamPermissionsTests.Grant_EveryCellOfTheRoleTable_IsTheCellIn2098` against a hand copy
of #2098, and `TeamAccessTests.Decide_EveryCell_...` with the role read from the database). Where an endpoint exists
today, the cell also has an endpoint test (`TeamEndpointGateTests.Check_EveryCellWithAnEndpointToday_...`); where it
does not, `TeamEndpointGateTests.Decide_EveryCellWhoseEndpointWaitsOnALaterIssue_...` and the issue named below.

Y = yes, O = only their own, N = no. Every cell below passed in the run listed under Test runs.

| Row (#2098) | Owner | Manager | Developer | Collaborator | Endpoint tested today | Endpoint waits on |
|---|---|---|---|---|---|---|
| Run sessions on their own computers | Y pass | Y pass | Y pass | N pass | `POST /sessions/{sid}/prompt` (own session) | - |
| See the team's Fleet Map | Y pass | Y pass | O pass | N pass | `GET /directors` | #2312 cuts the list to a Developer's own; until then a Developer is refused it |
| Use the team's shared skills and workflows | Y pass | Y pass | Y pass | N pass | `GET /gateway/skills` | - |
| Read the Mentor's page about themselves | Y pass | Y pass | Y pass | N pass ("no sessions") | `GET /gateway/mentor-report` (the report's on/off setting) | #2305 builds the page |
| Answer questions, send requests, read reports sent to them | Y pass | Y pass | Y pass | Y pass | policy only | #2306-#2309 |
| Invite or remove Developers and Collaborators | Y pass | Y pass | N pass | N pass | policy only | #2301 (invite), #2303 (remove) |
| Read the Mentor's page about each person | Y pass | Y pass | N pass | N pass | `GET /gateway/mentor-report` (another person's) | #2305 builds the page |
| Change the team's shared skills and workflows | Y pass | Y pass | N pass ("no, to start") | N pass | `POST /gateway/skills` | - |
| Make someone a Manager, change roles | Y pass | N pass | N pass | N pass | policy only | #2303 (change role), #2301 (invite a Manager) |
| Billing, rename or delete the team | Y pass | N pass | N pass | N pass | policy only | #2299 (billing); no first-version issue adds rename or delete |
| Join or watch someone else's session | N pass | N pass | N pass | N pass | `GET /sessions/{sid}/buffer` (another person's) | - |
| See the members and roles (beside the table) | Y pass | Y pass | Y pass | Y pass | `GET /teams/{teamId}/members`, also over real HTTP | - |
| Read another person's prompts (beside the table) | N pass | N pass | N pass | N pass | `GET /prompts` (another person's) | - |
| Read the prompts the Mentor quotes (beside the table) | Y pass | Y pass | N pass | N pass | policy only | #2305 |

## The four tests from the issue

| Issue test | Where it is proven |
|---|---|
| 1. One test per cell, calling the API, all four roles; a "no" is refused by the server | The table above: `TeamEndpointGateTests.Check_EveryCellWithAnEndpointToday_...` (36 cases), `..._WaitsOnALaterIssue_...` (20), `TeamAccessTests.Decide_EveryCell_...` (56), `TeamPermissionsTests.Grant_EveryCell_...` (56); over real HTTP, `TeamEndpointWalkTests.OverTheWire_EveryRole_SeesTheMembersAndRoles` (4) and `..._SomeoneWhoIsNotAMember_...` |
| 2. A Collaborator calling any session, computer, Mentor or skills endpoint is refused | `TeamEndpointWalkTests.Issue2302Test2_...` - every such endpoint on the real hosted route table, as the Collaborator's own and as someone else's; `TeamEndpointGateTests.Check_ACollaborator_...` over every rule |
| 3. No role can read another person's live session or full transcript | `TeamEndpointWalkTests.Issue2302Test3_...` - all four roles, every session and transcript endpoint on the real route table; `TeamEndpointGateTests.Check_AnyRole_AnotherPersonsLiveSessionOrTranscript_IsRefused` |
| 4. A Manager reading another person's prompts is refused, except the Mentor's quotes | `TeamEndpointWalkTests.Issue2302Test4_...` - every prompt endpoint on the real route table; `TeamEndpointGateTests.Check_AManager_AnotherPersonsPrompts_...` (and the exception granted, as a named action) |

Also: `TeamEndpointWalkTests.EveryEndpoint_InATeam_IsRefusedUnlessItStatesAnAction` (the endpoint walk),
`EveryEndpointOfTheNamedFamilies_StatesAnAction`, `EveryRule_StatesTheActionOfAtLeastOneRealEndpoint`,
`NotAMember_EveryEndpoint_IsRefused`, and unit tests for every public method.

## Test runs (final code)

See `test-runs.txt` for the exact output.

| Run | Result |
|---|---|
| `.\scripts\test-local.ps1` (default gate) | 10 suites, all `outcome=Completed`, 3,463 passed, 0 failed |
| `CcDirector.Gateway.UnitTests`, whole suite (`dotnet test`) | 8,293 passed, 0 failed, 8 skipped (its PostgreSQL proofs, which run only under `-Parked`) |
| `.\scripts\test-local.ps1 -Gateway -Filter "FullyQualifiedName~CcDirector.Gateway.Tests.Teams"` | FILLED IN BELOW |
| Revert check 1: one cell flipped (Manager may change roles) | 4 tests red, green again restored |
| Revert check 2: the gate lets an undeclared endpoint through | 3 tests red, green again restored |

## Not run, and why

- **`CcDirector.Gateway.Tests` - including the new `TeamEndpointWalkTests`.** The filtered run queued behind the
  machine-wide Gateway lock (two other Teams sessions were running the suite), and after about 20 minutes of waiting
  it was stopped because the machine ran low on memory. Its throwaway PostgreSQL container was removed by hand. So the
  walk over the real route table, the over-the-wire members test, and #2300's existing hosted Teams tests have NOT run
  on this branch. An earlier one-off run of a scratch test against the same hosted Gateway (to read its route table)
  did complete, which is where the route list the walk uses came from. This run is owed before merge.

- **The whole `-Parked` run.** One foreground command here is capped at ten minutes and `-Parked` takes over an hour;
  the Tech Lead runs it in a separate terminal session.
