using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Xunit;

namespace CcDirector.Gateway.Tests.Data;

/// <summary>
/// The PostgreSQL half of the Fleet Manager mission's step 9 settings clean-up: a real PostgreSQL database at
/// the schema before the change, holding the removed Assistant keys for two accounts, carried through
/// <c>RemoveAssistantSettings</c>. Only the two removed keys may go.
///
/// GATING. Like <see cref="CallerSuppliedKeyUpgradePreservesRowsPostgresTests"/>, the class is gated on
/// <c>CC_GATEWAY_TEST_PG_CONNECTION</c> and reports SKIPPED when it is unset. Skipped is not passed.
/// </summary>
public sealed class RemoveAssistantSettingsPostgresTests
{
    private const string ConnectionEnvVar = "CC_GATEWAY_TEST_PG_CONNECTION";
    private const string MigrationBefore = "20260917110209_AddFleetOutcomeAdvice";
    private const string MigrationUnderTest = "20260917110309_RemoveAssistantSettings";

    private sealed class RequiresPostgresFactAttribute : FactAttribute
    {
        public RequiresPostgresFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ConnectionEnvVar)))
                Skip = $"Set {ConnectionEnvVar} to a Postgres connection string to run the real-Postgres " +
                       "settings clean-up proof.";
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

    private static void Execute(string sql)
    {
        using var connection = new NpgsqlConnection(Connection);
        connection.Open();
        using var command = new NpgsqlCommand(sql, connection);
        command.ExecuteNonQuery();
    }

    private static List<string> Rows()
    {
        var rows = new List<string>();
        using var connection = new NpgsqlConnection(Connection);
        connection.Open();
        using var command = new NpgsqlCommand(
            """SELECT tenant_id, "Key", "Value" FROM gateway.tenant_settings ORDER BY tenant_id, "Key" """, connection);
        using var reader = command.ExecuteReader();
        while (reader.Read())
            rows.Add($"{reader.GetString(0)}/{reader.GetString(1)}={reader.GetString(2)}");
        return rows;
    }

    [RequiresPostgresFact]
    public void RemoveAssistantSettings_RowsForTwoAccounts_DeletesOnlyTheAssistantKeys()
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

        Execute("""
            INSERT INTO gateway.tenant_settings (tenant_id, "Key", "Value", "UpdatedAtUtc") VALUES
                ('tenant-one', 'car_mode_model', 'devthrottle/wingman', TIMESTAMPTZ '2026-09-01 10:00:00Z'),
                ('tenant-one', 'car_mode_end_phrase', 'over and out', TIMESTAMPTZ '2026-09-01 10:00:00Z'),
                ('tenant-one', 'time_zone', 'Europe/Copenhagen', TIMESTAMPTZ '2026-09-01 10:00:00Z'),
                ('tenant-two', 'car_mode_model', 'devthrottle/wingman-fast', TIMESTAMPTZ '2026-09-01 10:00:00Z'),
                ('tenant-two', 'tts_voice', 'shimmer', TIMESTAMPTZ '2026-09-01 10:00:00Z');
            """);
        Assert.Equal(5, Rows().Count);

        using (var ctx = NewContext())
        {
            ctx.GetService<IMigrator>().Migrate();
            Assert.Contains(MigrationUnderTest, ctx.Database.GetAppliedMigrations());
        }

        Assert.Equal(
            new[] { "tenant-one/time_zone=Europe/Copenhagen", "tenant-two/tts_voice=shimmer" },
            Rows());
    }
}
