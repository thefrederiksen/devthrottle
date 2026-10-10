using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Xunit;

namespace CcDirector.Gateway.Tests.Data;

/// <summary>
/// The PostgreSQL half of a team member's answers on their Questions page (devthrottle_internal#2307):
/// <c>AddTeamQuestionAnswers</c> applies to a real PostgreSQL database right after <c>AddFactoryRegistry</c>, adds the
/// nullable <c>dev_report_items.AnswererSubject</c> (byte-ordinal "C", with its index), <c>dev_report_items.SourceVersion</c>
/// and <c>dev_report_comments.QuestionId</c> without touching an existing item, with the unique index that allows one person
/// one not-refused answer to a question (review F2) - which this proves refuses a second one on PostgreSQL - and its Down
/// removes all of it again: the reversal the pull request names.
///
/// GATING. [RequiresPostgresFact], the one rule for this project: it runs under scripts\test-database.ps1,
/// against the throwaway PostgreSQL that run built, and reports SKIPPED anywhere else. Skipped is not passed.
/// </summary>
public sealed class AddTeamQuestionAnswersPostgresTests
{
    private const string ConnectionEnvVar = "CC_GATEWAY_TEST_PG_CONNECTION";
    private const string MigrationBefore = "20261006140813_AddFactoryRegistry";
    private const string MigrationUnderTest = "20261006171258_AddTeamQuestionAnswers";

    private static string Connection => PostgresProofDatabase.Connection;

    private static GatewayDbContext NewContext()
    {
        var options = new DbContextOptionsBuilder<GatewayDbContext>()
            .UseNpgsql(Connection, npg =>
            {
                npg.MigrationsAssembly("CcDirector.Gateway.Migrations.Postgres");
                npg.MigrationsHistoryTable("__EFMigrationsHistory", "gateway");
            })
            .Options;
        return new GatewayDbContext(options) { ActiveTenant = TenantId.Local.Value };
    }

    private static string? Scalar(string sql)
    {
        using var connection = new NpgsqlConnection(Connection);
        connection.Open();
        using var command = new NpgsqlCommand(sql, connection);
        return command.ExecuteScalar()?.ToString();
    }

    private static string ColumnsPresent() => Scalar("""
        SELECT count(*) FROM information_schema.columns
        WHERE table_schema = 'gateway' AND ((table_name = 'dev_report_items' AND column_name IN ('AnswererSubject', 'SourceVersion'))
                                         OR (table_name = 'dev_report_comments' AND column_name = 'QuestionId'))
        """)!;

    /// <summary>Mike's answer to one question, in the state given.</summary>
    private static string Member(string clientItemId, string status) => $"""
        INSERT INTO gateway.dev_report_items ("Id", tenant_id, "ReportId", "SessionId", "ClientItemId", "Kind", "Text", "QuestionId",
            "Question", "OptionValue", "OptionLabel", "Comment", "Status", "StatusLabel", "Sequence", "SenderKind", "SentAtUtc",
            "AnswererSubject", "SourceVersion")
        VALUES ('{Guid.NewGuid():D}', 'team-1', '7a000000-0000-4000-8000-000000000009', 's-1', '{clientItemId}', 'answer', '', 'deploy',
            'When?', 'tonight', 'Tonight', '', '{status}', 'x', 2, 'device', TIMESTAMPTZ '2026-10-06 10:00:00Z', 'sub-mike', 1);
        """;

    [RequiresPostgresFact]
    public void AddTeamQuestionAnswers_AppliesOnPostgres_LeavesOldItemsAlone_RefusesASecondAnswer_AndItsDownRemovesItAll()
    {
        PostgresProofDatabase.GuardThrowawayDatabase();
        using (var ctx = NewContext())
            ctx.Database.EnsureDeleted();

        using (var ctx = NewContext())
        {
            var all = ctx.Database.GetMigrations().ToList();
            Assert.Equal(MigrationBefore, all[all.IndexOf(MigrationUnderTest) - 1]);
            ctx.GetService<IMigrator>().Migrate(MigrationBefore);
        }
        // An owner's answer stored before the migration.
        Scalar("""
            INSERT INTO gateway.dev_report_items ("Id", tenant_id, "ReportId", "SessionId", "ClientItemId", "Kind", "Text", "QuestionId",
                "Question", "OptionValue", "OptionLabel", "Comment", "Status", "StatusLabel", "Sequence", "SenderKind", "SentAtUtc")
            VALUES ('7c000000-0000-4000-8000-000000000001', 'tenant-1', '7a000000-0000-4000-8000-000000000009', 's-old', 'a-old',
                'answer', '', 'deploy', 'When?', 'tonight', 'Tonight', 'owner words', 'delivered', 'Delivered', 1, 'device',
                TIMESTAMPTZ '2026-09-01 10:00:00Z');
            """);
        Assert.Equal("0", ColumnsPresent());

        using (var ctx = NewContext())
        {
            ctx.GetService<IMigrator>().Migrate(MigrationUnderTest);
            Assert.Equal(MigrationUnderTest, ctx.Database.GetAppliedMigrations().Last());
        }
        Assert.Equal("3", ColumnsPresent());
        Assert.Equal("null", Scalar("""SELECT coalesce("AnswererSubject", 'null') FROM gateway.dev_report_items WHERE "ClientItemId" = 'a-old'"""));
        Assert.Equal("C", Scalar("""
            SELECT collation_name FROM information_schema.columns
            WHERE table_schema = 'gateway' AND table_name = 'dev_report_items' AND column_name = 'AnswererSubject'
            """));
        Assert.Equal("1", Scalar("""SELECT count(*) FROM pg_indexes WHERE schemaname = 'gateway' AND indexname = 'IX_dev_report_items_tenant_id_AnswererSubject'"""));
        // One answer per person per question, in the database: a second one not refused cannot be written; a refused one can.
        Scalar(Member("a-m1", "held"));
        var duplicate = Assert.Throws<PostgresException>(() => Scalar(Member("a-m2", "delivered")));
        Assert.Equal((PostgresErrorCodes.UniqueViolation, "IX_dev_report_items_one_member_answer"), (duplicate.SqlState, duplicate.ConstraintName));
        Scalar(Member("a-m3", "refused"));
        Scalar("""DELETE FROM gateway.dev_report_items WHERE "AnswererSubject" IS NOT NULL""");

        // The reversal: back to AddFactoryRegistry removes the columns and the index, and the old answer survives with its words.
        using (var ctx = NewContext())
        {
            ctx.GetService<IMigrator>().Migrate(MigrationBefore);
            Assert.Equal(MigrationBefore, ctx.Database.GetAppliedMigrations().Last());
        }
        Assert.Equal("0", ColumnsPresent());
        Assert.Equal("owner words", Scalar("""SELECT "Comment" FROM gateway.dev_report_items WHERE "ClientItemId" = 'a-old'"""));

        using (var ctx = NewContext())
            ctx.Database.Migrate();
        Assert.Equal("3", ColumnsPresent());
    }
}
