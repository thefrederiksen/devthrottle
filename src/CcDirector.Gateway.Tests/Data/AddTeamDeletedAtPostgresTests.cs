using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Xunit;

namespace CcDirector.Gateway.Tests.Data;

/// <summary>
/// The PostgreSQL half of the deleted-team migration (Teams v1, rename, delete and leave): <c>AddTeamDeletedAt</c>
/// applies to a real PostgreSQL database right after <c>AddFactoryPurpose</c>, a team that was already there stays a live
/// team, a deleted team keeps its row, and the migration's Down removes the two columns again without losing a team -
/// the reversal the pull request names.
///
/// GATING. Like the other PostgreSQL proofs, gated on <c>CC_GATEWAY_TEST_PG_CONNECTION</c>, which the parked run
/// sets to a throwaway database it builds and destroys. It reports SKIPPED when unset. Skipped is not passed.
/// </summary>
public sealed class AddTeamDeletedAtPostgresTests
{
    private const string ConnectionEnvVar = "CC_GATEWAY_TEST_PG_CONNECTION";
    private const string MigrationBefore = "20261008200010_AddFactoryPurpose";
    private const string MigrationUnderTest = "20261008212033_AddTeamDeletedAt";

    private sealed class RequiresPostgresFactAttribute : FactAttribute
    {
        public RequiresPostgresFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ConnectionEnvVar)))
                Skip = $"Set {ConnectionEnvVar} to a Postgres connection string to run the real-Postgres deleted-team proof.";
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

    private static string NewColumnsPresent() => Scalar("""
        SELECT count(*) FROM information_schema.columns
        WHERE table_schema = 'gateway' AND table_name = 'teams'
          AND column_name IN ('deleted_at_utc', 'deleted_by_account_subject')
        """)!;

    [RequiresPostgresFact]
    public void AddTeamDeletedAt_AppliesOnPostgres_KeepsEveryTeam_AndItsDownRemovesBothColumns()
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
        Assert.Equal("0", NewColumnsPresent());
        Scalar("INSERT INTO gateway.teams (id, name, created_at_utc) VALUES ('team-1', 'Acme', TIMESTAMPTZ '2026-10-01 09:00:00Z');");
        Scalar("INSERT INTO gateway.teams (id, name, created_at_utc) VALUES ('team-2', 'Gone', TIMESTAMPTZ '2026-10-01 09:00:00Z');");

        using (var ctx = NewContext())
        {
            ctx.GetService<IMigrator>().Migrate(MigrationUnderTest);
            Assert.Equal(MigrationUnderTest, ctx.Database.GetAppliedMigrations().Last());
        }
        Assert.Equal("2", NewColumnsPresent());
        // Both teams that existed before the migration are live teams after it.
        Assert.Equal("2", Scalar("SELECT count(*) FROM gateway.teams WHERE deleted_at_utc IS NULL AND deleted_by_account_subject IS NULL"));

        // A delete marks the row; it does not remove it.
        Scalar("UPDATE gateway.teams SET deleted_at_utc = TIMESTAMPTZ '2026-10-08 10:00:00Z', deleted_by_account_subject = 'sub-owner' WHERE id = 'team-2';");
        Assert.Equal("sub-owner", Scalar("SELECT deleted_by_account_subject FROM gateway.teams WHERE id = 'team-2'"));

        // The reversal: migrating back to AddFactoryPurpose removes both columns and keeps every team row.
        using (var ctx = NewContext())
        {
            ctx.GetService<IMigrator>().Migrate(MigrationBefore);
            Assert.Equal(MigrationBefore, ctx.Database.GetAppliedMigrations().Last());
        }
        Assert.Equal("0", NewColumnsPresent());
        Assert.Equal("2", Scalar("SELECT count(*) FROM gateway.teams"));

        using (var ctx = NewContext())
        {
            ctx.Database.Migrate();
            Assert.False(ctx.Database.HasPendingModelChanges());
        }
        Assert.Equal("2", NewColumnsPresent());
    }
}
