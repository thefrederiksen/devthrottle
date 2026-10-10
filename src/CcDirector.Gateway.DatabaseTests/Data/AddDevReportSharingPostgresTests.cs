using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Xunit;

namespace CcDirector.Gateway.Tests.Data;

/// <summary>
/// The PostgreSQL half of a dev report sent to a member of a team (devthrottle_internal#2309): <c>AddDevReportSharing</c>
/// applies to a real PostgreSQL database right after <c>AddFleetManagerLessons</c>, adds the nullable author column to
/// <c>dev_reports</c> without touching an existing report, keeps one recipient row per (team, report, member), and its Down
/// removes both tables and the column again - the reversal the pull request names.
///
/// GATING. [RequiresPostgresFact], the one rule for this project: it runs under scripts\test-database.ps1,
/// against the throwaway PostgreSQL that run built, and reports SKIPPED anywhere else. Skipped is not passed.
/// </summary>
public sealed class AddDevReportSharingPostgresTests
{
    private const string ConnectionEnvVar = "CC_GATEWAY_TEST_PG_CONNECTION";
    private const string MigrationBefore = "20261005184813_AddFleetManagerLessons";
    private const string MigrationUnderTest = "20261005234644_AddDevReportSharing";

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

    private static string SharingTablesPresent() => Scalar("""
        SELECT count(*) FROM information_schema.tables
        WHERE table_schema = 'gateway' AND table_name IN ('dev_report_recipients', 'dev_report_comments')
        """)!;

    private static string AuthorColumnPresent() => Scalar("""
        SELECT count(*) FROM information_schema.columns
        WHERE table_schema = 'gateway' AND table_name = 'dev_reports' AND column_name = 'AuthorSubject'
        """)!;

    private static string Recipient(string id, string member) => $"""
        INSERT INTO gateway.dev_report_recipients ("Id", tenant_id, "ReportId", "RecipientSubject", "SentBySubject", "SentAtUtc", "SentVersion")
        VALUES ('{id}', 'team-1', '7a000000-0000-4000-8000-000000000001', '{member}', 'sub-author',
            TIMESTAMPTZ '2026-10-05 01:00:00Z', 1);
        """;

    [RequiresPostgresFact]
    public void AddDevReportSharing_AppliesOnPostgres_KeepsOneRowPerRecipient_LeavesOldReportsAlone_AndItsDownRemovesItAll()
    {
        PostgresProofDatabase.GuardThrowawayDatabase();
        using (var ctx = NewContext())
            ctx.Database.EnsureDeleted();

        // A dev report written before the migration.
        using (var ctx = NewContext())
        {
            var all = ctx.Database.GetMigrations().ToList();
            Assert.Equal(MigrationBefore, all[all.IndexOf(MigrationUnderTest) - 1]);
            ctx.GetService<IMigrator>().Migrate(MigrationBefore);
        }
        Scalar("""
            INSERT INTO gateway.dev_reports ("Id", tenant_id, "SessionId", "Key", "Title", "Status", "Version",
                "PublishedAtUtc", "UpdatedAtUtc")
            VALUES ('7a000000-0000-4000-8000-000000000009', 'tenant-1', 's-old', 'C:\old.html', 'Old', 'done', 1,
                TIMESTAMPTZ '2026-09-01 10:00:00Z', TIMESTAMPTZ '2026-09-01 10:00:00Z');
            """);
        Assert.Equal("0", SharingTablesPresent());
        Assert.Equal("0", AuthorColumnPresent());

        using (var ctx = NewContext())
        {
            ctx.GetService<IMigrator>().Migrate(MigrationUnderTest);
            Assert.Equal(MigrationUnderTest, ctx.Database.GetAppliedMigrations().Last());
        }
        Assert.Equal("2", SharingTablesPresent());
        Assert.Equal("1", AuthorColumnPresent());
        Assert.Equal("null", Scalar("""SELECT coalesce("AuthorSubject", 'null') FROM gateway.dev_reports WHERE "SessionId" = 's-old'"""));

        Scalar(Recipient("7b000000-0000-4000-8000-000000000001", "sub-mike"));
        Scalar(Recipient("7b000000-0000-4000-8000-000000000002", "sub-ana"));
        var duplicate = Assert.Throws<PostgresException>(() => Scalar(Recipient("7b000000-0000-4000-8000-000000000003", "sub-mike")));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, duplicate.SqlState);

        // The reversal: back to AddFleetManagerLessons removes both tables and the column, and the old report survives.
        using (var ctx = NewContext())
        {
            ctx.GetService<IMigrator>().Migrate(MigrationBefore);
            Assert.Equal(MigrationBefore, ctx.Database.GetAppliedMigrations().Last());
        }
        Assert.Equal("0", SharingTablesPresent());
        Assert.Equal("0", AuthorColumnPresent());
        Assert.Equal("1", Scalar("""SELECT count(*) FROM gateway.dev_reports WHERE "SessionId" = 's-old'"""));

        using (var ctx = NewContext())
            ctx.Database.Migrate();
        Assert.Equal("2", SharingTablesPresent());
    }
}
