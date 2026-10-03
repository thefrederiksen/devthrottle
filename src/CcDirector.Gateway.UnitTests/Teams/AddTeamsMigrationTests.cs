using CcDirector.Gateway.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace CcDirector.Gateway.Tests.Teams;

/// <summary>
/// The SQLite half of the teams migration (devthrottle_internal#2300): it applies from empty, adds exactly the two
/// tables, and its Down removes them again - the reversal the pull request names. The PostgreSQL half is
/// <c>AddTeamsPostgresTests</c> in the Gateway suite.
/// </summary>
public sealed class AddTeamsMigrationTests
{
    private const string MigrationBefore = "20260928124401_AddFactoryMemoryNotes";
    private const string MigrationUnderTest = "20261003182410_AddTeams";

    [Fact]
    public void AddTeams_UpThenDown_AddsAndRemovesExactlyTheTwoTeamTables()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using var ctx = new GatewayDbContext(new DbContextOptionsBuilder<GatewayDbContext>().UseSqlite(connection).Options);
        var migrator = ctx.GetService<IMigrator>();

        migrator.Migrate(MigrationBefore);
        var before = Tables(connection);
        Assert.DoesNotContain("teams", before);

        migrator.Migrate(MigrationUnderTest);
        var after = Tables(connection);
        Assert.Equal(new[] { "team_members", "teams" }, after.Except(before).OrderBy(t => t, StringComparer.Ordinal));
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
