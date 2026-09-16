using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Data;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CcDirector.Gateway.Tests.Data;

/// <summary>
/// Boot smoke proof for the hosted Gateway (Step 4b): the Postgres migration set must be findable and
/// applicable through the RUNTIME assembly resolution the hosted host uses, not only when a test project
/// references the migrations project directly. The hosted container publishes CcDirector.Gateway.Host, which
/// references CcDirector.Gateway.Migrations.Postgres so its DLL ships in the image; at boot the Gateway calls
/// Database.Migrate() with MigrationsAssembly "CcDirector.Gateway.Migrations.Postgres", which loads that
/// assembly BY NAME. These facts prove that resolution works.
///
/// The primary "the container carries the DLL" evidence is the publish-output check recorded in the QA doc
/// (the host's publish output contains CcDirector.Gateway.Migrations.Postgres.dll and lists it in the host
/// deps.json). These tests cover the other half: the migration set actually resolves by name (no database),
/// and, when a real Postgres is configured, the real GatewayDatabase startup path applies it.
/// </summary>
public sealed class GatewayHostBootSmokeTests
{
    private const string PostgresMigrationsAssembly = "CcDirector.Gateway.Migrations.Postgres";
    private const string InitialPostgresMigration = "20260718120027_InitialPostgres";

    // The Fleet Manager mission's tables (step 3), named literally so deleting either migration fails here.
    private const string FleetManagerOutcomesPostgresMigration = "20260917090009_AddFleetManagerOutcomes";
    private const string FleetManagerMarkHistoryPostgresMigration = "20260917090109_AddFleetManagerMarkHistory";
    private const string FleetManagerOutcomesSqliteMigration = "20260917090000_AddFleetManagerOutcomes";
    private const string FleetManagerMarkHistorySqliteMigration = "20260917090100_AddFleetManagerMarkHistory";

    /// <summary>A Fact that skips itself unless the runtime Postgres selector CC_GATEWAY_DB_CONNECTION is set
    /// to a non-blank value, so CI never reaches out to the hosted database and never needs the secret.</summary>
    private sealed class RequiresConfiguredPostgresFactAttribute : FactAttribute
    {
        public RequiresConfiguredPostgresFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(
                    Environment.GetEnvironmentVariable(GatewayDatabase.PostgresConnectionEnvVar)))
                Skip = $"Set {GatewayDatabase.PostgresConnectionEnvVar} to a real PostgreSQL connection " +
                       "string to run the live host-boot migration proof.";
        }
    }

    /// <summary>
    /// The Postgres migration set resolves BY ASSEMBLY NAME - the exact mechanism EF uses at runtime when the
    /// hosted host calls Migrate() with MigrationsAssembly "CcDirector.Gateway.Migrations.Postgres". This runs
    /// with NO database (a placeholder, never-connected connection string; GetMigrations reads the migrations
    /// assembly, it does not open a connection), so it always runs in CI and touches nothing. It proves the
    /// separately-assembled migration set is discoverable through the name the host wires - the gap Step 4b
    /// closes for the deployed image.
    /// </summary>
    [Fact]
    public void PostgresMigrationSet_ResolvesByAssemblyName_WithoutDatabase()
    {
        // A non-resolvable placeholder host (the reserved .invalid TLD never resolves) - GetMigrations does
        // not connect, and no connection must ever be attempted from this fact.
        using var ctx = new GatewayDbContext(
            new DbContextOptionsBuilder<GatewayDbContext>()
                .UseNpgsql("Host=pg.invalid;Database=none;Username=none;Password=none",
                    o => o.MigrationsAssembly(PostgresMigrationsAssembly))
                .Options);

        var migrations = ctx.Database.GetMigrations().ToList();

        Assert.Contains(InitialPostgresMigration, migrations);
        Assert.Contains(FleetManagerOutcomesPostgresMigration, migrations);
        Assert.Contains(FleetManagerMarkHistoryPostgresMigration, migrations);
    }

    /// <summary>
    /// EVERY SQLITE MIGRATION SINCE THE POSTGRES BASELINE HAS A POSTGRES TWIN OF THE SAME NAME, AND THE OTHER WAY
    /// ROUND. A table added for SQLite only boots on a self-hosted Gateway and fails on the hosted one the first
    /// time a row is written - and no SQLite test can see it. The two sets are read the way the host reads them
    /// (by assembly, with no database), compared by the name after the timestamp, and the SQLite migrations dated
    /// before the baseline are left out because the baseline folds them in.
    /// </summary>
    [Fact]
    public void EverySqliteMigrationSinceTheBaseline_HasAPostgresTwin_AndTheOtherWayRound()
    {
        using var postgres = new GatewayDbContext(
            new DbContextOptionsBuilder<GatewayDbContext>()
                .UseNpgsql("Host=pg.invalid;Database=none;Username=none;Password=none",
                    o => o.MigrationsAssembly(PostgresMigrationsAssembly))
                .Options);
        using var sqlite = new GatewayDbContext(
            new DbContextOptionsBuilder<GatewayDbContext>()
                .UseSqlite("Data Source=:memory:")
                .Options);

        var sqliteAll = sqlite.Database.GetMigrations().ToList();
        var baseline = InitialPostgresMigration[..InitialPostgresMigration.IndexOf('_')];
        static string Name(string migration) => migration[(migration.IndexOf('_') + 1)..];

        var sqliteSince = sqliteAll
            .Where(m => string.CompareOrdinal(m[..m.IndexOf('_')], baseline) > 0)
            .Select(Name).ToList();
        var postgresSince = postgres.Database.GetMigrations()
            .Where(m => m != InitialPostgresMigration)
            .Select(Name).ToList();

        // A PRESENCE, so an empty read cannot pass: the step 3 pair is in both lists.
        Assert.Contains(FleetManagerOutcomesSqliteMigration, sqliteAll);
        Assert.Contains(FleetManagerMarkHistorySqliteMigration, sqliteAll);
        Assert.Contains("AddFleetManagerMarkHistory", sqliteSince);

        Assert.Equal(sqliteSince.OrderBy(n => n, StringComparer.Ordinal), postgresSince.OrderBy(n => n, StringComparer.Ordinal));
    }

    /// <summary>
    /// The real hosted startup path: constructing GatewayDatabase (the exact class the hosted host boots)
    /// with CC_GATEWAY_DB_CONNECTION set runs Database.Migrate() against the configured Postgres, resolving
    /// and applying the Postgres migration set. Asserting the applied-migrations list contains the
    /// InitialPostgres migration proves the set was found by name and applied - end to end on a real server.
    /// Env-gated: skips cleanly when unset, so CI connects to nothing.
    /// </summary>
    [RequiresConfiguredPostgresFact]
    public void HostStartupPath_ResolvesAndAppliesPostgresMigrations_OnConfiguredPostgres()
    {
        using var db = new GatewayDatabase(new SingleTenantContext());
        using var ctx = db.CreateContext();

        var applied = ctx.Database.GetAppliedMigrations().ToList();

        Assert.Contains(InitialPostgresMigration, applied);
    }
}
