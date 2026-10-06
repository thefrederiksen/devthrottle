using CcDirector.Gateway.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace CcDirector.Gateway.Tests.Teams.Questions;

/// <summary>
/// The SQLite half of a team member's answers on their Questions page (devthrottle_internal#2307): the migration applies
/// right after the dev report sharing one, adds exactly the two nullable columns - who answered an item, and which
/// question a comment is about - leaves an owner's existing answer and an existing comment alone, and its Down removes
/// both columns again: the reversal the pull request names. The PostgreSQL half is
/// <c>AddTeamQuestionAnswersPostgresTests</c> in the Gateway suite.
/// </summary>
public sealed class AddTeamQuestionAnswersMigrationTests
{
    private const string MigrationBefore = "20261005234625_AddDevReportSharing";
    private const string MigrationUnderTest = "20261006025431_AddTeamQuestionAnswers";

    [Fact]
    public void AddTeamQuestionAnswers_UpThenDown_AddsAndRemovesTheTwoColumns_AndKeepsOldItemsAndComments()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using var ctx = new GatewayDbContext(new DbContextOptionsBuilder<GatewayDbContext>().UseSqlite(connection).Options);
        var migrator = ctx.GetService<IMigrator>();

        var all = ctx.Database.GetMigrations().ToList();
        Assert.Equal(MigrationBefore, all[all.IndexOf(MigrationUnderTest) - 1]);
        migrator.Migrate(MigrationBefore);
        Execute(connection, """
            INSERT INTO "dev_report_items" ("Id", "tenant_id", "ReportId", "SessionId", "ClientItemId", "Kind", "Text", "QuestionId",
                "Question", "OptionValue", "OptionLabel", "Comment", "Status", "StatusLabel", "Sequence", "SenderKind", "SentAtUtc")
            VALUES ('7c000000-0000-4000-8000-000000000001', 'tenant-1', '7a000000-0000-4000-8000-000000000009', 's-old', 'a-old',
                'answer', '', 'deploy', 'When?', 'tonight', 'Tonight', 'owner words', 'delivered', 'Delivered', 1, 'device',
                '2026-09-01 10:00:00');
            INSERT INTO "dev_report_comments" ("Id", "tenant_id", "ReportId", "FromSubject", "ToSubject", "Text", "AtUtc")
            VALUES ('7d000000-0000-4000-8000-000000000001', 'team-1', '7a000000-0000-4000-8000-000000000009', 'sub-mike',
                'sub-alice', 'an old comment', '2026-10-05 10:00:00');
            """);
        Assert.DoesNotContain("AnswererSubject", Columns(connection, "dev_report_items"));
        Assert.DoesNotContain("QuestionId", Columns(connection, "dev_report_comments"));

        migrator.Migrate(MigrationUnderTest);
        Assert.Contains("AnswererSubject", Columns(connection, "dev_report_items"));
        Assert.Contains("QuestionId", Columns(connection, "dev_report_comments"));
        Assert.Equal("null", Scalar(connection, """SELECT IFNULL("AnswererSubject", 'null') FROM "dev_report_items" WHERE "ClientItemId" = 'a-old'"""));
        Assert.Equal("null", Scalar(connection, """SELECT IFNULL("QuestionId", 'null') FROM "dev_report_comments" WHERE "Text" = 'an old comment'"""));
        Assert.Equal("1", Scalar(connection, """SELECT count(*) FROM sqlite_master WHERE type = 'index' AND name = 'IX_dev_report_items_tenant_id_AnswererSubject'"""));

        migrator.Migrate(MigrationBefore);
        Assert.DoesNotContain("AnswererSubject", Columns(connection, "dev_report_items"));
        Assert.DoesNotContain("QuestionId", Columns(connection, "dev_report_comments"));
        Assert.Equal("owner words", Scalar(connection, """SELECT "Comment" FROM "dev_report_items" WHERE "ClientItemId" = 'a-old'"""));
        Assert.Equal("1", Scalar(connection, """SELECT count(*) FROM "dev_report_comments" WHERE "Text" = 'an old comment'"""));
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
}
