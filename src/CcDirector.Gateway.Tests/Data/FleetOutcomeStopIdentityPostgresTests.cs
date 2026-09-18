using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Xunit;

namespace CcDirector.Gateway.Tests.Data;

/// <summary>
/// The PostgreSQL half of the round 2 fixes' storage: a real PostgreSQL database at the schema before the change,
/// holding an open record filed then, carried through <c>AddFleetOutcomeStopIdentity</c>. The record survives, naming no
/// stop, and the two new columns exist.
///
/// GATING. Like <see cref="TurnVerdictAnswerChoicePostgresTests"/>, the class is gated on
/// <c>CC_GATEWAY_TEST_PG_CONNECTION</c> and reports SKIPPED when it is unset. Skipped is not passed.
/// </summary>
public sealed class FleetOutcomeStopIdentityPostgresTests
{
    private const string ConnectionEnvVar = "CC_GATEWAY_TEST_PG_CONNECTION";
    private const string MigrationBefore = "20260917110509_AddTurnVerdictAnswerChoice";
    private const string MigrationUnderTest = "20260917110609_AddFleetOutcomeStopIdentity";

    private sealed class RequiresPostgresFactAttribute : FactAttribute
    {
        public RequiresPostgresFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ConnectionEnvVar)))
                Skip = $"Set {ConnectionEnvVar} to a Postgres connection string to run the real-Postgres " +
                       "record stop identity proof.";
        }
    }

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

    [RequiresPostgresFact]
    public void AddFleetOutcomeStopIdentity_FromEmpty_AddsTheColumnsAndKeepsARecordFiledBefore()
    {
        PostgresProofDatabase.GuardThrowawayDatabase();
        using (var ctx = NewContext())
            ctx.Database.EnsureDeleted();

        using (var ctx = NewContext())
        {
            var all = ctx.Database.GetMigrations().ToList();
            var index = all.IndexOf(MigrationUnderTest);
            Assert.True(index > 0, $"'{MigrationUnderTest}' is not in the Postgres migration set.");
            Assert.Equal(MigrationBefore, all[index - 1]);
            Assert.Equal(MigrationUnderTest, all[^1]);
            ctx.GetService<IMigrator>().Migrate(MigrationBefore);
        }

        Scalar("""
            INSERT INTO gateway.fleet_outcomes ("Id", tenant_id, "Kind", "FiledBy", "AboutSessionId", "CreatedAtUtc",
                "Title", "DetailsJson", "Status")
            VALUES ('6a000000-0000-4000-8000-000000000001', 'tenant-one', 'decision', 'fm-1', 'worker-1',
                TIMESTAMPTZ '2026-09-17 06:00:00Z', 'Publish?', '{}', 'open');
            """);

        using (var ctx = NewContext())
        {
            ctx.GetService<IMigrator>().Migrate();
            Assert.Equal(MigrationUnderTest, ctx.Database.GetAppliedMigrations().Last());
            Assert.False(ctx.Database.HasPendingModelChanges());
        }

        Assert.Equal("2", Scalar("""
            SELECT count(*) FROM information_schema.columns
            WHERE table_schema = 'gateway' AND table_name = 'fleet_outcomes'
              AND column_name IN ('AboutVerdictId', 'AboutTurnEndObservedAtUtc')
            """));
        Assert.Equal("Publish?|open|null|null", Scalar("""
            SELECT "Title" || '|' || "Status" || '|' || COALESCE("AboutVerdictId", 'null') || '|'
                || COALESCE("AboutTurnEndObservedAtUtc"::text, 'null') FROM gateway.fleet_outcomes
            """));
    }
}
