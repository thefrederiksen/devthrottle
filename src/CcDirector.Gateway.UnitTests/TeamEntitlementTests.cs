using System;
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
/// The Gateway half of devthrottle_internal #2299: a TEAM's bill, read from the team row the website's payment
/// side writes, decides whether the team's paid members hold paid features.
///
/// What these tests hold, in the words of the owner's decisions (3 Oct 2026):
///  - Every paid member (Owner, Manager, Developer) of a team whose bill is active gets paid features; none do
///    when the bill is canceled or does not exist. A Collaborator never does.
///  - "Nothing stops for anyone" on a failed payment: past_due grants, with no period cut-off.
///  - "We do not do trials for teams": the trial ledger is never consulted for a team.
///  - "A personal seat and a team seat is different": neither carries over to the other.
///  - Live money only on production hosted, and a failed read is Unknown - never a grant, never a refusal.
///
/// The team table is created here with raw SQL, not by a migration, for the same reason production does not
/// migrate it: the website owns it. Creating it here also states the columns this code reads, so a contract
/// drift shows up as a failing test.
/// </summary>
public sealed class TeamEntitlementTests : IDisposable
{
    private const string TeamA = "7f0c2a52-6f1e-4d7e-9b7a-0a1b2c3d4e5f";
    private const string TeamB = "1b2c3d4e-0000-4d7e-9b7a-aaaaaaaaaaaa";
    private const string OwnerSubject = "35543491-85cb-468d-a0c9-560193683105";

    private static readonly DateTime Now = new(2026, 10, 3, 12, 0, 0, DateTimeKind.Utc);

    private readonly GatewayDbTestHarness _harness = new();

    public void Dispose() => _harness.Dispose();

    /// <summary>Opens a database with BOTH payment-side tables the Gateway reads (personal and team), empty.</summary>
    private GatewayDatabase OpenWithBillingTables()
    {
        var db = _harness.Open();
        using var ctx = db.CreateUnscopedContext();
        ctx.Database.ExecuteSqlRaw(
            "CREATE TABLE IF NOT EXISTS entitlements (" +
            "subject TEXT NOT NULL PRIMARY KEY, status TEXT NOT NULL, " +
            "current_period_end TEXT NULL, stripe_subscription_id TEXT NULL, updated_at TEXT NULL, " +
            "livemode INTEGER NULL, tier TEXT NULL)");
        ctx.Database.ExecuteSqlRaw(
            "CREATE TABLE IF NOT EXISTS team_entitlements (" +
            "team_id TEXT NOT NULL PRIMARY KEY, status TEXT NOT NULL, seats INTEGER NULL, " +
            "current_period_end TEXT NULL, stripe_subscription_id TEXT NULL, livemode INTEGER NULL, updated_at TEXT NULL)");
        return db;
    }

    private static void SeedTeam(GatewayDatabase db, string teamId, string status, DateTime? periodEnd = null,
        bool? livemode = true, int? seats = 1)
    {
        using var ctx = db.CreateUnscopedContext();
        ctx.Database.ExecuteSqlRaw(
            "INSERT INTO team_entitlements (team_id, status, seats, current_period_end, livemode) VALUES ({0}, {1}, {2}, {3}, {4})",
            teamId, status, seats, periodEnd, livemode);
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

    private static bool HasPaidScopes(EntitlementDecision d) =>
        d.Outcome == EntitlementOutcome.Entitled
        && EntitlementScopes.Grants(d.Tier, EntitlementScopes.Wingman)
        && EntitlementScopes.Grants(d.Tier, EntitlementScopes.Dictation)
        && EntitlementScopes.Grants(d.Tier, EntitlementScopes.Tts);

    // ---- An active bill grants every paid member; canceled or absent grants none -------------------------------

    [Theory]
    [InlineData(TeamSeatRoles.Owner)]
    [InlineData(TeamSeatRoles.Manager)]
    [InlineData(TeamSeatRoles.Developer)]
    public void EvaluateTeamMember_ActiveBillAndPaidRole_GrantsTheProScopes(string role)
    {
        var db = OpenWithBillingTables();
        SeedTeam(db, TeamA, EntitlementRegistry.StatusActive);
        var registry = new EntitlementRegistry(db, requireLivemode: false);

        var decision = registry.EvaluateTeamMember(TeamA, role, Now);

        Assert.Equal(EntitlementOutcome.Entitled, decision.Outcome);
        Assert.Equal(EntitlementRegistry.TierTeam, decision.Tier);
        // A team seat grants exactly what Pro grants - stated against the table, not hard-coded here.
        Assert.Equal(EntitlementScopes.ForTier(EntitlementRegistry.TierPro), EntitlementScopes.ForTier(decision.Tier));
        Assert.True(HasPaidScopes(decision));
    }

    [Theory]
    [InlineData(TeamSeatRoles.Owner)]
    [InlineData(TeamSeatRoles.Manager)]
    [InlineData(TeamSeatRoles.Developer)]
    public void EvaluateTeamMember_CanceledBill_GrantsNothing(string role)
    {
        var db = OpenWithBillingTables();
        SeedTeam(db, TeamA, "canceled");
        var registry = new EntitlementRegistry(db, requireLivemode: false);

        var decision = registry.EvaluateTeamMember(TeamA, role, Now);

        Assert.Equal(EntitlementOutcome.NotEntitled, decision.Outcome);
        Assert.False(HasPaidScopes(decision));
    }

    [Theory]
    [InlineData(TeamSeatRoles.Owner)]
    [InlineData(TeamSeatRoles.Manager)]
    [InlineData(TeamSeatRoles.Developer)]
    public void EvaluateTeamMember_NoBillRow_GrantsNothing(string role)
    {
        var db = OpenWithBillingTables();
        var registry = new EntitlementRegistry(db, requireLivemode: false);

        var decision = registry.EvaluateTeamMember(TeamA, role, Now);

        Assert.Equal(EntitlementOutcome.NotEntitled, decision.Outcome);
        Assert.Null(decision.Tier);
    }

    [Fact]
    public void EvaluateTeam_UnrecognisedStatus_IsNotEntitled()
    {
        var db = OpenWithBillingTables();
        SeedTeam(db, TeamA, "incomplete");
        var registry = new EntitlementRegistry(db, requireLivemode: false);

        Assert.Equal(EntitlementOutcome.NotEntitled, registry.EvaluateTeam(TeamA, Now).Outcome);
    }

    // ---- A 100%-discount subscription is an active row and grants exactly as a full-price one ------------------

    [Fact]
    public void EvaluateTeamMember_FullyDiscountedActiveBill_GrantsExactlyAsAFullPriceOne()
    {
        // The discount lives on the payment provider's subscription; what reaches this table is an `active`
        // row like any other. Two teams, one "discounted" and one "full price", read identically - there is no
        // column the Gateway could tell them apart by, and that is the property.
        var db = OpenWithBillingTables();
        SeedTeam(db, TeamA, EntitlementRegistry.StatusActive, periodEnd: Now.AddDays(20), seats: 3);   // full price
        SeedTeam(db, TeamB, EntitlementRegistry.StatusActive, periodEnd: Now.AddDays(20), seats: 3);   // 100% coupon
        var registry = new EntitlementRegistry(db, requireLivemode: false);

        var fullPrice = registry.EvaluateTeamMember(TeamA, TeamSeatRoles.Developer, Now);
        var discounted = registry.EvaluateTeamMember(TeamB, TeamSeatRoles.Developer, Now);

        Assert.Equal(fullPrice, discounted);
        Assert.True(HasPaidScopes(discounted));
    }

    // ---- A failed payment changes no member's access -----------------------------------------------------------

    [Theory]
    [InlineData(TeamSeatRoles.Owner)]
    [InlineData(TeamSeatRoles.Manager)]
    [InlineData(TeamSeatRoles.Developer)]
    public void EvaluateTeamMember_PastDueLongAfterThePeriodEnded_StillGrants(string role)
    {
        // The personal row's past-due grace ends at the period end; a team's does not (owner, 3 Oct 2026:
        // "Nothing stops for anyone"). A year past the period end is still entitled.
        var db = OpenWithBillingTables();
        SeedTeam(db, TeamA, EntitlementRegistry.StatusPastDue, periodEnd: Now.AddDays(-365));
        var registry = new EntitlementRegistry(db, requireLivemode: false);

        var decision = registry.EvaluateTeamMember(TeamA, role, Now);

        Assert.True(HasPaidScopes(decision));
        // No boundary is handed to the access lease, because there is none.
        Assert.Null(decision.CurrentPeriodEnd);
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
        // The control for the test above: the PERSONAL policy is unchanged by the team one. Same state, same
        // dates, personal row -> refused.
        var db = OpenWithBillingTables();
        using (var ctx = db.CreateUnscopedContext())
        {
            ctx.Database.ExecuteSqlRaw(
                "INSERT INTO entitlements (subject, status, current_period_end, livemode, tier) VALUES ({0}, {1}, {2}, {3}, {4})",
                OwnerSubject, EntitlementRegistry.StatusPastDue, Now.AddDays(-365), true, EntitlementRegistry.TierPro);
        }
        var registry = new EntitlementRegistry(db, requireLivemode: false);

        Assert.Equal(EntitlementOutcome.NotEntitled, registry.Evaluate(OwnerSubject, Now).Outcome);
    }

    // ---- A Collaborator gets no paid scopes whatever the bill --------------------------------------------------

    [Theory]
    [InlineData(EntitlementRegistry.StatusActive)]
    [InlineData(EntitlementRegistry.StatusPastDue)]
    [InlineData("canceled")]
    public void EvaluateTeamMember_Collaborator_GetsNoPaidScopesWhateverTheBill(string status)
    {
        var db = OpenWithBillingTables();
        SeedTeam(db, TeamA, status);
        var registry = new EntitlementRegistry(db, requireLivemode: false);

        var decision = registry.EvaluateTeamMember(TeamA, TeamSeatRoles.Collaborator, Now);

        Assert.Equal(EntitlementOutcome.NotEntitled, decision.Outcome);
        Assert.Null(decision.Tier);
        Assert.False(HasPaidScopes(decision));
    }

    [Fact]
    public void EvaluateTeamMember_CollaboratorWhenTheBillCannotBeRead_IsStillNotEntitled()
    {
        // The role decides a Collaborator's answer, so a failed bill read does not turn it into an Unknown.
        var db = _harness.Open();   // no team_entitlements table at all
        var registry = new EntitlementRegistry(db, requireLivemode: false);

        Assert.Equal(EntitlementOutcome.NotEntitled,
            registry.EvaluateTeamMember(TeamA, TeamSeatRoles.Collaborator, Now).Outcome);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Owner")]       // ordinal: the membership table writes lower case; anything else is unrecognised
    [InlineData("admin")]
    public void EvaluateTeamMember_UnrecognisedRole_GetsNoPaidScopes(string? role)
    {
        var db = OpenWithBillingTables();
        SeedTeam(db, TeamA, EntitlementRegistry.StatusActive);
        var registry = new EntitlementRegistry(db, requireLivemode: false);

        Assert.Equal(EntitlementOutcome.NotEntitled, registry.EvaluateTeamMember(TeamA, role, Now).Outcome);
    }

    // ---- The trial ledger is never consulted for a team --------------------------------------------------------

    [Fact]
    public void EvaluatePerson_TeamTenantWithNoBill_IgnoresTheOwnersRunningTrial()
    {
        var db = OpenWithBillingTables();
        SeedRunningTrial(db, OwnerSubject);
        var registry = new EntitlementRegistry(db, requireLivemode: false, trials: new TrialRegistry(db));

        // Control: the trial is real and running - in the Owner's PERSONAL tenant it grants Pro.
        var personal = registry.EvaluatePerson(OwnerSubject, teamSeat: null, Now);
        Assert.Equal(EntitlementOutcome.Entitled, personal.Outcome);
        Assert.Equal(EntitlementRegistry.TierPro, personal.Tier);

        // In the team tenant, with no team bill, that same running trial grants nothing.
        var team = registry.EvaluatePerson(OwnerSubject, new TeamSeat(TeamA, TeamSeatRoles.Owner), Now);
        Assert.Equal(EntitlementOutcome.NotEntitled, team.Outcome);
        Assert.False(HasPaidScopes(team));
    }

    [Fact]
    public void EvaluateTeam_TrialLedgerUnreadable_IsNeverReached()
    {
        // The stronger form: drop the trial ledger. A read that consulted it would come back Unknown; the team
        // read answers from the team row alone, so it is unaffected in both directions.
        var db = OpenWithBillingTables();
        SeedTeam(db, TeamA, EntitlementRegistry.StatusActive);
        using (var ctx = db.CreateUnscopedContext())
            ctx.Database.ExecuteSqlRaw("DROP TABLE account_trials");
        var registry = new EntitlementRegistry(db, requireLivemode: false, trials: new TrialRegistry(db));

        Assert.Equal(EntitlementOutcome.Entitled,
            registry.EvaluatePerson(OwnerSubject, new TeamSeat(TeamA, TeamSeatRoles.Owner), Now).Outcome);
        Assert.Equal(EntitlementOutcome.NotEntitled,
            registry.EvaluatePerson(OwnerSubject, new TeamSeat(TeamB, TeamSeatRoles.Owner), Now).Outcome);
        // Control: the PERSONAL read does consult the ledger, and so cannot answer.
        Assert.Equal(EntitlementOutcome.Unknown, registry.EvaluatePerson(OwnerSubject, null, Now).Outcome);
    }

    // ---- A team seat and a personal seat never carry over ------------------------------------------------------

    [Fact]
    public void EvaluatePerson_PersonalTenant_IsUnchangedByAnActiveTeamBill()
    {
        var db = OpenWithBillingTables();
        SeedTeam(db, TeamA, EntitlementRegistry.StatusActive);
        var registry = new EntitlementRegistry(db, requireLivemode: false);

        // The Owner of an active team, in their own personal tenant, with no personal row: not entitled.
        var personal = registry.EvaluatePerson(OwnerSubject, teamSeat: null, Now);

        Assert.Equal(EntitlementOutcome.NotEntitled, personal.Outcome);
    }

    [Fact]
    public void EvaluatePerson_PersonalProInATeamTenantWithNoBill_GrantsNothing()
    {
        var db = OpenWithBillingTables();
        SeedPersonal(db, OwnerSubject, EntitlementRegistry.StatusActive, EntitlementRegistry.TierPro);
        var registry = new EntitlementRegistry(db, requireLivemode: false);

        // Control: the personal Pro is valid in the personal tenant.
        Assert.True(HasPaidScopes(registry.EvaluatePerson(OwnerSubject, null, Now)));

        // In a team tenant whose bill has not started, that Pro does not carry over.
        var team = registry.EvaluatePerson(OwnerSubject, new TeamSeat(TeamA, TeamSeatRoles.Developer), Now);
        Assert.Equal(EntitlementOutcome.NotEntitled, team.Outcome);
        Assert.False(HasPaidScopes(team));
    }

    [Fact]
    public void EvaluatePerson_SeatOnOnePaidTeam_GrantsNothingInAnotherTeamsTenant()
    {
        // The part of "a team seat gives paid features only on that team's Directors" provable before each
        // Director has its own key (#2311): the decision is keyed on the TENANT the request is in.
        var db = OpenWithBillingTables();
        SeedTeam(db, TeamA, EntitlementRegistry.StatusActive);
        var registry = new EntitlementRegistry(db, requireLivemode: false);

        Assert.True(HasPaidScopes(registry.EvaluatePerson(OwnerSubject, new TeamSeat(TeamA, TeamSeatRoles.Developer), Now)));
        Assert.False(HasPaidScopes(registry.EvaluatePerson(OwnerSubject, new TeamSeat(TeamB, TeamSeatRoles.Developer), Now)));
        Assert.False(HasPaidScopes(registry.EvaluatePerson(OwnerSubject, null, Now)));
    }

    // ---- Live money only on production hosted ------------------------------------------------------------------

    [Theory]
    [InlineData(false)]
    [InlineData(null)]
    public void EvaluateTeam_ProductionHostedAndTestModeRow_GrantsNothing(bool? livemode)
    {
        var db = OpenWithBillingTables();
        SeedTeam(db, TeamA, EntitlementRegistry.StatusActive, livemode: livemode);
        var registry = new EntitlementRegistry(db, requireLivemode: true);

        var decision = registry.EvaluateTeamMember(TeamA, TeamSeatRoles.Owner, Now);

        Assert.Equal(EntitlementOutcome.NotEntitled, decision.Outcome);
        Assert.False(HasPaidScopes(decision));
    }

    [Fact]
    public void EvaluateTeam_ProductionHostedAndLiveRow_Grants()
    {
        // The control for the refusal above: without it, "test mode grants nothing" would also hold if the
        // live-money check refused everything.
        var db = OpenWithBillingTables();
        SeedTeam(db, TeamA, EntitlementRegistry.StatusActive, livemode: true);
        var registry = new EntitlementRegistry(db, requireLivemode: true);

        Assert.True(HasPaidScopes(registry.EvaluateTeamMember(TeamA, TeamSeatRoles.Owner, Now)));
    }

    // ---- A failed read is Unknown ------------------------------------------------------------------------------

    [Fact]
    public void EvaluateTeam_ReadFails_IsUnknownNeverAGrantNeverARefusal()
    {
        // The read fails for real: the website-owned table does not exist, which is what a lost SELECT grant or
        // an un-migrated website database looks like from here.
        var db = _harness.Open();
        var registry = new EntitlementRegistry(db, requireLivemode: false);

        var decision = registry.EvaluateTeamMember(TeamA, TeamSeatRoles.Owner, Now);

        Assert.Equal(EntitlementOutcome.Unknown, decision.Outcome);
        Assert.Null(decision.Tier);
    }

    // ---- The table is the website's: mapped, excluded from migrations ------------------------------------------

    /// <summary>The exclusion flag lives only on the design-time model - the one the migration tooling reads,
    /// which is exactly the model whose answer matters here.</summary>
    private static Microsoft.EntityFrameworkCore.Metadata.IEntityType DesignTimeEntity(GatewayDbContext ctx)
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
        // why the read-failure test above can rely on its absence.
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
