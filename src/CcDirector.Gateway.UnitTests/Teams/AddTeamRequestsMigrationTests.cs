using CcDirector.Gateway.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace CcDirector.Gateway.Tests.Teams;

/// <summary>
/// The SQLite half of the team requests migration (devthrottle_internal#2308): it applies right after the message
/// links, adds exactly the two tables, and its Down removes them again - the reversal the pull request names.
/// The PostgreSQL half is <c>AddTeamRequestsPostgresTests</c> in the Gateway suite.
/// </summary>
public sealed class AddTeamRequestsMigrationTests
{
    private const string MigrationBefore = "20261005031703_AddFleetMessageLinks";
    private const string MigrationUnderTest = "20261005124413_AddTeamRequests";

    [Fact]
    public void AddTeamRequests_UpThenDown_AddsAndRemovesExactlyTheTwoRequestTables()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using var ctx = new GatewayDbContext(new DbContextOptionsBuilder<GatewayDbContext>().UseSqlite(connection).Options);
        var migrator = ctx.GetService<IMigrator>();
        var all = ctx.Database.GetMigrations().ToList();
        Assert.Equal(MigrationBefore, all[all.IndexOf(MigrationUnderTest) - 1]);

        migrator.Migrate(MigrationBefore);
        var before = Tables(connection);
        Assert.DoesNotContain("team_requests", before);

        migrator.Migrate(MigrationUnderTest);
        var after = Tables(connection);
        Assert.Equal(new[] { "team_request_changes", "team_requests" }, after.Except(before).OrderBy(t => t, StringComparer.Ordinal));
        Assert.Empty(before.Except(after));

        migrator.Migrate(MigrationBefore);
        Assert.Equal(before, Tables(connection));
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
