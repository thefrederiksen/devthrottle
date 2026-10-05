# Proof - a team Director's paid features come from the team's bill (devthrottle_internal#2311, Gateway step 2)

Branch `teams/2311-gateway-team-bill`, 5 October 2026, cut from origin/main after #3530 merged (3fa6f7ca0). Everything ran
locally on SOREN_NORTH. No production database, no deploy, no Director of the owner's was touched. **No schema change.**

Test commands, totals and revert proofs are in [test-runs.txt](test-runs.txt) (run 25). Part 1 is in [README.md](README.md).

## What was built

| Item | Piece | What it does |
|---|---|---|
| 7 (first) | `Streaming/DirectorHub.cs` `TeamTurnPushRefusalFor`, `SessionTurnStore.DirectorsOfAnyGeneration` | Review round 2 R2-F1. In a team, the hub accepts a turn push only into a session every writer of which (any generation) is the pushing Director or another Director of the same person. A colleague's push is refused for good (the push answers no watermark). A session with nothing stored yet is accepted only from the ONE Director in the tenant whose roster holds it (review S2-F2); otherwise - not in its roster, or another Director lists the id too - the answer is an empty watermark, a soft refusal so the Director tries again at its next trigger. Personal keys are not asked. |
| 1 | `Teams/TeamMemberEntitlement.cs` (new), `Tenancy/HostedAccessLeaseService.cs` | The lease reads the TEAM's bill and the calling person's role there. A member is always allowed (team tier with a seat, free tier without). A person who is not a member is refused 403 `team_member_required` and nothing is revoked. A bill that cannot be read is Unknown: an existing lease is honoured, otherwise a temporary refusal - never a grant, never a revoke. A team lease is keyed by tenant and person, so a stranger cannot ride a member's lease. The sweep re-reads each person's lease. |
| 2 | `Wingman/NarrationPlan.cs` `DecideForTeamSession`, `GatewayHost.ResolveNarrationPlan`, `Tenancy/PreFreeTierKeyReinstatement.cs`, `DeviceRegistry.ReinstateTeamMembersRevokedBefore` | The narration plan for a team session is its Director's owner's answer in that team; nobody's session is Unknown. Reinstatement in a team gives back only members' keys, one person at a time. |
| 3 | (no change to the personal path) | A personal tenant reads exactly as before - proven with and without the team branch wired. |
| 4 | `GatewayHost` | The team entitlement is handed to the lease and to reinstatement only when Teams is released. Dark, the personal rule runs exactly as before and team keys stay revoked by `Judge`. |
| 5 | `Teams/TeamCallerOwnership.PersonOf`, `AuthMiddleware.RequireToken.TeamPerson`, `TeamEndpointGate.CallerSubject` | Seam 2: one resolver. A device key names its account subject; a session key names its Director's owner, read live, and nobody once that owner's key is revoked. The middleware and the gate both ask it. |
| 6 | `EntitlementRegistry.ReadTeamBill` (`TeamBill`) | Decision D8: one reader of a team's bill. Paid features, invite, resend, accept and the seat convergence all read it. On a hosted Gateway a row that is not live money is no bill. `ReadTeamBilledSeats` is gone. |
| S2-F1 | `DeviceRegistry.AnotherPersonHasHeldDirectorInTenant`, `HostedEnrollmentEndpoint.DirectorIdTakenRefusal` | Review round 1 of #3552. A Director id in a team belongs to ONE person for good, kept in the device table so it survives a restart. Setting a Director up for a team, and moving one into a team, are refused 409 `director_id_taken_in_team` when another person has any key row for that id in the team, active or revoked. The same person setting the same id up again is unchanged. No schema change. |
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
not keyed by a session id and is not in the walk.

What actually holds this up (corrected after #3552 review S2-F1). Stored turns, and everything the walk's routes read,
name their writer by DIRECTOR ID; the read rule and item 7's write rule both ask `OwnerOf` who holds each writer's id
now. That is the right person only because **a Director id in a team is one person's for good**, and that rule lives in
the device table: enrollment and a move into a team refuse an id another person has ever held there
(`DirectorIdTakenRefusal`). The in-memory registry binding alone did not hold it - it is empty after every restart, and
before this fix a member enrolled under a colleague's id could say Hello first and inherit their stored sessions. With
the id fixed to one person, item 7 then refuses a colleague's push into a stored conversation, and the first rows of a
session with nothing stored are taken only from the one Director in the tenant whose roster holds it (S2-F2), so a
colleague cannot write first and make the session theirs.

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

## #3552 review round 1

The reviewer's two reproduction tests passed on bc0bbedea (both harms happened). Pasted into `TeamDirectorTunnelTests`
turned round, they are the first tests of the two fixes:

| Finding | Test | On bc0bbedea | On the new head |
|---|---|---|---|
| S2-F1 | `AKeyUnderAColleaguesDirectorId_IsRefusedAtEnrollment_SoAfterARestartTheColleagueStillOwnsTheirStoredSessions` | fails (the enrollment is 200; reproduced by turning the refusal off) | PASS |
| S2-F1 | `HostedTeamEnrollmentTests.EnrollOrMove_IntoATeam_UnderADirectorIdAnotherMemberHolds_IsRefused409_ActiveOrRevoked_AndNothingIsWritten` | fails | PASS |
| S2-F1 | `HostedTeamEnrollmentTests.Enroll_TheSameDirectorSomewhereElse_...` and `Enroll_TwoMembersPresentingTheSameDeviceId_TheSecondIsRefused_...` - changed on purpose: both pinned two people on one Director id in one team, which was the defect | fail | PASS |
| S2-F2 | `AColleagueListingALiveSessionId_CannotPushItsFirstRows_AndOnceTheyStop_TheOwnersPushIsStoredAndTheSessionIsTheirs` | fails (reproduced by restoring the old roster rule) | PASS |
| S2-F4 | `HostedTeamBillOverTheWireTests.ASessionKey_WhoseDirectorsOwnerIsNoLongerAMember_Is403TeamMemberRequired_FromTheLease_NotTheRegistrys401` (Gateway.Tests) | - | PASS (that one test run here, with leave; red with the mapping disabled) |

"Fails on bc0bbedea" was shown by putting the old behaviour back on the new head, not by checking out the old head:
the refusal answering "not taken" (4 red), and the nothing-stored rule back to "this Director's roster holds it" (1 red).

S2-F3: the rebase onto 8b23aaba4 (#3537). `TeamBill` carries `Fingerprint`, computed inside `ReadTeamBill` for every row
it counts as a bill, so #3537's stop holds. On a hosted Gateway a test-mode row is no bill: convergence answers `NoBill`
for it and clears any stop mark for that team. That is consistent with D8 (a test-mode row is not a bill there) and is
pinned by `ReadTeamBill_OnAHostedGateway_ALiveRowCarriesItsFingerprint_AndATestModeRowIsNoBillWithNone`. The D8 revert
check, redone after the rebase (`ReadTeamBill` ignoring livemode): 4 red of 68.

S2-F4: the line to remove is the `if (access == ...HostedAccessDecision.DenyNotAMember)` block in
`AuthMiddleware` (the 403 `team_member_required` answer); with it gone the request falls through as allowed and the new
request falls through to the team gate. Run with the Tech Lead's leave, that one test alone, filtered: with the block
disabled it is RED (403 from the team gate with code `team_action_refused`, not `team_member_required`); restored, it
is GREEN. The code assertion is what tells the lease's refusal from the gate's.

## #3552 review round 2

Rulings: `rulings-2311-step2-review2.md`. Fix commit bc1828f1c.

| Finding | Test | On 533a76cf1 | On the new head |
|---|---|---|---|
| S2-F5 part 1 | `TeamDirectorTunnelTests.ADeviceIdWithABarBeforeAColleaguesDirectorId_IsRefusedAtEnrollmentAndMove_AndTheColleagueKeepsTheirId` (the reviewer's first round-2 reproduction, turned round) | fails (`x|director-bob` is enrolled 200) | PASS |
| S2-F5 part 2 | `TeamDirectorTunnelTests.AColleaguesDirectorIdInAnotherLetterCase_IsRefused_ByTheComparisonHelloUses_MadeInMemory_NotByTheDatabase` | fails (the non-ASCII case variant is enrolled 200 by the old query) | PASS |
| S2-F5 part 3 | `TeamDirectorTunnelTests.Hello_UnderAnIdAnotherPersonHoldsAnActiveKeyFor_IsRefused_EvenForARowWrittenStraightIntoTheDeviceTable` | fails (the stray row's Hello is accepted) | PASS |
| S2-F6 | `TeamDirectorTunnelTests.AColleagueWhoListsALiveSessionIdUntilItEnds_IsNotItsOwner_BecauseTheSessionsKeyNamesTheOwnersDirector` (the reviewer's second round-2 reproduction, turned round) | fails (Bob's pushes refused for now, Alice answered owner) | PASS |
| S2-F6 part 1 | `TeamDirectorTunnelTests.ASessionWithNothingStored_WhoseKeyNamesAnotherPersonsDirector_IsNotTheOnlyHoldersOwn` | fails | PASS |
| S2-F2 | `AColleagueListingALiveSessionId_CannotPushItsFirstRows_...`, `PushTurns_ANewSessionWithNothingStored_...` - no key registered, so they now test the no-key-row path | - | PASS |
| S2-F7 | `HostedTeamBillOverTheWireTests.ASessionKey_WhoseDirectorsOwnerIsNoLongerAMember_...` (Gateway.Tests, the one test, filtered) | - | red with the block removed, green restored - output below |

"Fails on 533a76cf1" was shown by putting each part's old behaviour back on the new head, one at a time, on a build that
succeeded, with the WHOLE `CcDirector.Gateway.Tests.Teams` namespace run (796 tests, 2 skipped), restored by a trap on
exit and checked for an empty diff afterwards:

- S2-F5 part 1, `DeviceIdRefusal` never refuses: Failed 1 of 796, the bar reproduction.
- S2-F5 part 2, the old database query back (`DeviceId.EndsWith("|" + id)`, sent as `LIKE '%|<id>'`): Failed 1 of 796,
  the letter-case test. Its ASCII half (`DIRECTOR-BOB`) passes against the old query on SQLite, as the reviewer said; the
  non-ASCII half is the one that tells an in-memory comparison from a database one on the test database.
- S2-F5 part 3, Hello's other-person check never true: Failed 1 of 796, the stray-row test.
- S2-F6 part 1, the ownership answer not reading the session key row: Failed 1 of 796, the nothing-stored test.
- S2-F6 part 2, the hub not reading the session key row: Failed 1 of 796, the second reproduction.

A first try at the part 1 revert did not compile (a nullable warning is an error here); the script then ran the tests on
the unmutated binary and reported green. That run proves nothing and is not counted. The script now stops when the build
does not succeed, and the part 1 revert was redone on a build that succeeded.

What holds the stored-content rule up, after round 2: a Director id in a team is one person's, asked one way
(`DeviceCredentialIdentity.SameDirectorId`) at enrollment, at a move and at Hello, with the device table as the record;
then item 7 and S2-F2 for the stored rows; then the session key row for a session with nothing stored. The one gap left
is named in the S2-F6 answer: a session whose key registration was lost at launch has no key row until the next reseed,
and the reseed sends the roster before the keys.

S2-F7, run with the Tech Lead's leave, the one Gateway.Tests test, filtered, on the new head. The whole `DenyNotAMember`
block was REMOVED from `AuthMiddleware` (10 lines, shown), not disabled:

```
REMOVED from AuthMiddleware.cs:
                    if (access == CcDirector.Gateway.Tenancy.HostedAccessDecision.DenyNotAMember)
                    {
                        // In a team's tenant, the request names nobody who is a member (devthrottle_internal#2311). A
                        // refusal of the person, not of the team's bill: 403, and nothing was revoked.
                        ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                        ctx.Response.ContentType = "application/json; charset=utf-8";
                        await ctx.Response.WriteAsync(
                            "{\"error\":\"" + JsonEscape(TeamMemberRefusal) + "\",\"code\":\"team_member_required\"}");
                        return;
                    }

 src/CcDirector.Gateway/Util/AuthMiddleware.cs | 10 ----------
 1 file changed, 10 deletions(-)
Build succeeded.
[block removed]   Failed CcDirector.Gateway.Tests.Teams.HostedTeamBillOverTheWireTests.ASessionKey_WhoseDirectorsOwnerIsNoLongerAMember_Is403TeamMemberRequired_FromTheLease_NotTheRegistrys401 [3 s]
[block removed]    Assert.Equal() Failure: Strings differ
[block removed] Expected: "team_member_required"
[block removed] Actual:   "team_action_refused"
[block removed] Failed!  - Failed:     1, Passed:     0, Skipped:     0, Total:     1, Duration: 3 s - CcDirector.Gateway.Tests.dll (net10.0)
[restore] AuthMiddleware.cs restored, diff against HEAD empty
Build succeeded.
[restored] Passed!  - Failed:     0, Passed:     1, Skipped:     0, Total:     1, Duration: 4 s - CcDirector.Gateway.Tests.dll (net10.0)
```

## #3552 review round 3

Rulings: `rulings-2311-step2-review3.md`. Fix commit 82b85d59c, on origin/main b7e12f84b (#3539).

| Finding | Test | On 73fcc561e | On the new head |
|---|---|---|---|
| S2-F8 | `TeamDirectorTunnelTests.AColleaguesDirector_RegisteringASessionKeyForAnIdAnotherDirectorLists_IsRefused_AndTheOwnersKeyMakesItTheirs` (the reviewer's first round-3 reproduction, turned round) | fails (Alice's key row is written) | PASS |
| S2-F8 | `ASessionKey_ForAnIdWithRowsAnotherPersonsDirectorWrote_IsRefused_EvenWhenNobodyListsIt` | fails | PASS |
| S2-F8 | `ASessionKey_RegisteredBeforeTheRoster_AfterTheRoster_OrAgainAfterAReconnect_IsAccepted` - the honest orders | PASS | PASS |
| S2-F9 | `TheDirectorIdTakenRefusal_TellsThePersonWhatToDo` | fails (old text) | PASS |
| S2-F10 | `SettingOneDirectorUpAgain_UnderItsIdInAnotherLetterCase_RevokesThePersonsFirstKey` (ASCII and non-ASCII) | the non-ASCII case fails | PASS |

"Fails on 73fcc561e" was shown by putting each old behaviour back on the new head, one at a time, on a build that
succeeded, with the whole `CcDirector.Gateway.Tests.Teams` namespace run (999 tests, 3 skipped), restored by a trap and
checked for an empty diff:

- S2-F8, the roster half never refuses: Failed 1 of 999, the reproduction.
- S2-F8, the stored-rows half never refuses: Failed 1 of 999, the stored-rows test.
- S2-F8, the whole registration check removed: Failed 2 of 999, both.
- S2-F10, the database suffix match back in `RevokeOtherKeysOfDirector`: Failed 1 of 999, the non-ASCII case. The ASCII
  case passes against it on SQLite, which is why the non-ASCII case is there.

What holds the key row up now: in a team, only a Director whose roster alone lists the id, and that wrote nothing of
another person's, may write it. The Director half - sending keys before the roster on a reseed
(`GatewayStreamClient.ReseedAsync`, roster at line 724, keys from line 742, `RegisterSessionKey` at 787) - is a
follow-up outside this pull request. Corrected in round 4 (S2-F13): it does not merely narrow a delay. After a Gateway
restart the roster is empty until each Director sends it again, and a session that has no key row at that moment can be
keyed by a colleague's Director first; the Director half, keys before the roster, is what closes that harm. Teams must
not be released without it.

## #3552 review round 4

Rulings: `rulings-2311-step2-review4.md`. Fix commit 6263e2080, rebased onto origin/main a82762c44 (#3554, #3549,
#3542); one conflict, the hub's constructor, where #3549 added two parameters beside this branch's `teamOwnership`.

| Finding | Test | On 80d46ca40 | On the new head |
|---|---|---|---|
| S2-F11 | `TeamDirectorTunnelTests.AColleaguesDirector_RevokingAnotherPersonsSessionKey_EndsNothing_AndTheOwnersOwnRevokeStillWorks` (the reviewer's first round-4 reproduction, turned round) | fails | PASS |
| S2-F11 | `InAPersonalTenant_AnotherDirectorOfTheSamePerson_StillEndsTheSessionsKey_AsBefore` - personal tenant unchanged | PASS | PASS |
| S2-F12 | `WhileAColleagueListsASessionId_ItsOwnDirectorStillRefreshesTheSessionsKey_SoTheKeyDoesNotRunOut` (second reproduction) | fails | PASS |
| S2-F12 | `WhileAColleagueListsASessionId_TheExpirySweepFindsTheOwnersRefreshedKey_AndLeavesItWorking` (third reproduction) | fails | PASS |
| S2-F13 | `Gap_AfterAGatewayRestart_ASessionStillWithoutAKeyRow_CanBeKeyedByAColleagueFirst_UntilTheDirectorSendsKeysFirst` (fourth reproduction, kept passing on purpose: it pins the gap the Director half closes) | PASS | PASS |
| Question 1 | `AColleaguesSessionsFoldedState_IsSentOnlyToItsOwnDirector_NotToADirectorThatListsItsId` | does not build (the filter is the fix) | PASS |
| Question 2 | `ATurnEnd_ReportedForAColleaguesSession_ByADirectorThatListsItsId_IsNotTaken_ThroughEitherFeed` | does not build (the filter is the fix) | PASS |

"On 80d46ca40" for the first four rows was run literally: that head's `src/CcDirector.Gateway` checked out over the new
head, the two question tests (which need the new constructor parameters) compiled out, a build that succeeded, and the
four tests run: 3 failed, the personal-tenant one passed. Restored by a trap, diff empty.

Revert lines - each old behaviour put back on the new head, one at a time, on a build that succeeded, with the Teams,
display-state and turn-end namespaces run (1,054 tests, 4 skipped), restored by a trap and checked for an empty diff:

- S2-F11, the revoke not checking whose the key row is (`DirectorHub.cs:740` gated off): Failed 1 of 1,054, the revoke test.
- S2-F12, the roster half asked even with a key row (`DirectorHub.cs:507` always true): Failed 2 of 1,054, the two refresh tests.
- Question 2, the watcher taking any Director's report (`TurnEndWatcher.cs:168` gated off): Failed 1 of 1,054, the turn-end test.
- Question 2, the watcher taking any Director's removal (`TurnEndWatcher.cs:233` gated off): Failed 1 of 1,054, the turn-end test.
- Question 1, the sweep sending to any Director that lists the id (`FleetDisplayStateObserver.cs:173` gated off): Failed 1 of 1,054, the folded-state test.
- Question 1, the one-session push doing the same (`FleetDisplayStateObserver.cs:253` gated off): Failed 1 of 1,054, the folded-state test.

S2-F14 has no new test, as ruled; the round 3 tests of `AnotherPersonHasActiveKeyForDirector` pass unchanged.

Both questions were harms in a team and are fixed here. The one rule for "is this session this Director's own" is now
`TeamCallerOwnership.ClaimOf` (stored writers, then the key row, then the sole roster holder); the hub's turn-push
check, the turn-end watcher and the display push all ask it. The host's `IsTeamSessionOfDirector`, which puts the
team-and-released check in front of it, is covered only by Gateway.Tests, not run here; the unit tests wire
`TeamCallerOwnership.AcceptsReport` directly.

## Schema

None. No table, column or index added; no migration. The team bill table is the website's; the Gateway.Tests helper
`HostedTeamBill` creates it in a test database only, with the columns the seam states.

## Not run here

Gateway.Tests (by instruction, for memory): compiled, not run. The Tech Lead starts continuous integration for them.
