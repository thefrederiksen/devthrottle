using CcDirector.Gateway.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace CcDirector.Gateway.Tests.Teams.Reports;

/// <summary>
/// The SQLite half of a dev report sent to a member of a team (devthrottle_internal#2309): it applies right after the
/// Mentor's migration, adds exactly the two tables and the nullable author column, leaves an existing report alone, and its
/// Down removes all three again - the reversal the pull request names. The PostgreSQL half is
/// <c>AddDevReportSharingPostgresTests</c> in the Gateway suite.
/// </summary>
public sealed class AddDevReportSharingMigrationTests
{
    private const string MigrationBefore = "20261005184745_AddFleetManagerLessons";
    private const string MigrationUnderTest = "20261005234625_AddDevReportSharing";

    [Fact]
    public void AddDevReportSharing_UpThenDown_AddsAndRemovesTheTwoTablesAndTheAuthorColumn_AndKeepsOldReports()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using var ctx = new GatewayDbContext(new DbContextOptionsBuilder<GatewayDbContext>().UseSqlite(connection).Options);
        var migrator = ctx.GetService<IMigrator>();

        migrator.Migrate(MigrationBefore);
        var before = Tables(connection);
        Execute(connection, """
            INSERT INTO "dev_reports" ("Id", "tenant_id", "SessionId", "Key", "Title", "Status", "Version", "PublishedAtUtc", "UpdatedAtUtc")
            VALUES ('7a000000-0000-4000-8000-000000000009', 'tenant-1', 's-old', 'C:\old.html', 'Old', 'done', 1,
                '2026-09-01 10:00:00', '2026-09-01 10:00:00');
            """);
        Assert.DoesNotContain("AuthorSubject", Columns(connection, "dev_reports"));

        migrator.Migrate(MigrationUnderTest);
        var after = Tables(connection);
        Assert.Equal(new[] { "dev_report_comments", "dev_report_recipients" }, after.Except(before).OrderBy(t => t, StringComparer.Ordinal));
        Assert.Empty(before.Except(after));
        Assert.Contains("AuthorSubject", Columns(connection, "dev_reports"));
        Assert.Equal("null", Scalar(connection, """SELECT IFNULL("AuthorSubject", 'null') FROM "dev_reports" WHERE "SessionId" = 's-old'"""));

        migrator.Migrate(MigrationBefore);
        Assert.Equal(before, Tables(connection));
        Assert.DoesNotContain("AuthorSubject", Columns(connection, "dev_reports"));
        Assert.Equal("1", Scalar(connection, """SELECT count(*) FROM "dev_reports" WHERE "SessionId" = 's-old'"""));
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static string Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture)!;
    }

    private static List<string> Columns(SqliteConnection connection, string table)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT name FROM pragma_table_info('{table}')";
        using var reader = command.ExecuteReader();
        var names = new List<string>();
        while (reader.Read()) names.Add(reader.GetString(0));
        return names;
    }

    private static List<string> Tables(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' ORDER BY name";
        using var reader = command.ExecuteReader();
        var names = new List<string>();
        while (reader.Read()) names.Add(reader.GetString(0));
        return names;
    }
}
