# Proof - a Director's key bound to a team (devthrottle_internal#2311, Gateway part 1)

Branch `teams/2311-gateway-team-key`, 4 October 2026, cut from origin/main at cdaab4f2b (after #2300 and #2302
merged), and rebased for the Gateway review onto origin/main 24cf04bfa (after #3521 and #3527). Everything ran locally on SOREN_NORTH. No production
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
| Moving a Director | `Api/HostedEnrollmentEndpoint.cs` `Move` | `POST /devices/enroll-hosted/move` `{ deviceId, teamId }` with the account token: refused with any session registered; otherwise the old key is revoked, its tunnel cut, and a new key issued for the new team. |
| Setting up again elsewhere is a move | `Api/HostedEnrollmentEndpoint.cs` `LeaveOtherPlaces` | Gateway review F1: the one step `Enroll` and `Move` share. Into another tenant than the person's current key for that Director: refused with the move's 409 while it has sessions there, else the other keys revoked and the old tunnel cut. Same tenant: unchanged. |
| One key per Director | `DeviceRegistry.RevokeOtherKeysOfDirector`, `ActiveKeysOfDirector` | Where Teams is released, setting a Director up again revokes that person's other keys for the same Director id, so a move by id names exactly one key. |
| A team key's Hello | `DeviceRegistry.Judge` (`IsTeamKey`), `DirectorHub.Hello` | Gateway review F2: a team key is accepted at Hello only under the Director id its row was enrolled with. Personal keys unchanged. |
| A shared session id is nobody's | `PushedSessionStore.DirectorsHoldingSession`, `TeamCallerOwnership` | Gateway review F2: a session is the caller's own only when exactly one Director in the tenant holds it and it is the caller's. The roster does not refuse the duplicate. |
| A stored conversation is its writers' | `SessionTurnStore.DirectorsOfCurrentConversation`, `TeamCallerOwnership` | Tech Lead ruling on the review: in a team, a `{sid}` route is the caller's own only when every Director that wrote the session's stored conversation (head and current-generation rows) is the caller's. Closes the ended-session gap. |
| A dark start leaves team keys alone | `DeviceRegistry.InitializeAuthority` | Gateway review F4: with Teams off, a key bound to a team is not tombstoned; it resolves revoked while dark and works again when Teams is on. |
| The "Teams released" signal | `Contracts/HealthDto.Teams`, `GatewayEndpoints` `/healthz` | `teams: true` only on hosted with the switch on; `false` on a dark hosted Gateway; absent before Teams and on self-host. The Director reads it before asking for teams (review round 1, F1). |
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
| 8 | Start-up quarantine keeps a valid team key and still quarantines a bad one; a start with Teams off leaves team keys untouched and switching Teams on restores them (review F4) | PASS | `TeamDirectorKeyTests.Initialize_*` |
| - | A person with one team is never asked to choose | Director side - the list above is what it decides from | (desktop work) |
| - | The team gate knows who is calling | PASS | `TeamCallerOwnershipTests.*` |
| - | Review F1: both team routes pass the auth middleware without a device key; one explicit Teams signal | PASS | `AuthMiddlewareTests.The_team_enrollment_routes_are_public_...`, `A_path_under_the_hosted_enroll_route_...`, `HostedTeamDirectorKeyTests.Healthz_SaysTeamsIsOffered`, `HostedTeamDirectorKeyDarkTests.Dark_HealthzSaysTeamsIsNotOffered_Explicitly` |
| - | One Director, one key | PASS | `HostedTeamEnrollmentTests.Enroll_TheSameDirectorSomewhereElse_...`, `Enroll_Personal_WhereTeamsIsNotReleased_RevokesNothing`, `Move_ADirectorWithTwoWorkingKeys...`, `Move_TheSameDirectorTwice_...` |
| - | Gateway review F1: setting a Director up in another tenant with a session registered is refused 409; with none the old tunnel is cut; the same tenant is unchanged - with a session registered and a tunnel open | PASS at the real hub and real roster | `TeamDirectorTunnelTests.SettingADirectorUp*` (4 tests) |
| - | Gateway review F2: a Hello under another member's Director id is refused; a personal key is unchanged | PASS at the real hub | `TeamDirectorTunnelTests.ATeamKey_SayingHello*`, `APersonalKey_SayingHelloUnderAnIdItWasNotEnrolledFor_IsAcceptedAsBefore` |
| - | Gateway review F2: two members' Directors claiming one session id make it nobody's own | PASS at the real hub and real roster | `TeamDirectorTunnelTests.TwoMembersDirectors_ClaimingOneSessionId_...` |
| - | Gateway review F3: a move refused 409 by a real roster snapshot, then moved with the tunnel cut | PASS, over the Gateway's own wiring (`TeamEnrollment.Over`) | `TeamDirectorTunnelTests.Move_WithASessionInTheRealRoster_...` |
| - | Ruling on the ended-session gap: M2's session ends, M1's Director is the only holder of its old id, and M1 is refused; appending to M2's conversation does not make it M1's | PASS at the real hub, real roster, real turn store, over a database that reads the boundary's own tenant scope | `TeamDirectorTunnelTests.AColleaguesEndedSession_...`, `AMemberPushingIntoAColleaguesStoredConversation_...`, `AMembersOwnSession_WithItsOwnStoredConversation_...` |

One existing test changed meaning on purpose: `TeamEndpointWalkTests.OverTheWire_AKeyBoundToATeamsTenant_...` pinned
"a team key never authenticates today". It now pins the new truth: a Developer's team key authenticates and meets the
lease (402); a Collaborator's does not authenticate (401). `TeamEndpointGateTests.RunAsync_AKeyBoundToATeamsTenant...`
was renamed for the same reason; its assertion is unchanged.

## Revert checks

Each mechanism was broken on purpose, the tests run, and the source restored (commands in test-runs.txt):

- `IsLiveTeamBinding` made to answer no: 8 of 15 `TeamDirectorKeyTests` red.
- `TeamCallerOwnership` made to answer "the caller's own" for everything: 3 of 11 `TeamCallerOwnershipTests` red.
- `TeamMemberAccessRevoker` made to cut no tunnel: 4 of 16 tunnel and revoker tests red.

Gateway review round (run 11 in test-runs.txt; each mutation restored in a `finally`, then a fresh build, 450 of 450
green, no diff):

- The team-key Hello check off: 1 red (`ATeamKey_SayingHelloUnderAnotherMembersDirectorId_...`).
- Two Directors holding one session id let through: 1 red (`TwoMembersDirectors_ClaimingOneSessionId_...`).
- The session check in the shared leave step off: 4 red (two set-up-again, two move).
- The old tunnel cut in the shared leave step off: 3 red.
- The real roster count made to return nought in `TeamEnrollment.Over`: 3 red.
- A dark start tombstoning team keys again: 1 red (`Initialize_TeamsNotReleased_LeavesATeamKeyUntouched_...`).

The ended-session ruling (run 16): the stored-conversation check skipped, 2 red; the check reading the head only, 1 red
(`AMemberPushingIntoAColleaguesStoredConversation_...`); the stored read made outside the team's tenant scope, 4 red.
Restored: 453 of 453 green, no diff.

## What still answers "not entitled" for a team tenant (the next step)

`HostedAccessLeaseService` reads a personal account's bill through `SubjectForTenant`. A team's tenant has no such
subject, so every request and Hello made with a team key is refused **402 `hosted_subscription_required`** by the auth
middleware, and nothing is revoked by that. The narration plan and `PreFreeTierKeyReinstatement` likewise see no bill
for a team tenant. Reading the team's bill there (`EvaluateTeamTenant`, merged in #3521) is the next step, with the
end-to-end "paid features only on that team's Directors" proof. The move and the teams list are not behind the lease
(they take the account token), so they work today.

## Schema

None. No table, column or index added; no migration.

## Not run here

The full `-Parked` run (Gateway.Tests in full, Core.Tests) - too long for one command in this session; the Tech Lead
runs it. The default gate, the whole of Gateway.UnitTests and the teams/enrollment/tenancy slice of Gateway.Tests ran
and are recorded in test-runs.txt.
