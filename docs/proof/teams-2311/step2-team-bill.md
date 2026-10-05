# Proof - a team Director's paid features come from the team's bill (devthrottle_internal#2311, Gateway step 2)

Branch `teams/2311-gateway-team-bill`, 5 October 2026, cut from origin/main after #3530 merged (3fa6f7ca0). Everything ran
locally on SOREN_NORTH. No production database, no deploy, no Director of the owner's was touched. **No schema change.**

Test commands, totals and revert proofs are in [test-runs.txt](test-runs.txt) (run 25). Part 1 is in [README.md](README.md).

## What was built

| Item | Piece | What it does |
|---|---|---|
| 7 (first) | `Streaming/DirectorHub.cs` `TeamTurnPushRefusalFor`, `SessionTurnStore.DirectorsOfAnyGeneration` | Review round 2 R2-F1. In a team, the hub accepts a turn push only into a session every writer of which (any generation) is the pushing Director or another Director of the same person. A colleague's push is refused for good (the push answers no watermark). A session with nothing stored yet is accepted only from a Director whose roster holds it; otherwise the answer is an empty watermark, a soft refusal so the Director tries again at its next trigger once its roster has caught up. Personal keys are not asked. |
| 1 | `Teams/TeamMemberEntitlement.cs` (new), `Tenancy/HostedAccessLeaseService.cs` | The lease reads the TEAM's bill and the calling person's role there. A member is always allowed (team tier with a seat, free tier without). A person who is not a member is refused 403 `team_member_required` and nothing is revoked. A bill that cannot be read is Unknown: an existing lease is honoured, otherwise a temporary refusal - never a grant, never a revoke. A team lease is keyed by tenant and person, so a stranger cannot ride a member's lease. The sweep re-reads each person's lease. |
| 2 | `Wingman/NarrationPlan.cs` `DecideForTeamSession`, `GatewayHost.ResolveNarrationPlan`, `Tenancy/PreFreeTierKeyReinstatement.cs`, `DeviceRegistry.ReinstateTeamMembersRevokedBefore` | The narration plan for a team session is its Director's owner's answer in that team; nobody's session is Unknown. Reinstatement in a team gives back only members' keys, one person at a time. |
| 3 | (no change to the personal path) | A personal tenant reads exactly as before - proven with and without the team branch wired. |
| 4 | `GatewayHost` | The team entitlement is handed to the lease and to reinstatement only when Teams is released. Dark, the personal rule runs exactly as before and team keys stay revoked by `Judge`. |
| 5 | `Teams/TeamCallerOwnership.PersonOf`, `AuthMiddleware.RequireToken.TeamPerson`, `TeamEndpointGate.CallerSubject` | Seam 2: one resolver. A device key names its account subject; a session key names its Director's owner, read live, and nobody once that owner's key is revoked. The middleware and the gate both ask it. |
| 6 | `EntitlementRegistry.ReadTeamBill` (`TeamBill`) | Decision D8: one reader of a team's bill. Paid features, invite, resend, accept and the seat convergence all read it. On a hosted Gateway a row that is not live money is no bill. `ReadTeamBilledSeats` is gone. |
| 8 | `Teams/TeamsDarkRoutes.cs`, `DeviceRegistry.AccountSubjectOfActiveDevice`, `HostedTeamsDarkCockpitTests` | Review round 3. R3-F1: the dark filter catches `/team-invitations`. R3-F2: a trailing slash on the enrollment team routes. R3-F3: the REAL hosted Gateway, dark, with the Cockpit in the web root, answers 404 on `/teams` with a key and without. R3-F4: a test of OwnerOf's tenant check that fails when the check is removed. |

## #2299 and the tunnel tests

These run over the wire against a real hosted Gateway, so they are Gateway.Tests. By instruction Gateway.Tests were
**compiled but not run here**; the Tech Lead's continuous integration run is their result.

| Test | What it proves | Result |
|---|---|---|
| `HostedTeamBillOverTheWireTests.ATeamSeat_GivesPaidFeaturesOnlyOnThatTeamsDirectors_NotThePersonsOwn_AndNotAnotherTeams` | #2299: a team seat gives paid features on that team's Directors only; the person's own Director and another team's stay on their own plans | compiled; continuous integration to run |
| `...TwoDirectorsInTwoTeams_EachRegisterInTheirOwnTeam_OverTheWire_AndNeverAppearInTheOthersList` | #2311 Test 1 over the wire (moved here from part 1): each Director only in its own team's list | compiled; continuous integration to run |
| `...AMemberDemotedToCollaborator_LosesTheirKey_AndTheTeamsOtherKeysStayServed` | the demoted Collaborator | compiled; continuous integration to run |
| `...AForgedNonMemberCredentialInATeamsTenant_IsRefused_AndTheTeamsOtherKeysStayServed` | a forged non-member | compiled; continuous integration to run |
| `...ABillThatCannotBeRead_IsATemporaryRefusal_NotAGrantAndNotARevoke` | a failed read is Unknown (503), never a grant, never a revoke | compiled; continuous integration to run |
| `...AManagersTeamKey_AgainstAnotherPersonsSession_IsRefusedAsSomeoneElses_ForADeviceKeyAndASessionKey` | #2312 Test 3 over the wire, for a device key and a session key | compiled; continuous integration to run |
| `HostedTeamsDarkCockpitTests.Dark_GetTeams_WithTheCockpitInTheWebRoot_Is404_WithAKeyAndWithout` | R3-F3 on the real Gateway | compiled; continuous integration to run |
| `HostedTeamDirectorKeyTests.*`, `TeamEndpointWalkTests.OverTheWire_AKeyBoundToATeamsTenant_...` | changed meaning on purpose: a team member's key was 402 until this step; it is now served (200) | compiled; continuous integration to run |
| `TeamDirectorTunnelTests` (Gateway.UnitTests, real hub, real roster, real turn store): the five `PushTurns_*` tests, `AMemberPushingIntoAColleaguesStoredConversation_...`, the seam 2 tests, `AManagersTeamKey_..._ForADeviceKeyAndASessionKey`, `OwnerOf_ADirectorRegisteredInATeamByAKeyBoundToAnotherTeam_IsNobodys`, `EveryStoredContentRoute_...` | item 7, seam 2, #2312 Test 3 at the gate, R3-F4, the route walk | PASS |
| `TeamBillOnTheRequestPathTests` (15) | the lease, the entitlement, narration, reinstatement | PASS |
| `OneTeamBillReaderTests` (3) | D8 | PASS |
| `TeamsDarkRoutesTests` (new cases) | R3-F1, R3-F2 | PASS |

## The R2-F1 walk of the stored-content `{sid}` routes

`EveryStoredContentRoute_OfAColleaguesEndedSession_IsRefused_ToTheMemberWhoseDirectorNowHoldsItsId` is a theory over the
13 routes that read or change a session's stored content. A colleague's session ends; the member's Director now holds
its old id; each route is refused to the member. All 13 are covered by the gate's `CallersOwn` rule on `/sessions`,
which reads `TeamCallerOwnership` (every writer of the stored conversation must be the caller's). `/recording/{id}` is
not keyed by a session id and is not in the walk. Item 7 closes the write side: the member cannot push turns into the
colleague's stored conversation, so it cannot become theirs.

## Personal tenants unchanged (item 3)

- `AuthorizeAsync_APersonalTenant_ReadsExactlyAsBefore_WithOrWithoutTheTeamBranchWired`: the same decisions, both ways.
- `AuthorizeAsync_APersonsOwnPro_NeverReachesIntoATeam_AndATeamSeat_NeverReachesOut`.
- `Run_InATeam_GivesBackOnlyAMembersKeys_PersonByPerson_AndAPersonalTenantAsBefore` (reinstatement).
- `CallerSubject_ASessionKeyInAPersonalTenant_IsThatTenantsPerson_AsBefore`, `PushTurns_APersonalKey_IsNotAsked_AndWritesAsBefore`.
- The full Gateway.UnitTests and the default gate are green with no personal test changed.

## Dark unchanged (item 4) - how it was proved

By construction and by test. `GatewayHost` passes `TeamMemberEntitlement` to the lease and to reinstatement only when
`TeamsReleased`; without it the lease takes the personal path it took before (`..._WithOrWithoutTheTeamBranchWired`),
and reinstatement reads a team through the personal rule and gives nothing back
(`Run_InATeam_WithoutTheTeamBranch_...`). Dark, a team key never reaches the lease at all: `DeviceRegistry.Judge`
resolves it revoked (part 1's `TeamDirectorKeyTests.*TeamsNotReleased*`, unchanged and green). Every team route answers
404 dark (`TeamsDarkRoutesTests`, `HostedTeamsDarkTests`, and the new Cockpit test above).

## Revert checks

Each mechanism broken on purpose, the tests run on a build that succeeded, and the source restored (run 25):

- Item 7, `TeamTurnPushRefusalFor` answering "no refusal": 4 red of 22.
- Item 1, the lease's team branch never taken: 8 red of 15.
- Seam 2, `PersonOf` ignoring a session key: 6 red of 29.
- D8, `ReadTeamBill` ignoring livemode: 3 red of 50, including #3521's two test-mode cases - so paid features read the one reader.
- R3-F4, no tenant check in `AccountSubjectOfActiveDevice`: 2 red.
- R3-F1 and R3-F2, the filter as it was before round 3: 7 red.

Every one restored and green.

## Schema

None. No table, column or index added; no migration. The team bill table is the website's; the Gateway.Tests helper
`HostedTeamBill` creates it in a test database only, with the columns the seam states.

## Not run here

Gateway.Tests (by instruction, for memory): compiled, not run. The Tech Lead starts continuous integration for them.
