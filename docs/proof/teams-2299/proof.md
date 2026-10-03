# Proof: the Gateway reads a team's bill (thefrederiksen/devthrottle_internal#2299)

The Gateway half of "one bill for the team". The Gateway READS `gateway.team_entitlements` (the website
creates and writes it) and decides paid features for a team's members. Contract:
`devthrottle_internal` `docs/missions/teams-v1-2026-10-03/seam-team-billing.md`, sections 1, 2, 4 and 5.
Revised after the review (`reviews/review-2299-gateway-read.md` in the mission record): findings 2 and 3
changed the shape of the decision, findings 4 to 7 corrected comments and logging.

## What was built

| Piece | Where |
|---|---|
| `TeamEntitlementEntity`, mapped to `gateway.team_entitlements`, EXCLUDED FROM MIGRATIONS | `src/CcDirector.Gateway/Data/Entities/TeamEntitlementEntity.cs`, `Data/GatewayDbContext.cs` |
| The team BILL read (three-way: Entitled / NotEntitled / Unknown) | `EntitlementRegistry.EvaluateTeam` |
| A person in a PERSONAL tenant (unchanged behaviour) | `EntitlementRegistry.EvaluatePersonalTenant(subject, now)` |
| A person in a TEAM tenant | `EntitlementRegistry.EvaluateTeamTenant(teamId, TeamMembership, now)` -> `TeamTenantDecision` |
| The membership input (only `Member(role)` or `NotAMember`; no null) | `TeamMembership` (in `EntitlementRegistry.cs`) |
| Which roles are paid seats (one list) | `TeamSeatRoles.IsPaidSeat` (in `EntitlementRegistry.cs`) |
| A team seat grants the Pro scopes | `EntitlementScopes`, the `TierTeam` line |
| The seat-sync client | `src/CcDirector.Core/Account/TeamSeatSyncClient.cs` |
| What the membership code calls after its commit | `TeamSeatSync.SyncAfterMembershipChangeAsync(teamId)` |
| The convergence check | `TeamSeatSync.ConvergeAsync(teamId, gatewayPaidMembers)`, `TeamSeatSync.Compare`, `TeamSeatSync.Decide` |

The answer for a person in a team tenant:

| Situation | Answer |
|---|---|
| Owner, Manager or Developer; bill `active` or `past_due` (no cut-off), live money on production | member, Entitled at the team tier (the Pro scopes) |
| Owner, Manager or Developer; no bill yet, `canceled`, unrecognised state, or test mode on production | member, Entitled at the FREE tier (hosted access, no paid scopes) |
| Collaborator, or a role the code does not recognise, whatever the bill | member, Entitled at the FREE tier |
| Owner, Manager or Developer; the bill cannot be read | member, Unknown |
| Not a member of the team | NOT a member, no entitlement at all: a refusal of the person, never an entitlement verdict |

A member's answer is never NotEntitled, which is what the access lease turns into revoked device credentials.

No Gateway migration. Both model snapshots gain the excluded entity (so `HasPendingModelChanges` stays false);
the generated migrations were empty (`Up` and `Down` did nothing) and were deleted.

## The tests the brief and the review asked for, and where each lives

All in `src/CcDirector.Gateway.UnitTests/` (the `Gateway.UnitTests` suite - parked, so it runs under `-Parked`).

| Required | Test(s) |
|---|---|
| Paid features to every paid member (Owner, Manager, Developer) of an `active` team | `TeamEntitlementTests.EvaluateTeamTenant_ActiveBillAndPaidRole_GrantsTheProScopes` (x3 roles) |
| None when `canceled` or no row - and the member keeps the Gateway (review finding 2) | `EvaluateTeamTenant_CanceledBill_KeepsTheGatewayOnTheFreeTier` (x3), `EvaluateTeamTenant_BillNotStartedYet_KeepsTheGatewayOnTheFreeTier` (x3), `EvaluateTeamTenant_UnrecognisedBillState_KeepsTheGatewayOnTheFreeTier`, `EvaluateTeamTenant_AnyMemberAnyBill_IsNeverNotEntitled` (5 bill states x 5 roles); the raw bill read: `EvaluateTeam_BillThatDoesNotGrant_IsNotEntitled` (x3) |
| A 100%-discount subscription grants exactly as full price | `EvaluateTeamTenant_FullyDiscountedActiveBill_GrantsExactlyAsAFullPriceOne` (states the property; no column could carry a discount) |
| `past_due`, even after `current_period_end`, changes no member's access | `EvaluateTeamTenant_PastDueLongAfterThePeriodEnded_StillGrants` (x3), `EvaluateTeam_PastDueWithNoPeriodRecorded_StillGrants`; control: `Evaluate_PersonalPastDueAfterThePeriodEnded_IsStillRefused_TheTeamRuleDoesNotLeak` |
| A Collaborator gets no paid scopes whatever the bill, and keeps the Gateway (review finding 2) | `EvaluateTeamTenant_Collaborator_KeepsTheGatewayWithNoPaidScopesWhateverTheBill` (active, past_due, canceled, no row), `EvaluateTeamTenant_CollaboratorWhenTheBillCannotBeRead_IsStillTheFreeTier`, `EvaluateTeamTenant_UnrecognisedRole_GetsNoPaidScopes` (x4) |
| Team tenant, no seat (review finding 3) | `EvaluateTeamTenant_NotAMember_IsRefusedAsAPersonEvenWithAPersonalProAndAPaidTeam`, `EvaluateTeamTenant_NoMembershipValue_Throws` |
| A team read never consults the trial ledger | `EvaluateTeamTenant_NoBill_IgnoresTheOwnersRunningTrial` (control: the trial grants in the personal tenant), `EvaluateTeamTenant_TrialLedgerUnreadable_IsNeverReached` (control: the personal read goes Unknown) |
| A team entitlement never changes a personal tenant's answer; a personal Pro never grants in a team tenant | `EvaluatePersonalTenant_ActiveTeamBill_LeavesThePersonalAnswerUnchanged` (states the property), `EvaluateTeamTenant_PersonalProAndNoTeamBill_GetsNoPaidScopes` |
| Production hosted: a test-mode (`livemode` false or null) team row grants no paid scopes | `EvaluateTeamTenant_ProductionHostedAndTestModeRow_GetsNoPaidScopes` (false, null); control `EvaluateTeamTenant_ProductionHostedAndLiveRow_GrantsTheProScopes` |
| A read failure is Unknown | `EvaluateTeamTenant_PaidSeatAndBillReadFails_IsUnknownNeverAGrantNeverARefusal` |
| The sync client sends exactly `{team_id}` and the service header | `TeamSeatSyncTests.SyncSeatsAsync_AnyTeam_SendsOnlyTheTeamIdAndTheServiceHeader`, `SyncSeatsAsync_WebsiteRefuses_ReturnsNotSyncedWithTheWebsitesMessage` |
| Convergence says "call sync" when counts differ, nothing when equal | `TeamSeatSyncTests.Compare_GatewayCountAgainstBilledSeats_SaysCallSyncOnlyWhenTheyDiffer` (x4), `Compare_BillRecordedNoSeatCount_SaysCallSync`, `ConvergeAsync_CountsDiffer_CallsSyncOnce`, `ConvergeAsync_CountsEqual_CallsNothing`, `ConvergeAsync_PastDueAndCountsDiffer_StillCallsSync`, `ConvergeAsync_NoLiveBill_CallsNothing` (no row, canceled), `ConvergeAsync_TeamRowUnreadable_IsUnknownAndCallsNothing` |
| The method the membership code calls | `TeamSeatSyncTests.SyncAfterMembershipChangeAsync_AnyChange_CallsTheWebsiteForThatTeam`, `..._ServiceTokenNotSet_CallsNothingAndSaysWhy`, `..._WebsiteUnreachable_ReturnsNotSyncedWithoutThrowing` |
| Who counts as a paid seat | `TeamSeatSyncTests.IsPaidSeat_EachRole_CountsOnlyOwnerManagerAndDeveloper` |
| "A team seat gives paid features only on that team's Directors" - the part provable before #2311 | `EvaluateTeamTenant_SeatOnOnePaidTeam_GrantsNoPaidScopesInAnotherTeamsTenant` (the decision is keyed on the tenant the request is in) |
| The table is the website's | `TeamEntitlementsMapping_UnderSqlite_IsExcludedFromMigrationsAndUnqualified`, `TeamEntitlementsMapping_UnderPostgres_IsGatewaySchemaQualifiedAndExcludedFromMigrations`; `TenantGateArchitectureTests` lists it as a global table |

### What is NOT proven here

- **Nothing is on the request path.** No team tenant may serve production traffic until the access lease
  (`HostedAccessLeaseService.ReadUnderGateAsync`) and the narration plan (`GatewayHost.ResolveNarrationPlan`)
  call `EvaluateTeamTenant` for a team tenant. Today both still read the tenant's subject through the PERSONAL
  read. That wiring needs the membership table (#2300) and a key per Director (#2311).
- **Column drift against the website.** The tests' `CREATE TABLE` is this code's own statement of the columns
  it reads, not the website's migration. A renamed column would leave every test green and make every team
  read Unknown on production. The check that catches it is a read of the real table once the website migration
  exists, before Teams is released.

## Revert proof

Committed first (`e73ebeb7f`), then two lines broken and the two test classes run in full:
- A member whose team bill does not grant is answered NotEntitled instead of the free tier -> red: the canceled,
  not-started, unrecognised-state, test-mode and never-NotEntitled tests, plus the trial, personal-Pro and
  other-team tests that expect the free tier (17 cases).
- A non-member is treated as a member -> red: `EvaluateTeamTenant_NotAMember_IsRefusedAsAPersonEvenWithAPersonalProAndAPaidTeam`.

The first round, on the earlier shape, broke the `past_due` cut-off and the Collaborator rule and turned exactly
their tests red. Every other test stayed green each time; the source was restored and rebuilt before the gate.

## Gate runs (on commit e73ebeb7f)

`.\scripts\test-local.ps1` (default): GREEN. All ten suites outcome=Completed, every test executed, none
failed: Core.UnitTests 1148, Avalonia.Tests 860, Engine.Tests 68, HostedAgent.Tests 88, Launcher.Tests 197,
Terminal.Avalonia.Tests 64, Reclaim.Tests 310, cc-director-setup.Tests 25, cc-director-setup-engine.Tests 649,
cc-director-setup-cli.Tests 34 (3,443 tests).

`.\scripts\test-local.ps1 -Parked`: NOT RUN for this revision, on the Tech Lead's instruction (the machine
stopped it twice for low memory; the Tech Lead runs it when the machine is quiet). There is no verdict for
Core.Tests, Gateway.Tests or Gateway.UnitTests as a whole - this is not a pass.

What did run from the parked suite this change lives in: the related Gateway.UnitTests classes
(`TeamEntitlementTests`, `TeamSeatSyncTests`, `TenantGateArchitectureTests`, `GatewayHostBootSmokeTests` -
which asserts no pending model changes on both providers - `FleetOutcomeStopIdentityMigrationTests`,
`HostedEntitlementGateTests`, `HostedAccessPlanScopeTests`): 120 passed, 0 failed, 2 skipped of 122 (the skips
need a real Postgres server).
