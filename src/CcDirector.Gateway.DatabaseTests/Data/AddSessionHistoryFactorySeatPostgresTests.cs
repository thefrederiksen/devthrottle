using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Xunit;

namespace CcDirector.Gateway.Tests.Data;

/// <summary>
/// The PostgreSQL half of the seat's birth facts on the session history row (Factory Definition mission, phase 5):
/// <c>AddSessionHistoryFactorySeat</c> applies to a real PostgreSQL database right after
/// <c>AddBriefVersionKindToCronRuns</c>, adds the four nullable columns without touching an existing row, takes a
/// seat's values, and its Down removes the four columns again.
///
/// GATING. [RequiresPostgresFact], the one rule for this project: it runs under scripts\test-database.ps1,
/// against the throwaway PostgreSQL that run built, and reports SKIPPED anywhere else. Skipped is not passed.
/// </summary>
public sealed class AddSessionHistoryFactorySeatPostgresTests
{
    private const string MigrationBefore = "20261015142302_AddBriefVersionKindToCronRuns";
    private const string MigrationUnderTest = "20261015161336_AddSessionHistoryFactorySeat";

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

    private static string SeatColumnsPresent() => Scalar("""
        SELECT count(*) FROM information_schema.columns
        WHERE table_schema = 'gateway' AND table_name = 'session_history'
          AND column_name IN ('FactorySeat', 'FactoryDefinitionVersion', 'FactoryKit', 'FactoryStartOverride')
        """)!;

    [RequiresPostgresFact]
    public void AddSessionHistoryFactorySeat_AppliesOnPostgres_LeavesOldRowsAlone_TakesASeat_AndItsDownRemovesIt()
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
        // A session history row written before the migration: a session of an undefined factory.
        Scalar("""
            INSERT INTO gateway.session_history (tenant_id, "SessionId", "DirectorId", "StartedAtUtc", "LastSeenUtc",
                "SummaryAttempts", "SummaryIsPartial", "Factory")
            VALUES ('tenant-1', 's-old', 'd-1', TIMESTAMPTZ '2026-10-01 10:00:00Z', TIMESTAMPTZ '2026-10-01 11:00:00Z', 0, false,
                'website-factory');
            """);
        Assert.Equal("0", SeatColumnsPresent());

        using (var ctx = NewContext())
        {
            ctx.GetService<IMigrator>().Migrate(MigrationUnderTest);
            Assert.Equal(MigrationUnderTest, ctx.Database.GetAppliedMigrations().Last());
        }
        Assert.Equal("4", SeatColumnsPresent());
        Assert.Equal("null|null|null|null|website-factory", Scalar("""
            SELECT concat_ws('|', coalesce("FactorySeat", 'null'), coalesce("FactoryDefinitionVersion"::text, 'null'),
                coalesce("FactoryKit", 'null'), coalesce("FactoryStartOverride", 'null'), "Factory")
            FROM gateway.session_history WHERE "SessionId" = 's-old'
            """));

        Scalar("""
            INSERT INTO gateway.session_history (tenant_id, "SessionId", "DirectorId", "StartedAtUtc", "LastSeenUtc",
                "SummaryAttempts", "SummaryIsPartial", "Factory", "FactorySeat", "FactoryDefinitionVersion", "FactoryKit",
                "FactoryStartOverride")
            VALUES ('tenant-1', 's-seat', 'd-1', TIMESTAMPTZ '2026-10-15 10:00:00Z', TIMESTAMPTZ '2026-10-15 11:00:00Z', 0, false,
                'website-factory', 'outreach', 4, 'seat', 'agent Codex, chosen by the person');
            """);
        Assert.Equal("outreach|4|seat|agent Codex, chosen by the person", Scalar("""
            SELECT concat_ws('|', "FactorySeat", "FactoryDefinitionVersion"::text, "FactoryKit", "FactoryStartOverride")
            FROM gateway.session_history WHERE "SessionId" = 's-seat'
            """));

        // The reversal: back to AddBriefVersionKindToCronRuns removes the four columns, and both rows survive.
        using (var ctx = NewContext())
        {
            ctx.GetService<IMigrator>().Migrate(MigrationBefore);
            Assert.Equal(MigrationBefore, ctx.Database.GetAppliedMigrations().Last());
        }
        Assert.Equal("0", SeatColumnsPresent());
        Assert.Equal("2", Scalar("""SELECT count(*) FROM gateway.session_history WHERE "SessionId" IN ('s-old', 's-seat')"""));

        using (var ctx = NewContext())
            ctx.Database.Migrate();
        Assert.Equal("4", SeatColumnsPresent());
    }
}
