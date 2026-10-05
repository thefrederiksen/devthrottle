# Review: #2312 Fleet Map - the rebase and the one "whose Director" answer (thefrederiksen/devthrottle#3533)

Written by a separate review session, 4 October 2026. Head reviewed: e29b54d3f, on its merge base with main
d32b175d5. Only what changed since the last reviewed head, 4dcba6a6b (on 24cf04bfa).

## Scope

**How the delta was found:** `git fetch origin`, then `git range-diff 24cf04bfa..4dcba6a6b d32b175d5..e29b54d3f`.
Commits 2 to 9 are identical to the reviewed ones. Commit 1 differs only in its surrounding context and in the dark
test (moved onto the helper from #3532). Commits 10 (8ba28bebf) and 11 (e29b54d3f) are new.

**Read in full:** `Teams/TeamDirectorOwnership.cs`; `Teams/TeamFleetMap.cs` at this head and its diff against
4dcba6a6b; the pull request's whole diff against d32b175d5 for `GatewayHost.cs`, `Teams/TeamEndpointRules.cs`,
`Api/TeamEndpoints.cs`, `Discovery/DirectorRegistry.cs` and `HostedTeamsDarkTests.cs` (the whole test file); the rule
table and the `if (TeamsReleased)` block in `GatewayHost.cs`; the ownership tests in `TeamFleetMapTests.cs` (lines
505-590) and their seeding; `DeviceRegistry.ResolveCredential` and every writer of `RevokedAtUtc` and `Status`; the
`device_credentials` key; `seam-director-key.md`; the earlier review; the rebase section of `pr2-test-runs.txt`.

**Ran, in the review worktree, nothing edited (`git status` clean afterwards), one command at a time:**
- `dotnet test src\CcDirector.Gateway.UnitTests --filter FullyQualifiedName~Teams`: 660 passed, 0 failed, 1 skipped.
- `dotnet test src\CcDirector.Gateway.Tests --filter "FullyQualifiedName~HostedTeamsDarkTests|FullyQualifiedName~TeamFleetMap"`:
  4 passed, 0 failed - the four `HostedTeamsDarkTests`, including `SwitchUnset_TheTeamFleetMapRoute_IsNotOnTheRouteTable`.
  The lock was acquired at once.
- `git merge-tree --write-tree origin/main e29b54d3f` (in memory, nothing written to the worktree) - see F1.

**Did not run / could not reach:**
- The `TeamFleetMap` half of the second filter matched NOTHING in `CcDirector.Gateway.Tests`: the over-the-wire tests
  are `TeamEndpointWalkTests.OverTheWire_TheFleetMap_*`, which that filter does not name. So those four tests have no
  verdict from me; the Developer's proof file reports them inside a 65-passed run on code head 8ba28bebf.
- No full suite, as instructed. I did not look at continuous integration run 37244758459.
- I did not re-run the Developer's red proofs 13 and 14. I judged "can it fail" by reading.
- The Cockpit and client-core files are unchanged by the range-diff and were not re-read.
- #2311's open pull request #3530 was not read; "fit for the gate" is judged from the seam document and this code.

**What I checked and found sound (no finding):**
- The conflict resolution against d32b175d5 lost and duplicated nothing. The pull request's diff to each of the three
  conflict files is additions only, apart from the one `TeamEndpoints.Map` call it replaces: no line of main's is
  removed. Invitations (#2301), the library routes (#2304) and their rules are all still there.
- The fleet-map route is mapped once (`TeamEndpoints.cs` line 102), inside `if (TeamsReleased)` (`GatewayHost.cs`
  line 4699), and has exactly one rule (`TeamEndpointRules.cs` lines 119-120, exact, read only). No rule shadows it:
  no other rule's prefix covers `/teams/{teamId}/fleet-map`. `TeamDirectorOwnership` is built outside the switch,
  which maps no route and reads nothing until asked.
- `PersonOfDirector` refuses what it should: not registered in that tenant, no credential, a credential that is not a
  device key, a revoked row, a row bound to another tenant, a row with no person. Each has a test, and each test
  seeds a Director identical to a good one but for the one condition, so it fails if the condition is dropped.
- There is no second copy of "whose Director is this" on this head or on current main (e51e3d78f): the map's private
  `OwnersOf` is gone, and neither `OwnerOfDirector` nor `TeamCallerOwnership` exists in either tree.
- The privacy rules still hold. The map's own change is the single swap of `OwnersOf` for `PersonOfDirector`; the
  role check (F1 of the last review), the own-only cut for a Developer, and `SessionEntry` (repository and mission on
  the caller's own entries only) are byte-for-byte as reviewed. The new class logs no subject and hashes the team id.
- The dark test can fail if the route is mapped while dark: `Normalize` only fixes the leading slash, so a mapped
  `/teams/{teamId}/fleet-map` is in `MappedPatterns()` as written and trips both `DoesNotContain` checks, and the
  "more than 300 endpoints" line stops an empty table from passing. The route-table check is what carries it; the
  request with a random team id would get a not-found from a mapped handler too, so that leg alone would not.

## Verdict

One should-fix (F1: main has moved and the head no longer merges). Three notes on `PersonOfDirector` as the gate's
answer. No blocker in the code: the resolution I was asked to review is clean, the refusals are right, and I found no
path by which anyone reads more than the rulings allow.

## Findings

### F1 - should-fix - the head conflicts with current main, so what merges will not be what was reviewed

**Location:** `src/CcDirector.Gateway/Api/TeamEndpoints.cs`, against origin/main e51e3d78f (the Team page, #3529,
merged after this head's base d32b175d5).

**The harm:** `git merge-tree --write-tree origin/main e29b54d3f` reports `CONFLICT (content)` in `TeamEndpoints.cs`,
in two places: the route list in the class comment, and the block after the members route, where main now maps
`GET /teams/{teamId}/page`, `PUT /teams/{teamId}/members/{memberId}/role` and `DELETE /teams/{teamId}/members/{memberId}`
and writes its own "mapped ..." log line exactly where this pull request maps the fleet map and writes its own.
`GatewayHost.cs`, `TeamEndpointRules.cs`, `HostedTeamsDarkTests.cs` and `RoleTableSpec.cs` merge without conflict but
have not been built or run together. So #3533 cannot merge at e29b54d3f; it needs a third resolution, and that
resolution has been read by nobody. The proof file's runs are on d32b175d5 and say nothing about the merged result.

**What would settle it:** rebase onto current main, keep both blocks and one log line naming all seven routes, re-run
the two filtered commands, and have the new `TeamEndpoints.cs` diff read before merge.

Developer answer: ACCEPTED, fixed. Rebased onto origin/main e51e3d78f. The one conflict was `TeamEndpoints.cs`, in the two places named. In the class comment, main's three Team page routes come first, then the fleet map. In `Map`, main's Team page, change-role and remove-member blocks come first, then the fleet-map `MapGet`, then ONE log line naming all seven routes: `GET /teams`, `POST /teams`, `GET /teams/{teamId}/members`, `GET /teams/{teamId}/page`, `PUT /teams/{teamId}/members/{memberId}/role`, `DELETE /teams/{teamId}/members/{memberId}`, `GET /teams/{teamId}/fleet-map`. The diff against main for that file is additions only. The exception is the `Map` signature, which gains the fleet map, and its summary, which said "three routes" on main and now says "every route listed in the class comment". Nothing of main's is removed. `GatewayHost.cs`, `TeamEndpointRules.cs`, `HostedTeamsDarkTests.cs` and `RoleTableSpec.cs` merged on their own. All of it is now built and run together on the rebased head: the Teams unit filter (725 passed, 0 failed, 2 skipped, both proof rigs) and the hosted filter the Tech Lead named, which takes in `TeamEndpointWalkTests`, so the `OverTheWire_TheFleetMap_*` tests are in it (70 passed, 0 failed, 1 skipped, a real-Postgres proof).

### F2 - note - "active" here is one column; the Gateway's own credential check is three conditions

**Location:** `src/CcDirector.Gateway/Teams/TeamDirectorOwnership.cs` line 67, against
`src/CcDirector.Gateway/Pairing/DeviceRegistry.cs` lines 219-236.

**The harm:** `PersonOfDirector` calls a credential active when `RevokedAtUtc == null`. `ResolveCredential`, which
decides whether a key may call at all, calls a row revoked when `Status` is not active, OR `RevokedAtUtc` is set, OR
(hosted) the binding is invalid. I could NOT show a row on which the two disagree today: every writer I found sets or
clears `Status` and `RevokedAtUtc` together (lines 347-348, 400-401, 518, 595-596). So this is not a proven failure.
It matters because of what the ruling makes this method: once the gate rests on it, "is this key still good" has two
definitions in two files, and the seam document says a removed or demoted person's key "resolves as revoked on the
next request" - if #2311 does that as a live answer in `ResolveCredential` rather than a write to `RevokedAtUtc`,
this method goes on naming the person. The class comment does say it answers WHO and not WHETHER THEY MAY, and the
map is safe either way because it asks the role table itself. The gate must do the same, and #2311 should be told so
in words, not left to infer it.

Developer answer: ACCEPTED in part, fixed in edcf8247e; one leg declined with the reason. There is now ONE definition of an active credential row: `DeviceRegistry.IsActiveCredential(row)`, meaning the status is active AND there is no revocation time. `ResolveCredential` asks it in place of its own two conditions, and `PersonOfDirector` asks it of the whole row instead of writing `RevokedAtUtc == null` into a query. So a row marked revoked by status alone is nobody's too. Test: `PersonOfDirector_AStatusThatIsNotActive_IsNobody_EvenWithNoRevocationTime`, and `IsActiveCredential_TheStatusAndTheRevocationTimeBothDecide`. Red proof: dropping the status from the rule fails 3 tests. DECLINED: the third condition, the hosted binding check (`invalidHostedBinding`). It requires a `tenants` row whose id is the credential's tenant and whose subject is the credential's person - a PERSONAL tenant. A team's tenant has no such row today: `CreateTeam` writes `teams` and `team_members` only. So applying it here would refuse every Director on a team. How a key bound to a team resolves is #2311's to define, in `ResolveCredential`. In words for #2311, now also in the class comment: `PersonOfDirector` answers WHO, not WHETHER THEY MAY. A person refused by a live rule that writes nothing to the row (for example a removed or demoted person whose key "resolves as revoked on the next request") is still named by it. So the gate must still resolve the caller's own key through `ResolveCredential` and ask the role table, as the map does.

### F3 - note - a blank Director id throws rather than answering "nobody"

**Location:** `src/CcDirector.Gateway/Teams/TeamDirectorOwnership.cs` lines 53-54.

**The harm:** for the map this cannot happen - the ids come from the registry. For the gate, the Director id comes
from a session key's identity (seam 2). I did not read #3530 and cannot say whether that identity can carry a blank
Director id; if it can, the request ends as a fault (500) instead of the seam's "no person found -> refused". Every
other "cannot be said" case here answers null. Either is defensible; #2311 needs to know which it is getting.

Developer answer: ACCEPTED, fixed in edcf8247e. A blank or whitespace Director id now answers null and logs "no Director id, no person", like every other case where the person cannot be said. So #2311 gets "no person found", which it refuses, never a 500. Test: `PersonOfDirector_NoDirectorId_IsNobody` (empty and whitespace). Red proof: putting the throw back fails both cases.

### F4 - note - one database read and one log line per Director, on every poll of the map

**Location:** `src/CcDirector.Gateway/Teams/TeamFleetMap.cs` line 105, calling
`TeamDirectorOwnership.PersonOfDirector` (lines 65-72) inside the loop over the team's Directors.

**The harm:** the removed `OwnersOf` read every Director's credential in one query. The map now opens a database
context, runs one query and writes one log line for each Director, each time any viewer's page polls - and that
includes Directors a Developer will not be shown, because ownership is asked before the own-only cut. I measured
nothing, and for a small team it will not be felt, so this is a cost observation and not a proven failure. It is the
price of one shared answer shaped for the gate (one Director per request). If it is ever felt, the fix belongs in
`TeamDirectorOwnership` (a many-Directors overload over the same query), not in a second copy in the map.

Developer answer: DECLINED for now, with the reason. The cost is real: one indexed primary-key read and one log line per Director on each poll, including Directors a Developer is then not shown (whose a Director is must be known before the own-only cut can be made). But it is unmeasured, and a team's Directors number in single figures. A many-Directors overload adds a second public entry point that #2311 does not need, since the gate asks one Director per request. Agreed on where the fix goes if it is ever felt: a many-Directors overload in `TeamDirectorOwnership` over the same row rule, never a second copy in the map.
