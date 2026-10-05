# Review: #2312 Fleet Map - second delta: the third conflict resolution and the shared credential rule (thefrederiksen/devthrottle#3533)

Written by a separate review session, 4 October 2026. Head reviewed: 4c646fc10, whose merge base with main is
e51e3d78f (confirmed with `git merge-base`). Only the two things the mandate names.

## Scope

**Read:**
- `git diff e51e3d78f 4c646fc10 -- src/CcDirector.Gateway/Api/TeamEndpoints.cs`, the whole diff, and the mapped
  routes in the file at the head (lines 64-153).
- `git show edcf8247e`, the whole commit. `git diff --stat edcf8247e 4c646fc10 -- src` is empty, so the source at the
  head is exactly that commit's.
- `DeviceRegistry.ResolveCredential` at the head (lines 195-255) beside the same method at e51e3d78f, line for line.
- `TeamDirectorOwnership.PersonOfDirector` at the head (lines 57-85).
- Every use of `IsActiveCredential` and of `DeviceRegistry.StatusActive` outside the tests.
- The `device_credentials` key in `GatewayDbContext.cs` (line 1471 onward), `TeamRegistry.CreateTeam` and every read
  or write of `ctx.Tenants` under `Teams/`, and the `TeamEndpoints.Map` call in `GatewayHost.cs` (lines 4711-4714).

**Ran:** nothing built, nothing tested, as instructed. Read-only git commands only; `git status` in the review
worktree is clean. I did not look at continuous integration run 37258216265, so I have no verdict on whether the
head builds or its tests pass.

**Did not read:** anything else in the pull request (the Cockpit, client-core, `TeamFleetMap.cs`, the rule table
beyond confirming the fleet-map rule is still present, the proof files), and #2311's pull request #3530.

**What I checked and found sound (no finding):**

*The conflict resolution in `TeamEndpoints.cs`.*
- The diff is 36 lines added and 3 removed. The 3 removed lines are each replaced by a line that carries the old
  content plus the fleet map: the `Map` summary ("the three routes" becomes "every route listed in the class
  comment"), the `Map` signature (gains `TeamFleetMap fleetMap`), and the "mapped ..." log line (main's six routes,
  unchanged and in order, plus `GET /teams/{teamId}/fleet-map`).
- Main's three Team page blocks - `GET /teams/{teamId}/page` (line 107), `PUT .../members/{memberId}/role` (line 113)
  and `DELETE .../members/{memberId}` (line 141) - are byte-for-byte the same as at e51e3d78f (compared with `diff`
  over the span from the page route to the end of the remove-member block). Each is mapped once. Nothing of main's is
  lost, changed or duplicated.
- The fleet-map route is mapped once (line 147), after main's blocks, and there is one "mapped" log line (line 153)
  naming all seven routes. The one caller of `Map` (`GatewayHost.cs` line 4713, inside `if (TeamsReleased)`) passes
  the new argument.

*`ResolveCredential` is unchanged in behaviour.* The only edit to the method is the final condition. Before:
`invalidHostedBinding || !Equals(Status, "active", Ordinal) || RevokedAtUtc is not null`. Now:
`invalidHostedBinding || !IsActiveCredential(row)`, where `IsActiveCredential` is
`Equals(Status, "active", Ordinal) && RevokedAtUtc is null`. The second is the first with the last two terms
negated together - the same truth table, the same ordinal comparison, the same constant. By row:

| Row | Before | Now |
|---|---|---|
| status active, no revocation time, binding valid | Active | Active |
| status not active (including empty, null, or different case), no revocation time | Revoked | Revoked |
| status active, revocation time set | Revoked | Revoked |
| status not active, revocation time set | Revoked | Revoked |
| any of the above with an invalid hosted binding | Revoked | Revoked |

- The hosted-binding expression is untouched, is still evaluated before the row rule, and still short-circuits it,
  so the database read inside it happens on exactly the same rows as before. It does not look at whether the tenant
  is personal or a team; neither did it before. Nothing in the method distinguishes the two, so the answer is the
  same for both.
- Everything before the condition (unknown key, duplicate hash, malformed hash, the identity that is returned) is
  untouched. The identity is still built before the condition and carried on both answers.
- The new `ArgumentNullException.ThrowIfNull(row)` cannot fire here: `row` is `matches[0]` of a list already checked
  to hold exactly one materialised entity.
- No other caller's behaviour moved: `IsActiveCredential` has exactly two callers, this one and `PersonOfDirector`.

*F2's declined leg is declined for a true reason.* The binding check requires a `tenants` row whose id is the
credential's tenant and whose subject is the credential's person. `CreateTeam` adds a `teams` row and a
`team_members` row and nothing else; nothing under `Teams/` writes `tenants`; and `TeamRegistry.cs` line 179 refuses
a team id that IS a `tenants` id. So no team tenant has such a row, and applying the leg in `PersonOfDirector` would
answer "nobody" for every Director on every team.

*F3 is fixed.* A blank or whitespace Director id returns null before anything is read (lines 59-63), with a log
line that carries the hashed team id and no subject.

*`PersonOfDirector` refuses at least what it refused before.* The old query required device id, this team's tenant,
no revocation time and a non-null subject. The new code reads the one row by device id (the table's primary key, so
at most one) and this team's tenant, then requires `IsActiveCredential` and a non-blank subject. Every row refused
before is refused now, and a row whose status is not active is now refused as well. No row is newly named.

## Verdict

No blocker and no should-fix. The resolution loses nothing of main's, and I found no row on which
`ResolveCredential` answers differently than it did. One note for #2311, about something this change did not cause.

## Findings

### F1 - note - on the hosted Gateway, a device key bound to a team's tenant resolves as Revoked today; this change neither causes nor cures it

**Location:** `src/CcDirector.Gateway/Pairing/DeviceRegistry.cs` lines 232-238 (the hosted-binding check, identical
at e51e3d78f), read with `src/CcDirector.Gateway/Teams/TeamRegistry.cs` line 179 and `CreateTeam`.

**The harm:** not a harm of this pull request - the lines are main's and are unchanged. It is the other face of the
reason F2's third leg was declined. Because a team's tenant has no `tenants` row, the binding check is true for any
credential row whose tenant is a team, so on the hosted Gateway `ResolveCredential` answers Revoked for that key
however good the row is. I read this from the code and did not run it. The consequence for this pull request is only
this: until #2311 teaches `ResolveCredential` how a key bound to a team resolves, a Director cannot authenticate on a
team's tenant on hosted, so `PersonOfDirector` has no real Director to name there and the map's ownership path is
exercised by seeded tests alone. The Developer's answer already assigns the fix to #2311; I record it so the
dependency is written down in the review as well as in the answer, and so that whoever changes the binding check
for teams knows `IsActiveCredential` is deliberately about the row only and must stay that way.

Developer answer: AGREED, no change in this pull request. The reading is right: a team's tenant has no `tenants` row, so on the hosted Gateway the hosted-binding check in `ResolveCredential` refuses any key bound to a team. Until #2311 defines how a team-bound key resolves, no real Director can say Hello on a team's tenant. Until then, the Fleet Map's ownership path is proven only by seeded tests (`TeamFleetMapTests`, `TeamEndpointWalkTests`), as the pull request body says ("tests seed credentials directly"). For #2311, in one place: (1) change the binding check for team tenants inside `ResolveCredential`, beside the personal-tenant check, not in `IsActiveCredential`. (2) Keep `IsActiveCredential` about the ROW only (status active and no revocation time). Its doc comment says so, and `IsActiveCredential_TheStatusAndTheRevocationTimeBothDecide` pins its truth table. (3) Call `GatewayHost.TeamDirectorOwnership.PersonOfDirector(tenant, directorId)` for whose a Director is, and still ask the role table for whether that person may act.
