using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Tests.Factory.Definition;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CcDirector.Gateway.Tests.Data;

/// <summary>
/// A PUBLISH WHOSE SCHEDULE WRITE FAILS CHANGES NOTHING, ON POSTGRESQL (Factory Definition, phase 4). The hosted
/// Gateway runs on PostgreSQL, where a failed statement aborts the transaction it is in - so "the rollback leaves the
/// factory exactly as it was" is proven on that database as well as on SQLite, by the SAME scenario
/// (<see cref="FactoryPublishScenario.PublishWhoseScheduleWriteFails_ChangesNothing"/>): the registry, the schedules,
/// the triggers and the publish marks are read before and after and compared whole, and the answer is the 500
/// sentence naming the step and the row.
///
/// It owns a database of its own on the rig (<see cref="PostgresProofDatabase.ConnectionFor"/>), built by the
/// Gateway's own start-up path and dropped afterwards, so it shares nothing with the proof classes.
/// </summary>
public sealed class FactoryPublishPostgresTests
{
    [RequiresPostgresFact]
    public void Publish_WhoseScheduleWriteFails_ChangesNothing_OnPostgres()
    {
        var connection = PostgresProofDatabase.ConnectionFor("factorypublish");
        var previous = Environment.GetEnvironmentVariable(GatewayDatabase.PostgresConnectionEnvVar);
        Environment.SetEnvironmentVariable(GatewayDatabase.PostgresConnectionEnvVar, connection);
        try
        {
            DropDatabase(connection);
            using var db = new GatewayDatabase(new SingleTenantContext());
            var scenario = new FactoryPublishScenario(db, TenantId.Local);
            var failing = FactoryPublishScenario.Failing(b => b.UseNpgsql(connection));

            FactoryPublishScenario.PublishWhoseScheduleWriteFails_ChangesNothing(scenario, failing);
        }
        finally
        {
            Environment.SetEnvironmentVariable(GatewayDatabase.PostgresConnectionEnvVar, previous);
            DropDatabase(connection);
        }
    }

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
