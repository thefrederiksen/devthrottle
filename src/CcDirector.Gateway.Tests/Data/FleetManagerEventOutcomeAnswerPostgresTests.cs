using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Xunit;

namespace CcDirector.Gateway.Tests.Data;

/// <summary>
/// The PostgreSQL half of the steps 5 and 6 fixes' storage: a real PostgreSQL database at the schema before the
/// change, holding a stop stored as step 4 stores it, carried through <c>AddFleetManagerEventOutcomeAnswer</c>. The
/// stop survives and the three new columns exist, empty on it.
///
/// GATING. Like <see cref="RemoveAssistantSettingsPostgresTests"/>, the class is gated on
/// <c>CC_GATEWAY_TEST_PG_CONNECTION</c> and reports SKIPPED when it is unset. Skipped is not passed.
/// </summary>
public sealed class FleetManagerEventOutcomeAnswerPostgresTests
{
    private const string ConnectionEnvVar = "CC_GATEWAY_TEST_PG_CONNECTION";
    private const string MigrationBefore = "20260917110309_RemoveAssistantSettings";
    private const string MigrationUnderTest = "20260917110409_AddFleetManagerEventOutcomeAnswer";

    private sealed class RequiresPostgresFactAttribute : FactAttribute
    {
        public RequiresPostgresFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ConnectionEnvVar)))
                Skip = $"Set {ConnectionEnvVar} to a Postgres connection string to run the real-Postgres " +
                       "event answer columns proof.";
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
    public void AddFleetManagerEventOutcomeAnswer_FromEmpty_AddsTheColumnsAndKeepsAnEventStoredBefore()
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
            ctx.GetService<IMigrator>().Migrate(MigrationBefore);
        }

        Scalar("""
            INSERT INTO gateway.fleet_manager_events ("Id", "Kind", "SessionId", "SessionName", "AddressedTo",
                "CreatedAtUtc", "DeliveryCount", "ReadingPending", tenant_id)
            VALUES ('0b000000-0000-4000-8000-000000000001', 'stop', 'worker-1', 'Worker', 'fm-1',
                TIMESTAMPTZ '2026-09-17 06:00:00Z', 0, false, 'tenant-one');
            """);

        using (var ctx = NewContext())
        {
            ctx.GetService<IMigrator>().Migrate(MigrationUnderTest);
            Assert.Equal(MigrationUnderTest, ctx.Database.GetAppliedMigrations().Last());
        }

        Assert.Equal("3", Scalar("""
            SELECT count(*) FROM information_schema.columns
            WHERE table_schema = 'gateway' AND table_name = 'fleet_manager_events'
              AND column_name IN ('OutcomeId', 'OutcomeTitle', 'Words')
            """));
        Assert.Equal("stop|worker-1|null", Scalar("""
            SELECT "Kind" || '|' || "SessionId" || '|' || COALESCE("OutcomeId", 'null') FROM gateway.fleet_manager_events
            """));
    }
}
