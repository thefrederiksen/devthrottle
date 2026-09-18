using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Xunit;

namespace CcDirector.Gateway.Tests.Data;

/// <summary>
/// The PostgreSQL half of the step 7 fixes' storage: a real PostgreSQL database at the schema before the change,
/// holding a verdict answered as the answer route marked it then, carried through <c>AddTurnVerdictAnswerChoice</c>. The
/// verdict survives and the new column exists, empty on it.
///
/// GATING. Like <see cref="FleetManagerEventOutcomeAnswerPostgresTests"/>, the class is gated on
/// <c>CC_GATEWAY_TEST_PG_CONNECTION</c> and reports SKIPPED when it is unset. Skipped is not passed.
/// </summary>
public sealed class TurnVerdictAnswerChoicePostgresTests
{
    private const string ConnectionEnvVar = "CC_GATEWAY_TEST_PG_CONNECTION";
    private const string MigrationBefore = "20260917110409_AddFleetManagerEventOutcomeAnswer";
    private const string MigrationUnderTest = "20260917110509_AddTurnVerdictAnswerChoice";

    private sealed class RequiresPostgresFactAttribute : FactAttribute
    {
        public RequiresPostgresFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ConnectionEnvVar)))
                Skip = $"Set {ConnectionEnvVar} to a Postgres connection string to run the real-Postgres " +
                       "verdict answer column proof.";
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
    public void AddTurnVerdictAnswerChoice_FromEmpty_AddsTheColumnAndKeepsAVerdictStoredBefore()
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
            Assert.Equal("20260917110609_AddFleetOutcomeStopIdentity", all[index + 1]);
            Assert.Equal(index + 2, all.Count);
            ctx.GetService<IMigrator>().Migrate(MigrationBefore);
        }

        Scalar("""
            INSERT INTO gateway.turn_verdicts (tenant_id, "SessionId", "JudgedAtUtc", "VerdictId", "TurnEndObservedAtUtc",
                "ScreenHash", "Failed", "VerdictJson", "AnsweredAtUtc")
            VALUES ('tenant-one', 'worker-1', TIMESTAMPTZ '2026-09-17 06:00:00Z', 'tv-before',
                TIMESTAMPTZ '2026-09-17 05:59:48Z', 'hash-1', false, '{}', TIMESTAMPTZ '2026-09-17 06:01:00Z');
            """);

        using (var ctx = NewContext())
        {
            ctx.GetService<IMigrator>().Migrate(MigrationUnderTest);
            Assert.Equal(MigrationUnderTest, ctx.Database.GetAppliedMigrations().Last());
        }

        Assert.Equal("1", Scalar("""
            SELECT count(*) FROM information_schema.columns
            WHERE table_schema = 'gateway' AND table_name = 'turn_verdicts' AND column_name = 'AnswerJson'
            """));
        Assert.Equal("tv-before|null", Scalar("""
            SELECT "VerdictId" || '|' || COALESCE("AnswerJson", 'null') FROM gateway.turn_verdicts
            """));
    }
}
