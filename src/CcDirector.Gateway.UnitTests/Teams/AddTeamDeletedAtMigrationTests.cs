using CcDirector.Gateway.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace CcDirector.Gateway.Tests.Teams;

/// <summary>
/// The SQLite half of the deleted-team migration (Teams v1, rename, delete and leave): it applies right after the
/// factory purpose, adds exactly the two nullable columns to <c>teams</c>, keeps every team that was already there as a
/// live team, and its Down removes the columns again without losing a team - the reversal the pull request names.
/// The PostgreSQL half is <c>AddTeamDeletedAtPostgresTests</c> in the Gateway suite.
/// </summary>
public sealed class AddTeamDeletedAtMigrationTests
{
    private const string MigrationBefore = "20261008200000_AddFactoryPurpose";
    private const string MigrationUnderTest = "20261008211952_AddTeamDeletedAt";

    [Fact]
    public void AddTeamDeletedAt_UpThenDown_AddsAndRemovesExactlyTheTwoColumns_AndKeepsEveryTeam()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using var ctx = new GatewayDbContext(new DbContextOptionsBuilder<GatewayDbContext>().UseSqlite(connection).Options);
        var migrator = ctx.GetService<IMigrator>();
        var all = ctx.Database.GetMigrations().ToList();
        Assert.Equal(MigrationBefore, all[all.IndexOf(MigrationUnderTest) - 1]);

        migrator.Migrate(MigrationBefore);
        var before = Columns(connection);
        Execute(connection, "INSERT INTO teams (id, name, created_at_utc) VALUES ('team-1', 'Acme', '2026-10-01 09:00:00')");

        migrator.Migrate(MigrationUnderTest);
        var after = Columns(connection);
        Assert.Equal(new[] { "deleted_at_utc", "deleted_by_account_subject" }, after.Except(before).OrderBy(c => c, StringComparer.Ordinal));
        Assert.Empty(before.Except(after));
        // A team that existed before the migration is a live team after it.
        Assert.Equal("1", Scalar(connection, "SELECT count(*) FROM teams WHERE id = 'team-1' AND deleted_at_utc IS NULL"));

        migrator.Migrate(MigrationBefore);
        Assert.Equal(before, Columns(connection));
        Assert.Equal("Acme", Scalar(connection, "SELECT name FROM teams WHERE id = 'team-1'"));
    }

    private static List<string> Columns(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM pragma_table_info('teams') ORDER BY name";
        using var reader = command.ExecuteReader();
        var names = new List<string>();
        while (reader.Read()) names.Add(reader.GetString(0));
        return names;
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static string? Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar()?.ToString();
    }
}
