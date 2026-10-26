using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Xunit;

namespace CcDirector.Gateway.Tests.Data;

/// <summary>
/// The PostgreSQL half of a later run reading the owner's answers (Factories Run Alone): <c>AddDevReportItemActedOn</c>
/// and then <c>AddDevReportActionClaimsAndSeatIdentity</c> apply to a real PostgreSQL database right after
/// <c>AddAlertMarks</c>. Together they add the acted-on and claim columns to <c>dev_report_items</c> and the factory and
/// seat columns to <c>dev_reports</c>, all nullable, byte-collated where they hold an identity, and leave a report and an
/// answer stored before them untouched. The model then reads and writes both tables - the exact queries that would fail
/// with "column does not exist" on a hosted Gateway whose PostgreSQL set lacked the migration. Their Downs remove the
/// columns again and keep the rows.
///
/// GATING. [RequiresPostgresFact], the one rule for this project: it runs under scripts\test-database.ps1,
/// against the throwaway PostgreSQL that run built, and reports SKIPPED anywhere else. Skipped is not passed.
/// </summary>
public sealed class AddDevReportActionClaimsAndSeatIdentityPostgresTests
{
    private const string MigrationBefore = "20261025095255_AddAlertMarks";
    private const string ActedOnMigration = "20261026131404_AddDevReportItemActedOn";
    private const string MigrationUnderTest = "20261026171237_AddDevReportActionClaimsAndSeatIdentity";

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

    /// <summary>Each new column that exists, as name:collation, in name order ("-" for no collation).</summary>
    private static string NewColumns() => Scalar("""
        SELECT coalesce(string_agg(column_name || ':' || coalesce(collation_name, '-'), ',' ORDER BY column_name), '')
        FROM information_schema.columns
        WHERE table_schema = 'gateway'
          AND ((table_name = 'dev_reports' AND column_name IN ('Factory', 'FactorySeat'))
            OR (table_name = 'dev_report_items' AND column_name IN ('ActedOnAtUtc', 'ActedOnSessionId',
                'ActionClaimedAtUtc', 'ActionClaimedSessionId')))
        """)!;

    [RequiresPostgresFact]
    public void AddDevReportActionClaimsAndSeatIdentity_AppliesOnPostgres_TheModelReadsAndWritesIt_AndItsDownRemovesIt()
    {
        PostgresProofDatabase.GuardThrowawayDatabase();
        using (var ctx = NewContext())
            ctx.Database.EnsureDeleted();

        using (var ctx = NewContext())
        {
            var all = ctx.Database.GetMigrations().ToList();
            Assert.Equal(MigrationBefore, all[all.IndexOf(ActedOnMigration) - 1]);
            Assert.Equal(ActedOnMigration, all[all.IndexOf(MigrationUnderTest) - 1]);
            ctx.GetService<IMigrator>().Migrate(MigrationBefore);
        }
        // A report and the owner's answer to it, stored before either migration.
        Scalar("""
            INSERT INTO gateway.dev_reports ("Id", tenant_id, "SessionId", "Key", "Title", "Status", "Version",
                "PublishedAtUtc", "UpdatedAtUtc")
            VALUES ('7c000000-0000-4000-8000-000000000001', 'local', 's-old', '/home/owner/report.html', 'Old', 'done', 1,
                TIMESTAMPTZ '2026-10-25 10:00:00Z', TIMESTAMPTZ '2026-10-25 10:00:00Z');
            INSERT INTO gateway.dev_report_items ("Id", tenant_id, "ReportId", "SessionId", "ClientItemId", "Kind",
                "SenderKind", "Status", "StatusLabel", "Question", "QuestionId", "OptionLabel", "OptionValue", "Comment",
                "Text", "Sequence", "SentAtUtc")
            VALUES ('7d000000-0000-4000-8000-000000000001', 'local', '7c000000-0000-4000-8000-000000000001', 's-old',
                'item-1', 'answer', 'owner', 'saved', 'Saved', 'Ship it?', 'q1', 'Yes', 'yes', '', '', 1,
                TIMESTAMPTZ '2026-10-25 11:00:00Z');
            """);
        Assert.Equal("", NewColumns());

        using (var ctx = NewContext())
        {
            ctx.GetService<IMigrator>().Migrate(MigrationUnderTest);
            Assert.Equal(MigrationUnderTest, ctx.Database.GetAppliedMigrations().Last());
        }
        Assert.Equal(
            "ActedOnAtUtc:-,ActedOnSessionId:C,ActionClaimedAtUtc:-,ActionClaimedSessionId:C,Factory:C,FactorySeat:C",
            NewColumns());

        // The model's queries over both tables now run, and the rows stored before read back with no claim and no seat.
        using (var ctx = NewContext())
        {
            ctx.GetService<IMigrator>().Migrate();
            Assert.False(ctx.Database.HasPendingModelChanges());

            var report = ctx.DevReports.Single();
            Assert.Null(report.Factory);
            Assert.Null(report.FactorySeat);
            var item = ctx.DevReportItems.Single();
            Assert.Null(item.ActionClaimedSessionId);
            Assert.Null(item.ActedOnAtUtc);

            report.Factory = "factory-a";
            report.FactorySeat = "seat-1";
            item.ActionClaimedSessionId = "s-new";
            item.ActionClaimedAtUtc = new DateTime(2026, 10, 26, 9, 0, 0, DateTimeKind.Utc);
            ctx.SaveChanges();
        }
        using (var ctx = NewContext())
        {
            Assert.Equal("seat-1", ctx.DevReports.Single(r => r.Factory == "factory-a").FactorySeat);
            Assert.Equal(1, ctx.DevReportItems.Count(i => i.ActionClaimedSessionId == "s-new" && i.ActedOnAtUtc == null));
        }

        // The reversal: back to AddAlertMarks removes every new column and keeps both rows.
        using (var ctx = NewContext())
        {
            ctx.GetService<IMigrator>().Migrate(MigrationBefore);
            Assert.Equal(MigrationBefore, ctx.Database.GetAppliedMigrations().Last());
        }
        Assert.Equal("", NewColumns());
        Assert.Equal("1", Scalar("""SELECT count(*) FROM gateway.dev_reports WHERE "SessionId" = 's-old'"""));
        Assert.Equal("1", Scalar("""SELECT count(*) FROM gateway.dev_report_items WHERE "ClientItemId" = 'item-1'"""));

        using (var ctx = NewContext())
            ctx.Database.Migrate();
        Assert.Equal(6, NewColumns().Split(',').Length);
    }
}
