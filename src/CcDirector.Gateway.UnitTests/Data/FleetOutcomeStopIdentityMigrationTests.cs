using System.Reflection;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace CcDirector.Gateway.Tests.Data;

/// <summary>
/// The steps 7 to 9 round 2 fixes: an outcome record stores the stop it was filed about, so <c>fleet_outcomes</c> gains
/// <c>AboutVerdictId</c> and <c>AboutTurnEndObservedAtUtc</c> (<c>AddFleetOutcomeStopIdentity</c>). The SQLite migration
/// applies from an empty database and keeps an open record filed before it, naming no stop; both providers' Designer
/// files are found, are the newest, and carry the current model, and neither snapshot is behind the model. The
/// PostgreSQL apply is <c>FleetOutcomeStopIdentityPostgresTests</c>.
/// </summary>
public sealed class FleetOutcomeStopIdentityMigrationTests
{
    private const string SqliteBefore = "20260917110500_AddTurnVerdictAnswerChoice";
    private const string SqliteUnderTest = "20260917110600_AddFleetOutcomeStopIdentity";

    [Fact]
    public void AddFleetOutcomeStopIdentity_FromEmpty_AddsTheColumnsAndKeepsARecordFiledBefore()
    {
        var directory = Path.Combine(Path.GetTempPath(), "cc-outcome-stop-migration-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var connection = "Data Source=" + Path.Combine(directory, "gateway.db") + ";Pooling=False";
        try
        {
            var options = new DbContextOptionsBuilder<GatewayDbContext>().UseSqlite(connection).Options;
            using var context = new GatewayDbContext(options) { ActiveTenant = TenantId.Local.Value };
            var migrator = context.Database.GetService<IMigrator>();

            var all = context.Database.GetMigrations().ToList();
            var index = all.IndexOf(SqliteUnderTest);
            Assert.True(index > 0, $"'{SqliteUnderTest}' is not in the SQLite migration set.");
            Assert.Equal(SqliteBefore, all[index - 1]);
            Assert.True(all.Count > index + 1, $"'{SqliteUnderTest}' must not be the newest migration.");

            // From an EMPTY database to the schema just before, with an open record filed as it was filed then.
            Assert.Empty(context.Database.GetAppliedMigrations());
            migrator.Migrate(SqliteBefore);
            Execute(connection, """
                INSERT INTO "fleet_outcomes" ("Id", "tenant_id", "Kind", "FiledBy", "AboutSessionId", "CreatedAtUtc",
                    "Title", "DetailsJson", "Status")
                VALUES ('6a000000-0000-4000-8000-000000000001', 'tenant-one', 'decision', 'fm-1', 'worker-1',
                    '2026-09-17 06:00:00', 'Publish?', '{}', 'open');
                """);
            Assert.DoesNotContain("AboutVerdictId", ColumnNames(connection));

            migrator.Migrate();

            // Every migration in the set applied, in the set's order - whichever is newest today.
            Assert.Equal(all, context.Database.GetAppliedMigrations().ToList());
            Assert.Empty(context.Database.GetPendingMigrations());
            Assert.False(context.Database.HasPendingModelChanges());
            var columns = ColumnNames(connection);
            Assert.Contains("AboutVerdictId", columns);
            Assert.Contains("AboutTurnEndObservedAtUtc", columns);
            Assert.Equal("Publish?|open|null|null", Single(connection,
                """SELECT "Title" || '|' || "Status" || '|' || IFNULL("AboutVerdictId", 'null') || '|' || IFNULL("AboutTurnEndObservedAtUtc", 'null') FROM "fleet_outcomes" """));
        }
        finally
        {
            SqliteConnection.ClearPool(new SqliteConnection(connection));
            try { Directory.Delete(directory, recursive: true); }
            catch (IOException) { /* best-effort cleanup of a throwaway database */ }
        }
    }

    /// <summary>
    /// The NEWEST migration of each provider is found through its attributes, carries the current model, and the
    /// provider's snapshot equals the model. Read with no database.
    ///
    /// THE NEWEST IS DERIVED FROM THE MIGRATIONS ASSEMBLY, NEVER NAMED: the name moves when one is added, and a test
    /// that pinned it went red on every branch that added a migration until somebody edited the test. It is the
    /// newest Designer that has to match the model, whichever migration that is today. The columns checked at the end
    /// are the ones AddFleetOutcomeStopIdentity introduced, and a snapshot is cumulative, so they must still be in the
    /// newest one: that is what says a later migration did not quietly drop them.
    /// </summary>
    [Theory]
    [InlineData("sqlite")]
    [InlineData("postgres")]
    public void TheNewestMigrationsDesigner_IsDiscovered_AndCarriesTheCurrentModel(string provider)
    {
        using var context = FleetManagerEventOutcomeAnswerMigrationTests.Context(provider);
        var assembly = context.GetService<IMigrationsAssembly>();
        Assert.NotEmpty(assembly.Migrations);
        var id = assembly.Migrations.Keys.Max(StringComparer.Ordinal)!;
        var type = assembly.Migrations[id];
        Assert.True(string.CompareOrdinal(id, SqliteUnderTest) > 0, $"'{id}' must sort after '{SqliteUnderTest}' for {provider}.");
        Assert.Equal(typeof(GatewayDbContext), type.GetCustomAttribute<DbContextAttribute>()!.ContextType);

        var designed = FleetManagerEventOutcomeAnswerMigrationTests.DesignedModel(context, assembly, type);
        var current = context.GetService<IDesignTimeModel>().Model;
        Assert.False(context.GetService<IMigrationsModelDiffer>().HasDifferences(designed.GetRelationalModel(), current.GetRelationalModel()),
            $"The {provider} Designer of '{id}' does not carry the current model.");
        Assert.False(context.Database.HasPendingModelChanges(), $"The {provider} model snapshot is behind the model.");
        var outcomes = assembly.ModelSnapshot!.Model.GetEntityTypes().Single(e => e.GetTableName() == "fleet_outcomes");
        Assert.NotNull(outcomes.FindProperty("AboutVerdictId"));
        Assert.NotNull(outcomes.FindProperty("AboutTurnEndObservedAtUtc"));
    }

    private static void Execute(string connectionString, string sql)
    {
        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static string Single(string connectionString, string sql)
    {
        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (string)command.ExecuteScalar()!;
    }

    private static List<string> ColumnNames(string connectionString)
    {
        var rows = new List<string>();
        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """SELECT name FROM pragma_table_info('fleet_outcomes')""";
        using var reader = command.ExecuteReader();
        while (reader.Read()) rows.Add(reader.GetString(0));
        return rows;
    }
}
