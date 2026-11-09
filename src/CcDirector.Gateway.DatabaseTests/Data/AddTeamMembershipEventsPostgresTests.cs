using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.DevReports;
using CcDirector.Gateway.Teams;
using CcDirector.Gateway.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Xunit;

namespace CcDirector.Gateway.Tests.Data;

/// <summary>
/// The PostgreSQL half of Team implementation piece 1.5 (devthrottle_internal#2789): <c>AddTeamMembershipEvents</c>
/// applies right after <c>AddDevReportItemAnswererRole</c>; then the real <see cref="TeamRegistry"/>, over
/// <see cref="GatewayDatabase"/> on PostgreSQL, writes one membership record per change with the roles stored by name, and
/// removing a member deletes their recipient rows in the team - read past the tenant filter - in the same save, while
/// another member's row stays. The migration's Down removes the table again.
///
/// GATING. [RequiresPostgresFact], the one rule for this project: it runs under scripts\test-database.ps1, against the
/// throwaway PostgreSQL that run built, and reports SKIPPED anywhere else. Skipped is not passed.
///
/// It sets the runtime selector <c>CC_GATEWAY_DB_CONNECTION</c> to drive GatewayDatabase down its real PostgreSQL path,
/// so it joins the collection that never runs alongside another reader of that variable.
/// </summary>
[Collection("GatewayDatabase provider env var")]
public sealed class AddTeamMembershipEventsPostgresTests
{
    private const string RuntimeConnectionEnvVar = "CC_GATEWAY_DB_CONNECTION";
    private const string MigrationBefore = "20261109063046_AddDevReportItemAnswererRole";
    private const string MigrationUnderTest = "20261109081340_AddTeamMembershipEvents";

    private static string Connection => PostgresProofDatabase.Connection;

    private static GatewayDbContext NewContext()
    {
        var options = new DbContextOptionsBuilder<GatewayDbContext>()
            .UseNpgsql(Connection, npg =>
            {
                npg.MigrationsAssembly("CcDirector.Gateway.Migrations.Postgres");
                npg.MigrationsHistoryTable("__EFMigrationsHistory", "gateway");
            })
            .Options;
        return new GatewayDbContext(options) { ActiveTenant = TenantId.Local.Value };
    }

    private static string? Scalar(string sql)
    {
        using var connection = new NpgsqlConnection(Connection);
        connection.Open();
        using var command = new NpgsqlCommand(sql, connection);
        return command.ExecuteScalar()?.ToString();
    }

    private static string TablePresent() => Scalar("""
        SELECT count(*) FROM information_schema.tables WHERE table_schema = 'gateway' AND table_name = 'team_membership_events'
        """)!;

    [RequiresPostgresFact]
    public void AddTeamMembershipEvents_AppliesOnPostgres_RecordsEachChange_RemovalClearsWhatWasSent_AndItsDownRemovesIt()
    {
        PostgresProofDatabase.GuardThrowawayDatabase();
        using (var ctx = NewContext())
            ctx.Database.EnsureDeleted();

        using (var ctx = NewContext())
        {
            var all = ctx.Database.GetMigrations().ToList();
            Assert.Equal(MigrationBefore, all[all.IndexOf(MigrationUnderTest) - 1]);
            ctx.GetService<IMigrator>().Migrate(MigrationBefore);
        }
        Assert.Equal("0", TablePresent());

        using (var ctx = NewContext())
        {
            ctx.GetService<IMigrator>().Migrate(MigrationUnderTest);
            Assert.Equal(MigrationUnderTest, ctx.Database.GetAppliedMigrations().Last());
            ctx.GetService<IMigrator>().Migrate();
            Assert.False(ctx.Database.HasPendingModelChanges());
        }
        Assert.Equal("1", TablePresent());

        var priorRuntimeConn = Environment.GetEnvironmentVariable(RuntimeConnectionEnvVar);
        try
        {
            Environment.SetEnvironmentVariable(RuntimeConnectionEnvVar, Connection);
            using var db = new GatewayDatabase(new SingleTenantContext());
            var tenants = new TenantRegistry(db);
            foreach (var (subject, email) in new[] { ("sub-owner", "owner@example.com"), ("sub-mike", "mike@example.com"), ("sub-nina", "nina@example.com") })
                tenants.MintOrLookupBySubject(subject, email);
            var now = new DateTime(2026, 11, 9, 9, 0, 0, DateTimeKind.Utc);
            var teams = new TeamRegistry(db, tenants, () => now);
            var team = teams.CreateTeam("sub-owner", "Acme").Team!.TeamId;
            Assert.True(teams.AddMember(team, "sub-mike", TeamRole.Collaborator, "sub-owner").IsDone);
            Assert.True(teams.AddMember(team, "sub-nina", TeamRole.Collaborator, "sub-owner").IsDone);

            var tenant = new TenantId(team);
            var store = new DevReportStore(db);
            var report = store.Publish(tenant, Guid.NewGuid().ToString("D"), @"C:\work\pricing.html",
                "<header data-dev-report=\"header\" data-dev-report-status=\"waiting-on-you\"><h1>T</h1></header>" +
                "<section data-dev-report=\"summary\"><p>S</p></section>" +
                "<section data-dev-report=\"questions\"><p data-dev-report-no-questions>No questions.</p></section>" +
                "<section data-dev-report=\"detail\"><p>D</p></section>",
                "waiting-on-you", "Pricing", now, "sub-owner").Report;
            var recipients = new DevReportRecipients(db);
            Assert.True(recipients.Send(tenant, report.Id, "sub-owner", new[] { "sub-mike", "sub-nina" }, 1, now).Sent);

            now = now.AddHours(1);
            Assert.Equal(TeamMemberChangeOutcome.Done, teams.RemoveTeamMember(team, "sub-owner", TeamMemberIds.For(team, "sub-mike")).Outcome);

            Assert.Null(recipients.RowFor(tenant, report.Id, "sub-mike"));
            Assert.NotNull(recipients.RowFor(tenant, report.Id, "sub-nina"));
            Assert.Equal("4", Scalar($"SELECT count(*) FROM gateway.team_membership_events WHERE team_id = '{team}'"));
            Assert.Equal("removed|collaborator||sub-owner|2026-11-09 10:00:00", Scalar($"""
                SELECT event || '|' || coalesce(from_role, '') || '|' || coalesce(to_role, '') || '|' || by_subject || '|' ||
                       to_char(at_utc AT TIME ZONE 'UTC', 'YYYY-MM-DD HH24:MI:SS')
                FROM gateway.team_membership_events WHERE team_id = '{team}' AND account_subject = 'sub-mike' AND event = 'removed'
                """));
        }
        finally
        {
            Environment.SetEnvironmentVariable(RuntimeConnectionEnvVar, priorRuntimeConn);
        }

        // The reversal: back to AddDevReportItemAnswererRole removes the table, and forward again restores it.
        using (var ctx = NewContext())
        {
            ctx.GetService<IMigrator>().Migrate(MigrationBefore);
            Assert.Equal(MigrationBefore, ctx.Database.GetAppliedMigrations().Last());
        }
        Assert.Equal("0", TablePresent());

        using (var ctx = NewContext())
            ctx.Database.Migrate();
        Assert.Equal("1", TablePresent());
    }
}
