using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace CcDirector.Gateway.Tests.Data;

/// <summary>
/// The SQLite half of <c>AddTriggerBackupMachines</c> (Mail Desk, phase 5): <c>triggers</c> gains
/// <c>BackupMachinesJson</c> and <c>ClaimedByMachine</c>. It applies right after Session Rules phase 1,
/// <c>AddSessionRuleKindsAndHookFirings</c> (created before this migration but merged after it, and already applied in
/// production under that id, so it keeps its place in the chain), keeps the triggers stored before it, gives each no backups, and gives a claim held
/// before it the trigger's own machine - the only machine a claim was ever held on until now - while an unclaimed
/// trigger's holder machine stays empty. Its Down removes both columns and keeps the rows. The PostgreSQL half is
/// <c>AddTriggerBackupMachinesPostgresTests</c>, run by scripts/test-database.ps1.
/// </summary>
public sealed class AddTriggerBackupMachinesMigrationTests
{
    private const string SqliteBefore = "20261020014625_AddSessionRuleKindsAndHookFirings";
    private const string SqliteUnderTest = "20261020015915_AddTriggerBackupMachines";

    [Fact]
    public void AddTriggerBackupMachines_OnSqlite_AddsBothColumns_GivesAHeldClaimItsMachine_AndItsDownRemovesThem()
    {
        var directory = Path.Combine(Path.GetTempPath(), "cc-trigger-backup-migration-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var connection = "Data Source=" + Path.Combine(directory, "gateway.db") + ";Pooling=False";
        try
        {
            var options = new DbContextOptionsBuilder<GatewayDbContext>().UseSqlite(connection).Options;
            using var context = new GatewayDbContext(options) { ActiveTenant = TenantId.Local.Value };
            var migrator = context.Database.GetService<IMigrator>();
            var all = context.Database.GetMigrations().ToList();
            Assert.Equal(SqliteBefore, all[all.IndexOf(SqliteUnderTest) - 1]);

            // Arrange: two triggers stored before it - one held by a Director, one never claimed.
            migrator.Migrate(SqliteBefore);
            Execute(connection, """
                INSERT INTO "triggers" ("Id", "tenant_id", "Name", "Factory", "FactoryAgent", "Machine", "RepoPath", "CheckCommand",
                    "IntervalSeconds", "Prompt", "Paused", "CreatedBy", "CreatedUtc", "ClaimedByDirectorId", "ClaimedUtc")
                VALUES ('11111111-1111-1111-1111-111111111111', 'local', 'held', 'f', 'a', 'machine-a', '', 'check', 600, 'p', 0,
                        'test', '2026-10-18 09:00:00', 'dir-a', '2026-10-18 09:05:00'),
                       ('22222222-2222-2222-2222-222222222222', 'local', 'free', 'f', 'a', 'machine-b', '', 'check', 600, 'p', 0,
                        'test', '2026-10-18 09:00:00', '', NULL);
                """);
            Assert.DoesNotContain("ClaimedByMachine", ColumnNames(connection));

            // Act
            migrator.Migrate(SqliteUnderTest);

            // Assert
            Assert.Contains("BackupMachinesJson", ColumnNames(connection));
            Assert.Equal("free|[]||held|[]|machine-a", Single(connection, """
                SELECT group_concat("Name" || '|' || "BackupMachinesJson" || '|' || "ClaimedByMachine", '|') FROM
                    (SELECT * FROM "triggers" ORDER BY "Name")
                """));

            // The store reads today's model on the newest schema, and the model matches the migrations.
            migrator.Migrate();
            Assert.False(context.Database.HasPendingModelChanges());

            // The reversal keeps both rows and removes both columns.
            migrator.Migrate(SqliteBefore);
            Assert.DoesNotContain("BackupMachinesJson", ColumnNames(connection));
            Assert.DoesNotContain("ClaimedByMachine", ColumnNames(connection));
            Assert.Equal("2", Single(connection, """SELECT count(*) FROM "triggers" """));
        }
        finally
        {
            SqliteConnection.ClearPool(new SqliteConnection(connection));
            try { Directory.Delete(directory, recursive: true); }
            catch (IOException) { /* best-effort cleanup of a throwaway database */ }
        }
    }

    private static void Execute(string connection, string sql)
    {
        using var c = new SqliteConnection(connection);
        c.Open();
        using var command = c.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static string Single(string connection, string sql)
    {
        using var c = new SqliteConnection(connection);
        c.Open();
        using var command = c.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture)!;
    }

    private static List<string> ColumnNames(string connection)
    {
        using var c = new SqliteConnection(connection);
        c.Open();
        using var command = c.CreateCommand();
        command.CommandText = """SELECT name FROM pragma_table_info('triggers')""";
        using var reader = command.ExecuteReader();
        var names = new List<string>();
        while (reader.Read()) names.Add(reader.GetString(0));
        return names;
    }
}
