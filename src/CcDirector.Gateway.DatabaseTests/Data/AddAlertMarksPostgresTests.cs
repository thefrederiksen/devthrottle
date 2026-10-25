using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Alerts;
using CcDirector.Gateway.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Xunit;

namespace CcDirector.Gateway.Tests.Data;

/// <summary>
/// The PostgreSQL half of <c>AddAlertMarks</c> (the Alerts mission): the table of what the owner has seen and cleared.
/// It proves the migration applies right after <c>AddScheduleFailureNotices</c> without touching a row stored before
/// it, that the real <see cref="AlertMarkStore"/> then marks seen, clears, keeps two accounts' marks of THE SAME alert id
/// apart on the composite key, and sweeps through <see cref="GatewayDatabase"/> on PostgreSQL - including the timestamps
/// into <c>timestamp with time zone</c> columns and the lookup on the byte-collated alert id - and that its Down removes
/// the table again.
///
/// GATING. [RequiresPostgresFact], the one rule for this project: it runs under scripts\test-database.ps1, against the
/// throwaway PostgreSQL that run built, and reports SKIPPED anywhere else. Skipped is not passed.
///
/// It sets the runtime selector <c>CC_GATEWAY_DB_CONNECTION</c> to drive GatewayDatabase down its real PostgreSQL path,
/// so it joins the collection that never runs alongside another reader of that variable.
/// </summary>
[Collection("GatewayDatabase provider env var")]
public sealed class AddAlertMarksPostgresTests
{
    private const string RuntimeConnectionEnvVar = "CC_GATEWAY_DB_CONNECTION";
    private const string MigrationBefore = "20261024235609_AddScheduleFailureNotices";
    private const string MigrationUnderTest = "20261025095255_AddAlertMarks";

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

    private static string TablePresent() => Scalar("""
        SELECT count(*) FROM information_schema.tables WHERE table_schema = 'gateway' AND table_name = 'alert_marks'
        """)!;

    [RequiresPostgresFact]
    public void AddAlertMarks_AppliesOnPostgres_TheStoreMarksClearsAndSweepsPerAccount_AndItsDownRemovesIt()
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
        // A schedule failure notice stored before the migration.
        Scalar("""
            INSERT INTO gateway.cron_failure_notices (tenant_id, "JobId", "Cause", "LastSentUtc", "HeldBack",
                "LastCountedUtc", "LastCountedRunId")
            VALUES ('local', 'job-1', 'did-not-start', TIMESTAMPTZ '2026-10-24 11:00:00Z', 0,
                TIMESTAMPTZ '2026-10-24 11:00:00Z', 'run-1');
            """);
        Assert.Equal("0", TablePresent());

        using (var ctx = NewContext())
        {
            ctx.GetService<IMigrator>().Migrate(MigrationUnderTest);
            Assert.Equal(MigrationUnderTest, ctx.Database.GetAppliedMigrations().Last());
        }
        Assert.Equal("1", TablePresent());
        Assert.Equal("1", Scalar("""SELECT count(*) FROM gateway.cron_failure_notices"""));

        // The store reads today's model, so its queries run on the schema at the newest migration.
        using (var ctx = NewContext())
        {
            ctx.GetService<IMigrator>().Migrate();
            Assert.False(ctx.Database.HasPendingModelChanges());
        }

        var priorRuntimeConn = Environment.GetEnvironmentVariable(RuntimeConnectionEnvVar);
        try
        {
            Environment.SetEnvironmentVariable(RuntimeConnectionEnvVar, Connection);
            using var db = new GatewayDatabase(new SingleTenantContext());
            var marks = new AlertMarkStore(db);
            var alice = new TenantId("acct-alice");
            var bob = new TenantId("acct-bob");
            const string id = "restart-request:0123456789abcdef0123456789abcdef";
            var now = new DateTime(2026, 10, 25, 21, 0, 0, DateTimeKind.Utc);
            var version = now.AddMinutes(-3);

            // Seen and cleared in one account; the same alert id in another account is its own row.
            marks.MarkSeen(alice, new[] { (id, version) }, now);
            marks.MarkCleared(alice, new[] { id }, now);
            marks.MarkSeen(bob, new[] { (id, version) }, now);
            var a = marks.All(alice)[id];
            Assert.Equal((version, (DateTime?)now), (a.SeenVersionUtc!.Value, a.ClearedAtUtc));
            Assert.Equal(DateTimeKind.Utc, a.SeenVersionUtc!.Value.Kind);
            Assert.Null(marks.All(bob)[id].ClearedAtUtc);
            Assert.Equal("2", Scalar("""SELECT count(*) FROM gateway.alert_marks"""));
            Assert.Equal("2026-10-25 21:00:00", Scalar("""
                SELECT to_char("ClearedAtUtc" AT TIME ZONE 'UTC', 'YYYY-MM-DD HH24:MI:SS') FROM gateway.alert_marks
                WHERE tenant_id = 'acct-alice'
                """));

            // Writing the same alert again updates its one row rather than inserting a second.
            marks.MarkSeen(alice, new[] { (id, now) }, now.AddMinutes(1));
            Assert.Equal("2", Scalar("""SELECT count(*) FROM gateway.alert_marks"""));

            // Swept only when no source lists it AND it is old: listed keeps it, young keeps it, then it goes.
            var later = now.AddDays(2);
            Assert.Equal(0, marks.Sweep(alice, new HashSet<string> { id }, AlertWindow.Earlier, later));
            Assert.Equal(0, marks.Sweep(alice, new HashSet<string>(), AlertWindow.Earlier, now.AddMinutes(2)));
            Assert.Equal(1, marks.Sweep(alice, new HashSet<string>(), AlertWindow.Earlier, later));
            Assert.Empty(marks.All(alice));
            Assert.Single(marks.All(bob));
        }
        finally
        {
            Environment.SetEnvironmentVariable(RuntimeConnectionEnvVar, priorRuntimeConn);
        }

        // The reversal: back to AddScheduleFailureNotices removes the table, and the earlier row survives.
        using (var ctx = NewContext())
        {
            ctx.GetService<IMigrator>().Migrate(MigrationBefore);
            Assert.Equal(MigrationBefore, ctx.Database.GetAppliedMigrations().Last());
        }
        Assert.Equal("0", TablePresent());
        Assert.Equal("1", Scalar("""SELECT count(*) FROM gateway.cron_failure_notices"""));

        using (var ctx = NewContext())
            ctx.Database.Migrate();
        Assert.Equal("1", TablePresent());
    }
}
