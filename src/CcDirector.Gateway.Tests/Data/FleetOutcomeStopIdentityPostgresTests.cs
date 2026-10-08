using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Xunit;

namespace CcDirector.Gateway.Tests.Data;

/// <summary>
/// The PostgreSQL half of the round 2 fixes' storage: a real PostgreSQL database at the schema before the change,
/// holding an open record filed then, carried through <c>AddFleetOutcomeStopIdentity</c>. The record survives, naming no
/// stop, and the two new columns exist.
///
/// GATING. Like <see cref="TurnVerdictAnswerChoicePostgresTests"/>, the class is gated on
/// <c>CC_GATEWAY_TEST_PG_CONNECTION</c> and reports SKIPPED when it is unset. Skipped is not passed.
/// </summary>
public sealed class FleetOutcomeStopIdentityPostgresTests
{
    private const string ConnectionEnvVar = "CC_GATEWAY_TEST_PG_CONNECTION";
    private const string MigrationBefore = "20260917110509_AddTurnVerdictAnswerChoice";
    private const string MigrationUnderTest = "20260917110609_AddFleetOutcomeStopIdentity";

    private sealed class RequiresPostgresFactAttribute : FactAttribute
    {
        public RequiresPostgresFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ConnectionEnvVar)))
                Skip = $"Set {ConnectionEnvVar} to a Postgres connection string to run the real-Postgres " +
                       "record stop identity proof.";
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

    [RequiresPostgresFact]
    public void AddFleetOutcomeStopIdentity_FromEmpty_AddsTheColumnsAndKeepsARecordFiledBefore()
    {
        PostgresProofDatabase.GuardThrowawayDatabase();
        using (var ctx = NewContext())
            ctx.Database.EnsureDeleted();

        using (var ctx = NewContext())
        {
            var all = ctx.Database.GetMigrations().ToList();
            var index = all.IndexOf(MigrationUnderTest);
            Assert.True(index > 0, $"'{MigrationUnderTest}' is not in the Postgres migration set.");
            Assert.Equal(MigrationBefore, all[index - 1]);
            // The Wingman narration call trace (pull request 3105) landed after this proof was written, so the
            // migration under test is second from the end rather than last.
            Assert.Equal("20260918181205_AddWingmanNarrationCallTrace", all[index + 1]);
            // The repository catalog's discovered columns and the raised sessions table landed after that, and the
            // pins below moved with them.
            Assert.Equal("20260920021806_AddDiscoveredRepositories", all[index + 2]);
            Assert.Equal("20260920053001_AddRaisedSessions", all[index + 3]);
            // The factory activity record landed after that, and the factory triggers after it.
            Assert.Equal("20260921084515_AddFactoryActivity", all[index + 4]);
            Assert.Equal("20260921105238_AddFactoryTriggers", all[index + 5]);
            // The factory activity record's read indexes (the Factory Agents pages) after that.
            Assert.Equal("20260921131114_IndexFactoryActivityReads", all[index + 6]);
            // The name a trigger's pending start used, stored with its lock (the trigger's live check) after that.
            Assert.Equal("20260921203258_AddTriggerStartName", all[index + 7]);
            // The fleet message's unreachable-notice mark (issue 3289) after that.
            Assert.Equal("20260927212206_AddFleetMessageUnreachableNotice", all[index + 8]);
            // The factory a session or schedule belongs to, and the factory memory notes (issue 3436), after that.
            Assert.Equal("20260928123825_AddSessionAndScheduleFactory", all[index + 9]);
            Assert.Equal("20260928124407_AddFactoryMemoryNotes", all[index + 10]);
            // The teams and their members (devthrottle_internal#2300), after that.
            Assert.Equal("20261003182436_AddTeams", all[index + 11]);
            // The team invitations (devthrottle_internal#2301), after that.
            Assert.Equal("20261003233544_AddTeamInvitations", all[index + 12]);
            // The Mentor's weekly page (devthrottle_internal#2305), after that.
            Assert.Equal("20261004214416_AddTeamMentor", all[index + 13]);
            // The message links between sessions (issue #3548), after that.
            Assert.Equal("20261005031815_AddFleetMessageLinks", all[index + 14]);
            // The team requests (devthrottle_internal#2308), after that.
            Assert.Equal("20261005124444_AddTeamRequests", all[index + 15]);
            // The requests for a message link (issue #3548), after that.
            Assert.Equal("20261005160425_AddFleetMessageLinkRequests", all[index + 16]);
            // The Fleet Manager's lessons (issue #3559), after that.
            Assert.Equal("20261005184813_AddFleetManagerLessons", all[index + 17]);
            // A dev report sent to a member of a team (devthrottle_internal#2309), after that.
            Assert.Equal("20261005234644_AddDevReportSharing", all[index + 18]);
            // The factory registry and goal numbers (Factories screen mission, phase A), after that.
            Assert.Equal("20261006140813_AddFactoryRegistry", all[index + 19]);
            // A team member's answer and the question a person's comment is about (devthrottle_internal#2307), after that.
            Assert.Equal("20261006171258_AddTeamQuestionAnswers", all[index + 20]);
            // Archiving a factory (Factories screen mission, round 2), after that.
            Assert.Equal("20261007052952_ArchiveFactories", all[index + 21]);
            // The team bill the Gateway owns, and its billing history (Teams v1, the team bill without Stripe), after that.
            Assert.Equal("20261007070501_AddTeamBills", all[index + 22]);
            // The amount a link request asks for (issue #3631), after that.
            Assert.Equal("20261007180154_AddLinkRequestRequestedAmount", all[index + 23]);
            // The seat a schedule runs (issue #3650), after that.
            Assert.Equal("20261008043117_AddScheduleSeat", all[index + 24]);
            Assert.Equal(all.Count - 25, index);
            ctx.GetService<IMigrator>().Migrate(MigrationBefore);
        }

        Scalar("""
            INSERT INTO gateway.fleet_outcomes ("Id", tenant_id, "Kind", "FiledBy", "AboutSessionId", "CreatedAtUtc",
                "Title", "DetailsJson", "Status")
            VALUES ('6a000000-0000-4000-8000-000000000001', 'tenant-one', 'decision', 'fm-1', 'worker-1',
                TIMESTAMPTZ '2026-09-17 06:00:00Z', 'Publish?', '{}', 'open');
            """);

        using (var ctx = NewContext())
        {
            ctx.GetService<IMigrator>().Migrate();
            // Later migrations follow the one under test, so migrating fully applies them too; the raised sessions
            // table was the last of them until the factory activity record and then the factory triggers followed it,
            // and then the name a trigger's pending start used, and then the factory memory notes (issue 3436), and then
            // the teams (devthrottle_internal#2300) and the team invitations (devthrottle_internal#2301).
            Assert.Equal("20261008043117_AddScheduleSeat", ctx.Database.GetAppliedMigrations().Last());
            Assert.False(ctx.Database.HasPendingModelChanges());
        }

        Assert.Equal("2", Scalar("""
            SELECT count(*) FROM information_schema.columns
            WHERE table_schema = 'gateway' AND table_name = 'fleet_outcomes'
              AND column_name IN ('AboutVerdictId', 'AboutTurnEndObservedAtUtc')
            """));
        Assert.Equal("Publish?|open|null|null", Scalar("""
            SELECT "Title" || '|' || "Status" || '|' || COALESCE("AboutVerdictId", 'null') || '|'
                || COALESCE("AboutTurnEndObservedAtUtc"::text, 'null') FROM gateway.fleet_outcomes
            """));
    }
}
