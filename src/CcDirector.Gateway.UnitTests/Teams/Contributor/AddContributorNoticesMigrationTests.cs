using CcDirector.Gateway.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace CcDirector.Gateway.Tests.Teams.Contributor;

/// <summary>
/// The SQLite half of the record of what a contributor has not been told about yet, and of each person's choice of how
/// to be told (Team implementation piece 4.4): it applies right after the migration before it, adds exactly the two
/// tables, refuses a second choice for one person, and its Down removes both again. The PostgreSQL half is
/// <c>AddContributorNoticesPostgresTests</c>.
/// </summary>
public sealed class AddContributorNoticesMigrationTests
{
    private const string MigrationBefore = "20261109122437_AddFactoryContributors";
    private const string MigrationUnderTest = "20261109230841_AddContributorNotices";

    [Fact]
    public void AddContributorNotices_UpThenDown_AddsAndRemovesTheTwoTables()
    {
        // Arrange
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using var ctx = new GatewayDbContext(new DbContextOptionsBuilder<GatewayDbContext>().UseSqlite(connection).Options);
        var migrator = ctx.GetService<IMigrator>();
        var all = ctx.Database.GetMigrations().ToList();
        Assert.Equal(MigrationBefore, all[all.IndexOf(MigrationUnderTest) - 1]);
        migrator.Migrate(MigrationBefore);
        var before = Tables(connection);

        // Act
        migrator.Migrate(MigrationUnderTest);

        // Assert
        Assert.Equal(new[] { "contributor_notice_choices", "contributor_notices" }, Tables(connection).Except(before));
        Execute(connection,
            "INSERT INTO \"contributor_notices\" (\"Id\", \"tenant_id\", \"RecipientSubject\", \"Kind\", \"CreatedAtUtc\", \"FailedTries\") " +
            "VALUES ('7b000000-0000-4000-8000-000000000001', 'team-1', 'sub-mike', 'report', '2026-11-09 10:00:00', 0);");
        Execute(connection, ChoiceInsert("7b000000-0000-4000-8000-000000000002"));
        Assert.Throws<SqliteException>(() => Execute(connection, ChoiceInsert("7b000000-0000-4000-8000-000000000003")));

        migrator.Migrate(MigrationBefore);
        Assert.Equal(before, Tables(connection));
    }

    private static string ChoiceInsert(string id) =>
        "INSERT INTO \"contributor_notice_choices\" (\"Id\", \"tenant_id\", \"Mode\", \"DailyHour\", \"UpdatedAtUtc\") " +
        $"VALUES ('{id}', 'tenant-mike', 'daily', 8, '2026-11-09 10:00:00');";

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
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
