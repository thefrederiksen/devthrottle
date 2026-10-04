# Review: #2302 who can do what, enforced by the server (pull request thefrederiksen/devthrottle#3525)

Reviewed by a separate review session, 3 October 2026. Head 324897db8, base origin/main at bdf7b5411.

## Scope

**Read, in full:** the whole diff `origin/main...HEAD` (14 files): `TeamPermissions.cs`, `TeamAccess.cs`,
`TeamEndpointGate.cs`, `TeamEndpointRules.cs`, the `TeamRegistry.cs` and `GatewayHost.cs` changes, all five
unit test files, `TeamEndpointWalkTests.cs`, and the proof (`docs/proof/teams-2302/`). Around the diff:
`HostedTenantBoundary.cs`, `Api/TeamEndpoints.cs`, `TeamsReleaseSwitch.cs`, the middleware order in
`GatewayHost.cs` (lines 3753 to 3935), the tenant ownership check in `DeviceRegistry.cs`, and the list of
skills, workflow and Mentor routes. Issues #2302 and #2098 and the Developer's brief.

**Ran:** `dotnet test src/CcDirector.Gateway.UnitTests --filter "FullyQualifiedName~CcDirector.Gateway.Tests.Teams"`
in the review worktree: 336 passed, 0 failed, 0 skipped. This matches the proof's run 3.

**Could not reach:** `CcDirector.Gateway.Tests` (`TeamEndpointWalkTests`). I started it and it queued behind
the machine-wide lock held by another session's run; after six and a half minutes of waiting I stopped, with
no test executed. So the walk over the real route table and the three over-the-wire tests have no verdict
from me. I did not run the default gate, the whole `Gateway.UnitTests` suite, or `-Parked`. I did not repeat
the two revert checks.

**Checked by reading, and found sound (no finding):**

- The role table in code against #2098, cell by cell: all 11 rows by 4 roles agree, including the Fleet Map
  scope (Owner and Manager every Director, Developer their own, Collaborator no) and the Collaborator's
  "(no sessions)" as no. The hand copy in `RoleTableSpec.cs` also agrees with #2098, and it is a separate
  copy, so the per-cell tests can fail (the proof's revert check 1 shows four going red).
- A personal account sees no change. For a request whose key is bound to a personal tenant, the gate
  returns "not a team request" before asking who or whose, for every route except
  `GET /teams/{teamId}/members`, which answers as it did (same 404 sentence for a non-member). The gate is
  not installed on a self-hosted Gateway. The only new cost per hosted request is one in-memory set lookup;
  the team table it loads from comes from an ordinary migration, present whether or not Teams is released.
- Nothing personally identifying is logged: the gate logs method, route pattern (not the path), action and
  role; `TeamAccess` logs the team in its hashed form. No subject, no email.
- The claim that no request can run inside a team's tenant today holds: `DeviceRegistry` refuses a key whose
  account subject does not own the tenant it is bound to (line 223).
- Collaborator, another person's session or transcript, and a Manager on another person's prompts: refused
  in `Check` for every rule, by the table's cell and not by a rank comparison.

## Verdict

Not ready to merge as it stands: F1 is a condition on merge, F2 should be fixed in this pull request.
F3 and F4 are notes for the issues that follow.

## Findings

### F1 - blocker - the walk over the real route table has never run, and the proof reports it as passed

Location: `docs/proof/teams-2302/README.md` (sections "What was built", "The role table, cell by cell",
"The four tests from the issue", and the "Test runs" row that still reads "FILLED IN BELOW");
`src/CcDirector.Gateway.Tests/Teams/TeamEndpointWalkTests.cs`.

The harm: the proof says "The walk test reads the real hosted route table and proves every undeclared
endpoint is refused", lists `Issue2302Test2`, `Test3` and `Test4` as where three of the issue's four tests
are proven, and says the member list passed "also over real HTTP". Its own last section says none of that
ran. Nor did it run for me. So nothing has yet shown that the 26 rules match the real routes: whether a
rule is stale, whether a route in a named family states no action, or whether the hosted Gateway even
starts with the gate installed and existing hosted requests still pass. That file holds 11 test methods that
have never been executed once; whoever reads the top of the proof takes them as green. The run must happen on
this head before merge, and the proof must then say what actually ran (and lose the placeholder row).

Developer answer: Accepted. The proof overstated what ran; it will be rewritten from the Tech Lead's -Parked run, which executes TeamEndpointWalkTests. It will say exactly what ran, with no placeholder row and no claim beyond the run. That run is on 324897db8. The new head 70782b61e adds the F2 to F4 fixes, which touch the gate, the rules and IsTeam, so the walk owes a verdict on the new head too. I have not started a Gateway.Tests run (the lock).

### F2 - should-fix - a team route called from a person's own account is allowed by default, not refused

Location: `src/CcDirector.Gateway/Teams/TeamEndpointGate.cs` lines 123 to 127;
`src/CcDirector.Gateway/Teams/TeamEndpointRules.cs` line 92.

The harm: the gate treats a request as acting in a team only when its key is bound to a team's tenant, or
when a RULE says the team comes from the route. With no rule, `fromRoute` is false, and from a personal
account the answer is "not a team request" - the request goes on unchecked. That is the only way anyone
reaches a team today, and it is how #2301 (invite) and #2303 (remove, change role) will add their
endpoints. So a later `/teams/{teamId}/...` endpoint that forgets its rule - or a POST or DELETE on
`/teams/{teamId}/members`, which the one read-only rule does not cover - is served to any signed-in
account, a non-member included, without the role table being asked. Default deny as built protects only
requests inside a team's tenant, which cannot authenticate until #2311. The brief asked that a later
endpoint cannot skip the check silently; on this path it can.

The only thing standing behind it is `EveryEndpointOfTheNamedFamilies_StatesAnAction`: a test in the parked
suite the default gate does not run, keyed to the literal prefix `/teams/{teamId}` (a route with another
parameter name, or a team action under another prefix, is outside it). No test, in either suite, asserts
what the gate itself does with an undeclared team route from a personal account. The gate, not a test in a
parked suite, should refuse a route that names a team and states no action.

Developer answer: Accepted and fixed in 8ee7955ff. The GATE now refuses any route that names a team and states no action, from a personal account too, for a member and a stranger alike. A route names a team when it is under /teams/, or when any of its parameters has "team" in its name, in any case. That covers POST and DELETE on /teams/{teamId}/members today. GET and POST /teams (the caller's own list, and creating a team) name no team and are untouched. Proven in TeamEndpointGateTests.Check_AnUndeclaredRouteThatNamesATeam_FromAPersonalAccount_IsRefusedByTheGate (5 routes) and RunAsync_AnUndeclaredTeamRoute_FromAPersonalAccount_IsRefused, plus TeamEndpointRulesTests.NamesATeam_... (10 cases). Revert check: with the refusal removed, 6 tests go red; restored, green. Caveat on "default suite": no project in the default test-local.ps1 run references the Gateway at all, so the nearest suite is Gateway.UnitTests (parked, about 4 minutes, needs no lock). The tests are there, and I ran the whole suite: 8,326 passed.

### F3 - note - changing another person's Mentor setting is granted by a permission to read

Location: `src/CcDirector.Gateway/Teams/TeamEndpointRules.cs` line 127 (`TeamMethods.Any`).

The harm: `PUT /gateway/mentor-report` touching another person's is asked as "read the Mentor's page about
each person", which the Owner and a Manager have, so the gate allows a change on the strength of a read
cell. Unreachable today, because production always answers ownership as unknown; it becomes live the day
#2311 supplies ownership. Worth settling before then, in #2305 or #2311.

Developer answer: Accepted, fixed in 8ee7955ff. New action ChangeAnotherPersonsMentorSettings, which is no for every role (#2098 grants reading the page about each person, nothing more). The Mentor rule is split: a read of another person's is "read the Mentor's page about each person", and a change to another person's is the new action. So a read never grants a write. It has a per-cell test row (PUT /gateway/mentor-report, another person's), and all four roles are refused.

### F4 - note - the "is this a team" answer is one process's memory, and "not known" means "not gated"

Location: `src/CcDirector.Gateway/Teams/TeamRegistry.cs`, `IsTeam` and `LoadTeamIds`.

The harm: the set of team ids is read once and afterwards grows only through `CreateTeam` in the same
process. A team created by any other process (a second Gateway container during an overlapping deploy, if
the plan ever returns to a warmed swap) is never learned, and for that team the gate answers "not a team
request" and lets everything through - it fails open. No harm on today's single in-place container, and no
team-tenant key authenticates yet; it is an assumption to write down where #2311 will find it.

Developer answer: Accepted, fail closed, fixed in 8ee7955ff, with a cheaper shape than re-reading on every miss. A miss on every personal request would put a database read on every hosted request. Instead, only SETTLED answers are kept in memory: true for a team's id, false for an id in the tenants table (a personal account). Both are final, because ids are minted separately and never reused, and teams are not deleted in v1. Deleting a team must clear its entry, which is noted in the code. An id in neither table is not remembered, so it is read again on every request. A team created by another process is therefore recognised on its first request. Tests: TeamAccessTests.IsTeam_ATeamCreatedByAnotherProcess_IsRecognised_EvenIfItsIdWasAskedAboutBefore, and IsTeam_AnIdInNeitherTable_IsAskedAgain_SoATeamMadeUnderItLaterIsRecognised.
