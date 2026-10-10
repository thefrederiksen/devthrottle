using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Data;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CcDirector.Gateway.Tests.Data;

/// <summary>
/// THE HOSTED STARTUP PATH, ON A REAL POSTGRESQL. Constructing <see cref="GatewayDatabase"/> is exactly what the
/// hosted host does at boot: it reads <c>CC_GATEWAY_DB_CONNECTION</c>, selects PostgreSQL, opens through its
/// bounded retry and bounded pool, and runs <c>Database.Migrate()</c> with the migration set it resolves BY
/// ASSEMBLY NAME. <see cref="PostgresProviderProofTests"/> proves the migration set on a context it builds
/// itself; this is the one test that proves it through the class the Gateway really boots.
///
/// IT NEVER RAN BEFORE THIS PROJECT EXISTED. It lived in CcDirector.Gateway.UnitTests, skipped unless
/// <c>CC_GATEWAY_DB_CONNECTION</c> was set - and nothing ever set it, so it reported SKIPPED in every one of the
/// 52 recorded runs. Its sibling, GatewayDatabaseLivePostgresProofTests, had the same gate and the same record
/// (59 of 59 skipped); it was deleted rather than moved, because each of its four checks is already made, on the
/// rig, by <see cref="PostgresProviderProofTests"/>. This one is now wired to the rig: it points the Gateway's own
/// variable at a database of this run's, for the length of the test only.
///
/// IT ASSERTS THE PROPERTY, NOT A NAME. It used to check that the first migration had been applied, which stays
/// true if every later one is missing. It now checks that what the database says was applied is EVERY migration
/// the set contains, in order - so a migration that cannot be resolved or applied fails here, and adding one
/// needs no edit to this file.
///
/// Setting a process-wide variable is safe here and nowhere else, because this assembly runs its tests one at a
/// time (TestParallelization.cs) and the value is restored in a finally.
/// </summary>
public sealed class GatewayHostBootPostgresTests
{
    [RequiresPostgresFact]
    public void HostStartupPath_AppliesEveryPostgresMigration_OnTheRig()
    {
        var connection = PostgresProofDatabase.ConnectionFor("boot");
        var previous = Environment.GetEnvironmentVariable(GatewayDatabase.PostgresConnectionEnvVar);
        Environment.SetEnvironmentVariable(GatewayDatabase.PostgresConnectionEnvVar, connection);
        try
        {
            DropDatabase(connection);

            using var db = new GatewayDatabase(new SingleTenantContext());
            using var ctx = db.CreateContext();

            var known = ctx.Database.GetMigrations().ToList();
            var applied = ctx.Database.GetAppliedMigrations().ToList();

            // The set really resolved: an empty set would make the equality below vacuous.
            Assert.NotEmpty(known);
            Assert.Equal(known, applied);
            Assert.Empty(ctx.Database.GetPendingMigrations());
        }
        finally
        {
            Environment.SetEnvironmentVariable(GatewayDatabase.PostgresConnectionEnvVar, previous);
            DropDatabase(connection);
        }
    }

    /// <summary>Drop this test's own database. Its name carries the throwaway prefix by construction; the
    /// guard is repeated here because this is the line that destroys something.</summary>
    private static void DropDatabase(string connection)
    {
        var options = new DbContextOptionsBuilder<GatewayDbContext>().UseNpgsql(connection).Options;
        using var ctx = new GatewayDbContext(options) { ActiveTenant = TenantId.Local.Value };
        var name = ctx.Database.GetDbConnection().Database;
        if (!name.StartsWith("ccpg", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Refusing to drop '{name}': it is not a throwaway rig database.");
        ctx.Database.EnsureDeleted();
    }
}
