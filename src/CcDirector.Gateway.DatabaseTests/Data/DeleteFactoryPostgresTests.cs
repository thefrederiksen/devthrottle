using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Tests.Factory.Registry;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CcDirector.Gateway.Tests.Data;

/// <summary>
/// DELETING AN ARCHIVED FACTORY, ON POSTGRESQL (issue #4324). The hosted Gateway runs on PostgreSQL, where the factory
/// columns use the C collation and a trigger's factory is matched lower-cased, so the delete is proven there by the SAME
/// scenario the unit tests run on SQLite (<see cref="FactoryDeleteScenario"/>).
///
/// It owns a database of its own on the rig (<see cref="PostgresProofDatabase.ConnectionFor"/>), built by the
/// Gateway's own start-up path and dropped afterwards, so it shares nothing with the proof classes.
/// </summary>
public sealed class DeleteFactoryPostgresTests
{
    [RequiresPostgresFact]
    public void Delete_RemovesWhatMakesTheFactory_AndKeepsItsHistory_OnPostgres()
    {
        var connection = PostgresProofDatabase.ConnectionFor("factorydelete");
        var previous = Environment.GetEnvironmentVariable(GatewayDatabase.PostgresConnectionEnvVar);
        Environment.SetEnvironmentVariable(GatewayDatabase.PostgresConnectionEnvVar, connection);
        try
        {
            DropDatabase(connection);
            using var db = new GatewayDatabase(new SingleTenantContext());
            FactoryDeleteScenario.DeleteRemovesWhatMakesTheFactory_AndKeepsItsHistory(db, TenantId.Local);
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
