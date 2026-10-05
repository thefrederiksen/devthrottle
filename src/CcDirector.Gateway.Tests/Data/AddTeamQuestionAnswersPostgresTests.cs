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
/// <c>AddTeamQuestionAnswers</c> applies to a real PostgreSQL database right after <c>AddDevReportSharing</c>, adds the
/// nullable <c>dev_report_items.AnswererSubject</c> (byte-ordinal "C", with its index) and
/// <c>dev_report_comments.QuestionId</c> without touching an existing item, and its Down removes both again - the reversal
/// the pull request names.
///
/// GATING. Like the other PostgreSQL proofs, gated on <c>CC_GATEWAY_TEST_PG_CONNECTION</c>, which the parked run sets to
/// a throwaway database it builds and destroys. It reports SKIPPED when unset. Skipped is not passed.
/// </summary>
public sealed class AddTeamQuestionAnswersPostgresTests
{
    private const string ConnectionEnvVar = "CC_GATEWAY_TEST_PG_CONNECTION";
    private const string MigrationBefore = "20261005170330_AddDevReportSharing";
    private const string MigrationUnderTest = "20261005203403_AddTeamQuestionAnswers";

    private sealed class RequiresPostgresFactAttribute : FactAttribute
    {
        public RequiresPostgresFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ConnectionEnvVar)))
                Skip = $"Set {ConnectionEnvVar} to a Postgres connection string to run the real-Postgres team question answers proof.";
        }
    }

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
        WHERE table_schema = 'gateway' AND ((table_name = 'dev_report_items' AND column_name = 'AnswererSubject')
                                         OR (table_name = 'dev_report_comments' AND column_name = 'QuestionId'))
        """)!;

    [RequiresPostgresFact]
    public void AddTeamQuestionAnswers_AppliesOnPostgres_LeavesOldItemsAlone_AndItsDownRemovesBothColumns()
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
        Assert.Equal("2", ColumnsPresent());
        Assert.Equal("null", Scalar("""SELECT coalesce("AnswererSubject", 'null') FROM gateway.dev_report_items WHERE "ClientItemId" = 'a-old'"""));
        Assert.Equal("C", Scalar("""
            SELECT collation_name FROM information_schema.columns
            WHERE table_schema = 'gateway' AND table_name = 'dev_report_items' AND column_name = 'AnswererSubject'
            """));
        Assert.Equal("1", Scalar("""SELECT count(*) FROM pg_indexes WHERE schemaname = 'gateway' AND indexname = 'IX_dev_report_items_tenant_id_AnswererSubject'"""));

        // The reversal: back to AddDevReportSharing removes both columns, and the old answer survives with its words.
        using (var ctx = NewContext())
        {
            ctx.GetService<IMigrator>().Migrate(MigrationBefore);
            Assert.Equal(MigrationBefore, ctx.Database.GetAppliedMigrations().Last());
        }
        Assert.Equal("0", ColumnsPresent());
        Assert.Equal("owner words", Scalar("""SELECT "Comment" FROM gateway.dev_report_items WHERE "ClientItemId" = 'a-old'"""));

        using (var ctx = NewContext())
            ctx.Database.Migrate();
        Assert.Equal("2", ColumnsPresent());
    }
}
