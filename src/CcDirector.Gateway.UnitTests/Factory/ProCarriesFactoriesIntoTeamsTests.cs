using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Data.Entities;
using CcDirector.Gateway.Factory;
using CcDirector.Gateway.Running;
using CcDirector.Gateway.Settings;
using CcDirector.Gateway.Teams;
using CcDirector.Gateway.Tenancy;
using CcDirector.Gateway.Tests.Data;
using CcDirector.Gateway.Tests.Teams;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace CcDirector.Gateway.Tests.Factory;

/// <summary>
/// PRO CARRIES FACTORIES INTO THE TEAMS ITS OWNER OWNS (owner ruling, 9 November 2026: "Yes, Pro carries factories into
/// my teams."). A team is its own account, so before this the factory switch read only the TEAM's bill there, and a team
/// with no bill could not run a factory although its Owner was on Pro. Now the Owner's own plan grants too, read live on
/// every question, combined with the team's bill by OR. A personal account is never asked about an Owner.
///
/// Everything runs over a real migrated database and the REAL plan reads (<see cref="EntitlementRegistry"/>, the trial
/// ledger, the team bill, <see cref="TenantAiGate"/>, <see cref="TeamOwnerPlan"/>), with a fixed clock and no port.
///
/// HOW THESE FAIL ON PURPOSE: drop the Owner's grant and the Pro and trial rows go red; copy the Owner's answer onto the
/// team instead of reading it live and the "loses Pro" row goes red; replace the OR with the Owner alone and the
/// bill-only row goes red; ask the Owner of a personal account and the personal rows go red; word the team's off
/// answer as the personal one and the sentence rows go red.
/// </summary>
public sealed class ProCarriesFactoriesIntoTeamsTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 11, 9, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime FarFuture = new(2099, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private readonly GatewayDbTestHarness _harness = new();
    private readonly GatewayDatabase _db;
    private readonly TenantRegistry _tenants;
    private readonly EntitlementRegistry _entitlements;
    private readonly TeamRegistry _teams;
    private readonly TenantAiGate _plans;
    private readonly List<string> _log = new();

    public ProCarriesFactoriesIntoTeamsTests()
    {
        _db = _harness.Open();
        using (var ctx = _db.CreateUnscopedContext())
        {
            ctx.Database.ExecuteSqlRaw(
                "CREATE TABLE IF NOT EXISTS entitlements (" +
                "subject TEXT NOT NULL PRIMARY KEY, status TEXT NOT NULL, " +
                "current_period_end TEXT NULL, stripe_subscription_id TEXT NULL, updated_at TEXT NULL, " +
                "livemode INTEGER NULL, tier TEXT NULL)");
        }
        _tenants = new TenantRegistry(_db);
        // The production shape: live money required and the trial ledger wired.
        _entitlements = new EntitlementRegistry(_db, requireLivemode: true, trials: new TrialRegistry(_db));
        _teams = new TeamRegistry(_db, _tenants);
        _plans = new TenantAiGate(_entitlements, _tenants, new TeamMemberEntitlement(_teams, _entitlements),
            clock: new FakeTimeProvider(new DateTimeOffset(Now)));
    }

    public void Dispose() => _harness.Dispose();

    // ================================================================= set-up

    private TenantId TenantOf(string subject) => _tenants.MintOrLookupBySubject(subject, subject + "@example.com");

    private void Paid(string subject, string tier, string status = EntitlementRegistry.StatusActive)
    {
        using var ctx = _db.CreateUnscopedContext();
        ctx.Database.ExecuteSqlRaw(
            "INSERT INTO entitlements (subject, status, current_period_end, livemode, tier) VALUES ({0}, {1}, {2}, {3}, {4})",
            subject, status, FarFuture, true, tier);
    }

    private void RunningTrial(string subject)
    {
        using var ctx = _db.CreateUnscopedContext();
        ctx.AccountTrials.Add(new AccountTrialEntity
        {
            Subject = subject,
            StartedAtUtc = Now.AddDays(-1),
            ExpiresAtUtc = Now.AddDays(13),
        });
        ctx.SaveChanges();
    }

    private void Cancel(string subject)
    {
        using var ctx = _db.CreateUnscopedContext();
        ctx.Database.ExecuteSqlRaw("UPDATE entitlements SET status = {0} WHERE subject = {1}",
            EntitlementRegistry.StatusCanceled, subject);
    }

    /// <summary>A team owned by <paramref name="owner"/>, with no team bill.</summary>
    private TenantId TeamOwnedBy(string owner)
    {
        TenantOf(owner);
        return _teams.CreateTeam(owner, "A team").Team!.Tenant;
    }

    private TeamOwnerFactories TeamOwners() => new(_teams, TeamOwnerPlan.Hosted(_entitlements), () => Now, _log.Add);

    /// <summary>The hosted switch as GatewayHost builds it where Teams is released.</summary>
    private FactoryAgentsSwitch HostedSwitch()
        => new(false, new TenantSettingsStore(_db), _plans, _log.Add, TeamOwners());

    /// <summary>The hosted switch as it was before the ruling: no team Owner grant.</summary>
    private FactoryAgentsSwitch HostedSwitchWithoutTeamOwners()
        => new(false, new TenantSettingsStore(_db), _plans, _log.Add);

    // ================================================================= a team's account

    [Fact]
    public void Answer_TeamWhoseOwnerIsOnProWithNoTeamBill_IsOnByTheOwnersPlan()
    {
        // Arrange
        Paid("sub-owner-pro", EntitlementRegistry.TierPro);
        var team = TeamOwnedBy("sub-owner-pro");

        // Act
        var answer = HostedSwitch().Answer(team);

        // Assert
        Assert.Equal(FactoryAgentsState.On, answer.State);
        Assert.Equal(FactoryAgentsGrant.TeamOwnerPlan, answer.Grant);
        Assert.Equal(EntitlementRegistry.TierPro, answer.Tier);
        Assert.Equal("team owner's plan tier pro", answer.GrantName);
    }

    [Fact]
    public void Answer_TeamWhoseOwnerIsOnARunningTrial_IsOnByTheOwnersPlan()
    {
        // Arrange - no paid row at all; the trial ledger counts the Owner as Pro.
        RunningTrial("sub-owner-trial");
        var team = TeamOwnedBy("sub-owner-trial");

        // Act
        var answer = HostedSwitch().Answer(team);

        // Assert
        Assert.Equal(FactoryAgentsState.On, answer.State);
        Assert.Equal(FactoryAgentsGrant.TeamOwnerPlan, answer.Grant);
    }

    [Fact]
    public void Answer_TeamWhoseOwnerIsOnTheFreePlanWithNoTeamBill_IsOffForTheOwnersPlan()
    {
        // Arrange
        var team = TeamOwnedBy("sub-owner-free");

        // Act
        var answer = HostedSwitch().Answer(team);

        // Assert
        Assert.Equal(FactoryAgentsState.Off, answer.State);
        Assert.Equal(FactoryAgentsOffReasons.TeamOwnerPlanRequired, answer.OffReason);
        Assert.Equal("team_owner_plan_required", answer.OffReason);
    }

    [Fact]
    public void Answer_TeamWhoseOwnerIsOnTheRemotePlan_IsOffForTheOwnersPlan()
    {
        // Arrange - Remote does not carry factories, so it cannot carry them into a team either.
        Paid("sub-owner-remote", EntitlementRegistry.TierRemote);
        var team = TeamOwnedBy("sub-owner-remote");

        // Act
        var answer = HostedSwitch().Answer(team);

        // Assert
        Assert.Equal(FactoryAgentsOffReasons.TeamOwnerPlanRequired, answer.OffReason);
    }

    [Fact]
    public void Answer_OwnerLosesPro_TheTeamsFactoriesGoOffOnTheNextQuestion()
    {
        // Arrange
        Paid("sub-owner-lapses", EntitlementRegistry.TierPro);
        var team = TeamOwnedBy("sub-owner-lapses");
        var sw = HostedSwitch();
        Assert.True(sw.IsOn(team));

        // Act - the SAME switch instance, so nothing it could have kept survives the change.
        Cancel("sub-owner-lapses");
        var answer = sw.Answer(team);

        // Assert
        Assert.Equal(FactoryAgentsState.Off, answer.State);
        Assert.Equal(FactoryAgentsOffReasons.TeamOwnerPlanRequired, answer.OffReason);
    }

    [Fact]
    public void Answer_TeamWithARunningBillAndAFreeOwner_IsStillOnByTheBill()
    {
        // Arrange - the OR: a team bill keeps what it already granted.
        var team = TeamOwnedBy("sub-owner-free-with-bill");
        TeamBillSeed.Active(_db, team.Value, seats: 3);

        // Act
        var answer = HostedSwitch().Answer(team);

        // Assert
        Assert.Equal(FactoryAgentsState.On, answer.State);
        Assert.Equal(FactoryAgentsGrant.Plan, answer.Grant);
    }

    [Fact]
    public void Answer_TeamWhoseOwnersPlanCannotBeReadAndNoTeamBill_IsUnknownNeverOff()
    {
        // Arrange - the entitlement table is gone, as when the database is unreachable: the Owner's plan is Unknown.
        var team = TeamOwnedBy("sub-owner-unreadable");
        using (var ctx = _db.CreateUnscopedContext())
            ctx.Database.ExecuteSqlRaw("DROP TABLE entitlements");

        // Act
        var answer = HostedSwitch().Answer(team);

        // Assert
        Assert.Equal(FactoryAgentsState.Unknown, answer.State);
        Assert.Equal(FactoryAgentsOffReasons.PlanUnknown, answer.OffReason);
    }

    [Fact]
    public void Answer_TeamWithNoOwnerRecorded_IsOffForTheOwnersPlan()
    {
        // Arrange - a broken team: its Owner row is gone, so no Pro account stands behind it.
        var team = TeamOwnedBy("sub-owner-gone");
        Paid("sub-owner-gone", EntitlementRegistry.TierPro);
        using (var ctx = _db.CreateUnscopedContext())
            ctx.Database.ExecuteSqlRaw("DELETE FROM team_members WHERE team_id = {0}", team.Value);

        // Act
        var answer = HostedSwitch().Answer(team);

        // Assert
        Assert.Equal(FactoryAgentsOffReasons.TeamOwnerPlanRequired, answer.OffReason);
        Assert.Contains(_log, l => l.Contains("NO Owner recorded", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(TeamRole.Manager)]
    [InlineData(TeamRole.Developer)]
    public void Answer_PaidSeatOnProInATeamWhoseOwnerIsOnFree_IsOff(TeamRole role)
    {
        // Arrange - only the OWNER's plan carries factories in; another member's own Pro never does.
        var team = TeamOwnedBy("sub-owner-free-with-pro-member");
        Paid("sub-member-pro", EntitlementRegistry.TierPro);
        TenantOf("sub-member-pro");
        using (var ctx = _db.CreateUnscopedContext())
        {
            ctx.TeamMembers.Add(new TeamMemberEntity { TeamId = team.Value, AccountSubject = "sub-member-pro", Role = role, JoinedAtUtc = Now });
            ctx.SaveChanges();
        }

        // Act
        var answer = HostedSwitch().Answer(team);

        // Assert
        Assert.Equal(FactoryAgentsOffReasons.TeamOwnerPlanRequired, answer.OffReason);
    }

    [Fact]
    public void Answer_OwnersPlanUnreadableButTheTeamBillRuns_IsOnByTheBill()
    {
        // Arrange - the OR holds through an unreadable Owner: the bill alone grants.
        var team = TeamOwnedBy("sub-owner-unreadable-with-bill");
        TeamBillSeed.Active(_db, team.Value, seats: 3);
        using (var ctx = _db.CreateUnscopedContext())
            ctx.Database.ExecuteSqlRaw("DROP TABLE entitlements");

        // Act
        var answer = HostedSwitch().Answer(team);

        // Assert
        Assert.Equal(FactoryAgentsState.On, answer.State);
        Assert.Equal(FactoryAgentsGrant.Plan, answer.Grant);
    }

    [Fact]
    public void Read_TheMembershipReadFails_IsUnknownAndLogged()
    {
        // Arrange - the team is known (settled when it was created), and the membership table is then gone.
        var team = TeamOwnedBy("sub-owner-membership-fails");
        using (var ctx = _db.CreateUnscopedContext())
            ctx.Database.ExecuteSqlRaw("DROP TABLE team_members");

        // Act
        var read = TeamOwners().Read(team);

        // Assert
        Assert.Equal(TeamOwnerPlanAnswer.Unknown, read!.Answer);
        Assert.Contains(_log, l => l.StartsWith("[TeamOwnerFactories] Read FAILED", StringComparison.Ordinal));
    }

    [Fact]
    public void Answer_SelfHostedGatewayHandedTeamOwners_NeverGrantsByTheOwner()
    {
        // Arrange - no plan read, so the Owner grant does not exist: a self-hosted Gateway is switched by config.json.
        var team = TeamOwnedBy("sub-owner-selfhost");
        var sw = new FactoryAgentsSwitch(false, new TenantSettingsStore(_db), TenantAiGate.SelfHost, _log.Add, TeamOwners());

        // Act
        var answer = sw.Answer(team);

        // Assert
        Assert.Equal(FactoryAgentsOffReasons.NotSwitchedOn, answer.OffReason);
    }

    // ================================================================= the words a person reads

    [Fact]
    public void SwitchAnswer_TeamOwnerPlanRequired_SaysWhyInPlainWordsWithNoPricingLink()
    {
        // Arrange
        var team = TeamOwnedBy("sub-owner-words");
        var answer = HostedSwitch().Answer(team);

        // Act
        var dto = FactoryAgentsViewEndpoints.SwitchAnswer(answer, hosted: true);

        // Assert
        Assert.False(dto.Enabled);
        Assert.Equal("team_owner_plan_required", dto.Reason);
        Assert.Equal("Factories in a team come with the Owner's Pro plan", dto.Title);
        Assert.Contains("this team's Owner is not on Pro", dto.HowToStart);
        Assert.Null(dto.SubscribeUrl);
    }

    [Fact]
    public void OffSentences_TeamOwnerPlanRequired_NameTheOwnersPlanEverywhere()
    {
        // Arrange
        var team = TeamOwnedBy("sub-owner-sentences");
        var answer = HostedSwitch().Answer(team);

        // Act
        var route = FactoryAgentsGate.OffSentence(answer, "nothing was started");
        var check = CronEngine.CheckOffSentence(answer);
        var schedule = CronEngine.FactoryOffReason(answer);

        // Assert
        Assert.Equal("Factories in a team come with the team Owner's Pro plan, and this team's Owner is not on Pro, so nothing was started.", route);
        Assert.Equal(CronEngine.TeamFactoriesNeedTheOwnersPlanCheckSentence, check);
        Assert.Contains("this team's Owner is not on Pro", schedule);
        Assert.DoesNotContain("administrator", route, StringComparison.OrdinalIgnoreCase);
    }

    // ================================================================= a personal account: unchanged

    [Theory]
    [InlineData("free")]
    [InlineData("pro")]
    [InlineData("remote")]
    [InlineData("trial")]
    [InlineData("pro-canceled")]
    public void Answer_PersonalAccount_IsExactlyAsBeforeTheRuling(string kind)
    {
        // Arrange
        var subject = "sub-personal-" + kind;
        switch (kind)
        {
            case "pro": Paid(subject, EntitlementRegistry.TierPro); break;
            case "remote": Paid(subject, EntitlementRegistry.TierRemote); break;
            case "trial": RunningTrial(subject); break;
            case "pro-canceled": Paid(subject, EntitlementRegistry.TierPro, EntitlementRegistry.StatusCanceled); break;
        }
        var tenant = TenantOf(subject);

        // Act
        var before = HostedSwitchWithoutTeamOwners().Answer(tenant);
        var after = HostedSwitch().Answer(tenant);

        // Assert
        Assert.Equal(before, after);
        Assert.NotEqual(FactoryAgentsGrant.TeamOwnerPlan, after.Grant);
        Assert.NotEqual(FactoryAgentsOffReasons.TeamOwnerPlanRequired, after.OffReason);
    }

    [Fact]
    public void Answer_PersonalAccountOfATeamOwnerOnFree_IsTheirOwnPlanNotTheTeams()
    {
        // Arrange - owning a team changes nothing about the Owner's own account.
        var team = TeamOwnedBy("sub-owner-personal");
        TeamBillSeed.Active(_db, team.Value, seats: 3);
        var personal = TenantOf("sub-owner-personal");

        // Act
        var answer = HostedSwitch().Answer(personal);

        // Assert
        Assert.Equal(FactoryAgentsOffReasons.PlanRequired, answer.OffReason);
    }

    // ================================================================= Teams stays dark by default

    [Fact]
    public void TeamsReleaseSwitch_Unset_StaysOff()
    {
        // Arrange / Act / Assert - the Owner grant is wired only where Teams is released, and that is off by default.
        Assert.False(TeamsReleaseSwitch.Parse(null));
        Assert.False(TeamsReleaseSwitch.Parse(""));
    }
}
