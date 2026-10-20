using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace CcDirector.Gateway.Tests.Data;

/// <summary>
/// The SQLite half of <c>AddMailDeskRoutedLiveColumns</c> (Mail Desk, phase 4, section 3.1): <c>mail_desk_routed</c>
/// gains <c>ChannelId</c>, <c>RerouteCount</c>, <c>StartFailures</c>, <c>StartName</c>, <c>StartedUtc</c>,
/// <c>OwnerReason</c> and <c>DoneNote</c>, and its (tenant, decision) index becomes unique. It applies right after the
/// branch's newest migration, <c>AddMailDeskDecisionOutcomeTime</c>; a row stored before it keeps its fields, takes its
/// channel from its decision (a hand edit over what the tool generates, caught here if a regeneration drops it) and
/// counts no reroutes or failures; a second row for one decision is refused afterwards; and its Down removes the
/// columns and keeps the rows. The PostgreSQL half is <c>MailDeskRoutedPostgresTests</c>, run by
/// scripts/test-database.ps1.
/// </summary>
public sealed class AddMailDeskRoutedLiveColumnsMigrationTests
{
    private const string SqliteBefore = "20261020034316_AddMailDeskDecisionOutcomeTime";
    private const string SqliteUnderTest = "20261020034452_AddMailDeskRoutedLiveColumns";
    private const string Channel = "22222222-2222-2222-2222-222222222222";
    private const string Decision = "11111111-1111-1111-1111-111111111111";

    [Fact]
    public void AddMailDeskRoutedLiveColumns_OnSqlite_FillsTheChannelFromTheDecision_RefusesASecondRow_AndItsDownKeepsTheRow()
    {
        var directory = Path.Combine(Path.GetTempPath(), "cc-mail-desk-routed-migration-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var connection = "Data Source=" + Path.Combine(directory, "gateway.db") + ";Pooling=False";
        try
        {
            var options = new DbContextOptionsBuilder<GatewayDbContext>().UseSqlite(connection).Options;
            using var context = new GatewayDbContext(options) { ActiveTenant = TenantId.Local.Value };
            var migrator = context.Database.GetService<IMigrator>();
            var all = context.Database.GetMigrations().ToList();
            Assert.Equal(SqliteBefore, all[all.IndexOf(SqliteUnderTest) - 1]);

            // Arrange: one decision and one routed row stored before the migration.
            migrator.Migrate(SqliteBefore);
            Execute(connection, $"""
                INSERT INTO "mail_desk_decisions" ("Id", "ChannelId", "MessageKey", "ReceivedUtc", "SenderAddress", "Subject",
                    "Layer", "Decision", "RouteFactory", "RouteSeat", "Reason", "Mode", "DecidedUtc", "RulesVersion", "AttemptCount", "tenant_id")
                VALUES ('{Decision}', '{Channel}', 'message-1', '2026-10-19 09:00:00', 'sender@example.com', 'subject',
                        'rule', 'route', 'qa-books', 'clerk', 'invoices', 'live', '2026-10-19 09:01:00', 0, 1, 'local');
                INSERT INTO "mail_desk_routed" ("Id", "DecisionId", "Factory", "Seat", "SessionId", "State", "ReturnNote",
                    "ReturnedCount", "CreatedUtc", "ChangedUtc", "tenant_id")
                VALUES ('33333333-3333-3333-3333-333333333333', '{Decision}', 'qa-books', 'clerk', NULL, 'open', NULL, 0,
                        '2026-10-19 09:02:00', '2026-10-19 09:02:00', 'local');
                """);
            Assert.DoesNotContain("ChannelId", ColumnNames(connection));

            // Act
            migrator.Migrate(SqliteUnderTest);

            // Assert: the row keeps its fields, takes its decision's channel, and counts nothing yet.
            Assert.Equal($"{Channel}|0|0|||||qa-books|clerk|open",
                Single(connection, """
                    SELECT "ChannelId" || '|' || "RerouteCount" || '|' || "StartFailures" || '|' || ifnull("StartName", '') || '|' ||
                           ifnull("StartedUtc", '') || '|' || ifnull("OwnerReason", '') || '|' || ifnull("DoneNote", '') || '|' ||
                           "Factory" || '|' || "Seat" || '|' || "State" FROM "mail_desk_routed"
                    """));

            // One routed row per decision.
            var second = Assert.Throws<SqliteException>(() => Execute(connection, $"""
                INSERT INTO "mail_desk_routed" ("Id", "DecisionId", "ChannelId", "Factory", "Seat", "State", "ReturnedCount",
                    "RerouteCount", "StartFailures", "CreatedUtc", "ChangedUtc", "tenant_id")
                VALUES ('44444444-4444-4444-4444-444444444444', '{Decision}', '{Channel}', 'qa-books', 'clerk', 'open', 0, 0, 0,
                        '2026-10-19 09:03:00', '2026-10-19 09:03:00', 'local');
                """));
            Assert.Contains("UNIQUE", second.Message);

            // The model matches the migrations.
            Assert.False(context.Database.HasPendingModelChanges());

            // The reversal keeps the row and removes the columns.
            migrator.Migrate(SqliteBefore);
            var after = ColumnNames(connection);
            foreach (var column in new[] { "ChannelId", "RerouteCount", "StartFailures", "StartName", "StartedUtc", "OwnerReason", "DoneNote" })
                Assert.DoesNotContain(column, after);
            Assert.Equal("1", Single(connection, """SELECT count(*) FROM "mail_desk_routed" """));
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
        command.CommandText = """SELECT name FROM pragma_table_info('mail_desk_routed')""";
        using var reader = command.ExecuteReader();
        var names = new List<string>();
        while (reader.Read()) names.Add(reader.GetString(0));
        return names;
    }
}
