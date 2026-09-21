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
/// The steps 5 and 6 fixes: an owner's answer to a card travels to the Fleet Manager as an event, so
/// <c>fleet_manager_events</c> gains the record it answers, its title and the owner's words
/// (<c>AddFleetManagerEventOutcomeAnswer</c>). The SQLite migration applies from an empty database and keeps an
/// event stored before it; both providers' Designer files are found and carry the new columns. The PostgreSQL apply
/// is <c>FleetManagerEventOutcomeAnswerPostgresTests</c>.
/// </summary>
public sealed class FleetManagerEventOutcomeAnswerMigrationTests
{
    private const string SqliteBefore = "20260917110300_RemoveAssistantSettings";
    private const string SqliteUnderTest = "20260917110400_AddFleetManagerEventOutcomeAnswer";

    [Fact]
    public void AddFleetManagerEventOutcomeAnswer_FromEmpty_AddsTheColumnsAndKeepsAnEventStoredBefore()
    {
        var directory = Path.Combine(Path.GetTempPath(), "cc-fm-event-answer-migration-" + Guid.NewGuid().ToString("N"));
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
            Assert.Equal("20260917110500_AddTurnVerdictAnswerChoice", all[index + 1]);
            Assert.Equal("20260917110600_AddFleetOutcomeStopIdentity", all[index + 2]);
            Assert.Equal("20260918171353_AddWingmanNarrationCallTrace", all[index + 3]);
            Assert.Equal("20260920021757_AddDiscoveredRepositories", all[index + 4]);
            Assert.Equal("20260920052924_AddRaisedSessions", all[index + 5]);
            Assert.Equal("20260921081600_AddFactoryActivity", all[index + 6]); // the only migrations after it
            Assert.Equal(index + 7, all.Count);

            // From an EMPTY database to the schema just before, with a stop stored as step 4 stores it.
            Assert.Empty(context.Database.GetAppliedMigrations());
            migrator.Migrate(SqliteBefore);
            Execute(connection, """
                INSERT INTO "fleet_manager_events" ("Id", "Kind", "SessionId", "SessionName", "AddressedTo",
                    "CreatedAtUtc", "DeliveryCount", "ReadingPending", "tenant_id")
                VALUES ('0b000000-0000-4000-8000-000000000001', 'stop', 'worker-1', 'Worker', 'fm-1',
                    '2026-09-17 06:00:00', 0, 0, 'tenant-one');
                """);
            Assert.DoesNotContain("OutcomeId", ColumnNames(connection));

            migrator.Migrate(SqliteUnderTest);

            Assert.Equal(SqliteUnderTest, context.Database.GetAppliedMigrations().Last());
            var columns = ColumnNames(connection);
            Assert.Contains("OutcomeId", columns);
            Assert.Contains("OutcomeTitle", columns);
            Assert.Contains("Words", columns);
            Assert.Contains("IX_fleet_manager_events_tenant_id_OutcomeId", IndexNames(connection));
            Assert.Equal("stop|worker-1|null|null|null", Single(connection,
                """SELECT "Kind" || '|' || "SessionId" || '|' || IFNULL("OutcomeId", 'null') || '|' || IFNULL("OutcomeTitle", 'null') || '|' || IFNULL("Words", 'null') FROM "fleet_manager_events" """));
        }
        finally
        {
            SqliteConnection.ClearPool(new SqliteConnection(connection));
            try { Directory.Delete(directory, recursive: true); }
            catch (IOException) { /* best-effort cleanup of a throwaway database */ }
        }
    }

    /// <summary>
    /// Each provider's Designer is found through its attributes and carries the event answer columns. It is no longer
    /// the newest: the step 7 fixes added <c>AddTurnVerdictAnswerChoice</c> after it, which
    /// <c>TurnVerdictAnswerChoiceMigrationTests</c> holds to the current model and snapshot, and
    /// <c>FleetManagerLaterStepsMigrationChainTests</c> holds the difference between the two Designers to that one
    /// column. Read with no database.
    /// </summary>
    [Theory]
    [InlineData("sqlite", "20260917110400_AddFleetManagerEventOutcomeAnswer")]
    [InlineData("postgres", "20260917110409_AddFleetManagerEventOutcomeAnswer")]
    public void AddFleetManagerEventOutcomeAnswer_Designer_IsDiscoveredAndCarriesTheEventAnswerColumns(string provider, string id)
    {
        using var context = Context(provider);
        var assembly = context.GetService<IMigrationsAssembly>();
        Assert.True(assembly.Migrations.TryGetValue(id, out var type), $"'{id}' is not discovered for {provider}.");
        Assert.Equal("AddFleetManagerEventOutcomeAnswer", type!.Name);
        Assert.Equal(typeof(GatewayDbContext), type.GetCustomAttribute<DbContextAttribute>()!.ContextType);

        var designed = DesignedModel(context, assembly, type);
        var events = designed.GetEntityTypes().Single(e => e.GetTableName() == "fleet_manager_events");
        Assert.NotNull(events.FindProperty("OutcomeId"));
        Assert.NotNull(events.FindProperty("OutcomeTitle"));
        Assert.NotNull(events.FindProperty("Words"));
    }

    internal static GatewayDbContext Context(string provider)
    {
        var builder = new DbContextOptionsBuilder<GatewayDbContext>();
        if (provider == "sqlite")
            builder.UseSqlite("Data Source=:memory:");
        else
            builder.UseNpgsql("Host=pg.invalid;Database=none;Username=none;Password=none",
                o => o.MigrationsAssembly("CcDirector.Gateway.Migrations.Postgres"));
        return new GatewayDbContext(builder.Options);
    }

    internal static IModel DesignedModel(GatewayDbContext context, IMigrationsAssembly assembly, TypeInfo type)
    {
        var migration = assembly.CreateMigration(type, context.Database.ProviderName!);
        Assert.NotNull(migration.TargetModel);
        return context.GetService<IModelRuntimeInitializer>()
            .Initialize((IModel)migration.TargetModel!, designTime: true, validationLogger: null);
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

    private static List<string> ColumnNames(string connectionString) => Names(connectionString,
        """SELECT name FROM pragma_table_info('fleet_manager_events')""");

    private static List<string> IndexNames(string connectionString) => Names(connectionString,
        """SELECT name FROM pragma_index_list('fleet_manager_events')""");

    private static List<string> Names(string connectionString, string sql)
    {
        var rows = new List<string>();
        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        while (reader.Read()) rows.Add(reader.GetString(0));
        return rows;
    }
}
