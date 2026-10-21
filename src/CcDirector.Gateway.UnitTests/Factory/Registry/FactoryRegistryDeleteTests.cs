using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Tests.Data;
using Xunit;

namespace CcDirector.Gateway.Tests.Factory.Registry;

/// <summary>Deleting an archived factory on SQLite (issue #4324): <see cref="FactoryDeleteScenario"/>, the same
/// scenario the database tests run on PostgreSQL.</summary>
[Trait("Category", "FactoryRegistry")]
public sealed class FactoryRegistryDeleteTests : IDisposable
{
    private readonly GatewayDbTestHarness _h = new();

    public void Dispose() => _h.Dispose();

    [Fact]
    public void Delete_RemovesWhatMakesTheFactory_AndKeepsItsHistory()
        => FactoryDeleteScenario.DeleteRemovesWhatMakesTheFactory_AndKeepsItsHistory(_h.Open(), TenantId.Local);
}
