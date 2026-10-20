using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace CcDirector.Gateway.Tests.Data;

/// <summary>
/// The SQLite half of <c>AddMailDeskDecisionOutcomeTime</c> (Mail Desk, phase 4, section 2.3; review of 4274, finding 2):
/// <c>mail_desk_decisions</c> gains <c>OutcomeUtc</c>. It applies right after <c>AddMailDeskDecisionAttempts</c>, keeps
/// the decisions stored before it with no outcome time, and its Down removes the column and keeps the rows. The
/// PostgreSQL half is <c>MailDeskDecisionOutcomeTimePostgresTests</c>, run by scripts/test-database.ps1.
/// </summary>
public sealed class AddMailDeskDecisionOutcomeTimeMigrationTests
{
    private const string SqliteBefore = "20261020020444_AddMailDeskDecisionAttempts";
    private const string SqliteUnderTest = "20261020034316_AddMailDeskDecisionOutcomeTime";

    [Fact]
    public void AddMailDeskDecisionOutcomeTime_OnSqlite_KeepsAnEarlierDecisionWithNoOutcomeTime_AndItsDownRemovesTheColumn()
    {
        var directory = Path.Combine(Path.GetTempPath(), "cc-mail-desk-outcome-time-migration-" + Guid.NewGuid().ToString("N"));
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
                    "Layer", "Decision", "Reason", "Mode", "DecidedUtc", "tenant_id", "AttemptCount", "RulesVersion")
                VALUES ('11111111-1111-1111-1111-111111111111', '22222222-2222-2222-2222-222222222222', 'message-1',
                        '2026-10-18 09:00:00', 'sender@example.com', 'subject', 'allow', 'archive', 'reason', 'live',
                        '2026-10-18 09:01:00', 'local', 1, 0);
                """);
            Assert.DoesNotContain("OutcomeUtc", ColumnNames(connection));

            // Act
            migrator.Migrate(SqliteUnderTest);

            // Assert: the earlier decision has no outcome time; the model matches the migrations.
            Assert.Contains("OutcomeUtc", ColumnNames(connection));
            Assert.Equal("1", Single(connection, """SELECT count(*) FROM "mail_desk_decisions" WHERE "OutcomeUtc" IS NULL"""));
            Assert.False(context.Database.HasPendingModelChanges());

            // The reversal keeps the row and removes the column.
            migrator.Migrate(SqliteBefore);
            Assert.DoesNotContain("OutcomeUtc", ColumnNames(connection));
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
