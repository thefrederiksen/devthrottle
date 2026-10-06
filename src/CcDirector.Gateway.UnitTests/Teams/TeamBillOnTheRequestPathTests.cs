using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Pairing;
using CcDirector.Gateway.Teams;
using CcDirector.Gateway.Tenancy;
using CcDirector.Gateway.Tests.Data;
using CcDirector.Gateway.Wingman;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CcDirector.Gateway.Tests.Teams;

/// <summary>
/// A team Director reads the TEAM's bill (devthrottle_internal#2311, Gateway step 2; #2299). The three readers that
/// used to read a team tenant through the person's own subject - the access lease, the Wingman's narration plan and the
/// start-up key reinstatement - now ask <see cref="TeamMemberEntitlement"/>: the person's membership and the team's bill.
///
/// What these hold: a member is never refused for the bill (free tier without one, team tier with one); a request that
/// names nobody, or a non-member, is refused as a PERSON and never reaches the revoke branch, so one stranger can never
/// tombstone the team's keys; a failed read is Unknown, never a grant and never a revoke; a person's own Pro never
/// reaches into a team; and a personal tenant reads exactly as it did. Over a real database with the website's two
/// billing tables, created here as the seam states their columns.
/// </summary>
public sealed class TeamBillOnTheRequestPathTests : IDisposable
{
    private const string Owner = "sub-owner";
    private const string Alice = "sub-alice";
    private const string Carol = "sub-carol";
    private const string Stranger = "sub-stranger";

    private static readonly DateTime OldRuleRevocation = new(2026, 9, 2, 0, 0, 15, DateTimeKind.Utc);

    private readonly GatewayDbTestHarness _harness = new();
    private readonly GatewayDatabase _db;
    private readonly TenantRegistry _tenants;
    private readonly TeamRegistry _teams;
    private readonly EntitlementRegistry _entitlements;
    private readonly TeamMemberEntitlement _teamEntitlement;
    private readonly RecordingRevoker _revoker = new();
    private readonly string _paidTeam;
    private readonly string _unpaidTeam;

    public TeamBillOnTheRequestPathTests()
    {
        _db = _harness.Open();
        using (var ctx = _db.CreateUnscopedContext())
        {
            ctx.Database.ExecuteSqlRaw(
                "CREATE TABLE IF NOT EXISTS entitlements (" +
                "subject TEXT NOT NULL PRIMARY KEY, status TEXT NOT NULL, " +
                "current_period_end TEXT NULL, stripe_subscription_id TEXT NULL, updated_at TEXT NULL, " +
                "livemode INTEGER NULL, tier TEXT NULL)");
            ctx.Database.ExecuteSqlRaw(
                "CREATE TABLE IF NOT EXISTS team_entitlements (" +
                "team_id TEXT NOT NULL PRIMARY KEY, status TEXT NOT NULL, seats INTEGER NULL, " +
                "current_period_end TEXT NULL, stripe_subscription_id TEXT NULL, livemode INTEGER NULL, updated_at TEXT NULL)");
        }
        _tenants = new TenantRegistry(_db);
        _teams = new TeamRegistry(_db, _tenants);
        _entitlements = new EntitlementRegistry(_db, requireLivemode: true, trials: new TrialRegistry(_db));
        _teamEntitlement = new TeamMemberEntitlement(_teams, _entitlements);

        _paidTeam = _teams.CreateTeam(Owner, "Paid").Team!.TeamId;
        _unpaidTeam = _teams.CreateTeam(Owner, "Unpaid").Team!.TeamId;
        foreach (var team in new[] { _paidTeam, _unpaidTeam })
        {
            Assert.True(_teams.AddMember(team, Alice, TeamRole.Developer).IsDone);
            Assert.True(_teams.AddMember(team, Carol, TeamRole.Collaborator).IsDone);
        }
        SeedTeamBill(_paidTeam, EntitlementRegistry.StatusActive, livemode: true);
    }

    public void Dispose() => _harness.Dispose();

    private sealed class RecordingRevoker : ITenantAccessRevoker
    {
        public List<string> Revoked { get; } = new();

        public Task RevokeAsync(TenantId tenant, string reason, CancellationToken ct = default)
        {
            Revoked.Add(tenant.Value);
            return Task.CompletedTask;
        }
    }

    private void SeedTeamBill(string teamId, string status, bool livemode)
    {
        using var ctx = _db.CreateUnscopedContext();
        ctx.Database.ExecuteSqlRaw(
            "INSERT INTO team_entitlements (team_id, status, seats, livemode) VALUES ({0}, {1}, {2}, {3})",
            teamId, status, 2, livemode);
    }

    private void SeedPersonalPro(string subject)
    {
        using var ctx = _db.CreateUnscopedContext();
        ctx.Database.ExecuteSqlRaw(
            "INSERT INTO entitlements (subject, status, current_period_end, livemode, tier) VALUES ({0}, 'active', '2030-01-01T00:00:00Z', 1, {1})",
            subject, EntitlementRegistry.TierPro);
    }

    private HostedAccessLeaseService Leases(bool teamsWired = true) =>
        new(_entitlements, _tenants, _revoker, teams: teamsWired ? _teamEntitlement : null);

    // ---- the access lease -----------------------------------------------------------------------------------------

    [Fact]
    public async Task AuthorizeAsync_AMemberOfAPaidTeam_AndOfAnUnpaidTeam_AreBothAllowed_AndNothingIsRevoked()
    {
        var leases = Leases();

        Assert.Equal(HostedAccessDecision.Allow, await leases.AuthorizeAsync(new TenantId(_paidTeam), () => Alice));
        Assert.Equal(HostedAccessDecision.Allow, await leases.AuthorizeAsync(new TenantId(_unpaidTeam), () => Alice));
        Assert.Equal(HostedAccessDecision.Allow, await leases.AuthorizeAsync(new TenantId(_paidTeam), () => Carol));
        Assert.Empty(_revoker.Revoked);
    }

    [Fact]
    public async Task AuthorizeAsync_ANonMember_IsRefusedAsAPerson_NeverRevoked_AndTheTeamsMembersStayAllowed()
    {
        var leases = Leases();
        var team = new TenantId(_paidTeam);

        Assert.Equal(HostedAccessDecision.DenyNotAMember, await leases.AuthorizeAsync(team, () => Stranger));
        Assert.Equal(HostedAccessDecision.DenyNotAMember, await leases.AuthorizeAsync(team, () => null));
        Assert.Equal(HostedAccessDecision.DenyNotAMember, await leases.AuthorizeAsync(team));

        Assert.Empty(_revoker.Revoked);
        Assert.Equal(HostedAccessDecision.Allow, await leases.AuthorizeAsync(team, () => Alice));
    }

    [Fact]
    public async Task AuthorizeAsync_AMembersLease_IsTheirsAlone_AStrangerCannotRideIt()
    {
        var leases = Leases();
        var team = new TenantId(_paidTeam);
        Assert.Equal(HostedAccessDecision.Allow, await leases.AuthorizeAsync(team, () => Alice));

        Assert.Equal(HostedAccessDecision.DenyNotAMember, await leases.AuthorizeAsync(team, () => Stranger));
        Assert.Equal(new[] { new LiveLease(team, Alice) }, leases.LiveLeases().ToArray());
    }

    [Fact]
    public async Task AuthorizeAsync_APaidSeatWhoseBillCannotBeRead_IsUnknown_NeitherAGrantNorARevoke()
    {
        using (var ctx = _db.CreateUnscopedContext())
            ctx.Database.ExecuteSqlRaw("DROP TABLE team_entitlements");
        var leases = Leases();

        Assert.Equal(HostedAccessDecision.RetryUnknown, await leases.AuthorizeAsync(new TenantId(_paidTeam), () => Alice));
        Assert.Empty(_revoker.Revoked);
        Assert.Empty(leases.LiveLeases());
    }

    /// <summary>
    /// THE THREE STATES OF THE TEAM BILL TABLE, for the person behind a team Director's key (the Delivery Lead's question
    /// on #3552: was CI's 503 a setup gap, or a defect where "no row" answers 503 instead of "not entitled"?). Decided here:
    /// <list type="bullet">
    /// <item>Table PRESENT, NO row for the team: Allow - the member is served on the FREE tier. Not 503, and not 402: a
    /// team member is never refused for the team's bill (step 2's rule, <see cref="TeamMemberEntitlement"/>). A 402
    /// (<see cref="HostedAccessDecision.DenyNotEntitled"/>) is also the decision that revokes the tenant's devices, so
    /// answering it for an unpaid team would revoke every member's keys in the team.</item>
    /// <item>Table present, an ACTIVE LIVE row: Allow, on the team tier.</item>
    /// <item>Table MISSING: <see cref="HostedAccessDecision.RetryUnknown"/>, which the auth middleware answers 503
    /// entitlement_unknown - failing closed, with nothing revoked. So CI's 503 was the missing table (a setup gap in the
    /// test), and Teams must not be released before <c>team_entitlements</c> exists in production.</item>
    /// </list>
    /// </summary>
    [Fact]
    public async Task AuthorizeAsync_ATeamDirectorsPerson_NoBillRowIsTheFreeTier_ALiveRowIsServed_AMissingTableIsUnknown()
    {
        // Table present, no row for the team: served, on the free tier. Never 503, never 402.
        Assert.Equal(HostedAccessDecision.Allow, await Leases().AuthorizeAsync(new TenantId(_unpaidTeam), () => Alice));
        Assert.Equal(EntitlementRegistry.TierFree,
            _teamEntitlement.Decide(new TenantId(_unpaidTeam), Alice, DateTime.UtcNow).Entitlement!.Tier);

        // Table present, an active live row: served, on the team's tier (not the free one).
        Assert.Equal(HostedAccessDecision.Allow, await Leases().AuthorizeAsync(new TenantId(_paidTeam), () => Alice));
        Assert.NotEqual(EntitlementRegistry.TierFree,
            _teamEntitlement.Decide(new TenantId(_paidTeam), Alice, DateTime.UtcNow).Entitlement!.Tier);

        // Table missing: unknown for both teams (503 entitlement_unknown on the wire), never a grant, never a revoke.
        using (var ctx = _db.CreateUnscopedContext())
            ctx.Database.ExecuteSqlRaw("DROP TABLE team_entitlements");
        Assert.Equal(HostedAccessDecision.RetryUnknown, await Leases().AuthorizeAsync(new TenantId(_unpaidTeam), () => Alice));
        Assert.Equal(HostedAccessDecision.RetryUnknown, await Leases().AuthorizeAsync(new TenantId(_paidTeam), () => Alice));

        Assert.Empty(_revoker.Revoked);
    }

    [Fact]
    public async Task RefreshAsync_AMemberDemotedToCollaborator_StaysAllowedOnTheFreeTier_AndTheRevokeBranchIsNeverReached()
    {
        var leases = Leases();
        var team = new TenantId(_paidTeam);
        Assert.Equal(HostedAccessDecision.Allow, await leases.AuthorizeAsync(team, () => Alice));

        Assert.True(_teams.ChangeRole(_paidTeam, Alice, TeamRole.Collaborator).IsDone);

        Assert.Equal(HostedAccessDecision.Allow, await leases.RefreshAsync(new LiveLease(team, Alice)));
        Assert.Empty(_revoker.Revoked);
        var decision = _teamEntitlement.Decide(team, Alice, DateTime.UtcNow);
        Assert.Equal(EntitlementRegistry.TierFree, decision.Entitlement!.Tier);
    }

    [Fact]
    public async Task SweepOnceAsync_ReReadsEachPersonsLease_AndARemovedMembersLeaseIsDropped_WithNothingRevoked()
    {
        var leases = Leases();
        var team = new TenantId(_paidTeam);
        Assert.Equal(HostedAccessDecision.Allow, await leases.AuthorizeAsync(team, () => Alice));
        Assert.Equal(HostedAccessDecision.Allow, await leases.AuthorizeAsync(team, () => Carol));
        Assert.True(_teams.RemoveMember(_paidTeam, Alice).IsDone);

        await new EntitlementLeaseMonitor(leases).SweepOnceAsync();

        Assert.Equal(new[] { new LiveLease(team, Carol) }, leases.LiveLeases().ToArray());
        Assert.Equal(new[] { team }, leases.TenantsWithLiveLease().ToArray());
        Assert.Empty(_revoker.Revoked);
    }

    [Fact]
    public async Task InvalidateLease_DropsEveryPersonsLeaseInTheTeam_AndNoOtherTenants()
    {
        var leases = Leases();
        var team = new TenantId(_paidTeam);
        var other = new TenantId(_unpaidTeam);
        await leases.AuthorizeAsync(team, () => Alice);
        await leases.AuthorizeAsync(team, () => Carol);
        await leases.AuthorizeAsync(other, () => Alice);

        leases.InvalidateLease(team);

        Assert.Equal(new[] { new LiveLease(other, Alice) }, leases.LiveLeases().ToArray());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AuthorizeAsync_APersonalTenant_ReadsExactlyAsBefore_WithOrWithoutTheTeamBranchWired(bool teamsWired)
    {
        // The same personal scenarios through a lease with the team branch and one without: the same answers, the same
        // revocations. A Pro account is allowed; a self-host plan is denied and not revoked, as it always was.
        SeedPersonalPro(Alice);
        var alicesTenant = _tenants.MintOrLookupBySubject(Alice, null);
        using (var ctx = _db.CreateUnscopedContext())
            ctx.Database.ExecuteSqlRaw(
                "INSERT INTO entitlements (subject, status, current_period_end, livemode, tier) VALUES ({0}, 'active', '2030-01-01T00:00:00Z', 1, {1})",
                "sub-selfhost", EntitlementRegistry.TierProSelfHost);
        var selfHostTenant = _tenants.MintOrLookupBySubject("sub-selfhost", null);
        var leases = Leases(teamsWired);

        Assert.Equal(HostedAccessDecision.Allow, await leases.AuthorizeAsync(alicesTenant, () => throw new InvalidOperationException("a personal tenant never asks for the person")));
        Assert.Equal(HostedAccessDecision.DenyNotEntitled, await leases.AuthorizeAsync(selfHostTenant));
        Assert.Empty(_revoker.Revoked);
        Assert.Equal(new[] { new LiveLease(alicesTenant, null) }, leases.LiveLeases().ToArray());
    }

    [Fact]
    public async Task AuthorizeAsync_APersonsOwnPro_NeverReachesIntoATeam_AndATeamSeat_NeverReachesOut()
    {
        // Alice holds a personal Pro AND a seat on the paid team. In the unpaid team she is on the free tier - her Pro
        // does not carry in - and in her own tenant she is on Pro, with nothing of the team's carried out.
        SeedPersonalPro(Alice);
        var now = DateTime.UtcNow;

        Assert.Equal(EntitlementRegistry.TierTeam, _teamEntitlement.Decide(new TenantId(_paidTeam), Alice, now).Entitlement!.Tier);
        Assert.Equal(EntitlementRegistry.TierFree, _teamEntitlement.Decide(new TenantId(_unpaidTeam), Alice, now).Entitlement!.Tier);
        Assert.Equal(EntitlementRegistry.TierPro, _entitlements.EvaluatePersonalTenant(Alice, now).Tier);
        Assert.Equal(HostedAccessDecision.Allow, await Leases().AuthorizeAsync(new TenantId(_unpaidTeam), () => Alice));
    }

    // ---- TeamMemberEntitlement --------------------------------------------------------------------------------------

    [Fact]
    public void Decide_ReadsMembershipFromTheTeamsTable_AndATestModeBillGivesTheFreeTier()
    {
        var testModeTeam = _teams.CreateTeam(Owner, "Test mode").Team!.TeamId;
        SeedTeamBill(testModeTeam, EntitlementRegistry.StatusActive, livemode: false);
        var now = DateTime.UtcNow;

        Assert.True(_teamEntitlement.IsTeam(new TenantId(_paidTeam)));
        Assert.False(_teamEntitlement.IsTeam(_tenants.MintOrLookupBySubject(Alice, null)));
        Assert.False(_teamEntitlement.Decide(new TenantId(_paidTeam), Stranger, now).IsMember);
        Assert.Equal(EntitlementRegistry.TierTeam, _teamEntitlement.Decide(new TenantId(_paidTeam), Owner, now).Entitlement!.Tier);
        Assert.Equal(EntitlementRegistry.TierFree, _teamEntitlement.Decide(new TenantId(testModeTeam), Owner, now).Entitlement!.Tier);
        Assert.Throws<ArgumentException>(() => _teamEntitlement.Decide(new TenantId(_paidTeam), " ", now));
        Assert.Throws<ArgumentNullException>(() => new TeamMemberEntitlement(null!, _entitlements));
        Assert.Throws<ArgumentNullException>(() => new TeamMemberEntitlement(_teams, null!));
    }

    [Fact]
    public void DecideOrUnknown_AMembershipReadThatFails_IsNotKnown_NeverNotAMember()
    {
        using (var ctx = _db.CreateUnscopedContext())
            ctx.Database.ExecuteSqlRaw("DROP TABLE team_members");

        Assert.Null(_teamEntitlement.DecideOrUnknown(new TenantId(_paidTeam), Alice, DateTime.UtcNow));
    }

    // ---- the narration plan -----------------------------------------------------------------------------------------

    [Fact]
    public void DecideForTeamSession_IsTheOwnersAnswerInThatTeam_AndNobodysSessionIsUnknown()
    {
        var now = DateTime.UtcNow;
        Assert.Equal(NarrationPlan.Allowed,
            NarrationPlanRule.DecideForTeamSession(Alice, _teamEntitlement.Decide(new TenantId(_paidTeam), Alice, now)));
        Assert.Equal(NarrationPlan.NeedsPro,
            NarrationPlanRule.DecideForTeamSession(Alice, _teamEntitlement.Decide(new TenantId(_unpaidTeam), Alice, now)));
        Assert.Equal(NarrationPlan.NeedsPro,
            NarrationPlanRule.DecideForTeamSession(Carol, _teamEntitlement.Decide(new TenantId(_paidTeam), Carol, now)));
        Assert.Equal(NarrationPlan.Unknown,
            NarrationPlanRule.DecideForTeamSession(Stranger, _teamEntitlement.Decide(new TenantId(_paidTeam), Stranger, now)));
        Assert.Equal(NarrationPlan.Unknown, NarrationPlanRule.DecideForTeamSession(null, null));
        Assert.Equal(NarrationPlan.Unknown, NarrationPlanRule.DecideForTeamSession(Alice, null));
    }

    // ---- the start-up key reinstatement -----------------------------------------------------------------------------

    [Fact]
    public void Run_InATeam_GivesBackOnlyAMembersKeys_PersonByPerson_AndAPersonalTenantAsBefore()
    {
        using var devices = new DeviceRegistry(_db, _harness.LegacyPath("devices.json"), isHosted: true, teamsReleased: true);
        var team = new TenantId(_paidTeam);
        var aliceKey = devices.RegisterForTenant(team, Alice, _paidTeam + "|a", "M").DeviceKey;
        var strangerKey = devices.RegisterForTenant(team, Stranger, _paidTeam + "|s", "M").DeviceKey;
        devices.RevokeTenant(team, TenantAccessRevokeReasons.EntitlementLost, OldRuleRevocation);
        var personal = _tenants.MintOrLookupBySubject("sub-free", "free@example.com");
        var personalKey = devices.RegisterForTenant(personal, "sub-free", "home", "M").DeviceKey;
        devices.RevokeTenant(personal, TenantAccessRevokeReasons.EntitlementLost, OldRuleRevocation);

        var reinstated = PreFreeTierKeyReinstatement.Run(devices, _tenants, _entitlements, DateTime.UtcNow, _teamEntitlement);

        Assert.Equal(2, reinstated);
        Assert.Equal(DeviceCredentialResolutionKind.Active, devices.ResolveCredential(aliceKey).Kind);
        Assert.Equal(DeviceCredentialResolutionKind.Revoked, devices.ResolveCredential(strangerKey).Kind);
        Assert.Equal(DeviceCredentialResolutionKind.Active, devices.ResolveCredential(personalKey).Kind);
    }

    [Fact]
    public void Run_InATeam_WithoutTheTeamBranch_ReadsTheTeamThroughThePersonalRuleAndGivesNothingBack()
    {
        using var devices = new DeviceRegistry(_db, _harness.LegacyPath("devices.json"), isHosted: true, teamsReleased: true);
        var team = new TenantId(_paidTeam);
        var aliceKey = devices.RegisterForTenant(team, Alice, _paidTeam + "|a", "M").DeviceKey;
        devices.RevokeTenant(team, TenantAccessRevokeReasons.EntitlementLost, OldRuleRevocation);

        Assert.Equal(0, PreFreeTierKeyReinstatement.Run(devices, _tenants, _entitlements, DateTime.UtcNow));
        Assert.Equal(DeviceCredentialResolutionKind.Revoked, devices.ResolveCredential(aliceKey).Kind);
    }
}
