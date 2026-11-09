using CcDirector.Gateway.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace CcDirector.Gateway.Tests.Teams.Contributor;

/// <summary>
/// The SQLite half of the record of which factory was sent to which contributor (Team implementation piece 4.1): it
/// applies right after the migration before it, adds exactly the one table, refuses a second row for the same factory and
/// contributor, and its Down removes the table again. The PostgreSQL half is <c>AddFactoryContributorsPostgresTests</c>.
/// </summary>
public sealed class AddFactoryContributorsMigrationTests
{
    private const string MigrationBefore = "20261109062935_AddDevReportItemAnswererRole";
    private const string MigrationUnderTest = "20261109103816_AddFactoryContributors";

    [Fact]
    public void AddFactoryContributors_UpThenDown_AddsAndRemovesTheOneTable()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using var ctx = new GatewayDbContext(new DbContextOptionsBuilder<GatewayDbContext>().UseSqlite(connection).Options);
        var migrator = ctx.GetService<IMigrator>();
        var all = ctx.Database.GetMigrations().ToList();
        Assert.Equal(MigrationBefore, all[all.IndexOf(MigrationUnderTest) - 1]);

        migrator.Migrate(MigrationBefore);
        var before = Tables(connection);

        migrator.Migrate(MigrationUnderTest);
        Assert.Equal(new[] { "factory_contributors" }, Tables(connection).Except(before));
        Execute(connection, Insert("7a000000-0000-4000-8000-000000000001"));
        Assert.Throws<SqliteException>(() => Execute(connection, Insert("7a000000-0000-4000-8000-000000000002")));

        migrator.Migrate(MigrationBefore);
        Assert.Equal(before, Tables(connection));
    }

    private static string Insert(string id) => $"""
        INSERT INTO "factory_contributors" ("Id", "tenant_id", "Factory", "ContributorSubject", "SharedBySubject", "SharedAtUtc")
        VALUES ('{id}', 'team-1', 'website', 'sub-mike', 'sub-owner', '2026-11-09 10:00:00');
        """;

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
