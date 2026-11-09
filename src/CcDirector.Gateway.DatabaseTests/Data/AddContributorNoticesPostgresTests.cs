using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Xunit;

namespace CcDirector.Gateway.Tests.Data;

/// <summary>
/// The PostgreSQL half of the record of what a contributor has not been told about yet, and of each person's choice
/// (Team implementation piece 4.4): <c>AddContributorNotices</c> applies to a real PostgreSQL database right after
/// <c>AddFactoryContributors</c>, adds the two tables, keeps a row of each through the model, refuses a second choice for
/// one person, and its Down removes both again.
///
/// GATING. [RequiresPostgresFact], the one rule for this project: it runs under scripts\test-database.ps1, against the
/// throwaway PostgreSQL that run built, and reports SKIPPED anywhere else. Skipped is not passed.
/// </summary>
public sealed class AddContributorNoticesPostgresTests
{
    private const string MigrationBefore = "20261109122447_AddFactoryContributors";
    private const string MigrationUnderTest = "20261109230937_AddContributorNotices";

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

    private static string TablesPresent()
    {
        using var connection = new NpgsqlConnection(Connection);
        connection.Open();
        using var command = new NpgsqlCommand(
            "SELECT count(*) FROM information_schema.tables WHERE table_schema = 'gateway' " +
            "AND table_name IN ('contributor_notices', 'contributor_notice_choices')", connection);
        return command.ExecuteScalar()!.ToString()!;
    }

    private static ContributorNoticeChoiceEntity Choice() => new()
    {
        TenantId = TenantId.Local.Value,
        Mode = "daily",
        DailyHour = 8,
        UpdatedAtUtc = new DateTime(2026, 11, 9, 10, 0, 0, DateTimeKind.Utc),
    };

    [RequiresPostgresFact]
    public void AddContributorNotices_AppliesOnPostgres_KeepsRows_RefusesASecondChoice_AndItsDownRemovesThem()
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
        Assert.Equal("0", TablesPresent());

        using (var ctx = NewContext())
        {
            ctx.GetService<IMigrator>().Migrate(MigrationUnderTest);
            Assert.Equal(MigrationUnderTest, ctx.Database.GetAppliedMigrations().Last());
        }
        Assert.Equal("2", TablesPresent());
        using (var ctx = NewContext())
        {
            ctx.GetService<IMigrator>().Migrate();
            Assert.False(ctx.Database.HasPendingModelChanges());
        }

        using (var ctx = NewContext())
        {
            ctx.ContributorNotices.Add(new ContributorNoticeEntity
            {
                TenantId = TenantId.Local.Value,
                RecipientSubject = "sub-mike",
                Kind = "report",
                CreatedAtUtc = new DateTime(2026, 11, 9, 10, 0, 0, DateTimeKind.Utc),
            });
            ctx.ContributorNoticeChoices.Add(Choice());
            ctx.SaveChanges();
        }
        using (var ctx = NewContext())
        {
            Assert.Equal("sub-mike", ctx.ContributorNotices.AsNoTracking().Single().RecipientSubject);
            Assert.Equal("daily", ctx.ContributorNoticeChoices.AsNoTracking().Single().Mode);
        }
        using (var ctx = NewContext())
        {
            ctx.ContributorNoticeChoices.Add(Choice());
            Assert.Throws<DbUpdateException>(() => ctx.SaveChanges());
        }

        using (var ctx = NewContext())
        {
            ctx.GetService<IMigrator>().Migrate(MigrationBefore);
            Assert.Equal(MigrationBefore, ctx.Database.GetAppliedMigrations().Last());
        }
        Assert.Equal("0", TablesPresent());

        using (var ctx = NewContext())
            ctx.Database.Migrate();
        Assert.Equal("2", TablesPresent());
    }
}
