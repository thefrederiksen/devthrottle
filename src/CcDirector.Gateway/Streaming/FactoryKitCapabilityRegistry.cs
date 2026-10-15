using CcDirector.Core.Tenancy;

namespace CcDirector.Gateway.Streaming;

/// <summary>
/// Which connected Directors build a seat's kit (Factory Definition mission, phase 5), from what each said about
/// itself on Hello (<see cref="Contracts.DirectorStreamHello.BuildsFactoryKit"/>).
///
/// A SEPARATE REGISTRY, beside <see cref="FleetManagerHomeCapabilityRegistry"/> rather than that one grown: the two
/// capabilities belong to different missions and are asked about by different doors, and a registry named for the
/// Fleet Manager answering a factory question would mislead the next reader. The terms are the same: a stated
/// capability and not a version number, because a development build carries whatever number its branch was cut at;
/// keyed by account AND Director, because a Director id is written by the client.
///
/// A seat start - a create carrying any of the six seat-start fields - goes only to a Director recorded here as
/// true. An older Director would drop the fields and start the seat as an ordinary session with none of its limits,
/// which is the one outcome worse than not starting it.
/// </summary>
public sealed class FactoryKitCapabilityRegistry
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<(TenantId Tenant, string DirectorId), bool> _builds = new();

    /// <summary>Record what a Director said about itself on Hello, under the account its connection is bound to.</summary>
    public void Record(TenantId tenant, string directorId, bool buildsFactoryKit)
    {
        if (string.IsNullOrEmpty(directorId)) return;
        _builds[(tenant, directorId)] = buildsFactoryKit;
    }

    /// <summary>Whether this account's Director said it builds a seat's kit. False for one that never said so - an older
    /// build, or one this Gateway has not heard from since it started.</summary>
    public bool BuildsFactoryKit(TenantId tenant, string? directorId)
        => !string.IsNullOrEmpty(directorId) && _builds.TryGetValue((tenant, directorId), out var builds) && builds;
}
