using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace CcDirector.Gateway.Tests.Data;

/// <summary>
/// The SQLite half of <c>AddMailDeskRoutedOwnerColumns</c> (Mail Desk, phase 4, section 4.2): <c>mail_desk_routed</c>
/// gains <c>ReturnedUtc</c>, <c>ReturnedFrom</c>, <c>EarlierReturnedUtc</c>, <c>EarlierReturnedFrom</c>,
/// <c>EarlierReturnNote</c>, <c>OwnerUtc</c>, <c>ResolvedUtc</c>, <c>ResolvedBy</c> and <c>ResolvedReason</c>. It applies right after <c>AddMailDeskRoutedLiveColumns</c>; a row already handed to the owner
/// takes its hand time from when it last changed (a hand edit over what the tool generates, caught here if a
/// regeneration drops it), an open row takes none; and its Down removes the columns and keeps the rows. The PostgreSQL
/// half is <c>MailDeskOwnerPostgresTests</c>, run by scripts/test-database.ps1.
/// </summary>
public sealed class AddMailDeskRoutedOwnerColumnsMigrationTests
{
    private const string SqliteBefore = "20261020034452_AddMailDeskRoutedLiveColumns";
    private const string SqliteUnderTest = "20261020040253_AddMailDeskRoutedOwnerColumns";
    private const string Channel = "22222222-2222-2222-2222-222222222222";

    [Fact]
    public void AddMailDeskRoutedOwnerColumns_OnSqlite_GivesAHandedRowItsHandTime_AndItsDownKeepsTheRows()
    {
        var directory = Path.Combine(Path.GetTempPath(), "cc-mail-desk-owner-migration-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var connection = "Data Source=" + Path.Combine(directory, "gateway.db") + ";Pooling=False";
        try
        {
            var options = new DbContextOptionsBuilder<GatewayDbContext>().UseSqlite(connection).Options;
            using var context = new GatewayDbContext(options) { ActiveTenant = TenantId.Local.Value };
            var migrator = context.Database.GetService<IMigrator>();
            var all = context.Database.GetMigrations().ToList();
            Assert.Equal(SqliteBefore, all[all.IndexOf(SqliteUnderTest) - 1]);

            // Arrange: one row with the owner and one open, stored before the migration.
            migrator.Migrate(SqliteBefore);
            Execute(connection, $"""
                INSERT INTO "mail_desk_routed" ("Id", "DecisionId", "ChannelId", "Factory", "Seat", "State", "ReturnedCount",
                    "RerouteCount", "StartFailures", "OwnerReason", "CreatedUtc", "ChangedUtc", "tenant_id")
                VALUES ('33333333-3333-3333-3333-333333333333', '11111111-1111-1111-1111-111111111111', '{Channel}', 'mail-desk',
                        'mail-desk', 'returned', 2, 0, 0, 'returned twice: no', '2026-10-19 09:02:00', '2026-10-19 09:30:00', 'local'),
                       ('44444444-4444-4444-4444-444444444444', '55555555-5555-5555-5555-555555555555', '{Channel}', 'qa-books',
                        'clerk', 'open', 0, 0, 0, NULL, '2026-10-19 09:03:00', '2026-10-19 09:03:00', 'local');
                """);
            Assert.DoesNotContain("OwnerUtc", ColumnNames(connection));

            // Act
            migrator.Migrate(SqliteUnderTest);

            // Assert: the handed row's hand time is when it last changed; the open row has none; nothing else is set.
            Assert.Equal("returned|2026-10-19 09:30:00||||||||",
                Single(connection, """
                    SELECT "State" || '|' || ifnull("OwnerUtc", '') || '|' || ifnull("ReturnedUtc", '') || '|' || ifnull("ReturnedFrom", '')
                           || '|' || ifnull("EarlierReturnedUtc", '') || '|' || ifnull("EarlierReturnedFrom", '') || '|' || ifnull("EarlierReturnNote", '')
                           || '|' || ifnull("ResolvedUtc", '') || '|' || ifnull("ResolvedBy", '') || '|' || ifnull("ResolvedReason", '')
                    FROM "mail_desk_routed" WHERE "State" = 'returned'
                    """));
            Assert.Equal("", Single(connection, """SELECT ifnull("OwnerUtc", '') FROM "mail_desk_routed" WHERE "State" = 'open'"""));

            // The model matches the migrations.
            Assert.False(context.Database.HasPendingModelChanges());

            // The reversal keeps the rows and removes the columns.
            migrator.Migrate(SqliteBefore);
            var after = ColumnNames(connection);
            foreach (var column in new[] { "ReturnedUtc", "ReturnedFrom", "EarlierReturnedUtc", "EarlierReturnedFrom", "EarlierReturnNote", "OwnerUtc", "ResolvedUtc", "ResolvedBy", "ResolvedReason" })
                Assert.DoesNotContain(column, after);
            Assert.Equal("2", Single(connection, """SELECT count(*) FROM "mail_desk_routed" """));
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
