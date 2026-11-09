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
/// The PostgreSQL half of the record of which factory was sent to which contributor (Team implementation piece 4.1):
/// <c>AddFactoryContributors</c> applies to a real PostgreSQL database right after <c>AddDevReportItemAnswererRole</c>,
/// adds the one table, keeps a row through the model, refuses a second row for the same factory and contributor, and its
/// Down removes the table again.
///
/// GATING. [RequiresPostgresFact], the one rule for this project: it runs under scripts\test-database.ps1, against the
/// throwaway PostgreSQL that run built, and reports SKIPPED anywhere else. Skipped is not passed.
/// </summary>
public sealed class AddFactoryContributorsPostgresTests
{
    private const string MigrationBefore = "20261109063046_AddDevReportItemAnswererRole";
    private const string MigrationUnderTest = "20261109103904_AddFactoryContributors";

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
        SELECT count(*) FROM information_schema.tables
        WHERE table_schema = 'gateway' AND table_name = 'factory_contributors'
        """)!;

    private static FactoryContributorEntity Row() => new()
    {
        TenantId = TenantId.Local.Value,
        Factory = "website",
        ContributorSubject = "sub-mike",
        SharedBySubject = "sub-owner",
        SharedAtUtc = new DateTime(2026, 11, 9, 10, 0, 0, DateTimeKind.Utc),
    };

    [RequiresPostgresFact]
    public void AddFactoryContributors_AppliesOnPostgres_KeepsARow_RefusesADuplicate_AndItsDownRemovesIt()
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
        }
        Assert.Equal("1", TablePresent());

        using (var ctx = NewContext())
        {
            ctx.FactoryContributors.Add(Row());
            ctx.SaveChanges();
        }
        using (var ctx = NewContext())
            Assert.Equal("sub-mike", ctx.FactoryContributors.AsNoTracking().Single().ContributorSubject);
        using (var ctx = NewContext())
        {
            ctx.FactoryContributors.Add(Row());
            Assert.Throws<DbUpdateException>(() => ctx.SaveChanges());
        }

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
