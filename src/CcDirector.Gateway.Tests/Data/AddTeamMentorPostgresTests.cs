using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Xunit;

namespace CcDirector.Gateway.Tests.Data;

/// <summary>
/// The PostgreSQL half of the Mentor's storage (devthrottle_internal#2305): <c>AddTeamMentor</c> applies to a real
/// PostgreSQL database right after <c>AddTeamInvitations</c>, keeps one block per (team, week, person), adds the nullable
/// person column to session history without touching an existing row, and its Down removes all three tables and the
/// column again - the reversal the pull request names.
///
/// GATING. Like the other PostgreSQL proofs, gated on <c>CC_GATEWAY_TEST_PG_CONNECTION</c>, which the parked run sets to
/// a throwaway database it builds and destroys. It reports SKIPPED when unset. Skipped is not passed.
/// </summary>
public sealed class AddTeamMentorPostgresTests
{
    private const string ConnectionEnvVar = "CC_GATEWAY_TEST_PG_CONNECTION";
    private const string MigrationBefore = "20261003233544_AddTeamInvitations";
    private const string MigrationUnderTest = "20261004214416_AddTeamMentor";

    private sealed class RequiresPostgresFactAttribute : FactAttribute
    {
        public RequiresPostgresFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ConnectionEnvVar)))
                Skip = $"Set {ConnectionEnvVar} to a Postgres connection string to run the real-Postgres Mentor proof.";
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

    private static string MentorTablesPresent() => Scalar("""
        SELECT count(*) FROM information_schema.tables
        WHERE table_schema = 'gateway' AND table_name IN ('team_mentor_blocks', 'team_mentor_outcomes', 'team_mentor_runs')
        """)!;

    private static string PersonColumnPresent() => Scalar("""
        SELECT count(*) FROM information_schema.columns
        WHERE table_schema = 'gateway' AND table_name = 'session_history' AND column_name = 'PersonSubject'
        """)!;

    private static string Block(string person) => $"""
        INSERT INTO gateway.team_mentor_blocks (tenant_id, "Week", "PersonSubject", "Tone", "WorkedOn", "HowItWent",
            "WentBadlyAndWhy", "OneThingToTry", "QuotesJson", "WrittenAtUtc", "Model")
        VALUES ('team-1', '2026-W40', '{person}', 'good', 'Worked on it.', 'Fine.', NULL, 'Keep going.', '[]',
            TIMESTAMPTZ '2026-10-05 01:00:00Z', 'fake-model');
        """;

    [RequiresPostgresFact]
    public void AddTeamMentor_AppliesOnPostgres_KeepsOneBlockPerPersonAndWeek_LeavesOldRowsAlone_AndItsDownRemovesItAll()
    {
        PostgresProofDatabase.GuardThrowawayDatabase();
        using (var ctx = NewContext())
            ctx.Database.EnsureDeleted();

        // A session history row written before the migration.
        using (var ctx = NewContext())
        {
            var all = ctx.Database.GetMigrations().ToList();
            Assert.Equal(MigrationBefore, all[all.IndexOf(MigrationUnderTest) - 1]);
            ctx.GetService<IMigrator>().Migrate(MigrationBefore);
        }
        Scalar("""
            INSERT INTO gateway.session_history (tenant_id, "SessionId", "DirectorId", "StartedAtUtc", "LastSeenUtc",
                "SummaryAttempts", "SummaryIsPartial")
            VALUES ('tenant-1', 's-old', 'd-1', TIMESTAMPTZ '2026-09-01 10:00:00Z', TIMESTAMPTZ '2026-09-01 11:00:00Z', 0, false);
            """);
        Assert.Equal("0", MentorTablesPresent());
        Assert.Equal("0", PersonColumnPresent());

        using (var ctx = NewContext())
        {
            ctx.GetService<IMigrator>().Migrate(MigrationUnderTest);
            Assert.Equal(MigrationUnderTest, ctx.Database.GetAppliedMigrations().Last());
        }
        Assert.Equal("3", MentorTablesPresent());
        Assert.Equal("1", PersonColumnPresent());
        Assert.Equal("null", Scalar("""SELECT coalesce("PersonSubject", 'null') FROM gateway.session_history WHERE "SessionId" = 's-old'"""));

        Scalar(Block("sub-rob"));
        Scalar(Block("sub-dana"));
        var duplicate = Assert.Throws<PostgresException>(() => Scalar(Block("sub-rob")));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, duplicate.SqlState);

        // The reversal: back to AddTeamInvitations removes the three tables and the column, and the old row survives.
        using (var ctx = NewContext())
        {
            ctx.GetService<IMigrator>().Migrate(MigrationBefore);
            Assert.Equal(MigrationBefore, ctx.Database.GetAppliedMigrations().Last());
        }
        Assert.Equal("0", MentorTablesPresent());
        Assert.Equal("0", PersonColumnPresent());
        Assert.Equal("1", Scalar("""SELECT count(*) FROM gateway.session_history WHERE "SessionId" = 's-old'"""));

        using (var ctx = NewContext())
            ctx.Database.Migrate();
        Assert.Equal("3", MentorTablesPresent());
    }
}
