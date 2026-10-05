using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Xunit;

namespace CcDirector.Gateway.Tests.Data;

/// <summary>
/// The PostgreSQL half of the team requests tables (devthrottle_internal#2308): <c>AddTeamRequests</c> applies to a
/// real PostgreSQL database right after <c>AddFleetMessageLinks</c>, a trail step cannot name a request that does not
/// exist, deleting a request removes its trail (the foreign key cascades), and the migration's Down removes both
/// tables again - the reversal the pull request names.
///
/// GATING. Like the other PostgreSQL proofs, gated on <c>CC_GATEWAY_TEST_PG_CONNECTION</c>, which the parked run
/// sets to a throwaway database it builds and destroys. It reports SKIPPED when unset. Skipped is not passed.
/// </summary>
public sealed class AddTeamRequestsPostgresTests
{
    private const string ConnectionEnvVar = "CC_GATEWAY_TEST_PG_CONNECTION";
    private const string MigrationBefore = "20261005031815_AddFleetMessageLinks";
    private const string MigrationUnderTest = "20261005124444_AddTeamRequests";

    // The ids are Gateway-minted Guids, stored as uuid on PostgreSQL.
    private const string RequestOne = "11111111-1111-4111-8111-111111111111";
    private const string RequestTwo = "22222222-2222-4222-8222-222222222222";
    private const string StepOne = "33333333-3333-4333-8333-333333333333";
    private const string StepTwo = "44444444-4444-4444-8444-444444444444";
    private const string NoSuchRequest = "55555555-5555-4555-8555-555555555555";

    private sealed class RequiresPostgresFactAttribute : FactAttribute
    {
        public RequiresPostgresFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ConnectionEnvVar)))
                Skip = $"Set {ConnectionEnvVar} to a Postgres connection string to run the real-Postgres team requests proof.";
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

    private static string TablesPresent() => Scalar("""
        SELECT count(*) FROM information_schema.tables
        WHERE table_schema = 'gateway' AND table_name IN ('team_requests', 'team_request_changes')
        """)!;

    private static string Request(string id, string text) => $"""
        INSERT INTO gateway.team_requests (id, sender_subject, text, state, sent_at_utc, updated_at_utc, tenant_id)
        VALUES ('{id}', 'sub-sender', '{text}', 'sent', TIMESTAMPTZ '2026-10-04 12:00:00Z',
            TIMESTAMPTZ '2026-10-04 12:00:00Z', 'team-1');
        """;

    private static string Step(string id, string requestId) => $"""
        INSERT INTO gateway.team_request_changes (id, request_id, state, by_subject, at_utc, reason, tenant_id)
        VALUES ('{id}', '{requestId}', 'sent', 'sub-sender', TIMESTAMPTZ '2026-10-04 12:00:00Z', NULL, 'team-1');
        """;

    [RequiresPostgresFact]
    public void AddTeamRequests_AppliesOnPostgres_RefusesAnOrphanStep_CascadesWithTheRequest_AndItsDownRemovesBothTables()
    {
        PostgresProofDatabase.GuardThrowawayDatabase();
        using (var ctx = NewContext())
            ctx.Database.EnsureDeleted();

        using (var ctx = NewContext())
        {
            var all = ctx.Database.GetMigrations().ToList();
            Assert.Equal(MigrationBefore, all[all.IndexOf(MigrationUnderTest) - 1]);
            ctx.GetService<IMigrator>().Migrate(MigrationUnderTest);
            Assert.Equal(MigrationUnderTest, ctx.Database.GetAppliedMigrations().Last());
            Assert.False(ctx.Database.HasPendingModelChanges());
        }
        Assert.Equal("2", TablesPresent());

        Scalar(Request(RequestOne, "Please add a dark mode"));
        Scalar(Step(StepOne, RequestOne));

        // A trail step must belong to a request that exists.
        var orphan = Assert.Throws<PostgresException>(() => Scalar(Step(StepTwo, NoSuchRequest)));
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, orphan.SqlState);

        // A request's text is at most 4000 characters; the column refuses more.
        var tooLong = Assert.Throws<PostgresException>(() => Scalar(Request(RequestTwo, new string('x', 4001))));
        Assert.Equal(PostgresErrorCodes.StringDataRightTruncation, tooLong.SqlState);

        // Deleting the request deletes its trail.
        Scalar("DELETE FROM gateway.team_requests WHERE id = '" + RequestOne + "';");
        Assert.Equal("0", Scalar("SELECT count(*) FROM gateway.team_request_changes"));

        // The reversal: migrating back to AddFleetMessageLinks removes both tables, and forward again restores them.
        using (var ctx = NewContext())
        {
            ctx.GetService<IMigrator>().Migrate(MigrationBefore);
            Assert.Equal(MigrationBefore, ctx.Database.GetAppliedMigrations().Last());
        }
        Assert.Equal("0", TablesPresent());

        using (var ctx = NewContext())
            ctx.Database.Migrate();
        Assert.Equal("2", TablesPresent());
    }
}
