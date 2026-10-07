using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Factory.Registry;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Xunit;

namespace CcDirector.Gateway.Tests.Data;

/// <summary>
/// The PostgreSQL half of archiving a factory (Factories screen mission, round 2). <c>ArchiveFactories</c> shipped with
/// a SQLite migration only, so the hosted Gateway's PostgreSQL database never got the three archive columns and every
/// factory registry query failed after the deploy. This proves the PostgreSQL migration: it applies right after
/// <c>AddTeamQuestionAnswers</c>, adds the three nullable columns without touching a factory registered before it, the
/// real <see cref="FactoryRegistryStore"/> then lists, archives and restores through <see cref="GatewayDatabase"/> on
/// PostgreSQL - the exact queries that failed in production - and its Down removes the columns again.
///
/// GATING. Like the other PostgreSQL proofs, gated on <c>CC_GATEWAY_TEST_PG_CONNECTION</c>, which the parked run sets to
/// a throwaway database it builds and destroys. It reports SKIPPED when unset. Skipped is not passed.
///
/// It sets the runtime selector <c>CC_GATEWAY_DB_CONNECTION</c> to drive GatewayDatabase down its real PostgreSQL path,
/// so it joins the collection that never runs alongside another reader of that variable.
/// </summary>
[Collection("GatewayDatabase provider env var")]
public sealed class ArchiveFactoriesPostgresTests
{
    private const string ConnectionEnvVar = "CC_GATEWAY_TEST_PG_CONNECTION";
    private const string RuntimeConnectionEnvVar = "CC_GATEWAY_DB_CONNECTION";
    private const string MigrationBefore = "20261006171258_AddTeamQuestionAnswers";
    private const string MigrationUnderTest = "20261007052952_ArchiveFactories";

    private sealed class RequiresPostgresFactAttribute : FactAttribute
    {
        public RequiresPostgresFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ConnectionEnvVar)))
                Skip = $"Set {ConnectionEnvVar} to a Postgres connection string to run the real-Postgres archive factories proof.";
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
        WHERE table_schema = 'gateway' AND table_name = 'factory_registry'
          AND column_name IN ('ArchivedAtUtc', 'ArchivedBy', 'ArchivedSchedulesJson')
        """)!;

    [RequiresPostgresFact]
    public void ArchiveFactories_AppliesOnPostgres_TheRegistryListsArchivesAndRestores_AndItsDownRemovesTheColumns()
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
        // A factory registered before the migration.
        Scalar("""
            INSERT INTO gateway.factory_registry (tenant_id, "Factory", "Title", "Folder", "Computer", "SeatsJson", "RegisteredBy", "RegisteredAtUtc")
            VALUES ('local', 'machine-care', 'Machine Care', 'D:\machine-care', 'SOREN_NORTH', '[]', 'the owner', TIMESTAMPTZ '2026-10-06 10:00:00Z');
            """);
        Assert.Equal("0", ColumnsPresent());

        using (var ctx = NewContext())
        {
            ctx.GetService<IMigrator>().Migrate(MigrationUnderTest);
            Assert.Equal(MigrationUnderTest, ctx.Database.GetAppliedMigrations().Last());
            Assert.False(ctx.Database.HasPendingModelChanges());
        }
        Assert.Equal("3", ColumnsPresent());
        Assert.Equal("null", Scalar("""SELECT coalesce("ArchivedBy", 'null') FROM gateway.factory_registry WHERE "Factory" = 'machine-care'"""));

        // The queries that failed in production, through the real store on the real PostgreSQL path.
        var priorRuntimeConn = Environment.GetEnvironmentVariable(RuntimeConnectionEnvVar);
        try
        {
            Environment.SetEnvironmentVariable(RuntimeConnectionEnvVar, Connection);
            using var db = new GatewayDatabase(new SingleTenantContext());
            var store = new FactoryRegistryStore(db);
            var now = new DateTime(2026, 10, 7, 6, 0, 0, DateTimeKind.Utc);

            var listed = Assert.Single(store.List(TenantId.Local));
            Assert.Equal(("machine-care", null), (listed.Factory, listed.ArchivedAtUtc));

            store.Register(TenantId.Local, new RegisterFactoryRequest
            {
                Factory = "warm-forward",
                Title = "WarmForward",
                Folder = @"D:\warm-forward",
                Computer = "SOREN_NORTH",
                Seats = { new FactorySeatManifest { Id = "ceo", Name = "CEO", Role = "CEO", BriefFile = "agents/ceo.yaml" } },
            }, "the owner", now);
            var archived = store.Archive(TenantId.Local, "machine-care", "the owner", new[] { "machine-care-daily" }, now);
            Assert.Equal(now, archived.ArchivedAtUtc);

            var after = store.List(TenantId.Local);
            Assert.Equal(new[] { "machine-care", "warm-forward" }, after.Select(f => f.Factory));
            var stored = store.Find(TenantId.Local, "machine-care")!;
            Assert.Equal("the owner", stored.ArchivedBy);
            Assert.Equal(new[] { "machine-care-daily" }, stored.ArchivedSchedules);

            var restored = store.Restore(TenantId.Local, "machine-care");
            Assert.Null(restored.ArchivedAtUtc);
            Assert.Null(store.Find(TenantId.Local, "machine-care")!.ArchivedBy);
        }
        finally
        {
            Environment.SetEnvironmentVariable(RuntimeConnectionEnvVar, priorRuntimeConn);
        }

        // The reversal: back to AddTeamQuestionAnswers removes the columns, and both factories survive.
        using (var ctx = NewContext())
        {
            ctx.GetService<IMigrator>().Migrate(MigrationBefore);
            Assert.Equal(MigrationBefore, ctx.Database.GetAppliedMigrations().Last());
        }
        Assert.Equal("0", ColumnsPresent());
        Assert.Equal("2", Scalar("SELECT count(*) FROM gateway.factory_registry"));

        using (var ctx = NewContext())
            ctx.Database.Migrate();
        Assert.Equal("3", ColumnsPresent());
    }
}
