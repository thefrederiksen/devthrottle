using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Xunit;

namespace CcDirector.Gateway.Tests.Data;

/// <summary>
/// The PostgreSQL half of the teams tables (devthrottle_internal#2300): <c>AddTeams</c> applies to a real PostgreSQL
/// database, the filtered unique index refuses a second Owner there exactly as it does on SQLite (its filter is
/// raw SQL, so it is the part most likely to differ between the two), a non-Owner member is accepted, and the
/// migration's Down removes both tables again - the reversal the pull request names.
///
/// GATING. Like the other PostgreSQL proofs, gated on <c>CC_GATEWAY_TEST_PG_CONNECTION</c>, which the parked run
/// sets to a throwaway database it builds and destroys. It reports SKIPPED when unset. Skipped is not passed.
/// </summary>
public sealed class AddTeamsPostgresTests
{
    private const string ConnectionEnvVar = "CC_GATEWAY_TEST_PG_CONNECTION";
    private const string MigrationBefore = "20260928124407_AddFactoryMemoryNotes";
    private const string MigrationUnderTest = "20261003182436_AddTeams";

    private sealed class RequiresPostgresFactAttribute : FactAttribute
    {
        public RequiresPostgresFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ConnectionEnvVar)))
                Skip = $"Set {ConnectionEnvVar} to a Postgres connection string to run the real-Postgres teams proof.";
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
        WHERE table_schema = 'gateway' AND table_name IN ('teams', 'team_members')
        """)!;

    [RequiresPostgresFact]
    public void AddTeams_AppliesOnPostgres_RefusesASecondOwner_AndItsDownRemovesBothTables()
    {
        PostgresProofDatabase.GuardThrowawayDatabase();
        using (var ctx = NewContext())
            ctx.Database.EnsureDeleted();

        using (var ctx = NewContext())
        {
            var all = ctx.Database.GetMigrations().ToList();
            Assert.Equal(MigrationBefore, all[all.IndexOf(MigrationUnderTest) - 1]);
            // Migrated TO this migration, not to the newest: later migrations (AddTeamInvitations, #2301) follow it.
            ctx.GetService<IMigrator>().Migrate(MigrationUnderTest);
            Assert.Equal(MigrationUnderTest, ctx.Database.GetAppliedMigrations().Last());
            Assert.False(ctx.Database.HasPendingModelChanges());
        }
        Assert.Equal("2", TablesPresent());

        Scalar("""
            INSERT INTO gateway.teams (id, name, created_at_utc) VALUES ('team-1', 'Acme', TIMESTAMPTZ '2026-10-03 12:00:00Z');
            INSERT INTO gateway.team_members (team_id, account_subject, role, joined_at_utc)
                VALUES ('team-1', 'sub-owner', 'owner', TIMESTAMPTZ '2026-10-03 12:00:00Z');
            INSERT INTO gateway.team_members (team_id, account_subject, role, joined_at_utc)
                VALUES ('team-1', 'sub-dev', 'developer', TIMESTAMPTZ '2026-10-03 12:01:00Z');
            """);

        var second = Assert.Throws<PostgresException>(() => Scalar("""
            INSERT INTO gateway.team_members (team_id, account_subject, role, joined_at_utc)
                VALUES ('team-1', 'sub-other', 'owner', TIMESTAMPTZ '2026-10-03 12:02:00Z');
            """));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, second.SqlState);
        Assert.Equal("1", Scalar("SELECT count(*) FROM gateway.team_members WHERE role = 'owner'"));
        Assert.Equal("2", Scalar("SELECT count(*) FROM gateway.team_members"));
        // The filter names the STORED word: a capitalised 'Owner' is not a stored role, and the index does not see it.
        // The Gateway never writes one (TeamRoles.ToStored), which is why the lower-case words are the contract.

        // The reversal: migrating back to the migration before removes both tables, and forward again restores them.
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
