using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Factory.Registry;

namespace CcDirector.Gateway.Factory.Definition;

/// <summary>
/// What the spawn door needs to start a factory's Boss for the owner (issue #4351): the factory's id in the Gateway's
/// own spelling, its Boss seat as the registration names it (null when it names none), the published definition version
/// the Boss's kit is built from (null for a factory with no published definition), and whether it is archived.
/// </summary>
public sealed record FactoryBossStart(string Factory, string? BossSeat, int? PublishedVersion, bool Archived);

/// <summary>Reads, for one tenant, a factory's <see cref="FactoryBossStart"/>, or null when the account registers no
/// factory of that id. Bound to the tenant by the door that asks, so the settle that uses it never chooses an account.</summary>
public delegate FactoryBossStart? BossStartReader(string factory);

/// <summary>Builds the <see cref="BossStartReader"/> for one account from the registry and the definition store.</summary>
public static class FactoryBossStarts
{
    /// <summary>Read one factory's Boss start. Never throws for a factory the account does not register: that is null.</summary>
    public static FactoryBossStart? Read(FactoryRegistryStore registry, FactoryDefinitionStore definitions, TenantId tenant, string factory)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(definitions);
        FileLog.Write($"[FactoryBossStarts] Read: factory={factory}");

        if (registry.Find(tenant, factory) is not { } registered)
        {
            FileLog.Write($"[FactoryBossStarts] Read: {factory} is not registered");
            return null;
        }

        var boss = string.IsNullOrWhiteSpace(registered.BossSeat) ? null : registered.BossSeat.Trim();
        var published = definitions.Standing(tenant, registered.Factory).Published?.Version;
        var start = new FactoryBossStart(registered.Factory, boss, published, registered.ArchivedAtUtc is not null);
        FileLog.Write($"[FactoryBossStarts] Read: {start.Factory} boss={start.BossSeat ?? "(none)"}, published=v{start.PublishedVersion?.ToString() ?? "(none)"}, archived={start.Archived}");
        return start;
    }
}
