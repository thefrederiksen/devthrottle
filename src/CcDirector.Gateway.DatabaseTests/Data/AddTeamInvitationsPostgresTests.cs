using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Xunit;

namespace CcDirector.Gateway.Tests.Data;

/// <summary>
/// The PostgreSQL half of the invitations table (devthrottle_internal#2301): <c>AddTeamInvitations</c> applies to a
/// real PostgreSQL database right after <c>AddTeams</c>, the unique index on the link secret's hash refuses a second
/// invitation with the same hash, deleting a team removes its invitations (the foreign key cascades), and the
/// migration's Down removes the table again - the reversal the pull request names.
///
/// GATING. [RequiresPostgresFact], the one rule for this project: it runs under scripts\test-database.ps1,
/// against the throwaway PostgreSQL that run built, and reports SKIPPED anywhere else. Skipped is not passed.
/// </summary>
public sealed class AddTeamInvitationsPostgresTests
{
    private const string ConnectionEnvVar = "CC_GATEWAY_TEST_PG_CONNECTION";
    private const string MigrationBefore = "20261003182436_AddTeams";
    private const string MigrationUnderTest = "20261003233544_AddTeamInvitations";

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
        SELECT count(*) FROM information_schema.tables
        WHERE table_schema = 'gateway' AND table_name = 'team_invitations'
        """)!;

    private static string Invite(string id, string hash) => $"""
        INSERT INTO gateway.team_invitations (id, team_id, email, role, state, invited_by_subject, created_at_utc,
            sent_at_utc, expires_at_utc, accept_token_hash)
        VALUES ('{id}', 'team-1', 'anna@example.com', 'developer', 'sent', 'sub-owner', TIMESTAMPTZ '2026-10-03 12:00:00Z',
            TIMESTAMPTZ '2026-10-03 12:00:00Z', TIMESTAMPTZ '2026-10-10 12:00:00Z', '{hash}');
        """;

    [RequiresPostgresFact]
    public void AddTeamInvitations_AppliesOnPostgres_RefusesADuplicateLinkHash_CascadesWithTheTeam_AndItsDownRemovesTheTable()
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
        Assert.Equal("1", TablePresent());

        var hashA = new string('a', 64);
        Scalar("INSERT INTO gateway.teams (id, name, created_at_utc) VALUES ('team-1', 'Acme', TIMESTAMPTZ '2026-10-03 12:00:00Z');");
        Scalar(Invite("inv-1", hashA));
        // The same address may hold a second invitation row (a cancelled one, then a new one); only the hash is unique.
        Scalar(Invite("inv-2", new string('b', 64)));

        var duplicate = Assert.Throws<PostgresException>(() => Scalar(Invite("inv-3", hashA)));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, duplicate.SqlState);
        Assert.Equal("2", Scalar("SELECT count(*) FROM gateway.team_invitations"));

        // A link secret's hash is 64 hexadecimal characters; the column refuses anything longer.
        var tooLong = Assert.Throws<PostgresException>(() => Scalar(Invite("inv-4", new string('c', 65))));
        Assert.Equal(PostgresErrorCodes.StringDataRightTruncation, tooLong.SqlState);

        // Deleting the team deletes its invitations.
        Scalar("DELETE FROM gateway.teams WHERE id = 'team-1';");
        Assert.Equal("0", Scalar("SELECT count(*) FROM gateway.team_invitations"));

        // The reversal: migrating back to AddTeams removes the table, and forward again restores it.
        using (var ctx = NewContext())
        {
            ctx.GetService<IMigrator>().Migrate(MigrationBefore);
            Assert.Equal(MigrationBefore, ctx.Database.GetAppliedMigrations().Last());
        }
        Assert.Equal("0", TablePresent());

        using (var ctx = NewContext())
            ctx.Database.Migrate();
        Assert.Equal("1", TablePresent());
    }
}
