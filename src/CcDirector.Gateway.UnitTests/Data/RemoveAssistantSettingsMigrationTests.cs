using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Settings;
using CcDirector.Gateway.Tests.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using System.Reflection;
using Xunit;

namespace CcDirector.Gateway.Tests.Data;

/// <summary>
/// The Fleet Manager mission, step 9: the Assistant was removed, and the two per-account settings only it used
/// (<c>car_mode_model</c>, <c>car_mode_end_phrase</c>) went with it. The SQLite migration deletes their stored
/// rows for every account and nothing else; a database that still holds them - one the migration has not yet
/// reached - still reads its settings. The PostgreSQL twin is proved by
/// <c>RemoveAssistantSettingsPostgresTests</c>, and <see cref="GatewayHostBootSmokeTests"/> holds the two
/// migration names to each other.
/// </summary>
public sealed class RemoveAssistantSettingsMigrationTests
{
    private const string MigrationBefore = "20260917040637_AddFleetOutcomeAdvice";
    private const string MigrationUnderTest = "20260917060000_RemoveAssistantSettings";

    [Fact]
    public void RemoveAssistantSettings_RowsForTwoAccounts_DeletesOnlyTheAssistantKeys()
    {
        var directory = Path.Combine(
            Path.GetTempPath(), "cc-remove-assistant-settings-migration-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "gateway.db");
        var connection = "Data Source=" + path + ";Pooling=False";

        try
        {
            var options = new DbContextOptionsBuilder<GatewayDbContext>().UseSqlite(connection).Options;
            using var context = new GatewayDbContext(options) { ActiveTenant = TenantId.Local.Value };
            var migrator = context.Database.GetService<IMigrator>();

            // The proof isolates THIS migration only while the named predecessor really is the one before it.
            var all = context.Database.GetMigrations().ToList();
            var index = all.IndexOf(MigrationUnderTest);
            Assert.True(index > 0, $"'{MigrationUnderTest}' is not in the SQLite migration set.");
            Assert.Equal(MigrationBefore, all[index - 1]);

            // A database at exactly the schema before the change, holding the removed keys for two accounts
            // beside settings that must survive.
            migrator.Migrate(MigrationBefore);
            Execute(connection, """
                INSERT INTO "tenant_settings" ("tenant_id", "Key", "Value", "UpdatedAtUtc") VALUES
                    ('tenant-one', 'car_mode_model', 'devthrottle/wingman', '2026-09-01 10:00:00'),
                    ('tenant-one', 'car_mode_end_phrase', 'over and out', '2026-09-01 10:00:00'),
                    ('tenant-one', 'time_zone', 'Europe/Copenhagen', '2026-09-01 10:00:00'),
                    ('tenant-two', 'car_mode_model', 'devthrottle/wingman-fast', '2026-09-01 10:00:00'),
                    ('tenant-two', 'tts_voice', 'shimmer', '2026-09-01 10:00:00');
                """);
            Assert.Equal(5, Keys(connection).Count);

            migrator.Migrate();

            Assert.Contains(MigrationUnderTest, context.Database.GetAppliedMigrations());
            Assert.Equal(
                new[] { "tenant-one/time_zone=Europe/Copenhagen", "tenant-two/tts_voice=shimmer" },
                Keys(connection));
        }
        finally
        {
            SqliteConnection.ClearPool(new SqliteConnection(connection));
            try
            {
                if (Directory.Exists(directory))
                    Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort cleanup of a throwaway migration database.
            }
        }
    }

    /// <summary>
    /// A database the migration has not reached yet - here, rows written after it, which is the same state as far
    /// as a reader can tell - still serves every settings read. The keys are no longer settings, so nothing asks
    /// for them, and the reads that list every row do not choke on them.
    /// </summary>
    [Fact]
    public void SettingsRead_StaleAssistantRowsPresent_StillReadsEverySetting()
    {
        using var harness = new GatewayDbTestHarness();
        var database = harness.Open();
        var store = new TenantSettingsStore(database);
        var resolver = new TenantSettingsResolver(store);
        var tenant = new TenantId("tenant-one");
        resolver.SetTimeZone(tenant, "Asia/Tokyo", new DateTime(2026, 9, 17, 6, 0, 0, DateTimeKind.Utc));
        Execute("Data Source=" + harness.DbPath, """
            INSERT INTO "tenant_settings" ("tenant_id", "Key", "Value", "UpdatedAtUtc") VALUES
                ('tenant-one', 'car_mode_model', 'devthrottle/wingman', '2026-09-01 10:00:00'),
                ('tenant-one', 'car_mode_end_phrase', 'over and out', '2026-09-01 10:00:00');
            """);

        var all = store.GetAll(tenant);

        Assert.Equal(3, all.Count);
        Assert.Equal("Asia/Tokyo", resolver.TimeZone(tenant));
        Assert.Equal("Asia/Tokyo", store.Get(tenant, TenantSettingKeys.TimeZone));
        Assert.DoesNotContain("car_mode_model", TenantSettingKeys.All);
        Assert.DoesNotContain("car_mode_end_phrase", TenantSettingKeys.All);
    }

    /// <summary>
    /// THE HAND-WRITTEN DESIGNER FILES ARE WHAT ENTITY FRAMEWORK WOULD HAVE GENERATED, as far as it can tell: for each
    /// provider the migration is found through its <c>[DbContext]</c> and <c>[Migration]</c> attributes, it comes
    /// straight after step 7's advice migration, and the model its Designer carries has no difference from that
    /// migration's model - the schema did not change. (It is no longer the newest: the steps 5 and 6 fixes added
    /// <c>AddFleetManagerEventOutcomeAnswer</c> after it, which <c>FleetManagerEventOutcomeAnswerMigrationTests</c>
    /// holds to the current model and snapshot.) Read with no database.
    /// </summary>
    [Theory]
    [InlineData("sqlite", "20260917060000_RemoveAssistantSettings", "20260917040637_AddFleetOutcomeAdvice")]
    [InlineData("postgres", "20260917060010_RemoveAssistantSettings", "20260917040647_AddFleetOutcomeAdvice")]
    public void RemoveAssistantSettings_Designer_IsDiscoveredAndCarriesTheModelOfTheMigrationBefore(string provider, string id, string before)
    {
        using var context = FleetManagerEventOutcomeAnswerMigrationTests.Context(provider);

        var assembly = context.GetService<IMigrationsAssembly>();
        Assert.True(assembly.Migrations.TryGetValue(id, out var type), $"'{id}' is not discovered for {provider}.");
        Assert.Equal("RemoveAssistantSettings", type!.Name);
        var ordered = assembly.Migrations.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();
        Assert.Equal(before, ordered[ordered.IndexOf(id) - 1]);
        Assert.Equal(typeof(GatewayDbContext), type.GetCustomAttribute<DbContextAttribute>()!.ContextType);

        var designed = FleetManagerEventOutcomeAnswerMigrationTests.DesignedModel(context, assembly, type);
        var previous = FleetManagerEventOutcomeAnswerMigrationTests.DesignedModel(context, assembly, assembly.Migrations[before]);
        var differ = context.GetService<IMigrationsModelDiffer>();
        Assert.False(differ.HasDifferences(designed.GetRelationalModel(), previous.GetRelationalModel()),
            $"The {provider} Designer of '{id}' does not carry the model of '{before}'.");
    }

    private static void Execute(string connectionString, string sql)
    {
        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static List<string> Keys(string connectionString)
    {
        var rows = new List<string>();
        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """SELECT "tenant_id", "Key", "Value" FROM "tenant_settings" ORDER BY "tenant_id", "Key" """;
        using var reader = command.ExecuteReader();
        while (reader.Read())
            rows.Add($"{reader.GetString(0)}/{reader.GetString(1)}={reader.GetString(2)}");
        return rows;
    }
}
