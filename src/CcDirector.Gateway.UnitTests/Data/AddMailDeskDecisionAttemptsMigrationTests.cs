using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace CcDirector.Gateway.Tests.Data;

/// <summary>
/// The SQLite half of <c>AddMailDeskDecisionAttempts</c> (Mail Desk, phase 3, section 8): <c>mail_desk_decisions</c>
/// gains <c>AttemptCount</c> and <c>RulesVersion</c>. It applies right after the phase's
/// <c>AddMailDeskChannelChecks</c>, keeps the decisions stored before it, and counts each of them as screened
/// once (attempt 1) under rules version 0. That attempt count of 1 is a hand edit over what the tool generates (0),
/// so a regeneration that drops it is caught here. Its Down removes both columns and keeps the rows. The PostgreSQL
/// half is <c>MailDeskDecisionAttemptsPostgresTests</c>, run by scripts/test-database.ps1.
/// </summary>
public sealed class AddMailDeskDecisionAttemptsMigrationTests
{
    private const string SqliteBefore = "20261020020203_AddMailDeskChannelChecks";
    private const string SqliteUnderTest = "20261020020444_AddMailDeskDecisionAttempts";

    [Fact]
    public void AddMailDeskDecisionAttempts_OnSqlite_CountsAnEarlierDecisionAsScreenedOnce_AndItsDownRemovesBothColumns()
    {
        var directory = Path.Combine(Path.GetTempPath(), "cc-mail-desk-attempts-migration-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var connection = "Data Source=" + Path.Combine(directory, "gateway.db") + ";Pooling=False";
        try
        {
            var options = new DbContextOptionsBuilder<GatewayDbContext>().UseSqlite(connection).Options;
            using var context = new GatewayDbContext(options) { ActiveTenant = TenantId.Local.Value };
            var migrator = context.Database.GetService<IMigrator>();
            var all = context.Database.GetMigrations().ToList();
            Assert.Equal(SqliteBefore, all[all.IndexOf(SqliteUnderTest) - 1]);

            // Arrange: one decision stored before the migration.
            migrator.Migrate(SqliteBefore);
            Execute(connection, """
                INSERT INTO "mail_desk_decisions" ("Id", "ChannelId", "MessageKey", "ReceivedUtc", "SenderAddress", "Subject",
                    "Layer", "Decision", "Reason", "Mode", "DecidedUtc", "tenant_id")
                VALUES ('11111111-1111-1111-1111-111111111111', '22222222-2222-2222-2222-222222222222', 'message-1',
                        '2026-10-18 09:00:00', 'sender@example.com', 'subject', 'rules', 'ignore', 'reason', 'live',
                        '2026-10-18 09:01:00', 'local');
                """);
            Assert.DoesNotContain("AttemptCount", ColumnNames(connection));

            // Act
            migrator.Migrate(SqliteUnderTest);

            // Assert: the earlier decision was screened once, under no recorded rules version.
            Assert.Equal("1|0", Single(connection, """SELECT "AttemptCount" || '|' || "RulesVersion" FROM "mail_desk_decisions" """));

            // The model matches the migrations.
            Assert.False(context.Database.HasPendingModelChanges());

            // The reversal keeps the row and removes both columns.
            migrator.Migrate(SqliteBefore);
            Assert.DoesNotContain("AttemptCount", ColumnNames(connection));
            Assert.DoesNotContain("RulesVersion", ColumnNames(connection));
            Assert.Equal("1", Single(connection, """SELECT count(*) FROM "mail_desk_decisions" """));
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
        command.CommandText = """SELECT name FROM pragma_table_info('mail_desk_decisions')""";
        using var reader = command.ExecuteReader();
        var names = new List<string>();
        while (reader.Read()) names.Add(reader.GetString(0));
        return names;
    }
}
