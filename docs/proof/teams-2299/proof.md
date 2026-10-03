# Proof: the Gateway reads a team's bill (thefrederiksen/devthrottle_internal#2299)

The Gateway half of "one bill for the team". The Gateway READS `gateway.team_entitlements` (the website
creates and writes it) and decides paid features for a team's members. Contract:
`devthrottle_internal` `docs/missions/teams-v1-2026-10-03/seam-team-billing.md`, sections 1, 2, 4 and 5.

## What was built

| Piece | Where |
|---|---|
| `TeamEntitlementEntity`, mapped to `gateway.team_entitlements`, EXCLUDED FROM MIGRATIONS | `src/CcDirector.Gateway/Data/Entities/TeamEntitlementEntity.cs`, `Data/GatewayDbContext.cs` |
| The team read (three-way) | `EntitlementRegistry.EvaluateTeam` |
| The member decision (role as an input) | `EntitlementRegistry.EvaluateTeamMember` |
| The tenant-keyed person decision | `EntitlementRegistry.EvaluatePerson(subject, TeamSeat?, now)` |
| Which roles are paid seats (one list) | `TeamSeatRoles.IsPaidSeat` (in `EntitlementRegistry.cs`) |
| A team seat grants the Pro scopes | `EntitlementScopes`, the `TierTeam` line |
| The seat-sync client | `src/CcDirector.Core/Account/TeamSeatSyncClient.cs` |
| What the membership code calls after its commit | `TeamSeatSync.SyncAfterMembershipChangeAsync(teamId)` |
| The convergence check | `TeamSeatSync.ConvergeAsync(teamId, gatewayPaidMembers)`, `TeamSeatSync.Compare`, `TeamSeatSync.Decide` |

No Gateway migration. Both model snapshots gain the excluded entity (so `HasPendingModelChanges` stays false);
the generated migrations were empty (`Up` and `Down` did nothing) and were deleted.

## The tests the brief asked for, and where each lives

All in `src/CcDirector.Gateway.UnitTests/` (the `Gateway.UnitTests` suite - parked, so it runs under `-Parked`).

| Brief test | Test(s) |
|---|---|
| Paid features to every paid member (Owner, Manager, Developer) of an `active` team; none when `canceled` or no row | `TeamEntitlementTests.EvaluateTeamMember_ActiveBillAndPaidRole_GrantsTheProScopes` (x3 roles), `EvaluateTeamMember_CanceledBill_GrantsNothing` (x3), `EvaluateTeamMember_NoBillRow_GrantsNothing` (x3), `EvaluateTeam_UnrecognisedStatus_IsNotEntitled` |
| A 100%-discount subscription grants exactly as full price | `TeamEntitlementTests.EvaluateTeamMember_FullyDiscountedActiveBill_GrantsExactlyAsAFullPriceOne` |
| `past_due`, even after `current_period_end`, changes no member's access | `TeamEntitlementTests.EvaluateTeamMember_PastDueLongAfterThePeriodEnded_StillGrants` (x3), `EvaluateTeam_PastDueWithNoPeriodRecorded_StillGrants`; control that the personal rule is unchanged: `Evaluate_PersonalPastDueAfterThePeriodEnded_IsStillRefused_TheTeamRuleDoesNotLeak` |
| A Collaborator gets no paid scopes whatever the bill | `TeamEntitlementTests.EvaluateTeamMember_Collaborator_GetsNoPaidScopesWhateverTheBill` (active, past_due, canceled), `EvaluateTeamMember_CollaboratorWhenTheBillCannotBeRead_IsStillNotEntitled`, `EvaluateTeamMember_UnrecognisedRole_GetsNoPaidScopes` (x4) |
| A team read never consults the trial ledger | `TeamEntitlementTests.EvaluatePerson_TeamTenantWithNoBill_IgnoresTheOwnersRunningTrial` (a running trial row for the Owner's subject; control: it grants in the personal tenant), `EvaluateTeam_TrialLedgerUnreadable_IsNeverReached` (trial table dropped; control: the personal read goes Unknown) |
| A team entitlement never changes a personal tenant's answer; a personal Pro never grants in a team tenant | `TeamEntitlementTests.EvaluatePerson_PersonalTenant_IsUnchangedByAnActiveTeamBill`, `EvaluatePerson_PersonalProInATeamTenantWithNoBill_GrantsNothing` |
| Production hosted: a test-mode (`livemode` false or null) team row grants nothing | `TeamEntitlementTests.EvaluateTeam_ProductionHostedAndTestModeRow_GrantsNothing` (false, null); control `EvaluateTeam_ProductionHostedAndLiveRow_Grants` |
| A read failure is Unknown | `TeamEntitlementTests.EvaluateTeam_ReadFails_IsUnknownNeverAGrantNeverARefusal` |
| The sync client sends exactly `{team_id}` and the service header | `TeamSeatSyncTests.SyncSeatsAsync_AnyTeam_SendsOnlyTheTeamIdAndTheServiceHeader`, `SyncSeatsAsync_WebsiteRefuses_ReturnsNotSyncedWithTheWebsitesMessage` |
| Convergence says "call sync" when counts differ, nothing when equal | `TeamSeatSyncTests.Compare_GatewayCountAgainstBilledSeats_SaysCallSyncOnlyWhenTheyDiffer` (x4), `Compare_BillRecordedNoSeatCount_SaysCallSync`, `ConvergeAsync_CountsDiffer_CallsSyncOnce`, `ConvergeAsync_CountsEqual_CallsNothing`, `ConvergeAsync_PastDueAndCountsDiffer_StillCallsSync`, `ConvergeAsync_NoLiveBill_CallsNothing` (no row, canceled), `ConvergeAsync_TeamRowUnreadable_IsUnknownAndCallsNothing` |
| The method the membership code calls | `TeamSeatSyncTests.SyncAfterMembershipChangeAsync_AnyChange_CallsTheWebsiteForThatTeam`, `..._ServiceTokenNotSet_CallsNothingAndSaysWhy`, `..._WebsiteUnreachable_ReturnsNotSyncedWithoutThrowing` |
| Who counts as a paid seat | `TeamSeatSyncTests.IsPaidSeat_EachRole_CountsOnlyOwnerManagerAndDeveloper` |
| "A team seat gives paid features only on that team's Directors" - the part provable before #2311 | `TeamEntitlementTests.EvaluatePerson_SeatOnOnePaidTeam_GrantsNothingInAnotherTeamsTenant` (the decision is keyed on the tenant the request is in) |
| The table is the website's | `TeamEntitlementTests.TeamEntitlementsMapping_UnderSqlite_IsExcludedFromMigrationsAndUnqualified`, `TeamEntitlementsMapping_UnderPostgres_IsGatewaySchemaQualifiedAndExcludedFromMigrations`; `TenantGateArchitectureTests` lists it as a global table |

### What is NOT proven here

"A team seat gives paid features only on that team's Directors" end to end: the request path does not yet know
whether a request's tenant is a team, or which person is behind it. That arrives with the membership table
(#2300) and a key per Director (#2311). The decision itself is keyed on the tenant and is proven above; the
wiring into the request path is not built (see the pull request).

## Revert proof

Two policy lines were broken on the committed code and the two test classes run in full:
- `past_due` given the personal row's period cut-off -> red: `EvaluateTeamMember_PastDueLongAfterThePeriodEnded_StillGrants` (x3), `EvaluateTeam_PastDueWithNoPeriodRecorded_StillGrants`.
- `collaborator` made a paid seat -> red: `EvaluateTeamMember_Collaborator_GetsNoPaidScopesWhateverTheBill(active)`, `EvaluateTeamMember_CollaboratorWhenTheBillCannotBeRead_IsStillNotEntitled`, `IsPaidSeat_EachRole_CountsOnlyOwnerManagerAndDeveloper(collaborator)`.
Every other test stayed green; the source was restored and rebuilt before the gate runs below.

## Gate runs (on commit 943f6f38b)

`.\scripts	est-local.ps1` (default): GREEN. All ten suites outcome=Completed, every test executed, none failed:
Core.UnitTests 1148, Avalonia.Tests 860, Engine.Tests 68, HostedAgent.Tests 88, Launcher.Tests 197,
Terminal.Avalonia.Tests 64, Reclaim.Tests 310, cc-director-setup.Tests 25, cc-director-setup-engine.Tests 649,
cc-director-setup-cli.Tests 34 (3,443 tests).

`.\scripts	est-local.ps1 -Parked`: NOT COMPLETED. The run was stopped from outside by the machine for low
memory before the three parked suites (Core.Tests, Gateway.Tests, Gateway.UnitTests) reported, so there is no
verdict for them - this is not a pass. The ten default suites passed again inside that run.

What did run for the parked suite this change lives in: the two new test classes and the related existing
ones in Gateway.UnitTests (`TeamEntitlementTests`, `TeamSeatSyncTests`, `TenantGateArchitectureTests`,
`GatewayHostBootSmokeTests` - which asserts no pending model changes on both providers -
`FleetOutcomeStopIdentityMigrationTests`, `HostedEntitlementGateTests`, `HostedAccessPlanScopeTests`):
109 passed, 0 failed, 2 skipped of 111 (the skips need a real Postgres server).
