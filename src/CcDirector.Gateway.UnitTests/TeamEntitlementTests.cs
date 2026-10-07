using System;
using System.Linq;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Data.Entities;
using CcDirector.Gateway.Tenancy;
using CcDirector.Gateway.Tests.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The Gateway half of devthrottle_internal #2299: a TEAM's bill decides whether the team's paid members hold paid
/// features. Since Teams v1's team bill without Stripe (owner, 7 Oct 2026) that bill is the Gateway's own
/// <c>team_bills</c> row, created by the Gateway's migrations and written only by <c>TeamBillStore</c>.
///
/// What these tests hold, in the words of the owner's decisions:
///  - Every paid member (Owner, Manager, Developer) of a team whose bill is active gets the Pro scopes.
///  - "Free keeps the Gateway" (2 and 22 Sep 2026): a member is never refused. A Collaborator, a team whose bill
///    has not started, and a canceled team all land on the FREE tier - hosted access, no paid scopes - and never on
///    NotEntitled, which the access lease turns into revoked device credentials for the whole team.
///  - A non-member is refused as a person ("not a member of this team"), never as an entitlement verdict.
///  - "Nothing stops for anyone" on a failed payment: past_due grants, with no period cut-off.
///  - "We do not do trials for teams": the trial ledger is never consulted for a team.
///  - "A personal seat and a team seat is different": neither carries over to the other, and in a team tenant the
///    absence of a membership can only ever mean a refusal - never the person's own row.
///  - The live-money rule guards the website's PERSONAL rows only; the Gateway's own team bill grants without it. A
///    failed bill read is Unknown - never a grant, never a refusal.
///
/// The PERSONAL table is created here with raw SQL, not by a migration, for the same reason production does not
/// migrate it: the website owns it.
/// </summary>
public sealed class TeamEntitlementTests : IDisposable
{
    private const string TeamA = "7f0c2a52-6f1e-4d7e-9b7a-0a1b2c3d4e5f";
    private const string TeamB = "1b2c3d4e-0000-4d7e-9b7a-aaaaaaaaaaaa";
    private const string OwnerSubject = "35543491-85cb-468d-a0c9-560193683105";

    private static readonly DateTime Now = new(2026, 10, 3, 12, 0, 0, DateTimeKind.Utc);

    private readonly GatewayDbTestHarness _harness = new();

    public void Dispose() => _harness.Dispose();

    /// <summary>Opens a database with the website's personal entitlement table (empty) beside the Gateway's own tables.</summary>
    private GatewayDatabase OpenWithBillingTables()
    {
        var db = _harness.Open();
        using var ctx = db.CreateUnscopedContext();
        ctx.Database.ExecuteSqlRaw(
            "CREATE TABLE IF NOT EXISTS entitlements (" +
            "subject TEXT NOT NULL PRIMARY KEY, status TEXT NOT NULL, " +
            "current_period_end TEXT NULL, stripe_subscription_id TEXT NULL, updated_at TEXT NULL, " +
            "livemode INTEGER NULL, tier TEXT NULL)");
        return db;
    }

    /// <summary>The team (a bill cannot exist without one) and its bill on the Gateway's own table.</summary>
    private static void SeedTeam(GatewayDatabase db, string teamId, string status, DateTime? periodEnd = null, int seats = 1)
    {
        using var ctx = db.CreateUnscopedContext();
        if (!ctx.Teams.Any(t => t.Id == teamId))
            ctx.Teams.Add(new TeamEntity { Id = teamId, Name = "Team " + teamId[..4], CreatedAtUtc = Now.AddDays(-30) });
        var end = periodEnd ?? Now.AddDays(20);
        ctx.TeamBills.Add(new TeamBillEntity
        {
            TeamId = teamId,
            Status = status,
            Seats = seats,
            PricePerSeatCents = CcDirector.Gateway.Teams.TeamBillStore.PricePerSeatCents,
            PlanStartedUtc = end.AddMonths(-1),
            CurrentPeriodStartUtc = end.AddMonths(-1),
            CurrentPeriodEndUtc = end,
            AutoRenew = status == EntitlementRegistry.StatusActive,
            CreatedAtUtc = end.AddMonths(-1),
            UpdatedAtUtc = end.AddMonths(-1),
            Version = 1,
        });
        ctx.SaveChanges();
    }

    /// <summary>Take the team bill table away, so every read of a team's bill FAILS for real.</summary>
    private static void BreakTheBillTable(GatewayDatabase db)
    {
        using var ctx = db.CreateUnscopedContext();
        ctx.Database.ExecuteSqlRaw("DROP TABLE team_bills");
    }

    private static void SeedPersonal(GatewayDatabase db, string subject, string status, string tier)
    {
        using var ctx = db.CreateUnscopedContext();
        ctx.Database.ExecuteSqlRaw(
            "INSERT INTO entitlements (subject, status, livemode, tier) VALUES ({0}, {1}, {2}, {3})",
            subject, status, true, tier);
    }

    private static void SeedRunningTrial(GatewayDatabase db, string subject)
    {
        using var ctx = db.CreateUnscopedContext();
        ctx.AccountTrials.Add(new AccountTrialEntity
        {
            Subject = subject,
            StartedAtUtc = Now.AddDays(-1),
            ExpiresAtUtc = Now.AddDays(13),
        });
        ctx.SaveChanges();
    }

    private static TeamMembership Member(string? role) => TeamMembership.Member(role);

    private static bool HasPaidScopes(EntitlementDecision? d) =>
        d is not null
        && d.Outcome == EntitlementOutcome.Entitled
        && EntitlementScopes.Grants(d.Tier, EntitlementScopes.Wingman)
        && EntitlementScopes.Grants(d.Tier, EntitlementScopes.Dictation)
        && EntitlementScopes.Grants(d.Tier, EntitlementScopes.Tts);

    /// <summary>A member who keeps the team's hosted Gateway and holds NONE of the paid scopes.</summary>
    private static void AssertFreeTierMember(TeamTenantDecision d)
    {
        Assert.True(d.IsMember);
        Assert.NotNull(d.Entitlement);
        Assert.Equal(EntitlementOutcome.Entitled, d.Entitlement!.Outcome);
        Assert.Equal(EntitlementRegistry.TierFree, d.Entitlement.Tier);
        Assert.True(EntitlementScopes.GrantsHostedGateway(d.Entitlement.Tier));
        Assert.False(EntitlementScopes.Grants(d.Entitlement.Tier, EntitlementScopes.Wingman));
        Assert.False(EntitlementScopes.Grants(d.Entitlement.Tier, EntitlementScopes.Dictation));
        Assert.False(EntitlementScopes.Grants(d.Entitlement.Tier, EntitlementScopes.Tts));
    }

    /// <summary>A member who holds the team seat: exactly the Pro scopes.</summary>
    private static void AssertTeamSeatMember(TeamTenantDecision d)
    {
        Assert.True(d.IsMember);
        Assert.NotNull(d.Entitlement);
        Assert.Equal(EntitlementOutcome.Entitled, d.Entitlement!.Outcome);
        Assert.Equal(EntitlementRegistry.TierTeam, d.Entitlement.Tier);
        // A team seat grants exactly what Pro grants - stated against the table, not hard-coded here.
        Assert.Equal(EntitlementScopes.ForTier(EntitlementRegistry.TierPro), EntitlementScopes.ForTier(d.Entitlement.Tier));
        Assert.True(HasPaidScopes(d.Entitlement));
    }

    // ---- An active bill grants every paid member the Pro scopes ------------------------------------------------

    [Theory]
    [InlineData(TeamSeatRoles.Owner)]
    [InlineData(TeamSeatRoles.Manager)]
    [InlineData(TeamSeatRoles.Developer)]
    public void EvaluateTeamTenant_ActiveBillAndPaidRole_GrantsTheProScopes(string role)
    {
        var db = OpenWithBillingTables();
        SeedTeam(db, TeamA, EntitlementRegistry.StatusActive);
        var registry = new EntitlementRegistry(db, requireLivemode: false);

        AssertTeamSeatMember(registry.EvaluateTeamTenant(TeamA, Member(role), Now));
    }

    // ---- A canceled team or a team whose bill has not started keeps its Gateway on the free tier ---------------

    [Theory]
    [InlineData(TeamSeatRoles.Owner)]
    [InlineData(TeamSeatRoles.Manager)]
    [InlineData(TeamSeatRoles.Developer)]
    public void EvaluateTeamTenant_CanceledBill_KeepsTheGatewayOnTheFreeTier(string role)
    {
        var db = OpenWithBillingTables();
        SeedTeam(db, TeamA, EntitlementRegistry.StatusCanceled);
        var registry = new EntitlementRegistry(db, requireLivemode: false);

        AssertFreeTierMember(registry.EvaluateTeamTenant(TeamA, Member(role), Now));
    }

    [Theory]
    [InlineData(TeamSeatRoles.Owner)]
    [InlineData(TeamSeatRoles.Manager)]
    [InlineData(TeamSeatRoles.Developer)]
    public void EvaluateTeamTenant_BillNotStartedYet_KeepsTheGatewayOnTheFreeTier(string role)
    {
        // Seam section 3: the Owner pressed Create team and is on the website checkout. The team exists, its bill
        // does not. The first request in the new tenant must not be a refusal (which the lease would turn into a
        // revocation of the brand-new tenant).
        var db = OpenWithBillingTables();
        var registry = new EntitlementRegistry(db, requireLivemode: false);

        AssertFreeTierMember(registry.EvaluateTeamTenant(TeamA, Member(role), Now));
    }

    [Fact]
    public void EvaluateTeamTenant_UnrecognisedBillState_KeepsTheGatewayOnTheFreeTier()
    {
        var db = OpenWithBillingTables();
        SeedTeam(db, TeamA, "incomplete");
        var registry = new EntitlementRegistry(db, requireLivemode: false);

        AssertFreeTierMember(registry.EvaluateTeamTenant(TeamA, Member(TeamSeatRoles.Owner), Now));
    }

    [Theory]
    [InlineData(EntitlementRegistry.StatusCanceled)]
    [InlineData("incomplete")]
    [InlineData(null)]          // no row
    public void EvaluateTeam_BillThatDoesNotGrant_IsNotEntitled(string? status)
    {
        // The raw BILL read keeps its three-way answer; only the person's answer turns it into the free tier.
        var db = OpenWithBillingTables();
        if (status is not null) SeedTeam(db, TeamA, status);
        var registry = new EntitlementRegistry(db, requireLivemode: false);

        Assert.Equal(EntitlementOutcome.NotEntitled, registry.EvaluateTeam(TeamA, Now).Outcome);
    }

    [Theory]
    [InlineData(EntitlementRegistry.StatusActive)]
    [InlineData(EntitlementRegistry.StatusPastDue)]
    [InlineData(EntitlementRegistry.StatusCanceled)]
    [InlineData("incomplete")]
    [InlineData(null)]          // no row
    public void EvaluateTeamTenant_AnyMemberAnyBill_IsNeverNotEntitled(string? status)
    {
        // The property the access lease depends on: NotEntitled is what revokes the tenant, so no member's answer
        // may ever be NotEntitled, whatever the role and whatever the bill.
        var db = OpenWithBillingTables();
        if (status is not null) SeedTeam(db, TeamA, status);
        var registry = new EntitlementRegistry(db, requireLivemode: false);

        foreach (var role in new[] { TeamSeatRoles.Owner, TeamSeatRoles.Manager, TeamSeatRoles.Developer, TeamSeatRoles.Collaborator, "admin" })
        {
            var decision = registry.EvaluateTeamTenant(TeamA, Member(role), Now);
            Assert.True(decision.IsMember);
            Assert.NotEqual(EntitlementOutcome.NotEntitled, decision.Entitlement!.Outcome);
        }
    }

    // ---- A 100%-discount subscription is an active row and grants exactly as a full-price one ------------------

    [Fact]
    public void EvaluateTeamTenant_FullyDiscountedActiveBill_GrantsExactlyAsAFullPriceOne()
    {
        // A discount would live on a payment provider's subscription; the Gateway's bill is an `active` row like any
        // other. This states that property; it cannot fail against any implementation that reads only this table,
        // because there is no column a discount could appear in.
        var db = OpenWithBillingTables();
        SeedTeam(db, TeamA, EntitlementRegistry.StatusActive, periodEnd: Now.AddDays(20), seats: 3);   // full price
        SeedTeam(db, TeamB, EntitlementRegistry.StatusActive, periodEnd: Now.AddDays(20), seats: 3);   // 100% coupon
        var registry = new EntitlementRegistry(db, requireLivemode: false);

        var fullPrice = registry.EvaluateTeamTenant(TeamA, Member(TeamSeatRoles.Developer), Now);
        var discounted = registry.EvaluateTeamTenant(TeamB, Member(TeamSeatRoles.Developer), Now);

        Assert.Equal(fullPrice.Entitlement, discounted.Entitlement);
        AssertTeamSeatMember(discounted);
    }

    // ---- A failed payment changes no member's access -----------------------------------------------------------

    [Theory]
    [InlineData(TeamSeatRoles.Owner)]
    [InlineData(TeamSeatRoles.Manager)]
    [InlineData(TeamSeatRoles.Developer)]
    public void EvaluateTeamTenant_PastDueLongAfterThePeriodEnded_StillGrants(string role)
    {
        // The personal row's past-due grace ends at the period end; a team's does not (owner, 3 Oct 2026:
        // "Nothing stops for anyone"). A year past the period end is still entitled.
        var db = OpenWithBillingTables();
        SeedTeam(db, TeamA, EntitlementRegistry.StatusPastDue, periodEnd: Now.AddDays(-365));
        var registry = new EntitlementRegistry(db, requireLivemode: false);

        var decision = registry.EvaluateTeamTenant(TeamA, Member(role), Now);

        AssertTeamSeatMember(decision);
        // No boundary is handed to the access lease, because there is none.
        Assert.Null(decision.Entitlement!.CurrentPeriodEnd);
    }

    [Fact]
    public void EvaluateTeam_PastDueWithNoPeriodRecorded_StillGrants()
    {
        var db = OpenWithBillingTables();
        SeedTeam(db, TeamA, EntitlementRegistry.StatusPastDue, periodEnd: null);
        var registry = new EntitlementRegistry(db, requireLivemode: false);

        Assert.Equal(EntitlementOutcome.Entitled, registry.EvaluateTeam(TeamA, Now).Outcome);
    }

    [Fact]
    public void Evaluate_PersonalPastDueAfterThePeriodEnded_IsStillRefused_TheTeamRuleDoesNotLeak()
    {
        // The control for the tests above: the PERSONAL policy is unchanged by the team one. Same state, same
        // dates, personal row -> refused.
        var db = OpenWithBillingTables();
        using (var ctx = db.CreateUnscopedContext())
        {
            ctx.Database.ExecuteSqlRaw(
                "INSERT INTO entitlements (subject, status, current_period_end, livemode, tier) VALUES ({0}, {1}, {2}, {3}, {4})",
                OwnerSubject, EntitlementRegistry.StatusPastDue, Now.AddDays(-365), true, EntitlementRegistry.TierPro);
        }
        var registry = new EntitlementRegistry(db, requireLivemode: false);

        Assert.Equal(EntitlementOutcome.NotEntitled, registry.EvaluatePersonalTenant(OwnerSubject, Now).Outcome);
    }

    // ---- A Collaborator works in the team, with no paid scopes, whatever the bill ------------------------------

    [Theory]
    [InlineData(EntitlementRegistry.StatusActive)]
    [InlineData(EntitlementRegistry.StatusPastDue)]
    [InlineData(EntitlementRegistry.StatusCanceled)]
    [InlineData(null)]          // no row
    public void EvaluateTeamTenant_Collaborator_KeepsTheGatewayWithNoPaidScopesWhateverTheBill(string? status)
    {
        var db = OpenWithBillingTables();
        if (status is not null) SeedTeam(db, TeamA, status);
        var registry = new EntitlementRegistry(db, requireLivemode: false);

        AssertFreeTierMember(registry.EvaluateTeamTenant(TeamA, Member(TeamSeatRoles.Collaborator), Now));
    }

    [Fact]
    public void EvaluateTeamTenant_CollaboratorWhenTheBillCannotBeRead_IsStillTheFreeTier()
    {
        // The role decides a Collaborator's answer, so a failed bill read does not turn it into an Unknown.
        var db = _harness.Open();
        BreakTheBillTable(db);
        var registry = new EntitlementRegistry(db, requireLivemode: false);

        AssertFreeTierMember(registry.EvaluateTeamTenant(TeamA, Member(TeamSeatRoles.Collaborator), Now));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Owner")]       // ordinal: the membership table writes lower case; anything else is unrecognised
    [InlineData("admin")]
    public void EvaluateTeamTenant_UnrecognisedRole_GetsNoPaidScopes(string? role)
    {
        var db = OpenWithBillingTables();
        SeedTeam(db, TeamA, EntitlementRegistry.StatusActive);
        var registry = new EntitlementRegistry(db, requireLivemode: false);

        AssertFreeTierMember(registry.EvaluateTeamTenant(TeamA, Member(role), Now));
    }

    // ---- In a team tenant, no membership means a refusal - never the person's own row --------------------------

    [Fact]
    public void EvaluateTeamTenant_NotAMember_IsRefusedAsAPersonEvenWithAPersonalProAndAPaidTeam()
    {
        // "Team tenant, no seat": the person has a personal Pro and the team's bill is active, but the person is
        // not a member here. The answer is a refusal of the PERSON, carrying no entitlement at all - not the
        // person's Pro, and not an entitlement verdict the lease could act on.
        var db = OpenWithBillingTables();
        SeedTeam(db, TeamA, EntitlementRegistry.StatusActive);
        SeedPersonal(db, OwnerSubject, EntitlementRegistry.StatusActive, EntitlementRegistry.TierPro);
        var registry = new EntitlementRegistry(db, requireLivemode: false);

        var decision = registry.EvaluateTeamTenant(TeamA, TeamMembership.NotAMember, Now);

        Assert.False(decision.IsMember);
        Assert.Null(decision.Entitlement);
        // Control: the personal Pro is real - it grants in the person's own tenant.
        Assert.True(HasPaidScopes(registry.EvaluatePersonalTenant(OwnerSubject, Now)));
    }

    [Fact]
    public void EvaluateTeamTenant_NoMembershipValue_Throws()
    {
        // A null cannot stand for "no membership" - that is what lets a forgotten lookup fall through to a personal
        // row. The caller must say NotAMember, or answer a failed lookup as Unknown itself.
        var db = OpenWithBillingTables();
        var registry = new EntitlementRegistry(db, requireLivemode: false);

        Assert.Throws<ArgumentNullException>(() => registry.EvaluateTeamTenant(TeamA, null!, Now));
    }

    // ---- The trial ledger is never consulted for a team --------------------------------------------------------

    [Fact]
    public void EvaluateTeamTenant_NoBill_IgnoresTheOwnersRunningTrial()
    {
        var db = OpenWithBillingTables();
        SeedRunningTrial(db, OwnerSubject);
        var registry = new EntitlementRegistry(db, requireLivemode: false, trials: new TrialRegistry(db));

        // Control: the trial is real and running - in the Owner's PERSONAL tenant it grants Pro.
        var personal = registry.EvaluatePersonalTenant(OwnerSubject, Now);
        Assert.Equal(EntitlementOutcome.Entitled, personal.Outcome);
        Assert.Equal(EntitlementRegistry.TierPro, personal.Tier);

        // In the team tenant, with no team bill, that same running trial grants no paid scopes.
        AssertFreeTierMember(registry.EvaluateTeamTenant(TeamA, Member(TeamSeatRoles.Owner), Now));
    }

    [Fact]
    public void EvaluateTeamTenant_TrialLedgerUnreadable_IsNeverReached()
    {
        // The stronger form: drop the trial ledger. A read that consulted it would come back Unknown; the team
        // decision answers from the team row alone, so it is unaffected in both directions.
        var db = OpenWithBillingTables();
        SeedTeam(db, TeamA, EntitlementRegistry.StatusActive);
        using (var ctx = db.CreateUnscopedContext())
            ctx.Database.ExecuteSqlRaw("DROP TABLE account_trials");
        var registry = new EntitlementRegistry(db, requireLivemode: false, trials: new TrialRegistry(db));

        AssertTeamSeatMember(registry.EvaluateTeamTenant(TeamA, Member(TeamSeatRoles.Owner), Now));
        AssertFreeTierMember(registry.EvaluateTeamTenant(TeamB, Member(TeamSeatRoles.Owner), Now));
        // Control: the PERSONAL read does consult the ledger, and so cannot answer.
        Assert.Equal(EntitlementOutcome.Unknown, registry.EvaluatePersonalTenant(OwnerSubject, Now).Outcome);
    }

    // ---- A team seat and a personal seat never carry over ------------------------------------------------------

    [Fact]
    public void EvaluatePersonalTenant_ActiveTeamBill_LeavesThePersonalAnswerUnchanged()
    {
        // States the property; it cannot fail against an implementation whose personal read never touches the
        // team table, which is the design.
        var db = OpenWithBillingTables();
        SeedTeam(db, TeamA, EntitlementRegistry.StatusActive);
        var registry = new EntitlementRegistry(db, requireLivemode: false);

        // The Owner of an active team, in their own personal tenant, with no personal row: not entitled.
        Assert.Equal(EntitlementOutcome.NotEntitled, registry.EvaluatePersonalTenant(OwnerSubject, Now).Outcome);
    }

    [Fact]
    public void EvaluateTeamTenant_PersonalProAndNoTeamBill_GetsNoPaidScopes()
    {
        var db = OpenWithBillingTables();
        SeedPersonal(db, OwnerSubject, EntitlementRegistry.StatusActive, EntitlementRegistry.TierPro);
        var registry = new EntitlementRegistry(db, requireLivemode: false);

        // Control: the personal Pro is valid in the personal tenant.
        Assert.True(HasPaidScopes(registry.EvaluatePersonalTenant(OwnerSubject, Now)));

        // In a team tenant whose bill has not started, that Pro does not carry over.
        AssertFreeTierMember(registry.EvaluateTeamTenant(TeamA, Member(TeamSeatRoles.Developer), Now));
    }

    [Fact]
    public void EvaluateTeamTenant_SeatOnOnePaidTeam_GrantsNoPaidScopesInAnotherTeamsTenant()
    {
        // The part of "a team seat gives paid features only on that team's Directors" provable before each
        // Director has its own key (#2311): the decision is keyed on the TENANT the request is in.
        var db = OpenWithBillingTables();
        SeedTeam(db, TeamA, EntitlementRegistry.StatusActive);
        var registry = new EntitlementRegistry(db, requireLivemode: false);

        AssertTeamSeatMember(registry.EvaluateTeamTenant(TeamA, Member(TeamSeatRoles.Developer), Now));
        AssertFreeTierMember(registry.EvaluateTeamTenant(TeamB, Member(TeamSeatRoles.Developer), Now));
        Assert.False(HasPaidScopes(registry.EvaluatePersonalTenant(OwnerSubject, Now)));
    }

    // ---- The live-money rule is the website's, not the Gateway's own bill's -----------------------------------------

    [Fact]
    public void EvaluateTeamTenant_ProductionHostedAndTheGatewaysOwnActiveBill_GrantsTheProScopes()
    {
        // The production hosted registry requires live money of a PERSONAL row the website writes. The team's bill is the
        // Gateway's own row, with no live-money flag, and it grants.
        var db = OpenWithBillingTables();
        SeedTeam(db, TeamA, EntitlementRegistry.StatusActive);
        var registry = new EntitlementRegistry(db, requireLivemode: true);

        Assert.Equal(EntitlementOutcome.Entitled, registry.EvaluateTeam(TeamA, Now).Outcome);
        AssertTeamSeatMember(registry.EvaluateTeamTenant(TeamA, Member(TeamSeatRoles.Owner), Now));
    }

    [Fact]
    public void EvaluateTeamTenant_ProductionHostedAndTheGatewaysOwnEndedBill_GetsNoPaidScopes()
    {
        // The control for the grant above: an ended bill on the same registry grants nothing.
        var db = OpenWithBillingTables();
        SeedTeam(db, TeamA, EntitlementRegistry.StatusCanceled);
        var registry = new EntitlementRegistry(db, requireLivemode: true);

        Assert.Equal(EntitlementOutcome.NotEntitled, registry.EvaluateTeam(TeamA, Now).Outcome);
        AssertFreeTierMember(registry.EvaluateTeamTenant(TeamA, Member(TeamSeatRoles.Owner), Now));
    }

    // ---- A failed read is Unknown ------------------------------------------------------------------------------

    [Fact]
    public void EvaluateTeamTenant_PaidSeatAndBillReadFails_IsUnknownNeverAGrantNeverARefusal()
    {
        // The read fails for real: the team bill table does not exist, which is what a lost grant or an un-migrated
        // database looks like from here.
        var db = _harness.Open();
        BreakTheBillTable(db);
        var registry = new EntitlementRegistry(db, requireLivemode: false);

        var decision = registry.EvaluateTeamTenant(TeamA, Member(TeamSeatRoles.Owner), Now);

        Assert.True(decision.IsMember);
        Assert.Equal(EntitlementOutcome.Unknown, decision.Entitlement!.Outcome);
        Assert.Null(decision.Entitlement.Tier);
    }

    // ---- The table is the website's: mapped, excluded from migrations ------------------------------------------

    /// <summary>The exclusion flag lives only on the design-time model - the one the migration tooling reads,
    /// which is exactly the model whose answer matters here.</summary>
    private static IEntityType DesignTimeEntity(GatewayDbContext ctx)
    {
        var entity = ctx.GetService<IDesignTimeModel>().Model.FindEntityType(typeof(TeamEntitlementEntity));
        Assert.NotNull(entity);
        return entity!;
    }

    [Fact]
    public void TeamEntitlementsMapping_UnderSqlite_IsExcludedFromMigrationsAndUnqualified()
    {
        var db = _harness.Open();
        using var ctx = db.CreateUnscopedContext();
        var entity = ctx.Model.FindEntityType(typeof(TeamEntitlementEntity));

        Assert.NotNull(entity);
        Assert.Equal("team_entitlements", entity!.GetTableName());
        Assert.True(DesignTimeEntity(ctx).IsTableExcludedFromMigrations());
        Assert.Null(entity.GetSchema());
        // The Gateway's own migrations never create it: a freshly migrated database has no such table, which is
        // why the read-failure tests above can rely on its absence.
        Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() => ctx.TeamEntitlements.AsNoTracking().Count());
    }

    [Fact]
    public void TeamEntitlementsMapping_UnderPostgres_IsGatewaySchemaQualifiedAndExcludedFromMigrations()
    {
        var options = new DbContextOptionsBuilder<GatewayDbContext>()
            .UseNpgsql("Host=localhost;Database=unused;Username=u;Password=p")
            .Options;
        using var ctx = new GatewayDbContext(options) { ActiveTenant = Core.Tenancy.TenantId.Local.Value };
        var entity = ctx.Model.FindEntityType(typeof(TeamEntitlementEntity));

        Assert.NotNull(entity);
        Assert.Equal("gateway", entity!.GetSchema());
        Assert.True(DesignTimeEntity(ctx).IsTableExcludedFromMigrations());
        var sql = ctx.TeamEntitlements.AsNoTracking().Where(e => e.TeamId == TeamA).ToQueryString();
        Assert.Contains("gateway.team_entitlements", sql);
    }
}
