using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Data;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CcDirector.Gateway.Tests.Data;

/// <summary>
/// THE HOSTED GATEWAY'S OWN STARTUP PATH, RUN AGAINST A REAL POSTGRESQL SERVER.
///
/// Constructing <see cref="GatewayDatabase"/> - the exact class the hosted host boots - reads
/// <c>CC_GATEWAY_DB_CONNECTION</c> and runs <c>Database.Migrate()</c>, which has to FIND the PostgreSQL
/// migration set by assembly name and APPLY it. Asserting that the applied-migrations list contains the
/// baseline migration proves both halves end to end on a real server, which is the one thing a test
/// reading the migration set in memory can never prove.
///
/// WHERE IT CAME FROM, AND WHY IT MOVED. This was the single live-database test inside
/// GatewayHostBootSmokeTests in CcDirector.Gateway.UnitTests - a class whose other eleven tests open no
/// connection at all. That left one test in an assembly full of pure unit tests able to reach out to a
/// database, and it meant this proof reported SKIPPED on every continuous integration run there has ever
/// been. It now sits with the other proofs, where the PostgreSQL job starts a throwaway server, points
/// this test at it, and FAILS if the test is skipped. The assertion is unchanged; only its address is.
///
/// GATED ON THE RUNTIME SELECTOR, NOT ON THE RIG VARIABLES, and deliberately so: the whole point is that
/// the Gateway picks its own database up the way a deployment hands it one. The job gives it a database
/// of its own, separate from the one the other proofs delete and re-migrate, because this test applies
/// the real migration chain and must not be racing anyone dropping a schema underneath it.
/// </summary>
public sealed class HostStartupPathAppliesPostgresMigrationsTests
{
    private const string InitialPostgresMigration = "20260718120027_InitialPostgres";

    /// <summary>A Fact that skips itself unless the runtime PostgreSQL selector CC_GATEWAY_DB_CONNECTION is
    /// set to a non-blank value, so a developer who has pointed at nothing runs nothing and needs no
    /// secret. In the PostgreSQL job the variable IS set, and a skip there fails the job.</summary>
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

    [RequiresConfiguredPostgresFact]
    public void HostStartupPath_ResolvesAndAppliesPostgresMigrations_OnConfiguredPostgres()
    {
        using var db = new GatewayDatabase(new SingleTenantContext());
        using var ctx = db.CreateContext();

        var applied = ctx.Database.GetAppliedMigrations().ToList();

        Assert.Contains(InitialPostgresMigration, applied);
    }
}
