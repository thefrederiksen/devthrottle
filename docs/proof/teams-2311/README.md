# Proof - a Director's key bound to a team (devthrottle_internal#2311, Gateway part 1)

Branch `teams/2311-gateway-team-key`, 4 October 2026, cut from origin/main at cdaab4f2b (after #2300 and #2302
merged; origin/main had not moved when this was written). Everything ran locally on SOREN_NORTH. No production
database, no deploy, no Director of the owner's was touched. **No schema change.**

The routes, requests, responses and every refusal are in [gateway-contract.md](gateway-contract.md). Test commands and
totals are in [test-runs.txt](test-runs.txt).

## What was built

Everything new is dark: it exists only when `CC_GATEWAY_TEAMS=1`. With the switch off, and for every person who never
joins a team, behaviour is as before - proven by the dark tests below.

| Piece | File | What it does |
|---|---|---|
| Teams list | `Api/HostedEnrollmentEndpoint.cs` `ListTeams` | `GET /devices/enroll-hosted/teams`: the teams where `TeamAccess.Decide(..., RunSessionsOnOwnComputers)` allows. |
| Enrollment into a team | `Api/HostedEnrollmentEndpoint.cs` `EnrollIntoTeam` | `teamId` on `POST /devices/enroll-hosted` binds the key to the team's tenant for the person. Personal path unchanged; the personal paid gate moved into `PersonalAccountGate` without a word changed, so enrollment and a move home share it. |
| The key stays alive only while... | `Pairing/DeviceRegistry.cs` `IsLiveTeamBinding` | `ResolveCredential` and the start-up quarantine accept a team key while a `team_members` row has that team, that person, and a role `TeamPermissions.Allows(role, RunSessionsOnOwnComputers)`. |
| Removing a person | `Teams/TeamMemberAccessRevoker.cs`, `TeamRegistry.MembershipCommitted` | At `CommitMembershipChange`: removed, or made a Collaborator, means that person's keys in that team are revoked and their open tunnels on that team cut. |
| One person's tunnels | `Streaming/DirectorConnectionRegistry.cs`, `DirectorHub.Hello` | Each live tunnel is indexed by the key's person and the Director id, so `AbortForTenantMember` and `AbortForDirector` cut only those. |
| The gate knows who is calling | `Teams/TeamEndpointGate.cs`, `Teams/TeamCallerOwnership.cs` | In a team's tenant the caller is the device key's person; a Director is its Hello key's person's, a session its Director's. Unknown is still refused. |
| Moving a Director | `Api/HostedEnrollmentEndpoint.cs` `Move` | `POST /devices/enroll-hosted/move`: refused with any session registered; otherwise the old key is revoked, its tunnel cut, and a new key issued for the new team. |
| Comment corrected | `Discovery/DirectorRegistry.cs` | "Several Directors on one machine share one device key" was no longer true. Each Director enrolls its own key now; the one-credential-several-ids tolerance stays because the machine's shared Gateway token registers as one credential (`machine-token`) for every Director using it, and Directors enrolled before per-instance keys may still share one. |

## #2311's tests, and where each stands

| # | Test from the issue / brief | Result | Where |
|---|---|---|---|
| 1 | Two Directors (two ids, one person) on two teams each register in their own team and never appear in the other's Director list or Fleet data | PASS at the registry and at the real `DirectorHub` (Hello with real team keys, sessions pushed). The over-the-wire tunnel assertion **moves to the next step**: the access lease answers 402 for a team tenant until it reads the team's bill. | `TeamDirectorTunnelTests.TwoDirectorsOfOnePerson_OnTwoTeams_...`, `TeamDirectorKeyTests.TwoDirectorsOfOnePerson_...`, `HostedTeamEnrollmentTests.Enroll_OnePersonsTwoDirectors_...` |
| 2 | A session registered by a Director belongs to that Director's team tenant | PASS at the real hub | `TeamDirectorTunnelTests.ASessionRegisteredByADirector_BelongsToThatDirectorsTeamTenant` |
| 3 | Enrolling into a team where the person is only a Collaborator, or not a member, is refused | PASS (function and real web host) | `HostedTeamEnrollmentTests.Enroll_IntoATeam_AsACollaborator_...`, `..._NotAMember_...`, `Map_TeamsReleased_...` |
| 4 | Moving with a session registered is refused; with none it works, the Director is in the new team, the old key resolves revoked | PASS (function and real web host) | `HostedTeamEnrollmentTests.Move_*` |
| 5 | Removing a person (and demoting to Collaborator) revokes their team keys and cuts their open tunnels on that team only | PASS - keys over real HTTP through a real hosted Gateway; tunnels at the real hub and the connection registry | `HostedTeamDirectorKeyTests.*` (wire), `TeamMemberAccessRevokerTests.*`, `TeamDirectorTunnelTests.RemovingAPerson_...`, `..._Demoting...` |
| 6 | The teams list never offers a Collaborator team; a person with no team gets an empty list | PASS | `HostedTeamEnrollmentTests.ListTeams_*` |
| 7 | Switch off: the new routes are absent, `teamId` is refused, personal enrollment is unchanged | PASS (real hosted Gateway and real web host) | `HostedTeamDirectorKeyDarkTests`, `HostedTeamEnrollmentTests.Map_TeamsDark_...`, `Enroll_NoTeam_...`, `TeamDirectorKeyTests.*TeamsNotReleased*` |
| 8 | Start-up quarantine keeps a valid team key and still quarantines a bad one | PASS | `TeamDirectorKeyTests.Initialize_*` |
| - | A person with one team is never asked to choose | Director side - the list above is what it decides from | (desktop work) |
| - | The team gate knows who is calling | PASS | `TeamCallerOwnershipTests.*` |

One existing test changed meaning on purpose: `TeamEndpointWalkTests.OverTheWire_AKeyBoundToATeamsTenant_...` pinned
"a team key never authenticates today". It now pins the new truth: a Developer's team key authenticates and meets the
lease (402); a Collaborator's does not authenticate (401). `TeamEndpointGateTests.RunAsync_AKeyBoundToATeamsTenant...`
was renamed for the same reason; its assertion is unchanged.

## Revert checks

Each mechanism was broken on purpose, the tests run, and the source restored (commands in test-runs.txt):

- `IsLiveTeamBinding` made to answer no: 8 of 15 `TeamDirectorKeyTests` red.
- `TeamCallerOwnership` made to answer "the caller's own" for everything: 3 of 11 `TeamCallerOwnershipTests` red.
- `TeamMemberAccessRevoker` made to cut no tunnel: 4 of 16 tunnel and revoker tests red.

## What still answers "not entitled" for a team tenant (the next step)

`HostedAccessLeaseService` reads a personal account's bill through `SubjectForTenant`. A team's tenant has no such
subject, so every request and Hello made with a team key is refused **402 `hosted_subscription_required`** by the auth
middleware, and nothing is revoked by that. The narration plan and `PreFreeTierKeyReinstatement` likewise see no bill
for a team tenant. Reading the team's bill there (`EvaluateTeamTenant`, after #3521 merges) is the next step, with the
end-to-end "paid features only on that team's Directors" proof. The move and the teams list are not behind the lease
(they take the account token), so they work today.

## Schema

None. No table, column or index added; no migration.

## Not run here

The full `-Parked` run (Gateway.Tests in full, Core.Tests) - too long for one command in this session; the Tech Lead
runs it. The default gate, the whole of Gateway.UnitTests and the teams/enrollment/tenancy slice of Gateway.Tests ran
and are recorded in test-runs.txt.
