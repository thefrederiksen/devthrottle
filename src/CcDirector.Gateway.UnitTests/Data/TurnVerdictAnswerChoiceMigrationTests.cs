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
/// The step 7 fixes: a verdict's answer is stored with what was sent, so <c>turn_verdicts</c> gains
/// <c>AnswerJson</c> (<c>AddTurnVerdictAnswerChoice</c>). The SQLite migration applies from an empty database and keeps
/// an answered verdict stored before it, with no answer choice; both providers' Designer files are found, are the
/// newest, and carry the current model, and neither snapshot is behind the model. The PostgreSQL apply is
/// <c>TurnVerdictAnswerChoicePostgresTests</c>.
/// </summary>
public sealed class TurnVerdictAnswerChoiceMigrationTests
{
    private const string SqliteBefore = "20260917090600_AddFleetManagerEventOutcomeAnswer";
    private const string SqliteUnderTest = "20260917090700_AddTurnVerdictAnswerChoice";

    [Fact]
    public void AddTurnVerdictAnswerChoice_FromEmpty_AddsTheColumnAndKeepsAVerdictStoredBefore()
    {
        var directory = Path.Combine(Path.GetTempPath(), "cc-verdict-answer-migration-" + Guid.NewGuid().ToString("N"));
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
            Assert.Equal(SqliteUnderTest, all[^1]); // sorted after every migration on this branch

            // From an EMPTY database to the schema just before, with a verdict answered as the answer route marked it then.
            Assert.Empty(context.Database.GetAppliedMigrations());
            migrator.Migrate(SqliteBefore);
            Execute(connection, """
                INSERT INTO "turn_verdicts" ("tenant_id", "SessionId", "JudgedAtUtc", "VerdictId", "TurnEndObservedAtUtc",
                    "ScreenHash", "Failed", "VerdictJson", "AnsweredAtUtc")
                VALUES ('tenant-one', 'worker-1', '2026-09-17 06:00:00', 'tv-before', '2026-09-17 05:59:48',
                    'hash-1', 0, '{}', '2026-09-17 06:01:00');
                """);
            Assert.DoesNotContain("AnswerJson", ColumnNames(connection));

            migrator.Migrate();

            Assert.Equal(SqliteUnderTest, context.Database.GetAppliedMigrations().Last());
            Assert.Empty(context.Database.GetPendingMigrations());
            Assert.False(context.Database.HasPendingModelChanges());
            Assert.Contains("AnswerJson", ColumnNames(connection));
            Assert.Equal("tv-before|2026-09-17 06:01:00|null", Single(connection,
                """SELECT "VerdictId" || '|' || "AnsweredAtUtc" || '|' || IFNULL("AnswerJson", 'null') FROM "turn_verdicts" """));
        }
        finally
        {
            SqliteConnection.ClearPool(new SqliteConnection(connection));
            try { Directory.Delete(directory, recursive: true); }
            catch (IOException) { /* best-effort cleanup of a throwaway database */ }
        }
    }

    /// <summary>
    /// Each provider's Designer is found through its attributes, is the newest migration, carries the current model,
    /// and the provider's snapshot equals the model. Read with no database.
    /// </summary>
    [Theory]
    [InlineData("sqlite", "20260917090700_AddTurnVerdictAnswerChoice")]
    [InlineData("postgres", "20260917090709_AddTurnVerdictAnswerChoice")]
    public void AddTurnVerdictAnswerChoice_Designer_IsDiscoveredNewestAndCarriesTheCurrentModel(string provider, string id)
    {
        using var context = FleetManagerEventOutcomeAnswerMigrationTests.Context(provider);
        var assembly = context.GetService<IMigrationsAssembly>();
        Assert.True(assembly.Migrations.TryGetValue(id, out var type), $"'{id}' is not discovered for {provider}.");
        Assert.Equal("AddTurnVerdictAnswerChoice", type!.Name);
        Assert.Equal(id, assembly.Migrations.Keys.Max(StringComparer.Ordinal));
        Assert.Equal(typeof(GatewayDbContext), type.GetCustomAttribute<DbContextAttribute>()!.ContextType);

        var designed = FleetManagerEventOutcomeAnswerMigrationTests.DesignedModel(context, assembly, type);
        var current = context.GetService<IDesignTimeModel>().Model;
        Assert.False(context.GetService<IMigrationsModelDiffer>().HasDifferences(designed.GetRelationalModel(), current.GetRelationalModel()),
            $"The {provider} Designer of '{id}' does not carry the current model.");
        Assert.False(context.Database.HasPendingModelChanges(), $"The {provider} model snapshot is behind the model.");
        var verdicts = assembly.ModelSnapshot!.Model.GetEntityTypes().Single(e => e.GetTableName() == "turn_verdicts");
        Assert.NotNull(verdicts.FindProperty("AnswerJson"));
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
        command.CommandText = """SELECT name FROM pragma_table_info('turn_verdicts')""";
        using var reader = command.ExecuteReader();
        while (reader.Read()) rows.Add(reader.GetString(0));
        return rows;
    }
}
